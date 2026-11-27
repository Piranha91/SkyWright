using System.Numerics;
using System.Runtime.CompilerServices;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>The server's answers item 5 composes, each from its owner: doors near a reference, what names it, a door's
/// other side, a reference's base record type and enable parent, and the commit's placement check (F4).</summary>
internal interface IContextSource
{
    /// <summary>Doors (references whose record has a TeleportDestination) within <paramref name="within"/> units of the reference.</summary>
    Task<IReadOnlyList<string>> DoorsNearAsync(FormKey key, double within);
    /// <summary>Every record that names the reference, with the field that names it.</summary>
    Task<IReadOnlyList<(string Key, string Field)>> NamesAsync(FormKey key);
    /// <summary>The doors near it and how many more the server matched than it listed.</summary>
    async Task<(IReadOnlyList<string> Doors, int NotListed)> DoorsNearCountedAsync(FormKey key, double within) => (await DoorsNearAsync(key, within), 0);
    /// <summary>The doors near it, how many more matched than were listed, and one sentence when a plugin could not be read.</summary>
    async Task<(IReadOnlyList<string> Doors, int NotListed, string? Unread)> DoorsNearReadAsync(FormKey key, double within)
    {
        var (doors, notListed) = await DoorsNearCountedAsync(key, within);
        return (doors, notListed, null);
    }
    /// <summary>What names it and how many more the server matched than it listed.</summary>
    async Task<(IReadOnlyList<(string Key, string Field)> Names, int NotListed)> NamesCountedAsync(FormKey key) => (await NamesAsync(key), 0);
    /// <summary>A door's other side: the far door and its cell, and the doorstep a player coming out through this door arrives at
    /// (the far door's own destination, which stays where it is when this door moves).</summary>
    Task<(string? FarDoor, string? FarCell, Vec3? Arrival)> DestinationAsync(string door);
    /// <summary>A reference's base record type, as the server's summary names it.</summary>
    Task<string?> BaseTypeAsync(string key);
    /// <summary>The reference's own enable parent, if it has one.</summary>
    Task<string?> EnableParentAsync(FormKey key);
    /// <summary>The reference's own facts a disable is judged on (<see cref="DisableRule"/>); what names it is read apart.</summary>
    Task<DisableFacts> DisableFactsAsync(FormKey key) => throw new InvalidOperationException("this scene's context source cannot read what a disable depends on.");
    /// <summary>Why the server's record for the reference is not the one drawn (F4), or null when it is.</summary>
    Task<string?> MismatchAsync(FormKey key);
}

internal sealed record DoorFact(string Key, string Label, string? FarDoor, string? FarCell, Vec3? Arrival);
internal sealed record NameFact(string Key, string Field);
internal sealed record RestFact(string Key, DependentMotion Motion, string Sentence);
internal sealed record NavFact(string Navmesh, int Triangles);

/// <summary>What item 5 shows for one reference. A section that could not be read, or found nothing, says so in
/// <see cref="Sections"/>.</summary>
internal sealed record ContextAnswer(string Key, IReadOnlyList<DoorFact> Doors, IReadOnlyList<NameFact> Names, string? EnableParent,
                                     IReadOnlyList<string> Touches, IReadOnlyList<RestFact> Resting, IReadOnlyList<NavFact> Navmesh,
                                     IReadOnlyDictionary<string, string> Sections, string Statement, Aabb? Box = null);

/// <summary>Item 5: the context of a selected reference, composed from the server, the live scene's contact owner, the motion
/// rule and the scene's navmesh triangles. Read off the UI thread; shown, never judged.</summary>
internal static class ContextRead
{
    /// <summary>The search radius for doors; contact decides the label.</summary>
    internal const double DoorRadius = 256;

    internal const string NavRule = "navmesh triangles under its axis-aligned bounds, any height: shown, not judged here; a navmesh row's repair "
                                    + "(relink a broken border, or move vertices in height to follow ground a later plugin moved) is previewed and written "
                                    + "to a new patch from the row; navmesh is never moved sideways, re-cut or generated";

    /// <summary>The sections a read fills, said "reading…" while it runs.</summary>
    internal static readonly string[] SectionNames = { "doors", "names", "enableParent", "touches", "resting", "navmesh" };

    internal static async Task<ContextAnswer> ReadAsync(IContextSource src, CaptureViews.Geometry g, string key,
        IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> navTris,
        Func<string, IEnumerable<(string Layer, string MotionSystem)>?> bodiesOf, IReadOnlyList<string>? dependencyRows = null,
        CaptureViews.WorkCounter? counter = null, CancellationToken ct = default)
    {
        if (!FormKey.TryFactory(key, out var fk)) throw new InvalidOperationException($"'{key}' is not a reference key (XXXXXX:Plugin.esp).");
        // F4: an answer about a drawn reference whose record the server places elsewhere is refused, and nothing is shown
        if (await src.MismatchAsync(fk).ConfigureAwait(false) is { } why) throw new InvalidOperationException(why);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Msg(Exception ex) => ex.Message.TrimEnd('.');
        static string More(int n) => n > 0 ? $"; {n} more not listed (one read lists at most {ServerContextSource.Cap})" : "";

        // the geometry half: one contact read of the reference, then the support rays of what touches it
        var (contacts, restingOn) = await Task.Run(() =>
        {
            var c = CaptureViews.Contacts(g, key, counter);
            var on = c.Refusal is null ? CaptureViews.RestingOn(g, key, c.Touches, counter) : Array.Empty<string>();
            return (c, on);
        }, ct).ConfigureAwait(false);
        if (contacts.Refusal is { } cr)
            sections["touches"] = cr == "absent" ? $"{key} is not drawn in this scene, so what touches or rests on it is not assessed" : $"what touches it is not assessed: {cr}";
        bool Drawn(string k) => g.Refs.TryGetValue(k, out var r) && r.I.Count > 0;

        var doors = new List<DoorFact>();
        try
        {
            var (near, doorsNotListed, unread) = await src.DoorsNearReadAsync(fk, DoorRadius).ConfigureAwait(false);
            foreach (var d in near)
            {
                if (string.Equals(d, key, StringComparison.OrdinalIgnoreCase)) continue;
                string label = !Drawn(d) || contacts.Refusal is not null ? "near only (not drawn here, contact not assessed)"
                             : contacts.Touches.Contains(d) ? "touches it" : "near only";
                var (far, farCell, arrival) = await src.DestinationAsync(d).ConfigureAwait(false);
                doors.Add(new DoorFact(d, label, far, farCell, arrival));
            }
            sections["doors"] = (doors.Count == 0 ? $"no door within {DoorRadius:0} units"
                : $"{doors.Count} door(s) within {DoorRadius:0} units, nearest first; \"touches it\" is the contact read's, not the distance's") + More(doorsNotListed)
                + (unread is null ? "" : "; " + unread);
        }
        catch (Exception ex) { sections["doors"] = $"the doors within {DoorRadius:0} units could not be read: {Msg(ex)}"; }

        var names = new List<NameFact>();
        try
        {
            var (named, namesNotListed) = await src.NamesCountedAsync(fk).ConfigureAwait(false);
            names.AddRange(named.Select(n => new NameFact(n.Key, n.Field)));
            sections["names"] = (names.Count == 0 ? "nothing names it" : $"{names.Count} record(s) name it") + More(namesNotListed);
        }
        catch (Exception ex) { sections["names"] = $"what names it could not be read: {Msg(ex)}"; }

        string? parent = null;
        try { parent = await src.EnableParentAsync(fk).ConfigureAwait(false); sections["enableParent"] = parent is null ? "it has no enable parent" : $"its enable parent is {parent}"; }
        catch (Exception ex) { sections["enableParent"] = $"its enable parent could not be read: {Msg(ex)}"; }

        var resting = new List<RestFact>();
        foreach (var r in restingOn)
        {
            string? type = null;
            try { type = await src.BaseTypeAsync(r).ConfigureAwait(false); } catch (Exception ex) { sections["resting"] = $"a resting object's record type could not be read: {Msg(ex)}"; }
            var motion = DetectorModel.ClassifyMotion(type, bodiesOf(r));
            string name = g.Refs.TryGetValue(r, out var gr) ? gr.Name : r;
            resting.Add(new RestFact(r, motion, motion switch
            {
                DependentMotion.Movable => $"{r} ({name}) rests on it and falls when the cell loads if this moves",
                DependentMotion.Fixed => $"{r} ({name}) rests on it and stays in the air if this moves",
                _ => $"{r} ({name}) rests on it; what physics does with it at load is unknown (record type {type ?? "not read"})",
            }));
        }
        if (!sections.ContainsKey("resting")) sections["resting"] = resting.Count == 0 ? "nothing rests on it" : $"{resting.Count} object(s) rest on it";

        var box = contacts.Box.IsEmpty && g.Refs.TryGetValue(key, out var own) ? own.Box : contacts.Box;
        var nav = box.IsEmpty ? (IReadOnlyList<NavFact>)Array.Empty<NavFact>() : NavUnder(navTris, box);
        sections["navmesh"] = (nav.Count == 0 ? "no " : $"{nav.Sum(n => n.Triangles)} ") + NavRule;

        var statement = "A dependent does not move with its host: nothing listed here moves when this moves unless it is ticked to move with it; "
            + (dependencyRows is null ? "the declared-dependencies check was not run on this bundle."
               : dependencyRows.Count == 0 ? "the bundle's declared-dependencies check names no row for it."
               : "the bundle's declared-dependencies check names: " + string.Join("; ", dependencyRows) + ".");
        return new ContextAnswer(key, doors, names, parent, contacts.Touches.OrderBy(t => t, StringComparer.Ordinal).ToList(), resting, nav, sections, statement,
                                 box.IsEmpty ? null : box);
    }

    /// <summary>The navmesh triangles under the reference's axis-aligned bounds (XY, any height), counted by navmesh.</summary>
    internal static IReadOnlyList<NavFact> NavUnder(IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> navTris, Aabb box)
        => TrianglesUnder(navTris, box).GroupBy(t => t.Key).Select(gr => new NavFact(gr.Key.ToString(), gr.Count())).OrderBy(n => n.Navmesh, StringComparer.Ordinal).ToList();

    /// <summary>The triangles themselves, for the view's highlight.</summary>
    internal static IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> TrianglesUnder(IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> navTris, Aabb box)
        => navTris.Where(t => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) >= box.Min.X && MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) <= box.Max.X
                           && MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) >= box.Min.Y && MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)) <= box.Max.Y);

    /// <summary>The contact read in words: "crosses it" for a crossing, "is within 8 units of it" for a near touch.</summary>
    internal static IReadOnlyList<string> ContactWords(CaptureViews.ContactAnswer c, CaptureViews.Geometry g)
        => c.Touches.OrderBy(t => t, StringComparer.Ordinal).Select(t =>
        {
            string who = t == CaptureViews.TerrainKey ? "the terrain" : $"{t} ({(g.Refs.TryGetValue(t, out var r) ? r.Name : t)})";
            return c.Crossing.Contains(t) ? $"{who} crosses it" : $"{who} is within {CaptureViews.TouchTolerance:0} units of it";
        }).ToList();

    static string P(Vec3 v) => FormattableString.Invariant($"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})");

    /// <summary>The commit's sentence: what moves with the reference and what stays behind.</summary>
    /// <summary>For a nudged object (Gate C): what the drawn shapes show resting on it was judged by the nudge's proof with
    /// placement contact (collision), which left nothing in the air; it is not said to stay in the air.</summary>
    internal static string NudgedResting(IEnumerable<string> keys) =>
        $"What the drawn shapes show resting on it ({string.Join(", ", keys)}) was judged by the nudge's proof with placement contact, which found nothing left in the air.";

    internal static string StaysBehind(ContextAnswer a, IReadOnlyCollection<string> movingWith, bool nudged = false)
    {
        var moving = new HashSet<string>(movingWith, StringComparer.OrdinalIgnoreCase);
        var stay = new List<string>();
        foreach (var d in a.Doors.Where(d => !moving.Contains(d.Key)))
            stay.Add($"door {d.Key} ({d.Label})" + (d.FarDoor is not null ? $", whose far side {d.FarDoor} is unchanged" : ""));
        foreach (var n in a.Names.Where(n => !moving.Contains(n.Key))) stay.Add($"{n.Key}, which names it as {n.Field}");
        var resting = a.Resting.Where(r => !moving.Contains(r.Key)).ToList();
        if (!nudged) foreach (var r in resting) stay.Add(r.Sentence);
        var said = new List<string>();
        said.Add(moving.Count == 0 ? "Nothing moves with it on its own." : $"Moving with it by the same move: {string.Join(", ", moving.OrderBy(x => x, StringComparer.Ordinal))}.");
        said.Add(stay.Count == 0 ? "Nothing near it, naming it or resting on it stays behind." : "These stay behind: " + string.Join("; ", stay) + ".");
        if (nudged && resting.Count > 0) said.Add(NudgedResting(resting.Select(r => r.Key)));
        foreach (var d in a.Doors.Where(d => moving.Contains(d.Key) && d.FarDoor is not null))
            said.Add($"Door {d.Key} moves with it, but its far side {d.FarDoor} in cell {d.FarCell ?? "unknown"} still holds the old arrival point "
                     + (d.Arrival is { } p ? P(p) : "(not read)") + ", so a player coming through it arrives at the old doorstep; this write does not carry the far door's TeleportDestination.");
        said.Add("Navmesh is read and shown only: the navmesh under it, and any door's navmesh door triangle, stay as they are.");
        return string.Join(" ", said);
    }
}

/// <summary>Item 5: stage the host's rigid move, from its load-order place to its staged pose, for each named reference.
/// The move_with verb and the context panel's "move ticked with it" both call this.</summary>
internal static class MoveWithRule
{
    internal static string Apply(IWorkbenchScene sc, FormKey host, IReadOnlyList<FormKey> refs)
    {
        var hb = RefPlacementEdit.BakedPose(sc.Session.RefOf(host).Ref);
        var ha = sc.Session.Current(host);
        if (hb == ha) throw new InvalidOperationException($"{host} has no staged move; stage its move first, then move the others with it.");
        foreach (var k in refs)
        {
            if (k == host) throw new InvalidOperationException($"{k} is the selected reference itself.");
            if (!sc.Session.IsDrawn(k)) throw new InvalidOperationException($"{k} is not drawn in this scene, so it cannot be moved with {host}.");
        }
        foreach (var k in refs) sc.Session.Stage(k, MoveRules.Carry(hb, ha, RefPlacementEdit.BakedPose(sc.Session.RefOf(k).Ref)));
        sc.Session.Select(host);
        sc.AfterStage();
        ContextCache.AddMovedWith(sc, host.ToString(), refs.Select(k => k.ToString()));
        return $"moved {refs.Count} reference(s) with {host} by its move from its load-order place: {string.Join(", ", refs)}; nothing is written until commit";
    }
}

/// <summary>Item 5's reads per scene and reference, and which references were moved with which host, kept beside the scene.</summary>
internal static class ContextCache
{
    sealed class Of { internal readonly Dictionary<string, Task<ContextAnswer>> Reads = new(StringComparer.OrdinalIgnoreCase);
                      internal readonly Dictionary<string, HashSet<string>> MovedWith = new(StringComparer.OrdinalIgnoreCase); }
    static readonly ConditionalWeakTable<IWorkbenchScene, Of> Table = new();

    /// <summary>The reference's context read, started once per scene (off the UI thread).</summary>
    internal static Task<ContextAnswer>? Read(IWorkbenchScene sc, string key)
    {
        if (sc.ContextSource is not { } src) return null;
        var c = Table.GetOrCreateValue(sc);
        if (c.Reads.TryGetValue(key, out var t)) return t;
        // the geometry is a copy of the shapes near the reference, taken here on the UI thread; the read runs on the pool.
        // The reference stands at its load-order place (the build's own vertices), so what rests on or touches it is read
        // where it is in the game, whatever move is staged.
        var g = new CaptureViews.Geometry();
        foreach (var s in Near(sc.Shapes, key, 600f))
        {
            if (s.Kind == PickKind.Terrain) g.Add(CaptureViews.TerrainKey, CaptureViews.TerrainKey, s.Verts, s.Tris);
            else if (string.Equals(s.Ref.RefKey.ToString(), key, StringComparison.OrdinalIgnoreCase)) g.Add(key, s.Ref.BaseEditorId ?? key, RefPlacementEdit.BakedVerts(s), s.Tris);
            else if (!s.Ref.RefKey.IsNull) g.Add(s.Ref.RefKey.ToString(), s.Ref.BaseEditorId ?? s.Ref.RefKey.ToString(), s.Verts, s.Tris);
        }
        var nav = sc.NavTris.ToList();
        var rows = sc.DependencyRows(key);
        return c.Reads[key] = Task.Run(() => ContextRead.ReadAsync(src, g, key, nav, sc.BodiesOf, rows));
    }

    internal static IReadOnlyCollection<string> MovedWith(IWorkbenchScene sc, string host)
        => Table.TryGetValue(sc, out var c) && c.MovedWith.TryGetValue(host, out var m) ? m : Array.Empty<string>();

    internal static void AddMovedWith(IWorkbenchScene sc, string host, IEnumerable<string> refs)
    {
        var c = Table.GetOrCreateValue(sc);
        if (!c.MovedWith.TryGetValue(host, out var m)) c.MovedWith[host] = m = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs) m.Add(r);
    }

    /// <summary>The pick shapes within <paramref name="margin"/> (Y-up units) of the reference's own shapes, terrain included.</summary>
    static IEnumerable<PickShape> Near(IReadOnlyList<PickShape> shapes, string key, float margin)
    {
        var own = shapes.Where(s => s.Kind == PickKind.Ref && string.Equals(s.Ref.RefKey.ToString(), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count == 0) return Array.Empty<PickShape>();
        var at = own.SelectMany(s => RefPlacementEdit.BakedVerts(s)).ToList();             // around its load-order place
        var min = at.Aggregate(Vector3.Min) - new Vector3(margin);
        var max = at.Aggregate(Vector3.Max) + new Vector3(margin);
        return shapes.Where(s => (s.Kind == PickKind.Ref || s.Kind == PickKind.Terrain)
                                 && s.Max.X >= min.X && s.Min.X <= max.X && s.Max.Y >= min.Y && s.Min.Y <= max.Y && s.Max.Z >= min.Z && s.Min.Z <= max.Z);
    }
}
