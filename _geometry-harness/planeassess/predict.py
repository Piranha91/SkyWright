"""Predictive occlusion for a mod being added to an order: which planes will hide its content, which of its planes will
hide content already there, and an existing expert patch read as the answer key. Gate B interface (bodies follow).

Ownership. A plane belongs to the plugin whose version wins in the arm (never `definedBy`). Content belongs to a mod only
when that mod draws it differently from the masters (new, or an override that moves, rotates, scales, swaps the base or
changes the enable state); an override that draws the same is existing content.

    mods = {"A": [plugins], "B": [plugins]}    # the two mods; everything else is the base game
    direction "F": the new mod is A (added to an order with B); "R": the new mod is B.

    band_class(eye, look, centre, radius) -> "inside" | "outside" | "straddle"
        rows a world sphere covers in a level 1200-square, 80-degree render, against the 16:9 band [262, 938)
    in_band_range(row, eye, look, reader=None) -> (lo, hi, how)
        how "mask": exact from the evidence mask; "inside": lo = hi = whole-raster; "outside": 0, 0;
        "straddle": 0 .. whole-raster (the mask is gone: never read as either end)
    rescore_view(report, plane_winner, content_owner, mods, reader=None) -> {"eye", "look", "planes": {formKey: {
            "winner", "owner": "A"|"B"|"base", "refusal", "rows": [{"subject", "shape", "contentOwner", "wholePx",
            "inBandLo", "inBandHi", "how", "cullers": [formKey, ...]}]}}}
        plane_winner: {formKey: plugin winning in the arm}; content_owner: {subject: "A"|"B"} (absent = "base");
        every plane the report lists, every visible predicted-culled row (Assessed, VisiblePixels > 0); the report's
        `eye` and `look` place the camera; reader(mask path) -> RGBA array, or None when the mask is not on disk
    classify(plane_owner, content_owner, direction) -> "shown" | "machine" | "own" | None
        shown: the new mod's plane over the other mod's content, or the other mod's plane over the new mod's content;
        machine: the new mod's plane over base content, or a base plane over the new mod's content; own: new over new
    findings(views, direction, overrides_master=(), without=None) -> [finding]
        views: [{"gaze", "basis", "facts": rescore_view(...)}] in u. A finding is one (plane, class):
        {"plane", "planeOwner", "class", "status", "views": {gaze: {"lo", "hi", "shapes": {key: (lo, hi, owner)}}},
         "inBandLo", "inBandHi", "largestShape", "statement"}
        status "will hide" when some view has lo > 0; "possible only: straddles the band, mask thinned" when only hi > 0;
        "cannot tell: <rule refusal>" when the rule refuses the plane at every view it appears in with content behind.
        A plane in `overrides_master` (the new mod's override of a master plane, direction R) is shown only where
        `without` (facts of the order without the new mod, by gaze) shows the master version not hiding the same shapes;
        with no `without` view it is "cannot tell: needs the render without the new mod".
    answer_key(finding, p_facts, p_active, p_edits, evidence=None) -> {"outcome", "detail"}
        p_facts: {gaze: rescore_view facts in p, or None}; p_active: formKeys active in p; p_edits: {formKey: [kinds]}
        outcome "hit" | "partly resolved" | "open: cannot tell offline" | "open: confirmed in game" | "false alarm" |
        "resolution cannot tell" | "resolution needs a p render"; decided shape by shape, never by totals.
        evidence: {formKey: "confirmed in game" | "engine does not hide"} - the only routes to those two outcomes.
    drawn_content(mod_rows, master_rows) -> set of formKeys the mod draws differently from the masters
        mod_rows / master_rows: planeassess.Plugin rows (non-plane placed records); master_rows by formKey
    shadow_crosses(shape, eye, point, growth) -> bool
        the segment eye -> point crosses the plane (PortalBox mid-quad, Box volume) grown by `growth` u
    scope_planes(planes, refs, eye, bearing, rule, reach, growth) -> [formKey, ...]
        planes: [(formKey, shape)] not owned by the content's mod; refs: [position]; the rule-decidable planes within
        `reach` of the eye that have a reference in the level frustum (grown by `growth`) in their shadow
    blind_spots(non_a_edits, shown_planes, evidence) -> [{"plane", "outcome", "reason"}]
        outcome "blind spot" (an edited plane with evidence it hid the content, not predicted) or
        "edit without a predicted clash" (no evidence either way)

A refs entry may be (position, radius): a reference larger than `growth` grows the test by its own radius.
"""
from __future__ import annotations

import math
import os
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import planeassess as P  # noqa: E402
import occlscore as S  # noqa: E402

BAND = (262, 938)
SIZE = 1200
FOV = 80.0
SHAPE_KINDS = ("MOVE", "ROTATE", "SCALE", "BOUNDS", "BASE", "ENABLE", "DISABLE")


# ------------------------------------------------------------------ the 16:9 band without a mask
def _camera(eye, look):
    f = [look[i] - eye[i] for i in range(3)]
    n = math.sqrt(sum(x * x for x in f))
    f = [x / n for x in f]
    r = [f[1], -f[0], 0.0]
    rn = math.sqrt(sum(x * x for x in r)) or 1.0
    r = [x / rn for x in r]
    up = [r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0]]
    return f, up


def band_class(eye, look, centre, radius, size=SIZE, fov=FOV, band=BAND):
    """Rows a world sphere covers, exactly: the image rows whose plane through the eye (about the camera's right axis)
    meets the sphere, for a square raster whose field is `fov` on both axes."""
    f, up = _camera(eye, look)
    d = [centre[i] - eye[i] for i in range(3)]
    df, du = sum(d[i] * f[i] for i in range(3)), sum(d[i] * up[i] for i in range(3))
    rho = math.hypot(df, du)
    if rho <= radius:
        return "straddle"
    th, al = math.atan2(du, df), math.asin(radius / rho)
    lo, hi = th - al, th + al
    if hi >= math.pi / 2 or lo <= -math.pi / 2:
        return "straddle"
    foc = (size / 2) / math.tan(math.radians(fov) / 2)
    y_top, y_bot = size / 2 - foc * math.tan(hi), size / 2 - foc * math.tan(lo)
    if y_top >= band[0] and y_bot < band[1]:
        return "inside"
    if y_bot < band[0] or y_top >= band[1]:
        return "outside"
    return "straddle"


def _disk_reader(path):
    if not path or not os.path.exists(path):
        return None
    import numpy as np
    from PIL import Image
    return np.asarray(Image.open(path).convert("RGBA"))


def in_band_range(row, eye, look, reader=None):
    whole = (row.get("visibility") or {}).get("VisiblePixels", 0)
    m = row.get("visibilityMask")
    a = (reader or _disk_reader)(m) if m else None
    if a is not None:
        g = int((a[BAND[0]:BAND[1], :, 1] > 0).sum())
        return g, g, "mask"
    ws = row.get("worldSphere") or {}
    how = band_class(eye, look, ws["center"], ws.get("radius", 0.0)) if ws.get("center") else "straddle"
    if how == "inside":
        return whole, whole, how
    if how == "outside":
        return 0, 0, how
    return 0, whole, "straddle"


# ------------------------------------------------------------------ re-score: every plane of a report
def _owner(plugin, mods):
    if plugin is None:
        return "unknown"
    if plugin in mods.get("A", ()):
        return "A"
    if plugin in mods.get("B", ()):
        return "B"
    return "base"


def rescore_view(report, plane_winner, content_owner, mods, reader=None):
    d = report
    eye, look = d["eye"], d["look"]
    out = {"eye": eye, "look": look, "label": d.get("viewLabel"), "planes": {}}
    allp = {pl["formKey"] for pl, _ in S._rows(d)}
    under, _ = S.unreadable(d, allp)
    for pl, cands in S._rows(d):
        fk = pl["formKey"]
        w = plane_winner.get(fk)
        rows, unassessed = [], defaultdict(int)
        for c in cands:
            if not c.get("predictedCulled"):
                continue
            v = c.get("visibility") or {}
            co = content_owner.get(c.get("subject"), "base")
            if not v.get("Assessed"):
                unassessed[co] += 1
                continue
            if v.get("VisiblePixels", 0) <= 0:
                continue
            lo, hi, how = in_band_range(c, eye, look, reader)
            rows.append({"subject": c.get("subject"), "shape": c.get("shape"), "contentOwner": co,
                         "wholePx": v["VisiblePixels"], "inBandLo": lo, "inBandHi": hi, "how": how,
                         "cullers": list(c.get("allCullingPlanes") or [fk]),
                         "sphere": (c.get("worldSphere") or {}).get("center"),
                         "radius": (c.get("worldSphere") or {}).get("radius")})
        out["planes"][fk] = {"winner": w, "owner": _owner(w, mods), "definedBy": pl.get("definedBy"),
                             "refusal": S.refusal(d, fk), "rows": rows, "unassessedCulled": dict(unassessed),
                             "unreadableUnder": under.get(fk, [])}
    return out


# ------------------------------------------------------------------ findings
def classify(plane_owner, content_owner, direction):
    new, other = ("A", "B") if direction == "F" else ("B", "A")
    if plane_owner == new and content_owner == new:
        return "own"
    if (plane_owner, content_owner) in ((new, other), (other, new)):
        return "shown"
    if (plane_owner, content_owner) in ((new, "base"), ("base", new)):
        return "machine"
    return None


def _key(r):
    return "%s|%s" % (r["subject"], r["shape"])


def _hidden_by(facts, plane):
    """shape key -> (lo, hi) hidden by `plane` in these facts"""
    pl = (facts or {}).get("planes", {}).get(plane) or {}
    return {_key(r): (r["inBandLo"], r["inBandHi"]) for r in pl.get("rows", []) if r["inBandHi"] > 0}


def findings(views, direction, overrides_master=(), without=None):
    acc = {}
    for v in views:
        gaze, facts = v["gaze"], v["facts"]
        for fk, pl in facts["planes"].items():
            groups = defaultdict(list)
            for r in pl["rows"]:
                if r["inBandHi"] <= 0:
                    continue
                cls = classify(pl["owner"], r["contentOwner"], direction)
                if cls:
                    groups[cls].append(r)
            for cls, rows in groups.items():
                if cls == "shown" and fk in overrides_master:
                    w = (without or {}).get(gaze)
                    if w is None:
                        f = acc.setdefault((fk, cls), _new(fk, pl["owner"], cls))
                        f["needsWithout"].append(gaze)
                        continue
                    before = _hidden_by(w, fk)
                    already = [r for r in rows if _key(r) in before]
                    rows = [r for r in rows if _key(r) not in before]
                    if already:
                        f = acc.setdefault((fk, "machine"), _new(fk, pl["owner"], "machine"))
                        f["alreadyHiddenWithout"] = True
                        _add(f, gaze, v.get("basis", "route"), already, pl.get("refusal"))
                    if not rows:
                        continue
                f = acc.setdefault((fk, cls), _new(fk, pl["owner"], cls))
                _add(f, gaze, v.get("basis", "route"), rows, pl.get("refusal"))
    return [_finish(f) for f in sorted(acc.values(), key=lambda f: (f["class"], f["plane"]))]


def _new(fk, owner, cls):
    return {"plane": fk, "planeOwner": owner, "class": cls, "views": {}, "refusedViews": {}, "needsWithout": []}


def _add(f, gaze, basis, rows, refusal):
    if refusal:
        f["refusedViews"][gaze] = refusal
        return
    shapes = {_key(r): (r["inBandLo"], r["inBandHi"], r["contentOwner"]) for r in rows}
    f["views"][gaze] = {"basis": basis, "lo": sum(s[0] for s in shapes.values()),
                        "hi": sum(s[1] for s in shapes.values()), "shapes": shapes,
                        "cullers": {_key(r): r["cullers"] for r in rows}}


def _where(views):
    route = sum(1 for x in views.values() if x["basis"] == "route")
    pose = len(views) - route
    s = "%d route view%s" % (route, "" if route == 1 else "s")
    return s + (" and %d prior in-game pose%s" % (pose, "" if pose == 1 else "s") if pose else "")


def _finish(f):
    vs = f["views"]
    certain = {g: x for g, x in vs.items() if x["lo"] > 0}
    possible = {g: x for g, x in vs.items() if x["hi"] > 0}
    f["inBandLo"] = sum(x["lo"] for x in vs.values())
    f["inBandHi"] = sum(x["hi"] for x in vs.values())
    big = max(((k, s[0], s[1]) for x in vs.values() for k, s in x["shapes"].items()), key=lambda t: (t[1], t[2]),
              default=None)
    f["largestShape"] = big and {"shape": big[0], "inBandLo": big[1], "inBandHi": big[2]}
    if certain:
        f["status"] = "will hide"
        f["statement"] = "will hide at %s (%d px in band%s)" % (
            _where(certain), f["inBandLo"], "" if f["inBandHi"] == f["inBandLo"] else ", up to %d" % f["inBandHi"])
    elif possible:
        f["status"] = "possible only: straddles the band, mask thinned"
        f["statement"] = "possibly hides at %s (0 to %d px in band; masks thinned)" % (_where(possible), f["inBandHi"])
    elif f["needsWithout"]:
        f["status"] = "cannot tell: needs the render without the new mod"
        f["statement"] = "cannot tell at %d view%s: the plane overrides a base-game plane and the render without the new " \
                         "mod is missing" % (len(f["needsWithout"]), "" if len(f["needsWithout"]) == 1 else "s")
    elif f["refusedViews"]:
        f["status"] = "cannot tell: rule refuses the plane (%s)" % sorted(set(f["refusedViews"].values()))[0]
        f["statement"] = f["status"]
    else:
        f["status"] = "nothing hidden"
        f["statement"] = "nothing hidden"
    if f.get("alreadyHiddenWithout"):
        f["statement"] += "; already hidden by the base-game version without the new mod"
    return f


# ------------------------------------------------------------------ answer key
def answer_key(finding, p_facts, p_active, p_edits, evidence=None):
    plane = finding["plane"]
    edits = set(p_edits.get(plane) or [])
    certain = finding.get("status") == "will hide"
    per = {}
    for gaze, v in finding["views"].items():
        want = {k for k, s in v["shapes"].items() if (s[0] > 0 if certain else s[1] > 0)}
        if not want:
            continue
        pf = (p_facts or {}).get(gaze)
        if pf is not None:
            pl = pf["planes"].get(plane)
            if pl is not None and pl.get("refusal"):
                per[gaze] = ("refused", want, set())
                continue
            still, maybe = set(), set()
            for opl in pf["planes"].values():
                for r in opl.get("rows", []):
                    k = _key(r)
                    if k in want:
                        if r["inBandLo"] > 0:
                            still.add(k)
                        elif r["inBandHi"] > 0:
                            maybe.add(k)
            per[gaze] = ("unsure" if maybe - still else "read", want, still)
            continue
        if plane not in p_active:
            still, unsure = set(), False
            for k in want:
                others = [c for c in v["cullers"].get(k, []) if c != plane and c in p_active]
                if any(set(p_edits.get(c) or []) & set(SHAPE_KINDS) for c in others):
                    unsure = True
                elif others:
                    still.add(k)
            per[gaze] = ("needs p" if unsure else "read", want, still)
        elif edits & set(SHAPE_KINDS):
            per[gaze] = ("needs p", want, set())
        else:
            per[gaze] = ("read", want, set(want))
    states = {s for s, _, _ in per.values()}
    n_want = sum(len(w) for _, w, _ in per.values())
    n_still = sum(len(st) for _, _, st in per.values())
    if "needs p" in states:
        out = "resolution needs a p render"
    elif "refused" in states:
        out = "resolution cannot tell"
    elif "unsure" in states:
        out = "resolution cannot tell"
    elif n_still == 0:
        out = "hit"
    elif n_still < n_want:
        out = "partly resolved"
    else:
        e = (evidence or {}).get(plane)
        out = {"confirmed in game": "open: confirmed in game", "engine does not hide": "false alarm"}.get(
            e, "open: cannot tell offline")
    return {"outcome": out, "detail": {g: {"state": s, "shapes": len(w), "stillHidden": sorted(st)}
                                       for g, (s, w, st) in sorted(per.items())}}


def blind_spots(non_a_edits, shown_planes, evidence):
    out = []
    for fk, kinds in sorted(non_a_edits.items()):
        if fk in shown_planes:
            continue
        if fk in (evidence or {}):
            out.append({"plane": fk, "edits": list(kinds), "outcome": "blind spot", "reason": evidence[fk]})
        else:
            out.append({"plane": fk, "edits": list(kinds), "outcome": "edit without a predicted clash",
                        "reason": "no evidence that the plane hid the content at any view looked at"})
    return out


# ------------------------------------------------------------------ records and scope
def drawn_content(mod_rows, master_rows):
    out = set()
    for r in mod_rows:
        if r.get("pos") is None or P.disabled(r):
            continue
        m = master_rows.get(r["formKey"])
        if m is None or P.disabled(m) or set(P.edit_kinds(m, r)) & {"MOVE", "ROTATE", "SCALE", "BASE"}:
            out.add(r["formKey"])
    return out


def shadow_crosses(shape, eye, point, growth):
    R = P.refr_rotation(*shape["rot"])
    c, h = shape["pos"], shape["half"]

    def loc(q):
        return [sum((q[i] - c[i]) * R[k][i] for i in range(3)) for k in range(3)]
    a, b = loc(eye), loc(point)
    if shape["kind"] == "PortalBox":
        if a[1] == b[1] or (a[1] > 0) == (b[1] > 0):
            return False
        t = a[1] / (a[1] - b[1])
        x, z = a[0] + t * (b[0] - a[0]), a[2] + t * (b[2] - a[2])
        return abs(x) <= h[0] + growth and abs(z) <= h[2] + growth
    t0, t1 = 0.0, 1.0
    for k in range(3):
        d, lim = b[k] - a[k], h[k] + growth
        if abs(d) < 1e-9:
            if abs(a[k]) > lim:
                return False
            continue
        u, w = (-lim - a[k]) / d, (lim - a[k]) / d
        t0, t1 = max(t0, min(u, w)), min(t1, max(u, w))
        if t0 > t1:
            return False
    return True


def _nearest_in_view(shape, eye, bearing):
    if shape["kind"] == "Box" and P.eye_inside_box(shape, eye):
        return 0.0
    planes, best = P.frustum(bearing), math.inf
    for poly in P.shape_polys(shape):
        c = P._clip(poly, eye, planes)
        if c:
            best = min(best, P.point_poly_dist(eye, c))
    return best


def _in_frustum(p, eye, bearing, reach, growth):
    d = [p[i] - eye[i] for i in range(3)]
    if math.sqrt(sum(x * x for x in d)) > reach + growth:
        return False
    for a, b in P.frustum(bearing):
        n = math.sqrt(sum(x * x for x in a))
        if (sum(a[i] * d[i] for i in range(3)) + b) / n < -growth:
            return False
    return True


def scope_planes(planes, refs, eye, bearing, rule, reach, growth):
    rs = []
    for r in refs:
        pos, rad = (r[0], r[1]) if len(r) == 2 and isinstance(r[0], (list, tuple)) else (r, 0.0)
        g = max(growth, rad)
        if _in_frustum(pos, eye, bearing, reach, g):
            rs.append((pos, g))
    out = []
    for fk, sh in planes:
        if _nearest_in_view(sh, eye, bearing) > reach or P.rule_issue(sh, eye, rule):
            continue
        if any(shadow_crosses(sh, eye, pos, g) for pos, g in rs):
            out.append(fk)
    return sorted(out)


def record_owners(g, order):
    """From the records of one arm's order: {"winner": {plane formKey: plugin}, "active": {plane formKeys active},
    "content": {subject: "A"|"B"} (drawn differently from the masters), "mods", "overridesMaster": {"A": set, "B": set}}
    (the planes a mod's version wins although a master defines them)."""
    mods = {"A": list(g["owners"]["A"]), "B": list(g["owners"]["B"])}
    rows = {n: list(P.Plugin(g["_rel"](g["pluginFiles"][n])).placed()) for n in order}
    winner, wrow, master = {}, {}, {}
    for n in order:
        for r in rows[n]:
            if P.is_plane(r):
                winner[r["formKey"]], wrow[r["formKey"]] = n, r
            elif n not in mods["A"] + mods["B"]:
                master[r["formKey"]] = r
    content = {}
    for m in ("A", "B"):
        for n in mods[m]:
            if n in rows:
                for fk in drawn_content([r for r in rows[n] if not P.is_plane(r)], master):
                    content[fk] = m
    over = {m: {fk for fk, n in winner.items() if n in mods[m] and fk.split(":", 1)[1] not in mods[m]} for m in mods}
    return {"winner": winner, "active": {fk for fk, r in wrow.items() if P.active(r)}, "content": content, "mods": mods,
            "overridesMaster": over}
