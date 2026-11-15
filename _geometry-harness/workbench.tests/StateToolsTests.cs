using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda.Plugins;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The MCP state tools over the wire: the app's own server (<see cref="StateServer"/>) on a localhost port, its
/// calls carried onto a real dispatcher thread by <see cref="StateHub"/>, and an MCP client (ModelContextProtocol.Core
/// <c>HttpClientTransport</c>) calling <c>workbench_state</c> and <c>workbench_act</c>. The scene behind them is a real
/// <see cref="EditSession"/> over a one-reference scene; the shell and the host's write and re-check are stand-ins that
/// record what they were asked (the window's own write and re-check are exercised by the end-to-end run).</summary>
[Trait("tier", "integration")]
public sealed class StateToolsTests : IAsyncLifetime
{
    sealed class FakeScene : IWorkbenchScene
    {
        internal FakeScene(EditSession s) { Session = s; }
        public EditSession Session { get; }
        public string? Cell => "00B4BA:Skyrim.esm";
        public string Status => "status bar text";
        public string? CellOf(FormKey key) => Cell;
        public void Select(FormKey? key) => Session.Select(key);
        public void AfterStage() { }
        internal readonly List<(bool Dry, string Patch)> Commits = new();
        internal readonly List<string> Rechecks = new();
        internal TaskCompletionSource RecheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> CommitAsync(bool dryRun, string patch)
        {
            Commits.Add((dryRun, patch));
            CommitRefusal = null;
            if (patch == "Taken") { CommitRefusal = "the mod folder 'x' already holds Taken.esp; write to a new patch name."; return Task.FromResult<string?>(null); }
            if (dryRun) return Task.FromResult<string?>($"would write {Session.Net().Count} placement override(s) to {patch}.esp (dry run)");
            Session.MarkWritten(patch + ".esp");
            StartRecheck(patch);
            return Task.FromResult<string?>($"wrote {patch}.esp");
        }
        public string? CommitRefusal { get; private set; }
        public Task<string?> StartRecheck(string patch)
        {
            Rechecks.Add(patch);
            Rechecking = true;
            var t = RecheckGate.Task.ContinueWith(_ =>
            {
                LastRecheck = new RecheckResult(new RowVerdict("overlap|000800:hcwbtest.esp|000900:hcwbtest.esp", "resolved", "repaired", "resolved: the overlap is gone."),
                                                Array.Empty<RowVerdict>(), "DIFFERENTIAL-VERIFIED", Array.Empty<string>(), "same build", 1.5);
                Rechecking = false;
                return (string?)"resolved";
            });
            RecheckRunning = t;
            return t;
        }
        public Task? RecheckRunning { get; private set; }
        public RecheckResult? LastRecheck { get; private set; }
        public string? RecheckRefusal => null;
        public bool Rechecking { get; private set; }
    }

    sealed class FakeSurface : IWorkbenchSurface
    {
        internal FakeScene? SceneShown;
        internal IReadOnlyList<RankedRow> Rows = Array.Empty<RankedRow>();
        internal readonly List<int> Opened = new();
        public object? Bundle => new { planId = "test-plan", arm = "baseline" };
        public IReadOnlyList<RankedRow> Listed => Rows;
        public int Total => Rows.Count;
        public RankedRow? OpenRow { get; set; }
        public string Banner => "banner text";
        public string? Loading { get; set; }
        public IWorkbenchScene? Scene => SceneShown;
        public string OpenConflict(RankedRow row)
        {
            Opened.Add(row.Rank); OpenRow = row;
            SceneShown!.Session.Select(FormKey.Factory(row.Row.RefKey));
            return $"opened conflict {row.Rank}";
        }
    }

    static readonly string RefKey = "000800:HcWbTest.esp";
    Dispatcher _ui = null!;
    FakeSurface _surface = null!;
    FakeScene _scene = null!;
    StateHub _hub = null!;
    StateServer _server = null!;
    McpClient _client = null!;

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    static Dispatcher UiThread()
    {
        Dispatcher? d = null; using var ready = new ManualResetEventSlim();
        var t = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = "fake UI thread" };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait();
        return d!;
    }

    static ConflictRow Row(string id, string refKey, string other) =>
        new(id, "geometry.object-overlap", "OVERLAP", "Subject.esp", "Other.esp", "", refKey, other, PhysicalState.Holds, "00B4BA:Skyrim.esm",
            "Static", "TestBarrel", "Flora", "TestShrub", "calibrated-inference", false);

    public async Task InitializeAsync()
    {
        _ui = UiThread();
        var ro = PlacementTests.OneRef(out _);
        _scene = new FakeScene(new EditSession(new[] { ro }));
        var sev = Path.Combine(Path.GetTempPath(), "hc-wb-sev-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(sev, """{ "unread": 25, "default": 25, "weights": { "*": { "Static": 50 } } }""");
        var rows = ConflictRanking.Rank(new[] { Row("a-first", RefKey, "000900:HcWbTest.esp"), Row("b-second", "000801:HcWbTest.esp", "000902:HcWbTest.esp") },
                                        new[] { "Subject.esp" }, SeverityTable.Load(sev));
        _surface = new FakeSurface { SceneShown = _scene, Rows = rows };
        _hub = new StateHub(_ui, _surface) { Wait = TimeSpan.FromSeconds(2) };
        _server = await StateServer.StartAsync(FreePort(), _hub);
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_server.Url + "/"), TransportMode = HttpTransportMode.StreamableHttp }));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _ui.InvokeShutdown();
    }

    async Task<(bool Error, string Text)> Call(string tool, Dictionary<string, object?> args)
    {
        var r = await _client.CallToolAsync(tool, args);
        return (r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    async Task<JsonElement> Ok(string tool, Dictionary<string, object?> args)
    {
        var (err, text) = await Call(tool, args);
        Assert.False(err, $"{tool} refused: {text}");
        using var d = JsonDocument.Parse(text);
        return d.RootElement.Clone();
    }

    async Task<string> Refused(string tool, Dictionary<string, object?> args)
    {
        var (err, text) = await Call(tool, args);
        Assert.True(err, $"{tool} answered instead of refusing: {text}");
        Assert.DoesNotContain("\n", text);                                   // one sentence
        Assert.EndsWith(".", text);
        return text;
    }

    /// <summary>A property path in an answer; a missing one fails here, on an assertion, naming what was there.</summary>
    static JsonElement P(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            JsonElement v = default;
            Assert.True(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v), $"the answer has no '{name}' where expected: {Short(e)}");
            e = v;
        }
        return e;
    }
    static string Short(JsonElement e) { var s = e.GetRawText(); return s.Length > 400 ? s[..400] + "..." : s; }

    static Dictionary<string, object?> A(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
    static float[] Pos(JsonElement pose) => P(pose, "position").EnumerateArray().Select(e => e.GetSingle()).ToArray();

    [Fact]
    public async Task The_server_publishes_the_two_tools_with_their_inputs()
    {
        var tools = await _client.ListToolsAsync();
        Assert.Equal(new[] { "workbench_act", "workbench_state" }, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        var act = tools.Single(t => t.Name == "workbench_act").JsonSchema;
        var props = P(act, "properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var p in new[] { "verb", "conflict", "ref", "delta", "turn_degrees", "position", "rotation_degrees", "dry_run", "patch" })
            Assert.True(props.Contains(p), $"workbench_act has no '{p}' input (has: {string.Join(", ", props)})");
        Assert.Contains("verb", P(act, "required").EnumerateArray().Select(e => e.GetString()));
        var state = P(tools.Single(t => t.Name == "workbench_state").JsonSchema, "properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var p in new[] { "project", "limit", "offset", "filter", "wait_seconds" })
            Assert.True(state.Contains(p), $"workbench_state has no '{p}' input");
    }

    [Fact]
    public async Task State_projects_the_window_and_the_asked_for_parts_and_refuses_an_unknown_part()
    {
        var all = await Ok("workbench_state", A(("project", new[] { "bundle", "conflicts" })));
        Assert.Equal("00B4BA:Skyrim.esm", P(all, "window", "cell").GetString());
        Assert.Equal("test-plan", P(all, "bundle", "planId").GetString());
        var rows = P(all, "conflicts", "rows").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => P(r, "rank").GetInt32()));
        Assert.Equal(RefKey, P(rows[0], "ref").GetString());
        Assert.False(all.TryGetProperty("selection", out _), "a part not asked for was projected");
        var filtered = await Ok("workbench_state", A(("project", new[] { "conflicts" }), ("filter", "000801")));
        Assert.Equal(1, P(filtered, "conflicts", "matched").GetInt32());
        var dflt = await Ok("workbench_state", A());
        Assert.True(dflt.TryGetProperty("selection", out _) && dflt.TryGetProperty("pending", out _) && dflt.TryGetProperty("recheck", out _), "the default projection lacks selection, pending or recheck");
        Assert.Contains("'cells' is not a part", await Refused("workbench_state", A(("project", new[] { "cells" }))));
    }

    [Fact]
    public async Task Select_opens_a_conflict_by_rank_or_selects_a_drawn_reference_and_refuses_otherwise()
    {
        var r = await Ok("workbench_act", A(("verb", "select"), ("conflict", 1)));
        Assert.Equal(new[] { 1 }, _surface.Opened);
        Assert.Equal(RefKey, P(r, "state", "selection", "ref").GetString());
        Assert.Equal(1, P(r, "state", "openConflict", "rank").GetInt32());
        Assert.Contains("no conflict ranked 9", await Refused("workbench_act", A(("verb", "select"), ("conflict", 9))));
        await Ok("workbench_act", A(("verb", "select"), ("ref", "none")));
        Assert.Null(_scene.Session.Selected);
        var s = await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        Assert.Equal($"selected {RefKey}", P(s, "said").GetString());
        Assert.Contains("is not a drawn reference", await Refused("workbench_act", A(("verb", "select"), ("ref", "000123:Skyrim.esm"))));
        Assert.Contains("is not a reference key", await Refused("workbench_act", A(("verb", "select"), ("ref", "the barrel"))));
    }

    [Fact]
    public async Task Stage_moves_and_turns_the_selected_reference_in_the_edit_session()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var before = _scene.Session.Current(FormKey.Factory(RefKey));
        var r = await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { -70f, 0f, 0f }), ("turn_degrees", 30f)));
        var pending = P(r, "state", "pending").EnumerateArray().ToList();
        Assert.Single(pending);
        Assert.Equal(new[] { -70f, 0f, 0f }, P(pending[0], "moved").EnumerateArray().Select(e => e.GetSingle()));
        var drawn = Pos(P(r, "state", "selection", "drawn"));
        Assert.Equal(new[] { before.Position.X - 70, before.Position.Y, before.Position.Z }, drawn);
        Assert.NotEqual(before.Rotation, _scene.Session.Current(FormKey.Factory(RefKey)).Rotation);
        var abs = await Ok("workbench_act", A(("verb", "stage"), ("position", new[] { 10f, 20f, 30f })));
        Assert.Equal(new[] { 10f, 20f, 30f }, Pos(P(abs, "state", "selection", "drawn")));
        Assert.Contains("not both", await Refused("workbench_act", A(("verb", "stage"), ("delta", new[] { 1f, 0f, 0f }), ("position", new[] { 1f, 2f, 3f }))));
        Assert.Contains("three finite numbers", await Refused("workbench_act", A(("verb", "stage"), ("delta", new[] { 1f, 0f }))));
        Assert.Equal(2, _scene.Session.Pending.Count);
    }

    [Fact]
    public async Task Undo_takes_back_the_last_staged_edit_and_refuses_when_there_is_none()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var before = _scene.Session.Current(FormKey.Factory(RefKey));
        await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 0f, 50f, 0f })));
        var u = await Ok("workbench_act", A(("verb", "undo")));
        Assert.Empty(P(u, "state", "pending").EnumerateArray());
        Assert.Equal(new[] { before.Position.X, before.Position.Y, before.Position.Z }, Pos(P(u, "state", "selection", "drawn")));
        Assert.Contains("no staged edit to undo", await Refused("workbench_act", A(("verb", "undo"))));
    }

    [Fact]
    public async Task Commit_previews_by_default_writes_only_when_asked_and_says_why_it_was_refused()
    {
        await Ok("workbench_act", A(("verb", "select"), ("conflict", 1)));
        await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { -70f, 0f, 0f })));
        var dry = await Ok("workbench_act", A(("verb", "commit"), ("patch", "WbWire")));
        Assert.Equal((true, "WbWire"), Assert.Single(_scene.Commits));
        Assert.StartsWith("dry run, nothing written: would write 1", P(dry, "said").GetString());
        Assert.Single(P(dry, "state", "pending").EnumerateArray());
        Assert.Contains("needs patch", await Refused("workbench_act", A(("verb", "commit"))));
        Assert.Contains("already holds Taken.esp", await Refused("workbench_act", A(("verb", "commit"), ("patch", "Taken"), ("dry_run", false))));
        var real = await Ok("workbench_act", A(("verb", "commit"), ("patch", "WbWire"), ("dry_run", false)));
        Assert.Equal((false, "WbWire"), _scene.Commits[^1]);
        Assert.Contains("re-check of conflict 1 is running", P(real, "said").GetString());
        var st = P(real, "state");
        Assert.Empty(P(st, "pending").EnumerateArray());
        Assert.Equal("WbWire.esp", P(P(st, "written").EnumerateArray().Single(), "patch").GetString());
        Assert.Contains("written to WbWire.esp, not enabled", P(st, "selection", "view").GetString());
        Assert.True(P(st, "recheck", "running").GetBoolean());
        // The re-check finishes; a state read that waits for it reports its result.
        var waiting = Ok("workbench_state", A(("wait_seconds", 30)));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted, "the state read did not wait for the running re-check");
        _scene.RecheckGate.SetResult();
        var done = await waiting;
        Assert.Equal("resolved", P(done, "recheck", "row", "word").GetString());
        Assert.False(P(done, "recheck", "running").GetBoolean());
    }

    [Fact]
    public async Task Recheck_starts_the_re_check_of_the_open_conflict_against_a_named_patch()
    {
        await Ok("workbench_act", A(("verb", "select"), ("conflict", 1)));
        Assert.Contains("needs patch", await Refused("workbench_act", A(("verb", "recheck"))));
        var r = await Ok("workbench_act", A(("verb", "recheck"), ("patch", "WbEarlier")));
        Assert.Equal(new[] { "WbEarlier" }, _scene.Rechecks);
        Assert.StartsWith("re-checking conflict 1 against WbEarlier", P(r, "said").GetString());
        Assert.Contains("already running", await Refused("workbench_act", A(("verb", "recheck"), ("patch", "WbEarlier"))));
        _scene.RecheckGate.SetResult();
        var done = await Ok("workbench_state", A(("project", new[] { "recheck" }), ("wait_seconds", 30)));
        Assert.Equal("resolved", P(done, "recheck", "row", "word").GetString());
        Assert.Contains("is not a verb", await Refused("workbench_act", A(("verb", "fly"))));
    }

    [Fact]
    public async Task A_call_the_busy_window_does_not_take_up_is_withdrawn_with_one_sentence()
    {
        _surface.Loading = "013818:Skyrim.esm";
        var block = _ui.InvokeAsync(() => Thread.Sleep(TimeSpan.FromSeconds(4)));      // the thread rebuilding a scene
        var t = Refused("workbench_act", A(("verb", "stage"), ("ref", RefKey), ("delta", new[] { 5f, 0f, 0f })));
        Assert.Contains("rebuilding the scene for cell 013818:Skyrim.esm", await t);
        await block.Task;
        await Task.Delay(200);
        Assert.Empty(_scene.Session.Pending);                                           // withdrawn, never run late
    }

    // ---- Gate B (workbench manual mode): the launcher on the same two tools, and the forwarded houseCARL surface ----

    /// <summary>A free-mode instance on disk: ModOrganizer.ini, profile P with one enabled and one disabled mod folder.</summary>
    static string LauncherInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-wb-launch-" + Guid.NewGuid().ToString("N"));
        var prof = Path.Combine(root, "profiles", "P");
        Directory.CreateDirectory(prof);
        Directory.CreateDirectory(Path.Combine(root, "mods", "OnMod"));
        Directory.CreateDirectory(Path.Combine(root, "mods", "OffMod"));
        Directory.CreateDirectory(Path.Combine(root, "overwrite"));
        File.WriteAllText(Path.Combine(root, "ModOrganizer.ini"), "[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray(" + root.Replace("\\", "\\\\") + ")\r\nselected_profile=@ByteArray(P)\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+OnMod\r\n-OffMod\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "");
        return root;
    }

    [Fact]
    public async Task B10a_the_launcher_state_is_readable_with_no_scene_open()
    {
        _surface.SceneShown = null;                                                      // B11: no scene yet
        var s = await Ok("workbench_state", A(("project", new[] { "launcher" })));
        var l = P(s, "launcher");
        foreach (var part in new[] { "mode", "instance", "profiles", "mods", "probes", "output", "run" }) P(l, part);
        Assert.Equal("idle", P(l, "run", "state").GetString());
    }

    [Fact]
    public async Task B10b_select_probes_refuses_a_check_that_is_not_a_probe_naming_its_kind()
    {
        var why = await Refused("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "loadability.dangling-references" })));
        Assert.Contains("mcp-only", why);
    }

    [Fact]
    public async Task B10c_a_free_mode_profile_opens_and_ticking_a_disabled_mod_is_refused()
    {
        var inst = LauncherInstance();
        var opened = await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P")));
        var mods = P(opened, "launcher", "mods").EnumerateArray().ToList();
        Assert.Contains(mods, m => P(m, "name").GetString() == "OnMod" && P(m, "enabled").GetBoolean());
        Assert.Contains("disabled", await Refused("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OffMod" }))));
        var set = await Ok("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OnMod" })));
        Assert.Contains(P(set, "launcher", "mods").EnumerateArray(), m => P(m, "name").GetString() == "OnMod" && P(m, "selected").GetBoolean());
    }

    [Fact]
    public async Task B10d_run_with_no_profile_open_is_refused_saying_what_to_do()
    {
        Assert.Contains("open a profile", await Refused("workbench_act", A(("verb", "run"))));
    }

    [Fact]
    public async Task B13_the_endpoint_forwards_the_stated_houseCARL_tools_and_never_an_instance_setter()
    {
        var names = (await _client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        foreach (var n in new[] { "housecarl_records", "housecarl_apply", "housecarl_remove", "housecarl_check" })
            Assert.True(names.Contains(n), $"the workbench endpoint does not forward {n}; it lists {string.Join(", ", names.OrderBy(x => x))}");
        Assert.DoesNotContain("housecarl_set_mo2_instance", names);
        Assert.DoesNotContain("housecarl_set_tool_path", names);
    }
}
