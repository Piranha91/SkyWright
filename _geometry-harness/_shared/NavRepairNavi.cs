using System.Globalization;
using System.Text.Json.Nodes;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE NAVI HALF OF A RELINK (task 2026-11-16-navmesh-repair, the RN arm). The navigation info map (NAVI) holds one entry per
// navmesh; its MergedTo and PreferredMerges lists name the navmeshes it borders (Gate B's census: MergedTo is exactly the set
// the navmesh's own edge links name). A relink that moves a link into another navmesh changes that set, so the plan adds the
// new neighbour to both navmeshes' entries.
//
//   WHOSE ENTRY. The game merges NAVI per plugin: every plugin's contribution loads, and a navmesh's entry is taken from the
//   last plugin that states it (Research/COMPATIBILITY-CLASSES.md, "the whole per-plugin contribution"; the engine's
//   NavMeshInfoMap keeps one info per navmesh id). So the entry changed is the EFFECTIVE one, the last plugin's that states
//   the navmesh, not necessarily the record's winner's.
//   WHAT THE PATCH CARRIES. housecarl_apply overrides the record's winner (the last plugin carrying NAVI) into the patch, so
//   the patch carries that plugin's whole contribution, unchanged but for the plan: an entry the winner states gains its
//   neighbour in place; an entry only an earlier plugin states is added whole, copied from that plugin, with its neighbour.
//   A navmesh no plugin's NAVI states refuses its relink by name: the coarse map could not be told.

/// <summary>One plugin's NAVI record, in load order.</summary>
public sealed record NaviSource(string Plugin, string Path, INavigationMeshInfoMapGetter Map);

/// <summary>The order's NAVI as the plan needs it: the record, its winner (the last plugin carrying it) and, per navmesh, the
/// effective entry (the last plugin stating it, and the entry's index in that plugin's list).</summary>
public sealed class NaviView
{
    public FormKey? Record { get; private init; }
    public NaviSource? Winner { get; private init; }
    public string? Problem { get; private init; }
    public readonly Dictionary<FormKey, (NaviSource Src, int Index)> Effective = new();

    public static NaviView Of(IReadOnlyList<NaviSource> inOrder)
    {
        var keys = inOrder.Select(s => s.Map.FormKey).Distinct().ToList();
        if (keys.Count > 1)
            return new NaviView { Problem = $"the order carries {keys.Count} NAVI records ({string.Join(", ", keys)}), and the plan models one" };
        var v = new NaviView { Record = keys.Count == 1 ? keys[0] : null, Winner = inOrder.LastOrDefault() };
        foreach (var s in inOrder)
            for (int i = 0; i < s.Map.MapInfos.Count; i++) v.Effective[s.Map.MapInfos[i].NavigationMesh.FormKey] = (s, i);
        return v;
    }

    /// <summary>Every plugin's NAVI in <paramref name="paths"/> (load order), opened as binary overlays; the caller disposes them.</summary>
    public static (NaviView View, List<IDisposable> Open) Read(IReadOnlyList<string> paths)
    {
        var open = new List<IDisposable>(); var src = new List<NaviSource>();
        foreach (var p in paths)
        {
            ISkyrimModDisposableGetter mod; List<INavigationMeshInfoMapGetter> maps;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(p, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE); maps = mod.NavigationMeshInfoMaps.ToList(); }
            catch (Exception ex)
            {
                foreach (var o in open) o.Dispose();
                return (new NaviView { Problem = $"{Path.GetFileName(p)} could not be read for its NAVI ({ex.Message}), so the coarse map's entries are not known" }, new List<IDisposable>());
            }
            if (maps.Count == 0) { mod.Dispose(); continue; }
            open.Add(mod);
            foreach (var m in maps) src.Add(new NaviSource(Path.GetFileName(p), p, m));
        }
        return (NaviView.Of(src), open);
    }
}

/// <summary>One NAVI entry the plan changes: the navmesh, the plugin and index its effective entry comes from, its index in the
/// winner's list (null: the winner does not state it, so the patch adds it whole) and the neighbours added to its MergedTo.</summary>
public sealed record NaviEntryChange(FormKey Nav, string FromPlugin, string FromPath, int FromIndex, int? WinnerIndex, IReadOnlyList<FormKey> Add,
                                     IReadOnlyList<FormKey> MergedToBefore, IReadOnlyList<FormKey> PreferredMerges)
{
    /// <summary>Doors the entry gains in its LinkedDoors (navedit's door edit), each with <see cref="NavRepairNavi.LinkedDoorUnknown"/>.</summary>
    public IReadOnlyList<FormKey> AddDoors { get; init; } = Array.Empty<FormKey>();
    /// <summary>The LinkedDoors the entry ends with (a carried navmesh version's door table, in its order; null: unchanged), and how
    /// many of the entry's own LinkedDoors are kept as they are before the rest is written (<see cref="NavRepairNavi.KeptDoors"/>).</summary>
    public IReadOnlyList<FormKey>? DoorsAfter { get; init; }
    public int KeepDoors { get; init; }
    /// <summary>Neighbours removed from the entry's MergedTo and from its PreferredMerges (a carried version that no longer links
    /// into them, item 3), each removed once by value.</summary>
    public IReadOnlyList<FormKey> Remove { get; init; } = Array.Empty<FormKey>();
    public IReadOnlyList<FormKey> RemovePreferred { get; init; } = Array.Empty<FormKey>();
}

/// <summary>The NAVI change of a relink plan, and its ops for housecarl_apply.</summary>
public sealed class NaviPlan
{
    public FormKey? Record { get; init; }
    public string? WinnerPlugin { get; init; }
    public string? WinnerPath { get; init; }
    public readonly List<NaviEntryChange> Entries = new();
    public readonly List<RepairOp> Ops = new();
    public readonly List<string> Why = new();
}

public static class NavRepairNavi
{
    /// <summary>The Unknown field every door carries, in a navmesh's door table and in a NAVI entry's LinkedDoors alike: one
    /// value on every one of the base game's 1,703 door-table entries and 1,703 linked doors (Skyrim.esm, 2026-11-22). Copied,
    /// not understood; a door added by navedit carries it.</summary>
    public const int LinkedDoorUnknown = -460622861;

    /// <summary>The NAVI change that gives navmesh <paramref name="nav"/>'s entry <paramref name="doors"/> in its LinkedDoors
    /// (a door it now stands before: in the base game an entry's LinkedDoors are exactly its navmesh's door table, 1,103 of
    /// 1,103 entries). In place on the NAVI winner's entry, else the entry added whole from the plugin that states it.
    /// Refused in one sentence when no plugin's NAVI states the navmesh, or the entry already names a door.</summary>
    public static (NaviPlan Plan, string? Refused) ForDoors(FormKey nav, IReadOnlyList<FormKey> doors, NaviView view)
    {
        var np = new NaviPlan { Record = view.Record, WinnerPlugin = view.Winner?.Plugin, WinnerPath = view.Winner?.Path };
        string? why = view.Problem is { } pr ? pr
                    : view.Record is null ? "no plugin of the order carries a NAVI record, so the coarse map cannot be told of the door"
                    : !view.Effective.ContainsKey(nav) ? $"no plugin's NAVI states {nav}, so its entry cannot gain the door"
                    : null;
        if (why is not null) return (np, why);
        var (src, idx) = view.Effective[nav];
        var e = src.Map.MapInfos[idx];
        var have = e.LinkedDoors.Select(d => d.Door.FormKey).ToHashSet();
        if (doors.FirstOrDefault(have.Contains) is { IsNull: false } dup) return (np, $"{nav}'s navmesh info map entry ({src.Plugin}) already links door {dup}");
        int? wi = src == view.Winner ? idx : null;
        var merged = e.MergedTo.Select(x => x.FormKey).ToList(); var pref = e.PreferredMerges.Select(x => x.FormKey).ToList();
        np.Entries.Add(new NaviEntryChange(nav, src.Plugin, src.Path, idx, wi, Array.Empty<FormKey>(), merged, pref) { AddDoors = doors.ToList() });
        string id = view.Record!.Value.ToString();
        if (wi is { } w)
        {
            foreach (var d in doors) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].LinkedDoors", "Add", ComposeNode: LinkedDoor(d)));
            np.Why.Add($"{nav}'s entry in {src.Plugin}, the NAVI winner, gains linked door(s) {string.Join(" and ", doors)}");
        }
        else if (e.Parent is INavigationMapInfoCellParentGetter)
            // KNOWN CORE GAP (2026-11-22): the pinned write engine's compose of a WHOLE interior entry (a cell parent) leaves a
            // required sub-field null and the serialize refuses; the same entry built directly through Mutagen writes. Refused
            // here, by name, until houseCARL's core composes it.
            return (np, $"{nav}'s navmesh info map entry is stated by {src.Plugin}, not by the NAVI winner {view.Winner!.Plugin}, so it would be added whole, and the write engine cannot yet compose a whole interior entry");
        else
        {
            np.Ops.Add(new RepairOp(id, "MapInfos", "Add", ComposeNode: Compose(e, Array.Empty<FormKey>(), doors)));
            np.Why.Add($"{nav}'s entry, from {src.Plugin} (the winner {view.Winner!.Plugin} does not state it), is added whole with linked door(s) {string.Join(" and ", doors)}");
        }
        return (np, null);
    }

    static JsonObject LinkedDoor(FormKey door) => new()
    {
        ["type"] = "LinkedDoor",
        ["fields"] = new JsonObject { ["Unknown"] = LinkedDoorUnknown.ToString(CultureInfo.InvariantCulture), ["Door"] = door.ToString() },
    };

    /// <summary>How many of <paramref name="e"/>'s LinkedDoors already stand as <paramref name="target"/> wants them, counted from
    /// the front: the same door, with <see cref="LinkedDoorUnknown"/>. Those are kept; every later one is removed and the rest of
    /// the target appended, so the entry ends exactly as the target, in order.</summary>
    public static int KeptDoors(INavigationMapInfoGetter e, IReadOnlyList<FormKey> target)
    {
        int k = 0;
        while (k < e.LinkedDoors.Count && k < target.Count && e.LinkedDoors[k].Door.FormKey == target[k] && e.LinkedDoors[k].Unknown == LinkedDoorUnknown) k++;
        return k;
    }

    /// <summary>THE NAVI CHANGE FOR A CARRIED VERSION (use_version; houseCARL's items 2 and 3). In the base game an entry's
    /// LinkedDoors are its navmesh's door table exactly, in order and with repeats, and its MergedTo and PreferredMerges together
    /// are exactly the navmeshes its link table names (15,461 of 15,461 Skyrim.esm entries and 1,863 of 1,863 Dawnguard.esm's;
    /// SkyWright run 2026-11-22-overnight-non-ui, evidence/doorset). So when <paramref name="key"/>'s version is carried, its
    /// entry's LinkedDoors become <paramref name="doorTable"/>, the version's door table; its neighbour lists become
    /// <paramref name="targets"/>, the version's link targets (each one it lacks added to MergedTo, each it names beyond them
    /// removed from the list naming it); and each of <paramref name="gained"/> also gains <paramref name="key"/> in its own entry,
    /// as a relink's pair does. A neighbour's entry is never trimmed: it follows that neighbour's own links, which carrying K does
    /// not change. In place on an entry the NAVI winner states, else the entry added whole from the plugin that states it; an
    /// entry already standing so is left alone. Refused in one sentence: the map unreadable, a door table or gained neighbour for
    /// a navmesh no plugin's NAVI states, or a whole interior entry (the write engine's known gap).</summary>
    public static (NaviPlan Plan, string? Refused) ForVersion(FormKey key, IReadOnlyList<FormKey> doorTable, IReadOnlyCollection<FormKey> targets,
                                                              IReadOnlyCollection<FormKey> gained, NaviView view)
    {
        var np = new NaviPlan { Record = view.Record, WinnerPlugin = view.Winner?.Plugin, WinnerPath = view.Winner?.Path };
        if (view.Problem is { } pr) return (np, pr);
        // per entry: what it must name (null: only gain Add), what it gains, and its door table (null: unchanged)
        var want = new SortedDictionary<FormKey, (IReadOnlyCollection<FormKey>? Exactly, IReadOnlyCollection<FormKey> Add, IReadOnlyList<FormKey>? Doors)>(
            Comparer<FormKey>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString())));
        want[key] = (targets.Union(gained).Where(m => m != key).ToList(), gained, doorTable);
        foreach (var m in gained) if (m != key) want[m] = (null, new[] { key }, null);
        foreach (var (n, (_, add, doors)) in want)
            if (!view.Effective.ContainsKey(n) && (add.Count > 0 || doors is { Count: > 0 }))
                return (np, view.Record is null ? "no plugin of the order carries a NAVI record"
                                                : $"no plugin's NAVI states {n}");
        string id = view.Record?.ToString() ?? "";
        foreach (var (n, (exactly, adds, doors)) in want)
        {
            if (!view.Effective.TryGetValue(n, out var ef)) continue;
            var (src, idx) = ef;
            var e = src.Map.MapInfos[idx];
            var merged = e.MergedTo.Select(x => x.FormKey).ToList(); var pref = e.PreferredMerges.Select(x => x.FormKey).ToList();
            var add = (exactly ?? adds).Where(m => !merged.Contains(m) && !pref.Contains(m)).Distinct().ToList();
            var dropM = exactly is null ? new List<FormKey>() : merged.Where(m => !exactly.Contains(m)).ToList();
            var dropP = exactly is null ? new List<FormKey>() : pref.Where(m => !exactly.Contains(m)).ToList();
            int keep = doors is null ? 0 : KeptDoors(e, doors);
            bool doorsChange = doors is not null && (keep < e.LinkedDoors.Count || keep < doors.Count);
            if (add.Count == 0 && dropM.Count == 0 && dropP.Count == 0 && !doorsChange) { np.Why.Add($"{n}'s entry ({src.Plugin}) already follows the version"); continue; }
            int? wi = src == view.Winner ? idx : null;
            np.Entries.Add(new NaviEntryChange(n, src.Plugin, src.Path, idx, wi, add, merged, pref)
            {
                DoorsAfter = doorsChange ? doors!.ToList() : null, KeepDoors = doorsChange ? keep : 0, Remove = dropM, RemovePreferred = dropP,
            });
            var what = string.Join(", and ", new[]
            {
                add.Count > 0 ? $"gains {string.Join(" and ", add)}" : null,
                dropM.Count + dropP.Count > 0 ? $"drops {string.Join(" and ", dropM.Concat(dropP).Distinct())}, which the version no longer links into" : null,
                doorsChange ? $"its linked doors become the version's door table [{string.Join(", ", doors!)}] (were [{string.Join(", ", e.LinkedDoors.Select(d => d.Door.FormKey))}])" : null,
            }.Where(x => x is not null));
            if (wi is { } w)
            {
                foreach (var m in dropM) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].MergedTo", "Remove", Value: m.ToString()));
                foreach (var m in dropP) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].PreferredMerges", "Remove", Value: m.ToString()));
                foreach (var m in add) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].MergedTo", "Add", Value: m.ToString()));
                if (doorsChange)
                {
                    // from the back, so each index still names the door it did when planned; then the rest of the table, in order
                    for (int i = e.LinkedDoors.Count - 1; i >= keep; i--) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].LinkedDoors", "Remove", Key: i.ToString(CultureInfo.InvariantCulture)));
                    for (int j = keep; j < doors!.Count; j++) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].LinkedDoors", "Add", ComposeNode: LinkedDoor(doors[j])));
                }
                np.Why.Add($"{n}'s entry in {src.Plugin}, the NAVI winner, {what}");
            }
            else if (e.Parent is INavigationMapInfoCellParentGetter)
                // the KNOWN CORE GAP of ForDoors: a whole interior entry cannot be composed yet
                return (new NaviPlan { Record = view.Record, WinnerPlugin = view.Winner?.Plugin, WinnerPath = view.Winner?.Path },
                        $"{n}'s entry is stated by {src.Plugin}, not by the NAVI winner {view.Winner!.Plugin}, so it would be added whole, and the write engine cannot yet compose a whole interior entry");
            else
            {
                np.Ops.Add(new RepairOp(id, "MapInfos", "Add", ComposeNode: Compose(e, add, doorsAfter: doorsChange ? doors : null, drop: dropM.Concat(dropP).ToList())));
                np.Why.Add($"{n}'s entry, from {src.Plugin} (the winner {view.Winner!.Plugin} does not state it), is added whole, and it {what}");
            }
        }
        return (np, null);
    }

    /// <summary>A NAVI change as the plan and the read-back carry it: the record, its winner, and each entry changed.</summary>
    public static JsonObject NaviJson(NaviPlan n) => new()
    {
        ["record"] = n.Record?.ToString(),
        ["winner"] = n.WinnerPlugin is null ? null : new JsonObject { ["plugin"] = n.WinnerPlugin, ["path"] = n.WinnerPath },
        ["why"] = new JsonArray(n.Why.Select(w => (JsonNode)w).ToArray()),
        ["entries"] = new JsonArray(n.Entries.Select(e => (JsonNode)new JsonObject
        {
            ["navmesh"] = e.Nav.ToString(), ["from"] = new JsonObject { ["plugin"] = e.FromPlugin, ["path"] = e.FromPath, ["index"] = e.FromIndex },
            ["winnerIndex"] = e.WinnerIndex, ["add"] = new JsonArray(e.Add.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["addDoors"] = new JsonArray(e.AddDoors.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["doorsAfter"] = e.DoorsAfter is null ? null : new JsonArray(e.DoorsAfter.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["keepDoors"] = e.DoorsAfter is null ? null : e.KeepDoors,
            ["remove"] = new JsonArray(e.Remove.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["removePreferred"] = new JsonArray(e.RemovePreferred.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["mergedToBefore"] = new JsonArray(e.MergedToBefore.Select(m => (JsonNode)m.ToString()).ToArray()),
            ["preferredMerges"] = new JsonArray(e.PreferredMerges.Select(m => (JsonNode)m.ToString()).ToArray()),
        }).ToArray()),
    };
    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The navmeshes a navmesh's edges link to, by its table.</summary>
    public static HashSet<FormKey> Neighbours(NavmeshGeometry g)
    {
        var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>(); var s = new HashSet<FormKey>();
        foreach (var t in g.Triangles)
            for (int e = 0; e < 3; e++)
                if (NavBorderMatch.EdgeIsLink(t, e) && NavBorderMatch.EdgeLink(t, e) is var k && k >= 0 && k < links.Count) s.Add(links[k].Mesh);
        return s;
    }

    /// <summary>The NAVI change for <paramref name="plan"/>'s relinks: every navmesh pair a planned relink newly joins is added to
    /// both effective entries' MergedTo, unless either list already names it. A relink whose pair has no entry to write is
    /// refused by name, with every planned edit that depends on it.</summary>
    public static NaviPlan Plan(NavRepairPlan plan, IReadOnlyDictionary<FormKey, WinningNav> nav, NaviView view)
    {
        while (true)
        {
            var need = new SortedDictionary<FormKey, SortedSet<FormKey>>(Comparer<FormKey>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString())));
            SortedSet<FormKey> Need(FormKey k) => need.TryGetValue(k, out var s) ? s : need[k] = new SortedSet<FormKey>(need.Comparer);
            // a pair the plan joins: a navmesh whose planned links name another its winning version did not (a re-point within
            // one navmesh names nothing new; a link back does, and its pair's entries are then checked like any other)
            foreach (var p in plan.Plans.Where(p => p.Status != "refused" && p.Class == "relink"))
            {
                var before = Neighbours(nav[p.Nav].Geom);
                foreach (var m in Neighbours(NavRepair.Apply(nav[p.Nav].Geom, p.Change)).Where(m => m != p.Nav && !before.Contains(m)))
                { Need(p.Nav).Add(m); Need(m).Add(p.Nav); }
            }
            var (np, refused) = ForPairs(need.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<FormKey>)kv.Value), view);
            if (refused.Count == 0) return np;
            var refuse = new Dictionary<FormKey, string>();
            foreach (var (a, why) in refused)
                foreach (var p in plan.Plans.Where(p => p.Status != "refused" && p.Class == "relink" && (p.Nav == a || need[a].Contains(p.Nav))))
                    refuse.TryAdd(p.Nav, why);
            NavRepair.Refuse(plan, refuse);
        }
    }

    /// <summary>The NAVI change that joins each navmesh in <paramref name="need"/> to the navmeshes it lists: each is added to
    /// the navmesh's effective entry's MergedTo, unless the entry already names it (in place on the NAVI winner's entry, else
    /// the entry added whole from the plugin that states it). The caller lists both directions of a pair. Each navmesh whose
    /// entry cannot be written is returned with the one sentence why, and then nothing is planned.</summary>
    public static (NaviPlan Plan, Dictionary<FormKey, string> Refused) ForPairs(IReadOnlyDictionary<FormKey, IReadOnlyCollection<FormKey>> need, NaviView view)
    {
        var refused = new Dictionary<FormKey, string>();
        foreach (var (a, adds) in need.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            string? why = view.Problem is { } pr ? pr
                        : view.Record is null ? "no plugin of the order carries a NAVI record, so the coarse map cannot be told of the new neighbour"
                        : !view.Effective.ContainsKey(a) ? $"no plugin's NAVI states {a}, so its entry cannot gain {string.Join(" and ", adds)}"
                        : null;
            if (why is not null) refused[a] = why;
        }
        var np = new NaviPlan { Record = view.Record, WinnerPlugin = view.Winner?.Plugin, WinnerPath = view.Winner?.Path };
        if (refused.Count > 0) return (np, refused);
        if (need.Count == 0) { np.Why.Add("no relink joins a new pair of navmeshes, so NAVI is not written"); return (np, refused); }
        var winnerIndex = new Dictionary<FormKey, int>();
        for (int i = 0; i < view.Winner!.Map.MapInfos.Count; i++) winnerIndex[view.Winner.Map.MapInfos[i].NavigationMesh.FormKey] = i;
        string id = view.Record!.Value.ToString();
        foreach (var (a, adds) in need.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            var (src, idx) = view.Effective[a];
            var e = src.Map.MapInfos[idx];
            var merged = e.MergedTo.Select(x => x.FormKey).ToList(); var pref = e.PreferredMerges.Select(x => x.FormKey).ToList();
            var add = adds.Where(m => !merged.Contains(m) && !pref.Contains(m)).ToList();
            if (add.Count == 0) { np.Why.Add($"{a}'s entry ({src.Plugin}) already names {string.Join(" and ", adds)}"); continue; }
            int? wi = src == view.Winner ? idx : null;
            np.Entries.Add(new NaviEntryChange(a, src.Plugin, src.Path, idx, wi, add, merged, pref));
            if (wi is { } w)
            {
                foreach (var m in add) np.Ops.Add(new RepairOp(id, $"MapInfos[{w}].MergedTo", "Add", Value: m.ToString()));
                np.Why.Add($"{a}'s entry in {src.Plugin}, the NAVI winner, gains {string.Join(" and ", add)}");
            }
            else
            {
                np.Ops.Add(new RepairOp(id, "MapInfos", "Add", ComposeNode: Compose(e, add)));
                np.Why.Add($"{a}'s entry, from {src.Plugin} (the winner {view.Winner.Plugin} does not state it), is added whole with {string.Join(" and ", add)}");
            }
        }
        return (np, refused);
    }

    static JsonObject Set(string path, string verb, string? value = null, JsonObject? compose = null)
    {
        var j = new JsonObject { ["path"] = path, ["verb"] = verb };
        if (value is not null) j["value"] = value;
        if (compose is not null) j["compose"] = compose;
        return j;
    }

    /// <summary>A whole NAVI entry as a housecarl_apply compose: every field of <paramref name="e"/>, with <paramref name="add"/>
    /// appended to its MergedTo and <paramref name="doors"/> to its LinkedDoors; with <paramref name="doorsAfter"/>, its LinkedDoors
    /// are those instead (its own kept as far as <see cref="KeptDoors"/> reaches, the rest written new); <paramref name="drop"/>
    /// leaves those neighbours out of its MergedTo and PreferredMerges.</summary>
    public static JsonObject Compose(INavigationMapInfoGetter e, IEnumerable<FormKey> add, IEnumerable<FormKey>? doors = null, IReadOnlyList<FormKey>? doorsAfter = null,
                                     IReadOnlyCollection<FormKey>? drop = null)
    {
        var sets = new JsonArray();
        drop ??= Array.Empty<FormKey>();
        foreach (var m in e.MergedTo.Select(x => x.FormKey).Where(x => !drop.Contains(x)).Concat(add)) sets.Add(Set("MergedTo", "Add", m.ToString()));
        foreach (var m in e.PreferredMerges.Where(x => !drop.Contains(x.FormKey))) sets.Add(Set("PreferredMerges", "Add", m.FormKey.ToString()));
        int keep = doorsAfter is null ? e.LinkedDoors.Count : KeptDoors(e, doorsAfter);
        if (doorsAfter is not null) doors = doorsAfter.Skip(keep).Concat(doors ?? Array.Empty<FormKey>()).ToList();
        foreach (var d in e.LinkedDoors.Take(keep))
            sets.Add(Set("LinkedDoors", "Add", compose: new JsonObject
            {
                ["type"] = "LinkedDoor",
                ["fields"] = new JsonObject { ["Unknown"] = d.Unknown.ToString(CultureInfo.InvariantCulture), ["Door"] = d.Door.FormKey.ToString() },
            }));
        foreach (var d in doors ?? Array.Empty<FormKey>()) sets.Add(Set("LinkedDoors", "Add", compose: LinkedDoor(d)));
        sets.Add(Set("Parent", "Set", compose: e.Parent switch
        {
            INavigationMapInfoWorldParentGetter w => new JsonObject
            {
                ["type"] = "NavigationMapInfoWorldParent",
                ["fields"] = new JsonObject { ["ParentWorldspace"] = w.ParentWorldspace.FormKey.ToString(), ["ParentWorldspaceCoord"] = $"{w.ParentWorldspaceCoord.X},{w.ParentWorldspaceCoord.Y}" },
            },
            INavigationMapInfoCellParentGetter c => new JsonObject
            {
                ["type"] = "NavigationMapInfoCellParent",
                ["fields"] = new JsonObject { ["ParentCell"] = c.ParentCell.FormKey.ToString(), ["Unused"] = c.Unused.ToString(CultureInfo.InvariantCulture) },
            },
            _ => throw new InvalidOperationException($"{e.NavigationMesh.FormKey}'s NAVI entry has a parent of a kind the plan does not model ({e.Parent?.GetType().Name ?? "none"})"),
        }));
        if (e.Island is { } isl)
        {
            var isets = new JsonArray();
            foreach (var v in isl.Vertices) isets.Add(Set("Vertices", "Add", $"{F(v.X)},{F(v.Y)},{F(v.Z)}"));
            foreach (var t in isl.Triangles) isets.Add(Set("Triangles", "Add", $"{t.X},{t.Y},{t.Z}"));
            sets.Add(Set("Island", "Set", compose: new JsonObject
            {
                ["type"] = "IslandData",
                ["fields"] = new JsonObject { ["Min"] = $"{F(isl.Min.X)},{F(isl.Min.Y)},{F(isl.Min.Z)}", ["Max"] = $"{F(isl.Max.X)},{F(isl.Max.Y)},{F(isl.Max.Z)}" },
                ["sets"] = isets,
            }));
        }
        return new JsonObject
        {
            ["type"] = "NavigationMapInfo",
            ["fields"] = new JsonObject
            {
                ["NavigationMesh"] = e.NavigationMesh.FormKey.ToString(), ["Unknown"] = e.Unknown.ToString(CultureInfo.InvariantCulture),
                ["Point"] = $"{F(e.Point.X)},{F(e.Point.Y)},{F(e.Point.Z)}", ["PreferredMergesFlag"] = e.PreferredMergesFlag.ToString(CultureInfo.InvariantCulture),
                ["Unknown2"] = e.Unknown2.ToString(CultureInfo.InvariantCulture),
            },
            ["sets"] = sets,
        };
    }
}
