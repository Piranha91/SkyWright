using HousecarlCore;
using Mutagen.Bethesda.Skyrim;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>houseCARL's code in process (<see cref="HousecarlLibrary"/>, <see cref="PatchWriter.OpenInProcess"/>): a write,
/// its read-back and its undo with no program started; no call on a window's thread; nothing after the profile closes;
/// one rulebook per process; an identity that follows houseCARL's bytes.</summary>
[Trait("tier", "integration")]
public sealed class HousecarlLibraryTests : IClassFixture<TiltedRefWorld>
{
    readonly TiltedRefWorld _w;
    public HousecarlLibraryTests(TiltedRefWorld w) => _w = w;

    WriteTarget Target(string output) => new(_w.Instance, "Default", _w.DataDir, output);
    string Output() { var o = Path.Combine(_w.Root, "out-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(o); return o; }
    PatchWriter Open(string output) => PatchWriter.OpenInProcess(Target(output), Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });

    static readonly RefPlacementEdit.Pose Drawn = new(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);

    [Fact]
    public async Task A_staged_move_is_written_in_process_read_back_from_its_patch_and_undone()
    {
        var o = Output();
        await using var w = Open(o);
        Assert.True(w.InProcess);
        var after = Drawn with { Position = new Vec3(640f, -256f, 64f) };
        var answer = await w.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbLib", dryRun: false);
        var esp = Path.Combine(o, "houseCARL - WbLib", "WbLib.esp");
        Assert.True(File.Exists(esp), $"no patch at {esp}; houseCARL said: {answer}");
        var (pos, _) = await w.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: esp);
        Assert.Equal(640f, pos.X, 3);
        var said = await w.RevertWrittenAsync(TiltedRefWorld.Ref, Path.GetDirectoryName(esp)!);
        Assert.Contains("removed", said);
        using var back = SkyrimMod.CreateFromBinaryOverlay(esp, SkyrimRelease.SkyrimSE);
        Assert.Empty(back.EnumerateMajorRecords<IPlacedObjectGetter>());
    }

    /// <summary>Rejects a houseCARL call made on a window's thread: the tool methods are synchronous, so one there would
    /// freeze the window for the whole read or write.</summary>
    [Fact]
    public void A_call_on_a_windows_thread_is_refused_and_the_async_call_from_there_runs_elsewhere()
    {
        using var lib = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-ui"), TestCorpus.Path);
        var args = new Dictionary<string, object?> { ["formids"] = new[] { TiltedRefWorld.Ref.ToString() }, ["format"] = "json" };
        Exception? direct = null; string? viaAsync = null; Exception? asyncFault = null;
        var t = new Thread(() =>
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;     // this thread now has a window dispatcher
            try { lib.Invoke(ToolNames.Records, args); } catch (Exception ex) { direct = ex; }
            try { viaAsync = lib.CallAsync(ToolNames.Records, args).GetAwaiter().GetResult(); } catch (Exception ex) { asyncFault = ex; }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        Assert.NotNull(direct);
        Assert.Contains("window's thread", direct!.Message);
        Assert.Null(asyncFault);
        Assert.Contains(TiltedRefWorld.Ref.ToString(), viaAsync);
    }

    [Fact]
    public async Task A_call_after_the_profile_closes_is_refused_in_one_sentence()
    {
        var lib = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-closed"), TestCorpus.Path);
        lib.Dispose();
        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => lib.CallAsync(ToolNames.Records, new Dictionary<string, object?> { ["formids"] = new[] { TiltedRefWorld.Ref.ToString() } }));
        Assert.Contains("after its profile was closed", ex.Message);
    }

    [Fact]
    public void A_second_rulebook_in_one_process_is_refused()
    {
        using var first = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-r1"), TestCorpus.Path);
        var other = Path.Combine(_w.Root, "other-corpus.json");
        File.Copy(TestCorpus.Path, other, overwrite: true);
        var ex = Assert.Throws<InvalidOperationException>(() => HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-r2"), other));
        Assert.Contains("start the program again", ex.Message);
        Assert.Equal(Path.GetFullPath(TestCorpus.Path), CorpusRulebook.CorpusPath, ignoreCase: true);
    }

    [Fact]
    public void An_argument_the_tool_does_not_take_is_refused_before_houseCARL_runs()
    {
        using var lib = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-arg"), TestCorpus.Path);
        var ex = Assert.Throws<InvalidOperationException>(() => lib.Invoke(ToolNames.Records, new Dictionary<string, object?> { ["formid"] = "x" }));
        Assert.Contains("formid", ex.Message);
    }

    /// <summary>Rejects an identity that does not follow houseCARL's bytes (the server path's exe hash, for one, is the
    /// same for every build of the server: it hashes the launcher, not the code).</summary>
    [Fact]
    public void The_identity_changes_when_a_houseCARL_assembly_changes_and_not_otherwise()
    {
        var a = Path.Combine(_w.Root, "id-a", "housecarl-mcp.dll"); var b = Path.Combine(_w.Root, "id-b", "housecarl-mcp.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!); Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Copy(HousecarlLibrary.AssemblyPath, a, true);
        File.Copy(HousecarlLibrary.AssemblyPath, b, true);
        Assert.Equal(HousecarlLibrary.IdentityOf(a), HousecarlLibrary.IdentityOf(b));
        using (var f = File.Open(b, FileMode.Open, FileAccess.ReadWrite)) { f.Seek(-1, SeekOrigin.End); int last = f.ReadByte(); f.Seek(-1, SeekOrigin.End); f.WriteByte((byte)(last ^ 0xFF)); }
        Assert.NotEqual(HousecarlLibrary.IdentityOf(a), HousecarlLibrary.IdentityOf(b));
        Assert.StartsWith("in-process houseCARL ", HousecarlLibrary.Identity);
    }
}
