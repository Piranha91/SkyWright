"""Offline checks for shotplan's top-down FALLBACK: what a refused plan leaves behind.

    python test_shotplan_fallback.py

When every eye-level camera is refused -- at sightline + framing, at the placement test, or at the
final verification -- `shotplan.py plan` emits, by default, a sliced top-down shot of the subjects
instead of no shot. The refusal stays the answer: exit 1, `status: refused`, the reasons recorded.
The shot is labelled as a fallback and is never a verified eye-level view.

Same harness as `test_shotplan_plan.py`: the real `main()`, the real `Probe` subprocess and the real
writers, with `fake_viewprobe.py` answering from a declared toy scene. The scenes are synthetic and
the coordinates made up; each case is a KNOWN ANSWER whose verdict is arithmetic a reader can redo,
and each names the wrong answer it exists to reject.
"""
from __future__ import annotations

import json
import math
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import test_shotplan_plan as P  # noqa: E402  (the shared harness: run_cli, FAKE, room, check)

FAKE = P.FAKE
check = P.check

CURB = [{"id": "000401:Test.esp", "edid": "CurbN", "min": [-120, 100, 0], "max": [120, 120, 110]},
        {"id": "000402:Test.esp", "edid": "CurbS", "min": [-120, -120, 0], "max": [120, -100, 110]},
        {"id": "000403:Test.esp", "edid": "CurbE", "min": [100, -120, 0], "max": [120, 120, 110]},
        {"id": "000404:Test.esp", "edid": "CurbW", "min": [-120, -120, 0], "max": [-100, 120, 110]}]
UPPER = [{"min": [-790, -790], "max": [790, 790], "z": 2000.0, "name": "Test.esm"}]

# Defaults the design fixes (PLAN-5 §2.3): cut = highest subject + --subject-height (128) + 64;
# frame width W = max(2048, 2 x largest offset from the centroid + 1024) at the floor plane;
# eye z = floor + (W/2) / tan(fov/2); ring half-size = max(--subject-width (40), W/50).
DEFAULT_CUT_ABOVE_FEET = 128.0 + 64.0
FOV = 65.0


def placement_scene():
    """Eye-level refused at the PLACEMENT test: the only navmesh is a storey 2,000 u up -- the shape
    of a real refused cluster whose subject sits far below the rebuilt floors."""
    return dict(P.room(P.PLINTH), nav=list(UPPER))


def sightline_scene():
    """Eye-level refused at SIGHTLINE + FRAMING: nothing at all, so no probe ray is bounded."""
    return {"floorZ": None, "boxes": [], "nav": []}


def verification_scene(nav_flags=0):
    """Eye-level refused at the FINAL VERIFICATION: a curb ring hides the subject's feet."""
    sc = P.room(*CURB)
    for n in sc["nav"]:
        n["flags"] = nav_flags
    return sc


def plan(scene, *extra, subjects=("0,0,0=target",), trace=False):
    """Run `plan`; return (exit, stdout, worklist doc, json doc, fake trace lines)."""
    d = tempfile.mkdtemp()
    wl, jp = os.path.join(d, "wl.json"), os.path.join(d, "plan.json")
    tr = os.path.join(d, "trace.txt")
    argv = ["plan", "TestCell"]
    for s in subjects:
        argv.append(f"--subject={s}")   # '=' form: a negative X would read as a flag
    argv += ["--vp", FAKE, "--quiet", "--worklist", wl, "--json", jp, *extra]
    if trace:
        os.environ["FAKE_VIEWPROBE_TRACE"] = tr
    try:
        code, out = P.run_cli(argv, scene)
    finally:
        os.environ.pop("FAKE_VIEWPROBE_TRACE", None)
    w = json.load(open(wl, encoding="utf-8")) if os.path.exists(wl) else {}
    j = json.load(open(jp, encoding="utf-8")) if os.path.exists(jp) else {}
    lines = open(tr, encoding="utf-8").read().splitlines() if os.path.exists(tr) else []
    return code, out, w, j, lines


def shots(w):
    return [s for c in (w.get("cells") or []) for s in (c.get("shots") or [])]


def flag_value(flags, name):
    flags = list(flags or [])
    return flags[flags.index(name) + 1] if name in flags else None


def common_fallback_checks(tag, code, out, w, j, stage):
    """What every fallback must say, whatever the stage."""
    sh = shots(w)
    check(f"{tag}: the exit code still refuses (1)", code == 1, f"exit {code}")
    check(f"{tag}: the worklist's status is still refused", w.get("status") == "refused", str(w.get("status")))
    check(f"{tag}: the worklist says fallback top-down", w.get("fallback") == "top-down", str(w.get("fallback")))
    check(f"{tag}: the worklist holds exactly one shot", len(sh) == 1, f"{len(sh)} shot(s)")
    s = sh[0] if sh else {}
    check(f"{tag}: the shot's status is fallback, never verified", s.get("status") == "fallback", str(s.get("status")))
    check(f"{tag}: the shot's label says FALLBACK TOP-DOWN and not eye-level",
          str(s.get("label", "")).startswith("FALLBACK TOP-DOWN") and "not an eye-level view" in str(s.get("label", "")),
          str(s.get("label")))
    check(f"{tag}: the shot looks down at viewprobe's steepest legal pitch, north-up",
          s.get("pitch") == 89 and s.get("yaw") == 0,
          f"pitch {s.get('pitch')} yaw {s.get('yaw')}")
    check(f"{tag}: the shot carries no placement-tested feet", "feet" not in s, str(s.get("feet")))
    check(f"{tag}: the refusal stage is recorded", (w.get("refusal") or {}).get("stage") == stage,
          str((w.get("refusal") or {}).get("stage")))
    check(f"{tag}: a geometric refusal is not called evidence-incomplete",
          (w.get("refusal") or {}).get("kind") == "geometry" and not (w.get("refusal") or {}).get("causes"),
          str((w.get("refusal") or {}).get("kind")))
    check(f"{tag}: the refusal reasons are recorded", bool((w.get("refusal") or {}).get("reasons")),
          str((w.get("refusal") or {}).get("reasons"))[:120])
    check(f"{tag}: the plan JSON says refused with the same stage",
          j.get("status") == "refused" and j.get("refusedAt") == stage, f"{j.get('status')} {j.get('refusedAt')}")
    check(f"{tag}: --grid auto is a render flag", flag_value(w.get("renderFlags"), "--grid") == "auto",
          str(w.get("renderFlags")))
    check(f"{tag}: stdout announces the fallback", "=== FALLBACK: sliced top-down" in out)
    return s


# --------------------------------------------------------------------------------------
# F1-F3: one per refusal point
# --------------------------------------------------------------------------------------

def test_F1_placement_refusal_gets_a_top_down():
    """Rejects: only the start-of-run stub; a verified/eye-level label; --nav-z on another storey."""
    code, out, w, j, _ = plan(placement_scene())
    s = common_fallback_checks("F1 placement", code, out, w, j, "placement")
    e = s.get("eye") or [None, None, None]
    axis_y = None if e[1] is None else e[1] + (e[2] - 0.0) / math.tan(math.radians(89.0))
    check("F1 placement: the view axis meets the floor at the subject",
          e[0] == 0.0 and axis_y is not None and abs(axis_y) < 0.15, f"eye {e}, axis meets y {axis_y}")
    check("F1 placement: the cut is the default above the subject's feet",
          s.get("cullAbove") == DEFAULT_CUT_ABOVE_FEET, str(s.get("cullAbove")))
    check("F1 placement: the cut's basis is recorded as the default",
          str(s.get("cullAboveBasis", "")).startswith("default"), str(s.get("cullAboveBasis")))
    check("F1 placement: floorZ is the subject's floor", s.get("floorZ") == 0.0, str(s.get("floorZ")))
    check("F1 placement: no navmesh on the subject's storey, so the overlay is off", s.get("nav") == "off",
          str(s.get("nav")))
    check("F1 placement: no --nav-z for another storey", "--nav-z" not in (w.get("renderFlags") or []),
          str(w.get("renderFlags")))
    check("F1 placement: the storey that does exist is named",
          (w.get("fallbackDetail") or {}).get("storeysSampled") == [2000.0],
          str((w.get("fallbackDetail") or {}).get("storeysSampled")))
    want_eye_z = 0.0 + (2048 / 2.0) / math.tan(math.radians(FOV) / 2.0)
    got = (s.get("eye") or [0, 0, None])[2]
    check("F1 placement: eye height frames 2,048 u at the floor", got is not None and abs(got - want_eye_z) < 0.11,
          f"{got} vs {want_eye_z:.1f}")


def test_F2_sightline_refusal_gets_a_top_down():
    """Rejects: the stub; a fallback that needs a navmesh or a bounded bearing to exist."""
    code, out, w, j, _ = plan(sightline_scene())
    s = common_fallback_checks("F2 sightline", code, out, w, j, "sightline+framing")
    check("F2 sightline: no storey sampled is said, not invented",
          (w.get("fallbackDetail") or {}).get("storeysSampled") == [], str((w.get("fallbackDetail") or {}).get("storeysSampled")))
    check("F2 sightline: overlay off", s.get("nav") == "off", str(s.get("nav")))
    check("F2 sightline: the base message is still printed",
          "No camera survives sightline and framing" in out)


def test_F3_verification_refusal_keeps_the_refused_record():
    """Rejects: the fallback replacing or dropping the refused eye-level camera's record."""
    code, out, w, j, _ = plan(verification_scene())
    s = common_fallback_checks("F3 verification", code, out, w, j, "verification")
    ref = w.get("refusal") or {}
    check("F3 verification: the refused eye-level pose id is kept",
          isinstance(ref.get("poseId"), str) and ref.get("poseId") != s.get("poseId"),
          f"{ref.get('poseId')} vs shot {s.get('poseId')}")
    check("F3 verification: the refused verification record is kept",
          (ref.get("verification") or {}).get("status") == "failed", str((ref.get("verification") or {}).get("status")))
    check("F3 verification: the JSON keeps its verification block",
          (j.get("verification") or {}).get("status") == "failed", str((j.get("verification") or {}).get("status")))
    check("F3 verification: the worklist's top-level pose id is the shot's",
          w.get("poseId") == s.get("poseId"), f"{w.get('poseId')} vs {s.get('poseId')}")


# --------------------------------------------------------------------------------------
# F4-F5: the navmesh band
# --------------------------------------------------------------------------------------

def test_F4_band_on_the_subjects_floor_is_used():
    """Rejects: a missing band, or a band at the wrong height."""
    code, out, w, j, _ = plan(verification_scene())
    s = shots(w)[0] if shots(w) else {}
    check("F4 band: --nav-z is the subject's floor with the default tolerance",
          flag_value(w.get("renderFlags"), "--nav-z") == "0,64", str(w.get("renderFlags")))
    check("F4 band: the shot draws the overlay as wire", s.get("nav") == "wire", str(s.get("nav")))


def test_F5_a_deleted_triangle_is_not_a_band():
    """Rejects: counting a 0x08 triangle that viewprobe's --nav-z drops (the render would refuse)."""
    code, out, w, j, _ = plan(verification_scene(nav_flags=8))
    s = shots(w)[0] if shots(w) else {}
    check("F5 deleted: the fallback was emitted", w.get("fallback") == "top-down", str(w.get("fallback")))
    check("F5 deleted: no --nav-z", "--nav-z" not in (w.get("renderFlags") or []), str(w.get("renderFlags")))
    check("F5 deleted: overlay off", s.get("nav") == "off", str(s.get("nav")))


# --------------------------------------------------------------------------------------
# F6: the cut, through the probe
# --------------------------------------------------------------------------------------

def from_above(w, label="target"):
    for r in ((w.get("fallbackDetail") or {}).get("fromAbove") or []):
        if r.get("label") == label:
            return r.get("status")
    return "<absent>"


def test_F6_the_cut_reaches_the_probe():
    """Rejects: --cull-above not passed to the fallback probe, or a cut above the ceiling."""
    slab = {"id": "000501:Test.esp", "edid": "UpperSlab", "min": [-400, -400, 300], "max": [400, 400, 320]}
    sc = dict(P.room(P.PLINTH, slab), nav=list(UPPER))
    code, out, w, j, tr = plan(sc, trace=True)
    probe_lines = [l for l in tr if "--pitch 89" in l]
    check("F6 cut: the fallback probe looked straight down", bool(probe_lines), f"{len(probe_lines)} call(s)")
    check("F6 cut: and carried --cull-above at the default cut",
          any(f"--cull-above {DEFAULT_CUT_ABOVE_FEET:g}" in l for l in probe_lines),
          probe_lines[0][:160] if probe_lines else "")
    check("F6 cut: the slab above the cut does not hide the subject from above",
          from_above(w) == "clear", from_above(w))
    # a canopy SPANNING the cut stays whole, as viewprobe keeps a spanning wall
    canopy = {"id": "000502:Test.esp", "edid": "Canopy", "min": [-80, -80, 150], "max": [80, 80, 400]}
    sc2 = dict(P.room(P.PLINTH, canopy), nav=list(UPPER))
    code2, out2, w2, j2, _ = plan(sc2)
    check("F6 cut: a canopy spanning the cut still covers the subject", from_above(w2) == "blocked", from_above(w2))
    code3, out3, w3, j3, tr3 = plan(sc, "--fallback-cut", "500", trace=True)
    s3 = shots(w3)[0] if shots(w3) else {}
    check("F6 cut: --fallback-cut sets the cut", s3.get("cullAbove") == 500.0, str(s3.get("cullAbove")))
    check("F6 cut: and its basis says so", s3.get("cullAboveBasis") == "--fallback-cut", str(s3.get("cullAboveBasis")))
    check("F6 cut: a cut above the slab leaves the subject covered", from_above(w3) == "blocked", from_above(w3))


# --------------------------------------------------------------------------------------
# F7-F8: marking and extent
# --------------------------------------------------------------------------------------

def rings(s):
    return [r for r in (s.get("routes") or []) if str(r.get("caption", "")).startswith("SUBJECT ")]


def test_F7_each_subject_is_ringed_where_it_is():
    """Rejects: a ring at the aim height, or one ring at the centroid for several subjects."""
    code, out, w, j, _ = plan(placement_scene())
    s = shots(w)[0] if shots(w) else {}
    rs = rings(s)
    check("F7 mark: one ring for one subject", len(rs) == 1, f"{len(rs)} ring(s)")
    if rs:
        pts = rs[0].get("points") or []
        half = max(40.0, 2048 / 50.0)
        check("F7 mark: a closed ring of 5 points", len(pts) == 5 and pts[0] == pts[-1], str(pts))
        check("F7 mark: at the subject's own z", all(len(p) == 3 and p[2] == 0.0 for p in pts), str(pts))
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        check("F7 mark: centred on the subject with the stated half-size",
              abs(min(xs) + half) < 0.05 and abs(max(xs) - half) < 0.05
              and abs(min(ys) + half) < 0.05 and abs(max(ys) - half) < 0.05, f"x {min(xs)}..{max(xs)} half {half}")
    px = (w.get("fallbackDetail") or {}).get("subjectPixels") or []
    check("F7 mark: the subject's pixel is recorded and inside the frame",
          len(px) == 1 and px[0].get("inFrame") is True, str(px))
    code2, out2, w2, j2, _ = plan(sightline_scene(), subjects=("-300,0,0=a", "300,0,0=b"))
    s2 = shots(w2)[0] if shots(w2) else {}
    rs2 = rings(s2)
    centres = sorted(round(sum(p[0] for p in r["points"][:4]) / 4.0, 1) for r in rs2)
    check("F7 mark: two subjects, two rings, each on its subject", centres == [-300.0, 300.0], str(centres))


def test_F8_extent_grows_to_hold_every_subject():
    """Rejects: a fixed 2,048 u extent that crops a subject."""
    code, out, w, j, _ = plan(sightline_scene(), subjects=("-1500,0,0=a", "1500,0,0=b"))
    det = w.get("fallbackDetail") or {}
    check("F8 extent: the frame widens to the subjects plus margin", (det.get("extent") or 0) >= 3000 + 1024,
          str(det.get("extent")))
    px = det.get("subjectPixels") or []
    check("F8 extent: both subjects are inside the delivered frame",
          len(px) == 2 and all(p.get("inFrame") is True for p in px), str(px))


# --------------------------------------------------------------------------------------
# F9-F12: opt-out, success path, devbench, failed probe
# --------------------------------------------------------------------------------------

def test_F9_opt_out_restores_the_refusal_artifacts():
    """Rejects: a fallback emitted despite --no-fallback."""
    for tag, sc, wl_status in (("sightline", sightline_scene(), "incomplete"),
                                ("placement", placement_scene(), "incomplete"),
                                ("verification", verification_scene(), "refused")):
        code, out, w, j, _ = plan(sc, "--no-fallback")
        check(f"F9 --no-fallback {tag}: exit 1", code == 1, f"exit {code}")
        check(f"F9 --no-fallback {tag}: no fallback", "fallback" not in w and not shots(w), json.dumps(w)[:120])
        check(f"F9 --no-fallback {tag}: the worklist is what the base leaves", w.get("status") == wl_status,
              str(w.get("status")))


def test_F10_a_verified_plan_carries_no_fallback_fields():
    """Rejects: any fallback key leaking onto the success path. (The byte-identity of the success
    path against the base is measured separately, by digest, outside this suite.)"""
    code, out, w, j, _ = plan(P.room(P.PLINTH))
    check("F10 success: still verified, exit 0", code == 0 and j.get("status") == "verified", f"exit {code}")
    leaked = [k for k in ("fallback", "fallbackDetail", "refusal", "renderFlags") if k in w]
    leaked += [k for k in ("fallback", "fallbackDetail", "refusal", "refusedAt") if k in j]
    check("F10 success: no fallback key in the worklist or the JSON", not leaked, str(leaked))
    check("F10 success: no FALLBACK banner", "FALLBACK" not in out)


def test_F11_devbench_refuses_a_fallback_shot():
    """Rejects: a camera-only top-down shot turned into a DevBench script (a player sent to a void)."""
    # A worklist written by hand in the fallback's spelling, so this case tests the converter alone
    # (and is red on a converter that would drive it, whatever the planner does).
    w = {"label": "fixture", "status": "refused", "fallback": "top-down",
         "cells": [{"cell": "TestCell", "shots": [{
             "label": "FALLBACK TOP-DOWN (eye-level refused at placement; not an eye-level view) -- t",
             "status": "fallback", "fallback": "top-down", "eye": [0.0, 0.0, 1607.4],
             "yaw": 0, "pitch": 89, "fov": 65.0, "floorZ": 0.0, "cullAbove": 192.0, "nav": "off"}]}]}
    d = tempfile.mkdtemp()
    wl = os.path.join(d, "wl.json")
    with open(wl, "w", encoding="utf-8") as f:
        json.dump(w, f)
    import contextlib
    import io
    buf, msg = io.StringIO(), ""
    with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(io.StringIO()):
        try:
            code2 = P.S.main(["devbench", wl, "--json"])
        except SystemExit as e:
            code2, msg = (e.code, "") if isinstance(e.code, int) else (1, str(e.code))
    out2 = buf.getvalue()
    check("F11 devbench: refuses (non-zero)", code2 not in (0, None), f"exit {code2}")
    check("F11 devbench: and the refusal names the fallback shot", "fallback" in msg.lower(), msg[-160:] or "(no refusal message)")
    check("F11 devbench: no drive command is printed", '"action": "drive"' not in out2)


def test_F12_a_failed_fallback_probe_still_leaves_the_shot():
    """Rejects: a crash that leaves only the stub, or a shot that hides the failed probe."""
    sc = placement_scene()
    sc["failOnCullAbove"] = True
    code, out, w, j, _ = plan(sc)
    s = shots(w)[0] if shots(w) else {}
    probe = (w.get("fallbackDetail") or {}).get("probe") or {}
    check("F12 failed probe: exit 1", code == 1, f"exit {code}")
    check("F12 failed probe: the shot is still written", s.get("status") == "fallback", str(s.get("status")))
    check("F12 failed probe: with the overlay off and no band", s.get("nav") == "off"
          and "--nav-z" not in (w.get("renderFlags") or []), f"{s.get('nav')} {w.get('renderFlags')}")
    check("F12 failed probe: the failure is recorded", probe.get("status") == "failed" and bool(probe.get("why")),
          str(probe))


ALL = [test_F1_placement_refusal_gets_a_top_down,
       test_F2_sightline_refusal_gets_a_top_down,
       test_F3_verification_refusal_keeps_the_refused_record,
       test_F4_band_on_the_subjects_floor_is_used,
       test_F5_a_deleted_triangle_is_not_a_band,
       test_F6_the_cut_reaches_the_probe,
       test_F7_each_subject_is_ringed_where_it_is,
       test_F8_extent_grows_to_hold_every_subject,
       test_F9_opt_out_restores_the_refusal_artifacts,
       test_F10_a_verified_plan_carries_no_fallback_fields,
       test_F11_devbench_refuses_a_fallback_shot,
       test_F12_a_failed_fallback_probe_still_leaves_the_shot]


if __name__ == "__main__":
    per = {}
    for fn in ALL:
        print(f"\n{fn.__name__}")
        before = len(P.FAILS)
        fn()
        per[fn.__name__] = len(P.FAILS) - before
    print("\nFAILS PER TEST " + json.dumps(per))
    print("\n" + ("ALL PASS" if not P.FAILS else f"{len(P.FAILS)} FAILED: " + ", ".join(P.FAILS)))
    sys.exit(1 if P.FAILS else 0)
