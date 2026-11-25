using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workbench;

/// <summary>One planned navmesh as the preview shows it.</summary>
internal sealed record RepairEntry(string Navmesh, string Class, string Status, string Why, bool AdjacencyChange, int Edits,
                                   IReadOnlyList<string> Invariants);

/// <summary>A navmesh row's repair, previewed: navownprobe's plan for the row's navmesh on the arm's order (NavRepair.cs),
/// the links unproven in game near it, and the ops a write would send.</summary>
internal sealed record RepairPreview(string RowId, string Navmesh, string Plan, string UnpatchedEmit, string Candidate,
                                     IReadOnlyList<RepairEntry> Plans, IReadOnlyList<string> Unproven, JsonArray Ops, string Sentence);

/// <summary>A written repair: the patch, its read-back (each record it checked, navmeshes and carried containers, and the
/// containers forwarded from their winners), and the re-check's verdict per link.</summary>
internal sealed record RepairWritten(string Patch, bool ReadBackHolds, string ReadBack, IReadOnlyList<(string Record, bool Holds)> Checked,
                                     IReadOnlyList<string> Forwarded, IReadOnlyList<(string Row, string Kind, string Verdict)> Verdicts,
                                     IReadOnlyList<string> Created, string Sentence);

/// <summary>THE NAVMESH REPAIR LANE (task 2026-11-16-navmesh-repair): preview, write, read back and re-check a navmesh
/// Findings row's repair (<see cref="RepairDesk"/> holds the app's). Nothing here decides a repair: navownprobe owns the rows and runs NavRepair's plan (<c>--repair-plan</c>), the
/// houseCARL server writes it (<c>housecarl_apply</c> under the Output Folder), navownprobe reads it back
/// (<c>--repair-readback</c>), the server forwards each carried container the read-back finds is not its winner's from that
/// winner (<c>housecarl_forward</c> into the patch) and navownprobe reads it back again, then re-checks it by the same rule
/// on the order with the patch added (<c>--recheck-plan</c>).
/// One build of navownprobe makes the preview's run and the re-check's, so the comparison is that build with itself.</summary>
internal static class NavRepairLane
{
    internal static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "EDGE-IDX", "EDGE-NAV", "EDGE-NOMATCH", "EDGE-ONEWAY", "NAV-GROUND" };

    internal static string ProbeExe(string harness) => Environment.GetEnvironmentVariable("WB_NAVOWNPROBE") is { Length: > 0 } e ? e
        : HarnessShared.CheckoutLayout.ProgramExe(harness, "navownprobe");

    static (int Exit, string Text) Probe(string exe, IEnumerable<string> args, string log)
    {
        if (!File.Exists(exe)) throw new InvalidOperationException($"navownprobe is not built at '{exe}'; build _geometry-harness/navownprobe in Release first.");
        return Recheck.Run(exe, args, log);
    }

    static string[] Common(string order, string instance, string profile, string game) => new[] { "--order", order, "--instance", instance, "--profile", profile, "--game", game };

    /// <summary>Plan the repair of the row whose navmesh is <paramref name="navmesh"/> on the arm's order.</summary>
    internal static RepairPreview Preview(string probe, string candidate, string rowId, string kind, string navmesh,
                                          string instance, string profile, string game, string work)
    {
        if (!Kinds.Contains(kind)) throw new InvalidOperationException($"a {kind} row has no navmesh repair; only border links (EDGE-IDX, EDGE-NAV, EDGE-NOMATCH, EDGE-ONEWAY) and moved ground (NAV-GROUND) do.");
        Directory.CreateDirectory(work);
        var c = JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
        c["BaseProfile"] = profile;
        var order = Path.Combine(work, "order-unpatched.json");
        File.WriteAllText(order, c.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string emit = Path.Combine(work, "nr-unpatched.json"), plan = Path.Combine(work, "plan.json");
        var (exit, text) = Probe(probe, Common(order, instance, profile, game).Concat(new[] { "--emit", emit, "--repair-plan", plan, "--repair-scope", navmesh }),
                                 Path.Combine(work, "preview.log"));
        if (exit != 0 || !File.Exists(plan))
            throw new InvalidOperationException($"navownprobe could not plan the repair (exit {exit}); its log is {Path.Combine(work, "preview.log")}: {Last(text)}");
        var doc = JsonNode.Parse(File.ReadAllText(plan))!;
        var entries = doc["plans"]!.AsArray().Select(p => new RepairEntry((string)p!["source"]!, (string)p["class"]!, (string)p["status"]!, (string)p["why"]!,
            (bool)p["adjacencyChange"]!, p["edits"]!.AsArray().Count,
            p["invariants"]!.AsArray().Select(i => $"{((bool)i!["holds"]! ? "holds" : "FAILS")}: {(string)i["name"]!} ({(string)i["detail"]!})").ToList())).ToList();
        var unproven = doc["unproven"]!.AsArray().Where(u => string.Equals((string)u!["source"]!, navmesh, StringComparison.OrdinalIgnoreCase) || string.Equals((string)u["target"]!, navmesh, StringComparison.OrdinalIgnoreCase))
            .Select(u => $"{(string)u!["source"]!} triangle {(int)u["tri"]!}: {(string)u["why"]!}").ToList();
        var ops = doc["ops"]!.AsArray();
        string sentence = entries.Count == 0
            ? $"no repair is planned for {navmesh}: its row is not one the plan takes (a floor row, or unproven in game)" + (unproven.Count > 0 ? $"; {unproven.Count} of its links are unproven in game and offered no repair" : "")
            : $"the repair of {navmesh} touches {entries.Count(e => e.Status != "refused")} navmesh(es) with {ops.Count} edit(s): "
              + string.Join("; ", entries.Select(e => $"{e.Navmesh} {e.Status}" + (e.AdjacencyChange ? " (the navmeshes it borders change)" : "") + $": {e.Why}"))
              + (unproven.Count > 0 ? $"; {unproven.Count} link(s) unproven in game, offered no repair" : "");
        return new RepairPreview(rowId, navmesh, plan, emit, candidate, entries, unproven, ops, sentence);
    }

    /// <summary>Write the previewed repair into the new patch <paramref name="patchName"/> under the writer's Output Folder,
    /// read it back, and re-check it on the order with the patch added.</summary>
    internal static async Task<RepairWritten> WriteAsync(PatchWriter writer, RepairPreview p, string patchName, string probe,
                                                         string instance, string profile, string game, string work, CancellationToken ct = default)
    {
        // a use_version row (another plugin's version of a navmesh carried over its winner) is written and read back only by
        // navedit write --repair-plan, which re-plans it; this lane writes the plan's top-level ops alone, so it would drop it
        if (JsonNode.Parse(File.ReadAllText(p.Plan))!["plans"]!.AsArray().FirstOrDefault(x => (string?)x!["class"] == "use_version" && (string?)x["status"] != "refused") is { } uv)
            throw new InvalidOperationException($"the plan for {p.Navmesh} carries {(string)uv["winner"]!["plugin"]!}'s version of {(string)uv["source"]!}, which only navedit write --repair-plan writes and reads back; write the plan there.");
        if (p.Ops.Count == 0) throw new InvalidOperationException($"the plan for {p.Navmesh} has no edit to write ({p.Sentence}).");
        if (writer.Target.Output is not { } outDir) throw new InvalidOperationException("no Output Folder is chosen, so the repair has nowhere to go; choose one in the launcher.");
        var plugin = WorkbenchHost.PluginName(patchName);
        var ops = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(p.Ops.ToJsonString())!;
        await writer.CallAsync("housecarl_apply", new Dictionary<string, object?> { ["ops"] = ops, ["patch"] = Path.GetFileNameWithoutExtension(plugin), ["out_path"] = outDir }, ct);
        var folder = Path.GetFullPath(Recheck.PatchFolder(writer.Target.PatchRoot, plugin));
        var esp = Path.Combine(folder, plugin);
        Directory.CreateDirectory(work);
        (int Exit, JsonNode Doc) ReadBack(string tag)
        {
            var path = Path.Combine(work, $"readback{tag}.json");
            var (exit, text) = Probe(probe, new[] { "--repair-readback", p.Plan, esp, path }, Path.Combine(work, $"readback{tag}.log"));
            if (exit == 2 || !File.Exists(path)) throw new InvalidOperationException($"the read-back could not run (exit {exit}): {Last(text)}");
            return (exit, JsonNode.Parse(File.ReadAllText(path))!);
        }
        var (rbExit, rb) = ReadBack("-applied");
        // the write path copies a navmesh's CELL and WRLD from the navmesh's own plugin; each the read-back finds is not its
        // winner's is forwarded from that winner into the patch (the navmeshes under it kept), then read back again
        var forwarded = new List<string>();
        var navHolds = rb["records"]!.AsArray().All(r => (bool)r!["holds"]!) && rb["unplannedNavmeshes"]!.AsArray().Count == 0;
        if (rbExit != 0 && navHolds && rb["carriedFixes"]!.AsArray().Count > 0)
        {
            foreach (var f in rb["carriedFixes"]!.AsArray())
            {
                var ids = f!["formids"]!.AsArray().Select(x => (string)x!).ToList();
                await writer.CallAsync("housecarl_forward", new Dictionary<string, object?> { ["formids"] = ids, ["source"] = (string)f["source"]!, ["into"] = folder }, ct);
                forwarded.AddRange(ids.Select(i => $"{i} from {(string)f["source"]!}"));
            }
            (rbExit, rb) = ReadBack("");
        }
        bool holds = rbExit == 0 && (bool)rb["holds"]!;
        var carried = rb["carried"]!.AsArray();
        var checkedRecords = rb["records"]!.AsArray().Select(r => ((string)r!["navmesh"]!, (bool)r["holds"]!))
            .Concat(carried.Select(c => ($"{(string)c!["type"]!} {(string)c["record"]!}", (bool)c["holds"]!))).ToList();
        string rbSentence = holds ? $"every navmesh in the patch is its winner with exactly the planned change, and the {carried.Count} record(s) it carries are their winners'"
                                    + (forwarded.Count > 0 ? $" ({forwarded.Count} forwarded from their winner: {string.Join(", ", forwarded)})" : "")
            : "the read-back FAILS: " + string.Join("; ", rb["records"]!.AsArray().Where(r => !(bool)r!["holds"]!).Select(r => $"{(string)r!["navmesh"]!}: {string.Join(", ", r["differences"]!.AsArray().Select(d => (string)d!))}")
                                                 .Concat(carried.Where(c => !(bool)c!["holds"]!).Select(c => $"carried {(string)c!["type"]!} {(string)c["record"]!} is not its winner {(string?)c["winner"]}'s: {string.Join(", ", c["differences"]!.AsArray().Select(d => (string)d!))}")))
              + (rb["unplannedNavmeshes"]!.AsArray().Count > 0 ? "; navmeshes in the patch the plan does not name: " + string.Join(", ", rb["unplannedNavmeshes"]!.AsArray().Select(x => (string)x!)) : "");
        var (_, patched) = Recheck.Orders(p.Candidate, profile, plugin, folder, work);
        string emit = Path.Combine(work, "nr-patched.json"), rc = Path.Combine(work, "recheck.json");
        var (exit, text) = Probe(probe, Common(patched, instance, profile, game).Concat(new[] { "--emit", emit, "--base", p.UnpatchedEmit, "--recheck-plan", p.Plan, "--recheck-out", rc }),
                                 Path.Combine(work, "recheck.log"));
        if (exit != 0 || !File.Exists(rc)) throw new InvalidOperationException($"the re-check could not run (exit {exit}); its log is {Path.Combine(work, "recheck.log")}: {Last(text)}");
        var r = JsonNode.Parse(File.ReadAllText(rc))!;
        var verdicts = r["links"]!.AsArray().Select(l => ((string)l!["row"]!, (string)l["kind"]!, (string)l["verdict"]!)).ToList();
        var created = r["created"]!.AsArray().Select(c => $"{(string)c!["kind"]!} {(string)c["summary"]!}").ToList();
        string sentence = $"written to {esp}; {rbSentence}; re-check: "
            + string.Join(", ", verdicts.GroupBy(v => v.Item3).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
            + (created.Count == 0 ? "; no row is new with the patch" : $"; {created.Count} row(s) are new with the patch: " + string.Join("; ", created.Take(5)))
            + ". Nothing is enabled: the patch loads only when you enable it in Mod Organizer 2.";
        return new RepairWritten(esp, holds, rbSentence, checkedRecords, forwarded, verdicts, created, sentence);
    }

    /// <summary>The scene's last preview and write: a write sends exactly the open row's last preview.</summary>
    internal sealed class State
    {
        internal RepairPreview? Preview { get; private set; }
        internal RepairWritten? Written { get; private set; }
        internal RepairPreview Previewed(RepairPreview p) { Preview = p; Written = null; return p; }
        internal RepairPreview ForWrite(string rowId) => Preview is { } lp && lp.RowId == rowId ? lp
            : throw new InvalidOperationException("preview the open row's repair first; the write sends exactly what the preview showed.");
        internal RepairWritten Wrote(RepairWritten w) { Written = w; return w; }
    }

    static string Last(string text) => string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
}

/// <summary>A navmesh Findings row's repair (the window's Findings tab buttons, the state tools' repair_preview and
/// repair_write): a navmesh row names a navmesh, not a placed reference, so it opens no viewer and needs no scene. The
/// inputs (the arm's candidate order, the write target, navownprobe, the work folder) and the houseCARL writer come from
/// the caller: the app's own in <see cref="Shell"/> (<see cref="HousecarlWriters"/>), a fixture's in the tests. The
/// writer is the one of the target the inputs name at each write.</summary>
internal sealed class RepairDesk(Func<(string Candidate, WriteTarget Target, string Probe, string Work)> inputs, Func<WriteTarget, Task<PatchWriter>> startWriter)
{
    readonly NavRepairLane.State _state = new();
    PatchWriter? _writer;
    internal RepairPreview? Preview => _state.Preview;
    internal RepairWritten? Written => _state.Written;

    static void Check(RankedFinding f)
    {
        if (f.Row.RefKey is null || !NavRepairLane.Kinds.Contains(f.Row.Kind))
            throw new InvalidOperationException($"finding {f.Rank} ({f.Row.CheckId}, {f.Row.Kind}) is not a navmesh row; a repair is planned only for an EDGE-IDX, EDGE-NAV, EDGE-NOMATCH, EDGE-ONEWAY or NAV-GROUND row.");
    }

    internal async Task<RepairPreview> PreviewAsync(RankedFinding f)
    {
        Check(f);
        var (cand, t, probe, work) = inputs();
        var dir = Path.Combine(work, "repair-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff"));
        return _state.Previewed(await Task.Run(() => NavRepairLane.Preview(probe, cand, f.Row.Identity, f.Row.Kind, f.Row.RefKey!, t.Instance, t.Profile, t.Game, dir)));
    }

    internal async Task<RepairWritten> WriteAsync(RankedFinding f, string patch)
    {
        Check(f);
        var p = _state.ForWrite(f.Row.Identity);
        var (_, t, probe, work) = inputs();
        // the writer of the target the inputs name now: a desk never keeps the writer of a profile it was given before
        if (_writer is null || _writer.Target != t)
        {
            var old = _writer;
            _writer = await startWriter(t);
            if (old is not null && old != _writer && !HousecarlWriters.Owns(old)) await Task.Run(async () => await old.DisposeAsync());
        }
        var dir = Path.Combine(work, "repair-write-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff"));
        return _state.Wrote(await NavRepairLane.WriteAsync(_writer, p, patch, probe, t.Instance, t.Profile, t.Game, dir));
    }

    /// <summary>Closes a writer the desk's own factory made; one from <see cref="HousecarlWriters"/> is that owner's to close.</summary>
    internal async ValueTask DisposeWriterAsync() { if (_writer is not null && !HousecarlWriters.Owns(_writer)) await Task.Run(async () => await _writer.DisposeAsync()); }
}
