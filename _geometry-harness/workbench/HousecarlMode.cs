using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>How the client starts its houseCARL server: the command, its arguments and its environment.</summary>
internal sealed record ServerLaunch(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?> Env, string From);

/// <summary>houseCARL mode: the instance is whatever the client's own houseCARL server manages. The
/// workbench starts that server exactly as the client does (command, arguments and environment), so the server applies
/// its own precedence (saved config in HOUSECARL_DATA_DIR or beside the exe, then the install default), and reads the
/// instance from the server's answer; it never reads houseCARL.user.json itself.</summary>
internal static class HousecarlMode
{
    /// <summary>The client's launch, in order: WB_HOUSECARL_LAUNCH (a JSON file {command, args, env}); then the
    /// housecarl entry of ~/.claude.json, the project's (for <paramref name="workingDir"/>) before the user-level one.
    /// A plugin-installed server's .mcp.json fills ${user_config.*} from settings this build does not read, so it is
    /// refused naming WB_HOUSECARL_LAUNCH rather than started with a guessed environment.</summary>
    internal static ServerLaunch Locate(string? launchFile, string claudeJson, string workingDir)
    {
        if (!string.IsNullOrWhiteSpace(launchFile))
        {
            if (!File.Exists(launchFile)) throw new InvalidOperationException($"WB_HOUSECARL_LAUNCH names '{launchFile}', which does not exist; point it at a JSON file {{command, args, env}}.");
            using var d = JsonDocument.Parse(File.ReadAllText(launchFile));
            return FromEntry(d.RootElement, "WB_HOUSECARL_LAUNCH " + launchFile);
        }
        if (File.Exists(claudeJson))
        {
            using var d = JsonDocument.Parse(File.ReadAllText(claudeJson));
            var r = d.RootElement;
            if (r.TryGetProperty("projects", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var p in ps.EnumerateObject())
                    if (SameDir(p.Name, workingDir) && p.Value.TryGetProperty("mcpServers", out var ms) && ms.TryGetProperty("housecarl", out var e))
                        return FromEntry(e, $"{claudeJson} (project {p.Name})");
            if (r.TryGetProperty("mcpServers", out var um) && um.TryGetProperty("housecarl", out var ue))
                return FromEntry(ue, $"{claudeJson} (user)");
        }
        throw new InvalidOperationException("houseCARL mode needs the houseCARL server your client runs, and none was found in ~/.claude.json; set WB_HOUSECARL_LAUNCH to a JSON file {command, args, env} that starts it as your client does (a plugin-installed houseCARL's .mcp.json included), or use free mode.");
    }

    static bool SameDir(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static ServerLaunch FromEntry(JsonElement e, string from)
    {
        var cmd = e.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(cmd)) throw new InvalidOperationException($"the houseCARL launch in {from} names no command; give its command, args and env.");
        var args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("env", out var en) && en.ValueKind == JsonValueKind.Object)
            foreach (var kv in en.EnumerateObject()) env[kv.Name] = kv.Value.GetString();
        foreach (var t in args.Concat(env.Values.OfType<string>()).Append(cmd))
            if (t.Contains("${"))
                throw new InvalidOperationException($"the houseCARL launch in {from} carries '{t}', a placeholder its client fills from settings this build does not read; give WB_HOUSECARL_LAUNCH with the filled values.");
        return new ServerLaunch(cmd!, args, env, from);
    }

    /// <summary>Start the server as the client does and read the instance it manages from its own load-order status
    /// (the `instance:` line its Render writes).</summary>
    internal static async Task<string> ReportedInstanceAsync(ServerLaunch launch, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        await using var c = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "housecarl", Command = launch.Command, Arguments = launch.Args.ToArray(), EnvironmentVariables = launch.Env.ToDictionary(kv => kv.Key, kv => kv.Value),
        }), cancellationToken: cts.Token);
        var r = await c.CallToolAsync("housecarl_load_order_status", new Dictionary<string, object?>(), cancellationToken: cts.Token);
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text));
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("instance:", StringComparison.Ordinal));
        var dir = line?["instance:".Length..].Trim();
        if (r.IsError == true || string.IsNullOrWhiteSpace(dir) || dir.StartsWith("explicit-paths", StringComparison.Ordinal) || !Directory.Exists(dir))
            throw new InvalidOperationException($"the houseCARL server your client runs ({launch.From}) manages no MO2 instance it reports, so houseCARL mode has nothing to open; set its instance in your client, or use free mode.");
        return dir;
    }
}
