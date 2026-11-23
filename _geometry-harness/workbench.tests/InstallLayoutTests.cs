using HarnessShared;
using Xunit;
using L = HarnessShared.CheckoutLayout;

namespace WorkbenchTests;

/// <summary>The installed layout (step 4 of the standalone task): found beside a program, the checkout walk as the
/// fallback, an override winning, an incomplete install refused, the manifest's hashes checked, and the probe-folder
/// rule for both shapes. The expected paths are spelled here by hand, not by the rule under test.
/// Env-var tests share the "Shell" collection, which runs alone.</summary>
[Collection("Shell")]
public sealed class InstallLayoutTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "wb-install-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>What a fake install lists: manifest key and relative path.</summary>
    static readonly (string Key, string Rel)[] Pieces =
    [
        ("workbench", "workbench/workbench.exe"), ("runcontrol", "runcontrol/runcontrol.exe"),
        ("program:viewprobe", "viewprobe/viewprobe.exe"), ("program:navownprobe", "navownprobe/navownprobe.exe"),
        ("server", "housecarl-mcp/housecarl-mcp.exe"), ("corpus", "housecarl-mcp/corpus.json"),
        ("registry", "data/check-registry.json"), ("severity", "data/workbench-severity.json"),
        ("scenePolicy", "housecarl/.claude/skills/evaluation-reporting/references/scene-policy.json"),
    ];

    /// <summary>Every override variable as it was before the test, cleared for the test (another class may leave one set).</summary>
    readonly Dictionary<string, string?> _saved = Enum.GetValues<L.Piece>().Select(L.OverrideVariable)
        .ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public InstallLayoutTests()
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

    /// <summary>A checkout marker at the temp root (to tempt the walk), and a complete install under it at inst/.</summary>
    string FakeInstall()
    {
        File.WriteAllText(Path.Combine(_tmp, L.Marker), "");
        var root = Path.Combine(_tmp, "inst");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, rel) in Pieces.Append(("extra", "viewprobe/viewprobe.dll")))
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "content of " + rel);
            files[rel] = L.Hash(full);
        }
        new L.Manifest("0123456789abcdef0123456789abcdef01234567", "fedcba9876543210fedcba9876543210fedcba98", "2026-11-22T00:00:00Z",
                       Pieces.ToDictionary(p => p.Key, p => p.Rel), files).Write(Path.Combine(root, L.ManifestName));
        return root;
    }

    [Fact]
    public void InstalledFoundBesideProgram()
    {
        var root = FakeInstall();
        var layout = L.Locate(Path.Combine(root, "runcontrol"), out var why);
        Assert.True(layout is not null, "an install beside the program was not found: " + why);
        Assert.Equal(L.Kind.Installed, layout!.Kind);
        Assert.Equal(N(root), N(layout.Root));
        Assert.Equal(N(Path.Combine(root, "data", "check-registry.json")), N(layout.PathOf(L.Piece.Registry)));
        Assert.Equal(N(Path.Combine(root, "data", "workbench-severity.json")), N(layout.PathOf(L.Piece.Severity)));
        Assert.Equal(N(Path.Combine(root, "housecarl-mcp", "housecarl-mcp.exe")), N(layout.PathOf(L.Piece.Server)));
        Assert.Equal(N(Path.Combine(root, "housecarl-mcp", "corpus.json")), N(layout.PathOf(L.Piece.Corpus)));
        Assert.Equal(N(Path.Combine(root, "runcontrol", "runcontrol.exe")), N(layout.PathOf(L.Piece.Runcontrol)));
        Assert.Equal(N(Path.Combine(root, "housecarl", ".claude", "skills", "evaluation-reporting", "references", "scene-policy.json")), N(layout.PathOf(L.Piece.ScenePolicy)));
        Assert.Equal(N(root), N(layout.Harness));
        Assert.Equal(N(Path.Combine(root, "viewprobe", "viewprobe.exe")), N(layout.Program("viewprobe")));
        // the workbench, in its own folder, finds the same install
        Assert.Equal(N(root), N(L.Locate(Path.Combine(root, "workbench"), out _)!.Root));
    }

    [Fact]
    public void CheckoutFallback()
    {
        File.WriteAllText(Path.Combine(_tmp, L.Marker), "");
        var bin = Path.Combine(_tmp, "_geometry-harness", "runcontrol", "bin", "Release", "net9.0");
        Directory.CreateDirectory(bin);
        var layout = L.Locate(bin, out var why);
        Assert.True(layout is not null, "the checkout above the program was not found: " + why);
        Assert.Equal(L.Kind.Checkout, layout!.Kind);
        Assert.Equal(N(_tmp), N(layout.Root));
        Assert.Equal(N(Path.Combine(_tmp, "data", "check-registry.json")), N(layout.PathOf(L.Piece.Registry)));
        Assert.Equal(N(Path.Combine(_tmp, "external", "houseCARL", "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe")), N(layout.PathOf(L.Piece.Server)));
        Assert.Equal(N(Path.Combine(_tmp, "external", "houseCARL", "generated", "corpus.json")), N(layout.PathOf(L.Piece.Corpus)));
        Assert.Equal(N(Path.Combine(_tmp, "_geometry-harness", "runcontrol", "bin", "Release", "net9.0", "runcontrol.exe")), N(layout.PathOf(L.Piece.Runcontrol)));
        Assert.Equal(N(Path.Combine(_tmp, "_geometry-harness")), N(layout.Harness));
        // neither an install nor a checkout: one sentence naming both
        var none = Path.Combine(Path.GetTempPath(), "wb-none-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(none);
        try
        {
            Assert.Null(L.Locate(none, out var noWhy));
            Assert.Contains("not in a SkyWright install", noWhy);
            Assert.Contains("or checkout", noWhy);
        }
        finally { Directory.Delete(none); }
    }

    [Fact]
    public void OverrideWins()
    {
        var root = FakeInstall();
        var layout = L.Locate(Path.Combine(root, "workbench"), out var why) ?? throw new Xunit.Sdk.XunitException("no layout: " + why);
        var over = Path.Combine(_tmp, "elsewhere", "check-registry.json");
        Environment.SetEnvironmentVariable("WB_REGISTRY", over);
        Environment.SetEnvironmentVariable("WB_SERVER", Path.Combine(_tmp, "elsewhere", "housecarl-mcp.exe"));
        Assert.Equal((over, "WB_REGISTRY"), L.Resolve(L.Piece.Registry, layout));
        Assert.Equal((Path.Combine(_tmp, "elsewhere", "housecarl-mcp.exe"), "WB_SERVER"), L.Resolve(L.Piece.Server, layout));
        // a piece with no override still comes from the install
        Assert.Equal((Path.Combine(root, "data", "workbench-severity.json"), "install"), L.Resolve(L.Piece.Severity, layout));
        // the mixing is named in the start line
        var said = layout.Describe();
        Assert.Contains("WB_REGISTRY=" + over.Replace('\\', '/'), said);
        Assert.Contains("WB_SERVER=", said);
        Environment.SetEnvironmentVariable("WB_REGISTRY", null);
        Environment.SetEnvironmentVariable("WB_SERVER", null);
        Assert.Equal((Path.Combine(root, "data", "check-registry.json"), "install"), L.Resolve(L.Piece.Registry, layout));
        Assert.DoesNotContain("overridden", layout.Describe());
    }

    [Fact]
    public void IncompleteRefused()
    {
        var root = FakeInstall();
        File.Delete(Path.Combine(root, "housecarl-mcp", "corpus.json"));
        var layout = L.Locate(Path.Combine(root, "runcontrol"), out var why);
        // refused, and not passed over for the checkout planted above the install
        Assert.True(layout is null, $"an install missing its corpus was accepted as {layout?.Kind} at {layout?.Root}");
        Assert.Contains("is incomplete: corpus (housecarl-mcp/corpus.json) is missing", why);
        Assert.True(L.BesideInstall(Path.Combine(root, "runcontrol")));

        // a folder in the install that is not one of its programs
        var stray = Path.Combine(root, "strayprobe");
        Directory.CreateDirectory(stray);
        Assert.Null(L.Locate(stray, out var strayWhy));
        Assert.Contains("is not one of its programs", strayWhy);

        // an unreadable manifest
        File.WriteAllText(Path.Combine(root, L.ManifestName), "{ \"kind\": \"something-else\" }");
        Assert.Null(L.Locate(Path.Combine(root, "runcontrol"), out var badWhy));
        Assert.Contains("unreadable manifest", badWhy);
    }

    [Fact]
    public void ManifestHashesChecked()
    {
        var root = FakeInstall();
        var (clean, cleanExtra) = L.Verify(root);
        Assert.Empty(clean);
        Assert.Empty(cleanExtra);

        // one byte changed in a listed file
        var sev = Path.Combine(root, "data", "workbench-severity.json");
        var bytes = File.ReadAllBytes(sev);
        bytes[0] ^= 1;
        File.WriteAllBytes(sev, bytes);
        // one listed file gone, and one file nobody listed
        File.Delete(Path.Combine(root, "viewprobe", "viewprobe.dll"));
        File.WriteAllText(Path.Combine(root, "workbench", "launcher-settings.json"), "{}");

        var (problems, unlisted) = L.Verify(root);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("data/workbench-severity.json has hash ", StringComparison.Ordinal));
        Assert.Contains("viewprobe/viewprobe.dll is missing.", problems);
        Assert.Equal(["workbench/launcher-settings.json"], unlisted);
    }

    [Fact]
    public void ProbeFolderBothShapes()
    {
        var root = FakeInstall();
        Assert.Equal(N(Path.Combine(root, "navownprobe")), N(L.ProgramFolder(root, "navownprobe")));
        Assert.Equal(N(Path.Combine(root, "navownprobe", "navownprobe.exe")), N(L.ProgramExe(root, "navownprobe")));
        var harness = Path.Combine(_tmp, "_geometry-harness");
        Directory.CreateDirectory(harness);
        Assert.Equal(N(Path.Combine(harness, "navownprobe", "bin", "Release", "net9.0")), N(L.ProgramFolder(harness, "navownprobe")));
        // the harness a runcontrol.exe belongs to, in both shapes
        Assert.Equal(N(root), N(L.HarnessOf(Path.Combine(root, "runcontrol", "runcontrol.exe"))));
        Assert.Equal(N(harness), N(L.HarnessOf(Path.Combine(harness, "runcontrol", "bin", "Release", "net9.0", "runcontrol.exe"))));
    }
}
