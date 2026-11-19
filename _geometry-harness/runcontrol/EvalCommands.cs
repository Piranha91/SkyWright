namespace RunControl;

/// <summary>The command lines of evalrun.py's reading subcommands as runcontrol takes them: `finalize --run RUN`
/// and `eval-status --run RUN` (evalrun's `status`). Exit 0 done, 2 a refused
/// command line or input, 3 a refused result, 1 an input of a shape the rules cannot read (where Python stops with
/// a traceback).</summary>
public static class EvalCommands
{
    public static bool Handles(string command) => command is "finalize" or "eval-status";

    public static int Run(string[] args)
    {
        string command = args[0];
        try
        {
            string? run = null;
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
                if (flag == "--run") run = Value();
                else throw new Refusal($"{command} does not take {flag}.");
            }
            if (run == null) throw new Refusal($"{command} needs --run, the evaluation's RUN folder.");
            return command == "finalize" ? Finalizer.Run(run) : EvalStatus.Run(run);
        }
        catch (Refusal r)
        {
            Console.Error.WriteLine($"REFUSED: {r.Message}");
            return Exit.Refused;
        }
        catch (Exception ex) when (ex is PyError or KeyNotFoundException or InvalidCastException or FormatException)
        {
            Console.Error.WriteLine($"{command} stopped: a run file holds a value of a shape its rule cannot read ({ex.GetType().Name}: {ex.Message}).");
            return 1;
        }
    }
}
