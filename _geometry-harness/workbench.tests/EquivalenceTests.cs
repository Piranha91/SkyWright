using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Gate B's equivalence: every pre-existing patch-writing, undo, repair and read-back fixture run on both ways of
/// writing (the started server, the oracle; houseCARL's code in process), the same calls in the same order on the same
/// world, each arm writing to its own Output Folder. Compared per answer, per file and per record (<see cref="Equiv"/>);
/// the process watch shows the server arm started housecarl-mcp.exe and the in-process arm started none. Set
/// WB_EVIDENCE_DIR to keep each fixture's answers, files and differences.</summary>
[Collection("ProcessWatch")]
[Trait("tier", "integration")]
public sealed class EquivalenceTests
{
    static readonly RefPlacementEdit.Pose Drawn = new(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The steps run on one arm's writer; each outcome the app would act on goes into <see cref="Arm.Outcomes"/>.</summary>
    delegate Task Steps(PatchWriter w, Arm arm);

    /// <summary>Runs <paramref name="steps"/> on the server arm, then the in-process arm, each in its own Output Folder
    /// (or the same mods folder, when <paramref name="output"/> names it); <paramref name="after"/> puts the world back
    /// between arms and gathers what the arm wrote into its own folder. Fails on any difference, and unless the server
    /// arm started houseCARL's program and the in-process arm started none.</summary>
    static async Task Both(string fixture, string root, Func<string, WriteTarget> target, Steps steps,
                           Func<Arm, string?>? output = null, Action<Arm>? after = null, IEnumerable<string>? allowedPrograms = null)
    {
        var arms = new List<Arm>();
        foreach (var name in new[] { "server", "inproc" })
        {
            var armRoot = Path.Combine(root, "eq-" + fixture + "-" + name);
            var arm = new Arm { Name = name, Hc = Path.Combine(armRoot, "hc") };
            arm.Out = output?.Invoke(arm) ?? Path.Combine(armRoot, "out");
            Directory.CreateDirectory(arm.Out);
            var mark = ProcessWatch.Mark();
            var t = target(arm.Out);
            await using (var w = name == "server" ? await PatchWriter.StartAsync(TestBuild.Server, t, arm.Hc, TestCorpus.Path, _ => { })
                                                  : PatchWriter.OpenInProcess(t, arm.Hc, TestCorpus.Path, _ => { }))
            {
                w.Answered = (tool, flagged, text) => { lock (arm.Answers) arm.Answers.Add((tool, flagged, text)); };
                await steps(w, arm);
            }
            arm.Started = ProcessWatch.After(mark);
            after?.Invoke(arm);
            arms.Add(arm);
        }
        var (s, i) = (arms[0], arms[1]);
        var diffs = Equiv.CompareAnswers(s, i);
        var trees = new[] { ("server-files", Kept(s)), ("inproc-files", Kept(i)) };
        diffs.AddRange(Equiv.CompareTrees(Kept(s), Kept(i), "server", "inproc"));
        Equiv.Keep(fixture, s, i, diffs, trees);
        Assert.True(diffs.Count == 0, $"{fixture}: the two ways of writing differ:\n" + string.Join("\n", diffs));
        Assert.True(s.Answers.Count > 0, $"{fixture}: no houseCARL call was made");
        Assert.True(s.Started.Any(p => ProcessWatch.IsHousecarlServer(p.Image)), $"{fixture}: the server arm started no housecarl-mcp.exe: {string.Join(", ", s.Started.Select(p => p.Image))}");
        var allowed = (allowedPrograms ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inStarted = i.Started.Where(p => p.Image is null || !allowed.Contains(Path.GetFileName(p.Image))).ToList();
        Assert.True(inStarted.Count == 0, $"{fixture}: the in-process arm started {string.Join(", ", inStarted.Select(p => p.Image ?? $"pid {p.Pid}, gone before it was named"))}");
    }

    /// <summary>Where an arm's written files are compared from: its Output Folder, or the folder <c>after</c> gathered them into.</summary>
    static string Kept(Arm a) => Directory.Exists(Path.Combine(Path.GetDirectoryName(a.Hc)!, "gathered")) ? Path.Combine(Path.GetDirectoryName(a.Hc)!, "gathered") : a.Out;

    /// <summary>One outcome: what the call returned, as JSON, or the sentence it was refused with.</summary>
    static async Task Do(Arm arm, string what, Func<Task<object?>> call)
    {
        try { arm.Outcomes.Add($"{what}: {JsonSerializer.Serialize(await call(), Indented)}"); }
        catch (Exception ex) { arm.Outcomes.Add($"{what}: threw {ex.GetType().Name}: {ex.Message}"); }
    }

    static Dictionary<string, object?> Move(float z) => new()
    {
        ["ops"] = new[] { new Dictionary<string, object?> { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = $"512,-256,{z}" } },
    };
    static Dictionary<string, object?> With(Dictionary<string, object?> d, params (string K, object? V)[] kv) { foreach (var (k, v) in kv) d[k] = v; return d; }

    // ---- the comparator's own known answers (Gate A review F6) -----------------------------------------------------

    [Fact]
    public async Task The_comparator_names_one_changed_byte_in_one_record_one_extra_file_and_one_differing_refusal()
    {
        using var w = new TiltedRefWorld();
        var root = Path.Combine(w.Root, "known");
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(a);
        await using (var p = PatchWriter.OpenInProcess(new WriteTarget(w.Instance, "Default", w.DataDir, a), Path.Combine(root, "hc"), TestCorpus.Path, _ => { }))
            await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, Drawn with { Position = new Vec3(600, -256, 64) }) }, "WbKnown", dryRun: false);
        foreach (var f in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
        { var d = Path.Combine(b, Path.GetRelativePath(a, f)); Directory.CreateDirectory(Path.GetDirectoryName(d)!); File.Copy(f, d); }
        Assert.Empty(Equiv.CompareTrees(a, b));                                     // a copy is the same

        // one byte inside the placed reference's record, the last of its data
        var esp = Path.Combine(b, "houseCARL - WbKnown", "WbKnown.esp");
        var bytes = File.ReadAllBytes(esp);
        var key = "REFR " + TiltedRefWorld.Ref.ID.ToString("X6") + ":" + TiltedRefWorld.Master.FileName;
        var (start, length) = Equiv.RecordSpans(bytes, "WbKnown.esp")[key];
        bytes[start + length - 1] ^= 0xFF;
        File.WriteAllBytes(esp, bytes);
        var d1 = Equiv.CompareTrees(a, b);
        Assert.True(d1.Count == 1 && d1[0].Contains("record " + key + " differs"), "one changed byte was not named as its record: " + string.Join(" | ", d1));
        File.Copy(Path.Combine(a, "houseCARL - WbKnown", "WbKnown.esp"), esp, true);

        // one extra file
        File.WriteAllText(Path.Combine(b, "houseCARL - WbKnown", "extra.txt"), "x");
        var d2 = Equiv.CompareTrees(a, b);
        Assert.True(d2.Count == 1 && d2[0] == "only in B: " + Path.Combine("houseCARL - WbKnown", "extra.txt"), "an extra file was not named: " + string.Join(" | ", d2));

        // one differing refusal, and a refusal against an answer
        var sa = new Arm { Name = "server", Out = a, Hc = "" }; var ib = new Arm { Name = "inproc", Out = b, Hc = "" };
        sa.Answers.Add(("housecarl_apply", false, "error: out_path '" + a + "' is inside the instance"));
        ib.Answers.Add(("housecarl_apply", false, "error: out_path '" + b + "' is inside the game folder"));
        sa.Answers.Add(("housecarl_remove", false, "removed 1 record"));
        ib.Answers.Add(("housecarl_remove", false, "{\"ok\": false, \"error\": \"no such patch\"}"));
        var d3 = Equiv.CompareAnswers(sa, ib);
        Assert.True(d3.Count == 2 && d3[0].StartsWith("call 0 (housecarl_apply) differs") && d3[1].StartsWith("call 1 (housecarl_remove): inproc refused"),
                    "the differing refusals were not named: " + string.Join(" | ", d3));
        // the same refusal in each arm's own folder is not a difference
        ib.Answers[0] = ("housecarl_apply", false, "error: out_path '" + b + "' is inside the instance");
        ib.Answers[1] = ("housecarl_remove", false, "removed 1 record");
        Assert.Empty(Equiv.CompareAnswers(sa, ib));

        // a timing masked both as an answer spells it and as an outcome holding that answer as an escaped JSON string
        var plainArm = new Arm { Name = "inproc", Out = "", Hc = "" };
        Assert.Equal(("{\"render_ms\": 0}", 1), (Equiv.Normalise("{\"render_ms\": 616}", plainArm).Text, Equiv.Normalise("{\"render_ms\": 616}", plainArm).Counts.RenderMs));
        var escaped = JsonSerializer.Serialize("{\"render_ms\": 616, \"x\": 1}");
        var (maskedEscaped, escCounts) = Equiv.Normalise(escaped, plainArm);
        Assert.Equal(1, escCounts.RenderMs);
        Assert.Equal(JsonSerializer.Serialize("{\"render_ms\": 0, \"x\": 1}"), maskedEscaped);
        Assert.Equal(("{\\\"render_ms\\\": 0}", 1), (Equiv.Normalise("{\\\"render_ms\\\": 616}", plainArm).Text, Equiv.Normalise("{\\\"render_ms\\\": 616}", plainArm).Counts.RenderMs));
        Assert.Empty(Equiv.CompareAnswers(
            new Arm { Name = "server", Out = "", Hc = "", Outcomes = { "a: " + JsonSerializer.Serialize("{\"render_ms\": 616}") } },
            new Arm { Name = "inproc", Out = "", Hc = "", Outcomes = { "a: " + JsonSerializer.Serialize("{\"render_ms\": 12}") } }));

        // the server path's block join: a line break exactly at a 1 MiB boundary is taken out, nothing else is
        var whole = new string('a', 1 << 20) + "bcd";
        Assert.Equal((whole, 1), Equiv.UndoBlockJoin(new string('a', 1 << 20) + "\n" + "bcd"));
        var notJoined = new string('a', 1 << 20) + "xbcd";
        Assert.Equal((notJoined, 0), Equiv.UndoBlockJoin(notJoined));
        var shortText = "ab\ncd";
        Assert.Equal((shortText, 0), Equiv.UndoBlockJoin(shortText));
    }

    // ---- the fixtures ----------------------------------------------------------------------------------------------

    /// <summary>PatchWriteTests (both), ReviewSpeedServerTests' placement check, revert, view-only write and winners,
    /// ManualWriteTests H2a/H2b's write and read-back from an Output Folder, TrialPatchWriteTests P8o's out_path write.</summary>
    [Fact]
    public async Task Write_read_back_undo_and_the_placement_checks_are_the_same_both_ways()
    {
        using var w = new TiltedRefWorld();
        var after = Drawn with { Position = new Vec3(700f, -300f, 90f), Rotation = RefPlacementEdit.Turned(TiltedRefWorld.Rot, System.Numerics.Vector3.UnitZ, 0.6f) };
        var stale = new RefPlacementEdit.Pose(new Vec3(1f, 2f, 3f), TiltedRefWorld.Rot, 1f);
        await Both("tilted-write", w.Root, o => new WriteTarget(w.Instance, "Default", w.DataDir, o), async (p, arm) =>
        {
            var folder = Path.Combine(arm.Out, "houseCARL - WbEq");
            await Do(arm, "placement", async () => await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString()));
            await Do(arm, "mismatch as drawn", async () => await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn));
            await Do(arm, "mismatch moved", async () => await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn with { Position = new Vec3(1, 2, 3) }));
            await Do(arm, "mismatch turned", async () => await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn with { Rotation = new Vec3(TiltedRefWorld.Rot.X, TiltedRefWorld.Rot.Y, TiltedRefWorld.Rot.Z + 0.2f) }));
            await Do(arm, "stale commit", async () => await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, stale, stale with { Position = new Vec3(9, 9, 9) }) }, "WbStale", dryRun: true));
            await Do(arm, "dry run", async () => await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbEq", dryRun: true));
            await Do(arm, "write", async () => await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbEq", dryRun: false));
            await Do(arm, "read back by path", async () => await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: Path.Combine(folder, "WbEq.esp")));
            await Do(arm, "read back by name", async () => await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: "WbEq.esp"));
            await Do(arm, "winners", async () => await p.WinnersAsync(new[] { (TiltedRefWorld.Ref, (RefPlacementEdit.Pose?)Drawn) }));
            await Do(arm, "winners drawn elsewhere", async () => await p.WinnersAsync(new[] { (TiltedRefWorld.Ref, (RefPlacementEdit.Pose?)(Drawn with { Position = new Vec3(9, 9, 9) })) }));
            await Do(arm, "second write into", async () => await p.CallAsync("housecarl_apply", With(Move(91f), ("into", folder))));
            await Do(arm, "undo", async () => await p.RevertWrittenAsync(TiltedRefWorld.Ref, folder));
            await Do(arm, "undo again", async () => await p.RevertWrittenAsync(TiltedRefWorld.Ref, folder));
            await Do(arm, "trial write to its own folder", async () => await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbTrial", dryRun: false, outPath: Path.Combine(arm.Out, "trials", "t1")));
        });
    }

    /// <summary>ReviewSpeedServerTests' decision objects: every field of the reference, its base's geometry fields, the
    /// mesh's winner and content hash; refused for a reference drawn elsewhere; a changed position and a changed mesh seen.</summary>
    [Fact]
    public async Task Decision_objects_and_what_changes_them_are_the_same_both_ways()
    {
        using var w = new TiltedRefWorld(withMesh: true);
        var master = Path.Combine(w.Instance, "mods", "WorldMod", TiltedRefWorld.Master.FileName.String);
        var (bytes, stamp) = (File.ReadAllBytes(master), File.GetLastWriteTimeUtc(master));
        var (mesh, meshStamp) = (File.ReadAllBytes(w.MeshFile), File.GetLastWriteTimeUtc(w.MeshFile));
        var moved = new Vec3(TiltedRefWorld.Pos.X + 30, TiltedRefWorld.Pos.Y, TiltedRefWorld.Pos.Z);
        await Both("decision-objects", w.Root, o => new WriteTarget(w.Instance, "Default", w.DataDir, o), async (p, arm) =>
        {
            await Do(arm, "objects", async () => await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) }));
            await Do(arm, "objects drawn elsewhere", async () => await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn with { Position = new Vec3(9, 9, 9) }) }));
            MoveInMaster(w, master, moved);
            File.WriteAllText(w.MeshFile, "nif bytes, version two");
            // the same stamps both ways: houseCARL's epoch hashes each plugin's last-write time
            File.SetLastWriteTimeUtc(master, stamp.AddHours(1)); File.SetLastWriteTimeUtc(w.MeshFile, meshStamp.AddHours(1));
            await Do(arm, "objects after the change", async () => await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn with { Position = moved }) }));
        }, after: _ =>
        {
            File.WriteAllBytes(master, bytes); File.SetLastWriteTimeUtc(master, stamp);
            File.WriteAllBytes(w.MeshFile, mesh); File.SetLastWriteTimeUtc(w.MeshFile, meshStamp);
        });
    }

    static void MoveInMaster(TiltedRefWorld w, string path, Vec3 pos)
    {
        var m = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        foreach (var c in m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells))
            foreach (var r in c.Temporary.OfType<PlacedObject>())
                if (r.FormKey == TiltedRefWorld.Ref) r.Placement = new Placement { Position = new P3Float(pos.X, pos.Y, pos.Z), Rotation = new P3Float(TiltedRefWorld.Rot.X, TiltedRefWorld.Rot.Y, TiltedRefWorld.Rot.Z) };
        var tmp = Path.Combine(w.Root, "rewrite-" + Guid.NewGuid().ToString("N"), Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        m.BeginWrite.ToPath(tmp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>ServerContextSourceTests C1a to C1c: a door near a reference, its far side and the arrival point, what
    /// names a reference (counted past the cap), an enable parent, base types, the placement check.</summary>
    [Fact]
    public async Task The_context_reads_of_a_selected_reference_are_the_same_both_ways()
    {
        using var w = new DoorWorld();
        await Both("context", w.Root, o => new WriteTarget(w.Instance, "Default", w.DataDir, o), async (p, arm) =>
        {
            IContextSource src = new ServerContextSource(Task.FromResult(p), k => k == DoorWorld.House ? new RefPlacementEdit.Pose(new Vec3(9, 9, 9), new Vec3(0, 0, 0), 1f) : null);
            await Do(arm, "doors near", async () => await src.DoorsNearCountedAsync(DoorWorld.House, ContextRead.DoorRadius));
            await Do(arm, "destination", async () => await src.DestinationAsync(DoorWorld.DoorA.ToString()));
            await Do(arm, "names", async () => await src.NamesCountedAsync(DoorWorld.House));
            await Do(arm, "enable parent", async () => await src.EnableParentAsync(DoorWorld.Child));
            await Do(arm, "base type item", async () => await src.BaseTypeAsync(DoorWorld.Item.ToString()));
            await Do(arm, "base type static", async () => await src.BaseTypeAsync(DoorWorld.House.ToString()));
            await Do(arm, "crowd names", async () => await src.NamesCountedAsync(DoorWorld.Crowd));
            await Do(arm, "crowd doors", async () => await src.DoorsNearCountedAsync(DoorWorld.Crowd, ContextRead.DoorRadius));
            await Do(arm, "placement check", async () => await src.MismatchAsync(DoorWorld.House));
        });
    }

    /// <summary>OutputWriteTests B3, B3a, B3b, B3d, B3e, B3f, B3g and B3h: the lanes' own rules on out_path and into=,
    /// raw tool answers.</summary>
    [Fact]
    public async Task The_output_folder_lanes_answer_and_refuse_the_same_both_ways()
    {
        using var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        var mods = Path.Combine(w.Instance, "mods");
        // B3h's fixture: an active plugin that already has the stem a new patch asks for
        var shadow = new ModKey("WbShadow", ModType.Plugin);
        var sdir = Path.Combine(mods, "ShadowMod"); Directory.CreateDirectory(sdir);
        new SkyrimMod(shadow, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(Path.Combine(sdir, shadow.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var prof = Path.Combine(w.Instance, "profiles", "Default");
        File.AppendAllText(Path.Combine(prof, "plugins.txt"), "*" + shadow.FileName + "\r\n");
        File.AppendAllText(Path.Combine(prof, "loadorder.txt"), shadow.FileName + "\r\n");
        File.AppendAllText(Path.Combine(prof, "modlist.txt"), "+ShadowMod\r\n");
        var foreign = Path.Combine(w.Root, "foreign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "Foreign.esp"), "not ours");
        await Both("output-lanes", w.Root, o => new WriteTarget(w.Instance, "Default", w.DataDir, o), async (p, arm) =>
        {
            async Task Call(string what, string tool, Dictionary<string, object?> a) => await Do(arm, what, async () => await p.CallAsync(tool, a));
            await Call("B3 out_path", "housecarl_apply", With(Move(70f), ("patch", "WbOut"), ("out_path", arm.Out)));
            await Call("B3a out_path at the mods folder", "housecarl_apply", With(Move(71f), ("patch", "WbModsOut"), ("out_path", mods)));
            var twice = Path.Combine(arm.Out, "houseCARL - WbTwice");
            await Call("B3b first", "housecarl_apply", With(Move(72f), ("patch", "WbTwice"), ("out_path", arm.Out)));
            await Call("B3b into", "housecarl_apply", With(Move(73f), ("into", twice)));
            await Call("B3b undo", "housecarl_remove", new() { ["formids"] = new[] { TiltedRefWorld.Ref.ToString() }, ["into"] = twice });
            await Call("B3d into a foreign folder", "housecarl_apply", With(Move(74f), ("into", foreign)));
            foreach (var bad in new[] { w.DataDir, Path.Combine(mods, "WorldMod"), w.Instance, Path.Combine(w.Instance, "profiles"), Path.Combine(w.Instance, "NewOut"),
                                        game, Path.Combine(game, "Other"), Path.Combine(w.DataDir, "meshes"), Path.Combine(mods, "NewSub"), Path.Combine(w.Instance, "overwrite"),
                                        Path.Combine(w.Instance, "profiles", "Default") })
                await Call("B3e/B3f/B3g out_path " + Path.GetRelativePath(w.Root, bad), "housecarl_apply", With(Move(75f), ("patch", "WbBad"), ("out_path", bad)));
            await Call("B3h no out_path", "housecarl_apply", With(Move(78f), ("patch", "WbShadow"), ("dry_run", true)));
            await Call("B3h out_path at mods", "housecarl_apply", With(Move(78f), ("patch", "WbShadow"), ("dry_run", true), ("out_path", mods)));
        }, after: arm =>
        {
            // the mods-folder write lands in the instance both ways: gather it with the arm's own files, so the next arm starts the same
            var gathered = Path.Combine(Path.GetDirectoryName(arm.Hc)!, "gathered");
            CopyTree(arm.Out, gathered);
            foreach (var d in Directory.GetDirectories(mods, "houseCARL - *"))
            {
                CopyTree(d, Path.Combine(gathered, "mods", Path.GetFileName(d)));
                Directory.Delete(d, true);
            }
        });
    }

    /// <summary>NavRepairLaneTests C2479: a navmesh repair written under the Output Folder, its carried cell forwarded
    /// from its winner, read back and re-checked by navownprobe (which both arms start).</summary>
    [Fact]
    public async Task A_navmesh_repair_write_forward_read_back_and_re_check_are_the_same_both_ways()
    {
        using var w = new NavRepairWorld();
        const string L5 = "000A05:Skyrim.esm";
        await Both("navrepair", w.Root, o => new WriteTarget(w.Instance, "P", w.DataDir, o), async (p, arm) =>
        {
            var work = Path.Combine(Path.GetDirectoryName(arm.Hc)!, "work");
            var preview = NavRepairLane.Preview(NavRepairWorld.Probe, w.Candidate, "edge-idx|" + L5, "EDGE-IDX", L5, w.Instance, "P", w.Game, Path.Combine(work, "preview"));
            arm.Outcomes.Add("preview: " + preview.Sentence.Replace(work, "<WORK>"));
            await Do(arm, "repair write", async () =>
            {
                var r = await NavRepairLane.WriteAsync(p, preview, "WbNavRepair", NavRepairWorld.Probe, w.Instance, "P", w.Game, Path.Combine(work, "write"));
                return new { r.Patch, r.ReadBackHolds, r.ReadBack, r.Checked, r.Forwarded, r.Verdicts, r.Created, r.Sentence };
            });
        }, allowedPrograms: new[] { "navownprobe.exe", "conhost.exe" });
    }

    /// <summary>An answer far past the server's one-block size (1 MiB): every field of every reference of a crowded cell,
    /// and their winners in chunks.</summary>
    [Fact]
    public async Task An_oversized_records_answer_is_the_same_both_ways()
    {
        using var w = new CrowdWorld(600);
        await Both("oversized", w.Root, o => new WriteTarget(w.Instance, "Default", w.DataDir, o), async (p, arm) =>
        {
            await Do(arm, "every field of every reference", async () =>
            {
                var text = await p.CallAsync("housecarl_records", new()
                {
                    ["formids"] = w.Refs.Select(r => r.ToString()).ToArray(), ["project"] = new Dictionary<string, object?> { ["form"] = "everything", ["depth"] = 2 },
                    ["format"] = "json", ["limit"] = w.Refs.Count + 10, ["max_chars"] = 20_000_000,
                });
                return new { overOneBlock = text.Length > (1 << 20) };
            });
            await Do(arm, "winners", async () => (await p.WinnersAsync(w.Refs.Select(r => (r, (RefPlacementEdit.Pose?)null)).ToList())).Winners.Count);
        });
    }

    static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var d = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            File.Copy(f, d, true);
        }
    }
}

/// <summary>One master whose interior cell holds <c>count</c> placed references of one static, as an MO2 instance.</summary>
public sealed class CrowdWorld : IDisposable
{
    internal static readonly ModKey Master = new("HcWbCrowd", ModType.Master);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-wb-crowd-" + Guid.NewGuid().ToString("N"));
    public string Instance => Path.Combine(Root, "instance");
    public string DataDir => Path.Combine(Root, "game", "Data");
    internal readonly List<FormKey> Refs = new();

    public CrowdWorld(int count)
    {
        var profile = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(DataDir);
        var m = new SkyrimMod(Master, SkyrimRelease.SkyrimSE);
        var stat = new Static(new FormKey(Master, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcWbCrowdStatic" };
        m.Statics.Add(stat);
        var cell = new Cell(new FormKey(Master, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcWbCrowdCell", Flags = Cell.Flag.IsInteriorCell };
        for (int i = 0; i < count; i++)
        {
            var k = new FormKey(Master, 0x900 + (uint)i);
            Refs.Add(k);
            cell.Temporary.Add(new PlacedObject(k, SkyrimRelease.SkyrimSE)
            {
                EditorID = $"HcWbCrowdRef{i:D4}", Base = stat.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(i * 10, 0, 0), Rotation = new P3Float(0, 0, i * 0.01f) },
            });
        }
        var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        var dir = Path.Combine(Instance, "mods", "CrowdMod"); Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, Master.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Master.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+CrowdMod\r\n");
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}
