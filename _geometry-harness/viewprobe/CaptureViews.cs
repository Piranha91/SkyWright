using System.Numerics;
using HousecarlCore;

namespace ViewProbe;

/// <summary>
/// The capture fallbacks' render-side primitives (README, "Capture fallbacks: hide, protect, contacts, occluders,
/// coverage"). Each is a value on the render, not a job: the capture scripts compose them.
///
/// --hide FORMKEY        the ref is drawn in no pass (colour, depth, picks, marks, shadows). The CPU placement queries
///                       (--nav-at, --clear-at, --inside-at) still see it: where a camera may stand is judged on the
///                       whole scene. The frame carries a view label (kept in report mode) and the hidden list.
/// --protect FORMKEY     with --hide: refuse (exit 2, one sentence) when a hidden ref touches or supports the named ref
///                       in THIS render's setup, or when that cannot be decided.
/// --contacts-of FORMKEY what the ref touches (soup gap within <see cref="TouchTolerance"/>, crossing included, by
///                       <see cref="SpatialOverlap.Contact(TriangleSoup, TriangleSoup, float, float, float, CancellationToken)"/>)
///                       and rests on (the first geometry hit by downward rays from its lowest drawn vertices, within
///                       <see cref="SupportReach"/>), read on the full transformed render triangles of every drawn ref,
///                       before any --cull-above.
/// --occluders-of FORMKEY the drawn refs nearer than the named ref over its own pixels, with the pixel counts.
/// --coverage-of FORMKEY[@X,Y,Z] a see-through ref's drawn coverage, measured from isolated draws over a black and a
///                       white clear (a = 1 - (white - black)); the mark is the pixels at <see cref="CoverageAlphaMin"/>
///                       or more whose own nearest surface is in front of the opaque scene. With @X,Y,Z, whether that
///                       world point projects into the mark.
/// </summary>
public static class CaptureViews
{
    /// <summary>Frozen at Gate B (2026-10-31): the drawn contribution a coverage pixel needs.</summary>
    public const float CoverageAlphaMin = 0.25f;
    /// <summary>"Touches": the gap between two refs' render triangles is at most this (ContactIndex's near band).</summary>
    public const float TouchTolerance = 8f;
    /// <summary>"Rests on": how far down the support rays look.</summary>
    public const float SupportReach = 512f;
    /// <summary>The support rays start from drawn vertices within this of the ref's lowest point.</summary>
    public const float SupportBand = 16f;
    public const int SupportRays = 64;
    /// <summary>A ref that drew nothing is listed as unassessed when its placement is within this of the participant's box.</summary>
    public const float UnassessedReach = 256f;

    public static readonly List<string> CoverageOf = new();
    public static readonly List<string> Hide = new();
    public static readonly List<string> Protect = new();
    public static readonly List<string> ContactsOf = new();
    public static readonly List<string> OccludersOf = new();
    public static bool CoverageDump;

    public static bool WantsGeometry => Protect.Count > 0 || ContactsOf.Count > 0;

    /// <summary>"KEY" or "KEY@X,Y,Z" -> (key, point or null).</summary>
    public static (string Key, Vector3? Point) CoverageSpec(string spec)
    {
        int at = spec.IndexOf('@');
        if (at < 0) return (spec, null);
        var p = spec[(at + 1)..].Split(',');
        if (p.Length != 3 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
            throw new ArgumentException($"--coverage-of '{spec}': the point after @ must be X,Y,Z.");
        return (spec[..at], new Vector3(x, y, z));
    }

    /// <summary>Per pixel (GL row order, bottom-up): the recovered alpha, from the black- and white-clear RGBA read-backs.</summary>
    public static float[] Alpha(byte[] black, byte[] white, int w, int h)
    {
        var a = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            float d = ((white[o] - black[o]) + (white[o + 1] - black[o + 1]) + (white[o + 2] - black[o + 2])) / (3f * 255f);
            a[i] = Math.Clamp(1f - d, 0f, 1f);
        }
        return a;
    }

    /// <summary>The frame's view label, or null for a picture that is what the player sees.</summary>
    public static string? ViewLabel(int hidden, float? cullAbove, float gridSpacing)
    {
        var parts = new List<string>();
        if (hidden > 0) parts.Add($"CUTAWAY: {hidden} object{(hidden == 1 ? "" : "s")} hidden");
        if (cullAbove is { } c)
            parts.Add($"SLICED TOP-DOWN above z {c:0}" + (gridSpacing > 0f ? $", grid {gridSpacing:0} u" : ""));
        return parts.Count == 0 ? null : string.Join("; ", parts) + "; not the player's view";
    }

    // ---------------------------------------------------------------- geometry for contacts --

    /// <summary>One shot's drawn refs as world (Skyrim-space) triangles, before any --cull-above.</summary>
    public sealed class Geometry
    {
        public sealed class Ref
        {
            public string Key = "", Name = "";
            public readonly List<Vector3> V = new();
            public readonly List<int> I = new();
            public string? Unassessed;     // why this ref's contacts cannot be decided
            public Vector3? At;            // its placement, for a ref that drew no triangles
            public Aabb Box = Aabb.Empty;
            TriangleSoup? _soup;
            public TriangleSoup Soup => _soup ??= new TriangleSoup(V.ToArray(), I.ToArray());
        }
        public readonly Dictionary<string, Ref> Refs = new(StringComparer.Ordinal);

        Ref Get(string key, string name)
        {
            if (!Refs.TryGetValue(key, out var r)) Refs[key] = r = new Ref { Key = key, Name = name };
            return r;
        }

        /// <summary>A shape's world vertices in viewer Y-up space (the upload's own) and its triangle indices.</summary>
        public void Add(string key, string name, Vector3[] wpYUp, int[] indices)
        {
            var r = Get(key, name);
            int b = r.V.Count;
            foreach (var y in wpYUp)
            {
                var s = new Vector3(y.X, -y.Z, y.Y);
                r.V.Add(s);
                r.Box = r.Box.Add(s);
            }
            foreach (var i in indices) r.I.Add(b + i);
        }

        public void MarkUnassessed(string key, string name, string why, Vector3 at)
        {
            var r = Get(key, name);
            r.Unassessed ??= why;
            r.At ??= at;
        }
    }

    public sealed record ContactAnswer(string Key, HashSet<string> Touches, HashSet<string> RestsOn, HashSet<string> Unassessed,
                                       string? Refusal, Aabb Box);

    /// <summary>How far a ref can be lowered straight down onto what is below it, and onto what; or why it cannot.</summary>
    public sealed record DropAnswer(string Key, float? Distance, string? Onto, string? OntoName, string? Refusal);

    /// <summary>A live scene's drawn refs and terrain, from its pick shapes at their current placements.</summary>
    internal static Geometry FromPickShapes(IEnumerable<PickShape> shapes)
    {
        var g = new Geometry();
        foreach (var s in shapes)
        {
            if (s.Kind == PickKind.Ref && !s.Ref.RefKey.IsNull) g.Add(s.Ref.RefKey.ToString(), s.Ref.BaseEditorId ?? s.Ref.RefKey.ToString(), s.Verts, s.Tris);
            else if (s.Kind == PickKind.Terrain) g.Add(TerrainKey, TerrainKey, s.Verts, s.Tris);
        }
        return g;
    }

    /// <summary>The key the terrain carries in a live geometry.</summary>
    public const string TerrainKey = "terrain";

    /// <summary>The support rays' answer as a distance: the nearest drawn surface below the ref's lowest band, which the
    /// ref can be lowered onto. It lowers only: a ref another drawn surface already crosses (between its lowest band and
    /// its top, at up to <see cref="SupportRays"/> sampled points) is refused, as is one with nothing within reach below.
    /// The distance is measured from the lowest <see cref="SupportBand"/> units of the ref only, so a wider part higher up
    /// that would touch first is not seen.</summary>
    public static DropAnswer Drop(Geometry g, string key)
    {
        if (!g.Refs.TryGetValue(key, out var p) || p.I.Count == 0)
            return new(key, null, null, null, $"{key} is not drawn in this scene, so it has nothing to drop.");
        // sunk: cast from just under the top, over the lowest band's points; another surface above the band is one it is sunk into
        // sampled like the support rays: up to SupportRays of the lowest band's points
        float zmin = p.Box.Min.Z, zmax = p.Box.Max.Z;
        var band = p.V.Where(v => v.Z <= zmin + SupportBand).ToList();
        int stride = Math.Max(1, band.Count / SupportRays);
        for (int i = 0; i < band.Count; i += stride)
        {
            var v = band[i];
            var top = new Vector3(v.X, v.Y, zmax + 0.5f);
            var (above, d) = FirstBelow(g, key, top, zmax - zmin);
            if (above is not null && zmax + 0.5f - d > zmin + 0.5f)
                return new(key, null, null, null, $"{Describe(g, above)} crosses {key} above its lowest {SupportBand:0} units, so it is already sunk into it; drop lowers only, so move it up first.");
        }
        var hits = SupportHits(g, key, p).Where(h => h.Hit is not null).ToList();
        if (hits.Count == 0)
            return new(key, null, null, null, $"nothing drawn lies within {SupportReach:0} units below {key}; drop lowers only, onto a drawn surface below it.");
        var nearest = hits.MinBy(h => h.Distance);
        return new(key, MathF.Max(nearest.Distance - 0.5f, 0f), nearest.Hit, g.Refs[nearest.Hit!].Name, null);
    }

    static string Describe(Geometry g, string k) => k == TerrainKey ? "the terrain" : $"{k} ({g.Refs[k].Name})";

    static float BoxGap(Aabb a, Aabb b) => SpatialOverlap.BoxDistance(a, b);

    /// <summary>What <paramref name="key"/> touches and rests on in this shot, or a refusal when that cannot be decided.</summary>
    public static ContactAnswer Contacts(Geometry g, string key)
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        if (!g.Refs.TryGetValue(key, out var p) || p.I.Count == 0)
            return new ContactAnswer(key, empty, empty, empty, "absent", Aabb.Empty);
        if (p.Unassessed is { } why)
            return new ContactAnswer(key, empty, empty, empty, $"{key}'s own geometry is incomplete ({why}), so what it touches cannot be decided", p.Box);
        var touches = new HashSet<string>(StringComparer.Ordinal);
        var rests = new HashSet<string>(StringComparer.Ordinal);
        var unassessed = new HashSet<string>(StringComparer.Ordinal);
        var grid = TriangleGrid.Build(p.Soup);
        foreach (var r in g.Refs.Values)
        {
            if (r.Key == key) continue;
            // a placed ref the renderer drew nothing of (its mesh missing or unreadable) within reach: it is not in the picture,
            // so it cannot be hidden, but what it would touch is unknown, and it is named
            if (r.I.Count == 0)
            {
                if (r.Unassessed is not null && r.At is { } at && BoxGap(p.Box, Aabb.Empty.Add(at)) <= UnassessedReach) unassessed.Add(r.Key);
                continue;
            }
            if (BoxGap(p.Box, r.Box) > TouchTolerance) continue;
            if (r.Unassessed is not null) { unassessed.Add(r.Key); continue; }
            var c = SpatialOverlap.Contact(r.Soup, grid, TouchTolerance + 1f, 4f);
            if (c.Distance <= TouchTolerance || c.Crosses) touches.Add(r.Key);
        }
        // rests on: the first drawn geometry straight below the participant's lowest band of vertices
        foreach (var (hit, _) in SupportHits(g, key, p))
        {
            if (hit is not null)
            {
                if (g.Refs[hit].Unassessed is not null) unassessed.Add(hit); else rests.Add(hit);
            }
        }
        return new ContactAnswer(key, touches, rests, unassessed, null, p.Box);
    }

    /// <summary>The support rays: from up to <see cref="SupportRays"/> of the ref's lowest band of vertices (0.5 above
    /// each), straight down within <see cref="SupportReach"/>, the first other drawn ref hit and how far, or (null, reach).</summary>
    static List<(string? Hit, float Distance)> SupportHits(Geometry g, string key, Geometry.Ref p)
    {
        var hits = new List<(string?, float)>();
        float zmin = p.Box.Min.Z;
        var low = new List<Vector3>();
        foreach (var v in p.V) if (v.Z <= zmin + SupportBand) low.Add(v);
        int step = Math.Max(1, low.Count / SupportRays);
        for (int i = 0; i < low.Count; i += step)
        {
            var (hit, best) = FirstBelow(g, key, low[i] + new Vector3(0, 0, 0.5f), SupportReach);
            hits.Add((hit, best));
        }
        return hits;
    }

    /// <summary>Rays cast on this thread (each <see cref="FirstBelow"/> call), so a test can pin the sampling without a clock.</summary>
    [ThreadStatic] internal static long RayCasts;

    /// <summary>The first other drawn ref straight below <paramref name="o"/> within <paramref name="reach"/>, and how far.</summary>
    static (string? Hit, float Distance) FirstBelow(Geometry g, string key, Vector3 o, float reach)
    {
        RayCasts++;
        string? hit = null; float best = reach;
        foreach (var r in g.Refs.Values)
        {
            if (r.Key == key || r.I.Count == 0) continue;
            if (o.X < r.Box.Min.X || o.X > r.Box.Max.X || o.Y < r.Box.Min.Y || o.Y > r.Box.Max.Y) continue;
            if (r.Box.Min.Z > o.Z || r.Box.Max.Z < o.Z - reach) continue;
            var vs = r.V; var ix = r.I;
            for (int t = 0; t + 2 < ix.Count; t += 3)
            {
                float d = DownHit(o, vs[ix[t]], vs[ix[t + 1]], vs[ix[t + 2]]);
                if (d >= 0f && d < best) { best = d; hit = r.Key; }
            }
        }
        return (hit, best);
    }

    /// <summary>Distance along -Z from <paramref name="o"/> to the triangle, or -1 (Möller-Trumbore, both faces).</summary>
    static float DownHit(Vector3 o, Vector3 a, Vector3 b, Vector3 c)
    {
        var d = new Vector3(0, 0, -1);
        var e1 = b - a; var e2 = c - a;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-8f) return -1f;
        float inv = 1f / det;
        var tv = o - a;
        float u = Vector3.Dot(tv, pv) * inv;
        if (u < 0f || u > 1f) return -1f;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < 0f || u + v > 1f) return -1f;
        float t = Vector3.Dot(e2, qv) * inv;
        return t >= 0f ? t : -1f;
    }

    public static string Line(ContactAnswer a)
        => $"contacts-of {a.Key}: touches [{string.Join(", ", a.Touches.OrderBy(x => x, StringComparer.Ordinal))}]; "
         + $"rests on [{string.Join(", ", a.RestsOn.OrderBy(x => x, StringComparer.Ordinal))}]; "
         + $"unassessed [{string.Join(", ", a.Unassessed.OrderBy(x => x, StringComparer.Ordinal))}]";

    public static string BoxLine(ContactAnswer a)
        => a.Box.IsEmpty ? $"contacts-of {a.Key}: box none"
         : $"contacts-of {a.Key}: box {a.Box.Min.X:0},{a.Box.Min.Y:0},{a.Box.Min.Z:0} {a.Box.Max.X:0},{a.Box.Max.Y:0},{a.Box.Max.Z:0}";
}

/// <summary>A capture-view refusal: one plain sentence, exit 2 (caught where GlAllocationRefusal is).</summary>
internal sealed class CaptureRefusal : Exception
{
    public CaptureRefusal(string message) : base(message) { }
}
