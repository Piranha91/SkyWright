using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The Open control's rule: a path is what the first reader that recognises it says, by names on disk alone;
/// anything else is refused in one sentence naming what can be opened.</summary>
[Trait("tier", "unit")]
public sealed class OpenInputsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-open-" + Guid.NewGuid().ToString("N"));
    public OpenInputsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    sealed class FakeReader(string kind, string accepts, Func<string, bool> takes) : IInputReader
    {
        public string Kind => kind;
        public string Accepts => accepts;
        public InputMatch? Recognise(string path) => takes(path) ? new InputMatch(this, path, "a " + kind) : null;
        public string Open(InputMatch match) => "opened " + match.Path;
    }

    string Bundle(string name)
    {
        var ev = Path.Combine(_root, name, "bundle", "evidence");
        Directory.CreateDirectory(Path.Combine(ev, "receipts"));
        File.WriteAllText(Path.Combine(ev, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(ev, "plan.json"), "{}");
        return ev;
    }

    [Fact]
    public void A_bundle_is_recognised_from_its_evidence_folder_its_parent_or_a_file_inside()
    {
        var ev = Bundle("run");
        var r = new BundleReader();
        Assert.Equal(ev, r.Recognise(ev)!.Path);
        Assert.Equal(ev, r.Recognise(Path.GetDirectoryName(ev)!)!.Path);                          // the folder holding evidence/
        Assert.Equal(ev, r.Recognise(Path.GetDirectoryName(Path.GetDirectoryName(ev)!)!)!.Path);  // the run folder holding bundle/evidence
        Assert.Equal(ev, r.Recognise(Path.Combine(ev, "manifest.json"))!.Path);
        Assert.Null(r.Recognise(_root));
    }

    [Fact]
    public void The_first_reader_that_recognises_a_path_takes_it()
    {
        var readers = new IInputReader[] { new FakeReader("first", "a first thing", p => p.EndsWith("x")), new FakeReader("second", "a second thing", _ => true) };
        var dir = Directory.CreateDirectory(Path.Combine(_root, "x")).FullName;
        Assert.Equal("first", InputReaders.Detect(dir, readers).Reader.Kind);
        var other = Directory.CreateDirectory(Path.Combine(_root, "y")).FullName;
        Assert.Equal("second", InputReaders.Detect(other, readers).Reader.Kind);
    }

    [Fact]
    public void A_path_no_reader_recognises_is_refused_in_one_sentence_naming_every_kind()
    {
        var readers = new IInputReader[] { new FakeReader("a", "an evaluation bundle", _ => false), new FakeReader("b", "a game Data folder", _ => false), new FakeReader("c", "an MO2 profile folder", _ => false) };
        var ex = Assert.Throws<InvalidOperationException>(() => InputReaders.Detect(_root, readers));
        Assert.Equal($"'{_root}' is not something the workbench opens: choose an evaluation bundle, a game Data folder or an MO2 profile folder.", ex.Message);
    }

    [Theory]
    [InlineData("", "type or browse to a folder or file to open.")]
    [InlineData("relative\folder", "'relative\folder' is not a full path; give the folder or file by its full path.")]
    public void An_empty_or_relative_path_is_refused_before_any_reader_is_asked(string path, string sentence)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InputReaders.Detect(path, new IInputReader[] { new FakeReader("a", "a thing", _ => true) }));
        Assert.Equal(sentence, ex.Message);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_refused()
    {
        var gone = Path.Combine(_root, "not-here");
        var ex = Assert.Throws<InvalidOperationException>(() => InputReaders.Detect(gone, new IInputReader[] { new FakeReader("a", "a thing", _ => true) }));
        Assert.Equal($"'{gone}' does not exist; choose a folder or file that does.", ex.Message);
    }

    [Fact]
    public void The_recent_list_keeps_the_newest_first_once_each()
    {
        var file = Path.Combine(_root, OpenBar.RecentFile);
        OpenBar.Remember(file, @"C:\one"); OpenBar.Remember(file, @"C:\two"); OpenBar.Remember(file, @"c:\ONE");
        Assert.Equal(new[] { @"c:\ONE", @"C:\two" }, OpenBar.ReadRecent(file));
    }

    [Fact]
    public void Two_load_orders_are_one_scene_source_only_when_every_part_matches()
    {
        var a = new LoadOrderSpec(@"C:\inst", "P", @"C:\game", null, "a");
        Assert.Equal(a.Key, new LoadOrderSpec(@"c:\INST\", "p", @"C:\game", null, "other label").Key);
        Assert.NotEqual(a.Key, (a with { Profile = "Q" }).Key);
        Assert.NotEqual(a.Key, (a with { OrderFile = @"C:\orders\after.json" }).Key);
        Assert.Equal(new[] { "--instance", @"C:\inst", "--profile", "P", "--game", @"C:\game", "--order", @"C:\o.json" }, (a with { OrderFile = @"C:\o.json" }).Args());
    }

    [Fact]
    public void A_load_order_is_read_from_viewprobes_own_arguments_or_not_at_all()
    {
        var full = LoadOrderSpec.FromArgs(new[] { "--bundle", "x", "--instance", @"C:\i", "--profile", "P", "--game", @"C:\g", "--order", @"C:\o.json" }, "l");
        Assert.Equal((@"C:\i", "P", @"C:\g", @"C:\o.json"), (full!.Instance, full.Profile, full.Game, full.OrderFile));
        Assert.Null(LoadOrderSpec.FromArgs(new[] { "--instance", @"C:\i", "--game", @"C:\g" }, "l"));   // the profile is left to the environment
    }
}
