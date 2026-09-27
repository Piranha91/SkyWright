using Mutagen.Bethesda.Skyrim;

// How the swap layer's walk over the whole order opens each plugin. Held apart so --moved-ref-selftest runs the code a
// real run runs.
static class BosWalkOpen
{
    /// <summary>One plugin of the order, or null when it cannot be read.</summary>
    public static ISkyrimModGetter? Open(string path)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { return null; }
    }
}
