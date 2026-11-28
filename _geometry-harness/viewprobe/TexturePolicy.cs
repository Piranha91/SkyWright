using SkyRender;

namespace ViewProbe;

/// <summary>
/// The texture policy a render uses, stated once: how the DDS textures go to the GPU. SkyRender decides it from
/// <c>SKYRENDER_DDS_MAX_FINAL_MIP_DIMENSION</c> (or its former name <c>CVR_DDS_MAX_FINAL_MIP_DIMENSION</c> when that is
/// unset) and freezes it in each <see cref="AssetCache"/>; this class asks the library the same question
/// (<see cref="DdsMipPolicy.ReadEnvironmentValue"/>, <see cref="DdsMipPolicy.Parse"/>) and never reads the variables
/// itself, so the precedence has one owner.
///
/// <para>Nothing set: the library's default, authored-mip compressed uploads for chains whose last mip is at most
/// <see cref="DdsMipPolicy.DefaultMaximumFinalMipDimension"/> px. <c>decoded</c>: every texture decoded to RGBA and its
/// mips generated (the route viewprobe was pinned to until the policy was recorded). The policy changes captured
/// texture pixels (mostly normal-mapped surfaces and alpha edges) and, where an alpha-tested shape is cut by its texture's
/// alpha, that shape's depth and what is read from it (picks, visibility masks); never geometry. So it is part of
/// a picture's identity: <see cref="Token"/> joins every key a picture is reused or compared by.</para>
/// </summary>
internal sealed record TexturePolicy(int? MaxFinalMip, bool Explicit, string? Setting)
{
    /// <summary>The policy this process renders with, read once at start (Program.cs) before any cache is built.</summary>
    internal static TexturePolicy Current => _current ??= FromEnvironment();
    static TexturePolicy? _current;

    /// <summary>The library's reading of the environment. An unreadable value refuses: the library would log it and quietly
    /// render decoded, a policy nobody chose.</summary>
    internal static TexturePolicy FromEnvironment()
    {
        string? setting = DdsMipPolicy.ReadEnvironmentValue();
        int? max;
        try { max = DdsMipPolicy.Parse(setting); }
        catch (ArgumentException)
        {
            throw new TexturePolicyRefusal($"the texture policy '{setting}' (from {DdsMipPolicy.EnvironmentVariable}, or "
                + $"{DdsMipPolicy.LegacyEnvironmentVariable} when that is unset) is not one SkyRender knows; set it to decoded, "
                + "unlimited or a positive whole number of pixels, or unset it for the default.");
        }
        return new TexturePolicy(max, !string.IsNullOrWhiteSpace(setting), setting);
    }

    /// <summary>Re-read the environment (at process start, and in tests that set it).</summary>
    internal static TexturePolicy Refresh() => _current = FromEnvironment();

    /// <summary>The policy of a cache the library built: what it actually froze.</summary>
    internal static TexturePolicy Of(AssetCache cache) => new(cache.DdsMaximumFinalMipDimension, true, null);

    internal bool Compressed => MaxFinalMip.HasValue;

    /// <summary>The short form that joins identities and reuse keys: <c>decoded</c>, <c>compressed-final-mip-4</c>,
    /// <c>compressed-unlimited</c>. Two policies that render differently never share one.</summary>
    internal string Token => MaxFinalMip switch
    {
        null => "decoded",
        int.MaxValue => "compressed-unlimited",
        int m => "compressed-final-mip-" + m.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The plain words: <c>decoded</c>, or <c>compressed, last mip at most 4 px</c>.</summary>
    internal string Label => MaxFinalMip switch
    {
        null => "decoded",
        int.MaxValue => "compressed, any mip chain",
        int m => $"compressed, last mip at most {m} px",
    };

    /// <summary>How it was chosen, for the console line a run log keeps.</summary>
    internal string Source => Explicit ? $"set in the environment ('{Setting}')" : "SkyRender's default";

    /// <summary>The census line for a build: how many textures took each route (SkyRender's own upload diagnostics), the
    /// bytes uploaded, and what the cache decoded on the CPU since it was made (the decoded route, fallbacks and every CPU
    /// use of pixels, prefetch included).</summary>
    internal static string UploadSummary(IEnumerable<GlTextureManager?> managers, AssetCache cache)
    {
        var d = managers.OfType<GlTextureManager>().Distinct().SelectMany(m => m.UploadDiagnostics).ToList();
        static string MiB(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
        string Part(string route, string words)
        {
            var r = d.Where(x => x.Route == route).ToList();
            return $"{r.Count:N0} {words} ({MiB(r.Sum(x => x.UploadedBytes))})";
        }
        return $"texture uploads ({Current.Label}): {Part("compressed-authored", "compressed")}, {Part("decoded", "decoded")}, "
             + $"{d.Count(x => x.Route == "resident"):N0} already resident, {d.Count(x => x.Route == "error"):N0} failed; "
             + $"decoded on the CPU: {cache.TotalDecodeCalls:N0} texture(s), {MiB(cache.TotalDecodedBytes)} in {cache.TotalDecodeMs:0} ms, "
             + $"{MiB(cache.CachedDecodedBytes)} held";
    }

    /// <summary>A cache whose frozen policy is not the one this run records refuses: its pictures would carry the wrong
    /// identity.</summary>
    internal void RequireSameAs(AssetCache cache)
    {
        if (cache.DdsMaximumFinalMipDimension != MaxFinalMip)
            throw new TexturePolicyRefusal($"the texture cache was built with the texture policy '{Of(cache).Label}' but this run "
                + $"records '{Label}'; the texture policy variable changed while viewprobe ran, so start it again with one setting.");
    }
}

/// <summary>A texture policy that cannot be honoured or recorded truthfully.</summary>
internal sealed class TexturePolicyRefusal(string message) : Exception(message);
