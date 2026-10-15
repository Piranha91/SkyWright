namespace HarnessShared;

// The base-game plugin file names the probes split their populations by (rule 12: Bethesda-on-Bethesda is a population of
// its own), and the unofficial patch's name beside them. One definition; every comparison ignores case, as Windows and
// Mutagen's ModKey do. Creation Club plugins are not here: a caller that counts them as Bethesda says so itself
// (PluginKinds.IsBethesda), and navownprobe deliberately does not (decided 2026-09-28).
public static class BaseGamePlugins
{
    /// <summary>Bethesda's shipped game files.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };

    /// <summary>The unofficial patch, a population of its own beside the base game.</summary>
    public const string Ussep = "Unofficial Skyrim Special Edition Patch.esp";

    public static bool IsBaseGame(string fileName) => Names.Contains(fileName);
    public static bool IsUssep(string fileName) => string.Equals(fileName, Ussep, StringComparison.OrdinalIgnoreCase);

    /// <summary>The two masters every plugin carries: Skyrim.esm and Update.esm (overlapprobe's default candidate filter).</summary>
    public static bool IsCoreMaster(string fileName) =>
        string.Equals(fileName, "Skyrim.esm", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "Update.esm", StringComparison.OrdinalIgnoreCase);
}
