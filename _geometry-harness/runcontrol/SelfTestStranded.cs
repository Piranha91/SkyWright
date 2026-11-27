using System.Diagnostics;

namespace RunControl;

/// <summary>The stranded check (geometry.stranded-object) through runcontrol's own parts: the registry row is held (opt-in)
/// and planned only when named, and the real conflictdiff and the sweep's created-row split read strandprobe's KS09 emits
/// (an object a ticked mod places, stranded with it, absent without it) as a new defect of the arm (Gate A review finding 4).</summary>
static class SelfTestStranded
{
    const string Id = "geometry.stranded-object";

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
        Case("stranded: the registry row is opt-in, planned not-opted-in without --opt-in and planned with it", () =>
        {
            var reg = Registry.Load(Checkout.Registry);
            if (Registry.CheckById(reg, Id) is not { } check) return $"the registry has no {Id}";
            if (check.Get("optIn") is not true) return $"{Id} is not opt-in";
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

        const string diffCase = "stranded: the real conflictdiff credits KS09's row created on the arm, and the reverse makes it a new defect";
        string sp = Checkout.Program("strandprobe"), cd = Checkout.Program("conflictdiff");
        if (!File.Exists(sp) || !File.Exists(cd)) { Skip(diffCase, $"no built {(File.Exists(sp) ? "conflictdiff" : "strandprobe")} in this build"); return; }
        Case(diffCase, () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "runcontrol-stranded-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var (code, said) = Launch(sp, "--write-ks09-emits", dir);
                if (code != 0) return $"strandprobe --write-ks09-emits exited {code}: {said[^Math.Min(400, said.Length)..]}";
                var subject = said.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("the subject is ", StringComparison.Ordinal))?.Split("the subject is ")[1].Trim();
                if (subject is null) return "strandprobe did not name KS09's subject";
                string b = Path.Combine(dir, "baseline.json"), a = Path.Combine(dir, "arm.json"), r = Path.Combine(dir, "reverse.json");
                var (dcode, dtext) = Launch(cd, b, a);
                var diff = OrderSweep.ParseDiff(dtext);
                long created = PyValue.Truthy(PyValue.Dict(diff["counts"]).Get("created")) ? (long)PyValue.Int(PyValue.Dict(diff["counts"])["created"]) : 0;
                if (dcode != 0 || created < 1) return $"conflictdiff exit {dcode}, created {created}: {dtext[^Math.Min(400, dtext.Length)..]}";
                var split = OrderSweep.CreatedBreakdown(b, a, r);
                var rev = PyValue.Dict(PyJson.Loads(File.ReadAllText(r)));
                var pred = Py.L(rev["Predicates"]).Select(Py.D).FirstOrDefault(p => Py.S(p["FormKey"]) == subject);
                string? phys = pred is null ? null : pred["Physical"] is string s ? s : PyText.Str(pred["Physical"]);
                return (long)PyValue.Int(split["newDefect"]) >= 1 && phys is "Gone" or "1"
                    ? null : $"new defects {PyText.Str(split["newDefect"])}, the subject's reverse predicate {phys ?? "missing"}";
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        });
    }
}
