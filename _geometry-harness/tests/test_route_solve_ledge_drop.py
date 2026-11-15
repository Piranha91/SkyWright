"""Known answers: viewprobe --route-solve accepts a solved leg that drops down a walked ledge, and still refuses one that
really leaves the mesh (C2250-C2254).

    python tests/test_route_solve_ledge_drop.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp (viewprobe/CaptureFixture.cs), laid out over a Skyrim SE game's own archives
(tests/capture_fixture_lab.py). In game an actor drops ledges of about 212 u and 222 u (runs/2026-11-11-ledge-island-arm).
CfLedge (viewprobe/LedgeLinkFixture.cs): L (z 0) and U (z 200) meet in plan at x 0, U drops to L by 4 Ledge Down links.
CfLedgeGap (LedgeLinkFixture.GapMeshes): the same drop with the two sides 48 u apart in plan, as real ledges stand; a
notch deleted from L that a path must bend round; and P, joined to L by Portal links across a 48 u gap that no actor
crosses. Without game data (HOUSECARL_FIXTURE_GAME or `fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C2250  CfLedge, U (128,0) to L (-128,0), the 200 u drop: solved, not refused, and the leg's z span reads 0..200
    C2251  CfLedgeGap, U (176,-96) to L (-224,-96), the drop across the 48 u gap: solved, z 0..200, and the leg line
           says how many samples are in the air over the walked ledge link and that the longest run is 48 u
    C2252  CfLedgeGap, L (-224,-96) to P (-224,304), a Portal across a 48 u gap: still refused as leaving walkable
           ground, first off between L's edge (y 128) and P's (y 176)
    C2253  Legs on one storey are unchanged: inside CfLedge's L, inside its U, and round CfLedgeGap's notch, each leg
           line equals the one measured on viewprobe before this change (fork cad3fb1d; the notch line re-pinned
           2026-11-15 when its slack was fixed, C2306)
    C2254  CfLedgeGap, U (176,-32) to L (-224,32), the drop then the bend round the notch: every corner on the lower floor
           is inset off the mesh boundary (no corner within 3 u of a mesh vertex), as on one storey

The controls (not cases): a route inside L is solved; the notch route on one storey has its corners inset (so C2254's
red is the storey, not the fixture).

Exit 0: every case passed; 1: a case failed (or a control did); 3: not run.
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
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
CASES = ("C2250", "C2251", "C2252", "C2253", "C2254")
TOP = ["--eye", "0,0,1500", "--look", "0,1,0", "--size", "64", "--msaa", "0", "--nav", "off", "--route-solve"]
# C2253's leg lines as viewprobe printed them at fork cad3fb1d, before this change. The notch line was re-pinned on
# 2026-11-15 (C2306, tests/test_route_geometry.py): the path round the notch was slack, bending at an interior vertex
# (272 u); it now bends at the notch's corners and says it was re-routed.
PINNED = {
    "L": "leg 1: 7 triangle(s) crossed, 2 corner(s) after the funnel - 160 u to walk vs 160 u straight line  (x1.00), z 0..0",
    "U": "leg 1: 7 triangle(s) crossed, 3 corner(s) after the funnel - 160 u to walk vs 160 u straight line  (x1.00), z 200..200",
    "notch": "leg 1: 11 triangle(s) crossed, 4 corner(s) after the funnel - 261 u to walk vs 192 u straight line  (x1.36), z 0..0, re-routed round an interior vertex, 20 u shorter",
}
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def solve(rig, name, cell, z, *waypoints, debug=False):
    flags = [*TOP, "--route-z", str(z)]
    for w in waypoints:
        flags += ["--route", w]
    old = os.environ.get("VP_SOLVE_DEBUG")
    if debug:
        os.environ["VP_SOLVE_DEBUG"] = "1"
    try:
        r = rig.lab.render(rig.tmp / "renders" / name, cell, *flags)
    finally:
        if debug:
            if old is None:
                os.environ.pop("VP_SOLVE_DEBUG", None)
            else:
                os.environ["VP_SOLVE_DEBUG"] = old
    return r.returncode, r.stdout, r.stderr


def leg_line(out: str) -> str | None:
    # the dash prints as an em dash or as "-" depending on the console's code page; the pins use "-"
    return next((ln.strip().replace("—", "-") for ln in out.splitlines() if re.match(r"^\s*leg 1: \d+ triangle", ln)), None)


def z_span(line: str | None) -> tuple[int, int] | None:
    m = re.search(r", z (-?\d+)\.\.(-?\d+)", line or "")
    return (int(m.group(1)), int(m.group(2))) if m else None


def refusal(err: str) -> str:
    return " | ".join(ln.strip() for ln in err.splitlines() if "REFUSED" in ln or "off walkable ground" in ln)


def corners(out: str) -> list[tuple[float, float]]:
    ln = next((x for x in out.splitlines() if "[dbg] corners:" in x), "")
    return [(float(a), float(b)) for a, b in re.findall(r"(-?[\d.]+),(-?[\d.]+)", ln.split("corners:", 1)[-1])]


def vertex_gap(c: tuple[float, float]) -> float:
    """Plan distance from a corner to the nearest vertex of the 64 u lattice every fixture grid is built on."""
    return min(math.hypot(c[0] - 64 * round(c[0] / 64 + dx), c[1] - 64 * round(c[1] / 64 + dy))
               for dx in (-1, 0, 1) for dy in (-1, 0, 1))


def lower_interior(cs: list[tuple[float, float]]) -> list[tuple[float, float]]:
    return [c for c in cs[1:-1] if c[0] < 0]


def run_all(rig):
    code, out, _ = solve(rig, "control", "CfLedge", 0, "-200,0", "-40,0")
    if code != 0 or leg_line(out) is None:
        FAILS.append("CONTROL")
        RESULTS.append(("CONTROL", "RED", f"a route inside CfLedge's lower grid was not solved (exit {code})"))

    code, out, err = solve(rig, "C2250", "CfLedge", 200, "128,0", "-128,0")
    line = leg_line(out)
    check("C2250", code == 0 and line is not None and "route REFUSED" not in err and z_span(line) == (0, 200),
          "the 200 u Ledge Down leg is not solved with z span 0..200",
          f"exit {code}; leg {line!r}; refusal {refusal(err)[:400]!r}")

    code, out, err = solve(rig, "C2251", "CfLedgeGap", 200, "176,-96", "-224,-96")
    line = leg_line(out)
    m = re.search(r"(\d+) sample\(s\) in the air over a walked ledge link, longest (\d+) u", line or "")
    check("C2251", code == 0 and line is not None and "route REFUSED" not in err and z_span(line) == (0, 200)
          and m is not None and int(m.group(1)) >= 1 and 47 <= int(m.group(2)) <= 49,
          "the drop across the 48 u ledge gap is not solved with z span 0..200 and its air run named as 48 u",
          f"exit {code}; leg {line!r}; refusal {refusal(err)[:400]!r}")

    code, out, err = solve(rig, "C2252", "CfLedgeGap", 0, "-224,-96", "-224,304")
    said = refusal(err)
    m = re.search(r"off walkable ground, first at -?\d+ u along it, at (-?\d+),(-?\d+)", said)
    check("C2252", code != 0 and "leave walkable navmesh BETWEEN" in said and m is not None and 128 <= int(m.group(2)) <= 176,
          "the leg over the Portal gap is not refused as leaving walkable ground between y 128 and 176",
          f"exit {code}; refusal {said[:500]!r}")

    got = {}
    for key, cell, z, wps in (("L", "CfLedge", 0, ("-200,0", "-40,0")), ("U", "CfLedge", 200, ("200,0", "40,0")),
                              ("notch", "CfLedgeGap", 0, ("-224,0", "-32,0"))):
        code, out, err = solve(rig, f"C2253-{key}", cell, z, *wps)
        got[key] = (code, leg_line(out), refusal(err))
    print("C2253 measured:", {k: v[1] for k, v in got.items()})
    bad = {k: v for k, v in got.items() if v[0] != 0 or v[1] is None or PINNED[k] is None or v[1] != PINNED[k]}
    check("C2253", not bad, "a leg on one storey is not the leg viewprobe printed before this change",
          "; ".join(f"{k}: exit {v[0]} leg {v[1]!r} pinned {PINNED[k]!r} {v[2][:200]!r}" for k, v in bad.items()))

    code, out, _ = solve(rig, "control-notch-inset", "CfLedgeGap", 0, "-224,0", "-32,0", debug=True)
    low = lower_interior(corners(out))
    if not low or min(vertex_gap(c) for c in low) < 3:
        FAILS.append("CONTROL")
        RESULTS.append(("CONTROL", "RED", f"the notch route on one storey has no inset lower corner: corners {corners(out)}"))

    code, out, err = solve(rig, "C2254", "CfLedgeGap", 200, "176,-32", "-224,32", debug=True)
    cs = corners(out)
    low = lower_interior(cs)
    check("C2254", bool(low) and min(vertex_gap(c) for c in low) >= 3,
          "a corner on the lower floor after the drop is left on the mesh boundary (not inset)",
          f"corners {cs}; lower interior {[(c, round(vertex_gap(c), 2)) for c in low]}")


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
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="ledgedrop-"))
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
