using HarnessShared;
using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ViewProbe;

// --ring visible: LOAD EVERY EXTERIOR CELL WHOSE GROUND THE FRAME CAN SEE, not only the target and its +-1 ring.
//
// The ring is right for an eye-level shot, which is what the harness was built for; from a high camera the game shows
// terrain, water and objects out to the frame's edges and the 3x3 is an island in sky. This file picks the cells:
//   1. Bands: the winning LAND of every cell within `reach` cells of the eye's own cell, decoded to its lowest and
//      highest vertex (last plugin in load order wins; a LAND that declares no heights removes the square, as the
//      terrain walk does). One pass over the order, before the winning-state walk, because that walk's admission rule
//      (WinningRefWalk.Classify) needs the set.
//   2. Rays: a grid of rays through the frame (the render's own pinhole: vertical fov, the canvas aspect, world-Z up),
//      each marched from the eye in short steps. A cell joins when a ray passes over it no higher than the cell's
//      highest ground plus ObjectMargin (a tree or a roof standing on that ground can meet the ray there); the ray stops
//      at the first sample below a cell's LOWEST ground (it has met the ground by then), on leaving the reach, or once
//      it climbs above every band while rising. A square with no LAND (open sea) stands in at the lowest ground seen.
//   3. The target's 3x3 always joins, so the set is a superset of what the default draws, and the cap keeps the
//      cells nearest the eye.
// Conservative by construction: a cell some pixel could see the ground of is in the set (up to the ray spacing and
// the cap), and the extra cells it can include are ones the ray passes low over before it lands, all next to cells
// that are in the frame anyway. It reads the ground only: a tall reference in a cell outside the set is admitted by the
// ring-edge rule when its box enters the set (WinningRefWalk), and not otherwise.
internal static class VisibleCells
{
    internal const int DefaultReach = 8;      // cells from the eye's own cell (Chebyshev): 8 = 32,768 u
    internal const int DefaultCap = 160;      // cells, the target's 3x3 included
    internal const float ObjectMargin = 2048f;
    const float Step = 256f;
    const int RaysAcross = 96;

    internal sealed record Selection(HashSet<(int X, int Y)> Cells, int Footprint, int Rays, int RaysOutOfReach,
                                     int CutByCap, int BandCells, double Seconds);

    /// <summary>Lowest and highest ground per grid square within <paramref name="reach"/> of <paramref name="centre"/>,
    /// from the winning LAND of each square in <paramref name="worldspace"/>.</summary>
    internal static Dictionary<(int X, int Y), (float Min, float Max)> Bands(IReadOnlyList<string> orderedPaths,
        FormKey worldspace, (int X, int Y) centre, int reach)
    {
        var bands = new Dictionary<(int X, int Y), (float Min, float Max)>();
        for (int i = 0; i < orderedPaths.Count; i++)
        {
            var path = orderedPaths[i];
            ISkyrimModGetter m = PluginRead.Open(path, i, orderedPaths.Count, "the visible-cells pass");
            try
            {
                using (m as IDisposable)
                {
                    foreach (var ws in m.Worldspaces.Records)
                    {
                        if (ws.FormKey != worldspace) continue;
                        foreach (var blk in ws.SubCells)
                            foreach (var sub in blk.Items)
                                foreach (var c in sub.Items)
                                {
                                    if (c.Grid?.Point is not { } gp) continue;
                                    if (Math.Abs(gp.X - centre.X) > reach || Math.Abs(gp.Y - centre.Y) > reach) continue;
                                    if (c.Landscape is not { } ls) continue;
                                    var lc = LandHeight.Classify(ls);
                                    if (lc.Kind == LandHeight.LandKind.DeclaresNone) { bands.Remove((gp.X, gp.Y)); continue; }
                                    // Unreadable: the terrain walk refuses it with the plugin and square named; the
                                    // selection keeps the earlier band rather than refusing twice.
                                    if (lc.Kind != LandHeight.LandKind.Heights || lc.Gradients is null) continue;
                                    var h = LandHeight.Decode(lc.Offset, lc.Gradients);
                                    float lo = float.MaxValue, hi = float.MinValue;
                                    foreach (var v in h) { if (v < lo) lo = v; if (v > hi) hi = v; }
                                    bands[(gp.X, gp.Y)] = (lo, hi);
                                }
                    }
                }
            }
            catch (Exception ex) when (PluginRead.Wraps(ex))
            { throw PluginRead.Failed(path, i, orderedPaths.Count, "the visible-cells pass", ex); }
        }
        return bands;
    }

    /// <summary>The cells the frame can see (the file comment's rule).</summary>
    internal static Selection Select(Vector3 eye, Vector3 look, float vfovDeg, float aspect, (int X, int Y) target,
                                     IReadOnlyDictionary<(int X, int Y), (float Min, float Max)> bands, int reach, int cap,
                                     double bandSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eyeCell = ((int)MathF.Floor(eye.X / 4096f), (int)MathF.Floor(eye.Y / 4096f));
        float groundLo = bands.Count > 0 ? bands.Values.Min(b => b.Min) : 0f;
        float groundHi = bands.Count > 0 ? bands.Values.Max(b => b.Max) : 0f;
        var fwd = Vector3.Normalize(look - eye);
        var right = Vector3.Cross(fwd, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, fwd);
        float tanV = MathF.Tan(float.DegreesToRadians(vfovDeg) / 2f), tanH = tanV * aspect;
        int nx = RaysAcross, ny = Math.Max(2, (int)MathF.Round(RaysAcross / MathF.Max(aspect, 0.1f)));
        var seen = new HashSet<(int X, int Y)>();
        int outOfReach = 0;
        for (int iy = 0; iy <= ny; iy++)
            for (int ix = 0; ix <= nx; ix++)
            {
                float u = -1f + 2f * ix / nx, v = -1f + 2f * iy / ny;
                var d = Vector3.Normalize(fwd + right * (u * tanH) + up * (v * tanV));
                for (float t = 0f; ; t += Step)
                {
                    var p = eye + d * t;
                    var c = ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));
                    if (Math.Abs(c.Item1 - eyeCell.Item1) > reach || Math.Abs(c.Item2 - eyeCell.Item2) > reach)
                    { outOfReach++; break; }
                    (float Min, float Max) b = bands.TryGetValue(c, out var bb) ? bb : (groundLo, groundLo);
                    if (p.Z <= b.Max + ObjectMargin) seen.Add(c);
                    if (p.Z < b.Min) break;                                   // met the ground
                    if (d.Z >= 0f && p.Z > groundHi + ObjectMargin) break;     // rising above every band
                }
            }
        int footprint = seen.Count;
        var cells = new HashSet<(int X, int Y)>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                cells.Add((target.X + dx, target.Y + dy));
        float Dist((int X, int Y) c) =>
            MathF.Max(MathF.Abs((c.X + 0.5f) * 4096f - eye.X), MathF.Abs((c.Y + 0.5f) * 4096f - eye.Y));
        int cut = 0;
        foreach (var c in seen.OrderBy(Dist).ThenBy(c => c.X).ThenBy(c => c.Y))
        {
            if (cells.Contains(c)) continue;
            if (cells.Count >= cap) { cut++; continue; }
            cells.Add(c);
        }
        return new Selection(cells, footprint, (nx + 1) * (ny + 1), outOfReach, cut, bands.Count,
                             bandSeconds + sw.Elapsed.TotalSeconds);
    }

    /// <summary>The console line that says how many cells were loaded and why any were cut.</summary>
    internal static string Describe(Selection s, int reach, int cap, (int X, int Y) target)
    {
        int minX = s.Cells.Min(c => c.X), maxX = s.Cells.Max(c => c.X), minY = s.Cells.Min(c => c.Y), maxY = s.Cells.Max(c => c.Y);
        int beyondGrid = s.Cells.Count(c => Math.Max(Math.Abs(c.X - target.X), Math.Abs(c.Y - target.Y)) > RingReach.EdgeRadius);
        var cutText = new List<string>();
        if (s.RaysOutOfReach > 0) cutText.Add($"{s.RaysOutOfReach:N0} of {s.Rays:N0} rays left the {reach}-cell reach before meeting ground (--ring-reach)");
        if (s.CutByCap > 0) cutText.Add($"{s.CutByCap} more cell(s) under the frame dropped by the {cap}-cell cap, farthest first (--ring-cap)");
        return $"   visible cells: {s.Cells.Count} loaded (x {minX}..{maxX}, y {minY}..{maxY}) - {s.Footprint} under the frame's {s.Rays:N0} rays, "
             + $"plus the target's 3x3; LAND bands from {s.BandCells} cell(s) ({s.Seconds:0.0}s)"
             + (cutText.Count > 0 ? "; cut: " + string.Join("; ", cutText) : "; nothing cut")
             + $"\n   visible cells: full references, terrain and water in all of them; {beyondGrid} lie beyond the game's ±{RingReach.EdgeRadius} grid,"
             + " where the game draws LOD (or nothing) instead of these references";
    }
}
