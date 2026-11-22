using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// navownprobe's repair lanes (task 2026-11-16-navmesh-repair): --repair-plan writes NavRepair's plan for this run's findings
// (the ops for housecarl_apply, and what the read-back holds the written records to, read back by NavRepairRunJson); --recheck-plan, with --base the
// unpatched run's emit, says per planned link what the order with the patch added shows: repaired, cut, refused or not
// repaired. The plan itself is NavRepair's (_shared/NavRepair.cs); this file only gathers its inputs and writes JSON.
static class NavRepairRun
{
    public static JsonObject Write(string path, NavRepairPlan relink, NavRepairPlan ground, IEnumerable<NavBorderLinks.Row> unproven,
                                   Func<FormKey, (string Plugin, string Path)?> winner, EdgeTolerance tol, EdgeTolerance wide, IReadOnlyList<string> order,
                                   NaviPlan navi)
    {
        var plans = new JsonArray();
        foreach (var p in relink.Plans.Concat(ground.Plans).OrderBy(p => p.Nav.ToString(), StringComparer.Ordinal))
        {
            var w = winner(p.Nav);
            plans.Add(new JsonObject
            {
                ["source"] = p.Nav.ToString(), ["class"] = p.Class, ["status"] = p.Status, ["why"] = string.Join("; ", p.Why),
                ["adjacencyChange"] = p.AdjacencyChange,
                ["winner"] = w is { } x ? new JsonObject { ["plugin"] = x.Plugin, ["path"] = x.Path } : null,
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

    static JsonArray Ops(IEnumerable<RepairOp> ops) => new(ops.Select(o =>
    {
        var j = new JsonObject { ["formid"] = o.FormId, ["field_path"] = o.FieldPath, ["op"] = o.Op };
        if (o.Key is not null) j["key"] = o.Key;
        if (o.Value is not null) j["value"] = o.Value;
        if (o.Compose is not null)
            j["compose"] = new JsonObject { ["type"] = "EdgeLink", ["fields"] = new JsonObject(o.Compose.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) };
        if (o.ComposeNode is not null) j["compose"] = o.ComposeNode.DeepClone();
        return (JsonNode)j;
    }).ToArray());

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
