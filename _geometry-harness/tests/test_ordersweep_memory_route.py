"""Known answers for ordersweep's memory-route record (CR-15; task 2026-10-14-contactprobe-default-pass-memory-g1).

Dependency-free: `python _geometry-harness/tests/test_ordersweep_memory_route.py`.
contactprobe writes <emit>.memory.json beside its emit (route, memory case, unattended default, warning). An evaluation reads its
sweep's summary.json and jobs.jsonl, so ordersweep carries the sidecar into both. Each test names the wrong answer it rejects.
"""
import json
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(os.path.dirname(os.path.abspath(__file__)))
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))

import ordersweep as O  # noqa: E402

FAILURES, PASSED = [], []


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


TMP = Path(tempfile.mkdtemp(prefix="sweep-memory-route-"))


def lean_contended():
    """Rejects: a lean run under contention recorded without its route, its unattended default or its warning."""
    emit = TMP / "a.json"
    emit.write_text("{}", encoding="utf-8")
    Path(str(emit) + ".memory.json").write_text(json.dumps({
        "route": "lean", "case": "contended", "unattendedDefault": True, "outcome": "completed",
        "warning": "memory: the fast route needs 10.4 GB and 5.0 GB is free; closing python (pid 4242, 26.0 GB) frees it"}), encoding="utf-8")
    r = O.memory_route(emit)
    assert r["route"] == "lean" and r["case"] == "contended" and r["unattendedDefault"] is True, r
    assert "python (pid 4242" in r["warning"] and r["outcome"] == "completed", r


def absent_is_not_fast():
    """Rejects: a run with no sidecar (an older probe, or a probe that writes none) recorded as fast."""
    emit = TMP / "b.json"
    emit.write_text("{}", encoding="utf-8")
    assert O.memory_route(emit) == {"route": "not reported"}, O.memory_route(emit)
    assert O.memory_route(None) == {"route": "not reported"}


def unreadable_is_named():
    """Rejects: an unreadable sidecar silently dropped, or read as a route."""
    emit = TMP / "c.json"
    emit.write_text("{}", encoding="utf-8")
    Path(str(emit) + ".memory.json").write_text("{not json", encoding="utf-8")
    r = O.memory_route(emit)
    assert r["route"] == "not reported" and "sidecarUnreadable" in r, r


def wired_into_records():
    """Rejects: the reader present but the sweep's job records, candidate rows or baseline record not carrying it."""
    src = (HARNESS / "ordersweep.py").read_text(encoding="utf-8")
    for want in (r'"memoryRoute": memory_route\(emit\) if kind == "probe"', r"memoryRoute=memory_route\(got\)",
                 r'row\["reverseMemoryRoute"\] = memory_route\(rgot\)', r"baseline_routes\[p\] = memory_route\(got\)",
                 r'"baselineMemoryRoute": baseline_routes'):
        assert len(re.findall(want, src)) == 1, f"ordersweep.py does not carry the route here: {want}"


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    check("CR-15 a lean run under contention: route, case, unattended default and warning recorded", lean_contended)
    check("CR-15 no sidecar: 'not reported', never fast", absent_is_not_fast)
    check("CR-15 an unreadable sidecar is named, not read", unreadable_is_named)
    check("CR-15 job records, candidate rows and the baseline record carry it", wired_into_records)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
