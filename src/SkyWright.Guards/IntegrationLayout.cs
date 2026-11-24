using System.Reflection;
using System.Text.Json.Nodes;

namespace SkyWright.Guards;

/// <summary>Where the houseCARL integration module's programs are built, for the guards alone. The module builds to its
/// own folder per configuration (Directory.Build.props, which also names that folder to this assembly as metadata), with
/// the marker <c>housecarl-integration-layout.json</c> naming where each program is. Nothing outside the guards finds the
/// module's programs; CheckoutLayout, compiled into the app and the probes, has no way to.</summary>
internal static class IntegrationLayout
{
    static string? Meta(string key) =>
        typeof(IntegrationLayout).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>The module's build folder for this guard's configuration, under the checkout <paramref name="repo"/>.</summary>
    public static string Root(string repo) =>
        Path.GetFullPath(Path.Combine(repo, Meta("HousecarlIntegrationBuild") ?? throw new InvalidOperationException(
            "this guard was built without the module's build folder (Directory.Build.props names it); build the solution again.")));

    /// <summary>A module program's exe in that folder, where its marker says; a folder without a readable marker gives a
    /// path into a folder named for that fact, so the run's "not built" line says why.</summary>
    public static string ProgramExe(string root, string name)
    {
        var marker = Path.Combine(root, Meta("HousecarlIntegrationMarker") ?? "housecarl-integration-layout.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(marker)) is JsonObject o && (string?)o["kind"] == "housecarl-integration-build")
            {
                var tools = (o["tools"] as JsonArray)?.Select(n => (string?)n ?? "").ToList() ?? new List<string>();
                var template = (string?)(tools.Contains(name, StringComparer.OrdinalIgnoreCase) ? o["tool"] : o["program"]) ?? "";
                if (template.Length > 0) return Path.Combine(root, template.Replace("{name}", name).Replace('/', Path.DirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { /* named below */ }
        return Path.Combine(root, "(no houseCARL integration build here; run scripts\\build.ps1)", name + ".exe");
    }
}
