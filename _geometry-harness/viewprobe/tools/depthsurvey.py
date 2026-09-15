#!/usr/bin/env python3
"""depthsurvey.py -- an outward six-face depth survey FROM a declared world point, a lookup that
says whether that survey admits or blocks a camera, and a true-scale plan-view figure.

It exists to answer one question, and it is deliberately not a planner:

    does an outward depth survey taken from a declared surface point admit every camera the real
    planner verified, and refuse the one it refused?

Nothing here is a camera search, a score or an acceptance rule. `shotplan.py` stays frozen; this
script IMPORTS its conventions (`camera_basis`, `pixel_for_angles`, `surface_tolerance`,
`bearing_deg`, the pick regexes, the fingerprint regex) rather than restating them, so a survey
verdict and a verification verdict are built from the same arithmetic by construction.

What a survey is
----------------
Six renders from one eye, each writing its finished depth buffer with viewprobe's `--depth-dump`:
four side faces (`--pitch 0 --fov 90` at yaws 0/90/180/270, optionally rotated by
`--face-yaw-offset`) and two pole faces (`--pitch -89` / `--pitch 89`, `--fov 92`). The poles are
tilted 1 degree off vertical because the renderer clamps a camera's elevation to +-89.5 -- a LookAt
with world up degenerates at the pole (TexturedScene.cs:1262) -- and widened by 2 degrees so the
six frusta still close over the sphere: a side face reaches 45 degrees of elevation at its centre,
a pole face reaches down to 89 - 46 = 43.

Reading a dump, and why each term is there
------------------------------------------
The file is `size*size` little-endian float32 WINDOW depth in [0,1] in GL row order (row 0 is the
BOTTOM image row, so image row `iy` is buffer row `size-1-iy`), 1.0 meaning background. It is the
exact array `--pick` unprojects, which is what makes the self-check below a real control rather
than a restatement.

    view_dist  = 2*n*f / (f + n - (2w-1)*(f - n))        n = 15, f = 131072
    ray_dist   = view_dist * sqrt(1 + (ndc_x*t)^2 + (ndc_y*t)^2)          t = tan(fov/2)
    world      = eye + (fwd + right*ndc_x*t + up*ndc_y*t) * view_dist

`view_dist` is along the VIEW AXIS; `ray_dist` is along the pixel's own ray, which is the number
`--pick` prints as "N u from the eye" and the number a camera-to-point distance must be compared
against. Mixing them is a cos(off-axis angle) error -- 8% at the corner of a 90 degree face.

What the survey can and cannot see (from the renderer's source, not from hope)
-----------------------------------------------------------------------------
The near plane is 15 u and the opaque pass culls back faces, so a survey shot from inside a solid
sees nothing for 15 u and then meets that solid's far face from the inside, which is not drawn.
The half-space behind a face therefore reads OPEN. Those are false admits, and the retained
verification catches every one at the cost of one render. False discards -- a camera the survey
calls blocked and the verification confirms -- are the ones nothing downstream can recover, which
is why `lookup` reports them as their own count.

Subcommands
-----------
  survey    render the six faces from `point + offset` and self-check every dump against
            viewprobe's own `--pick` answers at five pixels per face
  lookup    ADMITS / BLOCKS a camera against one or more surveys, with the tolerance taken from
            `shotplan.surface_tolerance` at the planner's defaults
  figure    a true-scale plan view: free region per elevation slice, the point, the cameras
  selftest  arithmetic-only checks on synthetic depth arrays (see its own banner)

Every entry point opens a run through `scripts/runlog.py` per standards/HOUSECARL_RUN_EVENTS.md;
each viewprobe invocation is a child run whose real exit code, argv and full output are kept.
"""

from __future__ import annotations

import argparse
import array
import datetime
import importlib.util
import json
import math
import os
import struct
import subprocess
import sys
import time
import types

HERE = os.path.dirname(os.path.abspath(__file__))
VIEWPROBE_DIR = os.path.dirname(HERE)
REPO = os.path.abspath(os.path.join(VIEWPROBE_DIR, "..", ".."))

NEAR = 15.0
FAR = 131072.0
BACKGROUND = 0.999999           # viewprobe's own test: `if (d >= 0.999999f)` -> "background"
DEFAULT_SIZE = 512
SIDE_FOV = 90.0
POLE_FOV = 92.0
POLE_PITCH = 89.0
DEFAULT_VP = os.path.join(VIEWPROBE_DIR, "bin", "Release",
                          "net9.0-windows10.0.19041.0", "viewprobe.exe")

# The planner's own defaults for the surface tolerance, quoted from shotplan's argparse so the
# survey's band is the SAME band the verification uses. Not tunable here on purpose.
TOL_ARGS = types.SimpleNamespace(surface_tol=2.0, surface_obliquity=1.73)


# --------------------------------------------------------------------------------- loading --

def _load_by_path(name, path):
    """Import a module by absolute path. The tools directory is not on the import path and this
    script must not require one to be set up."""
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {name} from {path}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# shotplan is REQUIRED: its conventions are the point of importing rather than restating them.
SHOTPLAN = _load_by_path("housecarl_shotplan", os.path.join(HERE, "shotplan.py"))
camera_basis = SHOTPLAN.camera_basis
pixel_for_angles = SHOTPLAN.pixel_for_angles
surface_tolerance = SHOTPLAN.surface_tolerance
bearing_deg = SHOTPLAN.bearing_deg
PICK_HIT = SHOTPLAN.PICK_HIT
PICK_BG = SHOTPLAN.PICK_BG
CONFIG_FINGERPRINT = SHOTPLAN.CONFIG_FINGERPRINT


def _runlog():
    """The run-event adapter, loaded by path; absent, the whole thing degrades to a no-op and the
    script behaves exactly as it would without it (shotplan.py does the same)."""
    cand = os.path.join(REPO, "scripts", "runlog.py")
    if not os.path.isfile(cand):
        return None
    try:
        return _load_by_path("housecarl_runlog", cand)
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


def _now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


# -------------------------------------------------------------------------------- geometry --

def vsub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def vadd(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def vdot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def vlen(a):
    return math.sqrt(vdot(a, a))


def vscale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def window_to_view(w: float) -> float:
    """Window depth in [0,1] -> distance along the VIEW AXIS. The standard perspective inverse
    with the renderer's own near and far (TexturedScene.cs:1248)."""
    zn = w * 2.0 - 1.0
    return 2.0 * NEAR * FAR / (FAR + NEAR - zn * (FAR - NEAR))


def view_to_window(view: float) -> float:
    """The inverse, used to ENCODE synthetic frames in the arithmetic-only selftest."""
    zn = (FAR + NEAR - 2.0 * NEAR * FAR / view) / (FAR - NEAR)
    return (zn + 1.0) / 2.0


def ndc_of_pixel(ix: int, iy: int, size: int):
    """The NDC of a pixel's CENTRE. Image coordinates, top-left origin -- the numbers an image
    viewer shows and the numbers `--pick` takes."""
    return ((ix + 0.5) / size * 2.0 - 1.0,
            ((size - 1 - iy) + 0.5) / size * 2.0 - 1.0)


def pixel_of_ndc(nx: float, ny: float, size: int):
    """The pixel whose centre is nearest this NDC, clamped into the frame."""
    ix = int(round((nx + 1.0) / 2.0 * size - 0.5))
    gy = int(round((ny + 1.0) / 2.0 * size - 0.5))
    iy = size - 1 - gy
    return (min(max(ix, 0), size - 1), min(max(iy, 0), size - 1))


def ray_scale(nx: float, ny: float, t: float) -> float:
    """|ray| / |view axis component| for the pixel at this NDC."""
    return math.sqrt(1.0 + (nx * t) ** 2 + (ny * t) ** 2)


# ------------------------------------------------------------------------------------ face --

class Face:
    """One render of a survey: a pose, a frustum and (once loaded) its depth buffer.

    The buffer is held in GL row order exactly as written, and every read flips the row inside
    this class rather than in a caller's head.
    """

    def __init__(self, name, yaw, pitch, fov, size, eye, dump=None, buf=None):
        self.name = name
        self.yaw = float(yaw)
        self.pitch = float(pitch)
        self.fov = float(fov)
        self.size = int(size)
        self.eye = tuple(float(v) for v in eye)
        self.dump = dump
        self.buf = buf                      # array('f') in GL row order, or None until loaded
        self.f, self.r, self.u = camera_basis(self.yaw, self.pitch)
        self.t = math.tan(math.radians(self.fov) / 2.0)

    # -- construction ----------------------------------------------------------------------

    @classmethod
    def from_record(cls, rec, base_dir=None):
        dump = rec.get("dump")
        if dump and base_dir and not os.path.isabs(dump):
            dump = os.path.join(base_dir, dump)
        return cls(rec["name"], rec["yaw"], rec["pitch"], rec["fov"], rec["size"],
                   rec["eye"], dump=dump)

    @classmethod
    def synthetic(cls, name, yaw, pitch, fov, size, eye, fill_view=None):
        """A face whose buffer is built in memory -- the selftest's instrument, never a survey."""
        f = cls(name, yaw, pitch, fov, size, eye, buf=array.array("f", [1.0] * (size * size)))
        if fill_view is not None:
            f.fill_view(fill_view)
        return f

    def fill_view(self, view_dist):
        w = view_to_window(view_dist)
        for i in range(self.size * self.size):
            self.buf[i] = w

    def set_view(self, ix, iy, view_dist):
        self.buf[(self.size - 1 - iy) * self.size + ix] = (
            1.0 if view_dist is None else view_to_window(view_dist))

    def load(self):
        """Read the dump. A wrong-sized file is a refusal, never a silent partial read."""
        if self.buf is not None:
            return self
        if not self.dump or not os.path.isfile(self.dump):
            raise SystemExit(f"depthsurvey: face '{self.name}' has no depth dump at {self.dump}")
        want = self.size * self.size * 4
        got = os.path.getsize(self.dump)
        if got != want:
            raise SystemExit(f"depthsurvey: {self.dump} is {got} bytes; a {self.size}x{self.size} "
                             f"float32 dump is {want}")
        a = array.array("f")
        with open(self.dump, "rb") as fh:
            a.frombytes(fh.read())
        if sys.byteorder != "little":
            a.byteswap()
        self.buf = a
        return self

    # -- reading ---------------------------------------------------------------------------

    def window_at(self, ix, iy):
        return self.buf[(self.size - 1 - iy) * self.size + ix]

    def is_background(self, ix, iy):
        return self.window_at(ix, iy) >= BACKGROUND

    def ray_at(self, ix, iy):
        """Distance from the eye along THIS pixel's own ray, or math.inf for background."""
        w = self.window_at(ix, iy)
        if w >= BACKGROUND:
            return math.inf
        nx, ny = ndc_of_pixel(ix, iy, self.size)
        return window_to_view(w) * ray_scale(nx, ny, self.t)

    def ray_eroded(self, ix, iy):
        """The MINIMUM ray distance over the 3x3 neighbourhood, background counting as +inf.

        One pixel of erosion is the honest reading of a survey near an obstruction edge: the
        survey's pixel-centre ray and a verification's pixel-centre ray are different lines, and
        the eroded read is the conservative one -- it can only turn an admit into a discard,
        never the reverse, which is exactly the direction that must be measured.
        """
        best = math.inf
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                jx, jy = ix + dx, iy + dy
                if 0 <= jx < self.size and 0 <= jy < self.size:
                    best = min(best, self.ray_at(jx, jy))
        return best

    def world_at(self, ix, iy):
        """The world position of the hit under this pixel, or None for background."""
        w = self.window_at(ix, iy)
        if w >= BACKGROUND:
            return None
        nx, ny = ndc_of_pixel(ix, iy, self.size)
        v = window_to_view(w)
        d = (self.f[0] + self.r[0] * nx * self.t + self.u[0] * ny * self.t,
             self.f[1] + self.r[1] * nx * self.t + self.u[1] * ny * self.t,
             self.f[2] + self.r[2] * nx * self.t + self.u[2] * ny * self.t)
        return vadd(self.eye, vscale(d, v))

    # -- projection ------------------------------------------------------------------------

    def project(self, direction, slack_px=0.0):
        """Where a world DIRECTION lands in this face, or None if the frustum does not hold it.

        `slack_px` admits a direction up to that many pixels outside the frame, clamped to the
        edge pixel -- used only as a second pass, so a bearing that falls exactly on a face
        boundary is read at the outermost pixel and SAID to be, instead of vanishing.
        """
        zf = vdot(direction, self.f)
        if zf <= 1e-9:
            return None
        nx = vdot(direction, self.r) / (zf * self.t)
        ny = vdot(direction, self.u) / (zf * self.t)
        edge = max(abs(nx), abs(ny))
        slack = 1.0 + slack_px * 2.0 / self.size
        if edge >= slack:
            return None
        ix, iy = pixel_of_ndc(nx, ny, self.size)
        return {"face": self.name, "ndc": (nx, ny), "pixel": (ix, iy),
                "marginPx": (1.0 - edge) * self.size / 2.0,
                "edgeClamped": edge >= 1.0}

    def record(self):
        return {"name": self.name, "yaw": self.yaw, "pitch": self.pitch, "fov": self.fov,
                "size": self.size, "eye": list(self.eye), "dump": self.dump}


def face_plan(yaw_offset=0.0, size=DEFAULT_SIZE, eye=(0.0, 0.0, 0.0)):
    """The six poses of a survey. Side faces first, then up, then down."""
    out = []
    for k in range(4):
        y = (yaw_offset + 90.0 * k) % 360.0
        out.append(Face(f"side-{y:g}", y, 0.0, SIDE_FOV, size, eye))
    out.append(Face("up", yaw_offset % 360.0, -POLE_PITCH, POLE_FOV, size, eye))
    out.append(Face("down", yaw_offset % 360.0, POLE_PITCH, POLE_FOV, size, eye))
    return out


COVERAGE_NOTE = (
    f"four side faces (pitch 0, fov {SIDE_FOV:g}) cover elevation +-45 at their centres; the two "
    f"pole faces (pitch -+{POLE_PITCH:g}, fov {POLE_FOV:g}) reach down to "
    f"{POLE_PITCH - POLE_FOV / 2.0:g} degrees of elevation, so the six frusta overlap by "
    f"{45.0 - (POLE_PITCH - POLE_FOV / 2.0):g} degrees and close over the sphere. The poles are "
    f"tilted off vertical because the renderer clamps elevation to +-89.5")


# ---------------------------------------------------------------------------------- survey --

class Survey:
    """A loaded survey: the declared point, the eye it was actually shot from, and six faces."""

    def __init__(self, declared, offset, origin, faces, meta=None, label=None, path=None):
        self.declared = tuple(float(v) for v in declared)
        self.offset = tuple(float(v) for v in offset)
        self.origin = tuple(float(v) for v in origin)
        self.faces = faces
        self.meta = meta or {}
        self.label = label
        self.path = path

    @classmethod
    def load(cls, path, label=None):
        path = os.path.abspath(path)
        with open(path, "r", encoding="utf-8") as fh:
            doc = json.load(fh)
        base = os.path.dirname(path)
        faces = [Face.from_record(r, base) for r in doc["faces"]]
        for f in faces:
            f.load()
        return cls(doc["declaredPoint"], doc["offset"], doc["origin"], faces, meta=doc,
                   label=label or doc.get("label") or os.path.basename(base), path=path)

    def select(self, direction):
        """The face holding this direction with the largest inward pixel margin.

        A direction exactly on a face boundary is in NO frustum (|ndc| == 1 is outside), which is
        a hole of measure zero that an integer bearing sweep lands in every 90 degrees. The second
        pass reads it at the outermost pixel and flags `edgeClamped`, so it is reported rather
        than silently dropped -- and a lookup that lands there says so in its own row.
        """
        best = None
        for f in self.faces:
            p = f.project(direction)
            if p and (best is None or p["marginPx"] > best[1]["marginPx"]):
                best = (f, p)
        if best is not None:
            return best
        for f in self.faces:
            p = f.project(direction, slack_px=0.5)
            if p and (best is None or p["marginPx"] > best[1]["marginPx"]):
                best = (f, p)
        return best

    def read(self, direction, erode=False):
        """{face, pixel, ray, world, background, ...} for a direction, or None with no coverage."""
        sel = self.select(direction)
        if sel is None:
            return None
        face, p = sel
        ix, iy = p["pixel"]
        ray = face.ray_eroded(ix, iy) if erode else face.ray_at(ix, iy)
        return {"face": face.name, "faceFov": face.fov, "faceSize": face.size,
                "pixel": [ix, iy], "marginPx": p["marginPx"], "edgeClamped": p["edgeClamped"],
                "ray": ray, "background": math.isinf(ray),
                "pixelBackground": face.is_background(ix, iy),
                "world": face.world_at(ix, iy)}

    def lookup(self, camera, erode=False):
        """Does this survey admit a camera at `camera`?

        Two distances, deliberately both reported:
          D  |camera - DECLARED point|  -- what a verification's ray has to reach, so the number
             the survey depth is compared against
          |camera - origin|             -- how far the survey's own ray travelled, which differs
             by the offset and is the term an offset variant is testing
        """
        d = vsub(camera, self.origin)
        need = vlen(vsub(camera, self.declared))
        rd = self.read(d, erode=erode)
        row = {"camera": [round(v, 2) for v in camera],
               "declared": [round(v, 2) for v in self.declared],
               "origin": [round(v, 2) for v in self.origin],
               "offset": [round(v, 2) for v in self.offset],
               "needed": round(need, 2), "distFromOrigin": round(vlen(d), 2),
               "bearing": round(bearing_deg(d[0], d[1]), 2),
               "elevation": round(math.degrees(math.asin(d[2] / vlen(d))), 2) if vlen(d) else 0.0,
               "erode": bool(erode)}
        if rd is None:
            row.update(verdict="no-coverage", survey=None, tolerance=None, delta=None,
                       why="no survey face holds this direction")
            return row
        tol = surface_tolerance(need, rd["faceFov"], rd["faceSize"], TOL_ARGS)
        s = rd["ray"]
        row.update(face=rd["face"], pixel=rd["pixel"], marginPx=round(rd["marginPx"], 1),
                   edgeClamped=rd["edgeClamped"], tolerance=round(tol, 2),
                   survey=None if math.isinf(s) else round(s, 2),
                   delta=None if math.isinf(s) else round(s - need, 2),
                   hit=None if rd["world"] is None else [round(v, 1) for v in rd["world"]])
        if not math.isinf(s) and s < need - tol:
            row.update(verdict="blocked", why=(
                f"survey depth {s:.0f} u stands in front of the camera at {need:.0f} u "
                f"(tolerance {tol:.1f} u)"))
        elif math.isinf(s):
            row.update(verdict="admissible-background", why=(
                "the survey ray found no depth-writing geometry in this direction at all"))
        elif s > need + tol:
            row.update(verdict="admissible-beyond", why=(
                f"the survey ray ran to {s:.0f} u, {s - need:.0f} u past the camera at "
                f"{need:.0f} u (tolerance {tol:.1f} u)"))
        else:
            row.update(verdict="admissible-at", why=(
                f"the survey ray stopped at {s:.0f} u, within {tol:.1f} u of the camera at "
                f"{need:.0f} u -- the camera stands ON the first surface in this direction"))
        return row


def admissible(verdict):
    return verdict.startswith("admissible")


# ------------------------------------------------------------------------ running viewprobe --

def _child_run(parent, job_id, argv, log_path, env=None):
    """One viewprobe invocation as a child run: real exit code, full output and exact argv kept.

    Deliberately NOT `Runner`: the survey self-checks each face against its own dump before the
    next render, and the loop needs the child's stdout in hand at that moment.
    """
    kw = dict(mode="child", stage_source="external-opaque")
    if parent is not None and getattr(parent, "run_id", None):
        kw["parent_run_id"] = parent.run_id
    t0 = time.time()
    with _run(job_id, **kw) as child:
        proc = subprocess.run(argv, capture_output=True, text=True,
                              encoding="utf-8", errors="replace",
                              env={**os.environ, **(env or {})})
        secs = time.time() - t0
        text = (proc.stdout or "") + (
            ("\n---- stderr ----\n" + proc.stderr) if proc.stderr else "")
        if log_path:
            os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
            with open(log_path, "w", encoding="utf-8") as fh:
                fh.write("$ " + " ".join(argv) + "\n"
                         f"(exit {proc.returncode}, {secs:0.1f}s)\n\n" + text)
            child.artifact(log_path)
        # The renderer's own exit code survives the wrapper: 2 is a refusal ("you did not tell me
        # where", "unknown flag"), not a crash, and the caller must be able to tell them apart.
        if proc.returncode == 0:
            child.tally(ok=1)
            child.complete()
        else:
            child.refuse(f"exit code {proc.returncode}", proc.returncode)
    return proc.returncode, text, secs


def parse_picks(text):
    """{(ix, iy): (x, y, z, dist) or None} from viewprobe's own pick lines."""
    out = {}
    for line in text.splitlines():
        m = PICK_HIT.match(line)
        if m:
            out[(int(m[1]), int(m[2]))] = (float(m[3]), float(m[4]), float(m[5]), float(m[6]))
            continue
        m = PICK_BG.match(line)
        if m:
            out[(int(m[1]), int(m[2]))] = None
    return out


def selfcheck_pixels(size):
    """The centre and four well-off-centre pixels: the corners of the frame are where an error in
    the basis or in the ray/view distinction is LARGEST, and the centre is where it is zero."""
    return [(size // 2, size // 2),
            (size // 8, size // 8), (7 * size // 8, size // 8),
            (size // 8, 7 * size // 8), (7 * size // 8, 7 * size // 8)]


# ---------------------------------------------------------------------------- cmd: survey ---

def cmd_survey(args):
    point = parse_triple(args.point, "--point")
    offset = parse_triple(args.offset, "--offset") if args.offset else (0.0, 0.0, 0.0)
    origin = vadd(point, offset)
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    log_dir = os.path.join(out_dir, "logs")
    faces = face_plan(args.face_yaw_offset, args.size, origin)
    picks = selfcheck_pixels(args.size)
    vp = os.path.abspath(args.vp or DEFAULT_VP)
    if not os.path.isfile(vp):
        raise SystemExit(f"depthsurvey: no viewprobe at {vp} (--vp)")

    doc = {
        "tool": "depthsurvey", "schema": 1, "created": _now(),
        "command": "survey", "label": args.label, "cell": args.cell,
        "declaredPoint": [round(v, 3) for v in point],
        "offset": [round(v, 3) for v in offset],
        "origin": [round(v, 3) for v in origin],
        "size": args.size, "faceYawOffset": args.face_yaw_offset,
        "near": NEAR, "far": FAR, "background": BACKGROUND,
        "coverage": COVERAGE_NOTE,
        "viewprobe": vp,
        "viewprobeMtime": datetime.datetime.fromtimestamp(
            os.path.getmtime(vp), datetime.timezone.utc).isoformat(timespec="seconds"),
        "instance": args.instance, "profile": args.profile, "game": args.game,
        "configFingerprint": None,
        "selfCheckTolerance": {"rayDistanceU": args.check_dist, "positionPerAxisU": args.check_pos},
        "faces": [], "selfCheck": {"pass": 0, "fail": 0, "byFace": {}},
        "renders": {"ok": 0, "failed": 0, "seconds": 0.0},
    }
    json_path = os.path.join(out_dir, "survey.json")

    print(f"depthsurvey survey: {args.cell} from {fmt3(origin)} "
          f"(declared {fmt3(point)} + offset {fmt3(offset)}), {args.size}x{args.size}, "
          f"{len(faces)} faces", flush=True)

    fails = 0
    with _run("depthsurvey", mode="survey", stage_source="counted", heartbeat=True) as r:
        r.feature("faces", len(faces), "faces")
        r.feature("size", args.size, "px")
        r.stage("faces", "rendering survey faces", len(faces), "faces")
        for i, face in enumerate(faces):
            dump = os.path.join(out_dir, f"face-{face.name}.f32")
            face.dump = dump
            argv = [vp, args.cell,
                    "--eye", f"{origin[0]:.1f},{origin[1]:.1f},{origin[2]:.1f}",
                    "--yaw", f"{face.yaw:g}", "--pitch", f"{face.pitch:g}",
                    "--fov", f"{face.fov:g}", "--size", str(args.size),
                    "--msaa", "0", "--nav", "off",
                    "--out", out_dir, "--depth-dump", dump]
            for (ix, iy) in picks:
                argv += ["--pick", f"{ix},{iy}"]
            if i == 0:
                argv.append("--config-identity")
            for flag, val in (("--instance", args.instance), ("--profile", args.profile),
                              ("--game", args.game)):
                if val:
                    argv += [flag, val]
            argv += list(args.passthrough or [])

            log_path = os.path.join(log_dir, f"face-{face.name}.txt")
            rc, text, secs = _child_run(r, f"viewprobe-{face.name}", argv, log_path)
            rec = face.record()
            rec.update({"exitCode": rc, "seconds": round(secs, 2), "argv": argv,
                        "log": log_path, "dumpBytes": (os.path.getsize(dump)
                                                       if os.path.isfile(dump) else None)})
            doc["renders"]["seconds"] += secs
            m = CONFIG_FINGERPRINT.search(text)
            rec["configFingerprint"] = m[1] if m else None
            if rec["configFingerprint"] and not doc["configFingerprint"]:
                doc["configFingerprint"] = rec["configFingerprint"]

            if rc != 0:
                doc["renders"]["failed"] += 1
                rec["selfCheck"] = []
                rec["selfCheckPass"] = rec["selfCheckFail"] = 0
                rec["why"] = f"viewprobe exited {rc}; see {log_path}"
                print(f"  face {face.name:8} EXIT {rc}  ({secs:.0f}s)  -- see {log_path}")
                doc["faces"].append(rec)
                r.advance()
                r.tally(failed=1)
                continue

            doc["renders"]["ok"] += 1
            rows = face_selfcheck(face, text, picks, args.check_dist, args.check_pos)
            rec["selfCheck"] = rows
            rec["selfCheckPass"] = sum(1 for x in rows if x["pass"])
            rec["selfCheckFail"] = sum(1 for x in rows if not x["pass"])
            doc["selfCheck"]["pass"] += rec["selfCheckPass"]
            doc["selfCheck"]["fail"] += rec["selfCheckFail"]
            doc["selfCheck"]["byFace"][face.name] = {"pass": rec["selfCheckPass"],
                                                     "fail": rec["selfCheckFail"]}
            fails += rec["selfCheckFail"]
            doc["faces"].append(rec)
            print(f"  face {face.name:8} yaw {face.yaw:6.1f} pitch {face.pitch:6.1f} "
                  f"fov {face.fov:g}  exit 0  {secs:5.0f}s  self-check "
                  f"{rec['selfCheckPass']}/{len(rows)} pass", flush=True)
            for x in rows:
                if not x["pass"]:
                    print(f"      SELF-CHECK FAIL {face.name} pixel {x['pixel']}: {x['why']}")
            r.advance()
            r.tally(ok=1, inconclusive=rec["selfCheckFail"])
        doc["selfCheckFailed"] = doc["selfCheck"]["fail"]
        doc["renders"]["seconds"] = round(doc["renders"]["seconds"], 1)
        with open(json_path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=2)
        r.artifact(json_path)
        if doc["selfCheck"]["fail"]:
            r.note(f"{doc['selfCheck']['fail']} pick-vs-dump self-checks FAILED")

    print(f"\n  survey.json: {json_path}")
    print(f"  renders ok {doc['renders']['ok']}, failed {doc['renders']['failed']}, "
          f"{doc['renders']['seconds']:.0f}s total")
    print(f"  fingerprint: {doc['configFingerprint']}")
    if doc["selfCheck"]["fail"]:
        print("\n" + "!" * 78)
        print(f"!! SELF-CHECK FAILED on {doc['selfCheck']['fail']} of "
              f"{doc['selfCheck']['fail'] + doc['selfCheck']['pass']} pixels. The dump lookup "
              f"disagrees with viewprobe's own --pick.")
        print("!! Per face: " + ", ".join(f"{k} {v['pass']}P/{v['fail']}F"
                                          for k, v in doc["selfCheck"]["byFace"].items()))
        print("!! A lookup built on these faces is NOT trustworthy until this is explained.")
        print("!" * 78)
    else:
        print(f"  self-check: {doc['selfCheck']['pass']}/{doc['selfCheck']['pass']} pixels agree "
              f"with viewprobe's own picks (<= {args.check_dist} u distance, "
              f"<= {args.check_pos} u per axis)")
    return 0 if doc["renders"]["failed"] == 0 else 1


def face_selfcheck(face, text, picks, tol_dist, tol_pos):
    """Compare the dump's own answer with viewprobe's `--pick` answer at each picked pixel.

    This is the control that separates "the survey says the space is blocked" from "the lookup
    arithmetic is wrong". Both readings come from the SAME finished frame, so a disagreement can
    only be the decode -- the basis, the row order, the linearisation or the ray/view distinction.
    """
    face.load()
    printed = parse_picks(text)
    rows = []
    for (ix, iy) in picks:
        got = printed.get((ix, iy), "absent")
        w = face.window_at(ix, iy)
        dump_bg = w >= BACKGROUND
        row = {"pixel": [ix, iy], "window": round(w, 9), "dumpBackground": dump_bg,
               "dumpRay": None if dump_bg else round(face.ray_at(ix, iy), 3),
               "dumpWorld": None if dump_bg else [round(v, 2) for v in face.world_at(ix, iy)],
               "printed": None, "printedBackground": None,
               "distDelta": None, "posDelta": None, "pass": False, "why": ""}
        if got == "absent":
            row["why"] = "viewprobe printed no pick line for this pixel"
            rows.append(row)
            continue
        if got is None:
            row["printedBackground"] = True
            row["pass"] = dump_bg
            row["why"] = ("both background" if dump_bg else
                          f"viewprobe printed background; the dump holds {w:.6f} "
                          f"(< {BACKGROUND}), a hit at {face.ray_at(ix, iy):.1f} u")
            rows.append(row)
            continue
        row["printedBackground"] = False
        row["printed"] = {"pos": [got[0], got[1], got[2]], "dist": got[3]}
        if dump_bg:
            row["why"] = (f"viewprobe printed a hit at {got[3]:.0f} u; the dump holds background "
                          f"({w:.6f})")
            rows.append(row)
            continue
        dd = abs(face.ray_at(ix, iy) - got[3])
        world = face.world_at(ix, iy)
        pd = max(abs(world[k] - got[k]) for k in range(3))
        row["distDelta"] = round(dd, 3)
        row["posDelta"] = round(pd, 3)
        row["pass"] = dd <= tol_dist and pd <= tol_pos
        row["why"] = (f"dump {face.ray_at(ix, iy):.2f} u at {fmt3(world)} vs printed "
                      f"{got[3]:.0f} u at {got[0]:.0f},{got[1]:.0f},{got[2]:.0f} "
                      f"(d{dd:.2f} u, pos {pd:.2f} u/axis)")
        rows.append(row)
    return rows


# ---------------------------------------------------------------------------- cmd: lookup ---

def cmd_lookup(args):
    surveys = load_surveys(args.survey, args.variant_label)
    cameras = [parse_camera(c) for c in args.camera]
    erodes = [bool(int(v)) for v in str(args.erode).replace(" ", "").split(",") if v != ""]
    if not erodes:
        erodes = [False]

    doc = {"tool": "depthsurvey", "schema": 1, "created": _now(), "command": "lookup",
           "tolerance": {"surfaceTol": TOL_ARGS.surface_tol,
                         "surfaceObliquity": TOL_ARGS.surface_obliquity,
                         "from": "shotplan.surface_tolerance, planner defaults, called with the "
                                 "SURVEY face's own fov and size"},
           "surveys": [{"variant": s.label, "path": s.path,
                        "declaredPoint": list(s.declared), "offset": list(s.offset),
                        "origin": list(s.origin), "cell": s.meta.get("cell"),
                        "size": s.meta.get("size"), "faceYawOffset": s.meta.get("faceYawOffset"),
                        "configFingerprint": s.meta.get("configFingerprint"),
                        "selfCheckFailed": s.meta.get("selfCheck", {}).get("fail")}
                       for s in surveys],
           "rows": [], "counts": {}}

    bad_surveys = [s.label for s in surveys if s.meta.get("selfCheck", {}).get("fail")]
    if bad_surveys:
        print("!" * 78)
        print("!! survey(s) with FAILED pick-vs-dump self-checks are being read: "
              + ", ".join(bad_surveys))
        print("!! their verdicts below are reported, but they are NOT trustworthy.")
        print("!" * 78)

    with _run("depthsurvey", mode="lookup", stage_source="counted") as r:
        total = len(surveys) * len(cameras) * len(erodes)
        r.feature("lookups", total, "lookups")
        r.stage("lookups", "looking cameras up", total, "lookups")
        for s in surveys:
            for cam in cameras:
                for er in erodes:
                    row = s.lookup(cam["pos"], erode=er)
                    row["variant"] = s.label
                    row["label"] = cam["label"]
                    row["expected"] = cam["expected"]
                    if cam["expected"] == "verified":
                        row["agreement"] = ("FALSE DISCARD" if row["verdict"] == "blocked"
                                            else "admits the verified camera")
                    elif cam["expected"] == "refused":
                        row["agreement"] = ("refuses the refused camera"
                                            if row["verdict"] == "blocked" else "FALSE ADMIT")
                    else:
                        row["agreement"] = None
                    doc["rows"].append(row)
                    r.advance()
        fd = sum(1 for x in doc["rows"] if x["agreement"] == "FALSE DISCARD")
        fa = sum(1 for x in doc["rows"] if x["agreement"] == "FALSE ADMIT")
        doc["counts"] = {"rows": len(doc["rows"]),
                         "blocked": sum(1 for x in doc["rows"] if x["verdict"] == "blocked"),
                         "admissible": sum(1 for x in doc["rows"] if admissible(x["verdict"])),
                         "noCoverage": sum(1 for x in doc["rows"]
                                           if x["verdict"] == "no-coverage"),
                         "falseDiscards": fd, "falseAdmits": fa,
                         "surveysWithFailedSelfChecks": bad_surveys}
        r.tally(ok=len(doc["rows"]))
        out = os.path.abspath(args.out)
        os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
        with open(out, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=2)
        r.artifact(out)
        md = lookup_markdown(doc)
        if args.md:
            mdp = os.path.abspath(args.md)
            os.makedirs(os.path.dirname(mdp) or ".", exist_ok=True)
            with open(mdp, "w", encoding="utf-8") as fh:
                fh.write(md)
            r.artifact(mdp)
    print(md)
    print(f"\n  lookup.json: {out}" + (f"\n  lookup.md:   {args.md}" if args.md else ""))
    return 0


def lookup_markdown(doc):
    L = []
    L.append("# depth-survey lookup\n")
    L.append(f"Written {doc['created']} by `depthsurvey.py lookup`.\n")
    L.append("Tolerance is `shotplan.surface_tolerance` at the planner's defaults "
             f"({doc['tolerance']['surfaceTol']:g} u absolute, obliquity "
             f"{doc['tolerance']['surfaceObliquity']:g}), called with the survey face's own fov "
             "and size.\n")
    L.append("| variant | declared point | offset | origin | cell | faces | fingerprint | "
             "self-check fails |")
    L.append("|---|---|---|---|---|---|---|---|")
    for s in doc["surveys"]:
        fp = (s["configFingerprint"] or "-")[:16]
        L.append(f"| {s['variant']} | {fmt3(s['declaredPoint'])} | {fmt3(s['offset'])} | "
                 f"{fmt3(s['origin'])} | {s['cell']} | {s['size']}px yaw+{s['faceYawOffset']} | "
                 f"`{fp}` | {s['selfCheckFailed']} |")
    L.append("")
    L.append("| variant | camera | expected | erode | face | pixel | survey S | needed D | tol | "
             "S-D | verdict | agreement | hit |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for x in doc["rows"]:
        S = "background" if x["survey"] is None else f"{x['survey']:.1f}"
        dl = "-" if x["delta"] is None else f"{x['delta']:+.1f}"
        tol = "-" if x["tolerance"] is None else f"{x['tolerance']:.1f}"
        px = "-" if x.get("pixel") is None else f"{x['pixel'][0]},{x['pixel'][1]}"
        hit = "-" if x.get("hit") is None else fmt3(x["hit"])
        L.append(f"| {x['variant']} | {x['label']} {fmt3(x['camera'])} | "
                 f"{x['expected'] or '-'} | {int(x['erode'])} | {x.get('face', '-')} | {px} | "
                 f"{S} | {x['needed']:.1f} | {tol} | {dl} | **{x['verdict']}** | "
                 f"{x['agreement'] or '-'} | {hit} |")
    L.append("")
    c = doc["counts"]
    L.append(f"**False discards (a verified camera read blocked): {c['falseDiscards']}.** "
             f"False admits (a refused camera read admissible): {c['falseAdmits']}. "
             f"Rows {c['rows']}: {c['blocked']} blocked, {c['admissible']} admissible, "
             f"{c['noCoverage']} no-coverage.")
    if c["surveysWithFailedSelfChecks"]:
        L.append("")
        L.append("**These verdicts are not trustworthy**: the survey(s) "
                 + ", ".join(c["surveysWithFailedSelfChecks"])
                 + " have failed pick-vs-dump self-checks.")
    L.append("")
    for x in doc["rows"]:
        L.append(f"- `{x['variant']}` {x['label']} erode {int(x['erode'])}: {x['verdict']} -- "
                 f"{x['why']}" + ("  (edge-clamped read: the direction fell on a face boundary)"
                                  if x.get("edgeClamped") else ""))
    return "\n".join(L) + "\n"


# ---------------------------------------------------------------------------- cmd: figure ---

def cmd_figure(args):
    surveys = load_surveys(args.survey, args.variant_label)
    cameras = [parse_camera(c) for c in args.camera]
    slices = list(args.slice_elevation or [])
    if not slices:
        slices = [0.0]
        for cam in cameras:
            d = vsub(cam["pos"], surveys[0].origin)
            n = vlen(d)
            if n:
                e = round(math.degrees(math.asin(d[2] / n)), 1)
                if all(abs(e - s) > 0.5 for s in slices):
                    slices.append(e)
    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)

    with _run("depthsurvey", mode="figure", stage_source="counted") as r:
        total = len(surveys) * len(slices)
        r.feature("slices", total, "slices")
        r.stage("slices", "sweeping free regions", total, "slices")
        regions = []
        for s in surveys:
            for el in slices:
                pts = free_region(s, el, args.cap, args.erode)
                regions.append({"survey": s, "elevation": el, "points": pts})
                r.advance()
        draw_plan(out, surveys, regions, cameras, args)
        r.artifact(out)
        r.tally(ok=1)
    print(f"  figure: {out}")
    print(f"  slices: {', '.join(f'{e:g} deg' for e in slices)}; cap {args.cap:g} u")
    return 0


def free_region(survey, elevation_deg, cap, erode=False):
    """The free region at one elevation: for each integer bearing, how far the survey ray reaches
    before geometry, projected onto the horizontal plane. Background reads as the cap."""
    el = math.radians(elevation_deg)
    out = []
    for b in range(360):
        rb = math.radians(b)
        d = (math.sin(rb) * math.cos(el), math.cos(rb) * math.cos(el), math.sin(el))
        rd = survey.read(d, erode=erode)
        if rd is None:
            out.append({"bearing": b, "dist": None, "horiz": None, "clipped": True})
            continue
        s = rd["ray"]
        clipped = math.isinf(s) or s > cap
        s = cap if clipped else s
        out.append({"bearing": b, "dist": None if clipped else s,
                    "horiz": s * math.cos(el), "clipped": clipped})
    return out


# ------------------------------------------------------------------------------- drawing ----

class Canvas:
    """A tiny true-scale plan canvas over PIL or hand-written SVG.

    World units in, pixels out, one scale for both axes and +Y up the page -- a plan view that is
    not true-scale is a diagram, and the handoff asked for a figure a reader can measure on.
    """

    def __init__(self, path, wpx, hpx, cx, cy, scale, bg="#ffffff"):
        self.path, self.w, self.h = path, wpx, hpx
        self.cx, self.cy, self.scale = cx, cy, scale       # world centre, px per world unit
        self.svg = path.lower().endswith(".svg")
        self.ops = []
        self.bg = bg
        if not self.svg:
            from PIL import Image, ImageDraw, ImageFont      # noqa: F401 (checked by the caller)
            self.Image, self.ImageDraw, self.ImageFont = Image, ImageDraw, ImageFont
            self.img = Image.new("RGB", (wpx, hpx), bg)
            self.overlay = Image.new("RGBA", (wpx, hpx), (0, 0, 0, 0))
            self.d = ImageDraw.Draw(self.img)
            self.od = ImageDraw.Draw(self.overlay)
            self.fonts = {}

    def xy(self, x, y, *_ignored_z):
        """World (x, y) -> canvas pixels. A z is accepted and dropped: this is a PLAN view, and
        every caller holds 3-vectors."""
        return (self.w / 2.0 + (x - self.cx) * self.scale,
                self.h / 2.0 - (y - self.cy) * self.scale)

    def _font(self, size):
        if size not in self.fonts:
            try:
                self.fonts[size] = self.ImageFont.load_default(size=size)
            except TypeError:
                self.fonts[size] = self.ImageFont.load_default()
        return self.fonts[size]

    def line(self, a, b, color="#333333", width=1, dash=False):
        p, q = self.xy(*a), self.xy(*b)
        if self.svg:
            self.ops.append(
                f'<line x1="{p[0]:.2f}" y1="{p[1]:.2f}" x2="{q[0]:.2f}" y2="{q[1]:.2f}" '
                f'stroke="{color}" stroke-width="{width}"'
                + (' stroke-dasharray="4 4"' if dash else '') + ' />')
            return
        if dash:
            n = max(1, int(math.dist(p, q) / 6))
            for i in range(n):
                if i % 2:
                    continue
                t0, t1 = i / n, min(1.0, (i + 1) / n)
                self.d.line([p[0] + (q[0] - p[0]) * t0, p[1] + (q[1] - p[1]) * t0,
                             p[0] + (q[0] - p[0]) * t1, p[1] + (q[1] - p[1]) * t1],
                            fill=color, width=width)
        else:
            self.d.line([p[0], p[1], q[0], q[1]], fill=color, width=width)

    def polygon_fill(self, pts, rgba=(70, 130, 180, 40)):
        xy = [self.xy(*p) for p in pts]
        if self.svg:
            pth = " ".join(f"{x:.1f},{y:.1f}" for x, y in xy)
            col = f"rgb({rgba[0]},{rgba[1]},{rgba[2]})"
            self.ops.append(f'<polygon points="{pth}" fill="{col}" '
                            f'fill-opacity="{rgba[3] / 255.0:.2f}" stroke="none" />')
            return
        self.od.polygon([tuple(p) for p in xy], fill=rgba)

    def circle(self, c, r_px, fill=None, outline="#000000", width=2):
        p = self.xy(*c)
        box = [p[0] - r_px, p[1] - r_px, p[0] + r_px, p[1] + r_px]
        if self.svg:
            self.ops.append(f'<circle cx="{p[0]:.2f}" cy="{p[1]:.2f}" r="{r_px}" '
                            f'fill="{fill or "none"}" stroke="{outline}" stroke-width="{width}" />')
            return
        self.d.ellipse(box, fill=fill, outline=outline, width=width)

    def text(self, c, s, color="#000000", size=13, px_offset=(0, 0), anchor_left=True):
        p = self.xy(*c)
        p = (p[0] + px_offset[0], p[1] + px_offset[1])
        if self.svg:
            esc = (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))
            self.ops.append(f'<text x="{p[0]:.2f}" y="{p[1]:.2f}" font-size="{size}" '
                            f'font-family="DejaVu Sans, Verdana, sans-serif" fill="{color}" '
                            f'text-anchor="{"start" if anchor_left else "end"}">{esc}</text>')
            return
        self.d.text(p, s, fill=color, font=self._font(size),
                    anchor="ls" if anchor_left else "rs")

    def text_px(self, p, s, color="#000000", size=13):
        if self.svg:
            esc = (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))
            self.ops.append(f'<text x="{p[0]:.2f}" y="{p[1]:.2f}" font-size="{size}" '
                            f'font-family="DejaVu Sans, Verdana, sans-serif" fill="{color}">'
                            f'{esc}</text>')
            return
        self.d.text(p, s, fill=color, font=self._font(size), anchor="ls")

    def line_px(self, p, q, color="#333333", width=1):
        if self.svg:
            self.ops.append(f'<line x1="{p[0]:.2f}" y1="{p[1]:.2f}" x2="{q[0]:.2f}" '
                            f'y2="{q[1]:.2f}" stroke="{color}" stroke-width="{width}" />')
            return
        self.d.line([p[0], p[1], q[0], q[1]], fill=color, width=width)

    def save(self):
        if self.svg:
            body = "\n".join(self.ops)
            with open(self.path, "w", encoding="utf-8") as fh:
                fh.write(f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.w}" '
                         f'height="{self.h}" viewBox="0 0 {self.w} {self.h}">\n'
                         f'<rect width="{self.w}" height="{self.h}" fill="{self.bg}" />\n'
                         f'{body}\n</svg>\n')
            return
        from PIL import Image
        self.img = Image.alpha_composite(self.img.convert("RGBA"), self.overlay).convert("RGB")
        self.img.save(self.path)


SLICE_COLORS = ["#1f77b4", "#d62728", "#2ca02c", "#9467bd", "#ff7f0e", "#8c564b"]


def draw_plan(out, surveys, regions, cameras, args):
    """The plan view. North is up, one scale on both axes, a 100 u bar to measure with."""
    declared = surveys[0].declared
    radius = args.radius
    if not radius:
        far = max([vlen(vsub(c["pos"], declared)) for c in cameras] or [200.0])
        radius = max(160.0, far * 1.45)
    W, H = args.width, args.height
    pad_top, pad_bottom = 96, 56 + 16 * max(1, len(regions))
    scale = min((W - 60) / (2 * radius), (H - pad_top - pad_bottom) / (2 * radius))
    cy_world = declared[1]
    canvas = Canvas(out, W, H, declared[0], cy_world, scale)

    # grid every 100 u, labelled on the axes through the declared point
    g = 100.0
    n = int(radius / g) + 1
    for k in range(-n, n + 1):
        canvas.line((declared[0] + k * g, declared[1] - radius),
                    (declared[0] + k * g, declared[1] + radius), color="#e8e8e8", width=1)
        canvas.line((declared[0] - radius, declared[1] + k * g),
                    (declared[0] + radius, declared[1] + k * g), color="#e8e8e8", width=1)

    # Free regions, one outline per (survey, elevation). A direction the survey never bounds is
    # drawn DASHED at the edge of the window rather than out at the cap: a diagonal streak to
    # 1500 u tells a reader nothing, and a dashed arc says "open, past this picture" -- which is
    # what the number actually supports.
    edge_r = radius * 0.97
    legend = []
    for i, reg in enumerate(regions):
        col = SLICE_COLORS[i % len(SLICE_COLORS)]
        s, el, pts = reg["survey"], reg["elevation"], reg["points"]
        draw = []
        for p in pts:
            if p["horiz"] is None:
                draw.append(None)
                continue
            r_h = min(p["horiz"], edge_r)
            rb = math.radians(p["bearing"])
            draw.append({"xy": (s.origin[0] + r_h * math.sin(rb),
                                s.origin[1] + r_h * math.cos(rb)),
                         "open": p["clipped"] or p["horiz"] > edge_r})
        poly = [d["xy"] for d in draw if d]
        if len(poly) > 2:
            canvas.polygon_fill(poly, rgba=_rgba(col, 22))
        m = len(draw)
        for k in range(m):
            a, b = draw[k], draw[(k + 1) % m]
            if a is None or b is None:
                continue
            open_seg = a["open"] or b["open"]
            canvas.line(a["xy"], b["xy"], color=col, width=1 if open_seg else 3, dash=open_seg)
        bounded = sum(1 for p in pts if not p["clipped"])
        legend.append((col, f"{s.label}, elevation {el:+.1f} deg: {bounded}/360 bearings bounded "
                            f"(solid); the rest open past the {args.cap:g} u cap (dashed)"))

    # the declared point, and each survey origin that differs from it
    canvas.line((declared[0] - 9 / scale, declared[1]), (declared[0] + 9 / scale, declared[1]),
                color="#000000", width=2)
    canvas.line((declared[0], declared[1] - 9 / scale), (declared[0], declared[1] + 9 / scale),
                color="#000000", width=2)
    canvas.text(declared, f"declared point {fmt3(declared)}", size=13, px_offset=(10, -8))
    for s in surveys:
        if vlen(vsub(s.origin, s.declared)) > 1e-6:
            canvas.circle(s.origin, 4, fill=None, outline="#666666", width=2)
            canvas.text(s.origin, f"{s.label} origin (offset {fmt3(s.offset)})",
                        size=11, color="#666666", px_offset=(10, 22))

    # cameras
    for c in cameras:
        col = {"verified": "#127a2a", "refused": "#c01414"}.get(c["expected"], "#555555")
        canvas.line(c["pos"], declared, color=col, width=1, dash=True)
        canvas.circle(c["pos"], 6, fill=col, outline="#ffffff", width=1)
        d = vsub(c["pos"], declared)
        canvas.text(c["pos"], f"{c['label']} ({c['expected'] or 'unlabelled'}) "
                              f"{vlen(d):.0f} u, z {c['pos'][2]:.0f}",
                    size=12, color=col, px_offset=(10, 4))

    # scale bar (bottom right, clear of the legend), north arrow, caption
    bar = 100.0 * scale
    y0, x0 = H - 46, W - 60 - 100.0 * scale
    canvas.line_px((x0, y0), (x0 + bar, y0), color="#000000", width=3)
    canvas.line_px((x0, y0 - 5), (x0, y0 + 5), color="#000000", width=3)
    canvas.line_px((x0 + bar, y0 - 5), (x0 + bar, y0 + 5), color="#000000", width=3)
    canvas.text_px((x0, y0 - 10), "100 u (true scale, equal axes)", size=12)
    canvas.line_px((W - 40, 78), (W - 40, 40), color="#000000", width=3)
    canvas.line_px((W - 40, 40), (W - 46, 50), color="#000000", width=3)
    canvas.line_px((W - 40, 40), (W - 34, 50), color="#000000", width=3)
    canvas.text_px((W - 52, 92), "N (+Y)", size=12)

    title = args.title or f"depth survey around {fmt3(declared)}"
    canvas.text_px((24, 26), title, size=16)
    canvas.text_px((24, 44), f"{surveys[0].meta.get('cell', '?')}  --  plan view, z of the "
                             f"declared point {declared[2]:.0f}  --  free region = how far each "
                             f"survey ray reaches, projected onto the horizontal", size=11)
    canvas.text_px((24, 60), COVERAGE_NOTE[:150], size=10, color="#444444")
    canvas.text_px((24, 74), COVERAGE_NOTE[150:], size=10, color="#444444")
    ly = H - pad_bottom + 20
    for col, txt in legend:
        canvas.line_px((30, ly - 4), (54, ly - 4), color=col, width=3)
        canvas.text_px((60, ly), txt, size=11)
        ly += 16
    fp = (surveys[0].meta.get("configFingerprint") or "-")[:16]
    canvas.text_px((30, H - 14), f"viewprobe capture-input fingerprint {fp}  --  erode "
                                f"{int(bool(args.erode))}  --  open-direction cap {args.cap:g} u",
                   size=10, color="#444444")
    canvas.save()


def _rgba(hexcol, alpha):
    h = hexcol.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), alpha)


# -------------------------------------------------------------------------- cmd: selftest ---

SELFTEST_BANNER = (
    "arithmetic only: proves the lookup math, not the renderer. These checks run on SYNTHETIC "
    "depth arrays encoded through the same projection they are decoded with, so they cannot see "
    "a near-plane, back-face-culling or basis-orientation error in the real renderer. Only the "
    "per-face pick-vs-dump self-check in `survey` can."
)


def _approx(a, b, tol):
    return abs(a - b) <= tol


class Checks:
    def __init__(self):
        self.rows = []

    def ok(self, name, cond, detail=""):
        self.rows.append((bool(cond), name, detail))
        return bool(cond)

    @property
    def failed(self):
        return [r for r in self.rows if not r[0]]


def run_checks():
    c = Checks()

    # 1. window <-> view round trip, at float32 storage precision
    for v in (20.0, 60.0, 250.0, 1000.0, 5000.0):
        w = struct.unpack("<f", struct.pack("<f", view_to_window(v)))[0]
        back = window_to_view(w)
        c.ok(f"window/view round trip at {v:g} u", abs(back - v) <= max(0.05, v * 1e-3),
             f"{back:.4f}")

    # 2. background sentinel
    c.ok("background sentinel is the renderer's own test", BACKGROUND == 0.999999)

    # 3. a flat wall perpendicular to the view axis, read at five pixels
    size, fov, W = 64, 90.0, 250.0
    face = Face.synthetic("wall", 35.0, -12.0, fov, size, (10.0, -20.0, 30.0), fill_view=W)
    for (ix, iy) in selfcheck_pixels(size):
        nx, ny = ndc_of_pixel(ix, iy, size)
        want_ray = W * ray_scale(nx, ny, face.t)
        got_ray = face.ray_at(ix, iy)
        world = face.world_at(ix, iy)
        along = vdot(vsub(world, face.eye), face.f)
        c.ok(f"flat wall ray distance at {ix},{iy}", _approx(got_ray, want_ray, 0.02),
             f"{got_ray:.3f} vs {want_ray:.3f}")
        c.ok(f"flat wall view-axis depth at {ix},{iy}", _approx(along, W, 0.02), f"{along:.3f}")
        c.ok(f"flat wall |world-eye| == ray at {ix},{iy}",
             _approx(vlen(vsub(world, face.eye)), got_ray, 0.02))

    # 4. a ray distance is NOT the view distance off-axis (the error this term exists to prevent)
    nx, ny = ndc_of_pixel(0, 0, size)
    c.ok("corner ray exceeds the view distance by the expected factor",
         _approx(face.ray_at(0, 0) / W, ray_scale(nx, ny, face.t), 1e-6),
         f"{face.ray_at(0, 0) / W:.4f}")

    # 5. pixel <-> angle round trip against shotplan's own inverse
    for (h, v) in ((0.0, 0.0), (10.0, -7.5), (-30.0, 20.0), (40.0, 40.0)):
        px = pixel_for_angles(h, v, fov, size)
        c.ok(f"pixel_for_angles returns a pixel for {h},{v}", px is not None)
        if not px:
            continue
        ix, iy = px
        bx, by = ndc_of_pixel(ix, iy, size)
        t = math.tan(math.radians(fov / 2))
        gh, gv = math.degrees(math.atan(bx * t)), math.degrees(math.atan(by * t))
        half_px = fov / size
        c.ok(f"angle round trip at {h},{v}", abs(gh - h) <= half_px and abs(gv - v) <= half_px,
             f"{gh:.3f},{gv:.3f} vs {h},{v} (half pixel {half_px:.3f} deg)")

    # 6. erosion takes the 3x3 minimum, background counting as +inf
    er = Face.synthetic("erode", 0.0, 0.0, 90.0, 32, (0.0, 0.0, 0.0), fill_view=1000.0)
    er.set_view(10, 10, 100.0)
    er.set_view(20, 20, None)                       # background
    c.ok("erode 0 at a neighbour reads the far wall", er.ray_at(11, 11) > 900)
    # 100 u of VIEW distance off-axis is more than 100 u of RAY distance -- the threshold is the
    # near pixel's own ray, not the view distance that encoded it.
    c.ok("erode 1 at a neighbour reads the near pixel",
         _approx(er.ray_eroded(11, 11), er.ray_at(10, 10), 1e-9) and er.ray_eroded(11, 11) < 120,
         f"{er.ray_eroded(11, 11):.1f} (the near pixel's own ray {er.ray_at(10, 10):.1f})")
    c.ok("erode 1 two pixels away is unaffected", er.ray_eroded(12, 12) > 900)
    c.ok("background reads as +inf", math.isinf(er.ray_at(20, 20)))
    c.ok("erosion treats background as +inf, not as zero", er.ray_eroded(21, 21) > 900)
    c.ok("a background neighbour never lowers the minimum",
         _approx(er.ray_eroded(19, 19), min(er.ray_at(19 + dx, 19 + dy)
                                            for dx in (-1, 0, 1) for dy in (-1, 0, 1)), 1e-9))

    # 7. face selection, including at a face boundary
    faces = face_plan(0.0, 64, (0.0, 0.0, 0.0))
    sv = Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), faces)
    for b, want in ((0, "side-0"), (10, "side-0"), (44, "side-0"), (46, "side-90"),
                    (91, "side-90"), (181, "side-180"), (271, "side-270")):
        rb = math.radians(b)
        sel = sv.select((math.sin(rb), math.cos(rb), 0.0))
        c.ok(f"bearing {b} selects {want}", sel and sel[0].name == want,
             sel[0].name if sel else "none")
    for el, want in ((80, "up"), (-80, "down"), (50, "up"), (40, "side-0")):
        re_ = math.radians(el)
        sel = sv.select((0.0, math.cos(re_), math.sin(re_)))
        c.ok(f"elevation {el} selects {want}", sel and sel[0].name == want,
             sel[0].name if sel else "none")
    # The overlap band, where the pole face and the side face both hold the direction. At 44 the
    # two margins are EQUAL to the digit (tan 44 = 1/tan 46), so which one wins is not a claim
    # worth making -- that it is covered at all is.
    for el in (43.5, 44.0, 45.0, -44.0):
        re_ = math.radians(el)
        c.ok(f"elevation {el} is covered by some face",
             sv.select((0.0, math.cos(re_), math.sin(re_))) is not None)
    rb = math.radians(45.0)
    sel = sv.select((math.sin(rb), math.cos(rb), 0.0))
    c.ok("a bearing exactly on a face boundary is still read (edge-clamped), not dropped",
         sel is not None and sel[1]["edgeClamped"], sel[0].name if sel else "none")
    off = Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), face_plan(45.0, 64, (0, 0, 0)))
    sel = off.select((math.sin(rb), math.cos(rb), 0.0))
    c.ok("with --face-yaw-offset 45 the same bearing is a face CENTRE",
         sel and sel[0].name == "side-45" and sel[1]["marginPx"] > 31,
         f"{sel[0].name} margin {sel[1]['marginPx']:.1f} px" if sel else "none")

    # 8. verdicts end to end on synthetic faces
    vf = face_plan(0.0, 64, (0.0, 0.0, 0.0))
    for f in vf:
        f.buf = array.array("f", [1.0] * (f.size * f.size))
    vf[0].fill_view(100.0)                          # a wall due north at view distance 100
    sv2 = Survey((0, 0, 0), (0, 0, 0), (0, 0, 0), vf)
    r_far = sv2.lookup((0.0, 200.0, 0.0))
    r_near = sv2.lookup((0.0, 50.0, 0.0))
    r_at = sv2.lookup((0.0, 100.0, 0.0))
    r_bg = sv2.lookup((0.0, -200.0, 0.0))
    c.ok("a camera behind a wall is blocked", r_far["verdict"] == "blocked", r_far["verdict"])
    c.ok("a camera in front of the wall is admissible-beyond",
         r_near["verdict"] == "admissible-beyond", r_near["verdict"])
    c.ok("a camera ON the wall is admissible-at", r_at["verdict"] == "admissible-at",
         f"{r_at['verdict']} S {r_at['survey']} D {r_at['needed']} tol {r_at['tolerance']}")
    c.ok("an empty direction is admissible-background",
         r_bg["verdict"] == "admissible-background", r_bg["verdict"])
    c.ok("the blocked row names the survey depth and the needed distance",
         r_far["survey"] is not None and r_far["needed"] == 200.0)

    # 9. the tolerance is shotplan's, and grows with distance
    t1 = surface_tolerance(100.0, 90.0, 512, TOL_ARGS)
    t2 = surface_tolerance(400.0, 90.0, 512, TOL_ARGS)
    c.ok("tolerance is the planner's absolute term at zero distance",
         _approx(surface_tolerance(0.0, 90.0, 512, TOL_ARGS), TOL_ARGS.surface_tol, 1e-9))
    c.ok("tolerance grows with distance", t2 > t1 > TOL_ARGS.surface_tol,
         f"{t1:.2f} -> {t2:.2f}")

    # 10. the declared point, not the origin, sets the needed distance
    sv3 = Survey((0, 0, 0), (0, 0, 4), (0, 0, 4), vf)
    row = sv3.lookup((0.0, 100.0, 0.0))
    c.ok("needed distance is measured from the DECLARED point",
         _approx(row["needed"], 100.0, 1e-6), f"{row['needed']}")
    c.ok("distance from the survey origin is reported separately",
         _approx(row["distFromOrigin"], math.dist((0, 0, 4), (0, 100, 0)), 0.01),
         f"{row['distFromOrigin']} vs {math.dist((0, 0, 4), (0, 100, 0)):.3f}")

    # 11. the offset really moves the survey line: an off-axis pixel, not the centre
    d = vsub((0.0, 100.0, 0.0), (0.0, 0.0, 4.0))
    sel = sv3.select(d)
    c.ok("an offset origin reads an off-centre pixel", sel and sel[1]["pixel"][1] != 32,
         f"pixel {sel[1]['pixel']}" if sel else "none")

    return c


def cmd_selftest(args):
    print("depthsurvey selftest -- " + SELFTEST_BANNER + "\n")
    with _run("depthsurvey", mode="selftest", stage_source="counted") as r:
        c = run_checks()
        r.feature("checks", len(c.rows), "checks")
        r.stage("checks", "arithmetic checks", len(c.rows), "checks")
        r.progress(len(c.rows))
        for ok, name, detail in c.rows:
            print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"   [{detail}]" if detail else ""))
        r.tally(ok=len(c.rows) - len(c.failed), failed=len(c.failed))
        print(f"\n  {len(c.rows) - len(c.failed)}/{len(c.rows)} checks passed")
        if c.failed:
            r.fail(f"{len(c.failed)} arithmetic checks failed")
            return 1
    return 0


# ------------------------------------------------------------------------------- plumbing ---

def parse_triple(s, what):
    parts = str(s).replace(" ", "").split(",")
    if len(parts) != 3:
        raise SystemExit(f"depthsurvey: {what} wants X,Y,Z (signed), got {s!r}")
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        raise SystemExit(f"depthsurvey: {what} wants three numbers, got {s!r}")


def parse_camera(spec):
    """`X,Y,Z`, `X,Y,Z=label`, or `X,Y,Z=label:verified|refused`."""
    pos, _, rest = spec.partition("=")
    label, expected = rest, None
    if ":" in rest:
        label, _, expected = rest.partition(":")
        expected = expected.strip().lower()
        if expected not in ("verified", "refused", ""):
            raise SystemExit(f"depthsurvey: --camera expectation must be verified or refused, "
                             f"got {expected!r}")
    return {"pos": parse_triple(pos, "--camera"), "label": label or "camera",
            "expected": expected or None}


def load_surveys(paths, labels):
    """`--survey PATH` (repeatable), `--survey PATH=LABEL`, or `--variant-label` in the same
    order. A survey with no label takes its directory's name, which is how the run layout already
    names its variants."""
    labels = list(labels or [])
    out = []
    for i, p in enumerate(paths):
        path, _, inline = p.partition("=")
        lab = inline or (labels[i] if i < len(labels) else None)
        if not os.path.isfile(path):
            raise SystemExit(f"depthsurvey: no survey at {path}")
        out.append(Survey.load(path, label=lab))
    seen = {}
    for s in out:
        seen[s.label] = seen.get(s.label, 0) + 1
        if seen[s.label] > 1:
            s.label = f"{s.label}#{seen[s.label]}"
    return out


def fmt3(v):
    return ",".join(f"{x:g}" for x in v)


def build_parser():
    p = argparse.ArgumentParser(
        prog="depthsurvey.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=__doc__.split("Subcommands")[0].strip(),
        epilog="Conventions, the tolerance and the pick regexes are imported from shotplan.py, "
               "which stays frozen. Run `selftest` for the arithmetic-only checks.")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("survey", help="render a six-face outward depth survey from a point",
                       description="Render the six survey faces from `point + offset`, dump each "
                                   "one's depth buffer, and self-check every dump against "
                                   "viewprobe's own --pick answers at five pixels per face.")
    s.add_argument("cell", help="the cell to render (viewprobe's positional)")
    s.add_argument("--point", required=True, metavar="X,Y,Z",
                   help="the DECLARED world point the survey is about")
    s.add_argument("--offset", default=None, metavar="DX,DY,DZ",
                   help="signed offset from the declared point to the survey EYE (default 0,0,0)")
    s.add_argument("--label", default=None, help="a name for this survey, kept in survey.json")
    s.add_argument("--out", required=True, metavar="DIR",
                   help="directory for the renders, the dumps, the logs and survey.json")
    s.add_argument("--size", type=int, default=DEFAULT_SIZE, help="frame size (default 512)")
    s.add_argument("--face-yaw-offset", type=float, default=0.0, metavar="DEG",
                   help="rotate the four side faces, so a bearing that sat at a face centre sits "
                        "at a face edge instead (default 0)")
    s.add_argument("--vp", default=None, help=f"viewprobe.exe (default {DEFAULT_VP})")
    s.add_argument("--instance", default=None, help="MO2 instance directory")
    s.add_argument("--profile", default=None, help="MO2 profile")
    s.add_argument("--game", default=None, help="game directory")
    s.add_argument("--check-dist", type=float, default=1.0, metavar="U",
                   help="self-check: how far the dump's ray distance may differ from viewprobe's "
                        "printed whole-unit pick (default 1.0)")
    s.add_argument("--check-pos", type=float, default=1.5, metavar="U",
                   help="self-check: per-axis world position tolerance (default 1.5)")
    s.set_defaults(func=cmd_survey)

    l = sub.add_parser("lookup", help="does a survey admit or block a camera?",
                       description="Look one or more cameras up in one or more surveys. The "
                                   "needed distance is measured from the DECLARED point; the "
                                   "survey depth is read in the face that holds the direction "
                                   "from the survey's own origin.")
    l.add_argument("--survey", action="append", required=True, metavar="PATH[=LABEL]",
                   help="a survey.json (repeatable)")
    l.add_argument("--variant-label", action="append", default=None, metavar="LABEL",
                   help="label for the Nth --survey (repeatable); default is its directory name")
    l.add_argument("--camera", action="append", required=True,
                   metavar="X,Y,Z=LABEL[:verified|refused]",
                   help="a camera to look up (repeatable). The expectation, when given, is what "
                        "makes a row a false discard or a false admit")
    l.add_argument("--erode", default="0", metavar="0|1|0,1",
                   help="3x3 minimum erosion of the survey depth; a comma list runs both "
                        "(default 0)")
    l.add_argument("--out", required=True, metavar="FILE.json")
    l.add_argument("--md", default=None, metavar="FILE.md", help="also write a Markdown table")
    l.set_defaults(func=cmd_lookup)

    f = sub.add_parser("figure", help="true-scale plan view of the survey's free region",
                       description="A true-scale plan view around the declared point: the free "
                                   "region per elevation slice, the point, the survey origins "
                                   "and the cameras. PNG via PIL, or SVG by extension.")
    f.add_argument("--survey", action="append", required=True, metavar="PATH[=LABEL]")
    f.add_argument("--variant-label", action="append", default=None, metavar="LABEL")
    f.add_argument("--camera", action="append", default=[],
                   metavar="X,Y,Z=LABEL[:verified|refused]")
    f.add_argument("--slice-elevation", action="append", type=float, default=None, metavar="DEG",
                   help="elevation slice (repeatable). Default: 0 and each camera's own "
                        "elevation")
    f.add_argument("--erode", type=int, default=0, help="erode the survey depth (default 0)")
    f.add_argument("--cap", type=float, default=1500.0, metavar="U",
                   help="how far an open direction is drawn before it is called open "
                        "(default 1500)")
    f.add_argument("--radius", type=float, default=None, metavar="U",
                   help="half-width of the drawn window in world units (default: fits the "
                        "cameras)")
    f.add_argument("--width", type=int, default=1400)
    f.add_argument("--height", type=int, default=1200)
    f.add_argument("--title", default=None)
    f.add_argument("--out", required=True, metavar="FILE.png|FILE.svg")
    f.set_defaults(func=cmd_figure)

    t = sub.add_parser("selftest", help="arithmetic-only checks on synthetic depth arrays",
                       description=SELFTEST_BANNER)
    t.set_defaults(func=cmd_selftest)
    return p


COORD_FLAGS = ("--point", "--offset", "--camera", "--slice-elevation", "--radius", "--cap")


def glue_negative_values(argv):
    """`--point -852,-5451,45` -- argparse reads a leading-minus value as another flag and
    refuses with "expected one argument", which is a papercut this tool would hit on most real
    Skyrim coordinates. A value token that starts with a minus and then a DIGIT is glued to its
    flag with `=`, the spelling argparse already accepts. Nothing else is touched, so `--out -x`
    still fails the way it should.
    """
    out, i = [], 0
    while i < len(argv):
        a = argv[i]
        if (a in COORD_FLAGS and i + 1 < len(argv)
                and len(argv[i + 1]) > 1 and argv[i + 1][0] == "-"
                and (argv[i + 1][1].isdigit() or argv[i + 1][1] == ".")):
            out.append(f"{a}={argv[i + 1]}")
            i += 2
            continue
        out.append(a)
        i += 1
    return out


def main(argv=None):
    argv = glue_negative_values(list(sys.argv[1:] if argv is None else argv))
    passthrough = []
    if "--" in argv:
        i = argv.index("--")
        argv, passthrough = argv[:i], argv[i + 1:]
    args = build_parser().parse_args(argv)
    args.passthrough = passthrough
    if args.cmd == "figure":
        try:
            import PIL  # noqa: F401
        except ImportError:
            if not args.out.lower().endswith(".svg"):
                raise SystemExit("depthsurvey: PIL is not installed; ask for an .svg --out")
    return args.func(args) or 0


if __name__ == "__main__":
    sys.exit(main())
