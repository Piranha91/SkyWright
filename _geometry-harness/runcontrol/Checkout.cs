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

    /// <summary>A Python script of the checkout this build was made in (its _geometry-harness); refused in one sentence
    /// when the build sits in no checkout.</summary>
    public static string Script(string name, string step) => Layout.Checkout is { } checkout
        ? PyPath.Join(PyPath.Resolve(checkout), "_geometry-harness", name)
        : throw new Refusal($"{step} is a Python step of a SkyWright checkout, and this runcontrol's build at {PyOrder.Fwd(Layout.Root)} is in none; run it from a build inside a checkout.");

    /// <summary>The houseCARL integration module's source folder in the checkout this build was made in, or null when the
    /// build sits in none. Only the self-test's live cross-checks with the module's Python pipeline and settings use it;
    /// nothing a run does reads the module (a named crossing in SkyWright.Guards' fence check, until step 2).</summary>
    public static string? Integration =>
        HarnessShared.CheckoutLayout.Locate(null, out _)?.Checkout is { } checkout ? Path.Combine(checkout, "integration/housecarl") : null;

    static HarnessShared.CheckoutLayout.Layout Find() =>
        HarnessShared.CheckoutLayout.Locate(null, out var why) ?? throw new Refusal(why + ".");
}
