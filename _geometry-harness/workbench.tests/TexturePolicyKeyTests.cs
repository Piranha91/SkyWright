using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The texture policy in the keys a picture or a built scene is reused by (TexturePolicy.cs): two policies give
/// two keys, so nothing drawn under one is reused under the other. Configuration identity is on the strict-test list.</summary>
[Trait("tier", "unit")]
[Collection("texture-policy-environment")]
public class TexturePolicyKeyTests
{
    // ---- the policy in the keys a picture is reused by: two policies, two keys --------------------------------------

    static readonly (string, long, long)[] Order = { (@"C:\Game\Data\Skyrim.esm", 250_000_000, 637_000_000_000_000_000) };

    [Fact]
    public void Map_tiles_drawn_under_another_texture_policy_are_another_folder()
    {
        string decoded = MapTileCache.OrderKey(Order, MapTileCache.RendererIdentity(TexturePolicyTests.With("decoded", null)), "mods");
        string compressed = MapTileCache.OrderKey(Order, MapTileCache.RendererIdentity(TexturePolicyTests.With(null, null)), "mods");
        Assert.True(decoded != compressed, "map tiles drawn decoded and compressed share one tile folder, so one would be shown as the other");
        // the default spelled the library's way is the same renderer identity
        Assert.Equal(MapTileCache.RendererIdentity(TexturePolicyTests.With(null, null)), MapTileCache.RendererIdentity(TexturePolicyTests.With("4", null)));
    }

    static string StaticKey(string policy) => TexturedScene.StaticInputKey("prepared", Array.Empty<SceneRef>(),
        new Dictionary<string, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>>(),
        Array.Empty<(string, List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, float, float, float)>)>(),
        new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Covering>(), false, false, null, false, 0f, policy);

    [Fact]
    public void A_retained_scene_built_under_another_texture_policy_is_not_reused()
    {
        Assert.True(StaticKey(TexturePolicyTests.With("decoded", null).Token) != StaticKey(TexturePolicyTests.With(null, null).Token),
            "a retained scene built decoded would be reused for a compressed shot: the static key ignores the texture policy");
        Assert.Equal(StaticKey("decoded"), StaticKey("decoded"));
    }
}
