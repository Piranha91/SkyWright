"""Known answers for the report's figure units and the reel's spoken names and removals (Missives review, 2026-10-30).

Dependency-free: `python _geometry-harness/tests/test_report_figures.py`. Media rows are synthetic, in the shape the
presentation carries (provenance.captureRequest and provenance.vignetteState from capture_acquire's media items).
"""
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import evalreport as ER  # noqa: E402
import reel_plan as RP  # noqa: E402

PASSED, FAILURES = [], []


def check(name, fn):
    try:
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def m(mid, req, state):
    return {"id": mid, "provenance": {"captureRequest": req, "vignetteState": state}}


def case_1_a_vignette_shows_its_marked_view():
    units = ER.figure_units([m("w", "r1", "with"), m("h", "r1", "highlight"), m("o", "r1", "without")])
    assert [[x["id"] for x in u] for u in units] == [["h"]], f"the marked view states the claim, got {units}"


def case_3_a_split_shows_both_halves_as_one_figure():
    units = ER.figure_units([m("R", "r2", "right"), m("L", "r2", "left")])
    assert [[x["id"] for x in u] for u in units] == [["L", "R"]], f"left then right, one unit, got {units}"


def case_5_frames_of_two_requests_are_two_units():
    units = ER.figure_units([m("h", "r1", "highlight"), m("L", "r2", "left"), m("R", "r2", "right"),
                             {"id": "x", "provenance": {}}])
    assert [[x["id"] for x in u] for u in units] == [["x"], ["h"], ["L", "R"]], units


def case_7_capital_mod_prefixes_are_not_spoken():
    got = [RP.humanize(e) for e in ("XJKRRDLC2RRBuilding03RoofSideOnlyShort", "DLC2TreeFloraScathecraw02",
                                    "HandCart01Wheel", "MAG_TestThing")]
    assert got == ["Building Roof Side Only Short", "Tree Flora Scathecraw", "Hand Cart Wheel", "Test Thing"], got


CAM = ["--eye", "30889.0,35010.0,545.3", "--yaw", "190.0", "--pitch", "3.3", "--fov", "65.0", "--aspect", "638:680"]


def case_9_a_spot_is_in_frame_by_projection():
    cam = RP.camera_of(CAM)
    assert cam and abs(cam["aspect"] - 638 / 680) < 1e-9, cam
    # the split's subject, a removed barrel a few metres from it, and spots behind and far to the side
    got = [RP.in_view(cam, p) for p in ((30793, 34719, 435), (30727.2, 34648.95, 389.65), (30889, 35300, 545),
                                         (31500, 34800, 500))]
    assert got == [True, True, False, False], got


def case_11_a_check_missing_on_every_setup_is_said_once():
    missing = [{"checkId": "a", "armId": x} for x in ("baseline", "c1", "c2")] + [
        {"checkId": "b", "armId": "baseline"}, {"checkId": "c", "armId": "c1", "part": "diff"},
        {"checkId": "c", "armId": "c2", "part": "diff"}, {"checkId": "c", "armId": "baseline", "part": "diff"}]
    got = ER.missing_everywhere(missing, ["baseline", "c1", "c2"])
    assert got == {"a"}, f"only a check with no result of its own on every setup, got {got}"


def case_13_a_split_marks_a_framed_removal_only_where_it_stands():
    import capture_acquire as CA
    req = {"requestType": "placement-split", "highlights": [{"formKey": "02C074:Dragonborn.esm", "color": "cyan"}],
           "marks": [{"formKey": "02C075:Dragonborn.esm", "state": "left", "color": "magenta"}], "placements": {}}
    cam = {"cell": "00EED6:Dragonborn.esm", "viewprobe": "--eye 1,2,3 --yaw 10 --pitch 0 --fov 65"}
    left = CA.capture_argv(req, cam, "left", "o.txt", [], 640)
    right = CA.capture_argv(req, cam, "right", "o.txt", [], 640)
    assert "02C075:Dragonborn.esm=magenta" in left and "02C075:Dragonborn.esm=magenta" not in right, (left, right)


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
