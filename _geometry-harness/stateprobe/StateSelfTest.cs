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

    /// <summary>ST2: stateprobe/RouteObstacle.cs on the fixture's corridors. The obstacle soup and its columns are built the way
    /// the oracle builds them (the actor-solid collision of the winning base's mesh at the reference's pose); the band top is
    /// the oracle's for a cell with no actors and no playable race.</summary>
    static void RouteChecks(ProbeSelfTest t, Pipeline pl)
    {
        foreach (var (id, type, want) in new[] { ("A1", "Door", "a Door: passable by design, set aside"), ("A2", "MoveableStatic", "a MoveableStatic: pushed aside, set aside") })
        {
            bool aside = RouteObstacle.SetAside(type, out var why);
            t.Check($"PD4-S-{id} {type} is set aside: {want}", aside && why == want, $"{aside}: {why}");
        }
        bool staticAside = RouteObstacle.SetAside("Static", out var staticWhy);
        t.Check("PD4-S-A3 a Static is measured", !staticAside && staticWhy == "", $"{staticAside}: '{staticWhy}'");

        var walk = new RouteWalk(new HashSet<FormKey> { F.SB, F.SB2 });
        walk.Seed(pl.Paths[0]);
        foreach (var path in pl.Paths) { var m = RouteWalk.Open(path)!; using (m as IDisposable) walk.Plugin(m); }
        NavmeshIndex Nav(FormKey cell) => NavmeshIndex.Build(walk.WinNav[cell].Values.ToList(), out _);
        var navB = Nav(F.SB); var navB2 = Nav(F.SB2);
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(pl.Paths[0])!, "mods", F.Skyrim.Name, "meshes", F.SlabMesh));
        var model = NifGeometry.Measure(bytes, includeTriangles: true).Model!;
        var solid = model.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer))!;
        var box = Aabb.Empty; foreach (var v in solid.Vertices) box = box.Add(v);
        float top = ActorBand.PhysicalControllerTop(ActorBand.TallestPlayable(new()).Top);
        bool Enters(NavmeshIndex nav, FormKey key)
        {
            var w = pl.Contact.Index.History.Winning(key)!.Value;
            var world = SpatialOverlap.ToWorld(box, w.Pos, w.Rot, w.Scale);
            var columns = MeshColumnIndex.Build(NavcutGeometry.WorldVertices(solid, w.Pos, w.Rot, w.Scale), solid.Indices, triangleMaterials: solid.TriangleMaterials);
            return RouteObstacle.EntersActorColumn(nav, columns, SpatialOverlap.GrowDown(world, top), top);
        }
        t.Check("PD4-S-X1 W1, across the corridor, enters an actor column", Enters(navB, F.W1), "");
        t.Check("PD4-S-X2 Hv, 330 u above the navmesh, never enters", !Enters(navB, F.Hv), "");
        t.Check("PD4-S-X3 O3 stands only over Deleted-flagged triangles: it never enters", !Enters(navB, F.O3), "");
        t.Check("PD4-S-X4 control: O3's pose over the unflagged corridor SB2 enters", Enters(navB2, F.O3), "");
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
        // One row (2026-10-24): the host pass (C3a encloses C3b) and the dependent pass (C3b sits in C3a) read the same pair the same
        // way, to the same depth; they are one clip, and the dependent pass's row is the one kept.
        t.Check("PD4-S-C3 StM3: one row, C3b sunk in C3a (the dependent pass's): same set, not same author",
                byState["StM3"].Count == 1 && byState["StM3"][0] is { SameSet: true, SameAuthor: false, Verdict: StateVerdict.StateConditional, MemberDepends: true, Member: var m3, Other: var o3 }
                    && m3 == F.C3b && o3 == F.C3a, Of("StM3"));
        t.Check("PD4-S-C4 StM4: C4 arrives Sunk on S4, both StMod's: same author",
                Of("StM4") == Want($"Contact StateConditional {F.C4}/{F.S4} ModOnMod same-author"), Of("StM4"));
        t.Check("PD4-S-C5 the stub's route rows: W1 and W3 arrive (StateConditional), W2 leaves (StateResolved)",
                Rows(all.Where(r => r.Kind == StateRowKind.Route)) == Want($"Route StateConditional {F.W1}/- ModOnMod", $"Route StateResolved {F.W2}/- ModOnMod", $"Route StateConditional {F.W3}/- BethesdaFloor"),
                Rows(all.Where(r => r.Kind == StateRowKind.Route)));
        t.Check("PD4-S-F5 W3, a Bethesda obstacle under a Bethesda switch, reads BethesdaFloor: the switch is the route row's other side",
                all.SingleOrDefault(r => r.Member == F.W3)?.Population == StatePopulation.BethesdaFloor, all.SingleOrDefault(r => r.Member == F.W3)?.Population.ToString() ?? "no W3 row");

        // ST4 and ST1: stateprobe/StateFindings.cs over every state's rows.
        var split = StateFindings.Of(all);
        t.Check("PD4-S-K  the class is exactly C1, C2, W1 and W3; resolved exactly W2; own floor exactly C4; the C3 rows in none",
                string.Join(" ", split.Class.Select(r => r.Member.ToString()).OrderBy(x => x)) == string.Join(" ", new[] { F.C1, F.C2, F.W1, F.W3 }.Select(k => k.ToString()).OrderBy(x => x))
                && split.Resolved.Select(r => r.Member).SequenceEqual(new[] { F.W2 }) && split.OwnFloor.Select(r => r.Member).SequenceEqual(new[] { F.C4 }),
                $"class {Rows(split.Class)} | resolved {Rows(split.Resolved)} | own floor {Rows(split.OwnFloor)}");
        var emitted = StateFindings.Emitted(split.Class).ToList();
        RouteChecks(t, pl);
        t.Check("PD4-S-E  the emit carries C1 and W1; C2 and W3 (Bethesda floor) are withheld",
                string.Join(" ", emitted.Select(r => r.Member.ToString()).OrderBy(x => x)) == string.Join(" ", new[] { F.C1, F.W1 }.Select(k => k.ToString()).OrderBy(x => x)),
                Rows(emitted));
    });
}
