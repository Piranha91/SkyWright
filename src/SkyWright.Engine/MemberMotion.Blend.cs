using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// BLENDS (GATE-A §2a, §2b): transition blends between any two graph states (the region covers every pair: which states
// transition is not read, so this is a superset) and hkbBlenderGenerator states over their 2 or 3 children. A blend is an
// expression over leaves (a clip at its own time, a reference pose, the stored pose) and mixes (2 or 3 children, one weight
// vector for every node). Its parameters are each leaf's time and each mix's weights (two children: w in [0, 1]; three: the
// simplex as (1 - u, u (1 - v), u v) over [0, 1]^2). Two interpolation branches are sampled: every mix by nlerp, and every
// two-child mix by slerp (three or more children: the normalised weighted mean in both). See MemberMotion.cs for the bound.
// Lane N: a blender whose weights the file fixes (one child weighted 1, the rest 0, no binding, not parametric or smoothed, no
// reference-pose threshold: HavokBlender.FixedChild) is that child, in its own state and in every transition. Which dimension a
// cell splits is chosen on the cell itself (SplitDim); it changes where the budget goes, never a bound.

internal abstract class MixNode { }

/// <summary>A leaf: a source at its own time (dimension Dim of the cell), a one-pose source (Dim -1, at T0), or the stored pose
/// (Source null).</summary>
internal sealed class MixLeaf : MixNode
{
    public MotionSource? Source;
    public int Dim = -1;
    public float T0;
}

/// <summary>A mix of 2 or 3 children; WDim is its first weight dimension (two children: one, three: two).</summary>
internal sealed class MixMix : MixNode
{
    public required List<MixNode> Children;
    public int WDim;
    public int Id;
}

/// <summary>One blend family: the expression, its parameter dimensions (clock or weight, with the initial range), the
/// interpolation branch, its kind (blend: a transition between states; blender: a blender state alone) and the label a witness
/// carries (P1).</summary>
internal sealed class MixFamily
{
    public required MixNode Root;
    public required List<(bool Clock, double Lo, double Hi)> Dims;
    public required string Interp;
    public required string Kind;
    public required string Label;
    public required string Name;
    public string? Blender;
    public required List<MixLeaf> Leaves;
    public required HashSet<string> Nodes;
}

/// <summary>The blend at one cell as a motion source: Local gives each node's local at the cell's representative parameters (the
/// lower corner), Bound the certified change over the cell (translation, rotation angle, scale); an unbounded cell (the
/// quaternions' signs can change inside it) bounds by infinity.</summary>
internal sealed class MixSource : MotionSource
{
    readonly MixFamily fam; readonly double[] lo, hi;
    readonly IReadOnlyDictionary<(int Mix, string Node), int> branches;
    readonly Dictionary<string, (NodeXf Rep, float T, float A, float S)> cache = new(StringComparer.Ordinal);
    /// <summary>Two-child mixes (by mix and node) whose sign alignment can change inside the cell and that carry no branch yet.</summary>
    public readonly HashSet<(int Mix, string Node)> Unassigned = new();
    /// <summary>Per clock dimension, the largest rotation chord its leaf moves over the cell across the nodes evaluated (what a
    /// capped cell's split must shrink; lane N).</summary>
    public readonly Dictionary<int, double> ClockChord = new();
    /// <summary>True when some node took the any-rotation bound (a sign the cell cannot fix): only shorter clocks can lift it.</summary>
    public bool Capped { get; private set; }
    /// <summary>Per branched (mix, node): the sign the engine takes at the representative parameters (the lower corner).</summary>
    public readonly Dictionary<(int Mix, string Node), int> NaturalSign = new();
    /// <summary>Per branched (mix, node) whose sign the cell fixes (no ambiguity left): that sign.</summary>
    public readonly Dictionary<(int Mix, string Node), int> FixedSign = new();
    /// <summary>False when a branch the cell carries is never the engine's choice inside it (its sign is fixed the other way).</summary>
    public bool BranchesPossible => branches.All(kv => !FixedSign.TryGetValue(kv.Key, out int s) || s == kv.Value);
    public MixSource(MixFamily fam, double[] lo, double[] hi, IReadOnlyDictionary<(int, string), int>? branches = null)
    { this.fam = fam; this.lo = lo; this.hi = hi; this.branches = branches ?? new Dictionary<(int, string), int>(); }
    public override IReadOnlyCollection<string> Nodes => fam.Nodes;
    public override NodeXf Local(string node, NodeXf stored, float t) => Get(node, stored).Rep;
    public override (float T, float A, float S) Bound(string node, NodeXf stored, float t0, float t1) { var g = Get(node, stored); return (g.T, g.A, g.S); }
    public override IEnumerable<float> KeyTimes() => new[] { 0f };
    /// <summary>True when every branched (mix, node) takes, at the representative parameters, the sign its branch assumes.</summary>
    public bool RepRealized => branches.All(kv => !NaturalSign.TryGetValue(kv.Key, out int s) || s == 0 || s == kv.Value);
    /// <summary>A product this close to 0 is the engine's own float arithmetic's to sign (Havok's, not ours): both branches are taken.</summary>
    internal const double SignTolerance = 1e-6;

    (NodeXf Rep, float T, float A, float S) Get(string node, NodeXf stored)
    {
        if (cache.TryGetValue(node, out var v)) return v;
        var e = Eval(fam.Root, node, stored);
        var rep = new NodeXf(Matrix4x4.CreateFromQuaternion(e.Q), e.S, e.T);
        v = e.Bad ? (rep, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity)
                  : (rep, Up(e.DT), Up(Math.Min(Math.PI, e.Th)), Up(e.DS));
        cache[node] = v;
        return v;
    }

    static float Up(double x) { float f = (float)x; return f < x ? MathF.BitIncrement(f) : f; }

    readonly record struct NodeEval(Quaternion Q, Vector3 T, float S, double Th, double DT, double DS, bool Bad);

    NodeEval Eval(MixNode n, string node, NodeXf stored)
    {
        if (n is MixLeaf leaf)
        {
            if (leaf.Source is null || !leaf.Source.Nodes.Contains(node))
                return new(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(stored.R)), stored.T, stored.S, 0, 0, 0, false);
            float a = leaf.Dim < 0 ? leaf.T0 : (float)lo[leaf.Dim], b = leaf.Dim < 0 ? leaf.T0 : (float)hi[leaf.Dim];
            var x = leaf.Source.Local(node, stored, a);
            var bd = b > a ? leaf.Source.Bound(node, stored, a, b) : (0f, 0f, 0f);
            bool nonFinite = !float.IsFinite(bd.Item1) || !float.IsFinite(bd.Item2) || !float.IsFinite(bd.Item3);
            if (leaf.Dim >= 0)
            {
                double ch = float.IsFinite(bd.Item2) ? 2 * Math.Sin(Math.Min(bd.Item2, Math.PI) / 4) : 2;
                ClockChord[leaf.Dim] = Math.Max(ClockChord.GetValueOrDefault(leaf.Dim), ch);
            }
            return new(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(x.R)), x.T, x.S, bd.Item2, bd.Item1, Math.Abs(bd.Item3), nonFinite);
        }
        var mix = (MixMix)n;
        int k = mix.Children.Count;
        var c = mix.Children.Select(ch => Eval(ch, node, stored)).ToArray();
        // weights at the representative corner and the largest change of each over the cell
        double[] w0, dw;
        if (k == 2)
        {
            double wl = lo[mix.WDim], wh = hi[mix.WDim];
            w0 = new[] { 1 - wl, wl }; dw = new[] { wh - wl, wh - wl };
        }
        else
        {
            double ul = lo[mix.WDim], uh = hi[mix.WDim], vl = lo[mix.WDim + 1], vh = hi[mix.WDim + 1];
            static double[] W(double u, double v) => new[] { 1 - u, u * (1 - v), u * v };
            w0 = W(ul, vl); dw = new double[3];
            foreach (var (u, v) in new[] { (ul, vh), (uh, vl), (uh, vh) })
            {
                var wc = W(u, v);
                for (int i = 0; i < 3; i++) dw[i] = Math.Max(dw[i], Math.Abs(wc[i] - w0[i]));
            }
        }
        bool bad = c.Any(x => x.Bad);
        // sign alignment to the first child. Unambiguous: every pair keeps a positive product over the whole cell. A two-child mix
        // whose product can change sign inside the cell is split into the engine's two branches (the cell carries which one): in
        // branch s every realized point has s x product >= 0, so the sums stay at least sqrt((1 - e) / 2) long (e: the two
        // children's chords) and the geodesic stays within 90 degrees + asin(4.2 e)
        var q = new Quaternion[k]; var eps = new double[k];
        for (int i = 0; i < k; i++) eps[i] = 2 * Math.Sin(Math.Min(c[i].Th, Math.PI) / 4);
        q[0] = c[0].Q;
        double m = 1 / Math.Sqrt(k), fa = 1, fb = 1;
        bool cap = false;
        if (k == 2)
        {
            float dot = Quaternion.Dot(c[0].Q, c[1].Q);
            double e = eps[0] + eps[1];
            // natural 0: the product is within float noise of 0 at the representative, where either sign is the engine's choice
            int natural = Math.Abs(dot) < SignTolerance ? 0 : dot < 0 ? -1 : 1;
            if (Math.Abs(dot) > Math.Max(e, SignTolerance))
            {
                q[1] = natural < 0 ? Quaternion.Negate(c[1].Q) : c[1].Q;
                if (branches.ContainsKey((mix.Id, node))) FixedSign[(mix.Id, node)] = natural;
                // the product stays >= cmin over the cell: |sum|^2 >= 1 - 2 w (1 - w) (1 - cmin) at the cell's w nearest 0.5, and the
                // geodesic stays within acos(cmin), where slerp moves by at most sin(x W) / sin W of an endpoint's move (x: 1 - w
                // for the first, w for the second; increasing in x and in W)
                double cmin = Math.Max(0, Math.Abs(dot) - e), wl = lo[mix.WDim], wh = hi[mix.WDim], wm = Math.Clamp(0.5, wl, wh);
                m = Math.Sqrt(1 - 2 * wm * (1 - wm) * (1 - cmin));
                double big = Math.Acos(Math.Clamp(cmin, -1, 1));
                double F(double x) => big < 1e-9 ? x : Math.Min(1, Math.Sin(x * big) / Math.Sin(big));
                fa = F(1 - wl); fb = F(wh);
            }
            else if (branches.TryGetValue((mix.Id, node), out int s))
            {
                NaturalSign[(mix.Id, node)] = natural;
                q[1] = s < 0 ? Quaternion.Negate(c[1].Q) : c[1].Q;
                double eb = e + SignTolerance;   // a realized point of this branch has s x product >= -tolerance
                m = Math.Sqrt(Math.Max(0, (1 - eb) / 2));
                double beta = 4.2 * eb;
                if (m < 0.1 || beta >= 0.95) cap = true; else fa = fb = 1 / Math.Cos(Math.Asin(beta));
            }
            else { Unassigned.Add((mix.Id, node)); q[1] = c[1].Q; cap = true; }
        }
        else
        {
            for (int i = 1; i < k; i++) q[i] = Quaternion.Dot(c[i].Q, c[0].Q) < 0 ? Quaternion.Negate(c[i].Q) : c[i].Q;
            for (int i = 0; i < k; i++)
                for (int j = i + 1; j < k; j++)
                    if (Quaternion.Dot(q[i], q[j]) - eps[i] - eps[j] <= 0) cap = true;
        }
        var T = Vector3.Zero; double S = 0, dT = 0, dS = 0, dv = 0;
        for (int i = 0; i < k; i++)
        {
            T += (float)w0[i] * c[i].T; S += w0[i] * c[i].S;
            dT = Math.Max(dT, c[i].DT); dS = Math.Max(dS, c[i].DS); dv = Math.Max(dv, eps[i]);
        }
        for (int i = 1; i < k; i++)
        {
            dT += dw[i] * Vector3.Distance(c[i].T, c[0].T);
            dS += dw[i] * Math.Abs(c[i].S - c[0].S);
            dv += dw[i] * (q[i] - q[0]).Length();
        }
        Quaternion Q; double th;
        if (fam.Interp == "slerp" && k == 2)
        {
            Q = Quaternion.Normalize(Quaternion.Slerp(q[0], q[1], (float)w0[1]));
            double big = 2 * Math.Acos(Math.Clamp(Quaternion.Dot(q[0], q[1]), -1f, 1f));
            th = c[0].Th * fa + c[1].Th * fb + big * dw[1];
        }
        else
        {
            var v = new Quaternion(0, 0, 0, 0);
            for (int i = 0; i < k; i++) v += Quaternion.Multiply(q[i], (float)w0[i]);
            Q = v.Length() > 1e-6f ? Quaternion.Normalize(v) : q[0];
            double chord = dv / m;
            th = 4 * Math.Asin(Math.Min(1, chord / 2));
        }
        // a sign the cell cannot fix: any rotation (the angle between two rotations is at most pi); translation and scale stay linear
        if (cap) { th = Math.PI; Capped = true; }
        return new(Q, T, (float)S, Math.Min(Math.PI, th), dT, dS, bad);
    }
}

/// <summary>A box over a blend family's parameters with the sign branch of each two-child mix whose alignment can change inside it,
/// posed at its lower corner, or (when a branch is not the engine's choice there) at a corner where it is, with twice the bound.</summary>
internal sealed class MixCell : MemberCell
{
    readonly MixFamily fam; readonly double[] lo, hi;
    readonly IReadOnlyDictionary<(int, string), int> branches;
    readonly MixSource src;
    MixSource rep;
    readonly double[] repAt;
    readonly bool realized;
    const int MaxBranches = 4;

    MixCell(MemberCtx ctx, MixFamily fam, double[] lo, double[] hi, IReadOnlyDictionary<(int, string), int> branches)
    {
        this.fam = fam; this.lo = lo; this.hi = hi; this.branches = branches;
        var anim = new MotionAnimation("blend:" + fam.Name, "member-blend", fam.Name, fam.Interp, 0, 0, null, fam.Label);
        src = new MixSource(fam, lo, hi, branches) { Animation = anim };
        bool point = true;
        for (int i = 0; i < lo.Length; i++) if (hi[i] > lo[i]) point = false;
        Delta = SweptGeometry.DeltaBound(ctx.Reading.Rig, src, ctx.Pieces, 0, 1, ctx.Collision);
        if (point && src.Unassigned.Count == 0) Delta = 0;
        rep = src; repAt = lo; realized = src.RepRealized;
        if (!realized)
        {
            // a corner where every branch is the engine's own choice: the bound relative to it is twice the lower corner's
            var dims = Enumerable.Range(0, lo.Length).Where(i => hi[i] > lo[i]).ToList();
            for (int mask = 1; mask < (1 << Math.Min(dims.Count, 10)) && !realized; mask++)
            {
                var at = (double[])lo.Clone();
                for (int j = 0; j < dims.Count && j < 10; j++) if ((mask & (1 << j)) != 0) at[dims[j]] = hi[dims[j]];
                var p = new MixSource(fam, at, at, branches) { Animation = anim };
                SweptGeometry.DeltaBound(ctx.Reading.Rig, p, ctx.Pieces, 0, 1, ctx.Collision);   // evaluates every node's sign
                if (p.RepRealized) { rep = p; repAt = at; realized = true; Delta *= 2; }
            }
        }
    }

    /// <summary>The cell over [lo, hi] in every sign branch its two-child mixes need (each unassigned ambiguity doubles the cells,
    /// up to MaxBranches; beyond that a cell keeps the any-rotation bound).</summary>
    static IEnumerable<MixCell> Make(MemberCtx ctx, MixFamily fam, double[] lo, double[] hi, IReadOnlyDictionary<(int, string), int> branches)
    {
        var c = new MixCell(ctx, fam, lo, hi, branches);
        if (!c.src.BranchesPossible) yield break;   // that branch is not taken anywhere in this cell (its sibling covers it)
        if (c.src.Unassigned.Count == 0 || branches.Count >= MaxBranches) { yield return c; yield break; }
        var key = c.src.Unassigned.OrderBy(x => x.Mix).ThenBy(x => x.Node, StringComparer.Ordinal).First();
        foreach (int s in new[] { 1, -1 })
            foreach (var x in Make(ctx, fam, lo, hi, new Dictionary<(int, string), int>(branches) { [key] = s }))
                yield return x;
    }

    public override string Origin => MemberMotion.OriginBlend;
    public override bool RepRealized => realized;
    public override bool Splittable => Delta > 0 && Enumerable.Range(0, lo.Length).Any(i => hi[i] - lo[i] > 1e-6);
    public override MotionPoseSoup Pose(MemberCtx ctx) => NifMotion.PoseWith(ctx.Reading.Rig, rep, rep.Animation.Id, 0, ctx.Geometry);

    public static IEnumerable<MixCell> Initial(MemberCtx ctx, MixFamily f)
    {
        // each clock: its whole span and its end (the end is a pose of its own: a lower corner never reaches it)
        var clockDims = Enumerable.Range(0, f.Dims.Count).Where(i => f.Dims[i].Clock && f.Dims[i].Hi > f.Dims[i].Lo).ToList();
        int n = clockDims.Count;
        var none = new Dictionary<(int, string), int>();
        for (int mask = 0; mask < (1 << n); mask++)
        {
            var lo = f.Dims.Select(d => d.Lo).ToArray(); var hi = f.Dims.Select(d => d.Hi).ToArray();
            for (int j = 0; j < n; j++) if ((mask & (1 << j)) != 0) lo[clockDims[j]] = hi[clockDims[j]];
            foreach (var c in Make(ctx, f, lo, hi, none)) yield return c;
        }
    }

    public override IEnumerable<MemberCell> Split(MemberCtx ctx)
    {
        int best = SplitDim(ctx);
        double mid = (lo[best] + hi[best]) / 2;
        var hiA = (double[])hi.Clone(); hiA[best] = mid;
        var loB = (double[])lo.Clone(); loB[best] = mid;
        // a child keeps its parent's branches; the sign may be fixed there (then the branch is dropped by the child's own reading)
        return Make(ctx, fam, lo, hiA, branches).Concat(Make(ctx, fam, loB, hi, branches)).ToList();
    }

    /// <summary>Which dimension this cell splits (lane N; efficiency only, never a bound). A capped cell (a sign it cannot fix) is
    /// lifted only by its children's chords shrinking: it splits the clock whose leaf has the largest chord over the cell. Any other
    /// cell splits the dimension whose collapse to its lower value leaves the smallest bound (measured on this cell, with its
    /// branches). Ties: the widest relative to its span.</summary>
    int SplitDim(MemberCtx ctx)
    {
        var open = Enumerable.Range(0, lo.Length).Where(i => hi[i] - lo[i] > 1e-6).ToList();
        double Rel(int i) => (hi[i] - lo[i]) / Math.Max(1e-12, fam.Dims[i].Hi - fam.Dims[i].Lo);
        int Widest(IEnumerable<int> dims) => dims.OrderByDescending(Rel).ThenBy(i => i).First();
        var clocks = open.Where(i => fam.Dims[i].Clock).ToList();
        if (src.Capped && clocks.Count > 0)
        {
            double top = clocks.Max(i => src.ClockChord.GetValueOrDefault(i));
            return top > 0 ? Widest(clocks.Where(i => src.ClockChord.GetValueOrDefault(i) >= top)) : Widest(clocks);
        }
        var left = new Dictionary<int, double>();
        foreach (int i in open)
        {
            var hiC = (double[])hi.Clone(); hiC[i] = lo[i];
            double d = SweptGeometry.DeltaBound(ctx.Reading.Rig, new MixSource(fam, lo, hiC, branches) { Animation = src.Animation }, ctx.Pieces, 0, 1, ctx.Collision);
            left[i] = double.IsFinite(d) ? d : double.MaxValue;
        }
        double least = left.Values.Min();
        return Widest(open.Where(i => left[i] <= least * (1 + 1e-9) + 1e-12));
    }

    public override MemberDecision Witness(MemberCtx ctx, string? note, int evals)
    {
        var leaves = fam.Leaves;
        float T(MixLeaf l) => l.Dim < 0 ? l.T0 : (float)repAt[l.Dim];
        string Id(MixLeaf l) => l.Source?.Animation.Id ?? "stored";
        var weights = new List<double>();
        for (int i = 0; i < fam.Dims.Count; i++) if (!fam.Dims[i].Clock) weights.Add(repAt[i]);
        var extra = leaves.Count > 2 ? "; also " + string.Join(", ", leaves.Skip(2).Select(l => $"{Id(l)}@{T(l).ToString(System.Globalization.CultureInfo.InvariantCulture)}")) : "";
        return new("witness", null, MemberMotion.OriginBlend, Id(leaves[0]), T(leaves[0]), leaves.Count > 1 ? Id(leaves[1]) : null, leaves.Count > 1 ? T(leaves[1]) : null,
                   weights, null, $"{fam.Label}; {fam.Interp}{extra}", note, 0, evals, true);
    }

    public override string Describe() => $"blend {fam.Name} ({fam.Interp}) at [{string.Join(",", repAt.Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))}]";
    public override string SampleId => "blend:" + fam.Name + ":" + fam.Interp;
}

public static partial class MemberMotion
{
    /// <summary>Every blend family of a graph: each blender state alone, and each unordered pair of states (any two states can be a
    /// transition's source and target; which ones are is not read), each in the nlerp and the slerp branch. Null, or the refusal.</summary>
    static string? BuildFamilies(MemberReading r, MotionRig rig, HavokGraph g, double? seconds, string? note)
    {
        var states = g.States.Where(s => s.GeneratorKind != "state_machine").ToList();
        var blenders = (g.Blenders ?? Array.Empty<HavokBlender>()).ToDictionary(b => b.Name, StringComparer.Ordinal);
        string? why = null;
        MixNode? Leaf(string? kind, string? name)
        {
            string? id = kind switch { "clip" => $"clip:{name}", "sequence" => $"gseq:{name}", "reference_pose" => $"ref:{name}", _ => null };
            if (kind is "none") return new MixLeaf();
            if (id is null) { why = $"blend: a state or blender child of class {kind} ('{name}') is not read"; return null; }
            // a sequence generator naming a sequence the NIF lacks plays nothing when the NIF has no transform source (graph rule)
            return rig.Sources.TryGetValue(id, out var src) ? new MixLeaf { Source = src } : kind == "sequence" ? new MixLeaf() : Fail($"blend: '{name}' has no source");
        }
        MixNode? Fail(string w) { why = w; return null; }
        MixNode? StateExpr(HavokState s)
        {
            if (s.GeneratorKind != "hkbBlenderGenerator") return Leaf(s.GeneratorKind, s.GeneratorName);
            if (s.GeneratorName is null || !blenders.TryGetValue(s.GeneratorName, out var b)) return Fail($"blend: blender generator '{s.GeneratorName}' was not read");
            if (b.ChildKinds.Count > 3) return Fail($"blender generator '{b.Name}' has {b.ChildKinds.Count} children (four or more are not bounded)");
            if (b.ChildKinds.Count < 2) return b.ChildKinds.Count == 1 ? Leaf(b.ChildKinds[0], b.ChildNames[0]) : new MixLeaf();
            // lane N (option A): one-hot constant weights with no binding, not parametric, not smoothed, no reference-pose threshold:
            // the blender outputs that child alone, in the blender state and in every transition holding it
            if (b.FixedChild is int fc)
            {
                string w = string.Join(", ", b.Weights!.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
                string label = $"hkbBlenderGenerator '{b.Name}': fixed weights {w} read from the file (no binding, not parametric, not smoothed): child '{b.ChildNames[fc]}' alone";
                if (!r.FixedBlenders.Contains(label)) r.FixedBlenders.Add(label);
                return Leaf(b.ChildKinds[fc], b.ChildNames[fc]);
            }
            var kids = new List<MixNode>();
            for (int i = 0; i < b.ChildKinds.Count; i++) { var e = Leaf(b.ChildKinds[i], b.ChildNames[i]); if (e is null) return null; kids.Add(e); }
            return new MixMix { Children = kids };
        }
        void Add(MixNode root, string kind, string name, string label, string? blender)
        {
            foreach (var interp in new[] { "nlerp", "slerp" })
            {
                var copy = Clone(root);
                var dims = new List<(bool, double, double)>(); var leaves = new List<MixLeaf>(); var nodes = new HashSet<string>(StringComparer.Ordinal);
                bool twoWay = false; int mixes = 0;
                void Number(MixNode n)
                {
                    if (n is MixLeaf l)
                    {
                        leaves.Add(l);
                        if (l.Source is { } s)
                        {
                            foreach (var nd in s.Nodes) nodes.Add(nd);
                            if (s.Animation.End > s.Animation.Start) { l.Dim = dims.Count; dims.Add((true, s.Animation.Start, s.Animation.End)); }
                            else l.T0 = s.Animation.Start;
                        }
                        return;
                    }
                    var m = (MixMix)n;
                    foreach (var ch in m.Children) Number(ch);
                    m.WDim = dims.Count; m.Id = mixes++;
                    for (int i = 0; i < m.Children.Count - 1; i++) dims.Add((false, 0, 1));
                    if (m.Children.Count == 2) twoWay = true;
                }
                Number(copy);
                if (interp == "slerp" && !twoWay) continue;   // no two-child mix: the slerp branch is the nlerp branch
                if (nodes.Count == 0) continue;               // nothing the blend moves
                r.Blends.Add(new MixFamily { Root = copy, Dims = dims, Interp = interp, Kind = kind, Label = label, Name = name, Blender = blender, Leaves = leaves, Nodes = nodes });
            }
        }
        var exprs = new List<(HavokState S, MixNode E)>();
        foreach (var s in states)
        {
            var e = StateExpr(s);
            if (e is null) return why;
            exprs.Add((s, e));
            if (e is MixMix) Add(e, MemberMotion.KindBlender, $"{s.Name}", TransientLabel(null, $"blender weight runtime state: hkbBlenderGenerator '{s.GeneratorName}', weights not read"), s.GeneratorName);
        }
        string tl = TransientLabel(seconds, note);
        for (int i = 0; i < exprs.Count; i++)
            for (int j = i + 1; j < exprs.Count; j++)
                Add(new MixMix { Children = new List<MixNode> { exprs[i].E, exprs[j].E } }, MemberMotion.KindBlend, $"{exprs[i].S.Name}|{exprs[j].S.Name}", tl, null);
        foreach (var f in r.Blends)
            if (f.Dims.Count > 8) return $"blend '{f.Name}': {f.Dims.Count} parameters (more than 8 are not sampled)";
        return null;
    }

    static MixNode Clone(MixNode n) => n switch
    {
        MixLeaf l => new MixLeaf { Source = l.Source, T0 = l.T0 },
        MixMix m => new MixMix { Children = m.Children.Select(Clone).ToList() },
        _ => throw new InvalidOperationException(),
    };
}
