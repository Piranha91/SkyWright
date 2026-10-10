"""Known answers for the predictive mode on synthetic inputs. Each case names the wrong reading it rejects."""
import math
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import predict as X  # noqa: E402
import planeassess as pa  # noqa: E402

EYE, LOOK = [0.0, 0.0, 0.0], [0.0, 1000.0, 0.0]
MODS = {"A": ["New.esp"], "B": ["Planes.esp"]}


def green(lo, hi, cols=10):
    a = np.zeros((1200, 1200, 4), dtype=np.uint8)
    a[lo:hi, :cols, 1] = 255
    return a


def cand(subject, visible, centre=(0, 1000, 0), radius=10, mask=None, culled=True, cullers=None, relation="Inside"):
    return {"subject": subject, "shape": subject + ":0", "predictedCulled": culled, "relation": relation,
            "visibility": {"Assessed": True, "VisiblePixels": visible}, "visibilityMask": mask,
            "worldSphere": {"center": list(centre), "radius": radius}, "allCullingPlanes": cullers or []}


def report(planes):
    """planes: [(formKey, definedBy, [candidates])]"""
    return {"schema": "occlusion-view-research-v1", "eye": EYE, "look": LOOK,
            "planes": [{"formKey": fk, "definedBy": d, "position": [0, 500, 0], "candidates": c} for fk, d, c in planes]}


MASKS = {"m-in": green(300, 310)}
reader = MASKS.get        # an unknown path reads as a thinned mask (None)


class Band(unittest.TestCase):
    def test_band_classes(self):
        self.assertEqual(X.band_class(EYE, LOOK, [0, 1000, 0], 10), "inside")
        self.assertEqual(X.band_class(EYE, LOOK, [0, 1000, 700], 10), "outside")     # rows near 100
        self.assertEqual(X.band_class(EYE, LOOK, [0, 1000, 472], 30), "straddle")    # across row 262

    def test_straddler_without_mask_is_a_range_not_whole_raster(self):
        # rejects reading whole-raster px as in-band for a shape across the band edge (sabotage S2)
        lo, hi, how = X.in_band_range(cand("1:New.esp", 500, centre=(0, 1000, 472), radius=30), EYE, LOOK, reader)
        self.assertEqual((lo, hi, how), (0, 500, "straddle"))

    def test_inside_and_outside_without_mask(self):
        self.assertEqual(X.in_band_range(cand("1:New.esp", 80), EYE, LOOK, reader), (80, 80, "inside"))
        self.assertEqual(X.in_band_range(cand("1:New.esp", 80, centre=(0, 1000, 700)), EYE, LOOK, reader),
                         (0, 0, "outside"))

    def test_mask_is_exact_when_on_disk(self):
        self.assertEqual(X.in_band_range(cand("1:New.esp", 500, centre=(0, 1000, 472), radius=30, mask="m-in"),
                                         EYE, LOOK, reader), (100, 100, "mask"))


class Rescore(unittest.TestCase):
    def facts(self, winner):
        d = report([("000800:Planes.esp", "Planes.esp", [cand("1:New.esp", 50)]),
                    ("000900:Base.esm", "Base.esm", [cand("2:Base.esm", 40)])])
        return X.rescore_view(d, winner, {"1:New.esp": "A"}, MODS, reader)

    def test_every_plane_of_the_report_is_scored(self):
        # rejects scoring only a supplied plane list (the parent's score files; sabotage S6)
        f = self.facts({"000800:Planes.esp": "Planes.esp", "000900:Base.esm": "Base.esm"})
        self.assertEqual(sorted(f["planes"]), ["000800:Planes.esp", "000900:Base.esm"])

    def test_owner_is_the_arm_winner_not_definedBy(self):
        # a base-game plane whose winning version is the plane mod's override belongs to the plane mod (sabotage S1)
        f = self.facts({"000800:Planes.esp": "Planes.esp", "000900:Base.esm": "Planes.esp"})
        self.assertEqual(f["planes"]["000900:Base.esm"]["owner"], "B")
        self.assertEqual(f["planes"]["000800:Planes.esp"]["rows"][0]["contentOwner"], "A")
        self.assertEqual(f["planes"]["000900:Base.esm"]["rows"][0]["contentOwner"], "base")


class Classify(unittest.TestCase):
    def test_adding_the_content_mod(self):
        # only inter-mod pairs are shown; mod-against-base stays in the machine record (sabotage S5)
        c = lambda p, o: X.classify(p, o, "F")
        self.assertEqual((c("B", "A"), c("base", "A"), c("A", "base"), c("A", "A"), c("B", "base")),
                         ("shown", "machine", "machine", "own", None))

    def test_adding_the_plane_mod(self):
        c = lambda p, o: X.classify(p, o, "R")
        self.assertEqual((c("B", "A"), c("B", "base"), c("A", "B"), c("base", "A"), c("B", "B")),
                         ("shown", "machine", "shown", None, "own"))


class Drawn(unittest.TestCase):
    def test_an_override_that_draws_the_same_is_existing_content(self):
        # rejects counting a mod's identical override as the mod's new content (sabotage S7)
        from test_planeassess import row
        base = dict(row(formKey="000A00:Base.esm"), prim=None, base="000123:Base.esm")
        same = dict(base)
        moved = dict(base, formKey="000A01:Base.esm", pos=[1.0, 2.0, 3.0])
        mbase = dict(base, formKey="000A01:Base.esm")
        new = dict(base, formKey="000B00:New.esp")
        got = X.drawn_content([same, moved, new], {"000A00:Base.esm": base, "000A01:Base.esm": mbase})
        self.assertEqual(got, {"000A01:Base.esm", "000B00:New.esp"})


def views(*per_gaze):
    """per_gaze: (gaze, {plane: (owner, [(subject, contentOwner, lo, hi, cullers)], refusal)})"""
    out = []
    for gaze, planes in per_gaze:
        f = {"eye": EYE, "look": LOOK, "planes": {}}
        for fk, (owner, rows, refusal) in planes.items():
            f["planes"][fk] = {"winner": None, "owner": owner, "refusal": refusal, "rows": [
                {"subject": s, "shape": s + ":0", "contentOwner": co, "wholePx": hi, "inBandLo": lo, "inBandHi": hi,
                 "how": "mask" if lo == hi else "straddle", "cullers": cu or [fk]} for s, co, lo, hi, cu in rows]}
        out.append({"gaze": gaze, "basis": "route", "facts": f})
    return out


P = "000800:Planes.esp"


class Findings(unittest.TestCase):
    def test_certain_and_possible(self):
        v = views(("g1", {P: ("B", [("1:New.esp", "A", 30, 30, None)], None)}),
                  ("g2", {"000801:Planes.esp": ("B", [("2:New.esp", "A", 0, 40, None)], None)}))
        f = {x["plane"]: x for x in X.findings(v, "F") if x["class"] == "shown"}
        self.assertEqual(f[P]["status"], "will hide")
        self.assertEqual(f["000801:Planes.esp"]["status"], "possible only: straddles the band, mask thinned")

    def test_base_plane_over_new_content_is_machine_only(self):
        v = views(("g1", {"000900:Base.esm": ("base", [("1:New.esp", "A", 30, 30, None)], None)}))
        self.assertEqual([(x["class"], x["status"]) for x in X.findings(v, "F")], [("machine", "will hide")])

    def test_rule_refusal_is_cannot_tell(self):
        v = views(("g1", {P: ("B", [("1:New.esp", "A", 30, 30, None)], "plane orientation outside the supplied rule")}))
        self.assertTrue(X.findings(v, "F")[0]["status"].startswith("cannot tell"))

    def test_override_of_a_master_plane_needs_the_render_without_the_mod(self):
        # rejects blaming the added plane mod for what the base-game version already hid (sabotage S1/KP5)
        v = views(("g1", {"000900:Base.esm": ("B", [("1:New.esp", "A", 30, 30, None)], None)}))
        f = X.findings(v, "R", overrides_master={"000900:Base.esm"})
        self.assertEqual(f[0]["status"], "cannot tell: needs the render without the new mod")
        hid = {"g1": views(("g1", {"000900:Base.esm": ("base", [("1:New.esp", "A", 25, 25, None)], None)}))[0]["facts"]}
        self.assertEqual(X.findings(v, "R", {"000900:Base.esm"}, hid)[0]["class"], "machine")
        clear = {"g1": views(("g1", {"000900:Base.esm": ("base", [], None)}))[0]["facts"]}
        f = X.findings(v, "R", {"000900:Base.esm"}, clear)[0]
        self.assertEqual((f["class"], f["status"]), ("shown", "will hide"))


class AnswerKey(unittest.TestCase):
    def finding(self, cullers=None):
        v = views(("g1", {P: ("B", [("1:New.esp", "A", 30, 30, cullers)], None)}))
        return [x for x in X.findings(v, "F") if x["class"] == "shown"][0]

    def test_disabled_with_no_other_culler_is_a_hit(self):
        self.assertEqual(X.answer_key(self.finding(), {"g1": None}, set(), {P: ["DISABLE"]})["outcome"], "hit")

    def test_disabled_but_another_active_plane_still_culls(self):
        f = self.finding(cullers=[P, "000802:Planes.esp"])
        self.assertEqual(X.answer_key(f, {"g1": None}, {"000802:Planes.esp"}, {P: ["DISABLE"]})["outcome"],
                         "open: cannot tell offline")

    def test_unedited_plane_stays_open_and_open_is_not_a_false_alarm(self):
        # rejects calling every unresolved prediction a false alarm (sabotage S8)
        k = X.answer_key(self.finding(), {"g1": None}, {P}, {})
        self.assertEqual(k["outcome"], "open: cannot tell offline")
        self.assertEqual(X.answer_key(self.finding(), {"g1": None}, {P}, {}, {P: "confirmed in game"})["outcome"],
                         "open: confirmed in game")
        self.assertEqual(X.answer_key(self.finding(), {"g1": None}, {P}, {}, {P: "engine does not hide"})["outcome"],
                         "false alarm")

    def test_resolution_is_read_shape_by_shape(self):
        # p hides a different shape with the same px: still open, never a hit by equal or smaller totals (sabotage S9)
        p = {"g1": views(("g1", {P: ("B", [("9:New.esp", "A", 30, 30, None)], None)}))[0]["facts"]}
        self.assertEqual(X.answer_key(self.finding(), p, {P}, {P: ["MOVE"]})["outcome"], "hit")
        p = {"g1": views(("g1", {P: ("B", [("1:New.esp", "A", 5, 5, None)], None)}))[0]["facts"]}
        self.assertEqual(X.answer_key(self.finding(), p, {P}, {P: ["MOVE"]})["outcome"], "open: cannot tell offline")

    def test_moved_without_a_p_render_or_refused_in_p(self):
        self.assertEqual(X.answer_key(self.finding(), {"g1": None}, {P}, {P: ["MOVE"]})["outcome"],
                         "resolution needs a p render")
        p = {"g1": views(("g1", {P: ("B", [], "plane orientation outside the supplied rule")}))[0]["facts"]}
        self.assertEqual(X.answer_key(self.finding(), p, {P}, {P: ["ROTATE"]})["outcome"], "resolution cannot tell")


class BlindSpots(unittest.TestCase):
    def test_evidence_decides_blind_spot(self):
        got = X.blind_spots({P: ["MOVE"], "000801:Planes.esp": ["DISABLE"]}, set(), {P: "hid content at a prior in-game pose"})
        self.assertEqual({x["plane"]: x["outcome"] for x in got},
                         {P: "blind spot", "000801:Planes.esp": "edit without a predicted clash"})


class Scope(unittest.TestCase):
    PB = {"kind": "PortalBox", "pos": [0, 1000, 0], "rot": [0, 0, 0], "half": [300, 1, 200]}

    def test_shadow(self):
        self.assertTrue(X.shadow_crosses(self.PB, EYE, [0, 2000, 0], 0))
        self.assertFalse(X.shadow_crosses(self.PB, EYE, [0, 500, 0], 0))           # in front of the plane
        self.assertFalse(X.shadow_crosses(self.PB, EYE, [1000, 2000, 0], 0))       # beside its shadow
        self.assertTrue(X.shadow_crosses(self.PB, EYE, [1000, 2000, 0], 256))      # within the growth

    def test_reach_is_a_parameter(self):
        # rejects the fixed 4,096 u frustum reach (sabotage S4): a plane 5,000 u away still culls
        rule = {"maximumAbsNormalZ": 0.001, "minimumEyeDistance": 42, "boxSilhouette": True,
                "horizontalPortalBoxNormalDownEyeAbove": True}
        far = dict(self.PB, pos=[0, 5000, 0])
        self.assertEqual(X.scope_planes([("p", far)], [[0, 6000, 0]], EYE, 0.0, rule, 4096, 256), [])
        self.assertEqual(X.scope_planes([("p", far)], [[0, 6000, 0]], EYE, 0.0, rule, 12288, 256), ["p"])


if __name__ == "__main__":
    unittest.main()
