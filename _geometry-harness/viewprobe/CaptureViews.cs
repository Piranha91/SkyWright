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
    internal static Geometry FromPickShapes(IEnumerable<PickShape> shapes) => new();

    /// <summary>The support rays' answer as a distance: the nearest drawn surface below the ref's lowest band.</summary>
    public static DropAnswer Drop(Geometry g, string key) => new(key, null, null, null, "drop to surface is not built yet.");

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
        float zmin = p.Box.Min.Z;
        var low = new List<Vector3>();
        foreach (var v in p.V) if (v.Z <= zmin + SupportBand) low.Add(v);
        int step = Math.Max(1, low.Count / SupportRays);
        for (int i = 0; i < low.Count; i += step)
        {
            var o = low[i] + new Vector3(0, 0, 0.5f);
            string? hit = null; float best = SupportReach;
            foreach (var r in g.Refs.Values)
            {
                if (r.Key == key || r.I.Count == 0) continue;
                if (o.X < r.Box.Min.X || o.X > r.Box.Max.X || o.Y < r.Box.Min.Y || o.Y > r.Box.Max.Y) continue;
                if (r.Box.Min.Z > o.Z || r.Box.Max.Z < o.Z - SupportReach) continue;
                var vs = r.V; var ix = r.I;
                for (int t = 0; t + 2 < ix.Count; t += 3)
                {
                    float d = DownHit(o, vs[ix[t]], vs[ix[t + 1]], vs[ix[t + 2]]);
                    if (d >= 0f && d < best) { best = d; hit = r.Key; }
                }
            }
            if (hit is not null)
            {
                if (g.Refs[hit].Unassessed is not null) unassessed.Add(hit); else rests.Add(hit);
            }
        }
        return new ContactAnswer(key, touches, rests, unassessed, null, p.Box);
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
