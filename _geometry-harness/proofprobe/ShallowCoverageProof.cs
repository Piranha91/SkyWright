using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// Analytic controls through the real history, arm resolution, neighbour selection and rule.
static class ShallowCoverageProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        var root = Path.Combine(Path.GetTempPath(), "housecarl-shallow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var mk = ModKey.FromNameAndExtension("ShallowMaster.esm");
        var ak = ModKey.FromNameAndExtension("ShallowMover.esp");
        var cellK = new FormKey(mk, 0x900);
        var floorK = new FormKey(mk, 0xA00); var rugK = new FormKey(mk, 0xA01);
        var blockK = new FormKey(mk, 0xA02); var coverK = new FormKey(mk, 0xA03);
        var holeK = new FormKey(mk, 0xA04);
        var incompleteK = new FormKey(mk, 0xA05); var missingK = new FormKey(mk, 0xA06);
        FormKey H(int n) => new(mk, (uint)(0x1000 + n));
        FormKey D(int n) => new(mk, (uint)(0x2000 + n));
        FormKey T(int n) => new(mk, (uint)(0x3000 + n));
        FormKey C(int n) => new(mk, (uint)(0x4000 + n));
        FormKey N(int n) => new(ak, (uint)(0x5000 + n));
        static Static Stat(FormKey key) => new(key, SkyrimRelease.SkyrimSE)
        { EditorID = "Shallow" + key.ID, ObjectBounds = new ObjectBounds
          { First = new P3Int16(-100, -100, -20), Second = new P3Int16(100, 100, 20) } };
        static PlacedObject Ref(FormKey k, FormKey b, float x, float z, bool disabled = false)
        {
            var r = new PlacedObject(k, SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(x, 0, z), Rotation = new P3Float(0, 0, 0) } };
            r.Base.SetTo(b);
            if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
            return r;
        }
        static void File(SkyrimMod mod, Cell cell)
        {
            var block = new CellBlock { BlockNumber = (int)(cell.FormKey.ID % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = (int)(cell.FormKey.ID / 10 % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
        }
        string Write(SkyrimMod m, params ISkyrimModGetter[] masters)
        {
            var p = Path.Combine(root, m.ModKey.FileName.String);
            m.BeginWrite.ToPath(p).WithLoadOrder(masters).Write(); return p;
        }
        static TriangleSoup Plane(float x0, float y0, float x1, float y1)
            => new(new[] { new Vector3(x0,y0,0), new Vector3(x1,y0,0), new Vector3(x1,y1,0), new Vector3(x0,y1,0) }, new[] { 0,1,2,0,2,3 });
        static TriangleSoup Ring()
        {
            var parts = new[] { Plane(-50,-50,-5,50), Plane(5,-50,50,50), Plane(-5,-50,5,-5), Plane(-5,5,5,50) };
            var v = new List<Vector3>(); var i = new List<int>();
            foreach (var p in parts) { int offset = v.Count; v.AddRange(p.Vertices); i.AddRange(p.Indices.Select(t => t + offset)); }
            return new(v.ToArray(), i.ToArray());
        }
        var master = new SkyrimMod(mk, SkyrimRelease.SkyrimSE);
        foreach (var k in new[] { floorK, rugK, blockK, coverK, holeK, incompleteK, missingK }) master.Statics.Add(Stat(k));
        var mc = new Cell(cellK, SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        for (int n = 1; n <= 10; n++)
        {
            mc.Temporary.Add(Ref(H(n), floorK, n * 1000, -1));
            mc.Temporary.Add(Ref(D(n), rugK, n * 1000, 0));
            mc.Temporary.Add(Ref(T(n), blockK, n * 1000 + 60, 0)); // unchanged enclosing block: inherited 20 u depth
            if (n != 7) mc.Temporary.Add(Ref(C(n), n == 9 ? incompleteK : n == 10 ? missingK : n == 5 ? holeK : coverK, n * 1000 + (n == 2 || n >= 9 ? 0 : 400), 1.5f));
        }
        File(master, mc); var mp = Write(master);
        using var mo = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE);
        var mover = new SkyrimMod(ak, SkyrimRelease.SkyrimSE);
        var ac = new Cell(cellK, SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        for (int n = 1; n <= 10; n++)
        {
            ac.Temporary.Add(Ref(H(n), floorK, n * 1000 + 20, -1));
            if (n == 2) continue; // the above surface is unchanged in both arms
            ac.Temporary.Add(Ref(n == 7 ? N(n) : C(n), n == 5 ? holeK : coverK,
                n * 1000 + (n == 4 ? 40 : 0), n == 3 ? 12 : 1.5f));
            if (n == 6) ac.Temporary.Add(Ref(D(n), rugK, n * 1000, 0, disabled: true));
            if (n == 8) ac.Temporary.Add(Ref(D(n), rugK, n * 1000 + 1, 0));
        }
        File(mover, ac); var ap = Write(mover, mo);
        RefGeometry? Geometry(FormKey k)
        {
            var soup = k == floorK ? Plane(-100,-100,100,100) : k == rugK ? Plane(-20,-20,20,20)
                : k == coverK || k == incompleteK ? Plane(-50,-50,50,50) : k == holeK ? Ring()
                : k == blockK ? ContactIndex.BoxSoup(new Aabb(new Vec3(-100,-100,-20), new Vec3(100,100,20), false)) : null;
            if (soup is null) return null;
            if (k == rugK) soup = new TriangleSoup(soup.Vertices, new[] { 0,2,1,0,3,2 }); // the underside faces its support
            var box = soup.Vertices.Aggregate(Aabb.Empty, (b, v) => b.Add(v));
            return new(box, soup, soup, k == rugK ? '-' : k == incompleteK ? 'r' : 'c', k == rugK ? 'r' : 'c', k != rugK && k != incompleteK, Array.Empty<(string,string)>(), false);
        }
        ContactIndex Load(bool radius)
        {
            var index = new ContactIndex(Geometry) { ReadRadius = radius };
            for (int p = 0; p < 2; p++)
            { using var mod = SkyrimMod.CreateFromBinaryOverlay(p == 0 ? mp : ap, SkyrimRelease.SkyrimSE); index.Index.Ingest(p, mod); }
            return index;
        }
        var closure = DetectorModel.MasterClosure(new[] { new HashSet<int>(), new HashSet<int> { 0 } });
        var ci = Load(true); var skips = new ContactSkips();
        var hosts = ci.MovedHosts(skips, closure); var deps = ci.DependentSnaps(skips); var wins = ci.PhysicalWinners(null, skips);
        var rows = hosts.SelectMany(h => ci.Judge(h, deps, wins, closure, skips)).ToList();
        Console.WriteLine($"CI census: hosts={hosts.Count} deps={deps.Count} winners={wins.Count} rows={rows.Count} floorSnaps={ci.Index.History.Of(H(1))?.Count}");
        var hs = ci.Index.History.Of(H(1))!; var rs = ci.Index.History.Of(D(1))!;
        var pair = ci.PairContact(D(1), 0, rugK, rs[0], H(1), 0, floorK, hs[0]);
        Console.WriteLine($"CI author: {pair?.Contact}; hostGeometry={ci.GeomOf(floorK)?.CanHost} armBase={ci.ArmBase(H(1),1,closure)} noGeom={skips.HostNoGeometry} noColl={skips.HostNoCollision} tooSmall={skips.HostTooSmall}");
        Check("CI-0: every fixture has its authored support relation", pair?.Contact.FacingDistance <= 4 && pair?.Contact.NormalUp >= 0.866f && Enumerable.Range(1,10).All(n=>rows.Any(r=>r.Host.Key==H(n)&&r.Dependent==D(n)&&r.Relation==ContactRelation.Supported)), $"authored={pair?.Contact.FacingDistance} rows={rows.Count}");

        ContactRow? Row(int n) => rows.FirstOrDefault(r => r.Host.Key == H(n) && r.Dependent == D(n));
        string Detail(int n) => Row(n) is { } r ? $"{r.Relation}/{r.Verdict} against={r.Winner?.Against} pen={r.Facts.WinnerPenetration} {r.Why}" : "no row";
        Check("CI-1: new shallow whole cover beats inherited deep overlap while true penetration stays zero",
            Row(1) is { Verdict: ContactVerdict.Sunk } r1 && r1.Winner?.Against == C(1) && r1.Facts.WinnerPenetration == 0 && r1.Why.Contains("projected"), Detail(1));
        Check("CI-2: unchanged covering neighbour is not a new burial", Row(2)?.Verdict == ContactVerdict.ReSupported, Detail(2));
        Check("CI-3: high shelf is outside the shallow band", Row(3)?.Verdict == ContactVerdict.ReSupported, Detail(3));
        Check("CI-4: a partially covered rug is not whole cover", Row(4)?.Verdict == ContactVerdict.ReSupported, Detail(4));
        Check("CI-5: all rug corners are covered but a central hole prevents whole cover", Row(5)?.Verdict == ContactVerdict.ReSupported, Detail(5));
        Check("CI-6: a removed dependent keeps priority over coverage", Row(6)?.Verdict == ContactVerdict.Removed, Detail(6));
        Check("CI-7: introduced covering neighbour has no authored coverage to forgive",
            Row(7) is { Verdict: ContactVerdict.Sunk } r7 && r7.Winner?.Against == N(7) && r7.Facts.WinnerPenetration == 0, Detail(7));
        Check("CI-8: deliberately re-placed dependent keeps priority", Row(8)?.Verdict == ContactVerdict.MovedTogether, Detail(8));
        Check("CI-11: an incomplete authored collision decode is unknown, never zero coverage", Row(9) is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.ShallowCoverageIncomplete }, Detail(9));
        Check("CI-12: missing authored host geometry is a named incomplete measurement", Row(10) is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.ShallowCoverageIncomplete }, Detail(10));
        var ds = ci.Index.History.Of(D(1))!; var dw = ds[^1];
        var best = ci.BestContact(D(1), ds.Count-1, rugK, dw, ci.WorldBox(rugK, dw), wins, ci.WinnerPlacement, (k,_) => ci.WinnerBase(k), out _);
        Check("CI-9: a default BestContact caller retains its inherited block penetration", best?.Against == T(1) && best.Contact.EffectivePenetration > 4, $"against={best?.Against} pen={best?.Contact.EffectivePenetration}");
        var off = Load(false); var os = new ContactSkips(); var oh = off.MovedHosts(os, closure); var od = off.DependentSnaps(os); var ow = off.PhysicalWinners(null, os);
        var orow = oh.Where(h=>h.Key==H(1)).SelectMany(h=>off.Judge(h,od,ow,closure,os)).FirstOrDefault(r=>r.Dependent==D(1));
        Check("CI-10: radius off retains its prior differential contact", orow?.Verdict == ContactVerdict.ReSupported, $"{orow?.Verdict}: {orow?.Why}");

        // CT-1: the contact timers change nothing. The same fixture judged on two fresh indexes, timers off then on, radius on (both
        // callbacks, the coverage branch) and off; every row, a default BestContact, a PairContact and the skips serialised field for
        // field; and the timer must have run (its Measure count equals the core step timer's calls, each entry point was seen).
        var opts = new System.Text.Json.JsonSerializerOptions { IncludeFields = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (bool radius in new[] { true, false })
        {
            string Judged(ContactTimer? timer)
            {
                var ix = Load(radius); ix.Timer = timer; var sk = new ContactSkips();
                var h = ix.MovedHosts(sk, closure); var d = ix.DependentSnaps(sk); var w = ix.PhysicalWinners(null, sk);
                var rr = h.SelectMany(x => ix.Judge(x, d, w, closure, sk, keepNoRelation: true)).ToList();
                var dsn = ix.Index.History.Of(D(1))!; var dwn = dsn[^1];
                var b1 = ix.BestContact(D(1), dsn.Count - 1, rugK, dwn, ix.WorldBox(rugK, dwn), w, ix.WinnerPlacement, (k, _) => ix.WinnerBase(k), out int nt);
                var hs1 = ix.Index.History.Of(H(1))!; var rs1 = ix.Index.History.Of(D(1))!;
                var pc = ix.PairContact(D(1), 0, rugK, rs1[0], H(1), 0, floorK, hs1[0]);
                return System.Text.Json.JsonSerializer.Serialize(new { rows = rr, best = b1, nt, pair = pc, skips = sk }, opts);
            }
            var tmr = new ContactTimer();
            string off0 = Judged(null), on1 = Judged(tmr);
            long measured = tmr.Entries().Sum(x => x.E.Measured);
            bool ran = measured > 0 && tmr.CoreMeasure.Calls == measured && (radius ? tmr.CoreShell.Calls == measured : tmr.CoreShell.Calls == 0)
                && tmr.Best.Calls == 1 && tmr.Pair.Calls == 1 && tmr.BestInJudge.Calls > 0 && tmr.PairInJudge.Calls > 0 && tmr.JudgeCalls > 0
                && tmr.Entries().All(x => x.E.CallTicks <= x.E.WallTicks);
            int at = Enumerable.Range(0, Math.Min(off0.Length, on1.Length)).FirstOrDefault(i => off0[i] != on1[i], -1);
            Check($"CT-1 (radius {(radius ? "on" : "off")}): timers on leave every judged row, BestContact, PairContact and skip count unchanged, and the timer ran",
                off0 == on1 && ran,
                off0 == on1 ? $"{off0.Length} chars equal; measured {measured}, core {tmr.CoreMeasure.Calls}, shell {tmr.CoreShell.Calls}, best {tmr.Best.Calls}/{tmr.BestInJudge.Calls}, pair {tmr.Pair.Calls}/{tmr.PairInJudge.Calls}, judge {tmr.JudgeCalls}"
                            : $"serialisations differ at char {at} (lengths {off0.Length} vs {on1.Length}): ...{off0.Substring(Math.Max(0, at - 60), Math.Min(120, off0.Length - Math.Max(0, at - 60)))}");
        }
        mo.Dispose();
        Directory.Delete(root, true);
    }
}
