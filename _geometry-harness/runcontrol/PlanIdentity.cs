namespace RunControl;

/// <summary>The programs a plan can launch: where each probe's build is, what it is called in the plan, and its
/// identity on disk by the one identity rule (ToolIdentity).</summary>
public static class PlanIdentity
{
    public const int Contract = 1;
    public const string Controller = "controller", Comparator = "helper:conflictdiff";
    public const string Rule = "package: every top-level non-PDB file and runtimes/**, by lower-cased relative path and sha256; "
                               + "probe-cmd: the command text and the files it names; scripts: the controller's scripts";

    /// <summary>Adapter kinds whose check launches its probe as a child.</summary>
    public static readonly HashSet<string> LaunchKinds = new(StringComparer.Ordinal)
    { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target", "adviceprobe", "patchprobe" };

    public static string ProbeExe(string harness, string probe) => PyPath.Join(harness, probe, "bin", "Release", "net9.0", probe + ".exe");

    /// <summary>The child's program: a --probe-cmd prefix split on spaces, else the probe's build; null when there is neither.</summary>
    public static List<string>? ProbeArgv(PyDict opts, string harness, string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        object? over = Py.DictOrEmpty(opts.Get("probeCmd")).Get(exe);
        if (Py.Truthy(over)) return PyText.Split(Py.S(over));
        string path = ProbeExe(harness, exe);
        return File.Exists(path) ? [path] : null;
    }

    /// <summary>The older record of a probe's build: its exe, its own assembly and the core assembly, each by hash.</summary>
    public static PyDict ToolRecord(string harness, string probe)
    {
        string exe = ProbeExe(harness, probe);
        string folder = Path.GetDirectoryName(exe)!;
        var ident = new PyDict
        {
            ["exe"] = exe, ["exeSha256"] = Hashing.Sha256FileOrNull(exe),
            ["dllSha256"] = Hashing.Sha256FileOrNull(PyPath.Join(folder, probe + ".dll")),
            ["coreDllSha256"] = Hashing.Sha256FileOrNull(PyPath.Join(folder, "housecarl-core.dll")),
        };
        ident["present"] = ident["exeSha256"] != null;
        return ident;
    }

    public static string ProgramId(string exe) => exe == "conflictdiff" ? Comparator : "probe:" + exe;

    public static string ProgramLabel(string pid)
    {
        if (pid == Controller) return "the controller (runcontrol and the libraries beside it)";
        int colon = pid.IndexOf(':');
        string kind = colon < 0 ? pid : pid[..colon], name = colon < 0 ? "" : pid[(colon + 1)..];
        return $"{(kind == "helper" ? "helper" : "probe")} {name}";
    }

    /// <summary>A program's identity on disk now, located the way its launch locates it.</summary>
    public static PyDict Measure(PyDict opts, string harness, string pid)
    {
        if (pid == Controller) return ToolIdentity.Controller();
        string name = pid[(pid.IndexOf(':') + 1)..];
        object? over = Py.DictOrEmpty(opts.Get("probeCmd")).Get(name);
        if (Py.Truthy(over)) return ToolIdentity.ProbeCmd(Py.S(over));
        return ToolIdentity.Package(Path.GetDirectoryName(ProbeExe(harness, name))!);
    }

    /// <summary>Every program that can launch under a plan: the controller; the probe of every child-launching check
    /// with a planned arm; conflictdiff when a swept check plans an arm other than the baseline.</summary>
    public static HashSet<string> Launchable(IEnumerable<PyDict> checks, IReadOnlyDictionary<string, string> armKinds)
    {
        var programs = new HashSet<string>(StringComparer.Ordinal) { Controller };
        foreach (var c in checks)
        {
            var ad = Py.DictOrEmpty(c.Get("adapter"));
            var planned = Py.ListOrEmpty(c.Get("arms")).Select(Py.D).Where(r => r.Get("status") as string == "planned").ToList();
            if (planned.Count == 0) continue;
            string? kind = ad.Get("kind") as string;
            if (kind != null && LaunchKinds.Contains(kind) && Py.Truthy(ad.Get("exe"))) programs.Add(ProgramId(Py.S(ad["exe"])));
            if (kind == "ordersweep-probe" && planned.Any(r => armKinds.GetValueOrDefault(Py.S(r["armId"])) != "baseline")) programs.Add(Comparator);
        }
        return programs;
    }
}
