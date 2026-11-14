#!/usr/bin/env python
"""evalreel — the diagnostic reel of an evaluation presentation (Q45, D6).

    python _geometry-harness/evalreel.py render --presentation OUT/presentation.json --out OUT/reel
        [--max-seconds 120] [--chapters C-01,C-03] [--bundle DIR] [--ffmpeg EXE] [--dry-run]

Consumes the presentation plan (standards/HOUSECARL_EVALUATION_PRESENTATION.md, schema 1) and cuts a 1280x720,
30 fps, H.264/AAC narrated reel. Every scene uses qualified evidence media at full-frame scale, and any crop,
pan, or highlight comes from explicit `storyboard` metadata. The opening speaks the overall verdict. There are
no title cards, caption bands, subtitles, or other added text. Missing media, narration, or clean-frame
declarations refuse the render instead of silently substituting a card.

Layout and reviewed storyboard input are documented in
`.claude/skills/evaluation-reporting/references/reel-layout.md`. The scene fills the canvas with its aspect
ratio preserved; only declared crops, pans and subject highlights alter its framing.

Outputs under --out: `reel.mp4`, `reel.srt`, `reel-map.json`, `reel-status.json`, `contact-sheet.png`, and a
per-segment cache under `cache/` keyed by the source's hash, the segment's parameters, the caption text and the
ffmpeg version — a changed input re-renders only its segment. A process exit is never taken as success: the
reel is decoded and its frame count compared with the plan before `status: rendered` is written.

ffmpeg is resolved in this order: `--ffmpeg`, `HOUSECARL_FFMPEG`, `DBH_FFMPEG`, PATH, then the copies some
installed applications bundle (ShareX; winget's Gyan.FFmpeg; a plain Program Files\\ffmpeg) — the same order as
the DevBench harness's `ffmpeg_find`. Operation shapes (dry run, probe-then-verify, a fingerprinted segment
cache, a contact sheet, slideshow / pad / concat) are adapted from the MIT-licensed kajisho5/ffmpeg-skill and
n0an/ffmpeg-skill recipes; nothing of theirs is copied.

Exit codes: 0 rendered; 1 partial or failed (the status file says which and why); 2 the input was refused.
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import glob
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402
sys.path.insert(0, str(HERE))
import evalsettings  # noqa: E402
from report_scenes import capture_requests

EXIT_OK, EXIT_NOT_RENDERED, EXIT_REFUSED = 0, 1, 2

# ---------------------------------------------------------------------------------------------- the layout
LAYOUT_VERSION = "2-narrated-clean-frame"
WIDTH, HEIGHT, FPS = 1280, 720, 30
IMAGE_H = HEIGHT                   # retained in output schema: the visual occupies the full frame
BAND_H = 0
CANVAS_COLOUR = "0x0f1218"         # behind a letterboxed source
IMAGE_SECONDS = 5.0
IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"}
DURATION_TOLERANCE = 0.1

# ASS colours are &HAABBGGRR
ASS_STYLES = (
    # name, font, size, primary, outline colour, bold, border style (1 outline, 3 opaque box), outline px,
    # alignment (numpad: 7 top-left, 2 bottom-centre), marginL, marginR, marginV
    ("Sheet", "Arial", 14, "&H00FFFFFF", "&H00000000", 1, 3, 2, 7, 4, 4, 4),
)


def utcnow() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_text(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def write_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(obj, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def load_json(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


# ---------------------------------------------------------------------------------------------- ffmpeg
def ffmpeg_find(explicit: str | None = None, explicit_how: str = "--ffmpeg") -> dict | None:
    """D6's resolution order (the DevBench harness's `ffmpeg_find` order, restated here rather than imported
    from the skill script): the flag, HOUSECARL_FFMPEG, DBH_FFMPEG, PATH, then the bundled copies."""
    for cand, how in ((explicit, explicit_how), (os.environ.get("HOUSECARL_FFMPEG"), "HOUSECARL_FFMPEG"),
                      (os.environ.get("DBH_FFMPEG"), "DBH_FFMPEG")):
        if cand and os.path.isfile(cand):
            return {"exe": os.path.abspath(cand), "how": how}
    w = shutil.which("ffmpeg")
    if w:
        return {"exe": w, "how": "PATH"}
    pf = os.environ.get("ProgramFiles", r"C:\Program Files")
    la = os.environ.get("LOCALAPPDATA", "")
    for pattern, how in (
            (os.path.join(pf, "ShareX", "ffmpeg.exe"), "bundled with ShareX"),
            (os.path.join(la, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg*", "*", "bin", "ffmpeg.exe"),
             "winget Gyan.FFmpeg"),
            (os.path.join(pf, "ffmpeg", "bin", "ffmpeg.exe"), "Program Files\\ffmpeg")):
        for cand in glob.glob(pattern):
            if os.path.isfile(cand):
                return {"exe": cand, "how": how}
    return None


def run_ffmpeg(exe: str, args: list[str], cwd: Path | None = None, timeout: int = 600) -> subprocess.CompletedProcess:
    return subprocess.run([exe, "-hide_banner", "-nostdin", "-y"] + args, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", cwd=str(cwd) if cwd else None, timeout=timeout)


def ffmpeg_identity(exe: str) -> dict:
    """Version, and whether the filters and the encoder the reel needs are compiled in."""
    v = subprocess.run([exe, "-version"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    m = re.search(r"ffmpeg version (\S+)", v.stdout + v.stderr)
    version = m.group(1) if m else "unknown"
    filters = subprocess.run([exe, "-hide_banner", "-filters"], capture_output=True, text=True,
                             encoding="utf-8", errors="replace").stdout
    have = {name for name in ("ass", "subtitles", "drawbox", "overlay", "scale", "fps", "tile")
            if re.search(rf"^\s*\S+\s+{name}\s+", filters, re.M)}
    encoders = subprocess.run([exe, "-hide_banner", "-encoders"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace").stdout
    return {"path": exe, "version": version, "filters": sorted(have),
            "libx264": bool(re.search(r"^\s*V\S*\s+libx264\s", encoders, re.M))}


_DUR = re.compile(r"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")
_SIZE = re.compile(r"[ ,](\d{2,5})x(\d{2,5})(?=[ ,\[]|$)", re.M)
_FPS = re.compile(r"(\d+(?:\.\d+)?) fps")


def probe_header(exe: str, path: Path) -> dict:
    """What `ffmpeg -i` says about a file: duration, size, fps. No ffprobe is assumed to exist beside the
    binary (ShareX ships none), so the header is parsed from ffmpeg's own stderr."""
    r = subprocess.run([exe, "-hide_banner", "-i", str(path)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    text = r.stderr
    out = {"durationSeconds": None, "width": None, "height": None, "fps": None,
           "hasAudio": bool(re.search(r"Audio:", text))}
    m = _DUR.search(text)
    if m:
        out["durationSeconds"] = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
    vm = re.search(r"Video:.*", text)
    if vm:
        line = vm.group(0)
        m = _SIZE.search(line)
        if m:
            out["width"], out["height"] = int(m.group(1)), int(m.group(2))
        m = _FPS.search(line)
        if m:
            out["fps"] = float(m.group(1))
    out["readable"] = out["width"] is not None
    return out


def count_frames(exe: str, path: Path) -> int | None:
    """Decode the video stream to nothing and read the frame counter from the progress report — a decode,
    not the container's claim."""
    r = subprocess.run([exe, "-hide_banner", "-nostdin", "-v", "error", "-i", str(path), "-map", "0:v:0",
                        "-f", "null", "-nostats", "-progress", "pipe:1", "-"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    frames = None
    for line in r.stdout.splitlines():
        if line.startswith("frame="):
            try:
                frames = int(line.split("=", 1)[1].strip())
            except ValueError:
                pass
    return frames if r.returncode == 0 else None


def decodable(exe: str, path: Path) -> tuple[bool, str]:
    r = subprocess.run([exe, "-hide_banner", "-nostdin", "-v", "error", "-i", str(path), "-map", "0:v:0",
                        "-frames:v", "1", "-f", "null", "-"], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    return r.returncode == 0, " ".join(r.stderr.split())[:300]


def probe_video(exe: str, path: Path) -> dict:
    h = probe_header(exe, path)
    h["frames"] = count_frames(exe, path)
    ar = subprocess.run([exe, "-hide_banner", "-nostdin", "-v", "error", "-i", str(path), "-map", "0:a:0",
                        "-f", "null", "-"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    h["audioDecoded"] = ar.returncode == 0
    h["bytes"] = path.stat().st_size if path.exists() else None
    return h


def synthesize_windows(text: str, out: Path, voice: str | None = None, parent_run_id=None) -> tuple[bool, str]:
    """Use Windows' installed WinRT voices; no network, install, or remote service is involved."""
    if os.name != "nt":
        return False, "local speech synthesis is only available on Windows; supply narrationAudio"
    dotnet = shutil.which("dotnet")
    if not dotnet:
        return False, ".NET 9 is unavailable for the local WinRT speech helper; supply narrationAudio"
    txt = out.with_suffix(".txt")
    txt.write_text(text, encoding="utf-8")
    project = HERE / "winrt-tts" / "winrt-tts.csproj"
    try:
        r = subprocess.run([dotnet, "run", "--project", str(project), "--", str(txt.resolve()), str(out.resolve()), voice or ""],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120,
                           env={**os.environ, "HC_REEL_PARENT_RUN_ID": parent_run_id or ""})
    except (OSError, subprocess.SubprocessError) as exc:
        out.unlink(missing_ok=True)
        return False, f"Windows speech synthesis could not finish: {exc}"
    finally:
        txt.unlink(missing_ok=True)
    if r.returncode or not out.is_file() or out.stat().st_size == 0:
        out.unlink(missing_ok=True)
        return False, "Windows speech synthesis failed: " + " ".join((r.stderr or r.stdout).split())[-300:]
    if out.stat().st_size <= 46:
        out.unlink(missing_ok=True)
        return False, "Windows speech synthesis produced no speech; repair the WinRT voice or supply narrationAudio"
    identity = next((line.split("=", 1)[1] for line in r.stdout.splitlines() if line.startswith("voice=")), "unknown")
    return True, "Windows WinRT " + identity


# ---------------------------------------------------------------------------------------------- ASS text
def ass_escape(text: str) -> str:
    """ASS has no escape for its override braces; they are swapped for full-width ones (a documented limit)."""
    return text.replace("{", "｛").replace("}", "｝").replace("\r", "").replace("\n", "\\N")


def ass_time(seconds: float) -> str:
    cs = int(round(seconds * 100))
    return f"{cs // 360000}:{(cs // 6000) % 60:02d}:{(cs // 100) % 60:02d}.{cs % 100:02d}"


def ass_document(events: list[tuple[str, str]], seconds: float, play_w: int = WIDTH, play_h: int = HEIGHT) -> str:
    """One ASS script with the reel's styles and the given (style, text) events, all shown for the whole
    segment (plus a second, so a rounding at the tail never drops the last frame's text)."""
    lines = ["[Script Info]", "ScriptType: v4.00+", f"PlayResX: {play_w}", f"PlayResY: {play_h}",
             "WrapStyle: 0", "ScaledBorderAndShadow: yes", "", "[V4+ Styles]",
             "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
             "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
             "Alignment, MarginL, MarginR, MarginV, Encoding"]
    for name, font, size, primary, outline, bold, border, opx, align, ml, mr, mv in ASS_STYLES:
        lines.append(f"Style: {name},{font},{size},{primary},&H000000FF,{outline},{outline},{-1 if bold else 0},"
                     f"0,0,0,100,100,0,0,{border},{opx},0,{align},{ml},{mr},{mv},1")
    lines += ["", "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text"]
    end = ass_time(seconds + 1.0)
    for style, text in events:
        lines.append(f"Dialogue: 0,0:00:00.00,{end},{style},,0,0,0,,{text}")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------------------------- the plan
def resolve_bundle_dir(pres: dict, pres_path: Path, override: str | None) -> Path | None:
    """Where the plan's bundle-relative media paths resolve: the flag, else `source.bundleDir` (absolute, or
    relative to the presentation's own directory, then to the working directory)."""
    if override:
        return Path(override).resolve()
    given = ((pres.get("source") or {}).get("bundleDir")) or "."
    p = Path(given)
    if p.is_absolute():
        return p
    for base in (pres_path.parent, Path.cwd()):
        if (base / p).is_dir():
            return (base / p).resolve()
    return (pres_path.parent / p).resolve()


def plan_segments(pres: dict, ffmpeg: dict, bundle_dir: Path | None, chapter_filter: set[str] | None,
                  max_seconds: float | None) -> tuple[list[dict], list[dict]]:
    """Plan one narrated evidence scene per caption; refuse rather than inventing missing visuals."""
    media_by_id = {m.get("id"): m for m in (pres.get("media") or [])}
    chapters = [c for c in (pres.get("chapters") or [])
                if chapter_filter is None or c.get("id") in chapter_filter]
    probes: dict[str, dict] = {}
    segments: list[dict] = []
    omitted: list[dict] = []
    if chapter_filter is not None:
        omitted.extend({"chapterId": c.get("id"), "reason": "explicit chapter selection"}
                       for c in pres.get("chapters") or [] if c.get("id") not in chapter_filter)

    def base(ch: dict, kind: str) -> dict:
        return {"chapterId": ch.get("id"), "chapterKind": ch.get("kind"), "kind": kind,
                "findingIds": list(ch.get("findingIds") or []), "mediaId": None, "evidenceIds": [],
                "source": None, "sourceInterval": None, "caption": "", "label": "", "status": "planned",
                "error": None, "durationSeconds": 0.0, "events": [], "captionTruncated": False}

    errors = []
    for ch in chapters:
        ck = ch.get("kind")
        caps = ch.get("captions") or []
        if not caps:
            errors.append(f"{ch.get('id')}: no narrated scene is declared")
            continue
        for caption_index, cap in enumerate(caps):
            if cap.get("omitFromReel") is True:
                if ck in ("opening", "closing"):
                    errors.append(f"{ch.get('id')}: opening and closing scenes cannot be omitted")
                    continue
                if not str(cap.get("omissionReason") or "").strip():
                    errors.append(f"{ch.get('id')}: an omitted scene needs an omissionReason")
                    continue
                omitted.append({"chapterId": ch.get("id"), "mediaId": cap.get("mediaId"),
                                "caption": cap.get("text"), "reason": cap.get("omissionReason") or "explicit storyboard omission"})
                continue
            narration = " ".join(str(cap.get("narration") or "").split())
            board = cap.get("storyboard")
            if not narration:
                errors.append(f"{ch.get('id')}: caption has no narration")
                continue
            if not isinstance(board, dict):
                errors.append(f"{ch.get('id')}: caption has no explicit storyboard")
                continue
            if board.get("mode") not in ("full-frame", "crop-pan"):
                errors.append(f"{ch.get('id')}: storyboard mode must be full-frame or crop-pan")
                continue
            if board.get("mode") == "crop-pan":
                scales = [float(board.get("startScale", 1)), float(board.get("endScale", 1.12))]
                if any(not math.isfinite(x) or not 1 <= x <= 4 for x in scales):
                    errors.append(f"{ch.get('id')}: crop-pan scales must be finite numbers from 1 to 4")
                    continue
            review = board.get("visualReview") or {}
            if review.get("subjectVisible") is not True or review.get("addedText") not in ("none", "excluded-by-source-region"):
                errors.append(f"{ch.get('id')}: storyboard lacks visualReview confirming the subject and added-text treatment")
                continue
            clean = board.get("cleanFrame") is True
            crop_ok = isinstance(board.get("sourceRegion"), dict) and board.get("excludesAddedText") is True
            if not (clean or crop_ok):
                errors.append(f"{ch.get('id')}: storyboard must declare cleanFrame=true or an authored sourceRegion with excludesAddedText=true")
                continue
            if crop_ok:
                try:
                    vals = [float(board["sourceRegion"][k]) for k in ("x", "y", "width", "height")]
                    if not all(math.isfinite(v) for v in vals) or min(vals) < 0 or max(vals) > 1 or vals[2] <= 0 or vals[3] <= 0 or vals[0] + vals[2] > 1 or vals[1] + vals[3] > 1:
                        raise ValueError
                except (KeyError, TypeError, ValueError):
                    errors.append(f"{ch.get('id')}: sourceRegion must be a normalized rectangle inside the source")
                    continue
            for h in board.get("highlights", []):
                try:
                    vals = [float(h[k]) for k in ("x", "y", "width", "height")]
                    if h.get("shape", "rect") != "rect" or not all(math.isfinite(v) for v in vals) or min(vals) < 0 or max(vals) > 1 or vals[2] <= 0 or vals[3] <= 0 or vals[0] + vals[2] > 1 or vals[1] + vals[3] > 1:
                        raise ValueError
                except (KeyError, TypeError, ValueError):
                    errors.append(f"{ch.get('id')}: highlight must be a normalized rectangle inside the scene")
                    continue
            mid = cap.get("mediaId")
            row = media_by_id.get(mid)
            if mid is None or row is None:
                errors.append(f"{ch.get('id')}: no qualified media is bound to the narrated scene")
                continue
            if row.get("assessed") is not True:
                errors.append(f"{ch.get('id')}: media {mid} is unassessed ({row.get('reason')})")
                continue
            rel = row.get("path") or ""
            abs_path = (bundle_dir / rel) if bundle_dir else Path(rel)
            if not abs_path.is_file():
                errors.append(f"{ch.get('id')}: media file {rel} is not on disk")
                continue
            got = sha256_file(abs_path)
            if row.get("sha256") and got != row["sha256"]:
                errors.append(f"{ch.get('id')}: media file {rel} changed since planning")
                continue
            if got not in probes:
                ok, err = decodable(ffmpeg["path"], abs_path)
                probes[got] = {"ok": ok, "error": err,
                               "header": probe_header(ffmpeg["path"], abs_path) if ok else None}
            pr = probes[got]
            if not pr["ok"]:
                errors.append(f"{ch.get('id')}: media file {rel} does not decode: {pr['error']}")
                continue
            is_image = abs_path.suffix.lower() in IMAGE_SUFFIXES or row.get("mediaKind") in (
                "offline-render", "native-screenshot", "historical-illustration")
            s = base(ch, "image" if is_image else "clip")
            s["captionIndex"] = cap.get("captionIndex", caption_index)
            s["occurrenceIndex"] = cap.get("occurrenceIndex", 0)
            s["mediaId"] = mid
            s["evidenceIds"] = list(cap.get("evidenceIds") or [])
            s["caption"] = cap.get("text") or ""
            s["narration"] = narration
            s["storyboard"] = board
            s["narrationAudio"] = cap.get("narrationAudio")
            subj = row.get("subject") or {}
            s["label"] = " · ".join(str(x) for x in (subj.get("armId") or "?", row.get("state") or "?",
                                                      row.get("mediaKind") or "?"))
            s["source"] = {"path": rel, "sha256": got, "absPath": str(abs_path),
                           "width": pr["header"]["width"], "height": pr["header"]["height"]}
            if is_image:
                s["durationSeconds"] = IMAGE_SECONDS
            else:
                src_dur = pr["header"].get("durationSeconds")
                iv = row.get("sourceInterval") or {}
                start = float(iv.get("startSeconds") or 0.0)
                end = iv.get("endSeconds")
                end = float(end) if end is not None else (src_dur if src_dur is not None else start)
                clamped = False
                if src_dur is not None and end > src_dur + 0.05:
                    end, clamped = src_dur, True
                if end <= start:
                    errors.append(f"{ch.get('id')}: source interval {start}-{end} s is empty")
                    continue
                s["sourceInterval"] = {"startSeconds": start, "endSeconds": end,
                                       "sourcePath": iv.get("sourcePath"), "sourceSha256": iv.get("sourceSha256"),
                                       "clampedToSource": clamped, "sourceDurationSeconds": src_dur,
                                       "sourceFps": pr["header"].get("fps")}
                s["durationSeconds"] = round(end - start, 3)
            s["durationSeconds"] = max(s["durationSeconds"], round(len(narration.split()) / 2.35 + .75, 3))
            s["events"] = []
            segments.append(s)

    if errors:
        raise ValueError("storyboard-refused: " + "; ".join(errors))

    if max_seconds is not None and sum(s["durationSeconds"] for s in segments) > max_seconds:
        raise ValueError(f"storyboard-refused: narrated scenes exceed the {max_seconds} s budget; shorten narration or explicitly select a complete shorter presentation")
    for i, s in enumerate(segments):
        s["index"] = i
        s["plannedFrames"] = int(round(s["durationSeconds"] * FPS))
    return segments, omitted


# ---------------------------------------------------------------------------------------------- rendering
def cache_key(seg: dict, ffmpeg_version: str) -> str:
    params = {"layout": LAYOUT_VERSION, "kind": seg["kind"], "w": WIDTH, "h": HEIGHT, "fps": FPS,
              "source": (seg["source"] or {}).get("sha256"), "interval": seg["sourceInterval"],
              "duration": seg["durationSeconds"], "frames": seg["plannedFrames"], "caption": seg["caption"],
              "narration": seg.get("narration"), "audio": seg.get("audioSha256"),
              "storyboard": seg.get("storyboard"), "ffmpeg": ffmpeg_version}
    return sha256_text(json.dumps(params, sort_keys=True, ensure_ascii=False))[:16]


FIT = f"scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=decrease:flags=lanczos"
ENCODE = ["-r", str(FPS), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
          "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]


def segment_command(seg: dict) -> list[str]:
    d = f"{seg['durationSeconds']:.3f}"
    audio = ["-i", seg["audioPath"]]
    board = seg.get("storyboard") or {}
    if board.get("mode") not in ("full-frame", "crop-pan"):
        raise ValueError("storyboard mode must be full-frame or crop-pan")
    region = board.get("sourceRegion")
    crop = ""
    if isinstance(region, dict):
        crop = (f"crop=iw*{float(region['width'])}:ih*{float(region['height'])}:"
                f"iw*{float(region['x'])}:ih*{float(region['y'])},")
    highlights = "".join(f",drawbox=x=iw*{float(h['x'])}:y=ih*{float(h['y'])}:w=iw*{float(h['width'])}:"
                         f"h=ih*{float(h['height'])}:color={h.get('color','yellow')}@0.9:t=4"
                         for h in board.get("highlights", []) if h.get("shape", "rect") == "rect")
    motion = ""
    if board.get("mode") == "crop-pan":
        z0, z1 = float(board.get("startScale", 1.0)), float(board.get("endScale", 1.12))
        motion = (f",zoompan=z='{z0}+({z1}-{z0})*on/{max(1,seg['plannedFrames']-1)}':"
                  f"x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={WIDTH}x{HEIGHT}:fps={FPS}")
    vf = f"{crop}{FIT},pad={WIDTH}:{HEIGHT}:(ow-iw)/2:(oh-ih)/2:{CANVAS_COLOUR}{motion}{highlights},format=yuv420p[out]"
    if seg["kind"] == "image":
        return (["-loop", "1", "-framerate", str(FPS), "-t", d, "-i", seg["source"]["absPath"]] + audio
                + ["-filter_complex", f"[0:v]{vf};[1:a]apad=whole_dur={d}[aud]",
                   "-map", "[out]", "-map", "[aud]", "-t", d, "-frames:v", str(seg["plannedFrames"])] + ENCODE + ["seg.mp4"])
    if seg["kind"] == "clip":
        iv = seg["sourceInterval"]
        return (["-ss", f"{iv['startSeconds']:.3f}", "-i", seg["source"]["absPath"]] + audio
                + ["-filter_complex", f"[0:v]trim=duration={iv['endSeconds']-iv['startSeconds']},setpts=PTS-STARTPTS,fps={FPS},tpad=stop_mode=clone:stop_duration={d},{vf};[1:a]apad=whole_dur={d}[aud]",
                   "-map", "[out]", "-map", "[aud]", "-t", d, "-frames:v", str(seg["plannedFrames"])] + ENCODE + ["seg.mp4"])
    raise ValueError(f"unsupported scene kind {seg['kind']}")


def render_segment(seg: dict, out: Path, ffmpeg: dict) -> dict:
    """Render one segment into its cache directory, or reuse the cached one when its sidecar's hash still
    matches the file. Returns the sidecar: what was measured from the produced file, never assumed."""
    key = cache_key(seg, ffmpeg["version"])
    work = out / "cache" / key
    mp4, side = work / "seg.mp4", work / "seg.json"
    seg["cacheKey"] = key
    seg["file"] = f"cache/{key}/seg.mp4"
    cached = load_json(side)
    if cached and mp4.is_file() and cached.get("sha256") == sha256_file(mp4) and cached.get("frames"):
        cached["reused"] = True
        return cached
    work.mkdir(parents=True, exist_ok=True)
    r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "warning"] + segment_command(seg), cwd=work)
    stderr = " ".join(r.stderr.split())[-600:]
    if r.returncode != 0 or not mp4.is_file():
        return {"key": key, "reused": False, "frames": None, "error": f"ffmpeg exit {r.returncode}: {stderr}"}
    probe = probe_video(ffmpeg["path"], mp4)
    if not probe.get("frames"):
        return {"key": key, "reused": False, "frames": None, "error": f"the segment decodes to no frames: {stderr}"}
    side_doc = {"key": key, "reused": False, "frames": probe["frames"], "durationSeconds": probe["durationSeconds"],
                "width": probe["width"], "height": probe["height"], "fps": probe["fps"], "bytes": probe["bytes"],
                "sha256": sha256_file(mp4), "ffmpegWarnings": stderr or None, "error": None,
                "renderedUtc": utcnow()}
    write_json(side, side_doc)
    return side_doc


def srt_time(seconds: float) -> str:
    ms = int(round(seconds * 1000))
    return f"{ms // 3600000:02d}:{(ms // 60000) % 60:02d}:{(ms // 1000) % 60:02d},{ms % 1000:03d}"


#: a subtitle cue holds at most this many words and lasts at most this many seconds
CUE_MAX_WORDS, CUE_MAX_SECONDS, CUE_MIN_SECONDS = 14, 7.0, 1.2


def cue_texts(text: str) -> list[str]:
    """The narration as subtitle cues: one per sentence, a long sentence split at its clause marks, then by words."""
    out = []
    for sent in re.split(r"(?<=[.!?])\s+", " ".join(str(text or "").split())):
        pieces = [sent]
        if len(sent.split()) > CUE_MAX_WORDS:
            pieces = [x for x in re.split(r"(?<=[;:,])\s+", sent) if x]
        for pc in pieces:
            w = pc.split()
            out += [" ".join(w[i:i + CUE_MAX_WORDS]) for i in range(0, len(w), CUE_MAX_WORDS)]
    return [x for x in out if x]


def write_srt(path: Path, segments: list[dict]) -> int:
    """Cues timed across each segment's own speech, by the share of its characters: in order, never overlapping, none
    shorter than CUE_MIN_SECONDS (a short cue is merged into its neighbour) and none longer than CUE_MAX_SECONDS."""
    cues = []
    for s in segments:
        iv = s.get("reelInterval")
        if not iv:
            continue
        texts = cue_texts(s.get("narration") or s.get("caption") or "")
        start, stop = float(iv["startSeconds"]), float(iv["endSeconds"])
        span = min(float(s.get("speechSeconds") or (stop - start)), stop - start)
        total = sum(len(x) for x in texts) or 1
        parts = [[x, span * len(x) / total] for x in texts]
        # merge a cue too short to read into the next one (the last into the one before)
        k = 0
        while k < len(parts) and len(parts) > 1:
            if parts[k][1] < CUE_MIN_SECONDS:
                o = k + 1 if k + 1 < len(parts) else k - 1
                a, b = (k, o) if o > k else (o, k)
                parts[a] = [parts[a][0] + " " + parts[b][0], parts[a][1] + parts[b][1]]
                del parts[b]
                k = max(0, a - 1)
                continue
            k += 1
        at = start
        for x, dur in parts:
            end = min(at + dur, at + CUE_MAX_SECONDS, stop)
            cues.append((at, end, x))
            at += dur
    out = []
    for n, (a, e, x) in enumerate(cues):
        if n + 1 < len(cues):
            e = min(e, cues[n + 1][0])                       # never past the next cue's start
        out.append(f"{n + 1}\n{srt_time(a)} --> {srt_time(e)}\n{x}\n")
    path.write_text("\n".join(out) + ("\n" if out else ""), encoding="utf-8")
    return len(out)


def silences(exe: str, path: Path, noise_db: float, min_seconds: float) -> list[dict]:
    """The decoded audio's silent stretches of at least `min_seconds` (ffmpeg silencedetect)."""
    r = run_ffmpeg(exe, ["-hide_banner", "-nostdin", "-i", str(path), "-af",
                         f"silencedetect=noise={noise_db}dB:d={min_seconds}", "-f", "null", "-"])
    out, start = [], None
    for line in (r.stderr or "").splitlines():
        m = re.search(r"silence_start: ([\d.]+)", line)
        if m:
            start = float(m.group(1))
        m = re.search(r"silence_end: ([\d.]+) \| silence_duration: ([\d.]+)", line)
        if m:
            out.append({"start": start, "end": float(m.group(1)), "seconds": float(m.group(2))})
    return out


def contact_sheet(out: Path, reel: Path, segments: list[dict], ffmpeg: dict) -> dict:
    """A frame at each segment's start and midpoint, tiled four across with the segment index burned in.
    Read the exact integral frame bounds recorded in reel-map.json. Container timestamps may carry a
    fixed muxing offset, so time seeking can land on the preceding scene even when the map is right."""
    frames_dir = out / "cache" / "sheet"
    if frames_dir.exists():
        shutil.rmtree(frames_dir, ignore_errors=True)
    frames_dir.mkdir(parents=True, exist_ok=True)
    n = 0
    errors = []
    for s in segments:
        iv = s.get("reelInterval")
        if not iv:
            continue
        start_frame = int(iv["startFrame"])
        frame_count = int(iv["frames"])
        for tag, frame_number in (("start", start_frame),
                                  ("mid", start_frame + frame_count // 2)):
            nominal_time = frame_number / FPS
            (frames_dir / "lab.ass").write_text(ass_document([("Sheet", ass_escape(
                f"#{s['index']} {tag} frame {frame_number} ({nominal_time:.2f}s)"))],
                                                            2.0, 320, 180), encoding="utf-8")
            r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "error", "-i", str(reel), "-frames:v", "1",
                                            "-vf", f"select=eq(n\\,{frame_number}),setpts=PTS-STARTPTS,scale=320:180,ass=lab.ass",
                                            f"f-{n:03d}.png"], cwd=frames_dir)
            if r.returncode != 0 or not (frames_dir / f"f-{n:03d}.png").is_file():
                errors.append(f"frame {n} ({s['index']} {tag}): {' '.join(r.stderr.split())[:200]}")
            n += 1
    if n == 0:
        return {"frames": 0, "error": "no segments"}
    rows = (n + 3) // 4
    r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "error", "-framerate", "1", "-start_number", "0", "-i", "f-%03d.png",
                                    "-vf", f"tile=4x{rows}:padding=4:margin=4:color=0x202020", "-frames:v", "1",
                                    str(out / "contact-sheet.png")], cwd=frames_dir)
    if r.returncode != 0:
        errors.append("tile: " + " ".join(r.stderr.split())[:200])
    return {"frames": n, "error": "; ".join(errors) or None}


def refuse(reason: str, out: Path | None = None) -> int:
    print(f"REFUSED: {reason}", file=sys.stderr)
    if out is not None:
        write_json(out / "reel-status.json", {"schemaVersion": 1, "status": "failed", "error": reason,
                                              "generatedUtc": utcnow()})
    return EXIT_REFUSED


def cmd_storyboard_packet(a) -> int:
    pres = load_json(Path(a.presentation))
    if not isinstance(pres, dict) or not isinstance(pres.get("chapters"), list):
        return refuse("presentation-unreadable: storyboard packet needs a presentation with chapters")
    reel = pres.get("reel")
    if isinstance(reel, dict) and reel.get("status") == "refused":
        return refuse(f"reel-refused: {reel.get('reason')}")
    scenes = []
    if isinstance(reel, dict) and reel.get("status") == "planned":
        # the vignette reel: one narrated scene per reel chapter, its words the planner's; a still chapter one scene per
        # page as drawn, each with its own page's words, so each page is spoken and held for itself
        import reel_stills
        reel_stills.keep_names(reel.get("modNames") or {}, reel.get("shortForms"))
        try:
            font_, bold_ = reel_stills.font_files(getattr(a, "font", None), ((pres.get("settings") or {}).get("reel") or {}))
        except reel_stills.FontMissing as e:
            return refuse(f"font-missing: {e}")
        for ch in reel.get("chapters") or []:
            if ch.get("kind") in STILL_KINDS and (ch.get("still") or {}).get("pages"):
                for n, part in enumerate(reel_stills.still_parts(ch["still"]["pages"], font_, bold_)):
                    scenes.append({"chapterId": ch.get("id"), "captionIndex": n, "occurrenceIndex": 0, "mediaId": None,
                                   "kind": ch.get("kind"), "page": n, "narration": reel_stills.spoken_text(part["say"]),
                                   "narrationAudio": (ch.get("pageAudio") or {}).get(str(n)), "omitFromReel": False,
                                   "omissionReason": None})
                continue
            scenes.append({"chapterId": ch.get("id"), "captionIndex": 0, "occurrenceIndex": 0, "mediaId": None,
                           "kind": ch.get("kind"), "narration": reel_stills.spoken_text(ch.get("narration") or ""),
                           "narrationAudio": ch.get("narrationAudio"), "omitFromReel": False, "omissionReason": None})
    for ch in ([] if scenes else pres["chapters"]):
        for i, cap in enumerate(ch.get("captions") or []):
            scenes.append({"chapterId": ch.get("id"), "captionIndex": i, "occurrenceIndex": 0, "mediaId": cap.get("mediaId"),
                           "narration": cap.get("narration"), "storyboard": cap.get("storyboard"),
                           "omitFromReel": False, "omissionReason": None, "narrationAudio": cap.get("narrationAudio"),
                           "requiredReview": {"subjectVisible": True,
                                              "addedText": "none | excluded-by-source-region"}})
    with Run("evalreel-storyboard-packet", stage_source="counted") as run:
        run.stage("scenes", total=len(scenes), unit="scenes")
        try:
            captures = capture_requests(pres, resolve_bundle_dir(pres, Path(a.presentation).resolve(), a.bundle))
        except (OSError, ValueError, KeyError, TypeError) as exc:
            run.refuse(str(exc), EXIT_REFUSED)
            return refuse(f"capture-plan-refused: {exc}")
        write_json(Path(a.out), {"schemaVersion": 1, "presentationId": pres.get("presentationId"), "scenes": scenes,
                                "capturePlan": captures})
        run.advance(len(scenes)); run.artifact(str(Path(a.out))); run.tally(ok=len(scenes))
    print(f"storyboard packet: {len(scenes)} scene(s); {a.out}")
    return EXIT_OK


def apply_storyboard_answers(pres: dict, path: Path) -> str | None:
    answers = load_json(path)
    if not isinstance(answers, dict) or not isinstance(answers.get("scenes"), list):
        return "storyboard-answers-unreadable: expected an object with scenes[]"
    if answers.get("presentationId") != pres.get("presentationId"):
        return "storyboard-answers-foreign: presentationId does not match"
    reel = pres.get("reel") if isinstance(pres.get("reel"), dict) else {}
    reel_chapters = {c.get("id"): c for c in reel.get("chapters") or []} if reel.get("status") == "planned" else {}
    if reel_chapters:
        # the vignette reel takes only audio (and, listed as an overlay, words) from the answers; its selection,
        # frames and stills stay the planner's
        for row in answers["scenes"]:
            ch = reel_chapters.get(row.get("chapterId")) if isinstance(row, dict) else None
            if ch is None:
                return f"storyboard-answers-invalid: unknown reel scene {row.get('chapterId') if isinstance(row, dict) else row!r}"
            if ch.get("kind") in STILL_KINDS:
                # a still page's audio, by its page; its words stay the page's own
                if row.get("narrationAudio"):
                    ch.setdefault("pageAudio", {})[str(int(row.get("captionIndex") or 0))] = row["narrationAudio"]
                continue
            if row.get("narrationAudio"):
                ch["narrationAudio"] = row["narrationAudio"]
            text = " ".join(str(row.get("narration") or "").split())
            if text and text != " ".join(str(ch.get("narration") or "").split()):
                ch["narration"], ch["narrationOverlaid"] = text, True
        return None
    chapters = {c.get("id"): c for c in pres.get("chapters") or []}
    replacements = {}
    for row in answers["scenes"]:
        if not isinstance(row, dict):
            return "storyboard-answers-invalid: scene must be an object"
        ch = chapters.get(row.get("chapterId")); idx = row.get("captionIndex")
        caps = (ch or {}).get("captions") or []
        if type(idx) is not int or idx < 0 or idx >= len(caps):
            return f"storyboard-answers-invalid: unknown scene {row.get('chapterId')}[{idx}]"
        occurrence = row.get("occurrenceIndex", 0)
        if type(occurrence) is not int or occurrence < 0:
            return "storyboard-answers-invalid: occurrenceIndex must be a nonnegative integer"
        group = replacements.setdefault((row["chapterId"], idx), {})
        if occurrence in group:
            return f"storyboard-answers-invalid: duplicate scene occurrence {row['chapterId']}[{idx}][{occurrence}]"
        cap = copy.deepcopy(caps[idx])
        cap.update(captionIndex=idx, occurrenceIndex=occurrence)
        requested_mid = row.get("mediaId")
        original_mid = caps[idx].get("mediaId")
        if requested_mid != original_mid:
            media = next((m for m in pres.get("media") or [] if m.get("id") == requested_mid and m.get("assessed") is True), None)
            if not media:
                return f"storyboard-answers-invalid: replacement media is not assessed for {row.get('chapterId')}[{idx}]"
            if (ch or {}).get("kind") == "finding":
                findings = [f for f in pres.get("findings") or [] if f.get("id") in ((ch or {}).get("findingIds") or [])]
                if not any(requested_mid in (f.get("mediaIds") or []) for f in findings):
                    return f"storyboard-answers-invalid: replacement media subject does not match finding {row.get('chapterId')}[{idx}]"
            cap["mediaId"] = requested_mid
        if isinstance(row.get("storyboard"), dict): cap["storyboard"] = copy.deepcopy(row["storyboard"])
        if row.get("narrationAudio"): cap["narrationAudio"] = row["narrationAudio"]
        if row.get("narration"): cap["narration"] = " ".join(str(row["narration"]).split())
        if row.get("omitFromReel") is True:
            if (ch or {}).get("kind") in ("opening", "closing"):
                return f"storyboard-answers-invalid: opening/closing cannot be omitted ({row.get('chapterId')}[{idx}])"
            if not str(row.get("omissionReason") or "").strip():
                return f"storyboard-answers-invalid: an omitted scene needs an omissionReason ({row.get('chapterId')}[{idx}])"
            cap["omitFromReel"] = True
            cap["omissionReason"] = row["omissionReason"]
        group[occurrence] = cap
    for (chapter, idx), group in replacements.items():
        if sorted(group) != list(range(len(group))):
            return f"storyboard-answers-invalid: occurrenceIndex must be contiguous from zero for {chapter}[{idx}]"
    # Validate every binding against the original before expanding; absent answers retain their caption.
    for ch in pres.get("chapters") or []:
        expanded = []
        for idx, original in enumerate(ch.get("captions") or []):
            group = replacements.get((ch.get("id"), idx))
            if group:
                expanded.extend(group[n] for n in range(len(group)))
            else:
                cap = copy.deepcopy(original)
                cap.update(captionIndex=idx, occurrenceIndex=0)
                expanded.append(cap)
        ch["captions"] = expanded
    return None


def cmd_render(a) -> int:
    # A flag first, then the saved evaluation settings (evalsettings.py), then the plan's own settings.
    try:
        s = evalsettings.load()
    except evalsettings.SettingsError as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return EXIT_REFUSED
    if a.max_seconds is None and s.value("video.maxSeconds") is not None:
        a.max_seconds = float(s.value("video.maxSeconds"))
    if a.ffmpeg is None and s.options["video.ffmpeg"]["source"] == "file":
        a.ffmpeg, a.ffmpeg_how = s.value("video.ffmpeg"), f"the evaluation settings ({s.path})"
    out = Path(a.out).resolve()
    if not (os.environ.get("HOUSECARL_RUNLOG_DIR") or "").strip():
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(out / "runlog")
    with Run("evalreel-render", stage_source="counted") as run:
        run.stage("prepare", total=1, unit="presentations")
        result = render_reel(a, run)
        if result == EXIT_REFUSED:
            run.refuse("The storyboard or narration prerequisites were refused.", result)
        elif result != EXIT_OK:
            run.fail("The reel did not pass output verification.", result)
        return result


def render_reel(a, run) -> int:
    pres_path = Path(a.presentation).resolve()
    out = Path(a.out).resolve()
    pres = load_json(pres_path)
    if not isinstance(pres, dict):
        return refuse(f"presentation-unreadable: {pres_path} is absent or not JSON")
    if pres.get("schemaVersion") != 1:
        return refuse(f"schema-unsupported: presentation schemaVersion {pres.get('schemaVersion')!r}, expected 1")
    if not isinstance(pres.get("chapters"), list):
        return refuse("presentation-incomplete: no `chapters` list")
    if a.storyboard:
        err = apply_storyboard_answers(pres, Path(a.storyboard))
        if err: return refuse(err, out)
    out.mkdir(parents=True, exist_ok=True)
    if not (os.environ.get("HOUSECARL_RUNLOG_DIR") or "").strip():
        os.environ["HOUSECARL_RUNLOG_DIR"] = str((out / "runlog").resolve())
    found = ffmpeg_find(a.ffmpeg, getattr(a, "ffmpeg_how", "--ffmpeg"))
    if not found:
        return refuse("ffmpeg-missing: no ffmpeg via --ffmpeg, HOUSECARL_FFMPEG, DBH_FFMPEG, PATH or a bundled copy "
                      "(ShareX, winget Gyan.FFmpeg, Program Files\\ffmpeg)", out)
    ffmpeg = ffmpeg_identity(found["exe"])
    ffmpeg["how"] = found["how"]
    lacking = [f for f in ("drawbox", "scale", "fps", "tile") if f not in ffmpeg["filters"]]
    if lacking or not ffmpeg["libx264"]:
        return refuse(f"ffmpeg-incapable: {found['exe']} ({ffmpeg['version']}) lacks "
                      f"{', '.join(lacking + ([] if ffmpeg['libx264'] else ['libx264']))}; the reel needs "
                      f"scene-layout filters and an H.264 encoder", out)
    if isinstance(pres.get("reel"), dict):
        return render_vignettes(a, run, pres, pres_path, out, ffmpeg)
    settings_reel = ((pres.get("settings") or {}).get("reel") or {})
    max_seconds = a.max_seconds if a.max_seconds is not None else settings_reel.get("maxSeconds")
    chapter_filter = {c.strip() for c in a.chapters.split(",") if c.strip()} if a.chapters else None
    if chapter_filter:
        required_bookends = {c.get("id") for c in pres["chapters"] if c.get("kind") in ("opening", "closing")}
        if not required_bookends.issubset(chapter_filter):
            return refuse("storyboard-refused: --chapters must include the opening and closing scenes", out)
    bundle_dir = resolve_bundle_dir(pres, pres_path, a.bundle)

    try:
        segments, omitted = plan_segments(pres, ffmpeg, bundle_dir, chapter_filter, max_seconds)
    except (ValueError, KeyError, TypeError) as e:
        return refuse(str(e), out)
    if not segments or segments[0].get("chapterKind") != "opening":
        return refuse("storyboard-refused: the first included scene must be the opening", out)
    if segments[-1].get("chapterKind") != "closing":
        return refuse("storyboard-refused: the last included scene must be the closing", out)
    verdict = " ".join(str((pres.get("reader") or {}).get("verdict") or "").split())
    if not verdict or not segments[0]["narration"].startswith(verdict):
        return refuse("narration-refused: the opening must start with reader.verdict verbatim", out)

    audio_dir = out / "narration"
    audio_dir.mkdir(parents=True, exist_ok=True)
    for s in segments:
        supplied = s.get("narrationAudio")
        if supplied:
            apath = (bundle_dir / supplied) if bundle_dir else Path(supplied)
            method = "supplied"
        else:
            if not a.voice or not a.voice.strip():
                return refuse(f"narration-refused: {s['chapterId']}: prepare narrationAudio with evalspeech.py "
                              "(Kokoro George on CPU by default), or explicitly select an installed Windows voice "
                              "with --voice; no automatic voice fallback", out)
            helper = HERE / "winrt-tts" / "Program.cs"
            speech_key = sha256_text(s["narration"] + "\n" + (a.voice or "<default>") + "\n" + sha256_file(helper))[:12]
            apath = audio_dir / f"{s['index']:03d}-{speech_key}.wav"
            method = "Windows WinRT " + (a.voice or "default voice")
            if not apath.is_file():
                ok, detail = synthesize_windows(s["narration"], apath, a.voice, run.run_id)
                if not ok:
                    return refuse(f"narration-refused: {s['chapterId']}: {detail}", out)
                method = detail
        if not apath.is_file():
            return refuse(f"narration-refused: {s['chapterId']}: audio file {apath} is absent", out)
        ah = probe_header(ffmpeg["path"], apath)
        if not ah.get("hasAudio") or not ah.get("durationSeconds"):
            return refuse(f"narration-refused: {s['chapterId']}: audio file {apath} is not decodable audio", out)
        s["audioPath"], s["audioSha256"], s["audioMethod"] = str(apath), sha256_file(apath), method
        s["durationSeconds"] = max(s["durationSeconds"], round(ah["durationSeconds"] + .25, 3))
        s["plannedFrames"] = int(round(s["durationSeconds"] * FPS))
    if max_seconds is not None and sum(s["durationSeconds"] for s in segments) > max_seconds:
        return refuse(f"storyboard-refused: synthesized narration exceeds the {max_seconds} s budget; shorten narration", out)
    planned_seconds = round(sum(s["durationSeconds"] for s in segments), 3)
    planned_frames = sum(s["plannedFrames"] for s in segments)
    if a.dry_run:
        print(f"dry run: {len(segments)} segment(s), {planned_seconds} s planned, {planned_frames} frames; "
              f"ffmpeg {ffmpeg['version']} ({ffmpeg['how']})")
        for s in segments:
            print(f"  #{s['index']:>2} {s['chapterId']:<6} {s['kind']:<13} {s['durationSeconds']:>6.2f}s "
                  f"{s['status']:<13} {(s['source'] or {}).get('path') or '-'}  {s['caption'][:60]}")
        for o in omitted:
            print(f"  omitted {o['chapterId']}: {o['reason']}")
        return EXIT_OK

    storyboard_hash = sha256_file(Path(a.storyboard)) if a.storyboard else None
    status_doc = {"schemaVersion": 1, "status": "failed", "presentationId": pres.get("presentationId"),
                  "storyboardSha256": storyboard_hash,
                  "generatedUtc": utcnow(), "out": str(out), "bundleDir": str(bundle_dir),
                  "ffmpeg": {"path": ffmpeg["path"], "version": ffmpeg["version"], "how": ffmpeg["how"]},
                  "planned": {"seconds": planned_seconds, "frames": planned_frames, "segments": len(segments)},
                  "expected": None, "probe": None, "reused": 0, "rendered": 0, "missingCards": 0,
                  "omittedChapters": omitted, "segments": [], "outputs": {}, "errors": []}
    with contextlib.nullcontext(run):
        run.feature("segments", len(segments), "segments")
        run.feature("sourceSeconds", planned_seconds, "seconds")
        run.feature("chapters", len(pres.get("chapters") or []), "chapters")
        run.stage("segments", total=len(segments), unit="segments")
        cursor_frames = 0
        failed = False
        for s in segments:
            side = render_segment(s, out, ffmpeg)
            run.advance()
            if side.get("error") or not side.get("frames"):
                s["status"] = "failed"
                s["error"] = side.get("error")
                s["frames"] = None
                failed = True
                status_doc["errors"].append(f"segment {s['index']} ({s['chapterId']}): {side.get('error')}")
                continue
            s["frames"] = side["frames"]
            s["reelInterval"] = {"startSeconds": round(cursor_frames / FPS, 3),
                                 "endSeconds": round((cursor_frames + side["frames"]) / FPS, 3),
                                 "startFrame": cursor_frames, "frames": side["frames"]}
            cursor_frames += side["frames"]
            if side.get("reused"):
                status_doc["reused"] += 1
                if s["status"] == "planned":
                    s["status"] = "reused"
            else:
                status_doc["rendered"] += 1
                if s["status"] == "planned":
                    s["status"] = "rendered"
            if s["kind"] == "missing":
                status_doc["missingCards"] += 1
            if side["frames"] != s["plannedFrames"]:
                s["frameShortfall"] = s["plannedFrames"] - side["frames"]
                status_doc["errors"].append(f"segment {s['index']} ({s['chapterId']}): {side['frames']} frames "
                                            f"decoded, {s['plannedFrames']} planned")
        expected_frames = sum(s.get("frames") or 0 for s in segments)
        status_doc["expected"] = {"frames": expected_frames, "seconds": round(expected_frames / FPS, 3)}

        run.stage("concat", total=1, unit="reels")
        reel = out / "reel.mp4"
        probe = None
        if not failed:
            listing = out / "concat.txt"
            listing.write_text("".join(f"file '{s['file']}'\n" for s in segments), encoding="utf-8")
            r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "error", "-f", "concat", "-safe", "0", "-i", "concat.txt",
                                            "-c", "copy", "-movflags", "+faststart", "reel.mp4"], cwd=out)
            if r.returncode != 0 or not reel.is_file():
                failed = True
                status_doc["errors"].append(f"concat: ffmpeg exit {r.returncode}: {' '.join(r.stderr.split())[-400:]}")
            else:
                probe = probe_video(ffmpeg["path"], reel)
                probe["sha256"] = sha256_file(reel)
                status_doc["probe"] = probe
                checks = []
                if probe.get("frames") != expected_frames:
                    checks.append(f"frames {probe.get('frames')} decoded, {expected_frames} expected from the segments")
                if probe.get("durationSeconds") is None or abs(probe["durationSeconds"] - planned_seconds) > DURATION_TOLERANCE:
                    checks.append(f"duration {probe.get('durationSeconds')} s, {planned_seconds} s planned "
                                  f"(tolerance {DURATION_TOLERANCE} s)")
                if (probe.get("width"), probe.get("height")) != (WIDTH, HEIGHT):
                    checks.append(f"size {probe.get('width')}x{probe.get('height')}, {WIDTH}x{HEIGHT} planned")
                if probe.get("fps") != FPS:
                    checks.append(f"fps {probe.get('fps')}, {FPS} planned")
                if not probe.get("hasAudio") or not probe.get("audioDecoded"):
                    checks.append("no independently decoded audio stream")
                if checks:
                    failed = True
                    status_doc["errors"].append("reel.mp4 does not match the plan: " + "; ".join(checks))
        run.advance()

        # the map, the SRT and the contact sheet are written from what was produced
        segments_out = []
        for s in segments:
            segments_out.append({
                "index": s["index"], "chapterId": s["chapterId"], "chapterKind": s["chapterKind"], "kind": s["kind"],
                "captionIndex": s["captionIndex"], "occurrenceIndex": s["occurrenceIndex"],
                "findingIds": s["findingIds"], "mediaId": s["mediaId"],
                "source": ({"path": s["source"]["path"], "sha256": s["source"]["sha256"]} if s["source"] else None),
                "sourceInterval": s["sourceInterval"], "reelInterval": s.get("reelInterval"),
                "caption": s["caption"], "label": s["label"], "evidenceIds": s["evidenceIds"],
                "narration": s.get("narration"), "storyboard": s.get("storyboard"),
                "audio": {"sha256": s.get("audioSha256"), "method": s.get("audioMethod")},
                "status": s["status"], "error": s["error"], "captionTruncated": s["captionTruncated"],
                "cacheKey": s.get("cacheKey"), "file": s.get("file")})
        map_doc = {"schemaVersion": 1, "presentationId": pres.get("presentationId"), "generatedUtc": utcnow(),
                   "storyboardSha256": storyboard_hash,
                   "canvas": {"width": WIDTH, "height": HEIGHT, "fps": FPS, "imageRegionHeight": IMAGE_H,
                              "captionBandHeight": BAND_H, "layoutVersion": LAYOUT_VERSION},
                   "reel": ({"path": "reel.mp4", "sha256": probe.get("sha256"), "frames": probe.get("frames"),
                             "durationSeconds": probe.get("durationSeconds")} if probe else None),
                   "segments": segments_out, "omittedChapters": omitted}
        write_json(out / "reel-map.json", map_doc)
        cues = write_srt(out / "reel.srt", segments)
        sheet = None
        if probe and not failed:
            sheet = contact_sheet(out, reel, segments, ffmpeg)
            if sheet.get("error"):
                failed = True
                status_doc["errors"].append("contact sheet: " + sheet["error"])

        partial = any(s["status"] == "missing-media" for s in segments)
        status_doc["status"] = "failed" if failed else ("partial" if partial else "rendered")
        status_doc["segments"] = [{"index": s["index"], "chapterId": s["chapterId"], "kind": s["kind"],
                                   "status": s["status"], "error": s["error"], "frames": s.get("frames"),
                                   "durationSeconds": s["durationSeconds"], "cacheKey": s.get("cacheKey")}
                                  for s in segments]
        status_doc["srtCues"] = cues
        status_doc["contactSheet"] = sheet
        for name in ("reel.mp4", "reel.srt", "reel-map.json", "contact-sheet.png"):
            p = out / name
            if p.is_file():
                status_doc["outputs"][name] = {"sha256": sha256_file(p), "bytes": p.stat().st_size}
        write_json(out / "reel-status.json", status_doc)
        for name in ("reel.mp4", "reel.srt", "reel-map.json", "reel-status.json", "contact-sheet.png"):
            if (out / name).is_file():
                run.artifact(str(out / name))
        run.feature("outputSeconds", (probe or {}).get("durationSeconds") or 0.0, "seconds", at="final")
        run.feature("outputFrames", (probe or {}).get("frames") or 0, "frames", at="final")
        run.feature("reused", status_doc["reused"], "segments", at="final")
        run.tally(ok=status_doc["rendered"] + status_doc["reused"],
                  failed=sum(1 for s in segments if s["status"] == "failed"),
                  inconclusive=sum(1 for s in segments if s["status"] == "missing-media"))
        if status_doc["status"] != "rendered":
            run.note(f"reel {status_doc['status']}: " + ("; ".join(status_doc["errors"]) or
                                                          f"{status_doc['missingCards']} missing card(s)"))

    print(f"reel: {status_doc['status']} — {len(segments)} segment(s), {status_doc['reused']} reused, "
          f"{status_doc['rendered']} rendered"
          + (f", {(probe or {}).get('durationSeconds')} s / {(probe or {}).get('frames')} frames" if probe else "")
          + f"; {out / 'reel-status.json'}")
    for e in status_doc["errors"]:
        print("  " + e, file=sys.stderr)
    return EXIT_OK if status_doc["status"] == "rendered" else EXIT_NOT_RENDERED


# ---------------------------------------------------------------------------------------------- the vignette reel
STILL_KINDS = ("opening", "unpictured", "verdict", "closing")
SPLIT_HALF = (638, 680)              # a placement split's half (skill data scene-policy vignettes.split): two halves, a
SPLIT_DIVIDER, SPLIT_STRIP = 4, 40   # 4 px divider and a 40 px label strip make the 1280x720 frame with nothing scaled


def reel_stills_words(page: dict) -> int:
    import reel_plan
    return reel_plan.page_words(page)


def vignette_seconds(n: int, speech: float, hold: dict) -> list[float]:
    """How long each of a vignette's n frames is held: every frame but the last for its fixed hold (without, with), the
    last until the narration ends (at least highlightMin). A three-frame vignette is without / with / highlight; a
    two-frame navmesh vignette is winning / historical, whose last frame holds for the words too."""
    fixed = [float(hold.get("without", 2.5)), float(hold.get("with", 2.5))][:max(n - 1, 0)]
    return fixed + [max(float(hold.get("highlightMin", 4.0)), speech + 0.25 - sum(fixed))] if n else []


def frame_is_clean(media: dict, board: dict | None) -> bool:
    """An evidence frame carries no added text: it came from viewprobe --report-mode (the capture step records its
    argv), or a reviewed storyboard declares the clean frame and the visible subject."""
    argv = (media.get("provenance") or {}).get("argv") or []
    if "--report-mode" in argv:
        return True
    board = board or {}
    review = board.get("visualReview") or {}
    return board.get("cleanFrame") is True and review.get("subjectVisible") is True and review.get("addedText") == "none"


def sequence_command(items: list[tuple[str, int]], audio: str, total_frames: int) -> list[str]:
    """N still images, each held for its frame count, then one narration: the vignette and still-page segment."""
    args, chains = [], []
    for i, (path, frames) in enumerate(items):
        args += ["-loop", "1", "-framerate", str(FPS), "-t", f"{frames / FPS + 0.5:.3f}", "-i", path]
        chains.append(f"[{i}:v]{FIT},pad={WIDTH}:{HEIGHT}:(ow-iw)/2:(oh-ih)/2:{CANVAS_COLOUR},setsar=1,fps={FPS},"
                      f"trim=end_frame={frames},setpts=PTS-STARTPTS[v{i}]")
    n = len(items)
    d = f"{total_frames / FPS:.3f}"
    graph = ";".join(chains) + ";" + "".join(f"[v{i}]" for i in range(n)) + f"concat=n={n}:v=1:a=0,format=yuv420p[out];" \
        + f"[{n}:a]apad=whole_dur={d}[aud]"
    return (args + ["-i", audio, "-filter_complex", graph, "-map", "[out]", "-map", "[aud]", "-t", d,
                    "-frames:v", str(total_frames)] + ENCODE + ["seg.mp4"])


def split_command(left: str, right: str, strip: str, audio: str, total_frames: int) -> list[str]:
    """A placement split: the left half (the setup without the mod), a divider, the right half (the prediction), over the
    label strip, held for one narration. No evidence pixel is scaled."""
    d = f"{total_frames / FPS:.3f}"
    hold = f"{total_frames / FPS + 0.5:.3f}"
    args = []
    for path in (left, right, strip):
        args += ["-loop", "1", "-framerate", str(FPS), "-t", hold, "-i", path]
    args += ["-f", "lavfi", "-t", hold, "-i", f"color=c={CANVAS_COLOUR}:s={SPLIT_DIVIDER}x{SPLIT_HALF[1]}:r={FPS}"]
    graph = (f"[0:v][3:v][1:v]hstack=inputs=3[top];[top][2:v]vstack=inputs=2,setsar=1,fps={FPS},"
             f"trim=end_frame={total_frames},setpts=PTS-STARTPTS,format=yuv420p[out];[4:a]apad=whole_dur={d}[aud]")
    return (args + ["-i", audio, "-filter_complex", graph, "-map", "[out]", "-map", "[aud]", "-t", d,
                    "-frames:v", str(total_frames)] + ENCODE + ["seg.mp4"])


def render_vignettes(a, run, pres: dict, pres_path: Path, out: Path, ffmpeg: dict) -> int:
    """Cut the vignette reel the plan's `reel` block describes: generated stills and without/with/highlight
    vignettes, one narration each. Refuses rather than substituting anything."""
    import reel_stills
    reel = pres["reel"]
    if reel.get("status") != "planned":
        return refuse(f"reel-refused: {reel.get('reason') or 'the reel plan is not planned'}", out)
    reel_stills.keep_names(reel.get("modNames") or {}, reel.get("shortForms"))
    chapters = reel.get("chapters") or []
    closings_ = [i for i, c in enumerate(chapters) if c.get("kind") == "closing"]
    if not chapters or chapters[0].get("kind") != "opening" or closings_ not in ([], [len(chapters) - 1]):
        return refuse("storyboard-refused: the reel plan must open with the opening still, and a closing, if it has one, "
                      "must be its last scene", out)
    settings_reel = ((pres.get("settings") or {}).get("reel") or {})
    max_seconds = a.max_seconds if a.max_seconds is not None else settings_reel.get("maxSeconds")
    try:
        font, bold = reel_stills.font_files(getattr(a, "font", None), settings_reel)
    except reel_stills.FontMissing as e:
        return refuse(f"font-missing: {e}", out)
    font_doc = {"path": str(font), "sha256": sha256_file(font), "boldPath": str(bold), "boldSha256": sha256_file(bold)}
    bundle_dir = resolve_bundle_dir(pres, pres_path, a.bundle)
    # the plan's own media and the sibling bundle's frames the reel joined (the "without" setup's)
    media_by_id = {m.get("id"): m for m in list(pres.get("media") or []) + list(reel.get("media") or [])}
    hold = reel.get("holdSeconds") or {}
    stills_dir = out / "stills"
    stills_dir.mkdir(parents=True, exist_ok=True)
    audio_dir = out / "narration"
    audio_dir.mkdir(parents=True, exist_ok=True)
    segments, errors = [], []
    for ch in chapters:
        cid, kind = ch.get("id"), ch.get("kind")
        # the final speech and subtitle string: no form key or editor ID (C1961)
        narration = reel_stills.spoken_text(" ".join(str(ch.get("narration") or "").split()))
        if not narration:
            errors.append(f"{cid}: scene has no narration")
            continue
        seg = {"chapterId": cid, "chapterKind": kind, "narration": narration, "narrationOverlaid": bool(ch.get("narrationOverlaid")),
               "narrationAudio": ch.get("narrationAudio"), "items": [], "clash": ch.get("clash"), "patch": ch.get("patch")}
        if kind in STILL_KINDS:
            pages = (ch.get("still") or {}).get("pages") or []
            if not pages:
                errors.append(f"{cid}: still scene has no pages")
                continue
            # one segment per page as drawn: each page says its own words and is held for them (the storyboard cut the
            # same parts, so a prepared page audio is that page's)
            parts = reel_stills.still_parts(pages, font, bold)
            page_audio = ch.get("pageAudio") or {}
            if len(parts) > 1 and ch.get("narrationAudio") and not page_audio:
                errors.append(f"{cid}: the still scene has {len(parts)} pages but one narration; prepare one narration per "
                              "page (the storyboard packet lists each page as its own scene)")
                continue
            for n, page in enumerate(parts):
                ph = reel_stills.page_hash(page)
                png = stills_dir / f"{cid}-p{n}-{ph[:10]}-{font_doc['sha256'][:8]}-{reel_stills.names_digest()}.png"
                if not png.is_file():
                    try:
                        reel_stills.draw_page(page, font, bold).save(str(png), format="PNG")
                    except ValueError as e:
                        errors.append(f"{cid}: {e}")
                        break
                say = reel_stills.spoken_text(page["say"] or narration)
                segments.append({"chapterId": cid, "chapterKind": kind, "page": n, "kind": "still", "narration": say,
                                 "narrationOverlaid": False, "clash": ch.get("clash"), "patch": ch.get("patch"),
                                 "narrationAudio": page_audio.get(str(n)) or (ch.get("narrationAudio") if len(parts) == 1 else None),
                                 "dataSha256": sha256_text(json.dumps(page, sort_keys=True, ensure_ascii=False)),
                                 "items": [{"page": n, "layout": page.get("layout"), "path": str(png), "sha256": sha256_file(png),
                                            "dataSha256": ph, **({"seconds": float(page["seconds"])} if page.get("seconds") else {})}]})
            continue
        elif kind == "vignette":
            seg["kind"] = "sequence"
            frames = ch.get("frames") or []
            poses, sizes = set(), set()
            for fr in frames:
                mid = fr.get("mediaId")
                m = media_by_id.get(mid)
                if not mid or m is None:
                    errors.append(f"{cid}: the {fr.get('state')} frame has no captured media yet (run capture_acquire.py "
                                  f"capture for this vignette, or record its refusal with evalreport plan --capture-results)")
                    break
                if m.get("assessed") is not True:
                    errors.append(f"{cid}: media {mid} is unassessed ({m.get('reason')})")
                    break
                path = (bundle_dir / m.get("path", "")) if bundle_dir else Path(m.get("path", ""))
                if not path.is_file() or (m.get("sha256") and sha256_file(path) != m["sha256"]):
                    errors.append(f"{cid}: media file {m.get('path')} is missing or changed since planning")
                    break
                if not frame_is_clean(m, fr.get("storyboard")):
                    errors.append(f"{cid}: the {fr.get('state')} frame is not shown to be a clean frame (no --report-mode "
                                  f"capture and no reviewed storyboard)")
                    break
                poses.add((m.get("provenance") or {}).get("poseId"))
                hdr = probe_header(ffmpeg["path"], path)
                sizes.add((hdr.get("width"), hdr.get("height")))
                item_ = {"state": fr.get("state"), "mediaId": mid, "armId": (m.get("subject") or {}).get("armId"),
                         "path": str(path), "sha256": sha256_file(path), "poseId": (m.get("provenance") or {}).get("poseId")}
                if fr.get("label"):
                    # the reel's label strip under the evidence frame: the setup, or which marked shape is which colour
                    lab_ = stills_dir / f"{cid}-{fr.get('state')}-{sha256_text(fr['label'] + item_['sha256'])[:12]}-{reel_stills.names_digest()}.png"
                    if not lab_.is_file():
                        reel_stills.label_frame(path, fr["label"], font, bold).save(str(lab_), format="PNG")
                    item_.update(evidencePath=item_["path"], evidenceSha256=item_["sha256"], label=fr["label"],
                                 path=str(lab_), sha256=sha256_file(lab_))
                seg["items"].append(item_)
            else:
                if len(poses) != 1 or None in poses or len(sizes) != 1:
                    errors.append(f"{cid}: the vignette's frames were not taken at one pose and canvas "
                                  f"(poses {sorted(map(str, poses))}, sizes {sorted(map(str, sizes))})")
        elif kind == "split":
            seg["kind"] = "split"
            clash = ch.get("clash") or {}
            frames = ch.get("frames") or []
            if [f.get("state") for f in frames] != ["left", "right"]:
                errors.append(f"{cid}: a split shows its left then its right half, got {[f.get('state') for f in frames]}")
                continue
            poses, rel = set(), []
            for fr in frames:
                mid = fr.get("mediaId")
                m = media_by_id.get(mid)
                if not mid or m is None:
                    errors.append(f"{cid}: the {fr.get('state')} half has no captured media yet (run capture_acquire.py "
                                  f"capture for this split, or record its refusal with evalreport plan --capture-results)")
                    break
                if m.get("assessed") is not True:
                    errors.append(f"{cid}: media {mid} is unassessed ({m.get('reason')})")
                    break
                path = (bundle_dir / m.get("path", "")) if bundle_dir else Path(m.get("path", ""))
                if not path.is_file() or (m.get("sha256") and sha256_file(path) != m["sha256"]):
                    errors.append(f"{cid}: media file {m.get('path')} is missing or changed since planning")
                    break
                if not frame_is_clean(m, fr.get("storyboard")):
                    errors.append(f"{cid}: the {fr.get('state')} half is not shown to be a clean frame")
                    break
                hdr = probe_header(ffmpeg["path"], path)
                if (hdr.get("width"), hdr.get("height")) != SPLIT_HALF:
                    errors.append(f"{cid}: the {fr.get('state')} half is {hdr.get('width')}x{hdr.get('height')}, "
                                  f"not {SPLIT_HALF[0]}x{SPLIT_HALF[1]}")
                    break
                prov = m.get("provenance") or {}
                poses.add(prov.get("poseId"))
                rel.append(bool(prov.get("relativePose")))
                seg["items"].append({"state": fr.get("state"), "mediaId": mid, "armId": (m.get("subject") or {}).get("armId"),
                                     "path": str(path), "sha256": sha256_file(path), "poseId": prov.get("poseId"),
                                     "relativePose": bool(prov.get("relativePose"))})
            if len(seg["items"]) != 2:
                continue
            labels = clash.get("labels") or {}
            relative = len(rel) == 2 and all(rel)           # both halves say so in their own capture provenance
            if not (len(poses) == 1 and None not in poses) and not relative:
                errors.append(f"{cid}: the split's halves were not taken at one pose, nor marked as the same pose "
                              f"relative to each placement (poses {sorted(map(str, poses))})")
                continue
            if not (labels.get("left") and labels.get("right")):
                errors.append(f"{cid}: a split needs its left and right labels")
                continue
            strip = reel_stills.draw_strip(labels, font, bold, size=(WIDTH, SPLIT_STRIP),
                                           split_at=SPLIT_HALF[0] + SPLIT_DIVIDER // 2)
            sp = stills_dir / f"{cid}-strip-{sha256_text(json.dumps(labels, sort_keys=True))[:10]}-{font_doc['sha256'][:8]}-{reel_stills.names_digest()}.png"
            strip.save(str(sp), format="PNG")
            seg["strip"] = {"path": str(sp), "sha256": sha256_file(sp), "labels": labels}
        else:
            errors.append(f"{cid}: unknown reel scene kind {kind!r}")
            continue
        segments.append(seg)
    if errors:
        return refuse("storyboard-refused: " + "; ".join(errors), out)
    # narration audio, then the frame count of every item from it
    for i, s in enumerate(segments):
        s["index"] = i
        supplied = s.get("narrationAudio")
        if supplied:
            apath, method = ((bundle_dir / supplied) if bundle_dir and not Path(supplied).is_absolute() else Path(supplied)), "supplied"
        else:
            if not a.voice or not a.voice.strip():
                return refuse(f"narration-refused: {s['chapterId']}: prepare narrationAudio with evalspeech.py, or select an "
                              "installed Windows voice with --voice; no automatic voice fallback", out)
            helper = HERE / "winrt-tts" / "Program.cs"
            key = sha256_text(s["narration"] + "\n" + a.voice + "\n" + sha256_file(helper))[:12]
            apath, method = audio_dir / f"{i:03d}-{key}.wav", "Windows WinRT " + a.voice
            if not apath.is_file():
                ok, detail = synthesize_windows(s["narration"], apath, a.voice, run.run_id)
                if not ok:
                    return refuse(f"narration-refused: {s['chapterId']}: {detail}", out)
                method = detail
        if not apath.is_file():
            return refuse(f"narration-refused: {s['chapterId']}: audio file {apath} is absent", out)
        ah = probe_header(ffmpeg["path"], apath)
        if not ah.get("hasAudio") or not ah.get("durationSeconds"):
            return refuse(f"narration-refused: {s['chapterId']}: audio file {apath} is not decodable audio", out)
        s["audioPath"], s["audioSha256"], s["audioMethod"] = str(apath), sha256_file(apath), method
        speech = float(ah["durationSeconds"])
        s["speechSeconds"] = round(speech, 3)
        if s["kind"] == "split":
            secs = [speech + float(hold.get("stillPause", 1.5))] * 2          # both halves for the whole scene
        elif s["kind"] == "still":
            # a page holds for its own narration and a short pause, or for its reading time if longer; a page whose
            # reading time outruns its narration by more than the allowed silence is refused, never played silent
            pause_ = float(hold.get("stillPause", 1.5))
            read_ = float(s["items"][0].get("seconds") or 0)
            max_sil_ = float(hold.get("maxSilence", 3.0))
            if read_ - speech > max_sil_:
                return refuse(f"narration-refused: {s['chapterId']} page {s.get('page', 0)}: its reading time ({read_:.1f} s) "
                              f"outruns its narration ({speech:.1f} s) by more than {max_sil_:.1f} s of silence", out)
            secs = [max(speech + pause_, read_)]
        else:
            secs = vignette_seconds(len(s["items"]), speech, hold)
        for it, sec in zip(s["items"], secs):
            it["frames"] = int(round(sec * FPS))
        s["plannedFrames"] = s["items"][0]["frames"] if s["kind"] == "split" else sum(it["frames"] for it in s["items"])
        s["durationSeconds"] = round(s["plannedFrames"] / FPS, 3)
    planned_seconds = round(sum(s["durationSeconds"] for s in segments), 3)
    if max_seconds is not None and planned_seconds > float(max_seconds):
        return refuse(f"storyboard-refused: the reel's narrated scenes take {planned_seconds} s, over the {max_seconds} s budget", out)
    if a.dry_run:
        print(f"dry run: {len(segments)} reel scene(s), {planned_seconds} s planned; ffmpeg {ffmpeg['version']}")
        for s in segments:
            print(f"  #{s['index']:>2} {s['chapterId']:<5} {s['kind']:<9} {s['durationSeconds']:>6.2f}s  "
                  + ", ".join(str(it.get('state') or it.get('layout')) for it in s["items"]) + f"  {s['narration'][:70]}")
        return EXIT_OK
    status_doc = {"schemaVersion": 1, "status": "failed", "presentationId": pres.get("presentationId"), "format": "vignettes",
                  "generatedUtc": utcnow(), "out": str(out), "ffmpeg": {"path": ffmpeg["path"], "version": ffmpeg["version"]},
                  "planned": {"seconds": planned_seconds, "segments": len(segments)}, "errors": [], "outputs": {}}
    cursor, failed = 0, False
    run.stage("segments", total=len(segments), unit="segments")
    for s in segments:
        key = sha256_text(json.dumps({"layout": LAYOUT_VERSION + "-vignettes", "items": [(it["sha256"], it["frames"]) for it in s["items"]],
                                      "kind": s["kind"], "strip": (s.get("strip") or {}).get("sha256"),
                                      "audio": s["audioSha256"], "ffmpeg": ffmpeg["version"]}, sort_keys=True))[:16]
        work = out / "cache" / key
        mp4 = work / "seg.mp4"
        s["cacheKey"], s["file"] = key, f"cache/{key}/seg.mp4"
        side = load_json(work / "seg.json")
        if not (side and mp4.is_file() and side.get("sha256") == sha256_file(mp4)):
            work.mkdir(parents=True, exist_ok=True)
            cmd = (split_command(s["items"][0]["path"], s["items"][1]["path"], s["strip"]["path"], s["audioPath"], s["plannedFrames"])
                   if s["kind"] == "split" else
                   sequence_command([(it["path"], it["frames"]) for it in s["items"]], s["audioPath"], s["plannedFrames"]))
            r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "warning"] + cmd, cwd=work)
            if r.returncode != 0 or not mp4.is_file():
                failed = True
                status_doc["errors"].append(f"segment {s['index']} ({s['chapterId']}): ffmpeg exit {r.returncode}: "
                                            + " ".join(r.stderr.split())[-400:])
                run.advance()
                continue
            side = {"frames": count_frames(ffmpeg["path"], mp4), "sha256": sha256_file(mp4)}
            write_json(work / "seg.json", side)
        if side.get("frames") != s["plannedFrames"]:
            failed = True
            status_doc["errors"].append(f"segment {s['index']} ({s['chapterId']}): {side.get('frames')} frames decoded, "
                                        f"{s['plannedFrames']} planned")
        s["reelInterval"] = {"startFrame": cursor, "frames": s["plannedFrames"], "startSeconds": round(cursor / FPS, 3),
                             "endSeconds": round((cursor + s["plannedFrames"]) / FPS, 3)}
        inner = cursor
        for it in s["items"]:
            it["reelInterval"] = {"startFrame": inner, "frames": it["frames"]}
            if s["kind"] != "split":                       # a split's halves share the scene's whole interval
                inner += it["frames"]
        cursor += s["plannedFrames"]
        run.advance()
    reel_path, probe = out / "reel.mp4", None
    if not failed:
        (out / "concat.txt").write_text("".join(f"file '{s['file']}'\n" for s in segments), encoding="utf-8")
        r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "error", "-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy",
                                        "-movflags", "+faststart", "reel.mp4"], cwd=out)
        if r.returncode != 0 or not reel_path.is_file():
            failed = True
            status_doc["errors"].append("concat: " + " ".join(r.stderr.split())[-400:])
        else:
            probe = probe_video(ffmpeg["path"], reel_path)
            probe["sha256"] = sha256_file(reel_path)
            status_doc["probe"] = probe
            checks = []
            if probe.get("frames") != cursor:
                checks.append(f"frames {probe.get('frames')} decoded, {cursor} expected")
            if (probe.get("width"), probe.get("height")) != (WIDTH, HEIGHT) or probe.get("fps") != FPS:
                checks.append(f"{probe.get('width')}x{probe.get('height')} at {probe.get('fps')} fps")
            if not probe.get("hasAudio") or not probe.get("audioDecoded"):
                checks.append("no independently decoded audio stream")
            # the decoded audio itself: no stretch of silence longer than the allowed pause
            max_sil_ = float(hold.get("maxSilence", 3.0))
            # (a vignette may hold its frames past its words; a still page never shows silent text)
            quiet_ = silences(ffmpeg["path"], reel_path, float(hold.get("silenceNoiseDb", -40)), max_sil_)
            on_pages_ = []
            for q in quiet_:
                for s in segments:
                    iv = s.get("reelInterval") or {}
                    if s["kind"] != "still" or not iv:
                        continue
                    ov = min(q["end"], iv["endSeconds"]) - max(q["start"] or 0.0, iv["startSeconds"])
                    if ov > max_sil_:
                        on_pages_.append({**q, "chapterId": s["chapterId"], "page": s.get("page"), "overlap": round(ov, 2)})
            status_doc["silences"], status_doc["silencesOnPages"] = quiet_, on_pages_
            if on_pages_:
                checks.append(f"silence longer than {max_sil_:.1f} s on a still page in the decoded audio: " + "; ".join(
                    f"{q['overlap']:.1f} s on {q['chapterId']} page {q['page']} from {q['start']:.1f} s" for q in on_pages_[:5]))
            if checks:
                failed = True
                status_doc["errors"].append("reel.mp4 does not match the plan: " + "; ".join(checks))
    seg_out = [{"index": s["index"], "chapterId": s["chapterId"], "chapterKind": s["chapterKind"], "kind": s["kind"],
                "page": s.get("page"), "speechSeconds": s.get("speechSeconds"),
                "narration": s["narration"], "narrationOverlaid": s["narrationOverlaid"], "clash": s.get("clash"),
                "patch": s.get("patch"), "dataSha256": s.get("dataSha256"), "reelInterval": s.get("reelInterval"),
                "frames": [{k: v for k, v in it.items() if k != "path"} for it in s["items"]],
                "strip": ({k: v for k, v in s["strip"].items() if k != "path"} if s.get("strip") else None),
                "audio": {"sha256": s["audioSha256"], "method": s["audioMethod"]}, "cacheKey": s.get("cacheKey"),
                "file": s.get("file")} for s in segments]
    write_json(out / "reel-map.json", {"schemaVersion": 2, "format": "vignettes", "presentationId": pres.get("presentationId"),
                                       "generatedUtc": utcnow(), "stillFont": font_doc,
                                       "canvas": {"width": WIDTH, "height": HEIGHT, "fps": FPS, "layoutVersion": LAYOUT_VERSION + "-vignettes"},
                                       "reel": ({"path": "reel.mp4", "sha256": probe.get("sha256"), "frames": probe.get("frames"),
                                                 "durationSeconds": probe.get("durationSeconds")} if probe else None),
                                       "segments": seg_out})
    for s in segments:
        s.setdefault("reelInterval", None)
    write_srt(out / "reel.srt", segments)
    status_doc["status"] = "failed" if failed else "rendered"
    for name in ("reel.mp4", "reel.srt", "reel-map.json"):
        if (out / name).is_file():
            status_doc["outputs"][name] = {"sha256": sha256_file(out / name), "bytes": (out / name).stat().st_size}
            run.artifact(str(out / name))
    write_json(out / "reel-status.json", status_doc)
    print(f"reel: {status_doc['status']} - {len(segments)} scene(s)" + (f", {probe.get('durationSeconds')} s / {probe.get('frames')} frames"
                                                                         if probe else "") + f"; {out / 'reel-status.json'}")
    for e in status_doc["errors"]:
        print("  " + e, file=sys.stderr)
    return EXIT_OK if status_doc["status"] == "rendered" else EXIT_NOT_RENDERED


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="evalreel", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("storyboard-packet", help="write the scenes that require human visual review")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--bundle", default=None, help="relocated evidence bundle used to plan representative captures")
    p.set_defaults(fn=cmd_storyboard_packet)
    r = sub.add_parser("render", help="cut the reel of a presentation plan")
    r.add_argument("--presentation", required=True, help="OUT/presentation.json (schema 1)")
    r.add_argument("--out", required=True, help="output directory (reel.mp4, reel.srt, reel-map.json, "
                                                "reel-status.json, contact-sheet.png, cache/)")
    r.add_argument("--max-seconds", type=float, default=None,
                   help="drop trailing finding chapters over this budget (default: the plan's settings.reel.maxSeconds)")
    r.add_argument("--chapters", default=None, help="comma-separated chapter ids to include (default: all)")
    r.add_argument("--bundle", default=None, help="where the plan's bundle-relative media paths resolve "
                                                  "(default: source.bundleDir, relative to the presentation)")
    r.add_argument("--ffmpeg", default=None, help="an explicit ffmpeg binary (first in the resolution order)")
    r.add_argument("--voice", default=None, help="explicitly opt into an installed Windows WinRT voice; otherwise supply narrationAudio from evalspeech.py")
    r.add_argument("--storyboard", default=None, help="reviewed storyboard packet; overlays visual direction in memory without changing presentation.json")
    r.add_argument("--dry-run", action="store_true", help="print the segment plan and render nothing")
    r.add_argument("--font", default=None, help="the TrueType font the reel's stills are drawn with (default: "
                                                "HOUSECARL_REEL_FONT, then the skill's reel.stillFont; never a built-in font)")
    r.set_defaults(fn=cmd_render)
    a = ap.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    raise SystemExit(main())
