namespace RunControl;

/// <summary>The rows of the derived checks (adapter kind derived): computed from other receipts, no child. Not built
/// in this version: a plan that has such a row to settle is refused before anything is written.</summary>
public static class DerivedRows
{
    const string Sentence = "the derived check rows are not built in this version of runcontrol: this plan has derived checks to settle "
                            + "({0}), which `run` and `resume` cannot compute or account yet; run it with the Python controller "
                            + "(evalrun.py), or plan without them.";

    /// <summary>The plan's derived checks with a planned arm: the rows the loop after the schedule settles.</summary>
    public static List<PyDict> Planned(PyDict plan) =>
        PyValue.Iter(plan["checks"]).Select(PyValue.Dict)
            .Where(c => Ev.Eq(PyValue.Dict(c["adapter"]).Get("kind"), "derived") && Ev.Eq(c["status"], "planned")
                        && PyValue.Iter(c["arms"]).Any(r => Ev.Eq(Ev.G(r, "status"), "planned"))).ToList();

    /// <summary>Why this plan cannot be run by this version, or null when it has no derived row to settle.</summary>
    public static string? NotBuilt(PyDict plan)
    {
        var planned = Planned(plan);
        return planned.Count == 0 ? null : string.Format(Sentence, string.Join(", ", planned.Select(c => Ev.Str(c["checkId"]))));
    }

    /// <summary>Settles one planned row of a derived check that no earlier invocation completed (evalrun.py's
    /// run_derived): the derivation computed and receipted, or the row accounted.</summary>
    public static void Run(Executor ex, PyDict check, PyDict arm) =>
        throw new Refusal(string.Format(Sentence, Ev.Str(check["checkId"])));
}
