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

    /// <summary>Rejects taking a pool thread for a window's because some code attached a dispatcher to it: the Release suite
    /// run before commit 8 had a test do that, and four in-process calls on the pool were refused.</summary>
    [Fact]
    public async Task A_pool_thread_with_a_stray_dispatcher_is_not_a_windows_thread()
    {
        var (window, mta) = await Task.Run(() => { _ = System.Windows.Threading.Dispatcher.CurrentDispatcher; return (HousecarlLibrary.OnWindowThread(), Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA); });
        Assert.True(mta, "the pool thread was not a multi-threaded apartment");
        Assert.False(window);
    }

    [Fact]
    public async Task A_call_after_the_profile_closes_is_refused_in_one_sentence()
    {
        var lib = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-closed"), TestCorpus.Path);
        lib.Dispose();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lib.CallAsync(ToolNames.Records, new Dictionary<string, object?> { ["formids"] = new[] { TiltedRefWorld.Ref.ToString() } }));
        Assert.Equal("houseCARL's housecarl_records was called after its profile was closed; open the profile again.", ex.Message);
    }

    /// <summary>What the server's argument shim did that a typed call cannot need: every parameter of the five tools has
    /// a default, so none can go missing; a parameter without one is refused, never passed as null; an in_place edit is
    /// refused outright; and an argument that only the shim's coercion would accept (a bare string for a list) is
    /// refused in one sentence, not coerced. Rejects a call site that starts relying on any of them.</summary>
    [Fact]
    public void What_the_servers_argument_shim_did_is_refused_in_one_sentence_in_process()
    {
        using var lib = HousecarlLibrary.Open(Target(Output()), Path.Combine(_w.Root, "hcdata-shim"), TestCorpus.Path);
        foreach (var t in new[] { typeof(HousecarlMcp.RecordsTools).GetMethod("Records")!, typeof(HousecarlMcp.ApplyTools).GetMethod("Apply")!,
                                  typeof(HousecarlMcp.RemoveTools).GetMethod("Remove")!, typeof(HousecarlMcp.ForwardTools).GetMethod("Forward")!,
                                  typeof(HousecarlMcp.AssetTools).GetMethod("AssetStatus")! })
            Assert.All(t.GetParameters().Where(p => p.ParameterType != typeof(HousecarlMcp.LoadOrderService) && p.ParameterType != typeof(CancellationToken)),
                       p => Assert.True(p.HasDefaultValue, $"{t.Name}'s {p.Name} has no default, so a call that leaves it out must be refused"));
        var inPlace = Assert.Throws<InvalidOperationException>(() => lib.Invoke(ToolNames.Apply, new Dictionary<string, object?> { ["ops"] = Array.Empty<object>(), ["in_place"] = "HcWbWorld.esm" }));
        Assert.Equal("houseCARL's housecarl_apply was asked to edit a plugin in place, which SkyWright never does; this is a fault in SkyWright.", inPlace.Message);
        var bare = Assert.Throws<InvalidOperationException>(() => lib.Invoke(ToolNames.Records, new Dictionary<string, object?> { ["formids"] = TiltedRefWorld.Ref.ToString() }));
        Assert.StartsWith("houseCARL's housecarl_records could not be given its formids (", bare.Message);
        Assert.DoesNotContain('\n', bare.Message);
        // a nested part houseCARL marks required (an op's formid) is houseCARL's own refusal, in one answer
        var said = lib.Invoke(ToolNames.Apply, new Dictionary<string, object?> { ["ops"] = new[] { new Dictionary<string, object?> { ["field_path"] = "Placement.Position", ["value"] = "1,2,3" } }, ["patch"] = "WbShim", ["dry_run"] = true });
        Assert.StartsWith("error:", said.TrimStart());
    }

    [Fact]
    public void An_assembly_that_is_not_a_file_is_one_sentence_and_the_builds_two_assemblies_are_files()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HousecarlLibrary.IdentityOf(""));
        Assert.Equal("houseCARL's code is not a file this program can read (''), so what reads and writes cannot be named; reinstall SkyWright.", ex.Message);
        Assert.True(File.Exists(typeof(HousecarlMcp.LoadOrderService).Assembly.Location) && File.Exists(typeof(HousecarlCore.WriteEngine).Assembly.Location),
                    "the build's houseCARL assemblies are not files beside it");
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
