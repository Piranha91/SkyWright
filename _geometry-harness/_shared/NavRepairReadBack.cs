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
    public static (JsonArray Records, JsonArray Fixes, int Failed) Carried(ISkyrimModGetter patch, IReadOnlyList<string> order, bool naviPlanned = false)
    {
        var records = new JsonArray(); var fix = new List<(string Plugin, string Type, FormKey Key)>(); int failed = 0;
        // a NAVI the plan changes is held by NaviCheck (its winner with exactly the plan's entries changed), not to its winner whole
        var carried = patch.EnumerateMajorRecords().Where(r => r is not INavigationMeshGetter && !(naviPlanned && r is INavigationMeshInfoMapGetter)).ToList();
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

    /// <summary>The patch's NAVI against the plan's: the winner's NAVI (the plan's winner file) with exactly the plan's entries
    /// changed: an entry the winner states gains its neighbours in place, one it does not is appended whole from the plugin that
    /// states it, with its neighbours. Every other entry, and the record's own fields, must equal the winner's. Returns the
    /// differences and the number of entries compared.</summary>
    public static (List<string> Diff, int Compared) NaviCheck(JsonNode navi, ISkyrimModGetter patch)
    {
        var diff = new List<string>();
        var key = FormKey.Factory((string)navi["record"]!);
        string? wpath = (string?)navi["winner"]?["path"];
        var got = patch.NavigationMeshInfoMaps.FirstOrDefault(m => m.FormKey == key);
        if (got is null) return (new() { $"the plan changes NAVI {key}, and the patch does not carry it" }, 0);
        if (wpath is null || !File.Exists(wpath)) return (new() { $"the NAVI winner's plugin '{wpath}' is not on disk, so {key} cannot be compared" }, 0);
        var opened = new Dictionary<string, ISkyrimModDisposableGetter>(StringComparer.OrdinalIgnoreCase);
        ISkyrimModGetter Open(string path) => opened.TryGetValue(path, out var m) ? m : opened[path] = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        try
        {
            var win = Open(wpath).NavigationMeshInfoMaps.FirstOrDefault(m => m.FormKey == key);
            if (win is null) return (new() { $"{key} is not in its winner {Path.GetFileName(wpath)}" }, 0);
            var want = win.DeepCopy();
            foreach (var e in navi["entries"]!.AsArray())
            {
                var nav = FormKey.Factory((string)e!["navmesh"]!);
                var add = e["add"]!.AsArray().Select(x => FormKey.Factory((string)x!)).ToList();
                var doors = (e["addDoors"] as JsonArray ?? new JsonArray()).Select(x => FormKey.Factory((string)x!)).ToList();
                // a carried version's door table: the entry's first keepDoors kept, the rest of the table written after them
                var after = (e["doorsAfter"] as JsonArray)?.Select(x => FormKey.Factory((string)x!)).ToList();
                int keepD = (int?)e["keepDoors"] ?? 0;
                void Doors(NavigationMapInfo m)
                {
                    if (after is not null)
                    {
                        while (m.LinkedDoors.Count > keepD) m.LinkedDoors.RemoveAt(m.LinkedDoors.Count - 1);
                        foreach (var d in after.Skip(keepD)) m.LinkedDoors.Add(new LinkedDoor { Unknown = NavRepairNavi.LinkedDoorUnknown, Door = new FormLink<IPlacedObjectGetter>(d) });
                    }
                    foreach (var d in doors) m.LinkedDoors.Add(new LinkedDoor { Unknown = NavRepairNavi.LinkedDoorUnknown, Door = new FormLink<IPlacedObjectGetter>(d) });
                }
                // a carried version's dropped neighbours (item 3): each removed once, by value, from the list that names it
                var dropM = (e["remove"] as JsonArray ?? new JsonArray()).Select(x => FormKey.Factory((string)x!)).ToList();
                var dropP = (e["removePreferred"] as JsonArray ?? new JsonArray()).Select(x => FormKey.Factory((string)x!)).ToList();
                void Drop(NavigationMapInfo m)
                {
                    foreach (var d in dropM) { int j = m.MergedTo.FindIndex(x => x.FormKey == d); if (j >= 0) m.MergedTo.RemoveAt(j); }
                    foreach (var d in dropP) { int j = m.PreferredMerges.FindIndex(x => x.FormKey == d); if (j >= 0) m.PreferredMerges.RemoveAt(j); }
                }
                if (e["winnerIndex"] is { } wi)
                {
                    int i = (int)wi;
                    if (i >= want.MapInfos.Count || want.MapInfos[i].NavigationMesh.FormKey != nav) { diff.Add($"the winner's entry {i} is not {nav}'s; the plan is stale"); continue; }
                    Drop(want.MapInfos[i]);
                    foreach (var m in add) want.MapInfos[i].MergedTo.Add(new FormLink<INavigationMeshGetter>(m));
                    Doors(want.MapInfos[i]);
                }
                else
                {
                    string fpath = (string)e["from"]!["path"]!; int fi = (int)e["from"]!["index"]!;
                    var src = File.Exists(fpath) ? Open(fpath).NavigationMeshInfoMaps.FirstOrDefault(m => m.FormKey == key) : null;
                    if (src is null || fi >= src.MapInfos.Count || src.MapInfos[fi].NavigationMesh.FormKey != nav) { diff.Add($"{nav}'s entry is not at {Path.GetFileName(fpath)}'s index {fi}; the plan is stale"); continue; }
                    var copy = src.MapInfos[fi].DeepCopy();
                    Drop(copy);
                    foreach (var m in add) copy.MergedTo.Add(new FormLink<INavigationMeshGetter>(m));
                    Doors(copy);
                    want.MapInfos.Add(copy);
                }
            }
            void Eq<T>(string what, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) diff.Add($"NAVI {what}: written {b}, planned {a}"); }
            Eq("FormVersion", want.FormVersion, got.FormVersion);
            Eq("VersionControl", want.VersionControl, got.VersionControl);
            Eq("Version2", want.Version2, got.Version2);
            Eq("record flags", want.MajorRecordFlagsRaw, got.MajorRecordFlagsRaw);
            Eq("NavMeshVersion", want.NavMeshVersion, got.NavMeshVersion);
            byte[]? nw = want.NVSI is { } x ? x.Span.ToArray() : null, ng = got.NVSI is { } y ? y.Span.ToArray() : null;
            if ((nw is null) != (ng is null) || (nw is not null && !nw.SequenceEqual(ng!))) diff.Add("NAVI NVSI bytes differ from the winner's");
            if (!Equals(want.PreferredPathing, got.PreferredPathing)) diff.Add("NAVI PreferredPathing differs from the winner's");
            Eq("entry count", want.MapInfos.Count, got.MapInfos.Count);
            for (int i = 0; i < Math.Min(want.MapInfos.Count, got.MapInfos.Count); i++)
                if (!want.MapInfos[i].Equals(got.MapInfos[i]))
                    diff.Add($"NAVI entry {i} ({got.MapInfos[i].NavigationMesh.FormKey}) differs from the planned {want.MapInfos[i].NavigationMesh.FormKey}'s: MergedTo written "
                             + $"[{string.Join(",", got.MapInfos[i].MergedTo.Select(x => x.FormKey))}], planned [{string.Join(",", want.MapInfos[i].MergedTo.Select(x => x.FormKey))}]; LinkedDoors written "
                             + $"[{string.Join(",", got.MapInfos[i].LinkedDoors.Select(x => x.Door.FormKey))}], planned [{string.Join(",", want.MapInfos[i].LinkedDoors.Select(x => x.Door.FormKey))}]");
            if (diff.Count == 0 && !want.Equals(got)) diff.Add("the NAVI records differ in a field outside the named set (Mutagen's whole-record equality)");
            return (diff, want.MapInfos.Count);
        }
        finally { foreach (var m in opened.Values) m.Dispose(); }
    }

    /// <summary>THE NAVI AGAINST THE VERSION, apart from the plan (houseCARL's items 2 and 3; the master's condition of
    /// 2026-11-22: the written NAVI read back and compared with the version field by field). The entry the game takes for
    /// <paramref name="key"/> with the patch loaded last (the last plugin of <paramref name="order"/>, then
    /// <paramref name="patchPath"/>, that states it), read from the written files, against <paramref name="versionPath"/>'s own
    /// record of the navmesh: its LinkedDoors are the version's door table, door for door and in order, each with
    /// <see cref="NavRepairNavi.LinkedDoorUnknown"/>, and its MergedTo and PreferredMerges together name exactly the navmeshes the
    /// version's link table names (the base game's rules, 15,461 of 15,461 entries). One sentence per difference; empty when the
    /// entry follows the version. A navmesh no NAVI states follows when the version has no doors (its links then have no entry
    /// to follow).</summary>
    public static List<string> NaviFollowsVersion(IReadOnlyList<string> order, string patchPath, FormKey key, string versionPath)
    {
        var version = File.Exists(versionPath) ? NavUseVersionPlan.Winning(versionPath, key) : null;
        if (version is null) return new() { $"{Path.GetFileName(versionPath)} has no version of {key} to hold the navmesh info map to" };
        var full = Path.GetFullPath(patchPath);
        var (view, open) = NaviView.Read(order.Where(p => !string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)).Append(patchPath).ToList());
        try
        {
            if (view.Problem is { } pr) return new() { $"the navmesh info map cannot be read back ({pr})" };
            var table = (version.DoorTriangles ?? Array.Empty<NavDoorTriangle>()).Select(d => d.Door).ToList();
            if (!view.Effective.TryGetValue(key, out var ef))
                return table.Count == 0 ? new() : new() { $"no plugin's NAVI states {key}, so its linked doors are not the version's door table of {table.Count}" };
            var e = ef.Src.Map.MapInfos[ef.Index];
            var diff = new List<string>();
            if (e.LinkedDoors.Count != table.Count) diff.Add($"{key}'s navmesh info map entry ({ef.Src.Plugin}) links {e.LinkedDoors.Count} door(s), and the version's door table has {table.Count}");
            for (int i = 0; i < Math.Min(e.LinkedDoors.Count, table.Count); i++)
            {
                if (e.LinkedDoors[i].Door.FormKey != table[i]) diff.Add($"{key}'s linked door {i} is {e.LinkedDoors[i].Door.FormKey}, where the version's door table has {table[i]}");
                if (e.LinkedDoors[i].Unknown != NavRepairNavi.LinkedDoorUnknown) diff.Add($"{key}'s linked door {i} has Unknown {e.LinkedDoors[i].Unknown}, not the base game's {NavRepairNavi.LinkedDoorUnknown}");
            }
            var targets = (version.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(l => l.Mesh).Where(m => m != key).ToHashSet();
            var named = e.MergedTo.Select(x => x.FormKey).Concat(e.PreferredMerges.Select(x => x.FormKey)).ToHashSet();
            foreach (var m in named.Where(m => !targets.Contains(m)).OrderBy(m => m.ToString()))
                diff.Add($"{key}'s navmesh info map entry ({ef.Src.Plugin}) names neighbour {m}, which the version does not link into");
            foreach (var m in targets.Where(m => !named.Contains(m)).OrderBy(m => m.ToString()))
                diff.Add($"{key}'s navmesh info map entry ({ef.Src.Plugin}) does not name {m}, which the version links into");
            return diff;
        }
        finally { foreach (var o in open) o.Dispose(); }
    }

    /// <summary>A written navmesh against its expected state (navedit's plan, or a use_version row of a repair plan): its
    /// vertices, triangles (corners, edge fields, flags), links, doors, box and grid (rebuilt by NavGrid, or kept as its
    /// winner's when <paramref name="rebuilt"/> is false), and each kept triangle's cover fields against
    /// <paramref name="winnerPath"/>'s triangle <paramref name="oldIndex"/> names. Null when the navmesh is not in the patch;
    /// else one sentence per difference, empty when it holds.</summary>
    public static List<string>? GeometryDifferences(FormKey key, NavmeshGeometry want, int[] oldIndex, string winnerPath, bool rebuilt,
                                                    NavmeshGeometry? got, INavigationMeshGetter? rec)
    {
        var why = new List<string>();
        if (got is null || rec is null || rec.Data is not { } d) return null;
        if (!got.Vertices.SequenceEqual(want.Vertices)) why.Add($"vertices differ ({got.Vertices.Count} written, {want.Vertices.Count} planned)");
        if (!got.Triangles.SequenceEqual(want.Triangles))
        {
            int i = Enumerable.Range(0, Math.Min(got.Triangles.Count, want.Triangles.Count)).FirstOrDefault(k => got.Triangles[k] != want.Triangles[k], -1);
            why.Add(i < 0 ? $"{got.Triangles.Count} triangles written, {want.Triangles.Count} planned" : $"triangle {i} is {got.Triangles[i]}, planned {want.Triangles[i]}");
        }
        if (!(got.EdgeLinks ?? []).SequenceEqual(want.EdgeLinks ?? [])) why.Add("the edge-link table differs from the plan");
        if (!(got.DoorTriangles ?? []).SequenceEqual(want.DoorTriangles ?? [])) why.Add("the door table differs from the plan");
        using (var wmod = SkyrimMod.CreateFromBinaryOverlay(winnerPath, SkyrimRelease.SkyrimSE))
        {
            var wd = wmod.EnumerateMajorRecords<INavigationMeshGetter>().First(n => n.FormKey == key).Data!;
            // where it stands and its format, as its source has them (review of slice B, finding 5: a version copied from a plugin
            // that put the navmesh under another cell passed the geometry checks)
            static string ParentOf(INavigationMeshDataGetter x) => x.Parent switch
            {
                IWorldspaceNavmeshParentGetter w => $"worldspace {w.Parent.FormKey} cell {w.Coordinates.X},{w.Coordinates.Y}",
                ICellNavmeshParentGetter c => $"cell {c.Parent.FormKey}",
                var o => o?.GetType().Name ?? "none",
            };
            if (ParentOf(d) != ParentOf(wd)) why.Add($"it stands in {ParentOf(d)}, where its source has {ParentOf(wd)}");
            if (d.NavmeshVersion != wd.NavmeshVersion) why.Add($"its navmesh version is {d.NavmeshVersion}, its source's {wd.NavmeshVersion}");
            if (rebuilt)
            {
                var g = NavGrid.Rebuild(got.Vertices, got.Triangles.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
                if (d.NavmeshGridDivisor != g.Divisor || d.MaxDistanceX != g.SectionX || d.MaxDistanceY != g.SectionY || !d.NavmeshGrid.Span.SequenceEqual(g.CellBytes))
                    why.Add("the grid is not the one NavGrid rebuilds from the written navmesh");
                if (got.Min != g.Min || got.Max != g.Max) why.Add("the box is not the used vertices' extent");
            }
            else if (d.NavmeshGridDivisor != wd.NavmeshGridDivisor || d.MaxDistanceX != wd.MaxDistanceX || d.MaxDistanceY != wd.MaxDistanceY
                     || !d.NavmeshGrid.Span.SequenceEqual(wd.NavmeshGrid.Span) || d.Min != wd.Min || d.Max != wd.Max)
                why.Add("its geometry is unchanged, so its grid and box must be its winner's, and they are not");
            for (int i = 0; i < d.Triangles.Count && i < oldIndex.Length; i++)
            {
                var (now, was) = (d.Triangles[i], oldIndex[i] >= 0 ? wd.Triangles[oldIndex[i]] : null);
                bool coverOk = was is null ? !now.IsCover && now.CoverFlags == 0 : now.IsCover == was.IsCover && now.CoverFlags == was.CoverFlags;
                if (!coverOk) { why.Add($"triangle {i}'s cover fields are not {(was is null ? "clear on an added triangle" : $"its winner's (triangle {oldIndex[i]})")}"); break; }
            }
        }
        return why;
    }

    /// <summary>A repair plan's planned use_version rows.</summary>
    public static List<JsonNode> UseVersionRows(JsonNode plan) => (plan["plans"] as JsonArray ?? new JsonArray())
        .Where(p => (string?)p!["class"] == "use_version" && (string?)p["status"] != "refused").Select(p => p!).ToList();

    /// <summary>The union of one navmesh's planned changes (a relink's entries, edge fields and flags; a ground plan's vertices
    /// and box): the later row's value where both set the same thing.</summary>
    public static NavChange Merge(IEnumerable<NavChange> changes)
    {
        var m = new NavChange();
        foreach (var c in changes)
        {
            foreach (var (k, v) in c.Vertices) m.Vertices[k] = v;
            if (c.Min is { } mn) m.Min = mn;
            if (c.Max is { } mx) m.Max = mx;
            foreach (var (k, v) in c.SetEntries) m.SetEntries[k] = v;
            foreach (var k in c.RemovedEntries) m.RemovedEntries.Add(k);
            m.AddedEntries.AddRange(c.AddedEntries);
            foreach (var (k, v) in c.EdgeFields) m.EdgeFields[k] = v;
            foreach (var (k, v) in c.FlagBits) m.FlagBits[k] = v;
        }
        return m;
    }

    /// <summary><c>navownprobe --repair-readback PLAN.json PATCH.esp OUT.json</c>: every planned navmesh in the patch against
    /// its winner with the plan applied, every NAVM in the patch a planned one, and every other record the patch carries (the
    /// CELL and WRLD containers) equal to its winner on the plan's order. Exit 0 when every check holds, 1 when one fails (each
    /// said in one sentence; a failing container names the forward that makes it the winner's), 2 when the inputs cannot be
    /// read. A planned use_version row (navownprobe's class "use_version") is held to <paramref name="expect"/>, the expected
    /// state navedit write --repair-plan re-plans it to (GeometryDifferences, and its NAVI entries); without that map the row
    /// fails, saying where it is checked, since this read alone cannot re-plan it.</summary>
    public static int Run(string planPath, string patchPath, string outPath,
                          IReadOnlyDictionary<FormKey, (NavmeshGeometry Geom, int[] OldIndex, string WinnerPath, bool Rebuilt)>? expect = null)
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
            // one navmesh can carry a relink row and a follow-the-ground row: the patch holds both, so the read-back holds it to
            // their union (the Winterhold ground station, 2026-11-22: each row alone failed against the combined write)
            var rows = plan["plans"]!.AsArray().Where(p => (string)p!["status"]! != "refused" && (string?)p!["class"] != "use_version")
                .GroupBy(p => (string)p!["source"]!).Select(g => (Row: g.First()!, Change: Merge(g.Select(r => NavRepairRunJson.ChangeOf(r!["change"]!))))).ToList();
            foreach (var (p, change) in rows)
            {
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
                         : Differences(win, got, change);
                }
                if (diff.Count > 0) failed++;
                results.Add(new JsonObject { ["navmesh"] = key.ToString(), ["holds"] = diff.Count == 0, ["differences"] = new JsonArray(diff.Select(x => (JsonNode)x).ToArray()) });
            }
            // use_version rows: every navmesh each changes (the carried one, and each neighbour whose links were re-pointed)
            var geoms = PlacementExtract.FromMod(patch).SelectMany(cp => cp.Navmeshes).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.First());
            foreach (var p in UseVersionRows(plan))
                foreach (var k in p["changes"]!.AsArray().Select(x => FormKey.Factory((string)x!)))
                {
                    planned.Add(k);
                    List<string> diff;
                    if (expect is null || !expect.TryGetValue(k, out var e))
                        diff = new() { $"{k} is carried by a use_version row, whose written state only navedit write --repair-plan can hold to the re-planned version; read the patch back with it" };
                    else diff = GeometryDifferences(k, e.Geom, e.OldIndex, e.WinnerPath, e.Rebuilt, geoms.GetValueOrDefault(k), written.GetValueOrDefault(k))
                                ?? new() { $"{k} is planned but not in the patch" };
                    // the carried navmesh's NAVI entry, as the game takes it with the patch last, against the version itself
                    if (expect is not null && k == FormKey.Factory((string)p["source"]!))
                        diff.AddRange(NaviFollowsVersion(plan["order"]?.AsArray().Select(x => (string)x!).ToList() ?? new List<string>(), patchPath, k, (string?)p["winner"]?["path"] ?? ""));
                    if (diff.Count > 0) failed++;
                    results.Add(new JsonObject { ["navmesh"] = k.ToString(), ["useVersion"] = (string?)p["source"], ["holds"] = diff.Count == 0, ["differences"] = new JsonArray(diff.Select(x => (JsonNode)x).ToArray()) });
                }
        }
        finally { foreach (var m in opened.Values) m.Dispose(); }
        var extra = written.Keys.Where(k => !planned.Contains(k)).ToList();
        failed += extra.Count;
        var order = plan["order"]?.AsArray().Select(x => (string)x!).ToList() ?? new List<string>();
        JsonArray carried, fixes; int carriedFailed;
        if (order.Count == 0) { carried = new JsonArray(); fixes = new JsonArray(); carriedFailed = 0; Console.Error.WriteLine("the plan names no order, so the carried records cannot be held to their winners; re-write it with navownprobe --repair-plan."); return 2; }
        var navi = plan["navi"];
        // a use_version row that gains a neighbour carries its own NAVI change (the planner never lets it share a plan with
        // the relinks' NAVI change), held to its entries like the relinks' are
        var uvNavi = UseVersionRows(plan).Select(p => p["useVersion"]?["navi"]).Where(n => n?["entries"] is JsonArray { Count: > 0 }).ToList();
        bool naviPlanned = navi?["entries"] is JsonArray ne && ne.Count > 0 || uvNavi.Count > 0;
        if (navi?["entries"] is not JsonArray { Count: > 0 } && uvNavi.Count > 0) navi = uvNavi[0];
        if (uvNavi.Count > 1 || (uvNavi.Count == 1 && !ReferenceEquals(navi, uvNavi[0])))
        { Console.Error.WriteLine("the plan carries more than one change to the navmesh info map, which one patch cannot be held to; write the plan again with a current navownprobe --repair-plan."); return 2; }
        try { (carried, fixes, carriedFailed) = Carried(patch, order, naviPlanned); }
        catch (Exception ex) { Console.Error.WriteLine($"the plan's order could not be read to find the carried records' winners ({ex.Message}); check the plugins it names are on disk."); return 2; }
        failed += carriedFailed;
        if (naviPlanned)
        {
            var (naviDiff, compared) = NaviCheck(navi!, patch);
            if (naviDiff.Count > 0) failed++;
            carried.Add(new JsonObject
            {
                ["record"] = (string?)navi!["record"], ["type"] = "NavigationMeshInfoMap", ["winner"] = (string?)navi["winner"]?["plugin"], ["holds"] = naviDiff.Count == 0,
                ["differences"] = new JsonArray(naviDiff.Select(x => (JsonNode)x).ToArray()), ["compared"] = compared, ["planned"] = true,
            });
        }
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

    /// <summary>The repair plan's op wire shape (navownprobe writes it, navedit write --repair-plan compares a re-planned row
    /// against it): formid, field_path, op, and key, value, from_plugin (a CopyFrom's source: use_version carries a
    /// navmesh's Data from it) and compose when set. Its one owner.</summary>
    public static JsonArray OpsJson(IEnumerable<RepairOp> ops) => new(ops.Select(o =>
    {
        var j = new JsonObject { ["formid"] = o.FormId, ["field_path"] = o.FieldPath, ["op"] = o.Op };
        if (o.Key is not null) j["key"] = o.Key;
        if (o.Value is not null) j["value"] = o.Value;
        if (o.FromPlugin is not null) j["from_plugin"] = o.FromPlugin;
        if (o.Compose is not null)
            j["compose"] = new JsonObject { ["type"] = "EdgeLink", ["fields"] = new JsonObject(o.Compose.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) };
        if (o.ComposeNode is not null) j["compose"] = o.ComposeNode.DeepClone();
        return (JsonNode)j;
    }).ToArray());
}
