namespace RunControl;

/// <summary>The checkout this build sits in, and the tools and data the stages take from it.</summary>
public static class Checkout
{
    static string? _root;

    /// <summary>The folder that holds housecarl.sln, above this program's own folder.</summary>
    public static string Root => _root ??= Find();

    public static string Harness => PyPath.Join(Root, "_geometry-harness");
    public static string ScopeprobeExe => PyPath.Join(Harness, "scopeprobe", "bin", "Release", "net9.0", "scopeprobe.exe");
    public static string Registry => PyPath.Join(Root, ".claude/skills/world-edit-compatibility/references/check-registry.json");
    public static string Corpus => PyPath.Join(Root, "generated", "corpus.json");

    static string Find()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "housecarl.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir == null)
            throw new Refusal($"runcontrol is not inside a houseCARL checkout (no housecarl.sln above {PyOrder.Fwd(AppContext.BaseDirectory)}); "
                              + "build it in the checkout whose probes it should run.");
        return PyPath.Resolve(dir);
    }
}
