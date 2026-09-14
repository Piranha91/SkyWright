"""Post-render qualification. Pixel eligibility never substitutes for a normal-view witness.

Caller-owned case policy states the detectable feature and signal size. Source records and
capture receipts bind participants; this module cannot infer an object from its colour.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import sys

import numpy as np
from PIL import Image

VERSION = "subject-contact-gate-v2"


def sha(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def camera_axes(camera):
    eye = np.asarray(camera["eye"], dtype=float)
    forward = np.asarray(camera["look"], dtype=float) - eye
    length = np.linalg.norm(forward)
    if not math.isfinite(length) or length <= 0:
        raise ValueError("invalid camera direction")
    forward /= length
    right = np.cross(forward, [0., 0., 1.])
    if np.linalg.norm(right) < 1e-8:
        raise ValueError("vertical camera has no declared roll")
    right /= np.linalg.norm(right)
    return eye, forward, right, np.cross(right, forward)


def project_box(camera, lo, hi, dimensions):
    """Conservative rectangle of an entirely forward box; no fabricated near-plane cap."""
    lo, hi = np.asarray(lo, dtype=float), np.asarray(hi, dtype=float)
    if lo.shape != (3,) or hi.shape != (3,) or not np.all(np.isfinite([lo, hi])) or np.any(hi < lo):
        raise ValueError("invalid measured envelope")
    width, height = dimensions
    fov = float(camera["fov"])
    if not 0 < fov < 180 or min(width, height) <= 0:
        raise ValueError("invalid projection")
    eye, forward, right, up = camera_axes(camera)
    points = np.array(list(itertools.product(*zip(lo, hi)))) - eye
    depth = points @ forward
    if np.any(depth <= 0):
        raise ValueError("envelope crosses or lies behind the camera plane")
    tangent = math.tan(math.radians(fov) / 2)
    x = width / 2 * (1 + (points @ right) / depth / tangent / (width / height))
    y = height / 2 * (1 - (points @ up) / depth / tangent)
    raw = [float(x.min()), float(y.min()), float(x.max()), float(y.max())]
    box = [max(0, math.floor(raw[0])), max(0, math.floor(raw[1])),
           min(width, math.ceil(raw[2])), min(height, math.ceil(raw[3]))]
    if box[2] <= box[0] or box[3] <= box[1]:
        raise ValueError("measured envelope is outside the frame")
    return {"rectangle": box, "unclippedRectangle": raw,
            "fullyFramed": raw[0] >= 0 and raw[1] >= 0 and raw[2] <= width and raw[3] <= height,
            "units": "original image pixels; origin upper left", "kind": "eligibility_region_not_object_mask"}


def frame_contact(camera, evidence, room_position_evidence):
    """Keep a verified interior eye; aim and expand FOV from all measured contact corners.

    Does not move an eye through a room shell or accept the planner's old OBND fallback.
    A source-backed existing interior position can be supplied instead of guessing a room.
    Qualification still requires post-render control and normal-view review.
    """
    if evidence.get("status") != "complete" or not evidence.get("value"):
        raise ValueError("complete measured contact envelope required")
    if room_position_evidence.get("status") != "verified" or not room_position_evidence.get("evidence"):
        raise ValueError("verified room position required; planner score is insufficient")
    value = evidence["value"]
    lo, hi = value.get("envelopeMin"), value.get("envelopeMax")
    if lo is None or hi is None:
        raise ValueError("contact envelope missing")
    result = dict(camera)
    result["look"] = ((np.asarray(lo) + np.asarray(hi)) / 2).tolist()
    eye, forward, right, up = camera_axes(result)
    points = np.array(list(itertools.product(*zip(lo, hi)))) - eye
    depth = points @ forward
    if np.any(depth <= 0):
        raise ValueError("contact straddles eye; choose another verified room position")
    # Retain the existing context FOV unless the measured box requires more space.
    extent = max(float(np.max(np.abs(points @ right) / depth)),
                 float(np.max(np.abs(points @ up) / depth)))
    result["fov"] = max(float(camera["fov"]), math.degrees(2 * math.atan(extent / .8)))
    if result["fov"] >= 120:
        raise ValueError("contact requires excessive FOV; choose another verified room position")
    result["planningStatus"] = "proposed_requires_post_render_gate"
    result["positionEvidence"] = room_position_evidence
    return result


def _record_present(record):
    fields = record.get("fields") or {}
    enable = fields.get("resolvedEnable") or {}
    return not fields.get("deletion") and enable.get("resolved") is True and enable.get("enabled") is True


def _verified_document(identity):
    if not identity.get("path") or not identity.get("sha256") or sha(identity["path"]) != identity["sha256"]:
        raise ValueError("source/receipt identity missing or changed")
    return json.loads(Path(identity["path"]).read_text(encoding="utf-8-sig"))


def _normalize_contact(value):
    if not isinstance(value, dict):
        return value
    return {key[:1].lower() + key[1:]: _normalize_contact(item) for key, item in value.items()}


def qualify(case):
    result = {"schemaVersion": VERSION, "caseId": case.get("caseId"), "pairKey": case.get("pairKey"),
              "captureStatus": "unassessable", "measurementStatus": "refused",
              "consequenceStatus": "not_assessed_by_gate", "reasons": [], "participants": {},
              "metrics": {}, "limits": ["A projected envelope is a locator, not a contact mask.",
                  "Qualification does not establish interference, repair, collision or gameplay."]}
    reasons = result["reasons"]
    try:
        captures = case["captures"]
        keys = [role + str(i) for role in ("normal", "a", "b", "elsewhere") for i in (1, 2)]
        images, hashes = {}, {}
        forms = {role: case[role]["formid"] for role in ("a", "b", "elsewhere")}
        if len(set(forms.values())) != 3:
            raise ValueError("elsewhere control must be a different reference")
        if "||".join(sorted([forms["a"], forms["b"]], key=str.casefold)) != case["pairKey"]:
            raise ValueError("participant identity does not match nominated pair")
        if not _record_present(case["elsewhere"]):
            raise ValueError("elsewhere reference is not source-verified present")
        negative = case.get("elsewhereEvidence") or {}
        if negative.get("status") != "verified" or not negative.get("basis"):
            raise ValueError("known-elsewhere placement not verified")
        if not case.get("sourceIdentity") or not case.get("rendererIdentity"):
            raise ValueError("source/renderer identity missing")
        source = _verified_document(case["sourceIdentity"])
        source_records = {r["formid"]: r for r in source["records"] if r.get("formid") in forms.values()}
        if any(source_records.get(forms[role]) != case[role] for role in forms):
            raise ValueError("participant record differs from bound source scene")
        negative_box = case["elsewhere"].get("geometry", {}).get("worldBounds")
        if not negative_box or case["elsewhere"]["fields"].get("space") != case["a"]["fields"].get("space"):
            raise ValueError("elsewhere control lacks same-space measured footprint")
        eye, forward, _, _ = camera_axes(case["camera"])
        negative_depth = (np.array(list(itertools.product(*zip(negative_box["min"], negative_box["max"])))) - eye) @ forward
        if not np.all(np.isfinite(negative_depth)) or not np.all(negative_depth < 0):
            raise ValueError("elsewhere control is not independently located behind the camera")
        if source["scope"]["configuration"]["Fingerprint"] != case["configurationFingerprint"]:
            raise ValueError("source scene configuration differs")
        for identity in case["rendererIdentity"]:
            if sha(identity["path"]) != identity["sha256"]:
                raise ValueError("renderer identity changed")
        environment = case["reviewEnvironment"]
        renderer = Path(environment["renderer"]).resolve()
        if renderer not in {Path(item["path"]).resolve() for item in case["rendererIdentity"]}:
            raise ValueError("actual renderer executable is not hash-bound")
        candidate = case["candidate"]
        if sha(candidate["candidatePath"]) != candidate["candidateSha256"]:
            raise ValueError("candidate identity changed")
        candidate_doc = json.loads(Path(candidate["candidatePath"]).read_text(encoding="utf-8-sig"))
        if not isinstance(candidate_doc.get("Mods"), list):
            raise ValueError("candidate lacks explicit asset-provider list")
        if candidate.get("configurationFingerprint") != case["configurationFingerprint"]:
            raise ValueError("candidate configuration differs")
        view_args = case["viewArgs"]
        if not isinstance(view_args, list) or not all(isinstance(x, str) for x in view_args):
            raise ValueError("explicit frozen rendering arguments required")
        # Optional material choices are frozen inputs. They cannot override acquisition,
        # placement or diagnostic identity, or remove scene geometry from the normal view.
        forbidden = {"--ref", "--ref-surface", "--eye", "--look", "--fov", "--size", "--out",
                     "--order", "--instance", "--profile", "--game", "--msaa", "--nav",
                     "--report-mode", "--cull-above", "--no-terrain", "--no-water",
                     "--no-coverings", "--no-ring-refs", "--markers", "--interactive"}
        if forbidden.intersection(view_args):
            raise ValueError("frozen rendering arguments override or remove required scene state")
        if case.get("contactEvidenceSource"):
            contact_source = _verified_document(case["contactEvidenceSource"])
            contact_configuration = contact_source.get("configuration") or {}
            if contact_configuration.get("fingerprint", contact_configuration.get("Fingerprint")) != case["configurationFingerprint"]:
                raise ValueError("contact source configuration differs")
        else:
            contact_source = source
        contact_rows = contact_source.get("pairs") if "pairs" in contact_source else contact_source.get("records", [])
        matching = [p for p in contact_rows if p.get("pairKey") == case["pairKey"]]
        if len(matching) != 1 or _normalize_contact(matching[0].get("contactEvidence")) != _normalize_contact(case.get("contactEvidence")):
            raise ValueError("contact evidence differs from bound pair source")
        for key in keys:
            capture = captures[key]
            role = key[:-1]
            expected = [] if role == "normal" else [forms[role]]
            if capture.get("highlightRefs") != expected or capture.get("highlightMode") != "surface":
                raise ValueError("wrong highlight identity or mode: " + key)
            if capture.get("configurationFingerprint") != case["configurationFingerprint"] or capture.get("camera") != case["camera"]:
                raise ValueError("capture configuration/camera mismatch: " + key)
            if capture.get("exitCode") != 0:
                raise ValueError("capture failed: " + key)
            command = capture["actualRendererArguments"]
            actual_refs = [command[i + 1].rsplit("=", 1)[0] for i, flag in enumerate(command[:-1]) if flag == "--ref"]
            if actual_refs != expected or command.count("--ref-surface") != 1:
                raise ValueError("actual capture invocation targets wrong identity/mode: " + key)
            for flag, values in (("--eye", case["camera"]["eye"]), ("--look", case["camera"]["look"])):
                if command.count(flag) != 1 or not np.allclose([float(v) for v in command[command.index(flag) + 1].split(",")], values, rtol=1e-10, atol=1e-9):
                    raise ValueError("actual invocation camera differs: " + key)
            for flag, value in (("--fov", case["camera"]["fov"]), ("--size", case["camera"]["size"])):
                if command.count(flag) != 1 or float(command[command.index(flag) + 1]) != value:
                    raise ValueError("actual invocation projection/canvas differs: " + key)
            vector = lambda values: ",".join(format(float(v), ".12g") for v in values)
            expected_command = [environment["renderer"], case["cell"], "--instance", environment["instance"],
                "--profile", environment["profile"], "--game", environment["game"],
                "--order", candidate["candidatePath"], "--report-mode", "--ref-surface",
                "--eye", vector(case["camera"]["eye"]), "--look", vector(case["camera"]["look"]),
                "--fov", command[command.index("--fov") + 1], "--size", command[command.index("--size") + 1],
                "--msaa", "0", "--nav", "off", "--out", str(Path(capture["image"]).parent)] + view_args
            for form in expected:
                expected_command += ["--ref", form + ("=magenta" if role == "b" else "=cyan")]
            if command != expected_command:
                raise ValueError("actual invocation differs from frozen source/render state: " + key)
            sidecar = _verified_document(capture["configurationSidecar"])
            if sidecar.get("Configuration", {}).get("Fingerprint") != case["configurationFingerprint"]:
                raise ValueError("actual capture sidecar configuration differs: " + key)
            log = capture["log"]
            if sha(log["path"]) != log["sha256"]:
                raise ValueError("capture log changed: " + key)
            log_text = Path(log["path"]).read_text(encoding="utf-8")
            job_argv = capture["job"]["argv"]
            if job_argv[-len(command):] != command or log_text.splitlines()[0] != "$ " + " ".join(job_argv):
                raise ValueError("capture invocation differs from run receipt: " + key)
            if "subject-surface-mode: exact placed opaque triangles" not in log_text:
                raise ValueError("capture did not establish surface diagnostic mode: " + key)
            if "subject-surface-refused:" in log_text and not capture.get("surfaceRefusals"):
                raise ValueError("capture omitted renderer surface refusal: " + key)
            if capture.get("surfaceRefusals"):
                reasons.append("incomplete opaque-surface identity coverage: " + key)
            path = Path(capture["image"])
            actual_sha = sha(path)
            if actual_sha != capture["imageSha256"]:
                raise ValueError("capture image identity changed: " + key)
            with Image.open(path) as image:
                images[key] = np.asarray(image.convert("RGB"))
            hashes[key] = actual_sha
        if len({image.shape for image in images.values()}) != 1:
            raise ValueError("capture dimensions differ")
        canvas = case["camera"]["size"]
        if not isinstance(canvas, int) or canvas <= 0 or images["normal1"].shape[:2] != (canvas, canvas):
            raise ValueError("actual raster dimensions differ from declared camera canvas")
        result["captureStatus"] = "usable"
        result["imageHashes"] = hashes
        normal = images["normal1"]
        height, width = normal.shape[:2]
        envelope = _normalize_contact(case.get("contactEvidence") or {})
        value = envelope.get("value") or {}
        if envelope.get("status") != "complete" or value.get("envelopeMin") is None or value.get("envelopeMax") is None:
            raise ValueError("complete contact envelope unavailable; contact view remains unassessable")
        region = project_box(case["camera"], value["envelopeMin"], value["envelopeMax"], (width, height))
        result["eligibilityRegion"] = region
        x0, y0, x1, y1 = region["rectangle"]
        roi = np.zeros((height, width), dtype=bool)
        roi[y0:y1, x0:x1] = True
        result["metrics"]["regionPixels"] = int(roi.sum())
        if not region["fullyFramed"]:
            reasons.append("contact envelope is clipped; full contact scope not qualified")
        for role in ("normal", "a", "b", "elsewhere"):
            delta = np.any(images[role + "1"] != images[role + "2"], axis=2)
            result["metrics"][role + "Repeat"] = {"changedInRegion": int((delta & roi).sum()),
                                                    "changedOutsideRegion": int((delta & ~roi).sum())}
            if np.any(delta & roi):
                reasons.append("unrelated or repeat variation in eligibility region: " + role)
        negative_delta = np.any(images["elsewhere1"] != normal, axis=2) | np.any(images["elsewhere2"] != images["normal2"], axis=2)
        result["metrics"]["elsewhereChangedPixels"] = int(negative_delta.sum())
        if negative_delta.any():
            reasons.append("known-elsewhere highlight changed the frame")
        policy = case.get("signalPolicy") or {}
        minimum = policy.get("minimumPixels", 0)
        if (not policy.get("feature") or not policy.get("basis") or not isinstance(minimum, (int, float))
                or not math.isfinite(minimum) or minimum < 2):
            raise ValueError("claim-specific detectable feature/sensitivity missing")
        result["signalPolicy"] = policy
        for role in ("a", "b"):
            present = _record_present(case[role])
            delta = np.any(images[role + "1"] != normal, axis=2)
            repeat_delta = np.any(images[role + "2"] != images["normal2"], axis=2)
            count = int((delta & repeat_delta & roi).sum())
            participant = {"formid": forms[role], "sourcePresent": present,
                           "changedPixels": int(delta.sum()), "repeatSupportedRegionPixels": count}
            result["participants"][role] = participant
            enable = case[role].get("fields", {}).get("resolvedEnable", {})
            if not present and not case[role].get("fields", {}).get("deletion") and enable.get("resolved") is not True:
                participant["status"] = "source_presence_unresolved"
                reasons.append("source participant presence is unresolved: " + role)
            elif not present:
                participant["status"] = "expected_absent_or_excluded"
                reasons.append("source participant absent/excluded; no two-participant contact view: " + role)
            elif not delta.any():
                participant["status"] = "no_visible_contribution"
                reasons.append("expected-present participant has zero response: " + role)
            elif count < policy["minimumPixels"]:
                participant["status"] = "insufficient_contact_region_signal"
                reasons.append("participant signal does not meet declared contact-region sensitivity: " + role)
            else:
                participant["status"] = "diagnostic_signal_only"
        review = case.get("normalViewReview") or {}
        if review.get("imageHashes") != hashes or not review.get("reviewer") or not review.get("featureDescription"):
            reasons.append("normal-view feature review missing or not bound to these images")
        elif review.get("contactExposure") != "exposed" or review.get("identity") != "supported":
            reasons.append("normal-view relevant contact/edge not exposed with supported identity")
        elif not review.get("controlEvidence"):
            reasons.append("discriminating visual controls not recorded")
        if not reasons:
            result["measurementStatus"] = "qualified"
            for participant in result["participants"].values():
                participant["status"] = "visible_contact_feature_supported"
    except (KeyError, TypeError, ValueError, OSError) as exc:
        reasons.append(str(exc))
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("output already exists; preserve previous qualification")
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
    from runlog import Run
    with Run("subject-gate", stage_source="counted") as run:
        case = json.loads(args.input.read_text(encoding="utf-8-sig"))
        result = qualify(case)
        result["input"] = {"path": str(args.input.resolve()), "sha256": sha(args.input)}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
        run.artifact(args.output)
        if result["measurementStatus"] != "qualified":
            run.refuse("; ".join(result["reasons"]))
            return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
