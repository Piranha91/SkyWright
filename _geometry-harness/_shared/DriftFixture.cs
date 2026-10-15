using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace HarnessShared;

// THE ALIAS-SPLIT FIXTURE (driftprobe --self-test, probe-decisions slice 3): one quest whose aliases stand in pairs, and a
// mover that pulls some pairs apart. Test-only; written with Mutagen. Expectations are stated by each test, worked out by
// hand from this table.
//
// Load order: DfMaster.esm, DfQuest.esp (masters DfMaster), DfMover.esp (masters DfMaster), DfInject.esp (masters
// DfQuest); the fix order adds DfFix.esp (masters DfMaster, DfMover). Interior cells C1, C2. Bases: a static St; NPCs
// Nfree (no packages), Nbound (package Pk100: near its editor location, radius 100), Nwide (Pk600, radius 600).
//
//   alias  ref   placed in C1 (master)     DfMover            pair / stated by
//   1      R1    St (0,0)                  to C2 (0,0)        A = 1+2, scene SA; E = 1+10, nothing
//   2      R2    St (200,0)                -
//   3      R3    Nfree (0,1000)            to (2000,1000)     B = 3+4, scene SB
//   4      R4    St (200,1000)             -
//   5      R5    Nwide (0,2000)            to (1100,2000)     C = 5+6, scene SC
//   6      R6    Nwide (200,2000)          -
//   7      R7    Nbound (0,3000)           to (1100,3000)     D = 7+8, scene SD
//   8      R8    Nbound (200,3000)         -
//   9      R9    DfQuest:000900, placed only by DfInject in C1 (0,4000): no authoring view
//   10     R10   St (0,100)                -
//   11     -     no fill (conditions only): non-positional
//   12     -     UniqueActor Nbound, placed twice (R7, R8): several placements
// DfFix puts R1 and R7 back where the master had them. An objective of the quest targets alias 1.
public static class DriftFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey MasterKey = new("DfMaster", ModType.Master);
    public static readonly ModKey QuestKey = new("DfQuest", ModType.Plugin), MoverKey = new("DfMover", ModType.Plugin),
                                  InjectKey = new("DfInject", ModType.Plugin), FixKey = new("DfFix", ModType.Plugin);
    static FormKey Mk(uint id) => new(MasterKey, id);
    public static readonly FormKey St = Mk(0x801), Nfree = Mk(0x802), Nbound = Mk(0x803), Nwide = Mk(0x804), Race = Mk(0x805),
                                   Pk100 = Mk(0x806), Pk600 = Mk(0x807), C1 = Mk(0x810), C2 = Mk(0x811);
    public static readonly FormKey R1 = Mk(0x901), R2 = Mk(0x902), R3 = Mk(0x903), R4 = Mk(0x904), R5 = Mk(0x905), R6 = Mk(0x906),
                                   R7 = Mk(0x907), R8 = Mk(0x908), R10 = Mk(0x90A);
    public static readonly FormKey Q = new(QuestKey, 0x800), R9 = new(QuestKey, 0x900);
    public static readonly FormKey SA = new(QuestKey, 0x810), SB = new(QuestKey, 0x811), SC = new(QuestKey, 0x812), SD = new(QuestKey, 0x813);

    /// <summary>The order without the fix, and the fix plugin (load it last for the fixed order).</summary>
    public sealed record Written(IReadOnlyList<string> Order, string Fix);

    static PlacedObject Obj(FormKey key, float x, float y)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new(x, y, 0), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(St);
        return r;
    }
    static PlacedNpc Npc(FormKey key, FormKey npc, float x, float y)
    {
        var r = new PlacedNpc(key, Rel) { Placement = new Placement { Position = new(x, y, 0), Rotation = new(0, 0, 0) } };
        r.Base.SetTo(npc);
        return r;
    }
    static SkCell Cell(FormKey key, string edid) => new(key, Rel) { EditorID = edid, Flags = SkCell.Flag.IsInteriorCell };
    static Package Pack(FormKey key, string edid, uint radius)
    {
        var p = new Package(key, Rel) { EditorID = edid, ScheduleHour = -1 };
        p.Data[0] = new PackageDataLocation
        {
            Name = "Location",
            Location = new LocationTargetRadius { Radius = radius, Target = new LocationFallback { Type = LocationTargetRadius.LocationType.NearEditorLocation } },
        };
        return p;
    }
    static Npc NpcBase(FormKey key, string edid, params FormKey[] packages)
    {
        var n = new Npc(key, Rel) { EditorID = edid, Height = 1f, Race = new FormLinkNullable<IRaceGetter>(Race) };
        foreach (var pk in packages) n.Packages.Add(new FormLink<IPackageGetter>(pk));
        return n;
    }
    static QuestAlias Forced(int id, FormKey r) => new() { ID = (uint)id, Name = $"Alias{id}", ForcedReference = new FormLinkNullable<IPlacedGetter>(r) };
    static Scene SceneOf(FormKey key, params uint[] aliases)
    {
        var s = new Scene(key, Rel) { EditorID = $"DfScene{key.ID:X}", Quest = new FormLinkNullable<IQuestGetter>(Q) };
        foreach (var a in aliases) s.Actors.Add(new SceneActor { ID = a });
        return s;
    }

    /// <summary>Write the five plugins into <paramref name="dir"/> (created).</summary>
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
        m.Statics.Add(new Static(St, Rel) { EditorID = "DfStone" });
        m.Races.Add(new Race(Race, Rel) { EditorID = "DfRace", Flags = Mutagen.Bethesda.Skyrim.Race.Flag.Playable });
        m.Packages.Add(Pack(Pk100, "DfStay100", 100));
        m.Packages.Add(Pack(Pk600, "DfStay600", 600));
        m.Npcs.Add(NpcBase(Nfree, "DfFree"));
        m.Npcs.Add(NpcBase(Nbound, "DfBound", Pk100));
        m.Npcs.Add(NpcBase(Nwide, "DfWide", Pk600));
        var c1 = Cell(C1, "DfCellOne");
        c1.Temporary.Add(Obj(R1, 0, 0)); c1.Temporary.Add(Obj(R2, 200, 0));
        c1.Temporary.Add(Npc(R3, Nfree, 0, 1000)); c1.Temporary.Add(Obj(R4, 200, 1000));
        c1.Temporary.Add(Npc(R5, Nwide, 0, 2000)); c1.Temporary.Add(Npc(R6, Nwide, 200, 2000));
        c1.Temporary.Add(Npc(R7, Nbound, 0, 3000)); c1.Temporary.Add(Npc(R8, Nbound, 200, 3000));
        c1.Temporary.Add(Obj(R10, 0, 100));
        MovedRefFixture.FileInterior(m, c1);
        MovedRefFixture.FileInterior(m, Cell(C2, "DfCellTwo"));
        var mPath = Save(m);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, Rel);

        var qm = new SkyrimMod(QuestKey, Rel);
        var q = new Quest(Q, Rel) { EditorID = "DfQ", Name = "Find the stones" };
        foreach (var (id, r) in new[] { (1, R1), (2, R2), (3, R3), (4, R4), (5, R5), (6, R6), (7, R7), (8, R8), (9, R9), (10, R10) })
            q.Aliases.Add(Forced(id, r));
        q.Aliases.Add(new QuestAlias { ID = 11, Name = "Alias11" });
        q.Aliases.Add(new QuestAlias { ID = 12, Name = "Alias12", UniqueActor = new FormLinkNullable<INpcGetter>(Nbound) });
        var obj = new QuestObjective { Index = 10, DisplayText = "Find the first stone" };
        obj.Targets.Add(new QuestObjectiveTarget { AliasID = 1 });
        q.Objectives.Add(obj);
        qm.Quests.Add(q);
        qm.Scenes.Add(SceneOf(SA, 1, 2)); qm.Scenes.Add(SceneOf(SB, 3, 4)); qm.Scenes.Add(SceneOf(SC, 5, 6)); qm.Scenes.Add(SceneOf(SD, 7, 8));
        var qPath = Save(qm, mOv);
        using var qOv = SkyrimMod.CreateFromBinaryOverlay(qPath, Rel);

        var mv = new SkyrimMod(MoverKey, Rel);
        var mc1 = Cell(C1, "DfCellOne");
        mc1.Temporary.Add(Npc(R3, Nfree, 2000, 1000)); mc1.Temporary.Add(Npc(R5, Nwide, 1100, 2000)); mc1.Temporary.Add(Npc(R7, Nbound, 1100, 3000));
        var mc2 = Cell(C2, "DfCellTwo");
        mc2.Temporary.Add(Obj(R1, 0, 0));
        MovedRefFixture.FileInterior(mv, mc1); MovedRefFixture.FileInterior(mv, mc2);
        var mvPath = Save(mv, mOv);
        using var mvOv = SkyrimMod.CreateFromBinaryOverlay(mvPath, Rel);

        var inj = new SkyrimMod(InjectKey, Rel);
        var ic1 = Cell(C1, "DfCellOne");
        ic1.Temporary.Add(Obj(R9, 0, 4000));
        MovedRefFixture.FileInterior(inj, ic1);
        var injPath = Save(inj, mOv, qOv);

        var fx = new SkyrimMod(FixKey, Rel);
        var fc1 = Cell(C1, "DfCellOne");
        fc1.Temporary.Add(Obj(R1, 0, 0)); fc1.Temporary.Add(Npc(R7, Nbound, 0, 3000));
        MovedRefFixture.FileInterior(fx, fc1);
        var fxPath = Save(fx, mOv, mvOv);
        return new Written(new[] { mPath, qPath, mvPath, injPath }, fxPath);
    }
}
