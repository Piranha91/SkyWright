import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from subject_gate import frame_contact, project_box, qualify, sha


class SubjectGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.fixture_count = 0
        self.camera = {"eye": [0, 0, 0], "look": [0, 10, 0], "fov": 90, "size": 100}

    def tearDown(self):
        self.temp.cleanup()

    def document(self, name, doc):
        path = self.root / name
        path.write_text(json.dumps(doc), encoding="utf-8")
        return {"path": str(path), "sha256": sha(path)}

    def fixture(self):
        self.fixture_count += 1
        prefix = f"fixture-{self.fixture_count}-"
        records = [{"formid": name, "fields": {"space": "room", "resolvedEnable": {"enabled": True, "resolved": True}},
                    "geometry": {"worldBounds": {"min": [-1, -12, -1], "max": [1, -10, 1]}}} for name in ["a.esm", "b.esm", "c.esm"]]
        contact = {"status": "complete", "value": {"envelopeMin": [-2, 10, -2], "envelopeMax": [2, 12, 2]}}
        source = self.document(prefix + "source.json", {"records": records, "pairs": [{"pairKey": "a.esm||b.esm", "contactEvidence": contact}],
                    "scope": {"configuration": {"Fingerprint": "config"}}})
        renderer_path = self.root / (prefix + "mock-viewprobe.exe")
        renderer_path.write_bytes(b"bound analytic renderer executable")
        runtime = {"path": str(renderer_path), "sha256": sha(renderer_path)}
        candidate_path = self.root / (prefix + "candidate.json")
        candidate_path.write_text(json.dumps({"Mods": []}), encoding="utf-8")
        candidate = {"candidatePath": str(candidate_path), "candidateSha256": sha(candidate_path),
                     "configurationFingerprint": "config"}
        self.wrapper = self.root / (prefix + "bounded_exec.py")
        self.wrapper.write_text("# bound analytic wrapper\n", encoding="utf-8")
        sidecar = self.document(prefix + "sidecar.json", {"Configuration": {"Fingerprint": "config"}})
        case = {"caseId": "fixture", "pairKey": "a.esm||b.esm", "a": records[0], "b": records[1], "elsewhere": records[2],
                "cell": "cell.esm", "camera": self.camera, "sourceIdentity": source, "rendererIdentity": [runtime],
                "candidate": candidate, "configurationFingerprint": "config",
                "reviewEnvironment": {"renderer": str(renderer_path), "instance": "instance", "profile": "profile", "game": "game"},
                "viewArgs": ["--water-arm", "off"],
                "contactEvidence": contact, "elsewhereEvidence": {"status": "verified", "basis": "measured box behind eye"},
                "signalPolicy": {"feature": "eight-pixel-square analytic patches", "basis": "independent fixture geometry", "minimumPixels": 64}, "captures": {}}
        vector = lambda values: ",".join(format(float(value), ".12g") for value in values)
        for role in ["normal", "a", "b", "elsewhere"]:
            for i in [1, 2]:
                key = role + str(i)
                array = np.full((100, 100, 3), 30, dtype=np.uint8)
                if role == "a":
                    array[42:50, 42:50] = [0, 255, 255]
                if role == "b":
                    array[51:59, 51:59] = [255, 0, 255]
                capture_dir = self.root / (prefix + key)
                capture_dir.mkdir()
                path = capture_dir / "frame.png"
                Image.fromarray(array).save(path)
                refs = [] if role == "normal" else [case[role]["formid"]]
                argv = [str(renderer_path), case["cell"], "--instance", "instance", "--profile", "profile", "--game", "game",
                        "--order", str(candidate_path), "--report-mode", "--ref-surface",
                        "--eye", vector(self.camera["eye"]), "--look", vector(self.camera["look"]),
                        "--fov", "90", "--size", "100", "--msaa", "0", "--nav", "off", "--out", str(capture_dir),
                        *case["viewArgs"]]
                for ref in refs:
                    argv += ["--ref", ref + ("=magenta" if role == "b" else "=cyan")]
                job_argv = [sys.executable, str(self.wrapper), "90", *argv]
                log = self.root / (prefix + key + ".log")
                log.write_text("$ " + " ".join(job_argv) + "\nsubject-surface-mode: exact placed opaque triangles", encoding="utf-8")
                case["captures"][key] = {"image": str(path), "imageSha256": sha(path), "exitCode": 0, "highlightRefs": refs,
                    "highlightMode": "surface", "configurationFingerprint": "config", "camera": self.camera,
                    "actualRendererArguments": argv, "job": {"argv": job_argv},
                    "configurationSidecar": sidecar, "log": {"path": str(log), "sha256": sha(log)}}
        case["normalViewReview"] = {"imageHashes": {key: capture["imageSha256"] for key, capture in case["captures"].items()},
            "reviewer": "analytic fixture expectation", "featureDescription": "two independent square patches", "identity": "supported",
            "contactExposure": "exposed", "controlEvidence": "separate negative fixture"}
        return case

    def rewrite_image(self, case, key, image):
        path = Path(case["captures"][key]["image"])
        Image.fromarray(image).save(path)
        case["captures"][key]["imageSha256"] = sha(path)
        case["normalViewReview"]["imageHashes"][key] = sha(path)

    def sync_invocation(self, case, key):
        capture = case["captures"][key]
        command = capture["actualRendererArguments"]
        job_argv = [sys.executable, str(self.wrapper), "90", *command]
        capture["job"]["argv"] = job_argv
        log = Path(capture["log"]["path"])
        log.write_text("$ " + " ".join(job_argv) + "\nsubject-surface-mode: exact placed opaque triangles", encoding="utf-8")
        capture["log"]["sha256"] = sha(log)

    def rewrite_source_record(self, case, role, change):
        change(case[role])
        source_path = Path(case["sourceIdentity"]["path"])
        source = json.loads(source_path.read_text(encoding="utf-8"))
        source_record = next(record for record in source["records"] if record["formid"] == case[role]["formid"])
        change(source_record)
        source_path.write_text(json.dumps(source), encoding="utf-8")
        case["sourceIdentity"]["sha256"] = sha(source_path)

    def test_projection_independent_numeric_oracle(self):
        # 90 degree vertical FOV: at y=10 each x/z=+/-2 projects 10 px
        # either side of centre. Far corners y=12 project strictly inside.
        result = project_box(self.camera, [-2, 10, -2], [2, 12, 2], (100, 100))
        for actual, expected in zip(result["unclippedRectangle"], [40, 40, 60, 60]):
            self.assertAlmostEqual(actual, expected)
        camera = {**self.camera, "look": [10, 0, 0]}
        turned = project_box(camera, [10, -2, -2], [12, 2, 2], (100, 100))
        for actual, expected in zip(turned["unclippedRectangle"], [40, 40, 60, 60]):
            self.assertAlmostEqual(actual, expected)

    def test_qualified_only_with_bound_normal_feature_witness(self):
        case = self.fixture()
        self.assertEqual("qualified", qualify(case)["measurementStatus"])
        del case["normalViewReview"]
        self.assertIn("normal-view feature review missing", " ".join(qualify(case)["reasons"]))

    def test_hidden_contact_refused_despite_two_nonzero_highlights(self):
        case = self.fixture()
        case["normalViewReview"]["contactExposure"] = "occluded"
        result = qualify(case)
        self.assertEqual("usable", result["captureStatus"])
        self.assertEqual("refused", result["measurementStatus"])
        self.assertIn("not exposed", " ".join(result["reasons"]))

    def test_sky_only_refused_by_actual_signal(self):
        case = self.fixture()
        sky = np.full((100, 100, 3), 30, dtype=np.uint8)
        for role in ["a1", "a2", "b1", "b2"]:
            self.rewrite_image(case, role, sky)
        result = qualify(case)
        self.assertEqual("refused", result["measurementStatus"])
        self.assertIn("zero response", " ".join(result["reasons"]))

    def test_wrong_identity_receipt_refused(self):
        case = self.fixture()
        case["captures"]["a1"]["actualRendererArguments"][-1] = "c.esm=cyan"
        self.assertIn("wrong identity", " ".join(qualify(case)["reasons"]))

    def test_wrong_executable_refused_even_with_self_consistent_job_and_log(self):
        case = self.fixture()
        other = self.root / "other-viewprobe.exe"
        other.write_bytes(b"different renderer")
        case["captures"]["a1"]["actualRendererArguments"][0] = str(other)
        self.sync_invocation(case, "a1")
        self.assertIn("frozen source/render state", " ".join(qualify(case)["reasons"]))

    def test_extra_or_different_render_state_refused(self):
        case = self.fixture()
        command = case["captures"]["a1"]["actualRendererArguments"]
        command.insert(command.index("--ref"), "--fx")
        self.sync_invocation(case, "a1")
        self.assertIn("frozen source/render state", " ".join(qualify(case)["reasons"]))

        case = self.fixture()
        case["viewArgs"] += ["--cull-above", "10"]
        self.assertIn("override or remove required scene state", " ".join(qualify(case)["reasons"]))

    def test_translucent_surface_mark_is_not_opaque_identity_evidence(self):
        # A texture-preserving mark shows the subject's material through the colour. That is a
        # presentation choice for a human reader, not the opaque placed-triangle contribution this
        # gate qualifies, so it must be refused by the frozen-render-state check specifically --
        # not merely fall through to a command-mismatch reason that a re-frozen command would hide.
        case = self.fixture()
        case["viewArgs"] += ["--ref-surface-opacity", "0.5"]
        self.assertIn("override or remove required scene state", " ".join(qualify(case)["reasons"]))

    def test_candidate_hash_order_configuration_and_profile_are_bound(self):
        case = self.fixture()
        Path(case["candidate"]["candidatePath"]).write_text(json.dumps({"Mods": ["changed"]}), encoding="utf-8")
        self.assertIn("candidate identity changed", " ".join(qualify(case)["reasons"]))

        case = self.fixture()
        command = case["captures"]["a1"]["actualRendererArguments"]
        command[command.index("--order") + 1] = str(self.root / "other-candidate.json")
        self.sync_invocation(case, "a1")
        self.assertIn("frozen source/render state", " ".join(qualify(case)["reasons"]))

        case = self.fixture()
        command = case["captures"]["a1"]["actualRendererArguments"]
        command[command.index("--profile") + 1] = "other-profile"
        self.sync_invocation(case, "a1")
        self.assertIn("frozen source/render state", " ".join(qualify(case)["reasons"]))

        case = self.fixture()
        case["candidate"]["configurationFingerprint"] = "other-config"
        self.assertIn("candidate configuration differs", " ".join(qualify(case)["reasons"]))

    def test_candidate_requires_explicit_mods_list(self):
        case = self.fixture()
        candidate_path = Path(case["candidate"]["candidatePath"])
        candidate_path.write_text(json.dumps({"notMods": []}), encoding="utf-8")
        case["candidate"]["candidateSha256"] = sha(candidate_path)
        self.assertIn("explicit asset-provider list", " ".join(qualify(case)["reasons"]))

    def test_run_receipt_exactly_binds_actual_command(self):
        case = self.fixture()
        capture = case["captures"]["a1"]
        capture["job"]["argv"].append("--wrapper-added-state")
        log = Path(capture["log"]["path"])
        log.write_text("$ " + " ".join(capture["job"]["argv"]) + "\nsubject-surface-mode: exact placed opaque triangles", encoding="utf-8")
        capture["log"]["sha256"] = sha(log)
        self.assertIn("run receipt", " ".join(qualify(case)["reasons"]))

    def test_numeric_equivalent_fov_text_is_accepted(self):
        case = self.fixture()
        for key, capture in case["captures"].items():
            command = capture["actualRendererArguments"]
            command[command.index("--fov") + 1] = "90.0"
            self.sync_invocation(case, key)
        self.assertEqual("qualified", qualify(case)["measurementStatus"])

    def test_unrelated_repeat_variation_and_elsewhere_response_refused(self):
        case = self.fixture()
        image = np.full((100, 100, 3), 30, dtype=np.uint8)
        image[48, 48] = [80, 30, 30]
        self.rewrite_image(case, "normal2", image)
        result = qualify(case)
        self.assertIn("repeat variation", " ".join(result["reasons"]))
        self.rewrite_image(case, "elsewhere1", image)
        self.assertIn("known-elsewhere highlight changed", " ".join(qualify(case)["reasons"]))

    def test_declared_canvas_must_match_actual_raster_dimensions(self):
        case = self.fixture()
        for key in case["captures"]:
            image = np.full((200, 200, 3), 30, dtype=np.uint8)
            if key.startswith("a"):
                image[84:92, 84:92] = [0, 255, 255]
            elif key.startswith("b"):
                image[102:110, 102:110] = [255, 0, 255]
            self.rewrite_image(case, key, image)
        result = qualify(case)
        self.assertEqual("refused", result["measurementStatus"])
        self.assertIn("raster dimensions", " ".join(result["reasons"]))

    def test_minimum_signal_must_be_finite_and_at_least_two(self):
        for invalid in [float("nan"), float("inf"), float("-inf"), 1, None, "64"]:
            with self.subTest(invalid=invalid):
                case = self.fixture()
                case["signalPolicy"]["minimumPixels"] = invalid
                self.assertIn("detectable feature/sensitivity missing", " ".join(qualify(case)["reasons"]))

    def test_explicit_absence_and_unresolved_presence_remain_distinct_refusals(self):
        case = self.fixture()
        self.rewrite_source_record(case, "a", lambda record: record["fields"].update(deletion=True))
        result = qualify(case)
        self.assertEqual("expected_absent_or_excluded", result["participants"]["a"]["status"])
        self.assertIn("absent/excluded", " ".join(result["reasons"]))

        case = self.fixture()
        self.rewrite_source_record(case, "a", lambda record: record["fields"].update(
            resolvedEnable={"enabled": False, "resolved": False}))
        result = qualify(case)
        self.assertEqual("source_presence_unresolved", result["participants"]["a"]["status"])
        self.assertIn("presence is unresolved", " ".join(result["reasons"]))

    def test_same_picture_is_not_source_identity(self):
        case = self.fixture()
        case["a"]["fields"]["space"] = "another room"
        self.assertIn("record differs", " ".join(qualify(case)["reasons"]))

    def test_alpha_refusal_survives_visible_opaque_portion(self):
        case = self.fixture()
        case["captures"]["a1"]["surfaceRefusals"] = ["unsupported alpha shape"]
        self.assertEqual("refused", qualify(case)["measurementStatus"])

    def test_camera_proposal_never_becomes_qualification(self):
        contact = {"status": "complete", "value": {"envelopeMin": [-2, 10, -2], "envelopeMax": [2, 12, 2]}}
        with self.assertRaisesRegex(ValueError, "verified room position"):
            frame_contact(self.camera, contact, {"status": "planner_pass"})
        proposal = frame_contact(self.camera, contact, {"status": "verified", "evidence": "independent room observation"})
        self.assertEqual([0, 11, 0], proposal["look"])
        self.assertEqual(self.camera["eye"], proposal["eye"])
        self.assertEqual("proposed_requires_post_render_gate", proposal["planningStatus"])

    def test_missing_partial_or_camera_plane_envelope_refused(self):
        with self.assertRaises(ValueError):
            project_box(self.camera, [-1, -1, -1], [1, 1, 1], (100, 100))
        with self.assertRaises(ValueError):
            frame_contact(self.camera, {"status": "incomplete", "partialValue": {}}, {"status": "verified", "evidence": "room"})


if __name__ == "__main__":
    unittest.main()
