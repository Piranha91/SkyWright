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
            long hits = tmr.Entries().Sum(x => x.E.MemoHits);   // a memo hit is a Measure call that runs no core contact
            bool ran = measured > 0 && tmr.CoreMeasure.Calls + hits == measured && (radius ? tmr.CoreShell.Calls + hits == measured : tmr.CoreShell.Calls == 0)
                && tmr.Best.Calls == 1 && tmr.Pair.Calls == 1 && tmr.BestInJudge.Calls > 0 && tmr.PairInJudge.Calls > 0 && tmr.JudgeCalls > 0
                && tmr.Entries().All(x => x.E.CallTicks <= x.E.WallTicks);
            int at = Enumerable.Range(0, Math.Min(off0.Length, on1.Length)).FirstOrDefault(i => off0[i] != on1[i], -1);
            Check($"CT-1 (radius {(radius ? "on" : "off")}): timers on leave every judged row, BestContact, PairContact and skip count unchanged, and the timer ran",
                off0 == on1 && ran,
                off0 == on1 ? $"{off0.Length} chars equal; measured {measured}, memo hits {hits}, core {tmr.CoreMeasure.Calls}, shell {tmr.CoreShell.Calls}, best {tmr.Best.Calls}/{tmr.BestInJudge.Calls}, pair {tmr.Pair.Calls}/{tmr.PairInJudge.Calls}, judge {tmr.JudgeCalls}"
                            : $"serialisations differ at char {at} (lengths {off0.Length} vs {on1.Length}): ...{off0.Substring(Math.Max(0, at - 60), Math.Min(120, off0.Length - Math.Max(0, at - 60)))}");
        }

        // ---- Gate B red-first cases for the chosen changes (GATE-B.md §5) ----
        // CT-2 (shell facing-only): with the radius read, the shell contact is read for its facing gap alone, so it must run no
        // penetration and no enclosed-depth queries (the core contact still runs them). RED until the facing-only path exists.
        {
            var ix = Load(true); var tmr = new ContactTimer(); ix.Timer = tmr; var sk = new ContactSkips();
            var h = ix.MovedHosts(sk, closure); var d = ix.DependentSnaps(sk); var w = ix.PhysicalWinners(null, sk);
            foreach (var x in h) ix.Judge(x, d, w, closure, sk, keepNoRelation: true);
            Check("CT-2: the radius shell contact is read for its facing gap only (no penetration or enclosed queries on the shell)",
                tmr.CoreShell.Calls > 0 && tmr.CoreShell.PenetrationQueries == 0 && tmr.CoreShell.EnclosedQueries == 0 && tmr.CoreMeasure.EnclosedQueries > 0,
                $"shell calls {tmr.CoreShell.Calls}, shell penetration queries {tmr.CoreShell.PenetrationQueries}, shell enclosed queries {tmr.CoreShell.EnclosedQueries}, core enclosed {tmr.CoreMeasure.EnclosedQueries}");
        }
        // CT-4 (Measure memo): each distinct pair is measured once per index: the core contact runs once per distinct Measure key,
        // radius on and off. RED until the memo exists (the fixture repeats pairs: Judge, then a default BestContact and PairContact).
        foreach (bool radius in new[] { true, false })
        {
            var ix = Load(radius); var tmr = new ContactTimer(); ix.Timer = tmr; var sk = new ContactSkips();
            var h = ix.MovedHosts(sk, closure); var d = ix.DependentSnaps(sk); var w = ix.PhysicalWinners(null, sk);
            foreach (var x in h) ix.Judge(x, d, w, closure, sk, keepNoRelation: true);
            var dsn = ix.Index.History.Of(D(1))!; var dwn = dsn[^1];
            ix.BestContact(D(1), dsn.Count - 1, rugK, dwn, ix.WorldBox(rugK, dwn), w, ix.WinnerPlacement, (k, _) => ix.WinnerBase(k), out _);
            var hs1 = ix.Index.History.Of(H(1))!; var rs1 = ix.Index.History.Of(D(1))!;
            ix.PairContact(D(1), 0, rugK, rs1[0], H(1), 0, floorK, hs1[0]);
            Check($"CT-4 (radius {(radius ? "on" : "off")}): a pair already measured on this index is not measured again",
                tmr.MeasureRepeats > 0 && tmr.CoreMeasure.Calls == tmr.MeasureDistinct,
                $"Measure calls {tmr.MeasureDistinct + tmr.MeasureRepeats} ({tmr.MeasureRepeats} repeats), core contacts {tmr.CoreMeasure.Calls}, distinct pairs {tmr.MeasureDistinct}");
        }
        // CT-5 (Measure memo correctness; green on the base, the memo's sabotage arms must turn them red). Precondition shared with
        // the per-placement caches: a snap index names one placement (callers pass the snap at that index). On ONE index, a pair
        // measured again with the host at another placement, the host under another base at the same snap, or the dependent under
        // another base, must equal that call made on a FRESH index; and an index with another Reach must equal a fresh one with it.
        {
            string Key(ArmContact? a) => a is null ? "null" : System.Text.Json.JsonSerializer.Serialize(new { a.Contact, a.CoreGap, a.AgainstExtent, a.Against }, opts);
            var hs1 = ci.Index.History.Of(H(1))!; var rs1 = ci.Index.History.Of(D(1))!;
            var one = Load(true);
            var calls = new (string Name, Func<ContactIndex, ArmContact?> Call)[]
            {
                ("host at its authored placement", ix => ix.PairContact(D(1), 0, rugK, rs1[0], H(1), 0, floorK, hs1[0])),
                ("host moved (its next snap)", ix => ix.PairContact(D(1), 0, rugK, rs1[0], H(1), 1, floorK, hs1[1])),
                ("host under another base at the same snap", ix => ix.PairContact(D(1), 0, rugK, rs1[0], H(1), 0, blockK, hs1[0])),
                ("dependent under another base", ix => ix.PairContact(D(1), 0, coverK, rs1[0], H(1), 0, floorK, hs1[0])),
            };
            foreach (var (name, call) in calls)
            {
                string onShared = Key(call(one)), onFresh = Key(call(Load(true)));
                Check($"CT-5: {name}: the same index answers as a fresh index", onShared == onFresh, onShared == onFresh ? onShared[..Math.Min(140, onShared.Length)] : $"shared {onShared[..Math.Min(140, onShared.Length)]} vs fresh {onFresh[..Math.Min(140, onFresh.Length)]}");
            }
            var near = new ContactIndex(Geometry) { ReadRadius = true, Reach = 16f };
            for (int pp = 0; pp < 2; pp++) { using var mod = SkyrimMod.CreateFromBinaryOverlay(pp == 0 ? mp : ap, SkyrimRelease.SkyrimSE); near.Index.Ingest(pp, mod); }
            var nearFresh = new ContactIndex(Geometry) { ReadRadius = true, Reach = 16f };
            for (int pp = 0; pp < 2; pp++) { using var mod = SkyrimMod.CreateFromBinaryOverlay(pp == 0 ? mp : ap, SkyrimRelease.SkyrimSE); nearFresh.Index.Ingest(pp, mod); }
            // the block host is 19 u away: within the default reach (128), beyond 16, so the two reaches give different answers
            calls[2].Call(one); calls[2].Call(near);
            string a1 = Key(calls[2].Call(near)), b1 = Key(calls[2].Call(nearFresh)), c1 = Key(calls[2].Call(one));
            Check("CT-5: an index with another Reach answers as a fresh index with that Reach, not as one with the default",
                a1 == b1 && a1 != c1, $"reach 16 shared {a1[..Math.Min(100, a1.Length)]}; fresh {b1[..Math.Min(100, b1.Length)]}; default {c1[..Math.Min(100, c1.Length)]}");
        }
        // CT-3 (shell facing read): ContactFacing returns, bit for bit, the FacingDistance Contact returns for the same arguments:
        // every pair of this fixture's soups at several offsets, and a seeded set of random soups (open and closed, touching, crossing,
        // apart, empty), at the radius path's band (8) and the default (4).
        // CT-7 (memo across bucket targets, GATE-B review G2): the same pair measured on grids built at different bucket targets
        // (coarsened and not) gives a bit-identical SoupContact and facing gap, so a memo that keeps a pair's first answer cannot
        // disagree with a later rebuild of that grid at another target.
        {
            static string Bits(SoupContact c) => string.Join(",", new[] { c.Distance, c.Penetration, c.Normal.X, c.Normal.Y, c.Normal.Z, c.EnclosedDepth, c.FacingDistance }
                .Select(f => BitConverter.SingleToInt32Bits(f).ToString("x8"))) + $"|{c.ContactPairs},{c.PenetratingVertices},{c.DependentTriangles},{c.HostTriangles},{c.EnclosedVertices},{c.HostClosed},{c.CrossedFaces}";
            var rnd = new Random(20261021);
            TriangleSoup RandomSoup(int tris, float span, Vector3 at)
            {
                var v = new Vector3[tris * 3]; var ix = new int[tris * 3];
                for (int i = 0; i < v.Length; i++) { v[i] = at + new Vector3((float)rnd.NextDouble() * span, (float)rnd.NextDouble() * span, (float)rnd.NextDouble() * span * 0.5f); ix[i] = i; }
                return new TriangleSoup(v, ix);
            }
            static TriangleSoup Move(TriangleSoup s, Vector3 d) => new(s.Vertices.Select(x => x + d).ToArray(), s.Indices);
            var shapes = new List<TriangleSoup>
            {
                Plane(-100, -100, 100, 100), Plane(-20, -20, 20, 20), Ring(), TriangleSoup.Empty,
                ContactIndex.BoxSoup(new Aabb(new Vec3(-100, -100, -20), new Vec3(100, 100, 20), false)),
                ContactIndex.BoxSoup(new Aabb(new Vec3(-10, -10, -10), new Vec3(10, 10, 10), false)),
            };
            for (int k = 0; k < 6; k++) shapes.Add(RandomSoup(8 + 6 * k, 40f + 30f * k, new Vector3(-20f * k, -15f * k, -10f)));
            var offsets = new[] { Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, 0, 3.5f), new Vector3(0, 0, 9), new Vector3(7, -3, -2), new Vector3(30, 0, 0), new Vector3(0, 0, -15) };
            int pairs = 0, facingEqual = 0, bucketPairs = 0, bucketEqual = 0; string? firstFacing = null, firstBucket = null;
            var powers = new SortedSet<int>();
            foreach (var host in shapes)
            {
                var g0 = TriangleGrid.BuildAdaptive(host, targetBucketApproxBytes: 64L << 20);
                var coarse = new[] { 4096L, 256L, 0L }.Select(tb => TriangleGrid.BuildAdaptive(host, targetBucketApproxBytes: tb)).ToList();
                foreach (var gg in coarse.Prepend(g0)) powers.Add(gg.Sizing?.GroupingPower ?? -1);
                foreach (var dep0 in shapes)
                    foreach (var shift in offsets)
                        foreach (float band in new[] { 8f, 4f })
                        {
                            var dep = Move(dep0, shift);
                            var full = SpatialOverlap.Contact(dep, g0, 128f, band);
                            float facing = SpatialOverlap.ContactFacing(dep, g0, 128f, band, default, null);
                            pairs++;
                            if (BitConverter.SingleToInt32Bits(full.FacingDistance) == BitConverter.SingleToInt32Bits(facing)) facingEqual++;
                            else firstFacing ??= $"pair {pairs}: Contact {full.FacingDistance} vs ContactFacing {facing}";
                            foreach (var gc in coarse)
                            {
                                bucketPairs++;
                                var cc = SpatialOverlap.Contact(dep, gc, 128f, band);
                                float fc = SpatialOverlap.ContactFacing(dep, gc, 128f, band, default, null);
                                if (Bits(cc) == Bits(full) && BitConverter.SingleToInt32Bits(fc) == BitConverter.SingleToInt32Bits(facing)) bucketEqual++;
                                else firstBucket ??= $"pair {bucketPairs} at grouping power {gc.Sizing?.GroupingPower}: {Bits(cc)} vs {Bits(full)}";
                            }
                        }
            }
            Check("CT-3: ContactFacing equals Contact's FacingDistance bit for bit (fixture soups and seeded random soups, bands 8 and 4)",
                pairs > 0 && facingEqual == pairs, $"{facingEqual} of {pairs} equal{(firstFacing is null ? "" : "; first difference " + firstFacing)}");
            Check("CT-7: one pair on grids built at different bucket targets gives a bit-identical contact and facing gap (coarsening seen)",
                bucketPairs > 0 && bucketEqual == bucketPairs && powers.Count(x => x > 0) > 0 && powers.Contains(0),
                $"{bucketEqual} of {bucketPairs} equal; grouping powers built {string.Join(",", powers)}{(firstBucket is null ? "" : "; first difference " + firstBucket)}");
        }
        // CT-6 (memo key, by reflection): the memo key's fields are exactly Measure's parameters minus a reviewed list of derived
        // or inert ones, and every public setting of ContactIndex Measure could read is init-only (fixed for an index's life), except a
        // reviewed list Measure does not read. A new Measure parameter, or a new mutable setting, fails here rather than in the memo.
        {
            const System.Reflection.BindingFlags Np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var measure = typeof(ContactIndex).GetMethod("Measure", Np);
            var keyType = typeof(ContactIndex).GetNestedType("MeasureKey", System.Reflection.BindingFlags.NonPublic);
            var derived = new[] { "soup", "grid", "e" };   // built from the key by DepSoup / HostGrid; the timer entry changes nothing
            var parms = measure?.GetParameters().Select(x => x.Name!).Where(n => !derived.Contains(n)).Select(n => n.ToLowerInvariant()).OrderBy(n => n).ToList() ?? new();
            var fields = keyType?.GetProperties().Select(x => x.Name.ToLowerInvariant()).Where(n => n != "equalitycontract").OrderBy(n => n).ToList() ?? new();
            var mutableAllowed = new[] { nameof(ContactIndex.Timer), nameof(ContactIndex.Trace), nameof(ContactIndex.StageProbe), nameof(ContactIndex.State) };
            static bool InitOnly(System.Reflection.PropertyInfo pi) => pi.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            var mutable = typeof(ContactIndex).GetProperties().Where(pi => pi.SetMethod is { IsPublic: true } && !InitOnly(pi)).Select(pi => pi.Name).OrderBy(n => n).ToList();
            var unexpected = mutable.Except(mutableAllowed).ToList();
            Check("CT-6: the Measure memo key is exactly Measure's non-derived parameters, and no unreviewed ContactIndex setting can change after construction",
                measure is not null && keyType is not null && parms.Count > 0 && parms.SequenceEqual(fields) && unexpected.Count == 0,
                $"parameters [{string.Join(",", parms)}]; key [{string.Join(",", fields)}]; mutable settings [{string.Join(",", mutable)}]; unreviewed [{string.Join(",", unexpected)}]");
        }
        mo.Dispose();
        Directory.Delete(root, true);
    }
}
