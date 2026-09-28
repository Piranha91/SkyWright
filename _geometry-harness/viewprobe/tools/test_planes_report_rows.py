"""Small exact-output and refusal cases for both planes-report schemas."""
import copy
import json
import unittest
from unittest.mock import patch

import planes_report_rows as R


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.row = {"subject": "A", "shape": "S", "relation": "unassessed: rule",
                    "predictedCulled": True, "visibilityMask": "mask.png",
                    "visibility": {"Assessed": True, "VisiblePixels": 1}}
        self.sparse = {"subject": "B", "status": "unassessed", "reason": "no source shapes"}
        self.v1 = {"schema": R.SCHEMA_V1, "planes": [
            {"formKey": "P", "candidates": [self.row, self.sparse]}]}
        body = {k: v for k, v in self.row.items() if k not in R_PLANE_KEYS}
        overlay = {k: v for k, v in self.row.items() if k in R_PLANE_KEYS}
        self.v2 = {"schema": R.SCHEMA_V2, "candidateBodies": [
            {"id": 0, **body}, {"id": 1, **self.sparse}], "planes": [
            {"formKey": "P", "candidates": [{"candidate": 0, **overlay}, {"candidate": 1}]}]}

    def test_materialise_and_sparse_absence(self):
        self.assertEqual(R.materialise(self.v1), R.materialise(self.v2))
        self.assertNotIn("relation", R.materialise(self.v2)[0][1])
        self.assertNotIn("predictedCulled", R.materialise(self.v2)[0][1])

    def test_text_scan_any_root_order_minified_and_lookalikes(self):
        keys = ("subject", "shape", "visibilityMask", "relation")
        for indent in (None, 1):
            for v in (self.v1, self.v2):
                d = {"metadata": {"schema": "fake"}, "note": '"schema": "fake"', **v}
                rows, counts = R.masked_rows_text(json.dumps(d, indent=indent), keys)
                self.assertEqual(rows, [{k: self.row.get(k) for k in keys}])
                self.assertEqual(counts, {"rowsSeen": 2, "rowsSkippedNoMask": 1, "rowsDecoded": 1})

    def test_refusals_in_document_and_text(self):
        bad = []
        for schema in (None, "occlusion-view-research-v3"):
            d = copy.deepcopy(self.v2)
            if schema is None:
                d.pop("schema")
            else:
                d["schema"] = schema
            bad.append(d)
        for value in (True, 0.0, -1, 1):
            d = copy.deepcopy(self.v2)
            d["candidateBodies"][0]["id"] = value
            bad.append(d)
        for value in (True, 0.0, -1, 99):
            d = copy.deepcopy(self.v2)
            d["planes"][0]["candidates"][1]["candidate"] = value
            bad.append(d)
        d = copy.deepcopy(self.v2)
        d["candidateBodies"][0]["relation"] = "collision"
        bad.append(d)
        d = copy.deepcopy(self.v2)
        d["candidateBodies"].pop()
        bad.append(d)
        for d in bad:
            with self.subTest(d=d):
                with self.assertRaises(R.PlanesReportError):
                    R.materialise(d)
                with self.assertRaises(R.PlanesReportError):
                    R.masked_rows_text(json.dumps(d), ("subject",))

    def test_null_mask_still_checks_reference_and_collision(self):
        d = copy.deepcopy(self.v2)
        d["planes"][0]["candidates"][1]["candidate"] = 99
        with self.assertRaises(R.PlanesReportError):
            R.masked_rows_text(json.dumps(d), ("subject",))
        d = copy.deepcopy(self.v2)
        d["planes"][0]["candidates"][1]["status"] = "collision"
        with self.assertRaises(R.PlanesReportError):
            R.masked_rows_text(json.dumps(d), ("subject",))

    def test_only_masked_body_is_fully_decoded(self):
        original = R._decode
        decoded = []
        def instrument(text, span):
            value = original(text, span)
            if isinstance(value, dict) and "id" in value:
                decoded.append(value["id"])
            return value
        with patch.object(R, "_decode", instrument):
            R.masked_rows_text(json.dumps(self.v2), ("subject",))
        self.assertEqual(decoded, [0])

    def test_body_owned_mask_and_malformed_skipped_body(self):
        d = copy.deepcopy(self.v2)
        mask = d["planes"][0]["candidates"][0].pop("visibilityMask")
        d["candidateBodies"][0]["visibilityMask"] = mask
        rows, counts = R.masked_rows_text(json.dumps(d), ("visibilityMask",))
        self.assertEqual(rows, [{"visibilityMask": mask}])
        self.assertEqual(counts["rowsDecoded"], 1)
        malformed = json.dumps(self.v2).replace('"reason": "no source shapes"',
                                                '"reason": [1,]')
        with self.assertRaises(R.PlanesReportError):
            R.masked_rows_text(malformed, ("subject",))


R_PLANE_KEYS = {"relation", "predictedCulled", "visibility", "visibilityMask", "isolatedDepthSha256"}

if __name__ == "__main__":
    unittest.main()
