"""The diagnostic reel against a hand-written presentation plan (Q45, D6).

    python _geometry-harness/tests/test_evalreel.py                       # from the checkout root
    HOUSECARL_EVALREEL_KEEP=DIR python _geometry-harness/tests/test_evalreel.py   # keep the outputs for a look

Dependency-free beyond what the reel itself needs — an ffmpeg (resolved the way evalreel resolves it) and PIL to
draw two test images; exits 1 on failure, and a missing ffmpeg or PIL is a failure that says so, never a skip.
Every number asserted is read from the produced files: reel-status.json, reel-map.json, reel.srt, and the reel
decoded by ffmpeg. The frames written to `boundaries/` under the keep directory are for a person to read.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
FIXTURE = HERE / "fixtures" / "evalreel" / "sample-presentation.json"
EVALREEL = HARNESS / "evalreel.py"
sys.path.insert(0, str(HARNESS))
import evalreel  # noqa: E402

FAILURES: list[str] = []
FPS = 30


def check(condition, label):
    print(("  PASS  " if condition else "  FAIL  ") + label)
    if not condition:
        FAILURES.append(label)


def expect(got, want, label):
    ok = got == want
    print(("  PASS  " if ok else "  FAIL  ") + f"{label}: expected {want!r}, got {got!r}")
    if not ok:
        FAILURES.append(label)


def near(got, want, tol, label):
    ok = got is not None and abs(got - want) <= tol
    print(("  PASS  " if ok else "  FAIL  ") + f"{label}: expected {want!r} ± {tol}, got {got!r}")
    if not ok:
        FAILURES.append(label)


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None


def render(pres: Path, out: Path, *extra) -> subprocess.CompletedProcess:
    env = dict(os.environ)
    env["PYTHONIOENCODING"] = "utf-8"
    return subprocess.run([sys.executable, str(EVALREEL), "render", "--presentation", str(pres), "--out", str(out)]
                          + [str(x) for x in extra], capture_output=True, text=True, env=env, timeout=600)


def make_png(path: Path, size: tuple[int, int], seed: int) -> None:
    """A picture with structure a person can recognise at a glance: coloured quadrants, a diagonal, a border."""
    from PIL import Image, ImageDraw
    w, h = size
    im = Image.new("RGB", size, (24 + seed * 40, 40, 90))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, w // 2, h // 2], fill=(200, 60 + seed * 30, 40))
    d.rectangle([w // 2, h // 2, w, h], fill=(40, 180, 90 + seed * 40))
    d.line([0, 0, w, h], fill=(255, 255, 255), width=8)
    d.rectangle([4, 4, w - 5, h - 5], outline=(255, 220, 0), width=6)
    d.text((w // 2 - 40, h // 2 - 10), f"{w}x{h} seed {seed}", fill=(255, 255, 255))
    im.save(path)


def make_clip(ffmpeg: str, path: Path) -> None:
    r = subprocess.run([ffmpeg, "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi",
                        "-i", "testsrc2=size=640x360:rate=25", "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                        str(path)], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError("testsrc2 clip: " + r.stderr[-400:])


def parse_srt(text: str) -> list[dict]:
    cues = []
    for block in [b for b in text.strip().split("\n\n") if b.strip()]:
        lines = block.splitlines()
        a, b = lines[1].split(" --> ")

        def t(s):
            hh, mm, rest = s.split(":")
            ss, ms = rest.split(",")
            return int(hh) * 3600 + int(mm) * 60 + int(ss) + int(ms) / 1000
        cues.append({"index": int(lines[0]), "start": t(a), "end": t(b), "text": "\n".join(lines[2:])})
    return cues


def write_presentation(template: dict, media_dir: Path, pres_path: Path, ids: dict, files: dict) -> dict:
    """The fixture with the generated files' ids, paths, hashes and sizes in place of the placeholders."""
    text = json.dumps(template)
    for placeholder, real in ids.items():
        text = text.replace(f'"{placeholder}"', f'"{real}"')
    doc = json.loads(text)
    for row in doc["media"]:
        f = files.get(row["id"])
        if f is None:
            continue
        row["path"] = f"media/{f.name}"
        row["sha256"] = sha(f) if f.exists() else None
        row["bytes"] = f.stat().st_size if f.exists() else None
        if row.get("sourceInterval"):
            row["sourceInterval"]["sourcePath"] = row["path"]
            row["sourceInterval"]["sourceSha256"] = row["sha256"]
    pres_path.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return doc


def decode_reel(ffmpeg: str, reel: Path) -> tuple[int | None, float | None]:
    """The test's OWN decode of the reel — frames counted by a full decode (`-vsync 0`, every frame passed
    through) and the last decoded timestamp — so a status file whose probe merely copies the plan is caught."""
    r = subprocess.run([ffmpeg, "-hide_banner", "-nostdin", "-v", "error", "-i", str(reel), "-map", "0:v:0",
                        "-vsync", "0", "-f", "null", "-nostats", "-progress", "pipe:1", "-"],
                       capture_output=True, text=True)
    frames, out_us = None, None
    for line in r.stdout.splitlines():
        k, _, v = line.partition("=")
        if k == "frame" and v.strip().isdigit():
            frames = int(v)
        elif k == "out_time_us" and v.strip().lstrip("-").isdigit():
            out_us = int(v)
    if r.returncode != 0 or frames is None:
        return None, None
    # out_time_us is the last frame's timestamp; the stream ends one frame later
    return frames, round(out_us / 1_000_000 + 1 / FPS, 3) if out_us is not None else None


def boundary_frames(ffmpeg: str, reel: Path, segments: list[dict], out_dir: Path) -> list[Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    made = []
    for s in segments:
        iv = s.get("reelInterval")
        if not iv:
            continue
        t = iv["startSeconds"] + 0.5 / FPS
        p = out_dir / f"boundary-{s['index']:02d}-{s['kind']}.png"
        r = subprocess.run([ffmpeg, "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", f"{t:.4f}",
                            "-i", str(reel), "-frames:v", "1", str(p)], capture_output=True, text=True)
        if r.returncode == 0 and p.exists():
            made.append(p)
    return made


def main() -> int:
    keep = os.environ.get("HOUSECARL_EVALREEL_KEEP")
    found = evalreel.ffmpeg_find()
    check(found is not None, "ffmpeg is resolvable (HOUSECARL_FFMPEG, DBH_FFMPEG, PATH or a bundled copy) — the reel is not optional here")
    if not found:
        return 1
    ffmpeg = found["exe"]
    try:
        import PIL  # noqa: F401
        check(True, "PIL is importable (draws the two test images)")
    except ImportError:
        check(False, "PIL is importable (draws the two test images)")
        return 1
    template = load(FIXTURE)
    check(isinstance(template, dict) and template.get("schemaVersion") == 1, "the sample presentation is schema 1")

    tmp = Path(tempfile.mkdtemp(prefix="hc-evalreel-test-"))
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
    try:
        media = tmp / "media"
        media.mkdir()
        wide, tall, clip = media / "wide.png", media / "tall.png", media / "clip.mp4"
        make_png(wide, (1920, 1080), 0)
        make_png(tall, (800, 1000), 1)
        make_clip(ffmpeg, clip)
        ids = {"M-WIDE": f"M-{sha(wide)[:12]}", "M-TALL": f"M-{sha(tall)[:12]}", "M-CLIP": f"M-{sha(clip)[:12]}",
               "M-UNASSESSED": "M-000000000000"}
        files = {ids["M-WIDE"]: wide, ids["M-TALL"]: tall, ids["M-CLIP"]: clip}
        pres = tmp / "presentation.json"
        write_presentation(template, media, pres, ids, files)
        out = tmp / "reel"

        # ---- a dry run plans and renders nothing
        r = render(pres, out, "--dry-run")
        expect(r.returncode, 0, "dry run exits 0")
        check("6 segment(s)" in r.stdout, "dry run plans 6 segments (opening, 2 images, 1 clip, 1 missing, closing)")
        check(not (out / "reel.mp4").exists(), "dry run wrote no reel")

        # ---- render 1: everything from scratch
        t0 = time.time()
        r = render(pres, out)
        print(f"  (render 1: {time.time() - t0:.1f}s)")
        expect(r.returncode, 0, "render 1 exits 0")
        if r.returncode != 0:
            print(r.stdout[-2000:]); print(r.stderr[-2000:])
        status = load(out / "reel-status.json") or {}
        expect(status.get("status"), "rendered", "render 1 status")
        planned = status.get("planned") or {}
        expect(planned.get("seconds"), 32.0, "planned seconds = 6 + 5 + 5 + 3 + 5 + 8")
        expect(planned.get("frames"), 960, "planned frames = 32 s x 30 fps")
        probe = status.get("probe") or {}
        near(probe.get("durationSeconds"), planned.get("seconds") or 0, 0.1, "probed duration = planned ± 0.1 s")
        expect(probe.get("frames"), 960, "decoded frame count")
        expect((probe.get("width"), probe.get("height"), probe.get("fps")), (1280, 720, 30.0), "canvas and fps")
        own_frames, own_seconds = decode_reel(ffmpeg, out / "reel.mp4")
        expect(own_frames, 960, "the test's own decode of reel.mp4 counts the planned frames")
        expect(own_frames, probe.get("frames"), "the test's own frame count equals status.probe.frames")
        near(own_seconds, probe.get("durationSeconds") or 0, 0.05,
             "the test's own decoded duration equals status.probe.durationSeconds")
        expect(status.get("reused"), 0, "render 1 reused nothing")
        expect(status.get("rendered"), 6, "render 1 rendered every segment")
        expect(status.get("missingCards"), 1, "one missing card (the unassessed clip's chapter)")
        expect(status.get("srtCues"), 6, "one SRT cue per segment")
        for name in ("reel.mp4", "reel.srt", "reel-map.json", "reel-status.json", "contact-sheet.png"):
            check((out / name).is_file(), f"{name} exists")

        m = load(out / "reel-map.json") or {}
        segs = m.get("segments") or []
        expect(len(segs), 6, "map segments")
        expect([s["kind"] for s in segs], ["card-opening", "image", "image", "clip", "missing", "card-closing"],
               "segment kinds in chapter order")
        expect([s.get("status") for s in segs], ["rendered"] * 4 + ["no-media", "rendered"],
               "every segment rendered; the planned missing card keeps its no-media status")
        iv = segs[3].get("sourceInterval") or {}
        expect((iv.get("startSeconds"), iv.get("endSeconds")), (1.0, 4.0), "the clip's source interval")
        expect(iv.get("clampedToSource"), False, "the interval lies inside the 6 s source")
        reel_iv = [(s["reelInterval"]["startSeconds"], s["reelInterval"]["endSeconds"]) for s in segs]
        expect(reel_iv, [(0.0, 6.0), (6.0, 11.0), (11.0, 16.0), (16.0, 19.0), (19.0, 24.0), (24.0, 32.0)],
               "reel intervals are contiguous and sized as planned")
        expect(segs[1]["source"]["sha256"], sha(wide), "the wide image's segment cites its hash")
        expect(segs[3]["source"]["path"], "media/clip.mp4", "the clip's segment cites its bundle-relative path")
        expect(segs[4]["source"], None, "the missing card has no source")
        expect(segs[4]["label"], "no qualified media", "the missing card is labelled")
        expect(segs[4]["mediaId"], "M-000000000000", "the missing card names the unassessed media id")
        check(segs[1]["label"] == "baseline · baseline · offline-render", f"the image label reads arm · state · kind: {segs[1]['label']!r}")
        check(all(s.get("captionTruncated") is False for s in segs), "no caption was truncated")

        cues = parse_srt((out / "reel.srt").read_text(encoding="utf-8"))
        expect(len(cues), 6, "SRT cue count")
        expect([c["index"] for c in cues], [1, 2, 3, 4, 5, 6], "SRT cues numbered in order")
        expect([(c["start"], c["end"]) for c in cues], reel_iv, "SRT cue times equal the map's reel intervals")
        check(cues[1]["text"].startswith("F-001 Navmesh ownership"), "the SRT carries the caption text")

        from PIL import Image
        with Image.open(out / "contact-sheet.png") as im:
            expect(im.size, (4 * 320 + 3 * 4 + 2 * 4, 3 * 180 + 2 * 4 + 2 * 4),
                   "contact sheet tiles 12 frames (2 per segment) four across")

        keys1 = [s.get("cacheKey") for s in segs]
        seg_files = [out / s["file"] for s in segs]
        mtimes1 = [p.stat().st_mtime_ns for p in seg_files]

        # ---- render 2: nothing changed, everything reused, no re-encode
        r = render(pres, out)
        expect(r.returncode, 0, "render 2 exits 0")
        status2 = load(out / "reel-status.json") or {}
        expect(status2.get("status"), "rendered", "render 2 status")
        expect(status2.get("reused"), 6, "render 2 reused every segment")
        expect(status2.get("rendered"), 0, "render 2 re-encoded nothing")
        expect([p.stat().st_mtime_ns for p in seg_files], mtimes1, "no segment file was rewritten")
        expect((status2.get("probe") or {}).get("frames"), 960, "render 2 decodes to the same frame count")

        # ---- a chapter subset comes from the same cache
        sub = tmp / "reel-subset"
        shutil.copytree(out / "cache", sub / "cache")
        r = render(pres, sub, "--chapters", "C-01,C-05")
        expect(r.returncode, 0, "subset render exits 0")
        s3 = load(sub / "reel-status.json") or {}
        expect((s3.get("planned") or {}).get("seconds"), 14.0, "subset plans the two cards")
        expect(s3.get("reused"), 2, "subset reuses the two card segments")

        # ---- render 3: the clip is deleted -> partial, a missing card, no invented segment
        clip_bytes = clip.read_bytes()
        clip.unlink()
        r = render(pres, out)
        expect(r.returncode, 1, "render 3 (clip deleted) exits 1")
        status3 = load(out / "reel-status.json") or {}
        expect(status3.get("status"), "partial", "render 3 status")
        m3 = load(out / "reel-map.json") or {}
        segs3 = m3.get("segments") or []
        expect(len(segs3), 6, "render 3 keeps six segments")
        expect(segs3[3]["kind"], "missing", "the clip's segment is a missing card")
        expect(segs3[3]["status"], "missing-media", "its status names the missing media")
        expect(segs3[3]["source"], None, "it cites no source")
        expect(segs3[3]["label"], "no qualified media", "it is labelled")
        check("not on disk" in (segs3[3].get("error") or ""), f"its error says the file is not on disk: {segs3[3].get('error')!r}")
        check(not any((s.get("source") or {}).get("path") == "media/clip.mp4" for s in segs3),
              "no segment claims the deleted clip")
        expect(status3.get("reused"), 5, "render 3 reused the five unchanged segments")
        expect((status3.get("planned") or {}).get("seconds"), 34.0, "render 3 plans 34 s (a 5 s card for the 3 s clip)")
        near((status3.get("probe") or {}).get("durationSeconds"), 34.0, 0.1, "render 3 probed duration")
        expect((status3.get("probe") or {}).get("frames"), 1020, "render 3 decoded frames")
        own3, own3_s = decode_reel(ffmpeg, out / "reel.mp4")
        expect(own3, 1020, "the test's own decode of the partial reel counts 1020 frames")
        expect(own3, (status3.get("probe") or {}).get("frames"), "render 3: own frame count equals status.probe.frames")
        near(own3_s, (status3.get("probe") or {}).get("durationSeconds") or 0, 0.05,
             "render 3: own decoded duration equals status.probe.durationSeconds")
        clip.write_bytes(clip_bytes)

        # ---- render 4: the wide image changes and the plan is recompiled -> only its segment re-renders
        make_png(wide, (1920, 1080), 3)
        files[ids["M-WIDE"]] = wide
        write_presentation(template, media, pres, ids, files)
        r = render(pres, out)
        expect(r.returncode, 0, "render 4 (altered image, plan updated) exits 0")
        status4 = load(out / "reel-status.json") or {}
        expect(status4.get("status"), "rendered", "render 4 status")
        expect(status4.get("reused"), 5, "render 4 reused five segments")
        expect(status4.get("rendered"), 1, "render 4 re-rendered exactly one segment")
        m4 = load(out / "reel-map.json") or {}
        keys4 = [s.get("cacheKey") for s in (m4.get("segments") or [])]
        check(keys4[1] != keys1[1], "the altered image's cache key changed")
        expect([k for i, k in enumerate(keys4) if i != 1], [k for i, k in enumerate(keys1) if i != 1],
               "the other five cache keys are unchanged")
        expect((status4.get("probe") or {}).get("frames"), 960, "render 4 decoded frames")

        # ---- render 5: the image changes but the plan does not -> the hash mismatch is a missing card
        make_png(wide, (1920, 1080), 5)
        r = render(pres, out)
        expect(r.returncode, 1, "render 5 (altered image, plan stale) exits 1")
        status5 = load(out / "reel-status.json") or {}
        expect(status5.get("status"), "partial", "render 5 status")
        m5 = load(out / "reel-map.json") or {}
        s5 = (m5.get("segments") or [{}] * 6)[1]
        expect(s5.get("status"), "missing-media", "the stale image's segment is missing-media")
        check("hashes to" in (s5.get("error") or ""), f"its error names the hash mismatch: {s5.get('error')!r}")
        make_png(wide, (1920, 1080), 3)

        # ---- render 6: back to the recompiled plan; then frames at every boundary for a person to read
        r = render(pres, out)
        expect(r.returncode, 0, "render 6 exits 0")
        status6 = load(out / "reel-status.json") or {}
        expect(status6.get("reused"), 6, "render 6 reused every segment (the cache survived the stale render)")
        m6 = load(out / "reel-map.json") or {}
        frames = boundary_frames(ffmpeg, out / "reel.mp4", m6.get("segments") or [], tmp / "boundaries")
        expect(len(frames), 6, "one decoded frame at each segment boundary")

        if keep:
            dst = Path(keep)
            dst.mkdir(parents=True, exist_ok=True)
            for name in ("reel.mp4", "reel.srt", "reel-map.json", "reel-status.json", "contact-sheet.png"):
                if (out / name).exists():
                    shutil.copy2(out / name, dst / name)
            if (tmp / "boundaries").exists():
                shutil.copytree(tmp / "boundaries", dst / "boundaries", dirs_exist_ok=True)
            print(f"  kept outputs under {dst}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
