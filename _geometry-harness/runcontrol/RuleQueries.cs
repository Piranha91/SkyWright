namespace RunControl;

/// <summary>The rule answer the Python that stays asks for, so it holds no copy of a rule this program owns: `runcontrol
/// fingerprint` (ConfigurationIdentity.Compute, through Fingerprints.Of). It reads one JSON document on standard input
/// and writes one JSON answer on standard output, exit 0, or one REFUSED sentence on standard error, exit 2. It is pure,
/// reads and writes no file, and opens no run-log record: a caller may ask many times a run, and opening a record costs
/// seconds. Answer is that shape for any pure rule a caller passes (the rule a parameter).</summary>
public static class RuleQueries
{
    /// <summary>switchflip-outcome, refused: its rule left SkyWright with the check it reads.</summary>
    public const string SwitchFlipGone = "switchflip-outcome is not SkyWright's: the switch-flip reading rule is the houseCARL "
        + "integration module's, with the check whose launches it reads.";

    public static bool Handles(string[] args) => args.Length == 1 && args[0] is "fingerprint" or "switchflip-outcome";

    public static int Run(string[] args)
    {
        if (args[0] == "switchflip-outcome")
        {
            Console.Error.WriteLine($"REFUSED: {SwitchFlipGone}");
            return Exit.Refused;
        }
        return Answer(args[0], Fingerprints.Of);
    }

    /// <summary>One request on standard input answered by `rule` on standard output; `name` is the command's, for its
    /// refusals.</summary>
    public static int Answer(string name, Func<object?, object> rule)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            stdin.CopyTo(buffer);
            object? request;
            try { request = PyJson.Loads(PyJson.Decode(buffer.ToArray(), bom: true)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or PyError)
            {
                throw new Refusal($"{name}: standard input is not one JSON document ({ex.Message}); send the request as JSON.");
            }
            object answer;
            try { answer = rule(request); }
            catch (Exception ex) when (EvalCommands.IsShapeError(ex) && ex is not IOException and not UnauthorizedAccessException)
            {
                throw new Refusal($"{name}: the request holds a value of a shape the rule cannot read ({ex.GetType().Name}: {ex.Message}); "
                                  + "send each configuration or event as the documented JSON object.");
            }
            Console.WriteLine(PyJson.Dumps(answer, ensureAscii: false));
            return Exit.Ok;
        }
        catch (Refusal r)
        {
            Console.Error.WriteLine($"REFUSED: {r.Message}");
            return Exit.Refused;
        }
    }
}
