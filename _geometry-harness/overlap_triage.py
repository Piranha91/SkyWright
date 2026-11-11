#!/usr/bin/env python
"""Add generic overlap features, analogues, ranking and local incidents to a canonical manifest."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402
DEFAULT_POLICY = HERE.parent / ".claude/skills/world-edit-compatibility/references/overlap-triage-policy.json"
SCHEMA = "overlap-triage-v1"


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _digest(value):
    return hashlib.sha256(_canonical(value).encode("utf-8")).hexdigest()


def _file_digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _feature(status, value=None, reason=None, **source):
    result = {"status": status, "value": value, "reason": reason}
    result.update(source)
    return result


def _valid_contact_value(value, tested):
    if not isinstance(value, dict) or not isinstance(tested, int) or tested < 0:
        return False
    names = ("CrossingPairs", "CoplanarPairs", "TouchingPairs", "ParticipatingTrianglesA", "ParticipatingTrianglesB")
    if any(not isinstance(value.get(k), int) or value[k] < 0 for k in names):
        return False
    contacts = sum(value[k] for k in names[:3])
    if contacts > tested:
        return False
    low, high = value.get("EnvelopeMin"), value.get("EnvelopeMax")
    if contacts == 0:
        return value["ParticipatingTrianglesA"] == 0 and value["ParticipatingTrianglesB"] == 0 and low is None and high is None
    if value["ParticipatingTrianglesA"] <= 0 or value["ParticipatingTrianglesB"] <= 0:
        return False
    if not (isinstance(low, list) and isinstance(high, list) and len(low) == 3 and len(high) == 3):
        return False
    values = [float(x) for x in low + high]
    return all(math.isfinite(x) for x in values) and all(values[i] <= values[i + 3] for i in range(3))


def _normalize_contact_value(value):
    if not isinstance(value, dict):
        return value
    names = ("CrossingPairs", "CoplanarPairs", "TouchingPairs", "ParticipatingTrianglesA",
             "ParticipatingTrianglesB", "EnvelopeMin", "EnvelopeMax")
    return {name: value.get(name) if name in value else value.get(name[0].lower() + name[1:]) for name in names}


def _vec_distance(a, b):
    if not (isinstance(a, list) and isinstance(b, list) and len(a) >= 3 and len(b) >= 3):
        return None
    values = [float(x) for x in a[:3] + b[:3]]
    if not all(math.isfinite(x) for x in values):
        return None
    return math.sqrt(sum((values[i] - values[i + 3]) ** 2 for i in range(3)))


def _angle_distance(a, b):
    if not (isinstance(a, list) and isinstance(b, list) and len(a) >= 3 and len(b) >= 3):
        return None
    values = [float(x) for x in a[:3] + b[:3]]
    if not all(math.isfinite(x) for x in values):
        return None
    distances = []
    for i in range(3):
        d = abs(values[i] - values[i + 3]) % (2 * math.pi)
        distances.append(min(d, 2 * math.pi - d))
    return math.sqrt(sum(x * x for x in distances))


def _mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def _transpose(a):
    return [[a[j][i] for j in range(3)] for i in range(3)]


def _row_mul(v, matrix):
    return [sum(v[k] * matrix[k][j] for k in range(3)) for j in range(3)]


def _refr_rotation(rotation):
    if not (isinstance(rotation, list) and len(rotation) >= 3):
        return None
    x, y, z = (-float(rotation[0]), -float(rotation[1]), -float(rotation[2]))
    if not all(math.isfinite(v) for v in (x, y, z)):
        return None
    rx = [[1, 0, 0], [0, math.cos(x), -math.sin(x)], [0, math.sin(x), math.cos(x)]]
    ry = [[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]]
    rz = [[math.cos(z), -math.sin(z), 0], [math.sin(z), math.cos(z), 0], [0, 0, 1]]
    # SpatialOverlap.RefrRotation in row form (v * M, like Vector3.Transform): the transpose of Rx(-x)*Ry(-y)*Rz(-z)
    return _transpose(_mat_mul(_mat_mul(rx, ry), rz))


def _relative_transform(first, second):
    pa, pb = first.get("position"), second.get("position")
    ra, rb = _refr_rotation(first.get("rotation")), _refr_rotation(second.get("rotation"))
    sa, sb = first.get("scale"), second.get("scale")
    if (_vec_distance(pa, pb) is None or ra is None or rb is None or
            not isinstance(sa, (int, float)) or not isinstance(sb, (int, float)) or
            not math.isfinite(float(sa)) or not math.isfinite(float(sb))):
        return None
    delta_world = [float(pb[i]) - float(pa[i]) for i in range(3)]
    local_offset = _row_mul(delta_world, _transpose(ra))
    relative_rotation = _mat_mul(rb, _transpose(ra))
    trace = sum(relative_rotation[i][i] for i in range(3))
    orientation_angle = math.acos(max(-1.0, min(1.0, (trace - 1.0) / 2.0)))
    return {"localOffset": local_offset, "relativeOrientation": relative_rotation,
            "orientationAngleRadians": orientation_angle,
            "firstScale": float(sa), "secondScale": float(sb)}


def _transform_difference(target, source):
    if target is None or source is None:
        return None
    offset = _vec_distance(target["localOffset"], source["localOffset"])
    orientation_delta_matrix = _mat_mul(target["relativeOrientation"], _transpose(source["relativeOrientation"]))
    trace = sum(orientation_delta_matrix[i][i] for i in range(3))
    orientation = math.acos(max(-1.0, min(1.0, (trace - 1.0) / 2.0)))
    scales = []
    for key in ("firstScale", "secondScale"):
        denom = max(abs(target[key]), abs(source[key]), 1e-9)
        scales.append(abs(target[key] - source[key]) / denom)
    return {"localOffsetDistance": offset, "orientationDeltaRadians": orientation,
            "maximumRelativeScaleDelta": max(scales)}


def _identity_token(identity, kind, policy):
    if kind == "exact_mesh_pair":
        return identity.get("meshContentSha256")
    if kind == "mesh_family_pair":
        return _mesh_family(identity, policy)
    return identity.get("baseFormKey")


def _record_indexes(manifest):
    records = {str(r.get("formid")): r for r in manifest.get("records", []) if r.get("formid")}
    assets = {str(a.get("assetId")): a for a in manifest.get("assets", []) if a.get("assetId")}
    return records, assets


def _identity(record, assets):
    fields = record.get("fields") or {}
    geom = record.get("geometry") or {}
    asset = assets.get(str(geom.get("assetId")), {})
    base = fields.get("effectiveBase") or fields.get("recordBase") or {}
    return {
        "formKey": record.get("formid"), "type": record.get("type"), "name": record.get("name"),
        "editorId": record.get("editorid"), "winner": record.get("winner"),
        "responsiblePlugin": fields.get("responsiblePlugin"), "baseFormKey": base.get("formid"),
        "mesh": asset.get("relativePath"), "meshContentSha256": asset.get("consumedSha256"),
        "assetId": geom.get("assetId"),
        "position": fields.get("position"), "rotation": fields.get("rotation"), "scale": fields.get("scale"),
        "cell": fields.get("cell"), "space": fields.get("space")
    }


def _pair_feature(pair, ra, rb, assets, policy):
    ia, ib = _identity(ra, assets), _identity(rb, assets)
    distance = _vec_distance(ia["position"], ib["position"])
    relative_transform = _relative_transform(ia, ib)
    angle = relative_transform.get("orientationAngleRadians") if relative_transform else None
    scale_delta = None
    if isinstance(ia["scale"], (int, float)) and isinstance(ib["scale"], (int, float)):
        sa, sb = float(ia["scale"]), float(ib["scale"])
        if math.isfinite(sa) and math.isfinite(sb):
            denom = max(abs(sa), abs(sb), 1e-9)
            scale_delta = abs(sa - sb) / denom
    pose_ok = distance is not None and angle is not None and scale_delta is not None
    pose = _feature("measured" if pose_ok else "unsupported",
                    {"distance": distance, "localOffsetInFirstMeshFrame": relative_transform.get("localOffset"),
                     "orientationDistanceRadians": angle, "relativeScaleDelta": scale_delta,
                     "similar": bool(pose_ok and distance <= policy["pose"]["positionScale"] and
                                     angle <= policy["pose"]["rotationScaleRadians"] and
                                     scale_delta <= policy["pose"]["scaleRelativeTolerance"])} if pose_ok else None,
                    None if pose_ok else "both records do not provide finite position, rotation and scale",
                    source="record placement fields; RefrRotation=Rx(-x)*Ry(-y)*Rz(-z)")
    same_mesh = bool(ia["meshContentSha256"] and ib["meshContentSha256"] and
                     ia["meshContentSha256"] == ib["meshContentSha256"])
    same_base = bool(ia["baseFormKey"] and ia["baseFormKey"] == ib["baseFormKey"])
    mesh = _feature("measured" if ia["meshContentSha256"] and ib["meshContentSha256"] else "unsupported",
                    {"sameMesh": same_mesh, "sameBase": same_base,
                     "aMesh": ia["mesh"], "bMesh": ib["mesh"],
                     "aContentSha256": ia["meshContentSha256"], "bContentSha256": ib["meshContentSha256"]}
                    if ia["meshContentSha256"] and ib["meshContentSha256"] else None,
                    None if ia["meshContentSha256"] and ib["meshContentSha256"] else "both consumed mesh content hashes are not available",
                    source="consumed asset identities")
    actual = pair.get("actualShape") or {}
    extent = _feature("unsupported", None,
                      "exact actual-triangle intersection extent or volume is not measured; contact support envelopes are a different quantity",
                      source="actualShape")
    contact = pair.get("contactEvidence") or {}
    contact_value = _normalize_contact_value(contact.get("Value") if "Value" in contact else contact.get("value"))
    contact_partial = _normalize_contact_value(contact.get("PartialValue") if "PartialValue" in contact else contact.get("partialValue"))
    contact_status = str(contact.get("Status") or contact.get("status") or "unknown").lower()
    contact_policy = contact.get("PolicyVersion") or contact.get("policyVersion")
    contact_reason = contact.get("Reason") or contact.get("reason")
    contact_tested = contact.get("TrianglePairsTested") if "TrianglePairsTested" in contact else contact.get("trianglePairsTested")
    if (contact_status == "complete" and contact_policy == "contact_triangle_evidence_envelope_v1" and
            _valid_contact_value(contact_value, contact_tested)):
        contact_feature = _feature("measured", contact_value, contact_reason, quantity="classified-contact support envelope",
                                   trianglePairsTested=contact_tested,
                                   policyVersion=contact_policy,
                                   limits=["This is the envelope of all classified-contact triangle support, not exact intersection extent or volume."])
    elif contact_status == "incomplete" and _valid_contact_value(contact_partial, contact_tested):
        contact_feature = _feature("partial", contact_partial, contact_reason or "contact evidence is incomplete",
                                   quantity="partial classified-contact support envelope",
                                   trianglePairsTested=contact_tested,
                                   policyVersion=contact_policy,
                                   limits=["Partial support cannot establish the full contact location or lower review priority."])
    else:
        invalid_reason = "contact evidence violates count or finite-envelope invariants" if contact_value is not None or contact_partial is not None else None
        contact_feature = _feature("unsupported", None, invalid_reason or contact_reason or "complete classified-contact triangle evidence was not supplied",
                                   quantity="classified-contact support envelope", policyVersion=contact_policy)
    functional = []
    for side, record in (("a", ra), ("b", rb)):
        typ = str((((record.get("fields") or {}).get("effectiveBase") or {}).get("type")) or "")
        if typ in policy["ranking"]["functionalPriority"]:
            functional.append({"side": side, "type": typ, "formKey": record.get("formid")})
    return {"poseSimilarity": pose, "meshSimilarity": mesh, "actualIntersectionExtent": extent,
            "contactEvidenceEnvelope": contact_feature,
            "floorRelativeHeight": _feature("unsupported", None, "no identity-bound floor-context receipt was supplied"),
            "winningNavmeshDistance": _feature("unsupported", None, "no identity-bound winning-navmesh receipt was supplied"),
            "questContext": _feature("unsupported", None, "no identity-bound quest-target or linked-record receipt was supplied"),
            "functionalContext": _feature("measured", functional, None, source="record types")}, ia, ib


def _mesh_family(identity, policy):
    content, path = identity.get("meshContentSha256"), str(identity.get("mesh") or "").casefold()
    for family, declaration in sorted((policy.get("meshFamilies") or {}).items()):
        if content in (declaration.get("contentSha256") or []) or path in [str(x).casefold() for x in declaration.get("paths") or []]:
            return family
    return None


def _analogue_keys(identity_a, identity_b, policy):
    meshes = tuple(sorted(x for x in (identity_a.get("meshContentSha256"), identity_b.get("meshContentSha256")) if x))
    bases = tuple(sorted(x for x in (identity_a.get("baseFormKey"), identity_b.get("baseFormKey")) if x))
    families = tuple(sorted(x for x in (_mesh_family(identity_a, policy), _mesh_family(identity_b, policy)) if x))
    return (("exact_mesh_pair", meshes) if len(meshes) == 2 else None,
            ("mesh_family_pair", families) if len(families) == 2 else None,
            ("exact_base_pair", bases) if len(bases) == 2 else None)


def _analogue_index(all_pairs, records, assets, setup_id, source=None, source_role=None, policy=None):
    index = {}
    for other in all_pairs:
        oa, ob = records.get(str(other.get("a"))), records.get(str(other.get("b")))
        if not oa or not ob:
            continue
        oi = [_identity(oa, assets), _identity(ob, assets)]
        actual = other.get("actualShape") or {}
        source_contact = other.get("contactEvidence") or {}
        source_contact_status = str(source_contact.get("Status") or source_contact.get("status") or "").lower()
        source_contact_policy = source_contact.get("PolicyVersion") or source_contact.get("policyVersion")
        source_contact_value = _normalize_contact_value(
            source_contact.get("Value") if "Value" in source_contact else source_contact.get("value"))
        source_contact_tested = (source_contact.get("TrianglePairsTested") if "TrianglePairsTested" in source_contact
                                 else source_contact.get("trianglePairsTested"))
        same_author = bool(oi[0].get("responsiblePlugin") and
                           oi[0].get("responsiblePlugin") == oi[1].get("responsiblePlugin"))
        vanilla = set((policy or {}).get("analogueVanillaPlugins") or [])
        includes_vanilla = any(x.get("responsiblePlugin") in vanilla for x in oi)
        if actual.get("status") not in ("ok", "measured", "complete") or actual.get("intersects") is not True:
            continue
        if not same_author and not includes_vanilla and source_role != "vanilla":
            continue
        for entry in _analogue_keys(*oi, policy or {}):
            if entry:
                index.setdefault(entry, []).append({"sourceSetupId": setup_id, "pairKey": other.get("pairKey"),
                    "source": copy.deepcopy(source),
                    "matchKind": entry[0], "similarityEvidence": {
                        "consumedMeshSha256": sorted(x["meshContentSha256"] for x in oi if x.get("meshContentSha256")),
                        "baseFormKeys": sorted(x["baseFormKey"] for x in oi if x.get("baseFormKey")),
                        "sourceRelation": {"distance": _vec_distance(oi[0].get("position"), oi[1].get("position")),
                                           "eulerCoordinateDistanceRadians": _angle_distance(oi[0].get("rotation"), oi[1].get("rotation")),
                                           "surfaceRelation": actual.get("surfaceRelation"),
                                           "containment": actual.get("containment"),
                                           "relativeTransform": _relative_transform(oi[0], oi[1])},
                        "contactLocation": {"status": "complete-envelope", "value": source_contact_value,
                                            "policy": source_contact_policy}
                        if (source_contact_status == "complete" and
                            source_contact_policy == "contact_triangle_evidence_envelope_v1" and
                            _valid_contact_value(source_contact_value, source_contact_tested)) else
                        {"status": "unsupported", "value": None,
                         "reason": "source predicate did not provide a contact-location measurement"}},
                    "_identities": oi,
                    "limits": ["Placement similarity is contextual evidence; it does not establish a benign or harmful consequence."]})
    return index


def _merge_index(target, incoming):
    for key, rows in incoming.items():
        target.setdefault(key, []).extend(rows)


def _source_analogue_indices(result, current_setup_id, pairs, records, assets, policy):
    current_meta = (result.get("setups") or {}).get(current_setup_id) or {}
    index = {}
    source_limits = []
    current_predicate = (result.get("scope") or {}).get("predicate") or {}
    if not result.get("readerVersion") or not result.get("policyVersion") or not current_predicate:
        return index, ["combined setup lacks explicit reader, policy or predicate identity; no analogue evidence was admitted"]
    for sid, meta in sorted((result.get("setups") or {}).items()):
        if sid == current_setup_id or not isinstance(meta, dict):
            continue
        if meta.get("role") not in set(policy.get("analogueSourceRoles") or []):
            source_limits.append(f"setup {sid} role {meta.get('role')!r} is not an analogue source role")
            continue
        if meta.get("validity") != "valid" or not isinstance(meta.get("scene"), dict):
            source_limits.append(f"setup {sid} is not a valid scene and was not used for analogues")
            continue
        path, expected = meta.get("manifestPath"), meta.get("manifestSha256")
        if not path or not expected:
            source_limits.append(f"setup {sid} has no manifest path and hash for analogue indexing")
            continue
        try:
            source_doc = json.loads(Path(path).read_text(encoding="utf-8"))
            logical_doc = {k: v for k, v in source_doc.items() if not k.startswith("_source")}
            observed = (_digest(logical_doc) if meta.get("manifestHashKind") == "canonical-json" else _file_digest(path))
            if observed != expected:
                source_limits.append(f"setup {sid} manifest hash does not match; it was not used for analogues")
                continue
        except (OSError, ValueError):
            source_limits.append(f"setup {sid} manifest could not be read; it was not used for analogues")
            continue
        source_scene = source_doc.get("scene") or {}
        source_validity = source_scene.get("validity") or {}
        source_config = ((source_doc.get("scope") or {}).get("configuration") or {})
        if (source_scene.get("role") != meta.get("role") or source_validity.get("status") not in (None, "valid") or
                not source_scene.get("discovery") or
                not (source_config.get("Fingerprint") or source_config.get("fingerprint")) or
                source_config.get("Providers", source_config.get("providers")) is None):
            source_limits.append(f"setup {sid} source manifest does not prove the declared role, validity or discovery scope")
            continue
        source_predicate = (source_doc.get("scope") or {}).get("predicate") or {}
        current_config = ((result.get("scope") or {}).get("configuration") or {})
        current_limits = current_config.get("limits", current_config.get("Limits", []))
        source_limits_value = source_config.get("limits", source_config.get("Limits", []))
        if (source_doc.get("readerVersion") != result.get("readerVersion") or
                source_doc.get("policyVersion") != result.get("policyVersion") or
                source_predicate != current_predicate or source_limits_value != current_limits):
            source_limits.append(f"setup {sid} reader, predicate, tolerance or limits differ; it was not used for analogues")
            continue
        sr, sa = _record_indexes(source_doc)
        _merge_index(index, _analogue_index(source_doc.get("pairs") or [], sr, sa, sid,
                                             {"manifestPath": path, "manifestSha256": expected}, meta.get("role"),
                                             policy))
    return index, source_limits


def _analogues(pair, identity_a, identity_b, indices, setup_id, maximum, policy):
    matches = []
    target_actual = pair.get("actualShape") or {}
    if target_actual.get("status") not in ("ok", "measured", "complete") or target_actual.get("intersects") is not True:
        return matches
    target_transform = _relative_transform(identity_a, identity_b)
    target_transform_swapped = _relative_transform(identity_b, identity_a)
    for key in _analogue_keys(identity_a, identity_b, policy):
        if key:
            for row in indices.get(key, []):
                if row.get("pairKey") == pair.get("pairKey"):
                    continue
                source_ids = row.get("_identities") or []
                if len(source_ids) != 2:
                    continue
                target_relation = {"distance": _vec_distance(identity_a.get("position"), identity_b.get("position")),
                                   "surfaceRelation": target_actual.get("surfaceRelation"),
                                   "containment": target_actual.get("containment"),
                                   "relativeTransform": target_transform}
                source_relation = row["similarityEvidence"]["sourceRelation"]
                kind = row.get("matchKind")
                target_tokens = [_identity_token(identity_a, kind, policy), _identity_token(identity_b, kind, policy)]
                source_tokens = [_identity_token(source_ids[0], kind, policy), _identity_token(source_ids[1], kind, policy)]
                candidates = []
                if target_tokens == source_tokens:
                    direct = _transform_difference(target_transform, source_relation.get("relativeTransform"))
                    if direct is not None:
                        candidates.append(direct)
                if target_tokens[::-1] == source_tokens:
                    swapped = _transform_difference(target_transform_swapped, source_relation.get("relativeTransform"))
                    if swapped is not None:
                        candidates.append(swapped)
                if not candidates:
                    continue
                difference = min(candidates, key=lambda x: (x["localOffsetDistance"], x["orientationDeltaRadians"],
                                                             x["maximumRelativeScaleDelta"]))
                if (difference["localOffsetDistance"] > float(policy["pose"]["positionScale"]) or
                        difference["orientationDeltaRadians"] > float(policy["pose"]["rotationScaleRadians"]) or
                        difference["maximumRelativeScaleDelta"] > float(policy["pose"]["scaleRelativeTolerance"])):
                    continue
                if (target_relation["surfaceRelation"] and source_relation["surfaceRelation"] and
                        target_relation["surfaceRelation"] != source_relation["surfaceRelation"]):
                    continue
                kept_row = copy.deepcopy(row); kept_row.pop("_identities", None)
                kept_row["similarityEvidence"]["targetRelation"] = target_relation
                kept_row["similarityEvidence"]["relativeTransformDifference"] = difference
                contact = kept_row["similarityEvidence"]["contactLocation"]
                kept_row["confidence"] = "limited"
                kept_row["limits"].append(
                    "Contact support envelopes were not normalized against both source mesh frames; this analogue cannot lower review priority.")
                matches.append(kept_row)
    order = {"exact_mesh_pair": 0, "mesh_family_pair": 1, "exact_base_pair": 2}
    matches = sorted(matches, key=lambda x: (order.get(x["matchKind"], 9), str(x["sourceSetupId"]), str(x["pairKey"])))
    kept = matches[:maximum]
    for row in kept:
        row["matchAccounting"] = {"total": len(matches), "examplesRetained": len(kept)}
    return kept


def _centroid(ia, ib):
    a, b = ia.get("position"), ib.get("position")
    if not (isinstance(a, list) and isinstance(b, list) and len(a) >= 3 and len(b) >= 3):
        return None
    values = [float(x) for x in a[:3] + b[:3]]
    if not all(math.isfinite(x) for x in values):
        return None
    return [sum((values[i], values[i + 3])) / 2 for i in range(3)]


def _place_key(ia, ib):
    def one(identity):
        cell = identity.get("cell") or {}
        return (identity.get("space"), cell.get("formid"))
    a, b = one(ia), one(ib)
    return a if a == b else None


def _finite_box(value):
    if not isinstance(value, dict):
        return None
    low = value.get("min", value.get("EnvelopeMin"))
    high = value.get("max", value.get("EnvelopeMax"))
    if not (isinstance(low, list) and isinstance(high, list) and len(low) == 3 and len(high) == 3):
        return None
    numbers = [float(x) for x in low + high]
    if not all(math.isfinite(x) for x in numbers) or any(numbers[i] > numbers[i + 3] for i in range(3)):
        return None
    return (numbers[:3], numbers[3:])


def _box_union(first, second):
    return ([min(first[0][i], second[0][i]) for i in range(3)],
            [max(first[1][i], second[1][i]) for i in range(3)])


def _box_diagonal(box):
    return math.sqrt(sum((box[1][i] - box[0][i]) ** 2 for i in range(3)))


def _pair_support(pair, ra, rb):
    """Conservative contact-locality box in world-space Skyrim units."""
    contact = ((pair.get("features") or {}).get("contactEvidenceEnvelope") or {})
    if contact.get("status") == "measured":
        box = _finite_box(contact.get("value"))
        if box is not None:
            return box, "complete contact-support envelope"
    first = _finite_box((ra.get("geometry") or {}).get("worldBounds")) if ra else None
    second = _finite_box((rb.get("geometry") or {}).get("worldBounds")) if rb else None
    if first is None or second is None:
        return None, "complete contact support and both measured placed-NIF world bounds are unavailable"
    low = [max(first[0][i], second[0][i]) for i in range(3)]
    high = [min(first[1][i], second[1][i]) for i in range(3)]
    if any(low[i] > high[i] for i in range(3)):
        return None, "placed-NIF world bounds have no common support region"
    return (low, high), "intersection of measured placed-NIF world bounds"


def _geometry_unknown(pair):
    actual = pair.get("actualShape") or {}
    return actual.get("intersects") is None or actual.get("status") not in ("ok", "measured", "complete")


def annotate(manifest, policy=None):
    def adjudicated(review):
        return isinstance(review, dict) and (review.get("status", "unreviewed") != "unreviewed" or
                                             review.get("consequence") is not None)

    reviewed_pairs = [p.get("pairKey") for p in manifest.get("pairs") or [] if adjudicated(p.get("review"))]
    reviewed_incidents = [x.get("incidentId") for x in manifest.get("incidents") or [] if adjudicated(x.get("review"))]
    if reviewed_pairs or reviewed_incidents:
        raise ValueError("reviewed-input-refused: overlap_triage will not overwrite Q47 adjudication; "
                         "regenerate from the preserved attributed manifest before review "
                         f"(reviewed pairs={len(reviewed_pairs)}, incidents={len(reviewed_incidents)})")
    result = copy.deepcopy(manifest)
    policy = copy.deepcopy(policy) if policy is not None else json.loads(DEFAULT_POLICY.read_text(encoding="utf-8"))
    records, assets = _record_indexes(result)
    pairs = result.get("pairs") or []
    existing_attribution_policy = result.get("attributionPolicy") if isinstance(result.get("attributionPolicy"), dict) else {}
    setup_id = str((result.get("scope") or {}).get("setupId") or
                   existing_attribution_policy.get("combinedSetupId") or "manifest")
    result["triageSchemaVersion"] = SCHEMA
    result.setdefault("setups", {setup_id: {"manifestHash": _digest(manifest),
                                             "configuration": (result.get("scope") or {}).get("configuration"),
                                             "role": (result.get("scope") or {}).get("role") or "supplied",
                                             "validity": "supplied", "limits": []}})
    result["attributionPolicy"] = copy.deepcopy(existing_attribution_policy)
    result["attributionPolicy"]["triageRanking"] = {
        "schemaVersion": policy.get("schemaVersion"), "sha256": _digest(policy)}
    known_setups = set(result.get("setups") or {})
    for pair in pairs:
        for comparison in (pair.get("attribution") or {}).get("comparisons") or []:
            before_id, after_id = comparison.get("beforeSetupId"), comparison.get("afterSetupId")
            if before_id not in known_setups or after_id not in known_setups or after_id != setup_id:
                raise ValueError(f"comparison-direction-invalid: pair {pair.get('pairKey')} names "
                                 f"{before_id!r} -> {after_id!r}; expected a supplied setup -> {setup_id!r}")
    analogue_indices, analogue_limits = _source_analogue_indices(result, setup_id, pairs, records, assets, policy)
    annotated = []
    for pair in pairs:
        ra, rb = records.get(str(pair.get("a"))), records.get(str(pair.get("b")))
        pair.setdefault("attribution", {"status": "unresolved", "comparisons": [],
                                         "reasons": ["no setup comparison attribution was supplied"]})
        if not ra or not rb:
            pair["features"] = {"recordContext": _feature("unsupported", None, "one or both pair records are absent")}
            ia, ib = {}, {}
        else:
            pair["features"], ia, ib = _pair_feature(pair, ra, rb, assets, policy)
        pair["analogues"] = _analogues(pair, ia, ib, analogue_indices, setup_id,
                                        int(policy.get("maxAnalogueExamplesPerPair", 3)), policy) if ra and rb else []
        status = pair["attribution"].get("status", "unresolved")
        score = int(policy["ranking"]["attribution"].get(status, 0))
        reasons = [{"quantity": "attribution", "value": status, "points": score}]
        if (pair.get("actualShape") or {}).get("intersects") is True:
            points = int(policy["ranking"]["actualIntersection"]); score += points
            reasons.append({"quantity": "actualIntersection", "value": True, "points": points})
        funcs = ((pair.get("features") or {}).get("functionalContext") or {}).get("value") or []
        for typ in sorted({f["type"] for f in funcs}):
            points = int(policy["ranking"]["functionalPriority"].get(typ, 0)); score += points
            reasons.append({"quantity": "functionalType", "value": typ, "points": points})
        if pair["analogues"] and pair["analogues"][0].get("confidence") == "supported":
            kind = pair["analogues"][0]["matchKind"]
            key = ("exactMeshAnalogueAdjustment" if kind == "exact_mesh_pair" else
                   "meshFamilyAnalogueAdjustment" if kind == "mesh_family_pair" else "exactBaseAnalogueAdjustment")
            points = int(policy["ranking"][key]); score += points
            reasons.append({"quantity": "analogue", "value": kind, "points": points,
                            "limit": "ranking context only; never an automatic benign decision"})
        pair["ranking"] = {"score": score, "reasons": reasons,
                           "policy": {"schemaVersion": policy.get("schemaVersion"), "sha256": _digest(policy)}}
        pair["review"] = {"status": "unreviewed", "consequence": None}
        pair["queues"] = {
            "newOrChangedGeometryUnknown": status in ("new", "changed_existing") and _geometry_unknown(pair)
        }
        support, support_source = _pair_support(pair, ra, rb)
        pair["locality"] = {"status": "measured" if support is not None else "unknown",
                            "supportMin": support[0] if support else None,
                            "supportMax": support[1] if support else None,
                            "source": support_source,
                            "units": "Skyrim units"}
        annotated.append((pair, ia, ib, support, _place_key(ia, ib)))
    # Greedy representative grouping is deliberately non-transitive: a pair joins only when it is
    # directly local to the fixed representative and shares one placed object.
    incidents = []
    radius = float(policy.get("incidentRadius", 384.0))
    ordered = sorted(annotated, key=lambda x: (-x[0]["ranking"]["score"], str(x[0].get("pairKey"))))
    for pair, ia, ib, support, place_key in ordered:
        chosen = None
        pair_refs = {pair.get("a"), pair.get("b")}
        for incident in incidents:
            if (support is None or incident["_support"] is None or place_key is None or
                    place_key != incident["_placeKey"] or not pair_refs.intersection(incident["_representativeRefs"])):
                continue
            combined_support = _box_union(support, incident["_support"])
            if (_box_diagonal(support) <= radius and incident["_supportLocal"] and
                    _box_diagonal(combined_support) <= radius):
                chosen = incident; break
        if chosen is None:
            iid = f"incident-{len(incidents)+1:05d}"
            cell = ia.get("cell") or ib.get("cell") or {}
            chosen = {"incidentId": iid, "pairKeys": [], "representativePairKey": pair.get("pairKey"),
                      "representativeRefs": [pair.get("a"), pair.get("b")],
                      "place": {"cell": cell, "space": ia.get("space") or ib.get("space")},
                      "ranking": copy.deepcopy(pair["ranking"]), "review": {"status": "unreviewed", "consequence": None},
                      "limits": [f"Members share the fixed representative's placed reference and the diagonal of their combined world-space contact-support locality is at most {radius:g} Skyrim units; unknown or individually larger supports remain singleton and membership is not transitive."],
                      "_representativeRefs": pair_refs, "_support": support,
                      "_supportLocal": support is not None and _box_diagonal(support) <= radius}
            chosen["_placeKey"] = place_key
            incidents.append(chosen)
        elif support is not None:
            chosen["_support"] = _box_union(chosen["_support"], support)
        chosen["pairKeys"].append(pair.get("pairKey")); pair["incidentId"] = chosen["incidentId"]
    pair_by_key = {p.get("pairKey"): p for p in pairs}
    for incident in incidents:
        incident.pop("_representativeRefs", None); incident.pop("_placeKey", None)
        incident.pop("_support", None); incident.pop("_supportLocal", None)
        source_ids = set()
        for key in incident["pairKeys"]:
            member = pair_by_key[key]
            for comparison in (member.get("attribution") or {}).get("comparisons") or []:
                source_ids.update(x for x in (comparison.get("beforeSetupId"), comparison.get("afterSetupId")) if x)
            source_ids.update(x.get("sourceSetupId") for x in member.get("analogues") or [] if x.get("sourceSetupId"))
        incident["sourceSetups"] = [{"setupId": sid,
                                      "manifestPath": ((result.get("setups") or {}).get(sid) or {}).get("manifestPath"),
                                      "manifestSha256": ((result.get("setups") or {}).get(sid) or {}).get("manifestSha256")}
                                     for sid in sorted(source_ids)]
    result["incidents"] = incidents
    statuses = [str((p.get("attribution") or {}).get("status") or "unresolved") for p in pairs]
    producer_accounting = copy.deepcopy(result.get("accounting") or {})
    measurement_accounting = copy.deepcopy(producer_accounting.get("measurement") or
                                           result.get("measurementAccounting") or {})
    attribution_accounting = copy.deepcopy(producer_accounting.get("attribution") if "attribution" in producer_accounting else
                                           ({k: v for k, v in producer_accounting.items()
                                             if k not in ("measurement", "triage")} if producer_accounting else {}))
    triage_accounting = {"pairsTotal": len(pairs), "incidentsTotal": len(incidents),
                            "pairsByAttribution": {k: statuses.count(k) for k in sorted(set(statuses))},
                            "reviewedPairs": sum((p.get("review") or {}).get("status") != "unreviewed" for p in pairs),
                            "unreviewedPairs": sum((p.get("review") or {}).get("status") == "unreviewed" for p in pairs),
                            "reviewedIncidents": sum((x.get("review") or {}).get("status") != "unreviewed" for x in incidents),
                            "unreviewedIncidents": sum((x.get("review") or {}).get("status") == "unreviewed" for x in incidents),
                            "unknownPairs": sum(any(f.get("status") != "measured" for f in (p.get("features") or {}).values()) for p in pairs),
                            "unknownFeatureValues": sum(1 for p in pairs for f in (p.get("features") or {}).values()
                                                        if f.get("status") != "measured"),
                            "newOrChangedGeometryUnknownPairs": sum(bool((p.get("queues") or {}).get("newOrChangedGeometryUnknown")) for p in pairs),
                            "newOrChangedGeometryUnknownIncidents": len({p.get("incidentId") for p in pairs
                                if (p.get("queues") or {}).get("newOrChangedGeometryUnknown")})}
    triage_accounting["analogueSourceLimits"] = analogue_limits
    result["accounting"] = {"measurement": measurement_accounting,
                            "attribution": attribution_accounting,
                            "triage": triage_accounting}
    return result


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    args = parser.parse_args(argv)
    with Run("overlap-triage", stage_source="counted") as run:
        run.stage("annotate", total=1, unit="manifests")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        policy = json.loads(args.policy.read_text(encoding="utf-8"))
        output = annotate(manifest, policy)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        counts = output["accounting"]["triage"]
        run.artifact(str(args.out)); run.feature("pairs", counts["pairsTotal"], "pairs")
        run.feature("incidents", counts["incidentsTotal"], "incidents"); run.advance(1)
        print(json.dumps({"out": str(args.out), "pairs": counts["pairsTotal"],
                          "incidents": counts["incidentsTotal"],
                          "policySha256": output["attributionPolicy"]["triageRanking"]["sha256"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
