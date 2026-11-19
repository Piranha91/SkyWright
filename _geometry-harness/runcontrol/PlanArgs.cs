using System.Globalization;
using System.Numerics;

namespace RunControl;

/// <summary>What `plan` was asked, flag for flag as evalrun.py's parser reads them.</summary>
public sealed class PlanArgs
{
    public string? Registry, Scope, Candidates, States, Depth, Settings, ReviewArm, Out, Instance, Profile, Game, Harness,
                   PrimitiveSolidity, PopulationFrom, DiffTolerance;
    public string ProbeArgs = "";
    public List<string>? Alt, Supplied, Select, Exclude, OptIn, ProbeCmd;
    public double? BudgetMinutes;
    public long? MaxCandidates;
    public bool NoReverse;

    static readonly string[] Depths = ["standard", "deep"];

    // A token after a flag is its value unless it reads as another flag: a leading dash, no space, not a negative number.
    static bool ReadsAsFlag(string tok) =>
        tok.Length > 1 && tok[0] == '-' && !tok.Contains(' ')
        && !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>The arguments after `plan`; a flag it does not know, a missing value or a bad number is refused.</summary>
    public static PlanArgs Parse(IReadOnlyList<string> argv)
    {
        var a = new PlanArgs();
        for (int i = 0; i < argv.Count; i++)
        {
            string flag = argv[i];
            string? inline = null;
            if (flag.StartsWith("--", StringComparison.Ordinal) && flag.Contains('='))
            {
                inline = flag[(flag.IndexOf('=') + 1)..];
                flag = flag[..flag.IndexOf('=')];
            }
            string Value()
            {
                if (inline != null) return inline;
                if (i + 1 < argv.Count && !ReadsAsFlag(argv[i + 1])) return argv[++i];
                throw new Refusal($"plan: {flag} needs a value after it.");
            }
            static List<string> Add(List<string>? list, string v) { list ??= []; list.Add(v); return list; }
            switch (flag)
            {
                case "--registry": a.Registry = Value(); break;
                case "--scope": a.Scope = Value(); break;
                case "--candidates": a.Candidates = Value(); break;
                case "--alt": a.Alt = Add(a.Alt, Value()); break;
                case "--supplied": a.Supplied = Add(a.Supplied, Value()); break;
                case "--states": a.States = Value(); break;
                case "--depth":
                    a.Depth = Value();
                    if (!Depths.Contains(a.Depth))
                        throw new Refusal($"plan: --depth {PyText.Repr(a.Depth)} is not a depth; choose from 'standard', 'deep'.");
                    break;
                case "--settings": a.Settings = Value(); break;
                case "--select": a.Select = Add(a.Select, Value()); break;
                case "--review-arm": a.ReviewArm = Value(); break;
                case "--exclude": a.Exclude = Add(a.Exclude, Value()); break;
                case "--opt-in": a.OptIn = Add(a.OptIn, Value()); break;
                case "--budget-minutes":
                    string minutes = Value();
                    a.BudgetMinutes = PyFloat(minutes) ?? throw new Refusal($"plan: --budget-minutes {PyText.Repr(minutes)} is not a number.");
                    break;
                case "--max-candidates":
                    string count = Value();
                    a.MaxCandidates = PyValue.IntText(count) is { } n && n >= long.MinValue && n <= long.MaxValue ? (long)n
                        : throw new Refusal($"plan: --max-candidates {PyText.Repr(count)} is not a whole number.");
                    break;
                case "--out": a.Out = Value(); break;
                case "--instance": a.Instance = Value(); break;
                case "--profile": a.Profile = Value(); break;
                case "--game": a.Game = Value(); break;
                case "--harness": a.Harness = Value(); break;
                case "--probe-args": a.ProbeArgs = Value(); break;
                case "--primitive-solidity": a.PrimitiveSolidity = Value(); break;
                case "--population-from": a.PopulationFrom = Value(); break;
                case "--probe-cmd": a.ProbeCmd = Add(a.ProbeCmd, Value()); break;
                case "--diff-tolerance": a.DiffTolerance = Value(); break;
                case "--no-reverse" when inline == null: a.NoReverse = true; break;
                default: throw new Refusal($"plan does not take {argv[i]}; its flags are those of evalrun.py plan.");
            }
        }
        var missing = new[] { ("--registry", a.Registry), ("--candidates", a.Candidates), ("--out", a.Out) }.Where(x => x.Item2 == null).Select(x => x.Item1).ToList();
        if (missing.Count > 0) throw new Refusal($"plan needs {string.Join(", ", missing)}.");
        return a;
    }

    /// <summary>float(text): a decimal number, inf or nan, spaces round it allowed; null when it is none of those.</summary>
    public static double? PyFloat(string text)
    {
        string t = PyValue.Strip(text).Replace("_", "");
        string low = t.TrimStart('+', '-').ToLowerInvariant();
        if (low is "inf" or "infinity") return t.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        if (low == "nan") return double.NaN;
        if (t.Length == 0 || !(char.IsAsciiDigit(t[^1]) || t[^1] == '.') || t.Any(char.IsWhiteSpace)) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
