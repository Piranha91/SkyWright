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
  2. patch placement hides: at a usable gaze the p version hides a shape (any owner) that no plane hid in u at that
     gaze. Decided shape by shape ("subject|shape"), never by comparing totals. If the edit also stops hiding the
     content mod's content it hid in u, that is stated beside the verdict (`also`), never dropped.
  3. taste (no clash): the plane's u winner is the content mod (A's own plane), whatever it hid (G5's reading).
  4. fixes an inter-mod clash: the u winner is the plane mod (B's override wins, which covers B re-enabling it), it hid
     content-mod shapes at some usable gaze, and at every such gaze the p version hides none of those shapes.
     If it still hides some of them in p -> cannot tell: still hides the content mod's content.
     A base-game u winner (neither mod touched it) hiding the content mod's content is the content mod's own matter:
     taste (no clash), "corrects the content mod against the base game", kept in the machine record (shown False).
  5. set aside: the edit is a disable of a plane-mod or base-game plane that hid nothing in u at every usable gaze.
  6. taste (no clash): everything else; a plane that hid only non-mod content is kept in the machine record (shown False).
Ownership is never collapsed into a total: the plane's owner comes from the records (`winners`), the content's owner
from each shape (`owner`: "A" = the content mod's placed records), and every rule above reads shapes, not sums.
`visStatus` is scoped to the content rows under this plane (occlscore.vis_status filters by plane), not the whole view.
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


def _hidden(v, owner=None):
    """Shapes this view's plane hides (in-band px > 0), keyed "subject|shape" -> (px, owner); `owner` filters A/other.
    Without a per-shape list (older inputs) the view's totals stand in as one shape per owner."""
    if v is None:
        return {}
    shapes = v.get("shapes")
    if shapes is None:
        s = v.get("score") or {}
        shapes = [{"subject": "*", "shape": "content", "inBandPx": s.get("cityInBandPx", 0), "owner": "A"},
                  {"subject": "*", "shape": "other", "inBandPx": s.get("otherInBandPx", 0), "owner": "other"}]
    return {"%s|%s" % (x["subject"], x["shape"]): (x["inBandPx"], x["owner"]) for x in shapes
            if x["inBandPx"] > 0 and (owner is None or x["owner"] == owner)}


def _basis_of(arms):
    return (arms.get("u") or arms.get("p"))["basis"]


def _count(gz):
    route = sum(1 for a in gz if _basis_of(a) == "route")
    return route, len(gz) - route


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
    where = _where(*_count(usable))
    winner_u = (edit.get("winners") or {}).get("u")
    owners = edit["owners"]
    own_a, own_b = winner_u in owners["A"], winner_u in owners["B"]
    base = winner_u is not None and not own_a and not own_b and winner_u != owners.get("patch")

    # per gaze, shape by shape: what the p version newly hides (visible in u: hidden by no plane there), and what of
    # the content mod's content the u version hid that p still hides or now shows
    new_hide, hid_a, still_a = [], [], []
    for a in usable:
        u, p = a.get("u"), a.get("p")
        hu, hp = _hidden(u), _hidden(p)
        anywhere_u = set((u or p or {}).get("hiddenAnywhere") or []) | set(hu)
        if any(k not in anywhere_u for k in hp):
            new_hide.append(a)
        au = _hidden(u, "A")
        if au:
            hid_a.append(a)
            if any(k in hp for k in au):
                still_a.append(a)
    fixed_a = [a for a in hid_a if a not in still_a]
    fix_note = "fixes the content mod's content it hid in u at the %s" % _where(*_count(fixed_a)) if fixed_a else None
    hid_any = any(_hidden(a.get("u")) for a in usable)

    if new_hide:
        also = [fix_note] if fix_note and own_b else []
        if fixed_a and not own_b:
            also.append("shows again content it hid in u at the %s (not an inter-mod fix: %s plane)"
                        % (_where(*_count(fixed_a)), "the content mod's own" if own_a else "a base-game"))
        return _out(edit, "patch placement hides", "the patch's plane hides content the unpatched order showed", gazes, usable,
                    "patch placement hides content at %s" % _where(*_count(new_hide)), also=also)
    if own_a:
        st = "taste (no clash): the content mod's own plane, at %s" % where if hid_any else \
            "taste (no clash): nothing hidden at %s" % where
        return _out(edit, "taste (no clash)", "the content mod's own plane", gazes, usable, st)
    if hid_a and own_b:
        if still_a:
            return _out(edit, "cannot tell", "still hides the content mod's content", gazes, usable,
                        also=[fix_note] if fix_note else [])
        return _out(edit, "fixes an inter-mod clash", "the content mod's content hidden in u is drawn with the patch",
                    gazes, usable, "fixes an inter-mod clash at the %s where it hid" % _where(*_count(hid_a)))
    if hid_a and base:
        if still_a:
            return _out(edit, "taste (no clash)", "a base-game plane still hides the content mod's content (machine record)",
                        gazes, usable, "taste (no clash): a base-game plane hiding the content mod's content at %s" % where,
                        shown=False)
        return _out(edit, "taste (no clash)", "corrects the content mod against the base game (machine record)", gazes, usable,
                    "taste (no clash): corrects the content mod against the base game at the %s where it hid"
                    % _where(*_count(hid_a)), shown=False)
    disable = edit.get("class") == "disabled" or "DISABLE" in (edit.get("edits") or [])
    if disable and not hid_any:
        return _out(edit, "set aside", "a disable of a plane that hid nothing", gazes, usable,
                    "set aside: disabled; nothing hidden at %s" % where)
    if hid_any:
        return _out(edit, "taste (no clash)", "hid only non-mod content (machine record)", gazes, usable,
                    "taste (no clash): hid only non-mod content at %s" % where, shown=False)
    return _out(edit, "taste (no clash)", "nothing hidden", gazes, usable, "taste (no clash): nothing hidden at %s" % where)


def _out(edit, verdict, reason, gazes, usable, statement=None, shown=True, also=None):
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
            "boxLabel": BOX_LABEL if edit.get("kind") == "Box" else None, "also": [x for x in (also or []) if x]}


def missed(views, edited):
    out = []
    for v in views:
        fk = (v.get("plane") or {}).get("formKey")
        if fk and fk not in edited and v["arm"] == "u" and _px(v, "A") > 0 and fk not in out:
            out.append(fk)
    return out
