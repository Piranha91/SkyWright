using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// DANGLING NAVMESH LINKS, the decisions: the winning link state (written in load order, so the last write IS the winning
// state), the exact checks over it, the physical predicate a --base arm re-asks, and which dangles are findings (a mod on
// either end) rather than Bethesda's own floor. Moved out of Program.cs's top-level statements unchanged so the self-test
// can hold them to known answers (task 2026-10-14-probe-toplevel-decisions-testable).
sealed class DanglingLinks
{
    public readonly Dictionary<FormKey, int> TriCount = new();
    public readonly Dictionary<FormKey, (int Tri, FormKey Door)[]> DoorTris = new();
    public readonly Dictionary<FormKey, (FormKey Mesh, int Tri)[]> EdgeLinks = new();
    // A navmesh whose last word in load order takes it out (flagged deleted, or a record with no geometry): the plugin that
    // did it. Such a navmesh is not in the winning state, so a link into it names nothing, and its own earlier links are
    // gone with it (2026-11-16: before this, the earlier version's triangle count and links stood).
    public readonly Dictionary<FormKey, int> Deleter = new();
    // Winner-takes-all like the rest, but the PLUGIN is kept too: a dangling door link is a
    // disagreement between whoever last wrote the link and whoever last wrote the navmesh, and naming
    // both is what lets the patch verifier look the pair up.
    public readonly Dictionary<FormKey, (FormKey Nav, int Tri, int Plugin)> DoorNavLink = new();   // placed door -> its navmesh link
    public readonly HashSet<FormKey> RefExists = new();
    public readonly Dictionary<FormKey, List<int>> RefOverriders = new();   // placed ref -> every plugin that touches it
    public readonly HashSet<FormKey> RefGone = new();
    public long PermanentlyDisabledDoors;

    /// <summary>A navmesh record of plugin <paramref name="p"/> as plugin order reaches it: its triangle count, door triangles
    /// and edge links, or, deleted or without geometry, its removal from the winning state.</summary>
    public void NoteNavmesh(INavigationMeshGetter nm, int p)
    {
        if (nm.IsDeleted || nm.Data is null)
        {
            TriCount.Remove(nm.FormKey); DoorTris.Remove(nm.FormKey); EdgeLinks.Remove(nm.FormKey);
            Deleter[nm.FormKey] = p;
            return;
        }
        Deleter.Remove(nm.FormKey);
        if (nm.Data is { } nd)
        {
            TriCount[nm.FormKey] = nd.Triangles.Count;
            DoorTris[nm.FormKey] = nd.DoorTriangles.Select(d => ((int)d.TriangleBeforeDoor, d.Door.FormKey)).ToArray();
            EdgeLinks[nm.FormKey] = nd.EdgeLinks.Select(e => (e.Mesh.FormKey, (int)e.TriangleIndex)).ToArray();
        }
    }

    /// <summary>A placed object as plugin <paramref name="p"/> states it.</summary>
    public void NotePlaced(IPlacedObjectGetter po, int p)
    {
        if (po.IsDeleted) { RefGone.Add(po.FormKey); RefExists.Remove(po.FormKey); }
        else { RefExists.Add(po.FormKey); RefGone.Remove(po.FormKey); }
        // Every plugin that touches this door, not just the last. A dangling DOOR-* finding
        // names only one party, so there is no master PAIR for the patch verifier to look
        // up — but "who else already overrides this exact record" is answerable right here,
        // and it is the same question a fix would have to answer.
        if (!RefOverriders.TryGetValue(po.FormKey, out var ov)) RefOverriders[po.FormKey] = ov = new List<int>();
        if (ov.Count == 0 || ov[^1] != p) ov.Add(p);
        // WINNER-TAKES-ALL INCLUDES REMOVAL (2026-08-27, caught writing a field card): a
        // later override that DROPS NavigationDoorLink must erase the entry, or the vanilla
        // link goes stale in the model — door 0177FB read as a dangling link when its
        // winning override had removed the link entirely. Same for a door a city overhaul
        // permanently disables (enable-parent = the player ref with opposite-state, the
        // standard disable idiom): no NPC can ever use it, so its link belongs to no check.
        bool permanentlyDisabled = po.EnableParent is { } ep
            && ep.Reference.FormKey.ID == 0x14
            && ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
        if (po.NavigationDoorLink is { } dl && !dl.NavMesh.IsNull && !permanentlyDisabled)
            DoorNavLink[po.FormKey] = (dl.NavMesh.FormKey, dl.TeleportMarkerTriangle, p);
        else
        {
            DoorNavLink.Remove(po.FormKey);
            if (permanentlyDisabled) PermanentlyDisabledDoors++;
        }
    }

    public sealed record Found(List<string> Lines,
        List<(string Kind, FormKey Rec, string RecRole, FormKey Other, string OtherRole, int PluginA, int PluginB, string Summary, int? Tri)> Rows,
        int DoorTriChecked, int DoorLinkChecked, int EdgeChecked, int DoorIdxSuppressed);

    /// <summary>Every dangling link in the winning state. <paramref name="navOwner"/> names a navmesh's winning plugin
    /// (-1 if none); <paramref name="cellOf"/> labels a navmesh's cell for the printed line.</summary>
    public Found Find(Func<FormKey, int> navOwner, Func<FormKey, string> cellOf, bool reportDoorIdx)
    {
        var dangling = new List<string>();
        // The same findings, structurally, for --emit. Plugin indices are -1 where the detector genuinely
        // cannot name a second party (a link into a navmesh that is simply absent names no culprit).
        var dangleStruct = new List<(string Kind, FormKey Rec, string RecRole, FormKey Other, string OtherRole, int PluginA, int PluginB, string Summary, int? Tri)>();
        int doorTriChecked = 0, doorLinkChecked = 0, edgeChecked = 0;
        int doorIdxSuppressed = 0;

        foreach (var (nav, tris) in DoorTris)
        {
            if (!TriCount.TryGetValue(nav, out var count)) continue;
            foreach (var (tri, door) in tris)
            {
                doorTriChecked++;
                if (tri >= count)
                {
                    dangling.Add($"   DOOR-TRI   navmesh {nav} in {cellOf(nav)} points at triangle {tri} of {count}");
                    dangleStruct.Add(("DOOR-TRI", nav, "navmesh", door, "door", navOwner(nav), -1,
                                      $"navmesh {nav} in {cellOf(nav)} points at triangle {tri} of {count}", null));
                }
                else if (!door.IsNull && RefGone.Contains(door))
                {
                    dangling.Add($"   DOOR-GONE  navmesh {nav} in {cellOf(nav)} links door {door}, which is DELETED in the winning state");
                    dangleStruct.Add(("DOOR-GONE", nav, "navmesh", door, "door", navOwner(nav), -1,
                                      $"navmesh {nav} in {cellOf(nav)} links door {door}, deleted in the winning state", null));
                }
            }
        }

        foreach (var (door, link) in DoorNavLink)
        {
            doorLinkChecked++;
            if (!TriCount.TryGetValue(link.Nav, out var count))
            {
                string gone = Deleter.ContainsKey(link.Nav) ? "which is deleted in the winning state" : "which is not in the winning state";
                dangling.Add($"   DOOR-NAV   door {door} links navmesh {link.Nav}, {gone}");
                dangleStruct.Add(("DOOR-NAV", door, "door", link.Nav, "navmesh", link.Plugin, Deleter.GetValueOrDefault(link.Nav, -1),
                                  $"door {door} links navmesh {link.Nav}, {gone}", null));
            }
            else if (link.Tri >= count)
            {
                // RETIRED as a finding — field-tested and not player-visible. A door is described by THREE
                // independent structures, and this check reads only the first:
                //   1. the door reference's own navigation door link   (what this check validates)
                //   2. the navmesh's own list of the doors beside it
                //   3. the coarse routing index's linked-door list
                // Plus a runtime closed-door array this harness does not model at all. Structures 2 and 3
                // are what the engine walks, and they can be perfectly healthy while 1 dangles — the
                // observed failure is not an index that drifted by a few, but a link naming an unrelated
                // navmesh far away whose winning version carries no door entries at all. Traversal through
                // doors with this defect was confirmed working in BOTH directions in game, against a
                // control door carrying the identical defect. Independently: the large majority of these
                // links in a normal load order are Bethesda's own, and the most thorough bug-fix project
                // in the ecosystem edits hundreds of navmesh records while leaving every one of them
                // alone. Do not re-derive this; a re-scoped successor must show 2 and 3 failing too.
                doorIdxSuppressed++;
                if (reportDoorIdx)
                {
                    dangling.Add($"   DOOR-IDX   door {door} in {cellOf(link.Nav)} points at triangle {link.Tri} of {count}");
                    dangleStruct.Add(("DOOR-IDX", door, "door", link.Nav, "navmesh", link.Plugin, navOwner(link.Nav),
                                      $"door {door} in {cellOf(link.Nav)} points at triangle {link.Tri} of {count}", null));
                }
            }
        }

        foreach (var (nav, links) in EdgeLinks)
        {
            // One row per broken link, keyed by its target triangle: entries naming the same missing triangle are one row
            // that says how many (the target triangle is the link's identity; the table position renumbers with the source).
            var sameTri = new Dictionary<(FormKey Mesh, int Tri), (int Line, int Row, int N)>();
            foreach (var (mesh, tri) in links)
            {
                if (mesh.IsNull) continue;
                edgeChecked++;
                if (!TriCount.TryGetValue(mesh, out var count))
                {
                    string gone = Deleter.ContainsKey(mesh) ? "which is deleted in the winning state" : "which is not in the winning state";
                    dangling.Add($"   EDGE-NAV   navmesh {nav} in {cellOf(nav)} edge-links {mesh}, {gone}");
                    dangleStruct.Add(("EDGE-NAV", nav, "navmesh", mesh, "navmesh", navOwner(nav), Deleter.GetValueOrDefault(mesh, -1),
                                      $"navmesh {nav} in {cellOf(nav)} edge-links {mesh}, {gone}", null));
                }
                else if (tri >= count)
                {
                    string text = $"navmesh {nav} in {cellOf(nav)} edge-links {mesh} triangle {tri} of {count}";
                    if (sameTri.TryGetValue((mesh, tri), out var seen))
                    {
                        int n = seen.N + 1;
                        sameTri[(mesh, tri)] = (seen.Line, seen.Row, n);
                        dangling[seen.Line] = $"   EDGE-IDX   {text} ({n} links)";
                        dangleStruct[seen.Row] = dangleStruct[seen.Row] with { Summary = $"{text} ({n} links)" };
                        continue;
                    }
                    sameTri[(mesh, tri)] = (dangling.Count, dangleStruct.Count, 1);
                    dangling.Add($"   EDGE-IDX   {text}");
                    dangleStruct.Add(("EDGE-IDX", nav, "navmesh", mesh, "navmesh", navOwner(nav), navOwner(mesh), text, tri));
                }
            }
        }
        return new Found(dangling, dangleStruct, doorTriChecked, doorLinkChecked, edgeChecked, doorIdxSuppressed);
    }

    /// <summary>The physical half under this arm. <paramref name="tri"/> is an EDGE-IDX row's target triangle: with it the
    /// question is that one link; without it (a base row written before 2026-10-24) any link into the target.</summary>
    public (PhysicalState State, string Detail) Predicate(string kind, FormKey rec, FormKey other, int? tri = null)
    {
        switch (kind)
        {
            case "DOOR-TRI":
                if (!TriCount.TryGetValue(rec, out var dtCount)) return (PhysicalState.Gone, "the navmesh is not in the winning state");
                return DoorTris.TryGetValue(rec, out var dtris) && dtris.Any(t => t.Door == other && t.Tri >= dtCount)
                    ? (PhysicalState.Holds, $"the door triangle index still exceeds the winning count ({dtCount})")
                    : (PhysicalState.Gone, "the door triangle entry is gone or within the winning count");
            case "DOOR-GONE":
                return DoorTris.TryGetValue(rec, out var dgTris) && dgTris.Any(t => t.Door == other) && RefGone.Contains(other)
                    ? (PhysicalState.Holds, "the navmesh still links a door deleted in the winning state")
                    : (PhysicalState.Gone, "the door entry is gone, or the door exists again");
            case "DOOR-NAV":
                return DoorNavLink.TryGetValue(rec, out var dn) && dn.Nav == other && !TriCount.ContainsKey(other)
                    ? (PhysicalState.Holds, "the door still links a navmesh the winning state lacks")
                    : (PhysicalState.Gone, "the door's link was removed or re-pointed, or the navmesh is provided now");
            case "DOOR-IDX":
                return DoorNavLink.TryGetValue(rec, out var di) && di.Nav == other && TriCount.TryGetValue(other, out var diCount) && di.Tri >= diCount
                    ? (PhysicalState.Holds, $"the door's triangle index still exceeds the winning count ({diCount})")
                    : (PhysicalState.Gone, "the door's link is within the winning count, removed or re-pointed");
            case "EDGE-NAV":
                return EdgeLinks.TryGetValue(rec, out var en) && en.Any(x => x.Mesh == other) && !TriCount.ContainsKey(other)
                    ? (PhysicalState.Holds, "the edge link still points into a navmesh the winning state lacks")
                    : (PhysicalState.Gone, "the edge link is gone, or the target navmesh is provided now");
            case "EDGE-IDX":
                return EdgeLinks.TryGetValue(rec, out var ei) && TriCount.TryGetValue(other, out var eiCount) && ei.Any(x => x.Mesh == other && x.Tri >= eiCount && (tri is null || x.Tri == tri))
                    ? (PhysicalState.Holds, $"the edge link's triangle index still exceeds the target's winning count ({eiCount})")
                    : (PhysicalState.Gone, "the edge link is within the target's winning count, or gone");
            default:
                return (PhysicalState.Unmeasured, $"no physical predicate is defined for kind {kind}");
        }
    }

    /// <summary>A record is touched by a mod when any plugin that provides it is not vanilla-named.</summary>
    public static bool ModTouched(IReadOnlyList<int>? providers, Func<int, bool> isVanilla)
        => providers is not null && providers.Any(i => !isVanilla(i));

    /// <summary>A dangle is a finding when a mod touched either end; with neither, it is Bethesda's own floor.</summary>
    public static bool IsFinding(bool recTouched, bool otherTouched) => recTouched || otherTouched;
}
