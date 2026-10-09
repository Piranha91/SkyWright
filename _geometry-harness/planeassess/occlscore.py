"""The offline occlusion scorer for viewprobe planes reports, as a library: what a plane hides at one view.

Brought in from the first offline occlusion assessment's sealed scorer (`score_view`, `green_in_rows`) and its
owner split (`score_report`) and per-view counts (`census`, `vis_status`), with no change to what counts:

  * hidden = a candidate row the plane `predictedCulled`, whose `visibility` is Assessed with VisiblePixels > 0
    (measured against the unculled scene), counted in-band from its evidence mask: green channel > 0 in rows
    [262, 938) of a 1200-square render, the rows a 16:9 view keeps. Native pixels = in-band x 10.24.
  * attribution: a shape counts under the plane whose candidates list carries it; a shape culled by several planes
    counts under each.
  * a culled row that is not Assessed is skipped by the score and counted by `vis_status`; a caller must not read a
    skipped row as clean.

Reads planes reports of both schemas through `_geometry-harness/viewprobe/tools/planes_report_rows.py`.
"""
from __future__ import annotations

import gzip
import json
import sys
from collections import Counter
from pathlib import Path

_TOOLS = Path(__file__).resolve().parents[2] / "_geometry-harness" / "viewprobe" / "tools"
sys.path.insert(0, str(_TOOLS))
from planes_report_rows import rows_by_plane  # noqa: E402

BAND = (262, 938)            # [lo, hi) rows of a 1200-square render
NATIVE_PER_RASTER = 10.24


def _rows(d):
    """rows_by_plane(d), joined once per report and cached on it: every reader below walks the same rows."""
    if "_planeRows" not in d:
        d["_planeRows"] = [(pl, list(c)) for pl, c in rows_by_plane(d)]
    return d["_planeRows"]


def load_report(path):
    p = Path(path)
    raw = gzip.decompress(p.read_bytes()) if p.suffix == ".gz" else p.read_bytes()
    return json.loads(raw.decode("utf-8"))


def green_in_rows(mask_path, band=BAND, reader=None):
    """Visible samples of one shape inside [band[0], band[1]) rows; `reader` lets a test inject an array."""
    if reader is not None:
        a = reader(mask_path)
    else:
        import numpy as np
        from PIL import Image
        a = np.asarray(Image.open(mask_path).convert("RGBA"))
    lo, hi = band
    return int((a[lo:hi, :, 1] > 0).sum())


def score_view(report, band=BAND, reader=None, planes=None):
    """Per plane: in-band visible predicted-culled pixels, with the report's whole-raster figure beside it.
    `planes` limits the work to those planes (each plane's figures are independent of the others)."""
    d = report if isinstance(report, dict) else load_report(report)
    out = {}
    for pl, candidates in _rows(d):
        if planes is not None and pl["formKey"] not in planes:
            continue
        in_band = raster = 0
        shapes = []
        for c in candidates:
            if not c.get("predictedCulled"):
                continue
            v = c.get("visibility")
            if not v or not v.get("Assessed") or v.get("VisiblePixels", 0) <= 0:
                continue
            m = c.get("visibilityMask")
            g = green_in_rows(m, band, reader) if m else 0
            in_band += g
            raster += v["VisiblePixels"]
            shapes.append({"subject": c.get("subject"), "shape": c.get("shape"),
                           "constructed": bool(c.get("constructed")),
                           "inBandPx": g, "wholeRasterPx": v["VisiblePixels"]})
        out[pl["formKey"]] = {
            "inBandPx": in_band, "wholeRasterPx": raster, "nativePx": round(in_band * NATIVE_PER_RASTER),
            "largestShapeInBandPx": max((s["inBandPx"] for s in shapes), default=0),
            "largestShapeNativePx": round(max((s["inBandPx"] for s in shapes), default=0) * NATIVE_PER_RASTER),
            "shapes": shapes}
    return out


def score_report(report, city_keys, reader=None, planes=None):
    """score_view split by owner: shapes whose subject is in `city_keys` (the content mod's placed records)
    against all others. Field names are the sealed ones."""
    d = report if isinstance(report, dict) else load_report(report)
    sc = score_view(d, reader=reader, planes=planes)
    pos = {}
    for pl, candidates in _rows(d):
        for c in candidates:
            if c.get("refPosition") is not None:
                pos.setdefault(c.get("subject"), c.get("refPosition"))
    out = {}
    for fk, s in sc.items():
        city = [sh for sh in s["shapes"] if sh["subject"] in city_keys]
        other = [sh for sh in s["shapes"] if sh["subject"] not in city_keys]
        for sh in city + other:
            sh["refPosition"] = pos.get(sh["subject"])
        cb = sum(sh["inBandPx"] for sh in city)
        ob = sum(sh["inBandPx"] for sh in other)
        out[fk] = {
            "inBandPx": s["inBandPx"], "nativePx": s["nativePx"],
            "cityInBandPx": cb, "cityNativePx": round(cb * NATIVE_PER_RASTER),
            "otherInBandPx": ob, "otherNativePx": round(ob * NATIVE_PER_RASTER),
            "cityLargestShapeInBandPx": max((sh["inBandPx"] for sh in city), default=0),
            "cityShapes": sorted(city, key=lambda sh: -sh["inBandPx"])[:20],
            "otherShapes": sorted(other, key=lambda sh: -sh["inBandPx"])[:10],
            "cityShapeCount": len(city), "otherShapeCount": len(other),
        }
    return out


def census(report, city_keys, planes=None):
    """Per plane: the content mod's candidate rows as predicted-culled / unassessed / predicted-drawn."""
    d = report if isinstance(report, dict) else load_report(report)
    per = {}
    for pl, candidates in _rows(d):
        if planes is not None and pl["formKey"] not in planes:
            continue
        rows = [c for c in candidates if c.get("subject") in city_keys]
        cls, why = Counter(), Counter()
        for c in rows:
            rel = str(c.get("relation") or "")
            if c.get("predictedCulled"):
                cls["predicted-culled"] += 1
            elif rel.startswith("unassessed"):
                cls["unassessed"] += 1
                why[(c.get("reason") or rel)[:120]] += 1
            else:
                cls["predicted-drawn"] += 1
        per[pl["formKey"]] = {"kind": pl.get("kind"), "definedBy": pl.get("definedBy"), "changedBy": pl.get("changedBy"),
                              "cityShapes": dict(cls),
                              "cityCulledRefs": sorted({c["subject"] for c in rows if c.get("predictedCulled")}),
                              "unassessedReasons": dict(why.most_common(6))}
    return per


def vis_status(report, city_keys, planes):
    """Culled content rows under the given planes by visibility status. Any `notAssessed` or `noVisibility` row
    means the score could not see that content: the view cannot be called clean for that plane."""
    d = report if isinstance(report, dict) else load_report(report)
    c = Counter()
    for pl, candidates in _rows(d):
        if pl["formKey"] not in planes:
            continue
        for x in candidates:
            if x.get("subject") not in city_keys or not x.get("predictedCulled"):
                continue
            v = x.get("visibility")
            if not v:
                c["noVisibility"] += 1
            elif not v.get("Assessed"):
                c["notAssessed"] += 1
            elif v.get("VisiblePixels", 0) > 0:
                c["assessedVisible"] += 1
            else:
                c["assessedZero"] += 1
    return dict(c)


# viewprobe's plane-level rule refusals (ShapeOcclusion.Shadow, PortalBoxOrientation): the hidden-content test refused
RULE_REFUSALS = ("engine rule unconfigured", "Box face behavior unassessed", "invalid plane bounds or scale",
                 "box orientation outside the supplied rule's evidence scope",
                 "no box view shadow", "near-eye behavior unassessed", "degenerate plane or camera",
                 "plane orientation outside the supplied rule's evidence scope",
                 "horizontal PortalBox with its normal up", "horizontal PortalBox seen from below")


def refusal(report, plane):
    """The rule's refusal for one plane at this view: the rule-refusal text when every candidate row of the plane is
    `unassessed: ...` and at least one names a rule refusal (rows the renderer cannot judge carry their own shape-level
    reasons beside it), else None (no rows, or some row's relation was assessed)."""
    d = report if isinstance(report, dict) else load_report(report)
    for pl, candidates in _rows(d):
        if pl["formKey"] != plane:
            continue
        texts = set()
        for c in candidates:
            rel = str(c.get("relation") or "")
            if not rel:
                continue                # a row with no relation (no shape, a ref-level issue) says nothing either way
            if not rel.startswith("unassessed: "):
                return None
            t = rel[len("unassessed: "):]
            if any(t.startswith(r) for r in RULE_REFUSALS):
                texts.add(t)
        return sorted(texts)[0] if texts else None
    return None


def hidden_anywhere(report):
    """Every shape some plane culls while the unculled scene shows it (Assessed, VisiblePixels > 0), as
    "subject|shape". Read from the report alone (no masks): what is already hidden at this view."""
    d = report if isinstance(report, dict) else load_report(report)
    out = set()
    for pl, candidates in _rows(d):
        for c in candidates:
            v = c.get("visibility") or {}
            if c.get("predictedCulled") and v.get("Assessed") and v.get("VisiblePixels", 0) > 0:
                out.add("%s|%s" % (c.get("subject"), c.get("shape")))
    return out


def _norm_asset(a):
    a = (a or "").replace("/", "\\").lower()
    return a[len("meshes\\"):] if a.startswith("meshes\\") else a


def unreadable(report, planes):
    """The report's unreadable assets (viewprobe exits 3 when it names any) and, per plane of `planes`, those among the
    plane's own candidate rows (the shapes the plane relates to at this view): ({plane: [asset, ...]}, [asset, ...])."""
    d = report if isinstance(report, dict) else load_report(report)
    assets = [a.get("asset") for a in ((d.get("unreadableAssets") or {}).get("assets") or []) if a.get("asset")]
    bad = {_norm_asset(a): a for a in assets}
    under = {}
    for pl, candidates in _rows(d):
        if pl["formKey"] not in planes:
            continue
        hit = {bad[_norm_asset(c.get("mesh"))] for c in candidates if _norm_asset(c.get("mesh")) in bad}
        under[pl["formKey"]] = sorted(hit)
    return under, sorted(set(assets))
