using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// NAVMESH LEFT ON GROUND ANOTHER PLUGIN MOVED (NAV-GROUND; task 2026-11-16-navmesh-repair, repair class 3). The measure is the
// navmesh author's ground against the winning ground, never the navmesh against the ground: navmesh also lies on decks,
// bridges and floors, which no LAND describes. Per exterior winning navmesh, its owner is the plugin whose version wins; the
// author's ground at each vertex is the LAND the owner or its masters last stated for the vertex's cell
// (CellBoundaryIndex.LandAsSeenBy, the authoring view contactprobe --terrain and seamprobe use); the winning ground is the
// cell's last LAND. A vertex COUNTS when it lay on the author's ground (within Band) and the winning ground there moved more
// than Move away from it, so the vertex is now off the winning ground by more than Band. A navmesh with at least MinVertices
// counted vertices is a row. Heights are NavmeshFloor.LandHeightAt's bilinear reading; where its saddle bound (the two
// possible diagonals) exceeds Band the vertex cannot be told and is counted apart, never judged.
sealed class NavGround
{
    /// <summary>A row; <paramref name="Facts"/> are its used vertices' author's and winning ground, as judged (the repair
    /// plan's input, NavRepair.Ground).</summary>
    /// <paramref name="Movers"/> is every plugin whose winning LAND moved counted vertices, most first (the first is
    /// <paramref name="LandWinner"/>); null in rows built by hand.</summary>
    public sealed record Row(FormKey Nav, int Owner, int LandWinner, int Counted, int Vertices, float MaxMove, FormKey Cell,
                             IReadOnlyList<GroundVertex>? Facts = null, IReadOnlyList<(int Plugin, int Counted)>? Movers = null);
    public float Band { get; init; } = 16f;
    public float Move { get; init; } = 32f;
    public int MinVertices { get; init; } = 3;
    public readonly List<Row> Rows = new();
    public long VerticesJudged, CannotTell, NoAuthoredGround, NoLand;
    /// <summary>For the vanilla floor: per judged vertex on an unchanged cell, its navmesh and its height above the author's
    /// ground; and per navmesh, its vertices judged and those that could not be told (so the floor can be split by navmesh).</summary>
    public readonly List<(FormKey Nav, float Height)> OnOwnGround = new();
    public readonly Dictionary<FormKey, (int Judged, int CannotTell)> PerNav = new();

    /// <summary>--ground-tolerance BAND,MOVE in game units.</summary>
    public static (float Band, float Move) Parse(string s)
    {
        var p = s.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (p.Length != 2 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var a)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var b) || a < 0 || b < 0)
            throw new ArgumentException($"--ground-tolerance '{s}' is not BAND,MOVE in game units (two numbers, for example 16,32).");
        return (a, b);
    }

    public static NavGround Find(IReadOnlyDictionary<FormKey, (WinningNav W, int Owner)> nav, CellBoundaryIndex land,
                                 IReadOnlyList<HashSet<int>> closure, float band, float move, int minVertices, bool census)
    {
        var r = new NavGround { Band = band, Move = move, MinVertices = minVertices };
        foreach (var (key, (w, owner)) in nav)
        {
            if (w.Grid is null || owner < 0) continue;
            var ws = w.Geom.Triangles.Count == 0 ? default : WorldOf(w);
            if (ws.IsNull) continue;
            var used = w.Geom.Triangles.SelectMany(t => new[] { (int)t.A, t.B, t.C }).Where(i => i >= 0 && i < w.Geom.Vertices.Count).Distinct();
            int counted = 0, n = 0, unclear = 0; float maxMove = 0;
            var winners = new Dictionary<int, int>();
            var facts = new List<GroundVertex>();
            foreach (var i in used)
            {
                var v = w.Geom.Vertices[i];
                int gx = (int)MathF.Floor(v.X / 4096f), gy = (int)MathF.Floor(v.Y / 4096f);
                var cell = land.Single(ws, gx, gy);
                if (cell is null || cell.Land.Count == 0 || cell.LandWinnerDeclaresNone) { r.NoLand++; continue; }
                var authored = CellBoundaryIndex.LandAsSeenBy(cell.Land, owner, closure);
                if (authored is null) { r.NoAuthoredGround++; continue; }
                var winning = cell.Land[^1];
                var ha = NavmeshFloor.LandHeightAt(authored.Heights, gx, gy, v.X, v.Y, out var sa);
                var hw = NavmeshFloor.LandHeightAt(winning.Heights, gx, gy, v.X, v.Y, out var sw);
                if (ha is null || hw is null) { r.NoLand++; continue; }
                if (sa > band || sw > band) { r.CannotTell++; unclear++; facts.Add(new GroundVertex(i, float.NaN, float.NaN, true)); continue; }
                facts.Add(new GroundVertex(i, ha.Value, hw.Value, false));
                n++; r.VerticesJudged++;
                bool same = ReferenceEquals(authored, winning) || authored.SameHeightsAs(winning);
                if (census && same) r.OnOwnGround.Add((key, v.Z - ha.Value));
                if (same) continue;
                float moved = MathF.Abs(hw.Value - ha.Value);
                if (MathF.Abs(v.Z - ha.Value) <= band && moved > move && MathF.Abs(v.Z - hw.Value) > band)
                {
                    counted++; maxMove = Math.Max(maxMove, moved);
                    winners[winning.Plugin] = winners.GetValueOrDefault(winning.Plugin) + 1;
                }
            }
            if (census && n + unclear > 0) r.PerNav[key] = (n, unclear);
            if (counted >= minVertices)
            {
                var movers = winners.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
                r.Rows.Add(new Row(key, owner, movers[0].Key, counted, n, maxMove, w.Cell, facts, movers));
            }
        }
        return r;
    }

    /// <summary>Which ground mover's own version of a row's navmesh is the repair's source instead of following the ground, or
    /// null (follow the ground). Movers are tried most first; one qualifies when it holds a version of the navmesh
    /// (<paramref name="holders"/>), is not the owner, did not delete it, and that version is its own re-cut: it has triangles,
    /// its content hash differs from the winner's, and its vertices differ from the winner's (a different count, or some
    /// vertex more than <paramref name="band"/> away from the winner's at the same index; a hash that differs by flags alone,
    /// such as edge-link bits joining new neighbours, leaves the vertices where the winner's are, on the old ground).
    /// The mover authored that version on the ground it moved, so it is never a NAV-GROUND row itself (this detector measures
    /// the author's ground against the winning ground, never navmesh against LAND: navmesh lies on streets and floors too).
    /// <c>Others</c> are the remaining movers, whose ground that version may not fit.</summary>
    public static (int Mover, IReadOnlyList<int> Others)? ChooseMover(int owner, IReadOnlyList<(int Plugin, int Counted)> movers,
        IReadOnlyCollection<int> holders, Func<int, (int V, int T, int H)> size, Func<int, bool> deleted,
        Func<int, NavmeshGeometry?> version, NavmeshGeometry winner, float band)
    {
        var ws = size(owner);
        foreach (var (m, _) in movers)
        {
            if (m == owner || !holders.Contains(m) || deleted(m)) continue;
            var ms = size(m);
            if (ms.T == 0 || ms.H == ws.H) continue;
            if (version(m) is not { } g || VerticesAlike(g, winner, band)) continue;
            return (m, movers.Select(x => x.Plugin).Where(x => x != m).ToList());
        }
        return null;
    }

    static bool VerticesAlike(NavmeshGeometry a, NavmeshGeometry b, float band)
    {
        if (a.Vertices.Count != b.Vertices.Count) return false;
        for (int i = 0; i < a.Vertices.Count; i++)
        {
            var p = a.Vertices[i]; var q = b.Vertices[i];
            if (MathF.Abs(p.X - q.X) > band || MathF.Abs(p.Y - q.Y) > band || MathF.Abs(p.Z - q.Z) > band) return false;
        }
        return true;
    }

    static FormKey WorldOf(WinningNav w) => w.Space.StartsWith("wrld:") ? FormKey.Factory(w.Space[5..]) : FormKey.Null;

    public PhysicalState Predicate(FormKey nav) => Rows.Any(x => x.Nav == nav) ? PhysicalState.Holds : PhysicalState.Gone;
}
