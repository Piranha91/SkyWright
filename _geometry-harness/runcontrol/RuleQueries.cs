namespace RunControl;

/// <summary>The rule answers the Python that stays asks for, so it holds no copy of a rule this program owns: `runcontrol
/// fingerprint` (ConfigurationIdentity.Compute, through BundleMedia.Fingerprints) and `runcontrol switchflip-outcome`
/// (SwitchFlipOutcome). Each reads one JSON document on standard input and writes one JSON answer on standard output,
/// exit 0, or one REFUSED sentence on standard error, exit 2. They are pure, read and write no file, and open no run-log
/// record: the live driver asks once a second, and opening a record costs seconds.</summary>
public static class RuleQueries
{
    public static bool Handles(string[] args) => args.Length == 1 && args[0] is "fingerprint" or "switchflip-outcome";

    public static int Run(string[] args)
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
                throw new Refusal($"{args[0]}: standard input is not one JSON document ({ex.Message}); send the request as JSON.");
            }
            object answer = args[0] == "fingerprint" ? BundleMedia.Fingerprints(request) : SwitchFlipOutcome.Answer(request);
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
