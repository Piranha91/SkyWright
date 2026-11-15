using System;
using System.IO;
using System.Text.Json;

// ======================================================================
//  HarnessTarget — where the geometry-harness probes point.
//
//  NOTHING is compiled in. Every probe used to carry its author's MO2
//  instance path and profile name as a literal default, which is fine on
//  one machine and wrong everywhere else: a shared consumer's first run
//  silently walks a directory they do not have, and the failure surfaces
//  later as "0 plugins" rather than as "you have not told me where your
//  game is".
//
//  Resolution order, first hit wins:
//    1. the probe's own --instance / --profile / --game flags, where it has
//       them (applied by the caller AFTER reading these properties);
//    2. environment variables (below);
//    3. harness.local.json, found by walking up from the running binary to
//       the first directory that contains one — it lives beside the harness
//       and is gitignored;
//    4. nothing — Require() then fails LOUDLY, naming every way to set it.
//
//  EXCEPT the load order's identity. The MO2 instance is NEVER taken from the
//  config file: a run that names no instance (its --instance flag, or
//  HOUSECARL_MO2_INSTANCE set for that run) refuses with
//  `instance-not-explicit` rather than read the config's instance by omission
//  — on a maintainer's machine that is often the play install. The config's
//  `game` and `profile` describe the config's OWN instance, so they are used
//  only when the explicit instance is that instance (same path after
//  normalising); for any other instance they are withheld and Require()
//  refuses with `game-not-for-this-instance` / `profile-not-for-this-instance`.
//  HOUSECARL_MO2_INSTANCE counts only when set for the run: a value equal to
//  the one persisted at User or Machine level refuses with
//  `instance-env-persistent`.
//
//  Unset reads back as "" rather than null so a probe can hold it in a
//  plain string, let a flag overwrite it, and only then Require() it.
// ======================================================================

internal static class HarnessTarget
{
    /// <summary>MO2 instance directory — the one holding `profiles\`, `mods\` and `overwrite\`. The environment only, never the config file (see the header).</summary>
    public static string Instance => EnvInstance();

    /// <summary>MO2 profile name inside that instance. The config's value only for the config's own instance.</summary>
    public static string Profile => GetForInstance("profile", "HOUSECARL_MO2_PROFILE");

    /// <summary>Game install directory — the one holding `Data\`. The config's value only for the config's own instance.</summary>
    public static string Game => GetForInstance("game", "HOUSECARL_GAME_PATH");

    /// <summary>Wabbajack/mod-manager download pool, for the probes that read archives.</summary>
    public static string DownloadPool => Get("downloadPool", "HOUSECARL_DOWNLOAD_POOL");

    /// <summary>Path to 7z.exe, for the probes that crack archives open.</summary>
    public static string SevenZip => Get("sevenZip", "HOUSECARL_SEVENZIP");

    /// <summary>
    /// Directories to sweep for other MO2 instances / mod lists. Semicolon-separated in the
    /// environment; a JSON array (or a semicolon string) in harness.local.json. Empty when unset —
    /// callers Require it via <see cref="RequireRoots"/>, because a drive layout is the most
    /// machine-specific thing a probe can carry.
    /// </summary>
    public static string[] SearchRoots
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("HOUSECARL_SEARCH_ROOTS");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return Split(fromEnv!);

            LoadConfig();
            if (_config is { } root && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("searchRoots", out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return Split(v.GetString() ?? "");
                if (v.ValueKind == JsonValueKind.Array)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var e in v.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                            list.Add(e.GetString()!.Trim());
                    return list.ToArray();
                }
            }
            return Array.Empty<string>();
        }
    }

    /// <summary>As <see cref="Require"/>, for the search-root list.</summary>
    public static System.Collections.Generic.List<string> RequireRoots(
        System.Collections.Generic.List<string> value, string? flag = null)
    {
        if (value is { Count: > 0 }) return value;
        Console.Error.WriteLine("No search roots configured. This harness compiles in no paths — set them by any of:");
        if (flag is not null)
            Console.Error.WriteLine($"  * pass {flag} \"D:\\one;E:\\two\" on the command line");
        Console.Error.WriteLine("  * set HOUSECARL_SEARCH_ROOTS to a semicolon-separated list");
        Console.Error.WriteLine($"  * add to {ConfigName}:  {{ \"searchRoots\": [\"D:\\\\one\", \"E:\\\\two\"] }}");
        Console.Error.WriteLine($"    looked in: {SearchedDescription()}");
        Environment.Exit(2);
        return value;   // unreachable
    }

    static string[] Split(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Return <paramref name="value"/> if it is set, else exit with guidance. Call this AFTER the
    /// probe has applied its own flags, so a run that passes <c>--instance</c> never trips it.
    /// </summary>
    /// <param name="what">Human name of the setting, e.g. "MO2 instance directory".</param>
    /// <param name="key">The harness.local.json key and env var, e.g. "instance".</param>
    /// <param name="flag">The probe's own flag, e.g. "--instance", or null if it has none.</param>
    public static string Require(string value, string what, string key, string? flag = null)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;

        var env = EnvName(key);
        // A game or profile withheld because NO instance was named is the instance's refusal, not its own: a probe
        // that requires its game first must still say what is actually missing.
        var noInstance = key == "instance" || (_withheld.TryGetValue(key, out var w) && w == NoneGiven);
        if (noInstance)
        {
            if (_persistedInstance is { } pi)
            {
                // The variable is set, but it is the value stored at User/Machine level: every run on this machine
                // inherits it, so it is a default by another name, not a choice made for this run.
                Console.Error.WriteLine($"refused: instance-env-persistent — {EnvName("instance")} ({pi.Value}) is the value " +
                                        $"persisted at {pi.Level} level, so it would act as a default for every run.");
                Console.Error.WriteLine(key == "instance" && flag is not null
                    ? $"  * pass {flag} <dir> on the command line"
                    : "  * name the instance on the command line where the probe takes --instance");
                Console.Error.WriteLine($"  * or set {EnvName("instance")} only for this run (a driver's child environment, or `$env:` in one shell)");
                Environment.Exit(2);
            }
            // The named reason leads the line so a driver can match it; the config's instance is named so the reader
            // sees exactly what was NOT read.
            Console.Error.WriteLine("refused: instance-not-explicit — no MO2 instance directory given for this run.");
            if (key == "instance" && flag is not null)
                Console.Error.WriteLine($"  * pass {flag} <dir> on the command line");
            Console.Error.WriteLine($"  * or set the {EnvName("instance")} environment variable for this run");
            if (key != "instance")
                Console.Error.WriteLine($"  * or name the {what} itself: {(flag is null ? "" : $"pass {flag} <value>, or ")}set {env}");
            var cfgInstance = ConfigString("instance");
            Console.Error.WriteLine(cfgInstance.Length > 0
                ? $"  The harness config's \"instance\" ({cfgInstance}, in {SearchedDescription()}) is never used as a default."
                : "  The harness config's \"instance\" is never used as a default.");
            Environment.Exit(2);
        }
        if (_withheld.TryGetValue(key, out var forInstance))
        {
            Console.Error.WriteLine($"refused: {key}-not-for-this-instance — no {what} given for the instance {forInstance}.");
            Console.Error.WriteLine($"  The harness config's \"{key}\" ({ConfigString(key)}) belongs to its own instance " +
                                    $"({ConfigString("instance")}) and is not used for another one.");
            if (flag is not null)
                Console.Error.WriteLine($"  * pass {flag} <value> on the command line");
            Console.Error.WriteLine($"  * or set the {env} environment variable for this run");
            Environment.Exit(2);
        }
        Console.Error.WriteLine($"No {what} configured. This harness compiles in no paths — set it by any of:");
        if (flag is not null)
            Console.Error.WriteLine($"  * pass {flag} <value> on the command line");
        Console.Error.WriteLine($"  * set the {env} environment variable");
        Console.Error.WriteLine($"  * create {ConfigName} beside the harness (searched upward from the binary):");
        Console.Error.WriteLine($"        {{ \"{key}\": \"...\" }}");
        Console.Error.WriteLine($"    looked in: {SearchedDescription()}");
        Environment.Exit(2);
        return value;   // unreachable
    }

    // ------------------------------------------------------------------

    const string ConfigName = "harness.local.json";

    static JsonElement? _config;
    static bool _configLoaded;
    static string _searched = "(not searched yet)";

    static readonly System.Collections.Generic.Dictionary<string, string> _withheld = new();
    const string NoneGiven = "(none given)";

    static (string Value, string Level)? _persistedInstance;

    /// <summary>
    /// HOUSECARL_MO2_INSTANCE as an explicit choice: its process value, unless that value equals the one persisted at
    /// User or Machine level (then "" and the reason is kept for <see cref="Require"/>). The test seam
    /// HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE ("User=VALUE" or "Machine=VALUE") ADDS a persisted value without
    /// touching the registry; it can only cause a refusal, never suppress one.
    /// </summary>
    static string EnvInstance()
    {
        var v = Env("HOUSECARL_MO2_INSTANCE");
        if (v.Length == 0) return "";
        foreach (var (level, persisted) in PersistedInstanceValues())
            if (string.Equals(persisted.Trim(), v, StringComparison.OrdinalIgnoreCase))
            {
                _persistedInstance = (v, level);
                return "";
            }
        return v;
    }

    static System.Collections.Generic.IEnumerable<(string Level, string Value)> PersistedInstanceValues()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (level, target) in new[] { ("User", EnvironmentVariableTarget.User), ("Machine", EnvironmentVariableTarget.Machine) })
            {
                string? pv = null;
                try { pv = Environment.GetEnvironmentVariable("HOUSECARL_MO2_INSTANCE", target); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"warning: could not read the {level}-level HOUSECARL_MO2_INSTANCE ({ex.GetType().Name}); " +
                                            "the persisted-value check covers the other level only.");
                }
                if (!string.IsNullOrWhiteSpace(pv)) yield return (level, pv!);
            }
        }
        var seam = Env("HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE");
        var eq = seam.IndexOf('=');
        if (eq > 0) yield return (seam[..eq], seam[(eq + 1)..]);
    }

    static string Env(string envVar)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(v) ? "" : v!.Trim();
    }

    static string ConfigString(string key)
    {
        LoadConfig();
        if (_config is { } root && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Trim();
        return "";
    }

    /// <summary>
    /// The instance this run names explicitly: --instance on the command line (read here because the getters run
    /// before the probe parses its own flags), else HOUSECARL_MO2_INSTANCE, else "".
    /// </summary>
    static string ExplicitInstance()
    {
        var argv = Environment.GetCommandLineArgs();
        for (int i = 1; i < argv.Length; i++)
        {
            if (argv[i] == "--instance" && i + 1 < argv.Length) return argv[i + 1].Trim();
            if (argv[i].StartsWith("--instance=", StringComparison.Ordinal)) return argv[i]["--instance=".Length..].Trim();
        }
        return EnvInstance();
    }

    static string NormalisePath(string p)
    {
        try { p = Path.GetFullPath(p.Trim()); } catch (Exception) { p = p.Trim(); }
        return p.Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>
    /// For a value that describes one instance (game, profile): the environment, else the config file's value but
    /// only when the run's explicit instance is the config's instance. Otherwise "" with the key recorded as withheld,
    /// so <see cref="Require"/> names why instead of printing the generic "not configured".
    /// </summary>
    static string GetForInstance(string key, string envVar)
    {
        var fromEnv = Env(envVar);
        if (fromEnv.Length > 0) return fromEnv;
        var fromConfig = ConfigString(key);
        if (fromConfig.Length == 0) return "";
        var cfgInstance = ConfigString("instance");
        var explicitInstance = ExplicitInstance();
        if (explicitInstance.Length > 0 && cfgInstance.Length > 0
            && string.Equals(NormalisePath(explicitInstance), NormalisePath(cfgInstance), StringComparison.OrdinalIgnoreCase))
            return fromConfig;
        _withheld[key] = explicitInstance.Length > 0 ? explicitInstance : NoneGiven;
        return "";
    }

    static string Get(string key, string envVar)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv!.Trim();

        LoadConfig();
        if (_config is { } root
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
        }
        return "";
    }

    static string EnvName(string key) => key switch
    {
        "instance" => "HOUSECARL_MO2_INSTANCE",
        "profile" => "HOUSECARL_MO2_PROFILE",
        "game" => "HOUSECARL_GAME_PATH",
        "downloadPool" => "HOUSECARL_DOWNLOAD_POOL",
        "sevenZip" => "HOUSECARL_SEVENZIP",
        _ => "HOUSECARL_" + key.ToUpperInvariant(),
    };

    /// <summary>Forget the loaded config, so the next read takes the environment and the config file afresh.</summary>
    internal static void Reload() { _configLoaded = false; _config = null; }

    static void LoadConfig()
    {
        if (_configLoaded) return;
        _configLoaded = true;

        // HOUSECARL_HARNESS_CONFIG names the file outright, and is the way to keep it OUTSIDE this
        // repo entirely. That matters: this repo is the TOOL and is meant to be shared, while a
        // config naming your MO2 instance, profile and drive layout is a fact about your own mod
        // setup and belongs with it. The upward walk below is only a convenience for a checkout
        // being used on its own.
        var explicitPath = Environment.GetEnvironmentVariable("HOUSECARL_HARNESS_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            _searched = explicitPath!;
            if (!File.Exists(explicitPath))
            {
                // Named but absent is an error, never a silent fall-through to the walk: the user
                // believes they pointed at a file.
                Console.Error.WriteLine($"HOUSECARL_HARNESS_CONFIG points at {explicitPath}, which does not exist.");
                Environment.Exit(2);
            }
            ReadConfigFile(explicitPath!);
            return;
        }

        // Walk up from the running binary: a probe executes out of
        // <probe>\bin\<cfg>\<tfm>\, so the harness root is a few levels above.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var looked = new System.Collections.Generic.List<string>();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ConfigName);
            looked.Add(dir.FullName);
            if (File.Exists(candidate))
            {
                _searched = candidate;
                ReadConfigFile(candidate);
                return;
            }
            dir = dir.Parent;
        }
        _searched = string.Join(", ", looked);
    }

    static void ReadConfigFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _config = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            // A malformed config is a real error, not a silent fallback to "unset": the user
            // believes they configured this. Windows paths are the usual cause — a lone backslash
            // is an invalid JSON escape, so say so rather than just quoting the parser.
            Console.Error.WriteLine($"{path} could not be read as JSON — {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(@"  (Windows paths: use forward slashes, ""C:/Games/...""; a single \ is an invalid JSON escape.)");
            Environment.Exit(2);
        }
    }

    static string SearchedDescription()
    {
        LoadConfig();
        return _searched;
    }
}
