using HousecarlMcpTests;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE TRIGGER-MAP FIXTURE (switchprobe --self-test, probe-decisions slice 3): a synthetic order whose switches are each
// bound, or not, to a script that does, or does not, flip them. Test-only; written with Mutagen, the scripts with the
// test project's PexWriter (linked). Expectations are stated by each test, worked out by hand from this table.
//
// Load order: SwMaster.esm, SwQuest.esp, SwRef.esp. One interior cell SC1. Markers M1-M5 (base Mk), each the XESP parent
// of one child (M1 of two: A1 same state, A2 opposite).
//
//   switch  bound by                                                  script              does
//   M1      quest SwQ, script SwQScript, property Gate1               SwQScript.pex        Fragment_0 (stage 10): Gate1.Enable()
//   M2      quest SwQ, script SwQScript, property Gate2               SwQScript.pex        nothing on Gate2
//   M3      ref Rs (SwRef.esp), script SwMissing, property Target     (no pex)             -
//   M4      ref Rt (SwRef.esp), script SwRefScript, property Gate     SwRefScript.pex      OnActivate: Gate.Disable()
//   M5      nothing                                                   -                    -
public static class SwitchFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("SwMaster", ModType.Master);
    public static readonly ModKey QuestKey = new("SwQuest", ModType.Plugin), RefKey = new("SwRef", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);
    public static readonly FormKey Marker = Mk(0x801), Thing = Mk(0x802), SC1 = Mk(0x810);
    public static readonly FormKey M1 = Mk(0x901), M2 = Mk(0x902), M3 = Mk(0x903), M4 = Mk(0x904), M5 = Mk(0x905);
    public static readonly FormKey A1 = Mk(0x911), A2 = Mk(0x912), B1 = Mk(0x921), C1 = Mk(0x931), D1 = Mk(0x941), E1 = Mk(0x951);
    public static readonly FormKey Q = new(QuestKey, 0x800), Rs = new(RefKey, 0x800), Rt = new(RefKey, 0x801);
    public const string QuestScript = "SwQScript", RefScript = "SwRefScript", MissingScript = "SwMissing";

    /// <summary>The plugins in load order, and a mods folder holding each script loose under its plugin's mod folder
    /// (mods\SwQuest\Scripts\SwQScript.pex, mods\SwRef\Scripts\SwRefScript.pex), as an MO2 instance would.</summary>
    public sealed record Written(IReadOnlyList<string> Order, string ModsDir);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, string? edid = null, FormKey? parent = null, bool opposite = false)
    {
        var r = new PlacedObject(key, Rel) { EditorID = edid, Placement = new Placement { Position = new(x, 0, 0), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        return r;
    }
    static ScriptEntry Script(string name, params (string Prop, FormKey Obj)[] props)
    {
        var e = new ScriptEntry { Name = name };
        foreach (var (p, o) in props) { var sp = new ScriptObjectProperty { Name = p, Alias = -1 }; sp.Object.SetTo(o); e.Properties.Add(sp); }
        return e;
    }
    static SkCell Cell() => new(SC1, Rel) { EditorID = "SwCell", Flags = SkCell.Flag.IsInteriorCell };

    /// <summary>Write the three plugins into <paramref name="dir"/> and the two scripts under dir\mods.</summary>
    public static Written Write(string dir)
    {
        Directory.CreateDirectory(dir);
        string Save(SkyrimMod mod, params ISkyrimModGetter[] masters)
        {
            var path = Path.Combine(dir, mod.ModKey.FileName.String);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
            return path;
        }

        var m = new SkyrimMod(MasterKey, Rel);
        m.Statics.Add(new Static(Marker, Rel) { EditorID = "SwMarker" });
        m.Statics.Add(new Static(Thing, Rel) { EditorID = "SwThing" });
        var c = Cell();
        foreach (var (k, x, e) in new[] { (M1, 0f, "SwGate1"), (M2, 100f, "SwGate2"), (M3, 200f, "SwGate3"), (M4, 300f, "SwGate4"), (M5, 400f, "SwGate5") })
            c.Temporary.Add(Ref(k, Marker, x, e));
        c.Temporary.Add(Ref(A1, Thing, 10, parent: M1)); c.Temporary.Add(Ref(A2, Thing, 20, parent: M1, opposite: true));
        c.Temporary.Add(Ref(B1, Thing, 110, parent: M2)); c.Temporary.Add(Ref(C1, Thing, 210, parent: M3));
        c.Temporary.Add(Ref(D1, Thing, 310, parent: M4)); c.Temporary.Add(Ref(E1, Thing, 410, parent: M5));
        MovedRefFixture.FileInterior(m, c);
        var mPath = Save(m);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        var qm = new SkyrimMod(QuestKey, Rel);
        var quest = new Quest(Q, Rel) { EditorID = "SwQ" };
        var qa = new QuestAdapter { FileName = QuestScript };
        qa.Scripts.Add(Script(QuestScript, ("Gate1", M1), ("Gate2", M2)));
        qa.Fragments.Add(new QuestScriptFragment { Stage = 10, StageIndex = 0, ScriptName = QuestScript, FragmentName = "Fragment_0" });
        quest.VirtualMachineAdapter = qa;
        qm.Quests.Add(quest);
        var qPath = Save(qm, mOv);

        var rm = new SkyrimMod(RefKey, Rel);
        var rc = Cell();
        var rs = Ref(Rs, Thing, 500, "SwScriptedS"); rs.VirtualMachineAdapter = new VirtualMachineAdapter(); rs.VirtualMachineAdapter.Scripts.Add(Script(MissingScript, ("Target", M3)));
        var rt = Ref(Rt, Thing, 600, "SwScriptedT"); rt.VirtualMachineAdapter = new VirtualMachineAdapter(); rt.VirtualMachineAdapter.Scripts.Add(Script(RefScript, ("Gate", M4)));
        rc.Temporary.Add(rs); rc.Temporary.Add(rt);
        MovedRefFixture.FileInterior(rm, rc);
        var rPath = Save(rm, mOv);

        var mods = Path.Combine(dir, "mods");
        string ScriptsOf(ModKey k) { var d = Path.Combine(mods, k.Name, "Scripts"); Directory.CreateDirectory(d); return d; }
        var none = new[] { ("None", "::NoneVar") };
        PexWriter.WritePex(Path.Combine(ScriptsOf(QuestKey), QuestScript + ".pex"), QuestScript, "Quest",
            new[] { PexWriter.AutoObj("Gate1", "ObjectReference"), PexWriter.AutoObj("Gate2", "ObjectReference") },
            new[] { PexWriter.Function("Fragment_0", none,
                (InstructionOpcode.CALLMETHOD, new[] { PexWriter.Id("Enable"), PexWriter.Id("::Gate1_var"), PexWriter.Id("::NoneVar"), PexWriter.Int(0) })) });
        PexWriter.WritePex(Path.Combine(ScriptsOf(RefKey), RefScript + ".pex"), RefScript, "ObjectReference",
            new[] { PexWriter.AutoObj("Gate", "ObjectReference") },
            new[] { PexWriter.Function("OnActivate", none,
                (InstructionOpcode.CALLMETHOD, new[] { PexWriter.Id("Disable"), PexWriter.Id("::Gate_var"), PexWriter.Id("::NoneVar"), PexWriter.Int(0) })) });
        return new Written(new[] { mPath, qPath, rPath }, mods);
    }

    // THE COPIED-RULES ADDITION (copied-rules task, 2026-10-15): one more plugin after the three above, written by its own
    // method so their bytes do not change. SwCr.esp (masters SwMaster.esm) places markers M6-M8 in SC1, each the XESP
    // parent of one child, and quest SwCrQ whose aliases Door, Gate7, Gate8 are forced to M6, M7, M8. Its script SwCrScript
    // has Fragment_0 at stage 10: Alias_Door.TryToEnable(), Alias_Gate7.EnableLinkChain(), Alias_Gate8.TryToDisableNoWait().
    public static readonly ModKey CrKey = new("SwCr", ModType.Plugin);
    public static readonly FormKey M6 = new(CrKey, 0x906), M7 = new(CrKey, 0x907), M8 = new(CrKey, 0x908),
        F6 = new(CrKey, 0x916), F7 = new(CrKey, 0x917), F8 = new(CrKey, 0x918), CrQ = new(CrKey, 0x800);
    public const string CrScript = "SwCrScript";

    /// <summary>Write SwCr.esp into <paramref name="dir"/> (which must already hold SwMaster.esm from <see cref="Write"/>)
    /// and its script under dir\mods\SwCr\Scripts. Returns the plugin's path.</summary>
    public static string WriteCopiedRules(string dir)
    {
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, MasterKey.FileName.String), Rel);
        var cm = new SkyrimMod(CrKey, Rel);
        var c = Cell();
        foreach (var (k, ch, x, e) in new[] { (M6, F6, 700f, "SwGate6"), (M7, F7, 800f, "SwGate7"), (M8, F8, 900f, "SwGate8") })
        { c.Temporary.Add(Ref(k, Marker, x, e)); c.Temporary.Add(Ref(ch, Thing, x + 10, parent: k)); }
        MovedRefFixture.FileInterior(cm, c);
        var quest = new Quest(CrQ, Rel) { EditorID = "SwCrQ" };
        foreach (var (id, name, fk) in new[] { (0u, "Door", M6), (1u, "Gate7", M7), (2u, "Gate8", M8) })
            quest.Aliases.Add(new QuestAlias { ID = id, Name = name, ForcedReference = new FormLinkNullable<IPlacedGetter>(fk) });
        var qa = new QuestAdapter { FileName = CrScript };
        qa.Scripts.Add(new ScriptEntry { Name = CrScript });
        qa.Fragments.Add(new QuestScriptFragment { Stage = 10, StageIndex = 0, ScriptName = CrScript, FragmentName = "Fragment_0" });
        quest.VirtualMachineAdapter = qa;
        cm.Quests.Add(quest);
        var path = Path.Combine(dir, cm.ModKey.FileName.String);
        cm.BeginWrite.ToPath(path).WithLoadOrder(mOv).Write();

        var scripts = Path.Combine(dir, "mods", CrKey.Name, "Scripts");
        Directory.CreateDirectory(scripts);
        var none = new[] { ("None", "::NoneVar") };
        static (InstructionOpcode, PexObjectVariableData[]) Call(string name, string obj) => (InstructionOpcode.CALLMETHOD, new[] { PexWriter.Id(name), PexWriter.Id(obj), PexWriter.Id("::NoneVar"), PexWriter.Int(0) });
        PexWriter.WritePex(Path.Combine(scripts, CrScript + ".pex"), CrScript, "Quest",
            new[] { PexWriter.AutoObj("Alias_Door", "ReferenceAlias"), PexWriter.AutoObj("Alias_Gate7", "ReferenceAlias"), PexWriter.AutoObj("Alias_Gate8", "ReferenceAlias") },
            new[] { PexWriter.Function("Fragment_0", none,
                Call("TryToEnable", "::Alias_Door_var"), Call("EnableLinkChain", "::Alias_Gate7_var"), Call("TryToDisableNoWait", "::Alias_Gate8_var")) });
        return path;
    }

    // THE RAW-DUMP ADDITION (F4, 2026-10-15): SwRaw.esp after the three above (masters SwMaster.esm), written by its own method
    // so no other byte changes. Markers M9, M10 in SC1, each the XESP parent of one child; quest SwRawQ whose aliases Door9 and
    // Gate10 are forced to M9 and M10. Its script SwRawScript has Fragment_0 at stage 10: Alias_Door9.TryToEnable(),
    // Alias_Gate10.EnableLinkChain(), then a backward JMP no flow pattern matches, so the decompiler refuses the function and
    // prints its raw bytecode; the calls are in that dump only.
    public static readonly ModKey RawKey = new("SwRaw", ModType.Plugin);
    public static readonly FormKey M9 = new(RawKey, 0x909), M10 = new(RawKey, 0x90A), F9 = new(RawKey, 0x919), F10 = new(RawKey, 0x91A),
        RawQ = new(RawKey, 0x800);
    public const string RawScript = "SwRawScript";

    /// <summary>Write SwRaw.esp into <paramref name="dir"/> (which must already hold SwMaster.esm from <see cref="Write"/>)
    /// and its script under dir\mods\SwRaw\Scripts. Returns the plugin's path.</summary>
    public static string WriteRawDump(string dir)
    {
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, MasterKey.FileName.String), Rel);
        var rm = new SkyrimMod(RawKey, Rel);
        var c = Cell();
        foreach (var (k, ch, x, e) in new[] { (M9, F9, 1000f, "SwGate9"), (M10, F10, 1100f, "SwGate10") })
        { c.Temporary.Add(Ref(k, Marker, x, e)); c.Temporary.Add(Ref(ch, Thing, x + 10, parent: k)); }
        MovedRefFixture.FileInterior(rm, c);
        var quest = new Quest(RawQ, Rel) { EditorID = "SwRawQ" };
        foreach (var (id, name, fk) in new[] { (0u, "Door9", M9), (1u, "Gate10", M10) })
            quest.Aliases.Add(new QuestAlias { ID = id, Name = name, ForcedReference = new FormLinkNullable<IPlacedGetter>(fk) });
        var qa = new QuestAdapter { FileName = RawScript };
        qa.Scripts.Add(new ScriptEntry { Name = RawScript });
        qa.Fragments.Add(new QuestScriptFragment { Stage = 10, StageIndex = 0, ScriptName = RawScript, FragmentName = "Fragment_0" });
        quest.VirtualMachineAdapter = qa;
        rm.Quests.Add(quest);
        var path = Path.Combine(dir, rm.ModKey.FileName.String);
        rm.BeginWrite.ToPath(path).WithLoadOrder(mOv).Write();

        var scripts = Path.Combine(dir, "mods", RawKey.Name, "Scripts");
        Directory.CreateDirectory(scripts);
        var none = new[] { ("None", "::NoneVar") };
        static (InstructionOpcode, PexObjectVariableData[]) Call(string name, string obj) => (InstructionOpcode.CALLMETHOD, new[] { PexWriter.Id(name), PexWriter.Id(obj), PexWriter.Id("::NoneVar"), PexWriter.Int(0) });
        PexWriter.WritePex(Path.Combine(scripts, RawScript + ".pex"), RawScript, "Quest",
            new[] { PexWriter.AutoObj("Alias_Door9", "ReferenceAlias"), PexWriter.AutoObj("Alias_Gate10", "ReferenceAlias") },
            new[] { PexWriter.Function("Fragment_0", none,
                Call("TryToEnable", "::Alias_Door9_var"), Call("EnableLinkChain", "::Alias_Gate10_var"),
                (InstructionOpcode.JMP, new[] { PexWriter.Int(-2) })) });
        return path;
    }
}
