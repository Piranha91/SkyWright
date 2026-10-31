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

WHERE THE POINTS COME FROM. The measured world points for both participants are supplied as a
file this adapter then re-binds to the request (id, participants, source path + sha256, measured
configuration fingerprint), or, with `--points auto`, measured first by `capture_points.py`: a
point per participant on a face an eye-level camera around the pair sees, from the renderer's own
participant marks and depth picks, plus the floor and the witnessed bearing window. Guessing a
contact-envelope centre stays out of scope -- a known wrong answer: an overlap envelope's centre
lies INSIDE the volume where the two solids interpenetrate, so a camera aimed there is blocked from
every position by construction. A missing point or placement input is a prerequisite failure, never
a default, and a picker that cannot find a visible face refuses in one sentence.

WHAT "READY" MEANS HERE, and what it does not. A result reaches `camera-ready` only when the
planner returned `verified` AND the verification render reported its own configuration
fingerprint AND that OBSERVED fingerprint equals the one recorded in the request's source
evidence. A declaration-only scene binding (no renderer fingerprint) is still valid planner
output and is deliberately NOT enough here: this request's camera has to be shown to have been
judged against the configuration the finding was measured in. The request's EXPECTED fingerprint
can never stand in for a missing observed one -- that substitution is the exact silent wrong
answer the binding exists to prevent.

A POPULATION-SCOPED request (P25: its source emit's population is `<base>+population:<id>`) carries a fingerprint
that differs from any renderer's by the population line alone, because a scoped probe measured only some rows of
the same scene. For it the observed fingerprint is compared with the SCENE identity rebuilt from the re-hashed
source emit's own configuration -- once that configuration is shown to give the request's fingerprint -- with the
population line set to `<base>` (`scene_identity`, P33). Any other difference still refuses.

The fingerprint's scope is the planner's, unchanged and restated in the result: the plugin
set/order and their content, the provider list, the BOS overlays, the population and the state.
NOT mesh or material bytes, NOT archive contents, NOT the renderer build. Same records and same
provider list is not "drew the same pixels with the same build".

A camera being ready is NOT visual qualification. The result carries
`visualQualification.status = "pending"` and says so: subject recognition, contact visibility and
defect support remain unreviewed, and admitting media still goes through the established
workflow. Nothing here admits synthetic or newly planned media as qualified, and no gate
threshold is touched.

The request's without / with / highlight states share ONE camera: the same eye,
yaw, pitch, fov, aspect, canvas and pose id are emitted per state, because the only property the
sequence may change is the diagnostic highlight.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import shlex
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


def _load_points():
    """The point picker beside this file, loaded only for `--points auto`."""
    spec = importlib.util.spec_from_file_location("housecarl_capture_points", HERE / "capture_points.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# The configuration fingerprint has ONE Python spelling, evalrun's `configuration_fingerprint` (the text of
# `_shared/Configuration.cs`'s ConfigurationIdentity.Compute). Loaded the same way as the planner, for the same reason.
EVALRUN = HERE / "evalrun.py"


def _load_evalrun():
    spec = importlib.util.spec_from_file_location("housecarl_evalrun_fingerprint", EVALRUN)
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


def capture_tool_identity(vp=None) -> dict:
    """The capture's tool identity: this module, the point picker and the renderer build. A result taken with another
    identity is not reused (evalpipeline.reusable_capture)."""
    here_ = Path(__file__).resolve().parent
    vp_ = Path(vp) if vp else VIEWPROBE
    out_ = {}
    for name_, p_ in (("capture_acquire.py", here_ / "capture_acquire.py"), ("capture_points.py", here_ / "capture_points.py"),
                      ("viewprobe", Path(str(vp_)))):
        out_[name_] = sha256_file(p_) if p_.is_file() else None
    return out_


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

SUPPORTED_REQUEST_TYPES = ("overlap-pair", "state-pair", "placement-split")

# Frozen at Gate B (2026-10-31): a top-down picture is accepted only when each participant shows at least this many of its
# own marked pixels (--ref-surface colour or --coverage-of mask, the view label left out) in the probe frame, 320 px wide
# at the request's aspect. Fewer, and the top-down refuses for that participant rather than publish a picture of the
# floor around it.
TOP_DOWN_MIN_PIXELS = 25
TOP_DOWN_PROBE_SIZE = 320


def top_down_accepts(counts: dict) -> tuple[bool, str | None]:
    """(accepted, why not): every participant at TOP_DOWN_MIN_PIXELS or more."""
    short = {k: v for k, v in counts.items() if v < TOP_DOWN_MIN_PIXELS}
    if not short:
        return True, None
    k, v = sorted(short.items())[0]
    return False, (f"{k} shows {v} marked pixel(s) from above, under the {TOP_DOWN_MIN_PIXELS} the top-down needs")
SPLIT_SIZE, SPLIT_ASPECT = 638, "638:680"      # a split half (skill data scene-policy vignettes.split.half)


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
    if request.get("requestType") == "placement-split":
        # one placed object or actor, shown at two placements: its own highlight is the one participant
        rows = [h for h in request.get("highlights") or [] if isinstance(h, dict) and h.get("formKey")]
        if len(rows) != 1 or rows[0]["formKey"] not in (request.get("recordKeys") or []):
            raise Refused("request-participants-unusable",
                          f"a placement split names one highlighted participant among its recordKeys (highlights={rows!r})")
        return [{"formKey": rows[0]["formKey"], "color": rows[0].get("color")}]
    # a world-state pair may mark only its smaller participant, but its camera is planned on both (pointParticipants)
    highlights = request.get("pointParticipants") or request.get("highlights") or []
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


# ---------------------------------------------------------------- a population-scoped source --

#: A P25 scoped emit's population (`PopulationScope.IdentityOf`): the enable population, then the population file's id.
SCOPED_POPULATION = re.compile(r"^(?P<base>.+)\+population:[0-9a-f]{12}$")


def scene_identity(source: dict, expected: str, state: str | None = None) -> dict | None:
    """For a request whose source emit was measured on a plan's POPULATION (P25), the identity of the SCENE it was measured
    in: the same configuration with the population line set back to the enable population the renderer reads. None when the
    source's population is not of that form (the ordinary comparison then stands alone).

    A scoped probe judged only the rows its population reaches, but over the whole load order: its fingerprint differs from
    a renderer's only in the population line (P33). The scene identity is rebuilt from the source emit's OWN configuration
    (re-hashed by `verify_source`), and only after that configuration is shown to give the request's fingerprint, so the
    renderer is compared with the plugins, hashes, order, providers, overlays and state the finding was measured in."""
    try:
        doc = load_json(Path(source["absolutePath"]))
    except (OSError, ValueError, KeyError) as exc:
        raise Refused("source-unreadable", f"the source emit could not be read to rebuild its configuration ({exc})")
    cfg = doc.get("Configuration") if isinstance(doc, dict) else None
    population = (cfg or {}).get("Population")
    m = SCOPED_POPULATION.match(population or "")
    if not m and state is None:
        return None
    base = m.group("base") if m else population
    fp = _load_evalrun().configuration_fingerprint

    def recompute(pop: str, st=None) -> str:
        return fp([{"name": x.get("Name"), "sha256": x.get("Sha256")} for x in cfg.get("Plugins") or []],
                  [{"kind": x.get("Kind"), "name": x.get("Name")} for x in cfg.get("Providers") or []],
                  [{"kind": x.get("Kind"), "sha256": x.get("Sha256")} for x in cfg.get("Overlays") or []],
                  pop, cfg.get("State") if st is None else st, cfg.get("SchemaVersion"))
    own = recompute(population)
    if own != cfg.get("Fingerprint") or own != expected:
        raise Refused("source-configuration-inconsistent",
                      f"the source emit's configuration recomputes to {own[:16]}…, not to its recorded fingerprint "
                      f"{str(cfg.get('Fingerprint'))[:16]}… and the request's {str(expected)[:16]}…, so the scene it was "
                      "measured in cannot be rebuilt from it", recomputedFingerprint=own, expectedFingerprint=expected)
    # a world-state request: stateprobe measures every flip in one run, so its emit's state line is "baseline" and the
    # clash's state is the switch its conflict Kind names; the scene is the emit's configuration in that state
    return {"population": population, "base": base, "state": state, "sceneFingerprint": recompute(base, state),
            "rule": ("the request's fingerprint is population-bound (P25): the source emit's own configuration was "
                     "recomputed to it, then with the population line set to the enable population the renderer reads; "
                     "the renderer's fingerprint equals that, so the plugins, their hashes and order, the providers, "
                     "overlays and state are the ones the finding was measured in")}


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
               "verifyCandidates": 3, "aim": [x, y, z],
               "bearingWindow": [centre, half]}   # every key optional
  }

One point per highlighted participant, no extras. Each point is a measured point ON an opaque
face of that participant, which is what the planner's `--surface` target means: its sightline is
proved by a ray that STOPS there. An overlap envelope's centre is not such a point."""

CAMERA_KEYS = {"fov": float, "aspect": str, "eyeHeight": float, "size": int,
               "surfaceExtent": float, "standoffMin": float, "standoffMax": float,
               "verifyCandidates": int, "bearingStep": float, "sightMargin": float,
               "surfaceTol": float, "aim": list, "bearingWindow": list,
               # a see-through mark is planned as a --subject-nodepth, which the planner frames as a figure of this size
               # standing on its point; the picker sets it small, since the mark is a point on the object's surface
               "subjectHeight": float, "subjectWidth": float}


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
        if row.get("markKind") == "coverage":
            # a see-through mark writes no depth, so no ray can stop on it: framed with the depth test omitted, and
            # checked again at the final pose by its own coverage (coverage_at_pose)
            argv += [f"--subject-nodepth={x:g},{y:g},{z:g}={label}"]
        else:
            argv += [f"--surface={x:g},{y:g},{z:g}={label}"]
    flags = {"fov": "--fov", "aspect": "--aspect", "eyeHeight": "--eye-height", "size": "--size",
             "surfaceExtent": "--surface-extent", "standoffMin": "--standoff-min",
             "standoffMax": "--standoff-max", "verifyCandidates": "--verify-candidates",
             "bearingStep": "--bearing-step", "sightMargin": "--sight-margin",
             "surfaceTol": "--surface-tol", "subjectHeight": "--subject-height", "subjectWidth": "--subject-width"}
    for k, flag in flags.items():
        if k in cam:
            argv += [flag, str(cam[k])]
    if "aim" in cam:
        argv += ["--aim=" + ",".join(f"{float(v):g}" for v in cam["aim"])]
    if "bearingWindow" in cam:
        argv += ["--bearing-window=" + ",".join(f"{float(v):g}" for v in cam["bearingWindow"])]
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
    want_kinds = {"nodepth" if points["byKey"][p["formKey"]].get("markKind") == "coverage" else "surface" for p in parts}
    if got != want or kinds != want_kinds:
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
    scoped = None
    if observed != expected:
        scoped = scene_identity(source, expected, state=request.get("state") if request.get("requestType") == "state-pair" else None)
        if scoped is None or scoped["sceneFingerprint"] != observed:
            raise Refused("configuration-mismatch",
                          f"the renderer read configuration {observed[:16]}… but this finding was "
                          f"measured in {expected[:16]}…"
                          + (f" (scene {scoped['sceneFingerprint'][:16]}… at {scoped['base']})" if scoped else "")
                          + " -- the camera was judged against a different scene than the request",
                          expectedFingerprint=expected, observedFingerprint=observed,
                          sceneFingerprint=(scoped or {}).get("sceneFingerprint"))

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
                            + shotplan_mod.aspect_flag(pose.get("aspect"))),
              # a cutaway's hide and protect flags travel WITH the camera: every state is rendered with them
              "viewKind": points["doc"].get("viewKind") or "eye-level",
              "viewFlags": list(points["doc"].get("viewFlags") or [])}
    sequence = request.get("sequence") or {}
    setups = sequence.get("setups") or {}
    states = [{"state": s, "poseId": plan["poseId"], "camera": camera, "setup": setups.get(s),
               "highlight": s == "highlight"}
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
                          "markKind": points["byKey"][p["formKey"]].get("markKind") or "surface",
                          "coverage": points["byKey"][p["formKey"]].get("coverage"),
                          "surfaceOf": points["byKey"][p["formKey"]].get("surfaceOf")}
                         for p in parts],
        "viewKind": points["doc"].get("viewKind") or "eye-level",
        "cutaway": points["doc"].get("cutaway"),
        "fallbackChain": points["doc"].get("fallbackChain") or [],
        "source": source,
        "suppliedPoints": {"path": points["path"], "sha256": points["sha256"],
                           "provenance": points["doc"].get("provenance"),
                           "cell": points["doc"].get("cell"), "floorZ": points["doc"].get("floorZ"),
                           "cameraConstraints": points["camera"]},
        "configuration": {
            "expected": expected, "observed": observed, "match": True,
            "populationScoped": scoped,
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
                       "identical across the request's sequence. The states differ only in the measured setup "
                       "rendered (without / with the evaluated mod) and the diagnostic highlight."),
        "visualQualification": {
            "status": "pending",
            "note": ("A verified camera is geometry, not qualified media. Subject recognition, "
                     "contact visibility and defect support are unreviewed here. Capture and "
                     "admission still run through the established workflow; nothing in this "
                     "result admits media."),
            "outstanding": (request.get("review") or {}).get("separateJudgments")},
        "receipts": receipts,
    }


def _vp_run(a, shotplan_mod, cell: str, flags: list[str], out: Path) -> tuple[int, str]:
    """One viewprobe render for the acquisition's own checks; its console is kept beside the frame."""
    out.mkdir(parents=True, exist_ok=True)
    cmd = shotplan_mod.vp_command(a.vp) + [cell, *[str(f) for f in flags], "--out", str(out), *a.passthrough]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = (p.stdout or "") + "\n--stderr--\n" + (p.stderr or "")
    (out / "console.txt").write_text(" ".join(cmd) + "\n" + text, encoding="utf-8")
    return p.returncode, text


COVERAGE_AT = re.compile(r"^coverage-at (\S{6}:.+?\.es[pml]) \S+: (?:pixel (\d+),(\d+) (in the mark, (\d+) px inside|outside the mark)|off the frame)")


def coverage_at_pose(a, result: dict, receipts_dir: Path, shotplan_mod) -> None:
    """For a participant marked by coverage, render the final pose and require its point to land in its own mark (at least
    2 px inside, the picker's rule). Fills participant.coverage.atPose, or refuses."""
    cov = [p for p in result.get("participants") or [] if p.get("markKind") == "coverage"]
    if not cov:
        return
    cam = result["camera"]
    flags = shlex.split(cam["viewprobe"]) + ["--size", "320", "--msaa", "0", "--nav", "off", "--report-mode",
                                              *(cam.get("viewFlags") or [])]
    for p in cov:
        x, y, z = p["point"]
        flags += ["--coverage-of", f"{p['formKey']}@{x:g},{y:g},{z:g}"]
    rc, text = _vp_run(a, shotplan_mod, cam["cell"], flags, receipts_dir / "coverage-at-pose")
    if rc != 0:
        raise Refused("points-renderer-failed", f"the coverage check at the final pose did not render (viewprobe exit {rc})")
    for p in cov:
        m = next((mm for mm in (COVERAGE_AT.match(ln) for ln in text.splitlines()) if mm and mm[1] == p["formKey"]), None)
        inside = int(m[5]) if m and m[5] else -1
        if m is None or inside < 2:
            raise Refused("see-through-not-covered-at-pose",
                          f"{p['formKey']}'s measured point does not land in its own coverage mark at the final camera "
                          f"({'no reading' if m is None else m.group(0).split(': ', 1)[1]}); no camera is published",
                          final=False)
        p["coverage"] = {**(p.get("coverage") or {}), "atPose": True, "insideAtPose": inside,
                         "atPoseReceipt": str(receipts_dir / "coverage-at-pose" / "console.txt")}


# A refusal raised after the picker returned a pose is one of two things, decided here and nowhere else: the pose was
# judged and refused (it enters the chain: the top-down is next), or no fallback can apply, and the refusal says why.
# A code missing from both is still refused, and says it was not classified.
POSE_REFUSED = {
    "planner-not-verified": lambda e: e.extra.get("plannerStatus") == "refused",
    "see-through-not-covered-at-pose": lambda e: True,
}
NO_FALLBACK = {
    "planner-not-verified": "the planner did not finish judging the pose; the request is captured again",
    "points-renderer-failed": "the renderer failed; the request is captured again",
    "planner-result-stale": "the planner's result belongs to another run; a fallback would read the same files",
    "plan-subject-mismatch": "the planner judged other targets; a fallback would be bound to the same wrong targets",
    "planner-inconsistent": "the planner contradicts itself; a fallback goes through the same planner",
    "configuration-identity-absent": "the renderer named no configuration; a fallback is checked against the same identity",
    "request-configuration-unidentified": "the request has no configuration to match; no fallback can be bound to it",
    "configuration-mismatch": "the renderer read another configuration; a fallback renders the same scene",
}


def after_pick_refusal(exc: Refused, points_doc: dict) -> dict | None:
    """The chain to hand the top-down when the picked pose was refused, else None, with the refusal's noFallback set."""
    test = POSE_REFUSED.get(exc.code)
    td = points_doc.get("topDown")
    if test and test(exc) and td:
        chain = list(points_doc.get("fallbackChain") or [])
        kind = points_doc.get("viewKind") or "eye-level"
        detail = f"the picked {kind} pose was refused: {exc.detail}"
        if chain and chain[-1].get("kind") == kind and chain[-1].get("status") == "ok":
            chain[-1] = {**chain[-1], "status": "refused", "code": exc.code, "detail": detail}
        else:
            chain.append({"kind": kind, "status": "refused", "code": exc.code, "detail": detail})
        if kind == "eye-level" and not any(c.get("kind") == "cutaway" for c in chain):
            # the picker tries a cutaway only when no station saw both; one did, so nothing stood in front to hide
            chain.append({"kind": "cutaway", "status": "skipped", "code": "cutaway-not-applicable",
                          "detail": "a station saw both participants, so nothing stood in front to hide"})
        return {"topDown": td, "chain": chain, "coverageStates": points_doc.get("coverageStates") or {}}
    if test and test(exc):
        why = "the picker gave no top-down inputs, so there is nothing to take a top-down of"
    else:
        why = NO_FALLBACK.get(exc.code) or f"{exc.code} is not classified for the fallback chain; no fallback was tried"
    exc.extra.setdefault("noFallback", why)
    return None


def acquire_top_down(a, request: dict, parts: list[dict], source: dict, receipts_dir: Path, shotplan_mod, exc) -> dict:
    """The labelled sliced top-down, when the eye-level picture and the cutaway both refused (or no station stood): the
    planner's own fallback shot (shotplan plan --top-down-only), accepted only when each participant shows at least
    TOP_DOWN_MIN_PIXELS of its own mark from above. Returns the camera-ready result, or raises a refusal that says every
    fallback was tried (final) -- unless a step failed for a reason that is not the geometry's (final False)."""
    import numpy as np
    from PIL import Image
    CP = _load_points()
    td = exc.extra["topDown"]
    chain = list(exc.extra.get("chain") or [])
    expected = request.get("configurationFingerprint")
    d = receipts_dir / "top-down"
    d.mkdir(parents=True, exist_ok=True)
    pj, wl = d / "shotplan-plan.json", d / "shotplan-worklist.json"
    shotplan_mod.claim_outputs([str(pj), str(wl)], request["id"])
    why = (f"{chain[0]['code'] if chain else exc.code}" + (f", then {chain[-1]['code']}" if len(chain) > 1 else ""))
    argv = [sys.executable, str(SHOTPLAN), "plan", td["cell"], "--floor-z", repr(float(td["floorZ"])),
            "--top-down-only", f"eye-level refused ({why})", "--json", str(pj), "--worklist", str(wl),
            "--label", f"capture request {request['id']} top-down", "--fov", "65", "--aspect", "16:9"]
    for s_ in td["subjects"]:
        x, y, z = s_["point"]
        argv.append(f"--subject={x:g},{y:g},{z:g}={s_['formKey'].replace(':', '-')[:18]}")
    if td.get("cut") is not None:
        argv += ["--fallback-cut", f"{float(td['cut']):g}"]
    if a.vp:
        argv += ["--vp", a.vp]
    if a.passthrough:
        argv += ["--"] + list(a.passthrough)
    call = call_planner(argv, d)
    doc = load_json(wl) if wl.is_file() else {}
    shot = next((x for c_ in doc.get("cells") or [] for x in c_.get("shots") or []
                 if isinstance(x, dict) and x.get("status") == "fallback"), None)
    if shot is None:
        chain.append({"kind": "top-down", "status": "failed", "code": "planner-not-verified",
                      "detail": f"the planner wrote no top-down shot (exit {call['exitCode']})"})
        raise Refused("planner-not-verified", f"the planner wrote no top-down shot (exit {call['exitCode']}); see "
                      f"{call['log']}", final=False, chain=chain)
    observed = (doc.get("scene") or {}).get("renderer")
    if observed != expected:
        scoped = scene_identity(source, expected, state=request.get("state") if request.get("requestType") == "state-pair" else None)
        if scoped is None or scoped["sceneFingerprint"] != observed:
            raise Refused("configuration-mismatch",
                          f"the top-down was planned in configuration {str(observed)[:16]}… but this finding was measured "
                          f"in {str(expected)[:16]}…", expectedFingerprint=expected, observedFingerprint=observed)
    cut = shot.get("cullAbove")
    rflags = [str(x) for x in doc.get("renderFlags") or []]
    aspect = shot.get("aspect") or 1.777778
    vpcam = (f"--eye {shot['eye'][0]:.1f},{shot['eye'][1]:.1f},{shot['eye'][2]:.1f} --yaw {float(shot.get('yaw', 0)):.1f} "
             f"--pitch {float(shot['pitch']):.1f} --fov {shot.get('fov', 65)}" + shotplan_mod.aspect_flag(aspect)
             + (f" --cull-above {float(cut):.1f}" if cut is not None else ""))
    nav = "wire" if "--nav-z" in rflags else "off"
    # the acceptance probe: each participant's own mark, seen from that camera
    see = set(td.get("seeThrough") or [])
    flags = shlex.split(vpcam) + ["--size", str(TOP_DOWN_PROBE_SIZE), "--msaa", "0", "--nav", "off", "--report-mode",
                                  *[x for p in parts for x in ("--ref", f"{p['formKey']}={p['color']}")], "--ref-surface",
                                  *[x for k in sorted(see) for x in ("--coverage-of", k)]]
    rc, text = _vp_run(a, shotplan_mod, td["cell"], flags, d / "probe")
    png = next(iter(sorted((d / "probe").glob("*-fp.png"))), None)
    if rc != 0 or png is None:
        chain.append({"kind": "top-down", "status": "failed", "code": "points-renderer-failed",
                      "detail": f"the top-down probe did not render (viewprobe exit {rc})"})
        raise Refused("points-renderer-failed", f"the top-down probe did not render (viewprobe exit {rc})", final=False,
                      chain=chain)
    img = np.asarray(Image.open(png).convert("RGB"))
    counts = {}
    for p in parts:
        mask, _cov = CP.participant_mask(img, png, p, p["formKey"] in see, text)
        counts[p["formKey"]] = int(mask.sum())
    ok, why_not = top_down_accepts(counts)
    if not ok:
        short = min(counts, key=lambda k: counts[k])
        states = sorted((exc.extra.get("coverageStates") or {}).get(short) or [])
        code = ("see-through-unmarked" if short in set(td.get("allNonOpaque") or []) and "marked" not in states
                else "top-down-participant-unseen")
        detail = (f"{why_not} (the view sliced above z {float(cut):.0f})" if cut is not None else why_not)
        if code == "see-through-unmarked":
            detail += (f"; it draws only alpha, effect or non-depth-writing shapes and its coverage never reached alpha "
                       f"0.25 in front of the scene from any station (states seen: {', '.join(states) or 'none'})")
        chain.append({"kind": "top-down", "status": "refused", "code": code, "detail": detail})
        raise Refused(code, detail + "; every fallback was tried: " + "; ".join(
            f"{c['kind']}: {c.get('code') or c['status']}" for c in chain), final=True, chain=chain,
            topDownPixels=counts, participant=short)
    chain.append({"kind": "top-down", "status": "ok", "code": None,
                  "detail": f"sliced above z {float(cut):.0f}" if cut is not None else "top-down"})
    camera = {"cell": td["cell"], "eye": list(shot["eye"]), "yaw": float(shot.get("yaw", 0)), "pitch": float(shot["pitch"]),
              "fov": shot.get("fov", 65), "aspect": aspect, "cullAbove": cut, "floorZ": td["floorZ"],
              "viewKind": "top-down", "nav": nav, "renderFlags": rflags, "viewFlags": [], "viewprobe": vpcam}
    seq = request.get("sequence") or {}
    return {"schemaVersion": 1, "kind": "capture-acquisition", "status": "camera-ready", "generatedUtc": utcnow(),
            "synthetic": False, "viewKind": "top-down",
            "request": {"id": request["id"], "requestType": request.get("requestType"), "checkId": request.get("checkId"),
                        "armId": request.get("armId"), "findingIds": request.get("findingIds"),
                        "location": request.get("location"), "recordKeys": request.get("recordKeys"),
                        "state": request.get("state"), "status": request.get("status")},
            "participants": [{**p, "markKind": "top-down", "pixelsFromAbove": counts[p["formKey"]]} for p in parts],
            "source": source, "poseId": shot.get("poseId"), "camera": camera, "cutaway": None,
            "fallbackChain": chain,
            "topDown": {"acceptance": {"minPixels": TOP_DOWN_MIN_PIXELS, "probeSize": TOP_DOWN_PROBE_SIZE, "counts": counts},
                        "subjects": td["subjects"], "cut": cut, "note": shot.get("note"), "label": shot.get("label")},
            "configuration": {"expected": expected, "observed": observed, "match": True},
            "states": [{"state": s_, "poseId": shot.get("poseId"), "camera": camera, "setup": (seq.get("setups") or {}).get(s_),
                        "highlight": s_ == "highlight"} for s_ in (seq.get("states") or [])],
            "sequence": seq,
            "visualQualification": {"status": "pending",
                                    "note": "a sliced top-down is a picture of the geometry, not a player viewpoint; it is "
                                            "labelled in the frame and reviewed like any other"},
            "receipts": {"receiptsDir": str(receipts_dir), "planJson": str(pj), "worklist": str(wl),
                         "plannerLog": call["log"], "probe": str(d / "probe" / "console.txt")}}


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
                                     "populationScoped": result["configuration"].get("populationScoped"),
                                     "worklist": result["receipts"].get("worklist"),
                                     "synthetic": result["synthetic"]},
                     "nextAction": ("The camera is planned and geometrically verified. Capture the "
                                    "request's states at this exact pose, then run the established "
                                    "visual review; this pose is not qualified media.")}
    return out


# ---------------------------------------------------------------- one frame's viewprobe argv --

def _strip_flag(args: list[str], flag: str) -> list[str]:
    out, i = [], 0
    while i < len(args):
        if args[i] == flag:
            i += 2
            continue
        out.append(args[i])
        i += 1
    return out


def capture_argv(request: dict, camera: dict, state: str, order: str, passthrough: list[str], size: int) -> list[str]:
    """The viewprobe arguments (after the executable, before --out) for one frame of a request.

    Every frame: the camera verbatim, --report-mode, no navmesh, no cell outline (exteriors draw it otherwise, and it reads
    as part of a building), and a world-state request's --state on every state. The highlight state marks the request's
    highlights (ghost: coloured --ref). A placement split's halves both mark the one participant -- an actor by --npc with
    its puck, an object by a cyan --ref -- on the split's half canvas."""
    cam = shlex.split(camera["viewprobe"])
    split = request.get("requestType") == "placement-split"
    if split:
        cam = _strip_flag(cam, "--aspect") + ["--aspect", SPLIT_ASPECT]
        size = SPLIT_SIZE
    argv = [camera["cell"], *passthrough, "--order", str(order), *cam, "--nav", camera.get("nav") or "off",
            "--size", str(size), "--msaa", "4", "--report-mode", "--no-cell-outline",
            # a top-down's navmesh band and grid; a cutaway's hide and protect flags (the protect check runs again in
            # this state's own setup)
            *[str(x) for x in camera.get("renderFlags") or []], *[str(x) for x in camera.get("viewFlags") or []]]
    if request.get("state") and request.get("requestType") == "state-pair":
        argv += ["--state", str(request["state"])]
    if split:
        fk = request["highlights"][0]["formKey"]
        argv += (["--npc", fk, "--npc-puck", "on"] if request.get("actor") else ["--ref", f"{fk}=cyan"])
        # context marks: a removal the split frames, marked only in the half where it still stands
        for m in request.get("marks") or []:
            if m.get("state") in (None, state):
                argv += ["--ref", f"{m['formKey']}={m.get('color') or 'magenta'}"]
        spot = (request.get("placements") or {}).get(state)
        if spot:
            # what stands under this half's placement: a downward probe 64 u up (parse_support reads it back)
            argv += ["--clear-at", f"{float(spot[0]):g},{float(spot[1]):g},{float(spot[2]) + 64:g}"]
    elif state == "highlight":
        for h in request.get("highlights") or []:
            argv += ["--ref", f"{h['formKey']}={h['color']}"]
        if (request.get("sequence") or {}).get("highlightMode") != "ghost":
            argv.append("--ref-surface")
    return argv


def parse_support(text: str, spot) -> dict | None:
    """A split half's downward probe, read back from its render's console: `down` the distance from the probe point
    (64 u above the placement) to the first geometry below it (None when nothing is within the probe's reach), `terrainAbove` when the first thing above is
    the terrain (an exterior placement under the ground), `interior` from the render's own cell line."""
    shot = _load_shotplan()
    probe = type("ProbeText", (), {"stdout": text})()
    rows = shot.Probe.clear(probe)
    key = (int(round(float(spot[0]))), int(round(float(spot[1]))), int(round(float(spot[2]) + 64)))
    e = rows.get(key)
    if e is None:
        return None
    down = (e["axes"].get("down") or {})
    up = (e["axes"].get("up") or {})
    interior = bool(re.search(r"^cell .*\(interior\)", text, re.M))
    return {"at": list(key), "down": down.get("dist"),          # from the probe point, 64 u above the placement
            "downHit": down.get("formkey"), "terrainAbove": up.get("formkey") == "000000:TERRAIN", "interior": interior}


def _cell_of(request: dict, side: str) -> str:
    cell = str((request.get("cells") or {}).get(side) or "")
    if not cell or cell.endswith("!persistent"):
        raise Refused("placement-no-cell",
                      f"the {side} placement's reference is filed in "
                      + ("a worldspace's persistent cell" if cell else "no cell the names enrichment read")
                      + ", so there is no cell to render it in")
    return cell


def _pose_cam(cell: str, eye, yaw, pitch, fov, cull=None) -> dict:
    vp = f"--eye {eye[0]:.1f},{eye[1]:.1f},{eye[2]:.1f} --yaw {float(yaw):.1f} --pitch {float(pitch):.1f} --fov {fov}"
    if cull is not None:
        vp += f" --cull-above {float(cull):.1f}"
    return {"cell": cell, "eye": [float(v) for v in eye], "yaw": float(yaw), "pitch": float(pitch), "fov": fov,
            "cullAbove": cull, "aspect": SPLIT_ASPECT, "viewprobe": vp}


def acquire_split(a, request: dict, source: dict, receipts_dir: Path, shotplan_mod) -> dict:
    """A placement split's camera, by the Gate A2 rule (Q5): one shared camera when the planner verifies one seeing both
    placements; otherwise the left placement's camera, reused by the right half moved by (right - left); when even that
    is refused, the left placement's labelled top-down fallback, moved the same way (the blind review decides whether it
    reads). Every plan runs on the prediction setup, so the camera is judged against the configuration the finding was
    measured in; the observed fingerprint must equal the request's."""
    left, right = (request.get("placements") or {}).get("left"), (request.get("placements") or {}).get("right")
    if not left or not right:
        raise Refused("placement-unknown", "the split names no left or right placement")
    cell_l, cell_r = _cell_of(request, "left"), _cell_of(request, "right")
    expected = request.get("configurationFingerprint")
    attempts = []

    def plan(name, cell, subjects):
        d = receipts_dir / name
        d.mkdir(parents=True, exist_ok=True)
        pj, wl = d / "shotplan-plan.json", d / "shotplan-worklist.json"
        shotplan_mod.claim_outputs([str(pj), str(wl)], request["id"])
        argv = [sys.executable, str(SHOTPLAN), "plan", cell]
        for (x, y, z), lab in subjects:
            argv.append(f"--subject={x:g},{y:g},{z:g}={lab}")
        argv += ["--fov", "65", "--aspect", SPLIT_ASPECT, "--json", str(pj), "--worklist", str(wl),
                 "--label", f"capture request {request['id']} {name}"]
        if a.vp:
            argv += ["--vp", a.vp]
        if a.passthrough:
            argv += ["--"] + list(a.passthrough)
        call = call_planner(argv, d)
        doc = load_json(pj) if pj.is_file() else {}
        wdoc = load_json(wl) if wl.is_file() else {}
        observed = ((doc.get("scene") or {}).get("renderer")) if isinstance(doc, dict) else None
        attempts.append({"name": name, "cell": cell, "exitCode": call["exitCode"], "status": (doc or {}).get("status"),
                         "observedFingerprint": observed, "planJson": str(pj), "worklist": str(wl)})
        return doc or {}, wdoc or {}, observed

    def verified_cam(doc, cell, observed):
        if doc.get("status") != "verified" or not (doc.get("chosen") or {}).get("ok"):
            return None
        if observed != expected:
            raise Refused("configuration-mismatch",
                          f"the renderer read configuration {str(observed)[:16]}… but this finding was measured in "
                          f"{str(expected)[:16]}…", expectedFingerprint=expected, observedFingerprint=observed)
        ch, pose = doc["chosen"], (doc.get("verification") or {}).get("pose") or {}
        return _pose_cam(cell, ch["eye"], ch["yaw"], ch["pitch"], pose.get("fov") or 65), doc.get("poseId")

    shift = [float(right[i]) - float(left[i]) for i in range(3)]
    if cell_l == cell_r:
        doc, _w, obs = plan("shared", cell_l, [(left, "left"), (right, "right")])
        got = verified_cam(doc, cell_l, obs)
        if got:
            cam, pid = got
            return {"mode": "shared", "left": cam, "right": {**cam, "cell": cell_r}, "poseIds": [pid, pid],
                    "shift": None, "attempts": attempts}
    doc, wdoc, obs = plan("left", cell_l, [(left, "left")])
    got = verified_cam(doc, cell_l, obs)
    mode = "relative"
    if got:
        cam, pid = got
    else:
        shots = [x for c_ in wdoc.get("cells") or [] for x in c_.get("shots") or []
                 if isinstance(x, dict) and x.get("status") == "fallback"]
        shot = shots[0] if shots and shots[0].get("eye") else None
        if shot is None:
            raise Refused("planner-not-verified", "no camera was verified for the left placement and the planner left no "
                          "top-down fallback", attempts=attempts)
        cam = _pose_cam(cell_l, shot["eye"], shot.get("yaw", 0), shot["pitch"], shot.get("fov", 65), shot.get("cullAbove"))
        pid, mode = shot.get("poseId"), "fallback"
    eye_r = [cam["eye"][i] + shift[i] for i in range(3)]
    cam_r = _pose_cam(cell_r, eye_r, cam["yaw"], cam["pitch"], cam["fov"],
                      None if cam.get("cullAbove") is None else cam["cullAbove"] + shift[2])
    return {"mode": mode, "left": cam, "right": cam_r, "poseIds": [pid, pid], "shift": shift, "attempts": attempts}


# ---------------------------------------------------------------- the command --


def physical_cell(parts: list[dict], bundle: Path) -> str | None:
    """The cell to picture a pair in when one participant is filed in its worldspace's persistent cell: a persistent
    exterior reference is drawn in the grid cell its position falls in, never in the persistent container, so the
    container cannot load both participants. The bundle's names enrichment records each reference's filing cell
    (`!persistent` marks the container); when the pair has exactly one non-persistent filing cell, that cell is the
    one to load. Otherwise None, and the request's own location stands (viewprobe then says what it cannot place)."""
    names = bundle / "enrichment" / "names" / "names.json"
    if not names.is_file():
        return None
    refs = (load_json(names) or {}).get("refs") or {}
    cells = [c for p in parts for c in ((refs.get(p["formKey"]) or {}).get("cells") or {}).values()]
    if not any(str(c).endswith("!persistent") for c in cells):
        return None
    physical = {str(c) for c in cells if not str(c).endswith("!persistent")}
    return physical.pop() if len(physical) == 1 else None

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
            support = (request.get("camera") or {}).get("support") or {}
            if support.get("supported") is False:
                raise Refused("request-type-unsupported", str(support.get("reason") or
                              f"the storyboard marks {request.get('requestType')!r} requests as having no camera path"))
            if request.get("requestType") not in SUPPORTED_REQUEST_TYPES:
                raise Refused("request-type-unsupported",
                              f"{request.get('requestType')!r} is not one of "
                              f"{', '.join(SUPPORTED_REQUEST_TYPES)}; this adapter plans a camera "
                              f"for a two-object pair and nothing else")
            parts = participants(request)
            bundle = Path(a.bundle).resolve() if a.bundle else packet_path.parent
            source = verify_source(request, bundle)
            if request.get("requestType") == "state-pair":
                # the camera is planned and verified in the state the clash was measured in, without the cell outline
                a.passthrough = list(a.passthrough) + ["--state", str(request["state"]), "--no-cell-outline"]
            if request.get("requestType") == "placement-split":
                split = acquire_split(a, request, source, receipts_dir, shotplan_mod)
                result = {"schemaVersion": 1, "kind": "capture-acquisition", "status": "camera-ready",
                          "generatedUtc": utcnow(), "synthetic": False,
                          "request": {"id": request["id"], "requestType": request.get("requestType"),
                                      "checkId": request.get("checkId"), "armId": request.get("armId"),
                                      "findingIds": request.get("findingIds"), "recordKeys": request.get("recordKeys")},
                          "source": source, "poseId": split["poseIds"][0], "camera": split["left"], "split": split,
                          "configuration": {"expected": request.get("configurationFingerprint"),
                                            "observed": next((x["observedFingerprint"] for x in split["attempts"]
                                                              if x["observedFingerprint"]), None),
                                            "match": split["mode"] != "fallback" or None},
                          "visualQualification": {"status": "pending",
                                                  "note": "a split's halves are reviewed blind; a fallback top-down that "
                                                          "does not read counts the clash as not pictured"},
                          "receipts": {"receiptsDir": str(receipts_dir), "worklist": None}}
                write_json(out, result)
                run.advance(1)
                run.artifact(str(out))
                run.tally(ok=1)
                print(f"camera-ready {a.request}: split {split['mode']}, left {split['left']['viewprobe']}")
                return EXIT_OK
            if a.points == "auto":
                CP = _load_points()
                try:
                    doc = CP.pick_points(request, vp=a.vp, passthrough=a.passthrough,
                                         work=receipts_dir / "points-work", cell=physical_cell(parts, bundle),
                                         synthetic=a.synthetic_points,
                                         size=a.points_size, grid=a.points_grid, shotplan=shotplan_mod)
                except CP.Unpickable as exc:
                    if exc.extra.get("topDown") is None:
                        raise Refused("points-unpickable", exc.detail, pickerCode=exc.code, **exc.extra)
                    # every eye-level picture refused, the cutaway too (or no station stood): the labelled top-down
                    result = acquire_top_down(a, request, parts, source, receipts_dir, shotplan_mod, exc)
                    write_json(out, result)
                    run.advance(1)
                    run.artifact(str(out))
                    run.tally(ok=1)
                    print(f"camera-ready {a.request}: TOP-DOWN, sliced above z {result['camera']['cullAbove']} -- not a "
                          f"player viewpoint; {result['camera']['viewprobe']}")
                    return EXIT_OK
                points_path = receipts_dir / "points.json"
                write_json(points_path, doc)
            else:
                points_path = Path(a.points).resolve()
            points = read_points(points_path, request, parts, source)

            # a cutaway's hide and protect flags ride on every render the planner makes, so it verifies the pose it publishes
            argv, _pj, _wl = plan_argv(points, parts, receipts_dir, a.vp,
                                       list(a.passthrough) + list(points["doc"].get("viewFlags") or []))
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
            try:
                result = build_result(request, parts, points, source, plan, call, receipts,
                                      shotplan_mod)
                coverage_at_pose(a, result, receipts_dir, shotplan_mod)
            except Refused as pexc:
                handed = after_pick_refusal(pexc, points["doc"])
                if handed is None:
                    raise
                handoff = type("PoseRefusal", (), {})()
                handoff.code, handoff.extra = pexc.code, handed
                result = acquire_top_down(a, request, parts, source, receipts_dir, shotplan_mod, handoff)
                write_json(out, result)
                run.advance(1)
                run.artifact(str(out))
                run.tally(ok=1)
                print(f"camera-ready {a.request}: TOP-DOWN after the picked pose was refused ({pexc.code}); "
                      f"{result['camera']['viewprobe']}")
                return EXIT_OK
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


VIEWPROBE = HERE / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
VIEW_LABEL = re.compile(r"^view-label: (.*?) \| rect (\d+),(\d+),(\d+),(\d+)\s*$")


def cmd_capture(a) -> int:
    with _run("capture-acquire", mode="capture", stage_source="counted") as run:
        run.stage("capture", total=1, unit="requests")
        code = _capture(a)
        run.advance(1)
        if code == EXIT_OK:
            run.tally(ok=1)
        else:
            run.refuse("the vignette capture refused; see capture-result.json", code)
        return code


def _capture(a) -> int:
    """Render one request's frames at its acquired camera and write the media items for each setup's bundle.

    The camera comes verbatim from the acquisition; nothing here composes or adjusts a pose. Each frame is one viewprobe
    process with the argv `capture_argv` builds:
      - a vignette (overlap or world state): `without` renders the setup without the evaluated mod (--without-order),
        `with` and `highlight` the prediction setup (--with-order); `highlight` adds the request's highlights; a world
        state renders every state in its switch's state. The replay pose each PNG reports must be identical across states.
      - a placement split: `left` renders the setup without the mod at the left camera, `right` the prediction at the
        right camera (the same camera when shared; moved by the placements' difference when relative, and then each
        half's media says so). Both halves mark the one participant.
    A refusal is a result: it is written to capture-result.json with its sentence, for evalreport plan --capture-results."""
    out = Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    result = {"schemaVersion": 1, "kind": "vignette-capture", "request": a.request, "status": "refused", "reason": None,
              "states": [], "generatedUtc": utcnow(), "toolIdentity": capture_tool_identity(a.vp)}

    def finish(code):
        write_json(out / "capture-result.json", result)
        print(f"{result['status']} {a.request}" + (f": {result['reason']}" if result.get("reason") else ""))
        return code
    try:
        acq = load_json(Path(a.acquisition).resolve())
        packet = load_json(Path(a.packet).resolve())
        request, _ = find_request(packet, a.request)
    except (OSError, ValueError, Refused) as exc:
        result["reason"] = f"the acquisition or the request could not be read: {exc}"
        return finish(EXIT_REFUSED)
    result["formKey"] = (request.get("recordKeys") or [None])[0]
    result["recordKeys"] = list(request.get("recordKeys") or [])     # which clash a refusal is about (reel planner)
    if acq.get("status") != "camera-ready" or (acq.get("request") or {}).get("id") not in (None, a.request):
        # the acquisition's own refusal travels with the result, so the planner and a reader see why (WRW capture)
        ref_ = acq.get("refusal") or {}
        result["refusal"] = {k: ref_.get(k) for k in ("code", "pickerCode", "participant", "detail", "chain")
                             if ref_.get(k)}
        if "final" in ref_:
            result["refusal"]["final"] = ref_["final"]
        result["reason"] = (f"the camera acquisition for {a.request} is {acq.get('status')!r}"
                            + (f" ({ref_.get('pickerCode') or ref_.get('code')}): {ref_.get('detail')}" if ref_ else ""))
        return finish(EXIT_REFUSED)
    seq = request.get("sequence") or {}
    states = list(seq.get("states") or [])
    vig = request.get("vignette") or {}
    split = request.get("requestType") == "placement-split"
    if split:
        sp = acq.get("split") or {}
        arms = {"left": vig.get("withoutArmId"), "right": request.get("armId")}
        orders = {"left": a.without_order, "right": a.with_order}
        cams = {"left": sp.get("left"), "right": sp.get("right")}
        if states != ["left", "right"] or not arms["left"] or not cams["left"] or not cams["right"]:
            result["reason"] = f"the split's sequence is {states} with cameras {sorted(k for k, v in cams.items() if v)}"
            return finish(EXIT_REFUSED)
    else:
        arms = {"without": vig.get("withoutArmId"), "with": request.get("armId"), "highlight": request.get("armId")}
        orders = {"without": a.without_order, "with": a.with_order, "highlight": a.with_order}
        cams = {s_: acq["camera"] for s_ in ("without", "with", "highlight")}
        if states != ["without", "with", "highlight"] or not arms["without"]:
            result["reason"] = (f"the request's sequence is {states} with setup without the mod {arms['without']!r}; "
                                f"this step captures the reel's without / with / highlight states of a vignette request")
            return finish(EXIT_REFUSED)
    vp = Path(a.vp) if a.vp else VIEWPROBE
    renderer = {n: sha256_file(vp.parent / n) for n in ("viewprobe.exe", "viewprobe.dll", "housecarl-core.dll")
                if (vp.parent / n).is_file()}
    media = {}
    poses = set()
    for state in states:
        d = out / state
        d.mkdir(parents=True, exist_ok=True)
        argv = ([str(vp)] + capture_argv(request, cams[state], state, str(Path(orders[state]).resolve()), a.passthrough, a.size)
                + ["--out", str(d)])
        r = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace")
        text = r.stdout + "\n--stderr--\n" + r.stderr
        (d / "console.txt").write_text(" ".join(argv) + "\n" + text, encoding="utf-8")
        pngs = sorted(d.glob("*.png"), key=lambda f: f.stat().st_mtime)
        fp = next((ln.split("capture input configuration:")[1].strip().split()[0] for ln in text.splitlines()
                   if "capture input configuration:" in ln), None)
        refusals = [ln.strip() for ln in text.splitlines() if "refused" in ln.lower()]
        label = next((m_ for m_ in (VIEW_LABEL.match(ln) for ln in text.splitlines()) if m_), None)
        row = {"state": state, "armId": arms[state], "exit": r.returncode, "png": str(pngs[-1]) if pngs else None,
               "sha256": sha256_file(pngs[-1]) if pngs else None, "configurationFingerprint": fp, "refusals": refusals,
               "hidden": [ln.strip() for ln in text.splitlines() if "HIDDEN" in ln], "argv": argv,
               # the capture fallbacks: what this state hid (each key with whether it was drawn in this setup), its label,
               # and the protect check's own words in this setup
               "hiddenObjects": [ln.split(":", 1)[1].strip() for ln in text.splitlines() if ln.startswith("hidden: ")],
               "viewLabel": label[1] if label else None,
               "protect": [ln.strip() for ln in text.splitlines() if ln.startswith("protect")]}
        result["states"].append(row)
        view_kind = (cams[state] or {}).get("viewKind") or "eye-level"
        if r.returncode == 0 and pngs and fp and view_kind != "eye-level" and not row["viewLabel"]:
            result["reason"] = f"the {state} state of a {view_kind} picture carries no view label; an unlabelled picture is refused"
            return finish(EXIT_REFUSED)
        if r.returncode != 0 or not pngs or not fp:
            result["reason"] = (f"viewprobe could not render the {state} state (exit {r.returncode}"
                                + (f"; {refusals[0]}" if refusals else "") + ("" if fp else "; no configuration line") + ")")
            return finish(EXIT_REFUSED)
        # the pose the renderer itself wrote into the PNG ("... | pose: ..."), and the pixel size it drew
        from PIL import Image
        with Image.open(pngs[-1]) as im:
            meta, size = str(im.info.get("viewprobe") or ""), im.size
        reported = meta.split("| pose:", 1)[1].split("|", 1)[0].strip() if "| pose:" in meta else None
        if not reported:
            result["reason"] = f"the {state} PNG carries no replay pose in its viewprobe metadata"
            return finish(EXIT_REFUSED)
        row["reportedPose"], row["size"] = reported, list(size)
        poses.add((reported, tuple(size)))
        side = next(iter(sorted(d.glob("*-configuration.json"))), None)
        overlays = ((load_json(side) or {}).get("Configuration") or {}).get("Overlays") if side else None
        relative = split and (acq.get("split") or {}).get("mode") in ("relative", "fallback")
        support = parse_support(text, (request.get("placements") or {}).get(state)) if split and             (request.get("placements") or {}).get(state) else None
        row["support"] = support
        media.setdefault(arms[state], []).append({
            "path": str(pngs[-1]), "mediaKind": "offline-render",
            "subject": {"checkId": request.get("checkId"), "armId": arms[state], "formKey": result["formKey"],
                        "description": f"{'split' if split else 'vignette'} {vig.get('chapterId')} {state}: request {a.request}"},
            "state": request.get("state") or "baseline", "configurationFingerprint": fp, "fingerprintSource": "emitted",
            "provenance": {"tool": "viewprobe", "capturedUtc": utcnow(), "argv": argv, "rendererHashes": renderer,
                           "captureRequest": a.request, "request": a.request,
                           "poseId": acq.get("poseId") if not relative else f"{acq.get('poseId')}:{state}",
                           "relativePose": bool(relative), "splitMode": (acq.get("split") or {}).get("mode") if split else None,
                           "support": support,
                           "vignetteState": state, "camera": cams[state], "configurationSidecar": str(side) if side else None,
                           "viewKind": (cams[state] or {}).get("viewKind") or "eye-level",
                           "hidden": [{"formKey": h.split()[0]} for h in row["hiddenObjects"] if "not drawn in this setup" not in h],
                           "viewLabel": row["viewLabel"], "cullAbove": (cams[state] or {}).get("cullAbove"),
                           "runtimeOverlayMods": sorted({str(o.get("Mod") or o.get("Provider") or o.get("Name"))
                                                         for o in overlays or [] if isinstance(o, dict)})}})
    shared = not split or (acq.get("split") or {}).get("mode") == "shared"
    if shared and len(poses) != 1:
        result["reason"] = "the states were not rendered at one pose and canvas"
        return finish(EXIT_REFUSED)
    if not shared and len({p_[1] for p_ in poses}) != 1:
        result["reason"] = "the split's halves were not rendered on one canvas"
        return finish(EXIT_REFUSED)
    for arm, items in media.items():
        write_json(out / f"media-{arm}.json", {"schemaVersion": 2, "items": items})
    result.update(viewKind=acq.get("viewKind") or (acq.get("camera") or {}).get("viewKind") or "eye-level",
                  hiddenByState={r_["state"]: r_.get("hiddenObjects") or [] for r_ in result["states"]})
    result.update(status="captured", reason=None, poseId=acq.get("poseId"),
                  splitMode=(acq.get("split") or {}).get("mode") if split else None,
                  media={arm: str(out / f"media-{arm}.json") for arm in media})
    return finish(EXIT_OK)


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
    p.add_argument("--points", required=True,
                   help="supplied measured feature points (see below), or `auto` to measure them with "
                        "capture_points.py first (written to <receipts>/points.json)")
    p.add_argument("--synthetic-points", action="store_true",
                   help="with --points auto: the renderer is a stand-in, so the measured points are labelled synthetic")
    p.add_argument("--points-size", type=int, default=320, help="with --points auto: witness frame size (default 320)")
    p.add_argument("--points-grid", type=int, default=10, help="with --points auto: pick every N px (default 10)")
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
    c = sub.add_parser("capture", help="render a request's frames at its acquired camera: a vignette's without / with / "
                                         "highlight states, or a placement split's left and right halves")
    c.add_argument("--acquisition", required=True, help="the camera-ready result of `acquire` for this request")
    c.add_argument("--packet", required=True, help="the storyboard packet holding the request")
    c.add_argument("--request", required=True)
    c.add_argument("--with-order", required=True, help="the prediction setup's candidate file (viewprobe --order)")
    c.add_argument("--without-order", required=True, help="the setup without the evaluated mod's candidate file")
    c.add_argument("--out", required=True, help="state folders, media-<arm>.json per setup, capture-result.json")
    c.add_argument("--size", type=int, default=1280)
    c.add_argument("--vp", default=None, help="viewprobe.exe (default: the production build)")
    c.set_defaults(fn=cmd_capture)
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
