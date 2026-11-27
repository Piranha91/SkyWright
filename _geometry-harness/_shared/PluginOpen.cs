using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// A PLUGIN A WALK CANNOT READ IS REFUSED, NEVER SKIPPED (the probes' counterpart of viewprobe's PluginRead.cs).
// A walk that skipped an unreadable plugin kept an earlier plugin's version of every record the plugin overrides, with no
// word: the same stale-version outcome as a moved reference, by another route. Probes whose documented contract is to
// COUNT and print unreadable plugins (navownprobe's and stateprobe's first pass, driftprobe) keep that contract.

/// <summary>A plugin that could not be opened. The message names the file and the walk; the caller exits 2 with it.</summary>
public sealed class PluginUnreadableException : Exception
{
    public PluginUnreadableException(string path, string walk, Exception inner, bool midWalk = false)
        : base($"{Path.GetFileName(path)} {(midWalk ? "failed while " + walk + " read it" : "could not be read by " + walk)}: "
             + $"{inner.GetType().Name}: {inner.Message}. "
             + (midWalk ? "The fault is in the plugin's records or in the reader (Mutagen or this walk); either way this "
                          + "plugin's part of the walk is incomplete. " : "")
             + "Refusing: "
             + "without it, an earlier plugin's version of every record it overrides would be used silently.", inner) { }
}

public static class PluginOpen
{
    /// <summary>Run one plugin's part of a walk. A plugin opens lazily, so a damaged group or record throws while the walk
    /// reads it, not at open: any exception that is not already a refusal becomes a PluginUnreadableException naming the
    /// plugin and the walk (viewprobe's PluginRead.Wraps is the same rule).</summary>
    public static void Guard(string path, string walk, Action body)
    {
        try { body(); }
        catch (Exception ex) when (ex is not PluginUnreadableException) { throw new PluginUnreadableException(path, walk, ex, midWalk: true); }
    }

    /// <summary>Where a probe's <c>--plugin NAME</c> is: the game's Data folder first, else the profile's load order (a mod's
    /// plugin lives in its MO2 mod folder, which only the order knows). <paramref name="orderedPaths"/> is called only when
    /// Data lacks the file. Null when neither has it; the refusal is <see cref="NotLocated"/>.</summary>
    public static string? Locate(string name, string dataDir, Func<IEnumerable<string>> orderedPaths)
    {
        var inData = Path.Combine(dataDir, name);
        if (File.Exists(inData)) return inData;
        return orderedPaths().FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The one sentence for a <c>--plugin</c> that <see cref="Locate"/> found nowhere; the caller exits 2 with it.</summary>
    public static string NotLocated(string name, string dataDir) =>
        $"{name} is neither in {dataDir} nor in the profile's load order; check the plugin's file name, or name the MO2 profile that enables its mod.";

    /// <summary>Open one plugin of a walk, or throw <see cref="PluginUnreadableException"/>.</summary>
    public static ISkyrimModGetter Open(string path, string walk)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
        catch (Exception ex) { throw new PluginUnreadableException(path, walk, ex); }
    }
}
