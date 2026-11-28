using System.Diagnostics;

namespace RunControl;

/// <summary>The registration and dialogue check (records.registration-and-dialogue, record checks unit 4) through
/// runcontrol's own parts: the registry row is held (opt-in, its own reason), runs recordprobe through the probe-emit adapter
/// with the plan's population, and is planned only when named; the real recordprobe writes a conflict file runcontrol reads,
/// with its one form-list item lost between two mods, and runcontrol's validator accepts it.</summary>
static class SelfTestRegistration
{
    const string Id = "records.registration-and-dialogue";

    static (int exit, string text) Launch(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result + e.Result);
    }

    public static void Run(Action<string, Func<string?>> Case, Action<string, string> Skip)
    {
        Case("registration and dialogue: the registry row is held, runs recordprobe as a probe-emit with the population, and is planned only with --opt-in", () =>
        {
            var reg = Registry.Load(Checkout.Registry);
            if (Registry.CheckById(reg, Id) is not { } check) return $"the registry has no {Id}";
            if (check.Get("optIn") is not true || check.Get("required") is not false || check.Get("heldWhy") is not string { Length: > 0 })
                return $"{Id} is not held (optIn true, required false, its own heldWhy)";
            var ad = PyValue.Dict(check["adapter"]);
            if (Py.S(ad["kind"]) != "probe-emit" || Py.S(ad["exe"]) != "recordprobe" || Py.S(ad.Get("populationFlag")) != "--population"
                || !Py.L(ad["args"]).Select(Py.S).SequenceEqual(new[] { "--check", Id }))
                return $"{Id}'s adapter is not recordprobe --check {Id} as a probe-emit with --population";
            var problems = Registry.Validate(reg);
            if (problems.Count > 0) return "the registry does not validate: " + string.Join("; ", problems);
            var arms = new List<PyDict> { new() { ["armId"] = "baseline", ["kind"] = "baseline" }, new() { ["armId"] = "combined", ["kind"] = "candidate" } };
            var app = Registry.Applicability(reg, null);
            string StatusOf(IReadOnlySet<string> opted)
            {
                var (checks, _, _, _, _) = Plan.FreezeChecks([check], app, arms, "baseline", opted, new PyDict(), null, Checkout.Harness, ["unsupported", "manual"]);
                return Py.S(checks[0]["status"]);
            }
            string without = StatusOf(new HashSet<string>()), with = StatusOf(new HashSet<string> { Id });
            return without == EvalStore.NotOptedIn && with == "planned" ? null : $"without --opt-in {without}, with it {with}";
        });

        const string emitCase = "registration and dialogue: the real recordprobe writes a conflict file with exactly its one form-list item lost between two mods and its coverage, which the validator accepts";
        string rp = Checkout.Program("recordprobe");
        if (!File.Exists(rp)) { Skip(emitCase, "no built recordprobe in this build"); return; }
        Case(emitCase, () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "runcontrol-registration-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var (code, said) = Launch(rp, "--write-self-test-emit", dir);
                if (code != 0) return $"recordprobe --write-self-test-emit exited {code}: {said[^Math.Min(400, said.Length)..]}";
                var emit = PyValue.Dict(PyJson.Loads(File.ReadAllText(Path.Combine(dir, "registration.json"))));
                var rows = Py.L(emit["Conflicts"]).Select(Py.D).ToList();
                var cov = Py.L(emit["Coverage"]).Select(Py.D).ToList();
                if (rows.Count != 1 || Py.S(rows[0]["Kind"]) != "LOST-EDIT" || Py.S(rows[0]["A"]) != "Base.esp" || Py.S(rows[0]["B"]) != "Other.esp")
                    return $"{rows.Count} row(s), not the one LOST-EDIT row Base.esp / Other.esp";
                if (cov.Count < 1 || (long)PyValue.Int(cov[0]["Judged"]) != 1) return "the coverage does not judge exactly the one form list's version";
                return Validator.NothingAssessed(Validator.CoverageLines(emit)) is { } refused ? "the validator refuses it: " + refused : null;
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        });
    }
}
