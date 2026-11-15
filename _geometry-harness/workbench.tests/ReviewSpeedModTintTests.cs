using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Item 3, "make one mod see-through": a mod folder's plugins in the arm's view, through the layer map the
/// probes use (HarnessOrderCli.PluginLayerMap), and the drawn references those plugins win.</summary>
[Trait("tier", "unit")]
public sealed class ReviewSpeedModTintTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-modtint-" + Guid.NewGuid().ToString("N"));
    string Mods => Path.Combine(_root, "instance", "mods");
    string Profile => Path.Combine(_root, "instance", "profiles", "P");
    string Data => Path.Combine(_root, "game", "Data");
    string Overwrite => Path.Combine(_root, "instance", "overwrite");
    static readonly string[] View = { "ModA", "ModB", "ModC" };
    static readonly string[] Order = { "Base.esm", "A1.esp", "A2.esp", "B.esp" };

    public ReviewSpeedModTintTests()
    {
        foreach (var d in new[] { Profile, Data, Overwrite, Path.Combine(Mods, "ModA"), Path.Combine(Mods, "ModB"), Path.Combine(Mods, "ModC", "textures") })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(Data, "Base.esm"), "x");
        File.WriteAllText(Path.Combine(Mods, "ModA", "A1.esp"), "x");
        File.WriteAllText(Path.Combine(Mods, "ModA", "A2.esp"), "x");
        File.WriteAllText(Path.Combine(Mods, "ModB", "B.esp"), "x");
        File.WriteAllText(Path.Combine(Mods, "ModC", "textures", "t.dds"), "x");
        File.WriteAllText(Path.Combine(Profile, "modlist.txt"), "# header\r\n+ModC\r\n+ModB\r\n+ModA\r\n");
        File.WriteAllText(Path.Combine(Profile, "plugins.txt"), "*A1.esp\r\n*A2.esp\r\n*B.esp\r\n");
        File.WriteAllText(Path.Combine(Profile, "loadorder.txt"), "# header\r\nBase.esm\r\nA1.esp\r\nA2.esp\r\nB.esp\r\n");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    IReadOnlyDictionary<string, string> Map() => HarnessOrderCli.PluginLayerMap(Profile, View, Mods, Data, Overwrite);

    // B1a
    [Fact]
    public void A_mod_folder_resolves_to_the_plugins_it_provides_in_the_arms_view()
    {
        var (plugins, why) = ModTint.PluginsOf("ModA", Map(), Mods, Order);
        Assert.Null(why);
        Assert.Equal(new[] { "A1.esp", "A2.esp" }, plugins.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "B.esp" }, ModTint.PluginsOf("modb", Map(), Mods, Order).Plugins);
    }

    // B1c
    [Fact]
    public void A_mod_folder_that_provides_no_plugin_of_the_order_is_refused_in_one_sentence()
    {
        var (plugins, why) = ModTint.PluginsOf("ModC", Map(), Mods, Order);
        Assert.Empty(plugins);
        Assert.NotNull(why);
        Assert.Contains("ModC", why);
        Assert.DoesNotContain("\n", why);
        Assert.NotNull(ModTint.PluginsOf("NoSuchMod", Map(), Mods, Order).Refusal);
    }

    // B1b
    [Fact]
    public void Only_the_drawn_references_those_plugins_win_are_chosen()
    {
        FormKey K(uint id) => new(new ModKey("Base", ModType.Master), id);
        var winners = new Dictionary<FormKey, string> { [K(1)] = "A1.esp", [K(2)] = "a2.ESP", [K(3)] = "B.esp", [K(4)] = "Base.esm" };
        var chosen = ModTint.Select(new[] { K(1), K(2), K(3), K(4), K(5) }, winners, new[] { "A1.esp", "A2.esp" });
        Assert.Equal(new[] { K(1), K(2) }, chosen);
    }
}
