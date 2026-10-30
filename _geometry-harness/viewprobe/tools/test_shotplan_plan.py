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

def room(*extra, floor=0.0, nav=True, silent_nav=(), silent_inside=(), silent_clear=(),
         no_floor=False, no_box=0, unread=0):
    boxes = [
        {"id": "000201:Test.esp", "edid": "ShellN", "min": [-860, 800, 0], "max": [860, 860, 400]},
        {"id": "000202:Test.esp", "edid": "ShellS", "min": [-860, -860, 0], "max": [860, -800, 400]},
        {"id": "000203:Test.esp", "edid": "ShellE", "min": [800, -860, 0], "max": [860, 860, 400]},
        {"id": "000204:Test.esp", "edid": "ShellW", "min": [-860, -860, 0], "max": [-800, 860, 400]},
    ]
    boxes += list(extra)
    sc = {"floorZ": floor, "boxes": boxes,
          "navSilent": [list(p) for p in silent_nav],
          "insideSilent": [list(p) for p in silent_inside],
          "clearSilent": [list(p) for p in silent_clear],
          "clearNoBox": no_box, "clearUnread": unread}
    # A room with NO floor at all -- the shape the three real off-mesh cameras turned out to be in,
    # where the eye hangs in the void outside the shell and nothing is below it.
    if no_floor:
        del sc["floorZ"]
    if nav:
        sc["nav"] = [{"min": [-790, -790], "max": [790, 790], "z": floor, "name": "Test.esm"}]
    else:
        sc["nav"] = []
    return sc


PLINTH = {"id": "000301:Test.esp", "edid": "Plinth", "min": [-60, -60, 0], "max": [60, 60, 20]}
PLINTH_ALPHA = dict(PLINTH, alpha=True)
CHEST = {"id": "000302:Test.esp", "edid": "Chest", "min": [-120, -210, 0], "max": [120, -150, 60]}
PANEL = {"id": "000303:Test.esp", "edid": "Panel", "min": [-100, 38, 180], "max": [100, 42, 240]}
# One curb, on the +Y approach only: its top (110) is under the probe eye (120), so the clearance
# map flies over it and ranks that bearing first, while the aimed ray down to the subject clips
# it. The R10 shape again -- but on ONE side, so the FIRST candidate fails and a later one passes.
CURB_N = {"id": "000401:Test.esp", "edid": "CurbN", "min": [-120, 100, 0], "max": [120, 120, 110]}

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


def eye_level_shots(wl):
    """Shots a worklist offers as cameras. Since session 5 a refused plan may carry ONE shot, the
    labelled top-down fallback (`status: fallback`); that is evidence about the geometry, never an
    offered camera, so "no camera is published" means no shot other than a fallback."""
    return [s for c in (wl.get("cells") or []) for s in (c.get("shots") or [])
            if s.get("status") != "fallback"]


def says_why_evidence(doc, cause):
    """The file must say WHY no camera is offered: the base's claim stub ("did not finish"), or --
    since the top-down fallback -- a refusal record of kind `evidence incomplete` naming THIS
    cause, with the fallback shot's note naming it too. A refusal that dropped the cause is not
    an explanation."""
    if "did not finish" in doc.get("note", "") and "refusal" not in doc:
        return True
    ref = doc.get("refusal") or {}
    notes = " ".join(s.get("note", "") for c in (doc.get("cells") or []) for s in (c.get("shots") or []))
    return (ref.get("kind") == "evidence incomplete" and cause in (ref.get("causes") or [])
            and cause in notes)


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
    check("R10 shape: the worklist path holds a refusal record, not an eye-level shot",
          json.load(open(wl, encoding="utf-8")).get("status") == "refused"
          and not eye_level_shots(json.load(open(wl, encoding="utf-8"))))
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
# 1b. the off-mesh allowance, paid for in geometry
# --------------------------------------------------------------------------------------
#
# `nav_verdict`'s `off-mesh-near` arm passes a point NO navmesh covers, on the strength of its
# distance to the nearest triangle, and used to call it "a furniture footprint inside the room".
# Distance cannot say that. The cases below are the two shapes that arm has to tell apart, plus
# the three ways the geometry answer can be missing. Each is a KNOWN ANSWER over declared boxes.
#
# OFF_EYE stands on a 90 u pedestal 300 u out on -Y, outside the nav rectangle below, so every
# case here takes the off-mesh-near arm and nothing else.

OFF_EYE = "0,-900,120"
# navmesh that stops short of the eye's XY: nearest triangle ~110 u away, inside --offmesh-tol 160
NEAR_NAV = [{"min": [-790, -790], "max": [790, 790], "z": 0.0, "name": "Test.esm"}]
# a shell one box further out, so the clearance map is still bounded on every bearing
BIG_SHELL = [
    {"id": "000201:Test.esp", "edid": "ShellN", "min": [-1160, 1100, 0], "max": [1160, 1160, 400]},
    {"id": "000202:Test.esp", "edid": "ShellS", "min": [-1160, -1160, 0], "max": [1160, -1100, 400]},
    {"id": "000203:Test.esp", "edid": "ShellE", "min": [1100, -1160, 0], "max": [1160, 1160, 400]},
    {"id": "000204:Test.esp", "edid": "ShellW", "min": [-1160, -1160, 0], "max": [-1100, 1160, 400]},
]


def off_mesh_scene(**kw):
    """A room whose navmesh stops 110 u short of the eye -- the off-mesh-near arm, every time."""
    sc = {"boxes": list(BIG_SHELL) + list(kw.pop("extra", [])),
          "nav": NEAR_NAV,
          "navSilent": [], "insideSilent": [],
          "clearSilent": [list(q) for q in kw.pop("silent_clear", ())],
          "clearNoBox": kw.pop("no_box", 0), "clearUnread": kw.pop("unread", 0),
          "clearObndOnly": kw.pop("obnd_only", 0),
          "insideObndOnly": kw.pop("nom_obnd_only", 0),
          "insideUnreadable": kw.pop("nom_unreadable", 0),
          "insideCoverStyle": kw.pop("cover_style", "mesh")}
    if (terrain := kw.pop("terrain", None)) is not None:
        sc["clearTerrain"] = terrain
    if not kw.pop("no_floor", False):
        sc["floorZ"] = 0.0
    assert not kw, kw
    return sc


def off_mesh_check(scene):
    fd, jp = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    code, out = run_cli(["check", "TestCell", "--eye", OFF_EYE, "--vp", FAKE, "--quiet",
                         "--floor-z", "0", "--json", jp,
                         "--subject", "0,0,0=target"], scene)
    with open(jp, "r", encoding="utf-8") as f:
        doc = json.load(f)
    os.unlink(jp)
    return code, out, doc


def test_off_mesh_over_a_floor_is_still_accepted():
    """The vantage the allowance exists for: off navmesh, but a real floor under the eye.

    This is the control that must NOT change. The rule was written for a camera standing on a
    bed footprint 80 u from the nearest triangle, and a correction that refuses it has replaced
    one wrong answer with another.
    """
    code, out, doc = off_mesh_check(off_mesh_scene())
    pl = doc["placement"]
    check("off-mesh + floor: the nav arm is still off-mesh-near", pl["nav"] == "off-mesh-near",
          pl["nav"])
    check("off-mesh + floor: the geometry SUPPORTS it", pl["support"] == "supported",
          pl["support"] + " -- " + pl["supportWhy"])
    check("off-mesh + floor: and the camera is accepted", code == 0, f"exit {code}")
    check("off-mesh + floor: the report names the surface it measured",
          "120" in pl["supportWhy"], pl["supportWhy"])


def test_off_mesh_over_nothing_is_refused():
    """The three real cameras this correction came from, reproduced: off navmesh AND nothing
    at all under the eye."""
    code, out, doc = off_mesh_check(off_mesh_scene(no_floor=True))
    pl = doc["placement"]
    check("off-mesh + void: the nav arm alone would still pass it",
          pl["nav"] == "off-mesh-near", pl["nav"])
    check("off-mesh + void: the geometry CONTRADICTS it", pl["support"] == "contradicted",
          pl["support"] + " -- " + pl["supportWhy"])
    check("off-mesh + void: the camera is REFUSED", code == 1, f"exit {code}")
    check("off-mesh + void: and the refusal says the placement is why",
          any("placement" in f and "support" in f for f in doc["fails"]), "; ".join(doc["fails"]))


def test_off_mesh_over_another_storey_is_refused():
    """A floor exists, but it belongs to a level below -- outside --storey-tol of the declared one.

    `floorZ` -200 puts the only surface 320 u under a 120 u eye: 200 u below the declared floor 0,
    past the 96 u storey band. Same shape as the Ratway camera whose only surface below was
    another corridor 220 u down, 100 u under its own declared floor.
    """
    sc = off_mesh_scene()
    sc["floorZ"] = -200.0
    code, out, doc = off_mesh_check(sc)
    pl = doc["placement"]
    check("off-mesh + wrong level: the geometry CONTRADICTS it", pl["support"] == "contradicted",
          pl["support"] + " -- " + pl["supportWhy"])
    check("off-mesh + wrong level: the report says which level it found",
          "another level" in pl["supportWhy"], pl["supportWhy"])
    check("off-mesh + wrong level: the camera is REFUSED", code == 1, f"exit {code}")


def test_incomplete_obnd_coverage_is_unresolved_not_clear():
    """An absent answer over an INCOMPLETE broad phase is UNKNOWN, and unknown does not publish.

    The broad phase nominates by record OBND, so a physical ref with no usable box never reaches
    the ray test. "Nothing found" then says as much about the boxes as about the building, and
    reading it as clearance is the same silent pass this whole correction is about.
    """
    code, out, doc = off_mesh_check(off_mesh_scene(no_floor=True, no_box=3))
    pl = doc["placement"]
    check("incomplete coverage: the answer is UNRESOLVED, not contradicted and not clear",
          pl["support"] == "unresolved", pl["support"] + " -- " + pl["supportWhy"])
    check("incomplete coverage: the report names the blind spot",
          "OBND" in pl["supportWhy"], pl["supportWhy"])
    check("incomplete coverage: and an unresolved placement is still REFUSED", code == 1,
          f"exit {code}")


def test_unreadable_meshes_are_unresolved_not_clear():
    """Same rule for a candidate whose mesh could not be read: a weaker answer, stated."""
    code, out, doc = off_mesh_check(off_mesh_scene(no_floor=True, unread=2))
    pl = doc["placement"]
    check("unreadable mesh: the answer is UNRESOLVED", pl["support"] == "unresolved",
          pl["support"] + " -- " + pl["supportWhy"])
    check("unreadable mesh: the report says the meshes could not be read",
          "could not be read" in pl["supportWhy"], pl["supportWhy"])
    check("unreadable mesh: and the camera is REFUSED", code == 1, f"exit {code}")


def test_missing_terrain_outdoors_is_unresolved_not_contradicted():
    """Outdoors the ground is a LAND heightmap, not a placed reference.

    A run that did not build terrain has not looked at the thing an exterior camera stands on, so
    "nothing below" is a blind spot rather than an empty floor. Calling it CONTRADICTED would
    refuse every exterior vantage on a missing input -- the same silent wrong answer one level up.
    The camera is still refused, but for the reason that is true, and an operator reading the line
    knows to build terrain rather than to move the camera.
    """
    code, out, doc = off_mesh_check(off_mesh_scene(
        no_floor=True,
        terrain="terrain: NOT BUILT for this run (--no-terrain, or no LAND data loaded) "
                "-- absence below this eye is not evidence outdoors"))
    pl = doc["placement"]
    check("no terrain: the answer is UNRESOLVED, not contradicted",
          pl["support"] == "unresolved", pl["support"] + " -- " + pl["supportWhy"])
    check("no terrain: the report says the ground was never tested",
          "ground itself was never tested" in pl["supportWhy"], pl["supportWhy"])
    check("no terrain: an unresolved placement is still REFUSED", code == 1, f"exit {code}")


def test_terrain_gap_at_this_xy_is_unresolved():
    """Same rule one step finer: terrain was built, but no heightmap covers THIS XY."""
    code, out, doc = off_mesh_check(off_mesh_scene(
        no_floor=True,
        terrain="terrain: NO heightmap covers this XY -- absence below this eye is not evidence"))
    pl = doc["placement"]
    check("terrain gap: the answer is UNRESOLVED", pl["support"] == "unresolved",
          pl["support"] + " -- " + pl["supportWhy"])
    check("terrain gap: the report names the gap",
          "no LAND heightmap covers this XY" in pl["supportWhy"], pl["supportWhy"])
    check("terrain gap: and the camera is REFUSED", code == 1, f"exit {code}")


def test_interior_absence_is_still_contradicted():
    """The control for the two above: indoors there IS no terrain, so its absence says nothing and
    an empty answer stays a finding. A blind-spot rule that swallowed the interior case would have
    undone the correction it was added to protect."""
    code, out, doc = off_mesh_check(off_mesh_scene(no_floor=True))
    pl = doc["placement"]
    check("interior: absence below the eye is still CONTRADICTED",
          pl["support"] == "contradicted", pl["support"] + " -- " + pl["supportWhy"])
    check("interior: and the camera is REFUSED", code == 1, f"exit {code}")


def test_silent_clear_at_is_refused():
    """A point the probe never answered for has NOT had its placement tested -- the same rule the
    silent --nav-at case already carries, one query lower down."""
    code, out, doc = off_mesh_check(off_mesh_scene(silent_clear=[(0, -900, 120)]))
    pl = doc["placement"]
    check("silent clear-at: the answer is UNKNOWN, not clear", pl["support"] == "unknown",
          pl["support"] + " -- " + pl["supportWhy"])
    check("silent clear-at: and the camera is REFUSED", code == 1, f"exit {code}")


def test_support_does_not_override_the_other_placement_arms():
    """The support answer DECIDES only on the off-mesh arm.

    An on-storey eye keeps its navmesh answer, and the support line is reported beside it rather
    than re-judging it -- and it must not rescue an eye the inside test refuses either.
    """
    code, out, doc = check_cli(room(PLINTH), "--surface", f"{LOW}=low")
    pl = doc["placement"]
    check("on-storey: the nav arm is unchanged", pl["nav"] == "on-storey", pl["nav"])
    check("on-storey: the support answer is reported beside it",
          pl["support"] in ("supported", "contradicted", "unresolved"), pl["support"])
    check("on-storey: and the camera is still accepted", code == 0, f"exit {code}")
    check("the report states what support does NOT mean",
          "reachability" in pl["supportIs"], pl["supportIs"])


# --------------------------------------------------------------------------------------
# 1c. the two ways a box can lie
# --------------------------------------------------------------------------------------
#
# Both reported by Piranha on 2026-09-14 against the first version of this correction.


def test_unreadable_room_scale_mesh_is_unresolved_not_clear():
    """A room-scale box whose mesh cannot be read used to be waved through as "a shell".

    The box was the only evidence, and a box cannot tell a shell from a solid: the same eye is
    enclosed by a tower-kit wall piece's box and by the room it walls. Calling it clear is an
    assumption of exactly the kind the placement arm was corrected for. It is UNRESOLVED -- the eye
    was never tested -- and an unresolved eye must not publish a camera, whatever is underneath it.
    """
    shell = {"id": "000501:Test.esp", "edid": "BigShell", "type": "Static",
             "min": [-1400, -1400, -100], "max": [1400, 1400, 900], "meshUnreadable": True}
    code, out, doc = off_mesh_check(off_mesh_scene(extra=[shell]))
    fails = "; ".join(doc["fails"])
    check("unreadable shell: the eye is refused, not passed", code == 1, f"exit {code}")
    check("unreadable shell: the refusal says the eye was UNTESTED",
          "UNTESTED" in fails, fails)
    check("unreadable shell: and says a box cannot tell a shell from a solid",
          "shell from a solid" in fails, fails)
    check("unreadable shell: support underneath did NOT rescue it",
          doc["placement"]["support"] == "supported", doc["placement"]["support"])


def test_unreadable_small_box_still_refuses():
    """The control beside it: the small-box arm already refused and must keep refusing."""
    crate = {"id": "000502:Test.esp", "edid": "Crate", "type": "Container",
             "min": [-120, -1020, 0], "max": [120, -780, 240], "meshUnreadable": True}
    code, out, doc = off_mesh_check(off_mesh_scene(extra=[crate]))
    check("unreadable crate: still refused", code == 1, f"exit {code}")
    check("unreadable crate: and named as the box standing",
          "the box stands" in "; ".join(doc["fails"]), "; ".join(doc["fails"]))


def test_misleading_obnd_does_not_hide_the_mesh():
    """A record box that understates its mesh must not remove that mesh from the geometry answer.

    The floor here spans the room; its DECLARED box is a 20 u cube parked in a corner. The old
    broad phase filtered candidates on the record box, so a box like this dropped the ref silently
    and never touched the missing-box count -- which is why "zero coverage gaps" proved nothing.
    The contract now is that the ray test answers about the MESH. (This is the stand-in, so it
    proves the contract and not the renderer; the renderer's own arm is the real-cell A/B in
    RESULT.md §9, where `--clear-bounds obnd` reproduces the old filter.)
    """
    liar = {"id": "000503:Test.esp", "edid": "WideFloor", "type": "Static",
            "min": [-1100, -1100, -40], "max": [1100, 1100, 0],
            "obnd": {"min": [1000, 1000, -40], "max": [1020, 1020, -20]}}
    sc = off_mesh_scene(extra=[liar], no_floor=True)
    code, out, doc = off_mesh_check(sc)
    pl = doc["placement"]
    check("misleading OBND: the mesh is still found under the eye",
          pl["support"] == "supported", pl["support"] + " -- " + pl["supportWhy"])
    check("misleading OBND: and it is named as the thing holding the eye up",
          "WideFloor" in pl["supportWhy"], pl["supportWhy"])
    check("misleading OBND: the eye is accepted on that evidence", code == 0, f"exit {code}")
    # The point of the case is that the two evidence sources DISAGREE: the ray test finds the mesh
    # while the box-based nomination, which sees only the declared 20 u cube in the corner, does
    # not name it at all. Asserting both halves is what makes this a test of the distinction rather
    # than of the happy path.
    inside_block = out.split("--- inside")[1].split("--- doors")[0] if "--- inside" in out else ""
    check("misleading OBND: the box-based nomination does NOT name it -- the two sources differ",
          "WideFloor" not in inside_block, inside_block.strip()[:160])


def test_a_ref_selected_only_by_its_record_box_is_a_coverage_caveat():
    """When the mesh could not be read, the record box is all that is left to select with -- and
    nobody verified that box against the mesh. An ABSENCE over such a population is unresolved."""
    code, out, doc = off_mesh_check(off_mesh_scene(no_floor=True, obnd_only=2))
    pl = doc["placement"]
    check("obnd-only: absence over unverified boxes is UNRESOLVED",
          pl["support"] == "unresolved", pl["support"] + " -- " + pl["supportWhy"])
    check("obnd-only: the report names the unverified selection",
          "record OBND" in pl["supportWhy"], pl["supportWhy"])
    check("obnd-only: and the camera is REFUSED", code == 1, f"exit {code}")


def test_incomplete_nomination_is_not_clearance():
    """"No ref's box encloses the eye" is only clearance if every ref could be CHECKED.

    A ref whose mesh would not read was never ruled out. The old nomination said nothing about
    that population, so an empty answer read as a pass -- which is how an untested eye published a
    camera. The absence now has to come with complete coverage or it is UNRESOLVED.
    """
    code, out, doc = off_mesh_check(off_mesh_scene(nom_unreadable=2))
    fails = "; ".join(doc["fails"])
    check("incomplete nomination: an empty answer over unreadable refs is refused",
          code == 1, f"exit {code}")
    check("incomplete nomination: and says the nomination was INCOMPLETE",
          "INCOMPLETE" in fails, fails)
    check("incomplete nomination: naming the refs it could not rule out",
          "never ruled out" in fails, fails)


def test_nomination_by_unverified_record_box_is_not_clearance():
    """Same rule for a ref selectable only by a box nobody checked against its mesh."""
    code, out, doc = off_mesh_check(off_mesh_scene(nom_obnd_only=1))
    fails = "; ".join(doc["fails"])
    check("obnd-only nomination: refused", code == 1, f"exit {code}")
    check("obnd-only nomination: the report names the unverified box",
          "nobody verified" in fails, fails)


def test_a_complete_empty_nomination_is_still_clear():
    """The control that must NOT change: when every physical ref was nominated from its own mesh
    and none encloses the eye, that IS clearance, and the camera is accepted. A rule that refused
    here would refuse every healthy open-room camera."""
    code, out, doc = off_mesh_check(off_mesh_scene())
    check("complete nomination: an eye nothing encloses is still accepted", code == 0, f"exit {code}")
    check("complete nomination: and the note says the whole cell was tested",
          "every physical ref" in out, "")


def test_missing_coverage_statement_is_not_clearance():
    """Absent coverage is NOT zero gaps.

    The parser recorded a missing statement as `stated: False` and the verdict never read the
    field, so output that never said what it had checked -- an older renderer, a build predating
    the statement, anything unattributable -- returned `clear` AND printed "every physical ref in
    the cell was nominated from its own mesh bounds and tested". It claimed the coverage it had
    just failed to receive. Clearance is a positive claim and now requires a run that made it.
    """
    code, out, doc = off_mesh_check(off_mesh_scene(cover_style="none"))
    fails = "; ".join(doc["fails"])
    check("missing coverage: the camera is refused", code == 1, f"exit {code}")
    check("missing coverage: and the reason is the absent statement",
          "NO mesh-coverage statement" in fails, fails)
    check("missing coverage: it says absence is not zero gaps",
          "not zero gaps" in fails, fails)
    check("missing coverage: and the fully-checked claim is NOT printed",
          "every physical ref" not in out,
          "" if "every physical ref" not in out else "the unbacked coverage claim WAS printed")


def test_legacy_comparison_output_cannot_qualify_a_camera():
    """`--inside-bounds obnd` reproduces the OLD nomination on purpose.

    It is a measurement of the legacy behaviour and an instrument for the A/B in the result. It
    must never qualify a production camera -- which it could, because its header says something the
    coverage parser did not recognise and unrecognised read as fully checked.
    """
    code, out, doc = off_mesh_check(off_mesh_scene(cover_style="legacy"))
    fails = "; ".join(doc["fails"])
    check("legacy arm: the camera is refused", code == 1, f"exit {code}")
    check("legacy arm: and is named as the comparison arm",
          "COMPARISON arm" in fails, fails)
    check("legacy arm: which cannot qualify a camera",
          "cannot qualify a camera" in fails, fails)


def test_incomplete_nomination_prevents_publication():
    """Refusal has to stop the ARTIFACT, not just set an exit code.

    The acquisition path reads the worklist the planner emits; an untested eye that still wrote one
    would publish a camera nobody checked, which is the outcome all of this exists to prevent.
    """
    # Three ways the evidence can fall short, all of which must stop the artifact: a ref that could
    # not be read, a renderer that never stated its coverage, and the legacy comparison arm.
    for label, key, value, cause in (
            ("unreadable ref", "insideUnreadable", 1, "unreadable inside coverage"),
            ("missing coverage statement", "insideCoverStyle", "none", "missing coverage statement"),
            ("legacy comparison arm", "insideCoverStyle", "legacy", "legacy comparison arm")):
        wl2 = os.path.join(tempfile.mkdtemp(), "wl.json")
        sc2 = room(PLINTH)
        sc2[key] = value
        code2, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                            "--quiet", "--worklist", wl2], sc2)
        doc2 = json.load(open(wl2, encoding="utf-8"))
        check(f"publication ({label}): refused and no camera published",
              code2 != 0 and doc2.get("status") in ("incomplete", "refused") and not eye_level_shots(doc2),
              f"exit {code2}, {json.dumps(doc2)[:80]}")
        check(f"publication ({label}): and the file names its cause ({cause})",
              says_why_evidence(doc2, cause), json.dumps(doc2.get("refusal") or doc2.get("note"))[:160])

    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    sc = room(PLINTH)
    sc["insideUnreadable"] = 1
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE,
                         "--quiet", "--worklist", wl], sc)
    check("publication: the plan refuses", code != 0, f"exit {code}")
    # The path is CLAIMED up front with an `incomplete` stub (see claim_outputs), so its existence
    # is by design -- a stale verified worklist from an earlier run must not survive at that path.
    # The gate is therefore the CONTENT: no camera, and the file says why.
    doc = json.load(open(wl, encoding="utf-8"))
    check("publication: the worklist carries no camera",
          doc.get("status") in ("incomplete", "refused") and not eye_level_shots(doc), json.dumps(doc)[:120])
    check("publication: and says why: the claim stub, or a refusal naming unreadable inside coverage",
          says_why_evidence(doc, "unreadable inside coverage"),
          doc.get("note", "")[:80] or str(doc.get("refusal"))[:160])


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


# --------------------------------------------------------------------------------------
# 3. the four findings of the 2026-09-14 implementation review
# --------------------------------------------------------------------------------------

CURB = [{"id": "000401:Test.esp", "edid": "CurbN", "min": [-120, 100, 0], "max": [120, 120, 110]},
        {"id": "000402:Test.esp", "edid": "CurbS", "min": [-120, -120, 0], "max": [120, -100, 110]},
        {"id": "000403:Test.esp", "edid": "CurbE", "min": [100, -120, 0], "max": [120, 120, 110]},
        {"id": "000404:Test.esp", "edid": "CurbW", "min": [-120, -120, 0], "max": [-100, 120, 110]}]


def test_a_failed_rerun_cannot_leave_a_verified_artifact_behind():
    """Finding 1. The killer is path REUSE, which every fresh-path test misses."""
    d = tempfile.mkdtemp()
    wl, jp = os.path.join(d, "wl.json"), os.path.join(d, "plan.json")
    a, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                    "--json", jp, "--worklist", wl], room(PLINTH))
    first = json.load(open(wl, encoding="utf-8"))
    check("reuse: the first run really did verify and emit", a == 0
          and first["cells"][0]["shots"][0]["status"] == "verified")
    b, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                    "--json", jp, "--worklist", wl], room(*CURB))
    after_wl = json.load(open(wl, encoding="utf-8"))
    after_jp = json.load(open(jp, encoding="utf-8"))
    check("reuse: the second run refuses", b == 1, f"exit {b}")
    check("reuse: the worklist at that path NO LONGER claims verified",
          after_wl.get("status") == "refused", str(after_wl.get("status")))
    check("reuse: and offers no eye-level shot", not eye_level_shots(after_wl))
    check("reuse: the json at that path is the second run's", after_jp["status"] == "refused")
    check("reuse: no earlier pose id survives at either path",
          after_wl.get("poseId") != first["cells"][0]["shots"][0]["poseId"])


def test_an_early_refusal_cannot_leave_a_verified_artifact_behind():
    """Finding 1, second half: an early exit never reaches a writer at all."""
    d = tempfile.mkdtemp()
    jp = os.path.join(d, "plan.json")
    a, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                    "--json", jp], room(PLINTH))
    check("early refusal: a verified result exists first", a == 0
          and json.load(open(jp, encoding="utf-8"))["status"] == "verified")
    # No candidate can survive: nothing is bounded and nothing is walkable.
    b, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                    "--json", jp], {"floorZ": None, "boxes": [], "nav": []})
    doc = json.load(open(jp, encoding="utf-8"))
    check("early refusal: the run exits non-zero", b != 0, f"exit {b}")
    check("early refusal: the stale verified result is GONE",
          doc["status"] != "verified", doc["status"])


def test_skipped_verification_clears_the_final_boolean():
    """Finding 2. `chosen.ok` is documented as the final answer, so it cannot stay true."""
    d = tempfile.mkdtemp()
    jp = os.path.join(d, "plan.json")
    code, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                       "--no-verify", "--json", jp], room(PLINTH))
    doc = json.load(open(jp, encoding="utf-8"))
    check("--no-verify: chosen.ok is FALSE, matching the documented contract",
          doc["chosen"]["ok"] is False, str(doc["chosen"]["ok"]))
    check("--no-verify: the pre-verification success is preserved beside it",
          doc["chosen"]["preVerificationOk"] is True)
    check("--no-verify: and the reason names the skip",
          any("SKIPPED" in f for f in doc["chosen"]["fail"]))
    check("--no-verify: status and exit are unchanged",
          doc["status"] == "unverified" and code == 2)


def test_projection_uses_the_pose_that_is_actually_submitted():
    """Finding 3. Projecting from unrounded angles samples a different ray than it submits.

    A panel whose left edge is at x=0 with a declared point 0.15 u inside it. The candidate's
    angles round to 0.0/0.0 on submission; projecting from the unrounded values picks the pixel
    one column left, misses the panel entirely and reports `not-surface` against the far wall --
    while the pose actually submitted confirms it. Same exported pose, same pose id.
    """
    panel = {"id": "000601:Test.esp", "edid": "Panel",
             "min": [0, 250, 0], "max": [400, 254, 300]}
    back = {"id": "000602:Test.esp", "edid": "Back",
            "min": [-900, 800, 0], "max": [900, 804, 400]}
    scene = {"floorZ": None, "boxes": [panel, back], "nav": [], "navSilent": [],
             "insideSilent": []}
    fd, jp = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    code, out = run_cli(["check", "TestCell", "--eye", "0,0,100", "--floor-z", "0",
                         "--surface", "0.15,250,100.15=edge", "--aim", "0.15,250,100.15",
                         "--size", "1024", "--vp", FAKE, "--quiet", "--json", jp], scene)
    doc = json.load(open(jp, encoding="utf-8"))
    t = doc["verification"]["targets"][0]
    check("canonical pose: the edge target is CONFIRMED, not lost to a rounding mismatch",
          t["status"] == "confirmed", f"{t['status']} at {t.get('hit', {}).get('dist')}")
    p = doc["verification"]["pose"]
    check("canonical pose: the exported pose is already rounded to what was submitted",
          p["yaw"] == round(p["yaw"], 1) and p["pitch"] == round(p["pitch"], 1)
          and all(v == round(v, 1) for v in p["eye"]))


def test_pose_identity_binds_the_scene_configuration():
    """Finding 4. The same pose against two different declared scenes is not the same evidence."""
    d = tempfile.mkdtemp()
    ids = []
    for prof in ("ProfileA", "ProfileB"):
        jp = os.path.join(d, f"{prof}.json")
        run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                 "--json", jp, "--", "--profile", prof], room(PLINTH))
        ids.append(json.load(open(jp, encoding="utf-8")))
    check("scene binding: two profiles do NOT share a pose id",
          ids[0]["poseId"] != ids[1]["poseId"], f"{ids[0]['poseId']} vs {ids[1]['poseId']}")
    check("scene binding: the declared configuration is recorded",
          "ProfileA" in json.dumps(ids[0]["scene"]["declared"]))
    check("scene binding: an absent renderer fingerprint is stated, not implied",
          ids[0]["scene"]["renderer"] is None and "ABSENT" in ids[0]["scene"]["rendererIs"])
    check("scene binding: the declaration is labelled as not proof of bytes",
          "NOT proof" in ids[0]["scene"]["declaredIs"])


def test_an_input_refusal_cannot_leave_a_verified_artifact_behind():
    """Finding 1, third shape: a refusal raised while READING the inputs.

    The early-refusal case above fails after the maps have run. These three fail before any input
    is resolved at all, which is earlier than every writer -- and was earlier than the point where
    the output paths were claimed. Measured before the fix: each exited 1 while the previous run's
    `status: verified` JSON and its renderable worklist sat at the requested paths, still carrying
    that run's pose id. One case per refusing input reader, not per implementation detail.
    """
    d = tempfile.mkdtemp()
    jp, wp = os.path.join(d, "plan.json"), os.path.join(d, "worklist.json")
    scene = room(PLINTH)
    ok, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                     "--json", jp, "--worklist", wp], scene)
    first = json.load(open(jp, encoding="utf-8"))
    check("input refusal: a verified result and a renderable worklist exist first",
          ok == 0 and first["status"] == "verified"
          and bool(json.load(open(wp, encoding="utf-8"))["cells"][0]["shots"]), f"exit {ok}")
    stale = first["poseId"]
    cases = {
        "--surface with no Z": ["--surface=0,-60"],                       # collect_subjects
        "surface-only, no --floor-z": ["--surface=0,-60,10"],             # resolve_floor
        "more points than --surface-max": ["--surface=0,-60,10", "--surface=1,-60,10",
                                           "--surface=2,-60,10", "--surface-max", "2",
                                           "--floor-z", "0"],             # the bound
    }
    for name, extra in cases.items():
        code, _ = run_cli(["plan", "TestCell", "--vp", FAKE, "--quiet",
                           "--json", jp, "--worklist", wp, *extra], scene)
        doc = json.load(open(jp, encoding="utf-8"))
        wl = json.load(open(wp, encoding="utf-8"))
        check(f"input refusal ({name}): exits non-zero", code != 0, f"exit {code}")
        check(f"input refusal ({name}): no verified result survives at that path",
              doc.get("status") == "incomplete" and stale not in json.dumps(doc),
              str(doc.get("status")))
        check(f"input refusal ({name}): and no renderable worklist survives either",
              not wl.get("cells"), str(wl.get("status")))


def test_the_renderer_fingerprint_binds_when_viewprobe_prints_one():
    """Finding 4's other half: the renderer's OWN word about its inputs, when there is one.

    A declared profile name is a claim about bytes; viewprobe's `capture input configuration:`
    line is a hash of them. It is printed only for a candidate configuration (`--order`), so this
    case passes one through and the stand-in prints it under viewprobe's own gate. Two different
    fingerprints over the SAME pose and the SAME declared flags must not share an identity, or a
    verification cannot be shown to belong to the scene it ran against. Without this case the
    parser was never exercised and the binding was a claim about a regex.
    """
    d = tempfile.mkdtemp()
    fps = ["3f" + "0" * 62, "c7" + "1" * 62]
    got = []
    for i, fp in enumerate(fps):
        jp, wp = os.path.join(d, f"o{i}.json"), os.path.join(d, f"o{i}-wl.json")
        code, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                           "--json", jp, "--worklist", wp, "--", "--order", "cand.json"],
                          dict(room(PLINTH), configFingerprint=fp))
        got.append((code, json.load(open(jp, encoding="utf-8")),
                    json.load(open(wp, encoding="utf-8"))))
    (c0, a, awl), (_, b, _) = got
    check("renderer binding: the run verified", c0 == 0 and a["status"] == "verified", str(c0))
    check("renderer binding: viewprobe's own fingerprint reached the artifact",
          a["scene"]["renderer"] == fps[0], str(a["scene"]["renderer"])[:20])
    check("renderer binding: and it is no longer reported as absent",
          "ABSENT" not in a["scene"]["rendererIs"])
    check("renderer binding: two fingerprints do NOT share a pose id",
          a["poseId"] != b["poseId"], f"{a['poseId']} vs {b['poseId']}")
    check("renderer binding: the verification carries the plan's own pose and scene",
          a["verification"]["poseId"] == a["poseId"] and a["verification"]["scene"] == a["scene"])
    check("renderer binding: and the worklist names that same pose, scene and status",
          awl["poseId"] == a["poseId"] and awl["scene"] == a["scene"]
          and awl["status"] == a["status"], str(awl.get("status")))


def test_every_command_that_names_a_path_claims_it():
    """The artifact contract is not `plan`-only: `frame` reads an ini and can refuse too.

    A documented contract with one silent exception is worse than none, so the third subcommand
    that takes an output path is held to it as well -- a refused read must not leave the previous
    run's frame JSON at that path.
    """
    d = tempfile.mkdtemp()
    jp = os.path.join(d, "frame.json")
    prof = os.path.join(d, "inst", "profiles", "P")
    os.makedirs(prof)
    for name, body in (("settings.ini", "[General]\nLocalSettings=true\n"),
                       ("skyrimprefs.ini", "[Display]\niSize W=3840\niSize H=2160\n"
                                           "fDefaultWorldFOV=80.0\n")):
        with open(os.path.join(prof, name), "w", encoding="utf-8") as f:
            f.write(body)
    ok, _ = run_cli(["frame", "--instance", os.path.join(d, "inst"), "--profile", "P",
                     "--json", jp], {})
    check("frame: a real frame is written first", ok == 0
          and json.load(open(jp, encoding="utf-8")).get("fov_setting") == 80.0, f"exit {ok}")
    bad, _ = run_cli(["frame", "--instance", os.path.join(d, "inst"), "--profile", "Missing",
                      "--json", jp], {})
    doc = json.load(open(jp, encoding="utf-8"))
    check("frame: a refused read exits non-zero", bad != 0, f"exit {bad}")
    check("frame: and the earlier frame does not survive at that path",
          doc.get("status") == "incomplete" and "fov_setting" not in doc, str(doc.get("status")))


def test_the_planner_asks_the_renderer_for_its_configuration():
    """The other half of finding 4: a plain profile run must not be declaration-only by default.

    The earlier binding only worked when the CALLER happened to pass `--order`, which no normal
    run does -- so every ordinary plan recorded `renderer: null`. The planner now asks for the
    identity itself on the render whose answers decide the verdict, and asks ONCE, because
    resolving it hashes every loaded plugin. The scope
    is reported with it: this is a hash of records and providers, never of drawn pixels.
    """
    d = tempfile.mkdtemp()
    fp = "9b" + "e" * 62
    scene = dict(room(PLINTH), configFingerprint=fp)
    jp = os.path.join(d, "bound.json")
    trace = os.path.join(d, "trace.txt")
    os.environ["FAKE_VIEWPROBE_TRACE"] = trace
    try:
        code, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                           "--json", jp], scene)
    finally:
        os.environ.pop("FAKE_VIEWPROBE_TRACE", None)
    calls = [l for l in open(trace, encoding="utf-8").read().splitlines() if l.strip()]
    asked = [l for l in calls if "--config-identity" in l]
    doc = json.load(open(jp, encoding="utf-8"))
    check("asks: a plain run with no --order now binds the renderer's own fingerprint",
          code == 0 and doc["scene"]["renderer"] == fp, str(doc["scene"]["renderer"])[:20])
    check("asks: and it is asked for exactly once, not on every probe",
          len(asked) == 1 and len(calls) > 1, f"{len(asked)} of {len(calls)} invocation(s)")
    check("asks: the scope of the hash travels with it",
          any("plugins" in c for c in doc["scene"]["rendererCovers"])
          and "NOT mesh/material bytes" in doc["scene"]["rendererIs"])
    check("asks: the declaration is still labelled as not proof of bytes",
          "not a renderer build hash" in doc["scene"]["declaredIs"])

    jp2 = os.path.join(d, "unbound.json")
    code2, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                        "--no-bind-scene", "--json", jp2], scene)
    un = json.load(open(jp2, encoding="utf-8"))
    check("asks: --no-bind-scene returns to declaration-only and says so",
          code2 == 0 and un["scene"]["renderer"] is None
          and "ABSENT" in un["scene"]["rendererIs"] and un["scene"]["rendererCovers"] is None)
    check("asks: binding the scene changes the identity of the evidence",
          doc["poseId"] != un["poseId"], f"{doc['poseId']} vs {un['poseId']}")


def test_a_later_candidate_cannot_inherit_an_earlier_identity():
    """Reviewed 2026-09-14: the Runner cached the last fingerprint it had seen.

    A candidate whose verification supplied one, then a candidate whose verification supplied
    none, and the second reported the first's -- an identity the render in hand never produced,
    which is the silent wrong answer this binding exists to prevent. viewprobe really has that
    shape: a refused configuration resolve carries on rendering without an identity.

    One curb on the +Y approach gives the two-candidate retry; the stand-in answers the FIRST
    asking invocation and no other. Both existing suites passed with the defect present, so the
    case is the point: an identity has to belong to the render it is attached to.
    """
    d = tempfile.mkdtemp()
    jp, wp = os.path.join(d, "retry.json"), os.path.join(d, "retry-wl.json")
    fp = "ab" + "0" * 62
    code, _ = run_cli(["plan", "TestCell", "--subject", "0,0,0=t", "--vp", FAKE, "--quiet",
                       "--json", jp, "--worklist", wp],
                      dict(room(PLINTH, CURB_N), configFingerprint=fp,
                           configFingerprintAsks=[1]))
    doc = json.load(open(jp, encoding="utf-8"))
    wl = json.load(open(wp, encoding="utf-8"))
    at = doc["verifyAttempts"]
    check("retry: candidate 1 failed and candidate 2 verified",
          code == 0 and len(at) == 2 and at[0]["status"] == "failed"
          and at[1]["status"] == "verified", f"exit {code}, {len(at)} attempt(s)")
    check("retry: the first attempt keeps the identity ITS render reported",
          at[0]["renderer"] == fp, str(at[0]["renderer"])[:20])
    check("retry: the second reports ABSENT rather than inheriting it",
          at[1]["renderer"] is None, str(at[1]["renderer"])[:20])
    check("retry: the exported verification carries that absence",
          doc["verification"]["scene"]["renderer"] is None
          and "ABSENT" in doc["verification"]["scene"]["rendererIs"])
    check("retry: the plan and the worklist say the same thing, not a recomputed opinion",
          doc["scene"] == doc["verification"]["scene"] and wl["scene"] == doc["scene"])
    check("retry: and the exported pose id is the candidate that actually verified",
          doc["poseId"] == at[1]["poseId"] and doc["poseId"] != at[0]["poseId"])


def test_devbench_reads_a_worklist_in_either_spelling_or_refuses_by_name():
    """viewprobe reads a worklist's property names case-insensitively (_shared/Worklist.cs), so a PascalCase worklist
    (rendertwin and renderbatch write one) is a worklist. `devbench` must read it as the renderer does, or refuse it BY
    NAME - never 0 shots with exit 0, and never "not a worklist" for one the renderer renders."""
    def run(doc, *extra):
        d = tempfile.mkdtemp(prefix="sp-wl-")
        p = os.path.join(d, "w.json")
        with open(p, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        out = io.StringIO()
        try:
            with contextlib.redirect_stdout(out):
                code = S.main(["devbench", p, "--json", *extra])
            return "ran", code, out.getvalue()
        except SystemExit as e:
            return "refused", e.code, str(e.code)
        except Exception as e:  # noqa: BLE001 - a crash is an outcome under test, not a harness failure
            return "crashed", None, "%s: %s" % (type(e).__name__, e)

    def shots(r):
        try:
            return [s["label"] for s in json.loads(r[2])] if r[0] == "ran" else None
        except ValueError:
            return None

    lower = {"label": "x", "cells": [{"cell": "A", "shots": [
        {"label": "one", "eye": [0, 0, 100], "yaw": 90}, {"label": "two", "eye": [0, 0, 100], "look": [0, 100, 100]}]}]}
    pascal = {"Label": "x", "Cells": [{"Cell": "A", "Shots": [
        {"Label": "one", "Eye": [0, 0, 100], "Yaw": 90}, {"Label": "two", "Eye": [0, 0, 100], "Look": [0, 100, 100]}]}]}
    r = run(lower)
    check("SP-W1 control: a camelCase worklist gives its 2 shots", shots(r) == ["one", "two"], str(r)[:160])
    r = run(pascal)
    check("SP-W2 RED: a PascalCase worklist gives the same 2 shots, not 'not a worklist'",
          shots(r) == ["one", "two"], str(r)[:160])
    r = run({"cells": [{"cell": "A", "Shots": lower["cells"][0]["shots"]}]})
    check("SP-W3 RED: a cell spelling Shots inside a camelCase file gives its shots, not 0",
          shots(r) == ["one", "two"], str(r)[:160])
    # Each refusal fixture carries a renderable shot elsewhere, so the 0-shots rule cannot answer for it.
    r = run({"cells": [lower["cells"][0], {"cell": "B", "views": []}]})
    check("SP-W4 RED: a cell with no shots list is refused, naming 'shots' and the cell",
          r[0] == "refused" and "cell 2 (B) has no 'shots' list" in str(r[2]), str(r)[:160])
    r = run({"cells": lower["cells"], "Cells": pascal["Cells"]})
    check("SP-W5 RED: one key spelled two ways is refused as ambiguous, naming both",
          r[0] == "refused" and "Cells" in str(r[2]) and "cells" in str(r[2]), str(r)[:160])
    r = run({"label": "x", "status": "refused", "cells": []})
    check("SP-W6 RED: a worklist with 0 shots (a shotplan refusal record) is refused by name, not 0 lines and exit 0",
          r[0] == "refused" and "0 shots" in str(r[2]), str(r)[:160])


# --------------------------------------------------------------------------------------
# 3. assets the renderer could not draw (--allow-unreadable-assets) and nodepth subjects
# --------------------------------------------------------------------------------------
#
# The eye is EYE (0,-300,120) and the subject stands at the origin, aimed at z 100, so the one
# sightline runs from (0,-300,120) to (0,0,100). A hole is declared as viewprobe's census states
# it: a mesh, where each reference that uses it is placed, and its bounding radius there.

def hole(x, y, z, r, kind="mesh", asset="meshes\\test\\hole.nif", fk="00AB01:Test.esp"):
    return {"kind": kind, "asset": asset, "reason": "found, and it parses with 1 drawable shape(s), "
            "but the renderer produced no geometry from it", "at": [[fk, x, y, z, r]]}


def unread_check(holes, *flags, env=False, not_drawn=None):
    scene = dict(room(PLINTH), unreadable=holes, unreadableEnvOptOut=env)
    if not_drawn:
        scene["notDrawnByRule"] = not_drawn
    return check_cli(scene, "--subject", "0,0=s", *flags)


def hole_rows(doc):
    # A refused run leaves the path holding its claim stub, not a verification: that is a FAIL
    # of the checks that read it, never a crash that hides which check it was.
    return ((doc.get("verification") or {}).get("incomplete") or {}).get("assets", [])


def exit_message(argv, scene):
    """The SystemExit text shotplan raised, which run_cli folds into exit code 1."""
    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(scene, f)
    os.environ["FAKE_VIEWPROBE_SCENE"] = path
    try:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            S.main(argv)
        return ""
    except SystemExit as e:
        return str(e.code)
    finally:
        os.unlink(path)


def test_exit_3_still_stops_the_planner_without_the_flag():
    """The default is unchanged: viewprobe's exit 3 ends the plan, and the reason is printed."""
    scene = dict(room(PLINTH), unreadable=[hole(700, 700, 0, 30)])
    code, out = run_cli(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                         "--floor-z", "0", "--subject", "0,0=s"], scene)
    check("unreadable: without the flag exit 3 stops the plan", code != 0, f"exit {code}")
    msg = exit_message(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                        "--floor-z", "0", "--subject", "0,0=s"], scene)
    check("unreadable: and says viewprobe exited 3", msg.startswith("viewprobe exited 3"), msg[:80])


def test_a_hole_far_from_the_sightline_is_named_and_accepted():
    d = tempfile.mkdtemp()
    trace = os.path.join(d, "trace.txt")
    os.environ["FAKE_VIEWPROBE_TRACE"] = trace
    try:
        code, out, doc = unread_check([hole(700, 700, 0, 30)], "--allow-unreadable-assets")
    finally:
        os.environ.pop("FAKE_VIEWPROBE_TRACE", None)
    calls = [l for l in open(trace, encoding="utf-8").read().splitlines() if l.strip()]
    rows = hole_rows(doc)
    check("unreadable: a far hole is accepted", code == 0 and doc.get("status") == "verified",
          f"exit {code} {doc.get('status')}")
    check("unreadable: it is named, with its verdict", len(rows) == 1 and rows[0]["verdict"] == "away"
          and rows[0]["asset"] == "meshes\\test\\hole.nif", json.dumps(rows)[:160])
    check("unreadable: the placement and its measured gaps are carried",
          rows and rows[0]["placements"][0]["formkey"] == "00AB01:Test.esp"
          and rows[0]["placements"][0]["gapToSightline"] > 128, json.dumps(rows)[:200])
    check("unreadable: every viewprobe call carried the opt-out",
          calls and all("--allow-unreadable-assets" in c for c in calls), f"{len(calls)} call(s)")


def test_a_hole_named_with_spaces_is_still_placed():
    """Real census lines: a plugin name with spaces, a folder with " - " in it."""
    h = hole(0, -150, 110, 20, asset="meshes\\Some Mod - Parts\\hole.nif", fk="00AB02:Some Mod - Walls.esp")
    code, out, doc = unread_check([h], "--allow-unreadable-assets")
    rows = hole_rows(doc)
    check("unreadable: the asset path is read whole", rows and rows[0]["asset"] == "meshes\\Some Mod - Parts\\hole.nif",
          json.dumps(rows)[:160])
    check("unreadable: the spaced FormKey is placed, so the hole is judged by distance",
          rows and rows[0]["placements"] and rows[0]["placements"][0]["formkey"] == "00AB02:Some Mod - Walls.esp"
          and rows[0]["verdict"] == "refused" and "margin" in rows[0]["why"], json.dumps(rows)[:220])


def test_a_hole_on_the_sightline_is_refused():
    code, out, doc = unread_check([hole(0, -150, 110, 20)], "--allow-unreadable-assets")
    rows = hole_rows(doc)
    check("unreadable: a hole across the sightline refuses the camera", code == 1, f"exit {code}")
    check("unreadable: the target itself still reads clear -- the probe cannot see the hole",
          status_of(doc, "s") == "clear", status_of(doc, "s"))
    check("unreadable: the refusal names the hole", rows and rows[0]["verdict"] == "refused"
          and "unreadable-assets" in doc["verification"]["failed"], json.dumps(rows)[:160])


def test_a_hole_beside_the_eye_is_refused():
    code, out, doc = unread_check([hole(60, -300, 120, 10)], "--allow-unreadable-assets")
    rows = hole_rows(doc)
    check("unreadable: a hole 50 u from the eye, off the sightline, refuses", code == 1
          and rows and rows[0]["verdict"] == "refused", json.dumps(rows)[:160])
    check("unreadable: its gap to the eye is what refused it",
          rows and rows[0]["placements"][0]["gapToEye"] < 128, json.dumps(rows)[:200])


def test_a_hole_that_cannot_be_placed_is_refused():
    code, out, doc = unread_check([hole(700, 700, 0, None)], "--allow-unreadable-assets")
    r1 = hole_rows(doc)
    check("unreadable: an unmeasured radius is refused, not assumed small",
          code == 1 and r1 and r1[0]["verdict"] == "refused" and "not measured" in r1[0]["why"],
          json.dumps(r1)[:160])
    arc = {"kind": "archive", "asset": "meshes\\test\\lost.nif", "reason": "extraction failed", "at": []}
    code, out, doc = unread_check([arc], "--allow-unreadable-assets")
    r2 = hole_rows(doc)
    check("unreadable: a hole with no placement is refused, not assumed far",
          code == 1 and r2 and r2[0]["verdict"] == "refused" and "no placement" in r2[0]["why"],
          json.dumps(r2)[:160])


def test_an_unreadable_texture_is_named_not_judged():
    tex = {"kind": "texture", "asset": "textures\\test\\bad.dds", "reason": "found on disk and the "
           "decoder would not read it", "at": []}
    code, out, doc = unread_check([tex], "--allow-unreadable-assets")
    rows = hole_rows(doc)
    check("unreadable: a texture leaves its geometry drawn, so the camera is accepted",
          code == 0 and rows and rows[0]["verdict"] == "named", json.dumps(rows)[:160])


def test_an_ambient_opt_out_does_not_reach_a_verified_pose():
    scene = dict(room(PLINTH), unreadable=[hole(700, 700, 0, 30)], unreadableEnvOptOut=True)
    code, out = run_cli(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                         "--floor-z", "0", "--subject", "0,0=s"], scene)
    msg = exit_message(["check", "TestCell", "--eye", EYE, "--vp", FAKE, "--quiet",
                        "--floor-z", "0", "--subject", "0,0=s"], scene)
    check("unreadable: exit 0 from an opt-out the planner did not give is refused",
          code != 0 and "VP_ALLOW_UNREADABLE_ASSETS" in msg, msg[:120])


def test_not_drawn_by_rule_is_listed_and_never_fails():
    code, out, doc = unread_check([], not_drawn=["meshes\\water\\water2048x1024.nif"])
    check("not drawn by rule: the camera is accepted without any flag", code == 0, f"exit {code}")
    check("not drawn by rule: the mesh is listed", doc["verification"].get("notDrawnByRule")
          == ["meshes\\water\\water2048x1024.nif"], str(doc["verification"].get("notDrawnByRule")))
    code, out, doc = unread_check([])
    check("not drawn by rule: a clean scene's verification carries neither key",
          "notDrawnByRule" not in doc["verification"] and "incomplete" not in doc["verification"],
          ",".join(doc["verification"]))


def test_an_incomplete_plan_labels_its_shot():
    wl = os.path.join(tempfile.mkdtemp(), "wl.json")
    jp = os.path.join(tempfile.mkdtemp(), "plan.json")
    scene = dict(room(PLINTH), unreadable=[hole(760, 760, 0, 20)])
    code, out = run_cli(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE, "--quiet",
                         "--json", jp, "--worklist", wl, "--allow-unreadable-assets"], scene)
    doc = json.load(open(jp, encoding="utf-8"))
    shot = json.load(open(wl, encoding="utf-8"))["cells"][0]["shots"][0]
    check("incomplete plan: it verifies", code == 0 and doc["status"] == "verified",
          f"exit {code} {doc['status']}")
    check("incomplete plan: the frame's own label says what it lacks",
          shot["label"].startswith("INCOMPLETE (not drawn: hole.nif) -- "), shot["label"])
    check("incomplete plan: the shot carries the full list",
          shot["incomplete"]["assets"][0]["asset"] == "meshes\\test\\hole.nif")
    check("incomplete plan: the render instruction carries the opt-out",
          "--worklist " + wl + " --allow-unreadable-assets" in out)


def test_nodepth_subject_with_nothing_opaque_before_it_is_framed():
    # An alpha plinth on a floorless scene: the ray to the subject meets nothing that writes depth.
    scene = {"boxes": [PLINTH_ALPHA], "nav": [], "navSilent": [], "insideSilent": []}
    code, out, doc = check_cli(scene, "--subject-nodepth", "0,0=lux")
    t = [x for x in doc["verification"]["targets"] if x["label"] == "lux"][0]
    check("nodepth: no depth hit reads FRAMED", t["status"] == "framed", t["status"])
    check("nodepth: the reason disclaims the subject's own surface", "NOT established" in t["why"], t["why"])
    code, out, doc = check_cli(scene, "--subject", "0,0=lux")
    check("nodepth: the SAME scene as a plain --subject stays UNPROVEN",
          status_of(doc, "lux") == "unproven", status_of(doc, "lux"))


def test_nodepth_subject_behind_geometry_is_blocked():
    screen = {"id": "000501:Test.esp", "edid": "Screen", "min": [-120, -210, 0], "max": [120, -150, 200]}
    code, out, doc = check_cli(room(PLINTH, screen), "--subject-nodepth", "0,0=lux")
    check("nodepth: a nearer opaque hit is BLOCKED", status_of(doc, "lux") == "blocked", status_of(doc, "lux"))
    check("nodepth: and the camera is refused", code == 1, f"exit {code}")


def test_nodepth_subject_in_the_open_is_framed_and_accepted():
    code, out, doc = check_cli(room(PLINTH), "--subject-nodepth", "0,0=lux")
    check("nodepth: a ray running past it reads FRAMED", status_of(doc, "lux") == "framed", status_of(doc, "lux"))
    check("nodepth: and the camera is accepted", code == 0, f"exit {code}")
    check("nodepth: it is framed as a standing subject", doc["subjects"][0]["kind"] == "nodepth")


def test_a_candidate_without_a_wall_margin_prints():
    line = S.candidate_line({"score": 7.25, "_bearing": 12.0, "_standoff": 250.0, "fill": 0.31, "wall_margin": None,
                             "nav": True, "inside": False})
    check("a candidate with no wall in reach prints its line", "wall margin none u" in line, line)


ALL = [test_a_candidate_without_a_wall_margin_prints, test_devbench_reads_a_worklist_in_either_spelling_or_refuses_by_name,
       test_failed_verification_cannot_export_success,
       test_pose_identity_binds_the_result,
       test_skipped_verification_is_not_a_pass,
       test_verified_run_publishes_cleanly,
       test_emit_refused_marks_the_file,
       test_missing_placement_evidence_refuses,
       test_off_mesh_over_a_floor_is_still_accepted,
       test_off_mesh_over_nothing_is_refused,
       test_off_mesh_over_another_storey_is_refused,
       test_incomplete_obnd_coverage_is_unresolved_not_clear,
       test_unreadable_meshes_are_unresolved_not_clear,
       test_missing_terrain_outdoors_is_unresolved_not_contradicted,
       test_terrain_gap_at_this_xy_is_unresolved,
       test_interior_absence_is_still_contradicted,
       test_silent_clear_at_is_refused,
       test_unreadable_room_scale_mesh_is_unresolved_not_clear,
       test_unreadable_small_box_still_refuses,
       test_misleading_obnd_does_not_hide_the_mesh,
       test_a_ref_selected_only_by_its_record_box_is_a_coverage_caveat,
       test_incomplete_nomination_is_not_clearance,
       test_nomination_by_unverified_record_box_is_not_clearance,
       test_a_complete_empty_nomination_is_still_clear,
       test_missing_coverage_statement_is_not_clearance,
       test_legacy_comparison_output_cannot_qualify_a_camera,
       test_incomplete_nomination_prevents_publication,
       test_support_does_not_override_the_other_placement_arms,
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
       test_surface_map_ranks_but_never_refuses,
       test_a_failed_rerun_cannot_leave_a_verified_artifact_behind,
       test_an_early_refusal_cannot_leave_a_verified_artifact_behind,
       test_skipped_verification_clears_the_final_boolean,
       test_projection_uses_the_pose_that_is_actually_submitted,
       test_pose_identity_binds_the_scene_configuration,
       test_an_input_refusal_cannot_leave_a_verified_artifact_behind,
       test_the_renderer_fingerprint_binds_when_viewprobe_prints_one,
       test_every_command_that_names_a_path_claims_it,
       test_the_planner_asks_the_renderer_for_its_configuration,
       test_a_later_candidate_cannot_inherit_an_earlier_identity,
       test_exit_3_still_stops_the_planner_without_the_flag,
       test_a_hole_far_from_the_sightline_is_named_and_accepted,
       test_a_hole_named_with_spaces_is_still_placed,
       test_a_hole_on_the_sightline_is_refused,
       test_a_hole_beside_the_eye_is_refused,
       test_a_hole_that_cannot_be_placed_is_refused,
       test_an_unreadable_texture_is_named_not_judged,
       test_an_ambient_opt_out_does_not_reach_a_verified_pose,
       test_not_drawn_by_rule_is_listed_and_never_fails,
       test_an_incomplete_plan_labels_its_shot,
       test_nodepth_subject_with_nothing_opaque_before_it_is_framed,
       test_nodepth_subject_behind_geometry_is_blocked,
       test_nodepth_subject_in_the_open_is_framed_and_accepted]


if __name__ == "__main__":
    for fn in ALL:
        print(f"\n{fn.__name__}")
        fn()
    print("\n" + ("ALL PASS" if not FAILS else f"{len(FAILS)} FAILED: " + ", ".join(FAILS)))
    sys.exit(1 if FAILS else 0)
