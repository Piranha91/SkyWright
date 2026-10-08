"""Per-plane-edit verdicts for a plane patch, from scored views in the two arms (u: without the patch, p: with it).

    decide(edit, views) -> {"verdict": one of VERDICTS, "reason": str, "statement": str, "shown": bool,
                            "basis": {"gazes": N, "assessed": M, "route": r, "prior in-game pose": k},
                            "unjudgedShare": float | None, "boxLabel": BOX_LABEL | None}
    missed(views, edited) -> [plane formKey, ...]

`edit`: {"formKey", "class", "edits": [kinds], "kind": "Box"|"PortalBox",
         "owners": {"A": [content mod plugins], "B": [plane mod plugins], "patch": patch plugin},
         "winners": {"u": plugin whose version wins without the patch (None when the patch adds it), "p": ...},
         "versions": {"u": {"position": [x, y, z], "active": bool} | None, "p": {...} | None}}
`views`: one per (gaze, arm):
         {"gaze": label, "arm": "u"|"p", "basis": "route" | "prior in-game pose",
          "plane": the report's plane entry for this formKey (position, kind, definedBy, changedBy), or None if absent,
          "score": {"cityInBandPx": the content mod's hidden px, "otherInBandPx": everything else's} or None,
          "census": {"cityShapes": {"predicted-culled", "unassessed", "predicted-drawn"}, "unassessedReasons": {...}} or None,
          "visStatus": {"assessedVisible", "assessedZero", "notAssessed", "noVisibility"}}

Rules (the task's Gate A section 2.5 with Piranha's D3 and D6 and the reviewer's Gate B rulings), in precedence order:
  1. A gaze is usable when, in each arm where the plane is in the report: the report draws it at its winning position
     (position audit, 1 u), the rule predicted it (its rows are not `unassessed: <rule refusal>`), and the visibility
     test ran on every culled content row (no `notAssessed`, no `noVisibility`).
     - any gaze whose position audit fails -> cannot tell: position audit fails;
     - any gaze where the visibility test did not run -> cannot tell: visibility test did not run;
     - a gaze where the rule refuses the plane is dropped; no usable gaze left -> cannot tell: rule refuses a tilted
       plane (tilts) or rule refuses the shape: <reason> (other refusals).
  2. patch placement hides: at a usable gaze the p version hides content (any owner) that the u version did not hide.
  3. taste (no clash): the plane's u winner is the content mod (A's own plane), whatever it hid (G5's reading).
  4. fixes an inter-mod clash: the u winner is the plane mod or the base game, it hid the content mod's content at some
     usable gaze, and at every such gaze the p version hides none of it.
     If it still hides some of it in p -> cannot tell: still hides the content mod's content.
  5. set aside: the edit is a disable of a plane mod or base-game plane that hid nothing in u at every usable gaze.
  6. taste (no clash): everything else; a plane that hid only non-mod content is kept in the machine record (shown False).
Every verdict states its basis: "taste (no clash): nothing hidden at N route views", "fixes an inter-mod clash at the N
route views where it hid"; prior in-game poses are counted and named apart. `unjudgedShare` is the share of content rows
under the plane that were unassessed (shapes the renderer cannot judge), over the usable views: reported beside the
verdict, never a reason for cannot tell (D3). A Box edit carries BOX_LABEL (D6).

`missed` lists planes the patch does not edit that hide the content mod's content in u (reported apart, no verdict).
"""
from __future__ import annotations

VERDICTS = ("fixes an inter-mod clash", "patch placement hides", "taste (no clash)", "set aside", "cannot tell")
BOX_LABEL = "Box verdict: the Box silhouette rule rests on one in-game station"
AUDIT_TOLERANCE = 1.0          # u: the report's plane position against the arm's winning record
TILT_REFUSALS = ("plane orientation outside", "box orientation outside")


def _px(v, owner=None):
    s = (v or {}).get("score") or {}
    if owner == "A":
        return s.get("cityInBandPx", 0)
    if owner == "other":
        return s.get("otherInBandPx", 0)
    return s.get("cityInBandPx", 0) + s.get("otherInBandPx", 0)


def _dist(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def _where(n_route, n_pose):
    out = "%d route view%s" % (n_route, "" if n_route == 1 else "s")
    if n_pose:
        out += " and %d prior in-game pose%s" % (n_pose, "" if n_pose == 1 else "s")
    return out


def _gate(edit, v):
    """Why one arm's view cannot be used: 'audit', 'not run', 'refused: <text>', or None."""
    if v is None or v.get("plane") is None:
        return None
    ver = (edit.get("versions") or {}).get(v["arm"])
    pos = v["plane"].get("position")
    if ver is None or not ver.get("active") or pos is None or _dist(pos, ver["position"]) > AUDIT_TOLERANCE:
        return "audit"
    vs = v.get("visStatus") or {}
    if vs.get("notAssessed", 0) or vs.get("noVisibility", 0):
        return "not run"
    if v.get("refusal"):
        return "refused: " + v["refusal"]
    return None


def decide(edit, views):
    gazes = {}
    for v in views:
        gazes.setdefault(v["gaze"], {})[v["arm"]] = v
    usable, refusals = [], []
    for g, arms in sorted(gazes.items()):
        issues = [i for i in (_gate(edit, arms.get(a)) for a in ("u", "p")) if i]
        if "audit" in issues:
            return _out(edit, "cannot tell", "position audit fails: the render drew the plane away from its winning position",
                        gazes, [])
        if "not run" in issues:
            return _out(edit, "cannot tell", "visibility test did not run", gazes, [])
        if issues:
            refusals += issues
            continue
        usable.append(arms)
    if not usable:
        tilted = any(any(t in r for t in TILT_REFUSALS) for r in refusals)
        reason = "rule refuses a tilted plane" if tilted else (
            "rule refuses the shape: " + refusals[0][len("refused: "):] if refusals else "no view")
        return _out(edit, "cannot tell", reason, gazes, [])
    route = sum(1 for a in usable if (a.get("u") or a.get("p"))["basis"] == "route")
    pose = len(usable) - route
    where = _where(route, pose)
    winner_u = (edit.get("winners") or {}).get("u")
    own_a = winner_u in edit["owners"]["A"]
    hides_more = [a for a in usable if _px(a.get("p")) > _px(a.get("u"))]
    if hides_more:
        return _out(edit, "patch placement hides", "the patch's plane hides content the unpatched order showed", gazes, usable,
                    "patch placement hides content at %s" % _where(
                        sum(1 for a in hides_more if (a.get("u") or a.get("p"))["basis"] == "route"),
                        sum(1 for a in hides_more if (a.get("u") or a.get("p"))["basis"] != "route")))
    hid_any = any(_px(a.get("u")) > 0 for a in usable)
    if own_a:
        st = "taste (no clash): the content mod's own plane, at %s" % where if hid_any else \
            "taste (no clash): nothing hidden at %s" % where
        return _out(edit, "taste (no clash)", "the content mod's own plane", gazes, usable, st)
    hid_a = [a for a in usable if _px(a.get("u"), "A") > 0]
    if hid_a:
        still = [a for a in hid_a if _px(a.get("p"), "A") > 0]
        if still:
            return _out(edit, "cannot tell", "still hides the content mod's content", gazes, usable)
        n_route = sum(1 for a in hid_a if (a.get("u") or a.get("p"))["basis"] == "route")
        return _out(edit, "fixes an inter-mod clash", "the content mod's content hidden in u is drawn with the patch",
                    gazes, usable, "fixes an inter-mod clash at the %s where it hid" % _where(n_route, len(hid_a) - n_route))
    disable = edit.get("class") == "disabled" or "DISABLE" in (edit.get("edits") or [])
    if disable and not hid_any:
        return _out(edit, "set aside", "a disable of a plane that hid nothing", gazes, usable,
                    "set aside: disabled; nothing hidden at %s" % where)
    if hid_any:
        return _out(edit, "taste (no clash)", "hid only non-mod content (machine record)", gazes, usable,
                    "taste (no clash): hid only non-mod content at %s" % where, shown=False)
    return _out(edit, "taste (no clash)", "nothing hidden", gazes, usable, "taste (no clash): nothing hidden at %s" % where)


def _out(edit, verdict, reason, gazes, usable, statement=None, shown=True):
    route = sum(1 for a in usable if (a.get("u") or a.get("p"))["basis"] == "route")
    un = tot = 0
    for a in usable:
        for v in a.values():
            c = (v.get("census") or {}).get("cityShapes") or {}
            un += c.get("unassessed", 0)
            tot += sum(c.values())
    return {"verdict": verdict, "reason": reason, "statement": statement or "%s: %s" % (verdict, reason), "shown": shown,
            "basis": {"gazes": len(gazes), "assessed": len(usable), "route": route, "prior in-game pose": len(usable) - route},
            "unjudgedShare": round(un / tot, 4) if tot else None,
            "boxLabel": BOX_LABEL if edit.get("kind") == "Box" else None}


def missed(views, edited):
    out = []
    for v in views:
        fk = (v.get("plane") or {}).get("formKey")
        if fk and fk not in edited and v["arm"] == "u" and _px(v, "A") > 0 and fk not in out:
            out.append(fk)
    return out
