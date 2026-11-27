using System.Text.Json;
using HarnessShared;
using SkyWright.Engine;

/// <summary>The mod a plugin of the game's Data folder belongs to, by its file name cut at " - " and "." (houseCARL lane S's
/// family rule): "SfA - Extra.esp" and "SfA.esp" are one mod. A plugin in an MO2 mod folder is that folder instead.</summary>
static class StrandFamily
{
    public static string ByName(string plugin) => plugin.Split(" - ")[0].Split('.')[0].ToLowerInvariant();
}

/// <summary>The conflict file and its sidecar.</summary>
static class StrandEmit
{
    public const string Kind = "STRANDED";

    static PhysicalState Physical(StrandedState s) => s == StrandedState.Reported ? PhysicalState.Holds : PhysicalState.Unmeasured;

    static string Summary(ScanRow r) => r.Verdict.State == StrandedState.Reported
        ? $"{r.BaseType ?? "object"} {r.BaseEdid} from {r.Placer} in {r.CellEdid ?? r.Cell.ToString()}: {r.Winning:0.0} u from walkable ground with {r.NavPlugin}'s navmesh, {r.Own:0.0} u with the navmesh its mod was made with; {r.Verdict.Class}"
        : $"{r.BaseType ?? "object"} {r.BaseEdid} from {r.Placer} in {r.CellEdid ?? r.Cell.ToString()}: not judged, {r.Verdict.Reason}";

    /// <summary>Each row of <paramref name="baseFile"/> re-measured under this order: Holds (reported here), Unmeasured (not
    /// judged here), Gone (no row here: within reach, the same navmesh, or not in this order at all).</summary>
    public static List<Predicate> Predicates(ConflictFile baseFile, ScanResult here)
    {
        var mine = here.Rows.ToDictionary(r => r.Ref.ToString(), r => r, StringComparer.OrdinalIgnoreCase);
        var list = new List<Predicate>();
        foreach (var c in baseFile.Conflicts.Where(c => c.Kind == Kind))
            foreach (var n in c.Necessary)
            {
                var (state, detail) = mine.TryGetValue(n.FormKey, out var r)
                    ? (Physical(r.Verdict.State), r.Verdict.State == StrandedState.Reported ? "stranded under this order too" : "not judged under this order: " + r.Verdict.Reason)
                    : (PhysicalState.Gone, "no stranded row under this order");
                list.Add(new Predicate(Kind, c.A, c.B, n.FormKey, null, state, detail));
            }
        return list;
    }

    public static ConflictFile File(ScanResult res, ConflictFile? baseFile, string? basePath, string instance, ConfigurationIdentity? identity, bool mirror)
    {
        var conflicts = res.Rows.Select(r => new Conflict(Kind, r.Placer, r.NavPlugin ?? "", Summary(r),
            new[] { new ConflictRecord(r.Ref.ToString(), "object", r.CellEdid, Physical(r.Verdict.State),
                    r.Verdict.State == StrandedState.Reported ? $"{r.Winning:0.0} u from walkable ground, {r.Own:0.0} u with the navmesh its mod was made with, {r.Clearance:0.0} u from another mod's object" : r.Verdict.Reason) })).ToList();
        // judged: every object of the population the rule reached a verdict on (a row reported, or no row with its reason);
        // unjudgeable: the rows not judged, by reason; set aside: what is outside the population by definition
        var notJudged = res.Rows.Where(r => r.Verdict.State == StrandedState.NotJudged).GroupBy(r => StrandedRule.ReasonKey(r.Verdict.Reason))
                           .ToDictionary(g => g.Key, g => (long)g.Count());
        var outside = new HashSet<string> { StrandedRule.Outdoors, StrandedRule.DisabledAtLoad, StrandedRule.EditorMarker };
        var noRow = res.Coverage.Where(kv => kv.Key != "reported" && !kv.Key.StartsWith("not judged", StringComparison.Ordinal) && !outside.Contains(kv.Key));
        var coverage = new List<CoverageLine>
        {
            new("interior objects there at load (stranded)", res.Population - notJudged.Values.Sum(), notJudged,
                res.Coverage.Where(kv => outside.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                $"population {res.Population}; reported {res.Rows.Count(r => r.Verdict.State == StrandedState.Reported)}; measured after the identity cut {res.LeftByCut}; no row: "
                + string.Join("; ", noRow.Select(kv => $"{kv.Key} {kv.Value}")) + (mirror ? "" : "; instrument run: --no-mirror")),
        };
        return new ConflictFile("strandprobe", instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage,
                                baseFile is null ? null : Predicates(baseFile, res), basePath, "load-time", "baseline", identity);
    }

    static float[][]? Box(Aabb? b) => b is { IsEmpty: false } x ? new[] { new[] { x.Min.X, x.Min.Y, x.Min.Z }, new[] { x.Max.X, x.Max.Y, x.Max.Z } } : null;

    public static void Write(string path, ScanResult res, ConflictFile? baseFile, string? basePath, string instance, ConfigurationIdentity? identity, bool mirror)
    {
        ConflictIo.Write(path, File(res, baseFile, basePath, instance, identity, mirror));
        var rows = res.Rows.Select(r => new
        {
            id = r.Ref.ToString(), cell = r.CellEdid ?? r.Cell.ToString(), placer = r.Placer, navmesh = r.NavPlugin, baseType = r.BaseType, baseEdid = r.BaseEdid,
            geometry = r.Geometry, trigger = r.Trigger, winning = r.Winning, own = r.Own, clearance = r.Clearance, nearest = r.Nearest,
            state = r.Verdict.State.ToString(), reason = r.Verdict.Reason, cls = r.Verdict.Class,
            // the nearest walkable point (the navmesh side of the closest pair) with the navmesh that wins and with its own
            winningNear = r.WinningNear, ownNear = r.OwnNear,
            subjectBox = Box(r.SubjectBox), nearestBox = Box(r.NearestBox), nearestTriangles = Box(r.NearestTriangles), nearestRegion = Box(r.NearestRegion),
        });
        System.IO.File.WriteAllText(path + ".reach.json", JsonSerializer.Serialize(new
        {
            rule = new { StrandedRule.Reach, StrandedRule.Tolerance, band = new[] { StrandedRule.BandLo, StrandedRule.BandHi }, mirror },
            population = res.Population, leftByCut = res.LeftByCut, measured = res.Measured, coverage = res.Coverage, rows,
            // an instrument run over named cells (--cells) also lists every object without a row, with its reason
            noRows = res.All.Where(kv => kv.Value.State == StrandedState.NoRow).OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                            .Select(kv => new { id = kv.Key.ToString(), reason = kv.Value.Reason }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
