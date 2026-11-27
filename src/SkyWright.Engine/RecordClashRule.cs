namespace SkyWright.Engine;

// BETWEEN MODS, for the record checks (recordprobe): which mod a plugin belongs to, and when a record fact is a clash between
// two mods rather than one mod's own defect or the game's. This class owns the rule and every reason; recordprobe reads
// houseCARL's answers and asks. The game's own files (Bethesda's and Creation Club's) are named by the caller, which owns
// that list (HarnessShared.BaseGamePlugins).
//   a plugin's mod   the MO2 mod folder it sits in; a plugin anywhere else (the game's Data folder, MO2's overwrite) is a
//                    mod of its own (Gate A review finding 2);
//   dangling links   a link into a record its plugin does not have is a row only between two mods: judged when either
//                    plugin is in the selection (Gate A review finding 1); not a row into a plugin missing from the order
//                    (the masters check's), in the game's own files or into them, into its own plugin or its own mod.
//   lost edits       a field one mod changed whose winning value is another mod's (LostEditRule): a row only between two
//                    mods, judged when either plugin is in the selection (C5), neither a game file, the winner not made
//                    with the loser (its masters, transitively: Masters).
//   missing assets   a file a winning record names that the game does not find although a mod the record's plugin is
//                    made with (or its own mod) ships it, because another mod's archive of the same name wins (MissingAsset):
//                    judged when the record's plugin, the mod that ships the file or the mod whose archive wins is in the
//                    selection; not a row when the record is the game's own, or when no such mod ships it (one mod's own
//                    defect, or a mod it needs is not installed; an archive of its own mod that hides the file reads so).

/// <summary>One end of a record link: the plugin, its mod, whether it is one of the game's own files, whether it is in the
/// selection the scan judges, and whether it is in the load order at all.</summary>
public sealed record LinkEnd(string Plugin, string Mod, bool GameFile, bool Selected, bool Loaded = true);

/// <summary>What the rule says about one link: a row (between two mods) or no row, with the reason it is counted under.</summary>
public sealed record RecordVerdict(bool Row, string Reason);

public static class RecordClashRule
{
    public const string BetweenMods = "between two mods";
    public const string NotSelected = "neither plugin is in the selection";
    public const string InGameFiles = "in the game's own files";
    public const string IntoGameFiles = "points into the game's own files: one mod against the game's version";
    public const string SamePlugin = "points into its own plugin: one plugin's own defect";
    public const string SameMod = "points into another plugin of its own mod: one mod's own defect";
    public const string NotLoaded = "points into a plugin that is not in the load order: the masters check's";

    /// <summary>The mod a plugin belongs to: "folder:&lt;name&gt;" for a plugin in an MO2 mod folder under
    /// <paramref name="modsDir"/>, else "plugin:&lt;file name&gt;" (a mod of its own).</summary>
    public static string ModOf(string pluginPath, string modsDir)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(pluginPath)) ?? "";
        string parent = Path.GetDirectoryName(dir) ?? "";
        bool inModFolder = parent.Length > 0 && string.Equals(parent.TrimEnd('\\', '/'), Path.GetFullPath(modsDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        return inModFolder ? "folder:" + Path.GetFileName(dir) : "plugin:" + Path.GetFileName(pluginPath);
    }

    /// <summary>A mod key in words: the folder's name, or the plugin's own name.</summary>
    public static string ModName(string mod) => mod.Contains(':') ? mod[(mod.IndexOf(':') + 1)..] : mod;

    /// <summary>A dangling link from <paramref name="holder"/> (the plugin whose record carries it) into a record
    /// <paramref name="target"/>'s plugin should define and does not.</summary>
    public static RecordVerdict Dangling(LinkEnd holder, LinkEnd target)
    {
        if (!target.Loaded) return new(false, NotLoaded);
        if (!holder.Selected && !target.Selected) return new(false, NotSelected);
        if (holder.GameFile) return new(false, InGameFiles);
        if (target.GameFile) return new(false, IntoGameFiles);
        if (string.Equals(holder.Plugin, target.Plugin, StringComparison.OrdinalIgnoreCase)) return new(false, SamePlugin);
        if (string.Equals(holder.Mod, target.Mod, StringComparison.OrdinalIgnoreCase)) return new(false, SameMod);
        return new(true, BetweenMods);
    }

    public const string GameFileSide = "one of the two is the game's own file";
    public const string SameModEdit = "both are plugins of one mod: one mod's own business";
    public const string MadeWith = "the winner was made with the plugin it overrides, so its value is the later one by design";

    /// <summary>A lost edit, the <paramref name="loser"/>'s change under the <paramref name="winner"/>'s value: a row only
    /// between two mods. Judged when either plugin is in the selection (C5); not a row when either is one of the game's own
    /// files, when both are in one mod, or when the winner was made with the loser (lists it among its masters, transitively).</summary>
    public static RecordVerdict LostEdit(LinkEnd loser, LinkEnd winner, bool winnerMadeWithLoser)
    {
        if (!loser.Selected && !winner.Selected) return new(false, NotSelected);
        if (loser.GameFile || winner.GameFile) return new(false, GameFileSide);
        if (string.Equals(loser.Mod, winner.Mod, StringComparison.OrdinalIgnoreCase)) return new(false, SameModEdit);
        if (winnerMadeWithLoser) return new(false, MadeWith);
        return new(true, BetweenMods);
    }

    public const string AssetNamedByGameFile = "named by a record of the game's own files: the game's version";
    public const string AssetOwnDefect = "no mod the record's plugin is made with ships the file, nor its own mod: one mod's own defect, or a mod it needs is not installed";
    public const string AssetCannotTell = "the file is missing for a reason that cannot be told apart from one mod's own defect";

    /// <summary>A file a winning record of <paramref name="needer"/> names that the game does not find, although
    /// <paramref name="shipper"/> (the needer's own mod or a mod of a plugin it is made with; null when none of them ships it)
    /// has it in an archive whose name another layer's archive, <paramref name="hider"/>'s, wins (null when that cannot be
    /// read). A row only between two mods: judged when any of the three is in the selection; not a row when the record is the
    /// game's own, or when no mod it is made with ships the file (its own mod's archive hiding the file reads so too, since its
    /// own mod is one of them); counted as not told apart when the archive that wins cannot be named as a third mod's.</summary>
    public static RecordVerdict MissingAsset(LinkEnd needer, LinkEnd? shipper, LinkEnd? hider) => new(true, BetweenMods);   // STUB: the red point

    /// <summary>A plugin's masters, transitively, from each plugin's header (<paramref name="direct"/>: its masters, or null
    /// when its header could not be read or it is not in the order). Blind when any header on the way could not be read.</summary>
    public static (IReadOnlySet<string> Closure, bool Blind) Masters(string plugin, Func<string, IReadOnlyList<string>?> direct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = direct(plugin);
        bool blind = first is null;
        var stack = new Stack<string>(first ?? Array.Empty<string>());
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (!seen.Add(m)) continue;
            var next = direct(m);
            if (next is null) blind = true;
            else foreach (var n in next) stack.Push(n);
        }
        return (seen, blind);
    }
}
