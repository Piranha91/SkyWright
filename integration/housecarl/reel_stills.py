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
    """(style, text, size factor) lines of a page before wrapping. style: title, head, body, dim, mark. Every line is
    the text the page draws, so it passes through shown_text (R11: no form key or editor ID on screen), except the load
    order's plugin files, which a modder must recognise by their file names."""
    return [(s, t if keep else shown_text(t), k) for s, t, k, keep in _raw_blocks(page)]


def _raw_blocks(page: dict) -> list[tuple[str, str, float, bool]]:
    out = [("title", page.get("title") or "", 1.4)]
    lay = page.get("layout")
    keep = set()                                        # indices of lines drawn as given (plugin files)
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
            keep.add(len(out))
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
    return [(s, t, k, i in keep) for i, (s, t, k) in enumerate(out)]


def draw_strip(labels: dict, font: Path, bold: Path, size=(1280, 40), split_at: int = 640) -> Image.Image:
    """The label strip under a split: each half's label centred under it, from the plan's typed slots."""
    w, h = size
    img = Image.new("RGB", size, BG)
    d = ImageDraw.Draw(img)
    f = ImageFont.truetype(str(bold), 22)
    for text, (x0, x1) in ((shown_text(labels.get("left") or ""), (0, split_at)),
                           (shown_text(labels.get("right") or ""), (split_at, w))):
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


def _sentence(x) -> str:
    x = " ".join(str(x or "").split())
    return x if not x or x[-1] in ".:;!?" else x + "."


# what the reel may show but the narrator never says (C1931, C1961), checked on the final string handed to speech and
# subtitles, after every other substitution: a form key (six hex digits and a colon, whatever name follows: a plugin, or
# the mod name the reel put in its place) and an editor ID (a word joined by underscores, or a run of capitalised words
# and digits written as one word). A plugin file name is not an editor ID.
SPOKEN_PLACED = re.compile(r"\b[0-9A-Fa-f]{6}:[^,;()]*?\s*\((a placed [^)]*)\)")
SPOKEN_FORM_KEY = re.compile(r"(,\s*)?\b[0-9A-Fa-f]{6}:[^,;()]*?(?=\s*[,;()]|\.\s|\.?$)")
SPOKEN_EDITOR_ID = re.compile(r"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")
SPOKEN_PASCAL_ID = re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")
SPOKEN_BRACKET_ID = re.compile(r"\s*\((?=[A-Za-z0-9]*[a-z][A-Z])[A-Za-z][A-Za-z0-9]{3,}\)")
# the shapes the final speech string may not hold (the test's own check; spoken_text removes every one)
FORM_KEY_SHAPE = re.compile(r"\b[0-9A-Fa-f]{6}:")
# the reel's declared mod names and plugin file names without their extension: said as written, never put into words by
# humanize, which strips a prefix or a capital word ("DBM_MAS_Patch" said as "Patch"; C2166)
_NAMES: frozenset = frozenset()


def keep_names(mod_names: dict, short_forms: dict | None = None) -> None:
    """Set the names spoken_text leaves as written: each mod's display name and each plugin's file name without its
    extension, from the reel plan's modNames (plugin -> name, or -> {name})."""
    global _NAMES
    kept = set()
    for plugin, v in (mod_names or {}).items():
        name = v.get("name") if isinstance(v, dict) else v
        if name:
            kept.add(str(name))
        kept.add(re.sub(r"\.(?:esp|esm|esl)$", "", str(plugin), flags=re.I))
    kept |= {str(v) for v in (short_forms or {}).values()}          # a mod's short form (item 5), said as written
    _NAMES = frozenset(kept)


def names_digest() -> str:
    """A short digest of the kept names, so a still drawn under other names is never reused from the stills folder."""
    return hashlib.sha256("\n".join(sorted(_NAMES)).encode("utf-8")).hexdigest()[:8]


def spoken_text(text: str) -> str:
    """The words the narrator says and the subtitles show: a placed reference's form key before "(a placed X, in Y)" is
    said as that phrase; any other form key is dropped where it follows a name (", 0138CE:Skyrim.esm") and said as "a
    record" where it stands alone; an editor ID in brackets after a name is dropped; any other editor ID is said in words
    (reel_plan.humanize), the same words said twice in a row once with a count. A declared mod name or plugin file name
    (keep_names) is said as written. Idempotent; the page shows the original."""
    from reel_plan import humanize

    def words(m):
        return m.group(0) if m.group(0) in _NAMES else humanize(m.group(0)) or "a record"
    text = SPOKEN_PLACED.sub(lambda m: m.group(1), str(text))
    text = SPOKEN_FORM_KEY.sub(lambda m: "" if m.group(1) else "a record", text)
    text = SPOKEN_BRACKET_ID.sub(lambda m: m.group(0) if m.group(0).strip()[1:-1] in _NAMES else "", text)
    text = SPOKEN_EDITOR_ID.sub(words, text)
    text = SPOKEN_PASCAL_ID.sub(words, text)
    # a name said twice is said once with a count only when both are sure to be the same whole name: the second ends at
    # punctuation or the end (not ".esm", "'s", a number or another word), the first follows no capitalised word that
    # may be part of it; when unsure both names stay as written (C2610)
    text = re.sub(r"\b([A-Z][A-Za-z]*(?: [A-Za-z]+)*) and \1(?=[,;:!?)]|\.(?!\w)|$)",
                  lambda m: m.group(0) if re.search(r"[A-Z][\w'’]* $", m.string[:m.start()]) else m.group(1) + " (two of them)",
                  text)
    return " ".join(text.split())


def shown_text(text: str) -> str:
    """The words a still page draws (R11): the same form-key and editor-ID rules as the narration (spoken_text), so the
    screen never shows what the narrator would not say. A plugin file name is kept (spoken_text's rule)."""
    return spoken_text(text) if str(text or "").strip() else str(text or "")


def speech_shapes(text: str) -> list:
    """The form-key and editor-ID shapes a string still holds (empty for a clean final speech string); a declared name
    (keep_names) is not one."""
    t = str(text)
    return (FORM_KEY_SHAPE.findall(t) + [x for x in SPOKEN_EDITOR_ID.findall(t) + SPOKEN_PASCAL_ID.findall(t)
                                         if x not in _NAMES])


def page_say(page: dict) -> str:
    """The narration a still page carries, from its own text: a fixed `say` (the load order, spoken as a summary), else
    its lead sentence and then what the page shows, row by row, in the page's order; never a form key or editor ID
    (spoken_text)."""
    return spoken_text(_page_say(page))


def _page_say(page: dict) -> str:
    if page.get("say"):
        return " ".join(str(page["say"]).split())
    lay = page.get("layout")
    bits = [page.get("sayLead")] if page.get("sayLead") else []
    if lay == "statement":
        bits.append(page.get("text"))
    elif lay == "list":
        bits += [str(r) for r in page.get("rows") or []]
    elif lay == "checks":
        for r in page.get("rows") or []:
            reason = str(r.get("reason") or "")
            reason = reason[:1].upper() + reason[1:]
            subs = [str(s) for s in r.get("subjects") or []]
            bits.append(f"{reason.rstrip('.:')}: {'; '.join(subs)}" if subs else reason)
    elif lay == "load-order":
        plugins = list(page.get("plugins") or [])
        marked = [p for p in plugins if p in set(page.get("affected") or page.get("targets") or [])]
        bits.append(f"{page.get('title') or 'Load order'}: {len(plugins)} plugins")
        if marked:
            bits.append(f"{page.get('legend') or 'Highlighted'}: " + ", ".join(
                re.sub(r"\.(esp|esm|esl)$", "", str(m), flags=re.IGNORECASE) for m in marked))
    elif lay == "types":
        for r in page.get("rows") or []:
            bits.append(f"{r.get('label')}: {r.get('count')}" + (f" {r['unit']}" if r.get("unit") else "")
                        + (f", between {'; '.join(r['pairs'])}" if r.get("pairs") else "")
                        + (f"; {r['moreLine']}" if r.get("moreLine") else ""))
        if page.get("incomplete") and page.get("incomplete") not in str(page.get("sayLead") or ""):
            bits.append(page["incomplete"])
        for key in ("line", "markersLine"):
            if (page.get("leftOut") or {}).get(key):
                bits.append(page["leftOut"][key])
    return " ".join(_sentence(b) for b in bits if b)


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
                + paginate({**page, "text": " ".join(parts[k:]), "sayLead": None}, font, bold, size))
    rows = list(page.get("rows") or [])
    # each part says only what it shows: the lead sentence goes with the first part, never repeated
    if lay in ("list", "checks") and len(rows) > 1:
        k = len(rows) // 2
        return (paginate({**page, "rows": rows[:k]}, font, bold, size)
                + paginate({**page, "rows": rows[k:], "sayLead": None}, font, bold, size))
    if lay == "checks" and rows and len(rows[0].get("subjects") or []) > 1:
        subs = rows[0]["subjects"]
        k = len(subs) // 2
        return (paginate({**page, "rows": [{**rows[0], "subjects": subs[:k]}]}, font, bold, size)
                + paginate({**page, "rows": [{**rows[0], "subjects": subs[k:]}], "sayLead": None}, font, bold, size))
    return [page]


def still_parts(pages: list[dict], font: Path, bold: Path) -> list[dict]:
    """A still scene's pages as drawn, each with its own narration (`say`): the storyboard and the render cut the same
    parts, so every page is spoken in page order and held for its own words."""
    out = []
    for page in pages:
        parts = paginate(page, font, bold)
        if len(parts) > 1 and page.get("seconds"):
            wsum = sum(max(1, page_words_drawn(x)) for x in parts)
            parts = [{**x, "seconds": round(float(page["seconds"]) * max(1, page_words_drawn(x)) / wsum, 2)} for x in parts]
        out += [{**x, "say": page_say(x)} for x in parts]
    return out


def page_words_drawn(page: dict) -> int:
    return len(" ".join(str(b[1]) for b in _blocks(page)).split())


def label_frame(src: Path, label: str, font: Path, bold: Path, size=(1280, 720), strip_h: int = 40) -> Image.Image:
    """A vignette frame fitted above a label strip: which setup it shows, or which marked shape is which colour. The
    evidence frame itself carries no text; the strip is the reel's (drawn through shown_text, R11)."""
    label = shown_text(label)
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
            lines = _wrap(d, blocks[0][1], f, w - 2 * margin)            # the statement as drawn (shown_text)
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
