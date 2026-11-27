using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Workbench;

/// <summary>What the records say about one reference a disable is asked for, each fact from its owner: the reference's
/// own record (its type, flags, enable parent, the door it leads to, whether it cuts the navmesh) and every record that
/// names it from the server's one reader of that (<see cref="IContextSource.NamesCountedAsync"/>, houseCARL's
/// <c>references=</c>); whether it is a large reference from <see cref="LargeReferenceLists"/>, the probes' and the
/// viewer's own rule (the union of every copy of the worldspace's list).</summary>
internal sealed record DisableFacts(string Key, string? RecordType, long RawFlags, bool Deleted, string? EnableParent, string? LeadsTo,
                                    bool Navcut, string? LargeIn, IReadOnlyList<(string Key, string Field)> NamedBy, int NamedNotListed);

/// <summary>Whether a reference may be disabled, and the one sentence when not: what depends on it, named (with a count when
/// many), and what to try. Gate B refuses; there is no "disable anyway". A reference that cuts the navmesh is allowed, and
/// <see cref="Note"/> says that the cut opens.</summary>
internal static class DisableRule
{
    internal const long InitiallyDisabled = 0x800, Persistent = 0x400, DeletedBit = 0x20;

    /// <summary>The facts, read off the UI thread through the scene's context source.</summary>
    internal static async Task<DisableFacts> ReadAsync(IContextSource src, FormKey key)
    {
        var own = await src.DisableFactsAsync(key).ConfigureAwait(false);
        var (names, more) = await src.NamesCountedAsync(key).ConfigureAwait(false);
        return own with { NamedBy = names, NamedNotListed = more };
    }

    const string Try = "try moving it instead, or fix the conflict on the other object";

    static string List(IReadOnlyList<string> items, int notListed)
    {
        int n = items.Count + notListed;
        var shown = items.Take(5).ToList();
        var s = string.Join(", ", shown);
        if (n > shown.Count) s += $" and {n - shown.Count} more";
        return n > 1 ? $"{n} records ({s})" : s;
    }

    /// <summary>The word for how a record names it, from the naming record's type and field.</summary>
    /// <summary>The field names a placed referrer's answer gives (comma-separated), none for a record of another type or a
    /// placed referrer whose field the read did not find.</summary>
    static readonly string[] PlacedFields = { "EnableParent", "LinkedReferences", "ActivateParents", "TeleportDestination", "VirtualMachineAdapter" };

    static string[] Tokens(string field)
    {
        var t = field.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return t.Length > 0 && t.All(x => PlacedFields.Contains(x)) ? t : Array.Empty<string>();
    }

    /// <summary>How a record names it, in words, from the reader's answer: a placed referrer's fields, else the naming
    /// record's type and EditorID.</summary>
    internal static string How(string field)
    {
        var t = Tokens(field);
        if (t.Length > 0)
            return string.Join(" and ", t.Select(x => x switch
            {
                "EnableParent" => "its enable parent",
                "LinkedReferences" => "its linked reference",
                "ActivateParents" => "its activate parent",
                "TeleportDestination" => "the door it leads to",
                _ => "a script property",
            }));
        if (field.StartsWith("Quest", StringComparison.Ordinal)) return "quest " + field[5..].Trim() + ", in an alias or a script property";
        if (field.StartsWith("Package", StringComparison.Ordinal)) return "package " + field[7..].Trim() + ", as its location or target";
        if (field.StartsWith("Cell", StringComparison.Ordinal)) return "cell " + field[4..].Trim() + ", through a reference filed in it";
        if (field.StartsWith("PlacedObject (", StringComparison.Ordinal) || field.StartsWith("PlacedNpc (", StringComparison.Ordinal))
            return "a field of it this read does not name";
        return field;
    }

    /// <summary>Null when the reference may be disabled, else the one sentence the stage is refused with.</summary>
    internal static string? Refusal(DisableFacts f)
    {
        if (f.RecordType is { } t && t != "PlacedObject")
            return t == "PlacedNpc"
                ? $"{f.Key} is an actor, not a placed object, and this fix disables placed objects only; try moving the other object of the conflict."
                : $"{f.Key} is a {t}, not a placed object, and this fix disables placed objects only; try moving the other object of the conflict.";
        if (f.Deleted || (f.RawFlags & DeletedBit) != 0)
            return $"{f.Key} is deleted in the load order already, so there is nothing to disable; try reopening the scene, as it may be out of date.";
        if ((f.RawFlags & InitiallyDisabled) != 0)
            return $"{f.Key} is already initially disabled in the load order, so a disable changes nothing; try reopening the scene, as it may be out of date.";
        if (f.EnableParent is { } ep)
            return $"{f.Key} is switched on and off by its enable parent {ep}, so its own initially-disabled flag does nothing in game; {Try}.";
        if (f.LeadsTo is { } far)
            return $"{f.Key} is a door that leads to {far}, and disabling it cuts that way through; {Try}.";
        if (f.LargeIn is { } ws)
            return $"{f.Key} is a large reference (on worldspace {ws}'s large-reference list), and overriding a large reference from a patch plugin makes it flicker in game; {Try}.";
        // every record that names it, the cell answer kept only when no reference in the same answer names it (a cell is
        // credited through a reference filed in it, so the reference already says why)
        var named = f.NamedBy.Where(n => !n.Field.StartsWith("Cell", StringComparison.Ordinal) || !f.NamedBy.Any(o => IsPlacedField(o.Field))).ToList();
        if (named.Count == 0 && f.NamedNotListed == 0) return null;
        var parents = named.Where(n => Tokens(n.Field).Contains("EnableParent")).Select(n => n.Key).ToList();
        if (parents.Count > 0)
            return $"{f.Key} is the enable parent of {List(parents, 0)}, which would be switched off with it; {Try}.";
        var said = named.Select(n => $"{n.Key} ({How(n.Field)})").ToList();
        return $"{f.Key} is named by {List(said, f.NamedNotListed)}, which would still point at it once it is disabled; {Try}.";
    }

    static bool IsPlacedField(string field) => Tokens(field).Length > 0 || field.StartsWith("PlacedObject (", StringComparison.Ordinal) || field.StartsWith("PlacedNpc (", StringComparison.Ordinal);

    /// <summary>The sentence an allowed disable adds: the navmesh cut opens.</summary>
    internal static string? Note(DisableFacts f) => f.Navcut
        ? $"{f.Key} cuts the navmesh; disabled, the cut opens, and NPCs may walk where it stood."
        : null;

    /// <summary>The limits of what is read, said in the Disable button's tooltip and the disable verb's description.</summary>
    internal const string Limits = "What names it is read from the winning records (links, aliases, packages and script properties); "
        + "a script that finds it at run time, a package that picks furniture by type, and LOD built from it are not read.";
}

/// <summary>The button's and the <c>disable</c> verb's one path: read the facts, refuse in one sentence, else stage.</summary>
internal static class DisableAction
{
    internal static async Task<string> StageAsync(IWorkbenchScene sc, FormKey key)
    {
        if (!sc.Session.IsDrawn(key)) throw new InvalidOperationException($"{key} is not a drawn reference in this scene; select one the window shows.");
        if (sc.ContextSource is not { } src)
            throw new InvalidOperationException("this scene has no write target to read what depends on the object from; open it from the launcher's profile.");
        if (await src.MismatchAsync(key) is { } why) throw new InvalidOperationException(why);
        var f = await DisableRule.ReadAsync(src, key);
        if (DisableRule.Refusal(f) is { } no) throw new InvalidOperationException(no);
        sc.Session.StageDisable(key);
        sc.AfterStage();
        return $"staged: disable {key} (written initially disabled; nothing is written until commit)."
               + (DisableRule.Note(f) is { } note ? " " + note : "");
    }
}

/// <summary>The written disable read back by Mutagen, not by the writer: the patch's record of the reference against the
/// winner's in the load order (the plugins before the patch): its flags are the winner's with 0x800 added and nothing
/// lost, it sits in the same group of its cell (persistent or temporary), and every other field is equal, numbers
/// compared as numbers (houseCARL writes a rotation's -0 as +0). A failure throws: it is an error, not a log line.</summary>
internal static class DisableReadBack
{
    internal static void Check(IReadOnlyList<string> orderPaths, string patchPath, FormKey key)
    {
        using var patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        var name = Path.GetFileName(patchPath);
        var written = patch.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == key)
            ?? throw new InvalidOperationException($"{name} holds no override of {key} after the write; remove the patch and write again.");
        foreach (var path in orderPaths.Reverse())
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(patchPath), StringComparison.OrdinalIgnoreCase)) continue;
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            if (mod.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == key) is not { } winner) continue;
            long want = winner.MajorRecordFlagsRaw | (int)DisableRule.InitiallyDisabled, got = written.MajorRecordFlagsRaw;
            if (got != want)
                throw new InvalidOperationException($"{key}'s flags read back from {name} as 0x{got:X}, not the winner's 0x{winner.MajorRecordFlagsRaw:X} with 0x800 added"
                    + ((want & ~got) is var lost and not 0 ? $" (bits lost: 0x{lost:X})" : "") + "; remove the patch and write again.");
            string wg = GroupOf(mod, key), pg = GroupOf(patch, key);
            if (wg != pg)
                throw new InvalidOperationException($"{key}'s override in {name} is filed in its cell's {pg} group, but the winner in {Path.GetFileName(path)} is in the {wg} group; remove the patch and write again.");
            var a = (PlacedObject)winner.DeepCopy(); a.MajorRecordFlagsRaw = (int)want;
            if (Differences(a, (PlacedObject)written.DeepCopy()) is { Count: > 0 } diff)
                throw new InvalidOperationException($"{key}'s override in {name} differs from the winner in {Path.GetFileName(path)} beyond the flag ({string.Join(", ", diff)}); remove the patch and write again.");
            return;
        }
        throw new InvalidOperationException($"no plugin of the load order holds {key}, so its disable cannot be read back against a winner.");
    }

    /// <summary>The group of its cell a reference is filed in: persistent, temporary, or none.</summary>
    internal static string GroupOf(ISkyrimModGetter mod, FormKey key)
    {
        foreach (var c in mod.EnumerateMajorRecords<ICellGetter>())
        {
            if (c.Persistent.Any(r => r.FormKey == key)) return "persistent";
            if (c.Temporary.Any(r => r.FormKey == key)) return "temporary";
        }
        return "none";
    }

    /// <summary>The names of the fields two copies of a reference differ in, numbers compared as numbers.</summary>
    internal static IReadOnlyList<string> Differences(PlacedObject winner, PlacedObject written)
    {
        var diff = new List<string>();
        foreach (var p in typeof(PlacedObject).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            object? x = p.GetValue(winner), y = p.GetValue(written);
            if (!Equal(x, y)) diff.Add(p.Name);
        }
        return diff;
    }

    static bool Equal(object? x, object? y)
    {
        if (x is null || y is null) return x is null && y is null;
        if (x is float fx && y is float fy) return fx == fy;                       // -0 == +0
        if (x is Noggog.P3Float px && y is Noggog.P3Float py) return px.X == py.X && px.Y == py.Y && px.Z == py.Z;
        if (x is Placement plx && y is Placement ply)
            return Equal(plx.Position, ply.Position) && Equal(plx.Rotation, ply.Rotation);
        if (x is System.Collections.IEnumerable ex && y is System.Collections.IEnumerable ey && x is not string)
        {
            var a = ex.Cast<object?>().ToList(); var b = ey.Cast<object?>().ToList();
            return a.Count == b.Count && a.Zip(b).All(p => Equal(p.First, p.Second));
        }
        return x.Equals(y);
    }
}

/// <summary>What a re-check says after a disable: the open row, every other row naming the disabled object, and every row
/// the patch created (in the row's own probe and in contactprobe, which judges what rested on the object).</summary>
internal sealed record DisableRecheckResult(RowVerdict Row, IReadOnlyList<RowVerdict> Others, IReadOnlyList<RowVerdict> New);

/// <summary>The re-check's words after a disable, every one from the frozen rule (<see cref="RecheckRule"/>): a row reads
/// "resolved" only when its predicate was re-measured Gone under the patch (step 1 measured that a disabled object's rows
/// carry "Gone: a ref is disabled at runtime under this state"); a row merely absent from the patched run is not credited.
/// Anything the disable strands is a created contact row, shown as new, never hidden.</summary>
internal static class DisableRecheck
{
    internal static DisableRecheckResult Of(DifferentialRows.Credited overlap, DifferentialRows.Credited? contact, IReadOnlyCollection<string> keys, string openId)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        bool Names(DifferentialRows.Row r) => set.Contains(r.FormKey) || (r.Other is { } o && set.Contains(o));
        var others = overlap.Outcome.Keys
            .Where(id => id != openId && ((overlap.Before.TryGetValue(id, out var b) && Names(b)) || (overlap.After.TryGetValue(id, out var a) && Names(a))))
            .OrderBy(id => id, StringComparer.Ordinal).Select(id => RecheckRule.Of(overlap, id)).ToList();
        var news = RecheckRule.NewRows(overlap).Concat(contact is null ? Array.Empty<RowVerdict>() : RecheckRule.NewRows(contact)).ToList();
        return new DisableRecheckResult(RecheckRule.Of(overlap, openId), others, news);
    }
}
