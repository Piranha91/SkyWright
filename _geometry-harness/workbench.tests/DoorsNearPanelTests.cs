using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The context panel's doors come from SkyWright's own query over the write target's load order (step 5 of the
/// houseCARL boundary): no houseCARL call answers them, and a patch enabled or rewritten on disk is seen by the next read.</summary>
[Trait("tier", "integration")]
public sealed class DoorsNearPanelTests
{
    static Task<PatchWriter> Open(DoorWorld w) => Task.FromResult(PatchWriter.OpenInProcess(
        new WriteTarget(w.Instance, "Default", w.DataDir, Path.Combine(w.Instance, "mods")),
        Path.Combine(w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { }));

    // N1: the doors are answered with houseCARL's service closed, so no houseCARL call reads them.
    [Fact]
    public async Task N1_the_doors_are_read_with_no_houseCARL_call()
    {
        using var w = new DoorWorld();
        var writer = Open(w);
        await (await writer).DisposeAsync();                                   // any houseCARL call is refused from here
        IContextSource src = new ServerContextSource(writer, _ => null);
        var (doors, notListed) = await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius);
        Assert.Equal(new[] { DoorWorld.DoorA.ToString() }, doors);
        Assert.Equal(0, notListed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => src.NamesCountedAsync(DoorWorld.House));   // the control: the server is closed
    }

    // N2: a patch the app wrote, once enabled in the profile, is read by the next doors read; rewritten on disk, it is
    // read again.
    [Fact]
    public async Task N2_a_patch_enabled_or_rewritten_is_seen_by_the_next_read()
    {
        using var w = new DoorWorld();
        var writer = Open(w);
        IContextSource src = new ServerContextSource(writer, _ => null);
        try
        {
            Assert.Equal(new[] { DoorWorld.DoorA.ToString() }, (await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius)).Doors);
            var patch = ModKey.FromNameAndExtension("HcWbDoorsPatch.esp");
            var added = new FormKey(patch, 0x800);
            var dir = Path.Combine(w.Instance, "mods", "houseCARL - HcWbDoorsPatch");
            Directory.CreateDirectory(dir);
            WritePatch(Path.Combine(dir, patch.FileName), (added, new P3Float(0, 150, 0)));
            File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "modlist.txt"), "+houseCARL - HcWbDoorsPatch\r\n");
            File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "plugins.txt"), "*" + patch.FileName + "\r\n");
            File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "loadorder.txt"), patch.FileName + "\r\n");
            var enabled = (await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius)).Doors;
            Assert.Contains(added.ToString(), enabled);
            Assert.Contains(DoorWorld.DoorA.ToString(), enabled);

            // the same plugin rewritten: the door moved away, and a second door added beside the house
            var second = new FormKey(patch, 0x801);
            WritePatch(Path.Combine(dir, patch.FileName), (added, new P3Float(3000, 0, 0)), (second, new P3Float(0, -150, 0)));
            File.SetLastWriteTimeUtc(Path.Combine(dir, patch.FileName), DateTime.UtcNow.AddSeconds(5));
            var rewritten = (await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius)).Doors;
            Assert.DoesNotContain(added.ToString(), rewritten);
            Assert.Contains(second.ToString(), rewritten);
        }
        finally { await (await writer).DisposeAsync(); }
    }

    static void WritePatch(string path, params (FormKey Door, P3Float At)[] doors)
    {
        var rel = SkyrimRelease.SkyrimSE;
        using var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "DoorMod", DoorWorld.Master.FileName.String), rel);
        var m = new SkyrimMod(doors[0].Door.ModKey, rel);
        var cell = new Cell(DoorWorld.CellA, rel) { EditorID = "HcWbOutside", Flags = Cell.Flag.IsInteriorCell };
        foreach (var (door, at) in doors)
        {
            var r = new PlacedObject(door, rel) { Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, 0) } };
            r.Base.SetTo(new FormKey(DoorWorld.Master, 0x802));
            var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
            td.Door.SetTo(DoorWorld.DoorB);
            r.TeleportDestination = td;
            cell.Temporary.Add(r);
        }
        var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
    }
}
