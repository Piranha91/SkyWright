using Xunit;
using L = HarnessShared.CheckoutLayout;

namespace WorkbenchTests;

/// <summary>The build layout: bin\&lt;Configuration&gt;\&lt;.NET version&gt;\ with SkyWright.exe, Probes\, Tools\, Data\ and the
/// marker skywright-layout.json that Directory.Build.props writes. A program finds its build from its own folder;
/// an override wins; a folder outside a build is refused; the marker, not this code, names the paths. Expected paths
/// are spelled here by hand. Env-var tests share the "Shell" collection, which runs alone.</summary>
[Collection("Shell")]
public sealed class BuildLayoutTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "wb-build-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Every override variable as it was before the test, cleared for the test (another class may leave one set).</summary>
    readonly Dictionary<string, string?> _saved = Enum.GetValues<L.Piece>().Select(L.OverrideVariable).Concat(L.ProgramOverrides)
        .ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public BuildLayoutTests()
    {
        Directory.CreateDirectory(_tmp);
        foreach (var v in _saved.Keys) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        foreach (var (v, was) in _saved) Environment.SetEnvironmentVariable(v, was);
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    static string N(string p) => Path.GetFullPath(p);

    const string Marker = """
        { "kind": "skywright-build", "configuration": "Debug", "framework": "net9.0-windows10.0.19041.0", "app": "SkyWright.exe",
          "program": "Probes/{name}/{name}.exe", "tool": "Tools/{name}/{name}.exe", "tools": ["runcontrol"],
          "corpus": "Data/corpus.json",
          "registry": "Data/check-registry.json", "severity": "Data/workbench-severity.json", "scenePolicy": "Data/scene-policy.json" }
        """;

    /// <summary>A checkout marker at the temp root (a build always sits in one) and a build under bin\Debug\net9.0-windows…\.</summary>
    string FakeBuild(string marker = Marker)
    {
        File.WriteAllText(Path.Combine(_tmp, L.Marker), "");
        var root = Path.Combine(_tmp, "bin", "Debug", "net9.0-windows10.0.19041.0");
        foreach (var d in new[] { "Probes/overlapprobe", "Tools/runcontrol", "Tests/workbench.tests", "Data" })
            Directory.CreateDirectory(Path.Combine(root, d));
        File.WriteAllText(Path.Combine(root, L.LayoutMarker), marker);
        return root;
    }

    [Fact]
    public void BuildFoundFromTheAppAProbeAToolAndTheTests()
    {
        var root = FakeBuild();
        foreach (var from in new[] { root, Path.Combine(root, "Probes", "overlapprobe"), Path.Combine(root, "Tools", "runcontrol"), Path.Combine(root, "Tests", "workbench.tests") })
        {
            var layout = L.Locate(from, out var why);
            Assert.True(layout is not null, $"the build was not found from {from}: {why}");
            Assert.Equal(N(root), N(layout!.Root));
        }
        var l = L.Locate(Path.Combine(root, "Tools", "runcontrol"), out _)!;
        Assert.Equal(N(Path.Combine(root, "Data", "corpus.json")), N(l.PathOf(L.Piece.Corpus)));
        Assert.Equal(N(Path.Combine(root, "Data", "check-registry.json")), N(l.PathOf(L.Piece.Registry)));
        Assert.Equal(N(Path.Combine(root, "Data", "workbench-severity.json")), N(l.PathOf(L.Piece.Severity)));
        Assert.Equal(N(Path.Combine(root, "Data", "scene-policy.json")), N(l.PathOf(L.Piece.ScenePolicy)));
        Assert.Equal(N(Path.Combine(root, "Tools", "runcontrol", "runcontrol.exe")), N(l.PathOf(L.Piece.Runcontrol)));
        Assert.Equal(N(Path.Combine(root, "Probes", "overlapprobe", "overlapprobe.exe")), N(l.Program("overlapprobe")));
        Assert.Equal(N(root), N(l.Harness));
        Assert.Equal(N(_tmp), N(l.Checkout!));
        Assert.StartsWith("SkyWright Debug build ", l.Describe());
    }

    [Fact]
    public void TheMarkerNamesThePaths()
    {
        // a build whose marker puts probes elsewhere: the programs follow the marker, not a path spelled in code
        var root = FakeBuild(Marker.Replace("Probes/{name}/{name}.exe", "Checks/{name}/bin/{name}.exe").Replace("Data/check-registry.json", "Rules/registry.json"));
        var l = L.Locate(root, out var why) ?? throw new Xunit.Sdk.XunitException("no build: " + why);
        Assert.Equal(N(Path.Combine(root, "Checks", "overlapprobe", "bin", "overlapprobe.exe")), N(L.ProgramExe(root, "overlapprobe")));
        Assert.Equal(N(Path.Combine(root, "Checks", "overlapprobe", "bin")), N(L.ProgramFolder(root, "overlapprobe")));
        Assert.Equal(N(Path.Combine(root, "Rules", "registry.json")), N(l.PathOf(L.Piece.Registry)));
        // a tool keeps the tool form
        Assert.Equal(N(Path.Combine(root, "Tools", "runcontrol", "runcontrol.exe")), N(L.ProgramExe(root, "runcontrol")));
        Assert.Equal(N(root), N(L.HarnessOf(Path.Combine(root, "Tools", "runcontrol", "runcontrol.exe"))));
    }

    [Fact]
    public void OverrideWins()
    {
        var root = FakeBuild();
        var layout = L.Locate(root, out var why) ?? throw new Xunit.Sdk.XunitException("no build: " + why);
        var over = Path.Combine(_tmp, "elsewhere", "check-registry.json");
        Environment.SetEnvironmentVariable("WB_REGISTRY", over);
        Environment.SetEnvironmentVariable("WB_CONFLICTDIFF", Path.Combine(_tmp, "elsewhere", "conflictdiff.exe"));
        Assert.Equal((over, "WB_REGISTRY"), L.Resolve(L.Piece.Registry, layout));
        Assert.Equal((Path.Combine(root, "Data", "workbench-severity.json"), "build"), L.Resolve(L.Piece.Severity, layout));
        var said = layout.Describe();
        Assert.Contains("WB_REGISTRY=" + over.Replace('\\', '/'), said);
        Assert.Contains("WB_CONFLICTDIFF=", said);
        Environment.SetEnvironmentVariable("WB_REGISTRY", null);
        Environment.SetEnvironmentVariable("WB_CONFLICTDIFF", null);
        Assert.Equal((Path.Combine(root, "Data", "check-registry.json"), "build"), L.Resolve(L.Piece.Registry, layout));
        Assert.DoesNotContain("overridden", layout.Describe());
    }

    [Fact]
    public void OutsideABuildRefused()
    {
        var root = FakeBuild();
        // a project folder in the checkout (no marker in it or two up), with SkyWright.sln above to tempt a walk
        var src = Path.Combine(_tmp, "_geometry-harness", "runcontrol", "obj");
        Directory.CreateDirectory(src);
        Assert.Null(L.Locate(src, out var why));
        Assert.Contains("is not in a SkyWright build", why);
        // an unreadable marker: another kind, then a field of the wrong type
        File.WriteAllText(Path.Combine(root, L.LayoutMarker), "{ \"kind\": \"something-else\" }");
        Assert.Null(L.Locate(Path.Combine(root, "Probes", "overlapprobe"), out var bad));
        Assert.Contains("unreadable skywright-layout.json", bad);
        File.WriteAllText(Path.Combine(root, L.LayoutMarker), Marker.Replace("\"Probes/{name}/{name}.exe\"", "7"));
        Assert.Null(L.Locate(root, out var typed));
        Assert.Contains("unreadable skywright-layout.json", typed);
    }

    [Fact]
    public void TheDefaultsMatchTheMarkerTheBuildWrites()
    {
        // these tests run from the build (Tests\workbench.tests\), so its own marker is the one Directory.Build.props wrote
        var real = TestBuild.Layout.Spec;
        var expected = L.Spec.Expected;
        Assert.Equal(expected.Program, real.Program);
        Assert.Equal(expected.Tool, real.Tool);
        Assert.Equal(expected.Tools, real.Tools);
        Assert.Equal(expected.Corpus, real.Corpus);
        Assert.Equal(expected.Registry, real.Registry);
        Assert.Equal(expected.Severity, real.Severity);
        Assert.Equal(expected.ScenePolicy, real.ScenePolicy);
        // and the build holds what its marker names
        foreach (var p in new[] { L.Piece.Runcontrol, L.Piece.Registry, L.Piece.Severity })
            Assert.True(File.Exists(TestBuild.Layout.PathOf(p)), $"the build has no {p} at {TestBuild.Layout.PathOf(p)}");
        Assert.True(File.Exists(TestBuild.Program("overlapprobe")), "the build has no overlapprobe");
    }

    /// <summary>The rulebook the app's houseCARL reads is Data\corpus.json, the pin's generated rulebook byte for byte, so
    /// what keys on its content (runcontrol's tools digest, keyed corpus.json and hashed) reads the same rulebook as when
    /// it lay in Tools\housecarl-mcp\. Rejects a stale or partial copy.</summary>
    [Fact]
    public void The_rulebook_is_in_Data_and_is_the_pins_generated_one()
    {
        var corpus = TestBuild.Layout.PathOf(L.Piece.Corpus);
        Assert.Equal(N(Path.Combine(TestBuild.Layout.Root, "Data", "corpus.json")), N(corpus));
        var generated = Path.Combine(TestBuild.Layout.Checkout!, "external", "houseCARL", "generated", "corpus.json");
        Assert.True(File.Exists(corpus) && File.Exists(generated), $"no rulebook at {corpus} or {generated}");
        static string Sha(string f) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        Assert.Equal(Sha(generated), Sha(corpus));
    }

    /// <summary>houseCARL's server code sits beside the app as a library: its assembly is there, and nothing that would
    /// make it a program (its exe, runtimeconfig, deps or appsettings) is. Rejects a plain project reference to the
    /// server, which copies housecarl-mcp.exe into the app's own folder.</summary>
    [Fact]
    public void The_app_folder_holds_houseCARLs_server_code_as_a_library_and_not_its_program()
    {
        var root = TestBuild.Layout.Root;
        Assert.True(File.Exists(Path.Combine(root, "housecarl-mcp.dll")), $"the app's folder {root} has no housecarl-mcp.dll");
        foreach (var f in new[] { "housecarl-mcp.exe", "housecarl-mcp.runtimeconfig.json", "housecarl-mcp.deps.json", "appsettings.json" })
            Assert.False(File.Exists(Path.Combine(root, f)), $"the app's folder {root} holds {f}, which belongs to houseCARL's program");
    }

    /// <summary>No folder of the build holds houseCARL's server program (step 4, item 6): no housecarl-mcp.exe, runtimeconfig
    /// or deps anywhere under it (the tests' own folder included), no Tools\housecarl-mcp\, and its marker names no server
    /// and no housecarl-mcp tool. The program is the integration module's. Rejects the old layout, a project reference
    /// that carries the program into the tests' folder, and a build that leaves an older build's copy in place.</summary>
    [Fact]
    public void No_folder_of_the_build_holds_houseCARLs_server_program()
    {
        var root = TestBuild.Layout.Root;
        var program = new[] { "housecarl-mcp.exe", "housecarl-mcp.runtimeconfig.json", "housecarl-mcp.deps.json" };
        var found = Directory.EnumerateFiles(root, "housecarl-mcp.*", SearchOption.AllDirectories)
            .Where(f => program.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(found.Count == 0, "the build holds houseCARL's server program: " + string.Join(", ", found));
        Assert.False(Directory.Exists(Path.Combine(root, "Tools", "housecarl-mcp")), $"the build still has {Path.Combine(root, "Tools", "housecarl-mcp")}");
        var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, L.LayoutMarker)))!.AsObject();
        Assert.False(marker.ContainsKey("server"), "the build's marker still names a server");
        Assert.DoesNotContain("housecarl-mcp", marker["tools"]!.AsArray().Select(n => (string?)n));
    }

    /// <summary>Only these tests know where houseCARL's server program is (the assembly metadata HousecarlServerProgram,
    /// which Directory.Build.props gives workbench.tests alone): no source of the app, a probe, runcontrol, the shared
    /// code, the engine or the guards, and no Python tool outside a tests folder, names that program, the pin's build
    /// output it sits in, or the metadata. Comments count. Rejects app code that would start or read the program by its
    /// path. (A path built from separate pieces that name none of these is not seen; the build check above still holds
    /// no program in reach.)</summary>
    [Fact]
    public void Only_the_tests_name_houseCARLs_server_program()
    {
        var checkout = TestBuild.Layout.Checkout ?? throw new Xunit.Sdk.XunitException("the tests' build is not in a checkout");
        var tokens = new[] { "housecarl-mcp.exe", "housecarl-mcp/bin", "HousecarlServerProgram" };
        static bool Skipped(string rel) =>
            rel.Split('/').Any(d => d is "bin" or "obj" or "tests" or "workbench.tests" or "SkyWright.Engine.Tests" or "known-answers" or "fixtures");
        var hits = new List<string>();
        foreach (var (dir, pattern) in new[] { ("_geometry-harness", "*.cs"), ("src", "*.cs"), ("_geometry-harness", "*.py"), ("scripts", "*.py") })
        {
            var top = Path.Combine(checkout, dir);
            if (!Directory.Exists(top)) continue;
            foreach (var f in Directory.EnumerateFiles(top, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(checkout, f).Replace('\\', '/');
                if (Skipped(Path.GetDirectoryName(rel)!.Replace('\\', '/'))) continue;
                var text = File.ReadAllText(f).Replace("\\\\", "/").Replace('\\', '/');
                foreach (var t in tokens)
                    if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) hits.Add($"{rel} names {t}");
            }
        }
        Assert.True(hits.Count == 0, "outside the tests, code names houseCARL's server program: " + string.Join("; ", hits));
    }

    /// <summary>The tests' own entry to houseCARL's server program is the pin's build output, and the program is there.</summary>
    [Fact]
    public void The_tests_find_houseCARLs_server_program_in_the_pins_build_output()
    {
        var server = TestBuild.Server;
        var pin = Path.Combine(TestBuild.Layout.Checkout!, "external", "houseCARL", "src", "housecarl-mcp", "bin");
        Assert.StartsWith(N(pin), N(server), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(server), $"houseCARL's server program is not built at {server}; build the solution");
    }
}
