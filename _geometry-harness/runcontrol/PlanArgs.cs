using System.Globalization;

namespace RunControl;

/// <summary>What `plan` was asked, flag for flag as evalrun.py's parser reads them (Program.cs reads the command line).</summary>
public sealed class PlanArgs
{
    public string? Registry, Scope, Candidates, States, Depth, Settings, ReviewArm, Out, Instance, Profile, Game, Harness,
                   PrimitiveSolidity, PopulationFrom, DiffTolerance, MeshReach;
    public string ProbeArgs = "";
    public List<string>? Alt, Supplied, Select, Exclude, OptIn, ProbeCmd;
    public double? BudgetMinutes;
    public long? MaxCandidates;
    public bool NoReverse;

    /// <summary>A token after a flag is its value unless it reads as another flag: a leading dash, no space, not a number.</summary>
    public static bool ReadsAsFlag(string tok) =>
        tok.Length > 1 && tok[0] == '-' && !tok.Contains(' ')
        && !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>One more value of a repeatable flag.</summary>
    public static List<string> Add(List<string>? list, string value)
    {
        list ??= [];
        list.Add(value);
        return list;
    }

    public static string DepthOf(string text) => text is "standard" or "deep" ? text
        : throw new Refusal($"plan: --depth {PyText.Repr(text)} is not a depth; choose from 'standard', 'deep'.");

    public static double Minutes(string text) => PyFloat(text) ?? throw new Refusal($"plan: --budget-minutes {PyText.Repr(text)} is not a number.");

    public static long Count(string text) => PyValue.IntText(text) is { } n && n >= long.MinValue && n <= long.MaxValue ? (long)n
        : throw new Refusal($"plan: --max-candidates {PyText.Repr(text)} is not a whole number.");

    /// <summary>These arguments, once the flags plan cannot do without are known to be there.</summary>
    public PlanArgs Checked()
    {
        var missing = new[] { ("--registry", Registry), ("--candidates", Candidates), ("--out", Out) }.Where(x => x.Item2 == null).Select(x => x.Item1).ToList();
        return missing.Count == 0 ? this : throw new Refusal($"plan needs {string.Join(", ", missing)}.");
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
