using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// `navownprobe --navrepair-selftest`: NavRepair's plan on small in-memory navmeshes, for the shapes the plugin fixture does
// not carry (tests/test_navmesh_repair_rules.py runs it and reads each line). Each case prints "ok NAME" or "FAIL NAME: why".
static class NavRepairSelfTest
{
    static readonly ModKey M = new("SelfTest", ModType.Master);
    static FormKey K(uint id) => new(M, id);
    static readonly EdgeTolerance Tol = new(8, 63), Wide = new(16, 128);

    static WinningNav Nav(FormKey k, Vec3[] v, NavTri[] t, NavEdgeLink[] links)
    {
        var used = t.SelectMany(x => new[] { (int)x.A, x.B, x.C }).Distinct().Select(i => v[i]).ToList();
        var g = new NavmeshGeometry(k, v, t, new Vec3(used.Min(p => p.X), used.Min(p => p.Y), used.Min(p => p.Z)),
                                    new Vec3(used.Max(p => p.X), used.Max(p => p.Y), used.Max(p => p.Z)), links);
        return new WinningNav(g, "wrld:000800:SelfTest.esm", (0, 0), FormKey.Null);
    }

    /// <summary>Island data with the navmesh's box and an empty mesh.</summary>
    static NavIslandData BoxOnly(NavmeshGeometry g) => new(g.Min, g.Max, Array.Empty<Vec3>(), Array.Empty<(short, short, short)>());

    public static int Run()
    {
        int fail = 0;
        void Case(string name, Func<string?> body)
        {
            string? why;
            try { why = body(); } catch (Exception ex) { why = $"threw {ex.GetType().Name}: {ex.Message}"; }
            Console.WriteLine(why is null ? $"ok {name}" : $"FAIL {name}: {why}");
            if (why is not null) fail++;
        }

        // C2490: a cut takes its table entry, and a later entry's edge field is renumbered to follow it.
        Case("2490 cut-renumbers", () =>
        {
            var a = K(0xA01); var b = K(0xA02); var gone = K(0xA09);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                // A: two triangles; tri 0 edge 1-2 links a navmesh that is not there (entry 0), tri 1 edge 1-2 links B (entry 1)
                [a] = Nav(a, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0), new Vec3(0, 200, 0), new Vec3(100, 200, 0), new Vec3(100, 300, 0) },
                          new[] { new NavTri(0, 1, 2, 2, -1, 0, -1), new NavTri(3, 4, 5, 2, -1, 1, -1) },
                          new[] { new NavEdgeLink(gone, 0), new NavEdgeLink(b, 0) }),
                [b] = Nav(b, new[] { new Vec3(100, 200, 0), new Vec3(200, 200, 0), new Vec3(100, 300, 0) },
                          new[] { new NavTri(0, 1, 2, 4, -1, -1, 0) }, new[] { new NavEdgeLink(a, 1) }),
            };
            var plan = NavRepair.Relink(nav, new[] { new BrokenLink("EDGE-NAV", a, gone, 0) }, Tol, Wide);
            var p = plan.Plans.Single(x => x.Nav == a);
            var ops = p.Edits.Select(o => $"{o.Op} {o.FieldPath} {o.Key} {o.Value}").ToList();
            string[] want = { "Remove Data.EdgeLinks 0 ", "Set Data.Triangles[0].EdgeLink_1_2  -1", "Set Data.Triangles[1].EdgeLink_1_2  0", "Remove Data.Triangles[0].Flags  EdgeLink_1_2" };
            if (p.Status != "cut") return $"status {p.Status}: {string.Join("; ", p.Why)}";
            if (!want.SequenceEqual(ops)) return "ops " + string.Join(" | ", ops);
            var after = NavRepair.Apply(nav[a].Geom, p.Change);
            return after.EdgeLinks!.Count == 1 && after.EdgeLinks[0].Mesh == b && after.Triangles[1].EdgeLink12 == 0 ? null : "the applied table does not keep tri 1's link to B";
        });

        // The ground pair: G's ground moved 200 u up, H's did not; they meet along x = 100, linked both ways.
        (Dictionary<FormKey, WinningNav> Nav, FormKey G, FormKey H) Pair()
        {
            var g = K(0xB01); var h = K(0xB02);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                [g] = Nav(g, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0), new Vec3(0, 100, 0) },
                          new[] { new NavTri(0, 1, 2, 2, -1, 0, 1), new NavTri(0, 2, 3, 0, 0, -1, -1) }, new[] { new NavEdgeLink(h, 0) }),
                [h] = Nav(h, new[] { new Vec3(100, 0, 0), new Vec3(200, 0, 0), new Vec3(100, 100, 0) },
                          new[] { new NavTri(0, 1, 2, 4, -1, -1, 0) }, new[] { new NavEdgeLink(g, 0) }),
            };
            return (nav, g, h);
        }
        static GroundRow Moved(FormKey k, int n, float by) => new(k, Enumerable.Range(0, n).Select(i => new GroundVertex(i, 0, by, false)).ToList());

        // C2491: a height move that would leave a matched link 200 u apart is refused, naming the link.
        Case("2491 ground-keeps-links", () =>
        {
            var (nav, g, h) = Pair();
            var p = NavRepair.Ground(nav, new[] { Moved(g, 4, 200) }, 38, Tol).Plans.Single();
            return p.Status == "refused" && p.Why.Any(w => w.Contains("break") && w.Contains(h.ToString())) && p.Edits.Count == 0
                ? null : $"status {p.Status}, edits {p.Edits.Count}: {string.Join("; ", p.Why)}";
        });

        // C2492: the same move with the neighbour's ground moving too is planned for both, links still meeting.
        Case("2492 ground-moves-together", () =>
        {
            var (nav, g, h) = Pair();
            var plan = NavRepair.Ground(nav, new[] { Moved(g, 4, 200), Moved(h, 3, 200) }, 38, Tol);
            var bad = plan.Plans.Where(p => p.Status != "planned").Select(p => $"{p.Nav} {p.Status}: {string.Join("; ", p.Why)}").ToList();
            return bad.Count == 0 && plan.Plans.Count == 2 ? null : string.Join(" | ", bad);
        });

        // C2493: a vertex that cannot be told refuses the navmesh rather than tear it.
        Case("2493 ground-cannot-tell", () =>
        {
            var (nav, g, _) = Pair();
            var row = new GroundRow(g, new[] { new GroundVertex(0, 0, 200, false), new GroundVertex(1, 0, 200, false), new GroundVertex(2, 0, 200, false), new GroundVertex(3, float.NaN, float.NaN, true) });
            var p = NavRepair.Ground(nav, new[] { row }, 38, Tol).Plans.Single();
            return p.Status == "refused" && p.Why.Any(w => w.Contains("cannot be told")) ? null : $"status {p.Status}: {string.Join("; ", p.Why)}";
        });

        // C2506: a relink into another navmesh whose NAVI entry no plugin states is refused by name, and so is its link back;
        // with both entries stated, the same relink is planned and names each navmesh in the other's entry.
        Case("2506 navi-entry-missing", () =>
        {
            var a = K(0xC01); var c = K(0xC02); var gone = K(0xC09);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                [a] = Nav(a, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 2, -1, 0, -1) }, new[] { new NavEdgeLink(gone, 0) }),
                [c] = Nav(c, new[] { new Vec3(100, 0, 0), new Vec3(200, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>()),
            };
            static Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap Map(params FormKey[] navs)
            {
                var m = new Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap(K(0xC00), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                foreach (var n in navs) m.MapInfos.Add(new Mutagen.Bethesda.Skyrim.NavigationMapInfo { NavigationMesh = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(n) });
                return m;
            }
            var broken = new[] { new BrokenLink("EDGE-NAV", a, gone, 0) };
            var plan = NavRepair.Relink(nav, broken, Tol, Wide);
            if (plan.Plans.Count(p => p.Status == "planned") != 2) return "the relink itself is not planned: " + string.Join(" | ", plan.Plans.Select(p => $"{p.Nav} {p.Status}"));
            var n1 = NavRepairNavi.Plan(plan, nav, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", Map(a)) }));
            var bad = plan.Plans.Where(p => p.Status != "refused" || !p.Why.Any(w => w.Contains("NAVI") && (p.Nav == c ? w.Contains(c.ToString()) : true))).ToList();
            if (bad.Count > 0 || n1.Ops.Count > 0) return "with C's entry missing: " + string.Join(" | ", plan.Plans.Select(p => $"{p.Nav} {p.Status}: {string.Join("; ", p.Why)}")) + $"; NAVI ops {n1.Ops.Count}";
            plan = NavRepair.Relink(nav, broken, Tol, Wide);
            var n2 = NavRepairNavi.Plan(plan, nav, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", Map(a, c)) }));
            var got = n2.Ops.Select(o => $"{o.FieldPath} {o.Op} {o.Value}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            string[] want = { $"MapInfos[0].MergedTo Add {c}", $"MapInfos[1].MergedTo Add {a}" };
            return plan.Plans.All(p => p.Status == "planned") && want.SequenceEqual(got) ? null : "with both entries: " + string.Join(" | ", got);
        });

        // C2566: NAVI linked doors (NavRepairNavi.ForDoors, navedit's door edit). On the NAVI winner's entry the door is added in
        // place, with the base game's Unknown; a door the entry already links, and a navmesh no NAVI states, are refused; an
        // entry only an earlier plugin states, an interior one, is refused naming the write engine's gap.
        Case("2566 navi-linked-doors", () =>
        {
            FormKey nv = K(0xA70), other = K(0xA71), door = K(0xD70);
            Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap MapWith(bool linked, bool cellParent, params FormKey[] navs)
            {
                var m = new Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap(K(0xC00), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                foreach (var n in navs)
                {
                    var e = new Mutagen.Bethesda.Skyrim.NavigationMapInfo { NavigationMesh = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(n) };
                    if (linked) e.LinkedDoors.Add(new Mutagen.Bethesda.Skyrim.LinkedDoor { Unknown = NavRepairNavi.LinkedDoorUnknown, Door = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>(door) });
                    if (cellParent) e.Parent = new Mutagen.Bethesda.Skyrim.NavigationMapInfoCellParent { ParentCell = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.ICellGetter>(K(0xCE1)) };
                    m.MapInfos.Add(e);
                }
                return m;
            }
            var (inPlace, r1) = NavRepairNavi.ForDoors(nv, new[] { door }, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", MapWith(false, false, other, nv)) }));
            if (r1 is not null) return "in place refused: " + r1;
            var op = inPlace.Ops.Single();
            if (op.FieldPath != "MapInfos[1].LinkedDoors" || op.Op != "Add" || (string?)op.ComposeNode!["fields"]!["Door"] != door.ToString() || (string?)op.ComposeNode["fields"]!["Unknown"] != NavRepairNavi.LinkedDoorUnknown.ToString())
                return $"op {op.FieldPath} {op.Op} {op.ComposeNode?.ToJsonString()}";
            if (!inPlace.Entries.Single().AddDoors.SequenceEqual(new[] { door }) || inPlace.Entries.Single().WinnerIndex != 1) return "the entry change does not name the door at winner index 1";
            var (_, dup) = NavRepairNavi.ForDoors(nv, new[] { door }, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", MapWith(true, false, nv)) }));
            var (_, none) = NavRepairNavi.ForDoors(nv, new[] { door }, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", MapWith(false, false, other)) }));
            var (_, interior) = NavRepairNavi.ForDoors(nv, new[] { door }, NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", MapWith(false, true, nv)), new NaviSource("Later.esp", "", MapWith(false, false, other)) }));
            return dup?.Contains("already links") == true && none?.Contains("no plugin's NAVI states") == true && interior?.Contains("cannot yet compose a whole interior entry") == true
                ? null : $"duplicate: {dup}; unstated: {none}; interior: {interior}";
        });

        // C2605: the NAVI change for a carried version (NavRepairNavi.ForVersion, houseCARL's item 2). The entry's linked doors
        // become the version's door table, in order: its own doors are kept while they already stand so (the same door, the base
        // game's Unknown), the rest removed from the back and the table's rest appended. An entry already standing so is left
        // alone; one with another Unknown is rewritten from there; an entry only an earlier plugin states is added whole with the
        // table; a whole interior entry, and a navmesh no NAVI states that has doors, are refused by name; a gained neighbour is
        // added both ways.
        Case("2605 navi-for-version", () =>
        {
            FormKey nv = K(0xA80), m = K(0xA81), dA = K(0xD81), dB = K(0xD82), dC = K(0xD83);
            Mutagen.Bethesda.Skyrim.NavigationMapInfo Entry(FormKey nav, bool cell, params (FormKey Door, int Unknown)[] ds)
            {
                var e = new Mutagen.Bethesda.Skyrim.NavigationMapInfo { NavigationMesh = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(nav) };
                foreach (var (d, u) in ds) e.LinkedDoors.Add(new Mutagen.Bethesda.Skyrim.LinkedDoor { Unknown = u, Door = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>(d) });
                if (cell) e.Parent = new Mutagen.Bethesda.Skyrim.NavigationMapInfoCellParent { ParentCell = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.ICellGetter>(K(0xCE1)) };
                else e.Parent = new Mutagen.Bethesda.Skyrim.NavigationMapInfoWorldParent { ParentWorldspace = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IWorldspaceGetter>(K(0x800)) };
                return e;
            }
            NaviView View(params (string Plugin, Mutagen.Bethesda.Skyrim.NavigationMapInfo[] Infos)[] srcs) => NaviView.Of(srcs.Select(s =>
            {
                var map = new Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap(K(0xC00), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                map.MapInfos.AddRange(s.Infos);
                return new NaviSource(s.Plugin, "", map);
            }).ToList());
            const int U = NavRepairNavi.LinkedDoorUnknown;
            // the door cases name no neighbours: the version's link targets are none beyond what is gained
            (NaviPlan, string?) FV(FormKey k, IReadOnlyList<FormKey> doors, IReadOnlyCollection<FormKey> gained, NaviView v) => NavRepairNavi.ForVersion(k, doors, Array.Empty<FormKey>(), gained, v);
            string Ops(NaviPlan p) => string.Join(" ", p.Ops.Select(o => $"{o.FieldPath}:{o.Op}" + (o.Key is { } k ? $"@{k}" : "") + (o.ComposeNode?["fields"]?["Door"] is { } d ? $"={FormKey.Factory((string)d!).ID:X}" : o.Value is { } v ? $"={FormKey.Factory(v).ID:X}" : "")));
            var bad = new List<string>();
            // in place: [A, B, C] -> [A, C, B]: A kept, C and B removed from the back (2, then 1), C and B appended. Two removals,
            // so their order shows (corrected after the first arms run: with one removal the order could not be seen)
            var (p1, r1) = FV(nv, new[] { dA, dC, dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(m, false), Entry(nv, false, (dA, U), (dB, U), (dC, U)) })));
            if (r1 is not null) bad.Add("in place refused: " + r1);
            else if (Ops(p1) != "MapInfos[1].LinkedDoors:Remove@2 MapInfos[1].LinkedDoors:Remove@1 MapInfos[1].LinkedDoors:Add=D83 MapInfos[1].LinkedDoors:Add=D82") bad.Add("in place ops " + Ops(p1));
            else if (p1.Entries.Single() is var e1 && (e1.KeepDoors != 1 || e1.DoorsAfter is null || !e1.DoorsAfter.SequenceEqual(new[] { dA, dC, dB }) || e1.WinnerIndex != 1)) bad.Add($"in place entry keep {p1.Entries[0].KeepDoors}");
            // a strict prefix ([A, B, C] -> [A]), an empty table on linked doors ([A, B] -> []), a repeated door ([A] -> [A, A])
            var (q1, _) = FV(nv, new[] { dA }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U), (dB, U), (dC, U)) })));
            var (q2, _) = FV(nv, Array.Empty<FormKey>(), Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U), (dB, U)) })));
            var (q3, _) = FV(nv, new[] { dA, dA }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U)) })));
            if (Ops(q1) != "MapInfos[0].LinkedDoors:Remove@2 MapInfos[0].LinkedDoors:Remove@1") bad.Add("prefix ops " + Ops(q1));
            if (Ops(q2) != "MapInfos[0].LinkedDoors:Remove@1 MapInfos[0].LinkedDoors:Remove@0") bad.Add("empty table ops " + Ops(q2));
            if (Ops(q3) != "MapInfos[0].LinkedDoors:Add=D81") bad.Add("repeated door ops " + Ops(q3));
            // already standing so: nothing
            var (p2, r2) = FV(nv, new[] { dA, dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U), (dB, U)) })));
            if (r2 is not null || p2.Entries.Count != 0 || p2.Ops.Count != 0) bad.Add($"already following: refused {r2}, {p2.Entries.Count} entries");
            // another Unknown on the first door: rewritten from there
            var (p3, r3) = FV(nv, new[] { dA, dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, 7), (dB, U)) })));
            if (r3 is not null) bad.Add("other Unknown refused: " + r3);
            if (Ops(p3) != "MapInfos[0].LinkedDoors:Remove@1 MapInfos[0].LinkedDoors:Remove@0 MapInfos[0].LinkedDoors:Add=D81 MapInfos[0].LinkedDoors:Add=D82") bad.Add("other Unknown ops " + Ops(p3));
            // stated only by an earlier plugin: added whole, its linked doors the table
            var (p4, r4) = FV(nv, new[] { dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U)) }), ("Later.esp", new[] { Entry(m, false) })));
            var sets = p4.Ops.SingleOrDefault()?.ComposeNode?["sets"]?.AsArray().Where(x => (string?)x!["path"] == "LinkedDoors").Select(x => (string?)x!["compose"]!["fields"]!["Door"]).ToList();
            if (r4 is not null || p4.Ops.Count != 1 || p4.Ops[0].FieldPath != "MapInfos" || sets is null || !sets.SequenceEqual(new[] { dB.ToString() })) bad.Add($"whole entry: refused {r4}, ops {Ops(p4)}, doors [{string.Join(",", sets ?? new())}]");
            // a whole entry keeping its first door: [A, B] -> [A, C]
            var (p4k, _) = FV(nv, new[] { dA, dC }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, false, (dA, U), (dB, U)) }), ("Later.esp", new[] { Entry(m, false) })));
            var setsK = p4k.Ops.SingleOrDefault()?.ComposeNode?["sets"]?.AsArray().Where(x => (string?)x!["path"] == "LinkedDoors").Select(x => (string?)x!["compose"]!["fields"]!["Door"]).ToList();
            if (setsK is null || !setsK.SequenceEqual(new[] { dA.ToString(), dC.ToString() }) || p4k.Entries.Single().KeepDoors != 1) bad.Add($"whole entry keeping one: doors [{string.Join(",", setsK ?? new())}]");
            // a whole interior entry, and an unstated navmesh with doors: refused; unstated with no doors and nothing gained: nothing
            var (_, r5) = FV(nv, new[] { dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(nv, true, (dA, U)) }), ("Later.esp", new[] { Entry(m, false) })));
            var (_, r6) = FV(nv, new[] { dB }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(m, false) })));
            var (p7, r7) = FV(nv, Array.Empty<FormKey>(), Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { Entry(m, false) })));
            if (r5?.Contains("cannot yet compose a whole interior entry") != true || r6?.Contains("no plugin's NAVI states") != true || r7 is not null || p7.Entries.Count != 0)
                bad.Add($"refusals: interior '{r5}', unstated '{r6}', nothing '{r7}' ({p7.Entries.Count} entries)");
            // a gained neighbour, both ways, with the door table on the carried navmesh
            var (p8, _) = FV(nv, new[] { dA }, new[] { m }, View(("SelfTest.esm", new[] { Entry(m, false), Entry(nv, false) })));
            // entries in FormKey order: the carried navmesh (A80, entry 1) first, then its neighbour (A81, entry 0)
            if (Ops(p8) != "MapInfos[1].MergedTo:Add=A81 MapInfos[1].LinkedDoors:Add=D81 MapInfos[0].MergedTo:Add=A80") bad.Add("gained ops " + Ops(p8));
            // item 3: the entry's neighbour lists become the version's link targets. [x] in MergedTo and [y] in PreferredMerges,
            // the version linking into m only: m added, x and y removed by value from the list naming each
            FormKey x = K(0xA82), y = K(0xA83), z = K(0xA84);
            var withNbrs = Entry(nv, false); withNbrs.MergedTo.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(x));
            withNbrs.PreferredMerges.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(y));
            var (p9, r9) = NavRepairNavi.ForVersion(nv, Array.Empty<FormKey>(), new[] { m }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { withNbrs })));
            if (r9 is not null || Ops(p9) != "MapInfos[0].MergedTo:Remove=A82 MapInfos[0].PreferredMerges:Remove=A83 MapInfos[0].MergedTo:Add=A81"
                || !p9.Entries.Single().Remove.SequenceEqual(new[] { x }) || !p9.Entries.Single().RemovePreferred.SequenceEqual(new[] { y }))
                bad.Add($"neighbours: refused {r9}, ops {Ops(p9)}");
            // duplicates and both lists: MergedTo [x, x, m] and PreferredMerges [x, y], the version linking into m and y: x removed
            // twice from MergedTo and once from PreferredMerges, y kept where it is (PreferredMerges), m kept, nothing added
            var dup = Entry(nv, false);
            foreach (var k in new[] { x, x, m }) dup.MergedTo.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(k));
            foreach (var k in new[] { x, y }) dup.PreferredMerges.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(k));
            var (p12, _) = NavRepairNavi.ForVersion(nv, Array.Empty<FormKey>(), new[] { m, y }, Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { dup })));
            if (Ops(p12) != "MapInfos[0].MergedTo:Remove=A82 MapInfos[0].MergedTo:Remove=A82 MapInfos[0].PreferredMerges:Remove=A82") bad.Add("duplicates and both lists: ops " + Ops(p12));
            // a gained neighbour's entry is never trimmed: m names z, which K's version does not; m only gains K
            var mWithZ = Entry(m, false); mWithZ.MergedTo.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(z));
            var (p10, _) = NavRepairNavi.ForVersion(nv, Array.Empty<FormKey>(), new[] { m }, new[] { m }, View(("SelfTest.esm", new[] { Entry(nv, false), mWithZ })));
            if (Ops(p10) != "MapInfos[0].MergedTo:Add=A81 MapInfos[1].MergedTo:Add=A80") bad.Add("neighbour trimmed? ops " + Ops(p10));
            // a whole entry drops a neighbour too: an earlier plugin's entry names x; the version links into nothing
            var earlier = Entry(nv, false); earlier.MergedTo.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(x));
            var (p11, _) = NavRepairNavi.ForVersion(nv, Array.Empty<FormKey>(), Array.Empty<FormKey>(), Array.Empty<FormKey>(), View(("SelfTest.esm", new[] { earlier }), ("Later.esp", new[] { Entry(m, false) })));
            var merged11 = p11.Ops.SingleOrDefault()?.ComposeNode?["sets"]?.AsArray().Where(s => (string?)s!["path"] == "MergedTo").ToList();
            if (p11.Ops.Count != 1 || p11.Ops[0].FieldPath != "MapInfos" || merged11 is null || merged11.Count != 0) bad.Add($"whole entry drop: ops {Ops(p11)}, MergedTo sets {merged11?.Count}");
            return bad.Count == 0 ? null : string.Join(" | ", bad);
        });

        // ---- the renumbering-proof row key (RowsAt), navownprobe's --base summary for a navedit patch ----
        string A = K(0xA01).ToString(), B = K(0xB01).ToString(), Z = K(0xC01).ToString();
        Conflict Link(string kind, int srcTri, string? srcAt, int tgtTri, string? tgtAt, string src = "", string tgt = "") =>
            new(kind, "RpA.esp", "Skyrim.esm", "a link", new[] { new ConflictRecord(src.Length > 0 ? src : A, "navmesh", null, Triangle: srcTri, At: srcAt) },
                new[] { new ConflictRecord(tgt.Length > 0 ? tgt : B, "navmesh", null, Triangle: tgtTri, At: tgtAt) });

        // C2551: one broken link, its two triangles renumbered by a patch (source 5 -> 0, target 7 -> 2) but standing where
        // they stood: the row is unchanged by place, though its RowIdentity (by target index) changed.
        Case("2551 rows-at-renumbered", () =>
        {
            var before = new[] { Link("EDGE-IDX", 5, "10.00,20.00,0.00", 7, "110.00,20.00,0.00") };
            var after = new[] { Link("EDGE-IDX", 0, "10.00,20.00,0.00", 2, "110.00,20.00,0.00") };
            var (gone, fresh, same) = RowsAt.Compare(before, after, A);
            string Id(Conflict c) => ConflictIo.RowIdentity(c.Kind, c.Necessary[0].FormKey, c.Alternative![0].FormKey, c.A, c.Alternative[0].Triangle);
            if (Id(before[0]) == Id(after[0])) return "the index identity did not change, so this case shows nothing";
            return gone.Count == 0 && fresh.Count == 0 && same.Count == 1 ? null : $"gone {gone.Count}, new {fresh.Count}, unchanged {same.Count}";
        });

        // C2552: a row whose source triangle the patch replaced (a cut: another place) counts gone; a new finding counts new;
        // a row on another navmesh (Z to B) is not counted for A; a row with no triangles (CLOBBERED A) is matched by its
        // records.
        Case("2552 rows-at-gone-new-other", () =>
        {
            var clob = new Conflict("CLOBBERED", "RpA.esp", "Skyrim.esm", "re-cut", new[] { new ConflictRecord(A, "navmesh", null) });
            var before = new[] { Link("EDGE-NOMATCH", 3, "10.00,20.00,0.00", 7, "110.00,20.00,0.00"), Link("EDGE-IDX", 1, "1.00,1.00,0.00", 9, null, Z), clob };
            var after = new[] { Link("EDGE-NOMATCH", 3, "12.50,20.00,0.00", 7, "110.00,20.00,0.00"), Link("EDGE-ONEWAY", 4, "40.00,0.00,0.00", 1, "140.00,0.00,0.00"),
                                Link("EDGE-IDX", 1, "1.00,1.00,0.00", 9, null, Z), clob };
            var (gone, fresh, same) = RowsAt.Compare(before, after, A);
            var (zg, zn, zs) = RowsAt.Compare(before, after, Z);
            return gone.Count == 1 && fresh.Count == 2 && same.Count == 1 && same[0].StartsWith("CLOBBERED") && zg.Count == 0 && zn.Count == 0 && zs.Count == 1
                ? null : $"A: gone {gone.Count}, new {fresh.Count}, unchanged {string.Join(",", same)}; Z: {zg.Count}/{zn.Count}/{zs.Count}";
        });

        // C2555: a CLOBBERED row lists every navmesh of its plugin pair: A's finding is unchanged when the pair's group loses Z,
        // and gone (one new) when another pair now clobbers A; a NAV-GROUND row whose owner became the patch is unchanged.
        Case("2555 rows-at-grouped-rows", () =>
        {
            Conflict Clob(string a, string b, params string[] navs) => new("CLOBBERED", a, b, "re-cut", navs.Select(n => new ConflictRecord(n, "navmesh", null)).ToList());
            Conflict Ground(string owner) => new("NAV-GROUND", owner, "Land.esp", "off the ground", new[] { new ConflictRecord(A, "navmesh", null) });
            var same = RowsAt.Compare(new[] { Clob("U.esp", "N.esp", A, Z), Ground("Skyrim.esm") }, new[] { Clob("U.esp", "N.esp", A), Ground("Patch.esp") }, A);
            var moved = RowsAt.Compare(new[] { Clob("U.esp", "N.esp", A) }, new[] { Clob("N.esp", "Patch.esp", A) }, A);
            return same.Gone.Count == 0 && same.New.Count == 0 && same.Same.Count == 2 && moved.Gone.Count == 1 && moved.New.Count == 1
                ? null : $"same pair {same.Gone.Count}/{same.New.Count}/{same.Same.Count}; another pair {moved.Gone.Count}/{moved.New.Count}";
        });

        // C2553: a base emit whose link rows carry no place (written before places) is refused in one sentence, not compared
        // by index; one with places is not.
        Case("2553 rows-at-unplaced-base", () =>
        {
            var old = new[] { Link("EDGE-IDX", 5, null, 7, null) };
            var placed = new[] { Link("EDGE-IDX", 5, "10.00,20.00,0.00", 7, null) };
            return RowsAt.Unplaced(old) is { } why && why.Contains("emit it again") && RowsAt.Unplaced(placed) is null ? null : "old base not refused, or a placed one refused";
        });

        // C2590: a border link whose far edge lies along it within the wider band (75 u in height, the RV station of
        // 2026-11-22) is "unproven" only while the far triangle links back; with no link back it is EDGE-ONEWAY, a finding.
        // In game (SkyWright overnight lab, Ruunvald 079A55 <-> 0FFE80): such a link walked 3 of 3 forward and 0 of 3 back
        // where the far side had lost its link, 3 of 3 both ways where it had it.
        Case("2590 band-link-without-link-back", () =>
        {
            // each navmesh carries an unrelated triangle 0 first, so the border triangles are index 1 on both sides
            var a = K(0xC01); var b = K(0xC02);
            WinningNav A() => Nav(a, new[] { new Vec3(0, 500, 0), new Vec3(50, 500, 0), new Vec3(0, 550, 0), new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) },
                                  new[] { new NavTri(0, 1, 2, 0), new NavTri(3, 4, 5, 2, -1, 0, -1) }, new[] { new NavEdgeLink(b, 1) });
            // back: 1 links A's triangle 1 (both ways), 0 links nothing, 2 links A's unrelated triangle 0 instead
            WinningNav B(int back) => Nav(b, new[] { new Vec3(300, 500, 75), new Vec3(350, 500, 75), new Vec3(300, 550, 75), new Vec3(100, 0, 75), new Vec3(200, 0, 75), new Vec3(100, 100, 75) },
                                          new[] { new NavTri(0, 1, 2, 0), back == 0 ? new NavTri(3, 4, 5, 0, -1, -1, -1) : new NavTri(3, 4, 5, 4, -1, -1, 0) },
                                          back == 0 ? Array.Empty<NavEdgeLink>() : new[] { new NavEdgeLink(a, (short)(back == 1 ? 1 : 0)) });
            NavBorderLinks F(int back) => NavBorderLinks.Find(new Dictionary<FormKey, WinningNav> { [a] = A(), [b] = B(back) }, Tol, Wide);
            string Show(NavBorderLinks r) => string.Join("; ", r.Rows.Select(x => $"{x.Kind}/{x.Tier} {x.Src}#{x.SrcTri}->{x.Target}#{x.TargetTri}"));
            var both = F(1);
            // linked both ways, each side's link lies in the band: two unproven rows, no finding; the census knows they link back
            if (!(both.Rows.Count == 2 && both.Rows.All(x => x.Kind == "EDGE-NOMATCH" && x.Tier == "unproven")))
                return $"with the link back, want an unproven EDGE-NOMATCH each way and no finding, got [{Show(both)}]";
            if (!both.Judged.Where(c => !c.Matched).All(c => c.Reciprocal)) return "the census does not record that the band links link back";
            foreach (var (back, word) in new[] { (0, "links nothing back"), (2, "instead") })
            {
                var o = F(back).Rows.Where(x => x.Src == a).ToList();
                if (!(o.Count == 1 && o[0].Kind == "EDGE-ONEWAY" && o[0].Tier == "finding" && o[0].SrcTri == 1 && o[0].TargetTri == 1
                      && o[0].Why.Contains("75 u in height") && o[0].Why.Contains(word)))
                    return $"far side {(back == 0 ? "linking nothing" : "linking another triangle")}: want one EDGE-ONEWAY finding 1->1 naming the 75 u height and '{word}', got [{Show(F(back))}]";
            }
            // outside the band (130 u apart) no one-way is said: it stays a broken link (EDGE-NOMATCH finding) as before
            var farOff = NavBorderLinks.Find(new Dictionary<FormKey, WinningNav> { [a] = A(), [b] = Nav(b, new[] { new Vec3(300, 500, 130), new Vec3(350, 500, 130), new Vec3(300, 550, 130), new Vec3(100, 0, 130), new Vec3(200, 0, 130), new Vec3(100, 100, 130) },
                new[] { new NavTri(0, 1, 2, 0), new NavTri(3, 4, 5, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>()) }, Tol, Wide);
            var fo = farOff.Rows.Where(x => x.Src == a).ToList();
            return fo.Count == 1 && fo[0].Kind == "EDGE-NOMATCH" && fo[0].Tier == "finding" ? null : $"130 u apart: want one EDGE-NOMATCH finding, got [{Show(farOff)}]";
        });

        // C2598: a band link with no link back, where another navmesh's open edge lies along it within the tolerance, is a stale
        // link (EDGE-NOMATCH finding naming that edge), not EDGE-ONEWAY (review 3, item 8); without that edge it stays one way.
        Case("2598 band-oneway-yields-to-in-tolerance-match", () =>
        {
            var a = K(0xC41); var b = K(0xC42); var c = K(0xC43);
            var A = Nav(a, new[] { new Vec3(0, 500, 0), new Vec3(50, 500, 0), new Vec3(0, 550, 0), new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) },
                        new[] { new NavTri(0, 1, 2, 0), new NavTri(3, 4, 5, 2, -1, 0, -1) }, new[] { new NavEdgeLink(b, 1) });
            // B's triangle 1 lies 75 u up along A's edge (the band) and links nothing back
            var B = Nav(b, new[] { new Vec3(300, 500, 75), new Vec3(350, 500, 75), new Vec3(300, 550, 75), new Vec3(100, 0, 75), new Vec3(200, 0, 75), new Vec3(100, 100, 75) },
                        new[] { new NavTri(0, 1, 2, 0), new NavTri(3, 4, 5, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>());
            // C's triangle 0 has an open edge (100,100)-(100,0) at A's height: the re-cut far side
            var C = Nav(c, new[] { new Vec3(100, 0, 0), new Vec3(200, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>());
            string Show(NavBorderLinks r) => string.Join("; ", r.Rows.Select(x => $"{x.Kind}/{x.Tier} {x.Src}#{x.SrcTri}->{x.Target}#{x.TargetTri}: {x.Why}"));
            var with = NavBorderLinks.Find(new Dictionary<FormKey, WinningNav> { [a] = A, [b] = B, [c] = C }, Tol, Wide);
            var w = with.Rows.Where(x => x.Src == a).ToList();
            if (!(w.Count == 1 && w[0].Kind == "EDGE-NOMATCH" && w[0].Tier == "finding" && w[0].Why.Contains($"a matching edge: {c} triangle 0")))
                return $"with C's matching edge: want one EDGE-NOMATCH finding naming {c} triangle 0, got [{Show(with)}]";
            var without = NavBorderLinks.Find(new Dictionary<FormKey, WinningNav> { [a] = A, [b] = B }, Tol, Wide);
            var o = without.Rows.Where(x => x.Src == a).ToList();
            return o.Count == 1 && o[0].Kind == "EDGE-ONEWAY" ? null : $"without C: want EDGE-ONEWAY, got [{Show(without)}]";
        });

        // C2603: a use_version row shares the plan's patch only with rows that touch nothing it changes or was matched against
        // (review of slice B, 2026-11-22, finding 1: a neighbour whose link already fits the version is matched against, not
        // changed, and its relink planned on the old winner went unchecked).
        Case("2603 use-version-clash", () =>
        {
            FormKey gm = K(0xD01), b = K(0xD02), c = K(0xD03), other = K(0xD04);
            var none = new Dictionary<FormKey, FormKey>();
            string? Clash(IEnumerable<FormKey> ch, IEnumerable<FormKey> around, ISet<FormKey> planned, IReadOnlyDictionary<FormKey, FormKey>? claimed = null, IReadOnlyDictionary<FormKey, FormKey>? matched = null)
                => NavRepairRun.UseVersionClash(gm, "City.esp", ch, around, planned, claimed ?? none, matched ?? none);
            var bad = new List<string>();
            var x0 = Clash(new[] { gm }, new[] { b }, new HashSet<FormKey> { other });
            if (x0 is not null) bad.Add("disjoint rows refused: " + x0);
            var x1 = Clash(new[] { gm }, new[] { b }, new HashSet<FormKey> { b });
            if (x1 is null || !x1.Contains($"is matched against {b}") || !x1.Contains("a relink or ground row"))
                bad.Add("a relink of a neighbour it was matched against is not refused by name: " + x1);
            var x2 = Clash(new[] { gm, c }, Array.Empty<FormKey>(), new HashSet<FormKey> { c });
            if (x2 is null || !x2.Contains($"changes {c}"))
                bad.Add("a navmesh it changes, also a relink's, is not refused: " + x2);
            var x3 = Clash(new[] { gm }, new[] { b }, new HashSet<FormKey>(), claimed: new Dictionary<FormKey, FormKey> { [b] = other });
            if (x3 is null || !x3.Contains($"carrying a version of {other}"))
                bad.Add("a neighbour an earlier use_version changes is not refused: " + x3);
            var x4 = Clash(new[] { gm }, Array.Empty<FormKey>(), new HashSet<FormKey>(), matched: new Dictionary<FormKey, FormKey> { [gm] = other });
            if (x4 is null || !x4.Contains("was matched"))
                bad.Add("a navmesh an earlier use_version was matched against is not refused: " + x4);
            return bad.Count == 0 ? null : string.Join(" | ", bad);
        });

        // C2591: the plan for a band one-way row writes no link back across the band: refused by name.
        Case("2591 band-oneway-repair-refused", () =>
        {
            var a = K(0xC11); var b = K(0xC12);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                [a] = Nav(a, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 2, -1, 0, -1) }, new[] { new NavEdgeLink(b, 0) }),
                [b] = Nav(b, new[] { new Vec3(100, 0, 75), new Vec3(200, 0, 75), new Vec3(100, 100, 75) }, new[] { new NavTri(0, 1, 2, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>()),
            };
            var plan = NavRepair.Relink(nav, new[] { new BrokenLink("EDGE-ONEWAY", a, b, 0) }, Tol, Wide);
            var links = plan.Plans.SelectMany(p => p.Links).ToList();
            bool refused = links.Any(l => l.Status == "refused" && l.Why.Contains("75 u in height"));
            bool written = links.Any(l => l.Status == "linked-back");
            return refused && !written ? null : $"want the link back refused for the band, got [{string.Join("; ", links.Select(l => l.Status + ": " + l.Why))}]";
        });

        // C2592: a band one-way row whose far navmesh had the link back in a lower version gets it restored, labelled with
        // that plugin and "in the band" (houseCARL 2026-11-22, the RV pair): from an edge within the fitted tolerance, and
        // from an edge in the band too (an author's link is evidence whatever its gap); a lower version whose linking edge
        // lies beyond the band (200 u) does not lie along the source edge, and the row stays refused.
        Case("2592 band-oneway-restored-from-lower-version", () =>
        {
            var a = K(0xC21); var b = K(0xC22);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                [a] = Nav(a, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 2, -1, 0, -1) }, new[] { new NavEdgeLink(b, 0) }),
                [b] = Nav(b, new[] { new Vec3(100, 0, 75), new Vec3(200, 0, 75), new Vec3(100, 100, 75) }, new[] { new NavTri(0, 1, 2, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>()),
            };
            NavmeshGeometry Lower(float z) => Nav(b, new[] { new Vec3(100, 0, z), new Vec3(200, 0, z), new Vec3(100, 100, z) },
                                                  new[] { new NavTri(0, 1, 2, 4, -1, -1, 0) }, new[] { new NavEdgeLink(a, 0) }).Geom;
            List<LinkRepair> Run(float z) => NavRepair.Relink(nav, new[] { new BrokenLink("EDGE-ONEWAY", a, b, 0) }, Tol, Wide,
                lower: new Dictionary<FormKey, IReadOnlyList<(string, NavmeshGeometry)>> { [b] = new List<(string, NavmeshGeometry)> { ("Lower.esp", Lower(z)) } })
                .Plans.SelectMany(p => p.Links).ToList();
            string Show(List<LinkRepair> l) => string.Join("; ", l.Select(x => x.Status + ": " + x.Why));
            var near = Run(5); var inBand = Run(75); var beyond = Run(200);
            if (!near.Any(l => l.Status == "linked-back" && l.Why.Contains("restored from Lower.esp, in the band") && l.To == a && l.ToTri == 0))
                return "an authored link back (edge within the tolerance) was not restored: " + Show(near);
            if (!inBand.Any(l => l.Status == "linked-back" && l.Why.Contains("restored from Lower.esp, in the band") && l.Why.Contains("from an edge in the band too")))
                return "an authored link back from an edge in the band was not restored: " + Show(inBand);
            return beyond.Any(l => l.Status == "refused") && !beyond.Any(l => l.Status == "linked-back")
                ? null : "a lower version linking back from beyond the band was taken as authored: " + Show(beyond);
        });

        // C2593: a follow-the-ground plan on an island of the navmesh info map makes the same navmesh edits as on a plain one
        // (houseCARL's sweep, 0DEEFB). CORRECTED 2026-11-27 (R7): this expected the island refused, the behaviour before island
        // support; the island data's own change is the plan's NAVI change, held by 2658.
        Case("2593 ground-on-island-same-edits", () =>
        {
            var (nav, g, h) = Pair();
            var rows = new[] { Moved(g, 4, 200), Moved(h, 3, 200) };
            var plain = NavRepair.Ground(nav, rows, 38, Tol).Plans.Single(x => x.Nav == g);
            var island = NavRepair.Ground(nav, rows, 38, Tol, islands: new Dictionary<FormKey, NavIslandData> { [g] = BoxOnly(nav[g].Geom) }).Plans.Single(x => x.Nav == g);
            if (plain.Status != "planned") return $"without the island the row should plan, got {plain.Status}: {string.Join("; ", plain.Why)}";
            string Ops(NavPlan p) => string.Join(" | ", p.Edits.Select(o => $"{o.FieldPath} {o.Op} {o.Key} {o.Value}"));
            return island.Status == "planned" && Ops(island) == Ops(plain) && island.IslandAfter is not null && plain.IslandAfter is null
                ? null : $"on an island: status {island.Status}, edits [{Ops(island)}] against [{Ops(plain)}]: {string.Join("; ", island.Why)}";
        });

        // C2594: a one-way row whose far triangle's matching edge joins one of its own triangles is refused naming that
        // triangle, never an empty link target (houseCARL's sweep: 11 refusals read "links  triangle ").
        Case("2594 oneway-refusal-names-own-neighbour", () =>
        {
            var a = K(0xC31); var b = K(0xC32);
            var nav = new Dictionary<FormKey, WinningNav>
            {
                [a] = Nav(a, new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0) }, new[] { new NavTri(0, 1, 2, 2, -1, 0, -1) }, new[] { new NavEdgeLink(b, 0) }),
                // B's triangle 0 edge 2 (x = 100) joins B's own triangle 1 (no link flag)
                [b] = Nav(b, new[] { new Vec3(100, 0, 0), new Vec3(200, 0, 0), new Vec3(100, 100, 0), new Vec3(50, 50, 0) },
                          new[] { new NavTri(0, 1, 2, 0, -1, -1, 1), new NavTri(2, 3, 0, 0, -1, -1, -1) }, Array.Empty<NavEdgeLink>()),
            };
            var links = NavRepair.Relink(nav, new[] { new BrokenLink("EDGE-ONEWAY", a, b, 0) }, Tol, Wide).Plans.SelectMany(p => p.Links).ToList();
            var r = links.FirstOrDefault(l => l.Status == "refused");
            return r is not null && r.Why.Contains("joins its own triangle 1") && !r.Why.Contains("links  triangle")
                ? null : "want a refusal naming its own triangle 1, got: " + string.Join("; ", links.Select(l => l.Status + ": " + l.Why));
        });

        // C2595: a ground row whose mover ships its own version of the navmesh, sitting on the ground it moved, is refused in
        // favour of that version (use_version), where the same row without it plans (The Great City of Winterhold's 100F4B).
        Case("2595 ground-prefers-movers-own-version", () =>
        {
            var (nav, g, h) = Pair();
            var row = Moved(g, 4, 200);
            var plain = NavRepair.Ground(nav, new[] { row, Moved(h, 3, 200) }, 38, Tol).Plans.Single(x => x.Nav == g);
            var mover = NavRepair.Ground(nav, new[] { row with { Mover = new MoverVersion("City.esp", "Revert.esp", 297, 465, $"{g} (City.esp > Revert.esp)") }, Moved(h, 3, 200) }, 38, Tol)
                .Plans.Single(x => x.Nav == g);
            if (plain.Status != "planned") return $"without a mover's version the row should plan, got {plain.Status}: {string.Join("; ", plain.Why)}";
            return mover.Status == "refused" && mover.Edits.Count == 0 && mover.Change.Vertices.Count == 0
                   && mover.Why.Any(w => w.Contains("City.esp moved the ground") && w.Contains("297 triangles; Revert.esp's has 465") && w.Contains("Revert.esp's version overrides it") && w.Contains("use_version")
                                         && w.Contains($"CLOBBERED row {g} (City.esp > Revert.esp)"))
                ? null : $"with the mover's version: status {mover.Status}, edits {mover.Edits.Count}: {string.Join("; ", mover.Why)}";
        });

        // C2596: which ground mover's version replaces following the ground (NavGround.ChooseMover, review of 2026-11-22): only a
        // holder that is not the owner, did not delete it, and ships its own re-cut (triangles, another hash, other vertices);
        // a hash that differs by flags alone is not one; a minor mover's re-cut is found when the main mover has none.
        Case("2596 choose-ground-mover", () =>
        {
            var (nav, g, _) = Pair();
            var win = nav[g].Geom;
            var recut = win with { Vertices = win.Vertices.Select(v => v with { Z = v.Z + 200 }).ToList() };
            var flagsOnly = win with { Triangles = win.Triangles.Select(t => t with { Flags = t.Flags | 0x800 }).ToList() };
            const int owner = 9, main = 5, minor = 6;
            var size = new Dictionary<int, (int V, int T, int H)> { [owner] = (4, 2, 111), [main] = (4, 2, 222), [minor] = (4, 2, 333) };
            (int, IReadOnlyList<int>)? Pick(IReadOnlyList<(int, int)> movers, int[] holders, Func<int, NavmeshGeometry?> ver,
                                            Func<int, bool>? deleted = null, Dictionary<int, (int V, int T, int H)>? sz = null)
                => NavGround.ChooseMover(owner, movers, holders, pi => (sz ?? size).GetValueOrDefault(pi), deleted ?? (_ => false), ver, win, 38);
            string S((int Mover, IReadOnlyList<int> Others)? p) => p is { } x ? $"{x.Mover} others [{string.Join(",", x.Others)}]" : "none";
            var one = new[] { (main, 10) };
            var checks = new (string Name, string Got, string Want)[]
            {
                ("re-cut", S(Pick(one, new[] { main, owner }, _ => recut)), "5 others []"),
                ("same hash", S(Pick(one, new[] { main, owner }, _ => recut, sz: new() { [owner] = (4, 2, 111), [main] = (4, 2, 111) })), "none"),
                ("deleted", S(Pick(one, new[] { main, owner }, _ => recut, deleted: pi => pi == main)), "none"),
                ("no triangles", S(Pick(one, new[] { main, owner }, _ => recut, sz: new() { [owner] = (4, 2, 111), [main] = (0, 0, 222) })), "none"),
                ("flags only", S(Pick(one, new[] { main, owner }, _ => flagsOnly)), "none"),
                ("not a holder", S(Pick(one, new[] { owner }, _ => recut)), "none"),
                ("mover is owner", S(NavGround.ChooseMover(owner, new[] { (owner, 10) }, new[] { owner }, pi => size[pi], _ => false, _ => recut, win, 38)), "none"),
                ("minor re-cut", S(Pick(new[] { (main, 10), (minor, 3) }, new[] { minor, owner }, _ => recut)), "6 others [5]"),
                ("main re-cut, others named", S(Pick(new[] { (main, 10), (minor, 3) }, new[] { main, minor, owner }, pi => pi == main ? recut : null)), "5 others [6]"),
            };
            var bad = checks.Where(c => c.Got != c.Want).Select(c => $"{c.Name}: got {c.Got}, want {c.Want}").ToList();
            return bad.Count == 0 ? null : string.Join("; ", bad);
        });

        // C2597: the refusals keep their order: a mover's re-cut refuses following the ground on an island as on any navmesh
        // (the island is no refusal of its own since R7), naming use_version; the refusal names the other movers when there are
        // some, and claims no CLOBBERED row when the detector gave none. CORRECTED 2026-11-27 (R7): the island half expected
        // the island named first and use_version not offered, the order while islands were refused.
        Case("2597 ground-mover-refusal-order", () =>
        {
            var (nav, g, h) = Pair();
            var mv = new MoverVersion("City.esp", "Revert.esp", 297, 465, null, new[] { "Walls.esp" });
            var rows = new[] { Moved(g, 4, 200) with { Mover = mv }, Moved(h, 3, 0.5f) };
            var island = NavRepair.Ground(nav, rows, 38, Tol, islands: new Dictionary<FormKey, NavIslandData> { [g] = BoxOnly(nav[g].Geom) }).Plans.Single(x => x.Nav == g);
            if (!(island.Status == "refused" && island.Why.Any(w => w.Contains("use_version")) && !island.Why.Any(w => w.Contains("is an island")) && island.IslandAfter is null))
                return $"island with a mover: {island.Status}: {string.Join("; ", island.Why)}";
            var plan = NavRepair.Ground(nav, rows, 38, Tol);
            var pg = plan.Plans.Single(x => x.Nav == g);
            if (!pg.Why.Any(w => w.Contains("Walls.esp also moved ground under it") && !w.Contains("CLOBBERED")))
                return "the other mover is not named (or a CLOBBERED row was claimed with none): " + string.Join("; ", pg.Why);
            return null;
        });

        // C2658 (R7): following the ground on an island of the navmesh info map plans, and the plan says what the island data
        // becomes: the box the navmesh's own (its z recomputed), each island vertex raised by what the surface under it rose
        // (G and H both rise 200 u, so every island vertex rises 200 u: on G at (50,25), at (25,75) 4 u below it, and at
        // (120,50), 20 u east of G in plan, whose nearest point of G is (100,50)); H's island, an empty mesh, keeps it empty
        // with its box risen. Rejects: an island refused (the behaviour before R7), a box or vertex left behind, a mesh given
        // to an empty island.
        Case("2658 ground-on-island-plans", () =>
        {
            var (nav, g, h) = Pair();
            var gi = new NavIslandData(new Vec3(0, 0, 0), new Vec3(100, 100, 0), new[] { new Vec3(50, 25, 0), new Vec3(120, 50, 3), new Vec3(25, 75, -4) }, new[] { ((short)0, (short)1, (short)2) });
            var islands = new Dictionary<FormKey, NavIslandData> { [g] = gi, [h] = BoxOnly(nav[h].Geom) };
            var plan = NavRepair.Ground(nav, new[] { Moved(g, 4, 200), Moved(h, 3, 200) }, 38, Tol, islands: islands);
            var pg = plan.Plans.Single(x => x.Nav == g); var ph = plan.Plans.Single(x => x.Nav == h);
            if (pg.Status != "planned" || ph.Status != "planned") return $"G {pg.Status}: {string.Join("; ", pg.Why)}; H {ph.Status}: {string.Join("; ", ph.Why)}";
            if (pg.IslandAfter is not { } ga) return "G's plan says nothing of its island data";
            if (ga.Min != new Vec3(0, 0, 200) || ga.Max != new Vec3(100, 100, 200) || ga.Min != pg.Change.Min || ga.Max != pg.Change.Max)
                return $"G's island box {ga.Min}-{ga.Max}, want the navmesh's (0,0,200)-(100,100,200)";
            var want = new[] { new Vec3(50, 25, 200), new Vec3(120, 50, 203), new Vec3(25, 75, 196) };
            if (!ga.Vertices.SequenceEqual(want) || !ga.Triangles.SequenceEqual(gi.Triangles)) return $"G's island mesh [{string.Join(" ", ga.Vertices)}], want [{string.Join(" ", want)}], triangles kept";
            if (ph.IslandAfter is not { } ha || !ha.EmptyMesh || ha.Min != new Vec3(100, 0, 200) || ha.Max != new Vec3(200, 100, 200))
                return $"H's island data {(ph.IslandAfter is { } x ? $"{x.Min}-{x.Max}, {x.Vertices.Count} vertices" : "none")}, want an empty mesh in (100,0,200)-(200,100,200)";
            return null;
        });

        // C2659 (R7, Gate A review condition 5): two changes to one navmesh info map entry in one patch are refused by name. A
        // repair plan's relinks change navmesh A's entry (MergedTo gains C); its ground rows change the island data of A and B.
        // A's island change is refused, naming A and the relink; B's is planned in place on the winner's entry 1.
        Case("2659 ground-island-beside-a-relink", () =>
        {
            FormKey a = K(0xA91), b = K(0xA92), c = K(0xA93);
            var map = new Mutagen.Bethesda.Skyrim.NavigationMeshInfoMap(K(0xC00), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            foreach (var n in new[] { a, b })
                map.MapInfos.Add(new Mutagen.Bethesda.Skyrim.NavigationMapInfo
                {
                    NavigationMesh = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>(n), Unknown = NavIsland.IslandFlag,
                    Island = new Mutagen.Bethesda.Skyrim.IslandData { Min = new Noggog.P3Float(0, 0, 0), Max = new Noggog.P3Float(100, 100, 0) },
                    Parent = new Mutagen.Bethesda.Skyrim.NavigationMapInfoWorldParent { ParentWorldspace = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IWorldspaceGetter>(K(0x800)) },
                });
            var view = NaviView.Of(new[] { new NaviSource("SelfTest.esm", "", map) });
            var relinks = new NaviPlan { Record = view.Record, WinnerPlugin = "SelfTest.esm", WinnerPath = "" };
            relinks.Entries.Add(new NaviEntryChange(a, "SelfTest.esm", "", 0, 0, new[] { c }, Array.Empty<FormKey>(), Array.Empty<FormKey>()));
            relinks.Ops.Add(new RepairOp(K(0xC00).ToString(), "MapInfos[0].MergedTo", "Add", Value: c.ToString()));
            var raised = new NavIslandEntry(NavIsland.IslandFlag, new NavIslandData(new Vec3(0, 0, 200), new Vec3(100, 100, 200), Array.Empty<Vec3>(), Array.Empty<(short, short, short)>()));
            var (np, refused) = NavRepairNavi.ForGroundIslands(new Dictionary<FormKey, NavIslandEntry> { [a] = raised, [b] = raised }, view, relinks);
            if (!refused.TryGetValue(a, out var why) || !why.Contains(a.ToString()) || !why.Contains("relink")) return $"A's island change beside its relink was not refused by name: [{string.Join(" | ", refused.Select(kv => $"{kv.Key}: {kv.Value}"))}]";
            if (refused.ContainsKey(b)) return $"B was refused: {refused[b]}";
            var ops = np.Ops.Select(o => $"{o.FieldPath} {o.Op}").ToList();
            return np.Entries.Count == 1 && np.Entries[0].Nav == b && np.Entries[0].WinnerIndex == 1 && ops.SequenceEqual(new[] { "MapInfos[1].Island Set" })
                ? null : $"B's change: entries [{string.Join(",", np.Entries.Select(e => e.Nav))}], ops [{string.Join(", ", ops)}]";
        });

        Console.WriteLine($"{(fail == 0 ? "all" : $"{fail} failed of")} navrepair self-test cases");
        return fail == 0 ? 0 : 1;
    }
}
