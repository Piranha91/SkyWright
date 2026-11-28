using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda.Plugins;
using Workbench;
using ViewProbe;
using Xunit;

namespace WorkbenchTests;

/// <summary>The navmesh repair fixture (<c>navownprobe --write-navrepair-fixture</c>, the one tests/test_navmesh_repair_rules.py
/// reads) laid out as an MO2 instance with one profile, an empty game Data folder, the order as an evaluation candidate,
/// and an Output Folder outside the instance.</summary>
public sealed class NavRepairWorld : IDisposable
{
    internal static readonly string[] Plugins = { "Skyrim.esm", "Dawnguard.esm", "RpA.esp", "RpB.esp", "RpD.esp" };
    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string Game => Path.Combine(Root, "game");
    public string DataDir => Path.Combine(Game, "Data");
    public string Output => Path.Combine(Root, "out");
    public string Candidate => Path.Combine(Root, "candidate.json");
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, HarnessShared.CheckoutLayout.Marker))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no SkyWright.sln above the test's folder");
    }
    internal static string Probe => NavRepairLane.ProbeExe(TestBuild.Layout.Root);

    public NavRepairWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wb-navrepair-" + Guid.NewGuid().ToString("N"));
        var fixture = Path.Combine(Root, "fixture");
        if (!File.Exists(Probe)) throw new InvalidOperationException($"navownprobe is not in the build at {Probe}; build the solution first.");
        Directory.CreateDirectory(Root);
        var (exit, text) = Recheck.Run(Probe, new[] { "--write-navrepair-fixture", fixture }, Path.Combine(Root, "fixture-write.log"));
        if (exit != 0 || !Plugins.All(p => File.Exists(Path.Combine(fixture, p)))) throw new InvalidOperationException($"the fixture was not written (exit {exit}): {text}");
        var profile = Path.Combine(Instance, "profiles", "P");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(Path.Combine(Instance, "overwrite"));
        Directory.CreateDirectory(DataDir); Directory.CreateDirectory(Output);
        foreach (var p in Plugins)
        {
            var d = Path.Combine(Instance, "mods", Path.GetFileNameWithoutExtension(p));
            Directory.CreateDirectory(d);
            File.Copy(Path.Combine(fixture, p), Path.Combine(d, p));
        }
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Concat(Plugins.Select(p => "*" + p + "\n")));
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), string.Concat(Plugins.Select(p => p + "\n")));
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), string.Concat(Enumerable.Reverse(Plugins).Select(p => "+" + Path.GetFileNameWithoutExtension(p) + "\n")));
        File.WriteAllText(Candidate, new JsonObject
        {
            ["SchemaVersion"] = 1, ["Name"] = "nr", ["BaseProfile"] = "P",
            ["Plugins"] = new JsonArray(Plugins.Select(p => (JsonNode)p).ToArray()),
            ["Mods"] = new JsonArray(Enumerable.Reverse(Plugins).Select(p => (JsonNode)Path.GetFileNameWithoutExtension(p)).ToArray()),
            ["Changes"] = new JsonArray(),
        }.ToJsonString());
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}

/// <summary>The Workbench's navmesh repair lane through its state tools (C2479, review of Gate C part 1, finding 2):
/// <c>workbench_act repair_preview finding=1</c> then <c>repair_write</c> on the repair fixture's order, with no scene shown
/// (a navmesh row opens none), the preview and write being <see cref="RepairDesk"/>'s and <see cref="NavRepairLane"/>'s own
/// (navownprobe and the houseCARL server this checkout builds), the state read back with <c>workbench_state</c> part
/// <c>repair</c>. The Findings row is L5's broken link (EDGE-IDX L5 -> R5 3, whose unique candidate is R5 1); L5's cell is
/// renamed by RpD.esp, so the write must forward RpD's cell for the read-back to hold.</summary>
[Trait("tier", "integration")]
public sealed class NavRepairLaneTests : IClassFixture<NavRepairWorld>, IAsyncLifetime
{
    const string L5 = "000A05:Skyrim.esm", Cell00 = "000900:Skyrim.esm";
    readonly NavRepairWorld _w;
    public NavRepairLaneTests(NavRepairWorld w) => _w = w;

    /// <summary>The window's surface with no scene shown (a navmesh row opens none): one Findings row, L5's EDGE-IDX, and a
    /// repair desk whose inputs are the fixture's, as Shell's are the open bundle's.</summary>
    sealed class Surface : IWorkbenchSurface
    {
        internal RepairDesk Desk = null!;
        internal IReadOnlyList<RankedFinding> Rows = Array.Empty<RankedFinding>();
        public object? Bundle => null;
        public IReadOnlyList<RankedRow> Listed => Array.Empty<RankedRow>();
        public int Total => 0;
        public RankedRow? OpenRow => null;
        public string Banner => "";
        public string? Loading => null;
        public IWorkbenchScene? Scene => null;
        public string OpenConflict(RankedRow row) => "no rows in this test";
        public IReadOnlyList<RankedFinding> Findings => Rows;
        public Task<RepairPreview> RepairPreviewAsync(RankedFinding row) => Desk.PreviewAsync(row);
        public Task<RepairWritten> RepairWriteAsync(RankedFinding row, string patch) => Desk.WriteAsync(row, patch);
        public RepairPreview? LastRepairPreview => Desk.Preview;
        public RepairWritten? LastRepairWritten => Desk.Written;
    }

    Dispatcher _ui = null!;
    Surface _surface = null!;
    StateServer _server = null!;
    McpClient _client = null!;

    public async Task InitializeAsync()
    {
        Dispatcher? d = null; using var ready = new ManualResetEventSlim();
        var t = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = "fake UI thread" };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait();
        _ui = d!;
        var row = new FindingRow("geometry.navmesh-ownership", "EDGE-IDX", "Skyrim.esm", "RpA.esp", "L5 links R5 triangle 3, past its table",
                                 L5, null, "the names read did not reach this reference, so its cell is not known", "finding", "edge-idx|" + L5);
        _surface = new Surface
        {
            Rows = new[] { new RankedFinding(1, row, false, "", 0) },
            Desk = new RepairDesk(() => (_w.Candidate, new WriteTarget(_w.Instance, "P", _w.DataDir, _w.Output), NavRepairWorld.Probe, Path.Combine(_w.Root, "work")),
                                  t => Task.FromResult(PatchWriter.OpenInProcess(t, Path.Combine(_w.Root, "hcdata"), TestCorpus.Path, _ => { }))),
        };
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _server = await StateServer.StartAsync(port, new StateHub(_ui, _surface));
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_server.Url + "/"), TransportMode = HttpTransportMode.StreamableHttp }));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await _surface.Desk.DisposeWriterAsync();
        _ui.InvokeShutdown();
    }

    async Task<JsonElement> Ok(string tool, Dictionary<string, object?> args)
    {
        var r = await _client.CallToolAsync(tool, args, cancellationToken: new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(r.IsError == true, $"{tool} refused: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    static JsonElement P(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            JsonElement v = default;
            Assert.True(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v), $"the answer has no '{name}' where expected: {e.GetRawText()[..Math.Min(600, e.GetRawText().Length)]}");
            e = v;
        }
        return e;
    }

    static Dictionary<string, object?> A(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task C2479_repair_preview_then_repair_write_show_the_plan_write_under_the_output_folder_read_back_and_re_check()
    {
        var sw = Stopwatch.StartNew();
        // the preview: L5 re-pointed at R5 triangle 1, nothing written
        var pv = await Ok("workbench_act", A(("verb", "repair_preview"), ("finding", 1)));
        Assert.StartsWith("repair preview, nothing written: the repair of " + L5, P(pv, "said").GetString());
        var rp = P(await Ok("workbench_state", A(("project", new[] { "repair" }))), "repair");
        var plans = P(rp, "plans").EnumerateArray().Select(e => (P(e, "navmesh").GetString()!, P(e, "status").GetString()!)).ToList();
        Assert.Equal(new[] { (L5, "planned") }, plans);
        Assert.True(P(rp, "edits").GetInt32() >= 1, $"the preview shows {P(rp, "edits").GetInt32()} edits");
        Assert.Equal(JsonValueKind.Null, P(rp, "written").ValueKind);
        Assert.Empty(Directory.GetFiles(_w.Output, "*.esp", SearchOption.AllDirectories));

        // the write: under the Output Folder, the carried cell forwarded from its winner, read back whole, re-checked
        var wr = await Ok("workbench_act", A(("verb", "repair_write"), ("finding", 1), ("patch", "WbNavRepair")));
        Assert.StartsWith("repair written: written to ", P(wr, "said").GetString());
        var w = P(P(await Ok("workbench_state", A(("project", new[] { "repair" }))), "repair"), "written");
        var esp = Path.GetFullPath(Path.Combine(_w.Output, "houseCARL - WbNavRepair", "WbNavRepair.esp"));
        Assert.Equal(esp, Path.GetFullPath(P(w, "patch").GetString()!), ignoreCase: true);
        Assert.True(File.Exists(esp), $"no patch at {esp}");
        Assert.Equal(NavRepairWorld.Plugins.Length, Directory.GetDirectories(Path.Combine(_w.Instance, "mods")).Length);
        var checkedRecords = P(w, "readBackChecked").EnumerateArray().ToDictionary(e => P(e, "record").GetString()!, e => P(e, "holds").GetBoolean());
        Assert.True(checkedRecords.GetValueOrDefault(L5) && checkedRecords.GetValueOrDefault("Cell " + Cell00),
                    $"the read-back checked {string.Join(", ", checkedRecords.Select(kv => $"{kv.Key} {(kv.Value ? "holds" : "FAILS")}"))}; it must hold {L5} and Cell {Cell00}");
        Assert.True(P(w, "readBackHolds").GetBoolean(), $"the read-back: {P(w, "readBack").GetString()}");
        Assert.Equal(new[] { Cell00 + " from RpD.esp" }, P(w, "forwarded").EnumerateArray().Select(e => e.GetString()));
        var verdicts = P(w, "verdicts").EnumerateArray().Select(e => (P(e, "row").GetString()!, P(e, "kind").GetString()!, P(e, "verdict").GetString()!)).ToList();
        Assert.Contains((L5, "EDGE-IDX", "repaired"), verdicts);
        Assert.Empty(P(w, "created").EnumerateArray().Select(e => e.GetString()).Where(c => c!.StartsWith("EDGE") || c.StartsWith("NAV-GROUND")));
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(5), $"took {sw.Elapsed}");
    }
}
