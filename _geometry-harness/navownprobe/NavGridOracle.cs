using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Skyrim;

// `navownprobe --navgrid-oracle OUT_DIR PLUGIN...`: rebuild every navmesh's grid with NavGrid and compare it with the grid the
// plugin stores, piece by piece, so a difference says which part of the rule it is in:
//   bounds    NavGrid.Bounds against Min/Max, bit for bit;
//   divisor   NavGrid.Divisor against NavmeshGridDivisor;
//   sections  NavGrid.Section against MaxDistanceX/Y, bit for bit;
//   cells     the cells filled under the STORED header against NavmeshGrid, byte for byte, so the cell rule is judged
//             apart from the header rule;
//   whole     NavGrid.Rebuild's header and cell bytes against the stored ones at once.
// Every entry that differs is put in one class by how far its triangle is from its cell in plan (the separation: positive
// apart, negative overlapping; the largest gap along the cell's axes and the triangle's edge normals):
//   order             the same triangles in a different order;
//   edge-touch        within 0.001 u of the cell's edge either way: float rounding at the edge;
//   game-loose        stored only; the triangle misses the cell by 0.001 u or more, but its x/y box reaches it;
//   game-outside-box  stored only; not even the triangle's box reaches the cell;
//   game-omits        rebuilt only; the triangle overlaps the cell by 0.001 u or more and the stored cell leaves it out;
//   unclassified      none of these (the rule has a hole; expected 0).
// A navmesh is "exact", "edge-touch only", or "has <class>" for the worst class among its entries.
// Writes OUT_DIR/summary.json (per plugin and in total), OUT_DIR/differences.jsonl (every differing entry and its class) and
// OUT_DIR/divisor.csv (triangle count, extent, divisor and sizes of each navmesh: the data the divisor rule is fitted on).
static class NavGridOracle
{
    const double Touch = 0.001;
    static readonly string[] Worst = { "unclassified", "game-omits", "game-outside-box", "game-loose", "order", "edge-touch" };

    public static int Run(string outDir, IReadOnlyList<string> plugins)
    {
        Directory.CreateDirectory(outDir);
        using var diffs = new StreamWriter(Path.Combine(outDir, "differences.jsonl"));
        using var csv = new StreamWriter(Path.Combine(outDir, "divisor.csv"));
        csv.WriteLine("plugin,formkey,triangles,vertices,extentX,extentY,divisor,sectionX,sectionY");
        var summary = new List<Dictionary<string, object>>();
        var total = new SortedDictionary<string, long>();
        static void Add(IDictionary<string, long> d, string k, long n = 1) => d[k] = d.TryGetValue(k, out var v) ? v + n : n;

        foreach (var path in plugins)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var c = new SortedDictionary<string, long>();
            string plugin = Path.GetFileName(path);
            foreach (var nm in mod.EnumerateMajorRecords<INavigationMeshGetter>())
            {
                Add(c, "records");
                if (nm.IsDeleted || nm.Data is not { } d) { Add(c, "records without geometry"); continue; }
                if (d.Triangles.Count == 0) { Add(c, "records without triangles"); continue; }
                Add(c, "navmeshes");
                var verts = d.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToList();
                var tris = d.Triangles.Select(t => ((int)t.Vertices.X, (int)t.Vertices.Y, (int)t.Vertices.Z)).ToList();
                uint div = d.NavmeshGridDivisor;
                var smin = new Vec3(d.Min.X, d.Min.Y, d.Min.Z); var smax = new Vec3(d.Max.X, d.Max.Y, d.Max.Z);
                csv.WriteLine($"{plugin},{nm.FormKey},{tris.Count},{verts.Count},{d.Max.X - d.Min.X:R},{d.Max.Y - d.Min.Y:R},{div},{d.MaxDistanceX:R},{d.MaxDistanceY:R}");

                var (bmin, bmax) = NavGrid.Bounds(verts, tris);
                Add(c, Bits(bmin) == Bits(smin) && Bits(bmax) == Bits(smax) ? "bounds exact" : "bounds differ");
                Add(c, NavGrid.Divisor(tris.Count) == div ? "divisor exact" : "divisor differs");
                Add(c, div > 0 && Bits(NavGrid.Section(smin.X, smax.X, div)) == Bits(d.MaxDistanceX) && Bits(NavGrid.Section(smin.Y, smax.Y, div)) == Bits(d.MaxDistanceY)
                    ? "sizes exact" : "sizes differ");

                var stored = NavmeshGridDecode.Cells(div, d.NavmeshGrid.Span, out var why);
                if (stored is null) { Add(c, "stored grid undecodable"); continue; }
                var filled = NavGrid.Fill(verts, tris, div, smin, smax);
                if (filled.CellBytes.AsSpan().SequenceEqual(d.NavmeshGrid.Span)) { Add(c, "cells exact"); Add(c, "navmesh: exact"); }
                else
                {
                    Add(c, "cells differ");
                    var seen = new HashSet<string>();
                    for (int k = 0; k < stored.Length; k++)
                    {
                        var s = stored[k]; var f = filled.Cells[k];
                        if (s.SequenceEqual(f)) continue;
                        var ss = s.ToHashSet(); var fs = f.ToHashSet();
                        if (ss.SetEquals(fs)) { Entry(c, seen, diffs, plugin, nm.FormKey.ToString(), k, -1, "order", double.NaN); continue; }
                        foreach (var t in ss.Except(fs)) Classify(c, seen, diffs, plugin, nm.FormKey.ToString(), k, t, stored: true, verts, tris, (int)div, smin, filled);
                        foreach (var t in fs.Except(ss)) Classify(c, seen, diffs, plugin, nm.FormKey.ToString(), k, t, stored: false, verts, tris, (int)div, smin, filled);
                    }
                    var worst = Worst.First(seen.Contains);
                    Add(c, worst == "edge-touch" ? "navmesh: edge-touch only" : $"navmesh: has {worst}");
                }

                var whole = NavGrid.Rebuild(verts, tris);
                Add(c, whole.Divisor == div && Bits(whole.SectionX) == Bits(d.MaxDistanceX) && Bits(whole.SectionY) == Bits(d.MaxDistanceY)
                       && Bits(whole.Min) == Bits(smin) && Bits(whole.Max) == Bits(smax) && whole.CellBytes.AsSpan().SequenceEqual(d.NavmeshGrid.Span)
                    ? "whole exact" : "whole differs");
            }
            foreach (var (k, v) in c) Add(total, k, v);
            summary.Add(new Dictionary<string, object> { ["plugin"] = plugin, ["counts"] = c });
        }
        summary.Add(new Dictionary<string, object> { ["plugin"] = "TOTAL", ["counts"] = total });
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outDir, "summary.json"), json);
        Console.WriteLine(json);
        return total.GetValueOrDefault("entry: unclassified") == 0 && total.GetValueOrDefault("stored grid undecodable") == 0 ? 0 : 1;
    }

    static void Classify(IDictionary<string, long> c, HashSet<string> seen, StreamWriter w, string plugin, string fk, int k, int t, bool stored,
                         List<Vec3> v, List<(int A, int B, int C)> tris, int n, Vec3 min, NavGridData g)
    {
        var (sep, boxSep, scale) = Separation(v, tris[t], k, n, min, g.SectionX, g.SectionY);
        double touch = Math.Max(Touch, 4 * (MathF.BitIncrement(scale) - scale));
        string cls = Math.Abs(sep) < touch ? "edge-touch"
                   : stored && sep > 0 ? (boxSep < touch ? "game-loose" : "game-outside-box")
                   : !stored && sep < 0 ? "game-omits"
                   : "unclassified";
        Entry(c, seen, w, plugin, fk, k, t, cls, sep, stored);
    }

    static void Entry(IDictionary<string, long> c, HashSet<string> seen, StreamWriter w, string plugin, string fk, int cell, int tri, string cls, double sep, bool? stored = null)
    {
        c[$"entry: {cls}"] = c.TryGetValue($"entry: {cls}", out var x) ? x + 1 : 1;
        seen.Add(cls);
        w.WriteLine(JsonSerializer.Serialize(new { plugin, navmesh = fk, cell, tri, side = stored is null ? "both" : stored.Value ? "stored only" : "rebuilt only", cls, separation = double.IsNaN(sep) ? (double?)null : sep }));
    }

    /// <summary>The triangle's separation from cell k in plan (positive apart, negative overlapping), over the cell's axes
    /// and the triangle's unit edge normals, with the cell's edges as NavGrid computes them; and the same over the cell's
    /// axes alone (the triangle's box); and the largest coordinate magnitude of the cell's edges, whose float step sets
    /// what "at the edge" means. A triangle flat in plan is its line, both sides outward.</summary>
    static (double Sep, double BoxSep, float Scale) Separation(List<Vec3> v, (int A, int B, int C) t, int k, int n, Vec3 min, float sx, float sy)
    {
        int row = k / n, col = k % n;
        float ax = min.X, ay = min.Y;
        for (int i = 0; i < col; i++) ax += sx;
        for (int i = 0; i < row; i++) ay += sy;
        double x0 = ax, x1 = (float)(ax + sx), y0 = ay, y1 = (float)(ay + sy);
        var p = new[] { v[t.A], v[t.B], v[t.C] };
        double box = Math.Max(Math.Max(x0 - p.Max(q => q.X), p.Min(q => q.X) - x1), Math.Max(y0 - p.Max(q => q.Y), p.Min(q => q.Y) - y1));
        double sep = box;
        var corners = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
        for (int i = 0; i < 3; i++)
        {
            var a = p[i]; var b = p[(i + 1) % 3]; var c = p[(i + 2) % 3];
            double nx = b.Y - a.Y, ny = a.X - (double)b.X, len = Math.Sqrt(nx * nx + ny * ny);
            if (len == 0) continue;
            nx /= len; ny /= len;
            double sc = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
            if (sc > 0) { nx = -nx; ny = -ny; } // outward
            sep = Math.Max(sep, corners.Min(q => (q.Item1 - a.X) * nx + (q.Item2 - a.Y) * ny));
            if (sc == 0) sep = Math.Max(sep, corners.Min(q => -((q.Item1 - a.X) * nx + (q.Item2 - a.Y) * ny)));
        }
        float scale = (float)new[] { Math.Abs(x0), Math.Abs(x1), Math.Abs(y0), Math.Abs(y1) }.Max();
        return (sep, box, scale);
    }

    static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
    static string Bits(Vec3 v) => Bits(v.X) + Bits(v.Y) + Bits(v.Z);
}
