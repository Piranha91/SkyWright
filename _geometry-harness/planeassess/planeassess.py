#!/usr/bin/env python3
"""Occlusion assessment for plane patches: a repeatable driver for any patch that edits occlusion planes.

A plane patch adds, moves, resizes, disables or restores placed occlusion primitives (XPRM: Box, PortalBox).
A plane hides geometry but draws none, so the question per plane edit is whether the edited plane hides,
or stops hiding, content that another mod placed. This driver answers it in steps; the steps here read
records only (no renders, no game):

    planeassess.py selftest                                  # known answers for the frustum and the rule's shapes
    planeassess.py patch --group G.json --out DIR            # step 1: every plane edit, classed, with its prior owner
    planeassess.py stations --group G.json --work DIR        # station sampling through the deployed viewprobe
    planeassess.py scope --group G.json --out DIR            # step 2: the gazes where an edited plane is in view

A group definition (JSON, kept with the run, never in this tree) names the two load orders (u: without the
patch, p: with it, last plugin wins), each plugin's file, the patch plugin, the worldspaces with their station
files, and optionally the expected edit list to reconcile against. Its schema is `group_schema()` below.

The frustum test and its known answers come from the first offline occlusion assessment (route stations,
80 degree horizontal field, 16:9, near 15 u, reach 4,096 u). The plugin reader is an independent reader written
from the file format. The rule's shape classes mirror viewprobe's ShapeOcclusion.Shadow and
HarnessShared.PortalBoxOrientation; a refusal here is a scope-time prediction that the render confirms.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import struct
import subprocess
import sys
import time
import zlib
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import runlog  # noqa: E402
sys.path.insert(0, str(HERE))

HFOV, ASPECT, NEAR, REACH = 80.0, 16.0 / 9.0, 15.0, 4096.0
PRIM_KIND = {1: "Box", 3: "PortalBox"}
PLANE_MARKER = "000017:Skyrim.esm"
PLAYER = "000014:Skyrim.esm"
POS_EPS, ROT_EPS = 1e-4, 1e-5


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


# ------------------------------------------------------------------ plugin reader (placed references only)
FLAG_DELETED, FLAG_PERSISTENT, FLAG_INITIALLY_DISABLED, FLAG_COMPRESSED = 0x20, 0x400, 0x800, 0x40000
PLACED_TYPES = (b"REFR", b"ACHR", b"PARW", b"PBAR", b"PBEA", b"PCON", b"PFLA", b"PGRE", b"PHZD", b"PMIS")


def _u32(b, o=0):
    return struct.unpack_from("<I", b, o)[0]


class Plugin:
    """Reads TES4 masters and every placed record with the fields an occlusion question needs: base, position and
    rotation (radians), scale, XPRM (half bounds, colour, type), XESP (enable parent), XOCP, XTEL, header flags."""

    def __init__(self, path):
        self.path = str(path)
        with open(path, "rb") as fh:
            self.buf = fh.read()
        self.sha256 = hashlib.sha256(self.buf).hexdigest()
        self.name = os.path.basename(path)
        self.masters, self.header_flags = [], 0
        b = self.buf
        if b[0:4] != b"TES4":
            raise ValueError("%s is not a Bethesda plugin" % path)
        size = _u32(b, 4)
        self.header_flags = _u32(b, 8)
        body, o = b[24:24 + size], 0
        while o + 6 <= len(body):
            sig, ln = body[o:o + 4], struct.unpack_from("<H", body, o + 4)[0]
            if sig == b"MAST":
                self.masters.append(body[o + 6:o + 6 + ln].rstrip(b"\x00").decode("cp1252", "replace"))
            o += 6 + ln
        self.tes4_end = 24 + size

    def formkey(self, formid):
        idx, local = formid >> 24, formid & 0xFFFFFF
        return "%06X:%s" % (local, self.masters[idx] if idx < len(self.masters) else self.name)

    def placed(self):
        yield from self._walk(self.tes4_end, len(self.buf), None, None)

    def _walk(self, start, end, cell, world):
        b, o = self.buf, start
        while o + 24 <= end:
            sig, size = b[o:o + 4], _u32(b, o + 4)
            if sig == b"GRUP":
                gtype, label = struct.unpack_from("<i", b, o + 12)[0], _u32(b, o + 8)
                yield from self._walk(o + 24, o + size, label if gtype in (6, 7, 8, 9) else cell,
                                      label if gtype == 1 else world)
                o += size
                continue
            flags, formid = _u32(b, o + 8), _u32(b, o + 12)
            if sig == b"CELL":
                cell = formid
            elif sig in PLACED_TYPES:
                body = b[o + 24:o + 24 + size]
                if flags & FLAG_COMPRESSED:
                    body = zlib.decompress(body[4:])
                yield self._placed(sig, formid, flags, body, cell, world)
            o += 24 + size

    def _placed(self, sig, formid, flags, body, cell, world):
        out = {"formKey": self.formkey(formid), "sig": sig.decode("ascii"), "flagsRaw": flags,
               "deleted": bool(flags & FLAG_DELETED), "initiallyDisabled": bool(flags & FLAG_INITIALLY_DISABLED),
               "base": None, "pos": None, "rot": None, "scale": 1.0, "prim": None, "enableParent": None,
               "occlusionPlane": None, "teleport": None,
               "cell": self.formkey(cell) if cell is not None else None,
               "worldspace": self.formkey(world) if world is not None else None}
        o, big = 0, None
        while o + 6 <= len(body):
            sub, ln = body[o:o + 4], struct.unpack_from("<H", body, o + 4)[0]
            if sub == b"XXXX":
                big = _u32(body, o + 6)
                o += 6 + ln
                continue
            if big is not None:
                ln, big = big, None
            data = body[o + 6:o + 6 + ln]
            if sub == b"NAME" and len(data) >= 4:
                out["base"] = self.formkey(_u32(data))
            elif sub == b"DATA" and len(data) >= 24:
                v = struct.unpack_from("<6f", data, 0)
                out["pos"], out["rot"] = list(v[:3]), list(v[3:])
            elif sub == b"XSCL" and len(data) >= 4:
                out["scale"] = struct.unpack_from("<f", data, 0)[0]
            elif sub == b"XPRM" and len(data) >= 32:
                out["prim"] = {"bounds": list(struct.unpack_from("<3f", data, 0)),
                               "colour": list(struct.unpack_from("<3f", data, 12)),
                               "unknown": struct.unpack_from("<f", data, 24)[0], "typeRaw": _u32(data, 28)}
            elif sub == b"XESP" and len(data) >= 8:
                out["enableParent"] = {"ref": self.formkey(_u32(data)), "flagsRaw": _u32(data, 4)}
            elif sub == b"XTEL" and len(data) >= 4:
                out["teleport"] = self.formkey(_u32(data))
            elif sub == b"XOCP":
                out["occlusionPlane"] = data.hex()
            o += 6 + ln
        return out


def is_plane(row):
    return row["prim"] is not None or row["base"] == PLANE_MARKER


def kind_of(row):
    return PRIM_KIND.get(row["prim"]["typeRaw"], "type%d" % row["prim"]["typeRaw"]) if row and row.get("prim") else None


def disabled(row):
    """Disabled or deleted at load: the scorecard's DISABLE idiom (deleted, initially disabled, or enable parent =
    the player with the opposite flag)."""
    ep = row.get("enableParent") or {}
    return bool(row["deleted"] or row["initiallyDisabled"] or (ep.get("ref") == PLAYER and ep.get("flagsRaw", 0) & 1))


def active(row):
    return row is not None and not disabled(row) and row["pos"] is not None and row.get("prim") is not None


# ------------------------------------------------------------------ edit kinds (one edit = record x kind)
def _rot_differs(a, b):
    return any(abs((y - x + math.pi) % (2 * math.pi) - math.pi) > ROT_EPS for x, y in zip(a, b))


def edit_kinds(u, p):
    """The scorecard's edit kinds for a patch version p over the unpatched winner u (None when no plugin in the
    unpatched order defines the record). A DISABLE absorbs the idiom's position, rotation and parent changes."""
    if u is None:
        return ["ADD"]
    if disabled(p) and not disabled(u):
        return ["DISABLE"]
    out = []
    if disabled(u) and not disabled(p):
        out.append("ENABLE")
    if u["pos"] and p["pos"] and max(abs(x - y) for x, y in zip(u["pos"], p["pos"])) > POS_EPS:
        out.append("MOVE")
    if u["rot"] and p["rot"] and _rot_differs(u["rot"], p["rot"]):
        out.append("ROTATE")
    if abs((u["scale"] or 1.0) - (p["scale"] or 1.0)) > 1e-6:
        out.append("SCALE")
    if u["base"] != p["base"]:
        out.append("BASE")
    up, pp = u.get("prim") or {}, p.get("prim") or {}
    if up.get("bounds") and pp.get("bounds") and max(abs(x - y) for x, y in zip(up["bounds"], pp["bounds"])) > POS_EPS:
        out.append("BOUNDS")
    if up.get("typeRaw") != pp.get("typeRaw") or up.get("colour") != pp.get("colour") or up.get("unknown") != pp.get("unknown"):
        out.append("FIELD:Primitive")
    if u.get("enableParent") != p.get("enableParent") and "ENABLE" not in out:
        out.append("ENABLE-PARENT")
    if (u["flagsRaw"] ^ p["flagsRaw"]) & ~(FLAG_DELETED | FLAG_INITIALLY_DISABLED | FLAG_COMPRESSED) & 0xFFFF:
        out.append("RECORD-FLAGS")
    if u.get("occlusionPlane") != p.get("occlusionPlane"):
        out.append("FIELD:OcclusionPlane")
    if u.get("teleport") != p.get("teleport"):
        out.append("FIELD:Teleport")
    return out


def same_version(a, b):
    """Two versions place the same plane the same way (geometry, kind, enable state)."""
    if a is None or b is None:
        return a is b
    return not edit_kinds(a, b) and disabled(a) == disabled(b)


def shape_differs(a, b):
    """The plane the engine would cull with differs: enable state, primitive type, position, rotation, scale or
    bounds. Colour, the XOCP subrecord and a disabled record's parent are not a difference for this instrument:
    XOCP (Mutagen's OcclusionPlane, a Bounding: width, height, position, rotation) is a CK-era copy of the primitive's
    half bounds that goes stale when a plane is resized, and viewprobe never reads it (header of
    _geometry-harness/viewprobe/OcclusionPlanes.cs). Whether the engine reads XOCP is not tested here."""
    if not active(a) and not active(b):
        return False
    if active(a) != active(b):
        return True
    return bool(set(edit_kinds(a, b)) & {"MOVE", "ROTATE", "SCALE", "BOUNDS"}) or kind_of(a) != kind_of(b)


# ------------------------------------------------------------------ geometry (row-vector, System.Numerics layout)
def _rx(a):
    c, s = math.cos(a), math.sin(a)
    return [[1, 0, 0], [0, c, s], [0, -s, c]]


def _ry(a):
    c, s = math.cos(a), math.sin(a)
    return [[c, 0, -s], [0, 1, 0], [s, 0, c]]


def _rz(a):
    c, s = math.cos(a), math.sin(a)
    return [[c, s, 0], [-s, c, 0], [0, 0, 1]]


def _mm(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def refr_rotation(rx, ry, rz):
    """Rz(-z) * Ry(-y) * Rx(-x) for v * M: rows are the reference's local axes in world space."""
    return _mm(_mm(_rz(-rz), _ry(-ry)), _rx(-rx))


def _vm(v, m):
    return [v[0] * m[0][j] + v[1] * m[1][j] + v[2] * m[2][j] for j in range(3)]


def _add(a, b):
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def _sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def _scale(a, s):
    return [a[0] * s, a[1] * s, a[2] * s]


def _norm(a):
    return math.sqrt(_dot(a, a))


def shape(row):
    """{kind, pos, rot, half} for an active plane version. The raw XPRM floats are HALF extents: Mutagen reads them x2
    (PlacedPrimitive.cs, FillBinaryBoundsCustom) and viewprobe halves Mutagen's bounds back (`Bounds * 0.5f`)."""
    return {"kind": kind_of(row), "pos": row["pos"], "rot": row["rot"],
            "half": [b * (row["scale"] or 1.0) for b in row["prim"]["bounds"]]}


def shape_polys(s):
    r = refr_rotation(*s["rot"])
    hx, hy, hz = s["half"]
    if s["kind"] == "PortalBox":
        return [[_add(_vm(q, r), s["pos"]) for q in ([-hx, 0, -hz], [hx, 0, -hz], [hx, 0, hz], [-hx, 0, hz])]]
    cs = [_add(_vm([hx if i & 1 else -hx, hy if i & 2 else -hy, hz if i & 4 else -hz], r), s["pos"]) for i in range(8)]
    rings = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]]
    return [[cs[i] for i in ring] for ring in rings]


def eye_inside_box(s, eye):
    r, d = refr_rotation(*s["rot"]), _sub(eye, s["pos"])
    return all(abs(_dot(d, r[i])) <= s["half"][i] for i in range(3))


def frustum(bearing_deg):
    b = math.radians(bearing_deg)
    f = [math.sin(b), math.cos(b), 0.0]
    rt = _cross(f, [0, 0, 1.0])
    rt = _scale(rt, 1 / _norm(rt))
    up = _cross(rt, f)
    th = math.tan(math.radians(HFOV) / 2)
    tv = th / ASPECT
    return [(f, -NEAR), (_sub(_scale(f, th), rt), 0.0), (_add(_scale(f, th), rt), 0.0),
            (_sub(_scale(f, tv), up), 0.0), (_add(_scale(f, tv), up), 0.0)]


def _clip(poly, eye, planes):
    cur = list(poly)
    for a, b in planes:
        if not cur:
            break
        nxt = []
        for i in range(len(cur)):
            s, e = cur[i], cur[(i + 1) % len(cur)]
            gs, ge = _dot(a, _sub(s, eye)) + b, _dot(a, _sub(e, eye)) + b
            if gs >= 0:
                nxt.append(s)
            if (gs >= 0) != (ge >= 0):
                nxt.append(_add(s, _scale(_sub(e, s), gs / (gs - ge))))
        cur = nxt
    return cur


def point_poly_dist(p, poly):
    """Exact distance from p to a planar convex polygon in 3D."""
    if len(poly) < 3:
        return min(_norm(_sub(p, q)) for q in poly) if poly else math.inf
    n, k = _cross(_sub(poly[1], poly[0]), _sub(poly[2], poly[0])), 2
    while _norm(n) < 1e-9 and k + 1 < len(poly):
        k += 1
        n = _cross(_sub(poly[1], poly[0]), _sub(poly[k], poly[0]))
    best = math.inf
    if _norm(n) >= 1e-9:
        n = _scale(n, 1 / _norm(n))
        dpl = _dot(_sub(p, poly[0]), n)
        proj, sgn, inside = _sub(p, _scale(n, dpl)), 0, True
        for i in range(len(poly)):
            a, b = poly[i], poly[(i + 1) % len(poly)]
            c = _dot(_cross(_sub(b, a), _sub(proj, a)), n)
            if abs(c) < 1e-9:
                continue
            sg = 1 if c > 0 else -1
            if sgn == 0:
                sgn = sg
            elif sg != sgn:
                inside = False
                break
        if inside:
            best = abs(dpl)
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        ab = _sub(b, a)
        t = 0.0 if _dot(ab, ab) == 0 else max(0.0, min(1.0, _dot(_sub(p, a), ab) / _dot(ab, ab)))
        best = min(best, _norm(_sub(p, _add(a, _scale(ab, t)))))
    return best


def in_view(s, eye, bearing):
    """(in view within reach, nearest distance of the in-frustum part or None)."""
    if s["kind"] == "Box" and eye_inside_box(s, eye):
        return True, 0.0
    planes, best = frustum(bearing), math.inf
    for poly in shape_polys(s):
        c = _clip(poly, eye, planes)
        if c:
            best = min(best, point_poly_dist(eye, c))
    return best <= REACH, (None if best == math.inf else round(best, 2))


def rule_issue(s, eye, rule):
    """None when the occlusion rule predicts this shape from this eye, else the refusal reason (viewprobe's
    ShapeOcclusion.Shadow and PortalBoxOrientation.Check, read for scoping)."""
    r, tol = refr_rotation(*s["rot"]), rule["maximumAbsNormalZ"]
    if s["kind"] not in ("Box", "PortalBox"):
        return "not a Box or PortalBox primitive"
    full = shape_polys(s)
    eye_d = min(point_poly_dist(eye, poly) for poly in full)
    if s["kind"] == "Box":
        if not rule.get("boxSilhouette"):
            return "Box face behavior unassessed (rule has no Box silhouette)"
        if max(abs(r[0][2]), abs(r[1][2])) > tol:
            return "box not upright"
        if eye_inside_box(s, eye):
            return "eye inside the box"
        if eye_d <= rule["minimumEyeDistance"]:
            return "near-eye"
        return None
    n = r[1]
    if abs(n[2]) > tol:
        if not rule.get("horizontalPortalBoxNormalDownEyeAbove"):
            return "PortalBox tilted"
        if not (abs(n[0]) <= tol and abs(n[1]) <= tol):
            return "PortalBox tilted"
        if n[2] > 0:
            return "horizontal PortalBox normal up (+Z)"
        if not (eye[2] - s["pos"][2] > 0):
            return "horizontal PortalBox seen from below"
    if eye_d <= rule["minimumEyeDistance"]:
        return "near-eye"
    return None


def geometry_selftest(verbose=True):
    """Known answers for the frustum test (each rejects one wrong reading) and the rule's shape classes."""
    fails = []

    def chk(name, got, want):
        if got != want:
            fails.append("%s: got %s want %s" % (name, got, want))
        if verbose:
            print("  %-66s %s" % (name, "ok" if got == want else "FAIL (got %s, want %s)" % (got, want)))
    y = _vm([0, 1, 0], refr_rotation(0, 0, 0.8))
    chk("local +Y under yaw z is (sin z, cos z, 0)", all(abs(a - b) < 1e-12 for a, b in zip(y, [math.sin(0.8), math.cos(0.8), 0])), True)
    eye = [0.0, 0.0, 120.5]
    P = lambda pos, half, rot=(0, 0, 0), kind="PortalBox": {"kind": kind, "pos": pos, "half": half, "rot": list(rot)}
    chk("K1 PortalBox 1,000 u ahead is in view", in_view(P([0, 1000, 120], [300, 1, 200]), eye, 0)[0], True)
    chk("K2 PortalBox 1,000 u behind is not in view", in_view(P([0, -1000, 120], [300, 1, 200]), eye, 0)[0], False)
    chk("K3 PortalBox 5,000 u ahead is not in view (reach)", in_view(P([0, 5000, 120], [300, 1, 200]), eye, 0)[0], False)
    chk("K4 centre 60 deg off-axis, wide panel's edge inside: in view", in_view(P([1732, 1000, 120], [1500, 1, 200]), eye, 0)[0], True)
    chk("K4b same centre, narrow panel: not in view", in_view(P([1732, 1000, 120], [100, 1, 200]), eye, 0)[0], False)
    chk("K5 Box centre 4,600 u ahead, near face 4,100: not in view", in_view(P([0, 4600, 120], [300, 500, 200], kind="Box"), eye, 0)[0], False)
    chk("K5b Box centre 4,400 u ahead, near face 3,900: in view", in_view(P([0, 4400, 120], [300, 500, 200], kind="Box"), eye, 0)[0], True)
    chk("K6 panel 900-1,100 u above the eye at 1,000 u: not in view", in_view(P([0, 1000, 1120], [300, 1, 100]), eye, 0)[0], False)
    chk("K7 panel 1,000 u east, bearing 90: in view", in_view(P([1000, 0, 120], [1, 300, 200]), eye, 90)[0], True)
    chk("K7b same panel, bearing 270: not in view", in_view(P([1000, 0, 120], [1, 300, 200]), eye, 270)[0], False)
    chk("K8 eye inside a Box: in view", in_view(P([0, 0, 120], [300, 300, 300], kind="Box"), eye, 0)[0], True)
    rule = {"maximumAbsNormalZ": 0.001, "minimumEyeDistance": 42, "boxSilhouette": True,
            "horizontalPortalBoxNormalDownEyeAbove": True}
    chk("R1 upright PortalBox: predicted", rule_issue(P([0, 1000, 120], [300, 1, 200]), eye, rule), None)
    chk("R2 PortalBox tilted 30 deg about X: refused", rule_issue(P([0, 1000, 120], [300, 1, 200], (math.radians(30), 0, 0)), eye, rule), "PortalBox tilted")
    # local +Y -> world -Z needs rx = +90 deg in this layout (row 1 of Rx(-x) is (0, cos x, -sin x))
    chk("R3 horizontal PortalBox normal down, eye above: predicted", rule_issue(P([0, 1000, 0], [300, 1, 200], (math.pi / 2, 0, 0)), eye, rule), None)
    chk("R4 horizontal PortalBox normal up: refused", rule_issue(P([0, 1000, 0], [300, 1, 200], (-math.pi / 2, 0, 0)), eye, rule), "horizontal PortalBox normal up (+Z)")
    chk("R5 horizontal PortalBox normal down, eye below: refused", rule_issue(P([0, 1000, 500], [300, 1, 200], (math.pi / 2, 0, 0)), eye, rule), "horizontal PortalBox seen from below")
    chk("R6 upright Box: predicted", rule_issue(P([0, 1000, 120], [300, 300, 300], kind="Box"), eye, rule), None)
    chk("R7 Box rolled 10 deg: refused", rule_issue(P([0, 1000, 120], [300, 300, 300], (0, math.radians(10), 0), kind="Box"), eye, rule), "box not upright")
    chk("R8 Box with the rule's Box silhouette off: refused", rule_issue(P([0, 1000, 120], [300, 300, 300], kind="Box"), eye, dict(rule, boxSilhouette=False)),
        "Box face behavior unassessed (rule has no Box silhouette)")
    chk("R9 PortalBox 30 u from the eye: near-eye", rule_issue(P([0, 30, 120], [300, 1, 200]), eye, rule), "near-eye")
    if verbose:
        print("SELFTEST", "PASS" if not fails else "FAIL %d" % len(fails))
    return fails


# ------------------------------------------------------------------ group definition
def group_schema():
    return {"name": "label", "orders": {"u": "order JSON (Plugins, last wins)", "p": "order JSON"},
            "pluginFiles": {"<plugin name>": "path"}, "patch": "<patch plugin name>",
            "rule": "occlusion rule JSON (the one renders will use)",
            "worldspaces": {"<worldspace formKey>": {"name": "label", "setting": "city|wilderness",
                                                    "stations": "stations-<W>.json (accepted stations, gazes)",
                                                    "stationCell": "cell formKey for --stations",
                                                    "stationSpec": "{route spec}"}},
            "expectedEdits": "optional JSON list of {formKey, kind, target?}"}


def load_group(path):
    g = json.loads(Path(path).read_text(encoding="utf-8"))
    base = Path(path).resolve().parent

    def rel(p):
        q = Path(p)
        return q if q.is_absolute() else base / q
    g["_base"] = base
    g["_rel"] = rel
    for arm in ("u", "p"):
        g["orders"][arm] = rel(g["orders"][arm])
    return g


def order_plugins(g, arm):
    return json.loads(Path(g["orders"][arm]).read_text(encoding="utf-8"))["Plugins"]


# ------------------------------------------------------------------ step 1: the patch's plane edits
def read_versions(g):
    """formKey -> {plugin: row} for every placed plane in every plugin of the p order, plus plugin hashes."""
    versions, hashes = defaultdict(dict), {}
    for name in order_plugins(g, "p"):
        pl = Plugin(g["_rel"](g["pluginFiles"][name]))
        hashes[name] = pl.sha256
        for row in pl.placed():
            if is_plane(row):
                versions[row["formKey"]][name] = row
    return versions, hashes


def winner(vers, order):
    w = None
    for name in order:
        if name in vers:
            w = (name, vers[name])
    return w


def classify(u, p, b_versions):
    """Record class: new / disabled / re-enabled / restored to another plugin's version / moved-or-resized /
    unchanged (an identity forward)."""
    if u is None:
        return "new"
    if disabled(p) and not disabled(u):
        return "disabled"
    if disabled(u) and not disabled(p):
        return "re-enabled"
    if disabled(u) and disabled(p):
        return "disabled in both"
    for name, row in b_versions:
        if same_version(row, p) and not same_version(u, p):
            return "restored to %s's version" % name
    return "moved-or-resized" if edit_kinds(u, p) else "unchanged"


def patch_step(g, out):
    with runlog.Run("planeassess-patch", stage_source="single-stage") as run:
        run.input("orderU", str(g["orders"]["u"]))
        run.input("orderP", str(g["orders"]["p"]))
        versions, hashes = read_versions(g)
        uo, po, patch = order_plugins(g, "u"), order_plugins(g, "p"), g["patch"]
        assert po[-1] == patch and po[:-1] == uo, "the p order must be the u order plus the patch, last"
        rows, edits = [], []
        for fk, vers in sorted(versions.items()):
            if patch not in vers:
                continue
            p = vers[patch]
            uw = winner(vers, uo)
            u = uw[1] if uw else None
            others = [(n, r) for n, r in vers.items() if n not in (patch, uw[0] if uw else None)]
            kinds = edit_kinds(u, p)
            ws = p["worldspace"] or (u or {}).get("worldspace")
            row = {"formKey": fk, "worldspace": ws, "cell": p["cell"], "kind": kind_of(p) or kind_of(u),
                   "class": classify(u, p, others), "edits": kinds,
                   "priorOwner": uw[0] if uw else None, "definedBy": fk.split(":", 1)[1],
                   "equalsVersionOf": [n for n, r2 in others if same_version(r2, p)],
                   "activeU": active(u), "activeP": active(p), "p": p, "u": u}
            if u and p["pos"] and u["pos"]:
                row["moved_u"] = round(math.dist(u["pos"], p["pos"]), 2)
            rows.append(row)
            edits += [{"formKey": fk, "kind": k, "worldspace": ws, "class": row["class"], "priorOwner": row["priorOwner"],
                       "definedBy": row["definedBy"]} for k in kinds]
        doc = {"schema": "planeassess/patch/1", "group": g["name"], "pluginSha256": hashes,
               "orders": {a: {"path": str(g["orders"][a]), "sha256": sha(g["orders"][a])} for a in ("u", "p")},
               "counts": {"planeRecords": len(rows), "edits": len(edits),
                          "byWorldspaceClass": _count(rows, lambda r: (r["worldspace"], r["class"])),
                          "byWorldspaceEditKind": _count(edits, lambda e: (e["worldspace"], e["kind"])),
                          "byPriorOwnerEditKind": _count(edits, lambda e: (e["priorOwner"] or "-", e["kind"]))},
               "records": rows, "edits": edits}
        if g.get("expectedEdits"):
            doc["reconciliation"] = reconcile(edits, json.loads(Path(g["_rel"](g["expectedEdits"])).read_text(encoding="utf-8")))
        Path(out).mkdir(parents=True, exist_ok=True)
        dest = Path(out) / "patch.json"
        dest.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        run.artifact(str(dest))
        run.feature("planeRecords", len(rows), "records")
        print(json.dumps(doc["counts"], indent=1))
        if "reconciliation" in doc:
            print(json.dumps({k: v for k, v in doc["reconciliation"].items() if k != "detail"}, indent=1))
        return doc


def _count(xs, key):
    c = Counter(key(x) for x in xs)
    return {" | ".join(str(p) for p in k): n for k, n in sorted(c.items(), key=lambda kv: str(kv[0]))}


def reconcile(edits, expected):
    have = Counter((e["formKey"], e["kind"]) for e in edits)
    want = Counter((e["formKey"], e["kind"]) for e in expected)
    target = Counter((e["formKey"], e["kind"]) for e in expected if e.get("target", True))
    missing, extra = want - have, have - want
    return {"driverEdits": sum(have.values()), "expectedEdits": sum(want.values()), "targetEdits": sum(target.values()),
            "matched": sum((have & want).values()), "targetMatched": sum((have & target).values()),
            "missingFromDriver": sum(missing.values()), "extraInDriver": sum(extra.values()),
            "detail": {"missingFromDriver": sorted("%s %s" % k for k in missing),
                       "extraInDriver": sorted("%s %s" % k for k in extra)}}


# ------------------------------------------------------------------ station sampling (deployed viewprobe)
def busy_processes(exempt=()):
    """Heavy processes running now, by image name (a command line that merely mentions one does not count);
    an exempt executable path is ignored."""
    import csv
    import io
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-CimInstance Win32_Process | Select-Object Name,ExecutablePath | ConvertTo-Csv -NoTypeInformation"],
                         capture_output=True, text=True, errors="replace").stdout
    heavy = {"viewprobe.exe", "skyrimse.exe", "skyrimvr.exe", "modorganizer.exe", "overlapprobe.exe", "contactprobe.exe",
             "floorgapprobe.exe", "lightprobe.exe", "obs64.exe"}
    ex = {e.lower().replace("/", "\\") for e in exempt}
    return ["%s %s" % (r["Name"], r.get("ExecutablePath") or "") for r in csv.DictReader(io.StringIO(out))
            if (r.get("Name") or "").lower() in heavy and (r.get("ExecutablePath") or "").lower() not in ex]


def stations_step(g, viewprobe, work, copy_to, instance, game, only=None, exempt=(), locks=()):
    exe = Path(viewprobe)
    for lk in locks:
        assert not Path(lk).exists(), "refusing: %s exists" % lk
    busy = busy_processes(exempt)
    assert not busy, "refusing: busy machine: %s" % busy
    jobs, outs = [], []
    profile = json.loads(Path(g["orders"]["u"]).read_text(encoding="utf-8")).get("BaseProfile") or "Manual"
    for wk, w in g["worldspaces"].items():
        if only and w["name"] not in only:
            continue
        out = Path(work) / w["name"]
        out.mkdir(parents=True, exist_ok=True)
        wl = out / ("%s.worklist.json" % w["name"])
        label = "%s-route-%s" % (g["name"], w["name"])
        wl.write_text(json.dumps({"label": label, "cells": [{"cell": w["stationCell"], "shots": [
            {"label": label, "eye": [0, 0, 0], "look": [0, 1, 0], "fov": HFOV, "nav": "off", "occlusion": "off"}]}]},
            indent=1), encoding="utf-8")
        spec = out / "spec-route.json"
        spec.write_text(json.dumps(w["stationSpec"], indent=1), encoding="utf-8")
        argv = [str(exe), str(wl), "--stations", str(spec), "--stations-out", str(out / "route.poses.json"),
                "--instance", instance, "--profile", profile, "--game", game, "--order", str(g["orders"]["u"])]
        jobs.append({"id": "%s-stations-%s" % (g["name"], w["name"]), "argv": argv, "cwd": str(out)})
        outs.append((w["name"], out))
    summary = {"viewprobeDll": sha(exe.parent / "viewprobe.dll"), "coreDll": sha(exe.parent / "housecarl-core.dll"),
               "orderU": str(g["orders"]["u"]), "orderSha256": sha(g["orders"]["u"]), "profile": profile, "runs": []}
    t0 = time.time()
    res = runlog.Runner("planeassess-stations", out_dir=str(Path(work) / "logs")).run(jobs)
    summary["wallSeconds"] = round(time.time() - t0, 1)
    for (name, out), r in zip(outs, res):
        row = {"worldspace": name, "exitCode": r.get("exitCode"), "seconds": r.get("seconds"), "files": {}}
        dst = Path(copy_to) / name
        dst.mkdir(parents=True, exist_ok=True)
        for f in sorted(out.iterdir()):
            if f.is_file():
                row["files"][f.name] = {"bytes": f.stat().st_size, "sha256": sha(f)}
                if not f.name.startswith("navdump-") and f.stat().st_size <= (200 << 20):
                    (dst / f.name).write_bytes(f.read_bytes())
        summary["runs"].append(row)
    Path(copy_to).mkdir(parents=True, exist_ok=True)
    (Path(copy_to) / "stations-summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print(json.dumps(summary, indent=1)[:3000])
    return 0 if all(r["exitCode"] == 0 for r in summary["runs"]) else 1


# ------------------------------------------------------------------ step 2: scope
POSE = "prior in-game pose"


def pose_station(ps):
    """An engine-confirmed pose from an earlier in-game run as a one-gaze pseudo-station (level gaze from eye to look)."""
    e, k = ps["eye"], ps["look"]
    bearing = math.degrees(math.atan2(k[0] - e[0], k[1] - e[1])) % 360.0
    return {"id": ps["label"], "family": POSE, "x": e[0], "y": e[1], "eyeZ": e[2], "accepted": True,
            "gazes": [{"kind": "pose", "bearingDeg": bearing}], "cell": ps["cell"], "look": k, "source": ps["source"]}


def scope_step(g, out, patch_doc=None):
    fails = geometry_selftest(verbose=False)
    assert not fails, "geometry known answers fail: %s" % fails
    rule = json.loads(Path(g["_rel"](g["rule"])).read_text(encoding="utf-8"))
    with runlog.Run("planeassess-scope", stage_source="counted") as run:
        versions, _ = read_versions(g)
        uo, po = order_plugins(g, "u"), order_plugins(g, "p")
        arms = {"u": {}, "p": {}}
        for fk, vers in versions.items():
            for arm, order in (("u", uo), ("p", po)):
                w = winner(vers, order)
                if w:
                    arms[arm][fk] = w[1]
        differ = sorted(fk for fk in set(arms["u"]) | set(arms["p"]) if shape_differs(arms["u"].get(fk), arms["p"].get(fk)))
        patch_doc = patch_doc or json.loads((Path(out) / "patch.json").read_text(encoding="utf-8"))
        prec = {r["formKey"]: r for r in patch_doc["records"]}
        assert set(differ) <= set(prec), "u/p differences outside the patch: %s" % sorted(set(differ) - set(prec))[:5]
        gazes_total = 0
        per_ws, kept, edit_seen = {}, [], defaultdict(lambda: {"gazes": 0, "decidable": 0, "refusals": Counter(),
                                                                 "stations": set(), "nearest": None})
        run.stage("gazes", "scoping station gazes")
        for wk, w in g["worldspaces"].items():
            st = json.loads(Path(g["_rel"](w["stations"])).read_text(encoding="utf-8"))["stations"]
            acc = [s for s in st if s.get("accepted")]
            poses = [pose_station(ps) for ps in g.get("addedPoses", []) if ps["worldspace"] == wk]
            shapes = {arm: [(fk, shape(r)) for fk, r in arms[arm].items() if r["worldspace"] == wk and active(r)]
                      for arm in ("u", "p")}
            dset = {fk for fk in differ if (arms["u"].get(fk) or arms["p"].get(fk))["worldspace"] == wk}
            stat = {"setting": w.get("setting"), "acceptedStations": len(acc), "gazes": 0, "eligibleStations": 0,
                    "eligibleGazes": 0, "keptGazes": 0, "keptDecidableGazes": 0, "keptStations": 0,
                    "differingPlanes": len(dset), "addedPoses": len(poses), "addedPosesKept": 0}
            for s in acc + poses:
                is_pose = s.get("family") == POSE
                eye = [s["x"], s["y"], s["eyeZ"]]
                near = {arm: [(fk, sh) for fk, sh in shapes[arm]
                              if math.hypot(sh["pos"][0] - eye[0], sh["pos"][1] - eye[1]) <= REACH + max(sh["half"]) * math.sqrt(3)]
                        for arm in ("u", "p")}
                for fk in dset:
                    for arm in ("u", "p"):
                        r = arms[arm].get(fk)
                        if active(r):
                            d = math.hypot(r["pos"][0] - eye[0], r["pos"][1] - eye[1])
                            e = edit_seen[fk]
                            e["nearest"] = d if e["nearest"] is None else min(e["nearest"], d)
                st_elig = st_kept = False
                for gz in s["gazes"]:
                    stat["gazes"] += 0 if is_pose else 1
                    gazes_total += 1
                    run.advance()
                    seen = {arm: {fk: in_view(sh, eye, gz["bearingDeg"]) for fk, sh in near[arm]} for arm in ("u", "p")}
                    seen = {arm: {fk: v[1] for fk, v in d.items() if v[0]} for arm, d in seen.items()}
                    if (seen["u"] or seen["p"]) and not is_pose:
                        stat["eligibleGazes"] += 1
                        st_elig = True
                    dview = sorted((set(seen["u"]) | set(seen["p"])) & dset)
                    if not dview:
                        continue
                    stat["addedPosesKept" if is_pose else "keptGazes"] += 1
                    st_kept = not is_pose
                    planes = []
                    for fk in dview:
                        issues = {}
                        for arm in ("u", "p"):
                            if fk in seen[arm]:
                                issues[arm] = rule_issue(shape(arms[arm][fk]), eye, rule)
                        refused = sorted({v for v in issues.values() if v})
                        e = edit_seen[fk]
                        e["gazes"] += 1
                        e["stations"].add(s["id"])
                        if refused:
                            e["refusals"].update(refused)
                        else:
                            e["decidable"] += 1
                        planes.append({"formKey": fk, "distanceU": seen["u"].get(fk), "distanceP": seen["p"].get(fk),
                                       "ruleIssue": issues, "decidable": not refused})
                    if any(pl["decidable"] for pl in planes) and not is_pose:
                        stat["keptDecidableGazes"] += 1
                    kept.append({"worldspace": w["name"], "setting": w.get("setting"), "stationId": s["id"],
                                 "family": s.get("family"), "eye": eye, "gaze": gz["kind"], "bearingDeg": gz["bearingDeg"],
                                 "planesInViewU": len(seen["u"]), "planesInViewP": len(seen["p"]), "differingInView": planes,
                                 "basis": POSE if is_pose else "route",
                                 **({"cell": s["cell"], "look": s["look"], "source": s["source"]} if is_pose else {})})
                stat["eligibleStations"] += st_elig and not is_pose
                stat["keptStations"] += st_kept
            per_ws[w["name"]] = stat
        rows = []
        for fk, r in sorted(prec.items()):
            e = edit_seen.get(fk) or {"gazes": 0, "decidable": 0, "refusals": Counter(), "stations": set(), "nearest": None}
            if fk not in differ:
                reason = "no change to the culling shape viewprobe reads (colour, XOCP or a disabled record's parent only)"
            elif not (r["activeU"] or r["activeP"]):
                reason = "inactive in both arms (disabled or deleted in u and in p)"
            elif r["worldspace"] not in g["worldspaces"]:
                reason = "no station set for its worldspace (%s)" % r["worldspace"]
            elif e["gazes"] == 0:
                reason = "no accepted station gaze sees it within %d u" % REACH
            elif e["decidable"] == 0:
                reason = "rule refuses the shape at every gaze that sees it: " + ", ".join(
                    "%s (%d)" % kv for kv in sorted(e["refusals"].items()))
            else:
                reason = None
            rows.append({"formKey": fk, "worldspace": g["worldspaces"].get(r["worldspace"], {}).get("name", r["worldspace"]),
                         "kind": r["kind"], "class": r["class"], "edits": r["edits"], "priorOwner": r["priorOwner"],
                         "keptGazesSeeing": e["gazes"], "decidableGazes": e["decidable"], "stationsSeeing": len(e["stations"]),
                         "refusals": dict(e["refusals"]),
                         "nearestStationPlanarU": None if e["nearest"] is None else round(e["nearest"], 1),
                         "inCeiling": reason is None, "outsideReason": reason})
        edits = [{"formKey": r["formKey"], "kind": k, "worldspace": r["worldspace"], "inCeiling": r["inCeiling"],
                  "outsideReason": r["outsideReason"], "keptGazesSeeing": r["keptGazesSeeing"]} for r in rows for k in r["edits"]]
        doc = {"schema": "planeassess/scope/1", "group": g["name"],
               "reading": {"frustum": "level, %g deg horizontal, 16:9, near %g u" % (HFOV, NEAR), "reach": REACH,
                           "kept": "a gaze is kept when a plane whose u and p versions differ is in view in either arm",
                           "decidable": "every in-view version of the plane is predicted by the rule from that eye",
                           "rule": str(g["_rel"](g["rule"])), "ruleSha256": sha(g["_rel"](g["rule"]))},
               "perWorldspace": per_ws, "differingPlanes": len(differ),
               "ceiling": {"planeRecords": sum(r["inCeiling"] for r in rows), "planeRecordsTotal": len(rows),
                           "edits": sum(e["inCeiling"] for e in edits), "editsTotal": len(edits),
                           "seenAtKeptGaze": sum(1 for r in rows if r["keptGazesSeeing"] > 0)},
               "outsideByReason": _count([r for r in rows if not r["inCeiling"]], lambda r: (r["outsideReason"].split(":")[0],)),
               "planes": rows, "editRows": edits, "keptGazes": kept}
        if g.get("expectedEdits"):
            exp = json.loads(Path(g["_rel"](g["expectedEdits"])).read_text(encoding="utf-8"))
            tk = {(e["formKey"], e["kind"]) for e in exp if e.get("target", True)}
            tf = {fk for fk, _ in tk}
            tw = defaultdict(lambda: {"keptGazes": 0, "keptDecidableGazes": 0, "keptDecidablePoses": 0, "keptStations": set()})
            for k in kept:
                if any(pl["formKey"] in tf for pl in k["differingInView"]) and k.get("basis", "route") == "route":
                    tw[k["worldspace"]]["keptGazes"] += 1
                    tw[k["worldspace"]]["keptStations"].add(k["stationId"])
                if any(pl["formKey"] in tf and pl["decidable"] for pl in k["differingInView"]):
                    tw[k["worldspace"]]["keptDecidableGazes" if k.get("basis", "route") == "route" else "keptDecidablePoses"] += 1
            te = [e for e in edits if (e["formKey"], e["kind"]) in tk]
            doc["target"] = {"edits": len(tk), "editsFound": len(te), "ceilingEdits": sum(e["inCeiling"] for e in te),
                             "perWorldspace": {w: {"keptGazes": v["keptGazes"], "keptDecidableGazes": v["keptDecidableGazes"],
                                                   "keptDecidablePoses": v["keptDecidablePoses"],
                                                   "keptStations": len(v["keptStations"])}
                                               for w, v in tw.items()},
                             "outsideByReason": _count([e for e in te if not e["inCeiling"]],
                                                       lambda e: (e["outsideReason"].split(":")[0],))}
        dest = Path(out) / "scope.json"
        dest.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        run.artifact(str(dest))
        run.feature("gazes", gazes_total, "gazes")
        print(json.dumps({k: doc[k] for k in ("perWorldspace", "differingPlanes", "ceiling", "outsideByReason", "target")
                          if k in doc}, indent=1))
        return doc


# ------------------------------------------------------------------ step 3: render the kept gazes
def shots_step(g, out, per_worldspace=None, include=(), only=None):
    """The render list: every kept gaze where a target plane is decidable (or, with `per_worldspace`, the N such gazes
    of each worldspace's busiest cell, for a timed sample), plus any `include`d `station:gazeKind`. Cells come from
    each station set's route.poses.json."""
    scope = json.loads((Path(out) / "scope.json").read_text(encoding="utf-8"))
    tf = None
    if g.get("expectedEdits"):
        tf = {e["formKey"] for e in json.loads(Path(g["_rel"](g["expectedEdits"])).read_text(encoding="utf-8"))
              if e.get("target", True)}
    cells = {}
    for w in g["worldspaces"].values():
        poses = json.loads((Path(g["_rel"](w["stations"])).parent / "route.poses.json").read_text(encoding="utf-8"))["poses"]
        cells.update({p["stationId"]: p["cell"] for p in poses if p.get("accepted")})
    rows = []
    for k in scope["keptGazes"]:
        tag = "%s:%s" % (k["stationId"], k["gaze"])
        good = any(pl["decidable"] and (tf is None or pl["formKey"] in tf) for pl in k["differingInView"])
        if (only and k["worldspace"] not in only) or not (good or tag in include):
            continue
        b, eye = math.radians(k["bearingDeg"]), k["eye"]
        look = k.get("look") or [eye[0] + 1000 * math.sin(b), eye[1] + 1000 * math.cos(b), eye[2]]
        rows.append({"label": "%s-%s-%s" % (g["name"], k["stationId"].replace(":", "_"), k["gaze"]),
                     "worldspace": k["worldspace"], "stationId": k["stationId"], "gaze": k["gaze"],
                     "bearingDeg": k["bearingDeg"], "eye": eye,
                     "look": look, "basis": k.get("basis", "route"),
                     "cell": k.get("cell") or cells[k["stationId"]], "included": tag in include,
                     "planes": [pl["formKey"] for pl in k["differingInView"]]})
    if per_worldspace:
        pick = []
        for w in sorted({r["worldspace"] for r in rows}):
            rw = [r for r in rows if r["worldspace"] == w and not r["included"]]
            top = Counter(r["cell"] for r in rw).most_common(1)
            pick += [r for r in rw if top and r["cell"] == top[0][0]][:per_worldspace]
        rows = pick + [r for r in rows if r["included"]]
    dest = Path(out) / "shots.json"
    dest.write_text(json.dumps({"group": g["name"], "shots": rows}, indent=1), encoding="utf-8")
    print("shots", len(rows), dict(Counter(r["worldspace"] for r in rows)))
    return rows


def _proc_mem_mib(pid):
    """(private commit, peak working set) MiB of one Windows process, or (None, None)."""
    import ctypes

    class C(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong), ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t), ("a", ctypes.c_size_t), ("b", ctypes.c_size_t),
                    ("c", ctypes.c_size_t), ("d", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                    ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]
    if not isinstance(pid, int) or os.name != "nt":
        return None, None
    h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
    if not h:
        return None, None
    try:
        c = C()
        c.cb = ctypes.sizeof(c)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb):
            return None, None
        return c.PrivateUsage / 1048576.0, c.PeakWorkingSetSize / 1048576.0
    finally:
        ctypes.windll.kernel32.CloseHandle(h)


RESUME_KEYS = ("viewprobeDll", "coreDll", "rule", "order", "arm", "measurementMode", "occlusionQuery")
DEPTH_FLAG, DEPTH_MODE = "--depth-measurement", "depth-measurement"
QUERY_FLAG = "--occlusion-query"


def with_mode(ident, depth_measurement, occlusion_query=False):
    """The render identity, naming viewprobe's depth-measurement mode, and its GPU occlusion query, only when on: a
    ledger row without a key reads as None, so earlier renders resume exactly as before and no two of default, mode and
    mode-with-query ever resume each other."""
    ident = dict(ident, measurementMode=DEPTH_MODE) if depth_measurement else dict(ident)
    return dict(ident, occlusionQuery=True) if occlusion_query else ident


def occlusion_query_flags_problem(depth_measurement, occlusion_query):
    """None unless --occlusion-query is asked for without --depth-measurement, whose isolated draws it counts."""
    if occlusion_query and not depth_measurement:
        return "refusing: --occlusion-query only applies with --depth-measurement; add --depth-measurement or drop it."
    return None


def chunk_argv(exe, wl, instance, profile, game, order, rule, dest, no_reuse=False, depth_measurement=False,
               occlusion_query=False):
    """viewprobe's argv for one chunk."""
    return ([str(exe), str(wl), "--instance", instance, "--profile", profile, "--game", game, "--order", str(order),
             "--planes-report", "--occlusion-rule", str(rule), "--size", "1200", "--msaa", "0",
             "--visibility-budget", "4096", "--out", str(dest)] + (["--no-reuse-cell"] if no_reuse else [])
            + ([DEPTH_FLAG] if depth_measurement else []) + ([QUERY_FLAG] if occlusion_query else []))


def depth_measurement_problem(argv_prefix, cell="000000:Skyrim.esm", args=()):
    """None when this viewprobe accepts --depth-measurement; else one sentence. Asked with a cell and the chunk's own
    instance arguments and no --planes-report: a build with the flag refuses with its own sentence (exit 1), a build
    without it exits 2 naming the flag as unknown."""
    r = subprocess.run(list(argv_prefix) + [cell, DEPTH_FLAG] + list(args), capture_output=True, text=True,
                       errors="replace", timeout=600)
    if r.returncode == 2 and "does not recognise '%s'" % DEPTH_FLAG in (r.stdout + r.stderr):
        return ("refusing: %s does not accept --depth-measurement (an older viewprobe build); use a build that has it "
                "or drop the flag." % " ".join(map(str, argv_prefix)))
    return None


def occlusion_query_problem(argv_prefix, cell="000000:Skyrim.esm", args=()):
    """None when this viewprobe accepts --occlusion-query; else one sentence. Asked as depth_measurement_problem is: a
    build with the flag refuses with the --planes-report sentence (exit 1), a build without it exits 2 naming the flag."""
    r = subprocess.run(list(argv_prefix) + [cell, DEPTH_FLAG, QUERY_FLAG] + list(args), capture_output=True, text=True,
                       errors="replace", timeout=600)
    if r.returncode == 2 and "does not recognise '%s'" % QUERY_FLAG in (r.stdout + r.stderr):
        return ("refusing: %s does not accept --occlusion-query (an older viewprobe build); use a build that has it "
                "or drop the flag." % " ".join(map(str, argv_prefix)))
    return None


def report_records_query(path):
    """Whether a planes report (raw or gzipped) records the occlusion query in its measurementMode block."""
    import gzip
    p = Path(path)
    raw = p.read_bytes()
    d = json.loads(gzip.decompress(raw) if p.suffix == ".gz" else raw)
    return bool(((d.get("appearance") or {}).get("measurementMode") or {}).get("occlusionQuery"))


def report_records_mode(path):
    """Whether a planes report (raw or gzipped) records the depth-measurement mode in its appearance block."""
    import gzip
    p = Path(path)
    raw = p.read_bytes()
    d = json.loads(gzip.decompress(raw) if p.suffix == ".gz" else raw)
    return ((d.get("appearance") or {}).get("measurementMode") or {}).get("name") == DEPTH_MODE


def done_shots(out, ident):
    """Shots a previous run of this render already finished: a ledger row with exit 0 or 3 (3 = unreadable assets named,
    normal), not stopped, under the same renderer identity, rule, order and arm (`RESUME_KEYS`), whose report (raw or
    gzipped) is in that row's chunk folder. The memory mode is not part of the key: reports are equal across modes."""
    f = Path(out) / "ledger.jsonl"
    done = set()
    if not f.exists():
        return done
    for line in f.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        r = json.loads(line)
        if "shots" not in r or r.get("stopped") or r.get("exitCode") not in (0, 3):
            continue
        if any(r.get(k) != ident.get(k) for k in RESUME_KEYS):
            continue
        d = Path(out) / r["chunk"]
        for label in r["shots"]:
            key = label.replace("-", "_")
            if any(d.glob("*-%s-fp-planes.json" % key)) or any(d.glob("*-%s-fp-planes.json.gz" % key)):
                done.add(label)
    return done


def _alive(pid):
    if not isinstance(pid, int) or pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
        ctypes.windll.kernel32.CloseHandle(h)
        return bool(ok) and code.value == 259          # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def lock_problem(lock):
    """None when the lock file is absent; else one sentence: held by a running pid, or stale (its pid is gone) with
    removal left to a person."""
    lk = Path(lock)
    if not lk.exists():
        return None
    try:
        pid = json.loads(lk.read_text(encoding="utf-8")).get("pid")
    except (ValueError, OSError):
        pid = None
    if _alive(pid):
        return "refusing: %s is held by running pid %d." % (lk, pid)
    return ("refusing: %s is stale (pid %s is no longer running); remove it by hand once you have checked no render is "
            "running, then rerun." % (lk, pid))


def render_step(g, arm, shots_file, out, viewprobe, instance, game, caps, need_free_mib, lock, pause,
                no_reuse=False, chunk=9, exempt=(), probe_commit_mib=None, fallback_chunk=3, memory_mode="full",
                constrained_need_mib=None, constrained_heap_mib=None, constrained_chunk=1, depth_measurement=False,
                occlusion_query=False):
    """Render the listed gazes in one arm, one viewprobe process per chunk (same cell, at most `chunk` shots), one
    process at a time. Before each chunk: the PAUSE file stops the block, free RAM must be at least `need_free_mib`,
    no other heavy process may run. During it: the campaign caps (process commit, system floor, graphics used) stop
    the process tree. Writes ledger.jsonl with per-chunk wall time, peak commit and peak working set. With
    `probe_commit_mib`: when a worldspace's first full chunk peaks above it, that worldspace's remaining shots are
    re-chunked to `fallback_chunk` shots per process.
    Memory mode (the probe-memory-budget convention: same results, full speed when memory allows, chosen from free
    memory): `full` needs `need_free_mib` free before a chunk; `constrained` renders one shot per process with the managed
    heap capped at `constrained_heap_mib` and needs `constrained_need_mib`; `auto` takes full when it fits, else
    constrained, else refuses in one sentence naming the memory needed."""
    tools = Path(__file__).resolve().parents[2] / "_geometry-harness" / "viewprobe" / "tools"
    sys.path.insert(0, str(tools))
    from renderbatch import cap_observer
    from renderlimit import read_graphics_free, read_system_free
    shots = json.loads(Path(shots_file).read_text(encoding="utf-8"))["shots"]
    order = g["orders"][arm]
    profile = json.loads(Path(order).read_text(encoding="utf-8")).get("BaseProfile") or "Manual"
    exe = Path(viewprobe)
    ident = with_mode({"viewprobeDll": sha(exe.parent / "viewprobe.dll"), "coreDll": sha(exe.parent / "housecarl-core.dll"),
                       "rule": sha(g["_rel"](g["rule"])), "order": sha(order), "arm": arm, "reuseCell": not no_reuse},
                      depth_measurement, occlusion_query)
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    finished = done_shots(out, ident)
    skipped = [s for s in shots if s["label"] in finished]
    shots = [s for s in shots if s["label"] not in finished]
    print("resume: skipped %d shots already rendered under this identity; %d to render" % (len(skipped), len(shots)),
          flush=True)
    groups = defaultdict(list)
    for s in shots:
        groups[(s["worldspace"], s["cell"])].append(s)
    chunks = [v[i:i + chunk] for v in groups.values() for i in range(0, len(v), chunk)]
    probed = set()
    problem = occlusion_query_flags_problem(depth_measurement, occlusion_query) or lock_problem(lock)
    if not problem and depth_measurement and chunks:
        problem = depth_measurement_problem([exe], chunks[0][0]["cell"],
                                            ["--instance", instance, "--profile", profile, "--game", game, "--order", str(order)])
    if not problem and occlusion_query and chunks:
        problem = occlusion_query_problem([exe], chunks[0][0]["cell"],
                                          ["--instance", instance, "--profile", profile, "--game", game, "--order", str(order)])
    if problem:
        raise SystemExit(problem)
    Path(lock).write_text(json.dumps({"pid": os.getpid(), "purpose": "planeassess render %s %s" % (g["name"], arm),
                                      "at": time.strftime("%Y-%m-%d %H:%M:%S %Z")}), encoding="utf-8")
    ledger, done, failed = out / "ledger.jsonl", 0, []
    oom, retries, force_full = [], [], set()
    try:
        n = -1
        while n + 1 < len(chunks):
            n += 1
            todo = chunks[n]
            if Path(pause).exists():
                print("PAUSE file present; stopping before chunk %d" % n)
                break
            busy, free = busy_processes(exempt), read_system_free()
            if busy or not free.ok:
                print("refusing chunk %d: busy %s, free memory unread %s" % (n, busy, free.reason))
                break
            mode = memory_mode
            if id(todo) in force_full:
                mode = "full"
            elif mode == "auto":
                mode = "full" if free.free_mib >= need_free_mib else "constrained"
            need = need_free_mib if mode == "full" else constrained_need_mib
            if need is None or free.free_mib < need:
                print("refusing: this view needs about %d MiB of free memory (%s mode) and %d MiB is free."
                      % (need or constrained_need_mib or need_free_mib, mode, free.free_mib), flush=True)
                break
            if mode == "constrained" and len(todo) > constrained_chunk:
                rest = todo[constrained_chunk:]
                chunks[n + 1:n + 1] = [rest[i:i + constrained_chunk] for i in range(0, len(rest), constrained_chunk)]
                todo = todo[:constrained_chunk]
            tag = "%s-%s-%s-%s-%d" % (g["name"], arm, todo[0]["worldspace"], todo[0]["cell"].replace(":", "_"),
                                      int(time.time()))
            dest = out / tag
            dest.mkdir(parents=True, exist_ok=False)
            wl = out / (tag + ".worklist.json")
            wl.write_text(json.dumps({"label": tag, "cells": [{"cell": todo[0]["cell"], "shots": [{
                "label": s["label"], "eye": s["eye"], "look": s["look"], "fov": HFOV, "nav": "off", "occlusion": "off",
                "control": False, "viewpointKind": "nominated-player",
                "viewpointEvidence": "route station %s gaze %s bearing %.2f" % (s["stationId"], s["gaze"], s["bearingDeg"])}
                for s in todo]}]}, indent=1), encoding="utf-8")
            argv = chunk_argv(exe, wl, instance, profile, game, order, g["_rel"](g["rule"]), dest, no_reuse,
                              depth_measurement, occlusion_query)
            peak = {"commitMiB": 0.0, "workingSetMiB": 0.0, "graphicsUsedMiB": 0.0}
            caps_obs = cap_observer(lambda pid: _proc_mem_mib(pid)[0], caps[0], system_reader=read_system_free,
                                    system_available_floor_mib=caps[1], graphics_reader=read_graphics_free,
                                    graphics_used_cap_mib=caps[2])

            def observe(ev, peak=peak, caps_obs=caps_obs):
                if ev.get("kind") == "sample":
                    c, w = _proc_mem_mib(ev.get("ownedPid"))
                    if c is not None:
                        peak["commitMiB"] = max(peak["commitMiB"], c)
                        peak["workingSetMiB"] = max(peak["workingSetMiB"], w)
                    gr = read_graphics_free()
                    if gr.ok and gr.total_mib is not None and gr.free_mib is not None:
                        peak["graphicsUsedMiB"] = max(peak["graphicsUsedMiB"], gr.total_mib - gr.free_mib)
                return caps_obs(ev)
            t0 = time.time()
            env = {"DOTNET_GCHeapHardLimit": hex(int(constrained_heap_mib) << 20)} if mode == "constrained" and constrained_heap_mib else {}
            res = runlog.Runner("planeassess-render", out_dir=str(out / "logs")).run(
                [{"id": tag, "argv": argv, "cwd": str(out), "env": env}], echo=False, observer=observe, poll_seconds=1.0)[0]
            secs = round(time.time() - t0, 1)
            row = {"chunk": tag, "shots": [s["label"] for s in todo], "exitCode": res.get("exitCode"),
                   "stopped": res.get("stopped"), "seconds": secs, "secondsPerShot": round(secs / len(todo), 1),
                   "peakCommitMiB": round(peak["commitMiB"]), "peakWorkingSetMiB": round(peak["workingSetMiB"]),
                   "peakGraphicsUsedMiBWholeGpu": round(peak["graphicsUsedMiB"]),
                   "freeAtStartMiB": round(free.free_mib), "memoryMode": mode, "heapLimitMiB": constrained_heap_mib if env else None,
                   **ident, "at": time.strftime("%H:%M:%S")}
            with ledger.open("a", encoding="utf-8") as fh:
                fh.write(json.dumps(row) + "\n")
            print(json.dumps(row), flush=True)
            done += len(todo)
            logs = sorted((out / "logs").glob("*%s*" % tag), key=lambda f: f.stat().st_mtime)
            # a heap-cap refusal in auto mode is retried once; a retry that refuses again is recorded, not retried
            if env and memory_mode == "auto" and res.get("exitCode") not in (0, 3) and logs and out_of_memory(logs[-1]) \
                    and not any(x.get("_retry") for x in todo):
                retry_full = read_system_free().free_mib >= need_free_mib
                again = [todo] if retry_full else [todo[i:i + 3] for i in range(0, len(todo), 3)]
                for c in again:
                    c_marked = [dict(x, _retry="heap-cap Out of memory in %s; retried %s" % (
                        tag, "uncapped" if retry_full else "capped at 3 shots")) for x in c]
                    retries.append((c_marked, retry_full))
                oom.append(tag)
                done -= len(todo)
                with ledger.open("a", encoding="utf-8") as fh:
                    fh.write(json.dumps({"chunk": tag, "event": "heap-cap Out of memory", "retry": "uncapped" if retry_full
                                         else "capped at 3 shots", "at": time.strftime("%H:%M:%S")}) + "\n")
                chunks[n + 1:n + 1] = [c for c, _ in retries[-len(again):]]
                for c, full_ok in retries[-len(again):]:
                    if full_ok:
                        force_full.add(id(c))
                continue
            if depth_measurement and res.get("exitCode") in (0, 3):
                unmarked = [r for r in sorted(dest.glob("*-fp-planes.json")) if not report_records_mode(r)]
                if unmarked:
                    print("refusing further chunks: %s does not record the depth-measurement mode although it was asked "
                          "for; the renderer ignored the flag." % unmarked[0], flush=True)
                    failed.append(tag)
                    break
            if occlusion_query and res.get("exitCode") in (0, 3):
                unmarked = [r for r in sorted(dest.glob("*-fp-planes.json")) if not report_records_query(r)]
                if unmarked:
                    print("refusing further chunks: %s does not record the occlusion query although it was asked for; "
                          "the renderer ignored the flag." % unmarked[0], flush=True)
                    failed.append(tag)
                    break
            if res.get("stopped") or res.get("exitCode") not in (0, 3):
                failed.append(tag)
                print("chunk %s: stopped %s, exit %s; recorded, continuing" % (tag, res.get("stopped"), res.get("exitCode")),
                      flush=True)
            ws = todo[0]["worldspace"]
            if probe_commit_mib and ws not in probed and len(todo) > fallback_chunk:
                probed.add(ws)
                if peak["commitMiB"] > probe_commit_mib:
                    rest = [s for c in chunks[n + 1:] if c[0]["worldspace"] == ws for s in c]
                    others = [c for c in chunks[n + 1:] if c[0]["worldspace"] != ws]
                    regroup = defaultdict(list)
                    for s in rest:
                        regroup[s["cell"]].append(s)
                    small = [v[i:i + fallback_chunk] for v in regroup.values() for i in range(0, len(v), fallback_chunk)]
                    chunks = chunks[:n + 1] + small + others
                    print("peak commit %d MiB > %d in %s: remaining %d shots re-chunked by %d"
                          % (peak["commitMiB"], probe_commit_mib, ws, len(rest), fallback_chunk), flush=True)
    finally:
        Path(lock).unlink(missing_ok=True)
    print("chunks %d, stopped or failed %d: %s; heap-cap Out of memory %d: %s; measurement mode %s%s"
          % (len(chunks), len(failed), failed, len(oom), oom, DEPTH_MODE if depth_measurement else "default",
             " with the occlusion query" if occlusion_query else ""), flush=True)
    return done


# ------------------------------------------------------------------ step 4: score the rendered chunks
def report_facts(d, planes, city):
    """What the verdict reads from one report, per plane of `planes`: the report's plane entry, hidden px by owner,
    every hidden shape with its owner (A = the content mod's records), census and visibility status of the content rows
    under that plane, and the rule's refusal; plus, once per report, every shape hidden by any plane ("subject|shape").
    Masks are read for the listed planes only."""
    import occlscore as S
    planes = set(planes)
    sv = S.score_view(d, planes=planes)
    cen = S.census(d, city, planes)
    entries = {pl["formKey"]: {k: v for k, v in pl.items() if k != "candidates"} for pl, _ in S.rows_by_plane(d)
               if pl["formKey"] in planes}
    under, _ = S.unreadable(d, planes)
    out = {}
    for fk in planes:
        sc = sv.get(fk)
        shapes = None if sc is None else [{"subject": x["subject"], "shape": x["shape"], "inBandPx": x["inBandPx"],
                                           "owner": "A" if x["subject"] in city else "other"} for x in sc["shapes"]]
        out[fk] = {"plane": entries.get(fk),
                   "score": None if sc is None else {"cityInBandPx": sum(x["inBandPx"] for x in shapes if x["owner"] == "A"),
                                                     "otherInBandPx": sum(x["inBandPx"] for x in shapes if x["owner"] != "A")},
                   "shapes": shapes, "census": cen.get(fk), "visStatus": S.vis_status(d, city, {fk}),
                   "refusal": S.refusal(d, fk), "unreadableUnder": under.get(fk, [])}
    return out, sorted(S.hidden_anywhere(d))


def score_step(g, arm, shots_file, render_out, scores_out):
    """Score every rendered, not yet scored report of one arm: per shot, the facts of each edited plane in view, written
    to <scores_out>/<label>.json. Then the report is gzipped (raw deleted) and its evidence masks are kept only when an
    edited plane hides content there (G5's retention rule, widened to any owner)."""
    import gzip
    import shutil
    import occlscore as S
    city = set()
    for name in g["owners"]["A"]:
        city |= {r["formKey"] for r in Plugin(g["_rel"](g["pluginFiles"][name])).placed()}
    shots = {s["label"].replace("-", "_"): s for s in json.loads(Path(shots_file).read_text(encoding="utf-8"))["shots"]}
    out = Path(scores_out)
    out.mkdir(parents=True, exist_ok=True)
    done = 0
    reps = sorted(Path(render_out).glob("*/*-fp-planes.json")) + sorted(Path(render_out).glob("*/*-fp-planes.json.gz"))
    for rep in reps:
        zipped = rep.suffix == ".gz"
        name = rep.name[:-3] if zipped else rep.name
        key = next((k for k in shots if name.endswith("-%s-fp-planes.json" % k)), None)
        if key is None:
            continue
        s = shots[key]
        dest = out / (s["label"] + ".json")
        if dest.exists():
            continue
        raw = gzip.decompress(rep.read_bytes()) if zipped else rep.read_bytes()
        d = json.loads(raw.decode("utf-8"))
        try:
            facts, anywhere = report_facts(d, s["planes"], city)
        except FileNotFoundError as ex:      # a mask already thinned away: name it, never guess
            print("cannot score %s: %s" % (s["label"], ex), flush=True)
            continue
        hides = any(((f["score"] or {}).get("cityInBandPx", 0) + (f["score"] or {}).get("otherInBandPx", 0)) > 0
                    for f in facts.values())
        row = {"label": s["label"], "arm": arm, "gaze": key, "basis": s.get("basis", "route"), "worldspace": s["worldspace"],
               "report": str(rep) if zipped else str(rep) + ".gz", "reportSha256": hashlib.sha256(raw).hexdigest(), "reportBytes": len(raw),
               "masksKept": hides, "planes": facts, "hiddenAnywhere": anywhere,
               "unreadableAssets": S.unreadable(d, set())[1]}
        if not zipped:
            with gzip.open(str(rep) + ".gz", "wb", compresslevel=6) as fh:
                fh.write(raw)
            rep.unlink()
        dest.write_text(json.dumps(row), encoding="utf-8")      # after the gzip: a stop never leaves a scored raw
        ev = rep.with_name(name.replace("-fp-planes.json", "-fp-audit-evidence"))
        if not hides and ev.is_dir():
            shutil.rmtree(ev)
        done += 1
        print("scored", s["label"], "hides" if hides else "clean", flush=True)
    return done


def unrendered_verdict(why, kind, box_label=None):
    """The verdict of a plane record no rendered gaze sees, from the scope step's named reason: a change viewprobe does
    not read for culling is decided from the records (set aside: no change to what is culled); a rule refusal of a tilted
    shape reads 'rule refuses a tilted plane'; anything else stays cannot tell with its reason."""
    why = why or "no rendered view"
    if why.startswith("no change to the culling shape"):
        return {"verdict": "set aside", "reason": "no change to what is culled (records)",
                "statement": "set aside: no change to what is culled (%s)" % why.split("(", 1)[1].rstrip(")"),
                "shown": True, "basis": None, "unjudgedShare": None, "boxLabel": box_label if kind == "Box" else None, "also": []}
    if why.startswith("rule refuses the shape at every gaze"):
        refs = why.split(": ", 1)[1]
        why = "rule refuses a tilted plane" if ("tilted" in refs or "not upright" in refs) else "rule refuses the shape: " + refs
    return {"verdict": "cannot tell", "reason": why, "statement": "cannot tell: " + why, "shown": True, "basis": None,
            "unjudgedShare": None, "boxLabel": box_label if kind == "Box" else None, "also": []}


def out_of_memory(log_path):
    """A heap-cap refusal in a run's log: viewprobe's "Out of memory." or a .NET OutOfMemoryException (a capped heap
    refused while reading plugins, seen 2026-10-08 at a 512 MiB cap)."""
    try:
        t = Path(log_path).read_text(encoding="utf-8", errors="replace")
        return "Out of memory." in t or "OutOfMemoryException" in t
    except OSError:
        return False


# ------------------------------------------------------------------ step 5: a verdict per plane edit
def verdicts_step(g, out, scores_root):
    """Decide every plane record the patch carries from the arm score files (<scores_root>/u, /p): one entry per record
    with its edits, verdict, statement, basis, unjudged share, Box label and the gazes behind it -> <out>/planes.json.
    Only edited planes are scored in the block, so `missed` (unedited planes) is not computed here."""
    import verdict as V
    rec = {r["formKey"]: r for r in json.loads((Path(out) / "patch.json").read_text(encoding="utf-8"))["records"]}
    scope_why = {r["formKey"]: r["outsideReason"] for r in
                 json.loads((Path(out) / "scope.json").read_text(encoding="utf-8"))["planes"] if r.get("outsideReason")}
    owners = g["owners"]
    views = defaultdict(list)
    have = {arm: {f.name for f in (Path(scores_root) / arm).glob("*.json")} for arm in ("u", "p")}
    both = have["u"] & have["p"]          # a gaze counts only when both arms are scored: a missing arm is not "absent"
    for arm in ("u", "p"):
        for name in sorted(both):
            row = json.loads((Path(scores_root) / arm / name).read_text(encoding="utf-8"))
            for fk, facts in row["planes"].items():
                views[fk].append(dict(facts, gaze=row["gaze"], arm=arm, basis=row.get("basis", "route"),
                                      hiddenAnywhere=row.get("hiddenAnywhere"), unreadableAssets=row.get("unreadableAssets") or []))
    rows = []
    for fk, r in sorted(rec.items()):
        ver = lambda x: None if x is None else {"position": x["pos"], "active": active(x)}
        edit = {"formKey": fk, "class": r["class"], "edits": r["edits"], "kind": r["kind"], "owners": owners,
                "winners": {"u": r["priorOwner"], "p": owners["patch"]}, "versions": {"u": ver(r["u"]), "p": ver(r["p"])}}
        vs = views.get(fk, [])
        if vs:
            d = V.decide(edit, vs)
        else:   # never rendered: the scope step's named reason (a refused shape, no station, no change)
            d = unrendered_verdict(scope_why.get(fk), r["kind"], V.BOX_LABEL)
        rows.append({"formKey": fk, "worldspace": r["worldspace"], "kind": r["kind"], "class": r["class"], "edits": r["edits"],
                     "priorOwner": r["priorOwner"], **d, "gazes": sorted({v["gaze"] for v in vs})})
    doc = {"schema": "planeassess/planes/1", "group": g["name"], "gazesBothArms": len(both),
           "gazesOneArmOnly": len((have["u"] | have["p"]) - both), "records": rows,
           "byVerdict": _count(rows, lambda x: (x["verdict"],)),
           "editsByVerdict": _count([(x["verdict"], k) for x in rows for k in x["edits"]], lambda t: (t[0],))}
    (Path(out) / "planes.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")
    print(json.dumps({k: doc[k] for k in ("byVerdict", "editsByVerdict")}, indent=1))
    return doc


# ------------------------------------------------------------------ predictive mode (predict.py)
def _arm_order(g, arm, order_file=None):
    if order_file:
        return json.loads(Path(order_file).read_text(encoding="utf-8"))["Plugins"]
    return order_plugins(g, arm)


def finished_chunks(render_out):
    """Chunk folders a render ledger records as finished (exit 0 or 3, not stopped); with no ledger, every folder (a
    copied or hand-made render folder). A chunk still rendering has no row yet, so its reports are never read half-written."""
    f = Path(render_out) / "ledger.jsonl"
    if not f.exists():
        return {d.name for d in Path(render_out).iterdir() if d.is_dir()}
    out = set()
    for line in f.read_text(encoding="utf-8").splitlines():
        r = json.loads(line) if line.strip() else {}
        if "shots" in r and not r.get("stopped") and r.get("exitCode") in (0, 3):
            out.add(r["chunk"])
    return out


def rescore_step(g, arm, renders, out, order_file=None, gzip_raw=False):
    """Every plane of every planes report under `renders` (raw or gzipped) -> <out>/<view label>.json (predict.rescore_view
    with the arm's plane winners and the mods' drawn content). A view already re-scored is skipped. With `gzip_raw`, a raw
    report is gzipped after its facts are written; masks are never deleted here."""
    import gzip
    import predict as X
    own = X.record_owners(g, _arm_order(g, arm, order_file))
    own_u = X.record_owners(g, order_plugins(g, "u")) if arm != "u" else own
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    reps = []
    for r in renders:
        reps += sorted(Path(r).glob("*/*-fp-planes.json")) + sorted(Path(r).glob("*/*-fp-planes.json.gz"))
    reps = [p for p in reps if p.parent.name in finished_chunks(p.parent.parent)]
    done, seen = 0, set()
    for rep in reps:
        zipped = rep.suffix == ".gz"
        raw = gzip.decompress(rep.read_bytes()) if zipped else rep.read_bytes()
        d = json.loads(raw.decode("utf-8"))
        label = d.get("viewLabel")
        dest = out / ("%s.json" % label)
        if label in seen or dest.exists():
            seen.add(label)
            continue
        seen.add(label)
        facts = X.rescore_view(d, own["winner"], own_u["content"], own["mods"])
        facts.update({"arm": arm, "report": str(rep) + ("" if zipped else ".gz" if gzip_raw else ""),
                      "reportSha256": hashlib.sha256(raw).hexdigest(),
                      "basis": POSE if str(label).endswith("-pose") else "route"})
        if gzip_raw and not zipped:
            with gzip.open(str(rep) + ".gz", "wb", compresslevel=6) as fh:
                fh.write(raw)
            rep.unlink()
        dest.write_text(json.dumps(facts), encoding="utf-8")
        done += 1
        print("rescored", label, flush=True)
    return done


def _load_facts(dirs):
    out = {}
    for dd in dirs or []:
        for f in sorted(Path(dd).glob("*.json")):
            x = json.loads(f.read_text(encoding="utf-8"))
            out.setdefault(x.get("label") or f.stem, x)
    return out


def predict_step(g, facts_dirs, direction, out, without_dirs=None):
    """Predicted findings for one direction (F: the content mod A is added; R: the plane mod B is added) from u facts,
    with the render without the new mod where its override of a master plane needs it."""
    import predict as X
    own = X.record_owners(g, order_plugins(g, "u"))
    facts = _load_facts(facts_dirs)
    without = _load_facts(without_dirs) if without_dirs else None
    new = "A" if direction == "F" else "B"
    views = [{"gaze": k, "basis": v.get("basis", "route"), "facts": v} for k, v in sorted(facts.items())]
    fs = X.findings(views, direction, own["overridesMaster"][new] if direction == "R" else (), without)
    doc = {"schema": "planeassess/predict/1", "group": g["name"], "direction": direction, "newMod": g["owners"][new],
           "views": len(views), "findings": fs,
           "byClassStatus": _count(fs, lambda f: (f["class"], f["status"]))}
    Path(out).write_text(json.dumps(doc, indent=1), encoding="utf-8")
    print(json.dumps(doc["byClassStatus"], indent=1))
    return doc


def answerkey_step(g, findings_file, p_dirs, patch_json, out, evidence_file=None):
    """Each shown finding read against the patched order (p facts where rendered, the patch's records elsewhere), and the
    patch's edits to planes the content mod does not own that no shown finding explains (blind spots)."""
    import predict as X
    doc = json.loads(Path(findings_file).read_text(encoding="utf-8"))
    p_own = X.record_owners(g, order_plugins(g, "p"))
    pfacts = _load_facts(p_dirs)
    recs = json.loads(Path(patch_json).read_text(encoding="utf-8"))["records"]
    edits = {r["formKey"]: r["edits"] for r in recs}
    evidence = json.loads(Path(evidence_file).read_text(encoding="utf-8")) if evidence_file else {}
    rows = []
    for f in doc["findings"]:
        if f["class"] != "shown" or f["status"] not in ("will hide", "possible only: straddles the band, mask thinned"):
            continue
        k = X.answer_key(f, {gz: pfacts.get(gz) for gz in f["views"]}, p_active=p_own["active"], p_edits=edits,
                         evidence=evidence)
        rows.append({"plane": f["plane"], "status": f["status"], "statement": f["statement"],
                     "patchEdits": edits.get(f["plane"], []), **k})
    a_names = g["owners"]["A"]
    non_a = {r["formKey"]: r["edits"] for r in recs if r.get("priorOwner") and r["priorOwner"] not in a_names
             and set(r["edits"]) & set(X.SHAPE_KINDS)}
    shown = {f["plane"] for f in doc["findings"] if f["class"] == "shown"}
    out_doc = {"schema": "planeassess/answerkey/1", "group": g["name"], "direction": doc["direction"], "rows": rows,
               "byOutcome": _count(rows, lambda r: (r["outcome"],)),
               "unexplainedEdits": X.blind_spots(non_a, shown, evidence)}
    Path(out).write_text(json.dumps(out_doc, indent=1), encoding="utf-8")
    print(json.dumps(out_doc["byOutcome"], indent=1))
    return out_doc


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("selftest", help="known answers for the frustum test and the rule's shape classes")
    a = sub.add_parser("patch", help="step 1: classify every plane edit the patch carries")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--out", required=True, help="output folder for patch.json")
    a = sub.add_parser("scope", help="step 2: the station gazes where an edited plane is in view")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--out", required=True, help="folder holding patch.json; scope.json is written here")
    a = sub.add_parser("stations", help="sample route stations per worldspace with the deployed viewprobe")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--viewprobe", required=True, help="deployed viewprobe.exe (never rebuilt)")
    a.add_argument("--work", required=True, help="viewprobe output folder (large files stay here)")
    a.add_argument("--copy-to", required=True, help="folder that receives the compact outputs")
    a.add_argument("--instance", required=True, help="MO2 instance folder (read only)")
    a.add_argument("--game", required=True, help="game folder")
    a.add_argument("--only", action="append", help="worldspace label to sample (repeat); default all")
    a.add_argument("--exempt", action="append", default=[], help="process path that does not count as busy (repeat)")
    a.add_argument("--lock", action="append", default=[], help="lock file whose presence refuses the run (repeat)")
    a = sub.add_parser("shots", help="step 3a: the render list of kept gazes for a group")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--out", required=True, help="folder holding scope.json; shots.json is written here")
    a.add_argument("--per-worldspace", type=int, help="timed sample: N gazes from each worldspace's busiest cell")
    a.add_argument("--include", action="append", default=[], help="station:gazeKind to add (repeat)")
    a.add_argument("--worldspace", action="append", help="limit to this worldspace label (repeat)")
    a = sub.add_parser("render", help="step 3b: render a shots list in one arm, one viewprobe process at a time")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--arm", required=True, choices=["u", "p"], help="u: without the patch; p: with it")
    a.add_argument("--shots", required=True, help="shots.json from the shots step")
    a.add_argument("--render-out", required=True, help="output folder for reports, worklists and the ledger")
    a.add_argument("--viewprobe", required=True, help="deployed viewprobe.exe (never rebuilt)")
    a.add_argument("--instance", required=True, help="MO2 instance folder (read only)")
    a.add_argument("--game", required=True, help="game folder")
    a.add_argument("--caps", required=True, help="process commit, system floor, graphics used caps in MiB, comma separated")
    a.add_argument("--need-free-mib", required=True, type=float, help="free RAM required before each chunk")
    a.add_argument("--heavy-lock", required=True, help="lock file held for the block; refuses if present")
    a.add_argument("--pause", required=True, help="PAUSE file: present means no new chunk starts")
    a.add_argument("--no-reuse-cell", action="store_true", help="each shot builds its own scene (viewprobe reuses by default)")
    a.add_argument("--chunk", type=int, default=9, help="shots per viewprobe process (default 9)")
    a.add_argument("--probe-commit-mib", type=float, help="re-chunk a worldspace whose first full chunk peaks above this")
    a.add_argument("--fallback-chunk", type=int, default=3, help="shots per process after a probe-commit crossing (default 3)")
    a.add_argument("--memory-mode", choices=["full", "constrained", "auto"], default="full",
                   help="full, constrained (one shot per process, capped heap) or auto (picked from free memory)")
    a.add_argument("--constrained-need-mib", type=float, help="free RAM the constrained mode needs before a shot")
    a.add_argument("--constrained-heap-mib", type=float, help="managed heap cap per process in the constrained mode")
    a.add_argument("--constrained-chunk", type=int, default=1, help="shots per process in the constrained mode (default 1)")
    a.add_argument("--exempt", action="append", default=[], help="process path that does not count as busy (repeat)")
    a.add_argument("--depth-measurement", action="store_true",
                   help="pass viewprobe --depth-measurement (same measurement, no appearance work); off by default")
    a.add_argument("--occlusion-query", action="store_true",
                   help="with --depth-measurement: pass viewprobe --occlusion-query (GPU counts; isolated depth hashed "
                        "only for shapes with visible pixels); off by default")
    a = sub.add_parser("score", help="step 4: score rendered reports of one arm, then gzip them and thin their masks")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--arm", required=True, choices=["u", "p"], help="u: without the patch; p: with it")
    a.add_argument("--shots", required=True, help="shots.json the arm was rendered from")
    a.add_argument("--render-out", required=True, help="the render step's output folder")
    a.add_argument("--scores-out", required=True, help="folder for one score file per shot")
    a = sub.add_parser("verdicts", help="step 5: a verdict per plane edit from the arms' score files")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--out", required=True, help="folder holding patch.json; planes.json is written here")
    a.add_argument("--scores", required=True, help="folder holding the u and p score folders")
    a = sub.add_parser("rescore", help="predictive: every plane of every planes report in render folders -> per-view facts")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--arm", required=True, help="u, p, or a label for --order (e.g. plusa)")
    a.add_argument("--order", help="order JSON for an arm other than u and p")
    a.add_argument("--renders", required=True, action="append", help="render output folder (repeat)")
    a.add_argument("--out", required=True, help="folder for one facts file per view")
    a.add_argument("--gzip", action="store_true", help="gzip each raw report after its facts are written (masks kept)")
    a = sub.add_parser("predict", help="predictive: findings for a mod being added, from u facts")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--facts", required=True, action="append", help="u facts folder (repeat)")
    a.add_argument("--direction", required=True, choices=["F", "R"], help="F: the content mod is added; R: the plane mod")
    a.add_argument("--without", action="append", help="facts folder of the order without the new mod (repeat)")
    a.add_argument("--out", required=True, help="findings JSON")
    a = sub.add_parser("answerkey", help="predictive: the shown findings read against an existing patch")
    a.add_argument("--group", required=True, help="group definition JSON")
    a.add_argument("--findings", required=True, help="findings JSON from predict")
    a.add_argument("--p-facts", action="append", default=[], help="p facts folder (repeat)")
    a.add_argument("--patch-json", required=True, help="patch.json from the patch step")
    a.add_argument("--evidence", help="JSON {plane formKey: evidence text} (engine observations, outside lists)")
    a.add_argument("--out", required=True, help="answer-key JSON")
    args = ap.parse_args(argv)
    if args.cmd in ("rescore", "predict", "answerkey"):
        g = load_group(args.group)
        with runlog.Run("planeassess-" + args.cmd, stage_source="single-stage"):
            if args.cmd == "rescore":
                print("rescored", rescore_step(g, args.arm, args.renders, args.out, args.order, args.gzip))
            elif args.cmd == "predict":
                predict_step(g, args.facts, args.direction, args.out, args.without)
            else:
                answerkey_step(g, args.findings, args.p_facts, args.patch_json, args.out, args.evidence)
        return 0
    if args.cmd == "selftest":
        with runlog.Run("planeassess-selftest", stage_source="single-stage"):
            return 1 if geometry_selftest() else 0
    g = load_group(args.group)
    if args.cmd == "patch":
        patch_step(g, args.out)
        return 0
    if args.cmd == "scope":
        scope_step(g, args.out)
        return 0
    if args.cmd == "verdicts":
        with runlog.Run("planeassess-verdicts", stage_source="single-stage"):
            verdicts_step(g, args.out, args.scores)
        return 0
    if args.cmd == "score":
        with runlog.Run("planeassess-score", stage_source="single-stage"):
            print("scored", score_step(g, args.arm, args.shots, args.render_out, args.scores_out))
        return 0
    if args.cmd == "shots":
        with runlog.Run("planeassess-shots", stage_source="single-stage"):
            shots_step(g, args.out, args.per_worldspace, set(args.include), args.worldspace)
        return 0
    if args.cmd == "render":
        caps = [float(x) for x in args.caps.split(",")]
        n = render_step(g, args.arm, args.shots, args.render_out, args.viewprobe, args.instance, args.game, caps,
                        args.need_free_mib, args.heavy_lock, args.pause, args.no_reuse_cell, args.chunk, args.exempt,
                        args.probe_commit_mib, args.fallback_chunk, args.memory_mode, args.constrained_need_mib,
                        args.constrained_heap_mib, args.constrained_chunk, args.depth_measurement, args.occlusion_query)
        print("rendered", n)
        return 0
    return stations_step(g, args.viewprobe, args.work, args.copy_to, args.instance, args.game, args.only,
                         args.exempt, args.lock)


if __name__ == "__main__":
    sys.exit(main())
