using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

/// <summary>`--nav-link SOURCE=TARGET`: where one navmesh's edges cross into another, measured from the records.</summary>
/// <remarks>Both records are followed over the whole walk under the last-sighting rule, wherever they stand, so the
/// target's cell and winning triangle count are known even when its cell is not drawn. A crossing is a source triangle
/// edge whose flag bit switches its link field to the edge-link table and whose table entry names the target; it is
/// beyond when its target triangle is at or past the target's winning count (navownprobe's EDGE-IDX).</remarks>
internal sealed class NavmeshLinks
{
    public sealed record Pair(FormKey Source, FormKey Target);
    public readonly record struct Crossing(int Tri, string Edge, int Link, int TargetTri, Vec3 A, Vec3 B);
    sealed record CellInfo(string? EditorId, bool Interior, (int X, int Y)? Grid);

    public const int MaxPairs = 16;
    public IReadOnlyList<Pair> Pairs { get; }
    public int CellsObserved { get; private set; }
    readonly HashSet<FormKey> watched = new();
    readonly HarnessShared.LastSightingNavSet set = new();
    readonly Dictionary<FormKey, string> provider = new();
    readonly Dictionary<FormKey, CellInfo> cells = new();

    public NavmeshLinks(IEnumerable<string> specs)
    {
        var pairs = new List<Pair>();
        foreach (var spec in specs)
        {
            var p = spec.Split('=', 2);
            if (p.Length != 2) throw new ArgumentException($"--nav-link '{spec}' is not SOURCE_NAVM=TARGET_NAVM.");
            var pair = new Pair(Key(p[0], spec), Key(p[1], spec));
            if (pair.Source == pair.Target) throw new ArgumentException($"--nav-link '{spec}' names one navmesh twice.");
            if (pairs.Contains(pair)) throw new ArgumentException($"--nav-link '{spec}' is repeated.");
            pairs.Add(pair);
            watched.Add(pair.Source); watched.Add(pair.Target);
        }
        if (pairs.Count > MaxPairs) throw new ArgumentException($"--nav-link permits at most {MaxPairs} pairs.");
        Pairs = pairs;
    }

    static FormKey Key(string value, string spec)
    {
        try
        {
            var key = FormKey.Factory(value.Trim());
            if (key.IsNull || !value.Contains(':')) throw new FormatException();
            return key;
        }
        catch { throw new ArgumentException($"--nav-link '{spec}': '{value}' is not a full FormKey (XXXXXX:Plugin.esp)."); }
    }

    /// <summary>One cell of one plugin, in load order (every cell, as the selection sees them).</summary>
    public void Observe(CellPlacements cp, string plugin)
    {
        CellsObserved++;   // RED STUB: the records are not followed yet
    }

    /// <summary>Every source triangle edge whose edge-link entry names <paramref name="target"/>.</summary>
    public static List<Crossing> CrossingsOf(NavmeshGeometry src, FormKey target)
    {
        var found = new List<Crossing>();   // RED STUB: no crossing is located yet
        return found;
    }

    /// <summary>One JSON document per pair: the answer, or a refusal sentence.</summary>
    public IEnumerable<Dictionary<string, object?>> Answers()
    {
        foreach (var pair in Pairs)
        {
            var row = new Dictionary<string, object?> { ["source"] = pair.Source.ToString(), ["target"] = pair.Target.ToString() };
            string? why = CellsObserved == 0 ? "this run reused an earlier walk, so the records were not followed; run it on its own"
                : !set.Navs.TryGetValue(pair.Source, out var src) ? $"the source navmesh {pair.Source} has no winning version in this load order"
                : !set.Navs.TryGetValue(pair.Target, out var tgt) ? $"the target navmesh {pair.Target} has no winning version in this load order"
                : null;
            if (why is not null) { row["status"] = "refused"; row["reason"] = why; yield return row; continue; }
            src = set.Navs[pair.Source]; tgt = set.Navs[pair.Target];
            int count = tgt.Triangles.Count;
            var table = src.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            var crossings = CrossingsOf(src, pair.Target);
            row["status"] = "ok";
            row["sourceCell"] = Cell(set.CellOf[pair.Source]);
            row["sourcePlugin"] = provider[pair.Source];
            row["sourceTriangles"] = src.Triangles.Count;
            row["targetCell"] = Cell(set.CellOf[pair.Target]);
            row["targetPlugin"] = provider[pair.Target];
            row["targetTriangles"] = count;
            row["tableEntries"] = table.Count(e => e.Mesh == pair.Target);
            row["tableBeyond"] = table.Count(e => e.Mesh == pair.Target && e.TriangleIndex >= count);
            row["crossings"] = crossings.Select(c => new Dictionary<string, object?>
            {
                ["tri"] = c.Tri, ["edge"] = c.Edge, ["link"] = c.Link, ["targetTri"] = c.TargetTri,
                ["beyond"] = c.TargetTri >= count,
                ["a"] = new[] { R(c.A.X), R(c.A.Y), R(c.A.Z) }, ["b"] = new[] { R(c.B.X), R(c.B.Y), R(c.B.Z) },
            }).ToList();
            yield return row;
        }
    }

    static double R(float v) => Math.Round(v, 2);

    Dictionary<string, object?> Cell(FormKey cell)
    {
        var info = cells.GetValueOrDefault(cell);
        return new() { ["formKey"] = cell.ToString(), ["editorId"] = info?.EditorId, ["interior"] = info?.Interior,
                       ["grid"] = info?.Grid is { } g ? new[] { g.X, g.Y } : null };
    }

    public void Print(string? configuration)
    {
        foreach (var row in Answers())
        {
            row["configuration"] = configuration;
            Console.WriteLine("   nav-link " + JsonSerializer.Serialize(row));
        }
    }

    // Known answers on the production functions, from synthetic records.
    public static int Smoke()
    {
        FormKey K(uint id) => new(ModKey.FromNameAndExtension("Fixture.esm"), id);
        var ws = K(1); var cellS = K(2); var cellT = K(3); var cellT2 = K(4);
        var s = K(10); var t = K(11); var other = K(12);
        var v = new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 1), new Vec3(0, 100, 2), new Vec3(100, 100, 3) };
        // tri 0: edge 01 -> link 0 (t, 9), edge 12 -> link 1 (other, 0); tri 1: edge 20 -> link 2 (t, 4) with its bit;
        // tri 2: edge 12 holds 0 WITHOUT bit 0x02 (an in-mesh neighbour, not link 0)
        var tris = new[] {
            new NavTri(0, 1, 2, 0x01 | 0x02, 0, 1, -1),
            new NavTri(1, 3, 2, 0x04, -1, -1, 2),
            new NavTri(0, 1, 3, 0, -1, 0, -1) };
        var links = new[] { new NavEdgeLink(t, 9), new NavEdgeLink(other, 0), new NavEdgeLink(t, 4), new NavEdgeLink(t, 12) };
        var src = new NavmeshGeometry(s, v, tris, default, default, links);
        NavmeshGeometry Tgt(int n) => new(t, v, Enumerable.Range(0, n).Select(_ => new NavTri(0, 1, 2, 0)).ToArray(), default, default, Array.Empty<NavEdgeLink>());
        CellPlacements Cell(FormKey key, int x, params NavmeshGeometry[] navs) => new(key, null, false, ws, (x, 0), Array.Empty<PlacedRef>(), navs);
        int tests = 0, failed = 0;
        void Check(bool ok, string label, string got)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + label + (ok ? "" : $" (got {got})"));
            if (ok) tests++; else failed++;
        }

        var c = CrossingsOf(src, t);
        Check(c.Count == 2, "two crossings into the target: the other navmesh's link and the bit-less field are not crossings", c.Count.ToString());
        var e01 = c.FirstOrDefault(x => x.Edge == "01");
        Check(e01.Tri == 0 && e01.A == v[0] && e01.B == v[1] && e01.TargetTri == 9, "edge 01 runs from corner A to corner B", $"{e01}");
        var e20 = c.FirstOrDefault(x => x.Edge == "20");
        Check(e20.Tri == 1 && e20.A == v[2] && e20.B == v[1] && e20.TargetTri == 4, "edge 20 runs from corner C to corner A", $"{e20}");
        Check(CrossingsOf(src, other).Count == 1 && CrossingsOf(src, other)[0].Edge == "12", "edge 12 is read through bit 0x02", CrossingsOf(src, other).Count.ToString());

        var q = new NavmeshLinks(new[] { $"{s}={t}" });
        q.Observe(Cell(cellS, 0, src), "Fixture.esm");
        q.Observe(Cell(cellT, 1, Tgt(20)), "Fixture.esm");
        q.Observe(Cell(cellT2, 2, Tgt(9)), "Later.esp");      // a later plugin moves the target and cuts it to 9 triangles
        var row = q.Answers().Single();
        var rc = (List<Dictionary<string, object?>>?)row.GetValueOrDefault("crossings") ?? new();
        string tc = row.GetValueOrDefault("targetCell") is Dictionary<string, object?> d ? (string?)d["formKey"] ?? "" : "";
        Check(tc == cellT2.ToString() && (string?)row.GetValueOrDefault("targetPlugin") == "Later.esp",
              "the target's cell and provider are its LAST sighting", $"{tc} {row.GetValueOrDefault("targetPlugin")}");
        Check(row.GetValueOrDefault("targetTriangles") is 9, "the target's count is its winning version's", $"{row.GetValueOrDefault("targetTriangles")}");
        bool b9 = rc.Any(x => (int)x["targetTri"]! == 9 && (bool)x["beyond"]!), b4 = rc.Any(x => (int)x["targetTri"]! == 4 && !(bool)x["beyond"]!);
        Check(b9 && b4, "triangle 9 of 9 is beyond (no off-by-one); 4 of 9 is within", string.Join(",", rc.Select(x => $"{x["targetTri"]}:{x["beyond"]}")));
        Check(row.GetValueOrDefault("tableEntries") is 3 && row.GetValueOrDefault("tableBeyond") is 2,
              "the table counts every entry naming the target, used by an edge or not", $"{row.GetValueOrDefault("tableEntries")}/{row.GetValueOrDefault("tableBeyond")}");

        var gone = new NavmeshLinks(new[] { $"{s}={t}" });
        gone.Observe(Cell(cellS, 0, src), "Fixture.esm");
        gone.Observe(Cell(cellT, 1, Tgt(20)), "Fixture.esm");
        gone.Observe(Cell(cellT, 1, Tgt(20) with { Deleted = true }), "Later.esp");
        var g = gone.Answers().Single();
        Check((string?)g["status"] == "refused" && ((string?)g.GetValueOrDefault("reason") ?? "").Contains("target"),
              "a target deleted by a later plugin is refused, not answered from its earlier version", $"{g["status"]}");
        var unwalked = new NavmeshLinks(new[] { $"{s}={t}" }).Answers().Single();
        Check((string?)unwalked["status"] == "refused", "no walk, no answer", $"{unwalked["status"]}");
        bool threw = false;
        try { _ = new NavmeshLinks(new[] { $"{s}" }); } catch (ArgumentException) { threw = true; }
        Check(threw, "a malformed pair is refused", threw.ToString());

        Console.WriteLine($"{tests} navmesh link checks passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
