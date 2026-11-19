using System.Text;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>A Python `re` pattern compiled by .NET. The registry keeps its patterns in Python's syntax; they are
/// rewritten here, never in the file.
///
/// What is mapped: (?P&lt;name&gt;...) to (?&lt;name&gt;...); (?P=name) to \k&lt;name&gt;; \Z to \z (end of text only);
/// \w and \s to the sets Python's Unicode rules give (letters, numbers and "_"; .NET's \s plus U+001C to U+001F);
/// \b and \B to the same test by that word set; {,n} to {0,n}; "[" inside a set to a literal; the flag letter u dropped. re.M is RegexOptions.Multiline: in both
/// engines "$" then matches at the end and before each "\n", and without it at the end or before one final "\n".
/// Case is compared by the invariant culture.
///
/// What cannot agree, and is refused where it can be seen: the flags a and L, \N{...} and \U........, \W or \S inside
/// a set, a numbered back-reference beside named groups (.NET numbers named groups last), a flag group after the
/// start. Not seen and not the same: possessive quantifiers; a character outside the basic plane counts as two
/// for ".", sets and positions; (?i) on the few letters with more than two case forms (sigma, long s, dotless i).</summary>
public static class PyRegex
{
    const string Word = @"\p{L}\p{N}_", Space = @"\s\x1c-\x1f";

    /// <summary>re.compile(pattern), with re.M when multiline.</summary>
    public static Regex Compile(string pattern, bool multiline = false) =>
        new(ToDotNet(pattern), RegexOptions.CultureInvariant | (multiline ? RegexOptions.Multiline : RegexOptions.None));

    /// <summary>re.search(pattern, text), with re.M when multiline.</summary>
    public static Match Search(string pattern, string text, bool multiline = false) => Compile(pattern, multiline).Match(text);

    /// <summary>The same pattern in .NET's syntax.</summary>
    public static string ToDotNet(string p)
    {
        var sb = new StringBuilder();
        bool inSet = false, named = false, unnamed = false, numbered = false;
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '\\' && i + 1 < p.Length)
            {
                char e = p[++i];
                if (e is 'N' or 'U') throw new NotSupportedException($"\\{e} has no .NET spelling (at position {i - 1})");
                if (e is 'W' or 'S' && inSet) throw new NotSupportedException($"\\{e} inside a set has no .NET spelling (at position {i - 1})");
                if (e is >= '1' and <= '9' && !inSet) numbered = true;
                sb.Append(e switch
                {
                    'Z' when !inSet => @"\z",
                    'b' when !inSet => $"(?:(?<=[{Word}])(?![{Word}])|(?<![{Word}])(?=[{Word}]))",
                    'B' when !inSet => $"(?:(?<=[{Word}])(?=[{Word}])|(?<![{Word}])(?![{Word}]))",
                    'w' => inSet ? Word : $"[{Word}]",
                    'W' => $"[^{Word}]",
                    's' => inSet ? Space : $"[{Space}]",
                    'S' => $"[^{Space}]",
                    _ => "\\" + e,
                });
                continue;
            }
            if (inSet)
            {
                if (c == ']') inSet = false;
                sb.Append(c == '[' ? "\\[" : c.ToString());
                continue;
            }
            if (c == '[')
            {
                inSet = true;
                sb.Append(c);
                if (i + 1 < p.Length && p[i + 1] == '^') sb.Append(p[++i]);
                if (i + 1 < p.Length && p[i + 1] == ']') { sb.Append("\\]"); i++; }
                continue;
            }
            if (c == '{' && i + 1 < p.Length && p[i + 1] == ',')
            {
                int j = i + 2;
                while (j < p.Length && p[j] is >= '0' and <= '9') j++;
                if (j > i + 2 && j < p.Length && p[j] == '}') { sb.Append("{0"); continue; }
            }
            if (c != '(') { sb.Append(c); continue; }
            if (i + 1 >= p.Length || p[i + 1] != '?') { unnamed = true; sb.Append(c); continue; }
            if (string.CompareOrdinal(p, i, "(?P<", 0, 4) == 0) { named = true; sb.Append("(?<"); i += 3; continue; }
            if (string.CompareOrdinal(p, i, "(?P=", 0, 4) == 0)
            {
                int close = p.IndexOf(')', i);
                if (close < 0) throw new ArgumentException($"missing ), unterminated name at position {i + 4}");
                sb.Append("\\k<").Append(p, i + 4, close - i - 4).Append('>');
                i = close;
                continue;
            }
            int k = i + 2;
            while (k < p.Length && p[k] is 'a' or 'i' or 'L' or 'm' or 's' or 'u' or 'x' or '-') k++;
            if (k == i + 2 || k >= p.Length || p[k] is not (')' or ':')) { sb.Append(c); continue; }
            string flags = p[(i + 2)..k];
            if (flags.Contains('a') || flags.Contains('L'))
                throw new NotSupportedException($"the flags a and L have no .NET spelling (at position {i})");
            if (p[k] == ')' && i != 0) throw new ArgumentException($"global flags not at the start of the expression at position {i}");
            flags = flags.Replace("u", "");
            if (p[k] == ':') sb.Append("(?").Append(flags).Append(':');
            else if (flags.Length > 0) sb.Append("(?").Append(flags).Append(')');
            i = k;
        }
        if (numbered && named && unnamed)
            throw new NotSupportedException("a numbered back-reference beside named groups counts groups differently in .NET");
        return sb.ToString();
    }
}
