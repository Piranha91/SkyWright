#!/usr/bin/env python3
"""Acquire a verified camera for ONE reporting capture request, through the shared planner.

    python capture_acquire.py acquire --packet storyboard.json --request capture-xxxxxxxxxxxx \
        --points points.json --out acquisition.json [--bind-packet storyboard-with-camera.json]

WHAT THIS IS. `report_scenes.capture_requests` (reached by `evalreel storyboard-packet`) selects
evidence-bound capture requests and leaves `camera.pose` NULL, because a camera is a measurement
and request generation is declarative -- generating a packet must never start a renderer. This
adapter is the step that turns one such request into a camera, and it does so by CALLING
`shotplan.py plan`: the pose, the projection, the canonical rounding, the final status, the
verification and the renderer-configuration identity all come from there, verbatim. No scoring,
no projection, no camera convention and no depth acceptance is reimplemented here.

WHAT IT IS NOT. It does not find feature points. The measured world points for both participants
are supplied EXPLICITLY, by the operator, out of a source this adapter then re-binds to the
request (id, participants, source path + sha256, measured configuration fingerprint). Automatic
feature extraction, room discovery and guessing a contact-envelope centre are out of scope --
the last one is a known wrong answer: an overlap envelope's centre lies INSIDE the volume where
the two solids interpenetrate, so a camera aimed there is blocked from every position by
construction. A missing point or placement input is a prerequisite failure, never a default.

WHAT "READY" MEANS HERE, and what it does not. A result reaches `camera-ready` only when the
planner returned `verified` AND the verification render reported its own configuration
fingerprint AND that OBSERVED fingerprint equals the one recorded in the request's source
evidence. A declaration-only scene binding (no renderer fingerprint) is still valid planner
output and is deliberately NOT enough here: this request's camera has to be shown to have been
judged against the configuration the finding was measured in. The request's EXPECTED fingerprint
can never stand in for a missing observed one -- that substitution is the exact silent wrong
answer the binding exists to prevent.

The fingerprint's scope is the planner's, unchanged and restated in the result: the plugin
set/order and their content, the provider list, the BOS overlays, the population and the state.
NOT mesh or material bytes, NOT archive contents, NOT the renderer build. Same records and same
provider list is not "drew the same pixels with the same build".

A camera being ready is NOT visual qualification. The result carries
`visualQualification.status = "pending"` and says so: subject recognition, contact visibility and
defect support remain unreviewed, and admitting media still goes through the established
workflow. Nothing here admits synthetic or newly planned media as qualified, and no gate
threshold is touched.

The request's highlighted / unhighlighted / highlighted states share ONE camera: the same eye,
yaw, pitch, fov, aspect, canvas and pose id are emitted per state, because the only property the
sequence may change is the diagnostic highlight.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
SHOTPLAN = HERE / "viewprobe" / "tools" / "shotplan.py"

EXIT_OK, EXIT_REFUSED = 0, 2


# ---------------------------------------------------------------- borrowed, not copied --
# The planner owns the fingerprint's scope wording and the output-claim discipline. Loaded by
# path because a probe's tools directory is not on the import path; a failure to load is a hard
# refusal rather than a local re-statement, because a second opinion about either of these is
# how the two drift apart.
def _load_shotplan():
    spec = importlib.util.spec_from_file_location("housecarl_shotplan", SHOTPLAN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _load_runlog():
    cand = HERE.parent / "scripts" / "runlog.py"
    if not cand.is_file():
        return None
    try:
        spec = importlib.util.spec_from_file_location("housecarl_runlog", cand)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod
    except Exception:
        return None


_RUNLOG = _load_runlog()


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


# ---------------------------------------------------------------- small helpers --

def utcnow() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def write_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2, default=str)


def load_json(p: Path):
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


class Refused(Exception):
    """A named prerequisite or binding failure. The code is part of the contract."""

    def __init__(self, code: str, detail: str, **extra):
        super().__init__(f"{code}: {detail}")
        self.code, self.detail, self.extra = code, detail, extra


# ---------------------------------------------------------------- the request side --

SUPPORTED_REQUEST_TYPES = ("overlap-pair",)


def find_request(packet: dict, request_id: str) -> tuple[dict, dict]:
    """The named request out of a storyboard packet or a bare capture plan, with its plan."""
    plan = packet.get("capturePlan") if isinstance(packet.get("capturePlan"), dict) else packet
    if not isinstance(plan, dict) or not isinstance(plan.get("requests"), list):
        raise Refused("packet-unreadable",
                      "expected a storyboard packet with capturePlan.requests[] or a capture "
                      "plan with requests[]")
    rows = [r for r in plan["requests"] if isinstance(r, dict) and r.get("id") == request_id]
    if not rows:
        have = [r.get("id") for r in plan["requests"] if isinstance(r, dict)]
        raise Refused("request-not-in-packet",
                      f"{request_id!r} is not one of this packet's {len(have)} request(s)",
                      packetRequestIds=have)
    if len(rows) > 1:
        raise Refused("request-ambiguous", f"{request_id!r} appears {len(rows)} times")
    return rows[0], plan


def participants(request: dict) -> list[dict]:
    """The objects this request asks to be shown, with their assigned colour roles.

    For an `overlap-pair` those are the two highlighted records -- the pair the finding is about.
    Corroboration keys that ride along in `recordKeys` are context, not subjects, so they are not
    demanded as feature points; every highlighted key is.
    """
    highlights = request.get("highlights") or []
    keys = [h.get("formKey") for h in highlights if isinstance(h, dict) and h.get("formKey")]
    if len(keys) != len(highlights) or len(set(keys)) != len(keys) or len(keys) < 2:
        raise Refused("request-participants-unusable",
                      f"this request does not name two distinct highlighted participants "
                      f"(highlights={highlights!r})")
    for k in keys:
        if k not in (request.get("recordKeys") or []):
            raise Refused("request-participants-unusable",
                          f"highlighted {k!r} is not among the request's recordKeys")
    return [{"formKey": h["formKey"], "color": h.get("color")} for h in highlights]


def verify_source(request: dict, bundle: Path) -> dict:
    """The request's own source evidence, re-hashed NOW.

    `capture_requests` hashed this file when it selected the finding. A file that has changed
    since cannot back a camera that claims to belong to that finding, so this is re-checked at
    acquisition rather than trusted from the packet.
    """
    src = request.get("source") or {}
    want, rel = src.get("sha256"), src.get("path")
    if not want or not rel:
        raise Refused("source-unidentified",
                      "the request carries no source path + sha256 to bind the camera to")
    path = (bundle / rel).resolve()
    if not path.is_relative_to(bundle.resolve()):
        raise Refused("source-escapes-bundle", f"{rel!r} resolves outside {bundle}")
    if not path.is_file():
        raise Refused("source-missing", f"{path} is not present, so its hash cannot be checked")
    got = sha256_file(path)
    if got != want:
        raise Refused("source-changed",
                      f"{rel} hashes {got[:16]}… but the request recorded {want[:16]}… -- the "
                      f"evidence changed after the request was planned")
    return {"evidenceId": src.get("evidenceId"), "path": rel, "sha256": want,
            "rehashedAt": utcnow(), "absolutePath": str(path)}


# ---------------------------------------------------------------- the supplied-points side --

POINTS_DOC = """Supplied feature points -- the schema this adapter accepts:

  {
    "schemaVersion": 1,
    "requestId": "capture-xxxxxxxxxxxx",   # must equal the request being acquired
    "sourceSha256": "<the request's source sha256>",
    "synthetic": true,                     # REQUIRED and explicit; true must be labelled so
    "provenance": "where these numbers came from, in words",
    "cell": "<CellEditorID or FormID, args[0] for viewprobe>",
    "floorZ": 0.0,                         # the storey the camera stands on -- placement input
    "points": [
      {"formKey": "000001:Later.esp", "point": [x, y, z], "label": "…", "surfaceOf": "…"}
    ],
    "camera": {"fov": 65.0, "aspect": "16:9", "eyeHeight": 120.0, "size": 320,
               "surfaceExtent": 0.0, "standoffMin": 250.0, "standoffMax": 1200.0,
               "verifyCandidates": 3, "aim": [x, y, z]}   # every key optional
  }

One point per highlighted participant, no extras. Each point is a measured point ON an opaque
face of that participant, which is what the planner's `--surface` target means: its sightline is
proved by a ray that STOPS there. An overlap envelope's centre is not such a point."""

CAMERA_KEYS = {"fov": float, "aspect": str, "eyeHeight": float, "size": int,
               "surfaceExtent": float, "standoffMin": float, "standoffMax": float,
               "verifyCandidates": int, "bearingStep": float, "sightMargin": float,
               "surfaceTol": float, "aim": list}


def read_points(path: Path, request: dict, parts: list[dict], source: dict) -> dict:
    doc = load_json(path)
    if not isinstance(doc, dict):
        raise Refused("points-unreadable", f"{path} is not a JSON object\n\n{POINTS_DOC}")
    if doc.get("requestId") != request["id"]:
        raise Refused("points-request-mismatch",
                      f"the point file is for {doc.get('requestId')!r}, not {request['id']!r}")
    if doc.get("sourceSha256") != (request.get("source") or {}).get("sha256"):
        raise Refused("points-source-mismatch",
                      f"the point file cites source {str(doc.get('sourceSha256'))[:16]}… but this "
                      f"request's evidence is {str((request.get('source') or {}).get('sha256'))[:16]}…")
    if not isinstance(doc.get("synthetic"), bool):
        raise Refused("points-synthetic-undeclared",
                      "`synthetic` must be declared true or false: an offline or synthetic point "
                      "set has to be labelled as one wherever the camera is reported")
    if not doc.get("cell"):
        raise Refused("points-placement-missing",
                      "`cell` is required -- it is viewprobe's first positional and nothing can "
                      "be probed without it")
    if not isinstance(doc.get("floorZ"), (int, float)):
        raise Refused("points-placement-missing",
                      "`floorZ` is required: every target here is a surface point, whose Z is a "
                      "FEATURE height, and the eye height, the placement test and the default "
                      "aim are all measured from the storey instead")
    rows = doc.get("points")
    if not isinstance(rows, list) or not rows:
        raise Refused("points-missing", f"`points` must be a non-empty list\n\n{POINTS_DOC}")

    by_key: dict[str, dict] = {}
    for row in rows:
        if not isinstance(row, dict) or not row.get("formKey"):
            raise Refused("points-malformed", f"every point needs a formKey; got {row!r}")
        pt = row.get("point")
        if not (isinstance(pt, list) and len(pt) == 3 and all(isinstance(v, (int, float)) for v in pt)):
            raise Refused("points-malformed",
                          f"{row['formKey']}: `point` must be [x, y, z] in world units; got {pt!r}")
        if row["formKey"] in by_key:
            raise Refused("points-duplicate", f"{row['formKey']} is given twice")
        by_key[row["formKey"]] = row

    want = [p["formKey"] for p in parts]
    missing = [k for k in want if k not in by_key]
    if missing:
        raise Refused("points-participant-missing",
                      f"no measured point for {', '.join(missing)} -- this request needs one per "
                      f"highlighted participant ({', '.join(want)})")
    extra = [k for k in by_key if k not in want]
    if extra:
        raise Refused("points-participant-mismatch",
                      f"{', '.join(extra)} is not a participant of {request['id']} "
                      f"({', '.join(want)}); a point for another record cannot stand in for one "
                      f"of these")

    cam = doc.get("camera") or {}
    if not isinstance(cam, dict):
        raise Refused("points-malformed", "`camera` must be an object")
    for k, v in cam.items():
        if k not in CAMERA_KEYS:
            raise Refused("points-camera-unknown",
                          f"unknown camera constraint {k!r}; this adapter passes only "
                          f"{', '.join(sorted(CAMERA_KEYS))}")
        if not isinstance(v, CAMERA_KEYS[k]) and not (CAMERA_KEYS[k] is float and isinstance(v, int)):
            raise Refused("points-camera-malformed", f"camera.{k} must be {CAMERA_KEYS[k].__name__}")
    return {"doc": doc, "byKey": by_key, "camera": cam,
            "sha256": sha256_file(path), "path": str(path)}


# ---------------------------------------------------------------- the planner call --

def planner_paths(receipts: Path) -> tuple[Path, Path]:
    """Where the planner is asked to write. Named once so they can be CLAIMED before it runs."""
    return receipts / "shotplan-plan.json", receipts / "shotplan-worklist.json"


def surface_label(row: dict, part: dict) -> str:
    """The label this point is submitted under -- one spelling, used to send AND to check back."""
    return str(row.get("label") or part["formKey"]).replace("=", "-").replace(",", "-")[:18]


def declared_surfaces(points: dict, parts: list[dict]) -> list[tuple]:
    """The surface targets this invocation submitted, in the planner's own rounding.

    Compared against the plan's `subjects` on the way back, so a result about different points
    cannot be published under this request -- whatever the file says its status is.
    """
    out = []
    for part in parts:
        row = points["byKey"][part["formKey"]]
        x, y, z = (float(v) for v in row["point"])
        # `%g` is the formatting the flag carries, so the value compared is the value submitted.
        out.append((surface_label(row, part), float(f"{x:g}"), float(f"{y:g}"), float(f"{z:g}")))
    return sorted(out)


def plan_subjects(plan: dict) -> list[tuple]:
    rows = plan.get("subjects") or []
    out = []
    for s in rows:
        pos = s.get("pos") or [None, None, None]
        try:
            out.append((s.get("label"), float(pos[0]), float(pos[1]), float(pos[2])))
        except (TypeError, ValueError, IndexError):
            out.append((s.get("label"), None, None, None))
    return sorted(out)


def plan_argv(points: dict, parts: list[dict], receipts: Path, vp: str | None,
              passthrough: list[str]) -> tuple[list[str], Path, Path]:
    """The `shotplan.py plan` command line. Every camera decision is ITS flag, not our arithmetic."""
    doc, cam = points["doc"], points["camera"]
    plan_json, worklist = planner_paths(receipts)
    argv = [sys.executable, str(SHOTPLAN), "plan", str(doc["cell"]),
            "--floor-z", repr(float(doc["floorZ"])),
            "--json", str(plan_json), "--worklist", str(worklist),
            "--label", f"capture request {doc['requestId']}"]
    for p in parts:
        row = points["byKey"][p["formKey"]]
        x, y, z = (float(v) for v in row["point"])
        label = surface_label(row, p)
        # `=` form: a leading negative coordinate is read as an option otherwise.
        argv += [f"--surface={x:g},{y:g},{z:g}={label}"]
    flags = {"fov": "--fov", "aspect": "--aspect", "eyeHeight": "--eye-height", "size": "--size",
             "surfaceExtent": "--surface-extent", "standoffMin": "--standoff-min",
             "standoffMax": "--standoff-max", "verifyCandidates": "--verify-candidates",
             "bearingStep": "--bearing-step", "sightMargin": "--sight-margin",
             "surfaceTol": "--surface-tol"}
    for k, flag in flags.items():
        if k in cam:
            argv += [flag, str(cam[k])]
    if "aim" in cam:
        argv += ["--aim=" + ",".join(f"{float(v):g}" for v in cam["aim"])]
    if vp:
        argv += ["--vp", vp]
    if passthrough:
        argv += ["--"] + list(passthrough)
    return argv, plan_json, worklist


def call_planner(argv: list[str], receipts: Path) -> dict:
    """Run the planner as its own process, and keep what it said.

    A subprocess rather than an import: the exit code is part of shotplan's contract (0 verified,
    1 refused, 2 unverified) and its `__main__` is where its own run-log lifecycle lives, so
    calling `cmd_plan` in-process would quietly drop both.
    """
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", cwd=str(HERE))
    log = receipts / "shotplan-stdout.txt"
    log.write_text((proc.stdout or "") + ("\n--- stderr ---\n" + proc.stderr if proc.stderr else ""),
                   encoding="utf-8")
    return {"argv": argv, "exitCode": proc.returncode, "log": str(log),
            "stderrTail": " ".join((proc.stderr or "").split())[-400:] or None}


# ---------------------------------------------------------------- the result --

def build_result(request: dict, parts: list[dict], points: dict, source: dict, plan: dict,
                 call: dict, receipts: dict, shotplan_mod) -> dict:
    """The acquisition result: the planner's answers, bound to the request that asked for them.

    Every camera number here is READ from the planner's JSON. The only thing this function
    decides is whether the binding holds -- and it refuses rather than softening when it does
    not.
    """
    status = plan.get("status")
    chosen = plan.get("chosen") or {}
    verification = plan.get("verification")
    scene = plan.get("scene") or {}
    observed = (scene.get("renderer") or None)
    expected = request.get("configurationFingerprint")
    exit_code = call["exitCode"]

    # GATE 1 -- THIS invocation's planner run must have succeeded, and the file read back must be
    # the one it wrote. Two halves, and neither implies the other:
    #
    #   `shotplan plan` claims its own output paths as the first statement of `cmd_plan`, but
    #   argparse rejecting the command line exits BEFORE `cmd_plan` runs -- the planner's own
    #   docstring says exactly that. So a malformed camera constraint (a bad `--aspect`, say)
    #   never reaches a writer, and the PREVIOUS run's `status: verified` JSON is still sitting
    #   at the path this invocation asked for. Reading that file without checking the exit code
    #   republished the earlier camera as this request's answer -- exit 0, `camera-ready`, with
    #   the failed exit recorded inside the successful result (found in review, 2026-09-14).
    #
    # The paths are now also claimed before the subprocess starts (see cmd_acquire), so a result
    # file cannot outlive the run that wrote it. The exit code is kept as a SEPARATE gate because
    # it does not depend on any writer having been reached, and a false ready is the one failure
    # this adapter exists to prevent. Its contract: 0 verified, 1 refused, 2 unverified.
    if exit_code != 0 and status == "verified":
        raise Refused("planner-result-stale",
                      f"the planner exited {exit_code} but the result at "
                      f"{Path(receipts['planJson']).name} says `verified` -- that file does not "
                      f"belong to this invocation and its camera is NOT this request's answer",
                      plannerExitCode=exit_code, plannerStatus=status,
                      stderrTail=call.get("stderrTail"))
    # Not `exit_code != 0 or status != "verified"`: that disjunction reads like defence in depth
    # and is dead. Every nonzero exit arrives here with a status that is not `verified` -- the
    # claim stub's `incomplete` when no writer was reached, `refused` when the planner probed and
    # said no -- and the one case where a nonzero exit carries `verified` is the stale file the
    # gate above already refuses. A sabotage sweep proved it: removing the exit-code half left
    # every check green, and a branch with no honest fixture is a clause to delete rather than a
    # test to invent. The exit code stays in the refusal payload as diagnostics, not as a gate.
    if status != "verified":
        why = {"refused": "the planner probed this pose and refused it",
               "unverified": "the final verification was SKIPPED, so no camera was checked",
               "incomplete": "the planner did not finish and left only its start-of-run claim",
               None: "the planner wrote no status"}.get(status, f"planner status {status!r}")
        raise Refused("planner-not-verified",
                      f"{why} (exit {exit_code}); no camera is published for this request",
                      plannerStatus=status, plannerExitCode=exit_code,
                      stderrTail=call.get("stderrTail"),
                      plannerFailures=(verification or {}).get("failed") or chosen.get("fail"))

    # GATE 2 -- the plan has to be about the points this invocation submitted. A status and an
    # exit code say a run succeeded; they do not say WHICH targets it succeeded on.
    want, got = declared_surfaces(points, parts), plan_subjects(plan)
    kinds = {s.get("kind") for s in (plan.get("subjects") or [])}
    if got != want or kinds != {"surface"}:
        raise Refused("plan-subject-mismatch",
                      "the planner's result describes different targets than this invocation "
                      "submitted, so it is not this request's camera",
                      submitted=[list(t) for t in want], plannedFor=[list(t) for t in got],
                      plannedKinds=sorted(k for k in kinds if k))

    if not chosen.get("ok"):
        # Belt-and-braces against a future planner that reports `verified` over a refused pose.
        raise Refused("planner-inconsistent",
                      "the planner reports status `verified` but its chosen camera is not ok",
                      plannerFailures=chosen.get("fail"))
    if not observed:
        raise Refused("configuration-identity-absent",
                      "the verification render reported NO configuration identity, so the camera "
                      "is bound to this scene by declaration only. This request's expected "
                      f"fingerprint ({str(expected)[:16]}…) must not be used to fill that in",
                      expectedFingerprint=expected, observedFingerprint=None)
    if not expected:
        raise Refused("request-configuration-unidentified",
                      "the request records no measured configuration fingerprint to match the "
                      "renderer's against", observedFingerprint=observed)
    if observed != expected:
        raise Refused("configuration-mismatch",
                      f"the renderer read configuration {observed[:16]}… but this finding was "
                      f"measured in {expected[:16]}… -- the camera was judged against a different "
                      f"scene than the request",
                      expectedFingerprint=expected, observedFingerprint=observed)

    pose = (verification or {}).get("pose") or {}
    # THE EXPORTED COMMAND MUST CARRY THE ASPECT. It used to emit eye/yaw/pitch/fov only, beside a
    # `camera.aspect` of 1.7778 -- so a caller who followed the command exactly got viewprobe's
    # square, which keeps the vertical angle and crops about a third off the horizontal, and the
    # framing numbers that qualified this camera stopped describing the picture they took. Nothing
    # errored; the two fields simply disagreed and only the one nobody executes was right (Piranha,
    # 2026-09-15, on a sequence captured square from exactly this command).
    camera = {"cell": plan.get("cell"),
              "eye": list(chosen["eye"]), "yaw": chosen["yaw"], "pitch": chosen["pitch"],
              "fov": pose.get("fov"), "aspect": pose.get("aspect"),
              "probeFov": pose.get("probeFov"), "probeSize": pose.get("probeSize"),
              "feet": plan.get("feet"), "floorZ": plan.get("floorZ"), "aim": plan.get("aim"),
              "viewprobe": (f"--eye {chosen['eye'][0]:.1f},{chosen['eye'][1]:.1f},"
                            f"{chosen['eye'][2]:.1f} --yaw {chosen['yaw']:.1f} "
                            f"--pitch {chosen['pitch']:.1f} --fov {pose.get('fov')}"
                            + shotplan_mod.aspect_flag(pose.get("aspect")))}
    sequence = request.get("sequence") or {}
    states = [{"state": s, "poseId": plan["poseId"], "camera": camera,
               "onlyChangedProperty": sequence.get("onlyChangedProperty")}
              for s in (sequence.get("states") or [])]

    return {
        "schemaVersion": 1,
        "kind": "capture-acquisition",
        "status": "camera-ready",
        "generatedUtc": utcnow(),
        "synthetic": bool(points["doc"].get("synthetic")),
        "syntheticNote": ("The supplied feature points and the renderer stand-in are SYNTHETIC. "
                          "This camera is a demonstration of the acquisition path, not evidence "
                          "about any real cell.") if points["doc"].get("synthetic") else None,
        "request": {"id": request["id"], "requestType": request.get("requestType"),
                    "checkId": request.get("checkId"), "armId": request.get("armId"),
                    "findingIds": request.get("findingIds"), "location": request.get("location"),
                    "recordKeys": request.get("recordKeys"), "state": request.get("state"),
                    "status": request.get("status")},
        "participants": [{**p, "point": points["byKey"][p["formKey"]]["point"],
                          "label": points["byKey"][p["formKey"]].get("label"),
                          "surfaceOf": points["byKey"][p["formKey"]].get("surfaceOf")}
                         for p in parts],
        "source": source,
        "suppliedPoints": {"path": points["path"], "sha256": points["sha256"],
                           "provenance": points["doc"].get("provenance"),
                           "cell": points["doc"].get("cell"), "floorZ": points["doc"].get("floorZ"),
                           "cameraConstraints": points["camera"]},
        "configuration": {
            "expected": expected, "observed": observed, "match": True,
            "source": "the verification render's own `capture input configuration:` line",
            "scope": shotplan_mod.SCENE_FINGERPRINT_SCOPE,
            "covers": shotplan_mod.SCENE_FINGERPRINT_COVERS,
            "note": ("An equal fingerprint means the same records and the same provider list. It "
                     "is NOT a claim that the same pixels were drawn by the same renderer build. "
                     "A declaration-only identity is valid planner output but cannot satisfy "
                     "this request.")},
        "planner": {"tool": "shotplan.py plan", "status": status, "exitCode": call["exitCode"],
                    "argv": call["argv"], "renders": plan.get("renders"),
                    "seconds": plan.get("seconds"),
                    "verifyAttempts": plan.get("verifyAttempts")},
        "poseId": plan["poseId"],
        "camera": camera,
        "scene": scene,
        "verification": verification,
        "states": states,
        "sequence": sequence,
        "statesNote": ("One camera serves every state: the pose, projection and canvas above are "
                       "identical across the request's sequence, and the only property the "
                       "sequence may change is the diagnostic highlight."),
        "visualQualification": {
            "status": "pending",
            "note": ("A verified camera is geometry, not qualified media. Subject recognition, "
                     "contact visibility and defect support are unreviewed here. Capture and "
                     "admission still run through the established workflow; nothing in this "
                     "result admits media."),
            "outstanding": (request.get("review") or {}).get("separateJudgments")},
        "receipts": receipts,
    }


def refusal_doc(request_id: str, exc: Refused, extra: dict) -> dict:
    return {"schemaVersion": 1, "kind": "capture-acquisition", "status": "refused",
            "generatedUtc": utcnow(), "requestId": request_id,
            "refusal": {"code": exc.code, "detail": exc.detail, **exc.extra},
            "note": ("No camera is published for this request. This file replaces whatever was "
                     "at this path, so an earlier run's ready result cannot be read as this "
                     "run's answer."),
            **extra}


# ---------------------------------------------------------------- packet binding --

def bind_packet(packet: dict, request_id: str, result: dict, result_path: Path) -> dict:
    """Write the acquired camera back onto its request, and change nothing else.

    This is how the existing capture workflow picks the camera up: the packet it already reads
    now carries, on the one request that was acquired, the planner's pose, its pose id, its
    verification, the configuration match and the renderable worklist. Selection, claims,
    ranking, sequence, review requirements and every other request are untouched, and
    `status` still says the visual review is outstanding.
    """
    out = json.loads(json.dumps(packet))
    plan = out.get("capturePlan") if isinstance(out.get("capturePlan"), dict) else out
    hit = None
    for row in plan["requests"]:
        if isinstance(row, dict) and row.get("id") == request_id:
            hit = row
            break
    if hit is None:
        raise Refused("bind-request-not-in-packet",
                      f"{request_id!r} is not in the packet being bound")
    cam = result["camera"]
    hit["camera"] = {**(hit.get("camera") or {}),
                     "pose": {"eye": cam["eye"], "yaw": cam["yaw"], "pitch": cam["pitch"],
                              "fov": cam["fov"], "aspect": cam["aspect"], "cell": cam["cell"],
                              "feet": cam["feet"], "floorZ": cam["floorZ"]},
                     "poseId": result["poseId"],
                     "poseStatus": "verified-geometry-visual-review-pending",
                     "acquisition": {"resultPath": str(result_path),
                                     "resultSha256": None,  # filled by the caller after writing
                                     "plannerStatus": result["planner"]["status"],
                                     "configurationMatch": result["configuration"]["match"],
                                     "observedFingerprint": result["configuration"]["observed"],
                                     "worklist": result["receipts"].get("worklist"),
                                     "synthetic": result["synthetic"]},
                     "nextAction": ("The camera is planned and geometrically verified. Capture the "
                                    "request's states at this exact pose, then run the established "
                                    "visual review; this pose is not qualified media.")}
    return out


# ---------------------------------------------------------------- the command --

def cmd_acquire(a) -> int:
    out = Path(a.out).resolve()
    receipts_dir = Path(a.receipts).resolve() if a.receipts else out.parent / (out.stem + "-receipts")
    receipts_dir.mkdir(parents=True, exist_ok=True)
    shotplan_mod = _load_shotplan()
    # Claim every output path BEFORE reading an input, with the planner's own discipline: an
    # input refusal exits before any writer, and a path the caller reuses must never be left
    # holding an earlier run's ready result. Same reason, same helper.
    #
    # The planner's OWN result paths are claimed here too, and that is not belt-and-braces: it
    # claims its outputs inside `cmd_plan`, which argparse rejecting the command line never
    # reaches, so without this a reused receipts directory keeps the previous run's verified
    # plan and worklist across a failed invocation (review, 2026-09-14).
    plan_json, worklist = planner_paths(receipts_dir)
    claimed = [str(out), str(plan_json), str(worklist)]
    if a.bind_packet:
        claimed.append(str(Path(a.bind_packet).resolve()))
    shotplan_mod.claim_outputs(claimed, a.request)

    with _run("capture-acquire", mode="acquire", stage_source="counted") as run:
        run.stage("acquire", total=1, unit="requests")
        try:
            packet_path = Path(a.packet).resolve()
            packet = load_json(packet_path)
            request, _plan = find_request(packet, a.request)
            if request.get("requestType") not in SUPPORTED_REQUEST_TYPES:
                raise Refused("request-type-unsupported",
                              f"{request.get('requestType')!r} is not one of "
                              f"{', '.join(SUPPORTED_REQUEST_TYPES)}; this adapter plans a camera "
                              f"for a two-object pair and nothing else")
            parts = participants(request)
            bundle = Path(a.bundle).resolve() if a.bundle else packet_path.parent
            source = verify_source(request, bundle)
            points = read_points(Path(a.points).resolve(), request, parts, source)

            argv, _pj, _wl = plan_argv(points, parts, receipts_dir, a.vp, a.passthrough)
            call = call_planner(argv, receipts_dir)
            try:
                plan = load_json(plan_json)
            except (OSError, ValueError) as exc:
                raise Refused("planner-unreadable",
                              f"shotplan exited {call['exitCode']} and left no readable result at "
                              f"{plan_json.name} ({exc}); receipts kept at {receipts_dir}",
                              plannerExitCode=call["exitCode"], stderrTail=call["stderrTail"])
            receipts = {"planJson": str(plan_json), "worklist": str(worklist),
                        "plannerLog": call["log"], "receiptsDir": str(receipts_dir),
                        "plannerExitCode": call["exitCode"]}
            result = build_result(request, parts, points, source, plan, call, receipts,
                                  shotplan_mod)
        except Refused as exc:
            write_json(out, refusal_doc(a.request, exc,
                                        {"receipts": {"receiptsDir": str(receipts_dir)}}))
            print(f"REFUSED {exc.code}: {exc.detail}", file=sys.stderr)
            print(f"  receipts kept -> {receipts_dir}", file=sys.stderr)
            run.refuse(exc.code, EXIT_REFUSED)
            return EXIT_REFUSED

        write_json(out, result)
        if a.bind_packet:
            bound = bind_packet(packet, a.request, result, out)
            bp = Path(a.bind_packet).resolve()
            plan_b = bound.get("capturePlan") if isinstance(bound.get("capturePlan"), dict) else bound
            for row in plan_b["requests"]:
                if row.get("id") == a.request:
                    row["camera"]["acquisition"]["resultSha256"] = sha256_file(out)
            write_json(bp, bound)
            run.artifact(str(bp))
        run.advance(1)
        run.artifact(str(out))
        run.tally(ok=1)

    print(f"camera-ready {a.request}: pose {result['poseId']} at "
          f"{result['camera']['eye']} yaw {result['camera']['yaw']} pitch {result['camera']['pitch']}")
    print(f"  configuration {result['configuration']['observed'][:16]}… matches the request's")
    print(f"  states: {', '.join(s['state'] for s in result['states']) or '(none declared)'} "
          f"-- one camera, visual qualification PENDING")
    print(f"  result   -> {out}")
    print(f"  worklist -> {result['receipts']['worklist']}  "
          f"(render: viewprobe --worklist <it>; drive: shotplan.py devbench <it>)")
    if a.bind_packet:
        print(f"  packet   -> {Path(a.bind_packet).resolve()}  (camera.pose filled on this request)")
    return EXIT_OK


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="capture_acquire.py",
                                 description=__doc__.split("\n\n")[0],
                                 epilog=POINTS_DOC,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("acquire", help="plan and verify one request's camera",
                       epilog=POINTS_DOC,
                       formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--packet", required=True,
                   help="storyboard packet (evalreel storyboard-packet --out) or a bare capture plan")
    p.add_argument("--request", required=True, help="the capture request id to acquire")
    p.add_argument("--points", required=True, help="supplied measured feature points (see below)")
    p.add_argument("--out", required=True, help="where the acquisition result is written")
    p.add_argument("--bundle", default=None,
                   help="where the request's source path resolves (default: the packet's directory)")
    p.add_argument("--receipts", default=None,
                   help="directory for the planner's JSON, worklist and log "
                        "(default: <out>-receipts)")
    p.add_argument("--bind-packet", default=None,
                   help="also write a copy of the packet with this request's camera.pose filled in")
    p.add_argument("--vp", default=None, help="viewprobe command, passed straight to shotplan")
    p.set_defaults(fn=cmd_acquire)
    argv = list(sys.argv[1:] if argv is None else argv)
    passthrough = []
    if "--" in argv:
        i = argv.index("--")
        argv, passthrough = argv[:i], argv[i + 1:]
    a = ap.parse_args(argv)
    a.passthrough = passthrough
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
