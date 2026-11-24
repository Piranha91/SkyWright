using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — the Base Object Swapper reader (housecarl-core/BaseObjectSwaps.cs) and its hook
// (_shared/BosHook.cs). A synthetic two-plugin order written with Mutagen (a full master and a light plugin),
// temp *_SWAP.ini files in two VFS roots, every answer known before the reader runs. Cases follow the handoff's
// list: a Forms swap; References over Forms; a location filter admitting one cell and not another; an exclusion;
// a keyword filter through the location; a multi-swap set as a SET; chance(50) as a set with the probability;
// posR / posA / rotR / scale / scaleA / flags; a [Transforms] override on a swap with no properties, and one
// that loses to the swap line's own; an EditorID resolving to nothing, counted; an unknown token, counted; the
// reverse-ordinal precedence between filtered sections; the leveled-item rule; the actor exclusion; the
// light-plugin id forms; discovery's case rules; SimpleIni's merge/collapse/empty-token rules; the hook's steps.
static class BosProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — the Base Object Swapper reader (housecarl-core/BaseObjectSwaps.cs, _shared/BosHook.cs)");
        Console.WriteLine("   A synthetic order and temp *_SWAP.ini files; every outcome known by construction.\n");

        var root = Path.Combine(Path.GetTempPath(), "proofprobe-bos-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { RunInner(Check, root); RunHooks(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, float scale = 1f, string? edid = null)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (scale != 1f) r.Scale = scale;
        return r;
    }

    static void RunInner(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("BosMaster", ModType.Master);
        var lKey = new ModKey("BosL", ModType.Light);
        FormKey M(uint id) => new(mKey, id);
        FormKey S(int n) => M((uint)(0x800 + n));                       // statics S1..S7 = 0x801..0x807
        var lvlK = M(0x810); var kwK = M(0x811);
        var locA = M(0x820); var locB = M(0x821); var locC = M(0x822);
        var cellA = M(0x830); var cellB = M(0x831); var cellC = M(0x832);
        var ezK = M(0x840); var npcK = M(0x850);
        FormKey R(int n) => M((uint)(0xA00 + n));
        var sl1 = new FormKey(lKey, 0x801);

        // ---- the master: seven statics, a leveled item, a keyword, three locations, three cells, the refs ----
        var master = new SkyrimMod(mKey, release);
        for (int n = 1; n <= 7; n++)
            master.Statics.Add(new Static(S(n), release) { EditorID = $"S{n}", Model = new Model { File = $"meshes\\bos\\s{n}.nif" } });
        master.Keywords.Add(new Keyword(kwK, release) { EditorID = "BosKw" });
        var la = new Location(locA, release) { EditorID = "LocA" };
        var lb = new Location(locB, release) { EditorID = "LocB" };
        lb.ParentLocation.SetTo(locA);
        lb.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kwK) };
        var lc = new Location(locC, release) { EditorID = "LocC" };
        master.Locations.Add(la); master.Locations.Add(lb); master.Locations.Add(lc);
        master.EncounterZones.Add(new EncounterZone(ezK, release) { EditorID = "BosEZ" });
        var lvl = new LeveledItem(lvlK, release) { EditorID = "BosLvl", Entries = new ExtendedList<LeveledItemEntry>() };
        foreach (var s in new[] { S(5), S(3) })
        {
            var e = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1 } };
            e.Data.Reference.SetTo(s);
            lvl.Entries.Add(e);
        }
        master.LeveledItems.Add(lvl);
        master.Npcs.Add(new Npc(npcK, release) { EditorID = "BosNpc" });

        var ca = new Cell(cellA, release) { EditorID = "BosCellA", Flags = Cell.Flag.IsInteriorCell }; ca.Location.SetTo(locB);
        var cb = new Cell(cellB, release) { EditorID = "BosCellB", Flags = Cell.Flag.IsInteriorCell }; cb.Location.SetTo(locC);
        var cc = new Cell(cellC, release) { EditorID = "BosCellC", Flags = Cell.Flag.IsInteriorCell };
        ca.Temporary.Add(Ref(R(1), S(1), 100));                       // References -> S3
        cb.Temporary.Add(Ref(R(2), S(1), 200, 0, 50));                // plain Forms S1|S2 posR(0,0,10); a [Transforms] posA loses
        ca.Temporary.Add(Ref(R(3), S(2), 300));                       // two filtered sections admit it: LocA wins over BosCellA (reverse ordinal)
        cb.Temporary.Add(Ref(R(8), S(2), 800));                       // neither filter admits CellB -> unchanged
        ca.Temporary.Add(Ref(R(4), S(3), 400));                       // excluded by -LocA -> stays S3, self-swap line scales it x2
        cb.Temporary.Add(Ref(R(9), S(3), 900));                       // not excluded -> S5
        cc.Temporary.Add(Ref(R(5), S(4), 500));                       // plain: S4|S6 chance(50) then S4|S5 -> {S6 .5, S5 .5}, first S6
        ca.Temporary.Add(Ref(R(10), S(4), 1000));                     // keyword filter through LocB -> S6
        cc.Temporary.Add(Ref(R(6), S(7), 600));                       // set S7|S5,S6 -> {S5,S6}; [Transforms] by ref rotR applies (swap line has NONE)
        ca.Temporary.Add(Ref(R(11), S(6), 1100));                     // leveled: S6|BosLvl -> {S5,S3}; filtered [Transforms|LocA,,LocC] flags(0x800)
        var ro12 = Ref(R(12), S(6), 1200); ro12.EncounterZone.SetTo(ezK); ca.Temporary.Add(ro12);   // leveled cancelled by the EZ
        ca.Temporary.Add(Ref(R(13), S(5), 1300));                     // [Forms|NotAnEdid] never matches -> unchanged
        cc.Temporary.Add(Ref(R(15), S(6), 1500));                     // leveled; no location -> plain [Transforms] S6|scaleA(50)
        var pn = new PlacedNpc(R(14), release) { Placement = new Placement { Position = new P3Float(1400, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        pn.Base.SetTo(npcK); ca.Temporary.Add(pn);                    // an actor: never swapped
        ca.Temporary.Add(Ref(R(16), sl1, 1600));                      // base in the light plugin: [Forms] 0x801~BosL.esl|S2
        ca.Temporary.Add(Ref(R(17), S(5), 1700, edid: "BosRefTemp"));  // a TEMPORARY ref with an EditorID: not resolvable at ini load
        ca.Persistent.Add(Ref(R(18), S(5), 1800, edid: "BosRefPers")); // a PERSISTENT ref with an EditorID: resolvable
        FileInterior(master, ca); FileInterior(master, cb); FileInterior(master, cc);
        var mPath = Path.Combine(root, mKey.FileName.String);
        master.BeginWrite.ToPath(mPath).WithNoLoadOrder().Write();

        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);
        var light = new SkyrimMod(lKey, release) { IsSmallMaster = true };
        light.Statics.Add(new Static(sl1, release) { EditorID = "SL1", Model = new Model { File = "meshes\\bos\\sl1.nif" } });
        var lPath = Path.Combine(root, lKey.FileName.String);
        light.BeginWrite.ToPath(lPath).WithLoadOrder(mOv).Write();

        // ---- the inis: two roots, the first winning a shared name; near-misses the game never reads ----
        var root1 = Path.Combine(root, "mod1"); var root2 = Path.Combine(root, "mod2");
        Directory.CreateDirectory(Path.Combine(root1, "sub")); Directory.CreateDirectory(root2);
        File.WriteAllText(Path.Combine(root1, "Test_SWAP.ini"), string.Join("\n", new[]
        {
            "; a comment", "# also a comment",
            "[Forms]",
            "S7|S5,S6",
            "S1|S2|posR(0,0,10)",
            "S4|S5",
            "S4|S6|NONE|chance(50)",
            "S6|BosLvl",
            "Nope|S2",
            "S1|Nope",
            "S3|S3",
            "S3|S3|scale(2.0),wobble(1)",
            "S1|S2|posR(0,0,10)",                      // duplicate key: collapses onto the first occurrence
            "0x801~BosL.esl|S2",
            "[References]",
            "0x00000A01~BosMaster.esm|S3",
            "BosRefTemp|S4",                            // a temporary ref by EditorID: the game cannot find it when it reads the inis
            "BosRefPers|S4",                            // a persistent ref by EditorID: it can
            "[forms]",                                  // merges into [Forms]
            "S2|S7|NONE|chance(0)",                     // a 0 % line: never passes
            "[Forms|LocA]",
            "S2|S4",
            "[Forms|BosCellA]",
            "S2|S5",
            "[Forms|-LocA]",
            "S3|S5",
            "[Forms|BosKw]",
            "S4|S6",
            "[Forms|NotAnEdid]",
            "S5|S6",
            "[Transforms]",
            "0x00000A06~BosMaster.esm|rotR(0,0,90)",
            "0x00000A02~BosMaster.esm|posA(0,0,0)",
            "S6|scaleA(50)",
            "[Transforms|LocA,,LocC]",
            "S6|flags(0x800)",
            "[Broken",                                  // unterminated header: skipped, the next key lands in the section above
            "S1|S2",
            "",
        }));
        File.WriteAllText(Path.Combine(root1, "zz_SWAP.ini"), "; nothing here\n");
        File.WriteAllText(Path.Combine(root1, "NotASwap.ini"), "[Forms]\nS1|S2\n");
        File.WriteAllText(Path.Combine(root1, "lower_swap.ini"), "[Forms]\nS1|S2\n");
        File.WriteAllText(Path.Combine(root1, "Upper_SWAP.INI"), "[Forms]\nS1|S2\n");
        File.WriteAllText(Path.Combine(root1, "sub", "Deep_SWAP.ini"), "[Forms]\nS1|S2\n");
        File.WriteAllText(Path.Combine(root2, "Test_SWAP.ini"), "[Forms]\nS1|S7\n");   // shadowed by mod1's
        File.WriteAllText(Path.Combine(root2, "Other_SWAP.ini"), "[Forms]\nS7|S7|posA(1,2,3)\n");

        // ---- discovery ----
        var inis = BosDiscovery.Discover(new[] { ("mod1", root1), ("mod2", root2) });
        Check("bos/discovery: three inis read — Data root only, '_SWAP' and '.ini' case-sensitive, ordinal order",
              inis.Select(i => i.Name).SequenceEqual(new[] { "Other_SWAP.ini", "Test_SWAP.ini", "zz_SWAP.ini" }),
              string.Join(", ", inis.Select(i => i.Name)));
        Check("bos/discovery: the higher root wins a shared name and the loser is named as shadowed",
              inis.FirstOrDefault(i => i.Name == "Test_SWAP.ini") is { Provider: "mod1", ShadowedProviders: { Count: 1 } } t && t.ShadowedProviders[0] == "mod2",
              inis.FirstOrDefault(i => i.Name == "Test_SWAP.ini")?.Provider ?? "-");
        Check("bos/discovery: 'lower_swap.ini', 'Upper_SWAP.INI', a subfolder's ini and 'NotASwap.ini' are not read",
              !BosDiscovery.Matches("lower_swap.ini") && !BosDiscovery.Matches("Upper_SWAP.INI") && !BosDiscovery.Matches("NotASwap.ini") && BosDiscovery.Matches("X_SWAPPED.ini"),
              "as the game's get_configs compares them");

        // ---- reading rules ----
        var doc = BosIniReader.Read(File.ReadAllText(Path.Combine(root1, "Test_SWAP.ini")));
        var forms = doc.Sections.FirstOrDefault(s => s.Name == "Forms");
        Check("bos/read: [forms] merges into [Forms] (case-insensitive) and a duplicate key collapses onto its first occurrence",
              forms is not null && doc.Sections.Count(s => s.Head.Equals("Forms", StringComparison.OrdinalIgnoreCase) && !s.Conditional) == 1
              && forms.Keys.Count == 11 && forms.Keys.Any(k => k.Text == "S2|S7|NONE|chance(0)"),
              $"{forms?.Keys.Count} keys in the merged section (expect 11)");
        Check("bos/read: '[Broken' is skipped and its key lands in the section above; an empty filter token is counted",
              doc.Sections.FirstOrDefault(s => s.Name == "Transforms|LocA,,LocC") is { } tr && tr.Keys.Count == 2 && tr.Conditions.Length == 3 && tr.Conditions[1] == "",
              string.Join(" | ", doc.Anomalies.Where(a => a.Contains("'['")).Take(1)));
        Check("bos/read: a conditional section whose head is not 'Forms' is property overrides; a plain unknown name is References",
              new BosSection("Transforms|X", "Transforms", "X", 1, Array.Empty<BosKey>()).Kind == BosSectionKind.Properties
              && new BosSection("Reference", "Reference", null, 1, Array.Empty<BosKey>()).Kind == BosSectionKind.References
              && new BosSection("Properties", "Properties", null, 1, Array.Empty<BosKey>()).Kind == BosSectionKind.Properties,
              "the else branches of Manager::LoadForms");

        // ---- grammar ----
        var props = BosProperties.Parse("posR(1,2,3/4),rotA(0, 0, 90),scale(1.0/2.0),flags(0x800),flagsC(0x20000000),wobble(1)");
        Check("bos/grammar: posR is relative with a z range, rotA absolute in radians, scale a range, flags set and cleared, an unknown token counted",
              props.Position is { Relative: true, X.Min: 1f, Z.Min: 3f, Z.Max: 4f }
              && props.Rotation is { Relative: false } rr && MathF.Abs(rr.Z.Min - MathF.PI / 2) < 1e-5f
              && props.Scale is { Absolute: false, Value.Min: 1f, Value.Max: 2f }
              && props.FlagsSet == 0x800 && props.FlagsUnset == 0x20000000
              && props.UnknownTokens.Count == 1 && props.UnknownTokens[0] == "wobble(1)" && props.Random,
              $"pos {props.Position?.X}/{props.Position?.Z} rot {props.Rotation?.Z} scale {props.Scale?.Value} flags {props.FlagsSet:X}/{props.FlagsUnset:X} unknown {props.UnknownTokens.Count}");
        Check("bos/grammar: scaleA(50) is absolute in the percent domain (0.50); scale(1.49) on 1.0 gives 1.49; NONE is no property",
              BosProperties.Parse("scaleA(50)").Scale!.Apply(1f) == 0.5f && BosProperties.Parse("scale(1.49)").Scale!.Apply(1f) == 1.49f && !BosProperties.Parse("NONE").IsValid,
              $"{BosProperties.Parse("scaleA(50)").Scale!.Apply(1f)} {BosProperties.Parse("scale(1.49)").Scale!.Apply(1f)}");
        Check("bos/grammar: flags(0x0000C000) reads as a CLEAR because the token contains 'C' — BOS's own test, mirrored and noted",
              BosProperties.Parse("flags(0x0000C000)") is { FlagsUnset: 0xC000, FlagsSet: 0 } fc && fc.Notes.Count == 1, "the note names the digit");
        var ch = BosChance.Parse("chanceR(50, 1234)");
        Check("bos/grammar: chanceR(50, 1234) is 50 %, Random, seed 1234 (fixed, not per-session); chanceL is LocationHash; chance(10) is RefHash; 'NONE' and '60' are 100 %",
              ch is { Value: 50f, Type: BosChanceType.Random, Seed: 1234, PerSession: false }
              && BosChance.Parse("chanceL(25)").Type == BosChanceType.LocationHash
              && BosChance.Parse("chance(10)") is { Type: BosChanceType.RefHash, Value: 10f }
              && BosChance.Parse("chanceR(30)").PerSession
              && BosChance.Parse("NONE").Certain && BosChance.Parse("60").Certain,
              $"{ch.Value} {ch.Type} {ch.Seed}");

        // ---- the index over the synthetic order, read back through the real ingest ----
        var idx = new OverlapIndex { Bos = new BosFormIndex() };
        var order = new[] { mPath, lPath };
        var lightness = new List<(ModKey, bool)>();
        for (int p = 0; p < order.Length; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], release);
            lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
            idx.Ingest(p, mod);
        }
        idx.Bos!.SetLoadOrder(lightness);
        Check("bos/ids: the light plugin reads back light; 0x801~BosL.esl and bare 0xFE000801 both name SL1; bare 0x00000801 names S1",
              lightness[1].Item2 && idx.Bos.Resolve("0x801~BosL.esl").Key == sl1 && idx.Bos.Resolve("0xFE000801").Key == sl1 && idx.Bos.Resolve("0x00000801").Key == S(1),
              $"{idx.Bos.Resolve("0x801~BosL.esl").Key} {idx.Bos.Resolve("0xFE000801").Key} {idx.Bos.Resolve("0x00000801").Key}");
        Check("bos/ids: an EditorID resolves case-insensitively; a '~' id on a plugin not in the order is not found; one naming no record is found but does not exist",
              idx.Bos.Resolve("s3").Key == S(3) && !idx.Bos.Resolve("0x801~Missing.esp").Found && idx.Bos.Resolve("0xFFF~BosMaster.esm") is { Found: true, Exists: false } && !idx.Bos.Resolve("Nope").Found,
              "util::GetFormWithID's three branches");
        Check("bos/ids: a '~' spelling with three parts falls through to the EditorID lookup and fails, as in BOS",
              !idx.Bos.Resolve("0x1~A~B").Found, "REX::STR::SPLIT(...).size() == 2 required");

        var layer = BaseObjectSwaps.Load(inis, idx.Bos);
        var log = layer.GameLogLines().ToList();
        Check("bos/log: the account carries the game's shapes — the ini count, 'reading [Forms] : 3 conditions', '11 swaps found', the three fail messages, the text-filter note",
              log[0] == "3 matching inis found..."
              && log.Contains("\treading [Transforms] : 3 conditions") && log.Contains("\t\t\t11 swaps found")
              && log.Contains("\t\t\t\tfail : [Nope|S2] (BASE formID not found)") && log.Contains("\t\t\t\tfail : [S1|Nope] (SWAP formID not found)")
              && log.Contains("\t\t\t\tfail : [S3|S3] (BASE formID == SWAP formID)")
              && log.Contains("\t\tFilter [NotAnEdid] INFO - unable to find form, treating filter as FF keyword or cell editorID"),
              string.Join(" / ", log.Where(l => l.Contains("fail") || l.Contains("Filter")).Select(l => l.Trim())));
        Check("bos/layer: four failures counted, two unknown property tokens ('wobble(1)', and 'S2' on the stray line), unresolved EditorID bases, the '[Broken' line noted",
              layer.LinesFailed == 4 && layer.UnknownPropertyTokens == 2 && layer.EditorIdsUnresolved >= 2 && layer.Anomalies.Any(a => a.Contains("'['")),
              $"failed {layer.LinesFailed} unknown {layer.UnknownPropertyTokens} unresolved {layer.EditorIdsUnresolved}");
        Check("bos/ids: a TEMPORARY reference's EditorID is not resolvable when BOS reads the inis (the game's log on a real order, 2026-09-04); a PERSISTENT one is",
              !idx.Bos.Resolve("BosRefTemp").Found && idx.Bos.Resolve("BosRefPers") is { Found: true, Key: var pk } && pk == R(18)
              && log.Contains("\t\t\t\tfail : [BosRefTemp|S4] (BASE formID not found)") && layer.SwapRefs.ContainsKey(R(18)),
              $"{idx.Bos.Resolve("BosRefTemp").Note}");

        BaseObjectSwaps.BosResolution Res(FormKey r)
        {
            var w = idx.History.Winning(r)!.Value;
            idx.RefEncounterZone.TryGetValue(r, out var ez);
            return BaseObjectSwaps.Resolve(layer, idx.Bos!, r, idx.RefArm[r], idx.RefBase[r], w.Cell, ez == default ? null : ez);
        }
        var r1 = Res(R(1)); var r2 = Res(R(2)); var r3 = Res(R(3)); var r8 = Res(R(8)); var r4 = Res(R(4)); var r9 = Res(R(9));
        var r5 = Res(R(5)); var r10 = Res(R(10)); var r6 = Res(R(6)); var r11 = Res(R(11)); var r12 = Res(R(12)); var r13 = Res(R(13));
        var r15 = Res(R(15)); var r14 = Res(R(14)); var r16 = Res(R(16));

        Check("bos/resolve: a plain [Forms] swap — R2 (S1) becomes S2, certain", r2 is { Swapped: true, Random: false } && r2.EffectiveBase == S(2), $"{r2.EffectiveBase}");
        Check("bos/resolve: [References] beats [Forms] — R1 (S1) becomes S3, not S2", r1.Swapped && r1.EffectiveBase == S(3), $"{r1.EffectiveBase}");
        Check("bos/resolve: a location filter admits the cell whose location's PARENT it names (R3 in CellA via LocB -> LocA) and not another (R8 in CellB)",
              r3.Swapped && r8 is { Swapped: false, Random: false } && r8.EffectiveBase == S(2), $"R3 {r3.EffectiveBase} R8 {r8.EffectiveBase}");
        Check("bos/resolve: two filtered sections admit R3 — the one later in ORDINAL order of 'ini|conditions' wins (LocA over BosCellA): S4, not S5",
              r3.EffectiveBase == S(4), $"{r3.EffectiveBase}");
        Check("bos/resolve: an exclusion — R4 (S3, in LocA) keeps S3; R9 (S3, CellB) becomes S5",
              !r4.Swapped && r4.EffectiveBase == S(3) && r9.Swapped && r9.EffectiveBase == S(5), $"R4 {r4.EffectiveBase} R9 {r9.EffectiveBase}");
        Check("bos/resolve: a self-swap line with properties is the transform idiom — R4 keeps S3 and gets scale x2 from the line",
              r4.Properties is { Scale: { Absolute: false } } && r4.Apply(default, default, 1f, false).Scale == 2f && r4.PropertiesSource == "the swap line",
              $"scale {r4.Apply(default, default, 1f, false).Scale} from {r4.PropertiesSource}");
        Check("bos/resolve: a keyword filter through the location — R10 (S4 in CellA, LocB carries BosKw) becomes S6; R5 (S4 in CellC) is not admitted",
              r10.Swapped && r10.EffectiveBase == S(6) && !r5.Alternatives.Any(a => a.From?.Where.Condition == "BosKw"), $"R10 {r10.EffectiveBase}");
        Check("bos/resolve: chance(50) then a certain line, last first — R5 is a SET {S6 .5, S5 .5}, Random, First = S6",
              r5 is { Random: true, Swapped: true } && r5.EffectiveBase == S(6)
              && r5.Alternatives.Count == 2 && Math.Abs(r5.Alternatives.First(a => a.Base == S(6)).Probability - 0.5) < 1e-9 && Math.Abs(r5.Alternatives.First(a => a.Base == S(5)).Probability - 0.5) < 1e-9,
              string.Join(", ", r5.Alternatives.Select(a => $"{a.Base} {a.Probability:0.00}")));
        Check("bos/resolve: a multi-swap line is a SET — R6 (S7) is {S5 .5, S6 .5}, Random, First = the lower runtime id (S5)",
              r6 is { Random: true, Swapped: true } && r6.EffectiveBase == S(5) && r6.Alternatives.Count == 2 && r6.Alternatives.All(a => Math.Abs(a.Probability - 0.5) < 1e-9),
              string.Join(", ", r6.Alternatives.Select(a => $"{a.Base} {a.Probability:0.00}")));
        Check("bos/resolve: a 0 % line never passes — no R2 alternative comes from 'S2|S7|NONE|chance(0)'",
              !r3.Alternatives.Any(a => a.Base == S(7)) && !r8.Alternatives.Any(a => a.Base == S(7)), "chance(0)");
        var a2 = r2.Apply(new Vec3(200, 0, 50), default, 1f, false);
        Check("bos/transform: the swap line's posR(0,0,10) moves R2 to z 60 and the [Transforms] posA(0,0,0) by reference LOSES to it",
              a2.Position.Z == 60f && a2.Position.X == 200f && r2.PropertiesSource == "the swap line", $"{a2.Position} from {r2.PropertiesSource}");
        var a6 = r6.Apply(new Vec3(600, 0, 0), new Vec3(0, 0, 0), 1f, false);
        Check("bos/transform: a swap with NONE properties takes the [Transforms] line by reference — R6 rotR(0,0,90) is +pi/2 about z",
              MathF.Abs(a6.Rotation.Z - MathF.PI / 2) < 1e-5f && a6.Position.Z == 0f && r6.PropertiesSource == "[Transforms] by reference", $"{a6.Rotation} from {r6.PropertiesSource}");
        Check("bos/transform: a filtered [Transforms|LocA,,LocC] by base applies in CellA — R11 gets flags(0x800), initially disabled",
              r11.Properties is { FlagsSet: 0x800 } && r11.Apply(default, default, 1f, false).InitiallyDisabled && r11.PropertiesSource == "filtered [Transforms] by base",
              $"{r11.PropertiesSource}");
        Check("bos/transform: in CellC the filter fails and the plain [Transforms] S6|scaleA(50) applies — R15 scale 0.50",
              r15.Properties is { Scale: { Absolute: true } } && r15.Apply(default, default, 1f, false).Scale == 0.5f && r15.PropertiesSource == "[Transforms] by base",
              $"{r15.Apply(default, default, 1f, false).Scale} from {r15.PropertiesSource}");
        Check("bos/leveled: a swap to a leveled item is a SET of its entries, Random and Leveled — R11 {S5 .5, S3 .5}",
              r11 is { Leveled: true, Random: true, Swapped: true } && r11.Alternatives.Count(a => a.ViaLeveledItem) == 2 && r11.Alternatives.All(a => a.ViaLeveledItem),
              string.Join(", ", r11.Alternatives.Select(a => $"{a.Base} {a.Probability:0.00}")));
        Check("bos/leveled: an encounter zone on the reference cancels the leveled swap — R12 keeps S6, noted",
              !r12.Swapped && r12.EffectiveBase == S(6) && r12.Notes.Any(n => n.Contains("encounter zone")), string.Join(" | ", r12.Notes));
        Check("bos/filter: an unresolvable filter is text and matches nothing here — R13 (S5) is unchanged", !r13.Swapped && !r13.Random, $"{r13.EffectiveBase}");
        Check("bos/scope: a placed actor is never swapped (Character has its own vtable)", !r14.Swapped && r14.Notes.Any(n => n.Contains("vtable")), string.Join(" | ", r14.Notes));
        Check("bos/ids: a base defined in the light plugin is swapped through '0x801~BosL.esl' — R16 becomes S2", r16.Swapped && r16.EffectiveBase == S(2), $"{r16.EffectiveBase}");

        // ---- the hook: what the shared index reads afterwards ----
        int bosPlugin = order.Length;
        var rep = BosHook.Apply(idx, layer, bosPlugin);
        Check("bos/hook: RefBase is the effective base and RecordBase keeps the record's; BaseChanges gains the BOS step",
              idx.RefBase[R(1)] == S(3) && idx.RecordBase[R(1)] == S(1) && idx.BaseChanges[R(1)][^1] == (bosPlugin, S(3)),
              $"{idx.RefBase[R(1)]} was {idx.RecordBase.GetValueOrDefault(R(1))}");
        var h2 = idx.History.Of(R(2))!;
        Check("bos/hook: a moving override is a placement step under the BOS pseudo-plugin — R2 gains a snap at z 60",
              h2.Count == 2 && h2[^1].Plugin == bosPlugin && h2[^1].Pos.Z == 60f, $"{h2.Count} snaps, last {h2[^1].Plugin} at {h2[^1].Pos}");
        var h6 = idx.History.Of(R(6))!;
        Check("bos/hook: rotation- and scale-only overrides are not steps (the history's standing rule) and are counted — R6 rot, R4 scale, R15 scaleA",
              h6.Count == 1 && rep.SilentTransforms == 3, $"{h6.Count} snaps, silent {rep.SilentTransforms}");
        Check("bos/hook: a flag change is a step — R11's snap under BOS is disabled", idx.History.Winning(R(11)) is { Plugin: var bp, Disabled: true } && bp == bosPlugin, $"{idx.History.Winning(R(11))}");
        Check("bos/hook: the tally — 11 refs swapped (R1 R2 R3 R5 R6 R9 R10 R11 R15 R16 R18), 1 actor set aside, randoms and leveled counted",
              rep.Swapped == 11 && rep.NotSwappable == 1 && rep.Random == 5 && rep.Leveled == 3 && rep.TransformSteps == 3 && rep.FlagChanges == 2,
              $"swapped {rep.Swapped} notSwappable {rep.NotSwappable} random {rep.Random} leveled {rep.Leveled} steps {rep.TransformSteps} flags {rep.FlagChanges}");
    }

    // ---- the hooks for the probes that walk the order THEMSELVES (2026-09-04, roadmap 4.10's tail): a BosFeed fed in the
    //      walk, Effective per reference for the route rule (blockprobe, navprobe, corridorprobe), Apply(history, …) for
    //      driftprobe's own placement history. One resolve step behind both paths, so a sabotage of it reddens every case
    //      here at once. The corridor is crossprobe's synthetic strip; the obstacles are boxes keyed by base, so a swap to a
    //      wider base, a transform onto the line and a flags(0x800) removal each flip the route-cut verdict by construction. ----
    static void RunHooks(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var hKey = new ModKey("BosHooks", ModType.Master);
        FormKey H(uint id) => new(hKey, id);
        var postA = H(0x901); var wall = H(0x902); var marker = H(0x903); var post = H(0x904);
        var cellR = H(0x910); var cellD = H(0x911);
        FormKey R(int n) => H((uint)(0xB00 + n));

        var master = new SkyrimMod(hKey, release);
        master.Statics.Add(new Static(postA, release) { EditorID = "HPostA", Model = new Model { File = "meshes\\bos\\post.nif" } });
        master.Statics.Add(new Static(post, release) { EditorID = "HPost", Model = new Model { File = "meshes\\bos\\post.nif" } });
        master.Statics.Add(new Static(wall, release) { EditorID = "HWall", Model = new Model { File = "meshes\\bos\\wall.nif" } });
        master.Statics.Add(new Static(marker, release) { EditorID = "HMarker", Model = new Model { File = "meshes\\bos\\marker.nif" } });
        var cr = new Cell(cellR, release) { EditorID = "BosRouteCell", Flags = Cell.Flag.IsInteriorCell };
        var cd = new Cell(cellD, release) { EditorID = "BosDriftCell", Flags = Cell.Flag.IsInteriorCell };
        // The corridor is a 128 u strip along X (Y 3..131; the path line runs at Y 67); every obstacle stands on it.
        cr.Temporary.Add(Ref(R(1), postA, 0, 20));       // [Forms] HPostA|HWall: a post beside the line becomes a wall across it
        cr.Temporary.Add(Ref(R(2), post, 300, 20));      // [Transforms] by ref posR(0,47,0): the post is carried onto the line
        cr.Temporary.Add(Ref(R(3), wall, -300, 67));     // [Transforms] by ref flags(0x800): a wall across the line, disabled at init
        cr.Temporary.Add(Ref(R(4), post, -100, 20));     // untouched: the control, beside the line in both arms
        cd.Temporary.Add(Ref(R(5), marker, 0, 0));       // [Transforms] by ref rotR(0,0,90): no step
        cd.Temporary.Add(Ref(R(6), marker, 100, 0));     // [Transforms] by ref posA(2000,0,0): the pair splits
        FileInterior(master, cr); FileInterior(master, cd);
        var hPath = Path.Combine(root, hKey.FileName.String);
        master.BeginWrite.ToPath(hPath).WithNoLoadOrder().Write();

        var hRoot = Path.Combine(root, "hooks"); Directory.CreateDirectory(hRoot);
        File.WriteAllText(Path.Combine(hRoot, "Hooks_SWAP.ini"), string.Join("\n", new[]
        {
            "[Forms]",
            "HPostA|HWall",
            "[Transforms]",
            "0x00000B02~BosHooks.esm|posR(0,47,0)",
            "0x00000B03~BosHooks.esm|flags(0x800)",
            "0x00000B05~BosHooks.esm|rotR(0,0,90)",
            "0x00000B06~BosHooks.esm|posA(2000,0,0)",
            "",
        }));

        // ---- the walk, as the four probes make it: the feed, a placement history, the base and arm maps ----
        var feed = new BosFeed(true);
        var history = new PlacementHistory();
        var refBase = new Dictionary<FormKey, FormKey>(); var refArm = new Dictionary<FormKey, string>(); var refEz = new Dictionary<FormKey, FormKey>();
        var winRef = new Dictionary<FormKey, PlacedRef>(); var refCell = new Dictionary<FormKey, FormKey>();
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(hPath, release))
        {
            feed.Plugin(mod);
            foreach (var rec in mod.EnumerateMajorRecords()) feed.Record(rec);
            foreach (var cp in PlacementExtract.FromMod(mod))
            {
                feed.Cell(cp);
                foreach (var r in cp.Refs)
                {
                    winRef[r.FormKey] = r; refCell[r.FormKey] = cp.Cell;
                    history.Record(r.FormKey, new Snap(0, r.Position, cp.Cell, r.InitiallyDisabled, r.Deleted, cp.Worldspace));
                    if (r.Base is { } b) refBase[r.FormKey] = b;
                    refArm[r.FormKey] = r.RecordType;
                    if (r.EncounterZone is { } ez) refEz[r.FormKey] = ez;
                }
            }
        }
        var layer = feed.Load(new[] { ("hooks", hRoot) });
        Check("bos/feed: the walk fed the index and Load read the hooks ini — 5 lines, none refused, the ref ids resolved",
              layer.LinesRead == 5 && layer.LinesFailed == 0 && layer.SwapForms.ContainsKey(postA) && feed.Index!.Resolve("0x00000B02~BosHooks.esm").Key == R(2),
              $"read {layer.LinesRead} failed {layer.LinesFailed}");

        // ---- the route rule on the effective geometry: crossprobe's synthetic corridor, obstacles as boxes by base ----
        var corridor = NavmeshIndex.Build(new[] { new NavmeshGeometry(new FormKey(ModKey.FromNameAndExtension("synthetic.esp"), 1),
            new[] { new Vec3(-600, 3, 0), new Vec3(600, 3, 0), new Vec3(600, 131, 0), new Vec3(-600, 131, 0) },
            new[] { new NavTri(0, 1, 2, 0, -1, -1, 1), new NavTri(0, 2, 3, 0, 0, -1, -1) },
            new Vec3(-600, 3, 0), new Vec3(600, 131, 0), Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>()) }, out _);
        var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
        // Model-space boxes about the reference: a 20 u post, or a 64 x 160 u wall; both 100 u tall.
        (Vector3 Lo, Vector3 Hi) Shape(FormKey b) => b == wall ? (new Vector3(-32, -80, 0), new Vector3(32, 80, 100)) : (new Vector3(-10, -10, 0), new Vector3(10, 10, 100));
        RouteCutVerdict Judge(BosEffective e)
        {
            var (lo, hi) = Shape(e.Base);
            var box = new Aabb(new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, hi.Z), false);
            var world = SpatialOverlap.ToWorld(box, e.Position, e.Rotation, e.Scale);
            var v = new[] { new Vector3(lo.X, lo.Y, lo.Z), new Vector3(hi.X, lo.Y, lo.Z), new Vector3(hi.X, hi.Y, lo.Z), new Vector3(lo.X, hi.Y, lo.Z),
                            new Vector3(lo.X, lo.Y, hi.Z), new Vector3(hi.X, lo.Y, hi.Z), new Vector3(hi.X, hi.Y, hi.Z), new Vector3(lo.X, hi.Y, hi.Z) };
            var idx = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 3, 0, 4, 3, 4, 7 };
            var m = SpatialOverlap.RefrRotation(e.Rotation);
            var t = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
            var w = new Vector3[v.Length];
            for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(v[i] * e.Scale, m) + t;
            var facts = RouteConnectivity.Measure(corridor, MeshColumnIndex.Build(w, idx), world, 256f, 16f, 32f, 112f, ladder);
            return DetectorModel.RouteCut(facts.Judged, facts.Touched, facts.Line?.Found ?? false, facts.Line?.Why ?? "", facts.Line?.Cut ?? false, facts.Line?.Passage ?? float.NaN).Verdict;
        }

        var off = new BosFeed(false); var offRep = new BosApplyReport(); var rep = new BosApplyReport();
        BosEffective Off(int n) => off.Effective(winRef[R(n)], refCell[R(n)], offRep);
        BosEffective On(int n) => feed.Effective(winRef[R(n)], refCell[R(n)], rep);
        var e1off = Off(1); var e1on = On(1);
        Check("bos/route: --bos off returns the record's own base and pose, no resolution, nothing tallied",
              e1off.Base == postA && e1off.Resolution is null && e1off.Position == winRef[R(1)].Position && offRep.Considered == 0, $"{e1off.Base} at {e1off.Position}");
        Check("bos/route: a [Forms] swap to a wider base — the record's post beside the line is CLIPPED, the effective wall across it is BLOCKED",
              e1on.Base == wall && e1on.Swapped && Judge(e1off) == RouteCutVerdict.Clipped && Judge(e1on) == RouteCutVerdict.Blocked,
              $"off {Judge(e1off)} ({e1off.Base}) -> on {Judge(e1on)} ({e1on.Base})");
        var e2off = Off(2); var e2on = On(2);
        Check("bos/route: a posR override carries the post onto the path line — CLIPPED at the record's pose, LIKELY-BLOCKED at the effective one (y 20 -> 67)",
              e2on.Position.Y == 67f && !e2on.Swapped && Judge(e2off) == RouteCutVerdict.Clipped && Judge(e2on) == RouteCutVerdict.LikelyBlocked,
              $"off {Judge(e2off)} at y {e2off.Position.Y} -> on {Judge(e2on)} at y {e2on.Position.Y}");
        var e3off = Off(3); var e3on = On(3);
        Check("bos/route: a flags(0x800) override reads back initially-disabled — the wall across the line is BLOCKED under off and not an obstacle under on",
              !winRef[R(3)].InitiallyDisabled && e3on.InitiallyDisabled && Judge(e3off) == RouteCutVerdict.Blocked && rep.Disabled == 1,
              $"off {Judge(e3off)}; on disabled={e3on.InitiallyDisabled}");
        var e4on = On(4);
        Check("bos/route: an untouched reference reads the same under both arms — CLIPPED, no resolution kept",
              e4on.Resolution is null && !e4on.Touched && Judge(e4on) == RouteCutVerdict.Clipped && Judge(Off(4)) == RouteCutVerdict.Clipped, $"{Judge(e4on)}");
        Check("bos/route: the direct tally — 4 considered, 1 swapped, 2 property overrides, 2 steps (a move, a flag), 1 flag change, 1 disabled; the summary says 'applied'",
              rep.Direct && rep.Considered == 4 && rep.Swapped == 1 && rep.WithProperties == 2 && rep.TransformSteps == 2 && rep.FlagChanges == 1 && rep.Disabled == 1 && rep.Summary().First().Contains("applied"),
              $"considered {rep.Considered} swapped {rep.Swapped} props {rep.WithProperties} steps {rep.TransformSteps} flags {rep.FlagChanges} disabled {rep.Disabled}");

        // ---- driftprobe's path: the layer applied to the probe's own history, the pseudo-plugin after the one real plugin ----
        var recordBase = new Dictionary<FormKey, FormKey>(); var swaps = new Dictionary<FormKey, BaseObjectSwaps.BosResolution>();
        const int bosPlugin = 1;
        var hrep = BosHook.Apply(history, refBase, refArm, refEz, feed.Index!, layer, bosPlugin, recordBase, swaps);
        var closure = DetectorModel.MasterClosure(new[] { new HashSet<int>(), BosHook.PseudoMasters(1) });
        var h6 = history.Of(R(6))!; var seen6 = history.AsSeenBy(R(6), 0, closure); var now6 = history.Winning(R(6));
        Check("bos/drift: a [Transforms] posA on an anchor is a step under the pseudo-plugin — the master's authoring view keeps x 100, the winner reads x 2000",
              h6.Count == 2 && h6[^1].Plugin == bosPlugin && seen6 is { } s6 && s6.Pos.X == 100f && now6 is { } n6 && n6.Pos.X == 2000f && n6.Plugin == bosPlugin,
              $"{h6.Count} snaps; seen x {seen6?.Pos.X} now x {now6?.Pos.X} by {now6?.Plugin}");
        var r5 = history.Winning(R(5))!.Value;
        Check("bos/drift: the pair stood 100 u apart and stands 2,000 u apart — CameApart at near 600, BOS the mover, and no real plugin can have patched it",
              DetectorModel.CameApart(true, 100f, true, PlacementHistory.Dist(r5.Pos, now6!.Value.Pos), 600f) && r5.Plugin == 0 && !closure[0].Contains(bosPlugin) && closure[bosPlugin].Contains(0),
              $"now {PlacementHistory.Dist(r5.Pos, now6!.Value.Pos):0} u apart");
        Check("bos/drift: a rotR-only line is no step (the history's standing rule) and is counted silent",
              history.Of(R(5))!.Count == 1 && hrep.SilentTransforms == 1 && swaps.ContainsKey(R(5)), $"{history.Of(R(5))!.Count} snaps, silent {hrep.SilentTransforms}");
        Check("bos/drift: the base map — R1's refBase is the wall and recordBase keeps the post; the tally reads 1 swapped, 4 overrides, 3 steps, 1 flag change",
              refBase[R(1)] == wall && recordBase[R(1)] == postA && hrep.Swapped == 1 && hrep.WithProperties == 4 && hrep.TransformSteps == 3 && hrep.FlagChanges == 1 && !hrep.Direct,
              $"swapped {hrep.Swapped} props {hrep.WithProperties} steps {hrep.TransformSteps} flags {hrep.FlagChanges} silent {hrep.SilentTransforms}");
        var w2 = history.Winning(R(2))!.Value;
        Check("bos/hooks: the direct path (Effective) and the history path (Apply) give one answer for one override — R2 at y 67 on both, by the pseudo-plugin",
              e2on.Position.Y == 67f && w2.Pos.Y == 67f && w2.Plugin == bosPlugin && BosHook.Describe(swaps[R(1)]).Contains("Hooks_SWAP.ini:2"),
              $"direct y {e2on.Position.Y}, history y {w2.Pos.Y} by {w2.Plugin}; {BosHook.Describe(swaps[R(1)])}");
    }
}
