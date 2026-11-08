"""Known answers on a real render: effect-shader shapes are not solid to `--inside-at` and `--clear-at` (C1710-C1712).

    python tests/test_effect_shapes.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp's cell CfCloud (viewprobe/CaptureFixture.cs): the vanilla cloud mesh (one
effect-shader cloud shape and an effect-shader editor marker) at scale 0.1 on the floor, its box about z 1..512, and the
vanilla burning campfire (lit logs and stones; effect-shader flames, glow and smoke) at (600,0,0). Laid out as an MO2
instance over a Skyrim SE game's own archives (tests/capture_fixture_lab.py) and judged by the REAL shotplan parsers.
A player can stand inside a cloud and cannot stand on one; a campfire's stones stay solid. Without game data
(HOUSECARL_FIXTURE_GAME or `fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C1710  an eye inside the cloud: the cloud is listed "effect-only, not solid", is no enclosing row, the eye is clear
    C1711  `--clear-at` straight down from above the cloud: the first surface below is the floor, not the cloud
    C1712  the campfire (mixed): an eye inside its box still has it as an enclosing row, and the ray down from above it
           stops on the campfire's own lit body, not on the floor

Exit 0: every case passed; 1: a case failed; 3: not run.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


S = load("shotplan_fx", HARNESS / "viewprobe" / "tools" / "shotplan.py")
_ap = argparse.ArgumentParser()
S.add_common(_ap, need_subjects=False)
ARGS = _ap.parse_args(["picker"])

IDS = LAB.ids("CfCloud")
CLOUD, FIRE = IDS["walls"]["W"][0], IDS["walls"]["W"][1]
FLOOR = set(IDS["floor"])
IN_CLOUD = [(0, 0, 255), (-150, 0, 120), (150, 0, 400)]
ABOVE_CLOUD = (0, 0, 640)
IN_FIRE = (600, 0, 60)
ABOVE_FIRE = (600, 0, 400)


def probe(rig, flags):
    r = rig.lab.render(rig.tmp / "renders" / "CfCloud", "CfCloud", "--eye", "0,-1200,300", "--look", "0,0,100",
                       "--size", "64", "--msaa", "0", "--nav", "off", "--report-mode", "--clear-reach", "1024", *flags)
    pr = S.Probe([], echo=False)
    pr.stdout = r.stdout
    return pr, r.stdout


def run_all(rig):
    flags = []
    for p in IN_CLOUD + [IN_FIRE]:
        flags += ["--inside-at", ",".join(str(c) for c in p)]
    for p in [ABOVE_CLOUD, ABOVE_FIRE]:
        flags += ["--clear-at", ",".join(str(c) for c in p)]
    pr, out = probe(rig, flags)
    (rig.tmp / "console.txt").write_text(out, encoding="utf-8")
    inside, clear = pr.inside(), pr.clear()

    # C1710
    for p in IN_CLOUD:
        rows = inside.get(p)
        if not check("C1710", rows is not None, "viewprobe gave no inside-at answer", str(p)):
            continue
        listed = [r["formkey"] for r in rows]
        check("C1710", CLOUD not in listed, "the cloud was tested as an enclosing solid", f"{p}: rows {listed}")
        kind, fails, _ = S.inside_verdict(rows, ARGS)
        check("C1710", kind == "clear", "an eye inside the cloud was not accepted", f"{p}: {kind} {fails[:2]}")
        block = out.split(f"inside-at {p[0]},{p[1]},{p[2]}:", 1)[-1].split("\n   ", 1)[0]
        check("C1710", f"effect-only, not solid: {CLOUD}" in block, "the cloud is not named effect-only, not solid",
              f"{p}: {block[:300]}")

    # C1711
    e = clear.get(ABOVE_CLOUD)
    if check("C1711", e is not None, "viewprobe gave no clear-at answer", str(ABOVE_CLOUD)):
        down = e["axes"].get("down") or {}
        check("C1711", down.get("formkey") in FLOOR and abs((down.get("dist") or 0) - ABOVE_CLOUD[2]) <= 25,
              "the first surface below the eye is not the floor", f"down {down}")
        sup, smsg = S.support_verdict(e, tuple(float(c) for c in ABOVE_CLOUD), 0.0, ARGS)
        check("C1711", sup == "supported", "the floor under the cloud does not support an eye on it", f"{sup}: {smsg}")

    # C1712
    rows = inside.get(IN_FIRE) or []
    check("C1712", FIRE in [r["formkey"] for r in rows], "the campfire's box no longer nominates it (its body was lost)",
          str([r["formkey"] for r in rows]))
    e = clear.get(ABOVE_FIRE)
    if check("C1712", e is not None, "viewprobe gave no clear-at answer", str(ABOVE_FIRE)):
        down = e["axes"].get("down") or {}
        check("C1712", down.get("formkey") == FIRE and down.get("dist") is not None and down["dist"] < ABOVE_FIRE[2] - 2,
              "the ray down from above the campfire does not stop on the campfire", f"down {down}")


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
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="fx-"))
    tmp.mkdir(parents=True, exist_ok=True)
    try:
        run_all(Rig(tmp, Path(a.vp), game))
    except Exception as exc:  # a crash is reported as a crash, never as a case's own red
        FAILS.append("CRASH")
        RESULTS.append(("CRASH", "CRASH", f"{type(exc).__name__}: {exc} | {traceback.format_exc().splitlines()[-3:]}"))
    for case in ("C1710", "C1711", "C1712"):
        if case not in FAILS:
            RESULTS.append((case, "GREEN", ""))
    for case, verdict, text in RESULTS:
        print(f"{case:5} {verdict:5} {text}")
    print(f"work folder: {tmp}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
