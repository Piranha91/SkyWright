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

Gate C state: NOT implemented yet. `decide` answers `cannot tell: not implemented` and `missed` lists nothing.
"""
from __future__ import annotations

VERDICTS = ("fixes an inter-mod clash", "patch placement hides", "taste (no clash)", "set aside", "cannot tell")
BOX_LABEL = "Box verdict: the Box silhouette rule rests on one in-game station"
NOT_IMPLEMENTED = {"verdict": "cannot tell", "reason": "not implemented", "statement": "cannot tell: not implemented",
                   "shown": True, "basis": None, "unjudgedShare": None, "boxLabel": None}


def decide(edit, views):
    return dict(NOT_IMPLEMENTED)


def missed(views, edited):
    return []
