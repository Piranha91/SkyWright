using HarnessShared;
using HousecarlCore;
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

        Console.WriteLine($"{(fail == 0 ? "all" : $"{fail} failed of")} navrepair self-test cases");
        return fail == 0 ? 0 : 1;
    }
}
