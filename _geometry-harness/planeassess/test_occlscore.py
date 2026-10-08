"""Known answers for the occlusion scorer library on synthetic reports: the band, the green convention, attribution
to the culling plane only, the owner split and the skipped unassessed rows. Each case fails on one wrong scorer."""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import occlscore as S  # noqa: E402


def mask(lo, hi, cols=10):
    a = np.zeros((1200, 1200, 4), dtype=np.uint8)
    a[lo:hi, :cols, 1] = 255
    return a


MASKS = {"in": mask(300, 310), "edge": mask(250, 270), "out": mask(0, 100), "red": None}
MASKS["red"] = np.zeros((1200, 1200, 4), dtype=np.uint8)
MASKS["red"][300:310, :10, 0] = 255


def cand(subject, culled, visible, m, assessed=True):
    return {"subject": subject, "shape": subject + ":0", "predictedCulled": culled, "visibilityMask": m,
            "visibility": {"VisiblePixels": visible, "Assessed": assessed}}


def report(cands, plane="000800:B.esp"):
    return {"schema": "occlusion-view-research-v1", "planes": [{"formKey": plane, "candidates": cands}]}


reader = MASKS.__getitem__


class Scorer(unittest.TestCase):
    def test_in_band_rows_only(self):
        s = S.score_view(report([cand("1:A.esp", True, 100, "in"), cand("2:A.esp", True, 100, "out")]), reader=reader)
        self.assertEqual(s["000800:B.esp"]["inBandPx"], 100)

    def test_band_edge_counts_from_row_262(self):
        s = S.score_view(report([cand("1:A.esp", True, 200, "edge")]), reader=reader)
        self.assertEqual(s["000800:B.esp"]["inBandPx"], 8 * 10)

    def test_green_channel_is_the_visible_sample(self):
        s = S.score_view(report([cand("1:A.esp", True, 100, "red")]), reader=reader)
        self.assertEqual(s["000800:B.esp"]["inBandPx"], 0)

    def test_only_rows_this_plane_culls(self):
        s = S.score_view(report([cand("1:A.esp", False, 100, "in")]), reader=reader)
        self.assertEqual(s["000800:B.esp"]["inBandPx"], 0)

    def test_unassessed_rows_are_skipped_and_counted(self):
        d = report([cand("1:A.esp", True, 100, "in", assessed=False)])
        self.assertEqual(S.score_view(d, reader=reader)["000800:B.esp"]["inBandPx"], 0)
        self.assertEqual(S.vis_status(d, {"1:A.esp"}, {"000800:B.esp"}), {"notAssessed": 1})

    def test_visibility_status_is_scoped_to_the_plane(self):
        d = {"schema": "occlusion-view-research-v1", "planes": [
            {"formKey": "000800:B.esp", "candidates": [cand("1:A.esp", True, 100, "in")]},
            {"formKey": "000801:B.esp", "candidates": [cand("2:A.esp", True, 100, "in", assessed=False)]}]}
        self.assertEqual(S.vis_status(d, {"1:A.esp", "2:A.esp"}, {"000800:B.esp"}), {"assessedVisible": 1})

    def test_unreadable_assets_under_a_plane(self):
        d = {"schema": "occlusion-view-research-v1",
             "unreadableAssets": {"assets": [{"kind": "mesh", "asset": "meshes\\Water\\W.nif"},
                                             {"kind": "mesh", "asset": "meshes\\Far\\F.nif"}]},
             "planes": [{"formKey": "000800:B.esp", "candidates": [dict(cand("1:A.esp", False, 0, None), mesh="Water/W.nif")]},
                        {"formKey": "000801:B.esp", "candidates": [dict(cand("2:A.esp", False, 0, None), mesh="x.nif")]}]}
        under, every = S.unreadable(d, {"000800:B.esp", "000801:B.esp"})
        self.assertEqual(under, {"000800:B.esp": ["meshes\\Water\\W.nif"], "000801:B.esp": []})
        self.assertEqual(every, ["meshes\\Far\\F.nif", "meshes\\Water\\W.nif"])

    def test_owner_split(self):
        d = report([cand("1:A.esp", True, 100, "in"), cand("2:Base.esm", True, 100, "in")])
        s = S.score_report(d, {"1:A.esp"}, reader=reader)["000800:B.esp"]
        self.assertEqual((s["cityInBandPx"], s["otherInBandPx"], s["cityNativePx"]), (100, 100, 1024))


if __name__ == "__main__":
    unittest.main()
