"""switchflip_outcome — the reading rule for the runtime state-switch check (`runtime.state-switch-flip`).

Pure functions, no I/O: the live driver (`switchflip.py`) and the importer (`evallive.py`) both call them, so a
receipt's stored classification can always be re-derived from its recorded events, and is never trusted on its own.

One launch's events after the flip starts:
  {"kind": "call", "name", "ok": bool, "t", "err"?}            one scheduled Enable/Disable call
  {"kind": "readback", "name", "ok": bool, "t", "err"?, "target_reached": bool}
  {"kind": "skipped", "name", "t", "why"}                       a call dropped because the marker already had its target
  {"kind": "heartbeat", "ok": bool, "t", "err"?}               a liveness call during the observation window
  {"kind": "alive", "exe": bool, "t"}                           the game process present or not
  {"kind": "crashlog", "path", "mtime"}                         a crash log the driver found

classify(): fail(crash) = the game process gone AND a crash log newer than the flip start; fail(stall) = two
consecutive liveness timeouts (heartbeats, or a scheduled call or readback that timed out) while the process is
alive; complete = every scheduled call and readback answered, every readback at its target, and every heartbeat
answered over the whole observation window; anything else is not-measured, with the reason. Never a pass.

arm_reading(): an arm's replicates of one condition. finding = at least two measured failures; no-finding = at
least two measured completions and no failure; anything else is unsettled (run another replicate). Not-measured
launches are counted as unjudgeable with their reasons, never as either outcome.
"""
from __future__ import annotations

#: The shortest observation window a completion may claim (seconds after the last call).
WINDOW_S = 60.0
#: Slack on the window for the heartbeat cadence: a beat lands within this of the window's end.
WINDOW_SLACK_S = 2.0
#: Consecutive liveness timeouts that make a stall.
STALL_RUN = 2
#: Measured launches an arm reading needs on one side.
MIN_REPLICATES = 2

OUTCOMES = ("complete", "fail", "not-measured")
READINGS = ("finding", "no-finding", "unsettled")


def is_timeout(e: dict) -> bool:
    """A failed liveness probe: the call did not answer in time, or the transport could not reach the game."""
    s = str(e.get("err") or "")
    return (not e.get("ok")) and ("504" in s or "VM stalled" in s or "timed out" in s.lower()
                                  or "_transport" in s or "ConnectionRefused" in s)


def _r(outcome, branch, why):
    return {"outcome": outcome, "branch": branch, "why": why}


def classify(events: list[dict], flip_start: float, window_s: float = WINDOW_S) -> dict:
    crash = [e for e in events if e.get("kind") == "crashlog" and (e.get("mtime") or 0) > flip_start]
    exe_gone = any(e.get("kind") == "alive" and not e.get("exe") for e in events)
    if exe_gone and crash:
        return _r("fail", "crash", f"game process gone; crash log {crash[0].get('path')}")
    stream = sorted((e for e in events if e.get("kind") in ("heartbeat", "call", "readback")), key=lambda e: e["t"])
    run = 0
    for e in stream:
        if is_timeout(e):
            run += 1
            if run >= STALL_RUN:
                alive_after = [a for a in events if a.get("kind") == "alive" and a["t"] >= e["t"] - 1]
                if not alive_after or all(a.get("exe") for a in alive_after):
                    return _r("fail", "stall", f"{STALL_RUN} consecutive liveness timeouts ending at "
                                               f"t={e['t']:.1f} with the game process alive")
        else:
            run = 0
    if exe_gone:
        return _r("not-measured", None, "game process gone without a crash log newer than the flip "
                                        "(cannot classify as a crash)")
    calls = [e for e in events if e.get("kind") == "call"]
    backs = [e for e in events if e.get("kind") == "readback"]
    beats = [e for e in events if e.get("kind") == "heartbeat"]
    if not calls:
        return _r("not-measured", None, "no scheduled call was sent")
    if not all(e.get("ok") for e in calls + backs):
        return _r("not-measured", None, "a call or readback failed without consecutive timeouts")
    missed = [e.get("name") for e in backs if not e.get("target_reached")]
    if missed:
        return _r("not-measured", None, "did not reach its target: " + ", ".join(map(str, missed))
                  + " (never counted as a flip)")
    if len(backs) < len(calls):
        return _r("not-measured", None, "a call has no readback")
    if not beats or beats[-1]["t"] - max(e["t"] for e in calls) < window_s - WINDOW_SLACK_S:
        return _r("not-measured", None, "observation window shorter than required")
    if not all(e.get("ok") for e in beats):
        return _r("not-measured", None, "an isolated heartbeat failed (not consecutive timeouts)")
    return _r("complete", None, f"{len(calls)} calls answered, {len(beats)} heartbeats answered")


def arm_reading(classifications: list[dict]) -> dict:
    """Read one arm's replicates of one condition. Each item is a classify() result."""
    fails = [c for c in classifications if c.get("outcome") == "fail"]
    comps = [c for c in classifications if c.get("outcome") == "complete"]
    unjudgeable: dict[str, int] = {}
    for c in classifications:
        if c.get("outcome") not in ("fail", "complete"):
            why = str(c.get("why") or "unclassified")
            unjudgeable[why] = unjudgeable.get(why, 0) + 1
    counts = {"failed": len(fails), "completed": len(comps), "unjudgeable": unjudgeable}
    if len(fails) >= MIN_REPLICATES:
        branches = sorted({str(c.get("branch")) for c in fails})
        return {"reading": "finding", **counts,
                "why": f"{len(fails)} measured failures ({', '.join(branches)}), {len(comps)} completions"}
    if not fails and len(comps) >= MIN_REPLICATES:
        return {"reading": "no-finding", **counts,
                "why": f"{len(comps)} measured completions, no failure: no failure under this condition, never 'safe'"}
    return {"reading": "unsettled", **counts,
            "why": f"{len(fails)} failure(s) and {len(comps)} completion(s) measured: run another replicate"}
