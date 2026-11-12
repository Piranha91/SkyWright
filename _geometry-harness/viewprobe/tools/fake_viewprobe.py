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
                "meshUnreadable": false,    # true = --inside-at reports no rays for it
                "shell": false, "twoSided": false, "mirrored": false,  # the --inside-at facing (see there)
                "facing": {"+x": "back"}}],  # per-axis facing override
     "nav":  [{"min": [x,y], "max": [x,y], "z": 0.0, "name": "Test.esp tri",
               "flags": 0}],            # flags 8 = 0x08 deleted: printed NOT DRAWN, as viewprobe does
     "failOnCullAbove": false,       # exit 1 on any call carrying --cull-above (a failed fallback probe)
     "navSilent": [[x,y]],           # queried points that get NO nav-at line at all
     "insideSilent": [[x,y,z]],      # queried points that get NO inside-at line at all
     "clearSilent": [[x,y,z]],       # queried points that get NO clear-at line at all
     "clearNoBox": 0,                # physical refs the OBND broad phase could not offer
     "clearUnread": 0,               # candidate meshes that could not be read
     "clearTerrain": "terrain: ...", # the terrain state clear-at reports (default: interior)
     "terrain": {"origin": [x0, y0], "step": 128, "z": [[...]]},  # a LAND vertex grid: clear-at reports the ground
                                     # and its vertical rays meet it (000000:TERRAIN); outside the grid clearTerrain,
                                     # else NO heightmap. Not drawn in frames
     "exterior": false,              # true = the cell header says (exterior), as an exterior CELL record does
     "contacts": false,              # true = --contacts-of K prints K's drawn box and its touches / rests-on lines
     "clearObndOnly": 0,             # refs the broad phase could select only by their record box
     "clearObndWouldMiss": 0,        # candidates the record box alone would have excluded
     "insideObndOnly": 0,            # refs --inside-at could nominate only by their record box
     "insideUnreadable": 0,          # physical refs whose mesh would not read, so never ruled out
     "insideCoverStyle": "mesh",     # "mesh" | "legacy" (the obnd arm) | "none" (older renderer)
     "insideFacingStyle": "stated",  # "none" omits each row's `facing` field (a renderer from before it)
     "configFingerprint": "<64 hex>",  # what viewprobe prints for a candidate configuration
     "configFingerprintAsks": [1],     # print it only on these 1-based ASKING invocations
     "unreadable": [{"kind": "mesh", "asset": "meshes\\x.nif", "reason": "...",
                     "at": [["00ABCD:Test.esp", x, y, z, r]]}],   # r null = unmeasured
                                     # viewprobe's unreadable census; exit 3 unless the call
                                     # carries --allow-unreadable-assets (then SUPPRESSED, exit 0)
     "unreadableEnvOptOut": false,   # exit 0 without the flag, as VP_ALLOW_UNREADABLE_ASSETS=1 would
     "notDrawnByRule": ["meshes\\water.nif"],  # the not-drawn-by-rule block; never fails
     "waterZ": -100.0,               # a cell water surface, printed as viewprobe's `water z` line
     "navLinks": {"SRC=TGT": {...}}, # --nav-link answers, printed as viewprobe's `nav-link {json}` rows
     "navmeshes": [{"formKey": K, "cell": C, "winner": "P.esp", "tris": {"7": [[x,y],[x,y]]},
                    "versions": {"P.esp": {"rect": [[x,y],[x,y]], "triangles": N}}}]}
                                     # drawn on the floor plane from above: the positional cell's in
                                     # --nav-color, each --nav-cell's in its colour, --red over both,
                                     # --nav-version substituting; writes <cell>-navmeshes.json as viewprobe does

Boxes may also carry "pos": [x,y,z] (the placement --eye-ref/--look-ref resolve to; default the
box's bottom centre) and "drawn": "NOT DRAWN: base carries no model" (the --refs-at verdict).

Also answered, for the capture point picker: --eye-ref/--look-ref/--eye-height (the resolved
`eye ... -> look ...` line), --ref KEY=colour with --ref-surface (each pixel whose first depth-writing
hit is a highlighted box is painted that colour, in the renderer's measured report-mode values; a
highlighted alpha box prints `subject-surface-refused`), and --worklist FILE with --out DIR: every shot
prints `=== worklist cell 1/1 shot K/N: label ===`, answers the run's queries and picks at its own
camera, and writes `<cell>-c1sK-<label>-fp.png`.
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


def terrain_z(scene, x, y):
    """The fake LAND under a world X,Y, or None outside it. `terrain` is a vertex grid, {"origin": [x0, y0], "step": 128,
    "z": [[z at (x0 + i*step, y0 + j*step) for j] for i]}, split on viewprobe's own diagonal (Program.cs terrainHeightAt:
    the lower triangle (x,y)-(x+1,y)-(x+1,y+1) when u >= v). Only clear-at's vertical rays and its terrain note read it;
    the frames do not draw it."""
    t = scene.get("terrain")
    if not t:
        return None
    s = float(t.get("step", 128))
    zz = t["z"]
    fx, fy = (x - t["origin"][0]) / s, (y - t["origin"][1]) / s
    nx, ny = len(zz) - 1, len(zz[0]) - 1
    if not (0 <= fx <= nx and 0 <= fy <= ny):
        return None
    ix, iy = min(int(math.floor(fx)), nx - 1), min(int(math.floor(fy)), ny - 1)
    u, v = fx - ix, fy - iy
    h00, h10, h11, h01 = zz[ix][iy], zz[ix + 1][iy], zz[ix + 1][iy + 1], zz[ix][iy + 1]
    return h00 + (h10 - h00) * u + (h11 - h10) * v if u >= v else h00 + (h11 - h01) * u + (h01 - h00) * v


def trace(scene, eye, d, cull=None):
    got = trace_owner(scene, eye, d, cull)
    return None if got is None else got[:2]


def trace_owner(scene, eye, d, cull=None):
    """(hit point, distance) for the first DEPTH-WRITING surface along the ray, or None.

    Alpha-blended boxes are skipped exactly as viewprobe's depth probe skips them -- that is the
    known limit the planner has to keep reporting, so the stand-in has to reproduce it.
    """
    best, owner = None, None
    for b in scene.get("boxes", []):
        if b.get("alpha"):
            continue
        # --cull-above: viewprobe drops a STATIC triangle whose vertices all sit above the cut, so
        # a box wholly above it is gone and a box spanning it stays whole (README "Dollhouse slice").
        if cull is not None and b["min"][2] > cull:
            continue
        t = ray_box(eye, d, b["min"], b["max"])
        if t is not None and (best is None or t < best):
            best, owner = t, b
    fz = scene.get("floorZ")
    if fz is not None and d[2] < -1e-12:
        t = (fz - eye[2]) / d[2]
        if t > 1e-6 and (best is None or t < best):
            best, owner = t, None
    if best is None:
        return None
    return (eye[0] + d[0] * best, eye[1] + d[1] * best, eye[2] + d[2] * best), best, owner


def parse_args(argv):
    """viewprobe's own command line, as much of it as shotplan drives. Unknown flags are ignored
    on purpose: the stand-in must not fail on a flag the planner passes through."""
    out = {"pick": [], "nav_at": [], "inside_at": [], "refs_at": [], "clear_at": [],
           "clear_reach": 512.0,
           "eye": None, "yaw": 0.0, "pitch": 0.0, "fov": 65.0, "size": 320, "look": None,
           "cell": None, "cull": None, "eye_ref": None, "look_ref": None, "eye_height": 120.0,
           "refs": [], "ref_surface": False, "worklist": None, "out": None,
           "nav_link": [], "nav_cell": [], "nav_version": [], "red": [], "nav": "off", "nav_color": "auto",
           "contacts_of": []}
    takes = {"--eye", "--yaw", "--pitch", "--fov", "--size", "--look", "--msaa", "--nav",
             "--instance", "--profile", "--game", "--order"}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--pick":
            out["pick"].append(argv[i + 1]); i += 2
        elif a == "--nav-at":
            out["nav_at"].append(argv[i + 1]); i += 2
        elif a in ("--nav-link", "--nav-cell", "--nav-version", "--red"):
            out[a[2:].replace("-", "_")].append(argv[i + 1]); i += 2
        elif a == "--nav":
            out["nav"] = argv[i + 1]; i += 2
        elif a == "--nav-color":
            out["nav_color"] = argv[i + 1]; i += 2
        elif a == "--inside-at":
            out["inside_at"].append(argv[i + 1]); i += 2
        elif a == "--refs-at":
            out["refs_at"].append(argv[i + 1]); i += 2
        elif a == "--clear-at":
            out["clear_at"].append(argv[i + 1]); i += 2
        elif a == "--contacts-of":
            out["contacts_of"].append(argv[i + 1]); i += 2
        elif a == "--eye-ref":
            out["eye_ref"] = argv[i + 1]; i += 2
        elif a == "--look-ref":
            out["look_ref"] = argv[i + 1]; i += 2
        elif a == "--eye-height":
            out["eye_height"] = float(argv[i + 1]); i += 2
        elif a == "--ref":
            out["refs"].append(argv[i + 1]); i += 2
        elif a == "--ref-surface":
            out["ref_surface"] = True; i += 1
        elif a == "--worklist":
            out["worklist"] = argv[i + 1]; i += 2
        elif a == "--out":
            out["out"] = argv[i + 1]; i += 2
        elif a == "--clear-reach":
            out["clear_reach"] = float(argv[i + 1]); i += 2
        elif a in ("--eye", "--look"):
            out[a[2:]] = tuple(float(v) for v in argv[i + 1].split(",")); i += 2
        elif a in ("--yaw", "--pitch", "--fov"):
            out[a[2:]] = float(argv[i + 1]); i += 2
        elif a == "--cull-above":
            out["cull"] = float(argv[i + 1]); i += 2
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
    if a["cull"] is not None and scene.get("failOnCullAbove"):
        print("fake_viewprobe: failing on --cull-above as the scene asks", file=sys.stderr)
        return 1
    if a["worklist"]:
        with open(a["worklist"], "r", encoding="utf-8") as fh:
            wl = json.load(fh)
        cells = wl.get("cells") or []
        rc = 0
        for ci, cell in enumerate(cells):
            shots = cell.get("shots") or []
            for si, shot in enumerate(shots):
                print(f"=== worklist cell {ci + 1}/{len(cells)} shot {si + 1}/{len(shots)}: {shot.get('label', '')} ===")
                b = dict(a)
                b["cell"] = cell.get("cell")
                b["eye"] = tuple(float(v) for v in shot["eye"])
                b["look"] = tuple(float(v) for v in shot["look"]) if shot.get("look") else None
                b["yaw"], b["pitch"] = float(shot.get("yaw", 0.0)), float(shot.get("pitch", 0.0))
                if shot.get("fov") is not None:
                    b["fov"] = float(shot["fov"])
                label = "".join(c if c.isalnum() or c in "-_" else "_" for c in str(shot.get("label", "")))
                png = (os.path.join(b["out"], f"{b['cell']}-c{ci + 1}s{si + 1}-{label}-fp.png")
                       if b["out"] else None)
                rc = max(rc, one_shot(scene, b, argv, png))
        return rc
    if a["eye_ref"] or a["look_ref"]:
        def ref_pos(key, lift):
            for bx in scene.get("boxes", []):
                if bx["id"] == key:
                    p0 = bx.get("pos") or [(bx["min"][0] + bx["max"][0]) / 2.0,
                                           (bx["min"][1] + bx["max"][1]) / 2.0, bx["min"][2]]
                    return (p0[0], p0[1], p0[2] + lift)
            print(f"'{key}' is not a placed ref in this cell's winning state.", file=sys.stderr)
            return None
        if a["eye_ref"]:
            a["eye"] = ref_pos(a["eye_ref"], a["eye_height"])
            if a["eye"] is None:
                print("first-person mode needs --eye X,Y,Z or --eye-ref FORMID", file=sys.stderr)
                return 1
        if a["look_ref"]:
            a["look"] = ref_pos(a["look_ref"], 96.0)
    png = (os.path.join(a["out"], f"{a['cell']}-fp.png") if a["out"] and a["refs"] else None)
    if a["out"] and a["eye"] is not None and a["nav"] != "off" and scene.get("navmeshes"):
        png = os.path.join(a["out"], "".join(c if c.isalnum() else "_" for c in a["cell"]) + "-fp.png")
    return one_shot(scene, a, argv, png)


# The report-mode values the renderer's --ref-surface marks measure at (2026-10-19, flat overlay
# after tone mapping): what a frame of the real renderer carries, so a picker reads the same pixels.
SURFACE_RGB = {"cyan": (19, 255, 255), "magenta": (255, 76, 255), "orange": (255, 89, 26),
               "yellow": (255, 255, 0), "green": (0, 255, 0), "blue": (0, 0, 255)}


def one_shot(scene, a, argv, png):
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

    print(f"cell {a['cell']} ({'exterior' if scene.get('exterior') else 'interior'})  000000:Test.esm")
    if scene.get("waterZ") is not None:
        print(f"      water z {scene['waterZ']:.0f}: 1 cell(s) - 1 from the cell's own XCLW, 0 inherited "
              "from the worldspace default")
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
        got = trace(scene, eye, d, a["cull"])
        if got is None:
            print(f"   pick {ix},{iy}: background")
        else:
            (hx, hy, hz), dist = got
            # viewprobe rounds the printed position and distance to whole units. The planner's
            # tolerances are sized for exactly this, so the stand-in must round the same way.
            print(f"   pick {ix},{iy}: {hx:.0f},{hy:.0f},{hz:.0f}  cell (0,0)  {dist:.0f} u from the eye")

    hl = {}
    for spec in a["refs"]:
        key, _, col = spec.partition("=")
        hl[key] = col or "orange"
    if a["ref_surface"] and hl:
        print("subject-surface-mode: exact placed opaque triangles; depth-tested; no inflated hull or occluded ghost")
        for bx in sorted(scene.get("boxes", []), key=lambda b: b["id"]):
            if bx["id"] in hl and bx.get("alpha"):
                print(f"subject-surface-refused: {bx['id']}: alpha, effect, marker or non-depth-writing shape; "
                      "participant coverage incomplete")
    # --contacts-of K (scene "contacts": true): K's drawn box and what its box touches or rests on, in viewprobe's two
    # line shapes. Off by default, so a scene written before the picker read drawn boxes keeps its answers.
    if scene.get("contacts"):
        boxes = {b["id"]: b for b in scene.get("boxes", [])}
        for key in a["contacts_of"]:
            k = boxes.get(key)
            if k is None:
                continue
            touch, rests = [], []
            for o in scene.get("boxes", []):
                if o["id"] == key:
                    continue
                if all(o["min"][i] <= k["max"][i] + 1 and k["min"][i] <= o["max"][i] + 1 for i in range(3)):
                    touch.append(o["id"])
                    if abs(o["max"][2] - k["min"][2]) <= 1 or o["min"][2] <= k["min"][2] <= o["max"][2]:
                        rests.append(o["id"])
            print(f"contacts-of {key}: touches [{', '.join(sorted(touch))}]; rests on [{', '.join(sorted(rests))}]; "
                  "unassessed []")
            print(f"contacts-of {key}: box {k['min'][0]:.0f},{k['min'][1]:.0f},{k['min'][2]:.0f} "
                  f"{k['max'][0]:.0f},{k['max'][1]:.0f},{k['max'][2]:.0f}")
    for spec in a["nav_link"]:
        row = dict((scene.get("navLinks") or {}).get(spec) or {
            "source": spec.split("=")[0], "target": spec.split("=")[-1], "status": "refused",
            "reason": f"the source navmesh {spec.split('=')[0]} has no winning version in this load order"})
        row["configuration"] = (scene.get("configFingerprint")
                                if ("--order" in argv or "--config-identity" in argv) else None)
        print("   nav-link " + json.dumps(row))
    if a["cull"] is not None and png:
        print(f"view-label: SLICED TOP-DOWN above z {a['cull']:.0f}; not the player's view | rect 4,{size - 24},200,20")
    if png and a["nav"] != "off" and scene.get("navmeshes"):
        write_nav_frame(scene, a, eye, f, r, u, t, size, yaw, pitch, argv, png)
    elif png:
        write_frame(scene, eye, f, r, u, t, size, hl if a["ref_surface"] else {}, a["cull"], png)

    silent_nav = {tuple(p) for p in scene.get("navSilent", [])}
    for spec in a["nav_at"]:
        x, y = (int(v) for v in spec.split(","))
        if (x, y) in silent_nav:
            continue
        levels = [n for n in scene.get("nav", [])
                  if n["min"][0] <= x <= n["max"][0] and n["min"][1] <= y <= n["max"][1]]
        if levels:
            for k, n in enumerate(levels):
                fl = int(n.get("flags", 0))
                why = "NOT DRAWN: flagged deleted (0x08)" if fl & 0x08 else "drawn"
                print(f"   nav-at {x},{y}: {n.get('name', 'Test.esm')} tri {100 + k} covers it, "
                      f"z {n['z']:.1f}, flags 0x{fl:X} - {why}")
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
            # Which way the nearest face on each ray is turned. From inside a solid box every ray leaves through a
            # back face; `shell` is a box seen from inside its own hollow (faces turned in, so front), `twoSided` a
            # shape drawn on both sides, `mirrored` a shape whose winding a mirrored transform flips (viewprobe prints
            # `-`: the facing is unknown), `facing` an explicit per-axis answer. `insideFacingStyle: "none"` omits the
            # field, as a renderer from before it existed does.
            face = ("front" if b.get("shell") else "two" if b.get("twoSided") else "-" if b.get("mirrored")
                    else "back")
            fac = {k: face for k in ("+x", "-x", "+y", "-y", "up", "down")}
            fac.update(b.get("facing") or {})
            if not b.get("meshUnreadable") and scene.get("insideFacingStyle", "stated") != "none":
                ray_txt += "  facing " + " ".join(f"{k} {fac[k]}" for k in ("+x", "-x", "+y", "-y", "up", "down"))
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
        gz = terrain_z(scene, x, y)
        tnote = (f"terrain: ground at z {gz:.1f}" if gz is not None else
                 scene.get("clearTerrain") or ("terrain: NO heightmap covers this XY - absence below this eye is not "
                                               "evidence" if scene.get("terrain") else None))
        print(f"   clear-at {x},{y},{z}: nearest placed geometry on each world axis within "
              f"{reach:.0f} u - {len(phys)} candidate(s) in the broad phase, {len(phys)} with a "
              f"readable mesh, {scene.get('clearUnread', 0)} unreadable, "
              f"{scene.get('clearNoBox', 0)} physical ref(s) skipped for want of a usable OBND, "
              f"{scene.get('clearNoMesh', 0)} physical ref(s) with no model at all, "
              f"{scene.get('clearObndOnly', 0)} selected by record OBND because their mesh could not "
              f"be read, {scene.get('clearObndWouldMiss', 0)} admitted by mesh bounds that the record "
              f"box alone would have excluded; "
              f"{tnote or 'terrain: interior cell, none expected'} "
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
            # the fake LAND: the two vertical rays read the height field, as viewprobe's clear-at does
            if gz is not None and name in ("up", "down"):
                t = (gz - q[2]) * sgn
                if 1e-3 < t <= reach and (best is None or t < best):
                    best, who = t, {"terrain": True}
            if best is not None and who.get("terrain"):
                print(f"      {name:<4} {best:7.1f} u  000000:TERRAIN  LandHeightmap  at {x},{y},{gz:.0f}  "
                      "scale 1.00  mesh (terrain)")
            elif best is None:
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
                  f"[{b.get('drawn', 'drawn')}]  base {b.get('type', 'Static')} 000002:Test.esp {b.get('edid', '-')}")

    print("   textured: 1 shapes (1 with a real diffuse)")
    if a["eye"] is not None:
        lk = a["look"] or tuple(eye[k] + f[k] * 512.0 for k in range(3))
        print(f"   eye {eye[0]:.0f},{eye[1]:.0f},{eye[2]:.0f} -> look {lk[0]:.0f},{lk[1]:.0f},{lk[2]:.0f}  "
              f"yaw {yaw:.1f} pitch {pitch:.1f} fov {a['fov']:.0f}  (1 shapes)")
    # The census in UnreadableAssets.PrintCensus's shape, with the separator a redirected .NET
    # console actually delivers (" - "), and PrintNotDrawnByRule's block after it.
    unread = scene.get("unreadable") or []
    allowed = "--allow-unreadable-assets" in argv
    env_opt = scene.get("unreadableEnvOptOut", False)
    if unread:
        tail = ("exit 3 SUPPRESSED by --allow-unreadable-assets" if allowed else
                "exit 3 SUPPRESSED by VP_ALLOW_UNREADABLE_ASSETS=1 in the environment" if env_opt else
                "this run fails with exit 3")
        print(f"   assets FOUND but UNREADABLE: {len(unread)} - the picture is missing what they "
              f"would have drawn; {tail}")
        for e in unread:
            print(f"      {e['kind']:<8} {e['asset']} - {e['reason']}")
            for fk, x, y, z, r in e.get("at", []):
                print(f"         at {fk}  {x:.1f},{y:.1f},{z:.1f}  r " + ("?" if r is None else f"{r:.1f}"))
    nd = scene.get("notDrawnByRule") or []
    if nd:
        print(f"   meshes NOT DRAWN by the renderer's rule: {len(nd)} - found and read; the picture "
              "lacks them by design, not by a failed read; the run does not fail")
        for asset in nd:
            print(f"      mesh     {asset} - the rendering library skips every shape of it by rule")
    if unread and not allowed and not env_opt:
        return 3
    return 0


# The report-mode navmesh colours of the real renderer, measured on a lab frame (capture_acquire.NAV_RGB says where).
NAV_RGB = {"cyan": (0, 255, 255), "magenta": (255, 0, 255), "red": (255, 0, 0), "green": (0, 255, 0),
           "yellow": (255, 255, 0), "blue": (0, 0, 255)}


def nav_layers(scene, a):
    """(navmesh, plugin version, colour, red indices) per drawn navmesh, and the receipt's records."""
    colours = {a["cell"]: a["nav_color"] if a["nav_color"] != "auto" else "green"}
    for spec in a["nav_cell"]:
        k, _, col = spec.partition("=")
        colours[k] = col or "green"
    subs = dict(spec.split("=", 1) for spec in a["nav_version"])
    reds = {}
    for spec in a["red"]:
        k, _, idx = spec.rpartition(":")
        reds.setdefault(k, []).append(int(idx))
    drawn, records = [], []
    for n in scene.get("navmeshes") or []:
        if n["cell"] not in colours:
            continue
        plugin = n["winner"] if scene.get("ignoreNavVersion") else subs.get(n["formKey"], n["winner"])
        if plugin not in n["versions"]:
            print(f"--nav-version: {plugin} provides no version of {n['formKey']} in the selected cells.", file=sys.stderr)
            raise SystemExit(2)
        ver = n["versions"][plugin]
        idx = reds.get(n["formKey"]) or []
        drawn.append((n, ver, colours[n["cell"]], [i for i in idx if i < ver["triangles"]]))
        records.append({"FormKey": n["formKey"], "Cell": n["cell"], "Provider": plugin, "TriangleCount": ver["triangles"],
                        "Colour": colours[n["cell"]], "DrawableTrianglesInBand": ver["triangles"],
                        "RedWholeRecord": False,
                        "RedRequests": [{"Index": i, "Exists": i < ver["triangles"], "DrawableInBand": i < ver["triangles"]}
                                        for i in sorted(idx)] or None})
    return drawn, records


def write_nav_frame(scene, a, eye, f, r, u, t, size, yaw, pitch, argv, path):
    """A frame from above: each drawn navmesh version's rectangle on the floor plane in its colour, red triangles over it."""
    from PIL import Image, PngImagePlugin
    drawn, records = nav_layers(scene, a)
    img = Image.new("RGB", (size, size), (110, 120, 100))
    px = img.load()
    fz = float(scene.get("floorZ") or 0.0)

    def inside(rect, x, y):
        return rect[0][0] <= x <= rect[1][0] and rect[0][1] <= y <= rect[1][1]
    for iy in range(size):
        ny = ((size - 1 - iy) + 0.5) / size * 2.0 - 1.0
        for ix in range(size):
            nx = (ix + 0.5) / size * 2.0 - 1.0
            d = tuple(f[k] + r[k] * nx * t + u[k] * ny * t for k in range(3))
            if d[2] >= -1e-9:
                continue
            k_ = (fz - eye[2]) / d[2]
            x, y = eye[0] + d[0] * k_, eye[1] + d[1] * k_
            colour = None
            for n, ver, col, red in drawn:
                if any(inside(n["tris"][str(i)], x, y) for i in red if str(i) in n.get("tris", {})):
                    colour = "red"
                    break
                if inside(ver["rect"], x, y):
                    colour = col
            if colour:
                px[ix, iy] = NAV_RGB[colour]
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    meta = PngImagePlugin.PngInfo()
    meta.add_text("viewprobe", f"args: {' '.join(argv)} | pose: eye {eye[0]:.1f},{eye[1]:.1f},{eye[2]:.1f} yaw {yaw:.1f} "
                               f"pitch {pitch:.1f} fov {a['fov']:.0f} | cell {a['cell']}")
    img.save(path, pnginfo=meta)
    if a["nav_cell"] or a["nav_version"]:
        receipt = {"SchemaVersion": 1, "TargetCell": a["cell"], "AdditionalCells": a["nav_cell"],
                   "RecordSubstitutions": a["nav_version"], "Layers": [{"Label": "fake", "Records": records}]}
        with open(path[:-len("-fp.png")] + "-navmeshes.json", "w", encoding="utf-8") as fh:
            json.dump(receipt, fh, indent=1)


def write_frame(scene, eye, f, r, u, t, size, hl, cull, path):
    """The frame a --ref-surface render would show: a highlighted box's colour where it is the first
    depth-writing hit, a neutral grey elsewhere. Pixel centres and axes are the --pick convention."""
    from PIL import Image
    img = Image.new("RGB", (size, size), (128, 128, 128))
    px = img.load()
    for iy in range(size):
        ny = ((size - 1 - iy) + 0.5) / size * 2.0 - 1.0
        for ix in range(size):
            nx = (ix + 0.5) / size * 2.0 - 1.0
            d = tuple(f[k] + r[k] * nx * t + u[k] * ny * t for k in range(3))
            n = math.sqrt(sum(c * c for c in d))
            got = trace_owner(scene, eye, tuple(c / n for c in d), cull)
            if got is not None and got[2] is not None and got[2]["id"] in hl:
                px[ix, iy] = SURFACE_RGB.get(hl[got[2]["id"]], (255, 255, 255))
            elif got is None:
                px[ix, iy] = (179, 209, 235)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    img.save(path)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
