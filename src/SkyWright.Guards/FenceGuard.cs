using System.Text;
using System.Text.RegularExpressions;
using HarnessShared;

namespace SkyWright.Guards;

/// <summary>
/// The fence around the houseCARL integration module, <c>integration/housecarl/</c>: nothing outside it refers into it.
/// The module may use SkyWright; the app, the probes, runcontrol and the rest of the tree never reach the module, so
/// SkyWright runs whole without it. Each reference found outside is one sentence naming the file, the line and what it
/// names. A crossing that cannot be closed yet is on <see cref="SkyWright"/>'s named list with its reason and the step
/// that removes it; an entry there that matches nothing fails, so a closed crossing leaves the list.
///
/// What it learns from the module: names only. Every module file's Python module name (each <c>.py</c> stem) and file
/// name (<c>x.py</c>, <c>.html</c>), its project and program names (each <c>.csproj</c> stem), its top-level folder names
/// (but <c>tests</c>, <c>bin</c> and <c>obj</c>, which name nothing of its own), and the folder's own path.
///
/// What counts as a reference, by kind of file outside the module:
///   .py    an import of a module name (anywhere, a function's own import too; <c>import_module</c> and <c>__import__</c>
///          with a literal name), or a string literal outside comments and docstrings that names a module file, folder,
///          program, or the module's path (a path built from separate pieces, "integration" then "housecarl", is not
///          seen unless a piece names a module file or folder: the one such builder, runcontrol's accessor, is read by
///          its name below);
///   .cs    a string literal (comments removed) that names any of those, or holds Python source importing a module name
///          (runcontrol's self-test hands Python code to python -c); and the one accessor runcontrol has for the
///          module's source folder, <c>Checkout.Integration</c>, in code, so a caller that builds the path from it is
///          found even when it names no file;
///   .csproj .props .targets .sln .xaml .json   any text that names any of those (a project or linked source, a
///          registry adapter's program);
///   .ps1 .cmd .bat   a line that is not a comment and names any of those.
///
/// What it does not read, and why: comments and docstrings, and files of every other kind (.md, .txt, .html, .j2, .css,
/// images, binaries) are prose or data no program starts, imports or follows as a path; C# type use is left to the
/// compiler, which refuses a type the outside code has no project or source for (project references and linked sources
/// are read above). Folders that are not SkyWright's source are not walked (<see cref="NotSource"/>). The allowed
/// observers and the recorded outputs (<see cref="Exempt"/>) are read by name, printed on every run with their reason, and
/// an exemption that names nothing fails like a stale crossing. The direction module to outside is never a finding.
/// </summary>
internal static class FenceGuard
{
    internal sealed record Crossing(string File, string[] Names, string Why, string Step);
    internal sealed record Exemption(string Path, string Why);
    internal sealed record Config(string Module, Exemption[] Exempt, Crossing[] Crossings);
    internal sealed record Finding(string File, int Line, string Name, string Kind);

    /// <summary>Folders not walked: not SkyWright's source (the pin, build output, git, editors, nested worktrees,
    /// generated test output).</summary>
    internal static readonly string[] NotSource = ["external", "bin", "obj", ".git", ".vs", ".idea", "worktrees", "_generated", "__pycache__", "artifacts", "_claude"];

    internal static readonly Config SkyWright = new(
        Module: "integration/housecarl",
        Exempt:
        [
            new("Directory.Build.props", "allowed observer: the one owner of where the module builds (its output folder, its marker, and the folder the guards learn)"),
            new("scripts/build.ps1", "allowed observer: builds the module's own solution after the app's, in both configurations"),
            new("src/SkyWright.Guards/", "allowed observer: the guards run the module's suites and lookprobe's self-test, and this check"),
            new("docs/run-coverage.json", "allowed observer: the tool index's manifest names every entry point in the tree, the module's included"),
            new("docs/TOOLS.md", "allowed observer: the tool index rendered from that manifest"),
            new("_geometry-harness/runcontrol/known-answers/", "recorded output: runcontrol's expected values name the Python tool that computed them; they are compared, not followed"),
            new("_geometry-harness/tests/fixtures/evalreport/expected/", "recorded output: the report fixtures' expected values, written from the plan before the compiler existed"),
            new("_geometry-harness/tests/fixtures/evalrun/expected/", "recorded output: the controller oracle's expected values"),
        ],
        Crossings:
        [
            new("_geometry-harness/runcontrol/Checkout.cs", ["integration/housecarl"],
                "Checkout.Integration: the one place runcontrol finds the module's source folder, for the self-test's live cross-checks with the Python pipeline and settings (it opens nothing itself)",
                "step 2(b), when the lab-only stages and EvalSettings leave runcontrol with their cross-checks"),
            new("_geometry-harness/runcontrol/SelfTest.cs", ["Checkout.Integration", "evalpipeline.py", "import evalpipeline"],
                "live cases: starts python to import evalpipeline and compare its StageLedger with this program's (the file's presence gates them)",
                "step 2(b), with the lab-only stages"),
            new("_geometry-harness/runcontrol/SelfTestDriver.cs", ["Checkout.Integration", "evalpipeline.py"],
                "live case: starts evalpipeline.py run to show the Python pipeline reuses the stages this program accepted",
                "step 2(b), with the lab-only stages"),
            new("_geometry-harness/runcontrol/SelfTestPlanModules.cs", ["Checkout.Integration", "import evalsettings"],
                "live case: starts python to import evalsettings and compare its record with EvalSettings.cs",
                "step 2(b), when EvalSettings leaves runcontrol"),
            new("_geometry-harness/runcontrol/EvalSettings.cs", ["evalsettings.py"],
                "prints only: its refusal sentences tell the user to run evalsettings.py window or set; it opens the settings file, never the script",
                "step 2(b), when EvalSettings leaves runcontrol"),
            new("_geometry-harness/runcontrol/Driver.cs", ["evalpipeline.py"],
                "prints only: the refusal for --through past bundle tells the user to continue with evalpipeline.py run",
                "not named in the task file; proposed for step 2(b), with the lab-only stages"),
            new("_geometry-harness/runcontrol/EvaluateRun.cs", ["switchflip.py"],
                "prints only: a live-import check waiting for a receipt tells the user to run switchflip.py",
                "step 2(b), when LiveImport and SwitchFlip* leave runcontrol"),
            new("_geometry-harness/runcontrol/SwitchFlipOutcome.cs", ["switchflip.py"],
                "prints only: a damaged events file is said not to have been written by switchflip.py",
                "step 2(b), when SwitchFlip* leaves runcontrol"),
            new("data/record-field-scope.json", ["test_recordrows.py"],
                "prose only: its about text names the module's suite that checks every field it lists exists in the schema; no program follows it",
                "not named in the task file; proposed for step 2(a), when the app's data that the module reads is settled"),
            new("_geometry-harness/tests/test_evalrun.py", ["integration/housecarl", "evalreport.py"],
                "the controller oracle's case C567 starts evalreport.py plan on the controller's bundle; the suite stays outside because make_report_fixtures.py, which workbench.tests runs, imports it",
                "not named in the task file; proposed for step 3, when the bundle contract becomes SkyWright's and the case joins the module's suites"),
        ]);

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" fence guard — nothing outside integration/housecarl/ refers into it");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool ok, string label) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label); if (!ok) fail++; }

        foreach (var (ok, label) in SelfCheck()) Check(ok, "self-check: " + label);
        if (args.Contains("--self-check")) return fail == 0 ? 0 : 1;
        if (args.Any(a => a != "--self-check")) { Console.Error.WriteLine("fence takes only --self-check."); return 2; }

        var repo = CheckoutLayout.FindRoot();
        if (repo is null)
        {
            Console.WriteLine($"  FAIL  no SkyWright checkout ({CheckoutLayout.Marker}) above {AppContext.BaseDirectory}; run the guard from a checkout's build.");
            return 1;
        }
        Console.WriteLine($"  tree      {repo}");
        foreach (var e in SkyWright.Exempt) Console.WriteLine($"  exempt    {e.Path} — {e.Why}");
        foreach (var c in SkyWright.Crossings) Console.WriteLine($"  crossing  {c.File} [{string.Join(", ", c.Names)}] — {c.Why}; removed in {c.Step}");
        var (findings, problems, read) = Scan(repo, SkyWright);
        Console.WriteLine($"  read      {read} files outside the module");
        foreach (var p in problems) Check(false, p);
        var listed = findings.Where(f => Listed(SkyWright, f)).ToList();
        foreach (var f in listed) Console.WriteLine($"  named     {f.File}:{f.Line} {f.Kind} {f.Name}");
        foreach (var f in findings.Except(listed)) Check(false, Sentence(f));
        Console.WriteLine(fail == 0 ? $"\n  fence: holds ({listed.Count} references on the named crossing list)" : $"\n  fence: {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    internal static string Sentence(Finding f) =>
        $"{f.File}:{f.Line} refers into integration/housecarl/ ({f.Kind} {f.Name}); move that code into the module, have the module call SkyWright instead, or name the crossing with its reason and step in FenceGuard.";

    static bool Listed(Config cfg, Finding f) =>
        cfg.Crossings.Any(c => Same(c.File, f.File) && c.Names.Contains(f.Name, StringComparer.Ordinal));

    static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every reference outside the module, every reason the configuration itself is unsound, and how many
    /// files were read.</summary>
    internal static (List<Finding> Findings, List<string> Problems, int Read) Scan(string root, Config cfg)
    {
        var problems = new List<string>();
        var module = Path.Combine(root, cfg.Module);
        if (!Directory.Exists(module)) return (new(), new() { $"the module folder {cfg.Module} does not exist" }, 0);
        var names = ModuleNames(module, cfg.Module);
        if (names.Python.Count == 0) problems.Add($"the module folder {cfg.Module} holds no Python module, so the check would match nothing");

        foreach (var e in cfg.Exempt)
        {
            var p = Path.Combine(root, e.Path);
            if (!(e.Path.EndsWith('/') ? Directory.Exists(p) : File.Exists(p)))
                problems.Add($"the exemption {e.Path} names nothing in this tree: a moved file would hide behind it");
        }

        var findings = new List<Finding>();
        int read = 0;
        foreach (var file in Walk(root, module))
        {
            var rel = Norm(Path.GetRelativePath(root, file));
            if (cfg.Exempt.Any(e => e.Path.EndsWith('/') ? rel.StartsWith(e.Path, StringComparison.OrdinalIgnoreCase) : Same(e.Path, rel))) continue;
            var kind = Path.GetExtension(file).ToLowerInvariant();
            if (!Read.Contains(kind)) continue;
            read++;
            findings.AddRange(FindIn(rel, kind, File.ReadAllText(file), names));
        }
        findings = findings.Distinct().ToList();
        foreach (var c in cfg.Crossings)
        {
            if (!File.Exists(Path.Combine(root, c.File))) { problems.Add($"the crossing {c.File} names no file: a closed crossing leaves the list"); continue; }
            foreach (var n in c.Names)
                if (!findings.Any(f => Same(f.File, c.File) && f.Name == n))
                    problems.Add($"the crossing {c.File} [{n}] matches nothing: a closed crossing leaves the list");
        }
        return (findings, problems, read);
    }

    static readonly HashSet<string> Read = [".py", ".cs", ".csproj", ".props", ".targets", ".sln", ".xaml", ".json", ".ps1", ".cmd", ".bat"];

    internal sealed record Names(HashSet<string> Python, HashSet<string> Tokens, string ModulePath);

    /// <summary>What a reference into the module can name.</summary>
    static Names ModuleNames(string module, string modulePath)
    {
        var py = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(module, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(module, f).Split(Path.DirectorySeparatorChar);
            if (rel.Any(p => p is "bin" or "obj" or "__pycache__")) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".py") { py.Add(Path.GetFileNameWithoutExtension(f)); tokens.Add(Path.GetFileName(f)); }
            else if (ext == ".html") tokens.Add(Path.GetFileName(f));
            else if (ext == ".csproj") tokens.Add(Path.GetFileNameWithoutExtension(f));
        }
        foreach (var d in Directory.EnumerateDirectories(module))
        {
            var n = Path.GetFileName(d);
            if (n is not ("tests" or "bin" or "obj" or "__pycache__")) tokens.Add(n);
        }
        return new Names(py, tokens, Norm(modulePath));
    }

    static IEnumerable<string> Walk(string root, string module)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var mod = Path.GetFullPath(module).TrimEnd('\\', '/');
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (NotSource.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFullPath(d).TrimEnd('\\', '/'), mod, StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(d);
            }
        }
    }

    // ---- the reference rule, per kind of file

    internal static IEnumerable<Finding> FindIn(string rel, string kind, string text, Names n)
    {
        switch (kind)
        {
            case ".py":
                var (code, lits, dynamic) = PythonParts(text);
                foreach (var (line, s) in lits) foreach (var hit in InText(s, n, lone: true)) yield return new(rel, line, hit, "names");
                foreach (var (line, mod) in PythonImports(code).Concat(dynamic)) if (n.Python.Contains(mod)) yield return new(rel, line, "import " + mod, "imports");
                break;
            case ".cs":
                var (csCode, csLits) = CSharpParts(text);
                foreach (var (line, s) in csLits)
                {
                    foreach (var hit in InText(s, n, lone: true)) yield return new(rel, line, hit, "names");
                    foreach (var (_, mod) in PythonImports(s)) if (n.Python.Contains(mod)) yield return new(rel, line, "import " + mod, "hands python");
                }
                foreach (Match m in Regex.Matches(csCode, @"\bCheckout\.Integration\b"))
                    yield return new(rel, LineOf(csCode, m.Index), "Checkout.Integration", "uses");
                break;
            case ".ps1" or ".cmd" or ".bat":
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith('#') || t.StartsWith("::") || t.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var hit in InText(lines[i], n, lone: false)) yield return new(rel, i + 1, hit, "names");
                }
                break;
            default:
                var all = text.Split('\n');
                for (int i = 0; i < all.Length; i++) foreach (var hit in InText(all[i], n, lone: false)) yield return new(rel, i + 1, hit, "names");
                break;
        }
    }

    /// <summary>The module names one piece of text holds.</summary>
    static IEnumerable<string> InText(string s, Names n, bool lone)
    {
        var t = s.Replace('\\', '/');
        if (t.Contains(n.ModulePath, StringComparison.OrdinalIgnoreCase)) yield return n.ModulePath;
        foreach (var tok in n.Tokens)
            if (Regex.IsMatch(t, @"(?<![\w.-])" + Regex.Escape(tok) + @"(?![\w-])", RegexOptions.IgnoreCase)) yield return tok;
    }

    static int LineOf(string text, int index) { int l = 1; for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') l++; return l; }

    /// <summary>(line, module) of every import in Python code (strings and comments already blanked).</summary>
    static IEnumerable<(int, string)> PythonImports(string code)
    {
        foreach (Match m in Regex.Matches(code, @"(?m)(?:^|;)\s*import\s+([\w., ]+)"))
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var mod = part.Trim().Split(' ')[0].Split('.')[0];
                if (mod.Length > 0) yield return (LineOf(code, m.Index), mod);
            }
        foreach (Match m in Regex.Matches(code, @"(?m)(?:^|;)\s*from\s+(\w+)[\w.]*\s+import\b"))
            yield return (LineOf(code, m.Index), m.Groups[1].Value);
    }

    /// <summary>Python split into code (every string blanked to a marker, comments removed) and its string literals
    /// with their lines; a string that is a statement of its own (a docstring, or any bare string) is left out as prose.
    /// A literal handed straight to <c>import_module(</c> or <c>__import__(</c> is also a dynamic import.</summary>
    internal static (string Code, List<(int Line, string Text)> Literals, List<(int Line, string Module)> Dynamic) PythonParts(string text)
    {
        var code = new StringBuilder();
        var lits = new List<(int, string)>();
        var dynamic = new List<(int, string)>();
        int i = 0, line = 1, n = text.Length;
        bool lineHasCode = false;   // a token other than whitespace before this string on its logical line
        int depth = 0;              // bracket depth: a string inside brackets is never a statement
        while (i < n)
        {
            char c = text[i];
            if (c == '#') { while (i < n && text[i] != '\n') i++; continue; }
            if (c == '\n') { code.Append('\n'); line++; i++; if (depth == 0 && !(code.Length > 1 && code[^2] == '\\')) lineHasCode = false; continue; }
            int p = i;
            while (p < n && "rRbBuUfF".IndexOf(text[p]) >= 0 && p - i < 2) p++;
            if (p < n && (text[p] == '"' || text[p] == '\'') && (p == i || !char.IsLetterOrDigit(i > 0 ? text[i - 1] : ' ')))
            {
                char q = text[p];
                bool triple = p + 2 < n && text[p + 1] == q && text[p + 2] == q;
                bool raw = text.Substring(i, p - i).Contains('r', StringComparison.OrdinalIgnoreCase);
                int start = p + (triple ? 3 : 1), j = start;
                int startLine = line;
                while (j < n)
                {
                    if (!raw && text[j] == '\\') { if (j + 1 < n && text[j + 1] == '\n') line++; j += 2; continue; }
                    if (raw && text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '\n') { if (!triple) break; line++; }
                    if (text[j] == q && (!triple || (j + 2 < n && text[j + 1] == q && text[j + 2] == q))) break;
                    j++;
                }
                var body = text.Substring(start, Math.Max(0, Math.Min(j, n) - start));
                bool statement = !lineHasCode && depth == 0 && IsStatementEnd(text, j + (triple ? 3 : 1));
                if (!statement) lits.Add((startLine, body));
                var before = code.ToString().TrimEnd();
                if (before.EndsWith("import_module(", StringComparison.Ordinal) || before.EndsWith("__import__(", StringComparison.Ordinal))
                    dynamic.Add((startLine, body.Split('.')[0]));
                code.Append("\"\"");
                for (int k = 0; k < body.Count(ch => ch == '\n'); k++) code.Append('\n');
                lineHasCode = true;
                i = Math.Min(n, j + (triple ? 3 : 1));
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            if (!char.IsWhiteSpace(c)) lineHasCode = true;
            code.Append(c);
            i++;
        }
        return (code.ToString(), lits, dynamic);
    }

    /// <summary>Whether only whitespace or a comment follows a string up to the end of its line (a bare string statement).</summary>
    static bool IsStatementEnd(string text, int at)
    {
        for (int k = at; k < text.Length; k++)
        {
            if (text[k] == '\n' || text[k] == '#') return true;
            if (!char.IsWhiteSpace(text[k])) return false;
        }
        return true;
    }

    /// <summary>C# split into code (comments removed, strings blanked) and its string literals with their lines.</summary>
    internal static (string Code, List<(int Line, string Text)> Literals) CSharpParts(string text)
    {
        var code = new StringBuilder();
        var lits = new List<(int, string)>();
        int i = 0, line = 1, n = text.Length;
        void Lit(int from, int to, int at)
        {
            var s = text.Substring(from, Math.Max(0, to - from));
            lits.Add((at, s));
            code.Append("\"\"");
            foreach (var ch in s) if (ch == '\n') { code.Append('\n'); line++; }
        }
        while (i < n)
        {
            char c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? n : e + 2;
                for (int k = i; k < e; k++) if (text[k] == '\n') { code.Append('\n'); line++; }
                i = e; continue;
            }
            int p = i;
            while (p < n && (text[p] == '$' || text[p] == '@') && p - i < 3) p++;
            if (p < n && text[p] == '"')
            {
                bool verbatim = text.Substring(i, p - i).Contains('@');
                int quotes = 0; while (p + quotes < n && text[p + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    var close = new string('"', quotes);
                    int e = text.IndexOf(close, p + quotes, StringComparison.Ordinal); e = e < 0 ? n : e;
                    Lit(p + quotes, e, line); i = Math.Min(n, e + quotes); continue;
                }
                int j = p + 1;
                while (j < n)
                {
                    if (verbatim) { if (text[j] == '"') { if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; } break; } }
                    else { if (text[j] == '\\') { j += 2; continue; } if (text[j] == '"' || text[j] == '\n') break; }
                    j++;
                }
                Lit(p + 1, Math.Min(j, n), line); i = Math.Min(n, j + 1); continue;
            }
            if (c == '\'' )
            {
                int j = i + 1; while (j < n && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                code.Append("' '"); i = Math.Min(n, j + 1); continue;
            }
            if (c == '\n') line++;
            code.Append(c);
            i++;
        }
        return (code.ToString(), lits);
    }

    // ---- the check's own known answers

    internal static IEnumerable<(bool Ok, string Label)> SelfCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-fence-selfcheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Touch(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
            Touch("mod/m/tool.py", "import base\nprint(1)\n");
            Touch("mod/m/probe/probe.csproj", "<Project />");
            Touch("app/clean.py", "\"\"\"tool.py is mentioned in a docstring\"\"\"\n# and import tool in a comment\nimport base\nx = 'base.py'\n");
            Touch("app/Clean.cs", "// tool.py in a comment\n/* probe too */ var s = \"base.py\";\n");
            var cfg = new Config("mod/m", [], []);
            var (f0, p0, _) = Scan(root, cfg);
            yield return (f0.Count == 0 && p0.Count == 0, "a tree whose outside mentions the module only in comments and docstrings, and whose module imports outside code, holds: " + Describe(f0, p0));

            Touch("app/bad.py", "def f():\n    import tool\n");
            var (f1, _, _) = Scan(root, cfg);
            yield return (f1.Any(f => f.File == "app/bad.py" && f.Name == "import tool" && f.Line == 2), "an outside Python import of a module name fails on its line: " + Describe(f1, []));
            File.Delete(Path.Combine(root, "app/bad.py"));

            Touch("app/Bad.cs", "var argv = new[] { \"python\", Path.Combine(h, \"tool.py\") };\n");
            var (f2, _, _) = Scan(root, cfg);
            yield return (f2.Any(f => f.File == "app/Bad.cs" && f.Name == "tool.py"), "an outside C# literal naming a module script fails: " + Describe(f2, []));
            Touch("app/Bad.cs", "var code = \"import sys; import tool; print(1)\";\n");
            var (f2b, _, _) = Scan(root, cfg);
            yield return (f2b.Any(f => f.File == "app/Bad.cs" && f.Name == "import tool"), "an outside C# literal handing python an import of a module name fails: " + Describe(f2b, []));
            File.Delete(Path.Combine(root, "app/Bad.cs"));

            Touch("app/app.csproj", "<ItemGroup><ProjectReference Include=\"..\\mod\\m\\probe\\probe.csproj\" /></ItemGroup>");
            var (f3, _, _) = Scan(root, cfg);
            yield return (f3.Any(f => f.File == "app/app.csproj"), "an outside project reference into the module fails: " + Describe(f3, []));
            File.Delete(Path.Combine(root, "app/app.csproj"));

            Touch("x.sln", "Project(\"{FAE04EC0}\") = \"probe\", \"mod\\m\\probe\\probe.csproj\", \"{1}\"\n");
            var (f4, _, _) = Scan(root, cfg);
            yield return (f4.Any(f => f.File == "x.sln"), "a solution entry for a module project fails: " + Describe(f4, []));
            File.Delete(Path.Combine(root, "x.sln"));

            var (_, p5, _) = Scan(root, cfg with { Crossings = [new("app/clean.py", ["tool.py"], "fixture", "never")] });
            yield return (p5.Any(p => p.Contains("matches nothing")), "a named crossing that matches nothing fails: " + string.Join("; ", p5));

            var (_, p6, _) = Scan(root, cfg with { Exempt = [new("app/gone.py", "fixture")] });
            yield return (p6.Any(p => p.Contains("app/gone.py names nothing")), "an exemption that names nothing fails: " + string.Join("; ", p6));

            Touch("app/listed.py", "import tool\n");
            var c7 = cfg with { Crossings = [new("app/listed.py", ["import tool"], "fixture", "never")] };
            var (f7, p7, _) = Scan(root, c7);
            yield return (p7.Count == 0 && f7.All(f => Listed(c7, f)) && f7.Count == 1, "a reference on the named list passes and its entry is not stale: " + Describe(f7, p7));
            File.Delete(Path.Combine(root, "app/listed.py"));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static string Describe(List<Finding> f, List<string> p) =>
        (f.Count == 0 ? "no findings" : string.Join("; ", f.Select(x => $"{x.File}:{x.Line} {x.Name}"))) + (p.Count > 0 ? " | " + string.Join("; ", p) : "");

    static string Norm(string p) => p.Replace('\\', '/');
}
