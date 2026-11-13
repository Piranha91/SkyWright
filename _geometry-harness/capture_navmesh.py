"""The camera for a `navmesh-version-comparison` capture request: a sliced top-down over the corroborated broken link.

Called by `capture_acquire.py` (acquire, capture, navmesh-control); every helper it needs from there arrives as `CA`, so
the refusal class, the planner call and the fingerprint rules are the adapter's own, not copies.

A navmesh request names two navmesh records and a cell label, no placed object, so nothing in it places a camera. The
place is measured from the records, in the request's own configuration: `viewprobe --nav-link SOURCE=TARGET` lists every
source triangle edge whose edge-link entry names the target, with its two endpoints and whether its target triangle is at
or past the target's winning triangle count (navownprobe's EDGE-IDX). Those beyond crossings, every one the request's
corroboration names, are the camera's subjects; shotplan's labelled sliced top-down (`plan --top-down-only`) frames them
with 512 u of each navmesh around. The winning state draws the source cell's navmeshes cyan, each target cell magenta, the
source's broken-crossing triangles red and asks for the target triangles the links name (the receipt must say they are
absent, so none is drawn); the historical state is the same camera with the source's earlier version substituted
(`--nav-version`) and no source red (its triangle indices are the winner's). Each state is accepted only when the
renderer's `-navmeshes.json` receipt agrees with the measurement and the frame holds NAV_MIN_PIXELS of each colour.
Everything else refuses in one sentence. A picture of records, not a player viewpoint, and no evidence of NPC movement.
"""
from __future__ import annotations

import json
import math
import re
import shlex
import sys
from pathlib import Path

STATES = ("winning-linked-navmeshes", "historical-navmesh-context")
SUPPORTED_KINDS = ("EDGE-IDX",)
LOCATION = re.compile(r"^(?P<cell>\S.*?) \((?P<kind>ext|int)\)")
NAV_LINK = re.compile(r"^\s*nav-link (\{.*\})\s*$")
VIEW_LABEL = re.compile(r"^view-label: .*? \| rect (\d+),(\d+),(\d+),(\d+)\s*$")
NAV_MIN_PIXELS = 25           # the top-down floor of capture_acquire (TOP_DOWN_MIN_PIXELS): a guard, not a finding
NAV_PROBE_SIZE = 320
NAV_FRAME_MIN = 1024.0        # shotplan --fallback-extent: the frame is max(this, 2 * spread + 1024) u at the floor
NAV_MAX_SPREAD = 2048.0       # crossings farther than this from their centroid do not fit one legible top-down
NAV_MODE = "both"
# A pixel's colour class, by dominance, so the translucent sheet over ground still counts; the label rectangle is left out.
NAV_CLASSES = {
    "red": lambda r, g, b: (r >= 170) & (g <= 90) & (b <= 90),
    "magenta": lambda r, g, b: (r >= 170) & (b >= 170) & (g <= 140),
    "cyan": lambda r, g, b: (g >= 170) & (b >= 170) & (r <= 120),
}


def check_request(CA, request: dict, points: str) -> dict:
    """The request's own shape, before any render: what it names and which version is which."""
    if points != "auto":
        raise CA.Refused("navmesh-points-supplied", "a navmesh camera is measured from the navmesh records, so supplied "
                                                    "points are refused; pass --points auto")
    kinds = sorted({c.get("kind") for c in request.get("corroboration") or []})
    if not kinds or any(k not in SUPPORTED_KINDS for k in kinds):
        raise CA.Refused("navmesh-corroboration-unsupported",
                         f"this camera shows edge links whose triangle index is past the target's count (EDGE-IDX); the "
                         f"request is corroborated by {', '.join(str(k) for k in kinds) or 'nothing'}, which it does not draw")
    m = LOCATION.match(str(request.get("location") or ""))
    if not m:
        raise CA.Refused("navmesh-location-unreadable",
                         f"the request's location {request.get('location')!r} does not name a cell as navownprobe labels it")
    if m["kind"] == "int":
        raise CA.Refused("navmesh-interior-unsupported",
                         "the navmesh is in an interior, and a neighbour's navmesh can be coloured apart only in an exterior")
    views = {v.get("role"): v for v in request.get("views") or []}
    earlier, later = views.get("earlier-navmesh"), views.get("overwriting-navmesh")
    if not earlier or not later or earlier.get("formKey") != later.get("formKey"):
        raise CA.Refused("navmesh-views-unusable", "the request does not name one navmesh's earlier and overwriting versions")
    source = later["formKey"]
    pairs = []
    for c in request.get("corroboration") or []:
        if c.get("formKey") != source:
            raise CA.Refused("navmesh-views-unusable", f"a corroboration names {c.get('formKey')}, not the compared navmesh {source}")
        for t in c.get("otherRecordKeys") or []:
            if t not in pairs:
                pairs.append(t)
    if not pairs:
        raise CA.Refused("navmesh-views-unusable", "the corroboration names no target navmesh")
    return {"cell": m["cell"], "source": source, "targets": pairs, "earlier": earlier["navPlugin"],
            "overwriting": later["navPlugin"]}


def query_links(CA, a, shotplan_mod, spec: dict, receipts: Path, expected: str, source_doc: dict,
                state=None, order_flags=()) -> list[dict]:
    """The `--nav-link` rows for every source=target pair, from the configuration the renderer reads."""
    flags = ["--config-identity", *order_flags]
    for t in spec["targets"]:
        flags += ["--nav-link", f"{spec['source']}={t}"]
    rc, text = CA._vp_run(a, shotplan_mod, spec["cell"], flags, receipts)
    rows = {}
    for ln in text.splitlines():
        m = NAV_LINK.match(ln)
        if m:
            row = json.loads(m[1])
            rows[(row.get("source"), row.get("target"))] = row
    out = []
    for t in spec["targets"]:
        row = rows.get((spec["source"], t))
        if row is None:
            raise CA.Refused("navmesh-link-unmeasured", f"the renderer gave no --nav-link answer for {spec['source']} into {t} "
                                                        f"(viewprobe exit {rc}); see {receipts / 'console.txt'}")
        if row.get("status") != "ok":
            raise CA.Refused("navmesh-link-unmeasured", f"the link from {spec['source']} into {t} was not measured: {row.get('reason')}")
        if expected is not None:
            check_identity(CA, row.get("configuration"), expected, source_doc, state, "the link measurement")
        out.append(row)
    return out


def check_identity(CA, observed, expected, source_doc, state, what):
    if observed == expected:
        return
    scoped = CA.scene_identity(source_doc, expected, state=state) if source_doc else None
    if scoped is None or scoped["sceneFingerprint"] != observed:
        raise CA.Refused("configuration-mismatch", f"{what} was made in configuration {str(observed)[:16]}… but this finding "
                                                   f"was measured in {str(expected)[:16]}…",
                         expectedFingerprint=expected, observedFingerprint=observed)


def broken(CA, spec: dict, rows: list[dict], require_all=True) -> dict:
    """The beyond crossings per target, refusing what the request's corroboration says and this configuration does not."""
    src_cell = rows[0]["sourceCell"]
    if rows[0]["sourcePlugin"].casefold() != spec["overwriting"].casefold():
        raise CA.Refused("navmesh-overwrite-not-winning",
                         f"{spec['source']} is won by {rows[0]['sourcePlugin']}, not by the overwriting {spec['overwriting']}, so "
                         "the comparison's overwriting version is not the one the game loads")
    if src_cell.get("interior"):
        raise CA.Refused("navmesh-interior-unsupported", "the source navmesh stands in an interior cell")
    per = {}
    for row in rows:
        if row["targetCell"]["formKey"] == src_cell["formKey"]:
            raise CA.Refused("navmesh-link-same-cell", f"{row['target']} stands in the source's own cell, so the two navmeshes "
                                                       "cannot be drawn in different colours")
        xs = [c for c in row.get("crossings") or [] if c.get("beyond")]
        if not xs and require_all:
            if row.get("tableBeyond"):
                raise CA.Refused("navmesh-link-not-located",
                                 f"{spec['source']}'s link table names {row['target']} past its {row['targetTriangles']} "
                                 "triangles, but no triangle edge uses that entry, so there is no place on the seam to show")
            raise CA.Refused("navmesh-link-not-reproduced",
                             f"in this configuration no edge of {spec['source']} links {row['target']} past its "
                             f"{row['targetTriangles']} triangles, so the corroborated mismatch is not there to picture")
        per[row["target"]] = {"row": row, "crossings": xs}
    return per


def nav_flags(spec, per, state, red_source=True):
    """The navmesh flags of one state, after the camera: colours, red marks, and the historical substitution."""
    flags = ["--nav", NAV_MODE, "--nav-color", "cyan"]
    for cell in dict.fromkeys(p["row"]["targetCell"]["formKey"] for p in per.values()):
        flags += ["--nav-cell", f"{cell}=magenta"]
    if state == STATES[1]:
        flags += ["--nav-version", f"{spec['source']}={spec['earlier']}"]
    elif red_source:
        for tri in sorted({c["tri"] for p in per.values() for c in p["crossings"]}):
            flags += ["--red", f"{spec['source']}:{tri}"]
    for t, p in per.items():
        for k in sorted({c["targetTri"] for c in p["crossings"]}):
            flags += ["--red", f"{t}:{k}"]
    return flags


def count_pixels(png: Path, text: str) -> dict:
    import numpy as np
    from PIL import Image
    img = np.asarray(Image.open(png).convert("RGB")).astype(int)
    keep = np.ones(img.shape[:2], dtype=bool)
    for ln in text.splitlines():
        m = VIEW_LABEL.match(ln)
        if m:
            x, y, w, h = (int(v) for v in m.groups())
            keep[y:y + h, x:x + w] = False
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return {k: int((f(r, g, b) & keep).sum()) for k, f in NAV_CLASSES.items()}


def check_receipt(CA, receipt: dict, spec, per, state) -> None:
    """The renderer's own record of what it drew must agree with the measurement."""
    recs = {r["FormKey"]: r for layer in receipt.get("Layers") or [] for r in layer.get("Records") or []}
    want = spec["earlier"] if state == STATES[1] else spec["overwriting"]
    src = recs.get(spec["source"])
    if src is None or str(src.get("Provider")).casefold() != want.casefold():
        raise CA.Refused("navmesh-receipt-mismatch", f"the {state} frame drew {spec['source']} from "
                                                     f"{(src or {}).get('Provider')}, not from {want}")
    for t, p in per.items():
        rec = recs.get(t)
        if rec is None or rec.get("TriangleCount") != p["row"]["targetTriangles"] or str(rec.get("Colour")) != "magenta":
            raise CA.Refused("navmesh-receipt-mismatch", f"the {state} frame's {t} is {rec and (rec.get('TriangleCount'), rec.get('Colour'))}, "
                                                         f"not the measured {p['row']['targetTriangles']} triangles in magenta")
        asked = {q["Index"]: q for q in rec.get("RedRequests") or []}
        for k in {c["targetTri"] for c in p["crossings"]}:
            if k not in asked or asked[k].get("Exists") is not False:
                raise CA.Refused("navmesh-receipt-mismatch", f"the {state} frame has {t} triangle {k}, which the link was "
                                                             "measured to miss; a missing triangle is never drawn")
    if state == STATES[0]:
        asked = {q["Index"]: q for q in src.get("RedRequests") or []}
        for tri in {c["tri"] for p in per.values() for c in p["crossings"]}:
            if not (asked.get(tri) or {}).get("DrawableInBand"):
                raise CA.Refused("navmesh-receipt-mismatch", f"{spec['source']} triangle {tri}, the broken crossing, is not drawn")


def render_state(CA, a, shotplan_mod, cell, camera, flags, out: Path, size=NAV_PROBE_SIZE, order_flags=()):
    vflags = shlex.split(camera["viewprobe"]) + ["--size", str(size), "--msaa", "0", "--report-mode", "--config-identity",
                                                 *order_flags, *camera["renderFlags"], *flags]
    rc, text = CA._vp_run(a, shotplan_mod, cell, vflags, out)
    png = next(iter(sorted(out.glob("*-fp.png"))), None)
    receipt = next(iter(sorted(out.glob("*-navmeshes.json"))), None)
    return rc, text, png, receipt


def acquire(CA, a, request: dict, source: dict, receipts: Path, shotplan_mod) -> dict:
    spec = check_request(CA, request, a.points)
    expected = request.get("configurationFingerprint")
    rows = query_links(CA, a, shotplan_mod, spec, receipts / "nav-link", expected, source)
    per = broken(CA, spec, rows)
    cell = rows[0]["sourceCell"]["formKey"]
    pts = [p for t in per.values() for c in t["crossings"] for p in (c["a"], c["b"])]
    cx, cy = sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)
    spread = max(math.hypot(p[0] - cx, p[1] - cy) for p in pts)
    if spread > NAV_MAX_SPREAD:
        raise CA.Refused("navmesh-links-too-far-apart",
                         f"the broken crossings reach {spread:.0f} u from their centre, past the {NAV_MAX_SPREAD:.0f} u one "
                         "legible top-down can hold")
    d = receipts / "top-down"
    d.mkdir(parents=True, exist_ok=True)
    pj, wl = d / "shotplan-plan.json", d / "shotplan-worklist.json"
    shotplan_mod.claim_outputs([str(pj), str(wl)], request["id"])
    floor = min(p[2] for p in pts)
    argv = [sys.executable, str(CA.SHOTPLAN), "plan", cell, "--floor-z", repr(float(floor)),
            "--top-down-only", "a navmesh link is drawn from above: it names no placed object to stand in front of",
            "--json", str(pj), "--worklist", str(wl), "--label", f"capture request {request['id']} navmesh link",
            "--fov", "65", "--aspect", "16:9", "--fallback-extent", f"{NAV_FRAME_MIN:g}", *CA.planner_out(d)]
    for i, p in enumerate(pts):
        argv.append(f"--subject={p[0]:g},{p[1]:g},{p[2]:g}=crossing{i // 2}{'ab'[i % 2]}")
    if a.vp:
        argv += ["--vp", a.vp]
    if a.passthrough:
        argv += ["--"] + list(a.passthrough)
    call = CA.call_planner(argv, d)
    doc = CA.load_json(wl) if wl.is_file() else {}
    shot = next((x for c_ in doc.get("cells") or [] for x in c_.get("shots") or []
                 if isinstance(x, dict) and x.get("status") == "fallback"), None)
    if shot is None:
        raise CA.Refused("planner-not-verified", f"the planner wrote no top-down shot (exit {call['exitCode']}); see {call['log']}")
    check_identity(CA, (doc.get("scene") or {}).get("renderer"), expected, source, None, "the top-down")
    cut = shot.get("cullAbove")
    aspect = shot.get("aspect") or 1.777778
    vpcam = (f"--eye {shot['eye'][0]:.1f},{shot['eye'][1]:.1f},{shot['eye'][2]:.1f} --yaw {float(shot.get('yaw', 0)):.1f} "
             f"--pitch {float(shot['pitch']):.1f} --fov {shot.get('fov', 65)}" + shotplan_mod.aspect_flag(aspect)
             + (f" --cull-above {float(cut):.1f}" if cut is not None else ""))
    # the storey band would cut sloping exterior navmesh away; the grid stays
    rflags = CA._strip_flag([str(x) for x in doc.get("renderFlags") or []], "--nav-z")
    camera = {"cell": cell, "eye": list(shot["eye"]), "yaw": float(shot.get("yaw", 0)), "pitch": float(shot["pitch"]),
              "fov": shot.get("fov", 65), "aspect": aspect, "cullAbove": cut, "floorZ": floor, "viewKind": "top-down",
              "nav": NAV_MODE, "renderFlags": rflags, "viewFlags": [], "viewprobe": vpcam,
              "navFlags": {s_: nav_flags(spec, per, s_) for s_ in STATES}}
    accept = []
    for s_ in STATES:
        rc, text, png, rcpt = render_state(CA, a, shotplan_mod, cell, camera, camera["navFlags"][s_], d / f"probe-{s_}")
        if rc != 0 or png is None:
            tail = next((ln.strip() for ln in reversed(text.splitlines()) if "--nav-version" in ln), None)
            if s_ == STATES[1] and tail:
                raise CA.Refused("navmesh-version-absent", f"the historical frame could not substitute {spec['earlier']}'s "
                                                           f"version: {tail}")
            raise CA.Refused("points-renderer-failed", f"the {s_} probe did not render (viewprobe exit {rc})")
        if rcpt is None:
            raise CA.Refused("navmesh-receipt-mismatch", f"the {s_} frame wrote no -navmeshes.json receipt")
        check_receipt(CA, CA.load_json(rcpt), spec, per, s_)
        px = count_pixels(png, text)
        need = ("red", "cyan", "magenta") if s_ == STATES[0] else ("cyan", "magenta")
        short = [k for k in need if px[k] < NAV_MIN_PIXELS]
        if short:
            raise CA.Refused("navmesh-layer-unseen", f"the {s_} frame shows {', '.join(f'{px[k]} {k}' for k in short)} "
                                                     f"pixel(s), under the {NAV_MIN_PIXELS} each layer needs", pixels=px, state=s_)
        accept.append({"state": s_, "pixels": px, "png": str(png), "receipt": str(rcpt), "console": str(png.parent / "console.txt")})
    seq = request.get("sequence") or {}
    return {"schemaVersion": 1, "kind": "capture-acquisition", "status": "camera-ready", "generatedUtc": CA.utcnow(),
            "synthetic": False, "viewKind": "top-down",
            "request": {"id": request["id"], "requestType": request.get("requestType"), "checkId": request.get("checkId"),
                        "armId": request.get("armId"), "findingIds": request.get("findingIds"),
                        "location": request.get("location"), "recordKeys": request.get("recordKeys"),
                        "status": request.get("status")},
            "source": source, "poseId": shot.get("poseId"), "camera": camera, "cutaway": None,
            "navmesh": {"source": spec["source"], "earlier": spec["earlier"], "overwriting": spec["overwriting"],
                        "links": rows, "subjects": [{"point": p} for p in pts], "spread": round(spread, 1),
                        "acceptance": accept, "minPixels": NAV_MIN_PIXELS,
                        "note": "pixel counts are a guard that each layer is in the frame, not a measure of the defect"},
            "configuration": {"expected": expected, "observed": (doc.get("scene") or {}).get("renderer"), "match": True},
            "states": [{"state": s_, "poseId": shot.get("poseId"), "camera": camera, "setup": request.get("armId"),
                        "navFlags": camera["navFlags"][s_]} for s_ in STATES],
            "sequence": seq,
            "visualQualification": {"status": "pending",
                                    "note": "a sliced top-down of navmesh records, not a player viewpoint; it shows a "
                                            "record-level link mismatch, not NPC movement"},
            "receipts": {"receiptsDir": str(receipts), "planJson": str(pj), "worklist": str(wl), "plannerLog": call["log"],
                         "navLink": str(receipts / "nav-link" / "console.txt")}}


def capture_argv(request: dict, camera: dict, state: str, order: str, passthrough: list[str], size: int) -> list[str]:
    """One frame's viewprobe arguments: the camera verbatim, report mode, the cell outline kept (it is the linked border)."""
    return [camera["cell"], *passthrough, "--order", str(order), *shlex.split(camera["viewprobe"]),
            "--size", str(size), "--msaa", "4", "--report-mode", *[str(x) for x in camera.get("renderFlags") or []],
            *[str(x) for x in (camera.get("navFlags") or {}).get(state) or []]]


def receipt_check_for_capture(CA, request, acq, state, receipt_path):
    """The capture's frame is checked against the acquisition's measurement, as the probe was (None when it agrees)."""
    nm = acq.get("navmesh") or {}
    spec = {"source": nm.get("source"), "earlier": nm.get("earlier"), "overwriting": nm.get("overwriting")}
    per = {r["target"]: {"row": r, "crossings": [c for c in r.get("crossings") or [] if c.get("beyond")]}
           for r in nm.get("links") or []}
    try:
        check_receipt(CA, CA.load_json(receipt_path), spec, per, state)
    except CA.Refused as exc:
        return exc.detail
    return None


def read_control(CA, path, acq: dict, request_id: str):
    """(the control as a capture result records it, None) or (None, a refusal dict): the control must exist, be this
    request's, have been rendered at the acquisition's pose, and have found no broken crossing in its own setup."""
    try:
        return check_control(CA, path, acq, request_id), None
    except CA.Refused as exc:
        return None, {"code": exc.code, "detail": exc.detail}


def check_control(CA, path, acq: dict, request_id: str) -> dict:
    if not path:
        raise CA.Refused("navmesh-control-missing", "a navmesh comparison is captured only with its control: run "
                                                    "navmesh-control in the setup without the mod first and pass --control")
    try:
        doc = CA.load_json(Path(path).resolve())
    except (OSError, ValueError) as exc:
        raise CA.Refused("navmesh-control-missing", f"the control {path} could not be read: {exc}")
    if doc.get("kind") != "navmesh-control" or doc.get("request") not in (None, request_id):
        raise CA.Refused("navmesh-control-missing", f"{path} is not this request's navmesh control")
    if doc.get("poseId") != acq.get("poseId"):
        raise CA.Refused("navmesh-control-pose-mismatch", f"the control was rendered at pose {doc.get('poseId')}, not the "
                                                          f"acquisition's {acq.get('poseId')}")
    if doc.get("status") == "broken-crossing-present":
        raise CA.Refused("navmesh-control-crossing-present",
                         f"in the setup without the mod the source still has {doc.get('brokenCrossings')} broken "
                         f"crossing(s) at this camera, so the picture cannot show what the mod changes")
    if doc.get("status") != "no-broken-crossing":
        raise CA.Refused("navmesh-control-missing", f"the control did not finish ({doc.get('status')!r}"
                                                    + (f": {doc.get('reason')}" if doc.get("reason") else "") + ")")
    png = Path(str(doc.get("png") or ""))
    return {"status": doc["status"], "brokenCrossings": doc.get("brokenCrossings"), "poseId": doc.get("poseId"),
            "path": str(Path(path).resolve()), "png": str(png) if doc.get("png") else None,
            "pngSha256": CA.sha256_file(png) if doc.get("png") and png.is_file() else None,
            "pixels": doc.get("pixels")}


def control(CA, a) -> int:
    """The setup without the evaluated mod at the acquisition's camera: its own links re-measured, red only where ITS edges
    still cross past their target's count. A clean control shows no red crossing; one that still has it says so."""
    out = Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    res = {"schemaVersion": 1, "kind": "navmesh-control", "request": a.request, "status": "refused", "reason": None,
           "generatedUtc": CA.utcnow()}

    def finish(code_):
        CA.write_json(out / "control-result.json", res)
        print(f"{res['status']} {a.request}" + (f": {res['reason']}" if res.get("reason") else ""))
        return code_
    shotplan_mod = CA._load_shotplan()
    try:
        acq = CA.load_json(Path(a.acquisition).resolve())
        request, _ = CA.find_request(CA.load_json(Path(a.packet).resolve()), a.request)
        if acq.get("status") != "camera-ready" or not acq.get("navmesh"):
            raise CA.Refused("control-without-camera", "the acquisition is not a camera-ready navmesh camera")
        spec = check_request(CA, request, "auto")
        order = ["--order", str(Path(a.order).resolve())]
        rows = query_links(CA, a, shotplan_mod, spec, out / "nav-link", None, None, order_flags=order)
        per = {}
        for row in rows:
            per[row["target"]] = {"row": row, "crossings": [c for c in row.get("crossings") or [] if c.get("beyond")]}
        cam = acq["camera"]
        flags = nav_flags(spec, per, STATES[0])
        rc, text, png, _rcpt = render_state(CA, a, shotplan_mod, cam["cell"], cam, flags, out / "frame", order_flags=order)
        if rc != 0 or png is None:
            raise CA.Refused("points-renderer-failed", f"the control frame did not render (viewprobe exit {rc})")
    except (OSError, ValueError, CA.Refused) as exc:
        res["reason"] = str(exc)
        return finish(CA.EXIT_REFUSED)
    px = count_pixels(png, text)
    n = sum(len(p["crossings"]) for p in per.values())
    res.update(status="broken-crossing-present" if n else "no-broken-crossing", poseId=acq.get("poseId"), pixels=px,
               brokenCrossings=n, links=rows, png=str(png), flags=flags,
               note="the camera is the acquisition's; the red marks come from this setup's own measurement")
    return finish(CA.EXIT_OK)
