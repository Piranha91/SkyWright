namespace WorkbenchTests;

/// <summary>The SkyWright build these tests run from (they build into its Tests\workbench.tests\): its programs and
/// pieces, found the way every program finds them (HarnessShared.CheckoutLayout).</summary>
internal static class TestBuild
{
    internal static HarnessShared.CheckoutLayout.Layout Layout =>
        HarnessShared.CheckoutLayout.Locate(null, out var why) ?? throw new InvalidOperationException(why + ".");

    /// <summary>houseCARL's server program where the pin's own project builds it, for the tests that start it as an AI
    /// client does (H5a, H5b). The tests' own entry: Directory.Build.props gives this path to workbench.tests alone
    /// (assembly metadata HousecarlServerProgram); the build holds no houseCARL program.</summary>
    internal static string Server =>
        typeof(TestBuild).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "HousecarlServerProgram")?.Value
        ?? throw new InvalidOperationException("workbench.tests was built without the HousecarlServerProgram entry; build it through Directory.Build.props (scripts/build.ps1).");

    /// <summary>runcontrol in the build (Tools\runcontrol).</summary>
    internal static string Runcontrol => Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Runcontrol);

    /// <summary>A probe (or conflictdiff) in the build (Probes\name).</summary>
    internal static string Program(string name) => Layout.Program(name);
}
