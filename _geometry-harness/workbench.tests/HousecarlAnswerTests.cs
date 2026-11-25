using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The app's one reading of a houseCARL refusal (<see cref="HousecarlAnswer"/>, review F3), on real refusals
/// houseCARL gives: each of the five tools the app calls, in both shapes it refuses in (text starting <c>error:</c>, JSON
/// with <c>"ok": false</c>). A refusal reaches the caller as one sentence keeping houseCARL's own reason; an answer passes
/// unchanged. Rejects a reading that lets a refusal through as an answer.</summary>
[Trait("tier", "integration")]
public sealed class HousecarlAnswerTests : IClassFixture<TiltedRefWorld>
{
    readonly TiltedRefWorld _w;
    public HousecarlAnswerTests(TiltedRefWorld w) => _w = w;

    static Dictionary<string, object?> A(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
    object[] Op() => new[] { new Dictionary<string, object?> { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = "1,2,3" } };

    /// <summary>Each tool's refusal, as houseCARL itself answers it, in text and in JSON.</summary>
    IEnumerable<(string Tool, string Shape, Dictionary<string, object?> Args)> Refusals(string foreign) => new[]
    {
        ("housecarl_apply", "text", A(("ops", Op()), ("patch", "WbNo"), ("out_path", _w.Instance))),
        ("housecarl_apply", "json", A(("ops", Op()), ("into", foreign), ("format", "json"))),
        ("housecarl_remove", "text", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("into", foreign))),
        ("housecarl_remove", "json", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("into", foreign), ("format", "json"))),
        ("housecarl_forward", "text", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("source", "NotInTheOrder.esp"), ("into", foreign))),
        ("housecarl_forward", "json", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("source", "NotInTheOrder.esp"), ("into", foreign), ("format", "json"))),
        ("housecarl_records", "text", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("format", "not-a-format"))),
        ("housecarl_records", "json", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("source", "NotInTheOrder.esp"), ("format", "json"))),
        ("housecarl_asset_status", "text", A(("asset_paths", new[] { @"meshes\hcwb\box.nif" }), ("format", "not-a-format"))),
        ("housecarl_asset_status", "json", A(("asset_paths", new[] { @"meshes\hcwb\box.nif" }), ("format", "json"), ("limit", -1))),
    };

    [Fact]
    public async Task Each_tools_refusal_in_both_shapes_is_one_sentence_with_houseCARLs_reason_and_an_answer_passes()
    {
        var o = Path.Combine(_w.Root, "ans-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(o);
        var foreign = Path.Combine(_w.Root, "foreign-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "Foreign.esp"), "not ours");
        using var lib = HousecarlLibrary.Open(new WriteTarget(_w.Instance, "Default", _w.DataDir, o), Path.Combine(_w.Root, "hc-ans"), TestCorpus.Path);
        var problems = new List<string>();
        foreach (var (tool, shape, args) in Refusals(foreign))
        {
            var raw = await lib.CallAsync(tool, args);
            bool isShape = shape == "text" ? raw.TrimStart().StartsWith("error:") : raw.TrimStart().StartsWith('{') && raw.Contains("\"ok\": false");
            if (!isShape) { problems.Add($"{tool} {shape}: houseCARL's answer is not a refusal of that shape: {raw[..Math.Min(200, raw.Length)]}"); continue; }
            try { HousecarlAnswer.Checked(tool, raw); problems.Add($"{tool} {shape}: a refusal passed as an answer"); }
            catch (InvalidOperationException ex)
            {
                var reason = HousecarlAnswer.Refusal(raw)!;
                if (!ex.Message.StartsWith("houseCARL refused ") || !ex.Message.EndsWith(reason) || ex.Message.Contains('\n'))
                    problems.Add($"{tool} {shape}: not one sentence with houseCARL's reason: {ex.Message}");
                if (reason.Trim('.').Length < 10) problems.Add($"{tool} {shape}: the reason is empty: {ex.Message}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // an answer, in each shape, passes unchanged
        var text = await lib.CallAsync("housecarl_apply", A(("ops", Op()), ("patch", "WbYes"), ("dry_run", true)));
        Assert.Same(text, HousecarlAnswer.Checked("housecarl_apply", text));
        var json = await lib.CallAsync("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("format", "json")));
        Assert.Same(json, HousecarlAnswer.Checked("housecarl_records", json));
    }

    /// <summary>Through the app's writer, a refused undo is refused (it used to read "removed ... (error: ...)"), and
    /// a refused navmesh-repair write stops the repair with houseCARL's reason (it used to go on to the read-back).</summary>
    [Fact]
    public async Task The_writers_callers_get_the_refusal_not_an_answer()
    {
        var o = Path.Combine(_w.Root, "ans2-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(o);
        var foreign = Path.Combine(_w.Root, "foreign2-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(foreign);
        await using var p = PatchWriter.OpenInProcess(new WriteTarget(_w.Instance, "Default", _w.DataDir, o), Path.Combine(_w.Root, "hc-ans2"), TestCorpus.Path, _ => { });
        var undo = await Assert.ThrowsAsync<InvalidOperationException>(() => p.RevertWrittenAsync(TiltedRefWorld.Ref, foreign));
        Assert.StartsWith("houseCARL refused the undo: ", undo.Message);
    }

    /// <summary>The equivalence fixture's "undo again" (Gate B): the second undo of the same written edit is houseCARL's
    /// refusal (the patch no longer carries the reference), never a second "removed". Rejects the old reading, where it
    /// came back as "removed ... (error: refused ...)".</summary>
    [Fact]
    public async Task A_second_undo_of_the_same_edit_is_refused_naming_what_the_patch_no_longer_carries()
    {
        var o = Path.Combine(_w.Root, "ans3-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(o);
        await using var p = PatchWriter.OpenInProcess(new WriteTarget(_w.Instance, "Default", _w.DataDir, o), Path.Combine(_w.Root, "hc-ans3"), TestCorpus.Path, _ => { });
        var drawn = new ViewProbe.RefPlacementEdit.Pose(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
        await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, drawn, drawn with { Position = new HousecarlCore.Vec3(640, -256, 64) }) }, "WbTwiceUndo", dryRun: false);
        var folder = Path.Combine(o, "houseCARL - WbTwiceUndo");
        Assert.StartsWith("removed ", await p.RevertWrittenAsync(TiltedRefWorld.Ref, folder));
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => p.RevertWrittenAsync(TiltedRefWorld.Ref, folder));
        Assert.StartsWith("houseCARL refused the undo: ", again.Message);
        Assert.Contains("not carried by the patch", again.Message);
    }
}
