#!/usr/bin/env python
"""Prepare local narration WAVs for evalreel's existing narrationAudio interface.

Defaults to Kokoro's British George voice on CPU; other engines, voices and CUDA remain
explicit choices. Supply a locally installed model. This command never installs
packages or downloads weights. --out must be new; a manifest is published only after
every included scene succeeds. Transport validation is not listening acceptance.

APIs checked against kokoro 0.9.4, qwen-tts 0.1.1 and voxcpm 2.0.3 source:
https://github.com/hexgrad/kokoro
https://github.com/QwenLM/Qwen3-TTS
https://github.com/OpenBMB/VoxCPM
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import sys
import time
import wave

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

ENGINES = ("kokoro", "qwen3", "voxcpm2")


class Refused(ValueError):
    """A missing or invalid prerequisite; never substitute another engine."""


def sha_file(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def sha_text(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sha_object(obj):
    return sha_text(json.dumps(obj, sort_keys=True, ensure_ascii=False, allow_nan=False))


def read_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise Refused(f"cannot read JSON {path}: {exc}") from exc


def write_json(path, value):
    # Exclusive creation also protects files inside our reserved output directory.
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n")


def checked_file(path):
    path = Path(path).resolve()
    if not path.is_file():
        raise Refused(f"source file is missing: {path}")
    return {"path": str(path), "sha256": sha_file(path)}


def unchanged(identity):
    path = Path(identity["path"])
    if not path.is_file() or sha_file(path) != identity["sha256"]:
        raise Refused(f"source is missing or altered: {path}")


def read_source(args):
    source = checked_file(args.storyboard or args.text_file)
    if args.text_file:
        text = Path(source["path"]).read_text(encoding="utf-8-sig")
        if not text.strip():
            raise Refused("sample text is empty")
        source["kind"] = "text"
        return source, None, [{"chapterId": "sample", "captionIndex": 0, "narration": text}]
    source["kind"] = "storyboard"
    packet = read_json(source["path"])
    if not isinstance(packet, dict) or packet.get("schemaVersion") != 1:
        raise Refused("storyboard must be a schemaVersion 1 packet")
    if not isinstance(packet.get("presentationId"), str) or not packet["presentationId"].strip():
        raise Refused("storyboard needs its original presentationId")
    if not isinstance(packet.get("scenes"), list) or not packet["scenes"]:
        raise Refused("storyboard scenes must be a nonempty list")
    included, seen = [], set()
    for row in packet["scenes"]:
        if not isinstance(row, dict):
            raise Refused("storyboard scene must be an object")
        chapter, index = row.get("chapterId"), row.get("captionIndex")
        if not isinstance(chapter, str) or not chapter.strip() or type(index) is not int or index < 0:
            raise Refused("scene needs chapterId and a nonnegative integer captionIndex")
        key = (chapter, index)
        if key in seen:
            raise Refused(f"duplicate scene identity: {chapter}[{index}]")
        seen.add(key)
        if "omitFromReel" in row and type(row["omitFromReel"]) is not bool:
            raise Refused(f"omitFromReel must be boolean: {chapter}[{index}]")
        if row.get("omitFromReel") is True:
            if not isinstance(row.get("omissionReason"), str) or not row["omissionReason"].strip():
                raise Refused(f"omitted scene needs omissionReason: {chapter}[{index}]")
            continue
        if not isinstance(row.get("narration"), str) or not row["narration"].strip():
            raise Refused(f"included scene needs narration: {chapter}[{index}]")
        included.append(row)
    if not included:
        raise Refused("storyboard has no included narration")
    unchanged(source)
    return source, packet, included


def settings(args):
    if args.engine not in ENGINES:
        raise Refused("--engine must be kokoro, qwen3 or voxcpm2")
    if not args.model_dir or not args.model_revision:
        raise Refused("--model-dir and --model-revision are required; download and identify the model separately")
    if not re.fullmatch(r"[a-fA-F0-9]{40,64}", args.model_revision):
        raise Refused("--model-revision must be the immutable 40-64 digit repository commit hash")
    if not re.fullmatch(r"cpu|cuda(?::\d+)?", args.device):
        raise Refused("--device must be cpu, cuda or cuda:N; automatic device fallback is disabled")
    if not 0 <= args.seed <= 2**32 - 1:
        raise Refused("--seed must be between 0 and 4294967295")
    voice = "bm_george" if args.engine == "kokoro" and args.voice is None else args.voice
    if args.engine == "voxcpm2":
        if voice or not args.style or not args.style.strip():
            raise Refused("voxcpm2 requires --style (voice description), and does not accept --voice")
        if any(char in args.style for char in "()\r\n"):
            raise Refused("VoxCPM2 style must be one description without parentheses or line breaks")
    elif not voice or not voice.strip():
        raise Refused(f"{args.engine} requires an explicit --voice")
    if args.engine == "kokoro":
        if not re.fullmatch(r"[ab][fm]_[a-z0-9_]+", voice):
            raise Refused("Kokoro currently supports a named American/British English voice (for example af_heart or bm_george)")
        if args.style:
            raise Refused("Kokoro does not accept --style; choose --voice and optional --speed")
    if args.speed is not None and (args.engine != "kokoro" or not math.isfinite(args.speed) or args.speed <= 0):
        raise Refused("a positive finite --speed is supported only by Kokoro; no artificial speed control is applied")
    return {"engine": args.engine, "voice": voice, "style": args.style,
            "speed": (args.speed if args.speed is not None else 1.0) if args.engine == "kokoro" else None,
            "seed": args.seed, "requestedDevice": args.device, "language": "English",
            "modelRevision": args.model_revision.lower()}


def model_identity(args, config):
    directory = Path(args.model_dir).resolve()
    if not directory.is_dir():
        raise Refused(f"local model directory is missing: {directory}")
    model_config = read_json(directory / "config.json")
    if not isinstance(model_config, dict):
        raise Refused("model config.json must be an object")
    if config["engine"] == "kokoro":
        for name in ("kokoro-v1_0.pth", f"voices/{config['voice']}.pt"):
            if not (directory / name).is_file():
                raise Refused(f"Kokoro local model/voice file is missing: {directory / name}")
    elif config["engine"] == "qwen3":
        if model_config.get("tts_model_type") != "custom_voice":
            raise Refused("qwen3 needs a Qwen3-TTS CustomVoice model, not Base or VoiceDesign")
        if config["style"] and model_config.get("tts_model_size") != "1b7":
            raise Refused("Qwen style instructions require the 1.7B CustomVoice model; 0.6B ignores them")
    elif model_config.get("architecture", "").lower() != "voxcpm2":
        raise Refused("voxcpm2 needs a VoxCPM2 model directory (config architecture voxcpm2)")
    files = []
    for path in sorted(directory.rglob("*")):
        relative = path.relative_to(directory)
        if path.is_file() and not any(part in (".git", ".cache") for part in relative.parts):
            files.append({"path": relative.as_posix(), "bytes": path.stat().st_size, "sha256": sha_file(path)})
    if not any(Path(item["path"]).suffix in (".pth", ".pt", ".bin", ".safetensors") for item in files):
        raise Refused("local model directory contains no model weights")
    return {"directory": str(directory), "revision": config["modelRevision"],
            "revisionSource": "caller-supplied immutable repository revision; file hashes identify actual local bytes",
            "files": files, "filesSha256": sha_object(files)}


def load_engine(config, identity):
    # Set before importing libraries: a missing local prerequisite must fail offline.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import numpy as np
    import torch

    requested = config["requestedDevice"]
    device = torch.device(requested)
    if device.type == "cuda":
        if not torch.cuda.is_available():
            raise Refused(f"requested device {requested} is unavailable; choose --device cpu explicitly if wanted")
        index = device.index if device.index is not None else torch.cuda.current_device()
        if index >= torch.cuda.device_count():
            raise Refused(f"requested CUDA device {index} does not exist")
        device = torch.device(f"cuda:{index}")
    directory = Path(identity["directory"])
    packages = ["numpy", "torch"]
    runtime = {"python": platform.python_version(), "requestedDevice": requested}

    if config["engine"] == "kokoro":
        import spacy
        if not spacy.util.is_package("en_core_web_sm"):
            raise Refused("Kokoro needs the installed en_core_web_sm spaCy package; install it separately (automatic installation is disabled)")
        from misaki.espeak import EspeakFallback
        EspeakFallback(british=config["voice"].startswith("b"))
        from kokoro import KModel, KPipeline
        model = KModel(repo_id="hexgrad/Kokoro-82M", config=str(directory / "config.json"),
                       model=str(directory / "kokoro-v1_0.pth")).to(device).eval()
        pipeline = KPipeline(lang_code=config["voice"][0], repo_id="hexgrad/Kokoro-82M", model=model)
        if pipeline.g2p.fallback is None:
            raise Refused("Kokoro eSpeak fallback is unavailable; unknown words could otherwise disappear")
        voice = torch.load(directory / "voices" / f"{config['voice']}.pt", map_location="cpu", weights_only=True)
        runtime.update(actualDevice=str(model.device), dtype=str(next(model.parameters()).dtype))
        runtime["voiceSha256"] = sha_file(directory / "voices" / f"{config['voice']}.pt")
        packages += ["kokoro", "misaki", "spacy", "en-core-web-sm", "phonemizer", "phonemizer-fork", "espeakng-loader"]

        def infer(text):
            chunks, phonemes = [], []
            for result in pipeline(text, voice=voice, speed=config["speed"], split_pattern=r"\n+"):
                _, ps, audio = result
                if audio is None:
                    raise Refused("Kokoro returned a segment without audio")
                chunks.append(audio.detach().cpu().numpy())
                phonemes.append(ps)
            if not chunks:
                raise Refused("Kokoro returned no speech segments")
            return np.concatenate(chunks), 24000, {"phonemes": phonemes}
    elif config["engine"] == "qwen3":
        from qwen_tts import Qwen3TTSModel
        dtype = torch.bfloat16 if device.type == "cuda" else torch.float32
        model = Qwen3TTSModel.from_pretrained(str(directory), device_map=str(device), dtype=dtype,
                                            attn_implementation="sdpa", local_files_only=True)
        voices = model.get_supported_speakers() or []
        if config["voice"].lower() not in [voice.lower() for voice in voices]:
            raise Refused(f"Qwen voice {config['voice']!r} is unavailable; supported voices: {', '.join(voices)}")
        runtime.update(actualDevice=str(model.device), dtype=str(dtype), attention="sdpa",
                       generationDefaults=model.generate_defaults)
        packages += ["qwen-tts", "transformers", "tokenizers", "accelerate", "safetensors"]

        def infer(text):
            wavs, rate = model.generate_custom_voice(text=text, speaker=config["voice"], language="English",
                                                    instruct=config["style"] or "", non_streaming_mode=True)
            if len(wavs) != 1:
                raise Refused(f"Qwen returned {len(wavs)} outputs for one scene")
            return wavs[0], rate, {}
    else:
        from voxcpm import VoxCPM
        model = VoxCPM.from_pretrained(str(directory), load_denoiser=False, local_files_only=True,
                                      optimize=False, device=str(device))
        runtime.update(actualDevice=str(model.tts_model.device), dtype=str(model.tts_model.config.dtype),
                       generation={"cfg_value": 2.0, "inference_timesteps": 10, "normalize": False,
                                   "denoise": False, "retry_badcase": False, "optimize": False})
        packages += ["voxcpm", "transformers", "tokenizers", "safetensors", "librosa"]

        def infer(text):
            model_input = f"({config['style']}){text}"
            wav = model.generate(text=model_input, cfg_value=2.0, inference_timesteps=10,
                                 normalize=False, denoise=False, retry_badcase=False)
            return wav, model.tts_model.sample_rate, {"modelInput": model_input}
    if torch.device(runtime["actualDevice"]) != device:
        raise Refused(f"engine used {runtime['actualDevice']} instead of requested {device}")
    runtime["deviceName"] = torch.cuda.get_device_name(device) if device.type == "cuda" else platform.processor()
    runtime["packages"] = {}
    for package in packages:
        try:
            runtime["packages"][package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            runtime["packages"][package] = "not installed"

    def synthesize(text):
        random.seed(config["seed"])
        np.random.seed(config["seed"])
        torch.manual_seed(config["seed"])
        if device.type == "cuda":
            torch.cuda.manual_seed_all(config["seed"])
        with torch.inference_mode():
            return infer(text)
    return synthesize, runtime


def write_wave(path, samples, rate):
    import numpy as np
    array = np.asarray(samples)
    if array.ndim != 1 or not array.size or array.dtype.kind not in "fiu":
        raise Refused("engine waveform must be a nonempty one-dimensional numeric array")
    if isinstance(rate, bool) or not isinstance(rate, (int, np.integer)) or not 8000 <= rate <= 192000:
        raise Refused("engine sample rate must be an integer between 8000 and 192000 Hz")
    if not np.isfinite(array).all():
        raise Refused("engine waveform contains non-finite samples")
    peak = float(np.max(np.abs(array.astype(np.float64))))
    if peak > 1:
        raise Refused(f"engine waveform exceeds PCM range (peak {peak}); no silent clipping is applied")
    pcm = np.rint(array.astype(np.float64) * 32767).astype("<i2")
    if not np.any(pcm) or float(np.std(pcm.astype(np.float64))) == 0:
        raise Refused("engine waveform is silent or constant after PCM conversion")
    with Path(path).open("xb") as stream:
        with wave.open(stream, "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(int(rate))
            audio.writeframes(pcm.tobytes())
    return {"sampleRate": int(rate), "frames": len(pcm), "channels": 1,
            "durationSeconds": len(pcm) / int(rate), "peakBeforePcm": peak,
            "rmsBeforePcm": float(np.sqrt(np.mean(array.astype(np.float64) ** 2))),
            "sha256": sha_file(path)}


def prepare(args, run, loader=load_engine):
    out = Path(args.out).resolve()
    if out.exists():
        raise Refused(f"output already exists; select a fresh --out directory: {out}")
    run.stage("prepare", total=1, unit="inputs")
    config = settings(args)
    source, packet, scenes = read_source(args)
    identity = model_identity(args, config)
    if out.is_relative_to(Path(identity["directory"])):
        raise Refused("output must be outside the model directory; model files remain read-only")
    run.feature("engine", config["engine"])
    run.feature("scenes", len(scenes))
    run.advance()
    # Exclusive directory reservation prevents concurrent jobs or repeated names from
    # replacing an accepted output. No completed manifest exists on failure.
    out.mkdir(parents=True, exist_ok=False)
    run.stage("load-model", total=1, unit="models")
    synthesize, runtime = loader(config, identity)
    run.advance()
    bound = copy.deepcopy(packet)
    rows = []
    run.stage("speech", total=len(scenes), unit="scenes")
    for index, scene in enumerate(scenes):
        unchanged(source)
        text = scene["narration"]
        name = f"{index:03d}-{sha_object(scene)[:12]}.wav"
        started = time.monotonic()
        samples, rate, detail = synthesize(text)
        audio = write_wave(out / name, samples, rate)
        row = {"chapterId": scene["chapterId"], "captionIndex": scene["captionIndex"],
               "mediaId": scene.get("mediaId"), "sourceSceneSha256": sha_object(scene),
               "text": text, "textSha256": sha_text(text), "pronunciationChanges": [],
               "seed": config["seed"], "audioPath": str(out / name), "audio": audio,
               "synthesisSeconds": time.monotonic() - started, "engineDetail": detail}
        rows.append(row)
        if bound is not None:
            target = next(item for item in bound["scenes"] if
                          (item["chapterId"], item["captionIndex"]) == (scene["chapterId"], scene["captionIndex"]))
            target["narrationAudio"] = str(out / name)
        run.advance()
        run.artifact(str(out / name))
    unchanged(source)
    storyboard_identity = None
    if bound is not None:
        write_json(out / "storyboard-with-audio.json", bound)
        storyboard_identity = checked_file(out / "storyboard-with-audio.json")
    manifest = {"schemaVersion": 1, "status": "synthesized", "source": source,
                "presentationId": packet.get("presentationId") if packet else None,
                "settings": config, "settingsSha256": sha_object(config), "model": identity,
                "runtime": runtime, "scenes": rows, "storyboard": storyboard_identity,
                "preparer": checked_file(Path(__file__)),
                "listeningReview": "open: waveform validation does not establish intelligibility or comfort",
                "reproducibility": "seed and local bytes recorded; floating-point synthesis may vary across hardware"}
    pending = out / ".narration-manifest.pending.json"
    write_json(pending, manifest)
    pending.rename(out / "narration-manifest.json")
    run.artifact(str(out / "narration-manifest.json"))
    run.tally(ok=len(rows))
    return manifest


def verify_manifest(path):
    manifest = read_json(path)
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1 or manifest.get("status") != "synthesized":
        raise Refused("not a completed narration manifest")
    unchanged(manifest["source"])
    if sha_object(manifest["settings"]) != manifest["settingsSha256"]:
        raise Refused("narration settings have changed")
    model = manifest["model"]
    if sha_object(model["files"]) != model["filesSha256"]:
        raise Refused("model file inventory has changed")
    for item in model["files"]:
        unchanged({"path": str(Path(model["directory"]) / item["path"]), "sha256": item["sha256"]})
    source = manifest["source"]
    args = argparse.Namespace(storyboard=source["path"] if source["kind"] == "storyboard" else None,
                              text_file=source["path"] if source["kind"] == "text" else None)
    _, packet, scenes = read_source(args)
    if len(scenes) != len(manifest["scenes"]):
        raise Refused("manifest scene count differs from source")
    expected = copy.deepcopy(packet)
    for scene, result in zip(scenes, manifest["scenes"]):
        if (sha_object(scene) != result["sourceSceneSha256"] or scene["narration"] != result["text"]
                or sha_text(result["text"]) != result["textSha256"]
                or (scene["chapterId"], scene["captionIndex"], scene.get("mediaId")) !=
                   (result["chapterId"], result["captionIndex"], result["mediaId"])):
            raise Refused("narration scene binding or text differs from source")
        unchanged({"path": result["audioPath"], "sha256": result["audio"]["sha256"]})
        with wave.open(result["audioPath"], "rb") as audio:
            if (audio.getnframes(), audio.getframerate(), audio.getnchannels(), audio.getsampwidth()) != (
                    result["audio"]["frames"], result["audio"]["sampleRate"], 1, 2):
                raise Refused("WAV transport differs from the manifest")
        if expected:
            target = next(item for item in expected["scenes"] if
                          (item["chapterId"], item["captionIndex"]) == (scene["chapterId"], scene["captionIndex"]))
            target["narrationAudio"] = result["audioPath"]
    if expected:
        unchanged(manifest["storyboard"])
        if read_json(manifest["storyboard"]["path"]) != expected:
            raise Refused("bound storyboard changed more than included narrationAudio paths")
    return manifest


def parser():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = p.add_mutually_exclusive_group(required=True)
    source.add_argument("--text-file", help="UTF-8 sample script; synthesized verbatim")
    source.add_argument("--storyboard", help="reviewed evalreel scene packet; narration preserved verbatim")
    source.add_argument("--verify", metavar="MANIFEST", help="verify sources, local model bytes, scene bindings and WAVs without loading a model")
    p.add_argument("--engine", choices=ENGINES, default="kokoro", help="default: kokoro; no automatic fallback")
    p.add_argument("--model-dir", help="already downloaded local model with config and weights")
    p.add_argument("--model-revision", help="immutable repository commit hash for those local weights")
    p.add_argument("--voice", help="Kokoro voice (default: bm_george), or required Qwen CustomVoice speaker")
    p.add_argument("--style", help="VoxCPM2 voice description, or optional Qwen 1.7B delivery instruction")
    p.add_argument("--speed", type=float, help="Kokoro speed multiplier only; default 1")
    p.add_argument("--seed", type=int, default=42, help="seed reset per scene (default 42)")
    p.add_argument("--device", default="cpu", help="cpu (default), cuda or cuda:N; no automatic fallback")
    p.add_argument("--out", help="new output directory; existing directories are refused")
    return p


def main(argv=None):
    args = parser().parse_args(argv)
    with Run("evalspeech", mode="verify" if args.verify else "prepare", stage_source="counted") as run:
        try:
            if args.verify:
                run.stage("verify", total=1, unit="manifests")
                result = verify_manifest(args.verify)
                run.advance()
                print(f"verified {len(result['scenes'])} narration WAV(s); listening review remains separate")
            else:
                if not args.out:
                    raise Refused("generation requires a new --out directory")
                result = prepare(args, run)
                print(f"prepared {len(result['scenes'])} narration WAV(s): {Path(args.out).resolve()}")
            return 0
        except (Refused, FileExistsError) as exc:
            run.refuse(str(exc), 2)
            print(f"narration-refused: {exc}", file=sys.stderr)
            return 2
        except ImportError as exc:
            message = f"missing/incompatible local dependency: {exc}; install prerequisites separately; no fallback used"
            run.refuse(message, 2)
            print(f"narration-refused: {message}", file=sys.stderr)
            return 2
        except Exception as exc:
            # External model libraries expose many error classes. Preserve the type and
            # reason, exit unsuccessfully, and leave partial WAVs without a completion manifest.
            message = f"{type(exc).__name__}: {exc}"
            run.fail(message, 1)
            print(f"narration-failed: {message}", file=sys.stderr)
            return 1


if __name__ == "__main__":
    raise SystemExit(main())
