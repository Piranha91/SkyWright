"""Offline known answers for exterior camera stations (C1700-C1705): outdoors a station stands on navmesh at its own level.

    python tests/test_exterior_stations.py

No game and no renderer: `fake_viewprobe.py` over toy box scenes, with the REAL picker, `capture_acquire.py` and
`shotplan.py plan`. The rule under test: in an exterior cell (the renderer's `cell X (exterior)` header, the CELL
record's flag), a station whose spot has no navmesh moves along its own bearing to the nearest spot that has some, and
its eye is that navmesh level + the eye height, with the first surface under the eye on that level. Interiors keep the
one floor for every station. The coordinates are made up; nothing here is evidence about any real cell.

    C1700  navmesh only on a terrace 500-650 u out at z 200: camera-ready, the witness eye at z 320 on the terrace
    C1701  the terrace at z 80 to the north and z -150 to the south: each station's eye is its own level + 120
    C1702  a slab over the east terrace (z 260-300): the east station is refused, naming the slab under its eye
    C1703  the C1700 scene as an interior: no station moves, every eye stays at the one floor + 120 (z 120)
    C1704  shotplan plan outdoors, floor z 200, navmesh at z 270: the chosen eye is at z 390, its feet at 270
    C1705  the C1704 scene as an interior: the eye stays at the floor + 120 (z 320)
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "viewprobe" / "tools"))
import test_capture_points as TCP  # noqa: E402

FAILS: list[str] = []
EYE_H = 120.0


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def terraces(levels: dict) -> tuple[list, list]:
    """Navmesh on a square ring 500-650 u out (Chebyshev), one level per side, with a 40 u ground slab under each."""
    rects = {"N": ([-650, 500], [650, 650]), "S": ([-650, -650], [650, -500]),
             "E": ([500, -500], [650, 500]), "W": ([-650, -500], [-500, 500])}
    nav, ground = [], []
    for i, (side, (lo, hi)) in enumerate(rects.items()):
        z = levels[side]
        nav.append({"min": lo, "max": hi, "z": z, "name": "Test.esm"})
        ground.append({"id": f"0005{i:02d}:Test.esp", "edid": f"Terrace{side}", "min": [lo[0], lo[1], z - 40],
                       "max": [hi[0], hi[1], z]})
    return nav, ground


def outdoors(fx, levels, *extra, exterior=True):
    nav, ground = terraces(levels)
    # a pair big enough to cover several pixels of the 48 px witness frames from the terrace, 500-900 u away
    sc = TCP.scene(TCP.box(fx.cy, [-120, -60, 0], [0, 60, 80]), TCP.box(fx.mg, [0, -60, 0], [120, 60, 80]),
                   *ground, *extra)
    # the pair's own pad: the request's floor (z 0); the ring's 150, 250 and 400 u stations stand off it
    sc["nav"] = [{"min": [-60, -60], "max": [60, 60], "z": 0.0, "name": "Test.esm"}] + nav
    if exterior:
        sc["exterior"] = True
        sc["clearTerrain"] = "terrain: ground at z 0.0"
    return sc


def stations(fx, tag):
    p = fx.root / f"receipts-{tag}" / "points.json"
    return TCP.load(p) if p.is_file() else None


def c1700(fx):
    sc = outdoors(fx, {"N": 200.0, "S": 200.0, "E": 200.0, "W": 200.0})
    proc, acq, _ = TCP.acquire_with(fx, sc, "c1700", "auto")
    ok = (proc.returncode == 0 and acq and acq.get("status") == "camera-ready"
          and (acq.get("camera") or {}).get("viewKind") == "eye-level")
    check("C1700 terrace at z 200: the request is camera-ready at eye level", bool(ok),
          f"{(acq or {}).get('status')} {(acq or {}).get('camera', {}).get('viewKind')} "
          f"{(acq or {}).get('refusal', {}).get('code')} {TCP.tail(proc)[-160:]}")
    doc = stations(fx, "c1700")
    if not (ok and doc):
        return
    st = doc["witness"]["station"]
    x, y, z = st["eye"]
    check("C1700 the witness stands on the terrace (500-650 u out)", 500 <= max(abs(x), abs(y)) <= 650, str(st["eye"]))
    check("C1700 the witness eye is the terrace level + 120 (z 320)", abs(z - 320) <= 0.5, str(st["eye"]))
    check("C1700 the points' floorZ is the witness's own level (200)", abs(doc["floorZ"] - 200) <= 0.5, str(doc["floorZ"]))
    check("C1700 the top-down inputs keep the request's floor (0)", abs(doc["topDown"]["floorZ"]) <= 0.5,
          str(doc["topDown"]["floorZ"]))
    cam = (acq.get("camera") or {})
    eye = cam.get("eye") or [0, 0, 0]
    check("C1700 the planned camera's eye is at z 320", abs(eye[2] - 320) <= 0.5, str(eye))


def c1701(fx):
    sc = outdoors(fx, {"N": 80.0, "S": -150.0, "E": 80.0, "W": -150.0})
    proc, doc = TCP.pick(fx, sc, "c1701")
    ok = proc.returncode == 0 and doc
    check("C1701 two terrace levels: the picker places a witness", bool(ok), TCP.tail(proc))
    if not ok:
        return
    rows = [s for s in doc["witness"]["stations"] if s.get("valid")]
    wrong = []
    for s in rows:
        x, y, z = s["eye"]
        level = 80.0 if (y >= 500 or x >= 500) else -150.0
        if abs(z - (level + EYE_H)) > 0.5:
            wrong.append((s["id"], s["eye"]))
    check("C1701 every valid station's eye is its own terrace level + 120", rows and not wrong,
          f"{len(rows)} valid; wrong {wrong[:4]}")
    zs = {round(s["eye"][2]) for s in rows}
    check("C1701 valid stations stand on both levels (eyes at z 200 and z -30)", {200, -30} <= zs, str(sorted(zs)))


def c1702(fx):
    slab = {"id": "000601:Test.esp", "edid": "Overhang", "min": [480, -120, 260], "max": [670, 120, 300]}
    sc = outdoors(fx, {"N": 200.0, "S": 200.0, "E": 200.0, "W": 200.0}, slab)
    proc, doc = TCP.pick(fx, sc, "c1702")
    rows = (doc or {}).get("witness", {}).get("stations", [])
    east = [s for s in rows if abs(s["bearing"] - 90.0) < 1e-6]
    under = [s for s in east if s.get("eye") and 480 <= s["eye"][0] <= 670 and abs(s["eye"][1]) <= 120]
    check("C1702 the east stations stand under the slab, on the terrace", bool(under),
          str([(s["id"], s.get("eye")) for s in east]))
    bad = [s["id"] for s in under if s.get("valid") or "Overhang" not in (s.get("why") or "")]
    check("C1702 each one is refused, naming the slab under its eye", under and not bad,
          str([(s["id"], (s.get("why") or "")[:120]) for s in under]))


def c1703(fx):
    sc = outdoors(fx, {"N": 200.0, "S": 200.0, "E": 200.0, "W": 200.0}, exterior=False)
    TCP.pick(fx, sc, "c1703")
    wl = fx.root / "work-c1703" / "witness-worklist.json"
    shots = TCP.load(wl)["cells"][0]["shots"] if wl.is_file() else []
    import capture_points as CP
    ring = {s["id"]: s["xy"] for s in CP.stations((0.0, 0.0, 40.0), 12, (150.0, 250.0, 400.0))}
    moved = [s["label"] for s in shots if tuple(s["eye"][:2]) != tuple(ring.get(s["label"], ()))]
    off = [s["label"] for s in shots if abs(s["eye"][2] - 120) > 0.5]
    check("C1703 interior: all 36 ring stations, none moved, every eye at z 120",
          len(shots) == 36 and not moved and not off, f"{len(shots)} shots, moved {moved[:4]}, eye off {off[:4]}")


def plan_scene(exterior):
    import test_shotplan_plan as TSP
    slab = {"id": "000701:Test.esp", "edid": "Ground", "min": [-790, -790, 230], "max": [790, 790, 270]}
    plinth = {"id": "000702:Test.esp", "edid": "Plinth", "min": [-40, -40, 270], "max": [40, 40, 300]}
    sc = TSP.room(slab, plinth)
    sc["floorZ"] = 270.0
    sc["nav"] = [{"min": [-790, -790], "max": [790, 790], "z": 270.0, "name": "Test.esm"}]
    if exterior:
        sc["exterior"] = True
        sc["clearTerrain"] = "terrain: ground at z 0.0"
    return TSP, sc


def plan(fx, exterior, tag):
    TSP, sc = plan_scene(exterior)
    jp = fx.root / f"plan-{tag}.json"
    code, out = TSP.run_cli(["plan", "TestCell", "--subject", "0,0,270=target", "--floor-z", "200", "--vp", TSP.FAKE,
                             "--quiet", "--json", str(jp), "--worklist", str(fx.root / f"wl-{tag}.json")], sc)
    return code, out, (json.loads(jp.read_text(encoding="utf-8")) if jp.is_file() else None)


def c1704(fx):
    code, out, doc = plan(fx, True, "c1704")
    ok = doc and doc.get("status") == "verified"
    check("C1704 outdoors, navmesh 70 u above the floor: a camera verifies", bool(ok),
          (doc or {}).get("status") or out[-300:])
    if not ok:
        return
    eye = doc["chosen"]["eye"]
    check("C1704 the chosen eye is its navmesh level + 120 (z 390)", abs(eye[2] - 390) <= 0.5, str(eye))
    check("C1704 the feet stand on that level (z 270)", abs(doc["feet"][2] - 270) <= 0.5, str(doc["feet"]))


def c1705(fx):
    code, out, doc = plan(fx, False, "c1705")
    eye = ((doc or {}).get("chosen") or {}).get("eye")
    check("C1705 the same scene indoors: the chosen eye stays at the floor + 120 (z 320)",
          bool(eye) and abs(eye[2] - 320) <= 0.5, str(eye or out[-300:]))


CASES = [c1700, c1701, c1702, c1703, c1704, c1705]


def main() -> int:
    only = {a.upper() for a in sys.argv[1:] if not a.startswith("-")}
    with tempfile.TemporaryDirectory(prefix="exterior-stations-") as t:
        fx = TCP.Fixture(Path(t))
        for fn in CASES:
            if only and fn.__name__.upper() not in only:
                continue
            try:
                fn(fx)
            except Exception as exc:  # a crash is a crash, never a case's own red
                FAILS.append(fn.__name__)
                print(f"  CRASH {fn.__name__}: {type(exc).__name__}: {exc}")
    print(f"{len(FAILS)} failure(s): {FAILS}" if FAILS else "all exterior-station checks passed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
