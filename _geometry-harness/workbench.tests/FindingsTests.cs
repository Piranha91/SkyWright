using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using HarnessShared;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Tests that set the window's shared state (<see cref="Shell"/>'s statics) run one at a time.</summary>
[CollectionDefinition("Shell", DisableParallelization = true)]
public sealed class ShellCollection { }

/// <summary>H3's fixture (Gate B review, DESIGN R4.3): a hand-built bundle with a verify-bundle verdict of its own, so the
/// reader, the ranking and the state tools are tested without the controller. Arms baseline and candidate-01 (the review
/// arm). Overlap rows (Conflicts): 7 created on the review arm, of which the differential's capped examples name 2. Findings:
/// navmesh-ownership rows (calibrated-inference), one water row (record-fact) and two switch-census json entries.</summary>
public sealed class FindingsBundle : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-wb-findings-" + Guid.NewGuid().ToString("N"));
    public string Dir => Path.Combine(Root, "bundle");
    public string Verify => Path.Combine(Root, "verify.json");
    public const string Sel = "HcSel.esp", Other = "HcOther.esp", Base = "HcBase.esm";
    public const string CellA = "000A01:HcBase.esm";
    readonly JsonArray _outputs = new();

    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    static Conflict Nav(string a, string b, string fk, PhysicalState? ph = PhysicalState.Holds) =>
        new("CLOBBERED", a, b, "a navmesh row", new[] { new ConflictRecord(fk, "placed-ref", null, ph) });

    void Receipt(string key, string check, string output, string relOut, object? file, string adapterOutput)
    {
        var full = Path.Combine(Dir, relOut.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (file is ConflictFile cf) ConflictIo.Write(full, cf); else File.WriteAllText(full, (string)file!);
        File.WriteAllText(Path.Combine(Dir, "receipts", key + ".json"), JsonSerializer.Serialize(new
        {
            checkId = check, validation = new { accepted = true, sentence = "accepted" },
            outputs = new[] { new { path = relOut } }, adapter = new { output = adapterOutput },
        }));
        _outputs.Add(new JsonObject { ["receipt"] = key, ["recordedPath"] = relOut, ["path"] = relOut, ["present"] = true, ["matches"] = true, ["sha256"] = Sha(full) });
    }

    /// <param name="variant">full; findingsOnly (no overlap rows); unclickable (navmesh and water rows only, and no
    /// names, so no row opens the viewer); clean (every receipt accepted, no row anywhere).</param>
    public FindingsBundle(bool findingsOnly = false, string variant = "full")
    {
        Directory.CreateDirectory(Path.Combine(Dir, "receipts"));
        var checks = new JsonArray(
            new JsonObject { ["checkId"] = "geometry.object-overlap", ["evidence"] = new JsonObject { ["strength"] = "calibrated-inference" } },
            new JsonObject { ["checkId"] = "geometry.navmesh-ownership", ["evidence"] = new JsonObject { ["strength"] = "calibrated-inference" } },
            new JsonObject { ["checkId"] = "environment.water-boundaries", ["evidence"] = new JsonObject { ["strength"] = "record-fact" } },
            new JsonObject { ["checkId"] = "state.switch-census", ["evidence"] = new JsonObject { ["strength"] = "record-fact" } });
        File.WriteAllText(Path.Combine(Dir, "plan.json"), new JsonObject
        {
            ["planId"] = "findings-fixture", ["reviewArm"] = "candidate-01",
            ["arms"] = new JsonArray(new JsonObject { ["armId"] = "baseline", ["kind"] = "baseline" }, new JsonObject { ["armId"] = "candidate-01", ["kind"] = "candidate" }),
            ["targets"] = new JsonArray(new JsonObject { ["name"] = Sel }), ["checks"] = checks,
        }.ToJsonString());
        string T = "2026-11-15T00:00:00Z";
        bool clean = variant == "clean", unclickable = variant == "unclickable";
        if (clean || unclickable) findingsOnly = true;
        if (!findingsOnly)
        {
            var created = Enumerable.Range(0, 7).Select(i => new Conflict("OVERLAP", Sel, Base, "an overlap",
                new[] { new ConflictRecord($"0008{i:X2}:{Sel}", "placed-ref", null, PhysicalState.Holds) },
                new[] { new ConflictRecord("000900:" + Base, "placed-ref", null) })).ToList();
            Receipt("geometry.object-overlap__baseline__arm", "geometry.object-overlap", "conflict-file", "outputs/ov/baseline.json", new ConflictFile("overlapprobe", "i", T, Array.Empty<Conflict>()), "conflict-file");
            Receipt("geometry.object-overlap__candidate-01__arm", "geometry.object-overlap", "conflict-file", "outputs/ov/candidate-01.json", new ConflictFile("overlapprobe", "i", T, created), "conflict-file");
            var ids = created.Take(2).Select(c => ConflictIo.RowIdentity(c.Kind, c.Necessary[0].FormKey, c.Alternative![0].FormKey, c.A, null)).ToArray();
            File.WriteAllText(Path.Combine(Dir, "receipts", "geometry.object-overlap__candidate-01__diff.json"), JsonSerializer.Serialize(new
            { orderEvaluation = new { created = new { created = 7, newDefect = 7, unmeasured = 0, newExamples = ids } } }));
        }
        // navmesh: N1 target vs N2 not (key 1); N3 created vs N4 in both (key 2); N6 Holds vs N7 unmeasured (key 4);
        // N8 a Static base vs N9 unread (key 5)
        var navBase = new[] { Nav(Other, Base, "000B00:" + Other), Nav(Other, Base, "000B04:" + Other), Nav(Other, Base, "000B06:" + Other), Nav(Other, Base, "000B07:" + Other, null),
                              Nav(Other, Base, "000B08:" + Other), Nav(Other, Base, "000B09:" + Other) };
        if (clean) navBase = Array.Empty<Conflict>();
        var navCand = clean ? navBase : navBase.Concat(new[] { Nav(Sel, Base, "000B01:" + Sel), Nav(Other, Base, "000B02:" + Other), Nav(Other, Base, "000B03:" + Other) }).ToArray();
        // N1 and N2 are created too; N1 is the only one naming a target
        Receipt("geometry.navmesh-ownership__baseline__arm", "geometry.navmesh-ownership", "conflict-file", "outputs/nav/baseline.json", new ConflictFile("navownprobe", "i", T, navBase), "conflict-file");
        Receipt("geometry.navmesh-ownership__candidate-01__arm", "geometry.navmesh-ownership", "conflict-file", "outputs/nav/candidate-01.json", new ConflictFile("navownprobe", "i", T, navCand), "conflict-file");
        var water = clean ? Array.Empty<Conflict>() : new[] { new Conflict("WATER", Other, Base, "a water row", new[] { new ConflictRecord("000C01:" + Other, "placed-ref", null, PhysicalState.Holds) }) };
        Receipt("environment.water-boundaries__baseline__arm", "environment.water-boundaries", "conflict-file", "outputs/water/baseline.json", new ConflictFile("waterprobe", "i", T, water), "conflict-file");
        Receipt("environment.water-boundaries__candidate-01__arm", "environment.water-boundaries", "conflict-file", "outputs/water/candidate-01.json", new ConflictFile("waterprobe", "i", T, water), "conflict-file");
        Receipt("state.switch-census__candidate-01", "state.switch-census", "json", "outputs/switch/candidate-01.json",
                clean || unclickable ? """{"summary":{},"perPlugin":[],"unreadable":[],"switches":[],"jointFlips":[]}""" : """{"summary":{},"perPlugin":[],"unreadable":[],"switches":[{"key":"000D02:HcOther.esp","edid":"HcSw2","cell":"HcCell (int)","cellKey":"000A01:HcBase.esm","position":"(0, 0, 0)","owner":"HcOther.esp"},{"key":"000D01:HcOther.esp","edid":"HcSw1","cell":"HcCell (int)","cellKey":"000A01:HcBase.esm","position":"(0, 0, 0)","owner":"HcOther.esp"}],"jointFlips":[]}""", "json");
        if (variant == "full")
        {
            // a ticked check that was rejected, and one that was not ticked (the plan lists every check)
            foreach (var (key, exec, acc) in new[] { ("patch.discovery-installed__candidate-01", "rejected(exit-nonzero)", false), ("patch.discovery-pool__candidate-01", "skipped-user", false) })
                File.WriteAllText(Path.Combine(Dir, "receipts", key + ".json"), JsonSerializer.Serialize(new
                {
                    checkId = key.Split("__")[0], armId = "candidate-01", outputs = Array.Empty<object>(), result = new { execution = exec },
                    validation = new { accepted = acc, rejections = new[] { new { cause = "exit-nonzero", detail = "exit code 1" } } },
                }));
        }
        // names: the navmesh references sit in CellA; N8's base is a Static
        var refs = new JsonObject();
        foreach (var fk in new[] { "000B00:" + Other, "000B01:" + Sel, "000B02:" + Other, "000B03:" + Other, "000B04:" + Other, "000B06:" + Other, "000B07:" + Other, "000B08:" + Other, "000B09:" + Other, "000C01:" + Other })
            refs[fk] = new JsonObject { ["base"] = fk.EndsWith("B08:" + Other) ? "000E01:" + Base : null, ["carriedBy"] = new JsonArray(fk.Split(':')[1]), ["cells"] = new JsonObject { [fk.Split(':')[1]] = CellA } };
        if (unclickable) refs = new JsonObject();               // the names read reached none of these references
        Directory.CreateDirectory(Path.Combine(Dir, "enrichment", "names"));
        File.WriteAllText(Path.Combine(Dir, "enrichment", "names", "names.json"), new JsonObject
        {
            ["refs"] = refs, ["bases"] = new JsonObject { ["000E01:" + Base] = new JsonObject { ["type"] = "Static", ["editorId"] = "HcStatic" } },
        }.ToJsonString());
        File.WriteAllText(Verify, new JsonObject
        {
            ["bundle"] = new JsonObject { ["structurallyValid"] = true, ["complete"] = true, ["defects"] = new JsonArray(), ["mismatched"] = new JsonArray(), ["missing"] = new JsonArray() },
            ["toolIdentity"] = new JsonObject { ["guarantee"] = "verified" }, ["receiptOutputs"] = _outputs,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Root, "severity.json"), """{ "unread": 25, "default": 25, "weights": { "*": { "Static": 50 } } }""");
        // a stand-in for evalrun's verify-bundle that hands back this fixture's verdict (OpenBundle's own verify step)
        File.WriteAllText(Path.Combine(Root, "fake_evalrun.py"),
            "import shutil, sys\nshutil.copy(sys.argv[0].replace('fake_evalrun.py', 'verify.json'), sys.argv[sys.argv.index('--out') + 1])\nprint('verify: fixture verdict')\n");
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

/// <summary>H3 (Gate B review, DESIGN R4.3): routing, order, the projection, a click and the open of a Findings-only
/// bundle, and the Conflicts tab marking every created row new.</summary>
[Collection("Shell"), Trait("tier", "integration")]
public sealed class FindingsTests
{
    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    static Dispatcher UiThread()
    {
        Dispatcher? d = null; using var ready = new ManualResetEventSlim();
        var t = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait();
        return d!;
    }

    /// <summary>A surface over an opened bundle and no scene, as the window has before a cell is built. Its public
    /// <c>Open</c> and <c>Severity</c> are the bundle and the table the state tools read the Findings from.</summary>
    sealed class BundleSurface : IWorkbenchSurface
    {
        public BundleSurface(BundleConflicts b, SeverityTable sev) { Open = b; Severity = sev; Listed = ConflictRanking.Rank(b.Rows, b.Targets, sev); }
        public BundleConflicts? Open { get; }
        public SeverityTable? Severity { get; }
        public object? Bundle => new { planId = Open!.PlanId, arm = Open.Arm };
        public IReadOnlyList<RankedRow> Listed { get; }
        public int Total => Listed.Count;
        public RankedRow? OpenRow => null;
        public string Banner => "fixture";
        public string? Loading => null;
        public IWorkbenchScene? Scene => null;
        public string OpenConflict(RankedRow row) => "opened " + row.Rank;
    }

    /// <summary>The fixture opened as the window opens a bundle (BundleConflicts.Read on its verdict), served on the wire.</summary>
    static async Task<(JsonElement State, Func<Dictionary<string, object?>, Task<(bool, string)>> Act, IAsyncDisposable Stop)> Serve(FindingsBundle f, Dictionary<string, object?> args)
    {
        var sev = SeverityTable.Load(Path.Combine(f.Root, "severity.json"));
        var b = BundleConflicts.Read(f.Dir, "candidate-01", f.Verify, "verify: fixture verdict");
        var ui = UiThread();
        var server = await StateServer.StartAsync(FreePort(), new StateHub(ui, new BundleSurface(b, sev)) { Wait = TimeSpan.FromSeconds(5) });
        var c = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(server.Url + "/"), TransportMode = HttpTransportMode.StreamableHttp }));
        async Task<(bool, string)> Call(string tool, Dictionary<string, object?> a)
        {
            var r = await c.CallToolAsync(tool, a);
            return (r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(x => x.Text)));
        }
        var (err, text) = await Call("workbench_state", args);
        Assert.False(err, "workbench_state refused: " + text);
        using var d = JsonDocument.Parse(text);
        return (d.RootElement.Clone(), a => Call("workbench_act", a), new Stopper(async () => { await c.DisposeAsync(); await server.DisposeAsync(); ui.InvokeShutdown(); }));
    }

    sealed class Stopper(Func<Task> f) : IAsyncDisposable { public async ValueTask DisposeAsync() => await f(); }

    static JsonElement P(JsonElement e, params string[] path)
    {
        foreach (var n in path)
        {
            JsonElement v = default;
            Assert.True(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out v), $"no '{n}' in {e.GetRawText()[..Math.Min(300, e.GetRawText().Length)]}");
            e = v;
        }
        return e;
    }

    static List<JsonElement> FindingRows(JsonElement s) => P(s, "findings", "rows").EnumerateArray().ToList();
    static int RankOf(List<JsonElement> rows, string refKey) => P(rows.Single(r => r.TryGetProperty("ref", out var x) && x.GetString() == refKey), "rank").GetInt32();

    [Fact]
    public async Task H3a_Findings_take_every_check_but_the_three_placed_ones_and_Conflicts_take_only_those()
    {
        using var f = new FindingsBundle();
        var (s, _, stop) = await Serve(f, new() { ["project"] = new[] { "conflicts", "findings" }, ["limit"] = 100 });
        await using var _s = stop;
        Assert.All(P(s, "conflicts", "rows").EnumerateArray(), r => Assert.Equal("OVERLAP", P(r, "kind").GetString()));
        var checks = FindingRows(s).Select(r => P(r, "checkId").GetString()).ToHashSet();
        Assert.Equal(new[] { "environment.water-boundaries", "geometry.navmesh-ownership", "state.switch-census" }, checks.OrderBy(x => x));
        Assert.Equal(9 + 1 + 2, FindingRows(s).Count);
    }

    [Fact]
    public async Task H3b_Findings_are_ranked_by_the_six_keys_one_pair_per_key()
    {
        using var f = new FindingsBundle();
        var (s, _, stop) = await Serve(f, new() { ["project"] = new[] { "findings" }, ["limit"] = 100 });
        await using var _s = stop;
        var rows = FindingRows(s);
        string O(string id) => id + ":" + FindingsBundle.Other;
        Assert.True(RankOf(rows, "000B01:" + FindingsBundle.Sel) < RankOf(rows, O("000B02")), "key 1: a row naming a target ranks first");
        Assert.True(RankOf(rows, O("000B03")) < RankOf(rows, O("000B00")), "key 2: a row new on the review arm ranks before one that is not, whose identity sorts first");
        Assert.True(RankOf(rows, O("000C01")) < RankOf(rows, O("000B04")), "key 3: record-fact before calibrated-inference");
        Assert.True(RankOf(rows, O("000B06")) < RankOf(rows, O("000B07")), "key 4: holds before unmeasured");
        Assert.True(RankOf(rows, O("000B08")) < RankOf(rows, O("000B09")), "key 5: the heavier base type first");
        Assert.True(RankOf(rows, O("000D01")) < RankOf(rows, O("000D02")), "key 6: identity breaks the tie");
    }

    [Fact]
    public async Task H3c_the_findings_projection_pages_and_filters_by_check()
    {
        using var f = new FindingsBundle();
        var (s, _, stop) = await Serve(f, new() { ["project"] = new[] { "findings" }, ["limit"] = 2, ["offset"] = 1, ["check"] = "geometry.navmesh-ownership" });
        await using var _s = stop;
        Assert.Equal(9, P(s, "findings", "matched").GetInt32());
        Assert.Equal(12, P(s, "findings", "total").GetInt32());
        var rows = FindingRows(s);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("geometry.navmesh-ownership", P(r, "checkId").GetString()));
    }

    [Fact]
    public async Task H3d_a_Findings_row_is_clickable_at_its_reference_and_cell_and_a_row_that_is_not_says_why_when_selected()
    {
        using var f = new FindingsBundle();
        var (s, act, stop) = await Serve(f, new() { ["project"] = new[] { "findings" }, ["limit"] = 100 });
        await using var _s = stop;
        var rows = FindingRows(s);
        var nav = rows.Single(r => P(r, "ref").GetString() == "000B01:" + FindingsBundle.Sel);
        Assert.True(P(nav, "clickable").GetBoolean());
        Assert.Equal(FindingsBundle.CellA, P(nav, "cell").GetString());
        var (err, text) = await act(new() { ["verb"] = "select", ["finding"] = 999 });
        Assert.True(err && text.Contains("999"), "selecting a finding that is not listed was not refused naming it: " + text);
    }

    [Fact]
    public void H3e_every_row_the_review_arm_created_is_marked_new_not_only_the_differentials_examples()
    {
        using var f = new FindingsBundle();
        var b = BundleConflicts.Read(f.Dir, "candidate-01", f.Verify, "verify: fixture verdict");
        var overlap = b.Rows.Where(r => r.CheckId == "geometry.object-overlap").ToList();
        Assert.Equal(7, overlap.Count);
        Assert.True(overlap.All(r => r.NewOnArm == true), $"{overlap.Count(r => r.NewOnArm == true)} of 7 created rows are marked new");
    }

    /// <summary>Opens a bundle as the window does (OpenBundle, on an STA thread) and returns its answer or the failure.</summary>
    static (string[]? Args, Exception? Failed) OpenOnSta(FindingsBundle f)
    {
        var open = typeof(PatchWriter).Assembly.GetType("Workbench.Program")!.GetMethod("OpenBundle", BindingFlags.Static | BindingFlags.NonPublic)!;
        Environment.SetEnvironmentVariable("WB_EVALRUN", Path.Combine(f.Root, "fake_evalrun.py"));
        Environment.SetEnvironmentVariable("WB_SEVERITY", Path.Combine(f.Root, "severity.json"));
        Environment.SetEnvironmentVariable("WB_OUT", Path.Combine(f.Root, "work"));
        string[]? args = null; Exception? failed = null;
        try
        {
            var t = new Thread(() =>
            {
                try { args = (string[]?)open.Invoke(null, new object[] { new[] { "--bundle", f.Dir, "--arm", "candidate-01" } }); }
                catch (TargetInvocationException ex) { failed = ex.InnerException ?? ex; }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        }
        finally { foreach (var v in new[] { "WB_EVALRUN", "WB_SEVERITY", "WB_OUT" }) Environment.SetEnvironmentVariable(v, null); }
        return (args, failed);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("unclickable")]
    public void H3g_a_bundle_with_no_row_to_open_is_opened_with_no_scene_and_its_rows_listed(string variant)
    {
        using var f = new FindingsBundle(variant: variant);
        var (args, failed) = OpenOnSta(f);
        Assert.True(failed is null, $"the {variant} bundle was not opened: {failed?.Message}");
        Assert.Null(args);                                                   // no scene to build
        var findings = (System.Collections.ICollection)typeof(PatchWriter).Assembly.GetType("Workbench.Shell")!
            .GetField("Findings", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal(variant == "clean" ? 0 : 9 + 1, findings.Count);
    }

    [Fact]
    public void H3f_a_bundle_whose_only_rows_are_Findings_rows_opens_on_a_Findings_cell()
    {
        using var f = new FindingsBundle(findingsOnly: true);
        var open = typeof(PatchWriter).Assembly.GetType("Workbench.Program")!.GetMethod("OpenBundle", BindingFlags.Static | BindingFlags.NonPublic)!;
        Environment.SetEnvironmentVariable("WB_EVALRUN", Path.Combine(f.Root, "fake_evalrun.py"));
        Environment.SetEnvironmentVariable("WB_SEVERITY", Path.Combine(f.Root, "severity.json"));
        Environment.SetEnvironmentVariable("WB_OUT", Path.Combine(f.Root, "work"));
        try
        {
            string[]? args = null; Exception? failed = null;
            var t = new Thread(() =>                                   // the window's controls need an STA thread
            {
                try { args = (string[])open.Invoke(null, new object[] { new[] { "--bundle", f.Dir, "--arm", "candidate-01" } })!; }
                catch (TargetInvocationException ex) { failed = ex.InnerException ?? ex; }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (failed is not null) throw new Xunit.Sdk.XunitException("the Findings-only bundle did not open: " + failed.Message);
            var wl = args![Array.IndexOf(args, "--worklist") + 1];
            Assert.Contains(FindingsBundle.CellA, File.ReadAllText(wl));
        }
        finally
        {
            foreach (var v in new[] { "WB_EVALRUN", "WB_SEVERITY", "WB_OUT" }) Environment.SetEnvironmentVariable(v, null);
        }
    }

    [Fact]
    public async Task H3h_each_ticked_check_says_whether_it_ran_or_why_not_and_an_unticked_one_is_left_out()
    {
        using var f = new FindingsBundle();
        var (s, _, stop) = await Serve(f, new() { ["project"] = new[] { "findings" }, ["limit"] = 1 });
        await using var _s = stop;
        var checks = P(s, "findings", "checks").EnumerateArray().ToList();
        string? State(string id) => checks.Where(c => P(c, "checkId").GetString() == id).Select(c => P(c, "state").GetString()).SingleOrDefault();
        Assert.Equal("ran", State("geometry.navmesh-ownership"));
        Assert.Equal("rejected", State("patch.discovery-installed"));
        Assert.Contains("exit-nonzero", P(checks.Single(c => P(c, "checkId").GetString() == "patch.discovery-installed"), "why").GetString());
        Assert.Null(State("patch.discovery-pool"));
    }
}
