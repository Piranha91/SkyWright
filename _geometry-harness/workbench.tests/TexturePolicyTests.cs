using SkyRender;
using ViewProbe;
using Xunit;

namespace WorkbenchTests;

/// <summary>The texture policy (viewprobe TexturePolicy.cs): one owner asks SkyRender which DDS route a render uses, says
/// it in plain words and gives the token that joins every picture's identity. These cases set the two environment
/// variables of the process, so they run one at a time.</summary>
[Trait("tier", "unit")]
[Collection("texture-policy-environment")]
public class TexturePolicyTests
{
    internal static TexturePolicy With(string? current, string? legacy)
    {
        string? oldCurrent = Environment.GetEnvironmentVariable(DdsMipPolicy.EnvironmentVariable);
        string? oldLegacy = Environment.GetEnvironmentVariable(DdsMipPolicy.LegacyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DdsMipPolicy.EnvironmentVariable, current);
            Environment.SetEnvironmentVariable(DdsMipPolicy.LegacyEnvironmentVariable, legacy);
            return TexturePolicy.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DdsMipPolicy.EnvironmentVariable, oldCurrent);
            Environment.SetEnvironmentVariable(DdsMipPolicy.LegacyEnvironmentVariable, oldLegacy);
        }
    }

    [Fact]
    public void Nothing_set_is_the_library_default_compressed_route()
    {
        var p = With(null, null);
        Assert.Equal(DdsMipPolicy.DefaultMaximumFinalMipDimension, p.MaxFinalMip);
        Assert.False(p.Explicit);
        Assert.Equal("compressed-final-mip-4", p.Token);
        Assert.Equal("compressed, last mip at most 4 px", p.Label);
        Assert.Equal("SkyRender's default", p.Source);
    }

    [Fact]
    public void Decoded_stays_selectable_by_either_name()
    {
        Assert.Equal("decoded", With("decoded", null).Token);
        Assert.Equal("decoded", With(null, "decoded").Token);
        Assert.True(With(null, "decoded").Explicit);
    }

    [Fact]
    public void The_new_name_wins_over_the_legacy_name_as_the_library_decides()
    {
        Assert.Equal("decoded", With("decoded", "unlimited").Token);
        Assert.Equal("compressed-unlimited", With("unlimited", "decoded").Token);
        Assert.Equal("compressed-final-mip-8", With("8", "decoded").Token);
    }

    [Fact]
    public void Every_policy_that_renders_differently_has_its_own_token()
    {
        var tokens = new[] { With("decoded", null), With(null, null), With("8", null), With("unlimited", null) }.Select(p => p.Token).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_unreadable_setting_refuses_in_one_sentence_instead_of_rendering_decoded()
    {
        var ex = Assert.Throws<TexturePolicyRefusal>(() => With("sometimes", null));
        Assert.Contains("'sometimes'", ex.Message);
        Assert.Contains("set it to decoded, unlimited or a positive whole number", ex.Message);
        Assert.EndsWith(".", ex.Message);
    }
}
