"""The review reel's stills: the opening infographic, the load order, the statement of clashes it cannot picture, the
closing checks and a split's label strip, drawn with
Pillow from the reel plan's page data (task 2026-10-19-review-reel-vignettes).

    draw_page(page: dict, font: Path, bold: Path, size=(1280, 720)) -> PIL.Image

The font files are named, never defaulted: `font_files()` resolves them from the flag, HOUSECARL_REEL_FONT /
HOUSECARL_REEL_FONT_BOLD, then the skill data (`report-priorities.json` reel.stillFont / stillFontBold), and refuses in
one sentence naming the missing file. Pillow's built-in font is never used, so another machine cannot silently draw
different pages. These pages are the only frames of a reel that carry text; evidence frames carry none.
"""
from __future__ import annotations

import hashlib
import re
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
            out.append(("head", f"{r.get('label')}: {r.get('count')}" + (f" {r['unit']}" if r.get("unit") else ""), 1.15))
            if r.get("pairs"):
                out.append(("dim", "between " + "; ".join(r["pairs"]), 0.8))
            if r.get("moreLine"):
                out.append(("dim", r["moreLine"], 0.8))
        for key in ("line", "markersLine"):
            if (page.get("leftOut") or {}).get(key):
                out.append(("dim", page["leftOut"][key], 0.8))
        if page.get("incomplete"):
            out.append(("mark", page["incomplete"], 0.85))
    elif lay == "load-order":
        # the mod being added and every plugin of a counted clash in the highlight colour, under the same-colour legend
        marked = set(page.get("affected") or page.get("targets") or [])
        if page.get("legend"):
            out.append(("mark", page["legend"], 0.9))
        for i, p in enumerate(page.get("plugins") or []):
            out.append(("mark" if p in marked else "body", f"{i + 1:>3}  {p}", 0.75))
        if page.get("runtimeMods"):
            out.append(("head", page.get("runtimeTitle") or "", 0.9))
            out += [("body", m, 0.75) for m in page["runtimeMods"]]
    elif lay == "statement":
        out = [("head", page.get("text") or "", 1.3)]           # one sentence, no title: what the narration says
    elif lay == "list":
        out += [("body", str(r), 0.9) for r in page.get("rows") or []]
    elif lay == "checks":
        for r in page.get("rows") or []:
            out.append(("head", str(r.get("reason") or "")[:1].upper() + str(r.get("reason") or "")[1:], 0.95))
            out += [("body", s, 0.8) for s in r.get("subjects") or []]
    return out


def draw_strip(labels: dict, font: Path, bold: Path, size=(1280, 40), split_at: int = 640) -> Image.Image:
    """The label strip under a split: each half's label centred under it, from the plan's typed slots."""
    w, h = size
    img = Image.new("RGB", size, BG)
    d = ImageDraw.Draw(img)
    f = ImageFont.truetype(str(bold), 22)
    for text, (x0, x1) in ((labels.get("left") or "", (0, split_at)), (labels.get("right") or "", (split_at, w))):
        tw = d.textlength(text, font=f)
        d.text((x0 + (x1 - x0 - tw) / 2, (h - 26) / 2), text, font=f, fill=FG)
    return img


#: the smallest body size (px at 720p) a still page is drawn at; a page that would need smaller is split into pages
MIN_READABLE = 22


def fits(page: dict, font: Path, bold: Path, size=(1280, 720), floor: int = MIN_READABLE) -> bool:
    try:
        draw_page(page, font, bold, size, floor=floor)
        return True
    except ValueError:
        return False


def paginate(page: dict, font: Path, bold: Path, size=(1280, 720)) -> list[dict]:
    """A still page split into pages that each fit at a readable size: a statement by its sentences, a list or checks
    page by its rows (a checks row too long alone by its subjects). One page when it fits; a page that cannot be split
    further is returned as it is and draws at its smallest size."""
    limit = page.get("maxWords")
    n_words = len(" ".join(str(x) for x in [page.get("title"), page.get("text"), *[
        (" ".join([str(r.get("reason") or ""), *map(str, r.get("subjects") or [])]) if isinstance(r, dict) else str(r))
        for r in page.get("rows") or []]] if x).split())
    if fits(page, font, bold, size) and not (limit and n_words > int(limit)):
        return [page]
    lay = page.get("layout")
    if lay == "statement":
        parts = re.split(r"(?<=[.;])\s+", str(page.get("text") or ""))
        if len(parts) < 2:
            return [page]
        k = len(parts) // 2
        return (paginate({**page, "text": " ".join(parts[:k])}, font, bold, size)
                + paginate({**page, "text": " ".join(parts[k:])}, font, bold, size))
    rows = list(page.get("rows") or [])
    if lay in ("list", "checks") and len(rows) > 1:
        k = len(rows) // 2
        return (paginate({**page, "rows": rows[:k]}, font, bold, size)
                + paginate({**page, "rows": rows[k:]}, font, bold, size))
    if lay == "checks" and rows and len(rows[0].get("subjects") or []) > 1:
        subs = rows[0]["subjects"]
        k = len(subs) // 2
        return (paginate({**page, "rows": [{**rows[0], "subjects": subs[:k]}]}, font, bold, size)
                + paginate({**page, "rows": [{**rows[0], "subjects": subs[k:]}]}, font, bold, size))
    return [page]


def label_frame(src: Path, label: str, font: Path, bold: Path, size=(1280, 720), strip_h: int = 40) -> Image.Image:
    """A vignette frame fitted above a label strip: which setup it shows, or which marked shape is which colour. The
    evidence frame itself carries no text; the strip is the reel's."""
    w, h = size
    img = Image.new("RGB", size, BG)
    with Image.open(src) as fr:
        fr = fr.convert("RGB")
        scale = min(w / fr.width, (h - strip_h) / fr.height)
        fr = fr.resize((max(1, round(fr.width * scale)), max(1, round(fr.height * scale))))
        img.paste(fr, ((w - fr.width) // 2, (h - strip_h - fr.height) // 2))
    d = ImageDraw.Draw(img)
    sz = 22
    f = ImageFont.truetype(str(bold), sz)
    while d.textlength(label, font=f) > w - 32 and sz > 12:
        sz -= 1
        f = ImageFont.truetype(str(bold), sz)
    d.text(((w - d.textlength(label, font=f)) / 2, h - strip_h + (strip_h - sz) / 2 - 2), label, font=f, fill=FG)
    return img


def draw_page(page: dict, font: Path, bold: Path, size=(1280, 720), floor: int = 10) -> Image.Image:
    w, h = size
    margin = 64
    blocks = _blocks(page)
    if page.get("layout") == "statement":
        img = Image.new("RGB", size, BG)
        d = ImageDraw.Draw(img)
        sz = 40
        while True:
            f = ImageFont.truetype(str(bold), sz)
            lines = _wrap(d, page.get("text") or "", f, w - 2 * margin)
            if len(lines) * round(sz * 1.3) <= h - 2 * margin:
                break
            if sz <= max(floor, 10):
                raise ValueError("the statement page does not fit 1280x720 even at the smallest text size")
            sz -= 2
        lh = round(sz * 1.3)
        y = (h - len(lines) * lh) / 2
        for line in lines:
            d.text(((w - d.textlength(line, font=f)) / 2, y), line, font=f, fill=FG)
            y += lh
        return img
    columns = 2 if page.get("layout") == "load-order" and len(page.get("plugins") or []) > 18 else 1
    base = 34
    while base >= max(floor, 10):
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
