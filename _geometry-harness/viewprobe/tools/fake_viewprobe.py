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
                "min": [x,y,z], "max": [x,y,z],      # the MESH
                "obnd": {"min": [...], "max": [...]},  # the record's CLAIM about it, if it differs
                "alpha": false,      # true = draws with depth writes OFF: the ray passes through
                "physical": true,    # false = not offered by --inside-at
                "meshUnreadable": false}],  # true = --inside-at reports no rays for it
     "nav":  [{"min": [x,y], "max": [x,y], "z": 0.0, "name": "Test.esp tri"}],
     "navSilent": [[x,y]],           # queried points that get NO nav-at line at all
     "insideSilent": [[x,y,z]],      # queried points that get NO inside-at line at all
     "clearSilent": [[x,y,z]],       # queried points that get NO clear-at line at all
     "clearNoBox": 0,                # physical refs the OBND broad phase could not offer
     "clearUnread": 0,               # candidate meshes that could not be read
     "clearTerrain": "terrain: ...", # the terrain state clear-at reports (default: interior)
     "clearObndOnly": 0,             # refs the broad phase could select only by their record box
     "clearObndWouldMiss": 0,        # candidates the record box alone would have excluded
     "insideObndOnly": 0,            # refs --inside-at could nominate only by their record box
     "insideUnreadable": 0,          # physical refs whose mesh would not read, so never ruled out
     "insideCoverStyle": "mesh",     # "mesh" | "legacy" (the obnd arm) | "none" (older renderer)
     "configFingerprint": "<64 hex>",  # what viewprobe prints for a candidate configuration
     "configFingerprintAsks": [1]}     # print it only on these 1-based ASKING invocations
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
    out = {"pick": [], "nav_at": [], "inside_at": [], "refs_at": [], "clear_at": [],
           "clear_reach": 512.0,
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
        elif a == "--clear-at":
            out["clear_at"].append(argv[i + 1]); i += 2
        elif a == "--clear-reach":
            out["clear_reach"] = float(argv[i + 1]); i += 2
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
    # FAKE_VIEWPROBE_TRACE: one line per invocation, the argv as received. A test that wants to
    # know WHICH invocation carried a flag cannot read the planner's echo -- it goes to stderr and
    # is truncated to the last 14 tokens -- so the stand-in records what it was actually asked.
    trace_path = os.environ.get("FAKE_VIEWPROBE_TRACE")   # NOT `trace` -- that is the ray tracer
    if trace_path:
        with open(trace_path, "a", encoding="utf-8") as fh:   # NOT `f` -- the camera axis
            fh.write(" ".join(argv) + "\n")
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
    # Match the renderer's identity request gate: a candidate configuration or an explicit
    # profile-identity request. An unrequested identity must not make a binding test pass.
    if scene.get("configFingerprint") and ("--order" in argv or "--config-identity" in argv):
        # configFingerprintAsks: which ASKING invocations get an answer, 1-based, default all.
        # A real renderer can be asked and still report nothing -- viewprobe's own code lets a
        # refused resolve carry on rendering -- and a test cannot reach the stale-identity path
        # without a run where one verification answers and the next does not. The counter lives
        # beside the scene file, so it spans the processes of ONE run and dies with it.
        asks = scene.get("configFingerprintAsks")
        answer = True
        if asks is not None:
            counter = os.environ["FAKE_VIEWPROBE_SCENE"] + ".asks"
            asked = 0
            if os.path.exists(counter):
                with open(counter, encoding="utf-8") as fh:   # NOT `f`/`n` -- both are in use
                    asked = int(fh.read().strip() or 0)
            asked += 1
            with open(counter, "w", encoding="utf-8") as fh:
                fh.write(str(asked))
            answer = asked in asks
        if answer:
            print(f"   capture input configuration: {scene['configFingerprint']}")

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
        def declared(bx):
            """The record OBND a ref claims, which may differ from its mesh -- see `obnd`."""
            o = bx.get("obnd")
            return (o["min"], o["max"]) if o else (bx["min"], bx["max"])

        hits = []
        for b in scene.get("boxes", []):
            if not b.get("physical", True):
                continue
            lo, hi = declared(b)
            if lo[0] <= x <= hi[0] and lo[1] <= y <= hi[1] and lo[2] <= z <= hi[2]:
                hits.append(b)
        # `insideCoverStyle`: "mesh" (default) states the coverage, "legacy" reproduces the
        # --inside-bounds obnd wording, "none" omits it entirely -- an older renderer's output.
        style = scene.get("insideCoverStyle", "mesh")
        if style == "none":
            cover = None
        elif style == "legacy":
            cover = ("nominated by RECORD OBND (the legacy arm, kept for comparison: a box that "
                     "understates its mesh nominates nothing)")
        else:
            cover = (f"nominated by mesh bounds, {scene.get('insideObndOnly', 0)} by record OBND "
                     f"alone (mesh unreadable), {scene.get('insideUnreadable', 0)} physical ref(s) "
                     "whose mesh could not be read at all")
        tail = "" if cover is None else f"; {cover}"
        print(f"   inside-at {x},{y},{z}: {len(hits)} ref(s) whose box encloses it{tail}" if hits
              else f"   inside-at {x},{y},{z}: no physical ref's box encloses it{tail}")
        for b in hits:
            lo, hi = declared(b)
            dims = [hi[k] - lo[k] for k in range(3)]
            rays = {"+x": b["max"][0] - x, "-x": x - b["min"][0],
                    "+y": b["max"][1] - y, "-y": y - b["min"][1],
                    "up": b["max"][2] - z, "down": z - b["min"][2]}
            txt = " ".join(f"{k} {rays[k]:.0f}" for k in ("+x", "-x", "+y", "-y", "up", "down"))
            # `meshUnreadable` reproduces viewprobe's own wording for a ref whose mesh it could not
            # load: the row arrives with no ray distances at all, which is the shape the planner has
            # to decide on, and the only evidence left is the box.
            ray_txt = "rays n/a (mesh not loaded)" if b.get("meshUnreadable") else f"rays {txt}"
            print(f"      {b['id']}  {b.get('type', 'Static')}  {b.get('edid', '-')}  "
                  f"dims {dims[0]:.0f}x{dims[1]:.0f}x{dims[2]:.0f} u  {ray_txt}")

    # --clear-at: the nearest box surface on each world axis, whether or not anything encloses the
    # point. The real probe ray-casts the placed meshes; the stand-in intersects the toy AABBs,
    # which is the same answer for box-shaped geometry. `clearSilent` withholds a whole answer,
    # and `clearNoBox` / `clearUnread` declare the coverage the broad phase did NOT have, which is
    # what makes an empty answer read as UNRESOLVED rather than as clear.
    silent_clear = {tuple(p) for p in scene.get("clearSilent", [])}
    axes = (("+x", 0, +1), ("-x", 0, -1), ("+y", 1, +1), ("-y", 1, -1),
            ("up", 2, +1), ("down", 2, -1))
    reach = a["clear_reach"]
    phys = [b for b in scene.get("boxes", []) if b.get("physical", True)]
    for spec in a["clear_at"]:
        x, y, z = (int(float(v)) for v in spec.split(","))
        if (x, y, z) in silent_clear:
            continue
        q = (x, y, z)
        print(f"   clear-at {x},{y},{z}: nearest placed geometry on each world axis within "
              f"{reach:.0f} u - {len(phys)} candidate(s) in the broad phase, {len(phys)} with a "
              f"readable mesh, {scene.get('clearUnread', 0)} unreadable, "
              f"{scene.get('clearNoBox', 0)} physical ref(s) skipped for want of a usable OBND, "
              f"{scene.get('clearNoMesh', 0)} physical ref(s) with no model at all, "
              f"{scene.get('clearObndOnly', 0)} selected by record OBND because their mesh could not "
              f"be read, {scene.get('clearObndWouldMiss', 0)} admitted by mesh bounds that the record "
              f"box alone would have excluded; "
              f"{scene.get('clearTerrain', 'terrain: interior cell, none expected')} "
              "(broad phase: the mesh's own NIF bounds, with record OBND only as a counted fallback; "
              "the four lateral rays ignore terrain)")
        for name, ax, sgn in axes:
            other = [k for k in range(3) if k != ax]
            best, who = None, None
            for b in phys:
                # min/max is the MESH. `obnd` is the record's claim about it, which --inside-at
                # nominates by and which clear-at deliberately does NOT use: a box can be present
                # and wrong, and the ray test answers about the geometry.
                if not all(b["min"][k] <= q[k] <= b["max"][k] for k in other):
                    continue
                for face in (b["min"][ax], b["max"][ax]):
                    t = (face - q[ax]) * sgn
                    if 1e-3 < t <= reach and (best is None or t < best):
                        best, who = t, b
            # The scene's floor plane is geometry to the ray tracer above, so it is geometry here
            # too -- a toy room declaring `floorZ` has a floor under the eye. A scene that omits
            # `floorZ` has none, which is the shape the three Ratway cameras turned out to be.
            fz = scene.get("floorZ")
            if name == "down" and fz is not None:
                t = q[2] - fz
                if 1e-3 < t <= reach and (best is None or t < best):
                    best, who = t, {"id": "000000:Test.esm", "edid": "SceneFloor",
                                    "min": [0, 0, fz], "max": [0, 0, fz]}
            if best is None:
                print(f"      {name:<4}       -  nothing within {reach:.0f} u")
            else:
                c = [(who["min"][k] + who["max"][k]) / 2.0 for k in range(3)]
                print(f"      {name:<4} {best:7.1f} u  {who['id']}  {who.get('edid', '-')}  "
                      f"at {c[0]:.0f},{c[1]:.0f},{c[2]:.0f}  scale 1.00  mesh test\\{who['id']}.nif")

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
