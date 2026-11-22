"""Known answers for navmesh editing (C2513-C2519), on _shared/NavEdit.cs through `navedit --selftest`.

    2513  an interior vertex moved: planned, the box kept, the grid NavGrid's for the moved mesh, ops for exactly that
          vertex, the box and the grid
    2514  a move that folds a triangle is refused
    2515  a vertex on an edge linking into another navmesh: sideways refused, height-only planned
    2516  a point repeated under two indices moves as one
    2517  a move that overlaps another triangle on the same level is refused; the same triangle 200 u up is not an overlap
    2518  a vertex on a cell border: sideways refused, height-only planned
    2519  a vertex the navmesh lacks, and an empty move list, are refused in one sentence

The real-data check (plan and write on a lab profile, read back, byte-compared with the in-game-tested patch) is in the
2026-11-21 run folder; it needs a game and an MO2 instance, so it is not a test here.

    python tests/test_navedit.py           (HC_NR_CASES=2514,2515 to pick cases)
"""
import os
import subprocess
import sys
import traceback
from pathlib import Path

HARNESS = Path(__file__).resolve().parent.parent
PROBES = Path(os.environ.get("HC_NR_HARNESS") or HARNESS)
ONLY = {s.strip() for s in (os.environ.get("HC_NR_CASES") or "").split(",") if s.strip()}
FAILURES, PASSED = [], []
SELF = None


def selftest(name):
    global SELF
    if SELF is None:
        exe = PROBES / "navedit" / "bin" / "Release" / "net9.0" / "navedit.exe"
        SELF = subprocess.run([str(exe), "--selftest"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=600)
    lines = [l for l in SELF.stdout.splitlines() if l.startswith((f"ok {name} ", f"FAIL {name} "))]
    assert lines and lines[0].startswith("ok "), lines or f"no line for {name} (exit {SELF.returncode}): {SELF.stdout[-400:]} {SELF.stderr[-300:]}"


CASES = [
    ("2513 interior move planned with its grid and ops", lambda: selftest("2513")),
    ("2514 a fold is refused", lambda: selftest("2514")),
    ("2515 a linked edge holds sideways, moves in height", lambda: selftest("2515")),
    ("2516 coincident indices move together", lambda: selftest("2516")),
    ("2517 same-level overlap refused, a bridge is not", lambda: selftest("2517")),
    ("2518 a cell-border vertex holds sideways", lambda: selftest("2518")),
    ("2519 bad input refused in one sentence", lambda: selftest("2519")),
]

if __name__ == "__main__":
    for name, fn in CASES:
        if ONLY and name.split()[0] not in ONLY:
            continue
        try:
            fn()
        except Exception:
            FAILURES.append(name)
            print(f"FAIL  {name}")
            print("      " + traceback.format_exc().strip().splitlines()[-1])
        else:
            PASSED.append(name)
            print(f"ok    {name}")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
