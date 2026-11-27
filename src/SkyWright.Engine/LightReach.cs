using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

// LIGHT REACH (light-clash measure, lane L 2026-10-04): what a placed point light does to the surfaces around it, read
// from the render geometry. The engine facts it rests on (a public reconstruction of the SE lighting shader,
// reference only): a point light adds colour x (1 - (d/r)^2) x max(0, N.L) to a surface and nothing beyond its radius
// r; a NON-shadow light has no visibility term, so it lights every front-facing surface inside r whether or not
// something opaque stands between them. Three readings over one scene (the opaque render triangles of the references
// around the light, already placed in world space):
//  * Leak: the attenuated, cosine-weighted area of front-facing opaque surface inside r whose straight line to the light
//    is crossed by another opaque triangle - lit where the geometry says it should be dark - beside the same sum for
//    the unblocked surface (Lit). Only meaningful for a non-shadow light; the caller decides that.
//  * Facing: N fixed directions from the light; per ray whether the first opaque hit inside r is a front face, a back
//    face, or nothing. Inside a room the first hits are front faces; behind a wall or in the void they are back faces.
//  * Shape bounds: each drawn shape's stored bound sphere (the engine assigns lights per BSTriShape), for the
//    per-shape light count the caller makes.
// Zero-area triangles carry no area to cross or to light: they are skipped and counted, never a refusal (unlike the
// pair measure's mesh build). Front face = the side the winding normal (b - a) x (c - a) points to; a two-sided
// material is not modelled (stated in every result's policy).

/// <summary>One render shape of a model: whether it is drawn, and its bound sphere in model space.</summary>
/// <remarks>BoxMin / BoxMax: the shape's model-space vertex box (null when its vertices were not read).</remarks>
public sealed record LightShape(int Index, string Name, bool Drawn, bool Lit, Vector3 ModelCenter, float ModelRadius, string BoundSource,
                                Vector3? BoxMin = null, Vector3? BoxMax = null);

/// <summary>A model's opaque render triangles for the light readings, with the shape of each triangle; shapes that could not
/// be read are left out and named in Labels (status "partial"), never silently.</summary>
public sealed record LightModel(string Status, string? Reason, TriangleSoup? Soup, string[]? Tags, int[]? ShapeOfTriangle,
                                IReadOnlyList<LightShape> Shapes, IReadOnlyList<string> Labels);

/// <summary>One emitting part of a model (lane A, 2026-10-13): a drawn shape or particle emitter that shows its own light, with its model-frame
/// box. Kind: "emissive" (lighting shader, Own_Emit and emissive value at least <see cref="LightReach.EmitThreshold"/>), "self-lit effect"
/// (an effect shader without Effect_Lighting), "particle emitter" (a self-lit particle system, at its emitter extent).</summary>
public sealed record EmittingPart(string Part, string Kind, double? Value, Vector3 Min, Vector3 Max);

/// <summary>A model's emitting parts; Status "read" or "refused" with the reason (an unreadable model is not a model without emitters).</summary>
public sealed record EmitterModel(string Status, string? Reason, IReadOnlyList<EmittingPart> Parts);

public sealed record LeakResult(double Lit, double Leak, int Samples, int ReceiverTriangles, IReadOnlyDictionary<int, double> LeakByOwner,
                                IReadOnlyDictionary<int, double> LitByOwner);

public sealed record FacingResult(int Rays, int Front, int Back, int None)
{
    public double BackFraction => Rays == 0 ? 0 : (double)Back / Rays;
    /// <summary>Back-face first hits per surface owner (the scene's owner index): whose surfaces put the light behind them.</summary>
    public IReadOnlyDictionary<int, int> BackByOwner { get; init; } = new Dictionary<int, int>();
}

public static class LightReach
{
    public const string PolicyVersion = "light_reach_v4|winding-normal front|two-sided not modelled|opaque only|lit = drawn lighting-shader shapes; editor markers not drawn|radius = base + XRDS|shape count by placed box|emitters v1: emissive (Own_Emit, value > 0), self-lit effect (no Effect_Lighting), particle emitter extent";

    /// <summary>The smallest max(emissive R, G, B) x EmissiveMultiple that counts as emitting (about 5 / 255).</summary>
    public const double EmitThreshold = 0.02;

    /// <summary>A model's emitting parts (lane A, 2026-10-13, the light-fixture rule), model frame. Shader values come from
    /// <see cref="NifService.Inspect"/> (its flag words and its gated emissive reads), shape boxes from the render reader
    /// <see cref="NifGeometry.MeasureVisibleRender"/> (the boxes <see cref="ReadModel"/> places), particle emitters from
    /// <see cref="ParticleReach.EmitterExtents"/>. A drawn shape (not hidden, not named EditorMarker) emits when it is
    /// (a) a lighting-shader shape with Own_Emit and max(emissive R, G, B) x EmissiveMultiple above 0 ("emissive", with that value; the caller
    /// applies <see cref="EmitThreshold"/> or its own), or (b) an effect-shader shape without Effect_Lighting ("self-lit effect": it is not lit
    /// by scene lights, so it shows its own colour; its colour is not read). A particle system whose effect shader passes (b) emits at its
    /// emitter extent ("particle emitter"). Every part is reported; nothing is decided about hosting here.</summary>
    public static EmitterModel ReadEmitters(byte[] nifBytes)
    {
        var ins = NifService.Inspect(nifBytes);
        if (ins.Inspect is not { } I) return new("refused", "shader: " + ins.Error, Array.Empty<EmittingPart>());
        var geo = NifGeometry.MeasureVisibleRender(nifBytes);
        if (geo.Model is not { } G) return new("refused", "render_unreadable: " + geo.Error, Array.Empty<EmittingPart>());
        if (G.Shapes.Count != I.Shapes.Count)
            return new("refused", $"shape count: the shader reader saw {I.Shapes.Count} shapes, the render reader {G.Shapes.Count}", Array.Empty<EmittingPart>());
        var parts = new List<EmittingPart>();
        var particleShapes = new List<(string Name, NifShader? Shader)>();
        for (int i = 0; i < I.Shapes.Count; i++)
        {
            var s = I.Shapes[i]; var g = G.Shapes[i]; var sh = s.Shader;
            if (MeshSurface.IsParticleShape(g)) { if (!g.Hidden) particleShapes.Add((s.Name, sh)); continue; }
            if (!ShapeDrawn(s.Name, g.Hidden, false, false) || g.Bounds.IsEmpty || sh is null) continue;
            var mn = new Vector3(g.Bounds.Min.X, g.Bounds.Min.Y, g.Bounds.Min.Z); var mx = new Vector3(g.Bounds.Max.X, g.Bounds.Max.Y, g.Bounds.Max.Z);
            if (sh.BlockType == nameof(BSLightingShaderProperty))
            {
                if (sh.Flags1?.Names.Contains("Own_Emit") != true || sh.EmissiveColor is not { } e || sh.EmissiveMultiple is not float m) continue;
                double v = Math.Max(e.R, Math.Max(e.G, e.B)) * m;
                if (v > 0) parts.Add(new EmittingPart(s.Name, "emissive", v, mn, mx));
            }
            else if (sh.BlockType == nameof(BSEffectShaderProperty) && SelfLitEffect(sh))
                parts.Add(new EmittingPart(s.Name, "self-lit effect", null, mn, mx));
        }
        if (particleShapes.Count > 0)
        {
            var ems = ParticleReach.EmitterExtents(nifBytes);
            // pair each drawn particle shape with its emitter record by name and occurrence (both read in block order)
            var used = new HashSet<int>();
            foreach (var (name, sh) in particleShapes)
            {
                int k = Enumerable.Range(0, ems.Count).FirstOrDefault(j => !used.Contains(j) && ems[j].SystemName == name, -1);
                if (k < 0) return new("refused", $"particle system '{name}': no emitter record", Array.Empty<EmittingPart>());
                used.Add(k);
                if (sh is null || sh.BlockType != nameof(BSEffectShaderProperty) || !SelfLitEffect(sh)) continue;
                var pe = ems[k];
                if (pe.Refusal is not null) return new("refused", $"particle system '{name}': {pe.Refusal}", Array.Empty<EmittingPart>());
                parts.Add(new EmittingPart(name, "particle emitter", null, new Vector3(pe.Box.Min.X, pe.Box.Min.Y, pe.Box.Min.Z),
                                           new Vector3(pe.Box.Max.X, pe.Box.Max.Y, pe.Box.Max.Z)));
            }
        }
        return new("read", null, parts);
    }

    /// <summary>An effect shader shows its own colour unless Effect_Lighting (SLSF2) makes scene lights light it.</summary>
    static bool SelfLitEffect(NifShader sh) => sh.Flags2 is { } f2 && !f2.Names.Contains("Effect_Lighting");

    /// <summary>A model-frame box placed by a reference's pose exactly as lightprobe places a lit shape's box: the corner and the three
    /// edge ends through <see cref="SpatialOverlap.TransformSoup"/>; returns the placed corner and the three placed edges.</summary>
    public static (Vector3 Origin, Vector3 EdgeX, Vector3 EdgeY, Vector3 EdgeZ) PlaceBox(Vector3 min, Vector3 max, Vec3 position, Vec3 rotation, float scale)
    {
        var w = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { min, new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, max.Z) },
                                                              Array.Empty<int>()), position, rotation, scale).Vertices;
        return (w[0], w[1] - w[0], w[2] - w[0], w[3] - w[0]);
    }

    /// <summary>The engine's point-light falloff: 1 - (d/r)^2 inside the radius, 0 at and beyond it.</summary>
    public static double Attenuation(double d, double r) => r <= 0 || d >= r ? 0 : 1 - (d / r) * (d / r);

    /// <summary>A placed light's radius: the base's radius plus the reference's XRDS offset when it carries one. XRDS is an
    /// offset, not a replacement: xEdit (wbDefinitionsCommon wbREFRRadiusToStr) displays a light reference's radius as XRDS +
    /// the LIGH DATA radius and stores edits as the difference, and reverse-engineered headers name the field radiusModifier;
    /// vanilla stores negative values.</summary>
    public static float EffectiveRadius(uint baseRadius, float? referenceRadius) =>
        referenceRadius is float rr && float.IsFinite(rr) ? baseRadius + rr : baseRadius;

    /// <summary>Does a light of radius r at <paramref name="light"/> reach a shape's bound sphere (centre, radius R)? The
    /// engine keeps a per-light list of the shapes it affects; a sphere-sphere test is this probe's model of that
    /// assignment (labelled: the engine's own test is not in any source read).</summary>
    public static bool MeetsShape(Vector3 light, float r, Vector3 center, float shapeRadius) =>
        Vector3.Distance(light, center) <= r + shapeRadius;

    /// <summary>Is a placed reference's base drawn at all? A base carrying the Is-Marker header flag (0x00800000; XMarker,
    /// heading and north markers) is never drawn in game - the engine's own rule, as the contact index reads it.</summary>
    public static bool BaseDrawn(bool isMarkerFlag) => !isMarkerFlag;

    /// <summary>Is a render shape drawn in game? Not when hidden, a particle system, or unreadable; and not when it is
    /// editor-marker geometry (a shape named <c>EditorMarker</c>, which the game culls and the renderer skips).</summary>
    public static bool ShapeDrawn(string name, bool hidden, bool particle, bool unread) => ShapeDrawing.Drawn(name, hidden, particle, unread);

    /// <summary>Does a drawn shape take point lights? Only the lighting shader (BSLightingShaderProperty) applies them; an
    /// effect shader or any other shader is not lit by a placed light, so it is neither a lit surface nor a counted shape.</summary>
    public static bool ShapeLit(bool drawn, bool lightingShader) => drawn && lightingShader;

    /// <summary>Per render shape, in <c>NifFile.GetShapes</c> order (the order <see cref="NifGeometry"/> reads them in):
    /// does it use the lighting shader? Null when the NIF cannot be re-read.</summary>
    static List<bool>? LightingShaderShapes(byte[] bytes)
    {
        var nif = new NifFile();
        using (var ms = new MemoryStream(bytes, writable: false))
            if (nif.Load(ms) != 0) return null;
        return nif.GetShapes().Select(sh => nif.GetShader(sh) is BSLightingShaderProperty).ToList();
    }

    /// <summary>Does a light of radius r at <paramref name="light"/> reach a placed shape's box? The box is the shape's model-space
    /// vertex box placed in the world: corner <paramref name="origin"/> and the three edge vectors (rotation and uniform scale keep
    /// them orthogonal). Reached when the distance from the light to the box is at most r.</summary>
    public static bool MeetsShapeBox(Vector3 light, float r, Vector3 origin, Vector3 edgeX, Vector3 edgeY, Vector3 edgeZ)
    {
        var d = light - origin;
        double sq = 0;
        foreach (var e in new[] { edgeX, edgeY, edgeZ })
        {
            double len = e.Length();
            if (len <= 0) continue;   // a flat box: no extent on this axis, so the offset along it is measured in full by the others
            double t = Vector3.Dot(d, e) / len;
            double o = t < 0 ? -t : t > len ? t - len : 0;
            sq += o * o;
        }
        // the component of d outside the span of the non-zero edges (a flat or degenerate box) also counts
        var span = new[] { edgeX, edgeY, edgeZ }.Where(e => e.Length() > 0).Select(Vector3.Normalize).ToList();
        if (span.Count < 3)
        {
            var rest = d;
            foreach (var u in span) rest -= Vector3.Dot(d, u) * u;
            if (span.Count == 2) { var n = Vector3.Normalize(Vector3.Cross(span[0], span[1])); sq += Math.Pow(Vector3.Dot(d, n), 2); }
            else sq += rest.LengthSquared();
        }
        return Math.Sqrt(sq) <= r;
    }

    /// <summary>Read a NIF's render shapes for the light readings. Hidden shapes and particle systems are not drawn; a shape
    /// whose vertices or triangles cannot be read is left out and labelled; materials tag each triangle.</summary>
    public static LightModel ReadModel(byte[] nifBytes)
    {
        var raw = NifGeometry.MeasureVisibleRender(nifBytes, includeTriangles: true);
        if (raw.Model is not { } model) return new("refused", $"render_unreadable: {raw.Error}", null, null, null, Array.Empty<LightShape>(), Array.Empty<string>());
        var labels = new List<string>();
        var shapes = new List<LightShape>();
        var lighting = LightingShaderShapes(nifBytes);
        if (lighting is null || lighting.Count != model.Shapes.Count)
            return new("refused", "shader: the NIF could not be re-read for its shaders", null, null, null, Array.Empty<LightShape>(), Array.Empty<string>());
        var bad = new HashSet<int>();
        for (int i = 0; i < model.Shapes.Count; i++)
        {
            var s = model.Shapes[i];
            bool particle = MeshSurface.IsParticleShape(s);
            bool unread = !s.VerticesRead || !s.TrianglesRead;
            if (unread && !s.Hidden && !particle) { bad.Add(i); labels.Add($"shape '{s.Name}' unreadable: left out"); }
            Vector3 c; float r; string src;
            if (s.StoredBound is { } b) { c = new(b.ModelCenter.X, b.ModelCenter.Y, b.ModelCenter.Z); r = b.ModelRadius; src = "stored"; }
            else if (!s.Bounds.IsEmpty)
            {
                var mn = new Vector3(s.Bounds.Min.X, s.Bounds.Min.Y, s.Bounds.Min.Z); var mx = new Vector3(s.Bounds.Max.X, s.Bounds.Max.Y, s.Bounds.Max.Z);
                c = (mn + mx) / 2; r = Vector3.Distance(mn, mx) / 2; src = "vertex box";
            }
            else { c = default; r = 0; src = "none"; }
            bool drawn = ShapeDrawn(s.Name ?? "", s.Hidden, particle, unread);
            Vector3? bmin = null, bmax = null;
            if (!s.Bounds.IsEmpty)
            {
                bmin = new Vector3(s.Bounds.Min.X, s.Bounds.Min.Y, s.Bounds.Min.Z); bmax = new Vector3(s.Bounds.Max.X, s.Bounds.Max.Y, s.Bounds.Max.Z);
            }
            shapes.Add(new LightShape(i, s.Name ?? "", drawn, ShapeLit(drawn, lighting[i]), c, r, src, bmin, bmax));
        }
        if (model.Soup is not { } soup || model.SoupShapeOfTriangle is not { } map || map.Count != soup.TriangleCount)
            return new(shapes.Any(x => x.Drawn) ? "partial" : "refused", "render: no triangles", null, null, null, shapes, labels);
        var materials = MeshVolume.ShapeMaterials(nifBytes);
        if (materials is null || materials.Count != model.Shapes.Count)
            return new("refused", "material: the NIF could not be re-read for its materials", null, null, null, shapes, labels);
        var keep = new List<int>();
        for (int t = 0; t < soup.TriangleCount; t++)
        {
            int sh = map[t];
            if (sh < 0 || sh >= shapes.Count || !shapes[sh].Lit) continue;   // only lit shapes are surfaces here
            keep.Add(t);
        }
        var idx = new int[keep.Count * 3]; var tags = new string[keep.Count]; var shapeOf = new int[keep.Count];
        for (int k = 0; k < keep.Count; k++)
        {
            int t = keep[k];
            idx[3 * k] = soup.Indices[3 * t]; idx[3 * k + 1] = soup.Indices[3 * t + 1]; idx[3 * k + 2] = soup.Indices[3 * t + 2];
            tags[k] = materials[map[t]].Class; shapeOf[k] = map[t];
        }
        return new(bad.Count > 0 ? "partial" : "measured", bad.Count > 0 ? "some shapes unreadable" : null,
                   new TriangleSoup(soup.Vertices, idx), tags, shapeOf, shapes, labels);
    }
}

/// <summary>The opaque triangles around a light, placed in world space, with an owner index per triangle (the caller's
/// reference index), in a bounding-volume hierarchy for segment and ray queries.</summary>
public sealed class LightScene
{
    const int LeafSize = 8;
    readonly double[] _v;      // 9 per triangle: a, b, c
    readonly int[] _owner;
    readonly int[] _order;
    readonly List<Node> _nodes = new();
    public int Triangles => _owner.Length;
    public int DegenerateSkipped { get; }
    public int NonOpaqueSkipped { get; }

    readonly record struct Node(Vector3D Min, Vector3D Max, int Start, int Count, int Left, int Right);
    public readonly record struct Vector3D(double X, double Y, double Z)
    {
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vector3D b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vector3D Cross(Vector3D b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public static Vector3D From(Vector3 v) => new(v.X, v.Y, v.Z);
    }

    /// <summary>Build from placed soups. Only triangles tagged opaque (or untagged) are kept: alpha and effect surfaces neither
    /// block a light's line nor are counted as lit (opaque-only policy).</summary>
    public LightScene(IEnumerable<(TriangleSoup World, string[]? Tags, int Owner)> parts)
    {
        var v = new List<double>(); var own = new List<int>();
        int degenerate = 0, nonOpaque = 0;
        foreach (var (w, tags, owner) in parts)
        {
            for (int t = 0; t < w.TriangleCount; t++)
            {
                if (tags is not null && t < tags.Length && tags[t] != SurfaceMaterial.Opaque) { nonOpaque++; continue; }
                var a = Vector3D.From(w.Vertices[w.Indices[3 * t]]); var b = Vector3D.From(w.Vertices[w.Indices[3 * t + 1]]); var c = Vector3D.From(w.Vertices[w.Indices[3 * t + 2]]);
                var n = (b - a).Cross(c - a);
                if (!(n.Length > 1e-9) || !double.IsFinite(n.Length)) { degenerate++; continue; }
                v.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                own.Add(owner);
            }
        }
        _v = v.ToArray(); _owner = own.ToArray(); DegenerateSkipped = degenerate; NonOpaqueSkipped = nonOpaque;
        _order = Enumerable.Range(0, _owner.Length).ToArray();
        if (_owner.Length > 0) Build(0, _owner.Length);
    }

    Vector3D A(int t) => new(_v[9 * t], _v[9 * t + 1], _v[9 * t + 2]);
    Vector3D B(int t) => new(_v[9 * t + 3], _v[9 * t + 4], _v[9 * t + 5]);
    Vector3D C(int t) => new(_v[9 * t + 6], _v[9 * t + 7], _v[9 * t + 8]);
    public int OwnerOf(int t) => _owner[t];
    public Vector3D Normal(int t) => (B(t) - A(t)).Cross(C(t) - A(t));

    int Build(int start, int count)
    {
        double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
        double[] cmn = { double.MaxValue, double.MaxValue, double.MaxValue }, cmx = { double.MinValue, double.MinValue, double.MinValue };
        for (int i = start; i < start + count; i++)
        {
            int t = _order[i];
            for (int k = 0; k < 3; k++)
            {
                double c = 0;
                for (int j = 0; j < 3; j++) { double x = _v[9 * t + 3 * j + k]; mn[k] = Math.Min(mn[k], x); mx[k] = Math.Max(mx[k], x); c += x; }
                c /= 3; cmn[k] = Math.Min(cmn[k], c); cmx[k] = Math.Max(cmx[k], c);
            }
        }
        int id = _nodes.Count;
        _nodes.Add(default);
        if (count <= LeafSize) { _nodes[id] = new(new(mn[0], mn[1], mn[2]), new(mx[0], mx[1], mx[2]), start, count, -1, -1); return id; }
        int axis = cmx[0] - cmn[0] >= cmx[1] - cmn[1] && cmx[0] - cmn[0] >= cmx[2] - cmn[2] ? 0 : cmx[1] - cmn[1] >= cmx[2] - cmn[2] ? 1 : 2;
        double Cen(int t) => (_v[9 * t + axis] + _v[9 * t + 3 + axis] + _v[9 * t + 6 + axis]) / 3;
        Array.Sort(_order, start, count, Comparer<int>.Create((x, y) => { int c = Cen(x).CompareTo(Cen(y)); return c != 0 ? c : x.CompareTo(y); }));
        int half = count / 2;
        int l = Build(start, half), r = Build(start + half, count - half);
        _nodes[id] = new(new(mn[0], mn[1], mn[2]), new(mx[0], mx[1], mx[2]), start, count, l, r);
        return id;
    }

    static bool SlabHit(Node n, Vector3D o, Vector3D d, double tmax)
    {
        double t0 = 0, t1 = tmax;
        for (int k = 0; k < 3; k++)
        {
            double ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            double lo = k == 0 ? n.Min.X : k == 1 ? n.Min.Y : n.Min.Z, hi = k == 0 ? n.Max.X : k == 1 ? n.Max.Y : n.Max.Z;
            if (Math.Abs(dk) < 1e-15) { if (ok < lo - 1e-9 || ok > hi + 1e-9) return false; continue; }
            double a = (lo - ok) / dk, b = (hi - ok) / dk;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a - 1e-9); t1 = Math.Min(t1, b + 1e-9);
            if (t0 > t1) return false;
        }
        return true;
    }

    // Moller-Trumbore; returns t along d (not normalised) or -1.
    double Intersect(int t, Vector3D o, Vector3D d)
    {
        var a = A(t); var e1 = B(t) - a; var e2 = C(t) - a;
        var p = d.Cross(e2); double det = e1.Dot(p);
        if (Math.Abs(det) < 1e-12) return -1;
        double inv = 1 / det; var s = o - a;
        double u = s.Dot(p) * inv; if (u < -1e-9 || u > 1 + 1e-9) return -1;
        var q = s.Cross(e1); double w = d.Dot(q) * inv; if (w < -1e-9 || u + w > 1 + 1e-9) return -1;
        return e2.Dot(q) * inv;
    }

    /// <summary>The nearest triangle the ray o + t·d (0 &lt; t ≤ tmax, d unit) meets, and whether it meets its front face.</summary>
    public (int Tri, double T, bool Front)? FirstHit(Vector3D o, Vector3D d, double tmax, int ignore = -1)
    {
        if (_nodes.Count == 0) return null;
        (int, double, bool)? best = null; double bt = tmax;
        var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            var n = _nodes[stack.Pop()];
            if (!SlabHit(n, o, d, bt)) continue;
            if (n.Left < 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    int t = _order[i]; if (t == ignore) continue;
                    double h = Intersect(t, o, d);
                    if (h > 1e-6 && h <= bt) { bt = h; best = (t, h, Normal(t).Dot(d) < 0); }
                }
            }
            else { stack.Push(n.Left); stack.Push(n.Right); }
        }
        return best;
    }

    /// <summary>Is the open segment a→b crossed by any triangle other than <paramref name="ignore"/>?</summary>
    public bool SegmentBlocked(Vector3D a, Vector3D b, int ignore = -1)
    {
        var d = b - a; double len = d.Length; if (len < 1e-9) return false;
        var u = d * (1 / len);
        return FirstHit(a, u, len - 1e-6, ignore) is not null;
    }

    /// <summary>Front / back / no first hit over <paramref name="rays"/> fixed directions (a Fibonacci sphere) from the light.</summary>
    public FacingResult Facing(Vector3 light, double radius, int rays = 64)
    {
        var o = Vector3D.From(light); int f = 0, b = 0, none = 0;
        var backBy = new Dictionary<int, int>();
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < rays; i++)
        {
            double z = 1 - 2 * (i + 0.5) / rays, rr = Math.Sqrt(Math.Max(0, 1 - z * z)), th = golden * i;
            var d = new Vector3D(rr * Math.Cos(th), rr * Math.Sin(th), z);
            var h = FirstHit(o, d, radius);
            if (h is null) none++;
            else if (h.Value.Front) f++;
            else { b++; int ow = _owner[h.Value.Tri]; backBy[ow] = backBy.GetValueOrDefault(ow) + 1; }
        }
        return new FacingResult(rays, f, b, none) { BackByOwner = backBy };
    }

    /// <summary>Lit and leaked attenuated area around a point light (see the file header). Each triangle inside the radius is
    /// sampled on a barycentric grid of about <paramref name="spacing"/> u; a sample's line to the light starts a small offset
    /// off its surface and is tested against every other triangle.</summary>
    public LeakResult Leak(Vector3 light, double radius, double spacing = 32)
    {
        var L = Vector3D.From(light);
        double lit = 0, leak = 0; int samples = 0, receivers = 0;
        var leakBy = new Dictionary<int, double>(); var litBy = new Dictionary<int, double>();
        if (_nodes.Count == 0) return new(0, 0, 0, 0, leakBy, litBy);
        var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            var n = _nodes[stack.Pop()];
            double dx = Math.Max(0, Math.Max(n.Min.X - L.X, L.X - n.Max.X)), dy = Math.Max(0, Math.Max(n.Min.Y - L.Y, L.Y - n.Max.Y)), dz = Math.Max(0, Math.Max(n.Min.Z - L.Z, L.Z - n.Max.Z));
            if (dx * dx + dy * dy + dz * dz >= radius * radius) continue;
            if (n.Left >= 0) { stack.Push(n.Left); stack.Push(n.Right); continue; }
            for (int i = n.Start; i < n.Start + n.Count; i++)
            {
                int t = _order[i];
                var a = A(t); var b = B(t); var c = C(t);
                var nn = Normal(t); double area2 = nn.Length; var nu = nn * (1 / area2);
                double edge = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
                int k = Math.Max(1, (int)Math.Ceiling(edge / spacing));
                double subArea = area2 / 2 / (k * k);
                bool any = false;
                for (int p = 0; p < k; p++)
                    for (int q = 0; q < k - p; q++)
                        for (int up = 0; up < (q < k - p - 1 ? 2 : 1); up++)
                        {
                            // centroid of the small triangle (upright, then the inverted one beside it)
                            double s1, s2;
                            if (up == 0) { s1 = (p + 1.0 / 3) / k; s2 = (q + 1.0 / 3) / k; }
                            else { s1 = (p + 2.0 / 3) / k; s2 = (q + 2.0 / 3) / k; }
                            var pt = a + (b - a) * s1 + (c - a) * s2;
                            var toL = L - pt; double d = toL.Length;
                            if (d >= radius || d < 1e-6) continue;
                            double cos = nu.Dot(toL) / d;
                            if (cos <= 0) continue;
                            double w = LightReach.Attenuation(d, radius) * cos * subArea;
                            samples++; any = true;
                            var start = pt + nu * 0.5;
                            if (SegmentBlocked(start, L, t)) { leak += w; leakBy[_owner[t]] = leakBy.GetValueOrDefault(_owner[t]) + w; }
                            else { lit += w; litBy[_owner[t]] = litBy.GetValueOrDefault(_owner[t]) + w; }
                        }
                if (any) receivers++;
            }
        }
        return new(lit, leak, samples, receivers, leakBy, litBy);
    }
}
