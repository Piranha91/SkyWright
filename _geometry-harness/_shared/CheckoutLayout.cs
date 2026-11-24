using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessShared;

/// <summary>Where a program finds the pieces it takes from SkyWright: the other programs (runcontrol, the probes),
/// houseCARL's server and rulebook, and the app data. The one owner of that lookup.
/// <para>A build of the solution writes one folder per configuration, <c>bin\&lt;Configuration&gt;\&lt;.NET version&gt;\</c>:
/// SkyWright.exe at its root, <c>Probes\&lt;name&gt;\</c>, <c>Tools\&lt;name&gt;\</c>, <c>Data\</c>, and the marker
/// <c>skywright-layout.json</c>, which names where each piece is. <c>Directory.Build.props</c> owns that layout and
/// writes the marker; this class reads it. A program finds its build from its own folder: the root (the app) or two
/// folders up (a probe, a tool, the tests).</para>
/// <para>Order, per piece: its <c>WB_*</c> override, else the build the program sits in. There is no walk up a
/// checkout at run time, so pieces of two builds never mix unless an override names one, and the app logs every
/// override when it starts.</para></summary>
internal static class CheckoutLayout
{
    /// <summary>The file that marks a SkyWright checkout's root (for the repository's own files: docs, Python).</summary>
    public const string Marker = "SkyWright.sln";

    /// <summary>The file that marks a build folder's root and names its layout.</summary>
    public const string LayoutMarker = "skywright-layout.json";

    /// <summary>The pieces a program can ask for, and the variable that overrides each.</summary>
    public enum Piece { Server, Corpus, Registry, Severity, Runcontrol, Harness, ScenePolicy }

    public static string OverrideVariable(Piece p) => p switch
    {
        Piece.Server => "WB_SERVER", Piece.Corpus => "WB_CORPUS", Piece.Registry => "WB_REGISTRY", Piece.Severity => "WB_SEVERITY",
        Piece.Runcontrol => "WB_RUNCONTROL", Piece.Harness => "WB_HARNESS", Piece.ScenePolicy => "WB_SCENE_POLICY",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>The variables that name one program, or folders of programs, in place of the build's (the workbench's
    /// re-check and navmesh repair read them); named with the pieces' overrides when a program starts.</summary>
    public static readonly string[] ProgramOverrides = ["WB_NAVOWNPROBE", "WB_CONFLICTDIFF", "WB_PROBE_DIRS"];

    // ---- the marker

    /// <summary>The layout a build's marker names: relative paths, with {name} standing for a program's name.</summary>
    public sealed record Spec(string Configuration, string Program, string Tool, IReadOnlyList<string> Tools,
                              string Server, string Corpus, string Registry, string Severity, string ScenePolicy)
    {
        /// <summary>The names used only to say where a piece was expected when a folder has no marker. A test holds
        /// them equal to the marker a build writes.</summary>
        public static readonly Spec Expected = new("", "Probes/{name}/{name}.exe", "Tools/{name}/{name}.exe", ["runcontrol", "housecarl-mcp"],
            "Tools/housecarl-mcp/housecarl-mcp.exe", "Data/corpus.json", "Data/check-registry.json",
            "Data/workbench-severity.json", "Data/scene-policy.json");

        public static Spec Read(string path)
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("it is not a JSON object");
                if ((string?)o["kind"] != "skywright-build") throw new InvalidDataException("its kind is not skywright-build");
                string S(string k) => (string?)o[k] is { Length: > 0 } v ? v : throw new InvalidDataException($"it has no {k}");
                var tools = (o["tools"] as JsonArray ?? throw new InvalidDataException("it has no tools")).Select(n => (string?)n ?? "").ToList();
                return new Spec(S("configuration"), S("program"), S("tool"), tools, S("server"), S("corpus"), S("registry"), S("severity"), S("scenePolicy"));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
            { throw new InvalidDataException(ex.Message, ex); }
        }

        /// <summary>A program's exe, relative: a tool's (runcontrol) or a probe's.</summary>
        public string ExeOf(string name) => (Tools.Contains(name, StringComparer.OrdinalIgnoreCase) ? Tool : Program).Replace("{name}", name);
    }

    static readonly Dictionary<string, (DateTime Written, Spec Spec)> _specs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The marker at <paramref name="root"/>, read once per change; null when there is none. Throws
    /// <see cref="InvalidDataException"/> when it cannot be read.</summary>
    static Spec? SpecAt(string root)
    {
        var path = Path.Combine(root, LayoutMarker);
        if (!File.Exists(path)) return null;
        var written = File.GetLastWriteTimeUtc(path);
        lock (_specs)
        {
            if (_specs.TryGetValue(path, out var hit) && hit.Written == written) return hit.Spec;
            var spec = Spec.Read(path);
            _specs[path] = (written, spec);
            return spec;
        }
    }

    static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    // ---- a build

    /// <summary>A build folder: its root and the layout its marker names.</summary>
    public sealed record Layout(string Root, Spec Spec)
    {
        /// <summary>The folder the programs sit under, as plans record it (--harness): the build's root.</summary>
        public string Harness => Root;

        /// <summary>A piece's path in this build (no override).</summary>
        public string PathOf(Piece p) => p switch
        {
            Piece.Server => Path.Combine(Root, Native(Spec.Server)),
            Piece.Corpus => Path.Combine(Root, Native(Spec.Corpus)),
            Piece.Registry => Path.Combine(Root, Native(Spec.Registry)),
            Piece.Severity => Path.Combine(Root, Native(Spec.Severity)),
            Piece.Runcontrol => Program("runcontrol"),
            Piece.Harness => Root,
            Piece.ScenePolicy => Path.Combine(Root, Native(Spec.ScenePolicy)),
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        /// <summary>One of SkyWright's programs (a probe, runcontrol, conflictdiff) in this build.</summary>
        public string Program(string name) => Path.Combine(Root, Native(Spec.ExeOf(name)));

        /// <summary>The checkout this build was made in (its Python tools and repository files), or null.</summary>
        public string? Checkout => FindRoot(Root);

        /// <summary>The one line a program logs when it starts: which build, naming every override that is set.</summary>
        public string Describe()
        {
            var head = $"SkyWright {Spec.Configuration} build {Fwd(Root)}";
            var over = Overrides();
            return over.Count == 0 ? head : head + "; overridden: " + string.Join(", ", over.Select(o => $"{o.Variable}={Fwd(o.Path)}"));
        }

        /// <summary>The overrides set now, in piece order, then the program overrides.</summary>
        public static IReadOnlyList<(string Variable, string Path)> Overrides() =>
            Enum.GetValues<Piece>().Select(OverrideVariable).Concat(ProgramOverrides).Select(v => (v, x: Environment.GetEnvironmentVariable(v)))
                .Where(t => !string.IsNullOrWhiteSpace(t.x)).Select(t => (t.v, t.x!)).ToList();
    }

    /// <summary>A piece's path: its override when set, else the build's. (path, where it came from: the variable, or "build").</summary>
    public static (string Path, string Source) Resolve(Piece p, Layout layout) =>
        Environment.GetEnvironmentVariable(OverrideVariable(p)) is { } v && !string.IsNullOrWhiteSpace(v)
            ? (v, OverrideVariable(p))
            : (layout.PathOf(p), "build");

    /// <summary>A piece's path for this program: its override when set (no build needed), else the build this program
    /// sits in; throws <see cref="InvalidOperationException"/> with one sentence when there is none.</summary>
    public static string Resolve(Piece p, string what)
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable(p)) is { } v && !string.IsNullOrWhiteSpace(v)) return v;
        var layout = Locate(null, out var why)
            ?? throw new InvalidOperationException($"{why}; or set {OverrideVariable(p)} to the {what}.");
        return layout.PathOf(p);
    }

    // ---- the program path rule

    /// <summary>The program's exe under <paramref name="harness"/> (a build's root, as plans record it), where its
    /// marker says. A folder with no readable marker gets the path a build would have, so a refusal can say where it looked.</summary>
    public static string ProgramExe(string harness, string name)
    {
        Spec spec;
        try { spec = SpecAt(harness) ?? Spec.Expected; }
        catch (InvalidDataException) { spec = Spec.Expected; }
        return Path.Combine(harness, Native(spec.ExeOf(name)));
    }

    /// <summary>The folder a program sits in under <paramref name="harness"/> (see <see cref="ProgramExe"/>).</summary>
    public static string ProgramFolder(string harness, string name) => Path.GetDirectoryName(ProgramExe(harness, name))!;

    /// <summary>The build a given runcontrol.exe belongs to: two folders up (Tools\runcontrol\).</summary>
    public static string HarnessOf(string runcontrolExe) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runcontrolExe))!, "..", ".."));

    // ---- finding the build

    /// <summary>The SkyWright checkout above <paramref name="from"/> (default: this program's folder), or null. For the
    /// repository's own files only (docs, the Python tools); programs find each other with <see cref="Locate"/>.</summary>
    public static string? FindRoot(string? from = null)
    {
        var dir = new DirectoryInfo(from ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Marker))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>The build the program in <paramref name="from"/> (default: this program's folder) belongs to: the
    /// folder itself when it holds the marker (the app), else two folders up (Probes\x\, Tools\x\, Tests\x\). Null with
    /// one sentence in <paramref name="why"/> when neither holds one, or the marker cannot be read.</summary>
    public static Layout? Locate(string? from, out string? why)
    {
        var folder = Path.GetFullPath(from ?? AppContext.BaseDirectory).TrimEnd('\\', '/');
        var up2 = Path.GetDirectoryName(Path.GetDirectoryName(folder) ?? "");
        foreach (var root in new[] { folder, up2 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                if (SpecAt(root) is { } spec) { why = null; return new Layout(root, spec); }
            }
            catch (InvalidDataException ex)
            {
                why = $"the SkyWright build at {Fwd(root)} has an unreadable {LayoutMarker} ({ex.Message}); build the solution again";
                return null;
            }
        }
        why = $"this program is not in a SkyWright build (no {LayoutMarker} in {Fwd(folder)} or two folders up); build the solution and run it from bin\\<Configuration>";
        return null;
    }

    // ---- a checkout's sources (tests and the build's own inputs)

    /// <summary>The pinned houseCARL checkout, the submodule.</summary>
    public static string Housecarl(string root) => Path.Combine(root, "external", "houseCARL");

    /// <summary>The rulebook the generator writes into the submodule's generated/.</summary>
    public static string Corpus(string root) => Path.Combine(Housecarl(root), "generated", "corpus.json");

    /// <summary>The check registry in a checkout's data/.</summary>
    public static string Registry(string root) => Path.Combine(root, "data", "check-registry.json");

    /// <summary>The severity table in a checkout's data/.</summary>
    public static string Severity(string root) => Path.Combine(root, "data", "workbench-severity.json");

    static string Fwd(string p) => p.Replace('\\', '/');
}
