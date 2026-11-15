using System.Text.Json;
using HousecarlCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Workbench;

/// <summary>How the workbench endpoint treats each houseCARL tool it forwards to the server bound to the launcher's
/// profile (DESIGN R2.7, R4.1). The instance setters are not forwarded at all.</summary>
internal enum ForwardKind { Read, RecordWrite, Decompile, NotYet }

internal static class ForwardList
{
    internal static readonly IReadOnlyDictionary<string, ForwardKind> Tools = new Dictionary<string, ForwardKind>
    {
        ["housecarl_records"] = ForwardKind.Read, ["housecarl_check"] = ForwardKind.Read, ["housecarl_asset_status"] = ForwardKind.Read,
        ["housecarl_skse"] = ForwardKind.Read, ["housecarl_skypatcher_layer"] = ForwardKind.Read, ["housecarl_nif_inspect"] = ForwardKind.Read,
        ["housecarl_bsa_list"] = ForwardKind.Read, ["housecarl_update_status"] = ForwardKind.Read, ["housecarl_load_order_status"] = ForwardKind.Read,
        ["housecarl_nexus_search"] = ForwardKind.Read, ["housecarl_nexus_mod"] = ForwardKind.Read, ["housecarl_nexus_file_search"] = ForwardKind.Read,
        ["housecarl_nexus_identify"] = ForwardKind.Read, ["housecarl_nexus_check_updates"] = ForwardKind.Read, ["housecarl_nexus_graphql"] = ForwardKind.Read,
        ["housecarl_nexus_catalog"] = ForwardKind.Read,
        ["housecarl_apply"] = ForwardKind.RecordWrite, ["housecarl_remove"] = ForwardKind.RecordWrite, ["housecarl_forward"] = ForwardKind.RecordWrite,
        ["housecarl_copy"] = ForwardKind.RecordWrite, ["housecarl_create"] = ForwardKind.RecordWrite,
        ["housecarl_decompile_script"] = ForwardKind.Decompile,
        ["housecarl_create_plugin"] = ForwardKind.NotYet, ["housecarl_place"] = ForwardKind.NotYet, ["housecarl_nif_set"] = ForwardKind.NotYet,
        ["housecarl_compile_script"] = ForwardKind.NotYet, ["housecarl_bsa_extract"] = ForwardKind.NotYet, ["housecarl_bsa_repack"] = ForwardKind.NotYet,
        ["housecarl_write_seq"] = ForwardKind.NotYet, ["housecarl_compact_plugin"] = ForwardKind.NotYet, ["housecarl_merge_plugins"] = ForwardKind.NotYet,
    };

    /// <summary>The tools that change which instance the server reads, refused by name (they persist houseCARL.user.json,
    /// which beats explicit paths).</summary>
    internal static readonly string[] NeverForwarded = { "housecarl_set_mo2_instance", "housecarl_set_tool_path" };
}

/// <summary>The manual-mode write rule for a forwarded call (DESIGN R4.1): every write lands under the Output Folder O or
/// is refused in one sentence. Returns the arguments to send, or a refusal.</summary>
internal static class ForwardRule
{
    static bool Truthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => e.GetString() is { Length: > 0 } s && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        _ => true,
    };

    static string? Text(IReadOnlyDictionary<string, JsonElement> a, string k) =>
        a.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    internal static (Dictionary<string, JsonElement>? Args, string? Refusal) Prepare(string tool, IReadOnlyDictionary<string, JsonElement>? given,
                                                                                    string output, Mo2OutputLayout layout)
    {
        var a = new Dictionary<string, JsonElement>(given ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
        if (!ForwardList.Tools.TryGetValue(tool, out var kind))
            return (null, $"{tool} is not forwarded by the workbench in manual mode.");
        var o = OutputFolderPolicy.Full(output);
        bool modsIsOutput = string.Equals(o, OutputFolderPolicy.Full(layout.Mods), StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case ForwardKind.Read:
                return (a, null);
            case ForwardKind.NotYet:
                return (null, $"{tool} writes into the instance's mods folder and takes no output folder yet, so in manual mode it is not available; use housecarl_apply, which writes under the Output Folder.");
            case ForwardKind.Decompile:
            {
                var op = Text(a, "out_path");
                if (op is null)
                    return (null, $"in manual mode {tool} needs out_path, a folder under the Output Folder '{o}', because its other lanes write into the instance's mods folder.");
                if (!Path.IsPathFullyQualified(op) || !OutputFolderPolicy.AtOrUnder(OutputFolderPolicy.Full(op), o))
                    return (null, $"out_path '{op}' is not the Output Folder '{o}' or a folder under it; in manual mode every write goes under the Output Folder.");
                return (a, null);
            }
            default: // RecordWrite
            {
                if (a.TryGetValue("in_place", out var ip) && Truthy(ip))
                    return (null, $"in_place is refused in manual mode: an in-place edit writes a plugin the profile loads, and every write goes under the Output Folder '{o}'.");
                var into = Text(a, "into");
                if (into is not null)
                {
                    if (Path.IsPathFullyQualified(into))
                    {
                        if (!OutputFolderPolicy.AtOrUnder(OutputFolderPolicy.Full(into), o))
                            return (null, $"into '{into}' is not under the Output Folder '{o}'; in manual mode a patch is extended only where the Output Folder holds it.");
                        return (a, null);
                    }
                    if (!modsIsOutput)
                        return (null, $"into '{into}' names a patch in the instance's mods folder, which is not the Output Folder '{o}'; give into= the patch's folder under the Output Folder by its full path.");
                    return (a, null);
                }
                var outPath = Text(a, "out_path");
                if (outPath is not null)
                {
                    if (!Path.IsPathFullyQualified(outPath) || !OutputFolderPolicy.AtOrUnder(OutputFolderPolicy.Full(outPath), o))
                        return (null, $"out_path '{outPath}' is not the Output Folder '{o}' or a folder under it; in manual mode every write goes under the Output Folder.");
                    if (OutputFolderPolicy.Refusal(outPath, layout) is { } why) return (null, why);
                    return (a, null);
                }
                if (tool != "housecarl_apply")
                    return (null, $"in manual mode {tool} writes only into a patch under the Output Folder '{o}': give into= that patch's folder by its full path.");
                a["out_path"] = JsonSerializer.SerializeToElement(o);
                return (a, null);
            }
        }
    }
}

/// <summary>The forwarded tools' definitions as the houseCARL server publishes them, read once per process from the
/// server exe (it is started with an empty data folder of its own in the system temp folder and asked for its tools).</summary>
internal static class ForwardCatalog
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Dictionary<string, Tool>? _tools;
    internal static string? Problem { get; private set; }

    internal static async Task<IReadOnlyDictionary<string, Tool>> LoadAsync(string serverExe)
    {
        await Gate.WaitAsync();
        try
        {
            if (_tools is not null) return _tools;
            _tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
            if (!File.Exists(serverExe)) { Problem = $"the houseCARL server is not built at '{serverExe}'"; return _tools; }
            var data = Path.Combine(Path.GetTempPath(), "hc-wb-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var c = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "housecarl", Command = serverExe, Arguments = Array.Empty<string>(),
                    EnvironmentVariables = new Dictionary<string, string?> { ["HOUSECARL_DATA_DIR"] = data, ["HOUSECARL_RUNLOG_DIR"] = Path.Combine(data, "runs") },
                }), cancellationToken: cts.Token);
                foreach (var t in await c.ListToolsAsync(cancellationToken: cts.Token))
                    if (ForwardList.Tools.ContainsKey(t.Name)) _tools[t.Name] = t.ProtocolTool;
            }
            catch (Exception ex) { Problem = $"the houseCARL server at '{serverExe}' did not list its tools ({ex.Message})"; }
            finally { try { Directory.Delete(data, true); } catch { } }
            return _tools;
        }
        finally { Gate.Release(); }
    }
}

/// <summary>One forwarded houseCARL tool on the workbench endpoint: the server's own definition, and a call that applies
/// <see cref="ForwardRule"/> and then goes to the server the launcher bound to its profile.</summary>
internal sealed class ForwardedTool : McpServerTool
{
    readonly Tool _tool;
    internal ForwardedTool(Tool tool) { _tool = tool; }
    public override Tool ProtocolTool => _tool;
    public override IReadOnlyList<object> Metadata => Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var hub = request.Services?.GetService<StateHub>();
        if (hub is null) return StateTools.Refuse("the workbench endpoint has no launcher to forward to.");
        try { return await hub.Launcher.ForwardAsync(_tool.Name, request.Params?.Arguments, cancellationToken); }
        catch (InvalidOperationException ex) { return StateTools.Refuse(ex.Message); }
    }

    internal static async Task<IReadOnlyList<McpServerTool>> AllAsync(string serverExe)
    {
        var defs = await ForwardCatalog.LoadAsync(serverExe);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true });
        return ForwardList.Tools.Keys.Select(n => (McpServerTool)new ForwardedTool(defs.TryGetValue(n, out var t) ? t : new Tool
        {
            Name = n, InputSchema = schema,
            Description = $"Forwarded to the houseCARL server bound to the workbench launcher's profile; its own definition could not be read: {ForwardCatalog.Problem}.",
        })).ToList();
    }
}
