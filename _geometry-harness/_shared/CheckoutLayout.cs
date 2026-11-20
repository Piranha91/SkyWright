namespace HarnessShared;

/// <summary>Where a SkyWright checkout keeps what the app, runcontrol and their tests take from it: the pinned houseCARL
/// (external/houseCARL, whose server writes patches and whose generator makes the rulebook) and the app data (data/).</summary>
internal static class CheckoutLayout
{
    /// <summary>The file that marks a SkyWright checkout's root.</summary>
    public const string Marker = "SkyWright.sln";

    /// <summary>The SkyWright checkout above <paramref name="from"/> (default: this program's folder), or null.</summary>
    public static string? FindRoot(string? from = null)
    {
        var dir = new DirectoryInfo(from ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Marker))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>The pinned houseCARL checkout, the submodule.</summary>
    public static string Housecarl(string root) => Path.Combine(root, "external", "houseCARL");

    /// <summary>houseCARL's server as built in the submodule.</summary>
    public static string ServerExe(string root) =>
        Path.Combine(Housecarl(root), "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");

    /// <summary>The rulebook the generator writes into the submodule's generated/ (ignored by houseCARL's git).</summary>
    public static string Corpus(string root) => Path.Combine(Housecarl(root), "generated", "corpus.json");

    /// <summary>The check registry.</summary>
    public static string Registry(string root) => Path.Combine(root, "data", "check-registry.json");

    /// <summary>The severity table.</summary>
    public static string Severity(string root) => Path.Combine(root, "data", "workbench-severity.json");
}
