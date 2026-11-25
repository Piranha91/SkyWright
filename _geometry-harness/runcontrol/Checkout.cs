namespace RunControl;

/// <summary>The SkyWright build this program sits in (bin\\<Configuration>\\<.NET version>), and the tools and
/// data the stages take from it. Where each piece is belongs to <see cref="HarnessShared.CheckoutLayout"/>; this class
/// only asks it. runcontrol's own flags (--registry, --harness, --reader-cmd) are its overrides.</summary>
public static class Checkout
{
    static HarnessShared.CheckoutLayout.Layout? _layout;

    /// <summary>The build this program sits in (Tools\\runcontrol, two folders below the build's root).</summary>
    internal static HarnessShared.CheckoutLayout.Layout Layout => _layout ??= Find();

    /// <summary>The build's root: bin\\<Configuration>\\<.NET version>.</summary>
    public static string Root => PyPath.Resolve(Layout.Root);

    public static string Harness => PyPath.Resolve(Layout.Harness);
    public static string ScopeprobeExe => Program("scopeprobe");
    public static string Registry => PyPath.Resolve(Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Registry));
    public static string Corpus => PyPath.Resolve(Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Corpus));

    /// <summary>One of SkyWright's programs in this layout.</summary>
    public static string Program(string name) => PyPath.Str(HarnessShared.CheckoutLayout.ProgramExe(Harness, name));

    static HarnessShared.CheckoutLayout.Layout Find() =>
        HarnessShared.CheckoutLayout.Locate(null, out var why) ?? throw new Refusal(why + ".");
}
