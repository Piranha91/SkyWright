"""Known answers for the navmesh grid rebuild (C2507-C2512), on _shared/NavGrid.cs through `navownprobe --navgrid-selftest`.

    2507  the divisor at each side of its steps (16|17, 49|50, ... 499|500) and its cap of 12
    2508  a hand-computed 3 x 3 navmesh: divisor 2, the box over used vertices only, cells of 150, and each triangle in
          exactly the cells it meets (triangle 8's box reaches cell 2, the triangle does not; a box rule fails here)
    2509  the sabotage arm: an off-by-one cell size fails 2508's assertion on a cell
    2510  the cell bytes decode back through houseCARL core's NavmeshGridDecode
    2511  no triangles, and a triangle naming a missing vertex, are each refused in one sentence
    2512  vanilla 05CB2A:Skyrim.esm (2,474 triangles, divisor 12) reproduced byte for byte; needs the game's Data folder in
          HC_NAVGRID_DATA, and fails by name without it

The whole-game comparison (every navmesh of the five masters) is `navownprobe --navgrid-oracle OUT_DIR PLUGIN...`; it is a
measurement, not a test, and its result is in NVGD.md in the task's run folder.

    python tests/test_navgrid_rebuild.py           (HC_NAVGRID_DATA=<game>/Data; HC_NR_CASES=2508,2509 to pick cases)
"""
import os
import subprocess
import sys
import traceback
from pathlib import Path

HARNESS = Path(__file__).resolve().parent.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
PROBES = Path(os.environ.get("HC_NR_HARNESS") or buildlayout.root())
DATA = os.environ.get("HC_NAVGRID_DATA") or ""
ONLY = {s.strip() for s in (os.environ.get("HC_NR_CASES") or "").split(",") if s.strip()}
FAILURES, PASSED = [], []
SELF = None


def selftest(name):
    global SELF
    if SELF is None:
        exe = buildlayout.program_under(PROBES, "navownprobe")
        SELF = subprocess.run([str(exe), "--navgrid-selftest", *([DATA] if DATA else [])], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=600)
    lines = [l for l in SELF.stdout.splitlines() if l.startswith((f"ok {name} ", f"FAIL {name} "))]
    assert lines and lines[0].startswith("ok "), lines or f"no line for {name} (exit {SELF.returncode}): {SELF.stdout[-400:]} {SELF.stderr[-300:]}"


CASES = [
    ("2507 divisor steps and cap", lambda: selftest("2507")),
    ("2508 hand-computed grid", lambda: selftest("2508")),
    ("2509 sabotage: off-by-one cell size fails on a cell", lambda: selftest("2509")),
    ("2510 bytes decode back through the core decode", lambda: selftest("2510")),
    ("2511 refusals in one sentence", lambda: selftest("2511")),
    ("2512 vanilla 05CB2A reproduced byte for byte", lambda: selftest("2512")),
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
