using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>Where the houseCARL server reads and writes: an explicit game Data folder, MO2 mods folder and profile
/// folder (the server's explicit-paths mode), so the profile is never whatever MO2 last had selected.</summary>
internal sealed record WriteTarget(string Instance, string Profile, string DataDir)
{
    internal string ModsDir => Path.Combine(Instance, "mods");
    internal string ProfileDir => Path.Combine(Instance, "profiles", Profile);
    public override string ToString() => $"instance '{Instance}', profile '{Profile}'";
}

/// <summary>Writes the staged edits as placement overrides through the houseCARL server's own write path
/// (<c>housecarl_apply</c>; <c>housecarl_records</c> to check and read back). The app is an MCP client of the
/// <c>housecarl-mcp.exe</c> it was built with, started over stdio (ModelContextProtocol.Core 2.2.0:
/// <c>StdioClientTransport</c> + <c>McpClient.CreateAsync</c> + <c>CallToolAsync</c>).</summary>
internal sealed class PatchWriter : IAsyncDisposable
{
    readonly McpClient _client;
    internal WriteTarget Target { get; }
    internal string ServerExe { get; }
    internal string ServerSha256 { get; }
    internal Action<string> Log { get; }

    PatchWriter(McpClient client, WriteTarget target, string exe, string sha, Action<string> log)
    { _client = client; Target = target; ServerExe = exe; ServerSha256 = sha; Log = log; }

    /// <summary>Start the server at <paramref name="serverExe"/> against <paramref name="target"/>. Refused when the
    /// target's instance is not one of <paramref name="allowedInstances"/> (in development: the lab, and a test's own
    /// synthetic instance).</summary>
    internal static async Task<PatchWriter> StartAsync(string serverExe, WriteTarget target, IEnumerable<string> allowedInstances,
                                                       string dataDir, string corpusPath, Action<string> log, CancellationToken ct = default)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
        if (!allowedInstances.Any(a => string.Equals(Norm(a), Norm(target.Instance), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"writes to {target} are refused: only {string.Join(", ", allowedInstances.Select(a => $"'{a}'"))} may be written while the workbench is in development.");
        if (!File.Exists(serverExe))
            throw new InvalidOperationException($"the houseCARL server is not built at '{serverExe}'; build src/housecarl-mcp in this checkout first.");
        if (!File.Exists(corpusPath))
            throw new InvalidOperationException($"the houseCARL rulebook is not at '{corpusPath}'; run the generator (src/housecarl-generator) to make corpus.json.");
        if (!Directory.Exists(target.ProfileDir))
            throw new InvalidOperationException($"the profile folder '{target.ProfileDir}' does not exist; name a profile of that instance.");
        string sha;
        using (var fs = File.OpenRead(serverExe)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var dll = Path.ChangeExtension(serverExe, ".dll");
        string dllSha = File.Exists(dll) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant() : "(no dll)";
        Directory.CreateDirectory(dataDir);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "housecarl",
            Command = serverExe,
            Arguments = new[]
            {
                $"--HouseCarl:DataDir={target.DataDir}",
                $"--HouseCarl:ModsDir={target.ModsDir}",
                $"--HouseCarl:ProfileDir={target.ProfileDir}",
                $"--HouseCarl:CorpusPath={corpusPath}",
            },
            // A data folder of the workbench's own: no saved user config in it, so the explicit paths above are what
            // the server reads (a saved instance would otherwise win), and its run log stays beside it.
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HOUSECARL_DATA_DIR"] = dataDir,
                ["HOUSECARL_RUNLOG_DIR"] = Path.Combine(dataDir, "runs"),
            },
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        log($"houseCARL server: {serverExe} (exe sha256 {sha}, dll sha256 {dllSha}); writes go to {target}");
        return new PatchWriter(client, target, serverExe, sha, log);
    }

    internal async Task<string> CallAsync(string tool, Dictionary<string, object?> args, CancellationToken ct = default)
    {
        var r = await _client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (r.IsError == true) throw new InvalidOperationException($"{tool} refused: {text}");
        return text;
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V(HousecarlCore.Vec3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    /// <summary>The ops one commit writes: Placement.Position and Placement.Rotation per edited reference.</summary>
    internal static List<Dictionary<string, object?>> Ops(IEnumerable<PendingEdit> edits) =>
        edits.SelectMany(e => new[]
        {
            new Dictionary<string, object?> { ["formid"] = e.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = V(e.After.Position) },
            new Dictionary<string, object?> { ["formid"] = e.Ref.ToString(), ["field_path"] = "Placement.Rotation", ["value"] = V(e.After.Rotation) },
        }).ToList();

    /// <summary>The winner's Placement as the server reads it: (position, rotation), or a refusal.</summary>
    internal async Task<(HousecarlCore.Vec3 Pos, HousecarlCore.Vec3 Rot)> ReadPlacementAsync(string formKey, string? source = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["formids"] = new[] { formKey },
            ["project"] = new Dictionary<string, object?> { ["form"] = "fields", ["fields"] = new[] { "Placement" }, ["depth"] = 2 },
            ["format"] = "json",
        };
        if (source is not null) args["source"] = source;
        var text = await CallAsync("housecarl_records", args, ct);
        return ParsePlacement(text, formKey);
    }

    /// <summary>Pull Placement.Position and Placement.Rotation out of a records json answer.</summary>
    internal static (HousecarlCore.Vec3 Pos, HousecarlCore.Vec3 Rot) ParsePlacement(string json, string formKey)
    {
        HousecarlCore.Vec3? pos = null, rot = null;
        using var doc = JsonDocument.Parse(json);
        foreach (var el in Walk(doc.RootElement))
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("path", out var p) || !el.TryGetProperty("value", out var v)) continue;
            var path = p.GetString();
            if (path == "Placement.Position") pos = Parse(v);
            else if (path == "Placement.Rotation") rot = Parse(v);
        }
        if (pos is null || rot is null)
            throw new InvalidOperationException($"the server's answer for {formKey} carried no Placement.Position and Placement.Rotation; read it with housecarl_records to see why.");
        return (pos.Value, rot.Value);

        static HousecarlCore.Vec3 Parse(JsonElement v)
        {
            var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
            var parts = s.Trim('[', ']', '(', ')', ' ').Split(',');
            if (parts.Length != 3) throw new InvalidOperationException($"a placement value '{s}' is not three numbers.");
            float P(string x) => float.Parse(x.Trim(), CultureInfo.InvariantCulture);
            return new HousecarlCore.Vec3(P(parts[0]), P(parts[1]), P(parts[2]));
        }
        static IEnumerable<JsonElement> Walk(JsonElement e)
        {
            yield return e;
            if (e.ValueKind == JsonValueKind.Object) foreach (var c in e.EnumerateObject()) foreach (var x in Walk(c.Value)) yield return x;
            else if (e.ValueKind == JsonValueKind.Array) foreach (var c in e.EnumerateArray()) foreach (var x in Walk(c)) yield return x;
        }
    }

    /// <summary>Write (or dry-run) the net edits into one new patch, after checking each edit's <c>Before</c> is the
    /// winner the server reads. Returns the tool's own text.</summary>
    internal async Task<string> CommitAsync(IReadOnlyList<PendingEdit> net, string patchName, bool dryRun, string? into = null,
                                            float tolerance = 0.01f, CancellationToken ct = default)
    {
        if (net.Count == 0) throw new InvalidOperationException("there are no staged edits to write; move or rotate a reference first.");
        foreach (var e in net)
        {
            var (pos, rot) = await ReadPlacementAsync(e.Ref.ToString(), ct: ct);
            if (!Near(pos, e.Before.Position, tolerance) || !NearAngles(rot, e.Before.Rotation, 1e-4f))
                throw new InvalidOperationException($"{e.Ref}'s winning placement on the server ({V(pos)} / {V(rot)}) is not the one the scene drew "
                    + $"({V(e.Before.Position)} / {V(e.Before.Rotation)}); the scene and {Target} are not the same configuration.");
        }
        var args = new Dictionary<string, object?> { ["ops"] = Ops(net), ["dry_run"] = dryRun };
        if (into is not null) args["into"] = into; else args["patch"] = patchName;
        Log($"{(dryRun ? "dry run" : "WRITE")} of {net.Count} placement edit(s) to {Target}, {(into is not null ? $"into '{into}'" : $"new patch '{patchName}'")}");
        return await CallAsync("housecarl_apply", args, ct);
    }

    static bool Near(HousecarlCore.Vec3 a, HousecarlCore.Vec3 b, float t) =>
        MathF.Abs(a.X - b.X) <= t && MathF.Abs(a.Y - b.Y) <= t && MathF.Abs(a.Z - b.Z) <= t;
    static bool NearAngles(HousecarlCore.Vec3 a, HousecarlCore.Vec3 b, float t)
    {
        static float D(float x, float y) { var d = MathF.Abs(x - y) % (2 * MathF.PI); return MathF.Min(d, 2 * MathF.PI - d); }
        return D(a.X, b.X) <= t && D(a.Y, b.Y) <= t && D(a.Z, b.Z) <= t;
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
