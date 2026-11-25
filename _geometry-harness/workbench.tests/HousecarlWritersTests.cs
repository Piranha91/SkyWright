using HousecarlCore;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>One houseCARL writer per write target, with one owner (<see cref="HousecarlWriters"/>): a repair after a
/// profile change goes through the new target, never the old; a retired writer refuses in one sentence and the next
/// use opens the new one; nothing closes a service on a window's thread.</summary>
[Collection("ProcessWatch")]   // runs after the parallel tests: a retire closes every writer the owner holds
[Trait("tier", "integration")]
public sealed class HousecarlWritersTests
{
    const string L5 = "000A05:Skyrim.esm";

    static RankedFinding Row() => new(1, new FindingRow("geometry.navmesh-ownership", "EDGE-IDX", "Skyrim.esm", "RpA.esp", "L5 links R5 triangle 3, past its table",
                                                        L5, null, "the names read did not reach this reference, so its cell is not known", "finding", "edge-idx|" + L5), false, "", 0);

    /// <summary>Rejects a desk that keeps the writer of the first target it was given (the stale profile of the Gate A
    /// review, F4): after the target changes, the repair must land under the new target's Output Folder.</summary>
    [Fact]
    public async Task A_repair_after_a_target_change_goes_through_the_new_target_never_the_old()
    {
        using var w = new NavRepairWorld();
        string outA = Path.Combine(w.Root, "out-a"), outB = Path.Combine(w.Root, "out-b");
        Directory.CreateDirectory(outA); Directory.CreateDirectory(outB);
        var current = outA;
        var desk = new RepairDesk(() => (w.Candidate, new WriteTarget(w.Instance, "P", w.DataDir, current), NavRepairWorld.Probe, Path.Combine(w.Root, "work")),
                                  t => Task.FromResult(PatchWriter.OpenInProcess(t, Path.Combine(w.Root, "hc-" + Guid.NewGuid().ToString("N")[..6]), TestCorpus.Path, _ => { })));
        try
        {
            await desk.PreviewAsync(Row());
            var a = await desk.WriteAsync(Row(), "WbDeskA");
            Assert.StartsWith(Path.GetFullPath(outA), Path.GetFullPath(a.Patch), StringComparison.OrdinalIgnoreCase);
            current = outB;                                                   // the profile's Output Folder changes
            await desk.PreviewAsync(Row());
            var b = await desk.WriteAsync(Row(), "WbDeskB");
            Assert.True(Path.GetFullPath(b.Patch).StartsWith(Path.GetFullPath(outB), StringComparison.OrdinalIgnoreCase),
                        $"the repair after the change was written to {b.Patch}, not under the new target's Output Folder {outB}");
            Assert.Empty(Directory.GetFiles(outA, "WbDeskB*.esp", SearchOption.AllDirectories));
        }
        finally { await desk.DisposeWriterAsync(); }
    }

    /// <summary>Rejects a writer used after its profile was retired: the call is refused in one sentence, and the owner
    /// hands out a new writer for the target.</summary>
    [Fact]
    public async Task A_retired_writer_refuses_in_one_sentence_and_the_next_use_gets_a_new_one()
    {
        using var w = new TiltedRefWorld();
        var o = Path.Combine(w.Root, "out"); Directory.CreateDirectory(o);
        var t = new WriteTarget(w.Instance, "Default", w.DataDir, o);
        var first = HousecarlWriters.For(t, Path.Combine(w.Root, "hc"), TestCorpus.Path, _ => { });
        Assert.Same(first, HousecarlWriters.For(t, Path.Combine(w.Root, "hc"), TestCorpus.Path, _ => { }));
        Assert.True(HousecarlWriters.Owns(first));
        await HousecarlWriters.RetireAll();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => first.ReadPlacementAsync(TiltedRefWorld.Ref.ToString()));
        Assert.Equal("houseCARL's housecarl_records was called after its profile was closed; open the profile again.", ex.Message);
        var next = HousecarlWriters.For(t, Path.Combine(w.Root, "hc"), TestCorpus.Path, _ => { });
        Assert.NotSame(first, next);
        Assert.Equal(TiltedRefWorld.Pos.X, (await next.ReadPlacementAsync(TiltedRefWorld.Ref.ToString())).Pos.X, 3);
        await HousecarlWriters.RetireAll();
    }

    /// <summary>Rejects a close on a window's thread (it would wait there for a read or an index build); the owner's
    /// retire, asked from a window's thread, closes on the pool.</summary>
    [Fact]
    public void A_service_is_never_closed_on_a_windows_thread()
    {
        using var w = new TiltedRefWorld();
        var o = Path.Combine(w.Root, "out"); Directory.CreateDirectory(o);
        var t = new WriteTarget(w.Instance, "Default", w.DataDir, o);
        var lib = HousecarlLibrary.Open(t, Path.Combine(w.Root, "hc-lib"), TestCorpus.Path);
        Exception? onWindow = null; Exception? retire = null;
        var th = new Thread(() =>
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try { lib.Dispose(); } catch (Exception ex) { onWindow = ex; }
            HousecarlWriters.For(t, Path.Combine(w.Root, "hc-own"), TestCorpus.Path, _ => { });
            try { HousecarlWriters.RetireAll().GetAwaiter().GetResult(); } catch (Exception ex) { retire = ex; }
        });
        th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
        Assert.Equal("houseCARL's service was closed on a window's thread, where it would freeze the window until its calls end; this is a fault in SkyWright.", onWindow?.Message);
        Assert.Null(retire);
        lib.Dispose();
    }
}
