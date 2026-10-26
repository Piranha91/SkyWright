#!/usr/bin/env python3
"""Measured points on a face of each capture participant that an eye-level camera outside the pair can see.

    python capture_points.py pick --packet storyboard.json --request capture-xxxxxxxxxxxx \
        --out points.json [--bundle DIR] [--work DIR] [--vp CMD] [--synthetic] -- <viewprobe target flags>

    (or let `capture_acquire.py acquire --points auto` call it)

WHY. The camera planner verifies a declared surface point by one depth ray from the eye it chooses: the point passes only
when that ray stops ON it. A point taken by a ray from the object's own origin, or seen from above, can lie on a face the
partner covers or that no eye-level camera sees, and every pose is then refused. This tool asks the planner's own
question before handing a point over: from an eye-level camera standing around the pair, which pixels of the frame are
THIS object's surface, unoccluded, and where in the world are they?

HOW, in three viewprobe runs, each one cell load:
  1. placements   `--eye-ref A --eye-height 0 --look-ref B`: viewprobe resolves both refs in the cell's winning state and
                  prints the camera (`eye X,Y,Z -> look X,Y,Z`; the look is lifted 96 u, taken back off here).
  2. floor        `--nav-at` / `--clear-at` at the pair and at every station, `--refs-at` at each participant (drawn or
                  not) and `--ref ... --ref-surface` (a participant with no opaque face says so). The floor is the
                  walkable storey the stations and the pair share (navmesh first; else the first placed surface below
                  the pair's level), never a heightmap read at an XY regardless of what stands over it. A pair whose
                  floor lies under the cell's water surface is refused: no camera stands on that floor.
  3. witnesses    one static worklist of stations on a ring around the pair (12 bearings x 3 standoffs, 150-400 u, eye at floor +
                  eye height, looking at the pair), every shot with `--ref A=cyan --ref B=magenta --ref-surface`, a
                  grid of `--pick` pixels and the planner's own placement queries at the station eye.

"VISIBLE" means, for participant P at station S: the pixel carries P's flat `--ref-surface` colour (which the renderer
draws from P's own placed opaque triangles, depth-tested, so it shows only where nothing stands in front), lies at least
`--min-depth` pixels inside P's colour region (a pose rounded by the planner still lands on P), and the pick there hit
geometry. The pick's world point is then on P's face, and the depth ray from S stops at it: exactly what the planner
checks. Only stations that pass the planner's own placement verdicts (navmesh storey, support, inside) count, and only
stations that show EVERY participant can witness the pair. The chosen station is the one whose weakest participant is
deepest inside its colour; the bearings around it that also witness the pair become the planner's `bearingWindow`.

WHAT IT DOES NOT CLAIM. A visible point is not visual qualification. Alpha-blended geometry in front of a point is not
in the depth buffer (the planner's own stated limit). The colours are the renderer's report-mode values for its two
participant marks, measured 2026-10-19; a renderer that draws them differently finds no participant and refuses.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import math
import re
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
SHOTPLAN = HERE / "viewprobe" / "tools" / "shotplan.py"

EXIT_OK, EXIT_REFUSED = 0, 2

# viewprobe's --ref-surface marks as they arrive in a report-mode PNG (flat overlay after tone mapping), read off the
# renderer's own frames 2026-10-19: almost every marked pixel carries exactly this value; edges and blends do not.
SURFACE_RGB = {"cyan": (19, 255, 255), "magenta": (255, 76, 255)}
COLOUR_TOL = 2

EYE_LINE = re.compile(r"^\s*eye (-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?) -> look "
                      r"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)")
NOT_PLACED = re.compile(r"'([^']+)' is not a placed ref in this cell's winning state")
SURFACE_REFUSED = re.compile(r"^subject-surface-refused: (\S{6}:.+?\.es[pml]):")
WATER_LINE = re.compile(r"^\s*water z (-?\d+(?:\.\d+)?): ")
SHOT_HEAD = re.compile(r"^=== worklist cell \d+/\d+ shot (\d+)/(\d+): (.*) ===\s*$")
LOOK_LIFT = 96.0   # viewprobe's --look-ref lift (Program.cs RefPos(lookRef, 96f))


class Unpickable(Exception):
    """No measured visible point can be given. `detail` is one plain sentence: what went wrong and what to try."""

    def __init__(self, code: str, detail: str, **extra):
        detail = detail.strip()
        detail = detail if detail.endswith(".") else detail + "."
        super().__init__(f"{code}: {detail}")
        self.code, self.detail, self.extra = code, detail, extra


def _load_shotplan():
    spec = importlib.util.spec_from_file_location("housecarl_shotplan_points", SHOTPLAN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _load_runlog():
    cand = HERE.parent / "scripts" / "runlog.py"
    if not cand.is_file():
        return None
    try:
        spec = importlib.util.spec_from_file_location("housecarl_runlog", cand)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod
    except Exception:
        return None


class _NullRun:
    def __getattr__(self, _name):
        return lambda *a, **k: self

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _run(op, **kw):
    mod = _load_runlog()
    if mod is None:
        return _NullRun()
    try:
        return mod.Run(op, **kw)
    except Exception:
        return _NullRun()


# ---------------------------------------------------------------- the request --

def request_cell(request: dict) -> str:
    """viewprobe's first positional for the request's location: the label without its `(ext)`/`(int)` tail."""
    loc = str(request.get("location") or "").strip()
    loc = re.sub(r"\s*\((?:ext|int)\)\s*$", "", loc).strip()
    if not loc or " " in loc:
        raise Unpickable("points-cell-unknown",
                         f"the request's location {request.get('location')!r} does not name one cell; pass --cell")
    return loc


def request_participants(request: dict) -> list[dict]:
    # a world-state pair may mark only its smaller participant; its points are measured on both (pointParticipants)
    rows = [h for h in (request.get("pointParticipants") or request.get("highlights") or []) if isinstance(h, dict) and h.get("formKey")]
    if len(rows) != 2 or rows[0]["formKey"] == rows[1]["formKey"]:
        raise Unpickable("points-participants-unusable",
                         "the picker measures a pair: the request must highlight exactly two distinct records")
    for h in rows:
        if h.get("color") not in SURFACE_RGB:
            raise Unpickable("points-participants-unusable",
                             f"{h['formKey']} is highlighted {h.get('color')!r}; the picker reads only cyan and magenta marks")
    return [{"formKey": h["formKey"], "color": h["color"]} for h in rows]


# ---------------------------------------------------------------- viewprobe --

class Renderer:
    """The viewprobe command and the target flags, and one call = one process = one cell load."""

    def __init__(self, base: list[str], passthrough: list[str], work: Path, shotplan):
        self.base, self.passthrough, self.work, self.S = list(base), list(passthrough), work, shotplan
        self.calls: list[dict] = []

    def call(self, tag: str, head: list[str], flags: list[str]) -> tuple[int, str]:
        out = self.work / tag
        out.mkdir(parents=True, exist_ok=True)
        cmd = self.base + head + [str(f) for f in flags] + ["--out", str(out)] + self.passthrough
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        text = (p.stdout or "") + (p.stderr or "")
        (out / "console.txt").write_text(text, encoding="utf-8")
        self.calls.append({"tag": tag, "exitCode": p.returncode, "console": f"{tag}/console.txt"})
        return p.returncode, text

    def parsed(self, text: str):
        probe = self.S.Probe([])
        probe.stdout = text
        return probe


def ensure_ok(rc: int, text: str, what: str):
    if rc != 0:
        tail = " ".join(text.strip().splitlines()[-3:])[-300:]
        raise Unpickable("points-renderer-failed", f"viewprobe exited {rc} during the {what} run ({tail})")


# ---------------------------------------------------------------- the geometry of the ring --

def bearing_vec(deg: float) -> tuple[float, float]:
    r = math.radians(deg)
    return math.sin(r), math.cos(r)


def bearing_deg(dx: float, dy: float) -> float:
    return math.degrees(math.atan2(dx, dy)) % 360.0


def stations(mid, bearings: int, standoffs) -> list[dict]:
    out = []
    for si, st in enumerate(standoffs):
        for bi in range(bearings):
            b = bi * 360.0 / bearings
            ux, uy = bearing_vec(b)
            out.append({"id": f"s{len(out) + 1:02d}", "bearing": b, "standoff": float(st), "ring": si, "slot": bi,
                        "xy": (int(round(mid[0] + ux * st)), int(round(mid[1] + uy * st)))})
    return out


def depth_map(mask: np.ndarray) -> np.ndarray:
    """Pixels inside a region, labelled by how many 4-neighbour erosions they survive (0 = outside)."""
    depth = np.zeros(mask.shape, dtype=np.int32)
    cur = mask.copy()
    level = 0
    while cur.any():
        level += 1
        depth[cur] = level
        e = cur.copy()
        e[1:, :] &= cur[:-1, :]
        e[:-1, :] &= cur[1:, :]
        e[:, 1:] &= cur[:, :-1]
        e[:, :-1] &= cur[:, 1:]
        e[0, :] = e[-1, :] = False
        e[:, 0] = e[:, -1] = False
        cur = e
    return depth


def colour_mask(img: np.ndarray, colour: str) -> np.ndarray:
    want = np.array(SURFACE_RGB[colour], dtype=np.int32)
    return np.abs(img.astype(np.int32) - want).max(axis=2) <= COLOUR_TOL


# ---------------------------------------------------------------- the floor --

def choose_floor(levels: list[float], minz: float, maxz: float, below: float, above: float):
    """The storey most measurements agree on within [minz - below, maxz + above]; ties to the one nearest `minz`."""
    near = [z for z in levels if minz - below <= z <= maxz + above]
    if not near:
        return None
    votes: dict[int, list[float]] = {}
    for z in near:
        votes.setdefault(int(round(z / 8.0)), []).append(z)
    best = max(votes.values(), key=lambda zs: (len(zs), -abs(sum(zs) / len(zs) - minz)))
    return round(sum(best) / len(best), 1)


# ---------------------------------------------------------------- the picker --

def pick_points(request: dict, *, vp: str | None, passthrough: list[str], work: Path, cell: str | None = None,
                bearings: int = 12, standoffs=(150.0, 250.0, 400.0), size: int = 320, grid: int = 10, fov: float = 65.0,
                min_depth: int = 2, synthetic: bool = False, aspect: str = "16:9", shotplan=None) -> dict:
    """The points document `capture_acquire.py` reads, or Unpickable with one plain sentence."""
    S = shotplan or _load_shotplan()
    ap = argparse.ArgumentParser()
    S.add_common(ap, need_subjects=False)
    args = ap.parse_args(["picker"])   # the planner's own placement defaults: storey tolerance, inside test, reach
    parts = request_participants(request)
    cell = cell or request_cell(request)
    work.mkdir(parents=True, exist_ok=True)
    R = Renderer(S.vp_command(vp), passthrough, work, S)
    A, B = parts[0]["formKey"], parts[1]["formKey"]
    marks = []
    for p in parts:
        marks += ["--ref", f"{p['formKey']}={p['color']}"]

    # 1. placements
    rc, text = R.call("p0-placements", [cell], ["--eye-ref", A, "--eye-height", "0", "--look-ref", B,
                                                 "--size", "64", "--msaa", "0", "--nav", "off"])
    missing = NOT_PLACED.findall(text)
    if missing:
        raise Unpickable("points-not-placed",
                         f"{missing[0]} is not a placed reference in {cell}'s winning state, so it has no face to "
                         "measure; check the request's cell and load order")
    ensure_ok(rc, text, "placement")
    m = None
    for ln in text.splitlines():
        m = EYE_LINE.match(ln) or m
    if m is None:
        raise Unpickable("points-renderer-failed", "viewprobe did not print the resolved camera, so the "
                                                   "participants' placements are unknown")
    pos = {A: (float(m[1]), float(m[2]), float(m[3])), B: (float(m[4]), float(m[5]), float(m[6]) - LOOK_LIFT)}
    mid = tuple((pos[A][k] + pos[B][k]) / 2.0 for k in range(3))
    minz, maxz = min(pos[A][2], pos[B][2]), max(pos[A][2], pos[B][2])
    ring = stations(mid, bearings, standoffs)

    # 2. floor, drawn, opaque
    zq = int(round(maxz + 64))
    flags = ["--eye", f"{mid[0]:.0f},{mid[1]:.0f},{maxz + 600:.0f}", "--look", f"{mid[0]:.0f},{mid[1]:.0f},{mid[2]:.0f}",
             "--size", "64", "--msaa", "0", "--nav", "off", "--clear-reach", "2048", *marks, "--ref-surface"]
    for k in (A, B):
        flags += ["--refs-at", f"{pos[k][0]:.0f},{pos[k][1]:.0f},64", "--nav-at", f"{pos[k][0]:.0f},{pos[k][1]:.0f}"]
    flags += ["--nav-at", f"{mid[0]:.0f},{mid[1]:.0f}"]
    for st in ring:
        flags += ["--nav-at", f"{st['xy'][0]},{st['xy'][1]}", "--clear-at", f"{st['xy'][0]},{st['xy'][1]},{zq}"]
    rc, text = R.call("p1-floor", [cell], flags)
    ensure_ok(rc, text, "floor")
    # A participant with any alpha, effect or marker shape is reported by the renderer; its opaque shapes, if it has
    # any, are still marked. Only a participant with no marked face anywhere is refused for it (below).
    refused = sorted({mm[1] for ln in text.splitlines() if (mm := SURFACE_REFUSED.match(ln))} & {A, B})
    p1 = R.parsed(text)
    near = p1.refs_near()
    for k in (A, B):
        rows = [r for rows_ in near.values() for r in rows_ if r["formkey"] == k]
        if rows and not rows[0]["why"].startswith("drawn"):
            raise Unpickable("points-not-drawn",
                             f"{k} is not drawn by the renderer ({rows[0]['why']}), so no face of it can be measured")
    nav = p1.nav()
    levels = [z for e in nav.values() for z in e["levels"]]
    floor = choose_floor(levels, minz, maxz, 300.0, args.storey_tol)
    floor_src = "navmesh"
    if floor is None:
        downs = []
        for (x, y, z), e in p1.clear().items():
            d = (e["axes"].get("down") or {})
            if d.get("dist") is not None and d.get("formkey") not in (A, B):
                downs.append(z - d["dist"])
        floor = choose_floor(downs, minz, maxz, 300.0, args.storey_tol)
        floor_src = "the first placed surface or ground below the pair's level"
    if floor is None:
        raise Unpickable("points-no-floor",
                         f"no navmesh or placed surface within reach of the pair's level was measured in {cell}, so "
                         "no camera storey can be given; check the cell")
    water = [float(mm[1]) for ln in text.splitlines() if (mm := WATER_LINE.match(ln))]
    wet = [w for w in water if floor < w - 4.0 and minz < w + 64.0]
    if wet:
        raise Unpickable("points-under-water",
                         f"the pair stands in water: the measured floor z {floor:.0f} lies under the water surface at "
                         f"z {max(wet):.0f}, and no camera storey above the water was measured; choose another request")

    # 3. witnesses
    ez = int(round(floor + args.eye_height))
    lookz = min(max(mid[2], floor + 40.0), floor + 300.0)
    shots = [{"label": st["id"], "eye": [st["xy"][0], st["xy"][1], ez], "floorZ": floor,
              "look": [round(mid[0], 1), round(mid[1], 1), round(lookz, 1)], "fov": fov} for st in ring]
    wl = {"label": f"capture point witnesses for {request.get('id')}", "eyeHeight": args.eye_height,
          "cells": [{"cell": cell, "shots": shots}]}
    (work / "witness-worklist.json").write_text(json.dumps(wl, indent=1), encoding="utf-8")
    grid_px = [(x, y) for y in range(grid // 2, size, grid) for x in range(grid // 2, size, grid)]
    flags = [*marks, "--ref-surface", "--report-mode", "--size", str(size), "--msaa", "0", "--nav", "off",
             "--clear-reach", f"{args.clear_reach:g}"]
    for x, y in grid_px:
        flags += ["--pick", f"{x},{y}"]
    for st in ring:
        x, y = st["xy"]
        flags += ["--nav-at", f"{x},{y}", "--inside-at", f"{x},{y},{ez}", "--clear-at", f"{x},{y},{ez}"]
    rc, text = R.call("p2-witness", ["--worklist", str(work / "witness-worklist.json")], flags)
    ensure_ok(rc, text, "witness")
    chunks, cur = {}, None
    for ln in text.splitlines():
        hm = SHOT_HEAD.match(ln)
        if hm:
            cur = hm[3].strip()
            chunks[cur] = []
            continue
        if cur is not None:
            chunks[cur].append(ln)
    gridset = set(grid_px)
    seen = {p["formKey"]: 0 for p in parts}
    for st in ring:
        st["eye"] = [st["xy"][0], st["xy"][1], ez]
        chunk = chunks.get(st["id"])
        pngs = sorted((work / "p2-witness").glob(f"*-c1s{ring.index(st) + 1}-{st['id']}-fp.png"))
        st["frame"] = f"p2-witness/{pngs[0].name}" if pngs else None
        if chunk is None or not pngs:
            st.update(valid=False, why="the witness run gave no frame or console block for this station")
            continue
        pr = R.parsed("\n".join(chunk))
        key3, key2 = (st["xy"][0], st["xy"][1], ez), st["xy"]
        kind, nmsg = S.nav_verdict(pr.nav().get(key2), floor, args)
        eye = (float(st["xy"][0]), float(st["xy"][1]), float(ez))
        sup, smsg = S.support_verdict(pr.clear().get(key3), eye, floor, args)
        ikind, ifails, _notes = S.inside_verdict(pr.inside().get(key3), args)
        place = []
        if kind in ("wrong-storey", "no-floor", "unknown"):
            place.append(f"placement: {nmsg}")
        if kind == "off-mesh-near" and sup != "supported":
            place.append(f"placement: {nmsg}; and the geometry does not bear that out -- {smsg}")
        place += ifails
        img = np.asarray(Image.open(pngs[0]).convert("RGB"))
        picks = pr.picks()
        st["participants"] = {}
        for p in parts:
            mask = colour_mask(img, p["color"])
            dm = depth_map(mask)
            seen[p["formKey"]] = max(seen[p["formKey"]], int(mask.sum()))
            best = None
            for (x, y) in gridset:
                dpx = int(dm[y, x]) if y < dm.shape[0] and x < dm.shape[1] else 0
                hit = picks.get((x, y))
                if dpx < min_depth or hit is None:
                    continue
                centre = abs(x - size / 2) + abs(y - size / 2)
                cand = (dpx, -centre, -y, -x)
                if best is None or cand > best[0]:
                    best = (cand, {"pixel": [x, y], "regionDepth": dpx, "point": [hit[0], hit[1], hit[2]],
                                   "pickDistance": hit[3]})
            st["participants"][p["formKey"]] = {"pixels": int(mask.sum()), "maxDepth": int(dm.max()),
                                                "best": best[1] if best else None}
        st["placement"] = place
        st["valid"] = not place and all(v["best"] for v in st["participants"].values())
        if place:
            st["why"] = "; ".join(place)
        elif not st["valid"]:
            st["why"] = "not every participant shows a measurable face from here"

    valid = [st for st in ring if st["valid"]]
    if not valid:
        bare = [k for k in refused if seen.get(k, 0) == 0]
        if bare:
            raise Unpickable("points-no-opaque-face",
                             f"{bare[0]} draws only alpha-tested, effect or marker shapes, which the renderer cannot "
                             "mark, and no opaque face of it appeared from any station, so no point on it can be "
                             "measured; choose another request")
        placed = [st for st in ring if not st.get("placement") and "participants" in st]
        if not placed:
            raise Unpickable("points-no-station",
                             f"none of the {len(ring)} eye-level stations around the pair passed the planner's placement "
                             f"test on the floor at z {floor:.0f}; see the witness consoles")
        counts = {p["formKey"]: sum(1 for st in placed if st["participants"][p["formKey"]]["best"]) for p in parts}
        raise Unpickable("points-no-visible-face",
                         f"no eye-level camera {int(min(standoffs))}-{int(max(standoffs))} u around the pair sees both: "
                         f"{A} shows a measurable face from {counts[A]} of {len(placed)} stations and {B} from "
                         f"{counts[B]}; choose another request or a top-down view",
                         visibleFrom=counts, pixelsSeen=seen)

    def neighbours(st):
        return sum(1 for o in valid if o["ring"] == st["ring"] and (o["slot"] - st["slot"]) % bearings in (1, bearings - 1))

    def score(st):
        return (min(v["best"]["regionDepth"] for v in st["participants"].values()), neighbours(st), -st["standoff"],
                -ring.index(st))
    chosen = max(valid, key=score)
    rows = []
    for p in parts:
        b = chosen["participants"][p["formKey"]]["best"]
        rows.append({"formKey": p["formKey"], "point": [float(v) for v in b["point"]], "label": f"{p['color']}-face",
                     "surfaceOf": (f"{p['formKey']}: its {p['color']} --ref-surface pixel {b['pixel'][0]},{b['pixel'][1]} "
                                   f"({b['regionDepth']} px inside its colour region) seen from witness station "
                                   f"{chosen['id']} ({chosen['bearing']:.0f} deg, {chosen['standoff']:.0f} u); "
                                   f"--pick {b['pickDistance']:.0f} u from that eye")})
    cx = sum(r["point"][0] for r in rows) / len(rows)
    cy = sum(r["point"][1] for r in rows) / len(rows)
    wb = bearing_deg(chosen["eye"][0] - cx, chosen["eye"][1] - cy)
    ws = math.hypot(chosen["eye"][0] - cx, chosen["eye"][1] - cy)
    half = 20.0 if neighbours(chosen) == 2 else 10.0
    doc = {"schemaVersion": 1, "requestId": request.get("id"),
           "sourceSha256": (request.get("source") or {}).get("sha256"), "synthetic": bool(synthetic),
           "provenance": ("Measured by capture_points.py: each point is viewprobe's --pick at a pixel carrying the "
                          "participant's own --ref-surface colour in an eye-level witness frame, so the depth ray from "
                          "that station stops on the participant; the floor is "
                          f"{floor_src} at z {floor:.0f}." + (" SYNTHETIC renderer stand-in." if synthetic else "")),
           "cell": cell, "floorZ": floor,
           "points": rows,
           "camera": {"fov": float(fov), "aspect": aspect, "bearingWindow": [round(wb, 1), half], "standoffMin": float(max(50, int(ws // 50) * 50)),
                      "standoffMax": float(max(50, int(ws // 50) * 50) + 200)},
           "witness": {"station": {k: chosen[k] for k in ("id", "bearing", "standoff", "eye", "frame")},
                       "placements": {k: list(v) for k, v in pos.items()},
                       "floor": {"z": floor, "source": floor_src, "water": water},
                       "partlyUnmarked": refused,
                       "stations": [{"id": st["id"], "bearing": st["bearing"], "standoff": st["standoff"],
                                     "eye": st.get("eye"), "valid": st["valid"], "why": st.get("why"),
                                     "participants": {k: {"pixels": v["pixels"], "maxDepth": v["maxDepth"]}
                                                      for k, v in (st.get("participants") or {}).items()}}
                                    for st in ring],
                       "runs": R.calls,
                       "rule": (f"grid {grid} px on a {size} px, {fov:g} deg frame; a point needs {min_depth} px of its "
                                f"own colour around it and a pick hit; stations must pass the planner's placement verdicts")}}
    return doc


# ---------------------------------------------------------------- the command --

def cmd_pick(a) -> int:
    out = Path(a.out).resolve()
    work = Path(a.work).resolve() if a.work else out.parent / (out.stem + "-work")
    with _run("capture-points", mode="pick", stage_source="counted") as run:
        run.stage("pick", total=1, unit="requests")
        try:
            packet = json.loads(Path(a.packet).read_text(encoding="utf-8"))
            plan = packet.get("capturePlan") if isinstance(packet.get("capturePlan"), dict) else packet
            rows = [r for r in plan.get("requests") or [] if isinstance(r, dict) and r.get("id") == a.request]
            if len(rows) != 1:
                raise Unpickable("points-request-unknown", f"{a.request!r} is not one request of {a.packet}")
            doc = pick_points(rows[0], vp=a.vp, passthrough=a.passthrough, work=work, cell=a.cell,
                              size=a.size, grid=a.grid, synthetic=a.synthetic)
        except Unpickable as exc:
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_text(json.dumps({"schemaVersion": 1, "requestId": a.request, "status": "refused",
                                       "refusal": {"code": exc.code, "detail": exc.detail, **exc.extra}},
                                      indent=1), encoding="utf-8")
            print(f"REFUSED {exc.code}: {exc.detail}", file=sys.stderr)
            run.refuse(exc.code, EXIT_REFUSED)
            return EXIT_REFUSED
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        run.advance(1)
        run.artifact(str(out))
        run.tally(ok=1)
    print(f"points {a.request}: witness {doc['witness']['station']['id']}, floor z {doc['floorZ']:.0f} -> {out}")
    return EXIT_OK


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="capture_points.py", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pick", help="measure one visible surface point per participant of a capture request")
    p.add_argument("--packet", required=True, help="storyboard packet or bare capture plan")
    p.add_argument("--request", required=True, help="capture request id")
    p.add_argument("--out", required=True, help="the points document (capture_acquire's --points schema)")
    p.add_argument("--work", default=None, help="where the three runs' consoles and frames go (default <out>-work)")
    p.add_argument("--cell", default=None, help="viewprobe's cell, when the request's location does not name one")
    p.add_argument("--vp", default=None, help="viewprobe command (default: the built in-tree one)")
    p.add_argument("--size", type=int, default=320, help="witness frame size in px (default 320)")
    p.add_argument("--grid", type=int, default=10, help="pick every N px of the witness frame (default 10)")
    p.add_argument("--synthetic", action="store_true", help="the renderer is a stand-in: label the points SYNTHETIC")
    p.set_defaults(fn=cmd_pick)
    argv = list(sys.argv[1:] if argv is None else argv)
    passthrough = []
    if "--" in argv:
        i = argv.index("--")
        argv, passthrough = argv[:i], argv[i + 1:]
    a = ap.parse_args(argv)
    a.passthrough = passthrough
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
