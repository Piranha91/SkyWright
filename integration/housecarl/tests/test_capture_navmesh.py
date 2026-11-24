"""Offline contract for a navmesh comparison's camera: `capture_acquire.py acquire` and `capture` on a
`navmesh-version-comparison` request, end to end.

    python _geometry-harness/tests/test_capture_navmesh.py

No game and no renderer. The request is the REAL `report_scenes.capture_requests` output over a synthetic navownprobe
emit (a CLOBBERED source navmesh corroborated by one EDGE-IDX link); the camera is planned by the REAL `shotplan.py plan
--top-down-only`; `fake_viewprobe.py` stands in for the binary: it prints `--nav-link` rows from the scene, paints each
drawn navmesh version as a rectangle on the floor plane and writes the `-navmeshes.json` receipt. Every decision under
test -- which crossing is believed, what is framed, what is drawn in which state, what refuses -- runs for real.

The geometry is SYNTHETIC and a reader can redo it: source S (000100:World.esm) fills cell C1 (y 0..4096); its winning
version (Later.esp) crosses into target T (000200:World.esm, cell C2, y 4096..8192, 10 triangles from Earlier.esp) at
two edges on y 4096: tri 7 at x 1000..1200 into T's triangle 14 (beyond 10: the corroborated mismatch) and tri 3 at x
3000..3100 into triangle 2 (fine). The earlier version (Earlier.esp) of S starts at y 500 instead of 0.

Each case names the wrong answer it rejects (N01-N15, C711', C712').
"""
from __future__ import annotations

import copy
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
TOOLS = SW.HARNESS / "viewprobe" / "tools"
sys.path.insert(0, str(HARNESS))
import report_scenes  # noqa: E402

os.environ["PYTHONUTF8"] = "1"
FAILS: list[str] = []
FAKE = sys.executable.replace("\\", "/") + " " + str(TOOLS / "fake_viewprobe.py").replace("\\", "/")
FP = "c" * 64
S, T, T2 = "000100:World.esm", "000200:World.esm", "000300:World.esm"
C1, C2, C3 = "000010:World.esm", "000020:World.esm", "000030:World.esm"
POLICY = SW.CHECKOUT / "external" / "houseCARL" / ".claude/skills/evaluation-reporting/references/scene-policy.json"


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def crossing(tri, k, a, b, beyond):
    return {"tri": tri, "edge": "12", "link": 0, "targetTri": k, "beyond": beyond, "a": a, "b": b}


def link_row(target=T, tcell=C2, grid=(0, 1), crossings=None, count=10, splugin="Later.esp", scell=C1, status="ok"):
    if status != "ok":
        return {"source": S, "target": target, "status": status, "reason": "the target navmesh has no winning version"}
    xs = crossings if crossings is not None else [crossing(7, 14, [1000, 4096, 0], [1200, 4096, 0], True),
                                                  crossing(3, 2, [3000, 4096, 0], [3100, 4096, 0], False)]
    return {"source": S, "target": target, "status": "ok",
            "sourceCell": {"formKey": scell, "editorId": None, "interior": False, "grid": [0, 0]},
            "sourcePlugin": splugin, "sourceTriangles": 30,
            "targetCell": {"formKey": tcell, "editorId": None, "interior": False, "grid": list(grid)},
            "targetPlugin": "Earlier.esp", "targetTriangles": count,
            "tableEntries": len(xs), "tableBeyond": sum(1 for x in xs if x["beyond"]), "crossings": xs}


def scene(**over):
    sc = {"floorZ": 0.0, "boxes": [], "configFingerprint": FP,
          "nav": [{"min": [0, 0], "max": [8192, 8192], "z": 0.0, "name": "World.esm"}],
          "navLinks": {f"{S}={T}": link_row()},
          "navmeshes": [
              {"formKey": S, "cell": C1, "winner": "Later.esp",
               "tris": {"7": [[1000, 3996], [1200, 4096]], "3": [[3000, 3996], [3100, 4096]]},
               "versions": {"Later.esp": {"rect": [[0, 0], [4096, 4096]], "triangles": 30},
                            "Earlier.esp": {"rect": [[0, 500], [4096, 4096]], "triangles": 40}}},
              {"formKey": T, "cell": C2, "winner": "Earlier.esp", "tris": {"14": [[1000, 4096], [1200, 4196]]},
               "versions": {"Earlier.esp": {"rect": [[0, 4096], [4096, 8192]], "triangles": 10}}},
              {"formKey": T2, "cell": C3, "winner": "Earlier.esp", "tris": {},
               "versions": {"Earlier.esp": {"rect": [[4096, 0], [8192, 4096]], "triangles": 12}}}]}
    sc.update(over)
    return sc


def build(root: Path, label=f"{C1} (ext)  40 tris -> 30", kind="EDGE-IDX"):
    root.mkdir(parents=True, exist_ok=True)
    emit_path = root / "navown-emit.json"
    emit = {"Probe": "navownprobe", "State": "baseline",
            "Configuration": {"Fingerprint": FP, "Plugins": [{"Name": "World.esm"}, {"Name": "Earlier.esp"}, {"Name": "Later.esp"}]},
            "Conflicts": [
                {"Kind": "CLOBBERED", "A": "Earlier.esp", "B": "Later.esp",
                 "Necessary": [{"FormKey": S, "Label": label, "Physical": "Holds",
                                "PhysicalDetail": "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u"}]},
                {"Kind": kind, "A": "Later.esp", "B": "Earlier.esp",
                 "Necessary": [{"FormKey": S, "Label": label, "Physical": "Holds",
                                "PhysicalDetail": "the edge link's triangle index still exceeds the target's winning count (10)"}],
                 "Alternative": [{"FormKey": T}]}]}
    emit_path.write_text(json.dumps(emit, indent=1), encoding="utf-8")
    pres = {"presentationId": "navmesh-camera-fixture", "chapters": [], "source": {"bundleDir": str(root)},
            "findings": [{"id": "F-nav", "kind": "finding", "checkId": "geometry.navmesh-ownership", "armId": "baseline",
                          "part": "arm", "counts": {"groupKind": kind, "A": "Later.esp", "B": "Earlier.esp"},
                          "allRows": [{"formKey": S}], "evidenceIds": ["E-nav"]}],
            "evidence": [{"id": "E-nav", "kind": "emit", "part": "arm", "armId": "baseline",
                          "path": emit_path.name, "sha256": sha(emit_path)}]}
    plan = report_scenes.capture_requests(pres, root, POLICY)
    assert len(plan["requests"]) == 1, plan
    packet = {"schemaVersion": 1, "presentationId": pres["presentationId"], "scenes": [], "capturePlan": plan}
    pp = root / "storyboard-packet.json"
    pp.write_text(json.dumps(packet, indent=1), encoding="utf-8")
    return pp, plan["requests"][0]


def variant(pp: Path, tag: str, edit) -> Path:
    pk = load(pp)
    edit(pk["capturePlan"]["requests"][0])
    out = pp.parent / f"packet-{tag}.json"
    out.write_text(json.dumps(pk), encoding="utf-8")
    return out


def run(root: Path, sc: dict, tag: str, argv: list[str]):
    scp = root / f"scene-{tag}.json"
    scp.write_text(json.dumps(sc), encoding="utf-8")
    trace = root / f"trace-{tag}.txt"
    trace.unlink(missing_ok=True)
    env = dict(os.environ, FAKE_VIEWPROBE_SCENE=str(scp), FAKE_VIEWPROBE_TRACE=str(trace), PYTHONUTF8="1",
               PYTHONIOENCODING="utf-8")
    proc = subprocess.run([sys.executable, str(HARNESS / "capture_acquire.py"), *argv], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env)
    calls = [ln for ln in trace.read_text(encoding="utf-8").splitlines() if ln.strip()] if trace.is_file() else []
    return proc, calls


def acquire(root, pp, rid, sc, tag, points="auto"):
    out = root / f"acq-{tag}.json"
    proc, calls = run(root, sc, tag, ["acquire", "--packet", str(pp), "--request", rid, "--points", points,
                                      "--out", str(out), "--receipts", str(root / f"rc-{tag}"), "--vp", FAKE])
    return proc, (load(out) if out.is_file() else {}), calls, out


def code(doc):
    return ((doc or {}).get("refusal") or {}).get("code")


def why(proc, doc):
    return f"exit {proc.returncode}, {doc.get('status')}, {code(doc)}: {((doc.get('refusal') or {}).get('detail') or '')[:160]}"


def subjects_of(doc):
    return sorted(tuple(round(v) for v in s["point"][:2]) for s in ((doc.get("navmesh") or {}).get("subjects") or []))


def main():
    with tempfile.TemporaryDirectory(prefix="capture-navmesh-") as tmp:
        root = Path(tmp)
        pp, req = build(root / "base")
        rid = req["id"]
        base = root / "base"

        pol = load(POLICY)
        nav = [r for r in pol["rules"] if r["requestType"] == "navmesh-version-comparison"][0]["camera"].get("support") or {}
        check("C711' the scene policy marks navmesh comparisons supported, with the command that acquires them",
              nav.get("supported") is True and "capture_acquire.py acquire" in str(nav.get("command")), json.dumps(nav))
        check("C711' the real selector copies the support mark onto the navmesh request",
              ((req.get("camera") or {}).get("support") or {}).get("supported") is True)

        proc, doc, calls, acq_path = acquire(base, pp, rid, scene(), "ready")
        check("N01/C712' a navmesh request reaches camera-ready as a labelled top-down at one pose",
              proc.returncode == 0 and doc.get("status") == "camera-ready" and doc.get("viewKind") == "top-down"
              and len({s_.get("poseId") for s_ in doc.get("states") or []}) == 1 and doc.get("poseId"), why(proc, doc))
        check("N01 both sequence states are carried, winning first",
              [s_.get("state") for s_ in doc.get("states") or []] == ["winning-linked-navmeshes", "historical-navmesh-context"],
              str([s_.get("state") for s_ in doc.get("states") or []]))
        check("N02 the camera's subjects are exactly the broken crossing's two endpoints", subjects_of(doc) == [(1000, 4096), (1200, 4096)],
              str(subjects_of(doc)))
        eye = (doc.get("camera") or {}).get("eye") or [0, 0, 0]
        check("N02 the eye stands over the broken crossing, not the cell centre", abs(eye[0] - 1100) < 50 and abs(eye[1] - 4096) < 100,
              str(eye))
        check("N04 the in-range crossing at x 3000 is not a subject",
              bool(subjects_of(doc)) and all(x < 2000 for x, _y in subjects_of(doc)), str(subjects_of(doc)))
        pix = {s_["state"]: s_.get("pixels") for s_ in (doc.get("navmesh") or {}).get("acceptance") or []}
        check("N01 each state's measured pixels are recorded (red, cyan, magenta)",
              all(isinstance(v, dict) and v.get("cyan", 0) >= 25 and v.get("magenta", 0) >= 25 for v in pix.values())
              and (pix.get("winning-linked-navmeshes") or {}).get("red", 0) >= 25 and len(pix) == 2, json.dumps(pix))

        # N08: capture renders both states at the acquired camera; a navmesh capture is handed its control (the setup
        # without the mod at this camera, clean), which capture requires (navmesh-in-reel Gate A, C1974)
        n08_ctl = base / "control-n08"
        n08_scene = scene(navLinks={f"{S}={T}": link_row(splugin="Earlier.esp", count=20,
                          crossings=[crossing(7, 14, [1000, 4096, 0], [1200, 4096, 0], False)])})
        n08_scene["navmeshes"][0]["winner"] = "Earlier.esp"
        n08_scene["navmeshes"][1]["versions"]["Earlier.esp"]["triangles"] = 20
        run(base, n08_scene, "control-n08", ["navmesh-control", "--acquisition", str(acq_path), "--packet", str(pp),
                                             "--request", rid, "--order", str(base / "without.json"),
                                             "--out", str(n08_ctl), "--vp", FAKE])
        cap = base / "cap"
        proc_c, calls_c = run(base, scene(), "capture", ["capture", "--acquisition", str(acq_path), "--packet", str(pp),
                                                         "--request", rid, "--with-order", str(base / "order.json"),
                                                         "--control", str(n08_ctl / "control-result.json"),
                                                         "--out", str(cap), "--size", "320", "--vp", FAKE])
        res = load(cap / "capture-result.json") if (cap / "capture-result.json").is_file() else {}
        rows = res.get("states") or []
        argvs = {r_.get("state"): " ".join(r_.get("argv") or []) for r_ in rows}
        check("N08 capture: both states captured at one reported pose",
              res.get("status") == "captured" and len(rows) == 2 and len({r_.get("reportedPose") for r_ in rows}) == 1,
              f"{res.get('status')}: {res.get('reason')}")
        w, h = argvs.get("winning-linked-navmeshes", ""), argvs.get("historical-navmesh-context", "")
        check("N08 only the historical state substitutes the earlier version; it draws no source red",
              f"--nav-version {S}=Earlier.esp" in h and "--nav-version" not in w and f"--red {S}:7" in w
              and f"--red {S}:" not in h and f"--red {T}:14" in h, f"W[{w[-160:]}] H[{h[-160:]}]")
        check("N08 both states draw the target cell in magenta and keep the cell outline (the linked border)",
              f"--nav-cell {C2}=magenta" in w and f"--nav-cell {C2}=magenta" in h and "--no-cell-outline" not in w + h)

        # N03: the configuration holds no broken crossing
        fine = scene(navLinks={f"{S}={T}": link_row(crossings=[crossing(7, 4, [1000, 4096, 0], [1200, 4096, 0], False)])})
        proc, doc, calls, _ = acquire(base, pp, rid, fine, "fine")
        check("N03 a link within range in this configuration is refused, no camera",
              proc.returncode == 2 and code(doc) == "navmesh-link-not-reproduced" and not any("--eye" in c for c in calls), why(proc, doc))

        # N13: the record links the target in its table, no edge reaches it
        unlocated = scene(navLinks={f"{S}={T}": {**link_row(crossings=[]), "tableEntries": 1, "tableBeyond": 1}})
        proc, doc, calls, _ = acquire(base, pp, rid, unlocated, "unlocated")
        check("N13 a broken table entry no triangle edge uses is refused as not located",
              proc.returncode == 2 and code(doc) == "navmesh-link-not-located", why(proc, doc))
        proc, doc, calls, _ = acquire(base, pp, rid, scene(navLinks={}), "noline")
        check("N13 no nav-link row for the pair is refused, not read as fine",
              proc.returncode == 2 and code(doc) == "navmesh-link-unmeasured", why(proc, doc))

        # N05: the receipt says the target triangle exists
        exists = scene()
        exists["navmeshes"][1]["versions"]["Earlier.esp"]["triangles"] = 20
        proc, doc, calls, _ = acquire(base, pp, rid, exists, "exists")
        check("N05 a drawn target triangle the link was said to miss is refused",
              proc.returncode == 2 and code(doc) == "navmesh-receipt-mismatch", why(proc, doc))

        # N06: the target layer is not in the frame
        absent = scene()
        absent["navmeshes"][1]["versions"]["Earlier.esp"]["rect"] = [[0, 9000], [10, 9010]]
        proc, doc, calls, _ = acquire(base, pp, rid, absent, "absent")
        check("N06 a frame without the target navmesh is refused, naming magenta",
              proc.returncode == 2 and code(doc) == "navmesh-layer-unseen" and "magenta" in str((doc.get("refusal") or {}).get("detail")),
              why(proc, doc))

        # N07: the historical frame is the winner again
        proc, doc, calls, _ = acquire(base, pp, rid, scene(ignoreNavVersion=True), "n07")
        check("N07 a historical frame whose receipt shows the winner again is refused",
              proc.returncode == 2 and code(doc) == "navmesh-receipt-mismatch" and "Earlier.esp" in str((doc.get("refusal") or {}).get("detail")),
              why(proc, doc))
        p7 = variant(pp, "n07b", lambda r: r["views"][0].update(navPlugin="Missing.esp"))
        proc, doc, calls, _ = acquire(base, p7, rid, scene(), "n07b")
        check("N07 a historical version the setup does not have is refused",
              proc.returncode == 2 and code(doc) == "navmesh-version-absent", why(proc, doc))

        # N09: the shapes this path does not support
        ps = variant(pp, "same", lambda r: None)
        proc, doc, calls, _ = acquire(base, ps, rid, scene(navLinks={f"{S}={T}": link_row(tcell=C1)}), "same")
        check("N09 a target in the source's own cell is refused", proc.returncode == 2 and code(doc) == "navmesh-link-same-cell", why(proc, doc))
        pi = variant(pp, "int", lambda r: r.update(location="SomeInterior (int)  4 tris -> 5"))
        proc, doc, calls, _ = acquire(base, pi, rid, scene(), "int")
        check("N09 an interior location is refused before any render",
              proc.returncode == 2 and code(doc) == "navmesh-interior-unsupported" and not calls, why(proc, doc))
        pd = variant(pp, "door", lambda r: [c.update(kind="DOOR-TRI") for c in r["corroboration"]])
        proc, doc, calls, _ = acquire(base, pd, rid, scene(), "door")
        check("N09 a door corroboration is refused before any render",
              proc.returncode == 2 and code(doc) == "navmesh-corroboration-unsupported" and not calls, why(proc, doc))

        def add_t2(r):
            r["recordKeys"].append(T2)
            r["corroboration"].append({**r["corroboration"][0], "otherRecordKeys": [T2]})
        p2 = variant(pp, "two", add_t2)
        two = scene()
        two["navLinks"][f"{S}={T2}"] = link_row(target=T2, tcell=C3, grid=(1, 0),
                                               crossings=[crossing(9, 20, [4096, 2000, 0], [4096, 2100, 0], True)], count=12)
        proc, doc, calls, _ = acquire(base, p2, rid, two, "two")
        check("N02 two targets: every broken crossing of both is a subject (not only the first target's)",
              proc.returncode == 0 and subjects_of(doc) == [(1000, 4096), (1200, 4096), (4096, 2000), (4096, 2100)],
              f"{why(proc, doc)} {subjects_of(doc)}")
        far = copy.deepcopy(two)
        far["navLinks"][f"{S}={T2}"]["crossings"][0].update(a=[9000, 9000, 0], b=[9100, 9000, 0])
        proc, doc, calls, _ = acquire(base, p2, rid, far, "far")
        check("N09 crossings too far apart for one top-down are refused",
              proc.returncode == 2 and code(doc) == "navmesh-links-too-far-apart", why(proc, doc))
        fine2 = copy.deepcopy(two)
        fine2["navLinks"][f"{S}={T2}"]["crossings"][0].update(beyond=False, targetTri=3)
        fine2["navLinks"][f"{S}={T2}"]["tableBeyond"] = 0
        proc, doc, calls, _ = acquire(base, p2, rid, fine2, "fine2")
        check("N03 one of two corroborated pairs not reproduced refuses the request, naming it",
              proc.returncode == 2 and code(doc) == "navmesh-link-not-reproduced" and T2 in str((doc.get("refusal") or {}).get("detail")),
              why(proc, doc))

        # N10, N11, N12
        proc, doc, calls, _ = acquire(base, pp, rid, scene(configFingerprint="d" * 64), "mismatch")
        check("N10 a renderer in another configuration is refused", proc.returncode == 2 and code(doc) == "configuration-mismatch", why(proc, doc))
        proc, doc, calls, _ = acquire(base, pp, rid, scene(navLinks={f"{S}={T}": link_row(splugin="Third.esp")}), "third")
        check("N11 a source won by a third plugin is refused", proc.returncode == 2 and code(doc) == "navmesh-overwrite-not-winning",
              why(proc, doc))
        pts = base / "points.json"
        pts.write_text("{}", encoding="utf-8")
        proc, doc, calls, _ = acquire(base, pp, rid, scene(), "points", points=str(pts))
        check("N12 supplied points are refused for a navmesh request", proc.returncode == 2 and code(doc) == "navmesh-points-supplied"
              and not calls, why(proc, doc))

        # N14/N15: the setup without the mod, at the same camera
        ctl = base / "control"
        control_scene = scene(navLinks={f"{S}={T}": link_row(splugin="Earlier.esp", count=20,
                              crossings=[crossing(7, 14, [1000, 4096, 0], [1200, 4096, 0], False)])})
        control_scene["navmeshes"][0]["winner"] = "Earlier.esp"
        control_scene["navmeshes"][1]["versions"]["Earlier.esp"]["triangles"] = 20
        proc, _ = run(base, control_scene, "control", ["navmesh-control", "--acquisition", str(acq_path), "--packet", str(pp),
                                                      "--request", rid, "--order", str(base / "without.json"),
                                                      "--out", str(ctl), "--vp", FAKE])
        cr = load(ctl / "control-result.json") if (ctl / "control-result.json").is_file() else {}
        check("N14 the setup without the mod, at the same pose, re-measures its links and draws no red crossing",
              proc.returncode == 0 and cr.get("status") == "no-broken-crossing" and (cr.get("pixels") or {}).get("red", -1) == 0
              and cr.get("poseId") == load(acq_path).get("poseId"), f"exit {proc.returncode} {cr.get('status')} {cr.get('pixels')} {cr.get('reason')}")
        ctl2 = base / "control2"
        proc, _ = run(base, scene(), "control2", ["navmesh-control", "--acquisition", str(acq_path), "--packet", str(pp),
                                                 "--request", rid, "--order", str(base / "without.json"),
                                                 "--out", str(ctl2), "--vp", FAKE])
        cr2 = load(ctl2 / "control-result.json") if (ctl2 / "control-result.json").is_file() else {}
        check("N15 a control that still has the broken crossing says so and shows it red (attribution contradicted)",
              cr2.get("status") == "broken-crossing-present" and (cr2.get("pixels") or {}).get("red", 0) >= 25,
              f"{cr2.get('status')} {cr2.get('pixels')}")

    print()
    if FAILS:
        print(f"{len(FAILS)} FAILED:")
        for f in FAILS:
            print("  - " + f)
        return 1
    print("all navmesh camera checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
