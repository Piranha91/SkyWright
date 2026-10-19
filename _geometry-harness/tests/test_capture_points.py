"""Offline known answers for `capture_points.py`: a measured point on a face an eye-level camera can see (C700-C716).

    python tests/test_capture_points.py              # the cases
    python tests/test_capture_points.py --sabotage   # C716: the owner test removed; the cases must go red on their own

No game and no renderer. `fake_viewprobe.py` stands in for viewprobe over toy box scenes: it answers the placement line,
the navmesh / clear / inside / refs queries, the depth picks, and paints each `--ref-surface` participant's pixels in the
renderer's measured report-mode colours. The REAL picker, the REAL `capture_acquire.py` and the REAL `shotplan.py plan`
run over it. Each scene is a shape a real request was refused on (a face the partner covers, a top face flush beside a
tall neighbour, a floor on an arch far above the ground, objects in water, one object inside the other, an alpha
participant), and each verdict is arithmetic a reader can redo. The coordinates are made up; nothing here is evidence
about any real cell.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
TOOLS = HARNESS / "viewprobe" / "tools"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
import test_capture_acquire as TCA  # noqa: E402  (the real request selector's synthetic bundle)

os.environ["PYTHONUTF8"] = "1"
FAKE = TCA.FAKE
FAILS: list[str] = []
SIZE, GRID = ["--size", "48"], ["--grid", "4"]
PICKER = HARNESS / "capture_points.py"


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


# ---------------------------------------------------------------- scenes --

SHELL = [
    {"id": "000201:Test.esp", "edid": "ShellN", "min": [-860, 800, 0], "max": [860, 860, 400]},
    {"id": "000202:Test.esp", "edid": "ShellS", "min": [-860, -860, 0], "max": [860, -800, 400]},
    {"id": "000203:Test.esp", "edid": "ShellE", "min": [800, -860, 0], "max": [860, 860, 400]},
    {"id": "000204:Test.esp", "edid": "ShellW", "min": [-860, -860, 0], "max": [-800, 860, 400]},
]


def scene(cy, mg, *extra, floor=0.0, nav_z=0.0, shell=True, **kw):
    sc = {"floorZ": floor, "boxes": (list(SHELL) if shell else []) + [cy, mg] + list(extra),
          "nav": [{"min": [-790, -790], "max": [790, 790], "z": nav_z, "name": "Test.esm"}],
          "configFingerprint": TCA.FINGERPRINT}
    sc.update(kw)
    return sc


def box(key, lo, hi, **kw):
    return {"id": key, "edid": key.split(":")[0], "min": list(lo), "max": list(hi),
            "pos": [(lo[0] + hi[0]) / 2.0, (lo[1] + hi[1]) / 2.0, lo[2]], **kw}


def cross(cy, mg):
    """C700. A long block crossed through its middle by a wider slab: each object's faces nearest its origin lie inside
    the other, which is what a ray from the origin finds; the outer ends of each stay in the open."""
    return scene(box(cy, [-30, -100, 0], [30, 100, 80]), box(mg, [-60, -40, 0], [60, 40, 100]))


def flush_top(cy, mg):
    """C701. A trapdoor whose only exposed face is its top, flush with the floor, beside a tall shrine to its north."""
    return scene(box(cy, [-60, -60, -5], [60, 60, 0]), box(mg, [-100, 60, 0], [100, 220, 150]))


def arch(cy, mg):
    """C702. The pair stands on a slab 4,000 u above the ground plane (a courtyard on a rock arch)."""
    slab = {"id": "000301:Test.esp", "edid": "Arch", "min": [-900, -900, 3900], "max": [900, 900, 4000]}
    return scene(box(cy, [-150, -50, 4000], [-50, 50, 4080]), box(mg, [50, -50, 4000], [150, 50, 4100]), slab,
                 shell=False, nav_z=4000.0)


def enclosed(cy, mg):
    """C703. One participant entirely inside the other."""
    return scene(box(cy, [-20, -20, 0], [20, 20, 40]), box(mg, [-60, -60, 0], [60, 60, 100]))


def side_by_side(cy, mg, **kw):
    return scene(box(cy, [-80, -40, 0], [-10, 40, 60]), box(mg, [10, -40, 0], [80, 40, 60]), **kw)


def alcove(cy, mg):
    """C707. The pair in an alcove open only to the east: only east stations see either."""
    walls = [{"id": "000311:Test.esp", "edid": "AlcN", "min": [-200, 100, 0], "max": [200, 160, 300]},
             {"id": "000312:Test.esp", "edid": "AlcS", "min": [-200, -160, 0], "max": [200, -100, 300]},
             {"id": "000313:Test.esp", "edid": "AlcW", "min": [-260, -160, 0], "max": [-200, 160, 300]}]
    return scene(box(cy, [-40, -90, 0], [40, -10, 60]), box(mg, [-40, 10, 0], [40, 90, 60]), *walls)


def on_box_surface(pt, b, tol=1.5):
    lo, hi = b["min"], b["max"]
    inside = all(lo[k] - tol <= pt[k] <= hi[k] + tol for k in range(3))
    on_face = any(abs(pt[k] - lo[k]) <= tol or abs(pt[k] - hi[k]) <= tol for k in range(3))
    return inside and on_face


def strictly_inside(pt, b, tol=1.5):
    return all(b["min"][k] + tol < pt[k] < b["max"][k] - tol for k in range(3))


# ---------------------------------------------------------------- drivers --

class Fixture:
    def __init__(self, root: Path):
        self.root = root
        self.packet, self.rid, self.request = TCA.build_bundle(root / "bundle")
        self.cy = [h["formKey"] for h in self.request["highlights"] if h["color"] == "cyan"][0]
        self.mg = [h["formKey"] for h in self.request["highlights"] if h["color"] == "magenta"][0]


def env_for(root: Path, sc: dict, tag: str, trace=False):
    sp = root / f"scene-{tag}.json"
    sp.write_text(json.dumps(sc), encoding="utf-8")
    env = dict(os.environ, FAKE_VIEWPROBE_SCENE=str(sp), PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
    if trace:
        tp = root / f"trace-{tag}.txt"
        tp.write_text("", encoding="utf-8")
        env["FAKE_VIEWPROBE_TRACE"] = str(tp)
    return env


def pick(fx: Fixture, sc: dict, tag: str, picker=PICKER, packet=None):
    out = fx.root / f"points-{tag}.json"
    env = env_for(fx.root, sc, tag)
    argv = [sys.executable, str(picker), "pick", "--packet", str(packet or fx.packet), "--request", fx.rid,
            "--out", str(out), "--work", str(fx.root / f"work-{tag}"), "--vp", FAKE, *SIZE, *GRID, "--synthetic"]
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    return proc, (load(out) if out.is_file() else None)


def acquire_with(fx: Fixture, sc: dict, tag: str, points, bind=None, trace=False, packet=None):
    env = env_for(fx.root, sc, tag, trace)
    out = fx.root / f"acq-{tag}.json"
    argv = [sys.executable, str(HARNESS / "capture_acquire.py"), "acquire", "--packet", str(packet or fx.packet),
            "--request", fx.rid, "--points", str(points), "--out", str(out),
            "--receipts", str(fx.root / f"receipts-{tag}"), "--vp", FAKE,
            "--synthetic-points", "--points-size", "48", "--points-grid", "4"]
    if bind:
        argv += ["--bind-packet", str(bind)]
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    return proc, (load(out) if out.is_file() else None), env


def point_of(doc, key):
    return [r["point"] for r in doc["points"] if r["formKey"] == key][0]


def tail(proc):
    return " ".join((proc.stderr or proc.stdout or "").split())[-240:]


# ---------------------------------------------------------------- cases --

def c700(fx, picker=PICKER):
    sc = cross(fx.cy, fx.mg)
    proc, doc = pick(fx, sc, "c700", picker)
    ok = proc.returncode == 0 and doc and doc.get("points")
    check("C700 covered faces: the picker returns a point for each participant", bool(ok), tail(proc))
    if not ok:
        return
    a, b = sc["boxes"][-2], sc["boxes"][-1]
    pa, pb = point_of(doc, fx.cy), point_of(doc, fx.mg)
    check("C700 the cyan point lies on the cyan block's own surface, outside the slab",
          on_box_surface(pa, a) and not strictly_inside(pa, b), str(pa))
    check("C700 the magenta point lies on the slab's own surface, outside the block",
          on_box_surface(pb, b) and not strictly_inside(pb, a), str(pb))
    pts = fx.root / "points-c700.json"
    proc, acq, _ = acquire_with(fx, sc, "c700-acq", pts)
    check("C700 the planner verifies a camera on the picked points (camera-ready)",
          proc.returncode == 0 and acq and acq.get("status") == "camera-ready", tail(proc))
    # The old method on the same scene: the shortest clear-at ray owned by each participant, from probes at its origin.
    old = dict(doc, points=[dict(r) for r in doc["points"]])
    old["points"][0]["point"] = [30.0, 0.0, 8.0]    # the block's +x face where the slab crosses it
    old["points"][1]["point"] = [0.0, 40.0, 8.0]    # the slab's +y face where the block crosses it
    old.pop("camera", None)
    op = fx.root / "points-c700-old.json"
    op.write_text(json.dumps(old), encoding="utf-8")
    proc, acq, _ = acquire_with(fx, sc, "c700-old", op)
    check("C700 control: the origin-ray points of the old method are refused by the same planner",
          proc.returncode != 0 and acq and acq.get("status") == "refused",
          (acq or {}).get("refusal", {}).get("code", tail(proc)))


def c701(fx):
    sc = flush_top(fx.cy, fx.mg)
    proc, acq, _ = acquire_with(fx, sc, "c701", "auto")
    ok = proc.returncode == 0 and acq and acq.get("status") == "camera-ready"
    check("C701 flush top face: auto points give a verified camera", bool(ok), tail(proc))
    if acq and acq.get("participants"):
        pt = [p["point"] for p in acq["participants"] if p["formKey"] == fx.cy][0]
        check("C701 the trapdoor's point is on its top face (z 0)", abs(pt[2] - 0.0) <= 1.5, str(pt))
        doc = load(fx.root / "receipts-c701" / "points.json")
        st = doc["witness"]["station"]
        check("C701 the witness stands on the open side (south of the shrine)", st["eye"][1] < 60, str(st["eye"]))


def c702(fx):
    sc = arch(fx.cy, fx.mg)
    proc, acq, _ = acquire_with(fx, sc, "c702", "auto")
    doc = load(fx.root / "receipts-c702" / "points.json") if (fx.root / "receipts-c702" / "points.json").is_file() else {}
    check("C702 arch: the floor is the slab top the pair stands on (4000), not the ground plane (0)",
          doc.get("floorZ") == 4000.0, str(doc.get("floorZ")))
    check("C702 arch: verified camera", proc.returncode == 0 and acq and acq.get("status") == "camera-ready", tail(proc))


def c703(fx, picker=PICKER):
    proc, doc = pick(fx, enclosed(fx.cy, fx.mg), "c703", picker)
    ref = (doc or {}).get("refusal") or {}
    check("C703 enclosed: refused, no visible face, the hidden one seen from 0 stations",
          proc.returncode == 2 and ref.get("code") == "points-no-visible-face"
          and (ref.get("visibleFrom") or {}).get(fx.cy) == 0, ref.get("detail", tail(proc)))


def c704(fx):
    sc = side_by_side(fx.cy, fx.mg)
    sc["boxes"][-1]["alpha"] = True
    proc, doc = pick(fx, sc, "c704")
    ref = (doc or {}).get("refusal") or {}
    check("C704 alpha participant: refused, no opaque face, naming it",
          proc.returncode == 2 and ref.get("code") == "points-no-opaque-face" and fx.mg in ref.get("detail", ""),
          ref.get("detail", tail(proc)))


def c705(fx):
    proc, doc = pick(fx, side_by_side(fx.cy, fx.mg, waterZ=50.0), "c705")
    ref = (doc or {}).get("refusal") or {}
    check("C705 water: refused, the floor lies under the water surface",
          proc.returncode == 2 and ref.get("code") == "points-under-water" and "z 50" in ref.get("detail", ""),
          ref.get("detail", tail(proc)))


def c706(fx):
    sc = side_by_side(fx.cy, fx.mg)
    sc["boxes"] = sc["boxes"][:-1]
    proc, doc = pick(fx, sc, "c706")
    ref = (doc or {}).get("refusal") or {}
    check("C706 not placed: refused, naming the missing reference",
          proc.returncode == 2 and ref.get("code") == "points-not-placed" and fx.mg in ref.get("detail", ""),
          ref.get("detail", tail(proc)))


def c707(fx):
    sc = alcove(fx.cy, fx.mg)
    proc, acq, _ = acquire_with(fx, sc, "c707", "auto")
    check("C707 alcove open to the east: verified camera", proc.returncode == 0 and acq
          and acq.get("status") == "camera-ready", tail(proc))
    pp = fx.root / "receipts-c707" / "points.json"
    if not pp.is_file():
        return
    doc = load(pp)
    c, h = doc["camera"]["bearingWindow"]
    check("C707 the window centres on the open (east) side", abs(((c - 90) + 180) % 360 - 180) <= 15, f"{c} +- {h}")
    plan = load(fx.root / "receipts-c707" / "shotplan-plan.json")
    chosen = plan.get("chosen") or {}
    b = chosen.get("_bearing")
    check("C707 the planner's chosen bearing lies inside the window and the plan records it",
          b is not None and abs(((b - c) + 180) % 360 - 180) <= h + 1e-6 and plan.get("bearingWindow"),
          f"chosen {b}, window {plan.get('bearingWindow')}")


def c708(fx):
    sc = alcove(fx.cy, fx.mg)
    env = env_for(fx.root, sc, "c708")
    res = {}
    for tag, win in (("east", "90,10"), ("west", "270,10")):
        js = fx.root / f"plan-c708-{tag}.json"
        argv = [sys.executable, str(TOOLS / "shotplan.py"), "plan", "SyntheticAlcove", "--floor-z", "0",
                "--surface=40,-50,30=e1", "--surface=40,50,30=e2", "--json", str(js), "--vp", FAKE, "--quiet",
                "--no-fallback", f"--bearing-window={win}"]
        p = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
        res[tag] = (p, load(js) if js.is_file() else {})
    p, d = res["east"]
    check("C708 --bearing-window: bearings outside are not considered, and it says how many",
          "67 bearing(s) outside it were not considered" in (p.stdout or ""), tail(p))
    b = (d.get("chosen") or {}).get("_bearing")
    check("C708 an east window verifies a camera inside it", d.get("status") == "verified" and b is not None
          and 80 <= b <= 100, f"{d.get('status')} {b}")
    p, d = res["west"]
    check("C708 a window over the closed side gives no verified camera", d.get("status") != "verified",
          str(d.get("status")))


def c709_c710(fx):
    bind = fx.root / "bound-c709.json"
    proc, acq, _ = acquire_with(fx, cross(fx.cy, fx.mg), "c709", "auto", bind=bind)
    check("C709 --points auto end to end: camera-ready and bound into the packet",
          proc.returncode == 0 and acq and acq.get("status") == "camera-ready" and bind.is_file()
          and [r for r in load(bind)["capturePlan"]["requests"] if r["id"] == fx.rid][0]["camera"]["pose"],
          tail(proc))
    pp = fx.root / "receipts-c709" / "points.json"
    doc = load(pp) if pp.is_file() else {}
    check("C709 the measured points are kept with their provenance, witness and synthetic label",
          doc.get("synthetic") is True and "SYNTHETIC" in doc.get("provenance", "")
          and doc.get("witness", {}).get("station") and len(doc.get("points", [])) == 2)
    if doc.get("points"):
        import math
        cx = sum(r["point"][0] for r in doc["points"]) / 2
        cy = sum(r["point"][1] for r in doc["points"]) / 2
        eye = doc["witness"]["station"]["eye"]
        ws = math.hypot(eye[0] - cx, eye[1] - cy)
        check("C709 the planner's standoff range includes the witness's own distance from the points",
              doc["camera"]["standoffMin"] <= ws <= doc["camera"]["standoffMax"],
              f"{ws:.0f} in {doc['camera']['standoffMin']}-{doc['camera']['standoffMax']}")
    if acq and acq.get("suppliedPoints"):
        check("C710 the result binds the points file it measured (path and hash)",
              Path(acq["suppliedPoints"]["path"]) == pp.resolve() and acq["suppliedPoints"]["sha256"] == TCA.sha(pp))


def c711(fx):
    pol = load(HARNESS.parent / ".claude/skills/evaluation-reporting/references/scene-policy.json")
    by = {r["requestType"]: (r.get("camera") or {}).get("support") or {} for r in pol["rules"]}
    nav = by.get("navmesh-version-comparison", {})
    check("C711 the scene policy marks navmesh comparisons as having no camera path, with a plain reason",
          nav.get("supported") is False and str(nav.get("reason", "")).endswith(".") and "navmesh" in nav.get("reason", ""))
    check("C711 overlap pairs carry the supported path, and the real selector copies it onto the request",
          by.get("overlap-pair", {}).get("supported") is True
          and (fx.request.get("camera") or {}).get("support", {}).get("supported") is True)


def c712(fx):
    pk = load(fx.packet)
    row = pk["capturePlan"]["requests"][0]
    pol = load(HARNESS.parent / ".claude/skills/evaluation-reporting/references/scene-policy.json")
    nav = ([r for r in pol["rules"] if r["requestType"] == "navmesh-version-comparison"][0]["camera"].get("support")
           or {"supported": False, "reason": "(the scene policy carries no support mark)"})
    row["requestType"] = "navmesh-version-comparison"
    row["camera"]["support"] = nav
    pp = fx.root / "packet-c712.json"
    pp.write_text(json.dumps(pk), encoding="utf-8")
    proc, acq, env = acquire_with(fx, cross(fx.cy, fx.mg), "c712", "auto", trace=True, packet=pp)
    calls = [ln for ln in Path(env["FAKE_VIEWPROBE_TRACE"]).read_text(encoding="utf-8").splitlines() if ln.strip()]
    ref = (acq or {}).get("refusal") or {}
    check("C712 a navmesh request is refused with the storyboard's sentence before any render",
          proc.returncode == 2 and ref.get("code") == "request-type-unsupported" and ref.get("detail") == nav["reason"]
          and len(calls) == 0, f"{ref.get('detail')!r}; {len(calls)} render(s)")


def c713(fx):
    kiosk = {"id": "000321:Test.esp", "edid": "Kiosk", "min": [-50, 200, 0], "max": [50, 300, 300]}
    sc = side_by_side(fx.cy, fx.mg)
    sc["boxes"].append(kiosk)
    proc, doc = pick(fx, sc, "c713")
    sts = [s for s in ((doc or {}).get("witness") or {}).get("stations", [])
           if s["bearing"] == 0 and s["standoff"] == 250]
    s01 = sts[0] if sts else {}
    check("C713 a station inside a box is excluded by the planner's inside verdict",
          proc.returncode == 0 and s01.get("valid") is False and "inside" in (s01.get("why") or "")
          and doc["witness"]["station"]["id"] != s01.get("id"), str(s01.get("why"))[:160])


def c714(fx):
    sc = cross(fx.cy, fx.mg)
    _p1, d1 = pick(fx, sc, "c714a")
    _p2, d2 = pick(fx, sc, "c714b")
    check("C714 same scene, same request: the same points document", d1 is not None and d1 == d2)


def c715(fx):
    proc, acq, _ = acquire_with(fx, enclosed(fx.cy, fx.mg), "c715", "auto")
    ref = (acq or {}).get("refusal") or {}
    check("C715 a picker refusal reaches the acquisition as one sentence, exit 2",
          proc.returncode == 2 and acq and acq.get("status") == "refused" and ref.get("code") == "points-unpickable"
          and ref.get("pickerCode") == "points-no-visible-face" and "\n" not in ref.get("detail", "x\n")
          and ref.get("detail", "").endswith("view."), ref.get("detail", tail(proc)))


def run_cases(root: Path, picker=PICKER, only=None):
    fx = Fixture(root)
    if only == "sabotage":
        c700(fx, picker)
        c703(fx, picker)
        return
    for fn in (c700, c701, c702, c703, c704, c705, c706, c707, c708, c709_c710, c711, c712, c713, c714, c715):
        fn(fx)


# ---------------------------------------------------------------- C716 sabotage --

SABOTAGE_FROM = 'mask = colour_mask(img, p["color"])'
SABOTAGE_TO = 'mask = np.ones(img.shape[:2], dtype=bool)   # SABOTAGE: any hit counts as the participant'


def sabotage(root: Path) -> int:
    src = PICKER.read_text(encoding="utf-8")
    n = src.count(SABOTAGE_FROM)
    print(f"C716 sabotage: the owner test occurs {n} time(s) in capture_points.py (must be 1)")
    if n != 1:
        print("C716 FAIL: the sabotage target is not unique; the arm proves nothing")
        return 1
    sab = root / "sab" / "capture_points.py"
    sab.parent.mkdir(parents=True)
    sab.write_text(src.replace(SABOTAGE_FROM, SABOTAGE_TO), encoding="utf-8")
    (sab.parent / "viewprobe").mkdir()
    shutil.copytree(TOOLS, sab.parent / "viewprobe" / "tools")
    run_cases(root / "cases", picker=sab, only="sabotage")
    owner = {"C700 the cyan point lies on the cyan block's own surface, outside the slab",
             "C700 the magenta point lies on the slab's own surface, outside the block"}
    hidden = "C703 enclosed: refused, no visible face, the hidden one seen from 0 stations"
    red = set(FAILS)
    ok = bool(owner & red) and hidden in red and "C700 covered faces: the picker returns a point for each participant" not in red
    print(f"C716 sabotage: red on its own assertions: {sorted(red)}")
    print("C716 sabotage arm " + ("RED as required (an owner check and the hidden-face refusal)" if ok else
                                  "NOT red where required: the cases do not detect a picker that ignores ownership"))
    return 0 if ok else 1


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="capture-points-") as t:
        root = Path(t)
        if "--sabotage" in sys.argv:
            return sabotage(root)
        run_cases(root)
    print(f"{len(FAILS)} failure(s)" if FAILS else "all capture-point checks passed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
