using System.Text;

namespace RunControl;

/// <summary>STATUS.md, the review index finalize writes beside the disposition (evalrun.py's status_md). Every value
/// is printed the way Python's str() prints it: True, False, None, and a list or dict as its repr.</summary>
public static class StatusMd
{
    // str(v), as an f-string prints a value.
    static string P(object? v) => PyText.Str(v);

    // `v or fallback` printed.
    static string Or(object? v, string fallback) => PyValue.Truthy(v) ? P(v) : fallback;

    // text[:n], counted in code points as Python counts.
    static string Head(string s, int n)
    {
        int i = 0;
        for (int count = 0; i < s.Length && count < n; count++)
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }

    static PyDict D(object? v) => PyValue.Dict(v);

    /// <summary>The whole file's text, with "\n" line ends.</summary>
    public static string Build(PyDict plan, PyDict disp) => Build(plan, disp, null);

    /// <summary>As above, with a caller's sections (StatusMdSections): lines added at the places it names, never a
    /// line of this index changed or left out.</summary>
    public static string Build(PyDict plan, PyDict disp, StatusMdSections? more)
    {
        var ev = D(disp["evaluation"]);
        var registry = D(plan["registry"]);
        var identity = Ev.OrEmpty(ev.Get("toolIdentity"));
        var L = new List<string>
        {
            $"# Evaluation status — plan `{P(plan["planId"])}`", "",
            $"- registry version {P(registry["registryVersion"])} (`{Head((string)registry["sha256"]!, 12)}`)",
            "- arms: " + string.Join(", ", PyValue.Iter(plan["arms"]).Select(a => $"`{P(D(a)["armId"])}` ({P(D(a)["kind"])})")),
            "- states selected: " + string.Join(", ", PyValue.Iter(D(plan["states"])["selected"]).Select(s => (string)s!)),
            $"- **evaluation.complete: {P(ev["complete"])}**   claim: `{P(ev["claim"])}`",
            $"- tool identity: {P(Ev.G(identity, "guarantee", "legacy"))} — "
            + P(Ev.G(identity, "detail", IdentityChecks.Guarantee(new PyDict())["detail"])), "",
            "This is the review index, not the report. Every line below is derived from the ledger and the "
            + "validated receipts; nothing here is a judgement this runner made about the mod.", "",
        };
        var integrity = PyValue.Truthy(ev.Get("integrity")) ? D(ev["integrity"]) : new PyDict { ["ok"] = true, ["defects"] = new List<object?>() };
        if (!PyValue.Truthy(integrity.Get("ok")))
        {
            // defects first: a reader must not scroll past a completed claim to learn its evidence is gone
            int n = PyValue.Len(integrity["defects"]);
            L.AddRange([
                "## Evidence integrity — REFUSED", "",
                $"`evaluation.refused: evidence-integrity`. {n} completed entr{(n == 1 ? "y" : "ies")} no longer match the evidence recorded "
                + "for them, so nothing below counts them as completed.", "",
                "| entry | defect | path | recorded | found | next action |",
                "|---|---|---|---|---|---|"]);
            foreach (var d in PyValue.Iter(integrity["defects"]).Select(D))
                L.Add($"| `{P(d["key"])}` | `{P(d["kind"])}` | {Or(d["path"], "-")} | "
                    + $"{Head(Or(d["recorded"], "-"), 12)} | {Head(Or(d["found"], "-"), 12)} | {P(d["nextAction"])} |");
            L.AddRange(["", "The ledger was not rewritten. `resume --run RUN` invalidates and re-runs the affected entries.", ""]);
        }
        L.AddRange(["## Findings", ""]);
        if (PyValue.Truthy(ev["findings"]))
        {
            L.AddRange(["| check | arm | part | findings | verdict | strength |", "|---|---|---|---|---|---|"]);
            foreach (var f in PyValue.Iter(ev["findings"]).Select(D))
                L.Add($"| `{P(f["checkId"])}` | {P(f["armId"])} | {Or(f["part"], "-")} | {P(f["findings"])} | "
                    + $"{Or(f["verdict"], "-")} | {Or(f["strength"], "-")} |");
        }
        else L.Add("No completed check reported a finding. That is a statement about the completed checks only.");
        L.AddRange(["", "## Completed null checks", ""]);
        if (PyValue.Truthy(ev["completedNullChecks"]))
            foreach (var c in PyValue.Iter(ev["completedNullChecks"]).Select(D))
                L.Add($"- `{P(c["checkId"])}` [{P(c["armId"])}{(PyValue.Truthy(c["part"]) ? "/" + (string)c["part"]! : "")}] — no finding, "
                    + $"{P(c["coverage"])} coverage line(s) declared");
        else L.Add("- none");
        L.AddRange(["", "## Completed catalogues (rows that are evidence, not defects)", ""]);
        if (PyValue.Truthy(ev["completedCatalogues"]))
            foreach (var c in PyValue.Iter(ev["completedCatalogues"]).Select(D))
                L.Add($"- `{P(c["checkId"])}` [{P(c["armId"])}] — {P(c["count"])} {P(c["unit"])} catalogued "
                    + $"({P(c["kind"])}); a catalogue completes its check and is never a finding");
        else L.Add("- none");
        if (more?.AfterCatalogues is { } after) L.AddRange(after(disp));
        L.AddRange(["", "## Missing checks (each keeps the evaluation incomplete)", ""]);
        if (PyValue.Truthy(ev["missing"]))
        {
            L.AddRange(["| check | arm | status | cause |", "|---|---|---|---|"]);
            foreach (var m in PyValue.Iter(ev["missing"]).Select(D))
                L.Add($"| `{P(m["checkId"])}` | {P(m["armId"])} | `{P(m["status"])}` | {P(m["cause"])} |");
        }
        else L.Add("- none");
        if (PyValue.Truthy(ev["unknownApplicability"]))
        {
            L.AddRange(["", "## Unknown applicability (never read as not-applicable)", ""]);
            foreach (var u in PyValue.Iter(ev["unknownApplicability"]).Select(D))
                L.Add($"- `{P(u["checkId"])}` — {P(u["evidence"])}");
        }
        L.AddRange(["", "## Order evaluation per arm", ""]);
        foreach (var (aid, armRaw) in D(disp["arms"]))
        {
            var arm = D(armRaw);
            var oe = D(arm["orderEvaluation"]);
            string probes = string.Join(", ", PyValue.Iter(oe["probes"]).Select(p => (string)p!));
            L.Add($"- `{aid}` ({P(arm["kind"])}): classification **{Or(oe["classification"], "n/a")}**, "
                + $"regression: {P(oe["regression"])}, probes: {(probes.Length > 0 ? probes : "none")}");
            if (PyValue.Truthy(oe.Get("classificationNote"))) L.Add($"  - {P(oe["classificationNote"])}");
            if (PyValue.Truthy(D(Ev.G(arm, "payload", new PyDict())).Get("espOnly"))) L.Add($"  - {P(D(arm["payload"])["note"])}");
            if (PyValue.Truthy(oe.Get("note"))) L.Add($"  - {P(oe["note"])}");
            foreach (var (p, vRaw) in D(oe["perProbe"]))
            {
                var v = D(vRaw);
                L.Add($"  - {p}: {P(v["verdict"])} — repaired {P(v["repaired"])}, reclassified {P(v["reclassified"])}, "
                    + $"created {P(v["created"])} (new {P(v["newDefect"])} / attributed {P(v["attributed"])}), "
                    + $"unmeasured {P(v["unmeasured"])}");
            }
        }
        L.AddRange(["", "## Limitations and what was not assessed", ""]);
        var un = D(disp["unassessed"]);
        foreach (var (label, key) in new[]
                 {
                     ("states not explored", "statesNotExplored"), ("candidates held back", "candidatesHeldBack"),
                     ("families unsupported or manual", "familiesUnsupportedOrManual"),
                     ("scope extents the footprint probe does not read", "scopeUnmeasured"),
                 })
            L.Add($"- {label}: {Head(PyJson.Canonical(un[key]), 400)}");
        foreach (var p in Ev.Items(un.Get("unjudgeablePopulations")).Select(D))
            // a completed check can still leave part of its population unjudged; it is stated here, not only counted
            L.Add($"- `{P(p["checkId"])}` [{P(p["armId"])}] {P(p["population"])}: {P(p["judged"])} judged, not judged "
                + PyJson.Canonical(p["unjudgeable"]));
        // supplied media is the houseCARL integration module's: a disposition that carries its sentence shows it
        if (un.ContainsKey("mediaUnassessed")) L.Add($"- media: {P(un["mediaUnassessed"])}");
        if (more?.Limitations is { } limits) L.AddRange(limits(disp));
        L.Add($"- runtime: {P(un["runtimeObservations"])}");
        L.AddRange(["", "## Regeneration needs", ""]);
        var regen = PyValue.Iter(plan["checks"]).Select(D).Where(c => PyValue.Truthy(Ev.OrEmpty(c.Get("evidence")).Get("regeneration"))).ToList();
        if (regen.Count > 0)
            foreach (var c in regen) L.Add($"- `{P(c["checkId"])}`: {P(D(c["evidence"])["regeneration"])}");
        else L.Add("- none declared by the registry for the checks in this plan");
        L.AddRange(["", "## Explicit decisions", ""]);
        var ed = D(plan["explicitDecisions"]);
        if (PyValue.Truthy(disp.Get("budgetsInForce")))
            L.Add($"- time limit: the plan froze {P(D(plan["budgets"]).Get("minutes"))} minute(s); limits in force: "
                + string.Join(", ", PyValue.Iter(disp["budgetsInForce"]).Select(b => $"{P(D(b)["command"])} {P(D(b)["budgetMinutes"])}")));
        L.Add($"- --select: {(ed["select"] != null ? P(ed["select"]) : "not used")}");
        L.Add($"- --exclude: {Or(ed["exclude"], "not used")}");
        if (PyValue.Truthy(disp["manualDecisions"]))
        {
            L.AddRange(["", "## Manual decisions", ""]);
            foreach (var d in PyValue.Iter(disp["manualDecisions"]).Select(D))
                L.Add($"- `{P(d["checkId"])}` [{P(d["armId"])}] {P(d["decision"])} by {P(d["decidedBy"])} — "
                    + $"accepted: {P(d["accepted"])}" + (PyValue.Truthy(d.Get("why")) ? $"; {P(d["why"])}" : ""));
        }
        L.AddRange(["", "## Ledger", "", "| status | entries |", "|---|---|"]);
        foreach (var (k, n) in D(disp["ledgerCounts"])) L.Add($"| `{k}` | {P(n)} |");
        return string.Join("\n", L) + "\n";
    }
}

/// <summary>A caller's sections of STATUS.md (another program finalizing a run of its own): each returns the lines it
/// adds at its place, given the disposition. AfterCatalogues follows "Completed catalogues"; Limitations goes among the
/// "Limitations and what was not assessed" lines, before the runtime line.</summary>
public sealed record StatusMdSections(Func<PyDict, IEnumerable<string>>? AfterCatalogues = null, Func<PyDict, IEnumerable<string>>? Limitations = null);
