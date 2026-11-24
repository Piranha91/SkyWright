using HousecarlCore;

namespace SkyWright.Engine;

// VISIBLE CROSSING OF TWO PLACED SURFACES (lane D, Stage D alpha sampling). The pair measure (MeshSurface) splits the crossing
// length by material class and lets only opaque x opaque decide. This measure asks, for the alpha part: does the crossing curve
// pass through texels that are drawn?
//
// Definitions (lane D Gate A, accepted with F1-F6):
//  * Crossing pieces are the exact segments MeshIntersection.SurfaceContact sums (an opt-in sink; the default pair measure is
//    unchanged). Each lies inside one triangle of each surface (or on an edge: the edge's incident triangles).
//  * Along a piece the triangle's UV map is affine, so the piece is a straight line in UV space. UV' = UV * scale + offset (the
//    shader's transform); texel space x = u W - 0.5, y = v H - 0.5 (row 0 = v 0); repeat or clamp per axis. Mip 0, bilinear.
//  * Alpha along the piece = bilinear texture alpha (a quadratic in t inside one bilinear cell) x vertex alpha (linear, only when
//    the shader reads it) x material alpha: a cubic per cell; the visible sub-intervals are its exact roots against the threshold.
//  * Drawn: alpha test (with or without blend): alpha > theta (lower bound) .. alpha >= theta (upper bound), theta = threshold/255.
//    Blend only: alpha >= tau_blend (0.5), with readings at 0.1 and 0.9; a pair whose visible / not-visible status changes between
//    0.1 and 0.9 is threshold-sensitive (labelled, counted apart). Effect shaders: the view-dependent falloff is taken as 1, so the
//    reading is an upper bound on visibility (labelled).
//  * A point of the curve is a visible crossing only where both surfaces are drawn (opaque is drawn everywhere). An edge-lying
//    piece at a UV seam: lower bound = drawn on both incident triangles, upper = drawn on either. Undecided edge length stays in
//    the upper bound only.
//  * VisibleCrossing = the opaque x opaque interval (MeshSurface.OpaqueCrossing) + the visible alpha interval; attribution is the
//    unchanged four-setup rule on it. A refusal on a member that has alpha crossing length leaves the pair unmeasured, never zero.
//
// STATUS: API frozen at the lane D Gate B red gate; implemented at Gate C.

/// <summary>One crossing segment as the pair measure summed it. Kind <c>interior</c> (a triangle of each surface straddling the
/// other's plane), <c>edge_face</c> (an edge of one surface lying in a face of the other), <c>edge_edge</c>, or <c>undecided</c>
/// (an edge with other than two incident faces: upper bound only). TrianglesA / TrianglesB are soup triangle indices (the edge's
/// incident triangles on an edge side). Endpoints are in the frame the soups were measured in.</summary>
public sealed record CrossingPiece(string Kind, int[] TrianglesA, int[] TrianglesB,
                                   double X0, double Y0, double Z0, double X1, double Y1, double Z1, string TagKey, bool Decided)
{
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0) + (Z1 - Z0) * (Z1 - Z0));
}

/// <summary>The decoded alpha plane for one shape, or the reason there is none (texture missing, VFS read incomplete, unknown
/// format, unresolved alternate texture).</summary>
public delegate (AlphaPlane? Plane, string? Refusal) ShapeTexture(int shape);

/// <summary>What a member's alpha needs: its per-triangle texture coordinates and a texture source per shape.</summary>
public sealed record MemberAlpha(SurfaceTexcoordSet Texcoords, ShapeTexture Texture);

public sealed record VisibilityOptions(double BlendThreshold = 0.5, double LadderLow = 0.1, double LadderHigh = 0.9);

/// <summary>The alpha part of one tag key ("tagA x tagB"): its crossing length, and the visible length interval at the deciding
/// rule; LadderLow / LadderHigh: the visible interval with tau_blend at the ladder ends (null when neither side is blend-only).</summary>
public sealed record AlphaTagReading(string TagKey, double AlphaLength, SurfaceInterval Visible, SurfaceInterval? LadderLow, SurfaceInterval? LadderHigh);

/// <summary>Status <c>measured</c>, <c>refused</c> (Reason names the member and cause) or <c>incomplete</c>. Contact is the
/// unchanged pair measure. AlphaVisible: the visible alpha crossing interval; VisibleCrossing: opaque x opaque + AlphaVisible.
/// Labels: e.g. "effect shader falloff taken as 1: upper bound", "test function N stated; compared as an interval".</summary>
public sealed record SurfaceVisibilityResult(string Status, string? Reason, SurfaceContactResult? Contact,
                                             IReadOnlyList<AlphaTagReading>? ByTags, SurfaceInterval? AlphaVisible,
                                             SurfaceInterval? VisibleCrossing, bool ThresholdSensitive, IReadOnlyList<string> Labels)
{
    public string PolicyVersion => SurfaceVisibility.PolicyVersion;
}

public static class SurfaceVisibility
{
    public const string PolicyVersion = "surface_alpha_visibility_v1";
    const double ZeroAlpha = 1e-12;   // |alpha - threshold| at or below this is "on the threshold" (counts for the >= bound only)

    /// <summary>The pair measure with its crossing pieces (the opt-in sink). The contact result is the one
    /// <see cref="MeshSurface.Measure"/> returns for the same inputs.</summary>
    public static (SurfaceContactResult Contact, IReadOnlyList<CrossingPiece> Pieces) MeasureWithPieces(
        TriangleSoup a, string[]? tagsA, TriangleSoup b, string[]? tagsB, VolumeGeometry geometry,
        SurfaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pieces = new List<CrossingPiece>();
        var contact = MeshSurface.Measure(a, tagsA, b, tagsB, geometry, options, cancellationToken, pieces.Add);
        return (contact, pieces);
    }

    static bool IsAlpha(string tag) => tag is SurfaceMaterial.AlphaTest or SurfaceMaterial.AlphaBlend;

    /// <summary>Visible crossing of two world-space soups. A member whose tags are all opaque needs no alpha (null).</summary>
    public static SurfaceVisibilityResult Measure(TriangleSoup a, string[]? tagsA, MemberAlpha? alphaA,
                                                  TriangleSoup b, string[]? tagsB, MemberAlpha? alphaB,
                                                  VisibilityOptions? options = null, SurfaceOptions? surfaceOptions = null,
                                                  CancellationToken cancellationToken = default)
    {
        var (contact, pieces) = MeasureWithPieces(a, tagsA, b, tagsB, VolumeGeometry.Render, surfaceOptions, cancellationToken);
        return Evaluate(contact, pieces, a, tagsA, () => alphaA, b, tagsB, () => alphaB, options);
    }

    /// <summary>The visible crossing of two model-space surfaces placed by their references' transforms, measured exactly as
    /// <see cref="MeshSurface.MeasurePlacedFramed(TriangleSoup, string[], Vec3, Vec3, float, TriangleSoup, string[], Vec3, Vec3, float, VolumeGeometry, SurfaceOptions, CancellationToken)"/>
    /// measures them (pair frame, zero-area triangles left out): Contact is that call's result and Origin its frame. A member's alpha
    /// source is asked for only when a crossing piece lies on one of its alpha-tagged triangles; its texture coordinates must be
    /// index-aligned with the model soup.</summary>
    public static (SurfaceVisibilityResult Result, Vec3 Origin) MeasurePlaced(TriangleSoup modelA, string[]? tagsA, Func<MemberAlpha?> alphaA, Vec3 posA, Vec3 rotA, float scaleA,
                                                                             TriangleSoup modelB, string[]? tagsB, Func<MemberAlpha?> alphaB, Vec3 posB, Vec3 rotB, float scaleB,
                                                                             VisibilityOptions? options = null, SurfaceOptions? surfaceOptions = null,
                                                                             CancellationToken cancellationToken = default)
    {
        var pieces = new List<CrossingPiece>();
        var (contact, origin) = MeshSurface.MeasurePlacedFramed(modelA, tagsA, posA, rotA, scaleA, modelB, tagsB, posB, rotB, scaleB, VolumeGeometry.Render,
                                                                surfaceOptions, cancellationToken, pieces.Add, out var placedA, out var placedB);
        return (Evaluate(contact, pieces, placedA, tagsA, alphaA, placedB, tagsB, alphaB, options), origin);
    }

    static SurfaceVisibilityResult Evaluate(SurfaceContactResult contact, IReadOnlyList<CrossingPiece> pieces, TriangleSoup a, string[]? tagsA, Func<MemberAlpha?> alphaA,
                                            TriangleSoup b, string[]? tagsB, Func<MemberAlpha?> alphaB, VisibilityOptions? options)
    {
        options ??= new VisibilityOptions();
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        SurfaceVisibilityResult Refuse(string reason) => new("refused", reason, contact, null, null, null, false, labels.ToList());
        if (contact.Status != "measured") return new(contact.Status, contact.Reason, contact, null, null, null, false, labels.ToList());
        var ta = tagsA ?? Enumerable.Repeat(SurfaceMaterial.Opaque, a.TriangleCount).ToArray();
        var tb = tagsB ?? Enumerable.Repeat(SurfaceMaterial.Opaque, b.TriangleCount).ToArray();
        var sideA = new Side(a, ta, new Lazy<MemberAlpha?>(alphaA)); var sideB = new Side(b, tb, new Lazy<MemberAlpha?>(alphaB));

        var byTag = new Dictionary<string, (double Length, double Lo, double Hi, double LLo, double LHi, double HLo, double HHi, bool Ladder)>();
        double outOfScope = 0;
        foreach (var p in pieces)
        {
            if (!p.Decided || p.TagKey == SurfaceMaterial.Pair(SurfaceMaterial.Opaque, SurfaceMaterial.Opaque)) continue;
            double len = p.Length;
            // a side whose triangles are all opaque is drawn everywhere; effect / no-depth-write stay out of scope
            bool scope = true;
            foreach (var (side, tris) in new[] { (sideA, p.TrianglesA), (sideB, p.TrianglesB) })
                foreach (int t in tris)
                {
                    string tag = side.Tags[t];
                    if (tag != SurfaceMaterial.Opaque && !IsAlpha(tag)) scope = false;
                }
            if (!scope) { outOfScope += len; continue; }
            var setsA = sideA.Drawn(p, p.TrianglesA, options, labels, out var whyA);
            if (whyA is not null) return Refuse("A: " + whyA);
            var setsB = sideB.Drawn(p, p.TrianglesB, options, labels, out var whyB);
            if (whyB is not null) return Refuse("B: " + whyB);
            double lo = Measure01(Intersect(setsA.Lo, setsB.Lo)) * len, hi = Measure01(Intersect(setsA.Hi, setsB.Hi)) * len;
            bool ladder = setsA.Ladder || setsB.Ladder;
            double lLo = Measure01(Intersect(setsA.LowLo, setsB.LowLo)) * len, lHi = Measure01(Intersect(setsA.LowHi, setsB.LowHi)) * len;
            double hLo = Measure01(Intersect(setsA.HighLo, setsB.HighLo)) * len, hHi = Measure01(Intersect(setsA.HighHi, setsB.HighHi)) * len;
            var cur = byTag.GetValueOrDefault(p.TagKey);
            byTag[p.TagKey] = (cur.Length + len, cur.Lo + lo, cur.Hi + hi, cur.LLo + lLo, cur.LHi + lHi, cur.HLo + hLo, cur.HHi + hHi, cur.Ladder || ladder);
        }
        if (outOfScope > 0) labels.Add($"{outOfScope:0.###} u of crossing with an effect-shader or no-depth-write shape is out of scope (not counted)");
        var readings = byTag.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new AlphaTagReading(kv.Key, kv.Value.Length,
            new SurfaceInterval(kv.Value.Lo, kv.Value.Hi),
            kv.Value.Ladder ? new SurfaceInterval(kv.Value.LLo, kv.Value.LHi) : null,
            kv.Value.Ladder ? new SurfaceInterval(kv.Value.HLo, kv.Value.HHi) : null)).ToList();
        var alpha = new SurfaceInterval(byTag.Values.Sum(v => v.Lo), byTag.Values.Sum(v => v.Hi));
        var op = MeshSurface.OpaqueCrossing(contact) ?? new SurfaceInterval(0, 0);
        var visible = new SurfaceInterval(op.Lower + alpha.Lower, op.Upper + alpha.Upper);
        // threshold-sensitive: the pair's visible / not-visible status differs between the ladder ends
        bool sensitive = false;
        if (byTag.Values.Any(v => v.Ladder))
        {
            double low = op.Lower + byTag.Values.Sum(v => v.Ladder ? v.LLo : v.Lo), high = op.Lower + byTag.Values.Sum(v => v.Ladder ? v.HLo : v.Lo);
            sensitive = (low > 0) != (high > 0);
        }
        return new("measured", null, contact, readings, alpha, visible, sensitive, labels.ToList());
    }

    // One member: its soup, tags and alpha source, and the drawn subsets of a piece's parameter range [0, 1].
    sealed class Side(TriangleSoup soup, string[] tags, Lazy<MemberAlpha?> lazyAlpha)
    {
        public string[] Tags => tags;
        readonly Dictionary<int, (AlphaPlane? Plane, string? Refusal)> planes = new();

        public record struct Sets(List<(double, double)> Lo, List<(double, double)> Hi, List<(double, double)> LowLo, List<(double, double)> LowHi,
                                  List<(double, double)> HighLo, List<(double, double)> HighHi, bool Ladder);

        // Over the side's triangles at this piece: lower bound = drawn on every triangle (strict), upper = drawn on any (>=).
        public Sets Drawn(CrossingPiece p, int[] tris, VisibilityOptions o, SortedSet<string> labels, out string? refusal)
        {
            refusal = null;
            List<(double, double)>? lo = null, hi = null, lLo = null, lHi = null, hLo = null, hHi = null;
            bool ladder = false;
            foreach (int t in tris)
            {
                List<(double, double)> tLo, tHi, tlLo, tlHi, thLo, thHi;
                if (tags[t] == SurfaceMaterial.Opaque)
                {
                    tLo = tHi = tlLo = tlHi = thLo = thHi = Full();
                }
                else
                {
                    var r = OneTriangle(p, t, o, labels, out refusal);
                    if (refusal is not null) return default;
                    (tLo, tHi, tlLo, tlHi, thLo, thHi) = r.Sets;
                    ladder |= r.Ladder;
                }
                lo = lo is null ? tLo : Intersect(lo, tLo); hi = hi is null ? tHi : Union(hi, tHi);
                lLo = lLo is null ? tlLo : Intersect(lLo, tlLo); lHi = lHi is null ? tlHi : Union(lHi, tlHi);
                hLo = hLo is null ? thLo : Intersect(hLo, thLo); hHi = hHi is null ? thHi : Union(hHi, thHi);
            }
            return new(lo ?? Full(), hi ?? Full(), lLo ?? Full(), lHi ?? Full(), hLo ?? Full(), hHi ?? Full(), ladder);
        }

        ((List<(double, double)>, List<(double, double)>, List<(double, double)>, List<(double, double)>, List<(double, double)>, List<(double, double)>) Sets, bool Ladder)
            OneTriangle(CrossingPiece p, int t, VisibilityOptions o, SortedSet<string> labels, out string? refusal)
        {
            refusal = null;
            var alpha = lazyAlpha.Value;
            if (alpha is null) { refusal = "no alpha source for an alpha-tagged triangle"; return default; }
            var tc = alpha.Texcoords;
            if (tc.Status != "measured" || tc.Triangles is null || tc.Shapes is null) { refusal = tc.Reason ?? "texture coordinates refused"; return default; }
            if (t >= tc.Triangles.Count) { refusal = $"texture coordinates hold {tc.Triangles.Count} triangles, the piece names {t}"; return default; }
            var row = tc.Triangles[t];
            if (row.Shape < 0 || row.Shape >= tc.Shapes.Count) { refusal = $"triangle {t} names shape {row.Shape}, which has no rule"; return default; }
            var rule = tc.Shapes[row.Shape];
            if (rule.Refusal is not null) { refusal = $"shape '{rule.Name}': {rule.Refusal}"; return default; }
            if (!planes.TryGetValue(row.Shape, out var tex)) planes[row.Shape] = tex = alpha.Texture(row.Shape);
            if (tex.Plane is not { } plane) { refusal = $"shape '{rule.Name}': {tex.Refusal ?? "no texture"}"; return default; }
            if (plane.Width <= 0 || plane.Height <= 0 || plane.Alpha.Length < plane.Width * plane.Height) { refusal = $"shape '{rule.Name}': empty alpha plane"; return default; }
            float[] us = [row.U0, row.U1, row.U2], vs = [row.V0, row.V1, row.V2];
            if (us.Concat(vs).Any(x => !float.IsFinite(x))) { refusal = $"shape '{rule.Name}': triangle {t} has no texture coordinates"; return default; }
            if (rule.EffectShader) labels.Add("effect shader: the view-dependent falloff is taken as 1, so its visibility is an upper bound");
            if (rule.AlphaTest && rule.TestFunction is not 4 and not 6) labels.Add($"alpha test function {rule.TestFunction} stated; compared as the > .. >= interval");

            // endpoints in barycentric coordinates of triangle t
            int i0 = soup.Indices[3 * t], i1 = soup.Indices[3 * t + 1], i2 = soup.Indices[3 * t + 2];
            var w0 = Bary(soup.Vertices[i0], soup.Vertices[i1], soup.Vertices[i2], p.X0, p.Y0, p.Z0);
            var w1 = Bary(soup.Vertices[i0], soup.Vertices[i1], soup.Vertices[i2], p.X1, p.Y1, p.Z1);
            double U(double[] w) => (w[0] * row.U0 + w[1] * row.U1 + w[2] * row.U2) * rule.UScale + rule.UOffset;
            double V(double[] w) => (w[0] * row.V0 + w[1] * row.V1 + w[2] * row.V2) * rule.VScale + rule.VOffset;
            double Va(double[] w) => rule.UsesVertexAlpha ? w[0] * row.A0 + w[1] * row.A1 + w[2] * row.A2 : 1;
            var seg = new Segment(U(w0), V(w0), U(w1), V(w1), Va(w0), Va(w1), rule.MaterialAlpha, plane, rule.WrapU, rule.WrapV);

            bool blendOnly = rule.AlphaBlend && !rule.AlphaTest;
            if (rule.AlphaTest)
            {
                var (lo, hi) = seg.Drawn(rule.Threshold / 255.0);
                return ((lo, hi, lo, hi, lo, hi), false);
            }
            if (blendOnly)
            {
                var (lo, hi) = seg.Drawn(o.BlendThreshold);
                var (llo, lhi) = seg.Drawn(o.LadderLow);
                var (hlo, hhi) = seg.Drawn(o.LadderHigh);
                return ((lo, hi, llo, lhi, hlo, hhi), true);
            }
            refusal = $"shape '{rule.Name}' is tagged {tags[t]} but its alpha property has neither test nor blend";
            return default;
        }
    }

    // A piece's straight line in texture space, with vertex alpha linear along it and a constant material alpha.
    sealed record Segment(double U0, double V0, double U1, double V1, double Va0, double Va1, double Material, AlphaPlane Plane, TextureWrap WrapU, TextureWrap WrapV)
    {
        // The parameter sets where alpha > threshold (Lo) and alpha >= threshold (Hi), exact per bilinear cell (a cubic in s).
        public (List<(double, double)> Lo, List<(double, double)> Hi) Drawn(double threshold)
        {
            int W = Plane.Width, H = Plane.Height;
            double x0 = U0 * W - 0.5, x1 = U1 * W - 0.5, y0 = V0 * H - 0.5, y1 = V1 * H - 0.5;
            var cuts = new List<double> { 0, 1 };
            void Grid(double a, double b)
            {
                if (a == b) return;
                double mn = Math.Min(a, b), mx = Math.Max(a, b);
                for (double k = Math.Floor(mn) + 1; k < mx; k++) cuts.Add((k - a) / (b - a));
            }
            Grid(x0, x1); Grid(y0, y1);
            cuts.Sort();
            var lo = new List<(double, double)>(); var hiSet = new List<(double, double)>();
            for (int c = 0; c + 1 < cuts.Count; c++)
            {
                double sa = cuts[c], sb = cuts[c + 1];
                if (!(sb > sa)) continue;
                double sm = 0.5 * (sa + sb);
                int i = (int)Math.Floor(x0 + (x1 - x0) * sm), j = (int)Math.Floor(y0 + (y1 - y0) * sm);
                double c00 = Plane.At(i, j, WrapU, WrapV) / 255.0, c10 = Plane.At(i + 1, j, WrapU, WrapV) / 255.0;
                double c01 = Plane.At(i, j + 1, WrapU, WrapV) / 255.0, c11 = Plane.At(i + 1, j + 1, WrapU, WrapV) / 255.0;
                // local parameter w = s - sa
                double g0 = x0 + (x1 - x0) * sa - i, gw = x1 - x0, h0 = y0 + (y1 - y0) * sa - j, hw = y1 - y0;
                double d1 = c10 - c00, d2 = c01 - c00, d3 = c00 - c10 - c01 + c11;
                double A = c00 + d1 * g0 + d2 * h0 + d3 * g0 * h0;
                double B = d1 * gw + d2 * hw + d3 * (g0 * hw + gw * h0);
                double C = d3 * gw * hw;
                double V0 = Va0 + (Va1 - Va0) * sa, Vw = Va1 - Va0, m = Material;
                double k0 = m * A * V0 - threshold, k1 = m * (A * Vw + B * V0), k2 = m * (B * Vw + C * V0), k3 = m * C * Vw;
                double F(double w) => ((k3 * w + k2) * w + k1) * w + k0;
                double len = sb - sa;
                // split [0, len] at the cubic's real roots (bracketed on monotone pieces between critical points)
                var pts = new List<double> { 0, len };
                foreach (var cpt in QuadRoots(3 * k3, 2 * k2, k1)) if (cpt > 0 && cpt < len) pts.Add(cpt);
                pts.Sort();
                var roots = new List<double>();
                for (int q = 0; q + 1 < pts.Count; q++)
                {
                    double a = pts[q], b = pts[q + 1], fa = F(a), fb = F(b);
                    if (Math.Abs(fa) <= ZeroAlpha || Math.Abs(fb) <= ZeroAlpha || Math.Sign(fa) == Math.Sign(fb)) continue;
                    for (int it = 0; it < 200 && b - a > 1e-15 * Math.Max(1, len); it++)
                    {
                        double mid = 0.5 * (a + b), fm = F(mid);
                        if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; } else b = mid;
                    }
                    roots.Add(0.5 * (a + b));
                }
                var bounds = new List<double> { 0 }; bounds.AddRange(roots); bounds.Add(len);
                bounds.Sort();
                for (int q = 0; q + 1 < bounds.Count; q++)
                {
                    double a = bounds[q], b = bounds[q + 1];
                    if (!(b > a)) continue;
                    double f = F(0.5 * (a + b));
                    if (f > ZeroAlpha) { Add(lo, sa + a, sa + b); Add(hiSet, sa + a, sa + b); }
                    else if (f >= -ZeroAlpha) Add(hiSet, sa + a, sa + b);
                }
            }
            return (lo, hiSet);
        }

        static void Add(List<(double, double)> set, double a, double b)
        {
            if (set.Count > 0 && Math.Abs(set[^1].Item2 - a) <= 1e-15) set[^1] = (set[^1].Item1, b);
            else set.Add((a, b));
        }

        static IEnumerable<double> QuadRoots(double a, double b, double c)
        {
            if (Math.Abs(a) <= 1e-300)
            {
                if (Math.Abs(b) > 1e-300) yield return -c / b;
                yield break;
            }
            double disc = b * b - 4 * a * c;
            if (disc < 0) yield break;
            double sq = Math.Sqrt(disc), qq = -0.5 * (b + Math.CopySign(sq, b));
            if (qq != 0) { yield return qq / a; yield return c / qq; }
            else yield return -b / (2 * a);
        }
    }

    // Barycentric coordinates of a point (assumed in the triangle's plane) by the least-squares projection.
    static double[] Bary(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, double px, double py, double pz)
    {
        double[] A = [a.X, a.Y, a.Z], v0 = [b.X - a.X, b.Y - a.Y, b.Z - a.Z], v1 = [c.X - a.X, c.Y - a.Y, c.Z - a.Z], v2 = [px - a.X, py - a.Y, pz - a.Z];
        double Dot(double[] x, double[] y) => x[0] * y[0] + x[1] * y[1] + x[2] * y[2];
        double d00 = Dot(v0, v0), d01 = Dot(v0, v1), d11 = Dot(v1, v1), d20 = Dot(v2, v0), d21 = Dot(v2, v1);
        double den = d00 * d11 - d01 * d01;
        double v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
        return [1 - v - w, v, w];
    }

    static List<(double, double)> Full() => new() { (0, 1) };
    static double Measure01(List<(double, double)> set) => set.Sum(i => Math.Max(0, i.Item2 - i.Item1));
    static List<(double, double)> Intersect(List<(double, double)> x, List<(double, double)> y)
    {
        var r = new List<(double, double)>();
        foreach (var (a0, a1) in x) foreach (var (b0, b1) in y)
        {
            double lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
            if (hi > lo) r.Add((lo, hi));
        }
        return Normalise(r);
    }
    static List<(double, double)> Union(List<(double, double)> x, List<(double, double)> y) => Normalise(x.Concat(y).ToList());
    static List<(double, double)> Normalise(List<(double, double)> r)
    {
        r.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        var o = new List<(double, double)>();
        foreach (var (a, b) in r)
        {
            if (o.Count > 0 && a <= o[^1].Item2) o[^1] = (o[^1].Item1, Math.Max(o[^1].Item2, b));
            else o.Add((a, b));
        }
        return o;
    }

    /// <summary>Four-setup attribution on <see cref="SurfaceVisibilityResult.VisibleCrossing"/> by
    /// <see cref="MeshVolume.Attribution"/>'s rule; an unmeasured arm reads as <see cref="MeshSurface.Attribution"/> reads it.</summary>
    public static string Attribution(SurfaceVisibilityResult combined, SurfaceVisibilityResult? dependencyOnly,
                                     SurfaceVisibilityResult? plusA, SurfaceVisibilityResult? plusB)
    {
        if (combined.VisibleCrossing is not { } c) return "unresolved";
        bool anyUnmeasured = false;
        VolumeInterval? Arm(SurfaceVisibilityResult? r)
        {
            if (r is null) return null;
            if (r.VisibleCrossing is not { } i) { anyUnmeasured = true; return null; }
            return new VolumeInterval(i.Lower, i.Upper);
        }
        var d = Arm(dependencyOnly); var a = Arm(plusA); var b = Arm(plusB);
        if (anyUnmeasured && c.Upper > 0) return "unresolved";
        return MeshVolume.Attribution(new VolumeInterval(c.Lower, c.Upper), d, a, b);
    }
}
