using System.Numerics;

namespace RunControl;

/// <summary>Reading, copying and comparing Python values held as PyDict, List and scalars.</summary>
public static class Py
{
    public static PyDict D(object? v) => (PyDict)v!;
    /// <summary>`x or {}` for a value that is a dict or absent.</summary>
    public static PyDict DictOrEmpty(object? v) => v as PyDict ?? new PyDict();
    public static List<object?> L(object? v) => (List<object?>)v!;
    /// <summary>`x or []` for a value that is a list or absent.</summary>
    public static List<object?> ListOrEmpty(object? v) => v as List<object?> ?? [];
    public static string S(object? v) => (string)v!;
    public static List<string> Strs(object? v) => ListOrEmpty(v).Select(x => (string)x!).ToList();
    public static List<object?> Objs(IEnumerable<string> items) => items.Cast<object?>().ToList();

    /// <summary>bool(v).</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false, bool b => b, string s => s.Length > 0, long n => n != 0, int n => n != 0, double d => d != 0,
        BigInteger n => !n.IsZero, PyDict d => d.Count > 0, List<object?> l => l.Count > 0, PyTuple t => t.Items.Count > 0,
        _ => true,
    };

    /// <summary>float(v) for a JSON number.</summary>
    public static double Float(object? v) => v switch
    {
        double d => d, long n => n, int n => n, BigInteger n => (double)n, bool b => b ? 1 : 0,
        _ => throw new Refusal($"a number was expected, and the value is {PyText.Repr(v)}."),
    };

    /// <summary>json.loads(json.dumps(v)): a copy that shares nothing; a tuple becomes a list.</summary>
    public static object? Copy(object? v) => v switch
    {
        PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, Copy(kv.Value)))),
        List<object?> l => l.Select(Copy).ToList(),
        PyTuple t => t.Items.Select(Copy).ToList(),
        _ => v,
    };

    /// <summary>a == b: dicts by key and value whatever their order, lists in order, numbers by value.</summary>
    public static bool Eq(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa) return b is string sb && sa == sb;
        if (a is PyDict da)
            return b is PyDict db && da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var o) && Eq(kv.Value, o));
        if (a is List<object?> la)
            return b is List<object?> lb && la.Count == lb.Count && la.Zip(lb).All(p => Eq(p.First, p.Second));
        if (b is string or PyDict or List<object?>) return false;
        if (a is double || b is double) return Float(a) == Float(b);
        return Whole(a) == Whole(b);
    }

    static BigInteger Whole(object v) => v switch
    {
        long n => n, int n => n, BigInteger n => n, bool b => b ? 1 : 0,
        _ => throw new ArgumentException($"no Python equality is defined for a {v.GetType().Name}"),
    };

    /// <summary>list(dict.fromkeys(items)): the first of each, in order.</summary>
    public static List<string> Unique(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(seen.Add).ToList();
    }
}
