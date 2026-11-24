using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// SWEPT REGION OF A MOVING PART (animated-object swept volume, decision record D8). For a set of animations, the posed
// geometry is sampled at every key/knot time plus uniform subdivision until each interval's certified displacement bound
// delta_i (derived from the key interpolation itself, never from samples) is <= the target, or the sample budget runs out.
//   inner R- = union of the sampled poses (real moments of the animation)
//   outer R+ = union of (sampled pose grown by delta_i)
// Decisions against a partner: witness (a sampled pose crosses / overlaps it — exact predicates, labelled with animation
// and time), never (the partner lies outside R+ — certified), cannot_tell (else, with the width that remains). Volumes
// are certified octree intervals; a 2D moving part's inner volume is 0 by statement. A witness is never a resting clash.
//
// Displacement bound per interval [a, b], per point p of a shape, composed leaf to root along the node chain (root left
// out): D_j = (s_j + dS_j) D_{j+1} + dT_j + theta_j (s_j + dS_j) r_{j+1} + dS_j r_{j+1}, where (dT, theta, dS) bound the
// node's translation, rotation angle and scale change over the interval (NifMotionKeys ranges, HavokClip.Bound) and r is
// the largest radius of the shape's points in the node's child frame at a. Chord <= arc makes the rotation term sound.
// Skinned vertices take the largest bound of their bones' chains; every point of a triangle (and a rigid solid's interior)
// moves by at most its largest vertex bound.
//
// STATUS: API frozen at the Gate B red gate; implemented.

public sealed record MotionOptions(double DeltaTarget = 0.5, int SampleBudget = 4096, double RoundingAbsolute = 1e-4,
                                   double RoundingRelative = 1e-6);

/// <summary>One sample: the posed soup at Time and the certified bound Delta on how far any point of it moves before the
/// next sample of the same animation. Index and tag arrays may be shared across samples; clone them before mutation.
/// Origin (MemberMotion): <c>rest</c>, <c>blend</c>, <c>swing</c> or <c>reference</c>; null for a clip sample, so a clip-only
/// region writes exactly what it wrote before.</summary>
public sealed record SweptSample(string AnimationId, float Time, TriangleSoup Soup, string[]? Tags, double Delta,
                                 [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Origin = null);

/// <summary>Per-sample placed bounds and scaled deltas of a sweep, in Build's sample order (<see cref="SweptGeometry.PlacedSampleBounds"/>).</summary>
public sealed record SweptBounds(string Status, string? Reason, IReadOnlyList<(Aabb Bounds, double Delta)> Samples);

/// <summary>Status <c>built</c>, <c>refused</c> or <c>unknown</c>. Converged is false when the sample budget ran out
/// before every Delta met the target; MaxDelta is then the width a decision inherits.</summary>
public sealed record SweptRegion(string Status, string? Reason, VolumeGeometry Geometry, IReadOnlyList<MotionAnimation> Animations,
                                 IReadOnlyList<SweptSample> Samples, double MaxDelta, bool Converged, bool Solid);

/// <summary>Decision <c>witness</c>, <c>never</c>, <c>cannot_tell</c> or <c>refused</c>. A witness names the animation, the
/// time and the label (animation, cycle, base record type, trigger); Width is the delta a cannot_tell leaves.</summary>
public sealed record SweptDecision(string Decision, string? Reason, string? AnimationId, float? Time, string? Label,
                                   double Width, bool Converged, double? WitnessCrossingLower, double? WitnessVolumeLower,
                                   IReadOnlyDictionary<string, double>? WitnessCrossingByTags = null);

/// <summary>Optional instrumentation and threading for <see cref="SweptGeometry.Volume"/> (animated motion follow-ups,
/// item 2, E2). Threads: the classification threads (0 or less = the processor count; 1 = serial). After the call: Cells
/// placed, NearTests made (one per cell), TriangleTests made by the near tests. The answer does not depend on Threads.</summary>
public sealed class SweptVolumeRun
{
    public int Threads { get; init; }
    public long Cells { get; internal set; }
    public long NearTests { get; internal set; }
    public long TriangleTests { get; internal set; }
    public int ThreadsUsed { get; internal set; }
}

/// <summary>[inner, outer] volume of the swept region and, with a partner solid, of its intersection with the partner.</summary>
public sealed record SweptVolume(string Status, string? Reason, VolumeInterval? Swept, VolumeInterval? WithPartner, bool Converged);

public static partial class SweptGeometry
{
    public const string PolicyVersion = "swept_sampled_certified_v1";

    // One first-sample template per Build. Array consumers must clone before mutation.
    internal sealed class SampleTopology
    {
        bool initialized;
        int[]? indices;
        string[]? tags;

        internal (TriangleSoup Soup, string[]? Tags) Share(TriangleSoup soup, string[]? sampleTags)
        {
            if (!initialized)
            {
                indices = soup.Indices;
                tags = sampleTags;
                initialized = true;
                return (soup, sampleTags);
            }
            if (indices is not null && soup.Indices is { } current
                && !ReferenceEquals(current, indices) && current.AsSpan().SequenceEqual(indices))
                soup = soup with { Indices = indices };
            if (tags is not null && sampleTags is not null
                && !ReferenceEquals(sampleTags, tags) && sampleTags.AsSpan().SequenceEqual(tags))
                sampleTags = tags;
            return (soup, sampleTags);
        }
    }

    // ---------------- displacement bound ----------------
    internal sealed record ChainPiece(List<RigNodeRef> Nodes, List<Vector3> Points);   // nodes leaf-first (root excluded), points in the leaf frame
    internal readonly record struct RigNodeRef(NiflySharp.Blocks.NiAVObject Node, string? Name);

    static List<RigNodeRef> ChainBelowRoot(MotionRig rig, NiflySharp.Blocks.NiAVObject n)
    {
        var c = rig.Chain(n);
        return c.Take(c.Count - 1).Select(x => new RigNodeRef(x, x.Name?.String)).ToList();
    }

    /// <summary>The chains whose motion moves the geometry, each with the points it carries (in the chain's leaf frame).</summary>
    internal static List<ChainPiece> Pieces(MotionRig rig, VolumeGeometry g)
    {
        var pieces = new List<ChainPiece>();
        if (g == VolumeGeometry.Collision)
        {
            rig.Collision ??= HavokCollision.Decode(rig.Nif, includeTriangles: true);
            float unit = HavokCollision.UnitScaleFor(rig.Collision.StreamVersion);
            foreach (var b in rig.Collision.Bodies.Where(b => b.Solid && b.Node is not null))
                pieces.Add(new ChainPiece(ChainBelowRoot(rig, b.Node!), b.Vertices.Select(v => v * unit).ToList()));
            return pieces;
        }
        foreach (var rs in rig.Shapes)
        {
            if (rs.Hidden || rs.Particle) continue;
            if (rs.Weights is not null)
            {
                for (int bi = 0; bi < rs.Bones!.Count; bi++)
                {
                    var pts = new List<Vector3>();
                    for (int v = 0; v < rs.Vertices.Count; v++)
                        if (rs.Weights[v].Any(x => x.Bone == bi) || (rs.WeightsAlt is not null && rs.WeightsAlt[v].Any(x => x.Bone == bi)))
                            pts.Add(rs.Skin![bi].Apply(rs.Vertices[v]));   // the skin rule's second source moves vertices with its bones too
                    if (pts.Count > 0) pieces.Add(new ChainPiece(ChainBelowRoot(rig, rs.Bones[bi]), pts));
                }
            }
            else pieces.Add(new ChainPiece(ChainBelowRoot(rig, (NiflySharp.Blocks.NiAVObject)rs.Shape), rs.Vertices));
        }
        return pieces;
    }

    internal static double DeltaBound(MotionRig rig, MotionSource src, List<ChainPiece> pieces, float a, float b, bool collision)
    {
        double worst = 0, mag = 0;
        foreach (var piece in pieces)
        {
            // positions of the piece's points in each node's frame at time a, walking leaf to root
            var pts = piece.Points;
            double r = pts.Count == 0 ? 0 : pts.Max(p => (double)p.Length());
            double D = 0;
            var cur = pts;
            foreach (var nr in piece.Nodes)
            {
                var stored = rig.Stored(nr.Node, collision);
                bool driven = nr.Name is not null && src.Nodes.Contains(nr.Name);
                var local = driven ? src.Local(nr.Name!, stored, a) : stored;
                double s = Math.Abs(local.S);
                if (driven)
                {
                    var (dT, th, dS) = src.Bound(nr.Name!, stored, a, b);
                    if (float.IsInfinity(dT) || float.IsInfinity(dS)) return double.PositiveInfinity;
                    double sMax = s + dS;
                    D = sMax * D + dT + th * sMax * r + dS * r;
                }
                else D = s * D;
                cur = cur.Select(local.Apply).ToList();
                r = cur.Count == 0 ? 0 : cur.Max(p => (double)p.Length());
            }
            worst = Math.Max(worst, D);
            mag = Math.Max(mag, r);
        }
        return worst + 1e-4 + 1e-6 * mag;
    }

    /// <summary>Build the swept region of a mesh over the given animations (all of the model's when null).</summary>
    public static SweptRegion Build(byte[] nif, MotionModel model, IEnumerable<string>? animationIds, VolumeGeometry geometry,
                                    MotionOptions? options = null, Func<string, byte[]?>? read = null,
                                    CancellationToken cancellationToken = default)
    {
        options ??= new MotionOptions();
        SweptRegion Refuse(string why) => new("refused", why, geometry, model.Animations, Array.Empty<SweptSample>(), 0, false, false);
        var samples = new List<SweptSample>();
        var topology = new SampleTopology();
        var why = Sample(nif, model, animationIds, geometry, options, read, cancellationToken, (id, t, soup, tg, d) =>
        {
            var canonical = topology.Share(soup, tg);
            samples.Add(new SweptSample(id, t, canonical.Soup, canonical.Tags, d));
        }, out var anims, out bool converged);
        if (why is not null) return Refuse(why);
        if (samples.Count == 0) return Refuse("no animation to sweep");
        bool solid = MeshIntersection.PrepareVolumeSolid(samples[0].Soup, 50_000_000, cancellationToken).Solid is not null;
        if (geometry == VolumeGeometry.Render && solid && samples[0].Tags is { } tags && tags.Any(x => x != SurfaceMaterial.Opaque)) solid = false;
        return new SweptRegion("built", null, geometry, anims, samples, samples.Max(s => s.Delta), converged, solid);
    }

    /// <summary>Each sample of <see cref="Build"/>, placed as <see cref="Place"/> places it, reduced to its bounds and its scaled
    /// delta as it is posed; no posed soup is kept, so memory does not grow with the sample count. Status <c>built</c> or
    /// <c>refused</c> with Build's reason.</summary>
    public static SweptBounds PlacedSampleBounds(byte[] nif, MotionModel model, IEnumerable<string>? animationIds, VolumeGeometry geometry,
                                                 Vec3 position, Vec3 rotation, float scale, MotionOptions? options = null,
                                                 Func<string, byte[]?>? read = null, CancellationToken cancellationToken = default)
    {
        options ??= new MotionOptions();
        var bounds = new List<(Aabb Bounds, double Delta)>();
        var why = Sample(nif, model, animationIds, geometry, options, read, cancellationToken, (_, _, soup, _, d) =>
            bounds.Add((SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(soup, position, rotation, scale)), d * Math.Abs(scale))),
            out _, out _);
        if (why is not null) return new("refused", why, Array.Empty<(Aabb, double)>());
        if (bounds.Count == 0) return new("refused", "no animation to sweep", Array.Empty<(Aabb, double)>());
        return new("built", null, bounds);
    }

    /// <summary>The sampling loop of <see cref="Build"/>, <see cref="PlacedSampleBounds"/> and MemberMotion's clip-only regions: per animation, intervals refined by
    /// largest bound first until every bound meets the target or the budget is spent, then each sample posed and handed to
    /// <paramref name="sink"/> (animation id, time, posed soup, tags, delta). Returns the refusal, or null.</summary>
    internal static string? Sample(byte[] nif, MotionModel model, IEnumerable<string>? animationIds, VolumeGeometry geometry, MotionOptions options,
                          Func<string, byte[]?>? read, CancellationToken cancellationToken,
                          Action<string, float, TriangleSoup, string[]?, double> sink,
                          out List<MotionAnimation> anims, out bool converged)
    {
        anims = new List<MotionAnimation>();
        converged = true;
        if (model.Status == "refused") return model.Reason ?? "refused";
        var rig = NifMotion.RigOf(nif, model, read);
        var ids = animationIds?.ToList() ?? rig.Sources.Keys.ToList();
        bool collision = geometry == VolumeGeometry.Collision;
        var pieces = Pieces(rig, geometry);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rig.Sources.TryGetValue(id, out var src)) return $"no animation '{id}'";
            anims.Add(src.Animation);
            float t0 = src.Animation.Start, t1 = src.Animation.End;
            var times = src.KeyTimes().Where(t => t > t0 && t < t1).Append(t0).Append(t1).Distinct().OrderBy(t => t).ToList();
            // intervals refined by largest bound first until every bound meets the target or the budget is spent
            var intervals = new List<(float A, float B, double D)>();
            for (int i = 0; i + 1 < times.Count; i++) intervals.Add((times[i], times[i + 1], DeltaBound(rig, src, pieces, times[i], times[i + 1], collision)));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int worst = -1; double wd = options.DeltaTarget;
                for (int i = 0; i < intervals.Count; i++) if (intervals[i].D > wd && intervals[i].B - intervals[i].A > 1e-6f) { wd = intervals[i].D; worst = i; }
                if (worst < 0) break;
                if (intervals.Count + 1 >= options.SampleBudget) { converged = false; break; }
                var (a, b, _) = intervals[worst];
                float m = (a + b) / 2;
                intervals[worst] = (a, m, DeltaBound(rig, src, pieces, a, m, collision));
                intervals.Insert(worst + 1, (m, b, DeltaBound(rig, src, pieces, m, b, collision)));
            }
            if (intervals.Any(x => x.D > options.DeltaTarget)) converged = false;
            var sampleTimes = intervals.Select(x => (x.A, x.D)).Append((t1, 0.0)).ToList();
            if (intervals.Count == 0) sampleTimes = new() { (t0, 0.0) };
            foreach (var (t, d) in sampleTimes)
            {
                var p = NifMotion.PoseWith(rig, src, id, t, geometry);
                if (p.Status != "posed") return $"{id}@{t}: {p.Reason}";
                sink(id, t, p.Soup!, p.Tags, d);
            }
        }
        return null;
    }

    /// <summary>The region placed by its reference's transform (the production placement, SpatialOverlap.TransformSoup);
    /// deltas scale with the reference scale.</summary>
    public static SweptRegion Place(SweptRegion region, Vec3 position, Vec3 rotation, float scale) =>
        region with
        {
            Samples = region.Samples.Select(s => s with { Soup = SpatialOverlap.TransformSoup(s.Soup, position, rotation, scale), Delta = s.Delta * Math.Abs(scale) }).ToList(),
            MaxDelta = region.MaxDelta * Math.Abs(scale),
        };

    static string Label(SweptRegion r, string id) =>
        r.Animations.FirstOrDefault(a => a.Id == id) is { } a ? $"{a.Id} {a.Cycle} {a.Trigger}" : id;

    static MeshIntersection.SweptBox Box(TriangleSoup s)
    {
        var lo = s.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var hi = s.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return new(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
    }
    static bool Overlap(MeshIntersection.SweptBox a, MeshIntersection.SweptBox b, double grow) =>
        a.MinX - grow <= b.MaxX && b.MinX <= a.MaxX + grow && a.MinY - grow <= b.MaxY && b.MinY <= a.MaxY + grow && a.MinZ - grow <= b.MaxZ && b.MinZ <= a.MaxZ + grow;

    /// <summary>A partner's drawn view (ShapeDrawing) and its solidity: the caller's flag, re-judged on the drawn view when
    /// undrawn triangles were left out.</summary>
    internal static (TriangleSoup Drawn, bool Solid) DrawnPartner(TriangleSoup partner, string[]? partnerTags, bool partnerIsSolid)
    {
        var (pd, _) = MeshSurface.Drawn(partner, partnerTags);
        if (ReferenceEquals(pd, partner) || !partnerIsSolid) return (pd, partnerIsSolid);
        return (pd, pd.TriangleCount > 0 && MeshVolume.Solid(pd).Status == "solid");
    }

    /// <summary>The containment probe point: the soup's first vertex as before, or, for a drawn view that left triangles out, the
    /// first vertex a drawn triangle uses (the vertex array is shared with the whole soup).</summary>
    internal static System.Numerics.Vector3 FirstVertex(TriangleSoup view, TriangleSoup whole) =>
        ReferenceEquals(view, whole) || view.Indices.Length == 0 ? view.Vertices[0] : view.Vertices[view.Indices[0]];

    /// <summary>Witness / never / cannot_tell of a placed region against a placed partner (solid or open surface).</summary>
    public static SweptDecision Contact(SweptRegion moving, TriangleSoup partner, string[]? partnerTags, bool partnerIsSolid,
                                        MotionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (moving.Status != "built") return new("refused", moving.Reason, null, null, null, 0, false, null, null);
        if (partner is null || partner.TriangleCount == 0) return new("refused", "partner: empty_mesh", null, null, null, 0, false, null, null);
        // the geometry-only steps read the drawn view of both members (ShapeDrawing); the crossing measure keeps the whole soups
        var (pd, partnerIsSolidDrawn) = DrawnPartner(partner, partnerTags, partnerIsSolid);
        if (pd.TriangleCount == 0) return new("never", null, null, null, null, 0, moving.Converged, null, null);
        partnerIsSolid = partnerIsSolidDrawn;
        var pBox = Box(pd);
        var pMesh = MeshIntersection.PrepareSurfaceMesh(pd, out var why);
        if (pMesh is null) return new("refused", "partner: " + why, null, null, null, 0, false, null, null);
        // 1. never-test per sample; samples that are not clear are the witness candidates
        var unclear = new List<SweptSample>();
        foreach (var s in moving.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sd, _) = MeshSurface.Drawn(s.Soup, s.Tags);
            if (sd.TriangleCount == 0) continue;
            var sBox = Box(sd);
            if (!Overlap(sBox, pBox, s.Delta)) continue;
            var sMesh = MeshIntersection.PrepareSurfaceMesh(sd, out _);
            if (sMesh is null || MeshIntersection.MeshesWithin(sMesh, pMesh, s.Delta)) { unclear.Add(s); continue; }
            // no surface within delta: each component is wholly inside or outside the other; check containment both ways
            bool contained = false;
            if (partnerIsSolid && sd.Vertices.Length > 0)
            {
                var v = FirstVertex(sd, s.Soup);
                contained |= MeshIntersection.PointInSolid(pMesh, v.X, v.Y, v.Z) != 0;
            }
            if (moving.Solid && pd.Vertices.Length > 0 && sMesh is not null)
            {
                var v = FirstVertex(pd, partner);
                contained |= MeshIntersection.PointInSolid(sMesh, v.X, v.Y, v.Z) != 0;
            }
            if (contained) unclear.Add(s);
        }
        if (unclear.Count == 0) return new("never", null, null, null, null, 0, moving.Converged, null, null);
        // 2. witness search among the unclear samples, nearest first (overlapping boxes before grown-box overlaps)
        foreach (var s in unclear.OrderBy(s => Overlap(Box(MeshSurface.Drawn(s.Soup, s.Tags).Soup), pBox, 0) ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Overlap(Box(MeshSurface.Drawn(s.Soup, s.Tags).Soup), pBox, 0)) continue;
            if (moving.Solid && partnerIsSolid)
            {
                var v = MeshVolume.Measure(s.Soup, pd, moving.Geometry, cancellationToken: cancellationToken);
                if (v.Status == "measured" && v.Intersection is { Lower: > 0 } iv)
                    return new("witness", null, s.AnimationId, s.Time, Label(moving, s.AnimationId), 0, moving.Converged, null, iv.Lower);
            }
            var c = MeshSurface.Measure(s.Soup, s.Tags, partner, partnerTags, moving.Geometry, cancellationToken: cancellationToken);
            if (c.Status == "measured")
            {
                double cross = c.CrossingLength?.Lower ?? 0;
                double buried = Math.Max(c.AInB?.Inside?.Lower ?? 0, c.BInA?.Inside?.Lower ?? 0);
                if (cross > c.RoundingBound || buried > 0)
                    return new("witness", null, s.AnimationId, s.Time, Label(moving, s.AnimationId), 0, moving.Converged, cross, null, c.CrossingLengthByTags);
            }
        }
        return new("cannot_tell", "the partner lies within the swept region's outer bound and no sampled pose crosses it",
                   null, null, null, unclear.Max(s => s.Delta), moving.Converged, null, null);
    }

    /// <summary>The 1-DOF blend rule: null when every pose of a node lies on one family — translation along one line with a
    /// fixed rotation, or rotation about one fixed axis with a fixed translation — so a blend between any two of them stays
    /// inside the family; otherwise the reason a blend between them is not bounded.</summary>
    public static string? OneDofRefusal(IReadOnlyList<(Quaternion Rotation, Vector3 Translation)> nodePoses)
    {
        if (nodePoses.Count <= 1) return null;
        var q0 = Quaternion.Normalize(nodePoses[0].Rotation); var t0 = nodePoses[0].Translation;
        bool sameRot = nodePoses.All(p => HavokClip.AngleBetween(Quaternion.Normalize(p.Rotation), q0) < 1e-4f);
        bool sameT = nodePoses.All(p => Vector3.Distance(p.Translation, t0) < 1e-3f);
        if (sameRot && sameT) return null;
        if (sameRot)
        {
            var far = nodePoses.OrderByDescending(p => Vector3.Distance(p.Translation, t0)).First().Translation;
            var dir = Vector3.Normalize(far - t0);
            foreach (var p in nodePoses)
            {
                var d = p.Translation - t0;
                if ((d - Vector3.Dot(d, dir) * dir).Length() > 1e-3f) return "translations do not lie on one line";
            }
            return null;
        }
        if (sameT)
        {
            Vector3? axis = null;
            foreach (var p in nodePoses)
            {
                var rel = Quaternion.Normalize(p.Rotation) * Quaternion.Conjugate(q0);
                var v = new Vector3(rel.X, rel.Y, rel.Z);
                if (v.Length() < 1e-5f) continue;
                v = Vector3.Normalize(v);
                if (axis is null) { axis = v; continue; }
                if (Vector3.Cross(axis.Value, v).Length() > 1e-3f) return "rotations about more than one axis";
            }
            return null;
        }
        return "both rotation and translation change between poses";
    }

    /// <summary>The near-test index of a region's samples (Volume's), or null when the samples cannot be prepared.</summary>
    internal static MeshIntersection.SweptNear? NearIndex(SweptRegion region) =>
        MeshIntersection.PrepareSweptNear(region.Samples.Select(s => s.Soup).ToList(), out _);

    // ---------------- volume ----------------
    sealed class SampleMesh
    {
        public required SweptSample Sample;
        public object? Mesh;                       // surface mesh, kept when the region is solid
        public object? Solid;                      // prepared solid, when the region is solid
        public required MeshIntersection.SweptBox Box;
    }

    /// <summary>Certified [inner, outer] swept volume, and its intersection with a partner solid when given.</summary>
    public static SweptVolume Volume(SweptRegion moving, TriangleSoup? partnerSolid, VolumeOptions? options = null,
                                     CancellationToken cancellationToken = default, SweptVolumeRun? run = null)
    {
        int threads = run is { Threads: > 0 } ? run.Threads : Environment.ProcessorCount;
        if (run is not null) run.ThreadsUsed = threads;
        options ??= new VolumeOptions();
        if (moving.Status != "built") return new("refused", moving.Reason, null, null, false);
        var ms = new List<SampleMesh>();
        foreach (var s in moving.Samples)
        {
            var mesh = MeshIntersection.PrepareSurfaceMesh(s.Soup, out var why);
            if (mesh is null) return new("refused", "sample: " + why, null, null, false);
            object? solid = moving.Solid ? MeshIntersection.PrepareVolumeSolid(s.Soup, options.MaxTrianglePairs, cancellationToken).Solid : null;
            if (moving.Solid && solid is null) return new("refused", "a sampled pose is not a closed solid", null, null, false);
            ms.Add(new SampleMesh { Sample = s, Mesh = moving.Solid ? mesh : null, Solid = solid, Box = MeshIntersection.MeshBounds(mesh) });
        }
        object? partner = null; MeshIntersection.SweptBox? pBox = null;
        if (partnerSolid is not null)
        {
            var prep = MeshIntersection.PrepareVolumeSolid(partnerSolid, options.MaxTrianglePairs, cancellationToken);
            if (prep.Solid is null) return new("refused", "partner: " + prep.Reason, null, null, false);
            partner = prep.Solid; pBox = MeshIntersection.MeshBounds(partner);
        }
        // root: the outer region's box (grown samples), clipped to the partner's
        double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
        foreach (var m in ms)
        {
            double d = m.Sample.Delta;
            lx = Math.Min(lx, m.Box.MinX - d); ly = Math.Min(ly, m.Box.MinY - d); lz = Math.Min(lz, m.Box.MinZ - d);
            hx = Math.Max(hx, m.Box.MaxX + d); hy = Math.Max(hy, m.Box.MaxY + d); hz = Math.Max(hz, m.Box.MaxZ + d);
        }
        if (pBox is { } pb) { lx = Math.Max(lx, pb.MinX); ly = Math.Max(ly, pb.MinY); lz = Math.Max(lz, pb.MinZ); hx = Math.Min(hx, pb.MaxX); hy = Math.Min(hy, pb.MaxY); hz = Math.Min(hz, pb.MaxZ); }
        if (!(hx > lx && hy > ly && hz > lz))
            return new("measured", null, partner is null ? new VolumeInterval(0, 0) : null, partner is null ? null : new VolumeInterval(0, 0), true);
        double lower = 0, upper = 0, pending = 0;
        long cells = 0;
        var queue = new Queue<(MeshIntersection.SweptBox B, int Depth, long Hint)>();
        double target = options.AbsoluteTarget;
        // state of a cell: inner (inside some sample solid), outerOut (outside every grown sample), partner in/out
        // all samples' triangles in one near test, queried with the largest delta (a larger grow only enlarges the outer
        // region, so it stays sound); per-sample tests only where a sample's box contains the cell, nearest first. The near
        // index stores a triangle that is the same in every sample once (MeshIntersection.SweptNear): the combined mesh's
        // answer without its copies
        double maxDelta = ms.Max(m => m.Sample.Delta);
        var near = MeshIntersection.PrepareSweptNear(ms.Select(m => m.Sample.Soup).ToList(), out var cwhy);
        if (near is null) return new("refused", "swept samples: " + cwhy, null, null, false);
        static bool Contains(MeshIntersection.SweptBox o, MeshIntersection.SweptBox c) =>
            o.MinX <= c.MinX && c.MaxX <= o.MaxX && o.MinY <= c.MinY && c.MaxY <= o.MaxY && o.MinZ <= c.MinZ && c.MaxZ <= o.MaxZ;
        double minCell = Math.Max(1e-3, ms.Max(m => m.Sample.Delta));
        // E2: the near test stops at the first triangle that may meet, trying the parent cell's meeting triangle first
        // (MeshMeetsBoxFirst: MeshMeetsBox's answer, found sooner). Cells are classified in parallel (the eight children of
        // one parent at a time) and placed in their fixed order, so every count, sum and budget decision is the serial one.
        (bool InInner, bool OutOuter, bool InPartner, bool OutPartner, long Witness, long Tested) Classify(MeshIntersection.SweptBox c, long hint)
        {
            double cx = (c.MinX + c.MaxX) / 2, cy = (c.MinY + c.MaxY) / 2, cz = (c.MinZ + c.MaxZ) / 2;
            bool finest = Math.Max(c.MaxX - c.MinX, Math.Max(c.MaxY - c.MinY, c.MaxZ - c.MinZ)) <= minCell;
            long tested = 0;
            long witness = MeshIntersection.NearFirst(near, c, maxDelta, hint, ref tested);
            bool nearAny = witness >= 0;
            bool inInner = false, outOuter = !nearAny;
            if (moving.Solid)
            {
                // candidates whose box contains the cell, nearest box centre first
                var cand = ms.Where(m => Contains(m.Box, c))
                             .OrderBy(m => Math.Abs((m.Box.MinX + m.Box.MaxX) / 2 - cx) + Math.Abs((m.Box.MinY + m.Box.MaxY) / 2 - cy) + Math.Abs((m.Box.MinZ + m.Box.MaxZ) / 2 - cz));
                int tried = 0;
                foreach (var m in cand)
                {
                    // a finest-level cell near a surface is not subdivided further, and only its inner (lower) membership is
                    // at stake: try the 6 nearest samples (fewer tries can only lower the lower bound). Elsewhere the search is
                    // exhaustive, because "out" and the subdivision depend on it.
                    if (nearAny && finest && ++tried > 6) break;
                    if (nearAny && MeshIntersection.MeshMeetsBox(m.Mesh!, c, 0)) continue;   // this sample's surface crosses the cell
                    int inside = MeshIntersection.PointInSolid(m.Solid!, cx, cy, cz);
                    if (inside == 1) { inInner = true; outOuter = false; break; }
                    if (inside == -1) outOuter = false;
                }
                // with no surface within delta, a cell inside no candidate sample is outside every sample: out stays true
            }
            bool inP = true, outP = false;
            if (partner is not null)
            {
                bool meets = MeshIntersection.MeshMeetsBox(partner, c, 0);
                int inside = meets ? -1 : MeshIntersection.PointInSolid(partner, cx, cy, cz);
                inP = inside == 1; outP = inside == 0;
            }
            return (inInner, outOuter, inP, outP, witness, tested);
        }
        static double Vol(MeshIntersection.SweptBox b) => (b.MaxX - b.MinX) * (b.MaxY - b.MinY) * (b.MaxZ - b.MinZ);
        long nearTests = 0, triangleTests = 0;
        void Place(MeshIntersection.SweptBox b, int depth, (bool InInner, bool OutOuter, bool InPartner, bool OutPartner, long Witness, long Tested) k)
        {
            cells++; nearTests++; triangleTests += k.Tested;
            var (inI, outO, inP, outP, w, _) = k;
            if (outO || outP) return;
            if (inI && inP) { lower += Vol(b); upper += Vol(b); return; }
            pending += Vol(b);
            queue.Enqueue((b, depth, w));
        }
        var rootBox = new MeshIntersection.SweptBox(lx, ly, lz, hx, hy, hz);
        Place(rootBox, 0, Classify(rootBox, -1));
        var children = new MeshIntersection.SweptBox[8];
        var classes = new (bool, bool, bool, bool, long, long)[8];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken };
        bool budgetHit = false;
        // the outer bound is grown by the samples' deltas, so refining cells below that scale cannot narrow it: stop there
        // (minCell, defined with the classifier)
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double width = upper + pending - lower;
            target = Math.Max(options.AbsoluteTarget, options.RelativeTarget * Math.Max(lower, 1));
            if (width <= target) break;
            var (c, depth, hint) = queue.Dequeue();
            pending -= Vol(c);
            if (Math.Max(c.MaxX - c.MinX, Math.Max(c.MaxY - c.MinY, c.MaxZ - c.MinZ)) <= minCell) { upper += Vol(c); continue; }
            if (depth >= options.MaxDepth || cells + 8 > options.MaxCells) { upper += Vol(c); budgetHit = true; continue; }
            double mx = (c.MinX + c.MaxX) / 2, my = (c.MinY + c.MaxY) / 2, mz = (c.MinZ + c.MaxZ) / 2;
            for (int k = 0; k < 8; k++)
                children[k] = new((k & 1) == 0 ? c.MinX : mx, (k & 2) == 0 ? c.MinY : my, (k & 4) == 0 ? c.MinZ : mz,
                                  (k & 1) == 0 ? mx : c.MaxX, (k & 2) == 0 ? my : c.MaxY, (k & 4) == 0 ? mz : c.MaxZ);
            if (threads <= 1) for (int k = 0; k < 8; k++) classes[k] = Classify(children[k], hint);
            else Parallel.For(0, 8, parallel, k => classes[k] = Classify(children[k], hint));
            for (int k = 0; k < 8; k++) Place(children[k], depth + 1, classes[k]);
        }
        upper += pending;
        if (run is not null) { run.Cells = cells; run.NearTests = nearTests; run.TriangleTests = triangleTests; }
        bool conv = !budgetHit && moving.Converged;   // resolved to the swept delta's scale (the width then reflects the delta)
        var interval = new VolumeInterval(lower, upper);
        string? reason = conv ? null : budgetHit ? "bounded, not converged: cell or depth budget reached" : "bounded, not converged: sample budget reached";
        return partner is null ? new("measured", reason, interval, null, conv) : new("measured", reason, null, interval, conv);
    }
}
