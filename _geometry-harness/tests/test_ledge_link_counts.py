"""Known answers: viewprobe --route-solve says which cross-mesh links it held and why a pair has no path (C1990-C1993).

    python tests/test_ledge_link_counts.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp (viewprobe/CaptureFixture.cs), laid out over a Skyrim SE game's own archives
(tests/capture_fixture_lab.py). Cell CfLedge (viewprobe/LedgeLinkFixture.cs) holds three flat grids joined only by typed
links: L (z 0) to U (z 200) by 4 Ledge Up links, U to L by 4 Ledge Down, L and S (z 0) by 7 Enable/Disable links and 1 of
type 7. The default policy walks Ledge Down only, so 12 links are held. Without game data (HOUSECARL_FIXTURE_GAME or
`fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C1990  CfLedge: the route-solve graph line reports 4 up + 4 down ledge links, 7 enable/disable, 1 unknown type and
           12 held
    C1991  CfCutaway (one grid, no cross-mesh links): the same line reports 0 for each, so the counts are measured
    C1992  CfLedge, L to U (the climb; the drop makes L and U one piece): refused, and the refusal names the 12 held
           links and the 4 Ledge Up among them, so "no path" in one piece is explained
    C1993  CfLedge, L to S (S is a second piece only because its links are held): refused, and the refusal names the 12
           held links and the 7 Enable/Disable among them, so the piece is not read as a separate room

The control (not a case): a route inside L is solved (exit 0, a "leg 1:" line), so a red case is the report's defect and
not a fixture route-solve cannot read.

Exit 0: every case passed; 1: a case failed (or the control did); 3: not run.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = buildlayout.program("viewprobe")
CASES = ("C1990", "C1991", "C1992", "C1993")
TOP = ["--eye", "0,0,1500", "--look", "0,1,0", "--size", "64", "--msaa", "0", "--nav", "off", "--route-z", "0", "--route-solve"]
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def solve(rig, name, cell, *waypoints):
    flags = [*TOP]
    for w in waypoints:
        flags += ["--route", w]
    r = rig.lab.render(rig.tmp / "renders" / name, cell, *flags)
    return r.returncode, r.stdout, r.stderr


def graph_line(out: str) -> str | None:
    return next((ln for ln in out.splitlines() if "route-solve: graph" in ln), None)


def counts(line: str) -> dict[str, int | None]:
    def num(pat):
        m = re.search(pat, line)
        return int(m.group(1)) if m else None
    return {"up": num(r"(\d+) up \+"), "down": num(r"\+ (\d+) down"), "enableDisable": num(r"(\d+) enable/disable"),
            "unknown": num(r"(\d+) unknown type"), "held": num(r"(\d+) held")}


def refusal(err: str) -> str:
    return next((ln for ln in err.splitlines() if "route REFUSED" in ln), "")


def run_all(rig):
    code, out, _ = solve(rig, "control", "CfLedge", "-200,0", "-40,0")
    if code != 0 or not re.search(r"^\s*leg 1: \d+ triangle", out, re.M) or graph_line(out) is None:
        FAILS.append("CONTROL")
        RESULTS.append(("CONTROL", "RED", f"a route inside CfLedge's lower grid was not solved (exit {code})"))

    code, out, err = solve(rig, "C1990", "CfLedge", "-128,0", "128,0")
    line = graph_line(out) or ""
    want = {"up": 4, "down": 4, "enableDisable": 7, "unknown": 1, "held": 12}
    check("C1990", counts(line) == want, "the graph line does not report the ledge, enable/disable, unknown and held counts",
          f"got {counts(line)}; line {line.strip()[:400]!r}")
    said = refusal(err)
    check("C1992", code != 0 and re.search(r"\b12\b[^.;]*held", said) is not None and re.search(r"\b4 Ledge Up\b", said) is not None,
          "the climb's refusal does not name the held links", f"exit {code}; refusal {said.strip()[:400]!r}")

    code, out, err = solve(rig, "C1991", "CfCutaway", "-200,0", "200,0")
    line = graph_line(out) or ""
    check("C1991", counts(line) == {k: 0 for k in want}, "the graph line on a cell with no typed links does not report zeros",
          f"exit {code}; got {counts(line)}; line {line.strip()[:400]!r}")

    code, _, err = solve(rig, "C1993", "CfLedge", "-128,0", "-128,256")
    said = refusal(err)
    check("C1993", code != 0 and re.search(r"\b12\b[^.;]*held", said) is not None and re.search(r"\b7 Enable/Disable\b", said) is not None,
          "the refusal into the held piece does not name the held links", f"exit {code}; refusal {said.strip()[:400]!r}")


class Rig:
    def __init__(self, tmp: Path, vp: Path, game: Path):
        self.tmp = tmp
        self.lab = LAB.Lab(tmp / "lab", vp, game)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vp", default=str(PROD_VP))
    ap.add_argument("--keep", default=None)
    a = ap.parse_args(argv)
    game, why = LAB.game_folder()
    if game is None:
        print("NOT RUN:", why)
        return 3
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="ledgelinks-"))
    tmp.mkdir(parents=True, exist_ok=True)
    try:
        run_all(Rig(tmp, Path(a.vp), game))
    except Exception as exc:  # a crash is reported as a crash, never as a case's own red
        FAILS.append("CRASH")
        RESULTS.append(("CRASH", "CRASH", f"{type(exc).__name__}: {exc} | {traceback.format_exc().splitlines()[-3:]}"))
    for case in CASES:
        if case not in FAILS:
            RESULTS.append((case, "GREEN", ""))
    for case, verdict, text in RESULTS:
        print(f"{case:7} {verdict:5} {text}")
    print(f"work folder: {tmp}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
