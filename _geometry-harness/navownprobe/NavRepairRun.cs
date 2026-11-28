using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// navownprobe's repair lanes (task 2026-11-16-navmesh-repair): --repair-plan writes NavRepair's plan for this run's findings
// (the ops for housecarl_apply, and what the read-back holds the written records to, read back by NavRepairRunJson); --recheck-plan, with --base the
// unpatched run's emit, says per planned link what the order with the patch added shows: repaired, cut, refused or not
// repaired. The plan itself is NavRepair's (_shared/NavRepair.cs); this file only gathers its inputs and writes JSON.
static class NavRepairRun
{
    public static JsonObject Write(string path, NavRepairPlan relink, NavRepairPlan ground, IEnumerable<NavBorderLinks.Row> unproven,
                                   Func<FormKey, (string Plugin, string Path)?> winner, EdgeTolerance tol, EdgeTolerance wide, IReadOnlyList<string> order,
                                   NaviPlan navi, IReadOnlyList<JsonObject>? useVersions = null)
    {
        var plans = new JsonArray();
        foreach (var u in useVersions ?? Array.Empty<JsonObject>()) plans.Add(u);
        foreach (var p in relink.Plans.Concat(ground.Plans).OrderBy(p => p.Nav.ToString(), StringComparer.Ordinal))
        {
            var w = winner(p.Nav);
            plans.Add(new JsonObject
            {
                ["source"] = p.Nav.ToString(), ["class"] = p.Class, ["status"] = p.Status, ["why"] = string.Join("; ", p.Why),
                ["adjacencyChange"] = p.AdjacencyChange,
                ["winner"] = w is { } x ? new JsonObject { ["plugin"] = x.Plugin, ["path"] = x.Path, ["sha256"] = Sha(x.Path) } : null,
                ["links"] = new JsonArray(p.Links.Select(l => (JsonNode)new JsonObject
                {
                    ["kind"] = l.Kind, ["row"] = l.RowSource.ToString(), ["tri"] = l.SrcTri, ["edge"] = l.Edge, ["target"] = l.Target.ToString(),
                    ["targetTri"] = l.TargetTri, ["status"] = l.Status, ["why"] = l.Why, ["to"] = l.To?.ToString(), ["toTri"] = l.ToTri,
                }).ToArray()),
                ["invariants"] = new JsonArray(p.Invariants.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["holds"] = i.Holds, ["detail"] = i.Detail }).ToArray()),
                ["edits"] = Ops(p.Edits),
                ["change"] = ChangeJson(p.Change),
            });
        }
        var doc = new JsonObject
        {
            ["tolerance"] = new JsonObject { ["plan"] = tol.Plan, ["height"] = tol.Height },
            ["unprovenBand"] = new JsonObject { ["plan"] = wide.Plan, ["height"] = wide.Height },
            ["order"] = new JsonArray(order.Select(o => (JsonNode)o).ToArray()),
            ["plans"] = plans,
            ["unproven"] = new JsonArray(unproven.Select(r => (JsonNode)new JsonObject
            {
                ["kind"] = r.Kind, ["source"] = r.Src.ToString(), ["tri"] = r.SrcTri, ["edge"] = r.Edge, ["target"] = r.Target.ToString(), ["targetTri"] = r.TargetTri,
                ["plan"] = r.Gap.Plan, ["height"] = r.Gap.Height, ["why"] = r.Why,
            }).ToArray()),
            ["navi"] = NaviJson(navi),
            ["ops"] = Ops(relink.Ops.Concat(navi.Ops).Concat(ground.Ops)),
        };
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return doc;
    }

    /// <summary>The winner plugin's bytes as planned: navedit write --repair-plan refuses a plan whose winner changed since.</summary>
    static string? Sha(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    static JsonArray Ops(IEnumerable<RepairOp> ops) => NavRepairRunJson.OpsJson(ops);

    /// <summary>A use_version row (class "use_version"): where the ground plan refused in favour of a mover's re-cut, the plan
    /// of carrying that re-cut (NavUseVersionPlan, navedit's own code). Its "winner" is the plugin whose version is carried
    /// (the patch's source, hashed like any winner), "useVersion" navedit's row object, "edits" its ops and "changes" the
    /// navmeshes it changes. Its ops are not in the plan's top-level "ops": navedit write --repair-plan re-plans the row on
    /// the order and writes its own ops after requiring them equal to these.</summary>
    /// <summary>Why carrying <paramref name="mover"/>'s version of <paramref name="nav"/> cannot share this plan's patch, in one
    /// sentence, or null. It changes <paramref name="changes"/> and was matched against <paramref name="around"/> (the navmeshes
    /// linking into it, those its version links into, and the planned rows pointing a link at it) as they stand before the patch.
    /// Refused: a navmesh it changes or was matched against is a planned relink or ground row's (<paramref name="planned"/>), or
    /// an earlier use_version row changes it (<paramref name="claimed"/>); or a navmesh it changes was an earlier use_version
    /// row's match (<paramref name="matched"/>). Review of slice B, 2026-11-22, finding 1: a neighbour whose link already fits
    /// the version is not re-pointed, so a relink of it planned on the old winner went unchecked.</summary>
    public static string? UseVersionClash(FormKey nav, string mover, IEnumerable<FormKey> changes, IEnumerable<FormKey> around,
                                          ISet<FormKey> planned, IReadOnlyDictionary<FormKey, FormKey> claimed, IReadOnlyDictionary<FormKey, FormKey> matched)
    {
        var ch = changes.ToList();
        foreach (var k in ch.Concat(around))
        {
            if (planned.Contains(k))
                return $"Carrying {mover}'s version of {nav} {(ch.Contains(k) ? "changes" : "is matched against")} {k}, which a relink or ground row in this plan changes too; write them as separate patches.";
            if (claimed.TryGetValue(k, out var by))
                return $"Carrying {mover}'s version of {nav} {(ch.Contains(k) ? "changes" : "is matched against")} {k}, which carrying a version of {by} in this plan changes too; write them as separate patches.";
        }
        foreach (var k in ch)
            if (matched.TryGetValue(k, out var by))
                return $"Carrying {mover}'s version of {nav} changes {k}, against which carrying a version of {by} in this plan was matched; write them as separate patches.";
        return null;
    }

    /// <summary>R10 STUB (Gate B, red first): no row is carried with an earlier set.</summary>
    public static string? CarriedWith(FormKey nav, string mover, IReadOnlyDictionary<FormKey, (FormKey Seed, string Mover)> carried) => null;

    public static JsonObject UseVersionRow(FormKey nav, string mover, string moverPath, string? refused, UseVersionPlan? u)
    {
        bool ok = refused is null && u is { Planned: true };
        return new JsonObject
        {
            ["source"] = nav.ToString(), ["class"] = "use_version", ["status"] = ok ? "planned" : "refused",
            // one sentence: the first reason, then every reason counted by kind (NavUseVersionPlan.ReasonKind), each in "whyAll"
            ["why"] = refused ?? (u is null || u.Why.Count == 0 ? "" : u.Why.Count == 1 ? u.Why[0]
                                  : $"{u.Why[0].TrimEnd('.')}; in all {u.Why.Count} reasons: "
                                    + string.Join(", ", u.Why.GroupBy(NavUseVersionPlan.ReasonKind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                                                         .Select(g => $"{g.Count()} {g.Key}"))
                                    + " (each in whyAll)."),
            ["whyKinds"] = new JsonArray((refused is not null ? Array.Empty<string>() : u?.Why ?? Array.Empty<string>()).Select(w => (JsonNode)NavUseVersionPlan.ReasonKind(w)).ToArray()),
            ["whyAll"] = new JsonArray((refused is not null ? (IReadOnlyList<string>)new[] { refused } : u?.Why ?? Array.Empty<string>()).Select(w => (JsonNode)w).ToArray()),
            ["winner"] = new JsonObject { ["plugin"] = mover, ["path"] = moverPath, ["sha256"] = Sha(moverPath) },
            ["links"] = new JsonArray(),
            // the fields every plan row has (the workbench's repair lane reads them on each row)
            ["adjacencyChange"] = ok && (u!.Row?["gainedNeighbours"] as JsonArray)?.Count > 0,
            ["invariants"] = new JsonArray(),
            ["useVersion"] = u?.Row?.DeepClone(),
            ["edits"] = ok ? Ops(u!.Ops) : new JsonArray(),
            ["changes"] = new JsonArray((ok ? u!.Expect.Keys.Select(k => (JsonNode)k.ToString()) : Array.Empty<JsonNode>()).ToArray()),
        };
    }

    /// <summary>The plan's NAVI change (its one owner is NavRepairNavi.NaviJson).</summary>
    public static JsonObject NaviJson(NaviPlan n) => NavRepairNavi.NaviJson(n);

    static JsonObject V(Vec3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    public static JsonObject ChangeJson(NavChange c) => new()
    {
        ["vertices"] = new JsonArray(c.Vertices.Select(kv => (JsonNode)new JsonObject { ["i"] = kv.Key, ["v"] = V(kv.Value) }).ToArray()),
        ["min"] = c.Min is { } mn ? V(mn) : null, ["max"] = c.Max is { } mx ? V(mx) : null,
        ["setEntries"] = new JsonArray(c.SetEntries.Select(kv => (JsonNode)new JsonObject { ["k"] = kv.Key, ["mesh"] = kv.Value.Mesh.ToString(), ["tri"] = kv.Value.Tri }).ToArray()),
        ["removedEntries"] = new JsonArray(c.RemovedEntries.Select(k => (JsonNode)k).ToArray()),
        ["addedEntries"] = new JsonArray(c.AddedEntries.Select(a => (JsonNode)new JsonObject { ["mesh"] = a.Mesh.ToString(), ["tri"] = a.TriangleIndex, ["type"] = a.Type }).ToArray()),
        ["edgeFields"] = new JsonArray(c.EdgeFields.Select(kv => (JsonNode)new JsonObject { ["tri"] = kv.Key.Tri, ["edge"] = kv.Key.Edge, ["value"] = kv.Value }).ToArray()),
        ["flagBits"] = new JsonArray(c.FlagBits.Select(kv => (JsonNode)new JsonObject { ["tri"] = kv.Key.Tri, ["edge"] = kv.Key.Edge, ["on"] = kv.Value }).ToArray()),
    };

    /// <summary>The re-check: every planned link's verdict on this run (the order with the patch added), from its row's
    /// physical predicate here, and every row this run has that the base emit lacks.</summary>
    public static int Recheck(string planPath, string outPath, Func<string, FormKey, FormKey, int, bool> stillHolds,
                              IReadOnlySet<string> baseIds, IEnumerable<(string Id, string Kind, string Summary)> rowsHere)
    {
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!;
        var links = new JsonArray();
        var tally = new Dictionary<string, int>();
        foreach (var p in plan["plans"]!.AsArray())
        {
            string cls = (string)p!["class"]!;
            var src = FormKey.Factory((string)p["source"]!);
            if (cls == "ground")
            {
                bool h = stillHolds("NAV-GROUND", src, FormKey.Null, -1);
                string v = NavRepair.Verdict((string)p["status"]! == "refused" ? "refused" : "planned", h);
                tally[v] = tally.GetValueOrDefault(v) + 1;
                links.Add(new JsonObject { ["kind"] = "NAV-GROUND", ["row"] = src.ToString(), ["status"] = (string)p["status"]!, ["stillHolds"] = h, ["verdict"] = v });
                continue;
            }
            foreach (var l in p["links"]!.AsArray())
            {
                string kind = (string)l!["kind"]!;
                if (kind == "LINK-BACK") continue;
                var row = FormKey.Factory((string)l["row"]!); var tgt = FormKey.Factory((string)l["target"]!); int tt = (int)l["targetTri"]!;
                bool h = stillHolds(kind, row, tgt, tt);
                string st = (string)p["status"]! == "refused" ? "refused" : (string)l["status"]!;
                string v = NavRepair.Verdict(st, h);
                tally[v] = tally.GetValueOrDefault(v) + 1;
                links.Add(new JsonObject { ["kind"] = kind, ["row"] = row.ToString(), ["target"] = tgt.ToString(), ["targetTri"] = tt, ["tri"] = (int)l["tri"]!,
                                           ["status"] = st, ["stillHolds"] = h, ["verdict"] = v });
            }
        }
        var created = rowsHere.Where(r => !baseIds.Contains(r.Id)).Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["kind"] = r.Kind, ["summary"] = r.Summary }).ToArray();
        var doc = new JsonObject { ["links"] = links, ["created"] = new JsonArray(created),
                                   ["tally"] = new JsonObject(tally.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) };
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"   re-check of {Path.GetFileName(planPath)}: " + string.Join(", ", tally.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))
                          + $"; {created.Length} row(s) not in the base -> {outPath}");
        return 0;
    }
}
