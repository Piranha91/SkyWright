using System.Diagnostics;
using System.Text.Json;
using HousecarlCore;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The app's free-mode flows on the in-process path start no houseCARL program: save a staged edit, read it
/// back, undo it, write and read back and re-check a navmesh repair, show context for a selected reference, and read a
/// bundle's names. Every process the test process or its descendants start is seen (<see cref="ProcessWatch"/>), and
/// the test fails on any housecarl-mcp.exe, or on one that exits before it is named. The same flows on the server path
/// are shown caught, so the watch can fail. Until the callers are switched (commit 6), the flows are driven through the
/// in-process writer directly, as the app's callers will use it; houseCARL mode starts the user's own server by design
/// and is not covered.</summary>
[Collection("ProcessWatch")]
[Trait("tier", "integration")]
public sealed class NoHousecarlProgramTests
{
    static readonly RefPlacementEdit.Pose Drawn = new(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
    const string L5 = "000A05:Skyrim.esm";

    /// <summary>The flows, on writers <paramref name="open"/> makes; returns every process they started, named.</summary>
    static async Task<IReadOnlyList<(int Pid, string? Image)>> Flows(Func<WriteTarget, string, Task<PatchWriter>> open, bool namesRead)
    {
        using var w = new TiltedRefWorld();
        using var nav = new NavRepairWorld();
        using var doors = new DoorWorld();
        var mark = ProcessWatch.Mark();
        // save, read back, undo
        var o = Path.Combine(w.Root, "out"); Directory.CreateDirectory(o);
        await using (var p = await open(new WriteTarget(w.Instance, "Default", w.DataDir, o), Path.Combine(w.Root, "hc")))
        {
            var after = Drawn with { Position = new Vec3(640f, -256f, 64f) };
            Assert.Contains("wrote ", await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbNoProg", dryRun: false));
            var esp = Path.Combine(o, "houseCARL - WbNoProg", "WbNoProg.esp");
            Assert.Equal(640f, (await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: esp)).Pos.X, 3);
            Assert.Contains("removed", await p.RevertWrittenAsync(TiltedRefWorld.Ref, Path.GetDirectoryName(esp)!));
        }
        // a navmesh repair: write, read back, re-check (navownprobe runs: it is SkyWright's)
        await using (var p = await open(new WriteTarget(nav.Instance, "P", nav.DataDir, nav.Output), Path.Combine(nav.Root, "hc")))
        {
            var work = Path.Combine(nav.Root, "work");
            var preview = NavRepairLane.Preview(NavRepairWorld.Probe, nav.Candidate, "edge-idx|" + L5, "EDGE-IDX", L5, nav.Instance, "P", nav.Game, Path.Combine(work, "preview"));
            var r = await NavRepairLane.WriteAsync(p, preview, "WbNoProgRepair", NavRepairWorld.Probe, nav.Instance, "P", nav.Game, Path.Combine(work, "write"));
            Assert.True(r.ReadBackHolds, r.ReadBack);
            Assert.Contains((L5, "EDGE-IDX", "repaired"), r.Verdicts);
        }
        // context for a selected reference
        var dout = Path.Combine(doors.Root, "out"); Directory.CreateDirectory(dout);
        var writer = open(new WriteTarget(doors.Instance, "Default", doors.DataDir, dout), Path.Combine(doors.Root, "hc"));
        IContextSource src = new ServerContextSource(writer, _ => null);
        Assert.Contains(DoorWorld.DoorA.ToString(), (await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius)).Doors);
        Assert.Equal(DoorWorld.DoorB.ToString(), (await src.DestinationAsync(DoorWorld.DoorA.ToString())).FarDoor);
        await (await writer).DisposeAsync();
        // a bundle's names (runcontrol runs: it is SkyWright's)
        if (namesRead)
        {
            InPlaceRunTests.SelWorld(w);
            var game = Path.GetDirectoryName(w.DataDir)!;
            var entry = Path.Combine(w.Root, "entry.json");
            File.WriteAllText(entry, JsonSerializer.Serialize(new
            {
                schema = 1, id = "wb-noprog", inPlace = new { instance = w.Instance, profile = "Default", game },
                selection = new { mods = new[] { "SelMod" } }, evaluation = new { select = new[] { "geometry.object-overlap" }, depth = "standard" },
            }));
            var mf = Path.Combine(w.Root, "machine.json");
            File.WriteAllText(mf, "{}");
            var psi = new ProcessStartInfo(R3Bundle.Runcontrol) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "run", "--entry", entry, "--machine", mf, "--out", Path.Combine(w.Root, "work"), "--through", "bundle" }) psi.ArgumentList.Add(a);
            using var run = Process.Start(psi)!;
            var so = run.StandardOutput.ReadToEndAsync(); var se = run.StandardError.ReadToEndAsync();
            run.WaitForExit();
            Assert.True(run.ExitCode == 0, $"the run through its names read exited {run.ExitCode}: {(so.Result + se.Result)[^Math.Min(800, (so.Result + se.Result).Length)..]}");
        }
        return ProcessWatch.After(mark);
    }

    /// <summary>Rejects any start of houseCARL's program, and any process gone before it could be named.</summary>
    [Fact]
    public async Task The_free_mode_flows_on_the_in_process_path_start_no_houseCARL_program()
    {
        var started = await Flows((t, hc) => Task.FromResult(PatchWriter.OpenInProcess(t, hc, TestCorpus.Path, _ => { })), namesRead: true);
        var bad = started.Where(p => p.Image is null || ProcessWatch.IsHousecarlServer(p.Image)).ToList();
        Assert.True(bad.Count == 0, "the in-process flows started " + string.Join(", ", bad.Select(p => p.Image ?? $"pid {p.Pid}, gone before it was named"))
                                    + "; all started: " + string.Join(", ", started.Select(p => Path.GetFileName(p.Image ?? "?"))));
        Assert.Contains(started, p => Path.GetFileName(p.Image ?? "") == "navownprobe.exe");      // the watch saw the flows' own programs
        Assert.Contains(started, p => Path.GetFileName(p.Image ?? "") == "runcontrol.exe");
    }

    /// <summary>The control, since the server path is gone (step 4): a program named housecarl-mcp.exe, started by a
    /// descendant of this process and ending at once, is caught by name. Before, the same flows on the server path were
    /// the control, caught three times (GATE-B.md).</summary>
    [Fact]
    public void A_program_named_housecarl_mcp_started_by_a_descendant_is_caught()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-wb-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var named = Path.Combine(dir, "housecarl-mcp.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), named);
            var mark = ProcessWatch.Mark();
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "/d", "/c", named }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var started = ProcessWatch.After(mark);
            Assert.Contains(started, p => ProcessWatch.IsHousecarlServer(p.Image));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
