"""Known answers for the per-plane-edit verdict (verdict.decide) on dict-only views: a new hide, fixes against does
not fix, whose plane it is, a visibility test that did not run, and an eye-independent refusal held at every view of
a version. Each case fails on one wrong rule."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import verdict as V  # noqa: E402

A_SHAPE = {"subject": "000801:A.esp", "shape": "s0", "inBandPx": 5, "owner": "A"}
TILT = "plane orientation outside the supplied rule's evidence scope"


def edit(winner_u="B.esp"):
    return {"formKey": "000800:B.esp", "class": "moved", "edits": ["MOVE"], "kind": "PortalBox",
            "owners": {"A": ["A.esp"], "B": ["B.esp"], "patch": "P.esp"},
            "winners": {"u": winner_u, "p": "P.esp"},
            "versions": {"u": {"position": [0, 0, 0], "active": True}, "p": {"position": [0, 0, 0], "active": True}}}


def view(gaze, arm, shapes, vis=None, refusal=None):
    return {"gaze": gaze, "arm": arm, "basis": "route", "plane": {"position": [0, 0, 0]},
            "shapes": shapes, "visStatus": vis or {}, "refusal": refusal, "hiddenAnywhere": []}


class Decide(unittest.TestCase):
    def test_a_shape_the_patch_newly_hides_is_patch_placement_hides(self):
        out = V.decide(edit(), [view("g1", "u", []), view("g1", "p", [A_SHAPE])])
        self.assertEqual(out["verdict"], "patch placement hides")

    def test_the_plane_mod_hiding_content_that_the_patch_shows_is_a_fix(self):
        out = V.decide(edit(), [view("g1", "u", [A_SHAPE]), view("g1", "p", [])])
        self.assertEqual(out["verdict"], "fixes an inter-mod clash")

    def test_the_plane_mod_still_hiding_the_same_shape_does_not_fix(self):
        out = V.decide(edit(), [view("g1", "u", [A_SHAPE]), view("g1", "p", [A_SHAPE])])
        self.assertEqual(out["verdict"], "does not fix")

    def test_the_content_mods_own_plane_is_taste_not_a_fix(self):
        out = V.decide(edit(winner_u="A.esp"), [view("g1", "u", [A_SHAPE]), view("g1", "p", [])])
        self.assertEqual((out["verdict"], out["reason"]), ("taste (no clash)", "the content mod's own plane"))

    def test_a_visibility_test_that_did_not_run_is_cannot_tell(self):
        out = V.decide(edit(), [view("g1", "u", [A_SHAPE]), view("g1", "p", [], vis={"noVisibility": 1})])
        self.assertEqual((out["verdict"], out["reason"]), ("cannot tell", "visibility test did not run"))

    def test_an_eye_independent_refusal_holds_at_every_view_of_the_version(self):
        views = [view("g1", "u", []), view("g1", "p", [], refusal=TILT),
                 view("g2", "u", []), view("g2", "p", [A_SHAPE])]
        out = V.decide(edit(), views)
        self.assertEqual((out["verdict"], out["reason"]), ("cannot tell", "rule refuses a tilted plane"))


if __name__ == "__main__":
    unittest.main()
