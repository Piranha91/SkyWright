using System.Globalization;
using System.Numerics;

namespace RunControl;

/// <summary>An error the Python runtime raises on a value of the wrong kind; the message is Python's own text.</summary>
public sealed class PyError(string message) : Exception(message);

/// <summary>Python's rules for a parsed JSON value: truth, type names, equality, int(), len(), iteration.</summary>
public static class PyValue
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>bool(v): None, False, zero, and an empty string, list or dict are false.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int n => n != 0,
        long n => n != 0,
        BigInteger n => !n.IsZero,
        double d => d != 0,
        PyDict d => d.Count > 0,
        PyTuple t => t.Items.Count > 0,
        IEnumerable<object?> l => l.Any(),
        _ => true,
    };

    /// <summary>type(v).__name__.</summary>
    public static string TypeName(object? v) => v switch
    {
        null => "NoneType",
        bool => "bool",
        string => "str",
        int or long or BigInteger => "int",
        double or float => "float",
        PyDict => "dict",
        PyTuple => "tuple",
        _ => "list",
    };

    /// <summary>isinstance(v, int) and not a bool: a JSON number written without a point or exponent.</summary>
    public static bool IsInt(object? v) => v is int or long or BigInteger;

    /// <summary>isinstance(v, int): a bool counts, as it does in Python.</summary>
    public static bool IsIntOrBool(object? v) => v is bool || IsInt(v);

    /// <summary>isinstance(v, (int, float)) and not a bool.</summary>
    public static bool IsNumber(object? v) => IsInt(v) || v is double;

    /// <summary>v > 0 for a number; NaN is not above zero.</summary>
    public static bool AboveZero(object? v) => v switch
    {
        int n => n > 0,
        long n => n > 0,
        BigInteger n => n.Sign > 0,
        double d => d > 0,
        bool b => b,
        _ => throw new PyError($"'>' not supported between instances of '{TypeName(v)}' and 'int'"),
    };

    // A number as the exact value Python compares: an int (bools included) or a float.
    static bool Number(object? v, out BigInteger whole, out double real, out bool isFloat)
    {
        whole = default; real = 0; isFloat = false;
        switch (v)
        {
            case bool b: whole = b ? 1 : 0; return true;
            case int n: whole = n; return true;
            case long n: whole = n; return true;
            case BigInteger n: whole = n; return true;
            case double d: real = d; isFloat = true; return true;
            default: return false;
        }
    }

    /// <summary>a == b: numbers by value (1 == 1.0 == True), text by code unit, lists and dicts by content.</summary>
    public static bool Eq(object? a, object? b)
    {
        if (Number(a, out var wa, out double ra, out bool fa))
        {
            if (!Number(b, out var wb, out double rb, out bool fb)) return false;
            if (!fa && !fb) return wa == wb;
            if (fa && fb) return ra == rb;
            double r = fa ? ra : rb;
            var w = fa ? wb : wa;
            return double.IsFinite(r) && r == Math.Truncate(r) && new BigInteger(r) == w;
        }
        switch (a)
        {
            case null: return b == null;
            case string s: return b is string t && s == t;
            case PyDict d:
                return b is PyDict e && d.Count == e.Count && d.All(kv => e.ContainsKey(kv.Key) && Eq(kv.Value, e[kv.Key]));
            case PyTuple x: return b is PyTuple y && x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Eq(p.First, p.Second));
            case List<object?> l: return b is List<object?> m && l.Count == m.Count && l.Zip(m).All(p => Eq(p.First, p.Second));
            default: return false;
        }
    }

    /// <summary>v in a list or tuple: the first item equal to v.</summary>
    public static bool In(object? v, IEnumerable<object?> items) => items.Any(x => Eq(v, x));

    /// <summary>v in a set of strings: a list or dict cannot be hashed, as in Python.</summary>
    public static bool InSet(object? v, IReadOnlySet<string> set)
    {
        Hash(v);
        return v is string s && set.Contains(s);
    }

    /// <summary>The key a value has in a set or dict: equal values share it; a list or dict has none.</summary>
    public static string Hash(object? v)
    {
        if (v is PyDict or List<object?>) throw new PyError($"unhashable type: '{TypeName(v)}'");
        if (v is PyTuple t) return "t(" + string.Join(",", t.Items.Select(Hash)) + ")";
        if (v is string s) return "s" + s;
        if (v == null) return "none";
        Number(v, out var w, out double r, out bool f);
        if (f && double.IsFinite(r) && r == Math.Truncate(r)) { w = new BigInteger(r); f = false; }
        return f ? "f" + r.ToString("R", Inv) : "i" + w.ToString(Inv);
    }

    /// <summary>iter(v): a list's items, a string's characters, a dict's keys; anything else is not iterable.</summary>
    public static IEnumerable<object?> Iter(object? v) => v switch
    {
        List<object?> l => l,
        PyTuple t => t.Items,
        PyDict d => d.Keys,
        string s => CodePoints(s),
        _ => throw new PyError($"'{TypeName(v)}' object is not iterable"),
    };

    static IEnumerable<object?> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            bool pair = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            yield return pair ? s.Substring(i++, 2) : s[i].ToString();
        }
    }

    /// <summary>len(v).</summary>
    public static int Len(object? v) => v switch
    {
        List<object?> l => l.Count,
        PyTuple t => t.Items.Count,
        PyDict d => d.Count,
        string s => CodePoints(s).Count(),
        _ => throw new PyError($"object of type '{TypeName(v)}' has no len()"),
    };

    /// <summary>v.get: the dict itself, or Python's error for a value that has no get.</summary>
    public static PyDict Dict(object? v) =>
        v as PyDict ?? throw new PyError($"'{TypeName(v)}' object has no attribute 'get'");

    /// <summary>int(v): an int as it is, a bool as 0 or 1, a float cut toward zero, text read as a whole number.</summary>
    public static BigInteger Int(object? v)
    {
        switch (v)
        {
            case bool b: return b ? 1 : 0;
            case int n: return n;
            case long n: return n;
            case BigInteger n: return n;
            case double d:
                if (double.IsNaN(d)) throw new PyError("cannot convert float NaN to integer");
                if (double.IsInfinity(d)) throw new PyError("cannot convert float infinity to integer");
                return new BigInteger(Math.Truncate(d));
            case string s:
                return IntText(s) ?? throw new PyError($"invalid literal for int() with base 10: {PyText.Repr(s)}");
            default:
                throw new PyError($"int() argument must be a string, a bytes-like object or a real number, not '{TypeName(v)}'");
        }
    }

    /// <summary>int(text): surrounding whitespace, a sign, any Unicode decimal digits, single underscores between digits.</summary>
    public static BigInteger? IntText(string text)
    {
        // int() drops Unicode spaces and the ASCII ones C calls space; U+001C to U+001F stay, and are not digits.
        int from = 0, to = text.Length;
        while (from < to && IsSpace(text[from]) && text[from] is not (>= '\x1c' and <= '\x1f')) from++;
        while (to > from && IsSpace(text[to - 1]) && text[to - 1] is not (>= '\x1c' and <= '\x1f')) to--;
        string s = text[from..to];
        int i = 0;
        bool neg = false;
        if (i < s.Length && s[i] is '+' or '-') neg = s[i++] == '-';
        var n = BigInteger.Zero;
        bool digit = false;
        for (; i < s.Length; i++)
        {
            if (s[i] == '_')
            {
                if (!digit || i + 1 >= s.Length) return null;
                digit = false;
                continue;
            }
            int d = CharUnicodeInfo.GetDecimalDigitValue(s, i);
            if (d < 0) return null;
            if (char.IsHighSurrogate(s[i])) i++;
            n = n * 10 + d;
            digit = true;
        }
        return digit ? (neg ? -n : n) : null;
    }

    /// <summary>str.isdigit(): not empty, and every character a digit.</summary>
    public static bool IsDigit(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            if (CharUnicodeInfo.GetDigitValue(s, i) < 0) return false;
            if (char.IsHighSurrogate(s[i])) i++;
        }
        return true;
    }

    /// <summary>a + b for two numbers: an int unless either is a float.</summary>
    public static object Add(object? a, object? b)
    {
        if (!Number(a, out var wa, out double ra, out bool fa) || !Number(b, out var wb, out double rb, out bool fb))
            throw new PyError($"unsupported operand type(s) for +: '{TypeName(a)}' and '{TypeName(b)}'");
        if (fa || fb) return (fa ? ra : (double)wa) + (fb ? rb : (double)wb);
        return Whole(wa + wb);
    }

    /// <summary>An int in the form the JSON model holds it: a long when it fits.</summary>
    public static object Whole(BigInteger n) => n >= long.MinValue && n <= long.MaxValue ? (long)n : n;

    // Python's whitespace, asked of the one list PyText.Split holds.
    static bool IsSpace(char c) => PyText.Split(c.ToString()).Count == 0;

    /// <summary>str.strip().</summary>
    public static string Strip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }
}
