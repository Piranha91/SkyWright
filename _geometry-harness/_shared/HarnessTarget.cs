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
//  Unset reads back as "" rather than null so a probe can hold it in a
//  plain string, let a flag overwrite it, and only then Require() it.
// ======================================================================

internal static class HarnessTarget
{
    /// <summary>MO2 instance directory — the one holding `profiles\`, `mods\` and `overwrite\`.</summary>
    public static string Instance => Get("instance", "HOUSECARL_MO2_INSTANCE");

    /// <summary>MO2 profile name inside that instance.</summary>
    public static string Profile => Get("profile", "HOUSECARL_MO2_PROFILE");

    /// <summary>Game install directory — the one holding `Data\`.</summary>
    public static string Game => Get("game", "HOUSECARL_GAME_PATH");

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
