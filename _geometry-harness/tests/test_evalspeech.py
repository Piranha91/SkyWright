"""Narration preparation contracts, without model imports, weights or network access."""
import argparse
import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import os as _os_settings, tempfile as _tf_settings  # noqa: E402
# The evaluation tools read the saved evaluation settings; these known answers use the built-in defaults, never
# the user's own file (a path nothing writes).
_os_settings.environ["HOUSECARL_EVAL_SETTINGS"] = str(Path(_tf_settings.gettempdir()) / "hc-tests-no-eval-settings"
                                                   / "houseCARL.evaluation.json")
import evalspeech


class NarrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.model = self.root / "model"
        (self.model / "voices").mkdir(parents=True)
        (self.model / "config.json").write_text("{}")
        (self.model / "kokoro-v1_0.pth").write_bytes(b"fixture weights, never loaded")
        (self.model / "voices/af_heart.pt").write_bytes(b"fixture voice, never loaded")
        (self.model / "voices/bm_george.pt").write_bytes(b"fixture George voice, never loaded")
        self.packet = {
            "schemaVersion": 1, "presentationId": "same-reviewed-presentation",
            "capturePlan": {"requests": [{"id": "retain-this-request"}]},
            "scenes": [
                {"chapterId": "opening", "captionIndex": 0, "mediaId": "qualified-one",
                 "narration": "Exact verdict.\n  Second sentence. ", "storyboard": {"cleanFrame": True}},
                {"chapterId": "finding", "captionIndex": 0, "mediaId": "omitted-media",
                 "omitFromReel": True, "omissionReason": "disclosed duplicate", "narration": None,
                 "narrationAudio": "retained-omitted-input.wav"},
                {"chapterId": "closing", "captionIndex": 0, "mediaId": "qualified-one",
                 "narration": "Only this evidence was checked.", "narrationAudio": "previous.wav"},
            ]}
        self.source = self.root / "reviewed.json"
        self.source.write_text(json.dumps(self.packet), encoding="utf-8")
        self.args = argparse.Namespace(storyboard=str(self.source), text_file=None,
                                       engine="kokoro", model_dir=str(self.model),
                                       model_revision="a" * 40, voice="af_heart", style=None,
                                       device="cpu", speed=None, seed=42, out=str(self.root / "output"))
        self.spoken = []

    def loader(self, config, model):
        def synthesize(text):
            self.spoken.append(text)
            return [0., .2, -.2, .1] * 100, 24000, {"testDouble": True}
        return synthesize, {"actualDevice": "cpu", "packages": {"test-double": "1"}}

    def prepare(self, loader=None):
        return evalspeech.prepare(self.args, Mock(), loader=loader or self.loader)

    def manifest_path(self):
        return Path(self.args.out) / "narration-manifest.json"

    def test_complete_packet_preserves_identity_omissions_and_exact_text(self):
        original_bytes = self.source.read_bytes()
        result = self.prepare()
        expected = copy.deepcopy(self.packet)
        for row in result["scenes"]:
            scene = next(s for s in expected["scenes"] if s["chapterId"] == row["chapterId"])
            scene["narrationAudio"] = row["audioPath"]
            self.assertTrue(Path(row["audioPath"]).is_absolute())
            self.assertEqual(row["textSha256"], evalspeech.sha_text(scene["narration"]))
        self.assertEqual(evalspeech.read_json(result["storyboard"]["path"]), expected)
        self.assertEqual(self.spoken, [self.packet["scenes"][i]["narration"] for i in (0, 2)])
        self.assertEqual(self.source.read_bytes(), original_bytes)
        self.assertEqual(evalspeech.verify_manifest(self.manifest_path()), result)
        self.assertIn("open", result["listeningReview"])

    def test_sample_preserves_every_character(self):
        text = self.root / "sample.txt"
        text.write_text("  Triangle 94.\nA caveat.\n", encoding="utf-8")
        self.args.storyboard, self.args.text_file = None, str(text)
        result = self.prepare()
        self.assertEqual(self.spoken, [text.read_text()])
        self.assertIsNone(result["storyboard"])
        evalspeech.verify_manifest(self.manifest_path())

    def test_repeated_media_binds_each_occurrence_to_its_own_narration(self):
        first = self.packet["scenes"][0]
        self.packet["scenes"][0:1] = [
            {**copy.deepcopy(first), "occurrenceIndex": n, "narration": text}
            for n, text in enumerate(("Highlighted objects.", "Normal appearance.", "The same highlights return."))]
        self.source.write_text(json.dumps(self.packet))
        result = self.prepare()
        bound = evalspeech.read_json(result["storyboard"]["path"])
        self.assertEqual([r["occurrenceIndex"] for r in result["scenes"][:3]], [0, 1, 2])
        self.assertEqual(len({r["audioPath"] for r in result["scenes"][:3]}), 3)
        self.assertEqual([r["narrationAudio"] for r in bound["scenes"][:3]],
                         [r["audioPath"] for r in result["scenes"][:3]])
        self.assertEqual(self.spoken[:3], [r["narration"] for r in self.packet["scenes"][:3]])
        self.assertEqual(evalspeech.verify_manifest(self.manifest_path()), result)
        result["scenes"][2]["occurrenceIndex"] = 0
        self.manifest_path().write_text(json.dumps(result))
        with self.assertRaisesRegex(evalspeech.Refused, "binding"):
            evalspeech.verify_manifest(self.manifest_path())

    def test_gapped_and_invalid_occurrences_refused_before_loading_model(self):
        for occurrence in (2, -1, True, 1.5):
            with self.subTest(occurrence=occurrence):
                self.packet["scenes"][0]["occurrenceIndex"] = occurrence
                self.source.write_text(json.dumps(self.packet))
                loader = Mock(side_effect=AssertionError("model must not load"))
                with self.assertRaisesRegex(evalspeech.Refused, "occurrenceIndex"):
                    self.prepare(loader)
                loader.assert_not_called()
                self.assertFalse(Path(self.args.out).exists())

    def test_legacy_manifest_without_occurrence_indices_still_verifies(self):
        result = self.prepare()
        for row in result["scenes"]:
            row.pop("occurrenceIndex")
        self.manifest_path().write_text(json.dumps(result))
        self.assertEqual(evalspeech.verify_manifest(self.manifest_path()), result)
        result["scenes"][0]["occurrenceIndex"] = False
        self.manifest_path().write_text(json.dumps(result))
        with self.assertRaisesRegex(evalspeech.Refused, "occurrenceIndex"):
            evalspeech.verify_manifest(self.manifest_path())

    def test_missing_source_never_loads_model_or_creates_output(self):
        self.source.unlink()
        loader = Mock(side_effect=AssertionError("model load should not happen"))
        with self.assertRaisesRegex(evalspeech.Refused, "missing"):
            self.prepare(loader)
        loader.assert_not_called()
        self.assertFalse(Path(self.args.out).exists())

    def test_source_altered_during_generation_never_publishes_manifest(self):
        def loader(config, model):
            def synthesize(text):
                self.source.write_text("{}")
                return [0., .2, -.2], 24000, {}
            return synthesize, {}
        with self.assertRaisesRegex(evalspeech.Refused, "altered"):
            self.prepare(loader)
        self.assertFalse(self.manifest_path().exists())

    def test_changed_or_missing_audio_and_source_refused(self):
        result = self.prepare()
        audio = Path(result["scenes"][0]["audioPath"])
        original = audio.read_bytes()
        audio.write_bytes(original + b"changed")
        with self.assertRaisesRegex(evalspeech.Refused, "altered"):
            evalspeech.verify_manifest(self.manifest_path())
        audio.write_bytes(original)
        self.source.unlink()
        with self.assertRaisesRegex(evalspeech.Refused, "missing"):
            evalspeech.verify_manifest(self.manifest_path())

    def test_model_bytes_changed_refused(self):
        self.prepare()
        (self.model / "voices/af_heart.pt").write_bytes(b"changed voice")
        with self.assertRaisesRegex(evalspeech.Refused, "altered"):
            evalspeech.verify_manifest(self.manifest_path())

    def test_storyboard_extra_change_rejected_even_with_updated_file_hash(self):
        result = self.prepare()
        bound = Path(result["storyboard"]["path"])
        packet = evalspeech.read_json(bound)
        packet["scenes"][1]["omissionReason"] = "new unsupported omission"
        bound.write_text(json.dumps(packet))
        result["storyboard"]["sha256"] = evalspeech.sha_file(bound)
        self.manifest_path().write_text(json.dumps(result))
        with self.assertRaisesRegex(evalspeech.Refused, "changed more than"):
            evalspeech.verify_manifest(self.manifest_path())

    def test_existing_directory_and_source_paths_protected(self):
        self.prepare()
        original = {p.name: p.read_bytes() for p in Path(self.args.out).iterdir() if p.is_file()}
        with self.assertRaisesRegex(evalspeech.Refused, "already exists"):
            self.prepare()
        self.assertEqual({p.name: p.read_bytes() for p in Path(self.args.out).iterdir() if p.is_file()}, original)
        self.args.out = str(self.source)
        with self.assertRaisesRegex(evalspeech.Refused, "already exists"):
            self.prepare()

    def test_arbitrary_scene_names_never_become_output_paths(self):
        self.packet["scenes"][0]["chapterId"] = "../../outside"
        self.source.write_text(json.dumps(self.packet))
        result = self.prepare()
        self.assertEqual(len({row["audioPath"] for row in result["scenes"]}), 2)
        self.assertTrue(all(Path(row["audioPath"]).parent == Path(self.args.out) for row in result["scenes"]))

    def test_duplicate_scene_identity_refused(self):
        self.packet["scenes"].append(copy.deepcopy(self.packet["scenes"][0]))
        self.source.write_text(json.dumps(self.packet))
        with self.assertRaisesRegex(evalspeech.Refused, "duplicate"):
            self.prepare()

    def test_nonfinite_empty_silent_multichannel_and_invalid_rate_refused(self):
        cases = [([], 24000), ([0., 0.], 24000), ([.5, .5], 24000),
                 ([float("nan"), .2], 24000), ([float("inf"), .2], 24000),
                 ([[.2], [-.2]], 24000), ([.2, -.2], float("nan")), ([.2, -.2], True),
                 ([1.2, -.2], 24000), ([1e-9, -1e-9], 24000)]
        for index, (waveform, rate) in enumerate(cases):
            with self.subTest(index=index), self.assertRaises(evalspeech.Refused):
                evalspeech.write_wave(self.root / f"bad-{index}.wav", waveform, rate)
        self.assertFalse(list(self.root.glob("bad-*.wav")))

    def test_invalid_engine_voice_style_speed_and_revision_refused(self):
        cases = [("engine", None), ("engine", "windows"), ("voice", ""),
                 ("style", "ignored?"), ("speed", float("nan")), ("device", "auto"),
                 ("model_revision", "main")]
        for field, value in cases:
            with self.subTest(field=field, value=value):
                args = copy.copy(self.args)
                setattr(args, field, value)
                with self.assertRaises(evalspeech.Refused):
                    evalspeech.settings(args)
        self.args.engine, self.args.speed = "qwen3", 1.2
        with self.assertRaisesRegex(evalspeech.Refused, "only by Kokoro"):
            evalspeech.settings(self.args)
        self.args.voice, self.args.speed = None, None
        with self.assertRaisesRegex(evalspeech.Refused, "explicit --voice"):
            evalspeech.settings(self.args)

    def test_default_generation_binds_george_cpu_and_never_substitutes_heart(self):
        self.args = evalspeech.parser().parse_args([
            "--storyboard", str(self.source), "--model-dir", str(self.model),
            "--model-revision", "a" * 40, "--out", str(self.root / "default-output")])
        result = self.prepare()
        self.assertEqual((result["settings"]["engine"], result["settings"]["voice"],
                          result["settings"]["requestedDevice"]), ("kokoro", "bm_george", "cpu"))
        self.assertEqual(evalspeech.verify_manifest(self.manifest_path()), result)
        # Heart is present, but missing George must fail before loading any model.
        (self.model / "voices/bm_george.pt").unlink()
        self.args.out = str(self.root / "missing-george")
        loader = Mock(side_effect=AssertionError("must not substitute another voice"))
        with self.assertRaisesRegex(evalspeech.Refused, "bm_george"):
            self.prepare(loader)
        loader.assert_not_called()

    def test_dependency_failure_is_clear_no_traceback_no_fallback(self):
        with patch.object(evalspeech, "Run", return_value=Mock()) as run_type:
            run_type.return_value.__enter__ = Mock(return_value=Mock())
            run_type.return_value.__exit__ = Mock(return_value=False)
            stderr = io.StringIO()
            with patch.object(evalspeech, "prepare", side_effect=ModuleNotFoundError("No module named 'kokoro'")) as prep:
                with contextlib.redirect_stderr(stderr):
                    code = evalspeech.main(["--text-file", "sample.txt", "--out", "new", "--engine", "kokoro"])
            self.assertEqual(code, 2)
            self.assertEqual(prep.call_count, 1)
            self.assertIn("kokoro", stderr.getvalue())
            self.assertIn("no fallback", stderr.getvalue())
            self.assertNotIn("Traceback", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
