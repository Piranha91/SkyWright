namespace RunControl;

/// <summary>Which terminal-ok ledger entries still have the evidence their claim rests on (evalrun.py's
/// evidence_integrity). Nothing is rewritten: recovery is resume.</summary>
public static class EvidenceIntegrity
{
    /// <summary>({ok, defects}, ledger key -> the status word for evaluation.missing).</summary>
    public static (PyDict integrity, PyDict bad) Check(string run, PyDict plan, Ledger ledger, PyDict receipts,
        IReadOnlySet<string>? moreLaunchKinds = null)
    {
        var defects = new List<object?>();
        var bad = new PyDict();
        var memo = new HashMemo();
        foreach (var (k, eRaw) in ledger.Entries)
        {
            var e = PyValue.Dict(eRaw);
            if (!EvalStore.IsTerminalOk(e.Get("status"))) continue;
            string rpath = PyPath.Join(run, "receipts", $"{k}.json");
            if (!receipts.ContainsKey(k) || receipts[k] == null)
            {
                defects.Add(new PyDict
                {
                    ["key"] = k, ["kind"] = "receipt-mismatch", ["path"] = $"receipts/{k}.json",
                    ["recorded"] = e.Get("receiptId"), ["found"] = null,
                    ["nextAction"] = "resume: the receipt this claim rests on cannot be read",
                });
                bad[k] = "receipt-mismatch";
                continue;
            }
            var r = PyValue.Dict(receipts[k]);
            if (PyValue.Truthy(e.Get("receiptId")) && !Ev.Eq(r.Get("receiptId"), e.Get("receiptId")))
            {
                defects.Add(new PyDict
                {
                    ["key"] = k, ["kind"] = "receipt-mismatch", ["path"] = PyPath.RelTo(run, rpath),
                    ["recorded"] = e.Get("receiptId"), ["found"] = r.Get("receiptId"),
                    ["nextAction"] = "resume: the ledger and the receipt name different measurements",
                });
                bad[k] = "receipt-mismatch";
                continue;
            }
            if (!PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted")))
            {
                defects.Add(new PyDict
                {
                    ["key"] = k, ["kind"] = "receipt-mismatch", ["path"] = PyPath.RelTo(run, rpath),
                    ["recorded"] = "accepted", ["found"] = "not accepted",
                    ["nextAction"] = "resume: a terminal-ok entry whose receipt was not accepted cannot complete a check",
                });
                bad[k] = "receipt-mismatch";
                continue;
            }
            // the programs it was accepted under, per role, against the identities the plan froze
            var idd = IdentityChecks.Defects(plan, r, moreLaunchKinds);
            if (idd.Count > 0)
            {
                var d = idd[0];
                defects.Add(new PyDict
                {
                    ["key"] = k, ["kind"] = d.Kind, ["path"] = PyPath.RelTo(run, rpath), ["role"] = d.Role, ["program"] = d.Program,
                    ["recorded"] = d.Recorded, ["found"] = d.Found,
                    ["nextAction"] = $"this result's {d.Role} is not {Receipts.ProgramLabel(d.Program)} as the plan recorded it, "
                        + "so it cannot be credited under this plan; make a new plan in a new directory and run it",
                });
                bad[k] = $"{d.Kind}({d.Role})";
                continue;
            }
            // its own outputs and the run-internal inputs it was measured against
            var rows = new List<(object? rel, object? recorded, string label)>();
            foreach (var o in Ev.Items(r.Get("outputs"))) rows.Add((Ev.G(o, "path"), Ev.G(o, "sha256"), "output"));
            var c = Ev.OrEmpty(r.Get("consumed"));
            if (PyValue.Truthy(c.Get("baseEmitPath"))) rows.Add((c["baseEmitPath"], c.Get("baseEmitSha256"), "base emit"));
            foreach (var row in Ev.Items(c.Get("inputs")))
                rows.Add((Ev.G(row, "path"), Ev.G(row, "sha256"), $"{(PyValue.Truthy(Ev.G(row, "part")) ? Ev.Str(Ev.G(row, "part")) : "comparison")} input"));
            foreach (var (relRaw, recorded, label) in rows)
            {
                if (!PyValue.Truthy(relRaw)) continue;
                string rel = Ev.Str(relRaw);
                string p = PyPath.Join(run, rel);
                if (recorded == null || !Ev.Exists(p))
                {
                    defects.Add(new PyDict
                    {
                        ["key"] = k, ["kind"] = "evidence-missing", ["path"] = relRaw, ["recorded"] = recorded, ["found"] = null,
                        ["nextAction"] = $"restore the {label} and finalize again, or resume to re-run the check",
                    });
                    bad[k] = $"evidence-missing({rel})";
                    break;
                }
                string? got = memo.Sha256(p);
                if (!Ev.Eq(got, recorded))
                {
                    defects.Add(new PyDict
                    {
                        ["key"] = k, ["kind"] = "evidence-changed", ["path"] = relRaw, ["recorded"] = recorded, ["found"] = got,
                        ["nextAction"] = $"resume: the {label} this claim rests on is not the file that was measured",
                    });
                    bad[k] = $"evidence-changed({rel})";
                    break;
                }
            }
        }

        // transitively, along the edges resume uses: an entry whose input's claim is void has no claim of its own left
        bool spreading = true;
        while (spreading)
        {
            spreading = false;
            foreach (var (k, eRaw) in ledger.Entries)
            {
                var e = PyValue.Dict(eRaw);
                if (bad.ContainsKey(k) || !EvalStore.IsTerminalOk(e.Get("status"))) continue;
                foreach (string d in Receipts.DependencyKeys(plan, receipts, e))
                {
                    if (!bad.ContainsKey(d)) continue;
                    bad[k] = $"evidence-dependency({d})";
                    defects.Add(new PyDict
                    {
                        ["key"] = k, ["kind"] = "evidence-dependency", ["path"] = null, ["recorded"] = d, ["found"] = null,
                        ["nextAction"] = $"resume: this entry rests on {d}, whose evidence is not what was measured",
                    });
                    spreading = true;
                    break;
                }
            }
        }
        return (new PyDict { ["ok"] = defects.Count == 0, ["defects"] = defects }, bad);
    }
}
