"""Known answers for navmesh editing (C2513-C2550), on _shared/NavEdit.cs and _shared/NavCutFootprint.cs through `navedit --selftest`.

    2513  an interior vertex moved: planned, the box kept, the grid NavGrid's for the moved mesh, ops for exactly that
          vertex, the box and the grid
    2514  a move that folds a triangle is refused
    2515  a vertex on an edge linking into another navmesh: sideways refused, height-only planned
    2516  a point repeated under two indices moves as one
    2517  a move that overlaps another triangle on the same level is refused; the same triangle 200 u up is not an overlap
    2518  a vertex on a cell border: sideways refused, height-only planned
    2519  a vertex the navmesh lacks, and an empty move list, are refused in one sentence
    2520  removing a triangle moves the last one into its slot (swap-remove): fields follow, ops write it whole first
    2521  a triangle added back joins both neighbours, both ways, by a whole-triangle Add
    2522  an added triangle wound against its neighbour, or flat, is refused
    2523  an added triangle on an edge two triangles already share is refused
    2524  a triangle with a linked edge, and one before a door, are not removed
    2525  another navmesh's links follow the moved triangle, and one into the removed triangle is cut; the door follows
    2526  a cut that leaves two navmeshes with no link between them is refused
    2527  an added triangle overlapping one on the same level is refused; 200 u up it is a bridge
    2528  bad triangle input is refused in one sentence
    2529  a navmesh's own table naming itself follows the moved triangle, or is cut
    2530  NavCut: a hole round the centre leaves the area outside it, nothing over it, no half-joined edge, NavGrid's grid
    2531  NavCut: a hole inside one triangle splits its uncut neighbours at the cut points
    2532  NavCut: a non-convex, two-corner or flat outline, an empty band, and a cut that misses are refused
    2533  NavCut: the height band picks the level; above the navmesh it cuts nothing
    2534  NavCut: a cut reaching a linked edge is refused, saying so
    2535  cut of an obstacle: a box standing on the floor cuts exactly its area
    2536  the cut grown by a margin, across the triangles it reaches
    2537  an obstacle wholly above an actor's top, or below step height, is refused, naming the band
    2538  each level is measured on itself: a box on a bridge cuts the bridge, one on the floor under it the floor
    2539  two separate rocks: two holes, and the ground between them stays
    2540  an L-shaped obstacle (one mesh) is cut as the L; the notch's ground stays
    2541  two rocks joined by a thin bar of collision: the rocks and the bar go, the ground beside the bar stays
    2542  two overlapping bodies: their union goes, the overlap included
    2543  an overhang whose underside is in the band is cut; one wholly above an actor's top is refused, and not cut beside a post
    2544  navmesh lying on a rock stays; the floor round the rock is cut
    2545  sloping navmesh: each triangle measured on its own plane; a box low enough to step onto is not cut
    2546  NavCut: two outlines meeting an edge from its two sides a hair apart: their points are welded
    2547  NavCut: an outline reaching 0.3 u past an edge cuts the triangle beyond it
    2548  NavCut: a strip too narrow to stand on goes with the hole
    2549  NavCut: an outline edge parallel to a triangle's edge, a hair from it, is taken last; the far hairline stays
    2550  a notched roof is cut as two convex parts, not merged across the dip; the ground over the dip stays

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
    ("2520 swap-remove renumbers and writes the moved triangle whole", lambda: selftest("2520")),
    ("2521 an added triangle joins its neighbours", lambda: selftest("2521")),
    ("2522 winding and area refused", lambda: selftest("2522")),
    ("2523 a third triangle on an edge refused", lambda: selftest("2523")),
    ("2524 linked and door triangles stay", lambda: selftest("2524")),
    ("2525 inbound links follow or are cut", lambda: selftest("2525")),
    ("2526 the last link between navmeshes refused", lambda: selftest("2526")),
    ("2527 added overlap refused, a bridge is not", lambda: selftest("2527")),
    ("2528 bad triangle input refused", lambda: selftest("2528")),
    ("2529 self-links follow or are cut", lambda: selftest("2529")),
    ("2530 NavCut centre hole is sound", lambda: selftest("2530")),
    ("2531 NavCut splits uncut neighbours", lambda: selftest("2531")),
    ("2532 NavCut refusals", lambda: selftest("2532")),
    ("2533 NavCut height band", lambda: selftest("2533")),
    ("2534 NavCut linked edge refused", lambda: selftest("2534")),
    ("2535 obstacle box cut exactly", lambda: selftest("2535")),
    ("2536 obstacle margin", lambda: selftest("2536")),
    ("2537 obstacle out of the actor band refused", lambda: selftest("2537")),
    ("2538 obstacle measured on its own level", lambda: selftest("2538")),
    ("2539 obstacle of separate pieces", lambda: selftest("2539")),
    ("2540 L-shaped obstacle cut as the L", lambda: selftest("2540")),
    ("2541 rocks joined by a thin bar", lambda: selftest("2541")),
    ("2542 overlapping bodies cut as their union", lambda: selftest("2542")),
    ("2543 overhang in the band", lambda: selftest("2543")),
    ("2544 navmesh on a rock stays", lambda: selftest("2544")),
    ("2545 sloping navmesh, own plane", lambda: selftest("2545")),
    ("2546 NavCut welds near points", lambda: selftest("2546")),
    ("2547 NavCut shallow overlap is cut", lambda: selftest("2547")),
    ("2548 NavCut strip too narrow goes", lambda: selftest("2548")),
    ("2549 NavCut parallel edge taken last", lambda: selftest("2549")),
    ("2550 notched roof not merged across the dip", lambda: selftest("2550")),
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
