namespace RunControl;

/// <summary>A program's identity changed during a run: the invocation stops, with this sentence as its refusal.</summary>
public sealed class ToolChanged(string sentence) : Exception(sentence);

/// <summary>The programs a plan can launch, compared by content with the plan's record (evalrun.py): before `run`
/// and `resume` write anything, when each child launches, and before its result is accepted.</summary>
public static class ToolPreflight
{
    public const string IdentityAction = "Restore the recorded build, or make a new plan in a new directory.";

    /// <summary>Null when `now` is the identity the plan recorded for the program, else the cause and what differs, in one clause.</summary>
    public static string? Drift(string pid, object? recordedRaw, PyDict now)
    {
        string label = PlanIdentity.ProgramLabel(pid);
        object? state = (recordedRaw as PyDict)?.Get("state");
        if (recordedRaw is not PyDict recorded || state is not ("present" or "absent" or "unverifiable")
            || (Ev.Eq(state, "present") && !PyValue.Truthy(recorded.Get("digest"))))
            return $"tool-identity-missing: plan.json records no identity for {label}, which this plan can launch or whose work it retains";
        object? nowState = now.Get("state");
        if (Ev.Eq(state, "unverifiable") || Ev.Eq(nowState, "unverifiable"))
            return $"tool-unverifiable: the --probe-cmd for {pid[(pid.IndexOf(':') + 1)..]} ({PyText.Repr(recorded.Get("command"))}) names no "
                   + "file whose content can be checked, so the program that would run cannot be identified";
        if (Ev.Eq(state, "absent") && Ev.Eq(nowState, "absent")) return null;   // recorded absent and still absent: its checks are blocked(tool-missing)
        if (Ev.Eq(state, "absent"))
            return $"tool-changed: {label} had no build when this plan was made and has one now at {Ev.Str(now.Get("locator"))}";
        if (Ev.Eq(nowState, "absent"))
            return $"tool-missing: {label} was recorded at {ToolIdentity.Short(recorded.Get("digest"))}, and {Ev.Str(recorded.Get("locator"))} is gone";
        if (Ev.Eq(recorded.Get("digest"), now.Get("digest"))) return null;
        var diffs = ToolIdentity.Differences(recorded, now);
        return $"tool-changed: {label} is not the build this plan recorded ({(diffs.Count > 0 ? string.Join("; ", diffs) : "its content differs")}; "
               + $"digest {ToolIdentity.Short(recorded.Get("digest"))} -> {ToolIdentity.Short(now.Get("digest"))})";
    }

    /// <summary>The sentence for a program file that exists and could not be read.</summary>
    public static string UnreadableSentence(string pid, Unreadable ex) =>
        $"tool-unreadable: {PyPath.Name(ex.FilePath)} of {PlanIdentity.ProgramLabel(pid)} could not be read ({ex.Error.Message}) at {PyPath.Str(ex.FilePath)}, so the "
        + "code that would run cannot be identified";

    /// <summary>(refusal, measured identities): every program that can launch under the plan, plus every program a
    /// retained receipt of this plan names. Reads files; writes nothing.</summary>
    public static (string? refusal, PyDict measured) Check(string run, PyDict plan, IEnumerable<string>? only = null)
    {
        var progs = PyValue.Dict(PyValue.Dict(plan["toolIdentity"])["programs"]);
        HashSet<string> need;
        if (only != null) need = new(only, StringComparer.Ordinal);
        else
        {
            var armKinds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in PyValue.Iter(plan["arms"]).Select(PyValue.Dict)) armKinds[Ev.Str(a["armId"])] = Ev.Str(a["kind"]);
            need = PlanIdentity.Launchable(PyValue.Iter(plan["checks"]).Select(PyValue.Dict), armKinds);
            foreach (var (_, r) in Receipts.ReadReceipts(run))
            {
                if (!Ev.Eq(Ev.G(r, "planId"), plan["planId"])) continue;
                foreach (var (_, row) in Ev.OrEmpty(Ev.OrEmpty(Ev.G(r, "consumed")).Get("programs")))
                    if (row is PyDict d && d.Get("program") is string program && progs.ContainsKey(program)) need.Add(program);
            }
        }
        var opts = PyValue.Dict(plan["options"]);
        string harness = PyPath.Str(Ev.Str(opts["harness"]));
        var measured = new PyDict();
        var problems = new List<string>();
        var unread = new List<string>();
        foreach (string pid in PyOrder.SortedByCodePoint(need))
        {
            if (progs.Get(pid) is not PyDict rec)
            {
                problems.Add(Drift(pid, null, new PyDict())!);
                continue;
            }
            PyDict now;
            try { now = PlanIdentity.Measure(opts, harness, pid); }
            catch (Unreadable ex)
            {
                unread.Add(UnreadableSentence(pid, ex));
                continue;
            }
            measured[pid] = now;
            if (Drift(pid, rec, now) is { } why) problems.Add(why);
        }
        if (problems.Count == 0 && unread.Count == 0) return (null, measured);
        string action = problems.Count > 0 ? IdentityAction : "Make the file readable and run again, or make a new plan in a new directory.";
        return (string.Join("; ", problems.Concat(unread)) + ". " + action, measured);
    }
}
