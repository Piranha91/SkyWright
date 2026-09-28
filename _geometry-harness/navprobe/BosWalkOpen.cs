using Mutagen.Bethesda.Skyrim;

// How the swap layer's walk over the whole order opens each plugin. Held apart so --moved-ref-selftest runs the code a
// real run runs.
static class BosWalkOpen
{
    /// <summary>One plugin of the order, or throw PluginUnreadableException: refused, never skipped (the swap index would
    /// otherwise miss that plugin's records with no word).</summary>
    public static ISkyrimModGetter? Open(string path) => HarnessShared.PluginOpen.Open(path, "navprobe's swap-layer walk");
}
