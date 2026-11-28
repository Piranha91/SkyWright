namespace SkyWright.Engine;

/// <summary>One plugin's prepared file (stub).</summary>
static class WorldReadFile
{
    /// <summary>Where the prepared file of a plugin with this content and name lives in the cache folder.</summary>
    internal static string PathFor(string cacheFolder, string contentSha, string pluginName)
        => Path.Combine(cacheFolder, contentSha + ".swr");

    /// <summary>Prepare <paramref name="pluginPath"/> into <paramref name="outPath"/>, stating the given content hash,
    /// schema and Engine version in its header.</summary>
    internal static void WriteFor(string pluginPath, string contentSha, string outPath, int schema, Guid engine)
        => File.WriteAllBytes(outPath, Array.Empty<byte>());
}
