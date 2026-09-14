"""Configuration-correct attribution for overlapprobe scene manifests."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import sys
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402


POLICY = "configuration-scene-attribution-v1"
STATUSES = {"new", "inherited", "changed_existing", "absent_excluded", "unresolved"}
STATUS_PRECEDENCE = ("unresolved", "new", "changed_existing", "absent_excluded", "inherited")


def _aggregate_status(statuses: list[str]) -> str:
    """Return the conservative comparison aggregate without depending on input order."""
    return next(status for status in STATUS_PRECEDENCE if status in statuses)


def _record_map(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {r.get("formid"): r for r in manifest.get("records", []) if isinstance(r, dict) and r.get("formid")}


def _pair_map(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {p.get("pairKey"): p for p in manifest.get("pairs", []) if isinstance(p, dict) and p.get("pairKey")}


def _asset_map(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {a.get("assetId"): a for a in manifest.get("assets", []) if isinstance(a, dict) and a.get("assetId")}


def _setup_valid(manifest: dict[str, Any]) -> tuple[bool, list[str]]:
    reasons: list[str] = []
    scene = manifest.get("scene") or {}
    validity = scene.get("validity") or {}
    if validity.get("status") not in (None, "valid"):
        reasons.extend(validity.get("reasons") or ["scene validity is unresolved"])
    configuration = (manifest.get("scope") or {}).get("configuration") or {}
    if not configuration.get("Fingerprint") and not configuration.get("fingerprint"):
        reasons.append("configuration fingerprint is absent")
    providers = configuration.get("Providers", configuration.get("providers"))
    if providers is None:
        reasons.append("configuration asset providers are absent")
    if not scene.get("discovery"):
        reasons.append("explicit scene discovery receipt is absent")
    return not reasons, reasons


def _explicit_pair_scope(manifest: dict[str, Any], pair_key: str) -> bool:
    if pair_key in _pair_map(manifest):
        return True
    discovery = ((manifest.get("scene") or {}).get("discovery") or {})
    for receipt in discovery.get("importedScopes") or []:
        # Imported manifests retain their exact pair rows in producer output. A receipt alone
        # is accounting, never evidence that an unlisted pair was absent.
        if receipt.get("pairCount", 0) > 0:
            return False
    return False


def _object_snapshot(manifest: dict[str, Any], formid: str, records: dict[str, dict[str, Any]],
                     assets: dict[str, dict[str, Any]]) -> dict[str, Any]:
    record = records.get(formid)
    if record is None:
        return {"formid": formid, "status": "absent", "reason": "reference is absent from this explicit scene manifest"}
    fields = record.get("fields") or {}
    geometry = record.get("geometry") or {}
    asset = assets.get(geometry.get("assetId"))
    enable = fields.get("resolvedEnable") or {}
    eligibility = record.get("eligibility") or {}
    absent = bool(fields.get("deletion")) or enable.get("enabled") is False
    unresolved = enable.get("resolved") is False or eligibility.get("status") in {
        "state_unresolved", "bos_unresolved", "geometry_unmeasurable", "invalid_transform", "degenerate_geometry"
    }
    status = "unresolved" if unresolved else "absent" if absent else "present"
    return {
        "formid": formid, "status": status,
        "reason": eligibility.get("reason") if status != "present" else None,
        "record": {k: record.get(k) for k in ("type", "editorid", "name", "winner")},
        "base": fields.get("effectiveBase"), "recordBase": fields.get("recordBase"),
        "model": {"assetId": geometry.get("assetId"), "geometryStatus": geometry.get("status"),
                  "consumedSha256": asset.get("consumedSha256") if asset else None},
        "pose": {k: fields.get(k) for k in ("position", "rotation", "scale")},
        "place": {"cell": fields.get("cell"), "space": fields.get("space")},
        "state": {"deletion": fields.get("deletion"), "resolvedEnable": enable, "bos": fields.get("bos")},
        "provider": asset.get("winner") if asset else None,
        "responsiblePlugin": fields.get("responsiblePlugin"),
    }


def _effective(value: dict[str, Any]) -> dict[str, Any]:
    model = value.get("model") or {}
    return {"status": value.get("status"), "base": value.get("base"),
            "model": {"geometryStatus": model.get("geometryStatus"), "consumedSha256": model.get("consumedSha256")},
            "pose": value.get("pose"), "place": value.get("place"), "state": value.get("state")}


def _changes(before: dict[str, Any], after: dict[str, Any]) -> list[str]:
    old, new = _effective(before), _effective(after)
    return [p for p in old if old.get(p) != new.get(p)]


def _effective_relation(pair: dict[str, Any] | None) -> dict[str, Any]:
    relation = _relation(pair)
    return {k: relation.get(k) for k in ("status", "surfaceRelation", "containment", "intersects")}


def _relation(pair: dict[str, Any] | None) -> dict[str, Any]:
    if pair is None:
        return {"status": "absent", "reason": "pair row is absent from this explicit scene manifest",
                "surfaceRelation": None, "intersects": None}
    shape = pair.get("actualShape") or {}
    return {"status": shape.get("status", "unresolved"), "reason": shape.get("reason"),
            "surfaceRelation": shape.get("surfaceRelation"), "containment": shape.get("containment"),
            "intersects": shape.get("intersects"), "trianglePairsTested": shape.get("trianglePairsTested")}


def attribute(setups: dict[str, dict], combined: str, comparisons: list[str],
              candidate_plugins: list[str]) -> dict:
    if combined not in setups:
        raise ValueError(f"combined setup '{combined}' was not supplied")
    if not comparisons:
        raise ValueError("at least one comparison setup is required")
    comparison_ids = sorted(set(comparisons))
    missing = [s for s in comparison_ids if s not in setups]
    if missing:
        raise ValueError("comparison setup(s) not supplied: " + ", ".join(missing))

    setup_meta: dict[str, Any] = {}
    record_maps = {setup_id: _record_map(manifest) for setup_id, manifest in setups.items()}
    pair_maps = {setup_id: _pair_map(manifest) for setup_id, manifest in setups.items()}
    asset_maps = {setup_id: _asset_map(manifest) for setup_id, manifest in setups.items()}
    for setup_id, manifest in setups.items():
        valid, reasons = _setup_valid(manifest)
        scene = manifest.get("scene") or {}
        config = (manifest.get("scope") or {}).get("configuration")
        logical_manifest = {k: v for k, v in manifest.items() if not k.startswith("_source")}
        logical_sha = hashlib.sha256(json.dumps(logical_manifest, sort_keys=True, separators=(",", ":"),
            allow_nan=False).encode("utf-8")).hexdigest()
        setup_meta[setup_id] = {
            "setupId": setup_id, "role": scene.get("role"), "validity": "valid" if valid else "unresolved",
            "reasons": reasons, "configuration": config, "limits": (config or {}).get("Limits", (config or {}).get("limits", [])),
            "manifestPath": manifest.get("_sourcePath"),
            "manifestSha256": manifest.get("_sourceSha256") or logical_sha,
            "manifestHashKind": "source-bytes" if manifest.get("_sourceSha256") else "canonical-json",
            "scene": scene,
        }

    after_manifest = setups[combined]
    after_pairs = pair_maps[combined]
    all_keys = set(after_pairs)
    for setup_id in comparison_ids:
        all_keys.update(pair_maps[setup_id])

    output = copy.deepcopy(after_manifest)
    output["triageSchemaVersion"] = "overlap-triage-v1"
    output["setups"] = setup_meta
    output["attributionPolicy"] = {
        "policy": POLICY, "combinedSetupId": combined, "comparisonSetupIds": comparison_ids,
        "candidatePlugins": sorted(set(candidate_plugins), key=str.casefold), "directionIndependent": True,
        "statuses": sorted(STATUSES),
        "absenceRule": "absence requires a valid explicit scene whose retained union contains the pair; unknown never means absent",
    }
    rows: list[dict[str, Any]] = []
    counts = {s: 0 for s in STATUSES}
    for pair_key in sorted(all_keys):
        template = after_pairs.get(pair_key)
        if template is None:
            template = next(pair_maps[s][pair_key] for s in comparison_ids if pair_key in pair_maps[s])
            template = copy.deepcopy(template)
            template["actualShape"] = {"status": "unknown", "reason": "pair is absent from the combined retained union",
                "surfaceRelation": "unknown", "containment": "unknown", "intersects": None, "trianglePairsTested": 0}
            template["discovery"] = {"status": "actual_surface_unknown", "reason": "pair is absent from the combined retained union"}
        row = copy.deepcopy(template)
        a, b = row.get("a"), row.get("b")
        comparisons_out: list[dict[str, Any]] = []
        statuses: list[str] = []
        reasons: list[str] = []
        for before_id in comparison_ids:
            before_manifest = setups[before_id]
            before_pair = pair_maps[before_id].get(pair_key)
            after_pair = after_pairs.get(pair_key)
            before_objects = [_object_snapshot(before_manifest, a, record_maps[before_id], asset_maps[before_id]),
                              _object_snapshot(before_manifest, b, record_maps[before_id], asset_maps[before_id])]
            after_objects = [_object_snapshot(after_manifest, a, record_maps[combined], asset_maps[combined]),
                             _object_snapshot(after_manifest, b, record_maps[combined], asset_maps[combined])]
            changed = {formid: _changes(old, new) for formid, old, new in zip((a, b), before_objects, after_objects)}
            before_valid = setup_meta[before_id]["validity"] == "valid"
            after_valid = setup_meta[combined]["validity"] == "valid"
            before_absent = any(o["status"] == "absent" for o in before_objects)
            after_absent = after_pair is not None and any(o["status"] == "absent" for o in after_objects)
            before_missing = before_pair is None
            after_missing = after_pair is None
            before_predicate = ((before_manifest.get("scope") or {}).get("predicate") or {})
            after_predicate = ((after_manifest.get("scope") or {}).get("predicate") or {})
            before_policy = (before_manifest.get("readerVersion"), before_manifest.get("policyVersion"),
                json.dumps(before_predicate, sort_keys=True, separators=(",", ":")))
            after_policy = (after_manifest.get("readerVersion"), after_manifest.get("policyVersion"),
                json.dumps(after_predicate, sort_keys=True, separators=(",", ":")))
            policy_mismatch = (not before_predicate or not after_predicate or before_policy != after_policy
                or any(v is None for v in before_policy + after_policy))
            hard_unresolved = (not before_valid or not after_valid or after_missing or policy_mismatch
                or any(o["status"] == "unresolved" for o in before_objects + after_objects))
            relation_unresolved = ((before_pair is not None and _relation(before_pair)["intersects"] is None and not before_absent)
                or (after_pair is not None and _relation(after_pair)["intersects"] is None and not after_absent))
            if hard_unresolved:
                status = "unresolved"; reason = ("reader/predicate/tolerance/limit identities do not match"
                    if policy_mismatch else "a setup, object, or retained union row is unresolved")
            elif after_absent:
                status = "absent_excluded"; reason = "the pair or one object is explicitly absent/excluded in the combined scene"
            elif before_absent or before_missing:
                status = "new"; reason = "the complete source scene explicitly lacks a pair/object present in the combined scene"
            elif relation_unresolved:
                status = "unresolved"; reason = "a measured relation is unresolved"
            elif any(changed.values()) or _effective_relation(before_pair) != _effective_relation(after_pair):
                status = "changed_existing"; reason = "both objects were compared in both scenes and an effective object or relation changed"
            else:
                status = "inherited"; reason = "both objects and their measured relation are unchanged from this source scene"
            statuses.append(status); reasons.append(f"{before_id}: {reason}")
            comparisons_out.append({
                "beforeSetupId": before_id, "afterSetupId": combined, "status": status, "reason": reason,
                "before": {"objects": before_objects, "relation": _relation(before_pair)},
                "after": {"objects": after_objects, "relation": _relation(after_pair)},
                "changedFields": changed,
            })
        status = _aggregate_status(statuses)
        produced_narrow = row.get("oldNarrowVerdict") or {}
        legacy_origin = produced_narrow.get("origin")
        legacy_known = produced_narrow.get("status") == "measured" and legacy_origin in {"introduced", "inherited"}
        if not legacy_known:
            narrow_status = "unavailable"
            agreement = None
            narrow_reason = ("the producer has no resolved Q46 narrow counterfactual for this pair; "
                "newCandidate is not used as a proxy")
        elif status == "unresolved":
            narrow_status = "unresolved"
            agreement = None
            narrow_reason = ("complete-scene attribution is unresolved, so agreement with the resolved "
                "Q46 narrow counterfactual cannot be evaluated")
        else:
            narrow_status = legacy_origin
            agreement = ((legacy_origin == "introduced" and status == "new")
                or (legacy_origin == "inherited" and status == "inherited"))
            narrow_reason = "Q46 JudgeSurface previous-placement counterfactual compared with complete-scene attribution"
        row["attribution"] = {"status": status, "comparisons": comparisons_out, "reasons": reasons,
            "candidatePlugins": sorted(set(candidate_plugins), key=str.casefold),
            "candidateEffectiveChange": any(c["status"] in ("new", "changed_existing", "absent_excluded") for c in comparisons_out),
            "oldNarrowVerdict": {"status": narrow_status, "agreement": agreement,
                "producer": produced_narrow or None,
                "reason": narrow_reason}}
        counts[status] += 1
        rows.append(row)
    output["pairs"] = rows
    existing_accounting = copy.deepcopy(output.get("accounting") or {})
    if "measurement" not in existing_accounting:
        existing_accounting["measurement"] = copy.deepcopy(output.get("measurementAccounting") or {})
    existing_accounting["attribution"] = {"pairCount": len(rows), "attributionCounts": counts,
        "candidateEffectivePairCount": sum(bool((p.get("attribution") or {}).get("candidateEffectiveChange")) for p in rows),
        "candidateChangedReferences": sorted({formid for p in rows for c in p["attribution"]["comparisons"]
            for formid, fields in c["changedFields"].items() if fields}),
        "setupCount": len(setups), "comparisonCount": len(comparison_ids),
        "complete": counts["unresolved"] == 0,
        "notes": [] if counts["unresolved"] == 0 else ["Unresolved rows are retained and are not counted as absent or separated."]}
    output["accounting"] = existing_accounting
    return output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--setup", action="append", required=True, metavar="ID=PATH")
    parser.add_argument("--combined", required=True)
    parser.add_argument("--compare", action="append", required=True)
    parser.add_argument("--candidate", action="append", default=[])
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    with Run("overlap-attribution", stage_source="counted") as run:
        run.stage("attribute", total=1, unit="manifests")
        setups: dict[str, dict[str, Any]] = {}
        for spec in args.setup:
            if "=" not in spec:
                parser.error("--setup must be ID=PATH")
            setup_id, raw_path = spec.split("=", 1)
            if not setup_id or setup_id in setups:
                parser.error(f"setup id is empty or duplicated: {setup_id!r}")
            path = Path(raw_path).resolve()
            data = path.read_bytes()
            manifest = json.loads(data)
            manifest["_sourcePath"] = str(path)
            manifest["_sourceSha256"] = hashlib.sha256(data).hexdigest()
            setups[setup_id] = manifest
            run.artifact(str(path))
        result = attribute(setups, args.combined, args.compare, args.candidate)
        Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        counts = result["accounting"]["attribution"]
        run.feature("setups", counts["setupCount"], "setups").feature("pairs", counts["pairCount"], "pairs")
        run.feature("unresolved", counts["attributionCounts"]["unresolved"], "pairs")
        run.artifact(str(Path(args.output).resolve())); run.advance(1)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
