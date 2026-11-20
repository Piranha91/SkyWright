namespace RunControl;

/// <summary>Python's orderings: sorted(), sorted(key=str.lower), sorted(paths), and path equality.</summary>
public static class PyOrder
{
    /// <summary>Compares two strings by Unicode code point, as Python compares str (not by UTF-16 unit).</summary>
    public static int CompareCodePoints(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = Next(a, ref i), cb = Next(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    static int Next(string s, ref int i)
    {
        char c = s[i++];
        if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) return char.ConvertToUtf32(c, s[i++]);
        return c;
    }

    public static readonly IComparer<string> CodePoint = Comparer<string>.Create(CompareCodePoints);

    /// <summary>sorted(strings).</summary>
    public static List<string> SortedByCodePoint(IEnumerable<string> items) => items.OrderBy(x => x, CodePoint).ToList();

    /// <summary>sorted(strings, key=str.lower): stable, so equal keys keep their input order.</summary>
    public static List<string> SortedByLowerKey(IEnumerable<string> items) => items.OrderBy(PyText.Lower, CodePoint).ToList();

    /// <summary>sorted(items, key=key) for any item with a string key, stable.</summary>
    public static List<T> SortedBy<T>(IEnumerable<T> items, Func<T, string> key) => items.OrderBy(key, CodePoint).ToList();

    /// <summary>sorted(WindowsPath list): by the lower-cased path's parts, part by part, stable.</summary>
    public static List<string> SortedPaths(IEnumerable<string> paths) =>
        paths.Select(p => (p, key: PyText.Lower(PyPath.Str(p)).Split('\\'))).OrderBy(x => x.key, Parts).Select(x => x.p).ToList();

    static readonly IComparer<string[]> Parts = Comparer<string[]>.Create((a, b) =>
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            int c = CompareCodePoints(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    });

    /// <summary>same_path: two spellings of one path, by normcase(normpath()).</summary>
    public static bool SamePath(string a, string b) => PyPath.NormCase(PyPath.NormPath(a)) == PyPath.NormCase(PyPath.NormPath(b));

    /// <summary>evalrun's same_path: as SamePath, and an empty or absent side is never the same.</summary>
    public static bool SamePathGiven(string? a, string? b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && SamePath(a, b);

    /// <summary>fwd: a path in forward-slash spelling.</summary>
    public static string Fwd(string path) => path.Replace('\\', '/');
}
