namespace SkyWright.Engine;

// BETWEEN MODS, for the record checks (recordprobe): which mod a plugin belongs to, and when a record fact is a clash between
// two mods rather than one mod's own defect or the game's. This class owns the rule and every reason; recordprobe reads
// houseCARL's answers and asks. The game's own files (Bethesda's and Creation Club's) are named by the caller, which owns
// that list (HarnessShared.BaseGamePlugins).
//   a plugin's mod   the MO2 mod folder it sits in; a plugin anywhere else (the game's Data folder, MO2's overwrite) is a
//                    mod of its own (Gate A review finding 2);
//   dangling links   STUB (unit 1's red point): every link whose holder is in the selection is a row.

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
        return "";
    }

    /// <summary>A mod key in words: the folder's name, or the plugin's own name.</summary>
    public static string ModName(string mod) => mod.Contains(':') ? mod[(mod.IndexOf(':') + 1)..] : mod;

    /// <summary>A dangling link from <paramref name="holder"/> (the plugin whose record carries it) into a record
    /// <paramref name="target"/>'s plugin should define and does not.</summary>
    public static RecordVerdict Dangling(LinkEnd holder, LinkEnd target)
    {
        if (!holder.Selected) return new(false, NotSelected);
        return new(true, BetweenMods);
    }
}
