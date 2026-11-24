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
                  Outdoors (the cell header's exterior flag) a station stands on the navmesh at its own level: one
                  with no navmesh under it moves along its bearing to the nearest spot 100-1,000 u out that has some,
                  its eye is that level + eye height, and the first surface under the eye must be that level.
                  One that still has no floor (no level, nearest triangle beyond the off-mesh tolerance) may stand on
                  the terrain at its own spot (shotplan.terrain_verdict, 2026-11-12): the LAND under its 64 u footprint
                  planar to 4 deg and at most the measured 43.7 deg, nothing placed within 64 u beside it or 160 u over
                  it, the floor ray on the terrain; its eye is that ground + eye height, labelled a terrain station.

THE FALLBACKS (2026-10-31). A participant with non-opaque shapes is marked by its measured coverage too (viewprobe
--coverage-of); "no opaque face" is no longer a refusal of its own. When no placed station sees both, the CUTAWAY tries up
to three placed stations with what stands in front hidden (--occluders-of), never a participant, nothing either touches or
rests on (--contacts-of, measured in the placements run), and with --protect so the renderer refuses on its own too. When
that refuses, or no station stands, the refusal carries `topDown` (the inputs capture_acquire's sliced top-down needs) and
`chain` (each step's verdict); capture_acquire finishes the chain.

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
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
SHOTPLAN = SW.HARNESS / "viewprobe" / "tools" / "shotplan.py"

EXIT_OK, EXIT_REFUSED = 0, 2

# viewprobe's --ref-surface marks as they arrive in a report-mode PNG (flat overlay after tone mapping), read off the
# renderer's own frames 2026-10-19: almost every marked pixel carries exactly this value; edges and blends do not.
SURFACE_RGB = {"cyan": (19, 255, 255), "magenta": (255, 76, 255)}
COLOUR_TOL = 2

EYE_LINE = re.compile(r"^\s*eye (-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?) -> look "
                      r"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)")
# a FormKey's plugin name may itself carry an apostrophe ("JK's ..."): match up to the quote before the sentence
NOT_PLACED = re.compile(r"'(.+?)' is not a placed ref in this cell's winning state")
SURFACE_REFUSED = re.compile(r"^subject-surface-refused: (\S{6}:.+?\.es[pml]):")
WATER_LINE = re.compile(r"^\s*water z (-?\d+(?:\.\d+)?): ")
# the renderer's cell header: the CELL record's interior flag, which decides where a station may stand
CELL_KIND = re.compile(r"^cell .*? \((interior|exterior)\)\s", re.M)
# outdoors, a station with no navmesh under it moves along its own bearing to the nearest spot that has some
SNAP_FROM, SNAP_TO, SNAP_STEP = 100, 1000, 50
SHOT_HEAD = re.compile(r"^=== worklist cell \d+/\d+ shot (\d+)/(\d+): (.*) ===\s*$")
LOOK_LIFT = 96.0   # viewprobe's --look-ref lift (Program.cs RefPos(lookRef, 96f))


class Unpickable(Exception):
    """No measured visible point can be given. `detail` is one plain sentence: what went wrong and what to try.

    Every refusal is one of three kinds, decided where it is raised: `final=True` (a property of the request no fallback
    can change: a participant not placed or not drawn, an unusable request), `transient=True` (the renderer failed:
    captured again), or neither, and then pick_points attaches the top-down's inputs and the chain so far, and
    capture_acquire carries the chain on."""

    def __init__(self, code: str, detail: str, **extra):
        detail = detail.strip()
        detail = detail if detail.endswith(".") else detail + "."
        super().__init__(f"{code}: {detail}")
        self.code, self.detail, self.extra = code, detail, extra
        if extra.get("final"):
            extra.setdefault("noFallback", "this is a property of the request itself, which no other view changes")
        elif extra.get("transient"):
            extra.setdefault("noFallback", "the renderer failed; the request is captured again")


def _load_shotplan():
    spec = importlib.util.spec_from_file_location("housecarl_shotplan_points", SHOTPLAN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _load_runlog():
    cand = SW.SCRIPTS / "runlog.py"
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
        raise Unpickable("points-cell-unknown", final=True,
                         detail=f"the request's location {request.get('location')!r} does not name one cell; pass --cell")
    return loc


def request_participants(request: dict) -> list[dict]:
    # a world-state pair may mark only its smaller participant; its points are measured on both (pointParticipants)
    rows = [h for h in (request.get("pointParticipants") or request.get("highlights") or []) if isinstance(h, dict) and h.get("formKey")]
    if len(rows) != 2 or rows[0]["formKey"] == rows[1]["formKey"]:
        raise Unpickable("points-participants-unusable",
                         "the picker measures a pair: the request must highlight exactly two distinct records", final=True)
    for h in rows:
        if h.get("color") not in SURFACE_RGB:
            raise Unpickable("points-participants-unusable",
                             f"{h['formKey']} is highlighted {h.get('color')!r}; the picker reads only cyan and magenta marks",
                             final=True)
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
        raise Unpickable("points-renderer-failed", f"viewprobe exited {rc} during the {what} run ({tail})", transient=True)


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


# ---------------------------------------------------------------- the capture fallbacks' report lines --

SURFACE_COUNTS = re.compile(r"^subject-surface-refused: (\S{6}:.+?\.es[pml]): (?:(\d+) of (\d+) shape)?")
CONTACTS_LINE = re.compile(r"^contacts-of (\S{6}:.+?\.es[pml]): touches \[(.*?)\]; rests on \[(.*?)\]; unassessed \[(.*?)\]")
BOX_LINE = re.compile(r"^contacts-of (\S{6}:.+?\.es[pml]): box (-?\d+),(-?\d+),(-?\d+) (-?\d+),(-?\d+),(-?\d+)")
OCCLUDER_LINE = re.compile(r"^occluders-of (\S{6}:.+?\.es[pml]): occluder (\S{6}:.+?\.es[pml]) (\d+) px( translucent)?")
OCCLUDER_OTHER = re.compile(r"^occluders-of (\S{6}:.+?\.es[pml]): other geometry .*? (\d+) px")
OCCLUDER_COVERS = re.compile(r"^occluders-of (\S{6}:.+?\.es[pml]): covers (\d+) px, visible (\d+) px")
COVERAGE_STATE = re.compile(r"^coverage-of (\S{6}:.+?\.es[pml]): (marked|below-threshold|transparent|outside-frustum|back-facing|"
                            r"not-rendered|not-drawn|opaque)\b")
COVERAGE_PICK = re.compile(r"^coverage pick (\S{6}:.+?\.es[pml]) (\d+),(\d+): (-?\d+),(-?\d+),(-?\d+)\s+alpha ([0-9.]+)\s+(\d+) u")
VIEW_LABEL = re.compile(r"^view-label: (.*?) \| rect (\d+),(\d+),(\d+),(\d+)\s*$")
PROTECT_REFUSED = re.compile(r"^protect refused: (.*)$")
FOCUS_RATIO = 4.0         # a see-through participant this many times the partner's largest dimension rings the partner
CUTAWAY_STATIONS = 3      # frozen at Gate B: at most three cutaway witness runs per request


# viewprobe's reasons for a placed reference it does not draw (Program.cs: "NOT DRAWN: <why>")
NOT_DRAWN_PARENT = re.compile(r"disabled via enable parent (\S{6}:\S+?\.es[pml])( OPPOSITE)?")
PLAYER_REF = "000014:Skyrim.esm"


def not_drawn_detail(form_key: str, why: str) -> dict:
    """viewprobe's own reason a reference is not drawn, as fields (C1936, C1939): `deleted`, `enableParent` (with the
    parent and, only when viewprobe says so, its set-to-opposite flag), `initiallyDisabled`, `noBase`, `noModel`, else
    `other`. `forGood` (switched off in the game for good) needs both the player as enable parent (always enabled) AND
    the opposite flag from viewprobe's own words; viewprobe's NOT DRAWN reason does not print that flag today, so
    `forGood` stays False until it does, never inferred from the parent alone."""
    w = str(why).split("NOT DRAWN:", 1)[-1].strip()
    m = NOT_DRAWN_PARENT.search(w)
    if m:
        opposite = True if m[2] else None
        return {"formKey": form_key, "why": "enableParent", "enableParent": m[1], "opposite": opposite,
                "forGood": m[1] == PLAYER_REF and opposite is True, "said": w}
    kind = ("deleted" if w.startswith("deleted") or "flagged deleted" in w else "initiallyDisabled" if w == "initially disabled"
            else "noBase" if w == "no base" else "noModel" if w == "base carries no model" else "other")
    return {"formKey": form_key, "why": kind, "forGood": False, "said": w}


def keyset(text: str) -> set[str]:
    return {x.strip() for x in text.split(",") if x.strip()}


def coverage_stem(png: Path, key: str) -> str:
    """viewprobe's coverage picture prefix for a frame (CaptureViews / TexturedScene Stem)."""
    return png.stem + "-coverage-" + key.replace(":", "_").replace(" ", "_").replace("'", "")


def label_mask(text: str, shape) -> np.ndarray:
    """True on the pixels of the frame's view label (left out of every measurement)."""
    m = np.zeros(shape[:2], dtype=bool)
    for ln in text.splitlines():
        lm = VIEW_LABEL.match(ln)
        if lm:
            x, y, w, h = (int(lm[i]) for i in range(2, 6))
            m[y:y + h, x:x + w] = True
    return m


def participant_mask(img: np.ndarray, png: Path, part: dict, see_through: bool, text: str) -> tuple[np.ndarray, np.ndarray]:
    """(the participant's marked pixels, the subset measured by coverage): its --ref-surface colour on its opaque shapes,
    and, for a participant with non-opaque shapes, its --coverage-of mask; the view label's pixels are never in either."""
    lab = label_mask(text, img.shape)
    col = colour_mask(img, part["color"]) & ~lab
    cov = np.zeros_like(col)
    if see_through:
        f = png.parent / (coverage_stem(png, part["formKey"]) + "-mask.png")
        if f.is_file():
            cov = (np.asarray(Image.open(f).convert("L")) > 0) & ~lab & ~col
    return col | cov, cov


def coverage_picks(text: str) -> dict:
    """{(key, x, y): (X, Y, Z, dist, alpha)} from the coverage pick lines."""
    out = {}
    for ln in text.splitlines():
        cm = COVERAGE_PICK.match(ln)
        if cm:
            out[(cm[1], int(cm[2]), int(cm[3]))] = (float(cm[4]), float(cm[5]), float(cm[6]), float(cm[8]), float(cm[7]))
    return out


def measure_station(img, png, text, pr, parts, see_through, gridset, min_depth, size):
    """Per participant at one frame: pixels, depth, and the best measured point (a depth pick on its colour, or a coverage
    pick on its coverage mask), as the eye-level rule picks it."""
    picks = pr.picks()
    cpicks = coverage_picks(text)
    out = {}
    for p in parts:
        k = p["formKey"]
        mask, cov = participant_mask(img, png, p, k in see_through, text)
        dm = depth_map(mask)
        best = None
        for (x, y) in gridset:
            dpx = int(dm[y, x]) if y < dm.shape[0] and x < dm.shape[1] else 0
            if dpx < min_depth:
                continue
            if cov[y, x]:
                hit = cpicks.get((k, x, y))
                kind = "coverage"
                if hit is None:
                    continue
                pt, dist, alpha = hit[:3], hit[3], hit[4]
            else:
                hit = picks.get((x, y))
                kind, alpha = "surface", None
                if hit is None:
                    continue
                pt, dist = hit[:3], hit[3]
            centre = abs(x - size / 2) + abs(y - size / 2)
            cand = (dpx, -centre, -y, -x)
            if best is None or cand > best[0]:
                best = (cand, {"pixel": [x, y], "regionDepth": dpx, "point": [pt[0], pt[1], pt[2]], "pickDistance": dist,
                               "markKind": kind, "alpha": alpha})
        out[k] = {"pixels": int(mask.sum()), "coveragePixels": int(cov.sum()), "maxDepth": int(dm.max()),
                  "best": best[1] if best else None}
    return out


# ---------------------------------------------------------------- the picker --

def focus_storey_range(boxes: dict, focus: str, same_storey: float) -> tuple[float, float]:
    """The z range the ring's storey is searched in for a focus: where the two participants' drawn boxes overlap (the
    clash is there, and a player meets it on that storey), else the focus's own drawn box. A tall focus's whole height
    would let the storey land anywhere up it (F5: the WRW tower's top walkway). The ring's xy stays the focus's.
    A partner box shorter than `same_storey` (the planner's --storey-tol: two heights that close are one storey) is
    flat, an effect card: its overlap is a plane that says nothing about the storey, so the focus's own box is used."""
    fb = boxes[focus]
    other = next((b for k, b in boxes.items() if k != focus), None)
    if other is not None and other[5] - other[2] >= same_storey:
        lo = [max(fb[i], other[i]) for i in range(3)]
        hi = [min(fb[i + 3], other[i + 3]) for i in range(3)]
        if all(lo[i] <= hi[i] for i in range(3)):
            return lo[2], hi[2]
    return fb[2], fb[5]


def pick_points(request: dict, **kw) -> dict:
    """The points document, or Unpickable. A refusal that is neither final nor transient leaves here carrying the
    top-down's inputs and the chain so far, wherever in the picker it was raised (Unpickable's three kinds)."""
    ctx: dict = {}
    try:
        return _pick_points(request, _ctx=ctx, **kw)
    except Unpickable as exc:
        if exc.extra.get("final") or exc.extra.get("transient") or exc.extra.get("topDown") is not None:
            raise
        if ctx.get("topDown") is None:
            exc.extra.setdefault("noFallback", "the participants' placements were never read, so there is nothing to "
                                               "take a top-down of")
            raise
        exc.extra["topDown"] = ctx["topDown"](ctx.get("floor"))
        exc.extra.setdefault("chain", [{"kind": "eye-level", "status": "refused", "code": exc.code, "detail": exc.detail}])
        exc.extra.setdefault("coverageStates", ctx.get("coverageStates") or {})
        raise


def _pick_points(request: dict, *, vp: str | None, passthrough: list[str], work: Path, cell: str | None = None,
                 bearings: int = 12, standoffs=(150.0, 250.0, 400.0), size: int = 320, grid: int = 10, fov: float = 65.0,
                 min_depth: int = 2, synthetic: bool = False, aspect: str = "16:9", shotplan=None, _ctx=None) -> dict:
    """The points document `capture_acquire.py` reads, or Unpickable with one plain sentence.

    The chain (README, capture fallbacks): eye-level, with a see-through participant marked by its measured coverage;
    then, when no placed station sees both, the CUTAWAY at up to three placed stations, hiding what stands in front and
    never what the pair touches or rests on; when that refuses too, or no station stands at all, an Unpickable carrying
    `topDown` (the inputs capture_acquire's top-down needs) and `chain` (each step's verdict)."""
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

    # 1. placements, and each participant's drawn shapes (opaque or not), box and contacts
    rc, text = R.call("p0-placements", [cell], ["--eye-ref", A, "--eye-height", "0", "--look-ref", B,
                                                 "--size", "64", "--msaa", "0", "--nav", "off", *marks, "--ref-surface",
                                                 "--contacts-of", A, "--contacts-of", B])
    missing = NOT_PLACED.findall(text)
    if missing:
        raise Unpickable("points-not-placed",
                         f"{missing[0]} is not a placed reference in {cell}'s winning state, so it has no face to "
                         "measure; check the request's cell and load order", final=True)
    ensure_ok(rc, text, "placement")
    m = None
    for ln in text.splitlines():
        m = EYE_LINE.match(ln) or m
    if m is None:
        raise Unpickable("points-renderer-failed", "viewprobe did not print the resolved camera, so the "
                                                   "participants' placements are unknown", transient=True)
    pos = {A: (float(m[1]), float(m[2]), float(m[3])), B: (float(m[4]), float(m[5]), float(m[6]) - LOOK_LIFT)}
    kind_m = CELL_KIND.search(text)
    exterior = bool(kind_m) and kind_m[1] == "exterior"
    counts = {}
    for ln in text.splitlines():
        sm = SURFACE_COUNTS.match(ln)
        if sm:
            counts[sm[1]] = (int(sm[2]), int(sm[3])) if sm[2] else None
    # a participant with any non-opaque shape is marked by coverage too; one with ONLY non-opaque shapes is see-through
    see_through = set(counts)
    all_non_opaque = {k for k, c in counts.items() if c is None or c[0] == c[1]}
    contacts, boxes = {}, {}
    for ln in text.splitlines():
        cm = CONTACTS_LINE.match(ln)
        if cm:
            contacts[cm[1]] = {"touches": sorted(keyset(cm[2])), "restsOn": sorted(keyset(cm[3])),
                               "unassessed": sorted(keyset(cm[4]))}
        bm = BOX_LINE.match(ln)
        if bm:
            boxes[bm[1]] = [float(bm[i]) for i in range(2, 8)]
    mid = tuple((pos[A][k] + pos[B][k]) / 2.0 for k in range(3))
    minz, maxz = min(pos[A][2], pos[B][2]), max(pos[A][2], pos[B][2])
    focus = request.get("focus")
    if focus not in pos and len(boxes) == 2:
        # a see-through participant much larger than its partner (a cloud against a tower): its origin and its size say
        # nothing about where the clash is, so the ring and the floor come from the partner
        dim = {k: max(b[3] - b[0], b[4] - b[1], b[5] - b[2]) for k, b in boxes.items()}
        for big, small in ((A, B), (B, A)):
            if big in all_non_opaque and dim.get(big, 0) >= FOCUS_RATIO * max(1.0, dim.get(small, 0)):
                focus = small
    if focus in pos:
        # a landscape-scale partner (the reel's volume rule marks only the smaller participant): its origin says nothing
        # about where the clash is, so the stations ring the smaller participant, at its level. Its level is its DRAWN
        # box when there is one: an origin can sit far below the ground the object stands through (a cliff: WRW rerun,
        # fb173b065383), and a floor searched around it finds nothing
        mid, minz, maxz = pos[focus], pos[focus][2], pos[focus][2]
        if focus in boxes:
            minz, maxz = focus_storey_range(boxes, focus, args.storey_tol)
            mid = (pos[focus][0], pos[focus][1], (minz + maxz) / 2.0)

    # what a top-down needs, should every eye-level picture refuse: the subjects (the focus alone, else both), placed at
    # the centre of the overlap of their drawn boxes (a lone focus at its box's centre; origins when nothing is drawn),
    # the cut above it, and the floor: the measured storey, else the lowest drawn point of the subjects (a top-down needs
    # no camera storey; its frame is sized from what is drawn)
    def top_down_inputs(floor_z=None):
        keys = [focus] if focus in pos else [A, B]
        bx = [boxes.get(k) for k in keys]
        cut, subj = None, []
        if all(bx) and len(bx) == 2:
            lo = [max(bx[0][i], bx[1][i]) for i in range(3)]
            hi = [min(bx[0][i + 3], bx[1][i + 3]) for i in range(3)]
            if all(lo[i] <= hi[i] for i in range(3)):
                c = [(lo[i] + hi[i]) / 2 for i in range(3)]
                subj = [{"formKey": k, "point": [round(c[0], 1), round(c[1], 1), round(c[2], 1)]} for k in keys]
                cut = round(hi[2] + 64.0, 1)
        if not subj and len(bx) == 1 and bx[0]:
            c = [(bx[0][i] + bx[0][i + 3]) / 2 for i in range(3)]
            subj = [{"formKey": keys[0], "point": [round(v, 1) for v in c]}]
            cut = round(bx[0][5] + 64.0, 1)
        if not subj:
            subj = [{"formKey": k, "point": [round(v, 1) for v in pos[k]]} for k in keys]
            tops = [boxes[k][5] for k in keys if k in boxes]
            cut = round(max(tops) + 64.0, 1) if tops else None
        src = "measured storey"
        if floor_z is None:
            lows = [boxes[k][2] for k in keys if k in boxes] or [pos[k][2] for k in keys]
            floor_z, src = round(min(lows), 1), "no storey measured: the lowest drawn point of the subjects"
        return {"cell": cell, "floorZ": floor_z, "floorSource": src, "subjects": subj, "cut": cut,
                "focus": focus if focus in pos else None,
                "seeThrough": sorted(see_through), "allNonOpaque": sorted(all_non_opaque), "contacts": contacts}
    if _ctx is not None:
        _ctx["topDown"] = top_down_inputs
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
    # outdoors: navmesh samples along every bearing, where a station off the navmesh may move to (record queries only)
    samples = {}
    if exterior:
        for st in ring:
            if st["slot"] in samples:
                continue
            ux, uy = bearing_vec(st["bearing"])
            samples[st["slot"]] = [(float(d), (int(round(mid[0] + ux * d)), int(round(mid[1] + uy * d))))
                                   for d in range(SNAP_FROM, SNAP_TO + 1, SNAP_STEP)]
            for _, (x, y) in samples[st["slot"]]:
                flags += ["--nav-at", f"{x},{y}"]
    rc, text = R.call("p1-floor", [cell], flags)
    ensure_ok(rc, text, "floor")
    p1 = R.parsed(text)
    near = p1.refs_near()
    for k in (A, B):
        rows = [r for rows_ in near.values() for r in rows_ if r["formkey"] == k]
        if rows and not rows[0]["why"].startswith("drawn"):
            raise Unpickable("points-not-drawn",
                             f"{k} is not drawn by the renderer ({rows[0]['why']}), so no face of it can be measured",
                             final=True, notDrawn=not_drawn_detail(k, rows[0]["why"]))
    nav = p1.nav()
    # outdoors, the stations today's rule leaves with no floor -- no navmesh level at the spot or along the bearing, the
    # nearest triangle beyond the off-mesh tolerance -- may stand on the terrain; their footprints' LAND vertices are read
    def has_level(xy):
        return bool((nav.get(tuple(xy)) or {}).get("levels"))
    tcands = {}
    if exterior:
        for st in ring:
            e = nav.get(tuple(st["xy"]))
            if e is None or has_level(st["xy"]) or any(has_level(xy) for _, xy in samples.get(st["slot"], [])):
                continue
            if e.get("nearest") is not None and e["nearest"] <= args.offmesh_tol:
                continue
            tcands[st["id"]] = tuple(st["xy"])
    tverts = sorted({v for xy in tcands.values() for v in S.terrain_vertices(xy)})
    heights = None   # the candidates' LAND vertex heights, once read
    # the request's floor is chosen from the pair's and the ring's own spots, never from the snap samples
    asked = {(int(round(pos[k][0])), int(round(pos[k][1]))) for k in (A, B)} | {(int(round(mid[0])), int(round(mid[1])))} \
        | {tuple(st["xy"]) for st in ring}
    levels = [z for k, e in nav.items() if k in asked for z in e["levels"]]
    floor = choose_floor(levels, minz, maxz, 300.0, args.storey_tol)
    floor_src = "navmesh"
    # the ground actually under the stations (the first surface straight down, terrain or a placed ref, a participant
    # included: a landscape-scale participant IS the ground there). A navmesh level well below it is another storey --
    # a path under a rock, a cellar -- and a camera standing on it would stand inside the ground. The ray starts a
    # player's eye height plus the storey tolerance above the navmesh storey being judged, so it asks whether something
    # solid stands between that storey and the eye; cast from the top of the pair's box it found any floor high up the
    # focus instead (item 1: WRW 708f's ring on the tower's top floor, 1,000 u over the navmesh). Limit: a navmesh more
    # than that height under the ground is not caught here; the inside and support verdicts still apply.
    ground = []
    if floor is not None:
        zg = int(round(floor + args.eye_height + args.storey_tol))
        gflags = ["--eye", f"{mid[0]:.0f},{mid[1]:.0f},{maxz + 600:.0f}", "--look", f"{mid[0]:.0f},{mid[1]:.0f},{mid[2]:.0f}",
                  "--size", "64", "--msaa", "0", "--nav", "off", "--clear-reach", "2048"]
        for st in ring:
            gflags += ["--clear-at", f"{st['xy'][0]},{st['xy'][1]},{zg}"]
        for vx, vy in tverts:   # the terrain candidates' LAND vertices: only each answer's terrain height is read
            gflags += ["--clear-at", f"{vx},{vy},{zg}"]
        rc, gtext = R.call("p1-ground", [cell], gflags)
        ensure_ok(rc, gtext, "ground")
        gclear = R.parsed(gtext).clear()
        heights = {(x, y): g for (x, y, z), e in gclear.items() if (x, y) in set(tverts)
                   and (g := S.terrain_ground(e)) is not None}
        spots = {tuple(st["xy"]) for st in ring}
        ground = sorted(z - d["dist"] for (x, y, z), e in gclear.items()
                        if (x, y) in spots and (d := (e["axes"].get("down") or {})).get("dist") is not None)
    if floor is not None and ground:
        g = ground[len(ground) // 2]
        if g - floor > args.storey_tol:
            under = choose_floor(ground, minz, maxz, 300.0, args.storey_tol)
            if under is not None:
                floor, floor_src = under, "the ground under the stations (the navmesh found lies below it)"
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

    if _ctx is not None:
        _ctx["floor"] = floor

    # 2b. terrain stations: the slope from the LAND vertices, then one clearance run for the footprints it allows
    tverdict = {}
    if tcands:
        if heights is None:   # p1-ground did not run (no navmesh storey): the vertices get a record-only run of their own
            lflags = ["--eye", f"{mid[0]:.0f},{mid[1]:.0f},{maxz + 600:.0f}", "--look", f"{mid[0]:.0f},{mid[1]:.0f},{mid[2]:.0f}",
                      "--size", "64", "--msaa", "0", "--nav", "off", "--clear-reach", str(S.TERRAIN_REACH)]
            for vx, vy in tverts:
                lflags += ["--clear-at", f"{vx},{vy},{int(round(maxz + 600))}"]
            rc, ltext = R.call("p1-land", [cell], lflags)
            ensure_ok(rc, ltext, "LAND")
            heights = {(x, y): g for (x, y, z), e in R.parsed(ltext).clear().items()
                       if (g := S.terrain_ground(e)) is not None}
        slopes = {sid: S.terrain_slope(heights, xy) for sid, xy in tcands.items()}
        for sid, sl in slopes.items():
            if not sl["ok"]:
                tverdict[sid] = (None, sl["why"])
        allowed = {sid: tcands[sid] for sid, sl in slopes.items() if sl["ok"]}
        if allowed:
            tflags = ["--eye", f"{mid[0]:.0f},{mid[1]:.0f},{maxz + 600:.0f}", "--look", f"{mid[0]:.0f},{mid[1]:.0f},{mid[2]:.0f}",
                      "--size", "64", "--msaa", "0", "--nav", "off", "--clear-reach", str(S.TERRAIN_REACH)]
            for xy in allowed.values():
                for q in S.terrain_queries(heights, xy):
                    tflags += ["--clear-at", f"{q[0]},{q[1]},{q[2]}"]
            rc, ttext = R.call("p1-terrain", [cell], tflags)
            ensure_ok(rc, ttext, "terrain clearance")
            tclear = R.parsed(ttext).clear()
            for sid, xy in allowed.items():
                tverdict[sid] = S.terrain_verdict(heights, tclear, xy)

    # 3. witnesses. Indoors every station stands on the one floor. Outdoors a station stands on the navmesh at its own
    # level: where its spot has none, it moves along its bearing to the nearest sampled spot that has some, and its eye
    # is that level + the eye height (a player's eye where a player can stand; the ring's one floor put eyes in hillsides)
    for st in ring:
        st["floor"] = floor
    merged = []
    if exterior:
        def level_at(xy):
            lv = (nav.get(tuple(xy)) or {}).get("levels") or []
            return min(lv, key=lambda z: abs(z - floor)) if lv else None
        taken = set()
        for st in ring:
            lv = level_at(st["xy"])
            if lv is None:
                covered = [(d, xy) for d, xy in samples.get(st["slot"], []) if level_at(xy) is not None]
                if covered:
                    d, xy = min(covered, key=lambda c: (abs(c[0] - st["standoff"]), -c[0]))
                    st.update(xy=xy, standoff=d, movedFrom=st["xy"])
                    lv = level_at(xy)
            if lv is not None:
                st["floor"] = lv
                st["standing"] = {"kind": "navmesh"}
            elif st["id"] in tverdict:
                # no navmesh at its level: it stands on the terrain at its own spot when the LAND there allows it
                standing, why = tverdict[st["id"]]
                if standing:
                    st["floor"], st["standing"] = standing["ground"], standing
                else:
                    st["terrainWhy"] = why
            if tuple(st["xy"]) in taken:
                merged.append(st)
            taken.add(tuple(st["xy"]))
        ring = [st for st in ring if st not in merged]
    for st in ring:
        st["ez"] = int(round(st["floor"] + args.eye_height))
    lookz = min(max(mid[2], floor + 40.0), floor + 300.0)
    shots = [{"label": st["id"], "eye": [st["xy"][0], st["xy"][1], st["ez"]], "floorZ": st["floor"],
              "look": [round(mid[0], 1), round(mid[1], 1), round(lookz, 1)], "fov": fov} for st in ring]
    wl = {"label": f"capture point witnesses for {request.get('id')}", "eyeHeight": args.eye_height,
          "cells": [{"cell": cell, "shots": shots}]}
    (work / "witness-worklist.json").write_text(json.dumps(wl, indent=1), encoding="utf-8")
    grid_px = [(x, y) for y in range(grid // 2, size, grid) for x in range(grid // 2, size, grid)]
    cov_flags = [x for k in sorted(see_through) for x in ("--coverage-of", k)]
    flags = [*marks, "--ref-surface", "--report-mode", "--size", str(size), "--msaa", "0", "--nav", "off",
             "--clear-reach", f"{args.clear_reach:g}", *cov_flags]
    for x, y in grid_px:
        flags += ["--pick", f"{x},{y}"]
    for st in ring:
        x, y = st["xy"]
        flags += ["--nav-at", f"{x},{y}", "--inside-at", f"{x},{y},{st['ez']}", "--clear-at", f"{x},{y},{st['ez']}"]
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
    cov_states = {k: set() for k in see_through}
    for st in ring:
        ez = st["ez"]
        st["eye"] = [st["xy"][0], st["xy"][1], ez]
        chunk = chunks.get(st["id"])
        pngs = sorted((work / "p2-witness").glob(f"*-c1s{ring.index(st) + 1}-{st['id']}-fp.png"))
        st["frame"] = f"p2-witness/{pngs[0].name}" if pngs else None
        if chunk is None or not pngs:
            st.update(valid=False, why="the witness run gave no frame or console block for this station")
            continue
        ctext = "\n".join(chunk)
        for ln in chunk:
            cs = COVERAGE_STATE.match(ln)
            if cs and cs[1] in cov_states:
                cov_states[cs[1]].add(cs[2])
        pr = R.parsed(ctext)
        key3, key2 = (st["xy"][0], st["xy"][1], ez), st["xy"]
        kind, nmsg = S.nav_verdict(pr.nav().get(key2), st["floor"], args)
        eye = (float(st["xy"][0]), float(st["xy"][1]), float(ez))
        sup, smsg = S.support_verdict(pr.clear().get(key3), eye, st["floor"], args)
        ikind, ifails, _notes = S.inside_verdict(pr.inside().get(key3), args)
        place = []
        on_terrain = (st.get("standing") or {}).get("kind") == "terrain"
        if kind == "no-floor" and on_terrain:
            if sup != "supported":
                place.append(f"placement: this eye stands on the terrain at z {st['floor']:.0f}, but {smsg}")
        elif kind in ("wrong-storey", "no-floor", "unknown"):
            place.append(f"placement: {nmsg}" + (f"; and it cannot stand on the terrain there: {st['terrainWhy']}"
                                                 if st.get("terrainWhy") else ""))
        if kind == "off-mesh-near" and sup != "supported":
            place.append(f"placement: {nmsg}; and the geometry does not bear that out -- {smsg}")
        elif exterior and kind == "on-storey" and sup != "supported":
            # outdoors the eye stands on its own navmesh level, so the first surface under it must be that level
            place.append(f"placement: outdoors this eye stands on the navmesh at z {st['floor']:.0f}, but {smsg}")
        place += ifails
        img = np.asarray(Image.open(pngs[0]).convert("RGB"))
        st["participants"] = measure_station(img, pngs[0], ctext, pr, parts, see_through, gridset, min_depth, size)
        for k, v in st["participants"].items():
            seen[k] = max(seen[k], v["pixels"])
        st["placement"] = place
        st["valid"] = not place and all(v["best"] for v in st["participants"].values())
        if place:
            st["why"] = "; ".join(place)
        elif not st["valid"]:
            st["why"] = "not every participant shows a measurable face from here"

    def station_rows(sts):
        return [{"id": st["id"], "bearing": st["bearing"], "standoff": st["standoff"], "eye": st.get("eye"),
                 "floorZ": st.get("floor"), "movedFrom": st.get("movedFrom"), "standing": st.get("standing"),
                 "valid": st["valid"], "why": st.get("why"),
                 "participants": {k: {"pixels": v["pixels"], "maxDepth": v["maxDepth"]}
                                  for k, v in (st.get("participants") or {}).items()}} for st in sts]

    valid = [st for st in ring if st["valid"]]
    view_kind, cutaway, view_flags = "eye-level", None, []
    chain = []
    placed = [st for st in ring if not st.get("placement") and "participants" in st]
    if not valid:
        if not placed:
            code = "points-no-station"
            detail = (f"none of the {len(ring)} eye-level stations around the pair passed the planner's placement test on "
                      f"the floor at z {floor:.0f}; see the witness consoles")
            chain.append({"kind": "eye-level", "status": "refused", "code": code, "detail": detail, "stationsPlaced": 0,
                          "allNonOpaque": sorted(all_non_opaque)})
            chain.append({"kind": "cutaway", "status": "skipped", "code": "cutaway-no-station",
                          "detail": "no station stands, so no cutaway can stand either"})
        else:
            counts_ = {p["formKey"]: sum(1 for st in placed if st["participants"][p["formKey"]]["best"]) for p in parts}
            code = "points-no-visible-face"
            detail = (f"no eye-level camera {int(min(standoffs))}-{int(max(standoffs))} u around the pair sees both: "
                      f"{A} shows a measurable face from {counts_[A]} of {len(placed)} stations and {B} from "
                      f"{counts_[B]}")
            # the counts as fields (F8): a participant no placed station sees is how a report tells a buried clash
            chain.append({"kind": "eye-level", "status": "refused", "code": code, "detail": detail,
                          "stationsPlaced": len(placed), "visibleFrom": dict(counts_),
                          # a participant drawn only by see-through shapes hides nothing (item 4: never "buried inside" it)
                          "allNonOpaque": sorted(all_non_opaque)})
            got = cutaway_attempt(R, S, args, cell, parts, marks, placed, contacts, see_through, cov_flags, floor,
                                  mid, lookz, fov, size, grid_px, gridset, min_depth, work)
            if got["status"] == "ok":
                valid = [got["station"]]
                view_kind, cutaway, view_flags = "cutaway", got["record"], got["flags"]
                chain.append({"kind": "cutaway", "status": "ok", "code": None, "detail": got["detail"]})
            else:
                chain.append({"kind": "cutaway", "status": got["status"], "code": got["code"], "detail": got["detail"]})
        if not valid:
            raise Unpickable(code, detail + "; " + chain[-1]["detail"], topDown=top_down_inputs(floor), chain=chain,
                             stations=station_rows(ring),
                             coverageStates={k: sorted(v) for k, v in cov_states.items()},
                             visibleFrom={p["formKey"]: sum(1 for st in placed if st["participants"][p["formKey"]]["best"])
                                          for p in parts} if placed else None, pixelsSeen=seen)

    def neighbours(st):
        return sum(1 for o in valid if o["ring"] == st["ring"] and (o["slot"] - st["slot"]) % bearings in (1, bearings - 1))

    def score(st):
        return (min(v["best"]["regionDepth"] for v in st["participants"].values()), neighbours(st), -st["standoff"],
                -ring.index(st) if st in ring else 0)
    chosen = max(valid, key=score)
    if exterior and chosen.get("floor") is not None and chosen["floor"] != floor:
        floor_src = (f"outdoors, the witness station's own navmesh level (the request's floor, {floor_src}, is z "
                     f"{floor:.0f})")
    rows = []
    for p in parts:
        b = chosen["participants"][p["formKey"]]["best"]
        what = "--ref-surface pixel" if b["markKind"] == "surface" else "--coverage-of mask pixel"
        rows.append({"formKey": p["formKey"], "point": [float(v) for v in b["point"]], "label": f"{p['color']}-face",
                     "markKind": b["markKind"],
                     "coverage": ({"alphaMin": 0.25, "alpha": b["alpha"],
                                   "pixels": chosen["participants"][p["formKey"]]["coveragePixels"],
                                   "measures": ("pixels where the participant's own drawn contribution, recovered from "
                                                "isolated draws over black and white, is at least 0.25 and its own nearest "
                                                "surface is in front of the opaque scene; the point is that surface")}
                                  if b["markKind"] == "coverage" else None),
                     "surfaceOf": (f"{p['formKey']}: its {p['color']} {what} {b['pixel'][0]},{b['pixel'][1]} "
                                   f"({b['regionDepth']} px inside its region) seen from witness station "
                                   f"{chosen['id']} ({chosen['bearing']:.0f} deg, {chosen['standoff']:.0f} u)"
                                   + (" with the cutaway's objects hidden" if view_kind == "cutaway" else "")
                                   + f"; pick {b['pickDistance']:.0f} u from that eye")})
    cx = sum(r["point"][0] for r in rows) / len(rows)
    cy = sum(r["point"][1] for r in rows) / len(rows)
    wb = bearing_deg(chosen["eye"][0] - cx, chosen["eye"][1] - cy)
    ws = math.hypot(chosen["eye"][0] - cx, chosen["eye"][1] - cy)
    half = 10.0 if view_kind == "cutaway" else (20.0 if neighbours(chosen) == 2 else 10.0)
    chain.append({"kind": view_kind, "status": "ok", "code": None,
                  "detail": f"station {chosen['id']} ({chosen['bearing']:.0f} deg, {chosen['standoff']:.0f} u)",
                  "allNonOpaque": sorted(all_non_opaque)})
    doc = {"schemaVersion": 1, "requestId": request.get("id"),
           "sourceSha256": (request.get("source") or {}).get("sha256"), "synthetic": bool(synthetic),
           "provenance": ("Measured by capture_points.py: each point is viewprobe's --pick at a pixel carrying the "
                          "participant's own --ref-surface colour (or, for a see-through shape, a pixel of its measured "
                          "--coverage-of mark) in an eye-level witness frame, so the ray from that station stops on, or "
                          "for a see-through mark reaches, the participant; the floor is "
                          f"{floor_src} at z {floor:.0f}." + (" SYNTHETIC renderer stand-in." if synthetic else "")),
           "cell": cell, "floorZ": chosen.get("floor", floor), "cellKind": "exterior" if exterior else "interior",
           "viewKind": view_kind, "viewFlags": view_flags, "cutaway": cutaway, "fallbackChain": chain,
           # should the planner refuse every camera at this pose, the chain goes on to the top-down with these
           "topDown": top_down_inputs(floor), "coverageStates": {k: sorted(v) for k, v in cov_states.items()},
           "points": rows,
           "camera": {"fov": float(fov), "aspect": aspect, "bearingWindow": [round(wb, 1), half], "standoffMin": float(max(50, int(ws // 50) * 50)),
                      "standoffMax": float(max(50, int(ws // 50) * 50) + 200),
                      # a coverage mark is a point on a see-through surface: framed as a 16 u box on it, not a standing figure
                      **({"subjectHeight": 16.0, "subjectWidth": 16.0} if any(r["markKind"] == "coverage" for r in rows) else {})},
           # the station's own camera (its eye, the point it looks at, the field of view and square frame it was measured
           # in): the alpha-card rule's witness step renders it again when the composed pose misses a see-through mark
           "witness": {"station": {**{k: chosen[k] for k in ("id", "bearing", "standoff", "eye", "frame")},
                                   "standing": chosen.get("standing"),
                                   "look": [round(mid[0], 1), round(mid[1], 1), round(lookz, 1)], "fov": float(fov),
                                   "size": int(size)},
                       "placements": {k: list(v) for k, v in pos.items()},
                       "floor": {"z": floor, "source": floor_src, "water": water},
                       "partlyUnmarked": sorted(see_through), "focus": focus if focus in pos else None,
                       "contacts": contacts,
                       "stations": station_rows(ring),
                       "merged": [{"id": st["id"], "xy": list(st["xy"])} for st in merged],
                       "runs": R.calls,
                       "rule": (f"grid {grid} px on a {size} px, {fov:g} deg frame; a point needs {min_depth} px of its "
                                f"own colour or coverage around it and a pick hit; stations must pass the planner's "
                                f"placement verdicts")}}
    return doc


def protected_keys(keys: list[str], contacts: dict) -> tuple[set | None, str | None]:
    """What a cutaway may never hide: the participants and everything either touches, rests on or has undecided. (None,
    the participant) when a participant's contacts were not measured: then nothing may be hidden."""
    out = set(keys)
    for k in keys:
        c = contacts.get(k)
        if c is None:
            return None, k
        out |= set(c["touches"]) | set(c["restsOn"]) | set(c["unassessed"])
    return out, None


def parse_occluders(lines, keys: list[str]) -> tuple[dict, dict, dict]:
    """viewprobe --occluders-of report lines: per participant, the opaque objects in front of it ({key: px}), the other
    geometry in front (px) and how many of its pixels are covered."""
    occ, other, cover = {k: {} for k in keys}, {k: 0 for k in keys}, {k: 0 for k in keys}
    for ln in lines:
        om = OCCLUDER_LINE.match(ln)
        if om and not om[4] and om[1] in occ:
            occ[om[1]][om[2]] = int(om[3])
        oo = OCCLUDER_OTHER.match(ln)
        if oo and oo[1] in other:
            other[oo[1]] = int(oo[2])
        oc = OCCLUDER_COVERS.match(ln)
        if oc and oc[1] in cover:
            cover[oc[1]] = int(oc[2])
    return occ, other, cover


def hideable(occ: dict, other: dict, need: list[str], protected: set) -> tuple[set, int, int, dict]:
    """The cutaway's one hide rule, at whatever camera listed the occluders: of what stands in front of each participant
    in `need`, every opaque object not protected may be hidden. Returns (hide, pixels kept in front, pixels hidden, the
    protected objects in front {key: px})."""
    hide, kept_px, hide_px, blocked = set(), 0, 0, {}
    for k in need:
        for o, n in occ[k].items():
            if o in protected:
                kept_px += n
                blocked[o] = blocked.get(o, 0) + n
            else:
                hide.add(o)
                hide_px += n
        kept_px += other[k]
    return hide, kept_px, hide_px, blocked


def cutaway_attempt(R, S, args, cell, parts, marks, placed, contacts, see_through, cov_flags, floor, mid, lookz, fov,
                    size, grid_px, gridset, min_depth, work) -> dict:
    """The cutaway: at stations that passed placement, hide what stands in front of a participant the station cannot see,
    never a participant, nothing either touches or rests on, nothing whose contact is undecided. At most
    CUTAWAY_STATIONS witness runs, each with --protect on both participants so the renderer refuses on its own too."""
    keys = [p["formKey"] for p in parts]
    protected, unmeasured = protected_keys(keys, contacts)
    if protected is None:
        return {"status": "refused", "code": "cutaway-contacts-unknown",
                "detail": f"the cutaway was refused: what {unmeasured} touches was not measured, so nothing may be hidden"}
    # which objects stand in front, at every placed station (one worklist run, report only)
    shots = [{"label": st["id"], "eye": st["eye"], "floorZ": floor, "look": [round(mid[0], 1), round(mid[1], 1), round(lookz, 1)],
              "fov": fov} for st in placed]
    wl = {"label": "cutaway occluders", "cells": [{"cell": cell, "shots": shots}]}
    (work / "occluders-worklist.json").write_text(json.dumps(wl, indent=1), encoding="utf-8")
    rc, text = R.call("p3-occluders", ["--worklist", str(work / "occluders-worklist.json")],
                      ["--report-mode", "--size", str(size), "--msaa", "0", "--nav", "off",
                       *[x for k in keys for x in ("--occluders-of", k)]])
    if rc != 0:
        tail = " ".join(text.strip().splitlines()[-2:])[-200:]
        return {"status": "failed", "code": "points-renderer-failed",
                "detail": f"the cutaway was not tried: viewprobe exited {rc} listing what stands in front ({tail})"}
    chunks, cur = {}, None
    for ln in text.splitlines():
        hm = SHOT_HEAD.match(ln)
        if hm:
            cur = hm[3].strip(); chunks[cur] = []
            continue
        if cur is not None:
            chunks[cur].append(ln)
    cands = []
    blocked_by = {}
    for st in placed:
        occ, other, cover = parse_occluders(chunks.get(st["id"], []), keys)
        need = [k for k in keys if not st["participants"][k]["best"]]
        hide, kept_px, hide_px, blocked = hideable(occ, other, need, protected)
        for o, n in blocked.items():
            blocked_by[o] = blocked_by.get(o, 0) + n
        if hide and all(cover[k] > 0 for k in need):
            cands.append((kept_px, len(hide), -hide_px, st["id"], st, sorted(hide), {k: occ[k] for k in keys}))
    if not cands:
        names = ", ".join(f"{k} ({n} px)" for k, n in sorted(blocked_by.items(), key=lambda kv: -kv[1])[:4])
        return {"status": "refused", "code": "cutaway-refused",
                "detail": ("the cutaway was refused: at no placed station is what stands in front hideable"
                           + (f"; it is the ground or what the pair touches or rests on: {names}" if names else
                              "; nothing hideable was found in front of the pair"))}
    cands.sort(key=lambda c: c[:4])
    tried = []
    for kept_px, n, _hp, sid, st, hide, occ in cands[:CUTAWAY_STATIONS]:
        flags = [x for k in hide for x in ("--hide", k)] + [x for k in keys for x in ("--protect", k)]
        cam = ["--eye", f"{st['eye'][0]},{st['eye'][1]},{st['eye'][2]}",
               "--look", f"{mid[0]:.1f},{mid[1]:.1f},{lookz:.1f}", "--fov", f"{fov:g}"]
        tag = f"p3-cutaway-{sid}"
        rc, text = R.call(tag, [cell], [*cam, *marks, "--ref-surface", "--report-mode", "--size", str(size), "--msaa", "0",
                                        "--nav", "off", *cov_flags, *flags, *[x for xy in grid_px for x in ("--pick", f"{xy[0]},{xy[1]}")]])
        pr_ = next((m for m in (PROTECT_REFUSED.match(ln) for ln in text.splitlines()) if m), None)
        if rc != 0:
            tried.append(f"{sid}: " + (pr_[1] if pr_ else f"viewprobe exited {rc}"))
            continue
        png = next(iter(sorted((work / tag).glob("*-fp.png"))), None)
        if png is None:
            tried.append(f"{sid}: no frame")
            continue
        img = np.asarray(Image.open(png).convert("RGB"))
        meas = measure_station(img, png, text, R.parsed(text), parts, see_through, gridset, min_depth, size)
        if not all(v["best"] for v in meas.values()):
            tried.append(f"{sid}: with {len(hide)} object(s) hidden, " + " and ".join(
                k for k, v in meas.items() if not v["best"]) + " still shows no measurable face")
            continue
        if not VIEW_LABEL.search("\n".join(ln for ln in text.splitlines() if ln.startswith("view-label:"))):
            tried.append(f"{sid}: the cutaway frame carries no view label")
            continue
        station = dict(st, participants=meas, valid=True, frame=f"{tag}/{png.name}")
        record = {"station": sid, "hidden": [{"formKey": h, "occludedPixels": {k: occ[k].get(h, 0) for k in keys}}
                                               for h in hide],
                  "protected": {k: contacts[k] for k in keys},
                  "rule": ("hidden: the opaque objects in front of a participant this station could not see; never a "
                           "participant, nothing either touches (render surfaces within 8 u) or rests on (the first "
                           "geometry under its lowest vertices), nothing whose contact is undecided"),
                  "tried": tried}
        return {"status": "ok", "station": station, "record": record, "flags": flags,
                "detail": f"station {sid} with {len(hide)} object(s) hidden"}
    names = ", ".join(f"{k} ({n} px)" for k, n in sorted(blocked_by.items(), key=lambda kv: -kv[1])[:4])
    return {"status": "refused", "code": "cutaway-refused",
            "detail": "the cutaway was refused: " + "; ".join(tried)
                      + (f"; what still stands in front may not be hidden, because the pair touches or rests on it: {names}"
                         if names else "")}


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
