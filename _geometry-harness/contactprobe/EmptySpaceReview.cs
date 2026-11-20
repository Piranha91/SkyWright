using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --empty-space: LEFT IN EMPTY SPACE over the moved hosts of this order (and --state, when given). Every
/// dependent-scale reference within reach of a moved host's previous box is judged against that host's mover
/// (<see cref="EmptySpaceSearch.Judge"/>); the rows go to the --empty-space file, the class to --emit as EMPTY-SPACE.</summary>
static class EmptySpaceReview
{
    public static int Run(ContactIndex cidx, IReadOnlyList<HashSet<int>> closure, List<MovedHost> hosts,
                          Func<int, string> pluginName, Func<FormKey, string> baseLabel, Func<FormKey, string> cellOf,
                          Func<int, bool> isFloorPlugin, Func<string, bool> isBethesdaFile, Func<FormKey, string, bool?> nodeCheck,
                          string outPath, string? emitPath, string? basePath, string instance, int pluginsRead, int pluginsUnreadable,
                          ConfigurationIdentity identity, int listN, Stopwatch sw,
                          bool radius, bool bos, IReadOnlyList<string> forceEnable,
                          PopulationScope? population = null, string? auditEmit = null, string? auditOut = null)
    {
        var search = new EmptySpaceSearch(cidx, closure, pluginName, nodeCheck, isBethesdaFile, isFloorPlugin);
        // --population (window 2, task 1): the population's interior cells, closed over relocations and rack links.
        EmptySpaceScope? scope = null;
        if (population is not null)
        {
            scope = new EmptySpaceScope(cidx.Index, population.InteriorClosure(cidx.Index, search.RackPartners));
            Console.WriteLine($"   population scope: {scope.Cells.Count:N0} interior cells (the population's {population.Describe()})");
        }
        long outsidePairs = 0, judgedDropped = 0;
        int moverIndex(string name) { for (int p = 0; p < closure.Count; p++) if (string.Equals(pluginName(p), name, StringComparison.OrdinalIgnoreCase)) return p; return -1; }
        Console.WriteLine($"\n==== EMPTY SPACE — fixtures whose assembly reached a floor or wall a mover removed or moved ====");
        Console.WriteLine($"   anchors: Static (or Fixed MoveableStatic) with solid collision, at least {search.MinAnchorDim:0}u across, not a marker, not a reference "
                        + $"the fixture's own plugin defines (a Bethesda master's always count); touch within ε {cidx.Epsilon:0.#}u on the core surfaces (collision, else render, labelled)");
        var rows = new List<EmptySpaceRow>();
        long absent = 0, noRelation = 0;
        long seen = 0;
        foreach (var (f, m) in search.CandidatesNear(hosts))
        {
            if (++seen % 5000 == 0) Console.WriteLine($"   ... {seen:N0} pairs, {rows.Count:N0} rows, touch tests {search.TouchTests:N0}, {sw.Elapsed.TotalSeconds:0}s");
            if (scope is not null && !scope.MustJudge(f)) { outsidePairs++; continue; }
            var r = search.Judge(f, m);
            if (scope is not null && !scope.InScope(f)) { judgedDropped++; continue; }
            if (r.Verdict == EmptySpaceVerdict.Absent) { absent++; continue; }
            if (r.Verdict == EmptySpaceVerdict.NoRelation) noRelation++;
            rows.Add(r);
        }
        if (scope is not null)
            Console.WriteLine($"   population: {outsidePairs:N0} pairs outside it not judged; {judgedDropped:N0} judged only to keep the exterior memos exact, not reported");
        Console.WriteLine($"   pairs judged: {rows.Count + absent:N0} (absent in an arm {absent:N0}, not listed); touch tests {search.TouchTests:N0}, surfaces built {search.SurfacesBuilt:N0}, walks {search.WalksRun:N0} (reused {search.WalksReused:N0}); exterior hosts not searched (the land is not read) {search.ExteriorHostsSkipped:N0}  ({sw.Elapsed.TotalSeconds:0.0}s)");
        foreach (var g in rows.GroupBy(r => r.Verdict).OrderBy(g => g.Key))
            Console.WriteLine($"      {g.Key,-20} {g.Count(),8:N0}   distinct fixtures {g.Select(r => r.Fixture).Distinct().Count(),7:N0}");
        var cls = rows.Where(r => r.Verdict == EmptySpaceVerdict.LeftInEmptySpace).ToList();
        Console.WriteLine($"\n   ---- the class: LEFT IN EMPTY SPACE, {cls.Count:N0} rows over {cls.Select(r => r.Fixture).Distinct().Count():N0} fixtures; by (mover, side) ----");
        foreach (var g in cls.GroupBy(r => (pluginName(r.Mover), r.Fixture.ModKey.FileName.String)).OrderByDescending(g => g.Count()).Take(listN))
            Console.WriteLine($"   {g.Count(),6:N0}  {g.Key.Item1} removed or moved what fixtures of {g.Key.Item2} stood on");
        foreach (var r in cls.OrderBy(r => r.Cell.ToString()).ThenBy(r => r.Fixture.ToString()).Take(listN))
            Console.WriteLine($"   {r.Fixture} {baseLabel(r.Base)} ({r.HeldBy}) in {cellOf(r.Cell)}: assembly {r.Winner.Members}, mover {pluginName(r.Mover)}, "
                            + $"changed anchors {string.Join(" ", r.Authoring.ChangedAnchors.Take(3))}; grounded at 32u {r.GroundedAt32}, 128u {r.GroundedAt128}, 256u {r.GroundedAt256}");

        var opts = new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string sideText = JsonSerializer.Serialize(new
        {
            Schema = "housecarl.empty-space/1", State = cidx.State?.Name ?? EnablePopulation.BaselineStateName,
            Parameters = new { Epsilon = cidx.Epsilon, search.MinAnchorDim, search.MaxMembers, search.AnchorReport, Reach = search.Reach },
            Counts = rows.GroupBy(r => r.Verdict.ToString()).ToDictionary(g => g.Key, g => g.Count()), Absent = absent,
            Rows = rows.Where(r => r.Verdict != EmptySpaceVerdict.NoRelation).Select(r => EmptySpaceOutput.Row(r, pluginName, baseLabel, cellOf)).ToList(),
        }, opts);
        if (population is not null)
        {
            // a scoped side file names its population (an unscoped one keeps its shape byte for byte)
            var node = System.Text.Json.Nodes.JsonNode.Parse(sideText)!.AsObject();
            node["Population"] = population.Identity("load-time");
            sideText = node.ToJsonString(opts);
        }
        File.WriteAllText(outPath, cidx.Arms.StampForced(sideText));
        Console.WriteLine($"wrote {outPath} (no-relation rows counted, not listed: {noRelation:N0})");

        if (emitPath is not null)
        {
            var conflicts = cls.Select(r => EmptySpaceOutput.ToConflict(r, pluginName, baseLabel, cellOf, state: false)).ToList();
            List<Predicate>? predicates = null;
            if (basePath is not null)
            {
                predicates = new();
                foreach (var c in ConflictIo.Read(basePath).Conflicts)
                {
                    string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
                    foreach (var rec in c.Necessary)
                    {
                        if (c.Kind != EmptySpaceOutput.Kind) { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not an empty-space row")); continue; }
                        var (st, why) = search.Predicate(FormKey.Factory(rec.FormKey), moverIndex(c.B));
                        predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
                    }
                }
            }
            var coverage = new List<CoverageLine>
            {
                new("plugins", pluginsRead, new Dictionary<string, long> { ["UnreadablePlugin"] = pluginsUnreadable }),
                new("fixture-mover pairs", rows.Count(r => r.Verdict != EmptySpaceVerdict.Refused),
                    rows.Where(r => r.Verdict == EmptySpaceVerdict.Refused).GroupBy(r => r.Why.StartsWith("exterior") ? "Exterior" : "AssemblyOverCap")
                        .ToDictionary(g => g.Key, g => (long)g.Count()),
                    new Dictionary<string, long> { ["AbsentInArm"] = absent, ["Falls"] = rows.Count(r => r.Verdict == EmptySpaceVerdict.Falls),
                                                   ["FloatsAsShipped"] = rows.Count(r => r.Verdict == EmptySpaceVerdict.FloatsAsShipped),
                                                   ["FloorPopulation"] = rows.Count(r => r.Verdict == EmptySpaceVerdict.FloorPopulation) },
                    $"class {cls.Count:N0}"),
                new("mesh responsibility", 0, new Dictionary<string, long>(), new Dictionary<string, long>(),
                    "not applied: --empty-space judges fixtures with the plugin rule, so no mod folder's mesh is named as the cause of a row here"),
            };
            if (scope is not null)
                coverage.Add(new("population scope", rows.Count + absent, new Dictionary<string, long>(),
                    new Dictionary<string, long> { ["PairOutsidePopulation"] = outsidePairs, ["JudgedForExteriorMemo"] = judgedDropped },
                    $"fixtures judged only in the population's {scope.Cells.Count:N0} interior cells (closed over relocations and rack links); {population!.Describe()}"));
            string policy = ContactCredit.EmptySpacePolicy(cidx.Epsilon, search.Reach, search.MinAnchorDim, search.MaxMembers, search.AnchorReport, radius, bos, forceEnable);
            ConflictIo.Write(emitPath, new ConflictFile("contactprobe", instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                        population?.Identity("load-time") ?? "load-time", cidx.State?.Name ?? EnablePopulation.BaselineStateName, identity, MeasurementPolicy: policy));
            Console.WriteLine($"measurement policy: {policy}");
            Console.WriteLine($"emitted {conflicts.Count:N0} EMPTY-SPACE conflicts -> {emitPath}");
        }
        if (scope is not null && auditEmit is not null)
            EmptySpaceAudit.Write(auditEmit, auditOut!, "contactprobe", population!.Identity("load-time"), (_, f) => scope.InScope(f) ? "empty-space" : null);
        Console.WriteLine("Limits: interiors only (the land is not read); the anchor gate is a stated parameter: another plugin's Static at anchor size that itself");
        Console.WriteLine("        floats grounds what touches it; a render-only floor is no anchor; runtime existence is the records' enable chain (R1).");
        Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
}
