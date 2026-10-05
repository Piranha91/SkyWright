using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — the differential Sunk rule (closed-slabs lane, stage 2). A Sunk reading means the dependent sits
// deeper than ε inside something under the winner. The rule now asks whether that overlap already existed in the authoring
// arm (the order without the host's mover): the same reference, at its own authoring placement, measured against the
// dependent at its authoring placement. Only the NEW depth (winner depth minus the authoring depth) can make a row Sunk. A
// pre-existing overlap still counts as contact; a row whose ONLY contact is a pre-existing overlap carries its own label,
// "ReSupported (pre-existing overlap only)", so the rest of the object floating stays visible.
//
// Two levels: the rule on facts (DetectorModel.PlacementContact), and ContactIndex.Judge on a synthetic two-plugin order
// written with Mutagen and read back with OBND box soups, so BestContact's selection is the code under test. Every number
// below is fixed by construction (boxes are centred on their placement: OBND x,y half-extents, z extent halved).
static class ContactDifferentialProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — the differential Sunk rule (_shared/ContactIndex.cs BestContact/Judge, DetectorModel.PlacementContact)");
        Console.WriteLine("   Facts first, then a synthetic order: a floor host moved or removed under a crate, with a block beside it.\n");

        // ---- the rule on facts ----
        // A crate that rested on its floor as authored; under the winner something else is 10 u into it.
        var basis = new DetectorModel.ContactFacts(true, true, true, 0f, 0f, 1f, false, false, false,
            true, 0f, 10f, false, DependentMotion.Fixed);
        var plain = DetectorModel.PlacementContact(basis);
        Check("sd-facts: no authoring depth recorded (the default) -> SUNK 10, unchanged from before the rule (callers that do not opt in)",
              plain.Verdict == ContactVerdict.Sunk && !plain.Why.Contains("already as authored"), $"{plain.Verdict}: {plain.Why}");
        var preOnly = DetectorModel.PlacementContact(basis with { WinnerPreexistingPenetration = 10f, WinnerPreexistingOnly = true });
        Check("sd-facts: 10 u inside, all 10 already as authored, and nothing else touching -> RESUPPORTED with the label 'pre-existing overlap only' (F1)",
              preOnly.Verdict == ContactVerdict.ReSupported && preOnly.Why.Contains("pre-existing overlap only"), $"{preOnly.Verdict}: {preOnly.Why}");
        var deeper = DetectorModel.PlacementContact(basis with { WinnerPenetration = 30f, WinnerPreexistingPenetration = 10f });
        Check("sd-facts: 30 u inside, 10 of it already as authored -> SUNK (20 u new > ε), and the reason names the authored part",
              deeper.Verdict == ContactVerdict.Sunk && deeper.Why.Contains("10u of it already as authored"), $"{deeper.Verdict}: {deeper.Why}");
        var withinEps = DetectorModel.PlacementContact(basis with { WinnerPenetration = 13f, WinnerPreexistingPenetration = 10f });
        Check("sd-facts: 13 u inside, 10 already as authored, something else touching -> RESUPPORTED (3 u new is within ε), NOT labelled pre-existing only",
              withinEps.Verdict == ContactVerdict.ReSupported && !withinEps.Why.Contains("pre-existing overlap only"), $"{withinEps.Verdict}: {withinEps.Why}");
        var refused = DetectorModel.PlacementContact(basis with { WinnerPenetration = 40f, WinnerPreexistingPenetration = 10f, WinnerAgainstExtent = 30f });
        Check("sd-facts: R4 still reads the TOTAL depth — 40 u inside a 30 u object (10 of it authored) is refused, not Sunk",
              refused is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.DepthExceedsObject }, $"{refused.Verdict}/{refused.Reason}: {refused.Why}");
        var preOnlyStranded = DetectorModel.PlacementContact(basis with { WinnerDistance = 30f, WinnerPenetration = 0f });
        Check("sd-facts: CONTROL — nothing inside it and nothing within ε -> STRANDED (the label never stands in for Stranded)",
              preOnlyStranded.Verdict == ContactVerdict.Stranded, $"{preOnlyStranded.Verdict}");

        // ---- the synthetic order ----
        var root = Path.Combine(Path.GetTempPath(), "proofprobe-sd-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("SdMaster", ModType.Master);
        var aKey = new ModKey("SdA", ModType.Plugin);
        FormKey M(uint id) => new(mKey, id);
        FormKey A(uint id) => new(aKey, id);
        var floorK = M(0x801); var crateK = M(0x802); var blockK = M(0x803); var cellK = M(0x810); var shortK = M(0x804);
        FormKey H(int n) => M((uint)(0x900 + n));     // the floor host of case n
        FormKey D(int n) => M((uint)(0xA00 + n));     // the crate dependent of case n
        FormKey T(int n) => M((uint)(0xB00 + n));     // a block the master placed
        FormKey C(int n) => A((uint)(0xC00 + n));     // a block the mover introduced

        static Static Stat(FormKey k, string edid, int x, int y, int z) => new(k, SkyrimRelease.SkyrimSE)
            { EditorID = edid, Model = new Model { File = $"meshes\\sd\\{edid}.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) } };
        static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, string edid, bool disabled = false)
        {
            var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
            r.Base.SetTo(baseKey);
            if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
            return r;
        }
        static void FileInterior(SkyrimMod mod, Cell cell)
        {
            uint id = cell.FormKey.ID;
            int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
            var block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
        }
        string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(root, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        // Boxes: the floor is ±100 x ±100 x ±10 (placed at z −10, its top at 0, 200 u across — a host); the crate ±20 (placed at
        // z 20, resting on the floor top — a dependent); the block ±50 (placed at z 20: z −30..70, so its faces clear the crate's
        // top and bottom by 30 u and a crate corner inside it is as deep as the block's x face is past the crate's).
        // A block centred 60 u from the crate's centre overlaps it 10 u; 40 u -> 30 u; 57 u -> 13 u. Case n sits at x = 1000 n.
        const float FloorZ = -10f, CrateZ = 20f, BlockZ = 20f;
        var master = new SkyrimMod(mKey, release);
        master.Statics.Add(Stat(floorK, "SdFloor", 100, 100, 20));
        master.Statics.Add(Stat(crateK, "SdCrate", 20, 20, 40));
        master.Statics.Add(Stat(blockK, "SdBlock", 50, 50, 100));
        master.Statics.Add(Stat(shortK, "SdCrateShort", 10, 10, 20));          // ±10: at the crate's z 20 it spans z 10..30, 10 u above the floor
        var mc = new Cell(cellK, release) { EditorID = "SdCell", Flags = Cell.Flag.IsInteriorCell };
        foreach (int n in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            mc.Temporary.Add(Ref(H(n), floorK, 1000f * n, 0, FloorZ, $"SdFloor{n}"));
            mc.Temporary.Add(Ref(D(n), crateK, 1000f * n, 0, CrateZ, $"SdCrate{n}"));
        }
        mc.Temporary.Add(Ref(T(1), blockK, 1060, 0, BlockZ, "SdBlock1"));        // SD-1: 10 u into the crate as authored
        mc.Temporary.Add(Ref(T(2), blockK, 2060, 0, BlockZ, "SdBlock2"));        // SD-2: the same, and the floor will be removed
        mc.Temporary.Add(Ref(T(4), blockK, 4260, 0, BlockZ, "SdBlock4"));        // SD-4: 200 u off as authored; the mover brings it in
        mc.Temporary.Add(Ref(T(5), blockK, 5060, 0, BlockZ, "SdBlock5"));        // SD-5a: 10 u as authored; the mover pushes it to 30 u
        mc.Temporary.Add(Ref(T(6), blockK, 6060, 0, BlockZ, "SdBlock6"));        // SD-5b: 10 u as authored; the mover pushes it to 13 u
        mc.Temporary.Add(Ref(T(7), blockK, 7040, 0, BlockZ, "SdBlock7"));        // SD-6: 30 u as authored, untouched
        mc.Temporary.Add(Ref(T(8), blockK, 8060, 0, BlockZ, "SdBlock8"));        // SD-7: SD-1's arrangement, read without the opt-in
        mc.Temporary.Add(Ref(H(9), floorK, 9000, 0, FloorZ, "SdFloor9"));       // F3: the mover swaps Crate9's base without moving it
        mc.Temporary.Add(Ref(D(9), crateK, 9000, 0, CrateZ, "SdCrate9"));
        FileInterior(master, mc);
        var mPath = Write(master);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

        // THE MOVER (masters M): every floor moved 20 u along +X (over the 16 u threshold; the crate stays on it), except
        // SD-2's, which it disables.
        var a = new SkyrimMod(aKey, release);
        var ac = new Cell(cellK, release) { Flags = Cell.Flag.IsInteriorCell };
        foreach (int n in new[] { 1, 3, 4, 5, 6, 7, 8 })
            ac.Temporary.Add(Ref(H(n), floorK, 1000f * n + 20, 0, FloorZ, $"SdFloor{n}"));
        ac.Temporary.Add(Ref(H(2), floorK, 2000, 0, FloorZ, "SdFloor2", disabled: true));
        ac.Temporary.Add(Ref(H(9), floorK, 9020, 0, FloorZ, "SdFloor9"));                // F3: floor moved 20 u (crate still over it)
        ac.Temporary.Add(Ref(D(9), shortK, 9000, 0, CrateZ, "SdCrate9"));                // F3: same placement, base swapped to the short crate
        ac.Temporary.Add(Ref(C(3), blockK, 3060, 0, BlockZ, "SdNewBlock3"));      // SD-3: introduced 10 u into the crate
        ac.Temporary.Add(Ref(T(4), blockK, 4060, 0, BlockZ, "SdBlock4"));        // SD-4: moved onto the crate, 10 u
        ac.Temporary.Add(Ref(T(5), blockK, 5040, 0, BlockZ, "SdBlock5"));        // SD-5a: 10 u -> 30 u
        ac.Temporary.Add(Ref(T(6), blockK, 6057, 0, BlockZ, "SdBlock6"));        // SD-5b: 10 u -> 13 u
        ac.Temporary.Add(Ref(C(7), blockK, 6938, 0, BlockZ, "SdNewBlock7"));      // SD-6: introduced 8 u into the crate's other side
        FileInterior(a, ac);
        var aPath = Write(a, mOv);

        ContactIndex? cidx = null;
        cidx = new ContactIndex(k => cidx!.Index.ObndBox(k) is { IsEmpty: false } bx
            ? new RefGeometry(bx, ContactIndex.BoxSoup(bx), ContactIndex.BoxSoup(bx), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false) : null);
        var paths = new[] { mPath, aPath };
        var keys = new ModKey[paths.Length];
        var masterIdx = new HashSet<int>[paths.Length];
        for (int p = 0; p < paths.Length; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
            keys[p] = mod.ModKey;
            var names = mod.ModHeader.MasterReferences.Select(q => q.Master).ToHashSet();
            masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
            cidx.Index.Ingest(p, mod);
        }
        var closure = DetectorModel.MasterClosure(masterIdx);
        var skips = new ContactSkips();
        var hosts = cidx.MovedHosts(skips, closure);
        var depHash = cidx.DependentSnaps(skips);
        var winHash = cidx.PhysicalWinners(null, skips);
        var rows = hosts.SelectMany(h => cidx.Judge(h, depHash, winHash, closure, skips)).ToList();
        ContactRow? Row(int n) => rows.FirstOrDefault(r => r.Host.Key == H(n) && r.Dependent == D(n));
        string R(int n) => Row(n) is { } r
            ? $"{r.Relation}/{r.Verdict}/{r.Reason} against {(r.Winner is { } w ? (w.Against == H(n) ? "the floor" : w.Against.ID.ToString("X")) : "-")} "
            + $"pen {r.Facts.WinnerPenetration:0.##} pre {r.Facts.WinnerPreexistingPenetration:0.##} preOnly {r.Facts.WinnerPreexistingOnly} dist {r.Facts.WinnerDistance:0.##}: {r.Why}"
            : "(no row)";

        Check("sd-order: the census — the eight floors are the moved hosts (the blocks the mover moved are hosts too), and every case has its supported crate row",
              Enumerable.Range(1, 8).All(n => hosts.Any(h => h.Key == H(n))) && Enumerable.Range(1, 8).All(n => Row(n) is { Relation: ContactRelation.Supported }),
              string.Join(" ", Enumerable.Range(1, 8).Select(n => $"{n}:{Row(n)?.Relation.ToString() ?? "none"}")));
        Check("SD-1: the floor moved 20 u (crate still on it), a master block 10 u into the crate in BOTH arms -> RESUPPORTED by the floor, not Sunk 10 inside the block, and NOT labelled pre-existing only",
              Row(1) is { Verdict: ContactVerdict.ReSupported } r1 && r1.Facts.WinnerContactIsHost && !r1.Facts.WinnerPreexistingOnly && !r1.Why.Contains("pre-existing overlap only"), R(1));
        Check("SD-2 (F1): the floor removed; the crate's only contact is the block, 10 u into it exactly as authored -> RESUPPORTED (pre-existing overlap only): the label on the facts and in the reason, against the block, 10 u authored",
              Row(2) is { Verdict: ContactVerdict.ReSupported } r2 && r2.Facts.WinnerPreexistingOnly && r2.Why.Contains("pre-existing overlap only")
           && r2.Winner!.Against == T(2) && MathF.Abs(r2.Facts.WinnerPreexistingPenetration - 10f) < 0.5f && r2.Host.Change == HostChange.Removed, R(2));
        Check("SD-3: a block the MOVER introduced 10 u into the crate -> SUNK 10 inside it (nothing to forgive: it did not exist as authored)",
              Row(3) is { Verdict: ContactVerdict.Sunk } r3 && r3.Winner!.Against == C(3) && MathF.Abs(r3.Facts.WinnerPenetration - 10f) < 0.5f && r3.Facts.WinnerPreexistingPenetration == 0f, R(3));
        Check("SD-4: a master block 200 u away as authored, moved by the mover onto the crate (10 u) -> SUNK: identity alone is not pre-existence, the authoring depth is measured (0)",
              Row(4) is { Verdict: ContactVerdict.Sunk } r4 && r4.Winner!.Against == T(4) && r4.Facts.WinnerPreexistingPenetration == 0f, R(4));
        Check("SD-5a: a master block 10 u into the crate as authored, pushed by the mover to 30 u -> SUNK (20 u new > ε), facts 30 total / 10 authored",
              Row(5) is { Verdict: ContactVerdict.Sunk } r5 && r5.Winner!.Against == T(5) && MathF.Abs(r5.Facts.WinnerPenetration - 30f) < 0.5f
           && MathF.Abs(r5.Facts.WinnerPreexistingPenetration - 10f) < 0.5f && r5.Why.Contains("already as authored"), R(5));
        Check("SD-5b: the same block pushed to 13 u (3 u new, within ε) -> not Sunk: RESUPPORTED by the floor",
              Row(6) is { Verdict: ContactVerdict.ReSupported } r6 && r6.Facts.WinnerContactIsHost && !r6.Facts.WinnerPreexistingOnly, R(6));
        Check("SD-6: a master block 30 u into the crate as authored AND a mover's block 8 u into its other side -> SUNK 8 inside the MOVER's block (the deep authored overlap does not mask the new one)",
              Row(7) is { Verdict: ContactVerdict.Sunk } r7 && r7.Winner!.Against == C(7) && MathF.Abs(r7.Facts.WinnerPenetration - 8f) < 0.5f, R(7));

        // SD-7: a caller that does not pass the authoring-depth function (stateprobe's shape) reads exactly what it read before.
        {
            var dsn = cidx.Index.History.Of(D(8))!;
            var dw = dsn[^1]; var db = cidx.WinnerBase(D(8));
            var best = cidx.BestContact(D(8), dsn.Count - 1, db, dw, cidx.WorldBox(db, dw), winHash, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
            var f = new DetectorModel.ContactFacts(true, true, true, 0f, 0f, 1f, false, false, false, true,
                best?.Contact.FacingDistance ?? cidx.Reach, best?.Contact.EffectivePenetration ?? 0f, best is not null && best.Against == H(8), DependentMotion.Fixed,
                WinnerAgainstExtent: best?.AgainstExtent ?? float.NaN);
            var v = DetectorModel.PlacementContact(f);
            Check("SD-7 (guard): BestContact WITHOUT the authoring-depth function on SD-1's arrangement -> the block, 10 u, and the rule reads SUNK as before (no leak into callers that did not opt in)",
                  best is not null && best.Against == T(8) && MathF.Abs(best.Contact.EffectivePenetration - 10f) < 0.5f && best.PreexistingDepth == 0f && v.Verdict == ContactVerdict.Sunk,
                  $"against {best?.Against.ID:X} pen {best?.Contact.EffectivePenetration:0.##} -> {v.Verdict}");
        }
        Check("SD-8 (guard): Judge with the rule ON reads SD-1's twin case 8 the same way as SD-1 -> RESUPPORTED by the floor",
              Row(8) is { Verdict: ContactVerdict.ReSupported } r8 && r8.Facts.WinnerContactIsHost, R(8));

        // F3 (stage 2 review, side discovery): the shared soup / grid caches are keyed by (reference, snap index), not base. A base
        // swap with no position step adds no snap, so the authoring arm (arm base: the 40 u crate, resting on the floor) and the winner
        // arm (winner base: the short crate, 10 u above the floor) ask for the dependent's soup under ONE key. The right winner reading
        // is the short crate: nothing within ε, nearest about 10 u. The wrong one is the crate's soup reused: touching the floor.
        {
            var s9 = cidx.Index.History.Of(D(9))!;
            Check("F3 (cache key): the mover swaps Crate9's base to a short crate WITHOUT moving it -> the winner arm reads the SHORT crate (about 10 u above the floor, not touching); the authoring arm the 40 u crate (resting)",
                  Row(9) is { } r9 && r9.Facts.WinnerMeasured && MathF.Abs(r9.Facts.WinnerDistance - 10f) < 0.5f && r9.Relation == ContactRelation.Supported,
                  $"snaps {s9.Count}, arm base {cidx.ArmBase(D(9), 1, closure).ID:X}, winner base {cidx.WinnerBase(D(9)).ID:X}: {R(9)}");
        }

        try { Directory.Delete(root, true); } catch { }
    }
}
