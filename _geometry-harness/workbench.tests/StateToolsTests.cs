using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda.Plugins;
using HarnessShared;
using Workbench;
using ViewProbe;
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
        public ViewState? View { get; set; }
        public IReadOnlyList<ViewProbe.PickShape> Shapes { get; set; } = Array.Empty<ViewProbe.PickShape>();
        internal Dictionary<string, IReadOnlyList<FormKey>> ModRefs = new(StringComparer.OrdinalIgnoreCase);
        public Task<(IReadOnlyList<FormKey> Refs, string? Refusal)> RefsOfModAsync(string modFolder, string? plugin) =>
            Task.FromResult<(IReadOnlyList<FormKey>, string?)>(ModRefs.TryGetValue(modFolder ?? plugin ?? "", out var r) ? (r, null) : (Array.Empty<FormKey>(), $"{modFolder} provides no plugin of this order."));
        internal string ServerBuild = "srv-1";
        public Task<(RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild)> DecisionInputsAsync(RankedRow row) =>
            !Session.IsDrawn(FormKey.Factory(row.Row.RefKey))      // as the host: a reference the scene does not draw cannot be checked
            ? throw new InvalidOperationException($"{row.Row.RefKey} is not drawn in this scene, so its objects are not compared until its cell is open.")
            : Task.FromResult<(RowState, IReadOnlyList<ObjectState>, string)>((new RowState(row.Row.Physical?.ToString(), row.Row.Summary, ""),
                new[] { new ObjectState(row.Row.RefKey, "Subject.esp", new[] { new FieldValue("Placement.Position", "1,2,3") }, null, Array.Empty<FieldValue>(), null, null, null) },
                ServerBuild));
        public float[]? Camera { get; set; } = { 1f, 2f, 3f, 90f, 10f };
    }

    sealed class FakeSurface : IWorkbenchSurface
    {
        internal FakeScene? SceneShown;
        internal IReadOnlyList<RankedRow> Rows = Array.Empty<RankedRow>();
        internal readonly List<int> Opened = new();
        public object? Bundle => new { planId = "test-plan", arm = "baseline" };
        public string? WorkFolder { get; set; }
        public (string Instance, string Profile)? Binding { get; set; } = ("C:/Instance", "Default");
        public string? PlanId => "test-plan";
        public string? Arm => "baseline";
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
        // the workbench's own two, and otherwise only forwarded houseCARL tools
        Assert.Equal(new[] { "workbench_act", "workbench_state" }, tools.Select(t => t.Name).Where(n => !n.StartsWith("housecarl_")).OrderBy(n => n, StringComparer.Ordinal));
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

    // ---- manual mode: the launcher on the same two tools, and the forwarded houseCARL surface ----

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

    // ---- B13b: every forwarded write lands under the Output Folder or is refused ----

    static Dictionary<string, (long, DateTime)> Listing(params string[] roots) =>
        roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
             .ToDictionary(p => p, p => File.Exists(p) ? (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)) : (-1L, Directory.GetLastWriteTimeUtc(p)));

    static void Unchanged(Dictionary<string, (long, DateTime)> before, Dictionary<string, (long, DateTime)> after, string what)
    {
        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || b != a).ToList();
        Assert.True(changed.Count == 0, $"{what} changed: {string.Join("; ", changed.Take(5))}");
    }

    static List<Dictionary<string, object?>> MoveOps(float z) => new()
    {
        new() { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = $"512,-256,{z}" },
    };

    /// <summary>A free-mode profile on a synthetic world, with the Output Folder outside the instance and the game.</summary>
    async Task<(TiltedRefWorld W, string O, string Game)> OpenWorld()
    {
        Environment.SetEnvironmentVariable("WB_CORPUS", TestCorpus.Path);
        var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        var o = Path.Combine(w.Root, "out");
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", w.Instance), ("profile", "Default"), ("game", game)));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", o)));
        return (w, o, game);
    }

    [Fact]
    public async Task B13b_a_apply_with_no_out_path_lands_in_the_output_folder_and_the_instance_is_unchanged()
    {
        var (w, o, game) = await OpenWorld();
        using var _ = w;
        var before = Listing(w.Instance, game);
        var (err, text) = await Call("housecarl_apply", A(("ops", MoveOps(80f)), ("patch", "WbFwd")));
        Assert.False(err, "the forwarded housecarl_apply refused: " + text);
        Assert.True(File.Exists(Path.Combine(o, "houseCARL - WbFwd", "WbFwd.esp")), $"no patch at {o}\\houseCARL - WbFwd\\WbFwd.esp: {text}");
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
    }

    [Fact]
    public async Task B13b_b_an_out_path_outside_the_output_folder_is_refused()
    {
        var (w, o, _) = await OpenWorld();
        using var _w = w;
        var elsewhere = Path.Combine(w.Root, "elsewhere");
        var why = await Refused("housecarl_apply", A(("ops", MoveOps(81f)), ("patch", "WbElse"), ("out_path", elsewhere)));
        Assert.Contains("Output Folder", why);
        Assert.False(Directory.Exists(elsewhere), "the refused folder was created");
    }

    [Fact]
    public async Task B13b_c_an_in_place_write_is_refused()
    {
        var (w, _, game) = await OpenWorld();
        using var _w = w;
        var before = Listing(w.Instance, game);
        var why = await Refused("housecarl_apply", A(("ops", MoveOps(82f)), ("in_place", true)));
        Assert.Contains("in_place", why);
        Assert.Contains("Output Folder", why);                     // the workbench's rule refuses it, not the server's own consent gate
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
    }

    [Fact]
    public async Task B13b_d_into_a_houseCARL_folder_under_the_instances_mods_is_refused_when_the_output_is_elsewhere()
    {
        var (w, _, game) = await OpenWorld();
        using var _w = w;
        var owned = Path.Combine(w.Instance, "mods", "houseCARL - Old");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "meta.ini"), "[houseCARL]\r\ngenerated=true\r\n");
        File.WriteAllText(Path.Combine(owned, "Old.esp"), "old bytes");
        var before = Listing(w.Instance, game);
        foreach (var into in new[] { "houseCARL - Old", "Old", owned })
            Assert.Contains("Output Folder", await Refused("housecarl_apply", A(("ops", MoveOps(83f)), ("into", into))));
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
    }

    [Fact]
    public async Task B13b_e_a_write_tool_whose_lane_takes_no_out_path_is_refused_in_one_sentence()
    {
        var (w, _, game) = await OpenWorld();
        using var _w = w;
        var before = Listing(w.Instance, game);
        Assert.Contains("manual mode", await Refused("housecarl_create_plugin", A(("name", "WbNew"))));
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
    }

    [Fact]
    public async Task B13b_f_a_forwarded_read_answers_from_the_bound_instance()
    {
        var (w, _, _) = await OpenWorld();
        using var _w = w;
        // a second profile of the same instance that loads nothing: a server bound to it would not have the reference
        var other = Path.Combine(w.Instance, "profiles", "Other");
        Directory.CreateDirectory(other);
        foreach (var f in new[] { "modlist.txt", "plugins.txt", "loadorder.txt" }) File.WriteAllText(Path.Combine(other, f), "");
        var (err, text) = await Call("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("format", "json")));
        Assert.False(err, "the forwarded housecarl_records refused: " + text);
        Assert.Contains("HcWbRef", text);                        // the reference only the synthetic world has
    }

    /// <summary>The server binds an argument by its name with case and underscores ignored (toFile, inPlace, Into), so
    /// the forwarded rule judges the names the server will bind, not only their exact spelling.</summary>
    [Fact]
    public async Task B13b_i_an_argument_spelled_another_way_is_judged_as_the_parameter_it_binds()
    {
        var (w, o, game) = await OpenWorld();
        using var _w = w;
        var before = Listing(w.Instance, game);
        var bad = Path.Combine(w.Instance, "mods", "WorldMod", "x.jsonl");
        Assert.Contains("to_file", await Refused("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("toFile", bad))));
        Assert.Contains("to_file", await Refused("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("TO_FILE", bad))));
        // the same parameter given twice, under two spellings, is refused rather than one silently winning
        Assert.Contains("more than once", await Refused("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }),
            ("to_file", Path.Combine(o, "a.jsonl")), ("toFile", bad))));
        var owned = Path.Combine(w.Instance, "mods", "houseCARL - Old");
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "meta.ini"), "[houseCARL]\r\ngenerated=true\r\n");
        File.WriteAllText(Path.Combine(owned, "Old.esp"), "old bytes");
        var before2 = Listing(w.Instance, game);
        Assert.Contains("Output Folder", await Refused("housecarl_apply", A(("ops", MoveOps(84f)), ("Into", "houseCARL - Old"))));
        Unchanged(before2, Listing(w.Instance, game), "the instance or the game folder");
        Assert.False(File.Exists(bad), "a to_file spelled another way was written into a mod folder");
    }

    [Fact]
    public async Task B13b_j_in_place_spelled_another_way_is_refused_with_the_output_at_the_mods_folder()
    {
        Environment.SetEnvironmentVariable("WB_CORPUS", TestCorpus.Path);
        using var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", w.Instance), ("profile", "Default"), ("game", game)));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", Path.Combine(w.Instance, "mods"))));
        await Ok("workbench_act", A(("verb", "set_work_folder"), ("path", Path.Combine(w.Root, "work"))));
        var why = await Refused("housecarl_compact_plugin", A(("source", TiltedRefWorld.Master.FileName.String), ("inPlace", true)));
        Assert.Contains("in_place", why);
        Assert.Contains("Output Folder", why);
    }

    /// <summary>With the Output Folder an ancestor of the instance (a drive root, say), a to_file loose in the mods folder
    /// is still refused: a file there belongs to no mod.</summary>
    [Fact]
    public async Task B13b_k_a_to_file_loose_in_the_mods_folder_is_refused_when_the_output_folder_holds_the_instance()
    {
        Environment.SetEnvironmentVariable("WB_CORPUS", TestCorpus.Path);
        using var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", w.Instance), ("profile", "Default"), ("game", game)));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", w.Root)));
        await Ok("workbench_act", A(("verb", "set_work_folder"), ("path", Path.Combine(w.Root, "work"))));
        var loose = Path.Combine(w.Instance, "mods", "x.jsonl");
        Assert.Contains("to_file", await Refused("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("to_file", loose))));
        Assert.False(File.Exists(loose));
    }

    /// <summary>A forwarded read's to_file (records, check, asset_status) is a write: it lands under the Output Folder or
    /// the Work Folder, else it is refused by the output rule's verdict.</summary>
    [Fact]
    public async Task B13b_h_a_read_tools_to_file_outside_the_output_and_work_folders_is_refused()
    {
        var (w, o, game) = await OpenWorld();
        using var _w = w;
        var before = Listing(w.Instance, game);
        foreach (var bad in new[] { Path.Combine(w.Instance, "mods", "WorldMod", "x.jsonl"), Path.Combine(w.DataDir, "x.jsonl"),
                                    Path.Combine(w.Instance, "profiles", "Default", "x.jsonl"), Path.Combine(w.Root, "elsewhere", "x.jsonl") })
        {
            var why = await Refused("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("to_file", bad)));
            Assert.Contains("to_file", why);
        }
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
        var good = Path.Combine(o, "reads", "x.jsonl");
        var (err, text) = await Call("housecarl_records", A(("formids", new[] { TiltedRefWorld.Ref.ToString() }), ("to_file", good)));
        Assert.False(err, "a to_file under the Output Folder was refused: " + text);
    }

    [Fact]
    public async Task B13b_g_a_decompile_out_path_outside_the_output_folder_is_refused()
    {
        var (w, _, _) = await OpenWorld();
        using var _w = w;
        var elsewhere = Path.Combine(w.Root, "elsewhere-psc");
        Assert.Contains("Output Folder", await Refused("housecarl_decompile_script", A(("pex", Path.Combine(w.Root, "HcWbScript.pex")), ("out_path", elsewhere))));
        Assert.Contains("out_path", await Refused("housecarl_decompile_script", A(("pex", Path.Combine(w.Root, "HcWbScript.pex")))));
        Assert.False(Directory.Exists(elsewhere));
    }

    /// <summary>with the instance's mods folder as the Output Folder, a new plugin and a
    /// placed file copy land in a new houseCARL folder there, through the endpoint.</summary>
    [Fact]
    public async Task B13c_with_the_mods_folder_as_output_create_plugin_and_place_land_in_new_houseCARL_folders()
    {
        Environment.SetEnvironmentVariable("WB_CORPUS", TestCorpus.Path);
        using var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        var mods = Path.Combine(w.Instance, "mods");
        var mesh = Path.Combine(mods, "WorldMod", "meshes", "hcwb", "a.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!); File.WriteAllText(mesh, "mesh bytes");
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", w.Instance), ("profile", "Default"), ("game", game)));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", mods)));
        await Ok("workbench_act", A(("verb", "set_work_folder"), ("path", Path.Combine(w.Root, "work"))));
        var (e1, t1) = await Call("housecarl_create_plugin", A(("patch", "WbPlug")));
        Assert.False(e1, "the forwarded housecarl_create_plugin refused: " + t1);
        Assert.True(Directory.GetFiles(mods, "WbPlug*.esp", SearchOption.AllDirectories).Any(f => Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith("houseCARL - ")), t1);
        var (e2, t2) = await Call("housecarl_place", A(("assets", new[] { new Dictionary<string, object?> { ["path"] = "meshes/hcwb/a.nif" } }), ("patch", "WbPlaced")));
        Assert.False(e2, "the forwarded housecarl_place refused: " + t2);
        Assert.True(File.Exists(Path.Combine(mods, "houseCARL - WbPlaced", "meshes", "hcwb", "a.nif")), t2);
    }

    // ---- H5: houseCARL mode starts the server as the client does ----

    /// <summary>A copy of the server this checkout builds, so a config can sit beside it without touching the build.</summary>
    static string ServerCopy(string dir)
    {
        var src = Path.Combine(R3Bundle.Checkout(), "src", "housecarl-mcp", "bin", "Release", "net9.0");
        Assert.True(File.Exists(Path.Combine(src, "housecarl-mcp.exe")), "build src/housecarl-mcp in Release first");
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dir, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        return Path.Combine(dir, "housecarl-mcp.exe");
    }

    static void Mo2Ini(TiltedRefWorld w) => File.WriteAllText(Path.Combine(w.Instance, "ModOrganizer.ini"),
        "[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray(" + Path.GetDirectoryName(w.DataDir)!.Replace("\\", "\\\\") + ")\r\nselected_profile=@ByteArray(Default)\r\n");

    [Fact]
    public async Task H5a_houseCARL_mode_reports_the_instance_of_the_server_started_with_the_clients_environment()
    {
        using var a = new TiltedRefWorld(); using var b = new TiltedRefWorld();
        Mo2Ini(a); Mo2Ini(b);
        var root = Path.Combine(a.Root, "client");
        var exe = ServerCopy(Path.Combine(root, "server"));
        // beside the exe: instance B (what a workbench that ignores the client's environment would read)
        File.WriteAllText(Path.Combine(root, "server", "houseCARL.user.json"), JsonSerializer.Serialize(new { Mo2InstanceDir = b.Instance }));
        // the client's environment: HOUSECARL_DATA_DIR, whose config names instance A
        var data = Path.Combine(root, "plugin-data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "houseCARL.user.json"), JsonSerializer.Serialize(new { Mo2InstanceDir = a.Instance }));
        var launch = Path.Combine(root, "launch.json");
        File.WriteAllText(launch, JsonSerializer.Serialize(new { command = exe, args = new[] { $"--HouseCarl:CorpusPath={TestCorpus.Path}" }, env = new Dictionary<string, string> { ["HOUSECARL_DATA_DIR"] = data } }));
        Environment.SetEnvironmentVariable("WB_HOUSECARL_LAUNCH", launch);
        try
        {
            var opened = await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "housecarl")));
            Assert.Equal(Path.GetFullPath(a.Instance).TrimEnd('\\'), Path.GetFullPath(P(opened, "launcher", "instance").GetString()!).TrimEnd('\\'));
            Assert.Equal("housecarl", P(opened, "launcher", "mode").GetString());
        }
        finally { Environment.SetEnvironmentVariable("WB_HOUSECARL_LAUNCH", null); }
    }

    [Fact]
    public async Task H5b_a_server_that_manages_no_instance_is_refused_in_one_sentence()
    {
        using var a = new TiltedRefWorld();
        var root = Path.Combine(a.Root, "client");
        var exe = ServerCopy(Path.Combine(root, "server"));
        var data = Path.Combine(root, "plugin-data"); Directory.CreateDirectory(data);     // no saved config: no instance
        var launch = Path.Combine(root, "launch.json");
        File.WriteAllText(launch, JsonSerializer.Serialize(new { command = exe, args = new[] { $"--HouseCarl:CorpusPath={TestCorpus.Path}" }, env = new Dictionary<string, string> { ["HOUSECARL_DATA_DIR"] = data } }));
        Environment.SetEnvironmentVariable("WB_HOUSECARL_LAUNCH", launch);
        try
        {
            var why = await Refused("workbench_act", A(("verb", "open_profile"), ("mode", "housecarl")));
            Assert.Contains("manages no MO2 instance", why);                     // the server's own answer, not a missing feature
            Assert.Contains("free mode", why);
        }
        finally { Environment.SetEnvironmentVariable("WB_HOUSECARL_LAUNCH", null); }
    }

    // ---- the launcher's verbs and its run ----

    /// <summary>LauncherInstance with a second enabled mod and a separator (L5), and an Output Folder beside it.</summary>
    static string LauncherInstanceL5()
    {
        var root = LauncherInstance();
        Directory.CreateDirectory(Path.Combine(root, "mods", "TopMod"));
        File.WriteAllText(Path.Combine(root, "profiles", "P", "modlist.txt"), "+TopMod\r\n+Stuff_separator\r\n+OnMod\r\n-OffMod\r\n");
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        return root;
    }

    [Fact]
    public async Task M7a_the_mod_list_is_in_MO2_priority_order_and_a_separator_cannot_be_ticked()
    {
        var inst = LauncherInstanceL5();
        var opened = await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P")));
        var mods = P(opened, "launcher", "mods").EnumerateArray().ToList();
        Assert.Equal(new[] { "TopMod", "Stuff_separator", "OnMod", "OffMod" }, mods.Select(m => P(m, "name").GetString()));
        Assert.True(P(mods[0], "priority").GetInt32() > P(mods[2], "priority").GetInt32(), "the strongest folder has the highest priority");
        Assert.True(P(mods[1], "separator").GetBoolean());
        Assert.Contains("separator", await Refused("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "Stuff_separator" }))));
    }

    [Fact]
    public async Task M7b_set_output_answers_with_the_rules_verdict_and_set_work_folder_is_checked()
    {
        var inst = LauncherInstanceL5();
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P")));
        Assert.Contains("Data", await Refused("workbench_act", A(("verb", "set_output"), ("path", Path.Combine(inst, "Data")))));
        var o = inst + "-out";
        var set = await Ok("workbench_act", A(("verb", "set_output"), ("path", o)));
        Assert.Equal("allowed", P(set, "launcher", "outputVerdict").GetString());
        Assert.Equal(Path.Combine(o, "workbench-runs"), P(set, "launcher", "workFolder").GetString());
        Assert.Contains("instance", await Refused("workbench_act", A(("verb", "set_work_folder"), ("path", Path.Combine(inst, "work")))));
        var w = await Ok("workbench_act", A(("verb", "set_work_folder"), ("path", inst + "-work")));
        Assert.Equal(inst + "-work", P(w, "launcher", "workFolder").GetString());
    }

    [Fact]
    public async Task M7c_select_probes_all_takes_every_probe_check_and_each_other_kind_is_refused_naming_it()
    {
        var all = await Ok("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "all" })));
        var probes = P(all, "launcher", "probes").EnumerateArray().ToList();
        var kinds = new[] { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target", "patchprobe", "adviceprobe" };
        Assert.All(probes.Where(p => kinds.Contains(P(p, "kind").GetString())), p => Assert.True(P(p, "selected").GetBoolean(), P(p, "checkId").GetString()));
        Assert.Equal(probes.Count(p => kinds.Contains(P(p, "kind").GetString())), probes.Count(p => P(p, "selected").GetBoolean()));
        foreach (var kind in new[] { "mcp-only", "unsupported", "derived", "manual", "media", "live-import" })
        {
            var one = probes.First(p => P(p, "kind").GetString() == kind);
            Assert.False(P(one, "selectable").GetBoolean());
            Assert.Contains(kind, await Refused("workbench_act", A(("verb", "select_probes"), ("checks", new[] { P(one, "checkId").GetString() }))));
        }
    }

    [Fact]
    public async Task M7d_ticking_conditional_geometry_adds_the_switch_census_and_makes_the_run_deep_and_says_so()
    {
        var r = await Ok("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "state.conditional-geometry" })));
        var probes = P(r, "launcher", "probes").EnumerateArray().ToList();
        var census = probes.Single(p => P(p, "checkId").GetString() == "state.switch-census");
        Assert.True(P(census, "selected").GetBoolean(), "the switch census was not added");
        Assert.Contains("conditional-geometry", P(census, "addedFor").GetString());
        Assert.Equal("deep", P(r, "launcher", "depth").GetString());
        Assert.Contains("deep", P(r, "said").GetString());
    }

    [Fact]
    public async Task M7e_bos_and_author_advice_are_greyed_with_a_sentence_when_the_profile_cannot_feed_them()
    {
        var inst = LauncherInstanceL5();
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
        await Ok("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OnMod" })));
        var s = await Ok("workbench_state", A(("project", new[] { "launcher" })));
        var probes = P(s, "launcher", "probes").EnumerateArray().ToList();
        string? Why(string id) => P(probes.Single(p => P(p, "checkId").GetString() == id), "whyNot").GetString();
        Assert.Contains("_SWAP.ini", Why("geometry.bos-overlay"));
        Assert.Contains("meta.ini", Why("patch.author-advice"));
    }

    [Fact]
    public async Task M7f_with_the_output_at_the_mods_folder_run_is_refused_until_a_work_folder_is_chosen()
    {
        var inst = LauncherInstanceL5();
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
        await Ok("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OnMod" })));
        await Ok("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "geometry.object-overlap" })));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", Path.Combine(inst, "mods"))));
        Assert.Contains("Work Folder", await Refused("workbench_act", A(("verb", "run"))));
    }

    [Fact]
    public async Task M7g_run_starts_the_pipeline_on_the_selection_reaches_done_and_the_same_server_keeps_answering()
    {
        var inst = LauncherInstanceL5();
        var fake = Path.Combine(inst + "-fake", "fake_pipeline.py");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        // a stand-in for evalpipeline.py: records its arguments and the entry, and writes a bundle where the run asks
        File.WriteAllText(fake, string.Join("\n",
            "import json, os, sys",
            "a = sys.argv[1:]",
            "out = a[a.index('--out') + 1]",
            "ev = os.path.join(out, 'bundle', 'evidence'); os.makedirs(ev, exist_ok=True)",
            "open(os.path.join(ev, 'manifest.json'), 'w').write('{}')",
            "json.dump({'args': a, 'entry': json.load(open(a[a.index('--entry') + 1]))}, open(os.path.join(os.path.dirname(sys.argv[0]), 'called.json'), 'w'))",
            "print('stage bundle: done')"));
        Environment.SetEnvironmentVariable("WB_PIPELINE_CMD", fake);
        try
        {
            await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
            await Ok("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OnMod" })));
            await Ok("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "geometry.object-overlap" })));
            await Ok("workbench_act", A(("verb", "set_output"), ("path", inst + "-out")));
            var started = await Ok("workbench_act", A(("verb", "run")));
            Assert.Equal("running", P(started, "launcher", "run", "state").GetString());
            var done = await Ok("workbench_state", A(("project", new[] { "launcher", "bundle" }), ("wait_seconds", 60)));
            Assert.Equal("done", P(done, "launcher", "run", "state").GetString());
            var bundle = P(done, "launcher", "run", "bundle").GetString()!;
            Assert.True(File.Exists(Path.Combine(bundle, "manifest.json")), bundle);
            Assert.StartsWith(Path.Combine(inst + "-out", "workbench-runs"), bundle);
            using var called = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fake)!, "called.json")));
            var entry = P(called.RootElement, "entry");
            Assert.Equal(new[] { "OnMod" }, P(entry, "selection", "mods").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(inst, P(entry, "inPlace", "instance").GetString());
            Assert.Contains("geometry.object-overlap", P(entry, "evaluation", "select").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("test-plan", P(done, "bundle", "planId").GetString());     // the same server still answers
        }
        finally { Environment.SetEnvironmentVariable("WB_PIPELINE_CMD", null); }
    }

    // ---- manual mode: to_file, statuses, refusals ----

    [Fact]
    public async Task M7h_patch_probes_are_greyed_when_this_machine_names_no_search_roots_or_download_pool()
    {
        var saved = new[] { "HOUSECARL_HARNESS_CONFIG", "HOUSECARL_SEARCH_ROOTS", "HOUSECARL_DOWNLOAD_POOL" }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var k in saved.Keys) Environment.SetEnvironmentVariable(k, null);
        // the harness config is read once per process; read it afresh with these variables, and again after
        var reload = typeof(Workbench.PatchWriter).Assembly.GetReferencedAssemblies().Select(n => System.Reflection.Assembly.Load(n)).First(x => x.GetName().Name == "viewprobe").GetType("HarnessTarget")!.GetMethod("Reload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        reload.Invoke(null, null);
        try
        {
            var inst = LauncherInstanceL5();
            await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
            var s = await Ok("workbench_state", A(("project", new[] { "launcher" })));
            var p = P(s, "launcher", "probes").EnumerateArray().Single(x => P(x, "checkId").GetString() == "patch.discovery-installed");
            Assert.False(P(p, "selectable").GetBoolean(), "patch discovery was offered with no search roots configured");
            Assert.Contains("search roots", P(p, "whyNot").GetString());
        }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); reload.Invoke(null, null); }
    }

    [Fact]
    public async Task M7i_an_unmanaged_modlist_entry_is_listed_but_cannot_be_ticked()
    {
        var inst = LauncherInstanceL5();
        File.AppendAllText(Path.Combine(inst, "profiles", "P", "modlist.txt"), "*DLC: Dawnguard\r\n");
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
        Assert.Contains("unmanaged", await Refused("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "DLC: Dawnguard" }))));
    }

    [Fact]
    public async Task M7j_run_is_refused_in_one_sentence_while_a_scene_is_open()
    {
        var prop = typeof(Launcher).GetProperty("RunBlocker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.True(prop is not null, "the launcher has no way to know a scene is open (RunBlocker)");
        prop!.SetValue(_hub.Launcher, (Func<string?>)(() => "a scene is open in this window."));
        var inst = LauncherInstanceL5();
        await Ok("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst)));
        await Ok("workbench_act", A(("verb", "select_mods"), ("mods", new[] { "OnMod" })));
        await Ok("workbench_act", A(("verb", "select_probes"), ("checks", new[] { "geometry.object-overlap" })));
        await Ok("workbench_act", A(("verb", "set_output"), ("path", inst + "-out")));
        Assert.Contains("scene", await Refused("workbench_act", A(("verb", "run"))));
    }

    [Fact]
    public async Task M7k_an_instance_whose_mods_live_elsewhere_by_base_directory_is_refused_naming_it()
    {
        var inst = LauncherInstanceL5();
        var bd = inst + "-base";
        foreach (var d in new[] { "mods/OnMod", "profiles/P", "overwrite" }) Directory.CreateDirectory(Path.Combine(bd, d));
        File.Copy(Path.Combine(inst, "profiles", "P", "modlist.txt"), Path.Combine(bd, "profiles", "P", "modlist.txt"));
        File.AppendAllText(Path.Combine(inst, "ModOrganizer.ini"), "[Settings]" + Environment.NewLine + "base_directory=" + bd.Replace(Path.DirectorySeparatorChar, '/') + Environment.NewLine);
        Assert.Contains("base_directory", await Refused("workbench_act", A(("verb", "open_profile"), ("mode", "free"), ("instance", inst), ("profile", "P"), ("game", inst))));
    }
    // ---- review speed: guarded arguments on the wire, and the new state-tool values ----

    // R31d: the reachable case through the endpoint
    [Fact]
    public async Task R31d_a_number_for_into_is_refused_through_the_endpoint_and_nothing_is_written()
    {
        var (w, o, game) = await OpenWorld();
        using var _w = w;
        var before = Listing(w.Instance, game);
        var why = await Refused("housecarl_apply", A(("ops", MoveOps(85f)), ("into", 123)));
        Assert.Contains("into", why);
        Assert.Contains("text", why);
        Unchanged(before, Listing(w.Instance, game), "the instance or the game folder");
        Assert.False(Directory.Exists(o) && Directory.EnumerateFiles(o, "*.esp", SearchOption.AllDirectories).Any(), "a patch was written");
    }

    // R31e (pin): the server reads a null or empty into as absent, as the workbench does
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task R31e_a_null_or_empty_into_is_absent_on_both_sides(string? into)
    {
        var (w, o, _) = await OpenWorld();
        using var _w = w;
        var name = into is null ? "WbNullInto" : "WbEmptyInto";
        var (err, text) = await Call("housecarl_apply", A(("ops", MoveOps(86f)), ("patch", name), ("into", into)));
        Assert.False(err, "refused: " + text);
        Assert.True(File.Exists(Path.Combine(o, "houseCARL - " + name, name + ".esp")), $"no new patch under the Output Folder: {text}");
    }

    // I3h: the view on the wire
    [Fact]
    public async Task I3h_view_hides_and_restores_through_the_state_tools_and_the_state_says_so()
    {
        _scene.View = new ViewState(_scene.Session, () => Array.Empty<ViewProbe.RefOcclusion>());
        await Ok("workbench_act", A(("verb", "view"), ("op", "hide"), ("ref", RefKey)));
        var st = await Ok("workbench_state", A(("project", new[] { "view" })));
        Assert.Contains(RefKey, P(st, "view", "hidden").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Restore visibility", P(st, "view", "label").GetString());
        await Ok("workbench_act", A(("verb", "view"), ("op", "restore")));
        st = await Ok("workbench_state", A(("project", new[] { "view" })));
        Assert.Empty(P(st, "view", "hidden").EnumerateArray());
        Assert.Contains("is not a view operation", await Refused("workbench_act", A(("verb", "view"), ("op", "blur"))));
    }

    // I1g: originals on the wire, and a stage while they show puts the edits back
    [Fact]
    public async Task I1g_show_original_through_the_state_tools_and_a_stage_while_showing_clears_it()
    {
        _scene.View = new ViewState(_scene.Session, () => Array.Empty<ViewProbe.RefOcclusion>());
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 50f, 0f, 0f })));
        await Ok("workbench_act", A(("verb", "view"), ("op", "show_original")));
        Assert.True(P(await Ok("workbench_state", A(("project", new[] { "view" }))), "view", "showingOriginal").GetBoolean());
        await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 5f, 0f, 0f })));
        Assert.False(P(await Ok("workbench_state", A(("project", new[] { "view" }))), "view", "showingOriginal").GetBoolean(), "a stage left originals showing");
        await Ok("workbench_act", A(("verb", "view"), ("op", "show_original")));
        await Ok("workbench_act", A(("verb", "undo")));
        Assert.False(P(await Ok("workbench_state", A(("project", new[] { "view" }))), "view", "showingOriginal").GetBoolean(), "an undo left originals showing");
    }

    // E4h: the pending list in words, and revert on the wire
    [Fact]
    public async Task E4h_pending_rows_carry_a_sentence_and_revert_drops_one_reference()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var r = await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 150f, 0f, 0f })));
        Assert.StartsWith("Moved ", P(P(r, "state", "pending").EnumerateArray().First(), "sentence").GetString());
        var v = await Ok("workbench_act", A(("verb", "revert"), ("ref", RefKey)));
        Assert.Empty(P(v, "state", "pending").EnumerateArray());
        Assert.Contains("nothing staged", await Refused("workbench_act", A(("verb", "revert"), ("ref", RefKey))));
    }

    // M6k: snapping on the wire rounds only the moved axis
    [Fact]
    public async Task M6k_stage_with_snap_rounds_the_moved_axis_only()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var before = _scene.Session.Current(FormKey.Factory(RefKey));
        var r = await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 101f, 0f, 0f }), ("snap", 128f)));
        var drawn = Pos(P(r, "state", "selection", "drawn"));
        Assert.Equal(MathF.Round((before.Position.X + 101f) / 128f) * 128f, drawn[0]);
        Assert.Equal(before.Position.Y, drawn[1]);
        Assert.Equal(before.Position.Z, drawn[2]);
    }

    // M6l: drop on the wire, refused in one sentence when nothing is below
    [Fact]
    public async Task M6l_drop_with_nothing_below_is_refused_saying_it_lowers_only()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var why = await Refused("workbench_act", A(("verb", "drop"), ("ref", RefKey)));
        Assert.Contains("lowers only", why);
    }

    // D2k: decisions on the wire
    [Fact]
    public async Task D2k_decisions_are_a_part_of_the_state_and_decide_takes_only_the_three_verdicts()
    {
        var st = await Ok("workbench_state", A(("project", new[] { "decisions" })));
        Assert.Equal(JsonValueKind.Array, P(st, "decisions", "rows").ValueKind);
        var why = await Refused("workbench_act", A(("verb", "decide"), ("conflict", 1), ("verdict", "maybe"), ("reason", "x")));
        Assert.Contains("real problem", why);
        Assert.Contains("intentional", why);
        Assert.Contains("unsure", why);
    }

    string NewWorkFolder() { var w = Path.Combine(Path.GetTempPath(), "hc-wb-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(w); return w; }

    // B1b (wire): see-through by mod folder tints exactly the references that mod's plugins win
    [Fact]
    public async Task B1_see_through_by_mod_tints_exactly_that_mods_references_and_refuses_a_mod_with_none()
    {
        var ro = _scene.Session.RefOf(FormKey.Factory(RefKey));
        _scene.View = new ViewState(_scene.Session, () => new[] { ro });
        _scene.ModRefs["ModA"] = new[] { FormKey.Factory(RefKey) };
        await Ok("workbench_act", A(("verb", "view"), ("op", "see_through"), ("mod", "ModA")));
        var st = await Ok("workbench_state", A(("project", new[] { "view" })));
        Assert.Equal(new[] { RefKey }, P(st, "view", "seeThrough").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("ModZ", await Refused("workbench_act", A(("verb", "view"), ("op", "see_through"), ("mod", "ModZ"))));
    }

    // B3: decide stores the verdict and reason, needs a reason, and opens the next undecided row
    [Fact]
    public async Task B3_decide_stores_the_verdict_and_reason_and_opens_the_next_undecided_row()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        Assert.Contains("reason", await Refused("workbench_act", A(("verb", "decide"), ("conflict", 1), ("verdict", "intentional"))));
        var r = await Ok("workbench_act", A(("verb", "decide"), ("conflict", 1), ("verdict", "intentional"), ("reason", "placed on purpose")));
        Assert.Contains("conflict 2", P(r, "said").GetString());
        Assert.Contains(2, _surface.Opened);
        var file = Path.Combine(work, "review-decisions.json");
        Assert.True(File.Exists(file), "no decisions file in the Work Folder");
        Assert.Contains("placed on purpose", File.ReadAllText(file));
        var st = await Ok("workbench_state", A(("project", new[] { "decisions" })));
        var row = P(st, "decisions", "rows").EnumerateArray().Single(x => P(x, "rank").GetInt32() == 1);
        Assert.Equal("intentional", P(row, "verdict").GetString());
        Assert.Equal("placed on purpose", P(row, "reason").GetString());
        Assert.Equal("applies", P(row, "standing").GetString());
    }

    // B3: a stored decision whose row is not in this run is kept and listed
    [Fact]
    public async Task B3_a_decision_for_a_row_not_in_this_run_is_listed_and_kept()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        new DecisionStore(Path.Combine(work, "review-decisions.json")).Put(new Decision("C:/Instance", "Default", "geometry.object-overlap", "overlap|000a77:hcwbtest.esp|000a78:hcwbtest.esp",
            "real problem", "gone now", DateTimeOffset.UtcNow, new RowState("Holds", "s", "probe-1"), Array.Empty<ObjectState>(), "srv-1"));
        var st = await Ok("workbench_state", A(("project", new[] { "decisions" })));
        var gone = P(st, "decisions", "notInThisRun").EnumerateArray().ToList();
        Assert.Contains(gone, g => P(g, "key").GetString() == "overlap|000a77:hcwbtest.esp|000a78:hcwbtest.esp");
        Assert.Contains("gone now", File.ReadAllText(Path.Combine(work, "review-decisions.json")));
    }

    // B3: a decision is remembered by a new process on the same Work Folder
    [Fact]
    public async Task B3_a_decision_is_remembered_by_a_new_process_on_the_same_work_folder()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        await Ok("workbench_act", A(("verb", "decide"), ("conflict", 1), ("verdict", "real problem"), ("reason", "the box floats")));
        var surface2 = new FakeSurface { SceneShown = _scene, Rows = _surface.Rows, WorkFolder = work };
        var hub2 = new StateHub(_ui, surface2) { Wait = TimeSpan.FromSeconds(2) };
        await using var server2 = await StateServer.StartAsync(FreePort(), hub2);
        await using var client2 = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(server2.Url + "/"), TransportMode = HttpTransportMode.StreamableHttp }));
        var res = await client2.CallToolAsync("workbench_state", A(("project", new[] { "decisions" })));
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(res.IsError == true, text);
        using var d = JsonDocument.Parse(text);
        var row = P(d.RootElement, "decisions", "rows").EnumerateArray().Single(x => P(x, "rank").GetInt32() == 1);
        Assert.Equal("real problem", P(row, "verdict").GetString());
        Assert.Equal("applies", P(row, "standing").GetString());
    }

    // B6: drop succeeds on the wire and names what it landed on; stage takes an axis lock
    [Fact]
    public async Task B6_drop_stages_one_lowering_onto_the_terrain_and_stage_honours_an_axis_lock()
    {
        var key = FormKey.Factory(RefKey);
        var ro = _scene.Session.RefOf(key);
        _scene.Shapes = ro.Shapes.Append(Boxes.Terrain(0, 1000, 2000)).ToList();
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var before = _scene.Session.Current(key);
        var r = await Ok("workbench_act", A(("verb", "drop"), ("ref", RefKey)));
        Assert.Contains("terrain", P(r, "said").GetString());
        var pending = P(r, "state", "pending").EnumerateArray().ToList();
        Assert.Single(pending);
        var moved = P(pending[0], "moved").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.True(moved[2] < 0 && moved[0] == 0 && moved[1] == 0, $"drop moved by [{string.Join(", ", moved)}]");
        Assert.True(_scene.Session.Current(key).Position.Z < before.Position.Z);
        await Ok("workbench_act", A(("verb", "undo")));
        var locked = await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 30f, -40f, 12f }), ("axis", "x")));
        Assert.Equal(new[] { 30f, 0f, 0f }, P(P(locked, "state", "pending").EnumerateArray().Single(), "moved").EnumerateArray().Select(e => e.GetSingle()));
    }

    // ---- Gate C review, round 2

    static readonly RefPlacementEdit.Pose Z0 = new(new HousecarlCore.Vec3(0, 0, 0), new HousecarlCore.Vec3(0, 0, 0), 1f);

    // C1d: another cell's staged and written edits survive a save from this scene
    [Fact]
    public async Task C1d_a_save_keeps_the_edits_of_references_in_another_cell()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        var (path, _) = SessionFile.PathFor(work, "test-plan", "baseline");
        SessionFile.Write(path!, new SessionData("test-plan", "baseline", null, 2, null,
            new[] { new SavedEdit("000A01:HcWbTest.esp", Z0, Z0 with { Position = new HousecarlCore.Vec3(5, 0, 0) }) },
            new[] { new SavedWritten("000A02:HcWbTest.esp", "WbOld.esp", Path.Combine(work, "houseCARL - WbOld")) }));
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var saved = SessionFile.Read(path!)!;
        Assert.Contains(saved.Staged, e => e.Ref == "000A01:HcWbTest.esp");
        Assert.Contains(saved.Written, e => e.Ref == "000A02:HcWbTest.esp");
        Assert.Equal(RefKey, saved.Selected);
    }

    static Decision Stored(string key, string verdict, string summary) => new("C:/Instance", "Default", "geometry.object-overlap", key, verdict, "why",
        DateTimeOffset.UtcNow, new RowState("Holds", summary, ""), Array.Empty<ObjectState>(), "srv-1");

    // C2: the decisions part answers per row; a row in another cell is compared on its row half
    [Fact]
    public async Task C2_the_decisions_part_answers_per_row_and_says_what_it_could_not_compare()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        var store = new DecisionStore(Path.Combine(work, DecisionStore.FileName));
        store.Put(Stored("a-first", "intentional", ""));
        store.Put(Stored("b-second", "real problem", ""));
        var st = await Ok("workbench_state", A(("project", new[] { "decisions" })));
        var rows = P(st, "decisions", "rows").EnumerateArray().ToList();
        var second = rows.Single(x => P(x, "rank").GetInt32() == 2);
        Assert.Contains("not compared until its cell is open", P(second, "sentence").GetString());
        var first = rows.Single(x => P(x, "rank").GetInt32() == 1);
        Assert.Equal("applies", P(first, "standing").GetString());
        Assert.DoesNotContain("not compared", P(first, "sentence").GetString());
    }

    [Fact]
    public async Task C2b_a_changed_row_summary_flags_a_decision_whose_cell_is_not_open()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        new DecisionStore(Path.Combine(work, DecisionStore.FileName)).Put(Stored("b-second", "intentional", "an older summary"));
        var st = await Ok("workbench_state", A(("project", new[] { "decisions" })));
        var second = P(st, "decisions", "rows").EnumerateArray().Single(x => P(x, "rank").GetInt32() == 2);
        Assert.Equal("reconsider", P(second, "standing").GetString());
    }

    // C3: the conflicts part carries the decision, and decide offers a reconsider row next
    [Fact]
    public async Task C3_the_conflicts_part_carries_each_rows_decision_and_decide_opens_a_reconsider_row_next()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        new DecisionStore(Path.Combine(work, DecisionStore.FileName)).Put(Stored("b-second", "intentional", "an older summary"));
        var st = await Ok("workbench_state", A(("project", new[] { "conflicts" })));
        var two = P(st, "conflicts", "rows").EnumerateArray().Single(x => P(x, "rank").GetInt32() == 2);
        Assert.Equal("intentional", P(two, "verdict").GetString());
        Assert.Equal("reconsider", P(two, "standing").GetString());
        var r = await Ok("workbench_act", A(("verb", "decide"), ("conflict", 1), ("verdict", "unsure"), ("reason", "need to look in game")));
        Assert.Contains("conflict 2", P(r, "said").GetString());
        Assert.Contains(2, _surface.Opened);
    }

    // C7: an angle snap applies to a tilted reference's turn
    [Fact]
    public async Task C7_angle_snap_snaps_the_turn_of_a_tilted_reference()
    {
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        var before = _scene.Session.Current(FormKey.Factory(RefKey));
        await Ok("workbench_act", A(("verb", "stage"), ("turn_degrees", 22f), ("angle_snap", 15f)));
        var want = HousecarlCore.SpatialOverlap.RefrRotation(RefPlacementEdit.Turned(before.Rotation, System.Numerics.Vector3.UnitZ, float.DegreesToRadians(30)));
        var got = HousecarlCore.SpatialOverlap.RefrRotation(_scene.Session.Current(FormKey.Factory(RefKey)).Rotation);
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.Equal(want[i, j], got[i, j], 3);
    }

    // (d3): Findings rows are not decided yet
    [Fact]
    public async Task Cd3_decide_refuses_a_findings_row()
    {
        _surface.WorkFolder = NewWorkFolder();
        Assert.Contains("Findings rows are not decided", await Refused("workbench_act", A(("verb", "decide"), ("finding", 1), ("verdict", "unsure"), ("reason", "x"))));
    }

    // B7: the running workbench keeps the session file up to date
    [Fact]
    public async Task B7_select_stage_and_revert_are_saved_to_the_session_file()
    {
        var work = NewWorkFolder();
        _surface.WorkFolder = work;
        var (path, why) = SessionFile.PathFor(work, "test-plan", "baseline");
        Assert.True(path is not null, "no session path: " + why);
        await Ok("workbench_act", A(("verb", "select"), ("ref", RefKey)));
        await Ok("workbench_act", A(("verb", "stage"), ("delta", new[] { 40f, 0f, 0f })));
        var saved = SessionFile.Read(path!);
        Assert.True(saved is not null, "the session file was not written");
        Assert.Equal(RefKey, saved!.Selected);
        Assert.Single(saved.Staged);
        await Ok("workbench_act", A(("verb", "revert"), ("ref", RefKey)));
        Assert.Empty(SessionFile.Read(path!)!.Staged);
    }
}
