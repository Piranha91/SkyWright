"""Offline known answers for terrain stations (C1980-C1989): outdoors, a ring station with no navmesh may stand on bare LAND.

    python tests/test_terrain_stations.py [C1980 ...]

No game and no renderer: `fake_viewprobe.py` over toy box scenes and a LAND vertex grid, with the REAL picker,
`capture_acquire.py` and `shotplan.py plan`. The rule under test (terrain standing-slope RESULT 2026-11-12, section 7):
in an exterior cell, a station that today's navmesh rule leaves with no floor (no navmesh level at its spot or along its
bearing, the nearest triangle beyond the off-mesh tolerance) may stand on the terrain when the LAND under its 64 u
footprint is planar to 4 degrees and at most 43.7 degrees (the steepest triangle of either diagonal counts), with no
placed mesh within 64 u on the lateral rays, nothing within 160 u overhead, and the floor ray meeting the terrain. Its eye
is that ground + the eye height and it is labelled "terrain, slope X deg, measured limit 43.7 deg". The coordinates are
made up; nothing here is evidence about any real cell.

The scene: the pair on its own navmesh pad at z 0, no other navmesh, and a LAND pad north of it (x -256..256,
y 128..640, vertices every 128 u) starting 120 u below the pair and falling away from it at the case's slope. Six ring stations stand wholly on it:
s13 (0,250) and s25 (0,400) on bearing 0, s14 and s26 on bearing 30, s24 and s36 on bearing 330 (250 and 400 u). Every other station has no ground.

    C1980  a 40 deg pad: camera-ready at eye level; the witness on the pad, eye = its ground + 120, labelled terrain
    C1981  a 50 deg pad: no eye-level view; every pad station refused, naming the slope and the 43.7 deg limit
    C1982  the 40 deg pad under an overhang (a slab 140-160 u over each pad station's feet): every pad station refused,
           naming the slab overhead; no eye-level view
    C1983  guard, the 40 deg pad beside navmesh: s13's spot has navmesh on the ground there and s25's bearing a terrace
           500 u out: both stay navmesh stations as today (the station at s13's spot on its navmesh, s25 moved to the
           terrace), neither labelled terrain
    C1984  a twisted quad under s13 (one vertex 48 u up): s13 refused as not planar; s26 and s36, whose footprints
           touch no quad with that vertex, still stand on terrain
    C1985  a flat stone on the pad under s13's centre: s13 refused, the floor ray meeting the stone, not the terrain
    C1986  a post 40 u east of s13: s13 refused, naming the post within 64 u; and a low rock beside s13's downhill corner
           (top 69 u over that corner's ground): refused at the corner's own ground + 40, which a centre + 84 point misses
    C1987  shotplan plan outdoors (floor z 40), no navmesh, a 30 deg LAND plane through z 0: a camera verifies, its eye its
           own ground + 120 (not the floor's), and the plan says it stands on terrain
    C1988  guard, the C1980 scene as an interior: no station stands on terrain (the rule is exterior only)
    C1989  the slope rule itself (shotplan.terrain_slope): 43.71 deg accepted, 44.0 refused, a 4.5 deg spread refused
"""
from __future__ import annotations

import math
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(SW.HARNESS / "viewprobe" / "tools"))
import test_capture_points as TCP  # noqa: E402

FAILS: list[str] = []
EYE_H = 120.0
X0, Y0, NX, NY, STEP = -256, 128, 5, 5, 128
PAD = {"s13": (0, 250), "s25": (0, 400), "s14": (125, 217), "s26": (200, 346), "s24": (-125, 217), "s36": (-200, 346)}


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def plane(deg):
    """The pad's LAND: 120 u below the pair's floor at its south edge, falling north at `deg`. The drop keeps the ring's
    150 u stations, which are within the off-mesh tolerance of the pair's own navmesh, off today's furniture allowance:
    the ground under them lies outside the storey band, so today refuses them as it refuses the pad stations."""
    t = math.tan(math.radians(deg))
    return lambda x, y: -(y - Y0) * t - 120.0


def grid(fn, bumps=None):
    z = [[round(fn(X0 + i * STEP, Y0 + j * STEP), 3) for j in range(NY)] for i in range(NX)]
    for (vx, vy), dz in (bumps or {}).items():
        z[(vx - X0) // STEP][(vy - Y0) // STEP] += dz
    return {"origin": [X0, Y0], "step": STEP, "z": z}


def pad_scene(fx, deg, *extra, exterior=True, nav=(), bumps=None):
    sc = TCP.scene(TCP.box(fx.cy, [-120, -60, 0], [0, 60, 80]), TCP.box(fx.mg, [0, -60, 0], [120, 60, 80]),
                   *extra, shell=False)
    sc["nav"] = [{"min": [-60, -60], "max": [60, 60], "z": 0.0, "name": "Test.esm"}] + list(nav)
    del sc["floorZ"]
    sc["terrain"] = grid(plane(deg), bumps)
    if exterior:
        sc["exterior"] = True
    return sc


def ground(deg, xy):
    return plane(deg)(*xy)


def stations_of(doc):
    """The station rows, from a points document or from a refusal that carries them."""
    if not doc:
        return []
    if doc.get("status") == "refused":
        return (doc.get("refusal") or {}).get("stations") or []
    return (doc.get("witness") or {}).get("stations") or []


def by_id(rows):
    return {r["id"]: r for r in rows}


def is_terrain(row):
    return ((row or {}).get("standing") or {}).get("kind") == "terrain"


def view_kind(acq):
    return ((acq or {}).get("camera") or {}).get("viewKind")


def c1980(fx):
    proc, acq, _ = TCP.acquire_with(fx, pad_scene(fx, 40.0), "c1980", "auto")
    ok = proc.returncode == 0 and (acq or {}).get("status") == "camera-ready" and view_kind(acq) == "eye-level"
    check("C1980 a 40 deg pad: the request is camera-ready at eye level", ok,
          f"{(acq or {}).get('status')} {view_kind(acq)} {((acq or {}).get('refusal') or {}).get('code')}")
    p = fx.root / "receipts-c1980" / "points.json"
    doc = TCP.load(p) if p.is_file() else None
    rows = by_id(stations_of(doc))
    st = ((doc or {}).get("witness") or {}).get("station") or {}
    row = rows.get(st.get("id"))
    on = st.get("id") in PAD
    check("C1980 the witness stands on the pad", on, str(st.get("id")))
    if on:
        g = ground(40.0, PAD[st["id"]])
        check("C1980 the witness eye is its own ground + 120", abs(st["eye"][2] - (g + EYE_H)) <= 1.0,
              f"{st.get('eye')} vs ground {g:.1f}")
    check("C1980 the witness is labelled a terrain station with its slope and the measured limit",
          is_terrain(row) and "measured limit 43.7" in ((row or {}).get("standing") or {}).get("label", ""),
          str((row or {}).get("standing")))
    # the planner's own record (capture_acquire, fenced in this lane, does not yet copy it into the acquisition). Its
    # chosen camera may be a terrain candidate or one today's off-mesh allowance passes (a candidate within 160 u of the
    # pair's navmesh, over ground in the storey band); what must hold is that its terrain arm stood candidates at all
    so = fx.root / "receipts-c1980" / "shotplan-stdout.txt"
    import re
    m = re.search(r"outdoors: (\d+) of (\d+) stand on the terrain", so.read_text(encoding="utf-8") if so.is_file() else "")
    check("C1980 the planner stood candidates of its own on the pad's terrain", bool(m) and int(m[1]) > 0,
          m[0] if m else "no terrain line in the planner's output")


def c1981(fx):
    proc, acq, _ = TCP.acquire_with(fx, pad_scene(fx, 50.0), "c1981", "auto")
    check("C1981 a 50 deg pad: no eye-level view", view_kind(acq) != "eye-level", str(view_kind(acq)))
    proc, doc = TCP.pick(fx, pad_scene(fx, 50.0), "c1981p")
    rows = by_id(stations_of(doc))
    bad = [k for k in PAD if not (rows.get(k) and not rows[k].get("valid")
                                  and "43.7" in (rows[k].get("why") or "") and "50" in (rows[k].get("why") or ""))]
    check("C1981 every pad station is refused, naming its slope and the 43.7 deg limit", bool(rows) and not bad,
          f"{len(rows)} rows; not so: {bad}; s13 says {(rows.get('s13') or {}).get('why')!r:.160}")


def overhangs(deg):
    out = []
    for i, (k, (x, y)) in enumerate(sorted(PAD.items())):
        g = ground(deg, (x, y))
        out.append({"id": f"0009{i:02d}:Test.esp", "edid": "Overhang", "min": [x - 40, y - 40, g + 140],
                    "max": [x + 40, y + 40, g + 160]})
    return out


def c1982(fx):
    sc = pad_scene(fx, 40.0, *overhangs(40.0))
    proc, acq, _ = TCP.acquire_with(fx, sc, "c1982", "auto")
    check("C1982 the 40 deg pad under an overhang: no eye-level view", view_kind(acq) != "eye-level", str(view_kind(acq)))
    proc, doc = TCP.pick(fx, sc, "c1982p")
    rows = by_id(stations_of(doc))
    bad = [k for k in PAD if not (rows.get(k) and not rows[k].get("valid") and "Overhang" in (rows[k].get("why") or "")
                                  and "overhead" in (rows[k].get("why") or ""))]
    check("C1982 every pad station is refused, naming the slab overhead", bool(rows) and not bad,
          f"{len(rows)} rows; not so: {bad}; s13 says {(rows.get('s13') or {}).get('why')!r:.160}")


def c1983(fx):
    z13, z50 = round(ground(40.0, (0, 250))), round(ground(40.0, (0, 525)))
    nav = [{"min": [-60, 220], "max": [60, 280], "z": float(z13), "name": "Test.esm"},
           {"min": [-60, 490], "max": [60, 560], "z": float(z50), "name": "Test.esm"}]
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, nav=nav), "c1983")
    rows = stations_of(doc)
    at = {tuple((r.get("eye") or [None, None])[:2]): r for r in rows}
    a, b = at.get((0, 250)), rows and by_id(rows).get("s25")
    check(f"C1983 guard: the station at s13's spot stands on its navmesh (floor {z13}), not terrain",
          bool(a) and abs((a.get("floorZ") or 0) - z13) <= 0.5 and not is_terrain(a),
          str({k: (a or {}).get(k) for k in ("id", "eye", "floorZ", "standing")}))
    check(f"C1983 guard: s25 still moves along its bearing to the terrace 500 u out (floor {z50}), not terrain",
          bool(b) and bool(b.get("movedFrom")) and abs((b.get("floorZ") or 0) - z50) <= 0.5 and not is_terrain(b),
          str({k: (b or {}).get(k) for k in ("eye", "floorZ", "movedFrom", "standing")}))


def c1984(fx):
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, bumps={(0, 256): 48.0}), "c1984")
    rows = by_id(stations_of(doc))
    s13 = rows.get("s13") or {}
    check("C1984 a twisted quad under s13: s13 refused as not planar",
          bool(s13) and not s13.get("valid") and "planar" in (s13.get("why") or ""), repr(s13.get("why"))[:200])
    # the raised vertex (0,256) is a corner of a quad under s13, s14, s24 and s25; s26 and s36 touch none of them
    others = [k for k in ("s26", "s36") if is_terrain(rows.get(k))]
    check("C1984 the pad stations off the twist still stand on terrain (s26, s36)", len(others) == 2, str(others))


def c1985(fx):
    g = ground(40.0, PAD["s13"])
    stone = {"id": "000A01:Test.esp", "edid": "FlatStone", "min": [-20, 230, g - 10], "max": [20, 270, g + 5]}
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, stone), "c1985")
    s13 = by_id(stations_of(doc)).get("s13") or {}
    check("C1985 a stone under s13's centre: s13 refused, the floor ray meeting the stone, not the terrain",
          bool(s13) and not s13.get("valid") and "FlatStone" in (s13.get("why") or "")
          and "terrain" in (s13.get("why") or ""), repr(s13.get("why"))[:200])


def c1986(fx):
    post = {"id": "000A02:Test.esp", "edid": "Post", "min": [40, 240, -400], "max": [60, 260, 100]}
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, post), "c1986")
    s13 = by_id(stations_of(doc)).get("s13") or {}
    check("C1986 a post 40 u east of s13: s13 refused, naming the post within 64 u",
          bool(s13) and not s13.get("valid") and "Post" in (s13.get("why") or "") and "64" in (s13.get("why") or ""),
          repr(s13.get("why"))[:200])
    # a low rock 32 u beyond s13's downhill north-east corner, its top 69 u over that corner's ground: the corner's own
    # clearance point (its ground + 40) meets it; a point at the centre's ground + 84 would pass 66 u over its top
    cz = ground(40.0, (32, 282))
    rock = {"id": "000A03:Test.esp", "edid": "LowRock", "min": [64, 270, cz - 11], "max": [90, 300, cz + 69]}
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, rock), "c1986b")
    s13 = by_id(stations_of(doc)).get("s13") or {}
    check("C1986 a low rock beside s13's downhill corner: s13 refused at that corner's own ground + 40, naming the rock",
          bool(s13) and not s13.get("valid") and "LowRock" in (s13.get("why") or "")
          and f"32,282,{round(cz + 40)}" in (s13.get("why") or ""), repr(s13.get("why"))[:220])


def c1987(fx):
    import json
    import test_shotplan_plan as TSP
    plinth = {"id": "000B02:Test.esp", "edid": "Plinth", "min": [-40, -40, 0], "max": [40, 40, 30]}
    sc = TSP.room(plinth)
    del sc["floorZ"]
    sc["nav"] = []
    sc["exterior"] = True
    t = math.tan(math.radians(30.0))
    # a 30 deg plane through the plinth's foot, falling to the north: z = -y tan 30, x and y -1024 .. 1024
    sc["terrain"] = {"origin": [-1024, -1024], "step": 128,
                     "z": [[round(-(-1024 + j * 128) * t, 3) for j in range(17)] for i in range(17)]}
    jp = fx.root / "plan-c1987.json"
    code, out = TSP.run_cli(["plan", "TestCell", "--subject", "0,0,30=target", "--floor-z", "40", "--vp", TSP.FAKE,
                             "--quiet", "--json", str(jp), "--worklist", str(fx.root / "wl-c1987.json")], sc)
    doc = json.loads(jp.read_text(encoding="utf-8")) if jp.is_file() else None
    ok = bool(doc) and doc.get("status") == "verified"
    check("C1987 outdoors, no navmesh, a 30 deg LAND plane: a camera verifies", ok, (doc or {}).get("status") or out[-300:])
    if not ok:
        return
    eye = doc["chosen"]["eye"]
    g = -eye[1] * t
    check("C1987 the chosen eye is its own ground + 120", abs(eye[2] - (g + EYE_H)) <= 1.5, f"{eye} vs ground {g:.1f}")
    check("C1987 the plan says the camera stands on terrain",
          ((doc.get("chosen") or {}).get("standing") or {}).get("kind") == "terrain", str(doc.get("chosen", {}).get("standing")))


def c1988(fx):
    proc, doc = TCP.pick(fx, pad_scene(fx, 40.0, exterior=False), "c1988")
    rows = stations_of(doc)
    wl = fx.root / "work-c1988" / "witness-worklist.json"
    shots = TCP.load(wl)["cells"][0]["shots"] if wl.is_file() else []
    off = [s["label"] for s in shots if abs(s["eye"][2] - EYE_H) > 0.5]
    check("C1988 guard, interior: no terrain station, every eye at the one floor + 120",
          len(shots) == 36 and not off and not any(is_terrain(r) for r in rows), f"{len(shots)} shots, off {off[:4]}")


def c1989(fx):
    import shotplan as S
    fn = getattr(S, "terrain_slope", None)
    check("C1989 shotplan has the terrain slope rule", fn is not None)
    if fn is None:
        return

    def heights(deg, bump=0.0):
        t = math.tan(math.radians(deg))
        h = {(x, y): -y * t for x in range(-256, 257, 128) for y in range(-256, 257, 128)}
        h[(0, 0)] += bump
        return h
    for deg, want in ((43.71, True), (44.0, False)):
        got = fn(heights(deg), (64, 64))
        check(f"C1989 a planar {deg} deg footprint is {'accepted' if want else 'refused'}", got.get("ok") is want, str(got))
    # one vertex raised by 16 u on a 20 deg plane: a spread over 4 deg
    got = fn(heights(20.0, 16.0), (0, 0))
    check("C1989 a 20 deg footprint with a spread over 4 deg is refused as not planar",
          got.get("ok") is False and got.get("spread", 0) > 4.0, str(got))


CASES = [c1980, c1981, c1982, c1983, c1984, c1985, c1986, c1987, c1988, c1989]


def main() -> int:
    only = {a.upper() for a in sys.argv[1:] if not a.startswith("-")}
    with tempfile.TemporaryDirectory(prefix="terrain-stations-") as t:
        fx = TCP.Fixture(Path(t))
        for fn in CASES:
            if only and fn.__name__.upper() not in only:
                continue
            try:
                fn(fx)
            except Exception as exc:  # a crash is a crash, never a case's own red
                FAILS.append(fn.__name__)
                print(f"  CRASH {fn.__name__}: {type(exc).__name__}: {exc}")
    print(f"{len(FAILS)} failure(s): {FAILS}" if FAILS else "all terrain-station checks passed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
