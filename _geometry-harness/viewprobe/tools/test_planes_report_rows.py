"""Focused regression for the Python planes-report reader's interim schema guard."""
import unittest

from planes_report_rows import (
    SCHEMA_V1, SCHEMA_V2, UnsupportedPlanesReportSchema, require_v1, require_v1_text,
)


class SchemaGuardTests(unittest.TestCase):
    def test_v1_preserves_document_and_text(self):
        doc = {"schema": SCHEMA_V1, "planes": [{"candidates": [{"subject": "x"}]}]}
        encoded = '{"schema": "' + SCHEMA_V1 + '", "planes": []}'
        self.assertIs(require_v1(doc), doc)
        self.assertIs(require_v1_text(encoded), encoded)

    def test_v2_and_unknown_or_missing_refuse_explicitly(self):
        for schema in (SCHEMA_V2, "occlusion-view-research-v3", None):
            with self.subTest(schema=schema):
                with self.assertRaises(UnsupportedPlanesReportSchema) as doc_error:
                    require_v1({"schema": schema} if schema is not None else {})
                self.assertIn(repr(schema), str(doc_error.exception))
                encoded = '{"schema": "' + schema + '"}' if schema is not None else '{}'
                with self.assertRaises(UnsupportedPlanesReportSchema) as text_error:
                    require_v1_text(encoded)
                self.assertIn(repr(schema), str(text_error.exception))


if __name__ == "__main__":
    unittest.main()
