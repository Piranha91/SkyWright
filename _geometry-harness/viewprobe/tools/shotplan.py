#!/usr/bin/env python3
"""shotplan -- choose a camera from the records, instead of guessing one.

A camera guessed from coordinates is wrong more often than it is right, and it
fails SILENTLY: every number a test logs can be correct while the frame shows a
wall, the wrong room, or the outside of the building. That has happened. This
tool answers "where do I put the camera to see these subjects" from viewprobe's
own geometry, and REFUSES a camera it cannot justify, saying why.

It adds no verb to viewprobe. Everything here drives flags viewprobe already
has -- `--pick`, `--nav-at`, `--refs-at` -- and the only new thing is the
arithmetic between them.

    THE IDEA
    --------
    Testing candidate cameras one at a time costs one cell load each. Instead,
    put the probe camera AT a subject, at eye height, open the field of view,
    and `--pick` a fan of rays around the horizon. Every pick returns the world
    coordinates of the first geometry along that ray, so ONE render yields the
    clearance in every direction around that subject -- and therefore every
    camera position that can see it. Occlusion is symmetric: subject -> point
    clear means point -> subject clear.

    Do that per subject, intersect the maps, and what survives is the set of
    cameras that can see EVERY subject. Then the framing arithmetic
    (half-span = 0.637 x distance at fov 65) picks the standoff, `--nav-at`
    checks the point is on the storey you meant rather than under the floor
    above it, and an aimed probe render per subject confirms the choice against
    the real depth buffer rather than against the map's interpolation.

    WHY VIEWPROBE IS THE RIGHT AUTHORITY FOR THIS
    ---------------------------------------------
    viewprobe's depth probe was measured (2026-08-31) to DISAGREE with the
    engine's `HasLOS`, because `HasLOS` asks a different question -- it is about
    an actor's awareness, not about what a rasteriser draws. A screenshot is a
    rasterisation. For "will the camera see it", viewprobe's depth buffer and
    the game camera are answering the SAME question against the SAME render
    geometry. This is the use viewprobe is actually correct for; do not read
    these results as a line-of-sight claim about the engine.

    WHAT IT CANNOT SEE
    ------------------
    `--pick` unprojects the DEPTH buffer, and alpha-blended geometry draws with
    depth writes off. A curtain, a banner or a waterfall on the ray does not
    register, so a camera can be sited behind one and called clear. The failure
    is a bad picture, not a wrong verdict -- read the frame.

    Placements are records. Actors are NOT where their placement records say:
    an actor is put at its governing AI package's anchor on cell load, and a
    MOVABLE item whose host was taken away has dropped by the time the cell is
    loaded. Feed subject positions from the RUNNING game whenever it is up:
    `--subject-from-game FORMID[=label]` reads them off DevBench (`inspect
    kind=refs`) and refuses, naming the port, when no game answers; `--subject`
    takes coordinates from anywhere else.

    Three more things the GAME decides at runtime, each of which put a wrong
    thing in a planned frame on 2026-09-03, and what this tool now does about
    each: a DOOR LEAF standing open across the clear bearing (the records say
    closed) -- every drawn Door ref within `--door-radius` of a subject goes
    into the worklist shot as `doors`, for the runner to close before the
    shot; a camera INSIDE FURNITURE (navmesh runs up to a bookcase, the
    placement test allows a camera off-mesh, and the depth probe looks out
    through the case's open back) -- `--inside-at` asks whose box the eye is
    in, and the placement pass refuses an eye inside any physical ref's box up
    to `--inside-cap`; and the moved subject above.

    THE CONVENTION SEAM
    -------------------
    viewprobe yaw is DEGREES, 0 = north (+Y), clockwise. DevBench
    `camera action=drive` wants the same angle in RADIANS and applies it
    faithfully. Its `action=get` reports a DIFFERENT parameterization of the
    same orientation -- not the commanded angles, and not simply their
    negation: see `predict_readback`. Getting either wrong produces a plausible
    frame of the wrong thing with no error. Every pose this tool prints carries
    the drive form and the expected readback, and `shotplan.py devbench`
    converts a worklist without a human doing the multiplication.

Subcommands
    plan      pick a camera for a set of subjects, and emit it
    check     judge a camera someone already has -- the positive control
    devbench  read a viewprobe worklist and print DevBench `camera drive` args

Run `shotplan.py <subcommand> --help` for the flags.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request


# ---------------------------------------------------------------- run log --
# The progress/timing contract (standards/HOUSECARL_RUN_EVENTS.md, roadmap 4.16). Loaded by path
# rather than imported, because a probe's tools directory is not on the repo's import path; if the
# adapter is missing the whole thing degrades to a no-op and this script behaves as it did before.
def _runlog():
    import importlib.util
    here = os.path.dirname(os.path.abspath(__file__))
    cand = os.path.abspath(os.path.join(here, "..", "..", "..", "scripts", "runlog.py"))
    if not os.path.isfile(cand):
        return None
    try:
        spec = importlib.util.spec_from_file_location("housecarl_runlog", cand)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod
    except Exception:
        return None


_RUNLOG = _runlog()


class _NullRun:
    def __getattr__(self, _name):
        return lambda *a, **k: self

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _run(op, **kw):
    if _RUNLOG is None:
        return _NullRun()
    try:
        return _RUNLOG.Run(op, **kw)
    except Exception:
        return _NullRun()


HERE = os.path.dirname(os.path.abspath(__file__))
VIEWPROBE_DIR = os.path.dirname(HERE)

# viewprobe's own conventions, restated here because every formula below depends on them:
#   yaw  degrees, 0 = north (+Y), increasing CLOCKWISE   (Program.cs: atan2(dx, dy))
#   pitch degrees, POSITIVE looks DOWN
#   image square, --fov is the FULL vertical angle, so horizontal fov == vertical fov
# The pick unprojection was verified against real picks on 2026-09-01: for pixel (ix, iy) in a
# size x size frame, ndc x = (ix+0.5)/size*2-1 and ndc y = ((size-1-iy)+0.5)/size*2-1, and the
# ray's angle off the axis is atan(ndc * tan(fov/2)) in each axis. Two edge picks and two
# off-horizon picks reproduced their measured world hits to under 0.3 degrees.


# --------------------------------------------------------------------------------------
# geometry
# --------------------------------------------------------------------------------------

def bearing_deg(dx: float, dy: float) -> float:
    """Skyrim/viewprobe yaw for a direction: 0 = +Y (north), clockwise."""
    return math.degrees(math.atan2(dx, dy)) % 360.0


def bearing_vec(deg: float) -> tuple[float, float]:
    r = math.radians(deg)
    return math.sin(r), math.cos(r)


def ang_diff(a: float, b: float) -> float:
    """Signed smallest difference a - b, in (-180, 180]."""
    return (a - b + 180.0) % 360.0 - 180.0


def _ni_fast_atan2(a_y: float, a_x: float) -> float:
    """`NiFastATan2`, transcribed from CommonLibSSE-NG `src/RE/N/NiMath.cpp`.

    It is not `atan2`. It agrees with `-atan2` while |y| <= |x| and with `+atan2` otherwise, and
    that branch is the whole reason `camera get` was read as "a convention that changes with
    bearing" -- for the yaw pair the test |y| > |x| is
    |sin(yaw) cos(pitch)| > |cos(yaw)|, i.e. |tan(yaw) cos(pitch)| > 1. It
    reduces to |tan(yaw)| > 1 only at pitch zero; a non-zero pitch moves the
    switch away from the odd multiples of 45 degrees.
    """
    if a_x == 0.0 and a_y == 0.0:
        return 0.0
    offset = 0.0
    if abs(a_y) > abs(a_x):
        z = a_x / a_y
        if z > 0.0:
            offset = math.pi / 2.0
        elif z < 0.0:
            offset = -math.pi / 2.0
        else:
            return math.pi / 2.0 if a_y > 0.0 else -math.pi / 2.0
    else:
        z = a_y / a_x
        if z == 0.0:
            return 0.0 if a_x > 0.0 else math.pi
    z2 = z * z
    r = 0.0208351
    for c in (-0.0851330, 0.1801410, -0.3302995, 0.9998660):
        r = r * z2 + c
    r = offset - r * z
    if a_y < 0.0 and a_x < 0.0:
        r -= math.pi
    if a_y > 0.0 and a_x < 0.0:
        r += math.pi
    return r


def predict_readback(yaw_deg: float, pitch_deg: float):
    """What DevBench's `camera get` reports, in RADIANS, for a camera driven to these degrees.

    Derived from source, not fitted: DevBench (`src/Tools.cpp`) reports `camPitch = e.x` and
    `camYaw = e.z` from `cameraRoot->world.rotate.ToEulerAnglesXYZ(e)`, and CommonLibSSE-NG
    computes those as `-NiFastATan2(-m12, m22)` and `-NiFastATan2(-m01, m00)`. Checked against 26
    retained observations and 42 held-out live poses across both camera modes; worst error
    0.0007 deg, which is float32 quantization of the reported value.

    Returns None where `ToEulerAnglesXYZ` returns false and DevBench writes NEITHER key -- treat
    them as ABSENT there, not as zero. Compare a readback against THIS, never against the
    commanded angles: at yaw 90 the reported pitch is zero for every pitch, and comparing it to
    the command would call a correct pose 30 degrees wrong.
    """
    y, p = math.radians(yaw_deg), math.radians(pitch_deg)
    sy, cy, sp, cp = math.sin(y), math.cos(y), math.sin(p), math.cos(p)
    if abs(sy * sp) >= 1.0:
        return None
    return (-_ni_fast_atan2(-sy * cp, cy), -_ni_fast_atan2(-cy * sp, cp))


def camera_basis(yaw_deg: float, pitch_deg: float):
    """(forward, right, up) unit vectors in Skyrim world axes (Z up).

    Checked against the two poses the README documents: yaw 0 pitch 0 looks along +Y with
    +X to the right and +Z up; pitch 90 looks along -Z with north-up, which is what makes a
    top-down render read north-up.
    """
    y, p = math.radians(yaw_deg), math.radians(pitch_deg)
    f = (math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), -math.sin(p))
    r = (math.cos(y), -math.sin(y), 0.0)
    u = (r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0])
    return f, r, u


def pixel_for_angles(h_off_deg: float, v_off_deg: float, fov_deg: float, size: int):
    """The image pixel whose ray leaves the camera at these offsets from the axis.

    The inverse of the unprojection above, so a fan of rays can be sampled at UNIFORM
    ANGLES rather than at uniform pixels -- which is what a clearance map wants, and what a
    uniform pixel grid does not give (pixels crowd towards the axis in angle).
    Returns None when the angle falls outside the frustum.
    """
    t = math.tan(math.radians(fov_deg) / 2.0)
    nx = math.tan(math.radians(h_off_deg)) / t
    ny = math.tan(math.radians(v_off_deg)) / t
    if abs(nx) >= 1.0 or abs(ny) >= 1.0:
        return None
    ix = int(round((nx + 1.0) / 2.0 * size - 0.5))
    gy = int(round((ny + 1.0) / 2.0 * size - 0.5))
    iy = size - 1 - gy
    if not (0 <= ix < size and 0 <= iy < size):
        return None
    return ix, iy


# --------------------------------------------------------------------------------------
# running viewprobe, and reading what it says
# --------------------------------------------------------------------------------------

PICK_HIT = re.compile(
    r"^\s*pick (-?\d+),(-?\d+): (-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\s+cell \([^)]*\)\s+([\d.]+) u from the eye")
PICK_BG = re.compile(r"^\s*pick (-?\d+),(-?\d+): (background|degenerate|outside)")
# Deliberately ASCII-only. viewprobe writes an em dash in these lines and the encoding of the
# pipe is not guaranteed on Windows; a regex that requires the dash fails to match and the
# caller reads "no nav-at answer" instead of "the parser broke". Match up to the dash and no
# further, and read the drawn/deleted state out of the remainder by substring.
NAV_HIT = re.compile(
    r"^\s*nav-at (-?\d+),(-?\d+): (.+?) tri (\d+) covers it, z (-?[\d.]+), flags 0x([0-9A-Fa-f]+)(.*)$")
NAV_MISS = re.compile(
    r"^\s*nav-at (-?\d+),(-?\d+): NO winning triangle covers this point"
    r"(?:; nearest is (.+?) tri (\d+), ([\d.]+) u away)?")
# `--refs-at` block header and rows. A FormKey's plugin name can carry spaces ("JK's College of
# Winterhold.esp"), so it is matched up to its .esp/.esm/.esl rather than as one token; the base
# TYPE is the first field after `base`, which is the word the door test looks for.
REFS_HEAD = re.compile(r"^\s*---- refs within (\d+) u of (-?\d+),(-?\d+): (\d+) ----")
REFS_ROW = re.compile(
    r"^\s*(\d+) u\s+(\S{6}:.+?\.es[pml])\s+at (-?\d+),(-?\d+),(-?\d+)\s+scale ([\d.]+)\s+\[(.*?)\]"
    r"\s+base (\S+) (\S{6}:.+?\.es[pml]) (\S+)")
# `--inside-at` header (both forms) and rows.
INSIDE_HEAD = re.compile(r"^\s*inside-at (-?\d+),(-?\d+),(-?\d+): (\d+ ref|no physical)")
# The nomination's own provenance, printed on the same header line: which box selected the refs,
# how many could only be selected by an unverified record box, and how many refs the query could
# not rule out at all because their mesh would not read. An EMPTY nomination over a non-zero
# population of those is not clearance.
INSIDE_COVER = re.compile(
    r"nominated by mesh bounds, (\d+) by record OBND alone \(mesh unreadable\), "
    r"(\d+) physical ref\(s\) whose mesh could not be read at all")
# The `--inside-bounds obnd` arm exists to reproduce the OLD nomination for comparison. Its answers
# are a measurement of the legacy behaviour and must never qualify a production camera, so it is
# recognised by name rather than left to fall through as "no statement" -- which is how a
# comparison run could have published one.
INSIDE_COVER_LEGACY = re.compile(r"nominated by RECORD OBND")
INSIDE_ROW = re.compile(r"^\s*(\S{6}:.+?\.es[pml])\s{2}(\S+)\s{2}(\S+)\s{2}dims (-?\d+)x(-?\d+)x(-?\d+) u")
INSIDE_RAYS = re.compile(r"\srays ((?:(?:[+-][xy]|up|down) (?:\d+|-)\s*){6})")
# `--clear-at` header and rows: what placed mesh geometry is really around a point, on each world
# axis, whether or not any box encloses it. The header's four population counts are what makes an
# EMPTY answer readable -- "nothing found" only means "nothing there" when the broad phase saw
# everything, and the header says whether it did.
CLEAR_HEAD = re.compile(
    r"^\s*clear-at (-?\d+),(-?\d+),(-?\d+): nearest placed geometry on each world axis within "
    r"([\d.]+) u . (\d+) candidate\(s\) in the broad phase, (\d+) with a readable mesh, "
    r"(\d+) unreadable, (\d+) physical ref\(s\) skipped for want of a usable OBND, "
    r"(\d+) physical ref\(s\) with no model at all, (\d+) selected by record OBND because their "
    r"mesh could not be read, (\d+) admitted by mesh bounds that the record box alone would have "
    r"excluded; terrain: (.*?) \(broad phase")
CLEAR_ROW = re.compile(
    r"^\s*([+-][xy]|up|down)\s+(?:([\d.]+) u\s+"
    r"((?:\S{6}:.+?\.es[pml])|(?:000000:TERRAIN))\s{2}(\S+)\s{2}at |-\s+nothing within )")
# viewprobe's own configuration fingerprint, printed for a candidate configuration (--order).
CONFIG_FINGERPRINT = re.compile(r"capture input configuration:\s*([0-9a-fA-F]{16,64})")
RAY_AXES = ("+x", "-x", "+y", "-y", "up", "down")


class Probe:
    """One viewprobe invocation, with its answers parsed out of stdout.

    The load walk dominates the cost and the queries are free once it has run, so the callers
    below batch every question they can into one of these rather than paying the walk per
    question. That is not an optimisation, it is the difference between a planner worth using
    and one nobody will wait for.
    """

    def __init__(self, cmd, cwd=None, echo=True):
        self.cmd, self.cwd, self.echo = cmd, cwd, echo
        self.stdout = ""
        self.seconds = 0.0
        self.rc = None

    def run(self):
        t0 = time.time()
        if self.echo:
            print("    $ " + " ".join(shlex.quote(c) for c in self.cmd[-14:]), file=sys.stderr)
        p = subprocess.run(self.cmd, cwd=self.cwd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        self.stdout, self.rc, self.seconds = (p.stdout or "") + (p.stderr or ""), p.returncode, time.time() - t0
        if self.rc != 0:
            tail = "\n".join(self.stdout.strip().splitlines()[-12:])
            raise SystemExit(f"viewprobe exited {self.rc} after {self.seconds:.0f}s.\n{tail}")
        if self.echo:
            print(f"      ({self.seconds:.0f}s)", file=sys.stderr)
        return self

    def picks(self):
        """{(ix, iy): (x, y, z, dist) | None}. None means the ray found nothing to hit."""
        out = {}
        for line in self.stdout.splitlines():
            m = PICK_HIT.match(line)
            if m:
                out[(int(m[1]), int(m[2]))] = (float(m[3]), float(m[4]), float(m[5]), float(m[6]))
                continue
            m = PICK_BG.match(line)
            if m:
                out[(int(m[1]), int(m[2]))] = None
        return out

    def nav(self):
        """{(x, y): {'levels': [z, ...], 'nearest': u | None}} -- one entry per queried point.

        A point can return SEVERAL levels: `--nav-at` reports every storey, which is exactly
        what tells "the camera is in the room" apart from "the camera is under the floor above
        the room". Queries are sent as integers so the printed key matches the query key.
        """
        out = {}
        for line in self.stdout.splitlines():
            m = NAV_HIT.match(line)
            if m:
                e = out.setdefault((int(m[1]), int(m[2])), {"levels": [], "nearest": None})
                e["levels"].append(float(m[5]))
                continue
            m = NAV_MISS.match(line)
            if m:
                e = out.setdefault((int(m[1]), int(m[2])), {"levels": [], "nearest": None})
                e["nearest"] = float(m[5]) if m[5] else None
        return out

    def refs(self):
        """The `--refs-at` block, header and rows, for a human to read in a refusal.

        "What am I standing inside" is the question a refused camera raises, and this is the
        only flag that answers it by name. The rows are indented under a "---- refs within"
        header, so the block is read by position rather than by matching each row.
        """
        out, inside = [], False
        for ln in self.stdout.splitlines():
            if "---- refs within" in ln:
                inside = True
                out.append(ln.strip())
                continue
            if inside:
                if ln.startswith("      ") and " u  " in ln:
                    out.append(ln.strip())
                else:
                    inside = False
        return out

    def refs_near(self):
        """{(x, y, r): [row, ...]} for every `--refs-at` block, each row parsed: dist, formkey,
        pos, scale, why (viewprobe's drawn / not-drawn verdict), type, base, edid. Queries are
        sent as integers so the printed key matches the query key."""
        out, cur = {}, None
        for ln in self.stdout.splitlines():
            m = REFS_HEAD.match(ln)
            if m:
                cur = out.setdefault((int(m[2]), int(m[3]), int(m[1])), [])
                continue
            if cur is None:
                continue
            m = REFS_ROW.match(ln)
            if m:
                cur.append({"dist": float(m[1]), "formkey": m[2],
                            "pos": (float(m[3]), float(m[4]), float(m[5])), "scale": float(m[6]),
                            "why": m[7], "type": m[8], "base": m[9], "edid": m[10]})
            elif ln.strip() and not ln.startswith("      "):
                cur = None
        return out

    class _Nominations(list):
        """The rows, plus the nomination's own coverage. A plain list cannot carry the second, and
        the empty case is exactly where the coverage matters most."""
        coverage: dict

    def inside(self):
        """{(x, y, z): [enclosing ref, ...]} for every `--inside-at` query; an empty list means
        no physical ref's box encloses the point. Each entry: formkey, type, edid, dims (the
        world box's extents), maxdim. A query with no answer at all is ABSENT, not empty."""
        out, cur = {}, None
        for ln in self.stdout.splitlines():
            m = INSIDE_HEAD.match(ln)
            if m:
                key = (int(m[1]), int(m[2]), int(m[3]))
                cur = out.setdefault(key, self._Nominations())
                c = INSIDE_COVER.search(ln)
                # Carried on the LIST, so an empty nomination still arrives with its coverage --
                # which is the whole point: the empty case is the one that used to read as clear.
                # `source` is the load-bearing field: only "mesh" is evidence. A missing statement
                # is NOT zero gaps, and the legacy comparison arm is not a production answer; both
                # used to arrive as {0, 0} and read as fully-checked coverage.
                if c:
                    cur.coverage = {"source": "mesh", "obnd_only": int(c[1]),
                                    "unreadable": int(c[2])}
                elif INSIDE_COVER_LEGACY.search(ln):
                    cur.coverage = {"source": "obnd-legacy", "obnd_only": 0, "unreadable": 0}
                else:
                    cur.coverage = {"source": None, "obnd_only": 0, "unreadable": 0}
                continue
            if cur is None:
                continue
            m = INSIDE_ROW.match(ln)
            if m:
                dims = (float(m[4]), float(m[5]), float(m[6]))
                rays = None
                mr = INSIDE_RAYS.search(ln)
                if mr:
                    toks = mr[1].split()
                    rays = {toks[i]: (None if toks[i + 1] == "-" else float(toks[i + 1]))
                            for i in range(0, len(toks) - 1, 2)}
                cur.append({"formkey": m[1], "type": m[2], "edid": None if m[3] == "-" else m[3],
                            "dims": dims, "maxdim": max(dims), "rays": rays})
            elif ln.strip() and not ln.startswith("      "):
                cur = None
        return out

    def clear(self):
        """{(x, y, z): {reach, candidates, meshed, unread, no_box, no_mesh, axes}} per `--clear-at`.

        `axes` maps each of the six world axes to `{'dist', 'formkey', 'edid'}`, with `dist` None
        for "nothing within the reach". The population counts ride along because an axis with
        nothing on it is only evidence of open space when the broad phase could see the refs that
        would have been on it -- see support_verdict, which refuses to read an empty answer as a
        clear one when coverage was incomplete. A query with no answer at all is ABSENT, not empty.
        """
        out, cur = {}, None
        for ln in self.stdout.splitlines():
            m = CLEAR_HEAD.match(ln)
            if m:
                cur = out.setdefault((int(m[1]), int(m[2]), int(m[3])), {
                    "reach": float(m[4]), "candidates": int(m[5]), "meshed": int(m[6]),
                    "unread": int(m[7]), "no_box": int(m[8]), "no_mesh": int(m[9]),
                    "obnd_only": int(m[10]), "obnd_would_miss": int(m[11]),
                    "terrain": m[12], "axes": {}})
                continue
            if cur is None:
                continue
            m = CLEAR_ROW.match(ln)
            if m:
                cur["axes"][m[1]] = {"dist": float(m[2]) if m[2] else None,
                                     "formkey": m[3], "edid": None if m[4] in (None, "-") else m[4]}
            elif ln.strip() and not ln.startswith("      "):
                cur = None
        return out


def doors_near(subjects, near, args):
    """Door refs within --door-radius (3-D) of any subject, out of the `--refs-at` blocks: the
    FormKeys the worklist carries and a runner closes before the shot. Drawn doors only -- a
    disabled leaf is not standing anywhere. Measured 2026-09-03: the OFF candlestick's only clear
    bearings looked through a doorway whose leaf the game left open; closing every door within
    300 u of the subject with SetOpen(false) before the shot fixed the frame."""
    out, seen = [], set()
    for s in subjects:
        for r in near.get((round(s.x), round(s.y), int(args.door_radius)), []):
            if r["type"] != "Door" or not r["why"].startswith("drawn"):
                continue
            d = math.dist(r["pos"], (s.x, s.y, s.z))
            if d > args.door_radius or r["formkey"] in seen:
                continue
            seen.add(r["formkey"])
            out.append({"formkey": r["formkey"], "edid": r["edid"], "pos": r["pos"],
                        "subject": s.label, "dist": round(d)})
    return out


def print_doors(doors, args):
    print(f"\n--- doors within {args.door_radius:.0f} u of the subjects ({len(doors)}) ---")
    for d in doors:
        print(f"  {d['formkey']}  {d['edid'] or '-'}  at {d['pos'][0]:.0f},{d['pos'][1]:.0f},"
              f"{d['pos'][2]:.0f}  {d['dist']} u from {d['subject']}")
    if doors:
        print("  the records say closed; the game may leave a leaf standing open across the bearing."
              " The worklist carries these as `doors` -- close them before the shot"
              " (dbharness.close_doors), then drive the camera.")
    else:
        print("  none -- no drawn Door ref that close to a subject")


# --------------------------------------------------------------------------------------
# subjects read off the running game
# --------------------------------------------------------------------------------------

def devbench_base(args):
    """http://127.0.0.1:PORT for DevBench: --port, then DEVBENCH_PORT, then the port in the file
    DEVBENCH_RUNTIME_JSON names (the game writes it), then 8920."""
    port = getattr(args, "port", None) or os.environ.get("DEVBENCH_PORT")
    if not port:
        rj = os.environ.get("DEVBENCH_RUNTIME_JSON")
        if rj and os.path.exists(rj):
            try:
                port = json.load(open(rj)).get("port")
            except (OSError, ValueError):
                port = None
    return f"http://127.0.0.1:{port or 8920}"


def devbench_post(base, tool, body, timeout=20):
    req = urllib.request.Request(f"{base}/api/tool/{tool}", data=json.dumps(body).encode(),
                                 headers={"content-type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        return {"_httpError": e.code, "_body": e.read().decode(errors="replace")[:300]}


def subjects_from_game(args):
    """`--subject-from-game FORMID[=label]` -> "X,Y,Z=label" specs read off the RUNNING game.

    A movable subject is planned from the game after the load read, never from its record: the
    item has dropped by the time the cell is loaded (2026-09-03, a chitin's frame found a barrel).
    FORMID is a runtime id (0x........) or a FormKey (XXXXXX:Plugin.esp) resolved through the
    game's own load index. With no game answering this REFUSES and says where the port comes
    from -- a plan quietly made from the record would be the failure this flag exists to stop.
    """
    specs = getattr(args, "subject_from_game", None) or []
    if not specs:
        return []
    base = devbench_base(args)
    try:
        with urllib.request.urlopen(f"{base}/api/health", timeout=5) as r:
            health = json.load(r)
    except (OSError, ValueError) as e:
        raise SystemExit(
            f"--subject-from-game: no DevBench answers at {base} ({e}). Set DEVBENCH_PORT, or "
            "DEVBENCH_RUNTIME_JSON to <game>/Data/SKSE/Plugins/devbench/runtime.json (the game "
            "writes its port there), or pass --port. A movable subject is planned from the RUNNING "
            "game, after the load read -- its record says where it was authored, not where it lies.")
    index = None
    out = []
    print(f"--- subjects from the running game ({base}, {health.get('exe')}) ---")
    for spec in specs:
        body, _, label = spec.partition("=")
        fid = body.strip()
        if ":" in fid:
            if index is None:
                m = devbench_post(base, "inspect", {"kind": "mods"})
                index = ([e if isinstance(e, dict) else {"name": e, "index": i}
                          for i, e in enumerate(m.get("plugins") or [])],
                         [e if isinstance(e, dict) else {"name": e, "index": i}
                          for i, e in enumerate(m.get("lightPlugins") or [])])
                if not index[0] and not index[1]:
                    raise SystemExit("--subject-from-game: inspect kind=mods lists no plugins -- "
                                     "nothing is loaded (it answers nothing at the main menu)")
            lid, plugin = fid.split(":", 1)
            n = int(lid, 16)
            hit = None
            for e in index[0]:
                if e.get("name", "").lower() == plugin.lower():
                    hit = (e.get("index", 0) << 24) | (n & 0xFFFFFF)
            if hit is None:
                for e in index[1]:
                    if e.get("name", "").lower() == plugin.lower():
                        hit = 0xFE000000 | (e.get("index", 0) << 12) | (n & 0xFFF)
            if hit is None:
                raise SystemExit(f"--subject-from-game {spec}: plugin {plugin!r} is not in the "
                                 "running load order")
            fid = f"0x{hit:08X}"
        r = devbench_post(base, "inspect", {"kind": "refs", "formId": fid})
        refs = r.get("refs") or []
        p = refs[0].get("position") if refs else None
        if not p:
            raise SystemExit(f"--subject-from-game {spec}: the game has no reference {fid} "
                             f"(inspect kind=refs returned {json.dumps(r)[:200]}) -- is a game "
                             "loaded, and is the cell it lives in loaded?")
        name = label or fid
        print(f"  {name:<18} {p[0]:.1f}, {p[1]:.1f}, {p[2]:.1f}   ({fid}"
              + (f" from {body.strip()}" if ":" in body else "") + ", the game's current position)")
        out.append(f"{p[0]},{p[1]},{p[2]}={name}")
    return out


def collect_subjects(args):
    """--subject / --surface specs plus the ones read off the game; at least one is required.

    ONE input contract, stated here so no caller has to guess it:

        --subject X,Y[,Z][=label]   a thing standing at its feet: framing extent comes from
                                    --subject-height / --subject-width
        --surface X,Y,Z[=label]     a DECLARED point on an opaque face, supplied by the caller
                                    from source evidence; framing context comes from
                                    --surface-extent

    Both are repeatable and may be mixed. The surface points are NOT discovered -- this tool does
    not read meshes and does not find a contact curve. A caller that has such points (from a
    record, a dumped soup, a retained worklist) declares them; a caller that does not has to get
    them somewhere else first. That limit is deliberate and is restated in the output.
    """
    default_z = args.floor_z if args.floor_z is not None else 0.0
    out = [Subject(s, default_z) for s in list(args.subject) + subjects_from_game(args)]
    extent = getattr(args, "surface_extent", 0.0)
    out += [Subject(s, default_z, kind="surface", extent=extent)
            for s in (getattr(args, "surface", None) or [])]
    if not out:
        raise SystemExit("no subjects: give --subject X,Y[,Z][=label], --surface X,Y,Z[=label] "
                         "and/or --subject-from-game FORMID[=label]")
    n_surf = sum(1 for s in out if s.kind == "surface")
    cap = getattr(args, "surface_max", 8)
    if n_surf > cap:
        raise SystemExit(f"--surface takes a small bounded set: {n_surf} given, --surface-max is "
                         f"{cap}. Every surface point costs a pick in the verification render and "
                         "widens the frame it has to fit in; declare the few that carry the claim.")
    return out


def vp_command(vp: str | None) -> list[str]:
    if vp:
        return shlex.split(vp)
    return ["dotnet", "run", "--project", VIEWPROBE_DIR, "-c", "Release", "--no-launch-profile", "--"]


class Runner:
    """Holds the viewprobe command, the cell and the pass-through target flags.

    The cell MUST be args[0] literally -- viewprobe reads a leading positional as the cell (or a
    worklist), and a target flag placed before it silently takes its place. That trap cost a
    round on 2026-08-31 and is the reason this class exists rather than string concatenation at
    each call site.
    """

    def __init__(self, cell, vp=None, passthrough=(), out=None, size=320, echo=True):
        self.base, self.cell = vp_command(vp), cell
        self.passthrough, self.out, self.size, self.echo = list(passthrough), out, size, echo
        self.renders, self.seconds = 0, 0.0
        # NOTE: there is deliberately no run-wide `config_fingerprint` here. viewprobe's identity
        # belongs to the INVOCATION that printed it, and a Runner-level cache quietly lent one
        # render's identity to another: a candidate whose verification supplied a fingerprint, a
        # second candidate whose verification supplied none, and the second reported the first's
        # (reviewed 2026-09-14). An identity that the render in hand did not produce is exactly the
        # silent wrong answer this binding exists to prevent, so each Probe carries its own and
        # absence stays absence. See scene_identity.

    def probe(self, *args):
        cmd = self.base + [self.cell, "--size", str(self.size), "--msaa", "0", "--nav", "off"]
        cmd += [str(a) for a in args]
        if self.out:
            cmd += ["--out", self.out]
        cmd += self.passthrough
        p = Probe(cmd, echo=self.echo).run()
        self.renders += 1
        self.seconds += p.seconds
        m = CONFIG_FINGERPRINT.search(p.stdout)
        # THIS invocation's answer, every time -- including None, which is an answer.
        p.config_fingerprint = m[1] if m else None
        return p


# --------------------------------------------------------------------------------------
# the clearance map
# --------------------------------------------------------------------------------------

class Clearance:
    """bearing (deg) -> how far you can travel from this point before hitting geometry.

    Stored per bearing bin as the MINIMUM horizontal distance over the sampled elevations, so a
    low beam or a chair back bounds the direction rather than being averaged away. A bin whose
    rays all found nothing is stored as None -- UNKNOWN, not infinite. In an interior a ray that
    hits nothing has left the shell through a hole the renderer does not close, and treating
    that as open space is precisely how a camera ends up outside the building.
    """

    def __init__(self, origin, step):
        self.origin, self.step = origin, step
        self.bins: dict[int, float | None] = {}
        self.hits: dict[int, tuple] = {}

    def key(self, deg):
        return int(round((deg % 360.0) / self.step)) % int(round(360.0 / self.step))

    def add(self, deg, dist, hit):
        k = self.key(deg)
        if dist is None:
            self.bins.setdefault(k, None)
            return
        cur = self.bins.get(k, "missing")
        if cur == "missing" or cur is None or dist < cur:
            self.bins[k] = dist
            self.hits[k] = hit

    def at(self, deg):
        """(clearance, why) at a bearing. clearance None means REFUSE, with why saying which."""
        k = self.key(deg)
        if k not in self.bins:
            return None, "no ray was sampled along this bearing"
        v = self.bins[k]
        if v is None:
            return None, "every probe ray along this bearing left the cell without hitting geometry"
        return v, None

    def sampled(self):
        return sum(1 for v in self.bins.values() if v is not None)


def build_clearance(run: Runner, origin, floor_z, args) -> Clearance:
    """One clearance map, from `--map-yaws` renders taken AT the origin.

    Each render covers a sector; each bearing in that sector gets one pick per elevation. The
    pixels are solved from the angles rather than laid out as a uniform grid, so the angular
    step is exactly what was asked for at every bearing including the frame edges.
    """
    cl = Clearance(origin, args.bearing_step)
    eye = (origin[0], origin[1], floor_z + args.eye_height)
    n_bear = int(round(360.0 / args.bearing_step))
    bearings = [i * args.bearing_step for i in range(n_bear)]
    elevs = [float(e) for e in args.elevations.split(",")]
    half = args.fov_map / 2.0 - args.edge_margin

    for yaw in args.map_yaws:
        want = [b for b in bearings if abs(ang_diff(b, yaw)) <= half]
        px, meta = [], {}
        for b in want:
            for e in elevs:
                p = pixel_for_angles(ang_diff(b, yaw), e, args.fov_map, run.size)
                if p is None:
                    continue
                px.append(p)
                meta[p] = (b, e)
        if not px:
            continue
        flags = ["--eye", f"{eye[0]:.1f},{eye[1]:.1f},{eye[2]:.1f}",
                 "--yaw", f"{yaw:g}", "--pitch", "0", "--fov", f"{args.fov_map:g}"]
        for ix, iy in px:
            flags += ["--pick", f"{ix},{iy}"]
        got = run.probe(*flags).picks()
        for pix, (b, e) in meta.items():
            hit = got.get(pix)
            if hit is None:
                cl.add(b, None, None)
            else:
                d2 = math.hypot(hit[0] - eye[0], hit[1] - eye[1])
                cl.add(b, d2, hit)
    return cl



# --------------------------------------------------------------------------------------
# the frame the GAME will actually render
# --------------------------------------------------------------------------------------
#
# Estimating the target frame from eyeballed pixels is the same mistake as guessing a camera.
# The game states its resolution and its field of view in the ini files an MO2 profile hands it,
# so read them.
#
# The mapping from the setting to a frustum is the part that is NOT in the ini. It is MEASURED:
# fDefaultWorldFOV is the HORIZONTAL angle at a fixed 16:9 BASE, so the VERTICAL angle is fixed
# by the setting alone and a wider frame gets more horizontal view ("Hor+"):
#
#     tan(fov_v / 2) = tan(fov_setting / 2) / (16/9)
#     tan(fov_h / 2) = tan(fov_v / 2) * (width / height)
#
# Measured 2026-09-09 on Skyrim SE 1.7.104 by rotating a position-pinned camera through known
# angles and fitting the frustum under which the two frames register -- a measurement that
# depends on no renderer and on no knowledge of the scene:
#
#     setting  output       vertical            horizontal
#     80       3840x2160    50.524 / 50.539     79.998 / 80.005     (free cam / player cam)
#     100      3840x2160    67.684 / 67.698     99.999 / 99.997
#     65       3840x2160    39.420              64.993              (set by the console `fov`)
#     80       1600x1200    50.546 / 50.546     64.372 / 64.373
#
# THE VERTICAL ANGLE IS THE SAME AT BOTH OUTPUT ASPECTS. That is the whole finding, and it is
# what a single-aspect campaign cannot see. Two earlier readings died on exactly that:
#
#   * "horizontal at a fixed 4:3 base", which stood here. It was checked against a 2026-09-01
#     measurement of ~50.5 deg vertical and looked right to half a degree -- but only because it
#     was paired with a WRONG setting: 65 was assumed where the running game held 80, and the
#     two errors cancelled. At setting 80 it predicts 64.37 vertical against 50.53 measured.
#   * "horizontal at the frame's OWN aspect", which fits every 16:9 number to 0.005 deg and is
#     refuted the moment the output stops being 16:9: at 1600x1200 it predicts 64.37 vertical
#     and 80.00 horizontal against 50.55 and 64.37 measured.
#
# It is a RULE, not a constant: change the setting or the output shape and RECALCULATE, do not
# re-measure and do not fit a fresh empirical angle per session. What DOES need a fresh
# measurement is a different runtime (VR) or a mod that takes the projection over.

FOV_BASE_ASPECT = 16.0 / 9.0

# The value the engine holds when no ini sets the key. NOT a guess and not universal: measured on
# Skyrim SE 1.7.104 by the 2026-09-05 ini audit (the setting is a `Name:Section` string in the
# exe; SKSE's Utility.GetINIFloat reads the live collection) and confirmed 2026-09-09 by a live
# query returning 80.0 with the key absent from every ini of the profile, on a frame that then
# measured 50.53 deg vertical. The widely repeated 65 is Skyrim LE's default, and adopting it
# here is exactly the error described above. Any other runtime needs its own reading, so this is
# reported with its provenance and overridable with --fov-setting.
FOV_ENGINE_DEFAULT = 80.0
FOV_ENGINE_DEFAULT_PROVENANCE = ("measured on Skyrim SE 1.7.104; NOT the LE default of 65, and "
                                 "not established for VR -- confirm with a live "
                                 "Utility.GetINIFloat query or pass --fov-setting")

INI_LOAD_ORDER = ["skyrim.ini", "skyrimcustom.ini", "skyrimprefs.ini"]

# Which file WINS when a key is set in more than one. This is a different question from the
# order the files are listed in, and conflating the two is how this got shipped wrong once:
# arm B measured skyrim.ini beating skyrimprefs.ini, that was encoded as "the first hit in
# INI_LOAD_ORDER wins", and arm D then measured skyrimcustom.ini beating skyrim.ini -- which
# INI_LOAD_ORDER lists SECOND, so the selection silently picked 95 where the game used 110 and
# the planner's vertical angle was out by 14.5 degrees. Precedence is now stated on its own.
#
# Measured 2026-09-09 on Skyrim SE 1.7.104, two pairs, one profile:
#   skyrimcustom.ini 110  beats  skyrim.ini 95        (the game reported 110)
#   skyrim.ini       100  beats  skyrimprefs.ini 110  (the game reported 100)
# Highest precedence first. A file not named here ranks below every file that is.
INI_PRECEDENCE = ["skyrimcustom.ini", "skyrim.ini", "skyrimprefs.ini"]


def ini_winner(hits):
    """The (path, value) the game will actually use, from `ini_lookup`'s hits.

    Selects by INI_PRECEDENCE, not by position in the list -- see the note above.
    """
    if not hits:
        return None
    rank = {name: i for i, name in enumerate(INI_PRECEDENCE)}
    return min(hits, key=lambda h: rank.get(os.path.basename(h[0]).lower(), len(rank)))
MY_GAMES_CANDIDATES = ["Skyrim Special Edition", "Skyrim Special Edition GOG",
                       "Skyrim VR", "Skyrim"]


def _ci_path(directory, name):
    """A case-insensitive file lookup. MO2 writes lower-case ini names on disk while every
    document spells them Skyrim.ini; on a case-sensitive filesystem the obvious spelling misses."""
    if not os.path.isdir(directory):
        return None
    want = name.lower()
    for f in os.listdir(directory):
        if f.lower() == want:
            return os.path.join(directory, f)
    return None


def parse_ini(path):
    """{section_lower: {key_lower: (raw_key, value)}}. Keys contain spaces ("iSize W"), so the
    split is on the first '=' only."""
    out, sec = {}, ""
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line or line[0] in ";#":
                continue
            if line[0] == "[" and line.endswith("]"):
                sec = line[1:-1].strip().lower()
                continue
            if "=" not in line:
                continue
            k, _, v = line.partition("=")
            out.setdefault(sec, {})[k.strip().lower()] = (k.strip(), v.strip())
    return out


def resolve_inis(instance, profile, game=None, my_games=None):
    """Which ini files this MO2 profile actually hands the game, in load order.

    MO2's per-profile `settings.ini` decides: `LocalSettings=true` means the profile's own copies
    are used, false means the ones under Documents / My Games. Getting this backwards reads a file
    the game never sees, which is worse than not reading one at all -- so the answer names every
    file it used and says WHY.
    """
    prof_dir = None
    pdir = os.path.join(instance, "profiles")
    if os.path.isdir(pdir):
        for d in os.listdir(pdir):
            if d.lower() == profile.lower():
                prof_dir = os.path.join(pdir, d)
                break
    if prof_dir is None:
        raise SystemExit(f"no profile directory: looked for {pdir}{os.sep}{profile}")

    note = []
    settings = _ci_path(prof_dir, "settings.ini")
    local = None
    if settings:
        s = parse_ini(settings)
        raw = (s.get("general") or {}).get("localsettings")
        if raw:
            local = raw[1].strip().lower() in ("true", "1", "yes")
    if local is None:
        note.append("this profile's settings.ini does not state LocalSettings; assuming GLOBAL "
                    "inis, which is a guess -- check it")
        local = False

    if local:
        base, why = prof_dir, "LocalSettings=true, so the profile's own inis are what the game reads"
    else:
        if my_games is None:
            docs = os.path.join(os.path.expanduser("~"), "Documents", "My Games")
            hint = [c for c in MY_GAMES_CANDIDATES
                    if game and c.lower().replace(" ", "") in os.path.basename(
                        os.path.normpath(game)).lower().replace(" ", "")]
            for c in hint + MY_GAMES_CANDIDATES:
                if os.path.isdir(os.path.join(docs, c)):
                    my_games = os.path.join(docs, c)
                    break
        if my_games is None:
            raise SystemExit("LocalSettings is off and no Documents/My Games/<game> folder was "
                             "found; pass --my-games")
        base, why = my_games, "LocalSettings=false, so the game reads the Documents copies"

    files = [q for q in (_ci_path(base, n) for n in INI_LOAD_ORDER) if q]
    if not files:
        raise SystemExit(f"no ini files found in {base}")
    return files, why, note


def ini_lookup(files, section, key):
    """Every file that defines this key, in INI_LOAD_ORDER. Ask `ini_winner` which one wins.

    This function deliberately does NOT pick a winner: the listing order is file discovery, and
    precedence is a separate measured fact (INI_PRECEDENCE). Reporting all the hits rather than
    only the winner is the point -- a value set in two files is exactly the case where a single
    answer would be confidently wrong.

    Measured 2026-09-09 on Skyrim SE 1.7.104, from the running game's own `Utility.GetINIFloat`:
    `fDefaultWorldFOV` 110 in `skyrimcustom.ini` beat 95 in `skyrim.ini`, and 100 in `skyrim.ini`
    beat 110 in `skyrimprefs.ini`. Two pairs, one key, one profile, one runtime -- enough to
    order the three files, not enough to generalise to other keys or a modded ini loader.
    """
    hits = []
    for f in files:
        d = parse_ini(f).get(section.lower(), {})
        if key.lower() in d:
            hits.append((f, d[key.lower()][1]))
    return hits


def vertical_fov(setting_deg, aspect=None):
    """The vertical angle the game renders. Fixed by the SETTING alone -- see the block above.

    `aspect` is accepted and ignored so that a caller reading this line cannot conclude the
    vertical angle depends on the output shape. It does not; the horizontal one does.
    """
    return 2.0 * math.degrees(math.atan(math.tan(math.radians(setting_deg) / 2.0)
                                        / FOV_BASE_ASPECT))


def horizontal_fov(setting_deg, aspect):
    """The horizontal angle the game renders at this output aspect. Equals the setting at 16:9."""
    v = vertical_fov(setting_deg)
    return 2.0 * math.degrees(math.atan(math.tan(math.radians(v) / 2.0) * aspect))


def game_frame(instance, profile, game=None, my_games=None, fov_setting=None):
    """Resolution, aspect and vertical field of view, read from the game's own configuration."""
    files, why, note = resolve_inis(instance, profile, game, my_games)
    w = ini_lookup(files, "Display", "iSize W")
    h = ini_lookup(files, "Display", "iSize H")
    fov = ini_lookup(files, "Display", "fDefaultWorldFOV")
    if not w or not h:
        raise SystemExit("no 'iSize W'/'iSize H' in any of:\n  " + "\n  ".join(files)
                         + "\nThe resolution is not guessable -- it decides the frame shape.")
    W = int(float(ini_winner(w)[1]))
    H = int(float(ini_winner(h)[1]))
    if fov_setting is not None:
        setting, src = float(fov_setting), "--fov-setting on the command line"
    elif fov:
        win = ini_winner(fov)          # by INI_PRECEDENCE, not by list position
        setting, src = float(win[1]), os.path.basename(win[0])
    else:
        setting = FOV_ENGINE_DEFAULT
        src = ("ENGINE DEFAULT %g (no fDefaultWorldFOV in any ini) -- %s"
               % (FOV_ENGINE_DEFAULT, FOV_ENGINE_DEFAULT_PROVENANCE))
        note = note + ["fDefaultWorldFOV is set in NO ini, so this is the engine's own default: "
                       + FOV_ENGINE_DEFAULT_PROVENANCE]
    return {"files": files, "why": why, "note": note, "width": W, "height": H,
            "aspect": W / H, "fov_setting": setting, "fov_source": src,
            "fov_vertical": vertical_fov(setting),
            "fov_horizontal": horizontal_fov(setting, W / H),
            "w_hits": w, "h_hits": h, "fov_hits": fov}


def print_frame(fr):
    print("--- the frame the game will render ---")
    print("  inis, in load order (" + fr["why"] + "):")
    for f in fr["files"]:
        print(f"    {f}")
    for n in fr["note"]:
        print(f"  !! {n}")
    for label, hits in (("iSize W/H", fr["w_hits"] + fr["h_hits"]), ("fDefaultWorldFOV", fr["fov_hits"])):
        if len(hits) > 2 if label == "iSize W/H" else len(hits) > 1:
            print(f"  !! {label} is set in more than one file; by measured precedence "
                  f"({' > '.join(INI_PRECEDENCE)}) the winner is marked:")
            win = ini_winner(hits)
            for f, v in hits:
                mark = "  <-- wins" if (f, v) == win else ""
                print(f"       {os.path.basename(f)} = {v}{mark}")
    print(f"  resolution   {fr['width']} x {fr['height']}   aspect {fr['aspect']:.4f}")
    print(f"  fov setting  {fr['fov_setting']:g}  ({fr['fov_source']})")
    print(f"  -> vertical   {fr['fov_vertical']:.2f} deg  (fixed by the setting alone)")
    print(f"  -> horizontal {fr['fov_horizontal']:.2f} deg  (the setting only at 16:9)")
    print(f"  plan with:   --fov {fr['fov_vertical']:.2f} --aspect {fr['width']}:{fr['height']}")


# --------------------------------------------------------------------------------------
# candidates and their verdicts
# --------------------------------------------------------------------------------------

class Subject:
    """One thing the camera must see. Two KINDS, and the difference decides what proves it.

    `space` (from `--subject`) is the original: a thing standing at its FEET, whose sightline is
    proved by a ray reaching AT LEAST as far as it. Geometry further out is fine -- the subject is
    somewhere in front of it.

    `surface` (from `--surface`) is a declared point ON an opaque face -- the corner of a step,
    the lip of a table, a point on a contact seam. Its sightline is proved by a ray that stops
    THERE. A ray that runs past it has not established the surface at all: either the point is not
    where the source said, or the face is alpha-blended and the depth probe cannot see it. Reading
    "further than the target" as "clear" is correct for a space subject and wrong for a surface
    one, which is the whole reason the kind is carried rather than inferred.
    """

    def __init__(self, spec, default_z, kind="space", extent=0.0):
        body, _, label = spec.partition("=")
        flag = "--subject" if kind == "space" else "--surface"
        try:
            parts = [float(v) for v in body.split(",")]
        except ValueError:
            raise SystemExit(f"{flag} wants X,Y[,Z][=label]; got {spec!r}")
        if len(parts) == 2:
            if kind == "surface":
                raise SystemExit(f"{flag} wants X,Y,Z[=label] -- a surface point has no default "
                                 f"height, the height is the point of it; got {spec!r}")
            parts.append(default_z)
        if len(parts) != 3:
            raise SystemExit(f"{flag} wants X,Y[,Z][=label]; got {spec!r}")
        self.x, self.y, self.z = parts
        self.kind, self.extent = kind, extent
        self.label = label or f"{self.x:.0f},{self.y:.0f}"
        self.cl: Clearance | None = None

    def pos(self):
        return (self.x, self.y, self.z)

    def aim_point(self, args):
        """The point the sightline probe shoots at: a surface point is the target ITSELF."""
        if self.kind == "surface":
            return (self.x, self.y, self.z)
        return (self.x, self.y, self.z + args.sight_height)

    def frame_offsets(self, args):
        """(dz, dx, dy) offsets that must all be inside the frame.

        A space subject is a standing figure -- feet to `--subject-height`, widened by
        `--subject-width`. A surface target is a POINT, widened only by `--surface-extent`: the
        context the caller asked to keep around it. Modelling a declared point as a 128 u figure
        would push the camera back for a body that is not there.
        """
        if self.kind == "surface":
            e = self.extent
            if e <= 0.0:
                return [(0.0, 0.0, 0.0)]
            return [(dz, dx, dy) for dz in (-e, 0.0, e)
                    for dx, dy in ((0, 0), (e, 0), (-e, 0), (0, e), (0, -e))]
        return [(dz, dx, dy) for dz in (0.0, args.subject_height)
                for dx, dy in ((0, 0), (args.subject_width, 0), (-args.subject_width, 0),
                               (0, args.subject_width), (0, -args.subject_width))]

    def frame_centre(self, args):
        """The height the framing report treats as this target's middle."""
        if self.kind == "surface":
            return self.z
        return self.z + args.subject_height / 2.0


def resolve_floor(subjects, args):
    """Which storey are we planning on?

    A SPACE subject stands on the floor, so the lowest one gives it. A SURFACE point does not --
    its Z is a feature height, which is the entire reason the kind exists. Taking a lintel at
    z 200 for the floor puts the probe eye at 320 and aims the camera at 300, and the result is
    a plausible pose of the ceiling. So when nothing stands on the floor, this REFUSES and names
    the flag rather than inventing a storey.
    """
    if args.floor_z is not None:
        return float(args.floor_z)
    standing = [s.z for s in subjects if s.kind == "space"]
    if standing:
        return min(standing)
    raise SystemExit(
        "--floor-z is required when every target is a --surface point: a surface point's Z is "
        "the height of a FEATURE, not of a storey, and the eye height, the placement test and "
        "the default aim are all measured from the floor. Give --floor-z for the storey the "
        "camera stands on.")


def resolve_aim(subjects, floor_z, args):
    """Where the camera looks by default.

    Space subjects are aimed at `--aim-height` above their centroid -- a standing head. With only
    surface points there is no body to aim at: the default becomes the centroid of the declared
    points themselves, at their own mean height, because that is the feature the caller asked to
    expose. `--aim` overrides either.
    """
    if args.aim:
        return tuple(float(v) for v in args.aim.split(","))
    standing = [s for s in subjects if s.kind == "space"]
    pool = standing or subjects
    cx = sum(s.x for s in pool) / len(pool)
    cy = sum(s.y for s in pool) / len(pool)
    if standing:
        return (cx, cy, floor_z + args.aim_height)
    return (cx, cy, sum(s.z for s in pool) / len(pool))


def nav_verdict(entry, floor_z, args):
    """Is a camera at this XY standing in the room, or under the floor above it?

    Three outcomes, and the middle one is the one that matters. A camera point is often NOT on
    navmesh -- navmesh is cut out around furniture, and the known-good vantage of 2026-08-31
    stands on a bed footprint with its nearest triangle 80 u away. So "no navmesh here" cannot
    be a refusal on its own. What IS a refusal is navmesh present only at a storey far from the
    camera's own floor: that says the point belongs to a different level of the building.
    """
    if entry is None:
        return "unknown", "no --nav-at answer for this point"
    near = [z for z in entry["levels"] if abs(z - floor_z) <= args.storey_tol]
    if near:
        return "on-storey", f"walkable at z {near[0]:.0f} ({near[0]-floor_z:+.0f} from the floor)"
    if entry["levels"]:
        zs = ", ".join(f"{z:.0f}" for z in sorted(entry["levels"]))
        return "wrong-storey", (f"the only walkable storey(s) here are z {zs}, "
                                f"none within {args.storey_tol:.0f} u of the floor z {floor_z:.0f} "
                                f"-- this point is on another level, not in the room")
    n = entry["nearest"]
    if n is not None and n <= args.offmesh_tol:
        return "off-mesh-near", (f"no navmesh covers it, nearest triangle {n:.0f} u away "
                                 f"-- a furniture footprint inside the room")
    return "no-floor", (f"no navmesh covers it and the nearest triangle is "
                        f"{'unknown' if n is None else f'{n:.0f} u'} away "
                        f"(tolerance {args.offmesh_tol:.0f} u)")


def support_verdict(entry, eye, floor_z, args):
    """Is there real placed geometry UNDER this eye, on this eye's own storey?

    `nav_verdict`'s `off-mesh-near` arm used to answer this by assertion: within --offmesh-tol of
    a navmesh triangle it called the point "a furniture footprint inside the room" and passed it.
    Distance to navmesh cannot say that. Three Ratway cameras the planner fully ACCEPTED on that
    arm (2026-09-14) have NO placed geometry at all above or below them -- one of them finds
    nothing within 2,048 u in either direction -- because they stand in the void OUTSIDE the
    corridor shell, where a back-face-culled kit wall lets them render the room they are not in. An absent OBND box
    said nothing either way: a point in the void is enclosed by nothing, exactly like a point in
    the middle of the room.

    So the question is asked of geometry, through `--clear-at`'s downward ray:

      supported    a placed mesh is below the eye and its surface sits within --storey-tol of the
                   declared floor -- which is the furniture case the off-mesh allowance exists for
                   (a camera over a bed reads its physical floor a few tens of units down).
      contradicted nothing is below it within the probe's reach, or the only thing below belongs to
                   another storey, AND the broad phase saw every candidate that could have been
                   there. Absence is a finding only when coverage was complete.
      unresolved   the probe could not see enough to say: refs skipped for want of a usable OBND,
                   meshes it could not read, or a reach too short to cover the storey band. NOT a
                   pass -- an eye whose placement is unknown must not publish a camera.

    Note what this does NOT claim. It is a statement about STATIC PLACED GEOMETRY in the rendered
    scene, not about player reachability, actor collision or havok: a floor under the eye does not
    make the spot walkable, and this function never says it does.
    """
    if entry is None:
        return "unknown", "no --clear-at answer for this point"
    band = args.storey_tol
    reach = entry["reach"]
    # Coverage the absence would have to survive to mean anything.
    blind = []
    if entry["no_box"]:
        blind.append(f"{entry['no_box']} physical ref(s) carry no usable OBND, so the broad phase "
                     "never offered them to the ray test")
    if entry["unread"]:
        blind.append(f"{entry['unread']} candidate mesh(es) could not be read")
    # A ref the broad phase could only select by its RECORD box is a ref whose real extent nobody
    # read. That box can be present and wrong -- undersized, or offset from the mesh it claims to
    # bound -- and a wrong box drops the ref without leaving a trace, which is why the missing-box
    # count alone never established an absence (Piranha, 2026-09-14).
    if entry.get("obnd_only"):
        blind.append(f"{entry['obnd_only']} ref(s) could be selected only by their record OBND, "
                     "whose agreement with the mesh nobody verified")
    # Outdoors the ground is a LAND heightmap, not a placed ref. --clear-at reads it for the two
    # vertical rays, but only when the run BUILT it -- and a run that did not (`--no-terrain`, or
    # no LAND record at that XY) has not looked at the thing an exterior camera is standing on.
    # That is a blind spot, not an empty floor: calling it CONTRADICTED would refuse every
    # exterior vantage on a missing input, which is the same silent wrong answer one level up.
    # An interior is the one case where absent terrain is expected and says nothing.
    terrain = entry.get("terrain") or ""
    if "NOT BUILT" in terrain:
        blind.append("the run built no terrain, so the ground itself was never tested "
                     "(--no-terrain, or no LAND data loaded)")
    elif "NO heightmap" in terrain:
        blind.append("no LAND heightmap covers this XY, so the ground itself was never tested")
    down = entry["axes"].get("down")
    if down is None:
        return "unresolved", "the --clear-at answer carries no downward ray for this point"
    # The reach has to cover the whole storey band below the eye, or "nothing below" is a statement
    # about the reach rather than about the building.
    need = (eye[2] - floor_z) + band
    if down["dist"] is None and reach < need:
        return "unresolved", (f"nothing below it within the probe's {reach:.0f} u reach, but the "
                              f"storey band under this eye runs to {need:.0f} u -- the reach, not "
                              "the geometry, is what ran out")
    if down["dist"] is None:
        if blind:
            return "unresolved", (f"nothing found below it within {reach:.0f} u, but coverage was "
                                  "incomplete: " + "; ".join(blind))
        return "contradicted", (f"NO geometry or ground below it within {reach:.0f} u -- nothing "
                                f"holds this eye up on any storey ({entry['candidates']} candidate(s) "
                                f"tested on their own mesh bounds, all with readable meshes, "
                                f"{entry.get('obnd_would_miss', 0)} of them a ref the record box "
                                f"alone would have excluded; {terrain})")
    surface = eye[2] - down["dist"]
    off = surface - floor_z
    who = down["edid"] or down["formkey"]
    kind = "terrain" if down["formkey"] == "000000:TERRAIN" else "placed floor"
    if abs(off) <= band:
        return "supported", (f"{kind} {down['dist']:.0f} u below it -- `{who}` at z "
                             f"{surface:.0f} ({off:+.0f} from the declared floor z {floor_z:.0f})")
    if blind:
        return "unresolved", (f"the nearest placed surface below it is `{who}` at z {surface:.0f}, "
                              f"{off:+.0f} from the floor z {floor_z:.0f} and outside the "
                              f"{band:.0f} u storey band -- but coverage was incomplete: "
                              + "; ".join(blind))
    return "contradicted", (f"the only placed surface below it is `{who}` at z {surface:.0f}, "
                            f"{off:+.0f} from the declared floor z {floor_z:.0f} and outside the "
                            f"{band:.0f} u storey band -- that floor belongs to another level")


def frame_check(eye, yaw, pitch, fov, subjects, args):
    """Is every subject inside the frame, and how much of it do they fill?

    Exact rectilinear test -- |x/z| and |y/z| against tan(fov/2) -- on each subject modelled as
    a vertical segment from its feet to `--subject-height`, widened by `--subject-width`. The
    0.637 x distance rule of thumb is this same arithmetic at fov 65; the tangents are used here
    because a planner should not carry a constant that is only true at one field of view.

    `fov` is the VERTICAL angle and `--aspect` widens the horizontal limit. The default is
    square because viewprobe's frame is square -- but a game camera is not, and a shot planned
    against a square frame can lose a subject's head off the top of a 16:9 one. Plan against the
    frame you are actually going to take.
    """
    f, r, u = camera_basis(yaw, pitch)
    t = math.tan(math.radians(fov) / 2.0)
    th = t * getattr(args, "aspect", 1.0)
    worst_h = worst_v = 0.0
    for s in subjects:
        for dz, wx, wy in s.frame_offsets(args):
            d = (s.x + wx - eye[0], s.y + wy - eye[1], s.z + dz - eye[2])
            zf = d[0] * f[0] + d[1] * f[1] + d[2] * f[2]
            if zf <= 1.0:
                return False, 9.9, f"{s.label} is behind the camera", 0.5
            xr = (d[0] * r[0] + d[1] * r[1] + d[2] * r[2]) / (zf * th)
            yu = (d[0] * u[0] + d[1] * u[1] + d[2] * u[2]) / (zf * t)
            worst_h, worst_v = max(worst_h, abs(xr)), max(worst_v, abs(yu))
    fill = max(worst_h, worst_v)
    # Where the subjects sit vertically, 0 = top edge, 1 = bottom edge. Containment says they
    # are in the picture; this says whether they are in the MIDDLE of it.
    ctr = []
    for s in subjects:
        d = (s.x - eye[0], s.y - eye[1], s.frame_centre(args) - eye[2])
        zf = d[0] * f[0] + d[1] * f[1] + d[2] * f[2]
        yu = (d[0] * u[0] + d[1] * u[1] + d[2] * u[2]) / (zf * t)
        ctr.append(0.5 - yu / 2.0)
    v_centre = sum(ctr) / len(ctr)
    if fill > 1.0 - args.frame_pad:
        return False, fill, (f"a subject sits at {fill:.2f} of the half-frame, past the "
                             f"{1-args.frame_pad:.2f} limit -- widen --fov or back the camera off"), v_centre
    return True, fill, None, v_centre


def inside_verdict(entry, args):
    """(kind, fails, notes) for an eye against the refs whose OBND boxes enclose it.

    The box only NOMINATES. A tower-kit wall piece's box (966x966x384 u) encloses every camera in
    the room it walls, exactly as a bookcase's box encloses a camera on its shelf (both measured
    2026-09-03), so a box alone cannot refuse. viewprobe fires six world-axis rays from the eye
    against the nominated ref's OWN mesh: a shelf above and below and a back panel behind all hit
    within arm's reach, a room's walls do not. Geometry within --inside-reach on at least
    --inside-rays of the six axes is ENCLOSED -- a refusal naming the ref. A ref whose mesh could
    not be read falls back to the box with --inside-cap, and says so: a weaker answer, stated.

    A mesh too big to read used to be waved through as "a room shell". That was an assumption of
    exactly the kind this planner's placement arm was corrected for: the box was the only evidence,
    and a box cannot tell a shell from a solid. It is now UNRESOLVED -- the eye was not tested, the
    camera does not publish, and finding support underneath does not make it unobstructed
    (Piranha, 2026-09-14). Three outcomes, not two: `inside` is a positive refusal naming the ref,
    `unresolved` is an untested eye, and only `clear` is a pass.
    """
    if entry is None:
        return "unknown", [], ["inside: no --inside-at answer for this point"]
    fails, notes, untested = [], [], []

    def fmt(rays):
        return " ".join(f"{k} {'-' if rays.get(k) is None else f'{rays[k]:.0f}'}" for k in RAY_AXES)

    for h in entry:
        name = h["edid"] or h["formkey"]
        dims = f"{h['dims'][0]:.0f}x{h['dims'][1]:.0f}x{h['dims'][2]:.0f} u"
        rays = h.get("rays")
        if rays is not None:
            close = [k for k in RAY_AXES if rays.get(k) is not None and rays[k] <= args.inside_reach]
            if len(close) >= args.inside_rays:
                fails.append(f"inside: eye ENCLOSED by `{name}` ({h['type']}): its geometry within "
                             f"{args.inside_reach:.0f} u on {len(close)} of 6 axes ({fmt(rays)})")
            else:
                notes.append(f"inside: within `{name}`'s box ({dims}) but open -- {fmt(rays)}")
        elif h["maxdim"] <= args.inside_cap:
            fails.append(f"inside: eye inside `{name}`'s box ({h['type']}, {dims}) and its mesh "
                         "could not be read to test further -- the box stands")
        else:
            untested.append(f"inside: within room-scale box `{name}` ({h['type']}, {dims}) and its "
                            f"mesh could not be read -- over --inside-cap {args.inside_cap:.0f}. "
                            "Whether the eye is INSIDE that ref or in the room it walls is UNTESTED: "
                            "the box was the only evidence and a box cannot tell a shell from a "
                            "solid. Support under the eye does not make it unobstructed")
    # THE NOMINATION'S OWN COVERAGE. The rows above are refs the query could test. What decides
    # whether their ABSENCE means anything is how many refs it could not: one whose mesh would not
    # read was never ruled out, and one selectable only by an unverified record box was selected on
    # evidence nobody checked. An empty nomination over either population is UNRESOLVED, not clear
    # -- the exact step that let a camera publish from "no physical ref's box encloses the eye"
    # (Piranha, 2026-09-14).
    cover = getattr(entry, "coverage", None) or {}
    # WHAT MAKES AN ANSWER USABLE. Clearance is a positive claim -- "the refs that could enclose
    # this eye were nominated from their own meshes and none does" -- and it can only be made by a
    # run that SAID so. A missing statement is not a statement of zero gaps: it is an older
    # renderer, a different arm, or output nobody can attribute, and reading it as full coverage is
    # how a camera gets qualified by evidence that was never produced (Piranha, 2026-09-14).
    source = cover.get("source")
    if source == "mesh":
        pass
    elif source == "obnd-legacy":
        untested.append("inside: this answer comes from the `--inside-bounds obnd` COMPARISON arm, "
                        "which reproduces the old record-box nomination on purpose. It measures the "
                        "legacy behaviour and cannot qualify a camera")
    else:
        untested.append("inside: the nomination carries NO mesh-coverage statement -- this renderer "
                        "output does not say which refs were nominated from their own meshes, or "
                        "how many it could not read. Absent coverage is not zero gaps")
    gaps = []
    if cover.get("unreadable"):
        gaps.append(f"{cover['unreadable']} physical ref(s) whose mesh could not be read at all, so "
                    "they were never ruled out")
    if cover.get("obnd_only"):
        gaps.append(f"{cover['obnd_only']} ref(s) selectable only by their record OBND, whose "
                    "agreement with the mesh nobody verified")
    if not entry:
        if gaps:
            untested.append("inside: no ref's geometry encloses the eye, but the nomination was "
                            "INCOMPLETE -- " + "; ".join(gaps) + ". An absence over an incomplete "
                            "nomination is not clearance")
        elif source == "mesh":
            notes.append("inside: no physical ref's geometry encloses the eye; every physical ref "
                         "in the cell was nominated from its own mesh bounds and tested")
    elif gaps:
        untested.append("inside: the refs that WERE tested read open, but the nomination was "
                        "INCOMPLETE -- " + "; ".join(gaps))
    kind = "inside" if fails else ("unresolved" if untested else "clear")
    return kind, fails + untested, notes


def evaluate(eye, subjects, floor_z, nav_entry, args, aim, skip_nav=False, inside_entry=None,
             clear_entry=None):
    """Every reason this camera could be wrong, collected rather than short-circuited.

    A planner that stops at the first failure teaches nothing when it refuses. The caller prints
    the whole list, which is what makes a refusal actionable.
    """
    yaw = bearing_deg(aim[0] - eye[0], aim[1] - eye[1])
    dxy = math.hypot(aim[0] - eye[0], aim[1] - eye[1])
    pitch = math.degrees(math.atan2(eye[2] - aim[2], dxy)) if dxy > 1 else 0.0
    v = {"eye": eye, "yaw": yaw, "pitch": pitch, "fail": [], "note": [], "sight": {}}

    rank_penalty = 0.0
    for s in subjects:
        d = math.hypot(eye[0] - s.x, eye[1] - s.y)
        b = bearing_deg(eye[0] - s.x, eye[1] - s.y)
        if s.cl is None:
            v["note"].append(f"{s.label}: no clearance map (visibility unproven from the map)")
            continue
        c, why = s.cl.at(b)
        v["sight"][s.label] = {"bearing": b, "need": d, "clear": c}
        # A clearance map is built at EYE HEIGHT around the target's XY. For a space subject --
        # a figure standing at that XY, spanning the eye's own height band -- that is the right
        # ray and it REFUSES on it. For a declared surface point at some other height it is not:
        # a step's lip can be hidden behind a chest the eye-height ray flies over, and a lintel
        # can be clear while the eye-height ray hits the wall under it. Measured on both sides in
        # this tool's offline cases. So for a surface target the map RANKS -- it moves the score,
        # never the verdict -- and the aimed in-pose probe is what decides.
        soft = s.kind == "surface"
        if c is None:
            msg = f"{s.label}: {why} (bearing {b:.0f} deg)"
        elif c <= d + args.sight_margin:
            msg = (f"{s.label}: geometry at {c:.0f} u blocks the {d:.0f} u sightline "
                   f"on bearing {b:.0f} deg (needs {args.sight_margin:.0f} u of margin)")
        else:
            v["note"].append(f"{s.label}: clear -- first geometry {c:.0f} u along a {d:.0f} u ray"
                             + (" (eye-height map: RANKING only for a surface target)" if soft else ""))
            continue
        if soft:
            rank_penalty += 30.0
            v["note"].append(msg + " -- eye-height map only, which cannot refuse a surface target "
                                   "at another height; ranked down, decided by the aimed probe")
        else:
            v["fail"].append(msg)

    ok, fill, why, v_centre = frame_check(eye, yaw, pitch, args.fov, subjects, args)
    v["fill"], v["v_centre"] = fill, v_centre
    if not ok:
        v["fail"].append(f"framing: {why}")

    kind, msg = ("skipped", "not probed yet") if skip_nav else \
        nav_verdict(nav_entry, floor_z, args)
    v["nav"] = kind
    # "unknown" is a MISSING answer, not a passing one. A point the probe never reported on has
    # not been placement-tested, and letting it through would be exactly the silent degradation
    # the aimed-verification fix above exists to stop -- the same bug one query lower down.
    if kind in ("wrong-storey", "no-floor", "unknown"):
        v["fail"].append(f"placement: {msg}")
    else:
        v["note"].append(f"placement: {msg}")
    # The off-mesh allowance is the one arm that passes a point NO navmesh covers, on the strength
    # of a distance. Distance cannot tell a furniture footprint in the room from the void outside
    # the shell, so that arm now has to be paid for in geometry: the eye needs a real placed floor
    # under it, on its own storey. on-storey points already have their walkable surface, and the
    # two failing arms have already failed -- the support answer is reported for all of them so a
    # refusal says what is really underneath, but it only DECIDES here.
    if skip_nav:
        v["support"] = "skipped"
    else:
        sup, smsg = support_verdict(clear_entry, eye, floor_z, args)
        v["support"] = sup
        if kind == "off-mesh-near" and sup != "supported":
            v["fail"].append(f"placement: {msg}; and the geometry does not bear that out -- {smsg}")
        else:
            v["note"].append(f"support: {smsg}")
    if skip_nav:
        v["inside"] = "skipped"
    else:
        v["inside"], ifails, inotes = inside_verdict(inside_entry, args)
        if v["inside"] == "unknown":
            ifails = ifails + ["inside: no --inside-at answer for this point -- the eye was NOT "
                               "tested against the refs whose box encloses it"]
        v["fail"] += ifails
        v["note"] += inotes

    margins = [x["clear"] - x["need"] for x in v["sight"].values() if x["clear"] is not None]
    v["wall_margin"] = min(margins) if margins else None
    v["ok"] = not v["fail"]
    v["score"] = (0.0 if not v["ok"] else
                  100.0
                  - 70.0 * abs(fill - args.fill)
                  - 25.0 * (1.0 - min(1.0, (v["wall_margin"] or 0) / args.comfort))
                  + (8.0 if kind == "on-storey" else 0.0)
                  - rank_penalty)
    return v


# --------------------------------------------------------------------------------------
# the final check: the pose that will be delivered, probed as itself
# --------------------------------------------------------------------------------------
#
# The check that decides the answer has to be taken THROUGH THE DELIVERED POSE. The older
# per-subject confirmation re-aimed the camera at each subject in turn (`--look SUBJECT`), which
# answers "could a camera at this point see it" and NOT "does the camera we are about to hand
# over see it". Those differ whenever the pose is aimed anywhere but straight at that one target,
# which is every multi-target shot and every shot with an --aim. So: one render at the emitted
# eye/yaw/pitch, and one `--pick` at the pixel each target actually lands on.


POSE_DECIMALS = 1  # every pose this tool emits is formatted "%.1f"; see canonical_pose


def canonical_pose(eye, yaw, pitch):
    """The pose EXACTLY as it will be submitted and exported -- settled once, before use.

    Every consumer of a pose here formats it to one decimal: the `--eye/--yaw/--pitch` flags, the
    printed block, the DevBench conversion, the worklist, the identity digest. Projecting a target
    pixel from the UNROUNDED angles while submitting the rounded ones samples a slightly different
    ray than the one the renderer takes, and near a surface edge that lands on the other side of
    it: a declared point 0.15 u inside a panel's edge came back `not-surface` against a background
    wall 550 u away, while the pose actually submitted confirms it -- same exported pose, same
    pose id, opposite verdicts (reviewed 2026-09-14).

    A tolerance cannot repair that, because the error is WHICH SURFACE was sampled, not how far
    along the ray. So the pose is canonicalised ONCE here and every step downstream -- projection,
    containment, target distance, identity, command generation -- uses these numbers and no
    others. The rounding term in `surface_tolerance` is retained as a floor for callers that hand
    in a pose from elsewhere; on this path its residual is now zero by construction.
    """
    return ((round(eye[0], POSE_DECIMALS), round(eye[1], POSE_DECIMALS),
             round(eye[2], POSE_DECIMALS)),
            round(yaw, POSE_DECIMALS), round(pitch, POSE_DECIMALS))


def surface_tolerance(dist, probe_fov, size, args):
    """How far from a declared surface point a depth hit may land and still BE that surface.

    Derived from the instrument, not copied from a scene. Four terms:

      abs    viewprobe prints hit position and distance as WHOLE UNITS, and a declared point
             carried out of a record or a dump is itself rounded. --surface-tol (default 2 u).
      pixel  a pick samples the ray through a PIXEL CENTRE, which misses the exact direction to
             the target by up to half a pixel: fov/size/2 in angle.
      pose   the emitted pose is rounded to 0.1 deg of yaw and pitch, so the ray the runner
             actually takes is up to 0.05 deg off the one planned.
      slant  a lateral miss of L on a face tilted t from perpendicular reads as a RANGE error of
             L*tan(t). --surface-obliquity is tan(t) (default 1.73 = 60 deg of tilt).

    so   tol(d) = surface_tol + d * (half_pixel_rad + pose_round_rad) * obliquity.

    At d 250, size 320, fov 65 that is about 3 u -- which is why the counterexamples this is
    tested against (a blocker tens of units short, a background hit hundreds of units beyond)
    are nowhere near the boundary. A tolerance this small is NOT a claim that the planner can
    resolve a 3 u feature; it is the width of the band in which "the ray stopped here" and "the
    ray stopped at the declared point" cannot be told apart by this instrument.
    """
    half_pixel = math.radians(probe_fov / max(size, 1) / 2.0)
    pose_round = math.radians(0.05)
    return (args.surface_tol
            + abs(dist) * (half_pixel + pose_round) * args.surface_obliquity)


# What viewprobe's fingerprint is a hash OF -- copied from the definition, not from a hope.
# `_shared/Configuration.cs::Compute` hashes a canonical text of: the loaded plugins (name +
# content sha256, IN ORDER), the asset providers (kind + name, in priority), the BOS ini overlays
# (kind + sha256), the population, the state and the schema version. It does NOT hash mesh or
# material bytes, the archives' contents, or the renderer binary -- so two runs with the same
# fingerprint read the same RECORDS and the same provider list, which is not the same claim as
# "drew the same pixels with the same build". Say the narrow thing.
SCENE_FINGERPRINT_COVERS = ["plugins: name + content sha256, in load order",
                            "asset providers: kind + name, in priority order",
                            "BOS ini overlays: kind + content sha256",
                            "population, state, schema version"]
SCENE_FINGERPRINT_SCOPE = (
    "viewprobe's own capture-input-configuration fingerprint (_shared/Configuration.cs): a hash "
    "of the plugin set/order and their content, the provider list, the BOS overlays, the "
    "population and the state. NOT mesh/material bytes, archive contents or the renderer build")


def scene_identity(run, args, fingerprint=None):
    """What SCENE the camera was judged against -- declared inputs, and the renderer's own word.

    Two halves, kept apart because they are worth different amounts:

      declared   the target flags the caller passed through (instance, profile, game, order) and
                 the viewprobe command itself. This is a DECLARATION. A profile NAME does not
                 prove the bytes behind it, and this field must never be read as if it did.
      renderer   the `capture input configuration:` fingerprint viewprobe printed for THE RENDER
                 THIS SCENE DESCRIBES -- passed in by the caller that has that render in hand,
                 never read from shared state. This one IS about bytes, within the scope in
                 SCENE_FINGERPRINT_COVERS.

    Without this, the same pose over two different profiles produced the SAME id, so a
    verification could not be shown to belong to the scene it was run against (reviewed
    2026-09-14).

    `fingerprint=None` means the render in hand reported none, and the result says ABSENT. It is
    the default because absence is the safe answer: a caller that does not have a render's own
    identity must not be able to inherit one by omission.
    """
    # instance/profile/game may be given as flags OR passed through after `--` to viewprobe;
    # resolved the same way `--from-profile` resolves them, so the recorded value is the one the
    # run actually used rather than null beside a passthrough list that holds it.
    declared = {"vp": list(run.base), "cell": run.cell,
                "passthrough": list(getattr(args, "passthrough", []) or []),
                "instance": getattr(args, "instance", None) or _passthrough_value(args, "--instance"),
                "profile": getattr(args, "profile", None) or _passthrough_value(args, "--profile"),
                "game": getattr(args, "game", None) or _passthrough_value(args, "--game")}
    return {"declared": declared,
            "declaredIs": "caller-declared inputs, NOT proof of bytes: `vp` is a command path, "
                          "not a renderer build hash, and a profile NAME is not its contents",
            "renderer": fingerprint,
            "rendererIs": (SCENE_FINGERPRINT_SCOPE if fingerprint else
                           "ABSENT -- the renderer reported no configuration identity for this "
                           "run (it predates --config-identity, was asked not to with "
                           "--no-bind-scene, or refused to resolve one); the binding here is "
                           "declaration-only and proves nothing about bytes"),
            "rendererCovers": SCENE_FINGERPRINT_COVERS if fingerprint else None}


def pose_identity(cell, eye, yaw, pitch, fov, aspect, size, subjects, args, scene=None):
    """A short digest over the pose, the inputs that produced it AND the scene it ran against.

    A verification result is only about the thing it was run on. Every artifact this tool writes
    carries this id, so a saved camera, a saved verification and a saved worklist can be checked
    to be the SAME candidate against the SAME scene -- the failure the 2026-09-14 review found in
    a hand-built packet, where the recommended camera and the saved camera were different poses
    and nothing in the files said so.

    `eye/yaw/pitch` are expected CANONICAL (see canonical_pose); they are rounded again here so a
    caller that forgets still gets the id of the pose that would actually be submitted.
    """
    import hashlib
    payload = {
        "cell": cell,
        "eye": [round(v, POSE_DECIMALS) for v in eye],
        "yaw": round(yaw, POSE_DECIMALS), "pitch": round(pitch, POSE_DECIMALS),
        "fov": round(fov, 4), "aspect": round(aspect, 6), "size": int(size),
        "targets": sorted((s.kind, s.label, round(s.x, 2), round(s.y, 2), round(s.z, 2),
                           round(s.extent, 2)) for s in subjects),
        "policy": {k: getattr(args, k, None) for k in
                   ("sight_margin", "sight_height", "surface_tol", "surface_obliquity",
                    "frame_pad", "eye_height")},
        "scene": scene,
    }
    blob = json.dumps(payload, sort_keys=True, default=str).encode("utf-8")
    return hashlib.sha256(blob).hexdigest()[:16]


def target_pixels(eye, yaw, pitch, subjects, args, size):
    """Where each target lands, and the probe fov needed to reach it.

    Returns (rows, probe_fov). Each row has the target's angular offsets from the axis, whether
    it is inside the DELIVERABLE frame (--fov by --aspect), and -- once the probe fov is settled
    -- the pixel to --pick. The two frames are different objects and are reported separately: the
    probe's canvas is square, and a 16:9 deliverable is WIDER, so a target near the left edge of
    the delivered picture can sit outside a square probe frame at the same --fov. Widening the
    PROBE frustum does not move the camera and does not change what the delivered frame contains.
    """
    f, r, u = camera_basis(yaw, pitch)
    rows = []
    t_v = math.tan(math.radians(args.fov) / 2.0)
    t_h = t_v * getattr(args, "aspect", 1.0)
    for s in subjects:
        p = s.aim_point(args)
        d = (p[0] - eye[0], p[1] - eye[1], p[2] - eye[2])
        zf = d[0] * f[0] + d[1] * f[1] + d[2] * f[2]
        xr = d[0] * r[0] + d[1] * r[1] + d[2] * r[2]
        yu = d[0] * u[0] + d[1] * u[1] + d[2] * u[2]
        row = {"label": s.label, "kind": s.kind, "point": p,
               "dist": math.dist(eye, p), "behind": zf <= 1.0}
        if row["behind"]:
            row.update({"h_off": None, "v_off": None, "in_frame": False,
                        "frame_xy": None, "pixel": None})
        else:
            row["h_off"] = math.degrees(math.atan2(xr, zf))
            row["v_off"] = math.degrees(math.atan2(yu, zf))
            # normalized position in the DELIVERED frame: |x| and |y| <= 1 is inside it
            fx, fy = xr / (zf * t_h), yu / (zf * t_v)
            row["frame_xy"] = (fx, fy)
            row["in_frame"] = abs(fx) <= 1.0 and abs(fy) <= 1.0
        rows.append(row)
    need = max((max(abs(r_["h_off"]), abs(r_["v_off"]))
                for r_ in rows if r_["h_off"] is not None), default=0.0)
    # A little headroom so the target is not sampled by the outermost pixel, where half a pixel
    # of angle is the largest fraction of the remaining margin.
    probe_fov = args.fov
    if need * 2.0 + 4.0 > probe_fov:
        probe_fov = min(args.verify_fov_max, need * 2.0 + 4.0)
    for row in rows:
        row["probe_fov"] = probe_fov
        row["pixel"] = (None if row["h_off"] is None else
                        pixel_for_angles(row["h_off"], row["v_off"], probe_fov, size))
    return rows, probe_fov


def verify_in_pose(run, cell, eye, yaw, pitch, subjects, args):
    """Probe the DELIVERED pose once, and judge every target in it.

    Per-target status, and what each one means:

      clear         a space subject: the first depth-writing geometry on its ray is at least as
                    far as the subject, so nothing stands between them
      confirmed     a surface target: the ray STOPPED at the declared point, within tolerance
      blocked       geometry substantially nearer than the target -- something is in the way
      not-surface   a surface target whose ray ran substantially PAST it. NOT a success: either
                    the point is not where it was declared, or its face is alpha-blended and
                    the depth probe cannot see it. Unproven, with the reason stated.
      unproven      no depth-writing geometry on the ray at all (background, or alpha)
      off-probe     the target is outside the probe canvas even widened -- not judged
      behind        the target is behind the camera

    The overall status is `verified` only when EVERY target is clear or confirmed.
    """
    # Settle the pose FIRST. Everything below -- the projection, the flags, the distances, the
    # identity and the exported pose -- reads these numbers, so the ray that is projected is the
    # ray that is submitted. See canonical_pose for the false refusal this prevents.
    eye, yaw, pitch = canonical_pose(eye, yaw, pitch)
    rows, probe_fov = target_pixels(eye, yaw, pitch, subjects, args, run.size)
    flags = ["--eye", f"{eye[0]:.1f},{eye[1]:.1f},{eye[2]:.1f}",
             "--yaw", f"{yaw:.1f}", "--pitch", f"{pitch:.1f}", "--fov", f"{probe_fov:g}"]
    # Ask the renderer what configuration it is about to read. This is THE render whose answers
    # decide the verdict, so it is the one that has to be bound to a scene -- and asking here
    # rather than on every probe pays the resolve once per attempt. A viewprobe that predates
    # the flag ignores it and
    # the binding stays declaration-only, which `scene_identity` reports rather than hides.
    if not getattr(args, "no_bind_scene", False):
        flags.append("--config-identity")
    picked = [r_ for r_ in rows if r_["pixel"] is not None]
    for r_ in picked:
        flags += ["--pick", f"{r_['pixel'][0]},{r_['pixel'][1]}"]
    # The probe object, not just its picks: the verification's scene identity comes from THIS
    # render. When nothing was picked there is no render at all, and the identity is absent --
    # which is the honest answer, not an invitation to borrow the last one.
    probe = run.probe(*flags) if picked else None
    got = probe.picks() if probe is not None else {}

    results, bad = [], []
    for r_ in rows:
        d, tol = r_["dist"], surface_tolerance(r_["dist"], probe_fov, run.size, args)
        res = {"label": r_["label"], "kind": r_["kind"], "want": round(d, 1),
               "point": [round(v, 1) for v in r_["point"]], "pixel": r_["pixel"],
               "probeFov": round(probe_fov, 2),
               "hOff": None if r_["h_off"] is None else round(r_["h_off"], 3),
               "vOff": None if r_["v_off"] is None else round(r_["v_off"], 3),
               "inDeliveredFrame": r_["in_frame"], "tolerance": round(tol, 2)}
        if r_["behind"]:
            res.update(status="behind", why="the target is behind the camera")
        elif r_["pixel"] is None:
            res.update(status="off-probe", why=(
                f"the target is {max(abs(r_['h_off']), abs(r_['v_off'])):.1f} deg off the axis and "
                f"falls outside a {probe_fov:.0f} deg probe canvas (--verify-fov-max "
                f"{args.verify_fov_max:.0f}) -- NOT judged"))
        else:
            hit = got.get(r_["pixel"])
            if hit is None:
                res.update(status="unproven", hit=None, why=(
                    "the ray found no depth-writing geometry. An alpha-blended surface -- a "
                    "curtain, a banner, a waterfall -- reads exactly this way, so this is "
                    "UNPROVEN, not clear"))
            else:
                res["hit"] = {"pos": [hit[0], hit[1], hit[2]], "dist": hit[3]}
                if r_["kind"] == "surface":
                    if hit[3] < d - tol:
                        res.update(status="blocked", why=(
                            f"geometry at {hit[3]:.0f} u stands in front of the declared surface "
                            f"at {d:.0f} u (tolerance {tol:.1f} u)"))
                    elif hit[3] > d + tol:
                        res.update(status="not-surface", why=(
                            f"the ray ran to {hit[3]:.0f} u, past the declared surface at "
                            f"{d:.0f} u (tolerance {tol:.1f} u). Nothing opaque was found AT the "
                            "point: either it is not where it was declared, or its face is "
                            "alpha-blended. The surface is NOT established"))
                    else:
                        res.update(status="confirmed", why=(
                            f"the ray stopped at {hit[3]:.0f} u against a declared surface at "
                            f"{d:.0f} u (within {tol:.1f} u)"))
                else:
                    if hit[3] >= d - args.sight_margin:
                        res.update(status="clear", why=(
                            f"first geometry {hit[3]:.0f} u along a {d:.0f} u ray"))
                    else:
                        res.update(status="blocked", why=(
                            f"geometry at {hit[3]:.0f} u, short of the {d:.0f} u subject "
                            f"(at {hit[0]:.0f},{hit[1]:.0f},{hit[2]:.0f})"))
        if res["status"] not in ("clear", "confirmed"):
            bad.append(res["label"])
        if not res["inDeliveredFrame"] and res["status"] != "behind":
            bad.append(res["label"])
        results.append(res)

    ok = not bad
    scene = scene_identity(run, args, probe.config_fingerprint if probe is not None else None)
    return {"status": "verified" if ok else "failed",
            "poseId": pose_identity(cell, eye, yaw, pitch, args.fov,
                                    getattr(args, "aspect", 1.0), run.size, subjects, args, scene),
            "scene": scene,
            "pose": {"eye": list(eye), "yaw": yaw,
                     "pitch": pitch, "fov": round(args.fov, 2),
                     "aspect": round(getattr(args, "aspect", 1.0), 4), "probeFov": round(probe_fov, 2),
                     "probeSize": run.size},
            "targets": results,
            "failed": sorted(set(bad))}


def print_verification(v, indent="  "):
    st = v["status"]
    print(f"{indent}pose id {v['poseId']}   probe fov {v['pose']['probeFov']:.0f} deg on a "
          f"{v['pose']['probeSize']}px square canvas; the DELIVERED frame is "
          f"{v['pose']['fov']:.1f} deg vertical at aspect {v['pose']['aspect']:.3f}")
    for t in v["targets"]:
        mark = {"clear": "CLEAR", "confirmed": "CONFIRMED", "blocked": "BLOCKED",
                "not-surface": "NOT-SURFACE", "unproven": "UNPROVEN",
                "off-probe": "OFF-PROBE", "behind": "BEHIND"}[t["status"]]
        px = "-" if t["pixel"] is None else f"{t['pixel'][0]},{t['pixel'][1]}"
        print(f"{indent}  {t['label']:<18} {mark:<12} [{t['kind']}] pick {px}  {t['why']}")
        if not t["inDeliveredFrame"] and t["status"] != "behind":
            fx, fy = (t["hOff"], t["vOff"])
            print(f"{indent}                   OFF-FRAME -- {fx} deg h / {fy} deg v is outside the "
                  f"DELIVERED frame. The sightline above is a separate question from containment;"
                  f" this target would not be in the picture")
    print(f"{indent}=> {'VERIFIED' if st == 'verified' else 'FAILED: ' + ', '.join(v['failed'])}")


# --------------------------------------------------------------------------------------
# reporting
# --------------------------------------------------------------------------------------

def claim_outputs(paths, cell):
    """Stamp every requested output path as belonging to THIS run, before the run starts.

    Refusing to write a success artifact is not enough when the caller reuses a path: a verified
    worklist from an earlier run sits there byte-identical, still saying `verified`, still
    carrying the earlier run's pose id, while the new run exits 1 (reviewed 2026-09-14). An early
    refusal -- bad input, no surviving candidate -- left a stale verified JSON behind too, because
    it exits before any writer is reached.

    So each requested path is claimed up front with an `incomplete` stub, and the real writers
    overwrite it. Whatever happens, the file at a path the caller asked to be written describes
    the latest run that asked for it. Only paths the caller NAMED are touched -- nothing else is
    discovered, moved or deleted.

    This is called as the FIRST statement of a command, ahead of reading the inputs, because an
    input refusal exits before any writer too. The one case it cannot cover is argparse rejecting
    the command line itself: the command function never runs, nothing is claimed, and whatever is
    at those paths is untouched -- correct, because no run of this tool started.
    """
    for p in paths:
        if not p:
            continue
        try:
            with open(p, "w", encoding="utf-8") as f:
                json.dump({"status": "incomplete", "cell": cell,
                           "note": "shotplan claimed this path at the start of a run that has "
                                   "not written its result yet. If you are reading this, that "
                                   "run did not finish -- it is NOT a plan, and any earlier "
                                   "result at this path has been superseded."}, f, indent=2)
        except OSError as e:
            print(f"  !! cannot write {p}: {e}", file=sys.stderr)


def report_refusals(scored):
    """Why the refused poses were refused, most common first.

    A planner that refuses everything and says nothing is indistinguishable from one that is
    broken, so the reasons are always printed -- including on the success path's counts.
    """
    why = {}
    for v in scored:
        for f in v["fail"]:
            k = f.split(":", 1)[1].strip()[:70] if ":" in f else f[:70]
            why[k] = why.get(k, 0) + 1
    if not why:
        return
    print("  refused for:")
    for k, n in sorted(why.items(), key=lambda kv: -kv[1])[:8]:
        print(f"    {n:5d}  {k}")


def pose_block(v, fov, label=""):
    e, yaw, pitch = v["eye"], v["yaw"], v["pitch"]
    return "\n".join([
        f"  {label}eye        {e[0]:.1f}, {e[1]:.1f}, {e[2]:.1f}",
        f"  viewprobe  --eye {e[0]:.1f},{e[1]:.1f},{e[2]:.1f} --yaw {yaw:.1f} "
        f"--pitch {pitch:.1f} --fov {fov:.2f}",
        f"  DevBench   camera action=drive "
        f"{{\"x\": {e[0]:.1f}, \"y\": {e[1]:.1f}, \"z\": {e[2]:.1f}, "
        f"\"yaw\": {math.radians(yaw):.4f}, \"pitch\": {math.radians(pitch):.4f}}}",
        f"             (yaw {yaw:.1f} deg -> {math.radians(yaw):.4f} rad. DevBench wants RADIANS;"
        f" degrees are wrapped mod 2pi with no error. Its `get` does NOT round-trip `drive`"
        f" -- verify by what the frame shows, never by the readback)",
    ])


def print_verdict(v, fov, target=None, indent="  "):
    for f in v["fail"]:
        print(f"{indent}REFUSED  {f}")
    for n in v["note"]:
        print(f"{indent}ok       {n}")
    print(f"{indent}framing  subjects reach {v['fill']:.2f} of the half-frame at vertical fov {fov:.2f}"
          + (f" (target {target:.2f})" if target is not None else ""))
    vc = v.get("v_centre")
    if vc is not None:
        print(f"{indent}         their midpoint sits {vc:.2f} down the frame"
              + ("" if abs(vc - 0.5) < 0.08
                 else " -- OFF-CENTRE. --aim-height is the knob: aim at the MIDDLE of the "
                      "subject, which for a SEATED actor is far below a standing head"))


# --------------------------------------------------------------------------------------
# subcommands
# --------------------------------------------------------------------------------------

def cmd_plan(args):
    # FIRST, before a single input is read. A refusal raised while resolving the inputs -- a
    # malformed --surface, a surface-only plan with no --floor-z, more points than --surface-max,
    # a --from-profile whose ini cannot be found -- exits before every writer below, so claiming
    # the paths any later leaves the PREVIOUS run's verified result and its renderable worklist
    # sitting at the paths THIS run asked for (measured 2026-09-14: three such refusals each
    # exited 1 over a stale `status: verified` JSON carrying the earlier run's pose id).
    claim_outputs([args.worklist, args.json, args.dump], args.cell)
    apply_profile_frame(args)
    subjects = collect_subjects(args)
    floor_z = resolve_floor(subjects, args)
    cx = sum(s.x for s in subjects) / len(subjects)
    cy = sum(s.y for s in subjects) / len(subjects)
    aim = resolve_aim(subjects, floor_z, args)

    run = Runner(args.cell, args.vp, args.passthrough, args.out, args.size, not args.quiet)

    print(f"cell {args.cell}   floor z {floor_z:.0f}   probe eye z {floor_z + args.eye_height:.0f}")
    for s in subjects:
        print(f"  subject {s.label:<18} {s.x:.1f}, {s.y:.1f}, {s.z:.1f}")
    print(f"  aim point          {aim[0]:.1f}, {aim[1]:.1f}, {aim[2]:.1f}")

    # Pass 1 -- the clearance maps. One render per subject per sector; every pick in a render is
    # free once its load walk has run, which is the whole reason this beats testing candidate
    # cameras one at a time.
    print(f"\n--- pass 1: clearance maps ({len(subjects)} subject(s) "
          f"x {len(args.map_yaws)} render(s)) ---")
    n_bear = int(round(360.0 / args.bearing_step))
    for s in subjects:
        s.cl = build_clearance(run, s.pos(), floor_z, args)
        vals = sorted(v for v in s.cl.bins.values() if v)
        print(f"  {s.label:<18} {s.cl.sampled()}/{n_bear} bearings bounded"
              + (f", clearance {vals[0]:.0f}-{vals[-1]:.0f} u, median {vals[len(vals)//2]:.0f} u"
                 if vals else " -- NOTHING was bounded, so nothing can be planned"))

    # Pass 2 -- score every candidate on sightline and framing alone. Both are pure arithmetic
    # over pass 1, so the whole polar grid is judged without another render.
    stands = [args.standoff_min + i * args.standoff_step
              for i in range(int((args.standoff_max - args.standoff_min) / args.standoff_step) + 1)]
    cands = []
    for i in range(n_bear):
        b = i * args.bearing_step
        ux, uy = bearing_vec(b)
        for st in stands:
            cands.append((b, st, (round(cx + ux * st), round(cy + uy * st))))
    print(f"\n--- pass 2: {len(cands)} candidate poses "
          f"({n_bear} bearings x {len(stands)} standoffs), scored without navmesh ---")
    stage1 = []
    for b, st, xy in cands:
        eye = (float(xy[0]), float(xy[1]), floor_z + args.eye_height)
        v = evaluate(eye, subjects, floor_z, None, args, aim, skip_nav=True)
        v["_bearing"], v["_standoff"], v["_xy"] = b, st, xy
        stage1.append(v)
    live = [v for v in stage1 if v["ok"]]
    print(f"  {len(live)} survive sightline + framing; {len(stage1) - len(live)} refused")
    report_refusals(stage1)
    if not live:
        raise SystemExit("\nNo camera survives sightline and framing. Widen --fov, raise "
                         "--standoff-max, or check the subjects are where you think they are.")

    # Pass 3 -- one invocation carrying `--nav-at` AND `--inside-at` for the survivors, and
    # `--refs-at` around each subject for the doors. Batched here rather than with pass 1
    # because the full polar grid would be ~1,400 probes, and that many flags overruns a
    # Windows command line. A cap that silently dropped candidates would read as "these were
    # considered and rejected", so anything dropped is named.
    live.sort(key=lambda v: -v["score"])
    seen, pts = set(), []
    for v in live:
        if v["_xy"] not in seen:
            seen.add(v["_xy"])
            pts.append(v["_xy"])
    dropped = max(0, len(pts) - args.nav_probe_max)
    pts = pts[:args.nav_probe_max]
    eye_z = round(floor_z + args.eye_height)
    print(f"\n--- pass 3: nav- and inside-probing {len(pts)} surviving point(s) in ONE render ---"
          + (f"\n  !! {dropped} point(s) DROPPED by --nav-probe-max={args.nav_probe_max}; "
             f"those poses are NOT judged and NOT offered" if dropped else ""))
    flags = ["--eye", f"{cx:.1f},{cy:.1f},{floor_z + args.eye_height:.1f}", "--yaw", "0"]
    for x, y in pts:
        flags += ["--nav-at", f"{x},{y}", "--inside-at", f"{x},{y},{eye_z}",
                  "--clear-at", f"{x},{y},{eye_z}"]
    flags += ["--clear-reach", f"{args.clear_reach:g}"]
    for s in subjects:
        flags += ["--refs-at", f"{round(s.x)},{round(s.y)},{int(args.door_radius)}"]
    p3 = run.probe(*flags)
    nav, inside, near, clear = p3.nav(), p3.inside(), p3.refs_near(), p3.clear()
    doors = doors_near(subjects, near, args)
    kept = set(pts)
    scored = []
    for v in live:
        if v["_xy"] not in kept:
            continue
        v2 = evaluate(v["eye"], subjects, floor_z, nav.get(v["_xy"]), args, aim,
                      inside_entry=inside.get((v["_xy"][0], v["_xy"][1], eye_z)),
                      clear_entry=clear.get((v["_xy"][0], v["_xy"][1], eye_z)))
        v2["_bearing"], v2["_standoff"], v2["_xy"] = v["_bearing"], v["_standoff"], v["_xy"]
        scored.append(v2)
    ok = [v for v in scored if v["ok"]]
    ok.sort(key=lambda v: -v["score"])
    n_inside = sum(1 for v in scored if v.get("inside") in ("inside", "unresolved", "unknown"))
    n_unsup = sum(1 for v in scored if v.get("support") in ("contradicted", "unresolved", "unknown"))
    print(f"  {len(ok)} of {len(scored)} survive the placement test; "
          f"{len(scored) - len(ok)} refused ({n_inside} for an eye inside -- or untested against -- "
          f"a physical ref's box, "
          f"{n_unsup} with no placed floor under the eye on its own storey)")
    report_refusals(scored)
    if not ok:
        raise SystemExit("\nEvery sightline-clear camera sits off the storey or inside a ref. Raise "
                         "--offmesh-tol / --storey-tol / --inside-cap only if you can say why.")

    print(f"\n--- top {min(args.top, len(ok))} ---")
    for v in ok[:args.top]:
        print(f"  score {v['score']:5.1f}  bearing {v['_bearing']:5.1f} deg  standoff {v['_standoff']:4.0f} u  "
              f"fill {v['fill']:.2f}  wall margin {v['wall_margin']:.0f} u  nav {v['nav']}  inside {v['inside']}")

    # The final check, and the candidate walk it drives. The map is interpolated between sampled
    # bearings and is probed FROM the target at eye height; this shoots the ray the DELIVERED
    # camera will actually use, and it is the claim that counts. When the top candidate fails,
    # walking down the ranked list is cheap (one render each) and is the thing a human would do
    # by hand anyway -- but it is a SAMPLED search over the candidates this grid happened to
    # produce, so exhausting it says nothing about whether some other camera exists.
    best, verification, attempts = ok[0], None, []
    if args.no_verify:
        print(f"\n=== chosen (PROPOSAL -- NOT VERIFIED) ===")
    else:
        tries = max(1, args.verify_candidates)
        print(f"\n--- verification: the chosen pose, probed as itself "
              f"(up to {tries} candidate(s)) ---")
        for i, cand in enumerate(ok[:tries]):
            v = verify_in_pose(run, args.cell, cand["eye"], cand["yaw"], cand["pitch"],
                               subjects, args)
            attempts.append({"rank": i + 1, "bearing": cand["_bearing"],
                             "standoff": cand["_standoff"], "poseId": v["poseId"],
                             # this attempt's OWN renderer identity: an attempt may keep the one
                             # its render reported, and can never lend it to a later one
                             "renderer": v["scene"]["renderer"],
                             "status": v["status"], "failed": v["failed"]})
            print(f"  candidate {i+1} of {tries}: bearing {cand['_bearing']:.0f} deg, standoff "
                  f"{cand['_standoff']:.0f} u")
            print_verification(v, indent="    ")
            best, verification = cand, v
            if v["status"] == "verified":
                break
        if verification["status"] != "verified":
            print(f"\n  !! no candidate verified in {len(attempts)} attempt(s). The map and the "
                  f"aimed probe DISAGREE, and the aimed probe wins.")
            print("     This is a SAMPLED search over the candidates this grid produced -- it is "
                  "NOT a proof that no camera can see these targets. Try a finer --bearing-step, "
                  "a different --standoff range, more --verify-candidates, or declare the feature "
                  "points differently.")

    best = dict(best)
    # Adopt the SAME canonical pose the verification ran on, so the printed block, the worklist,
    # the DevBench conversion and the identity all describe one pose rather than three roundings
    # of it.
    c_eye, c_yaw, c_pitch = canonical_pose(best["eye"], best["yaw"], best["pitch"])
    best["eye"], best["yaw"], best["pitch"] = c_eye, c_yaw, c_pitch
    # The verification's own scene, verbatim -- recomputing it here would be a second opinion
    # about one render, and the two could disagree the moment they were gathered at different
    # moments. With --no-verify there is no verification render, so there is no renderer identity
    # to report and the binding is declaration-only.
    scene = verification["scene"] if verification else scene_identity(run, args)
    pose_id = pose_identity(args.cell, c_eye, c_yaw, c_pitch, args.fov,
                            getattr(args, "aspect", 1.0), run.size, subjects, args, scene)
    if verification is None:
        status = "unverified"
    elif verification["status"] == "verified":
        status = "verified"
    else:
        status = "refused"
    best["poseId"] = pose_id
    best["verified"] = status == "verified"
    if status != "verified":
        # `ok` is the field callers read, so it carries the FINAL answer -- for a SKIPPED check as
        # well as a failed one. A proposal whose verification is missing has not been shown to be
        # ok, and leaving `ok: true` there was the original bug wearing a different hat. The
        # pre-verification detail is kept beside it rather than thrown away.
        best["preVerificationOk"] = best["ok"]
        best["ok"] = False
        if verification is None:
            best["fail"] = list(best["fail"]) + [
                "verification: SKIPPED by --no-verify -- this camera's final check was never run"]
        else:
            best["fail"] = list(best["fail"]) + [
                f"verification: {t['label']} {t['status']} -- {t['why']}"
                for t in verification["targets"] if t["status"] not in ("clear", "confirmed")]

    print(f"\n=== chosen ({status.upper()}) ===")
    print(pose_block(best, args.fov))
    print(f"  pose id    {pose_id}   (the camera, its targets and its policy; every artifact "
          f"below carries it)")
    print()
    print_verdict(best, args.fov, args.fill)
    print(f"  feet       {best['eye'][0]:.1f}, {best['eye'][1]:.1f}, {floor_z:.1f}"
          "   (the player's spot: the eye's X,Y at the floor, placement-tested)")
    if status == "unverified":
        print("  !! --no-verify was given: this is a PROPOSAL whose final check was never run. "
              "It is not a verified camera and nothing below says it is.")
    print_doors(doors, args)

    if args.worklist and status == "refused" and not args.emit_refused:
        # No renderable shot -- but the path the caller named must not be left holding an earlier
        # run's verified worklist, so it gets a truthful, shot-less refusal record instead.
        with open(args.worklist, "w", encoding="utf-8") as f:
            json.dump({"label": args.label or f"shotplan -- {args.cell}",
                       "status": "refused", "poseId": pose_id, "scene": scene,
                       "verification": verification,
                       "cells": [],
                       "note": "REFUSED by the final check -- no shot is offered. This file "
                               "replaces whatever was at this path so an earlier run's verified "
                               "worklist cannot be mistaken for this run's result. "
                               "--emit-refused writes the refused shot itself."},
                      f, indent=2, default=str)
        print(f"\nworklist REFUSED -> {args.worklist} (no shot: the final check refused this "
              f"camera -- {', '.join(verification['failed'])}). Pass --emit-refused to write the "
              f"shot anyway, marked REFUSED.")
    elif args.worklist:
        mark = {"verified": "", "unverified": "UNVERIFIED -- ", "refused": "REFUSED -- "}[status]
        shot = {"label": mark + (args.label or "planned camera")
                         + f" -- {', '.join(s.label for s in subjects)}",
                "eye": [round(best["eye"][0], 1), round(best["eye"][1], 1), round(best["eye"][2], 1)],
                "floorZ": round(floor_z, 1),
                "poseId": pose_id,
                "status": status,
                "verification": verification,
                # the PLAYER's spot: the point the placement test just passed, at the floor --
                # a runner takes it from here, never from a coordinate typed at the storey's height
                "feet": [round(best["eye"][0], 1), round(best["eye"][1], 1), round(floor_z, 1)],
                "yaw": round(best["yaw"], 1), "pitch": round(best["pitch"], 1), "fov": args.fov,
                "note": (f"planned by shotplan: bearing {best['_bearing']:.0f} deg, standoff "
                         f"{best['_standoff']:.0f} u, subjects fill {best['fill']:.2f} of the "
                         f"half-frame, nearest obstruction behind the camera "
                         f"{best['wall_margin']:.0f} u further out")}
        if doors:
            shot["doors"] = [d["formkey"] for d in doors]
        wl = {
            "label": args.label or f"shotplan -- {args.cell}",
            # The same three fields a REFUSED worklist carries, in the same place, so one read
            # answers "what is this file" either way: a consumer that checks `status` at the top
            # level must not get None on success and "refused" on refusal.
            "status": status,
            "poseId": pose_id,
            "scene": scene,
            "eyeHeight": args.eye_height,
            "cells": [{"cell": args.cell, "shots": [shot]}],
        }
        with open(args.worklist, "w", encoding="utf-8") as f:
            json.dump(wl, f, indent=2)
        print(f"\nworklist -> {args.worklist}")
        print("  render it:  viewprobe --worklist " + args.worklist)
        print("  drive it :  shotplan.py devbench " + args.worklist)

    if args.dump:
        with open(args.dump, "w", encoding="utf-8") as f:
            json.dump([{"bearing": v["_bearing"], "standoff": v["_standoff"], "xy": v["_xy"],
                        "ok": v["ok"], "nav": v["nav"], "fill": round(v["fill"], 3),
                        "score": round(v["score"], 1), "fail": v["fail"]}
                       for v in (scored if args.dump_survivors else stage1)], f, indent=1)
        print(f"dump     -> {args.dump} "
              f"({'nav-probed survivors' if args.dump_survivors else 'all candidates'})")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"cell": args.cell, "floorZ": floor_z, "aim": aim,
                       # THE field to read. "verified" means the delivered pose was probed as
                       # itself and every target came back clear or confirmed; "refused" means it
                       # was probed and did not; "unverified" means --no-verify, so the final
                       # check was never run and this is a proposal.
                       "status": status,
                       "poseId": pose_id,
                       # what scene this camera was judged against -- declared inputs, plus the
                       # renderer's own fingerprint when it printed one. See scene_identity.
                       "scene": scene,
                       "verification": verification,
                       "verifyAttempts": attempts,
                       "subjects": [{"label": s.label, "pos": s.pos(), "kind": s.kind,
                                     "extent": s.extent} for s in subjects],
                       "feet": [best["eye"][0], best["eye"][1], floor_z], "doors": doors,
                       "chosen": {k: v for k, v in best.items() if k != "_target"},
                       "top": [{k: v for k, v in x.items() if k != "_target"} for x in ok[:args.top]],
                       "renders": run.renders, "seconds": round(run.seconds, 1)}, f, indent=2,
                      default=str)
        print(f"json     -> {args.json}")

    print(f"\n{run.renders} viewprobe renders, {run.seconds:.0f}s of load walks.")
    # The exit code is the last place the answer can go wrong: a caller that reads neither the
    # stdout nor the JSON still has to be told. refused -> 1, unverified -> 2 (an explicit
    # request, so not an error, but never mistakable for a pass), verified -> 0.
    return {"verified": 0, "refused": 1, "unverified": 2}[status]


def cmd_check(args):
    """Judge a camera someone already has. This is the positive control.

    A planner that never refuses anything is not validated. Point this at a camera known to be
    bad and it must say so, for a reason you can check by hand.
    """
    claim_outputs([args.json], args.cell)  # before the inputs are read; see cmd_plan
    apply_profile_frame(args)
    subjects = collect_subjects(args)
    floor_z = resolve_floor(subjects, args)
    eye = tuple(float(v) for v in args.eye.split(","))
    aim = resolve_aim(subjects, floor_z, args)

    yaw = bearing_deg(aim[0] - eye[0], aim[1] - eye[1])
    dxy = math.hypot(aim[0] - eye[0], aim[1] - eye[1])
    pitch = math.degrees(math.atan2(eye[2] - aim[2], dxy)) if dxy > 1 else 0.0

    # Same canonicalisation as `plan`: judge, submit and export ONE pose.
    eye, yaw, pitch = canonical_pose(eye, yaw, pitch)

    run = Runner(args.cell, args.vp, args.passthrough, args.out, args.size, not args.quiet)
    print(f"cell {args.cell}   camera {eye[0]:.1f},{eye[1]:.1f},{eye[2]:.1f}   floor z {floor_z:.0f}")

    # Same contract as `plan`: the pose that would be delivered is probed AS ITSELF, in one
    # render, with a pick at the pixel each target lands on -- not re-aimed at each target in
    # turn. The old per-target `--look` render answered "could a camera here see it", which is a
    # different question from "does THIS camera, aimed where it is aimed, see it", and the two
    # separate on every shot that is not aimed straight at one target.
    eye_i = (round(eye[0]), round(eye[1]), round(eye[2]))
    extra = ["--nav-at", f"{eye_i[0]},{eye_i[1]}",
             "--inside-at", f"{eye_i[0]},{eye_i[1]},{eye_i[2]}",
             "--clear-at", f"{eye_i[0]},{eye_i[1]},{eye_i[2]}",
             "--clear-reach", f"{args.clear_reach:g}",
             "--refs-at", f"{eye[0]:.0f},{eye[1]:.0f},{args.refs_radius:g}"]
    for s2 in subjects:
        extra += ["--refs-at", f"{round(s2.x)},{round(s2.y)},{int(args.door_radius)}"]
    p = run.probe("--eye", "{:.1f},{:.1f},{:.1f}".format(*eye), "--yaw", "0", *extra)
    nav = p.nav().get((round(eye[0]), round(eye[1])))
    inside = p.inside().get(eye_i)
    clear = p.clear().get(eye_i)
    doors = doors_near(subjects, p.refs_near(), args)
    refs = p.refs()[:14]

    ver = verify_in_pose(run, args.cell, eye, yaw, pitch, subjects, args)
    print(f"\n--- sightlines, in the pose this camera would be delivered in "
          f"(aimed at {aim[0]:.0f},{aim[1]:.0f},{aim[2]:.0f}) ---")
    print_verification(ver)
    fails = [f"{t['label']}: {t['status']}" for t in ver["targets"]
             if t["status"] not in ("clear", "confirmed")]
    fails += [f"{t['label']}: off-frame" for t in ver["targets"]
              if not t["inDeliveredFrame"] and t["status"] != "behind"]

    kind, msg = nav_verdict(nav, floor_z, args)
    skind, smsg = support_verdict(clear, eye, floor_z, args)
    print(f"\n--- placement ---\n  {kind}: {msg}\n  support ({skind}): {smsg}")
    if kind in ("wrong-storey", "no-floor", "unknown"):
        fails.append(f"placement: {kind}")
    elif kind == "off-mesh-near" and skind != "supported":
        # The off-mesh allowance passes a point no navmesh covers on the strength of a
        # distance. It now has to be paid for in geometry -- see support_verdict.
        fails.append(f"placement: off-mesh-near, support {skind}")
    ikind, ifails, inotes = inside_verdict(inside, args)
    if ikind == "unknown":
        ifails = ifails + ["inside: no --inside-at answer -- the eye was NOT tested"]
    print(f"\n--- inside (whose OBND box encloses the eye; --inside-cap {args.inside_cap:.0f} u) ---")
    for f in ifails:
        print(f"  REFUSED  {f}")
    for n in inotes:
        print(f"  ok       {n}")
    if ifails:
        fails.append("inside: " + "; ".join(f.split(": ", 1)[1] for f in ifails))
    print_doors(doors, args)

    ok, fill, why, v_centre = frame_check(eye, yaw, pitch, args.fov, subjects, args)
    print(f"\n--- framing (aimed at {aim[0]:.0f},{aim[1]:.0f},{aim[2]:.0f}) ---")
    print(f"  subjects reach {fill:.2f} of the half-frame at vertical fov {args.fov:.2f}"
          + ("" if ok else f"  -- {why}"))
    print(f"  their midpoint sits {v_centre:.2f} down the frame "
          f"({'centred' if abs(v_centre - 0.5) < 0.08 else 'OFF-CENTRE; raise or lower --aim-height'})")
    if not ok:
        fails.append("framing")

    if args.show_refs and refs:
        print("\n--- what is around the camera ---")
        for ln in refs:
            print("  " + ln.strip())

    print("\n=== verdict ===")
    print(f"  pose id {ver['poseId']}")
    if fails:
        print("  REFUSED -- " + "; ".join(fails))
    else:
        print("  ACCEPTED")
        print(pose_block({"eye": eye, "yaw": yaw, "pitch": pitch}, args.fov))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"cell": args.cell, "floorZ": floor_z, "aim": aim,
                       "status": "refused" if fails else "verified",
                       "poseId": ver["poseId"], "scene": ver["scene"], "verification": ver,
                       "subjects": [{"label": s.label, "pos": s.pos(), "kind": s.kind,
                                     "extent": s.extent} for s in subjects],
                       "framing": {"fill": round(fill, 3), "vCentre": round(v_centre, 3),
                                   "ok": ok},
                       "placement": {"nav": kind, "navWhy": msg,
                                     "support": skind, "supportWhy": smsg,
                                     "supportIs": "static placed geometry under the eye, NOT player "
                                                  "reachability or actor collision"},
                       "doors": doors, "fails": fails,
                       "renders": run.renders, "seconds": round(run.seconds, 1)}, f, indent=2,
                      default=str)
        print(f"  json -> {args.json}")
    print(f"\n{run.renders} viewprobe renders, {run.seconds:.0f}s.")
    return 1 if fails else 0


def cmd_frame(args):
    """What frame will the game actually render? Read it, do not estimate it."""
    # Same contract as `plan` and `check`: the path is claimed before the ini is read, because a
    # missing profile or an unreadable resolution exits before the writer below and would leave
    # an earlier run's frame sitting at the path this run asked for.
    claim_outputs([args.json], None)
    fr = game_frame(args.instance, args.profile, args.game, args.my_games,
                    getattr(args, "fov_setting", None))
    print_frame(fr)
    if args.explain:
        print()
        print("How the vertical angle follows from the setting:")
        print("  The ini gives a SETTING, not a frustum. fDefaultWorldFOV is the HORIZONTAL")
        print("  angle at a fixed 16:9 BASE, so the VERTICAL angle is fixed by the setting")
        print("  alone and a wider frame gets more horizontal view:")
        print("      tan(fov_v/2) = tan(setting/2) / (16/9)")
        print("      tan(fov_h/2) = tan(fov_v/2) * (width/height)")
        print("  This is a rule, not a constant: change the setting or the resolution and")
        print("  RECALCULATE. Do not re-measure, and do not fit an empirical angle per session.")
        print()
        print("  Measured 2026-09-09 on Skyrim SE 1.7.104, free camera / normal player camera:")
        print("    setting  output       vertical          horizontal")
        print("    80       3840x2160    50.524 / 50.539   79.998 / 80.005")
        print("    100      3840x2160    67.684 / 67.698   99.999 / 99.997")
        print("    65       3840x2160    39.420            64.993   (set by the console `fov`)")
        print("    80       1600x1200    50.546 / 50.546   64.372 / 64.373")
        print("  The vertical angle is the SAME at both output aspects. Two earlier readings")
        print("  died on that: horizontal-at-4:3-base (predicts 64.37 vertical at setting 80)")
        print("  and horizontal-at-the-actual-aspect (fits every 16:9 frame and predicts 64.37")
        print("  vertical at 1600x1200). A single output aspect cannot identify the base.")
        print()
        print("  What DOES need re-measuring: another runtime (VR), or a mod that takes the")
        print("  projection over. To measure it, do not crop a render against a game frame --")
        print("  rotate a position-pinned free camera by a known angle and find the frustum")
        print("  under which the two frames register. That depends on no renderer and on no")
        print("  knowledge of the scene, and it measures each axis separately.")
        print()
        print("  A runtime SetINI of this key does NOT reach the running camera (measured: the")
        print("  query changes, the frustum does not, and a POV cycle does not apply it either).")
        print("  The console `fov` command does change it, immediately, and is a different path.")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({k: v for k, v in fr.items() if k not in ("w_hits", "h_hits", "fov_hits")},
                      f, indent=2)
        print(f"\njson -> {args.json}")
    return 0


def cmd_devbench(args):
    """A worklist is already a shot list. This makes it a DevBench camera script.

    One file, three consumers now -- the fly-through, the console bat, and the game camera -- so
    the render, the tester's feet and the screenshot all come from the same coordinates and
    cannot drift apart.
    """
    with open(args.worklist, encoding="utf-8") as f:
        wl = json.load(f)
    if "cells" not in wl:
        raise SystemExit(f"{args.worklist} has no 'cells' -- that is not a worklist. "
                         "A conflict emit and a worklist share an extension and nothing else.")
    n = 0
    out = []
    for cell in wl["cells"]:
        for shot in cell.get("shots", []):
            n += 1
            if args.shot and n != args.shot:
                continue
            eye = shot["eye"]
            yaw = shot.get("yaw")
            pitch = shot.get("pitch", 0) or 0
            if yaw is None and "look" in shot:
                lk = shot["look"]
                yaw = bearing_deg(lk[0] - eye[0], lk[1] - eye[1])
                dxy = math.hypot(lk[0] - eye[0], lk[1] - eye[1])
                pitch = math.degrees(math.atan2(eye[2] - lk[2], dxy)) if dxy > 1 else 0.0
            if yaw is None:
                raise SystemExit(f"shot {n} has neither yaw nor look")
            drive = {"action": "drive", "x": round(eye[0], 1), "y": round(eye[1], 1),
                     "z": round(eye[2], 1),
                     "yaw": round(math.radians(yaw), 4), "pitch": round(math.radians(pitch), 4)}
            back = predict_readback(yaw, pitch)
            out.append({"n": n, "cell": cell["cell"], "label": shot.get("label", ""),
                        "floorZ": shot.get("floorZ"), "feet": shot.get("feet"),
                        "doors": shot.get("doors") or [], "yawDeg": round(yaw, 1),
                        "pitchDeg": round(pitch, 1), "drive": drive,
                        "expectReadback": (None if back is None else
                                           {"camYaw": round(back[0], 4),
                                            "camPitch": round(back[1], 4)})})
    if args.json:
        print(json.dumps(out, indent=2))
        return 0
    for s in out:
        print(f"# shot {s['n']} -- {s['cell']}: {s['label']}")
        print(f"#   yaw {s['yawDeg']} deg -> {s['drive']['yaw']} rad, "
              f"pitch {s['pitchDeg']} deg -> {s['drive']['pitch']} rad  (DevBench wants RADIANS)")
        if s["expectReadback"] is None:
            print("#   camera action=get will report NEITHER camYaw NOR camPitch at this pose "
                  "(the Euler extraction fails); check the aim in the image.")
        else:
            print(f"#   camera action=get should then report camYaw "
                  f"{s['expectReadback']['camYaw']} camPitch {s['expectReadback']['camPitch']} "
                  f"-- assert THAT, not the commanded angles")
        if s["feet"] is not None:
            print(f"#   the PLAYER stands at feet {s['feet']} -- the placement-tested spot at the "
                  f"floor; dbharness.move_within_cell(*feet) first, or the interior renders unlit")
        elif s["floorZ"] is not None:
            print(f"#   the PLAYER goes to the floor: player.setpos z {s['floorZ']} "
                  f"-- move the player to the subject first or the interior renders unlit")
        if s["doors"]:
            print(f"#   close these doors FIRST (the records say closed; the game may not): "
                  f"{s['doors']} -- dbharness.close_doors(doors) resolves the FormKeys and asserts")
        print(json.dumps(s["drive"]))
        print(f"curl -s -X POST http://127.0.0.1:{args.port}/api/tool/camera "
              f"-H 'content-type: application/json' -d '"
              + json.dumps({"action": "freecam", "on": True}) + "'")
        print(f"curl -s -X POST http://127.0.0.1:{args.port}/api/tool/camera "
              f"-H 'content-type: application/json' -d '" + json.dumps(s["drive"]) + "'")
        print()
    print(f"# {len(out)} shot(s). freecam is fire-and-forget -- poll camera action=get until")
    print("# freeCam is true before driving. Verify a pose against the expected readback above,")
    print("# never against the commanded angles -- they are different quantities.")
    return 0


# --------------------------------------------------------------------------------------

def apply_profile_frame(args):
    """--from-profile: take --fov and --aspect from the game's own ini instead of a guess."""
    if not getattr(args, "from_profile", False):
        return
    inst = args.instance or _passthrough_value(args, "--instance")
    prof = args.profile or _passthrough_value(args, "--profile")
    gm = args.game or _passthrough_value(args, "--game")
    if not inst or not prof:
        raise SystemExit("--from-profile needs the instance and profile: give --instance/--profile, "
                         "or pass them through after -- as viewprobe already takes them")
    fr = game_frame(inst, prof, gm, args.my_games, getattr(args, "fov_setting", None))
    print_frame(fr)
    args.fov, args.aspect = fr["fov_vertical"], fr["aspect"]
    print()


def _passthrough_value(args, flag):
    pt = getattr(args, "passthrough", []) or []
    for i, a in enumerate(pt):
        if a == flag and i + 1 < len(pt):
            return pt[i + 1]
    return None


def add_common(p, need_subjects=True):
    p.add_argument("cell", help="CellEditorID or FormID -- passed to viewprobe as args[0]")
    p.add_argument("--subject", action="append", default=[],
                   metavar="X,Y[,Z][=label]",
                   help="a thing that must be in frame, at its FEET. Repeatable. Feed RUNTIME "
                        "positions (DevBench inspect kind=refs) when the game is up -- an actor "
                        "is placed at its AI package anchor, not at its placement record")
    p.add_argument("--subject-from-game", action="append", default=[],
                   metavar="FORMID[=label]",
                   help="a subject whose position is read off the RUNNING game (DevBench inspect "
                        "kind=refs): a runtime 0x........ id or a FormKey XXXXXX:Plugin.esp. "
                        "Repeatable. REFUSES when no game answers -- a movable item has dropped by "
                        "the time the cell is loaded, so it is planned from the game, never from "
                        "its record")
    p.add_argument("--surface", action="append", default=[], metavar="X,Y,Z[=label]",
                   help="a DECLARED point on an opaque face that the shot must expose -- the lip "
                        "of a step, a point on a contact seam, the corner of a table. Repeatable. "
                        "Unlike --subject it is proved by a ray that STOPS there: a ray that runs "
                        "PAST it establishes nothing. The points are supplied BY THE CALLER from "
                        "source evidence (a record, a dumped mesh, a retained worklist) -- this "
                        "tool reads no meshes and finds no features")
    p.add_argument("--surface-extent", type=float, default=0.0,
                   help="half-extent of context kept in frame around each --surface point "
                        "(default 0: the point alone). This is the framing/context extent; the "
                        "point itself is what gets verified")
    p.add_argument("--surface-max", type=int, default=8,
                   help="how many --surface points are accepted (default 8). Each costs a pick "
                        "in the verification render and widens the frame they must all fit in")
    p.add_argument("--surface-tol", type=float, default=2.0,
                   help="the CONSTANT term of the surface-hit tolerance, in units (default 2). "
                        "viewprobe prints hit positions and distances as whole units and a "
                        "declared point is itself rounded; the distance-dependent terms (pixel "
                        "centre, pose rounding, face slant) are computed -- see surface_tolerance")
    p.add_argument("--surface-obliquity", type=float, default=1.73,
                   help="tan of the worst face tilt assumed when converting a lateral sampling "
                        "miss into a range error (default 1.73 = 60 deg)")
    p.add_argument("--verify-fov-max", type=float, default=150.0,
                   help="widest PROBE frustum the final check may open to reach an off-axis "
                        "target (default 150). The probe canvas is square and a wide deliverable "
                        "frame is wider still; widening the probe does NOT move the camera and "
                        "does not change what the delivered frame contains")
    p.add_argument("--port", type=int, default=None,
                   help="DevBench port for --subject-from-game (default: DEVBENCH_PORT, then the "
                        "port in the file DEVBENCH_RUNTIME_JSON names, then 8920)")
    p.add_argument("--door-radius", type=float, default=300.0,
                   help="every drawn Door ref within this 3-D distance of a subject goes into the "
                        "worklist shot as `doors` for the runner to close first (default 300, the "
                        "radius that fixed the open-leaf frame on 2026-09-03)")
    p.add_argument("--inside-reach", type=float, default=96.0,
                   help="an eye whose nominating ref (the one whose OBND box encloses it) has "
                        "geometry within this distance on --inside-rays of the six world axes is "
                        "ENCLOSED and refused (default 96: a shelf above, a shelf below, a back "
                        "panel; a room's walls are further)")
    p.add_argument("--inside-rays", type=int, default=3,
                   help="how many of the six axis rays must hit within --inside-reach (default 3)")
    p.add_argument("--inside-cap", type=float, default=1024.0,
                   help="FALLBACK when a nominating ref's mesh cannot be read: its box refuses the "
                        "eye when its largest side is at most this, and is taken as a room shell "
                        "above it (default 1024)")
    p.add_argument("--floor-z", type=float, default=None,
                   help="the storey the subjects stand on (default: the lowest subject z)")
    p.add_argument("--eye-height", type=float, default=120.0,
                   help="camera height above the floor (default 120, viewprobe's own default)")
    p.add_argument("--aim-height", type=float, default=100.0,
                   help="height above a subject's feet that the camera aims at (default 100, a "
                        "standing head). Aim at the MIDDLE of what should be centred -- a seated "
                        "actor centred wants roughly half this, or it frames low")
    p.add_argument("--sight-height", type=float, default=100.0,
                   help="height above a subject's feet that the SIGHTLINE probes aim at "
                        "(default 100). Keep this at head height even when --aim-height is "
                        "lower: a ray fired at a seated actor's middle hits the chair, and "
                        "reports the chair as an obstruction")
    p.add_argument("--aim", default=None, metavar="X,Y,Z", help="aim here instead of the centroid")
    p.add_argument("--fov", type=float, default=65.0,
                   help="the DELIVERABLE camera's VERTICAL fov (default 65, viewprobe's own)")
    p.add_argument("--aspect", default="1:1",
                   help="frame aspect W:H (default 1:1, viewprobe's square frame). Set it to the "
                        "frame you will actually take -- a 16:9 game frame is WIDER horizontally "
                        "and, at the same --fov, is not the same crop")
    p.add_argument("--subject-height", type=float, default=128.0,
                   help="how tall a subject is, for the framing test (default 128)")
    p.add_argument("--subject-width", type=float, default=40.0,
                   help="half-width of a subject, for the framing test (default 40)")
    p.add_argument("--frame-pad", type=float, default=0.12,
                   help="fraction of the half-frame kept clear at the edges (default 0.12)")
    p.add_argument("--sight-margin", type=float, default=24.0,
                   help="how much further than the subject the first geometry must be (default 24)")
    p.add_argument("--storey-tol", type=float, default=96.0,
                   help="how far a walkable level may sit from the floor and still be it (default 96)")
    p.add_argument("--offmesh-tol", type=float, default=160.0,
                   help="how far off navmesh a camera point may sit and still count as inside "
                        "the room (default 160 -- navmesh is cut around furniture)")
    p.add_argument("--clear-reach", type=float, default=512.0,
                   help="how far --clear-at's axis rays look for real placed geometry around the "
                        "eye (default 512). It must cover the storey band under the eye, or "
                        "'nothing below' is a statement about the reach and reads UNRESOLVED")
    p.add_argument("--size", type=int, default=320, help="probe render size (default 320)")
    p.add_argument("--out", default=None, help="where probe PNGs go")
    p.add_argument("--vp", default=None,
                   help="the viewprobe command (default: dotnet run against this checkout). "
                        "Anything that takes viewprobe's args, e.g. a wrapper script")
    p.add_argument("--quiet", action="store_true", help="do not echo each invocation")
    p.add_argument("--no-bind-scene", action="store_true",
                   help="do not ask the renderer for its configuration identity on the "
                        "verification render. The pose is then bound to the caller's DECLARED "
                        "inputs only, and the artifact says so")
    p.add_argument("--fov-setting", type=float, default=None,
                   help="the game's fDefaultWorldFOV, when no ini sets it and the runtime's own "
                        "default is not the measured SE one (see FOV_ENGINE_DEFAULT)")
    p.add_argument("--from-profile", action="store_true",
                   help="read --fov and --aspect from the MO2 profile's own ini instead of "
                        "guessing the frame shape. Needs --instance/--profile (or the same flags "
                        "passed through after --)")
    p.add_argument("--instance", default=None, help="MO2 instance dir, for --from-profile")
    p.add_argument("--profile", default=None, help="MO2 profile name, for --from-profile")
    p.add_argument("--game", default=None, help="game dir, for --from-profile")
    p.add_argument("--my-games", default=None,
                   help="Documents\\My Games\\<game> dir, when LocalSettings is off")
    # NOTE: the pass-through list is split off by hand in main() before argparse ever sees it.
    # A `nargs="*"` positional beside a required one is ambiguous, and argparse resolves the
    # ambiguity by refusing the whole command line.


def main(argv=None):
    ap = argparse.ArgumentParser(
        prog="shotplan.py", description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("plan", help="choose a camera for a set of subjects")
    add_common(p)
    p.add_argument("--fov-map", type=float, default=100.0,
                   help="fov of the CLEARANCE probe renders (default 100; four of them tile 360 deg)")
    p.add_argument("--map-yaws", default="0,90,180,270",
                   help="the probe yaws that tile the circle (default 0,90,180,270)")
    p.add_argument("--edge-margin", type=float, default=2.0,
                   help="degrees of each probe frustum left unsampled at the edges (default 2)")
    p.add_argument("--bearing-step", type=float, default=5.0,
                   help="angular resolution of the clearance map, degrees (default 5)")
    p.add_argument("--elevations", default="-4,0,4",
                   help="elevations sampled per bearing, degrees (default -4,0,4)")
    p.add_argument("--standoff-min", type=float, default=250.0)
    p.add_argument("--standoff-max", type=float, default=1200.0)
    p.add_argument("--standoff-step", type=float, default=50.0)
    p.add_argument("--fill", type=float, default=0.55,
                   help="how much of the half-frame the subjects should reach (default 0.55)")
    p.add_argument("--comfort", type=float, default=200.0,
                   help="clearance beyond the camera that scores full marks (default 200)")
    p.add_argument("--nav-probe-max", type=int, default=250,
                   help="cap on batched --nav-at/--inside-at probe points (two flags per point "
                        "since the inside test; 250 keeps the command line under Windows's "
                        "limit); anything dropped is REPORTED")
    p.add_argument("--top", type=int, default=5)
    p.add_argument("--dump-survivors", action="store_true",
                   help="with --dump, dump only the nav-probed survivors instead of everything")
    p.add_argument("--verify-candidates", type=int, default=3,
                   help="how far down the ranked list the final check may walk when the top "
                        "candidate fails (default 3, one render each). Exhausting them is a "
                        "SAMPLED search, never a proof that no camera exists")
    p.add_argument("--emit-refused", action="store_true",
                   help="write the worklist even when the final check REFUSED the camera (it is "
                        "labelled REFUSED in the file). Without this a refused camera writes no "
                        "worklist at all")
    p.add_argument("--no-verify", action="store_true",
                   help="skip the final check. The result is then a PROPOSAL with its verification "
                        "MISSING: status `unverified`, exit code 2, and the worklist label carries "
                        "UNVERIFIED. It never means the check passed")
    p.add_argument("--worklist", default=None, help="write the chosen shot as a viewprobe worklist")
    p.add_argument("--json", default=None, help="write the full result as JSON")
    p.add_argument("--dump", default=None,
                   help="write EVERY candidate and its verdict as JSON -- what you need to ask "
                        "why a particular position was or was not offered")
    p.add_argument("--label", default=None, help="worklist label")
    p.set_defaults(func=cmd_plan)

    p = sub.add_parser("check", help="judge a camera you already have (the positive control)")
    add_common(p)
    p.add_argument("--eye", required=True, metavar="X,Y,Z", help="the camera to judge")
    p.add_argument("--refs-radius", type=float, default=96.0)
    p.add_argument("--show-refs", action="store_true", help="print what is around the camera")
    p.add_argument("--json", default=None,
                   help="write the verdict, its pose id and its per-target verification as JSON")
    p.set_defaults(func=cmd_check)

    p = sub.add_parser("frame", help="read the game's own resolution and fov from its ini")
    p.add_argument("--instance", required=True, help="MO2 instance dir")
    p.add_argument("--profile", required=True, help="MO2 profile name")
    p.add_argument("--game", default=None)
    p.add_argument("--my-games", default=None,
                   help="Documents\\My Games\\<game> dir, when LocalSettings is off")
    p.add_argument("--explain", action="store_true", help="print how the angle is derived and measured")
    p.add_argument("--json", default=None)
    p.set_defaults(func=cmd_frame)

    p = sub.add_parser("devbench", help="turn a worklist into DevBench camera args")
    p.add_argument("worklist")
    p.add_argument("--shot", type=int, default=None, help="just this shot (1-based)")
    p.add_argument("--port", type=int, default=8920)
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_devbench)

    argv = list(sys.argv[1:] if argv is None else argv)
    passthrough = []
    if "--" in argv:
        i = argv.index("--")
        argv, passthrough = argv[:i], argv[i + 1:]
    args = ap.parse_args(argv)
    args.passthrough = passthrough
    if isinstance(getattr(args, "aspect", None), str):
        a = args.aspect.replace("x", ":").split(":")
        args.aspect = float(a[0]) / float(a[1]) if len(a) == 2 else float(a[0])
    if hasattr(args, "map_yaws"):
        args.map_yaws = [float(v) for v in args.map_yaws.split(",")]
    return args.func(args) or 0


if __name__ == "__main__":
    # The mode is what makes two shotplan runs comparable: `plan` shells out to viewprobe
    # repeatedly and `frame` does arithmetic, and averaging the two would describe neither.
    _mode = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else None
    with _run("shotplan", mode=_mode) as _r:
        _code = main()
        if _code:
            _r.refuse(f"exit code {_code}", exit_code=_code)
        else:
            _r.tally(ok=1)
    sys.exit(_code)
