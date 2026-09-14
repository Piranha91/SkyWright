"""Offline checks for shotplan's PLANNING path: what it believes, and what it publishes.

    python test_shotplan_plan.py

No game and no renderer. `fake_viewprobe.py` stands in for the binary and answers from a declared
toy scene, so the real `Probe` subprocess, the real parsers, the real candidate scoring, the real
final verification and the real artifact writers all run. What is replaced is the rasteriser, not
the planner -- a unit test of a helper would have missed every bug these cases cover, because all
of them are about which answer reaches the exported file.

Companion to `test_shotplan_camera.py`, which keeps the settled angle/FOV/ini contract.

The scenes are synthetic and the coordinates are made up. Nothing here is evidence about any real
cell; each case is a KNOWN ANSWER, chosen so the right verdict is arithmetic a reader can redo.
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shotplan as S

HERE = os.path.dirname(os.path.abspath(__file__))
# --vp is shlex.split POSIX-style, which eats Windows backslashes; both interpreters accept
# forward slashes, so the command is written that way rather than quoted into a corner.
FAKE = (sys.executable.replace("\\", "/") + " "
        + os.path.join(HERE, "fake_viewprobe.py").replace("\\", "/"))
FAILS = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


# --------------------------------------------------------------------------------------
# the scenes
# --------------------------------------------------------------------------------------
#
# ROOM is a closed 1,600 u box so every clearance bearing is BOUNDED (an unbounded bearing is
# refused by design, and a scene without walls would refuse everything for the wrong reason).
#
#   plinth   a low block; the declared LOW surface point sits mid-height on its -Y face
#   chest    a waist-high blocker on the -Y approach. Its top (60) is BELOW the probe eye
#            (floor + 120), so the eye-height clearance map flies straight over it and calls
#            the bearing clear -- which is the whole point: a low feature can be hidden behind
#            furniture that the map cannot see.
#   panel    a thin vertical board well ABOVE the eye; the declared HIGH point is on its -Y face

def room(*extra, floor=0.0, nav=True, silent_nav=(), silent_inside=()):
    boxes = [
        {"id": "000201:Test.esp", "edid": "ShellN", "min": [-860, 800, 0], "max": [860, 860, 400]},
        {"id": "000202:Test.esp", "edid": "ShellS", "min": [-860, -860, 0], "max": [860, -800, 400]},
        {"id": "000203:Test.esp", "edid": "ShellE", "min": [800, -860, 0], "max": [860, 860, 400]},
        {"id": "000204:Test.esp", "edid": "ShellW", "min": [-860, -860, 0], "max": [-800, 860, 400]},
    ]
    boxes += list(extra)
    sc = {"floorZ": floor, "boxes": boxes,
          "navSilent": [list(p) for p in silent_nav],
          "insideSilent": [list(p) for p in silent_inside]}
    if nav:
        sc["nav"] = [{"min": [-790, -790], "max": [790, 790], "z": floor, "name": "Test.esm"}]
    else:
        sc["nav"] = []
    return sc


PLINTH = {"id": "000301:Test.esp", "edid": "Plinth", "min": [-60, -60, 0], "max": [60, 60, 20]}
PLINTH_ALPHA = dict(PLINTH, alpha=True)
CHEST = {"id": "000302:Test.esp", "edid": "Chest", "min": [-120, -210, 0], "max": [120, -150, 60]}
PANEL = {"id": "000303:Test.esp", "edid": "Panel", "min": [-100, 38, 180], "max": [100, 42, 240]}

LOW = "0,-60,10"     # mid-height on the plinth's -Y face
HIGH = "0,38,200"    # on the panel's -Y face, 80 u above the probe eye
AIR = "0,-120,10"    # empty space 60 u in FRONT of the plinth face -- nothing is there
EYE = "0,-300,120"


def run_cli(argv, scene):
    """Drive shotplan's own main() end to end. Returns (exit code, stdout)."""
    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(scene, f)
    os.environ["FAKE_VIEWPROBE_SCENE"] = path
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(io.StringIO()):
            try:
                code = S.main(argv)
            except SystemExit as e:
                code = e.code if isinstance(e.code, int) else 1
    finally:
        os.unlink(path)
    return code, buf.getvalue()


def check_cli(scene, *extra):
    """`check` against a fixed eye -- the deterministic path for surface semantics."""
    fd, jp = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    code, out = run_cli(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                         "--floor-z", "0", "--json", jp, *extra], scene)
    with open(jp, "r", encoding="utf-8") as f:
        doc = json.load(f)
    os.unlink(jp)
    return code, out, doc


def status_of(doc, label):
    for t in doc["verification"]["targets"]:
        if t["label"] == label:
            return t["status"]
    return "<absent>"


# --------------------------------------------------------------------------------------
# 1. the final check governs what is published
# --------------------------------------------------------------------------------------

def test_failed_verification_cannot_export_success():
    """The retained R10 shape: the eye-height map says clear, the aimed ray says blocked.

    A low curb whose top (110) sits just under the probe eye (120) is invisible to the map at the
    sampled elevations, and squarely in the way of the ray the delivered camera takes down to the
    subject's head. Before this fix that printed BLOCKED and still wrote `chosen.ok: true`, a
    worklist and exit 0.
    """
    curb = [
        {"id": "000401:Test.esp", "edid": "CurbN", "min": [-120, 100, 0], "max": [120, 120, 110]},
        {"id": "000402:Test.esp", "edid": "CurbS", "min": [-120, -120, 0], "max": [120, -100, 110]},
        {"id": "000403:Test.esp", "edid": "CurbE", "min": [100, -120, 0], "max": [120, 120, 110]},
        {"id": "000404:Test.esp", "edid": "CurbW", "min": [-120, -120, 0], "max": [-100, 120, 110]},
    ]
    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    jp = os.path.join(tempfile.mkdtemp(), "plan.json")
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                         "--quiet", "--json", jp, "--worklist", wl], room(*curb))
    doc = json.load(open(jp, encoding="utf-8"))
    check("R10 shape: the map calls the bearing clear", "clear -- first geometry" in out)
    check("R10 shape: the aimed probe refuses it", "BLOCKED" in out)
    check("R10 shape: exit code is non-zero", code == 1, f"exit {code}")
    check("R10 shape: exported status is refused", doc["status"] == "refused", doc["status"])
    check("R10 shape: chosen.ok is FALSE", doc["chosen"]["ok"] is False)
    check("R10 shape: the pre-verification verdict is kept, not silently rewritten",
          doc["chosen"]["preVerificationOk"] is True)
    check("R10 shape: the refusal reason reaches chosen.fail",
          any("verification:" in f for f in doc["chosen"]["fail"]))
    check("R10 shape: no worklist is written", not os.path.exists(wl))
    check("R10 shape: the candidate walk is reported", len(doc["verifyAttempts"]) == 3,
          f"{len(doc['verifyAttempts'])} attempts")
    check("R10 shape: an exhausted search is not called a proof",
          "NOT a proof that no camera" in out)
    return doc


def test_pose_identity_binds_the_result():
    doc = test_failed_verification_cannot_export_success()
    check("identity: the verification names the pose it ran on",
          doc["verification"]["poseId"] == doc["chosen"]["poseId"] == doc["poseId"])
    ids = {a["poseId"] for a in doc["verifyAttempts"]}
    check("identity: each candidate attempt has its OWN id, so an earlier one cannot pass for "
          "the final one", len(ids) == len(doc["verifyAttempts"]), f"{len(ids)} distinct")
    check("identity: the delivered id is the LAST attempt's",
          doc["verifyAttempts"][-1]["poseId"] == doc["poseId"])


def test_skipped_verification_is_not_a_pass():
    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    jp = os.path.join(tempfile.mkdtemp(), "plan.json")
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                         "--quiet", "--no-verify", "--json", jp, "--worklist", wl],
                        room(PLINTH))
    doc = json.load(open(jp, encoding="utf-8"))
    check("--no-verify: status is unverified", doc["status"] == "unverified", doc["status"])
    check("--no-verify: the verification block is MISSING, not empty-and-passing",
          doc["verification"] is None)
    check("--no-verify: chosen.verified is false", doc["chosen"]["verified"] is False)
    check("--no-verify: exit code is 2, distinct from a refusal and from a pass", code == 2,
          f"exit {code}")
    wl_doc = json.load(open(wl, encoding="utf-8"))
    shot = wl_doc["cells"][0]["shots"][0]
    check("--no-verify: the worklist shot is labelled UNVERIFIED",
          shot["label"].startswith("UNVERIFIED -- "), shot["label"])
    check("--no-verify: the worklist shot carries the status and the pose id",
          shot["status"] == "unverified" and shot["poseId"] == doc["poseId"])


def test_verified_run_publishes_cleanly():
    """The positive control: a plan that really does verify must still pass, exit 0 and emit."""
    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    jp = os.path.join(tempfile.mkdtemp(), "plan.json")
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                         "--quiet", "--json", jp, "--worklist", wl], room(PLINTH))
    doc = json.load(open(jp, encoding="utf-8"))
    check("clear scene: status is verified", doc["status"] == "verified", doc["status"])
    check("clear scene: exit code is 0", code == 0, f"exit {code}")
    check("clear scene: chosen.verified is true", doc["chosen"]["verified"] is True)
    check("clear scene: a worklist IS written", os.path.exists(wl))
    check("clear scene: its label carries no warning prefix",
          not json.load(open(wl, encoding="utf-8"))["cells"][0]["shots"][0]["label"]
          .startswith(("UNVERIFIED", "REFUSED")))


def test_emit_refused_marks_the_file():
    curb = [{"id": "000401:Test.esp", "edid": "CurbN", "min": [-120, 100, 0], "max": [120, 120, 110]},
            {"id": "000402:Test.esp", "edid": "CurbS", "min": [-120, -120, 0], "max": [120, -100, 110]},
            {"id": "000403:Test.esp", "edid": "CurbE", "min": [100, -120, 0], "max": [120, 120, 110]},
            {"id": "000404:Test.esp", "edid": "CurbW", "min": [-120, -120, 0], "max": [-100, 120, 110]}]
    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                         "--quiet", "--worklist", wl, "--emit-refused"], room(*curb))
    shot = json.load(open(wl, encoding="utf-8"))["cells"][0]["shots"][0]
    check("--emit-refused: the file is written", os.path.exists(wl))
    check("--emit-refused: and says REFUSED in its label",
          shot["label"].startswith("REFUSED -- "), shot["label"])
    check("--emit-refused: with the per-target verification beside it",
          shot["verification"]["status"] == "failed")
    check("--emit-refused: the exit code still refuses", code == 1, f"exit {code}")


def test_missing_placement_evidence_refuses():
    """A point the probe never answered for has NOT been placement-tested."""
    code, out, doc = check_cli(
        room(PLINTH, silent_nav=[(0, -300)], silent_inside=[(0, -300, 120)]),
        "--surface", LOW)
    check("missing evidence: a silent --nav-at is refused, not passed",
          "placement: unknown" in "; ".join(doc["fails"]), "; ".join(doc["fails"]))
    check("missing evidence: a silent --inside-at is refused too",
          any("inside" in f for f in doc["fails"]), "; ".join(doc["fails"]))
    check("missing evidence: the verdict is REFUSED", code == 1, f"exit {code}")


# --------------------------------------------------------------------------------------
# 2. declared surface targets
# --------------------------------------------------------------------------------------

def test_surface_hit_is_confirmed():
    code, out, doc = check_cli(room(PLINTH), "--surface", f"{LOW}=low")
    check("surface: a ray that STOPS at the declared point confirms it",
          status_of(doc, "low") == "confirmed", status_of(doc, "low"))
    check("surface: and the camera is accepted", code == 0, f"exit {code}")


def test_nearer_blocker_is_blocked():
    """A low feature behind furniture the eye-height map flies over."""
    code, out, doc = check_cli(room(PLINTH, CHEST), "--surface", f"{LOW}=low")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "low"][0]
    check("surface: a nearer hit is BLOCKED", t["status"] == "blocked", t["status"])
    check("surface: the blocker's distance is reported", t["hit"]["dist"] < t["want"],
          f"{t['hit']['dist']} < {t['want']}")
    check("surface: and the camera is refused", code == 1, f"exit {code}")


def test_farther_hit_does_not_establish_the_surface():
    """The counterexample the old rule got wrong: a hit BEYOND the target used to read `clear`."""
    code, out, doc = check_cli(room(PLINTH), "--surface", f"{AIR}=air")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "air"][0]
    check("surface: a hit past the declared point is NOT-SURFACE, not clear",
          t["status"] == "not-surface", t["status"])
    check("surface: the report says why it is unproven rather than clear",
          "not established" in t["why"] or "NOT established" in t["why"], t["why"])
    check("surface: the same geometry would satisfy a SPACE subject, which is the distinction",
          t["hit"]["dist"] > t["want"], f"{t['hit']['dist']} > {t['want']}")
    check("surface: and the camera is refused", code == 1, f"exit {code}")


def test_missing_depth_response_is_unproven():
    """An alpha-blended face writes no depth: the ray finds nothing and nothing is proved."""
    scene = {"floorZ": None, "boxes": [PLINTH_ALPHA], "nav": [], "navSilent": [], "insideSilent": []}
    code, out, doc = check_cli(scene, "--surface", f"{LOW}=low")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "low"][0]
    check("surface: no depth-writing geometry reads UNPROVEN, never clear",
          t["status"] == "unproven", t["status"])
    check("surface: the alpha limit is named in the reason", "alpha" in t["why"], t["why"])


def test_declared_alpha_surface_is_not_confirmed_by_what_is_behind_it():
    code, out, doc = check_cli(room(PLINTH_ALPHA), "--surface", f"{LOW}=low")
    check("surface: an alpha face with the floor behind it is not confirmed",
          status_of(doc, "low") in ("not-surface", "unproven"), status_of(doc, "low"))


def test_feature_above_the_eye():
    # Aimed at the feature itself: with no space subject to stand on, --floor-z gives the storey
    # and the default aim is the declared point rather than a head height above an invented floor.
    code, out, doc = check_cli(room(PLINTH, PANEL), "--surface", f"{HIGH}=high",
                               "--aim", "0,-60,10")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "high"][0]
    check("surface: a feature 80 u above the eye is confirmed on its own ray",
          t["status"] == "confirmed", t["status"])
    check("surface: and its pick is ABOVE the frame centre (v offset looks up)",
          t["vOff"] > 0 and t["pixel"][1] < 160, f"vOff {t['vOff']} pixel {t['pixel']}")


def test_off_frame_target_is_reported_separately_from_its_sightline():
    far = {"id": "000305:Test.esp", "edid": "SidePost",
           "min": [600, -140, 0], "max": [640, -60, 120]}
    code, out, doc = check_cli(room(PLINTH, far), "--surface", f"{LOW}=low",
                               "--surface", "600,-100,60=side", "--aim", "0,-60,10")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "side"][0]
    check("off-frame: containment is reported separately from the sightline",
          t["inDeliveredFrame"] is False and t["status"] in ("confirmed", "blocked", "not-surface",
                                                             "unproven", "off-probe"),
          f"inFrame {t['inDeliveredFrame']} status {t['status']}")
    check("off-frame: it appears in the refusal", code == 1, f"exit {code}")
    check("off-frame: and the report says so in words", "OFF-FRAME" in out)


def test_tolerance_scales_with_distance_and_canvas():
    class A:
        surface_tol, surface_obliquity = 2.0, 1.73
    near = S.surface_tolerance(50.0, 65.0, 320, A)
    far = S.surface_tolerance(500.0, 65.0, 320, A)
    coarse = S.surface_tolerance(500.0, 65.0, 80, A)
    check("tolerance: grows with distance", far > near, f"{near:.2f} -> {far:.2f}")
    check("tolerance: grows on a coarser canvas", coarse > far, f"{far:.2f} -> {coarse:.2f}")
    check("tolerance: stays far below the counterexamples it has to separate", far < 10.0,
          f"{far:.2f} u at 500 u")
    check("tolerance: never below the rounding floor", near >= 2.0, f"{near:.2f}")


def test_canvas_size_changes_the_pixel_not_the_pose():
    a = check_cli(room(PLINTH, PANEL), "--surface", f"{HIGH}=high", "--aim", "0,-60,10")[2]
    b = check_cli(room(PLINTH, PANEL), "--surface", f"{HIGH}=high", "--aim", "0,-60,10",
                  "--size", "160")[2]
    ta = [x for x in a["verification"]["targets"] if x["label"] == "high"][0]
    tb = [x for x in b["verification"]["targets"] if x["label"] == "high"][0]
    check("canvas: a different probe size moves the PIXEL", ta["pixel"] != tb["pixel"],
          f"{ta['pixel']} vs {tb['pixel']}")
    check("canvas: but not the pose's angles", abs(ta["hOff"] - tb["hOff"]) < 1e-6
          and abs(ta["vOff"] - tb["vOff"]) < 1e-6)
    check("canvas: and both still confirm the same surface",
          ta["status"] == tb["status"] == "confirmed", f"{ta['status']}/{tb['status']}")
    check("canvas: the pose id changes with the canvas, so the two are not interchangeable",
          a["poseId"] != b["poseId"])


def test_surface_input_contract_refuses_a_flat_spec():
    code, out = run_cli(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                         "--floor-z", "0", "--surface", "0,-60"], room(PLINTH))
    check("contract: --surface without a Z is refused, with the reason", code != 0)


def test_surface_count_is_bounded():
    args = ["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet", "--floor-z", "0",
            "--surface-max", "2"]
    for i in range(3):
        args += ["--surface", f"0,-60,{10 + i}"]
    code, out = run_cli(args, room(PLINTH))
    check("contract: more surface points than --surface-max is refused", code != 0)


def test_surface_only_plan_refuses_to_invent_a_storey():
    """A surface point's Z is a feature height. Taking it for the floor put the probe eye above
    the feature and aimed the camera at the ceiling, so it is refused by name instead."""
    code, out = run_cli(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                         "--surface", f"{HIGH}=high"], room(PLINTH, PANEL))
    check("storey: a surface-only target with no --floor-z is refused", code != 0, f"exit {code}")


def test_surface_map_ranks_but_never_refuses():
    """The eye-height map cannot refuse a feature at another height -- it may only rank it."""
    code, out = run_cli(["plan", "TestCell", "--surface", f"{LOW}=low", "--vp", FAKE,
                         "--quiet", "--floor-z", "0", "--standoff-min", "250",
                         "--standoff-max", "450"], room(PLINTH, CHEST))
    check("ranking: the map's objection is recorded as a NOTE about ranking, not a refusal",
          "RANKING only" in out or "ranked down" in out)
    check("ranking: the aimed probe is what decides", "verification:" in out or "BLOCKED" in out
          or "CONFIRMED" in out)


ALL = [test_failed_verification_cannot_export_success,
       test_pose_identity_binds_the_result,
       test_skipped_verification_is_not_a_pass,
       test_verified_run_publishes_cleanly,
       test_emit_refused_marks_the_file,
       test_missing_placement_evidence_refuses,
       test_surface_hit_is_confirmed,
       test_nearer_blocker_is_blocked,
       test_farther_hit_does_not_establish_the_surface,
       test_missing_depth_response_is_unproven,
       test_declared_alpha_surface_is_not_confirmed_by_what_is_behind_it,
       test_feature_above_the_eye,
       test_off_frame_target_is_reported_separately_from_its_sightline,
       test_tolerance_scales_with_distance_and_canvas,
       test_canvas_size_changes_the_pixel_not_the_pose,
       test_surface_input_contract_refuses_a_flat_spec,
       test_surface_count_is_bounded,
       test_surface_only_plan_refuses_to_invent_a_storey,
       test_surface_map_ranks_but_never_refuses]


if __name__ == "__main__":
    for fn in ALL:
        print(f"\n{fn.__name__}")
        fn()
    print("\n" + ("ALL PASS" if not FAILS else f"{len(FAILS)} FAILED: " + ", ".join(FAILS)))
    sys.exit(1 if FAILS else 0)
