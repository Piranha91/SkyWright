using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — the physical population (Q27, 2026-09-06; _shared/EnablePopulation.cs, _shared/EnableArms.cs).
//
// Every case below states its EXPECTED MEMBERSHIP as a literal, worked out from the records by hand before the resolver
// is asked: the point of the proof is that the two agree, and a proof that computed its expectation the way the code
// does would only prove the code equals itself. The order is written with Mutagen and read back through the same index
// the probes use, so the extraction is exercised too.
//
// The cases follow the handoff's list: a parent off and on; a child whose own flag contradicts its parent; an opposite
// child; a multi-hop chain; the player-parent idiom; a parent in another cell surviving floor ingestion; an
// initially-disabled child that its parent enables surviving the exterior grid filing; a missing parent and a cycle
// staying visible as unresolved; an override that changes the enable facts, with the winner arm and the arm without
// that plugin each resolving their own chain; a switch that moves a floor, an obstacle, a navcut box and the tallest
// actor together; a named arm's arrivals and departures against the census; and the CLI contract with its rejections
// and the emit's population metadata.
static class PopulationProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — the physical population (_shared/EnablePopulation.cs, _shared/EnableArms.cs, Q27)");
        Console.WriteLine("   A synthetic order written with Mutagen; every reference's presence at load known by hand.\n");

        // ---- the two names for the load-time state must be one name -----------------------------------------------
        Check("population/name: EnablePopulation.BaselineStateName is StateArm.BaselineName (the default run and --state baseline are the same state)",
              EnablePopulation.BaselineStateName == StateArm.BaselineName, $"{EnablePopulation.BaselineStateName} / {StateArm.BaselineName}");

        // ---- the CLI contract -------------------------------------------------------------------------------------
        Check("cli: no options -> the LOAD-TIME population (the default is the game's own state, not the record flag)",
              PopulationCli.Resolve("p", false, null) == PopulationMode.LoadTime, "");
        Check("cli: --flags -> the FLAGS population",
              PopulationCli.Resolve("p", true, null) == PopulationMode.Flags, "");
        Check("cli: --state baseline -> LOAD-TIME (a state arm is a load-time population with switches flipped)",
              PopulationCli.Resolve("p", false, "baseline") == PopulationMode.LoadTime, "");
        Check("cli: --flags WITH --state is refused, and the message says both what each is and which is the default",
              Refused(() => PopulationCli.Resolve("p", true, "baseline"), out var m1)
              && m1.Contains("--flags and --state") && m1.Contains("default"), m1);
        Check("cli: --state on a probe that builds no switch census is refused, naming the probes that do build one",
              Refused(() => PopulationCli.Resolve("navprobe", false, "SomeMarker", stateSupported: false), out var m2)
              && m2.Contains("builds no switch census") && m2.Contains("blockprobe"), m2);
        Check("cli: --flags on a probe that never had a flag population is refused, not quietly accepted",
              Refused(() => PopulationCli.Resolve("contactprobe", true, null, flagsSupported: false), out var m3)
              && m3.Contains("no flag-only population"), m3);

        // ---- the emit's identity ----------------------------------------------------------------------------------
        {
            var labelled = new ConflictFile("p", "i", "t", Array.Empty<Conflict>(), Population: "load-time", State: "baseline");
            var old = new ConflictFile("p", "i", "t", Array.Empty<Conflict>());
            Check("emit: a labelled file states its population and state; an emit written before Q27 reads UNKNOWN on both, never 'the same'",
                  labelled.PopulationIdentity == "load-time / state baseline" && old.Population is null && old.State is null
                  && old.PopulationIdentity == "unknown / state unknown", $"{labelled.PopulationIdentity} | {old.PopulationIdentity}");
        }

        var root = Path.Combine(Path.GetTempPath(), "proofprobe-pop-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { RunOrder(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static bool Refused(Action a, out string message)
    {
        try { a(); message = "(not refused)"; return false; }
        catch (ArgumentException ex) { message = ex.Message; return true; }
    }

    // ---- the synthetic order --------------------------------------------------------------------------------------

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

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, string? edid = null, bool disabled = false,
                            FormKey? parent = null, bool opposite = false, uint? layer = null, P3Float? prim = null)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        if (layer is { } l) r.CollisionLayer = l;
        if (prim is { } p) r.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = p };
        return r;
    }

    static PlacedNpc Actor(FormKey key, FormKey npcKey, float x, string? edid = null, bool disabled = false, FormKey? parent = null)
    {
        var r = new PlacedNpc(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(npcKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        return r;
    }

    static Cell Interior(FormKey key, string? edid = null) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
    static Static Stat(FormKey k, string edid, int x, int y, int z, bool marker = false) => new(k, SkyrimRelease.SkyrimSE)
    { EditorID = edid, Model = new Model { File = $"meshes\\pop\\{edid}.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) },
      MajorRecordFlagsRaw = marker ? (int)EnableState.IsMarkerFlag : 0 };

    static void RunOrder(Action<string, bool, string> Check, string root)
    {
        var release = SkyrimRelease.SkyrimSE;
        var mKey = new ModKey("PopMaster", ModType.Master);
        var pKey = new ModKey("PopPlug", ModType.Plugin);
        FormKey M(uint id) => new(mKey, id);
        FormKey P(uint id) => new(pKey, id);

        FormKey markerK = M(0x801), slabK = M(0x802), daisK = M(0x803), collK = M(0x804), npcK = M(0x805), raceK = M(0x806), layerK = M(0x807);
        FormKey cell1K = M(0x820), cell2K = M(0x821);
        FormKey S(int n) => M((uint)(0x900 + n));
        FormKey R(int n) => M((uint)(0xA00 + n));

        var master = new SkyrimMod(mKey, release);
        master.Statics.Add(Stat(markerK, "PopMarker", 2, 2, 4, marker: true));
        // ObjectBounds are FULL extents and ObndBox centres them on the placement, so these are the HALF heights the
        // floor reads: the slab's top is 10 u above its placement, the dais's 24 u above its.
        master.Statics.Add(Stat(slabK, "PopSlab", 700, 700, 20));      // 1400 x 1400 x 20 -> +/-700, +/-700, +/-10
        master.Statics.Add(Stat(daisK, "PopDais", 300, 300, 48));      // 600 x 600 x 48   -> +/-300, +/-300, +/-24
        master.Statics.Add(Stat(collK, "CollisionMarker", 2, 2, 4, marker: true));
        master.CollisionLayers.Add(new CollisionLayer(layerK, release) { EditorID = "L_NAVCUT", Name = "L_NAVCUT", Index = 49, Flags = CollisionLayer.Flag.NavmeshObstacle, Description = "cuts navmesh" });
        var popRace = new Race(raceK, release) { EditorID = "PopRace", Flags = Race.Flag.Playable };
        popRace.Height.Male = 1.0f; popRace.Height.Female = 1.0f;
        master.Races.Add(popRace);
        master.Npcs.Add(new Npc(npcK, release) { EditorID = "PopNpc", Height = 1.0f, Race = new FormLinkNullable<IRaceGetter>(raceK) });

        var c1 = Interior(cell1K, "PopCell1");
        var c2 = Interior(cell2K, "PopCell2");

        // ---- the two markers everything hangs off -------------------------------------------------------------------
        c1.Temporary.Add(Ref(S(1), markerK, 0, 0, 0, "MOff", disabled: true));    // OFF at load
        c1.Temporary.Add(Ref(S(2), markerK, 100, 0, 0, "MOn"));                   // ON at load

        // ---- parent off / on, and a child whose own flag contradicts its parent --------------------------------------
        c1.Temporary.Add(Ref(R(1), slabK, 0, 100, 0, "COff", parent: S(1)));                       // parent off -> ABSENT
        c1.Temporary.Add(Ref(R(2), slabK, 0, 200, 0, "COn", parent: S(2)));                        // parent on  -> PRESENT
        c1.Temporary.Add(Ref(R(3), slabK, 0, 300, 0, "CFlagSetOnParent", disabled: true, parent: S(2)));   // own flag SET, parent ON -> PRESENT
        c1.Temporary.Add(Ref(R(4), slabK, 0, 400, 0, "CFlagClearOffParent", parent: S(1)));        // own flag clear, parent OFF -> ABSENT

        // ---- opposite children ---------------------------------------------------------------------------------------
        c1.Temporary.Add(Ref(R(5), slabK, 0, 500, 0, "COppOfOff", parent: S(1), opposite: true));   // opposite of OFF -> PRESENT
        c1.Temporary.Add(Ref(R(6), slabK, 0, 600, 0, "COppOfOn", parent: S(2), opposite: true));    // opposite of ON  -> ABSENT

        // ---- a multi-hop chain ---------------------------------------------------------------------------------------
        c1.Temporary.Add(Ref(S(3), markerK, 200, 0, 0, "MHop", parent: S(1), opposite: true));      // opposite of OFF -> PRESENT
        c1.Temporary.Add(Ref(R(7), slabK, 200, 100, 0, "CHop2", parent: S(3)));                     // -> PRESENT  (two hops)
        c1.Temporary.Add(Ref(S(4), markerK, 300, 0, 0, "MHop3", parent: S(3), opposite: true));     // opposite of PRESENT -> ABSENT
        c1.Temporary.Add(Ref(R(8), slabK, 300, 100, 0, "CHop3", parent: S(4)));                     // -> ABSENT   (three hops)

        // ---- the player-parent idiom ---------------------------------------------------------------------------------
        c1.Temporary.Add(Ref(R(9), slabK, 400, 0, 0, "CPlayerOpp", parent: new FormKey(mKey, EnableState.PlayerRefId), opposite: true));  // ABSENT
        c1.Temporary.Add(Ref(R(10), slabK, 400, 100, 0, "CPlayer", disabled: true, parent: new FormKey(mKey, EnableState.PlayerRefId)));  // PRESENT

        // ---- a parent in ANOTHER CELL, and the child a floor read of cell 1 must judge --------------------------------
        c2.Temporary.Add(Ref(S(5), markerK, 5000, 0, 0, "MCrossCell", disabled: true));
        // Well clear of every other case's slab (they are 1,400 u wide), so the reading at this point has exactly two
        // candidates: the dais (top +24) and the slab under it (top −58).
        c1.Temporary.Add(Ref(R(11), daisK, 20000, 0, 0, "CrossDais", parent: S(5)));                // ABSENT, and its parent is not in this cell
        c1.Temporary.Add(Ref(R(12), slabK, 20000, 0, -68, "CrossSlab"));                            // the real floor under it (top at −58)

        // ---- unresolved chains: a parent nothing places, and a cycle --------------------------------------------------
        c1.Temporary.Add(Ref(R(13), slabK, 500, 0, 0, "CMissingParent", parent: M(0xFFFF)));        // enabled by fallback, UNRESOLVED
        c1.Temporary.Add(Ref(S(6), markerK, 600, 0, 0, "CycA", parent: S(7)));
        c1.Temporary.Add(Ref(S(7), markerK, 700, 0, 0, "CycB", parent: S(6)));

        // ---- a switch that moves a floor, an obstacle, a navcut box and the tallest actor together --------------------
        c1.Temporary.Add(Ref(S(8), markerK, 2000, 0, 0, "MSet", disabled: true));                   // OFF at load
        c1.Temporary.Add(Ref(R(14), daisK, 2000, 0, 0, "SetFloor", parent: S(8)));                  // a 48 u platform, ABSENT at load
        c1.Temporary.Add(Ref(R(15), slabK, 2000, 0, -68, "SetGround"));                             // the floor under it
        c1.Temporary.Add(Ref(R(16), daisK, 2000, 500, 0, "SetObstacle", parent: S(8)));             // ABSENT at load
        c1.Temporary.Add(Ref(R(17), collK, 2000, 900, 25, "SetNavcut", parent: S(8), layer: 49, prim: new P3Float(100, 100, 50)));
        c1.Temporary.Add(Actor(R(18), npcK, 2400, "SetActor", parent: S(8)));                       // ABSENT at load
        c1.Temporary.Add(Actor(R(19), npcK, 2500, "AlwaysActor"));                                  // PRESENT at load

        // ---- an override that CHANGES the enable facts (the winner arm and the without-P arm differ) ------------------
        c1.Temporary.Add(Ref(R(20), slabK, 3000, 0, 0, "OverriddenChild", parent: S(1)));           // in the master: parent OFF -> ABSENT

        FileInterior(master, c1);
        FileInterior(master, c2);
        var mPath = Path.Combine(root, mKey.FileName.String);
        master.BeginWrite.ToPath(mPath).WithNoLoadOrder().Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

        // PLUGIN P: drops OverriddenChild's enable parent, so it is PRESENT under the winner and ABSENT in the arm
        // without P — the two arms must not share an answer.
        var plug = new SkyrimMod(pKey, release);
        var p1 = Interior(cell1K);
        p1.Temporary.Add(Ref(R(20), slabK, 3000, 0, 0, "OverriddenChild"));
        FileInterior(plug, p1);
        var pPath = Path.Combine(root, pKey.FileName.String);
        plug.BeginWrite.ToPath(pPath).WithLoadOrder(mOv).Write();

        // ---- read it back the way a probe does ------------------------------------------------------------------------
        var paths = new[] { mPath, pPath };
        var facts = new EnableFactsIndex();
        var idx = new OverlapIndex();
        var npcs = new Dictionary<FormKey, ActorBand.NpcFacts>();
        var races = new Dictionary<FormKey, ActorBand.RaceFacts>();
        var cellRefs = new Dictionary<FormKey, List<PlacedRef>>();
        var collByIndex = new Dictionary<uint, bool>();
        for (int p = 0; p < paths.Length; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
            idx.Ingest(p, mod);
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                if (rec is IPlacedGetter pg) { if (pg.IsDeleted) facts.NoteDeleted(pg.FormKey); continue; }
                if (rec is ICollisionLayerGetter coll) { collByIndex[coll.Index] = coll.Flags.HasFlag(CollisionLayer.Flag.NavmeshObstacle); continue; }
                ActorBand.CollectRecord(rec, npcs, races);
            }
            foreach (var cp in PlacementExtract.FromMod(mod))
            {
                foreach (var r in cp.Refs) facts.Note(r);
                if (!cellRefs.TryGetValue(cp.Cell, out var l)) cellRefs[cp.Cell] = l = new List<PlacedRef>();
                l.RemoveAll(x => cp.Refs.Any(y => y.FormKey == x.FormKey));
                l.AddRange(cp.Refs);
            }
        }

        var pop = EnablePopulation.LoadTime(facts);
        var flagPop = EnablePopulation.Flags(facts);

        // ---- membership, stated by hand -------------------------------------------------------------------------------
        var expected = new (int Ref, string Name, bool Present)[]
        {
            (1,  "COff",                 false),
            (2,  "COn",                  true),
            (3,  "CFlagSetOnParent",     true),
            (4,  "CFlagClearOffParent",  false),
            (5,  "COppOfOff",            true),
            (6,  "COppOfOn",             false),
            (7,  "CHop2",                true),
            (8,  "CHop3",                false),
            (9,  "CPlayerOpp",           false),
            (10, "CPlayer",              true),
            (11, "CrossDais",            false),
            (12, "CrossSlab",            true),
            (14, "SetFloor",             false),
            (16, "SetObstacle",          false),
            (17, "SetNavcut",            false),
            (18, "SetActor",             false),
            (19, "AlwaysActor",          true),
            (20, "OverriddenChild",      true),   // P dropped the parent
        };
        var wrong = expected.Where(e => pop.Enabled(R(e.Ref)) != e.Present).Select(e => e.Name).ToList();
        Check($"population/chain: all {expected.Length} references resolve to the membership the records were built to have "
            + "(parent off and on, a child's own flag against its parent, opposite children, two- and three-hop chains, the player idiom, a cross-cell parent, an override that drops the parent)",
              wrong.Count == 0, wrong.Count == 0 ? "" : "wrong: " + string.Join(", ", wrong));

        // The flag population is the OTHER answer, and it is wrong about the game wherever a parent decides — this is
        // what --flags reproduces, and the case exists so the two are never confused.
        Check("population/flags: by the record flag alone the four parent-decided cases read the opposite way (COff and CFlagClearOffParent present, CFlagSetOnParent absent, COppOfOff present only by accident)",
              flagPop.Enabled(R(1)) && !flagPop.Enabled(R(3)) && flagPop.Enabled(R(4)) && flagPop.Enabled(R(5)),
              $"{flagPop.Enabled(R(1))} {flagPop.Enabled(R(3))} {flagPop.Enabled(R(4))} {flagPop.Enabled(R(5))}");

        // ---- unresolved chains stay visible ---------------------------------------------------------------------------
        var missing = pop.Resolution(R(13));
        var cyc = pop.Resolution(S(6));
        Check("population/unresolved: a parent nothing places resolves ENABLED by the stated fallback and is marked unresolved — the answer is a fallback, not a measurement",
              missing.Enabled && !missing.Resolved, $"{missing}");
        Check("population/unresolved: a cycle falls back to the reference's own flag and is marked unresolved",
              !cyc.Resolved, $"{cyc}");
        Check("population/unresolved: the count reaches the run's summary and the emit's coverage block, so no clean verdict can rest on an unread chain",
              pop.Unresolved >= 2 && pop.Unjudgeable.TryGetValue("EnableChainUnresolved", out var un) && un == pop.Unresolved
              && pop.Describe().Contains("could not be read to the end"), $"unresolved {pop.Unresolved}");
        Check("population/unresolved: the facts index counts the parents nothing places apart (one here: the missing parent; a cycle's parents ARE placed)",
              facts.UnplacedParents == 1, $"{facts.UnplacedParents}");

        // ---- Q33: the Base Object Swapper own-flag overlay, over the SAME facts -----------------------------------------
        // Measured in game 2026-09-06 (SkyrimSE 1.7.104 + BOS 3.5.0): a flags(0x800)/flagsC(0x800) override replaces the
        // reference's OWN initially-disabled flag and nothing else. Routing it through the facts is what makes the engine's
        // precedence come out of ONE rule — so these four cells are the same four the game showed, at the layer every
        // geometry probe shares. Applied to a second facts index so the checks above keep reading the untouched one.
        {
            FormKey Q(uint id) => M(0xC00 + id);
            PlacedRef Q_(uint id, bool disabled, FormKey? parent = null, bool opposite = false) =>
                new(Q(id), "PlacedObject", $"Q33_{id}", new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f,
                    Q(0), Persistent: false, InitiallyDisabled: disabled, Deleted: false,
                    EnableParent: parent, EnableParentOpposite: opposite);

            var bosFacts = new EnableFactsIndex();
            var arms = new[]
            {
                Q_(10, disabled: false),                                   // set control:   no parent, present
                Q_(11, disabled: true),                                    // clear control: no parent, absent
                Q_(1,  disabled: false),                                   // an ENABLED parent
                Q_(2,  disabled: true),                                    // a DISABLED parent
                Q_(12, disabled: false, parent: Q(1)),                     // A: enabled parent, ordinary parity
                Q_(13, disabled: true,  parent: Q(2)),                     // B: disabled parent, ordinary parity
                Q_(14, disabled: false, parent: Q(2), opposite: true),     // A-opposite: present through a disabled parent
                Q_(15, disabled: true,  parent: Q(1), opposite: true),     // B-opposite: absent through an enabled parent
                Q_(16, disabled: false, parent: Q(11)),                    // a child of the clear control, for the state-arm order below
            };
            foreach (var r in arms) bosFacts.Note(r);
            var before = EnablePopulation.LoadTime(bosFacts);
            var was = arms.ToDictionary(r => r.FormKey, r => before.Enabled(r.FormKey));
            Check("population/bos: the baseline is the record's own answer — the two controls, both parities, as the records say",
                  was[Q(10)] && !was[Q(11)] && was[Q(12)] && !was[Q(13)] && was[Q(14)] && !was[Q(15)],
                  string.Join(" ", arms.Select(r => $"{r.EditorId}={was[r.FormKey]}")));

            // The ini's six lines, as the game read them: set on 10/12/14, clear on 11/13/15.
            var overrides = new Dictionary<FormKey, bool>
            {
                [Q(10)] = true, [Q(12)] = true, [Q(14)] = true,
                [Q(11)] = false, [Q(13)] = false, [Q(15)] = false,
            };
            int applied = bosFacts.ApplyBosOwnFlags(overrides);
            var after = EnablePopulation.LoadTime(bosFacts);
            Check("population/bos: the overlay flips exactly the six own flags it names, is idempotent, and invents nothing for a reference nothing places",
                  applied == 6 && bosFacts.ApplyBosOwnFlags(overrides) == 0
               && bosFacts.ApplyBosOwnFlags(new Dictionary<FormKey, bool> { [M(0xCFF)] = true }) == 0,
                  $"{applied} applied");
            Check("population/bos: with NO parent the override decides, in BOTH directions — the clear brings a record-disabled reference back, which a filter applied after the chain cannot do",
                  !after.Enabled(Q(10)) && after.Enabled(Q(11)),
                  $"set control {after.Enabled(Q(10))}, clear control {after.Enabled(Q(11))}");

            // The overlay laid over a caller's FACTS FUNCTION (blockprobe's --state census, which no BosHook.Apply has
            // touched), and the ORDER it composes in. BOTH arms are pinned, because only the pair shows the order
            // matters: Q33_11 is record-disabled with a BOS CLEAR, so its own flag differs between the two
            // compositions and a state arm that flips it lands on opposite answers. Under the arm is the engine's
            // reading — BOS writes the LOAD-TIME own flag, and an arm flips FROM the load-time state.
            var rawIdx = new EnableFactsIndex();
            foreach (var r in arms) rawIdx.Note(r);
            var flipArm = new StateArm("q33flip", new[] { Q(11) });
            var underFacts = EnableFactsIndex.WithBosOwnFlags(rawIdx.Facts, overrides);
            var underMemo = new Dictionary<FormKey, EnableResolution>();
            var under = EnablePopulation.Of(flipArm.Wrap(underFacts, k => EnableState.Resolve(k, underFacts, underMemo)), flipArm.Name);
            var overMemo = new Dictionary<FormKey, EnableResolution>();
            var over = EnablePopulation.Of(
                EnableFactsIndex.WithBosOwnFlags(flipArm.Wrap(rawIdx.Facts, k => EnableState.Resolve(k, rawIdx.Facts, overMemo)), overrides),
                flipArm.Name);
            Check("population/bos: over a caller's FACTS the overlay composes UNDER a state arm — BOS clears the switch's own flag, so flipping it turns the switch OFF and its child with it",
                  !under.Enabled(Q(11)) && !under.Enabled(Q(16)),
                  $"switch {under.Enabled(Q(11))}, child {under.Enabled(Q(16))}");
            Check("population/bos: composed OVER the arm instead, the overlay would overwrite the flip and leave the switch ON — the wrong answer that order exists to stop",
                  over.Enabled(Q(11)) && over.Enabled(Q(16)),
                  $"switch {over.Enabled(Q(11))}, child {over.Enabled(Q(16))}");
            var noop = EnableFactsIndex.WithBosOwnFlags(rawIdx.Facts, new Dictionary<FormKey, bool>());
            Check("population/bos: a BOS-off run composes to a no-op — an empty override map leaves every one of the caller's own facts as it was",
                  arms.All(r => noop(r.FormKey)?.InitiallyDisabled == rawIdx.Facts(r.FormKey)?.InitiallyDisabled)
               && noop(M(0xCFF)) is null,
                  "empty map changes nothing");
            Check("population/bos: under an enable parent the override changes nothing, at BOTH parities and in BOTH directions — measured in game, Q33 (2026-09-06)",
                  after.Enabled(Q(12)) && !after.Enabled(Q(13)) && after.Enabled(Q(14)) && !after.Enabled(Q(15))
               && new[] { 12u, 13u, 14u, 15u }.All(n => after.Resolution(Q(n)).ViaParent && after.Resolution(Q(n)).Resolved),
                  string.Join(" ", new[] { 12u, 13u, 14u, 15u }.Select(n => $"Q33_{n}={after.Enabled(Q(n))}")));
        }

        // ---- the floor, over a cell whose parent lives in another cell -------------------------------------------------
        (Aabb Box, TriangleSoup Soup)? Geo(PlacedRef r)
        {
            var b = idx.ObndBox(r.Base ?? FormKey.Null);
            return b.IsEmpty ? null : (b, ContactIndex.BoxSoup(b));
        }
        var cell1 = cellRefs[cell1K];
        var flagField = FloorFeed.Build(cell1, Geo, null, 0f);
        var chainField = FloorFeed.Build(cell1, Geo, null, 0f, enabled: pop.Predicate);
        var fFlag = flagField.Read(20000f, 0f, 0f);
        var fChain = chainField.Read(20000f, 0f, 0f);
        Check("floor/cross-cell: by the flag alone the dais whose marker sits in ANOTHER CELL is the floor — its top 24 u above the navmesh, δ −24",
              fFlag.Found && MathF.Abs(fFlag.Offset + 24f) < 1e-3f, $"{fFlag}");
        Check("floor/cross-cell: through the chain it is gone and the slab under it is the floor (δ +58) — the parent was resolved from outside the cell being read",
              fChain.Found && MathF.Abs(fChain.Offset - 58f) < 1e-3f, $"{fChain}");

        // ---- the exterior grid filing must not prune on the flag -------------------------------------------------------
        {
            // A ref whose OWN flag is set but whose parent enables it: filed by grid, it must survive ingestion so the
            // chain can put it back. Before Q27 FileByGrid dropped it during the walk and no resolver ever saw it.
            var ext = new CellPlacements(M(0x830), "PopExt", false, M(0x840), (0, 0),
                new List<PlacedRef>
                {
                    new(R(3), "PlacedObject", "CFlagSetOnParent", new Vec3(10, 10, 0), new Vec3(0, 0, 0), 1f, slabK, false,
                        InitiallyDisabled: true, Deleted: false, EnableParent: S(2)),
                    new(R(1), "PlacedObject", "COff", new Vec3(20, 20, 0), new Vec3(0, 0, 0), 1f, slabK, false,
                        InitiallyDisabled: false, Deleted: false, EnableParent: S(1)),
                }, new List<NavmeshGeometry>(), null);
            var byGrid = new Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>();
            var landByGrid = new Dictionary<(int, int), float[,]>();
            FloorFeed.FileByGrid(ext, byGrid, landByGrid);
            var (nb, _) = FloorFeed.Neighbourhood((0, 0), byGrid, landByGrid);
            Check("floor/ingest: an initially-disabled reference whose PARENT enables it survives the exterior grid filing — the enable decision belongs to the population, not to ingestion",
                  nb.Any(r => r.FormKey == R(3)) && nb.Any(r => r.FormKey == R(1)), $"{nb.Count} refs filed");
            var extChain = FloorFeed.Build(nb, Geo, null, 0f, enabled: pop.Predicate);
            var extFlag = FloorFeed.Build(nb, Geo, null, 0f);
            Check("floor/ingest: and the two populations then disagree about it exactly as they should — the chain keeps it, the flag drops it",
                  extChain.Candidates == 1 && extFlag.Candidates == 1 && extChain.SkippedDisabled == 1 && extFlag.SkippedDisabled == 1,
                  $"chain {extChain.Candidates}/{extChain.SkippedDisabled} flag {extFlag.Candidates}/{extFlag.SkippedDisabled}");
        }

        // ---- one population: floor, obstacle, navcut and the tallest actor move together --------------------------------
        {
            var setField = FloorFeed.Build(cell1, Geo, null, 0f, enabled: pop.Predicate);
            var setRead = setField.Read(2000f, 0f, 0f);
            bool navcutIsNavcut = idx.RefCollisionLayer.TryGetValue(R(17), out var cl) && collByIndex.TryGetValue(cl, out var no) && no;
            var band = ActorBand.ForCell(cell1, npcs, races, 138f, "fallback", pop.Predicate);
            var flagBand = ActorBand.ForCell(cell1, npcs, races, 138f, "fallback");
            Check("one population/at load: the switch is OFF, so its platform is not the floor (the ground slab is, δ +58), its obstacle and its navcut box are not there, and the actor it brings is not counted",
                  setRead.Found && MathF.Abs(setRead.Offset - 58f) < 1e-3f
               && !pop.Enabled(R(16)) && navcutIsNavcut && !pop.Enabled(R(17))
               && band.Actors == 1, $"floor {setRead.Offset:0.#}, obstacle {pop.Enabled(R(16))}, navcut {pop.Enabled(R(17))}, actors {band.Actors}");
            Check("one population/the band is on the same population as everything else — by the flag alone the switch's actor is counted, which is what it did before Q27 even inside a state arm",
                  flagBand.Actors == 2 && band.Actors == 1, $"chain {band.Actors} flag {flagBand.Actors}");

            // Flip the switch: every one of the four must move, and together.
            var sx = SwitchIndex.Build(idx);
            sx.NameSwitches(idx.RefEditorId);
            var arm = StateArm.Parse("MSet", sx);
            var arms = new EnableArms(idx) { State = arm };
            var statePop = EnablePopulation.Of(arms.WinnerStateFacts, arm.Name);
            var stateField = FloorFeed.Build(cell1, Geo, null, 0f, enabled: statePop.Predicate);
            var stateRead = stateField.Read(2000f, 0f, 0f);
            var stateBand = ActorBand.ForCell(cell1, npcs, races, 138f, "fallback", statePop.Predicate);
            Check("one population/flipped: the platform becomes the floor (24 u above the navmesh, δ −24), the obstacle and the navcut box arrive, and the actor is counted — all four from the one flip",
                  stateRead.Found && MathF.Abs(stateRead.Offset + 24f) < 1e-3f
               && statePop.Enabled(R(16)) && statePop.Enabled(R(17)) && stateBand.Actors == 2,
                  $"floor {stateRead.Offset:0.#}, obstacle {statePop.Enabled(R(16))}, navcut {statePop.Enabled(R(17))}, actors {stateBand.Actors}");

            // The census keeps the dormant switch and its members whatever the state — a census is not a physical population.
            Check("census: the dormant switch is in the census with its whole set, and its baseline reads disabled — a census keeps what the physical populations drop",
                  sx.Switches.TryGetValue(S(8), out var info) && !info.Baseline.Enabled && info.Descendants.Count == 4,
                  $"{(sx.Switches.ContainsKey(S(8)) ? sx.Switches[S(8)].Descendants.Count : -1)} members");
            var changes = arm.Changes(sx).ToList();
            Check("arm: the flip's arrivals are discoverable and its departures are not invented — MSet is off at load, so its four members and the marker arrive and nothing departs",
                  changes.Count(c => c.After) == 5 && changes.Count(c => !c.After) == 0, $"{changes.Count(c => c.After)} arrive / {changes.Count(c => !c.After)} depart");
            Check("arm: the default population and an explicit --state baseline are the same population, reference for reference",
                  expected.All(e => pop.Enabled(R(e.Ref)) == EnablePopulation.Of(new EnableArms(idx).WinnerStateFacts, StateArm.BaselineName).Enabled(R(e.Ref))), "");
            Check("arm: 'flags' is NOT a reserved switch name — --state flags looks for a switch called flags, finds none, and is refused",
                  Refused(() => StateArm.Parse("flags", sx), out var mf) && mf.Contains("not a switch in this order"), mf);
        }

        // ---- the arms: the winner and the arm without a plugin each resolve their own chain -------------------------------
        {
            var closure = DetectorModel.MasterClosure(new IReadOnlySet<int>[]
            {
                new HashSet<int>(),          // the master has no masters
                new HashSet<int> { 0 },      // P masters the master
            });
            var armsA = new EnableArms(idx);
            var armsB = new EnableArms(idx);
            // Deliberately ask the WINNER first on one instance and the counterfactual first on the other: if a winning
            // answer could leak into the without-P memo, the two would disagree.
            bool winnerFirst = armsA.Winner(R(20)).Enabled;
            bool withoutAfter = armsA.Without(R(20), 1, closure).Enabled;
            bool withoutFirst = armsB.Without(R(20), 1, closure).Enabled;
            bool winnerAfter = armsB.Winner(R(20)).Enabled;
            Check("arms: OverriddenChild is PRESENT under the winner (P dropped its enable parent) and ABSENT in the arm without P (the master's parent is off) — the counterfactual resolves its own chain, not the winner's",
                  winnerFirst && !withoutAfter && !withoutFirst && winnerAfter,
                  $"winner {winnerFirst}/{winnerAfter}, without P {withoutAfter}/{withoutFirst}");
            Check("arms: and the answers do not depend on which arm was asked first — the memos are per arm",
                  winnerFirst == winnerAfter && withoutAfter == withoutFirst, "");
        }
    }
}
