using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A synthetic MO2 instance: one master holding an interior cell with one placed reference, tilted on all three
/// axes; one profile loading it. Built with Mutagen in a temp folder, deleted after.</summary>
public sealed class TiltedRefWorld : IDisposable
{
    internal static readonly ModKey Master = new("HcWbWorld", ModType.Master);
    internal static readonly FormKey Ref = new(Master, 0x810);
    internal static readonly Vec3 Pos = new(512f, -256f, 64f);
    internal static readonly Vec3 Rot = new(0.25f, -0.4f, 4.78319f);

    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string DataDir => Path.Combine(Root, "game", "Data");

    public TiltedRefWorld() : this(withMesh: false) { }

    /// <summary>The model file the static names, when the world carries one: loose in the world's mod folder.</summary>
    internal string MeshFile => Path.Combine(Instance, "mods", "WorldMod", "meshes", "hcwb", "box.nif");

    internal TiltedRefWorld(bool withMesh)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wb-world-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(mods); Directory.CreateDirectory(DataDir);
        var m = new SkyrimMod(Master, SkyrimRelease.SkyrimSE);
        var stat = new Static(new FormKey(Master, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcWbStatic" };
        if (withMesh) stat.Model = new Model { File = @"hcwb\box.nif" };
        m.Statics.Add(stat);
        var cell = new Cell(new FormKey(Master, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcWbCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Temporary.Add(new PlacedObject(Ref, SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcWbRef",
            Base = stat.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(Pos.X, Pos.Y, Pos.Z), Rotation = new P3Float(Rot.X, Rot.Y, Rot.Z) },
        });
        var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        var dir = Path.Combine(mods, "WorldMod"); Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, Master.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        if (withMesh) { Directory.CreateDirectory(Path.GetDirectoryName(MeshFile)!); File.WriteAllText(MeshFile, "nif bytes, version one"); }
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+WorldMod\r\n");
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}

/// <summary>The rulebook the server reads, generated once per test run by the generator's own entry point, as the
/// houseCARL suite's TestCorpus does.</summary>
public static class TestCorpus
{
    static readonly Lazy<string> Generated = new(() =>
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc-wb-corpus-" + Guid.NewGuid().ToString("N"));
        HousecarlGenerator.CorpusGenerator.GenerateAll(System.IO.Path.Combine(dir, "gen"), System.IO.Path.Combine(dir, "ref"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch { } };
        return System.IO.Path.Combine(dir, "gen", "corpus.json");
    });
    public static string Path => Generated.Value;
}

/// <summary>The write path end to end: the edit staged in the workbench, written by the houseCARL server this checkout
/// builds (<c>src/housecarl-mcp/bin/Release</c>), read back from the written file.</summary>
[Trait("tier", "integration")]
public class PatchWriteTests : IClassFixture<TiltedRefWorld>
{
    readonly TiltedRefWorld _w;
    public PatchWriteTests(TiltedRefWorld w) => _w = w;

    static string ServerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, HarnessShared.CheckoutLayout.Marker))) dir = dir.Parent;
        Assert.NotNull(dir);
        var exe = TestBuild.Server;
        Assert.True(File.Exists(exe), $"build src/housecarl-mcp in Release first: {exe}");
        return exe;
    }

    Task<PatchWriter> Start() => PatchWriter.StartAsync(ServerExe(),
        new WriteTarget(_w.Instance, "Default", _w.DataDir, Path.Combine(_w.Instance, "mods")),
        Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });

    [Fact]
    public async Task A_staged_move_and_turn_is_written_as_one_override_whose_placement_reads_back_as_staged()
    {
        var before = new RefPlacementEdit.Pose(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
        var after = before with
        {
            Position = new Vec3(700f, -300f, 90f),
            Rotation = RefPlacementEdit.Turned(TiltedRefWorld.Rot, System.Numerics.Vector3.UnitZ, 0.6f),
        };
        await using var w = await Start();
        var answer = await w.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, before, after) }, "WbReadback", dryRun: false);

        var written = Directory.GetFiles(Path.Combine(_w.Instance, "mods"), "WbReadback*.esp", SearchOption.AllDirectories);
        Assert.True(written.Length == 1, $"expected one written patch, found {written.Length}; the server said: {answer}");
        using var patch = SkyrimMod.CreateFromBinaryOverlay(written[0], SkyrimRelease.SkyrimSE);
        var refs = patch.EnumerateMajorRecords<IPlacedObjectGetter>().ToList();
        Assert.Single(refs);
        Assert.Equal(TiltedRefWorld.Ref, refs[0].FormKey);
        Assert.Contains(patch.ModHeader.MasterReferences, mr => mr.Master == TiltedRefWorld.Master);

        var (pos, rot) = await w.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: Path.GetFileName(written[0]));
        Assert.Equal(after.Position.X, pos.X, 3); Assert.Equal(after.Position.Y, pos.Y, 3); Assert.Equal(after.Position.Z, pos.Z, 3);
        // The written angles, rebuilt by the detectors' convention, are the rotation the preview drew.
        var drawn = SpatialOverlap.RefrRotation(after.Rotation);
        var back = SpatialOverlap.RefrRotation(rot);
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Assert.Equal(drawn[r, c], back[r, c], 4);
    }

    // The lab-only guard's test is replaced by OutputFolderRuleTests and ManualWriteTests.H2a.

    [Fact]
    public async Task A_commit_is_refused_when_the_scene_drew_a_different_placement_than_the_server_reads()
    {
        var stale = new RefPlacementEdit.Pose(new Vec3(1f, 2f, 3f), TiltedRefWorld.Rot, 1f);
        await using var w = await Start();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, stale, stale with { Position = new Vec3(9f, 9f, 9f) }) }, "WbStale", dryRun: true));
        Assert.Contains("not the same configuration", ex.Message);
    }
}
