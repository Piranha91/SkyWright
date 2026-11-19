using HousecarlCore.Progress;

namespace RunControl;

/// <summary>The rows of the swept checks (adapter kind ordersweep-probe): one order sweep child for all of them, its
/// jobs split into arm, reverse and diff receipts. Not built in this version: a plan that has such rows to settle is
/// refused before anything is written.</summary>
public static class SweepRows
{
    const string Sentence = "the order sweep rows are not built in this version of runcontrol: this plan has swept checks to settle "
                            + "({0}), which `run` and `resume` cannot measure or account yet; run it with the Python controller "
                            + "(evalrun.py), or plan without the swept checks.";

    /// <summary>The plan's swept checks that are planned, in plan order: the checks one sweep child measures.</summary>
    public static List<PyDict> Planned(PyDict plan) =>
        PyValue.Iter(plan["checks"]).Select(PyValue.Dict)
            .Where(c => Ev.Eq(PyValue.Dict(c["adapter"]).Get("kind"), "ordersweep-probe") && Ev.Eq(c["status"], "planned")).ToList();

    /// <summary>Why this plan cannot be run by this version, or null when it has no swept row to settle.</summary>
    public static string? NotBuilt(PyDict plan)
    {
        var planned = Planned(plan);
        return planned.Count == 0 ? null : string.Format(Sentence, string.Join(", ", planned.Select(c => Ev.Str(c["checkId"]))));
    }

    /// <summary>Settles every row of the swept checks, once per invocation, at the place of the first of them in the
    /// schedule (evalrun.py's sweep block in execute): budget gone with work pending, nothing to settle, or the
    /// sweep child launched and its jobs split into receipts.</summary>
    public static void Settle(Executor ex, List<PyDict> sweepChecks, Run parent) =>
        throw new Refusal(string.Format(Sentence, string.Join(", ", sweepChecks.Select(c => Ev.Str(c["checkId"])))));
}
