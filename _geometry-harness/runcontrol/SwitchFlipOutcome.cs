using System.Numerics;

namespace RunControl;

/// <summary>The reading rule for the runtime state-switch check (`runtime.state-switch-flip`), its one owner. The live
/// driver (through `runcontrol switchflip-outcome`) and the live import (LiveImport) both call it, so a
/// receipt's stored classification can always be re-derived from its recorded events and is never trusted on its own.
///
/// One launch's events after the flip starts:
///   {"kind": "call", "name", "ok": bool, "t", "err"?}            one scheduled Enable/Disable call
///   {"kind": "readback", "name", "ok": bool, "t", "err"?, "target_reached": bool}
///   {"kind": "skipped", "name", "t", "why"}                       a call dropped because the marker already had its target
///   {"kind": "heartbeat", "ok": bool, "t", "err"?}               a liveness call during the observation window
///   {"kind": "alive", "exe": bool, "t"}                           the game process present or not
///   {"kind": "crashlog", "path", "mtime"}                         a crash log the driver found
///
/// Classify: fail(crash) = the game process gone AND a crash log newer than the flip start; fail(stall) = two consecutive
/// liveness timeouts (heartbeats, or a scheduled call or readback that timed out) while the process is alive; complete =
/// every scheduled call and readback answered, every readback at its target, and every heartbeat answered over the whole
/// observation window; anything else is not-measured, with the reason. Never a pass.
///
/// ArmReading: an arm's replicates of one condition. finding = at least two measured failures; no-finding = at least two
/// measured completions and no failure; anything else is unsettled (run another replicate). Not-measured launches are
/// counted as unjudgeable with their reasons, never as either outcome.</summary>
public static class SwitchFlipOutcome
{
    /// <summary>The shortest observation window a completion may claim (seconds after the last call).</summary>
    public const double WindowS = 60.0;
    /// <summary>Slack on the window for the heartbeat cadence: a beat lands within this of the window's end.</summary>
    public const double WindowSlackS = 2.0;
    /// <summary>Consecutive liveness timeouts that make a stall.</summary>
    public const int StallRun = 2;
    /// <summary>Measured launches an arm reading needs on one side.</summary>
    public const int MinReplicates = 2;

    // a number as Python compares it; anything else is the TypeError Python would raise
    static double Num(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        int i => i,
        BigInteger n => (double)n,
        double d => d,
        _ => throw new PyError($"a number was expected, found {PyText.Repr(v)}"),
    };

    static object? G(object? e, string key) => PyValue.Dict(e).Get(key);
    static bool Kind(object? e, string kind) => Ev.Eq(G(e, "kind"), kind);
    // e["t"]: a missing time is the KeyError Python would raise
    static double T(object? e) => PyValue.Dict(e).ContainsKey("t") ? Num(G(e, "t")) : throw new PyError("an event has no t");

    /// <summary>A failed liveness probe: the call did not answer in time, or the transport could not reach the game.</summary>
    public static bool IsTimeout(object? e)
    {
        string s = PyValue.Truthy(G(e, "err")) ? PyText.Str(G(e, "err")) : "";
        return !PyValue.Truthy(G(e, "ok")) && (s.Contains("504") || s.Contains("VM stalled") || PyText.Lower(s).Contains("timed out")
                                               || s.Contains("_transport") || s.Contains("ConnectionRefused"));
    }

    static PyDict R(string outcome, string? branch, string why) => new() { ["outcome"] = outcome, ["branch"] = branch, ["why"] = why };

    public static PyDict Classify(IReadOnlyList<object?> events, double flipStart, double windowS = WindowS)
    {
        var crash = events.Where(e => Kind(e, "crashlog") && (PyValue.Truthy(G(e, "mtime")) ? Num(G(e, "mtime")) : 0) > flipStart).ToList();
        bool exeGone = events.Any(e => Kind(e, "alive") && !PyValue.Truthy(G(e, "exe")));
        if (exeGone && crash.Count > 0)
            return R("fail", "crash", $"game process gone; crash log {PyText.Str(G(crash[0], "path"))}");
        // sorted(..., key=t): stable
        var stream = events.Where(e => Kind(e, "heartbeat") || Kind(e, "call") || Kind(e, "readback"))
            .Select((e, i) => (e, i, t: T(e))).OrderBy(x => x.t).ThenBy(x => x.i).Select(x => x.e).ToList();
        int run = 0;
        foreach (var e in stream)
        {
            if (IsTimeout(e))
            {
                run++;
                if (run >= StallRun)
                {
                    double te = T(e);
                    var aliveAfter = events.Where(a => Kind(a, "alive") && T(a) >= te - 1).ToList();
                    if (aliveAfter.Count == 0 || aliveAfter.All(a => PyValue.Truthy(G(a, "exe"))))
                        return R("fail", "stall", $"{StallRun} consecutive liveness timeouts ending at t={PyText.Fixed(te, 1)} with the game process alive");
                }
            }
            else run = 0;
        }
        if (exeGone)
            return R("not-measured", null, "game process gone without a crash log newer than the flip (cannot classify as a crash)");
        var calls = events.Where(e => Kind(e, "call")).ToList();
        var backs = events.Where(e => Kind(e, "readback")).ToList();
        var beats = events.Where(e => Kind(e, "heartbeat")).ToList();
        if (calls.Count == 0) return R("not-measured", null, "no scheduled call was sent");
        if (!calls.Concat(backs).All(e => PyValue.Truthy(G(e, "ok"))))
            return R("not-measured", null, "a call or readback failed without consecutive timeouts");
        var missed = backs.Where(e => !PyValue.Truthy(G(e, "target_reached"))).Select(e => PyText.Str(G(e, "name"))).ToList();
        if (missed.Count > 0)
            return R("not-measured", null, "did not reach its target: " + string.Join(", ", missed) + " (never counted as a flip)");
        if (backs.Count < calls.Count) return R("not-measured", null, "a call has no readback");
        if (beats.Count == 0 || T(beats[^1]) - calls.Max(T) < windowS - WindowSlackS)
            return R("not-measured", null, "observation window shorter than required");
        if (!beats.All(e => PyValue.Truthy(G(e, "ok"))))
            return R("not-measured", null, "an isolated heartbeat failed (not consecutive timeouts)");
        return R("complete", null, $"{calls.Count} calls answered, {beats.Count} heartbeats answered");
    }

    /// <summary>Read one arm's replicates of one condition. Each item is a Classify result.</summary>
    public static PyDict ArmReading(IReadOnlyList<object?> classifications)
    {
        var fails = classifications.Where(c => Ev.Eq(G(c, "outcome"), "fail")).ToList();
        var comps = classifications.Where(c => Ev.Eq(G(c, "outcome"), "complete")).ToList();
        var unjudgeable = new PyDict();
        foreach (var c in classifications)
            if (!Ev.Eq(G(c, "outcome"), "fail") && !Ev.Eq(G(c, "outcome"), "complete"))
            {
                string why = PyValue.Truthy(G(c, "why")) ? PyText.Str(G(c, "why")) : "unclassified";
                unjudgeable[why] = (unjudgeable.Get(why) is long n ? n : 0L) + 1L;
            }
        PyDict Reading(string reading, string why) => new()
        {
            ["reading"] = reading, ["failed"] = (long)fails.Count, ["completed"] = (long)comps.Count, ["unjudgeable"] = unjudgeable, ["why"] = why,
        };
        if (fails.Count >= MinReplicates)
        {
            var branches = PyOrder.SortedByCodePoint(fails.Select(c => PyText.Str(G(c, "branch"))).ToHashSet());
            return Reading("finding", $"{fails.Count} measured failures ({string.Join(", ", branches)}), {comps.Count} completions");
        }
        if (fails.Count == 0 && comps.Count >= MinReplicates)
            return Reading("no-finding", $"{comps.Count} measured completions, no failure: no failure under this condition, never 'safe'");
        return Reading("unsettled", $"{fails.Count} failure(s) and {comps.Count} completion(s) measured: run another replicate");
    }

    /// <summary>`runcontrol switchflip-outcome`: one JSON request on standard input, one JSON answer on standard output,
    /// for the live driver, which must not hold its own copy of the rule:
    ///   {"classify": {"events": [...], "flipStart": T, "windowSeconds"?: W}}  -> a Classify result
    ///   {"armReading": [classification, ...]}                                -> an ArmReading result
    ///   {"rules": true}                                                       -> {"windowSeconds": 60.0}
    /// A request of another shape is refused in one sentence.</summary>
    public static object Answer(object? request)
    {
        try
        {
            if (request is PyDict q && q.Count == 1)
            {
                if (q.Get("classify") is PyDict c && c.Get("events") is List<object?> events && c.ContainsKey("flipStart"))
                    return Classify(events, Num(c["flipStart"]), c.Get("windowSeconds") is { } w ? Num(w) : WindowS);
                if (q.Get("armReading") is List<object?> cls) return ArmReading(cls);
                if (q.Get("rules") is true) return new PyDict { ["windowSeconds"] = WindowS };
            }
        }
        catch (PyError ex)
        {
            throw new Refusal($"switchflip-outcome: the request holds a value of a shape the rule cannot read ({ex.Message}); "
                              + "the events file is damaged or was not written by the live switch check.");
        }
        throw new Refusal("switchflip-outcome reads one JSON object on standard input: {\"classify\": {\"events\", \"flipStart\", \"windowSeconds\"?}}, "
                          + "{\"armReading\": [classifications]} or {\"rules\": true}.");
    }
}
