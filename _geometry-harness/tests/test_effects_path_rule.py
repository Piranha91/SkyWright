"""Known answers on a real render: a shape is solid by its own data, not by its folder (C1720-C1726).

    python tests/test_effects_path_rule.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp (viewprobe/CaptureFixture.cs), laid out over a Skyrim SE game's own archives
(tests/capture_fixture_lab.py) and judged by the REAL shotplan parsers. Cell CfFxLit holds vanilla meshes from an
effects\\ folder; CfCloud holds the vanilla burning campfire. Without game data (HOUSECARL_FIXTURE_GAME or
`fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C1720  the frozen Falmer statue (lit shapes): an eye inside it is enclosed by it and refused
    C1721  the ice sheet (one lit shape, z 100): the ray down from above stops on the ice, not on the floor
    C1722  the blue fire, whose only lit shape is a refraction plane: listed "no solid shape, not solid", no row, eye clear
    C1723  the crater FX (effect shapes and hidden emitter geometry only): listed "no solid shape, not solid", eye clear
    C1724  the thin waterfall's shader-less plane stays solid: the ray up from under it stops on it
    C1725  the ice sheet under an FX editor id is solid like C1721's: no name rule drops it (the ray down stops on the ice
           at z 100; corrected 2026-11-22 when the FX editor-id rule went, task 2026-11-11-viewprobe-fx-editor-id-rule)
    C1726  the campfire's refraction heat plane is not solid: the ray down at (650,0) passes it (base: stops about
           z 56) and stops on the campfire's ground ring near z 0; its logs still hold the ray at (600,0) (C1712's point)
    C1727  cell CfFxMissing: a ref whose effects\\ mesh does not exist is a physical ref the queries could not read: the
           inside-at header counts it (1 by record OBND alone, 1 unreadable) and the clear-at header counts it (1 unreadable)
    C1728  the same cell's FX-named ref whose vanilla mesh (Effects\\FXEmptyObject.nif) reads but holds no triangles is no
           solid shape, not unreadable: C1727's counts stay 1 with it standing there, and the queries list it "no solid shape"

Exit 0: every case passed; 1: a case failed; 3: not run.
"""
from __future__ import annotations

import argparse
import importlib.util
import itertools
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = buildlayout.program("viewprobe")
CASES = ("C1720", "C1721", "C1722", "C1723", "C1724", "C1725", "C1726", "C1727", "C1728")
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


S = load("shotplan_fxpath", HARNESS / "viewprobe" / "tools" / "shotplan.py")
_ap = argparse.ArgumentParser()
S.add_common(_ap, need_subjects=False)
ARGS = _ap.parse_args(["picker"])

FX = LAB.ids("CfFxLit")
FALMER, ICE, BLUEFIRE, CRATER, WATERFALL, ICE_FX = FX["walls"]["W"][:6]
FX_FLOOR = set(FX["floor"])
IN_FALMER = (-1050, 0, 60)
ABOVE_ICE = (-350, 0, 600)
IN_FIRE = (350, 0, 60)
IN_CRATER = (1050, 0, 60)
UNDER_PLANE = (0, 750, 1500)          # the waterfall's shader-less CurrentPlane lies about z 2103 over this point
ABOVE_ICE_FX = (0, -700, 600)
CF = LAB.ids("CfCloud")
CAMPFIRE = CF["walls"]["W"][1]
CF_FLOOR = set(CF["floor"])
PAST_HEAT = (650, 0, 400)             # through the campfire's heat plane (base: stops about z 56)
ON_LOGS = (600, 0, 400)               # C1712's point: the logs
AT_MISSING = (0, 0, 50)               # CfFxMissing: inside the missing mesh's record box


def probe(rig, cell, inside, clear):
    flags = []
    for p in inside:
        flags += ["--inside-at", ",".join(str(c) for c in p)]
    for p in clear:
        flags += ["--clear-at", ",".join(str(c) for c in p)]
    r = rig.lab.render(rig.tmp / "renders" / cell, cell, "--eye", "0,-1700,500", "--look", "0,0,80",
                       "--size", "64", "--msaa", "0", "--nav", "off", "--report-mode", "--clear-reach", "1024", *flags)
    (rig.tmp / f"console-{cell}.txt").write_text(r.stdout, encoding="utf-8")
    pr = S.Probe([], echo=False)
    pr.stdout = r.stdout
    return pr.inside(), pr.clear(), r.stdout


def block(out: str, p) -> str:
    """The --inside-at header line for p and the indented lines under it."""
    tail = out.split(f"inside-at {p[0]},{p[1]},{p[2]}:", 1)[-1].splitlines()
    return "\n".join([tail[0]] + list(itertools.takewhile(lambda ln: ln.startswith("      "), tail[1:])))


def down(clear, p):
    e = clear.get(p)
    return (e or {}).get("axes", {}).get("down") or {}, e


def run_all(rig):
    inside, clear, out = probe(rig, "CfFxLit", [IN_FALMER, IN_FIRE, IN_CRATER],
                               [ABOVE_ICE, UNDER_PLANE, ABOVE_ICE_FX, IN_FIRE, IN_CRATER])

    # C1720
    rows = inside.get(IN_FALMER)
    if check("C1720", rows is not None, "viewprobe gave no inside-at answer", str(IN_FALMER)):
        keys = [r["formkey"] for r in rows]
        check("C1720", FALMER in keys, "the frozen Falmer statue is not an enclosing row", f"rows {keys}")
        kind, fails, _ = S.inside_verdict(rows, ARGS)
        check("C1720", kind != "clear", "an eye inside the frozen Falmer statue was accepted", f"{kind} {fails[:2]}")

    # C1721
    d, e = down(clear, ABOVE_ICE)
    if check("C1721", e is not None, "viewprobe gave no clear-at answer", str(ABOVE_ICE)):
        check("C1721", d.get("formkey") == ICE and abs((d.get("dist") or 0) - (ABOVE_ICE[2] - 100)) <= 10,
              "the first surface below is not the ice sheet at z 100", f"down {d}")
        sup, smsg = S.support_verdict(e, tuple(float(c) for c in ABOVE_ICE), 100.0, ARGS)
        check("C1721", sup == "supported", "the ice sheet does not support an eye stood on it", f"{sup}: {smsg}")

    # C1722, C1723
    for case, p, key in (("C1722", IN_FIRE, BLUEFIRE), ("C1723", IN_CRATER, CRATER)):
        rows = inside.get(p)
        if not check(case, rows is not None, "viewprobe gave no inside-at answer", str(p)):
            continue
        check(case, key not in [r["formkey"] for r in rows], "it was tested as an enclosing solid",
              str([r["formkey"] for r in rows]))
        kind, fails, _ = S.inside_verdict(rows, ARGS)
        check(case, kind == "clear", "an eye inside it was not accepted", f"{kind} {fails[:2]}")
        check(case, f"no solid shape, not solid: {key}" in block(out, p), "it is not listed as having no solid shape",
              block(out, p)[:400])
        d, e = down(clear, p)
        check(case, d.get("formkey") in FX_FLOOR, "the ray down from inside it does not reach the floor", f"down {d}")

    # C1724
    e = clear.get(UNDER_PLANE)
    if check("C1724", e is not None, "viewprobe gave no clear-at answer", str(UNDER_PLANE)):
        up = e["axes"].get("up") or {}
        check("C1724", up.get("formkey") == WATERFALL and abs((up.get("dist") or 0) - 603) <= 15,
              "the ray up does not stop on the waterfall's shader-less plane near z 2103", f"up {up}")

    # C1725
    d, e = down(clear, ABOVE_ICE_FX)
    if check("C1725", e is not None, "viewprobe gave no clear-at answer", str(ABOVE_ICE_FX)):
        check("C1725", d.get("formkey") == ICE_FX and abs((d.get("dist") or 0) - (ABOVE_ICE_FX[2] - 100)) <= 10,
              "the first surface below is not the FX-named ice sheet at z 100 (a name rule dropped it)", f"down {d}")

    # C1726
    _, clear2, _ = probe(rig, "CfCloud", [], [PAST_HEAT, ON_LOGS])
    d, e = down(clear2, PAST_HEAT)
    if check("C1726", e is not None, "viewprobe gave no clear-at answer", str(PAST_HEAT)):
        z = PAST_HEAT[2] - (d.get("dist") or 0)
        check("C1726", d.get("formkey") in {CAMPFIRE} | CF_FLOOR and z < 20,
              "the ray down stops on the campfire's heat plane (or above z 20)", f"down {d} (hit z {z:.1f})")
    d, e = down(clear2, ON_LOGS)
    if check("C1726", e is not None, "viewprobe gave no clear-at answer", str(ON_LOGS)):
        check("C1726", d.get("formkey") == CAMPFIRE and (d.get("dist") or 999) < ON_LOGS[2] - 2,
              "the ray down at the logs does not stop on the campfire", f"down {d}")

    # C1727
    ins3, clear3, out3 = probe(rig, "CfFxMissing", [AT_MISSING], [AT_MISSING])
    rows = ins3.get(AT_MISSING)
    if check("C1727", rows is not None, "viewprobe gave no inside-at answer", str(AT_MISSING)):
        head = block(out3, AT_MISSING).splitlines()[0]
        check("C1727", "1 by record OBND alone (mesh unreadable), 1 physical ref(s) whose mesh could not be read at all" in head,
              "the inside-at header does not count the unreadable effects\\ mesh", head[:300])
    e = clear3.get(AT_MISSING)
    if check("C1727", e is not None, "viewprobe gave no clear-at answer", str(AT_MISSING)):
        check("C1727", e["unread"] == 1 and e["obnd_only"] == 1,
              "the clear-at header does not count the unreadable effects\\ mesh", f"unread {e['unread']} obnd_only {e['obnd_only']}")
    # C1728
    check("C1728", "no solid shape" in out3, "the empty FX mesh (no triangles) is not listed as no solid shape", out3[:400])


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
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="fxpath-"))
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
        print(f"{case:5} {verdict:5} {text}")
    print(f"work folder: {tmp}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
