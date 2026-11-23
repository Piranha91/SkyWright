using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessShared;

/// <summary>Where a program finds the pieces it takes from SkyWright: the other programs (runcontrol, the probes),
/// houseCARL's server and data (the rulebook, the reporting skill's scene policy), and the app data (data/).
/// The one owner of that lookup, for both layouts:
/// <list type="bullet">
/// <item>an installed layout: <c>&lt;root&gt;/skywright-install.json</c> (the manifest), each program in
/// <c>&lt;root&gt;/&lt;program&gt;/</c>, houseCARL's server and its rulebook in <c>&lt;root&gt;/housecarl-mcp/</c>, its other data under
/// <c>&lt;root&gt;/housecarl/</c>, the app data in <c>&lt;root&gt;/data/</c>; written by <c>skywright-publish</c>;</item>
/// <item>a checkout: the folder holding <c>SkyWright.sln</c>, each program in its in-tree <c>bin/Release</c>, houseCARL
/// the submodule <c>external/houseCARL</c>.</item>
/// </list>
/// Order, per piece: its <c>WB_*</c> override; else the install this program's folder sits in; else the checkout above
/// it. An installed program never falls back to a checkout piece, and an incomplete install is refused.</summary>
internal static class CheckoutLayout
{
    /// <summary>The file that marks a SkyWright checkout's root.</summary>
    public const string Marker = "SkyWright.sln";

    /// <summary>The file that marks an installed layout's root, and lists what is in it.</summary>
    public const string ManifestName = "skywright-install.json";

    /// <summary>The kind of manifest this owner reads and writes.</summary>
    public const string ManifestKind = "skywright-install";
    public const int ManifestSchema = 1;

    /// <summary>The reporting skill's grouping data, relative to houseCARL's data root (the submodule, or the install's housecarl/).</summary>
    public static readonly string ScenePolicyRelative = Path.Combine(".claude", "skills", "evaluation-reporting", "references", "scene-policy.json");

    /// <summary>The rulebook, relative to houseCARL's data root, where its generator writes it.</summary>
    public static readonly string CorpusRelative = Path.Combine("generated", "corpus.json");

    /// <summary>The pieces a program can ask for, and the variable that overrides each.</summary>
    public enum Piece { Server, Corpus, Registry, Severity, Runcontrol, Harness, ScenePolicy }

    public static string OverrideVariable(Piece p) => p switch
    {
        Piece.Server => "WB_SERVER", Piece.Corpus => "WB_CORPUS", Piece.Registry => "WB_REGISTRY", Piece.Severity => "WB_SEVERITY",
        Piece.Runcontrol => "WB_RUNCONTROL", Piece.Harness => "WB_HARNESS", Piece.ScenePolicy => "WB_SCENE_POLICY",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>The variables that name one program, or folders of programs, in place of the layout's (the workbench's
    /// re-check and navmesh repair read them); named with the pieces' overrides when a program starts.</summary>
    public static readonly string[] ProgramOverrides = ["WB_NAVOWNPROBE", "WB_CONFLICTDIFF", "WB_PROBE_DIRS"];

    /// <summary>The manifest's name for a piece (its <c>pieces</c> key).</summary>
    public static string PieceKey(Piece p) => p switch
    {
        Piece.Server => "server", Piece.Corpus => "corpus", Piece.Registry => "registry", Piece.Severity => "severity",
        Piece.Runcontrol => "runcontrol", Piece.ScenePolicy => "scenePolicy",
        _ => throw new ArgumentOutOfRangeException(nameof(p), "the harness is a folder, not a listed piece"),
    };

    public enum Kind { Installed, Checkout }

    /// <summary>A found layout: which kind, its root, and (installed) its manifest.</summary>
    public sealed record Layout(Kind Kind, string Root, Manifest? Manifest)
    {
        public bool Installed => Kind == Kind.Installed;

        /// <summary>houseCARL's data root: the install's housecarl/, or the submodule.</summary>
        public string HousecarlData => Installed ? Path.Combine(Root, "housecarl") : Path.Combine(Root, "external", "houseCARL");

        /// <summary>The folder the programs sit under: the install root, or the checkout's _geometry-harness.</summary>
        public string Harness => Installed ? Root : Path.Combine(Root, "_geometry-harness");

        /// <summary>A piece's path in this layout (no override).</summary>
        public string PathOf(Piece p) => p switch
        {
            Piece.Server => Installed ? Path.Combine(Root, "housecarl-mcp", "housecarl-mcp.exe")
                                      : Path.Combine(Root, "external", "houseCARL", "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe"),
            // installed: beside the server, which is where the server reads it when given no path
            Piece.Corpus => Installed ? Path.Combine(Root, "housecarl-mcp", "corpus.json") : Path.Combine(HousecarlData, CorpusRelative),
            Piece.Registry => Path.Combine(Root, "data", "check-registry.json"),
            Piece.Severity => Path.Combine(Root, "data", "workbench-severity.json"),
            Piece.Runcontrol => ProgramExe(Harness, "runcontrol"),
            Piece.Harness => Harness,
            Piece.ScenePolicy => Path.Combine(HousecarlData, ScenePolicyRelative),
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        /// <summary>One of SkyWright's programs (a probe, runcontrol, conflictdiff) in this layout.</summary>
        public string Program(string name) => ProgramExe(Harness, name);

        /// <summary>The one line a program logs when it starts: where its pieces come from, naming every override that
        /// mixes another folder's piece into an install.</summary>
        public string Describe()
        {
            var head = Installed ? $"SkyWright install {Fwd(Root)} (commit {Manifest!.Commit[..Math.Min(12, Manifest.Commit.Length)]})" : $"SkyWright checkout {Fwd(Root)}";
            var over = Overrides();
            return over.Count == 0 ? head : head + "; overridden: " + string.Join(", ", over.Select(o => $"{o.Variable}={Fwd(o.Path)}"));
        }

        /// <summary>The overrides set now, in piece order.</summary>
        public static IReadOnlyList<(string Variable, string Path)> Overrides() =>
            Enum.GetValues<Piece>().Select(OverrideVariable).Concat(ProgramOverrides).Select(v => (v, x: Environment.GetEnvironmentVariable(v)))
                .Where(t => t.x is { Length: > 0 } && !string.IsNullOrWhiteSpace(t.x)).Select(t => (t.v, t.x!)).ToList();
    }

    /// <summary>A piece's path: its override when set, else the layout's. (path, where it came from: the variable, or "install"/"checkout").</summary>
    public static (string Path, string Source) Resolve(Piece p, Layout layout) =>
        Environment.GetEnvironmentVariable(OverrideVariable(p)) is { } v && !string.IsNullOrWhiteSpace(v)
            ? (v, OverrideVariable(p))
            : (layout.PathOf(p), layout.Installed ? "install" : "checkout");

    /// <summary>A piece's path for this program: its override when set (no layout needed), else the layout this
    /// program sits in; throws <see cref="InvalidOperationException"/> with one sentence when there is none.</summary>
    public static string Resolve(Piece p, string what)
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable(p)) is { } v && !string.IsNullOrWhiteSpace(v)) return v;
        var layout = Locate(null, out var why)
            ?? throw new InvalidOperationException($"{why}; or set {OverrideVariable(p)} to the {what}.");
        return layout.PathOf(p);
    }

    // ---- a named checkout's pieces (tests and checkout-only checks), by the same rule

    public static Layout InCheckout(string root) => new(Kind.Checkout, root, null);
    public static string Housecarl(string root) => InCheckout(root).HousecarlData;
    public static string ServerExe(string root) => InCheckout(root).PathOf(Piece.Server);
    public static string Corpus(string root) => InCheckout(root).PathOf(Piece.Corpus);
    public static string Registry(string root) => InCheckout(root).PathOf(Piece.Registry);
    public static string Severity(string root) => InCheckout(root).PathOf(Piece.Severity);

    // ---- the probe path rule

    /// <summary>The folder a program is built or installed in under <paramref name="harness"/>: <c>&lt;harness&gt;/&lt;name&gt;</c>
    /// when the harness is an install root (it holds the manifest), else the checkout's <c>&lt;harness&gt;/&lt;name&gt;/bin/Release/net9.0</c>.
    /// The checkout shape holds for the net9.0 programs runcontrol and the workbench launch; the Windows-targeted
    /// builds (viewprobe, winrt-tts, the workbench itself) sit under net9.0-windows… and are never looked up here.</summary>
    public static string ProgramFolder(string harness, string name) =>
        File.Exists(Path.Combine(harness, ManifestName)) ? Path.Combine(harness, name) : Path.Combine(harness, name, "bin", "Release", "net9.0");

    /// <summary>The program's exe under <paramref name="harness"/> (see <see cref="ProgramFolder"/>).</summary>
    public static string ProgramExe(string harness, string name) =>
        Path.Combine(ProgramFolder(harness, name), OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>The harness a given runcontrol.exe belongs to: the install root when its folder's parent holds the
    /// manifest, else the checkout's _geometry-harness four folders up (runcontrol/bin/Release/net9.0).</summary>
    public static string HarnessOf(string runcontrolExe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(runcontrolExe))!;
        var parent = Path.GetDirectoryName(folder);
        return parent is not null && File.Exists(Path.Combine(parent, ManifestName))
            ? parent : Path.GetFullPath(Path.Combine(folder, "..", "..", "..", ".."));
    }

    // ---- finding the layout

    /// <summary>The SkyWright checkout above <paramref name="from"/> (default: this program's folder), or null. Only the checkout walk.</summary>
    public static string? FindRoot(string? from = null)
    {
        var dir = new DirectoryInfo(from ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Marker))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>The layout the program in <paramref name="from"/> (default: this program's folder) belongs to, or null
    /// with one sentence in <paramref name="why"/>. Installed first: the folder's parent holds the manifest. Then the
    /// checkout walk. A found install that is broken (an unreadable manifest, this folder not one of its programs, a
    /// listed piece missing) is refused, never passed over for a checkout.</summary>
    public static Layout? Locate(string? from, out string? why)
    {
        var folder = Path.GetFullPath(from ?? AppContext.BaseDirectory).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(folder);
        if (parent is not null && File.Exists(Path.Combine(parent, ManifestName)))
            return Installed(parent, Path.GetFileName(folder), out why);
        if (FindRoot(folder) is { } root) { why = null; return new Layout(Kind.Checkout, root, null); }
        why = $"this program is not in a SkyWright install (no {ManifestName} beside its folder) or checkout (no {Marker} above {Fwd(folder)})";
        return null;
    }

    /// <summary>The checkout this program was built in, or null when it runs from an install (even one placed inside a
    /// checkout's folder) or from neither. For checks that are about a checkout itself, such as its Python.</summary>
    public static string? BuiltInCheckout(string? from = null) => Locate(from, out _) is { Kind: Kind.Checkout } l ? l.Root : null;

    /// <summary>Whether the folder <paramref name="from"/> (default: this program's) sits in an install: its parent holds the manifest.</summary>
    public static bool BesideInstall(string? from = null) =>
        Path.GetDirectoryName(Path.GetFullPath(from ?? AppContext.BaseDirectory).TrimEnd('\\', '/')) is { } parent && File.Exists(Path.Combine(parent, ManifestName));

    static Layout? Installed(string root, string programFolder, out string? why)
    {
        var manifestPath = Path.Combine(root, ManifestName);
        Layout? layout = null;
        Manifest? m;
        try { m = Manifest.Read(manifestPath); why = null; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { m = null; why = $"the SkyWright install at {Fwd(root)} has an unreadable manifest ({ex.Message}); publish it again"; }
        if (m is not null)
        {
            // a program's folder: the folder of a listed program (the app, runcontrol, a probe, the server), not a data folder
            why = !m.Pieces.Where(kv => kv.Key.StartsWith("program:", StringComparison.Ordinal) || kv.Key is "app" or "runcontrol" or "server")
                    .Any(kv => string.Equals(Path.GetDirectoryName(Native(kv.Value)), programFolder, StringComparison.OrdinalIgnoreCase))
                ? $"the folder {Fwd(Path.Combine(root, programFolder))} sits in the SkyWright install at {Fwd(root)} but is not one of its programs; run the install's own copy, or publish again"
                : Complete(root, m);
            if (why is null) layout = new Layout(Kind.Installed, root, m);
        }
        return layout;
    }

    /// <summary>Null when every piece the manifest lists is present; else one sentence naming the first missing one.</summary>
    static string? Complete(string root, Manifest m)
    {
        foreach (var (name, rel) in m.Pieces)
            if (!File.Exists(Path.Combine(root, Native(rel))))
                return $"the SkyWright install at {Fwd(root)} is incomplete: {name} ({rel}) is missing; publish it again";
        return null;
    }

    // ---- the manifest

    /// <summary>The install's manifest: the commit it was published from, its named pieces and every file's hash.</summary>
    public sealed record Manifest(string Commit, string HousecarlPin, string PublishedUtc,
                                  IReadOnlyDictionary<string, string> Pieces, IReadOnlyDictionary<string, string> Files)
    {
        /// <summary>The manifest at <paramref name="path"/>; any shape it should not have is an <see cref="InvalidDataException"/>.</summary>
        public static Manifest Read(string path)
        {
            var text = File.ReadAllText(path);
            try { return Parse(text); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or JsonException or InvalidCastException)
            { throw new InvalidDataException(ex.Message, ex); }
        }

        static Manifest Parse(string text)
        {
            var o = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("not a JSON object");
            if ((string?)o["kind"] != ManifestKind) throw new InvalidDataException($"its kind is not {ManifestKind}");
            if ((int?)o["schema"] != ManifestSchema) throw new InvalidDataException($"its schema is not {ManifestSchema}");
            static Dictionary<string, string> Map(JsonNode? n, string what) =>
                (n as JsonObject ?? throw new InvalidDataException($"it has no {what}"))
                .ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? throw new InvalidDataException($"{what} {kv.Key} is not a string"), StringComparer.Ordinal);
            var pieces = Map(o["pieces"], "pieces");
            var files = Map(o["files"], "files");
            if (pieces.Count == 0) throw new InvalidDataException("it lists no pieces");
            foreach (var (k, rel) in pieces)
                if (!files.ContainsKey(rel)) throw new InvalidDataException($"piece {k} ({rel}) has no hash in files");
            return new Manifest((string?)o["commit"] ?? throw new InvalidDataException("it names no commit"),
                                (string?)o["housecarlPin"] ?? "", (string?)o["publishedUtc"] ?? "", pieces, files);
        }

        public void Write(string path)
        {
            var o = new JsonObject
            {
                ["schema"] = ManifestSchema, ["kind"] = ManifestKind, ["commit"] = Commit, ["housecarlPin"] = HousecarlPin,
                ["publishedUtc"] = PublishedUtc,
                ["pieces"] = new JsonObject(Pieces.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["files"] = new JsonObject(Files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
            File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }

    /// <summary>A file's hash as the manifest records it: sha256, lower hex.</summary>
    public static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>The manifest's spelling of a path under <paramref name="root"/>: relative, forward slashes.</summary>
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Every listed file re-hashed: (problems, one sentence each; files present that the manifest does not list).
    /// No problems means the install is exactly what was published.</summary>
    public static (IReadOnlyList<string> Problems, IReadOnlyList<string> Unlisted) Verify(string root)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(root, ManifestName);
        Manifest m;
        try { m = Manifest.Read(manifestPath); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return ([$"the manifest {Fwd(manifestPath)} cannot be read: {ex.Message}"], []); }
        foreach (var (rel, want) in m.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var full = Path.Combine(root, Native(rel));
            if (!File.Exists(full)) { problems.Add($"{rel} is missing."); continue; }
            var got = Hash(full);
            if (!string.Equals(got, want, StringComparison.Ordinal)) problems.Add($"{rel} has hash {got}, but the manifest says {want}.");
        }
        var listed = new HashSet<string>(m.Files.Keys, StringComparer.OrdinalIgnoreCase) { ManifestName };
        var unlisted = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Relative(root, f))
            .Where(r => !listed.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
        return (problems, unlisted);
    }

    static string Fwd(string p) => p.Replace('\\', '/');
}
