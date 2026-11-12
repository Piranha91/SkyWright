"""Known answers for the capture follow-ups after item 28 (C1830-C1831).

    python tests/test_capture_follow_ups.py [--only C1830,C1835]

No game and no renderer. The picker cases (C1830, C1831, C1833) run the REAL capture_points.py over
`viewprobe/tools/fake_viewprobe.py`'s toy box scenes, with the scene's `contacts` key on so the stand-in prints each
participant's drawn box as viewprobe's `--contacts-of` does. The reel and report cases (C1832, C1834, C1835) call the
real reel_plan and evalreport on hand-built inputs (test_reel_vignettes' synthetic evaluation, test_capture_fallbacks'
refused capture result). Coordinates and counts are made up; nothing here is evidence about a real cell.

    C1830  item 1: a tall focus whose see-through partner contains it, with a slab 1,800 u up over the ring's spots and
           the navmesh at z 0: the ring stands on the navmesh storey, not on the slab far above a player's eye
    C1831  item 2: a tall focus whose see-through partner's drawn box is flat (no height) and 1,000 u above the floor:
           the floor at z 0 under the focus is still found, and the ring stands there

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
SKILL = HARNESS.parent / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    """`wrong` names the wrong answer the assertion rejects; it is what a red case prints."""
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return cond


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------- picker scenes (fake renderer) --

def picker_env():
    import test_capture_points as TCP   # noqa: E402  (the picker driver, its toy-scene helpers and the request bundle)
    return TCP


def ring_floors(fx, tag: str) -> list[float] | None:
    wl = fx.root / f"work-{tag}" / "witness-worklist.json"
    if not wl.is_file():
        return None
    return sorted({float(s["floorZ"]) for s in json.loads(wl.read_text(encoding="utf-8"))["cells"][0]["shots"]})


def c1830_storey_not_from_the_box_top(fx, TCP):
    """A tower 2,000 u tall (the focus: its partner is a see-through cloud whose box contains it, as WRW 708f's cloud
    contains the tower) with a slab at z 1,800-1,820 over every ring spot and the navmesh at z 0. Wrong answer: the ring
    on the slab, 1,700 u above the eye of a player on the navmesh, because the ground was cast down from the box top."""
    tower = TCP.box(fx.cy, [-100, -100, 0], [100, 100, 2000])
    cloud = TCP.box(fx.mg, [-6000, -6000, -2000], [6000, 6000, 8000], alpha=True, physical=False)
    slab = {"id": "000401:Test.esp", "edid": "Walkway", "min": [-500, -500, 1800], "max": [500, 500, 1820]}
    sc = TCP.scene(tower, cloud, slab, shell=False, contacts=True)
    proc, _doc = TCP.pick(fx, sc, "c1830")
    floors = ring_floors(fx, "c1830")
    check("C1830", floors is not None and all(-8 <= f <= 8 for f in floors),
          "the ring stands on a slab far above the navmesh storey (the ground cast from the top of the focus box)",
          f"ring floors {floors}; {TCP.tail(proc)}")


def c1831_flat_partner_box(fx, TCP):
    """A tower 2,000 u tall (the focus) and a see-through partner whose drawn box is flat at z 1,000 and 10,000 u wide
    (an effect card). Wrong answer: the storey searched only around z 1,000, so the floor at z 0 is missed and the
    picker refuses for want of a floor, or the ring stands anywhere but z 0."""
    tower = TCP.box(fx.cy, [-100, -100, 0], [100, 100, 2000])
    card = TCP.box(fx.mg, [-5000, -5000, 1000], [5000, 5000, 1000], alpha=True, physical=False)
    sc = TCP.scene(tower, card, shell=False, contacts=True)
    proc, _doc = TCP.pick(fx, sc, "c1831")
    floors = ring_floors(fx, "c1831")
    check("C1831", floors is not None and all(-8 <= f <= 8 for f in floors),
          "a flat partner box narrowed the storey to a plane and the floor under the focus was missed",
          f"ring floors {floors}; {TCP.tail(proc)}")




# ---------------------------------------------------------------- reel and report (hand-built inputs) --









PICKER_CASES = {"C1830": c1830_storey_not_from_the_box_top, "C1831": c1831_flat_partner_box}
CODE_CASES = {
             }


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    ap.add_argument("--keep", default=None, help="keep the work folder here")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="cfu-"))
    root.mkdir(parents=True, exist_ok=True)
    fx = TCP = None
    for cid, fn in {**PICKER_CASES, **CODE_CASES}.items():
        if only and cid not in only:
            continue
        try:
            if cid in PICKER_CASES:
                if fx is None:
                    TCP = picker_env()
                    fx = TCP.Fixture(root / "picker")
                fn(fx, TCP)
            else:
                fn(root)
        except Exception as e:  # noqa: BLE001  (a crash is reported as such, never as the case's own answer)
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:6} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
