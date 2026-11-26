using System.Text;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>Reading parsed JSON the way evalrun.py's expressions read it: get, `x or {}`, `x or []`, str().</summary>
public static class Ev
{
    /// <summary>d.get(key); a value that is not a dict has no get, as in Python.</summary>
    public static object? G(object? d, string key) => PyValue.Dict(d).Get(key);

    /// <summary>d.get(key, fallback): the fallback only when the key is absent.</summary>
    public static object? G(object? d, string key, object? fallback)
    {
        var dict = PyValue.Dict(d);
        return dict.ContainsKey(key) ? dict[key] : fallback;
    }

    /// <summary>(v or {}): the dict, or an empty one for any false value.</summary>
    public static PyDict OrEmpty(object? v) => PyValue.Truthy(v) ? PyValue.Dict(v) : new PyDict();

    /// <summary>for x in (v or []): the items, or none for any false value.</summary>
    public static IEnumerable<object?> Items(object? v) => PyValue.Truthy(v) ? PyValue.Iter(v) : [];

    /// <summary>(v or []) kept as a value for a document.</summary>
    public static object OrList(object? v) => PyValue.Truthy(v) ? v! : new List<object?>();

    /// <summary>str(v).</summary>
    public static string Str(object? v) => PyText.Str(v);

    /// <summary>str(v)[:n], counted in code units (the hashes and ids cut here are ASCII).</summary>
    public static string Head(object? v, int n)
    {
        string s = PyText.Str(v);
        return s.Length <= n ? s : s[..n];
    }

    /// <summary>a == b by Python's rule.</summary>
    public static bool Eq(object? a, object? b) => PyValue.Eq(a, b);

    /// <summary>Path(p).is_absolute() on Windows: a drive and a root.</summary>
    public static bool IsAbsolute(string path)
    {
        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return p.Length >= 3 && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>os.path.isabs() of Python 3.12 on Windows: a leading separator, or a drive and a root.</summary>
    public static bool OsIsAbs(string path)
    {
        string s = (path.Length > 3 ? path[..3] : path).Replace('/', '\\');
        return s.StartsWith('\\') || (s.Length >= 3 && s[1] == ':' && s[2] == '\\');
    }

    /// <summary>Path(p).stem: the name without its last suffix.</summary>
    public static string Stem(string path)
    {
        string name = PyPath.Name(path);
        return name[..(name.Length - PyPath.Suffix(path).Length)];
    }

    /// <summary>Path.exists(): a file or a folder.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>sorted(folder.glob("*.json")): the entries whose name ends .json, in Python's path order.</summary>
    public static List<string> SortedJson(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return PyOrder.SortedPaths(Directory.EnumerateFileSystemEntries(folder)
            .Where(p => Path.GetFileName(p).EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>read_text(encoding="utf-8", errors="replace"): invalid bytes become U+FFFD; None when unreadable is the caller's.</summary>
    public static string ReadLoose(string path) => new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path));

    /// <summary>read_text(encoding="utf-8", errors="replace") as Python's text mode gives it: every line end is one line feed.</summary>
    public static string ReadText(string path) => ReadLoose(path).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>write_text(text, encoding="utf-8"): Python's text mode writes "\n" as the platform's line end.</summary>
    public static void WriteText(string path, string text)
    {
        if (Environment.NewLine != "\n") text = text.Replace("\n", Environment.NewLine);
        File.WriteAllBytes(path, PyJson.Utf8(text));
    }
}

/// <summary>The run folder's plan: loading it, the refusals that guard it, and its frozen premises (evalrun.py).</summary>
public static class EvalStore
{
    public const int SchemaVersion = 2;
    public const int BundleSchema = 2;
    public const string RequestProviders = "arm-view";
    public const int ToolIdentityContract = 1;
    public const string NotOptedIn = "not-opted-in";
    public static readonly string[] TerminalOk = ["completed", "reused"];
    public static readonly string[] Claims = ["no-findings-in-completed-checks", "findings", "incomplete"];

    /// <summary>status in TERMINAL_OK.</summary>
    public static bool IsTerminalOk(object? status) => status is string s && TerminalOk.Contains(s);

    /// <summary>The clock evalrun's utcnow() reads; a test replaces it.</summary>
    public static Func<string> UtcNow = PyText.UtcNowZ;

    /// <summary>A run's timing history stays with the run unless the caller said otherwise.</summary>
    public static void SetRunStore(string run)
    {
        if (PyValue.Strip(Environment.GetEnvironmentVariable("HOUSECARL_RUNLOG_DIR") ?? "").Length == 0)
            Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", PyPath.Resolve(PyPath.Join(run, "runlog")));
    }

    /// <summary>The plan, or the reason it is refused as "cause: detail". Immutability and the schema are checked here only.</summary>
    public static (PyDict? plan, string? why) LoadPlan(string run)
    {
        string p = PyPath.Join(run, "plan.json");
        if (!Ev.Exists(p)) return (null, $"plan-missing: {p} does not exist: this is not an evalrun RUN directory");
        string recorded = PyPath.Join(run, "plan.sha256");
        if (!Ev.Exists(recorded))
            return (null, $"plan-changed: {recorded} is missing, so the plan's immutability cannot be checked");
        if (Hashing.Sha256FileOrNull(p) != PyValue.Strip(PyJson.Decode(File.ReadAllBytes(recorded), bom: false)))
            return (null, "plan-changed: plan.json no longer hashes to plan.sha256");
        if (PyJson.LoadEvalFile(p) is not PyDict doc) return (null, $"plan-unreadable: {p} is not readable as JSON");
        if (!Ev.Eq(doc.Get("schemaVersion"), (long)SchemaVersion))
            return (null, $"plan-schema-unsupported: plan.json declares schemaVersion {PyText.Repr(doc.Get("schemaVersion"))}; "
                + $"this controller reads {SchemaVersion}. A schema-1 plan carries no per-arm requested identity, so an accepted "
                + "receipt could not be bound to the configuration the arm asked for. Next action: plan again from the same "
                + "registry, scope and candidates.");
        return (doc, null);
    }

    /// <summary>A plan frozen before requestProviders cannot bind new receipts; run, resume and import refuse it by name.</summary>
    public static string? RequestRefusal(PyDict plan)
    {
        if (Ev.Eq(plan.Get("requestProviders"), RequestProviders)) return null;
        return $"plan-request-outdated: plan.json declares requestProviders {PyText.Repr(plan.Get("requestProviders"))}, "
            + $"not {PyText.Repr(RequestProviders)}: it froze the scope's providers onto every arm, so a receipt bound to it now "
            + "could be bound to a view that arm never read. Next action: plan again from the same registry, scope and candidates.";
    }

    static readonly Regex CauseWord = new(@"\A[a-z][a-z-]*\z", RegexOptions.CultureInvariant);

    /// <summary>The cause word out of a "cause: detail" refusal, for the disposition's refused field.</summary>
    public static string RefusalCause(string? why)
    {
        string head = PyValue.Strip((why ?? "").Split(':', 2)[0]);
        return CauseWord.IsMatch(head) ? head : "plan-changed";
    }

    /// <summary>A stored relative path that does not resolve from this folder: the plan cannot say where it was made.</summary>
    public static string? RelativePathRefusal(PyDict plan, IReadOnlyCollection<string>? only = null, string? remedy = null)
    {
        var stored = new List<(string what, object? path)>
        {
            ("registry", Ev.OrEmpty(plan.Get("registry")).Get("path")),
            ("scope", Ev.OrEmpty(plan.Get("scope")).Get("path")),
            ("candidates folder", plan.Get("candidatesDir")),
        };
        foreach (var a in Ev.Items(plan.Get("arms")))
            stored.Add(($"candidate (arm {Ev.Str(Ev.G(a, "armId"))})", Ev.G(a, "candidatePath")));
        foreach (var (what, p) in stored)
        {
            if (only != null && !only.Contains(what)) continue;
            if (PyValue.Truthy(p) && !Ev.IsAbsolute(Ev.Str(p)) && !Ev.Exists(Ev.Str(p)))
                return $"plan-path-relative: this plan stores the {what} path '{Ev.Str(p)}' relative to the folder it was planned "
                    + $"from, which the plan does not record, and it does not resolve from {Directory.GetCurrentDirectory()}; run from the "
                    + "folder the plan was made in, or " + (remedy ?? "plan again (plans now store absolute paths).");
        }
        return null;
    }

    // What a missing or changed premise costs, in the refusal's words.
    static readonly Dictionary<string, (string lost, string changed)> PremiseWords = new()
    {
        ["registry"] = ("the checklist this run was derived from cannot be re-read", "A checklist cannot change under a frozen plan."),
        ["scope"] = ("the applicability this plan froze cannot be re-derived", "A scope change changes the checklist."),
        ["search"] = ("the candidate search this plan read cannot be re-read", "A candidate search cannot change under a frozen plan."),
    };

    /// <summary>Where bundle copies each premise.</summary>
    public static readonly (string label, string dest)[] PremiseCopies =
    [
        ("registry", "registry/check-registry.json"), ("scope", "scope/scope.json"), ("search", "search/candidates.json"),
    ];

    /// <summary>The one hash check of a frozen premise: the plan's stored file, or a named one in its place.</summary>
    public static string? PremiseFileRefusal(PyDict plan, string label, string nxt, string? named = null)
    {
        string want = Ev.Str(Ev.OrEmpty(plan.Get(label)).Get("sha256"));
        string p = PyPath.Str(named ?? Ev.Str(PyValue.Dict(plan[label])["path"]));
        var (lost, changed) = PremiseWords[label];
        if (!File.Exists(p))
        {
            if (named != null) return $"{label}-missing: --{label} {p} does not exist. {nxt}";
            return $"{label}-missing: the plan's {label} {p} does not exist, so {lost}. {nxt}";
        }
        string? got = Hashing.Sha256FileOrNull(p);
        if (got != want)
        {
            if (named != null) return $"{label}-mismatch: --{label} {p} hashes {Ev.Head(got, 12)}, the plan froze {Ev.Head(want, 12)}. {nxt}";
            return $"{label}-changed: {p} now hashes {Ev.Head(got, 12)}, the plan recorded {Ev.Head(want, 12)}. {changed} {nxt}";
        }
        return null;
    }

    /// <summary>The registry and the scope are premises of a frozen plan: a changed one is a new plan.</summary>
    public static string? PremiseRefusal(PyDict plan)
    {
        string? rel = RelativePathRefusal(plan);
        if (rel != null) return rel;
        const string nxt = "Next action: re-run scopeprobe and plan again.";
        string? bad = PremiseFileRefusal(plan, "registry", nxt);
        if (bad != null) return bad;
        if (!PyValue.Truthy(Ev.OrEmpty(plan.Get("scope")).Get("path"))) return null;
        return PremiseFileRefusal(plan, "scope", nxt);
    }
}
