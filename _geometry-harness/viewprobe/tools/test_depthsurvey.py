#!/usr/bin/env python3
"""Arithmetic-only checks for depthsurvey.py's lookup math.

ARITHMETIC ONLY: these run on SYNTHETIC depth arrays encoded through the same projection they are
decoded with. They prove the lookup math -- the linearisation, the row order, the ray-vs-view
distinction, the pixel/angle correspondence, the erosion, the face selection and the verdicts --
and they prove NOTHING about the renderer. A near-plane blind radius, back-face culling, or a
basis that is wrong at pitch +-89 is invisible here by construction. Only the per-face
pick-vs-dump self-check in `depthsurvey.py survey`, which compares the dump with viewprobe's own
`--pick` answers on the SAME finished frame, can see those.

Run with `python -m pytest test_depthsurvey.py` or plain `python test_depthsurvey.py`.
"""

from __future__ import annotations

import importlib.util
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def _load():
    spec = importlib.util.spec_from_file_location("depthsurvey",
                                                  os.path.join(HERE, "depthsurvey.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


ds = _load()


# -- the shared battery ---------------------------------------------------------------------

def test_selftest_battery_passes():
    """The same checks `depthsurvey.py selftest` prints, asserted one per failure line."""
    checks = ds.run_checks()
    failed = [f"{name}: {detail}" for ok, name, detail in checks.rows if not ok]
    assert not failed, "\n".join(failed)
    assert len(checks.rows) > 40, "the battery should not have shrunk to nothing"


# -- the individual claims, spelled out -------------------------------------------------------

def test_window_view_round_trip_at_float32():
    for v in (16.0, 50.0, 250.0, 2000.0):
        w = struct.unpack("<f", struct.pack("<f", ds.view_to_window(v)))[0]
        assert abs(ds.window_to_view(w) - v) <= max(0.05, v * 1e-3)


def test_near_and_far_are_the_renderers_own():
    assert (ds.NEAR, ds.FAR) == (15.0, 131072.0)
    assert ds.BACKGROUND == 0.999999


def test_row_order_is_bottom_up():
    """Image row 0 is the LAST buffer row. A dump read top-down mirrors the frame vertically,
    which is the error this fixes: the encode writes through set_view, the decode reads the
    buffer index directly."""
    f = ds.Face.synthetic("f", 0.0, 0.0, 90.0, 8, (0.0, 0.0, 0.0), fill_view=1000.0)
    f.set_view(3, 0, 100.0)                      # top image row
    assert f.buf[(8 - 1 - 0) * 8 + 3] < 1.0      # ... is the last buffer row
    assert f.buf[0 * 8 + 3] >= ds.BACKGROUND or f.window_at(3, 7) != f.window_at(3, 0)
    assert f.ray_at(3, 0) < 200 and f.ray_at(3, 7) > 900


def test_ray_distance_exceeds_view_distance_off_axis():
    f = ds.Face.synthetic("f", 0.0, 0.0, 90.0, 64, (0.0, 0.0, 0.0), fill_view=250.0)
    centre = f.ray_at(32, 32)
    corner = f.ray_at(0, 0)
    assert abs(centre - 250.0) < 3.0             # within half a pixel of the axis
    assert corner > centre * 1.6                 # a 90 deg square face: sqrt(3) at the corner


def test_world_position_lies_on_the_pixel_ray():
    f = ds.Face.synthetic("f", 123.0, -31.0, 90.0, 64, (5.0, -7.0, 11.0), fill_view=300.0)
    for (ix, iy) in ds.selfcheck_pixels(64):
        w = f.world_at(ix, iy)
        assert abs(ds.vlen(ds.vsub(w, f.eye)) - f.ray_at(ix, iy)) < 0.02
        assert abs(ds.vdot(ds.vsub(w, f.eye), f.f) - 300.0) < 0.02


def test_background_is_infinite_not_zero():
    f = ds.Face.synthetic("f", 0.0, 0.0, 90.0, 16, (0.0, 0.0, 0.0))
    assert math.isinf(f.ray_at(8, 8))
    assert f.world_at(8, 8) is None
    assert math.isinf(f.ray_eroded(8, 8))


def test_erosion_takes_the_neighbourhood_minimum():
    f = ds.Face.synthetic("f", 0.0, 0.0, 90.0, 32, (0.0, 0.0, 0.0), fill_view=1000.0)
    f.set_view(10, 10, 100.0)
    assert f.ray_at(11, 11) > 900
    assert abs(f.ray_eroded(11, 11) - f.ray_at(10, 10)) < 1e-9
    assert f.ray_eroded(12, 12) > 900


def test_erosion_never_admits_more_than_no_erosion():
    """The direction of the erosion matters: it may only turn an admit into a discard."""
    f = ds.Face.synthetic("f", 0.0, 0.0, 90.0, 16, (0.0, 0.0, 0.0), fill_view=500.0)
    f.set_view(8, 8, 60.0)
    for iy in range(16):
        for ix in range(16):
            assert f.ray_eroded(ix, iy) <= f.ray_at(ix, iy) + 1e-9


def test_face_selection_and_the_boundary():
    sv = ds.Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), ds.face_plan(0.0, 64, (0, 0, 0)))
    for bearing, want in ((0, "side-0"), (44, "side-0"), (46, "side-90"), (200, "side-180")):
        rb = math.radians(bearing)
        assert sv.select((math.sin(rb), math.cos(rb), 0.0))[0].name == want
    rb = math.radians(45.0)
    face, proj = sv.select((math.sin(rb), math.cos(rb), 0.0))
    assert proj["edgeClamped"] is True           # reported, not silently dropped
    off = ds.Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), ds.face_plan(45.0, 64, (0, 0, 0)))
    face2, proj2 = off.select((math.sin(rb), math.cos(rb), 0.0))
    assert face2.name == "side-45" and proj2["edgeClamped"] is False


def test_six_faces_cover_the_sphere():
    sv = ds.Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), ds.face_plan(0.0, 64, (0, 0, 0)))
    misses = []
    for b in range(0, 360, 1):
        for el in range(-89, 90, 1):
            rb, re_ = math.radians(b), math.radians(el)
            d = (math.sin(rb) * math.cos(re_), math.cos(rb) * math.cos(re_), math.sin(re_))
            if sv.select(d) is None:
                misses.append((b, el))
    assert not misses, f"{len(misses)} directions in no face, e.g. {misses[:5]}"


def test_verdicts():
    faces = ds.face_plan(0.0, 64, (0, 0, 0))
    for f in faces:
        f.buf = ds.array.array("f", [1.0] * (f.size * f.size))
    faces[0].fill_view(100.0)                    # a wall due north
    sv = ds.Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), faces)
    assert sv.lookup((0.0, 200.0, 0.0))["verdict"] == "blocked"
    assert sv.lookup((0.0, 50.0, 0.0))["verdict"] == "admissible-beyond"
    assert sv.lookup((0.0, 100.0, 0.0))["verdict"] == "admissible-at"
    assert sv.lookup((0.0, -200.0, 0.0))["verdict"] == "admissible-background"
    assert ds.admissible("admissible-at") and not ds.admissible("blocked")


def test_needed_distance_is_from_the_declared_point_not_the_origin():
    faces = ds.face_plan(0.0, 64, (0, 0, 4))
    for f in faces:
        f.buf = ds.array.array("f", [1.0] * (f.size * f.size))
    sv = ds.Survey((0, 0, 0), (0, 0, 4), (0, 0, 4), faces)
    row = sv.lookup((0.0, 100.0, 0.0))
    assert abs(row["needed"] - 100.0) < 1e-6
    assert abs(row["distFromOrigin"] - math.dist((0, 0, 4), (0, 100, 0))) < 0.01
    assert row["offset"] == [0, 0, 4]


def test_tolerance_is_shotplans():
    for d in (0.0, 100.0, 400.0):
        assert (ds.surface_tolerance(d, 90.0, 512, ds.TOL_ARGS)
                == ds.SHOTPLAN.surface_tolerance(d, 90.0, 512, ds.TOL_ARGS))
    assert ds.TOL_ARGS.surface_tol == 2.0 and ds.TOL_ARGS.surface_obliquity == 1.73


def test_camera_spec_parsing():
    assert ds.parse_camera("1,2,3")["pos"] == (1.0, 2.0, 3.0)
    c = ds.parse_camera("-602,-5451,167=A1:verified")
    assert c["label"] == "A1" and c["expected"] == "verified"
    assert ds.parse_camera("0,0,0=x:refused")["expected"] == "refused"
    assert ds.parse_triple("-1.5,0,2", "--offset") == (-1.5, 0.0, 2.0)


def test_negative_coordinates_survive_argparse():
    """A Skyrim coordinate usually starts with a minus, which argparse reads as a flag."""
    got = ds.glue_negative_values(["survey", "Cell", "--point", "-852,-5451,45", "--out", "d"])
    assert got == ["survey", "Cell", "--point=-852,-5451,45", "--out", "d"]
    assert ds.glue_negative_values(["--camera", "-602,-5451,167=A1:verified"]) == [
        "--camera=-602,-5451,167=A1:verified"]
    # positive values and other flags are untouched
    assert ds.glue_negative_values(["--point", "1,2,3", "--out", "x"]) == [
        "--point", "1,2,3", "--out", "x"]
    args = ds.build_parser().parse_args(
        ds.glue_negative_values(["survey", "C", "--point", "-1,-2,-3", "--offset", "0,0,-4",
                                 "--out", "d"]))
    assert ds.parse_triple(args.point, "--point") == (-1.0, -2.0, -3.0)
    assert ds.parse_triple(args.offset, "--offset") == (0.0, 0.0, -4.0)


def test_pick_regexes_match_viewprobes_own_lines():
    """The self-check is worth nothing if the parser silently matches nothing."""
    hit = "   pick 256,256: -852,-5451,45  cell (-1,-2)  254 u from the eye, no LAND here"
    bg = "   pick 0,0: background - no geometry under this pixel"
    got = ds.parse_picks(hit + "\n" + bg.replace("-", "--", 1))
    assert got[(256, 256)][3] == 254.0
    assert ds.PICK_BG.match("   pick 0,0: background")


def _main():
    fails = 0
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
                print(f"  PASS  {name}")
            except AssertionError as e:
                fails += 1
                print(f"  FAIL  {name}: {e}")
    print(f"\n  {'all tests passed' if not fails else str(fails) + ' FAILED'}")
    return 1 if fails else 0


if __name__ == "__main__":
    print(__doc__.strip().splitlines()[0])
    sys.exit(_main())
