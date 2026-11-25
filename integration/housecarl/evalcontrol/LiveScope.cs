using RunControl;

namespace EvalControl;

/// <summary>The live switch check's reading of one arm (moved from runcontrol's Prerequisites with the check): the
/// arm's accepted state.switch-census output against the plan's scope, by SwitchFlipScope.Population. In an overlay the
/// census is the app scan's, read from the app bundle the overlay is bound to (its ledger, its receipt and the output
/// it copied under evidence/); in a run whose own plan holds the census (a test registry's), from that run, as before
/// the move.</summary>
public static class LiveScope
{
    /// <summary>The live switch check's id (ai-checks.json).</summary>
    public const string CheckId = "runtime.state-switch-flip";
    const string Census = "state.switch-census";

    /// <summary>Null when there is no accepted census output or either document cannot be read.</summary>
    public static PyDict? Read(string run, PyDict plan, Ledger ledger, object? armId)
    {
        string root = run, copied = "";
        var censusLedger = ledger;
        if (plan.Get(OverlayPlan.Key) is PyDict bound)
        {
            root = PyPath.Resolve(PyPath.Join(run, Ev.Str(bound.Get("appBundle"))));
            if (PyJson.LoadEvalFile(PyPath.Join(root, "plan.json")) is not PyDict appPlan || !Ev.Exists(PyPath.Join(root, "ledger.json"))) return null;
            censusLedger = new Ledger(root, appPlan);
            copied = "evidence";   // a bundle holds each receipt output under evidence/, at the path the receipt names
        }
        var e = censusLedger.Entry(Census, armId, null);
        object? r = PyJson.LoadEvalFile(PyPath.Join(PyPath.Join(root, "receipts"), $"{Census}__{Ev.Str(armId)}.json"));
        if (!(PyValue.Truthy(e) && EvalStore.IsTerminalOk(e!.Get("status")) && PyValue.Truthy(r)
              && PyValue.Truthy(Ev.OrEmpty(Ev.G(r, "validation")).Get("accepted")))) return null;
        var outs = Ev.Items(Ev.G(r, "outputs")).ToList();
        if (outs.Count == 0 || !PyValue.Truthy(Ev.G(outs[0], "path"))) return null;
        string rel = Ev.Str(Ev.G(outs[0], "path"));
        string cpath = copied == "" ? PyPath.Join(root, rel) : PyPath.Join(PyPath.Join(root, copied), rel);
        object? census = PyJson.LoadEvalFile(cpath);
        object? scopePath = Ev.OrEmpty(plan.Get("scope")).Get("path");
        object? scope = PyValue.Truthy(scopePath) ? PyJson.LoadEvalFile(PyPath.Str(Ev.Str(scopePath))) : null;
        if (census is not PyDict c || scope is not PyDict s) return null;
        var res = SwitchFlipScope.Population(c, s);
        res["census"] = new PyDict { ["path"] = Ev.G(outs[0], "path"), ["sha256"] = Hashing.Sha256FileOrNull(cpath) };
        return res;
    }

    /// <summary>What a not-applicable receipt records: the census it read, the counts and the rule.</summary>
    public static PyDict Record(PyDict res) => new()
    {
        ["census"] = res["census"], ["counts"] = res["counts"], ["status"] = res["status"], ["rule"] = "switchflip_scope.population",
    };

    /// <summary>What a run says about the engine at play time: nothing, unless an accepted live receipt is bound to it
    /// (moved from runcontrol's disposition with the live check; an app scan's says nothing was supplied).</summary>
    public static string RuntimeObservations(PyDict receipts)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, rRaw) in receipts)
        {
            var r = PyValue.Dict(rRaw);
            if (Ev.Eq(Ev.OrEmpty(r.Get("adapter")).Get("kind"), "live-import") && PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted")))
                live.Add((string)r.Get("checkId")!);
        }
        if (live.Count == 0) return Disposition.NoRuntimeObservations;
        return "accepted live receipts for " + string.Join(", ", PyOrder.SortedByCodePoint(live))
            + " say what the engine did in the conditions their flip plans list, and nothing about any other condition";
    }
}
