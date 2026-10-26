"""The review reel's stills: the opening infographic, the load order, data stills and the closing checks, drawn with
Pillow from the reel plan's page data (task 2026-10-19-review-reel-vignettes).

    draw_page(page: dict, font: Path, bold: Path, size=(1280, 720)) -> PIL.Image

The font files are named, never defaulted: `font_files()` resolves them from the flag, HOUSECARL_REEL_FONT /
HOUSECARL_REEL_FONT_BOLD, then the skill data (`report-priorities.json` reel.stillFont / stillFontBold), and refuses in
one sentence naming the missing file. Pillow's built-in font is never used, so another machine cannot silently draw
different pages. These pages are the only frames of a reel that carry text; evidence frames carry none.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

BG = (15, 18, 24)
FG = (232, 234, 238)
DIM = (150, 158, 170)
ACCENT = (96, 200, 230)
MARK = (230, 110, 210)


class FontMissing(ValueError):
    pass


def font_files(explicit: str | None, settings_reel: dict) -> tuple[Path, Path]:
    regular = explicit or os.environ.get("HOUSECARL_REEL_FONT") or settings_reel.get("stillFont")
    bold = os.environ.get("HOUSECARL_REEL_FONT_BOLD") or settings_reel.get("stillFontBold") or regular
    if explicit or os.environ.get("HOUSECARL_REEL_FONT"):
        bold = os.environ.get("HOUSECARL_REEL_FONT_BOLD") or regular
    for p in (regular, bold):
        if not p or not Path(p).is_file():
            raise FontMissing(f"the still font file {p!r} is absent; name an installed TrueType font with --font, "
                              f"HOUSECARL_REEL_FONT or the skill's reel.stillFont (no default font is used)")
    return Path(regular), Path(bold)


def page_hash(page: dict) -> str:
    return hashlib.sha256(json.dumps(page, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()


def _wrap(draw, text, font, width):
    words, lines, cur = str(text).split(), [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if draw.textlength(t, font=font) <= width or not cur:
            cur = t
        else:
            lines.append(cur)
            cur = w
    if cur:
        lines.append(cur)
    return lines


def _blocks(page: dict) -> list[tuple[str, str, float]]:
    """(style, text, size factor) lines of a page before wrapping. style: title, head, body, dim, mark."""
    out = [("title", page.get("title") or "", 1.4)]
    lay = page.get("layout")
    if lay == "types":
        for r in page.get("rows") or []:
            out.append(("head", f"{r.get('count')}   {r.get('label')}", 1.15))
            if r.get("pairs"):
                out.append(("dim", "between " + "; ".join(r["pairs"]), 0.8))
        if (page.get("leftOut") or {}).get("line"):
            out.append(("dim", page["leftOut"]["line"], 0.8))
    elif lay == "load-order":
        targets = set(page.get("targets") or [])
        for i, p in enumerate(page.get("plugins") or []):
            out.append(("mark" if p in targets else "body", f"{i + 1:>3}  {p}", 0.75))
        if page.get("runtimeMods"):
            out.append(("head", page.get("runtimeTitle") or "", 0.9))
            out += [("body", m, 0.75) for m in page["runtimeMods"]]
    elif lay == "rows":
        for r in page.get("rows") or []:
            where = f" in {r['place']}" if r.get("place") else ""
            out.append(("head", f"{r.get('object')}{where}", 1.0))
            bits = [" and ".join(r.get("pair") or [])]
            if r.get("field"):
                bits.append(str(r["field"]))
            if r.get("winner"):
                bits.append(f"{r['winner']} wins")
            out.append(("body", " | ".join(bits), 0.85))
            for k, v in (r.get("values") or {}).items():
                out.append(("dim", f"{k}: {v}", 0.8))
            if r.get("reason"):
                out.append(("dim", r["reason"], 0.7))
    elif lay == "checks":
        for r in page.get("rows") or []:
            out.append(("head", str(r.get("reason") or "")[:1].upper() + str(r.get("reason") or "")[1:], 0.95))
            out += [("body", s, 0.8) for s in r.get("subjects") or []]
    return out


def draw_page(page: dict, font: Path, bold: Path, size=(1280, 720)) -> Image.Image:
    w, h = size
    margin = 64
    blocks = _blocks(page)
    columns = 2 if page.get("layout") == "load-order" and len(page.get("plugins") or []) > 18 else 1
    base = 34
    while base >= 10:
        img = Image.new("RGB", size, BG)
        d = ImageDraw.Draw(img)
        col_w = (w - 2 * margin - (columns - 1) * 32) / columns
        x0, y, col = margin, margin, 0
        fits = True
        for i, (style, text, k) in enumerate(blocks):
            f = ImageFont.truetype(str(bold if style in ("title", "head", "mark") else font), max(8, round(base * k)))
            width = (w - 2 * margin) if style == "title" else col_w
            lines = _wrap(d, text, f, width) or [""]
            lh = round(base * k * 1.3)
            if i and style == "title":
                y += lh // 2
            for line in lines:
                if y + lh > h - margin:
                    if columns > 1 and col + 1 < columns and style != "title":
                        col += 1
                        x0 = margin + col * (col_w + 32)
                        y = margin + round(base * 1.4 * 1.3) + 12
                    else:
                        fits = False
                        break
                fill = {"title": ACCENT, "dim": DIM, "mark": MARK}.get(style, FG)
                d.text((x0 if style != "title" else margin, y), line, font=f, fill=fill)
                y += lh
            if not fits:
                break
            if style == "title":
                y += 12
        if fits:
            return img
        base -= 2
    raise ValueError(f"the still page ({page.get('layout')}) does not fit 1280x720 even at the smallest text size")
