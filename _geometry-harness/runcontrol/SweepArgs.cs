using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The sweep stops here with this exit code; a sentence goes to standard error first.</summary>
public sealed class SweepExit(int code, string sentence = "") : Exception(sentence)
{
    public int Code { get; } = code;
}

/// <summary>The arguments of `runcontrol ordersweep`, read by argparse's rules: an option's value is the next
/// argument unless that looks like an option, `--name=value` is accepted, and a unique prefix names an option.</summary>
public sealed class SweepArgs
{
    public string Candidates = "", Out = "", Probes = "navownprobe,overlapprobe,contactprobe", ProbeArgs = "", Harness = "", PopulationProbes = "";
    public string? Instance, Profile, Game, Baseline, DiffTolerance, Population;
    public BigInteger? MaxCandidates;
    public double? DeadlineSeconds;
    public bool NoReverse, NoPairResults, FullReverse;
    public List<string> ProbeCmd = [], ReuseCache = [];

    // Every option in the order argparse holds them: the name, and whether it takes a value.
    static readonly (string name, bool value)[] Options =
    [
        ("-h", false), ("--help", false), ("--candidates", true), ("--out", true), ("--probes", true), ("--instance", true),
        ("--profile", true), ("--game", true), ("--probe-args", true), ("--harness", true), ("--baseline", true),
        ("--max-candidates", true), ("--diff-tolerance", true), ("--no-reverse", false), ("--no-pair-results", false),
        ("--full-reverse", false), ("--deadline-seconds", true), ("--probe-cmd", true), ("--population", true),
        ("--population-probes", true), ("--reuse-cache", true),
    ];

    public const string Usage = "usage: runcontrol ordersweep --candidates DIR --out DIR [--probes a,b,c] [--instance DIR --profile NAME --game DIR] "
        + "[--probe-args \"...\"] [--harness DIR] [--baseline FILE] [--max-candidates N] [--diff-tolerance T] [--no-reverse] "
        + "[--no-pair-results] [--full-reverse] [--deadline-seconds S] [--probe-cmd NAME=<argv prefix>]... "
        + "[--population FILE --population-probes a,b] [--reuse-cache FILE]...";

    static readonly Regex NegativeNumber = new(@"^-\d+$|^-\d*\.\d+$", RegexOptions.CultureInvariant);

    static SweepExit Error(string message) => new(2, Usage + "\nruncontrol ordersweep: error: " + message);

    // argparse's _parse_optional: the option an argument names (with a value given after "="), "" for an option
    // nothing declares, null for a plain argument.
    static string? Optional(string arg, out string? explicitValue)
    {
        explicitValue = null;
        if (arg.Length == 0 || arg[0] != '-') return null;
        if (Options.Any(o => o.name == arg)) return arg;
        if (arg.Length == 1) return null;
        string prefix = arg;
        int eq = arg.IndexOf('=');
        if (eq >= 0)
        {
            prefix = arg[..eq];
            explicitValue = arg[(eq + 1)..];
            if (Options.Any(o => o.name == prefix)) return prefix;
        }
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var matches = Options.Where(o => o.name.StartsWith(prefix, StringComparison.Ordinal)).Select(o => o.name).ToList();
            if (matches.Count > 1) throw Error($"ambiguous option: {arg} could match {string.Join(", ", matches)}");
            if (matches.Count == 1) return matches[0];
        }
        explicitValue = null;
        if (NegativeNumber.IsMatch(arg)) return null;
        if (arg.Contains(' ')) return null;
        return "";
    }

    // float(text) as Python reads it; null when it is not a number.
    static double? Float(string text)
    {
        string s = PyValue.Strip(text).Replace("_", "");
        string low = s.TrimStart('+', '-').ToLowerInvariant();
        if (low is "inf" or "infinity") return s.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        if (low == "nan") return double.NaN;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !low.Contains("inf") && !low.Contains("nan") ? d : null;
    }

    public static SweepArgs Parse(string[] args)
    {
        var a = new SweepArgs { Harness = PyPath.Str(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))) };
        var seen = new HashSet<string>();
        var extras = new List<string>();
        bool rest = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (rest) { extras.Add(arg); continue; }
            if (arg == "--") { rest = true; continue; }
            string? opt = Optional(arg, out string? given);
            if (opt is null or "") { extras.Add(arg); continue; }
            if (opt is "-h" or "--help") throw new SweepExit(0, Usage);
            if (!Options.First(o => o.name == opt).value)
            {
                if (given != null) throw Error($"argument {opt}: ignored explicit argument {PyText.Repr(given)}");
                if (opt == "--no-reverse") a.NoReverse = true;
                else if (opt == "--no-pair-results") a.NoPairResults = true;
                else a.FullReverse = true;
                continue;
            }
            string value;
            if (given != null) value = given;
            else if (i + 1 < args.Length && args[i + 1] != "--" && Optional(args[i + 1], out _) == null) value = args[++i];
            else throw Error($"argument {opt}: expected one argument");
            seen.Add(opt);
            switch (opt)
            {
                case "--candidates": a.Candidates = value; break;
                case "--out": a.Out = value; break;
                case "--probes": a.Probes = value; break;
                case "--instance": a.Instance = value; break;
                case "--profile": a.Profile = value; break;
                case "--game": a.Game = value; break;
                case "--probe-args": a.ProbeArgs = value; break;
                case "--harness": a.Harness = value; break;
                case "--baseline": a.Baseline = value; break;
                case "--diff-tolerance": a.DiffTolerance = value; break;
                case "--population": a.Population = value; break;
                case "--population-probes": a.PopulationProbes = value; break;
                case "--probe-cmd": a.ProbeCmd.Add(value); break;
                case "--reuse-cache": a.ReuseCache.Add(value); break;
                case "--max-candidates":
                    a.MaxCandidates = PyValue.IntText(value) ?? throw Error($"argument --max-candidates: invalid int value: {PyText.Repr(value)}");
                    break;
                case "--deadline-seconds":
                    a.DeadlineSeconds = Float(value) ?? throw Error($"argument --deadline-seconds: invalid float value: {PyText.Repr(value)}");
                    break;
            }
        }
        var missing = new[] { "--candidates", "--out" }.Where(o => !seen.Contains(o)).ToList();
        if (missing.Count > 0) throw Error("the following arguments are required: " + string.Join(", ", missing));
        if (extras.Count > 0) throw Error("unrecognized arguments: " + string.Join(" ", extras));
        return a;
    }
}
