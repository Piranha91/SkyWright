using System.Numerics;
using CharacterViewer.Rendering;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// OCCLUSION PLANES — the engine's culling volumes, drawn as what they are.
//
// WHAT ONE IS, IN THE RECORDS. A placed object (REFR) whose base is the PlaneMarker static
// (000017:Skyrim.esm). The base carries the Is-Marker flag, so the engine never draws it and the
// marker filter drops it from the scene — which is why planes are invisible in viewprobe and in
// game alike, and why this file exists. The GEOMETRY is the ref's XPRM primitive: a PortalBox whose
// Bounds are (width, 2, height) — a two-unit-thick slab standing in the ref's local X–Z plane — or
// a Box with real thickness, whose culling semantics are a separate question for the game. Bounds
// are FULL extents about the ref's position: every unaltered vanilla plane that also carries the
// CK-era OcclusionPlane subrecord stores exactly Bounds.X/2 and Bounds.Z/2 there as Width and
// Height (measured 2026-09-04 over the rows that carry both; the Dragonborn and one Dawnguard row
// disagree because that subrecord went stale when the plane was resized — which is also why it is
// never read here). Placement is the REFR convention, SpatialOverlap.RefrRotation, never EulerZyx.
//
// PROVENANCE — who put the plane there — is the colour, because the audit this instrument serves
// asks about planes a location mod added, moved, or left standing in a cell it rearranged:
//   vanilla    defined by a Bethesda master, never changed, in a cell no mod touches;
//   inherited  the same plane, unchanged, in a cell a mod contests — the "left standing" candidate;
//   added      defined by a mod;
//   moved      an override changed its placement, extent, kind or enable flag — whoever did it.
// The history is the walk's own: every plugin's version of every PlaneMarker ref, in load order,
// compared with the version before it, on the changed-only rule PlacementHistory uses.

enum PlaneKind { PortalBox, Box }
enum PlaneProvenance { Vanilla, Inherited, Added, Moved }

/// <summary>One occlusion plane as the winning state places it, with the provenance the load walk
/// established. <paramref name="Bounds"/> are the primitive's FULL extents (see the file header).</summary>
sealed record PlaneRender(
    FormKey Key,
    PlaneKind Kind,
    Vector3 Bounds,
    Vector3 Position,
    Vec3 Rotation,
    float Scale,
    PlaneProvenance Provenance,
    string DefinedBy,
    string? ChangedBy,
    IReadOnlyList<string> Chain,
    string RuntimeId)
{
    Vector3[]? _corners;
    Vector3[]? _quad;

    public Vector3 Colour => PlanePalette.Of(Provenance);
    public string KindLabel => Kind == PlaneKind.PortalBox ? "PortalBox" : "Box";
    public string ExtentLabel => Kind == PlaneKind.PortalBox
        ? $"{Bounds.X:0}x{Bounds.Z:0} u"
        : $"{Bounds.X:0}x{Bounds.Y:0}x{Bounds.Z:0} u";
    public string ProvenanceLabel => PlanePalette.Name(Provenance);
    /// <summary>The provenance sentence, with who did it: "added by X", "moved by Y (defined by Skyrim.esm)".</summary>
    public string ProvenanceDetail => Provenance switch
    {
        PlaneProvenance.Added => $"added by {DefinedBy}",
        PlaneProvenance.Moved => $"moved by {ChangedBy ?? "?"} (defined by {DefinedBy})",
        PlaneProvenance.Inherited => $"inherited from {DefinedBy}, unchanged, in a contested cell",
        _ => $"vanilla ({DefinedBy})",
    };
    public string ChainLabel => string.Join(" -> ", Chain);

    /// <summary>The eight corners of the primitive's box in Skyrim space, indexed by axis bits (1 = +x, 2 = +y, 4 = +z).</summary>
    public Vector3[] Corners => _corners ??= PlaneGeometry.BoxCorners(this);
    /// <summary>The slab's mid-plane quad (local y = 0), ring-ordered, Skyrim space. For a Box this is the box's
    /// horizontal mid-section, which is only used for labels; a Box is drawn and tested by its faces.</summary>
    public Vector3[] Quad => _quad ??= PlaneGeometry.MidQuad(this);
    /// <summary>Unit normal of the slab: the ref's local +Y in the world.</summary>
    public Vector3 Normal => PlaneGeometry.Axis(this, Vector3.UnitY);
    public Vector3 Centre => Position;
}

static class PlanePalette
{
    // Chosen clear of the reserved semantic colours — conflict RED, door ORANGE, diff VIOLET, cell
    // outline WHITE — and pre-divided by the overlay shader's x1.5 lift at draw time, so the drawn
    // colour is exactly the one named in the caption. Gold, magenta and teal sit near palette entries
    // the nav wire's auto-pick might choose, so each drawn colour is RESERVED from that pick, the way
    // the route's is.
    public static readonly Vector3 Vanilla = new(0.80f, 0.80f, 0.78f);
    public static readonly Vector3 Inherited = new(1.00f, 0.78f, 0.15f);
    public static readonly Vector3 Added = new(0.90f, 0.25f, 0.95f);
    public static readonly Vector3 Moved = new(0.10f, 0.80f, 0.75f);
    /// <summary>The tint a culled ref wears in the `diff` occlusion preview: a red wash, distinct from the
    /// finding's red outline by being a surface tint rather than a line.</summary>
    public static readonly Vector3 CulledTint = new(1.00f, 0.15f, 0.10f);

    public static Vector3 Of(PlaneProvenance p) => p switch
    {
        PlaneProvenance.Inherited => Inherited,
        PlaneProvenance.Added => Added,
        PlaneProvenance.Moved => Moved,
        _ => Vanilla,
    };
    public static string Name(PlaneProvenance p) => p switch
    {
        PlaneProvenance.Inherited => "inherited",
        PlaneProvenance.Added => "added",
        PlaneProvenance.Moved => "moved",
        _ => "vanilla",
    };
    public static string ColourName(PlaneProvenance p) => p switch
    {
        PlaneProvenance.Inherited => "gold",
        PlaneProvenance.Added => "magenta",
        PlaneProvenance.Moved => "teal",
        _ => "grey",
    };
}

/// <summary>Every plugin's version of one PlaneMarker ref, in load order, reduced to what provenance needs:
/// who defined it, who last changed it, and the chain of plugins that carry it.</summary>
sealed class PlaneHistory
{
    public string DefinedBy = "";
    public readonly List<string> Chain = new();
    public string? ChangedBy;
    public bool Changed;
    string _type = "";
    Vec3 _pos, _rot, _bounds;
    float _scale;
    bool _disabled, _deleted;

    /// <summary>The five base masters. "Vanilla" here means one of these — Creation Club content and the
    /// unofficial patch are mods for this purpose, because the audit asks what a MOD did to the planes.</summary>
    static readonly HashSet<string> Bethesda = new(StringComparer.OrdinalIgnoreCase)
    { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static bool IsBethesda(string plugin) => Bethesda.Contains(plugin);

    /// <summary>Record <paramref name="plugin"/>'s version of <paramref name="r"/>. Call in load order.</summary>
    public static void Record(Dictionary<FormKey, PlaneHistory> hist, PlacedRef r, string plugin)
    {
        string type = r.Primitive?.Type ?? "";
        var bounds = r.Primitive?.Bounds ?? default;
        if (!hist.TryGetValue(r.FormKey, out var h))
        {
            h = new PlaneHistory { DefinedBy = plugin };
            hist[r.FormKey] = h;
        }
        else if (!Same(r.Position, h._pos, 0.01f) || !Same(r.Rotation, h._rot, 1e-4f) || !Same(bounds, h._bounds, 0.01f)
                 || type != h._type || MathF.Abs(r.Scale - h._scale) > 1e-4f
                 || r.InitiallyDisabled != h._disabled || r.Deleted != h._deleted)
        {
            h.Changed = true;
            h.ChangedBy = plugin;
        }
        h.Chain.Add(plugin);
        h._type = type; h._pos = r.Position; h._rot = r.Rotation; h._bounds = bounds;
        h._scale = r.Scale; h._disabled = r.InitiallyDisabled; h._deleted = r.Deleted;
    }

    public PlaneProvenance Classify(bool cellContested)
        => Changed ? PlaneProvenance.Moved
         : !IsBethesda(DefinedBy) ? PlaneProvenance.Added
         : cellContested ? PlaneProvenance.Inherited
         : PlaneProvenance.Vanilla;

    static bool Same(Vec3 a, Vec3 b, float eps)
        => MathF.Abs(a.X - b.X) <= eps && MathF.Abs(a.Y - b.Y) <= eps && MathF.Abs(a.Z - b.Z) <= eps;
}

static class PlaneGeometry
{
    static Vector3 Unit(Vector3 d) => d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;

    public static Vector3[] BoxCorners(PlaneRender p)
    {
        var r = SpatialOverlap.RefrRotation(p.Rotation);
        var h = p.Bounds * 0.5f * p.Scale;
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var l = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            c[i] = Vector3.Transform(l, r) + p.Position;
        }
        return c;
    }

    public static Vector3[] MidQuad(PlaneRender p)
    {
        var r = SpatialOverlap.RefrRotation(p.Rotation);
        var h = p.Bounds * 0.5f * p.Scale;
        return new[]
        {
            Vector3.Transform(new Vector3(-h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(-h.X, 0f, h.Z), r) + p.Position,
        };
    }

    public static Vector3 Axis(PlaneRender p, Vector3 local)
        => Unit(Vector3.TransformNormal(local, SpatialOverlap.RefrRotation(p.Rotation)));

    /// <summary>The box's twelve edges as corner-index pairs.</summary>
    public static readonly (int A, int B)[] BoxEdges =
    {
        (0, 1), (1, 3), (3, 2), (2, 0),
        (4, 5), (5, 7), (7, 6), (6, 4),
        (0, 4), (1, 5), (3, 7), (2, 6),
    };

    /// <summary>The box's six faces: ring-ordered corner indices and the outward LOCAL normal.</summary>
    public static readonly (int[] Ring, Vector3 LocalNormal)[] BoxFaces =
    {
        (new[] { 0, 4, 6, 2 }, -Vector3.UnitX),
        (new[] { 1, 3, 7, 5 }, Vector3.UnitX),
        (new[] { 0, 1, 5, 4 }, -Vector3.UnitY),
        (new[] { 2, 6, 7, 3 }, Vector3.UnitY),
        (new[] { 0, 2, 3, 1 }, -Vector3.UnitZ),
        (new[] { 4, 5, 7, 6 }, Vector3.UnitZ),
    };

    /// <summary>The triangles a ray test or a fill uses: the mid-quad for a PortalBox, the six faces for a Box.
    /// Skyrim space.</summary>
    public static (Vector3[] Verts, int[] Tris) PickTriangles(PlaneRender p)
    {
        if (p.Kind == PlaneKind.PortalBox)
            return (p.Quad, new[] { 0, 1, 2, 0, 2, 3 });
        var tris = new List<int>(36);
        foreach (var (ring, _) in BoxFaces)
        {
            tris.Add(ring[0]); tris.Add(ring[1]); tris.Add(ring[2]);
            tris.Add(ring[0]); tris.Add(ring[2]); tris.Add(ring[3]);
        }
        return (p.Corners, tris.ToArray());
    }

    /// <summary>An n×n lattice of points across the mid-quad, inset a little from its edges — where the report's
    /// collision rays start. Skyrim space.</summary>
    public static Vector3[] GridPoints(PlaneRender p, int n = 5)
    {
        var q = p.Quad;
        var pts = new Vector3[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float u = 0.05f + 0.9f * i / (n - 1), v = 0.05f + 0.9f * j / (n - 1);
                var bottom = Vector3.Lerp(q[0], q[1], u);
                var top = Vector3.Lerp(q[3], q[2], u);
                pts[i * n + j] = Vector3.Lerp(bottom, top, v);
            }
        return pts;
    }

    /// <summary>Nearest hit of a Skyrim-space ray against a plane's pick triangles (both faces), or null.</summary>
    public static float? RayHit(PlaneRender p, Vector3 o, Vector3 d, float limit)
    {
        var (v, idx) = PickTriangles(p);
        float? best = null;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            var a = v[idx[i]]; var e1 = v[idx[i + 1]] - a; var e2 = v[idx[i + 2]] - a;
            var pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-7f) continue;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, pv) * inv;
            if (u < 0f || u > 1f) continue;
            var q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0f || u + w > 1f) continue;
            float t = Vector3.Dot(e2, q) * inv;
            if (t > 1e-3f && t <= limit && (best is null || t < best)) best = t;
        }
        return best;
    }
}

/// <summary>The planes layer as overlay geometry: a translucent fill (the mid-quad of a PortalBox, the six
/// faces of a Box, fainter), a solid depth-tested outline, and the always-visible dashed copy of that
/// outline — the depth buffer is the arbiter, so a plane standing behind a wall is drawn dashed where the
/// wall hides it and never lost.</summary>
static class PlaneOverlay
{
    public const float QuadAlpha = 0.30f, BoxAlpha = 0.12f;

    public static float HalfWidthAt(float d) => Math.Clamp(d * 0.0015f, 0.5f, 20f);

    public static (List<float> QuadFill, List<float> BoxFill, List<float> Solid, List<float> Dash)
        Build(IReadOnlyList<PlaneRender> planes, Vector3 eye)
    {
        var quadFill = new List<float>(); var boxFill = new List<float>();
        var solid = new List<float>(); var dash = new List<float>();
        foreach (var p in planes)
        {
            var col = p.Colour / 1.5f;                       // pre-divided for the overlay shader's x1.5 lift
            if (p.Kind == PlaneKind.PortalBox)
            {
                var q = p.Quad;
                Fill(quadFill, q, col);
                Outline(solid, dash, new[] { q[0], q[1], q[2], q[3], q[0] }, col, eye);
            }
            else
            {
                var c = p.Corners;
                foreach (var (ring, _) in PlaneGeometry.BoxFaces)
                    Fill(boxFill, new[] { c[ring[0]], c[ring[1]], c[ring[2]], c[ring[3]] }, col);
                foreach (var (a, b) in PlaneGeometry.BoxEdges)
                    Outline(solid, dash, new[] { c[a], c[b] }, col, eye);
            }
        }
        return (quadFill, boxFill, solid, dash);
    }

    static void Fill(List<float> dst, Vector3[] ring, Vector3 col)
    {
        void V(Vector3 s) => TexturedScene.Overlay.Vertex(dst, TexturedScene.YUp(s), col, Vector3.Zero);
        V(ring[0]); V(ring[1]); V(ring[2]);
        V(ring[0]); V(ring[2]); V(ring[3]);
    }

    static void Outline(List<float> solid, List<float> dash, Vector3[] pts, Vector3 col, Vector3 eye)
    {
        TexturedScene.Overlay.SolidPolyline(solid, pts, col, HalfWidthAt, eye);
        float d = MathF.Max(1f, Vector3.Distance((pts[0] + pts[^1]) * 0.5f, eye));
        TexturedScene.Overlay.DashedPolyline(dash, pts, col, HalfWidthAt(d), eye);
    }
}

// Per-shape stored-bound predictions and same-shape visibility evidence.
// ShapeCullPolicy carries the separate, explicit empirical support boundary.

/// <summary>Reference identity and per-NIF-block parts. The uploaded world AABB remains
/// display metadata for picking and summaries; it never decides engine culling.</summary>
sealed class RefOcclusion
{
    public SceneRef Ref;
    public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    public readonly List<GlMesh> Meshes = new();
    public readonly List<PickShape> Shapes = new();
    public readonly List<ShapeOcclusion> Parts = new();
    public string AssetHash = "";
    public string? Issue;
    public bool Culled;
    public PlaneRender? CulledBy;
    /// <summary>Report only: at least one predicted-culled shape has its own visible raster
    /// samples. Coverage and viewpoint evidence still govern interpretation.</summary>
    public bool Visible => Parts.Any(s => s.Visible);
    public Vector3 Size => Max - Min;
    public float Volume => MathF.Max(Size.X, 1f) * MathF.Max(Size.Y, 1f) * MathF.Max(Size.Z, 1f);

    /// <summary>Grow the bounds by a Y-up viewer-space vertex (the upload's own space, un-swapped here).</summary>
    public void Grow(Vector3 yUp)
    {
        var s = new Vector3(yUp.X, -yUp.Z, yUp.Y);
        Min = Vector3.Min(Min, s); Max = Vector3.Max(Max, s);
    }
}

/// <summary>The preview's state: every candidate ref, every plane, and the last answer. <see cref="Apply"/>
/// re-evaluates for an eye and a mode and pushes the result onto the meshes — hidden in `on`, tinted in
/// `diff` — and onto the pick shapes' <see cref="PickShape.Culled"/> flag, which the fly-through's clip
/// folds into visibility so the two knives cannot disagree about a shape.</summary>
sealed class OcclusionState
{
    public readonly List<RefOcclusion> Refs = new();
    public readonly IReadOnlyList<PlaneRender> Planes;
    public readonly List<string> SceneIssues = new();
    public string? SceneDepthHash;
    public string? SceneDepthPath;
    public const int DefaultVisibilityBudget = 256;
    public const int MaximumVisibilityBudget = 16384;
    public int VisibilityBudget = DefaultVisibilityBudget;
    public int VisibilityDraws;
    /// <summary>Per plane, the refs it culls from the last eye (a ref culled by several planes is listed under each).</summary>
    public readonly Dictionary<PlaneRender, List<RefOcclusion>> CulledByPlane = new(ReferenceEqualityComparer.Instance);
    /// <summary>Per plane, the refs that reach into its shadow without being contained — the straddlers, kept.</summary>
    public readonly Dictionary<PlaneRender, int> StraddlingByPlane = new(ReferenceEqualityComparer.Instance);
    public int CulledCount;
    /// <summary>Report only: culled refs the eye would see — set by <see cref="MarkVisible"/>.</summary>
    public int VisibleCulledCount;
    public Vector3 LastEye;
    public string LastMode = "off";

    /// <summary>Join culling and otherwise-visible pixels of the SAME shape. The renderer owns
    /// the full scene and isolated shape depth passes; reference centres are never sampled.</summary>
    public void MarkVisible(Func<ShapeOcclusion, ShapeVisibility> visibility)
    {
        VisibleCulledCount = 0;
        foreach (var r in Refs)
        {
            foreach (var shape in r.Parts)
                shape.Visibility = shape.Culled && shape.Issue is null ? visibility(shape) : null;
            if (r.Visible) VisibleCulledCount++;
        }
    }
    readonly Dictionary<GlMesh, (bool Emissive, bool GlowMap, bool Enabled, Vector3 Colour, float Multiple)> _tintSaved = new();
    bool _applied;

    public OcclusionState(IReadOnlyList<PlaneRender> planes) => Planes = planes;

    /// <summary>Re-evaluate for <paramref name="eye"/> and <paramref name="mode"/>. Returns true when anything was
    /// recomputed (the eye moved, the mode changed, or <paramref name="force"/>). <paramref name="ownsVisibility"/>:
    /// the static path, which has no clip to fold into — the meshes' IsRendering is written here.</summary>
    public bool Apply(Vector3 eye, string mode, bool ownsVisibility, bool force = false)
    {
        if (_applied && !force && mode == LastMode && (LastEye - eye).LengthSquared() < 1e-4f) return false;
        _applied = true; LastEye = eye; LastMode = mode;

        Evaluate(eye, mode != "off", honourCanary: true);

        foreach (var r in Refs)
        {
            foreach (var part in r.Parts)
            {
                bool canary = Environment.GetEnvironmentVariable("VP_OCCL_TINT_ALL") == "1";
                bool hide = mode == "on" && (part.Culled || canary);
                bool tint = mode == "diff" && (part.Culled || canary);
                foreach (var m in part.Meshes) SetTint(m, tint);
                if (ownsVisibility) foreach (var m in part.Meshes) m.IsRendering = !hide;
                foreach (var s in part.Picks) s.Culled = hide;
            }
        }
        return true;
    }

    /// <summary>The answer alone — which refs each plane culls from <paramref name="eye"/>, which straddle — with
    /// nothing pushed onto a mesh. The report reads this; the preview reads it and then pushes.</summary>
    /// <param name="honourCanary">The preview's positive control, VP_OCCL_TINT_ALL=1: every ref counts as culled, so
    /// a `diff` frame must come out washed red and an `on` frame empty. A well-placed plane's cull changes no pixel
    /// (its shadow lies inside the shadow of the wall it stands in), which is indistinguishable from a preview that
    /// failed — this is what tells the two apart. The report never honours it.</param>
    public void Evaluate(Vector3 eye, bool active = true, bool honourCanary = false)
    {
        foreach (var r in Refs)
        {
            r.Culled = false; r.CulledBy = null;
            foreach (var s in r.Parts) { s.CulledBy.Clear(); s.Relations.Clear(); s.Visibility = null; }
        }
        CulledByPlane.Clear(); StraddlingByPlane.Clear(); CulledCount = 0;
        if (!active) return;
        if (honourCanary && Environment.GetEnvironmentVariable("VP_OCCL_TINT_ALL") == "1")
        {
            foreach (var r in Refs) r.Culled = true;
            CulledCount = Refs.Count;
        }
        else
            foreach (var p in Planes)
            {
                var (shadow, planeIssue) = ShapeCullPolicy.Shadow(p, eye);
                List<RefOcclusion>? list = null; int straddle = 0;
                foreach (var r in Refs)
                {
                    bool inside = false, touches = false;
                    foreach (var s in r.Parts)
                    {
                        string? issue = r.Issue ?? s.Issue ?? planeIssue;
                        if (issue is not null || s.World is not { } bound || shadow is null)
                        { s.Relations[p] = "unassessed: " + (issue ?? "missing stored sphere"); continue; }
                        var relation = shadow.Classify(bound);
                        s.Relations[p] = relation.ToString();
                        if (relation == ShadowRelation.Inside) { s.CulledBy.Add(p); inside = true; }
                        else if (relation == ShadowRelation.Crossing) touches = true;
                    }
                    if (inside)
                    {
                        (list ??= new()).Add(r);
                        if (!r.Culled) { r.Culled = true; r.CulledBy = p; CulledCount++; }
                    }
                    else if (touches) straddle++;
                }
                if (list is not null) CulledByPlane[p] = list;
                if (straddle > 0) StraddlingByPlane[p] = straddle;
            }
    }

    /// <summary>The `diff` wash: the material shader's own emissive term, exactly the hover highlight's
    /// mechanism — it brightens a surface in proportion to its own albedo, so a dark stone and a pale banner both
    /// read as tinted without either becoming a flat repaint. Saved once per mesh and handed back when the
    /// wash comes off.</summary>
    void SetTint(GlMesh m, bool on)
    {
        if (on)
        {
            if (!_tintSaved.ContainsKey(m))
                _tintSaved[m] = (m.HasEmissive, m.HasGlowMap, m.EmissiveEnabled, m.EmissiveColor, m.EmissiveMultiple);
            m.HasEmissive = true; m.HasGlowMap = false; m.EmissiveEnabled = true;
            m.EmissiveColor = PlanePalette.CulledTint; m.EmissiveMultiple = 0.9f;
        }
        else if (_tintSaved.Remove(m, out var g))
        {
            m.HasEmissive = g.Emissive; m.HasGlowMap = g.GlowMap; m.EmissiveEnabled = g.Enabled;
            m.EmissiveColor = g.Colour; m.EmissiveMultiple = g.Multiple;
        }
    }

    /// <summary>The console's summary of the last answer.</summary>
    public string Summary(string mode)
    {
        if (mode == "off") return "occlusion preview: off";
        int planesCulling = CulledByPlane.Count;
        int boxCulls = CulledByPlane.Where(kv => kv.Key.Kind == PlaneKind.Box).Sum(kv => kv.Value.Count);
        int straddlers = StraddlingByPlane.Values.Sum();
        return $"occlusion research preview ({mode}): {Refs.Sum(r => r.Parts.Count(s => s.Culled)):N0} stored-bound shape(s) in {CulledCount:N0} of {Refs.Count:N0} ref(s) predicted culled"
             + $" — affected shapes {(mode == "on" ? "not drawn" : "tinted red")}; {planesCulling} of {Planes.Count} plane(s) predict culling"
             + (boxCulls > 0 ? $" ({boxCulls:N0} cull(s) by Box-kind planes, counted apart)" : "")
             + $"; {straddlers:N0} straddler(s) kept (reach into a shadow without being inside it)";
    }
}
