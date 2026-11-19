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

    /// <summary>bool(v): PyValue's rule.</summary>
    public static bool Truthy(object? v) => PyValue.Truthy(v);

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

    /// <summary>a == b: PyValue's rule.</summary>
    public static bool Eq(object? a, object? b) => PyValue.Eq(a, b);

    /// <summary>list(dict.fromkeys(items)): the first of each, in order.</summary>
    public static List<string> Unique(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(seen.Add).ToList();
    }
}
