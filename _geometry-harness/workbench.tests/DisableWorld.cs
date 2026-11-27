using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyWright.Engine;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace WorkbenchTests;

/// <summary>A synthetic MO2 instance for "disable this object": one master holding one reference of each kind a disable is
/// refused for (and the plain ones it is allowed on), and a later plugin that overrides a persistent reference, so the
/// winner a disable copies is not the master's record. Built with Mutagen in a temp folder, deleted after.</summary>
public sealed class DisableWorld : IDisposable
{
    internal static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    internal static readonly ModKey Master = new("HcWbDisable", ModType.Master);
    internal static readonly ModKey Later = new("HcWbDisableLater", ModType.Plugin);
    static FormKey K(uint id) => new(Master, id);

    // bases and other records
    internal static readonly FormKey Stat = K(0x801), DoorBase = K(0x804), NpcBase = K(0x805), NavcutLayer = K(0x806), RaceKey = K(0x807);
    internal static readonly FormKey Cell = K(0x802), FarCell = K(0x803);
    internal static readonly FormKey Quest = K(0x830), PackLocation = K(0x831), PackTarget = K(0x832);
    internal static readonly FormKey World = K(0x850), ExtCell = K(0x851);

    // the references
    internal static readonly FormKey Plain = K(0x810);            // nothing names it: a disable is allowed
    internal static readonly FormKey Persistent = K(0x811);       // persistent (0x400), overridden by the later plugin (its winner)
    internal static readonly FormKey AlreadyDisabled = K(0x812);  // initially disabled already
    internal static readonly FormKey Deleted = K(0x813);          // deleted
    internal static readonly FormKey Child = K(0x814);            // switched by an enable parent
    internal static readonly FormKey Parent = K(0x815);           // the enable parent of Child
    internal static readonly FormKey LinkTarget = K(0x816);       // named by Linker's linked reference
    internal static readonly FormKey Linker = K(0x817);
    internal static readonly FormKey ActTarget = K(0x818);        // named by ActChild's activate parent
    internal static readonly FormKey ActChild = K(0x819);
    internal static readonly FormKey Door = K(0x81A);             // a door that leads to FarDoor
    internal static readonly FormKey FarDoor = K(0x81B);
    internal static readonly FormKey AliasTarget = K(0x81C);      // a quest alias's forced reference
    internal static readonly FormKey PackLocTarget = K(0x81D);    // a package's location
    internal static readonly FormKey PackTargetRef = K(0x81E);    // a package's target
    internal static readonly FormKey Actor = K(0x81F);            // a placed actor, not a placed object
    internal static readonly FormKey Navcut = K(0x820);           // cuts the navmesh (XTRI on a NavmeshObstacle layer): allowed, said
    internal static readonly FormKey MoveTarget = K(0x821);       // a plain reference for the move-and-disable commit
    internal static readonly FormKey LargeRef = K(0x822);         // on the master copy of the worldspace's large-reference list only (the winning copy lists nothing)
    internal static readonly FormKey PlainExt = K(0x823);         // an exterior reference nothing names
    internal static readonly FormKey ScriptTarget = K(0x824);     // named by a script property (VMAD) of ScriptHolder
    internal static readonly FormKey ScriptHolder = K(0x825);
    internal static readonly FormKey PersistLinker = K(0x826);    // a persistent reference whose linked reference names PersistLinkTarget
    internal static readonly FormKey PersistLinkTarget = K(0x827);
    internal static readonly FormKey OneWayDoor = K(0x829);       // a door that leads to FarDoor, which does not lead back: nothing names it
    internal static readonly FormKey QuestScriptTarget = K(0x828);// named by a script property of the quest

    internal static readonly P3Float PersistentAtMaster = new(100, 0, 0), PersistentAtLater = new(130, -20, 5);
    internal static readonly P3Float PersistentRot = new(0f, -0f, 3.1415927f);   // a negative zero, as Gate A's winner had

    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string DataDir => Path.Combine(Root, "game", "Data");
    public string Mods => Path.Combine(Instance, "mods");
    internal string MasterPath => Path.Combine(Mods, "DisMaster", Master.FileName.String);
    internal string LaterPath => Path.Combine(Mods, "DisLater", Later.FileName.String);

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0)
    {
        var r = new PlacedObject(key, Rel) { EditorID = $"HcWbDis{key.ID:X}", Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }

    public DisableWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wb-disable-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(MasterPath)!); Directory.CreateDirectory(Path.GetDirectoryName(LaterPath)!);

        var m = new SkyrimMod(Master, Rel);
        m.Statics.Add(new Static(Stat, Rel) { EditorID = "HcWbDisStatic" });
        m.Doors.Add(new Door(DoorBase, Rel) { EditorID = "HcWbDisDoor" });
        m.Races.Add(new Race(RaceKey, Rel) { EditorID = "HcWbDisRace" });
        m.Npcs.Add(new Npc(NpcBase, Rel) { EditorID = "HcWbDisNpc", Height = 1f, Race = new FormLinkNullable<IRaceGetter>(RaceKey) });
        m.CollisionLayers.Add(new CollisionLayer(NavcutLayer, Rel) { EditorID = "L_NAVCUT", Name = "L_NAVCUT", Index = 49, Flags = CollisionLayer.Flag.NavmeshObstacle, Description = "cuts navmesh" });

        var cell = new SkCell(Cell, Rel) { EditorID = "HcWbDisCell", Flags = SkCell.Flag.IsInteriorCell };
        cell.Temporary.Add(Ref(Plain, Stat, 0));
        var persistent = Ref(Persistent, Stat, PersistentAtMaster.X, PersistentAtMaster.Y, PersistentAtMaster.Z);
        persistent.Placement!.Rotation = PersistentRot;
        persistent.MajorRecordFlagsRaw = 0x400;
        cell.Persistent.Add(persistent);
        var off = Ref(AlreadyDisabled, Stat, 200); off.MajorRecordFlagsRaw = 0x800; cell.Temporary.Add(off);
        cell.Temporary.Add(new PlacedObject(Deleted, Rel) { IsDeleted = true });
        var child = Ref(Child, Stat, 300); child.EnableParent = new EnableParent(); child.EnableParent.Reference.SetTo(Parent); cell.Temporary.Add(child);
        cell.Temporary.Add(Ref(Parent, Stat, 310));
        cell.Temporary.Add(Ref(LinkTarget, Stat, 400));
        var linker = Ref(Linker, Stat, 410); var l = new LinkedReferences(); l.Reference.SetTo(LinkTarget); linker.LinkedReferences.Add(l); cell.Temporary.Add(linker);
        cell.Temporary.Add(Ref(ActTarget, Stat, 500));
        var act = Ref(ActChild, Stat, 510); act.ActivateParents = new ActivateParents(); var ap = new ActivateParent { Delay = 0 }; ap.Reference.SetTo(ActTarget); act.ActivateParents.Parents.Add(ap); cell.Temporary.Add(act);
        cell.Persistent.Add(LoadDoor(Door, FarDoor, 600));
        cell.Temporary.Add(Ref(AliasTarget, Stat, 700));
        cell.Temporary.Add(Ref(PackLocTarget, Stat, 800));
        cell.Temporary.Add(Ref(PackTargetRef, Stat, 900));
        var npc = new PlacedNpc(Actor, Rel) { EditorID = "HcWbDisActor", Placement = new Placement { Position = new P3Float(1000, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        npc.Base.SetTo(NpcBase); cell.Temporary.Add(npc);
        var navcut = Ref(Navcut, Stat, 1100); navcut.CollisionLayer = 49; navcut.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(50, 50, 50) };
        cell.Temporary.Add(navcut);
        cell.Temporary.Add(Ref(MoveTarget, Stat, 1200));
        cell.Temporary.Add(Ref(ScriptTarget, Stat, 1300));
        var holder = Ref(ScriptHolder, Stat, 1310);
        holder.VirtualMachineAdapter = Vmad("HcWbDisHolderScript", ScriptTarget);
        cell.Temporary.Add(holder);
        cell.Temporary.Add(Ref(PersistLinkTarget, Stat, 1400));
        var plink = Ref(PersistLinker, Stat, 1410); var pl2 = new LinkedReferences(); pl2.Reference.SetTo(PersistLinkTarget); plink.LinkedReferences.Add(pl2); cell.Persistent.Add(plink);
        cell.Temporary.Add(Ref(QuestScriptTarget, Stat, 1500));
        cell.Persistent.Add(LoadDoor(OneWayDoor, FarDoor, 1600));
        var far = new SkCell(FarCell, Rel) { EditorID = "HcWbDisFarCell", Flags = SkCell.Flag.IsInteriorCell };
        far.Persistent.Add(LoadDoor(FarDoor, Door, 0));
        var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); sub.Cells.Add(far); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);

        var q = new Quest(Quest, Rel) { EditorID = "HcWbDisQuest" };
        q.Aliases.Add(new QuestAlias { ID = 0, Name = "Forced", ForcedReference = new FormLinkNullable<IPlacedGetter>(AliasTarget) });
        q.VirtualMachineAdapter = new QuestAdapter();
        var qs = new ScriptEntry { Name = "HcWbDisQuestScript" };
        qs.Properties.Add(new ScriptObjectProperty { Name = "Thing", Object = new FormLink<ISkyrimMajorRecordGetter>(QuestScriptTarget) });
        q.VirtualMachineAdapter.Scripts.Add(qs);
        m.Quests.Add(q);
        var pl = new Package(PackLocation, Rel) { EditorID = "HcWbDisPackLoc", ScheduleHour = -1 };
        var lt = new LocationTarget(); lt.Link.SetTo(PackLocTarget);
        pl.Data[0] = new PackageDataLocation { Name = "Location", Location = new LocationTargetRadius { Radius = 0, Target = lt } };
        m.Packages.Add(pl);
        var pt = new Package(PackTarget, Rel) { EditorID = "HcWbDisPackTarget", ScheduleHour = -1 };
        var ts = new PackageTargetSpecificReference(); ts.Reference.SetTo(PackTargetRef);
        pt.Data[0] = new PackageDataTarget { Name = "Target", Target = ts };
        m.Packages.Add(pt);

        var ws = new Worldspace(World, Rel) { EditorID = "HcWbDisWorld" };
        var grid = new WorldspaceGridReference { GridPosition = new P2Int16(0, 0) };
        grid.References.Add(new WorldspaceReference { Position = new P2Int16(0, 0), Reference = new FormLink<IPlacedObjectGetter>(LargeRef) });
        ws.LargeReferences.Add(grid);
        var ext = new SkCell(ExtCell, Rel) { EditorID = "HcWbDisExt", Grid = new CellGrid { Point = new P2Int(0, 0) } };
        ext.Temporary.Add(Ref(LargeRef, Stat, 1000, 1000));
        ext.Temporary.Add(Ref(PlainExt, Stat, 2000, 2000));
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var wsub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        wsub.Items.Add(ext); blk.Items.Add(wsub); ws.SubCells.Add(blk);
        m.Worldspaces.Add(ws);
        m.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // the later plugin: the persistent reference's winner, moved, still persistent, its rotation's negative zero kept
        var later = new SkyrimMod(Later, Rel);
        var lcell = new SkCell(Cell, Rel) { EditorID = "HcWbDisCell", Flags = SkCell.Flag.IsInteriorCell };
        var lp = Ref(Persistent, Stat, PersistentAtLater.X, PersistentAtLater.Y, PersistentAtLater.Z);
        lp.Placement!.Rotation = PersistentRot;
        lp.MajorRecordFlagsRaw = 0x400;
        lp.Scale = 1.25f;
        lcell.Persistent.Add(lp);
        var lblock = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var lsub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        lsub.Cells.Add(lcell); lblock.SubBlocks.Add(lsub); later.Cells.Records.Add(lblock);
        // and its copy of the worldspace wins without the large-reference list (as an eFPS-style override does): the
        // game still treats LargeRef as large (the union of every copy's list), the winning record names it nowhere
        later.Worldspaces.Add(new Worldspace(World, Rel) { EditorID = "HcWbDisWorld" });
        later.BeginWrite.ToPath(LaterPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Master.FileName + "\r\n" + Later.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Master.FileName + "\r\n*" + Later.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+DisLater\r\n+DisMaster\r\n");
    }

    static VirtualMachineAdapter Vmad(string script, FormKey target)
    {
        var v = new VirtualMachineAdapter();
        var e = new ScriptEntry { Name = script };
        e.Properties.Add(new ScriptObjectProperty { Name = "Thing", Object = new FormLink<ISkyrimMajorRecordGetter>(target) });
        v.Scripts.Add(e);
        return v;
    }

    static PlacedObject LoadDoor(FormKey key, FormKey to, float x)
    {
        var r = Ref(key, DoorBase, x);
        var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
        td.Door.SetTo(to);
        r.TeleportDestination = td;
        return r;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}
