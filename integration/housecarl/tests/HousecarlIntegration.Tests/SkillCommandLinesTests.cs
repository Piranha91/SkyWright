using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SkyWright.Engine.Tests;

/// <summary>Every command line in the world-edit-compatibility skill resolves: each program, verb, flag, MCP tool,
/// parameter and path it names exists, checked against the programs' own option parsing.
/// <list type="bullet">
/// <item>houseCARL's MCP tools: the [McpServerTool] methods of the module's built housecarl-mcp.dll, their parameters,
/// and the properties of a parameter whose type is a class (plugins= is RecordsScope). A JsonElement parameter's members
/// cannot be read off its type; they are listed as unchecked, by name.</item>
/// <item>Python tools: argparse's own --help, per subcommand.</item>
/// <item>evalcontrol: the per-verb table its parser consults (Args.Takes), from the module's built evalcontrol.dll.</item>
/// <item>runcontrol, switchprobe, nearprobe (plain args[i] parsers, no per-verb table): their row in docs/run-coverage.json,
/// which the tool-index guard keeps equal, both ways, to the flags houseCARL's generator reads out of the parser's source.
/// That checks the program's flag set, not which verb takes which flag.</item>
/// <item>Paths: SW/… in the checkout, references/… beside the skill, Data/… in the app's build, other relative paths in
/// the checkout or houseCARL's pin; markdown link targets beside the skill.</item>
/// </list>
/// A lone flag or parameter span is checked against the programs or tools named in code spans of the same paragraph.</summary>
public sealed class SkillCommandLinesTests
{
    readonly ITestOutputHelper _out;
    public SkillCommandLinesTests(ITestOutputHelper output) => _out = output;

    static string Checkout()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SkyWright.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no checkout above the test folder");
    }

    static string ModuleBuild() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    static string Configuration() => new DirectoryInfo(ModuleBuild()).Name;
    static string SkillDir() => Path.Combine(Checkout(), "integration", "housecarl", "claude-plugin", "skills", "world-edit-compatibility");
    static string SkillText() => File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));

    static string ModuleTool(string name)
    {
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModuleBuild(), "housecarl-integration-layout.json")));
        return Path.GetDirectoryName(Path.Combine(ModuleBuild(), marker.RootElement.GetProperty("tool").GetString()!.Replace("{name}", name)))!;
    }

    // ---- what the tree knows -------------------------------------------------------------------------------------

    /// <summary>One program's option list: its verbs (empty when it has none) and the flags each verb takes (key "" when
    /// the list is the program's whole flag set).</summary>
    sealed record Options(string Program, string How, IReadOnlySet<string> Verbs, Func<string?, IReadOnlySet<string>> Flags);

    sealed record Tool(string Name, IReadOnlyDictionary<string, Type> Parameters);

    sealed class Known
    {
        public readonly Dictionary<string, Tool> Tools = new(StringComparer.Ordinal);
        public readonly Dictionary<string, JsonElement> Rows = new(StringComparer.Ordinal);
        readonly Dictionary<string, Options?> _options = new(StringComparer.Ordinal);
        readonly string _checkout;
        public int TypesNotLoaded { get; }

        public Known()
        {
            _checkout = Checkout();
            // Read as metadata, not loaded: the server is built against other versions of the framework's libraries than
            // this test runs on. A type whose metadata would not resolve is counted (and printed), so a tool class among
            // them shows as a tool not found, never as a pass.
            var server = ModuleTool("housecarl-mcp");
            var paths = Directory.GetFiles(server, "*.dll").Concat(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
                .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First());
            var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
            var mcp = mlc.LoadFromAssemblyPath(Path.Combine(server, "housecarl-mcp.dll"));
            Type[] types;
            try { types = mcp.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; TypesNotLoaded = ex.Types.Count(t => t is null); }
            static Type Plain(Type t) => t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1" ? t.GetGenericArguments()[0] : t;
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var attr = m.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "McpServerToolAttribute");
                    if (attr?.NamedArguments.FirstOrDefault(n => n.MemberName == "Name").TypedValue.Value is not string name) continue;
                    Tools[name] = new Tool(name, m.GetParameters().Where(p => p.Name is not null)
                        .ToDictionary(p => p.Name!, p => Plain(p.ParameterType), StringComparer.Ordinal));
                }
            using var cov = JsonDocument.Parse(File.ReadAllText(Path.Combine(_checkout, "docs", "run-coverage.json")));
            foreach (var row in cov.RootElement.GetProperty("entryPoints").EnumerateArray())
                Rows[row.GetProperty("id").GetString()!] = row.Clone();
        }

        /// <summary>The row a program word names: its id, its path, or a script whose file name it is (the module's own
        /// first, then the one other row of that name; two others are ambiguous and name none).</summary>
        public string? RowOf(string word)
        {
            var w = word.Replace('\\', '/');
            if (w.StartsWith("SW/", StringComparison.Ordinal)) w = w[3..];
            if (Rows.ContainsKey(w)) return w;
            var file = w.Split('/')[^1];
            var byFile = new List<(string Id, string Path)>();
            foreach (var (id, row) in Rows)
                if (row.TryGetProperty("path", out var p) && p.GetString() is { } path)
                {
                    if (path == w) return id;
                    if (file.EndsWith(".py", StringComparison.Ordinal) && path.EndsWith("/" + file, StringComparison.Ordinal)) byFile.Add((id, path));
                }
            var own = byFile.Where(b => b.Path.StartsWith("integration/housecarl/", StringComparison.Ordinal)).ToList();
            return own.Count == 1 ? own[0].Id : byFile.Count == 1 ? byFile[0].Id : null;
        }

        public Options? OptionsOf(string id)
        {
            if (_options.TryGetValue(id, out var o)) return o;
            var row = Rows[id];
            string parser = row.TryGetProperty("flagCoverage", out var fc) ? fc.GetProperty("parser").GetString()! : "";
            if (parser == "argparse")
            {
                // argparse's own --help when the script starts here; a script whose imports this python lacks (numpy)
                // is checked by its run-coverage row instead, which the tool-index guard keeps equal to its parser, and
                // the fallback is said in the output
                try { o = Argparse(id, row.GetProperty("path").GetString()!); }
                catch (InvalidOperationException ex)
                {
                    o = FromRow(id, row) with { How = "its run-coverage row (python --help could not run here: " + ex.Message.Split('\n').Last(s => s.Trim().Length > 0).Trim() + ")" };
                }
            }
            else if (id == "evalcontrol") o = Evalcontrol();
            else if (parser == "csharp-args") o = FromRow(id, row);
            _options[id] = o;
            return o;
        }

        static Options FromRow(string id, JsonElement row)
        {
            var verbs = new HashSet<string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            if (row.TryGetProperty("capabilities", out var caps))
                foreach (var c in caps.EnumerateArray())
                    if (c.TryGetProperty("selector", out var s) && s.TryGetProperty("tokens", out var toks))
                        foreach (var t in toks.EnumerateArray()) (t.GetString()!.StartsWith("--") ? flags : verbs).Add(t.GetString()!);
            if (row.TryGetProperty("modifiers", out var mods)) foreach (var m in mods.EnumerateObject()) flags.Add(m.Name);
            return new Options(id, "its run-coverage row (the tool-index guard keeps it equal to its parser's flags)", verbs, _ => flags);
        }

        Options Evalcontrol()
        {
            var asm = Assembly.LoadFrom(Path.Combine(ModuleTool("evalcontrol"), "evalcontrol.dll"));
            var takes = (Dictionary<string, string[]>)asm.GetType("EvalControl.Args", true)!
                .GetField("Takes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var verbs = new HashSet<string>(takes.Keys, StringComparer.Ordinal) { "switchflip-outcome", "--self-test" };
            return new Options("evalcontrol", "its per-verb table (Args.Takes)", verbs,
                v => v is not null && takes.TryGetValue(v, out var f) ? f.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(takes.Values.SelectMany(x => x), StringComparer.Ordinal));
        }

        Options Argparse(string id, string path)
        {
            string script = Path.Combine(_checkout, path);
            var top = Help(script);
            var verbs = Regex.Match(top, @"\{([a-z0-9_,-]+)\}") is { Success: true } m ? m.Groups[1].Value.Split(',').ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var cache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            IReadOnlySet<string> FlagsOf(string? verb)
            {
                var key = verb ?? "";
                if (cache.TryGetValue(key, out var f)) return f;
                var text = verb is null ? top : Help(script, verb);   // the top level started, so a verb's help does too
                return cache[key] = Regex.Matches(text, @"(?<![\w-])--[a-z0-9][a-z0-9-]*").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
            }
            return new Options(id, "argparse's --help", verbs, FlagsOf);
        }

        static string Help(string script, params string[] verb)
        {
            var psi = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.ArgumentList.Add(script);
            foreach (var v in verb) psi.ArgumentList.Add(v);
            psi.ArgumentList.Add("--help");
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { p.Kill(true); throw new TimeoutException($"python {script} --help did not finish"); }
            if (p.ExitCode != 0) throw new InvalidOperationException($"python {Path.GetFileName(script)} {string.Join(' ', verb)} --help exited {p.ExitCode}: {e.Result}");
            return o.Result;
        }
    }

    // ---- reading the skill ---------------------------------------------------------------------------------------

    /// <summary>One thing to check: its text, the line it starts on, whether it was a fenced line, the code spans of its
    /// paragraph, and the lines of the nearest fenced block above it in the same section (a lone flag or parameter is
    /// attributed to a program or tool its paragraph names, else to one that block names).</summary>
    sealed record Line(string Text, int At, bool Fenced, IReadOnlyList<string> Paragraph, IReadOnlyList<string> FenceAbove);

    static List<Line> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<Line>();
        var para = new List<(string Text, int At)>();
        bool fence = false;
        var fenced = new List<(string, int)>();
        IReadOnlyList<string> above = Array.Empty<string>();
        void Flush()
        {
            if (para.Count == 0) return;
            var joined = string.Join("\n", para.Select(p => p.Text));
            var spans = new List<(string, int)>();
            foreach (Match m in Regex.Matches(joined, "`([^`]+)`"))
            {
                int at = para[0].At + joined[..m.Index].Count(c => c == '\n');
                spans.Add((Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim(), at));
            }
            foreach (Match m in Regex.Matches(joined, @"\]\(([^)\s]+)\)"))
                spans.Add(("link:" + m.Groups[1].Value, para[0].At + joined[..m.Index].Count(c => c == '\n')));
            var names = spans.Select(s => s.Item1).ToList();
            foreach (var (s, at) in spans) result.Add(new Line(s, at, false, names, above));
            para.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (fence)
                {
                    // a shell line continued with a trailing backslash is one command
                    var joinedLines = new List<(string Text, int At)>();
                    foreach (var (t, at) in fenced)
                        if (joinedLines.Count > 0 && joinedLines[^1].Text.EndsWith('\\'))
                            joinedLines[^1] = (joinedLines[^1].Text[..^1].TrimEnd() + " " + t.Trim(), joinedLines[^1].At);
                        else joinedLines.Add((t.Trim(), at));
                    var block = joinedLines.Select(f => f.Text).ToList();
                    foreach (var (t, at) in joinedLines) if (t.Length > 0) result.Add(new Line(t, at, true, block, above));
                    above = block;
                    fenced.Clear();
                }
                else Flush();
                fence = !fence;
                continue;
            }
            if (fence) { fenced.Add((l, i + 1)); continue; }
            if (l.Trim().Length == 0) { Flush(); continue; }
            if (l.StartsWith('#')) { Flush(); above = Array.Empty<string>(); continue; }
            para.Add((l, i + 1));
        }
        Flush();
        return result;
    }

    static List<string> Words(string s)
    {
        var words = new List<string>();
        foreach (Match m in Regex.Matches(s, "\"[^\"]*\"|'[^']*'|\\S+")) words.Add(m.Value);
        return words;
    }

    static bool Placeholder(string w) => w.Contains('<') || w.Contains('…') || w == "..." || w.Contains('*') || w.Contains('$');

    /// <summary>The top-level keys of an object literal as the skill writes it ({"names": [...], "defined_in": true}).</summary>
    static List<string> ObjectKeys(string literal)
    {
        var keys = new List<string>();
        int depth = 0;
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == '"')
            {
                int end = literal.IndexOf('"', i + 1);
                if (end < 0) break;
                var after = literal[(end + 1)..].TrimStart();
                if (depth == 1 && after.StartsWith(':')) keys.Add(literal[(i + 1)..end]);
                i = end;
            }
        }
        return keys;
    }

    /// <summary>key=value pairs of an MCP call's argument list, at the top level of the brackets.</summary>
    static List<(string Key, string Value)> Arguments(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0; bool quote = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '"') quote = !quote;
            if (quote) continue;
            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(args[start..i]); start = i + 1; }
        }
        parts.Add(args[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0)
            .Select(p => p.IndexOf('=') is var e && e > 0 ? (p[..e].Trim(), p[(e + 1)..].Trim()) : (p, "")).ToList();
    }

    // ---- the resolver -------------------------------------------------------------------------------------------

    sealed class Report
    {
        public readonly List<string> Problems = new();
        public readonly List<string> Checked = new();
        public readonly SortedSet<string> Unchecked = new(StringComparer.Ordinal);
    }

    static Report Resolve(string skillText, string skillDir, Known k)
    {
        var r = new Report();
        string checkout = Checkout();
        string appData = Path.Combine(checkout, "bin", Configuration(), "net9.0-windows10.0.19041.0", "Data");

        void Parameter(Line l, Tool tool, string key, string value)
        {
            if (!tool.Parameters.TryGetValue(key, out var type)) { r.Problems.Add($"line {l.At}: {tool.Name} has no parameter {key}= ({l.Text})"); return; }
            r.Checked.Add($"line {l.At}: {tool.Name} {key}= (houseCARL's tool method)");
            if (!value.TrimStart().StartsWith('{')) return;
            var members = ObjectKeys(value.Trim());
            if (type.Name == "JsonElement") { foreach (var m in members) r.Unchecked.Add($"{tool.Name} {key}={{\"{m}\"}} (a JsonElement parameter)"); return; }
            if (!type.IsClass || type.FullName == "System.String" || type.IsArray) { r.Problems.Add($"line {l.At}: {tool.Name} {key}= takes {type.Name}, not an object ({l.Text})"); return; }
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var m in members)
                if (!props.Contains(m)) r.Problems.Add($"line {l.At}: {tool.Name} {key}= has no member \"{m}\" ({type.Name}; {l.Text})");
        }

        void Program(Line l, string id, List<string> words)
        {
            var o = k.OptionsOf(id);
            if (o is null) { r.Problems.Add($"line {l.At}: {id} has no option list this check can read (no flagCoverage in its run-coverage row)"); return; }
            string? verb = null;
            int i = 0;
            if (o.Verbs.Count > 0 && words.Count > 0 && !words[0].StartsWith("--") && !Placeholder(words[0]))
            {
                verb = words[0]; i = 1;
                if (!o.Verbs.Contains(verb)) { r.Problems.Add($"line {l.At}: {id} has no command '{verb}' (by {o.How}; {l.Text})"); return; }
            }
            var flags = o.Flags(verb);
            for (; i < words.Count; i++)
                if (words[i].StartsWith("--") && !Placeholder(words[i]))
                {
                    var f = words[i].Split('=')[0];
                    if (!flags.Contains(f)) r.Problems.Add($"line {l.At}: {id}{(verb is null ? "" : " " + verb)} takes no {f} (by {o.How}; {l.Text})");
                    else r.Checked.Add($"line {l.At}: {id}{(verb is null ? "" : " " + verb)} {f} (by {o.How})");
                }
        }

        /// <summary>The program or tool a span names first, if any: (kind, name, the words after it).</summary>
        (string Kind, string Name, List<string> After)? Head(string text)
        {
            var call = Regex.Match(text, @"^([a-z_]+)\((.*)\)\s*$", RegexOptions.Singleline);
            if (call.Success && k.Tools.ContainsKey(call.Groups[1].Value)) return ("call", call.Groups[1].Value, new() { call.Groups[2].Value });
            var words = Words(text);
            if (words.Count == 0) return null;
            int s = words[0] == "python" ? 1 : 0;
            if (s >= words.Count) return null;
            if (k.Tools.ContainsKey(words[s])) return ("tool", words[s], words.Skip(s + 1).ToList());
            if (k.RowOf(words[s]) is { } id) return ("program", id, words.Skip(s + 1).ToList());
            return null;
        }

        foreach (var l in Lines(skillText))
        {
            var text = l.Text;
            if (text.StartsWith("link:", StringComparison.Ordinal))
            {
                var target = text[5..].Split('#')[0];
                if (target.Length == 0 || target.Contains("://")) continue;
                if (!File.Exists(Path.Combine(skillDir, target)) && !Directory.Exists(Path.Combine(skillDir, target))) r.Problems.Add($"line {l.At}: the link target {target} is not beside the skill");
                else r.Checked.Add($"line {l.At}: link {target}");
                continue;
            }
            var head = Head(text);
            if (head is { Kind: "call" } c)
            {
                foreach (var (key, value) in Arguments(c.After[0])) Parameter(l, k.Tools[c.Name], key, value);
                continue;
            }
            if (head is { Kind: "tool" } t)
            {
                foreach (var w in t.After.Where(w => w.Contains('=')))
                    Parameter(l, k.Tools[t.Name], w.Split('=')[0], w[(w.IndexOf('=') + 1)..]);
                continue;
            }
            if (head is { Kind: "program" } p) { Program(l, p.Name, p.After); CheckPaths(l, Words(text)); continue; }

            // A lone parameter (source=, project={...}) or flag (--depth): the tools or programs its paragraph names, else
            // those the nearest fenced block above it in its section names.
            List<string> Named(Func<string, bool> kind)
            {
                foreach (var source in new[] { l.Paragraph, l.FenceAbove })
                {
                    var found = source.Where(s => s != text).Select(Head).Where(h => h is not null && kind(h.Value.Kind)).Select(h => h!.Value.Name).Distinct().ToList();
                    if (found.Count > 0) return found;
                }
                return new List<string>();
            }
            if (Regex.IsMatch(text, @"^[a-z_]+=") && !l.Fenced)
            {
                var tools = Named(kind => kind is "call" or "tool");
                var key = text[..text.IndexOf('=')];
                if (tools.Count == 0) { r.Problems.Add($"line {l.At}: the parameter `{text}` names no houseCARL tool in its paragraph or the block above it, so which tool takes it cannot be checked"); continue; }
                var owner = tools.FirstOrDefault(tn => k.Tools[tn].Parameters.ContainsKey(key));
                if (owner is null) r.Problems.Add($"line {l.At}: none of {string.Join(", ", tools)} takes {key}= ({text})");
                else Parameter(l, k.Tools[owner], key, text[(text.IndexOf('=') + 1)..]);
                continue;
            }
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var flag = Words(text)[0].Split('=')[0];
                var programs = Named(kind => kind == "program");
                if (programs.Count == 0) { r.Problems.Add($"line {l.At}: the flag `{flag}` names no program in its paragraph or the block above it, so which program takes it cannot be checked"); continue; }
                var owner = programs.FirstOrDefault(pn => k.OptionsOf(pn) is { } o && (o.Flags(null).Contains(flag) || o.Verbs.Any(v => o.Flags(v).Contains(flag))));
                if (owner is null) r.Problems.Add($"line {l.At}: none of {string.Join(", ", programs)} takes {flag}");
                else r.Checked.Add($"line {l.At}: {owner} {flag} (attributed by its paragraph or the block above)");
                continue;
            }
            var words = Words(text);
            if (!l.Fenced && words.Count > 1 && words.Skip(1).Any(w => w.StartsWith("--")) && Regex.IsMatch(words[0], @"^[\w./-]+$"))
                r.Problems.Add($"line {l.At}: `{text}` reads as a command, but '{words[0]}' is no program or tool the tree knows");
            else if (l.Fenced && !text.StartsWith('#'))
                r.Problems.Add($"line {l.At}: the fenced line `{text}` names no program or tool the tree knows");
            CheckPaths(l, words);
        }
        return r;

        void CheckPaths(Line l, List<string> words)
        {
            foreach (var raw in words)
            {
                var w = raw.Trim('"', '\'', '(', ')', ',', '.', ';');
                if (Placeholder(w) || !w.Contains('/') || w.Contains("://") || w.Contains('=') || w.StartsWith('@')) continue;
                if (!Regex.IsMatch(w, @"\.[a-z]{2,5}/?$|/$")) continue;
                string[] bases = w.StartsWith("SW/", StringComparison.Ordinal) ? new[] { Path.Combine(checkout, w[3..]) }
                    : w.StartsWith("references/", StringComparison.Ordinal) ? new[] { Path.Combine(skillDir, w) }
                    : w.StartsWith("Data/", StringComparison.Ordinal) ? new[] { Path.Combine(appData, w[5..]) }
                    : new[] { Path.Combine(checkout, w), Path.Combine(checkout, "external", "houseCARL", w) };
                if (bases.Any(b => File.Exists(b) || Directory.Exists(b))) r.Checked.Add($"line {l.At}: path {w}");
                else r.Problems.Add($"line {l.At}: the path {w} does not exist (looked at {string.Join(", ", bases)})");
            }
        }
    }

    void Print(Report r)
    {
        foreach (var c in r.Checked) _out.WriteLine("checked   " + c);
        foreach (var u in r.Unchecked) _out.WriteLine("unchecked " + u);
        foreach (var p in r.Problems) _out.WriteLine("PROBLEM   " + p);
    }

    // ---- the cases ------------------------------------------------------------------------------------------------

    [Fact]
    public void No_skill_of_the_module_names_houseCARLs_dropped_near_option()
    {
        var skills = Path.Combine(Checkout(), "integration", "housecarl", "claude-plugin", "skills");
        var hits = Directory.GetFiles(skills, "*.md", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)).Where(x => x.l.Contains("near=")))
            .Select(x => $"{Path.GetRelativePath(skills, x.f)}:{x.i + 1}").ToList();
        Assert.True(hits.Count == 0, "near= is still named at " + string.Join(", ", hits));
    }

    [Fact]
    public void Every_command_line_in_world_edit_compatibility_resolves()
    {
        var k = new Known();
        _out.WriteLine($"houseCARL's MCP tools read: {k.Tools.Count} ({string.Join(", ", k.Tools.Keys.Order(StringComparer.Ordinal))}); types of housecarl-mcp.dll not loaded: {k.TypesNotLoaded}");
        var r = Resolve(SkillText(), SkillDir(), k);
        Print(r);
        Assert.True(r.Problems.Count == 0, $"{r.Problems.Count} command line part(s) do not resolve:\n" + string.Join("\n", r.Problems));
        Assert.True(r.Checked.Count > 0, "nothing was checked");
    }

    // The four reference files beside the skill hold no command line the check would have to read.
    [Fact]
    public void The_skills_references_hold_no_unresolved_command()
    {
        var k = new Known();
        foreach (var f in Directory.GetFiles(Path.Combine(SkillDir(), "references"), "*.md"))
        {
            var r = Resolve(File.ReadAllText(f), Path.GetDirectoryName(f)!, k);
            Print(r);
            Assert.True(r.Problems.Count == 0, $"{Path.GetFileName(f)}: " + string.Join("\n", r.Problems));
        }
    }

    // F4: the skill says how to find nearprobe from the module's layout marker, and following it reaches the program.
    [Fact]
    public void The_skill_says_how_to_find_nearprobe_from_the_modules_layout_marker()
    {
        var text = SkillText();
        var span = Regex.Match(text, @"`SW/integration/housecarl/bin/(\w+)/housecarl-integration-layout\.json`");
        Assert.True(span.Success, "the skill names no `SW/integration/housecarl/bin/<Configuration>/housecarl-integration-layout.json` to find nearprobe from");
        int at = span.Index;
        var around = text.Substring(Math.Max(0, at - 400), Math.Min(text.Length - Math.Max(0, at - 400), 1000));
        Assert.Contains("`program`", around);
        Assert.Contains("`{name}`", around);
        Assert.Contains("nearprobe", around);
        // the configuration it names when that build exists here, else this test's own (said in the output)
        var config = span.Groups[1].Value;
        var root = Path.Combine(Checkout(), "integration", "housecarl", "bin", config);
        if (!File.Exists(Path.Combine(root, "housecarl-integration-layout.json"))) { _out.WriteLine($"no {config} build of the module here; following it in {Configuration()}"); root = ModuleBuild(); }
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "housecarl-integration-layout.json")));
        var exe = Path.Combine(root, marker.RootElement.GetProperty("program").GetString()!.Replace("{name}", "nearprobe"));
        _out.WriteLine("the skill's instruction leads to " + exe);
        Assert.True(File.Exists(exe), $"following the skill's instruction gives {exe}, which does not exist");
    }

    // The resolver's own sabotage arm: one flag and one MCP parameter misspelt in a copy of the skill; both are named.
    [Fact]
    public void A_copy_with_a_misspelt_flag_and_parameter_fails_naming_both()
    {
        var text = SkillText();
        const string flag = "--within 0", param = "to_file=\"<abs>/seeds.jsonl\"";
        Assert.True(Regex.Matches(text, Regex.Escape(flag)).Count == 1, $"the skill should name `{flag}` exactly once (in step 3's nearprobe line)");
        Assert.True(Regex.Matches(text, Regex.Escape(param)).Count == 1, $"the skill should name `{param}` exactly once (in step 3's seeds line)");
        var bad = text.Replace(flag, "--withn 0").Replace(param, "tofile=\"<abs>/seeds.jsonl\"");
        var r = Resolve(bad, SkillDir(), new Known());
        Print(r);
        Assert.Contains(r.Problems, p => p.Contains("--withn"));
        Assert.Contains(r.Problems, p => p.Contains("tofile="));
    }
}
