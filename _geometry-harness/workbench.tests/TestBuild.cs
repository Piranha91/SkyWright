namespace WorkbenchTests;

/// <summary>The SkyWright build these tests run from (they build into its Tests\workbench.tests\): its programs and
/// pieces, found the way every program finds them (HarnessShared.CheckoutLayout).</summary>
internal static class TestBuild
{
    internal static HarnessShared.CheckoutLayout.Layout Layout =>
        HarnessShared.CheckoutLayout.Locate(null, out var why) ?? throw new InvalidOperationException(why + ".");

    /// <summary>houseCARL's server in the build (Tools\housecarl-mcp).</summary>
    internal static string Server => Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Server);

    /// <summary>runcontrol in the build (Tools\runcontrol).</summary>
    internal static string Runcontrol => Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Runcontrol);

    /// <summary>A probe (or conflictdiff) in the build (Probes\name).</summary>
    internal static string Program(string name) => Layout.Program(name);
}
