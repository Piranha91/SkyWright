#!/usr/bin/env python3
"""A stand-in for viewprobe that answers shotplan's queries from a declared toy scene.

    shotplan.py plan CELL --vp "python fake_viewprobe.py" ...     (FAKE_VIEWPROBE_SCENE=scene.json)

WHY THIS EXISTS. shotplan's failure modes are in the ORCHESTRATION -- which answer is believed,
which answer reaches the exported artifact, what the exit code says -- and none of that is
exercised by unit-testing a helper. This process is invoked by the real `Probe.run` through the
real command line, and prints lines in viewprobe's own format so the real parsers read them. What
is faked is the renderer, not the planner.

The line shapes are copied from real viewprobe stdout, with
the scene replaced by synthetic geometry. It is deliberately small: axis-aligned boxes, a floor,
and rectangles of navmesh. It is NOT a renderer and proves nothing about a real cell -- a test
built on it demonstrates the planner's decisions over known answers, and that is all.

Scene JSON:

    {"floorZ": 0.0,
     "boxes": [{"id": "000001:Test.esp", "edid": "TestCrate", "type": "Static",
                "min": [x,y,z], "max": [x,y,z],
                "alpha": false,      # true = draws with depth writes OFF: the ray passes through
                "physical": true}],  # false = not offered by --inside-at
     "nav":  [{"min": [x,y], "max": [x,y], "z": 0.0, "name": "Test.esp tri"}],
     "navSilent": [[x,y]],           # queried points that get NO nav-at line at all
     "insideSilent": [[x,y,z]]}      # queried points that get NO inside-at line at all
"""
from __future__ import annotations

import json
import math
import os
import sys

FAR = 1.0e9


def load_scene():
    path = os.environ.get("FAKE_VIEWPROBE_SCENE")
    if not path:
        print("fake_viewprobe: FAKE_VIEWPROBE_SCENE is not set", file=sys.stderr)
        raise SystemExit(2)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def basis(yaw_deg, pitch_deg):
    """viewprobe's own camera basis: yaw 0 = +Y, clockwise; pitch POSITIVE looks DOWN."""
    y, p = math.radians(yaw_deg), math.radians(pitch_deg)
    f = (math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), -math.sin(p))
    r = (math.cos(y), -math.sin(y), 0.0)
    u = (r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0])
    return f, r, u


def ray_box(eye, d, lo, hi):
    """Slab intersection. Returns the entry distance along d, or None."""
    t0, t1 = 0.0, FAR
    for i in range(3):
        if abs(d[i]) < 1e-12:
            if eye[i] < lo[i] or eye[i] > hi[i]:
                return None
            continue
        a = (lo[i] - eye[i]) / d[i]
        b = (hi[i] - eye[i]) / d[i]
        if a > b:
            a, b = b, a
        t0, t1 = max(t0, a), min(t1, b)
        if t0 > t1:
            return None
    return t0 if t0 > 1e-6 else None


def trace(scene, eye, d):
    """(hit point, distance) for the first DEPTH-WRITING surface along the ray, or None.

    Alpha-blended boxes are skipped exactly as viewprobe's depth probe skips them -- that is the
    known limit the planner has to keep reporting, so the stand-in has to reproduce it.
    """
    best = None
    for b in scene.get("boxes", []):
        if b.get("alpha"):
            continue
        t = ray_box(eye, d, b["min"], b["max"])
        if t is not None and (best is None or t < best):
            best = t
    fz = scene.get("floorZ")
    if fz is not None and d[2] < -1e-12:
        t = (fz - eye[2]) / d[2]
        if t > 1e-6 and (best is None or t < best):
            best = t
    if best is None:
        return None
    return (eye[0] + d[0] * best, eye[1] + d[1] * best, eye[2] + d[2] * best), best


def parse_args(argv):
    """viewprobe's own command line, as much of it as shotplan drives. Unknown flags are ignored
    on purpose: the stand-in must not fail on a flag the planner passes through."""
    out = {"pick": [], "nav_at": [], "inside_at": [], "refs_at": [],
           "eye": None, "yaw": 0.0, "pitch": 0.0, "fov": 65.0, "size": 320, "look": None,
           "cell": None}
    takes = {"--eye", "--yaw", "--pitch", "--fov", "--size", "--look", "--msaa", "--nav",
             "--out", "--instance", "--profile", "--game", "--order"}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--pick":
            out["pick"].append(argv[i + 1]); i += 2
        elif a == "--nav-at":
            out["nav_at"].append(argv[i + 1]); i += 2
        elif a == "--inside-at":
            out["inside_at"].append(argv[i + 1]); i += 2
        elif a == "--refs-at":
            out["refs_at"].append(argv[i + 1]); i += 2
        elif a in ("--eye", "--look"):
            out[a[2:]] = tuple(float(v) for v in argv[i + 1].split(",")); i += 2
        elif a in ("--yaw", "--pitch", "--fov"):
            out[a[2:]] = float(argv[i + 1]); i += 2
        elif a == "--size":
            out["size"] = int(argv[i + 1]); i += 2
        elif a in takes:
            i += 2
        elif a.startswith("--"):
            i += 1
        else:
            if out["cell"] is None:
                out["cell"] = a
            i += 1
    return out


def main(argv):
    scene = load_scene()
    a = parse_args(argv)
    eye = a["eye"] or (0.0, 0.0, 0.0)
    yaw, pitch = a["yaw"], a["pitch"]
    if a["look"]:
        dx, dy = a["look"][0] - eye[0], a["look"][1] - eye[1]
        yaw = math.degrees(math.atan2(dx, dy)) % 360.0
        dxy = math.hypot(dx, dy)
        pitch = math.degrees(math.atan2(eye[2] - a["look"][2], dxy)) if dxy > 1 else 0.0
    f, r, u = basis(yaw, pitch)
    t = math.tan(math.radians(a["fov"]) / 2.0)
    size = a["size"]

    print(f"cell {a['cell']} (interior)  000000:Test.esm")
    print(f"winning state: {len(scene.get('boxes', []))} refs, 1 navmeshes  (0.1s)")

    for spec in a["pick"]:
        ix, iy = (int(v) for v in spec.split(","))
        nx = (ix + 0.5) / size * 2.0 - 1.0
        ny = ((size - 1 - iy) + 0.5) / size * 2.0 - 1.0
        d = tuple(f[k] + r[k] * nx * t + u[k] * ny * t for k in range(3))
        n = math.sqrt(sum(c * c for c in d))
        d = tuple(c / n for c in d)
        got = trace(scene, eye, d)
        if got is None:
            print(f"   pick {ix},{iy}: background")
        else:
            (hx, hy, hz), dist = got
            # viewprobe rounds the printed position and distance to whole units. The planner's
            # tolerances are sized for exactly this, so the stand-in must round the same way.
            print(f"   pick {ix},{iy}: {hx:.0f},{hy:.0f},{hz:.0f}  cell (0,0)  {dist:.0f} u from the eye")

    silent_nav = {tuple(p) for p in scene.get("navSilent", [])}
    for spec in a["nav_at"]:
        x, y = (int(v) for v in spec.split(","))
        if (x, y) in silent_nav:
            continue
        levels = [n for n in scene.get("nav", [])
                  if n["min"][0] <= x <= n["max"][0] and n["min"][1] <= y <= n["max"][1]]
        if levels:
            for k, n in enumerate(levels):
                print(f"   nav-at {x},{y}: {n.get('name', 'Test.esm')} tri {100 + k} covers it, "
                      f"z {n['z']:.1f}, flags 0x0 - drawn")
        else:
            near = None
            for n in scene.get("nav", []):
                cx = min(max(x, n["min"][0]), n["max"][0])
                cy = min(max(y, n["min"][1]), n["max"][1])
                dd = math.hypot(x - cx, y - cy)
                near = dd if near is None else min(near, dd)
            if near is None:
                print(f"   nav-at {x},{y}: NO winning triangle covers this point")
            else:
                print(f"   nav-at {x},{y}: NO winning triangle covers this point; "
                      f"nearest is Test.esm tri 1, {near:.1f} u away")

    silent_inside = {tuple(p) for p in scene.get("insideSilent", [])}
    for spec in a["inside_at"]:
        x, y, z = (int(v) for v in spec.split(","))
        if (x, y, z) in silent_inside:
            continue
        hits = [b for b in scene.get("boxes", [])
                if b.get("physical", True)
                and b["min"][0] <= x <= b["max"][0] and b["min"][1] <= y <= b["max"][1]
                and b["min"][2] <= z <= b["max"][2]]
        print(f"   inside-at {x},{y},{z}: {len(hits)} ref" if hits
              else f"   inside-at {x},{y},{z}: no physical")
        for b in hits:
            dims = [b["max"][k] - b["min"][k] for k in range(3)]
            rays = {"+x": b["max"][0] - x, "-x": x - b["min"][0],
                    "+y": b["max"][1] - y, "-y": y - b["min"][1],
                    "up": b["max"][2] - z, "down": z - b["min"][2]}
            txt = " ".join(f"{k} {rays[k]:.0f}" for k in ("+x", "-x", "+y", "-y", "up", "down"))
            print(f"      {b['id']}  {b.get('type', 'Static')}  {b.get('edid', '-')}  "
                  f"dims {dims[0]:.0f}x{dims[1]:.0f}x{dims[2]:.0f} u  rays {txt}")

    for spec in a["refs_at"]:
        x, y, rad = (int(float(v)) for v in spec.split(","))
        rows = []
        for b in scene.get("boxes", []):
            c = [(b["min"][k] + b["max"][k]) / 2.0 for k in range(3)]
            dd = math.hypot(c[0] - x, c[1] - y)
            if dd <= rad:
                rows.append((dd, b, c))
        print(f"   ---- refs within {rad} u of {x},{y}: {len(rows)} ----")
        for dd, b, c in sorted(rows, key=lambda t: t[0]):
            print(f"      {dd:.0f} u  {b['id']}  at {c[0]:.0f},{c[1]:.0f},{c[2]:.0f}  scale 1.00  "
                  f"[drawn]  base {b.get('type', 'Static')} 000002:Test.esp {b.get('edid', '-')}")

    print("   textured: 1 shapes (1 with a real diffuse)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
