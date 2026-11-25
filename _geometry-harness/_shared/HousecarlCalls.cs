using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;

namespace HarnessShared;

/// <summary>houseCARL's own tool code, compiled in from the pin (the app's writes and reads, runcontrol's names read):
/// the server project's public tool methods called on a <see cref="LoadOrderService"/>, with no houseCARL program
/// started. Nothing here decides a record, a lane or a refusal; each answer is the tool method's own text, as the
/// server would have sent it.</summary>
public static class HousecarlCalls
{
    /// <summary>The tools called in process, by the names their owner gives them, each to its public method.</summary>
    static readonly Dictionary<string, MethodInfo> Tools = new(StringComparer.Ordinal)
    {
        [ToolNames.Records] = typeof(RecordsTools).GetMethod(nameof(RecordsTools.Records))!,
        [ToolNames.Apply] = typeof(ApplyTools).GetMethod(nameof(ApplyTools.Apply))!,
        [ToolNames.Remove] = typeof(RemoveTools).GetMethod(nameof(RemoveTools.Remove))!,
        [ToolNames.Forward] = typeof(ForwardTools).GetMethod(nameof(ForwardTools.Forward))!,
        [ToolNames.AssetStatus] = typeof(AssetTools).GetMethod(nameof(AssetTools.AssetStatus))!,
    };

    static readonly Lazy<string> _identity = new(() => IdentityOf(typeof(LoadOrderService).Assembly.Location, typeof(WriteEngine).Assembly.Location));

    /// <summary>What answers the calls: houseCARL's server and core assemblies as this process loaded them, by content;
    /// one sentence when either is not a file this program can read.</summary>
    public static string Identity => _identity.Value;

    /// <summary>The houseCARL server assembly this process loaded.</summary>
    public static string AssemblyPath => typeof(LoadOrderService).Assembly.Location;

    /// <summary>A service over explicit Data, mods and profile folders (the server's explicit-paths mode, built as its own
    /// start-up builds it), its store and spilled results in <paramref name="dataDir"/>. The rulebook is one per
    /// process (<see cref="HousecarlRulebook.Use"/>): a second, different one is refused.</summary>
    public static LoadOrderService Open(string gameData, string modsDir, string profileDir, string dataDir, string corpusPath)
    {
        HousecarlRulebook.Use(corpusPath);
        Directory.CreateDirectory(dataDir);
        var store = new UserConfigStore(Path.Combine(dataDir, "houseCARL.user.json"));
        return LoadOrderService.WithExplicitPaths(gameData, modsDir, profileDir, 0, store);
    }

    /// <summary>One call: the tool's public method with the arguments bound to its parameters by name, as the server's
    /// MCP binding does (JSON to each parameter's type), and the tool's own text back.</summary>
    public static string Invoke(LoadOrderService svc, string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        if (!Tools.TryGetValue(tool, out var method))
            throw new InvalidOperationException($"houseCARL's {tool} is not one this program calls in process; this is a fault in SkyWright.");
        var ps = method.GetParameters();
        var unknown = args.Keys.Where(k => ps.All(p => p.Name != k)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"houseCARL's {tool} was called with {string.Join(", ", unknown)}, which it does not take; this is a fault in SkyWright.");
        // the app never edits a plugin's own file: a patch is always a new one (houseCARL's server refuses a bare in_place too)
        if (args.ContainsKey("in_place") || args.ContainsKey("acknowledge"))
            throw new InvalidOperationException($"houseCARL's {tool} was asked to edit a plugin in place, which SkyWright never does; this is a fault in SkyWright.");
        // a parameter with no default must be given: never passed on as null
        var missing = ps.Where(p => p.ParameterType != typeof(LoadOrderService) && p.ParameterType != typeof(CancellationToken) && !p.HasDefaultValue
                                    && (!args.TryGetValue(p.Name!, out var v) || v is null || v is JsonElement { ValueKind: JsonValueKind.Null })).Select(p => p.Name).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"houseCARL's {tool} needs {string.Join(", ", missing)}, which the call did not give; this is a fault in SkyWright.");
        var options = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        var values = ps.Select(p =>
        {
            if (p.ParameterType == typeof(LoadOrderService)) return svc;
            if (p.ParameterType == typeof(CancellationToken)) return ct;
            if (!args.TryGetValue(p.Name!, out var v) || v is null || v is JsonElement { ValueKind: JsonValueKind.Null })
                return p.HasDefaultValue ? p.DefaultValue : null;
            try { return JsonSerializer.SerializeToElement(v, options).Deserialize(p.ParameterType, options); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            { throw new InvalidOperationException($"houseCARL's {tool} could not be given its {p.Name} ({OneLine(ex.Message)}); this is a fault in SkyWright."); }
        }).ToArray();
        try { return (string)method.Invoke(null, values)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException); throw; }
    }

    /// <summary>A fingerprint of houseCARL's code: the SHA-256 of each assembly file's bytes, hashed together in order
    /// (the files' names are shown, not hashed). It changes whenever either assembly's bytes change, so a decision or a
    /// bundle keyed on it sees a new houseCARL build.</summary>
    public static string IdentityOf(params string[] assemblies)
    {
        if (assemblies.FirstOrDefault(a => string.IsNullOrEmpty(a) || !File.Exists(a)) is { } bad)
            throw new InvalidOperationException($"houseCARL's code is not a file this program can read ('{bad}'), so what reads and writes cannot be named; reinstall SkyWright.");
        var hashes = assemblies.Select(a => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))).ToLowerInvariant()).ToList();
        var whole = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", hashes)))).ToLowerInvariant();
        return $"in-process houseCARL {whole[..16]} ({string.Join(", ", assemblies.Select((a, i) => $"{Path.GetFileName(a)} {hashes[i][..12]}"))})";
    }

    static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " ").Trim().TrimEnd('.');

}
