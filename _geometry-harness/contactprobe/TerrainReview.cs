using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// TERRAIN SUPPORT — contactprobe --terrain (Research/TERRAIN-SUPPORT-DESIGN-2026-10-18.md; _shared/TerrainContactIndex.cs).
// A Fixed exterior reference that rested on the ground in its placement owner's view floats over, or is buried in, the winning
// ground, which a plugin the owner could not see supplied. Printed in order: the LAND plumbing, the absolute floor (with
// --absolute), the Inherited floor (Bethesda on Bethesda), then the class. Only Introduced Blind Fixed rows of the Mod population
// that float or are buried reach the emit; everything else is counted with one line saying what was set aside.
static class TerrainReview
{
    public const string Population = "exterior placed references resting on terrain as authored";
    public const string Kind = "TERRAIN";
    static readonly float[] Columns = { 8f, 32f, 128f };
    static readonly float[] AbsoluteColumns = { 4f, 8f, 32f, 96f };

    public static int Run(ContactIndex cidx, CellBoundaryIndex land, IReadOnlyList<HashSet<int>> closure, Func<int, string> pluginName,
                          Func<FormKey, string> baseLabel, float eps, float floatThreshold, float burialThreshold, bool absolute, int sample,
                          string? emitPath, string? basePath, string? jsonPath, string instance, ConfigurationIdentity identity, int listN, Stopwatch sw)
    {
        var tci = new TerrainContactIndex(cidx, land, closure) { Epsilon = eps, FloatThreshold = floatThreshold, BurialThreshold = burialThreshold };
        tci.Progress = (pass, n) => Console.WriteLine($"   ... {pass}: {n:N0} references, {sw.Elapsed.TotalSeconds:0}s");
        string Name(int p) => pluginName(p);
        bool Beth(int p) => PluginKinds.IsBethesda(Name(p));
        bool Cc(int p) => Name(p).StartsWith("cc", StringComparison.OrdinalIgnoreCase);
        static float Pct(long a, long b) => b == 0 ? 0f : 100f * a / b;

        // ---- 1. LAND plumbing ----------------------------------------------------------------------------------------------
        Console.WriteLine($"\n==== TERRAIN SUPPORT — 1. LAND PLUMBING ====");
        var ext = land.Cells.Values.Where(c => !c.Interior && c.GX != int.MinValue).ToList();
        int withStatement = ext.Count(c => c.LandStatements.Count > 0), twoPlus = ext.Count(c => c.LandStatements.Count >= 2);
        Console.WriteLine($"   exterior cells {ext.Count:N0}; with a LAND statement {withStatement:N0}; with 2+ statements {twoPlus:N0} (only these can hold a row)");
        var heightSteps = new Dictionary<string, int>(); var heightStepsWs = new Dictionary<(string Ws, string Plugin), int>();
        foreach (var c in ext)
            for (int i = 1; i < c.Land.Count; i++)
                if (!c.Land[i].SameHeightsAs(c.Land[i - 1]))
                {
                    string pn = Name(c.Land[i].Plugin);
                    heightSteps[pn] = heightSteps.GetValueOrDefault(pn) + 1;
                    string ws = land.WorldEditorId.TryGetValue(c.Worldspace, out var we) && we is not null ? we : c.Worldspace.ToString();
                    heightStepsWs[(ws, pn)] = heightStepsWs.GetValueOrDefault((ws, pn)) + 1;
                }
        Console.WriteLine($"   cells whose heights a later plugin CHANGED (not restated), by plugin: " +
                          string.Join(", ", heightSteps.OrderByDescending(k => k.Value).Take(Math.Max(listN, 12)).Select(k => $"{k.Key} {k.Value:N0}")));
        Console.WriteLine($"   …of those by Bethesda-family plugins, by worldspace: " +
                          string.Join(", ", heightStepsWs.Where(k => PluginKinds.IsBethesda(k.Key.Plugin)).OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key.Ws}/{k.Key.Plugin} {k.Value:N0}")));
        var none = ext.SelectMany(c => c.LandStatements).Where(s => s.Kind != LandHeight.LandKind.Heights).GroupBy(s => (Name(s.Plugin), s.Kind))
                      .Select(g => $"{g.Key.Item1} {g.Key.Kind} {g.Count():N0}").ToList();
        Console.WriteLine($"   LAND statements without heights (no ground / unreadable), by plugin: {(none.Count == 0 ? "none" : string.Join(", ", none))}");
        Console.WriteLine($"   thresholds: ε {eps:0.#} u (contact shell); float {floatThreshold:0.#} u; burial {burialThreshold:0.#} u (frozen in the run plan)  ({sw.Elapsed.TotalSeconds:0.0}s)");

        // ---- 2. the absolute floor ----------------------------------------------------------------------------------------------
        if (absolute)
        {
            Console.WriteLine($"\n==== 2. THE ABSOLUTE FLOOR — every exterior placed reference with a body against the winning ground and the winning arm's supports, as shipped ====");
            Func<FormKey, bool>? inSample = sample > 1 ? k => (uint)HashCode.Combine(k.ID, k.ModKey.FileName.String.ToLowerInvariant().GetHashCode()) % (uint)sample == 0 : null;
            if (sample > 1) Console.WriteLine($"   SAMPLE: one reference in {sample} by FormKey hash (deterministic); the rates below are the sample's");
            var abs = tci.JudgeAbsolute(inSample);
            foreach (var kv in tci.Counts.Where(k => k.Key.StartsWith("absolute")).OrderBy(k => k.Key)) Console.WriteLine($"   {kv.Key,-48} {kv.Value,10:N0}");
            foreach (var motion in new[] { DependentMotion.Fixed, DependentMotion.Movable, DependentMotion.Unknown })
                foreach (var src in new[] { 'c', 'r' })
                {
                    var set = abs.Where(r => r.Motion == motion && r.BodySrc == src).ToList();
                    if (set.Count == 0) continue;
                    var line = AbsoluteColumns.Select(col =>
                    {
                        long fl = set.Count(r => r.Facts is { } f && Floating(f, eps, col));
                        long below = set.Count(r => r.Facts is { } f && MathF.Max(f.D0.WinningClearance, f.D1.WinningClearance) < -col && !f.OnReference);
                        return $"≥{col:0}u: floating {fl:N0} ({Pct(fl, set.Count):0.0}%), below ground {below:N0} ({Pct(below, set.Count):0.0}%)";
                    });
                    Console.WriteLine($"   {motion,-7} body {(src == 'c' ? "collision" : "render   ")} {set.Count,8:N0} refs   " + string.Join("   ", line));
                }
            var ccRows = abs.Where(r => Cc(r.Owner >= 0 ? r.Owner : r.StepPlugin)).ToList();
            Console.WriteLine($"   of which owned by Creation Club plugins: {ccRows.Count:N0} (floating ≥8u {ccRows.Count(r => r.Facts is { } f && Floating(f, eps, 8f)):N0})");
            Console.WriteLine($"   largest floats as shipped:");
            foreach (var r in abs.Where(r => r.Motion == DependentMotion.Fixed && r.Verdict == TerrainVerdict.Floating).OrderByDescending(r => r.Gap).Take(listN))
                Console.WriteLine($"      {(float.IsPositiveInfinity(r.Gap) ? "no ground" : r.Gap.ToString("0.#") + " u"),10}  {baseLabel(r.Base)} {r.Key} [{r.BodySrc}] at ({r.Pose.Pos.X:0},{r.Pose.Pos.Y:0},{r.Pose.Pos.Z:0})");
            Console.WriteLine($"   ({sw.Elapsed.TotalSeconds:0.0}s)");
        }

        // ---- 3. the differential pass ------------------------------------------------------------------------------------------
        var rows = tci.Judge(keepAll: true);
        var judged = rows.Where(r => r.Status is TerrainStatus.Row or TerrainStatus.StillResting).ToList();
        Console.WriteLine($"\n==== 3. THE INHERITED FLOOR — Bethesda-owned references on a Bethesda LAND change (printed before any class count) ====");
        var inh = judged.Where(r => r.Population == TerrainPopulation.Inherited).ToList();
        var inhCc = inh.Where(r => Cc(r.Owner) || Cc(r.Winner)).ToList();
        Console.WriteLine($"   denominator: Inherited references in the population (rested on the authored ground) in a cell whose LAND changed by more than one heightmap unit under them: {inh.Count:N0} (of which a CC plugin on either side {inhCc.Count:N0})");
        PrintDistribution("Inherited", inh, eps);
        PrintDistribution("Inherited without CC", inh.Except(inhCc).ToList(), eps);
        Console.WriteLine($"   ({sw.Elapsed.TotalSeconds:0.0}s)");

        Console.WriteLine($"\n==== 4. THE CLASS — at float {floatThreshold:0.#} u / burial {burialThreshold:0.#} u ====");
        foreach (var kv in tci.Counts.Where(k => !k.Key.StartsWith("absolute")).OrderByDescending(k => k.Value)) Console.WriteLine($"   status {kv.Key,-56} {kv.Value,10:N0}");
        var rowSet = rows.Where(r => r.Status == TerrainStatus.Row).ToList();
        foreach (var g in rowSet.GroupBy(r => (r.Population, r.Sight, r.Motion)).OrderBy(g => g.Key.Population).ThenBy(g => g.Key.Sight))
            Console.WriteLine($"   {g.Key.Population,-9} {g.Key.Sight,-9} {g.Key.Motion,-8} " +
                              string.Join(", ", g.GroupBy(r => r.Verdict).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count():N0}")));
        var cls = rowSet.Where(TerrainContactIndex.Emitted).ToList();
        PrintDistribution("Mod Blind Fixed (all verdicts, before the thresholds)", judged.Where(r => r.Population == TerrainPopulation.Mod && r.Sight == TerrainSight.Blind && r.Motion == DependentMotion.Fixed).ToList(), eps);
        Console.WriteLine($"   THE CLASS (Introduced Blind, Fixed, Mod population, Floating or Buried): {cls.Count:N0}  — by owner/winner: " +
                          string.Join(", ", cls.GroupBy(r => (Name(r.Owner), Name(r.Winner))).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key.Item1} × {g.Key.Item2} {g.Count():N0}")));
        Console.WriteLine($"   set aside, never shown as clashes: {rowSet.Count - cls.Count:N0} other judged rows (Seen, Inherited, USSEP, Movable, CannotTell, Refused); every status above is counted");
        foreach (var r in cls.OrderByDescending(r => r.Verdict == TerrainVerdict.Buried ? r.Burial : r.Gap).Take(listN))
            Console.WriteLine($"      {r.Verdict} {(r.Verdict == TerrainVerdict.Buried ? r.Burial : r.Gap):0.#} u  {baseLabel(r.Base)} {r.Key} [{r.BodySrc}] owner {Name(r.Owner)}, ground {Name(r.Winner)} in " +
                              string.Join(" ", (r.Cells ?? Array.Empty<TerrainCellReading>()).Where(c => c.Changed).Select(c => $"({c.GX},{c.GY})")));

        // ---- emit -----------------------------------------------------------------------------------------------------------------
        if (emitPath is not null)
        {
            var conflicts = new List<Conflict>();
            foreach (var r in cls)
            {
                var (st, why) = tci.Predicate(r.Key);
                var cell = (r.Cells ?? Array.Empty<TerrainCellReading>()).FirstOrDefault(c => c.Changed && c.WinnerPlugin == r.Winner);
                string what = r.Verdict == TerrainVerdict.Buried ? $"is buried {r.Burial:0.#} u deeper" : float.IsPositiveInfinity(r.Gap) ? "floats over no ground" : $"floats {r.Gap:0.#} u in the air";
                conflicts.Add(new Conflict(Kind, Name(r.Owner), Name(r.Winner),
                    $"{Name(r.Winner)} reshaped the ground under {Name(r.Owner)}'s {baseLabel(r.Base)} at ({r.Pose.Pos.X:0}, {r.Pose.Pos.Y:0}): it {what}  {r.Key}",
                    new List<ConflictRecord> { new(r.Key.ToString(), "placed-ref", cell is null ? null : $"cell ({cell.GX},{cell.GY})", st, why) },
                    cell is { Cell.IsNull: false } ? new List<ConflictRecord> { new(cell.Cell.ToString(), "cell-land", null) } : null));
            }
            List<Predicate>? predicates = null;
            if (basePath is not null)
            {
                predicates = new List<Predicate>();
                foreach (var c in ConflictIo.Read(basePath).Conflicts)
                {
                    string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
                    foreach (var rec in c.Necessary)
                    {
                        if (c.Kind != Kind) { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not a terrain row")); continue; }
                        var (st, why) = tci.Predicate(FormKey.Factory(rec.FormKey));
                        predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
                    }
                }
                Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — " +
                                  string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
            }
            long C(TerrainStatus s) => tci.Counts.GetValueOrDefault(s.ToString());
            var coverage = new List<CoverageLine>
            {
                new("exterior placed references whose footprint touches a cell with 2+ LAND statements",
                    C(TerrainStatus.Row) + C(TerrainStatus.StillResting) + C(TerrainStatus.TerrainAgrees) + C(TerrainStatus.NotRestingAsAuthored) + C(TerrainStatus.NoAuthoredGround),
                    new Dictionary<string, long> { ["UnreadableLand"] = C(TerrainStatus.Refused), ["DeletedWithPlacement"] = C(TerrainStatus.DeletedWithPlacement),
                        ["DeletedInOwnerView"] = C(TerrainStatus.DeletedInOwnerView), ["EnableChainUnresolved"] = C(TerrainStatus.EnableUnresolved),
                        ["DuplicateGrid"] = C(TerrainStatus.DuplicateGrid), ["AuthoredDiagonalsDisagree"] = C(TerrainStatus.AuthoredCannotTell) },
                    new Dictionary<string, long> { ["NotAPlacedObject"] = C(TerrainStatus.NotObject), ["Marker"] = C(TerrainStatus.Marker), ["NoBody"] = C(TerrainStatus.NoBody),
                        ["DisabledAtLoad"] = C(TerrainStatus.DisabledAtLoad), ["DeletedAtLoad"] = C(TerrainStatus.DeletedAtLoad) },
                    $"float {floatThreshold:0.#} u, burial {burialThreshold:0.#} u, ε {eps:0.#} u; rows {rowSet.Count:N0}, class {cls.Count:N0}; burial is exact for a uniform shift, a lower bound otherwise"),
            };
            ConflictIo.Write(emitPath, new ConflictFile("contactprobe", instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                        Population, EnablePopulation.BaselineStateName, identity));
            Console.WriteLine($"configuration: {identity.Short}");
            Console.WriteLine($"\nemitted {conflicts.Count:N0} terrain-support conflicts -> {emitPath}");
        }

        if (jsonPath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool Full(TerrainRow r) => r.Status is TerrainStatus.Row or TerrainStatus.StillResting or TerrainStatus.Refused or TerrainStatus.AuthoredCannotTell or TerrainStatus.NotRestingAsAuthored;
            // every other status as a short record (key, status, why, footprint cells), so a reader can account for each reference
            var brief = rows.Where(r => !Full(r)).Select(r => new
            {
                key = r.Key.ToString(), status = r.Status.ToString(), owner = r.Owner >= 0 ? Name(r.Owner) : null, why = r.Why,
                cells = r.Cells?.Select(c => new { c.GX, c.GY, c.Changed, authored = Name(c.AuthoredPlugin), winner = Name(c.WinnerPlugin) }),
            });
            var keep = rows.Where(Full).Select(r => new
            {
                key = r.Key.ToString(), @base = baseLabel(r.Base), status = r.Status.ToString(), verdict = r.Verdict.ToString(), sight = r.Sight.ToString(),
                population = r.Population.ToString(), motion = r.Motion.ToString(), body = r.BodySrc.ToString(), owner = Name(r.Owner), step = Name(r.StepPlugin),
                winner = r.Winner >= 0 ? Name(r.Winner) : null, pos = new[] { r.Pose.Pos.X, r.Pose.Pos.Y, r.Pose.Pos.Z },
                gap = F(r.Gap), burial = F(r.Burial), saddle = F(r.Saddle), authored = new[] { F(r.AuthoredD0), F(r.AuthoredD1) },
                winning = new[] { F(r.WinningD0), F(r.WinningD1) }, burialByDiagonal = new[] { F(r.BurialD0), F(r.BurialD1) }, coreGap = F(r.CoreGap),
                landing = r.Landing is { } l ? F(l) : null, support = r.Support?.ToString(), emitted = TerrainContactIndex.Emitted(r), why = r.Why,
                cells = r.Cells?.Select(c => new { c.GX, c.GY, authored = Name(c.AuthoredPlugin), c.AuthoredKind, winner = Name(c.WinnerPlugin), c.WinnerKind, delta = F(c.MaxDelta), c.Changed, sight = c.Sight.ToString(), population = c.Population.ToString() }),
            });
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { probe = "contactprobe", mode = "terrain", population = Population, epsilon = eps, floatThreshold, burialThreshold,
                                                                        counts = tci.Counts, rows = keep, others = brief }, new JsonSerializerOptions { WriteIndented = false }));
            Console.WriteLine($"wrote {jsonPath}");
        }
        Console.WriteLine($"\nLimits: the body is the reference's collision (or render when it has none), posed with the winning record's rotation in both views " +
                          "(a later rotation-only override is not attributed); the engine's terrain triangulation is not in the file, so both diagonals are read and a " +
                          "disagreement is CannotTell; burial is exact for a uniform shift and a lower bound otherwise; nothing here is verified in game.");
        Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    static float? F(float v) => float.IsFinite(v) ? MathF.Round(v, 2) : null;

    /// <summary>Floating at a threshold: neither diagonal within ε of the ground, no reference holding it, the least gap over it.</summary>
    static bool Floating(DetectorModel.TerrainSupportFacts f, float eps, float col)
        => DetectorModel.TerrainSupport(f, eps, col, float.PositiveInfinity).Verdict == TerrainVerdict.Floating;

    static void PrintDistribution(string label, List<TerrainRow> set, float eps)
    {
        if (set.Count == 0) { Console.WriteLine($"   {label}: none"); return; }
        foreach (var col in Columns)
        {
            int fl = 0, bu = 0, ct = 0;
            foreach (var r in set)
            {
                if (r.Facts is not { } f) continue;
                var v = DetectorModel.TerrainSupport(f, eps, col, col).Verdict;
                if (v == TerrainVerdict.Floating) fl++; else if (v == TerrainVerdict.Buried) bu++; else if (v == TerrainVerdict.CannotTell) ct++;
            }
            Console.WriteLine($"   {label} at ≥{col:0} u: floating {fl:N0} ({100f * fl / set.Count:0.00}%), buried {bu:N0} ({100f * bu / set.Count:0.00}%), cannot tell {ct:N0} ({100f * ct / set.Count:0.00}%) of {set.Count:N0}");
        }
    }
}
