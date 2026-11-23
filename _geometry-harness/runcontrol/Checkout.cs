namespace RunControl;

/// <summary>The SkyWright layout this program sits in (an install, or the checkout it was built in), and the tools and
/// data the stages take from it. Where each piece is belongs to <see cref="HarnessShared.CheckoutLayout"/>; this class
/// only asks it. runcontrol's own flags (--registry, --harness, --reader-cmd) are its overrides.</summary>
public static class Checkout
{
    static HarnessShared.CheckoutLayout.Layout? _layout;

    /// <summary>The layout: the install beside this program's folder, else the checkout above it.</summary>
    internal static HarnessShared.CheckoutLayout.Layout Layout => _layout ??= Find();

    /// <summary>The layout's root: the install root, or the folder holding SkyWright.sln.</summary>
    public static string Root => PyPath.Resolve(Layout.Root);

    public static string Harness => PyPath.Resolve(Layout.Harness);
    public static string ScopeprobeExe => Program("scopeprobe");
    public static string Registry => PyPath.Resolve(Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Registry));
    public static string Corpus => PyPath.Resolve(Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Corpus));

    /// <summary>One of SkyWright's programs in this layout.</summary>
    public static string Program(string name) => PyPath.Str(HarnessShared.CheckoutLayout.ProgramExe(Harness, name));

    /// <summary>A Python script of the checkout's harness; an install carries no Python, so there it is refused.</summary>
    public static string Script(string name, string step) => Layout.Installed
        ? throw new Refusal($"{step} is a Python step that runs only from a SkyWright checkout, and this runcontrol is installed at {PyOrder.Fwd(Layout.Root)}; run it from a checkout's runcontrol.")
        : PyPath.Join(Harness, name);

    static HarnessShared.CheckoutLayout.Layout Find() =>
        HarnessShared.CheckoutLayout.Locate(null, out var why) ?? throw new Refusal(why + "; run the runcontrol of an install or of the checkout whose probes it should run.");
}
