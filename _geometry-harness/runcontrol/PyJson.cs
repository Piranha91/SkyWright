using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using HousecarlCore;

namespace RunControl;

/// <summary>A Python dict: string keys in insertion order. Setting an existing key keeps its place.</summary>
public sealed class PyDict : IEnumerable<KeyValuePair<string, object?>>
{
    readonly List<string> _order = [];
    readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);

    public PyDict() { }
    public PyDict(IEnumerable<KeyValuePair<string, object?>> items) { foreach (var kv in items) this[kv.Key] = kv.Value; }

    public int Count => _order.Count;
    public IReadOnlyList<string> Keys => _order;
    public bool ContainsKey(string key) => _map.ContainsKey(key);
    public bool TryGetValue(string key, out object? value) => _map.TryGetValue(key, out value);

    /// <summary>dict[key]; reading an absent key throws, as Python's KeyError does.</summary>
    public object? this[string key]
    {
        get => _map[key];
        set { if (!_map.ContainsKey(key)) _order.Add(key); _map[key] = value; }
    }

    /// <summary>dict.get(key): null when absent.</summary>
    public object? Get(string key) => _map.GetValueOrDefault(key);

    /// <summary>For collection initialisers; the same as setting the key.</summary>
    public void Add(string key, object? value) => this[key] = value;

    /// <summary>dict.pop(key, None).</summary>
    public object? Pop(string key)
    {
        if (!_map.Remove(key, out var v)) return null;
        _order.Remove(key);
        return v;
    }

    /// <summary>dict.update(other).</summary>
    public PyDict Update(PyDict other) { foreach (var kv in other) this[kv.Key] = kv.Value; return this; }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var k in _order.ToArray()) yield return new(k, _map[k]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Python's json module, byte for byte, for the forms the controllers write and hash.
/// Values are null, bool, long or BigInteger (an int), double (a float), string, List&lt;object?&gt;, PyTuple and PyDict.</summary>
public static class PyJson
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ---- writing

    /// <summary>json.dumps(v, indent, sort_keys, ensure_ascii); compact means separators=(",", ":").</summary>
    public static string Dumps(object? v, int? indent = null, bool sortKeys = false, bool ensureAscii = true, bool compact = false)
    {
        var sb = new StringBuilder();
        string item = compact || indent != null ? "," : ", ";
        Write(sb, v, indent, sortKeys, ensureAscii, item, compact ? ":" : ": ", 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? v, int? indent, bool sort, bool ascii, string item, string key, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s, ascii); break;
            case double d: sb.Append(PyText.FloatJson(d)); break;
            case float f: sb.Append(PyText.FloatJson(f)); break;
            case int or long or BigInteger: sb.Append(Convert.ToString(v, Inv)); break;
            case PyTuple t: Write(sb, t.Items.ToList(), indent, sort, ascii, item, key, depth); break;
            case PyDict d:
                if (d.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                var keys = sort ? PyOrder.SortedByCodePoint(d.Keys) : d.Keys;
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(item);
                    Break(sb, indent, depth + 1);
                    WriteString(sb, keys[i], ascii);
                    sb.Append(key);
                    Write(sb, d[keys[i]], indent, sort, ascii, item, key, depth + 1);
                }
                Break(sb, indent, depth);
                sb.Append('}');
                break;
            case IEnumerable<object?> seq:
                var list = seq as IReadOnlyList<object?> ?? seq.ToList();
                if (list.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(item);
                    Break(sb, indent, depth + 1);
                    Write(sb, list[i], indent, sort, ascii, item, key, depth + 1);
                }
                Break(sb, indent, depth);
                sb.Append(']');
                break;
            default: throw new ArgumentException($"a {v.GetType().Name} is not a JSON value here");
        }
    }

    static void Break(StringBuilder sb, int? indent, int depth)
    {
        if (indent is int n) sb.Append('\n').Append(' ', n * depth);
    }

    static void WriteString(StringBuilder sb, string s, bool ascii)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7E)) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>str.encode("utf-8"): a lone surrogate is an error, never a replacement character.</summary>
    public static byte[] Utf8(string text) => StrictUtf8.GetBytes(text);

    // ---- the named forms

    /// <summary>evalpipeline.sha256_json: sorted keys, compact, ASCII-escaped.</summary>
    public static string Sha256Json(object? v) =>
        Hashing.Sha256Text(Dumps(v, sortKeys: true, ensureAscii: true, compact: true));

    /// <summary>evalrun.canonical: sorted keys, compact, non-ASCII raw.</summary>
    public static string Canonical(object? v) => Dumps(v, sortKeys: true, ensureAscii: false, compact: true);

    /// <summary>evalpipeline.write_json: indent 2, insertion order, raw non-ASCII, a final newline; replaced atomically.</summary>
    public static void WritePipelineFile(string path, object? v) =>
        WriteText(path, Dumps(v, indent: 2, ensureAscii: false) + "\n", nativeNewlines: true);

    /// <summary>evalrun.write_json: indent 1, insertion order, raw non-ASCII, no final newline; replaced atomically.</summary>
    public static void WriteEvalFile(string path, object? v) =>
        WriteText(path, Dumps(v, indent: 1, ensureAscii: false), nativeNewlines: true);

    /// <summary>The population file's text: indent 1, sorted keys, ASCII-escaped, a final newline.</summary>
    public static string PopulationText(object? v) => Dumps(v, indent: 1, sortKeys: true, ensureAscii: true) + "\n";

    /// <summary>The population file as plan writes it: the text with "\n" line ends.</summary>
    public static void WritePopulationFile(string path, string text) => WriteText(path, text, nativeNewlines: false);

    // Python's text mode writes "\n" as the platform's line end; both write_json functions go through it.
    static void WriteText(string path, string text, bool nativeNewlines)
    {
        if (nativeNewlines && Environment.NewLine != "\n") text = text.Replace("\n", Environment.NewLine);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        AtomicFile.WriteAllBytes(full, Utf8(text));
    }

    // ---- reading

    /// <summary>evalpipeline.read_json: UTF-8 with an optional byte-order mark; an error throws.</summary>
    public static object? ReadPipelineFile(string path) => Loads(Decode(File.ReadAllBytes(path), bom: true));

    /// <summary>evalrun.load_json: UTF-8 without a byte-order mark; null when unreadable or not JSON.</summary>
    public static object? LoadEvalFile(string path)
    {
        try { return Loads(Decode(File.ReadAllBytes(path), bom: false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>bytes.decode("utf-8-sig" or "utf-8"): invalid bytes are an error.</summary>
    public static string Decode(byte[] bytes, bool bom)
    {
        int skip = bom && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return StrictUtf8.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException ex) { throw new FormatException("the file is not UTF-8: " + ex.Message); }
    }

    /// <summary>json.loads: NaN and Infinity accepted, a repeated key keeps its last value, an int stays an int.</summary>
    public static object? Loads(string text)
    {
        int i = 0;
        SkipSpace(text, ref i);
        object? v = Parse(text, ref i);
        SkipSpace(text, ref i);
        if (i != text.Length) throw Bad("extra data", i);
        return v;
    }

    static FormatException Bad(string what, int at) => new($"not JSON: {what} at character {at}");

    static void SkipSpace(string t, ref int i)
    {
        while (i < t.Length && t[i] is ' ' or '\t' or '\n' or '\r') i++;
    }

    static object? Parse(string t, ref int i)
    {
        if (i >= t.Length) throw Bad("a value is missing", i);
        char c = t[i];
        if (c == '"') return ParseString(t, ref i);
        if (c == '{')
        {
            var d = new PyDict();
            i++;
            SkipSpace(t, ref i);
            if (i < t.Length && t[i] == '}') { i++; return d; }
            while (true)
            {
                SkipSpace(t, ref i);
                if (i >= t.Length || t[i] != '"') throw Bad("a key must be a string", i);
                string k = ParseString(t, ref i);
                SkipSpace(t, ref i);
                if (i >= t.Length || t[i] != ':') throw Bad("':' expected", i);
                i++;
                SkipSpace(t, ref i);
                d[k] = Parse(t, ref i);
                SkipSpace(t, ref i);
                if (i < t.Length && t[i] == ',') { i++; continue; }
                if (i < t.Length && t[i] == '}') { i++; return d; }
                throw Bad("',' or '}' expected", i);
            }
        }
        if (c == '[')
        {
            var l = new List<object?>();
            i++;
            SkipSpace(t, ref i);
            if (i < t.Length && t[i] == ']') { i++; return l; }
            while (true)
            {
                SkipSpace(t, ref i);
                l.Add(Parse(t, ref i));
                SkipSpace(t, ref i);
                if (i < t.Length && t[i] == ',') { i++; continue; }
                if (i < t.Length && t[i] == ']') { i++; return l; }
                throw Bad("',' or ']' expected", i);
            }
        }
        if (Word(t, ref i, "null")) return null;
        if (Word(t, ref i, "true")) return true;
        if (Word(t, ref i, "false")) return false;
        if (Word(t, ref i, "NaN")) return double.NaN;
        if (Word(t, ref i, "Infinity")) return double.PositiveInfinity;
        if (Word(t, ref i, "-Infinity")) return double.NegativeInfinity;
        return ParseNumber(t, ref i);
    }

    static bool Word(string t, ref int i, string w)
    {
        if (string.CompareOrdinal(t, i, w, 0, w.Length) != 0) return false;
        i += w.Length;
        return true;
    }

    static object ParseNumber(string t, ref int i)
    {
        int s = i;
        if (i < t.Length && t[i] == '-') i++;
        if (i < t.Length && t[i] == '0') i++;
        else if (i < t.Length && t[i] is >= '1' and <= '9') { while (i < t.Length && t[i] is >= '0' and <= '9') i++; }
        else throw Bad("a value expected", s);
        bool isFloat = false;
        if (i + 1 < t.Length && t[i] == '.' && t[i + 1] is >= '0' and <= '9')
        {
            isFloat = true;
            i++;
            while (i < t.Length && t[i] is >= '0' and <= '9') i++;
        }
        if (i < t.Length && t[i] is 'e' or 'E')
        {
            int j = i + 1;
            if (j < t.Length && t[j] is '+' or '-') j++;
            if (j < t.Length && t[j] is >= '0' and <= '9')
            {
                isFloat = true;
                while (j < t.Length && t[j] is >= '0' and <= '9') j++;
                i = j;
            }
        }
        string num = t[s..i];
        if (isFloat) return double.Parse(num, NumberStyles.Float, Inv);
        return long.TryParse(num, NumberStyles.AllowLeadingSign, Inv, out long n) ? n : (object)BigInteger.Parse(num, Inv);
    }

    static string ParseString(string t, ref int i)
    {
        int open = i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= t.Length) throw Bad("a string does not end", open);
            char c = t[i++];
            if (c == '"') return sb.ToString();
            if (c < 0x20) throw Bad("a control character inside a string", i - 1);
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= t.Length) throw Bad("a string does not end", open);
            char e = t[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > t.Length || !ushort.TryParse(t.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, Inv, out ushort u))
                        throw Bad("a \\u escape needs four hex digits", i - 2);
                    sb.Append((char)u);
                    i += 4;
                    break;
                default: throw Bad("an unknown escape", i - 2);
            }
        }
    }

    // ---- reading values out

    /// <summary>evalrun.dotted: "a.b.0.c" read out of a document; null when any step is absent.</summary>
    public static object? Dotted(object? doc, string path)
    {
        object? cur = doc;
        foreach (string part in path.Split('.'))
        {
            if (cur is PyDict d && d.ContainsKey(part)) cur = d[part];
            else if (cur is List<object?> l && part.Length > 0 && part.All(ch => ch is >= '0' and <= '9')
                     && BigInteger.Parse(part, Inv) < l.Count) cur = l[(int)BigInteger.Parse(part, Inv)];
            else return null;
        }
        return cur;
    }
}
