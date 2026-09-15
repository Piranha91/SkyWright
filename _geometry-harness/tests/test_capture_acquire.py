"""Offline contract for `capture_acquire.py`: the request-to-camera path, end to end.

    python tests/test_capture_acquire.py            # run the cases
    python tests/test_capture_acquire.py --emit DIR # write the replayable demonstration bundle

No game and no renderer. The request is produced by the REAL
`report_scenes.capture_requests` out of a synthetic presentation and emit; the camera is planned
by the REAL `shotplan.py plan` in its own subprocess, with `fake_viewprobe.py` standing in for
the binary and answering from a declared toy scene. What is replaced is the rasteriser. Every
decision under test -- which answer is believed, which reaches the published result, what the
exit code says -- runs for real.

The scene and the feature points are SYNTHETIC and the coordinates are made up. Nothing here is
evidence about any real cell; each case is a known answer whose verdict is arithmetic a reader
can redo:

    two crates in a three-walled alcove open to -Y. The declared point for each is mid-height on
    its own -Y face, so only a camera in the mouth of the alcove can stop a ray on both. The
    alcove walls stand 300 u tall -- ABOVE the probe eye -- so they bound the clearance map at
    110-290 u on the three closed sides, under the 300 u standoff floor, and the surviving
    bearings are the open ones.
"""
from __future__ import annotations

import copy
import hashlib
import io
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
import report_scenes  # noqa: E402

os.environ["PYTHONUTF8"] = "1"
FAILS: list[str] = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def load(p: Path):
    return json.loads(Path(p).read_text(encoding="utf-8"))


# --vp is shlex.split POSIX-style, which eats Windows backslashes; both interpreters take
# forward slashes. Same form as test_shotplan_plan.py.
FAKE = (sys.executable.replace("\\", "/") + " "
        + str(TOOLS / "fake_viewprobe.py").replace("\\", "/"))
MISSING_VP = (sys.executable.replace("\\", "/") + " "
              + str(TOOLS / "no_such_viewprobe_stand_in.py").replace("\\", "/"))

FINGERPRINT = "a" * 64          # what the emit records AND what the stand-in reports
OTHER_FINGERPRINT = "b" * 64    # a different configuration, for the mismatch case

CRATE_A = {"id": "000101:Later.esp", "edid": "CrateA", "min": [-160, -60, 0], "max": [-20, 60, 48]}
CRATE_B = {"id": "000101:Earlier.esp", "edid": "CrateB", "min": [-40, -60, 0], "max": [100, 60, 48]}
POINT_A = [-90.0, -60.0, 24.0]   # mid-height on CrateA's -Y face
POINT_B = [50.0, -60.0, 24.0]    # mid-height on CrateB's -Y face
POINT_B_AIR = [50.0, -180.0, 24.0]  # empty space 120 u in FRONT of that face -- nothing is there


def scene(fingerprint=FINGERPRINT, alcove=True):
    """The toy cell: an outer shell, a three-walled alcove open to -Y, and the two crates."""
    boxes = [
        {"id": "000201:Test.esp", "edid": "ShellN", "min": [-860, 800, 0], "max": [860, 860, 400]},
        {"id": "000202:Test.esp", "edid": "ShellS", "min": [-860, -860, 0], "max": [860, -800, 400]},
        {"id": "000203:Test.esp", "edid": "ShellE", "min": [800, -860, 0], "max": [860, 860, 400]},
        {"id": "000204:Test.esp", "edid": "ShellW", "min": [-860, -860, 0], "max": [-800, 860, 400]},
        dict(CRATE_A), dict(CRATE_B),
    ]
    if alcove:
        boxes += [
            {"id": "000211:Test.esp", "edid": "AlcoveN", "min": [-260, 120, 0], "max": [260, 180, 300]},
            {"id": "000212:Test.esp", "edid": "AlcoveE", "min": [200, -260, 0], "max": [260, 180, 300]},
            {"id": "000213:Test.esp", "edid": "AlcoveW", "min": [-260, -260, 0], "max": [-200, 180, 300]},
        ]
    sc = {"floorZ": 0.0, "boxes": boxes,
          "nav": [{"min": [-790, -790], "max": [790, 790], "z": 0.0, "name": "Test.esm"}]}
    if fingerprint:
        sc["configFingerprint"] = fingerprint
    return sc


def build_bundle(root: Path) -> tuple[Path, str, dict]:
    """Write the synthetic evidence bundle and packet; return (packet path, request id, request).

    The request is the REAL selector's output: a `geometry.object-overlap` finding over an
    `overlapprobe` emit whose one OVERLAP conflict names both participants.
    """
    root.mkdir(parents=True, exist_ok=True)
    emit_path = root / "overlap-emit.json"
    emit = {"Probe": "overlapprobe", "State": "baseline",
            "Configuration": {"Fingerprint": FINGERPRINT,
                              "Plugins": [{"Name": "Earlier.esp"}, {"Name": "Later.esp"}]},
            "Conflicts": [{"Kind": "OVERLAP", "A": "Earlier.esp", "B": "Later.esp",
                           "Necessary": [{"FormKey": "000101:Later.esp", "Label": "SyntheticAlcove",
                                          "Physical": "Holds",
                                          "PhysicalDetail": "the winning boxes overlap 20u deep, 0.14 of the smaller"}],
                           "Alternative": [{"FormKey": "000101:Earlier.esp"}]}]}
    emit_path.write_text(json.dumps(emit, indent=2), encoding="utf-8")
    pres = {"presentationId": "capture-acquire-fixture", "chapters": [],
            "source": {"bundleDir": str(root)},
            "findings": [{"id": "F-overlap", "kind": "finding", "checkId": "geometry.object-overlap",
                          "armId": "baseline", "part": "arm",
                          "counts": {"A": "Earlier.esp", "B": "Later.esp"},
                          "allRows": [{"formKey": "000101:Later.esp"}],
                          "evidenceIds": ["E-overlap"]}],
            "evidence": [{"id": "E-overlap", "kind": "emit", "part": "arm", "armId": "baseline",
                          "path": emit_path.name, "sha256": sha(emit_path)}]}
    pres_path = root / "presentation.json"
    pres_path.write_text(json.dumps(pres, indent=2), encoding="utf-8")
    plan = report_scenes.capture_requests(pres, root)
    assert len(plan["requests"]) == 1, plan
    request = plan["requests"][0]
    packet = {"schemaVersion": 1, "presentationId": pres["presentationId"], "scenes": [],
              "capturePlan": plan}
    packet_path = root / "storyboard-packet.json"
    packet_path.write_text(json.dumps(packet, indent=2), encoding="utf-8")
    return packet_path, request["id"], request


def points_doc(request_id: str, source_sha: str, *, a=POINT_A, b=POINT_B, drop_b=False,
               floor=0.0, **over) -> dict:
    rows = [{"formKey": "000101:Later.esp", "point": list(a), "label": "crateA-face",
             "surfaceOf": "CrateA -Y face, mid height"}]
    if not drop_b:
        rows.append({"formKey": "000101:Earlier.esp", "point": list(b), "label": "crateB-face",
                     "surfaceOf": "CrateB -Y face, mid height"})
    doc = {"schemaVersion": 1, "requestId": request_id, "sourceSha256": source_sha,
           "synthetic": True,
           "provenance": ("SYNTHETIC. Declared by hand for the offline acquisition test; the "
                          "renderer is fake_viewprobe.py over a toy scene. Not a measurement of "
                          "any real cell."),
           "cell": "SyntheticAlcove", "floorZ": floor, "points": rows,
           "camera": {"fov": 65.0, "aspect": "16:9", "standoffMin": 300.0, "standoffMax": 600.0,
                      "verifyCandidates": 3}}
    doc.update(over)
    return doc


def acquire(root: Path, packet: Path, request_id: str, points: dict, sc: dict, *,
            out=None, bind=None, vp=FAKE, tag="run", receipts=None):
    """Drive `capture_acquire.py acquire` as its own process, as an operator would."""
    scene_path = root / f"scene-{tag}.json"
    scene_path.write_text(json.dumps(sc), encoding="utf-8")
    for stale in (str(scene_path) + ".asks",):
        Path(stale).unlink(missing_ok=True)
    points_path = root / f"points-{tag}.json"
    points_path.write_text(json.dumps(points, indent=2), encoding="utf-8")
    out = Path(out) if out else root / f"acquisition-{tag}.json"
    env = dict(os.environ, FAKE_VIEWPROBE_SCENE=str(scene_path), PYTHONUTF8="1",
               PYTHONIOENCODING="utf-8")
    argv = [sys.executable, str(HARNESS / "capture_acquire.py"), "acquire",
            "--packet", str(packet), "--request", request_id, "--points", str(points_path),
            "--out", str(out),
            "--receipts", str(receipts or root / f"receipts-{tag}"), "--vp", vp]
    if bind:
        argv += ["--bind-packet", str(bind)]
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", env=env)
    doc = load(out) if out.is_file() else None
    return proc, doc, out, points_path


# --------------------------------------------------------------------------------------
# case 1 -- a valid request obtains a verified camera
# --------------------------------------------------------------------------------------

def case_ready(root: Path, packet: Path, rid: str, request: dict):
    bind = root / "packet-with-camera.json"
    proc, doc, out, _ = acquire(root, packet, rid, points_doc(rid, request["source"]["sha256"]),
                                scene(), bind=bind, tag="ready")
    check("a valid request with source-backed points obtains a verified camera",
          proc.returncode == 0 and doc and doc["status"] == "camera-ready",
          f"exit {proc.returncode}; " + " ".join((proc.stderr or proc.stdout).split())[-260:])
    if not doc or doc["status"] != "camera-ready":
        return None, None
    plan = load(Path(doc["receipts"]["planJson"]))
    check("the published camera is the planner's own pose, verification and pose id, unaltered",
          doc["poseId"] == plan["poseId"]
          and doc["camera"]["eye"] == list(plan["chosen"]["eye"])
          and doc["camera"]["yaw"] == plan["chosen"]["yaw"]
          and doc["camera"]["pitch"] == plan["chosen"]["pitch"]
          and doc["verification"] == plan["verification"]
          and doc["planner"]["status"] == "verified",
          f"pose {doc['poseId']} eye {doc['camera']['eye']}")
    check("every declared target was confirmed on its own surface by the planner",
          [t["status"] for t in doc["verification"]["targets"]] == ["confirmed", "confirmed"],
          str([(t["label"], t["status"]) for t in doc["verification"]["targets"]]))
    check("the result preserves the request, participant and source identity",
          doc["request"]["id"] == rid
          and doc["request"]["findingIds"] == request["findingIds"]
          and [p["formKey"] for p in doc["participants"]] == [h["formKey"] for h in request["highlights"]]
          and [p["color"] for p in doc["participants"]] == ["cyan", "magenta"]
          and doc["source"]["sha256"] == request["source"]["sha256"]
          and doc["source"]["path"] == request["source"]["path"])
    states = doc["states"]
    one = {json.dumps(s["camera"], sort_keys=True) for s in states}
    check("the same camera, canvas and pose id serve every comparison state",
          [s["state"] for s in states] == ["highlighted", "unhighlighted", "highlighted"]
          and len(one) == 1 and {s["poseId"] for s in states} == {doc["poseId"]}
          and all(s["onlyChangedProperty"] == "diagnosticHighlight" for s in states))
    check("visual qualification stays pending and names what is unreviewed",
          doc["visualQualification"]["status"] == "pending"
          and "objectComprehension" in (doc["visualQualification"]["outstanding"] or [])
          and doc["request"]["status"] == "needs-capture-and-visual-review")
    check("the observed configuration is the renderer's own and its narrow scope is restated",
          doc["configuration"]["observed"] == FINGERPRINT
          and doc["configuration"]["match"] is True
          and "NOT mesh/material bytes" in doc["configuration"]["scope"])
    check("the synthetic inputs are labelled as synthetic",
          doc["synthetic"] is True and "SYNTHETIC" in (doc["syntheticNote"] or "").upper())
    wl = load(Path(doc["receipts"]["worklist"]))
    check("the receipt set carries a renderable worklist at the same pose id",
          wl["status"] == "verified" and wl["poseId"] == doc["poseId"]
          and wl["cells"][0]["shots"][0]["eye"] == doc["camera"]["eye"])
    bound = load(bind)
    brow = [r for r in bound["capturePlan"]["requests"] if r["id"] == rid][0]
    orig = [r for r in load(packet)["capturePlan"]["requests"] if r["id"] == rid][0]
    check("the capture workflow's own packet receives the camera on that request",
          brow["camera"]["pose"]["eye"] == doc["camera"]["eye"]
          and brow["camera"]["poseId"] == doc["poseId"]
          and brow["camera"]["acquisition"]["resultSha256"] == sha(out)
          and brow["camera"]["reportMode"] is True
          and brow["camera"]["lockPoseAndFramingAcrossViews"] is True)
    check("binding a camera changes nothing about what the request selects or claims",
          {k: v for k, v in brow.items() if k != "camera"} == {k: v for k, v in orig.items() if k != "camera"}
          and bound["capturePlan"]["policySha256"] == load(packet)["capturePlan"]["policySha256"]
          and len(bound["capturePlan"]["requests"]) == len(load(packet)["capturePlan"]["requests"]))
    return doc, out


# --------------------------------------------------------------------------------------
# case 2 -- missing evidence and mismatched identity fail explicitly
# --------------------------------------------------------------------------------------

def case_prerequisites(root: Path, packet: Path, rid: str, request: dict):
    sha_src = request["source"]["sha256"]
    cases = [
        ("a missing participant point is an explicit prerequisite failure",
         points_doc(rid, sha_src, drop_b=True), "points-participant-missing"),
        ("a missing placement input is an explicit prerequisite failure",
         {k: v for k, v in points_doc(rid, sha_src).items() if k != "floorZ"},
         "points-placement-missing"),
        ("a point file for another request cannot acquire this one",
         points_doc("capture-000000000000", sha_src), "points-request-mismatch"),
        ("a point file citing another source cannot acquire this request",
         points_doc(rid, "0" * 64), "points-source-mismatch"),
        ("a point for a record that is not a participant is refused",
         points_doc(rid, sha_src, points=[
             {"formKey": "000101:Later.esp", "point": POINT_A},
             {"formKey": "000101:Earlier.esp", "point": POINT_B},
             {"formKey": "000999:Other.esp", "point": POINT_B}]), "points-participant-mismatch"),
        ("an undeclared synthetic flag is refused rather than assumed",
         {k: v for k, v in points_doc(rid, sha_src).items() if k != "synthetic"},
         "points-synthetic-undeclared"),
    ]
    for name, pts, code in cases:
        proc, doc, _out, _p = acquire(root, packet, rid, pts, scene(), tag="pre-" + code)
        check(name, proc.returncode != 0 and doc and doc["status"] == "refused"
              and doc["refusal"]["code"] == code,
              f"exit {proc.returncode} code {(doc or {}).get('refusal', {}).get('code')}")

    proc, doc, _o, _p = acquire(root, packet, "capture-deadbeefdead",
                                points_doc(rid, sha_src), scene(), tag="pre-absent")
    check("a request id that is not in the packet is refused",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "request-not-in-packet")

    # The source file itself, changed after the request was planned.
    moved = root / "changed-source"
    shutil.copytree(root, moved, ignore=shutil.ignore_patterns("changed-source", "receipts-*"))
    emit = moved / "overlap-emit.json"
    emit.write_text(emit.read_text(encoding="utf-8").replace("20u deep", "21u deep"),
                    encoding="utf-8")
    proc, doc, _o, _p = acquire(moved, moved / "storyboard-packet.json", rid,
                                points_doc(rid, sha_src), scene(), tag="changed")
    check("source evidence changed since its recorded hash cannot be accepted",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "source-changed",
          str((doc or {}).get("refusal", {}).get("code")))
    emit.unlink()
    proc, doc, _o, _p = acquire(moved, moved / "storyboard-packet.json", rid,
                                points_doc(rid, sha_src), scene(), tag="gone")
    check("a source file that is absent is refused rather than skipped",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "source-missing")


# --------------------------------------------------------------------------------------
# case 3 -- geometry verified, configuration absent or different
# --------------------------------------------------------------------------------------

def case_configuration(root: Path, packet: Path, rid: str, request: dict):
    sha_src = request["source"]["sha256"]
    proc, doc, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src),
                                scene(fingerprint=None), tag="no-config")
    plan = None
    rec = root / "receipts-no-config" / "shotplan-plan.json"
    if rec.is_file():
        plan = load(rec)
    check("a geometrically verified camera with NO renderer fingerprint is refused for this request",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "configuration-identity-absent"
          and plan and plan["status"] == "verified" and plan["scene"]["renderer"] is None,
          f"planner said {(plan or {}).get('status')}; adapter said "
          f"{(doc or {}).get('refusal', {}).get('code')}")
    check("the request's expected fingerprint never fills in the missing observed one",
          doc and doc["refusal"].get("observedFingerprint") is None
          and doc["refusal"].get("expectedFingerprint") == FINGERPRINT
          and FINGERPRINT not in json.dumps(doc.get("camera") or {}))
    check("a refusal on configuration publishes no camera at all",
          doc and "camera" not in doc and "poseId" not in doc)

    proc, doc, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src),
                                scene(fingerprint=OTHER_FINGERPRINT), tag="other-config")
    plan2 = load(root / "receipts-other-config" / "shotplan-plan.json")
    check("a verified camera judged against a DIFFERENT configuration is refused for this request",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "configuration-mismatch"
          and doc["refusal"]["observedFingerprint"] == OTHER_FINGERPRINT
          and doc["refusal"]["expectedFingerprint"] == FINGERPRINT
          and plan2["status"] == "verified",
          f"planner said {plan2['status']}")


# --------------------------------------------------------------------------------------
# case 4 -- planner refusal, subprocess failure, and a reused output path
# --------------------------------------------------------------------------------------

def case_refusals_and_staleness(root: Path, packet: Path, rid: str, request: dict):
    sha_src = request["source"]["sha256"]
    proc, doc, _o, _p = acquire(root, packet, rid,
                                points_doc(rid, sha_src, b=POINT_B_AIR), scene(), tag="air")
    plan = load(root / "receipts-air" / "shotplan-plan.json")
    check("a point declared where nothing is cannot publish a camera",
          proc.returncode != 0 and doc and doc["refusal"]["code"] == "planner-not-verified"
          and plan["status"] == "refused",
          f"planner said {plan['status']}, failed {plan['verification']['failed']}")
    check("the planner's own diagnostic receipts are preserved on refusal",
          (root / "receipts-air" / "shotplan-stdout.txt").is_file()
          and doc["refusal"].get("plannerFailures"))

    proc, doc, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src), scene(),
                                vp=MISSING_VP, tag="badvp")
    check("a renderer subprocess that cannot run cannot publish a camera",
          proc.returncode != 0 and doc and doc["status"] == "refused"
          and doc["refusal"]["code"] in ("planner-not-verified", "planner-unreadable"),
          f"exit {proc.returncode} code {(doc or {}).get('refusal', {}).get('code')}")

    # Reuse the SAME output and packet paths: success, then a failure at the same location.
    out = root / "reused-acquisition.json"
    bind = root / "reused-packet.json"
    proc, doc, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src), scene(),
                                out=out, bind=bind, tag="reuse-ok")
    ready = doc and doc["status"] == "camera-ready"
    first_pose = (doc or {}).get("poseId")
    proc2, doc2, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src, drop_b=True),
                                  scene(), out=out, bind=bind, tag="reuse-bad")
    after_bind = load(bind)
    check("reusing an output location cannot leave a stale successful result as the new answer",
          ready and proc2.returncode != 0 and doc2["status"] == "refused"
          and doc2["refusal"]["code"] == "points-participant-missing"
          and first_pose not in json.dumps(doc2),
          f"first {first_pose}, second {json.dumps(doc2)[:120]}")
    # The refused run never reaches the binder, so the packet path it claimed at the start holds
    # the claim stub -- not the previous run's packet with its camera still in it.
    check("a reused packet path cannot keep the earlier run's camera either",
          after_bind.get("status") == "incomplete"
          and first_pose not in json.dumps(after_bind),
          f"bound packet now: {json.dumps(after_bind)[:160]}")


# --------------------------------------------------------------------------------------
# case 5 -- generating the packet still starts no renderer
# --------------------------------------------------------------------------------------

def case_packet_stays_declarative(root: Path, packet: Path, rid: str, request: dict):
    trace = root / "packet-trace.txt"
    out = root / "packet-from-cli.json"
    env = dict(os.environ, FAKE_VIEWPROBE_SCENE=str(root / "scene-ready.json"),
               FAKE_VIEWPROBE_TRACE=str(trace), PYTHONUTF8="1")
    proc = subprocess.run([sys.executable, str(HARNESS / "evalreel.py"), "storyboard-packet",
                           "--presentation", str(root / "presentation.json"),
                           "--out", str(out), "--bundle", str(root)],
                          capture_output=True, text=True, encoding="utf-8", env=env)
    doc = load(out) if out.is_file() else None
    check("generating a storyboard packet still requires no renderer invocation",
          proc.returncode == 0 and doc is not None and not trace.exists(),
          f"exit {proc.returncode}; trace {'written' if trace.exists() else 'absent'}")
    fresh = report_scenes.capture_requests(load(root / "presentation.json"), root)
    check("request selection, sequencing and qualification semantics are unchanged",
          doc and doc["capturePlan"] == fresh
          and doc["capturePlan"]["requests"][0]["camera"]["pose"] is None
          and doc["capturePlan"]["requests"][0]["status"] == "needs-capture-and-visual-review")
    # And the packet the CLI wrote is directly acquirable -- the documented workflow, not a
    # hand-built fixture.
    proc2, doc2, _o, _p = acquire(root, out, rid,
                                  points_doc(rid, request["source"]["sha256"]), scene(),
                                  tag="from-cli")
    check("the acquisition command consumes the packet the reporting CLI itself produced",
          proc2.returncode == 0 and doc2 and doc2["status"] == "camera-ready",
          f"exit {proc2.returncode}")


# --------------------------------------------------------------------------------------
# case 6 -- the publication boundary: a saved success belongs to ONE invocation
# --------------------------------------------------------------------------------------

def case_publication_boundary(root: Path, packet: Path, rid: str, request: dict):
    """A reused receipts directory must not let an earlier planner result be republished.

    The hole this closes: `shotplan plan` claims its own output paths as the first statement of
    `cmd_plan`, but argparse rejecting the command line exits BEFORE `cmd_plan` runs -- the
    planner's own docstring says exactly that -- so nothing is claimed and the previous run's
    `status: verified` JSON is still sitting at the path this invocation asked for. An adapter
    that reads that file and does not check the exit code publishes the earlier camera as this
    request's answer, with the failed exit recorded inside the successful result (found in
    review, 2026-09-14).
    """
    sha_src = request["source"]["sha256"]
    shared = root / "receipts-shared"
    out = root / "boundary-acquisition.json"

    proc1, doc1, _o, _p = acquire(root, packet, rid, points_doc(rid, sha_src), scene(),
                                  out=out, receipts=shared, tag="boundary-ok")
    first_pose = (doc1 or {}).get("poseId")
    check("the boundary case starts from a genuine success",
          proc1.returncode == 0 and doc1 and doc1["status"] == "camera-ready" and first_pose,
          f"exit {proc1.returncode}")
    saved = load(shared / "shotplan-plan.json")
    check("that success left a verified planner result in the shared receipts directory",
          saved.get("status") == "verified")

    # Same receipts directory, a camera constraint the planner's own argument handling rejects.
    bad = points_doc(rid, sha_src)
    bad["camera"] = {**bad["camera"], "aspect": "not-an-aspect"}
    proc2, doc2, _o, _p = acquire(root, packet, rid, bad, scene(),
                                  out=out, receipts=shared, tag="boundary-bad")
    blob = json.dumps(doc2 or {})
    check("a planner invocation that fails cannot publish the previous run's camera",
          proc2.returncode != 0 and doc2 and doc2["status"] == "refused",
          f"exit {proc2.returncode} status {(doc2 or {}).get('status')} "
          f"code {(doc2 or {}).get('refusal', {}).get('code')}")
    check("the earlier pose id appears nowhere in the failed invocation's result",
          doc2 and first_pose not in blob,
          f"earlier pose {first_pose} present={first_pose in blob}")
    check("a nonzero planner exit is never recorded inside a successful result",
          doc2 and not (doc2.get("status") == "camera-ready"
                        and (doc2.get("planner") or {}).get("exitCode", 0) != 0))

    # The planner's own result paths were claimed before it ran, so the stale verified plan and
    # its renderable worklist are gone rather than merely unread.
    after_plan = load(shared / "shotplan-plan.json")
    after_wl = load(shared / "shotplan-worklist.json")
    check("a failed invocation does not leave the previous verified plan in the receipts",
          after_plan.get("status") == "incomplete" and after_wl.get("status") == "incomplete"
          and first_pose not in json.dumps(after_plan) + json.dumps(after_wl),
          f"plan now {after_plan.get('status')}, worklist now {after_wl.get('status')}")

    # The two gates, each proved load-bearing on its own rather than only in combination.
    sys.path.insert(0, str(HARNESS))
    import capture_acquire as CA
    mod = CA._load_shotplan()
    parts = CA.participants(request)
    pts = CA.read_points(root / "points-boundary-ok.json", request, parts, {"sha256": sha_src})
    src = {"evidenceId": "E-overlap", "path": request["source"]["path"],
           "sha256": sha_src, "rehashedAt": "fixture"}
    rec = {"planJson": str(shared / "shotplan-plan.json"), "worklist": "-",
           "plannerLog": "-", "receiptsDir": str(shared), "plannerExitCode": 2}

    def refusal_code(plan_doc, points_in, exit_code):
        try:
            CA.build_result(request, parts, points_in, src, plan_doc,
                            {"exitCode": exit_code, "argv": [], "log": "-", "stderrTail": None},
                            rec, mod)
            return None
        except CA.Refused as exc:
            return exc.code

    check("the exit-code gate alone refuses a verified result from a failed invocation",
          refusal_code(saved, pts, 2) == "planner-result-stale",
          f"got {refusal_code(saved, pts, 2)}")
    moved = copy.deepcopy(pts)
    moved["byKey"]["000101:Later.esp"]["point"] = [-90.0, -60.0, 41.0]
    check("the subject-identity gate alone refuses a plan about different points",
          refusal_code(saved, moved, 0) == "plan-subject-mismatch",
          f"got {refusal_code(saved, moved, 0)}")


def main(argv):
    if len(argv) >= 2 and argv[0] == "--emit":
        root = Path(argv[1]).resolve()
        if root.exists():
            shutil.rmtree(root)
        packet, rid, request = build_bundle(root)
        pts = points_doc(rid, request["source"]["sha256"])
        proc, doc, out, pts_path = acquire(root, packet, rid, pts, scene(),
                                           bind=root / "packet-with-camera.json", tag="demo")
        print((proc.stdout or "") + (proc.stderr or ""))
        print(f"demo bundle -> {root}   exit {proc.returncode}")
        return 0 if proc.returncode == 0 else 1

    with tempfile.TemporaryDirectory(prefix="capture-acquire-") as tmp:
        root = Path(tmp)
        packet, rid, request = build_bundle(root)
        print(f"request {rid}  participants "
              f"{[h['formKey'] for h in request['highlights']]}")
        print("\n-- case 1: a valid request obtains a verified camera")
        case_ready(root, packet, rid, request)
        print("\n-- case 2: missing evidence and mismatched identity")
        case_prerequisites(root, packet, rid, request)
        print("\n-- case 3: configuration identity absent or different")
        case_configuration(root, packet, rid, request)
        print("\n-- case 4: planner refusal, subprocess failure, reused output")
        case_refusals_and_staleness(root, packet, rid, request)
        print("\n-- case 5: packet generation stays declarative")
        case_packet_stays_declarative(root, packet, rid, request)
        print("\n-- case 6: the publication boundary (regression, review 2026-09-14)")
        case_publication_boundary(root, packet, rid, request)

    print()
    if FAILS:
        print(f"{len(FAILS)} FAILED:")
        for f in FAILS:
            print("  - " + f)
        return 1
    print("all capture-acquisition checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
