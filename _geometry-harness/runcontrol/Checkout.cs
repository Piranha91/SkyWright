namespace RunControl;

/// <summary>The checkout this build sits in, and the tools and data the stages take from it.</summary>
public static class Checkout
{
    static string? _root;

    /// <summary>The SkyWright checkout (the folder holding SkyWright.sln), above this program's own folder.</summary>
    public static string Root => _root ??= Find();

    public static string Harness => PyPath.Join(Root, "_geometry-harness");
    public static string ScopeprobeExe => PyPath.Join(Harness, "scopeprobe", "bin", "Release", "net9.0", "scopeprobe.exe");
    public static string Registry => PyPath.Resolve(HarnessShared.CheckoutLayout.Registry(Root));
    public static string Corpus => PyPath.Resolve(HarnessShared.CheckoutLayout.Corpus(Root));

    static string Find()
    {
        string? dir = HarnessShared.CheckoutLayout.FindRoot();
        if (dir == null)
            throw new Refusal($"runcontrol is not inside a SkyWright checkout (no {HarnessShared.CheckoutLayout.Marker} above {PyOrder.Fwd(AppContext.BaseDirectory)}); "
                              + "build it in the checkout whose probes it should run.");
        return PyPath.Resolve(dir);
    }
}
