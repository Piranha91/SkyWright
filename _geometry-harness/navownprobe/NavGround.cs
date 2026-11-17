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
    public sealed record Row(FormKey Nav, int Owner, int LandWinner, int Counted, int Vertices, float MaxMove, FormKey Cell);
    public float Band { get; init; } = 16f;
    public float Move { get; init; } = 32f;
    public int MinVertices { get; init; } = 3;
    public readonly List<Row> Rows = new();
    public long VerticesJudged, CannotTell, NoAuthoredGround, NoLand;
    /// <summary>For the vanilla floor: per judged vertex on an unchanged cell, its height above the author's ground.</summary>
    public readonly List<float> OnOwnGround = new();

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
            int counted = 0, n = 0; float maxMove = 0;
            var winners = new Dictionary<int, int>();
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
                if (sa > band || sw > band) { r.CannotTell++; continue; }
                n++; r.VerticesJudged++;
                bool same = ReferenceEquals(authored, winning) || authored.SameHeightsAs(winning);
                if (census && same) r.OnOwnGround.Add(v.Z - ha.Value);
                if (same) continue;
                float moved = MathF.Abs(hw.Value - ha.Value);
                if (MathF.Abs(v.Z - ha.Value) <= band && moved > move && MathF.Abs(v.Z - hw.Value) > band)
                {
                    counted++; maxMove = Math.Max(maxMove, moved);
                    winners[winning.Plugin] = winners.GetValueOrDefault(winning.Plugin) + 1;
                }
            }
            if (counted >= minVertices)
                r.Rows.Add(new Row(key, owner, winners.OrderByDescending(kv => kv.Value).First().Key, counted, n, maxMove, w.Cell));
        }
        return r;
    }

    static FormKey WorldOf(WinningNav w) => w.Space.StartsWith("wrld:") ? FormKey.Factory(w.Space[5..]) : FormKey.Null;

    public PhysicalState Predicate(FormKey nav) => Rows.Any(x => x.Nav == nav) ? PhysicalState.Holds : PhysicalState.Gone;
}
