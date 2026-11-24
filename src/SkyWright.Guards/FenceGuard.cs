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
///          registry adapter's program), and in a build file the module's layout properties, $(SkyWrightIntegration...);
///   .ps1 .cmd .bat .sh   a line that is not a comment and names any of those.
///
/// What it does not read, and why: comments and docstrings, and files of every other kind (.md, .txt, .html, .j2, .css,
/// images, binaries) are prose or data no program starts, imports or follows as a path; C# type use is left to the
/// compiler, which refuses a type the outside code has no project or source for (project references and linked sources
/// are read above). Folders that are not SkyWright's source are not walked (<see cref="NotSource"/>, matched by name at any
/// depth and printed on every run with the reason). The allowed
/// observers and the recorded outputs (<see cref="Exempt"/>) are read by name, printed on every run with their reason, and
/// an exemption that names nothing fails like a stale crossing. The direction module to outside is never a finding.
///
/// A second fence, about houseCARL's AI-facing skill data: its two evaluation skills' folders
/// (<c>.claude/skills/evaluation-reporting</c>, <c>.claude/skills/world-edit-compatibility</c>) and its agent definitions
/// (<c>plugin/agents</c>). Their data lives in the module now, so
///   (1) outside the module, any reference to them is a finding: the app and the scan read none of it;
///   (2) inside the module, any reference to them is a finding too: the module reads its own copy, never the old place.
/// What counts: in a .py or .cs string literal (comments and docstrings removed, as above), a literal holding
/// <c>skills/&lt;either skill&gt;</c> or <c>plugin/agents</c> with any separator, or a literal that is exactly one of the
/// two skill names (a path piece, <c>Path.Combine(".claude", "skills", "evaluation-reporting")</c>), or a literal
/// <c>"agents"</c> in a file that also holds a literal <c>"plugin"</c> (this pair rule reads the one file, not the code
/// that joins them, so a file naming both for other reasons is a finding too); in build
/// files, .json and scripts, the path forms only (a skill's bare name in data or prose is a name, not a read). What they
/// cannot see: a path built from variables or across lines, a skill named in a data value a program later joins, prose
/// files, and what houseCARL's own programs read (the pin is not walked). Each fence has its own named list, printed with
/// the step that removes each entry; an entry that matches nothing fails.
/// </summary>
internal static class FenceGuard
{
    internal sealed record Crossing(string File, string[] Names, string Why, string Step);
    internal sealed record Exemption(string Path, string Why);
    internal sealed record Config(string Module, Exemption[] Exempt, Crossing[] Crossings,
                                  Crossing[]? SkillReadsOutside = null, Crossing[]? SkillReadsInside = null, Exemption[]? SkillExempt = null);
    internal sealed record Finding(string File, int Line, string Name, string Kind);

    /// <summary>Folders not walked: not SkyWright's source (the pin, build output, git, editors, nested worktrees,
    /// generated test output).</summary>
    internal static readonly Exemption[] NotSource =
    [
        new("external", "the houseCARL pin, read-only here; its own code is not SkyWright's"),
        new("bin", "build output"),
        new("obj", "build output"),
        new(".git", "git's own store"),
        new(".vs", "editor state"),
        new(".idea", "editor state"),
        new("worktrees", "nested git worktrees: other checkouts with their own fence"),
        new("_generated", "output the suites generate while they run"),
        new("__pycache__", "Python's compiled cache"),
        new("artifacts", "install and packaging output (ignored by git)"),
        new("_claude", "the owner's notes repository (ignored by git)"),
    ];

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
                "Checkout.Integration: the one place runcontrol finds the module's source folder, for the self-test's live cross-check of the Python settings record (it opens nothing itself)",
                "step 2(b), with that cross-check, when EvalSettings leaves runcontrol"),
            new("_geometry-harness/runcontrol/SelfTestPlanModules.cs", ["Checkout.Integration", "import evalsettings"],
                "live case: starts python to import evalsettings and compare its record with EvalSettings.cs",
                "step 2(b), when EvalSettings leaves runcontrol"),
            new("_geometry-harness/runcontrol/EvalSettings.cs", ["evalsettings.py"],
                "prints only: its refusal sentences tell the user to run evalsettings.py window or set; it opens the settings file, never the script",
                "step 2(b), when EvalSettings leaves runcontrol"),
            new("data/record-field-scope.json", ["test_recordrows.py"],
                "prose only: its about text names the module's suite that checks every field it lists exists in the schema; no program follows it",
                "not named in the task file; proposed with the split of the mixed suites (test_recordrows' outside cases return outside), or a prose edit in step 3's registry work"),
        ],
        SkillReadsOutside:
        [
            new("Directory.Build.props", ["skills/evaluation-reporting"],
                "the build copies the reporting skill's scene-policy.json into Data\\ for the app's grouping",
                "step 2(a), after step 4 lands: the app reads SkyWright's own data/scene-policy.json"),
            new("_geometry-harness/workbench/Scenes.cs", ["evaluation-reporting"],
                "the app's grouping names the skill's file as a path relative to a checkout (RelativePath)",
                "step 2(a), after step 4 lands"),
            new("_geometry-harness/workbench.tests/ReviewSpeedPart2Tests.cs", ["evaluation-reporting"],
                "the grouping tests open the skill's copy in the pin",
                "step 2(a), after step 4 lands"),
            new("_geometry-harness/runcontrol/EvalSettings.cs", ["plugin + agents"],
                "the plan's settings reader looks for plugin/agents above its own program to validate model IDs",
                "step 2(b), when EvalSettings leaves runcontrol"),
        ],
        SkillReadsInside:
        [
            new("integration/housecarl/tests/test_reel_vignettes.py", ["skills/evaluation-reporting"],
                "case_skill_data's docs check reads the skill's prose (SKILL.md, references/reel-layout.md) for rules the vignette format superseded; prose, not data",
                "step 3, when the skill moves into the module"),
            new("integration/housecarl/tests/test_narration_fidelity.py", ["evaluation-reporting"],
                "C2029 reads the skill's SKILL.md for the setting and flag a rewrite needs; prose, not data",
                "step 3, when the skill moves into the module"),
        ],
        SkillExempt:
        [
            new("src/SkyWright.Guards/", "the guard names the folders it fences"),
        ]);

    static readonly string[] SkillNames = ["evaluation-reporting", "world-edit-compatibility"];

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
        foreach (var e in NotSource) Console.WriteLine($"  not walked {e.Path}/ (a folder of this name at any depth) — {e.Why}");
        foreach (var e in SkyWright.Exempt) Console.WriteLine($"  exempt    {e.Path} — {e.Why}");
        foreach (var c in SkyWright.Crossings) Console.WriteLine($"  crossing  {c.File} [{string.Join(", ", c.Names)}] — {c.Why}; removed in {c.Step}");
        var (findings, problems, read) = Scan(repo, SkyWright);
        Console.WriteLine($"  read      {read} files outside the module");
        foreach (var p in problems) Check(false, p);
        var listed = findings.Where(f => Listed(SkyWright, f)).ToList();
        foreach (var f in listed) Console.WriteLine($"  named     {f.File}:{f.Line} {f.Kind} {f.Name}");
        foreach (var f in findings.Except(listed)) Check(false, Sentence(f));

        // the second fence: houseCARL's two evaluation skills and its agent definitions
        foreach (var e in SkyWright.SkillExempt ?? []) Console.WriteLine($"  skill-exempt {e.Path} — {e.Why}");
        foreach (var c in SkyWright.SkillReadsOutside ?? []) Console.WriteLine($"  skill read (outside) {c.File} [{string.Join(", ", c.Names)}] — {c.Why}; removed in {c.Step}");
        foreach (var c in SkyWright.SkillReadsInside ?? []) Console.WriteLine($"  skill read (module)  {c.File} [{string.Join(", ", c.Names)}] — {c.Why}; removed in {c.Step}");
        var (outside, inside, skillProblems) = ScanSkills(repo, SkyWright);
        foreach (var p in skillProblems) Check(false, p);
        var outsideListed = outside.Where(f => ListedIn(SkyWright.SkillReadsOutside, f)).ToList();
        var insideListed = inside.Where(f => ListedIn(SkyWright.SkillReadsInside, f)).ToList();
        foreach (var f in outsideListed.Concat(insideListed)) Console.WriteLine($"  named     {f.File}:{f.Line} {f.Kind} {f.Name}");
        foreach (var f in outside.Except(outsideListed)) Check(false, SkillSentence(f, inside: false));
        foreach (var f in inside.Except(insideListed)) Check(false, SkillSentence(f, inside: true));
        int skillNamed = outsideListed.Count + insideListed.Count;
        Console.WriteLine(fail == 0
            ? $"\n  fence: holds ({listed.Count} references on the named crossing list; {skillNamed} reads of houseCARL's skill data on the named skill lists)"
            : $"\n  fence: {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    internal static string SkillSentence(Finding f, bool inside) => inside
        ? $"{f.File}:{f.Line} reads houseCARL's {f.Name} from inside the integration module; that data lives in the module now: read the module's own copy, or name the read with its reason and step in FenceGuard."
        : $"{f.File}:{f.Line} reads houseCARL's {f.Name} from outside the integration module; the app and the scan read no AI-facing skill data: take it from SkyWright's data/ or move the reader into the module, or name it with its reason and step in FenceGuard.";

    static bool ListedIn(Crossing[]? list, Finding f) =>
        (list ?? []).Any(c => Same(c.File, f.File) && c.Names.Contains(f.Name, StringComparer.Ordinal));

    /// <summary>Every reference to houseCARL's two skills or plugin/agents: outside the module (less the skill
    /// exemptions), inside it, and every reason the two named lists are unsound.</summary>
    internal static (List<Finding> Outside, List<Finding> Inside, List<string> Problems) ScanSkills(string root, Config cfg)
    {
        var problems = new List<string>();
        var module = Path.Combine(root, cfg.Module);
        var outside = new List<Finding>();
        var inside = new List<Finding>();
        foreach (var e in cfg.SkillExempt ?? [])
        {
            var p = Path.Combine(root, e.Path);
            if (!(e.Path.EndsWith('/') ? Directory.Exists(p) : File.Exists(p)))
                problems.Add($"the skill exemption {e.Path} names nothing in this tree: a moved file would hide behind it");
        }
        void ReadFile(string file, List<Finding> into)
        {
            var rel = Norm(Path.GetRelativePath(root, file));
            var kind = Path.GetExtension(file).ToLowerInvariant();
            if (!Read.Contains(kind)) return;
            into.AddRange(SkillsIn(rel, kind, File.ReadAllText(file)));
        }
        if (Directory.Exists(module))
            foreach (var file in Walk(root, module))
            {
                var rel = Norm(Path.GetRelativePath(root, file));
                if ((cfg.SkillExempt ?? []).Any(e => e.Path.EndsWith('/') ? rel.StartsWith(e.Path, StringComparison.OrdinalIgnoreCase) : Same(e.Path, rel))) continue;
                ReadFile(file, outside);
            }
        if (Directory.Exists(module) && cfg.SkillReadsInside != null)
            foreach (var file in WalkModule(module)) ReadFile(file, inside);
        outside = outside.Distinct().ToList();
        inside = inside.Distinct().ToList();
        foreach (var (list, found, label) in new[] { (cfg.SkillReadsOutside, outside, "outside"), (cfg.SkillReadsInside, inside, "module") })
            foreach (var c in list ?? [])
            {
                if (!File.Exists(Path.Combine(root, c.File))) { problems.Add($"the skill read ({label}) {c.File} names no file: a closed read leaves the list"); continue; }
                foreach (var n in c.Names)
                    if (!found.Any(f => Same(f.File, c.File) && f.Name == n))
                        problems.Add($"the skill read ({label}) {c.File} [{n}] matches nothing: a closed read leaves the list");
            }
        return (outside, inside, problems);
    }

    /// <summary>The module's own files, but its build output and caches.</summary>
    static IEnumerable<string> WalkModule(string module)
    {
        var stack = new Stack<string>();
        stack.Push(module);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!NotSource.Any(x => string.Equals(x.Path, Path.GetFileName(d), StringComparison.OrdinalIgnoreCase))) stack.Push(d);
        }
    }

    /// <summary>The skill forms one piece of text holds: a path form, and (for a code literal) the bare piece.</summary>
    static IEnumerable<string> SkillForms(string s, bool literal)
    {
        var t = Regex.Replace(s.Replace('\\', '/'), "/{2,}", "/");
        foreach (var n in SkillNames)
            if (Regex.IsMatch(t, @"(?<![\w.-])skills/" + Regex.Escape(n) + @"(?![\w-])", RegexOptions.IgnoreCase)) yield return "skills/" + n;
        if (Regex.IsMatch(t, @"(?<![\w.-])plugin/agents(?![\w-])", RegexOptions.IgnoreCase)) yield return "plugin/agents";
        if (literal)
            foreach (var n in SkillNames)
                if (string.Equals(s.Trim(), n, StringComparison.OrdinalIgnoreCase)) yield return n;
    }

    internal static IEnumerable<Finding> SkillsIn(string rel, string kind, string text)
    {
        const string Kind = "reads";
        List<(int Line, string Text)>? lits = kind switch
        {
            ".py" => PythonParts(text).Literals,
            ".cs" => CSharpParts(text).Literals,
            _ => null,
        };
        if (lits != null)
        {
            foreach (var (line, s) in lits) foreach (var hit in SkillForms(s, literal: true)) yield return new(rel, line, hit, Kind);
            // the pair rule: a literal "agents" in a file that also holds a literal "plugin" (Path.Combine pieces, or
            // PLUGIN = root / "plugin" then PLUGIN / "agents"); the finding is on the "agents" line
            if (lits.Any(l => l.Text == "plugin"))
                foreach (var (line, _) in lits.Where(l => l.Text == "agents")) yield return new(rel, line, "plugin + agents", Kind);
            yield break;
        }
        var lines = text.Split('\n');
        bool script = kind is ".ps1" or ".cmd" or ".bat" or ".sh";
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (script && (t.StartsWith('#') || t.StartsWith("::") || t.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var hit in SkillForms(lines[i], literal: false)) yield return new(rel, i + 1, hit, Kind);
        }
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

    static readonly HashSet<string> Read = [".py", ".cs", ".csproj", ".props", ".targets", ".sln", ".xaml", ".json", ".ps1", ".cmd", ".bat", ".sh"];

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
                if (NotSource.Any(x => string.Equals(x.Path, Path.GetFileName(d), StringComparison.OrdinalIgnoreCase))) continue;
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
                foreach (var (line, s) in lits) foreach (var hit in InText(s, n)) yield return new(rel, line, hit, "names");
                foreach (var (line, mod) in PythonImports(code).Concat(dynamic)) if (n.Python.Contains(mod)) yield return new(rel, line, "import " + mod, "imports");
                break;
            case ".cs":
                var (csCode, csLits) = CSharpParts(text);
                foreach (var (line, s) in csLits)
                {
                    foreach (var hit in InText(s, n)) yield return new(rel, line, hit, "names");
                    foreach (var (_, mod) in PythonImports(s)) if (n.Python.Contains(mod)) yield return new(rel, line, "import " + mod, "hands python");
                }
                foreach (Match m in Regex.Matches(csCode, @"\bCheckout\.Integration\b"))
                    yield return new(rel, LineOf(csCode, m.Index), "Checkout.Integration", "uses");
                break;
            case ".ps1" or ".cmd" or ".bat" or ".sh":
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith('#') || t.StartsWith("::") || t.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var hit in InText(lines[i], n)) yield return new(rel, i + 1, hit, "names");
                }
                break;
            default:
                var all = text.Split('\n');
                for (int i = 0; i < all.Length; i++)
                {
                    foreach (var hit in InText(all[i], n)) yield return new(rel, i + 1, hit, "names");
                    // a build file can reach the module through the layout's own properties without naming a file
                    if (kind is ".csproj" or ".props" or ".targets" && Regex.IsMatch(all[i], @"\$\(SkyWrightIntegration\w*\)"))
                        yield return new(rel, i + 1, "$(SkyWrightIntegration)", "uses");
                }
                break;
        }
    }

    /// <summary>The module names one piece of text holds.</summary>
    static IEnumerable<string> InText(string s, Names n)
    {
        // a separator however it is spelled: / or \, and \\ (an escaped backslash in a Python or C# literal) or // as one
        var t = Regex.Replace(s.Replace('\\', '/'), "/{2,}", "/");
        if (t.Contains(n.ModulePath, StringComparison.OrdinalIgnoreCase)) yield return n.ModulePath;
        foreach (var tok in n.Tokens)
            if (Regex.IsMatch(t, @"(?<![\w.-])" + Regex.Escape(tok) + @"(?![\w-])", RegexOptions.IgnoreCase)) yield return tok;
    }

    static int LineOf(string text, int index) { int l = 1; for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') l++; return l; }

    /// <summary>(line, module) of every import in Python code (strings and comments already blanked): at the start of a
    /// line, after a <c>;</c>, or after the colon of a one-line compound statement (<c>try: import x</c>,
    /// <c>if a: import x</c>, <c>else: import x</c>).</summary>
    static IEnumerable<(int, string)> PythonImports(string code)
    {
        foreach (Match m in Regex.Matches(code, @"(?m)(?:^|[;:])[ \t]*import[ \t]+([\w., \t]+)"))
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var mod = part.Trim().Split(' ', '\t')[0].Split('.')[0];
                if (mod.Length > 0) yield return (LineOf(code, m.Groups[1].Index), mod);
            }
        foreach (Match m in Regex.Matches(code, @"(?m)(?:^|[;:])[ \t]*from[ \t]+(\w+)[\w.]*[ \t]+import\b"))
            yield return (LineOf(code, m.Groups[1].Index), m.Groups[1].Value);
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

    /// <summary>C# split into code (comments removed, strings blanked) and its string literals with their lines. An
    /// interpolated string's holes are read as code, so a literal inside a hole (<c>$"{Path.Combine(h, "x.py")}"</c>) is a
    /// literal of its own; the string's own text is one literal with each hole marked; a hole's format clause is text.</summary>
    internal static (string Code, List<(int Line, string Text)> Literals) CSharpParts(string text)
    {
        var s = new CsScanner(text);
        s.Code(hole: false);
        return (s.Out.ToString(), s.Lits);
    }

    sealed class CsScanner(string t)
    {
        int i, line = 1;
        readonly int n = t.Length;
        public readonly StringBuilder Out = new();
        public readonly List<(int, string)> Lits = new();

        void NewLine() { Out.Append('\n'); line++; }

        /// <summary>Code up to the end, or, in an interpolation hole, up to and past the hole's closing brace (a format
        /// clause, after a colon outside any bracket, is skipped as text).</summary>
        public void Code(bool hole, int closers = 1)
        {
            int braces = 0, brackets = 0;
            while (i < n)
            {
                char c = t[i];
                if (c == '/' && i + 1 < n && t[i + 1] == '/') { while (i < n && t[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < n && t[i + 1] == '*')
                {
                    int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? n : e + 2;
                    for (int k = i; k < e; k++) if (t[k] == '\n') NewLine();
                    i = e; continue;
                }
                int p = i;
                while (p < n && (t[p] == '$' || t[p] == '@') && p - i < 8) p++;
                if (p < n && t[p] == '"' && (p == i || !char.IsLetterOrDigit(i > 0 ? t[i - 1] : ' '))) { String(p); continue; }
                if (c == '\'')
                {
                    int j = i + 1; while (j < n && t[j] != '\'' && t[j] != '\n') j += t[j] == '\\' ? 2 : 1;
                    Out.Append("' '"); i = Math.Min(n, j + 1); continue;
                }
                if (hole)
                {
                    if (c is '(' or '[') brackets++;
                    else if (c is ')' or ']') brackets = Math.Max(0, brackets - 1);
                    else if (c == '{') braces++;
                    else if (c == '}' && braces > 0) braces--;
                    else if (c == '}' || (c == ':' && braces == 0 && brackets == 0))
                    {
                        // the hole ends here; a format clause runs to the closing brace(s)
                        if (c == ':') while (i < n && t[i] != '}') { if (t[i] == '\n') NewLine(); i++; }
                        i = Math.Min(n, i + closers);
                        return;
                    }
                }
                if (c == '\n') NewLine(); else Out.Append(c);
                i++;
            }
        }

        /// <summary>One string literal starting with its prefix at i and its first quote at p.</summary>
        void String(int p)
        {
            var prefix = t.Substring(i, p - i);
            int dollars = prefix.Count(ch => ch == '$');
            bool verbatim = prefix.Contains('@');
            int quotes = 0; while (p + quotes < n && t[p + quotes] == '"') quotes++;
            bool raw = quotes >= 3;
            if (!raw) quotes = 1;
            if (!raw && quotes == 1 && p + 1 < n && t[p + 1] == '"' && !(p + 2 < n && t[p + 2] == '"')) { Lits.Add((line, "")); Out.Append("\"\""); i = p + 2; return; }
            int startLine = line;
            var lit = new StringBuilder();
            int j = p + quotes;
            int open = raw ? Math.Max(1, dollars) : 1;   // braces that open a hole
            while (j < n)
            {
                char c = t[j];
                if (raw && c == '"' && t.AsSpan(j).StartsWith(new string('"', quotes))) { j += quotes; break; }
                if (!raw && c == '"')
                {
                    if (verbatim && j + 1 < n && t[j + 1] == '"') { lit.Append('"'); j += 2; continue; }
                    j++; break;
                }
                if (!raw && !verbatim && c == '\\') { lit.Append(c); if (j + 1 < n) lit.Append(t[j + 1]); j += 2; continue; }
                if (!raw && !verbatim && c == '\n') { j++; NewLine(); break; }
                if (dollars > 0 && c == '{')
                {
                    if (!raw && j + 1 < n && t[j + 1] == '{') { lit.Append('{'); j += 2; continue; }
                    if (!raw || t.AsSpan(j).StartsWith(new string('{', open)))
                    {
                        lit.Append('\u0001');
                        i = j + open;
                        Code(hole: true, closers: open);
                        j = i;
                        continue;
                    }
                }
                if (dollars > 0 && !raw && c == '}' && j + 1 < n && t[j + 1] == '}') { lit.Append('}'); j += 2; continue; }
                if (c == '\n') NewLine();
                lit.Append(c);
                j++;
            }
            Lits.Add((startLine, lit.ToString()));
            Out.Append("\"\"");
            i = Math.Min(n, j);
        }
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
            var cfg = new Config("mod/m", [], [], SkillReadsInside: []);
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

            // interpolated strings: a literal inside a hole is a literal; the reader keeps its place after the string,
            // through format clauses and nested braces, and in verbatim and raw interpolated strings
            (string Code, string[] Names, string Label)[] interpolated =
            [
                ("var a = $\"{System.IO.Path.Combine(h, \"tool.py\")}\";\n", ["tool.py"], "a literal inside an interpolation hole"),
                ("var a = $\"{x}\"; var b = \"tool.py\";\n", ["tool.py"], "a literal after an interpolated string on the same line"),
                ("var a = $\"{n:0.00} {d:yyyy-MM-dd} {new[] { \"a\" }.Length} {Path.Combine(h, \"tool.py\")}\"; var b = \"probe\";\n", ["tool.py", "probe"], "a hole after format clauses and nested braces, and a literal after the string"),
                ("var a = $@\"{Path.Combine(h, \"tool.py\")}\\x\";\n", ["tool.py"], "a hole in a verbatim interpolated string"),
                ("var a = $\"\"\"{Path.Combine(h, \"tool.py\")}\"\"\";\n", ["tool.py"], "a hole in a raw interpolated string"),
            ];
            foreach (var (code, names, label) in interpolated)
            {
                Touch("app/Bad.cs", code);
                var (fi, _, _) = Scan(root, cfg);
                yield return (names.All(nm => fi.Any(f => f.File == "app/Bad.cs" && f.Name == nm && f.Line == 1)), $"C#: {label} fails: " + Describe(fi, []));
            }

            // an escaped separator spells the module's path as well as a plain one
            Touch("app/Bad.cs", @"var p = ""mod\\m\\y"";" + "\n");
            var (fe, _, _) = Scan(root, cfg);
            yield return (fe.Any(f => f.File == "app/Bad.cs" && f.Name == "mod/m"), "C#: the module's path with escaped backslashes fails: " + Describe(fe, []));
            File.Delete(Path.Combine(root, "app/Bad.cs"));
            Touch("app/bad.py", @"x = ""mod\\m\\y""" + "\n");
            var (fpe, _, _) = Scan(root, cfg);
            yield return (fpe.Any(f => f.File == "app/bad.py" && f.Name == "mod/m"), "Python: the module's path with escaped backslashes fails: " + Describe(fpe, []));

            // an import after the colon of a one-line compound statement
            foreach (var (code, label) in new[] { ("x = 1\ntry: import tool\nexcept ImportError: pass\n", "try: import"),
                                                  ("x = 1\nif True: import tool\n", "if ...: import"),
                                                  ("x = 1\nif x: pass\nelse: from tool import y\n", "else: from ... import") })
            {
                Touch("app/bad.py", code);
                var (fc, _, _) = Scan(root, cfg);
                int want = code.Split('\n').TakeWhile(l => !l.Contains("tool")).Count() + 1;
                yield return (fc.Any(f => f.File == "app/bad.py" && f.Name == "import tool" && f.Line == want), $"Python: {label} on one line fails on its line {want}: " + Describe(fc, []));
            }
            File.Delete(Path.Combine(root, "app/bad.py"));

            Touch("app/app.csproj", "<ItemGroup><ProjectReference Include=\"..\\mod\\m\\probe\\probe.csproj\" /></ItemGroup>");
            var (f3, _, _) = Scan(root, cfg);
            yield return (f3.Any(f => f.File == "app/app.csproj"), "an outside project reference into the module fails: " + Describe(f3, []));
            Touch("app/app.csproj", "<ItemGroup><Compile Include=\"$(SkyWrightIntegrationLayoutRoot)x.cs\" /></ItemGroup>");
            var (f3b, _, _) = Scan(root, cfg);
            yield return (f3b.Any(f => f.File == "app/app.csproj" && f.Name == "$(SkyWrightIntegration)"), "an outside build file using the module's layout properties fails: " + Describe(f3b, []));
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

            // the second fence: houseCARL's two evaluation skills and plugin/agents
            (string Rel, string Text, string Name, bool Inside, string Label)[] skill =
            [
                ("app/Bad.cs", "var p = \".claude\\\\skills\\\\evaluation-reporting\\\\references\\\\x.json\";\n", "skills/evaluation-reporting", false, "C#: a literal holding a skill's path"),
                ("app/bad.py", "p = root / \".claude\" / \"skills\" / \"world-edit-compatibility\"\n", "world-edit-compatibility", false, "Python: a skill's name as a path piece"),
                ("app/Bad.cs", "var a = Path.Combine(at, \"plugin\", \"agents\");\n", "plugin + agents", false, "C#: \"plugin\" and \"agents\" as pieces on one line"),
                ("app/app.props", "<P>$(Root).claude\\skills\\evaluation-reporting\\x.json</P>\n", "skills/evaluation-reporting", false, "a build file naming a skill's path"),
                ("app/data.json", "{ \"from\": \"external/houseCARL/plugin/agents/model-x.md\" }\n", "plugin/agents", false, "a data file naming plugin/agents"),
                ("mod/m/reader.py", "P = HC / \".claude/skills/evaluation-reporting/references/report-priorities.json\"\n", "skills/evaluation-reporting", true, "inside the module: a moved file at its old place"),
            ];
            foreach (var (rel, text, name, inside, label) in skill)
            {
                Touch(rel, text);
                var (so, si, _) = ScanSkills(root, cfg);
                var got = inside ? si : so;
                yield return (got.Any(f => f.File == rel && f.Name == name && f.Line == 1), $"skills: {label} fails: " + Describe(got, []));
                File.Delete(Path.Combine(root, rel));
            }
            Touch("app/registry.json", "{ \"skill\": \"world-edit-compatibility\", \"about\": \"read under the evaluation-reporting skill's rules\" }\n");
            Touch("app/Agents.cs", "var a = \"agents\"; // plugin/agents in a comment\n");
            Touch("app/two.py", "PLUGIN = ROOT / \"plugin\"\nx = 1\nAGENTS = PLUGIN / \"agents\"\n");
            var (sp0, _, _) = ScanSkills(root, cfg);
            yield return (sp0.Any(f => f.File == "app/two.py" && f.Name == "plugin + agents" && f.Line == 3), "skills: Python: \"plugin\" and \"agents\" as pieces on two lines of one file fails on the agents line: " + Describe(sp0, []));
            File.Delete(Path.Combine(root, "app/two.py"));
            var (sh, sih, sph) = ScanSkills(root, cfg);
            yield return (sh.Count == 0 && sih.Count == 0 && sph.Count == 0, "skills: a skill's bare name in data or prose, a lone \"agents\" and a comment hold: " + Describe(sh.Concat(sih).ToList(), sph));
            File.Delete(Path.Combine(root, "app/registry.json"));
            File.Delete(Path.Combine(root, "app/Agents.cs"));
            var (_, _, sp1) = ScanSkills(root, cfg with { SkillReadsOutside = [new("app/clean.py", ["plugin/agents"], "fixture", "never")] });
            yield return (sp1.Any(p => p.Contains("matches nothing")), "skills: a named outside read that matches nothing fails: " + string.Join("; ", sp1));
            var (_, _, sp2) = ScanSkills(root, cfg with { SkillReadsInside = [new("mod/m/tool.py", ["skills/evaluation-reporting"], "fixture", "never")] });
            yield return (sp2.Any(p => p.Contains("matches nothing")), "skills: a named module read that matches nothing fails: " + string.Join("; ", sp2));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static string Describe(List<Finding> f, List<string> p) =>
        (f.Count == 0 ? "no findings" : string.Join("; ", f.Select(x => $"{x.File}:{x.Line} {x.Name}"))) + (p.Count > 0 ? " | " + string.Join("; ", p) : "");

    static string Norm(string p) => p.Replace('\\', '/');
}
