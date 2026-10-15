using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.StateFixture;

// stateprobe --self-test: _shared/StateFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), the order run through
// the probe's own passes (the contact index over StateGeometry, the switch census, the evaluator), and the decisions the
// probe's top level hands to its classes checked on the rows. Expectations are worked out by hand from the fixture's table.
// The evaluator's route question is a stub here (Blocked for W1-W3, None otherwise): the route oracle's own choices are
// checked through RouteObstacle on the fixture's corridor, and its rows come from the probe runs.
static class StateSelfTest
{
    sealed record Pipeline(ContactIndex Contact, StateGeometry Geometry, SwitchIndex Switches, ModKey[] Plugins, IReadOnlyList<string> Paths)
    {
        public string Name(int p) => p < 0 ? "(none)" : Plugins[p].FileName.String;
        public bool IsVanilla(int p) => p >= 0 && PluginKinds.IsBethesda(Name(p));
        public bool IsUssep(int p) => p >= 0 && PluginKinds.IsUssep(Name(p));
    }

    static Pipeline Build(F.Written w, string root)
    {
        var empty = Path.Combine(root, "st-empty"); Directory.CreateDirectory(empty);
        var assets = AssetResolver.Build(empty, w.ModsDir, empty, new[] { F.Skyrim.Name }, Array.Empty<ActiveArchive>());
        // Program.cs's reader: the winning provider's bytes, or null when none supplies the path.
        var geo = new StateGeometry(full => assets.ResolveForPlacement(full) is { Sources.Count: > 0 } res ? AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes : null);
        var cidx = new ContactIndex(geo.Of, cacheBudgetBytes: 256L << 20);
        geo.Contact = cidx;
        var keys = new ModKey[w.Order.Count];
        for (int p = 0; p < w.Order.Count; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(w.Order[p], SkyrimRelease.SkyrimSE);
            keys[p] = mod.ModKey;
            cidx.Index.Ingest(p, mod);
        }
        var sx = SwitchIndex.Build(cidx.Index);
        sx.NameSwitches(cidx.Index.RefEditorId);
        return new Pipeline(cidx, geo, sx, keys, w.Order);
    }

    static string Row(StateRow r) => $"{r.Kind} {r.Verdict} {r.Member}/{(r.Other is { } o ? o.ToString() : "-")} {r.Population}{(r.SameSet ? " same-set" : "")}{(r.SameAuthor ? " same-author" : "")}";
    static string Rows(IEnumerable<StateRow> rows) => string.Join("; ", rows.Select(Row).OrderBy(x => x, StringComparer.Ordinal));

    public static int Run() => ProbeSelfTest.Run("stateprobe", "StateFixture (switches flipping collision boxes)", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD4-S-F0 the fixture writes Skyrim.esm, Dawnguard.esm, StMod.esp and the slab and cup meshes",
                w.Order.Count == 3 && w.Order.All(File.Exists)
                && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.SlabMesh)) && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.CupMesh)),
                string.Join(", ", w.Order.Select(Path.GetFileName)));

        var pl = Build(w, t.Root);
        // T0: stateprobe/StateGeometry.cs, the probe's host/dependent soup rule.
        var slab = pl.Geometry.Of(F.Slab); var cup = pl.Geometry.Of(F.Cup); var sw = pl.Geometry.Of(F.SwitchBase);
        float Dim(RefGeometry? g) => g is null ? float.NaN : MathF.Max(g.Box.Max.X - g.Box.Min.X, MathF.Max(g.Box.Max.Y - g.Box.Min.Y, g.Box.Max.Z - g.Box.Min.Z));
        t.Check("PD4-S-G1 the slab is a collision host ('c', host usable), about 140 u across",
                slab is { HostSrc: 'c', DepSrc: 'c', HostUsable: true } && Dim(slab) is > 139f and < 141f, $"{slab?.HostSrc}{slab?.DepSrc} usable {slab?.HostUsable}, {Dim(slab):0.##} u");
        t.Check("PD4-S-G2 the cup is collision too, about 56 u across: under the 64 u host minimum",
                cup is { HostSrc: 'c', HostUsable: true } && Dim(cup) is > 55f and < 57f && Dim(cup) < pl.Contact.MinHostDim, $"{cup?.HostSrc}, {Dim(cup):0.##} u");
        t.Check("PD4-S-G3 the switch base (no model, no bounds) has no geometry and is counted",
                sw is null && pl.Geometry.NoGeometry >= 1 && pl.Geometry.MeshesMeasured == 2, $"meshes measured {pl.Geometry.MeshesMeasured}, no geometry {pl.Geometry.NoGeometry}");
        t.Check("PD4-S-W  seven switches StM1-StM7", string.Join(",", pl.Switches.Switches.Values.Select(s => s.Label).OrderBy(x => x)) == "StM1,StM2,StM3,StM4,StM5,StM6,StM7",
                string.Join(",", pl.Switches.Switches.Values.Select(s => s.Label).OrderBy(x => x)));

        RouteCutVerdict? Stub(StateArm arm, FormKey key, bool navcut, out string why)
        { why = "stub"; return key == F.W1 || key == F.W2 || key == F.W3 ? RouteCutVerdict.Blocked : RouteCutVerdict.None; }
        var ev = new StateEvaluator(pl.Contact, pl.Switches) { Route = Stub };
        var byState = new Dictionary<string, List<StateRow>>();
        var all = new List<StateRow>();
        foreach (var s in pl.Switches.Switches.Values.OrderBy(s => s.Key.ToString()))
        {
            var r = ev.Evaluate(new StateArm(s.Label, new[] { s.Key }), pl.IsVanilla, pl.IsUssep, new StateTally());
            byState[s.Label] = r; all.AddRange(r);
        }
        string Of(string state) => Rows(byState[state]);
        string Want(params string[] rows) => string.Join("; ", rows.OrderBy(x => x, StringComparer.Ordinal));
        t.Check("PD4-S-C1 StM1: C1 arrives Sunk on S1, mod set over vanilla",
                Of("StM1") == Want($"Contact StateConditional {F.C1}/{F.S1} ModSetOverVanilla"), Of("StM1"));
        t.Check("PD4-S-C2 StM2: C2 arrives Sunk on S2, Bethesda floor",
                Of("StM2") == Want($"Contact StateConditional {F.C2}/{F.S2} BethesdaFloor"), Of("StM2"));
        t.Check("PD4-S-C3 StM3: two rows, C3b on C3a and C3a enclosing C3b: same set, not same author",
                byState["StM3"].Count == 2 && byState["StM3"].Select(r => r.Member).Distinct().Count() == 2 && byState["StM3"].All(r => r.SameSet && !r.SameAuthor && r.Verdict == StateVerdict.StateConditional
                    && new[] { F.C3a, F.C3b }.Contains(r.Member) && r.Other is { } o && new[] { F.C3a, F.C3b }.Contains(o)), Of("StM3"));
        t.Check("PD4-S-C4 StM4: C4 arrives Sunk on S4, both StMod's: same author",
                Of("StM4") == Want($"Contact StateConditional {F.C4}/{F.S4} ModOnMod same-author"), Of("StM4"));
        t.Check("PD4-S-C5 the stub's route rows: W1 and W3 arrive (StateConditional), W2 leaves (StateResolved)",
                Rows(all.Where(r => r.Kind == StateRowKind.Route)) == Want($"Route StateConditional {F.W1}/- ModOnMod", $"Route StateResolved {F.W2}/- ModOnMod", $"Route StateConditional {F.W3}/- ModRefUnderVanillaSet"),
                Rows(all.Where(r => r.Kind == StateRowKind.Route)));
        t.Check("PD4-S-F5 W3, a Bethesda obstacle under a Bethesda switch, reads ModRefUnderVanillaSet (today's behaviour; task 2026-10-15-stateprobe-vanilla-population)",
                all.Single(r => r.Member == F.W3).Population == StatePopulation.ModRefUnderVanillaSet, all.Single(r => r.Member == F.W3).Population.ToString());
    });
}
