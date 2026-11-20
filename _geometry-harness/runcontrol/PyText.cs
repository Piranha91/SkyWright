using System.Globalization;
using System.Numerics;
using System.Text;

namespace RunControl;

/// <summary>A Python tuple, so a repr prints round brackets; JSON writes it as a list.</summary>
public sealed record PyTuple(IReadOnlyList<object?> Items);

/// <summary>Python's text forms: repr, float formats, splitlines, timestamps.</summary>
public static class PyText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- floats

    /// <summary>repr(float): shortest round-trip digits, exponent form below 1e-4 and from 1e16.</summary>
    public static string FloatRepr(double x)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        string sign = double.IsNegative(x) ? "-" : "";
        if (x == 0) return sign + "0.0";
        var (digits, decpt) = ShortestDigits(Math.Abs(x));
        if (decpt <= -4 || decpt > 16)
        {
            string mant = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
            return sign + mant + Exponent(decpt - 1);
        }
        if (decpt <= 0) return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length) return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    /// <summary>A float as json.dumps writes it: repr, with NaN and Infinity spelled the JSON way.</summary>
    public static string FloatJson(double x) =>
        double.IsNaN(x) ? "NaN" : double.IsInfinity(x) ? (x > 0 ? "Infinity" : "-Infinity") : FloatRepr(x);

    // The shortest digits that read back as x, and where the point sits: value = 0.DIGITS x 10^decpt.
    static (string digits, int decpt) ShortestDigits(double abs)
    {
        string r = abs.ToString("R", Inv);
        int e = r.IndexOf('E');
        int exp = e < 0 ? 0 : int.Parse(r[(e + 1)..], Inv);
        string mant = e < 0 ? r : r[..e];
        int dot = mant.IndexOf('.');
        string all = dot < 0 ? mant : mant[..dot] + mant[(dot + 1)..];
        int decpt = (dot < 0 ? mant.Length : dot) + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all[lead..];
        decpt -= lead;
        all = all.TrimEnd('0');
        return (all.Length == 0 ? "0" : all, decpt);
    }

    static string Exponent(int e) => (e < 0 ? "e-" : "e+") + Math.Abs(e).ToString("00", Inv);

    // The exact decimal value of a finite double: digits / 10^scale.
    static (BigInteger digits, int scale) Exact(double abs)
    {
        long bits = BitConverter.DoubleToInt64Bits(abs);
        int be = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = be == 0 ? frac : frac | (1L << 52);
        int e = (be == 0 ? 1 : be) - 1075;
        return e >= 0 ? (m << e, 0) : (m * BigInteger.Pow(5, -e), -e);
    }

    // digits / 10^drop, rounded half to even.
    static BigInteger DivRound(BigInteger digits, int drop)
    {
        if (drop <= 0) return digits * BigInteger.Pow(10, -drop);
        var p = BigInteger.Pow(10, drop);
        var q = BigInteger.DivRem(digits, p, out var rem);
        int c = (rem * 2).CompareTo(p);
        return c > 0 || (c == 0 && !q.IsEven) ? q + 1 : q;
    }

    /// <summary>format(x, ".Nf"): the exact value rounded half to even at N places.</summary>
    public static string Fixed(double x, int places)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var (d, scale) = Exact(Math.Abs(x));
        string s = DivRound(d, scale - places).ToString(Inv);
        if (places > 0)
        {
            if (s.Length <= places) s = new string('0', places - s.Length + 1) + s;
            s = s[..^places] + "." + s[^places..];
        }
        return (double.IsNegative(x) ? "-" : "") + s;
    }

    /// <summary>round(x, n) for a float: the double nearest the exact value rounded at n places.</summary>
    public static double Round(double x, int places) =>
        double.IsFinite(x) ? double.Parse(Fixed(x, places), Inv) : x;

    /// <summary>format(x, "g"): six significant digits, trailing zeros dropped.</summary>
    public static string General(double x)
    {
        const int p = 6;
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        string sign = double.IsNegative(x) ? "-" : "";
        if (x == 0) return sign + "0";
        var (d, scale) = Exact(Math.Abs(x));
        int len = d.ToString(Inv).Length;
        // decpt: value = 0.DIGITS x 10^decpt
        int decpt = len - scale;
        string digits = DivRound(d, len - p).ToString(Inv);
        if (digits.Length > p) { digits = digits[..p]; decpt++; }
        int exp = decpt - 1;
        if (exp < -4 || exp >= p)
        {
            string mant = digits.TrimEnd('0');
            mant = mant.Length <= 1 ? mant : mant[..1] + "." + mant[1..];
            return sign + mant + Exponent(exp);
        }
        string body = decpt <= 0 ? "0." + new string('0', -decpt) + digits : digits[..decpt] + "." + digits[decpt..];
        return sign + body.TrimEnd('0').TrimEnd('.');
    }

    // ---- repr and str

    /// <summary>repr() of a value built from JSON kinds, lists, tuples and dicts.</summary>
    public static string Repr(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => ReprString(s),
        double d => FloatRepr(d),
        float f => FloatRepr(f),
        int or long or BigInteger => Convert.ToString(v, Inv)!,
        PyTuple t => t.Items.Count == 1 ? "(" + Repr(t.Items[0]) + ",)" : "(" + string.Join(", ", t.Items.Select(Repr)) + ")",
        PyDict d => "{" + string.Join(", ", d.Select(kv => ReprString(kv.Key) + ": " + Repr(kv.Value))) + "}",
        IEnumerable<object?> l => "[" + string.Join(", ", l.Select(Repr)) + "]",
        _ => throw new ArgumentException($"no Python repr is defined for a {v.GetType().Name}"),
    };

    /// <summary>str() of the same values: a string is itself, everything else is its repr.</summary>
    public static string Str(object? v) => v is string s ? s : Repr(v);

    static string ReprString(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                c = char.ConvertToUtf32(s[i], s[++i]);
            if (c == quote || c == '\\') sb.Append('\\').Append((char)c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 0x20 || c == 0x7F) sb.Append("\\x").Append(c.ToString("x2", Inv));
            else if (c < 0x7F || Printable(c)) sb.Append(c <= 0xFFFF ? ((char)c).ToString() : char.ConvertFromUtf32(c));
            else if (c <= 0xFF) sb.Append("\\x").Append(c.ToString("x2", Inv));
            else if (c <= 0xFFFF) sb.Append("\\u").Append(c.ToString("x4", Inv));
            else sb.Append("\\U").Append(c.ToString("x8", Inv));
        }
        return sb.Append(quote).ToString();
    }

    // str.isprintable for one character: not a control, format, surrogate, private, unassigned or separator.
    static bool Printable(int c)
    {
        if (c is >= 0xD800 and <= 0xDFFF) return false;
        return CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
    }

    // ---- strings

    /// <summary>str.lower as the port spells it: the probes' own invariant rule (DESIGN section 4).</summary>
    public static string Lower(string s) => s.ToLowerInvariant();

    /// <summary>str.splitlines(): every line boundary Python knows, no trailing empty line.</summary>
    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')) continue;
            lines.Add(s[start..i]);
            if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < s.Length) lines.Add(s[start..]);
        return lines;
    }

    /// <summary>str.split(): pieces between runs of Python whitespace.</summary>
    public static List<string> Split(string s)
    {
        var parts = new List<string>();
        int start = -1;
        for (int i = 0; i <= s.Length; i++)
        {
            bool space = i == s.Length || IsSpace(s[i]);
            if (!space && start < 0) start = i;
            else if (space && start >= 0) { parts.Add(s[start..i]); start = -1; }
        }
        return parts;
    }

    static bool IsSpace(char c) => c is (>= '\t' and <= '\r') or (>= '\x1c' and <= ' ') or '\x85' or '\xa0' or '\u1680'
        or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000';

    // ---- timestamps

    /// <summary>evalpipeline's utcnow spelling: whole seconds, "+00:00".</summary>
    public static string IsoPlus(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "+00:00";

    /// <summary>evalrun's utcnow spelling: whole seconds, "Z".</summary>
    public static string IsoZ(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "Z";

    public static string UtcNowPlus() => IsoPlus(DateTime.UtcNow);
    public static string UtcNowZ() => IsoZ(DateTime.UtcNow);

    /// <summary>evalrun's iso_of: a Unix time rounded half to even at the microsecond, then cut to the second.</summary>
    public static string IsoOf(double ts)
    {
        double whole = Math.Truncate(ts);
        double micro = Math.Round((ts - whole) * 1e6, MidpointRounding.ToEven);
        if (micro >= 1e6) whole += 1;
        else if (micro < 0) whole -= 1;
        return IsoZ(DateTime.UnixEpoch.AddSeconds(whole));
    }
}
