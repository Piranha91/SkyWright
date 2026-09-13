#!/usr/bin/env python
"""evalreel — the diagnostic reel of an evaluation presentation (Q45, D6).

    python _geometry-harness/evalreel.py render --presentation OUT/presentation.json --out OUT/reel
        [--max-seconds 120] [--chapters C-01,C-03] [--bundle DIR] [--ffmpeg EXE] [--dry-run]

Consumes the presentation plan (standards/HOUSECARL_EVALUATION_PRESENTATION.md, schema 1) and cuts a 1280x720,
30 fps, H.264 reel: an opening card, one segment per chapter caption — an image held for 5 s, a clip's
`sourceInterval` cut and conformed to 30 fps, or a text card labelled "no qualified media" — and a closing card
listing what the evaluation did not establish. Editing, layout and declared overlays only: nothing is
reconstructed, generated or filled in.

Layout (`.claude/skills/evaluation-reporting/references/reel-layout.md`): the image region is the top 1280x600,
the source scaled to fit and letterboxed (never stretched, never cropped); the bottom 120 px band carries the
label (`arm · state · media kind`, top-left of the band) and the caption. Nothing is drawn over the image region.

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
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import textwrap
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

EXIT_OK, EXIT_NOT_RENDERED, EXIT_REFUSED = 0, 1, 2

# ---------------------------------------------------------------------------------------------- the layout
LAYOUT_VERSION = "1"
WIDTH, HEIGHT, FPS = 1280, 720, 30
IMAGE_H = 600                      # the image region: y 0..600
BAND_H = HEIGHT - IMAGE_H          # the caption band: y 600..720
CANVAS_COLOUR = "0x0f1218"         # behind a letterboxed source
BAND_COLOUR = "0x1c2230"
OPENING_SECONDS, CLOSING_SECONDS, IMAGE_SECONDS, MISSING_SECONDS = 6.0, 8.0, 5.0, 5.0
CAPTION_WRAP, CAPTION_WRAP_SMALL = 84, 100     # characters per line at 26 px / 22 px Arial on 1200 px
CARD_BODY_MAX_LINES = 14
IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"}
MISSING_LABEL = "no qualified media"
DURATION_TOLERANCE = 0.1

# ASS colours are &HAABBGGRR
ASS_STYLES = (
    # name, font, size, primary, outline colour, bold, border style (1 outline, 3 opaque box), outline px,
    # alignment (numpad: 7 top-left, 2 bottom-centre), marginL, marginR, marginV
    ("Label", "Arial", 20, "&H00D8E0F0", "&H00101418", 0, 1, 1, 7, 20, 20, IMAGE_H + 6),
    ("Caption", "Arial", 26, "&H00FFFFFF", "&H00101418", 0, 1, 1, 2, 40, 40, 10),
    ("Title", "Arial", 42, "&H00FFFFFF", "&H00101418", 1, 1, 1, 7, 64, 64, 60),
    ("Body", "Arial", 28, "&H00E6EAF0", "&H00101418", 0, 1, 1, 7, 64, 64, 136),
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
def ffmpeg_find(explicit: str | None = None) -> dict | None:
    """D6's resolution order (the DevBench harness's `ffmpeg_find` order, restated here rather than imported
    from the skill script): the flag, HOUSECARL_FFMPEG, DBH_FFMPEG, PATH, then the bundled copies."""
    for cand, how in ((explicit, "--ffmpeg"), (os.environ.get("HOUSECARL_FFMPEG"), "HOUSECARL_FFMPEG"),
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
    out = {"durationSeconds": None, "width": None, "height": None, "fps": None}
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
    h["bytes"] = path.stat().st_size if path.exists() else None
    return h


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


def wrap_caption(text: str) -> tuple[str, bool]:
    """The caption as ASS text: at most two lines at 26 px, else three at 22 px, else truncated and flagged.
    Truncation is reported in the map (`captionTruncated`), never silent."""
    text = " ".join((text or "").split())
    lines = textwrap.wrap(text, CAPTION_WRAP) or [""]
    if len(lines) <= 2:
        return ass_escape("\n".join(lines)), False
    lines = textwrap.wrap(text, CAPTION_WRAP_SMALL)
    truncated = len(lines) > 3
    if truncated:
        lines = lines[:3]
        lines[-1] = lines[-1][:CAPTION_WRAP_SMALL - 2].rstrip() + " …"
    return "{\\fs22}" + ass_escape("\n".join(lines)), truncated


def card_events(title: str, body_lines: list[str], label: str) -> list[tuple[str, str]]:
    """A text card: a title and up to CARD_BODY_MAX_LINES body lines, each wrapped to the card width, plus
    the persistent label in the band — every segment carries one."""
    wrapped: list[str] = []
    for line in body_lines:
        wrapped += textwrap.wrap(" ".join(str(line).split()), 78) or [""]
    if len(wrapped) > CARD_BODY_MAX_LINES:
        kept = wrapped[:CARD_BODY_MAX_LINES - 1]
        kept.append(f"… and {len(wrapped) - len(kept)} more line(s): see the report")
        wrapped = kept
    return [("Label", ass_escape(label)), ("Title", ass_escape(title)), ("Body", ass_escape("\n".join(wrapped)))]


# ---------------------------------------------------------------------------------------------- the plan
def resolve_bundle_dir(pres: dict, pres_path: Path, override: str | None) -> Path | None:
    """Where the plan's bundle-relative media paths resolve: the flag, else `source.bundleDir` (absolute, or
    relative to the presentation's own directory, then to the working directory)."""
    if override:
        return Path(override)
    given = ((pres.get("source") or {}).get("bundleDir")) or "."
    p = Path(given)
    if p.is_absolute():
        return p
    for base in (pres_path.parent, Path.cwd()):
        if (base / p).is_dir():
            return (base / p).resolve()
    return (pres_path.parent / p).resolve()


def opening_lines(pres: dict) -> tuple[str, list[str]]:
    scope = pres.get("scope") or {}
    src = pres.get("source") or {}
    targets = scope.get("targets") or []
    target_names = [t.get("name") or t.get("file") or str(t) if isinstance(t, dict) else str(t) for t in targets]
    arms = scope.get("arms") or []
    completion = scope.get("completion") or {}
    claim = completion.get("claim") or ((src.get("verify") or {}).get("claim")) or "unknown"
    missing = completion.get("missingRequired")
    findings = pres.get("findings") or []
    kinds: dict[str, int] = {}
    for f in findings:
        kinds[f.get("kind", "finding")] = kinds.get(f.get("kind", "finding"), 0) + 1
    lines = [f"Target: {', '.join(target_names) or '(unnamed)'}"]
    lines.append("Arms: " + ("; ".join(
        f"{a.get('armId')} ({a.get('kind')}{', ' + Path(a['candidateFile']).name if a.get('candidateFile') else ''})"
        for a in arms) or "(none)"))
    if completion.get("complete") is True:
        lines.append("Completion: every required check has an accepted result")
    else:
        lines.append("Completion: the evaluation is incomplete"
                     + (f" — {missing} required check(s) without an accepted result" if missing is not None else ""))
    lines.append(f"Claim: {claim}")
    lines.append("Findings: " + (", ".join(f"{n} {k}" for k, n in sorted(kinds.items())) or "none listed"))
    lines.append(f"Bundle plan {src.get('planId', '?')} · presentation {pres.get('presentationId', '?')}")
    return "Evaluation reel", lines


def closing_lines(pres: dict) -> tuple[str, list[str]]:
    scope = pres.get("scope") or {}
    completion = scope.get("completion") or {}
    lines = []
    by_cause = completion.get("byCause") or {}
    missing = completion.get("missingRequired")
    if missing:
        lines.append(f"Required checks still without an accepted result: {missing}"
                     + (" (" + ", ".join(f"{k} {v}" for k, v in sorted(by_cause.items())) + ")" if by_cause else ""))
    else:
        lines.append("Required checks still without an accepted result: none listed")
    for f in (pres.get("findings") or []):
        if f.get("kind") == "missing":
            lines.append(f"  missing: {f.get('checkId') or f.get('title')} [{f.get('armId') or '-'}]")
    unknown = completion.get("unknownApplicability") or []
    if unknown:
        lines.append(f"Applicability unknown: {', '.join(map(str, unknown))}")
    un = pres.get("unassessed") or {}
    areas = [f"{k} ({len(v) if isinstance(v, (list, dict)) else v})" for k, v in un.items() if k != "media" and v]
    if areas:
        lines.append("Unassessed: " + ", ".join(areas))
    media_un = [m for m in (pres.get("media") or []) if m.get("assessed") is False] or (un.get("media") or [])
    if media_un:
        lines.append(f"Media not assessed: {len(media_un)} item(s) — shown as \"{MISSING_LABEL}\" cards")
    lines.append("What was not measured is not shown; nothing in this reel is reconstructed.")
    return "What this reel does not show", lines


def plan_segments(pres: dict, ffmpeg: dict, bundle_dir: Path | None, chapter_filter: set[str] | None,
                  max_seconds: float | None) -> tuple[list[dict], list[dict]]:
    """The chapters of the plan, as segments — one per caption. A segment with qualified media whose file is
    absent, altered or undecodable becomes a `missing` card with `status: missing-media` (the reel is then
    `partial`); a chapter planned without qualified media is a `missing` card with `status: no-media`."""
    media_by_id = {m.get("id"): m for m in (pres.get("media") or [])}
    chapters = [c for c in (pres.get("chapters") or [])
                if chapter_filter is None or c.get("id") in chapter_filter]
    probes: dict[str, dict] = {}
    segments: list[dict] = []

    def base(ch: dict, kind: str) -> dict:
        return {"chapterId": ch.get("id"), "chapterKind": ch.get("kind"), "kind": kind,
                "findingIds": list(ch.get("findingIds") or []), "mediaId": None, "evidenceIds": [],
                "source": None, "sourceInterval": None, "caption": "", "label": "", "status": "planned",
                "error": None, "durationSeconds": 0.0, "events": [], "captionTruncated": False}

    def missing_card(ch: dict, cap: dict | None, media_id, reason: str, status: str) -> dict:
        s = base(ch, "missing")
        s["mediaId"] = media_id
        s["evidenceIds"] = list((cap or {}).get("evidenceIds") or [])
        s["caption"] = (cap or {}).get("text") or ch.get("title") or ""
        s["label"] = MISSING_LABEL
        s["status"] = status
        s["error"] = reason
        s["durationSeconds"] = MISSING_SECONDS
        body = [f"Chapter {ch.get('id')}: {ch.get('title') or ''}", "", f"Why: {reason}"]
        if s["caption"]:
            body.insert(1, s["caption"])
        s["events"] = card_events(MISSING_LABEL, body, MISSING_LABEL)
        return s

    for ch in chapters:
        ck = ch.get("kind")
        if ck in ("opening", "closing"):
            s = base(ch, f"card-{ck}")
            s["durationSeconds"] = OPENING_SECONDS if ck == "opening" else CLOSING_SECONDS
            title, lines = opening_lines(pres) if ck == "opening" else closing_lines(pres)
            caps = ch.get("captions") or []
            if caps:
                lines = [c.get("text") or "" for c in caps]
                s["evidenceIds"] = sorted({e for c in caps for e in (c.get("evidenceIds") or [])})
            s["label"] = ck
            s["caption"] = ch.get("title") or title
            s["events"] = card_events(ch.get("title") or title, lines, ck)
            segments.append(s)
            continue
        caps = ch.get("captions") or []
        if not caps:
            segments.append(missing_card(ch, None, None, "the chapter carries no caption and no media", "no-media"))
            continue
        for cap in caps:
            mid = cap.get("mediaId")
            row = media_by_id.get(mid)
            if mid is None or row is None:
                segments.append(missing_card(ch, cap, mid, "no media is bound to this caption", "no-media"))
                continue
            if row.get("assessed") is not True:
                segments.append(missing_card(ch, cap, mid, f"the media is unassessed: {row.get('reason')}", "no-media"))
                continue
            rel = row.get("path") or ""
            abs_path = (bundle_dir / rel) if bundle_dir else Path(rel)
            if not abs_path.is_file():
                segments.append(missing_card(ch, cap, mid, f"the media file {rel} is not on disk", "missing-media"))
                continue
            got = sha256_file(abs_path)
            if row.get("sha256") and got != row["sha256"]:
                segments.append(missing_card(ch, cap, mid, f"the media file {rel} hashes to {got[:12]}, not the "
                                             f"plan's {str(row['sha256'])[:12]}", "missing-media"))
                continue
            if got not in probes:
                ok, err = decodable(ffmpeg["path"], abs_path)
                probes[got] = {"ok": ok, "error": err,
                               "header": probe_header(ffmpeg["path"], abs_path) if ok else None}
            pr = probes[got]
            if not pr["ok"]:
                segments.append(missing_card(ch, cap, mid, f"the media file {rel} does not decode: {pr['error']}",
                                             "missing-media"))
                continue
            is_image = abs_path.suffix.lower() in IMAGE_SUFFIXES or row.get("mediaKind") in (
                "offline-render", "native-screenshot", "historical-illustration")
            s = base(ch, "image" if is_image else "clip")
            s["mediaId"] = mid
            s["evidenceIds"] = list(cap.get("evidenceIds") or [])
            s["caption"] = cap.get("text") or ""
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
                    segments.append(missing_card(ch, cap, mid, f"the source interval {start}-{end} s is empty",
                                                 "missing-media"))
                    continue
                s["sourceInterval"] = {"startSeconds": start, "endSeconds": end,
                                       "sourcePath": iv.get("sourcePath"), "sourceSha256": iv.get("sourceSha256"),
                                       "clampedToSource": clamped, "sourceDurationSeconds": src_dur,
                                       "sourceFps": pr["header"].get("fps")}
                s["durationSeconds"] = round(end - start, 3)
            cap_text, truncated = wrap_caption(s["caption"])
            s["captionTruncated"] = truncated
            s["events"] = [("Label", ass_escape(s["label"])), ("Caption", cap_text)]
            segments.append(s)

    omitted: list[dict] = []
    if max_seconds is not None:
        total = sum(s["durationSeconds"] for s in segments)
        droppable = [i for i, s in enumerate(segments) if not s["kind"].startswith("card-")]
        while total > max_seconds and droppable:
            i = droppable.pop()
            s = segments[i]
            total -= s["durationSeconds"]
            omitted.append({"chapterId": s["chapterId"], "mediaId": s["mediaId"], "caption": s["caption"],
                            "seconds": s["durationSeconds"], "reason": f"over the {max_seconds} s budget"})
            segments[i] = None
        segments = [s for s in segments if s is not None]
    for i, s in enumerate(segments):
        s["index"] = i
        s["plannedFrames"] = int(round(s["durationSeconds"] * FPS))
    return segments, omitted


# ---------------------------------------------------------------------------------------------- rendering
def cache_key(seg: dict, ffmpeg_version: str) -> str:
    params = {"layout": LAYOUT_VERSION, "kind": seg["kind"], "w": WIDTH, "h": HEIGHT, "fps": FPS,
              "source": (seg["source"] or {}).get("sha256"), "interval": seg["sourceInterval"],
              "duration": seg["durationSeconds"], "frames": seg["plannedFrames"], "caption": seg["caption"],
              "label": seg["label"], "events": seg["events"], "ffmpeg": ffmpeg_version}
    return sha256_text(json.dumps(params, sort_keys=True, ensure_ascii=False))[:16]


FILTER_TAIL = (f"drawbox=x=0:y={IMAGE_H}:w={WIDTH}:h={BAND_H}:color={BAND_COLOUR}:t=fill,"
               f"ass=seg.ass,format=yuv420p[out]")
FIT = f"scale={WIDTH}:{IMAGE_H}:force_original_aspect_ratio=decrease:flags=lanczos"
PLACE = f"overlay=x=(main_w-overlay_w)/2:y=({IMAGE_H}-overlay_h)/2:shortest=1"
ENCODE = ["-r", str(FPS), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
          "-an", "-movflags", "+faststart"]


def segment_command(seg: dict) -> list[str]:
    d = f"{seg['durationSeconds']:.3f}"
    colour = ["-f", "lavfi", "-t", d, "-i", f"color=c={CANVAS_COLOUR}:s={WIDTH}x{HEIGHT}:r={FPS}"]
    if seg["kind"] == "image":
        return (["-loop", "1", "-framerate", str(FPS), "-t", d, "-i", seg["source"]["absPath"]] + colour
                + ["-filter_complex", f"[0:v]{FIT},format=rgba[img];[1:v][img]{PLACE},{FILTER_TAIL}"]
                + ["-map", "[out]", "-frames:v", str(seg["plannedFrames"])] + ENCODE + ["seg.mp4"])
    if seg["kind"] == "clip":
        iv = seg["sourceInterval"]
        return (["-ss", f"{iv['startSeconds']:.3f}", "-t", d, "-i", seg["source"]["absPath"]] + colour
                + ["-filter_complex", f"[0:v]fps={FPS},{FIT},setsar=1[img];[1:v][img]{PLACE},{FILTER_TAIL}"]
                + ["-map", "[out]", "-frames:v", str(seg["plannedFrames"])] + ENCODE + ["seg.mp4"])
    return (colour + ["-filter_complex", f"[0:v]{FILTER_TAIL}", "-map", "[out]",
                      "-frames:v", str(seg["plannedFrames"])] + ENCODE + ["seg.mp4"])


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
    (work / "seg.ass").write_text(ass_document(seg["events"], seg["durationSeconds"]), encoding="utf-8")
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


def write_srt(path: Path, segments: list[dict]) -> int:
    cues = []
    for s in segments:
        iv = s.get("reelInterval")
        if not iv:
            continue
        text = s["caption"] if s["kind"] in ("image", "clip") else f"[{s['label']}] {s['caption']}".strip()
        cues.append(f"{len(cues) + 1}\n{srt_time(iv['startSeconds'])} --> {srt_time(iv['endSeconds'])}\n{text}\n")
    path.write_text("\n".join(cues) + ("\n" if cues else ""), encoding="utf-8")
    return len(cues)


def contact_sheet(out: Path, reel: Path, segments: list[dict], ffmpeg: dict) -> dict:
    """A frame at each segment's start and midpoint, tiled four across with the segment index burned in.
    Read from the produced reel, so it shows what a viewer would see."""
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
        for tag, t in (("start", iv["startSeconds"] + 0.5 / FPS),
                       ("mid", (iv["startSeconds"] + iv["endSeconds"]) / 2)):
            (frames_dir / "lab.ass").write_text(ass_document([("Sheet", ass_escape(f"#{s['index']} {tag} {t:.2f}s"))],
                                                            2.0, 320, 180), encoding="utf-8")
            r = run_ffmpeg(ffmpeg["path"], ["-loglevel", "error", "-ss", f"{t:.4f}", "-i", str(reel), "-frames:v", "1",
                                            "-vf", "scale=320:180,ass=lab.ass", f"f-{n:03d}.png"], cwd=frames_dir)
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


def cmd_render(a) -> int:
    pres_path = Path(a.presentation)
    out = Path(a.out)
    pres = load_json(pres_path)
    if not isinstance(pres, dict):
        return refuse(f"presentation-unreadable: {pres_path} is absent or not JSON")
    if pres.get("schemaVersion") != 1:
        return refuse(f"schema-unsupported: presentation schemaVersion {pres.get('schemaVersion')!r}, expected 1")
    if not isinstance(pres.get("chapters"), list):
        return refuse("presentation-incomplete: no `chapters` list")
    out.mkdir(parents=True, exist_ok=True)
    if not (os.environ.get("HOUSECARL_RUNLOG_DIR") or "").strip():
        os.environ["HOUSECARL_RUNLOG_DIR"] = str((out / "runlog").resolve())
    found = ffmpeg_find(a.ffmpeg)
    if not found:
        return refuse("ffmpeg-missing: no ffmpeg via --ffmpeg, HOUSECARL_FFMPEG, DBH_FFMPEG, PATH or a bundled copy "
                      "(ShareX, winget Gyan.FFmpeg, Program Files\\ffmpeg)", out)
    ffmpeg = ffmpeg_identity(found["exe"])
    ffmpeg["how"] = found["how"]
    lacking = [f for f in ("ass", "drawbox", "overlay", "scale", "fps", "tile") if f not in ffmpeg["filters"]]
    if lacking or not ffmpeg["libx264"]:
        return refuse(f"ffmpeg-incapable: {found['exe']} ({ffmpeg['version']}) lacks "
                      f"{', '.join(lacking + ([] if ffmpeg['libx264'] else ['libx264']))}; the reel needs libass "
                      f"captions and an H.264 encoder", out)
    settings_reel = ((pres.get("settings") or {}).get("reel") or {})
    max_seconds = a.max_seconds if a.max_seconds is not None else settings_reel.get("maxSeconds")
    chapter_filter = {c.strip() for c in a.chapters.split(",") if c.strip()} if a.chapters else None
    bundle_dir = resolve_bundle_dir(pres, pres_path, a.bundle)

    segments, omitted = plan_segments(pres, ffmpeg, bundle_dir, chapter_filter, max_seconds)
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

    status_doc = {"schemaVersion": 1, "status": "failed", "presentationId": pres.get("presentationId"),
                  "generatedUtc": utcnow(), "out": str(out), "bundleDir": str(bundle_dir),
                  "ffmpeg": {"path": ffmpeg["path"], "version": ffmpeg["version"], "how": ffmpeg["how"]},
                  "planned": {"seconds": planned_seconds, "frames": planned_frames, "segments": len(segments)},
                  "expected": None, "probe": None, "reused": 0, "rendered": 0, "missingCards": 0,
                  "omittedChapters": omitted, "segments": [], "outputs": {}, "errors": []}
    with Run("evalreel-render", stage_source="counted") as run:
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
                if checks:
                    failed = True
                    status_doc["errors"].append("reel.mp4 does not match the plan: " + "; ".join(checks))
        run.advance()

        # the map, the SRT and the contact sheet are written from what was produced
        segments_out = []
        for s in segments:
            segments_out.append({
                "index": s["index"], "chapterId": s["chapterId"], "chapterKind": s["chapterKind"], "kind": s["kind"],
                "findingIds": s["findingIds"], "mediaId": s["mediaId"],
                "source": ({"path": s["source"]["path"], "sha256": s["source"]["sha256"]} if s["source"] else None),
                "sourceInterval": s["sourceInterval"], "reelInterval": s.get("reelInterval"),
                "caption": s["caption"], "label": s["label"], "evidenceIds": s["evidenceIds"],
                "status": s["status"], "error": s["error"], "captionTruncated": s["captionTruncated"],
                "cacheKey": s.get("cacheKey"), "file": s.get("file")})
        map_doc = {"schemaVersion": 1, "presentationId": pres.get("presentationId"), "generatedUtc": utcnow(),
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


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="evalreel", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
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
    r.add_argument("--dry-run", action="store_true", help="print the segment plan and render nothing")
    r.set_defaults(fn=cmd_render)
    a = ap.parse_args(argv)
    return a.fn(a)


if __name__ == "__main__":
    raise SystemExit(main())
