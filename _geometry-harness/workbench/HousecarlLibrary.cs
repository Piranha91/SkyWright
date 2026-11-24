using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;

namespace Workbench;

/// <summary>houseCARL's own tool code, compiled in from the pin: the server project's public tool methods called on a
/// <see cref="LoadOrderService"/> over one write target, with no houseCARL program started. Nothing here decides a
/// record, a lane or a refusal; each answer is the tool method's own text, as the server would have sent it.</summary>
internal sealed class HousecarlLibrary : IDisposable
{
    /// <summary>The tools the app calls, by the names their owner gives them, each to its public method.</summary>
    static readonly Dictionary<string, MethodInfo> Tools = new(StringComparer.Ordinal)
    {
        [ToolNames.Records] = typeof(RecordsTools).GetMethod(nameof(RecordsTools.Records))!,
        [ToolNames.Apply] = typeof(ApplyTools).GetMethod(nameof(ApplyTools.Apply))!,
        [ToolNames.Remove] = typeof(RemoveTools).GetMethod(nameof(RemoveTools.Remove))!,
        [ToolNames.Forward] = typeof(ForwardTools).GetMethod(nameof(ForwardTools.Forward))!,
        [ToolNames.AssetStatus] = typeof(AssetTools).GetMethod(nameof(AssetTools.AssetStatus))!,
    };

    static readonly object CorpusGate = new();
    static string? _corpus;

    readonly LoadOrderService _svc;
    // calls share it; Dispose takes it alone, so the service is never closed under a call, nor used after
    readonly ReaderWriterLockSlim _use = new();
    int _disposed;

    /// <summary>What answers the calls: the houseCARL assemblies compiled in, by their content (see <see cref="IdentityOf"/>).</summary>
    internal static string Identity { get; } = IdentityOf(typeof(LoadOrderService).Assembly.Location, typeof(WriteEngine).Assembly.Location);

    /// <summary>The houseCARL server assembly this process loaded.</summary>
    internal static string AssemblyPath => typeof(LoadOrderService).Assembly.Location;

    HousecarlLibrary(LoadOrderService svc) => _svc = svc;

    /// <summary>A service over the target's explicit Data, mods and profile folders (the server's explicit-paths mode,
    /// built as its own start-up builds it), keeping its store and spilled results in <paramref name="dataDir"/>. The
    /// rulebook is one per process: a second, different one is refused.</summary>
    internal static HousecarlLibrary Open(WriteTarget target, string dataDir, string corpusPath)
    {
        var corpus = Path.GetFullPath(corpusPath);
        lock (CorpusGate)
        {
            if (_corpus is not null && !string.Equals(_corpus, corpus, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"houseCARL's rulebook in this app is '{_corpus}', and a write asked for '{corpus}'; restart SkyWright to use another rulebook.");
            CorpusRulebook.CorpusPath = _corpus = corpus;
        }
        Directory.CreateDirectory(dataDir);
        var store = new UserConfigStore(Path.Combine(dataDir, "houseCARL.user.json"));
        return new HousecarlLibrary(LoadOrderService.WithExplicitPaths(target.DataDir, target.ModsDir, target.ProfileDir, 0, store));
    }

    /// <summary>One tool call, run off the calling thread so a window never waits on houseCARL.</summary>
    internal Task<string> CallAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        => Task.Run(() => Invoke(tool, args, ct), ct);

    /// <summary>The call itself: the tool's public method with the arguments bound to its parameters by name, as the
    /// server's MCP binding does (JSON to each parameter's type), and the tool's own text back. Refused on a window
    /// thread, and after <see cref="Dispose"/>.</summary>
    internal string Invoke(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        if (System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) is not null)
            throw new InvalidOperationException($"houseCARL's {tool} was called on a window's thread, where it would freeze the window; this is a fault in SkyWright, not in your load order.");
        if (!Tools.TryGetValue(tool, out var method))
            throw new InvalidOperationException($"the app called houseCARL's {tool}, which it does not call in process; this is a fault in SkyWright.");
        var ps = method.GetParameters();
        var unknown = args.Keys.Where(k => ps.All(p => p.Name != k)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"the app called houseCARL's {tool} with {string.Join(", ", unknown)}, which it does not take; this is a fault in SkyWright.");
        var values = ps.Select(p =>
        {
            if (p.ParameterType == typeof(LoadOrderService)) return _svc;
            if (p.ParameterType == typeof(CancellationToken)) return ct;
            if (!args.TryGetValue(p.Name!, out var v) || v is null) return p.HasDefaultValue ? p.DefaultValue : null;
            try { return JsonSerializer.SerializeToElement(v, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Deserialize(p.ParameterType, ModelContextProtocol.McpJsonUtilities.DefaultOptions); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            { throw new InvalidOperationException($"the app could not give houseCARL's {tool} its {p.Name} ({ex.Message}); this is a fault in SkyWright."); }
        }).ToArray();
        _use.EnterReadLock();
        try
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(HousecarlLibrary), $"houseCARL's {tool} was called after its profile was closed; open the profile again.");
            return (string)method.Invoke(null, values)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException); throw; }
        finally { _use.ExitReadLock(); }
    }

    /// <summary>A fingerprint of houseCARL's code: the SHA-256 of each assembly file, in order, hashed together. It changes
    /// whenever either assembly's bytes change, so a decision or a bundle keyed on it sees a new houseCARL build.</summary>
    internal static string IdentityOf(params string[] assemblies)
    {
        var parts = assemblies.Select(a => Path.GetFileName(a) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))).ToLowerInvariant());
        return "in-process houseCARL " + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant()[..16]
               + " (" + string.Join(", ", parts.Select(p => p[..(p.IndexOf(':') + 13)])) + ")";
    }

    /// <summary>Waits for the calls under way, then closes the service; a later call is refused in one sentence.</summary>
    public void Dispose()
    {
        _use.EnterWriteLock();
        try { if (_disposed != 0) return; _disposed = 1; _svc.Dispose(); }
        finally { _use.ExitWriteLock(); }
    }
}
