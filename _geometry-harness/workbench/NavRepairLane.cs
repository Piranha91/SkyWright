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

/// <summary>A written repair: the patch, its read-back, and the re-check's verdict per link.</summary>
internal sealed record RepairWritten(string Patch, bool ReadBackHolds, string ReadBack, IReadOnlyList<(string Row, string Kind, string Verdict)> Verdicts,
                                     IReadOnlyList<string> Created, string Sentence);

/// <summary>THE NAVMESH REPAIR LANE (task 2026-11-16-navmesh-repair): preview, write, read back and re-check a navmesh row's
/// repair. Nothing here decides a repair: navownprobe owns the rows and runs NavRepair's plan (<c>--repair-plan</c>), the
/// houseCARL server writes it (<c>housecarl_apply</c> under the Output Folder), navownprobe reads it back
/// (<c>--repair-readback</c>) and re-checks it by the same rule on the order with the patch added (<c>--recheck-plan</c>).
/// One build of navownprobe makes the preview's run and the re-check's, so the comparison is that build with itself.</summary>
internal static class NavRepairLane
{
    internal static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "EDGE-IDX", "EDGE-NAV", "EDGE-NOMATCH", "EDGE-ONEWAY", "NAV-GROUND" };

    internal static string ProbeExe(string harness) => Environment.GetEnvironmentVariable("WB_NAVOWNPROBE") is { Length: > 0 } e ? e
        : Path.Combine(harness, "navownprobe", "bin", "Release", "net9.0", "navownprobe.exe");

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
        if (p.Ops.Count == 0) throw new InvalidOperationException($"the plan for {p.Navmesh} has no edit to write ({p.Sentence}).");
        if (writer.Target.Output is not { } outDir) throw new InvalidOperationException("no Output Folder is chosen, so the repair has nowhere to go; choose one in the launcher.");
        var plugin = WorkbenchHost.PluginName(patchName);
        var ops = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(p.Ops.ToJsonString())!;
        await writer.CallAsync("housecarl_apply", new Dictionary<string, object?> { ["ops"] = ops, ["patch"] = Path.GetFileNameWithoutExtension(plugin), ["out_path"] = outDir }, ct);
        var folder = Recheck.PatchFolder(writer.Target.PatchRoot, plugin);
        var esp = Path.Combine(folder, plugin);
        Directory.CreateDirectory(work);
        var rbPath = Path.Combine(work, "readback.json");
        var (rbExit, rbText) = Probe(probe, new[] { "--repair-readback", p.Plan, esp, rbPath }, Path.Combine(work, "readback.log"));
        if (rbExit == 2 || !File.Exists(rbPath)) throw new InvalidOperationException($"the read-back could not run (exit {rbExit}): {Last(rbText)}");
        var rb = JsonNode.Parse(File.ReadAllText(rbPath))!;
        bool holds = rbExit == 0 && (bool)rb["holds"]!;
        string rbSentence = holds ? "every navmesh in the patch is its winner with exactly the planned change"
            : "the read-back FAILS: " + string.Join("; ", rb["records"]!.AsArray().Where(r => !(bool)r!["holds"]!).Select(r => $"{(string)r!["navmesh"]!}: {string.Join(", ", r["differences"]!.AsArray().Select(d => (string)d!))}"))
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
        return new RepairWritten(esp, holds, rbSentence, verdicts, created, sentence);
    }

    static string Last(string text) => string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
}
