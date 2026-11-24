using System.Text.Json.Nodes;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The strict checks on the in-process writer: patch contents, file destinations (the Output Folder rule),
/// undo, configuration identity and a "resolved" claim, each read from what is on disk (Mutagen, navownprobe), never
/// from the writer's own words. Each check is a function returning what is wrong, and each test also shows it reject a
/// wrong answer, so no check here passes by being unable to fail.</summary>
[Trait("tier", "integration")]
public sealed class InProcessStrictTests
{
    static readonly RefPlacementEdit.Pose Drawn = new(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);

    static PatchWriter Open(TiltedRefWorld w, string output) =>
        PatchWriter.OpenInProcess(new WriteTarget(w.Instance, "Default", w.DataDir, output), Path.Combine(w.Root, "hcdata-" + Guid.NewGuid().ToString("N")[..6]), TestCorpus.Path, _ => { });

    static string NewOut(string root) { var o = Path.Combine(root, "out-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(o); return o; }

    /// <summary>What is wrong with a placement patch: it must hold exactly the edited reference (and the cell it sits
    /// in, which the write path carries), at the staged position and rotation, with the world's master as a master.</summary>
    static List<string> PatchFaults(string esp, FormKey refKey, RefPlacementEdit.Pose staged)
    {
        var faults = new List<string>();
        using var mod = SkyrimMod.CreateFromBinaryOverlay(esp, SkyrimRelease.SkyrimSE);
        var placed = mod.EnumerateMajorRecords<IPlacedObjectGetter>().ToList();
        if (placed.Count != 1 || placed[0].FormKey != refKey) faults.Add($"placed references: {string.Join(", ", placed.Select(p => p.FormKey))}, not exactly {refKey}");
        foreach (var p in placed.Where(p => p.FormKey == refKey))
        {
            var pl = p.Placement!;
            if (Math.Abs(pl.Position.X - staged.Position.X) > 1e-3 || Math.Abs(pl.Position.Y - staged.Position.Y) > 1e-3 || Math.Abs(pl.Position.Z - staged.Position.Z) > 1e-3)
                faults.Add($"position {pl.Position} is not the staged {staged.Position.X},{staged.Position.Y},{staged.Position.Z}");
            if (Math.Abs(pl.Rotation.X - staged.Rotation.X) > 1e-5 || Math.Abs(pl.Rotation.Y - staged.Rotation.Y) > 1e-5 || Math.Abs(pl.Rotation.Z - staged.Rotation.Z) > 1e-5)
                faults.Add($"rotation {pl.Rotation} is not the staged {staged.Rotation.X},{staged.Rotation.Y},{staged.Rotation.Z}");
        }
        var others = mod.EnumerateMajorRecords().Where(r => r is not IPlacedObjectGetter && r is not ICellGetter).Select(r => r.FormKey.ToString()).ToList();
        if (others.Count > 0) faults.Add("records beyond the reference and its cell: " + string.Join(", ", others));
        if (!mod.ModHeader.MasterReferences.Any(m => m.Master == TiltedRefWorld.Master)) faults.Add($"{TiltedRefWorld.Master} is not a master");
        return faults;
    }

    /// <summary>Every file under <paramref name="roots"/> with its size and last write, to tell what a call wrote.</summary>
    static Dictionary<string, (long, DateTime)> Listing(params string[] roots) =>
        roots.SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories)).ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)), StringComparer.OrdinalIgnoreCase);

    static List<string> Changed(Dictionary<string, (long, DateTime)> before, Dictionary<string, (long, DateTime)> after) =>
        before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b).Order().ToList();

    /// <summary>Rejects a patch with the wrong pose, a missing master, or anything beyond the edited reference.</summary>
    [Fact]
    public async Task The_patch_holds_exactly_the_staged_placement_of_the_edited_reference()
    {
        using var w = new TiltedRefWorld();
        var o = NewOut(w.Root);
        var after = Drawn with { Position = new Vec3(700f, -300f, 90f), Rotation = RefPlacementEdit.Turned(TiltedRefWorld.Rot, System.Numerics.Vector3.UnitZ, 0.6f) };
        await using (var p = Open(w, o)) await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbStrict", dryRun: false);
        var esp = Path.Combine(o, "houseCARL - WbStrict", "WbStrict.esp");
        Assert.Empty(PatchFaults(esp, TiltedRefWorld.Ref, after));
        // the check rejects a wrong answer: the same patch against another staged pose
        Assert.NotEmpty(PatchFaults(esp, TiltedRefWorld.Ref, after with { Position = new Vec3(700f, -300f, 91f) }));
        Assert.NotEmpty(PatchFaults(esp, TiltedRefWorld.Ref, after with { Rotation = TiltedRefWorld.Rot }));
    }

    /// <summary>The Output Folder rule as the files show it: the patch lands at Output\houseCARL - name\name.esp with its
    /// marker, and nothing else anywhere changes (instance, game, Output); a refused folder gets nothing. Rejects a write
    /// that also touches the instance or the game folder.</summary>
    [Fact]
    public async Task A_write_lands_only_in_its_Output_Folder_and_a_refused_folder_gets_nothing()
    {
        using var w = new TiltedRefWorld();
        var o = NewOut(w.Root);
        var game = Path.GetDirectoryName(w.DataDir)!;
        var before = Listing(w.Instance, game, o);
        await using (var p = Open(w, o))
        {
            await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, Drawn with { Position = new Vec3(600, -256, 64) }) }, "WbWhere", dryRun: false);
            var mid = Listing(w.Instance, game, o);
            var wrote = Changed(before, mid);
            Assert.Equal(new[] { Path.Combine(o, "houseCARL - WbWhere", "meta.ini"), Path.Combine(o, "houseCARL - WbWhere", "WbWhere.esp") }.Order(StringComparer.OrdinalIgnoreCase), wrote, StringComparer.OrdinalIgnoreCase);
            foreach (var bad in new[] { w.Instance, w.DataDir, Path.Combine(w.Instance, "mods", "WorldMod"), game })
            {
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => p.CallAsync("housecarl_apply", new() { ["ops"] = new[] { new Dictionary<string, object?> { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = "1,2,3" } }, ["patch"] = "WbBad", ["out_path"] = bad }));
                Assert.StartsWith("houseCARL refused the write: ", refused.Message);
            }
            Assert.Empty(Changed(mid, Listing(w.Instance, game, o)));
        }
        // the check rejects a wrong answer: a file appearing in the instance is seen
        var mark = Listing(w.Instance, game, o);
        File.WriteAllText(Path.Combine(w.Instance, "mods", "stray.esp"), "x");
        Assert.Contains(Path.Combine(w.Instance, "mods", "stray.esp"), Changed(mark, Listing(w.Instance, game, o)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A dry run, through the app's commit and through the raw tool with and without an Output Folder, writes
    /// nothing anywhere (instance, game, Output Folder, data folder aside), and still answers what it would write.
    /// Rejects a dry run that writes (the plant the Gate B review used: the binding dropping dry_run).</summary>
    [Fact]
    public async Task A_dry_run_writes_nothing_anywhere_and_says_what_it_would_write()
    {
        using var w = new TiltedRefWorld();
        var o = NewOut(w.Root);
        var game = Path.GetDirectoryName(w.DataDir)!;
        await using var p = Open(w, o);
        var before = Listing(w.Instance, game, o);
        var said = await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, Drawn with { Position = new Vec3(600, -256, 64) }) }, "WbDry", dryRun: true);
        Assert.Contains("DRY RUN", said);
        var op = new[] { new Dictionary<string, object?> { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = "1,2,3" } };
        Assert.Contains("DRY RUN", await p.CallAsync("housecarl_apply", new() { ["ops"] = op, ["patch"] = "WbDry2", ["out_path"] = o, ["dry_run"] = true }));
        Assert.Contains("DRY RUN", await p.CallAsync("housecarl_apply", new() { ["ops"] = op, ["patch"] = "WbDry3", ["dry_run"] = true }));
        var wrote = Changed(before, Listing(w.Instance, game, o));
        Assert.True(wrote.Count == 0, "a dry run wrote " + string.Join(", ", wrote));
    }

    /// <summary>Undo removes the reference's override from the patch, keeps the file, and leaves the load order's own
    /// placement as the winner again. Rejects an undo that writes the old pose back (an override would remain).</summary>
    [Fact]
    public async Task Undo_removes_the_override_keeps_the_file_and_the_winner_is_the_load_orders_again()
    {
        using var w = new TiltedRefWorld();
        var o = NewOut(w.Root);
        var after = Drawn with { Position = new Vec3(640f, -256f, 64f) };
        var esp = Path.Combine(o, "houseCARL - WbUndo", "WbUndo.esp");
        await using var p = Open(w, o);
        await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbUndo", dryRun: false);
        static int Overrides(string f, FormKey k) { using var m = SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE); return m.EnumerateMajorRecords<IPlacedObjectGetter>().Count(r => r.FormKey == k); }
        Assert.Equal(1, Overrides(esp, TiltedRefWorld.Ref));                  // the check sees an override when there is one
        var keep = Path.Combine(w.Root, "before-undo-" + Guid.NewGuid().ToString("N")[..6], "WbUndo.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!); File.Copy(esp, keep);
        await p.RevertWrittenAsync(TiltedRefWorld.Ref, Path.GetDirectoryName(esp)!);
        Assert.True(File.Exists(esp), "the patch file was deleted by the undo");
        Assert.Equal(0, Overrides(esp, TiltedRefWorld.Ref));
        Assert.Equal(1, Overrides(keep, TiltedRefWorld.Ref));                 // and rejects the file as it was before the undo
        var (pos, _) = await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString());
        Assert.Equal(TiltedRefWorld.Pos.X, pos.X, 3);
    }

    /// <summary>A decision is keyed on what read its objects: the in-process writer names houseCARL's build by its
    /// assemblies' bytes, and a decision stored under another build says the reader changed. Rejects an identity that
    /// does not follow the reader (Decisions.Compare would then say nothing changed).</summary>
    [Fact]
    public async Task A_decisions_reader_identity_is_houseCARLs_build_and_another_build_is_said()
    {
        using var w = new TiltedRefWorld();
        await using var p = Open(w, NewOut(w.Root));
        Assert.Equal(HarnessShared.HousecarlCalls.Identity, p.ReaderBuild);
        var objects = await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) });
        var row = new RowState("Holds", "s", "b");
        var stored = new Decision(w.Instance, "Default", "geometry.object-overlap", "k", "intentional", "r", DateTimeOffset.UtcNow, row, objects, p.ReaderBuild);
        var same = Decisions.Compare(stored, row, objects, p.ReaderBuild);
        Assert.DoesNotContain("reader changed", same.Sentence);
        var other = Decisions.Compare(stored, row, objects, "in-process houseCARL 0000000000000000 (another build)");
        Assert.Contains("reader changed", other.Sentence);
    }

    /// <summary>A navmesh repair written in process is resolved as navownprobe reads it from the patch: the planned link
    /// re-pointed and the carried cell its winner's. Rejects a patch without the repair: with L5's navmesh taken out of a
    /// copy of the patch, the same read-back fails.</summary>
    [Fact]
    public async Task A_repair_written_in_process_reads_back_resolved_and_a_patch_without_it_does_not()
    {
        using var w = new NavRepairWorld();
        const string L5 = "000A05:Skyrim.esm";
        var work = Path.Combine(w.Root, "strict-work");
        var preview = NavRepairLane.Preview(NavRepairWorld.Probe, w.Candidate, "edge-idx|" + L5, "EDGE-IDX", L5, w.Instance, "P", w.Game, Path.Combine(work, "preview"));
        await using var p = PatchWriter.OpenInProcess(new WriteTarget(w.Instance, "P", w.DataDir, w.Output), Path.Combine(w.Root, "hcdata-strict"), TestCorpus.Path, _ => { });
        var r = await NavRepairLane.WriteAsync(p, preview, "WbStrictRepair", NavRepairWorld.Probe, w.Instance, "P", w.Game, Path.Combine(work, "write"));
        Assert.True(r.ReadBackHolds, "the read-back: " + r.ReadBack);
        Assert.Contains((L5, "EDGE-IDX", "repaired"), r.Verdicts);

        // the same read-back on a copy of the patch with L5's navmesh removed (houseCARL's remove, into the copy)
        var folder = Path.GetDirectoryName(r.Patch)!;
        var copy = Path.Combine(w.Output, "houseCARL - WbStrictRepairCopy");
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(folder)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f) == "WbStrictRepair.esp" ? "WbStrictRepairCopy.esp" : Path.GetFileName(f)));
        var navmesh = JsonNode.Parse(File.ReadAllText(preview.Plan))!["plans"]!.AsArray().Select(x => (string)x!["source"]!).First();
        var removed = await p.CallAsync("housecarl_remove", new() { ["formids"] = new[] { navmesh }, ["into"] = copy });
        Assert.DoesNotContain("error:", removed);
        var outFile = Path.Combine(work, "readback-copy.json");
        var (exit, _) = Recheck.Run(NavRepairWorld.Probe, new[] { "--repair-readback", preview.Plan, Path.Combine(copy, "WbStrictRepairCopy.esp"), outFile }, Path.Combine(work, "readback-copy.log"));
        Assert.True(exit != 0 || !(bool)JsonNode.Parse(File.ReadAllText(outFile))!["holds"]!, "the read-back held on a patch without the repair");
    }
}
