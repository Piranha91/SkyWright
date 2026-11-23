using System.Text.Json;
using HarnessShared;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The load-order panel's rules on a hand-built MO2 instance: the list is the order the scene is built on, and
/// switching a plugin off takes exactly the plugins that need it as a master with it.</summary>
[Trait("tier", "unit")]
public sealed class LoadOrderPanelTests : IDisposable
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-loadorder-" + Guid.NewGuid().ToString("N"));
    readonly string _instance, _game;
    const string Profile = "P";

    public LoadOrderPanelTests()
    {
        _instance = Directory.CreateDirectory(Path.Combine(_root, "instance")).FullName;
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(_game, "Data")).FullName;
        Directory.CreateDirectory(Path.Combine(_instance, "mods"));
        Directory.CreateDirectory(Path.Combine(_instance, "overwrite"));
        var prof = Directory.CreateDirectory(Path.Combine(_instance, "profiles", Profile)).FullName;

        var b = Plugin(data, new ModKey("Base", ModType.Master));
        var d = Plugin(data, new ModKey("D", ModType.Light), b);
        var a = Plugin(data, new ModKey("A", ModType.Plugin), b);
        var bb = Plugin(data, new ModKey("B", ModType.Plugin), b, a);
        var c = Plugin(data, new ModKey("C", ModType.Plugin), b);
        var e = Plugin(data, new ModKey("E", ModType.Plugin), b, bb);
        var town = Plugin(data, new ModKey("Town", ModType.Plugin), b);
        Plugin(data, new ModKey("Town Extra", ModType.Plugin), b, town);
        Plugin(data, new ModKey("Town - Patch", ModType.Plugin), b);
        _ = (d, c, e);
        File.WriteAllLines(Path.Combine(prof, "plugins.txt"), new[] { "*Base.esm", "*D.esl", "*A.esp", "*B.esp", "*C.esp", "*E.esp" });
        File.WriteAllLines(Path.Combine(prof, "loadorder.txt"), new[] { "Base.esm", "D.esl", "A.esp", "B.esp", "C.esp", "E.esp" });
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "");
    }

    static SkyrimMod Plugin(string data, ModKey key, params SkyrimMod[] masters)
    {
        var m = new SkyrimMod(key, Rel);
        // an override of each master's first static makes the master a real dependency
        foreach (var master in masters) m.Statics.Add(new Static(new FormKey(master.ModKey, 0x800), Rel) { EditorID = "Over" + master.ModKey.Name });
        m.Statics.Add(new Static(new FormKey(key, 0x800), Rel) { EditorID = "Own" + key.Name });
        m.BeginWrite.ToPath(Path.Combine(data, key.FileName)).WithLoadOrder(masters).Write();
        return m;
    }

    LoadOrderSpec Spec(string? orderFile) => new(_instance, Profile, _game, orderFile, "test order");

    string Candidate(params string[] plugins)
    {
        var path = Path.Combine(_root, "candidate.json");
        new CandidateFile(ConfigurationIdentity.CurrentSchema, "test", Profile, plugins, null, Array.Empty<string>(), null).Write(path);
        return path;
    }

    [Fact]
    public void The_list_is_the_order_file_the_scene_is_built_on_in_its_own_order()
    {
        // the candidate puts C before A and B: the list follows the candidate, not the profile's loadorder.txt
        string[] want = { "Base.esm", "D.esl", "C.esp", "A.esp", "B.esp", "E.esp" };
        var list = LoadOrderRules.Entries(Spec(Candidate(want)));
        Assert.Equal(want, list.Select(x => x.Name));
        Assert.Equal(new[] { "00", "FE:000", "01", "02", "03", "04" }, list.Select(x => x.Index));
        // an order file's own spelling is kept, byte for byte, where it differs from the file's name on disk in case only
        string[] spelled = { "Base.esm", "d.ESL", "c.esp", "A.esp", "B.esp", "E.esp" };
        Assert.Equal(spelled, LoadOrderRules.Entries(Spec(Candidate(spelled))).Select(x => x.Name));
        // and the profile's own order when the scene names no order file
        Assert.Equal(new[] { "Base.esm", "D.esl", "A.esp", "B.esp", "C.esp", "E.esp" }, LoadOrderRules.Entries(Spec(null)).Select(x => x.Name));
    }

    [Fact]
    public void Switching_a_plugin_off_takes_every_plugin_that_needs_it_as_a_master_and_nothing_else()
    {
        var list = LoadOrderRules.Entries(Spec(null));
        var (kept, also) = LoadOrderRules.Without(list, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.esp" });
        // B masters A, and E masters B: both go; C and the light plugin master only Base and stay
        Assert.Equal(new[] { "B.esp", "E.esp" }, also);
        Assert.Equal(new[] { "Base.esm", "D.esl", "C.esp" }, kept);
        var (kept2, also2) = LoadOrderRules.Without(list, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C.esp" });
        Assert.Empty(also2);
        Assert.Equal(new[] { "Base.esm", "D.esl", "A.esp", "B.esp", "E.esp" }, kept2);
    }

    [Fact]
    public void A_plugin_named_for_a_switched_off_plugin_that_does_not_master_it_stays_and_is_named_with_why()
    {
        // Town Extra masters Town and goes with it; Town - Patch only carries the name, so it stays and is marked
        var list = LoadOrderRules.Entries(Spec(Candidate("Base.esm", "Town.esp", "Town Extra.esp", "Town - Patch.esp", "A.esp")));
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Town.esp" };
        var (kept, also) = LoadOrderRules.Without(list, off);
        Assert.Equal(new[] { "Town Extra.esp" }, also);
        Assert.Contains("Town - Patch.esp", kept);
        var marked = LoadOrderRules.LooksLikePatches(list, off, also);
        Assert.Equal(new[] { "Town - Patch.esp" }, marked.Keys);
        Assert.Contains("does not list Town.esp as a master", marked["Town - Patch.esp"]);
        // a name that does not hold the switched-off plugin's name is not marked, and nothing is marked when nothing is off
        Assert.Empty(LoadOrderRules.LooksLikePatches(list, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.esp" }, Array.Empty<string>()));
    }

    [Fact]
    public void The_order_file_without_the_plugins_loads_exactly_the_kept_plugins_and_keeps_the_mod_list()
    {
        string[] want = { "Base.esm", "D.esl", "C.esp", "A.esp", "B.esp", "E.esp" };
        var baseFile = Path.Combine(_root, "with-mods.json");
        new CandidateFile(ConfigurationIdentity.CurrentSchema, "test", Profile, want, new[] { "Some Mod" }, Array.Empty<string>(), null).Write(baseFile);
        var spec = Spec(baseFile);
        var list = LoadOrderRules.Entries(spec);
        var (kept, also) = LoadOrderRules.Without(list, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.esp" });
        var folder = Path.Combine(_root, "work");
        var file = LoadOrderRules.WriteWithout(spec, kept, new[] { "A.esp" }, also, folder);
        Assert.StartsWith(Path.GetFullPath(folder), Path.GetFullPath(file));
        var written = CandidateFile.Read(file);
        Assert.Equal(new[] { "Base.esm", "D.esl", "C.esp" }, written.Plugins);
        Assert.Equal(new[] { "Some Mod" }, written.Mods);
        Assert.Equal(Profile, written.BaseProfile);
        // the same choice writes the same file, byte for byte
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(file, LoadOrderRules.WriteWithout(spec, kept, new[] { "A.esp" }, also, folder));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        // the base order file is not touched
        Assert.Equal(want, CandidateFile.Read(baseFile).Plugins);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
