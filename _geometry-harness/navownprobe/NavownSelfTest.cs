using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using F = HarnessShared.MovedRefFixture;

// navownprobe --moved-ref-selftest: the fixture's order through the needs-witness census (NeedsWitnessJoin.Build, with E00
// as the pool's cell) and through the UNCUT population (UncutPopulation, as the main walk feeds it). Expectations are
// worked out by hand from MovedRefFixture's table.
static class NavownSelfTest
{
    public static int Run() => MovedRefSelfTest.Run("navownprobe", t =>
    {
        // ---- N1: the witness census ----
        var c = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.E00 });
        // M5 is persistent in T and stands at (1400,1400), in grid (0,0): a witness of E00 by position (NW-P2).
        var expect = new[] { F.M5, F.M6, F.C1, F.C2, F.P1, F.P3, F.NPC1, F.U3 }.Select(MovedRefSelfTest.Name).OrderBy(x => x).ToList();
        var got = c.Refs.Keys.Select(MovedRefSelfTest.Name).OrderBy(x => x).ToList();
        t.Check("N1  witnesses in E00: exactly the references whose last sighting is there",
                got.SequenceEqual(expect), $"got [{string.Join(", ", got)}] expected [{string.Join(", ", expect)}]");
        t.Check("N1-M1 a witness moved out of the pool's cells is not a witness", !c.Refs.ContainsKey(F.M1),
                c.Refs.TryGetValue(F.M1, out var wm1) ? $"held in {wm1.Cell}" : "absent");
        string Cell(FormKey k) => c.RefCellAnywhere.TryGetValue(k, out var v) ? v.ToString() : "none";
        t.Check("N1-D1 RefCellAnywhere: a reference deleted with no placement data has no cell", !c.RefCellAnywhere.ContainsKey(F.D1), Cell(F.D1));
        t.Check("N1-D2 RefCellAnywhere: deleted in a copy holding only the deletion: no cell", !c.RefCellAnywhere.ContainsKey(F.D2), Cell(F.D2));
        t.Check("N1-M3 RefCellAnywhere: moved to an interior, that interior", Cell(F.M3) == F.I2.ToString(), Cell(F.M3));
        t.Check("N1-NPC4 RefCellAnywhere: a reference deleted WITH its data is placed nowhere", !c.RefCellAnywhere.ContainsKey(F.NPC4), Cell(F.NPC4));
        var c10 = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.E10 });
        t.Check("N1-D3 a reference deleted WITH its data in a wanted cell is not a witness", !c10.Refs.ContainsKey(F.D3),
                c10.Refs.TryGetValue(F.D3, out var w3) ? $"held, state '{w3.State}'" : "absent");
        // ---- NW-P: a reference held in the worldspace's persistent cell (T, XCLC 0,0) is a witness where it stands, as
        // viewprobe merges persistent references by position, never at T's grid ----
        string HeldIn(NeedsWitnessJoin.Census cs, FormKey k) => cs.Refs.TryGetValue(k, out var w) ? w.Cell.ToString() : "absent";
        var c50 = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.E50 });
        t.Check("NW-P1 a persistent reference standing in grid (5,0) is a witness of E50, filed under E50",
                c50.Refs.TryGetValue(F.Q1, out var q1) && q1.Cell == F.E50, $"Q1 {HeldIn(c50, F.Q1)}");
        t.Check("NW-P2 a persistent reference standing in grid (0,0) is a witness of E00, filed under E00",
                c.Refs.TryGetValue(F.M5, out var m5) && m5.Cell == F.E00, $"M5 {HeldIn(c, F.M5)}");
        var cT = NeedsWitnessJoin.Build(t.Paths, new HashSet<FormKey> { F.T });
        var inT = cT.Refs.Values.Where(r => r.Cell == F.T).Select(r => MovedRefSelfTest.Name(r.Key)).OrderBy(x => x).ToList();
        t.Check("NW-P3 nothing is filed under the persistent cell itself, even when it is wanted", inT.Count == 0,
                $"filed under T: [{string.Join(", ", inT)}]");
        t.Check("NW-P4 RefCellAnywhere: a persistent reference's cell is the gridded cell it stands in", Cell(F.Q1) == F.E50.ToString(),
                $"Q1 {Cell(F.Q1)}");
        t.Check("NW-C  control: a temporary reference whose last sighting is E50 is filed under E50",
                c50.Refs.TryGetValue(F.M1, out var m1) && m1.Cell == F.E50, $"M1 {HeldIn(c50, F.M1)}");
        var npcs = c.NpcPlacedAnywhere.Select(k => k == F.N1 ? "N1" : k == F.N2 ? "N2" : k.ToString()).OrderBy(x => x).ToList();
        t.Check("N1-NPC NpcPlacedAnywhere: N2 only (N1's one reference was re-based to N2; N2's other was deleted)",
                npcs.SequenceEqual(new[] { "N2" }), string.Join(", ", npcs));
        t.Check("C2 control: a witness untouched in E00" + (t.Canary ? " [CANARY: expectation planted wrong]" : ""),
                t.Canary ? !c.Refs.ContainsKey(F.C2) : c.Refs.ContainsKey(F.C2), c.Refs.ContainsKey(F.C2) ? "present" : "absent");

        // ---- N3: the navmesh version history (NavVersions, as the main walk feeds it): whose winning version is a deletion.
        // NVD and NVK are deleted by A (1), NVK with its data; NVX by X (3), in a copy holding nothing else; NVR, deleted by
        // A, is restored by B (2) ----
        var hist = new NavVersions();
        foreach (var (i, mod, _) in t.Plugins())
        {
            var deletedHere = mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>().Where(n => n.IsDeleted).Select(n => n.FormKey).ToHashSet();
            foreach (var cp in UncutPopulation.Cells(mod)) hist.Note(cp, i, deletedHere, _ => 0);
        }
        var del = hist.DeletedAtWinner().Select(kv => $"{MovedRefSelfTest.NavName(kv.Key)}/{kv.Value}").OrderBy(x => x).ToList();
        t.Check("N3  navmeshes whose winning version is a deletion, with or without data: NVD/1, NVK/1, NVX/3 (NVR restored by B)",
                del.SequenceEqual(new[] { "NVD/1", "NVK/1", "NVX/3" }), string.Join(", ", del));

        // ---- N4: the DELETED section's report, with the shipped-game deletions counted apart (default) or listed
        // (--list-master-deletions). The fixture has no Bethesda master, so plugin A (1) stands in for one: its deletions
        // (NVD, NVK) are the shipped game's; X's deletion (NVX, 3) is a mod's and a finding in both modes. ----
        var names = t.Plugins().ToDictionary(p => p.Index, p => p.Mod.ModKey.FileName.String);
        string CellOf(FormKey k) => k == F.E00 ? "E00" : k == F.E11 ? "E11" : k.ToString();
        List<string> Report(bool listShipped) => NavDeletions.Report(hist, _ => true, p => p == 1, p => names[p], CellOf,
                                                                     listN: 25, listShippedGame: listShipped);
        var off = Report(false);
        var on = Report(true);
        string Show(List<string> l) => string.Join(" | ", l.Select(x => x.Trim()));
        var nvx = $"   DELETED  {F.NVX} in E11 by {names[3]} (no geometry left)";
        t.Check("N4-off default: the findings are X's deletion only; the shipped-game deletions are a count, never listed",
                off.Contains(nvx) && off.Contains("   findings: 1")
                && off.Contains("   deleted by a Bethesda master (the shipped game; not findings): 2")
                && !off.Any(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())), Show(off));
        var shipped = on.Where(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())).ToList();
        t.Check("N4-on --list-master-deletions: NVD and NVK are listed, each line labelled a shipped-game deletion",
                shipped.Count == 2 && shipped.All(l => l.Contains("shipped-game deletion") && l.Contains($"by {names[1]}")),
                Show(on));
        t.Check("N4-on a mod's deletion stays a finding when the shipped game's are listed: findings 1, NVX in the findings",
                on.Contains(nvx) && on.Contains("   findings: 1")
                && on.IndexOf(nvx) < on.FindIndex(l => l.Contains(F.NVD.ToString()) || l.Contains(F.NVK.ToString())), Show(on));
        t.Check("N4-on the count line is unchanged by the toggle",
                on.Contains("   deleted by a Bethesda master (the shipped game; not findings): 2"), Show(on));
        t.Check("N4-off the default report is the toggle-on report minus the shipped-game listing, line for line",
                off.SequenceEqual(on.Where(l => !shipped.Contains(l) && !l.Contains("shipped-game deletions ("))), Show(off));

        // ---- N5: the command line. Every argv shape a tracked caller passes is accepted, and anything else is refused BY
        // NAME: a typo of a flag used to run the default report with exit 0. Callers: differential-sweep.ps1 (base and
        // candidate arms), ordersweep.py / evalrun.py (the check registry's adapter args plus the common pins), the
        // navmesh-packet regression row, and the historical drivers' reach/state/flags shapes. ----
        (string Name, string[] Argv)[] accepted =
        {
            ("differential-sweep base", new[] { "--emit", "base.json" }),
            ("differential-sweep candidate", new[] { "--enable", "Cand.esp", "--base", "base.json", "--emit", "e.json" }),
            ("ordersweep/evalrun arm", new[] { "--order", "o.json", "--base", "b.json", "--emit", "e.json", "--instance", "I", "--profile", "P", "--game", "G" }),
            ("ordersweep/evalrun baseline", new[] { "--order", "o.json", "--emit", "e.json", "--instance", "I", "--profile", "P", "--game", "G" }),
            ("regression row", new[] { "--instance", "I", "--game", "G", "--profile", "P", "--order", "o.json", "--emit", "e.json" }),
            ("reach drivers", new[] { "--reach-candidates", "--reach-json", "r.json", "--reach-top", "5", "--reach-witness" }),
            ("population and filters", new[] { "--flags", "--state", "s.json", "--list", "50", "--plugin", "A.esp", "--disable", "X.esp", "--door-idx" }),
            ("the toggle", new[] { "--list-master-deletions" }),
        };
        foreach (var (name, argv) in accepted)
        {
            var a = NavownArgs.Parse(argv);
            t.Check($"N5  accepted: {name}", a.Error is null, a.Error ?? string.Join(" ", argv));
        }
        var full = NavownArgs.Parse(new[] { "--instance", "I", "--game", "G", "--profile", "P", "--order", "o.json", "--base", "b.json",
                                            "--emit", "e.json", "--list", "50", "--plugin", "A.esp", "--enable", "E.esp",
                                            "--reach-json", "r.json", "--reach-top", "7", "--list-master-deletions" });
        t.Check("N5  values land where the old loop put them",
                full.Error is null && full.Instance == "I" && full.Game == "G" && full.Profile == "P" && full.OrderFile == "o.json"
                && full.BasePath == "b.json" && full.EmitPath == "e.json" && full.ListN == 50 && full.OnlyPlugin == "A.esp"
                && full.Enable.SequenceEqual(new[] { "E.esp" }) && full.ReachCandidates && full.ReachJsonPath == "r.json"
                && full.ReachTopN == 7 && full.ListMasterDeletions && !full.ReachWitness && !full.FlagsOnly,
                full.Error ?? "parsed");
        var dflt = NavownArgs.Parse(Array.Empty<string>());
        t.Check("N5  defaults: list 25, reach-top 20, toggle off", dflt.Error is null && dflt.ListN == 25 && dflt.ReachTopN == 20
                && !dflt.ListMasterDeletions, dflt.Error ?? "");
        var typo = NavownArgs.Parse(new[] { "--order", "o.json", "--list-master-deletion" });
        t.Check("N5  RED: a typo of the toggle is refused, naming the argument",
                typo.Error is not null && typo.Error.Contains("--list-master-deletion"), typo.Error ?? "accepted");
        var stray = NavownArgs.Parse(new[] { "stray" });
        t.Check("N5  RED: a stray positional argument is refused, naming it", stray.Error is not null && stray.Error.Contains("stray"),
                stray.Error ?? "accepted");
        var bare = NavownArgs.Parse(new[] { "--order", "o.json", "--emit" });
        t.Check("N5  RED: a flag missing its value is refused, naming the flag", bare.Error is not null && bare.Error.Contains("--emit"),
                bare.Error ?? "accepted");
        var badN = NavownArgs.Parse(new[] { "--list", "many" });
        t.Check("N5  RED: a non-integer --list is refused, naming the flag", badN.Error is not null && badN.Error.Contains("--list"),
                badN.Error ?? "accepted");

        // ---- N2: the UNCUT population ----
        var uncut = new UncutPopulation();
        foreach (var (i, mod, _) in t.Plugins())
            foreach (var cp in UncutPopulation.Cells(mod)) uncut.Note(cp, i, mod.ModKey);
        var cellNewRefs = new Dictionary<(FormKey Cell, int Plugin), List<FormKey>>();
        uncut.Resolve(k => k == F.S ? "Static" : null, new HashSet<string> { "Static" }, _ => true, cellNewRefs, out _, out _);
        // A's introductions only (plugin 1): the master's own placements are introductions too, and are not this case.
        var counted = uncut.Counted.Where(x => x.Plugin == 1).Select(x => $"{MovedRefSelfTest.Name(x.Ref)}@{(x.Cell == F.E00 ? "E00" : x.Cell.ToString())}/{x.Plugin}").OrderBy(x => x).ToList();
        t.Check("N2  UNCUT counts A's introductions still standing and still obstructing: U3 only (U1 moved out, U2 deleted by B, U4 re-based to a marker, U5 base cleared)",
                counted.SequenceEqual(new[] { "U3@E00/1" }), string.Join(", ", counted));

        // ---- PD: the clobber and dangling-link fixture (NavownFixture.cs), written beside the moved-reference one ----
        var nvPaths = NavownFixture.Write(Path.Combine(t.Root, "navown"));
        var nvMods = nvPaths.Select(p => Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(p, NavownFixture.Rel)).ToList();
        try
        {
            string Masters(int i) => string.Join(",", nvMods[i].ModHeader.MasterReferences.Select(r => r.Master.FileName.String));
            t.Check("PD-F0 the fixture writes its seven plugins in order",
                    nvPaths.Select(Path.GetFileName).SequenceEqual(NavownFixture.Order), string.Join(", ", nvPaths.Select(Path.GetFileName)));
            t.Check("PD-F0 NvB masters only NvMaster.esm (so NvA's re-cuts are undeclared)", Masters(5) == "NvMaster.esm", Masters(5));
            t.Check("PD-F0 NvFix masters NvA.esp and NvB.esp", Masters(6).Contains("NvA.esp") && Masters(6).Contains("NvB.esp"), Masters(6));

            // The walk's inputs for the first n plugins, read as navownprobe's main walk reads them.
            (string[] Names, HashSet<int>[] Closure, NavVersions Versions, Dictionary<(FormKey, int), NavmeshGeometry> Geom) Order(int n)
            {
                var names = nvMods.Take(n).Select(mo => mo.ModKey.FileName.String).ToArray();
                var direct = new HashSet<int>[n];
                var versions = new NavVersions();
                var geom = new Dictionary<(FormKey, int), NavmeshGeometry>();
                for (int i = 0; i < n; i++)
                {
                    var ms = nvMods[i].ModHeader.MasterReferences.Select(r => r.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    direct[i] = Enumerable.Range(0, i).Where(q => ms.Contains(names[q])).ToHashSet();
                    var deletedHere = nvMods[i].EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>().Where(x => x.IsDeleted).Select(x => x.FormKey).ToHashSet();
                    foreach (var cp in UncutPopulation.Cells(nvMods[i])) versions.Note(cp, i, deletedHere, ClobberFindings.GeomHash);
                    foreach (var cp in PlacementExtract.FromMod(nvMods[i]))
                        foreach (var nav in cp.Navmeshes) geom[(nav.FormKey, i)] = nav;
                }
                return (names, DetectorModel.MasterClosure(direct), versions, geom);
            }
            string NvName(FormKey k) => k == NavownFixture.NMOD ? "NMOD" : k == NavownFixture.NFWD ? "NFWD" : k == NavownFixture.NNEAR ? "NNEAR"
                                      : k == NavownFixture.NUS ? "NUS" : k == NavownFixture.NVAN ? "NVAN" : k.ToString();
            var o6 = Order(6);
            var clob = ClobberFindings.Find(o6.Versions.Providers, o6.Closure, o6.Versions.Size, _ => true);
            ClobberPopulation PopOf((FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material) c)
                => ClobberFindings.Population(o6.Names[c.Loser], o6.Names[c.Winner], BaseGamePlugins.Names);
            string Row((FormKey Nav, int Loser, int Winner, int LoserT, int WinnerT, bool Material) c)
                => $"{NvName(c.Nav)} {o6.Names[c.Loser]}->{o6.Names[c.Winner]}";

            // PD-N1: the findings are the material clobbers outside the Bethesda floor.
            var findings = clob.Where(c => ClobberFindings.IsFinding(c.Material, PopOf(c))).Select(Row).OrderBy(x => x).ToList();
            t.Check("PD-N1 clobber findings: NMOD and NNEAR (NvA->NvB) and NUS (USSEP->NvB); NVAN (class A) and NFWD (not material) withheld",
                    findings.SequenceEqual(new[] { "NMOD NvA.esp->NvB.esp", "NNEAR NvA.esp->NvB.esp", "NUS Unofficial Skyrim Special Edition Patch.esp->NvB.esp" }),
                    string.Join(" | ", findings));
            var all = clob.Select(c => $"{Row(c)} material={c.Material}").OrderBy(x => x).ToList();
            t.Check("PD-N1 the clobber walk itself finds all five chains, NFWD not material",
                    all.SequenceEqual(new[] { "NFWD NvA.esp->NvB.esp material=False", "NMOD NvA.esp->NvB.esp material=True",
                                              "NNEAR NvA.esp->NvB.esp material=True", "NUS Unofficial Skyrim Special Edition Patch.esp->NvB.esp material=True",
                                              "NVAN Dawnguard.esm->HearthFires.esm material=True" }), string.Join(" | ", all));

            // PD-N2: the population by file name.
            var pops = clob.Select(c => $"{NvName(c.Nav)}:{PopOf(c)}").OrderBy(x => x).ToList();
            t.Check("PD-N2 populations: NUS UssepDiscarded, NVAN BethesdaFloor, NMOD/NNEAR/NFWD ModOnMod",
                    pops.SequenceEqual(new[] { "NFWD:ModOnMod", "NMOD:ModOnMod", "NNEAR:ModOnMod", "NUS:UssepDiscarded", "NVAN:BethesdaFloor" }),
                    string.Join(", ", pops));
            t.Check("PD-N2 file names match whatever their letter case: dawnguard.esm x HEARTHFIRES.ESM is the floor; a lower-case USSEP loser is USSEP's",
                    ClobberFindings.Population("dawnguard.esm", "HEARTHFIRES.ESM", BaseGamePlugins.Names) == ClobberPopulation.BethesdaFloor
                    && ClobberFindings.Population("unofficial skyrim special edition patch.esp", "NvB.esp", BaseGamePlugins.Names) == ClobberPopulation.UssepDiscarded,
                    "");

            // PD-N3: the physical predicate.
            var holdMod = ClobberFindings.Predicate(NavownFixture.NMOD, 4, 5, o6.Geom, "NvB.esp");
            var near = ClobberFindings.Predicate(NavownFixture.NNEAR, 4, 5, o6.Geom, "NvB.esp");
            var same = ClobberFindings.Predicate(NavownFixture.NMOD, 4, 4, o6.Geom, "NvA.esp");
            var missing = ClobberFindings.Predicate(NavownFixture.NMOD, 4, 5, new Dictionary<(FormKey, int), NavmeshGeometry>(), "NvB.esp");
            var o7 = Order(7);
            var fixedMod = ClobberFindings.Predicate(NavownFixture.NMOD, 4, 6, o7.Geom, "NvFix.esp");
            t.Check("PD-N3 NMOD: NvA's island about 2,500 u from NvB's ground is victim-only ground: Holds", holdMod.State == PhysicalState.Holds, holdMod.Detail);
            t.Check("PD-N3 NNEAR: every NvA point within 50 u of NvB's ground: Gone", near.State == PhysicalState.Gone, near.Detail);
            t.Check("PD-N3 loser == winner: the loser's own version wins: Gone", same.State == PhysicalState.Gone, same.Detail);
            t.Check("PD-N3 a version's geometry unread: Unmeasured", missing.State == PhysicalState.Unmeasured, missing.Detail);
            t.Check("PD-N3 under NvFix, which restores the island: Gone", fixedMod.State == PhysicalState.Gone, fixedMod.Detail);

            // The dangling-link state for the first n plugins, noted in record order as the main walk notes it.
            DanglingLinks Links(int n)
            {
                var l = new DanglingLinks();
                for (int i = 0; i < n; i++)
                    foreach (var rec in nvMods[i].EnumerateMajorRecords())
                        if (rec is Mutagen.Bethesda.Skyrim.INavigationMeshGetter nm) l.NoteNavmesh(nm);
                        else if (rec is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter po) l.NotePlaced(po, i);
                return l;
            }
            string Id(FormKey k) => k == NavownFixture.NE ? "NE" : k == NavownFixture.NE2 ? "NE2" : k == NavownFixture.ND ? "ND" : k == NavownFixture.NG ? "NG"
                                  : k == NavownFixture.NX ? "NX" : k == NavownFixture.DR3 ? "DR3" : k == NavownFixture.DR4 ? "DR4" : k == NavownFixture.VF ? "VF" : k.ToString();
            int Owner(NavVersions v, FormKey nav) => v.Providers.TryGetValue(nav, out var pr) && pr.Count > 0 ? pr[^1] : -1;
            var l6 = Links(6);
            var f6 = l6.Find(k => Owner(o6.Versions, k), _ => "IC", reportDoorIdx: false);
            var kinds = f6.Rows.Select(r => $"{r.Kind} {Id(r.Rec)}").OrderBy(x => x).ToList();

            // PD-N4: the walk, at the triangle count.
            t.Check("PD-N4 dangles: DOOR-TRI ND, DOOR-GONE NG, DOOR-NAV DR3, EDGE-IDX NE and VF (index = count), EDGE-NAV NX; NE2 (count - 1) is not one",
                    kinds.SequenceEqual(new[] { "DOOR-GONE NG", "DOOR-NAV DR3", "DOOR-TRI ND", "EDGE-IDX NE", "EDGE-IDX VF", "EDGE-NAV NX" }),
                    string.Join(", ", kinds));
            var f6i = l6.Find(k => Owner(o6.Versions, k), _ => "IC", reportDoorIdx: true);
            t.Check("PD-N4 DOOR-IDX is retired: counted once, listed only with --door-idx",
                    f6.DoorIdxSuppressed == 1 && !f6.Rows.Any(r => r.Kind == "DOOR-IDX")
                    && f6i.Rows.Count(r => r.Kind == "DOOR-IDX" && r.Rec == NavownFixture.DR4) == 1,
                    $"suppressed {f6.DoorIdxSuppressed}; with the flag {f6i.Rows.Count(r => r.Kind == "DOOR-IDX")}");

            // PD-N5: the predicate, re-asked of a winning state.
            var l7 = Links(7);
            var neBase = l6.Predicate("EDGE-IDX", NavownFixture.NE, NavownFixture.NT);
            var ne2 = l6.Predicate("EDGE-IDX", NavownFixture.NE2, NavownFixture.NT);
            var neFix = l7.Predicate("EDGE-IDX", NavownFixture.NE, NavownFixture.NT);
            var ndBase = l6.Predicate("DOOR-TRI", NavownFixture.ND, NavownFixture.DR1);
            var ndFix = l7.Predicate("DOOR-TRI", NavownFixture.ND, NavownFixture.DR1);
            t.Check("PD-N5 EDGE-IDX at exactly the target's count: Holds", neBase.State == PhysicalState.Holds, neBase.Detail);
            t.Check("PD-N5 EDGE-IDX at count - 1: Gone", ne2.State == PhysicalState.Gone, ne2.Detail);
            t.Check("PD-N5 EDGE-IDX under NvFix, which gives NT a third triangle: Gone", neFix.State == PhysicalState.Gone, neFix.Detail);
            t.Check("PD-N5 DOOR-TRI at exactly its own count: Holds, with and without NvFix",
                    ndBase.State == PhysicalState.Holds && ndFix.State == PhysicalState.Holds, $"{ndBase.Detail} / {ndFix.Detail}");

            // PD-N6: a dangle with only vanilla-named plugins on both ends is the floor, withheld; the rest are findings.
            bool Touched(FormKey rec, string role) => DanglingLinks.ModTouched(
                role == "navmesh" ? o6.Versions.Providers.GetValueOrDefault(rec) : l6.RefOverriders.GetValueOrDefault(rec),
                i => BaseGamePlugins.Names.Contains(o6.Names[i]));
            var emitted = f6.Rows.Where(r => DanglingLinks.IsFinding(Touched(r.Rec, r.RecRole), Touched(r.Other, r.OtherRole)))
                                 .Select(r => $"{r.Kind} {Id(r.Rec)}").OrderBy(x => x).ToList();
            t.Check("PD-N6 emitted dangles: all but VF->VT (Dawnguard.esm on both ends)",
                    emitted.SequenceEqual(new[] { "DOOR-GONE NG", "DOOR-NAV DR3", "DOOR-TRI ND", "EDGE-IDX NE", "EDGE-NAV NX" }),
                    string.Join(", ", emitted));
        }
        finally { foreach (var mo in nvMods) (mo as IDisposable)?.Dispose(); }
    });
}
