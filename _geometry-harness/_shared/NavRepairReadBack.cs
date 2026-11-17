using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace HarnessShared;

// THE READ-BACK OF A WRITTEN NAVMESH REPAIR (Gate A review finding 4, 2026-11-16). For every navmesh the plan changes, the
// written record must equal the winner's version with exactly the plan's change applied: the winner is copied through
// Mutagen, the plan's change (NavChange, the plan's own statement of what it changes, not the ops sent to the writer) is
// applied to the copy here, and every field of the copy is compared with the written record: the record's header fields
// and every NAVM field Mutagen models (vertices, triangles with their edge fields, flags and cover data, the edge-link
// table, door triangles, the box, the grid divisor, section lengths and grid bytes, the hash, the parent and the version),
// and then Mutagen's own whole-record equality. Mutagen re-writes an unchanged NVNM byte for byte (Gate B's round trip, 3 of
// 3 records), so a field it models is the NVNM's bytes. Every NAVM in the patch must be a planned one.
public static class NavRepairReadBack
{
    /// <summary>The differences between <paramref name="written"/> and the winner with <paramref name="change"/> applied;
    /// empty when the written record is exactly that.</summary>
    public static List<string> Differences(INavigationMeshGetter winner, INavigationMeshGetter written, NavChange change)
    {
        var want = winner.DeepCopy();
        if (want.Data is { } d) Apply(d, change);
        var got = written.DeepCopy();
        var diff = new List<string>();
        void Eq<T>(string what, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) diff.Add($"{what}: written {b}, planned {a}"); }
        Eq("FormKey", want.FormKey, got.FormKey);
        Eq("EditorID", want.EditorID, got.EditorID);
        Eq("record flags", want.MajorRecordFlagsRaw, got.MajorRecordFlagsRaw);
        Eq("form version", want.FormVersion, got.FormVersion);
        Eq("version control", want.VersionControl, got.VersionControl);
        Eq("version 2", want.Version2, got.Version2);
        if (want.Data is null || got.Data is null) { Eq("has data", want.Data is not null, got.Data is not null); return diff; }
        var a = want.Data; var b = got.Data;
        Eq("NavmeshVersion", a.NavmeshVersion, b.NavmeshVersion);
        Eq("CrcHash", a.CrcHash, b.CrcHash);
        Eq("Min", a.Min, b.Min);
        Eq("Max", a.Max, b.Max);
        Eq("NavmeshGridDivisor", a.NavmeshGridDivisor, b.NavmeshGridDivisor);
        Eq("MaxDistanceX", a.MaxDistanceX, b.MaxDistanceX);
        Eq("MaxDistanceY", a.MaxDistanceY, b.MaxDistanceY);
        if (!a.NavmeshGrid.Span.SequenceEqual(b.NavmeshGrid.Span)) diff.Add($"NavmeshGrid: {b.NavmeshGrid.Length} bytes written differ from the planned {a.NavmeshGrid.Length}");
        if (!Equals(a.Parent, b.Parent)) diff.Add("Parent differs");
        Eq("vertex count", a.Vertices.Count, b.Vertices.Count);
        for (int i = 0; i < Math.Min(a.Vertices.Count, b.Vertices.Count); i++) Eq($"Vertices[{i}]", a.Vertices[i], b.Vertices[i]);
        Eq("triangle count", a.Triangles.Count, b.Triangles.Count);
        for (int i = 0; i < Math.Min(a.Triangles.Count, b.Triangles.Count); i++)
        {
            var x = a.Triangles[i]; var y = b.Triangles[i];
            Eq($"Triangles[{i}].Vertices", x.Vertices, y.Vertices);
            Eq($"Triangles[{i}].EdgeLink_0_1", x.EdgeLink_0_1, y.EdgeLink_0_1);
            Eq($"Triangles[{i}].EdgeLink_1_2", x.EdgeLink_1_2, y.EdgeLink_1_2);
            Eq($"Triangles[{i}].EdgeLink_2_0", x.EdgeLink_2_0, y.EdgeLink_2_0);
            Eq($"Triangles[{i}].Flags", x.Flags, y.Flags);
            Eq($"Triangles[{i}].CoverFlags", x.CoverFlags, y.CoverFlags);
            Eq($"Triangles[{i}].IsCover", x.IsCover, y.IsCover);
        }
        Eq("edge-link count", a.EdgeLinks.Count, b.EdgeLinks.Count);
        for (int i = 0; i < Math.Min(a.EdgeLinks.Count, b.EdgeLinks.Count); i++)
        {
            Eq($"EdgeLinks[{i}].Mesh", a.EdgeLinks[i].Mesh.FormKey, b.EdgeLinks[i].Mesh.FormKey);
            Eq($"EdgeLinks[{i}].TriangleIndex", a.EdgeLinks[i].TriangleIndex, b.EdgeLinks[i].TriangleIndex);
            Eq($"EdgeLinks[{i}].Unknown", a.EdgeLinks[i].Unknown, b.EdgeLinks[i].Unknown);
        }
        Eq("door-triangle count", a.DoorTriangles.Count, b.DoorTriangles.Count);
        for (int i = 0; i < Math.Min(a.DoorTriangles.Count, b.DoorTriangles.Count); i++)
            if (!a.DoorTriangles[i].Equals(b.DoorTriangles[i])) diff.Add($"DoorTriangles[{i}] differs");
        if (diff.Count == 0 && !want.Equals(got)) diff.Add("the records differ in a field outside the named set (Mutagen's whole-record equality)");
        return diff;
    }

    /// <summary>The plan's change applied to a Mutagen navmesh: vertices, box, table entries re-pointed, removed and
    /// appended, edge fields and flag bits.</summary>
    public static void Apply(NavigationMeshData d, NavChange c)
    {
        foreach (var (i, v) in c.Vertices) d.Vertices[i] = new P3Float(v.X, v.Y, v.Z);
        if (c.Min is { } mn) d.Min = new P3Float(mn.X, mn.Y, mn.Z);
        if (c.Max is { } mx) d.Max = new P3Float(mx.X, mx.Y, mx.Z);
        foreach (var (k, (m, t)) in c.SetEntries) { d.EdgeLinks[k].Mesh = new FormLink<INavigationMeshGetter>(m); d.EdgeLinks[k].TriangleIndex = t; }
        foreach (var k in c.RemovedEntries.Reverse()) d.EdgeLinks.RemoveAt(k);
        foreach (var e in c.AddedEntries) d.EdgeLinks.Add(new EdgeLink { Unknown = e.Type, Mesh = new FormLink<INavigationMeshGetter>(e.Mesh), TriangleIndex = e.TriangleIndex });
        foreach (var ((t, e), f) in c.EdgeFields)
        {
            var tr = d.Triangles[t];
            if (e == 0) tr.EdgeLink_0_1 = f; else if (e == 1) tr.EdgeLink_1_2 = f; else tr.EdgeLink_2_0 = f;
        }
        foreach (var ((t, e), on) in c.FlagBits)
        {
            var bit = (NavmeshTriangle.Flag)(1 << e);
            d.Triangles[t].Flags = on ? d.Triangles[t].Flags | bit : d.Triangles[t].Flags & ~bit;
        }
    }

    // A container's child groups and their group-header stamps: the records under it, not its own fields (each child the
    // patch carries is compared on its own).
    static readonly HashSet<string> ChildGroups = new(StringComparer.Ordinal)
    {
        "Persistent", "Temporary", "NavigationMeshes", "Landscape", "PersistentTimestamp", "PersistentUnknownGroupData",
        "TemporaryTimestamp", "TemporaryUnknownGroupData", "Timestamp", "UnknownGroupData",
        "SubCells", "SubCellsTimestamp", "SubCellsUnknown", "TopCell",
    };

    /// <summary>The fields of a carried record (a CELL or WRLD the write path copies as a navmesh's container) that differ
    /// between the patch's copy and the load-order winner's: every property of the record's getter interfaces except its
    /// child groups, by value (links by FormKey, byte fields by bytes, lists element by element). Also returns the names
    /// compared.</summary>
    public static (List<string> Diff, List<string> Compared) ContainerDifferences(IMajorRecordGetter winner, IMajorRecordGetter written)
    {
        var diff = new List<string>(); var compared = new List<string>();
        var iface = winner.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "I" + RecordNaming.StripOverlay(winner.GetType().Name) + "Getter");
        if (iface is null || !iface.IsInstanceOfType(written)) { diff.Add($"the written record is not a {RecordNaming.StripOverlay(winner.GetType().Name)}"); return (diff, compared); }
        var props = iface.GetInterfaces().Append(iface)
            .Where(i => i.Namespace?.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal) == true)
            .SelectMany(i => i.GetProperties()).Where(p => p.GetIndexParameters().Length == 0 && !ChildGroups.Contains(p.Name))
            .GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        foreach (var p in props)
        {
            if (p.PropertyType.Namespace?.StartsWith("Loqui", StringComparison.Ordinal) == true) continue;   // registration metadata
            compared.Add(p.Name);
            object? a, b;
            try { a = p.GetValue(winner); b = p.GetValue(written); }
            catch (Exception ex) { diff.Add($"{p.Name}: could not be read ({(ex.InnerException ?? ex).Message})"); continue; }
            if (!Same(a, b)) diff.Add($"{p.Name}: written {Show(b)}, winner {Show(a)}");
        }
        return (diff, compared);
    }

    static bool Same(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is IFormLinkGetter fa && b is IFormLinkGetter fb) return fa.FormKey == fb.FormKey;
        if (a is ReadOnlyMemorySlice<byte> ma && b is ReadOnlyMemorySlice<byte> mb) return ma.Span.SequenceEqual(mb.Span);
        if (a is string sa && b is string sb) return sa == sb;
        if (a is ITranslatedStringGetter ta && b is ITranslatedStringGetter tb) return ta.String == tb.String;   // the text, however stored
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(x => Same(x.First, x.Second));
        }
        return a.Equals(b);
    }

    static string Show(object? v) => v switch
    {
        null => "none",
        IFormLinkGetter f => f.FormKey.ToString(),
        ReadOnlyMemorySlice<byte> m => $"{m.Length} bytes",
        string s => $"'{s}'",
        ITranslatedStringGetter t => $"'{t.String}'",
        System.Collections.IEnumerable e => $"{e.Cast<object?>().Count()} entries",
        _ => v.ToString() ?? "?",
    };

    /// <summary>Every record in the patch that is not a navmesh (the containers the write path carried) against its winner on
    /// the plan's order. Each entry: the record, its type, its winner, holds, the differences and the fields compared. The
    /// fixes are what the write path must forward to make each failing one the winner's: grouped by winner plugin, the
    /// worldspaces first.</summary>
    public static (JsonArray Records, JsonArray Fixes, int Failed) Carried(ISkyrimModGetter patch, IReadOnlyList<string> order)
    {
        var records = new JsonArray(); var fix = new List<(string Plugin, string Type, FormKey Key)>(); int failed = 0;
        var carried = patch.EnumerateMajorRecords().Where(r => r is not INavigationMeshGetter).ToList();
        if (carried.Count == 0) return (records, new JsonArray(), 0);
        using var resolver = LoadOrderResolver.Build(order);
        var view = resolver.Capture();
        using var session = view.OpenSession();
        foreach (var r in carried)
        {
            string type = RecordNaming.StripOverlay(r.GetType().Name);
            var w = view.ResolveWinner(r.FormKey);
            List<string> diff; List<string> compared = new();
            if (w is null) diff = new() { $"{r.FormKey} is in no plugin of the order, so the patch's copy has no winner to equal" };
            else if (view.GetRecord(session, w.Value.WinnerPlugin, r.FormKey, r.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "I" + type + "Getter")) is not { } body)
                diff = new() { $"the winner {w.Value.WinnerPlugin} did not yield {r.FormKey}" };
            else (diff, compared) = ContainerDifferences(body, r);
            if (diff.Count > 0) { failed++; if (w is not null) fix.Add((w.Value.WinnerPlugin, type, r.FormKey)); }
            records.Add(new JsonObject
            {
                ["record"] = r.FormKey.ToString(), ["type"] = type, ["winner"] = w?.WinnerPlugin, ["holds"] = diff.Count == 0,
                ["differences"] = new JsonArray(diff.Select(x => (JsonNode)x).ToArray()), ["compared"] = compared.Count,
            });
        }
        var fixes = new JsonArray(fix.GroupBy(f => f.Plugin, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Any(f => f.Type == "Worldspace") ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (JsonNode)new JsonObject
            {
                ["source"] = g.Key,
                ["formids"] = new JsonArray(g.OrderBy(f => f.Type == "Worldspace" ? 0 : 1).Select(f => (JsonNode)FormIdToken.Of(f.Key)).ToArray()),
            }).ToArray());
        return (records, fixes, failed);
    }

    /// <summary><c>navownprobe --repair-readback PLAN.json PATCH.esp OUT.json</c>: every planned navmesh in the patch against
    /// its winner with the plan applied, every NAVM in the patch a planned one, and every other record the patch carries (the
    /// CELL and WRLD containers) equal to its winner on the plan's order. Exit 0 when every check holds, 1 when one fails (each
    /// said in one sentence; a failing container names the forward that makes it the winner's), 2 when the inputs cannot be
    /// read.</summary>
    public static int Run(string planPath, string patchPath, string outPath)
    {
        JsonNode plan;
        try { plan = JsonNode.Parse(File.ReadAllText(planPath))!; }
        catch (Exception ex) { Console.Error.WriteLine($"the plan '{planPath}' could not be read ({ex.Message}); write it with navownprobe --repair-plan."); return 2; }
        if (!File.Exists(patchPath)) { Console.Error.WriteLine($"the patch '{patchPath}' does not exist; write the plan's ops with housecarl_apply first."); return 2; }
        using var patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        var written = patch.EnumerateMajorRecords<INavigationMeshGetter>().ToDictionary(n => n.FormKey);
        var results = new JsonArray(); int failed = 0;
        var planned = new HashSet<FormKey>();
        var opened = new Dictionary<string, ISkyrimModDisposableGetter>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in plan["plans"]!.AsArray())
            {
                if ((string)p!["status"]! == "refused") continue;
                var key = FormKey.Factory((string)p["source"]!);
                planned.Add(key);
                string? path = (string?)p["winner"]?["path"];
                List<string> diff;
                if (path is null || !File.Exists(path)) diff = new() { $"the winner's plugin '{path}' is not on disk, so {key} cannot be compared" };
                else
                {
                    if (!opened.TryGetValue(path, out var mod)) opened[path] = mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    var win = mod.EnumerateMajorRecords<INavigationMeshGetter>().FirstOrDefault(n => n.FormKey == key);
                    diff = win is null ? new() { $"{key} is not in its winner {Path.GetFileName(path)}" }
                         : !written.TryGetValue(key, out var got) ? new() { $"{key} is planned but not in the patch" }
                         : Differences(win, got, NavRepairRunJson.ChangeOf(p["change"]!));
                }
                if (diff.Count > 0) failed++;
                results.Add(new JsonObject { ["navmesh"] = key.ToString(), ["holds"] = diff.Count == 0, ["differences"] = new JsonArray(diff.Select(x => (JsonNode)x).ToArray()) });
            }
        }
        finally { foreach (var m in opened.Values) m.Dispose(); }
        var extra = written.Keys.Where(k => !planned.Contains(k)).ToList();
        failed += extra.Count;
        var order = plan["order"]?.AsArray().Select(x => (string)x!).ToList() ?? new List<string>();
        JsonArray carried, fixes; int carriedFailed;
        if (order.Count == 0) { carried = new JsonArray(); fixes = new JsonArray(); carriedFailed = 0; Console.Error.WriteLine("the plan names no order, so the carried records cannot be held to their winners; re-write it with navownprobe --repair-plan."); return 2; }
        try { (carried, fixes, carriedFailed) = Carried(patch, order); }
        catch (Exception ex) { Console.Error.WriteLine($"the plan's order could not be read to find the carried records' winners ({ex.Message}); check the plugins it names are on disk."); return 2; }
        failed += carriedFailed;
        var doc = new JsonObject
        {
            ["patch"] = patchPath, ["plan"] = planPath, ["holds"] = failed == 0, ["records"] = results,
            ["unplannedNavmeshes"] = new JsonArray(extra.Select(k => (JsonNode)k.ToString()).ToArray()),
            ["carried"] = carried, ["carriedFixes"] = fixes,
        };
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var r in results)
            Console.WriteLine($"   {((bool)r!["holds"]! ? "holds" : "FAILS")}  {r["navmesh"]}" + ((bool)r["holds"]! ? "" : ": " + string.Join("; ", r["differences"]!.AsArray().Select(x => (string)x!))));
        foreach (var k in extra) Console.WriteLine($"   FAILS  {k}: in the patch but not in the plan");
        foreach (var c in carried)
            Console.WriteLine($"   {((bool)c!["holds"]! ? "holds" : "FAILS")}  carried {c["type"]} {c["record"]} (winner {c["winner"]}, {c["compared"]} fields)"
                              + ((bool)c["holds"]! ? "" : ": " + string.Join("; ", c["differences"]!.AsArray().Select(x => (string)x!))));
        foreach (var f in fixes)
            Console.WriteLine($"   to make the carried records the winner's: housecarl_forward source={f!["source"]} formids={string.Join(",", f["formids"]!.AsArray().Select(x => (string)x!))} into the patch");
        Console.WriteLine($"read-back: {(failed == 0 ? "every planned navmesh is the winner with exactly the plan applied, and every carried record is its winner's" : $"{failed} check(s) fail")} -> {outPath}");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>The plan file's change object (NavRepairRun writes it), read back into a <see cref="NavChange"/>.</summary>
public static class NavRepairRunJson
{
    static Vec3 V(JsonNode n) => new((float)n["x"]!, (float)n["y"]!, (float)n["z"]!);
    public static NavChange ChangeOf(JsonNode j)
    {
        var c = new NavChange();
        foreach (var v in j["vertices"]!.AsArray()) c.Vertices[(int)v!["i"]!] = V(v["v"]!);
        if (j["min"] is { } mn) c.Min = V(mn);
        if (j["max"] is { } mx) c.Max = V(mx);
        foreach (var s in j["setEntries"]!.AsArray()) c.SetEntries[(int)s!["k"]!] = (FormKey.Factory((string)s["mesh"]!), (short)s["tri"]!);
        foreach (var k in j["removedEntries"]!.AsArray()) c.RemovedEntries.Add((int)k!);
        foreach (var a in j["addedEntries"]!.AsArray()) c.AddedEntries.Add(new NavEdgeLink(FormKey.Factory((string)a!["mesh"]!), (short)a["tri"]!, (int)a["type"]!));
        foreach (var f in j["edgeFields"]!.AsArray()) c.EdgeFields[((int)f!["tri"]!, (int)f["edge"]!)] = (short)f["value"]!;
        foreach (var f in j["flagBits"]!.AsArray()) c.FlagBits[((int)f!["tri"]!, (int)f["edge"]!)] = (bool)f["on"]!;
        return c;
    }
}
