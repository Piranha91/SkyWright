using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkbenchTests;

/// <summary>One way of writing in an equivalence run: its name, its Output Folder and its houseCARL data folder (the
/// only paths the normaliser replaces), the answers it gave call by call, what the app made of them, and the processes
/// it started.</summary>
internal sealed class Arm
{
    internal string Name = "", Out = "", Hc = "";
    internal readonly List<(string Tool, bool Flagged, string Text)> Answers = new();
    internal readonly List<string> Outcomes = new();
    internal IReadOnlyList<(int Pid, string? Image)> Started = Array.Empty<(int, string?)>();
}

/// <summary>The equivalence comparison (Gate A, section 9): per answer, per file, per record, never per total. The
/// normaliser makes four substitutions, each counted: the arm's Output Folder to &lt;OUT&gt;, its data folder to
/// &lt;HC&gt;, a JSON <c>render_ms</c> value to 0, and a duration in a note (<c>in 54 ms</c>) to <c>in 0 ms</c> (the
/// last two are timings; the fourth was added at Gate B, when the context fixture showed it). One reconciliation runs on
/// the server arm alone, also counted: <see cref="UndoBlockJoin"/>. In a file, only a <c>meta.ini</c>'s <c>created=</c>
/// line (a timestamp) is masked.</summary>
internal static class Equiv
{
    internal sealed record Counts(int Out, int Hc, int RenderMs, int NoteMs);

    /// <summary>The server sends an answer over 1 MiB as text blocks of 1 MiB (houseCARL's ResponseBlocks: "the joined
    /// text is the answer"), and the server path's client (PatchWriter.CallAsync) joins them with a line break, so its
    /// answer carries one extra line break at each block boundary. This takes exactly those out, and says how many.</summary>
    internal static (string Text, int Undone) UndoBlockJoin(string text)
    {
        const int block = 1 << 20;
        var sb = new StringBuilder(text.Length); int undone = 0, at = 0;
        while (text.Length - at > block && text[at + block] == '\n')
        {
            sb.Append(text, at, block); at += block + 1; undone++;
        }
        sb.Append(text, at, text.Length - at);
        return (sb.ToString(), undone);
    }

    internal static (string Text, Counts Counts) Normalise(string text, Arm arm)
    {
        int o = 0, h = 0, r = 0;
        string Sub(string s, string path, string token, ref int n)
        {
            if (path.Length == 0) return s;
            foreach (var spelling in new[] { path, path.Replace("\\", "\\\\"), path.Replace('\\', '/') }.Distinct())
            {
                int c = Regex.Matches(s, Regex.Escape(spelling), RegexOptions.IgnoreCase).Count;
                if (c == 0) continue;
                n += c;
                s = Regex.Replace(s, Regex.Escape(spelling), token, RegexOptions.IgnoreCase);
            }
            return s;
        }
        // the longer path first, so a data folder under the Output Folder is not half replaced
        foreach (var (path, token, which) in new[] { (arm.Out, "<OUT>", 0), (arm.Hc, "<HC>", 1) }.OrderByDescending(x => x.Item1.Length))
            text = which == 0 ? Sub(text, path, token, ref o) : Sub(text, path, token, ref h);
        // as an answer spells it ("render_ms"), and as an outcome that re-serialised a raw answer into a JSON string spells
        // it ("render_ms" from System.Text.Json, or \"render_ms\")
        const string renderMs = @"(\\u0022|\\?"")render_ms(\\u0022|\\?""): *[0-9]+";
        r = Regex.Matches(text, renderMs).Count;
        text = Regex.Replace(text, renderMs, "${1}render_ms${2}: 0");
        const string noteMs = @"\bin [0-9]+ ms\b";
        int m = Regex.Matches(text, noteMs).Count;
        text = Regex.Replace(text, noteMs, "in 0 ms");
        return (text, new Counts(o, h, r, m));
    }

    /// <summary>Every difference between the two arms' answers, by call: a different tool, a refusal on one side only,
    /// a flag on one side only, or a different text after the substitutions (both texts quoted).</summary>
    internal static List<string> CompareAnswers(Arm a, Arm b)
    {
        var diffs = new List<string>();
        if (a.Answers.Count != b.Answers.Count) diffs.Add($"{a.Name} gave {a.Answers.Count} answers and {b.Name} gave {b.Answers.Count}");
        for (int i = 0; i < Math.Min(a.Answers.Count, b.Answers.Count); i++)
        {
            var (ta, fa, xa) = a.Answers[i]; var (tb, fb, xb) = b.Answers[i];
            if (ta != tb) { diffs.Add($"call {i}: {a.Name} called {ta}, {b.Name} called {tb}"); continue; }
            var na = Normalise(a.Name == "server" ? UndoBlockJoin(xa).Text : xa, a).Text;
            var nb = Normalise(b.Name == "server" ? UndoBlockJoin(xb).Text : xb, b).Text;
            if (Refused(na) != Refused(nb)) diffs.Add($"call {i} ({ta}): {(Refused(na) ? a.Name : b.Name)} refused and the other answered: {Head(Refused(na) ? na : nb)}");
            else if (fa != fb) diffs.Add($"call {i} ({ta}): only {(fa ? a.Name : b.Name)} flagged it an error");
            else if (na != nb) diffs.Add($"call {i} ({ta}) differs:\n  {a.Name}: {Head(na)}\n  {b.Name}: {Head(nb)}\n  first difference at char {FirstDiff(na, nb)}: '{Around(na, FirstDiff(na, nb))}' / '{Around(nb, FirstDiff(na, nb))}'");
        }
        if (a.Outcomes.Count != b.Outcomes.Count) diffs.Add($"{a.Name} reached {a.Outcomes.Count} outcomes and {b.Name} {b.Outcomes.Count}");
        for (int i = 0; i < Math.Min(a.Outcomes.Count, b.Outcomes.Count); i++)
        {
            var oa = Normalise(a.Outcomes[i], a).Text; var ob = Normalise(b.Outcomes[i], b).Text;
            if (oa != ob) diffs.Add($"outcome {i} differs:\n  {a.Name}: {Head(oa)}\n  {b.Name}: {Head(ob)}");
        }
        return diffs;
    }

    /// <summary>houseCARL's two refusal shapes: a text answer that starts <c>error:</c>, a JSON answer with <c>"ok": false</c>.</summary>
    internal static bool Refused(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("error:", StringComparison.Ordinal)) return true;
        if (!t.StartsWith('{')) return false;
        try { using var d = JsonDocument.Parse(t); return d.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False; }
        catch (JsonException) { return false; }
    }

    /// <summary>Every difference between two folders: a file on one side only, by its relative path; a plugin's records
    /// (by type and FormKey) present on one side only, or whose bytes differ; any other file whose bytes differ (a
    /// <c>meta.ini</c> with its <c>created=</c> line masked).</summary>
    internal static List<string> CompareTrees(string a, string b, string nameA = "A", string nameB = "B")
    {
        var diffs = new List<string>();
        static IEnumerable<string> Files(string d) => Directory.Exists(d)
            ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(d, f)) : Enumerable.Empty<string>();
        var fa = Files(a).ToHashSet(StringComparer.OrdinalIgnoreCase); var fb = Files(b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fa.Except(fb, StringComparer.OrdinalIgnoreCase).Order()) diffs.Add($"only in {nameA}: {f}");
        foreach (var f in fb.Except(fa, StringComparer.OrdinalIgnoreCase).Order()) diffs.Add($"only in {nameB}: {f}");
        foreach (var f in fa.Intersect(fb, StringComparer.OrdinalIgnoreCase).Order())
        {
            byte[] x = File.ReadAllBytes(Path.Combine(a, f)), y = File.ReadAllBytes(Path.Combine(b, f));
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".esp" or ".esm" or ".esl")
            {
                var rx = Records(x, Path.GetFileName(f)); var ry = Records(y, Path.GetFileName(f));
                foreach (var k in rx.Keys.Except(ry.Keys)) diffs.Add($"{f}: record {k} only in {nameA}");
                foreach (var k in ry.Keys.Except(rx.Keys)) diffs.Add($"{f}: record {k} only in {nameB}");
                foreach (var k in rx.Keys.Intersect(ry.Keys).Where(k => !rx[k].AsSpan().SequenceEqual(ry[k])))
                    diffs.Add($"{f}: record {k} differs ({rx[k].Length} / {ry[k].Length} bytes, first difference at byte {FirstDiff(rx[k], ry[k])})");
                if (!x.AsSpan().SequenceEqual(y) && diffs.All(d => !d.StartsWith(f + ":")))
                    diffs.Add($"{f}: the files differ outside every record (group headers or order) ({x.Length} / {y.Length} bytes)");
            }
            else if (Path.GetFileName(f).Equals("meta.ini", StringComparison.OrdinalIgnoreCase))
            {
                static string Mask(byte[] z) => Regex.Replace(Encoding.UTF8.GetString(z), "(?m)^created=.*$", "created=<masked>");
                if (Mask(x) != Mask(y)) diffs.Add($"{f}: differs beyond its created= line");
            }
            else if (!x.AsSpan().SequenceEqual(y)) diffs.Add($"{f}: differs ({x.Length} / {y.Length} bytes)");
        }
        return diffs;
    }

    /// <summary>A plugin's records as written, keyed "TYPE XXXXXX:Master" (the FormID's master from its own MAST list;
    /// the plugin itself past them), each its whole record bytes (header and data). Groups are walked, not compared.</summary>
    internal static Dictionary<string, byte[]> Records(byte[] b, string self) => RecordSpans(b, self).ToDictionary(kv => kv.Key, kv => b.AsSpan(kv.Value.Start, kv.Value.Length).ToArray());

    /// <summary>Where each record sits in the file: its start and length.</summary>
    internal static Dictionary<string, (int Start, int Length)> RecordSpans(byte[] b, string self)
    {
        var masters = new List<string>();
        var spans = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        string Type(int at) => Encoding.ASCII.GetString(b, at, 4);
        int U32(int at) => BitConverter.ToInt32(b, at);
        // TES4: its MAST subrecords
        int tesData = U32(4);
        for (int at = 24; at < 24 + tesData;)
        {
            var t = Type(at); int len = BitConverter.ToUInt16(b, at + 4);
            if (t == "MAST") masters.Add(Encoding.UTF8.GetString(b, at + 6, len).TrimEnd('\0'));
            at += 6 + len;
        }
        string Key(int at)
        {
            uint fid = (uint)U32(at + 12); int idx = (int)(fid >> 24);
            var master = idx < masters.Count ? masters[idx] : self;
            var key = $"{Type(at)} {fid & 0xFFFFFF:X6}:{master}";
            for (int n = 2; spans.ContainsKey(key); n++) key = $"{Type(at)} {fid & 0xFFFFFF:X6}:{master} #{n}";
            return key;
        }
        void Walk(int from, int to)
        {
            for (int at = from; at < to;)
            {
                if (Type(at) == "GRUP") { int size = U32(at + 4); Walk(at + 24, at + size); at += size; }
                else { int size = 24 + U32(at + 4); spans[Key(at)] = (at, size); at += size; }
            }
        }
        Walk(24 + tesData, b.Length);
        return spans;
    }

    static int FirstDiff(string a, string b) { int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }
    static int FirstDiff(byte[] a, byte[] b) { int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }
    static string Around(string s, int i) => s.Substring(Math.Max(0, i - 30), Math.Min(s.Length - Math.Max(0, i - 30), 80)).Replace("\n", " ");
    static string Head(string s) { var l = s.Replace("\r", "").Replace("\n", " "); return l.Length > 220 ? l[..220] + "…" : l; }

    /// <summary>Writes an arm's answers, outcomes and processes under WB_EVIDENCE_DIR/equivalence/&lt;fixture&gt;/, with the
    /// differences found, when that variable is set.</summary>
    internal static void Keep(string fixture, Arm a, Arm b, IReadOnlyList<string> diffs, IEnumerable<(string Name, string Dir)> trees)
    {
        if (Environment.GetEnvironmentVariable("WB_EVIDENCE_DIR") is not { Length: > 0 } root) return;
        var dir = Path.Combine(root, "equivalence", fixture);
        Directory.CreateDirectory(dir);
        foreach (var arm in new[] { a, b })
        {
            var rows = arm.Answers.Select((x, i) =>
            {
                var (joined, undone) = arm.Name == "server" ? UndoBlockJoin(x.Text) : (x.Text, 0);
                var (t, c) = Normalise(joined, arm);
                return new { call = i, tool = x.Tool, flagged = x.Flagged, refused = Refused(t), substitutions = c, blockJoinsUndone = undone, chars = x.Text.Length, text = t };
            });
            File.WriteAllText(Path.Combine(dir, arm.Name + "-answers.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllLines(Path.Combine(dir, arm.Name + "-outcomes.txt"), arm.Outcomes.Select(o => Normalise(o, arm).Text));
            File.WriteAllLines(Path.Combine(dir, arm.Name + "-processes.txt"), arm.Started.Select(s => $"{s.Pid} {s.Image ?? "(exited before it was named)"}"));
        }
        foreach (var (name, tree) in trees)
            if (Directory.Exists(tree))
                foreach (var f in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(dir, name, Path.GetRelativePath(tree, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                }
        File.WriteAllLines(Path.Combine(dir, "DIFFERENCES.txt"), diffs.Count == 0 ? new[] { "none" } : diffs);
    }
}
