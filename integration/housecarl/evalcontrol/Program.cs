// evalcontrol: the AI-side evaluation's own checks, as an overlay on a finished app scan. The app's scan (runcontrol,
// census to bundle) knows nothing an LLM alone serves; the rows of ai-checks.json are planned here over the app plan's
// arms into an overlay run folder, evaluated, finalized, bundled beside the app's bundle and verified with it, all by
// runcontrol's own rules for plans, receipts, ledgers, dispositions and bundles (it is used as a library), plus the
// rules only these rows need, which live here.
//
//   evalcontrol plan     --app EVAL --app-bundle BUNDLE --out AI [--opt-in CHECK]... [--list FILE] [--probe-cmd NAME=CMD]...
//                        freeze the overlay's plan: ai-checks.json's rows over the app plan's arms, bound to the app plan
//                        and the app bundle's manifest; --list plans another list of the same schema, validated the
//                        same way, in place of the shipped one (a trim of it, for a suite); --probe-cmd stands in one of
//                        this module's probes with a command (for a suite), as runcontrol's --probe-cmd does for the app's
//   evalcontrol evaluate --run AI [--rebuild-ledger]
//                        account every row of the overlay plan (what an earlier call settled is re-checked and reused);
//                        --rebuild-ledger first rebuilds the ledger from the journal and the receipts
//   evalcontrol import   --run AI --file DOC
//                        bind a record reader's evidence (houseCARL's record tools), or a live game driver's export
//                        (switchflip.py export), to one arm the overlay left open
//   evalcontrol switchflip-outcome
//                        read one JSON request on standard input ({"classify": {events, flipStart, windowSeconds?}},
//                        {"armReading": [...]} or {"rules": true}); print the switch-flip reading rule's answer, for
//                        the live switch check's driver (no run-log record: it is asked once a second)
//   evalcontrol finalize --run AI      the overlay's disposition and STATUS.md
//   evalcontrol bundle   --run AI --out DIR [--media FILE] [--registry FILE] [--scope FILE] [--search FILE]
//                        the overlay's evidence bundle; --media adds supplied pictures and clips, each checked against
//                        the configurations the app scan's accepted receipts measured
//   evalcontrol verify   DIR [--app BUNDLE] [--out FILE]
//                        the overlay bundle, the app bundle it is bound to and the binding; writes verify.json with
//                        aiEvaluation.complete, the one place that rule is decided
//   evalcontrol --self-test
//
// Exit 0; 2 for a refused command line or input; 3 for a run that cannot be finalized or bundled, or a bundle that
// does not verify.
using System.Reflection;
using HousecarlCore.Progress;
using RunControl;

namespace EvalControl;

public static class Program
{
    /// <summary>The app build this program uses, from the metadata its build wrote (Directory.Build.props owns the layout).</summary>
    public static string AppBuild()
    {
        string rel = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "SkyWrightAppBuild").Value!;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        ChildLaunch.LogLabel = "evalcontrol";
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest.Run();
        // a pure answer the live driver asks once a second: before the run-log record, whose opening costs seconds
        if (args.Length == 1 && args[0] == "switchflip-outcome") return RuleQueries.Answer(args[0], SwitchFlipOutcome.Answer);
        if (args.Length == 0)
        {
            Console.Error.WriteLine("REFUSED: evalcontrol needs a command: plan, evaluate, import, finalize, bundle, verify, switchflip-outcome or --self-test (see Program.cs).");
            return Exit.Refused;
        }
        using var record = RunLog.BeginProcess("evalcontrol", args);
        try
        {
            Checkout.UseBuild(AppBuild());
            var a = Args.Parse(args);
            return args[0] switch
            {
                "plan" => OverlayPlan.Run(a.Need("--app"), a.Need("--app-bundle"), a.Need("--out"), a.Many("--opt-in"), a.One("--list"), a.Many("--probe-cmd")),
                "evaluate" => OverlayEvaluate.Run(a.Need("--run"), a.Has("--rebuild-ledger")),
                "import" => RecordImport.Run(a.Need("--run"), a.Need("--file")),
                "finalize" => Finalizer.Run(a.Need("--run"), OverlayFinalize.Amend, ModuleRows.LaunchKinds),
                "bundle" => OverlayBundle.Run(a.Need("--run"), a.Need("--out"), a.One("--media"), a.One("--registry"), a.One("--scope"), a.One("--search")),
                "verify" => OverlayVerify.Run(a.Positional ?? throw new Refusal("verify needs the overlay bundle's folder: evalcontrol verify DIR [--app BUNDLE] [--out FILE]."),
                    a.One("--app"), a.One("--out")),
                _ => throw new Refusal($"evalcontrol has no command '{args[0]}'; the commands are plan, evaluate, import, finalize, bundle, verify, switchflip-outcome and --self-test."),
            };
        }
        catch (Refusal r)
        {
            Console.Error.WriteLine($"REFUSED: {r.Message}");
            record.Refuse(r.Message, Exit.Refused);
            return Exit.Refused;
        }
    }
}

/// <summary>A command line: --flag value pairs (a flag may repeat) and at most one positional argument.</summary>
public sealed class Args
{
    readonly Dictionary<string, List<string>> _flags = new(StringComparer.Ordinal);
    public string? Positional { get; private set; }
    string _command = "";

    static readonly Dictionary<string, string[]> Takes = new(StringComparer.Ordinal)
    {
        ["plan"] = ["--app", "--app-bundle", "--out", "--opt-in", "--list", "--probe-cmd"],
        ["evaluate"] = ["--run", "--rebuild-ledger"],
        ["import"] = ["--run", "--file"],
        ["finalize"] = ["--run"],
        ["bundle"] = ["--run", "--out", "--media", "--registry", "--scope", "--search"],
        ["verify"] = ["--app", "--out"],
    };

    public static Args Parse(string[] args)
    {
        var a = new Args { _command = args[0] };
        if (!Takes.TryGetValue(args[0], out var takes)) return a;
        for (int i = 1; i < args.Length; i++)
        {
            string flag = args[i], f = flag;
            // every option evalcontrol has (the tool index reads them from this line)
            bool known = flag is "--app" or "--app-bundle" or "--out" or "--opt-in" or "--run" or "--media" or "--registry" or "--scope" or "--search" or "--file" or "--rebuild-ledger" or "--list" or "--probe-cmd";
            if (f.StartsWith("--", StringComparison.Ordinal) && !known) throw new Refusal($"evalcontrol has no option {f}.");
            if (!f.StartsWith("--", StringComparison.Ordinal))
            {
                if (a.Positional != null || args[0] != "verify") throw new Refusal($"{args[0]} does not take the argument {f}.");
                a.Positional = f;
                continue;
            }
            if (!takes.Contains(f)) throw new Refusal($"{args[0]} does not take {f}; it takes {string.Join(", ", takes)}.");
            if (Switches.Contains(f))
            {
                if (!a._flags.TryGetValue(f, out var on)) a._flags[f] = on = [];
                on.Add("");
                continue;
            }
            if (i + 1 >= args.Length) throw new Refusal($"{args[0]}: {f} needs a value after it.");
            if (!a._flags.TryGetValue(f, out var list)) a._flags[f] = list = [];
            list.Add(args[++i]);
        }
        return a;
    }

    /// <summary>The options that take no value.</summary>
    static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "--rebuild-ledger" };

    public bool Has(string flag) => _flags.ContainsKey(flag);
    public string Need(string flag) => One(flag) ?? throw new Refusal($"{_command} needs {flag}.");
    public string? One(string flag) => _flags.TryGetValue(flag, out var v) ? v[^1] : null;
    public List<string> Many(string flag) => _flags.TryGetValue(flag, out var v) ? v : [];
}
