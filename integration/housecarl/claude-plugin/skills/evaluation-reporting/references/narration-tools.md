# Local narration choices

Class: LIVING. The preparation helper is `SW/integration/housecarl/evalspeech.py`; final assembly remains
`evalreel.py`, both in the SkyWright checkout `SW` (the skill's "Where the tools are"). Speech is generated separately and supplied through `narrationAudio`. Models and
their dependencies are optional installations, never bundled into or silently installed by the helper.

**Default: Kokoro, British George (`bm_george`), CPU, normal speed.** GPU acceleration is optional.
American Heart (`--voice af_heart`) remains an alternative; honor an existing listener preference.
Missing George weights or dependencies produce an explicit error, never a different narrator.

## Choose with samples

Survey current primary documentation before choosing: a familiar model name is not a comparison.
Check code, weights and selected voice terms separately, local device support, required references,
voice and delivery controls, and the exact installed API. Keep viable alternatives visible. Shortlist
a small set representing useful differences, then generate the **same diagnostic passage** with each:
mod names, exact numbers, a scene transition and a caveat. Retain WAVs and generation receipts.

Offer the listener meaningful choices in voice identity and delivery. A fast, compact model can be a
practical default without being the most comfortable voice. Published demos, benchmark rankings and
model size cannot determine the listener's preference. Keep original levels; if making loudness-matched
comparison copies, retain the originals and record the transform. Do not mistake louder for better.

Current candidates worth considering include:

| Option | What the user can choose | Setup and terms to check |
|---|---|---|
| [Kokoro](https://github.com/hexgrad/kokoro) | Preset identity, American/British English and speed; compact CPU/GPU model | Apache-2.0 code and [weights](https://huggingface.co/hexgrad/Kokoro-82M); Misaki, English spaCy data and working espeak-ng are required. |
| [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) | CustomVoice presets; the 1.7B version also takes delivery instructions | Apache-2.0 [weights](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice). GPU-oriented; isolate pinned Transformers dependencies. Base/cloning and VoiceDesign are distinct models. |
| [VoxCPM2](https://github.com/OpenBMB/VoxCPM) | Describe the narrator's voice and delivery in words, with optional reference-based workflows upstream | Apache-2.0 [weights](https://huggingface.co/openbmb/VoxCPM2); larger GPU model and dependencies. The helper uses description-only generation. |
| [KittenTTS](https://github.com/KittenML/KittenTTS) | Several stock voices and speed on CPU | Apache-2.0 code and mini-0.8 weights; ONNX and phonemizer dependencies. Preserve exact model version. |
| [Pocket TTS](https://github.com/kyutai-labs/pocket-tts) | Stock voices on CPU; optional cloning is a separate capability | MIT code, CC-BY-4.0 [stock-model weights](https://huggingface.co/kyutai/pocket-tts-without-voice-cloning); [voice references](https://huggingface.co/kyutai/tts-voices) have their own licenses. |
| [Piper](https://github.com/OHF-Voice/piper1-gpl) | Many stock voices with CPU generation | The maintained engine is GPL-3.0; each voice has a model card and separate terms. The old repository's MIT license does not describe the successor. |

Also inspect relevant alternatives such as Chatterbox variants, Soprano, F5-TTS, XTTS, MeloTTS,
StyleTTS2, Parler, CosyVoice, VibeVoice, Dia, Bark, OpenVoice and Fish releases rather than assuming
they are interchangeable. Reference-audio requirements, research/noncommercial terms, exact-script
deviation, single-voice limits or dependency incompatibility are explicit tradeoffs, not evidence of
inferior sound. A dated candidate survey belongs with the run; revisit the shortlist as tools change.

## Prepare and verify speech

Use an isolated environment. Download official weights separately at an immutable repository revision,
retain licenses and download provenance, and pass their **complete local directory** to the helper.
Qwen needs its `speech_tokenizer/` subtree. Kokoro needs `config.json`, `kokoro-v1_0.pth` and
`voices/VOICE.pt`. Install its `en_core_web_sm` language package before generation; a missing
phonemizer must refuse rather than silently lose unknown names. Inspect installed package source
when it differs from current web documentation.

The helper supports Kokoro 0.9.4, qwen-tts 0.1.1 and voxcpm 2.0.3 argument shapes. Its
engine choices are `kokoro` (default), `qwen3` and `voxcpm2`. Other tools can still provide recorded WAVs
through the existing `narrationAudio` surface. `--device cpu` is the helper default; request
`--device cuda:0` for an available GPU. An unavailable requested device never falls back silently.

For a machine without a supported GPU, keep Kokoro and the selected voice and use CPU generation.
GPU acceleration is optional; it is not a requirement for George, Heart or the other supported
English presets. Measure a short passage on the actual host before estimating a full narration.
Preserve voice choice when changing devices. If CPU speed or installation requirements are unsuitable,
offer the separately evaluated CPU engines (Kitten, Pocket or Piper) explicitly through external WAVs;
their adapters are not integrated into this helper. Do not silently substitute a different narrator.

```text
python SW/integration/housecarl/evalspeech.py --model-dir MODELS/kokoro --model-revision COMMIT_HASH --text-file diagnostic.txt --out OUT/sample
python SW/integration/housecarl/evalspeech.py --model-dir MODELS/kokoro --model-revision COMMIT_HASH --storyboard OUT/reviewed-storyboard.json --out OUT/speech
python SW/integration/housecarl/evalspeech.py --verify OUT/speech/narration-manifest.json
python SW/integration/housecarl/evalreel.py render --presentation OUT/presentation.json --storyboard OUT/speech/storyboard-with-audio.json --out OUT/reel
```

Use a real immutable commit hash for `COMMIT_HASH`. The manifest labels that revision as caller-supplied;
its file hashes identify the actual local bytes. Generation requires a **new output directory**.
It preserves the reviewed packet, omissions, exact narration and presentation identity, changing only
included `narrationAudio` paths. Those audio paths are absolute; relocating the files requires a new
binding. A partial failed generation has no completed manifest and must not be rendered as accepted output.

For Qwen, choose `--engine qwen3 --voice Ryan` (or another supported preset); optional `--style`
controls delivery on **1.7B CustomVoice only**. For VoxCPM2, choose `--engine voxcpm2 --style
"A calm adult narrator with clear measured delivery"`, without `--voice`. Both use their natural
generated timing; `--speed` is accepted only by Kokoro. VoxCPM 2.0.3 has no `generate(seed=...)`
parameter despite newer documentation: the helper seeds Python, NumPy and Torch, disables compilation,
text normalization and internal bad-case retries, and records the actual settings. Qwen uses SDPA
attention. Neither requires FlashAttention installation for this path.

Retain the helper manifest alongside the reel map. It records input, text, model/voice and WAV hashes,
model revision, package versions, device, settings, per-scene seed, duration and synthesis time.
Verification checks those source files and scene bindings. A seed and pinned files aid reproduction;
they do not promise byte-identical floating-point output across different machines and libraries.

## Word checks and listening are separate

[Whisper](https://github.com/openai/whisper) transcribes speech; it is not a text-to-speech engine.
It can compare generated narration with the intended text after synthesis. Run recognition locally
without an expected-text prompt or hotwords; retain raw transcripts, audio/model identities and
alignment differences. Report number formatting, punctuation and spelling normalization separately.
Never normalize away changed numbers, negation, caveats, names or missing clauses to improve a score.
An apparent mismatch may be a recognition error and needs listening before attributing it to the voice.

Review source identity, image agreement, decoded audio/video transport, recognized wording and listening
quality separately. Listen to complete narration where an audio-perception path is actually available;
playing a file to speakers is not the same as the agent hearing it. When it cannot inspect speech,
provide playable samples/video and leave comfort, pronunciation and pacing open for the listener.
Do not silently fall back to Windows speech when a chosen free voice fails.
