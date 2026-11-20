using System.Globalization;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>Two Python readings of text the bundle's names step depends on: a command line split the way
/// shlex.split(posix=False) splits it, and float(text).</summary>
public static class PyShell
{
    /// <summary>shlex.split(text, posix=False): pieces between whitespace; a piece that starts with a quote runs to
    /// the same quote and ends there, quotes kept; a quote inside a piece is an ordinary character. An unclosed
    /// quote is Python's ValueError("No closing quotation").</summary>
    public static List<string> SplitWindows(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            int start = i;
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1);
                if (close < 0) throw new PyValueError("No closing quotation");
                i = close + 1;
            }
            else
                while (i < text.Length && text[i] is not (' ' or '\t' or '\r' or '\n')) i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }

    /// <summary>A command line as bundleenrich's Reader starts it: split, then one pair of surrounding double quotes
    /// taken off each piece.</summary>
    public static List<string> ReaderArgv(string commandLine) =>
        SplitWindows(commandLine).Select(t => t.Length > 1 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t).ToList();

    static readonly Regex Number = new(@"\A[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);
    static readonly Regex Underscore = new(@"(?<=[0-9])_(?=[0-9])", RegexOptions.CultureInvariant);

    /// <summary>float(v) for a JSON value: a number as it is, a text by Python's grammar (surrounding whitespace,
    /// inf, nan, an underscore between digits). Text that is no number is Python's ValueError with its sentence.</summary>
    public static double Float(object? v)
    {
        switch (v)
        {
            case double d: return d;
            case bool b: return b ? 1.0 : 0.0;
            case int or long or System.Numerics.BigInteger: return (double)PyValue.Int(v);
            case string s:
                // any Unicode decimal digit counts as its ASCII digit, as in Python
                string t = Underscore.Replace(string.Concat(PyValue.Strip(s).Select(c => c > 0x7F && char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c)), "");
                string low = t.TrimStart('+', '-').ToLowerInvariant();
                bool negative = t.StartsWith('-');
                if (t.Length - low.Length <= 1 && low is "inf" or "infinity") return negative ? double.NegativeInfinity : double.PositiveInfinity;
                if (t.Length - low.Length <= 1 && low == "nan") return double.NaN;
                if (Number.IsMatch(t)) return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
                throw new PyValueError($"could not convert string to float: {PyText.Repr(s)}");
            default:
                throw new PyError($"float() argument must be a string or a real number, not '{PyValue.TypeName(v)}'");
        }
    }
}

/// <summary>Python's ValueError where a caller catches it by that name; the message is Python's own.</summary>
public sealed class PyValueError(string message) : Exception(message);
