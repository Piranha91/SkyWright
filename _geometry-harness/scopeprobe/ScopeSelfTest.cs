using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// scopeprobe --self-test: MovedRefFixture (mr/), a copy of MrA.esp whose first REFR cannot be read (broken/), and a plugin
// whose one door leads to a reference no plugin files (door/), written under the fixture folder (PD_FIXTURE_KEEP keeps them
// for a probe run); then the probe's own decisions over them, each against an answer worked out by hand.
static class ScopeSelfTest
{
    public static int Run() => ProbeSelfTest.Run("scopeprobe", "the scope fixtures through the footprint and outcome decisions", t =>
    {
        var mr = MovedRefFixture.Write(Path.Combine(t.Root, "mr"));
        var mrA = mr.Single(p => Path.GetFileName(p) == "MrA.esp");
        var brokenDir = Path.Combine(t.Root, "broken"); Directory.CreateDirectory(brokenDir);
        var broken = Path.Combine(brokenDir, "MrA.esp");
        File.WriteAllBytes(broken, MovedRefFixture.BreakFirstRefData(File.ReadAllBytes(mrA)));
        var door = ScopeDoorFixture.Write(Path.Combine(t.Root, "door"));
        t.Check("PD2-F0 the fixtures write: MovedRefFixture, the broken MrA copy, the door plugin",
                mr.All(File.Exists) && File.Exists(broken) && File.Exists(door), string.Join(", ", mr.Append(broken).Append(door).Select(Path.GetFileName)));
    });
}

/// <summary>SdDoor.esp: a door base, and an interior cell holding one placed door whose teleport destination is a door
/// reference no plugin files.</summary>
static class ScopeDoorFixture
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey Key = new("SdDoor", ModType.Plugin);
    public static readonly FormKey DoorBase = new(Key, 0x800), CellKey = new(Key, 0x801), Placed = new(Key, 0x802), Nowhere = new(Key, 0x8FF);

    public static string Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var mod = new SkyrimMod(Key, Rel);
        mod.Doors.Add(new Door(DoorBase, Rel) { EditorID = "SdDoorBase" });
        var cell = new Cell(CellKey, Rel) { EditorID = "SdDoorCell", Flags = Cell.Flag.IsInteriorCell };
        var r = new PlacedObject(Placed, Rel) { Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(DoorBase);
        var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
        td.Door.SetTo(Nowhere);
        r.TeleportDestination = td;
        cell.Temporary.Add(r);
        uint id = CellKey.ID;
        var block = new CellBlock { BlockNumber = (int)(id % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = (int)((id / 10) % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
        var path = Path.Combine(dir, Key.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }
}
