"""Known answers: viewprobe --route-solve's path is taut through the navmesh, with no corner twice and every interior
corner inset (C2300, C2301, C2306).

    python tests/test_route_geometry.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp's cell CfLedgeGap (viewprobe/LedgeLinkFixture.GapMeshes), laid out over a Skyrim SE
game's own archives (tests/capture_fixture_lab.py): L (z 0, x -256..0, y -128..128) with the squares x -128..-64,
y -64..64 deleted to make a notch; U (z 200, x 48..304) dropping to L by Ledge Down links across a 48 u plan gap. Every
grid is 64 u squares, so a corner left on the mesh boundary sits on the 64 u lattice (U's lattice starts at x 48).
Without game data (HOUSECARL_FIXTURE_GAME or `fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C2300  U (176,0) to L (-224,0), the drop then round the notch: no two consecutive corners are equal, and every
           interior corner is inset (at least 3 u from its grid's nearest vertex)
    C2301  L (-224,32) to (-32,-32), on one storey round the notch: the same
    C2306  L (-224,0) to (-32,0), round the notch: the path bends at the notch's own corners (each interior corner
           within 8 u of (-128,-64) and (-64,-64), or of their mirror at y 64), and "u to walk" is the length of that
           path through its inset corners (within 2 u), so at least the taut 250.9 u and under the 272 u of the slack
           path. The two 6 u insets add about 10 u to the taut length.

The NavGraph-level answers C2302-C2305 are xUnit tests (src/housecarl-mcp-tests/NavGraphFunnelTests.cs).

Exit 0: every case passed; 1: a case failed; 3: not run.
"""
from __future__ import annotations

import argparse
import math
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
CASES = ("C2300", "C2301", "C2306")
TOP = ["--eye", "0,0,1500", "--look", "0,1,0", "--size", "64", "--msaa", "0", "--nav", "off", "--route-solve"]
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def solve(rig, name, z, *waypoints):
    flags = [*TOP, "--route-z", str(z)]
    for w in waypoints:
        flags += ["--route", w]
    old = os.environ.get("VP_SOLVE_DEBUG")
    os.environ["VP_SOLVE_DEBUG"] = "1"
    try:
        r = rig.lab.render(rig.tmp / "renders" / name, "CfLedgeGap", *flags)
    finally:
        if old is None:
            os.environ.pop("VP_SOLVE_DEBUG", None)
        else:
            os.environ["VP_SOLVE_DEBUG"] = old
    return r.returncode, r.stdout, r.stderr


def leg_line(out: str) -> str | None:
    # the dash prints as an em dash or as "-" depending on the console's code page
    return next((ln.strip().replace("—", "-") for ln in out.splitlines() if re.match(r"^\s*leg 1: \d+ triangle", ln)), None)


def walked(line: str | None) -> float | None:
    m = re.search(r" (\d+) u to walk", line or "")
    return float(m.group(1)) if m else None


def corners(out: str) -> list[tuple[float, float]]:
    ln = next((x for x in out.splitlines() if "[dbg] corners:" in x), "")
    return [(float(a), float(b)) for a, b in re.findall(r"(-?[\d.]+),(-?[\d.]+)", ln.split("corners:", 1)[-1])]


def vertex_gap(c: tuple[float, float]) -> float:
    """Plan distance from a corner to the nearest vertex of its grid's 64 u lattice (U's starts at x 48)."""
    ox = 48.0 if c[0] >= 24.0 else 0.0
    return min(math.hypot(c[0] - (ox + 64 * (round((c[0] - ox) / 64) + dx)), c[1] - 64 * (round(c[1] / 64) + dy))
               for dx in (-1, 0, 1) for dy in (-1, 0, 1))


def dup_or_uninset(cs):
    dups = [i for i in range(len(cs) - 1) if math.hypot(cs[i][0] - cs[i + 1][0], cs[i][1] - cs[i + 1][1]) < 1e-3]
    flat = [(c, round(vertex_gap(c), 2)) for c in cs[1:-1] if vertex_gap(c) < 3]
    return dups, flat


def run_all(rig):
    for case, z, a, b in (("C2300", 200, "176,0", "-224,0"), ("C2301", 0, "-224,32", "-32,-32")):
        code, out, err = solve(rig, case, z, a, b)
        cs = corners(out)
        dups, flat = dup_or_uninset(cs)
        check(case, code == 0 and len(cs) >= 2 and not dups and not flat,
              "a corner is emitted twice in a row, or an interior corner is left on the mesh boundary",
              f"exit {code}; corners {cs}; repeated at {dups}; uninset {flat}; leg {leg_line(out)!r}")

    code, out, err = solve(rig, "C2306", 0, "-224,0", "-32,0")
    cs = corners(out)
    line = leg_line(out)
    w = walked(line)
    inner = cs[1:-1]

    def near(c, p):
        return math.hypot(c[0] - p[0], c[1] - p[1]) <= 8.0

    side_ok = any(len(inner) == 2 and near(inner[0], (-128, s)) and near(inner[1], (-64, s)) for s in (-64, 64))
    poly = sum(math.hypot(cs[i + 1][0] - cs[i][0], cs[i + 1][1] - cs[i][1]) for i in range(len(cs) - 1))
    check("C2306", code == 0 and side_ok and w is not None and abs(w - poly) <= 2 and 250 <= w < 272,
          "the path round the notch does not bend at the notch's own corners with the taut length",
          f"exit {code}; corners {cs}; corner path {poly:.1f} u; leg {line!r}")


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
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="routegeom-"))
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
