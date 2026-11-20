namespace RunControl;

/// <summary>Reuse of what an earlier invocation completed (evalrun.py's revalidate): every completed or reused entry
/// is re-checked against the disk, then invalidated transitively through what it depends on.</summary>
public static class Revalidate
{
    /// <summary>Per entry, first match: the receipt gone, another plan's receipt, an input that cannot be re-checked
    /// (reuse refused), a changed input (invalidated), a dependency invalidated; a survivor is reused.</summary>
    public static void Run(string run, PyDict plan, Ledger ledger)
    {
        var receipts = Receipts.ReadReceipts(run);
        var memo = new HashMemo();
        var invalidated = new PyDict();
        var refused = new PyDict();

        foreach (var (k, raw) in ledger.Entries.ToList())
        {
            var e = PyValue.Dict(raw);
            if (!EvalStore.IsTerminalOk(e.Get("status"))) continue;
            if (!receipts.ContainsKey(k) || receipts[k] == null)
            {
                invalidated[k] = "the receipt file is gone";
                continue;
            }
            var r = PyValue.Dict(receipts[k]);
            if (!Ev.Eq(r.Get("planId"), plan["planId"]))
            {
                invalidated[k] = $"foreign-run: the receipt belongs to plan {Ev.Str(r.Get("planId"))}";
                continue;
            }
            if (ReuseGap(r) is { } gap)
            {
                refused[k] = gap;
                continue;
            }
            if (ChangedInput(run, r, memo) is { } changed) invalidated[k] = changed;
        }

        // transitive: an entry follows every key it depends on
        bool again = true;
        while (again)
        {
            again = false;
            foreach (var (k, raw) in ledger.Entries.ToList())
            {
                var e = PyValue.Dict(raw);
                if (invalidated.ContainsKey(k) || refused.ContainsKey(k) || !EvalStore.IsTerminalOk(e.Get("status"))) continue;
                foreach (string d in Receipts.DependencyKeys(plan, receipts, e))
                    if (invalidated.ContainsKey(d) || refused.ContainsKey(d))
                    {
                        invalidated[k] = $"an input it depends on was invalidated ({d})";
                        again = true;
                        break;
                    }
            }
        }

        foreach (var (k, gap) in refused)
        {
            var e = PyValue.Dict(ledger.Entries[k]);
            ledger.Transition((string)e["checkId"]!, (string)e["armId"]!, e.Get("part") as string, $"refused-reuse({gap})",
                cause: "stale-inputs", detail: (string)gap!,
                nextAction: "re-run this check; an input it cannot state cannot be re-checked, so measured evidence is not reused on this entry");
        }
        foreach (var (k, why) in invalidated)
        {
            var e = PyValue.Dict(ledger.Entries[k]);
            ledger.Transition((string)e["checkId"]!, (string)e["armId"]!, e.Get("part") as string, $"invalidated({why})",
                cause: "stale-inputs", detail: (string)why!,
                nextAction: "restore the named file and resume, or re-run this check to measure the inputs as they are now");
        }
        var kept = ledger.Entries.Where(kv => EvalStore.IsTerminalOk(Ev.G(kv.Value, "status"))).Select(kv => kv.Key).ToList();
        foreach (string k in kept)
        {
            var e = PyValue.Dict(ledger.Entries[k]);
            if (Ev.Eq(e.Get("status"), "completed"))
                ledger.Transition((string)e["checkId"]!, (string)e["armId"]!, e.Get("part") as string, "reused",
                    receiptId: e.Get("receiptId") as string, detector: e.Get("detector") as string,
                    detail: "every stated input still hashes to its recorded value", nextAction: "nothing: this measurement stands",
                    extra: new PyDict { ["reusedFromRunId"] = e.Get("runId") });
        }
        Console.Out.WriteLine($"   resume: {kept.Count} receipt(s) reused, {invalidated.Count} invalidated, {refused.Count} refused-reuse");
    }

    /// <summary>Every input of an accepted receipt that cannot be re-checked at all, named; null when there is none.
    /// A gap is not a change: reuse is refused because the entry's inputs cannot be stated.</summary>
    public static string? ReuseGap(PyDict receipt)
    {
        if (!PyValue.Truthy(Ev.OrEmpty(receipt.Get("validation")).Get("accepted"))) return null;
        var c = Ev.OrEmpty(receipt.Get("consumed"));
        var cfg = Ev.OrEmpty(c.Get("configuration"));
        var gaps = new List<string>();
        if (PyValue.Truthy(cfg.Get("reuseGap"))) gaps.Add(Ev.Str(cfg["reuseGap"]));
        var plugins = Ev.Items(cfg.Get("plugins")).Select(PyValue.Dict).ToList();
        if (plugins.Count == 0) gaps.Add("the receipt names no consumed plugins, so what it read cannot be re-checked on disk");
        foreach (var p in plugins)
        {
            string name = PyValue.Truthy(p.Get("name")) ? Ev.Str(p["name"]) : "(unnamed)";
            if (!PyValue.Truthy(p.Get("path"))) gaps.Add($"consumed plugin {name} has no path");
            if (!PlanArms.IsSha256(p.Get("sha256"))) gaps.Add($"consumed plugin {name} has no 64-hex sha256");
        }
        foreach (var o in Ev.Items(cfg.Get("overlays")).Select(PyValue.Dict))
            if (!PyValue.Truthy(o.Get("path")) || !PlanArms.IsSha256(o.Get("sha256")))
                gaps.Add($"consumed overlay {(PyValue.Truthy(o.Get("kind")) ? Ev.Str(o["kind"]) : "(unnamed)")} has no path or no sha256");
        foreach (var o in Ev.Items(receipt.Get("outputs")).Select(PyValue.Dict))
            if (o.Get("sha256") == null) gaps.Add($"output {Ev.Str(o.Get("path"))} has no hash");
        var tool = Ev.OrEmpty(c.Get("tool"));
        if (PyValue.Truthy(tool.Get("present")) && !PlanArms.IsSha256(tool.Get("dllSha256")))
            gaps.Add($"the tool {PyPath.Name(PyValue.Truthy(tool.Get("exe")) ? Ev.Str(tool["exe"]) : "?")} is present but its managed "
                     + "assembly has no recorded hash, so the code that ran cannot be identified");
        // a comparison input recorded without a hash cannot be verified, so reuse refuses it
        if (PyValue.Truthy(c.Get("baseEmitPath")) && !PlanArms.IsSha256(c.Get("baseEmitSha256")))
            gaps.Add($"the base emit {Ev.Str(c["baseEmitPath"])} has no recorded hash");
        foreach (var row in Ev.Items(c.Get("inputs")).Select(PyValue.Dict))
            if (PyValue.Truthy(row.Get("path")) && !PlanArms.IsSha256(row.Get("sha256")))
                gaps.Add($"the {(PyValue.Truthy(row.Get("part")) ? Ev.Str(row["part"]) : "comparison")} input {Ev.Str(row["path"])} has no recorded hash");
        return gaps.Count > 0 ? string.Join("; ", gaps) : null;
    }

    /// <summary>Everything a receipt rests on that is gone or hashes differently now, each named with its path; null
    /// when nothing changed. A file that is gone is a change.</summary>
    public static string? ChangedInput(string run, PyDict receipt, HashMemo? memo = null)
    {
        memo ??= new HashMemo();
        var changes = new List<string>();
        void Verify(string p, object? recorded, string what)
        {
            if (!Ev.Exists(p)) changes.Add($"{what} is gone at {p}");
            else if (!Ev.Eq(memo.Sha256(p), recorded)) changes.Add($"{what} changed on disk at {p}");
        }
        foreach (var o in Ev.Items(receipt.Get("outputs")).Select(PyValue.Dict))
            if (o.Get("sha256") != null) Verify(PyPath.Join(run, (string)o["path"]!), o["sha256"], $"output {Ev.Str(o["path"])}");
        var c = Ev.OrEmpty(receipt.Get("consumed"));
        if (PyValue.Truthy(c.Get("candidatePath")) && PyValue.Truthy(c.Get("candidateSha256")))
            Verify(PyPath.Str((string)c["candidatePath"]!), c["candidateSha256"], $"the candidate file {PyPath.Name((string)c["candidatePath"]!)}");
        var cfg = Ev.OrEmpty(c.Get("configuration"));
        foreach (var p in Ev.Items(Ev.G(cfg, "plugins", new List<object?>())).Select(PyValue.Dict))
        {
            if (!PyValue.Truthy(p.Get("path")) || !PlanArms.IsSha256(p.Get("sha256"))) continue;   // not re-checkable: ReuseGap names it
            Verify(PyPath.Str((string)p["path"]!), p["sha256"], $"plugin {Ev.Str(p.Get("name"))}");
        }
        foreach (var o in Ev.Items(Ev.G(cfg, "overlays", new List<object?>())).Select(PyValue.Dict))
        {
            if (!PyValue.Truthy(o.Get("path")) || !PlanArms.IsSha256(o.Get("sha256"))) continue;
            Verify(PyPath.Str((string)o["path"]!), o["sha256"], $"overlay {PyPath.Name((string)o["path"]!)}");
        }
        var tool = Ev.OrEmpty(c.Get("tool"));
        if (PyValue.Truthy(tool.Get("present")) && PyValue.Truthy(tool.Get("exe")))
        {
            string exe = PyPath.Str((string)tool["exe"]!);
            string folder = Path.GetDirectoryName(exe) ?? "", name = Ev.Stem(exe);
            foreach (var (file, recorded, label) in new[]
                     {
                         ($"{name}.dll", tool.Get("dllSha256"), $"the managed assembly {name}.dll"),
                         ("housecarl-core.dll", tool.Get("coreDllSha256"), "the core assembly housecarl-core.dll"),
                     })
                if (PlanArms.IsSha256(recorded)) Verify(PyPath.Join(folder, file), recorded, label);
        }
        if (PyValue.Truthy(c.Get("baseEmitPath")) && PyValue.Truthy(c.Get("baseEmitSha256")))
            Verify(PyPath.Join(run, (string)c["baseEmitPath"]!), c["baseEmitSha256"], $"the base emit {Ev.Str(c["baseEmitPath"])}");
        foreach (var row in Ev.Items(c.Get("inputs")).Select(PyValue.Dict))
            if (PyValue.Truthy(row.Get("path")) && PlanArms.IsSha256(row.Get("sha256")))
                Verify(PyPath.Join(run, (string)row["path"]!), row["sha256"],
                    $"the {(PyValue.Truthy(row.Get("part")) ? Ev.Str(row["part"]) : "comparison")} input {Ev.Str(row["path"])}");
        return changes.Count > 0 ? string.Join("; ", changes) : null;
    }
}
