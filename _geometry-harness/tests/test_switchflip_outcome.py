"""Known answers for the runtime state-switch reading rule (switchflip_outcome.py).

Dependency-free: `python _geometry-harness/tests/test_switchflip_outcome.py`.
Each test names the wrong answer it rejects, so a sabotage sweep can point at the assertion that went red.
Synthetic event streams only; recorded lab receipts are replayed outside the tracked tree.
"""

import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import switchflip_outcome as O  # noqa: E402

FAILURES, PASSED = [], []
T0 = 1000.0


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def calls(n=3, t=T0 + 1, reached=True):
    ev = []
    for i in range(n):
        ev.append({"kind": "call", "name": f"m{i}", "ok": True, "t": t})
        ev.append({"kind": "readback", "name": f"m{i}", "ok": True, "t": t + 0.3, "target_reached": reached})
        t += 1
    return ev


def beats(start, seconds, ok=True):
    return [{"kind": "heartbeat", "ok": ok, "t": start + i} for i in range(int(seconds) + 1)]


def timeout(t, kind="heartbeat"):
    return {"kind": kind, "ok": False, "t": t, "err": "504 VM stalled"}


def last_call(ev):
    return max(e["t"] for e in ev if e["kind"] == "call")


def full_complete():
    ev = calls()
    return ev + beats(last_call(ev) + 1, 60) + [{"kind": "alive", "exe": True, "t": T0 + 70}]


# K1 -- rejects: complete / not-measured for a real crash
def test_k1_crash_with_new_log_is_fail_crash():
    ev = calls() + [{"kind": "alive", "exe": False, "t": T0 + 6},
                    {"kind": "crashlog", "path": "crash-x.log", "mtime": T0 + 5}]
    r = O.classify(ev, T0)
    assert (r["outcome"], r["branch"]) == ("fail", "crash"), r


# K2 -- rejects: fail(crash) from a crash log older than the flip
def test_k2_old_crash_log_is_not_a_crash():
    ev = calls() + [{"kind": "alive", "exe": False, "t": T0 + 6},
                    {"kind": "crashlog", "path": "old.log", "mtime": T0 - 100}]
    r = O.classify(ev, T0)
    assert r["outcome"] == "not-measured", r
    assert "without a crash log" in r["why"], r


# K3 -- rejects: not-measured for two consecutive timeouts with the process alive
def test_k3_two_consecutive_timeouts_alive_is_stall():
    ev = calls()
    t = last_call(ev) + 1
    ev += beats(t, 5) + [timeout(t + 6), timeout(t + 14), {"kind": "alive", "exe": True, "t": t + 15}]
    r = O.classify(ev, T0)
    assert (r["outcome"], r["branch"]) == ("fail", "stall"), r


def test_k3b_timed_out_call_then_heartbeat_timeout_is_stall():
    ev = calls(2)
    t = last_call(ev) + 1
    ev += [timeout(t, "call"), timeout(t + 8), {"kind": "alive", "exe": True, "t": t + 9}]
    r = O.classify(ev, T0)
    assert (r["outcome"], r["branch"]) == ("fail", "stall"), r


# K4 -- rejects: fail(stall) for one isolated timeout
def test_k4_isolated_timeout_is_not_measured():
    ev = calls()
    t = last_call(ev) + 1
    hb = beats(t, 60)
    hb[10] = timeout(hb[10]["t"])
    r = O.classify(ev + hb + [{"kind": "alive", "exe": True, "t": t + 61}], T0)
    assert r["outcome"] == "not-measured", r
    assert "isolated" in r["why"], r


# K5 -- rejects: not-measured for a full clean window
def test_k5_full_window_is_complete():
    r = O.classify(full_complete(), T0)
    assert r["outcome"] == "complete", r


# K6 -- rejects: complete for a short window
def test_k6_short_window_is_not_measured():
    ev = calls()
    ev += beats(last_call(ev) + 1, 40)
    r = O.classify(ev, T0)
    assert r["outcome"] == "not-measured", r
    assert "window" in r["why"], r


# K7 -- rejects: complete when a readback never reached its target (an inert call is not a flip)
def test_k7_target_not_reached_is_not_measured():
    ev = calls(reached=False)
    ev += beats(last_call(ev) + 1, 60)
    r = O.classify(ev, T0)
    assert r["outcome"] == "not-measured", r
    assert "did not reach its target" in r["why"], r


def test_k7b_no_call_sent_is_not_measured():
    ev = [{"kind": "skipped", "name": "m0", "t": T0, "why": "already at target"}] + beats(T0 + 1, 60)
    r = O.classify(ev, T0)
    assert r["outcome"] == "not-measured" and "no scheduled call" in r["why"], r


# K8 -- arm readings; rejects: a finding from one failure, a not-measured counted as a completion
FAIL = {"outcome": "fail", "branch": "crash", "why": "x"}
COMP = {"outcome": "complete", "branch": None, "why": "y"}
NM = {"outcome": "not-measured", "branch": None, "why": "observation window shorter than required"}


def test_k8_two_failures_is_finding():
    assert O.arm_reading([FAIL, FAIL])["reading"] == "finding"


def test_k8_two_completions_is_no_finding():
    assert O.arm_reading([COMP, COMP])["reading"] == "no-finding"


def test_k8_one_and_one_is_unsettled():
    assert O.arm_reading([FAIL, COMP])["reading"] == "unsettled"


def test_k8_one_failure_is_unsettled():
    assert O.arm_reading([FAIL, NM, NM])["reading"] == "unsettled"


def test_k8_not_measured_never_counts_as_completion():
    r = O.arm_reading([COMP, NM])
    assert r["reading"] == "unsettled", r
    assert r["unjudgeable"] == {NM["why"]: 1}, r


def test_k8_failure_blocks_no_finding():
    assert O.arm_reading([COMP, COMP, FAIL])["reading"] == "unsettled"


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
