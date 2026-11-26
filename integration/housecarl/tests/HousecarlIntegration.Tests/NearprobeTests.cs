extern alias nearprobe;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using nearprobe::NearProbe;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Running nearprobe on a load order laid out from one of the engine's own test worlds, and reading its JSON.
/// The worlds are the engine tests' (NearSelectionTests.World, DoorsNearTests.World, DoorsNearUnreadTests.World): their
/// plugins are copied into a throw-away MO2 instance (one mod folder, the profile's three lists, an empty game Data), so
/// the program reads them through its own command line and load-order reader.</summary>
static class Nearprobe
{
    public const string Profile = "P";

    /// <summary>The program, where the module's layout marker says its probes are.</summary>
    public static string Exe()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "housecarl-integration-layout.json")));
        string program = marker.RootElement.GetProperty("program").GetString()!.Replace("{name}", "nearprobe");
        return Path.Combine(root, program.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A throw-away instance holding <paramref name="plugins"/> (in load order) in one mod folder; with
    /// <paramref name="garbage"/>, a plugin of that name holding bytes no plugin has is listed last. Returns the root
    /// (inst\ and game\ under it).</summary>
    public static string LayOut(IReadOnlyList<string> plugins, string? garbage = null, IReadOnlyList<string>? enabled = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "nearprobe-test-" + Guid.NewGuid().ToString("N")[..10]);
        string inst = Path.Combine(root, "inst"), mod = Path.Combine(inst, "mods", "Fixture"), prof = Path.Combine(inst, "profiles", Profile);
        foreach (var d in new[] { mod, prof, Path.Combine(inst, "overwrite"), Path.Combine(root, "game", "Data") }) Directory.CreateDirectory(d);
        var names = new List<string>();
        foreach (var p in plugins.Where(File.Exists))
        {
            File.Copy(p, Path.Combine(mod, Path.GetFileName(p)));
            names.Add(Path.GetFileName(p));
        }
        if (garbage is not null)
        {
            File.WriteAllBytes(Path.Combine(mod, garbage), Enumerable.Range(0, 64).Select(i => (byte)(0xA5 ^ i)).ToArray());
            names.Add(garbage);
        }
        var on = enabled ?? names;
        File.WriteAllLines(Path.Combine(prof, "modlist.txt"), new[] { "+Fixture" });
        // every plugin is listed (one left out of plugins.txt but in loadorder.txt is a force-loaded master to MO2); the
        // enabled ones carry the '*'
        File.WriteAllLines(Path.Combine(prof, "plugins.txt"), names.Select(n => (on.Contains(n) ? "*" : "") + n));
        File.WriteAllLines(Path.Combine(prof, "loadorder.txt"), names);
        return root;
    }

    public static void Remove(string root)
    {
        try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>The program run with exactly these arguments; houseCARL's instance variables are taken out of its
    /// environment, so only the command line names a load order.</summary>
    public static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var v in new[] { "HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH" }) psi.Environment.Remove(v);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException("nearprobe did not finish in 120 s"); }
        return (p.ExitCode, o.Result, e.Result);
    }

    /// <summary>The program on the laid-out instance with the question's own arguments.</summary>
    public static (int Exit, string Out, string Err) Ask(string root, params string[] question) =>
        Run(new[] { "--instance", Path.Combine(root, "inst"), "--profile", Profile, "--game", Path.Combine(root, "game") }.Concat(question).ToArray());

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public sealed record Hit(string Seed, string Provider, double Gap, string Rule);

    /// <summary>The answer's JSON, asserted to be an answer (exit 0 or 3, one object with the fields nearprobe prints).</summary>
    public static JsonElement Answer((int Exit, string Out, string Err) r, int exit = 0)
    {
        Assert.True(r.Exit == exit, $"nearprobe exit {r.Exit}, expected {exit}; stderr: {r.Err}; stdout: {Cut(r.Out)}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(r.Out); }
        catch (JsonException ex) { Assert.Fail($"nearprobe's stdout is not one JSON document ({ex.Message}): {Cut(r.Out)}"); throw; }
        var root = doc.RootElement.Clone();
        foreach (var field in new[] { "tool", "query", "source", "complete", "records", "note", "unread", "notIndexed", "completeWithin" })
            Assert.True(root.TryGetProperty(field, out _), $"nearprobe's answer has no '{field}': {Cut(r.Out)}");
        return root;
    }

    /// <summary>A refusal: the exit code, nothing on stdout, and one sentence (its first line) on stderr.</summary>
    public static string Refusal((int Exit, string Out, string Err) r, int exit = 2)
    {
        Assert.True(r.Exit == exit, $"nearprobe exit {r.Exit}, expected the refusal's {exit}; stderr: {r.Err}; stdout: {Cut(r.Out)}");
        Assert.True(r.Out.Trim().Length == 0, $"a refusal prints nothing on stdout, but it printed: {Cut(r.Out)}");
        var first = r.Err.Split('\n')[0].Trim();
        Assert.False(string.IsNullOrWhiteSpace(first), "a refusal says why on stderr, but stderr is empty");
        return first;
    }

    /// <summary>One sentence: ends with a full stop and holds no sentence break inside.</summary>
    public static void OneSentence(string s)
    {
        Assert.EndsWith(".", s);
        Assert.True(s.Split(". ").Length == 1, $"more than one sentence: {s}");
    }

    public static Dictionary<string, List<Hit>> Records(JsonElement answer)
    {
        var d = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        foreach (var rec in answer.GetProperty("records").EnumerateArray())
            d[rec.GetProperty("formid").GetString()!] = rec.GetProperty("hits").EnumerateArray()
                .Select(h => new Hit(h.GetProperty("seed").GetString()!, h.GetProperty("provider").GetString()!,
                                     h.GetProperty("gap").GetDouble(), h.GetProperty("rule").GetString()!)).ToList();
        return d;
    }

    public static List<string> Order(JsonElement answer) =>
        answer.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("formid").GetString()!).ToList();

    static string NoClock(string note) => Regex.Replace(note, @"in \d+ ms", "in # ms");

    /// <summary>The command's answer is the engine's: the same records in the same order, each with the same hits (seed,
    /// provider, exact gap, rule) in the same order, the same unread plugins and the same note but for its clock.</summary>
    public static void SameAsEngine(JsonElement answer, NearAnswer engine)
    {
        Assert.True(engine.Refusal is null, "the engine refused: " + engine.Refusal);
        Assert.Equal(engine.Keys.Select(FormIdToken.Of).ToList(), Order(answer));
        var got = Records(answer);
        foreach (var k in engine.Keys)
            Assert.Equal(engine.Hits[k].Select(h => new Hit(FormIdToken.Of(h.Seed), h.Provider, h.Gap, h.Rule)).ToList(), got[FormIdToken.Of(k)]);
        Assert.Equal(engine.Unread, answer.GetProperty("unread").EnumerateArray().Select(u => u.GetString()!).ToList());
        Assert.Equal(NoClock(engine.Note), NoClock(answer.GetProperty("note").GetString()!));
    }

    static string Cut(string s) => s.Length > 600 ? s[..600] + "…" : s;
}

/// <summary>The NearSelection world (houseCARL's near= cases 480-489, the engine's NearSelectionTests) laid out once.</summary>
public sealed class NearprobeSelectionWorld : IDisposable
{
    public NearSelectionTests.World World { get; } = new();
    public string Root { get; }
    public NearprobeSelectionWorld() => Root = Nearprobe.LayOut(World.PluginPaths);
    public void Dispose() { Nearprobe.Remove(Root); World.Dispose(); }
}

/// <summary>nearprobe on the NearSelection world: the engine's known answers 480-489 through the command, its equality
/// with the engine, and every refusal of the command line, the seeds, the radius, the record types and the load order.</summary>
[Trait("tier", "unit")]
public sealed class NearprobeSelectionTests : IClassFixture<NearprobeSelectionWorld>
{
    readonly NearprobeSelectionWorld _f;
    public NearprobeSelectionTests(NearprobeSelectionWorld f) => _f = f;
    static string Fid(FormKey fk) => Nearprobe.Fid(fk);
    static readonly string SeedFid = Fid(NearSelectionTests.World.Seed);

    (int, string, string) Ask(params string[] q) => Nearprobe.Ask(_f.Root, q);
    JsonElement Answer(params string[] q) => Nearprobe.Answer(Ask(q));
    Dictionary<string, List<Nearprobe.Hit>> Near(string within, params FormKey[] seeds) =>
        Nearprobe.Records(Answer(seeds.SelectMany(s => new[] { "--of", Fid(s) }).Concat(new[] { "--within", within }).ToArray()));

    // ---- the engine's known answers (NearSelectionTests, by case) through the command -----------------------------

    // 480: a touching neighbour is listed, naming the seed, its provider, gap 0 and the rule; the seed is not its own.
    [Fact]
    public void M480_a_touching_neighbour_with_seed_provider_gap_and_rule()
    {
        var m = Near("0", NearSelectionTests.World.Seed);
        var hits = Assert.Contains(Fid(NearSelectionTests.World.Touching), (IDictionary<string, List<Nearprobe.Hit>>)m);
        Assert.Contains(new Nearprobe.Hit(SeedFid, "hcNearA.esp", 0, "sphere"), hits);
        Assert.DoesNotContain(SeedFid, (IDictionary<string, List<Nearprobe.Hit>>)m);
    }

    // 481: an origin 3,096 units away whose base's bounds reach the seed.
    [Fact]
    public void M481_a_far_origin_with_large_bounds() =>
        Assert.Contains(Fid(NearSelectionTests.World.LargeFar), (IDictionary<string, List<Nearprobe.Hit>>)Near("0", NearSelectionTests.World.Seed));

    // 482: across the border the gap decides (absent at 0, present at 100). Sabotage S1 (the radius not passed to the
    // engine) is red here: AcrossBorder's gap is 64.6, so at 0 it is absent.
    [Fact]
    public void M482_across_the_border_the_gap_decides()
    {
        Assert.DoesNotContain(Fid(NearSelectionTests.World.AcrossBorder), (IDictionary<string, List<Nearprobe.Hit>>)Near("0", NearSelectionTests.World.Seed));
        Assert.Contains(Fid(NearSelectionTests.World.AcrossBorder), (IDictionary<string, List<Nearprobe.Hit>>)Near("100", NearSelectionTests.World.Seed));
    }

    // 482's far query (100,000 units outdoors, where the engine answers only the cells it read): refused, naming the
    // bound the engine gives (20548.287187078898, derived by hand in NearSelectionTests N1). Sabotage S4 (the
    // comparison skipped) is red here on the exit code.
    [Fact]
    public void M482_far_a_radius_past_the_cells_read_is_refused_naming_the_bound()
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within", "100000"));
        Assert.Contains("20548", why);
        Assert.Contains(SeedFid, why);
        Nearprobe.OneSentence(why);
    }

    // 483: a master reference moved beside the seed is credited to the mover, never the master.
    [Fact]
    public void M483_a_moved_reference_is_credited_to_the_mover()
    {
        var hits = Assert.Contains(Fid(NearSelectionTests.World.Vanilla), (IDictionary<string, List<Nearprobe.Hit>>)Near("0", NearSelectionTests.World.Seed));
        Assert.All(hits, h => Assert.Equal("hcNearMove.esp", h.Provider));
    }

    // 484: terrain and navmesh by their cell square: the seed's cell at 0, the next cell at 100 only.
    [Fact]
    public void M484_terrain_and_navmesh_by_cell_square_both_ways()
    {
        var at0 = Near("0", NearSelectionTests.World.Seed);
        var land = Assert.Contains(Fid(NearSelectionTests.World.LandHere), (IDictionary<string, List<Nearprobe.Hit>>)at0);
        Assert.Contains(land, h => h.Rule == "cell square" && h.Provider == "hcNearTerrain.esp");
        Assert.Contains(Fid(NearSelectionTests.World.NavHere), (IDictionary<string, List<Nearprobe.Hit>>)at0);
        Assert.DoesNotContain(Fid(NearSelectionTests.World.LandNext), (IDictionary<string, List<Nearprobe.Hit>>)at0);
        Assert.Contains(Fid(NearSelectionTests.World.LandNext), (IDictionary<string, List<Nearprobe.Hit>>)Near("100", NearSelectionTests.World.Seed));
    }

    // 485: an interior seed sees its own cell only, at any radius (interior answers have no bound: completeWithin null).
    [Fact]
    public void M485_an_interior_seed_sees_its_own_cell_at_any_radius()
    {
        var a = Answer("--of", Fid(NearSelectionTests.World.SeedInterior), "--within", "100000");
        var m = Nearprobe.Records(a);
        Assert.Contains(Fid(NearSelectionTests.World.InteriorSame), (IDictionary<string, List<Nearprobe.Hit>>)m);
        Assert.DoesNotContain(Fid(NearSelectionTests.World.InteriorOther), (IDictionary<string, List<Nearprobe.Hit>>)m);
        Assert.DoesNotContain(Fid(NearSelectionTests.World.Touching), (IDictionary<string, List<Nearprobe.Hit>>)m);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("completeWithin").ValueKind);
    }

    // 486: an isolated seed lists nothing and the note says what was read.
    [Fact]
    public void M486_an_isolated_seed_lists_nothing_and_says_what_was_read()
    {
        var a = Answer("--of", Fid(NearSelectionTests.World.SeedIsolated), "--within", "0");
        Assert.Empty(Nearprobe.Records(a));
        Assert.Matches(@"read \d+ cell\(s\)", a.GetProperty("note").GetString());
        Assert.True(a.GetProperty("complete").GetBoolean());
    }

    // 487: a base the order cannot size counts by its origin (gap 12.3: absent at 0, present at 20), and the note says so.
    [Fact]
    public void M487_a_base_without_bounds_counts_by_its_origin()
    {
        Assert.DoesNotContain(Fid(NearSelectionTests.World.NoBounds), (IDictionary<string, List<Nearprobe.Hit>>)Near("0", NearSelectionTests.World.Seed));
        var a = Answer("--of", SeedFid, "--within", "20");
        Assert.Contains(Fid(NearSelectionTests.World.NoBounds), (IDictionary<string, List<Nearprobe.Hit>>)Nearprobe.Records(a));
        Assert.Contains("origin only", a.GetProperty("note").GetString());
    }

    // 487b: a match made by a reference's own primitive is labelled 'primitive'; a base-sized one keeps 'sphere'.
    [Fact]
    public void M487b_a_primitive_match_is_labelled_primitive()
    {
        var m = Near("0", NearSelectionTests.World.Seed);
        Assert.All(Assert.Contains(Fid(NearSelectionTests.World.Occluder), (IDictionary<string, List<Nearprobe.Hit>>)m), h => Assert.Equal("primitive", h.Rule));
        Assert.All(m[Fid(NearSelectionTests.World.Touching)], h => Assert.Equal("sphere", h.Rule));
    }

    // 489: a base record, and a reference the order does not hold, are refused in the engine's one sentence.
    [Theory]
    [InlineData("000800:hcNearMaster.esp", "not a placed reference")]
    [InlineData("000800:hcNearAbsent.esp", "load order")]
    public void M489_a_seed_the_question_cannot_be_asked_of_is_refused_in_the_engines_sentence(string seed, string expect)
    {
        var why = Nearprobe.Refusal(Ask("--of", seed, "--within", "0"));
        Assert.Contains(expect, why);
        Assert.Contains(seed, why);
        Nearprobe.OneSentence(why);
    }

    // ---- the command's answer is the engine's ------------------------------------------------------------------

    [Theory]
    [InlineData("0", new[] { "Seed" })]
    [InlineData("100", new[] { "Seed" })]
    [InlineData("20", new[] { "Seed" })]
    [InlineData("0", new[] { "Seed", "SeedIsolated" })]
    [InlineData("100000", new[] { "SeedInterior" })]
    [InlineData("64.5", new[] { "Seed", "SeedInterior" })]
    public void Mq_the_answer_is_the_engines_field_for_field(string within, string[] seedNames)
    {
        var seeds = seedNames.Select(n => (FormKey)typeof(NearSelectionTests.World).GetField(n)!.GetValue(null)!).ToList();
        var a = Answer(seeds.SelectMany(s => new[] { "--of", Fid(s) }).Concat(new[] { "--within", within }).ToArray());
        Nearprobe.SameAsEngine(a, NearReferences.Of(_f.World.Resolver.Capture(), seeds, double.Parse(within, CultureInfo.InvariantCulture)));
    }

    // The bound the engine gives is printed: the Seed's, derived by hand in NearSelectionTests N1.
    [Fact]
    public void Mb_the_answer_carries_the_engines_complete_radius()
    {
        var a = Answer("--of", SeedFid, "--within", "0");
        Assert.Equal(20548.287187078898, a.GetProperty("completeWithin").GetDouble(), 6);
        Assert.True(a.GetProperty("complete").GetBoolean());
        Assert.Equal("nearprobe", a.GetProperty("tool").GetString());
        Assert.Equal(SeedFid, Assert.Single(a.GetProperty("query").GetProperty("seeds").EnumerateArray()).GetString());
        Assert.Equal(0, a.GetProperty("query").GetProperty("within").GetDouble());
    }

    // ---- seeds ---------------------------------------------------------------------------------------------------

    /// <summary>A houseCARL result artifact (to_file=) naming <paramref name="seeds"/>, written by houseCARL's own writer.</summary>
    static string Artifact(string identity, params FormKey[] seeds)
    {
        var path = Path.Combine(Path.GetTempPath(), "nearprobe-seeds-" + Guid.NewGuid().ToString("N")[..10] + ".jsonl");
        using var w = new ResultArtifact.Writer();
        foreach (var s in seeds)
            w.WriteRow((j, _) => { j.WriteStartObject(); j.WriteString(identity, Fid(s)); j.WriteEndObject(); }, "PlacedObject");
        var (m, err) = w.Save(ArtifactTarget.Named(path), "housecarl_records", new List<KeyValuePair<string, string>>(), identity,
                              new[] { identity }, identity, seeds.Length, "test-epoch");
        Assert.True(err is null, err);
        return path;
    }

    [Fact]
    public void Ms1_seeds_from_a_to_file_result_are_the_seeds_named_one_by_one()
    {
        var file = Artifact("formid", NearSelectionTests.World.Seed, NearSelectionTests.World.SeedIsolated);
        try
        {
            var fromFile = Answer("--of", "@" + file, "--within", "0");
            Nearprobe.SameAsEngine(fromFile, NearReferences.Of(_f.World.Resolver.Capture(),
                new[] { NearSelectionTests.World.Seed, NearSelectionTests.World.SeedIsolated }, 0));
            Assert.Equal(2, fromFile.GetProperty("query").GetProperty("seeds").GetArrayLength());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Ms2_a_plain_list_file_is_refused_saying_how_to_pass_the_references()
    {
        var file = Path.Combine(Path.GetTempPath(), "nearprobe-plain-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(file, SeedFid + "\n");
        try
        {
            var why = Nearprobe.Refusal(Ask("--of", "@" + file, "--within", "0"));
            Assert.Contains("--of", why);
            Assert.Contains("to_file=", why);
            Nearprobe.OneSentence(why);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Ms3_a_result_of_other_identities_is_refused()
    {
        var file = Artifact("path", NearSelectionTests.World.Seed);
        try { Assert.Contains("formid", Nearprobe.Refusal(Ask("--of", "@" + file, "--within", "0"))); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Ms4_a_file_mixed_with_named_references_is_refused()
    {
        var file = Artifact("formid", NearSelectionTests.World.Seed);
        try { Nearprobe.OneSentence(Nearprobe.Refusal(Ask("--of", "@" + file, "--of", SeedFid, "--within", "0"))); }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("@relative/seeds.jsonl")]
    [InlineData("@C:/no/such/folder/seeds.jsonl")]
    public void Ms5_a_seeds_file_that_cannot_be_read_is_refused(string of) =>
        Nearprobe.OneSentence(Nearprobe.Refusal(Ask("--of", of, "--within", "0")));

    // A runtime FormID (the console's eight digits) and a hybrid of both forms are refused: write the plugin form.
    [Theory]
    [InlineData("0001A2B3")]
    [InlineData("0001A2B3:hcNearSeed.esp")]
    [InlineData("not-a-formid")]
    [InlineData("800:")]
    public void Ms6_a_token_that_is_not_a_plugin_form_FormID_is_refused(string token)
    {
        // the command's sentence says "write it as XXXXXX:Plugin.esp"; a hybrid gets houseCARL's (RuntimeFormId.HybridNote),
        // which names the six-digit form to write ("Write '01A2B3:hcNearSeed.esp'")
        var why = Nearprobe.Refusal(Ask("--of", token, "--within", "0"));
        Assert.Contains(token, why);
        Assert.True(why.Contains("write it as XXXXXX:Plugin.esp") || why.Contains("Write '01A2B3:hcNearSeed.esp'"), why);
    }

    // ---- the radius: the engine's refusals relayed, the command's own for a value that is not a number -----------

    // Sabotage S5 (engine: NaN let through) is red here on the exit code as well as in the engine's N4.
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-Infinity")]
    [InlineData("-5")]
    public void Mr1_a_distance_the_engine_refuses_is_relayed_in_its_sentence(string within)
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within", within));
        Assert.Contains("distance", why);
        Nearprobe.OneSentence(why);
        var engine = NearReferences.Of(_f.World.Resolver.Capture(), new[] { NearSelectionTests.World.Seed },
                                       double.Parse(within, CultureInfo.InvariantCulture));
        Assert.Equal(engine.Refusal, why);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2,5")]
    [InlineData("")]
    [InlineData("0x10")]
    public void Mr2_a_distance_that_is_not_a_number_is_refused(string within)
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within", within));
        Assert.Contains("--within", why);
        Nearprobe.OneSentence(why);
    }

    [Fact]
    public void Mr3_minus_zero_is_zero() =>
        Assert.Contains(Fid(NearSelectionTests.World.Touching), (IDictionary<string, List<Nearprobe.Hit>>)Near("-0", NearSelectionTests.World.Seed));

    // ---- the command line ----------------------------------------------------------------------------------------

    [Fact]
    public void Mc1_an_unknown_option_is_refused_naming_the_options()
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within", "0", "--radius", "5"));
        Assert.Contains("--radius", why);
        Assert.Contains("--within", why);
        Nearprobe.OneSentence(why);
    }

    [Fact]
    public void Mc2_an_option_without_its_value_is_refused()
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within"));
        Assert.Contains("--within", why);
        Nearprobe.OneSentence(why);
    }

    [Fact]
    public void Mc3_no_reference_is_refused() => Assert.Contains("--of", Nearprobe.Refusal(Ask("--within", "0")));

    [Fact]
    public void Mc4_no_radius_is_refused() => Assert.Contains("--within", Nearprobe.Refusal(Ask("--of", SeedFid)));

    [Fact]
    public void Mc5_a_stray_argument_is_refused()
    {
        var why = Nearprobe.Refusal(Ask("--of", SeedFid, "--within", "0", "extra"));
        Assert.Contains("extra", why);
        Nearprobe.OneSentence(why);
    }

    // No instance named: HarnessTarget's own refusal, as the module's other probes give it (a recorded exception to the
    // one-sentence rule: its first line is the reason, two remedy lines follow).
    [Fact]
    public void Mc6_no_load_order_is_refused_by_the_load_order_owner()
    {
        var r = Nearprobe.Run("--of", SeedFid, "--within", "0");
        Assert.StartsWith("refused: instance-", Nearprobe.Refusal(r));
    }

    // ---- record types (houseCARL's resolver, reached by reflection) -----------------------------------------------

    [Theory]
    [InlineData("REFR", "Mutagen.Bethesda.Skyrim.IPlacedObjectGetter")]
    [InlineData("ACHR", "Mutagen.Bethesda.Skyrim.IPlacedNpcGetter")]
    public void Mt1_a_type_resolves_to_houseCARLs_getter_type(string type, string getter)
    {
        var a = Answer("--of", SeedFid, "--within", "0", "--types", type);
        Assert.Equal(new[] { getter }, a.GetProperty("query").GetProperty("typesResolved").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal(new[] { type }, a.GetProperty("query").GetProperty("types").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    [Fact]
    public void Mt2_an_unknown_type_is_refused_in_houseCARLs_sentence() =>
        Assert.Contains("unknown record type 'QQQQ'", Nearprobe.Refusal(Ask("--of", SeedFid, "--within", "0", "--types", "QQQQ")));

    // In process (not through the command line): when houseCARL's resolver is not where the pin had it, one sentence
    // names the pin and what to try, never an exception's trace.
    [Fact]
    public void Mt3_in_process_a_resolver_the_pin_no_longer_has_is_one_sentence()
    {
        var (types, why) = NearProbeMain.ResolveTypes(new[] { "REFR" }, "HousecarlMcp.NoSuchTypeLookup, housecarl-mcp");
        Assert.Null(types);
        Assert.NotNull(why);
        Assert.Contains("houseCARL", why);
        Assert.Contains("pin", why);
        Nearprobe.OneSentence(why!);
    }

    // ---- the load order --------------------------------------------------------------------------------------------

    // A plugin of the order the index cannot read: the answer is printed, marked incomplete, the plugin named (exit 3).
    [Fact]
    public void Ml1_a_plugin_the_index_cannot_read_is_named_and_the_answer_marked_incomplete()
    {
        var root = Nearprobe.LayOut(_f.World.PluginPaths, garbage: "hcNearGarbage.esp");
        try
        {
            var r = Nearprobe.Ask(root, "--of", SeedFid, "--within", "0");
            var a = Nearprobe.Answer(r, exit: 3);
            Assert.False(a.GetProperty("complete").GetBoolean());
            Assert.Contains(a.GetProperty("notIndexed").EnumerateArray(), n => n.GetProperty("plugin").GetString() == "hcNearGarbage.esp");
            Assert.Contains(Fid(NearSelectionTests.World.Touching), (IDictionary<string, List<Nearprobe.Hit>>)Nearprobe.Records(a));
            Assert.Contains("hcNearGarbage.esp", r.Err);
        }
        finally { Nearprobe.Remove(root); }
    }

    // A plugin enabled without its master: the load-order reader's refusal, relayed.
    [Fact]
    public void Ml2_an_order_the_reader_refuses_is_relayed()
    {
        var root = Nearprobe.LayOut(_f.World.PluginPaths, enabled: new[] { "hcNearSeed.esp" });
        try { Assert.Contains("MISSING MASTER", Nearprobe.Refusal(Nearprobe.Ask(root, "--of", SeedFid, "--within", "0"))); }
        finally { Nearprobe.Remove(root); }
    }

    [Fact]
    public void Ml3_an_order_with_no_active_plugin_is_refused()
    {
        var root = Nearprobe.LayOut(_f.World.PluginPaths, enabled: Array.Empty<string>());
        try
        {
            var why = Nearprobe.Refusal(Nearprobe.Ask(root, "--of", SeedFid, "--within", "0"));
            Assert.Contains("no active plugin", why);
            Nearprobe.OneSentence(why);
        }
        finally { Nearprobe.Remove(root); }
    }
}

/// <summary>The DoorsNear world (the engine's DoorsNearTests) laid out once.</summary>
public sealed class NearprobeDoorsWorld : IDisposable
{
    public DoorsNearTests.World World { get; } = new();
    public string Root { get; }
    public NearprobeDoorsWorld() => Root = Nearprobe.LayOut(World.PluginPaths);
    public void Dispose() { Nearprobe.Remove(Root); World.Dispose(); }
}

/// <summary>nearprobe on the DoorsNear world at the panel's radius (256) with --types REFR: the placed objects near the
/// seed, of which the panel's doors are those whose winner is a load door (DoorsNear's filter, not this command's).</summary>
[Trait("tier", "unit")]
public sealed class NearprobeDoorsTests : IClassFixture<NearprobeDoorsWorld>
{
    readonly NearprobeDoorsWorld _f;
    public NearprobeDoorsTests(NearprobeDoorsWorld f) => _f = f;
    static string Fid(FormKey fk) => Nearprobe.Fid(fk);
    static readonly string SeedFid = Fid(DoorsNearTests.World.Seed);

    JsonElement Answer(params string[] q) => Nearprobe.Answer(Nearprobe.Ask(_f.Root, q));
    IDictionary<string, List<Nearprobe.Hit>> Placed(FormKey seed) =>
        Nearprobe.Records(Answer("--of", Fid(seed), "--within", "256", "--types", "REFR"));

    // D1-D4, D7, D8, D15, D16 (the doors the panel lists), plus the placed objects DoorsNear drops as not doors.
    [Fact]
    public void Md1_the_placed_objects_within_256_by_whichever_version_reaches()
    {
        var m = Placed(DoorsNearTests.World.Seed);
        foreach (var k in new[] { DoorsNearTests.World.Touching, DoorsNearTests.World.JustIn, DoorsNearTests.World.Scaled, DoorsNearTests.World.NoBounds,
                                  DoorsNearTests.World.PrimitiveDoor, DoorsNearTests.World.WideFar, DoorsNearTests.World.MovedAway, DoorsNearTests.World.Persistent,
                                  DoorsNearTests.World.NonLoad, DoorsNearTests.World.WinnerStripped, DoorsNearTests.World.WinnerDeleted })
            Assert.True(m.ContainsKey(Fid(k)), $"{Fid(k)} is not listed");
        Assert.False(m.ContainsKey(Fid(DoorsNearTests.World.JustOut)), "JustOut (gap 261.7) is listed at 256");      // D2
        Assert.False(m.ContainsKey(SeedFid), "the seed is listed as its own neighbour");
    }

    // The type filter: the actor is not a placed object (positive control: without --types it is listed). Sabotage S3
    // (types not passed to the engine) is red here.
    [Fact]
    public void Md2_types_keep_records_whose_winner_is_of_the_type()
    {
        Assert.False(Placed(DoorsNearTests.World.Seed).ContainsKey(Fid(DoorsNearTests.World.Npc)), "the actor is listed under --types REFR");
        Assert.True(Nearprobe.Records(Answer("--of", SeedFid, "--within", "256")).ContainsKey(Fid(DoorsNearTests.World.Npc)), "the actor is not listed without --types");
    }

    // The hit is credited to the version that reached, not the winner: MovedAway's version beside the seed is
    // hcDnDoors.esp's, and hcDnLater.esp (its winner) moved it 1,500 units away; WinnerDeleted's is hcDnDoors.esp's too,
    // its winner hcDnLater.esp deletes it. Sabotage S2 (the winner written as the provider) is red here.
    [Fact]
    public void Md3_a_hit_names_the_plugin_whose_version_reached_not_the_winner()
    {
        var m = Placed(DoorsNearTests.World.Seed);
        Assert.All(m[Fid(DoorsNearTests.World.MovedAway)], h => Assert.Equal("hcDnDoors.esp", h.Provider));
        Assert.All(m[Fid(DoorsNearTests.World.WinnerDeleted)], h => Assert.Equal("hcDnDoors.esp", h.Provider));
    }

    // D9: an interior reference sees its own cell only.
    [Fact]
    public void Md9_an_interior_reference_sees_its_own_cell_only() =>
        Assert.Equal(new[] { Fid(DoorsNearTests.World.InteriorSame) }, Placed(DoorsNearTests.World.SeedInterior).Keys.ToArray());

    // D10: nothing is cut: all 205 doors around the crowd seed are listed (the panel shows 200 and counts the rest).
    [Fact]
    public void Md10_nothing_is_cut()
    {
        var m = Placed(DoorsNearTests.World.SeedCrowd);
        Assert.All(Enumerable.Range(0, DoorsNearTests.World.CrowdSize), i => Assert.True(m.ContainsKey(Fid(DoorsNearTests.World.CrowdDoor(i))), $"crowd door {i} is not listed"));
    }

    // D12: a reference the question cannot be asked of, in the engine's sentence naming it.
    [Theory]
    [InlineData("absent")]
    [InlineData("base")]
    [InlineData("deleted")]
    public void Md12_a_reference_that_cannot_be_asked_of_is_refused(string which)
    {
        var key = which switch { "absent" => new FormKey(ModKey.FromNameAndExtension("hcDnAbsent.esp"), 0x800), "base" => DoorsNearTests.World.DoorBase, _ => DoorsNearTests.World.SeedDeleted };
        var why = Nearprobe.Refusal(Nearprobe.Ask(_f.Root, "--of", Fid(key), "--within", "256"));
        Assert.Contains(Fid(key), why);
        Nearprobe.OneSentence(why);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mdq_the_answer_is_the_engines_field_for_field(bool types)
    {
        var q = new List<string> { "--of", SeedFid, "--of", Fid(DoorsNearTests.World.SeedEast), "--within", "256" };
        if (types) q.AddRange(new[] { "--types", "REFR" });
        Nearprobe.SameAsEngine(Answer(q.ToArray()), NearReferences.Of(_f.World.Resolver.Capture(),
            new[] { DoorsNearTests.World.Seed, DoorsNearTests.World.SeedEast }, 256,
            types ? new[] { typeof(Mutagen.Bethesda.Skyrim.IPlacedObjectGetter) } : null));
    }
}

/// <summary>IN PROCESS, not through the command line: nearprobe's question (<see cref="NearProbeMain.Ask"/>) on the
/// DoorsNearUnread world, whose plugin hcDuGone.esp is deleted after the index is built — a state a fresh process cannot
/// reach (its load-order reader would not find the file at all). These cases reach the engine call, the comparison and
/// the JSON, not the argument handling or the load-order reading.</summary>
[Trait("tier", "unit")]
public sealed class NearprobeUnreadInProcessTests : IClassFixture<DoorsNearUnreadTests.World>
{
    readonly DoorsNearUnreadTests.World _w;
    public NearprobeUnreadInProcessTests(DoorsNearUnreadTests.World w) => _w = w;

    NearOutcome Ask(FormKey seed) => NearProbeMain.Ask(_w.Resolver.Capture(), new NearRequest(new[] { seed }, 256, null, null),
        new Dictionary<string, object?> { ["configuration"] = "in-process test" }, new Dictionary<string, string>());

    // D19: a reference whose cell cannot be read is refused in the engine's one sentence naming it.
    [Fact]
    public void Mu19_in_process_a_reference_whose_cell_cannot_be_read_is_refused()
    {
        var o = Ask(DoorsNearUnreadTests.World.SeedJ);
        Assert.Equal(NearProbeMain.Refused, o.Exit);
        Assert.Equal("", o.Json);
        Assert.NotNull(o.Sentence);
        Assert.Contains(Nearprobe.Fid(DoorsNearUnreadTests.World.SeedJ), o.Sentence);
        Assert.Contains("cell", o.Sentence);
    }

    // D20: a plugin that cannot be read is named, the answer marked incomplete (exit 3), the rest listed.
    [Fact]
    public void Mu20_in_process_an_unreadable_plugin_is_named_and_the_rest_listed()
    {
        var o = Ask(DoorsNearUnreadTests.World.SeedU);
        Assert.Equal(NearProbeMain.Incomplete, o.Exit);
        Assert.NotNull(o.Sentence);
        Assert.Contains("hcDuGone.esp", o.Sentence);
        var a = Nearprobe.Answer((o.Exit, o.Json, o.Sentence!), exit: 3);
        Assert.False(a.GetProperty("complete").GetBoolean());
        Assert.Contains("hcDuGone.esp", a.GetProperty("unread").EnumerateArray().Select(u => u.GetString()));
        Assert.Contains(Nearprobe.Fid(DoorsNearUnreadTests.World.DoorU), (IDictionary<string, List<Nearprobe.Hit>>)Nearprobe.Records(a));
        Assert.DoesNotContain(Nearprobe.Fid(DoorsNearUnreadTests.World.DoorX), (IDictionary<string, List<Nearprobe.Hit>>)Nearprobe.Records(a));
    }
}
