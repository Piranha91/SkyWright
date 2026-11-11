#!/usr/bin/env python
import copy
import importlib.util
import json
import math
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("overlap_triage", ROOT / "_geometry-harness/overlap_triage.py")
triage = importlib.util.module_from_spec(spec); spec.loader.exec_module(triage)
report_spec = importlib.util.spec_from_file_location("evalreport_for_triage", ROOT / "_geometry-harness/evalreport.py")
evalreport = importlib.util.module_from_spec(report_spec); report_spec.loader.exec_module(evalreport)


def fixture():
    def record(key, typ, pos, mesh, base):
        return {"formid": key, "type": typ, "winner": key.split(":", 1)[1], "fields": {
            "position": pos, "rotation": [0, 0, 0], "scale": 1.0, "responsiblePlugin": key.split(":", 1)[1],
            "effectiveBase": {"formid": base, "type": typ},
            "cell": {"formid": "1:World.esm", "editorid": "TestPlace"}},
            "geometry": {"assetId": mesh, "worldBounds": {"min": [pos[0]-10, pos[1]-10, pos[2]-10],
                                                              "max": [pos[0]+10, pos[1]+10, pos[2]+10]}}}
    records = [record("1:A.esp", "Door", [0, 0, 0], "m1", "10:A.esp"),
               record("2:B.esp", "PlacedObject", [4, 0, 0], "m2", "20:B.esp"),
               record("3:A.esp", "Door", [20, 0, 0], "m1", "10:A.esp"),
               record("4:B.esp", "PlacedObject", [24, 0, 0], "m2", "20:B.esp"),
               record("5:B.esp", "PlacedObject", [1000, 0, 0], "m2", "20:B.esp")]
    pairs = [{"pairKey": "1||2", "a": "1:A.esp", "b": "2:B.esp", "actualShape": {"status": "ok", "intersects": True},
              "attribution": {"status": "new", "comparisons": [], "reasons": []}},
             {"pairKey": "1||3", "a": "1:A.esp", "b": "3:A.esp", "actualShape": {"status": "ok", "intersects": True},
              "attribution": {"status": "inherited", "comparisons": [], "reasons": []}},
             {"pairKey": "3||4", "a": "3:A.esp", "b": "4:B.esp", "actualShape": {"status": "ok", "intersects": True},
              "attribution": {"status": "changed_existing", "comparisons": [], "reasons": []}},
             {"pairKey": "4||5", "a": "4:B.esp", "b": "5:B.esp", "actualShape": {"intersects": None},
              "attribution": {"status": "unresolved", "comparisons": [], "reasons": []}}]
    return {"schemaVersion": "original-v2", "readerVersion": "reader-v1", "policyVersion": "shape-v1",
            "job": "test", "scene": {"role": "source", "validity": {"status": "valid"},
                                     "discovery": {"scope": "fixture"}},
            "scope": {"setupId": "combined", "predicate": {"policyVersion": "actual_triangle_mesh_v1", "tolerance": 1e-5},
                      "configuration": {"Fingerprint": "fixture", "Providers": [], "Limits": []}},
            "records": records, "assets": [{"assetId": "m1", "relativePath": "a.nif", "consumedSha256": "a" * 64},
                                             {"assetId": "m2", "relativePath": "b.nif", "consumedSha256": "b" * 64}],
            "pairs": pairs, "notes": ["preserve me"]}


def test_contract():
    source = fixture(); out = triage.annotate(source)
    assert source["pairs"][0].get("features") is None
    assert out["schemaVersion"] == "original-v2" and out["notes"] == ["preserve me"]
    assert out["triageSchemaVersion"] == "overlap-triage-v1"
    assert out["accounting"]["triage"]["pairsTotal"] == len(source["pairs"])
    assert sum(len(x["pairKeys"]) for x in out["incidents"]) == len(source["pairs"])
    assert {p["pairKey"] for p in out["pairs"]} == {k for i in out["incidents"] for k in i["pairKeys"]}
    assert all(p["review"] == {"status": "unreviewed", "consequence": None} for p in out["pairs"])
    assert out["pairs"][0]["features"]["actualIntersectionExtent"]["value"] is None
    assert "not measured" in out["pairs"][0]["features"]["actualIntersectionExtent"]["reason"]
    assert out["pairs"][0]["ranking"]["score"] > out["pairs"][1]["ranking"]["score"]
    assert out["pairs"][0]["features"]["functionalContext"]["value"][0]["type"] == "Door"
    assert out["pairs"][0]["features"]["contactEvidenceEnvelope"]["status"] == "unsupported"
    assert out["pairs"][0]["analogues"] == []  # combined-scene repetition is not a source analogue
    # 1||2 groups with 1||3; 3||4 cannot join through a transitive 1||3 link because it does not
    # share the fixed representative's reference. 4||5 is also outside the representative radius.
    by_pair = {p["pairKey"]: p["incidentId"] for p in out["pairs"]}
    assert by_pair["1||2"] == by_pair["1||3"]
    assert by_pair["3||4"] != by_pair["1||2"]
    assert by_pair["4||5"] != by_pair["3||4"]
    again = triage.annotate(copy.deepcopy(source))
    assert [(p["pairKey"], p["incidentId"], p["ranking"]) for p in out["pairs"]] == [
        (p["pairKey"], p["incidentId"], p["ranking"]) for p in again["pairs"]]
    assert triage.annotate(copy.deepcopy(out)) == out
    reviewed_pair = copy.deepcopy(out)
    reviewed_pair["pairs"][0]["review"] = {"status": "reviewed", "consequence": "harmful"}
    try:
        triage.annotate(reviewed_pair)
        assert False, "reviewed pair should be refused"
    except ValueError as exc:
        assert "reviewed-input-refused" in str(exc)
    reviewed_incident = copy.deepcopy(out)
    reviewed_incident["incidents"][0]["review"]["consequence"] = "unknown"
    try:
        triage.annotate(reviewed_incident)
        assert False, "adjudicated incident should be refused"
    except ValueError as exc:
        assert "reviewed-input-refused" in str(exc)


def test_cli():
    with tempfile.TemporaryDirectory() as td:
        td = Path(td); src = td / "in.json"; dst = td / "out.json"
        src.write_text(json.dumps(fixture()), encoding="utf-8")
        assert triage.main(["--manifest", str(src), "--out", str(dst)]) == 0
        assert json.loads(dst.read_text(encoding="utf-8"))["accounting"]["triage"]["pairsTotal"] == 4


def test_report_catalogue():
    policy = json.loads(triage.DEFAULT_POLICY.read_text(encoding="utf-8"))
    annotated = triage.annotate(fixture(), policy)
    qualification = ("Legacy retained-population replay; counts cover only retained identities, not an "
                     "R2-corrected exterior recapture; exterior completeness is unestablished and distant "
                     "origin is not clearance.")
    annotated["scope"]["reportQualification"] = qualification
    catalogue, page = evalreport.compile_triage_catalogue(annotated, policy, "c" * 64)
    assert catalogue["schemaVersion"] == "overlap-triage-catalogue-v1"
    assert len(catalogue["incidents"]) == annotated["accounting"]["triage"]["incidentsTotal"]
    assert "Pairs awaiting review" in page and "Unknown or unsupported feature values" in page
    assert "TestPlace" in page and "Door" in page and "new" in page
    assert catalogue["source"]["reportQualification"] == qualification
    assert qualification in page
    detail_pages = evalreport.render_triage_incident_pages(catalogue, page_size=1)
    assert len(detail_pages) == len(catalogue["incidents"])
    rendered_members = "".join(detail_pages.values())
    assert all(qualification in detail for detail in detail_pages.values())
    assert all(key in rendered_members for key in {p["pairKey"] for p in annotated["pairs"]})
    assert all(f'incidents-{n:04d}.html' in rendered_members for n in range(1, len(detail_pages) + 1))
    changed = copy.deepcopy(policy); changed["incidentRadius"] += 1
    try:
        evalreport.compile_triage_catalogue(annotated, changed, "c" * 64)
        raise AssertionError("changed policy was accepted")
    except ValueError as exc:
        assert "triage-policy-changed" in str(exc)
    malformed = copy.deepcopy(annotated)
    malformed["incidents"][0]["pairKeys"].append(malformed["incidents"][1]["pairKeys"][0])
    try:
        evalreport.compile_triage_catalogue(malformed, policy, "c" * 64)
        raise AssertionError("duplicate incident membership was accepted")
    except ValueError as exc:
        assert "triage-accounting-invalid" in str(exc)
    with tempfile.TemporaryDirectory() as td:
        td = Path(td); source = td / "triage.json"; report = td / "report"
        source.write_text(json.dumps(annotated), encoding="utf-8")
        assert evalreport.main(["triage", "--manifest", str(source), "--out", str(report)]) == 0
        assert (report / "overlap-triage-catalogue.html").exists()
        assert (report / "incidents-0001.html").exists()
        assert json.loads((report / "overlap-triage-catalogue.json").read_text(encoding="utf-8"))["identity"]


def test_cross_setup_analogue_and_nonfinite_pose():
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        source = fixture(); source["scope"]["setupId"] = "source"
        source["records"][1]["fields"]["responsiblePlugin"] = "A.esp"  # a same-author source relationship
        source_path = td / "source.json"
        source_path.write_text(json.dumps(source), encoding="utf-8")
        combined = fixture()
        combined["attributionPolicy"] = {"combinedSetupId": "combined", "comparisonSetupIds": ["source"]}
        combined["setups"] = {
            "combined": {"setupId": "combined", "manifestPath": None, "manifestSha256": None},
            "source": {"setupId": "source", "role": "source", "validity": "valid", "scene": {},
                       "manifestPath": str(source_path),
                       "manifestSha256": triage._file_digest(source_path)}}
        combined["setups"]["patch"] = {"setupId": "patch", "role": "patch", "validity": "valid", "scene": {},
                                                "manifestPath": str(source_path),
                                                "manifestSha256": triage._file_digest(source_path)}
        combined["setups"]["invalid"] = {"setupId": "invalid", "role": "source", "validity": "unresolved",
                                          "scene": {}, "manifestPath": str(source_path),
                                          "manifestSha256": triage._file_digest(source_path)}
        combined["records"][0]["fields"]["position"] = [float("nan"), 0, 0]
        out = triage.annotate(combined)
        assert out["pairs"][0]["features"]["poseSimilarity"]["status"] == "unsupported"
        assert any(a["sourceSetupId"] == "source" and a["source"]["manifestSha256"] == triage._file_digest(source_path)
                   for a in out["pairs"][2]["analogues"])
        assert out["pairs"][2]["analogues"][0]["confidence"] == "limited"
        assert not any(x["quantity"] == "analogue" for x in out["pairs"][2]["ranking"]["reasons"])
        limits = out["accounting"]["triage"]["analogueSourceLimits"]
        assert any("patch" in x and "role" in x for x in limits)
        assert any("invalid" in x and "not a valid scene" in x for x in limits)
        # The source's unrelated unknown 4||5 pair did not block its valid known 1||2 analogue.
        assert any(a["sourceSetupId"] == "source" for a in out["pairs"][2]["analogues"])


def test_comparison_direction_refusal():
    source = fixture()
    source["attributionPolicy"] = {"combinedSetupId": "combined"}
    source["setups"] = {"combined": {}, "baseline": {}, "patch": {}}
    source["pairs"][0]["attribution"]["comparisons"] = [
        {"beforeSetupId": "baseline", "afterSetupId": "patch", "changedFields": {}}]
    try:
        triage.annotate(source)
        raise AssertionError("foreign comparison target was accepted")
    except ValueError as exc:
        assert "comparison-direction-invalid" in str(exc)


def test_counterpart_locality_and_pair_order_invariance():
    source = fixture()
    # The two pairs share a very large reference at the origin. Their other objects are 700 units
    # apart, so midpoint grouping would merge them under a 384-unit radius; counterpart locality must split them.
    # All three origins coincide: origins cannot locate where contact occurs on the long shared mesh.
    for record in source["records"][:3]:
        record["fields"]["position"] = [0, 0, 0]
    source["records"][0]["geometry"]["worldBounds"] = {"min": [-1000,-20,-20], "max": [1000,20,20]}
    source["records"][1]["geometry"]["worldBounds"] = {"min": [-710,-10,-10], "max": [-690,10,10]}
    source["records"][2]["geometry"]["worldBounds"] = {"min": [690,-10,-10], "max": [710,10,10]}
    source["pairs"] = [source["pairs"][0], {"pairKey": "1||3", "a": "1:A.esp", "b": "3:A.esp",
        "actualShape": {"status": "ok", "intersects": True},
        "attribution": {"status": "new", "comparisons": [], "reasons": []}}]
    out = triage.annotate(source)
    assert out["pairs"][0]["incidentId"] != out["pairs"][1]["incidentId"]
    reversed_source = copy.deepcopy(source)
    reversed_source["pairs"].reverse()
    for pair in reversed_source["pairs"]:
        pair["a"], pair["b"] = pair["b"], pair["a"]
    reversed_out = triage.annotate(reversed_source)
    groups = lambda doc: sorted(sorted(i["pairKeys"]) for i in doc["incidents"])
    assert groups(out) == groups(reversed_out)
    assert sum(len(x["pairKeys"]) for x in out["incidents"]) == len(out["pairs"])


def test_partial_member_reviews_and_unknown_change_queue_are_reported():
    policy = json.loads(triage.DEFAULT_POLICY.read_text(encoding="utf-8"))
    source = fixture()
    source["pairs"][0]["actualShape"] = {"status": "unknown", "intersects": None, "reason": "geometry unavailable"}
    source["pairs"][2]["attribution"]["status"] = "inherited"
    source["setups"] = {"combined": {}, "source": {}}
    source["pairs"][0]["attribution"]["comparisons"] = [{"beforeSetupId": "source", "afterSetupId": "combined",
        "status": "new", "reason": "pose changed", "changedFields": {"1:A.esp": ["pose"]}}]
    annotated = triage.annotate(source, policy)
    incident_id = annotated["pairs"][0]["incidentId"]
    members = [p for p in annotated["pairs"] if p["incidentId"] == incident_id]
    assert len(members) == 2
    members[0]["review"] = {"status": "reviewed", "consequence": "limited benign", "nextAction": "none"}
    catalogue, page = evalreport.compile_triage_catalogue(annotated, policy, "c" * 64)
    row = next(x for x in catalogue["incidents"] if x["incidentId"] == incident_id)
    assert row["reviewScope"] == "partially_reviewed"
    assert row["reviewedMemberCount"] == 1 and row["unreviewedMemberCount"] == 1
    assert row["members"][0]["review"] and all("objects" in m for m in row["members"])
    assert catalogue["accounting"]["outstandingReviewIncidents"] >= 1
    assert catalogue["accounting"]["newOrChangedGeometryUnknownPairs"] == 1
    assert "Outstanding review" in page and "New/changed geometry unknown" in page
    assert "effective fields: pose ×1" in page and "source→combined: new ×1" in page
    comparison = next(m for m in row["members"] if m["pairKey"] == "1||2")["attribution"]["comparisons"][0]
    assert "1:A.esp" in comparison["changedValues"] and "pose" in comparison["changedValues"]["1:A.esp"]


def test_relative_transform_counterexamples_and_family_order():
    ident = lambda pos, rot, scale, sha, base: {"position": pos, "rotation": rot, "scale": scale,
                                                "meshContentSha256": sha, "mesh": sha + ".nif",
                                                "baseFormKey": base}
    a = ident([0, 0, 0], [0, 0, 0], 1, "a", "base-a")
    b = ident([4, 0, 0], [0, 0, 0], 1, "b", "base-b")
    target = triage._relative_transform(a, b)
    opposite = triage._relative_transform(a, ident([-4, 0, 0], [0, 0, 0], 1, "b", "base-b"))
    turned = triage._relative_transform(a, ident([4, 0, 0], [0, 0, 1], 1, "b", "base-b"))
    scaled = triage._relative_transform(a, ident([4, 0, 0], [0, 0, 0], .5, "b", "base-b"))
    assert triage._transform_difference(target, opposite)["localOffsetDistance"] == 8
    assert triage._transform_difference(target, turned)["orientationDeltaRadians"] > .9
    assert triage._transform_difference(target, scaled)["maximumRelativeScaleDelta"] == .5
    rotated_a = ident([0, 0, 0], [0, 0, math.pi / 2], 1, "a", "base-a")
    rotated_b = ident([0, 4, 0], [0, 0, math.pi / 2], 1, "b", "base-b")
    local = triage._relative_transform(rotated_a, rotated_b)["localOffset"]
    # A faces east (heading 90), B is 4 u north: B is on A's left. Was +4, the transpose (run 2026-11-10-primitive-box-rotation-sense, K7).
    assert abs(local[0] + 4) < 1e-9 and abs(local[1]) < 1e-9
    policy = json.loads(triage.DEFAULT_POLICY.read_text(encoding="utf-8"))
    policy["meshFamilies"] = {"family-a": {"contentSha256": ["a"]}, "family-b": {"contentSha256": ["b"]}}
    assert [x[0] for x in triage._analogue_keys(a, b, policy)] == [
        "exact_mesh_pair", "mesh_family_pair", "exact_base_pair"]


def _placed_box(lo, hi, pos, rot, scale):
    """A model box placed corner by corner with the helper, as a caller does (row vectors, like Vector3.Transform)."""
    m = triage._refr_rotation(rot)
    cs = [triage._row_mul([x * scale, y * scale, z * scale], m) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]
    return [min(c[i] for c in cs) + pos[i] for i in range(3)] + [max(c[i] for c in cs) + pos[i] for i in range(3)]


def test_refr_rotation_matches_spatial_overlap_to_world():
    # crossprobe section 13 (Program.cs:300-344): the boxes the C# SpatialOverlap.ToWorld is pinned to
    deg = math.pi / 180
    near = lambda got, want: all(abs(g - w) < .02 for g, w in zip(got, want))
    centred = ([-100, -20, -50], [100, 20, 50])
    off = ([0, -20, 0], [200, 20, 100])
    assert near(_placed_box(*centred, [1000, 2000, 300], [0, 0, 30 * deg], 1),
                [903.3975, 1932.6795, 250, 1096.6025, 2067.3205, 350])
    assert near(_placed_box(*centred, [1000, 2000, 300], [40 * deg, 0, 30 * deg], 1),
                [903.3975, 1916.2901, 218.425, 1096.6025, 2083.7099, 381.575])
    assert near(_placed_box(*off, [0, 0, 0], [0, 0, 90 * deg], 1), [-20, -200, 0, 20, 0, 100])
    assert near(_placed_box(*off, [500, -300, 40], [20 * deg, -35 * deg, 110 * deg], 2),
                [357.1434, -646.6001, 24.4199, 645.5053, -223.7372, 411.8254])


def test_relative_transform_is_unchanged_by_moving_the_pair_rigidly():
    # both references turned by one world rotation Q about the origin: their relation must not change
    def mm(p, q): return [[sum(p[i][k] * q[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

    def engine(rot):  # the engine's matrix in row form, (Rx(-x)Ry(-y)Rz(-z))^T, written out apart from the helper
        x, y, z = (-r for r in rot)
        rx = [[1, 0, 0], [0, math.cos(x), -math.sin(x)], [0, math.sin(x), math.cos(x)]]
        ry = [[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]]
        rz = [[math.cos(z), -math.sin(z), 0], [math.sin(z), math.cos(z), 0], [0, 0, 1]]
        a = mm(mm(rx, ry), rz)
        return [[a[j][i] for j in range(3)] for i in range(3)]

    def euler_of(m):  # record angles whose engine matrix is m (row form, m[2][0] = sin(-y))
        return [-math.atan2(-m[2][1], m[2][2]), -math.asin(max(-1, min(1, m[2][0]))), -math.atan2(-m[1][0], m[0][0])]
    q = engine([.3, -.2, 1.1])
    ident = lambda pos, rot: {"position": pos, "rotation": rot, "scale": 1}
    pa, ra, pb, rb = [100, 50, 10], [.2, .1, .7], [160, 20, 30], [-.1, .4, 2.0]
    for r in (ra, rb):
        back, want = engine(euler_of(mm(engine(r), q))), mm(engine(r), q)
        assert max(abs(back[i][j] - want[i][j]) for i in range(3) for j in range(3)) < 1e-9
    row = lambda p: [sum(p[k] * q[k][j] for k in range(3)) for j in range(3)]
    moved = lambda p, r: ident(row(p), euler_of(mm(engine(r), q)))
    source = triage._relative_transform(ident(pa, ra), ident(pb, rb))
    target = triage._relative_transform(moved(pa, ra), moved(pb, rb))
    d = triage._transform_difference(target, source)
    assert d["localOffsetDistance"] < 1e-6 and d["orientationDeltaRadians"] < 1e-6


def test_contact_evidence_quantity():
    source = fixture()
    source["pairs"][0]["contactEvidence"] = {
        "Status": "complete", "Reason": None, "TrianglePairsTested": 14,
        "PolicyVersion": "contact_triangle_evidence_envelope_v1",
        "Value": {"CrossingPairs": 2, "CoplanarPairs": 1, "TouchingPairs": 0,
                  "ParticipatingTrianglesA": 2, "ParticipatingTrianglesB": 3,
                  "EnvelopeMin": [0, 0, 0], "EnvelopeMax": [1, 2, 3]}}
    out = triage.annotate(source)
    feature = out["pairs"][0]["features"]["contactEvidenceEnvelope"]
    assert feature["status"] == "measured" and feature["value"]["CrossingPairs"] == 2
    assert "not exact intersection extent" in feature["limits"][0]
    assert out["pairs"][0]["features"]["actualIntersectionExtent"]["status"] == "unsupported"
    camel = fixture()
    camel["pairs"][0]["contactEvidence"] = {
        "status": "complete", "reason": None, "trianglePairsTested": 3,
        "policyVersion": "contact_triangle_evidence_envelope_v1",
        "value": {"crossingPairs": 1, "coplanarPairs": 1, "touchingPairs": 1,
                  "participatingTrianglesA": 2, "participatingTrianglesB": 2,
                  "envelopeMin": [0, 0, 0], "envelopeMax": [1, 2, 3]}}
    camel_feature = triage.annotate(camel)["pairs"][0]["features"]["contactEvidenceEnvelope"]
    assert camel_feature["status"] == "measured" and camel_feature["value"]["TouchingPairs"] == 1
    broken = fixture()
    broken["pairs"][0]["contactEvidence"] = {
        "Status": "complete", "TrianglePairsTested": 1,
        "PolicyVersion": "contact_triangle_evidence_envelope_v1",
        "Value": {"CrossingPairs": 2, "CoplanarPairs": 0, "TouchingPairs": 0,
                  "ParticipatingTrianglesA": 0, "ParticipatingTrianglesB": 1,
                  "EnvelopeMin": [2, 0, 0], "EnvelopeMax": [1, 1, 1]}}
    rejected = triage.annotate(broken)["pairs"][0]["features"]["contactEvidenceEnvelope"]
    assert rejected["status"] == "unsupported" and "invariants" in rejected["reason"]


def test_missing_source_identity_excludes_analogues():
    with tempfile.TemporaryDirectory() as td:
        td = Path(td); source = fixture(); source.pop("readerVersion")
        source_path = td / "source.json"; source_path.write_text(json.dumps(source), encoding="utf-8")
        combined = fixture(); combined["attributionPolicy"] = {"combinedSetupId": "combined"}
        combined["setups"] = {"combined": {}, "source": {"role": "source", "validity": "valid", "scene": {},
            "manifestPath": str(source_path), "manifestSha256": triage._file_digest(source_path)}}
        out = triage.annotate(combined)
        assert all(not p["analogues"] for p in out["pairs"])
        assert any("reader" in x for x in out["accounting"]["triage"]["analogueSourceLimits"])


if __name__ == "__main__":
    test_contract(); test_cli(); test_report_catalogue(); test_cross_setup_analogue_and_nonfinite_pose()
    test_relative_transform_counterexamples_and_family_order()
    test_refr_rotation_matches_spatial_overlap_to_world()
    test_relative_transform_is_unchanged_by_moving_the_pair_rigidly()
    test_contact_evidence_quantity()
    test_comparison_direction_refusal()
    test_counterpart_locality_and_pair_order_invariance()
    test_partial_member_reviews_and_unknown_change_queue_are_reported()
    test_missing_source_identity_excludes_analogues()
    print("PASS: overlap triage")
