"""Read candidate rows from a viewprobe planes report.

The first migration step deliberately accepts only v1. A caller must reject a
v2 report before interpreting its slim overlays as complete candidate rows.
"""

import re

SCHEMA_V1 = "occlusion-view-research-v1"
SCHEMA_V2 = "occlusion-view-research-v2"
_SCHEMA_TEXT = re.compile(r'"schema"\s*:\s*"([^"]*)"')


class UnsupportedPlanesReportSchema(ValueError):
    pass


def require_v1(doc):
    schema = doc.get("schema") if isinstance(doc, dict) else None
    if schema != SCHEMA_V1:
        raise UnsupportedPlanesReportSchema(
            "planes report schema %r is unsupported; expected %s" % (schema, SCHEMA_V1))
    return doc


def require_v1_text(text):
    match = _SCHEMA_TEXT.search(text[:8192])
    schema = match.group(1) if match else None
    if schema != SCHEMA_V1:
        raise UnsupportedPlanesReportSchema(
            "planes report schema %r is unsupported; expected %s" % (schema, SCHEMA_V1))
    return text
