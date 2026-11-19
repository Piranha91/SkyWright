namespace RunControl;

/// <summary>What one of evalrun.py's commands was asked, flag for flag as its parser reads them.</summary>
public sealed class EvalArgs
{
    public string? Run, Dir, Out, Instance;
    public string EnablePopulation = "load-time";
    public double? BudgetMinutes;
    public bool RebuildLedger;
    public List<string> Plugins = [], Mods = [], Targets = [];
}

/// <summary>The command lines of evalrun.py's commands as runcontrol takes them: `run --run RUN`, `resume --run RUN`,
/// `status --run RUN`, `finalize --run RUN`, `verify-bundle DIR [--out FILE]` and `population`. `run` and `status`
/// are also pipeline commands; the flag `--run` says which is meant. Exit 0 done, 2 a refused command line or input
/// (an input of a shape the rules cannot read included), 3 a refused result.</summary>
public static class EvalCommands
{
    // The pipeline's `run` takes --entry and --out, its `status` takes --out; evalrun's take --run.
    static bool NamesRun(string[] args) => args.Skip(1).Any(a => a == "--run" || a.StartsWith("--run=", StringComparison.Ordinal));

    public static bool Handles(string[] args) => args.Length >= 1 && args[0] switch
    {
        "finalize" or "verify-bundle" or "resume" or "population" => true,
        "run" or "status" => NamesRun(args),
        _ => false,
    };

    /// <summary>What a Python traceback is here: an input of a shape the rules cannot read.</summary>
    public static bool IsShapeError(Exception ex) => ex is PyError or KeyNotFoundException or InvalidCastException or FormatException
        or NullReferenceException or ArgumentOutOfRangeException or IndexOutOfRangeException or IOException or UnauthorizedAccessException;

    /// <summary>The one sentence for such an input.</summary>
    public static string ShapeSentence(string command, Exception ex) =>
        $"{command}: a file of the run holds a value of a shape its rule cannot read ({ex.GetType().Name}: {ex.Message}); "
        + "the file is damaged or was not written by this controller, so restore it or plan again in a new folder.";

    static EvalArgs Parse(string command, string[] args)
    {
        var a = new EvalArgs();
        bool has(params string[] commands) => commands.Contains(command);
/// <summary>The command lines of evalrun.py's reading subcommands as runcontrol takes them: `finalize --run RUN`,
/// `bundle --run RUN [--out DIR] …`, `verify-bundle DIR [--out FILE]` and `eval-status --run RUN` (evalrun's
/// `status`). Exit 0 done, 2 a refused command line or input, 3 a refused result, 1 an input of a shape the rules
/// cannot read (where Python stops with a traceback); bundle refuses that input with exit 2 instead.</summary>
public static class EvalCommands
{
    public static bool Handles(string command) => command is "finalize" or "bundle" or "verify-bundle" or "eval-status";

    // bundle's own command line: every flag evalrun.py's `bundle` takes.
    static int RunBundle(string[] args)
    {
        var a = new BundleArgs();
        bool run = false;
        for (int i = 1; i < args.Length; i++)
        {
            string flag = args[i];
            string? joined = null;   // the value of `--flag=value`
            if (flag.StartsWith("--", StringComparison.Ordinal) && flag.Contains('='))
            {
                joined = flag[(flag.IndexOf('=') + 1)..];
                flag = flag[..flag.IndexOf('=')];
            }
            string Value() => joined ?? (i + 1 < args.Length ? args[++i] : throw new Refusal($"{command}: {flag} needs a value after it."));
            if (flag == "--run" && command != "verify-bundle" && command != "population") a.Run = Value();
            else if (flag == "--budget-minutes" && has("run", "resume"))
            {
                string text = Value();
                a.BudgetMinutes = PlanArgs.PyFloat(text) ?? throw new Refusal($"{command}: --budget-minutes {PyText.Repr(text)} is not a number.");
            }
            else if (flag == "--rebuild-ledger" && command == "resume" && joined == null) a.RebuildLedger = true;
            else if (flag == "--out" && has("verify-bundle", "population")) a.Out = Value();
            else if (flag == "--plugin" && command == "population") a.Plugins.Add(Value());
            else if (flag == "--mod" && command == "population") a.Mods.Add(Value());
            else if (flag == "--target" && command == "population") a.Targets.Add(Value());
            else if (flag == "--instance" && command == "population") a.Instance = Value();
            else if (flag == "--enable-population" && command == "population") a.EnablePopulation = Value();
            else if (command == "verify-bundle" && a.Dir == null && !flag.StartsWith('-')) a.Dir = flag;
            else throw new Refusal($"{command} does not take {flag}.");
        }
        return a;
            string Value() => joined ?? (i + 1 < args.Length ? args[++i] : throw new Refusal($"bundle: {flag} needs a value after it."));
            switch (flag)
            {
                case "--run": a.Run = Value(); run = true; break;
                case "--out": a.Out = Value(); break;
                case "--media": a.Media = Value(); break;
                case "--registry": a.Registry = Value(); break;
                case "--scope": a.Scope = Value(); break;
                case "--search": a.Search = Value(); break;
                case "--reader-cmd": a.ReaderCmd = Value(); break;
                case "--reader-env": a.ReaderEnv.Add(Value()); break;
                case "--mod-name": a.ModName.Add(Value()); break;
                case "--no-names" when joined == null: a.NoNames = true; break;
                default: throw new Refusal($"bundle does not take {flag}.");
            }
        }
        if (!run) throw new Refusal("bundle needs --run, the evaluation's RUN folder.");
        try { return Bundle.Run(a); }
        catch (Exception ex) when (ex is PyError or KeyNotFoundException or InvalidCastException or FormatException or ArgumentException
            or IndexOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            // where Python stops with a traceback: one sentence, and the folder is not a bundle
            Console.Error.WriteLine($"REFUSED: bundle stopped on a file of a shape its rule cannot read ({ex.GetType().Name}: {ex.Message}); "
                + "what it wrote so far is not a bundle, so correct the file and run bundle again into an empty folder.");
            return Exit.Refused;
        }
    }

    public static int Run(string[] args)
    {
        string command = args[0];
        try
        {
            var a = Parse(command, args);
            if (command == "bundle") return RunBundle(args);
            string? run = null, dir = null, outFile = null;
            for (int i = 1; i < args.Length; i++)
            {
                string flag = args[i];
                string? joined = null;   // the value of `--flag=value`
                if (flag.StartsWith("--", StringComparison.Ordinal) && flag.Contains('='))
                {
                    joined = flag[(flag.IndexOf('=') + 1)..];
                    flag = flag[..flag.IndexOf('=')];
                }
                string Value() => joined ?? (i + 1 < args.Length ? args[++i] : throw new Refusal($"{command}: {flag} needs a value after it."));
                if (flag == "--run" && command != "verify-bundle") run = Value();
                else if (flag == "--out" && command == "verify-bundle") outFile = Value();
                else if (command == "verify-bundle" && dir == null && !flag.StartsWith('-')) dir = flag;
                else throw new Refusal($"{command} does not take {flag}.");
            }
            if (command == "verify-bundle")
                return VerifyBundle.Run(a.Dir ?? throw new Refusal("verify-bundle needs the bundle folder: runcontrol verify-bundle DIR [--out FILE]."), a.Out);
            if (command == "population") return Population.Run(a);
            if (a.Run == null) throw new Refusal($"{command} needs --run, the evaluation's RUN folder.");
            return command switch
            {
                "finalize" => Finalizer.Run(a.Run),
                "status" => EvalStatus.Run(a.Run),
                "resume" => EvaluateRun.Run(a, resuming: true),
                _ => EvaluateRun.Run(a, resuming: false),
            };
        }
        catch (Refusal r)
        {
            Console.Error.WriteLine($"REFUSED: {r.Message}");
            return Exit.Refused;
        }
        catch (Unreadable u)
        {
            Console.Error.WriteLine($"REFUSED: {u.Message}; make the file readable and run again.");
            return Exit.Refused;
        }
        catch (Exception ex) when (IsShapeError(ex))
        {
            Console.Error.WriteLine($"REFUSED: {ShapeSentence(command, ex)}");
            return Exit.Refused;
        }
    }
}
