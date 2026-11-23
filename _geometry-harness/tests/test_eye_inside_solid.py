"""Known answers for the planner's inside test on a real render: an eye inside a solid of any thickness is refused.

    python tests/test_eye_inside_solid.py [--vp VIEWPROBE.exe] [--only IS1,IS4] [--keep DIR]

The REAL viewprobe `--inside-at` on CfFixture.esp (viewprobe/CaptureFixture.cs, cells CfThick, CfThickWalls and CfTower), laid out
as an MO2 instance over a Skyrim SE game's own archives (tests/capture_fixture_lab.py), judged by the REAL shotplan
`inside_verdict` at the planner's defaults. IS4 runs the real capture_acquire on a pair walled in by walls about 300 u thick.
Without game data (HOUSECARL_FIXTURE_GAME or `fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

Why: from inside a solid its back faces are culled, so a camera there sees out through it and the picture shows what
no player can see. The old rule counted faces within 96 u, so a solid thicker than that passed an eye nearer one face.

Exit 0: every case passed; 1: a case failed; 3: not run.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
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


S = load("shotplan_is", HARNESS / "viewprobe" / "tools" / "shotplan.py")
_ap = argparse.ArgumentParser()
S.add_common(_ap, need_subjects=False)
ARGS = _ap.parse_args(["picker"])

T = LAB.ids("CfThick")["walls"]["W"]          # CfThick's lone solids are Walls[0..3]: T1, T2, T3, the thin wall
T1, T2, T3, THIN = T[0], T[1], T[2], T[3]
# Eyes inside a solid. The crate's drawn mesh is not quite its record box: measured on the base renderer's own rays
# (red/base-is1.txt), T1 (scale 3) has faces at x -677 and -523, z 0 and 123; T3 (scale 8) at x 411 and 788, top z 315.
INSIDE = [
    ((-534, 0, 11), T1, "T1 (153 u): 10 u inside a face and 11 u above the bottom -- base run 3's two near faces"),
    ((-534, 0, 112), T1, "T1 (153 u): 10 u inside a face and 11 u under the top"),
    ((0, 468, 82), T2, "T2 (204 u): the centre, every face 82-102 u away"),
    ((600, -265, 164), T3, "T3 (377 u): the centre, no face within 96 u"),
    ((774, -265, 164), T3, "T3 (377 u): 14 u inside one face, the only face near"),
    ((600, -265, 305), T3, "T3: 10 u under the top face, the only face near -- base run 2's eye at a top face"),
]
# Eyes that must pass: beside the thin wall (x -38.25..38.25, y -756..-543, z 0..61.5) and in the open.
OUTSIDE = [
    ((44, -650, 30), "6 u in front of the thin wall's face"),
    ((68, -650, 30), "30 u in front of the thin wall's face"),
    ((300, -300, 120), "the open floor, nothing near"),
    ((600, -265, 326), "11 u above T3's top face, still inside its mesh bounds (nominated, open above)"),
]


def inside_answers(rig, cell: str, points) -> tuple[dict, str]:
    flags = ["--eye", "0,-1200,300", "--look", "0,0,40", "--size", "64", "--msaa", "0", "--nav", "off", "--report-mode"]
    for p in points:
        flags += ["--inside-at", ",".join(str(c) for c in p)]
    r = rig.render(rig.tmp / "renders" / cell, cell, *flags)
    pr = S.Probe([], echo=False)
    pr.stdout = r.stdout
    return pr.inside(), r.stdout


def is1_is2_is3(rig):
    pts = [p for p, _, _ in INSIDE] + [p for p, _ in OUTSIDE]
    got, out = inside_answers(rig, "CfThick", pts)
    if not check("IS1", bool(got), "viewprobe gave no inside-at answers", out[-400:]):
        return
    for p, solid, what in INSIDE:
        e = got.get(p)
        kind, fails, _ = S.inside_verdict(e, ARGS)
        rows = [h for h in (e or []) if h["formkey"] == solid]
        check("IS1", kind == "inside" and bool(rows) and any("CfCrate" in f for f in fails),
              f"an eye inside a solid was not refused as inside it: {what}",
              f"{p}: {kind}; rays {[h.get('rays') for h in rows]}")
        facing = rows[0].get("facing") if rows else None
        backs = [k for k, v in (facing or {}).items() if v == "back"]
        check("IS3", len(backs) >= 2,
              f"the facing line does not show the eye behind at least two of the solid's faces: {what}",
              f"{p}: facing {facing}")
    for p, what in OUTSIDE:
        e = got.get(p)
        kind, fails, _ = S.inside_verdict(e, ARGS)
        check("IS2", kind == "clear", f"an eye outside every solid was not accepted: {what}", f"{p}: {kind} {fails[:2]}")
        for h in (e or []):
            backs = [k for k, v in (h.get("facing") or {}).items() if v == "back"]
            check("IS3", len(backs) < 2,
                  f"an eye outside the solid reads as behind its faces: {what}", f"{p}: {h.get('facing')}")


def wall_boxes(rig) -> dict:
    """{wall formkey: box} for CfThickWalls, worked out from the fixture table rather than asked of viewprobe (scale 6:
    x +-153, y -624..226, z +-123 about the piece), each shrunk 15 u on every side because the drawn crate is a little
    smaller than its record box. The S and N pieces take a quarter turn whose sense this does not assume: their box is
    the part both senses share, so an eye is only called inside where it is inside either way."""
    i = LAB.ids("CfThickWalls")
    m, z0, z1 = 15.0, 115 - 123 + 15.0, 115 + 123 - 15.0
    out = {}
    for side, cx in (("W", -203.0), ("E", 233.0)):
        for k, cy in enumerate((100.0, 200.0)):
            out[i["walls"][side][k]] = ((cx - 153 + m, cy - 624 + m, z0), (cx + 153 - m, cy + 226 - m, z1))
    for side, cy in (("S", -272.0), ("N", 246.0)):
        for k, cx in enumerate((-130.0, 160.0)):
            out[i["walls"][side][k]] = ((cx - 226 + m, cy - 153 + m, z0), (cx + 226 - m, cy + 153 - m, z1))
    return out


def is4_thick_walls(rig):
    """The pair walled in by walls about 300 u thick: no witness station the picker keeps, and no camera that comes back,
    may stand inside a drawn wall (base run 3 came back camera-ready from one)."""
    CF = load("cf_rig", HERE / "test_capture_fallbacks.py")
    r = CF.Rig(rig.tmp / "is4", rig.vp, rig.game)
    doc = r.acquire("CfThickWalls")
    boxes = wall_boxes(rig)

    def walls_around(eye, skip=()):
        x, y, z = eye
        return [k for k, (lo, hi) in boxes.items() if k not in skip
                and lo[0] < x < hi[0] and lo[1] < y < hi[1] and lo[2] < z < hi[2]]

    pts = Path(doc.get("_dir", "")) / "receipts" / "points.json"
    if check("IS4", pts.is_file(), "the picker wrote no points.json to judge", str(pts)):
        stations = (json.loads(pts.read_text(encoding="utf-8")).get("witness") or {}).get("stations") or []
        kept = [(s["id"], s["eye"], walls_around(s["eye"])) for s in stations if s.get("valid") and s.get("eye")]
        bad = [k for k in kept if k[2]]
        check("IS4", not bad, "the picker kept a witness station whose eye stands inside a wall",
              f"{len(bad)} of {len(kept)} kept: {bad[:4]}")
    cams = []
    if isinstance(doc.get("camera"), dict):
        cams.append(doc["camera"])
    for side in ("left", "right"):
        if isinstance((doc.get("split") or {}).get(side), dict):
            cams.append(doc["split"][side])
    hidden = {h.get("formKey") for h in ((doc.get("cutaway") or {}).get("hidden") or [])}
    if doc.get("status") != "camera-ready":
        RESULTS.append(("IS4", "NOTE", f"refused: {(doc.get('refusal') or {}).get('code')} -- no camera to check"))
        return
    for c in cams:
        inside = walls_around(c.get("eye"), hidden)
        check("IS4", not inside, "the walled pair came back camera-ready from an eye inside a drawn wall",
              f"eye {c.get('eye')} view {c.get('viewKind')} inside {inside}")


def is5_room_inside_a_shell(rig):
    """CfTower: the vanilla Nordic tower ruin's outer shell and its interior top-floor piece, both at the origin. On the
    top floor the eye is inside the shell's box and every lateral face of the shell is turned away from it, but the
    interior piece's walls stand nearer and face it: that is the view a player has up there, so it must be accepted.
    Wrong answer: judging each ref alone, which refuses it as inside the shell (the 2026-11-08 count found three such
    White River Watch captures, one of them on this floor). An eye inside the tower's masonry is still refused."""
    T = LAB.ids("CfTower")["walls"]["W"]
    shell, top = T[0], T[1]
    room = [((0, 0, 1400), "the top floor's middle"), ((-200, 0, 1400), "the top floor, west of its middle (no face within 96 u)")]
    inside = ((-428, -191, 1400), "in the tower's masonry, behind the top floor's west wall")
    got, out = inside_answers(rig, "CfTower", [p for p, _ in room] + [inside[0]])
    for p, what in room:
        e = got.get(p) or []
        rows = {h["formkey"]: h for h in e}
        sf, tf = (rows.get(shell) or {}).get("facing") or {}, (rows.get(top) or {}).get("facing") or {}
        check("IS5", all(sf.get(k) == "back" for k in ("+x", "-x", "+y", "-y"))
              and all(tf.get(k) == "front" for k in ("+x", "-x", "+y", "-y")),
              f"the case is not the shape it tests (shell back, interior front on the four sides): {what}",
              f"{p}: shell {sf} interior {tf}")
        kind, fails, _ = S.inside_verdict(e, ARGS)
        check("IS5", kind == "clear", f"an eye on the tower's top floor was refused as inside the shell: {what}",
              f"{p}: {kind} {fails[:1]}")
    kind, fails, _ = S.inside_verdict(got.get(inside[0]), ARGS)
    check("IS5", kind == "inside" and any("back face" in f for f in fails),
          f"an eye inside the tower's masonry was not refused: {inside[1]}", f"{inside[0]}: {kind} {fails[:1]}")


class Rig:
    def __init__(self, tmp: Path, vp: Path, game: Path):
        self.tmp, self.vp, self.game = tmp, vp, game
        self.lab = LAB.Lab(tmp / "lab", vp, game)

    def render(self, out, cell, *flags):
        return self.lab.render(out, cell, *flags)


CASES = [is1_is2_is3, is4_thick_walls, is5_room_inside_a_shell]


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vp", default=str(PROD_VP))
    ap.add_argument("--only", default="", help="IS1 (runs IS1-IS3), IS4, IS5")
    ap.add_argument("--keep", default=None)
    a = ap.parse_args(argv)
    only = {x.strip().upper() for x in a.only.split(",") if x.strip()}
    game, why = LAB.game_folder()
    if game is None:
        print("NOT RUN:", why)
        return 3
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="is-"))
    tmp.mkdir(parents=True, exist_ok=True)
    rig = Rig(tmp, Path(a.vp), game)
    for fn in CASES:
        name = fn.__name__.split("_")[0].upper()
        if only and name not in only:
            continue
        before = len(FAILS)
        try:
            fn(rig)
        except Exception as exc:  # a crash is reported as a crash, never as the case's own red
            FAILS.append(name)
            RESULTS.append((name, "CRASH", f"{type(exc).__name__}: {exc} | {traceback.format_exc().splitlines()[-3:]}"))
        if len(FAILS) == before:
            RESULTS.append((name, "GREEN", ""))
    for case, verdict, text in RESULTS:
        print(f"{case:5} {verdict:5} {text}")
    print(f"work folder: {tmp}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
