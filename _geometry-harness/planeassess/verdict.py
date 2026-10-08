"""Per-plane-edit verdicts for a plane patch, from scored views in the two arms (u: without the patch, p: with it).

The contract the known answers hold it to (decision rules: the task's Gate A, section 2.5):

    decide(edit, views) -> {"verdict": one of VERDICTS, "reason": str, "shown": bool}
    missed(views, edited) -> [plane formKey, ...]

`edit`: {"formKey", "class", "edits": [kinds], "kind": "Box"|"PortalBox",
         "owners": {"A": [content mod plugins], "B": [plane mod plugins], "patch": patch plugin},
         "versions": {"u": {"position": [x, y, z], "active": bool} | None, "p": {...} | None}}
`views`: one per (gaze, arm) where the plane is in view:
         {"gaze": label, "arm": "u"|"p",
          "plane": the report's plane entry for this formKey (position, kind, definedBy, changedBy) or None,
          "score": {"cityInBandPx": content mod's hidden px, "otherInBandPx": everything else's},
          "census": {"cityShapes": {"predicted-culled", "unassessed", "predicted-drawn"}, "unassessedReasons": {...}},
          "visStatus": {"assessedVisible", "assessedZero", "notAssessed", "noVisibility"}}

`shown` is False for a finding kept in the machine record only (a plane mod hiding vanilla content: not between mods).

Gate B state: the decision is NOT implemented. `decide` answers `cannot tell: not implemented` and `missed` lists
nothing, so every known answer is red on its own assertion. Gate C replaces both bodies.
"""
from __future__ import annotations

VERDICTS = ("fixes an inter-mod clash", "patch placement hides", "taste (no clash)", "set aside", "cannot tell")
NOT_IMPLEMENTED = {"verdict": "cannot tell", "reason": "not implemented", "shown": True}


def decide(edit, views):
    return dict(NOT_IMPLEMENTED)


def missed(views, edited):
    return []
