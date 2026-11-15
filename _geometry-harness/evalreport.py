#!/usr/bin/env python
"""evalreport - compile a verified schema-2 evaluation bundle into one presentation plan and, from that
plan, a concise illustrated Markdown report, an HTML rendition, a paginated PDF with a verifiable page
limit, a complete evidence index and a presentation manifest.

No model call, no paid service, no network. Every sentence the report prints is a template sentence from
the evaluation-reporting skill's `report-priorities.json`, bound to numbers and names the bundle itself
carries: the disposition's findings, missing list, order evaluation and unassessed block; the receipts'
coverage lines; the emits' conflict rows; the registry copy's titles, claims and limits. Where the bundle
lacks something the report says "not assessed" or "not supplied" - never a guess.

Subcommands (contract: standards/HOUSECARL_EVALUATION_PRESENTATION.md):

  plan            --bundle DIR --out OUT [--mode concise|exhaustive] [--max-pages N]
  triage          --manifest ANNOTATED.json --out OUT [--policy POLICY.json]
  report          --presentation OUT/presentation.json --out OUT [--no-print]
  verify          --out OUT
  narrate-packet  --presentation P --out FILE
  narrate-apply   --presentation P --answers FILE [--out P2]

Exit codes: 0 ok; 2 a refused input (named on stderr, nothing written past the refusal record); 3 a
refused result (verify found a defect). A missing browser is `print.status: "not-rendered"` with the
reason - the Markdown and HTML are still written, and no page count is ever invented.

Q31: every subcommand opens a `scripts/runlog.py` Run (`evalreport-plan`, `evalreport-report`,
`evalreport-verify`, `evalreport-narrate`) with findings / groups / media / figures / pages as features.
"""
from __future__ import annotations

import argparse
import collections
import copy
import datetime as dt
import hashlib
import html
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402
sys.path.insert(0, str(HERE))
import evalsettings  # noqa: E402
import recordrows  # noqa: E402
import refscan  # noqa: E402
import pluginread  # noqa: E402

try:
    import jinja2
except ImportError:  # pragma: no cover - stated, never worked around
    jinja2 = None

TEMPLATES = HERE / "evalreport_templates"
PRIORITIES = HERE.parent / ".claude" / "skills" / "evaluation-reporting" / "references" / "report-priorities.json"
NARRATION = PRIORITIES.parent / "narration.json"
EVALRUN = HERE / "evalrun.py"

PRESENTATION_SCHEMA = 1
BUNDLE_SCHEMA = 2
COMPILER_VERSION = "2"
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3

ARTIFACTS = ("report.md", "report.html", "report.pdf", "evidence-index.md", "evidence-index.html")
TRIAGE_SCHEMA = "overlap-triage-v1"
TRIAGE_POLICY = HERE.parent / ".claude" / "skills" / "world-edit-compatibility" / "references" / "overlap-triage-policy.json"


# ------------------------------------------------------------------------------------------ helpers --


def view_kind_caption(provenance: dict, words: dict, first: bool = True) -> str:
    """The figure caption's addition for a picture that is not what the player sees ("" for one that is), from the
    skill's figureCaptions words: a cutaway names how many objects it hides, a sliced top-down its cut."""
    kind = (provenance or {}).get("viewKind") or "eye-level"
    if kind == "cutaway":
        n = len((provenance or {}).get("hidden") or [])
        return fill(str(words.get("cutaway") or ""), count=n, plural="" if n == 1 else "s", them="it" if n == 1 else "them")
    if kind == "top-down":
        cut = (provenance or {}).get("cullAbove")
        n = len((provenance or {}).get("hidden") or [])
        return (fill(str(words.get("topDown") or ""), cut="?" if cut is None else f"{float(cut):.0f}")
                + (fill(str(words.get("topDownHidden") or ""), count=n, plural="" if n == 1 else "s",
                                                               them="it" if n == 1 else "them") if n else ""))
    # the alpha-card rule's station view: the picker's own witness camera, not a composed shot (round 2 rerun, C1894)
    # said once per figure, on the frame it shows first, as the render line is (C1899)
    if ((provenance or {}).get("camera") or {}).get("cameraSource") == "witness-station":
        return str(words.get("stationView") or "") if first else ""
    return ""


def highlight_caption(marks: dict, fig_object, fig_mod, participants=None) -> tuple[str, dict]:
    """(the figureCaptions key, its slots) for a marked view: both participants marked (`highlight`), or one alone
    (`highlightOne`: a world-state vignette marks only its focus), never a missing one named "None" (C1899). With the
    pair's participants, a one-mark view names the object left plain too (`highlightOneAgainst`, R13)."""
    cy_, mg_ = marks.get("cyan"), marks.get("magenta")
    if cy_ and mg_:
        return "highlight", {"a": fig_object(cy_), "modA": fig_mod(str(cy_).split(":", 1)[-1]),
                             "b": fig_object(mg_), "modB": fig_mod(str(mg_).split(":", 1)[-1])}
    one_, col_ = (cy_, "cyan") if cy_ else (mg_, "magenta")
    slots = {"one": fig_object(one_), "modOne": fig_mod(str(one_).split(":", 1)[-1]), "colour": col_}
    plain_ = [p for p in participants or [] if p and str(p).casefold() != str(one_).casefold()]
    if plain_:
        return "highlightOneAgainst", {**slots, "other": fig_object(plain_[0]),
                                       "modOther": fig_mod(str(plain_[0]).split(":", 1)[-1])}
    return "highlightOne", slots

def patch_search_leads(search: dict, words: dict, loaded: set, supplied: list, masters: dict) -> list[str]:
    """What to do's Nexus lines (R3, N1): one line per Nexus page the search found, said with its most usable version
    (reel_plan.usable_first over every hit of that page, so a live file before a retired or old one, and a retired one's
    caution only when the page has nothing live) and the pairs it covers named once; one line per pair the search found
    nothing for. Never one line per listed file version, nor per pair that shares a page. Raises reel_plan.Refused for
    a hit the reel cannot say."""
    import reel_plan
    lead = words["reportLead"]
    out, pages = [], {}

    def top_(p_):
        pm_ = set(masters.get(p_.lower(), set()))
        return {m_ for m_ in pm_ if not any(m_ in (masters.get(o_) or ()) for o_ in pm_ if o_ != m_)}
    for entry in search.get("pairs") or []:
        pair = [str(x) for x in entry.get("pair") or []]
        if len(pair) != 2:
            continue
        hits = [reel_plan.flat_hit(h) for h in entry.get("hits") or []]
        if not hits:
            # a supplied patch for this pair that the search did not find: said as the search's miss, never as no patch
            own = next((p_ for p_ in supplied if {x.lower() for x in pair} <= top_(p_)), None)
            out.append(fill(lead["notFoundSupplied"], a=pair[0], b=pair[1], patch=own)
                       if own and lead.get("notFoundSupplied") else fill(lead["notFound"], a=pair[0], b=pair[1]))
            continue
        evaluated = any({x.lower() for x in pair} <= set(masters.get(p_.lower(), set())) for p_ in supplied)
        for h in hits:
            key = str(h.get("pageId") or h.get("modId") or h["file"]).casefold()
            if key not in pages:
                pages[key] = {"hits": [], "pairs": [], "evaluated": False}
                out.append(pages[key])                          # the page's line, in the search's order
            pg = pages[key]
            pg["hits"].append(h)
            if pair not in pg["pairs"]:
                pg["pairs"].append(pair)
            pg["evaluated"] |= evaluated
    lines = []
    for x in out:
        if isinstance(x, str):
            lines.append(x)
            continue
        best = reel_plan.usable_first(x["hits"], loaded)[0]
        file_ = reel_plan.said_file(best, words)
        pairs = x["pairs"]
        common = [p for p in pairs[0] if all(p.lower() in {q.lower() for q in pr} for pr in pairs)]
        if len(pairs) == 1:
            text = fill(lead["listed"], a=pairs[0][0], b=pairs[0][1], file=file_)
        elif common and lead.get("listedPage"):
            others = [q for pr in pairs for q in pr if q.lower() != common[0].lower()]
            text = fill(lead["listedPage"], b=common[0], a=reel_plan.joined(list(dict.fromkeys(others))), file=file_)
        else:
            text = fill(lead["listedPagePairs"], pairs="; ".join(f"{pr[0]} and {pr[1]}" for pr in pairs), file=file_)
        lines.append(text + reel_plan.lead_qualifiers(best, words, loaded, x["evaluated"]))
    return lines


def id_words(eid) -> str:
    """An editor ID said in words, its numbers kept so two cells stay apart ("SolitudeExterior01" -> "Solitude Exterior
    01", "BUV_EisaFollowerRef" -> "BUV Eisa Follower Ref")."""
    s = re.sub(r"[_]+", " ", str(eid or ""))
    s = re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])", " ", s)
    return " ".join(s.split())


def said_place(label, places: dict, words: dict, reader) -> str:
    """A place as the report prints it (N6): the cell's in-game name; else, for an exterior cell named by form key, its
    worldspace and grid cell read from the cell's own plugin (reader(what, formKey) -> editorId, cellWorld, cellGrid);
    else its editor ID said in words. Never a form key and never a bare editor ID: those stay in the evidence index."""
    lab = str(label)
    m = re.match(r"^([A-Za-z][A-Za-z0-9_]*)(?: \((?:int|ext)\))?$", lab)
    if m:
        return (places.get(m.group(1)) or {}).get("name") or id_words(m.group(1))
    m = re.match(r"^([0-9A-Fa-f]{6}):(\S+?)(?: \((int|ext)\))?$", lab)
    if not m:
        return lab
    fk = f"{m.group(1).upper()}:{m.group(2)}"
    eid = reader("editorId", fk)
    if isinstance(eid, str) and eid:
        return (places.get(eid) or {}).get("name") or id_words(eid)
    world, grid = reader("cellWorld", fk), reader("cellGrid", fk)
    if isinstance(world, str) and isinstance(grid, (list, tuple)) and len(grid) == 2:
        weid = reader("editorId", world)
        wname = ((places.get(weid) or {}).get("name") or id_words(weid)) if isinstance(weid, str) and weid else None
        if wname and words.get("persistentPlaceGridWorld"):
            return bind(words["persistentPlaceGridWorld"], {"world": wname, "x": fmt_n(grid[0]), "y": fmt_n(grid[1])})
    # (item 4) never a silent generic name: an unread cell says why it was not read; a read one with nothing to say it by
    # says that, as a fact of its file
    plugin = m.group(2)
    why = reader("why", fk)
    if why and why != "read":
        reasons = words.get("placeUnreadWhy") or {}
        return bind(words["placeUnread"], {"plugin": plugin, "why": reasons.get(why) or reasons.get("error") or why})
    return bind(words["placeNoName"], {"plugin": plugin})


FK_SHAPE = re.compile(r"^([0-9A-Fa-f]{6}):(.+)$")


def record_object_name(fk, names: dict, words: dict) -> str | None:
    """A placed object in words (item 1): its base's kind (skill data objectKinds) and its base's in-game name, else the
    base's editor ID in words ("a tree (Tree Thicket 01 Snow)"); None when the names enrichment does not read its base."""
    refs, bases = (names or {}).get("refs") or {}, (names or {}).get("bases") or {}
    base = bases.get((refs.get(str(fk)) or {}).get("base") or "") or {}
    eid = base.get("editorId")
    if not eid:
        return None
    said = ((names.get("objects") or {}).get(eid) or {}).get("name") or id_words(eid)
    kind = ((words.get("objectKinds") or {}).get("types") or {}).get(str(base.get("type") or ""), "")
    return bind(words.get("recordObject") or "{kind} ({name})", {"kind": kind, "name": said}) if kind else said


def said_record(fk, names: dict, labels: dict, words: dict, reader) -> str:
    """A record as the report prints it (item 1), the way said_place says a place: a placed object by its kind and base
    (record_object_name); a record the plugin files name by that name (labels: record_labels); else its editor ID, read
    from its own plugin (reader("editorId", fk)), as an in-game name the names enrichment holds for it or in words; a
    navmesh by its plugin and its place (reader("navmeshPlace", fk) -> ["cell", cellFk] or ["grid", worldFk, x, y]);
    else "a record of <plugin> whose name was not read". Never a form key: those stay in the evidence index."""
    m = FK_SHAPE.match(str(fk))
    if not m:
        return str(fk)
    fk, plugin = f"{m.group(1).upper()}:{m.group(2)}", m.group(2)
    obj = record_object_name(fk, names, words)
    if obj:
        return obj
    places, objects = (names or {}).get("places") or {}, (names or {}).get("objects") or {}

    def eid_said(eid):
        return (places.get(eid) or {}).get("name") or (objects.get(eid) or {}).get("name") or id_words(eid)
    lab = (labels or {}).get(fk)
    if lab:
        return eid_said(lab) if re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", str(lab)) else str(lab)
    eid = reader("editorId", fk)
    if isinstance(eid, str) and eid:
        return eid_said(eid)
    base = reader("placedBase", fk)
    if isinstance(base, str) and base:
        obj = record_object_name(fk, {**(names or {}), "refs": {fk: {"base": base}}}, words)
        beid = reader("editorId", base)
        if obj or (isinstance(beid, str) and beid):
            return obj or bind(words.get("recordPlaced") or "a placed {name}", {"name": eid_said(beid)})
    def cell_said(cell_fk):
        """The cell said by said_place; None for a cell nothing names, which does not tell one record from another."""
        where_ = said_place(str(cell_fk), places, words, reader)
        cp_ = str(cell_fk).split(":", 1)[-1]
        if words.get("placeNoName") and where_ == bind(words["placeNoName"], {"plugin": cp_}) or (
                words.get("placeUnread") and where_.startswith(bind(words["placeUnread"], {"plugin": cp_, "why": ""})[:-3])):
            return None
        return where_
    place = reader("navmeshPlace", fk)
    if isinstance(place, (list, tuple)) and place:
        if place[0] == "cell" and len(place) >= 2:
            where = cell_said(place[1])
        elif place[0] == "grid" and len(place) >= 4:
            weid = reader("editorId", str(place[1]))
            wname = eid_said(weid) if isinstance(weid, str) and weid else None
            where = bind(words["persistentPlaceGridWorld"], {"world": wname, "x": fmt_n(place[2]), "y": fmt_n(place[3])}) \
                if wname and words.get("persistentPlaceGridWorld") else None
        else:
            where = None
        if where:
            return bind(words.get("recordNavmesh") or "a navmesh of {plugin} in {place}", {"plugin": plugin, "place": where})
    # (report words, item 2) a landscape record is the ground of its cell: the cell-children group that files it, read
    # from its own plugin (reader("recordCell", fk) -> {"type", "cell"}), said as said_place says that cell
    held = reader("recordCell", fk)
    if isinstance(held, dict) and held.get("type") == "LAND" and held.get("cell"):
        where = cell_said(held["cell"])
        if where:
            return bind(words.get("recordLandscape") or "the ground of {place}", {"place": where})
    if reader("why", fk) == "read":
        # read from its own plugin: the record has no editor ID and nothing else here names it
        return bind(words.get("recordNoName") or "a record of {plugin} with no editor ID", {"plugin": plugin})
    return bind(words.get("recordUnread") or "a record of {plugin} whose name was not read", {"plugin": plugin})


# data a later step parses (form keys, a row's summary, paths, ids) keeps its own spelling; only sentences are said
SAY_SKIP_KEYS = frozenset({"sha256", "id", "formkey", "other", "record", "records", "object", "summary", "members", "fk",
                           "recordkeys", "keys", "refs", "partners", "pair", "placelabels", "label", "door", "navmesh"})


OBJECT_PAREN = re.compile(r" \(((?:a|an|the) [^,()]*?) ([A-Za-z][A-Za-z0-9_]*)(?=[,)])")


def say_form_keys(obj, sayer, plugins, words: dict, objects: dict | None = None):
    """Every form key left in a sentence said in words (item 1), at the one boundary the reader, groups and findings
    leave the plan by: `sayer(fk) -> (said, editorId, label)` (said_record, and the names the builders print beside a
    key). "<key> (<kind> <EditorID>, ...)" (the builders' object name) becomes "<kind> (<words>, ...)"; "<label>, <key>"
    and "<key> <editorId>" become the words alone; a bare key becomes the words. A value (quoted, or written with its
    editor ID) whose words another value in the same sentence shares keeps its form key, so two different values never
    read the same."""
    names_ = sorted({str(p) for p in plugins if p}, key=len, reverse=True)
    if not names_:
        return obj
    pat = re.compile(r"(?<![0-9A-Fa-f])([0-9A-Fa-f]{6}):(" + "|".join(re.escape(n) for n in names_) + r")(?![\w])",
                     re.IGNORECASE)
    objects = objects or {}
    cache = {}

    def say(fk):
        if fk not in cache:
            cache[fk] = sayer(fk)
        return cache[fk]

    def fix(s):
        ms = list(pat.finditer(s))
        if not ms or (len(ms) == 1 and ms[0].span() == (0, len(s.strip()))):
            return s                      # a field that is one form key is data (the evidence index prints it)
        found = []
        for m in ms:
            fk = f"{m.group(1).upper()}:{m.group(2)}"
            said, eid, lab = say(fk)
            a, b = m.start(), m.end()
            kind = "bare"
            om = OBJECT_PAREN.match(s, b)
            if om:
                # the builder's own object name: its kind kept, its editor ID said in words
                kind_, nm_ = om.group(1), (objects.get(om.group(2)) or {}).get("name") or id_words(om.group(2))
                b, kind = om.end(), "object"
                if kind_.split()[-1] == "placed":
                    said = f"{kind_} {nm_}"
                    if s.startswith(", ", b):
                        said, b = said + " (", b + 2
                    elif s.startswith(")", b):
                        b += 1
                else:
                    said = bind(words.get("recordObject") or "{kind} ({name})", {"kind": kind_, "name": nm_})[:-1]
            else:
                for nm_ in (lab, eid):
                    if nm_ and s[max(0, a - len(nm_) - 2):a].casefold() == f"{nm_}, ".casefold():
                        a, kind = a - len(nm_) - 2, "label"
                        break
                if eid and kind == "bare" and s[b:].startswith(f" {eid}"):
                    b, kind = b + 1 + len(eid), "value"
                elif kind == "bare" and s[max(0, a - 1):a] == '"' and s[b:b + 1] == '"':
                    kind = "value"
            found.append([a, b, fk, said, kind])
        # different values that would read the same keep their form keys, so a sentence never says "A has X, B has
        # X" of two values (Gate A review, answer 2); different records named alike are numbered in the order the
        # text first names them ("a tree (Tree Thicket 01 Snow #1)"), so one line never makes two of them one
        # a record nothing names (its plugin not read, or read and holding no editor ID) keeps its form key: no words
        # here can say which record it is
        unnamed_ = {bind(words.get(k_) or d_, {"plugin": x[2].split(":", 1)[1]}).casefold() for x in found
                    for k_, d_ in (("recordUnread", "a record of {plugin} whose name was not read"),
                                   ("recordNoName", "a record of {plugin} with no editor ID"))}
        apart = lambda x: x[4] == "value"  # noqa: E731
        by_said = {}
        for x in found:
            if apart(x):
                by_said.setdefault(x[3].casefold(), set()).add(x[2])
        alike = {}
        key_ = lambda w_: w_.rstrip(" (").rstrip(")").casefold()  # noqa: E731
        for x in found:
            if not apart(x) and x[3].casefold() not in unnamed_:
                g_ = alike.setdefault(key_(x[3]), [])
                if x[2] not in g_:
                    g_.append(x[2])
        out, last = [], 0
        for x in found:
            a, b, fk, said, kind = x
            if a < last:
                continue
            keep = (apart(x) and len(by_said.get(said.casefold(), ())) > 1) or said.casefold() in unnamed_
            group = alike.get(key_(said)) or []
            if not keep and len(group) > 1:
                tail_ = " (" if said.endswith(" (") else ")" if said.endswith(")") else ""
                said = said[:len(said) - len(tail_)] + bind(words.get("recordNumbered") or " #{n}",
                                                             {"n": group.index(fk) + 1}) + tail_
            out.append(s[last:a])
            out.append(s[a:b] if keep else said)
            last = b
        out.append(s[last:])
        return "".join(out)

    def walk(x, key=None):
        if isinstance(x, str):
            return x if (key and ("path" in str(key).lower() or str(key).lower() in SAY_SKIP_KEYS)) else fix(x)
        if isinstance(x, list):
            return [walk(v, key) for v in x]
        if isinstance(x, dict):
            return {k: walk(v, k) for k, v in x.items()}
        return x
    return walk(obj)


def media_kind_caption(media: dict, words: dict) -> str:
    """The figure caption's addition for a picture drawn offline from the game's files rather than taken in the game
    ("" for a game capture), from the skill's figureCaptions words (Ice r3 report review 8)."""
    kinds = (words.get("mediaKinds") or {})
    return str(kinds.get(str((media or {}).get("mediaKind") or "")) or "")


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def sha256_text(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def canonical(obj) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def load_json(p: Path):
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def write_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False)
        f.write("\n")


def write_text(p: Path, text: str) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def compile_triage_catalogue(manifest: dict, policy: dict, source_sha: str, source_path: str | None = None,
                             report_base: str | None = None) -> tuple[dict, str]:
    """Compile the triage extension through evalreport without changing the canonical manifest."""
    if manifest.get("triageSchemaVersion") != TRIAGE_SCHEMA:
        raise ValueError(f"triage-schema-unsupported: expected {TRIAGE_SCHEMA}")
    policy_sha = sha256_text(canonical(policy))
    recorded_policy = manifest.get("attributionPolicy") or {}
    recorded = (recorded_policy.get("triageRanking") or {}).get("sha256") or recorded_policy.get("sha256")
    if recorded != policy_sha:
        raise ValueError("triage-policy-changed: the supplied policy does not match the manifest; rerun overlap_triage")
    records = {str(x.get("formid")): x for x in manifest.get("records") or [] if isinstance(x, dict)}
    pairs = {str(x.get("pairKey")): x for x in manifest.get("pairs") or [] if isinstance(x, dict)}
    accounting_root = manifest.get("accounting") or {}
    accounting = dict(accounting_root.get("triage") or accounting_root)
    memberships = [key for incident in manifest.get("incidents") or [] for key in incident.get("pairKeys") or []]
    duplicates = sorted(k for k, n in collections.Counter(memberships).items() if n > 1)
    dangling = sorted(set(memberships) - set(pairs))
    unaccounted = sorted(set(pairs) - set(memberships))
    if (duplicates or dangling or unaccounted or accounting.get("pairsTotal") != len(pairs) or
            accounting.get("incidentsTotal") != len(manifest.get("incidents") or [])):
        raise ValueError(f"triage-accounting-invalid: duplicates={len(duplicates)}, dangling={len(dangling)}, "
                         f"unaccounted={len(unaccounted)}, declared={accounting.get('pairsTotal')}, actual={len(pairs)}, "
                         f"declaredIncidents={accounting.get('incidentsTotal')}, actualIncidents={len(manifest.get('incidents') or [])}")

    def is_reviewed(review):
        return isinstance(review, dict) and (review.get("status", "unreviewed") != "unreviewed" or
                                              review.get("consequence") is not None)

    def member_summary(pair):
        pair_objects = []
        for key in (pair.get("a"), pair.get("b")):
            record = records.get(str(key), {})
            fields = record.get("fields") or {}; base = fields.get("effectiveBase") or {}
            pair_objects.append({"formKey": key,
                                 "label": record.get("name") or record.get("editorid") or
                                          base.get("name") or base.get("editorid") or str(key),
                                 "type": base.get("type") or record.get("type"),
                                 "mod": fields.get("responsiblePlugin") or record.get("winner")})
        comparisons = []
        for comparison in (pair.get("attribution") or {}).get("comparisons") or []:
            changes = comparison.get("changedFields") or {}
            before_objects = {str(x.get("formid")): x for x in (comparison.get("before") or {}).get("objects") or []}
            after_objects = {str(x.get("formid")): x for x in (comparison.get("after") or {}).get("objects") or []}
            changed_values = {}
            for formid, names in changes.items():
                before, after = before_objects.get(str(formid), {}), after_objects.get(str(formid), {})
                changed_values[str(formid)] = {name: {"before": before.get(name), "after": after.get(name)}
                                               for name in names or []}
            comparisons.append({"beforeSetupId": comparison.get("beforeSetupId"),
                                "afterSetupId": comparison.get("afterSetupId"),
                                "status": comparison.get("status"), "reason": comparison.get("reason"),
                                "changedFields": {str(k): list(v or []) for k, v in changes.items() if v},
                                "changedValues": changed_values})
        features = {name: {"status": f.get("status"), "reason": f.get("reason")}
                    for name, f in (pair.get("features") or {}).items()}
        actual = pair.get("actualShape") or {}
        review = copy.deepcopy(pair.get("review") or {"status": "unreviewed", "consequence": None})
        unknown_names = sorted(name for name, feature in features.items() if feature.get("status") != "measured")
        confidence = {
            "actualShapeStatus": actual.get("status"), "intersects": actual.get("intersects"),
            "unknownFeatureNames": unknown_names,
            "analogueConfidence": sorted({str(a.get("confidence")) for a in pair.get("analogues") or [] if a.get("confidence")})}
        return {"pairKey": pair.get("pairKey"), "a": pair.get("a"), "b": pair.get("b"),
                "objects": pair_objects,
                "attribution": {"status": (pair.get("attribution") or {}).get("status"),
                                "reasons": (pair.get("attribution") or {}).get("reasons") or [],
                                "comparisons": comparisons},
                "review": review, "reviewed": is_reviewed(review), "confidenceBasis": confidence,
                "queues": copy.deepcopy(pair.get("queues") or {}),
                "analogues": [{"matchKind": a.get("matchKind"), "confidence": a.get("confidence"),
                               "sourceSetupId": a.get("sourceSetupId"),
                               "sourcePairKey": a.get("sourcePairKey"), "limits": a.get("limits") or []}
                              for a in pair.get("analogues") or []],
                "features": features}
    rows = []
    for incident in manifest.get("incidents") or []:
        members = [pairs[k] for k in incident.get("pairKeys") or [] if k in pairs]
        objects, mods, limits = [], set(), set(incident.get("limits") or [])
        comparison_counts, field_counts = collections.Counter(), collections.Counter()
        for pair in members:
            for key in (pair.get("a"), pair.get("b")):
                record = records.get(str(key), {})
                fields = record.get("fields") or {}
                base = fields.get("effectiveBase") or {}
                label = record.get("name") or record.get("editorid") or base.get("name") or base.get("editorid") or str(key)
                objects.append({"formKey": key, "label": label, "type": base.get("type") or record.get("type"),
                                "mod": fields.get("responsiblePlugin") or record.get("winner")})
                if objects[-1]["mod"]:
                    mods.add(objects[-1]["mod"])
            attr = pair.get("attribution") or {}
            for comparison in attr.get("comparisons") or []:
                comparison_counts[(comparison.get("beforeSetupId"), comparison.get("afterSetupId"),
                                   comparison.get("status") or "unresolved")] += 1
                for names in (comparison.get("changedFields") or {}).values():
                    field_counts.update(str(name) for name in names or [])
            for feature in (pair.get("features") or {}).values():
                if feature.get("status") != "measured" and feature.get("reason"):
                    limits.add(feature["reason"])
        dedup_objects = list({str(x["formKey"]): x for x in objects}.values())
        member_rows = [member_summary(p) for p in members]
        reviewed_count = sum(m["reviewed"] for m in member_rows)
        review_scope = "fully_reviewed" if member_rows and reviewed_count == len(member_rows) else \
                       "partially_reviewed" if reviewed_count else "unreviewed"
        confidence_basis = {
            "measuredActualIntersections": sum(m["confidenceBasis"]["intersects"] is True for m in member_rows),
            "unknownActualGeometry": sum(m["confidenceBasis"]["intersects"] is None for m in member_rows),
            "membersWithAnalogueContext": sum(bool(m["analogues"]) for m in member_rows),
        }
        effective_change = [f"{before}→{after}: {status} ×{count}" for (before, after, status), count in
                            sorted(comparison_counts.items(), key=lambda item: tuple(str(x) for x in item[0]))]
        if field_counts:
            effective_change.append("effective fields: " + ", ".join(
                f"{name} ×{count}" for name, count in sorted(field_counts.items())))
        rows.append({"incidentId": incident.get("incidentId"), "place": incident.get("place"),
                     "objects": dedup_objects, "mods": sorted(mods), "effectiveChange": effective_change,
                     "pairKeys": list(incident.get("pairKeys") or []), "ranking": incident.get("ranking"),
                     "review": incident.get("review"), "reviewScope": review_scope,
                     "reviewedMemberCount": reviewed_count, "unreviewedMemberCount": len(member_rows) - reviewed_count,
                     "confidenceBasis": confidence_basis,
                     "limits": sorted(limits),
                     "sourceSetups": incident.get("sourceSetups") or [],
                     "members": member_rows})
    for index, row in enumerate(rows):
        row["page"] = f"incidents-{index // 50 + 1:04d}.html"
    report_qualification = (manifest.get("scope") or {}).get("reportQualification")
    if report_qualification is not None and (not isinstance(report_qualification, str) or not report_qualification.strip()):
        raise ValueError("triage-report-qualification-invalid: scope.reportQualification must be a non-empty string")
    report_qualification = report_qualification.strip() if report_qualification else None
    identity_input = {"generator": f"evalreport-{COMPILER_VERSION}", "triageSchemaVersion": TRIAGE_SCHEMA,
                      "sourceSha256": source_sha, "policySha256": policy_sha,
                      "reportQualification": report_qualification}
    accounting.update({
        "reviewedPairs": sum(row["reviewedMemberCount"] for row in rows),
        "unreviewedPairs": sum(row["unreviewedMemberCount"] for row in rows),
        "fullyReviewedIncidents": sum(row["reviewScope"] == "fully_reviewed" for row in rows),
        "partiallyReviewedIncidents": sum(row["reviewScope"] == "partially_reviewed" for row in rows),
        "unreviewedIncidents": sum(row["reviewScope"] == "unreviewed" for row in rows),
        "outstandingReviewIncidents": sum(row["unreviewedMemberCount"] > 0 for row in rows),
        "newOrChangedGeometryUnknownPairs": sum(bool((m.get("queues") or {}).get("newOrChangedGeometryUnknown"))
                                                 for row in rows for m in row["members"]),
        "newOrChangedGeometryUnknownIncidents": sum(any((m.get("queues") or {}).get("newOrChangedGeometryUnknown")
                                                        for m in row["members"]) for row in rows),
        "pairsByAttribution": {status: sum(m["attribution"]["status"] == status for row in rows for m in row["members"])
                               for status in ("unresolved", "new", "changed_existing", "absent_excluded", "inherited")},
        "pairsWithAnalogueContext": sum(bool(m["analogues"]) for row in rows for m in row["members"]),
        "pairsDeprioritizedByAnalogue": sum(any(reason.get("quantity") == "analogue" and int(reason.get("points") or 0) < 0
                                                 for reason in (pairs[m["pairKey"]].get("ranking") or {}).get("reasons") or [])
                                            for row in rows for m in row["members"]),
    })
    catalogue = {"schemaVersion": "overlap-triage-catalogue-v1", "identity": sha256_text(canonical(identity_input)),
                 "source": identity_input, "accounting": accounting, "incidents": rows}
    catalogue["source"]["canonicalManifest"] = {"path": source_path, "sha256": source_sha}
    catalogue["source"]["reportBase"] = report_base
    base_title = (policy.get("catalogue") or {}).get("title") or "Overlap incident review catalogue"
    attribution_policy = manifest.get("attributionPolicy") or {}
    candidate_names = attribution_policy.get("candidatePlugins") or []
    comparison_ids = attribution_policy.get("comparisonSetupIds") or []
    title_scope = ", ".join(candidate_names) + ((" across " if candidate_names else "") + ", ".join(comparison_ids) if comparison_ids else "")
    title = base_title + (f" — {title_scope}" if title_scope else "")
    unknown_label = (policy.get("catalogue") or {}).get("unknownLabel") or "Unknown feature values"
    unreviewed_label = (policy.get("catalogue") or {}).get("unreviewedLabel") or "Pairs awaiting review"
    unreviewed_consequence = (policy.get("catalogue") or {}).get("unreviewedConsequence") or "No consequence established."
    next_action = (policy.get("catalogue") or {}).get("nextAction") or "Review the representative pair."
    def href_for(path):
        if not path:
            return None
        if report_base:
            return Path(os.path.relpath(Path(path).resolve(), Path(report_base).resolve())).as_posix()
        return Path(path).resolve().as_uri()
    parts = ["<!doctype html><html><head><meta charset=\"utf-8\"><title>", html.escape(title),
             "</title><style>body{font:16px/1.45 system-ui;max-width:1100px;margin:2rem auto;padding:0 1rem}"
             "table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:.45rem;vertical-align:top}"
             "th{text-align:left}code{overflow-wrap:anywhere}</style></head><body><h1>", html.escape(title), "</h1>",
             "<p>Every discovered pair remains listed under one local incident. Ranking orders review and does not decide consequence.</p>",
             (f'<p>Canonical full data: <a href="{html.escape(href_for(source_path), quote=True)}">annotated manifest</a> '
              f'<code>{html.escape(source_sha)}</code>.</p>' if source_path else
              f'<p>Canonical full data hash: <code>{html.escape(source_sha)}</code>.</p>'),
             (f'<p><strong>Scope qualification:</strong> {html.escape(report_qualification)}</p>'
              if report_qualification else ""),
             "<ul><li>Total pairs: ", str(accounting.get("pairsTotal", 0)), "</li><li>Total incidents: ",
             str(accounting.get("incidentsTotal", 0)), "</li><li>", html.escape(unreviewed_label), ": ",
             str(accounting.get("unreviewedPairs", 0)), "</li><li>", html.escape(unknown_label), ": ",
             str(accounting.get("unknownFeatureValues", 0)), " values across ", str(accounting.get("unknownPairs", 0)),
             " of ", str(accounting.get("pairsTotal", 0)), " pairs</li><li>New/changed pairs with unknown geometry: ",
             str(accounting.get("newOrChangedGeometryUnknownPairs", 0)), " of ", str(accounting.get("pairsTotal", 0)),
             " pairs, in ", str(accounting.get("newOrChangedGeometryUnknownIncidents", 0)),
             " incidents (a separately visible subset that can overlap the broader unknown-feature view)</li><li>Inherited pairs: ",
             str((accounting.get("pairsByAttribution") or {}).get("inherited", 0)), " of ", str(accounting.get("pairsTotal", 0)),
             "</li><li>Pairs with analogue context: ", str(accounting.get("pairsWithAnalogueContext", 0)), " of ",
             str(accounting.get("pairsTotal", 0)), "; ranking-deprioritized by analogue: ",
             str(accounting.get("pairsDeprioritizedByAnalogue", 0)), "</li></ul>",
             "<p>Views: <button onclick=\"showRows('all')\">All incidents (", str(accounting.get("incidentsTotal", 0)),
             ")</button> <button onclick=\"showRows('unknown')\">Incidents with unknowns (", str(sum(any(f.get("status") != "measured" for m in row["members"] for f in m["features"].values()) for row in rows)),
             ")</button> <button onclick=\"showRows('outstanding')\">Outstanding review (", str(accounting.get("outstandingReviewIncidents", 0)),
             ")</button> <button onclick=\"showRows('partially_reviewed')\">Partially reviewed (", str(accounting.get("partiallyReviewedIncidents", 0)),
             ")</button> <button onclick=\"showRows('geometry-unknown-change')\">New/changed geometry unknown (", str(accounting.get("newOrChangedGeometryUnknownIncidents", 0)),
             ")</button></p><table><thead><tr><th>Incident and place</th><th>Objects and mods</th><th>Effective change</th>"
             "<th>Review order, status and next action</th><th>Limits and evidence</th></tr></thead><tbody>"]
    for row in rows:
        place = row.get("place") or {}; cell = place.get("cell") or {}
        place_text = cell.get("editorid") or cell.get("formid") or place.get("space") or "place not supplied"
        object_text = "; ".join(f"{x.get('label')} ({x.get('type') or 'type unknown'}, {x.get('mod') or 'mod unknown'})"
                                for x in row["objects"])
        ranking = row.get("ranking") or {}; review = row.get("review") or {}
        rank_reasons = "; ".join(f"{x.get('quantity')}={x.get('value')} ({int(x.get('points') or 0):+d})"
                                 for x in ranking.get("reasons") or [])
        consequence = review.get("consequence") or unreviewed_consequence
        row_next_action = (f"Review each of the {row['unreviewedMemberCount']} outstanding member pairs in fixed-camera source and combined scenes; "
                           "a representative review applies only to that pair."
                           if row["unreviewedMemberCount"] else
                           (review.get("nextAction") or "Every member pair has an individual review."))
        confidence = row.get("confidenceBasis") or {}
        confidence_text = (f"confidence basis: {confidence.get('measuredActualIntersections', 0)} measured intersections; "
                           f"{confidence.get('unknownActualGeometry', 0)} geometry unknown; "
                           f"{confidence.get('membersWithAnalogueContext', 0)} with analogue context")
        unknown = any(f.get("status") != "measured" for member in row["members"] for f in member["features"].values())
        queue_unknown = any((m.get("queues") or {}).get("newOrChangedGeometryUnknown") for m in row["members"])
        row_class = "unknown " if unknown else ""
        row_class += row["reviewScope"] + " "
        row_class += "outstanding " if row["unreviewedMemberCount"] else ""
        row_class += "geometry-unknown-change" if queue_unknown else ""
        source_parts = []
        for source_setup in row["sourceSetups"]:
            source_path = source_setup.get("manifestPath")
            label = html.escape(str(source_setup.get("setupId")))
            if source_path:
                label = f'<a href="{html.escape(href_for(source_path), quote=True)}">{label}</a>'
            source_parts.append(f"{label}: {html.escape(str(source_path or 'path unavailable'))} "
                                f"[{html.escape(str(source_setup.get('manifestSha256') or 'hash unavailable'))}]")
        sources = "; ".join(source_parts)
        detail_link = f'{row["page"]}#{html.escape(str(row.get("incidentId")), quote=True)}'
        parts.extend(["<tr class=\"", row_class, "\"><td><strong>", html.escape(str(row.get("incidentId"))), "</strong><br>", html.escape(str(place_text)),
                      "<br><small>", str(len(row["pairKeys"])), " pair(s)</small></td><td>", html.escape(object_text),
                      "</td><td>", html.escape(", ".join(row["effectiveChange"])), "</td><td>score ",
                      html.escape(str(ranking.get("score"))), " — ", html.escape(rank_reasons), "<br>",
                      html.escape(row["reviewScope"]), " (", str(row["reviewedMemberCount"]), " reviewed; ",
                      str(row["unreviewedMemberCount"]), " outstanding): ", html.escape(str(consequence)),
                      "<br>", html.escape(row_next_action),
                      "</td><td>", html.escape(confidence_text), "<br>", html.escape("; ".join(row["limits"])), "<p>", sources or "No source setup link supplied.",
                      f'</p><a href="{detail_link}">Every member pair and source-evidence citation</a></td></tr>'])
    parts.extend(["</tbody></table><p><small>Catalogue identity <code>", catalogue["identity"],
                  "</code>; source <code>", source_sha, "</code>; policy <code>", policy_sha,
                  "</code>.</small></p><script>function showRows(k){document.querySelectorAll('tbody tr').forEach(r=>r.hidden=k!=='all'&&!r.classList.contains(k));}</script></body></html>"])
    return catalogue, "".join(parts)


def render_triage_incident_pages(catalogue: dict, page_size: int = 50) -> dict[str, str]:
    """Render bounded detail pages while retaining every incident and pair membership."""
    rows = catalogue.get("incidents") or []
    total_pages = max(1, (len(rows) + page_size - 1) // page_size)
    rendered = {}
    for page_index in range(total_pages):
        subset = rows[page_index * page_size:(page_index + 1) * page_size]
        nav = " ".join(f'<a href="incidents-{n:04d}.html">{n}</a>' for n in range(1, total_pages + 1))
        parts = ["<!doctype html><html><head><meta charset=\"utf-8\"><title>Overlap incident details</title>",
                 "<style>body{font:16px/1.45 system-ui;max-width:1100px;margin:2rem auto;padding:0 1rem}"
                 "pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f5f5;padding:.75rem}"
                 "article{border-top:1px solid #bbb;margin-top:2rem}</style></head><body>",
                 '<p><a href="overlap-triage-catalogue.html">Catalogue index</a></p>',
                 (f'<p><strong>Scope qualification:</strong> '
                  f'{html.escape(str((catalogue.get("source") or {}).get("reportQualification")))}</p>'
                  if (catalogue.get("source") or {}).get("reportQualification") else ""),
                 f"<p>Detail page {page_index + 1} of {total_pages}; {len(subset)} incidents on this page. Pages: {nav}</p>"]
        for row in subset:
            members = row.get("members") or []
            page_unknown = sum(any(f.get("status") != "measured" for f in (m.get("features") or {}).values()) for m in members)
            page_unreviewed = row.get("unreviewedMemberCount", 0)
            member_summaries = []
            for member in members:
                labels = " ↔ ".join(f"{x.get('label')} [{x.get('formKey')}, {x.get('mod') or 'mod unknown'}]"
                                    for x in member.get("objects") or [])
                comparisons = "; ".join(
                    f"{c.get('beforeSetupId')}→{c.get('afterSetupId')}: {c.get('status')}"
                    for c in (member.get("attribution") or {}).get("comparisons") or [])
                review = member.get("review") or {}
                consequence = review.get("consequence") or "unreviewed"
                member_summaries.append("<li><strong>" + html.escape(str(member.get("pairKey"))) +
                    "</strong>: " + html.escape(labels) + "<br>" + html.escape(comparisons) +
                    "<br>Consequence: " + html.escape(str(consequence)) + "</li>")
            parts.extend([f'<article id="{html.escape(str(row.get("incidentId")), quote=True)}"><h2>',
                          html.escape(str(row.get("incidentId"))), "</h2><p>", str(len(members)),
                          " member pairs; review scope: ", html.escape(str(row.get("reviewScope"))),
                          "; unreviewed members: ", str(page_unreviewed),
                          "; member pairs with unknown features: ", str(page_unknown),
                          ". Global totals: ", str(catalogue.get("accounting", {}).get("pairsTotal", 0)),
                          " pairs, ", str(catalogue.get("accounting", {}).get("unreviewedPairs", 0)),
                          " unreviewed.</p><ul>", "".join(member_summaries),
                          "</ul><details><summary>Full member evidence, changes, confidence and review data</summary><pre>",
                          html.escape(json.dumps(members, indent=2, ensure_ascii=False)),
                          "</pre></details></article>"])
        parts.append("</body></html>")
        rendered[f"incidents-{page_index + 1:04d}.html"] = "".join(parts)
    return rendered


def read_text(p: Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


def refuse(run: Run | None, reason: str, code: int = EXIT_REFUSED_INPUT) -> int:
    print(f"REFUSED: {reason}", file=sys.stderr)
    if run is not None:
        run.refuse(reason, exit_code=code)
    return code


PATH_RUN = re.compile(r"[\w .'()\\/-]+")          # one character class: linear, no backtracking
PATH_END = re.compile(r"\.[A-Za-z0-9]{2,4}\s*$")


def _fold_paths(v: str) -> str:
    """Every run of path characters that holds a slash and ends in a file extension, lower-cased with forward slashes."""
    def one(m):
        t = m.group(0)
        return t.replace("\\", "/").lower() if ("\\" in t or "/" in t) and PATH_END.search(t) else t
    return PATH_RUN.sub(one, v)


def path_folded(v) -> str:
    """A value with every file path in it compared as the game reads it: letter case and slash direction ignored."""
    return _fold_paths(str(v))


def same_but_path_case(a, b) -> bool:
    """Two values that differ only in the letter case or slashes of a file path are the same value to the game."""
    return a is not None and b is not None and str(a) != str(b) and path_folded(a) == path_folded(b)


def fmt_units(v: float) -> str:
    """A distance in game units, as a modder reads it: two decimals below 1, one below 10, whole numbers above."""
    v = abs(float(v))
    return f"{v:.2f} u" if v < 1 else f"{v:.1f} u" if v < 10 else f"{fmt_n(round(v))} u"


def missing_everywhere(missing, arm_ids) -> set:
    """(Missives r3 report review, finding 3) the checks with no result of their own on every tested setup (a missing
    comparison part does not count): said once as such, never as a gap of only the setups a section happens to list."""
    arm_ids = set(arm_ids)
    on = collections.defaultdict(set)
    for m in missing or []:
        if m.get("part") not in ("diff", "reverse") and m.get("armId"):
            on[str(m.get("checkId"))].add(m.get("armId"))
    return {c for c, arms in on.items() if arms_ids_cover(arms, arm_ids)}


def arms_ids_cover(arms, arm_ids) -> bool:
    return bool(arm_ids) and arm_ids <= set(arms)


def and_list(xs) -> str:
    """Names as one phrase: "a", "a and b", "a, b and c"."""
    xs = [str(x) for x in xs]
    return "" if not xs else xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


def fmt_n(v) -> str:
    if isinstance(v, bool) or v is None:
        return str(v)
    if isinstance(v, int):
        return f"{v:,}"
    if isinstance(v, float):
        return f"{v:,.2f}"
    return str(v)


def arm_sort_key(arm_id: str, kinds: dict | None = None) -> tuple:
    kind = (kinds or {}).get(arm_id)
    return (0 if (arm_id == "baseline" or kind == "baseline") else 1, arm_id)


PART_ORDER = {None: 0, "arm": 1, "reverse": 2, "diff": 3}


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", str(text).lower()).strip("-")


def load_priorities() -> dict:
    pr = load_json(PRIORITIES)
    if not isinstance(pr, dict):
        raise SystemExit(f"REFUSED: report-priorities.json is absent or not JSON at {PRIORITIES}")
    return pr


# ------------------------------------------------------------------------------------ the browser --

WINDOWS_BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Chromium\Application\chrome.exe",
]


def browser_version(exe: Path) -> str:
    # Chrome and Edge on Windows keep the version as a sibling directory of the executable; asking the
    # binary itself prints nothing usable in headless mode. Elsewhere `--version` answers.
    try:
        vers = sorted(d.name for d in exe.parent.iterdir()
                      if d.is_dir() and re.fullmatch(r"\d+\.\d+\.\d+\.\d+", d.name))
        if vers:
            return vers[-1]
    except OSError:
        pass
    if os.name != "nt":
        try:
            out = subprocess.run([str(exe), "--version"], capture_output=True, text=True, timeout=20)
            if out.stdout.strip():
                return out.stdout.strip()
        except (OSError, subprocess.SubprocessError):
            pass
    return "unknown"


def resolve_browser() -> tuple[Path | None, str, str]:
    """Resolution order (D4): HOUSECARL_BROWSER, PATH chrome / msedge / chromium, the standard Windows
    install paths. Returns (path, version, reason-when-absent)."""
    env = (os.environ.get("HOUSECARL_BROWSER") or "").strip()
    if env:
        p = Path(env)
        if p.exists():
            return p, browser_version(p), ""
        return None, "", f"HOUSECARL_BROWSER points at {env}, which does not exist"
    for name in ("chrome", "google-chrome", "google-chrome-stable", "msedge", "chromium", "chromium-browser"):
        w = shutil.which(name)
        if w:
            return Path(w), browser_version(Path(w)), ""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        cands = list(WINDOWS_BROWSERS)
        if local:
            cands.append(str(Path(local) / "Google" / "Chrome" / "Application" / "chrome.exe"))
        for c in cands:
            if Path(c).exists():
                return Path(c), browser_version(Path(c)), ""
    return None, "", ("no Chromium browser found: set HOUSECARL_BROWSER, or put chrome / msedge / chromium "
                      "on PATH, or install Chrome or Edge at the standard location")


def pdf_page_count(pdf: Path) -> tuple[int | None, int | None, str]:
    """Count `/Type /Page` objects (not `/Pages`) in the PDF's uncompressed page tree and cross-check
    against the root `/Pages` node's `/Count`. Chrome's Skia backend writes the page tree uncompressed.
    Returns (pages, rootCount, note); pages is None when nothing countable was found."""
    try:
        data = pdf.read_bytes()
    except OSError as ex:
        return None, None, f"unreadable: {ex}"
    if not data.startswith(b"%PDF"):
        return None, None, "not a PDF (no %PDF header)"
    pages = len(re.findall(rb"/Type\s*/Page\b", data))
    counts = []
    for m in re.finditer(rb"\d+\s+\d+\s+obj\b(.*?)endobj", data, re.S):
        body = m.group(1)
        if re.search(rb"/Type\s*/Pages\b", body):
            c = re.search(rb"/Count\s+(\d+)", body)
            if c:
                counts.append(int(c.group(1)))
    root = max(counts) if counts else None
    if pages == 0 and root is None:
        return None, None, "no page objects visible (object streams?) - the count could not be established"
    if root is not None and root != pages:
        return pages, root, f"page objects {pages} disagree with the root /Count {root}"
    return pages, root, "ok"


def print_pdf(browser: Path, html: Path, pdf: Path, timeout: int = 180) -> tuple[bool, str]:
    profile = Path(tempfile.mkdtemp(prefix="evalreport-browser-"))
    try:
        argv = [str(browser), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                f"--user-data-dir={profile}", f"--print-to-pdf={pdf.resolve()}", html.resolve().as_uri()]
        try:
            r = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return False, f"the browser did not finish within {timeout}s"
        except OSError as ex:
            return False, f"the browser could not be started: {ex}"
        if not pdf.exists() or pdf.stat().st_size == 0:
            tail = (r.stderr or "")[-400:].strip().replace("\n", " | ")
            return False, f"the browser exited {r.returncode} and wrote no PDF: {tail or 'no stderr'}"
        why = relative_pdf_links(pdf, html.resolve().parent)
        return (True, "") if why is None else (False, why)
    finally:
        shutil.rmtree(profile, ignore_errors=True)


def relative_pdf_links(pdf: Path, folder: Path) -> str | None:
    """(Ice r3 report review) The browser prints every relative link of the page as an absolute file:/// address,
    which breaks once the report folder is moved. Each link into `folder` becomes relative to it, and the PDF's
    cross-reference offsets move by what each rewrite changed, so the file reads as before. None when done (or nothing
    to do); the reason when the file has no plain cross-reference table to correct."""
    data = pdf.read_bytes()
    prefix = re.escape(folder.as_uri().rstrip("/").encode() + b"/")
    pat = re.compile(rb"(/URI\s*\()" + prefix + rb"([^()\\]*)(\))", re.IGNORECASE)
    hits = list(pat.finditer(data))
    if not hits:
        return None
    m_ = list(re.finditer(rb"startxref\s+(\d+)\s+%%EOF\s*$", data))
    xref_at = int(m_[-1].group(1)) if m_ else None
    if xref_at is None or data[xref_at:xref_at + 4] != b"xref":
        return "the PDF has no plain cross-reference table, so its links were left as the browser wrote them"
    out, shifts, last, delta = [], [], 0, 0
    for h in hits:
        new = h.group(1) + h.group(2) + h.group(3)
        out += [data[last:h.start()], new]
        delta += len(new) - (h.end() - h.start())
        shifts.append((h.start(), delta))
        last = h.end()
    out.append(data[last:])
    body = b"".join(out)

    def moved(off):
        return off + next((d for at, d in reversed(shifts) if at < off), 0)
    new_xref = moved(xref_at)
    pos = new_xref + 4
    sec = re.compile(rb"\s*(\d+) (\d+)[ \t]*\r?\n")
    parts = [body[:pos]]
    while True:
        s_ = sec.match(body, pos)
        if not s_:
            break
        parts.append(body[pos:s_.end()])
        pos = s_.end()
        for _ in range(int(s_.group(2))):
            ent = body[pos:pos + 20]
            if ent[17:18] == b"n":
                ent = b"%010d" % moved(int(ent[:10])) + ent[10:]
            parts.append(ent)
            pos += 20
    rest = re.sub(rb"startxref\s+\d+(\s+%%EOF\s*)$", lambda mm: b"startxref\n%d" % new_xref + mm.group(1), body[pos:])
    pdf.write_bytes(b"".join(parts) + rest)
    return None


# ------------------------------------------------------------------------------ bundle verification --

def run_verify_bundle(bundle: Path, out: Path) -> tuple[dict | None, int, str]:
    """D1: `evalrun.py verify-bundle DIR` is the authority. It reads the bundle in place and writes its result to
    OUT/verify.json (`--out`), so the bundle is never written and nothing is copied. An earlier OUT/verify.json is
    removed first, so a verify that dies before writing cannot be read as this run's verdict."""
    result = out / "verify.json"
    result.unlink(missing_ok=True)
    try:
        r = subprocess.run([sys.executable, str(EVALRUN), "verify-bundle", str(bundle), "--out", str(result)],
                           capture_output=True, text=True, timeout=900)
    except (OSError, subprocess.SubprocessError) as ex:
        return None, EXIT_REFUSED_INPUT, f"verify-bundle could not run: {ex}"
    verify = load_json(result)
    if not isinstance(verify, dict):
        return None, r.returncode, ("verify-bundle wrote no readable verify.json: " +
                                    (r.stderr or r.stdout or "")[-400:].strip())
    return verify, r.returncode, ""


# -------------------------------------------------------------------------------------- compile --

class Bundle:
    """Read-only view of a schema-2 bundle: manifest, disposition, plan, ledger, registry, receipts."""

    def __init__(self, d: Path):
        self.dir = d
        self.manifest = load_json(d / "manifest.json") or {}
        self.disposition = load_json(d / self.manifest.get("disposition", "disposition.json")) or {}
        self.plan = load_json(d / self.manifest.get("plan", "plan.json")) or {}
        self.ledger = load_json(d / self.manifest.get("ledger", "ledger.json")) or {}
        self.registry = load_json(d / self.manifest.get("registry", "registry/check-registry.json")) or {}
        self.scope = load_json(d / self.manifest["scope"]) if self.manifest.get("scope") else None
        self.checks = {c.get("id"): c for c in (self.registry.get("checks") or []) if isinstance(c, dict)}
        self.families = {f.get("id"): f for f in (self.registry.get("families") or []) if isinstance(f, dict)}
        self.exe_to_check = {}
        for cid, c in self.checks.items():
            exe = (c.get("adapter") or {}).get("exe")
            if exe and exe not in self.exe_to_check:
                self.exe_to_check[exe] = cid
        self.evidence = [e for e in (self.manifest.get("evidence") or []) if isinstance(e, dict)]
        self.by_path = {e["path"]: e for e in self.evidence if e.get("path")}
        self.receipts = {}
        for e in self.evidence:
            if e.get("kind") == "receipt":
                rc = load_json(d / e["path"])
                if isinstance(rc, dict) and rc.get("receiptId"):
                    self.receipts[rc["receiptId"]] = rc
        self.receipt_by_key = {}
        for rid, rc in self.receipts.items():
            self.receipt_by_key[(rc.get("checkId"), rc.get("armId"), rc.get("part"))] = rc
        self._emits = {}

    def emit(self, path: str):
        if path not in self._emits:
            self._emits[path] = load_json(self.dir / path)
        return self._emits[path]

    def check_title(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("title") or cid

    def check_family(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("family") or (cid.split(".")[0] if cid else "")

    def evidence_for(self, check_id: str, arm_id: str, part: str | None) -> list[dict]:
        """Manifest evidence rows recorded by the receipt of (check, arm, part), plus the receipt row."""
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        rows = []
        if rc:
            rid = rc["receiptId"]
            for e in self.evidence:
                if e.get("recordedBy") == rid or ((e.get("provenance") or {}).get("receiptId") == rid):
                    rows.append(e)
            rp = f"receipts/{check_id}__{arm_id}" + (f"__{part}" if part else "") + ".json"
            if rp in self.by_path:
                rows.append(self.by_path[rp])
        return rows

    def output_emit_path(self, check_id: str, arm_id: str, part: str | None) -> str | None:
        """The file a check's rows are read from: an import's `rows` output (copied as `NN-rows.json`), else rows
        derived after measurement from that import's transcripts (a `record-rows` enrichment), else the first JSON
        output the receipt recorded."""
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        if not rc:
            return None
        rid = rc["receiptId"]
        emits = [e["path"] for e in self.evidence if e.get("kind") == "emit" and e.get("recordedBy") == rid
                 and str(e.get("path", "")).endswith(".json")]
        rows = [x for x in emits if x.endswith("-rows.json")]
        if rows:
            return rows[0]
        if part in (None, "arm"):
            for e in self.evidence:
                if (e.get("kind") == "enrichment" and e.get("enrichment") == "record-rows"
                        and e.get("checkId") == check_id and e.get("armId") == arm_id):
                    return e["path"]
        return emits[0] if emits else None

    def arm_output_path(self, check_id: str, arm_id: str) -> str | None:
        """A setup's own result of a check: its `arm` part, or the one run a check with no comparison step records
        without a part."""
        return self.output_emit_path(check_id, arm_id, "arm") or self.output_emit_path(check_id, arm_id, None)

    def enrichment(self) -> list[dict]:
        return [e for e in self.evidence if e.get("kind") == "enrichment"]

    def transcripts(self, check_id: str, arm_id: str):
        """A record import's two MCP transcripts and the plugins of the configuration it consumed: (request path,
        response path, plugins), or None when the check's receipt records no such pair."""
        rc = self.receipt_by_key.get((check_id, arm_id, "arm")) or self.receipt_by_key.get((check_id, arm_id, None))
        if not rc:
            return None
        paths = [e["path"] for e in self.evidence if e.get("recordedBy") == rc["receiptId"] and e.get("path")]
        req = next((x for x in paths if x.endswith("request.jsonl")), None)
        resp = next((x for x in paths if x.endswith("response.jsonl")), None)
        plugs = ((rc.get("consumed") or {}).get("configuration") or {}).get("plugins")
        if not (req and resp and isinstance(plugs, list)):
            return None
        return self.dir / req, self.dir / resp, plugs

    def diff_log_path(self, check_id: str, arm_id: str) -> str | None:
        rc = self.receipt_by_key.get((check_id, arm_id, "diff"))
        if not rc:
            return None
        rid = rc["receiptId"]
        for e in self.evidence:
            if e.get("recordedBy") == rid and str(e.get("path", "")).endswith(".txt"):
                return e["path"]
        return None


DIFF_LINE = re.compile(r"^\s*(NEW|GREW|SHRANK|GONE|SAME|UNCHANGED)\s+(\S+)\s+(.+?)\s{2,}x\s{2,}(.+?)\s{2,}([+-]?\d+)\s+record\(s\):\s*(.*?)\s*$")


def parse_diff_log(text: str) -> list[dict]:
    out = []
    for line in text.splitlines():
        m = DIFF_LINE.match(line)
        if not m:
            continue
        tail = m.group(6)
        nums = {k: int(v) for k, v in re.findall(r"(repaired|reclassified|created|unmeasured)\s+(\d+)", tail)}
        out.append({"change": m.group(1), "kind": m.group(2), "A": m.group(3).strip(), "B": m.group(4).strip(),
                    "delta": int(m.group(5)), "line": line.strip(), **nums})
    return out


def base_has_no_model(names: dict | None, fk) -> bool:
    """A placed object whose base record the names enrichment read with no model (the field absent, ""): nothing is drawn for
    it. Whether it has a body is base_has_no_body's question, by type."""
    if not isinstance(names, dict):
        return False
    base = ((names.get("bases") or {}).get(((names.get("refs") or {}).get(str(fk)) or {}).get("base") or "") or {})
    return bool(base.get("type")) and base.get("model") == ""


def no_model_type(names: dict | None, fk) -> str | None:
    """The base record type of a placed object whose base has no model (base_has_no_model), else None."""
    if not base_has_no_model(names, fk):
        return None
    return str(((names.get("bases") or {}).get(((names.get("refs") or {}).get(str(fk)) or {}).get("base") or "") or {})
               .get("type"))


def base_has_no_body(names: dict | None, fk, types) -> bool:
    """A placed object with no body in game: its base record has no model AND is of a type measured to have none without one
    (skill data reader.prediction.bodilessTypes, the probes' rule DetectorModel.HasNoBody: a light; an activator or a door,
    station AD 2026-10-25). This replaces the round 4 ruling (2026-10-17: any type with no model has no body, decided from the
    record field alone): a CollisionMarker is a static with no model whose box blocks in game (station Q)."""
    if not base_has_no_model(names, fk):
        return False
    return str(((names.get("bases") or {}).get(((names.get("refs") or {}).get(str(fk)) or {}).get("base") or "") or {})
               .get("type")) in set(types or ())


def row_has_no_body(names: dict | None, r: dict, types) -> bool:
    """A geometry row whose object, or any partner object, has no body (see base_has_no_body)."""
    return any(base_has_no_body(names, x, types) for x in [r.get("formKey"), *(r.get("other") or ())] if x)


NAV_LINK_ID = re.compile(r"navmesh ([0-9A-Fa-f]{6}:.+?) in .*? edge-links ([0-9A-Fa-f]{6}:.+?) triangle (\d+)")


def row_identity(row: dict) -> tuple:
    """A comparison row as one thing in both setups: a broken navmesh link by its linking navmesh, target navmesh and
    target triangle (the provider chain and the target's triangle count it is written with differ between setups); any
    other row by its object, its partner objects and its summary."""
    m = NAV_LINK_ID.search(str(row.get("summary") or ""))
    if m:
        return ("link", m.group(1).upper(), m.group(2).upper(), int(m.group(3)))
    return ("row", row.get("formKey"), tuple(row.get("other") or ()), row.get("summary"))


def conflict_rows(conflict: dict, evidence_id: str | None) -> list[dict]:
    rows = []
    # the partner objects a geometry row is measured against (an overlap's other object, a fixture's removed host):
    # with the row's own object they identify the relationship whatever plugins own the two objects
    alternative = conflict.get("Alternative")
    other = (sorted(str(x.get("FormKey")) for x in alternative if isinstance(x, dict) and x.get("FormKey"))
             if isinstance(alternative, list) and conflict.get("Kind") != "LOST-EDIT" else None)
    for n in conflict.get("Necessary") or []:
        if not isinstance(n, dict):
            continue
        rows.append({"formKey": n.get("FormKey"), "label": n.get("Label"), "summary": conflict.get("Summary"),
                     "physical": n.get("Physical"), "physicalDetail": n.get("PhysicalDetail"),
                     "evidenceId": evidence_id,
                     **({"other": other} if other is not None else {}),
                     **({"field": conflict.get("Field"), "values": n.get("Values"), "reads": n.get("Reads")}
                        if conflict.get("Kind") == "LOST-EDIT" else {})})
    if not rows:
        rows.append({"formKey": None, "label": None, "summary": conflict.get("Summary"), "physical": None,
                     "physicalDetail": None, "evidenceId": evidence_id})
    return rows


def _transcript_messages(text: str):
    """(batch marker, message) per JSON line of an MCP import transcript; a batch starts at each `batchMarker` line."""
    batch = None
    for line in text.splitlines():
        if not line.strip():
            continue
        m = json.loads(line)
        if isinstance(m, dict) and "batchMarker" in m:
            batch = m["batchMarker"]
            continue
        for x in (m if isinstance(m, list) else [m]):
            if isinstance(x, dict):
                yield batch, x


def missing_assets(b: "Bundle", check_id: str, arm_id: str) -> list[dict] | None:
    """The asset paths a missing-asset check's import measured absent on one setup, each with the plugins whose records
    name it (their `Model.File`), read from the import's own transcripts: every `housecarl_asset_status` answer's
    results with `exists` false, and every `housecarl_records` answer's Model.File values with the plugin that carries
    the record. None when the receipt records no transcript pair or it cannot be read."""
    rc = b.receipt_by_key.get((check_id, arm_id, None)) or b.receipt_by_key.get((check_id, arm_id, "arm"))
    if not rc:
        return None
    paths = [e["path"] for e in b.evidence_for(check_id, arm_id, rc.get("part")) if e.get("kind") == "emit"]
    req = next((x for x in paths if x.endswith("request.jsonl")), None)
    resp = next((x for x in paths if x.endswith("response.jsonl")), None)
    if not req or not resp:
        return None
    try:
        calls = {(bt, m.get("id")): (m.get("params") or {}).get("name")
                 for bt, m in _transcript_messages((b.dir / req).read_text(encoding="utf-8"))
                 if m.get("method") == "tools/call"}
        owners, absent, records_of = collections.defaultdict(list), [], collections.defaultdict(list)
        for bt, m in _transcript_messages((b.dir / resp).read_text(encoding="utf-8")):
            name = calls.get((bt, m.get("id")))
            if name not in ("housecarl_records", "housecarl_asset_status"):
                continue
            text = "".join(c.get("text", "") for c in ((m.get("result") or {}).get("content") or []) if isinstance(c, dict))
            doc = json.loads(text) if text.strip().startswith("{") else {}
            if name == "housecarl_asset_status":
                absent += [str(x.get("path")) for x in doc.get("results") or []
                           if isinstance(x, dict) and x.get("exists") is False and x.get("path")]
                continue
            # (ninth review) a query answers in "matches", a walk in "records": the same record entries
            for rec in (doc.get("matches") or []) + (doc.get("records") or []):
                for fl in (rec.get("fields") or []) if isinstance(rec, dict) else []:
                    if isinstance(fl, dict) and fl.get("path") == "Model.File" and fl.get("value"):
                        v = str(fl["value"]).replace("\\", "/").lstrip("/")
                        v = (v if v.lower().startswith("meshes/") else "meshes/" + v).lower()
                        if rec.get("source") and rec["source"] not in owners[v]:
                            owners[v].append(rec["source"])
                        if rec.get("formid") and rec["formid"] not in records_of[v]:
                            records_of[v].append(str(rec["formid"]))
    except (OSError, ValueError):
        return None
    return [{"path": p_, "namedBy": owners.get(p_.replace("\\", "/").lower(), []),
             "records": records_of.get(p_.replace("\\", "/").lower(), [])} for p_ in dict.fromkeys(absent)]


def asset_view_read_whole(b: "Bundle", check_id: str, arm_id: str) -> bool:
    """(eighth review, Pilgrim 27) True where every `housecarl_asset_status` answer of a missing-asset import reports its
    archive reads, with no archive that failed to read (none omitted either): the asset view searched every enabled
    mod's folders and archives. False where any answer reports a failure or does not report its archive reads."""
    rc = b.receipt_by_key.get((check_id, arm_id, None)) or b.receipt_by_key.get((check_id, arm_id, "arm"))
    if not rc:
        return False
    paths = [e["path"] for e in b.evidence_for(check_id, arm_id, rc.get("part")) if e.get("kind") == "emit"]
    req = next((x for x in paths if x.endswith("request.jsonl")), None)
    resp = next((x for x in paths if x.endswith("response.jsonl")), None)
    if not req or not resp:
        return False
    try:
        calls = {(bt, m.get("id")): (m.get("params") or {}).get("name")
                 for bt, m in _transcript_messages((b.dir / req).read_text(encoding="utf-8"))
                 if m.get("method") == "tools/call"}
        seen = False
        for bt, m in _transcript_messages((b.dir / resp).read_text(encoding="utf-8")):
            if calls.get((bt, m.get("id"))) != "housecarl_asset_status":
                continue
            text = "".join(c.get("text", "") for c in ((m.get("result") or {}).get("content") or []) if isinstance(c, dict))
            doc = json.loads(text) if text.strip().startswith("{") else {}
            if not isinstance(doc.get("bsa_failures"), list) or doc["bsa_failures"] or int(doc.get("bsa_failures_omitted") or 0):
                return False
            seen = True
        return seen
    except (OSError, ValueError):
        return False


def infer_clobber_direction(finding: dict, plugin_order: list[str]) -> dict | None:
    c = finding.get("counts") or {}
    if c.get("groupKind") != "CLOBBERED" or not c.get("A") or not c.get("B"):
        return None
    a_name, b_name = str(c["A"]), str(c["B"])
    rows = finding.get("allRows") or finding.get("rows") or []
    summaries = [str(r.get("summary") or "") for r in rows]
    details = " ".join(str(r.get("physicalDetail") or "") for r in rows)
    direction_pattern = re.compile(r"re-cut by\s+" + re.escape(a_name) + r"\s+and then re-cut again by\s+"
                                   + re.escape(b_name) + r"(?:\s|,)", re.I)
    stated = bool(summaries) and all(direction_pattern.search(s) for s in summaries)
    named_winners = {m.group(1) for m in re.finditer(r"winner's \(([^)]+)\)", details, re.I)}
    winner_name = b_name if not named_winners or {x.lower() for x in named_winners} == {b_name.lower()} else None
    folded = {str(n).lower(): i for i, n in enumerate(plugin_order)}
    ordered = (winner_name is not None and a_name.lower() in folded and winner_name.lower() in folded
               and folded[a_name.lower()] < folded[winner_name.lower()])
    if not stated or not ordered:
        return None
    return {"overwrittenPlugin": a_name, "winningPlugin": winner_name, "order": "earlier-to-later",
            "basis": "conflict evidence and recorded plugin order"}


def fill(template, **slots) -> str:
    """A skill template filled with the reel's one filler (its possessive rule; reel_plan.fill)."""
    import reel_plan
    return reel_plan.fill(template, **slots)


def bind(template: str, values: dict) -> str:
    class Safe(dict):
        def __missing__(self, k):
            return "not supplied"
    import reel_plan
    vals = {k: (fmt_n(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else
                ("not supplied" if v is None else v)) for k, v in values.items()}
    # the reel's possessive rule: "{plugin}'s" over a name ending in s says "NPCs'", never "NPCs's"
    template = reel_plan.POSSESSIVE_SLOT.sub(lambda m: m.group(0)[:-1] if isinstance(vals.get(m.group(1)), str) and str(
        vals[m.group(1)])[-1:].casefold() == "s" else m.group(0), str(template))
    return template.format_map(Safe(vals))


IMPORT_ORIGINS = ("fresh", "reused")
PREREQUISITE_STATUS = re.compile(r"^blocked\(prerequisite:([^)]+)\)$")


def import_narrowed_facts(rows: list, pr: dict, title_of) -> list:
    """(fourth review, Missives S2) A record import whose own tool note says it left a field out of the comparison
    (skill data `importNarrowedPattern`): one coverage fact per check and fields, so the field is not taken as judged."""
    w_ = ((pr.get("reader") or {}).get("prediction") or {})
    pat_ = w_.get("importNarrowedPattern")
    if not pat_ or not w_.get("importNarrowedFact"):
        return []
    out_ = collections.OrderedDict()
    for r_ in rows or []:
        name_ = str(((r_.get("tool") or {}) if isinstance(r_.get("tool"), dict) else {}).get("name") or "")
        # (fifth review, Missives S1) every narrowing the note names, in any of its wordings (skill data)
        for m_ in re.finditer(pat_, name_):
            n_ = re.search(r"over the (\d+) ", name_)
            key_ = (r_.get("checkId"), m_.group("fields").strip())
            # (sixth review, Missives S1) the record type the note narrowed (its signature just before), so a cell's
            # field is never taken for the worldspace field of the same name
            sig_ = re.findall(r"\b([A-Z][A-Z_]{3})\b", name_[:m_.start()])
            typ_ = (w_.get("importNarrowedTypes") or {}).get(sig_[-1]) if sig_ else None
            out_.setdefault(key_, bind(w_["importNarrowedFact"], {
                "title": title_of(r_.get("checkId")), "fields": f"{typ_} {key_[1]}" if typ_ else key_[1],
                "records": f" on the {n_.group(1)} records it read" if n_ else ""}))
    return list(out_.values())


def imported_evidence(b: Bundle) -> dict:
    """Where each credited MCP import's evidence came from.

    WHICH imports are credited: the disposition's `importedEvidence` list when the bundle carries one; a bundle
    finalized before that list existed has none, and then an accepted receipt with an `imported` block whose
    (check, arm) is a completed result in the disposition counts. WHAT the origin is: the receipt's own
    `imported.origin` when the bundle carries the receipt (the controller's recovery authority), else the
    disposition row's. Anything but fresh or reused - including a receipt written before an origin had to be
    declared - is `unknown`, never fresh. `reused` is the import's declaration that the evidence was captured for
    another evaluation; the ledger's own `reused` status (this run re-using its receipt on resume) is not read."""
    by_rid = {rid: rc for rid, rc in b.receipts.items() if isinstance(rc.get("imported"), dict)}
    ev = b.disposition.get("evaluation") or {}
    listed = b.disposition.get("importedEvidence")
    if isinstance(listed, list):
        source = "disposition"
        credited = [(r.get("checkId"), r.get("armId"), r.get("receiptId"), r) for r in listed if isinstance(r, dict)]
    else:
        source = "receipts"
        completed = {(r.get("checkId"), r.get("armId")) for key in ("findings", "completedNullChecks", "completedCatalogues")
                     for r in ev.get(key) or [] if isinstance(r, dict)}
        credited = sorted(((rc.get("checkId"), rc.get("armId"), rid, {}) for rid, rc in by_rid.items()
                           if (rc.get("validation") or {}).get("accepted") and (rc.get("checkId"), rc.get("armId")) in completed),
                          key=lambda x: (str(x[0]), str(x[1])))
    rows = []
    for check_id, arm_id, rid, drow in credited:
        imp = (by_rid.get(rid) or {}).get("imported")
        declared = imp.get("origin") if isinstance(imp, dict) else drow.get("origin")
        origin = declared if declared in IMPORT_ORIGINS else "unknown"
        imp = imp if isinstance(imp, dict) else {}
        row = {"checkId": check_id, "armId": arm_id, "receiptId": rid, "origin": origin,
               "capturedUtc": imp.get("capturedUtc") or drow.get("capturedUtc"),
               "tool": imp.get("tool"), "sourceSha256": imp.get("sourceSha256"),
               "receiptPath": f"receipts/{check_id}__{arm_id}.json"}
        if origin == "fresh":
            row["originCheck"] = imp.get("originCheck")
        elif origin == "reused":
            row["reusedFrom"] = imp.get("reusedFrom") or drow.get("reusedFrom")
            row["reusedFromVerified"] = bool(imp.get("reusedFromVerified", drow.get("reusedFromVerified")))
            row["reusedFromNote"] = imp.get("reusedFromNote")
            within = imp.get("reusedWithin") or drow.get("reusedWithin")
            row["reusedWithin"] = within if within in ("this-evaluation", "another-evaluation") else "unstated"
            # What the capture says about the file it was measured against, in the same three states the
            # controller records (bundle contract 4.3, source rule): `other-file` names another candidate,
            # `unstated` means the capture names none at all, and no key means the names agree and there is
            # nothing extra to tell the reader. `unstated` is a limitation in its own right and must survive to
            # the page - filtering on a truthy filename dropped it silently (Piranha's handback, 2026-09-17).
            # Read from the identity limit the controller wrote, never re-derived here. The bundle's own
            # receipt is the authority; the disposition row is the fallback when this reader lacks the receipt.
            lim = next((x for x
                        in ((by_rid.get(rid) or {}).get("validation") or {}).get("identityLimits") or []
                        if isinstance(x, dict) and x.get("armCandidate")), None)
            if lim is not None:
                row["sourceDisclosure"] = "other-file" if lim.get("capturedAgainst") else "unstated"
                row["capturedAgainst"] = lim.get("capturedAgainst") or None
                row["armCandidate"] = lim.get("armCandidate")
            elif drow.get("sourceDisclosure") in ("other-file", "unstated"):
                row["sourceDisclosure"] = drow["sourceDisclosure"]
                row["capturedAgainst"] = drow.get("capturedAgainst") or None
                row["armCandidate"] = drow.get("armCandidate")
        rows.append(row)
    return {"source": source if rows or isinstance(listed, list) else None, "rows": rows}


def plain_population(population, words: dict) -> str:
    """A probe's population name in the skill's words (`populationWords`); a name not listed is shown as written."""
    return str((words.get("populationWords") or {}).get(str(population)) or population)


def is_base_game(name: str, words: dict) -> bool:
    """A Bethesda file: the listed base-game files, or a Creation Club plugin (`cc*.esm/esp/esl`)."""
    n = str(name or "").lower()
    listed = {str(x).lower() for x in words.get("baseGamePlugins") or []}
    prefix = str(words.get("baseGamePrefix") or "").lower()
    return n in listed or bool(prefix and n.startswith(prefix) and n.endswith((".esm", ".esp", ".esl")))


_PLUGIN_SHA = {}


def setup_version(plugins: list, fk: str, reads: list | None = None):
    """(plugin, its placementFull) of the last plugin of a setup holding the placed record `fk`, read from every plugin
    file of that setup from the record's origin on, each only where its bytes are the recorded ones (sha256); (None,
    None) where none holds it; None where a file of the setup is missing, differs or does not read. Each read is
    appended to `reads` (pluginReads)."""
    origin_, started_, last_ = str(fk).split(":", 1)[-1].lower(), False, (None, None)
    for x in plugins or []:
        started_ = started_ or str(x.get("name")).lower() == origin_
        if not started_:
            continue
        path_ = str(x.get("path") or "")
        if not path_ or not Path(path_).is_file():
            return None
        if path_ not in _PLUGIN_SHA:
            _PLUGIN_SHA[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
        if _PLUGIN_SHA[path_] != str(x.get("sha256") or "").lower():
            return None
        try:
            got_ = pluginread.placements_full(path_, [str(fk)])["records"].get(str(fk))
        except pluginread.PluginReadError:
            return None
        if reads is not None:
            reads.append({"plugin": x.get("name"), "sha256": _PLUGIN_SHA[path_], "record": str(fk), "read": "placementFull",
                          "found": got_ is not None})
        if got_ is not None:
            last_ = (x.get("name"), got_)
    return last_


def setup_placed_users(plugins: list, base_keys: list, reads: list | None = None):
    """(ninth review, BPT 33) {reference: (plugin, its placementFull)}: the last version of every placed reference a
    plugin of the setup places with one of `base_keys` as its base, where that last version still has such a base. Read
    from every plugin file of the setup that is or masters a base's plugin, each only where its bytes are the recorded
    ones (sha256); None where a file of the setup is missing, differs or does not read."""
    origins_ = {str(b_).split(":", 1)[-1].lower() for b_ in base_keys}
    bases_low_ = {str(b_).lower() for b_ in base_keys}
    refs_ = set()
    for x in plugins or []:
        path_ = str(x.get("path") or "")
        if not path_ or not Path(path_).is_file():
            return None
        if path_ not in _PLUGIN_SHA:
            _PLUGIN_SHA[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
        if _PLUGIN_SHA[path_] != str(x.get("sha256") or "").lower():
            return None
        try:
            masters_, _ = pluginread._index(Path(path_), pluginread.PLACED_TYPES)
            if not ({str(m_).lower() for m_ in masters_} | {str(x.get("name")).lower()}) & origins_:
                continue
            got_ = pluginread.placed_of_bases(path_, list(base_keys))
        except pluginread.PluginReadError:
            return None
        if reads is not None:
            reads.append({"plugin": x.get("name"), "sha256": _PLUGIN_SHA[path_], "record": ",".join(base_keys),
                          "read": "placedOfBases", "found": sorted(got_["refs"])})
        refs_ |= set(got_["refs"])
    out_ = {}
    for r_ in sorted(refs_):
        v_ = setup_version(plugins, r_, reads)
        if v_ is None:
            return None
        if v_[0] is not None and str((v_[1] or {}).get("base") or "").lower() in bases_low_:
            out_[r_] = v_
    return out_


def placement_off_for_good(v: dict, words: dict) -> bool:
    """A placementFull version sets its object off for good: moved below the world (disabledValuePatterns), or enabled
    opposite to a parent that is always enabled (the player); the same rule as the record chains' provider_disables."""
    if not v:
        return False
    pos_ = v.get("position") or []
    text_ = "Placement.Position=" + ",".join(str(float(x)) for x in pos_)
    if any(re.search(p_, text_) for p_ in words.get("disabledValuePatterns") or []):
        return True
    ep_ = v.get("enableParent")
    always_ = {str(x).lower() for x in (words.get("disabledRecord") or {}).get("alwaysEnabledParents") or []}
    return bool(ep_ and str(ep_[0]).lower() in always_ and ep_[1])


def support_still_removed(plugins_in: list, plugins_ref: list, fk: str, others, words: dict, reads=None) -> bool | None:
    """(sixth review, Winterhold r6 S1) In the setup `plugins_in`, every support of the object (`others`) is off for good
    at its last version, and the object's last version is on (not off for good, not initially disabled, not deleted)
    and within skill data supportUnmovedUnits of where it stands in the setup `plugins_ref`: the object still has no
    support there, whatever that setup's check lists. None where a plugin file of either setup is not read."""
    if not others:
        return False
    o_in, o_ref = setup_version(plugins_in, fk, reads), setup_version(plugins_ref, fk, reads)
    if o_in is None or o_ref is None:
        return None
    v_in, v_ref = o_in[1], o_ref[1]
    if not v_in or not v_ref or not v_in.get("position") or not v_ref.get("position"):
        return False
    if placement_off_for_good(v_in, words) or v_in.get("initiallyDisabled") or v_in.get("deleted"):
        return False
    if math.dist(v_in["position"], v_ref["position"]) > float(words.get("supportUnmovedUnits") or 1.0):
        return False
    for s_ in others:
        sv_ = setup_version(plugins_in, str(s_), reads)
        if sv_ is None:
            return None
        if not placement_off_for_good(sv_[1], words):
            return False
    return True


def plugin_masters(b: "Bundle") -> dict | None:
    """Each plugin's own master list (lower case), read from the configuration the probes record in their emits
    (`Configuration.Plugins[].Masters`): a fact of the plugin file, the same in every setup that loads it. Per setup the
    smallest emit that records the lists is read. None when no emit records any."""
    out, seen_arms = {}, set()
    # a reverse emit records the configuration it was measured against, not its own setup's
    emits = [e for e in b.evidence if e.get("kind") == "emit" and str(e.get("path", "")).endswith(".json")
             and ".reverse." not in str(e.get("path"))]

    def size(e):
        try:
            return (b.dir / e["path"]).stat().st_size
        except OSError:
            return float("inf")
    for e in sorted(emits, key=size):
        if e.get("armId") in seen_arms:
            continue
        em = load_json(b.dir / e["path"])
        cfg = em.get("Configuration") if isinstance(em, dict) else None
        plugs = [x for x in (cfg or {}).get("Plugins") or [] if isinstance(x, dict) and isinstance(x.get("Masters"), list)]
        if not plugs:
            continue
        seen_arms.add(e.get("armId"))
        for x in plugs:
            out.setdefault(str(x.get("Name")).lower(), {str(m).lower() for m in x["Masters"]})
    # a plugin no emit records: its TES4 header, read only when the file is the bytes the plan recorded
    import evalpipeline
    import struct
    for a_ in b.plan.get("arms") or []:
        for q_ in (a_.get("requested") or {}).get("plugins") or []:
            nm_ = str((q_ or {}).get("name") or "").lower() if isinstance(q_, dict) else ""
            if not nm_ or nm_ in out or not q_.get("path") or not q_.get("sha256") or not Path(str(q_["path"])).is_file():
                continue
            try:
                if sha256_file(Path(str(q_["path"]))) != str(q_["sha256"]).lower():
                    continue
                out[nm_] = {str(m_).lower() for m_ in evalpipeline.plugin_header(q_["path"])["masters"]}
            except (OSError, ValueError, struct.error, evalpipeline.Refusal):
                continue
    return out or None


def master_of(masters: dict | None, a, b) -> tuple[str, str] | None:
    """(dependent, master) when one of the two plugins lists the other as a master, by the plugins' own master lists."""
    if not masters:
        return None
    al, bl = str(a or "").lower(), str(b or "").lower()
    if bl in masters.get(al, ()):
        return a, b
    if al in masters.get(bl, ()):
        return b, a
    return None


def field_name(row: dict) -> str:
    """A lost edit's field as its value names it ("Grid.Flags=8" is Grid.Flags), else the row's field."""
    v = (row.get("values") or {}).get("lost")
    first = (v[0] if isinstance(v, list) and v else v) or ""
    m = re.match(r"^([A-Za-z][\w.]*)=", str(first))
    return m.group(1) if m else str(row.get("field"))


# lost-edit rows whose values show no change the game sees, or whose subject another check judges (window 2, cw)
NOISE_ADMISSIONS = ("navmeshCheck", "doorLinkNavmesh", "noWater", "winnerListDrop", "orderOnly", "carriedList", "listKept",
                    "unusedBits", "sameAsRead", "winnerOwnChange", "storageFlag", "sameFlagsView")
FIELD_SCOPE_FILE = (Path(__file__).resolve().parent.parent / ".claude" / "skills" / "world-edit-compatibility"
                    / "references" / "record-field-scope.json")
_field_masks = []


def field_masks() -> dict:
    """{record type: {field path: mask}}: the bytes of an integer field the game reads (record-field-scope.json)."""
    if not _field_masks:
        try:
            data = json.loads(FIELD_SCOPE_FILE.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            data = {}
        _field_masks.append({t: {k: int(m) for k, m in (v or {}).items() if not k.startswith("_")}
                             for t, v in (data.get("integerMasks") or {}).items() if isinstance(v, dict)})
    return _field_masks[0]


_record_flags = []


def record_flags_rule() -> dict:
    """The record-flags table (record-field-scope.json `recordFlags`): the base field, its per-type views, the bits
    by name and the mask of storage-only bits. Empty where the skill data has none."""
    if not _record_flags:
        try:
            rf = json.loads(FIELD_SCOPE_FILE.read_text(encoding="utf-8")).get("recordFlags") or {}
        except (OSError, ValueError):
            rf = {}
        bits = {str(k): int(v) for k, v in (rf.get("bits") or {}).items()}
        _record_flags.append({"field": rf.get("field"), "views": set(rf.get("sameBitsAs") or []), "bits": bits,
                              "storage": sum(bits[n] for n in rf.get("storageOnly") or [] if n in bits)})
    return _record_flags[0]


def record_flags_value(field: str, v):
    """A record-flags value as an int: a number, or (on the base field) comma-separated names the table knows; None
    otherwise (a per-type view's names are not in the table)."""
    rule = record_flags_rule()
    s = str((v[0] if isinstance(v, list) and len(v) == 1 else v) if v is not None else "").strip()
    if re.fullmatch(r"-?\d+", s):
        return int(s) & 0xFFFFFFFF
    if field != rule["field"] or not s:
        return None
    total = 0
    for part in [x.strip() for x in s.split(",") if x.strip()]:
        if part not in rule["bits"]:
            return None
        total |= rule["bits"][part]
    return total


def storage_flag_only(field: str, lost, other) -> bool:
    """A record-flags row whose two values differ only in storage bits (the compressed flag): no change in game."""
    rule = record_flags_rule()
    if not rule["field"] or (field != rule["field"] and field not in rule["views"]):
        return False
    a, b = record_flags_value(field, lost), record_flags_value(field, other)
    return a is not None and b is not None and (a & ~rule["storage"]) == (b & ~rule["storage"])


LEAF_VALUE = re.compile(r"^\s*([A-Za-z][\w.]*)\s*=\s*(.*?)\s*$")


def value_leaves(v, top: str) -> dict | None:
    """A recorded lost-edit value as {field path: value} ("Grid.Flags=8", or "Flags=8; Point=1,2" under the field), else
    None (a summary such as a list comparison is not a value)."""
    out = {}
    for item in (v if isinstance(v, list) else [v]):
        for part in str(item).split("; "):
            m = LEAF_VALUE.match(part)
            if not m:
                return None
            k = m.group(1)
            out[k if k == top or k.startswith(top + ".") else f"{top}.{k}"] = m.group(2)
    return out or None


def masked_same(lost, overridden, rtype: str, top: str) -> bool:
    """The losing value differs from the one it overrides only in bytes of the field the game does not read."""
    masks = field_masks().get(str(rtype)) or {}
    a, b = value_leaves(lost, top), value_leaves(overridden, top)
    if not a or not b or set(a) != set(b) or not any(k in masks for k in a):
        return False
    for k in a:
        if k in masks:
            try:
                if (int(a[k]) & masks[k]) != (int(b[k]) & masks[k]):
                    return False
            except ValueError:
                return False
        elif a[k] != b[k]:
            return False
    return True


def moved_plugins(before: list, after: list, prefer_moved=()) -> list:
    """The plugins whose place differs between two orders of the same plugins, each with the plugins it moved past:
    the plugins outside the longest run kept in the same relative order (among equally long runs, the one that moves
    the plugins of `prefer_moved`, the evaluated mods). [(plugin, "after" | "before", [plugins it now loads after or
    before, in the new order])]."""
    a, b = [str(x) for x in before], [str(x) for x in after]
    pref = {str(x).lower() for x in prefer_moved}
    n, m = len(a), len(b)
    big = 2 * (n + 1)
    w = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            w[i][j] = max(w[i + 1][j], w[i][j + 1],
                          (w[i + 1][j + 1] + big - (1 if a[i].lower() in pref else 0)) if a[i] == b[j] else -1)
    kept, i, j = set(), 0, 0
    while i < n and j < m:
        if a[i] == b[j] and w[i][j] == w[i + 1][j + 1] + big - (1 if a[i].lower() in pref else 0):
            kept.add(a[i])
            i, j = i + 1, j + 1
        elif w[i + 1][j] >= w[i][j + 1]:
            i += 1
        else:
            j += 1
    pa, pb = {x: k for k, x in enumerate(a)}, {x: k for k, x in enumerate(b)}
    out = []
    for x in b:
        if x in kept or x not in pa:
            continue
        past = [y for y in b if y != x and y in pa and (pa[y] < pa[x]) != (pb[y] < pb[x])]
        if past:
            out.append((x, "after" if pb[past[0]] < pb[x] else "before", past))
    return out


def shown_value(v, n: int = 60) -> str:
    """A field's value as a sentence quotes it: list entries joined, cut at n characters."""
    t = "; ".join(str(x) for x in v) if isinstance(v, list) else str(v)
    t = re.sub(r"\s+", " ", t).strip()
    if len(t) <= n:
        return t
    # (item 1) never cut inside a form key: a cut-off key names no plugin, so no sentence could say its record
    cut = n - 1
    starts = [m.start() for m in re.finditer(r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{6}:", t) if m.start() < cut]
    if starts and cut - starts[-1] < 64 and (t.find(".es", starts[-1]) < 0 or t.find(".es", starts[-1]) + 4 > cut):
        cut = starts[-1]
    return t[:cut].rstrip() + "\u2026"


def record_type_of(summary) -> str:
    """The record type a record-lane row's words open with ("Cell X: Grid - ...")."""
    return str(summary or "").split(" ", 1)[0]


def is_geometry_check(check_id, words: dict) -> bool:
    """A check whose rows are objects in the world (they intersect, lose their support, hang): a clash there is physical
    and shows in game whoever built on whom, so the own-master rule, which is about record values, never applies."""
    return str(check_id or "").startswith("geometry.") or str(check_id) in set(words.get("bodyChecks") or [])


class InputRefused(Exception):
    """An input the compiler will not use, with the one-sentence reason; `plan` exits 2 with it."""


def _pair_rows_paths(b: "Bundle", arm_id: str, variant: bool = False) -> list:
    """The files the pair's record lost edits on one setup are read from: the setup's record emits, or (variant) its
    order-variant enrichment files."""
    if variant:
        return [e["path"] for e in b.evidence if e.get("kind") == "enrichment" and e.get("enrichment") == "order-variant"
                and e.get("armId") == arm_id]
    paths = []
    for cid in sorted({str(e.get("checkId")) for e in b.evidence if e.get("armId") == arm_id
                       and str(e.get("checkId") or "").startswith("records.")}):
        pth = b.output_emit_path(cid, arm_id, "arm") or b.output_emit_path(cid, arm_id, None)
        if pth:
            paths.append(pth)
    return paths


def _pair_lost_keys(b: "Bundle", arm_id: str, pair: list, variant: bool = False) -> tuple[set, dict]:
    """The pair's record lost edits on one setup: {(FormKey, field)} and {losing plugin: fields}. variant: the
    order-variant enrichment's rows for that setup (its reversed order) instead of the setup's own rows."""
    low = {str(x).lower() for x in pair}
    keys, by_loser = set(), collections.Counter()
    for pth in _pair_rows_paths(b, arm_id, variant):
        doc = b.emit(pth)
        for r in (doc.get("Conflicts") if isinstance(doc, dict) else None) or []:
            if r.get("Kind") != "LOST-EDIT" or {str(r.get("A")).lower(), str(r.get("B")).lower()} != low:
                continue
            for n in r.get("Necessary") or []:
                k = (str(n.get("FormKey")), str(r.get("Field")))
                if k not in keys:
                    keys.add(k)
                    by_loser[str(r.get("A"))] += 1
    return keys, dict(by_loser)


def load_order_fact(ctx: dict, p_arm: str, patched_arm, pair: list, words: dict) -> dict | None:
    """Item 18 (roadmap 4.24 phase 3): whether the evaluated pair's order is a choice, which orders were weighed, the
    record fields that change hands between them (the union of the pair's lost edits over the measured order and the
    bundle's order-variant rows), and, with a declared patch, whether the order still matters on the patched setup
    (its own reversed rows). Header facts are the bundle's recorded ones (bundleenrich.pair_headers). The advice never
    ranks the two orders."""
    import bundleenrich
    if len(pair) != 2:
        return None
    main_b, sib = ctx.get("mainBundle"), (ctx.get("sibling") or {}).get("bundle")
    if main_b is None:
        return None
    w = words.get("loadOrderWords") or {}
    heads = bundleenrich.pair_headers(main_b.dir, main_b.manifest, main_b.plan, pair)
    if len(heads) < 2 and sib is not None:
        heads = {**bundleenrich.pair_headers(sib.dir, sib.manifest, sib.plan, pair), **heads}
    ch = bundleenrich.order_choice(pair, heads)
    arm = next((a for a in main_b.plan.get("arms") or [] if a.get("armId") == p_arm), {}) or {}
    names = [str(x.get("name")) for x in (arm.get("requested") or {}).get("plugins") or []]
    low = [n.lower() for n in names]
    if all(x.lower() in low for x in pair):
        first, second = sorted(pair, key=lambda x: low.index(x.lower()))
    else:
        first, second = pair
    fact = {**ch, "measuredOrder": [first, second], "ordersEvaluated": [f"{first} before {second}"],
            "bothWeighed": False, "changeHands": None, "withPatch": None, "patchCarries": None}
    # the reversed setup: this bundle's variant of the setup without the patch, else the sibling's of the same setup
    src = None
    if any(e.get("enrichment") == "order-variant" and e.get("armId") == p_arm for e in main_b.evidence):
        src = (main_b, p_arm)
    elif sib is not None:
        for a in sib.plan.get("arms") or []:
            if [str(x.get("name")) for x in (a.get("requested") or {}).get("plugins") or []] == names and any(
                    e.get("enrichment") == "order-variant" and e.get("armId") == a.get("armId") for e in sib.evidence):
                src = (sib, a.get("armId"))
                break
    if ch["forced"] is False and src is not None:
        mk, m_lost = _pair_lost_keys(src[0], src[1], pair)
        rk, r_lost = _pair_lost_keys(src[0], src[1], pair, variant=True)
        u = mk | rk
        fact.update({"bothWeighed": True, "ordersEvaluated": [f"{first} before {second}", f"{second} before {first}"],
                     "changeHands": {"fields": len(u), "records": len({k[0] for k in u}),
                                     "lostByMeasuredOrder": m_lost, "lostByReversed": r_lost,
                                     # (item 18 review) every file the count was read from, both orders
                                     "rowsPaths": (_pair_rows_paths(src[0], src[1])
                                                   + _pair_rows_paths(src[0], src[1], variant=True))}})
    # session 1's record-level split, for the load-order table: of the fields that change hands, those on records the
    # declared patch has a version of (read from the patched setup's own record transcripts)
    patches_ = {str(x).lower() for x in ctx.get("declared") or []}
    if fact["changeHands"] is not None and patches_ and patched_arm:
        import recordrows as RR_
        scope_ = RR_.load_scope()
        carried = set()
        for rc_, _, req_, resp_, cfg_ in bundleenrich._record_imports(main_b.dir, main_b.manifest):
            if rc_.get("armId") != patched_arm:
                continue
            for fk_, c_ in RR_.read_chains(req_, resp_, cfg_.get("plugins") or [], scope_).items():
                if any(str(p_).lower() in patches_ for p_ in c_.get("providers") or []):
                    carried.add(str(fk_).lower())
        keys_ = (_pair_lost_keys(src[0], src[1], pair)[0] | _pair_lost_keys(src[0], src[1], pair, variant=True)[0])
        n_ = sum(1 for k in keys_ if k[0].lower() in carried)
        fact["patchCarries"] = {"fields": n_, "notCarried": len(keys_) - n_}
    if ch["forced"] is False and patched_arm and any(e.get("enrichment") == "order-variant"
                                                     and e.get("armId") == patched_arm for e in main_b.evidence):
        mk, _ = _pair_lost_keys(main_b, patched_arm, pair)
        rk, _ = _pair_lost_keys(main_b, patched_arm, pair, variant=True)
        fact["withPatch"] = {"changeHands": len(mk | rk), "orderMatters": bool(mk | rk)}
    other = second if fact.get("loadsFirst") == first else first

    def lost_words(d_):
        return "; ".join(bind(w.get("lostPart") or "{count} of {plugin}'s are lost", {"count": fmt_n(n), "plugin": x})
                         for x, n in d_.items()) or (w.get("lostNone") or "nothing between them is lost")
    if ch["forced"] is None:
        parts = [bind(w.get("unknown") or "Whether the order of these two mods is a choice was not established: "
                      "{missing}.", {"missing": "; ".join(ch.get("missing") or [])})]
    elif ch["forced"]:
        key = "forcedEsm" if ch["rule"] == "ESM flag" else "forcedMaster"
        parts = [bind(w.get(key) or "{first} loads first: the order of these two mods is not a choice.",
                      {"first": fact["loadsFirst"], "other": other})]
    elif fact["changeHands"] is None:
        parts = [bind(w.get("freeOne") or "Neither mod has to load first. Only the tested order ({first} before "
                      "{second}) was measured; the other order was not checked.", {"first": first, "second": second})]
    else:
        c = fact["changeHands"]
        parts = [bind(w.get("freeBoth") or "Neither mod has to load first, and both orders were weighed: {fields} "
                      "record fields on {records} records change hands between them. With {first} before {second}, "
                      "{measured}; with {second} before {first}, {reversed}. Which order to choose depends on which of "
                      "these changes matters more to you, not on how many there are.",
                      {"first": first, "second": second, "fields": fmt_n(c["fields"]), "records": fmt_n(c["records"]),
                       "measured": lost_words(c["lostByMeasuredOrder"]), "reversed": lost_words(c["lostByReversed"])})]
    if fact["withPatch"] is not None:
        wp = fact["withPatch"]
        parts.append(bind(w.get("withPatchMatters") or "With the patch installed, {count} record fields still change "
                          "hands between the two orders.", {"count": fmt_n(wp["changeHands"])}) if wp["orderMatters"]
                     else (w.get("withPatchNot") or "With the patch installed, the order no longer matters for these "
                           "fields: the same values win in either order."))
    fact["sentence"] = " ".join(parts)
    return fact


def with_geometry(lo: dict | None, geo: dict | None) -> dict | None:
    """The order fact with item 10's geometry part: its fact under `geometry` and its sentence after the record one."""
    if geo is None:
        return lo
    if lo is None:
        return {"geometry": geo, "sentence": geo["sentence"]}
    return {**lo, "geometry": geo, "sentence": (str(lo.get("sentence") or "") + " " + geo["sentence"]).strip()}


#: the geometry probes ordersweep runs in both orders (item 10), in the order their lines are said
ORDER_GEOMETRY_PROBES = ("contactprobe", "overlapprobe", "navownprobe")


def order_geometry(ctx: dict, p_arm: str, words: dict) -> tuple[list, dict | None]:
    """Item 10: placed-object geometry in both orders of a free pair, from the bundle's order-geometry enrichment. A
    row holding in one order and gone in the other is a load-order risk: one line per probe and direction. A row in
    both orders is not; a row either order could not measure is counted as not judged. The two orders must hold the
    same plugins with the same bytes, be different orders, and be the setup without the patch's plugins; a plugin
    whose bytes differ from the bundle's is named (both orders were measured again on today's files)."""
    main_b = ctx.get("mainBundle")
    if main_b is None:
        return [], None
    ents = sorted((e for e in main_b.evidence if e.get("kind") == "enrichment" and e.get("enrichment") == "order-geometry"
                   and e.get("role") is None), key=lambda e: str(e.get("path")))
    if not ents:
        return [], None
    w = words.get("orderGeometryWords") or {}
    arm = next((a for a in main_b.plan.get("arms") or [] if a.get("armId") == p_arm), {}) or {}
    p_plugs = {str(x.get("name")).lower(): str(x.get("sha256") or "").lower()
               for x in (arm.get("requested") or {}).get("plugins") or []}
    lines, by, drifted, unj = [], {}, set(), 0
    for e in ents:
        doc = main_b.emit(e["path"]) or {}
        orders = doc.get("orders") or {}
        m = [(str(x.get("name")), str(x.get("sha256") or "").lower()) for x in orders.get("measured") or []]
        r = [(str(x.get("name")), str(x.get("sha256") or "").lower()) for x in orders.get("reversed") or []]
        if not m or sorted((n.lower(), s) for n, s in m) != sorted((n.lower(), s) for n, s in r):
            raise InputRefused(f"order-geometry-foreign: {e['path']} does not hold the same plugins with the same bytes "
                               f"in both orders, so it was not used.")
        if [n.lower() for n, _ in m] == [n.lower() for n, _ in r]:
            raise InputRefused(f"order-geometry-same-order: {e['path']} measured the same order twice, so it shows no "
                               f"order risk.")
        named = {str(x.get("name")).lower() for x in doc.get("drifted") or []}
        if sorted(n.lower() for n, _ in m) != sorted(p_plugs) or any(
                p_plugs.get(n.lower()) != s and n.lower() not in named for n, s in m):
            raise InputRefused(f"order-geometry-foreign: {e['path']} does not hold the plugins of the setup without the "
                               f"patch (other plugins, or other bytes it does not name), so it was not used.")
        drifted |= {str(x.get("name")) for x in doc.get("drifted") or []}
        probe = str(doc.get("probe"))
        pair = [str(x) for x in doc.get("pair") or []]
        low_m = [n.lower() for n, _ in m]
        first, second = sorted(pair, key=lambda x: low_m.index(x.lower()) if x.lower() in low_m else 0)
        meas = [x for x in doc.get("rows") or [] if x.get("measured") == "Holds" and x.get("reversed") == "Gone"]
        rev = [x for x in doc.get("rows") or [] if x.get("measured") == "Gone" and x.get("reversed") == "Holds"]
        bad = [x for x in doc.get("rows") or [] if "Unmeasured" in (x.get("measured"), x.get("reversed"))
               or None in (x.get("measured"), x.get("reversed"))]
        unj += len(bad)
        by[probe] = {"measuredOnly": len(meas), "reversedOnly": len(rev), "unjudged": len(bad),
                     "both": sum(1 for x in doc.get("rows") or [] if x.get("measured") == x.get("reversed") == "Holds"),
                     "path": e["path"], "checkId": doc.get("checkId")}
        nouns = (w.get("nouns") or {}).get(probe) or ["geometry row", "geometry rows"]
        groups_ = []
        for key_, rows_all in (("measured", meas), ("reversed", rev)):
            by_pair = collections.OrderedDict()
            for x in rows_all:
                by_pair.setdefault(tuple(sorted((str(x.get("A")), str(x.get("B"))), key=str.lower)), []).append(x)
            groups_ += [(key_, pr_, rs_) for pr_, rs_ in by_pair.items()]
        for key_, pr_, rows_ in groups_:
            labs = sorted({str(x.get("Label") or x.get("FormKey")) for x in rows_})
            ex = ", ".join(labs) if len(labs) <= 4 else bind(w.get("more") or "{shown} and {count} more",
                                                            {"shown": ", ".join(labs[:3]), "count": fmt_n(len(labs) - 3)})
            lines.append({"path": e["path"], "probe": probe, "direction": key_, "rows": len(rows_), "between": list(pr_),
                          "records": [{"formKey": x.get("FormKey"), "other": x.get("Other"), "kind": x.get("Kind"),
                                       "A": x.get("A"), "B": x.get("B"), "label": x.get("Label")} for x in rows_],
                          "text": bind(w["line"] if key_ == "measured" else w["lineReversed"], {
                              "check": (w.get("checks") or {}).get(probe) or probe, "count": fmt_n(len(rows_)),
                              "noun": nouns[0 if len(rows_) == 1 else 1], "verb": "is" if len(rows_) == 1 else "are",
                              "first": first, "second": second, "examples": ex,
                              "between": f"{pr_[0]} and {pr_[1]}"})})
    order_ = {p: i for i, p in enumerate(ORDER_GEOMETRY_PROBES)}
    lines.sort(key=lambda x: (order_.get(x["probe"], 9), x["direction"] != "measured"))
    probes = sorted(by, key=lambda p: order_.get(p, 9))
    not_measured = [p for p in ORDER_GEOMETRY_PROBES if p not in by and p in main_b.exe_to_check]
    flips = sum(v["measuredOnly"] + v["reversedOnly"] for v in by.values())
    chk = ", ".join((w.get("checks") or {}).get(p) or p for p in probes)
    s_ = (bind(w["sentence"], {"checks": chk, "flips": fmt_n(flips), "flipNoun": "row" if flips == 1 else "rows",
                               "flipVerb": "holds" if flips == 1 else "hold"}) if flips
          else bind(w["sentenceNone"], {"checks": chk}))
    if unj:
        s_ += bind(w.get("unjudged") or "", {"count": fmt_n(unj), "rowNoun": "row" if unj == 1 else "rows"})
    if not_measured:
        s_ += bind(w.get("notMeasured") or "", {"checks": ", ".join((w.get("checks") or {}).get(p) or p
                                                                     for p in not_measured),
                                                 "why": ctx.get("orderGeometryWhyNot") or ""})
    if drifted:
        s_ += bind(w.get("drifted") or "", {"names": ", ".join(sorted(drifted))})
    fact = {"measured": True, "probes": probes, "notMeasured": not_measured, "byProbe": by,
            "measuredOnly": sum(v["measuredOnly"] for v in by.values()),
            "reversedOnly": sum(v["reversedOnly"] for v in by.values()), "unjudged": unj,
            "drifted": sorted(drifted), "sentence": s_}
    return lines, fact


def build_prediction(ctx: dict) -> dict:
    """The prediction, coverage and validation blocks a report leads with.

    ctx: findings (planned, with allRows), armOrders {armId: [plugin]}, armIds (setups-compared order), targets
    [name], declared (lower-case declared patches), comparisonKinds, armChanges {armId: (added, removed)}, missing,
    unassessed, checkTitles, missingReason (fn), oldVerdict, limitations, words (report-priorities reader.prediction),
    inGame (reader.inGame), kindPhrases.

    The prediction setup P is the one with the evaluated mods and no patch: the arm that removes a declared patch when
    one does, else the baseline. W has none of the evaluated mods; S(m) has only m. A row is keyed
    (check, Kind, A, B, FormKey); presence is read from each setup's own conflict file (part `arm` or none)."""
    words, findings = ctx["words"], ctx["findings"]
    lower = lambda xs: {str(x).lower() for x in xs if x}                          # noqa: E731
    target_lower = lower(ctx["targets"])
    orders = ctx["armOrders"]
    kinds = ctx["comparisonKinds"]
    changes = ctx["armChanges"]
    declared = ctx["declared"]
    # each plugin's own master list (P63): a plugin that lists another as a master is built on it deliberately, so its
    # change to that master's value is no clash between mods; None when the bundle records no master list
    masters = ctx.get("masters")

    # --- which setup is P, and which (if any) carries the patch
    patched_arm, p_arm, patch_names = None, "baseline", []
    comparisons = []
    for arm_id in ctx["armIds"]:
        if kinds.get(arm_id) != "patch-comparison":
            continue
        added, removed = changes.get(arm_id) or ([], [])
        # a same-named file replaced in place by a declared patch (item 20): the side whose copy is the patch has it
        rep_ = (ctx.get("replacedPatches") or {}).get(arm_id) or {}
        in_arm, in_base = list(rep_.get("arm") or []), list(rep_.get("baseline") or [])
        if removed and not added and not in_arm:
            comparisons.append({"withPatch": "baseline", "withoutPatch": arm_id, "patches": list(removed) + in_base})
        elif added and not removed and not in_base:
            comparisons.append({"withPatch": arm_id, "withoutPatch": "baseline", "patches": list(added) + in_arm})
        elif not added and not removed and bool(in_arm) != bool(in_base):
            comparisons.append({"withPatch": arm_id if in_arm else "baseline",
                                "withoutPatch": "baseline" if in_arm else arm_id, "patches": in_arm or in_base})
    if comparisons:
        main = max(comparisons, key=lambda c: len(c["patches"]))
        p_arm, patched_arm, patch_names = main["withoutPatch"], main["withPatch"], main["patches"]
    p_set = lower(orders.get(p_arm) or [])
    p_targets = [t for t in ctx["targets"] if str(t).lower() in p_set]
    pt_lower = lower(p_targets)

    replaced_by = ctx.get("armReplaced") or {}

    def arm_with(plugins_lower):
        for arm_id in ctx["armIds"]:
            # A same-named variant has the plain setup's names but not its files (SW2): never take it as that setup.
            if replaced_by.get(arm_id):
                continue
            if arm_id != p_arm and lower(orders.get(arm_id) or []) == plugins_lower:
                return arm_id
        return None
    # Every setup's plugins, in this bundle and (below) the sibling: a plugin that is loaded in exactly the setups
    # that load a given mod travels with it (its own resources or masters), so the setup without that mod also lacks
    # it. A target or a declared patch never travels with anything.
    setups_seen = [lower(v) for v in orders.values()]
    declared_lower = {str(x).lower() for x in declared}

    def companions(drop_lower):
        out = set()
        for d in drop_lower:
            with_d = [s_ for s_ in setups_seen if d in s_]
            without_d = [s_ for s_ in setups_seen if d not in s_]
            if not with_d or not without_d:
                continue
            for c in set.intersection(*with_d) - set.union(*without_d) if without_d else set():
                if c != d and c not in target_lower and c not in declared_lower:
                    out.add(c)
        return out

    def arm_without(drop_lower):
        """This bundle's setup equal to P without these plugins (and, failing that, without their companions too)."""
        return arm_with(p_set - drop_lower) or arm_with(p_set - drop_lower - companions(drop_lower))
    w_arm = arm_without(pt_lower) if p_targets else None
    s_arms = {t: arm_without(pt_lower - {str(t).lower()}) for t in p_targets} if len(p_targets) > 1 else {}

    # --- per (check, arm): measured rows, measured with no conflict file, or nothing
    rows_by, prefixes, measured, count_only = {}, {}, set(), {}
    ident_of = {}
    by_object = set(words.get("identityByObject") or [])
    by_summary = set(words.get("identityBySummary") or [])

    # A world-state row's kind names its state ("STATE-CONTACT@<state>@Triggered", skill data `stateKinds`): the same
    # object in the same clash in several states is one clash (Pilgrim review: one cairn counted once per state), and
    # a contact or empty-space row there is the object's own state, as geometry.placement-contact and
    # geometry.fixture-empty-space rows are (P72)
    state_words = words.get("stateKinds") or {}
    state_kind_re = re.compile(state_words["pattern"]) if state_words.get("pattern") else None
    state_by_object = set(state_words.get("byObject") or [])

    def state_split(kind):
        """(the kind without its world state, the state), or (kind, None) for a row of no world state."""
        m_ = state_kind_re.match(str(kind or "")) if state_kind_re else None
        return (m_.group("kind"), m_.group("state")) if m_ else (kind, None)

    def identity(check_id, kind, a, b, r):
        """What makes two rows on two setups the same clash, never the plugin pair a probe names (window 2, lane core:
        new with a mod is the same object or field with and without it, in every check). A record lost edit is one
        plugin's change to one field of one record, lost whoever wins it. A geometry row with a partner object is the
        relationship between two objects, whichever plugins own them on each setup. A check whose row is a state of
        its own object (skill data `identityByObject`: a fixture left hanging, an object left without support) is that
        object: a fixture hanging because both evaluated mods remove hosts hangs with one of them alone if that one
        removes enough. Any other row is its object in that kind of clash. Plugin names are compared without regard to
        case, as the game reads them: a probe names the file on disk, a sibling's rows the load order's spelling."""
        fk = str(r.get("formKey")).lower() if r.get("formKey") is not None else None
        other = tuple(str(o).lower() for o in r.get("other") or ())
        if kind == "LOST-EDIT":
            # whose change is lost, and where: the losing plugin's change to one field of one record, whoever wins it
            # (Clockwork review B1: a change already lost without the mod, to another winner, is not new with it)
            return (check_id, kind, fk, r.get("field"), str(a).lower())
        base_kind, in_state = state_split(kind)
        if in_state is not None:
            if base_kind in state_by_object:
                return (check_id, base_kind, fk)
            if r.get("other") is not None:
                return (check_id, base_kind, fk, other)
            return (check_id, base_kind, fk)
        if check_id in by_object:
            return (check_id, kind, fk)
        if check_id in by_summary:
            # one conflict the probe lists once per object it names (two aliases split in one quest): the conflict is
            # its words and its partner record, whichever of its objects the row names (Pilgrim review: 2 splits, 4 rows)
            return (check_id, kind, other, str(r.get("summary") or "").lower())
        if r.get("other") is not None:
            return (check_id, kind, fk, other)
        return (check_id, kind, fk)

    def collect(fs, prefix=""):
        for f in fs:
            if f.get("part") not in ("arm", None):
                continue
            key = (f.get("checkId"), prefix + str(f.get("armId")))
            if f.get("kind") == "null":
                measured.add(key)
            elif f.get("kind") == "finding":
                measured.add(key)
                c = f.get("counts") or {}
                if c.get("derivedRows") == 0:
                    continue
                if not (c.get("A") and c.get("B")):
                    count_only[key] = count_only.get(key, 0) + int(c.get("findings") or 0)
                    continue
                for r in f.get("allRows") or f.get("rows") or []:
                    # one entry per reported row (a record can carry several conflicts); presence elsewhere is read
                    # on the record and its pair, the first five parts of the key
                    rk = (f.get("checkId"), str(c.get("groupKind") or ""), str(c.get("A")), str(c.get("B")),
                          r.get("formKey"), str(r.get("summary") or ""))
                    prior = rows_by.get(key, {}).get(rk)
                    if prior is not None and (prior["row"].get("other") or None) != (r.get("other") or None):
                        # two rows whose words are the same but whose partner objects differ are two rows (Sewers
                        # review, P93: a tower base against 13 objects lost one to an identical summary)
                        rk = rk[:5] + (rk[5] + " [" + ", ".join(map(str, r.get("other") or [])) + "]",)
                    rows_by.setdefault(key, {})[rk] = {"finding": f, "row": r,
                                                       "history": f.get("readerEvidenceReason") == "context-only-kind"}
                    ident = identity(rk[0], rk[1], rk[2], rk[3], r)
                    ident_of.setdefault(rk, ident)
                    prefixes.setdefault(key, set()).add(ident)
    collect(findings)

    # --- the siblings: separate evaluations of the same order without the patch, whose setups stand in for the ones
    #     this bundle lacks. Strict by design. The first sibling holding this bundle's P plugin for plugin, hash for
    #     hash, is the primary: a check is used from it only where both ran the same adapter and probe build and
    #     measured the same rows on that shared setup. A further sibling holds P too (compared the same way on its own
    #     copy) or holds only P with plugins left out ("sub-order"): then its population and location scope must be
    #     this bundle's, and a check is used only where it ran the same adapter and probe build and its receipt records
    #     that setup's plugins. Otherwise nothing is joined (refusal) or the check stays unseparated.
    siblings_in = list(ctx.get("siblings") or ([ctx["sibling"]] if ctx.get("sibling") else []))
    many_siblings = len(siblings_in) > 1
    sibling, sib_ok, sibling_record = (siblings_in[0] if siblings_in else None), {}, None
    sib_by_prefix = {s_.get("prefix") or "sibling:": s_ for s_ in siblings_in}
    for s_ in siblings_in:
        s_.setdefault("prefix", "sibling:")
    sib_ok_by = {}                             # prefix -> {check: joined}
    sib_infos = []                             # per sibling, in the order given: its setups, anchor and record
    sib_dup_of, sib_disagree = {}, set()       # a setup two siblings both hold -> the one used; (used setup, check)
    sib_not_comparable = set()                 # checks not joined because the two probe builds cannot be compared
    sib_setup_plugins, p_plugin_names = {}, []
    sib_role_ref = []
    sib_only, anchor, pp = set(), None, "sibling:"
    sib_roles = {}                             # role -> prefixed setup
    all_orders = {a_: [str(x) for x in v_] for a_, v_ in orders.items()}      # every setup's plugins, sibling's prefixed
    sib_prefix_re = re.compile(r"^(sibling\d*:)")

    def sib_split(arm_id):
        """(the sibling an arm id names, its own arm id), or (None, arm id) for this bundle's setup."""
        m_ = sib_prefix_re.match(str(arm_id)) if arm_id is not None else None
        if not m_ or m_.group(1) not in sib_by_prefix:
            return None, arm_id
        return sib_by_prefix[m_.group(1)], str(arm_id)[len(m_.group(1)):]

    def sib_check_ok(arm_id, check_id):
        """Whether a setup's rows for a check may be read: always for this bundle's, for a sibling's where it is joined."""
        s_, _ = sib_split(arm_id)
        return s_ is None or bool(sib_ok_by.get(s_["prefix"], {}).get(check_id))

    def p_of(check_id):
        """The setup a check's prediction rows come from: this bundle's P, or the primary sibling's identical setup when
        only that sibling measured the check there."""
        return pp + anchor if check_id in sib_only else p_arm

    if siblings_in and p_targets:
        main_b = ctx["mainBundle"]

        def ident(bundle, arm_id):
            a = next((x for x in bundle.plan.get("arms") or [] if x.get("armId") == arm_id), {}) or {}
            req = a.get("requested") or {}
            plugs = tuple((str(x.get("name")), str(x.get("sha256") or "")) for x in req.get("plugins") or [])
            states = tuple(req.get("states") or (a.get("expected") or {}).get("states") or [])
            return plugs, req.get("population"), states

        p_plugs, p_pop, p_states = ident(main_b, p_arm)

        def sub_order(v):
            """P with some plugins left out: the same plugins in the same order with the same bytes, population, states."""
            it_ = iter(p_plugs)
            return v[1] == p_pop and v[2] == p_states and all(x in it_ for x in v[0])

        def why_unusable(v):
            p_by = {n.lower(): h for n, h in p_plugs}
            for n, h in v[0]:
                if n.lower() not in p_by:
                    return f"it holds {n}, which the setup without the patch does not"
                if p_by[n.lower()] != h:
                    return f"its {n} has other bytes than the setup without the patch's"
            if v[1] != p_pop or v[2] != p_states:
                return "its population or world states are not the setup without the patch's"
            return "its plugins are not in the order of the setup without the patch"

        def scope_cells(bundle):
            sc = bundle.scope
            if not isinstance(sc, dict):
                return None
            return {str(l_.get("cell")) for l_ in ((sc.get("locationContributors") or {}).get("locations") or [])}

        def pop_identity(bundle):
            pop = bundle.plan.get("population")
            return pop.get("identity") if isinstance(pop, dict) else pop

        main_cells, main_pop_id = scope_cells(main_b), pop_identity(main_b)
        for s_ in siblings_in:
            idents = {x.get("armId"): ident(s_["bundle"], x.get("armId")) for x in s_["bundle"].plan.get("arms") or []}
            anc = next((a for a, v in idents.items() if v == (p_plugs, p_pop, p_states)), None)
            usable = [a for a, v in idents.items() if sub_order(v)]
            info = {"s": s_, "idents": idents, "anchor": anc, "usable": usable,
                    "unusable": {a: why_unusable(v) for a, v in idents.items() if a not in usable}}
            if anc is None:
                if not many_siblings:
                    raise InputRefused("sibling-foreign: the sibling bundle has no setup with the same plugins, in the "
                                       "same order and with the same bytes, as the setup without the patch, so nothing "
                                       "was joined.")
                s_cells = scope_cells(s_["bundle"])
                why_ = None
                if pop_identity(s_["bundle"]) != main_pop_id:
                    why_ = "its population is not the one this evaluation measured"
                elif s_cells is not None and (main_cells is None or not main_cells <= s_cells):
                    why_ = "its location scope does not cover this evaluation's locations"
                elif not usable:
                    why_ = ("it has no setup that is the setup without the patch, or that setup with plugins left out, "
                            "with the same bytes")
                if why_:
                    raise InputRefused(f"sibling-foreign: the sibling bundle {s_['pathAsGiven']} cannot stand in for "
                                       f"any setup here because {why_}, so nothing was joined.")
            sib_infos.append(info)
        primary = next((i_ for i_ in sib_infos if i_["anchor"] is not None), None)
        if primary is None:
            raise InputRefused("sibling-unanchored: none of the sibling bundles has a setup identical to the setup "
                               "without the patch, so no check could be compared between the evaluations and nothing "
                               "was joined.")
        sibling, sib_b, anchor, pp = primary["s"], primary["s"]["bundle"], primary["anchor"], primary["s"]["prefix"]
        ctx["sibling"] = sibling
        sib_idents = primary["idents"]
        collect(sibling["findings"], pp)
        setups_seen.extend({x[0].lower() for x in v[0]} for v in sib_idents.values())
        all_orders.update({pp + a_: [x[0] for x in v_[0]] for a_, v_ in sib_idents.items()})
        # a further sibling's setups that are P or P with plugins left out (the others are never read)
        for i_ in sib_infos:
            if i_ is not primary:
                setups_seen.extend({x[0].lower() for x in i_["idents"][a_][0]} for a_ in i_["usable"])
                all_orders.update({i_["s"]["prefix"] + a_: [x[0] for x in i_["idents"][a_][0]] for a_ in i_["usable"]})
        # who stands in for a role first: a sibling holding P (its checks are compared row for row), then one holding
        # only P with plugins left out; among equals the earlier --sibling-bundle. A setup two siblings both hold is
        # read from the first of them; the other is recorded as offering it.
        sib_order = [i_ for i_ in sib_infos if i_["anchor"] is not None] + [i_ for i_ in sib_infos if i_["anchor"] is None]
        seen_setups = {}
        for i_ in sib_order:
            for a_ in (i_["usable"] if i_ is not primary else list(i_["idents"])):
                key_ = i_["idents"][a_]
                if key_ in seen_setups:
                    sib_dup_of[i_["s"]["prefix"] + a_] = seen_setups[key_]
                else:
                    seen_setups[key_] = i_["s"]["prefix"] + a_

        def sib_role(drop_lower):
            for drop in (drop_lower, drop_lower | companions(drop_lower)):
                want = (tuple(x for x in p_plugs if x[0].lower() not in drop), p_pop, p_states)
                for i_ in sib_order:
                    found = next((a for a in (i_["usable"] if i_ is not primary else list(i_["idents"]))
                                  if i_["idents"][a] == want), None)
                    if found:
                        return i_["s"]["prefix"] + found
            return None
        sib_role_ref.append(sib_role)
        if w_arm is None:
            w_sib = sib_role(pt_lower)
            if w_sib:
                w_arm = sib_roles["without the evaluated mods"] = w_sib
        for t in list(s_arms):
            if s_arms[t] is None:
                s_sib = sib_role(pt_lower - {str(t).lower()})
                if s_sib:
                    s_arms[t] = sib_roles[f"only {t}"] = s_sib

        def receipt_build(bundle, check_id, arm_id):
            """(program, dll, legacy): the probe build a receipt records -- the emitter's bound program identity
            (program id, digest) or None, the measured (dll, core dll) or None, and whether the receipt predates
            program identity (no `consumed.programs`)."""
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
            consumed = (rc or {}).get("consumed") or {}
            progs, t = consumed.get("programs"), consumed.get("tool") or {}
            em = progs.get("emitter") if isinstance(progs, dict) else None
            program = ((em.get("program"), em["digest"]) if isinstance(em, dict) and isinstance(em.get("digest"), str)
                       and em["digest"] else None)
            dll = (t.get("dllSha256"), t.get("coreDllSha256")) if t.get("dllSha256") else None
            return program, dll, not isinstance(progs, dict)

        def build_compare_on(check_id, one, two):
            """(basis, note, why): how two receipts' probe builds compare, each (bundle, arm). Program identity where
            both bind it, else the dll fields where both record them (the note says one side has no program identity),
            else nothing when the check runs no probe; otherwise the builds cannot be compared and the check is not
            joined."""
            (mp, md_, ml), (sp, sd, sl) = receipt_build(one[0], check_id, one[1]), receipt_build(two[0], check_id, two[1])
            if mp and sp:
                return "program", None, None if mp == sp else "the two evaluations ran this check with different probe builds"
            if md_ and sd:
                note = None
                if not (ml and sl):
                    note = ("one side predates program identity, so only the probe dll was compared" if (ml or sl) else
                            "a receipt binds no program identity for its probe, so only the probe dll was compared")
                return "dll", note, None if md_ == sd else "the two evaluations ran this check with different probe builds"
            if not (mp or sp or md_ or sd) and not ((main_b.checks.get(check_id) or {}).get("adapter") or {}).get("exe"):
                return "no-probe", None, None
            return "not-comparable", None, ("a receipt for this check on the shared setup records no probe build that "
                                            "can be compared with the other's, so whether the two evaluations ran the "
                                            "same probe build cannot be told")

        def build_compare(check_id, main_arm, sib_arm):
            return build_compare_on(check_id, (main_b, main_arm), (sib_b, sib_arm))

        def receipt_config(bundle, check_id, arm_id):
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
            cfg = ((rc or {}).get("consumed") or {}).get("configuration") or {}
            plugs = cfg.get("plugins")
            return None if not isinstance(plugs, list) else tuple(
                (str(x.get("name")), str(x.get("sha256") or "")) for x in plugs if isinstance(x, dict))

        checks_record = []
        for check_id in sorted({c for c, _ in measured if c}):
            why, how, basis, note = None, None, None, None
            if (main_b.checks.get(check_id) or {}).get("adapter") != (sib_b.checks.get(check_id) or {}).get("adapter"):
                why = "the two evaluations ran this check with different adapters"
            elif (check_id, p_arm) not in measured and (check_id, pp + anchor) in measured:
                # Only the sibling measured it on the shared setup: its rows stand in for this bundle's, and only when
                # the configuration its own receipt records is this setup's, plugin for plugin, in order, byte for byte.
                got = receipt_config(sib_b, check_id, anchor)
                if got is None:
                    why = ("the evaluation without the patch records no configuration for this check on the shared "
                           "setup, so its result is not used")
                elif got != p_plugs:
                    why = ("the evaluation without the patch measured this check on a configuration that differs from "
                           "this setup's (plugins, order or bytes), so its result is not used")
                else:
                    how = "from-sibling-only"
                    sib_only.add(check_id)
            elif (check_id, p_arm) not in measured or (check_id, pp + anchor) not in measured:
                why = "one of the two evaluations did not measure this check on the shared setup"
            else:
                basis, note, why = build_compare(check_id, p_arm, anchor)
                if basis == "not-comparable":
                    sib_not_comparable.add(check_id)
                elif why is None and ((prefixes.get((check_id, p_arm)) or set())
                                      != (prefixes.get((check_id, pp + anchor)) or set())):
                    why = "the two evaluations measured different results for this check on the shared setup"
            sib_ok[check_id] = why is None
            checks_record.append({"checkId": check_id, "joined": why is None, "why": why, "buildBasis": basis,
                                  **({"buildBasisNote": note} if note else {}), **({"how": how} if how else {})})
        sib_ok_by[pp] = sib_ok
        sib_setup_plugins = {a_: [x[0] for x in v_[0]] for a_, v_ in sib_idents.items()}
        p_plugin_names = [x[0] for x in p_plugs]
        sibling_record = {"pathAsGiven": sibling.get("pathAsGiven"), "planId": sibling.get("planId"),
                          "manifestSha256": sibling.get("manifestSha256"), "anchorArm": anchor,
                          "roles": {}, "checks": checks_record}
        primary["record"] = sibling_record

        # a further sibling: its rows, then each check it measured, compared with the setup the prediction reads
        for i_ in sib_order:                   # holding the shared setup first: a sub-order join rests on theirs
            if i_ is primary:
                continue
            pre_, s_b2, anc2 = i_["s"]["prefix"], i_["s"]["bundle"], i_["anchor"]
            collect(i_["s"]["findings"], pre_)
            ok2, recs2 = {}, []
            for check_id in sorted({c for c, a_ in measured if c and str(a_).startswith(pre_)}):
                why, basis, note = None, None, None
                ref_ = p_of(check_id)
                ref_b = sib_b if ref_ != p_arm else main_b
                ref_a = anchor if ref_ != p_arm else p_arm
                arms_ = [a_ for a_ in (i_["usable"]) if (check_id, pre_ + a_) in measured]
                if (main_b.checks.get(check_id) or {}).get("adapter") != (s_b2.checks.get(check_id) or {}).get("adapter"):
                    why = "the two evaluations ran this check with different adapters"
                elif (check_id, ref_) not in measured:
                    why = "this evaluation has no result for this check on the setup without the patch to compare with"
                elif not arms_:
                    why = "it measured this check on no setup that is the setup without the patch or that setup with plugins left out"
                elif anc2 is not None and anc2 not in arms_:
                    why = "it holds the setup without the patch but did not measure this check there"
                elif anc2 is None and not any(sib_ok_by.get(x_["s"]["prefix"], {}).get(check_id)
                                              for x_ in sib_infos if x_["anchor"] is not None):
                    # (round 2, F2) it compares no rows itself: only a check an evaluation holding the shared setup
                    # measured the same way as this one is taken from it
                    why = ("no separate evaluation holding the shared setup measured this check the same way as this "
                           "one, so its result is not used")
                else:
                    for a_ in arms_:
                        basis, note, why = build_compare_on(check_id, (ref_b, ref_a), (s_b2, a_))
                        if why is None:
                            got = receipt_config(s_b2, check_id, a_)
                            if got != i_["idents"][a_][0]:
                                why = ("its receipt for this check does not record the setup's own plugins, order and "
                                       "bytes, so its result is not used")
                        if why is not None:
                            break
                    if why is None and anc2 is not None and ((prefixes.get((check_id, ref_)) or set())
                                                             != (prefixes.get((check_id, pre_ + anc2)) or set())):
                        why = "the two evaluations measured different results for this check on the shared setup"
                ok2[check_id] = why is None
                recs2.append({"checkId": check_id, "joined": why is None, "why": why, "buildBasis": basis,
                              **({"buildBasisNote": note} if note else {})})
            sib_ok_by[pre_] = ok2
            i_["record"] = {"pathAsGiven": i_["s"].get("pathAsGiven"), "planId": i_["s"].get("planId"),
                            "manifestSha256": i_["s"].get("manifestSha256"), "anchorArm": anc2,
                            "roles": {}, "checks": recs2}
        for k_, v_ in sib_roles.items():
            s_, a_ = sib_split(v_)
            next(x for x in sib_infos if x["s"] is s_)["record"]["roles"][k_] = a_
        # a setup two siblings hold and both may be read for a check: where their rows differ, neither is used for it
        for dup_, used_ in sib_dup_of.items():
            for check_id in {c for c, a_ in measured if a_ == dup_}:
                if (check_id, used_) in measured and sib_check_ok(dup_, check_id) and sib_check_ok(used_, check_id) and (
                        (prefixes.get((check_id, dup_)) or set()) != (prefixes.get((check_id, used_)) or set())):
                    sib_disagree.add((used_, check_id))

    def sib_usable_arm(arm_id):
        """A sibling's setup that may be read for a fewer-plugins role: P or P with plugins left out (same bytes),
        never a setup another sibling's copy stands in for."""
        s_, a_ = sib_split(arm_id)
        if s_ is None:
            return True
        if arm_id in sib_dup_of:
            return False
        i_ = next((x for x in sib_infos if x["s"] is s_), None)
        return i_ is not None and a_ in i_["usable"]

    def present(arm_id, rk):
        if arm_id is not None and not sib_check_ok(arm_id, rk[0]):
            return False                       # a sibling check that is not joined is unused
        if arm_id is not None and (arm_id, rk[0]) in sib_disagree:
            return False                       # two siblings measured this setup differently for this check
        if arm_id == p_arm:
            arm_id = p_of(rk[0])
        return arm_id is not None and ident_of.get(rk, tuple(rk[:5])) in (prefixes.get((rk[0], arm_id)) or set())

    # A setup's own re-measure of the prediction setup's rows (its emit's `Predicates`), where that setup was measured
    # against P: this bundle's setups when P is its baseline, the sibling's when its baseline is P. A pair a setup does
    # not list can still be there: with one mod alone its partner may be a base-game object at its own position, which
    # no pair listing names (third blind review). Holds is there; Gone, Unmeasured or no re-measure is not.
    remeasure_cache = {}

    def remeasured(arm_id, check_id):
        key = (arm_id, check_id)
        if key not in remeasure_cache:
            remeasure_cache[key] = None
            bundle, a = None, arm_id
            if arm_id is None or arm_id == p_arm:
                pass
            elif sib_split(arm_id)[0] is not None:
                s_, a_ = sib_split(arm_id)
                i_ = next((x for x in sib_infos if x["s"] is s_), None)
                if (i_ is not None and i_["anchor"] == "baseline" and sib_check_ok(arm_id, check_id)
                        and p_of(check_id) == p_arm):
                    bundle, a = s_["bundle"], a_
            elif p_arm == "baseline" and p_of(check_id) == p_arm:
                bundle = ctx.get("mainBundle")
            if bundle is not None:
                path = bundle.output_emit_path(check_id, a, "arm") or bundle.output_emit_path(check_id, a, None)
                emit = bundle.emit(path) if path else None
                preds = emit.get("Predicates") if isinstance(emit, dict) else None
                if isinstance(preds, list):
                    remeasure_cache[key] = {(x.get("Kind"), x.get("A"), x.get("B"), x.get("FormKey"), x.get("Other")):
                                            str(x.get("Physical")) for x in preds if isinstance(x, dict)}
        return remeasure_cache[key]

    def there(arm_id, rk):
        """New with a mod, or already there without it: the one comparison every check, count and sentence uses. The
        clash is there on that setup when it lists the same clash by its identity (the object or field, never the pair
        a probe names), or re-measures the prediction setup's row there as still holding."""
        if present(arm_id, rk):
            return True
        if rk[1] == "LOST-EDIT" or rk not in p_rows:
            return False
        preds = remeasured(arm_id, rk[0])
        if not preds:
            return False
        other = (p_rows.get(rk) or {}).get("row", {}).get("other") or [None]
        return preds.get((rk[1], rk[2], rk[3], rk[4], other[0])) == "Holds"

    # The names enrichment, where the bundle carries one: each object a row names, its base and the plugins read that
    # carry a version of it (in load order); each base, its type and model ("" when the record has none).
    preferred_example_fields = set(words.get("examplePreferredFields") or [])
    names = ctx.get("names") if isinstance(ctx.get("names"), dict) else {}
    name_refs, name_bases = names.get("refs") or {}, names.get("bases") or {}

    kinds_ = words.get("objectKinds") or {}
    field_words_ = words.get("fieldWords") or {}
    flag_bits_ = words.get("flagBits") or {}

    def field_meaning(rtypes, f):
        fig_ = words.get("fieldInGame") or {}
        return next((fig_[f"{t_}|{f}"] for t_ in rtypes if f"{t_}|{f}" in fig_), None) or fig_.get(str(f))

    def field_word(f):
        """A field in plain words (Winterhold review 5, skill data `fieldWords`); the record's name where none is set."""
        return field_words_.get(str(f), str(f)) if f is not None else ""

    def plain_value(field, v):
        """A value as a modder reads it: a flag field's numbers decoded bit by bit (skill data `flagBits`; 1024 on a
        placed object is Persistent), any bit not listed left as its number."""
        bits_ = flag_bits_.get(str(field))
        t = shown_value(v)
        # (third review, Snazzy S4) a value skill data names in words (the base game's no-water height)
        named_ = ((words.get("valueWords") or {}).get(str(field)) or {}).get(t.strip())
        if named_:
            return named_
        if not bits_:
            return t
        out_ = []
        for tok_ in [x.strip() for x in t.split(",") if x.strip()]:
            if re.fullmatch(r"\d+", tok_):
                n_ = int(tok_)
                for b_, name_ in sorted(((int(k_), w_) for k_, w_ in bits_.items() if k_.isdigit()), reverse=True):
                    if n_ & b_:
                        out_.append(name_)
                        n_ &= ~b_
                if n_ or not int(tok_):
                    out_.append(str(n_))
            else:
                out_.append(tok_)
        return ", ".join(out_)

    def object_kind(base_):
        """What kind of thing a base is, in plain words (Pilgrim review 3): its record type, a fact the record states
        (skill data `objectKinds`); never a role read from its model's name (reviewer); "" when the type is not listed."""
        return (kinds_.get("types") or {}).get(str(base_.get("type") or ""), "")

    def object_name(fk, summary=None):
        """What a record is, for a modder: its kind and editor ID from the row's summary, else a placed object's base
        kind and editor ID from the names enrichment ("a visual effect <base>"); "" when neither is read (Pilgrim review
        S3 and 3, Missives review 6)."""
        base_ = name_bases.get((name_refs.get(str(fk)) or {}).get("base") or "") or {}
        kind_ = object_kind(base_) if base_ else ""
        m_ = re.match(r"^\S+ (\S+?):", str(summary or ""))
        if m_ and not re.match(r"^[0-9A-Fa-f]{6}$", m_.group(1)) and not str(fk).startswith(m_.group(1)):
            return f"{kind_} {m_.group(1)}" if kind_ else m_.group(1)
        if not base_.get("editorId"):
            return ""
        return f"{kind_} {base_['editorId']}" if kind_ else f"a {base_['editorId']}"

    def named(fk, summary=None):
        n_ = object_name(fk, summary)
        return f"{fk} ({n_})" if n_ else str(fk)

    marker_models = {str(x).replace("\\", "/").lower() for x in (words.get("markerModels") or {}).get("models") or []}

    def marker_model(fk):
        """(seventh review, Sewers R4 S4) the object's base's model is one the game does not draw (every shape an editor
        marker; skill data `markerModels`, from a census of the base game's meshes)."""
        m_ = str((name_bases.get((name_refs.get(str(fk)) or {}).get("base") or "") or {}).get("model") or "")
        m_ = m_.replace("\\", "/").lower()
        return bool(m_) and (m_ in marker_models or (m_.startswith("meshes/") and m_[7:] in marker_models))

    def bodiless(fk):
        """An object with no body to clash: its base record has no model and its type has none without one (base_has_no_body,
        skill data bodilessTypes), decided from the record, never a name. (seventh review) Nor has one whose model the game
        does not draw."""
        return base_has_no_body(names, fk, words.get("bodilessTypes")) or marker_model(fk)

    marker_cache = {}

    def base_is_marker(fk):
        """(sixth review, Winterhold r6 S2) The object's base is an editor marker: a base of a type skill data
        `markerFlags` lists, carrying that record flag in its own plugin's file (recorded bytes only). False where not
        read."""
        b_ = (name_refs.get(str(fk)) or {}).get("base")
        typ_ = str((name_bases.get(b_ or "") or {}).get("type") or "")
        bit_ = (words.get("markerFlags") or {}).get(typ_)
        if marker_model(fk):
            return True                        # (seventh review) a model the game does not draw
        if not b_ or not bit_:
            return False
        if b_ not in marker_cache:
            path_, sha_ = plugin_file(str(b_).split(":", 1)[-1])
            got_ = None
            if path_ and sha_ and Path(path_).is_file():
                if path_ not in sha_cache:
                    sha_cache[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
                if sha_cache[path_] == sha_:
                    try:
                        got_ = pluginread.record_flags(path_, [b_], (words.get("markerRecordTypes") or {}).get(typ_, "STAT").encode())
                        got_ = got_.get(b_)
                        plugin_reads.append({"plugin": str(b_).split(":", 1)[-1], "sha256": sha_, "record": b_,
                                             "read": "recordFlags", "found": got_ is not None})
                    except pluginread.PluginReadError:
                        got_ = None
            marker_cache[b_] = bool(got_ is not None and int(got_) & int(bit_))
        return marker_cache[b_]

    def no_body(rk, other):
        return rk[1] != "LOST-EDIT" and (bodiless(rk[4]) or any(bodiless(o) for o in other or ()))

    # Whether an object has a body is read from its base record; with no names enrichment carrying base records the
    # report cannot tell for any geometry clash (a coverage line says so, with the count), and a clash whose object's
    # base the enrichment did not read is "cannot tell", never counted or dropped unsaid (round 3, coordinator D2)
    bases_read = isinstance(names.get("bases"), dict)

    def carried_origin(provs_, vals_, w_):
        """The plugin whose own change provider w_'s value is, walking back through the versions it only carries; None
        where w_'s value is its own change."""
        k_ = recordrows.attribute(provs_, vals_, w_, masters)["owner"]
        return provs_[k_] if k_ != w_ else None

    def owner_of(provs_, vals_, k_):
        """(seventh review round 2) (index, name) of the one owner of provider k_'s value: the first provider holding it
        as a change of its own; "the base game" for the record's original value."""
        o_ = recordrows.attribute(provs_, vals_, k_, masters)["owner"]
        return o_, ((words.get("baseGameOwner") or "the base game") if o_ == 0 and is_base_game(provs_[0], words)
                    else provs_[o_])

    def whose_value(arm_id, check_id, fk, field, plugin=None, index=None):
        """(seventh review) The attribution read of one record field in one setup, keyed by setup, check, record and
        field, never by a row: (providers, values, recordrows.attribute of the plugin or index), or None where that
        setup's chain does not hold the field (the sentence then names no one)."""
        if not field:
            return None
        ch_ = (chains_of(check_id, arm_id) or {}).get(str(fk))
        if not ch_ or not ch_.get("providers") or not (field in ch_["values"] or ch_.get("allListed")):
            return None
        provs_ = list(ch_["providers"])
        vals_ = list(ch_["values"].get(field) or [()] * len(provs_))
        if index is None:
            low_ = [str(x).lower() for x in provs_]
            if str(plugin).lower() not in low_:
                return None
            index = low_.index(str(plugin).lower())
        return provs_, vals_, recordrows.attribute(provs_, vals_, index, masters)

    def base_unknown(fk):
        ref_ = name_refs.get(str(fk)) or {}
        if not ref_.get("base") and name_bases and ":" in str(fk):
            # (sixth review, Sewers r6 S2) a reference the names enrichment did not read: its base from its own plugin
            # file (recorded bytes only), judged by that base's record where the enrichment has it
            got_ = plugin_record(str(fk).split(":", 1)[1], "placementFull", str(fk))
            if got_ and got_.get("base") and got_["base"] in name_bases:
                name_refs[str(fk)] = {**ref_, "base": got_["base"], "baseReadFrom": str(fk).split(":", 1)[1]}
        base = name_bases.get((name_refs.get(str(fk)) or {}).get("base") or "") or {}
        return not base.get("type") or (base.get("type") == "Light" and base.get("model") is None)

    body_checks = set(words.get("bodyChecks") or [])   # checks whose rows name placed objects, which have a base
    effect_model_re = re.compile(words["effectModelPattern"], re.I) if words.get("effectModelPattern") else None

    touching_re = re.compile(words["touchingPattern"]) if words.get("touchingPattern") else None
    coplanar_re = re.compile(words["coplanarPattern"]) if words.get("coplanarPattern") else None

    def effect_pair(fk, others):
        """A geometry pair with an object whose base's model is a visual effect (skill data effectModelPattern)."""
        return effect_model_re is not None and any(
            effect_model_re.search(str((name_bases.get((name_refs.get(str(o_)) or {}).get("base") or "") or {})
                                       .get("model") or "")) for o_ in [fk, *(others or ())])

    def body_unread(rk, other):
        return (bases_read and rk[0] in body_checks
                and (base_unknown(rk[4]) or any(base_unknown(o) for o in other or ())))

    # A value that disables its object (initially disabled, or moved below the world), by skill data patterns; an object
    # both mods disable has no lost change that shows in game (third blind review, W-S1)
    disabled_res = [re.compile(x) for x in words.get("disabledValuePatterns") or []]

    always_on = {str(x).lower() for x in (words.get("disabledRecord") or {}).get("alwaysEnabledParents") or []}
    dis_flag = (words.get("disabledRecord") or {}).get("flag") or "InitiallyDisabled"

    def disables(v):
        """A value that sets its object off for good (blind review, Winterhold 0A0315): moved below the world, or enabled
        opposite to a parent that is always enabled (the player). A bare initially-disabled flag only starts it off."""
        text = " ".join(str(x) for x in v) if isinstance(v, list) else str(v or "")
        if any(r_.search(text) for r_ in disabled_res):
            return True
        return "OppositeOfParent" in text and any(p_ in text.lower() for p_ in always_on)

    def off_at_load(v):
        """A value that has its object off when the game loads: off for good (disables), or only initially disabled.
        What a load-time statement reads ("brings back", "switches back on"); never what has no effect in game."""
        text = " ".join(str(x) for x in v) if isinstance(v, list) else str(v or "")
        return disables(v) or dis_flag in text
    both_disabled_cache = []

    def both_disabled(rk):
        if not both_disabled_cache:
            both_disabled_cache.append({(k_[2].lower(), k_[3].lower(), k_[4]) for k_, h_ in p_rows.items()
                                        if k_[1] == "LOST-EDIT"
                                        and disables((h_["row"].get("values") or {}).get("lost"))
                                        and disables((h_["row"].get("values") or {}).get("winner"))})
        if (rk[2].lower(), rk[3].lower(), rk[4]) in both_disabled_cache[0]:
            return True
        # judged from each side's whole record, not only the row's field (Clockwork review S3)
        return bool(side_disables(rk, rk[2]) and side_disables(rk, rk[3]))
    anchor_unclear_re = re.compile(words["fixtureAnchorUnclearPattern"]) if words.get("fixtureAnchorUnclearPattern") else None

    def path_case_only(rk):
        v = (p_rows.get(rk) or {}).get("row", {}).get("values") or {}
        return same_but_path_case(v.get("lost"), v.get("winner"))

    def chain_same(arm_id, rk, a, b):
        """Plugin a's and plugin b's values of the row's field read equal in that setup's record import chain (path case,
        unused bytes and rounding folded, as recordrows compares); None where the chain does not hold both."""
        got_ = field_chain(arm_id, rk, a)
        if got_ is None:
            return None
        provs_, vals_, _ = got_
        low_ = [str(x).lower() for x in provs_]
        if str(a).lower() not in low_ or str(b).lower() not in low_:
            return None
        return vals_[low_.index(str(a).lower())] == vals_[low_.index(str(b).lower())]

    def same_in_chain(arm_id, rk):
        """The losing plugin's value of the row's field reads equal to the winner's in the record import's own chain
        (path case and unused bytes folded, as recordrows compares): no change the game sees."""
        return bool(chain_same(arm_id, rk, rk[2], rk[3]))

    # Whose edit a value is (window 2, lane core; Pilgrim's review B1): the one rule, recordrows.own_change, asked of
    # the record's provider chain in that setup, read from the record import's own transcripts. A plugin changed a field
    # only if its value differs from the one it inherits from its nearest master; a value equal to it is no edit of
    # that plugin's, whoever loaded before it changed the field. Every lost, kept, dropped and undone change asks this.
    unrecorded_values = {str(x) for x in words.get("unrecordedValues") or []}

    def value_recorded(v):
        vs = v if isinstance(v, list) else [v]
        return v not in (None, "", []) and not any(str(x) in unrecorded_values for x in vs)
    chain_cache, chain_scope, needed_fks = {}, [], collections.defaultdict(set)
    for (c_, a_), rs in rows_by.items():
        needed_fks[(c_, a_)].update(str(k_[4]) for k_ in rs if k_[1] == "LOST-EDIT")
    # the objects of geometry rows too, wherever a record import was measured: whose version of an object loads last
    # (a patch's placement, Clockwork review S5) is read from the same chains
    geo_fks = {str(x) for rs in rows_by.values() for k_, h_ in rs.items() if k_[1] != "LOST-EDIT"
               for x in [k_[4], *((h_.get("row") or {}).get("other") or [])] if x}
    for key_ in [k for k in list(needed_fks) if needed_fks[k]]:
        needed_fks[key_] |= geo_fks
    # (round 2, Snazzy S2) every measured setup of a record check reads the records any setup lost a change on, so the
    # setup with the patch says whether the patch holds a field it no longer loses (a removal)
    lost_by_check_ = collections.defaultdict(set)
    for (c_, a_), fks_ in list(needed_fks.items()):
        lost_by_check_[c_] |= {x for x in fks_ if x not in geo_fks}
    for (c_, a_) in measured:
        if lost_by_check_.get(c_):
            needed_fks[(c_, a_)] |= lost_by_check_[c_]

    def chains_of(check_id, arm_id):
        key = (check_id, arm_id)
        if key not in chain_cache:
            chain_cache[key] = None
            bundle, a = ctx.get("mainBundle"), arm_id
            if sib_split(arm_id)[0] is not None:
                s_, a = sib_split(arm_id)
                bundle = s_.get("bundle")
            got = bundle.transcripts(check_id, a) if bundle is not None and needed_fks.get(key) else None
            if got:
                if not chain_scope:
                    chain_scope.append(recordrows.load_scope())
                chain_cache[key] = recordrows.read_chains(got[0], got[1], got[2], chain_scope[0], needed_fks[key])
        return chain_cache[key]

    def field_chain(arm_id, rk, plugin):
        """(providers, values, items) of the row's field in that setup, with `plugin` among the providers: from the
        setup's provider chain; where the transcripts do not hold it, from the row's own three values (the plugin before
        the loser, the loser, the winner). `items`: a list field's items per provider, where they were read. None where
        neither can tell."""
        row_ = ((rows_by.get((rk[0], arm_id)) or {}).get(rk) or {}).get("row") or {}
        field = row_.get("field")
        ch = (chains_of(rk[0], arm_id) or {}).get(str(rk[4]))
        if ch and str(plugin).lower() in [str(x).lower() for x in ch["providers"]] and (
                field in ch["values"] or ch.get("allListed")):
            # a tree answer lists every difference from the winner: a field it does not list, every provider holds
            provs = list(ch["providers"])
            return provs, list(ch["values"].get(field) or [()] * len(provs)), (ch.get("items") or {}).get(field)
        reads, v = row_.get("reads") or [], row_.get("values") or {}
        before = reads[0].get("plugin") if reads and isinstance(reads[0], dict) else None
        three = [v.get("overridden"), v.get("lost"), v.get("winner")]
        if not all(value_recorded(x) for x in three) or str(plugin).lower() not in (str(rk[2]).lower(), str(rk[3]).lower()):
            return None
        return [before or "", rk[2], rk[3]], [path_folded(x) for x in three], None

    def own_edit(arm_id, rk, plugin):
        """Whether `plugin`'s value of the row's field is a change of its own in that setup: {changed, inherits (the
        plugin it inherits from), before (the plugin loaded just before it), beforeWins (the winner holds that plugin's
        value), listKept (a list whose items it added or removed the winning list keeps so, item by item: Pilgrim's
        review B1)}. None where the chain cannot tell."""
        got = field_chain(arm_id, rk, plugin)
        if got is None:
            return None
        provs, vals, items = got
        i = [str(x).lower() for x in provs].index(str(plugin).lower())
        ms = (masters or {}).get(str(plugin).lower()) if masters is not None else None
        changed, j = recordrows.own_change(provs, vals, i, ms)
        kept = (recordrows.list_change_kept(items[j], items[i], items[-1])
                if items and j is not None and len(items) == len(provs) else None)
        # (third review, Missives S1) a value equal to the one an earlier base-game plugin set itself is that plugin's
        # change carried or restored, not the plugin's own: the latest such base-game provider, and whether it is the
        # record's first (the base game's original value) or a later fix
        # (fourth review, Clockwork r3 S1) the EARLIEST such provider: the plugin that set the value; a later base-game
        # plugin holding the same value (a Creation Club plugin carrying an update's value) only carries it
        base_k = next((k for k in range(1, i) if is_base_game(provs[k], words) and vals[k] == vals[i]
                       and recordrows.own_change(provs, vals, k, (masters or {}).get(str(provs[k]).lower())
                                                 if masters is not None else None)[0]), None)
        return {"changed": changed, "inherits": provs[j] if j is not None else None,
                "restoresBase": provs[base_k] if base_k is not None else None, "restoresBaseFix": bool(base_k),
                "before": provs[i - 1] if i > 0 else None,
                "beforeWins": i > 0 and vals[-1] == vals[i - 1], "listKept": bool(kept),
                "lostIsOriginal": vals[i] == vals[0]}

    def puts_back(arm_id, rk):
        """Whose change, against the nearest master that carries the record (Missives' review B2): the winning plugin
        lists the losing one as a master, its value differs from that master's (its own change), and it is the value
        that master changed away from: the winner puts back the value from before its master's change. None where the
        chain cannot tell."""
        a, b = str(rk[2]), str(rk[3])
        ms_b = (masters or {}).get(b.lower()) if masters is not None else None
        if not ms_b or a.lower() not in {str(m).lower() for m in ms_b}:
            return False
        got = field_chain(arm_id, rk, b)
        if got is None:
            return None
        provs, vals, _ = got
        low = [str(x).lower() for x in provs]
        if a.lower() not in low or b.lower() not in low:
            return None
        ib, ia = low.index(b.lower()), low.index(a.lower())
        changed_b, jb = recordrows.own_change(provs, vals, ib, ms_b)
        _, ja = recordrows.own_change(provs, vals, ia, (masters or {}).get(a.lower()))
        return bool(changed_b and jb == ia and ja is not None and vals[ib] == vals[ja])

    # A rebuilt record supersedes the loser's small fix (Pilgrim final review B1): the losing value describes a record
    # or a spot that no longer exists in that form, so forwarding it would undo the winner's rebuild. Decided from the
    # record import's own chain; the fields that say what a record is are skill data per record type.
    sup_data = words.get("supersede") or {}
    sup_kinds = sup_data.get("kindFields") or {}
    sup_factor = float(sup_data.get("relocateFactor") or 10)
    sup_place = sup_data.get("placementField") or "Placement"
    # An object moved less than its own size still stands on the spot a seat fix adjusted; where the names enrichment
    # carries no bounds for its base, 64 u (one navmesh sample spacing, a small object's size) stands in for it
    SEAT_SPOT_UNITS = 64.0
    sup_cache = {}

    def _vec(s_):
        xs = [float(x) for x in re.findall(r"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", str(s_ or ""))]
        return xs[:3] if len(xs) >= 3 else None

    def _turn(a_, b_):
        d_ = abs(a_ - b_) % (2 * math.pi)
        return min(d_, 2 * math.pi - d_)

    def spot_size(fk):
        """The object's horizontal half-extent from its base's bounds in the names enrichment, else SEAT_SPOT_UNITS."""
        base_ = name_bases.get((name_refs.get(str(fk)) or {}).get("base") or "") or {}
        bd_ = base_.get("bounds") if isinstance(base_.get("bounds"), dict) else {}
        lo_, hi_ = _vec(bd_.get("first")), _vec(bd_.get("second"))
        if lo_ and hi_:
            return max(abs(hi_[0] - lo_[0]), abs(hi_[1] - lo_[1])) / 2
        return SEAT_SPOT_UNITS

    def provider_fields_all(ch, i):
        return {k_: v_ for top_ in ch.get("values") or {} for k_, v_ in recordrows.provider_fields(ch, top_, i).items()}

    def provider_disables(ch, i):
        """Whether provider i's version of the record sets the object off for good, from its whole record: moved below
        the world, or enabled opposite to a parent that is always enabled (the player). An initially disabled object
        with no parent or another parent only starts off: a script or quest may enable it (blind review, 0A0315)."""
        f_ = provider_fields_all(ch, i)
        if disables("; ".join(f"{k_}={v_}" for k_, v_ in f_.items() if k_.endswith("Position"))):
            return True
        ref_ = str(f_.get("EnableParent.Reference") or "").strip()
        has_parent = bool(re.match(r"^[0-9A-Fa-f]{6}:", ref_))     # a form key; absent in any wording is none
        opposite_ = "Opposite" in str(f_.get("EnableParent.Flags") or "")
        return bool(has_parent and ref_.lower() in always_on and opposite_)

    def side_starts_off(rk, plugin):
        """`plugin`'s version is initially disabled but not off for good: it starts off, and a script or quest may
        enable it, so its position and values still matter then."""
        ch = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return False
        low_ = [str(x).lower() for x in ch["providers"]]
        if str(plugin).lower() not in low_:
            return False
        i_ = low_.index(str(plugin).lower())
        f_ = provider_fields_all(ch, i_)
        return (dis_flag in " ".join(str(v_) for k_, v_ in f_.items() if k_.endswith("Flags")
                                     and not k_.startswith("EnableParent"))
                and not provider_disables(ch, i_))

    # (third review, Pilgrim S1) whether the losing plugin refers to an object at all: a read-only scan of the plugin
    # file the bundle's prediction setup names, only where its bytes are the recorded ones (sha256); None where there is
    # no such read. Every read is recorded in the prediction (referenceReads).
    ref_reads, ref_cache, sha_cache = [], {}, {}

    def plugin_file(name):
        """(path, sha256) of a plugin as the prediction setup records it; (seventh review round 2) a plugin only the
        other setups load (a declared patch) as the first of them records it."""
        main_b = ctx.get("mainBundle")
        arms_ = (main_b.plan.get("arms") if main_b else None) or []
        for arm_ in sorted(arms_, key=lambda x: x.get("armId") != p_arm):
            for x in (arm_.get("requested") or {}).get("plugins") or []:
                if str(x.get("name")).lower() == str(name).lower():
                    return str(x.get("path") or ""), str(x.get("sha256") or "").lower()
        return "", ""

    def refers_to(plugin, fk):
        key_ = (str(plugin).lower(), str(fk))
        if key_ not in ref_cache:
            got_, path_, sha_ = None, *plugin_file(plugin)
            if path_ and sha_ and Path(path_).is_file():
                if path_ not in sha_cache:
                    sha_cache[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
                if sha_cache[path_] == sha_:
                    try:
                        res_ = refscan.scan(path_, [str(fk)])
                        got_ = bool(res_["refs"].get(str(fk)))
                        ref_reads.append({"plugin": plugin, "sha256": sha_, "object": str(fk),
                                          "references": res_["refs"].get(str(fk)) or []})
                    except refscan.RefScanError as e_:
                        ref_reads.append({"plugin": plugin, "sha256": sha_, "object": str(fk), "error": str(e_)})
            ref_cache[key_] = got_
        return ref_cache[key_]

    # (round 2 of the third review) record data the imports do not carry, read from the plugin file the prediction setup
    # names, only where its bytes are the recorded ones (sha256): a navmesh's edge links, a cell's DATA flags. None where
    # there is no such read; every read is recorded in the prediction (pluginReads).
    plugin_reads, pread_cache, pread_why = [], {}, {}

    def plugin_record(plugin, what, fk):
        key_ = (str(plugin).lower(), what, str(fk))
        if key_ not in pread_cache:
            got_, (path_, sha_) = None, plugin_file(plugin)
            # (item 4) why a record was not read, so a place is never said as "no name" for want of a read
            pread_why[key_] = ("noFile" if not (path_ and sha_) else "missing" if not Path(path_).is_file() else None)
            if path_ and sha_ and Path(path_).is_file():
                if path_ not in sha_cache:
                    sha_cache[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
                if sha_cache[path_] != sha_:
                    pread_why[key_] = "changed"
                if sha_cache[path_] == sha_:
                    try:
                        if what == "editorId":          # (seventh review, Missives S2) a record's editor ID
                            res_ = {"records": pluginread.editor_ids(path_, [str(fk)])}
                        elif what == "cellWorld":       # (eighth review) the worldspace a cell is filed under
                            res_ = {"records": pluginread.cell_worlds(path_, [str(fk)])}
                        elif what == "recordCell":      # (report words) the cell a record is filed under, and its type
                            res_ = {"records": pluginread.record_cells(path_, [str(fk)])}
                        else:
                            res_ = {"navmesh": pluginread.navmesh_links, "placement": pluginread.placements,
                                    "placementFull": pluginread.placements_full, "cellGrid": pluginread.cell_grids,
                                    "navmeshPlace": pluginread.navmesh_places}.get(
                                what, pluginread.cell_flags)(path_, [str(fk)])
                        got_ = res_["records"].get(str(fk))
                        plugin_reads.append({"plugin": plugin, "sha256": sha_, "record": str(fk), "read": what,
                                             "found": got_ is not None})
                        if got_ is None:
                            got_ = {}                  # read: the plugin holds no such record
                    except pluginread.PluginReadError as e_:
                        pread_why[key_] = "error"
                        plugin_reads.append({"plugin": plugin, "sha256": sha_, "record": str(fk), "read": what,
                                             "error": str(e_)})
            pread_cache[key_] = got_
        return pread_cache[key_]

    def unread_why(plugin, what, fk):
        """(item 4) Why plugin_record did not read this record (noFile, missing, changed, error); None once read."""
        plugin_record(plugin, what, fk)
        return pread_why.get((str(plugin).lower(), what, str(fk)))

    fk_any_re = re.compile(r"\b([0-9A-Fa-f]{6}):([^\s,;()\[\]]+?\.es[mpl])\b")

    def with_editor_ids(text):
        """(seventh review, Missives S2) each FormID in a value named with its editor ID, read from its defining plugin
        file (recorded bytes only); unchanged where it was not read."""
        def one(m_):
            got_ = plugin_record(m_.group(2), "editorId", f"{m_.group(1).upper()}:{m_.group(2)}")
            return f"{m_.group(0)} {got_}" if isinstance(got_, str) and got_ else m_.group(0)
        return fk_any_re.sub(one, str(text or ""))

    persistent_re = re.compile(words["persistentPlacePattern"]) if words.get("persistentPlacePattern") else None

    place_labels = {}
    # (Helgi and Ice r3 reviews) a cell the evaluated mod's own footprint records, with its worldspace and grid, and the
    # in-game names the names enrichment read: a place to look is said by them, never by an editor ID or form key alone
    footprint_cells = {}
    for t_ in ((getattr(ctx.get("mainBundle"), "plan", None) or {}).get("targets") or []):
        for c_ in (((t_.get("footprint") or {}).get("placed") or {}).get("cells") or []) if isinstance(t_, dict) else []:
            if (isinstance(c_, dict) and c_.get("cell") and c_.get("worldspace")
                    and c_.get("x") is not None and c_.get("y") is not None):
                footprint_cells.setdefault(str(c_["cell"]).lower(), c_)
    place_names = (names.get("places") or {}) if isinstance(names.get("places"), dict) else {}

    def place_reader(what, fk):
        """said_place's reads: the evaluated mod's own footprint for a cell it records, else the cell's own plugin file
        (recorded bytes only)."""
        c_ = footprint_cells.get(str(fk).lower())
        if c_ and what == "cellGrid":
            return [c_["x"], c_["y"]]
        if c_ and what == "cellWorld":
            return "footprint:" + str(c_["worldspace"])
        if what == "editorId" and str(fk).startswith("footprint:"):
            return str(fk).split(":", 1)[1]
        if what == "why":
            # "read" once the record's own plugin was read; else why it was not (noFile, missing, changed, error)
            return unread_why(str(fk).split(":", 1)[-1], "editorId", str(fk)) or "read"
        if what == "placedBase":
            got_ = plugin_record(str(fk).split(":", 1)[-1], "placementFull", str(fk))
            return (got_ or {}).get("base") if isinstance(got_, dict) else None
        got_ = plugin_record(str(fk).split(":", 1)[-1], what, str(fk))
        if what == "navmeshPlace":
            return (got_ or {}).get("place") if isinstance(got_, dict) else None
        return got_ if got_ != {} else None

    record_labels_ = ctx.get("recordLabels") or {}

    def say_record(fk):
        """(item 1) say_form_keys's sayer: the record in words (said_record over the names enrichment, the plugin files'
        names and this bundle's recorded reads) and the editor ID and label the builders print beside its key."""
        eid_ = place_reader("editorId", fk)
        return (said_record(fk, names, record_labels_, words, place_reader), eid_ if isinstance(eid_, str) else None,
                record_labels_.get(fk))
    ctx["sayRecord"] = say_record

    def named_place(label):
        return said_place(label, place_names, words, place_reader)

    def located_place(fk, label):
        got_ = placement_place(fk, label)
        return got_ if got_ != label else named_place(label)

    def placement_place(fk, label):
        """(fourth review, Missives S3) a place that is only a worldspace's persistent cell (a bare form key) says the grid
        cell of the object's position, read from the plugin file of the version that loads last (its recorded bytes)."""
        if persistent_re is None or not persistent_re.match(str(label)) or not words.get("persistentPlaceGrid"):
            return label
        order_ = [str(x) for x in all_orders.get(p_arm) or []]
        carriers_ = {str(x).lower() for x in (name_refs.get(str(fk)) or {}).get("carriedBy") or []}
        origin_ = str(fk).split(":", 1)[-1]
        cands_ = [x for x in reversed(order_) if x.lower() in carriers_] or [origin_]
        got_ = plugin_record(cands_[0], "placement", str(fk))
        pos_ = (got_ or {}).get("position")
        if not pos_:
            return label
        # (eighth review, Missives) the worldspace the persistent cell is filed under, by its editor ID, read from the
        # cell's own plugin file; the grid alone would send a modder to another worldspace's cell
        m_ = re.match(r"^([0-9A-Fa-f]{6}):(\S+)", str(label))
        w_ = plugin_record(m_.group(2), "cellWorld", f"{m_.group(1).upper()}:{m_.group(2)}") if m_ else None
        w_name_ = (plugin_record(str(w_).split(":", 1)[1], "editorId", str(w_))
                   if isinstance(w_, str) and ":" in w_ else None)
        if isinstance(w_name_, str) and w_name_ and words.get("persistentPlaceGridWorld"):
            return bind(words["persistentPlaceGridWorld"], {"world": (place_names.get(w_name_) or {}).get("name")
                                                            or id_words(w_name_),
                                                            "x": fmt_n(math.floor(pos_[0] / 4096)),
                                                            "y": fmt_n(math.floor(pos_[1] / 4096))})
        return bind(words["persistentPlaceGrid"], {"x": fmt_n(math.floor(pos_[0] / 4096)),
                                                   "y": fmt_n(math.floor(pos_[1] / 4096))})

    def winner_on_at_load(rk):
        """"on": the winner's whole version has the object on when the game loads; "parent": its own record does not
        switch it off but it has an enable parent; "off": it switches it off itself; None: the chain does not hold it."""
        ch = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return None
        low_ = [str(x).lower() for x in ch["providers"]]
        if str(rk[3]).lower() not in low_:
            return None
        f_ = provider_fields_all(ch, low_.index(str(rk[3]).lower()))
        flags_ = " ".join(str(v_) for k_, v_ in f_.items() if k_.endswith("Flags") and not k_.startswith("EnableParent"))
        nums_ = [int(x_) for x_ in re.findall(r"(?<![\w.])(\d+)(?![\w.])", flags_)]
        if (dis_flag in flags_ or "Deleted" in flags_ or any(n_ & 0x820 for n_ in nums_)
                or disables("; ".join(f"{k_}={v_}" for k_, v_ in f_.items() if k_.endswith("Position")))):
            return "off"
        pos_ = re.findall(r"-?\d+(?:\.\d+)?", str(f_.get("Placement.Position") or ""))
        if len(pos_) >= 3 and float(pos_[2]) <= -30000:
            return "off"
        if re.match(r"^[0-9A-Fa-f]{6}:", str(f_.get("EnableParent.Reference") or "").strip()):
            return "parent"
        return "on"

    # (fourth review) What a lost value means in game is read once, from the whole records: each side's state at load,
    # whether the winner placed the object itself, and whether a mod undoing its master's fix changed the record itself.
    # Every sentence about a lost value's meaning takes its words from these (Winterhold S1, S2; Snazzy r3 S2; BPT r3 S2).
    lm_words = words.get("lostMeaning") or {}

    def version_state(ch, i):
        """Provider i's whole version: "off" (off for good), "starts" (only initially disabled), "parent" (on in its own
        record, under an enable parent), or "on"."""
        if provider_disables(ch, i):
            return "off"
        f_ = provider_fields_all(ch, i)
        flags_ = " ".join(str(v_) for k_, v_ in f_.items() if k_.endswith("Flags") and not k_.startswith("EnableParent"))
        if dis_flag in flags_ or any(int(x_) & 0x800 for x_ in re.findall(r"(?<![\w.])(\d+)(?![\w.])", flags_)):
            return "starts"
        if re.match(r"^[0-9A-Fa-f]{6}:", str(f_.get("EnableParent.Reference") or "").strip()):
            return "parent"
        return "on"

    def side_state(rk, plugin, arm_id=None):
        """version_state of `plugin`'s version of the row's record in that setup; None where the chain does not hold it."""
        ch = (chains_of(rk[0], arm_id or p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return None
        low_ = [str(x).lower() for x in ch["providers"]]
        return version_state(ch, low_.index(str(plugin).lower())) if str(plugin).lower() in low_ else None

    def winner_placed(rk):
        """{across, rebased} where the winner's version is its own placement of the object: another base than the one the
        losing plugin inherits, or moved across at least the object's size (the supersede measure) from where the losing
        plugin inherits it; None otherwise or where the chain cannot tell."""
        ch = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return None
        provs = [str(x).lower() for x in ch["providers"]]
        if rk[2].lower() not in provs or rk[3].lower() not in provs:
            return None
        ia, ib = provs.index(rk[2].lower()), provs.index(rk[3].lower())
        _, j = recordrows.own_change(ch["providers"], [0] * len(provs), ia,
                                     (masters or {}).get(rk[2].lower()) if masters is not None else None)
        if j is None:
            return None
        bases_ = (ch.get("values") or {}).get(lm_words.get("baseField") or "Base")
        rebased = bool(bases_) and bases_[ib] != bases_[j]
        pj = _vec(next((v_ for k_, v_ in recordrows.provider_fields(ch, sup_place, j).items() if k_.endswith("Position")), None))
        pb = _vec(next((v_ for k_, v_ in recordrows.provider_fields(ch, sup_place, ib).items() if k_.endswith("Position")), None))
        across = math.dist(pj[:2], pb[:2]) if pj and pb else None
        moved = across is not None and across >= spot_size(rk[4])
        return {"across": across if moved else None, "rebased": rebased} if (rebased or moved) else None

    def reworked(arm_id, rk):
        """The fields of skill data `reworkFields` (other than the row's own) the winner changed itself: its value differs,
        as the record chain reads it (path case, unused bytes and rounding folded), from the one it inherits and from the
        value before that plugin's change. Where a mod undoes its master's fix, such a change says it rebuilt the object."""
        ch = (chains_of(rk[0], arm_id) or {}).get(str(rk[4]))
        if not ch:
            return []
        provs = [str(x).lower() for x in ch["providers"]]
        if rk[2].lower() not in provs or rk[3].lower() not in provs:
            return []
        ia, ib = provs.index(rk[2].lower()), provs.index(rk[3].lower())
        field_ = str(((p_rows.get(rk) or {}).get("row") or {}).get("field") or "")
        _, ja = recordrows.own_change(ch["providers"], [0] * len(provs), ia,
                                      (masters or {}).get(rk[2].lower()) if masters is not None else None)
        out_ = []
        for t_ in lm_words.get("reworkFields") or []:
            vals_ = (ch.get("values") or {}).get(t_)
            if t_ == field_ or not vals_:
                continue
            if vals_[ib] != vals_[ia] and (ja is None or vals_[ib] != vals_[ja]):
                out_.append(t_)
        return out_

    def bare_start_off(rk, plugin):
        """`plugin`'s version only starts the object disabled, with no enable parent at all."""
        if not side_starts_off(rk, plugin):
            return False
        ch = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        low_ = [str(x).lower() for x in ch["providers"]]
        f_ = provider_fields_all(ch, low_.index(str(plugin).lower()))
        return not re.match(r"^[0-9A-Fa-f]{6}:", str(f_.get("EnableParent.Reference") or "").strip())

    def side_own_removal(rk, plugin, arm_id=None):
        """(fifth review, BPT S2) `plugin`'s version sets the object off for good by its own change: its version is off
        for good and the version it inherits from its masters is not; False where the chain does not hold it."""
        ch = (chains_of(rk[0], arm_id or p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return False
        low_ = [str(x).lower() for x in ch["providers"]]
        if str(plugin).lower() not in low_:
            return False
        i_ = low_.index(str(plugin).lower())
        _, j_ = recordrows.own_change(ch["providers"], [0] * len(low_), i_,
                                      (masters or {}).get(str(plugin).lower()) if masters is not None else None)
        return j_ is not None and provider_disables(ch, i_) and not provider_disables(ch, j_)

    def side_disables(rk, plugin, arm_id=None):
        """Whether `plugin`'s version of the row's record disables the object (provider_disables); None where the
        record import's chain does not hold it."""
        ch = (chains_of(rk[0], arm_id or p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return None
        low_ = [str(x).lower() for x in ch["providers"]]
        return provider_disables(ch, low_.index(str(plugin).lower())) if str(plugin).lower() in low_ else None

    def supersede(rk):
        """The record facts that make the row's losing value superseded ({kind, ...}), or None. (a) kind: the winner
        changed a field that says what the record is, the loser left it as it inherited it, and the lost field is not
        one of those fields. (b) place: the loser only seated the object (moved it straight up or down, not turned,
        not disabled) and the winner moved it across to another spot: horizontally at least `relocateFactor` times the
        seat and at least the object's size, more across than down, and without disabling it. Also {kind: undecided}
        where the lost field describes the record and the winner rewrote it but kept the record's kind."""
        if rk in sup_cache:
            return sup_cache[rk]
        sup_cache[rk] = None
        if rk[1] != "LOST-EDIT":
            return None
        row_ = (p_rows.get(rk) or {}).get("row") or {}
        field_ = str(row_.get("field") or "")
        ch = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch:
            return None
        provs = [str(x).lower() for x in ch["providers"]]
        if rk[2].lower() not in provs or rk[3].lower() not in provs:
            return None
        ia, ib = provs.index(rk[2].lower()), provs.index(rk[3].lower())
        _, j = recordrows.own_change(ch["providers"], [0] * len(provs), ia,
                                             (masters or {}).get(rk[2].lower()) if masters is not None else None)
        if j is None or ib <= ia:
            return None
        spec = sup_kinds.get(ch.get("type")) or {}
        kinds = [k_ for k_ in spec.get("fields") or [] if k_ in (ch.get("values") or {})]
        if spec and field_ not in (spec.get("fields") or []):
            moved = [k_ for k_ in kinds if ch["values"][k_][ib] != ch["values"][k_][j]]
            kept = all(ch["values"][k_][ia] == ch["values"][k_][j] for k_ in kinds)
            if moved and kept:
                changes = []
                for k_ in moved + [x for x in spec.get("alsoSay") or [] if x in (ch.get("values") or {})]:
                    old_, new_ = (recordrows.provider_fields(ch, k_, j), recordrows.provider_fields(ch, k_, ib))
                    for leaf_ in sorted(set(old_) | set(new_)):
                        # only the leaves the skill data names in plain words are said (an association key is not)
                        if old_.get(leaf_) != new_.get(leaf_) and leaf_ in (spec.get("leaves") or {}):
                            changes.append(((spec.get("leaves") or {}).get(leaf_, leaf_), old_.get(leaf_),
                                            new_.get(leaf_)))
                sup_cache[rk] = {"kind": "kind", "noun": spec.get("noun") or "record", "changes": changes,
                                 "field": field_}
                return sup_cache[rk]
            if (not moved and field_ in (spec.get("describing") or [])
                    and (own_edit(p_of(rk[0]), rk, rk[3]) or {}).get("changed")):
                sup_cache[rk] = {"kind": "undecided"}
            return sup_cache[rk]
        if field_ != sup_place or sup_place not in (ch.get("values") or {}):
            return None
        f_ = [recordrows.provider_fields(ch, sup_place, k_) for k_ in (j, ia, ib)]
        pos = [_vec(next((v_ for k_, v_ in x.items() if k_.endswith("Position")), None)) for x in f_]
        rot = [_vec(next((v_ for k_, v_ in x.items() if k_.endswith("Rotation")), None)) for x in f_]
        if not all(pos):
            return None
        (o_, l_, w_) = pos
        if max(abs(l_[0] - o_[0]), abs(l_[1] - o_[1])) > 0.01 or abs(l_[2] - o_[2]) <= 0:
            return None                        # the loser's change is no seat: it moves the object sideways
        if rot[0] and rot[1] and max(_turn(a_, b_) for a_, b_ in zip(rot[0], rot[1])) > 0.001:
            return None                        # the loser's change turns the object
        if rot[0] is None and rot[1] is not None or rot[1] is None and rot[0] is not None:
            return None
        if provider_disables(ch, ia) or provider_disables(ch, ib):
            return None                        # a disabled object is no new spot; said by the disabled rules
        seat = abs(l_[2] - o_[2])
        across = math.dist(o_[:2], w_[:2])
        down = abs(w_[2] - o_[2])
        if across >= sup_factor * seat and across >= down and across >= spot_size(rk[4]):
            sup_cache[rk] = {"kind": "place", "seat": seat, "up": l_[2] > o_[2], "across": across,
                             "moved": math.dist(o_, w_)}
        return sup_cache[rk]

    lv_words = words.get("lostEditValuePatterns") or {}
    order_only_re = re.compile(lv_words["orderOnly"]) if lv_words.get("orderOnly") else None
    list_diff_re = re.compile(lv_words["listDiff"]) if lv_words.get("listDiff") else None
    judged_by = [x for x in words.get("lostEditJudgedBy") or [] if isinstance(x, dict)]

    def carried_list(v, b):
        """Every entry a list comparison whose winning list keeps every item of the losing list and adds only items of
        the winning plugin's own (FormKeys in it): no plugin before the losing one can hold those, so the losing mod's
        additions and removals both stand; its change is carried forward, not lost (Clockwork review 3, P105)."""
        items = v if isinstance(v, list) else [v]
        if not items or list_diff_re is None:
            return False
        for it in items:
            m = list_diff_re.match(str(it))
            if not m or "(+" in m.group("rest"):
                return False
            parts = re.split(r";\s*only ", m.group("rest"))
            if len(parts) != 1 or not m.group("rest").startswith("only in "):
                return False                   # an item only in the losing list (lost), or another difference
            listed = m.group("rest").split(":", 1)[1] if ":" in m.group("rest") else ""
            for x in listed.split(";"):
                fk = re.search(r"\]\s*([0-9A-Fa-f]{6}:(.+?))\s*$", x)
                if not fk or fk.group(2).strip().lower() != str(b).lower():
                    return False
        return True

    def read_bits(arm_id, rk, top):
        """The bits the game reads of the row's masked field ({leaf: int}) in the losing and the winning plugin's
        versions, from the record import's chain of that setup; None where the chain does not hold both."""
        masks = field_masks().get(record_type_of(rk[5])) or {}
        ch = (chains_of(rk[0], arm_id) or {}).get(str(rk[4]))
        if not ch or not masks:
            return None
        low_ = [str(x).lower() for x in ch["providers"]]
        if rk[2].lower() not in low_ or rk[3].lower() not in low_:
            return None
        got_ = []
        for p_ in (rk[2], rk[3]):
            f_ = recordrows.provider_fields(ch, top, low_.index(p_.lower()))
            try:
                got_.append({k: int(v) & int(masks[k]) for k, v in f_.items() if k in masks})
            except ValueError:
                return None
        return tuple(got_) if got_[0] and got_[1] and set(got_[0]) == set(got_[1]) else None

    def door_link_off_winner(rk, arm_id):
        """(tenth review, Snazzy S1) The losing plugin's door link (its version's XNDP: navmesh, triangle) is not listed for
        the door by the version of that navmesh that loads last in the setup (its door triangles, read from the plugin
        files). False where it is listed, and where any file needed is not read (nothing decided)."""
        link_ = (plugin_record(rk[2], "placementFull", rk[4]) or {}).get("doorLink")
        if not link_:
            return False
        for pl_ in reversed(all_orders.get(arm_id) or []):
            nav_ = plugin_record(pl_, "navmesh", link_[0])
            if nav_ is None:
                return False                   # a file not read: nothing decided
            if nav_:
                return [str(rk[4]).lower(), int(link_[1])] not in [[str(d_).lower(), int(t_)]
                                                                     for d_, t_ in nav_.get("doors") or []]
        return False

    flag_base_rows = {}                        # id(rows) -> (check, A, B, record) of each base-field flags row

    def lost_edit_noise(rk, rows=None, arm=None):
        """A lost-edit row whose values show no change the game sees, or whose subject another check judges: lists
        holding the same items in another order (lists are compared as sets of items, the record lane's rule; P108), a
        list the winner keeps in full (P105), a change only in bytes the game does not read (P21), and a navmesh's
        geometry, which the navmesh check judges by links and lost ground (P103). Kept in the record, said apart."""
        rows_ = p_rows if rows is None else rows
        r_ = (rows_.get(rk) or {}).get("row") or {}
        v = r_.get("values") or {}
        lost = v.get("lost")
        top = str(r_.get("field") or "")
        rtype = record_type_of(rk[5])
        # record flags, by the record stage's rule (record-field-scope.json recordFlags): a difference only in the
        # compressed-storage bit is how a record is stored, not what the game does; the same header bits under a
        # per-type name are one change, judged on the base field where that row exists
        if storage_flag_only(top, lost, v.get("overridden")) or storage_flag_only(top, lost, v.get("winner")):
            return "storageFlag"
        rf_ = record_flags_rule()
        if top in rf_["views"] and rf_["field"]:
            key_ = id(rows_)
            if key_ not in flag_base_rows:
                flag_base_rows[key_] = {(k_[0], str(k_[2]).lower(), str(k_[3]).lower(), str(k_[4]))
                                        for k_, h_ in rows_.items()
                                        if str(((h_ or {}).get("row") or {}).get("field") or "") == rf_["field"]}
            if (rk[0], str(rk[2]).lower(), str(rk[3]).lower(), str(rk[4])) in flag_base_rows[key_]:
                return "sameFlagsView"
        for j_ in judged_by:
            if (rtype == j_.get("recordType") and top == j_.get("field")
                    and (j_.get("check"), arm or p_of(j_.get("check"))) in measured):
                return "navmeshCheck"
        # (tenth review, Snazzy S1) a door's navmesh door link names a navmesh and its triangle at the door; it is one
        # link only with the version of that navmesh that wins. A losing value that version does not list for the door
        # belongs with a navmesh version that does not win: no clash to forward. Read from the plugin files; a file not
        # read decides nothing
        dl_ = words.get("doorLinkNeedsNavmesh") or {}
        if rtype == dl_.get("recordType") and top in (dl_.get("fields") or []) and door_link_off_winner(
                rk, arm or p_of(rk[0])):
            return "doorLinkNavmesh"
        # (sixth review, Snazzy S1; gate A condition R3) a water field on an interior cell neither side of which has
        # water (its HasWater flag, read from both plugin files): no change in game. Exterior cells are not judged so
        wn_ = words.get("waterNeedsFlag") or {}
        if rtype == wn_.get("recordType") and top in (wn_.get("fields") or []):
            fl_ = [plugin_record(pl_, "cell", rk[4]) for pl_ in (rk[2], rk[3])]
            if all(f_ and f_.get("value") is not None and wn_.get("interiorFlag") in (f_.get("flags") or [])
                   and wn_.get("flag") not in (f_.get("flags") or []) for f_ in fl_):
                return "noWater"
        if order_only_re is not None and lost and all(order_only_re.match(str(x))
                                                      for x in (lost if isinstance(lost, list) else [lost])):
            return "orderOnly"
        if carried_list(lost, rk[3]):
            return "carriedList"
        if masked_same(lost, v.get("overridden"), rtype, top):
            # the losing mod changed only bytes the game does not read; where the winner's read bits differ from the
            # value the losing mod carries, that is the winner's own change, said on its own line (Missives review 1)
            rb_ = read_bits(arm or p_of(rk[0]), rk, top)
            return "winnerOwnChange" if rb_ is not None and rb_[0] != rb_[1] else "unusedBits"
        if same_in_chain(arm or p_of(rk[0]), rk):
            return "sameAsRead"                # the import's chain reads both values the same (case, bytes, rounding)
        return None

    one_sided_res = {k: re.compile(v) for k, v in (words.get("oneSidedKinds") or {}).items() if k != "about"}

    def one_sided(rk):
        """Whose change a seam row is: only one side differs from the base game, so the plugin on the other side
        carries the base game's value and made no change of its own there (skill data `oneSidedKinds`)."""
        r_ = one_sided_res.get(state_split(rk[1])[0])
        return bool(r_ and r_.search(str(rk[5])))

    link_re = re.compile(r"^navmesh (\S+) in .* edge-links (\S+) triangle (\d+) of \d+\s+\[record provided by: ([^\]]+)\]")

    def link_origin(rk):
        """(round 2, Sewers B1 and Clockwork S1) Who made a broken link of a base-game navmesh record that another,
        not evaluated, mod carries (the probe's provider chain: a base-game plugin first, that mod last): read from the
        edge links of each provider's version in its plugin file, the link being (linking navmesh, target navmesh, target
        triangle). ("base", None): a base-game version holds it; ("added", plugin): the first other provider whose version
        holds it; ("unread", None): a version needed was not read; None: not such a row."""
        m_ = link_re.match(str(rk[5] or "")) if rk[1] == "EDGE-IDX" else None
        if not m_:
            return None
        provs_ = [x.strip() for x in m_.group(4).split(" -> ")]
        if (len(provs_) < 2 or not is_base_game(provs_[0], words) or provs_[-1].lower() != str(rk[2]).lower()
                or str(rk[2]).lower() in pt_lower or is_base_game(rk[2], words)):
            return None
        link_ = (m_.group(2).upper(), int(m_.group(3)))
        for pv_ in provs_:
            got_ = plugin_record(pv_, "navmesh", m_.group(1))
            if got_ is None:
                return ("unread", None)
            if link_ in {(str(t_).upper(), int(n_)) for _, t_, n_ in got_.get("links") or []}:
                return ("base", None) if is_base_game(pv_, words) else ("added", pv_)
        return ("unread", None)

    base_link_unsure = {}                         # row -> why it is not separated (the winner without the carrier unread)

    def base_link_alone(rk):
        """(Helgi, three review rounds) whether a base link the carrier holds breaks without that carrier, so it is the
        evaluated mod's against the base game: the measured setup without the carrier decides; unmeasured, the version
        that wins with the carrier left out (the provider before it in the chain, read from its plugin file) must hold
        the link. True, False, or None where that version was not read."""
        arm_c_ = without_other(str(rk[2]).lower())
        if arm_c_ is not None and (rk[0], arm_c_) in measured and sib_check_ok(arm_c_, rk[0]):
            return there(arm_c_, rk)
        m_ = link_re.match(str(rk[5] or ""))
        provs_ = [x.strip() for x in m_.group(4).split(" -> ")]
        got_ = plugin_record(provs_[-2], "navmesh", m_.group(1))
        if got_ is None:
            return None
        return (m_.group(2).upper(), int(m_.group(3))) in {(str(t_).upper(), int(n_)) for _, t_, n_ in got_.get("links") or []}

    def base_link_cut(rk):
        """(fifth review, Sewers S2) a base link the evaluated mod breaks: the base game's own version of the target
        navmesh (its origin plugin, read from the file) has the triangle the link points at, and the row's own count of
        the target's triangles (the evaluated mod's version) is at most that index."""
        m_ = re.search(r"edge-links (\S+) triangle (\d+) of (\d+)", str(rk[5] or ""))
        if not m_:
            return False
        tgt_, tri_, now_ = m_.group(1), int(m_.group(2)), int(m_.group(3))
        origin_ = tgt_.split(":", 1)[-1]
        if not is_base_game(origin_, words):
            return False
        got_ = plugin_record(next((x for x in all_orders.get(p_arm) or [] if x.lower() == origin_.lower()), origin_),
                             "navmesh", tgt_)
        return bool(got_ and got_.get("triangles") is not None and tri_ < int(got_["triangles"]) and now_ <= tri_)

    def list_items_(v):
        """The items a list delta says only this version holds ("only here: [i] X; ..."); None where the delta is cut
        short ("more element(s)") or names none."""
        t_ = " ".join(map(str, v)) if isinstance(v, (list, tuple)) else str(v or "")
        if "more element" in t_:
            return None
        m_ = re.search(r"only here:(.*?)(?:only in |$)", t_)
        got_ = set(re.findall(r"\[\d+\]\s*([0-9A-Fa-f]{6}:[^;,()\]]+?)\s*(?=;|,|\(|$)", m_.group(1))) if m_ else set()
        return got_ or None

    def list_drop(rk):
        """(sixth review, Missives r5 S2; gate A condition R4) an evaluated mod's list that drops items the base game's
        version holds, against a losing version that holds the same items as the base game's (in any order): the
        evaluated mod's own change against the base game, never a clash of the losing plugin's. The dropped items, or
        None."""
        if rk[1] != "LOST-EDIT" or rk[3].lower() not in pt_lower:
            return None
        f_ = str(((p_rows.get(rk) or {}).get("row") or {}).get("field") or "")
        ch_ = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
        if not ch_ or f_ not in (ch_.get("values") or {}):
            return None
        low_ = [str(x).lower() for x in ch_["providers"]]
        if rk[2].lower() not in low_ or low_[-1] != rk[3].lower() or not is_base_game(ch_["providers"][0], words):
            return None
        vals_ = ch_["values"][f_]
        bi_, li_ = list_items_(vals_[0]), list_items_(vals_[low_.index(rk[2].lower())])
        return sorted(bi_) if bi_ and li_ == bi_ else None

    own_layout_cache, own_layout_by = {}, {}

    def own_layout(rk, other):
        """(ninth review, Winterhold F-039) A fixture left hanging whose winning version (on) and whose removed host's
        winning version (off for good, or deleted) are the same plugin's, read from the prediction setup's plugin files
        (recorded bytes only): it hangs as that plugin ships it, whichever other mod the probe names. The plugin, or None
        (another plugin's host, or a version not read)."""
        if rk[0] not in set(words.get("ownLayoutChecks") or []) or not other:
            return None
        key_ = (str(rk[4]), tuple(map(str, other)))
        if key_ not in own_layout_cache:
            mb_ = ctx.get("mainBundle")
            plugs_ = next(((a_.get("requested") or {}).get("plugins") or [] for a_ in ((mb_.plan.get("arms") if mb_ else None) or [])
                           if a_.get("armId") == p_of(rk[0])), [])
            got_ = None
            fx_ = setup_version(plugs_, str(rk[4]), plugin_reads)
            if fx_ and fx_[0] and not (placement_off_for_good(fx_[1], words) or fx_[1].get("deleted")):
                for o_ in other:
                    ho_ = setup_version(plugs_, str(o_), plugin_reads)
                    if (ho_ and ho_[0] and str(ho_[0]).lower() == str(fx_[0]).lower()
                            and (placement_off_for_good(ho_[1], words) or ho_[1].get("deleted"))):
                        got_ = ho_[0]
                        break
            own_layout_cache[key_] = got_
        return own_layout_cache[key_]

    def partner_alone(rk):
        """(Missives final report review, finding 1) a clash of a compared mod against the base game, there without the
        evaluated mod but gone in the measured setup without that compared mod: the compared mod's own problem, not one
        already present without the evaluated mods. The compared mod, or None."""
        a_, b_ = rk[2], rk[3]
        if not (is_base_game(a_, words) ^ is_base_game(b_, words)):
            return None
        m_ = b_ if is_base_game(a_, words) else a_
        if m_.lower() in pt_lower:
            return None
        arm_m_ = without_other(m_.lower())
        if arm_m_ is None or (rk[0], arm_m_) not in measured or there(arm_m_, rk):
            return None
        partner_alone_mods.setdefault(m_, None)
        return m_

    partner_alone_mods = collections.OrderedDict()

    def admission(rk, history, other=()):
        a, b = rk[2], rk[3]
        if history:
            return "history"
        if a.lower() == b.lower():
            return "sameMod"
        if list_drop(rk):
            return "winnerListDrop"
        if there(w_arm, rk):
            if partner_alone(rk):
                return "partnerAlone"
            return "alreadyPresent"
        if rk[1] == "EDGE-IDX" and (link_origin(rk) or ("",))[0] == "base":
            # (round 2, Sewers B1) the base game's own version of the linking navmesh holds the link and the other mod
            # only carries it: the evaluated mod against the base game, not a clash with the mod that carries the record,
            # but only where it breaks without that mod too (Helgi); otherwise the setups attribute it
            alone_ = base_link_alone(rk)
            if alone_:
                return "baseLink"
            if alone_ is None:
                m_ = link_re.match(str(rk[5] or ""))
                base_link_unsure[rk] = bind(words["notSeparableWhy"]["linkWinnerUnread"], {
                    "navmesh": m_.group(1), "carrier": rk[2], "plugin": [x.strip() for x in m_.group(4).split(" -> ")][-2],
                    "mod": rk[3]})
        if one_sided(rk):
            # new with the evaluated mods and only one side differs: their side against the base game's value
            return "baseGame"
        if (rk[1] == "LOST-EDIT" and {a.lower(), b.lower()} & pt_lower and not is_base_game(b, words)
                and puts_back(p_of(rk[0]), rk)):
            # the winner puts back the value from before its own master's change (Missives review B2): its own change,
            # whose effect is that master's fix lost in game. A plugin built on its master is no clash between mods
            # (P63; coordinator D1): said on its own line as undoing that master's fix, never in the clash counts
            put_back.add(rk)
            return lost_edit_noise(rk) or "undoesMaster"
        if ((is_base_game(a, words) or is_base_game(b, words)) and rk[1] != "LOST-EDIT"
                and any(bodiless(o_) and (name_bases.get((name_refs.get(str(o_)) or {}).get("base") or "") or {}).get("type")
                        in set(words.get("bodilessTypes") or []) for o_ in [rk[4], *(other or [])])):
            # (BPT re-review S3) an object of a type that has no body without a model (skill data: a light), against a
            # base-game object too; a marker without a model may still block by a collision primitive, so not by type
            return "noBody"
        if is_base_game(a, words) or is_base_game(b, words):
            return "baseGame"
        if not ({a.lower(), b.lower()} & pt_lower):
            return "notInvolving"
        if master_of(masters, a, b) and not is_geometry_check(rk[0], words):
            return "ownMaster"                 # a plugin's record value over its own master's: it wins as intended
        if no_body(rk, other):
            return "noBody"                    # a clash between the mods but for an object with no model (no body)
        if body_unread(rk, other):
            return "bodyUnread"                # cannot tell: its base record was not read
        if rk[1] == "LOST-EDIT" and path_case_only(rk):
            return "pathCase"                  # the two values name the same file (Snazzy review 3)
        if rk[1] == "LOST-EDIT" and both_disabled(rk):
            return "bothDisabled"              # both mods disable the object: no effect in game
        if (rk[1] == "LOST-EDIT" and bare_start_off(rk, rk[2]) and side_disables(rk, rk[3])
                and refers_to(rk[2], rk[4]) is False):
            # (third review, Pilgrim S1) the loser only starts it disabled and nothing in the loser refers to it, so it
            # stays off in the loser's game too; the winner sets it off for good: no effect in game
            return "offUnreferenced"
        if (rk[1] == "LOST-EDIT" and bare_start_off(rk, rk[3]) and side_disables(rk, rk[2])
                and refers_to(rk[3], rk[4]) is False):
            # (fourth review, Clockwork r3 S3) the mirror: the winner only starts it disabled and no record of the winning
            # plugin refers to it; the loser sets it off for good: off in both versions, as far as their records show
            unref_winners.setdefault(rk[3], None)
            return "offUnreferencedWinner"
        if rk[1] == "LOST-EDIT":
            noise = lost_edit_noise(rk)
            if noise:
                return noise
            oe = own_edit(p_of(rk[0]), rk, rk[2])
            if oe is not None and not oe["changed"]:
                return "notOwnEdit"            # the losing value is the one it inherits: no edit of that mod's
            if oe is not None and oe.get("restoresBaseFix"):
                # a later base-game plugin's own change (a DLC, update or CC value) it carries or restores (Missives S1);
                # the record's first value put back over one's own master stays that mod's change (C968, lane core)
                return "notOwnEdit"
            if oe is not None and oe.get("listKept"):
                return "listKept"              # every item it added or removed the winning list keeps so
            if (supersede(rk) or {}).get("kind") in ("kind", "place"):
                return "superseded"            # the winner rebuilt the record after the loser's small fix
        # (fifth review, Winterhold S3) the same rule for a quest-or-switch row of a fixture left in empty space
        if ((rk[0] == "geometry.fixture-empty-space"
             or str(rk[1]).split("@", 1)[0] in set(words.get("fixtureAnchorUnclearKinds") or []))
                and anchor_unclear_re is not None and anchor_unclear_re.search(rk[5])):
            return "anchorUnclear"             # a floor or wall's box reaches it though no touch was found: cannot tell
        # (ninth review, Winterhold F-039) one plugin's versions win for the fixture and its removed host: its own layout
        lay_ = own_layout(rk, other)
        if lay_:
            own_layout_by[str(rk[4])] = lay_
            return "ownLayout"
        return "admitted"

    without_cache, partner_arms, put_back = {}, {}, set()
    fewer_setup_of = {}                         # row -> the fewer-plugins setup that decided its attribution
    unref_winners = collections.OrderedDict()

    def without_other(m_lower):
        """The setup equal to P without one other mod (and its companions), in this bundle or the sibling."""
        if m_lower not in without_cache:
            found = arm_without({m_lower})
            if found is None and sib_role_ref:
                found = sib_role_ref[0]({m_lower})
            without_cache[m_lower] = found
        return without_cache[m_lower]

    def partner_owns(rk, other):
        """(round 2, F1) whether the row's other side is the other mod's own record (a form it adds, so the clash
        cannot exist without it): the row's object or a partner object whose FormKey names that plugin."""
        row_ = (p_rows.get(rk) or {}).get("row") or {}
        keys_ = [rk[4], *(row_.get("other") or [])]
        return any(":" in str(k_) and str(k_).split(":", 1)[1].lower() == str(other).lower() for k_ in keys_)

    def absent_on_fewer(rk, t, other):
        """A clash absent on a setup that leaves out the other mod and more: it needs the other mod only where its
        other side is that mod's own record; otherwise it needs one or more of the plugins the setup leaves out."""
        return ("needs-both" if partner_owns(rk, other) else "needs-some"), t, None

    def setup_phrase(arm_id):
        """A measured setup in plugins, against the setup without the patch: what it holds, or what it lacks."""
        plugs = all_orders.get(arm_id) or []
        low = {x.lower() for x in plugs}
        kept = [x for x in plugs if not is_base_game(x, words)]
        removed = [x for x in all_orders.get(p_arm) or [] if x.lower() not in low]
        return (words["setupBaseGameAlone"] if not kept else
                bind(words["setupOnly"], {"plugins": " and ".join(kept)}) if len(kept) <= len(removed) else
                bind(words["setupWithout"], {"plugins": ", ".join(removed)}))

    def attribution(rk):
        sides = [x for x in (rk[2], rk[3]) if x.lower() in pt_lower]
        if rk in base_link_unsure:
            return "not-separable", None, base_link_unsure[rk]
        if len(p_targets) <= 1:
            # One evaluated mod t, a clash with another mod m: does it need m too? Only the setup without m can say
            # (Snazzy review, P38): absent there, it needs both; present there, it comes from t; not measured, the
            # report says it appears when t is added and no more.
            t = sides[0] if sides else None
            others = [x for x in (rk[2], rk[3]) if x.lower() not in pt_lower]
            if not t or not others:
                return "single-mod", t, None
            arm_m = without_other(others[0].lower())
            if arm_m is not None and sib_check_ok(arm_m, rk[0]):
                # named in the record: which setup said so (a sibling's only where its check is joined, round 2)
                partner_arms.setdefault(others[0], arm_m)
            if arm_m is not None and (arm_m, rk[0]) in sib_disagree:
                return "not-separable", None, words["notSeparableWhy"]["siblingsDisagree"]
            if (arm_m is None or (rk[0], arm_m) not in measured
                    or not sib_check_ok(arm_m, rk[0])):
                # no setup equal to this one without the other mod: a measured setup of fewer of this setup's plugins that
                # keeps the evaluated mod and leaves the other out still answers whether the clash needs it (the setups
                # that pair the mod with each partner alone)
                p_low_ = {x.lower() for x in all_orders.get(p_arm) or []}
                # (item 21) a joined sibling's setup counts too, read only where that sibling's check is joined
                alt_ = [a_ for a_, o_ in sorted(all_orders.items()) if a_ != p_arm
                        and (sib_split(a_)[0] is None or (sib_usable_arm(a_) and sib_check_ok(a_, rk[0])))
                        and (rk[0], a_) in measured and t.lower() in {x.lower() for x in o_}
                        and others[0].lower() not in {x.lower() for x in o_} and {x.lower() for x in o_} <= p_low_]
                if not alt_:
                    return "with-target", t, None
                if any((a_, rk[0]) in sib_disagree for a_ in alt_):
                    return "not-separable", None, words["notSeparableWhy"]["siblingsDisagree"]
                decider_ = next((a_ for a_ in alt_ if there(a_, rk)), None)
                partner_arms.setdefault(others[0], decider_ or alt_[0])
                # the page names the setup that decided: it leaves out more than the other mod (item 21, A2)
                fewer_setup_of[rk] = decider_ or alt_[0]
                return ("single-mod", t, None) if decider_ else absent_on_fewer(rk, t, others[0])
            # (item 21, A2) a setup without the other mod that also lacks plugins taken as its companions is named
            # on the page, never called "the setup without" the other mod alone
            fewer_m_ = {str(x).lower() for x in all_orders.get(arm_m) or []} != p_set - {others[0].lower()}
            if there(arm_m, rk):
                if fewer_m_:
                    fewer_setup_of[rk] = arm_m
                return "single-mod", t, None
            # (seventh review, BPT r6 S3) a lost record change where the measured setup without the other mod also lacks
            # other plugins: judged by the prediction setup's own versions with the other mod's version left out. Where
            # a later plugin's different value still wins over the losing value there, it does not need the other mod.
            if (rk[1] == "LOST-EDIT" and rk[2].lower() == str(t).lower()
                    and {str(x).lower() for x in all_orders.get(arm_m) or []} != p_set - {others[0].lower()}):
                got_ = whose_value(p_arm, rk[0], rk[4], ((p_rows.get(rk) or {}).get("row") or {}).get("field"), rk[2])
                if got_ is not None:
                    provs_, vals_, _ = got_
                    low_ = [str(x).lower() for x in provs_]
                    rest_ = [k_ for k_ in range(len(low_)) if low_[k_] != others[0].lower()]
                    i_ = low_.index(rk[2].lower())
                    if rest_ and rest_[-1] > i_ and vals_[rest_[-1]] != vals_[i_]:
                        miss_ = sorted((x for x in all_orders.get(p_arm) or [] if x.lower() not in
                                        {str(y).lower() for y in all_orders.get(arm_m) or []} and x.lower() != others[0].lower()),
                                       key=str.lower)
                        return "single-mod", t, ["chain", provs_[rest_[-1]], miss_]
            if fewer_m_:
                fewer_setup_of[rk] = arm_m
                return absent_on_fewer(rk, t, others[0])
            return "needs-both", t, None
        sib_single_ = [s_arms.get(t) for t in p_targets if sib_split(s_arms.get(t))[0] is not None]
        bad_ = next((a_ for a_ in sib_single_ if not sib_check_ok(a_, rk[0])), None)
        if bad_ is not None:
            if sib_split(bad_)[0] is sibling:
                return "not-separable", None, words["notSeparableWhy"][
                    "siblingNotComparable" if rk[0] in sib_not_comparable else "siblingDisagrees"]
            return "not-separable", None, words["notSeparableWhy"]["siblingNotJoined"]
        if any((a_, rk[0]) in sib_disagree for a_ in sib_single_):
            return "not-separable", None, words["notSeparableWhy"]["siblingsDisagree"]
        unmeasured = [t for t in p_targets if s_arms.get(t) is None or (rk[0], s_arms[t]) not in measured]
        if unmeasured:
            if all(s_arms.get(t) is None for t in p_targets):
                why = words["notSeparableWhy"]["noSingleSetups"]
            else:
                why = bind(words["notSeparableWhy"]["notMeasured"], {"mod": unmeasured[0]})
            return "not-separable", None, why
        # an object both evaluated mods break may be broken by either alone; the mod the row names comes first
        alone = sorted((t for t in p_targets if there(s_arms[t], rk)), key=lambda t: t.lower() not in
                       {rk[2].lower(), rk[3].lower()})
        if alone:
            return "single-mod", alone[0], None
        # (only-t overclaim) a mod is shown needed where it owns a record on the row or another "only" setup lacks just it
        p_all_ = {x.lower() for x in all_orders.get(p_arm) or []}
        left_ = {u: p_all_ - {x.lower() for x in all_orders.get(s_arms[u]) or []} for u in p_targets}
        for t in p_targets:
            if partner_owns(rk, t) or any(left_[u] == {t.lower()} for u in p_targets if u != t):
                continue
            by_ = next((u for u in p_targets if u != t and t.lower() in left_[u]), None)
            if by_ is None:
                return "not-separable", None, bind(words["notSeparableWhy"]["noSetupLeavesOut"], {"other": t})
            return "not-separable", None, bind(words["notSeparableWhy"]["onlyLeavesOutMore"],
                                               {"setup": setup_phrase(s_arms[by_]), "other": t})
        return "needs-both", None, None

    record, set_aside, carried_notown, base_link_by, base_link_cut_by = [], collections.Counter(), [], [], {}
    lost_seen = set()
    groups = collections.OrderedDict()
    multi = len(p_targets) > 1
    single_mode = ctx.get("singleMod") or words.get("singleModDefault") or "show"
    unit = ctx.get("headlineUnit") or words.get("headlineUnit") or "relationships"
    p_rows = {}
    for (check_id, arm_id), rs in rows_by.items():
        if arm_id == p_of(check_id):
            p_rows.update(rs)
    # A world-state row is one clash however many world states show it: the same object against the same other
    # object between the same two mods is counted once, its states listed on it (P72); each further state's row is kept
    # in the record as the same clash. Rows against different objects stay separate clashes (P93).
    obj_rep = {}
    # The same lost value carried by two plugins (a patch carrying its master's change forward) is one change lost:
    # counted once, under the plugin loaded first (Pilgrim review S1); the other row stays in the record. Each row's
    # status is decided first and only counted rows are folded together (Missives review B1: a mod's change folded into
    # a base-game row and set aside with it was counted nowhere)
    p_order_low = [x.lower() for x in all_orders.get(p_arm) or []]
    adm_of = {rk_: admission(rk_, h_["history"], h_["row"].get("other")) for rk_, h_ in p_rows.items()}
    carried, by_value = set(), collections.defaultdict(list)
    for rk_, h_ in p_rows.items():
        if rk_[1] == "LOST-EDIT" and adm_of[rk_] in ("admitted", "undoesMaster"):
            v_ = (h_["row"].get("values") or {}).get("lost")
            if value_recorded(v_):
                by_value[(rk_[0], rk_[3].lower(), str(rk_[4]), h_["row"].get("field"), path_folded(v_))].append(rk_)
    for rks_ in by_value.values():
        if any(adm_of[r_] == "undoesMaster" for r_ in rks_):
            # the value a mod's own-master undo loses, carried by another plugin too, is one change lost: said once, on
            # the undo line (Clockwork review S1: USSEP carries Update.esm's Location)
            carried.update(r_ for r_ in rks_ if adm_of[r_] == "admitted")
            continue
        rks_ = [r_ for r_ in rks_ if adm_of[r_] == "admitted"]
        if len({r_[2].lower() for r_ in rks_}) > 1:
            keep = min(rks_, key=lambda r_: (p_order_low.index(r_[2].lower()) if r_[2].lower() in p_order_low else 10 ** 6,
                                             str(r_)))
            carried.update(r_ for r_ in rks_ if r_ != keep)
    # An object pair one check counts is not counted again by a later check that finds the same two objects (Sewers
    # review S1: a scaffold in 13 Sewers objects was 13 overlaps and 13 world-state clashes); the first check's item
    # says the other found it too
    pair_owner = {}
    for rk in sorted(p_rows, key=lambda k: tuple(str(x) for x in k)):
        hit = p_rows[rk]
        adm = adm_of[rk]
        if adm == "admitted" and rk in carried:
            adm = "sameValue"
        pair_ = (frozenset([str(rk[4]), str((hit["row"].get("other") or [None])[0])])
                 if rk[1] != "LOST-EDIT" and rk[0] not in by_object and hit["row"].get("other") else None)
        if adm == "admitted" and pair_ is not None and pair_ in pair_owner and pair_owner[pair_][0] != rk[0]:
            g_owner = pair_owner[pair_][1]
            g_owner.setdefault("_alsoFoundBy", collections.Counter())[rk[0]] += 1
            if rk[0] in sib_only:
                # (fourth review, Sewers S2) that check's rows come only from the separate evaluation: said where used
                g_owner.setdefault("_alsoFromSibling", set()).add(rk[0])
            # (third review, Sewers S3) what the other check found: the switch under which an object leaves or arrives
            sw_m = re.search(r"under (\S+)(?: \(([^)]*)\))?: (\S+) \[[^\]]*\] (leaves|arrives)", str(rk[5] or ""))
            if sw_m:
                k_sw_ = (sw_m.group(1), sw_m.group(2) or "", named(sw_m.group(3), ""), sw_m.group(4))
                g_owner.setdefault("_alsoSwitch", collections.Counter())[k_sw_] += 1
                # (fourth review, Sewers r2 S1) what the check finds after the flip, and whether its trigger was found
                dp_ = re.search(r"sits (\d+(?:\.\d+)?)\s*u inside something else", str(rk[5] or ""))
                if dp_:
                    g_owner.setdefault("_alsoSwitchDepth", {}).setdefault(k_sw_, []).append(float(dp_.group(1)))
                if str(rk[1]).endswith("@Unknown"):
                    g_owner.setdefault("_alsoSwitchUnknown", set()).add(k_sw_)
            adm = "samePair"
        base_kind, in_state = state_split(rk[1])
        ident_ = ident_of.get(rk, tuple(rk[:5]))
        other0 = (hit["row"].get("other") or [None])[0]
        obj_key = (ident_, tuple(hit["row"].get("other") or ()), rk[2].lower(), rk[3].lower())
        if adm == "admitted" and in_state is not None:
            rep_ = obj_rep.get(obj_key)
            if rep_ is not None:
                if in_state is not None and in_state not in rep_["states"]:
                    rep_["states"].append(in_state)
                rep_["rows"].append([rk[1], rk[2], rk[3], other0])
                record.append({"checkId": rk[0], "kind": rk[1], "A": rk[2], "B": rk[3], "formKey": rk[4],
                               "summary": rk[5], "admission": "sameClash", "findingId": hit["finding"].get("id"),
                               "sameAs": rep_.get("findingId"), **({"other": list(hit["row"]["other"])}
                                                                   if hit["row"].get("other") is not None else {})})
                set_aside["sameClash"] += 1
                continue
        entry = {"checkId": rk[0], "kind": rk[1], "A": rk[2], "B": rk[3], "formKey": rk[4], "summary": rk[5],
                 "admission": adm,
                 "findingId": hit["finding"].get("id")}
        if in_state is not None:
            entry.update({"itemKind": base_kind, "states": [in_state], "rows": [[rk[1], rk[2], rk[3], other0]]})
            if adm == "admitted":
                obj_rep[obj_key] = entry
        if rk[0] in sib_only:
            # its finding lives in the sibling's plan, not this one: named, never linked as if it were here
            entry["source"] = "sibling"
            entry["siblingFindingId"] = entry.pop("findingId")
            entry["findingId"] = None
        if hit["row"].get("other") is not None:
            entry["other"] = list(hit["row"]["other"])
        if hit["row"].get("physical") is not None:
            entry["physical"] = hit["row"]["physical"]
        if hit["row"].get("field") is not None:
            entry["field"] = hit["row"]["field"]
            entry["values"] = hit["row"].get("values")
            # Two record checks read overlapping populations (a cell's fields are in both the contested-fields and
            # the cell-attributes check): the same lost edit is one relationship, counted under the first check.
            same = (rk[2].lower(), rk[3].lower(), rk[4], entry["field"])
            if (adm in ("admitted", "ownMaster", "undoesMaster", "superseded", "notOwnEdit", *NOISE_ADMISSIONS)
                    and same in lost_seen):
                adm = entry["admission"] = "repeat"
            lost_seen.add(same)
        if adm != "admitted":
            set_aside[adm] += 1
            if adm == "baseLink" and rk[2] not in base_link_by:
                base_link_by.append(rk[2])
            if adm == "baseLink" and base_link_cut(rk):
                base_link_cut_by.setdefault(rk[3], 0)
                base_link_cut_by[rk[3]] += 1
            if adm == "samePair":
                entry["sameAsCheck"] = pair_owner[pair_][0]
            if adm == "notOwnEdit":
                oe = own_edit(p_of(rk[0]), rk, rk[2]) or {}
                entry["undoes"], entry["inherits"] = oe.get("before"), oe.get("inherits")
                if oe.get("restoresBaseFix"):     # carried or restored: the winner drops it either way (C966)
                    entry["baseValueOf"] = oe["restoresBase"]    # the loss is the winner's against that plugin
                if oe.get("beforeWins") and oe.get("lostIsOriginal"):
                    set_aside["notOwnEditEarlierWins"] += 1
                elif not oe.get("lostIsOriginal"):
                    # the value lost is still an earlier plugin's change the losing mod carries, so the game's value
                    # does change (Missives review 3): said, never "nothing is lost"
                    carried_notown.append(f"{rk[4]} {entry.get('field')}")
            record.append(entry)
            continue
        attr, attr_mod, why = attribution(rk)
        entry.update({"attribution": attr, "attributionMod": attr_mod})
        if attr == "single-mod" and multi and single_mode == "aside":
            # a clash one evaluated mod has with a third mod on its own: set aside by the switch (Piranha's R1)
            entry["admission"] = "singleMod"
            set_aside["singleMod"] += 1
            record.append(entry)
            continue
        record.append(entry)
        mods = sorted([rk[2], rk[3]], key=str.lower)
        gk = (rk[0], base_kind, tuple(mods), attr, attr_mod, why if not isinstance(why, list) else (why[0], why[1], tuple(why[2])))
        g = groups.setdefault(gk, {"checkId": rk[0], "kind": base_kind, "mods": mods, "count": 0, "attribution": attr,
                                   "attributionMod": attr_mod, "attributionWhy": why, "findingIds": [], "examples": [],
                                   "_subjects": set()})
        g["count"] += 1
        g["_subjects"].add(rk[4] if rk[4] is not None else ("row", rk[5]))
        if pair_ is not None and pair_ not in pair_owner:
            pair_owner[pair_] = (rk[0], g)
        if in_state is not None:
            g.setdefault("_stateEntries", []).append(entry)
        if rk[0] in sib_only:
            g["_fromSibling"] = True
        if rk in fewer_setup_of:
            g.setdefault("_fewerSetups", []).append(fewer_setup_of[rk])
        if rk[1] != "LOST-EDIT" and hit["row"].get("other"):
            g.setdefault("_partners", []).extend(o_ for o_ in [str(rk[4])] + [str(x_) for x_ in hit["row"]["other"]]
                                                 if o_.split(":", 1)[-1].lower() not in pt_lower)
            g.setdefault("_hosts", []).append(str(hit["row"]["other"][0]))     # (eighth review) each row's named host
        if rk[1] != "LOST-EDIT" and hit["row"].get("other"):
            # the objects by name (Missives review 6) and whether every row is against one object (BPT review S6)
            o0_ = str(hit["row"]["other"][0])
            g.setdefault("_partnerSet", set()).add(o0_)
            pairs_ = g.setdefault("_objPairs", [])
            if not pairs_ and (object_name(rk[4]) or object_name(o0_)):
                # a world-state row left in empty space is an object and the support it lost, never two objects in a clash
                empty_ = str(rk[1]).startswith("STATE-EMPTY") and words.get("itemObjectEmpty")
                pairs_.append(bind(words["itemObjectEmpty" if empty_ else "itemObjectPair"], {"a": named(rk[4]), "b": named(o0_)}))
        eff_row_ = rk[1] != "LOST-EDIT" and bool(hit["row"].get("other")) and effect_pair(rk[4], hit["row"]["other"])
        if eff_row_:
            # (fourth review, Pilgrim S2) a pair with an object whose model is a visual effect (skill data): said apart
            g["_effectPairs"] = g.get("_effectPairs", 0) + 1
        touch_row_ = (rk[1] != "LOST-EDIT" and touching_re is not None and bool(hit["row"].get("other"))
                      and touching_re.search(str(rk[5] or "")) is not None)
        cop_row_ = (rk[1] != "LOST-EDIT" and coplanar_re is not None and bool(hit["row"].get("other"))
                    and not touch_row_ and coplanar_re.search(str(rk[5] or "")) is not None)
        if cop_row_:
            # (sixth review, BPT r5 S3) a pair the probe reports as coplanar: surfaces in one plane, said apart
            g["_coplanarPairs"] = g.get("_coplanarPairs", 0) + 1
            if not eff_row_:
                g["_notCrossing"] = g.get("_notCrossing", 0) + 1
        if touch_row_:
            # (fifth review, Sewers S3) a pair the probe reports as touching: surfaces in contact, said apart
            g["_touchingPairs"] = g.get("_touchingPairs", 0) + 1
        if eff_row_ or touch_row_:
            g["_notCrossing"] = g.get("_notCrossing", 0) + 1
        if rk[1] != "LOST-EDIT" and hit["row"].get("other"):
            g.setdefault("_rowObjs", []).append((str(rk[4]), str(hit["row"]["other"][0])))
        if rk[1] == "LOST-EDIT" and str(hit["row"].get("field") or "") == sup_place:
            # (fourth review, Pilgrim S4) a straight vertical difference between the two values: said with its height
            v4_ = hit["row"].get("values") or {}
            lp_, wp_ = (_vec(next((x_ for x_ in (v4_.get(k_) if isinstance(v4_.get(k_), list) else [v4_.get(k_)])
                                   if "Position" in str(x_)), None)) for k_ in ("lost", "winner"))
            if not (lp_ and wp_):
                # a row carrying the winner's value as a placeholder: the record chain's positions
                chh_ = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
                lowh_ = [str(x).lower() for x in (chh_ or {}).get("providers") or []]
                if chh_ and rk[2].lower() in lowh_ and rk[3].lower() in lowh_:
                    lp_, wp_ = (_vec(next((v_ for k_, v_ in recordrows.provider_fields(chh_, sup_place, lowh_.index(x_.lower())).items()
                                           if k_.endswith("Position")), None)) for x_ in (rk[2], rk[3]))
            # (regen-r4c) every Placement row of the pair is kept (a level difference is said only where every one
            # is 64 u or more the same way, at render); a value below the world is a switch-off, said apart
            if lp_ and wp_ and lp_[2] > -29000 and wp_[2] > -29000:
                g.setdefault("_heightDiff", {}).setdefault((rk[3], rk[2]), []).append(wp_[2] - lp_[2])
                # (fifth review, BPT S3) and how far apart across the floor
                g.setdefault("_acrossDiff", {}).setdefault((rk[3], rk[2]), []).append(math.dist(wp_[:2], lp_[:2]))
            else:
                g.setdefault("_heightDiff", {}).setdefault((rk[3], rk[2]), []).append(None)
        if rk[1] != "LOST-EDIT" and rk[0] not in by_object and hit["row"].get("other"):
            # a pair of objects: both are objects in the clash (blind review: 323 pairs are 274 objects)
            g.setdefault("_pairObjects", set()).update([str(rk[4]), *map(str, hit["row"]["other"])])
        if rk[1] == "LOST-EDIT":
            g.setdefault("_fieldNames", collections.Counter())[field_name(hit["row"])] += 1
            g.setdefault("_lostKeys", []).append((rk, hit["row"].get("field")))
        g.setdefault("_subjectRows", collections.Counter())[rk[4] if rk[4] is not None else ("row", rk[5])] += 1
        g.setdefault("_rowSummaries", []).append(str(rk[5] or ""))
        g.setdefault("_rowKinds", []).append((str(rk[1]), str(rk[4])))
        g.setdefault("_rowOthers", {})[str(rk[4])] = str((hit["row"].get("other") or [""])[0] or "")
        # (ninth review, Sewers 27) a row from the separate evaluation links its entry in this index, never that plan's id
        fid = hit["finding"].get("id") if rk[0] not in sib_only else (
            f"sibling-{hit['finding'].get('id')}" if hit["finding"].get("id") else None)
        if fid and fid not in g["findingIds"]:
            g["findingIds"].append(fid)
        label = hit["row"].get("label")
        if label and len(g["examples"]) < 2 and label not in g["examples"]:
            g["examples"].append(label)
        # what makes the item actionable: the direction a lost edit goes, example records, the places it is in,
        # and whether its count rests on a result reused from another evaluation
        g.setdefault("_directions", set()).add((rk[2], rk[3]))
        if rk[1] == "LOST-EDIT" and (disables((hit["row"].get("values") or {}).get("winner"))
                                     or side_disables(rk, rk[3])) and not (
                disables((hit["row"].get("values") or {}).get("lost")) or side_disables(rk, rk[2])):
            g["_disables"] = g.get("_disables", 0) + 1
            if side_own_removal(rk, rk[3]):
                g["_disablesOwn"] = g.get("_disablesOwn", 0) + 1     # (fifth review, BPT S2) the winner's own removal
        lost_v_ = (hit["row"].get("values") or {}).get("lost")
        lost_n_ = re.fullmatch(r"\s*(?:\w*MajorRecordFlags=)?(\d+)\s*", " ".join(map(str, lost_v_)) if isinstance(lost_v_, list)
                               else str(lost_v_ or ""))
        if rk[1] == "LOST-EDIT" and (off_at_load(lost_v_) or (
                str(hit["row"].get("field") or "").endswith("MajorRecordFlags") and lost_n_ and int(lost_n_.group(1)) & 0x800)):
            # (round 2, B2) record flags written as a number: bit 2048 is the initially-disabled flag
            # (third review, BPT S2) the loser's value switches the object off; whether the winner's whole version has it
            # on when the game loads: not initially disabled, not deleted, not below the world, and no enable parent
            # (whose state the record data does not give: then only that its own record does not switch it off)
            won_ = winner_on_at_load(rk)
            if won_ == "on" and not base_has_no_model(names, rk[4]) and base_is_marker(rk[4]):
                # (sixth review, Winterhold r6 S2) an editor marker's base (record flag, read from its plugin file):
                # never drawn in game, so never counted as drawn
                won_ = "onMarker"
            if won_ == "on" and base_has_no_model(names, rk[4]):
                # (fifth review, Snazzy 17, BPT 19) no model: what it does in game is its base type's (noModelMeaning)
                won_ = ("onNoModel", no_model_type(names, rk[4]))
            if won_ in ("on", "parent", "onMarker") or isinstance(won_, tuple):
                # (round 2, Snazzy B1) counted by object, as the sentence names them: one object's fields count once
                sw_ = g.setdefault("_switchOff", {}).setdefault((rk[2], rk[3], won_), {})
                sw_.setdefault(str(rk[4]), named(rk[4], rk[5]))
        if rk[1] == "LOST-EDIT" and side_starts_off(rk, rk[2]) and side_disables(rk, rk[3]):
            # (third review, Pilgrim S1) only the loser's version starts it disabled; the winner's sets it off for good
            g.setdefault("_startsOffLoser", {}).setdefault(rk[2], set()).add(str(rk[4]))
        elif (rk[1] == "LOST-EDIT" and bare_start_off(rk, rk[3]) and not side_starts_off(rk, rk[2])
              and refers_to(rk[3], rk[4]) is False):
            # (fourth review, Clockwork r3 S3) the winner only starts it disabled and its own records never refer to it:
            # named, with the read's scope, never "a script or quest may enable it"
            g.setdefault("_winnerStartsOffUnref", {}).setdefault((rk[3], rk[2]), {}).setdefault(str(rk[4]), named(rk[4], rk[5]))
        elif rk[1] == "LOST-EDIT" and (side_starts_off(rk, rk[2]) or side_starts_off(rk, rk[3])):
            g.setdefault("_startsOff", set()).add(str(rk[4]))
        # (round 2, Sewers B1 and Clockwork S1) a broken link of a base-game navmesh record the other mod carries, decided
        # by the edge links themselves (link_origin): one that mod (or an earlier one) added is said as its own; one whose
        # versions were not read keeps the pair named against the base game's navmesh and says the links were not read
        org_ = link_origin(rk)
        if org_ is not None and org_[0] == "added":
            g.setdefault("_navAdded", collections.Counter())[(org_[1], rk[2])] += 1
        elif org_ is not None and org_[0] == "unread":
            g.setdefault("_baseNavCarried", collections.Counter())[rk[2]] += 1
        if rk[1] == "LOST-EDIT":
            # what each side has (Pilgrim review B1): the winner put back its master's earlier value, or changed the
            # field itself too; said in the item, never only "lost to"
            if (supersede(rk) or {}).get("kind") == "undecided":
                g["_notDecided"] = g.get("_notDecided", 0) + 1
            own_w_ = whose_value(p_of(rk[0]), rk[0], rk[4], hit["row"].get("field"), rk[3])
            if (own_edit(p_of(rk[0]), rk, rk[3]) or {}).get("changed") and (
                    own_w_ is None or own_w_[2]["owner"] == own_w_[2]["origin"] == [str(x).lower() for x in own_w_[0]].index(rk[3].lower())):
                # (seventh review round 2) and the value is its own, not one an earlier plugin made first
                g["_winnerOwn"] = g.get("_winnerOwn", 0) + 1
                v_ = hit["row"].get("values") or {}
                if value_recorded(v_.get("lost")) and value_recorded(v_.get("winner")):
                    nm_ = object_name(rk[4], rk[5])
                    f_ = hit["row"].get("field")
                    ex_ = (rk[4], (nm_ + ", " if nm_ else "") + field_word(f_), plain_value(f_, v_.get("lost")),
                           plain_value(f_, v_.get("winner")))
                    best_ = g.get("_winnerOwnExample")

                    def ex_key(x_):
                        # a flag example whose numbers all decode reads better than one left as a bare number (W5)
                        raw_ = bool(flag_bits_.get(str(f_))) and any(re.fullmatch(r"\d+", t_.strip())
                                                                     for t_ in (x_[2] + "," + x_[3]).split(","))
                        return (raw_, len(x_[2]) + len(x_[3]))
                    if best_ is None or ex_key(ex_) < ex_key(best_):
                        g["_winnerOwnExample"] = ex_
            # where the lost value was first set (Missives review 2): a value several plugins carry is named with the
            # plugin that introduced it, so a reader does not take it for the loser's alone
            # (third review, Missives S2) other plugins holding the same lost value as their own change; a lost value that
            # is an absence (the loser has no such data); a winner keeping the base game's value
            g.setdefault("_fieldRows", collections.Counter())[field_word(hit["row"].get("field"))] += 1
            g.setdefault("_rawFields", collections.Counter())[str(hit["row"].get("field"))] += 1
            # (fourth review, Missives S4) which leaves of the field differ between the two versions, and whose value is
            # used without the winner (the provider loaded just before it), from the record chain
            ch4_ = (chains_of(rk[0], p_of(rk[0])) or {}).get(str(rk[4]))
            low4_ = [str(x).lower() for x in (ch4_ or {}).get("providers") or []]
            if ch4_ and rk[2].lower() in low4_ and rk[3].lower() in low4_:
                ia4_, ib4_ = low4_.index(rk[2].lower()), low4_.index(rk[3].lower())
                f4_ = str(hit["row"].get("field") or "")
                la_, lb_ = (recordrows.provider_fields(ch4_, f4_, k_) for k_ in (ia4_, ib4_))
                diff4_ = sorted(k_ for k_ in set(la_) | set(lb_) if la_.get(k_) != lb_.get(k_))
                g.setdefault("_leafDiff", {}).setdefault(f4_, []).append(
                    (tuple(diff4_), la_.get(diff4_[0]) if len(diff4_) == 1 else None,
                     lb_.get(diff4_[0]) if len(diff4_) == 1 else None))
                pw_ = ch4_["providers"][ib4_ - 1] if ib4_ > 0 else None
                po_ = None
                if pw_ is not None:
                    gp_ = whose_value(p_of(rk[0]), rk[0], rk[4], f4_, pw_)
                    po_ = owner_of(gp_[0], gp_[1], gp_[2]["origin"])[1] if gp_ is not None else None
                g.setdefault("_prevWinner", set()).add((pw_, po_) if pw_ is not None else None)
            got3_ = field_chain(p_of(rk[0]), rk, rk[2])
            if got3_ is not None:
                provs3, vals3, _ = got3_
                low3 = [str(x).lower() for x in provs3]
                if rk[2].lower() in low3 and rk[3].lower() in low3:
                    i3, w3 = low3.index(rk[2].lower()), low3.index(rk[3].lower())
                    # (seventh review round 2, Missives 21) where the loser owns its value, the others holding it
                    # carry it, whether by a master or by the same bytes
                    if owner_of(provs3, vals3, i3)[0] == i3:
                        for j3 in range(i3 + 1, w3):
                            if vals3[j3] == vals3[i3] and not is_base_game(provs3[j3], words):
                                g.setdefault("_sameBy", collections.Counter())[provs3[j3]] += 1
                    # (sixth review, BPT r6 S3, Missives) the winner's value is an earlier, not base-game plugin's
                    # own change it only carries: that plugin is named as the value's own
                    ow3_ = owner_of(provs3, vals3, w3)
                    org_ = ow3_[1] if ow3_[0] != w3 else None
                    if org_ is not None and ow3_[0] != 0:
                        g.setdefault("_winnerCarries", collections.Counter())[(rk[3], org_)] += 1
                        # (round 3, Winterhold 17) its example is a record of its own rows
                        g.setdefault("_winnerCarriesEx", {}).setdefault(
                            (rk[3], org_), (str(rk[4]), field_word(hit["row"].get("field"))))
                    if (vals3[w3] == vals3[0] and not recordrows.own_change(
                            provs3, vals3, w3, (masters or {}).get(low3[w3]) if masters is not None else None)[0]):
                        g.setdefault("_winnerBase", collections.Counter())[field_word(hit["row"].get("field"))] += 1
                        # (fifth review, Winterhold R2-3) the sentence names a record of its own field as its example
                        g.setdefault("_winnerBaseEx", {}).setdefault(field_word(hit["row"].get("field")), str(rk[4]))
            lost_txt_ = " ".join(map(str, (hit["row"].get("values") or {}).get("lost") or [])) if isinstance(
                (hit["row"].get("values") or {}).get("lost"), list) else str((hit["row"].get("values") or {}).get("lost") or "")
            lost_ls_ = (hit["row"].get("values") or {}).get("lost")
            lost_ls_ = [str(x) for x in (lost_ls_ if isinstance(lost_ls_, list) else [lost_ls_]) if x not in (None, "")]
            # (round 2, BPT S1) an absence only where the losing mod has none of the field: every line of its lost value is
            # one only the winner has, never a value with some entries of the loser's own beside others only the winner has
            if lost_ls_ and all(re.search(r"\bonly in " + re.escape(rk[3]), x) or re.search(r": ABSENT here$", x)
                                for x in lost_ls_) and lost_txt_:
                g.setdefault("_loserAbsent", collections.Counter())[field_word(hit["row"].get("field"))] += 1
            if "_carriedFrom" not in g:
                got_ = field_chain(p_of(rk[0]), rk, rk[2])
                if got_ is not None:
                    provs_, vals_, _ = got_
                    low_ = [str(x).lower() for x in provs_]
                    if rk[2].lower() in low_:
                        i_ = low_.index(rk[2].lower())
                        # (seventh review round 2) the one owner of the value
                        k_ = recordrows.attribute(provs_, vals_, i_, masters)["owner"]
                        if k_ != i_ and k_ > 0:
                            g["_carriedFrom"] = (named(rk[4], rk[5]), hit["row"].get("field"),
                                                 shown_value((hit["row"].get("values") or {}).get("lost")),
                                                 provs_[k_], [p_ for j_, p_ in enumerate(provs_[k_ + 1:i_ + 1], k_ + 1)
                                                              if vals_[j_] == vals_[i_]])
        if rk[1] == "LOST-EDIT" and rk[4]:
            # examples: a field skill data prefers (a placed object's position) before any other (Snazzy review 3:
            # a worldspace texture path is no example a modder can act on)
            nm_ = object_name(rk[4], rk[5])
            # (eighth review, BPT 19) an example whose winning version sets the object off for good says so, and comes
            # after examples that are drawn
            off_ex_ = bool(side_disables(rk, rk[3])) and bool(words.get("exampleOffForGood"))
            ex = (f"{rk[4]} ({nm_ + ', ' if nm_ else ''}{field_word(hit['row'].get('field'))}"
                  + (words.get("exampleOffForGood") or "" if off_ex_ else "") + ")" if hit["row"].get("field")
                  else named(rk[4], rk[5]))
            rank = (0 if hit["row"].get("field") in preferred_example_fields else 1) + (2 if off_ex_ else 0)
            pool = g.setdefault("_recordPool", ([], [], [], []))[rank]
            if len(pool) < 2 and ex not in pool:
                pool.append(ex)
        if master_of(masters, rk[2], rk[3]):
            g["_ownMaster"] = master_of(masters, rk[2], rk[3])  # a geometry clash with its master's objects (P63)
        if label and rk[1] != "LOST-EDIT":
            # every row's place counts, a place named by form key included; a navmesh row's triangle note is not
            # part of its place
            raw_ = re.sub(r"\s+\d+ tris -> \d+$", "", str(label))
            place_ = located_place(rk[4], raw_)
            # counted by the cell, said by its name (N6): two cells said alike are still two places
            g.setdefault("_places", collections.Counter())[raw_] += 1
            g.setdefault("_placeSaid", {})[raw_] = place_
            place_labels.setdefault(str(rk[4]), place_)             # the reel says the same place the report does
        if hit["finding"].get("readerProvenance"):
            # the full sentence is in the limits and beside the finding; the item says it in short
            g["_provenance"] = words.get("itemReused") or hit["finding"]["readerProvenance"]

    titles, in_game, phrases = ctx["checkTitles"], ctx["inGame"], ctx["kindPhrases"]

    def title_of(check_id):
        return titles.get(str(check_id)) or ctx["checkTitle"](check_id)

    def unit_noun(check_id, kind, n):
        """The unit a kind of clash is counted in: record fields, the objects of a by-object check, the kind's own unit."""
        one = int(n) == 1
        if kind == "LOST-EDIT":
            return words["fieldNoun"][0 if one else 1]
        if check_id in by_object:
            # a row is the object against one host or support: one object can be several (P94), so the unit is that
            # pair, named for its check (skill data `byObjectUnits`); the objects are counted apart in the headline
            u_ = (words.get("byObjectUnits") or {}).get(str(check_id)) or ["clash", "clashes"]
            return u_[0] if one else u_[1]
        ph_ = phrases.get(kind) or {}
        return (ph_.get("unitSingular") if one else ph_.get("unitPlural")) or ("clash" if one else "clashes")

    def reason_words(code, check_id=None):
        # a probe's reason in the skill's words; a reason already written in words passes through; a code the skill
        # does not name gets one generic phrase, never the code (the code stays in the evidence index). A count the
        # adapter reads with no reason ("unjudgeable") is worded for its check where the skill names it.
        code = str(code or "").strip()
        if code in ("", "unjudgeable") and check_id and (words.get("unjudgeableByCheck") or {}).get(check_id):
            return words["unjudgeableByCheck"][check_id]
        known = (words.get("unjudgeableWords") or {}).get(code)
        if known:
            return known
        if " " in code:
            return code
        return words["unjudgeableUnnamed"]
    items, subjects_of, subject_rows_of, pair_objects_of, field_names_of, lost_keys_of = [], {}, {}, {}, {}, {}
    names_fields = set(words.get("verdictNamesFields") or [])

    def fields_text(counter):
        return ", ".join(f"{n_} {fmt_n(c_)}" for n_, c_ in sorted(counter.items(), key=lambda kv: (-kv[1], kv[0])))
    def pair_objects_of_base(g):
        """Every partner object of the item's rows is a base-game record (the other mod only edits it)."""
        others_ = [o_ for o_ in g.get("_partners") or []]
        return bool(others_) and all(is_base_game(str(o_).split(":", 1)[-1], words) for o_ in others_)
    dis_of, dis_own_of = {}, {}
    for g in groups.values():
        g["title"] = title_of(g["checkId"])
        g["inGame"] = in_game.get(f"{g['checkId']}|{g['kind']}") or in_game.get(g["checkId"])
        # (eighth review, Winterhold 19) a fixture's named host whose model is no building piece (skill data: the folders
        # of floors and walls) is no floor or wall: the item says the host's kind, with how many rows and an example
        fh_ = words.get("fixtureHosts") or {}
        if g["checkId"] in (fh_.get("checks") or []) and fh_.get("structuralModelPattern") and fh_.get("inGameOther"):
            st_re_ = re.compile(fh_["structuralModelPattern"], re.I)
            hb_ = [(h_, name_bases.get((name_refs.get(h_) or {}).get("base") or "") or {}) for h_ in g.get("_hosts") or []]
            non_ = [(h_, b_) for h_, b_ in hb_ if b_.get("model") and not st_re_.search(str(b_["model"]).replace("\\", "/"))]
            if non_:
                g["inGame"] = bind(fh_["inGameOther"], {
                    "count": fmt_n(len(non_)), "total": fmt_n(len(hb_)),
                    "example": named(non_[0][0])})
        g.pop("_hosts", None)
        ph = phrases.get(g["kind"]) or {}
        noun = (ph.get("unitSingular") if g["count"] == 1 else ph.get("unitPlural")) or ("clash" if g["count"] == 1 else "clashes")
        attr_key, replaced_ = g["attribution"], 0
        other_mod = next((m for m in g["mods"] if str(m).lower() != str(g["attributionMod"] or "").lower()), "")
        if not multi and attr_key in ("needs-both", "single-mod", "with-target", "needs-some") and g["attributionMod"]:
            attr_key = {"needs-both": "needs-both-other", "single-mod": "single-mod-target",
                        "with-target": "with-target", "needs-some": "needs-some"}[attr_key]
        if (attr_key == "single-mod-target" and g["kind"] != "LOST-EDIT" and pair_objects_of_base(g)
                and words["attribution"].get("single-mod-target-base")):
            attr_key = "single-mod-target-base"       # its partners are base-game objects the other mod edits
            # (third review, Snazzy S3) where the other mod's version of a partner uses a base record of its own (the
            # names enrichment's winning base), it replaces the model there: never "only edits"
            replaced_ = sum(1 for o_ in g.get("_partners") or []
                            if str((name_refs.get(str(o_)) or {}).get("base") or "").split(":", 1)[-1].lower()
                            == str(other_mod).lower())
            if replaced_ and words["attribution"].get("single-mod-target-base-replaced"):
                attr_key = "single-mod-target-base-replaced"
        elif (attr_key == "single-mod" and g["attributionMod"]
                and str(g["attributionMod"]).lower() not in {m.lower() for m in g["mods"]}):
            attr_key = "single-mod-elsewhere"       # the same object is broken with the other evaluated mod alone
        fewer_ = list(dict.fromkeys(g.pop("_fewerSetups", None) or []))
        if fewer_ and words["attribution"].get(attr_key + "-fewer"):
            # (item 21, A2) decided on a measured setup that leaves out more than the other mod: that setup is named,
            # never "the setup without" the other mod
            attr_key = attr_key + "-fewer"
        chain_why_ = g.get("attributionWhy") if isinstance(g.get("attributionWhy"), list) else None
        if attr_key == "single-mod-target" and chain_why_ and words["attribution"].get("single-mod-target-chain"):
            # (seventh review, BPT r6 S3) judged from the record's own versions, not from a setup lacking more
            attr_key = "single-mod-target-chain"
        attr_text = bind(words["attribution"][attr_key], {"mod": g["attributionMod"], "other": other_mod,
                                                          "next": chain_why_[1] if chain_why_ else "",
                                                          "missing": and_list(chain_why_[2]) if chain_why_ and chain_why_[2] else "",
                                                          "replaced": fmt_n(replaced_) if "replaced" in attr_key else "",
                                                          "why": g["attributionWhy"] or "",
                                                          "setup": " or ".join(setup_phrase(a_) for a_ in fewer_)})
        subjects_of[id(g)] = g.pop("_subjects")
        pair_objects_of[id(g)] = g.pop("_pairObjects", set())
        field_names_of[id(g)] = g.pop("_fieldNames", collections.Counter())
        lost_keys_of[id(g)] = g.pop("_lostKeys", [])
        if g["checkId"] in names_fields and field_names_of[id(g)]:
            g["_fieldsLine"] = fields_text(field_names_of[id(g)])
        subject_rows_of[id(g)] = g.pop("_subjectRows")
        g["relationships"] = g["count"]
        g["objects"] = len(subjects_of[id(g)])
        # a kind whose rows are fields of a record (a path, not a thing) also says how many records they are on
        on = (f", on {fmt_n(g['objects'])} {ph['objectsSingular'] if g['objects'] == 1 else ph['objectsPlural']},"
              if ph.get("objectsPlural") else "")
        directions = g.pop("_directions", set())
        detail = []
        if g["kind"] == "LOST-EDIT" and len(directions) == 1:
            loser, winner = next(iter(directions))
            detail.append(bind(words["itemDirection"], {"loser": loser, "winner": winner}))
        if attr_key == "with-target" and g["kind"] != "LOST-EDIT" and other_mod and words.get("itemOtherNudged"):
            # (fifth review, Pilgrim S1) where the other mod's object is a base-game record it only moves (same turn,
            # less than the object's size), read from both plugin files: how far, so a modder sees it is in effect the
            # base game's object; no geometry at the base game's place is claimed
            order_ = [str(x) for x in all_orders.get(p_arm) or []]
            low_order_ = [x.lower() for x in order_]
            # (review5: Sewers B1, Pilgrim B1, Missives S1) every base-game partner the other mod carries, with every change
            # it makes there that sets where or what the object is: position, turn, base, scale, enable state
            changes_ = collections.OrderedDict()
            for o_ in dict.fromkeys(g.get("_partners") or []):
                if not is_base_game(str(o_).split(":", 1)[-1], words):
                    continue
                carr_ = {str(c_).lower() for c_ in (name_refs.get(str(o_)) or {}).get("carriedBy") or []}
                if str(other_mod).lower() not in carr_ or str(other_mod).lower() not in low_order_:
                    continue
                prev_ = [x for x in order_[:low_order_.index(str(other_mod).lower())] if x.lower() in carr_]
                if not prev_:
                    continue
                mine_, before_ = (plugin_record(other_mod, "placementFull", str(o_)),
                                  plugin_record(prev_[-1], "placementFull", str(o_)))
                if not (mine_ and before_ and mine_.get("position") and before_.get("position")):
                    changes_[str(o_)] = None                      # carried, but its versions were not read
                    continue
                d_ = math.dist(mine_["position"], before_["position"])
                turned_ = any(abs((a_ - b_ + math.pi) % (2 * math.pi) - math.pi) >= 1e-3
                              for a_, b_ in zip(mine_.get("rotation") or (), before_.get("rotation") or ()))
                changes_[str(o_)] = {
                    "moved": d_ if d_ >= 0.05 else None, "turned": turned_,
                    "rebased": mine_.get("base") != before_.get("base"),
                    "scale": (before_.get("scale"), mine_.get("scale")) if mine_.get("scale") != before_.get("scale") else None,
                    "enable": (mine_.get("enableParent") != before_.get("enableParent")
                               or mine_.get("initiallyDisabled") != before_.get("initiallyDisabled")
                               or mine_.get("deleted") != before_.get("deleted")),
                    "far": d_ >= spot_size(str(o_)),
                    # (seventh review, Pilgrim S2) moved farther than the object's whole size (twice its half extent): the
                    # measured crossing is where that mod places it; the attribution read over the placement
                    "beyond": d_ >= 2 * spot_size(str(o_))}
            read_ = {k_: v_ for k_, v_ in changes_.items() if v_ is not None}
            small_ = read_ and all(v_["moved"] is not None and not v_["far"] and not v_["turned"] and not v_["rebased"]
                                   and not v_["scale"] and not v_["enable"] for v_ in read_.values())
            if small_ and len(read_) == len(changes_) and words.get("itemOtherNudged"):
                # "only" where every partner it carries is a small move and nothing else
                ds_ = sorted(v_["moved"] for v_ in read_.values())
                g["nudged"] = True             # (eighth review) the report says once how such pairs are counted
                detail.append(bind(words["itemOtherNudged"], {
                    "mod": other_mod, "count": fmt_n(len(read_)), "objects": "object" if len(read_) == 1 else "objects",
                    "range": (f"{ds_[0]:,.1f} u" if ds_[-1] - ds_[0] < 0.05 else f"{ds_[0]:,.1f} u to {ds_[-1]:,.1f} u"),
                    "examples": "; ".join(named(k_) for k_ in list(read_)[:2])}))
            elif changes_ and words.get("itemOtherChanges"):
                ow_ = words.get("otherChangeWords") or {}

                def what_(v_):
                    if v_ is None:
                        return ow_.get("unread") or "its versions were not read"
                    parts_ = []
                    if v_["moved"] is not None:
                        parts_.append(bind(ow_.get("moved") or "moved {d}", {"d": f"{v_['moved']:,.1f} u"}))
                    if v_["turned"]:
                        parts_.append(ow_.get("turned") or "turned")
                    if v_["rebased"]:
                        parts_.append(ow_.get("rebased") or "given another base")
                    if v_["scale"]:
                        parts_.append(bind(ow_.get("scale") or "scaled {a} -> {b}", {"a": f"{v_['scale'][0]:g}", "b": f"{v_['scale'][1]:g}"}))
                    if v_["enable"]:
                        parts_.append(ow_.get("enable") or "its enable state changed")
                    return ", ".join(parts_) or ow_.get("nothing") or "nothing that sets where it stands or what it is"
                shown_ = list(changes_.items())[:int(words.get("itemOtherChangesMax") or 6)]
                more_ = len(changes_) - len(shown_)
                detail.append(bind(words["itemOtherChanges"], {
                    "mod": other_mod, "parts": "; ".join(f"{named(k_)} {what_(v_)}" for k_, v_ in shown_)
                    + (bind(ow_.get("more") or "; and {count} more", {"count": fmt_n(more_)}) if more_ else ""),
                    # (sixth review, Pilgrim r6 S1) naming the partners it applies to, never one only nudged
                    # (seventh review, Pilgrim S2) a partner moved beyond its size is credited to the mod's placement
                    "own": (bind(words.get("itemOtherChangesFar") or "", {"mod": other_mod, "examples": "; ".join(
                        k_ for k_, v_ in changes_.items() if v_ and v_["beyond"])})
                        if any(v_ and v_["beyond"] for v_ in changes_.values()) else "")
                    + (bind(words.get("itemOtherChangesOwn") or "", {"examples": "; ".join(
                        k_ for k_, v_ in changes_.items() if v_ and not v_["beyond"]
                        and (v_["far"] or v_["rebased"] or v_["turned"]))})
                       if any(v_ and not v_["beyond"] and (v_["far"] or v_["rebased"] or v_["turned"])
                              for v_ in changes_.values()) else "")}))
        n_own, ex_own = g.pop("_winnerOwn", 0), g.pop("_winnerOwnExample", None)
        if len(directions) == 1 and g.get("kind") == "LOST-EDIT":
            # (Helgi r3 reviews) how many of the lost fields the winner changed itself: the next step forwards the losing
            # mod's value on the rest, and asks for a patch that carries both only on these
            g["lostSplit"] = {"loser": next(iter(directions))[0], "winner": next(iter(directions))[1], "own": n_own}
        if n_own and len(directions) == 1:
            loser, winner = next(iter(directions))
            detail.append(bind(words["itemWinnerOwn"], {"count": fmt_n(n_own), "loser": loser, "winner": winner,
                                                        "example": bind(words["itemWinnerOwnExample"], {
                                                            "record": ex_own[0], "field": ex_own[1], "lost": ex_own[2],
                                                            "won": ex_own[3], "loser": loser, "winner": winner})
                                                        if ex_own else ""}))
        carried_from = g.pop("_carriedFrom", None)
        if carried_from and words.get("itemCarriedFrom"):
            detail.append(bind(words["itemCarriedFrom"], {
                "record": carried_from[0], "field": carried_from[1], "origin": carried_from[3],
                "value": re.sub("^" + re.escape(str(carried_from[1])) + "=", "", str(carried_from[2])),
                "carriers": " and ".join(carried_from[4][-3:])}))
        # each said only where it holds for every row of the item, never of a few rows as if of all
        same_by = [m_ for m_, n_ in (g.pop("_sameBy", None) or {}).items() if n_ == g["count"]]
        win_base_c, absent_c = g.pop("_winnerBase", None) or {}, g.pop("_loserAbsent", None) or {}
        rows_c = g.pop("_fieldRows", None) or {}
        win_base = [f_ for f_, n_ in win_base_c.items() if n_ == rows_c.get(f_)]     # per field, every row of it
        absent_ = [f_ for f_, n_ in absent_c.items() if n_ == rows_c.get(f_)]
        if len(directions) == 1 and words.get("itemSameValueBy"):
            loser, winner = next(iter(directions))
            if absent_:
                detail.append(bind(words["itemLoserAbsent"], {"loser": loser, "fields": " and ".join(absent_)}))
            if same_by:
                detail.append(bind(words["itemSameValueBy"], {
                    "others": " and ".join(same_by), "holds": "holds" if len(same_by) == 1 else "hold",
                    "their": "its" if len(same_by) == 1 else "their", "loser": loser}))
            # (sixth review, Pilgrim r5 S1) where it holds for some rows of a field only, said with its count
            some_ = [(f_, n_) for f_, n_ in win_base_c.items() if f_ not in win_base and 0 < n_ < (rows_c.get(f_) or 0)]
            if some_ and words.get("itemWinnerBaseSome"):
                ex_ws_ = (g.get("_winnerBaseEx") or {}).get(some_[0][0])
                detail.append(bind(words["itemWinnerBaseSome"], {
                    "count": fmt_n(sum(n_ for _, n_ in some_)), "winner": winner, "loser": loser,
                    "fields": " and ".join(f_ for f_, _ in some_),
                    "example": bind(words["itemWinnerBaseExample"], {"example": ex_ws_, "field": some_[0][0]})
                    if ex_ws_ and words.get("itemWinnerBaseExample") else ""}))
            if win_base:
                ex_wb_ = (g.get("_winnerBaseEx") or {}).get(win_base[0])
                detail.append(bind(words["itemWinnerBase"], {
                    "winner": winner, "fields": " and ".join(win_base),
                    "example": bind(words["itemWinnerBaseExample"], {"example": ex_wb_, "field": win_base[0]})
                    if ex_wb_ and words.get("itemWinnerBaseExample") else ""}))
        g.pop("_winnerBaseEx", None)
        # (round 3, Winterhold 17) said after the item's examples, with an example of its own rows, so the item's
        # examples never read as this sentence's
        carried_lines_ = []
        ex_wc_ = g.pop("_winnerCarriesEx", None) or {}
        for (wc_, oc_), nc_ in (g.pop("_winnerCarries", None) or {}).items():
            if words.get("itemWinnerCarries"):
                ex_ = ex_wc_.get((wc_, oc_))
                carried_lines_.append(bind(words["itemWinnerCarries"], {
                    "winner": wc_, "origin": oc_, "count": fmt_n(nc_),
                    "all": "every one" if nc_ == g["count"] else f"{fmt_n(nc_)} of them",
                    "example": bind(words["itemWinnerBaseExample"], {"example": ex_[0], "field": ex_[1]})
                    if ex_ and words.get("itemWinnerBaseExample") else ""}))
        n_undecided = g.pop("_notDecided", 0)
        if n_undecided and len(directions) == 1:
            loser, winner = next(iter(directions))
            detail.append(bind(words["itemNotDecided"], {"count": fmt_n(n_undecided), "loser": loser, "winner": winner}))
        # (Missives review 3) a small record item says each side's value (what a patch would forward) and, where
        # skill data has it, what the field does in game
        lk_ = lost_keys_of.get(id(g)) or []
        if g["kind"] == "LOST-EDIT" and lk_ and len(lk_) <= int(words.get("itemValuesMax") or 0):
            for rk_, f_ in lk_:
                ch_ = (chains_of(rk_[0], p_of(rk_[0])) or {}).get(str(rk_[4]))
                low_ = [str(x).lower() for x in (ch_ or {}).get("providers") or []]
                if not f_ or rk_[2].lower() not in low_ or rk_[3].lower() not in low_:
                    continue
                if ex_own and str(ex_own[0]) == str(rk_[4]):
                    continue                   # its values are already quoted where the winner changed it too
                vs_ = []
                for p_ in (rk_[2], rk_[3]):
                    lv_ = recordrows.provider_fields(ch_, f_, low_.index(p_.lower()))
                    vs_.append(plain_value(f_, lv_[f_]) if list(lv_) == [f_] else
                               "; ".join(f"{k_.split('.', 1)[-1]}={v_}" for k_, v_ in sorted(lv_.items())))
                if vs_[0] == vs_[1] or not all(vs_) or max(map(len, vs_)) > int(words.get("itemValueMaxChars") or 80):
                    continue                   # no value, the same reading, or a table too long to quote (C926)
                detail.append(bind(words["itemValues"], {"record": named(rk_[4], rk_[5]), "field": field_word(f_),
                                                         "loser": rk_[2], "lost": vs_[0], "winner": rk_[3], "won": vs_[1]}))
                mean_ = (words.get("fieldInGame") or {}).get(f"{record_type_of(rk_[5])}|{f_}")
                if mean_:
                    detail.append(bind(words["itemFieldInGame"], {"field": field_word(f_), "meaning": mean_}))
                # (third review, Snazzy S4) a field whose effect depends on a flag of the record: whether the winning
                # version sets that flag, where the record data shows it, else that it does not show it
                dep_ = (words.get("fieldDependsOnFlag") or {}).get(f"{record_type_of(rk_[5])}|{f_}")
                if dep_:
                    fv_ = recordrows.provider_fields(ch_, dep_["field"], low_.index(rk_[3].lower())) \
                        if dep_["field"] in (ch_.get("values") or {}) else {}
                    txt_ = " ".join(str(x) for x in fv_.values())
                    read_ = None
                    if not fv_ and dep_.get("read") == "cellFlags":
                        # (round 2, Snazzy S1) the flag the import does not carry, read from the winning plugin's own
                        # record in its file; never said to be absent from the record data
                        got_ = plugin_record(rk_[3], "cell", rk_[4])
                        if got_ and got_.get("value") is not None:
                            txt_, read_ = " ".join(got_.get("flags") or []), rk_[3]
                    detail.append(dep_["unread"] if not fv_ and read_ is None else
                                  dep_["set"] if dep_["flag"] in txt_ else dep_["unset"])
                    if read_ is not None and dep_.get("readFrom"):
                        detail.append(bind(dep_["readFrom"], {"plugin": read_}))
        pool = g.pop("_recordPool", None)
        if pool:
            g["_records"] = (pool[0] + pool[1] + pool[2] + pool[3])[:2]
        if g.get("_records"):
            detail.append(bind(words["itemExamples"], {"examples": "; ".join(g["_records"])}))
        detail.extend(carried_lines_)
        places = g.pop("_places", None)
        said_ = g.pop("_placeSaid", {})
        if places:
            places = collections.Counter({said_.get(k_, k_) + "\x00" + k_: n_ for k_, n_ in places.items()})
            top = [(k_.split("\x00", 1)[0], n_) for k_, n_ in places.most_common(2)]
            if len(places) == 1:
                detail.append(bind(words["itemPlaceOne" if g["count"] == 1 else "itemPlaceAll"], {"place": top[0][0]}))
            else:
                detail.append(bind(words["itemPlaces" if top[0][1] * 2 > sum(places.values()) else "itemPlacesSpread"],
                                   {"places": "; ".join(f"{pl} ({fmt_n(n)})" for pl, n in top),
                                                        "more": (bind(words["itemPlacesMore"], {
                                                            "count": len(places) - len(top),
                                                            "noun": "place" if len(places) - len(top) == 1 else "places",
                                                            # the rest named too, so the reader can find every one
                                                            "rest": "; ".join(f"{pl.split(chr(0), 1)[0]} ({fmt_n(n)})"
                                                                              for pl, n in places.most_common()[len(top):])})
                                                                 if len(places) > len(top) else "")}))
        g.pop("_records", None)
        partners_ = g.pop("_partnerSet", set())
        obj_pairs_ = g.pop("_objPairs", [])
        if g["count"] > 1 and len(partners_) == 1:
            # (Clockwork review 4) a navmesh is a navmesh record, never an object
            on_ = (words.get("objectNouns") or {}).get(g["checkId"]) or ["object", "objects"]
            detail.append(bind(words["itemOnePartner"], {"count": fmt_n(g["count"]), "object": named(next(iter(partners_))),
                                                         "noun": on_[0]}))
        elif obj_pairs_ and g["count"] <= int(words.get("itemObjectPairsMax") or 5):
            detail.append(bind(words["itemObjectPairs"], {"pairs": "; ".join(obj_pairs_)}))
        st_entries = g.pop("_stateEntries", None)
        several = [len(e_["states"]) for e_ in st_entries or [] if len(e_["states"]) > 1]
        if several and words.get("itemStates"):
            # one object in several world states is one clash, and the item says in how many (P72)
            detail.append(bind(words["itemStates"][0 if g["count"] == 1 else 1],
                               {"states": fmt_n(max(several)), "count": fmt_n(len(several))}))

        also_ = g.pop("_alsoFoundBy", None)
        also_sib_ = g.pop("_alsoFromSibling", None) or set()
        if also_ and words.get("itemAlsoFoundBy"):
            for c_, n_ in also_.items():
                detail.append(bind(words["itemAlsoFoundBy"], {"count": fmt_n(n_), "title": title_of(c_)})
                              + (" " + words["itemAlsoFromSibling"] if c_ in also_sib_ and words.get("itemAlsoFromSibling")
                                 else ""))
        sw_depth_, sw_unknown_ = g.pop("_alsoSwitchDepth", None) or {}, g.pop("_alsoSwitchUnknown", None) or set()
        for (sw_, by_, obj_, how_), n_ in (g.pop("_alsoSwitch", None) or {}).items():
            key_ = "itemAlsoSwitchLeaves" if how_ == "leaves" else "itemAlsoSwitchArrives"
            dps_ = sw_depth_.get((sw_, by_, obj_, how_)) or []
            if how_ == "leaves" and dps_ and words.get("itemAlsoSwitchLeavesSunk"):
                # (fourth review, Sewers r2 S1) the overlap with the object that leaves ends, but the check finds the
                # other objects sunk in something else afterwards: said, never "hold only until then"
                key_ = "itemAlsoSwitchLeavesSunk"
            if words.get(key_):
                lo_, hi_ = (min(dps_), max(dps_)) if dps_ else (0, 0)
                detail.append(bind(words[key_], {
                    "count": fmt_n(n_), "switch": sw_, "by": f" ({by_})" if by_ else "", "object": obj_,
                    "sunk": fmt_n(len(dps_)), "range": f"{lo_:.1f} u" if lo_ == hi_ else f"{lo_:.1f} u to {hi_:.1f} u",
                    "trigger": words.get("itemAlsoSwitchTriggerUnknown") or ""
                    if (sw_, by_, obj_, how_) in sw_unknown_ else ""}))
        n_dis = g.pop("_disables", 0)
        n_dis_own = g.pop("_disablesOwn", 0)
        dis_of[id(g)] = n_dis                  # (third review, BPT S1) the headline carries the same count
        dis_own_of[id(g)] = n_dis_own
        if n_dis and n_dis_own == n_dis and words.get("itemDisablesAllOwn"):
            # every one the winner's own removal: said in one sentence (page budget)
            detail.append(bind(words["itemDisablesAllOwn"], {"count": fmt_n(n_dis)}))
        elif n_dis and words.get("itemDisables"):
            detail.append(bind(words["itemDisables"], {"count": fmt_n(n_dis)})
                          + (" " + bind(words["itemDisablesOwn"], {"count": fmt_n(n_dis_own),
                                                                   "lead": "All" if n_dis_own == n_dis else f"In {fmt_n(n_dis_own)}"})
                             if n_dis_own and words.get("itemDisablesOwn") else ""))
        for m_, n_ in (g.pop("_baseNavCarried", None) or {}).items():
            if n_ == g["count"] and words.get("baseNavCarriedName"):
                # (reviewer on faa9ce47) every row is a link of a base-game navmesh the mod only carries: the pair is
                # named as the evaluated mod against the base game's navmesh records, not as that mod's clash
                g.setdefault("pairNames", {})[m_] = bind(words["baseNavCarriedName"], {"mod": m_})
            if words.get("itemBaseNavCarried"):
                detail.append(bind(words["itemBaseNavCarried"], {
                    "lead": f"All {fmt_n(n_)} are" if n_ == g["count"] else f"{fmt_n(n_)} of them are", "mod": m_}))
        for (by_, m_), n_ in (g.pop("_navAdded", None) or {}).items():
            if words.get("itemNavAdded"):
                detail.append(bind(words["itemNavAdded"], {
                    "lead": f"All {fmt_n(n_)} are" if n_ == g["count"] else f"{fmt_n(n_)} of them are", "by": by_,
                    "carried": "" if by_.lower() == m_.lower() else bind(words.get("itemNavAddedCarried") or "", {"mod": m_})}))
        def objs_(n_):
            return "object" if n_ == 1 else "objects"
        sw_all_ = g.pop("_switchOff", None) or {}
        nm_words_ = words.get("noModelMeaning") or {}

        def sw_order(kv):
            h_ = kv[0][2]
            return (("on", "onNoModel", "onMarker", "parent").index(h_[0] if isinstance(h_, tuple) else h_),
                    str(h_[1]) if isinstance(h_, tuple) else "")
        for (lo_, wi_, how_), ex_ in sorted(sw_all_.items(), key=sw_order):
            typ_ = how_[1] if isinstance(how_, tuple) else None
            how_ = how_[0] if isinstance(how_, tuple) else how_
            key_ = {"on": "itemSwitchOffDrawn", "onNoModel": "itemSwitchOffNoModel",
                    "onMarker": "itemSwitchOffMarker"}.get(how_, "itemSwitchOffParent")
            n_ex_ = 2
            if how_ == "onNoModel" and (lo_, wi_, "on") in sw_all_ and words.get("itemSwitchOffNoModelMore"):
                key_, n_ex_ = "itemSwitchOffNoModelMore", 1     # said after the drawn ones, short (page budget)
            # (fifth review) one sentence per base type, its meaning from skill data: never "shows nothing"
            forms_ = (nm_words_.get("types") or {}).get(typ_) or nm_words_.get("default") or ["", ""]
            meaning_ = bind(forms_[0 if len(ex_) == 1 else 1], {"loser": lo_, "winner": wi_})
            if words.get(key_):
                detail.append(bind(words[key_], {"count": fmt_n(len(ex_)), "objects": objs_(len(ex_)), "loser": lo_,
                                                 "winner": wi_, "examples": "; ".join(list(ex_.values())[:1 if how_ == "onMarker" else n_ex_]),
                                                 "meaning": meaning_}))
        for m_, s_ in (g.pop("_startsOffLoser", None) or {}).items():
            if words.get("itemStartsOffLoser"):
                detail.append(bind(words["itemStartsOffLoser"], {"count": fmt_n(len(s_)), "objects": objs_(len(s_)),
                                                                 "loser": m_}))
        for (wi_, lo_), ex_ in (g.pop("_winnerStartsOffUnref", None) or {}).items():
            if words.get("itemWinnerStartsOffUnref"):
                detail.append(bind(words["itemWinnerStartsOffUnref"], {
                    "count": fmt_n(len(ex_)), "objects": objs_(len(ex_)), "winner": wi_, "loser": lo_,
                    "examples": "; ".join(list(ex_.values())[:2])}))
        s_off = g.pop("_startsOff", None) or set()
        if s_off and words.get("itemStartsOff"):
            detail.append(bind(words["itemStartsOff"], {"count": fmt_n(len(s_off)), "objects": objs_(len(s_off))}))
        for f4_, ds4_ in (g.pop("_leafDiff", None) or {}).items():
            leaves_ = {d_[0] for d_ in ds4_}
            if (len(leaves_) == 1 and len(next(iter(leaves_))) == 1 and next(iter(leaves_))[0].endswith("Flags")
                    and words.get("itemLeafFlagsOnly")):
                pairs4_ = list(dict.fromkeys((d_[1] or "none", d_[2] or "none") for d_ in ds4_))
                detail.append(bind(words["itemLeafFlagsOnly"], {
                    "field": field_word(f4_), "values": "; ".join(f"{a_} against {b_}" for a_, b_ in pairs4_[:2])}))
        prev_ = g.pop("_prevWinner", None) or set()
        if len(directions) == 1 and len(prev_) == 1 and None not in prev_ and words.get("itemPreviousWinner"):
            lo4_, wi4_ = next(iter(directions))
            p4_, o4_ = next(iter(prev_))
            if str(p4_).lower() != str(lo4_).lower():
                # (seventh review round 2) the value used is named by its one owner, with the plugin that carries it
                detail.append(bind(words["itemPreviousWinnerCarried"], {"winner": wi4_, "prev": p4_, "owner": o4_})
                              if o4_ and str(o4_).lower() != str(p4_).lower() and words.get("itemPreviousWinnerCarried")
                              else bind(words["itemPreviousWinner"], {"winner": wi4_, "prev": p4_}))
        n_eff_ = g.pop("_effectPairs", 0)
        g["effectPairs"] = n_eff_          # (sixth review, Pilgrim S2, BPT S2) carried to the totals
        g["_effN"] = g.pop("_notCrossing", 0)
        # (sixth review, Clockwork r5 S1) pairs on objects whose own record change between the same two mods is lost
        # (an admitted record-field row): linked to that item, where the object's place in game is decided
        row_objs_ = g.pop("_rowObjs", None) or []
        if row_objs_ and words.get("itemLinkedLost") and g["kind"] != "LOST-EDIT":
            mods_l_ = {str(m_).lower() for m_ in g["mods"]}
            lost_o_ = {str(e["formKey"]) for e in record if e["kind"] == "LOST-EDIT" and e["admission"] == "admitted"
                       and {e["A"].lower(), e["B"].lower()} == mods_l_}
            linked_ = [(a_, b_) for a_, b_ in row_objs_ if a_ in lost_o_ or b_ in lost_o_]
            if linked_:
                # (tenth review, Snazzy S3) a by-object check counts its own objects in the headline; where some pairs
                # count here only through the partner, the two bases are said apart
                own_ = sum(1 for a_, _ in linked_ if a_ in lost_o_)
                split_ = (bind(words["itemLinkedLostSplit"], {"own": fmt_n(own_), "partner": fmt_n(len(linked_) - own_)})
                          if g["checkId"] in by_object and own_ < len(linked_) and words.get("itemLinkedLostSplit") else "")
                detail.append(bind(words["itemLinkedLost"], {
                    "count": fmt_n(len(linked_)), "pairs": "pairs is" if len(linked_) == 1 else "pairs are",
                    "examples": named(linked_[0][0] if linked_[0][0] in lost_o_ else linked_[0][1])})
                    + (" " + split_ if split_ else ""))
        n_touch_ = g.pop("_touchingPairs", 0)
        n_cop_ = g.pop("_coplanarPairs", 0)
        if n_cop_ and words.get("itemCoplanarPairs"):
            detail.append(bind(words["itemCoplanarPairs"], {"count": fmt_n(n_cop_), "verb": "is" if n_cop_ == 1 else "are"}))
        if n_touch_ and words.get("itemTouchingPairs"):
            detail.append(bind(words["itemTouchingPairs"], {"count": fmt_n(n_touch_), "verb": "touch" if n_touch_ != 1 else "touch"}))
        if n_eff_ and words.get("itemEffectPairs"):
            detail.append(bind(words["itemEffectPairs"], {"count": fmt_n(n_eff_), "all": "All" if n_eff_ == g["count"] else "",
                                                          "verb": "involves" if n_eff_ == 1 else "involve"}))
        for (wi_, lo_), dzs_ in (g.pop("_heightDiff", None) or {}).items():
            if words.get("itemHeightDiff") and all(x_ is not None and abs(x_) >= SEAT_SPOT_UNITS for x_ in dzs_) and (
                    all(x_ < 0 for x_ in dzs_) or all(x_ > 0 for x_ in dzs_)):
                lo_d, hi_d = min(abs(x_) for x_ in dzs_), max(abs(x_) for x_ in dzs_)
                dir_ = ("lower" if all(x_ < 0 for x_ in dzs_) else "higher" if all(x_ > 0 for x_ in dzs_)
                        else "lower or higher")
                acr_ = (g.get("_acrossDiff") or {}).get((wi_, lo_)) or []
                across_ = ""
                if acr_ and max(acr_) >= SEAT_SPOT_UNITS and words.get("itemHeightDiffAcross"):
                    lo_a, hi_a = min(acr_), max(acr_)
                    across_ = bind(words["itemHeightDiffAcross"], {
                        "range": fmt_units(lo_a) if abs(hi_a - lo_a) < 0.5 else f"{fmt_units(lo_a)} to {fmt_units(hi_a)}"})
                detail.append(bind(words["itemHeightDiff"], {
                    "winner": wi_, "loser": lo_, "count": fmt_n(len(dzs_)), "noun": words["fieldNoun"][0 if len(dzs_) == 1 else 1],
                    "range": fmt_units(lo_d) if abs(hi_d - lo_d) < 0.5 else f"{fmt_units(lo_d)} to {fmt_units(hi_d)}",
                    "dir": dir_, "across": across_}))
        g.pop("_acrossDiff", None)
        fn_ = g.pop("_rawFields", None) or {}
        if g["kind"] == "LOST-EDIT" and fn_ and words.get("itemMainFieldInGame"):
            top_f, top_n = collections.Counter(fn_).most_common(1)[0]
            mean_ = field_meaning([], top_f)
            if top_n * 2 > sum(fn_.values()) and mean_ and not any(mean_ in d_ for d_ in detail):
                detail.append(bind(words["itemMainFieldInGame"], {
                    "count": fmt_n(top_n), "field": field_word(top_f), "meaning": mean_,
                    "lead": "All" if top_n == sum(fn_.values()) else "Most"}))
                # (sixth review, Pilgrim r5 S1) every row's field has its meaning said: no "not described" for it
                g["_meaningAll"] = top_n == sum(fn_.values())
        fl_ = g.pop("_fieldsLine", None)
        if fl_:
            detail.append(bind(words["itemFields"], {"fields": fl_}))
        om_ = g.pop("_ownMaster", None)
        if om_:
            detail.append(bind(words["itemOwnMasterGeometry"], {"mod": om_[0], "master": om_[1]}))
        if g.pop("_fromSibling", False) and sibling_record and words.get("itemFromSibling"):
            # counted from the separate evaluation without the patch: said where it is counted, with its file (P95)
            s_b = (sibling or {}).get("bundle")
            s_file = (s_b.output_emit_path(g["checkId"], anchor, "arm") or s_b.output_emit_path(g["checkId"], anchor, None)
                      if s_b is not None else None)
            detail.append(bind(words["itemFromSibling"], {"bundle": sibling_record.get("pathAsGiven") or "",
                                                          "file": s_file or ""}))
        g.pop("_partners", None)
        provenance = g.pop("_provenance", None)
        g["attributionKey"], g["attributionText"] = attr_key, attr_text
        pn_ = [(g.get("pairNames") or {}).get(m_, m_) for m_ in g["mods"]]
        g["plain"] = (f"{fmt_n(g['count'])} {noun}{on} between {pn_[0]} and {pn_[1]}. "
                      + attr_text + (f" {ph['readerQualification']}" if ph.get("readerQualification")
                                     and not (ph.get("qualificationInInGame") and g.get("inGame")) else "")
                      + "".join(" " + d for d in detail) + (f" {provenance}" if provenance else ""))
        # (Pilgrim review 6) a quest or switch clash: its in-game effect from the probe's own row (switch, depth)
        summaries_ = g.pop("_rowSummaries", [])
        st_words = words.get("stateInGame") or {}
        if not g["inGame"] and st_words.get("pattern"):
            for sm_ in summaries_:
                m_ = re.search(st_words["pattern"], sm_)
                what_ = (st_words.get("states") or {}).get(m_.group("state")) if m_ else None
                if what_:
                    # (BPT re-review S2) every row's depth under the same switch: a range and its deepest, never one
                    # row's depth for all
                    ds_ = sorted({float(x_) for s2_ in summaries_ if re.search(st_words["pattern"], s2_)
                                  and re.search(st_words["pattern"], s2_).group("switch") == m_.group("switch")
                                  for x_ in re.findall(r"(\d+(?:\.\d+)?)\s*u inside", s2_)})
                    rng_ = (st_words.get("statesRange") or {}).get(m_.group("state"))
                    if len(ds_) > 1 and rng_:
                        what_ = bind(rng_, {"low": f"{ds_[0]:g} u", "high": f"{ds_[-1]:g} u"})
                    dm_ = re.search(r"(\d+(?:\.\d+)?)\s*u inside", sm_)
                    # (eighth review, Missives 23) "the arriving host encloses the dependent N u deep": the dependent,
                    # the row's other object, sits N u inside the host
                    enc_ = re.search(r"encloses the dependent (\d+(?:\.\d+)?)\s*u deep", sm_)
                    # (seventh review, Sewers S1) a switch state whose direction the check did not find is said without
                    # one, and the depth is the row's own object's, against something the check does not name
                    row_kinds_ = g.get("_rowKinds") or []
                    unknown_ = bool(row_kinds_) and all(k_.endswith("@Unknown") for k_, _ in row_kinds_)
                    objs_ = list(dict.fromkeys(f_ for _, f_ in row_kinds_))
                    line_ = st_words.get("lineUnknown") if unknown_ and st_words.get("lineUnknown") else st_words["line"]
                    what_o_ = (st_words.get("statesOwn") or {}).get(m_.group("state")) if len(objs_) == 1 else None
                    if enc_ and len(objs_) == 1 and (g.get("_rowOthers") or {}).get(objs_[0]):
                        objs_ = [(g.get("_rowOthers") or {})[objs_[0]]]
                        dm_ = enc_
                    # (eighth review, Sewers 27) a direction the check did not find, settled by the plugin files: the
                    # object's enable parent is the switch, not opposite, and the switch starts enabled with no parent
                    # of its own, so an object that leaves with the flip is there from load until the switch turns off
                    sw_ref_ = re.match(r"^[^@]*@([0-9A-Fa-f]{6}:\S+)@Unknown$", row_kinds_[0][0]) if unknown_ and row_kinds_ else None
                    if (sw_ref_ and len(objs_) == 1 and not enc_ and re.search(r"\] leaves\b", sm_)
                            and st_words.get("lineFromLoad")):
                        ch_ = plugin_record(objs_[0].split(":", 1)[1], "placementFull", objs_[0]) or {}
                        par_ = plugin_record(sw_ref_.group(1).split(":", 1)[1], "placementFull", sw_ref_.group(1)) or {}
                        ep_ = ch_.get("enableParent") or []
                        if (ep_ and str(ep_[0]).lower() == sw_ref_.group(1).lower() and not ep_[1]
                                and not ch_.get("initiallyDisabled") and par_ and not par_.get("initiallyDisabled")
                                and not par_.get("enableParent") and not par_.get("deleted")):
                            line_ = st_words["lineFromLoad"]
                            oth_ = (g.get("_rowOthers") or {}).get(objs_[0])
                            # (tenth review, Sewers S1) the other object is what the object sits inside only where the
                            # row says so; a row that says "something else" names nothing it is inside
                            unn_ = st_words.get("insideUnnamedPattern")
                            own_w_ = ((st_words.get("statesOwnUnnamed") if unn_ and re.search(unn_, sm_) else None)
                                      or st_words.get("statesOwnOther") or {})
                            if oth_ and own_w_.get(m_.group("state")):
                                what_o_ = own_w_[m_.group("state")].replace("{other}", named(oth_))
                    g["inGame"] = bind(line_, {"switch": m_.group("switch"), "what": bind(what_o_ if what_o_ and len(ds_) <= 1
                                                                                        else what_, {
                        "depth": f"{dm_.group(1)} u" if dm_ else "", "object": named(objs_[0]) if objs_ else ""})})
                    break
        g.pop("_rowKinds", None)
        g.pop("_rowOthers", None)
        g["inGameText"] = (f"In game: {g['inGame']}." if g["inGame"] else "" if g.pop("_meaningAll", False)
                           else words["inGameMissing"])
        n_eff_ = g.pop("_effN", 0)
        if n_eff_ and g["inGame"] and g["kind"] != "LOST-EDIT":
            # (fifth review, Pilgrim S4) the in-game line speaks only of the pairs with no visual-effect model
            g["inGameText"] = ("" if n_eff_ >= g["count"] else bind(words.get("inGameOthers") or "In game, for the other {count} {unit}: {what}.", {
                "count": fmt_n(g["count"] - n_eff_), "unit": unit_noun(g["checkId"], g["kind"], g["count"] - n_eff_),
                "what": g["inGame"]}))
        g["shownAs"] = ("pair" if not multi or g["attribution"] == "needs-both" else
                        "alone" if g["attribution"] == "single-mod" else "unseparated")
        items.append(g)
    # the pair's clashes first, then the ones that could not be separated, then those of one mod alone (own heading)
    shown_rank = {"pair": 0, "unseparated": 1, "alone": 2}
    items.sort(key=lambda g: (shown_rank[g["shownAs"]], -g["count"], g["checkId"], g["mods"]))
    # W-B4: overwrite history between the evaluated mods is not a clash, but a navmesh one mod's navmesh replaces with
    # a measured loss of ground (its predicate holds) is a measured fact of this setup: said here, with the largest
    # losses, and tied to the broken links counted above that point into a replaced navmesh. No claim about NPCs.
    loss_lines = []
    ground_still = []                  # (loser, winner, navmesh records) whose lost ground the patch setup still loses
    ground_lost = []                   # (loser, winner, navmesh records losing ground, still so with the patch or None)
    by_pair = collections.OrderedDict()
    for rk, hit in sorted(p_rows.items(), key=lambda kv: tuple(str(x) for x in kv[0])):
        if not (hit["history"] and rk[0] == "geometry.navmesh-ownership" and rk[2].lower() != rk[3].lower()):
            continue
        # two evaluated mods: the loss between them; one evaluated mod: its loss against any other mod (fourth blind
        # review, S2), never the base game
        if not ({rk[2].lower(), rk[3].lower()} <= pt_lower if multi else
                ({rk[2].lower(), rk[3].lower()} & pt_lower and not is_base_game(rk[2], words)
                 and not is_base_game(rk[3], words))):
            continue
        by_pair.setdefault((rk[2], rk[3]), []).append(hit)
    # The setup with the patch's own re-measure of P's rows (an emit's `Predicates`: every row of P measured again with
    # the patch): key (Kind, A, B, FormKey, Other) -> (Physical, Detail). Whether a loss is still there with the patch is
    # read from it, never from whether the row is still flagged: a row stops being flagged when the new winner declares
    # the overwritten mod a master, with the same ground lost (fifth blind review, W-S1).
    remeasure_with_patch_cache = {}

    remeasure_with_patch_path = {}

    def remeasure_with_patch(check_id):
        if check_id not in remeasure_with_patch_cache:
            remeasure_with_patch_cache[check_id] = None
            bundle = ctx.get("mainBundle")
            if bundle is not None and patched_arm is not None and p_of(check_id) == p_arm:
                where = ((patched_arm, "arm") if p_arm == "baseline" else
                         (p_arm, "reverse") if patched_arm == "baseline" else None)
                path = None
                if where:
                    path = bundle.output_emit_path(check_id, *where) or (
                        bundle.output_emit_path(check_id, where[0], None) if where[1] == "arm" else None)
                emit = bundle.emit(path) if path else None
                preds = emit.get("Predicates") if isinstance(emit, dict) else None
                remeasure_with_patch_path[check_id] = path      # (tenth review) the file, said in the evidence index
                if isinstance(preds, list):
                    remeasure_with_patch_cache[check_id] = {
                        (x.get("Kind"), x.get("A"), x.get("B"), x.get("FormKey"), x.get("Other")):
                            (str(x.get("Physical")), str(x.get("Detail") or "")) for x in preds if isinstance(x, dict)}
        return remeasure_with_patch_cache[check_id]
    patch_labels = ctx.get("patchLabels") or {}
    patch_text = (", ".join(patch_labels.get(str(n).lower(), str(n)) for n in patch_names) if len(patch_names) <= 2 else
                  bind(words["patchesShort"], {"count": len(patch_names)}))

    def navmesh_stub(plugin, fk):
        """The plugin's own version of the navmesh has at most skill data navmeshStubTriangles triangles while the base
        game's has more: a stub, how a mod removes a navmesh (read from both plugin files, recorded bytes only)."""
        lim_ = words.get("navmeshStubTriangles")
        fk_ = str(fk or "")
        if lim_ is None or ":" not in fk_:
            return False
        mine_ = plugin_record(plugin, "navmesh", fk_)
        origin_ = fk_.split(":", 1)[1]
        base_ = plugin_record(next((x for x in all_orders.get(p_arm) or [] if x.lower() == origin_.lower()), origin_),
                              "navmesh", fk_) if is_base_game(origin_, words) else None
        return bool(mine_ and mine_.get("triangles") is not None and int(mine_["triangles"]) <= int(lim_)
                    and (base_ is None or base_.get("triangles") is None or int(base_["triangles"]) > int(lim_)
                         or int(base_["triangles"]) == int(mine_["triangles"])))

    def navmesh_base_single(plugin, fk):
        """(tenth review) The plugin's single-triangle version of a base-game navmesh has the base game's own triangle
        count: that version is the base game's single triangle, not a removal (read from both plugin files)."""
        fk_ = str(fk or "")
        lim_ = words.get("navmeshStubTriangles")
        if lim_ is None or ":" not in fk_:
            return False
        origin_ = fk_.split(":", 1)[1]
        if not is_base_game(origin_, words):
            return False
        mine_ = plugin_record(plugin, "navmesh", fk_)
        base_ = plugin_record(next((x for x in all_orders.get(p_arm) or [] if x.lower() == origin_.lower()), origin_),
                              "navmesh", fk_)
        return bool(mine_ and base_ and mine_.get("triangles") is not None
                    and mine_.get("triangles") == base_.get("triangles") and int(mine_["triangles"]) <= int(lim_))

    own_nav_cache = {}

    def own_navmeshes(plugin):
        """(tenth review) {formKey: {triangles, place}} of every navmesh record the plugin's file holds (its own records
        and its overrides), read from its file (sha256 checked); None where the file is not read."""
        key_ = str(plugin).lower()
        if key_ not in own_nav_cache:
            own_nav_cache[key_] = None
            path_, sha_ = plugin_file(plugin)
            if path_ and sha_ and Path(path_).is_file():
                if path_ not in sha_cache:
                    sha_cache[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
                if sha_cache[path_] == sha_:
                    try:
                        own_nav_cache[key_] = pluginread.navmesh_places_all(path_)
                    except pluginread.PluginReadError:
                        pass
        return own_nav_cache[key_]

    def tri_label(label):
        m = re.match(r"^(.*?)\s+(\d+) tris -> (\d+)$", str(label or ""))
        return f"{m.group(1)} {fmt_n(int(m.group(2)))} -> {fmt_n(int(m.group(3)))} triangles" if m else str(label or "")

    def persisting(rows):
        """Of these overwrite-history rows (kind, A, B, form key, label), how many the setup with the patch re-measures as
        still holding the loss of ground, how many it re-measured at all, and the largest losses still there (by the
        re-measure's own count of lost-ground samples)."""
        rm = remeasure_with_patch("geometry.navmesh-ownership") or {}
        got, measured_ = [], 0
        for kind, a_, b_, fk, lab in rows:
            hit_ = rm.get((kind, a_, b_, fk, None))
            if hit_ is None:
                continue
            measured_ += 1
            if hit_[0] == "Holds":
                m_ = re.search(r"(\d+) sample", hit_[1])
                got.append((int(m_.group(1)) if m_ else 0, re.sub(r"\s+\d+ tris -> \d+$", "", str(lab or fk)).strip()))
        got.sort(key=lambda x: (-x[0], x[1]))
        return len(got), measured_, "; ".join(bind(words["historyPersistsExample"], {"place": pl, "samples": fmt_n(s_)})
                                              for s_, pl in got[:3])
    for (loser, winner), hits in by_pair.items():
        held_all = [h for h in hits if h["row"].get("physical") == "Holds"]
        # (blind round 1, Winterhold S1) a losing version that is a stub of a navmesh (skill data navmeshStubTriangles,
        # read from the losing plugin's file) is how a mod removes a navmesh: no ground of the losing mod's is lost there
        stubs = [h for h in held_all if navmesh_stub(loser, h["row"].get("formKey"))]
        # (tenth review, Winterhold S2; Gate A condition a) a single-triangle version that is the base game's own
        # single triangle is no removal: said apart
        stubs_base_ = [h for h in stubs if navmesh_base_single(loser, h["row"].get("formKey"))]
        stubs = [h for h in stubs if h not in stubs_base_]
        held = [h for h in held_all if h not in stubs and h not in stubs_base_]
        if not held and not stubs and not stubs_base_:
            continue
        drops, lost_ = [], []
        for h in held:
            m = re.match(r"^(.*?)\s+(\d+) tris -> (\d+)$", str(h["row"].get("label") or ""))
            if m:
                drops.append((int(m.group(2)) - int(m.group(3)), m.group(1), int(m.group(2)), int(m.group(3)),
                              h["row"].get("formKey")))
            # (Clockwork review 3) the largest losses by the row's own measured samples of lost ground
            s_ = re.search(r"(\d+) sample", str(h["row"].get("physicalDetail") or ""))
            if s_:
                lost_.append((int(s_.group(1)), re.sub(r"\s+\d+ tris -> \d+$", "", str(h["row"].get("label") or
                                                                                      h["row"].get("formKey"))).strip()))
        drops.sort(key=lambda d: (-d[0], str(d[4])))
        lost_.sort(key=lambda x: (-x[0], x[1]))
        replaced = {str(h["row"].get("formKey")) for h in hits}
        links = [r_ for k_, r_ in p_rows.items() if k_[0] == "geometry.navmesh-ownership" and not r_["history"]
                 and {k_[2].lower(), k_[3].lower()} == {loser.lower(), winner.lower()}
                 and any(str(o) in replaced for o in r_["row"].get("other") or ())]
        targets = list(dict.fromkeys(str(o) for r_ in links for o in r_["row"].get("other") or () if str(o) in replaced))
        tdesc = []
        for t in targets:
            d = next((x for x in drops if str(x[4]) == t), None)
            tdesc.append(f"{t} ({fmt_n(d[2])} -> {fmt_n(d[3])} triangles)" if d else t)
        n_persist, n_measured, persist_ex = persisting(
            [(str((h["finding"].get("counts") or {}).get("groupKind") or "CLOBBERED"), loser, winner, h["row"].get("formKey"),
              h["row"].get("label")) for h in held])
        # with a re-measure the largest losses are named by its lost ground (below), not by triangles (fifth review, M2)
        # a place is said as a name (N6), never its editor ID or form key
        ex_ = ("; ".join(bind(words["historyPersistsExample"], {"place": named_place(pl_), "samples": fmt_n(n_)})
                         for n_, pl_ in lost_[:3]) if len(lost_) == len(held) else
               "; ".join(f"{named_place(d[1])} {fmt_n(d[2])} -> {fmt_n(d[3])} triangles" for d in drops[:3] if d[0] > 0))
        text = bind(words["historyLossLineMeasured" if (n_persist or not ex_) and words.get("historyLossLineMeasured")
                          else "historyLossLine"], {
            "setup": words["historyLossSetup"][("withoutPatches" if len(patch_names) > 1 else "withoutPatch")
                                               if patched_arm else "together"],
            "winner": winner, "loser": loser, "count": fmt_n(len(hits)), "holds": fmt_n(len(held)),
            "examples": ex_})
        # (fourth review, Clockwork S2) a replacing navmesh with the base game's own triangle count, read from the base
        # game's plugin file: the city mod's navmesh work is undone there, not trimmed
        base_same_ = []
        for d_ in drops:
            fk_ = str(d_[4] or "")
            origin_ = fk_.split(":", 1)[1] if ":" in fk_ else ""
            if origin_ and is_base_game(origin_, words):
                got_ = plugin_record(origin_, "navmesh", fk_)
                if got_ and got_.get("triangles") == d_[3]:
                    base_same_.append(f"{fk_} ({fmt_n(d_[3])} triangles)")
        if stubs and words.get("historyLossStub"):
            text += " " + bind(words["historyLossStub"], {
                "count": fmt_n(len(stubs)), "loser": loser, "version": "version is" if len(stubs) == 1 else "versions are",
                "records": ", ".join(str(h["row"].get("formKey")) for h in stubs[:3])
                + (f" and {fmt_n(len(stubs) - 3)} more" if len(stubs) > 3 else "")})
        if stubs_base_ and words.get("historyLossStubBase"):
            text += " " + bind(words["historyLossStubBase"], {
                "count": fmt_n(len(stubs_base_)), "loser": loser,
                "version": "version is" if len(stubs_base_) == 1 else "versions are",
                "records": ", ".join(str(h["row"].get("formKey")) for h in stubs_base_[:3])
                + (f" and {fmt_n(len(stubs_base_) - 3)} more" if len(stubs_base_) > 3 else "")})
        # (tenth review, Winterhold S2) the losing mod's own navmesh records in the cells of the navmeshes it removed,
        # loaded beside the winning version; read from the plugin files, overlap not measured
        own_ = own_navmeshes(loser)
        if stubs and own_ is not None and words.get("historyLossStubOwnBeside"):
            beside_ = []
            for h in stubs:
                pl_ = (own_.get(str(h["row"].get("formKey"))) or {}).get("place")
                mates_ = [fk2_ for fk2_, v2_ in own_.items() if pl_ and v2_.get("place") == pl_
                          and fk2_.split(":", 1)[1].lower() == str(loser).lower()
                          and (v2_.get("triangles") or 0) > int(words.get("navmeshStubTriangles") or 1)]
                if mates_:
                    beside_.append((str(h["row"].get("formKey")),
                                    sorted(mates_, key=lambda x_: -(own_[x_].get("triangles") or 0))))
            if beside_:
                text += " " + bind(words["historyLossStubOwnBeside"], {
                    "count": fmt_n(len(beside_)), "loser": loser, "winner": winner,
                    "examples": "; ".join(f"{m_[0]} beside {r_}" for r_, m_ in beside_[:1])})
        # (sixth review, Sewers r6 S1) a stub whose winning version has the base game's triangle count: the winner puts
        # back the base game's navmesh where the losing mod removed it, read from both plugin files
        put_back_ = []
        for h in stubs:
            fk_ = str(h["row"].get("formKey") or "")
            origin_ = fk_.split(":", 1)[1] if ":" in fk_ else ""
            if origin_ and is_base_game(origin_, words):
                b_ = plugin_record(origin_, "navmesh", fk_)
                w_ = plugin_record(winner, "navmesh", fk_)
                if (b_ and w_ and b_.get("triangles") is not None and b_.get("triangles") == w_.get("triangles")
                        and int(b_["triangles"]) > int(words.get("navmeshStubTriangles") or 1)):
                    put_back_.append(f"{fk_} ({fmt_n(int(b_['triangles']))} triangles)")
        if put_back_ and words.get("historyLossStubPutBack"):
            text += " " + bind(words["historyLossStubPutBack"], {"winner": winner, "loser": loser,
                                                                  "records": ", ".join(x_.split(" ", 1)[0] for x_ in put_back_[:3])})
            # (eighth review, Sewers 30; Gate A condition b) what it means in game, from each setup's winning version read
            # from the plugin files: without the patch NPCs path on the winner's base-size navmesh; with it, the winning
            # version there, a single triangle again or still full
            if words.get("historyLossStubPutBackInGame"):
                text += " " + bind(words["historyLossStubPutBackInGame"], {"winner": winner, "loser": loser})
            if patched_arm and words.get("historyLossStubPatchStub") and words.get("historyLossStubPatchFull"):
                stub_n_ = int(words.get("navmeshStubTriangles") or 1)
                for x_ in put_back_[:3]:
                    fk2_ = x_.split(" ", 1)[0]
                    pw_ = next(((pl_, got2_) for pl_ in reversed([str(y_) for y_ in all_orders.get(patched_arm) or []])
                                for got2_ in [plugin_record(pl_, "navmesh", fk2_)] if got2_ and got2_.get("triangles") is not None), None)
                    if pw_ is None:
                        continue
                    text += " " + bind(words["historyLossStubPatchStub" if int(pw_[1]["triangles"]) <= stub_n_
                                             else "historyLossStubPatchFull"], {
                        "patch": patch_text, "winner": pw_[0], "record": fk2_, "triangles": fmt_n(int(pw_[1]["triangles"]))})
            base_same_ += put_back_
        if base_same_ and words.get("historyLossBaseCount"):
            text += " " + bind(words["historyLossBaseCount"], {"count": fmt_n(len(base_same_)), "winner": winner,
                                                                "records": ", ".join(base_same_[:3])})
        if links:
            text += " " + bind(words["historyLossLinks"], {"links": fmt_n(len(links)), "targets": ", ".join(tdesc)})
        if n_measured and n_persist:
            ground_still.append((loser, winner, n_persist))
        ground_lost.append((loser, winner, len(held), n_persist if n_measured else None))
        if n_measured:
            text += " " + (bind(words["historyLossPersists"][0 if n_persist == 1 else 1], {
                "patch": patch_text, "count": fmt_n(n_persist), "examples": persist_ex}) if n_persist else
                bind(words["historyLossRestored"], {"patch": patch_text}))
        loss_lines.append({"text": text, "findingIds": list(dict.fromkeys(
            h["finding"].get("id") for h in held if h["finding"].get("id")))})
    # W4: the asset files a missing-asset check measured absent are named, with the plugins whose records name them and
    # the setups they are missing in; the check then leaves the count-only line
    asset_named = set()
    for check_id in words.get("missingAssetChecks") or []:
        main_b = ctx.get("mainBundle")
        if main_b is None or (check_id, p_arm) not in count_only:
            continue
        got_p = missing_assets(main_b, check_id, p_arm)
        got_w = missing_assets(main_b, check_id, patched_arm) if patched_arm else None
        if got_p is None:
            continue
        asset_named.add(check_id)
        if not got_p:
            continue
        with_paths = {x["path"].lower() for x in got_w or []}
        where = ("both" if got_w is not None and {x["path"].lower() for x in got_p} == with_paths else
                 "withoutOnly" if got_w is not None and not with_paths else "only")
        owners = list(dict.fromkeys(o for x in got_p for o in x["namedBy"]))
        # each file named by its kind (its extension), counted; never every file called a mesh
        ak_ = words.get("missingAssetKinds") or {}
        kinds_ = collections.Counter()
        for x in got_p:
            ext_ = str(x["path"]).rsplit(".", 1)[-1].lower()
            kinds_[ext_ if ext_ in ak_ else "other"] += 1
        by_word_ = collections.Counter()
        for k_, n_ in kinds_.items():
            by_word_[tuple(ak_.get(k_) or ak_.get("other") or ["file", "files"])] += n_
        kinds_text_ = and_list([f"{fmt_n(n_)} {w_[0] if n_ == 1 else w_[1]}" for w_, n_ in by_word_.most_common()])
        noun_key_ = "missingAssetNounKinds" if ak_ and words.get("missingAssetNounKinds") else "missingAssetNoun"
        cap_ = int(ctx.get("rowsPerGroup") or 0) or len(got_p)
        paths_ = "; ".join(x["path"] for x in got_p[:cap_]) + (
            bind(words.get("missingAssetMore") or "", {"count": fmt_n(len(got_p) - cap_)}) if len(got_p) > cap_ else "")
        text = bind(words["missingAssetLine"], {
            "count": fmt_n(len(got_p)), "noun": bind(words[noun_key_][0 if len(got_p) == 1 else 1], {
                "owners": " and ".join(owners) if owners else words["missingAssetOwnersUnknown"], "kinds": kinds_text_}),
            "where": bind(words["missingAssetWhere"][where], {"patch": patch_text}),
            "paths": paths_,
            # (eighth review, Pilgrim 27) what was searched, where every archive read: no install hedge
            "hedge": words["missingAssetSearched"] if words.get("missingAssetSearched") and asset_view_read_whole(
                main_b, check_id, p_arm) else words.get("missingAssetHedge") or ""})
        if where == "only" and got_w:
            text += " " + bind(words["missingAssetWithPatch"], {"patch": patch_text, "count": fmt_n(len(got_w))})
        # the same files missing in a measured setup without the evaluated mod do not come from it
        for t_ in p_targets:
            arm_t_ = arm_without({str(t_).lower()})
            if arm_t_ and (check_id, arm_t_) in count_only and words.get("missingAssetNotSubject"):
                got_t_ = missing_assets(main_b, check_id, arm_t_) or []
                if {x["path"].lower() for x in got_p} <= {x["path"].lower() for x in got_t_}:
                    text += " " + bind(words["missingAssetNotSubject"], {"count": fmt_n(len(got_p)), "subject": t_})
                    break
        # (ninth review, BPT 33; gate A answer 3) what uses each missing mesh, read from every plugin file of each setup:
        # a placed user switched off for good in every setup is not drawn while off; a mesh no placed object uses is said
        # so, with the other uses (a script or a list creating the object) not read. A mesh with a placed user drawn, or a
        # setup whose files do not read, is said as before.
        uw_ = words.get("missingAssetUsers") or {}
        if uw_:
            arms_u_ = [p_arm] + ([patched_arm] if patched_arm and got_w is not None else [])
            plans_ = {a_.get("armId"): (a_.get("requested") or {}).get("plugins") or []
                      for a_ in (main_b.plan.get("arms") or [])}
            none_, off_ = [], []
            for x in got_p:
                if not x.get("records"):
                    continue
                per_ = [setup_placed_users(plans_.get(a_) or [], x["records"], plugin_reads) for a_ in arms_u_]
                if any(u_ is None for u_ in per_):
                    continue
                users_ = sorted({r_ for u_ in per_ for r_ in u_})
                if not users_:
                    none_.append(x["path"])
                elif all(placement_off_for_good(v_[1], words) or v_[1].get("deleted") for u_ in per_ for v_ in u_.values()):
                    off_.append((x["path"], users_))
            where_u_ = (uw_.get("where") or {}).get("both" if len(arms_u_) > 1 else "one") or ["", ""]
            for path_, users_ in off_:
                # one missing mesh: the line has just named it
                text += " " + bind(uw_["offOne" if len(got_p) == 1 and uw_.get("offOne") else "off"][0 if len(users_) == 1 else 1], {
                    "paths": path_, "refs": ", ".join(users_[:3]) + (f" and {fmt_n(len(users_) - 3)} more" if len(users_) > 3 else ""),
                    "where": where_u_[0]})
            if none_:
                text += " " + (bind(uw_["noneAll"][0 if len(none_) == 1 else 1], {"where": where_u_[1]})
                               if len(none_) == len(got_p) else
                               bind(uw_["none"], {"where": where_u_[1], "paths": "; ".join(none_)}))
        loss_lines.append({"text": text, "findingIds": [f_.get("id") for f_ in findings if f_.get("checkId") == check_id
                                                        and f_.get("armId") == p_arm and f_.get("id")][:1]})
    # Sewers review S2: an evaluated mod's own geometry clashes with base-game objects no other mod edits are not
    # clashes between mods, but they are measured problems of that mod: said by check, never only in a set-aside count
    base_geo = collections.OrderedDict()
    type_only_re = re.compile(words["seamHeightsPattern"]) if words.get("seamHeightsPattern") else None
    # a cell whose counted lost edit is its mod putting back the value from before its master's change: a one-sided
    # seam there is that change seen at the cell's boundary, said as that (Missives review B2)
    put_cells = {str(rk_[4]): (rk_[3].lower(), rk_[2]) for rk_ in put_back if adm_of.get(rk_) == "undoesMaster"}
    seam_cells = {}
    for e in record:
        if (e["admission"] == "baseGame" and e["kind"] != "LOST-EDIT"
                and (is_geometry_check(e["checkId"], words) or state_split(e["kind"])[0] in one_sided_res)
                and {e["A"].lower(), e["B"].lower()} & pt_lower and e["A"].lower() != e["B"].lower()):
            mod_ = e["A"] if e["A"].lower() in pt_lower else e["B"]
            side_ = state_split(e["kind"])[0] in one_sided_res     # the other side carries the base game's value
            if side_:
                hit_ = [(c_, put_cells[c_][1]) for c_ in [str(e["formKey"]), *map(str, e.get("other") or ())]
                        if put_cells.get(c_, ("",))[0] == mod_.lower()]
                if hit_:
                    side_ = ("putBack", hit_[0][1])
                    seam_cells.setdefault((mod_, side_), set()).update(c_ for c_, _ in hit_)
            c_ = base_geo.setdefault((mod_, side_), collections.OrderedDict()).setdefault(e["checkId"], {"n": 0, "fids": [], "kind": e.get("itemKind") or e["kind"], "pairs": set()})
            # a pair of objects is one clash however many world states show it (Pilgrim's second review, S5)
            base_k, in_st = state_split(e["kind"])
            pair_ = ((base_k, str(e["formKey"]), tuple(sorted(map(str, e.get("other") or ())))) if in_st is not None
                     else (e["kind"], str(e["formKey"]), e.get("summary"), tuple(map(str, e.get("other") or ()))))
            # every finding is linked, also a world state of an object counted once (Pilgrim review 4, C929)
            if e.get("findingId") and e["findingId"] not in c_["fids"]:
                c_["fids"].append(e["findingId"])
            if pair_ in c_["pairs"]:
                continue
            c_["pairs"].add(pair_)
            c_["n"] += 1
            if type_only_re is not None and type_only_re.search(str(e.get("summary") or "")):
                m_ = type_only_re.search(str(e.get("summary") or ""))
                c_["typeOnly"] = c_.get("typeOnly", 0) + (1 if m_.group("h1") == m_.group("h2") else 0)
            # (Pilgrim review 4) the object, by kind and editor ID from the probe's own words, and its place
            ob_ = re.search(r"\[(\w+) ([^\]\s]+)\]", str(e.get("summary") or ""))
            if ob_:
                kind_ = (kinds_.get("types") or {}).get(ob_.group(1), "")
                pl_ = str((p_rows.get((e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])) or {}).get("row", {}).get("label") or "")
                pl_ = named_place(pl_) if pl_ else pl_                  # a place is said as a name (N6)
                # (sixth review, Sewers S3) an object of a Creation Club plugin is named with its plugin, not as the
                # base game's
                listed_ = {str(x).lower() for x in words.get("baseGamePlugins") or []}
                cc_ = next((str(x).split(":", 1)[1] for x in [e["formKey"], *(e.get("other") or ())]
                            if ":" in str(x) and is_base_game(str(x).split(":", 1)[1], words)
                            and str(x).split(":", 1)[1].lower() not in listed_), None)
                c_.setdefault("objs", collections.OrderedDict()).setdefault(pl_, {})[str(e["formKey"])] = (
                    (f"{kind_} {ob_.group(2)}" if kind_ else ob_.group(2)) + (f" of {cc_}" if cc_ else ""))
            if e.get("source") == "sibling":
                c_["sibling"] = True
    for (mod_, side_), by_c in base_geo.items():
        line_ = words.get("baseGameSidePutBackLine" if isinstance(side_, tuple) else
                          "baseGameSideLine" if side_ else "baseGameGeometryLine")
        if line_:
            loss_lines.append({"text": bind(line_, {
                "mod": mod_, "master": side_[1] if isinstance(side_, tuple) else "",
                "cells": fmt_n(len(seam_cells.get((mod_, side_)) or ())), "parts": "; ".join(
                bind(words["seamTypeOnlyPart"], {"count": fmt_n(c_["n"])}) if c_.get("typeOnly") == c_["n"] and
                words.get("seamTypeOnlyPart") else
                f"{fmt_n(c_['n'])} {unit_noun(cid_, c_['kind'], c_['n'])} ({title_of(cid_)}"
                + (words.get("baseGameFromSibling") or "" if c_.get("sibling") else "")
                + (": " + "; ".join(bind(words["baseGameObjectsIn"], {"objects": and_list(list(o_.values())), "place": pl_})
                                    if pl_ else and_list(list(o_.values())) for pl_, o_ in c_["objs"].items())
                   if c_.get("objs") else "") + ")" for cid_, c_ in by_c.items())}),
                # every finding of a line that names its objects is linked (Pilgrim review 4)
                "findingIds": [f_ for c_ in by_c.values() for f_ in c_["fids"]][:4],
                "linkAll": any(c_.get("objs") for c_ in by_c.values())})
    # P63: a mod changing a value of a plugin it lists as a master is said as that, one line per mod and master, with
    # the count by check, never as a clash between mods (its count is in `setAside`, said by this line alone)
    own_master = collections.OrderedDict()
    for e in record:
        if e["admission"] != "ownMaster":
            continue
        dep, mas = master_of(masters, e["A"], e["B"])
        by_check = own_master.setdefault((dep, mas), collections.OrderedDict())
        c_ = by_check.setdefault((e["checkId"], e["kind"]), {"n": 0, "findingIds": []})
        c_["n"] += 1
        if e.get("findingId") and e["findingId"] not in c_["findingIds"]:
            c_["findingIds"].append(e["findingId"])
    for (dep, mas), by_check in own_master.items():
        parts, fids = [], []
        for (check_id, kind), c_ in sorted(by_check.items(), key=lambda kv: (-kv[1]["n"], title_of(kv[0][0]))):
            fids += c_["findingIds"]
            ph = phrases.get(kind) or {}
            noun_ = ((words["fieldNoun"][0 if c_["n"] == 1 else 1]) if kind == "LOST-EDIT" else
                     (ph.get("unitSingular") if c_["n"] == 1 else ph.get("unitPlural"))
                     or ("clash" if c_["n"] == 1 else "clashes"))
            parts.append(bind(words["ownMasterPart"], {"count": fmt_n(c_["n"]), "noun": noun_,
                                                       "title": title_of(check_id)}))
        loss_lines.append({"text": bind(words["ownMasterLine"], {
            "mod": dep, "master": mas, "count": fmt_n(sum(c_["n"] for c_ in by_check.values())),
            "parts": ", ".join(parts)}),
            "findingIds": list(dict.fromkeys(fids))})
    def field_carriers(rks_, base_, mod_, fallback=()):
        """(seventh review, Missives S1) The plugins that carry base_'s value of each field in this order, read per field
        from the prediction setup's chain (the attribution read): those after base_ and before mod_ holding base_'s
        value, not base-game plugins. {carrier: fields it carries in}, and how many fields were read; where no field's
        chain was read, the rows' own losers (fallback), each said for every field."""
        per_, read_ = collections.OrderedDict(), 0
        base_c_ = set()                        # (tenth review, Missives S2) base-game plugins carrying it, counted
        for rk_ in rks_:
            got_ = whose_value(p_of(rk_[0]), rk_[0], rk_[4], ((p_rows.get(rk_) or {}).get("row") or {}).get("field"),
                               plugin=base_)
            if got_ is None:
                continue
            provs_, vals_, a_ = got_
            low_ = [str(x).lower() for x in provs_]
            if str(mod_).lower() not in low_:
                continue
            read_ += 1
            b_, d_ = low_.index(str(base_).lower()), low_.index(str(mod_).lower())
            for k_ in range(b_ + 1, d_):
                if vals_[k_] == vals_[b_] and not is_base_game(provs_[k_], words):
                    per_[provs_[k_]] = per_.get(provs_[k_], 0) + 1
                elif vals_[k_] == vals_[b_]:
                    base_c_.add(str(provs_[k_]).lower())
        if not read_:
            return collections.OrderedDict((c_, len(rks_)) for c_ in fallback), len(rks_)
        per_.base_n = len(base_c_)
        return per_, read_

    def carriers_verb(per_):
        return "carries" if len(per_) + (1 if getattr(per_, "base_n", 0) else 0) == 1 else "carry"

    def carriers_text(per_, n_):
        """The carriers, each with its count of fields where it carries the value in fewer than all of them."""
        names_ = [c_ if k_ >= n_ else bind(words.get("carrierInSome") or "{carrier} (in {count} of them)",
                                          {"carrier": c_, "count": fmt_n(k_)}) for c_, k_ in per_.items()]
        return (" and ".join(names_[:3]) + (bind(words.get("carrierMore") or " and {count} more",
                                                   {"count": fmt_n(len(names_) - 3)}) if len(names_) > 3 else "")
                # (tenth review, Missives S2) the base game's own plugins carrying it, counted, not named
                + (bind(words["carrierBaseGame"], {"count": fmt_n(per_.base_n)})
                   if getattr(per_, "base_n", 0) and words.get("carrierBaseGame") else ""))

    # coordinator D1: a mod undoing its own master's fix (it puts back the value from before that master's change) is a
    # measured effect, one line per mod and master: the fields, examples, and how many are still so with the patch
    undo_ = collections.OrderedDict()
    for e in record:
        if e["admission"] == "undoesMaster":
            u_ = undo_.setdefault((e["B"], e["A"]), {"fields": collections.OrderedDict(), "findingIds": []})
            u_["fields"].setdefault((str(e["formKey"]), e.get("field")),
                                    (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"]))
            if e.get("findingId") and e["findingId"] not in u_["findingIds"]:
                u_["findingIds"].append(e["findingId"])
    for (mod_, mas_), u_ in undo_.items():
        n_ = len(u_["fields"])
        left_ = (sum(1 for rk_ in u_["fields"].values() if there(patched_arm, rk_))
                 if patched_arm and any((rk_[0], patched_arm) in measured for rk_ in u_["fields"].values()) else None)
        # (fourth review, BPT r3 S2) where the mod also changed the same record's base or position itself, read by the
        # chain's read-same rule, the undone fix is on an object it rebuilt: said with its count, as likely deliberate
        rw_ = collections.OrderedDict((k_, reworked(p_of(rk_[0]), rk_)) for k_, rk_ in u_["fields"].items())
        rw_ = collections.OrderedDict((k_, v_) for k_, v_ in rw_.items() if v_)
        by_what_ = collections.Counter(t_ for v_ in rw_.values() for t_ in dict.fromkeys(v_))
        what_ = ", ".join(f"{lm_words.get('baseWords' if t_ == (lm_words.get('baseField') or 'Base') else 'positionWords') or t_}"
                          f" on {fmt_n(n__)}" for t_, n__ in by_what_.most_common())
        rework_ = (bind(words["undoesMasterRework"], {
            "count": fmt_n(len(rw_)), "what": what_,
            "examples": "; ".join(fk_ for fk_ in dict.fromkeys(k_[0] for k_ in rw_))}) if rw_ and words.get(
            "undoesMasterRework") else "")
        # (sixth review, Clockwork r5 S2) the plugins that carry the master's value in this order, whose record the mod
        # overrides there (set aside as no edit of theirs): named, so the loss is not read as the master's alone
        carr_ = list(dict.fromkeys(e["A"] for e in record if e["admission"] == "notOwnEdit" and e["B"] == mod_
                                   and (str(e["formKey"]), e.get("field")) in u_["fields"]
                                   and e["A"].lower() != str(mas_).lower()))
        # (seventh review, Missives S1) read per field from the chain: each carrier for the fields it carries in
        per_c_, n_c_ = field_carriers(list(u_["fields"].values()), mas_, mod_, carr_)
        rework_ += (bind(words["undoesMasterCarried"], {"carriers": carriers_text(per_c_, n_c_), "master": mas_,
                                                         "verb": carriers_verb(per_c_)})
                    if per_c_ and words.get("undoesMasterCarried") else "")
        loss_lines.append({"text": bind(words["undoesMasterLine"], {
            "rework": rework_,
            "mod": mod_, "master": mas_, "count": fmt_n(n_), "noun": words["fieldNoun"][0 if n_ == 1 else 1],
            "examples": "; ".join(f"{fk_} ({f_})" for fk_, f_ in list(u_["fields"])[:2]),
            "patch": "" if left_ is None else bind(words["undoesMasterPatch"], {
                "patch": patch_text, "left": fmt_n(left_), "count": fmt_n(n_)})}),
            "findingIds": u_["findingIds"][:1]})
    # (sixth review, Clockwork r6 S1) a navmesh the navmesh check judges by links and ground (navmeshCheck rows) may
    # also drop the losing version's door links. Read from the plugin files: the doors the losing version links and the
    # winning one does not, kept where no navmesh of the setup links the door at its last version and the door's own
    # last version still leads somewhere (a teleport) and is not off for good (gate A condition R2); with the patch,
    # how many still have none. A measured line, not counted (no check judges door links), no NPC claim.
    door_lines_read = False
    door_lines_said = False                    # (tenth review, Snazzy S4) a door-links line was printed
    door_entries_ = []                         # (tenth review, Clockwork S1) each line's evidence-index entry
    nav_rows_ = [e for e in record if e["admission"] == "navmeshCheck"]
    main_b6_ = ctx.get("mainBundle")
    arm_rows6_ = {a_.get("armId"): (a_.get("requested") or {}).get("plugins") or []
                  for a_ in ((main_b6_.plan.get("arms") if main_b6_ else None) or [])}
    scan6_ = {}

    def setup_doors(arm_):
        """{navmesh formKey: (plugin, doors)} at each navmesh's last version in the setup; None if a file is not read."""
        if arm_ not in scan6_:
            out_, per_ = {}, {}
            for x_ in arm_rows6_.get(arm_) or []:
                path_ = str(x_.get("path") or "")
                if not path_ or not Path(path_).is_file():
                    out_ = None
                    break
                if path_ not in _PLUGIN_SHA:
                    _PLUGIN_SHA[path_] = hashlib.sha256(Path(path_).read_bytes()).hexdigest().lower()
                if _PLUGIN_SHA[path_] != str(x_.get("sha256") or "").lower():
                    out_ = None
                    break
                try:
                    got_ = pluginread.navmesh_doors_all(path_)
                except pluginread.PluginReadError:
                    out_ = None
                    break
                for nfk_, ds_ in got_.items():
                    out_[nfk_.lower()] = (x_.get("name"), {d_.lower() for d_ in ds_})
                    per_[(str(x_.get("name")).lower(), nfk_.lower())] = {d_.lower() for d_ in ds_}
            scan6_[arm_] = (out_, per_) if out_ is not None else (None, None)
            plugin_reads.append({"read": "navmeshDoors", "setup": arm_, "complete": out_ is not None})
        return scan6_[arm_]
    if nav_rows_ and words.get("doorLinksLine"):
        last_p_, per_p_ = setup_doors(p_arm)
        if last_p_ is not None:
            door_lines_read = True
            lost_by_ = collections.OrderedDict()           # winner -> loser -> {door: navmesh}
            for e in nav_rows_:
                nfk_ = str(e["formKey"]).lower()
                lose_, win_ = per_p_.get((e["A"].lower(), nfk_)), per_p_.get((e["B"].lower(), nfk_))
                if lose_ is None or win_ is None:
                    continue
                for d_ in sorted(lose_ - win_):
                    if any(d_ in ds_ for _, ds_ in last_p_.values()):
                        continue                           # another navmesh links it
                    dfk_ = next((f"{d_[:6].upper()}:{x_.get('name')}" for x_ in arm_rows6_.get(p_arm) or []
                                 if str(x_.get("name")).lower() == d_.split(":", 1)[1]), None)
                    dv_ = setup_version(arm_rows6_.get(p_arm), dfk_) if dfk_ else None
                    if not dv_ or not dv_[1] or not dv_[1].get("teleport") or placement_off_for_good(dv_[1], words):
                        continue                           # a door that leads nowhere, or is off for good (R2)
                    lost_by_.setdefault(e["B"], collections.OrderedDict()).setdefault(e["A"], collections.OrderedDict())[dfk_] = str(e["formKey"])
            last_w_ = setup_doors(patched_arm)[0] if patched_arm else None
            for winner_, by_loser_ in lost_by_.items():
                door_lines_said = True
                doors_ = [d_ for m_ in by_loser_.values() for d_ in m_]
                navs_ = {n_ for m_ in by_loser_.values() for n_ in m_.values()}
                still_ = ([d_ for d_ in doors_ if not any(d_.lower() in ds_ for _, ds_ in last_w_.values())]
                          if last_w_ is not None else None)
                loss_lines.append({"text": bind(words["doorLinksLine"], {
                    "winner": winner_, "count": fmt_n(len(doors_)), "records": fmt_n(len(navs_)),
                    "recordNoun": "navmesh record" if len(navs_) == 1 else "navmesh records",
                    "doorNoun": "door" if len(doors_) == 1 else "doors",
                    "parts": "; ".join(bind(words["doorLinksPart"], {
                        "loser": l_, "count": fmt_n(len(m_)), "examples": ", ".join(list(m_))})
                        for l_, m_ in by_loser_.items()),
                    "patch": "" if still_ is None else bind(words["doorLinksPatch"], {
                        "patch": patch_text, "count": fmt_n(len(still_)), "has": "has" if len(still_) == 1 else "have",
                        "examples": ", ".join(still_) or "-"})}),
                    # (tenth review, Clockwork S1) the line links its own entry in the evidence index: each door with
                    # the navmesh record that dropped its link, the plugin whose version held it and the winner's
                    "findingIds": [f"door-links-{len(door_entries_) + 1}"]})
                door_entries_.append({"anchor": f"door-links-{len(door_entries_) + 1}", "winner": winner_,
                                      "rows": [{"door": d_, "navmesh": n_, "held": l_, "wins": winner_,
                                                "still": still_ is not None and d_ in still_}
                                               for l_, m_ in by_loser_.items() for d_, n_ in m_.items()],
                                      "patchRead": still_ is not None})
    # (third review, Missives S1) a base-game plugin's own fix that the winner's value drops, where the losing mod only
    # carries or restores it: one line per winner and base-game plugin, naming the plugins that carry the value
    base_drop = collections.OrderedDict()
    undone_once = {(str(e["formKey"]), e.get("field"), e["B"]) for e in record if e["admission"] == "undoesMaster"}
    for e in record:
        # a winner putting back the value from before its own master's fix is said on that line, once (C810)
        if (e["admission"] == "notOwnEdit" and e.get("baseValueOf")
                and (str(e["formKey"]), e.get("field"), e["B"]) not in undone_once):
            d_ = base_drop.setdefault((e["B"], e["baseValueOf"]), {"fields": collections.OrderedDict(),
                                                                   "carriers": [], "findingIds": []})
            d_["fields"].setdefault((str(e["formKey"]), e.get("field")), e.get("label") or "")
            # (seventh review, Missives S2) the value lost, where it names records: said with their editor IDs
            lv_ = " ".join(map(str, (e.get("values") or {}).get("lost") or [])) if isinstance(
                (e.get("values") or {}).get("lost"), list) else str((e.get("values") or {}).get("lost") or "")
            if fk_any_re.search(lv_):
                d_.setdefault("lostIds", {}).setdefault((str(e["formKey"]), e.get("field")), ", ".join(
                    dict.fromkeys(m_.group(0) for m_ in fk_any_re.finditer(lv_))))
            d_.setdefault("rows", {}).setdefault((str(e["formKey"]), e.get("field")),
                                                 (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"]))
            # (sixth review, Missives S5) a water height outside any height in the world (skill data bound): its
            # effect in game is not read here, never said lost in game alone
            wb_ = words.get("waterHeightOutside") or {}
            if str(e.get("field")) in (wb_.get("fields") or []):
                for v_ in (e.get("values") or {}).get("lost") or []:
                    m_ = re.search(r"=\s*(-?[\d.]+(?:[eE][-+]?\d+)?)\s*$", str(v_))
                    if m_ and abs(float(m_.group(1))) >= float(wb_.get("bound") or 1e9):
                        d_.setdefault("outside", collections.OrderedDict())[str(e["formKey"])] = m_.group(1)
            if e["A"] not in d_["carriers"]:
                d_["carriers"].append(e["A"])
            if e.get("findingId") and e["findingId"] not in d_["findingIds"]:
                d_["findingIds"].append(e["findingId"])
    def base_drop_meaning(d_):
        """(ninth review, Missives 30; gate A answer 1) each field the line names: what it does in game where the skill
        data holds it (fieldInGame), else that it is not read here; a water height the outside clause covers is skipped."""
        if not words.get("baseValueDroppedNotRead"):
            return ""
        means_, unread_ = [], []
        wf_ = set((words.get("waterHeightOutside") or {}).get("fields") or [])
        for fk_, f_ in list(d_["fields"])[:2]:
            if f_ in wf_ and fk_ in (d_.get("outside") or {}):
                continue
            rk_ = (d_.get("rows") or {}).get((fk_, f_))
            m_ = field_meaning([record_type_of(rk_[5])] if rk_ else [], f_)
            if m_:
                t_ = bind(words["itemFieldInGame"], {"field": field_word(f_), "meaning": m_})
                if t_ not in means_:
                    means_.append(t_)
            elif field_word(f_) not in unread_:
                unread_.append(field_word(f_))
        return "".join(" " + t_ for t_ in means_) + (" " + bind(words["baseValueDroppedNotRead"], {
            "fields": and_list(unread_), "verb": "does" if len(unread_) == 1 else "do"}) if unread_ else "")

    for (mod_, base_), d_ in base_drop.items():
        n_ = len(d_["fields"])
        # (fourth review, Clockwork r3 S1) what the setup with the patch has: whether its winner holds the base-game
        # plugin's value, read by the record chain's read-same rule
        kept_, later_, none_ = None, collections.OrderedDict(), collections.Counter()
        if patched_arm and words.get("baseValueDroppedPatch"):
            kept_ = 0
            for rk_ in (d_.get("rows") or {}).values():
                ch_ = (chains_of(rk_[0], patched_arm) or {}).get(str(rk_[4]))
                f_ = str(((p_rows.get(rk_) or {}).get("row") or {}).get("field") or "")
                low_ = [str(x).lower() for x in (ch_ or {}).get("providers") or []]
                if ch_ and str(base_).lower() in low_ and (f_ in (ch_.get("values") or {}) or ch_.get("allListed")):
                    vs_ = (ch_.get("values") or {}).get(f_) or [()] * len(low_)
                    kept_ += int(vs_[low_.index(str(base_).lower())] == vs_[-1])  # the chain's read-same values
                    # (seventh review, Clockwork S1) a winning value that is neither the base plugin's nor the mod's but
                    # a later change replacing it: who first made it (the earliest provider after the base plugin
                    # holding it as its own change, by the attribution read; decision 1 at Gate A)
                    b_ = low_.index(str(base_).lower())
                    # (seventh review round 2, Snazzy 28 / 73) a winning version holding none of the field holds no later
                    # change: the field is gone there, said so, as the removal line says it
                    held_last_ = {k_: v_ for k_, v_ in recordrows.provider_fields(ch_, f_, len(low_) - 1).items()
                                  if not re.match(r"^has (?:no \S|.* ABSENT$)", str(v_))}
                    if not held_last_ and any(recordrows.provider_fields(ch_, f_, j_) for j_ in range(len(low_) - 1)):
                        # (eighth review, Missives 30) whose version holds none: the winning one, a patch or not
                        none_[str(ch_["providers"][-1])] += 1
                    elif (vs_[-1] != vs_[b_] and (str(mod_).lower() not in low_
                                                  or vs_[-1] != vs_[low_.index(str(mod_).lower())])):
                        # the value's one owner (round 2), where it comes after the base plugin
                        o_ = owner_of(ch_["providers"], vs_, len(low_) - 1)
                        if o_[0] > b_:
                            later_[o_[1]] = later_.get(o_[1], 0) + 1
            if not any((chains_of(rk_[0], patched_arm) or {}).get(str(rk_[4])) for rk_ in (d_.get("rows") or {}).values()):
                kept_ = None
        # (seventh review, Missives S1) the carriers read per field from the chain
        per_c_, n_c_ = field_carriers(list((d_.get("rows") or {}).values()), base_, mod_, d_["carriers"])
        loss_lines.append({"text": bind(words["baseValueDroppedLine"], {
            "mod": mod_, "base": base_, "count": fmt_n(n_), "noun": words["fieldNoun"][0 if n_ == 1 else 1],
            "examples": "; ".join(f"{fk_} ({f_}" + (f": {with_editor_ids((d_.get('lostIds') or {})[(fk_, f_)])}"
                                                    if (fk_, f_) in (d_.get("lostIds") or {}) else "") + ")"
                                  for fk_, f_ in list(d_["fields"])[:2]),
            "verb": carriers_verb(per_c_),
            "patch": "" if kept_ is None else bind(words["baseValueDroppedPatch"], {
                "patch": patch_text, "kept": fmt_n(kept_), "count": fmt_n(n_), "base": base_})
            + "".join(bind(words.get("baseValueDroppedLater") or "", {
                "count": fmt_n(c_), "later": l_, "base": base_}) for l_, c_ in later_.items())
            + "".join(bind(words.get("baseValueDroppedNone") or "", {
                "count": fmt_n(c_), "base": base_,
                "who": words.get("baseValueDroppedNonePatch") or "" if str(w_).lower() in {str(x_).lower() for x_ in patch_names}
                else bind(words.get("baseValueDroppedNoneOther") or "", {"winner": w_})}) for w_, c_ in none_.items()),
            "carriers": carriers_text(per_c_, n_c_)})
            + (" " + bind(words["waterHeightOutside"]["line"], {"examples": "; ".join(
                f"{fk_} ({v_})" for fk_, v_ in list(d_["outside"].items())[:2])})
               if d_.get("outside") and (words.get("waterHeightOutside") or {}).get("line") else "")
            + base_drop_meaning(d_),
            "findingIds": d_["findingIds"][:1]})
    # (sixth review, Missives r5 S2; R4) an evaluated mod's list dropping items the base game's version holds, against
    # versions that only carry the base game's items: its own change against the base game, one line per mod
    drop_ = collections.OrderedDict()
    for e in record:
        if e["admission"] != "winnerListDrop":
            continue
        rk_ = (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])
        d_ = drop_.setdefault(e["B"], {"fields": collections.OrderedDict(), "carriers": [], "findingIds": []})
        d_["fields"].setdefault((str(e["formKey"]), e.get("field")), (rk_, list_drop(rk_) or []))
        if e["A"] not in d_["carriers"]:
            d_["carriers"].append(e["A"])
        if e.get("findingId") and e["findingId"] not in d_["findingIds"]:
            d_["findingIds"].append(e["findingId"])
    for mod_, d_ in drop_.items():
        n_ = len(d_["fields"])
        still_ = None
        if patched_arm:
            still_ = sum(1 for (fk_, f_), (rk_, _) in d_["fields"].items()
                         if [str(x).lower() for x in ((chains_of(rk_[0], patched_arm) or {}).get(fk_) or {}).get("providers") or []][-1:]
                         == [mod_.lower()])
        if words.get("winnerListDropLine"):
            loss_lines.append({"text": bind(words["winnerListDropLine"], {
                "mod": mod_, "count": fmt_n(n_), "noun": words["fieldNoun"][0 if n_ == 1 else 1],
                # (seventh review, Missives S2) each dropped item named with its editor ID
                "examples": "; ".join(f"{named(fk_)} ({f_}: {with_editor_ids(', '.join(it_))})"
                                      for (fk_, f_), (_, it_) in list(d_["fields"].items())[:2]),
                "carriers": " and ".join(d_["carriers"]), "verb": "carries" if len(d_["carriers"]) == 1 else "carry",
                "patch": "" if still_ is None else bind(words["winnerListDropPatch"], {
                    "patch": patch_text, "still": fmt_n(still_), "count": fmt_n(n_)})}),
                "findingIds": d_["findingIds"][:1]})
    # (Missives review 1) the winner's own change to the bits the game reads of a field whose losing change is only in
    # unread bytes: one line per winning mod, with each record and what the read bits mean (skill data)
    own_ = collections.OrderedDict()
    rb_words = words.get("readBitsWords") or {}
    for e in record:
        if e["admission"] != "winnerOwnChange":
            continue
        rk_ = (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])
        rb_ = read_bits(p_of(e["checkId"]), rk_, str(e.get("field") or ""))
        if rb_ is None:
            continue
        o_ = own_.setdefault(e["B"], {"parts": collections.OrderedDict(), "findingIds": [], "noun": None,
                                      "records": (rb_words.get(record_type_of(e["summary"])) or {}).get("records")})
        lab_ = re.match(r"^\S+ (\S+):", str(e.get("summary") or ""))
        for leaf_ in sorted(rb_[1]):
            w_ = (rb_words.get(record_type_of(e["summary"])) or {}).get(leaf_) or {}
            o_["noun"] = o_["noun"] or w_.get("noun") or leaf_
            o_["inGame"] = o_.get("inGame") or w_.get("inGame")
            old_, new_ = rb_[0][leaf_], rb_[1][leaf_]
            if old_ == new_:
                continue
            if w_.get("bits"):
                old_, new_ = (bin(x_ & int(w_["bits"])).count("1") for x_ in (old_, new_))
            o_["parts"].setdefault(str(e["formKey"]), bind(words["winnerOwnChangePart"], {
                "label": lab_.group(1) if lab_ else "", "formKey": e["formKey"], "unit": w_.get("unit") or leaf_,
                "old": fmt_n(old_), "new": fmt_n(new_), "of": (" of " + fmt_n(w_["of"])) if w_.get("of") else ""}))
        if e.get("findingId") and e["findingId"] not in o_["findingIds"]:
            o_["findingIds"].append(e["findingId"])
        # (fourth review, Missives S5) whether the setup with the patch's winning version reads the same bits
        if patched_arm:
            chp_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
            masks_ = field_masks().get(record_type_of(e["summary"])) or {}
            if chp_ and chp_.get("providers") and masks_:
                try:
                    lastf_ = {k_: int(v_) & int(masks_[k_]) for k_, v_ in recordrows.provider_fields(
                        chp_, str(e.get("field") or ""), len(chp_["providers"]) - 1).items() if k_ in masks_}
                    o_.setdefault("withPatch", {})[str(e["formKey"])] = lastf_ == rb_[1]
                except ValueError:
                    pass
    for mod_, o_ in own_.items():
        if o_["parts"]:
            n_ = len(o_["parts"])
            wp_ = o_.get("withPatch") or {}
            loss_lines.append({"text": bind(words["winnerOwnChangeLine"], {
                "mod": mod_, "noun": o_["noun"], "count": fmt_n(n_), "records": (o_["records"] or ["record", "records"])[0 if n_ == 1 else 1],
                "patch": bind(words["winnerOwnChangePatch"], {"patch": patch_text, "left": fmt_n(sum(1 for k_ in o_["parts"] if wp_.get(k_))),
                                                               "count": fmt_n(n_)})
                if words.get("winnerOwnChangePatch") and all(k_ in wp_ for k_ in o_["parts"]) else "",
                "parts": "; ".join(o_["parts"].values()),
                # (fifth review, Missives S2) what the bits do in game, from skill data readBitsWords
                "inGame": bind(words.get("winnerOwnChangeInGame") or "", {"what": o_["inGame"]}) if o_.get("inGame") else ""}),
                "findingIds": o_["findingIds"][:1]})
    # a rebuilt record supersedes the loser's small fix (Pilgrim final review B1): one line per winning mod, with the
    # record facts in plain words, never in the clash counts
    sup_ = collections.OrderedDict()
    for e in record:
        if e["admission"] == "superseded":
            s_ = sup_.setdefault(e["B"], {"rows": [], "findingIds": []})
            s_["rows"].append(e)
            if e.get("findingId") and e["findingId"] not in s_["findingIds"]:
                s_["findingIds"].append(e["findingId"])

    def sup_put_back(e):
        """(tenth review, Snazzy S2) A supplied patch's version wins the superseded row's field in the setup with the
        patches and holds the losing plugin's value (the record import's chain of that setup, read-same rule)."""
        if not patched_arm:
            return False
        ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
        if not ch_ or e.get("field") not in (ch_.get("values") or {}) or not ch_["providers"]:
            return False
        low_ = [str(x).lower() for x in ch_["providers"]]
        vals_ = ch_["values"][e["field"]]
        return (str(e["A"]).lower() in low_ and low_[-1] in {str(x).lower() for x in patch_names}
                and low_[-1] != str(e["A"]).lower() and vals_[low_.index(str(e["A"]).lower())] == vals_[-1])
    sup_back_ = collections.OrderedDict()      # winner -> the superseded rows a patch puts back

    def u_range(xs):
        lo_, hi_ = fmt_units(min(xs)), fmt_units(max(xs))
        return lo_ if lo_ == hi_ else f"{lo_[:-2]}-{hi_}"
    for winner_, s_ in sup_.items():
        losers_ = collections.Counter(e["A"] for e in s_["rows"])
        facts_, kind_rows, place_rows = [], [], []
        for e in s_["rows"]:
            f_ = supersede((e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])) or {}
            (kind_rows if f_.get("kind") == "kind" else place_rows).append((e, f_))
        shown_n = int(words.get("supersededExamples") or 3)
        for e, f_ in kind_rows[:shown_n]:
            eid_ = re.match(r"^\S+ (\S+):", str(e.get("summary") or ""))
            rec_ = f"{e['formKey']}" + (f" ({eid_.group(1)})" if eid_ and not str(e["formKey"]).startswith(eid_.group(1)) else "")
            changes_ = "; ".join(bind(words["supersededChange"], {"what": w_, "old": o_ or "none", "new": n_ or "none"})
                                 for w_, o_, n_ in f_["changes"][:3])
            facts_.append(bind(words["supersededKind"], {"record": rec_, "winner": winner_, "loser": e["A"],
                                                          "noun": f_["noun"], "changes": changes_, "field": f_["field"]}))
        if len(kind_rows) > shown_n:
            facts_.append(bind(words["supersededKindMore"], {
                "count": fmt_n(len(kind_rows) - shown_n), "records": ", ".join(str(e["formKey"]) for e, _ in kind_rows[shown_n:])}))
        if place_rows:
            n_ = len(place_rows)
            facts_.append(bind(words["supersededPlace"], {
                "winner": winner_, "count": fmt_n(n_), "objects": "object" if n_ == 1 else "objects",
                "across": u_range([f_["across"] for _, f_ in place_rows]),
                "seat": u_range([f_["seat"] for _, f_ in place_rows]),
                "way": words["supersededUp" if all(f_["up"] for _, f_ in place_rows) else
                             "supersededDown" if not any(f_["up"] for _, f_ in place_rows) else "supersededSeat"],
                "examples": ", ".join(str(e["formKey"]) for e, _ in place_rows[:2])}))
        n_ = len(s_["rows"])
        # (tenth review, Snazzy S2) where a supplied patch puts the losing value back, the line says so and does not
        # call that change one that no longer applies (the patch author's call is the opposite)
        back_ = [e for e in s_["rows"] if sup_put_back(e)]
        if back_:
            sup_back_[winner_] = back_
        loss_lines.append({"text": bind(words["supersededLinePatched"] if back_ and words.get("supersededLinePatched")
                                        else words["supersededLine"], {
            "winner": winner_, "count": fmt_n(n_), "noun": words["fieldNoun"][0 if n_ == 1 else 1],
            "losers": ", ".join(m_ if len(losers_) == 1 else f"{m_} {fmt_n(c_)}" for m_, c_ in losers_.most_common()),
            "patch": patch_text, "back": fmt_n(len(back_)), "examples": ", ".join(str(e["formKey"]) for e in back_[:2]),
            "facts": " ".join(facts_)}),
            "findingIds": s_["findingIds"][:1]})
    # P53: the clashes already there with one mod alone, one line per check: each pair with its count and the mod it
    # comes from; the check's in-game sentence once
    alone_lines = collections.OrderedDict()
    for g in items:
        if g["shownAs"] != "alone":
            continue
        ph = phrases.get(g["kind"]) or {}
        mod = str(g["attributionMod"] or "")
        elsewhere = mod.lower() not in {m.lower() for m in g["mods"]}
        third = next((m for m in g["mods"] if m.lower() != mod.lower()), g["mods"][0]) if not elsewhere else \
            " and ".join(g["mods"])
        line = alone_lines.setdefault(g["checkId"], {"title": g["title"], "inGameText": g["inGameText"],
                                                     "unit": ph.get("unitPlural") or "clashes", "groups": {}})
        key = bind(words["aloneFrom" + ("Elsewhere" if elsewhere else "")], {"mod": mod})
        # (Winterhold review 2) a lost change already there with this mod alone is lost, in that mod's own setup, to
        # the plugin that wins it there: named from that setup's own rows (by record, field and losing plugin)
        alone_win = collections.Counter()
        if g["kind"] == "LOST-EDIT" and not elsewhere and s_arms.get(mod):
            idx_ = {}
            for rk2_, h2_ in (rows_by.get((g["checkId"], s_arms[mod])) or {}).items():
                if rk2_[1] == "LOST-EDIT":
                    idx_.setdefault((str(rk2_[4]), h2_["row"].get("field"), str(rk2_[2]).lower()), set()).add(rk2_[3])
            for rk_, f_ in lost_keys_of.get(id(g)) or []:
                for w_ in idx_.get((str(rk_[4]), f_, str(rk_[2]).lower())) or ():
                    alone_win[w_] += 1
        win_ = next(iter(alone_win)) if len(alone_win) == 1 else None
        # the pair's other side is the other evaluated mod: the objects it edits, crossed with this mod alone too
        if (win_ and win_.lower() != third.lower() and not elsewhere and third.lower() in pt_lower
                and words.get("aloneAgainstOther")):
            text_ = bind(words["aloneAgainstOther"], {"other": win_, "evaluated": third, "count": fmt_n(g["count"])})
        else:
            text_ = (bind(words["aloneAgainstEvaluated"], {"other": third, "count": fmt_n(g["count"])})
                     if not elsewhere and third.lower() in pt_lower and words.get("aloneAgainstEvaluated")
                     else f"{third} {fmt_n(g['count'])}")
        line["groups"].setdefault(key, []).append({"text": text_,
                                                   "findingIds": list(g["findingIds"])})
        # (fourth review, Winterhold S3) each mod's clashes keep their own in-game meaning
        # (seventh review) the "for the other N pairs" lead belongs to the item's effect-model split, which this line
        # does not say: its meaning is compared and said without it
        line.setdefault("_inGameBy", collections.OrderedDict()).setdefault(mod, re.sub(
            r"^In game, for the other [\d,]+ [^:]+: ", "In game: ", g["inGameText"] or ""))
    for line in alone_lines.values():
        by_ = line.pop("_inGameBy", None) or {}
        if by_ and len(set(by_.values())) == 1:
            line["inGameText"] = next(iter(by_.values()))
        if len(set(by_.values())) > 1 and words.get("aloneInGamePart"):
            line["inGameText"] = words.get("aloneInGameLead", "In game, ") + "; ".join(
                bind(words["aloneInGamePart"], {"mod": m_, "text": (re.sub(r"^In game: ", "", t_).rstrip(".")[:1].lower()
                                                                     + re.sub(r"^In game: ", "", t_).rstrip(".")[1:])})
                for m_, t_ in by_.items()) + "."

    subject_mods = p_targets or list(ctx["targets"])
    subject = (" and ".join([", ".join(subject_mods[:-1]), subject_mods[-1]]) + " together"
               if len(subject_mods) > 1 else (subject_mods[0] if subject_mods else "the evaluated mod"))
    # The number counts what needs both evaluated mods (with one evaluated mod, every admitted clash is its own).
    # A clash one mod has with a third mod on its own is shown under its own heading or set aside, never counted
    # here; a clash whose cause could not be separated is stated in one clause with its count. Each kind of clash
    # (a check and its kind) is stated in its own unit and never summed with another (Gate C round 1): objects and
    # record fields are different things. A kind whose rows are fields counts records in the verdict (skill data
    # `verdictKinds`); its field count stays in its item's line.
    vk = words["verdictKinds"]

    def per_kind(gs):
        out = collections.OrderedDict()
        for g in gs:
            e = out.setdefault((g["checkId"], g["kind"]), {"checkId": g["checkId"], "kind": g["kind"],
                                                           "relationships": 0, "_s": set(), "_po": set(),
                                                           "_f": collections.Counter(), "offForGood": 0})
            e["relationships"] += g["relationships"]
            e["offForGood"] += dis_of.get(id(g), 0)
            e["offOwn"] = e.get("offOwn", 0) + dis_own_of.get(id(g), 0)
            e["effectPairs"] = e.get("effectPairs", 0) + int(g.get("effectPairs") or 0)
            e["_s"] |= subjects_of[id(g)]
            e["_po"] |= pair_objects_of[id(g)]
            e["_f"].update(field_names_of[id(g)])
        return [{**{k: v for k, v in e.items() if k not in ("_s", "_po", "_f")}, "objects": len(e["_s"]),
                 "pairObjects": len(e["_po"]), "fieldNames": dict(e["_f"])} for e in out.values()]

    def stated(entries):
        out = []
        for e in entries:
            w = vk.get(f"{e['checkId']}|{e['kind']}") or vk.get(e["checkId"]) or vk["default"]
            u = w.get("unit") or unit
            n = e[u]
            nouns = w.get(u) or vk["default"][u]
            phrase = f"{fmt_n(n)} " + bind(nouns[0] if n == 1 else nouns[1], {"title": title_of(e["checkId"])})
            if e["kind"] == "LOST-EDIT" and u != "relationships" and words.get("verdictFieldsToo"):
                # records in the headline, record fields in the patch tally: both units said (fifth blind review, W-S4)
                phrase += " " + bind(words["verdictFieldsNamed" if e["checkId"] in names_fields and e.get("fieldNames")
                                           else "verdictFieldsToo"], {
                    "count": fmt_n(e["relationships"]),
                    "noun": words["fieldNoun"][0 if e["relationships"] == 1 else 1]
                    + (bind(words["verdictFieldsOff"], {"count": fmt_n(e["offForGood"])})
                       + (bind(words["verdictFieldsOffOwn"], {"count": fmt_n(e["offOwn"])})
                          if e.get("offOwn") and words.get("verdictFieldsOffOwn") else "")
                       if e.get("offForGood") and words.get("verdictFieldsOff") else ""),
                    "fields": fields_text(collections.Counter(e.get("fieldNames") or {}))})
            elif (e["kind"] != "LOST-EDIT" and e.get("pairObjects") and words.get("verdictObjectsToo")
                  and e["checkId"] in set(words.get("verdictObjectsChecks") or [])):
                phrase += " " + bind(words["verdictObjectsEffect"] if e.get("effectPairs") and words.get("verdictObjectsEffect")
                                     else words["verdictObjectsToo"], {"count": fmt_n(e["pairObjects"]),
                                                                       "effect": fmt_n(e.get("effectPairs") or 0)})
            out.append({**e, "unit": u, "count": n, "phrase": phrase})
        return sorted(out, key=lambda e: (-e["count"], e["checkId"], e["kind"]))

    def listed(parts):
        return parts[0] if len(parts) == 1 else "; ".join(parts[:-1]) + "; and " + parts[-1]
    by_attr = {k: [g for g in items if g["attribution"] == k]
               for k in ("needs-both", "single-mod", "not-separable", "with-target")}
    counts = {key: per_kind(by_attr[a])
              for key, a in (("needsBoth", "needs-both"), ("singleMod", "single-mod"), ("notSeparable", "not-separable"))}
    counted = by_attr["needs-both"] if multi else items
    verdict_counts = stated(per_kind(counted))
    # Snazzy review 2: an object a check names as a state of its own (identityByObject: a fixture left hanging, an
    # object left without support) is counted once in the headline, in the first such check that names it; a later
    # one counts only its other objects and says how many it shares
    # (round 2, Snazzy B2) every entry counted before holds its objects: a record entry its records, a pair check both
    # objects of each pair, a by-object check its objects; the shared ones are all of them, by construction
    # (eighth review, Snazzy 14) each object with the entry that counted it first, so a later entry names, per object,
    # where it is counted
    seen_objects = {}
    for i, e in enumerate(verdict_counts):
        rows_of, held_ = collections.Counter(), set()
        for g in counted:
            if (g["checkId"], g["kind"]) == (e["checkId"], e["kind"]):
                rows_of.update(subject_rows_of[id(g)])
                held_ |= pair_objects_of.get(id(g)) or set()
        if e["checkId"] not in by_object:
            for x_ in [str(x) for x in rows_of if isinstance(x, str)] + [str(x) for x in held_]:
                seen_objects.setdefault(x_, title_of(e["checkId"]))
            continue
        subj = set(rows_of)
        shared_objects = subj & set(seen_objects)
        shared_with_ = collections.Counter(seen_objects[o_] for o_ in shared_objects)
        for x_ in subj:
            seen_objects.setdefault(x_, title_of(e["checkId"]))
        if not shared_objects:
            continue
        shared = len(shared_objects)
        # the count left in the entry's own unit: its objects not counted above, or the rows on them
        new = (len(subj - shared_objects) if e["unit"] == "objects"
               else sum(n for s_, n in rows_of.items() if s_ not in shared_objects))
        w = vk.get(f"{e['checkId']}|{e['kind']}") or vk.get(e["checkId"]) or vk["default"]
        nouns = w.get(e["unit"]) or vk["default"][e["unit"]]
        # the check's own total is printed (the number its item shows), with how many are objects already counted
        # (third blind review, S-S2: a smaller number than the bullet read as a contradiction)
        total = e["count"]
        phrase = (f"{fmt_n(total)} " + bind(nouns[0] if total == 1 else nouns[1], {"title": title_of(e["checkId"])})
                  # (Snazzy re-review on regen-3) the shared objects are named, so "of them" is not read as the check's own
                  + " " + bind(words["verdictSharedObjects"], {"count": fmt_n(shared), "noun": "object" if shared == 1 else "objects", "examples": ", ".join(
                      sorted(str(x) for x in shared_objects)[:3]) + (", ..." if shared > 3 else "")}) if new else
                  bind(words["verdictSameObjects"][0 if shared == 1 else 1], {
                      "count": fmt_n(shared), "title": title_of(e["checkId"]),
                      # (eighth review round 2, Snazzy 7) the earlier items that count them, as the kind line names them
                      "titles": (f'"{next(iter(shared_with_))}"' if len(shared_with_) == 1 else and_list(
                          [f'"{t_}" ({fmt_n(n_)})' for t_, n_ in sorted(shared_with_.items(), key=lambda kv: (-kv[1], kv[0]))]))}))
        verdict_counts[i] = {**e, "count": total, "newObjects": new, "sharedObjects": shared, "phrase": phrase,
                             "sharedWith": dict(shared_with_)}
        if not new:
            # every object of this entry is a clash counted above: a tally of clashes counts it there, never again here
            for g in counted:
                if (g["checkId"], g["kind"]) == (e["checkId"], e["kind"]):
                    g["countedElsewhere"] = True
    unseparated = stated(counts["notSeparable"]) if multi else []
    checks_on_p = sorted({c for (c, a) in measured if a == p_of(c)})
    if verdict_counts:
        verdict = bind(words["verdictFound"], {"subject": subject, "kinds": listed([e["phrase"] for e in verdict_counts])})
    else:
        verdict = bind(words["verdictNone"], {"subject": subject})
    if unseparated:
        verdict += " " + bind(words["verdictNotSeparable"], {"kinds": listed([e["phrase"] for e in unseparated])})
    if ground_lost and words.get("verdictNavmeshLoss"):
        # (fourth review, Clockwork S4) the largest measured navmesh effect is said at the top too, not counted
        by_w_ = collections.OrderedDict()
        for l_, w_, n_, st_ in ground_lost:
            x_ = by_w_.setdefault(w_, {"losers": [], "n": 0, "still": 0, "measured": True})
            x_["losers"].append(l_)
            x_["n"] += n_
            x_["still"] += st_ or 0
            x_["measured"] = x_["measured"] and st_ is not None
        verdict += " " + " ".join(bind(words["verdictNavmeshLoss"], {
            "winner": w_, "losers": (and_list([f"{l_}'s" for l_ in x_["losers"]]) if len(x_["losers"]) <= 2
                       else f"{fmt_n(len(x_['losers']))} other mods'"),
            "count": fmt_n(x_["n"]), "noun": "navmesh record" if x_["n"] == 1 else "navmesh records",
            "still": bind(words["verdictNavmeshLossStill"], {"count": fmt_n(x_["still"])})
            if x_["measured"] and words.get("verdictNavmeshLossStill") else ""}) for w_, x_ in by_w_.items())
    missing_facts = []
    if not p_targets:
        missing_facts.append(words["noModInSetup"])
    elif len(p_targets) > 1 and all(v is None for v in s_arms.values()) and not items:
        missing_facts.append(bind(words["attribution"]["not-separable"], {"why": words["notSeparableWhy"]["noSingleSetups"]}))
    def aside_n(adm):
        """(seventh review, Missives S3, Clockwork S2) a set-aside count in its unit: distinct record fields (navmesh
        records for the navmesh check's rows), and the plugin pairs (rows) behind them."""
        es_ = [e for e in record if e["admission"] == adm]
        if not es_:
            return set_aside[adm], set_aside[adm]
        n_ = len({str(e["formKey"]) if adm == "navmeshCheck" else (str(e["formKey"]), e.get("field")) for e in es_})
        return n_, len(es_)

    def aside_noun(adm):
        n_, r_ = aside_n(adm)
        return words["fieldNoun"][0 if n_ == 1 else 1] + (bind(words["setAsidePairs"], {"count": fmt_n(r_)})
                                                          if r_ > n_ and words.get("setAsidePairs") else "")

    aside = {"baseGame": set_aside["baseGame"], "sameMod": set_aside["sameMod"],
             "alreadyPresent": set_aside["alreadyPresent"], "partnerAlone": set_aside["partnerAlone"],
             "history": set_aside["history"],
             "notInvolving": set_aside["notInvolving"], "singleMod": set_aside["singleMod"],
             "repeat": set_aside["repeat"], "noBody": set_aside["noBody"], "pathCase": set_aside["pathCase"],
             "bodyUnread": set_aside["bodyUnread"], "bothDisabled": set_aside["bothDisabled"],
             "offUnreferenced": set_aside["offUnreferenced"], "baseLink": set_aside["baseLink"],
             "offUnreferencedWinner": set_aside["offUnreferencedWinner"],
             "anchorUnclear": set_aside["anchorUnclear"], "notOwnEdit": set_aside["notOwnEdit"],
             "ownLayout": set_aside["ownLayout"],
             "ownMaster": set_aside["ownMaster"], "undoesMaster": set_aside["undoesMaster"],
             "superseded": set_aside["superseded"], "sameClash": set_aside["sameClash"],
             "sameValue": set_aside["sameValue"], "samePair": set_aside["samePair"],
             **{k_: set_aside[k_] for k_ in NOISE_ADMISSIONS}}
    def sibling_rows():
        """(ninth review, Sewers 27) the separate evaluation's findings this report counts or sets aside rows from, each
        with its anchor in this index, its check, pair, file and rows (the items link them)."""
        out_ = collections.OrderedDict()
        s_b_ = (sibling or {}).get("bundle")
        for e in record:
            if e.get("source") != "sibling" or not e.get("siblingFindingId"):
                continue
            x_ = out_.setdefault(e["siblingFindingId"], {
                "anchor": f"sibling-{e['siblingFindingId']}", "id": e["siblingFindingId"], "checkId": e["checkId"],
                "file": (s_b_.output_emit_path(e["checkId"], anchor, "arm") or s_b_.output_emit_path(e["checkId"], anchor, None)
                         if s_b_ is not None else None) or "", "rows": []})
            x_["rows"].append({"kind": e["kind"], "A": e["A"], "B": e["B"], "formKey": e["formKey"],
                               "other": e.get("other") or [], "admission": e["admission"], "summary": e.get("summary")})
        return list(out_.values())

    def base_links_total(mod_):
        """(sixth review, Sewers r5 S3) the evaluated mod's broken links of base-game navmeshes said as one figure: the
        base-game line's and the carried ones, and how many the setup with the patch still has."""
        rows_ = [e for e in record if e["kind"] == "EDGE-IDX" and e["admission"] in ("baseGame", "baseLink")
                 and mod_ in (e["A"], e["B"])]
        other_ = sum(1 for e in rows_ if e["admission"] == "baseGame")
        if not other_ or not words.get("setAsideBaseLinkTotal"):
            return ""
        still_ = (sum(1 for e in rows_ if there(patched_arm, (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"],
                                                               e["summary"])))
                  if patched_arm and (rows_[0]["checkId"], patched_arm) in measured else None)
        return " " + bind(words["setAsideBaseLinkTotal"], {
            "other": fmt_n(other_), "mod": mod_, "total": fmt_n(len(rows_)),
            "patch": "" if still_ is None else bind(words["setAsideBaseLinkTotalPatch"], {
                "patch": patch_text, "still": fmt_n(still_), "total": fmt_n(len(rows_))})})

    def nobody_lights():
        """(sixth review, BPT r6 S4) the lights a support or fixture row sets aside as having no body: a light left where
        the object it stood on or hung from was moved or removed still lights that spot."""
        out_ = collections.OrderedDict()
        for e in record:
            if e["admission"] != "noBody" or e["checkId"] not in set(words.get("supportChecks") or []):
                continue
            for o_ in [e["formKey"], *(e.get("other") or [])]:
                if no_model_type(names, o_) == "Light":
                    out_.setdefault(str(o_), named(o_))
        return out_

    def unread_examples():
        """(fourth review, Sewers S5) the objects whose base record was not read, with the place their row gives."""
        ex_ = []
        for e in record:
            if e["admission"] != "bodyUnread":
                continue
            lab_ = str(((p_rows.get((e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])) or {})
                        .get("row") or {}).get("label") or "")
            for o_ in [e["formKey"], *(e.get("other") or [])]:
                if base_unknown(o_):
                    ex_.append(f"{o_}" + (f" in {lab_}" if lab_ else ""))
        ex_ = list(dict.fromkeys(ex_))
        return (" (" + "; ".join(ex_[:3]) + (f"; and {fmt_n(len(ex_) - 3)} more" if len(ex_) > 3 else "") + ")") if ex_ else ""

    def describe_setup(arm_id):
        """A measured setup in plugins, against the setup without the patch: what it holds, or what it lacks."""
        plugs = all_orders.get(arm_id) or []
        low = {x.lower() for x in plugs}
        kept = [x for x in plugs if not is_base_game(x, words)]
        removed = [x for x in all_orders.get(p_arm) or [] if x.lower() not in low]
        return (words["setupBaseGameAlone"] if not kept else
                bind(words["setupOnly"], {"plugins": " and ".join(kept)}) if len(kept) <= len(removed) else
                bind(words["setupWithout"], {"plugins": ", ".join(removed)}))

    def sibling_source():
        """Which setups the separate evaluation measured and this report uses, in plugins (blind review of the BPT
        report: "each mod on its own" claimed setups that were never measured)."""
        if many_siblings and sib_infos:
            return siblings_source()
        if not (sibling_record and sibling_record["roles"]):
            return None
        # (third review, Clockwork S6) named against the measured load order (the bundle's baseline), which a "measured load
        # order without ..." phrase names: the plugins the setup lacks include any patch the measured order holds
        measured_ = all_orders.get("baseline") or p_plugin_names
        p_low = [x.lower() for x in measured_]
        descs = []
        for a_ in dict.fromkeys(sibling_record["roles"].values()):
            plugs = sib_setup_plugins.get(a_) or []
            low = {x.lower() for x in plugs}
            kept = [x for x in plugs if not is_base_game(x, words)]
            removed = [x for x, xl in zip(measured_, p_low) if xl not in low]
            d_ = (words["setupBaseGameAlone"] if not kept else
                  bind(words["setupOnly"], {"plugins": " and ".join(kept)}) if len(kept) <= len(removed) else
                  bind(words["setupWithout"], {"plugins": ", ".join(removed)}))
            descs.append(d_)
        return bind(words["siblingSetups"], {"setups": "; ".join(descs)})
    def siblings_source():
        """(item 21) several separate evaluations: the setups each lends, named against the measured load order, with
        the folder of the evaluation it came from."""
        measured_ = all_orders.get("baseline") or p_plugin_names
        p_low = [x.lower() for x in measured_]
        parts = []
        for i_ in sib_order:                   # the order roles are taken in, so the order given changes nothing
            descs = []
            for a_ in dict.fromkeys((i_.get("record") or {}).get("roles", {}).values()):
                plugs = [x[0] for x in i_["idents"].get(a_, ((), None, ()))[0]]
                low = {x.lower() for x in plugs}
                kept = [x for x in plugs if not is_base_game(x, words)]
                removed = [x for x, xl in zip(measured_, p_low) if xl not in low]
                descs.append(words["setupBaseGameAlone"] if not kept else
                             bind(words["setupOnly"], {"plugins": " and ".join(kept)}) if len(kept) <= len(removed) else
                             bind(words["setupWithout"], {"plugins": ", ".join(removed)}))
            if descs:
                name_ = Path(str(i_["s"].get("pathAsGiven") or "")).name or str(i_["s"].get("pathAsGiven") or "")
                parts.append(bind(words["siblingSetupsFrom"], {"setups": "; ".join(descs), "name": name_}))
        return bind(words["siblingSetupsMany"], {"setups": "; ".join(parts)}) if parts else None

    # A load-order risk the record lane measured: the setup without the patch with its plugins in another order (same
    # plugins, same bytes), whose record rows hold changes lost between a pair whose order differs, that the measured
    # order does not lose. Said as a measured record fact, one line per pair; no claim of how it shows in game.
    order_lines, order_record = [], []
    geo_lines, geo_fact = order_geometry(ctx, p_arm, words)
    if ctx.get("orderSetups"):
        main_b = ctx.get("mainBundle")
        p_req = next((x for x in (main_b.plan.get("arms") if main_b else None) or [] if x.get("armId") == p_arm), {})
        p_plugs = [(str(x.get("name")), str(x.get("sha256") or "")) for x in (p_req.get("requested") or {}).get("plugins") or []]
        p_pos = {n.lower(): i for i, (n, _) in enumerate(p_plugs)}
        # the measured order loses the same change (the same plugin's change to the same field), whoever wins it
        p_lost = {ident_of.get(k_) for (c_, a_), rs in rows_by.items() if a_ == p_of(c_) for k_ in rs
                  if k_[1] == "LOST-EDIT"}
        for o_ in ctx["orderSetups"]:
            o_plugs = [(str(x.get("name")), str(x.get("sha256") or "")) for x in o_.get("plugins") or []]
            if sorted(o_plugs) != sorted(p_plugs):
                raise InputRefused(f"order-setup-foreign: the order setup {o_.get('armId')} ({o_.get('planPath')}) does "
                                   f"not hold the same plugins with the same bytes as the setup without the patch, so it "
                                   f"was not used.")
            if o_plugs == p_plugs:
                raise InputRefused(f"order-setup-same-order: the order setup {o_.get('armId')} ({o_.get('planPath')}) "
                                   f"loads the plugins in the measured order, so it shows no order risk.")
            o_pos = {n.lower(): i for i, (n, _) in enumerate(o_plugs)}
            per_pair = collections.OrderedDict()
            for r_ in o_.get("rows") or []:
                a_, b_ = str(r_.get("A")), str(r_.get("B"))
                al, bl = a_.lower(), b_.lower()
                if (r_.get("Kind") != "LOST-EDIT" or not ({al, bl} & pt_lower) or al not in p_pos or bl not in p_pos
                        or (p_pos[al] < p_pos[bl]) == (o_pos[al] < o_pos[bl])):
                    continue                   # a pair whose order did not change, or no evaluated mod in it
                for n_ in r_.get("Necessary") or []:
                    v_ = n_.get("Values") or {}
                    if same_but_path_case(v_.get("lost"), v_.get("winner")):
                        continue
                    if identity(str(r_.get("_check")), "LOST-EDIT", a_, b_, {"formKey": n_.get("FormKey"),
                                                                           "field": r_.get("Field")}) in p_lost:
                        continue               # the measured order loses it too
                    # (eighth review, Pilgrim 31) the prediction's no-water rule: a water field on an interior cell
                    # neither plugin's version of which has water, read from both plugin files, is no change in game
                    wn_ = words.get("waterNeedsFlag") or {}
                    if str(r_.get("_type")) == wn_.get("recordType") and str(r_.get("Field")) in (wn_.get("fields") or []):
                        fl_ = [plugin_record(pl_, "cell", str(n_.get("FormKey"))) for pl_ in (a_, b_)]
                        if all(f_ and f_.get("value") is not None and wn_.get("interiorFlag") in (f_.get("flags") or [])
                               and wn_.get("flag") not in (f_.get("flags") or []) for f_ in fl_):
                            continue
                    g_ = per_pair.setdefault((a_, b_), {"n": 0, "records": {}, "fields": collections.Counter(),
                                                        "fieldTypes": collections.defaultdict(collections.Counter)})
                    g_["n"] += 1
                    if r_.get("_type"):
                        g_["fieldTypes"][str(r_.get("Field"))][str(r_.get("_type"))] += 1
                    g_["records"].setdefault(str(n_.get("FormKey")), r_.get("_type"))
                    g_["fields"][str(r_.get("Field"))] += 1
            def ftypes_(g_, f_):
                """(fifth review, Pilgrim S3) the record types of the field's own rows, most rows first"""
                return [t_ for t_, _ in g_["fieldTypes"][f_].most_common()]
            for (a_, b_), g_ in sorted(per_pair.items(), key=lambda kv: -kv[1]["n"]):
                types_ = collections.Counter(t_ for t_ in g_["records"].values() if t_)
                n_rec = len(g_["records"])
                mw_ = words.get("orderMoved") or {}

                def names_(xs):
                    shown = xs if len(xs) <= 4 else xs[:3]
                    t_ = ", ".join(shown[:-1]) + " and " + shown[-1] if len(shown) > 1 else shown[0]
                    return t_ if len(xs) <= 4 else bind(mw_.get("more") or "{shown} and {count} more",
                                                        {"shown": ", ".join(shown), "count": fmt_n(len(xs) - 3)})
                moved_ = (mw_.get("join") or "; ").join(
                    bind(mw_.get(d_) or "{plugin} moved " + d_ + " {plugins}", {"plugin": x_, "plugins": names_(ps_)})
                    for x_, d_, ps_ in moved_plugins([n for n, _ in p_plugs], [n for n, _ in o_plugs], p_targets))
                order_lines.append({"path": "evidence/order-setup-" + str(o_.get("armId")) + ".json",
                                    "text": bind(words["orderRiskLine"], {
                    "loser": a_, "winner": b_, "count": fmt_n(g_["n"]), "moved": moved_,
                    "noun": words["fieldNoun"][1], "verb": "is" if g_["n"] == 1 else "are",
                    "records": f"{fmt_n(n_rec)} {'record' if n_rec == 1 else 'records'}" + (
                        " (" + ", ".join(f"{t_} {fmt_n(c_)}" for t_, c_ in types_.most_common()) + ")" if types_ else ""),
                    "fields": fields_text(g_["fields"]),
                    "inGame": (bind(words["orderRiskInGame"], {"meanings": "; ".join(
                        f"{field_word(f_)}: {m_}" for f_, m_ in ((f_, field_meaning(ftypes_(g_, f_), f_))
                                                                 for f_, _ in collections.Counter(g_["fields"]).most_common())
                        if m_)}) if any(field_meaning(ftypes_(g_, f_), f_) for f_ in g_["fields"])
                        else words["orderRiskInGameMissing"])})})
                order_record.append({"armId": o_.get("armId"), "planPath": o_.get("planPath"),
                                     "rowsPath": o_.get("rowsPath"), "loser": a_, "winner": b_, "fields": g_["n"],
                                     "records": n_rec})
    if sibling_record:
        # Snazzy review 1: the sibling's setup that decided "needs both" for a partner mod is named with the others
        for m_, a_ in partner_arms.items():
            s_, bare_ = sib_split(a_)
            if s_ is not None:
                next(x for x in sib_infos if x["s"] is s_)["record"]["roles"].setdefault(f"without {m_}", bare_)
    def siblings_out():
        """(item 21) one record per sibling, in the order given: how it was joined, the roles it filled, the roles it
        offered that another sibling's identical setup filled, the setups it could not lend and why."""
        out_ = []
        for i_ in sib_infos:
            rec_ = dict(i_.get("record") or {})
            if i_["s"] is sibling:
                rec_["rows"] = sibling_rows()
            offered_ = {}
            for role_, used_ in [*sib_roles.items(), *((f"without {m_}", a_) for m_, a_ in partner_arms.items())]:
                for dup_, canon_ in sib_dup_of.items():
                    d_s, d_a = sib_split(dup_)
                    if canon_ == used_ and d_s is i_["s"]:
                        offered_[role_] = {"armId": d_a, "usedFrom": (sib_split(used_)[0] or {}).get("pathAsGiven")}
            rec_.update({"joinBasis": "shared-setup" if i_["anchor"] is not None else "sub-order",
                         "offered": offered_, "unusableSetups": dict(i_["unusable"])})
            out_.append(rec_)
        return out_

    prediction = {"subject": subject, "mods": subject_mods, "predictionArm": p_arm, "withoutArm": w_arm,
                  "referenceReads": ref_reads,
                  "pluginReads": plugin_reads,
                  "singleArms": s_arms, "verdict": verdict, "status": "found" if items else "none-found",
                  "items": items, "historyLossLines": loss_lines, "aloneLines": [{**v, "groups": [{"from": k, "entries": e} for k, e in v["groups"].items()]}
                                                 for v in alone_lines.values()],
                  "verdictCounts": verdict_counts, "counts": counts, "headlineUnit": unit,
                  "singleModMode": single_mode, "singleModHeading": words.get("singleModHeading"),
                  "missingFacts": list(dict.fromkeys(missing_facts)),
                  "setAside": aside,
                  # only the kinds that occur are named (a list of zeros says nothing and read as a contradiction
                  # beside a clash counted elsewhere, P64 lane / review 17)
                  # (eighth review round 2, Pilgrim 63) the unit names the setup the prediction is made on
                  "setAsideLine": ((bind(words["setAsideLine"], {"setup": (
                      words.get("setAsideSetupMeasured") or "the measured load order") if p_arm == "baseline" else (
                      words.get("setAsideSetupWithout") or "the setup without the patch"), "parts": ", ".join(
                      bind(t_, {"n": fmt_n(aside[k_]), "mods": and_list(list(partner_alone_mods))})
                      for k_, t_ in words["setAsideParts"].items() if aside.get(k_))})
                      if any(aside.get(k_) for k_ in words["setAsideParts"]) else "")
                                   + (" " + bind(words["setAsideNoBody"], aside)
                                                                         if aside["noBody"] else "")
                                   + ((" " + bind(words["setAsideNoBodyLights"], {
                                       "count": fmt_n(len(nobody_lights())),
                                       "noun": "is a light" if len(nobody_lights()) == 1 else "are lights",
                                       "examples": "; ".join(list(nobody_lights().values())[:2])}))
                                      if aside["noBody"] and nobody_lights() and words.get("setAsideNoBodyLights") else "")
                                   + (" " + bind(words["setAsidePathCase"], aside) if aside["pathCase"] else "")
                                   + (" " + bind(words["setAsideBodyUnread"], {**aside, "examples": unread_examples()})
                                      if aside["bodyUnread"] else "")
                                   + (" " + bind(words["setAsideBothDisabled"], aside) if aside["bothDisabled"] else "")
                                   + (" " + bind(words["setAsideOffUnreferenced"], aside) if aside["offUnreferenced"] else "")
                                   + (" " + bind(words["setAsideOffUnreferencedWinner"], {**aside, "winners": " and ".join(unref_winners)})
                                      if aside["offUnreferencedWinner"] else "")
                                   + (" " + bind(words["setAsideBaseLink"], {
                                       "count": fmt_n(aside["baseLink"] - sum(base_link_cut_by.values())),
                                       "carriers": " and ".join(base_link_by),
                                       "noun": "link" if aside["baseLink"] - sum(base_link_cut_by.values()) == 1 else "links",
                                       "verb": "carries" if len(base_link_by) == 1 else "carry"})
                                      if aside["baseLink"] > sum(base_link_cut_by.values()) else "")
                                   + "".join(" " + bind(words["setAsideBaseLinkCut"], {
                                       "count": fmt_n(n_), "mod": m_, "carriers": " and ".join(base_link_by),
                                       "noun": "link" if n_ == 1 else "links",
                                       "verb": "carries" if len(base_link_by) == 1 else "carry"})
                                       + base_links_total(m_)
                                       for m_, n_ in base_link_cut_by.items() if words.get("setAsideBaseLinkCut"))
                                   + (" " + bind(words["setAsideAnchorUnclear"], aside) if aside["anchorUnclear"] else "")
                                   + (" " + bind(words["setAsideOwnLayout"], {
                                       "count": fmt_n(aside["ownLayout"]),
                                       "noun": "fixture" if aside["ownLayout"] == 1 else "fixtures",
                                       "plugins": and_list(list(dict.fromkeys(
                                           own_layout_by[str(e["formKey"])] for e in record if e["admission"] == "ownLayout"))),
                                       "examples": "; ".join(list(dict.fromkeys(
                                           str(e["formKey"]) for e in record if e["admission"] == "ownLayout"))[:2])})
                                      if aside["ownLayout"] and words.get("setAsideOwnLayout") else "")
                                   + (" " + bind(words["setAsideNotOwnEditAllCarried"], {
                                       "count": fmt_n(aside_n("notOwnEdit")[0]), "noun": aside_noun("notOwnEdit"),
                                       "examples": "; ".join(sorted(set(carried_notown))[:2])})
                                      if (aside["notOwnEdit"] and words.get("setAsideNotOwnEditAllCarried")
                                          and not set_aside["notOwnEditEarlierWins"]
                                          and len(set(carried_notown)) == aside_n("notOwnEdit")[0]) else
                                      " " + bind(words["setAsideNotOwnEdit"], {
                                       "count": fmt_n(aside_n("notOwnEdit")[0]),
                                       "noun": aside_noun("notOwnEdit"),
                                       "earlier": (bind(words["setAsideNotOwnEditEarlier"], {
                                           "count": fmt_n(set_aside["notOwnEditEarlierWins"])})
                                           if set_aside["notOwnEditEarlierWins"] else "") + (bind(
                                           words["setAsideNotOwnEditCarried"], {
                                               "count": fmt_n(len(carried_notown)),   # fields, as its sentence counts (fourth review, Clockwork r3 S2)
                                               "examples": "; ".join(sorted(set(carried_notown))[:2])})
                                           if carried_notown else "")}) if aside["notOwnEdit"] else "")
                                   + "".join(" " + bind(words["setAsideNoiseNavmeshDoorsRead"]
                                                        if (k_ == "navmeshCheck" and door_lines_said
                                                            and words.get("setAsideNoiseNavmeshDoorsRead"))
                                                        # (tenth review, Snazzy S4) read, and no door left without one
                                                        else words["setAsideNoiseNavmeshDoorsNone"]
                                                        if (k_ == "navmeshCheck" and door_lines_read
                                                            and words.get("setAsideNoiseNavmeshDoorsNone"))
                                                        else words["setAsideNoise"][k_], {
                                       "count": fmt_n(aside_n(k_)[0]), "noun": aside_noun(k_),
                                       # (tenth review) an example where a sentence names one
                                       "examples": ", ".join(sorted({str(e["formKey"]) for e in record
                                                                     if e["admission"] == k_})[:2])})
                                       for k_ in NOISE_ADMISSIONS if aside[k_] and (words.get("setAsideNoise") or {}).get(k_))
                                   if sum(aside.values()) else None),
                  "noneFoundBasis": None if items else bind(words["noneFoundBasis"], {
                      "checks": len(checks_on_p), "checkNoun": "check" if len(checks_on_p) == 1 else "checks",
                      "subject": subject}),
                  "record": record, "detailAsideWords": words.get("detailAside") or {},
                  "doorLinkEntries": door_entries_,
                  "sibling": ({**sibling_record, "rows": sibling_rows()} if sibling_record else sibling_record),
                  **({"siblings": siblings_out()} if many_siblings and sib_infos else {}),
                  "withoutPartnerArms": dict(partner_arms),
                  "siblingSource": sibling_source(),
                  "orderRiskLines": order_lines, "orderRisks": order_record,
                  # item 10: placed-object geometry measured in both orders of a free pair
                  "orderGeometryLines": geo_lines,
                  # item 18 (4.24 phase 3): whether the order is a choice, and both orders weighed when it is
                  "loadOrder": with_geometry(load_order_fact(ctx, p_arm, patched_arm, list(subject_mods), words), geo_fact)}

    sib_counted = {e["checkId"] for e in record if e.get("source") == "sibling" and e["admission"] == "admitted"}
    # (sixth review, Sewers S3) a check whose rows this report takes from the separate evaluation, counted or not
    sib_used = {e["checkId"] for e in record if e.get("source") == "sibling"} - sib_counted
    # --- coverage: what the checks could not judge, by named reason and count
    reasons = words["coverageReasons"]
    lines = []
    relevant_arms = {p_arm, patched_arm, w_arm, *s_arms.values()} - {None}
    statuses_of, arms_of = collections.OrderedDict(), {}
    for m in ctx["missing"]:
        if m.get("armId") in relevant_arms:
            statuses_of.setdefault(str(m.get("checkId")), [])
            if str(m.get("status")) not in statuses_of[str(m.get("checkId"))]:
                statuses_of[str(m.get("checkId"))].append(str(m.get("status")))
            arms_of.setdefault(str(m.get("checkId")), []).append((m.get("armId"), m.get("part")))
    # one reason per check, by the skill's precedence, and one line per reason: a check with two statuses is listed with
    # the other checks of its reason, never under a second line with the same heading (Hroggar r3 report review 3)
    by_status = collections.OrderedDict()
    for check_id, sts in statuses_of.items():
        by_status.setdefault(ctx["missingReason"](first_status(sts, words.get("missingReasonPrecedence")),
                                                  "has no accepted result"),
                             collections.OrderedDict())[check_id] = arms_of[check_id]
    labels = ctx["armLabels"]
    all_relevant = len(relevant_arms)
    everywhere_ = missing_everywhere(ctx["missing"], labels.keys())
    for reason, by_check in by_status.items():
        subjects = []
        for check_id, arms_ in sorted(by_check.items(), key=lambda kv: title_of(kv[0])):
            # a missing comparison part is the comparison with the measured order, not the setup's own result
            def place(a, part):
                label = labels.get(a)
                # it follows "for" / "of" mid-sentence: lower-cased, with its article
                label = ("the " + label[:1].lower() + label[1:]) if label else "a tested setup"
                if (part not in ("diff", "reverse") and a == p_arm and check_id in sib_counted
                        and words.get("missingFromSibling")):
                    # its result for this setup is taken from the separate evaluation, said here too (P95)
                    label += words["missingFromSibling"]
                elif (part not in ("diff", "reverse") and a == p_arm and check_id in sib_used
                        and words.get("missingFromSiblingUsed")):
                    label += words["missingFromSiblingUsed"]
                return (f"the comparison of {label} with the measured load order" if part in ("diff", "reverse")
                        else label)
            setups = sorted({(a, part) for a, part in arms_ if a}, key=lambda x: (str(x[0]), str(x[1])))
            where = ((words.get("missingEverySetup") or "") if check_id in everywhere_
                     and check_id not in sib_counted and check_id not in sib_used else
                     "" if len({a for a, _ in setups}) >= all_relevant and all(pt not in ("diff", "reverse")
                                                                               for _, pt in setups)
                     and not (check_id in sib_counted and any(a == p_arm for a, _ in setups)) else
                     " (for " + "; ".join(place(a, part) for a, part in setups) + ")")
            subjects.append(title_of(check_id) + where)
        lines.append({"reason": bind(reasons["missing"], {"reason": reason}), "count": len(by_check),
                      "unit": "check" if len(by_check) == 1 else "checks", "subject": "; ".join(subjects)})
    # (2026-10-24, task report-route-cut-leads) A check that ran and reached no verdict may still have rated objects as
    # blocking or likely to block a route (blockprobe --rows). Those are LEADS for an in-game check, never clashes: listed
    # here per setup, each object with its own plugin, its cell and the passage the probe states, and in the evidence
    # index. Nothing else in the report reads them. A check with a verdict, or a bundle with no rows file, prints nothing.
    lw = words.get("routeCutLeads") or {}
    route_leads = []
    arm_order = list(dict.fromkeys(a for a in [p_arm, patched_arm, w_arm, *s_arms.values()] if a))
    b_ = ctx.get("mainBundle")
    for cid_, aid_ in [(c_, a_) for a_ in arm_order for c_ in (lw.get("checks") or [])] if lw and b_ is not None else []:
        # the setup's own run of the check (no comparison part): it ran to the end and its detector reached no verdict
        rc_ = b_.receipt_by_key.get((cid_, aid_, None)) or b_.receipt_by_key.get((cid_, aid_, "arm"))
        res_ = (rc_ or {}).get("result") or {}
        if (not rc_ or not (rc_.get("validation") or {}).get("accepted", True) or res_.get("execution") != "completed"
                or res_.get("detector") is not None):
            continue
        ev_ = next((e for e in b_.evidence if e.get("recordedBy") == rc_["receiptId"]
                    and str(e.get("path", "")).endswith(lw.get("rowsSuffix") or ".route-cut-rows.json")), None)
        doc_ = b_.emit(ev_["path"]) if ev_ else None
        if not isinstance(doc_, dict):
            continue
        rows_ = [r for r in doc_.get("Rows") or [] if isinstance(r, dict) and not r.get("Navcut")
                 and r.get("Verdict") in (lw.get("verdicts") or [])]
        if not rows_:
            continue
        label_ = labels.get(aid_)
        setup_ = ("the " + label_[:1].lower() + label_[1:]) if label_ else "a tested setup"
        targets_ = [str(t) for t in doc_.get("Targets") or []]

        def lead_words(r):
            fk_ = str(r.get("FormKey"))
            owner_ = fk_.split(":", 1)[1] if ":" in fk_ else ""
            n_ = object_name(fk_)
            obj_ = f"{fk_} ({n_})" if n_ else (f"{fk_} ({r['Mesh']})" if r.get("Mesh") else fk_)
            by_ = [str(x) for x in r.get("By") or []]
            if owner_ and owner_.lower() in {x.lower() for x in by_}:
                whose_ = bind(lw["own"], {"plugin": owner_})
            elif by_:
                whose_ = bind(lw["other"], {"owner": owner_, "by": " and ".join(by_)})
            else:
                whose_ = bind(lw["unplaced"], {"owner": owner_})
            p_, cap_ = r.get("Passage"), r.get("PassageCap")
            if r.get("Verdict") == "Blocked":
                pass_ = bind(lw["blocked"], {"passage": fmt_n(p_)})
            else:
                pass_ = bind(lw["atCap"] if (p_ is not None and cap_ is not None and p_ >= cap_) else lw["likely"],
                             {"passage": fmt_n(p_), "share": str(r.get("PassageShare") or "")})
            return {"object": obj_, "text": bind(lw["item"], {"object": obj_, "whose": whose_, "cell": located_place(fk_, str(r.get("Cell"))),
                                                               "passage": pass_})}
        shown_ = [lead_words(r) for r in rows_]
        cap_n = int(lw.get("pageMax") or 3)
        objects_ = "; ".join(x["text"] for x in shown_[:cap_n])
        if len(shown_) > cap_n:
            objects_ += bind(lw["more"], {"count": fmt_n(len(shown_) - cap_n)})
        verdicts_ = {r.get("Verdict") for r in rows_}
        rating_ = lw["rating"]["both" if len(verdicts_) > 1 else ("blocked" if verdicts_ == {"Blocked"} else "likely")]
        one_ = len(rows_) == 1
        line_ = bind(lw["lead"], {"check": title_of(cid_), "setup": setup_, "count": fmt_n(len(rows_)),
                                  "noun": lw["noun"][0 if one_ else 1], "targets": " and ".join(targets_) or lw["targetsUnknown"],
                                  "rating": rating_, "objects": objects_, "them": lw["them"][0 if one_ else 1]})
        lines.append({"reason": lw["reason"], "count": None, "subject": line_, "anchor": lw.get("anchor") or "route-cut-leads"})
        route_leads.append({"checkId": cid_, "check": title_of(cid_), "armId": aid_, "setup": setup_,
                            "path": ev_["path"], "sha256": ev_.get("sha256"),
                            "rows": [{**r, "objectWords": w_["object"]} for r, w_ in zip(rows_, shown_)]})
    # What P's own measurement could not judge, read from P's own receipt (the disposition's list also carries
    # each comparison's receipts, which counted one population two or three times), one clause per population in
    # its own unit, never summed across populations. A reason that names another check is not "could not be
    # judged": it is judged there, and said so on its own line.
    elsewhere_re = re.compile(r"^(.*?):\s*judged by ([\w.-]+), not field-compared here$")

    def own_coverage(check_id):
        arm_id, bundle = p_of(check_id), ctx.get("mainBundle")
        if sib_split(arm_id)[0] is not None:
            s_, arm_id = sib_split(arm_id)
            bundle = s_.get("bundle")
        rc = None
        if bundle is not None:
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
        cov = ((rc or {}).get("result") or {}).get("coverage") if rc else None
        own = [(c.get("Population", c.get("population")), c.get("Unjudgeable", c.get("unjudgeable")))
               for c in cov if isinstance(c, dict)] if isinstance(cov, list) else []
        for c in cov if isinstance(cov, list) else []:
            if isinstance(c, dict) and isinstance(c.get("Judged", c.get("judged")), (int, float)):
                judged_seen[(check_id, c.get("Population", c.get("population")))] = c.get("Judged", c.get("judged"))
        if any((sum(v for v in uj.values() if isinstance(v, (int, float))) if isinstance(uj, dict) else uj or 0) > 0
               for _, uj in own):
            return own
        seen, out = set(), []                  # no receipt here: the disposition's rows for P, each population once
        for u in ctx["unassessed"].get("unjudgeablePopulations") or []:
            if u.get("armId") == p_arm and u.get("checkId") == check_id and u.get("population") not in seen:
                seen.add(u.get("population"))
                out.append((u.get("population"), u.get("unjudgeable")))
                if isinstance(u.get("judged"), (int, float)):
                    judged_seen[(check_id, u.get("population"))] = u.get("judged")
        return out
    judged_seen = {}
    unjudged_checks = list(dict.fromkeys(
        [str(u.get("checkId")) for u in ctx["unassessed"].get("unjudgeablePopulations") or [] if u.get("armId") == p_arm]
        + list(checks_on_p)))
    elsewhere = collections.Counter()

    def emit_unjudged(check_id):
        arm_id, bundle = p_of(check_id), ctx.get("mainBundle")
        if sib_split(arm_id)[0] is not None:
            s_, arm_id = sib_split(arm_id)
            bundle = s_.get("bundle")
        if bundle is None:
            return None
        path = bundle.output_emit_path(check_id, arm_id, "arm") or bundle.output_emit_path(check_id, arm_id, None)
        emit = bundle.emit(path) if path else None
        ul = emit.get("Unjudged") if isinstance(emit, dict) else None
        return ul if isinstance(ul, list) else None

    def emit_coverage_counts(check_id):
        """(item 18 A2) every unjudgeable count the check's rows file states on its own coverage lines, by reason
        (judged-elsewhere reasons excluded: they have their own line)."""
        arm_id, bundle = p_of(check_id), ctx.get("mainBundle")
        if sib_split(arm_id)[0] is not None:
            s_, arm_id = sib_split(arm_id)
            bundle = s_.get("bundle")
        if bundle is None:
            return collections.Counter()
        path = bundle.output_emit_path(check_id, arm_id, "arm") or bundle.output_emit_path(check_id, arm_id, None)
        out = collections.Counter()
        # a record check's rows file only (an import's rows output or a record-rows enrichment); a probe's emit has
        # its populations line
        if not path or not (str(path).endswith("-rows.json") or str(path).startswith("enrichment/record-rows/")):
            return out
        emit = bundle.emit(path)
        for c in (emit.get("Coverage") if isinstance(emit, dict) else None) or []:
            for k, v in ((c.get("Unjudgeable") or {}).items() if isinstance(c, dict) else []):
                if isinstance(v, (int, float)) and not isinstance(v, bool) and v > 0 and not elsewhere_re.match(str(k)):
                    out[str(k)] += int(v)
        return out
    for check_id in unjudged_checks:
        # a check whose rows file lists its unjudged fields gets the fields line below, which says more; its
        # populations line would count the same fields twice (judged-elsewhere reasons are still read here)
        fields_line = (any(isinstance(u, dict) and u.get("Field") for u in emit_unjudged(check_id) or [])
                       or bool(emit_coverage_counts(check_id)))
        pops = []
        for population, uj in own_coverage(check_id):
            per = collections.Counter()
            for k, v in (uj.items() if isinstance(uj, dict) else [("unjudgeable", uj)]):
                if not (isinstance(v, (int, float)) and not isinstance(v, bool) and v > 0):
                    continue
                m_ = elsewhere_re.match(str(k))
                if m_:
                    elsewhere[(m_.group(1).strip(), title_of(m_.group(2)))] += v
                    continue
                per[reason_words(k, check_id)] += v
            if per:
                pops.append((population, per))
        if not pops or fields_line:
            continue

        def detail_of(per):
            top = per.most_common()
            shown = top[:3]
            text = "; ".join(r if len(top) == 1 else f"{r} {fmt_n(n)}" for r, n in shown)
            if len(top) > len(shown):
                text += "; " + bind(reasons["unjudgeableMore"], {"count": len(top) - len(shown)})
            return text
        if len(pops) == 1:
            total_n = sum(pops[0][1].values())
            # the count beside what the check did judge, so the reader can weigh it
            judged_ = judged_seen.get((check_id, pops[0][0]))
            share_ = (bind(reasons["unjudgeableShare"], {"judged": fmt_n(int(judged_))})
                      if judged_ is not None and reasons.get("unjudgeableShare") else "")
            lines.append({"reason": reasons["unjudgeable"], "count": total_n,
                          "unit": "item" if total_n == 1 else "items",
                          "subject": f"{title_of(check_id)}: {detail_of(pops[0][1])}{share_}"})
        else:
            lines.append({"reason": reasons["unjudgeable"], "count": None,
                          "subject": f"{title_of(check_id)}: " + "; ".join(
                              f"{fmt_n(sum(per.values()))} {plain_population(population, words) if population else 'items'} ({detail_of(per)})"
                              for population, per in pops)})
    if elsewhere:
        lines.append({"reason": reasons["judgedElsewhere"], "count": sum(elsewhere.values()),
                      "unit": "item" if sum(elsewhere.values()) == 1 else "items",
                      "subject": "; ".join(bind(reasons["judgedElsewhereItem"], {"what": what, "check": t, "count": fmt_n(n)})
                                           for (what, t), n in elsewhere.items())})
    # The fields a check left unjudged on P, as its rows file lists them (a record-rows enrichment's `Unjudged`):
    # counted with why, and how many lie on records both evaluated mods change, where a clash between them could
    # hide (Gate C round 1, M1).
    for check_id in checks_on_p:
        ul = [u for u in emit_unjudged(check_id) or [] if isinstance(u, dict)]
        fields = [u for u in ul if u.get("Field") and not elsewhere_re.match(str(u.get("Reason") or ""))]
        # (item 18 A2) a count the rows file states on its coverage line beyond the fields it lists is counted too,
        # under its reason, so no unjudged field leaves the report silently
        listed = collections.Counter(str(u.get("Reason") or "") for u in ul)
        beyond = {r_: n_ - listed.get(r_, 0) for r_, n_ in emit_coverage_counts(check_id).items()
                  if n_ > listed.get(r_, 0)}
        if not fields and not [u for u in ul if not u.get("Field")] and not beyond:
            continue
        fw_ = words.get("fieldUnjudgedWords") or {}
        per = collections.Counter(fw_.get(str(u.get("Reason"))) or reason_words(u.get("Reason")) for u in fields)
        for r_, n_ in beyond.items():
            per[fw_.get(r_) or reason_words(r_)] += n_
        parts = [f"{r} {fmt_n(n)}" for r, n in per.most_common()]
        unreadable_records = [u for u in ul if not u.get("Field")]
        if unreadable_records:
            parts.append(bind(words["recordsUnreadable"], {"count": fmt_n(len(unreadable_records))}))
        if multi:
            both = sum(1 for u in fields if pt_lower <= {str(x).lower() for x in u.get("Plugins") or []})
            parts.append(bind(words["fieldsUnjudgedBoth"], {"count": fmt_n(both)}))
            if beyond and words.get("fieldsUnjudgedUnlisted"):
                # (item 18 A2) fields counted without their records: whether they lie on such records is not known
                parts.append(bind(words["fieldsUnjudgedUnlisted"], {"count": fmt_n(sum(beyond.values()))}))
        n_fields = len(fields) + sum(beyond.values())
        lines.append({"reason": reasons["fieldsUnjudged"], "count": n_fields,
                      "unit": "field" if n_fields == 1 else "fields",
                      "subject": f"{title_of(check_id)}: " + "; ".join(parts)})
    not_sep = sum(g["count"] for g in items if g["attribution"] == "not-separable")
    if not_sep:
        lines.append({"reason": reasons["notSeparable"], "count": not_sep,
                      "subject": "; ".join(sorted({title_of(g["checkId"]) for g in items
                                                   if g["attribution"] == "not-separable"}))})
    # a check that reported no count is not a result "given as a count": a 0 is never printed beside its title
    co = {c: n for (c, a), n in count_only.items() if a == p_arm and c not in asset_named and n}
    if co:
        lines.append({"reason": reasons["countOnly"], "count": sum(co.values()),
                      "subject": "; ".join(sorted(title_of(c) for c in co))})
    un = ctx["unassessed"]
    states_p = [x for x in un.get("statesNotExplored") or []
                if not isinstance(x, dict) or x.get("armId") in (None, p_arm, p_of(x.get("checkId")))]
    if states_p:
        state_words = words.get("stateClassWords") or {}

        def state_clause(x):
            if not isinstance(x, dict):
                return str(x)
            what = state_words.get(str(x.get("code") or x.get("subject"))) or str(x.get("reason") or x.get("subject"))
            n_ = x.get("count")
            return f"{what} ({fmt_n(n_)})" if isinstance(n_, int) else f"{what} ({words['stateExtentUnknown']})"
        lines.append({"reason": reasons["statesNotExplored"], "count": len(states_p),
                      "subject": "; ".join(dict.fromkeys(state_clause(x) for x in states_p))})
    if un.get("scopeUnmeasured"):
        # the controller's extent ids in the skill's words; an id it does not name gets one generic phrase (the ids
        # stay in the evidence index)
        extent_words = words.get("extentWords") or {}
        areas = list(dict.fromkeys(extent_words.get(str(x.get("extent"))) or words["extentUnnamed"]
                                   for x in un["scopeUnmeasured"] if isinstance(x, dict)))
        lines.append({"reason": reasons["scopeUnmeasured"], "count": len(un["scopeUnmeasured"]),
                      "subject": "; ".join(areas)})
    if masters is None:
        # no emit recorded the plugins' master lists: a mod building on its master cannot be told apart (P63)
        pair_rows = [e for e in record if e["admission"] == "admitted"]
        if pair_rows:
            lines.append({"reason": reasons["mastersNotRead"], "count": len(pair_rows),
                          "subject": "; ".join(sorted({title_of(e["checkId"]) for e in pair_rows}))})
    if not bases_read:
        # a row measured as crossing mesh triangles has bodies on both sides, whatever names were read (Clockwork S6)
        mesh_re = re.compile(words["meshCrossingPattern"]) if words.get("meshCrossingPattern") else None
        body_rows = [e for e in record if e["admission"] == "admitted" and e["checkId"] in body_checks
                     and not (mesh_re and mesh_re.search(str(e.get("summary") or "")))]
        if body_rows:
            lines.append({"reason": reasons["bodyNotRead"], "count": len(body_rows),
                          "subject": "; ".join(sorted({title_of(e["checkId"]) for e in body_rows}))})
    cannot_run = [c for c in ctx.get("cannotRunChecks") or [] if c not in statuses_of and (c, p_of(c)) not in measured]
    if cannot_run:
        lines.append({"reason": reasons["checksCannotRun"], "count": len(cannot_run),
                      "subject": "; ".join(sorted(title_of(c) for c in cannot_run))})
    # (eighth review, standing nudge set-aside) how a nudged partner's pair is counted, said once, whatever is set aside
    if words.get("nudgeCountedNote") and any(i_.get("nudged") for i_ in items):
        prediction["setAsideLine"] = (prediction["setAsideLine"] or "") + " " + words["nudgeCountedNote"]
    prediction["setAsideLine"] = (prediction["setAsideLine"] or "").strip() or None
    coverage = {"lines": lines, "notes": list(ctx["limitations"]), "setAsideLine": prediction["setAsideLine"],
                "checksOnPrediction": len(checks_on_p), "relevantArms": sorted(relevant_arms),
                "routeCutLeads": route_leads}

    # --- validation against the patch
    if patched_arm is None:
        validation = {"status": "no-patch", "headline": ctx["oldVerdict"], "patches": [],
                      "sentence": words["validationNone"],
                      "other": bind(words["validationOtherSetups"], {"headline": ctx["oldVerdict"]}),
                      "hits": None, "open": None, "falseAlarms": None, "patchProblems": None, "blindSpots": [],
                      "byItem": [], "missingFacts": []}
    else:
        # several patches are named once (the scope line and the setups list name them all) and called "the N
        # patches" in each sentence after (P53: six names repeated in every line took a page)
        patch = (", ".join(patch_labels.get(str(n).lower(), str(n)) for n in patch_names) if len(patch_names) <= 2
                 else bind(words["patchesShort"], {"count": len(patch_names)}))
        admitted = [e for e in record if e["admission"] == "admitted"]
        keyof = lambda e: (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])  # noqa: E731
        not_measured = [e for e in admitted if (e["checkId"], patched_arm) not in measured]
        judged = [e for e in admitted if (e["checkId"], patched_arm) in measured]
        patched_rows = {k: v for (c, a), rs in rows_by.items() if a == patched_arm for k, v in rs.items()}

        def reads_same_patched(rk, h_):
            """(round 3, BPT re-review B1) The one rule by which two values of a lost edit read the same (the prediction's
            sameAsRead: the import's chain reads them equal, path case, unused bytes and rounding folded; or they differ
            only in a path's case), asked of a row of the setup with the patch."""
            v_ = h_["row"].get("values") or {}
            return same_but_path_case(v_.get("lost"), v_.get("winner")) or same_in_chain(patched_arm, rk)
        # the patched setup's lost edits a declared patch wins whose values show no change in game, by the same rules
        # as the prediction's (coordinator D2): the detail's patch-wins line does not count them
        # (BPT re-review S1) lost edits of the setup with the patch whose two values the record chain reads as the same file
        # but for letter case: no change in game, said so on the detail lines (the row's own winner value may be a
        # placeholder, so the chain decides)
        case_only_rows = sorted([str(rk[0]), str(rk[4]), str(h_["row"].get("field")), str(rk[2]), str(rk[3])]
                                for rk, h_ in patched_rows.items() if rk[1] == "LOST-EDIT" and reads_same_patched(rk, h_))
        # (eighth review, Clockwork 135 / 139) lost edits a declared patch wins whose losing value is no change of the
        # losing plugin's own (it only carries an earlier value): nothing of its own is lost; the measured-order line
        # says them apart (the prediction's own-change rule)
        def carried_loser_(rk):
            """The losing value is no change of the loser's own (the value it inherits), or its one owner is an earlier
            plugin (eighth review round 2, Clockwork 139: an absent value the base game holds from the start)."""
            oe_ = own_edit(patched_arm, rk, rk[2])
            if oe_ is not None and oe_["changed"] is False:
                return True
            got_ = field_chain(patched_arm, rk, rk[2])
            if got_ is None:
                return False
            low_ = [str(x).lower() for x in got_[0]]
            i_ = low_.index(str(rk[2]).lower())
            return owner_of(got_[0], got_[1], i_)[0] != i_
        patch_carried = sorted([str(rk[0]), str(rk[4]), str(h_["row"].get("field")), str(rk[2]), str(rk[3])]
                               for rk, h_ in patched_rows.items()
                               if rk[1] == "LOST-EDIT" and str(rk[3]).lower() in {str(x).lower() for x in patch_names}
                               and str(rk[2]).lower() not in {str(x).lower() for x in patch_names}
                               and carried_loser_(rk))
        patch_noise = sorted([str(rk[0]), str(rk[4]), str(h_["row"].get("field")), str(rk[2]), str(rk[3])]
                             for rk, h_ in patched_rows.items()
                             if rk[1] == "LOST-EDIT" and str(rk[3]).lower() in {str(x).lower() for x in patch_names}
                             and lost_edit_noise(rk, patched_rows, patched_arm))
        patch_lower = {str(x).lower() for x in patch_names}
        # A lost edit (record lane) is not judged by presence: with the patch loaded last the same plugin's change can
        # still be lost, to the patch. Which plugins' values lose to the patch says what the patch carries, the same
        # way whether or not the values are recorded (round 4): only the winning side's value loses (the patch carries
        # the lost change: resolved); both lose (the patch's own value: the change is still lost); only the losing
        # side's loses (the patch carries the value that wins without it: kept, with no claim about why). No change
        # lost with the patch is resolved; a winner that is not a patch is left (nothing touched it). Where neither
        # side loses to the patch, the recorded values decide, and with a placeholder value it cannot be told.
        # (round 3, BPT re-review B1) a plugin whose value reads the same as the patch's by that one rule loses nothing to
        # the patch: rounding in the last float digit is no value of the patch's own
        lost_w = collections.defaultdict(list)
        for rk, hit in patched_rows.items():
            if rk[1] == "LOST-EDIT" and not reads_same_patched(rk, hit):
                lost_w[(rk[0], rk[4], hit["row"].get("field"))].append(hit["row"] | {"_rk": rk, "winnerPlugin": rk[3],
                                                                                    "loserPlugin": rk[2]})

        unrecorded = {str(x) for x in words.get("unrecordedValues") or []}

        def recorded(v):
            if v is None or v == "" or v == []:
                return False
            vs = v if isinstance(v, list) else [v]
            return not any(str(x) in unrecorded for x in vs)

        def no_change_of_winner(e):
            """The winning mod made no change of its own to the field without the patch (whose edit: own_edit)."""
            oe = own_edit(p_of(e["checkId"]), keyof(e), e["B"])
            return oe is not None and not oe["changed"]

        def base_put_back(e):
            """(round 3, BPT 15 / 71 / 85 / 88) the patch's winning value is the base game's own value (its one owner, by
            the read, is the record's base-game version): the patch puts the base game's value back."""
            rows_w = lost_w.get((e["checkId"], e["formKey"], e.get("field")))
            if not rows_w:
                return False
            rk_ = rows_w[0]["_rk"]
            got_ = field_chain(patched_arm, rk_, rk_[3])
            if got_ is None or not got_[0] or not is_base_game(got_[0][0], words):
                return False
            return owner_of(got_[0], got_[1], len(got_[0]) - 1)[0] == 0

        def lost_outcome(e):
            rows_w = lost_w.get((e["checkId"], e["formKey"], e.get("field")))
            if not rows_w:
                return "resolved"              # no plugin's change to the field is lost with the patch
            w = rows_w[0]
            if str(w["winnerPlugin"]).lower() not in patch_lower:
                return "left"
            losers = {str(r["loserPlugin"]).lower() for r in rows_w if str(r["winnerPlugin"]).lower() in patch_lower}
            a_lost, b_lost = str(e["A"]).lower() in losers, str(e["B"]).lower() in losers
            if a_lost and b_lost:
                return "own"
            if b_lost or a_lost:
                # the patch keeps one side's value and drops the other's change (fifth blind review, W-S2): symmetric.
                # Where the winning mod's value is the one the losing mod changed (no change of its own), the patch
                # restores the losing mod's change and drops nothing of the winning mod's (Pilgrim's second review, B1)
                if b_lost:
                    return "resolved" if no_change_of_winner(e) else "switched"
                return "kept"
            with_patch, without = (w.get("values") or {}).get("winner"), (e.get("values") or {}).get("winner")
            lost_v = (e.get("values") or {}).get("lost")
            if not (recorded(with_patch) and recorded(without) and recorded(lost_v)):
                # neither side's value loses to the patch: the losing mod's change, lost without it, is no longer lost,
                # so the patch carries that value, and the winning mod had no change of its own there (Clockwork P101)
                return "resolved"
            # (round 3b, review finding 1) the patch's value against each side's by the one read-same rule: the chain
            # of the setup with the patch where it holds both, else the recorded values with path case folded
            def same_as_patch(side, v_side):
                c_ = chain_same(patched_arm, w["_rk"], side, w["_rk"][3])
                return c_ if c_ is not None else path_folded(with_patch) == path_folded(v_side)
            return ("kept" if same_as_patch(e["B"], without) else
                    "resolved" if same_as_patch(e["A"], lost_v) else "own")
        # A geometry row is judged by its own predicate re-measured with the patch, where the probe carries one (an
        # emit's `Predicates`: every row of the setup it was measured against, measured again in this setup): Gone is
        # resolved, Holds is left (whoever owns the objects now), anything else cannot be told. Presence of the row
        # (by its identity) is the rule only for a check whose emits carry no predicates.
        pred_cache = {}

        def predicates(check_id):
            if check_id not in pred_cache:
                rm_ = remeasure_with_patch(check_id)
                pred_cache[check_id] = None if rm_ is None else {k_: v_[0] for k_, v_ in rm_.items()}
            return pred_cache[check_id]

        diff_arm = patched_arm if p_arm == "baseline" else (p_arm if patched_arm == "baseline" else None)

        def geometry_outcome(e):
            # (eighth review, Snazzy 13 / 75) a comparison that refused its verdict still re-measured each row: the row's
            # own predicate decides (Gone or Holds is a measurement; anything else cannot be told); only a refused
            # comparison with no re-measure leaves its rows untold
            refused_ = ((ctx.get("comparisonState") or {}).get((e["checkId"], diff_arm)) or {}).get("refused")
            preds = predicates(e["checkId"])
            if refused_ and preds is None:
                return "cannot-tell"           # the comparison with and without the patch refused a verdict
            if preds is None:
                if there(patched_arm, keyof(e)):
                    return "left"
                # with one evaluated mod, a clash from it alone is credited to the patch only on the patched setup's
                # own re-measure (Gone), never from its absence from a list (coordinator, D3)
                return "cannot-tell" if not multi and e.get("attribution") == "single-mod" else "resolved"
            other = e.get("other") or []
            # an object in several world states is judged in each: still there in any is left (P72)
            outs = [{"Gone": "resolved", "Holds": "left"}.get(preds.get((k_, a_, b_, fk_, o_)), "cannot-tell")
                    for k_, a_, b_, fk_, o_ in ([(x[0], x[1], x[2], e["formKey"], x[3]) for x in e["rows"]]
                                                if e.get("rows") else
                                                [(e["kind"], e["A"], e["B"], e["formKey"], other[0] if other else None)])]
            return "left" if "left" in outs else "cannot-tell" if "cannot-tell" in outs else "resolved"

        outcome = {id(e): lost_outcome(e) if e["kind"] == "LOST-EDIT" else geometry_outcome(e) for e in judged}

        def left_kind(e):
            """Why a geometry clash left by its own predicate (the objects still intersect with the patch) is, or is no
            longer, reported between two mods (W-B2): still listed in the setup with the patch; not listed, and a
            declared patch's version of one of the two objects loads last among the plugins the names enrichment read
            (the record data says who holds it); or not listed for a reason the record data does not give."""
            if present(patched_arm, keyof(e)):
                return "listed"
            for fk in [e["formKey"]] + list(e.get("other") or []):
                carriers = (name_refs.get(str(fk)) or {}).get("carriedBy") or []
                if carriers and str(carriers[-1]).lower() in patch_lower:
                    return "patchHeld"
            return "notAttributed"
        left_split = {id(e): left_kind(e) for e in judged if outcome[id(e)] == "left" and e["kind"] != "LOST-EDIT"}
        # objects whose placement a declared patch's value sets in the setup with the patch (its record rows): a clash
        # still there between them crosses at the patch's position (Clockwork review S5)
        patch_placed = {str(k_[4]) for k_, h_ in patched_rows.items()
                        if k_[1] == "LOST-EDIT" and k_[3].lower() in patch_lower and h_["row"].get("field") == "Placement"}

        def chain_patch_places(fk):
            """A declared patch's version of the object loads last in a record import's chain of the setup with the
            patch and moves it from the version before it."""
            for (c_, a_) in [k for k in needed_fks if k[1] == patched_arm]:
                ch_ = (chains_of(c_, a_) or {}).get(str(fk))
                if ch_ and str(ch_["providers"][-1]).lower() in patch_lower and len(ch_["providers"]) > 1:
                    v_ = (ch_.get("values") or {}).get("Placement")
                    if v_ and v_[-1] != v_[-2]:
                        return True
            return False

        def patch_places(e):
            return (left_split.get(id(e)) == "patchHeld"
                    or any(str(x) in patch_placed or chain_patch_places(x) for x in [e["formKey"], *(e.get("other") or [])]))
        # (Clockwork review 1) one reading of who places the objects: where the record chain shows a patch placing one of
        # them, every line says so, never "not attributed" beside a still-there line that attributes them
        for e in judged:
            if left_split.get(id(e)) == "notAttributed" and patch_places(e):
                left_split[id(e)] = "patchHeld"
        # (third review, Clockwork S1) a clash still there that the setup with the patch does not list between two mods,
        # where a declared patch places one of the objects, is the patch's own placement: not left, said apart
        for e in judged:
            if outcome[id(e)] == "left" and left_split.get(id(e)) == "patchHeld":
                outcome[id(e)] = "patchPlaced"
        # (sixth review, Winterhold r6 S1) an object left without its support, whose supports are all still off for good
        # in the setup with the patch and which no patch moves or switches off, still has no support there: left,
        # whatever the setup's own list says (a patch carrying the removal is no placement). Never over a re-measure
        # that found the object supported (Gone: gate A condition R1); only where it found none (Holds) or did not
        # measure. Read from the plugin files of both setups (recorded bytes only); every judgement is in pluginReads.
        support_checks_ = set(words.get("supportChecks") or [])
        main_b_ = ctx.get("mainBundle")
        arm_rows_ = {a_.get("armId"): (a_.get("requested") or {}).get("plugins") or []
                     for a_ in ((main_b_.plan.get("arms") if main_b_ else None) or [])}
        for e in judged:
            if (e["checkId"] not in support_checks_ or e["kind"] == "LOST-EDIT" or e.get("rows")
                    or outcome[id(e)] not in ("resolved", "patchPlaced", "cannot-tell")):
                continue
            o_ = (e.get("other") or [None])[0]
            if ((predicates(e["checkId"]) or {}).get((e["kind"], e["A"], e["B"], e["formKey"], o_))) == "Gone":
                continue
            st_ = support_still_removed(arm_rows_.get(patched_arm), arm_rows_.get(p_arm), str(e["formKey"]),
                                        [str(x) for x in e.get("other") or []], words)
            plugin_reads.append({"read": "supportState", "record": str(e["formKey"]), "supports": e.get("other"),
                                 "setup": patched_arm, "stillRemoved": st_})
            if st_:
                outcome[id(e)] = "left"
                left_split[id(e)] = "supportRemoved"
        hits = [e for e in judged if outcome[id(e)] == "resolved"]
        switched = [e for e in judged if outcome[id(e)] == "switched"]
        own = [e for e in judged if outcome[id(e)] == "own"]
        kept = [e for e in judged if outcome[id(e)] == "kept"]
        open_ = [e for e in judged if outcome[id(e)] == "left"]
        placed = [e for e in judged if outcome[id(e)] == "patchPlaced"]
        untold = [e for e in judged if outcome[id(e)] == "cannot-tell"]
        untold_value = [e for e in judged if outcome[id(e)] == "cannot-tell-value"]
        # by item: every judged clash; the totals: only the clashes the prediction counts. With two evaluated mods, a
        # clash already there with one mod alone is not credited to the patch (third blind review): it has its own
        # line. With one evaluated mod, a clash that comes from it alone is counted in the headline, so the patch is
        # credited with it too (fourth blind review, S1): the totals add up to the headline.
        all_ = {"hits": hits, "switched": switched, "own": own, "kept": kept, "open": open_, "placed": placed,
                "untold": untold + untold_value}

        def credit(xs):
            return [e for e in xs if not multi or e.get("attribution") != "single-mod"]
        hits, switched, own, kept, open_, placed, untold, untold_value = (credit(x) for x in (
            hits, switched, own, kept, open_, placed, untold, untold_value))
        for e in admitted:
            e["withPatch"] = outcome.get(id(e), "not-measured")
        # (BPT review 1) a lost change still there with the patch, on an object the winning version in the setup with
        # the patch sets off for good: it has no effect in game while the object is off. Read from the record import's
        # chain of that setup (the one off-for-good rule); every count of these sits next to the count it is part of.
        # Nothing is said about what forwarding would do: the winner may switch the object off by other fields too, and
        # a patch may remove it on purpose (blind reviews of regen-2)
        off_cache = {}

        def off_why(ch_, i_):
            """How provider i's version sets the object off for good: "position" (below the world), "parent" (enabled
            opposite to the player), or None; provider_disables' rule, with its reason."""
            f_ = provider_fields_all(ch_, i_)
            if disables("; ".join(f"{k_}={v_}" for k_, v_ in f_.items() if k_.endswith("Position"))):
                return "position"
            return "parent" if provider_disables(ch_, i_) else None

        def still_off(e):
            """None, or the reason the winning version with the patch sets the row's object off for good."""
            if id(e) not in off_cache:
                r_ = None
                if e["kind"] == "LOST-EDIT" and outcome.get(id(e)) in ("left", "kept"):
                    ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
                    if ch_ and ch_.get("providers"):
                        r_ = off_why(ch_, len(ch_["providers"]) - 1)
                    elif outcome.get(id(e)) == "left" and side_disables(keyof(e), e["B"]):
                        r_ = "unknown"
                off_cache[id(e)] = r_
            return off_cache[id(e)]

        OFF_STATES = ("onOwn", "onPatch", "on", "parent", "starts", "off", "unknown", "keptOn", "keptOnPlaced", "keptBase", "keptPlace")

        def kept_meaning(e):
            """(fifth review, Snazzy S2, Winterhold R2-2) what a kept lost change on an object the version with the patch
            does not set off for good means in game: the losing value switches the object off and the last version has
            it on ("keptOn"; "keptOnPlaced" where that version stands across at least the object's size from the
            winner's), or the lost field is the object's base ("keptBase"); None otherwise."""
            ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
            if e["kind"] != "LOST-EDIT" or not ch_ or not ch_.get("providers"):
                return None
            last_ = len(ch_["providers"]) - 1
            lost_k_ = (e.get("values") or {}).get("lost")
            lost_kt_ = " ".join(map(str, lost_k_)) if isinstance(lost_k_, list) else str(lost_k_ or "")
            num_k_ = re.fullmatch(r"\s*(?:\w*MajorRecordFlags=)?(\d+)\s*", lost_kt_)
            # a switch-off by name, or as bit 2048 of a record-flags number (as the prediction's switch-off rows read it)
            if ((off_at_load(lost_k_) or bool(num_k_ and int(num_k_.group(1)) & 0x800))
                    and version_state(ch_, last_) == "on"):
                low_ = [str(x).lower() for x in ch_["providers"]]
                if str(e["B"]).lower() in low_:
                    pw_, pl_ = (_vec(next((v_ for k_, v_ in recordrows.provider_fields(ch_, sup_place, i_).items()
                                           if k_.endswith("Position")), None))
                                for i_ in (low_.index(str(e["B"]).lower()), last_))
                    if pw_ and pl_ and math.dist(pw_[:2], pl_[:2]) >= spot_size(e["formKey"]):
                        return "keptOnPlaced"
                return "keptOn"
            if str(e.get("field") or "") == (lm_words.get("baseField") or "Base"):
                return "keptBase"
            # (seventh review, BPT S3) every kept row has its meaning: a kept position is the winning mod's spot
            if str(e.get("field") or "") == sup_place:
                return "keptPlace"
            return None

        def own_removal(e):
            """(fifth review, BPT S2) The pair winner's version in the setup with the patch sets the object off for good
            by its own change: its version is off for good and the version it inherits from its masters is not."""
            ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
            if not ch_:
                return False
            low_ = [str(x).lower() for x in ch_["providers"]]
            if str(e["B"]).lower() not in low_:
                return False
            ib_ = low_.index(str(e["B"]).lower())
            # (seventh review) the attribution read over each version's off-for-good state
            a_ = recordrows.attribute(ch_["providers"], [provider_disables(ch_, k_) for k_ in range(len(low_))], ib_,
                                      masters)
            return a_["inherits"] is not None and provider_disables(ch_, ib_) and a_["own"]

        def removal_by_patch(e):
            """(seventh review, Snazzy S1) The last version with the patch sets the object off for good by a declared
            patch's own change (the attribution read over each version's off-for-good state)."""
            ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
            if not ch_ or not ch_.get("providers"):
                return False
            n_ = len(ch_["providers"])
            flags_ = [provider_disables(ch_, k_) for k_ in range(n_)]
            if not flags_[-1]:
                return False
            o_ = recordrows.attribute(ch_["providers"], flags_, n_ - 1, masters)["origin"]
            return str(ch_["providers"][o_]).lower() in patch_lower and o_ > 0

        def off_state(e):
            """(fourth review, Winterhold S2) None, or the losing plugin's own version's state (version_state) in the
            setup with the patch, for a row still_off covers: 'no effect in game' only where it is off for good too.
            (fifth review, BPT S2) a losing version that has the object on, where the winner removes it by its own
            change, is "onOwn": the winner's own removal, not an earlier mod's it carries."""
            if not still_off(e):
                return kept_meaning(e) if outcome.get(id(e)) == "kept" else None
            st_ = side_state(keyof(e), e["A"], patched_arm) or "unknown"
            if st_ == "on" and own_removal(e):
                return "onOwn"
            return "onPatch" if st_ == "on" and removal_by_patch(e) else st_

        def off_counts(es_):
            return collections.Counter(off_state(e) for e in es_ if off_state(e))

        def off_text(c_, wrap=True, objs=None, total=None):
            """The off-for-good note: a Counter by the losing version's state (an int is an unknown state). objs: the
            objects per state, said with the record fields where they differ (seventh review, Winterhold S2)."""
            if isinstance(c_, int):
                c_ = collections.Counter({"unknown": c_}) if c_ else collections.Counter()
            notes_ = words.get("offGoodNotes") or {}

            def cnt_(s_):
                o_ = (objs or {}).get(s_)
                # (page budget) the objects are said where the note says they are drawn
                if not o_ or o_ >= c_[s_] or not words.get("offGoodObjects") or s_ != "keptOn":
                    return fmt_n(c_[s_])
                return bind(words["offGoodObjects"], {"objects": fmt_n(o_), "count": fmt_n(c_[s_]),
                                                      "noun": words["fieldNoun"][0 if c_[s_] == 1 else 1],
                                                      "objNoun": "object" if o_ == 1 else "objects"})
            parts_ = [bind(notes_.get(s_) or words["offGoodNote"], {"count": cnt_(s_)}) for s_ in OFF_STATES
                      if (c_ or {}).get(s_)]
            if not parts_:
                return ""
            # a breakdown whose parts fall short of the count it breaks down says the rest, so the parts add up
            rest_ = (int(total) - sum(int(c_.get(s_) or 0) for s_ in OFF_STATES)) if total is not None else 0
            if rest_ > 0 and notes_.get("rest"):
                parts_.append(bind(notes_["rest"], {"count": fmt_n(rest_)}))
            return (" ({})" if wrap else "{}").format("; ".join(parts_))

        def off_note(es_):
            """The off-for-good note of a summary or sentence line: the kept rows' meanings (keptOn, keptOnPlaced,
            keptBase) are said once, in the with-patch kind and pair lines (page budget, fifth review)."""
            return off_text(collections.Counter({k_: v_ for k_, v_ in off_counts(es_).items() if not k_.startswith("kept")}))

        def off_keys(prefix, es_):
            """Per-state counts as int keys of a by-item dict ({prefix}_{state}), so they add up across items."""
            c_ = off_counts(es_)
            return {f"{prefix}_{s_}": c_.get(s_, 0) for s_ in OFF_STATES}

        def off_from(bi, prefix):
            return collections.Counter({s_: bi.get(f"{prefix}_{s_}") or 0 for s_ in OFF_STATES})

        # (sixth review, BPT r6 S1) a lost change no patch touches, on an object the losing mod has on and the pair's
        # winner removes by its own change, has no effect in game while that removal stands: the winner's own removal
        # standing, nothing to patch. Its own with-patch outcome, never "left" (not in the left totals, the still-there
        # line or "still present"). An inherited removal ("on") stays left: the losing mod may undo it on purpose.
        for e in judged:
            if outcome[id(e)] == "left" and e["kind"] == "LOST-EDIT" and off_state(e) == "onOwn":
                outcome[id(e)] = "removalStands"
        all_["stands"] = [e for e in judged if outcome[id(e)] == "removalStands"]
        all_["open"] = [e for e in all_["open"] if outcome[id(e)] == "left"]
        stands = credit(all_["stands"])
        open_ = [e for e in open_ if outcome[id(e)] == "left"]
        for e in admitted:
            e["withPatch"] = outcome.get(id(e), "not-measured")
        problems, choices, problem_finding, no_body_new, problem_seen = [], 0, {}, set(), set()
        support_new = set()
        problem_objects, problem_movers = {}, {}
        gap_re = (words.get("newObjects") or {}).get("gapPattern")
        problem_rows = collections.Counter()          # rows per new problem: an object's pairs (BPT review S3)
        # new with the patch is decided by the same setup with and without the patch alone (coordinator ruling on
        # Pilgrim's blind review); a new clash another setup without the patch also has (Grand Solitude alone splits
        # the aliases the patch restores) is said as brought back from there
        no_patch_arms = [a_ for a_, pl_ in all_orders.items() if a_ != p_arm and not replaced_by.get(a_)
                         and not ({str(x).lower() for x in pl_} & patch_lower)]
        not_new = []
        master_idents = set()
        # a field a patch's value now wins over a mod's change, where no change to that field was lost without the patch:
        # a record change the patch newly undoes, a new clash (fifth blind review, W-S3 / S-S1)
        p_fields = {(str(k_[4]), (h_["row"].get("field"))) for k_, h_ in p_rows.items() if k_[1] == "LOST-EDIT"}
        undone, undone_seen = [], set()
        for rk, hit in sorted(patched_rows.items(), key=lambda kv: tuple(str(x) for x in kv[0])):
            if rk[1] == "LOST-EDIT" and rk[3].lower() in patch_lower:
                choices += 1
                v_ = hit["row"].get("values") or {}
                oe_ = own_edit(patched_arm, rk, rk[2]) or {"changed": True}
                # the prediction's set-aside rules apply here too, and a field two record checks list is one field
                # (Clockwork review S6): list order only, a list kept item by item, unused bytes are no change undone
                once_ = (str(rk[4]), hit["row"].get("field"), rk[2].lower())
                if (rk[2].lower() not in patch_lower and not is_base_game(rk[2], words)
                        and (str(rk[4]), hit["row"].get("field")) not in p_fields
                        and not reads_same_patched(rk, hit)
                        and oe_["changed"] and not oe_.get("listKept")
                        and not lost_edit_noise(rk, patched_rows, patched_arm) and once_ not in undone_seen):
                    undone_seen.add(once_)
                    undone.append((rk, hit))
                continue
            if there(p_arm, rk) or hit["history"] or rk[2].lower() == rk[3].lower():
                continue
            if rk[1] == "LOST-EDIT" and (reads_same_patched(rk, hit) or lost_edit_noise(rk, patched_rows, patched_arm)):
                continue                       # two values naming the same file, or no change the game sees (BPT S1)
            # counted by what the clash is (an object of a by-object check, once however many rows name it), never
            # by rows (P64): an object left without support against three hosts is one new problem
            ident_ = ident_of.get(rk, tuple(rk[:5]))
            if no_body(rk, hit["row"].get("other")):
                no_body_new.add(ident_)        # an object with no model: no body, not a clash
                continue
            if rk[0] in support_checks_ and support_still_removed(
                    arm_rows_.get(p_arm), arm_rows_.get(patched_arm), str(rk[4]),
                    [str(x) for x in hit["row"].get("other") or []], words):
                # (sixth review, Winterhold r6 S1) its support is removed without the patch too: not new with it
                support_new.add(ident_)
                continue
            if master_of(masters, rk[2], rk[3]) and is_geometry_check(rk[0], words):
                master_idents.add(ident_)      # any row of it is between a plugin's object and its master's
            if is_base_game(rk[2], words) or is_base_game(rk[3], words) or (
                    master_of(masters, rk[2], rk[3]) and not is_geometry_check(rk[0], words)):
                continue                       # the base game, or a record value over the plugin's own master (P63)
            if (rk[0], p_of(rk[0])) in measured:
                problem_rows[ident_] += 1
            if (rk[0], p_of(rk[0])) in measured and ident_ not in problem_seen:
                problem_seen.add(ident_)
                where_ = next((a_ for a_ in no_patch_arms if (rk[0], a_) in measured and there(a_, rk)), None)
                if where_ is not None:
                    not_new.append((rk, where_))     # still new with the patch; said where else it is measured
                problems.append(rk)

                problem_finding.setdefault(rk[0], []).append(hit["finding"].get("id"))
            if (rk[0], p_of(rk[0])) in measured and rk[1] != "LOST-EDIT" and rk[4]:
                # (BPT review 2) each new object by its base and model, with the measured gap to the nearest surface
                gap_ = None
                for t_ in (hit["row"].get("physicalDetail"), hit["row"].get("summary")):
                    m_ = re.search(gap_re, str(t_ or "")) if gap_re else None
                    if m_:
                        gap_ = float(m_.group(1))
                        break
                # (third review, BPT S3) who moved the support, as the probe's own row says
                mv_ = re.search(r"which (.+?) Moved\b", str(hit["row"].get("summary") or ""))
                if mv_:
                    problem_movers.setdefault(str(rk[4]), [])
                    if mv_.group(1) not in problem_movers[str(rk[4])]:
                        problem_movers[str(rk[4])].append(mv_.group(1))
                o_ = problem_objects.setdefault(rk[0], collections.OrderedDict()).setdefault(str(rk[4]), None)
                if gap_ is not None and (o_ is None or gap_ < o_):
                    problem_objects[rk[0]][str(rk[4])] = gap_
        spot_rows = [e for e in record if e["admission"] not in ("admitted", "repeat", "sameClash", "sameValue", "samePair",
                                                                 "undoesMaster", "superseded",
                                                                 *NOISE_ADMISSIONS)
                     and (e["checkId"], patched_arm) in measured
                     # overwrite history is said by whether it is still flagged; its measured loss, apart (W-S1)
                     and not (present if e["admission"] == "history" else there)(patched_arm, keyof(e))]
        # (eighth review round 2, Pilgrim 74) a set-aside lost edit on a field an admitted predicted row counts is that
        # field, counted once there: never again among the clashes resolved but not predicted (applied below, after the
        # undone family takes its rows: a change the patch undoes there is said, not counted)
        counted_fields_ = {(str(e["formKey"]), e.get("field")) for e in record
                           if e["admission"] == "admitted" and e["kind"] == "LOST-EDIT"}
        # (round 2, Missives S1 and Snazzy S2) an earlier record clash a patch also fixes is a change in game: one field of
        # one record, however many plugins' values or record checks name it; one whose values differ only in bytes the game
        # does not read is none; and where the patch holds none of the field, the patch removes it, said as a removal
        def spot_noise(e):
            if e["kind"] != "LOST-EDIT":
                return False
            rk_ = keyof(e)
            if lost_edit_noise(rk_) in NOISE_ADMISSIONS:
                return True
            rb_ = read_bits(p_of(e["checkId"]), rk_, str(e.get("field") or ""))
            return rb_ is not None and rb_[0] == rb_[1]

        def patch_removes(e):
            ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
            if not ch_ or len(ch_["providers"]) < 2 or str(ch_["providers"][-1]).lower() not in patch_lower:
                return False
            f_ = str(e.get("field") or "")
            n_ = len(ch_["providers"])

            def held(j_):
                # a tree answer writes a field a provider lacks as "has X ABSENT" or "has no X"
                return {k_: v_ for k_, v_ in recordrows.provider_fields(ch_, f_, j_).items()
                        if not re.match(r"^has (?:no \S|.* ABSENT$)", str(v_))}
            return not held(n_ - 1) and any(held(j_) for j_ in range(n_ - 1))
        spot_rows = [e for e in spot_rows if not (e["admission"] == "alreadyPresent" and spot_noise(e))]
        spot_kind = {id(e): ("patchRemoves" if e["admission"] == "alreadyPresent" and e["kind"] == "LOST-EDIT"
                             and patch_removes(e) else e["admission"]) for e in spot_rows}

        def spot_unit(e):
            if spot_kind[id(e)] in ("alreadyPresent", "patchRemoves", "baseOverChange") and e["kind"] == "LOST-EDIT":
                return ("change", str(e["formKey"]), str(e.get("field") or ""))
            return ("row", id(e))
        spot_units = collections.defaultdict(dict)
        for e in spot_rows:
            spot_units[spot_kind[id(e)]].setdefault(spot_unit(e), e)
        spots = collections.Counter({k: len(v) for k, v in spot_units.items()})
        fixes_ = "fix" if re.match(r"^the [\d,]+ patches$", str(patch)) else "fixes"
        # overwrite history no longer flagged is said in its own sentence, never as resolved by the patch (W3)
        # (third review, Clockwork S5) a clash already there without the evaluated mods that the patch resolves is named by
        # its pairs, so the sentence says which earlier clashes the patch also fixes; a record change by its field
        # (fourth review, Clockwork S5, BPT r2 S4) an earlier loss is named by whose change it is and the plugin it is
        # lost to in the setup without the evaluated mods (that setup's own rows), never as a pair naming the evaluated mod
        losers_of_unit = collections.defaultdict(list)
        for e in spot_rows:
            if e["kind"] == "LOST-EDIT" and e["A"] not in losers_of_unit[spot_unit(e)]:
                losers_of_unit[spot_unit(e)].append(e["A"])
        w_wins = collections.defaultdict(list)
        for (c2_, a2_), rs2_ in rows_by.items():
            if a2_ != w_arm:
                continue
            for rk2_, h2_ in rs2_.items():
                if rk2_[1] == "LOST-EDIT":
                    w_wins[(str(rk2_[4]), h2_["row"].get("field"), str(rk2_[2]).lower())].append(rk2_[3])

        # (eighth review, BPT 78) an earlier loss whose lost value is the base game's own, where the value it loses to
        # without the evaluated mods is another plugin's own change: the patch putting the base game's value back undoes
        # that change. Said in the undone family, neutral (whether intended is not recorded), never as a fix.
        bg_name_ = words.get("baseGameOwner") or "the base game"
        over_ = {}

        def owner_in(arm_, e, plugin_):
            got_ = whose_value(arm_, e["checkId"], e["formKey"], e.get("field"), plugin_)
            return None if got_ is None else owner_of(got_[0], got_[1], got_[2]["origin"])[1]
        for e in spot_rows:
            if spot_kind.get(id(e)) != "alreadyPresent" or e["kind"] != "LOST-EDIT" or not w_arm:
                continue
            ls_ = losers_of_unit.get(spot_unit(e)) or [e["A"]]
            if {owner_in(p_of(e["checkId"]), e, l_) or l_ for l_ in ls_} != {bg_name_}:
                continue
            for w_ in dict.fromkeys(w_ for l_ in ls_ for w_ in w_wins.get(
                    (str(e["formKey"]), e.get("field"), str(l_).lower()), [])):
                # that setup's own chain; where it holds none, the same plugin's value in the prediction setup
                o_ = owner_in(w_arm, e, w_) or owner_in(p_of(e["checkId"]), e, w_)
                if o_ and o_ != bg_name_ and not is_base_game(o_, words):
                    over_[id(e)] = (o_, w_)
                    spot_kind[id(e)] = "baseOverChange"
                    break
        if over_:
            spot_units = collections.defaultdict(dict)
            for e in spot_rows:
                spot_units[spot_kind[id(e)]].setdefault(spot_unit(e), e)
            spots = collections.Counter({k: len(v) for k, v in spot_units.items()})

        for k_ in [k_ for k_ in spot_units if k_ not in ("baseOverChange", "patchRemoves", "history")]:
            spot_units[k_] = {u_: e for u_, e in spot_units[k_].items()
                              if not (e["kind"] == "LOST-EDIT" and (str(e["formKey"]), e.get("field")) in counted_fields_)}
        spots = collections.Counter({k: len(v) for k, v in spot_units.items() if v})

        def spot_pairs(k):
            c_, change_ = collections.Counter(), set()
            for u_, e in spot_units.get(k, {}).items():
                if u_[0] == "change":
                    nm_ = object_name(e["formKey"], e.get("summary"))
                    key_ = f"{field_word(e.get('field'))} on {e['formKey']}" + (f" ({nm_})" if nm_ else "")
                    if k == "alreadyPresent" and words.get("blindSpotLostTo"):
                        ls_ = losers_of_unit.get(u_) or [e["A"]]
                        ws_ = list(dict.fromkeys(w_ for l_ in ls_ for w_ in w_wins.get(
                            (str(e["formKey"]), e.get("field"), str(l_).lower()), [])))

                        def own_of(l_):
                            """(sixth review, Missives) a losing value another plugin's own change carries is that
                            plugin's change"""
                            # (seventh review, Missives r6 S2) the read keyed by setup, record and field, never by a
                            # row rebuilt from another loser's summary; a value the base game's record holds from the
                            # start is the base game's
                            got_ = whose_value(p_of(e["checkId"]), e["checkId"], e["formKey"], e.get("field"), l_)
                            if got_ is None:
                                return l_
                            return owner_of(got_[0], got_[1], got_[2]["origin"])[1]
                        ls_ = list(dict.fromkeys(own_of(l_) for l_ in ls_))
                        bg_ = words.get("baseGameOwner") or "the base game"
                        if ws_ and ls_ == [bg_] and words.get("blindSpotBasePutBack"):
                            # (round 3, BPT 78) the lost value is the base game's own: the patch puts it back
                            key_ += bind(words["blindSpotBasePutBack"], {"winners": and_list([f"{w_}'s" for w_ in ws_[:2]])})
                        elif ws_:
                            key_ += bind(words["blindSpotLostTo"], {"losers": and_list([f"{l_}'s" for l_ in ls_[:2]]) + (
                                f" and {fmt_n(len(ls_) - 2)} more" if len(ls_) > 2 else ""), "winners": and_list(ws_[:2])})
                    change_.add(key_)
                else:
                    key_ = f"{e['A']} and {e['B']}"
                c_[key_] += 1
            top_ = [pr_ if pr_ in change_ else f"{pr_} {fmt_n(n_)}"
                    for pr_, n_ in sorted(c_.items(), key=lambda kv: (-kv[1], kv[0].lower()))[:3]]
            more_ = words["blindSpotMorePairs"] if len(change_) < len(c_) else words.get("blindSpotMoreChanges") or "; and {count} more"
            return "; ".join(top_) + (bind(more_, {"count": fmt_n(len(c_) - 3)}) if len(c_) > 3 else "")
        # (fourth review, Snazzy r2/r3 S1) a field the patch removes is a loss the patch introduces: its own sentence,
        # never under "resolved"
        blind = [{"admission": k, "count": n, "why": bind(words["blindSpotWhy"][k], {"patch": patch, "pairs": spot_pairs(k),
                                                                                     "fixes": fixes_})}
                 for k, n in sorted(spots.items()) if k not in ("history", "patchRemoves", "baseOverChange")]
        over_grp_ = collections.OrderedDict()           # (owner, carrier, field) -> records, said once per group
        for e in (spot_units.get("baseOverChange") or {}).values():
            over_grp_.setdefault((over_[id(e)][0], over_[id(e)][1], field_word(e.get("field"))), []).append(str(e["formKey"]))
        over_items_ = [bind(words.get("validationBaseOverChangeItem") or "", {
            "owner": o_, "winner": w_, "field": f_, "record": and_list(rs_),
            "carried": "" if str(o_).lower() == str(w_).lower()
            else bind(words.get("validationBaseOverChangeCarried") or "", {"winner": w_})})
            for (o_, w_, f_), rs_ in over_grp_.items()]
        over_sentence = (bind(words["validationBaseOverChange"], {
            "patch": patch, "count": fmt_n(spots["baseOverChange"]),
            "noun": words["fieldNoun"][0 if spots["baseOverChange"] == 1 else 1], "items": "; ".join(over_items_)})
            if spots.get("baseOverChange") and words.get("validationBaseOverChange") else None)
        removes_sentence = (bind(words["validationPatchRemoves"], {
            "patch": patch, "count": fmt_n(spots["patchRemoves"]),
            "noun": words["fieldNoun"][0 if spots["patchRemoves"] == 1 else 1], "pairs": spot_pairs("patchRemoves")})
            if spots.get("patchRemoves") and words.get("validationPatchRemoves") else None)
        # overwrite history is not a predicted clash, but a row no longer flagged with the patch whose physical test held
        # (the overwritten mod's ground lost) is named with its count (Winterhold review 8; the claim stays demoted), and
        # a navmesh whose loss still holds with the patch under another pair is named as persisting (fourth review, W3)
        # (blind round 1, Winterhold S1) a stub of the losing mod's navmesh held no ground of that mod's (navmesh_stub)
        held_gone = [e for e in spot_rows if e["admission"] == "history" and e.get("physical") == "Holds"
                     and e["checkId"] == "geometry.navmesh-ownership" and not navmesh_stub(e["A"], e["formKey"])]
        history_holds = sum(1 for e in spot_rows if e["admission"] == "history" and e.get("physical") == "Holds"
                            and not (e["checkId"] == "geometry.navmesh-ownership" and navmesh_stub(e["A"], e["formKey"])))
        # (eighth review, BPT 77, Snazzy 70) what the patch's winning value is, by the attribution read of the setup with
        # the patches: undone only where it reads the same as the value the mod's change replaced (the losing plugin's
        # inherited value); a value the patch or another plugin owns is no undo and is said apart, by its owner
        def undone_what(rk):
            # (Gate A review of 225871bb) no chain and no recorded values: whether it is undone is not told, said so,
            # never a replacement by anyone (field_chain already falls back to the row's own recorded values)
            got_ = field_chain(patched_arm, rk, rk[2])
            if got_ is None:
                return "unread", None
            provs_, vals_, _ = got_
            low_ = [str(x).lower() for x in provs_]
            if str(rk[3]).lower() not in low_:
                return "unread", None
            i_, w_ = low_.index(str(rk[2]).lower()), low_.index(str(rk[3]).lower())
            _, j_ = recordrows.own_change(provs_, vals_, i_, (masters or {}).get(str(rk[2]).lower())
                                          if masters is not None else None)
            if j_ is not None and vals_[w_] == vals_[j_]:
                return "putBack", None
            return "replaced", owner_of(provs_, vals_, w_)[1]

        def patch_sets_off(rk):
            """The winning patch's own version of the object sets it off for good (provider_disables)."""
            ch_ = (chains_of(rk[0], patched_arm) or {}).get(str(rk[4]))
            low_ = [str(x).lower() for x in (ch_ or {}).get("providers") or []]
            return bool(ch_) and str(rk[3]).lower() in low_ and provider_disables(ch_, low_.index(str(rk[3]).lower()))
        what_ = {id(h_): undone_what(rk) for rk, h_ in undone}
        replaced = [(rk, h_) for rk, h_ in undone if what_[id(h_)][0] == "replaced"]
        undone_unread = [(rk, h_) for rk, h_ in undone if what_[id(h_)][0] == "unread"]
        undone = [(rk, h_) for rk, h_ in undone if what_[id(h_)][0] == "putBack"]
        # (Gate A review, finding 4) the owning patch is named, as every other line names plugins
        replaced_whose = collections.Counter(
            ("own" if str(what_[id(h_)][1]).lower() in patch_lower else "of", what_[id(h_)][1]) for rk, h_ in replaced)
        # each replaced row's owner, read here while the classification is at hand
        replaced_owner_ = {id(h_): str(what_[id(h_)][1]) for rk, h_ in replaced}
        undone_off = [rk for rk, _ in undone if patch_sets_off(rk)]
        undone_by = collections.Counter(rk[2] for rk, _ in undone)
        switched_on = [rk for rk, h_ in undone if off_at_load((h_["row"].get("values") or {}).get("lost"))
                       and not off_at_load((h_["row"].get("values") or {}).get("winner"))]
        n_enable = len(switched_on)
        enable_examples = list(dict.fromkeys(str(rk[4]) for rk in switched_on))[:3]
        switched_on_r = [rk for rk, h_ in replaced if off_at_load((h_["row"].get("values") or {}).get("lost"))
                         and not off_at_load((h_["row"].get("values") or {}).get("winner"))]
        def problem_owners(xs):
            """(ninth review, BPT 68 / 76 / 79) the check each new object of a by-object check is counted under: the first
            by-object check that names it, in the order the tallies print (most new objects first)."""
            n_of_ = collections.Counter(rk_[0] for rk_ in xs if rk_[0] in by_object)
            owner_ = {}
            for c_ in sorted(n_of_, key=lambda c_: -n_of_[c_]):
                for rk_ in xs:
                    if rk_[0] == c_:
                        owner_.setdefault(str(rk_[4]), c_)
            return owner_

        def problems_text(with_checks=False, xs=None):
            """The new problems with the patch in their units: an object of a by-object check is counted as an object,
            with its pairs beside it (BPT review S3); with_checks names each check. xs: a subset of the problems.
            (ninth review) an object two by-object checks find is counted once, under the first; the later check's rows
            on it are said as on an object already counted."""
            xs = problems if xs is None else xs
            owner_ = problem_owners(xs)
            per = collections.OrderedDict()
            for rk_ in xs:
                ident_ = ident_of.get(rk_, tuple(rk_[:5]))
                c_ = per.setdefault((rk_[0], rk_[1] if rk_[0] not in by_object else ""), [0, 0, 0, 0])
                rows1_ = problem_rows.get(ident_, 1)
                if rk_[0] in by_object and owner_.get(str(rk_[4]), rk_[0]) != rk_[0]:
                    c_[2] += 1
                    c_[3] += rows1_
                else:
                    c_[0] += 1
                    c_[1] += rows1_
            parts_ = []
            for (check_, kind_), (n_, rows_, sn_, srows_) in sorted(per.items(), key=lambda kv: -(kv[1][0] + kv[1][2])):
                if check_ in by_object:
                    t_ = (f"{fmt_n(n_)} {'object' if n_ == 1 else 'objects'}" + (
                        f" ({fmt_n(rows_)} {unit_noun(check_, kind_, rows_)})" if rows_ != n_ else "")) if n_ else ""
                    if sn_ and words.get("validationProblemShared"):
                        # a check whose every new object is counted above names itself in the shared phrase
                        by_ = with_checks and not n_ and words.get("validationProblemSharedBy")
                        t_ += (" and " if t_ else "") + bind(words["validationProblemSharedBy" if by_ else "validationProblemShared"], {
                            "rows": f"{fmt_n(srows_)} {unit_noun(check_, kind_, srows_)}", "count": fmt_n(sn_),
                            "noun": "object" if sn_ == 1 else "objects", "title": title_of(check_)})
                        if by_:
                            parts_.append(t_)
                            continue
                else:
                    t_ = f"{fmt_n(n_)} {unit_noun(check_, kind_, n_)}"
                parts_.append(bind(words["validationProblemInCheck"], {"count": t_, "title": title_of(check_)})
                              if with_checks else t_)
            return ", ".join(parts_) if parts_ else "0"

        # (seventh review, Sewers S2) a by-object kind whose every object the headline counts under an earlier check is
        # counted once there in the comparison too: said beside the others, never added
        shared_kinds = {}
        for i_, v_ in enumerate(prediction.get("verdictCounts") or []):
            if v_.get("sharedObjects") and not v_.get("newObjects") and v_.get("sharedWith"):
                # (eighth review, Snazzy 14) each earlier entry that counted some of its objects, with how many
                sw_ = v_["sharedWith"]
                shared_kinds[(v_["checkId"], v_["kind"])] = (f'"{next(iter(sw_))}"' if len(sw_) == 1 else and_list(
                    [f'"{t_}" ({fmt_n(n_)})' for t_, n_ in sorted(sw_.items(), key=lambda kv: (-kv[1], kv[0]))]))

        def shared_note(xs):
            c_ = collections.Counter((e["checkId"], e["kind"]) for e in xs if (e["checkId"], e["kind"]) in shared_kinds)
            return "".join(bind(words["byUnitSameObjects"], {"count": fmt_n(n_), "noun": unit_noun(k_[0], k_[1], n_),
                                                           "titles": shared_kinds[k_]})
                           for k_, n_ in c_.items() if words.get("byUnitSameObjects"))

        def by_unit(xs, named=False):
            """A count given once per unit when its clashes are of several (never pairs added to record fields); named:
            the unit is said even when there is one."""
            c_ = collections.Counter()
            note_ = shared_note(xs)
            if note_:
                xs = [e for e in xs if (e["checkId"], e["kind"]) not in shared_kinds]
            for e in xs:
                c_[(unit_noun(e["checkId"], e["kind"], 1), unit_noun(e["checkId"], e["kind"], 2))] += 1
            if len(c_) <= 1 and not named:
                return fmt_n(sum(c_.values())) + note_
            return ", ".join(f"{fmt_n(n_)} {u_[0] if n_ == 1 else u_[1]}" for u_, n_ in
                             sorted(c_.items(), key=lambda kv: (-kv[1], kv[0]))) + note_

        def by_unit_note(xs, named=True):
            """by_unit, each unit's count followed by how many of its lost changes are on objects set off for good."""
            c_ = collections.defaultdict(list)
            for e in xs:
                c_[(unit_noun(e["checkId"], e["kind"], 1), unit_noun(e["checkId"], e["kind"], 2))].append(e)
            if len(c_) <= 1 and not named:
                return fmt_n(len(xs)) + off_note(xs)
            def eff_note_(es_):
                n_ = sum(1 for e in es_ if e["kind"] != "LOST-EDIT" and effect_pair(e["formKey"], e.get("other")))
                return (" (" + bind(words["stillThereEffect"], {"count": fmt_n(n_)}) + ")"
                        if n_ and words.get("stillThereEffect") else "")
            return ", ".join(f"{fmt_n(len(es_))} {u_[0] if len(es_) == 1 else u_[1]}" + off_note(es_) + eff_note_(es_)
                             for u_, es_ in sorted(c_.items(), key=lambda kv: (-len(kv[1]), kv[0])))

        sentences = [bind(words["validationHits"], {"patch": patch, "count": by_unit(hits)})]
        patch_moves_said_ = []
        # (Missives report review R1) what the patch itself does to an object of a clash it resolves, read from the
        # patch's own record and the version it overrides (both plugin files the plan pinned): a move, a turn, a
        # removal. Said beside the clash, so the reader is told which edit ends it.
        if words.get("validationPatchMoves"):
            said_ = []
            for e in hits:
                if e["kind"] == "LOST-EDIT":
                    continue
                for fk_ in [e["formKey"], *(e.get("other") or [])]:
                    for p_ in patch_names:
                        mine_ = plugin_record(p_, "placementFull", str(fk_)) or {}
                        if not mine_.get("position"):
                            continue
                        prev_ = next(((m_, plugin_record(m_, "placementFull", str(fk_)) or {}) for m_ in (e["A"], e["B"])
                                      if (plugin_record(m_, "placementFull", str(fk_)) or {}).get("position")), (None, {}))
                        if not prev_[0]:
                            continue
                        d_ = sum((float(a_) - float(b_)) ** 2 for a_, b_ in zip(mine_["position"], prev_[1]["position"])) ** 0.5
                        turned_ = any(abs(float(a_) - float(b_)) > 1e-3 for a_, b_ in
                                      zip(mine_.get("rotation") or [], prev_[1].get("rotation") or []))
                        gone_ = mine_.get("initiallyDisabled") or mine_.get("deleted")
                        if d_ < 1.0 and not turned_ and not gone_:
                            continue
                        what_ = (words.get("validationPatchMovesRemoved") if gone_ else
                                 bind(words["validationPatchMovesMoved"], {"m": fmt_n(round(d_ / 70.0))})
                                 if d_ >= 70.0 else words.get("validationPatchMovesNudged") or "")
                        if turned_ and not gone_ and words.get("validationPatchMovesTurned"):
                            what_ = (what_ + " " if what_ else "") + words["validationPatchMovesTurned"]
                        said_.append(bind(words["validationPatchMoves"], {
                            "patch": p_, "object": object_name(fk_) or str(fk_), "formKey": str(fk_),
                            "mod": prev_[0], "what": what_.strip(), "clash": unit_noun(e["checkId"], e["kind"], 1)}))
                        break
            patch_moves_said_ = list(dict.fromkeys(said_))
            sentences += patch_moves_said_

        def pair_by(xs):
            return sorted(collections.Counter(f"{e['A']}|{e['B']}" for e in xs).items(), key=lambda kv: (-kv[1], kv[0].lower()))
        if switched:
            sentences.append(bind(words["validationSwitched"], {"patch": patch, "count": fmt_n(len(switched)), "by": "; ".join(
                bind(words["validationSwitchedBy"], {"keeper": k_.split("|")[0], "dropped": k_.split("|")[1],
                                                     "count": fmt_n(n_)}) for k_, n_ in pair_by(switched))}))
        if own:
            n_ob_ = sum(1 for e in own if base_put_back(e))
            sentences.append(bind(words["validationOwnAllBase"], {"patch": patch, "count": fmt_n(len(own))})
                             if n_ob_ == len(own) and words.get("validationOwnAllBase") else
                             bind(words["validationOwnSomeBase"], {"count": fmt_n(len(own)), "base": fmt_n(n_ob_),
                                                                   "own": fmt_n(len(own) - n_ob_)})
                             if n_ob_ and words.get("validationOwnSomeBase") else
                             bind(words["validationOwn"], {"patch": patch, "count": len(own)}))
        if kept:
            kept_by = collections.Counter(e["A"] for e in kept)
            # whose change: named in the with-patch line and each item line; here only when several mods lose
            by_ = (" (" + "; ".join(
                bind(words["validationKeptBy"], {"loser": m_, "count": fmt_n(n_)})
                for m_, n_ in sorted(kept_by.items(), key=lambda kv: (-kv[1], kv[0].lower()))) + ")") if len(kept_by) > 1 else ""
            n_kept_ = fmt_n(len(kept)) + off_note(kept)
            sentences.append(bind(words["validationKept"], {"patch": patch, "count": n_kept_, "by": by_}))
        placed_pairs, placed_sentence_ = [], None      # (tenth review) the re-measured pairs, for the index
        if placed:
            # (seventh review, Clockwork S3) where the setup with the patch measured each again, what it found there
            # (seventh review round 3, Missives 73) each pair's figures before and after come from that same pair's own
            # rows: its row in the setup without the patch and its re-measure in the setup with it
            tri_ = []
            for e in placed:
                rm_ = (remeasure_with_patch(e["checkId"]) or {}).get(
                    (e["kind"], e["A"], e["B"], e["formKey"], (e.get("other") or [None])[0]))
                # (round 4, Winterhold 88) the whole number, thousands separator included
                m_ = re.search(r"(\d[\d,]*) triangle pairs", rm_[1]) if rm_ and rm_[0] == "Holds" else None
                b_ = re.search(r"(\d[\d,]*) triangle pairs", str(((p_rows.get(keyof(e)) or {}).get("row") or {}).get(
                    "physicalDetail") or ""))
                tri_.append((int(b_.group(1).replace(",", "")), int(m_.group(1).replace(",", ""))) if m_ and b_ else None)
                # (tenth review, Clockwork 75 / Winterhold 88) each pair's figures and files, for the evidence index;
                # the probe's summary count beside them, as the files print both
                s_ = re.search(r"(\d[\d,]*) triangle pairs", str(e.get("summary") or ""))
                main_b10_ = ctx.get("mainBundle")
                placed_pairs.append({
                    "object": str(e["formKey"]), "other": str((e.get("other") or [""])[0] or ""), "A": e["A"], "B": e["B"],
                    "before": b_.group(1) if b_ else None, "summary": s_.group(1) if s_ else None,
                    "after": m_.group(1) if m_ else None,
                    "beforeFile": (main_b10_.output_emit_path(e["checkId"], p_arm, "arm")
                                   or main_b10_.output_emit_path(e["checkId"], p_arm, None)) if main_b10_ else None,
                    "afterFile": remeasure_with_patch_path.get(e["checkId"])})
            placed_sentence_ = True
            sentences.append(bind(words["validationPlaced"], {"patch": patch, "count": by_unit(placed)})
                             + (" " + bind(words["validationPlacedMeasured"], {
                                 "patch": patch, "before": fmt_n(sum(x_[0] for x_ in tri_)),
                                 "after": fmt_n(sum(x_[1] for x_ in tri_)),
                                 "each": words.get("validationPlacedEach") or "" if len(tri_) > 1 else ""})
                                if tri_ and None not in tri_ and words.get("validationPlacedMeasured") else ""))
            # (WRW r3 report review 1) pairs the patch's own placement makes cross more are said, with the largest
            # rise, never only inside a total that falls
            if tri_ and None not in tri_ and words.get("validationPlacedWorse"):
                up_ = [(a_ - b_, k_) for k_, (b_, a_) in enumerate(tri_) if a_ > b_]
                if up_:
                    _, k_ = max(up_)
                    e_ = placed[k_]
                    o_ = (e_.get("other") or [None])[0]
                    sentences[-1] += " " + bind(words["validationPlacedWorse"], {
                        "up": fmt_n(len(up_)), "count": fmt_n(len(tri_)), "upVerb": "crosses" if len(up_) == 1 else "cross",
                        "downVerb": "crosses" if sum(1 for b_, a_ in tri_ if a_ < b_) == 1 else "cross",
                        "down": fmt_n(sum(1 for b_, a_ in tri_ if a_ < b_)), "same": fmt_n(sum(1 for b_, a_ in tri_ if a_ == b_)),
                        "object": named(e_["formKey"], e_.get("summary")), "other": named(o_, None) if o_ else "another object",
                        "before": fmt_n(tri_[k_][0]), "after": fmt_n(tri_[k_][1])})
            placed_sentence_ = sentences[-1] if placed_pairs and tri_ and None not in tri_ else None
        if stands:
            sentences.append(bind(words["validationStands"], {"patch": patch, "count": by_unit(stands)}))
        # still present: left untouched never counts a clash whose objects a patch edits (round 4)
        edited = sum(1 for e in open_ if left_split.get(id(e)) == "patchHeld")
        n_open_ = by_unit_note(open_, False)
        # (third review, Clockwork S1) where every clash still intersecting is a patch's placement, nothing is said as
        # still present: no "still present: 0"
        sentences += ([bind(words["validationOpen"], {"patch": patch, "count": n_open_, "split": (bind(
                          words["validationOpenSplit"], {"untouched": fmt_n(len(open_) - edited), "edited": fmt_n(edited)})
                          if edited else "")})] if open_ or not (placed or stands) else []) + [
                     # new problems in their units, an object of a by-object check with its pairs (BPT review S3)
                     bind(words["validationPatchProblems"], {"patch": patch, "count": problems_text(), "undone": ""})]
        new_idx = len(sentences) - 1                   # the new-clashes sentence
        # (fourth review, Sewers S3; round 3) what is still there with a mod on records, navmeshes or objects no supplied
        # patch edits, and what lies on ones a patch edits: what the records show, never what a patch sets out to do
        if masters is not None and open_ and words.get("validationPatchScope"):
            covered_ = list(dict.fromkeys(m_ for p_ in patch_names for m_ in (masters.get(str(p_).lower()) or [])
                                          if not is_base_game(m_, words) and str(m_).lower() not in pt_lower
                                          and str(m_).lower() not in patch_lower))
            disp_ = {str(x_).lower(): x_ for a_ in all_orders.values() for x_ in a_ or []}
            # in load order (a plugin's master list is read as a set: its order is not the file's)
            ord_ = [str(x_).lower() for x_ in all_orders.get(p_arm) or []]
            covered_ = sorted((disp_.get(str(m_).lower(), m_) for m_ in covered_),
                              key=lambda m_: (ord_.index(str(m_).lower()) if str(m_).lower() in ord_ else 10 ** 6, str(m_).lower()))
            cov_low_ = {str(m_).lower() for m_ in covered_}
            def owner_(e, m_):
                """(sixth review, BPT r6 S3) a winning value another plugin's own change carries is that plugin's"""
                if e["kind"] != "LOST-EDIT" or m_ != e["B"]:
                    return m_
                # (seventh review) the attribution read, by setup, record and field
                got_ = whose_value(p_of(e["checkId"]), e["checkId"], e["formKey"], e.get("field"), m_)
                o_ = owner_of(got_[0], got_[1], got_[2]["origin"])[1] if got_ is not None else m_
                # (eighth review, BPT 75) a winning value whose owner is the base game is no mod's: the remainder is
                # said with the plugin whose version wins, never dropped
                if is_base_game(o_, words) or str(o_).lower() == str(words.get("baseGameOwner") or "the base game").lower():
                    return m_
                return o_
            def patch_edits(e):
                """(seventh review round 2, Clockwork 77) a declared patch holds a version of what the remainder is
                about: the record of a lost change (the chain with the patches), a navmesh, or an object of a geometry
                pair (read from the patch's own file). Only then is the remainder inside what the patch sets out to fix;
                listing the mod as a master is not enough."""
                if e["kind"] == "LOST-EDIT":
                    ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"])) or {}
                    return bool({str(x).lower() for x in ch_.get("providers") or []} & patch_lower)
                what_ = "navmesh" if e["checkId"] == "geometry.navmesh-ownership" else "placementFull"
                for p_ in patch_names:
                    for fk_ in [e["formKey"], *(e.get("other") or [])]:
                        if fk_ and plugin_record(p_, what_, str(fk_)):
                            return True
                return False
            edited_ = {id(e): patch_edits(e) for e in open_}
            outside_ = list(dict.fromkeys(m_ for e in open_ for m_ in (owner_(e, e["A"]), owner_(e, e["B"]))
                                          if str(m_).lower() not in pt_lower and not is_base_game(m_, words)
                                          and str(m_).lower() != str(words.get("baseGameOwner") or "the base game").lower()
                                          and str(m_).lower() not in patch_lower
                                          and not edited_[id(e)]))
            # (seventh review, Clockwork S4) what is still there with a mod the patches do list as a master is inside what
            # they set out to fix and left by them: said too, in its units
            inside_ = collections.OrderedDict()
            for e in open_:
                for m_ in dict.fromkeys((owner_(e, e["A"]), owner_(e, e["B"]))):
                    if str(m_).lower() not in pt_lower and edited_[id(e)] and not is_base_game(m_, words)                             and str(m_).lower() not in patch_lower:
                        inside_.setdefault(disp_.get(str(m_).lower(), m_), []).append(e)
            # (eighth review, page budget) every mod inside said in one sentence, each with its count
            inside_text_ = (" " + bind(words["validationPatchScopeInside"], {
                "inside": and_list([f"{m_} ({by_unit(es_, True)})" for m_, es_ in inside_.items()])})
                if inside_ and words.get("validationPatchScopeInside") else "")
            # (seventh review round 2) a mod with remainders on both sides is said with the count of each, so one name
            # is never both outside and inside without saying which part
            def outside_part_(m_):
                if disp_.get(str(m_).lower(), m_) not in inside_:
                    return m_
                es_ = [e for e in open_ if not edited_[id(e)]
                       and str(m_).lower() in {str(x_).lower() for x_ in (owner_(e, e["A"]), owner_(e, e["B"]))}]
                return f"{m_} ({by_unit(es_, True)})" if es_ else m_
            if outside_:
                sentences.insert(new_idx, bind(words["validationPatchScope"], {
                    "patch": patch, "covered": and_list(covered_) if covered_ else words.get("validationPatchScopeNone") or "none",
                    "outside": and_list([outside_part_(m_) for m_ in outside_]), "lists": "list" if len(patch_names) > 1 else "lists",
                    "it": "they set" if len(patch_names) > 1 else "it sets"})
                    + inside_text_)
                new_idx += 1
            elif inside_text_:
                sentences.insert(new_idx, inside_text_.strip())
                new_idx += 1
        not_new_sentence = None
        if not_new:
            by_setup = collections.OrderedDict()
            for rk, a_ in not_new:
                by_setup.setdefault((rk[0], a_), []).append(rk)
            sentences.append(bind(words["validationBroughtBack"], {"patch": patch, "parts": "; ".join(
                bind(words["validationBroughtBackPart"], {
                    "count": by_unit([{"checkId": c_, "kind": rk_[1]} for rk_ in rks]), "title": title_of(c_),
                    "setup": describe_setup(a_), "patch": patch, "mods": " and ".join(
                        x for x in all_orders.get(p_arm) or [] if x.lower() not in {y.lower() for y in all_orders.get(a_) or []}
                        and x.lower() in pt_lower) or "the evaluated mod"}) for (c_, a_), rks in by_setup.items())}))
            not_new_sentence = sentences[-1]
        undone_sentence = None

        def by_plugin(rows_):
            """Each plugin's fields, with the records they are on (seventh review, BPT S1); itemised for a plugin with at
            most 3 fields on at most 3 records, a count otherwise (page budget)."""
            by_ = collections.Counter(rk_[2] for rk_, _ in rows_)
            return "; ".join(bind(words.get("validationUndoneBy") if (
                len({h_["row"].get("field") for rk_, h_ in rows_ if rk_[2] == m_}) <= 3
                and len({str(rk_[4]) for rk_, _ in rows_ if rk_[2] == m_}) <= 3) and words.get("validationUndoneBy")
                else "{mod} {count}", {
                "mod": m_, "count": fmt_n(n_),
                "fields": (lambda fs_: and_list(fs_[:2]) + (" and more" if len(fs_) > 2 else ""))(
                    list(dict.fromkeys(field_word(h_["row"].get("field")) for rk_, h_ in rows_ if rk_[2] == m_))),
                "records": (lambda rs_: f" of {and_list(rs_)}" if len(rs_) <= 3 else "")(
                    list(dict.fromkeys(str(rk_[4]) for rk_, _ in rows_ if rk_[2] == m_)))})
                for m_, n_ in sorted(by_.items(), key=lambda kv: (-kv[1], kv[0].lower())))

        def enables_text(on_, key_="validationUndoneEnables", total_=None):
            return bind(words.get(key_) or words["validationUndoneEnables"], {"total": fmt_n(total_ or len(on_)),
                "count": fmt_n(len(on_)), "examples": "; ".join(list(dict.fromkeys(str(rk[4]) for rk in on_))[:3]),
                "objects": (lambda n_: f"{fmt_n(n_)} {'object' if n_ == 1 else 'objects'}")(
                    len({str(rk[4]) for rk in on_}))}) if on_ else ""
        # with several patches, each undone change is said under the patch whose own record holds the winning value (the
        # row's winner), never under all of them and never by load position
        undone_said_, undone_by_patch_ = {}, []
        undone_groups_ = ([(patch, list(undone))] if len(patch_names) <= 1 else
                          [(p_, [x_ for x_ in undone if str(x_[0][3]).lower() == str(p_).lower()]) for p_ in patch_names]
                          + [(patch, [x_ for x_ in undone if str(x_[0][3]).lower() not in patch_lower])])
        for who_, und_ in undone_groups_:
            if not und_:
                continue
            und_keys_ = {id(x_[0]) for x_ in und_}
            off_ = [rk_ for rk_ in undone_off if id(rk_) in und_keys_ or len(undone_groups_) == 1]
            on_ = [rk_ for rk_ in switched_on if id(rk_) in und_keys_ or len(undone_groups_) == 1]
            # changes the patches undo are the patch author's choices, said in their own neutral line, never as new
            # clashes (coordinator, item 3 of the fifth review; P52): the objects switched back on are the examples
            sentences.append(bind(words["validationUndone"], {
                "patch": who_, "count": fmt_n(len(und_)), "noun": words["fieldNoun"][0 if len(und_) == 1 else 1],
                "by": by_plugin(und_),
                "off": bind(words.get("validationUndoneOff") or "", {"count": fmt_n(len(off_)),
                                                                      "patch": (lambda ps_: ps_[0] if len(ps_) == 1 else "a patch")(
                                                                          list(dict.fromkeys(str(rk_[3]) for rk_ in off_)))})
                if off_ else "",
                "enables": enables_text(on_, total_=len(und_))}))
            undone_sentence = undone_sentence or sentences[-1]
            undone_said_[who_] = sentences[-1]
            # each patch's side effect for its verdict: whose changes it puts back, on which records
            for m_ in dict.fromkeys(rk_[2] for rk_, _ in und_):
                undone_by_patch_.append({"patch": who_, "mod": m_,
                                         "count": sum(1 for rk_, _ in und_ if rk_[2] == m_),
                                         "records": list(dict.fromkeys(str(rk_[4]) for rk_, _ in und_ if rk_[2] == m_))})
        replaced_sentence = None
        # with several patches, each replaced change is said under the patch whose own record sets the value, and joined
        # to an undone line only when that line is the same patch's
        own_of_ = lambda h_: (replaced_owner_[id(h_)] if replaced_owner_[id(h_)].lower() in patch_lower else None)  # noqa: E731
        replaced_groups_ = ([(patch, list(replaced))] if len(patch_names) <= 1 else
                            [(p_, [x_ for x_ in replaced if str(own_of_(x_[1]) or "").lower() == str(p_).lower()])
                             for p_ in patch_names] + [(patch, [x_ for x_ in replaced if own_of_(x_[1]) is None])])
        for who_r_, rep_ in replaced_groups_:
            if not (rep_ and words.get("validationReplaced")):
                continue
            whose_ = collections.Counter(
                ("own" if replaced_owner_[id(h_)].lower() in patch_lower else "of", replaced_owner_[id(h_)]) for rk, h_ in rep_)
            sw_on_ = [rk for rk, h_ in rep_ if off_at_load((h_["row"].get("values") or {}).get("lost"))
                      and not off_at_load((h_["row"].get("values") or {}).get("winner"))]
            host_ = (undone_said_.get(who_r_) if len(patch_names) > 1 else undone_sentence)
            in_undone_ = bool(host_) and bool(words.get("validationReplacedClause"))
            sentences.append(bind(words["validationReplacedClause" if in_undone_ else "validationReplaced"], {
                "patch": who_r_, "count": fmt_n(len(rep_)), "noun": words["fieldNoun"][0 if len(rep_) == 1 else 1],
                "by": by_plugin(rep_),
                # (page budget) the owning patches with their counts after one lead; other owners by name
                "whose": "; ".join(x_ for x_ in (
                    (words.get("validationReplacedOwnLead") or "") + ", ".join(
                        bind(words["validationReplacedOwn"], {"count": fmt_n(n_), "owner": k_[1]})
                        for k_, n_ in whose_.most_common() if k_[0] == "own"),
                    ", ".join(bind(words["validationReplacedOf"], {"count": fmt_n(n_), "owner": k_[1]})
                              for k_, n_ in whose_.most_common() if k_[0] != "own"))
                    if x_ and x_ != (words.get("validationReplacedOwnLead") or "")),
                "enables": enables_text(sw_on_, "validationReplacedEnables", len(rep_))}))
            replaced_sentence = replaced_sentence or sentences[-1]
            if in_undone_:
                tail_ = words.get("validationUndoneTail") or ""
                i_ = sentences.index(host_)
                head_ = host_[:-len(tail_)] if tail_ and host_.endswith(tail_) else host_.rstrip(".")
                sentences[i_] = head_ + sentences.pop() + tail_
                if host_ == undone_sentence:
                    undone_sentence = sentences[i_]
                undone_said_[who_r_] = sentences[i_]
                replaced_sentence = sentences[i_]
        if undone_unread and words.get("validationUndoneUnread"):
            sentences.append(bind(words["validationUndoneUnread"], {
                "patch": patch, "count": fmt_n(len(undone_unread)),
                "noun": words["fieldNoun"][0 if len(undone_unread) == 1 else 1], "by": by_plugin(undone_unread)}))
        # (fifth review, Snazzy pelts) a row set aside as cannot tell that the setup with the patch no longer lists is no
        # longer found there, never "resolved": whether it was a problem was not told
        ct_set_ = set(words.get("cannotTellAdmissions") or [])
        ct_ = [s_ for s_ in blind if s_["admission"] in ct_set_]
        blind = [s_ for s_ in blind if s_["admission"] not in ct_set_]
        if ct_ and words.get("validationNoLongerFound"):
            sentences.append(bind(words["validationNoLongerFound"], {"patch": patch, "parts": "; ".join(
                f"{fmt_n(s_['count'])} " + bind(words['blindSpotShort'].get(s_['admission'], s_['why']), {"patch": patch})
                for s_ in ct_)}))
        if len(blind) > 1 and words.get("validationBlindSpots"):
            # several reasons in one sentence, each with its count (P53: one line each took a third of a page)
            sentences.append(bind(words["validationBlindSpots"], {"patch": patch, "parts": "; ".join(
                f"{fmt_n(s['count'])} " + bind(words['blindSpotShort'].get(s['admission'], s['why']), {
                    "patch": patch, "pairs": spot_pairs(s['admission']), "fixes": fixes_}) for s in blind)}))
        else:
            sentences += [bind(words["validationBlindSpot"], {"patch": patch, "count": s["count"], "why": s["why"]})
                          for s in blind]
        if removes_sentence:
            sentences.append(removes_sentence)
        # (tenth review, Snazzy S2) superseded rows a supplied patch puts back: named in the comparison
        for w_, rows_ in sup_back_.items():
            if words.get("validationSupersededBack"):
                sentences.append(bind(words["validationSupersededBack"], {
                    "winner": w_, "patch": patch, "count": fmt_n(len(rows_)),
                    "noun": words["fieldNoun"][0 if len(rows_) == 1 else 1],
                    "losers": and_list(list(dict.fromkeys(e["A"] for e in rows_))),
                    "examples": "; ".join(f"{e['formKey']} ({field_word(e.get('field'))})" for e in rows_[:3])}))
        if over_sentence:
            # (page budget) said in the undone line where there is one, as its own clause
            if undone_sentence in sentences and words.get("validationBaseOverChangeClause"):
                i_ = sentences.index(undone_sentence)
                tail_ = words.get("validationUndoneTail") or ""
                head_ = undone_sentence[:-len(tail_)] if tail_ and undone_sentence.endswith(tail_) else undone_sentence.rstrip(".")
                sentences[i_] = head_ + bind(words["validationBaseOverChangeClause"], {
                    "count": fmt_n(spots["baseOverChange"]), "noun": words["fieldNoun"][0 if spots["baseOverChange"] == 1 else 1],
                    "items": "; ".join(over_items_)}) + tail_
                undone_sentence = sentences[i_]
            else:
                sentences.append(over_sentence)
        if spots.get("history"):
            sentences.append(bind(words["validationHistoryHolds"], {"count": fmt_n(history_holds), "patch": patch,
                                                                    "total": fmt_n(spots.get("history", 0))}))
            n_persist, _, persist_ex = persisting([(e["kind"], e["A"], e["B"], e["formKey"], (p_rows.get(keyof(e)) or {}).get("row", {}).get("label"))
                                                   for e in held_gone])
            if n_persist:
                sentences[-1] += " " + bind(words["validationHistoryPersists"], {"patch": patch, "count": fmt_n(n_persist),
                                                                                 "examples": persist_ex})
            # (fourth review, Winterhold S4) the navmesh lines at the top count every record still losing ground with
            # the patch; the ones still flagged are not in this line: said, so the two figures reconcile
            still_total_ = sum(n_ for _, _, n_ in ground_still)
            gone_pair_ = {(str(e["A"]).lower(), str(e["B"]).lower()) for e in held_gone}
            n_gone_pair_ = persisting([(e["kind"], e["A"], e["B"], e["formKey"], None) for e in held_gone
                                       if any((str(e["A"]).lower(), str(e["B"]).lower()) == (l_.lower(), w_.lower())
                                              for l_, w_, _ in ground_still)])[0] if gone_pair_ else 0
            if still_total_ > n_gone_pair_ and words.get("validationHistoryStillFlagged"):
                sentences[-1] += " " + bind(words["validationHistoryStillFlagged"], {
                    "count": fmt_n(still_total_ - n_gone_pair_), "total": fmt_n(still_total_), "patch": patch})
        no_body_new -= problem_seen            # an object a bodied row of another check counts is not set aside
        if no_body_new:
            # said with the new clashes it is set aside from (page budget)
            sentences[new_idx] += " " + bind(words["validationNoBody"], {"patch": patch, "count": len(no_body_new)})
        support_new -= problem_seen
        if support_new and words.get("validationSupportRemoved"):
            sentences[new_idx] += " " + bind(words["validationSupportRemoved"], {"patch": patch, "count": len(support_new)})
        if problems and not bases_read and any(rk[0] in body_checks for rk in problems):
            sentences.append(bind(words["validationBodyNotRead"], {"patch": patch}))
        if untold:
            sentences.append(bind(words["validationCannotTell"], {"patch": patch, "count": by_unit(untold)}))
        if untold_value:
            sentences.append(bind(words["validationValueUnrecorded"], {"patch": patch, "count": len(untold_value)}))
        if not_measured:
            sentences.append(bind(words["validationNotMeasured"], {"patch": patch, "count": by_unit(not_measured)}))
        # (Missives r3 report review, finding 1) what the patch does in game, as one list read from its own records: the
        # contested fields where it keeps the losing mod's version, the changes it undoes (an object switched back on
        # first), and the values it sets of its own (a move in metres, else the field). It leads the section and
        # replaces the patch-move lines; the counted lines stay below it.
        does_, does_who_ = [], []

        def bare_(name_):
            return re.sub(r"^(a|an) ", "", str(name_))
        if words.get("validationDoes"):
            dw_ = words["validationDoes"]
            restored_ = [e for e in hits if e["kind"] == "LOST-EDIT"] + list(switched)
            for (los_, win_), es_ in sorted(collections.OrderedDict(
                    ((e["A"], e["B"]), [x for x in restored_ if (x["A"], x["B"]) == (e["A"], e["B"])])
                    for e in restored_).items()):
                objs_ = list(dict.fromkeys(object_name(e["formKey"]) or str(e["formKey"]) for e in es_))
                # the patch whose own record wins the field with the patch loaded (a lost row's winner), else none named
                who_ = list(dict.fromkeys(str(w_["winnerPlugin"]) for e in es_
                                          for w_ in lost_w.get((e["checkId"], e["formKey"], e.get("field"))) or []
                                          if str(w_["winnerPlugin"]).lower() in patch_lower))
                does_who_.append(who_[0] if len(who_) == 1 else None)
                does_.append(bind(dw_["keeps"], {"loser": los_, "winner": win_, "count": fmt_n(len(es_)),
                                                 "noun": words["fieldNoun"][0 if len(es_) == 1 else 1],
                                                 "objects": and_list(objs_[:4]) + (f" and {fmt_n(len(objs_) - 4)} more"
                                                                                   if len(objs_) > 4 else "")}))
            on_keys_ = {str(rk[4]) for rk in list(switched_on) + list(switched_on_r)}
            by_obj_ = collections.OrderedDict()
            for rk, h_ in list(undone) + list(replaced):
                by_obj_.setdefault((str(rk[4]), rk[2]), []).append((rk, h_, (rk, h_) in replaced))
            for (fk_, mod_), xs_ in sorted(by_obj_.items(), key=lambda kv: (kv[0][0] not in on_keys_, kv[0][0])):
                if fk_ in on_keys_:
                    fs_ = [field_word(h_["row"].get("field")) for _, h_, _ in xs_]
                    does_who_.append(str(xs_[0][0][3]))
                    does_.append(bind(dw_["switchOn"], {"object": object_name(fk_) or fk_, "formKey": fk_, "mod": mod_,
                                                        "fields": and_list(list(dict.fromkeys(fs_)))}))
            moves_ = collections.OrderedDict()
            offs_ = collections.OrderedDict()
            for (fk_, mod_), xs_ in by_obj_.items():
                if fk_ in on_keys_:
                    continue
                # a patch version that sets the object off for good (below the world, or opposite to an always-enabled
                # parent) switches it off: said as that, never as a move or as flags it sets
                won_ = plugin_record(str(xs_[0][0][3]), "placementFull", fk_) or {}
                if dw_.get("switchesOff") and placement_off_for_good(won_, words):
                    offs_.setdefault((mod_, str(xs_[0][0][3])), []).append(fk_)
                    continue
                put_ = [field_word(h_["row"].get("field")) for _, h_, r_ in xs_ if not r_]
                if put_:
                    does_who_.append(str(xs_[0][0][3]))
                    does_.append(bind(dw_["putBack"], {"object": object_name(fk_) or fk_, "mod": mod_,
                                                       "fields": and_list(list(dict.fromkeys(put_)))}))
                own_ = [h_["row"].get("field") for _, h_, r_ in xs_ if r_]
                moved_ = None
                if any(str(f_) in set(words.get("placementFields") or ["Placement"]) for f_ in own_):
                    # the move is read from the record of the patch that holds the winning placement, not by position
                    for p_ in [str(xs_[0][0][3])]:
                        a_ = plugin_record(p_, "placementFull", fk_) or {}
                        b_ = plugin_record(mod_, "placementFull", fk_) or {}
                        if a_.get("position") and b_.get("position"):
                            moved_ = sum((float(x) - float(y)) ** 2 for x, y in zip(a_["position"], b_["position"])) ** 0.5
                            break
                if moved_ is not None and moved_ >= 70.0:
                    moves_.setdefault((mod_, str(xs_[0][0][3])), []).append((fk_, moved_ / 70.0))
                    own_ = [f_ for f_ in own_ if str(f_) not in set(words.get("placementFields") or ["Placement"])]
                if own_:
                    does_who_.append(str(xs_[0][0][3]))
                    does_.append(bind(dw_["sets"], {"object": bare_(object_name(fk_) or fk_), "mod": mod_,
                                                    "fields": and_list(list(dict.fromkeys(field_word(f_) for f_ in own_)))}))
            for (mod_, who_), fks_ in offs_.items():
                does_who_.append(who_)
                objs_ = list(dict.fromkeys(bare_(object_name(f_) or f_) for f_ in fks_))
                does_.append(bind(dw_["switchesOff"], {
                    "mod": mod_, "count": fmt_n(len(fks_)), "noun": "object" if len(fks_) == 1 else "objects",
                    "objects": and_list(objs_[:4]) + (f" and {fmt_n(len(objs_) - 4)} more" if len(objs_) > 4 else "")}))
            for (mod_, mover_), ms_ in moves_.items():
                lo_, hi_ = min(m_ for _, m_ in ms_), max(m_ for _, m_ in ms_)
                does_who_.append(mover_)
                does_.append(bind(dw_["moves"], {
                    "mod": mod_, "objects": and_list(list(dict.fromkeys(bare_(object_name(f_) or f_) for f_, _ in ms_))),
                    "m": fmt_n(round(lo_)) if round(lo_) == round(hi_) else f"{fmt_n(round(lo_))}-{fmt_n(round(hi_))}"}))
            if does_:
                # the move lines are in the list; the undone and set-to lines stay below it as the counted detail with
                # their evidence links
                sentences = [x for x in sentences if x not in set(patch_moves_said_)]
                # one list per patch, each from the records that patch carries with the winning value
                groups_ = collections.OrderedDict()
                for who_, d_ in zip(does_who_, does_):
                    key_ = next((p_ for p_ in patch_names if who_ and str(p_).lower() == str(who_).lower()), None)
                    groups_.setdefault(patch if len(patch_names) <= 1 or key_ is None else key_, []).append(d_)
                for k_ in list(groups_):
                    # a clause said for several records with the same name is said once, with how many
                    n_ = collections.Counter(groups_[k_])
                    groups_[k_] = [d_ + (bind(dw_.get("times") or "", {"count": fmt_n(n_[d_])}) if n_[d_] > 1 else "")
                                   for d_ in dict.fromkeys(groups_[k_])]
                for k_, (who_, ds_) in enumerate(groups_.items()):
                    sentences.insert(1 + k_, bind(dw_["lead"], {"patch": who_}) + " " + "; ".join(ds_) + ".")
        # (Missives r3 report review, finding 3) the setup with the patch's own gaps, said where the patch is judged
        if words.get("validationPatchGaps") and patched_arm:
            gaps_ = [m for m in ctx["missing"] if m.get("armId") == patched_arm and m.get("part") not in ("diff", "reverse")]
            by_check_ = collections.OrderedDict()
            for m in gaps_:
                by_check_.setdefault(str(m.get("checkId")), []).append(str(m.get("status")))
            # the same one reason per check as "What the checks could not judge" says (the skill's precedence)
            parts_ = [f"{title_of(c_)} ({ctx['missingReason'](first_status(sts_, words.get('missingReasonPrecedence')), 'has no accepted result')})"
                      for c_, sts_ in by_check_.items()]
            if parts_:
                sentences.append(bind(words["validationPatchGaps"], {"patch": patch, "parts": "; ".join(parts_)}))
        # the same counts per prediction item, in the items' order, so a reader can be told which items the patch fixes
        def patch_switches_off(e):
            """(blind round 1, Snazzy S1) the setup with the patch has an object of the row off at load (off for good or
            only initially disabled, its whole last version) where the setup without it has it on: for a record field
            the patch sets itself, both mods' versions have the object on; for a geometry clash, either object."""
            rec_c = (words.get("switchOffRecordCheck") or "records.contested-fields")
            if e["kind"] == "LOST-EDIT":
                ch_ = (chains_of(e["checkId"], patched_arm) or {}).get(str(e["formKey"]))
                if not ch_ or not ch_.get("providers"):
                    return False
                low_ = [str(x).lower() for x in ch_["providers"]]
                if str(e["A"]).lower() not in low_ or str(e["B"]).lower() not in low_:
                    return False
                return (version_state(ch_, len(low_) - 1) in ("off", "starts")
                        and version_state(ch_, low_.index(str(e["A"]).lower())) == "on"
                        and version_state(ch_, low_.index(str(e["B"]).lower())) == "on")
            if not str(e["checkId"]).startswith("geometry."):
                return False
            for o_ in [e["formKey"], *(e.get("other") or ())]:
                chw_ = (chains_of(rec_c, patched_arm) or {}).get(str(o_))
                chp_ = (chains_of(rec_c, p_arm) or {}).get(str(o_))
                if (chw_ and chw_.get("providers") and version_state(chw_, len(chw_["providers"]) - 1) in ("off", "starts")
                        and (not chp_ or not chp_.get("providers") or version_state(chp_, len(chp_["providers"]) - 1) == "on")):
                    return True
                # (seventh review round 2, BPT B1) an object the record import does not hold (no contest on it): its
                # last version in each setup read from the plugin files, so either object of the pair is judged alike
                if not (chw_ and chw_.get("providers")):
                    sw_, sp_ = files_state(patched_arm, str(o_)), files_state(p_arm, str(o_))
                    if sw_ == "off" and sp_ == "on":
                        return True
            return False

        files_state_cache = {}

        def files_state(arm_, fk_):
            """'off' (off for good or initially disabled at its last version), 'on', or None where not read."""
            if (arm_, fk_) not in files_state_cache:
                files_state_cache[(arm_, fk_)] = setup_version(arm_rows6_.get(arm_), fk_)
            got_ = files_state_cache[(arm_, fk_)]
            if not got_ or got_[1] is None:
                return None
            v_ = got_[1]
            return "off" if (placement_off_for_good(v_, words) or v_.get("initiallyDisabled")) else "on"

        def item_of(e):
            return (e["checkId"], e.get("itemKind") or e["kind"], tuple(sorted([e["A"], e["B"]], key=str.lower)),
                    e.get("attribution"), e.get("attributionMod"))
        by_item = []
        for g in prediction["items"]:
            k = (g["checkId"], g["kind"], tuple(g["mods"]), g["attribution"], g["attributionMod"])
            by_item.append({"hits": sum(1 for e in all_["hits"] if item_of(e) == k),
                            # (blind round 1, Snazzy S1) resolved / own by the patch switching an object off
                            "hitsOff": sum(1 for e in all_["hits"] if item_of(e) == k and patch_switches_off(e)),
                            "ownOff": sum(1 for e in all_["own"] if item_of(e) == k and patch_switches_off(e)),
                            "switched": sum(1 for e in all_["switched"] if item_of(e) == k),
                            "switchedBy": dict(collections.Counter(f"{e['A']}|{e['B']}" for e in all_["switched"]
                                                                   if item_of(e) == k)),
                            "own": sum(1 for e in all_["own"] if item_of(e) == k),
                            "ownBase": sum(1 for e in all_["own"] if item_of(e) == k and base_put_back(e)),
                            "open": sum(1 for e in all_["open"] if item_of(e) == k),
                            "stands": sum(1 for e in all_["stands"] if item_of(e) == k),
                            # (sixth review, Pilgrim S2, BPT S2) left pairs with a visual-effect model
                            "openEffect": sum(1 for e in all_["open"] if item_of(e) == k and e["kind"] != "LOST-EDIT"
                                              and effect_pair(e["formKey"], e.get("other"))),
                            "kept": sum(1 for e in all_["kept"] if item_of(e) == k),
                            "placed": sum(1 for e in all_["placed"] if item_of(e) == k),
                            # (third review, Winterhold S1) by loser and winner: the patch keeps the winner's value
                            "keptBy": collections.Counter(f"{e['A']}|{e['B']}" for e in all_["kept"] if item_of(e) == k),
                            # (fourth review) each off-for-good count by the losing version's own state
                            "offKeptBy": collections.Counter(f"{e['A']}|{e['B']}|{off_state(e)}" for e in all_["kept"]
                                                             if item_of(e) == k and off_state(e)),
                            # (seventh review, Winterhold S2) the objects those record fields are on
                            "offKeptObjs": sorted({(f"{e['A']}|{e['B']}|{off_state(e)}", str(e["formKey"]))
                                                   for e in all_["kept"] if item_of(e) == k and off_state(e)}),
                            **{k2_: v2_ for n_, x_ in (("Open", "open"), ("Kept", "kept"))
                               for k2_, v2_ in off_keys(f"off{n_}", [e for e in all_[x_] if item_of(e) == k]).items()},
                            "cannotTell": sum(1 for e in all_["untold"] if item_of(e) == k),
                            **{f"open{n[0].upper()}{n[1:]}": sum(1 for e in all_["open"] if item_of(e) == k
                                                                 and left_split.get(id(e)) == n)
                               for n in ("listed", "patchHeld", "notAttributed", "supportRemoved")},
                            "notMeasured": sum(1 for e in not_measured if item_of(e) == k)})
        # a geometry clash between a plugin's object and its master's is counted and labelled (coordinator ruling)
        problem_master = collections.Counter(rk[0] for rk in problems if ident_of.get(rk, tuple(rk[:5])) in master_idents)
        def objects_note(c):
            """The new objects of a check grouped by base: the base's editor ID, its model and the measured gaps."""
            nw_ = words.get("newObjects") or {}
            groups_, movers_ = collections.OrderedDict(), {}
            for fk_, gap_ in (problem_objects.get(c) or {}).items():
                base_ = name_bases.get((name_refs.get(fk_) or {}).get("base") or "") or {}
                if not base_.get("editorId"):
                    return None                # a new object whose base was not read: no partial naming
                groups_.setdefault((base_["editorId"], base_.get("model") or ""), []).append(gap_)
                for m_ in problem_movers.get(fk_) or []:
                    movers_.setdefault((base_["editorId"], base_.get("model") or ""), [])
                    if m_ not in movers_[(base_["editorId"], base_.get("model") or "")]:
                        movers_[(base_["editorId"], base_.get("model") or "")].append(m_)
            if not groups_ or not nw_.get("group"):
                return None
            parts_ = []
            # (fourth review, BPT r2/r3 S5) a gap at the probe's search limit is the limit, not a distance (skill data)
            lim_ = (nw_.get("searchLimit") or {}).get(c)
            for (ed_, model_), gaps_ in groups_.items():
                known_ = [g_ for g_ in gaps_ if g_ is not None and (lim_ is None or g_ < lim_)]
                far_ = sum(1 for g_ in gaps_ if g_ is not None and lim_ is not None and g_ >= lim_)
                gtext_ = (bind(nw_["gaps"], {"gaps": ", ".join(fmt_units(g_) for g_ in sorted(known_, reverse=True))})
                          if known_ else "") + (bind(nw_["gapsBeyond"], {"count": fmt_n(far_), "limit": fmt_units(lim_)})
                                                if far_ and nw_.get("gapsBeyond") else "")
                parts_.append(bind(nw_["group"], {
                    "count": fmt_n(len(gaps_)), "base": ed_, "model": bind(nw_["model"], {"model": model_}) if model_ else "",
                    "gaps": gtext_}) + (bind(nw_["movers"], {"movers": " and ".join(movers_[(ed_, model_)])})
                                        if movers_.get((ed_, model_)) and nw_.get("movers") else ""))
            return "; ".join(parts_)

        def split_note(c):
            """(fifth review, Pilgrim S2) the new alias splits in one unit: actor pairs and the quests they are split in."""
            sp5_ = words.get("splitDetail") or {}
            if c != sp5_.get("check") or not sp5_.get("byCheck"):
                return None
            sums_ = [str(rk[5] or "") for rk in problems if rk[0] == c]
            qs_ = list(dict.fromkeys(m_.group(1) for x_ in sums_ for m_ in [re.search(sp5_.get("scenePattern") or "$^", x_)] if m_))
            ps_ = list(dict.fromkeys(tuple(m_.groups()) for x_ in sums_ for m_ in [re.search(sp5_.get("pairPattern") or "$^", x_)] if m_))
            ps_ = list(dict.fromkeys(tuple(sorted(p_)) for p_ in ps_))
            if not qs_ or not ps_:
                return None
            return bind(sp5_["byCheck"], {"pairs": fmt_n(len(ps_)), "pairNoun": sp5_["pairNoun"][0 if len(ps_) == 1 else 1],
                                          "names": "; ".join(" and ".join(p_) for p_ in ps_), "count": fmt_n(len(qs_)),
                                          "scenes": sp5_["noun"][0 if len(qs_) == 1 else 1],
                                          "quests": ", ".join(f"'{q_}'" for q_ in qs_)})

        def new_in_game(c):
            """(fifth review, BPT S5) the check's in-game meaning, as worth a look in game"""
            ig_ = in_game.get(c)
            # said for the checks whose title does not say what a new problem does in game (skill data, page budget)
            if c not in set(words.get("newByCheckInGameChecks") or []):
                return ""
            if not (ig_ and words.get("newByCheckInGame")):
                return ""
            # the check's meaning up to its first clause, then the hedge (page budget)
            return bind(words["newByCheckInGame"], {"what": ig_.split(";", 1)[0]}) + (words.get("newByCheckCandidate") or "")

        def new_effect(c):
            """(seventh review, Winterhold S1) how many of the check's new pairs have an object whose model is a visual
            effect (the same predicate as the headline's share)"""
            n_ = sum(1 for rk in problems if rk[0] == c and rk[1] != "LOST-EDIT"
                     and effect_pair(rk[4], ((patched_rows.get(rk) or {}).get("row") or {}).get("other")))
            return bind(words["newByCheckEffect"], {"count": fmt_n(n_)}) if n_ and words.get("newByCheckEffect") else ""

        # (ninth review, BPT 79) a check's new objects already counted under another check are said as the same objects
        owners_all_ = problem_owners(problems)

        def same_note(c):
            sh_ = collections.Counter(owners_all_[str(rk[4])] for rk in problems if rk[0] == c and c in by_object
                                      and owners_all_.get(str(rk[4]), c) != c)
            if not sh_ or not words.get("newByCheckSameObjects"):
                return None
            n_ = sum(sh_.values())
            all_ = n_ == sum(1 for rk in problems if rk[0] == c)
            return bind(words["newByCheckSameObjects"][0 if not all_ else 1 if n_ == 1 else 2], {
                "count": fmt_n(n_), "titles": and_list([f'"{title_of(c2_)}"' for c2_ in sorted(sh_, key=title_of)])})
        new_by_check = [{"title": title_of(c), "count": sum(1 for rk in problems if rk[0] == c),
                         "objectsNote": "; ".join(x_ for x_ in (same_note(c), split_note(c) or objects_note(c), new_effect(c),
                                                                new_in_game(c)) if x_) or None,
                         "masterNote": (bind(words["newByCheckOwnMaster"], {"count": fmt_n(problem_master[c])})
                                        if problem_master.get(c) else None),
                         "findingIds": [x for x in dict.fromkeys(fids) if x]}
                        for c, fids in sorted(problem_finding.items(), key=lambda kv: title_of(kv[0]))]
        ow = words["itemOutcomeWords"]

        def outcome_text(bi):
            keys = ("hits", "switched", "own", "kept", "stands", "open", "placed", "cannotTell", "notMeasured")

            def part(k):
                if k == "switched":
                    by_ = bi.get("switchedBy") or {}
                    if sum(by_.values()) != bi.get("switched"):
                        return bind(ow["switchedUnnamed"], {"count": fmt_n(bi.get(k) or 0)})
                    return ", ".join(bind(ow["switched"], {"keeper": k_.split("|")[0], "dropped": k_.split("|")[1],
                                                           "count": fmt_n(n_)})
                                     for k_, n_ in sorted(by_.items(), key=lambda kv: (-kv[1], kv[0].lower())))
                if k == "open":
                    return (bind(ow[k], {"count": fmt_n(bi.get(k) or 0)}) + off_text(off_from(bi, "offOpen"))
                            + (" (" + bind(words["stillThereEffect"], {"count": fmt_n(bi["openEffect"])}) + ")"
                               if bi.get("openEffect") and words.get("stillThereEffect") else ""))
                if k == "own" and bi.get("ownBase") and ow.get("ownBase"):
                    # (round 3) a patch value that is the base game's own is the base game's value put back
                    n_b_, n_o_ = bi["ownBase"], (bi.get("own") or 0) - bi["ownBase"]
                    return ", ".join(x_ for x_ in (
                        bind(ow["own"], {"count": fmt_n(n_o_)}) if n_o_ else "",
                        bind(ow["ownBase"], {"count": fmt_n(n_b_)})) if x_)
                if k in ("hits", "own") and bi.get(f"{k}Off") and ow.get(f"{k}Off"):
                    # (blind round 1, Snazzy S1) how many the patch resolves by switching an object off: its choice
                    return bind(ow[k], {"count": fmt_n(bi.get(k) or 0)}) + bind(ow[f"{k}Off"], {"count": fmt_n(bi[f"{k}Off"])})
                if k != "kept":
                    return bind(ow[k], {"count": fmt_n(bi.get(k) or 0)})
                # whose change stays lost, as without the patch (fourth blind review, W1): never "kept" alone
                by = bi.get("keptBy") or {}
                if sum(by.values()) != bi.get("kept"):
                    return (bind(ow["keptUnnamed"], {"count": fmt_n(bi.get(k) or 0)})
                            + off_text(off_from(bi, "offKept")))
                # each pair's off-for-good count beside its own count (reviewer, BPT 1); the patch keeps the winner's
                # value: the patch's resolution, said as switched is (third review, Winterhold S1)
                return ", ".join(bind(ow["kept"], {"loser": m_.split("|")[0], "winner": m_.split("|")[1],
                                                   "count": fmt_n(n_)})
                                 + off_text(collections.Counter({s_: (bi.get("offKeptBy") or {}).get(f"{m_}|{s_}", 0)
                                                                 for s_ in OFF_STATES}), total=n_,
                                            objs=collections.Counter(s_.rsplit("|", 1)[-1] for s_, _ in
                                                                     {tuple(x_) for x_ in bi.get("offKeptObjs") or []}
                                                                     if s_.rsplit("|", 1)[0] == m_)
                                            if bi.get("offKeptObjs") is not None else None)
                                 for m_, n_ in sorted(by.items(), key=lambda kv: (-kv[1], kv[0].lower())))
            parts = [part(k) for k in keys if bi.get(k)]
            if bi.get("open") and (bi.get("openPatchHeld") or bi.get("openNotAttributed") or bi.get("openSupportRemoved")):
                # the objects still intersect: say how many are still reported between two mods and why the rest
                # are not (W-B2), never a bare "left N" that the detail sections cannot reconcile
                split = [bind(ow["openSplit"][n], {"count": fmt_n(bi[f"open{n[0].upper()}{n[1:]}"])})
                         for n in ("listed", "patchHeld", "notAttributed", "supportRemoved") if bi.get(f"open{n[0].upper()}{n[1:]}")]
                # the off-for-good count goes first, beside the count it is part of (reviewer, BPT 1)
                off_ = off_text(off_from(bi, "offOpen"), wrap=False)
                eff_ = ([bind(words["stillThereEffect"], {"count": fmt_n(bi["openEffect"])})]
                        if bi.get("openEffect") and words.get("stillThereEffect") else [])
                parts[[k for k in keys if bi.get(k)].index("open")] = (
                    bind(ow["open"], {"count": fmt_n(bi["open"])}) + " (" + "; ".join(([off_] if off_ else []) + eff_ + split)
                    + ")")
            return ", ".join(parts) if parts else ow["none"]
        def no_kept_meaning(bi):
            """(fifth review, page budget) a by-item tally without the kept rows' meanings, said once in the kind line"""
            out_ = {k_: v_ for k_, v_ in bi.items() if "_kept" not in k_}
            if isinstance(bi.get("offKeptBy"), dict):
                out_["offKeptBy"] = collections.Counter({k_: v_ for k_, v_ in bi["offKeptBy"].items()
                                                         if not k_.rsplit("|", 1)[-1].startswith("kept")})
            return out_

        item_lines, alone_total = [], collections.Counter()
        for g, bi in zip(prediction["items"], by_item):
            if g.get("shownAs") == "alone" or (multi and g.get("attribution") == "single-mod"):
                # many losing mods in one line: their changes are counted together, said as the losing mod's
                alone_total.update({k: v for k, v in bi.items() if isinstance(v, int)})
                alone_total["count"] += g["count"]
                continue
            from_one = not multi and g.get("attribution") == "single-mod" and g.get("attributionMod")
            item_lines.append(bind(words["validationItemLineFrom" if from_one else "validationItemLine"], {
                "title": g["title"], "modA": (g.get("pairNames") or {}).get(g["mods"][0], g["mods"][0]),
                "modB": (g.get("pairNames") or {}).get(g["mods"][1], g["mods"][1]), "count": fmt_n(g["count"]),
                "mod": g.get("attributionMod"), "other": next((m_ for m_ in g["mods"] if m_.lower() != str(
                    g.get("attributionMod")).lower()), ""), "outcomes": outcome_text(no_kept_meaning(bi))}))
        if alone_total.get("count"):
            item_lines.append(bind(words["validationAloneLine"], {"count": fmt_n(alone_total["count"]),
                                                                  # kept rows' meanings are said in the kind lines (page budget)
                                                                  "outcomes": outcome_text({k_: v_ for k_, v_ in alone_total.items()
                                                                                            if "_kept" not in k_})}))
        # what stays with the patch, said at the top under the prediction (third blind review, W-S6 / S-S5)
        # the clashes the totals divide: every item credit() keeps (the counted ones and those not separated)
        counted_items = [g for g in prediction["items"] if not multi or g.get("attribution") != "single-mod"]
        # each kind in its own unit (blind review of the BPT report: overlap pairs were added to record fields in one
        # total): one line per check and kind with its total and outcomes, then what stays, in plain words
        kind_lines, by_kind = [], collections.OrderedDict()
        for g, bi in zip(prediction["items"], by_item):
            if g in counted_items:
                by_kind.setdefault((g["checkId"], g["kind"]), []).append((g, bi))
        for (check_id, kind), gs in by_kind.items():
            agg = collections.Counter()
            sw_by, kp_by, ok_by = (collections.Counter() for _ in range(3))
            ob_by = []
            for g, bi in gs:
                agg.update({k_: v_ for k_, v_ in bi.items() if isinstance(v_, int)})
                sw_by.update(bi.get("switchedBy") or {})
                kp_by.update(bi.get("keptBy") or {})
                ok_by.update(bi.get("offKeptBy") or {})
                ob_by += list(bi.get("offKeptObjs") or [])
            total = sum(g["count"] for g, _ in gs)
            eff_t_ = sum(int(g.get("effectPairs") or 0) for g, _ in gs)
            kind_lines.append(bind(words["withPatchKindLine"], {
                "title": title_of(check_id), "total": fmt_n(total), "unit": unit_noun(check_id, kind, total)
                + (", " + bind(words["stillThereEffect"], {"count": fmt_n(eff_t_)}) if eff_t_ and words.get("stillThereEffect") else "")
                + (bind(words["kindLineSameObjects"], {"titles": shared_kinds[(check_id, kind)]})
                   if (check_id, kind) in shared_kinds and words.get("kindLineSameObjects") else ""),
                "outcomes": outcome_text({**agg, "switchedBy": dict(sw_by), "keptBy": kp_by,
                                         "offKeptBy": ok_by, "offKeptObjs": ob_by})}))
        stays_parts, back = [], collections.OrderedDict()
        reused_moved, reused_base = collections.OrderedDict(), collections.OrderedDict()
        stay_groups = collections.OrderedDict()
        for g in counted_items:
            k = (g["checkId"], g["kind"], tuple(g["mods"]), g["attribution"], g["attributionMod"])
            stay_groups.setdefault((g["checkId"], g["kind"], tuple(g["mods"])), (g, []))[1].extend(
                e for e in all_["open"] if item_of(e) == k)
        # (third review, Winterhold S1, Snazzy S2) still there is what no patch touches: a value a patch keeps is the
        # patch's resolution (said in the with-patch lines), so neither its fields nor what it brings back are here
        for g, es in stay_groups.values():
            if not es:
                continue
            if g["kind"] == "LOST-EDIT":
                pairs_ = collections.OrderedDict()
                for e in es:
                    pairs_.setdefault((e["A"], e["B"]), []).append(e)
                for (loser, winner), pes_ in pairs_.items():
                    u_ = (words.get("stillThereLostUnits") or {}).get(g["checkId"]) or words["fieldNoun"]
                    # "left" is a lost change no patch touches (Winterhold review 1, BPT review 3)
                    n_open = len(pes_)
                    # (seventh review, BPT r6 S3 line 16) where the winner only carries the value, whose change it is
                    org_ = collections.OrderedDict()
                    for e in pes_:
                        got_ = whose_value(p_of(e["checkId"]), e["checkId"], e["formKey"], e.get("field"), winner)
                        own_ = None if got_ is None else owner_of(got_[0], got_[1], got_[2]["origin"])
                        org_[None if own_ is None or str(own_[1]).lower() == winner.lower() else own_[1]] = 1
                    carried_ = [x for x in org_ if x] if None not in org_ else []
                    text_ = (bind(words["stillThereLost"], {"count": fmt_n(n_open), "loser": loser, "winner": winner,
                                                            "unit": u_[0 if n_open == 1 else 1]})
                             + (bind(words.get("stillThereCarriedBase") if carried_ == [words.get("baseGameOwner") or
                                                                                       "the base game"]
                                     and words.get("stillThereCarriedBase") else words.get("stillThereCarried") or "",
                                     {"winner": winner, "origin": and_list(carried_)})
                                if carried_ else "") + off_note(pes_))
                    # (sixth review, BPT r6 S1) the winner's own removal is never here: its own outcome (removalStands)
                    stays_parts.append((len(pes_), text_))
                for e in es:
                    v_ = e.get("values") or {}
                    if (off_at_load(v_.get("lost")) and not off_at_load(v_.get("winner"))
                            and not side_disables(keyof(e), e["B"]) and not side_starts_off(keyof(e), e["B"])):
                        # brings back is said of the load: the winner's version, by any field, has the object on at
                        # load (Clockwork review S2; a version only initially disabled does not bring it back)
                        # (fourth review, Winterhold S1) an object the winner placed itself (moved across at least its
                        # size, or another base) is the winner's own placement, not the object the loser removed
                        wp_ = winner_placed(keyof(e))
                        if wp_ and wp_.get("across") is not None:
                            reused_moved.setdefault((e["B"], e["A"]), {}).setdefault(str(e["formKey"]), wp_["across"])
                        elif wp_:
                            reused_base.setdefault((e["B"], e["A"]), []).append(str(e["formKey"]))
                        else:
                            back.setdefault((e["B"], e["A"]), []).append(str(e["formKey"]))
                continue
            objs = {str(e["formKey"]) for e in es} | {str(o_) for e in es for o_ in e.get("other") or []}
            places = collections.Counter(located_place(e["formKey"], str((p_rows.get(keyof(e)) or {}).get("row", {}).get("label") or ""))
                                         if (p_rows.get(keyof(e)) or {}).get("row", {}).get("label") else "" for e in es)
            places.pop("", None)
            where_ = []
            if g["checkId"] not in by_object:
                on_ = (words.get("objectNouns") or {}).get(g["checkId"]) or ["object", "objects"]
                where_.append(bind(words["stillThereObjects"], {"count": fmt_n(len(objs)),
                                                                 "noun": on_[0] if len(objs) == 1 else on_[1]}))
            if places:
                pl_, n_pl = places.most_common(1)[0]
                w_key = ("stillThereAllIn" if len(places) == 1 else "stillThereMostIn" if n_pl * 2 > sum(places.values())
                         else "stillThereSomeIn")
                # (third review, Sewers S2) the place count is of the item's unit (pairs, links), said with it
                where_.append(bind(words[w_key], {"place": re.sub(r"\s+\d+ tris -> \d+$", "", pl_),
                                                  "count": fmt_n(n_pl), "more": fmt_n(len(places) - 1),
                                                  "unit": unit_noun(g["checkId"], g["kind"], n_pl)}))
            eff_ = sum(1 for e in es if g["kind"] != "LOST-EDIT" and e.get("other") and effect_pair(e["formKey"], e["other"]))
            if eff_ and words.get("stillThereEffect"):
                # (fifth review, BPT S1) how many of the pairs still there involve a visual-effect model
                where_.append(bind(words["stillThereEffect"], {"count": fmt_n(eff_)}))
            held_ = sum(1 for e in es if patch_places(e))
            if held_ and words.get("stillTherePatchMoves"):
                # a patch places one of the objects and they still cross there (Clockwork review S5)
                where_.append(bind(words["stillTherePatchMoves"], {"count": fmt_n(held_)}))
            stays_parts.append((len(es), bind(words["stillThereGeometry"], {
                "count": fmt_n(len(es)), "unit": unit_noun(g["checkId"], g["kind"], len(es)),
                "modA": (g.get("pairNames") or {}).get(g["mods"][0], g["mods"][0]),
                "modB": (g.get("pairNames") or {}).get(g["mods"][1], g["mods"][1]),
                "where": (" (" + ", ".join(where_) + ")") if where_ else ""})))
        stays_parts.sort(key=lambda x: -x[0])
        limit = int(words.get("stillThereMax") or 3)
        if len(stays_parts) == limit + 1:
            limit += 1                         # one more item is said, never "1 more in 1 other item" (BPT review S5)
        shown = [t_ for _, t_ in stays_parts[:limit]]
        if len(stays_parts) > limit:
            shown.append(bind(words["stillThereMore"][0 if len(stays_parts) - limit == 1 else 1], {"count": fmt_n(sum(x_[0] for x_ in stays_parts[limit:])),
                                                        "items": fmt_n(len(stays_parts) - limit)}))
        if ground_still and words.get("stillThereGround"):
            # navmesh ground still lost with the patches is said at the top too, in its own unit (Clockwork review S4)
            shown.append(bind(words["stillThereGround"], {"parts": "; ".join(
                bind(words["stillThereGroundPart"], {"loser": l_, "winner": w_, "count": fmt_n(n_),
                                                     "noun": "navmesh record" if n_ == 1 else "navmesh records"})
                for l_, w_, n_ in sorted(ground_still, key=lambda x: (-x[2], x[0].lower())))}))
        for (winner, loser), fks in back.items():
            fks = list(dict.fromkeys(fks))
            shown.append(bind(words["stillThereBringsBack"], {
                "winner": winner, "loser": loser, "count": fmt_n(len(fks)),
                "noun": "object" if len(fks) == 1 else "objects", "examples": "; ".join(fks[:2])}))
        for (winner, loser), d_ in reused_moved.items():
            shown.append(bind(words["stillThereMovedOwn"], {
                "winner": winner, "loser": loser, "count": fmt_n(len(d_)), "noun": "object" if len(d_) == 1 else "objects",
                "it": "it" if len(d_) == 1 else "them",
                "examples": "; ".join(f"{fk_} {fmt_units(u_)} away" for fk_, u_ in list(d_.items())[:2])}))
        for (winner, loser), fks in reused_base.items():
            fks = list(dict.fromkeys(fks))
            shown.append(bind(words["stillThereRebased"], {
                "winner": winner, "loser": loser, "count": fmt_n(len(fks)), "noun": "object" if len(fks) == 1 else "objects",
                "examples": "; ".join(fks[:2])}))
        prediction["withPatchHead"] = bind(words["withPatchHead"], {"patch": patch})
        prediction["withPatchLines"] = kind_lines
        prediction["withPatchStays"] = (bind(words["stillThereLine"], {"patch": patch, "parts": "; ".join(shown)})
                                        if shown else None)
        prediction["withPatchLine"] = " ".join([prediction["withPatchHead"], *kind_lines]
                                               + ([prediction["withPatchStays"]] if shown else []))
        # one plain summary of what the patch does, from the same counts as the lines below, never a sentence that
        # undercuts them (Pilgrim review S1; BPT review S2: every check with new problems is named)
        sum_parts = [bind(words["validationSummaryResolved"], {"count": by_unit(hits, True)}) if hits
                     else words["validationSummaryNoneResolved"]]
        if switched:
            sum_parts.append(bind(words["validationSummarySwitched"], {"count": by_unit(switched, True)}))
        if own:
            own_b_ = [e for e in own if base_put_back(e)]
            own_o_ = [e for e in own if not base_put_back(e)]
            if own_o_:
                sum_parts.append(bind(words["validationSummaryOwn"], {"count": by_unit(own_o_, True)}))
            if own_b_ and words.get("validationSummaryOwnBase"):
                sum_parts.append(bind(words["validationSummaryOwnBase"], {"count": by_unit(own_b_, True)}))
        if kept:
            # a patch carrying the winning value is said apart, never as left (Winterhold review 1, BPT review 3)
            sum_parts.append(bind(words["validationSummaryCarried"], {"count": by_unit_note(kept)}))
        if stands:
            # (sixth review, BPT r6 S1) the winner's own removal standing: nothing to patch, never left
            sum_parts.append(bind(words["validationSummaryStands"], {"count": by_unit(stands, True)}))
        if open_:
            sum_parts.append(bind(words["validationSummaryLeft"], {"count": by_unit_note(open_)}))
        if placed:
            # (third review, Clockwork S1) the patch's own placement, never folded into left
            sum_parts.append(bind(words["validationSummaryPlaced"], {"count": by_unit(placed, True)}))
        # (third review, Pilgrim S2) a new clash a setup without the evaluated mod already has is the patch bringing that
        # setup's state back: said apart, never counted against the patch as new
        back_ids = {id(rk_) for rk_, _ in not_new}
        new_rest = [rk_ for rk_ in problems if id(rk_) not in back_ids]
        sum_parts.append(bind(words["validationSummaryNew"], {"count": problems_text(True, new_rest)}) if new_rest
                         else words["validationSummaryNoNewBeyond"] if not_new and words.get("validationSummaryNoNewBeyond")
                         else words["validationSummaryNoNewIncomplete"]
                         if patched_arm in (ctx.get("physicalUnfinished") or ()) and words.get("validationSummaryNoNewIncomplete")
                         else words["validationSummaryNoNew"])
        # (eighth review, Snazzy 63 / 69) record fields the patch undoes, sets to another value or removes beside the
        # predicted clashes are said in the summary with their counts, never left behind "nothing new"
        # (tenth review, BPT S2) the fields the patch undoes, and apart from them the base game's value put back over
        # another plugin's change, with how many of those are fields a predicted clash already counts
        n_over_ = int(spots.get("baseOverChange") or 0) if words.get("validationSummaryBaseOver") else 0
        n_undo_ = len(undone) + int(spots.get("baseOverChange") or 0) - n_over_
        adm_f_ = {(str(e["formKey"]), e.get("field")) for e in record if e["admission"] == "admitted"}
        n_over_seen_ = len({(str(e["formKey"]), e.get("field")) for e in (spot_units.get("baseOverChange") or {}).values()}
                           & adm_f_)
        rec_fx_ = [bind(words[k_], {"count": fmt_n(n_), "noun": words["fieldNoun"][0 if n_ == 1 else 1],
                                    "counted": bind(words.get("validationSummaryBaseOverCounted") or "",
                                                    {"count": fmt_n(n_over_seen_)}) if n_over_seen_ else ""})
                   for k_, n_ in (("validationSummaryUndoes", n_undo_), ("validationSummarySets", len(replaced)),
                                  ("validationSummaryRemoves", int(spots.get("patchRemoves") or 0)),
                                  ("validationSummaryBaseOver", n_over_))
                   if n_ and words.get(k_)]
        if rec_fx_ and words.get("validationSummaryRecordEffects"):
            sum_parts.insert(len(sum_parts) - 1, bind(words["validationSummaryRecordEffects"], {
                "parts": and_list(rec_fx_)}))
        if not_new:
            sum_parts.insert(len(sum_parts) - 1, bind(words["validationSummaryBroughtBack"], {
                "count": problems_text(False, [rk_ for rk_, _ in not_new])}))
        summary_ = bind(words["validationSummary"], {"patch": patch, "parts": ((", ".join(sum_parts[:-1]) + ", and "
                                                                                + sum_parts[-1]) if len(sum_parts) > 1
                                                                               else sum_parts[0])})
        removing_ = patched_arm == "baseline"
        # (eighth review, Pilgrim 116) removing the patch brings back every predicted outcome it changes: resolved, the
        # losing mod's value kept (switched) and the patch's own value
        changed_ = hits + switched + own
        side_lines = ({"noRegressionLine": bind(words["validationRemovingNoRegression"], {
                           "patch": patch, "count": by_unit(changed_, True)}) if changed_ else None,
                       "noChangeLine": bind(words["validationRemovingNoChange"], {
                           "patch": patch, "count": problems_text()}) if problems else
                       bind(words["validationRemovingNoChangeNone"], {"patch": patch}) if hits else None}
                      if removing_ else
                      {"noRegressionLine": None,
                       "noChangeLine": bind(words["validationAddingNoChange"], {
                           "patch": patch, "count": by_unit(hits, True)}) if hits else None})
        # the comparison's own sentence follows where it says more (a new defect, a refused comparison), never the
        # generic "do not establish", which the summary answers
        old_ = str(ctx.get("oldVerdict") or "")
        if ctx.get("genericVerdict"):
            old_ = old_.replace(ctx["genericVerdict"], "").strip()
        # on the page the summary stands for the comparison's sentence, which repeats it, except a refused comparison,
        # which is said beside it (round 4); the sentence stays in the plan
        # each supplied patch measured alone where a setup holds it alone (the setup without the patches plus that one):
        # its own verdict is that setup's, so which patch does what is told, not left joint
        alone_ = {}
        if len(patch_names) > 1:
            for p_ in patch_names:
                a_ = arm_with(set(p_set) | {str(p_).lower()})
                if not a_:
                    continue
                rows_alone_ = []
                for g in prediction["items"]:
                    k = (g["checkId"], g["kind"], tuple(g["mods"]), g["attribution"], g["attributionMod"])
                    es_ = [e for e in admitted if item_of(e) == k]
                    meas_ = [e for e in es_ if (e["checkId"], a_) in measured]
                    left_a_ = sum(1 for e in meas_ if there(a_, keyof(e)))
                    rows_alone_.append({"hits": len(meas_) - left_a_, "left": left_a_, "unknown": len(es_) - len(meas_)})
                alone_[str(p_)] = {"arm": a_, "byItem": rows_alone_}
        validation = {"status": "compared", "summary": summary_, "headline": old_, "alone": alone_,
                      "headlineShown": " ".join(ctx.get("refusedLines") or []),
                      "patches": patch_names, "patchLabels": [patch_labels.get(str(n).lower(), str(n)) for n in patch_names],
                      **side_lines,
                      "itemLines": item_lines, "newByCheck": new_by_check,
                      "newByCheckHeading": words["validationNewByCheck"],
                      "withPatchArm": patched_arm, "withoutPatchArm": p_arm, "byItem": by_item,
                      "patchChoiceNoise": patch_noise, "patchChoiceCarried": patch_carried, "caseOnlyRows": case_only_rows,
                      "hits": len(hits), "switched": len(switched), "own": len(own), "kept": len(kept),
                      "open": len(open_), "stands": len(stands), "undone": len(undone), "undoneBy": dict(undone_by), "replaced": len(replaced),
                      "replacedWhose": {f"{k_[1]}": n_ for k_, n_ in replaced_whose.items()},
                      "undoneByPatch": undone_by_patch_, "undoneOff": len(undone_off), "undoneUnread": len(undone_unread),
                      "undoneSentence": undone_sentence, "replacedSentence": replaced_sentence,
                      "replacedFindingIds": [f_ for f_, _ in collections.Counter(
                          h_["finding"].get("id") for _, h_ in replaced if h_["finding"].get("id")).most_common()],
                      # (Winterhold review 3) the finding holding the objects named as switched back on first, then
                      # the others by how many undone changes they hold
                      "placedSentence": placed_sentence_ or None, "placedPairs": placed_pairs if placed_sentence_ else [],
                      # the one anchor both the report's link and the index's entry are written from
                      "placedAnchor": "patch-placed-pairs" if placed_sentence_ else None,
                      "undoneFindingIds": list(dict.fromkeys(
                          [h_["finding"].get("id") for rk_, h_ in undone
                           if h_["finding"].get("id") and str(rk_[4]) in enable_examples]
                          + [f_ for f_, _ in collections.Counter(h_["finding"].get("id") for _, h_ in undone
                                                                 if h_["finding"].get("id")).most_common()])),
                      "cannotTell": len(untold) + len(untold_value), "valueUnrecorded": len(untold_value),
                      # (eighth review, Snazzy 150) predicted clashes re-measured gone in a comparison that refused its
                      # verdict, by check: its own line says them beside the refusal
                      "measuredGoneRefused": dict(collections.Counter(
                          e["checkId"] for e in judged if e["kind"] != "LOST-EDIT" and outcome.get(id(e)) == "resolved"
                          and ((ctx.get("comparisonState") or {}).get((e["checkId"], diff_arm)) or {}).get("refused"))),
                      "falseAlarms": None,
                      "patchProblems": len(problems) - sum(
                          1 for rk in problems if rk[0] in by_object
                          and problem_owners(problems).get(str(rk[4]), rk[0]) != rk[0]),
                      "notNewWithPatch": len(not_new), "notNewSentence": not_new_sentence,
                      "patchChoices": choices,
                      "notMeasured": len(not_measured), "blindSpots": blind, "noLongerFound": ct_, "sentences": sentences,
                      "otherComparisons": [c for c in comparisons if c["withPatch"] != patched_arm or c["withoutPatch"] != p_arm],
                      "missingFacts": list(words["validationMissingFacts"])}
    headings = {k: bind(v, {"subject": subject}) for k, v in words["headings"].items()}
    # (Missives review 2) a one-sided seam finding whose every boundary touches a cell where the mod undoes its own
    # master's fix is said as that undo in the detail list, never as a clash with the mod owning the neighbouring cell
    sd_ = words.get("seamPutBackDetail")
    for f_ in findings if sd_ and put_cells else []:
        c_ = f_.get("counts") or {}
        if f_.get("kind") != "finding" or state_split(str(c_.get("groupKind") or ""))[0] not in one_sided_res:
            continue
        rows_ = [r_ for r_ in (f_.get("allRows") or f_.get("rows") or []) if isinstance(r_, dict)]
        hits_ = set()
        for r_ in rows_:
            got_ = {put_cells[x_] for x_ in [str(r_.get("formKey")), *map(str, r_.get("other") or ())] if x_ in put_cells}
            hits_ |= got_ if got_ else {None}
        mods_ = {str(m_).lower(): m_ for m_ in (c_.get("A"), c_.get("B")) if m_}
        if rows_ and len(hits_) == 1 and None not in hits_ and next(iter(hits_))[0] in mods_:
            mod_l, master_ = next(iter(hits_))
            n_ = len(rows_)
            f_["readerPlain"] = bind(sd_, {"count": fmt_n(n_), "noun": "boundary" if n_ == 1 else "boundaries",
                                           "mod": mods_[mod_l], "master": master_})
            f_["readerSeamPutBack"] = [mods_[mod_l], master_]   # the folded detail line says it the same way (C928)
    # (Pilgrim review 1) a split of quest aliases is said by scene, with the probe's own distance sentence, naming the
    # plugin that first set the actors' winning placement (the record import's chain of that setup), never as records
    # "between" the probe's plugin pair
    sp_ = words.get("splitDetail") or {}
    for f_ in findings:
        if f_.get("kind") != "finding" or f_.get("checkId") != sp_.get("check"):
            continue
        rows_ = [r_ for r_ in f_.get("rows") or [] if isinstance(r_, dict)]
        scenes_ = list(dict.fromkeys(m_.group(1) for r_ in rows_
                                     for m_ in [re.search(sp_.get("scenePattern") or "$^", str(r_.get("summary") or ""))]
                                     if m_))
        if not scenes_:
            continue
        origins_ = collections.Counter()
        for r_ in rows_:
            ch_ = (chains_of(sp_.get("recordCheck"), f_.get("armId")) or {}).get(str(r_.get("formKey")))
            vals_ = (ch_ or {}).get("values", {}).get(sp_.get("field") or "Placement")
            if not vals_:
                continue
            k_ = next(i_ for i_ in range(len(vals_)) if vals_[i_] == vals_[-1])
            if 0 < k_ < len(vals_) - 1:
                origins_[(ch_["providers"][k_], ch_["providers"][-1])] += 1
        c_ = f_.get("counts") or {}
        ex_ = str(rows_[0].get("summary") or "")
        # (fifth review, Pilgrim S2) one unit: the actor pairs, and the quests (scenes) they are split in, all named
        pairs_ = {frozenset(m_.groups()) for r_ in rows_
                  for m_ in [re.search(sp_.get("pairPattern") or "$^", str(r_.get("summary") or ""))] if m_}
        vals_ = {"count": fmt_n(len(scenes_)), "scenes": sp_["noun"][0 if len(scenes_) == 1 else 1], "example": ex_,
                 "winner": c_.get("B") or "", "pairs": fmt_n(len(pairs_) or 1),
                 "pairNoun": (sp_.get("pairNoun") or ["", ""])[0 if len(pairs_) <= 1 else 1],
                 "quests": ", ".join(f"'{q_}'" for q_ in scenes_)}
        if len(origins_) == 1:
            (origin_, carrier_), _ = origins_.most_common(1)[0]
            f_["readerPlain"] = bind(sp_["lineFrom"], {**vals_, "origin": origin_, "carrier": carrier_})
        else:
            f_["readerPlain"] = bind(sp_["line"], vals_)
    prediction["placeLabels"] = place_labels
    return {"prediction": prediction, "coverage": coverage, "validation": validation, "headings": headings}


def first_status(statuses, precedence) -> str:
    """One status for a check with several: the first by the skill's precedence (a status starting with a listed prefix
    ranks by that prefix; an unlisted one after them), so two reason labels are never glued together."""
    prec = list(precedence or [])

    def rank(st):
        return next((i for i, p in enumerate(prec) if str(st).startswith(p)), len(prec))
    return sorted(statuses, key=lambda st: (rank(st), str(st)))[0]


def is_physical_check(check_id, words: dict) -> bool:
    """A check that judges objects in the world (skill data `physicalFamilies`)."""
    return str(check_id).split(".")[0] in set((words.get("physicalFamilies") or {}).get("families") or [])


def arm_check_state(ev: dict, missing: list, arm_id) -> tuple[set, dict]:
    """(checks with an accepted arm result on the setup, {check: status} of its required checks with
    none), read from the disposition: the setup's own runs, not its comparisons."""
    measured = {str(x.get("checkId")) for x in (ev.get("findings") or []) + (ev.get("completedNullChecks") or [])
                if x.get("armId") == arm_id and x.get("part") in ("arm", None)}
    miss = {}
    for m in missing or []:
        if m.get("armId") == arm_id and m.get("part") in ("arm", None) and m.get("required", True):
            miss.setdefault(str(m.get("checkId")), str(m.get("status")))
    return measured, {c: s for c, s in miss.items() if c not in measured}


def physical_unfinished(ev: dict, missing: list, arm_id, words: dict) -> bool:
    """The setup has a physical check without a result and no physical check with one: nothing it says
    about objects in the world can read as clean."""
    measured, miss = arm_check_state(ev, missing, arm_id)
    return any(is_physical_check(c, words) for c in miss) and not any(is_physical_check(c, words) for c in measured)


def incomplete_line(ev: dict, missing: list, arm_id, label: str, words: dict, heading: str) -> str | None:
    """The scale and cause of the setup's unfinished testing, said first; None when
    every required check of the setup has a result."""
    iw = words.get("incomplete") or {}
    measured, miss = arm_check_state(ev, missing, arm_id)
    if not miss or not iw.get("line"):
        return None
    n_budget = sum(1 for s in miss.values() if s in set(iw.get("budgetStatuses") or []))
    line = bind(iw["line"], {"missing": len(miss), "total": len(set(miss) | measured),
                             "setup": (lambda x: x[:1].lower() + x[1:])(label or "the measured setup"),
                             "budget": bind(iw.get("budget") or "", {"count": n_budget}) if n_budget else ""})
    if physical_unfinished(ev, missing, arm_id, words):
        line += iw.get("physical") or ""
    if iw.get("where") and heading:
        line += " " + bind(iw["where"], {"heading": heading})
    return line


def what_to_do(lead: dict, masters: dict | None, words: dict, ev: dict, missing: list, proper: dict,
               plan_targets: list | None = None) -> list[str]:
    """One verdict for each supplied patch, from the clashes of the pair it is built
    for (its masters), then one next step for each predicted kind still left; the report's box says these sentences and
    the reel's "What to do" page says them without their caveat clauses. A verdict over a setup whose physical checks did not finish says so (G1 x G4)."""
    ww = words.get("whatToDo") or {}
    if not ww:
        return []
    pred, val = lead.get("prediction") or {}, lead.get("validation") or {}
    items = [i for i in pred.get("items") or []]
    by_item = list(val.get("byItem") or [])
    compared = val.get("status") == "compared" and bool(val.get("patches"))
    noun = ww.get("clashNoun") or ["clash", "clashes"]
    name = lambda x: proper.get(str(x).lower(), str(x))  # noqa: E731

    def unknown_of(k):
        bi = by_item[k] if k < len(by_item) else {}
        return int(bi.get("notMeasured") or 0) + int(bi.get("cannotTell") or 0)

    def hits_of(k):
        return int((by_item[k] if k < len(by_item) else {}).get("hits") or 0)

    def left_of(k):
        # still a clash with the patch: left as it was, or the losing mod's change kept lost (never a value the patch sets)
        bi = by_item[k] if k < len(by_item) else {}
        return int(bi.get("open") or 0) + int(bi.get("kept") or 0)

    def kept_of(k):
        # a record field the supplied patch carries with one side's value: the patch author's choice, not a clash left open
        bi = by_item[k] if k < len(by_item) else {}
        return int(bi.get("kept") or 0) if ww.get("kept") else 0
    out = []
    if compared:
        patches = list(val["patches"])
        phys_unknown = physical_unfinished(ev, missing, val.get("withPatchArm"), words)
        new_n = int(val.get("patchProblems") or 0)
        for p in patches:
            pm = None if masters is None else masters.get(str(p).lower())
            if pm is not None:
                # the pair a patch is built for: its masters that are not themselves masters of its other masters (a
                # shared dependency is not one side of the pair), narrowed to the plugins the predicted clashes name
                # where at least two of them are
                pm = {m for m in pm if not any(m in (masters.get(o) or ()) for o in pm if o != m)}
                parties = {str(x).lower() for it in items for x in it.get("mods") or []}
                pm = pm & parties if len(pm & parties) >= 2 else pm
            if pm is None:
                mine = list(range(len(items)))
            else:
                mine = [k for k, it in enumerate(items)
                        if len(it.get("mods") or []) >= 2 and {str(m).lower() for m in it["mods"]} <= set(pm)]
            # a clash the headline counts once, under an earlier check, is not counted again in a patch's tally
            mine = [k for k in mine if not items[k].get("countedElsewhere")]
            count = sum(int(items[k].get("count") or 0) for k in mine)
            hits = sum(hits_of(k) for k in mine)
            unknown = sum(unknown_of(k) for k in mine)
            left = sum(left_of(k) for k in mine)
            al_ = (val.get("alone") or {}).get(str(p))
            if al_ and len(patches) > 1:
                # measured on the setup with this patch alone, every row of its items in one of three counts
                bi_ = al_["byItem"]
                hits = sum(int(bi_[k]["hits"]) for k in mine if k < len(bi_))
                left = sum(int(bi_[k]["left"]) for k in mine if k < len(bi_))
                unknown = sum(int(bi_[k]["unknown"]) for k in mine if k < len(bi_))
                count = hits + left + unknown
            pair = and_list([name(m) for m in sorted(pm or []) if not is_base_game(name(m), words)])
            vals = {"patch": p, "pair": pair, "count": count, "hits": hits, "clashNoun": noun[0 if count == 1 else 1]}
            if pm is None:
                s = bind(ww["noPair"] if hits or not ww.get("noPairNone") else ww["noPairNone"], vals)
            elif not mine:
                s = bind(ww["notItsPair"], vals)
            elif hits:
                kept = 0 if (al_ and len(patches) > 1) else sum(kept_of(k) for k in mine)
                open_ = left - kept
                tail = (bind(ww["left"], {"count": open_, "verb": "is" if open_ == 1 else "are"}) if open_ else "") + (
                    bind(ww["kept"], {"count": kept}) if kept else "") + (
                    bind(ww["unknown"], {"count": unknown}) if unknown else "")
                # the rest of the total, by what the patch does with them, so resolved + left + kept + unknown + rest = total
                rest = count - hits - open_ - kept - unknown
                if rest > 0 and ww.get("rest"):
                    rw_ = ww.get("restKinds") or {}
                    named_ = [(kk_, sum(int((by_item[k] if k < len(by_item) else {}).get(kk_) or 0) for k in mine))
                              for kk_ in rw_]
                    parts_, sum_ = [], 0
                    for kk_, n_ in named_:
                        if n_ and sum_ + n_ <= rest:
                            parts_.append(bind(rw_[kk_], {"count": n_}))
                            sum_ += n_
                    if rest - sum_ and ww.get("restOther"):
                        parts_.append(bind(ww["restOther"], {"count": rest - sum_}))
                    tail += bind(ww["rest"], {"count": rest, "parts": and_list(parts_)})
                s = bind(ww["resolves"], {**vals, "left": tail})
            else:
                s = bind(ww["resolvesNone"], vals)
                if unknown:
                    s = s[:-1] + bind(ww["unknown"], {"count": unknown}) + "."
            # what the patch puts back of another mod's changes, beside its verdict and never as "no new clash" alone
            for u_ in [u for u in val.get("undoneByPatch") or [] if str(u.get("patch")).lower() == str(p).lower()]:
                if ww.get("sideEffect"):
                    s += bind(ww["sideEffect"], {"count": u_["count"], "mod": name(u_["mod"]),
                                                 "noun": (ww.get("sideEffectNoun") or ["field", "fields"])[0 if u_["count"] == 1 else 1],
                                                 "records": and_list(u_["records"][:3]) + (" and others" if len(u_["records"]) > 3 else "")})
            if len(patches) == 1:
                s += (ww["physicalUnknown"] if phys_unknown else
                      bind(ww["newSome"], {"count": new_n, "clashNoun": noun[0 if new_n == 1 else 1]}) if new_n
                      else ww["noNew"])
            elif mine and not (val.get("alone") or {}).get(str(p)):
                s += bind(ww["joint"], {"patches": and_list(patches)})
            elif mine and ww.get("aloneMeasured"):
                s += bind(ww["aloneMeasured"], {"patch": p})
            out.append(s)
        if len(patches) > 1:
            jv = {"patches": and_list(patches), "count": new_n, "clashNoun": noun[0 if new_n == 1 else 1]}
            out.append(bind(ww["physicalUnknownJoint"] if phys_unknown else ww["newSomeJoint"] if new_n
                            else ww["noNewJoint"], jv))
    ns = ww.get("nextStep") or {}
    nm_ = ww.get("nextStepMerged") or {}
    # (item 2) one next step per check and step kind, across the mod pairs it is found between: the pairs of one check
    # that take the same step are said in one line with the summed count (each pair's own count kept in a lost-field
    # line); each pair's item below still lists its own
    steps, order_ = {}, []
    for k, it in enumerate(items):
        if it.get("shownAs") == "alone":
            continue
        n = left_of(k) if compared else int(it.get("count") or 0)
        if n <= 0:
            continue
        kept_n = kept_of(k) if compared else 0
        if kept_n and kept_n == n and ns.get("keptByPatch"):
            # every one left is a field the supplied patch carries with one mod's value: its choice, said as that
            kb_ = (by_item[k] if k < len(by_item) else {}).get("keptBy") or {}
            winners_ = list(dict.fromkeys(str(x).split("|")[1] for x in kb_ if "|" in str(x)))
            step, extra = "keptByPatch", {"winners": winners_}
        else:
            sp_ = it.get("lostSplit") or {}
            own_ = int(sp_.get("own") or 0)
            if sp_ and own_ < n and n == int(it.get("count") or 0) and ns.get("forwardAll") and ns.get("forwardSome"):
                # the winner only keeps an earlier value on n - own of them: forwarding the losing mod's value keeps both
                # mods' changes there (said only where the step's count is the item's whole count, which the split is of)
                step, extra = ("forwardAll" if own_ == 0 else "forwardSome"), {"split": sp_, "own": own_}
            else:
                key_ = next((x for x in (str(it.get("checkId")), str(it.get("kind"))) if ns.get(x)), "default")
                if not ns.get(key_):
                    continue
                step, extra = key_, {}
        gk = (str(it.get("checkId")), step)
        if gk not in steps:
            steps[gk] = []
            order_.append(gk)
        steps[gk].append({"item": it, "n": n, **extra})
    noun_of = lambda key, n: (ns.get(key) or ["", ""])[0 if n == 1 else 1]  # noqa: E731
    still_ = ns.get("still", "") if compared else ns.get("stillNone", "")
    for gk in order_:
        g_ = steps[gk]
        step = gk[1]
        n = sum(x["n"] for x in g_)
        it = g_[0]["item"]
        pairs_ = [[name(m) for m in x["item"].get("mods") or []] for x in g_]
        if len(g_) == 1:
            mods_ = and_list(pairs_[0])
        else:
            # each pair with its own count; the mod every pair shares said once
            common_ = [m for m in pairs_[0] if all(m in p_ for p_ in pairs_)]
            if common_:
                mods_ = and_list([common_[0]] + [and_list([m for m in p_ if m != common_[0]]) + f" ({fmt_n(x['n'])})"
                                                 for p_, x in zip(pairs_, g_)])
            else:
                mods_ = "; ".join(and_list(p_) + f" ({fmt_n(x['n'])})" for p_, x in zip(pairs_, g_))
        base_ = {"title": it.get("title"), "count": n, "verb": "is" if n == 1 else "are", "mods": mods_,
                 "them": "it" if n == 1 else "them", "fieldNoun": noun_of("fieldNoun", n),
                 "candNoun": noun_of("candNoun", n), "still": still_, "pairs": fmt_n(len(g_))}
        if step == "keptByPatch":
            winners_ = list(dict.fromkeys(w for x in g_ for w in x["winners"]))
            out.append(bind(ns["keptByPatch"], {**base_, "winner": and_list([name(w) for w in winners_]) or "one mod"}))
        elif step in ("forwardAll", "forwardSome"):
            win_ = {name(x["split"]["winner"]) for x in g_}
            lose_ = {name(x["split"]["loser"]) for x in g_}
            own_ = sum(x["own"] for x in g_)
            out.append(bind(ns[step], {**base_, "loser": next(iter(lose_)) if len(lose_) == 1 else ns.get("anyLoser", "the losing mod"),
                                       "winner": next(iter(win_)) if len(win_) == 1 else ns.get("anyWinner", "the winning mod"),
                                       "fwd": fmt_n(n - own_), "own": fmt_n(own_),
                                       "every": "it" if n == 1 else "every one"}))
        else:
            # a step that names no pair ("check them in game") says how many pairs it covers; one that names its mods
            # takes the pairs with their counts
            tmpl = nm_[step] if len(g_) > 1 and nm_.get(step) else ns[step]
            out.append(bind(tmpl, {**base_, "state": bind(ns.get("state" if compared else "stateNone") or "",
                                                          {"verb": "is" if n == 1 else "are"})}))
    # (Cut Content r3 report review 3) generated distant scenery and occlusion: the check that would say whether they
    # need rebuilding has no result on the measured setup, and the mod places objects in exterior cells (its own
    # footprint), so the step is said; never for a mod with interior cells only
    rb_ = ns.get("rebuildGenerated") or {}
    if rb_ and any(str(m_.get("checkId")) == rb_.get("check") and m_.get("armId") == (pred.get("predictionArm") or "baseline")
                   for m_ in missing or []):
        for t_ in plan_targets or []:
            ext_ = {str(c_.get("cell")) for c_ in (((t_.get("footprint") or {}).get("placed") or {}).get("cells") or [])
                    if isinstance(c_, dict) and c_.get("kind") in set(rb_.get("cellKinds") or []) and c_.get("worldspace")
                    and any(int(c_.get(k_) or 0) > 0 for k_ in ("refs", "navmesh", "land"))}
            if ext_:
                out.append(bind(rb_["line"], {"mod": name(t_.get("name")), "count": fmt_n(len(ext_)),
                                               "cells": "cell" if len(ext_) == 1 else "cells"}))
    return out


def proper_plugin_case(obj, names):
    """Every plugin name in the reader's text in its load-order spelling (a probe reports the file on disk, whose name may
    differ in case); a path is left as it is."""
    proper = {}
    for n in names:
        proper.setdefault(str(n).lower(), str(n))
    if not proper:
        return obj
    pat = re.compile(r"(?<![\\/\w])(" + "|".join(re.escape(k) for k in sorted(proper, key=len, reverse=True)) + r")(?![\w])",
                     re.IGNORECASE)

    def fix(s):
        return pat.sub(lambda m: proper[m.group(1).lower()], s)

    def walk(x, key=None):
        if isinstance(x, str):
            return x if (key and ("path" in str(key).lower() or str(key).lower() in ("sha256", "id"))) else fix(x)
        if isinstance(x, list):
            return [walk(v, key) for v in x]
        if isinstance(x, dict):
            return {k: walk(v, k) for k, v in x.items()}
        return x
    return walk(obj)


def record_labels(b: "Bundle") -> dict:
    """{form key: what a reader calls the record}, from the names enrichment's own recorded read of the plugin files: the
    record's Name where it has one, else an NPC's or cell's editor ID. {} when the bundle holds no such read."""
    row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    resp = (b.dir / row["path"]).parent / "02-response.jsonl" if row and row.get("path") else None
    out = {}
    if not resp or not resp.is_file():
        return out

    def walk(x):
        if isinstance(x, str) and x[:1] in "{[":
            try:
                walk(json.loads(x))
            except ValueError:
                pass
        elif isinstance(x, list):
            for v in x:
                walk(v)
        elif isinstance(x, dict):
            fk, eid = x.get("formid"), x.get("editorid")
            if isinstance(fk, str) and isinstance(x.get("fields"), list):
                nm = next((f.get("value") for f in x["fields"] if isinstance(f, dict) and f.get("path") == "Name"
                           and isinstance(f.get("value"), str) and f.get("value").strip()), None)
                lab = nm or (eid if str(x.get("type")) in ("Npc", "Cell") and eid else None)
                if lab and fk not in out:
                    out[fk] = str(lab)
            for v in x.values():
                walk(v)
    for line in resp.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            walk(json.loads(line))
        except ValueError:
            continue
    return out


def load_precedent(folder) -> dict:
    """{patch plugin (lower case): its source row} from a precedent cards folder's summary.json; {} when absent."""
    doc = load_json(Path(folder) / "summary.json") if folder else None
    return {str(x.get("patch")).lower(): x for x in (doc or {}).get("sources") or [] if isinstance(x, dict) and x.get("patch")}


def precedent_census(lead: dict, folder, words: dict) -> None:
    """The patch's own edits, counted from its records by the precedent stage, said beside what the checks compared; and
    the stock "not listed" and "not recorded" lines withdrawn where this report lists or counts the edits."""
    val = lead.get("validation") or {}
    if val.get("status") != "compared":
        return
    pc = words.get("precedentCensus") or {}
    prec = load_precedent(folder)
    sentences = list(val.get("sentences") or [])
    lines, carded, cards_out = [], False, []
    for p in val.get("patches") or []:
        src = prec.get(str(p).lower())
        if not src or not pc.get("line"):
            continue
        c = src.get("census") or {}
        kinds = collections.Counter()
        for k, n in (c.get("editKinds") or {}).items():
            kinds["FIELD" if str(k).startswith("FIELD:") else str(k)] += int(n or 0)
        # records are the new ones plus the overrides; the overrides' changes are every edit but an addition, and the
        # named kinds sum to them, the rest said as "other"
        records_, additions_ = int(c.get("patchRecords") or 0), int(c.get("additions") or 0)
        changes_ = sum(n for k, n in kinds.items() if k != "ADD")
        named_ = [(k, n) for k, n in kinds.most_common() if n and k != "ADD" and k in pc["kinds"]]
        rest_ = changes_ - sum(n for _, n in named_)
        parts_ = [bind(pc["kinds"][k], {"count": n}) for k, n in named_]
        if rest_ and pc.get("otherKinds"):
            parts_.append(bind(pc["otherKinds"], {"count": rest_}))
        lines.append(bind(pc["line"], {"patch": p, "records": records_, "additions": additions_,
                                       "overrides": max(0, records_ - additions_), "changes": changes_,
                                       "kinds": and_list(parts_) or "none"}))
        dc = (src.get("cardPass") or {}).get("decidingConstraint") or {}
        given = {k: v for k, v in dc.items() if k != "not-established" and v}
        # the cards themselves, listed in the evidence index; the pointer to them is said only when they are there
        listed_ = precedent_cards(folder, p, pc)
        cards_out += listed_
        key_ = "cards" if listed_ else "cardsUnlisted"
        if given and pc.get(key_):
            carded = True
            lines.append(bind(pc[key_], {"count": sum(given.values()), "reasons": and_list(
                [f"{n} {(pc.get('constraints') or {}).get(k, k)}" for k, n in sorted(given.items(), key=lambda kv: -kv[1])])}))
    listed = any("read from its own records" in str(x) for x in sentences)
    if lines:
        at = next((i for i, x in enumerate(sentences) if "read from its own records" in str(x)), 0)
        sentences[at + 1 if listed else min(1, len(sentences)):at + 1 if listed else min(1, len(sentences))] = lines
    tail_ = pc.get("intentTail") if cards_out else pc.get("intentTailUnlisted")
    if carded and tail_:
        sentences = [str(x).replace(" Whether that is intended is not recorded.", tail_)
                     .replace("; chosen or copied is not recorded.", ".") for x in sentences]
    stock = list(words.get("validationMissingFacts") or [])
    facts = list(val.get("missingFacts") or [])
    if stock and (listed or lines):
        facts = [x for x in facts if x != stock[0]]
    if len(stock) > 1 and carded:
        facts = [x for x in facts if x != stock[1]]
    for k in ("undoneSentence", "replacedSentence"):
        if val.get(k) and carded and tail_:
            val[k] = str(val[k]).replace(" Whether that is intended is not recorded.", tail_)
    val["sentences"], val["missingFacts"], val["censusLines"] = sentences, facts, lines
    val["precedentCards"] = cards_out
    # a patch that switches off another mod's placed object, or removes a record's scripts, says so in its verdict,
    # read from the patch's own records (the precedent stage's edit list, not its model-written cards)
    acts_w = words.get("patchActs") or {}
    todo = list((lead.get("prediction") or {}).get("whatToDo") or [])
    for p in val.get("patches") or []:
        off_, strip_ = patch_acts(folder, p)
        add_ = ""
        for mod_, objs_ in off_.items():
            if acts_w.get("switchesOff"):
                add_ += bind(acts_w["switchesOff"], {"mod": mod_, "objects": and_list(objs_[:3]) + (
                    f" and {len(objs_) - 3} more" if len(objs_) > 3 else ""), "count": fmt_n(len(objs_)),
                    "noun": (acts_w.get("objectNoun") or ["object", "objects"])[0 if len(objs_) == 1 else 1]})
        for mod_, recs_ in strip_.items():
            if acts_w.get("stripsScripts"):
                add_ += bind(acts_w["stripsScripts"], {"mod": mod_, "records": and_list(recs_[:3]) + (
                    f" and {len(recs_) - 3} more" if len(recs_) > 3 else ""), "count": fmt_n(len(recs_)),
                    "noun": (acts_w.get("recordNoun") or ["record", "records"])[0 if len(recs_) == 1 else 1]})
        if not add_:
            continue
        at_ = next((i for i, x in enumerate(todo) if str(x).startswith((str(p), f"Use {p} "))), None)
        if at_ is not None:
            todo[at_] = str(todo[at_]) + add_
    if todo:
        lead["prediction"]["whatToDo"] = todo


def patch_acts(folder, patch) -> tuple[dict, dict]:
    """({mod: objects the patch switches off that the mod places}, {mod: records whose scripts the patch removes}), from
    the precedent stage's edit list of the patch's own records (edits.jsonl); empty when the folder holds none."""
    off, strip = {}, {}
    if not folder:
        return off, strip
    f = Path(folder) / re.sub(r"\.(esp|esm|esl)$", "", str(patch), flags=re.IGNORECASE) / "edits.jsonl"
    if not f.is_file():
        return off, strip
    for line in f.read_text(encoding="utf-8").splitlines():
        try:
            e = json.loads(line)
        except ValueError:
            continue
        fk = str(e.get("formKey") or "")
        owner = fk.split(":", 1)[1] if ":" in fk else ""
        if not owner or owner.lower() == str(patch).lower():
            continue
        # (item 1) each record in words: a placed object by its base, a record by its editor ID; the form key and editor
        # IDs stay in the precedent stage's edit list
        eid = id_words(e.get("editorId")) if e.get("editorId") else fk
        if e.get("kind") == "DISABLE":
            base = ((e.get("touched") or {}).get("base") or {})
            cell = (((e.get("touched") or {}).get("cell") or {}).get("name"))
            what = (f"a placed {id_words(base.get('editorId'))}" + (f" in {cell}" if cell else "") if base.get("editorId")
                    else eid + (f" (in {cell})" if cell else ""))
            off.setdefault(owner, []).append(what)
        elif str(e.get("kind")) == "FIELD:VirtualMachineAdapter":
            pv = ((e.get("values") or {}).get("patch") or {}).get("values") or {}
            if pv and all(v is None for v in pv.values()):
                strip.setdefault(owner, []).append(eid)
    return off, strip


def precedent_cards(folder, patch, pc: dict) -> list[dict]:
    """A patch's precedent cards (its folder's cards.jsonl, named by the plugin without its extension): what the patch
    did, the cell by its name, and the card's reason; [] when the folder holds none."""
    if not folder:
        return []
    f = Path(folder) / re.sub(r"\.(esp|esm|esl)$", "", str(patch), flags=re.IGNORECASE) / "cards.jsonl"
    out = []
    if not f.is_file():
        return out
    for line in f.read_text(encoding="utf-8").splitlines():
        try:
            c = json.loads(line)
        except ValueError:
            continue
        cell = ((c.get("whatItTouched") or {}).get("cell") or {}) if isinstance(c, dict) else {}
        place = (f"{cell['name']} ({cell.get('editorId') or cell.get('formKey')})" if cell.get("name")
                 else cell.get("editorId") or cell.get("formKey"))
        out.append({"patch": str(patch), "what": c.get("whatThePatchDid"), "place": place, "reason": c.get("reason"),
                    "constraint": (pc.get("constraints") or {}).get(c.get("decidingConstraint"), c.get("decidingConstraint"))})
    return out


def patch_declaration(v, source: str) -> dict | None:
    """One patch declaration: {name, sha256, folder, source}, or None for a blank entry. A plugin name alone, a dict
    {name, sha256?, folder?}, or the --declare-patch spellings NAME=sha256:<64 hex> and NAME=folder:<mod folder>.
    Raises InputRefused for a malformed one."""
    if v is None or (isinstance(v, str) and not v.strip()):
        return None
    if isinstance(v, str):
        name, sha, folder = v.strip(), None, None
        for marker in ("=sha256:", "=folder:"):
            if marker in name:
                name, rest = name.split(marker, 1)
                sha, folder = (rest.strip(), None) if marker == "=sha256:" else (None, rest.strip())
                break
    elif isinstance(v, dict):
        name, sha, folder = str(v.get("name") or ""), v.get("sha256"), v.get("folder")
    else:
        raise InputRefused(f"patch-declaration-invalid: the patch declaration {v!r} ({source}) is neither a plugin name "
                           f"nor a {{name, sha256, folder}} object; give the patch's file name")
    name = name.strip()
    if not name:
        raise InputRefused(f"patch-declaration-invalid: the patch declaration {v!r} ({source}) names no plugin; give "
                           f"the patch's file name, with its sha256 or folder when another file has the same name")
    if sha is not None and not (isinstance(sha, str) and re.fullmatch(r"[0-9a-fA-F]{64}", sha.strip())):
        raise InputRefused(f"patch-declaration-invalid: the patch declaration {name} ({source}) gives sha256 {sha!r}; "
                           f"give the file's full 64-character sha256")
    if folder is not None and not (isinstance(folder, str) and folder.strip()):
        raise InputRefused(f"patch-declaration-invalid: the patch declaration {name} ({source}) gives an empty folder; "
                           f"give the mod folder that provides it")
    return {"name": name, "sha256": sha.strip().lower() if sha else None,
            "folder": folder.strip() if folder else None, "source": source}


def copy_folder(row: dict) -> str | None:
    """The mod folder a plan row's copy comes from: an own-view row's layer, else the folder its path sits in."""
    src = str((row or {}).get("hashSource") or "")
    if src.startswith("instance:"):
        return src.split(":", 1)[1]
    path = str((row or {}).get("path") or "")
    parts = [p for p in re.split(r"[\\/]", path) if p]
    return parts[-2] if len(parts) >= 2 else None


def compile_plan(bundle_dir: Path, verify: dict, settings: dict, pr: dict, bundle_dir_as_given: str,
                 location_scopes: dict | None = None, sibling_dir: Path | None = None,
                 sibling_as_given: str | None = None, order_setups: list | None = None,
                 siblings: list | None = None, mod_names: dict | None = None) -> dict:
    """sibling_dir: a verified bundle of the same order without the patch; its single-mod setups stand in for the ones
    this bundle lacks (build_prediction's join). Raises InputRefused when it cannot be joined at all.
    siblings: several such bundles, [(dir, as given)] in the order given; sibling_dir is a list of one.
    order_setups: the record lane's rows measured on the setup without the patch with its plugins in another order
    [{armId, planPath, rowsPath, plugins [{name, sha256}], rows [conflict rows, each with its `_check`]}]."""
    b = Bundle(bundle_dir)
    # the names enrichment, where the bundle carries one: each placed object's base and the base's model, so every
    # section that counts objects leaves out an object with no body (round 4)
    names_row0 = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names_doc0 = load_json(bundle_dir / names_row0["path"]) if names_row0 else None
    body_checks = set(((pr.get("reader") or {}).get("prediction") or {}).get("bodyChecks") or [])
    bodiless_types = set(((pr.get("reader") or {}).get("prediction") or {}).get("bodilessTypes") or [])
    by_object_checks = set(((pr.get("reader") or {}).get("prediction") or {}).get("identityByObject") or [])
    sibling_list = []
    for s_dir, s_given in (list(siblings or []) or ([(sibling_dir, sibling_as_given)] if sibling_dir is not None else [])):
        s_b = Bundle(Path(s_dir))
        s_given = s_given or str(s_dir)
        s_sha = sha256_file(Path(s_dir) / "manifest.json")
        for prev in sibling_list:
            if Path(s_dir).resolve() == prev["bundle"].dir.resolve() or (
                    prev["manifestSha256"] == s_sha and prev["planId"] == s_b.plan.get("planId")):
                raise InputRefused(f"sibling-repeated: the sibling bundle {s_given} is the same evaluation (plan "
                                   f"{s_b.plan.get('planId')}) as an earlier --sibling-bundle, so nothing was joined.")
        sib_plan = compile_plan(Path(s_dir), verify, settings, pr, str(s_dir))
        k_ = len(sibling_list)
        sibling_list.append({"bundle": s_b, "findings": sib_plan.get("findings") or [],
                             "planId": s_b.plan.get("planId"), "manifestSha256": s_sha, "pathAsGiven": s_given,
                             "prefix": "sibling:" if k_ == 0 else f"sibling{k_ + 1}:"})
    sibling = sibling_list[0] if sibling_list else None
    disp = b.disposition
    ev = disp.get("evaluation") or {}
    arms_disp = disp.get("arms") or {}
    plan_arms = {a.get("armId"): a for a in (b.plan.get("arms") or []) if isinstance(a, dict)}
    kinds = {a: (v.get("kind") or plan_arms.get(a, {}).get("kind")) for a, v in arms_disp.items()}
    for a, v in plan_arms.items():
        kinds.setdefault(a, v.get("kind"))
    arm_ids = sorted(set(arms_disp) | set(plan_arms), key=lambda a: arm_sort_key(a, kinds))

    # --- what each alternative setup changes in the measured load order (the baseline arm's requested plugins).
    #     The patch is whatever non-target plugin distinguishes an alternative from the baseline, in EITHER
    #     direction: a candidate may add it, or the baseline may carry it and the candidate remove it. A comparison
    #     sentence names that change, and a setup that removes a plugin is never called the supplied setup.
    base_names = [p.get("name") for p in ((plan_arms.get("baseline") or {}).get("requested") or {}).get("plugins") or []]
    base_set = {n.lower() for n in base_names if n}
    arm_changes, arm_orders = {}, {}
    for arm_id in arm_ids:
        names = [p.get("name") for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []]
        arm_orders[arm_id] = [n for n in names if n]
        added = [n for n in names if n and n.lower() not in base_set] if arm_id != "baseline" else []
        removed = [n for n in base_names if n and n.lower() not in {x.lower() for x in names if x}] if (arm_id != "baseline" and names) else []
        arm_changes[arm_id] = (added, removed)
    # SW2: a plugin is the same plugin only with the same NAME and the same BYTES. An arm can keep every name and still
    # load a different file under one of them (an author's same-named "compatible" variant). A row whose hash is
    # unstated on either side cannot be compared, and is said to be so rather than taken as equal.
    base_rows = {str(p.get("name")).lower(): p for p in
                 ((plan_arms.get("baseline") or {}).get("requested") or {}).get("plugins") or [] if p.get("name")}

    def stated(row):
        sha = str((row or {}).get("sha256") or "")
        return sha.lower() if len(sha) == 64 and (row or {}).get("hashSource") != "unstated" else None
    arm_replaced, arm_unverified = {}, {}
    for arm_id in arm_ids:
        replaced, unverified = [], []
        if arm_id != "baseline":
            for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []:
                b_row = base_rows.get(str(p.get("name") or "").lower())
                if b_row is None:
                    continue
                mine, theirs = stated(p), stated(b_row)
                if mine is None or theirs is None:
                    unverified.append(p.get("name"))
                elif mine != theirs:
                    replaced.append(p.get("name"))
        arm_replaced[arm_id], arm_unverified[arm_id] = replaced, unverified

    def replacement_folder(arm_id, name):
        row = next((p for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []
                    if str(p.get("name") or "").lower() == str(name).lower()), {})
        src = str(row.get("hashSource") or "")
        return src.split(":", 1)[1] if src.startswith("instance:") else None
    base_order = [n.lower() for n in arm_orders.get("baseline") or []]
    plan_target_set = {str(t.get("name")).lower() for t in (b.plan.get("targets") or []) if isinstance(t, dict) and t.get("name")}

    # --- WHAT KIND of comparison each alternative arm is.
    #
    # This distinction is load-bearing and its absence produced two wrong statements. One
    # concept -- "the plugin that differs from baseline" -- was doing three incompatible
    # jobs, so an arm that merely REMOVES content was eligible to be described as resolving
    # conflicts, and any non-target plugin that happened to differ was named as a patch.
    #
    #   patch-comparison    the arm ADDS a non-target plugin. Only this kind can support a
    #                       repair reading, because only here does the alternative contain
    #                       something the reader could install.
    #   dependency-control  the arm removes every target, leaving the shared dependencies. A
    #                       finding surviving here is inherited, not created by the targets.
    #   mod-removal         the arm removes some but not all targets. Fewer conflicts here is
    #                       arithmetic: a setup without a mod does not have that mod's
    #                       conflicts.
    #   same-order-control  the arm's plugins equal the baseline's -- names, order AND bytes --
    #                       a comparability control, not a change.
    #   same-name-replacement  the same names in the same order, but at least one is a different
    #                       file (stated hashes differ): an author's same-named variant, say.
    #   bytes-not-compared  the same names in the same order, but a hash is unstated on one side,
    #                       so whether it is the same file was not checked; never called a control.
    #   mixed               adds and removes at once; described explicitly, never summarised.
    # --- DECLARED patch identity. A patch is declared, never inferred.
    #
    # Inferring it from plugin membership was wrong in both directions: adding any unrelated
    # plugin was read as a patch comparison, and nothing distinguished a patch from a plugin
    # that merely differs. Plugin membership does not identify a patch. Sources, in order:
    # the plan's own `patches` list, an arm's `provenance.patch`/`patches`/`patchPlugins`, and
    # `--declare-patch` at report time for a plan that predates the field. Absent all three,
    # the difference is reported as UNSTATED rather than named as a patch.
    #
    # Item 20: a patch is a FILE, not a name. An author's same-named "compatible" plugin shares its name with the mod
    # it replaces, so a declaration may name the file by sha256 or by the folder that provides it, and then marks only
    # the copies with those bytes / from that folder. A name alone marks every copy of the name, unless the plan loads
    # different files under it (or cannot compare the baseline's copy with another's): then it marks none and the
    # reader is told, so the baseline's copy is never taken for the patch.
    decls_ = []
    for v in (b.plan.get("patches") or []):
        decls_.append(patch_declaration(v, "the plan's patches list"))
    for arm_id_ in arm_ids:
        prov = (plan_arms.get(arm_id_) or {}).get("provenance") or {}
        for key in ("patch", "patches", "patchPlugins"):
            v = prov.get(key)
            for x in ([v] if isinstance(v, (str, dict)) else (v or [])):
                decls_.append(patch_declaration(x, f"setup {arm_id_}'s provenance"))
    for v in (settings.get("declaredPatches") or []):
        decls_.append(patch_declaration(v, "--declare-patch"))
    copies_of = collections.defaultdict(list)
    arms_with_rows = set()
    for arm_id_ in arm_ids:
        for p in ((plan_arms.get(arm_id_) or {}).get("requested") or {}).get("plugins") or []:
            if p.get("name"):
                copies_of[str(p["name"]).lower()].append((arm_id_, p))
                arms_with_rows.add(arm_id_)

    def name_ambiguous(cs) -> bool:
        """Different files under one name, or a baseline copy that cannot be compared with another setup's copy."""
        if len({stated(p) for _, p in cs if stated(p)}) > 1:
            return True
        base_ = [p for a_, p in cs if a_ == "baseline"]
        others_ = [p for a_, p in cs if a_ != "baseline"]
        return bool(base_ and others_ and (stated(base_[0]) is None or any(stated(p) != stated(base_[0]) for p in others_)))

    declared_copies, patch_declarations_out, ambiguous_names = set(), [], []
    for d in (x for x in decls_ if x):
        cs = copies_of.get(d["name"].lower(), [])
        ambiguous = False
        if d["sha256"] or d["folder"]:
            hit = [(a_, p) for a_, p in cs
                   if (not d["sha256"] or stated(p) == d["sha256"])
                   and (not d["folder"] or str(copy_folder(p) or "").lower() == d["folder"].lower())]
            if not hit:
                what = " and ".join(([f"sha256 {d['sha256'][:12]}…"] if d["sha256"] else [])
                                    + ([f"folder {d['folder']}"] if d["folder"] else []))
                unstated_in = [a_ for a_, p in cs if stated(p) is None]
                raise InputRefused(
                    f"patch-declaration-unmatched: the patch declaration {d['name']} with {what} ({d['source']}) "
                    f"matches no plugin any setup of this plan loads; "
                    + (f"the plan's hash of {d['name']} is unstated in {', '.join(unstated_in)}, so that copy cannot be "
                       f"matched: plan the setups with --instance, --game and --profile, or declare the patch by name."
                       if unstated_in else
                       "check the hash or folder against the plan's requested plugins, or declare the patch by name."))
        else:
            ambiguous = name_ambiguous(cs)
            hit = [] if ambiguous else cs
            if ambiguous and d["name"].lower() not in {n.lower() for n, _ in ambiguous_names}:
                ambiguous_names.append((d["name"], len({stated(p) for _, p in cs if stated(p)}) > 1))
        declared_copies |= {(a_, d["name"].lower()) for a_, _ in hit}
        patch_declarations_out.append({"source": d["source"], "name": d["name"], "sha256": d["sha256"],
                                       "folder": d["folder"],
                                       "copies": [{"armId": a_, "sha256": stated(p), "folder": copy_folder(p)}
                                                  for a_, p in hit],
                                       **({"ambiguous": True} if ambiguous else {})})
    # names with at least one declared copy (for the readers that ask of a name, not of a setup's copy)
    declared_patches = {n for _, n in declared_copies}

    def declared_copy(arm_id, name) -> bool:
        """Is this setup's copy of the name a declared patch?"""
        return (arm_id, str(name or "").lower()) in declared_copies

    def replaced_patch_side(arm_id, name):
        """For a name this arm loads as another file than the baseline: which side's copy is the declared patch
        ("arm" or "baseline"), or None when neither or both are."""
        mine, theirs = declared_copy(arm_id, name), declared_copy("baseline", name)
        return "arm" if mine and not theirs else "baseline" if theirs and not mine else None

    def setup_of_rows(f) -> str | None:
        """The setup whose plugins a finding's rows name: a reverse part is the baseline order (evalrun reference_arm)."""
        return "baseline" if f.get("part") == "reverse" else f.get("armId")

    def finding_declared(f, name) -> bool:
        """A setup the plan requests no plugins for (a disposition-only arm, a sibling's) has no copies to ask of, so
        its rows are judged by the name, as before item 20."""
        setup_ = setup_of_rows(f)
        if setup_ in arms_with_rows:
            return declared_copy(setup_, name)
        return str(name or "").lower() in declared_patches

    def arm_comparison_kind(arm_id) -> str:
        if arm_id == "baseline":
            return "baseline"
        added, removed = arm_changes.get(arm_id) or ([], [])
        removed_targets = [n for n in removed if n.lower() in plan_target_set]
        added_targets = [n for n in added if n.lower() in plan_target_set]
        replaced = arm_replaced.get(arm_id) or []
        same_order = [n.lower() for n in arm_orders.get(arm_id) or []] == base_order
        if added and removed:
            return "mixed"
        if not added and not removed:
            # Same MEMBERSHIP is not the same configuration. A load order is ordered, and
            # reordering it is a real tested alternative that can change every winner.
            # Calling it a control asserted an equality that was never checked. Nor is the same
            # NAME the same file (SW2): a same-named replacement is its own comparison.
            if replaced:
                # a replaced file that is a declared patch on exactly one side is that patch, added or removed in place
                if same_order and all(replaced_patch_side(arm_id, n) for n in replaced):
                    return "patch-comparison"
                return "same-name-replacement" if same_order else "mixed"
            if not same_order:
                return "reordered-alternative"
            return "bytes-not-compared" if arm_unverified.get(arm_id) else "same-order-control"
        if replaced:
            # a target's file replaced by its declared patch is the patch moving, not the target (item 20)
            if any(n.lower() in plan_target_set for n in added + removed + [n for n in replaced
                                                                           if not replaced_patch_side(arm_id, n)]):
                return "mixed"
            return ("patch-comparison" if (all(declared_copy(arm_id, n) for n in added)
                                           and all(declared_copy("baseline", n) for n in removed)
                                           and all(replaced_patch_side(arm_id, n) for n in replaced))
                    else "unstated-difference")
        # An evaluated MOD moving decides the kind, whichever direction it moves in. Its
        # companion files travel with it and are collateral, not a patch: a mod's own extra
        # masters differ from the baseline for the same reason the mod does.
        if removed_targets or added_targets:
            if removed_targets and not added:
                return ("dependency-control"
                        if plan_target_set and len(removed_targets) >= len(plan_target_set)
                        else "mod-removal")
            return "mixed"
        # Only NON-target plugins move. That is a patch comparison in either direction ONLY
        # if every plugin that moved is a DECLARED patch. Otherwise the identity is unstated
        # and is reported as such.
        if (added or removed) and (all(declared_copy(arm_id, n) for n in added)
                                   and all(declared_copy("baseline", n) for n in removed)):
            return "patch-comparison"
        return "unstated-difference"

    comparison_kinds = {a: arm_comparison_kind(a) for a in arm_ids}

    # A plugin is "a patch evaluated" only when it is the difference in an arm that is a
    # PATCH COMPARISON. Previously any non-target plugin that differed at all was named as a
    # patch, so a mod's own companion masters -- removed only because the mod they belong to
    # was removed -- were reported to the reader as the patches under evaluation.
    load_position = {n.lower(): i for i, n in enumerate(arm_orders.get("baseline") or [])}
    for arm_id in arm_ids:
        for i, n in enumerate(arm_orders.get(arm_id) or []):
            load_position.setdefault(n.lower(), len(load_position) + i)
    def patch_moves(arm_id) -> list:
        """The declared patch copies this arm moves against the baseline: added (its copy), removed (the baseline's),
        and a same-named file replaced in place where exactly one side's copy is declared -- that one even under a
        target's name (item 20: an author's compatible version of the evaluated mod)."""
        added, removed = arm_changes.get(arm_id) or ([], [])
        return ([n for n in added if declared_copy(arm_id, n) and n.lower() not in plan_target_set]
                + [n for n in removed if declared_copy("baseline", n) and n.lower() not in plan_target_set]
                + [n for n in arm_replaced.get(arm_id) or [] if replaced_patch_side(arm_id, n)])

    supplied = sorted({n
                       for arm_id in arm_changes
                       if comparison_kinds.get(arm_id) == "patch-comparison"
                       for n in patch_moves(arm_id)},
                      key=lambda n: (load_position.get(n.lower(), 10 ** 6), n.lower()))
    # Differences carried by an arm whose patch identity is not declared. Named to the reader
    # as what they are -- a plugin that differs -- and never as an evaluated patch: the moved files that are not
    # declared copies, a replaced same-named file included.
    unstated_difference_plugins = sorted({n
                                          for arm_id, (added, removed) in arm_changes.items()
                                          if comparison_kinds.get(arm_id) == "unstated-difference"
                                          for n in ([x for x in added if not declared_copy(arm_id, x)]
                                                    + [x for x in removed if not declared_copy("baseline", x)]
                                                    + [x for x in arm_replaced.get(arm_id) or []
                                                       if not replaced_patch_side(arm_id, x)])})
    # Declared patches changed by a MIXED arm (one that adds and removes at once). Such an arm
    # evaluates no patch on its own, but the reader must not be told no patch was declared.
    mixed_declared_patches = sorted({n
                                     for arm_id in arm_changes
                                     if comparison_kinds.get(arm_id) == "mixed"
                                     for n in patch_moves(arm_id)
                                     if n not in supplied})
    # A same-named patch is named with the folder it comes from (else its hash): its bare name is also the other file's.
    patch_labels = {}
    for arm_id in arm_changes:
        for n in arm_replaced.get(arm_id) or []:
            side_ = replaced_patch_side(arm_id, n)
            if side_:
                row_ = next((p for p in ((plan_arms.get(arm_id if side_ == "arm" else "baseline") or {}).get("requested")
                                         or {}).get("plugins") or [] if str(p.get("name") or "").lower() == n.lower()), {})
                patch_labels.setdefault(n.lower(), f"{n} from {copy_folder(row_)}" if copy_folder(row_) else
                                        f"{n} (sha256 {str(stated(row_) or '')[:12]})")

    def patch_label(n) -> str:
        return patch_labels.get(str(n).lower(), str(n))
    ambiguous_text = " ".join(
        f"{n} is declared as a patch by name only, but the setups load different files with that name, so neither is "
        f"read as the patch; declare it with its sha256 or its folder to compare it as one." if differ else
        f"{n} is declared as a patch by name only, but the plan could not compare the bytes of its copies of that name, "
        f"so neither is read as the patch; declare it with its sha256 or its folder to compare it as one."
        for n, differ in ambiguous_names)

    def repair_claimable(ids) -> bool:
        """Can a reduction across these arms be read as REPAIR the reader could install?

        Only from an arm that is a DECLARED patch comparison. A reduction from removing a mod
        is arithmetic; a reduction from reordering is a load-order change, not a patch; and a
        reduction from an undeclared plugin cannot be attributed to a patch at all, because
        nothing establishes that the plugin is one.
        """
        return any(comparison_kinds.get(a) == "patch-comparison" for a in ids)

    def non_repair_reason(ids) -> str:
        kinds_here = {comparison_kinds.get(a) for a in ids}
        if kinds_here <= {"dependency-control"}:
            return ("These setups remove every evaluated mod, so what remains is the shared "
                    "dependencies. Fewer conflicts here does not describe a fix; it separates "
                    "what the dependencies already carried from what the mods add.")
        if kinds_here <= {"reordered-alternative"}:
            return ("These setups hold the same plugins in a different load order. A change "
                    "here is a load-order effect, not a patch, and installing nothing would "
                    "reproduce it.")
        if kinds_here <= {"same-name-replacement"}:
            return ("These setups swap a plugin for a different file with the same name, such as an "
                    "author's compatibility version. A change here comes from that file's contents; "
                    "it is not reported as a fix.")
        if kinds_here <= {"bytes-not-compared"}:
            return ("These setups list the same plugins in the same order, but the plan could not state "
                    "the bytes of every one, so they were not compared and the setups are not called "
                    "the same.")
        if kinds_here <= {"unstated-difference"}:
            return ("These setups differ by a plugin that is not declared as a patch, so the "
                    "change cannot be attributed to one. Declare the patch to have this "
                    "reported as a patch comparison.")
        return ("These setups reach that by removing content rather than by patching it: a "
                "setup without a mod does not have that mod's conflicts. They are controls "
                "for attribution, not a configuration to install.")


    def arm_subject(arm_id) -> str:
        added, removed = arm_changes.get(arm_id) or ([], [])
        def own_folder(n):
            row_ = next((p for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []
                         if str(p.get("name") or "").lower() == str(n).lower()), {})
            return copy_folder(row_)
        replaced = [(f"replacing {n} with the patch of the same name" + (f" from {own_folder(n)}" if own_folder(n) else "")
                     if replaced_patch_side(arm_id, n) == "arm" else
                     f"replacing the patch {patch_label(n)} with another file of the same name"
                     + (f" (from {own_folder(n)})" if own_folder(n) else "")
                     if replaced_patch_side(arm_id, n) == "baseline" else
                     f"replacing {n} with another file of the same name"
                     + (f" (from {replacement_folder(arm_id, n)})" if replacement_folder(arm_id, n) else ""))
                    for n in arm_replaced.get(arm_id) or []]
        unverified = ([f"keeping {', '.join(arm_unverified[arm_id])}, whose bytes were not compared"]
                      if not added and not removed and not replaced and arm_unverified.get(arm_id) else [])
        def named(ns, side):
            # more than two declared patches are "the N patches" here; the setups list names every one (P53)
            if len(ns) > 2 and all(declared_copy(side, n) for n in ns):
                return f"the {len(ns)} patches"
            return ", ".join(ns)
        change = " and ".join(([f"adding {named(added, arm_id)}"] if added else [])
                              + ([f"removing {named(removed, 'baseline')}"] if removed else [])
                              + replaced + unverified)
        return change[:1].upper() + change[1:] if change else "The supplied setup"

    def setup_subject(ids) -> str:
        subjects = {arm_subject(a) for a in ids}
        if len(subjects) == 1:
            return subjects.pop()
        return "A tested alternative setup" if any((arm_changes.get(a) or ([], []))[1] for a in ids) else "The supplied setup"

    def lower_first(s: str) -> str:
        return s[:1].lower() + s[1:]

    def subject_values(ids) -> dict:
        # the skill's wording templates take the same subject as {subject} (sentence start) / {subjectLower}
        subject = setup_subject(ids)
        return {"subject": subject, "subjectLower": lower_first(subject)}

    templates = pr.get("templates") or {}
    reader_data = pr.get("reader") or {}
    reader_comparison_templates = reader_data.get("comparisonTemplates") or {}
    reader_verdict_templates = reader_data.get("verdictTemplates") or {}
    overlap_terms = (reader_data.get("kindPhrases") or {}).get("OVERLAP") or {}

    def overlap_candidate_noun(count: int) -> str:
        return (overlap_terms["candidateSingular"] if count == 1 else overlap_terms["candidatePlural"])

    # --- evidence rows: E-<manifest id>, with the receipt's part attached where the manifest lacks it
    evidence = []
    eid_by_path = {}
    for e in b.evidence:
        rid = e.get("recordedBy") or ((e.get("provenance") or {}).get("receiptId"))
        rc = b.receipts.get(rid) if rid else None
        row = {"id": f"E-{e.get('id')}", "kind": e.get("kind"), "path": e.get("path"), "sha256": e.get("sha256"),
               "bytes": e.get("bytes"), "checkId": e.get("checkId") or (rc or {}).get("checkId"),
               "armId": e.get("armId") or (rc or {}).get("armId"), "part": (rc or {}).get("part"),
               "strength": e.get("strength"), "recordedBy": e.get("recordedBy")}
        evidence.append(row)
        eid_by_path[e.get("path")] = row["id"]
    evidence.sort(key=lambda r: (r["path"] or ""))

    def eids_for(check_id, arm_id, part) -> list[str]:
        ids = []
        for e in b.evidence_for(check_id, arm_id, part):
            i = f"E-{e.get('id')}"
            if i not in ids:
                ids.append(i)
        return ids

    # --- media rows (manifest rows whose strength is supplied-media) and unassessed media
    media = []
    for e in b.evidence:
        if e.get("strength") != "supplied-media":
            continue
        sha = e.get("sha256") or ""
        media.append({"id": f"M-{sha[:12]}", "manifestId": e.get("id"), "path": e.get("path"), "sha256": sha,
                      "bytes": e.get("bytes"), "mediaKind": e.get("mediaKind"), "subject": e.get("subject"),
                      "state": e.get("state"), "configurationFingerprint": e.get("configurationFingerprint"),
                      "fingerprintSource": e.get("fingerprintSource"), "provenance": e.get("provenance"),
                      "sourceInterval": e.get("sourceInterval"), "original": e.get("original"),
                      "transformation": e.get("transformation"), "assessed": True, "reason": None})
    unassessed_media = []
    for u in b.manifest.get("unassessedMedia") or []:
        if not isinstance(u, dict):
            continue
        item = u.get("item") if isinstance(u.get("item"), dict) else u
        sha = u.get("sha256") or item.get("sha256") or sha256_text(canonical(u))
        unassessed_media.append({"id": f"M-{sha[:12]}", "manifestId": None, "path": item.get("path") or u.get("path"),
                                 "sha256": u.get("sha256") or item.get("sha256"), "mediaKind": item.get("mediaKind"),
                                 "subject": item.get("subject"), "state": item.get("state"),
                                 "configurationFingerprint": item.get("configurationFingerprint"),
                                 "fingerprintSource": item.get("fingerprintSource"), "provenance": item.get("provenance"),
                                 "sourceInterval": item.get("sourceInterval"), "original": item.get("original"),
                                 "transformation": item.get("transformation"), "assessed": False,
                                 "reason": u.get("reason") or "reason not stated"})
    media.sort(key=lambda m: (m["path"] or ""))
    unassessed_media.sort(key=lambda m: (m["path"] or ""))

    def media_for(check_id, arm_id, form_keys, part=None) -> list[str]:
        """A media item declared for arm X illustrates X's OWN configuration: it binds to X's blocks with part
        null, arm or diff, never to X's reverse part (the baseline order measured with X as --base)."""
        out = []
        if part == "reverse":
            return out
        for m in media:
            s = m.get("subject") or {}
            if s.get("checkId") != check_id or s.get("armId") != arm_id:
                continue
            fk = s.get("formKey")
            if fk and form_keys and fk not in form_keys:
                continue
            out.append(m["id"])
        return out

    # --- groups
    groups: dict[tuple, dict] = {}

    def group_for(check_id, kind, a_, b_) -> dict:
        key = (check_id, kind, a_, b_)
        if key not in groups:
            groups[key] = {"id": None, "title": f"{kind}: {a_} x {b_}", "checkId": check_id,
                           "key": f"{kind}|{a_}|{b_}", "findingIds": [], "rowsTotal": 0}
        return groups[key]

    findings: list[dict] = []
    mode = settings["mode"]
    rows_per_group = settings["rowsPerGroup"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")
    context_only_by_check = ((pr.get("reader") or {}).get("contextOnlyKinds") or {})

    # (round 3, Clockwork re-review B1) a comparison whose sentence names its unit (skill data `regressionUnits`: records
    # with new broken links) counts that unit from the two setups' own rows: the distinct records holding a row one setup
    # lists and the other does not, never the probe's per-group count (which counts (record, target) pairs)
    unit_checks = {k for k in ((pr.get("reader") or {}).get("regressionUnits") or {}) if k != "about"}

    def records_changed(check_id, groups, from_emit, against_emit):
        """The distinct records (formKey) of the rows `from_emit` lists in `groups` ((kind, A, B)) whose identity no group
        of `against_emit` lists, leaving out kinds kept as context only; None where an emit is not read or no such row
        is found though the groups count some (the count is then not in that unit)."""
        if check_id not in unit_checks or not isinstance(from_emit, dict) or not isinstance(against_emit, dict):
            return None
        had = {row_identity(x) for c in against_emit.get("Conflicts") or [] if not context_only(check_id, c.get("Kind"))
               for x in conflict_rows(c, None)}
        keys = {(d["kind"], d["A"], d["B"]) for d in groups}
        got = {x.get("formKey") for c in from_emit.get("Conflicts") or [] if (c.get("Kind"), c.get("A"), c.get("B")) in keys
               for x in conflict_rows(c, None) if x.get("formKey") and row_identity(x) not in had}
        return len(got) if got or not groups else None

    def repaired_objects(check_id, groups, from_emit, against_emit):
        """(fourth review, BPT r3 S3) A by-object check's repairs in the unit of its new problems: (objects with a body,
        objects with no model) of the rows `from_emit` lists in `groups` that `against_emit` no longer lists; None where
        the check is not by object, the emits are not read or no names enrichment says which objects have a body."""
        if (check_id not in by_object_checks or check_id not in body_checks or not isinstance(from_emit, dict)
                or not isinstance(against_emit, dict) or not isinstance(names_doc0, dict)):
            return None
        keys = {(d["kind"], d["A"], d["B"]) for d in groups}
        still = {x.get("formKey") for c in against_emit.get("Conflicts") or [] for x in conflict_rows(c, None)}
        # (fifth review, BPT S6) an object the compared setup no longer lists but whose own carried predicate there still
        # Holds left the class with its problem (the probe's reclassified): no repair
        still |= {str(p_.get("FormKey")) for p_ in against_emit.get("Predicates") or []
                  if isinstance(p_, dict) and p_.get("Physical") == "Holds"
                  and (p_.get("Kind"), p_.get("A"), p_.get("B")) in keys}
        rows_ = [x for c in from_emit.get("Conflicts") or [] if (c.get("Kind"), c.get("A"), c.get("B")) in keys
                 for x in conflict_rows(c, None) if x.get("formKey") and x.get("formKey") not in still]
        if not rows_:
            return None
        bodied_ = {x.get("formKey") for x in rows_ if not row_has_no_body(names_doc0, x, bodiless_types)}
        # (sixth review, Sewers r6 S4) the pairs those bodied objects were listed in, so the unit can be said
        return (len(bodied_), len({x.get("formKey") for x in rows_} - bodied_),
                sum(1 for x in rows_ if x.get("formKey") in bodied_))

    def context_only(check_id, group_kind) -> bool:
        return str(group_kind or "") in set(context_only_by_check.get(str(check_id), []) or [])

    def add(f: dict) -> dict:
        f.setdefault("rows", [])
        f["rowsTotal"] = len(f["rows"])
        if f.get("kind") == "finding" and f.get("checkId") in body_checks:
            # rows whose object (or partner) has no model: not counted on the page, kept in the evidence index
            f["rowsNoBody"] = sum(1 for r in f["rows"] if row_has_no_body(names_doc0, r, bodiless_types))
        f["readerRows"] = f["rowsTotal"] - int(f.get("rowsNoBody") or 0)
        f.setdefault("mediaIds", [])
        f.setdefault("evidenceIds", [])
        f.setdefault("groupId", None)
        findings.append(f)
        return f

    # What each comparison's own record says about it, read once: a verdict the comparison refused (an UNJUDGED
    # verdict, its log's `REFUSED:` line) prints no repaired or created count anywhere; counts the controller did not
    # accept (a `missing` diff row whose comparison was not refused: rows measured on one side only) are printed with
    # that qualifier; rows that left the class with their defect still there are named beside a no-problem line.
    missing_diff = {(m.get("checkId"), m.get("armId")) for m in ev.get("missing") or [] if m.get("part") == "diff"}
    comparison_state = {}
    for arm_id in arm_ids:
        per = (((arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}).get("perProbe") or {})
        for probe in sorted(per):
            check_id = b.exe_to_check.get(probe, probe)
            log_path = b.diff_log_path(check_id, arm_id)
            text = read_text(bundle_dir / log_path) if log_path else ""
            refused_line = next((m.group(1).strip() for m in re.finditer(r"^\s*!\s*REFUSED:\s*(.+)$", text, re.M)), None)
            holding = re.search(r"left the class still holding (\d+)", text)
            verdict_ = str((per[probe] or {}).get("verdict") or "")
            refused = verdict_.endswith("UNJUDGED") or refused_line is not None
            comparison_state[(check_id, arm_id)] = {
                "verdict": verdict_, "refused": refused, "refusedWhy": refused_line,
                "notAccepted": (check_id, arm_id) in missing_diff and not refused,
                "unmeasured": int((per[probe] or {}).get("unmeasured") or 0),
                "stillHolding": int(holding.group(1)) if holding else 0}

    # (1) regressions and (4) repairs, from orderEvaluation verbatim
    for arm_id in arm_ids:
        oe = (arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}
        per = oe.get("perProbe") or {}
        for probe in sorted(per):
            p = per[probe] or {}
            check_id = b.exe_to_check.get(probe, probe)
            counts = {"probe": probe, "classification": oe.get("classification"), **{k: p.get(k) for k in
                      ("verdict", "repaired", "reclassified", "created", "newDefect", "attributed", "unmeasured")}}
            log_path = b.diff_log_path(check_id, arm_id)
            diff_lines = parse_diff_log(read_text(bundle_dir / log_path)) if log_path else []
            disp_row = next((r for r in ev.get("findings") or [] if r.get("checkId") == check_id and
                             r.get("armId") == arm_id and r.get("part") == "diff"), None)
            strength = (disp_row or {}).get("strength") or (b.checks.get(check_id, {}).get("evidence") or {}).get("strength")
            emit_path = b.output_emit_path(check_id, arm_id, "arm")
            emit = b.emit(emit_path) if emit_path else None
            if (p.get("newDefect") or 0) > 0:
                created_groups = [d for d in diff_lines if d["change"] in ("NEW", "GREW") and d.get("created", 0) > 0]
                shrank_groups = [d for d in diff_lines if d["change"] in ("SHRANK", "GONE")]
                rows = []
                reader_form_keys = set()
                group = None
                # the created rows whose object has no model (no body) are not new problems (round 4): counted only
                # where the created rows are known, the group's rows on this setup less those the setup compared with
                # already had, and only when that is exactly the comparison's own count
                known_rows, known_count = [], 0
                base_path = b.output_emit_path(check_id, "baseline", "arm") if arm_id != "baseline" else None
                base_emit = b.emit(base_path) if base_path else None
                for d in created_groups:
                    g = group_for(check_id, d["kind"], d["A"], d["B"])
                    group = group or g
                    created_rows = []
                    if isinstance(emit, dict):
                        for c in emit.get("Conflicts") or []:
                            if (c.get("Kind"), c.get("A"), c.get("B")) == (d["kind"], d["A"], d["B"]):
                                group_rows = conflict_rows(c, eid_by_path.get(emit_path))
                                rows.extend(group_rows)
                                created_rows.extend(group_rows)
                                if not context_only(check_id, d.get("kind")):
                                    reader_form_keys.update(x.get("formKey") for x in group_rows if x.get("formKey"))
                    if (check_id in body_checks and not context_only(check_id, d.get("kind"))
                            and len(created_rows) != int(d.get("created") or 0) and isinstance(base_emit, dict)):
                        had = {(x.get("formKey"), tuple(x.get("other") or ())) for c in base_emit.get("Conflicts") or []
                               if (c.get("Kind"), c.get("A"), c.get("B")) == (d["kind"], d["A"], d["B"])
                               for x in conflict_rows(c, None)}
                        created_rows = [x for x in created_rows if (x.get("formKey"), tuple(x.get("other") or ())) not in had]
                    if (check_id in body_checks and not context_only(check_id, d.get("kind"))
                            and len(created_rows) == int(d.get("created") or 0)):
                        known_rows.extend(created_rows)
                        known_count += len(created_rows)
                # where the created rows are known, a check whose row is a state of its own object counts objects,
                # each once however many rows (and pairs) name it (P64); an object with no body is left out
                def what(x):
                    return (x.get("formKey") if check_id in by_object_checks else
                            (x.get("formKey"), tuple(x.get("other") or ())))
                # an object the compared setup already lists (with any partner) is no new object
                had_objects = ({x.get("formKey") for c in base_emit.get("Conflicts") or [] for x in conflict_rows(c, None)}
                               if check_id in by_object_checks and isinstance(base_emit, dict) else set())
                known_objects = {what(x) for x in known_rows} - had_objects
                bodied = {what(x) for x in known_rows if not row_has_no_body(names_doc0, x, bodiless_types)} - had_objects
                no_body_created = len(known_objects - bodied)
                # (sixth review, Winterhold r6 S1) an object whose support is off for good in the setup compared with too,
                # where it stands on and unmoved, is no new problem: that setup's check does not list it
                pw_ = (pr.get("reader") or {}).get("prediction") or {}
                cmp_arm_ = "baseline" if arm_id != "baseline" else None
                arm_rows6_ = {a_.get("armId"): (a_.get("requested") or {}).get("plugins") or [] for a_ in b.plan.get("arms") or []}
                support_kept = ({what(x) for x in known_rows if what(x) in bodied and x.get("other")
                                 and support_still_removed(arm_rows6_.get(cmp_arm_), arm_rows6_.get(arm_id),
                                                           str(x.get("formKey")), [str(o) for o in x.get("other") or []], pw_)}
                                if check_id in set(pw_.get("supportChecks") or []) and cmp_arm_ else set())
                bodied -= support_kept
                first = created_groups[0] if created_groups else {}
                gated = bool(context_only_by_check.get(str(check_id)))
                reader_groups = [d for d in created_groups if not context_only(check_id, d.get("kind"))]
                reader_shrank = [d for d in shrank_groups if not context_only(check_id, d.get("kind"))]
                reader_created = (sum(int(d.get("created") or 0) for d in reader_groups) - known_count + len(bodied)
                                  if created_groups else (0 if gated else int(p.get("newDefect") or p.get("created") or 0)))
                unit_n = records_changed(check_id, reader_groups, emit, base_emit) if reader_groups else None
                if unit_n is not None:
                    reader_created = unit_n
                reader_repaired = (sum(int(d.get("repaired") or 0) for d in reader_shrank) if shrank_groups
                                   else (0 if gated else int(p.get("repaired") or 0)))
                rep_obj_ = repaired_objects(check_id, reader_shrank, base_emit, emit) if reader_shrank else None
                if rep_obj_ is not None:
                    reader_repaired = rep_obj_[0]
                counts_r = {**counts, "readerRepairedNoBody": rep_obj_[1] if rep_obj_ else 0, "groups": [{"kind": d["kind"], "A": d["A"], "B": d["B"], "created": d.get("created", 0)}
                                                 for d in created_groups], "readerCreated": reader_created,
                            "readerUnitCounted": unit_n is not None,
                            "readerNoBody": no_body_created,
                            "readerSupportRemoved": len(support_kept),
                            "readerRepaired": reader_repaired}
                f = add({"id": None, "kind": "regression", "priority": 1, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get("regression", ""), {**counts, "armId": arm_id,
                                                                            "groupKind": first.get("kind", "conflict")}),
                         "counts": {**counts_r, "diffLines": [d["line"] for d in created_groups]},
                         "evidenceIds": eids_for(check_id, arm_id, "diff") + [x for x in eids_for(check_id, arm_id, "arm")
                                                                             if x not in eids_for(check_id, arm_id, "diff")],
                         "mediaIds": media_for(check_id, arm_id, reader_form_keys),
                         "groupId": group, "rows": rows})
                f["readerRelevant"] = (bool(reader_groups) and reader_created > 0) if created_groups else not gated
                if created_groups and reader_groups and reader_created <= 0:
                    f["readerEvidenceReason"] = "no-body-only"     # every created row names an object with no body
                if not f["readerRelevant"] and gated:
                    f["readerEvidenceReason"] = "context-only-or-inseparable"
                f["_group"] = group
            if (p.get("repaired") or 0) > 0 or (p.get("reclassified") or 0) > 0:
                kind = "repaired" if (p.get("repaired") or 0) > 0 else "reclassified"
                shrank = [d for d in diff_lines if d["change"] in ("SHRANK", "GONE")]
                reader_shrank = [d for d in shrank if not context_only(check_id, d.get("kind"))]
                gated = bool(context_only_by_check.get(str(check_id)))
                reader_repaired = (sum(int(d.get("repaired") or 0) for d in reader_shrank) if shrank
                                   else (0 if gated else int(p.get("repaired") or 0)))
                reader_reclassified = (sum(int(d.get("reclassified") or 0) for d in reader_shrank) if shrank
                                       else (0 if gated else int(p.get("reclassified") or 0)))
                base_path_r = b.output_emit_path(check_id, "baseline", "arm") if arm_id != "baseline" else None
                unit_r = (records_changed(check_id, reader_shrank, b.emit(base_path_r) if base_path_r else None, emit)
                          if reader_shrank else None)
                if unit_r is not None:
                    reader_repaired = unit_r
                rep_obj_ = (repaired_objects(check_id, reader_shrank, b.emit(base_path_r) if base_path_r else None, emit)
                            if reader_shrank else None)
                if rep_obj_ is not None:
                    reader_repaired = rep_obj_[0]
                f = add({"id": None, "kind": kind, "priority": 4, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get(kind, ""), {**counts, "armId": arm_id}),
                         "counts": {**counts, "diffLines": [d["line"] for d in shrank],
                                    # the mods each repaired line is between, so a reader never takes it for another pair
                                    "readerRepairedByPair": [[d.get("A"), d.get("B"), int(d.get("repaired") or 0)]
                                                             for d in reader_shrank if d.get("repaired")],
                                    "readerRepaired": reader_repaired, "readerReclassified": reader_reclassified,
                                    "readerRepairedNoBody": rep_obj_[1] if rep_obj_ else 0,
                                    "readerRepairedPairs": rep_obj_[2] if rep_obj_ else None,
                                    "readerRepairGroupKind": (shrank[0] or {}).get("kind") if shrank else None,
                                    "readerUnitCounted": unit_r is not None},
                         "evidenceIds": eids_for(check_id, arm_id, "diff"), "rows": []})
                f["readerRelevant"] = bool(reader_shrank) if shrank else not gated
                if not f["readerRelevant"] and gated:
                    f["readerEvidenceReason"] = "context-only-or-inseparable"

    # (2) missing, verbatim from the disposition
    for m in ev.get("missing") or []:
        check_id = m.get("checkId")
        add({"id": None, "kind": "missing", "priority": 2, "checkId": check_id, "family": m.get("family") or b.check_family(check_id),
             "armId": m.get("armId"), "part": m.get("part"), "title": b.check_title(check_id), "strength": None,
             "verdict": None, "plain": bind(templates.get("missing", ""), {"armId": m.get("armId"), "status": m.get("status"),
                                                                             "cause": m.get("cause") or ""}),
             "counts": {"status": m.get("status"), "cause": m.get("cause"), "required": m.get("required")},
             "evidenceIds": eids_for(check_id, m.get("armId"), m.get("part")), "rows": []})

    # (3) findings on each arm: one per conflict group per (check, arm, part); a diff part with findings
    #     and no regression row; a non-conflict-file finding as one count
    regression_keys = {(f["checkId"], f["armId"]) for f in findings if f["kind"] == "regression"}
    for r in sorted(ev.get("findings") or [], key=lambda r: (r.get("checkId") or "", arm_sort_key(r.get("armId") or "", kinds),
                                                             PART_ORDER.get(r.get("part"), 9))):
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        oe = ((arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}).get("perProbe") or {}
        exe = (b.checks.get(check_id, {}).get("adapter") or {}).get("exe")
        verdict = r.get("verdict") or (oe.get(exe) or {}).get("verdict")
        base_eids = eids_for(check_id, arm_id, part)
        if part == "diff":
            if (check_id, arm_id) in regression_keys:
                continue
            p = oe.get(exe) or {}
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-diff", ""), {**p, "probe": exe, "armId": arm_id}),
                 "counts": {"findings": r.get("findings"), **p}, "evidenceIds": base_eids, "rows": []})
            if context_only_by_check.get(str(check_id)):
                f["readerRelevant"] = False  # aggregate diff cannot honestly separate context-only kinds
                f["readerEvidenceReason"] = "context-only-or-inseparable"
            continue
        emit_path = b.output_emit_path(check_id, arm_id, part)
        emit = b.emit(emit_path) if emit_path else None
        conflicts = emit.get("Conflicts") if isinstance(emit, dict) else None
        if isinstance(conflicts, list) and not conflicts and r.get("findings"):
            # rows derived from a counted result that hold no row of their own: measured, nothing between mods
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                     "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                     "verdict": verdict, "plain": bind(templates.get("finding-count", ""), {
                         "findings": r.get("findings"), "armId": arm_id, "strength": r.get("strength")}),
                     "counts": {"findings": r.get("findings"), "derivedRows": 0}, "evidenceIds": base_eids, "rows": []})
            f["readerRelevant"] = False
            f["readerEvidenceReason"] = "derived-rows-none"
            continue
        if not isinstance(conflicts, list):
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-count", ""), {"findings": r.get("findings"),
                                                                                       "armId": arm_id, "strength": r.get("strength")}),
                 "counts": {"findings": r.get("findings")}, "evidenceIds": base_eids, "rows": []})
            if context_only_by_check.get(str(check_id)):
                f["readerRelevant"] = False  # count-only result cannot identify which kinds are reader findings
                f["readerEvidenceReason"] = "context-only-or-inseparable"
            continue
        by_key: dict[tuple, list] = collections.OrderedDict()
        for c in conflicts:
            if not isinstance(c, dict):
                continue
            by_key.setdefault((c.get("Kind"), c.get("A"), c.get("B")), []).append(c)
        coverage = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                     "setAside": c.get("SetAside"), "note": c.get("Note")}
                    for c in (emit.get("Coverage") or []) if isinstance(c, dict)]
        ordered = sorted(by_key.items(), key=lambda kv: (-sum(len(c.get("Necessary") or []) or 1 for c in kv[1]),
                                                         kv[0][1] or "", kv[0][2] or "", kv[0][0] or ""))
        for (kind, a_, b_), cs in ordered:
            rows = []
            for c in cs:
                rows.extend(conflict_rows(c, eid_by_path.get(emit_path)))
            rows.sort(key=lambda x: (x.get("formKey") or "", x.get("summary") or ""))
            g = group_for(check_id, kind, a_, b_)
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                     "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                     "verdict": verdict,
                     "plain": bind(templates.get("finding", ""), {"rowsTotal": len(rows), "groupKind": kind, "A": a_, "B": b_,
                                                                  "armId": arm_id, "part": part or "arm",
                                                                  "findingsInPart": r.get("findings"), "strength": r.get("strength")}),
                     "counts": {"groupKind": kind, "A": a_, "B": b_, "conflicts": len(cs), "rows": len(rows),
                                "findingsInPart": r.get("findings"), "coverage": coverage,
                                "population": emit.get("Population"), "state": emit.get("State")},
                     "evidenceIds": base_eids, "mediaIds": media_for(check_id, arm_id, {x["formKey"] for x in rows}, part),
                     "rows": rows})
            f["readerRelevant"] = not context_only(check_id, kind)
            if not f["readerRelevant"]:
                f["readerEvidenceReason"] = "context-only-kind"
            f["_group"] = g

    # (5) completed null checks with their coverage lines from the receipt
    def judged_elsewhere(check_id, cov):
        """(seventh review, Sewers S4) a population of this check whose field another check judges (skill data
        lostEditJudgedBy: a navmesh's geometry, judged by the navmesh check): said, so 'no finding' is not read as none
        lost there."""
        pw_ = (pr.get("reader") or {}).get("prediction") or {}
        out_ = []
        for j_ in pw_.get("lostEditJudgedBy") or []:
            if not isinstance(j_, dict):
                continue
            n_ = next((c_.get("judged") for c_ in cov if str(c_.get("population") or "").endswith(
                f" {j_.get('recordType')} records") and c_.get("judged")), None)
            if n_ and pw_.get("nullJudgedElsewhere"):
                out_.append(" " + bind(pw_["nullJudgedElsewhere"], {
                    "count": n_, "recordType": j_.get("recordType"), "field": j_.get("field"),
                    "title": ((pr.get("reader") or {}).get("checkTitles") or {}).get(j_.get("check")) or j_.get("check")}))
        return "".join(out_)

    for r in ev.get("completedNullChecks") or []:
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        rc = b.receipt_by_key.get((check_id, arm_id, part)) or {}
        cov = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                "setAside": c.get("SetAside"), "note": c.get("Note")}
               for c in ((rc.get("result") or {}).get("coverage") or []) if isinstance(c, dict)]
        add({"id": None, "kind": "null", "priority": 5, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": part, "title": b.check_title(check_id),
             "strength": (rc.get("result") or {}).get("strength"), "verdict": (rc.get("result") or {}).get("verdict"),
             "plain": bind(templates.get("null", ""), {"armId": arm_id, "partText": f" ({part} part)" if part else "",
                                                      "coverage": r.get("coverage")}) + judged_elsewhere(check_id, cov),
             "counts": {"coverage": r.get("coverage"), "coverageLines": cov}, "evidenceIds": eids_for(check_id, arm_id, part),
             "rows": []})

    # (6) catalogues
    advice_by = {(a_.get("checkId"), a_.get("armId")): a_ for a_ in (disp.get("discovery") or {}).get("advice") or []
                 if isinstance(a_, dict)}
    for r in ev.get("completedCatalogues") or []:
        check_id, arm_id = r.get("checkId"), r.get("armId")
        plain_ = bind(templates.get("catalogue", ""), {"count": r.get("count"), "unit": r.get("unit"), "armId": arm_id})
        adv_ = advice_by.get((check_id, arm_id)) or {}
        if (isinstance(adv_.get("rules"), int) and isinstance(adv_.get("clashCount"), int)
                and templates.get("catalogueAdvice")):
            # an author's page states order rules and clashes: said apart, each clash with the mod it names (item 23)
            cl_ = [str(c_.get("namedModName") or c_.get("namedModId") or "") + (f" ({c_['symptom']})" if c_.get("symptom")
                                                                                 else "")
                   for c_ in adv_.get("clashes") or [] if isinstance(c_, dict)]
            plain_ = bind(templates["catalogueAdvice"], {
                "rules": adv_["rules"], "ruleNoun": "rule" if adv_["rules"] == 1 else "rules",
                "clashes": adv_["clashCount"], "clashNoun": "clash" if adv_["clashCount"] == 1 else "clashes",
                "named": (": " + "; ".join(x for x in cl_ if x)) if any(cl_) else "", "armId": arm_id})
        add({"id": None, "kind": "catalogue", "priority": 6, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": r.get("part"), "title": b.check_title(check_id),
             "strength": (b.checks.get(check_id, {}).get("evidence") or {}).get("strength"), "verdict": None,
             "plain": plain_,
             "counts": {"count": r.get("count"), "unit": r.get("unit"), "resultKind": r.get("kind")},
             "evidenceIds": eids_for(check_id, arm_id, r.get("part")), "rows": []})

    # (7) media not assessed, (8) everything under unassessed
    un = disp.get("unassessed") or {}
    for m in unassessed_media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": (m.get("subject") or {}).get("checkId"),
             "family": "runtime", "armId": (m.get("subject") or {}).get("armId"), "part": None, "title": "Media not assessed",
             "strength": None, "verdict": None,
             "plain": bind(templates.get("unassessed-media", ""), {"basename": Path(str(m.get("path") or "")).name or "unnamed",
                                                                   "mediaId": m["id"], "mediaKind": m.get("mediaKind"),
                                                                   "armId": (m.get("subject") or {}).get("armId"), "reason": m.get("reason")}),
             "counts": {"reason": m.get("reason")}, "mediaIds": [m["id"]], "rows": []})
    if not unassessed_media and not media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": None, "family": "runtime",
             "armId": None, "part": None, "title": "Media not assessed", "strength": None, "verdict": None,
             "plain": "No media was supplied to this bundle: no screenshot, render or clip illustrates any finding. "
                      + str(un.get("mediaUnassessed") or ""),
             "counts": {"supplied": 0}, "rows": []})

    def un_add(area, title, text, counts=None, check_id=None, arm_id=None):
        add({"id": None, "kind": "unassessed", "area": area, "priority": 8, "checkId": check_id,
             "family": b.check_family(check_id) if check_id else None, "armId": arm_id, "part": None, "title": title,
             "strength": None, "verdict": None, "plain": bind(templates.get("unassessed", "{text}"), {"text": text}),
             "counts": counts or {}, "evidenceIds": eids_for(check_id, arm_id, None) if check_id and arm_id else [],
             "rows": []})

    states = un.get("statesNotExplored")
    if states:
        un_add("states", "States not explored", "States not explored: " + ", ".join(map(str, states)) + ".",
               {"states": states})
    else:
        un_add("states", "States not explored", "No state beyond those the plan selected was named by the census as "
               "unexplored; the extent of world states is not enumerated by this runner.", {"states": []})
    held = un.get("candidatesHeldBack") or {}
    un_add("candidates", "Candidates held back",
           f"Candidates held back: {fmt_n(held.get('count') or 0)} by --max-candidates; the candidate manifest's own budget "
           f"evaluated {fmt_n((held.get('fromManifestBudget') or {}).get('evaluated') or 0)} and left "
           f"{fmt_n((held.get('fromManifestBudget') or {}).get('legalNotEvaluated') or 0)} legal order(s) not evaluated. "
           + str((held.get("fromManifestBudget") or {}).get("note") or ""), held)
    for u in un.get("unjudgeablePopulations") or []:
        uj = u.get("unjudgeable") or {}
        total = sum(v for v in uj.values() if isinstance(v, (int, float))) if isinstance(uj, dict) else uj
        detail = ", ".join(f"{k} {fmt_n(v)}" for k, v in uj.items()) if isinstance(uj, dict) else str(uj)
        un_add("unjudgeable", "Unjudgeable population",
               bind(templates.get("unjudgeable", ""), {"armId": u.get("armId"), "checkId": u.get("checkId"),
                                                       "judged": u.get("judged"), "population": u.get("population"),
                                                       "unjudgeableTotal": total, "unjudgeableDetail": detail}),
               {"judged": u.get("judged"), "unjudgeable": uj, "population": u.get("population")},
               u.get("checkId"), u.get("armId"))
    fam = un.get("familiesUnsupportedOrManual") or []
    if fam:
        un_add("families", "Families unsupported or manual",
               "Families with checks this runner cannot perform (unsupported, MCP-only or manual): "
               + ", ".join(b.families.get(x, {}).get("title") or x for x in fam) + ".", {"families": fam})
    for s in un.get("scopeUnmeasured") or []:
        un_add("scope", f"Scope extent not measured: {s.get('extent')}",
               f"Not measured ({s.get('extent')}): {s.get('why')}", {"extent": s.get("extent")})
    if un.get("runtimeObservations"):
        un_add("runtime", "Runtime observations", str(un.get("runtimeObservations")), {})

    # --- sort within sections, number findings and groups
    section_of = {}
    for s in pr.get("sections") or []:
        for k in s.get("kinds") or []:
            for area in (s.get("areas") or [None]):
                section_of[(k, area)] = s.get("order", 99)

    def sec(f):
        return section_of.get((f["kind"], f.get("area")), section_of.get((f["kind"], None), 99))

    def count_of(f):
        c = f.get("counts") or {}
        for k in ("newDefect", "created", "rows", "repaired", "reclassified", "findings", "count", "coverage"):
            v = c.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool):
                return v
        return f.get("rowsTotal") or 0

    def group_key(f):
        c = f.get("counts") or {}
        return "|".join(str(c.get(k) or "") for k in ("groupKind", "A", "B"))

    findings.sort(key=lambda f: (sec(f), f.get("checkId") or "", arm_sort_key(f.get("armId") or "", kinds), -count_of(f),
                                 (f.get("rows") or [{}])[0].get("formKey") or "", group_key(f),
                                 f.get("counts", {}).get("status") or "", PART_ORDER.get(f.get("part"), 9), f.get("plain") or ""))
    for i, f in enumerate(findings, 1):
        f["id"] = f"F-{i:03d}"
        f["priority"] = sec(f)
        c = f.get("counts") or {}
        labels = (pr.get("reader") or {}).get("kindLabels") or {}
        group_kind = str(c.get("groupKind") or "")
        kind_phrases = (pr.get("reader") or {}).get("kindPhrases") or {}

        def noun(count, singular, plural=None):
            try:
                one = int(count or 0) == 1
            except (TypeError, ValueError):
                one = False
            return singular if one else (plural or singular + "s")

        count_rows = int(f.get("readerRows", f.get("rowsTotal")) or 0)     # an object with no body is not counted
        phrases = kind_phrases.get(group_kind) or {}
        group_label = (phrases.get("singular") if count_rows == 1 else phrases.get("plural")) or labels.get(
            group_kind, noun(count_rows, "reported conflict"))
        if f["kind"] == "regression":
            new_count = int(c.get("readerCreated") or 0)
            repaired_count = int(c.get("readerRepaired") or 0)
            check_phrase = ((pr.get("reader") or {}).get("regressionCheckPhrases") or {}).get(
                f.get("checkId"), "completed check")
            if f.get("checkId") == "geometry.object-overlap":
                # (Clockwork review 1) only the crossings the probe calls a new defect are new; the others that entered
                # the class cross in both setups and are said apart
                nd_, cr_ = c.get("newDefect"), c.get("created")
                held_ = (int(cr_) - int(nd_) if isinstance(nd_, int) and isinstance(cr_, int) and 0 <= nd_ < cr_
                         else 0)
                new_count = max(0, new_count - held_)
                f["readerPlain"] = bind(reader_comparison_templates["overlapRegression"],
                                        {"newCount": new_count, **subject_values([f.get("armId")]),
                                         "candidateNoun": overlap_candidate_noun(new_count)})
                if held_:
                    f["readerPlain"] += bind(reader_comparison_templates["overlapRegressionHeld"],
                                             {"heldCount": fmt_n(held_)})
                if repaired_count:
                    f["readerPlain"] += bind(reader_comparison_templates["overlapRegressionReduction"],
                                             {"repairedCount": repaired_count,
                                              "candidateNoun": overlap_candidate_noun(repaired_count)})
            else:
                no_body_n = int(c.get("readerNoBody") or 0)
                # a comparison counting in another unit than the prediction says its unit (navmesh: records)
                ru = (((pr.get("reader") or {}).get("regressionUnits") or {}).get(f.get("checkId"))
                      if c.get("readerUnitCounted") else None)
                f["readerPlain"] = (f"{arm_subject(f.get('armId'))} introduced {fmt_n(new_count)} "
                                    + (ru[0 if new_count == 1 else 1] if ru else f"new {noun(new_count, 'problem')}")
                                    + f" in the {check_phrase}"
                                    + (bind((reader_data.get("comparisonState") or {})["noBodyCreated"], {"count": fmt_n(no_body_n)})
                                       if no_body_n else "")
                                    # (sixth review, Winterhold r6 S1) objects whose support is removed in both setups
                                    + (bind((reader_data.get("comparisonState") or {})["supportRemovedCreated"],
                                            {"count": fmt_n(c.get("readerSupportRemoved"))})
                                       if c.get("readerSupportRemoved") else "")
                                    + "." +
                                    (f" It also repaired {fmt_n(repaired_count)} existing {noun(repaired_count, 'problem')}"
                                     + (bind((reader_data.get("comparisonState") or {})["noBodyCreated"],
                                             {"count": fmt_n(c.get("readerRepairedNoBody"))})
                                        if c.get("readerRepairedNoBody") else "")
                                     + f"; those repairs do not cancel the new {noun(new_count, 'problem')}."
                                     if repaired_count else ""))
        elif f["kind"] == "finding" and c.get("A") and c.get("B"):
            sample = next((r.get("label") for r in f.get("rows") or [] if r.get("label")
                           and not (f.get("rowsNoBody") and row_has_no_body(names_doc0, r, bodiless_types))
                           and " tris" not in str(r.get("label")).lower()
                           and not re.match(r"^[0-9a-f]{6}:", str(r.get("label")), re.I)), None)
            if sample:
                # a place printed as a place is a name, never a bare editor ID (N6)
                sample = said_place(sample, (names_doc0 or {}).get("places") or {}, {}, lambda what_, fk_: None)
            unit = phrases.get("unitSingular") if count_rows == 1 else phrases.get("unitPlural")
            if group_kind == "LOST-EDIT" and finding_declared(f, c.get("B")):
                f["readerPlain"] = bind((pr.get("reader") or {}).get("patchChoiceLine") or "{count} {unit}: {patch}", {
                    "count": fmt_n(count_rows), "unit": unit or "record fields", "patch": c.get("B"), "other": c.get("A")})
                f["readerPatchChoice"] = True
            elif unit:
                f["readerPlain"] = (f"{fmt_n(count_rows)} {unit} {'was' if count_rows == 1 else 'were'} reported between {c.get('A')} and {c.get('B')}. "
                                    f"{phrases.get('readerQualification', '')}".strip())
            else:
                verb = "shows" if count_rows == 1 else "show"
                f["readerPlain"] = (f"{fmt_n(count_rows)} {noun(count_rows, 'record')} {verb} {group_label} between "
                                    f"{c.get('A')} and {c.get('B')}" + (f", including {sample}" if sample else "") + ".")
        elif f["kind"] == "finding":
            finding_count = int(c.get("findings") or c.get("created") or count_rows or 0)
            f["readerPlain"] = (f"This check reported {fmt_n(finding_count)} "
                                f"{noun(finding_count, 'finding')} for this setup. The exact result is in the evidence index.")
        elif f["kind"] in ("repaired", "reclassified"):
            repaired_count = int(c.get("readerRepaired") or 0)
            reclassified_count = int(c.get("readerReclassified") or 0)
            if f.get("checkId") == "geometry.object-overlap":
                clauses = []
                if repaired_count:
                    clauses.append(bind(reader_comparison_templates["overlapReduction"],
                                        {"repairedCount": repaired_count, **subject_values([f.get("armId")]),
                                         "candidateNoun": overlap_candidate_noun(repaired_count)}))
                    by_pair_ = c.get("readerRepairedByPair") or []
                    if by_pair_ and reader_comparison_templates.get("reductionPairs"):
                        clauses[-1] = clauses[-1].rstrip(".") + bind(reader_comparison_templates["reductionPairs"], {
                            "pairs": "; ".join(bind(reader_comparison_templates.get("reductionPair") or "{a} and {b}: {count}",
                                                    {"a": a_, "b": b_, "count": fmt_n(n_)}) for a_, b_, n_ in by_pair_)}) + "."
                if reclassified_count:
                    clauses.append(bind(reader_comparison_templates["overlapReclassification"],
                                        {"reclassifiedCount": reclassified_count,
                                         "candidateNoun": overlap_candidate_noun(reclassified_count)}))
                clauses.append(reader_comparison_templates["overlapQualification"])
                f["readerPlain"] = " ".join(clauses)
            else:
                ru_ = (((pr.get("reader") or {}).get("regressionUnitsRepaired") or {}).get(f.get("checkId"))
                       if c.get("readerUnitCounted") else None)
                f["readerPlain"] = (f"{arm_subject(f.get('armId'))} repaired {fmt_n(repaired_count)} existing "
                                    f"{(ru_[0 if repaired_count == 1 else 1]) if ru_ else noun(repaired_count, 'problem')}"
                                    # (sixth review, Sewers r6 S4) a by-object repair names its objects and their pairs
                                    + (bind((reader_data.get("comparisonState") or {})["repairedObjectsPairs"], {
                                        "objects": fmt_n(repaired_count), "objectNoun": noun(repaired_count, "object"),
                                        "pairs": fmt_n(c.get("readerRepairedPairs")),
                                        "unit": ((((pr.get("reader") or {}).get("prediction") or {}).get("byObjectUnits") or {})
                                                 .get(str(f.get("checkId"))) or ["pair", "pairs"])[0 if c.get("readerRepairedPairs") == 1 else 1]})
                                       if (c.get("readerRepairedPairs") or 0) > repaired_count
                                       and (reader_data.get("comparisonState") or {}).get("repairedObjectsPairs") else "")
                                    + (bind((reader_data.get("comparisonState") or {})["noBodyCreated"],
                                            {"count": fmt_n(c.get("readerRepairedNoBody"))})
                                       if c.get("readerRepairedNoBody") else "")
                                    + " and changed which mod owns "
                                    f"{fmt_n(reclassified_count)} still-present {noun(reclassified_count, 'problem')}.")
        elif f["kind"] == "null":
            diff_null = ((pr.get("reader") or {}).get("differentialNullPhrases") or {}).get(f.get("checkId"))
            f["readerPlain"] = (diff_null or "This comparison found no additional problem in the records it examined; "
                                "problems already present in either setup are reported separately."
                                if f.get("part") == "diff" else
                                "This completed check found no problem in the records it examined.")
        elif f["kind"] == "catalogue":
            f["readerPlain"] = f"The check recorded {fmt_n(c.get('count'))} {c.get('unit') or 'item(s)'} for reference."
        else:
            f["readerPlain"] = "Details are recorded in the evidence index."
    cmp_words = reader_data.get("comparisonState") or {}

    def refused_words(state) -> str:
        """A comparison's refusal in words: the share it could not judge, before and after, and the limit."""
        why = str(state.get("refusedWhy") or "")
        m = re.match(r"^(.+?):\s*unjudgeable\s+([\d.]+%)\s*->\s*([\d.]+%).*?more than\s+([\d.]+%)", why)
        if m:
            return bind(cmp_words["refusedShare"], {"population": plain_population(
                m.group(1).strip(), reader_data.get("prediction") or {}), "before": m.group(2),
                                                     "after": m.group(3), "limit": m.group(4)})
        return cmp_words["refusedUnnamed"]

    for f in findings:
        state = comparison_state.get((f.get("checkId"), f.get("armId")))
        if not state or f.get("kind") not in ("regression", "repaired", "reclassified"):
            continue
        if state["refused"]:
            # its counts are not a result: never printed as a repair or a new problem, never counted in a verdict
            f["readerRelevant"] = False
            f["readerEvidenceReason"] = "comparison-refused"
        elif state["notAccepted"]:
            f["readerPlain"] = (f.get("readerPlain") or "").rstrip() + " " + bind(
                cmp_words["notAcceptedQualifier"], {"count": state["unmeasured"],
                                                    "rowNoun": "row" if state["unmeasured"] == 1 else "rows"})

    gi = 0
    for key in sorted(groups):
        gi += 1
        groups[key]["id"] = f"G-{gi:03d}"
    for f in findings:
        g = f.pop("_group", None)
        if g:
            f["groupId"] = g["id"]
            g["findingIds"].append(f["id"])
            g["rowsTotal"] += f["rowsTotal"]

    # --- rows shown and omissions (concise), everything (exhaustive). `allRows` is the complete list the
    #     evidence index renders; `rows` is the shown subset, so len(rows) == rowsShown always.
    omissions = []
    for f in findings:
        f["allRows"] = f["rows"]
        if mode == "exhaustive":
            f["rowsShown"] = f["rowsTotal"]
            continue
        if f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            f["rowsShown"] = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
            continue
        f["rowsShown"] = min(f["rowsTotal"], rows_per_group)
        if f["rowsShown"] < f["rowsTotal"]:
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - f['rowsShown'])} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                              "why": f"concise mode lists {rows_per_group} row(s) per group",
                              "where": f"evidence-index.md#{f['id']}"})
    for f in findings:
        f["rows"] = (f.get("allRows") or [])[: f["rowsShown"]]

    # --- scope
    targets = [{"name": t.get("name"), "sha256": t.get("sha256"), "bytes": t.get("bytes"), "provider": t.get("provider"),
                "providerKind": t.get("providerKind"), "records": (t.get("footprint") or {}).get("records")}
               for t in (b.plan.get("targets") or []) if isinstance(t, dict)]
    target_names_for_scope = [str(t.get("name")) for t in targets if t.get("name")]
    target_set_for_scope = {n.lower() for n in target_names_for_scope}
    evidence_plugins_by_arm = collections.defaultdict(set)
    for f in findings:
        if f.get("part") == "reverse" or f.get("readerRelevant") is False:
            continue
        pair = [str(x) for x in ((f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")) if x]
        if any(x.lower() in target_set_for_scope for x in pair):
            evidence_plugins_by_arm[f.get("armId")].update(pair)
    scope_locations = []
    for target in b.plan.get("targets") or []:
        placed = ((target.get("footprint") or {}).get("placed") or {}) if isinstance(target, dict) else {}
        for cell in placed.get("cells") or []:
            if not isinstance(cell, dict):
                continue
            if cell.get("worldspace") and cell.get("x") is not None and cell.get("y") is not None:
                label = f"{cell['worldspace']} ({cell['x']}, {cell['y']})"
            else:
                label = cell.get("name") or cell.get("cell")
            if label and label not in scope_locations:
                scope_locations.append(label)
    arms_out = []
    for arm_id in arm_ids:
        pa = plan_arms.get(arm_id) or {}
        da = arms_disp.get(arm_id) or {}
        req = pa.get("requested") or {}
        names = [p.get("name") for p in req.get("plugins") or []]
        added, removed = arm_changes[arm_id]
        unstated = [{"name": p.get("name"), "unstatedReason": p.get("unstatedReason")}
                    for p in req.get("plugins") or [] if p.get("hashSource") == "unstated"]
        limits = 0
        for rc in b.receipts.values():
            if rc.get("armId") == arm_id and (rc.get("validation") or {}).get("accepted"):
                limits += len((rc.get("validation") or {}).get("identityLimits") or [])
        added_label = ", ".join(added)
        display_label = ("Measured load order" if arm_id == "baseline" else
                         (("Setup including supplied patch" if kinds.get(arm_id) == "supplied-combination" else "Tested alternative setup") +
                          (f": {added_label}" if added_label else "")))
        payload = (da.get("payload") or pa.get("payload") or {})
        setup_limits = []
        if payload.get("espOnly"):
            setup_limits.append("Only the plugin was included; its meshes, scripts and installer choices were not tested.")
        supplied_location_scope = ((location_scopes or {}).get(arm_id) or {}).get("locationContributors") or pa.get("locationContributors") or {}
        supplied_locations = supplied_location_scope.get("locations") or []
        supplied_contributors = [p for loc in supplied_locations if isinstance(loc, dict)
                                  for p in loc.get("plugins") or []]
        selected_plugins = {str(p.get("name")).lower() for p in supplied_contributors
                            if isinstance(p, dict) and p.get("name")}
        location_basis = "measured per-location contributors"
        location_complete = bool(supplied_location_scope.get("complete"))
        location_missing = supplied_location_scope.get("missingInput")
        if not location_missing and supplied_location_scope.get("missing"):
            location_missing = "; ".join(str(x) for x in supplied_location_scope.get("missing") or [])
        if not location_missing and supplied_location_scope.get("limits"):
            location_missing = " ".join(str(x) for x in supplied_location_scope.get("limits") or [])
        if not selected_plugins:
            selected_plugins = {n.lower() for n in evidence_plugins_by_arm.get(arm_id, set()) if n}
            location_basis = "plugins named by accepted target-conflict evidence"
            location_complete = False
            # the fallback list is only the target's recorded conflict partners, so it says so, and names a compared
            # patch this setup loads that the list leaves out rather than let the list read as the setup's order
            location_missing = ("This list names only plugins that appear in recorded conflicts involving the target mods. "
                                + (location_missing or
                                   "This bundle does not enumerate every plugin that changes the listed cells or worldspace coordinates.")
                                + "".join(f" This setup also loads {n}, which is not listed." for n in names
                                          if n in supplied and n.lower() not in selected_plugins))
        affecting_subset = [n for n in names if n and n.lower() in selected_plugins]
        arms_out.append({"armId": arm_id, "kind": kinds.get(arm_id), "displayLabel": display_label,
                         "candidateFile": da.get("candidateFile") or pa.get("candidateFile"),
                         "pluginsAdded": added, "pluginsRemoved": removed,
                         "payloadNote": (da.get("payload") or pa.get("payload") or {}).get("note"),
                         "espOnly": (da.get("payload") or pa.get("payload") or {}).get("espOnly"),
                         "loadedInGameNote": da.get("loadedInGameNote") or pa.get("loadedInGameNote"),
                         "readerLimits": setup_limits,
                         "locationPluginOrder": affecting_subset,
                         # (Missives r3 reel review, finding 6) the setup's whole load order, for the reel's plugin page
                         "plugins": [n for n in names if n],
                         "locationPluginScope": {"basis": location_basis, "complete": location_complete,
                                                 "missingInput": location_missing,
                                                 "locations": [((f"{x.get('worldspace')} ({x.get('x')}, {x.get('y')})"
                                                                  if x.get('worldspace') and x.get('x') is not None and x.get('y') is not None
                                                                  else x.get('name') or x.get('cell')))
                                                               for x in supplied_locations if isinstance(x, dict)] or scope_locations},
                         "orderEvaluation": da.get("orderEvaluation"),
                         "identity": {"pluginCount": len(names), "unstatedRows": unstated, "identityLimits": limits}})
    # Setups that differ only by a declared patch load the same plugins at these locations: one without its own
    # measured list shows the measured setup's list, with the patch where it loads, and says so (blind reviews of the
    # Pilgrim and Sewers reports: the fallback list of conflict partners read as a different load order). The measured
    # list names any plugin the findings name that it does not cover.
    plugins_of_arm = {aid: [p.get("name") for p in ((plan_arms.get(aid) or {}).get("requested") or {}).get("plugins") or []]
                      for aid in arm_ids}
    measured_ = next((a for a in arms_out if a["locationPluginScope"]["basis"] == "measured per-location contributors"), None)
    if measured_ is not None:
        m_names = plugins_of_arm.get(measured_["armId"]) or []
        m_low = {str(n).lower() for n in m_names}
        m_list = {str(n).lower() for n in measured_["locationPluginOrder"]}
        uncovered = [n for n in m_names if n and n.lower() in {x.lower() for x in evidence_plugins_by_arm.get(measured_["armId"], set()) if x}
                     and n.lower() not in m_list]
        if uncovered:
            # (fifth review, Missives line 107) the evidence names them, set-aside rows included: never "the findings above"
            measured_["locationPluginScope"]["note"] = ("The evidence index also names " + ", ".join(uncovered)
                                                        + ", at locations this list does not cover.")
        for a in arms_out:
            if a is measured_ or a["locationPluginScope"]["basis"] == "measured per-location contributors":
                continue
            a_names = plugins_of_arm.get(a["armId"]) or []
            diff_ = {str(n).lower() for n in a_names} ^ m_low
            if not diff_ or not diff_ <= {str(x).lower() for x in supplied}:
                continue
            extra_ = [n for n in a_names if n and n.lower() in diff_]
            gone_ = [n for n in m_names if n and n.lower() in diff_]
            a["locationPluginOrder"] = [n for n in a_names if n and (n.lower() in m_list or n.lower() in diff_)]
            a["sameListAs"] = measured_["displayLabel"]      # said in one sentence, not printed again (page budget)
            a["locationPluginScope"] = dict(measured_["locationPluginScope"], complete=False, note=None,
                                            basis="the measured load order's per-location contributors",
                                            missingInput=("This setup loads the same plugins as the measured load order"
                                                          + (", with " + ", ".join(extra_) if extra_ else "")
                                                          + (", without " + ", ".join(gone_) if gone_ else "")
                                                          + ", so its list is the one shown for the measured load order"
                                                          + (" with " + ", ".join(extra_) + " where it loads; whether "
                                                             + ", ".join(extra_) + " changes these locations was not measured."
                                                             if extra_ else ".")))
    # every setup with its own measured list says the plugins it loads that this report names (a clash party, a patch the
    # setup adds) and its list leaves out, so a list of locations never reads as the whole order of the setup
    for a in arms_out:
        if (a is measured_ or a["locationPluginScope"].get("basis") != "measured per-location contributors"
                or a["locationPluginScope"].get("note")):
            continue
        a_list_ = {str(n).lower() for n in a.get("locationPluginOrder") or []}
        named_ = {x.lower() for x in evidence_plugins_by_arm.get(a["armId"], set()) if x} | {
            str(x).lower() for x in a.get("pluginsAdded") or []}
        unc_ = [n for n in plugins_of_arm.get(a["armId"]) or [] if n and n.lower() in named_ and n.lower() not in a_list_]
        if unc_:
            a["locationPluginScope"]["note"] = ("The evidence index also names " + ", ".join(unc_)
                                                + ", at locations this list does not cover.")
    # Several alternatives can share one display label (every removal-only arm is "Tested alternative setup").
    # A per-setup sentence then names WHICH one in the setups-compared list's own words, so a result on one
    # alternative is never read as another's and no-problem lines are never merged across them. A label no other
    # arm carries is kept as it is. Arms whose changes are also identical are numbered in the order the setups-compared
    # list shows them ("2 of 2 setups with this change"), and that list's row carries the same words, so the label
    # still points at one row without exposing the technical setup id.
    base_order_ = next((list(a_.get("plugins") or []) for a_ in arms_out if a_.get("armId") == "baseline"), [])

    def order_words(arm) -> str:
        """A setup with the measured plugins in another order: the first two whose order is swapped, in words."""
        if arm.get("pluginsAdded") or arm.get("pluginsRemoved") or arm.get("armId") == "baseline":
            return ""
        mine_ = [str(x) for x in arm.get("plugins") or []]
        if sorted(x.lower() for x in mine_) != sorted(str(x).lower() for x in base_order_) or mine_ == base_order_:
            return ""
        pos_ = {str(x).lower(): i for i, x in enumerate(base_order_)}
        for i_, x_ in enumerate(mine_):
            for y_ in mine_[i_ + 1:]:
                if pos_.get(x_.lower(), 0) > pos_.get(y_.lower(), 0):
                    return bind((reader_data.get("prediction") or {}).get("orderSwapped") or "loads {first} before {second}",
                                {"first": x_, "second": y_})
        return ""

    def change_words(arm) -> str:
        return "; ".join(([f"adds {', '.join(arm['pluginsAdded'])}"] if arm.get("pluginsAdded") else []) +
                         ([f"removes {', '.join(arm['pluginsRemoved'])}"] if arm.get("pluginsRemoved") else []) +
                         ([order_words(arm)] if order_words(arm) else []))

    for a in arms_out:
        if order_words(a):
            a["orderChange"] = order_words(a)
    shared_labels = collections.Counter(a["displayLabel"] for a in arms_out)
    for a in arms_out:
        # a setup is always named by what it changes when its label alone does not say it (an order-only setup)
        words = change_words(a) if shared_labels[a["displayLabel"]] > 1 or order_words(a) else ""
        a["readerLabel"] = f"{a['displayLabel']} ({words})" if words else a["displayLabel"]
    shared_labels = collections.Counter(a["readerLabel"] for a in arms_out)
    seen_labels = collections.Counter()
    for a in arms_out:
        shared = a["readerLabel"]
        if shared_labels[shared] > 1:
            seen_labels[shared] += 1
            words = change_words(a)
            a["setupOrdinal"] = (f"{seen_labels[shared]} of {shared_labels[shared]} setups with "
                                 + ("this change" if words else "no plugin change"))
            a["readerLabel"] = f"{a['displayLabel']} ({words + '; ' if words else ''}{a['setupOrdinal']})"
    arm_labels = {a["armId"]: a["readerLabel"] for a in arms_out}
    # a list of locations several setups share is printed once, at the first setup that has it; later ones point at it
    first_with = {}
    for a in arms_out:
        locs = tuple(a["locationPluginScope"].get("locations") or [])
        a["locationPluginScope"]["sameAs"] = first_with.get(locs) if locs else None
        if locs:
            first_with.setdefault(locs, a["readerLabel"])
    # the page prints a list up to the skill's bound and counts the rest, which the evidence index lists in full
    # (Gate C round 1, H2: one footprint's list took about seven pages)
    lo_words = reader_data.get("locationOrder") or {}
    bound = lo_words.get("maxLocations")
    for a in arms_out:
        # (Helgi and Cut Content r3 reviews) the list says what share of the setup's load order it is
        shown_, total_ = len(a.get("locationPluginOrder") or []), len(a.get("plugins") or [])
        a["locationShareLine"] = (bind(lo_words["share"], {"shown": fmt_n(shown_), "total": fmt_n(total_),
                                                            "these": "This is" if shown_ == 1 else "These are"})
                                  if lo_words.get("share") and 0 < shown_ < total_ else None)
        locs = list(a["locationPluginScope"].get("locations") or [])
        k = bound if isinstance(bound, int) and bound > 0 else len(locs)
        a["locationPluginScope"]["shown"] = locs[:k]
        a["locationPluginScope"]["moreLine"] = (bind(lo_words["moreLocations"], {"count": fmt_n(len(locs) - k)})
                                                if len(locs) > k else None)
    for f in findings:
        f["readerArmLabel"] = arm_labels.get(f.get("armId"), "Measured setup")
        c = f.get("counts") or {}
        if f.get("kind") != "finding" or c.get("groupKind") != "CLOBBERED" or not c.get("A") or not c.get("B"):
            continue
        arm = next((x for x in arms_out if x.get("armId") == f.get("armId")), None)
        pa = plan_arms.get(f.get("armId")) or {}
        order = [p.get("name") for p in ((pa.get("requested") or {}).get("plugins") or []) if p.get("name")]
        direction = infer_clobber_direction(f, order)
        if direction:
            f["overwriteDirection"] = direction
            f["readerPlain"] = (f"{direction['winningPlugin']}, loaded later, overwrites {fmt_n(f.get('rowsTotal'))} "
                                f"{noun(f.get('rowsTotal'), 'navmesh edit')} from {direction['overwrittenPlugin']}.")
    # Context-only overwrite history remains fully indexed, but is never presented as a compatibility finding.
    # A candidate is called pre-existing only when the same structured group and every record key occur at baseline.
    baseline_context = {}
    for f in findings:
        c = f.get("counts") or {}
        if f.get("armId") == "baseline" and context_only(f.get("checkId"), c.get("groupKind")):
            baseline_context[(f.get("checkId"), c.get("groupKind"), c.get("A"), c.get("B"))] = {
                r.get("formKey") for r in f.get("allRows") or [] if r.get("formKey")}
    for f in findings:
        c = f.get("counts") or {}
        if not context_only(f.get("checkId"), c.get("groupKind")):
            continue
        keys = {r.get("formKey") for r in f.get("allRows") or [] if r.get("formKey")}
        prior = baseline_context.get((f.get("checkId"), c.get("groupKind"), c.get("A"), c.get("B")))
        if f.get("armId") != "baseline" and keys and prior is not None and keys <= prior:
            f["readerContext"] = "Already present before adding the patch; this remains overwrite history, not an established incompatibility."
        else:
            f["readerContext"] = "Recorded overwrite history for this setup; it does not by itself establish an incompatibility or attribute the result to the patch."
    # Imported MCP evidence: the reader sees a practical qualification beside the result it affects (reused: not
    # measured for this evaluation, source stated not re-checked; origin unknown: not recorded whether it was);
    # fresh stays unremarked. Plan, arm, hash and receipt detail go to the evidence index. Counts are untouched.
    origin_words = reader_data.get("importOrigin") or {}
    imports = imported_evidence(b)
    by_rid = {rid: rc for rid, rc in b.receipts.items() if isinstance(rc.get("imported"), dict)}
    imports["limit"] = origin_words.get("limit")
    import_by_key = {(r["checkId"], r["armId"]): r for r in imports["rows"]}
    for row in imports["rows"]:
        row["receiptEvidenceId"] = eid_by_path.get(row["receiptPath"])
        # The importer's own note about what this result IS. Reuse provenance ("captured for
        # another evaluation") and correction provenance ("re-interpreted under corrected
        # rules") answer different questions, and a reader shown only the first cannot tell
        # that the numbers were recomputed. The note is the import's statement about itself,
        # carried verbatim and attributed, never summarised into a claim of our own.
        rc_note = ((by_rid.get(row.get("receiptId")) or {}).get("result") or {}).get("note")
        row["note"] = rc_note if isinstance(rc_note, str) and rc_note.strip() else None
        # Reuse between two setups of THIS evaluation is a different statement from reuse across evaluations, so it
        # has its own sentence; a receipt that does not say which keeps the cautious cross-evaluation wording.
        row["originWording"] = ("reusedWithinEvaluation"
                                if row["origin"] == "reused" and row.get("reusedWithin") == "this-evaluation"
                                else row["origin"])
        row["readerProvenance"] = origin_words.get(row["originWording"]) or None
        row["indexOrigin"] = origin_words.get("index", {}).get(row["originWording"])
        row["findingIds"] = []
    for f in findings:
        row = import_by_key.get((f.get("checkId"), f.get("armId")))
        if row is None or f.get("kind") not in ("finding", "null", "catalogue") or f.get("part") is not None:
            continue
        f["importOrigin"] = row["origin"]
        f["readerProvenance"] = row["readerProvenance"]
        row["findingIds"].append(f["id"])
    # Distinct import notes, in first-seen order. One sentence per DISTINCT note, not one per
    # import: twelve identical copies of the same explanation is noise, and the reader needs
    # the statement once.
    import_notes = list(dict.fromkeys(r["note"] for r in imports["rows"] if r.get("note")))
    imports["notes"] = import_notes
    missing = ev.get("missing") or []

    def reader_status(m) -> str:
        status = str(m.get("status"))
        state = comparison_state.get((m.get("checkId"), m.get("armId"))) if m.get("part") == "diff" else None
        if status == "completed" and state:
            if state["refused"]:
                return "comparison-refused|" + refused_words(state)
            if state["unmeasured"]:
                # a setup that leaves an evaluated mod out cannot measure that mod's own objects: said with the count
                gone_ = [t_ for t_ in [x_.get("name") for x_ in targets if x_.get("name")] if str(t_).lower() in
                         {str(x).lower() for x in ((arm_changes.get(m.get("armId")) or ([], []))[1] or [])}]
                return f"comparison-unmeasured|{state['unmeasured']}" + (f"|{gone_[0]}" if gone_ else "")
        if status == "completed" and not state:
            return "completed(undetermined)"   # it ran and its detector reached no verdict
        return status
    reader_missing = [{**m, "status": reader_status(m)} for m in missing]
    by_cause = collections.Counter(str(m.get("status")) for m in missing)
    run_ids = b.manifest.get("runIds") or []
    scope_out = {"targets": targets, "registryVersion": b.plan.get("registry", {}).get("registryVersion") or disp.get("registryVersion"),
                 "planId": b.plan.get("planId"), "planCreatedUtc": b.plan.get("createdUtc"),
                 "runStartedUtc": (run_ids[0].get("startedUtc") if run_ids else None),
                 "bundleCreatedUtc": b.manifest.get("createdUtc"),
                 "scopeGeneratedUtc": (b.scope or {}).get("generatedUtc") if isinstance(b.scope, dict) else None,
                 "dispositionCreatedUtc": disp.get("createdUtc"),
                 "locationScope": {"locations": scope_locations,
                                   "basis": "locations recorded in the target footprint"},
                 "arms": arms_out, "patchDeclarations": patch_declarations_out, "states": b.plan.get("states") or {"selected": [], "notExplored": []},
                 "completion": {"complete": bool(ev.get("complete")), "claim": ev.get("claim"), "missingRequired": len(missing),
                                "byCause": dict(sorted(by_cause.items())), "unknownApplicability": ev.get("unknownApplicability") or []},
                 "integrity": ev.get("integrity") or {"ok": None, "defects": []},
                 "explicitDecisions": {"select": (disp.get("explicitDecisions") or {}).get("select"),
                                       "exclude": (disp.get("explicitDecisions") or {}).get("exclude")},
                 "checks": {cid: {"title": c.get("title"), "family": c.get("family"), "required": c.get("required"),
                                  "strength": (c.get("evidence") or {}).get("strength"), "claim": (c.get("evidence") or {}).get("claim"),
                                  "limits": (c.get("evidence") or {}).get("limits")} for cid, c in sorted(b.checks.items())}}

    # --- figures (Missives review R2/R3): every frame of one capture request belongs to the finding any of its frames
    # illustrates (a split's left half is measured on the other setup), and each frame gets a caption from data: the
    # setup it shows, the objects and colours its own renderer command marked, and what to look for
    import reel_plan
    caption_words = (pr.get("reader") or {}).get("figureCaptions") or {}
    by_request: dict = collections.defaultdict(list)
    for m_ in media:
        req_ = (m_.get("provenance") or {}).get("captureRequest")
        if req_:
            by_request[req_].append(m_["id"])
    media_by_id0 = {m_["id"]: m_ for m_ in media}
    # the frame each figure shows first (figure_units' order), keyed by its capture request
    first_frame_ = {}
    for u_ in figure_units(media):
        if u_:
            first_frame_.setdefault((u_[0].get("provenance") or {}).get("captureRequest") or u_[0]["id"], u_[0]["id"])
    for f_ in findings:
        mids_ = list(f_.get("mediaIds") or [])
        for mid_ in list(mids_):
            req_ = ((media_by_id0.get(mid_) or {}).get("provenance") or {}).get("captureRequest")
            for other_ in by_request.get(req_, []):
                if other_ not in mids_:
                    mids_.append(other_)
        f_["mediaIds"] = mids_
    n_refs = (names_doc0 or {}).get("refs") or {}
    n_bases = (names_doc0 or {}).get("bases") or {}
    n_objects = (names_doc0 or {}).get("objects") or {}
    # each mod's name as the reel says it (the plan command's one resolution, modnames.py), else the bundle's
    n_mods = {**((names_doc0 or {}).get("mods") or {}), **(mod_names or {})}

    def fig_object(fk_):
        eid_ = (n_bases.get((n_refs.get(str(fk_)) or {}).get("base") or "") or {}).get("editorId")
        return (n_objects.get(eid_) or {}).get("name") or reel_plan.humanize(eid_) or str(fk_)

    def fig_mod(plugin_):
        return (n_mods.get(str(plugin_)) or {}).get("name") or re.sub(r"\.(esp|esm|esl)$", "", str(plugin_), flags=re.I)

    target_plugin = next((t.get("name") for t in targets if t.get("name")), None)
    for f_ in findings:
        row_ = (f_.get("rows") or f_.get("allRows") or [{}])[0] if (f_.get("rows") or f_.get("allRows")) else {}
        for mid_ in f_.get("mediaIds") or []:
            m_ = media_by_id0.get(mid_)
            if m_ is None or m_.get("caption"):
                continue
            prov_ = m_.get("provenance") or {}
            state_ = prov_.get("vignetteState")
            argv_ = [str(x) for x in prov_.get("argv") or []]
            marks_ = {}
            for i_, x_ in enumerate(argv_[:-1]):
                if x_ == "--ref" and "=" in argv_[i_ + 1]:
                    fk_, col_ = argv_[i_ + 1].rsplit("=", 1)
                    marks_[col_] = fk_
            arm_ = (m_.get("subject") or {}).get("armId")
            setup_ = (caption_words.get("setupWith") if arm_ == f_.get("armId") else caption_words.get("setupWithout") or "")
            setup_ = fill(str(setup_ or ""), mod=fig_mod(target_plugin))
            fk_ = (m_.get("subject") or {}).get("formKey") or row_.get("formKey")
            slots_ = {"setup": setup_, "object": fig_object(fk_)}
            key_ = state_
            if state_ == "highlight" and (marks_.get("cyan") or marks_.get("magenta")):
                # the pair (R13): the frame's subject and its marks; where the subject is the one marked, its partner
                # from the finding's one row on that object (none said when several rows name it: not guessed)
                pair_ = [fk_, *marks_.values()]
                if {str(x).casefold() for x in pair_} == {str(fk_).casefold()}:
                    own_rows_ = [r_ for r_ in (f_.get("rows") or f_.get("allRows") or [])
                                 if str(r_.get("formKey")).casefold() == str(fk_).casefold()]
                    if len(own_rows_) == 1:
                        pair_ += list(own_rows_[0].get("other") or [])[:1]
                key_, hs_ = highlight_caption(marks_, fig_object, fig_mod, participants=pair_)
                slots_.update(hs_)
            if state_ in NAVMESH_STATES:
                # a navmesh frame: the plugin whose earlier version the historical frame draws, from that frame's own
                # renderer command (--nav-version SOURCE=PLUGIN), never from the finding's pair (a finding's first pair
                # need not be the pictured source's: Clockwork's Falkreath figure named the wrong winner)
                hist_ = navmesh_earlier_plugin([media_by_id0.get(o_) or {}
                                                for o_ in by_request.get(prov_.get("captureRequest"), [])])
                slots_.update(earlier=fig_mod(hist_) if hist_ else (caption_words.get("navmeshEarlierUnknown") or "an earlier plugin"))
            if state_ in ("left", "right"):
                # the left half is the setup without the evaluated mod: the losing side's spot; the right, the winner's
                ab_ = f_.get("counts") or {}
                slots_["mod"] = fig_mod(row_.get("A") or ab_.get("A")) if state_ == "left" else fig_mod(row_.get("B") or ab_.get("B"))
            tmpl_ = caption_words.get(key_) if key_ in caption_words else caption_words.get("fallback")
            try:
                m_["caption"] = fill(str(tmpl_ or ""), **slots_) or None
            except (KeyError, IndexError):
                m_["caption"] = fill(str(caption_words.get("fallback") or ""), **slots_) or None
            # (Missives retry 2 blind pass) a split's magenta mark is a removal the split frames, not a second clash
            # with the cyan object: said on both halves, from the left half's own renderer command
            if state_ in ("left", "right") and m_.get("caption"):
                left_marks_ = {}
                for o_ in by_request.get(prov_.get("captureRequest"), []):
                    po_ = (media_by_id0.get(o_) or {}).get("provenance") or {}
                    if po_.get("vignetteState") == "left":
                        av_ = [str(x) for x in po_.get("argv") or []]
                        left_marks_ = {av_[i + 1].rsplit("=", 1)[1]: av_[i + 1].rsplit("=", 1)[0]
                                       for i, x in enumerate(av_[:-1]) if x == "--ref" and "=" in av_[i + 1]}
                gone_ = left_marks_.get("magenta")
                key_ = "removedLeft" if state_ == "left" else "removedRight"
                if gone_ and caption_words.get(key_):
                    m_["caption"] += fill(caption_words[key_], 
                        removed=fig_object(gone_), object=fig_object(fk_),
                        winner=fig_mod(row_.get("B") or (f_.get("counts") or {}).get("B") or target_plugin))
            # a cutaway or a sliced top-down says so in its caption, not only in the frame's own label (a crop can cut that)
            if m_.get("caption"):
                # how the picture was made is said once per figure, on the frame it shows first
                first_ = first_frame_.get(prov_.get("captureRequest") or mid_) == mid_
                m_["caption"] += view_kind_caption(prov_, caption_words, first=first_) + (
                    media_kind_caption(m_, caption_words) if first_ else "")
            m_["captureState"] = state_
            m_["captureRequest"] = prov_.get("captureRequest")

    # --- chapters (the reel's plan; D6 renders them)
    reel = pr.get("reel") or {}
    chapters = []
    claim = ev.get("claim")
    claim_sentence = bind((pr.get("claimSentences") or {}).get(claim or "incomplete", "{claim}"),
                          {"missingRequired": len(missing), "claim": claim})
    target_names = [t["name"] for t in targets if t.get("name")]
    target_set = {str(x).lower() for x in target_names}
    comparison_weight = collections.Counter()
    for f in findings:
        if f.get("readerRelevant") is False:
            continue
        c = f.get("counts") or {}
        pair = [str(x) for x in (c.get("A"), c.get("B")) if x]
        if not any(x.lower() in target_set for x in pair):
            continue
        for name in pair:
            if name.lower() not in target_set and name not in supplied:
                comparison_weight[name] += max(1, int(f.get("rowsTotal") or 0))
    # a difference with the base game is set aside, never a conflict partner named in the opening; nor is a plugin an
    # evaluated mod lists as a master (it builds on it, P63)
    plugin_masters_doc = plugin_masters(b)
    pw_ = reader_data.get("prediction") or {}
    geometry_partners = set()
    for f in findings:
        pair_ = {str(x).lower() for x in ((f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")) if x}
        if is_geometry_check(f.get("checkId"), pw_) and pair_ & target_set:
            geometry_partners |= pair_ - target_set
    # a master named only by record values is not a conflict partner; one whose objects clash with the mod's still is
    target_masters = {m for t in target_set for m in (plugin_masters_doc or {}).get(t, ())} - geometry_partners
    base_game_partners = [n for n in comparison_weight if is_base_game(n, reader_data.get("prediction") or {})]
    comparison_names = [n for n, _ in sorted(comparison_weight.items(), key=lambda x: (-x[1], x[0].lower()))
                        if n not in base_game_partners and n.lower() not in target_masters]
    reader_scope_names = target_set | {x.lower() for x in supplied}
    for f in findings:
        c = f.get("counts") or {}
        pair = {str(x).lower() for x in (c.get("A"), c.get("B")) if x}
        f["readerRelevant"] = bool(f.get("readerRelevant", True)) and (not pair or bool(pair & reader_scope_names))
    # The detail list shows only clashes between two different mods. A result within one mod, against the base game,
    # or naming no pair at all (a count only) is set aside to the evidence index with its reason; readerRelevant is
    # unchanged, so the verdict, the opening and the reel read what they read before.
    aside_counts = collections.Counter()
    for f in findings:
        if f.get("kind") != "finding":
            continue
        c = f.get("counts") or {}
        a_, b_ = c.get("A"), c.get("B")
        aside = ("countOnly" if not (a_ and b_) else "sameMod" if str(a_).lower() == str(b_).lower() else
                 "baseGame" if (is_base_game(a_, reader_data.get("prediction") or {})
                                or is_base_game(b_, reader_data.get("prediction") or {})) else
                 "noBody" if f.get("rowsNoBody") and not f.get("readerRows") else
                 # an evaluated mod building on its own master (P63); a declared patch's own choices stay listed
                 "ownMaster" if (master_of(plugin_masters_doc, a_, b_)
                                 and not is_geometry_check(f.get("checkId"), reader_data.get("prediction") or {})
                                 and {str(a_).lower(), str(b_).lower()} & target_set
                                 and not (finding_declared(f, a_) or finding_declared(f, b_))) else None)
        f["readerDetail"] = aside is None
        f["readerDetailAside"] = aside
        if aside and f.get("readerRelevant"):
            aside_counts[aside] += 1
    # A record group between one evaluated mod and a mod that is neither evaluated nor a declared patch is not listed
    # in the detail list (Gate C round 1, H2: listing each such pair once per setup, then once per pair, still broke
    # the page limit). It goes to the evidence index (`singleMod`), counted in one sentence; the prediction lists
    # those clashes under their own heading. Clashes between the evaluated mods, or with a patch, stay listed.
    supplied_lower = {str(x).lower() for x in supplied}
    single_mod_count = 0
    for f in findings:
        if not (f.get("kind") == "finding" and f.get("readerDetail") and f.get("family") == "records"):
            continue
        c = f.get("counts") or {}
        pair = {str(x).lower() for x in (c.get("A"), c.get("B"))}
        if len(pair & target_set) != 1 or any(x in supplied_lower and finding_declared(f, x) for x in pair):
            continue
        f["readerDetail"] = False
        f["readerDetailAside"] = "singleMod"
        if f.get("readerRelevant"):
            single_mod_count += 1
    compared =", ".join(comparison_names) if comparison_names else "the measured load order"
    # the tested setups leave out or add mods to attribute each clash; said as that, never as one setup differing by a
    # list of plugins as if they were a patch
    # counted and described from the arms themselves: a setup counts when it leaves out or adds a plugin other than
    # an evaluated mod or a declared patch, and the verb is what those setups do
    alt_left_, alt_added_, n_alt_ = [], [], 0
    for arm_id_, (added_, removed_) in arm_changes.items():
        if comparison_kinds.get(arm_id_) in ("baseline", "patch-comparison", "supplied-combination"):
            continue
        keep_ = lambda xs, side_: [n for n in xs if n.lower() not in plan_target_set and not declared_copy(side_, n)]  # noqa: E731
        if keep_(added_, arm_id_) or keep_(removed_, "baseline"):
            n_alt_ += 1
            alt_left_ += [n for n in keep_(removed_, "baseline") if n not in alt_left_]
            alt_added_ += [n for n in keep_(added_, arm_id_) if n not in alt_added_]
    us_words_ = (reader_data.get("prediction") or {}).get("unstatedSetupsVerbs") or {}
    alt_verb_ = ("both" if alt_left_ and alt_added_ else "add" if alt_added_ else "leave")
    unstated_text = ((" " + bind((reader_data.get("prediction") or {})["unstatedSetups"], {
                          "count": fmt_n(n_alt_), "noun": "setup" if n_alt_ == 1 else "setups",
                          "verb": us_words_.get(alt_verb_, "leave out or add"),
                          "plugins": and_list(sorted(set(alt_left_ + alt_added_), key=str.lower))}))
                     if n_alt_ and (reader_data.get("prediction") or {}).get("unstatedSetups") else
                     (f" One tested setup differs by {', '.join(unstated_difference_plugins)}, "
                      f"which is not declared as a patch; it is reported as a plugin that "
                      f"differs, not as an evaluated patch.")
                     if unstated_difference_plugins else "")
    patch_text = ", ".join(patch_label(n) for n in supplied) if supplied else "no separate patch plugin"
    # One sentence for declared patches a mixed arm changes; number agreement is its only branch.
    _mixed_setup = ("The tested setup changes" if sum(1 for k in comparison_kinds.values() if k == "mixed") == 1
                    else "The tested setups change")
    mixed_declared_text = ((f"{_mixed_setup} the declared patches {', '.join(map(patch_label, mixed_declared_patches))} "
                            f"at once, so none of them is evaluated on its own." if len(mixed_declared_patches) > 1 else
                            f"{_mixed_setup} the declared patch {patch_label(mixed_declared_patches[0])} together with another "
                            f"plugin, so it is not evaluated on its own.")
                           if mixed_declared_patches else "")
    patch_adds = any(n in supplied for added, _ in arm_changes.values() for n in added)
    patch_removes = any(n in supplied for _, removed in arm_changes.values() for n in removed)
    # a same-named patch is loaded in place of the other file of its name (item 20)
    in_place_ = {replaced_patch_side(a_, n) for a_ in arm_changes for n in arm_replaced.get(a_) or [] if n in supplied}
    patch_how = ("added or removed for comparison" if patch_adds and patch_removes else
                 "included in the measured load order and removed for comparison" if patch_removes else
                 "added to the measured load order for comparison" if patch_adds or not in_place_ else
                 ("loaded in place of the evaluated mod's file of the same name for comparison"
                  if all(n.lower() in plan_target_set for n in supplied) else
                  "loaded in place of the file of the same name for comparison") if in_place_ == {"arm"} else
                 "included in the measured load order and replaced by the file of the same name for comparison"
                 if in_place_ == {"baseline"} else "added or removed in place of the file of the same name for comparison")
    scope_head = f"This report evaluates {', '.join(target_names) or 'the target mod'} within the measured load order. "
    scope_line = (scope_head
                  + (f"The main recorded conflicts involving the target are with {compared}. " if comparison_names else
                     ("The recorded conflicts involving the target are with the base game only; they are set aside "
                      "below, as differences rather than clashes between mods. " if base_game_partners else
                      "No comparison mod was identified by the recorded conflicts. "))
                  + " ".join(x for x in (
                      ((f"The patch evaluated is {patch_text}, {patch_how}." if len(supplied) == 1 else
                        (f"The patches evaluated are {patch_text}, {patch_how}." if len(supplied) <= 2 else
                        f"The {len(supplied)} patches named in the title are evaluated, {patch_how}.")) if supplied else
                       # never claim no patch was declared while a changed plugin is a declared patch
                       "" if mixed_declared_patches or ambiguous_names else
                       "No separate patch plugin was declared for this comparison."),
                      mixed_declared_text, ambiguous_text) if x)
                  + unstated_text)
    scope_tail = (" ".join(x for x in (
                      ((f"The patch evaluated is {patch_text}, {patch_how}." if len(supplied) == 1 else
                        (f"The patches evaluated are {patch_text}, {patch_how}." if len(supplied) <= 2 else
                        f"The {len(supplied)} patches named in the title are evaluated, {patch_how}.")) if supplied else
                       "" if mixed_declared_patches or ambiguous_names else
                       "No separate patch plugin was declared for this comparison."),
                      mixed_declared_text, ambiguous_text) if x)
                  + unstated_text)
    for _a in arms_out:
        _a["comparisonKind"] = comparison_kinds.get(_a.get("armId"))
        if arm_replaced.get(_a.get("armId")):
            _a["replacedPlugins"] = list(arm_replaced[_a.get("armId")])
        if comparison_kinds.get(_a.get("armId")) == "bytes-not-compared":
            _a["bytesNotCompared"] = list(arm_unverified[_a.get("armId")])
    alternative_arms = [a["armId"] for a in arms_out if a.get("kind") != "baseline"]
    candidate_orders = [a.get("orderEvaluation") or {} for a in arms_out if a.get("kind") != "baseline"]
    reader_regressions = [f for f in findings if f.get("kind") == "regression" and f.get("readerRelevant")]
    has_regression = bool(reader_regressions)
    overlap_created_total = sum(int((f.get("counts") or {}).get("readerCreated") or 0)
                                for f in reader_regressions if f.get("checkId") == "geometry.object-overlap")
    has_other_regression = any(f.get("checkId") != "geometry.object-overlap" for f in reader_regressions)
    repaired_total = sum(int((f.get("counts") or {}).get("readerRepaired") or 0) for f in findings
                         if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                         and f.get("checkId") != "geometry.object-overlap")
    repair_arms = {f.get("armId") for f in findings if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                   and f.get("checkId") != "geometry.object-overlap" and int((f.get("counts") or {}).get("readerRepaired") or 0)}
    overlap_reduction_total = sum(int((f.get("counts") or {}).get("readerRepaired") or 0) for f in findings
                                  if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                                  and f.get("checkId") == "geometry.object-overlap")
    overlap_reduction_arms = {f.get("armId") for f in findings if f.get("kind") in ("repaired", "reclassified")
                              and f.get("readerRelevant") and f.get("checkId") == "geometry.object-overlap"
                              and int((f.get("counts") or {}).get("readerRepaired") or 0)}
    has_alternative = any(a.get("kind") != "baseline" for a in arms_out)
    has_completed_result = any(f.get("kind") in ("regression", "finding", "repaired", "reclassified", "null", "catalogue")
                               and f.get("readerRelevant") for f in findings)
    has_context_only = any(f.get("readerEvidenceReason") in ("context-only-kind", "context-only-or-inseparable")
                           for f in findings)
    def defect_phrase() -> str:
        # how many new problems, in which checks, and whether a comparison was not accepted as a result
        per_check = collections.OrderedDict()
        for f in reader_regressions:
            if f.get("checkId") == "geometry.object-overlap":
                continue
            per_check.setdefault(f.get("checkId"), (f, [], [], []))[1].append(int((f.get("counts") or {}).get("readerCreated") or 0))
            per_check[f.get("checkId")][3].append(int((f.get("counts") or {}).get("readerNoBody") or 0))
            if (comparison_state.get((f.get("checkId"), f.get("armId"))) or {}).get("notAccepted"):
                per_check[f.get("checkId")][2].append(True)
        parts = []
        for check_id, (f, ns, unacc, nbs) in per_check.items():
            title_ = (reader_data.get("checkTitles") or {}).get(check_id, f.get("title"))
            ru = ((reader_data.get("regressionUnits") or {}).get(check_id)
                  if (f.get("counts") or {}).get("readerUnitCounted") else None)
            if len(ns) == 1:
                parts.append(f"{fmt_n(ns[0])} " + (ru[0 if ns[0] == 1 else 1] if ru else
                                                   f"new {'problem' if ns[0] == 1 else 'problems'}")
                             + f" in the check \"{title_}\""
                             + (bind(cmp_words["noBodyCreated"], {"count": fmt_n(nbs[0])}) if nbs[0] else ""))
            else:                              # one count per setup, never summed across setups
                parts.append(f"new problems in the check \"{title_}\" in {len(ns)} setups ("
                             + ", ".join(fmt_n(n_) for n_ in ns) + ")")
            if unacc:
                parts[-1] += cmp_words["verdictNotAccepted"]
        return " and ".join(parts) if parts else "a new defect in a check that ran"

    generic_verdict = None
    if has_regression:
        if has_other_regression:
            change_subject = setup_subject({f.get("armId") for f in reader_regressions
                                            if f.get("checkId") != "geometry.object-overlap"} | repair_arms)
            if repaired_total and repair_claimable(repair_arms):
                verdict = (f"{change_subject} resolves some measured conflicts, but it also "
                           f"introduces {defect_phrase()}.")
            elif repaired_total:
                verdict = (f"{change_subject} records fewer conflicts than the measured load "
                           f"order and also introduces {defect_phrase()}. "
                           + non_repair_reason(repair_arms))
            else:
                verdict = f"{change_subject} introduces {defect_phrase()}."
            if overlap_created_total:
                verdict += bind(reader_verdict_templates["overlapAlongsideDefect"],
                                {"createdCount": overlap_created_total,
                                 "candidateNoun": overlap_candidate_noun(overlap_created_total)})
        else:
            verdict = bind(reader_verdict_templates["overlapOnlyRegression"],
                           {"createdCount": overlap_created_total,
                            **subject_values({f.get("armId") for f in reader_regressions}),
                            "candidateNoun": overlap_candidate_noun(overlap_created_total)})
        if overlap_reduction_total:
            verdict += reader_verdict_templates["overlapReductionAlongsideRegression"]
    elif repaired_total and repair_claimable(repair_arms):
        verdict = f"{setup_subject(repair_arms)} resolves some measured conflicts and no new defect was reported by the checks that ran."
    elif repaired_total:
        # Fewer conflicts, but produced by an arm that cannot carry a repair reading. Say what
        # was observed and refuse the repair reading -- then say what happened to the patch
        # comparison SEPARATELY, because which arm improved and whether a patch arm exists are
        # different questions. Appending "no setup containing a patch was compared"
        # unconditionally denied a declared patch comparison that had in fact been measured,
        # whenever the reduction happened to come from a removal control.
        patch_arms = [a for a in alternative_arms
                      if comparison_kinds.get(a) == "patch-comparison"]
        if not patch_arms:
            patch_clause = (" No setup containing a patch was compared, so these results do "
                            "not say whether a patch would resolve them.")
        else:
            patch_named = ", ".join(map(patch_label, supplied)) if supplied else "a patch"
            patch_repaired = any(a in repair_arms for a in patch_arms)
            patch_regressed = any(a in {f.get("armId") for f in reader_regressions}
                                  for a in patch_arms)
            if patch_repaired:
                patch_clause = ""          # handled by the repair branch above
            elif patch_regressed:
                patch_clause = (f" A setup containing {patch_named} was also compared, and it "
                                f"introduced a new defect in a check that ran.")
            else:
                patch_clause = (f" A setup containing {patch_named} was also compared; the "
                                f"checks that ran recorded no reduction and no new defect from "
                                f"it, so the patch was tested and this result is not a "
                                f"statement that it went untested.")
        verdict = (f"{setup_subject(repair_arms)} records fewer conflicts than the measured "
                   f"load order, and no new defect was reported by the checks that ran. "
                   + non_repair_reason(repair_arms) + patch_clause)
    elif overlap_reduction_total:
        verdict = bind(reader_verdict_templates["overlapOnlyReduction"], subject_values(overlap_reduction_arms))
    elif has_context_only and not any(f.get("kind") in ("regression", "finding") and f.get("readerRelevant") for f in findings):
        verdict = ("The available results record navmesh overwrite history, but they cannot determine incompatibility "
                   "without a measured mismatch in the winning scene or links.")
    elif claim == "no-findings-in-completed-checks":
        verdict = "The completed checks found no problem in what they examined."
    elif has_alternative:
        verdict = generic_verdict = (f"The completed checks do not establish that "
                                     f"{lower_first(setup_subject(alternative_arms))} improves the target mod.")
    elif any(f.get("kind") == "finding" and f.get("readerRelevant") for f in findings):
        verdict = "The completed checks found conflicts involving the target mod; no alternative setup was supplied for comparison."
    elif has_completed_result:
        verdict = "The completed checks found no problem in what they examined; no alternative setup was supplied for comparison."
    else:
        verdict = "The available results cannot determine whether the target mod has a problem in the measured setup."
    # a comparison that refused a verdict is said in the summary, where a reader decides, not only further down
    # (round 4); its counts were never part of the sentence above
    # (Missives r3 report review, finding 2) every refusal names the setup it belongs to, so it is never read as the
    # patch's when it is another setup's
    refused_said, refused_lines = [], []
    for (check_id_, arm_), state_ in comparison_state.items():
        if state_.get("refused") and arm_ in alternative_arms and (check_id_, arm_) not in refused_said:
            refused_said.append((check_id_, arm_))
            refused_lines.append(bind(cmp_words["refusedSummary"], {
                "title": (reader_data.get("checkTitles") or {}).get(check_id_) or b.check_title(check_id_),
                "setup": lower_first(arm_subject(arm_)), "why": refused_words(state_)}))
    if refused_lines:
        verdict += " " + " ".join(refused_lines)
    limitations = []
    if missing:
        limitations.append(bind((reader_data.get("prediction") or {}).get("limitationIncomplete")
                                or "Testing is incomplete; the outstanding areas are listed under \"{heading}\".",
                                {"heading": ((reader_data.get("prediction") or {}).get("headings") or {}).get("coverage")
                                 or "What the checks could not judge"}))
    if not any(m.get("assessed") and m.get("mediaKind") == "game-video" for m in media):
        limitations.append("No matching play-time evidence was supplied, so this does not show what happened during play.")
    if any(a.get("espOnly") for a in arms_out):
        limitations.append("The supplied patch was tested as a plugin only; its meshes, scripts and installer choices were not included.")
    if has_context_only:
        limitations.append("Navmesh overwrite history is retained in the evidence index as context and is not treated as proof of incompatibility.")
    scope_limits = reader_data.get("scopeLimits") or {}
    other_limits = [scope_limits.get(x.get("extent"), "An additional area could not be assessed; see the full scope in the evidence index.")
                    for x in un.get("scopeUnmeasured") or [] if isinstance(x, dict)]
    # the areas not measured and the objects not judged are stated with their counts under the coverage heading;
    # this list keeps only what that heading does not say (P53: each was printed twice)
    other_limits = []
    if unassessed_media:
        other_limits.append("Some supplied images or clips could not be matched to this evaluation and were excluded from the evidence shown.")
    # One bullet, not one per import. Notes that embed per-import counts differ only in their
    # tails, so deduplicating on the whole string leaves a dozen near-identical paragraphs --
    # the reader needs the explanation once. Emit the shared opening once and send the
    # per-import remainder to the evidence index, which is the reference document and keeps
    # every note in full. Nothing is summarised: the shared text is the importer's own, cut
    # only at a sentence boundary.
    if import_notes:
        # The shared text is every SENTENCE present in all the notes, in the order the first
        # note gives them -- not the common prefix. A note that puts per-import figures in the
        # middle has common sentences on BOTH sides of them, and a prefix rule silently drops
        # everything after the first difference. That is how "this correction was made after
        # the subject was inspected" fell off the page while surviving in the index.
        def sentences(text):
            return [x.strip() for x in re.split(r"(?<=[.!?])\s+", text.strip()) if x.strip()]
        note_sentences = [sentences(n) for n in import_notes]
        common = set(note_sentences[0]).intersection(*(set(x) for x in note_sentences[1:]))             if len(note_sentences) > 1 else set(note_sentences[0])
        shared = " ".join(x for x in note_sentences[0] if x in common)

        # Many notes can share no whole sentence when each names its own setup and file in the sentence that says
        # what the evidence is ("REUSED evidence, not freshly measured: captured ... arm X; ..."); the clause they
        # all share still reaches the page, once (Gate C round 1: Group 6's sixteen notes took two pages).
        def clauses(text):
            return [x.strip() for x in re.split(r"(?<=[.!?;:])\s+", text.strip()) if x.strip()]
        note_clauses = [clauses(n) for n in import_notes]
        common_c = set(note_clauses[0]).intersection(*(set(x) for x in note_clauses[1:]))
        # a shared clause of fewer than four words ("Rows:", a file name) says nothing on its own
        shared_c = " ".join(re.sub(r"[;:]$", ".", x) for x in note_clauses[0] if x in common_c and len(x.split()) >= 4)
        if len(import_notes) > 1 and len(shared) >= 40:
            other_limits.append(
                "The imported results in this report carry a note from the tool that produced "
                "them: " + shared + " Each import's own note, including the figures that "
                "differ between them, is in the evidence index.")
        elif len(import_notes) > 3:
            # a note's opening clause says what the evidence is; one at least half of them open with (say, half
            # reused and half freshly measured) is stated with its count
            seen_c = collections.Counter(cs[0] for cs in note_clauses if cs)
            half = [x for x in dict.fromkeys(cs[0] for cs in note_clauses if cs)
                    if x not in common_c and len(x.split()) >= 4 and seen_c[x] * 2 >= len(import_notes)]
            other_limits.append(
                f"The {fmt_n(len(import_notes))} imported results in this report carry notes from the tool that "
                "produced them, which differ from one import to another" +
                (f"; every one of them says: {shared_c}" if shared_c else ".") +
                "".join(f" {fmt_n(seen_c[x])} of them say: {re.sub(r'[;:]$', '.', x)}" for x in half) +
                " Each note in full is in the evidence index.")
        else:
            for note in import_notes:
                other_limits.append("The imported results in this report carry a note from the "
                                    "tool that produced them: " + note.strip())
    check_titles = reader_data.get("checkTitles") or {}
    reason_labels = reader_data.get("missingReasons") or {}

    def missing_reason(status: str, fallback: str) -> str:
        # `blocked(prerequisite:<check>)` carries the check it waited for; the reader is told which one, in its title
        blocked_by = PREREQUISITE_STATUS.match(status)
        if blocked_by and reader_data.get("missingPrerequisiteReason"):
            prerequisite = check_titles.get(blocked_by.group(1)) or b.check_title(blocked_by.group(1))
            return bind(reader_data["missingPrerequisiteReason"], {"prerequisite": lower_first(prerequisite)})
        if status.startswith("comparison-refused|"):
            return bind(cmp_words["missingRefused"], {"why": status.split("|", 1)[1]})
        if status.startswith("comparison-unmeasured|"):
            parts_ = status.split("|")
            n_ = int(parts_[1])
            said_ = bind(cmp_words["missingUnmeasured"], {"count": fmt_n(n_), "rowNoun": "row" if n_ == 1 else "rows"})
            if len(parts_) > 2 and cmp_words.get("missingUnmeasuredRemoved"):
                said_ += bind(cmp_words["missingUnmeasuredRemoved"], {"mod": parts_[2]})
            return said_
        return reason_labels.get(status, fallback)
    reason_explanations = reader_data.get("missingReasonExplanations") or {}
    # Grouped by status, as the reason is a statement about the status - but each subject names the setup it is
    # missing ON, in the setups-compared list's own words. The disposition's missing list is per arm, so a check
    # supplied on one setup and missing on another is never told as a statement about the evaluation as a whole.
    # The no-problem list below already names the setup this way.
    arm_order = {a["armId"]: i for i, a in enumerate(arms_out)}
    def summarise_missing(rows_in):
        missing_summary = []
        # each setup's check under its own status (several statuses of one check on one setup: one by precedence),
        # never under another setup's
        statuses_by_check = collections.OrderedDict()
        for m in rows_in:
            sts = statuses_by_check.setdefault((str(m.get("checkId")), str(m.get("armId"))), [])
            if str(m.get("status")) not in sts:
                sts.append(str(m.get("status")))
        for status_set in sorted({tuple(sorted(v)) for v in statuses_by_check.values()}):
            rows_ = []
            for m in rows_in:
                if tuple(sorted(statuses_by_check[(str(m.get("checkId")), str(m.get("armId")))])) != status_set:
                    continue
                title = check_titles.get(str(m.get("checkId")), b.check_title(m.get("checkId")))
                rows_.append((title, arm_order.get(m.get("armId"), len(arm_order)),
                              arm_labels.get(m.get("armId"), "Measured setup")))
            subjects = [f"{title} — {setup}" for title, _, setup in sorted(dict.fromkeys(rows_))]
            reason_ = missing_reason(first_status(status_set, (reader_data.get("prediction") or {}).get(
                "missingReasonPrecedence")), "has no accepted result")
            same_ = next((x_ for x_ in missing_summary if x_["reason"] == reason_), None)
            if same_ is not None:
                same_["subjects"] = list(dict.fromkeys(same_["subjects"] + subjects))
                continue
            missing_summary.append({"reason": reason_,
                                    "subjects": subjects,
                                    "explanation": " ".join(x for x in (reason_explanations.get(st) for st in status_set) if x)
                                    or None})
        return missing_summary
    missing_summary = summarise_missing(reader_missing)
    null_summary = []
    comparison_summary = []
    kind_phrases_all = reader_data.get("kindPhrases") or {}

    def bare_phrase(phrase: str) -> str:
        return re.sub(r"^(a|an) ", "", phrase)

    def setup_totals_sentence(comparison) -> str:
        # The totals beside a no-additional-problem comparison. Each number counts the listed rows (records, or a
        # kind's own unit) of this check's findings on one setup: the measured load order's arm part, and this
        # setup's own measurement. They cover every plugin pair the check reported, including pairs the report
        # does not list because they involve neither a target nor a compared patch. Rows of a context-only kind
        # (navmesh overwrite history) are left out of both numbers but still counted by the probe - the evidence
        # index's "out of N conflicts" includes them - so the sentence names what it leaves out.
        def rows_on(arm_id, skip_parts):
            return [x for x in findings if x.get("kind") == "finding" and x.get("checkId") == comparison.get("checkId")
                    and x.get("armId") == arm_id and x.get("part") not in skip_parts]
        before = rows_on("baseline", ("reverse",))
        after = rows_on(comparison.get("armId"), ("diff", "reverse"))
        counted_before = [x for x in before if not x.get("readerEvidenceReason")]
        counted_after = [x for x in after if not x.get("readerEvidenceReason")]
        total_before = sum(int(x.get("rowsTotal") or 0) for x in counted_before)
        total_after = sum(int(x.get("rowsTotal") or 0) for x in counted_after)
        if not (total_before or total_after):
            return ""
        units = {(kind_phrases_all.get(str((x.get("counts") or {}).get("groupKind") or "")) or {}).get("unitPlural")
                 for x in counted_before + counted_after if int(x.get("rowsTotal") or 0)}
        unit_kind = (next((k for k, v in kind_phrases_all.items() if v.get("unitPlural") in units and v.get("unitSingular")), None)
                     if len(units) == 1 and None not in units else None)
        if unit_kind:
            one, many = kind_phrases_all[unit_kind]["unitSingular"], kind_phrases_all[unit_kind]["unitPlural"]
        else:
            one, many = "record with a problem", "records with a problem"
        sentence = (f" In total this check reported {fmt_n(total_before)} {one if total_before == 1 else many} "
                    f"in the measured load order and {fmt_n(total_after)} in this setup")
        if any(x.get("readerRelevant") is False and int(x.get("rowsTotal") or 0) for x in counted_before + counted_after):
            involved = "the target mods" + (" or a compared patch" if supplied else "")
            sentence += f", including conflicts that do not involve {involved}, which this report does not list"
        sentence += "."
        history_before = [x for x in before if x.get("readerEvidenceReason") == "context-only-kind"]
        history_after = [x for x in after if x.get("readerEvidenceReason") == "context-only-kind"]
        hist_before = sum(int(x.get("rowsTotal") or 0) for x in history_before)
        hist_after = sum(int(x.get("rowsTotal") or 0) for x in history_after)
        if hist_before or hist_after:
            kinds_ = {str((x.get("counts") or {}).get("groupKind") or "") for x in history_before + history_after
                      if int(x.get("rowsTotal") or 0)}
            phrase = kind_phrases_all.get(next(iter(kinds_))) if len(kinds_) == 1 else None
            if phrase and phrase.get("singular") and phrase.get("plural"):
                noun_ = bare_phrase(phrase["singular"]) if hist_before == 1 else phrase["plural"]
            else:
                noun_ = "record of overwrite history" if hist_before == 1 else "records of overwrite history"
            sentence += (f" Not counted: {fmt_n(hist_before)} {noun_} in the measured load order and "
                         f"{fmt_n(hist_after)} in this setup, kept in the evidence index as history rather than "
                         "as problems.")
        return sentence

    def holding_sentence(f) -> str:
        # rows that left the class with their defect still there are not "no problem" (the comparison's own count)
        n_ = int((comparison_state.get((f.get("checkId"), f.get("armId"))) or {}).get("stillHolding") or 0)
        return (" " + bind(cmp_words["stillHolding"], {"count": fmt_n(n_), "verb": "is" if n_ == 1 else "are"})
                if n_ else "")

    diff_said = {(f.get("checkId"), f.get("armId")) for f in findings
                 if f.get("kind") == "null" and f.get("part") == "diff" and holding_sentence(f)}
    # one status source: a check and setup with no accepted result is never also listed as having found no problem
    no_result_ = {(str(m.get("checkId")), str(m.get("armId"))) for m in reader_missing}
    for f in findings:
        if f.get("kind") != "null":
            continue
        if (str(f.get("checkId")), str(f.get("armId"))) in no_result_:
            continue
        subject = check_titles.get(str(f.get("checkId")), f.get("title"))
        if f.get("part") in ("diff", "reverse"):
            if f.get("part") == "diff":
                comparison_summary.append(f"{subject} — {f.get('readerArmLabel')}: {f.get('readerPlain')}"
                                          f"{setup_totals_sentence(f)}{holding_sentence(f)}")
            continue
        if holding_sentence(f):
            # Snazzy review 4: rows still physically there are not "no problem"; they are said once, with the
            # comparison (its own line carries the sentence; a check whose comparison has no line gets one here)
            if (f.get("checkId"), f.get("armId")) not in diff_said:
                comparison_summary.append(f"{subject} — {f.get('readerArmLabel')}:{holding_sentence(f)}")
            continue
        context = (f.get("readerPlain") or ("the comparison found no additional issue" if f.get("part") == "diff" else
                                            "no problem was found in the records examined")).rstrip(".")
        if f.get("readerProvenance"):
            context += ". " + str(f["readerProvenance"]).rstrip(".")
        null_summary.append(f"{subject} — {f.get('readerArmLabel')}: {context}")
    null_summary = sorted(set(null_summary))
    refused_at = []                    # (eighth review) where each refused comparison's line sits, to add its re-measure
    for (check_id, arm_id), state in sorted(comparison_state.items()):
        if state["refused"] and arm_id != "baseline":
            refused_at.append((len(comparison_summary), check_id, arm_id, state))
            comparison_summary.append(f"{check_titles.get(str(check_id), b.check_title(check_id))} — "
                                      f"{arm_labels.get(arm_id, 'Measured setup')}: "
                                      + bind(cmp_words["refusedLine"], {"why": refused_words(state)}))
    catalogue_summary = sorted({check_titles.get(str(f.get("checkId")), f.get("title"))
                                for f in findings if f.get("kind") == "catalogue"})
    catalogue_summary += sorted({f"{check_titles.get(str(f.get('checkId')), f.get('title'))} — {f.get('readerArmLabel')}: "
                                 f"{str(f['readerProvenance']).rstrip('.')}"
                                 for f in findings if f.get("kind") == "catalogue" and f.get("readerProvenance")})
    title_subject = ", ".join(target_names) or "Target mod"
    alternative_subject = lower_first(setup_subject(alternative_arms))
    # A patch review names the patch. With no separate patch the setups differ only by the target mods themselves,
    # so the title says what was evaluated against what: the target within the measured load order. The mods its
    # recorded conflicts involve are named in the opening as the main conflicts found, not in the title as the
    # comparison.
    title = (f"{title_subject} with {', '.join(map(patch_label, supplied))} — patch review" if supplied else
             f"{title_subject} within the measured load order — conflict review")
    reader = {"title": title,
              "scopeLine": scope_line, "verdict": verdict, "limitations": limitations,
              "routeCutLeadsWords": (reader_data.get("prediction") or {}).get("routeCutLeads") or {},
              "sectionHeadings": {"regressions": f"Problems introduced by {alternative_subject}",
                                  "changes": f"Changes made by {alternative_subject}"},
              # (ninth review, page fit) with one alternative setup the section heading names it: its entries do not repeat it
              "changesOneSetup": len(alternative_arms) == 1,
              "comparisonMods": comparison_names, "suppliedPatches": supplied, "checkTitles": check_titles,
              "locationOrder": reader_data.get("locationOrder") or {},
              "otherLimits": list(dict.fromkeys(other_limits)),
              "checkExplanations": reader_data.get("checkExplanations") or {},
              "missingSummary": missing_summary, "nullSummary": null_summary,
              "comparisonSummary": comparison_summary,
              "catalogueSummary": catalogue_summary,
              "context": {"evaluatedAt": disp.get("createdUtc") or b.manifest.get("createdUtc"),
                          "statesExamined": (b.plan.get("states") or {}).get("selected") or [],
                          # the plan's own list and the classes the census could not express, in words, once each
                          "statesNotExamined": list(dict.fromkeys(
                              [str(x) for x in (b.plan.get("states") or {}).get("notExplored") or []]
                              + [((reader_data.get("prediction") or {}).get("stateClassWords") or {}).get(
                                  str(x.get("code") or x.get("subject"))) or str(x.get("subject"))
                                 for x in un.get("statesNotExplored") or [] if isinstance(x, dict)]))}}
    # The report leads with the prediction (what adding the evaluated mods will break), then coverage, then the patch
    # as the answer key. The comparison sentence above keeps its words as the validation headline; the reader's
    # verdict -- and so the reel's opening -- is the prediction.
    # the names enrichment: each named object's base and carriers, each base's type and model (W-B2, W-B3)
    names_early_row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names_early = load_json(bundle_dir / names_early_row["path"]) if names_early_row else None
    labels_ = record_labels(b)
    lead = build_prediction(pctx_ := {
        "findings": findings, "recordLabels": labels_, "armOrders": arm_orders, "armIds": arm_ids, "targets": target_names,
        "declared": declared_patches, "patchLabels": patch_labels, "replacedPatches": {
            a_: {side_: [n for n in arm_replaced.get(a_) or [] if replaced_patch_side(a_, n) == side_]
                 for side_ in ("arm", "baseline")} for a_ in arm_changes}, "comparisonKinds": comparison_kinds, "armChanges": arm_changes, "armReplaced": arm_replaced,
        "missing": reader_missing, "unassessed": un, "checkTitles": check_titles, "missingReason": missing_reason,
        "checkTitle": b.check_title, "armLabels": arm_labels,
        "familyTitle": lambda x: (b.families.get(x) or {}).get("title") or str(x),
        "cannotRunChecks": [str(c.get("checkId")) for c in b.plan.get("checks") or []
                            if isinstance(c, dict) and c.get("status") in ("unsupported", "manual")],
        "oldVerdict": verdict, "genericVerdict": generic_verdict, "refusedLines": list(refused_lines),
        "limitations": limitations, "words": reader_data["prediction"],
        "inGame": reader_data.get("inGame") or {}, "kindPhrases": reader_data.get("kindPhrases") or {},
        "mainBundle": b, "sibling": sibling, "siblings": sibling_list, "comparisonState": comparison_state,
        "singleMod": settings.get("singleMod"), "headlineUnit": settings.get("headlineUnit"),
        "names": names_early, "masters": plugin_masters_doc, "orderSetups": list(order_setups or []),
        "rowsPerGroup": settings.get("rowsPerGroup") if settings.get("mode") != "exhaustive" else None,
        "physicalUnfinished": {a_ for a_ in arm_ids
                               if physical_unfinished(ev, missing, a_, reader_data.get("prediction") or {})}})
    # the scale and cause of unfinished testing on the prediction's setup, said in the box first
    pw_ = reader_data.get("prediction") or {}
    p_arm_r2 = lead["prediction"].get("predictionArm") or "baseline"
    lead["prediction"]["incompleteLine"] = incomplete_line(
        ev, missing, p_arm_r2, arm_labels.get(p_arm_r2, "the measured setup"), pw_,
        bind((pw_.get("headings") or {}).get("coverage") or "", {"subject": ""}))
    lead["prediction"]["incompleteReel"] = incomplete_line(
        ev, missing, p_arm_r2, arm_labels.get(p_arm_r2, "the measured setup"), pw_, "")
    # the checks behind that line, each with its own reason: the reel's closing sentence is said when any exists, and
    # its plan's movedToReport lists exactly these
    meas_r2_, miss_r2_ = arm_check_state(ev, reader_missing, p_arm_r2)
    lead["prediction"]["incompleteChecks"] = {
        "total": len(set(miss_r2_) | meas_r2_),
        "checks": [{"checkId": c_, "title": check_titles.get(c_, b.check_title(c_)),
                    "reason": missing_reason(s_, "has no accepted result")} for c_, s_ in miss_r2_.items()]}
    # a verdict per supplied patch and a next step per kind left; report and reel read these
    proper_r2 = {str(q.get("name")).lower(): str(q.get("name")) for a_ in b.plan.get("arms") or []
                 for q in ((a_.get("requested") or {}).get("plugins") or []) if isinstance(q, dict)}
    # each plugin's own master list (emits, else the recorded file's header): a patch no probe measured still has its pair
    lead["prediction"]["whatToDo"] = what_to_do(lead, plugin_masters_doc, pw_, ev, missing, proper_r2,
                                                plan_targets=b.plan.get("targets") or [])
    lead["prediction"]["whatToDoHeading"] = (pw_.get("whatToDo") or {}).get("heading")
    precedent_census(lead, settings.get("precedentDir"), reader_data.get("prediction") or {})
    # what a lost record change means in play, from the fields it is on (skill data fieldMeanings), where nothing does
    fm_ = pw_.get("fieldMeanings") or {}
    for it_ in lead["prediction"].get("items") or []:
        if it_.get("kind") != "LOST-EDIT" or it_.get("inGameText") != pw_.get("inGameMissing") or not pw_.get("inGameFields"):
            continue
        # this clash's own fields: the admitted rows of its check between its own two mods, never the check's pool
        mods_ = {str(m_).lower() for m_ in it_.get("mods") or []}
        own_fields_ = [str(r_.get("field") or "") for r_ in lead["prediction"].get("record") or []
                       if r_.get("checkId") == it_.get("checkId") and r_.get("admission") == "admitted"
                       and (mods_ <= {str(r_.get("A")).lower(), str(r_.get("B")).lower()} if len(mods_) < 2 else
                            {str(r_.get("A")).lower(), str(r_.get("B")).lower()} == mods_)]
        means_ = list(dict.fromkeys(fm_[f_.split(".")[0]] for f_ in own_fields_ if f_.split(".")[0] in fm_))
        if means_:
            it_["inGameText"] = bind(pw_["inGameFields"], {"meanings": and_list(means_)})
    # a report with clashes and no picture says so, rather than leaving the reader to wonder
    lead["prediction"]["noPicturesLine"] = (pw_.get("noPictures") if lead["prediction"].get("items")
                                            and not any(m_.get("assessed") for m_ in media) else None)
    # no all-clear on the alternative setups when none of their physical checks finished
    others_r2 = [a_ for a_ in arm_ids if a_ != p_arm_r2]
    if others_r2 and all(physical_unfinished(ev, missing, a_, pw_) or not arm_check_state(ev, missing, a_)[0]
                         for a_ in others_r2) and any(physical_unfinished(ev, missing, a_, pw_) for a_ in others_r2):
        lead["validation"]["noRegressionLine"] = pw_.get("noRegressionUnknown")
    elif not lead["validation"].get("noRegressionLine"):
        lead["validation"]["noRegressionLine"] = pw_.get("noRegressionDefault")
    # (fourth review, Missives S2) fields a record import narrowed out of its comparison, said with what was not judged
    for fact_ in import_narrowed_facts(imports["rows"], pr, lambda c_: check_titles.get(c_) or b.check_title(c_)):
        if fact_ not in (lead["prediction"].get("missingFacts") or []):
            lead["prediction"].setdefault("missingFacts", []).append(fact_)
    verdict = lead["prediction"]["verdict"]
    # The scope line names the evaluated pair when the counted clashes need both of them, never the third mod with
    # the most rows; the third mods of single-mod clashes shown are named after it.
    pred_ = lead["prediction"]
    sw = reader_data.get("scopeWords") or {}
    if len(pred_["mods"]) > 1 and any(i["attribution"] == "needs-both" for i in pred_["items"]):
        pair = list(pred_["mods"])
        alone_mods = []
        for i in pred_["items"]:
            if i.get("shownAs") == "alone":
                alone_mods += [m for m in i["mods"] if m.lower() not in {x.lower() for x in pair} and m not in alone_mods]
        # a counted clash between one evaluated mod and a third mod (it appears only with both loaded) is said to be
        # counted, never hidden behind "between the pair" (round 4)
        pair_lower = {x.lower() for x in pair}
        third_counted = list(dict.fromkeys(m for i in pred_["items"] if i["attribution"] == "needs-both"
                                           for m in i["mods"] if m.lower() not in pair_lower))
        partner = bind(sw["pair"], {"modA": pair[0], "modB": " and ".join(pair[1:])}) + (
            " " + bind(sw["thirdCounted"], {"mods": ", ".join(third_counted)})
            if third_counted and sw.get("thirdCounted") else "") + (
            " " + bind(sw["alone"], {"mods": ", ".join(alone_mods)}) if alone_mods and sw.get("alone") else "")
        scope_line = scope_head + partner + " " + scope_tail
        reader["scopeLine"] = scope_line
        reader["scopePair"] = pair
        reader["comparisonMods"] = alone_mods
    elif comparison_names and pred_["items"]:
        # the main conflicts named are the mods with a counted clash, by how many; a mod whose rows are all set aside
        # (a value it only carries, the same file in other letters) is not one (Pilgrim's second review, S1)
        counted_ = collections.Counter()
        for i in pred_["items"]:
            for m in i["mods"]:
                if str(m).lower() not in target_set:
                    counted_[m] += int(i.get("count") or 0)
        old_ = f"The main recorded conflicts involving the target are with {compared}. "
        if counted_ and old_ in scope_line:
            names_ = ", ".join(m for m, _ in sorted(counted_.items(), key=lambda x: (-x[1], str(x[0]).lower())))
            scope_line = scope_line.replace(old_, f"The main recorded conflicts involving the target are with {names_}. ")
            reader["scopeLine"] = scope_line
    reader["verdict"] = verdict
    reader["refusedSummary"] = refused_lines       # said again where the page reports no new problem
    reader["prediction"] = lead["prediction"]
    # (sixth review, Snazzy S2, Missives line 157) a comparison's rows still physically there that the prediction's
    # patch-placed pairs account for (a patch places one of the two objects) are said with that reason
    if cmp_words.get("stillHoldingPatchPlaced"):
        placed_n_ = collections.Counter(e.get("checkId") for e in lead["prediction"].get("record") or []
                                        if e.get("withPatch") == "patchPlaced")
        for f in findings:
            n_ = int((comparison_state.get((f.get("checkId"), f.get("armId"))) or {}).get("stillHolding") or 0)
            if not n_ or placed_n_.get(f.get("checkId")) != n_:
                continue
            old_ = bind(cmp_words["stillHolding"], {"count": fmt_n(n_), "verb": "is" if n_ == 1 else "are"})
            new_ = bind(cmp_words["stillHoldingPatchPlaced"], {"count": fmt_n(n_), "verb": "is" if n_ == 1 else "are"})
            title_ = check_titles.get(str(f.get("checkId")), f.get("title"))
            reader["comparisonSummary"] = [x_.replace(old_, new_) if str(x_).startswith(str(title_)) else x_
                                           for x_ in reader.get("comparisonSummary") or []]
    # further evaluations supplied with the report are named with what each measured, never left unsaid (Missives
    # review 4); they are not joined into the counts
    pw_also = reader_data.get("prediction") or {}
    also_lines = []
    for x_ in settings.get("alsoBundles") or []:
        ab_ = Bundle(Path(x_))
        setups_ = []
        for arm_ in ab_.plan.get("arms") or []:
            names_ = [str(q.get("name")) for q in ((arm_.get("requested") or {}).get("plugins") or [])
                      if isinstance(q, dict) and not is_base_game(str(q.get("name")), pw_also)]
            shown_ = ", ".join(names_[:6]) + (f" and {len(names_) - 6} more" if len(names_) > 6 else "")
            setups_.append(bind(pw_also["alsoEvaluationSetup"], {
                "arm": arm_.get("armId"), "plugins": shown_ or pw_also.get("alsoEvaluationBaseOnly") or "the base game only"}))
        also_lines.append(bind(pw_also["alsoEvaluation"], {"name": Path(x_).name, "setups": "; ".join(setups_)}))
        # (fifth review, Sewers S1) a setup of it that loads another version of an evaluated plugin (same name, other
        # bytes): for each counted geometry clash kind between two plugins, how many of those pairs that setup finds
        p_arm_ = (reader.get("prediction") or {}).get("predictionArm")
        main_sha_ = {str(q.get("name")).lower(): str(q.get("sha256") or "").lower()
                     for a_ in b.plan.get("arms") or [] if a_.get("armId") == p_arm_
                     for q in (a_.get("requested") or {}).get("plugins") or [] if isinstance(q, dict)}
        targets_ = {str(m).lower() for m in (reader.get("prediction") or {}).get("mods") or []}
        for arm_ in ab_.plan.get("arms") or []:
            var_ = [str(q.get("name")) for q in (arm_.get("requested") or {}).get("plugins") or []
                    if isinstance(q, dict) and str(q.get("name")).lower() in targets_
                    and main_sha_.get(str(q.get("name")).lower()) not in (None, str(q.get("sha256") or "").lower())]
            if not var_ or not pw_also.get("alsoVariantFinds") or not pw_also.get("alsoVariantPart"):
                continue
            groups_ = collections.OrderedDict()
            for e_ in (reader.get("prediction") or {}).get("record") or []:
                # (eighth review round 2, Sewers 39) every counted group: with one evaluated mod those from it alone
                # are counted too; checks of any family whose rows name two objects
                if (e_.get("admission") == "admitted" and e_.get("other") and e_.get("kind") != "LOST-EDIT"
                        and (len((reader.get("prediction") or {}).get("mods") or []) <= 1
                             or e_.get("attribution") != "single-mod")):
                    groups_.setdefault((e_["checkId"], e_["A"], e_["B"], e_["kind"]), []).append(e_)
            loaded_ = {str(q.get("name")).lower() for q in (arm_.get("requested") or {}).get("plugins") or [] if isinstance(q, dict)}
            parts_v_, none_v_, unmeasured_v_ = [], [], []
            for (c_, a_, b2_, k_), es_ in groups_.items():
                if str(a_).lower() not in loaded_ or str(b2_).lower() not in loaded_:
                    continue                   # a pair that setup does not load: nothing to say
                # (round 3, review12 S2) a check that version ran once per setup is read too
                path_ = ab_.arm_output_path(c_, arm_.get("armId"))
                em_ = ab_.emit(path_) if path_ else None
                if not isinstance(em_, dict):
                    unmeasured_v_.append(f'"{check_titles.get(str(c_), str(c_))}"')     # said, never dropped
                    continue
                there_ = {(str(r_.get("formKey")), frozenset(map(str, r_.get("other") or ())))
                          for cc_ in em_.get("Conflicts") or [] for r_ in conflict_rows(cc_, None)}
                n_ = sum(1 for e_ in es_ if (str(e_["formKey"]), frozenset(map(str, e_.get("other") or ()))) in there_)
                unit_v_ = ((reader_data.get("kindPhrases") or {}).get(k_, {}).get("unitPlural" if len(es_) != 1 else "unitSingular")
                           or bind(pw_also.get("alsoVariantUnitOfCheck") or "{unit}", {
                               "unit": "clashes" if len(es_) != 1 else "clash",        # (round 3) agrees with its count
                               # a kind with no unit of its own names its check, so two checks' groups read apart
                               "title": check_titles.get(str(c_), str(c_))}))
                parts_v_.append(bind(pw_also["alsoVariantPart"], {
                    "found": fmt_n(n_), "count": fmt_n(len(es_)), "unit": unit_v_, "a": a_, "b": b2_}))
                if not n_:
                    # (eighth review, Sewers 39) a counted group that version finds none of, said in plain words
                    none_v_.append(f"{'the ' if len(es_) != 1 else ''}{fmt_n(len(es_))} {unit_v_} between {a_} and {b2_}")
            if parts_v_:
                # one sentence per setup, its parts joined (page budget)
                # (sixth review, Sewers r5 S1) the other version named by the folder that setup loads it from
                folders_ = [Path(str(q.get("path") or "").replace("\\", "/")).parent.name for q in
                            (arm_.get("requested") or {}).get("plugins") or [] if isinstance(q, dict) and str(q.get("name")) in var_]
                also_lines.append(bind(pw_also["alsoVariantFinds"], {"arm": arm_.get("armId"), "plugin": " and ".join(var_),
                                                                     "folder": " and ".join(f_ for f_ in folders_ if f_) or "-",
                                                                     "parts": "; ".join(parts_v_)})
                                  + (" " + bind(pw_also["alsoVariantNone"], {"groups": and_list(none_v_),
                                                                              "be": "are" if len(none_v_) > 1 or not none_v_[0].startswith("1 ") else "is"})
                                     if none_v_ and pw_also.get("alsoVariantNone") else "")
                                  + (" " + bind(pw_also["alsoVariantUnmeasured"], {"checks": and_list(list(dict.fromkeys(unmeasured_v_)))})
                                     if unmeasured_v_ and pw_also.get("alsoVariantUnmeasured") else ""))
    reader["prediction"]["alsoEvaluations"] = also_lines
    # Evidence derived after measurement (bundle standard §8, enrichment): listed in the evidence index with what
    # it was derived from; names reach the plan so a reader's page needs no side input.
    reader["enrichment"] = [{k: e.get(k) for k in ("path", "sha256", "enrichment", "checkId", "armId", "derivedFrom")}
                            for e in b.enrichment()]
    reader["enrichmentLine"] = reader_data.get("enrichmentLine") if reader["enrichment"] else None
    names_row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names_doc = load_json(bundle_dir / names_row["path"]) if names_row else None
    reader["names"] = ({k: names_doc.get(k) for k in ("places", "objects", "mods", "missing", "derivedFrom")}
                       if isinstance(names_doc, dict) else None)
    if reader["names"] is not None and mod_names:
        # the report names a mod as its reel does: one resolution for both (item 36 F2)
        reader["names"]["mods"] = {**(reader["names"].get("mods") or {}), **mod_names}
    reader["coverage"] = lead["coverage"]
    # P53 (accepted under the standing rule, 2026-10-17): in concise mode a check with no result on a setup the
    # coverage section already lists, by reason and with its setups, is not listed again on the page under "what
    # still needs checking"; one sentence points there. `missingSummary` stays complete in the plan;
    # `missingShown` is what the page lists.
    if settings.get("mode") != "exhaustive":
        covered = set(lead["coverage"].get("relevantArms") or [])
        # a check with no result on every setup is said once, in the coverage list, as such (Missives r3 finding 3)
        everywhere_ = missing_everywhere(reader_missing, arm_labels.keys())
        rest = [m for m in reader_missing if m.get("armId") not in covered and str(m.get("checkId")) not in everywhere_]
        reader["missingShown"] = summarise_missing(rest)
        if len(rest) < len(reader_missing):
            reader["missingCoveredLine"] = (reader_data.get("prediction") or {}).get("missingCoveredLine")
    reader["validation"] = lead["validation"]
    # (eighth review, Snazzy 150) a refused comparison whose predicted clashes were re-measured gone says both: the rows
    # measured gone, and that it refused a verdict on the rest
    gone_ = (lead["validation"] or {}).get("measuredGoneRefused") or {}
    for idx_, ck_, arm_, st_ in refused_at:
        n_ = int(gone_.get(ck_) or 0)
        if n_ and arm_ == (lead["validation"] or {}).get("withPatchArm") and cmp_words.get("refusedLineMeasured"):
            # the reader's list may have been rebuilt since (labels rewritten): the refused line is found by its words
            old_ = comparison_summary[idx_]
            head_ = old_.split(": ", 1)[0] if ": " in old_ else ""
            new_ = (head_ + ": " + bind(cmp_words["refusedLineMeasured"], {
                "count": fmt_n(n_), "noun": "clash" if n_ == 1 else "clashes",
                "them": "it" if n_ == 1 else "them", "why": refused_words(st_)}))
            refused_tail_ = bind(cmp_words["refusedLine"], {"why": refused_words(st_)})
            for lst_ in (comparison_summary, reader.get("comparisonSummary")):
                if isinstance(lst_, list):
                    for j_, x_ in enumerate(lst_):
                        if str(x_).endswith(refused_tail_) and str(x_).startswith(check_titles.get(str(ck_), b.check_title(ck_))):
                            lst_[j_] = str(x_)[:-len(refused_tail_)] + new_.split(": ", 1)[1]
    reader["sectionHeadings"].update(lead["headings"])
    pw = reader_data.get("prediction") or {}
    aside_total = sum(aside_counts.values())
    reader["detailAsideLine"] = (bind(pw["detailAsideLine"], {
        "count": fmt_n(aside_total), "resultNoun": "result" if aside_total == 1 else "results",
        "parts": "; ".join(f"{fmt_n(aside_counts[k])} {pw['detailAside'][k]}"
                           for k in ("baseGame", "ownMaster", "sameMod", "countOnly", "noBody") if aside_counts.get(k))})
                                 if aside_total else None)
    # (Pilgrim review 5) a result left out of this list that holds a clash counted at the top is said so, never "set
    # aside": counted from the prediction record's admitted rows by finding
    counted_fids_ = {str(e.get("findingId")) for e in lead["prediction"].get("record") or []
                     if e.get("admission") == "admitted" and e.get("findingId")}
    single_counted = sum(1 for f in findings if f.get("readerDetailAside") == "singleMod" and f.get("readerRelevant")
                         and str(f.get("id")) in counted_fids_)
    reader["detailSingleModLine"] = (bind(pw["detailSingleModCounted" if single_counted else "detailSingleModLine"], {
        "count": fmt_n(single_mod_count), "resultNoun": "result" if single_mod_count == 1 else "results",
        "verb": "is" if single_mod_count == 1 else "are", "counted": fmt_n(single_counted),
        "holds": "holds" if single_counted == 1 else "hold", "rest": fmt_n(single_mod_count - single_counted)})
                                     if single_mod_count else None)
    # the `missing` chapter names every required missing check id, one caption per status group
    missing_by_status: dict = collections.OrderedDict()
    for m in missing:
        if m.get("required", True) is False:
            continue                                     # an optional check's missing row keeps nothing incomplete
        missing_by_status.setdefault(str(m.get("status")), []).append(m)
    missing_lines = []
    for status in sorted(missing_by_status):
        rows_ = missing_by_status[status]
        ids = sorted({str(m.get("checkId")) for m in rows_})
        missing_lines.append(f"{status} ({fmt_n(len(rows_))}): " + ", ".join(ids))
    chapters.append({"id": "C-01", "kind": "opening", "title": "Evaluation of " + ", ".join(t["name"] for t in targets if t.get("name")),
                     "findingIds": [], "mediaIds": [],
                     "captions": [{"text": f"Arms: " + ", ".join(a['armId'] for a in arms_out) + ". " + claim_sentence
                                   + (" Missing: " + "; ".join(missing_lines) + "." if missing_lines else ""),
                                   "evidenceIds": [eid_by_path.get("disposition.json")] if eid_by_path.get("disposition.json") else [],
                                   "mediaId": None}],
                     "plannedSeconds": reel.get("openingSeconds", 6)})
    n = 1
    media_by_id = {m["id"]: m for m in media}
    blocks: dict = collections.OrderedDict()
    for f in findings:
        if f["kind"] in ("regression", "finding") and f.get("readerRelevant"):
            blocks.setdefault((f["checkId"], f["armId"], f.get("part")), []).append(f)
    order = {f["id"]: i for i, f in enumerate(findings)}
    ordered_blocks = sorted(blocks.items(), key=lambda kv: (0 if any(x["kind"] == "regression" for x in kv[1]) else 1,
                                                          kv[0][0] or "", arm_sort_key(kv[0][1] or "", kinds),
                                                          PART_ORDER.get(kv[0][2], 9), order[kv[1][0]["id"]]))

    def seconds_for(mid):
        m = media_by_id.get(mid) if mid else None
        secs = reel.get("imageSeconds", 5)
        if m and isinstance(m.get("sourceInterval"), dict):
            si = m["sourceInterval"]
            try:
                secs = max(1, float(si.get("endSeconds", 0)) - float(si.get("startSeconds", 0)))
            except (TypeError, ValueError):
                pass
        return secs

    # one `finding` chapter per (checkId, armId, part) block; one caption per qualified media item bound to the
    # block, else an unbound scene that needs qualified media or an explicit reviewed omission.
    for (check_id, arm_id, part), fs in ordered_blocks:
        n += 1
        mids = []
        for f in fs:
            for mid in f.get("mediaIds") or []:
                if mid not in mids and mid in media_by_id and media_by_id[mid].get("assessed"):
                    mids.append(mid)
        captions = []
        for mid in mids:
            m = media_by_id[mid]
            owner = next(f for f in fs if mid in (f.get("mediaIds") or []))
            desc = str((m.get("subject") or {}).get("description") or "").strip()
            spoken = (desc + " " if desc else "") + owner["plain"]
            captions.append({"text": spoken, "narration": " ".join(x for x in (owner["readerPlain"], owner.get("readerProvenance")) if x),
                             "storyboard": {"mode": "full-frame"},
                             "evidenceIds": owner.get("evidenceIds") or [], "mediaId": mid})
        if not captions:
            head = fs[0]
            text = head["plain"] if len(fs) == 1 else (
                f"{check_id} on arm {arm_id} ({part or 'arm'}): {len(fs)} conflict group(s), "
                f"{fmt_n(sum(x['rowsTotal'] for x in fs))} row(s). " + head["plain"])
            captions.append({"text": text, "narration": " ".join(x for x in (head["readerPlain"], head.get("readerProvenance")) if x),
                             "storyboard": {"mode": "full-frame"},
                             "evidenceIds": sorted({e for f in fs for e in (f.get("evidenceIds") or [])}), "mediaId": None})
        chapters.append({"id": f"C-{n:02d}", "kind": "finding", "title": f"{fs[0]['title']} - {arm_id} / {part or 'arm'}",
                         "findingIds": [f["id"] for f in fs], "mediaIds": mids, "captions": captions,
                         "plannedSeconds": sum(seconds_for(c["mediaId"]) for c in captions)})
    for a in arms_out:
        oe = a.get("orderEvaluation") or {}
        if oe.get("classification"):
            per = oe.get("perProbe") or {}
            text = (f"Arm {a['armId']}: classification {oe.get('classification')}; " +
                    "; ".join(f"{p}: {v.get('verdict')} repaired {fmt_n(v.get('repaired'))} reclassified {fmt_n(v.get('reclassified'))} "
                              f"created {fmt_n(v.get('created'))}" for p, v in sorted(per.items())) + ". " + str(a.get("payloadNote") or ""))
            fids = [f["id"] for f in findings if f["armId"] == a["armId"] and f["kind"] in ("regression", "repaired", "reclassified")
                    and f.get("readerRelevant")]
            if not fids:
                continue
            n += 1
            chapters.append({"id": f"C-{n:02d}", "kind": "comparison", "title": f"Order evaluation: {a['armId']}", "findingIds": fids,
                             "mediaIds": [], "captions": [{"text": text, "evidenceIds": sorted({e for f in findings if f["id"] in fids for e in f["evidenceIds"]}),
                                                          "mediaId": None}],
                             "plannedSeconds": reel.get("imageSeconds", 5)})
    if missing:
        n += 1
        fids = [f["id"] for f in findings if f["kind"] == "missing"]
        chapters.append({"id": f"C-{n:02d}", "kind": "missing", "title": "Required checks with no accepted result", "findingIds": fids,
                         "mediaIds": [], "captions": [{"text": line, "evidenceIds": [], "mediaId": None} for line in missing_lines],
                         "plannedSeconds": reel.get("imageSeconds", 5) * len(missing_lines)})
    n += 1
    chapters.append({"id": f"C-{n:02d}", "kind": "closing", "title": "What this evaluation does not say", "findingIds":
                     [f["id"] for f in findings if f["kind"] == "unassessed"], "mediaIds": [],
                     "captions": [{"text": claim_sentence
                                   + (" Required checks still missing: " + "; ".join(missing_lines) + "." if missing_lines else "")
                                   + " Not assessed: " + ", ".join(sorted({f.get("area") or "" for f in findings if f["kind"] == "unassessed"})) + ".",
                                   "evidenceIds": [], "mediaId": None}],
                     "plannedSeconds": reel.get("closingSeconds", 8)})
    for chapter in chapters:
        for i, caption in enumerate(chapter.get("captions") or []):
            if chapter["kind"] == "opening":
                caption["narration"] = " ".join([verdict, scope_line] + limitations)
            elif chapter["kind"] == "closing":
                caption["narration"] = " ".join(limitations + ["Review the findings and unfinished checks in the report before choosing this setup."])
            elif chapter["kind"] == "missing":
                status = sorted(missing_by_status)[i]
                subjects = sorted({check_titles.get(str(m.get("checkId")), "an additional check")
                                   for m in missing_by_status[status]})
                reason = missing_reason(status, "Still needs checking")
                caption["narration"] = reason[:1].upper() + reason[1:] + ": " + "; ".join(subjects) + "."
            elif chapter["kind"] == "comparison":
                caption["narration"] = " ".join(f["readerPlain"] for f in findings
                                                 if f["id"] in chapter["findingIds"] and f["kind"] in ("regression", "repaired", "reclassified")) or "The completed checks do not establish an improvement for this setup."
            caption.setdefault("narration", "Review this scene alongside the findings in the report.")
            storyboard = caption.setdefault("storyboard", {})
            storyboard.setdefault("mode", "full-frame")
            storyboard.setdefault("cleanFrame", False)
            storyboard.setdefault("needsAuthorReview", True)
            storyboard.setdefault("visualReview", {"addedText": "none", "subjectVisible": False})

    plan = {
        "schemaVersion": PRESENTATION_SCHEMA,
        "presentationId": None,
        "generatedUtc": None,
        "source": {"bundleDir": bundle_dir_as_given, "planId": b.manifest.get("planId"), "bundleSchema": b.manifest.get("bundleSchema"),
                   "manifestSha256": sha256_file(bundle_dir / "manifest.json"),
                   "dispositionSha256": sha256_file(bundle_dir / b.manifest.get("disposition", "disposition.json")),
                   "ledgerSha256": sha256_file(bundle_dir / b.manifest.get("ledger", "ledger.json")),
                   "verify": {"bundleComplete": bool((verify.get("bundle") or {}).get("complete")),
                              "evaluationComplete": bool((verify.get("evaluation") or {}).get("complete")),
                              "claim": (verify.get("evaluation") or {}).get("claim")},
                   "verifiedFiles": (verify.get("bundle") or {}).get("verified")},
        "compiler": {"name": "evalreport", "version": COMPILER_VERSION, "sourceSha256": compiler_hashes()},
        "settings": settings,
        "scope": scope_out,
        "reader": reader,
        "reelNarration": {
            "opening": {"mediaId": None, "narration": verdict + " " + scope_line,
                        "storyboard": {"mode": "full-frame", "cleanFrame": False, "needsAuthorReview": True,
                                       "visualReview": {"addedText": "none", "subjectVisible": False}}},
            "closing": {"mediaId": None, "narration": " ".join(limitations),
                        "storyboard": {"mode": "full-frame", "cleanFrame": False, "needsAuthorReview": True,
                                       "visualReview": {"addedText": "none", "subjectVisible": False}}}
        },
        "claimSentence": claim_sentence,
        "imports": imports,
        "findings": findings,
        "groups": [groups[k] for k in sorted(groups)],
        "evidence": evidence,
        "media": media + unassessed_media,
        "unassessed": {**un, "media": unassessed_media},
        "omissions": omissions,
        "chapters": chapters,
        "narration": {"source": "template", "validated": True, "refused": []},
    }
    # one spelling of each plugin name in every sentence, the load order's (report and reel alike)
    names_ = [str(q.get("name") if isinstance(q, dict) else q) for a_ in b.plan.get("arms") or []
              for q in (a_.get("requested") or {}).get("plugins") or [] if (q.get("name") if isinstance(q, dict) else q)]
    for k_ in ("reader", "scope", "groups", "findings"):
        plan[k_] = proper_plugin_case(plan[k_], names_)
    # (item 1) a record given by its form key is said in words (said_record) in every sentence; the form keys stay in
    # the evidence index and in the rows' own fields
    pw_say_ = (pr.get("reader") or {}).get("prediction") or {}
    sayer_ =pctx_.get("sayRecord") or (lambda fk_: (said_record(fk_, names_early or {}, labels_, pw_say_, lambda w_, f_: None),
                                                      None, labels_.get(fk_)))
    for k_ in ("reader", "groups", "findings"):
        plan[k_] = say_form_keys(plan[k_], sayer_, names_ + [str(fk_).split(":", 1)[1] for fk_ in labels_ if ":" in str(fk_)],
                                 pw_say_, (names_early or {}).get("objects") or {})
    for m_ in plan.get("media") or []:
        if isinstance(m_, dict) and isinstance(m_.get("caption"), str):
            m_["caption"] = say_form_keys(m_["caption"], sayer_, names_, pw_say_, (names_early or {}).get("objects") or {})
    return plan


SCENE_POLICY = HERE.parent / ".claude" / "skills" / "evaluation-reporting" / "references" / "scene-policy.json"


def bundle_media_rows(b: "Bundle", arms: set) -> list[dict]:
    """A bundle's admitted media for some of its arms, with absolute paths (a sibling bundle's frames for the reel)."""
    rows = []
    for e in b.evidence:
        if e.get("strength") != "supplied-media" or (e.get("subject") or {}).get("armId") not in arms:
            continue
        sha = e.get("sha256") or ""
        rows.append({"id": f"M-{sha[:12]}", "manifestId": e.get("id"), "path": str((b.dir / e.get("path")).resolve()),
                     "sha256": sha, "mediaKind": e.get("mediaKind"), "subject": e.get("subject"), "state": e.get("state"),
                     "configurationFingerprint": e.get("configurationFingerprint"), "provenance": e.get("provenance"),
                     "assessed": True, "fromBundle": str(b.dir)})
    return rows


def reel_markers(plan: dict, names: dict, plans: list[dict], pr: dict) -> set:
    """The references of the reel's clashes that the report's marker rule calls editor-only: the base's model is one the
    game does not draw (skill data markerModels), or the base carries the marker record flag (markerFlags), read from
    the base's own plugin file as the evaluation recorded it (path and sha256 must match)."""
    words = (pr.get("reader") or {}).get("prediction") or {}
    models = {str(x).replace("\\", "/").lower() for x in (words.get("markerModels") or {}).get("models") or []}
    flags, rtypes = words.get("markerFlags") or {}, words.get("markerRecordTypes") or {}
    files = {}
    for pl in plans:
        for arm in (pl or {}).get("arms") or []:
            for x in ((arm.get("requested") or {}).get("plugins") or []):
                files.setdefault(str(x.get("name")).lower(), (str(x.get("path") or ""), str(x.get("sha256") or "").lower()))
    refs, bases = (names or {}).get("refs") or {}, (names or {}).get("bases") or {}
    rows = [r for r in (((plan.get("reader") or {}).get("prediction") or {}).get("record") or []) if r.get("admission") == "admitted"]
    out, checked = set(), {}
    for fk in sorted({str(r["formKey"]) for r in rows} | {str(o) for r in rows for o in r.get("other") or []}):
        b_ = (refs.get(fk) or {}).get("base")
        base = bases.get(b_ or "") or {}
        model = str(base.get("model") or "").replace("\\", "/").lower()
        if model and (model in models or (model.startswith("meshes/") and model[7:] in models)):
            out.add(fk)
            continue
        bit = flags.get(str(base.get("type") or ""))
        if not b_ or not bit:
            continue
        if b_ not in checked:
            path, sha = files.get(str(b_).split(":", 1)[-1].lower(), ("", ""))
            got = None
            if path and sha and Path(path).is_file() and sha256_file(Path(path)).lower() == sha:
                try:
                    got = pluginread.record_flags(path, [b_], str(rtypes.get(str(base.get("type")), "STAT")).encode()).get(b_)
                except pluginread.PluginReadError:
                    got = None
            checked[b_] = bool(got is not None and int(got) & int(bit))
        if checked[b_]:
            out.add(fk)
    return out


def buried_lines(plan: dict, captures: list[dict], names: dict, words: dict) -> int:
    """F8: each prediction item one of whose clashes a capture found buried (reel_plan.buried_participant: unseen from
    every placed eye-level station and from above) says so where its picture would be, with the count of camera spots
    it rests on. The clash stays in the item and in every count; only the reason there is no picture is added. Returns
    how many items got a line."""
    import reel_plan
    tmpl = str(words.get("buried") or "")
    pred = (plan.get("reader") or {}).get("prediction") or {}
    if not tmpl or not captures:
        return 0
    refs, bases, objects = ((names or {}).get(k) or {} for k in ("refs", "bases", "objects"))

    def obj(fk):
        eid = (bases.get((refs.get(str(fk)) or {}).get("base") or "") or {}).get("editorId")
        # the object in the words its item says it by (record_object_name: in-game name, else the editor ID with its
        # numbers kept), so one object never reads under two names
        return (objects.get(eid) or {}).get("name") or (id_words(eid) if eid else None) or str(fk)
    by_finding = {}
    for r_ in pred.get("record") or []:
        if r_.get("findingId") and r_.get("formKey"):
            b_ = reel_plan.clash_buried([r_["formKey"], *(r_.get("other") or [])], captures)
            if b_:
                by_finding.setdefault(r_["findingId"], []).append(b_)
    n = 0
    for g in pred.get("items") or []:
        found = [b_ for fid in g.get("findingIds") or [] for b_ in by_finding.get(fid, [])]
        lines = list(dict.fromkeys(fill(str(words.get("buriedSeeThrough") if b_.get("partnerSeeThrough") and words.get("buriedSeeThrough")
                                       else tmpl), object=obj(b_["formKey"]), partner=obj(b_["partner"]) if b_["partner"] else "another object",
                                               count=b_["stations"], plural="" if b_["stations"] == 1 else "s",
                                               verb="sees" if b_["stations"] == 1 else "see")
                                   for b_ in found))
        if lines:
            g["buriedLines"] = lines
            n += 1
    return n


def reel_block(plan: dict, bundle: Path, sibling: Path | None, pr: dict, a, out: Path, resolved=None,
               siblings_all=None) -> dict:
    """The review reel's plan (reel_plan.py) from the compiled presentation, the bundle's names and masters, the
    skill's vignette policy and words, the recorded patch search and the evaluation's mod names."""
    import reel_plan
    b = Bundle(bundle)
    names_row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names = load_json(bundle / names_row["path"]) if names_row else {}
    masters = plugin_masters(b) or {}
    sb = Bundle(sibling) if sibling is not None else None
    # every joined sibling, with the prefix the plan names its setups by ("sibling:", "sibling2:", ...), in the order
    # given; the primary's masters first (item 21 follow-up: media and masters from every sibling, not the first only)
    sibs = [(Path(p_), str(x_)) for p_, x_ in (siblings_all or ([(sibling, "sibling:")] if sibling is not None else []))]
    for s_ in ([sb] if sb is not None else []) + [Bundle(p_) for p_, _ in sibs if sibling is None or Path(p_) != Path(sibling)]:
        for k, v in (plugin_masters(s_) or {}).items():
            masters.setdefault(k, v)
    policy = dict((load_json(SCENE_POLICY) or {}).get("vignettes") or {})
    policy["effectModelPattern"] = ((pr.get("reader") or {}).get("prediction") or {}).get("effectModelPattern")
    words = (pr.get("reel") or {}).get("words") or {}
    mod_names, names_doc = resolved if resolved is not None else resolve_mod_names(bundle, sibling, a, out)
    import modnames
    print(modnames.summary(mod_names), file=sys.stderr)
    names_ev = out / "evidence" / "mod-names.json"
    write_json(names_ev, names_doc)
    search, search_ref = None, None
    if getattr(a, "patch_search", None):
        sp = Path(a.patch_search)
        search = load_json(sp)
        if not isinstance(search, dict) or not isinstance(search.get("pairs"), list):
            raise InputRefused(f"patch-search-unreadable: {sp} is not a patch-search document with a `pairs` list")
        dest = out / "evidence" / "patch-search.json"
        write_json(dest, search)
        search_ref = {"path": dest.relative_to(out).as_posix(), "sha256": sha256_file(dest), "source": str(sp)}
    return _reel_block_rest(plan, bundle, sibling, pr, a, out, b, names, masters, sb, policy, words, mod_names,
                            names_ev, search, search_ref, sibs)


def resolve_mod_names(bundle: Path, siblings, a, out: Path) -> tuple[dict, dict]:
    """Each mod's name, for the report and the reel alike: declared (the bundle's names or --mod-name), else its Nexus
    page's (the catalogue, read offline or fetched when asked), else its MO2 folder's with the reason no Nexus name was
    found; the lookups are kept beside the reel (modnames.py)."""
    import modnames
    b = Bundle(bundle)
    names_row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names = load_json(bundle / names_row["path"]) if names_row else {}
    sbs = [Bundle(x) for x in (siblings if isinstance(siblings, (list, tuple)) else [siblings]) if x is not None]
    declared = {k: v["name"] for k, v in ((names or {}).get("mods") or {}).items() if v.get("source") == "declared"}
    for pair in list(getattr(a, "mod_name", None) or []):
        if "=" not in pair:
            raise InputRefused(f"mod-name-invalid: --mod-name takes PLUGIN=NAME, got {pair!r}")
        k, v = pair.split("=", 1)
        declared[k.strip()] = v.strip()
    nexus_mode = getattr(a, "nexus_names", None) or "offline"
    reader_ = getattr(a, "nexus_reader_cmd", None) or os.environ.get("HOUSECARL_RECORD_READER_CMD") or None
    if nexus_mode == "fetch" and not reader_:
        exe_ = HERE.parent / "src" / "housecarl-mcp" / "bin" / "Release" / "net9.0" / ("housecarl-mcp.exe" if os.name == "nt" else "housecarl-mcp")
        reader_ = f'"{exe_}"' if exe_.is_file() else None
    mod_names, names_doc = modnames.resolve([b.plan] + [x.plan for x in sbs],
                                            ((b.scope or {}).get("targets") or [])
                                            + [t for x in sbs for t in ((x.scope or {}).get("targets") or [])],
                                            declared, nexus_mode, reader_, out / "evidence" / "mod-names")
    return mod_names, names_doc


def _reel_block_rest(plan, bundle, sibling, pr, a, out, b, names, masters, sb, policy, words, mod_names, names_ev,
                     search, search_ref, sibs=()):
    """reel_block's body after the names and the patch search are read."""
    import reel_plan
    captures = []
    for f in list(getattr(a, "capture_results", None) or []):
        doc = load_json(Path(f))
        captures += doc if isinstance(doc, list) else [doc] if isinstance(doc, dict) else []
    pred0_ = (plan.get("reader") or {}).get("prediction") or {}
    records_ = [r_ for r_ in [pred0_.get("sibling")] + list(pred0_.get("siblings") or []) if isinstance(r_, dict)]
    extra_media = []
    for path_, prefix_ in sibs:
        # the setups this sibling plays a role in (its own arm ids), and the prediction's own setups it holds
        own_ = {a_ for r_ in records_ if r_.get("pathAsGiven") and Path(str(r_["pathAsGiven"])) == Path(path_) for a_ in (r_.get("roles") or {}).values()}
        own_ |= {str(x_)[len(prefix_):] for x_ in [pred0_.get("withoutArm"), pred0_.get("predictionArm"),
                                                    *((pred0_.get("withoutPartnerArms") or {}).values())]
                 if str(x_ or "").startswith(prefix_)}
        for m_ in bundle_media_rows(Bundle(path_), own_):
            # the setup id as the plan names it: prefixed, so a sibling's setup is never read as this bundle's
            m_["subject"] = dict(m_.get("subject") or {}, armId=prefix_ + str((m_.get("subject") or {}).get("armId")))
            m_["setupFrom"] = str(path_)
            extra_media.append(m_)
    view = dict(plan, media=list(plan.get("media") or []) + extra_media)
    markers = reel_markers(plan, names, [b.plan] + [Bundle(p_).plan for p_, _ in sibs], pr)
    # the prediction setup's switch census: a world-state clash's condition is read from its switch's trigger
    arm = str(((plan.get("reader") or {}).get("prediction") or {}).get("predictionArm") or "")
    census_bundle, census_arm = next(((p_, arm[len(x_):]) for p_, x_ in sibs if arm.startswith(x_)), (bundle, arm))
    census_path = (census_bundle / "evidence" / "outputs" / "state.switch-census" / f"{census_arm}.json") if census_bundle else None
    switches = {}
    if census_path is not None and census_path.is_file():
        for sw in (load_json(census_path) or {}).get("switches") or []:
            if isinstance(sw, dict) and sw.get("key"):
                switches[sw["key"]] = {"edid": sw.get("edid"), "baselineEnabled": sw.get("baselineEnabled"),
                                       "trigger": sw.get("trigger")}
    # the patch search said in the report too, with the same cautions as the reel: one line per Nexus page (R3, N1)
    pred_ = (plan.get("reader") or {}).get("prediction") or {}
    if search and words.get("reportLead"):
        arm_ = next((a_ for a_ in (plan.get("scope") or {}).get("arms") or [] if a_.get("armId") == pred_.get("predictionArm")), {})
        loaded_ = {str(x).lower() for x in arm_.get("plugins") or []}
        supplied_ = [str(x) for x in (plan.get("reader") or {}).get("suppliedPatches") or []]
        try:
            leads_ = patch_search_leads(search, words, loaded_, supplied_, masters)
        except reel_plan.Refused as ex:
            raise InputRefused(f"patch-search-unreadable: {ex}")
        if leads_:
            # the search file names plugins by the file on disk; said in the load order's spelling, as every line is
            leads_ = proper_plugin_case(leads_, [str(q.get("name") if isinstance(q, dict) else q)
                                                 for a_ in b.plan.get("arms") or []
                                                 for q in (a_.get("requested") or {}).get("plugins") or []])
            pred_.setdefault("whatToDo", [])
            pred_["whatToDo"] = list(pred_["whatToDo"]) + [x for x in leads_ if x not in pred_["whatToDo"]]
            pred_["whatToDoHeading"] = pred_.get("whatToDoHeading") or ((pr.get("reader") or {}).get("prediction") or {}).get("whatToDo", {}).get("heading")
    reel = reel_plan.plan_reel(view, names, masters, policy, words, search, mod_names, captures, markers=markers,
                               switches=switches, captures_final=bool(getattr(a, "captures_final", False)))
    reel["switchCensus"] = ({"path": str(census_path), "sha256": sha256_file(census_path), "switches": len(switches)}
                            if census_path is not None and census_path.is_file() else None)
    reel["media"] = extra_media
    reel["patchSearch"] = search_ref
    reel["modNames"] = mod_names
    reel["modNamesEvidence"] = {"path": names_ev.relative_to(out).as_posix(), "sha256": sha256_file(names_ev)}
    reel["captureResults"] = [{"formKey": c.get("formKey"), "status": c.get("status"), "reason": c.get("reason")} for c in captures]
    reel["buriedItems"] = buried_lines(plan, captures, names, (pr.get("reader") or {}).get("figureCaptions") or {})
    return reel


def compiler_hashes() -> dict:
    out = {"evalreport.py": sha256_file(Path(__file__).resolve())}
    for t in sorted(TEMPLATES.glob("*")) if TEMPLATES.exists() else []:
        out[f"templates/{t.name}"] = sha256_file(t)
    out["report-priorities.json"] = sha256_file(PRIORITIES)
    out["scene-policy.json"] = sha256_file(SCENE_POLICY)
    out["reel_plan.py"] = sha256_file(HERE / "reel_plan.py")
    return out


def stamp(plan: dict) -> dict:
    body = {k: v for k, v in plan.items() if k not in ("presentationId", "generatedUtc")}
    plan["presentationId"] = sha256_text(canonical(body))[:12]
    plan["generatedUtc"] = utc_now()
    return plan


# ------------------------------------------------------------------------------------------ render --

def env(autoescape: bool):
    if jinja2 is None:
        raise SystemExit("REFUSED: jinja2 is not installed; the templates cannot be rendered (pip install jinja2)")
    e = jinja2.Environment(loader=jinja2.FileSystemLoader(str(TEMPLATES)), autoescape=autoescape,
                           trim_blocks=True, lstrip_blocks=True, keep_trailing_newline=True, undefined=jinja2.StrictUndefined)
    e.filters["n"] = fmt_n
    e.filters["slug"] = slug
    e.filters["md"] = lambda s: str(s if s is not None else "").replace("|", "\\|").replace("\n", " ")
    # the HTML index's counterpart to `md`: autoescaping handles the escaping, so all that is left is the
    # None-to-empty rule, which keeps an absent value blank in both indexes rather than printing "None"
    e.filters["txt"] = lambda s: str(s if s is not None else "")
    e.filters["short"] = lambda s, n=96: (str(s) if s is not None else "")[:n] + ("..." if s and len(str(s)) > n else "")
    e.filters["kv"] = fmt_kv
    e.filters["sentences"] = sentence_cut
    e.filters["tailpath"] = tail_path
    return e


def sentence_cut(s, n=220) -> str:
    """Cut at a sentence boundary within n characters; empty when no boundary falls past 40% of n."""
    s = str(s or "").strip()
    if len(s) <= n:
        return s
    head = s[:n]
    cut = max(head.rfind(". "), head.rfind("; "))
    if cut < int(n * 0.4):
        return ""
    return head[:cut + 1]


def tail_path(p) -> str:
    """`<supplier folder>/<basename>` - a shareable rendition carries no machine path."""
    parts = [x for x in re.split(r"[\\/]+", str(p or "")) if x]
    return "/".join(parts[-2:]) if len(parts) > 2 else "/".join(parts)


def fmt_kv(d) -> str:
    """`{'NoGeometry': 65}` -> `NoGeometry 65`; an empty or all-zero mapping reads `none`."""
    if not isinstance(d, dict):
        return "none" if d in (None, {}, [], "") else str(d)
    items = [(k, v) for k, v in d.items()]
    if not items or all((isinstance(v, (int, float)) and v == 0) for _, v in items):
        return "none"
    return ", ".join(f"{k} {fmt_n(v)}" for k, v in items)


def once_on_page(reader: dict, pr: dict) -> dict:
    """W-B1 (P53): the concise page states a sentence every prediction item repeats once, after the headline (the
    reuse of the result a count rests on; a check whose in-game look is not described), and a check that found no
    problem in its short form. The plan keeps every item's full sentence; only the page is shorter."""
    conc, words = pr.get("concise") or {}, ((pr.get("reader") or {}).get("prediction") or {})
    pred = dict(reader.get("prediction") or {})
    reused, missing_look, items = [], [], []
    for g in pred.get("items") or []:
        g = dict(g)
        if g.get("shownAs") != "alone":
            if words.get("itemReused") and g.get("plain", "").endswith(" " + words["itemReused"]):
                g["plain"] = g["plain"][:-len(words["itemReused"]) - 1]
                reused.append(g.get("title"))
            if words.get("inGameMissing") and g.get("inGameText") == words["inGameMissing"]:
                g["inGameText"] = ""
                missing_look.append(g.get("title"))
        items.append(g)
    pred["items"] = items
    # the attribution every shown item shares, said once before the others (each item keeps it in the plan)
    nb = ((words.get("attribution") or {}).get("needs-both") or "")
    shown = [g for g in items if g.get("shownAs") != "alone"]
    lead_once = []
    if nb and len(shown) > 1 and conc.get("onceNeedsBoth") and all(f" {nb} " in g.get("plain", "") + " " for g in shown):
        for g in shown:
            g["plain"] = (g["plain"] + " ").replace(f" {nb} ", " ", 1).rstrip()
        lead_once.append(conc["onceNeedsBoth"])
    # "whether it also needs X was not measured" repeated on every item (Clockwork review 5, P53): said once, for the
    # items it applies to; an item with any other attribution keeps its own sentence
    wt = [g for g in shown if g.get("attributionKey") == "with-target" and g.get("attributionText")
          and f" {g['attributionText']} " in g.get("plain", "") + " "]
    if len(wt) > 1 and conc.get("onceWithTarget"):
        for g in wt:
            g["plain"] = (g["plain"] + " ").replace(f" {g['attributionText']} ", " ", 1).rstrip()
        lead_once.append(bind(conc["onceWithTarget"], {"mod": wt[0].get("attributionMod") or "",
                                                       "count": fmt_n(len(wt)),
                                                       "all": "every item" if len(wt) == len(shown) else
                                                       bind(conc.get("onceWithTargetSome") or "{count} items", {
                                                           "count": fmt_n(len(wt)),
                                                           # (Pilgrim review 2) the items named by their other mods
                                                           "others": and_list(list(dict.fromkeys(
                                                               m_ for g in wt for m_ in g.get("mods") or []
                                                               if m_.lower() != str(g.get("attributionMod") or "").lower())))})}))

    def titles(xs):
        xs = list(dict.fromkeys(f'"{x}"' for x in xs if x))
        return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]
    once = []
    if (reused and missing_look and titles(reused) == titles(missing_look) and conc.get("onceReusedInGameMissing")):
        # the same checks both reused and without an in-game description: said in one sentence (page budget)
        once.append(bind(conc["onceReusedInGameMissing"], {"titles": titles(reused)}))
        reused, missing_look = [], []
    if reused and conc.get("onceReused"):
        once.append(bind(conc["onceReused"], {"titles": titles(reused)}))
    if missing_look and conc.get("onceInGameMissing"):
        once.append(bind(conc["onceInGameMissing"], {"titles": titles(missing_look)}))
    pred["onceLines"] = lead_once + once
    out = dict(reader)
    out["prediction"] = pred
    if conc.get("nullLong") and conc.get("nullShort"):
        out["nullSummary"] = [x.replace(conc["nullLong"], conc["nullShort"]) for x in reader.get("nullSummary") or []]
    # a patch item line that only repeats the kind line at the top (its kind has this one counted item) is said once,
    # at the top; the plan keeps every item line
    val_ = reader.get("validation") or {}
    kls = pred.get("withPatchLines") or []
    if kls and val_.get("itemLines"):
        kind_titles = {re.sub(r" \([\d,]+ [^)]*\): .*$", "", k_) for k_ in kls}
        per_title = collections.Counter(t_ for x in val_["itemLines"] for t_ in kind_titles if str(x).startswith(t_ + ", "))
        keep = [x for x in val_["itemLines"]
                if not any(str(x).startswith(t_ + ", ") and per_title.get(t_) == 1 for t_ in kind_titles)]
        if len(keep) < len(val_["itemLines"]):
            out["validation"] = {**val_, "itemLines": keep}
    # a check with the same no-problem result on several setups is said once with those setups named, and checks
    # with the same result on the same setups share one line (the page states each fact once; the plan keeps one line
    # per setup)
    labels, by_check, other = [], collections.OrderedDict(), []
    for x in out.get("nullSummary") or []:
        # a setup's label can hold ": " itself (a named alternative), the result is after the last one
        m = re.match(r"^(.+?) — (.+): (.+)$", str(x))
        if not m:
            other.append(x)
            continue
        if m.group(2) not in labels:
            labels.append(m.group(2))
        by_check.setdefault(m.group(1), collections.OrderedDict())[m.group(2)] = m.group(3)
    if conc.get("nullSetups"):
        merged = collections.OrderedDict()
        for check, per in by_check.items():
            ctxs = set(per.values())
            if len(per) > 1 and len(ctxs) == 1:
                merged.setdefault((tuple(lab for lab in labels if lab in per), next(iter(ctxs))), []).append(check)
            else:
                other += [f"{check} — {lab}: {c}" for lab, c in per.items()]
        out["nullSummary"] = sorted(other) + [
            bind(conc["nullSetups"], {"checks": "; ".join(cs), "setups": " and ".join(labs), "result": c})
            for (labs, c), cs in merged.items()]
    return out


NAVMESH_STATES = ("winning-linked-navmeshes", "historical-navmesh-context")


def navmesh_earlier_plugin(request_media: list[dict]) -> str | None:
    """The plugin a navmesh comparison's historical frame draws the earlier version from: reel_plan's one parser."""
    import reel_plan
    return reel_plan.navmesh_earlier_plugin(request_media)


def figure_units(figs: list[dict]) -> list[list[dict]]:
    """A finding's figures as units: the frames of one capture request are one unit, and the unit shows the frames
    that state the claim (Missives review R2): a vignette's marked view, else its with-frame; a split's two halves,
    left then right. Frames of no capture request are a unit each, in order."""
    units, by_req = [], collections.OrderedDict()
    for m in figs:
        req = m.get("captureRequest") or ((m.get("provenance") or {}).get("captureRequest"))
        if req:
            by_req.setdefault(req, []).append(m)
        else:
            units.append([m])
    for frames in by_req.values():
        st = {(m.get("captureState") or (m.get("provenance") or {}).get("vignetteState")): m for m in frames}
        if "left" in st or "right" in st:
            units.append([st[k] for k in ("left", "right") if k in st])
        elif any(k in st for k in ("without", "with", "highlight")):
            # a vignette is its plain pair and its marked view together: what the player sees, with and without the
            # mod, then the marked shapes; one unit, so a figure limit never keeps only the marked view
            units.append([st[k] for k in ("without", "with", "highlight") if k in st])
        elif any(k in st for k in NAVMESH_STATES):
            # a navmesh comparison is its winning frame and the earlier version at the same camera: one unit
            units.append([st[k] for k in NAVMESH_STATES if k in st])
        else:
            units.append(frames[:1])
    return units


def media_unaccounted(plan: dict, view: dict, report_md: str) -> list[str]:
    """The assessed media of the plan that the report neither shows nor names in an omission line: a picture produced
    and admitted is on the page or said to be left out, never dropped unsaid."""
    said = " ".join(str(o.get("what") or "") for o in view.get("omissions") or [])
    out = []
    for m in plan.get("media") or []:
        if not m.get("assessed"):
            continue
        shown = bool(m.get("reportPath")) and str(m["reportPath"]) in report_md
        name = Path(str(m.get("path") or "")).name
        if not shown and not (name and f"media/{name}" in report_md) and str(m.get("id")) not in said:
            out.append(str(m.get("id")))
    return out


def effective_view(plan: dict, rows_per_group: int, figures_per_finding: int, pr: dict, layout: str = "normal") -> dict:
    """The plan as the templates see it at one ladder step: rows shown per finding, figures per finding,
    omissions recomputed for the effective settings (exhaustive mode: everything, no ladder)."""
    mode = plan["settings"]["mode"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")
    media_by_id = {m["id"]: m for m in plan.get("media") or []}
    reel_media = {str(fr.get("mediaId")) for c in (plan.get("reel") or {}).get("chapters") or []
                  for fr in c.get("frames") or [] if fr.get("mediaId")}
    findings = []
    omissions = []
    figures_by_finding: dict = {}
    for f in plan["findings"]:
        g = dict(f)
        if mode == "exhaustive":
            shown = f["rowsTotal"]
        elif f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            shown = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
        else:
            shown = min(f["rowsTotal"], rows_per_group)
            if shown < f["rowsTotal"]:
                omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - shown)} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                                  "why": f"concise mode lists {rows_per_group} row(s) per group", "where": f"evidence-index.md#{f['id']}"})
        g["rowsShown"] = shown
        g["shownRows"] = (f.get("allRows") or f.get("rows") or [])[:shown]
        figs = [media_by_id[m] for m in (f.get("mediaIds") or []) if m in media_by_id and media_by_id[m].get("assessed")]
        units = figure_units(figs)
        # the vignettes the reel shows come first, so a figure limit never leaves out a picture the reel shows
        units.sort(key=lambda u_: not any(str(m_.get("id")) in reel_media for m_ in u_))
        if mode != "exhaustive" and len(units) > figures_per_finding:
            left_ = [str(m_.get("id")) for u_ in units[figures_per_finding:] for m_ in u_]
            omissions.append({"what": f"{f['id']}: {len(units) - figures_per_finding} of {len(units)} figure(s) not shown "
                                      f"({', '.join(left_)})",
                              "why": f"concise mode shows {figures_per_finding} figure(s) per finding", "where": f"evidence-index.md#{f['id']}"})
            units = units[:figures_per_finding]
        g["figures"] = [m for u in units for m in u]
        findings.append(g)
        figures_by_finding[f["id"]] = g["figures"]
    sections = []
    for s in sorted(pr.get("sections") or [], key=lambda s: s.get("order", 99)):
        kinds = set(s.get("kinds") or [])
        areas = set(s.get("areas") or [])
        fs = [f for f in findings if f["kind"] in kinds and (not areas or f.get("area") in areas)]
        sections.append({**s, "findings": fs})
    # section 2, compact: one row per (status, check, cause) with the arms joined
    missing_rows = []
    seen = collections.OrderedDict()
    for f in findings:
        if f["kind"] != "missing":
            continue
        key = (str(f["counts"].get("status")), str(f["counts"].get("cause")))
        if key not in seen:
            seen[key] = {"status": key[0], "cause": f["counts"].get("cause"), "checks": collections.OrderedDict(), "findingIds": []}
        seen[key]["checks"].setdefault(f.get("checkId"), []).append(f.get("armId"))
        seen[key]["findingIds"].append(f["id"])
    for key in sorted(seen):
        row = seen[key]
        row["checks"] = [{"checkId": c, "arms": a} for c, a in row["checks"].items()]
        row["count"] = len(row["findingIds"])
        missing_rows.append(row)
    # section 3, compact: per check, per (arm, part) block with its groups
    blocks = collections.OrderedDict()
    detail_aside = []
    for f in findings:
        if f["kind"] != "finding" or not f.get("readerRelevant"):
            continue
        if f.get("readerDetail") is False:                  # not a clash between two mods: the evidence index lists it
            detail_aside.append(f)
            continue
        cb = blocks.setdefault(f["checkId"], {"checkId": f["checkId"], "title": f.get("title"),
                                              "check": (plan["scope"].get("checks") or {}).get(f["checkId"]) or {},
                                              "parts": collections.OrderedDict()})
        pk = (f.get("armId"), f.get("part"))
        part = cb["parts"].setdefault(pk, {"armId": f.get("armId"), "part": f.get("part"), "findingsInPart": f["counts"].get("findingsInPart"),
                                           "verdict": f.get("verdict"), "strength": f.get("strength"), "evidenceIds": f.get("evidenceIds") or [],
                                           "indexOnly": (mode != "exhaustive" and f.get("part") == "reverse" and reverse_rule == "index-only"),
                                           "population": f["counts"].get("population"), "state": f["counts"].get("state"),
                                           "groups": [], "rowsTotal": 0})
        part["groups"].append(f)
        part["rowsTotal"] += f["rowsTotal"]
    finding_blocks = []
    arm_kinds = {a["armId"]: a.get("kind") for a in plan["scope"]["arms"]}
    conc = pr.get("concise") or {}
    phrases = (pr.get("reader") or {}).get("kindPhrases") or {}
    compact_over = int(conc.get("compactGroupsOver", 3))
    # a detail line on the prediction setup says how many of its rows the prediction counts, so a larger number here
    # never reads as a different count from the one at the top (BPT review S4)
    pred_ = (plan.get("reader") or {}).get("prediction") or {}
    counted_by_fid = collections.Counter(e.get("findingId") for e in pred_.get("record") or []
                                         if e.get("admission") == "admitted" and e.get("findingId"))
    # (fifth review, Winterhold S1) with two evaluated mods, rows already there with one mod alone are not counted at the top
    alone_by_fid = (collections.Counter(e.get("findingId") for e in pred_.get("record") or []
                                        if e.get("admission") == "admitted" and e.get("findingId")
                                        and e.get("attribution") == "single-mod")
                    if len(pred_.get("mods") or []) > 1 else collections.Counter())
    pred_arm_ = pred_.get("predictionArm")

    # (third review: Winterhold S4, Clockwork S2, BPT S4) a detail line on the setup with the patch says the same, its rows
    # joined to the prediction's rows by identity (the object and its partners, or the record field, and the two mods)
    with_arm_ = ((plan.get("reader") or {}).get("validation") or {}).get("withPatchArm")
    multi_ = len(pred_.get("mods") or []) > 1
    f_by_id = {f["id"]: f for f in findings if f.get("id")}

    def ident_(check, fk, field, other, a, b):
        return (check, str(fk), field if field else tuple(sorted(str(x) for x in other or [])),
                frozenset((str(a).lower(), str(b).lower())))
    pred_ident = {}
    for e_ in pred_.get("record") or []:
        k_ = ident_(e_["checkId"], e_["formKey"], e_.get("field") if e_["kind"] == "LOST-EDIT" else None,
                    e_.get("other"), e_["A"], e_["B"])
        pred_ident[k_] = pred_ident.get(k_, False) or (e_.get("admission") == "admitted"
                                                       and not (multi_ and e_.get("attribution") == "single-mod"))

    case_rows_ = {tuple(x) for x in ((plan.get("reader") or {}).get("validation") or {}).get("caseOnlyRows") or []}
    sym_checks_ = set(((pr.get("reader") or {}).get("prediction") or {}).get("symmetricPairChecks") or [])

    def with_patch_note(fids, n_rows):
        got_ = collections.Counter()
        sym_ = sym_checks_
        seen_pairs_, n_dup_ = set(), 0
        for x in fids:
            f_ = f_by_id.get(x) or {}
            c_ = f_.get("counts") or {}
            for r_ in f_.get("allRows") or f_.get("rows") or []:       # every row, not only the ones shown
                if f_.get("checkId") in sym_ and r_.get("other"):
                    # (fourth review, Pilgrim S3) the rows of one scene (the other record) are one clash, as the line counts
                    pk_ = (frozenset(map(str, r_.get("other") or ())),
                           frozenset((str(c_.get("A")).lower(), str(c_.get("B")).lower())))
                    if pk_ in seen_pairs_:
                        n_dup_ += 1
                        continue
                    seen_pairs_.add(pk_)
                k_ = ident_(f_.get("checkId"), r_.get("formKey"), r_.get("field"), r_.get("other"), c_.get("A"), c_.get("B"))
                v_ = r_.get("values") or {}
                # (BPT re-review S1) two values naming the same file but for letter case are no change in game
                lv_, wv_ = ("; ".join(map(str, x_)) if isinstance(x_, list) else x_ for x_ in (v_.get("lost"), v_.get("winner")))
                case_ = k_ not in pred_ident and (same_but_path_case(lv_, wv_) or (
                    str(f_.get("checkId")), str(r_.get("formKey")), str(r_.get("field")), str(c_.get("A")), str(c_.get("B")))
                    in case_rows_)
                got_["counted" if pred_ident.get(k_) else "aside" if k_ in pred_ident else "case" if case_ else "new"] += 1
        n_rows -= n_dup_
        if sum(got_.values()) != n_rows or got_["counted"] == n_rows or not conc.get("withPatchSplitParts"):
            return ""
        return " (" + "; ".join(bind(conc["withPatchSplitParts"][w_], {"count": fmt_n(got_[w_])})
                                for w_ in ("counted", "aside", "case", "new") if got_[w_]) + ")"

    placed_seen_ = set()

    def placed_note(f):
        """(fifth review, Clockwork S1) on the setup with the patch, the pairs the patch comparison finds still crossing
        where a patch places one of the objects, which that setup's own list does not hold; said once per check"""
        if not with_arm_ or f.get("armId") != with_arm_ or str(f.get("checkId")) in placed_seen_ or not conc.get("detailPatchPlaced"):
            return ""
        by_ = collections.Counter((e_["A"], e_["B"]) for e_ in pred_.get("record") or []
                                  if e_.get("checkId") == f.get("checkId") and e_.get("withPatch") == "patchPlaced")
        if not by_:
            return ""
        placed_seen_.add(str(f.get("checkId")))
        ph_ = ((pr.get("reader") or {}).get("kindPhrases") or {}).get(next((e_["kind"] for e_ in pred_.get("record") or []
                                                                            if e_.get("checkId") == f.get("checkId")), ""), {})
        # (eighth review, Winterhold 88 / 145) with two evaluated mods, how many the top counts and how many were already
        # there with one mod alone, so the note reconciles with the patch comparison's count
        alone_ = collections.Counter((e_["A"], e_["B"]) for e_ in pred_.get("record") or []
                                     if e_.get("checkId") == f.get("checkId") and e_.get("withPatch") == "patchPlaced"
                                     and len(pred_.get("mods") or []) > 1 and e_.get("attribution") == "single-mod")
        parts_ = [f"{fmt_n(n_)} {('pair' if n_ == 1 else 'pairs') if not ph_ else (ph_.get('unitSingular') if n_ == 1 else ph_.get('unitPlural'))} between {a_} and {b_}"
                  + (bind(conc.get("detailPatchPlacedSplit") or "", {"counted": fmt_n(n_ - alone_[(a_, b_)]),
                                                                    "alone": fmt_n(alone_[(a_, b_)])})
                     if alone_.get((a_, b_)) else "")
                  for (a_, b_), n_ in by_.items()]
        return bind(conc["detailPatchPlaced"], {"parts": "; ".join(parts_)})

    def counted_note(fids, n_rows):
        if with_arm_ and with_arm_ != pred_arm_ and fids and all((f_by_id.get(x) or {}).get("armId") == with_arm_
                                                                 for x in fids):
            return with_patch_note(fids, n_rows)
        k_ = sum(counted_by_fid.get(x, 0) for x in fids)
        if (pred_arm_ and not k_ and n_rows and conc.get("withPatchSplitParts")
                and all((f_by_id.get(x) or {}).get("armId") == pred_arm_ for x in fids)):
            # every row set aside: said, so the list adds up with the top (third review, Clockwork S2)
            return " (" + bind(conc["withPatchSplitParts"]["aside"], {"count": fmt_n(n_rows)}) + ")"
        al_ = sum(alone_by_fid.get(x, 0) for x in fids)
        # said where the line mixes rows counted at the top with ones already there with one mod alone (page budget)
        if pred_arm_ and k_ and al_ and k_ > al_ and conc.get("countedAboveAloneNote") and k_ <= n_rows:
            return bind(conc["countedAboveAloneNote"], {"counted": fmt_n(k_ - al_), "alone": fmt_n(al_),
                                                         "rest": (bind(conc.get("countedAboveRest") or "; {rest} set aside",
                                                                       {"rest": fmt_n(n_rows - k_)}) if n_rows > k_ else "")})
        if not pred_arm_ or not k_ or k_ >= n_rows or not conc.get("countedAboveNote"):
            return ""
        return bind(conc["countedAboveNote"], {"counted": fmt_n(k_), "rest": fmt_n(n_rows - k_)})
    seen_choice_ = {}                  # (eighth review) setup -> {(record, field): the check line that counted it first}
    for cb in blocks.values():
        parts = sorted(cb["parts"].values(), key=lambda x: (arm_sort_key(x["armId"] or "", arm_kinds), PART_ORDER.get(x["part"], 9)))
        if mode != "exhaustive":
            for part in parts:
                # P53: a setup's many pair lines become one line, each pair with its count and its evidence link; the
                # lost edits a declared patch wins are counted once as the patches' own choices. The evidence index
                # keeps every group in full.
                choices = [f for f in part["groups"] if f.get("readerPatchChoice") and f.get("readerRelevant")]
                rest = [f for f in part["groups"] if f not in choices]
                compact = []
                if choices:
                    # record fields, not rows: one field two plugins lose to the patch is one field in two pairs
                    # (Pilgrim's second review, S4)
                    # rows whose values show no change in game are set aside here as in the prediction (D2)
                    noise_ = {tuple(x) for x in ((plan.get("reader") or {}).get("validation") or {}).get("patchChoiceNoise") or []}
                    # (eighth review, Clockwork 135 / 139) rows whose losing plugin only carries an earlier value
                    carried_ = {tuple(x) for x in ((plan.get("reader") or {}).get("validation") or {}).get("patchChoiceCarried") or []}

                    def row_key(f, r_):
                        c_ = f.get("counts") or {}
                        return (str(f.get("checkId")), str(r_.get("formKey")), str(r_.get("field")), str(c_.get("A")),
                                str(c_.get("B")))

                    def kept_rows(f):
                        return [r_ for r_ in f.get("allRows") or [] if isinstance(r_, dict)
                                and row_key(f, r_) not in noise_ and row_key(f, r_) not in carried_]
                    all_ = {(str(r_.get("formKey")), str(r_.get("field"))) for f in choices
                            for r_ in f.get("allRows") or [] if isinstance(r_, dict)}
                    fields_ = {(str(r_.get("formKey")), str(r_.get("field"))) for f in choices for r_ in kept_rows(f)}
                    carried_f_ = {(str(r_.get("formKey")), str(r_.get("field"))) for f in choices
                                  for r_ in f.get("allRows") or [] if isinstance(r_, dict) and row_key(f, r_) in carried_
                                  and row_key(f, r_) not in noise_} - fields_
                    n_aside = len(all_ - fields_ - carried_f_)
                    n_fields = len(fields_) if all_ else sum(int(f.get("rowsTotal") or 0) for f in choices)
                    n_rows_ = None                 # (eighth review, Winterhold 159) the rows its links hold, set below
                    # (eighth review, Clockwork F-215 / F-167) a field an earlier record check's line counted is said there
                    seen_ = seen_choice_.setdefault(part.get("armId"), {})
                    again_ = collections.Counter(seen_[x] for x in fields_ if x in seen_)
                    for x in fields_:
                        seen_.setdefault(x, ((pr.get("reader") or {}).get("checkTitles") or {}).get(str(choices[0].get("checkId")), str(choices[0].get("title") or "")))
                    # (round 3, review12 S1 / finding 7) the rows in one unit, one per record field and plugin pair, and
                    # split so the parts add up: counted, no change in game, only carried by the loser
                    rows_all_ = [row_key(f, r_) for f in choices for r_ in f.get("allRows") or [] if isinstance(r_, dict)]
                    n_rows_ = len(rows_all_)
                    n_noise_rows_ = sum(1 for k_ in rows_all_ if k_ in noise_)
                    n_carried_rows_ = sum(1 for k_ in rows_all_ if k_ in carried_ and k_ not in noise_)
                    n_kept_rows_ = n_rows_ - n_noise_rows_ - n_carried_rows_
                    # (tenth review, Missives S1) a counted row whose loss the prediction holds as already there without
                    # the evaluated mods (so no predicted clash and no line of the patch comparison holds it) is named
                    already_ = {(str(e.get("formKey")), str(e.get("field")), str(e.get("A")).lower())
                                for e in pred_.get("record") or [] if e.get("admission") == "alreadyPresent"}
                    counted_named_ = [k_ for k_ in dict.fromkeys(rows_all_) if k_ not in noise_ and k_ not in carried_
                                      and (k_[1], k_[2], k_[3].lower()) in already_]
                    named_cnt_ = (bind(conc.get("patchChoicesCountedNamed") or "", {"items": "; ".join(
                        bind(conc.get("patchChoicesCountedItem") or "", {
                            "record": k_[1], "field": k_[2], "loser": k_[3], "patch": k_[4],
                            "already": conc.get("patchChoicesCountedAlready") or ""}) for k_ in counted_named_[:2])
                        + (bind(conc.get("patchChoicesCountedMore") or "", {"count": fmt_n(len(counted_named_) - 2)})
                           if len(counted_named_) > 2 else "")})
                        # every counted row is such a row, and few enough to name (page budget, skill data)
                        if counted_named_ and len(counted_named_) == n_kept_rows_
                        and n_kept_rows_ <= int(conc.get("patchChoicesCountedMax") or 0)
                        and conc.get("patchChoicesCountedNamed") else "")
                    rows_said_ = bool(n_fields and n_rows_ > n_fields)
                    if n_aside or carried_f_:
                        choices = [f for f in choices if kept_rows(f)] or choices[:1]
                    # (third review, Clockwork S3) every pair counted is linked, so the count recomputes from the rows
                    ids_ = [f["id"] for f in choices] or [f["id"] for f in part["groups"] if f.get("readerPatchChoice")][:1]
                    compact.append({"id": choices[0]["id"], "readerArmLabel": choices[0].get("readerArmLabel"),
                                    "text": bind(conc.get("patchChoicesCollapsed" if n_fields else "patchChoicesNone")
                                                 or "{count} record fields in {pairs} pairs where a patch wins{aside}", {
                                        "count": fmt_n(n_fields),
                                        "pairs": fmt_n(len(choices)),
                                        "fieldNoun": "field" if n_fields == 1 else "fields",
                                        "pairNoun": "pair" if len(choices) == 1 else "pairs",
                                        # (eighth review, page budget) the line's notes in one parenthesis
                                        "aside": (lambda ps_: f" ({'; '.join(ps_)})" if ps_ else "")([x_ for x_ in [
                                            bind(conc.get("patchChoicesRows") or "", {"rows": fmt_n(n_rows_), "parts": (
                                                lambda ps_: (": " + ", ".join(ps_)) if len(ps_) > 1 else "")([
                                                    bind(conc.get(w_) or "", {"count": fmt_n(n_)}) for w_, n_ in (
                                                        ("patchChoicesRowsCounted", n_kept_rows_),
                                                        ("patchChoicesRowsNoise", n_noise_rows_),
                                                        ("patchChoicesRowsCarried", n_carried_rows_)) if n_])})
                                            if rows_said_ else "",
                                            named_cnt_,
                                            # with the rows said, the fields not counted are among them, never in a
                                            # second unit
                                            bind(conc.get("patchChoicesAside") or "{count} more not counted", {
                                                "count": fmt_n(n_aside), "fieldNoun": "field" if n_aside == 1 else "fields"})
                                            if n_aside and not rows_said_ else "",
                                            bind(conc.get("patchChoicesCarried") or "", {
                                                "count": fmt_n(len(carried_f_)),
                                                "fieldNoun": "field" if len(carried_f_) == 1 else "fields"})
                                            if carried_f_ and not rows_said_ else "",
                                            *[bind(conc.get("patchChoicesAgain") or "", {"count": fmt_n(n_), "title": t_})
                                              for t_, n_ in again_.items()]] if x_])}),
                                    "ids": ids_})
                relevant = [f for f in rest if f.get("readerRelevant")]
                if len(relevant) > compact_over:
                    # one entry per plugin pair and unit: the pair's groups (several kinds of one check) are summed,
                    # each still linked
                    by_pair = collections.OrderedDict()
                    for f in relevant:
                        c = f.get("counts") or {}
                        ph = phrases.get(str(c.get("groupKind") or "")) or {}
                        if f.get("readerSeamPutBack"):
                            # seams on cells a mod undoes its master's fix on: one entry per undo, never by neighbour
                            m_, ms_ = f["readerSeamPutBack"]
                            e_ = by_pair.setdefault(("putBack", m_, ms_), {"n": 0, "ids": [], "putBack": (m_, ms_)})
                            e_["n"] += int(f.get("readerRows", f.get("rowsTotal")) or 0)
                            e_["ids"].append(f["id"])
                            continue
                        st_ = str(c.get("groupKind") or "").startswith("STATE-")
                        e_ = by_pair.setdefault((c.get("A"), c.get("B"), ph.get("unitPlural"), st_),
                                                {"n": 0, "ids": [], "ph": ph, "A": c.get("A"), "B": c.get("B"),
                                                 "objs": set(), "state": st_})
                        e_["n"] += int(f.get("readerRows", f.get("rowsTotal")) or 0)
                        e_["ids"].append(f["id"])
                        # (Pilgrim review, earlier S6) a world-state row is one object, counted once across states
                        e_["objs"] |= {(str(r_.get("formKey")), tuple(map(str, r_.get("other") or ())))
                                       for r_ in f.get("allRows") or [] if isinstance(r_, dict)}
                    entries = []
                    for e_ in by_pair.values():
                        if e_.get("putBack"):
                            sd_ = ((pr.get("reader") or {}).get("prediction") or {}).get("seamPutBackDetail") or ""
                            entries.append({"id": e_["ids"][0], "more": e_["ids"][1:], "text": bind(sd_, {
                                "count": fmt_n(e_["n"]), "noun": "boundary" if e_["n"] == 1 else "boundaries",
                                "mod": e_["putBack"][0], "master": e_["putBack"][1]}).rstrip(".")})
                            continue
                        n_, ph = e_["n"], e_["ph"]
                        if e_["state"] and e_["objs"] and conc.get("detailStateObjects"):
                            no_ = len(e_["objs"])
                            entries.append({"id": e_["ids"][0], "more": e_["ids"][1:],
                                            "text": bind(conc["detailStateObjects"], {
                                                "count": fmt_n(no_), "noun": "object" if no_ == 1 else "objects",
                                                "A": e_["A"], "B": e_["B"], "states": bind(conc.get("detailStateObjectsStates") or "",
                                                                                           {"states": fmt_n(len(e_["ids"]))})
                                                if len(e_["ids"]) > 1 else ""})})
                            continue
                        unit = (ph.get("unitSingular") if n_ == 1 else ph.get("unitPlural")) or ("record" if n_ == 1 else "records")
                        entries.append({"id": e_["ids"][0], "more": e_["ids"][1:],
                                        "text": f"{fmt_n(n_)} {unit} between {e_['A']} and {e_['B']}"
                                                + counted_note(e_["ids"], n_)})
                    note_c_ = next((f.get("readerProvenance") for f in relevant if f.get("readerProvenance")), None)
                    pp_ = placed_note(relevant[0])
                    compact.append({"id": relevant[0]["id"], "readerArmLabel": relevant[0].get("readerArmLabel"),
                                    "entries": entries,
                                    "note": " ".join(x_ for x_ in (note_c_, pp_) if x_) or None})
                    rest = [f for f in rest if not f.get("readerRelevant")]
                for f in rest:
                    pp_ = placed_note(f) if f.get("readerRelevant") and f.get("readerPlain") else ""
                    if pp_ and pp_ not in str(f["readerPlain"]):
                        f["readerPlain"] = str(f["readerPlain"]).rstrip() + " " + pp_
                    if f.get("readerRelevant") and f.get("armId") in (pred_arm_, with_arm_) and f.get("readerPlain"):
                        note_ = counted_note([f["id"]], int(f.get("readerRows", f.get("rowsTotal")) or 0))
                        if note_:
                            head_, _, tail_ = str(f["readerPlain"]).partition(". ")
                            f["readerPlain"] = head_.rstrip(".") + note_ + "." + (" " + tail_ if tail_ else "")
                part["groups"] = rest
                part["compact"] = compact
        finding_blocks.append({**cb, "parts": parts})
    if mode != "exhaustive" and conc.get("provenanceAgain"):
        # a provenance sentence the detail list already printed is said again in short (P53); the index keeps it
        said = set()
        for cb in finding_blocks:
            for part in cb["parts"]:
                for c in part.get("compact") or []:
                    if c.get("note"):
                        c["note"] = conc["provenanceAgain"] if c["note"] in said else c["note"]
                        said.add(c["note"])
                groups = []
                for f in part["groups"]:
                    pv = f.get("readerProvenance")
                    if pv and pv in said and f.get("readerRelevant"):
                        f = {**f, "readerProvenance": conc["provenanceAgain"]}
                    elif pv:
                        said.add(pv)
                    groups.append(f)
                part["groups"] = groups
    view = dict(plan)
    if mode != "exhaustive":
        view["reader"] = once_on_page(plan.get("reader") or {}, pr)
    view["missingRows"] = missing_rows
    view["findingBlocks"] = finding_blocks
    view["detailAside"] = detail_aside
    view["unjudgeable"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") == "unjudgeable"]
    view["unassessedOther"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") not in ("unjudgeable", "media")]
    view["findings"] = findings
    view["sections"] = sections
    view["omissions"] = omissions
    view["effective"] = {"layout": layout, "rowsPerGroup": rows_per_group, "figuresPerFinding": figures_per_finding}
    view["figuresByFinding"] = figures_by_finding
    view["layout"] = layout
    view["evidenceTokensCompact"] = (pr.get("concise") or {}).get("evidenceTokensCompact", 3)
    view["mediaAssessed"] = [m for m in plan.get("media") or [] if m.get("assessed")]
    view["mediaUnassessed"] = [m for m in plan.get("media") or [] if not m.get("assessed")]
    view["assertedSentence"] = pr.get("mediaAssertedSentence")
    return view


def copy_media(plan: dict, bundle_dir: Path, out: Path) -> list[dict]:
    """Copy every assessed media file the report may show under OUT/media/, by content hash - so a
    relocated OUT still resolves its figures. Returns the referenced-media rows for the manifest."""
    rows = []
    for m in plan.get("media") or []:
        if not m.get("assessed") or not m.get("path"):
            continue
        src = bundle_dir / m["path"]
        if not src.exists():
            m["reportPath"] = None
            m["reportNote"] = "media file absent from the bundle at render time"
            continue
        dest = out / "media" / Path(m["path"]).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.exists() or sha256_file(dest) != m.get("sha256"):
            shutil.copy2(src, dest)
        m["reportPath"] = f"media/{dest.name}"
        rows.append({"id": m["id"], "path": m["reportPath"], "sha256": sha256_file(dest), "bundlePath": m["path"]})
    return rows


def copy_supporting_evidence(plan: dict, presentation_dir: Path, out: Path) -> list[dict]:
    rows = []
    for e in plan.get("evidence") or []:
        if e.get("storage") != "presentation" or not e.get("path"):
            continue
        src = presentation_dir / e["path"]
        if not src.exists() or sha256_file(src) != e.get("sha256"):
            raise ValueError(f"supporting-evidence-changed: {e['path']} is absent or does not match its recorded sha256")
        dest = out / e["path"]
        dest.parent.mkdir(parents=True, exist_ok=True)
        if src.resolve() != dest.resolve():
            shutil.copy2(src, dest)
        rows.append({"id": e.get("id"), "path": e["path"], "sha256": e["sha256"], "bytes": dest.stat().st_size})
    return rows


def report_short_forms(plan: dict, pr: dict) -> dict:
    """(report words, item 3) {full name: short form} for the report's long plugin file stems and mod names, by the reel's
    one rule (reel_plan.short_mod_names) over every name the report can say: each setup's plugins and their mod names,
    the evaluated mod's own names as the subject, and each plugin's stem and mod name counted as one plugin."""
    import reel_plan
    words = (pr.get("reel") or {}).get("words") or {}
    mods = ((plan.get("reel") or {}).get("modNames") or ((plan.get("reader") or {}).get("names") or {}).get("mods") or {})
    plugins = []
    for a_ in (plan.get("scope") or {}).get("arms") or []:
        plugins += [str(x) for x in (a_.get("plugins") or []) if isinstance(a_, dict)]
    targets = [str(t.get("name")) for t in (plan.get("scope") or {}).get("targets") or [] if isinstance(t, dict) and t.get("name")]
    plugins = list(dict.fromkeys(plugins + targets + [str(k) for k in mods]))

    def stem(x):
        return re.sub(r"\.(esp|esm|esl)$", "", str(x), flags=re.I)

    def name_of(x):
        v = mods.get(x) or next((v_ for k_, v_ in mods.items() if str(k_).lower() == str(x).lower()), None)
        return (v or {}).get("name") if isinstance(v, dict) else v
    pool = [stem(x) for x in plugins] + [n for n in (name_of(x) for x in plugins) if n]
    subject = [y for t in targets[:1] for y in (stem(t), name_of(t)) if y]
    same = [{stem(x), *([name_of(x)] if name_of(x) else [])} for x in plugins]
    got = reel_plan.short_mod_names(pool, words, subject=subject, same=same)
    # the report prints file names a modder looks up: only a plugin whose file name is long (skill data minLength, the
    # name as printed, extension included) is said short, by its stem and its mod name alike; a short file name such as
    # "ModA - ModB patch.esp" stays in full even where the reel says it by the rest (a patch named after the mod)
    floor = int(((words.get("shortForm") or {}).get("minLength")) or 40)
    long_ = {n.casefold() for x in plugins if len(str(x)) > floor for n in (stem(x), name_of(x)) if n}
    return {k: v for k, v in got.items() if k.casefold() in long_}


#: rendered text a reader does not read as a sentence, or that quotes a name: left exactly as rendered
REPORT_MD_KEEP = re.compile(r"```[\s\S]*?```|`[^`\n]*`|\]\([^)\s]*\)|!\[[^\]\n]*\]|<[^>\n]*>|\"[^\"\n]*\"|“[^”\n]*”")
REPORT_HTML_KEEP = re.compile(r"<(style|script|title)\b[\s\S]*?</\1>|<[^>]*>", re.I)
TEXT_QUOTED = re.compile(r"\"[^\"\n]*\"|“[^”\n]*”")


def say_report_short_forms(text: str, short: dict, gloss: str, as_html: bool = False) -> str:
    """(report words, item 3) A long plugin file name or mod name said in full until its first plain mention, which
    carries the gloss ("X.esp (X below)"), and by its short form after, in the rendered text's reading order. Link
    targets, tags and attributes, image alt text, code and anything in double quotes (an in-game name quoted as a
    value) are left as rendered. A possessive after the name is said again by the reel's rule for the short form."""
    import reel_plan
    if not short or not text:
        return text
    pat = reel_plan.short_form_pattern(short, ext=True)
    glossed = set()

    def one(m):
        n, own = m.group("name"), m.group("own")
        key = short[n].casefold()
        if key in glossed:
            return reel_plan.said_short(short[n], own)
        if own or not gloss:
            return m.group(0)
        glossed.add(key)
        return fill(gloss, name=m.group(0), short=short[n])

    def plain(chunk):
        out, last = [], 0
        for q in TEXT_QUOTED.finditer(chunk):
            out += [pat.sub(one, chunk[last:q.start()]), q.group(0)]
            last = q.end()
        return "".join(out + [pat.sub(one, chunk[last:])])
    if not as_html:
        out, last = [], 0
        for k in REPORT_MD_KEEP.finditer(text):
            out += [plain(text[last:k.start()]), k.group(0)]
            last = k.end()
        return "".join(out + [plain(text[last:])])
    body = text.find("<body")
    head, rest = (text[:body], text[body:]) if body >= 0 else ("", text)
    out, last = [head], 0

    def node(chunk):
        said = plain(html.unescape(chunk))
        return chunk if said == html.unescape(chunk) else html.escape(said, quote=False)
    for k in REPORT_HTML_KEEP.finditer(rest):
        out += [node(rest[last:k.start()]), k.group(0)]
        last = k.end()
    return "".join(out + [node(rest[last:])])


def render_all(plan: dict, view: dict, out: Path, css: str) -> dict:
    e_md = env(False)
    e_html = env(True)
    ctx = {"p": view, "css": css}
    md = e_md.get_template("report.md.j2").render(**ctx)
    html = e_html.get_template("report.html.j2").render(**ctx)
    # (report words, item 3) long names said in full once, then by their short forms; the evidence index keeps them
    pr_ = load_priorities()
    short_ = (plan.get("reader") or {}).get("shortForms")
    short_ = report_short_forms(plan, pr_) if short_ is None else short_
    gloss_ = str((((pr_.get("reel") or {}).get("words") or {}).get("shortForm") or {}).get("reportFirst") or "")
    md = say_report_short_forms(md, short_, gloss_)
    html = say_report_short_forms(html, short_, gloss_, as_html=True)
    idx = e_md.get_template("evidence-index.md.j2").render(**ctx)
    # the same index as a browser can read: report.html links into THIS one, report.md into the Markdown one.
    # One template per format, as the report itself has - not a converter, so every sentence stays a template
    # sentence bound to bundle values.
    idx_html = e_html.get_template("evidence-index.html.j2").render(**ctx)
    write_text(out / "report.md", md)
    write_text(out / "report.html", html)
    write_text(out / "evidence-index.md", idx)
    write_text(out / "evidence-index.html", idx_html)
    return {"report.md": md, "report.html": html, "evidence-index.md": idx, "evidence-index.html": idx_html}


# -------------------------------------------------------------------------------------- commands --

def cmd_plan(a) -> int:
    pr = load_priorities()
    bundle = Path(a.bundle)
    out = Path(a.out)
    # A flag first, then the saved evaluation settings (evalsettings.py), then the skill's data.
    try:
        s = evalsettings.tool_defaults(["report.mode", "report.maxPages", "report.rowsPerGroup",
                                        "report.figuresPerFinding"])
    except evalsettings.SettingsError as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return EXIT_REFUSED_INPUT
    if getattr(a, "exhaustive", False):
        a.mode = "exhaustive"
    if a.mode is None:
        a.mode = s["report.mode"] or "concise"
    for attr, key in (("max_pages", "report.maxPages"), ("rows_per_group", "report.rowsPerGroup"),
                      ("figures_per_finding", "report.figuresPerFinding")):
        if getattr(a, attr) is None and s[key] is not None:
            setattr(a, attr, int(s[key]))
    with Run("evalreport-plan", stage_source="counted") as r:
        r.stage("verify-bundle", total=1, unit="bundles")
        if not (bundle / "manifest.json").exists():
            return refuse(r, f"bundle-missing: {bundle / 'manifest.json'} does not exist")
        manifest = load_json(bundle / "manifest.json")
        if not isinstance(manifest, dict):
            return refuse(r, f"manifest-unreadable: {bundle / 'manifest.json'} is not JSON")
        if manifest.get("bundleSchema") != BUNDLE_SCHEMA:
            return refuse(r, f"schema-unsupported: bundleSchema {manifest.get('bundleSchema')!r} is not {BUNDLE_SCHEMA}; "
                             "this bundle predates evidence-chain verification - rebuild it from its run")
        out.mkdir(parents=True, exist_ok=True)
        verify, code, err = run_verify_bundle(bundle, out)
        if verify is None:
            return refuse(r, f"verify-failed: {err}")
        bl = verify.get("bundle") or {}
        if not bl.get("schemaSupported", True):
            return refuse(r, "schema-unsupported: " + "; ".join(d.get("detail", "") for d in bl.get("defects") or []))
        if not bl.get("complete"):
            defects = bl.get("defects") or []
            names = ", ".join(f"{d.get('kind')} ({d.get('path')})" for d in defects[:8]) or "unnamed defect"
            more = f" and {len(defects) - 8} more" if len(defects) > 8 else ""
            return refuse(r, f"bundle-refused: verify-bundle found the bundle incomplete: {names}{more}; nothing is compiled "
                             f"from a bundle whose evidence does not hash to what was measured")
        bundle_plan = load_json(bundle / manifest["plan"])
        plan_arms = {x.get("armId"): x for x in bundle_plan.get("arms") or []}
        location_scopes = {}
        requested_scopes = list(getattr(a, "location_scope", None) or [])
        normal_scope = load_json(bundle / manifest["scope"]) if manifest.get("scope") else None
        if isinstance(normal_scope, dict) and normal_scope.get("locationContributors"):
            requested_scopes.insert(0, "baseline=" + str(bundle / manifest["scope"]))
        for binding in requested_scopes:
            if "=" not in binding:
                return refuse(r, f"location-scope-invalid: {binding!r} must be ARM=FILE")
            arm_id, source = binding.split("=", 1)
            if arm_id not in plan_arms:
                return refuse(r, f"location-scope-arm-unknown: {arm_id!r} is not a planned arm")
            doc = load_json(Path(source))
            if not isinstance(doc, dict) or doc.get("probe") != "scopeprobe" or not isinstance(doc.get("locationContributors"), dict):
                return refuse(r, f"location-scope-invalid: {source} is not a scopeprobe document with locationContributors")
            req_plugins = [(p.get("name") or p.get("Name"), p.get("sha256") or p.get("Sha256"))
                           for p in ((plan_arms[arm_id].get("requested") or {}).get("plugins") or [])]
            got_plugins = [(p.get("name") or p.get("Name"), p.get("sha256") or p.get("Sha256"))
                           for p in ((doc.get("configuration") or {}).get("plugins") or [])]
            if req_plugins != got_plugins:
                return refuse(r, f"location-scope-foreign: {source} plugin order or hashes do not match arm {arm_id}")
            requested = plan_arms[arm_id].get("requested") or {}
            config = doc.get("configuration") or {}
            def named(rows):
                return [(x.get("name") or x.get("Name"), x.get("kind") or x.get("Kind"),
                         x.get("sha256") or x.get("Sha256")) for x in rows or []]
            if named(requested.get("providers")) != named(config.get("providers")):
                return refuse(r, f"location-scope-foreign: {source} providers do not match arm {arm_id}")
            if named(requested.get("overlays")) != named(config.get("overlays")):
                return refuse(r, f"location-scope-foreign: {source} overlays do not match arm {arm_id}")
            if requested.get("population") not in (None, config.get("population")):
                return refuse(r, f"location-scope-foreign: {source} population does not match arm {arm_id}")
            requested_states = requested.get("states") or []
            if requested_states and config.get("state") not in requested_states:
                return refuse(r, f"location-scope-foreign: {source} state does not match arm {arm_id}")
            location_scopes[arm_id] = doc
        # the record lane's rows on the setup without the patch with its plugins in another order (an order risk): the
        # plan that describes that setup and the rows measured there; build_prediction checks the plugins and bytes
        order_setups = []
        for binding in list(getattr(a, "order_setup", None) or []):
            if "@" not in binding or "=" not in binding.split("@", 1)[1]:
                return refuse(r, f"order-setup-invalid: {binding!r} must be ARM@PLAN=ROWS")
            o_arm, rest = binding.split("@", 1)
            plan_src, rows_src = rest.split("=", 1)
            o_plan, o_rows = load_json(Path(plan_src)), load_json(Path(rows_src))
            o_desc = next((x for x in (o_plan.get("arms") if isinstance(o_plan, dict) else None) or []
                           if x.get("armId") == o_arm), None)
            if o_desc is None:
                return refuse(r, f"order-setup-invalid: {plan_src} is not an evaluation plan with an arm {o_arm!r}")
            if isinstance(o_rows, dict) and isinstance(o_rows.get("rows"), list):
                rows_ = o_rows["rows"]
            elif isinstance(o_rows, dict) and isinstance(o_rows.get("Conflicts"), list):
                rows_ = [dict(c, _check=c.get("_check") or o_rows.get("Population")) for c in o_rows["Conflicts"]]
            else:
                return refuse(r, f"order-setup-invalid: {rows_src} holds no record rows (a `rows` or `Conflicts` list)")
            if any(not x.get("_check") for x in rows_):
                return refuse(r, f"order-setup-invalid: {rows_src} has rows that name no check")
            order_setups.append({"armId": o_arm, "planId": o_plan.get("planId"), "planPath": plan_src,
                                 "rowsPath": rows_src, "plugins": (o_desc.get("requested") or {}).get("plugins") or [],
                                 "rows": rows_})
        r.advance(1)
        r.stage("compile", total=1, unit="plans")
        conc = pr.get("concise") or {}
        page = pr.get("page") or {}
        settings = {"mode": a.mode, "layout": conc.get("layout", "normal"),
                    "maxPages": (a.max_pages if a.max_pages is not None else conc.get("maxPages", 8)),
                    "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16), "baseFontPt": page.get("baseFontPt", 10.5)},
                    "declaredPatches": list(a.declare_patch or []),
                    "rowsPerGroup": (a.rows_per_group if a.rows_per_group is not None else conc.get("rowsPerGroup", 3)),
                    "figuresPerFinding": (a.figures_per_finding if a.figures_per_finding is not None else conc.get("figuresPerFinding", 1)),
                    "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height", "stillFont", "stillFontBold")},
                    "singleMod": a.single_mod, "headlineUnit": a.headline_unit,
                    "alsoBundles": list(getattr(a, "also_bundle", None) or []),
                    "precedentDir": getattr(a, "precedent", None)}
        if a.mode == "exhaustive":
            settings["maxPages"] = None
        for x_ in settings["alsoBundles"]:
            aman = load_json(Path(x_) / "manifest.json")
            if not isinstance(aman, dict) or aman.get("bundleSchema") != BUNDLE_SCHEMA:
                return refuse(r, f"also-bundle-unreadable: {Path(x_) / 'manifest.json'} is not a schema-{BUNDLE_SCHEMA} "
                                 f"bundle manifest, so it is not named.")
        sib_given = list(getattr(a, "sibling_bundle", None) or [])
        for k_, given_ in enumerate(sib_given):
            sibling = Path(given_)
            sman = load_json(sibling / "manifest.json")
            if not isinstance(sman, dict) or sman.get("bundleSchema") != BUNDLE_SCHEMA:
                return refuse(r, f"sibling-unreadable: {sibling / 'manifest.json'} is not a schema-{BUNDLE_SCHEMA} bundle "
                                 f"manifest, so nothing was joined.")
            vname = "sibling-verify" if k_ == 0 else f"sibling-verify-{k_ + 1}"
            sdir = out / vname
            sdir.mkdir(parents=True, exist_ok=True)
            sverify, _, serr = run_verify_bundle(sibling, sdir)
            if sverify is None or not (sverify.get("bundle") or {}).get("complete"):
                who_ = "the sibling bundle" if len(sib_given) == 1 else f"the sibling bundle {given_}"
                return refuse(r, f"sibling-refused: verify-bundle did not find {who_} complete "
                                 f"({serr or f'see {vname}/verify.json'}), so nothing was joined.")
        # each mod's name, resolved once for the report and the reel (item 36 F2)
        try:
            resolved_ = resolve_mod_names(bundle, [Path(x_) for x_ in sib_given], a, out)
        except InputRefused as ex:
            return refuse(r, str(ex))
        try:
            plan = compile_plan(bundle, verify, settings, pr, str(a.bundle), location_scopes,
                                order_setups=order_setups, siblings=[(Path(x_), x_) for x_ in sib_given],
                                mod_names=resolved_[0])
        except InputRefused as ex:
            return refuse(r, str(ex))
        # the reel's media and masters come from the sibling the prediction anchors on (the first holding the shared setup)
        primary_ = ((plan.get("reader") or {}).get("prediction") or {}).get("sibling") or {}
        sibling = next((Path(x_) for x_ in sib_given if x_ == primary_.get("pathAsGiven")), None)
        for arm_id, doc in location_scopes.items():
            dest = out / "evidence" / f"location-scope-{arm_id}.json"
            write_json(dest, doc)
            location_sha = sha256_file(dest)
            plan["evidence"].append({"id": "E-" + location_sha[:16], "kind": "scope",
                                     "path": dest.relative_to(out).as_posix(), "sha256": location_sha,
                                     "bytes": dest.stat().st_size, "checkId": None, "armId": arm_id,
                                     "part": None, "strength": "measured-scope", "recordedBy": "scopeprobe",
                                     "storage": "presentation"})
        for o_ in order_setups:
            # what the order risk lines rest on, kept with the report and hashed like every other evidence file
            dest = out / "evidence" / f"order-setup-{o_['armId']}.json"
            write_json(dest, {"planId": o_["planId"], "armId": o_["armId"], "planPath": o_["planPath"],
                              "rowsPath": o_["rowsPath"], "plugins": o_["plugins"], "rows": o_["rows"]})
            o_sha = sha256_file(dest)
            plan["evidence"].append({"id": "E-" + o_sha[:16], "kind": "order-setup",
                                     "path": dest.relative_to(out).as_posix(), "sha256": o_sha,
                                     "bytes": dest.stat().st_size, "checkId": None, "armId": o_["armId"],
                                     "part": None, "strength": "measured-records", "recordedBy": "record lane",
                                     "storage": "presentation"})
        # item 10: what the order-geometry lines rest on, kept with the report like the order setups
        pred_ = (plan.get("reader") or {}).get("prediction") or {}
        g_dest = {}
        for ln_ in pred_.get("orderGeometryLines") or []:
            src_ = str(ln_.get("path") or "")
            if src_ not in g_dest:
                dest = out / "evidence" / ("order-geometry-" + Path(src_).stem + ".json")
                write_json(dest, load_json(Path(bundle) / src_))
                g_sha = sha256_file(dest)
                plan["evidence"].append({"id": "E-" + g_sha[:16], "kind": "order-geometry",
                                         "path": dest.relative_to(out).as_posix(), "sha256": g_sha,
                                         "bytes": dest.stat().st_size, "checkId": None, "armId": None, "part": None,
                                         "strength": "geometric-measurement", "recordedBy": "enrichment:order-geometry",
                                         "storage": "presentation", "source": src_})
                g_dest[src_] = dest.relative_to(out).as_posix()
            ln_["path"] = g_dest[src_]
        try:
            plan["reel"] = reel_block(plan, bundle, sibling, pr, a, out, resolved=resolved_,
                                      siblings_all=[(Path(x_), "sibling:" if k_ == 0 else f"sibling{k_ + 1}:")
                                                    for k_, x_ in enumerate(sib_given)])
            plan["reader"]["shortForms"] = report_short_forms(plan, pr)
        except InputRefused as ex:
            return refuse(r, str(ex))
        stamp(plan)
        write_json(out / "presentation.json", plan)
        r.artifact(str(out / "presentation.json"))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("groups", len(plan["groups"]), "groups")
        r.feature("media", len(plan["media"]), "media")
        r.feature("evidence", len(plan["evidence"]), "files")
        r.feature("imports", len((plan["imports"] or {}).get("rows") or []), "imports")
        r.advance(1)
        print(f"presentation {plan['presentationId']}: {len(plan['findings'])} findings, {len(plan['groups'])} groups, "
              f"{len(plan['media'])} media rows, {len(plan['evidence'])} evidence files; mode {a.mode}; "
              f"bundle complete {plan['source']['verify']['bundleComplete']}, evaluation complete "
              f"{plan['source']['verify']['evaluationComplete']} ({plan['source']['verify']['claim']})")
        print(f"wrote {out / 'presentation.json'}")
    return EXIT_OK


def cmd_triage(a) -> int:
    """Render the version-bound overlap incident catalogue via the report entry point."""
    source = Path(a.manifest)
    out = Path(a.out)
    with Run("evalreport-triage", stage_source="counted") as r:
        r.stage("compile", total=1, unit="catalogues")
        manifest = load_json(source)
        policy = load_json(Path(a.policy))
        if not isinstance(manifest, dict):
            return refuse(r, f"triage-manifest-unreadable: {source} is absent or not JSON")
        if not isinstance(policy, dict):
            return refuse(r, f"triage-policy-unreadable: {a.policy} is absent or not JSON")
        source_sha = sha256_file(source)
        try:
            catalogue, rendered = compile_triage_catalogue(manifest, policy, source_sha, str(source.resolve()), str(out.resolve()))
        except ValueError as exc:
            return refuse(r, str(exc))
        out.mkdir(parents=True, exist_ok=True)
        write_json(out / "overlap-triage-catalogue.json", catalogue)
        write_text(out / "overlap-triage-catalogue.html", rendered)
        detail_pages = render_triage_incident_pages(catalogue)
        for name, content in detail_pages.items():
            write_text(out / name, content)
        r.artifact(str(out / "overlap-triage-catalogue.json"))
        r.artifact(str(out / "overlap-triage-catalogue.html"))
        for name in detail_pages:
            r.artifact(str(out / name))
        r.feature("incidents", len(catalogue["incidents"]), "incidents")
        r.feature("pairs", int(catalogue["accounting"].get("pairsTotal") or 0), "pairs")
        r.advance(1)
        print(f"triage catalogue {catalogue['identity']}: {len(catalogue['incidents'])} incidents; wrote {out}")
    return EXIT_OK


def load_presentation(path: Path):
    plan = load_json(path)
    if not isinstance(plan, dict):
        return None, f"presentation-unreadable: {path} is absent or not JSON"
    if plan.get("schemaVersion") != PRESENTATION_SCHEMA:
        return None, f"presentation-schema-unsupported: schemaVersion {plan.get('schemaVersion')!r} is not {PRESENTATION_SCHEMA}"
    for k in ("findings", "settings", "source", "scope", "evidence"):
        if k not in plan:
            return None, f"presentation-incomplete: no `{k}`"
    if (not isinstance(plan.get("reader"), dict) or "sectionHeadings" not in plan["reader"]
            or any(isinstance(a, dict) and "readerLabel" not in a for a in (plan.get("scope") or {}).get("arms") or [])):
        return None, "presentation-needs-replan: run evalreport plan again to add the modder-facing summary"
    if not all(isinstance(plan["reader"].get(k), dict) for k in ("prediction", "coverage", "validation")):
        return None, ("presentation-needs-replan: run evalreport plan again so the report leads with what adding "
                      "the mods will break")
    if not isinstance(plan.get("imports"), dict):
        # a plan made before imported evidence was described cannot say whether a result was measured for this
        # evaluation, and a report that stays silent about that would read as if it had been
        return None, ("presentation-needs-replan: run evalreport plan again to record where imported evidence "
                      "came from")
    return plan, ""


def cmd_report(a) -> int:
    try:
        if not a.no_print and evalsettings.tool_defaults(["report.pdf"])["report.pdf"] == "off":
            a.no_print = "settings"
    except evalsettings.SettingsError as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return EXIT_REFUSED_INPUT
    pr = load_priorities()
    ppath = Path(a.presentation)
    out = Path(a.out)
    with Run("evalreport-report", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        bundle_dir = Path(plan["source"]["bundleDir"])
        if not bundle_dir.is_absolute():
            bundle_dir = (ppath.parent / bundle_dir).resolve() if not bundle_dir.exists() else bundle_dir
        out.mkdir(parents=True, exist_ok=True)
        try:
            supporting_rows = copy_supporting_evidence(plan, ppath.parent, out)
        except ValueError as ex:
            return refuse(r, str(ex))
        css = read_text(TEMPLATES / "report.css") if (TEMPLATES / "report.css").exists() else ""
        settings = plan["settings"]
        mode = settings.get("mode", "concise")
        ladder = pr.get("abridgementLadder") or [{"step": 0, "layout": settings.get("layout", "normal"),
                                                  "rowsPerGroup": settings.get("rowsPerGroup", 3),
                                                  "figuresPerFinding": settings.get("figuresPerFinding", 1)}]
        # the ladder starts at the plan's own settings and only descends
        steps = [s for s in ladder if s["rowsPerGroup"] <= settings.get("rowsPerGroup", 3) and
                 s["figuresPerFinding"] <= settings.get("figuresPerFinding", 1)] or ladder
        if mode == "exhaustive":
            steps = [{"step": 0, "layout": settings.get("layout", "normal"), "rowsPerGroup": 0, "figuresPerFinding": 10 ** 6}]
        assessed_ids = {m["id"] for m in plan.get("media") or [] if m.get("assessed")}
        has_figures = any(mid in assessed_ids for f in plan["findings"] for mid in (f.get("mediaIds") or []))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("media", len(assessed_ids), "media")
        media_rows = copy_media(plan, bundle_dir, out)
        browser, version, why = (None, "", "printing turned off in the evaluation settings (report.pdf off)" if a.no_print == "settings" else "printing disabled by --no-print") if a.no_print else resolve_browser()
        print_info = {"browser": str(browser) if browser else None, "version": version or None, "pages": None,
                      "maxPages": settings.get("maxPages"), "status": "not-rendered", "reason": why or None,
                      "ladder": [], "rootCount": None, "kept": None}
        limit = settings.get("maxPages")
        view = None
        rendered = 0

        def step_row(step, pages=None, note=None):
            return {"step": step.get("step"), "layout": step.get("layout", "normal"), "rowsPerGroup": step["rowsPerGroup"],
                    "figuresPerFinding": step["figuresPerFinding"], "pages": pages, "note": note}

        def render_step(step):
            nonlocal rendered
            v = effective_view(plan, step["rowsPerGroup"], step["figuresPerFinding"], pr, step.get("layout", "normal"))
            rendered += len(render_all(plan, v, out, css))
            r.feature("figures", sum(len(f.get("figures") or []) for f in v["findings"]), "figures")
            return v

        def print_step():
            pdf = out / "report.pdf"
            if pdf.exists():
                pdf.unlink()
            ok, perr = print_pdf(browser, out / "report.html", pdf)
            if not ok:
                return None, None, perr
            return pdf_page_count(pdf)

        r.stage("print", total=len(steps), unit="attempts")
        prev_figs = None
        for step in steps:
            if prev_figs is not None and step["figuresPerFinding"] < prev_figs and not has_figures:
                print_info["ladder"].append(step_row(step, None, "skipped: no figure to drop"))
                r.advance(1)
                continue
            prev_figs = step["figuresPerFinding"]
            view = render_step(step)
            if browser is None:
                r.advance(1)
                break
            pages, root, note = print_step()
            r.advance(1)
            if pages is None and root is None and note and not note.startswith("no page objects"):
                print_info.update({"status": "not-rendered", "reason": note, "pages": None})
                r.note(f"print failed: {note}")
                break
            print_info["rootCount"] = root
            print_info["ladder"].append(step_row(step, pages, note))
            if pages is None or note != "ok":
                print_info.update({"status": "count-unverified", "reason": note, "pages": pages})
                break
            print_info["pages"] = pages
            print_info["reason"] = None
            print_info["kept"] = step_row(step, pages, "kept")
            if mode == "exhaustive" or limit is None:
                print_info["status"] = "rendered"
                break
            if pages <= limit:
                print_info["status"] = "within-limit"
                break
            print_info["status"] = "over-limit"
        if print_info["status"] == "over-limit":
            # A-9 / A-11: a report over the limit either way keeps its pictures, and an abridgement that does not
            # buy the limit is pure loss - the kept rendition is the LEAST abridged step that loses no content:
            # the compact layout (chrome, not content) at the setting's own rows per group and figures per
            # finding; the normal-layout step when the ladder has no compact one. Every step stays in the ladder.
            counted = [l for l in print_info["ladder"] if l.get("pages") is not None]
            full = [l for l in counted if l["rowsPerGroup"] >= settings.get("rowsPerGroup", 3)
                    and l["figuresPerFinding"] >= settings.get("figuresPerFinding", 1)]
            compact_full = [l for l in full if l["layout"] == "compact"]
            best = min(compact_full or full or counted, key=lambda l: l["step"])
            print_info["reason"] = (f"the limit of {limit} page(s) was unreachable without hiding required content; "
                                    "pages at each step: " + ", ".join(f"{l['layout']}/{l['rowsPerGroup']}/{l['figuresPerFinding']} "
                                                                       f"{l['pages']}" for l in counted))
            if best["step"] != (print_info["kept"] or {}).get("step"):
                bstep = next(x for x in steps if x.get("step") == best["step"])
                view = render_step(bstep)
                pages, root, note = print_step()
                print_info["ladder"].append(step_row(bstep, pages, f"{note}; re-rendered as the kept step"))
                best = {**best, "pages": pages if pages is not None else best["pages"]}
            print_info["pages"] = best["pages"]
            print_info["kept"] = {**best, "note": "kept"}
        # ------- manifest
        produced = []
        for name in ARTIFACTS:
            p = out / name
            if p.exists():
                produced.append({"path": name, "sha256": sha256_file(p), "bytes": p.stat().st_size})
        effective = view["effective"] if view else {"layout": settings.get("layout", "normal"), "rowsPerGroup": settings.get("rowsPerGroup"),
                                                    "figuresPerFinding": settings.get("figuresPerFinding")}
        r.note(f"rendered {rendered} rendition(s) over {len(print_info['ladder']) or 1} ladder step(s)")
        manifest = {"schemaVersion": 1, "presentationId": plan.get("presentationId"), "generatedUtc": utc_now(),
                    "source": plan["source"], "compiler": plan["compiler"], "settings": settings, "effective": effective,
                    "artifacts": produced, "media": media_rows,
                    "supportingEvidence": supporting_rows,
                    "omissions": view["omissions"] if view else plan.get("omissions"),
                    "print": print_info}
        write_json(out / "presentation-manifest.json", manifest)
        for x in produced:
            r.artifact(str(out / x["path"]))
        r.artifact(str(out / "presentation-manifest.json"))
        if print_info.get("pages") is not None:
            r.feature("pages", print_info["pages"], "pages", at="final")
        st = print_info["status"]
        print(f"report: {', '.join(x['path'] for x in produced)}; print {st}" +
              (f" ({print_info['pages']} page(s), limit {print_info['maxPages']})" if print_info.get("pages") is not None else
               f" ({print_info.get('reason')})") +
              f"; layout {effective['layout']}, rows per group {effective['rowsPerGroup']}, figures per finding {effective['figuresPerFinding']}"
              + (f"; ladder {[(l['layout'], l['rowsPerGroup'], l['figuresPerFinding'], l['pages']) for l in print_info['ladder']]}" if print_info['ladder'] else ""))
        if st == "over-limit":
            print(f"over-limit: the abridgement ladder ended at {print_info['pages']} page(s) against a limit of "
                  f"{print_info['maxPages']}; the font was not reduced. Use --mode exhaustive for an unbounded rendition.")
        if st == "not-rendered":
            print(f"not-rendered: {print_info.get('reason')}; report.md and report.html were written, no page count is claimed.")
    return EXIT_OK


LINK_MD = re.compile(r"\]\(([^)\s]+)\)")
LINK_HTML = re.compile(r"(?:href|src)=\"([^\"]+)\"")


def check_links(out: Path, name: str, text: str, defects: list, counter: dict) -> None:
    pat = LINK_HTML if name.endswith(".html") else LINK_MD
    anchors_cache: dict[str, set] = {}
    for m in pat.finditer(text):
        target = m.group(1)
        if re.match(r"^[a-z]+:", target) or target.startswith("data:"):
            continue
        path, _, frag = target.partition("#")
        counter["links"] += 1
        if path:
            fp = out / path
            if not fp.exists():
                defects.append({"kind": "link-unresolved", "path": name, "detail": f"{target} does not resolve from {out}"})
                continue
        else:
            fp = out / name
        if frag:
            key = str(fp)
            if key not in anchors_cache:
                t = read_text(fp) if fp.exists() else ""
                anchors_cache[key] = set(re.findall(r"id=\"([^\"]+)\"", t))
            if frag not in anchors_cache[key]:
                defects.append({"kind": "anchor-unresolved", "path": name, "detail": f"{target}: no anchor `{frag}` in {fp.name}"})


def cmd_verify(a) -> int:
    out = Path(a.out)
    with Run("evalreport-verify", stage_source="counted") as r:
        man = load_json(out / "presentation-manifest.json")
        if not isinstance(man, dict):
            return refuse(r, f"manifest-missing: {out / 'presentation-manifest.json'} is absent or not JSON")
        plan, err = load_presentation(out / "presentation.json")
        if plan is None:
            return refuse(r, err)
        defects = []
        counter = {"links": 0, "artifacts": 0, "media": 0, "supportingEvidence": 0}
        arts = man.get("artifacts") or []
        r.stage("verify", total=len(arts) + len(man.get("media") or []) + len(man.get("supportingEvidence") or []) + 1, unit="checks")
        if plan.get("presentationId") != man.get("presentationId"):
            defects.append({"kind": "presentation-mismatch", "path": "presentation.json",
                            "detail": f"presentationId {plan.get('presentationId')} is not the manifest's {man.get('presentationId')}"})
        for x in arts:
            p = out / x["path"]
            counter["artifacts"] += 1
            if not p.exists():
                defects.append({"kind": "artifact-missing", "path": x["path"], "detail": "listed in the manifest, absent on disk"})
            elif sha256_file(p) != x.get("sha256"):
                defects.append({"kind": "artifact-changed", "path": x["path"], "detail": "does not hash to the manifest's sha256"})
            elif x["path"].endswith((".md", ".html")):
                check_links(out, x["path"], read_text(p), defects, counter)
            r.advance(1)
        for m in man.get("media") or []:
            p = out / m["path"]
            counter["media"] += 1
            if not p.exists():
                defects.append({"kind": "media-missing", "path": m["path"], "detail": "referenced media absent"})
            elif sha256_file(p) != m.get("sha256"):
                defects.append({"kind": "media-changed", "path": m["path"], "detail": "does not hash to the manifest's sha256"})
            r.advance(1)
        # every assessed picture is on the page or named in an omission line, never dropped unsaid
        if (out / "report.md").is_file():
            for mid_ in media_unaccounted(plan, {"omissions": man.get("omissions") or []}, read_text(out / "report.md")):
                defects.append({"kind": "media-not-shown", "path": "report.md",
                                "detail": f"{mid_} is an assessed picture the report neither shows nor names as left out"})
        for e in man.get("supportingEvidence") or []:
            p = out / e["path"]
            counter["supportingEvidence"] += 1
            if not p.exists():
                defects.append({"kind": "supporting-evidence-missing", "path": e["path"], "detail": "listed in the manifest, absent on disk"})
            elif sha256_file(p) != e.get("sha256"):
                defects.append({"kind": "supporting-evidence-changed", "path": e["path"], "detail": "does not hash to the manifest's sha256"})
            r.advance(1)
        pi = man.get("print") or {}
        pages_now = None
        if pi.get("status") in ("within-limit", "over-limit", "rendered", "count-unverified"):
            if not (out / "report.pdf").exists():
                defects.append({"kind": "pdf-missing", "path": "report.pdf", "detail": f"print status {pi.get('status')} but no PDF"})
            else:
                pages_now, root, note = pdf_page_count(out / "report.pdf")
                if pages_now != pi.get("pages"):
                    defects.append({"kind": "page-count-mismatch", "path": "report.pdf",
                                    "detail": f"recomputed {pages_now} page(s) ({note}); the manifest says {pi.get('pages')}"})
                if pi.get("status") == "within-limit" and pi.get("maxPages") is not None and (pages_now or 0) > pi["maxPages"]:
                    defects.append({"kind": "limit-contradiction", "path": "report.pdf",
                                    "detail": f"{pages_now} page(s) exceed the limit {pi['maxPages']} yet the status is within-limit"})
                if pi.get("status") == "over-limit":
                    # a report over its page limit fails its own acceptance: verify never says ok for it
                    defects.append({"kind": "over-limit", "path": "report.pdf",
                                    "detail": f"{pages_now} page(s) against a limit of {pi.get('maxPages')}"})
        r.advance(1)
        result = {"schemaVersion": 1, "checkedUtc": utc_now(), "out": str(out), "presentationId": plan.get("presentationId"),
                  "ok": not defects, "artifacts": counter["artifacts"], "media": counter["media"],
                  "supportingEvidence": counter["supportingEvidence"], "links": counter["links"],
                  "pages": pages_now, "printStatus": pi.get("status"), "defects": defects}
        write_json(out / "presentation-verify.json", result)
        r.feature("links", counter["links"], "links")
        if pages_now is not None:
            r.feature("pages", pages_now, "pages", at="final")
        if defects:
            for d in defects:
                print(f"DEFECT {d['kind']} {d['path']}: {d['detail']}", file=sys.stderr)
            return refuse(r, f"presentation-refused: {len(defects)} defect(s) - see presentation-verify.json", EXIT_REFUSED_RESULT)
        print(f"verified: {counter['artifacts']} artifact(s), {counter['media']} media file(s), {counter['links']} link(s) resolve"
              + (f", {pages_now} page(s) ({pi.get('status')})" if pages_now is not None else f", print {pi.get('status')}"))
    return EXIT_OK


# ------------------------------------------------------------------------------- narration seam --

NUM = re.compile(r"(?<![\w:./-])\d[\d,]*(?:\.\d+)?(?![\w:./-])")
CHECK_TOKEN = re.compile(r"\b[a-z]+\.[a-z][a-z-]+\b")
ARM_TOKEN = re.compile(r"\b(?:candidate|supplied)-\d\d[\w-]*")
PLUGIN_TOKEN = re.compile(r"[A-Za-z0-9_'()\-]+(?: [A-Za-z0-9_'()\-]+)*\.(?:esp|esm|esl)\b", re.I)
FORMKEY = re.compile(r"\b[0-9A-Fa-f]{6}:[^\s,;()]+\.(?:esp|esm|esl)\b", re.I)


def row_texts(f: dict) -> list[str]:
    """The text of a finding's bound rows (every row, not only the shown ones): the fields a rewrite may quote."""
    return [str(row[k]) for row in f.get("allRows") or f.get("rows") or [] if isinstance(row, dict)
            for k in ("summary", "physicalDetail", "label") if row.get(k)]


def bound_sets(plan: dict, f: dict) -> tuple[set, set]:
    nums = set()

    def walk(v):
        if isinstance(v, bool):
            return
        if isinstance(v, (int, float)):
            nums.add(str(int(v)) if float(v).is_integer() else str(v))
        elif isinstance(v, dict):
            for x in v.values():
                walk(x)
        elif isinstance(v, list):
            for x in v:
                walk(x)
        elif isinstance(v, str):
            for n in NUM.findall(v):
                nums.add(n.replace(",", ""))
    walk(f.get("counts"))
    for row in f.get("allRows") or f.get("rows") or []:
        walk({k: row.get(k) for k in ("summary", "physicalDetail", "label")})
    names = set()
    c = f.get("counts") or {}
    for k in ("A", "B", "probe"):
        if c.get(k):
            names.add(str(c[k]))
    for g in c.get("groups") or []:
        for k in ("A", "B"):
            if g.get(k):
                names.add(str(g[k]))
    for row in f.get("allRows") or f.get("rows") or []:
        for k in ("formKey", "label", "summary", "physicalDetail"):
            if row.get(k):
                names.add(str(row[k]))
    # a bound row is usable whole, so the form keys, plan plugins and plan cells written inside it are bound too
    texts = row_texts(f)
    blob = "\n".join(texts).lower()
    for t in texts:
        for fk in FORMKEY.findall(t):
            names.update((fk, fk.split(":", 1)[1]))
    plugins, _, labels, _ = universe(plan)
    names.update(n for n in plugins | labels if n.lower() in blob)
    if f.get("checkId"):
        names.add(f["checkId"])
    if f.get("armId"):
        names.add(f["armId"])
    arm = next((a for a in plan["scope"]["arms"] if a["armId"] == f.get("armId")), None)
    if arm:
        for n in (arm.get("pluginsAdded") or []) + (arm.get("pluginsRemoved") or []):
            names.add(n)
    return nums, names


def universe(plan: dict) -> tuple[set, set, set, set]:
    plugins, formkeys, labels = set(), set(), set()
    for f in plan["findings"]:
        c = f.get("counts") or {}
        for k in ("A", "B"):
            if c.get(k):
                plugins.add(str(c[k]))
        for row in f.get("allRows") or f.get("rows") or []:
            if row.get("formKey"):
                formkeys.add(row["formKey"])
            if row.get("label"):
                labels.add(row["label"])
    for a in plan["scope"]["arms"]:
        for n in (a.get("pluginsAdded") or []) + (a.get("pluginsRemoved") or []):
            plugins.add(n)
    for t in plan["scope"].get("targets") or []:
        if t.get("name"):
            plugins.add(t["name"])
    checks = set(plan["scope"].get("checks") or {}) | {f["checkId"] for f in plan["findings"] if f.get("checkId")}
    return plugins, formkeys, labels, checks


def validate_answer(plan: dict, f: dict, text: str, pr: dict) -> list[dict]:
    """D7: every number bound, every named plugin / cell / formKey bound, no check or arm outside the plan,
    none of the forbidden words. Returns the rule violations (empty = accepted)."""
    bad = []
    nums, names = bound_sets(plan, f)
    plugins, formkeys, labels, checks = universe(plan)
    arms = {a["armId"] for a in plan["scope"]["arms"]}
    low = text.lower()
    for pat in pr.get("forbiddenPatterns") or []:
        m = re.search(pat, low)
        if m:
            bad.append({"rule": "forbidden-word", "detail": f"contains `{m.group(0)}`"})
    for n in NUM.findall(text):
        v = n.replace(",", "")
        if v not in nums:
            bad.append({"rule": "unbound-number", "detail": f"{n} is not one of the finding's bound numbers"})
    for tok in set(CHECK_TOKEN.findall(text)):
        if tok in checks and tok != f.get("checkId"):
            bad.append({"rule": "unbound-check", "detail": f"names check {tok}, which is not this finding's"})
        elif tok not in checks and re.match(r"^(loadability|records|geometry|functional|state|environment|patch|runtime)\.", tok):
            bad.append({"rule": "unknown-check", "detail": f"names {tok}, which is not a check in the plan"})
    for tok in set(ARM_TOKEN.findall(text)):
        if tok not in arms:
            bad.append({"rule": "unknown-arm", "detail": f"names arm {tok}, which is not in the plan"})
        elif tok != f.get("armId"):
            bad.append({"rule": "unbound-arm", "detail": f"names arm {tok}, which is not this finding's"})
    if re.search(r"\bbaseline\b", low) and f.get("armId") not in (None, "baseline") and (f.get("armId") or "").lower() not in low:
        # the baseline is the reference a differential is stated against, so naming it beside the finding's own
        # arm is fine; naming it INSTEAD of that arm places the finding on the wrong arm
        bad.append({"rule": "unbound-arm", "detail": "names the baseline arm without naming this finding's own arm"})
    for p in plugins:
        if p.lower() in low and p not in names:
            bad.append({"rule": "unbound-plugin", "detail": f"names {p}, which is not bound to this finding"})
    masked = text
    whole = set(row_texts(f))
    for known in sorted(set(names) | set(plugins), key=len, reverse=True):
        if known in whole or known.lower().endswith((".esp", ".esm", ".esl")):
            masked = re.sub(re.escape(known), " " * len(known), masked, flags=re.I)
    for tok in set(PLUGIN_TOKEN.findall(masked)):
        short = " ".join(tok.split()[-4:])
        bad.append({"rule": "unknown-plugin", "detail": f"names {short}, which is not a plugin bound to this finding"})
    for fk in set(FORMKEY.findall(text)):
        if fk not in names and fk.upper() not in {n.upper() for n in names}:
            bad.append({"rule": "unbound-formkey" if fk in formkeys else "unknown-formkey",
                        "detail": f"names {fk}, which is not bound to this finding"})
    for lab in labels:
        if len(lab) >= 6 and lab.lower() in low and lab not in names:
            bad.append({"rule": "unbound-cell", "detail": f"names {lab}, which is not bound to this finding"})
    return bad


def load_narration() -> dict:
    """The narrator's words, the glossary and the fidelity judge's instructions (the reporting skill's narration.json)."""
    d = load_json(NARRATION)
    n = (d or {}).get("narrator") if isinstance(d, dict) else None
    if (not isinstance(n, dict) or not all(isinstance(n.get(k), str) for k in ("lead", "plainWords", "glossaryPointer", "binding"))
            or not isinstance(d.get("glossary"), list) or not isinstance((d.get("judge") or {}).get("instructions"), str)):
        raise SystemExit(f"REFUSED: narration.json is absent or incomplete at {NARRATION}")
    return d


def narrator_instructions(nd: dict) -> str:
    n = nd["narrator"]
    return " ".join(n[k] for k in ("lead", "plainWords", "glossaryPointer", "binding"))


def read_answers(path: Path, plan: dict):
    """(answers list, None) or (None, refusal reason)."""
    answers = load_json(path)
    if isinstance(answers, dict) and isinstance(answers.get("answers"), dict):
        answers = {**answers, "answers": [{"id": k, "text": v} for k, v in answers["answers"].items()]}
    if not isinstance(answers, dict) or not isinstance(answers.get("answers"), list):
        return None, (f"answers-unreadable: {path} is not a JSON object with an `answers` list "
                      f"([{{id, text}}]) or mapping ({{id: text}})")
    if answers.get("presentationId") not in (None, plan.get("presentationId")):
        return None, (f"answers-foreign: the answers name presentation {answers.get('presentationId')}, "
                      f"this is {plan.get('presentationId')}")
    return answers["answers"], None


def cmd_narrate_packet(a) -> int:
    ppath = Path(a.presentation)
    nd = load_narration()
    answers_path = getattr(a, "answers", None)   # a caller of the function form may predate --answers
    with Run("evalreport-narrate", mode="judge-packet" if answers_path else "packet", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        if answers_path:
            answers, err = read_answers(Path(answers_path), plan)
            if answers is None:
                return refuse(r, err)
            by_id = {f["id"]: f for f in plan["findings"]}
            r.stage("judge-packet", total=len(answers), unit="answers")
            items = []
            for ans in answers:
                f = by_id.get((ans or {}).get("id"))
                text = (ans or {}).get("text")
                if f is not None and isinstance(text, str) and text.strip():
                    # narrate-apply refuses an unknown id or an empty text by itself; the judge sees only real answers
                    # every bound row, not only the shown ones: the fields bound_sets lets a narrator use
                    rows = [{k: row.get(k) for k in ("formKey", "label", "summary", "physicalDetail")}
                            for row in f.get("allRows") or f.get("rows") or [] if isinstance(row, dict)]
                    items.append({"id": f["id"], "title": f.get("title"), "template": f.get("plain"),
                                  "counts": f.get("counts"), "rows": rows, "text": text, "textSha256": sha256_text(text)})
                r.advance(1)
            packet = {"schemaVersion": 1, "presentationId": plan.get("presentationId"),
                      "instructions": nd["judge"]["instructions"], "glossary": nd["glossary"], "answers": items}
            write_json(Path(a.out), packet)
            r.artifact(str(a.out))
            r.feature("answers", len(items), "answers")
            print(f"judge packet: {len(items)} answer(s) -> {a.out}")
            return EXIT_OK
        r.stage("packet", total=len(plan["findings"]), unit="findings")
        items = []
        for f in plan["findings"]:
            nums, names = bound_sets(plan, f)
            items.append({"id": f["id"], "kind": f["kind"], "checkId": f.get("checkId"), "armId": f.get("armId"), "part": f.get("part"),
                          "title": f.get("title"), "template": f.get("plain"), "counts": f.get("counts"),
                          "boundNumbers": sorted(nums, key=lambda s: (len(s), s)), "boundNames": sorted(names),
                          "rows": (f.get("allRows") or f.get("rows") or [])[: max(1, plan["settings"].get("rowsPerGroup") or 3)]})
            r.advance(1)
        packet = {"schemaVersion": 1, "presentationId": plan.get("presentationId"),
                  "instructions": narrator_instructions(nd), "glossary": nd["glossary"],
                  "checks": sorted(plan["scope"].get("checks") or {}), "arms": [x["armId"] for x in plan["scope"]["arms"]],
                  "findings": items,
                  # item 18 (4.24 phase 3): the order sentence, read-only for a narrator, with the numbers it binds
                  "loadOrder": ({"sentence": lo_["sentence"], "boundNumbers": sorted(set(re.findall(r"\b\d[\d,]*\b",
                                                                                                    lo_["sentence"])))}
                                if isinstance((lo_ := ((plan.get("reader") or {}).get("prediction") or {}).get("loadOrder")), dict)
                                and lo_.get("sentence") else None)}
        write_json(Path(a.out), packet)
        r.artifact(str(a.out))
        r.feature("findings", len(items), "findings")
        print(f"packet: {len(items)} finding(s) -> {a.out}")
    return EXIT_OK


def judgement_refusal(j: dict | None, text: str) -> dict | None:
    """None when a judgement made for exactly this text says the meaning is kept; else the refusal."""
    if not isinstance(j, dict):
        return {"rule": "unjudged", "detail": "the fidelity judge gave no judgement for this answer"}
    if j.get("textSha256") != sha256_text(text):
        return {"rule": "judgement-stale", "detail": "the judgement was made for another text"}
    if j.get("faithful") is not True:
        return {"rule": "judged-unfaithful", "detail": str(j.get("reason") or "the judge found the meaning changed")}
    return None


def cmd_narrate_apply(a) -> int:
    pr = load_priorities()
    ppath = Path(a.presentation)
    with Run("evalreport-narrate", mode="apply", stage_source="counted") as r:
        if not a.judgements:
            return refuse(r, "judgements-missing: pass --judgements FILE, the fidelity judge's answer to `narrate-packet "
                             "--answers` run in the agent `evalsettings.py route models.checkExplanations` names")
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        answers, err = read_answers(Path(a.answers), plan)
        if answers is None:
            return refuse(r, err)
        jd = load_json(Path(a.judgements))
        if not isinstance(jd, dict) or not isinstance(jd.get("judgements"), list):
            return refuse(r, f"judgements-unreadable: {a.judgements} is not a JSON object with a `judgements` list")
        if jd.get("presentationId") != plan.get("presentationId"):
            return refuse(r, f"judgements-foreign: the judgements name presentation {jd.get('presentationId')}, this is "
                             f"{plan.get('presentationId')}: judge this presentation's answers")
        judgements = {j.get("id"): j for j in jd["judgements"] if isinstance(j, dict)}
        by_id = {f["id"]: f for f in plan["findings"]}
        r.stage("apply", total=len(answers), unit="answers")
        accepted, refused = 0, []
        for ans in answers:
            fid = (ans or {}).get("id")
            text = (ans or {}).get("text")
            f = by_id.get(fid)
            if f is None:
                refused.append({"id": fid, "rule": "unknown-finding", "detail": f"{fid} is not in the plan"})
                r.advance(1)
                continue
            if not isinstance(text, str) or not text.strip():
                refused.append({"id": fid, "rule": "empty-answer", "detail": "no text"})
                r.advance(1)
                continue
            bad = validate_answer(plan, f, text, pr)
            if not bad:
                jr = judgement_refusal(judgements.get(fid), text)
                bad = [jr] if jr else []
            if bad:
                for x in bad:
                    refused.append({"id": fid, **x})
            else:
                f.setdefault("templatePlain", f["plain"])
                old_plain = f["plain"]
                f["plain"] = text.strip()
                f["readerPlain"] = text.strip()
                f["narrated"] = True
                for chapter in plan.get("chapters") or []:
                    if f.get("id") not in (chapter.get("findingIds") or []):
                        continue
                    for caption in chapter.get("captions") or []:
                        for key in ("text", "narration"):
                            if old_plain in str(caption.get(key) or ""):
                                caption[key] = str(caption[key]).replace(old_plain, text.strip())
                accepted += 1
            r.advance(1)
        plan["narration"] = {"source": f"answers:{sha256_file(Path(a.answers))[:12]}", "validated": True,
                             "judgements": f"judgements:{sha256_file(Path(a.judgements))[:12]}",
                             "accepted": accepted, "refused": refused}
        dest = Path(a.out) if a.out else ppath
        stamp(plan)
        write_json(dest, plan)
        r.artifact(str(dest))
        r.feature("findings", len(plan["findings"]), "findings")
        r.tally(ok=accepted, failed=len(refused))
        print(f"narration: {accepted} answer(s) accepted, {len(refused)} refusal(s); wrote {dest}")
        for x in refused:
            print(f"  refused {x['id']}: {x['rule']} - {x['detail']}")
    return EXIT_OK


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalreport", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("plan", help="verify the bundle and compile the presentation plan")
    p.add_argument("--bundle", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--mode", choices=("concise", "exhaustive"), default=None,
                   help="default: the saved evaluation settings' report.mode, else concise")
    p.add_argument("--exhaustive", action="store_true", help="list every row; no page limit (same as --mode exhaustive)")
    p.add_argument("--max-pages", type=int, default=None)
    p.add_argument("--rows-per-group", type=int, default=None)
    p.add_argument("--figures-per-finding", type=int, default=None)
    p.add_argument("--verify-copy", action="store_true",
                   help="no effect: plan never writes into a bundle it reads; kept so recorded commands still run")
    p.add_argument("--location-scope", action="append", default=[], metavar="ARM=FILE",
                   help="bind a scopeprobe location-contributor document to an arm; repeat for multiple arms")
    p.add_argument("--order-setup", action="append", default=[], metavar="ARM@PLAN=ROWS",
                   help="the record lane's rows (ROWS: a `rows` list or a `Conflicts` emit, each row naming its check) "
                        "measured on the plan PLAN's arm ARM: the setup without the patch with its plugins in another "
                        "order, same plugins and bytes. Changes lost there between a pair whose order differs, that "
                        "the measured order does not lose, are said as a load-order risk; repeat for several")
    p.add_argument("--single-mod", choices=("show", "aside"), default=None,
                   help="a clash one evaluated mod has with a third mod on its own: shown under its own heading (show) "
                        "or set aside with a count (aside); never counted in the verdict. Default: the reporting "
                        "skill's singleModDefault")
    p.add_argument("--headline-unit", choices=("relationships", "objects"), default=None,
                   help="what the verdict counts: the check's relationships (rows) or the objects they name. Default: "
                        "the reporting skill's headlineUnit")
    p.add_argument("--also-bundle", action="append", default=[], metavar="DIR",
                   help="a further evaluation of the same mods, named in the report with the setups it measured; its "
                        "results are not joined into the counts")
    p.add_argument("--sibling-bundle", action="append", default=None, metavar="DIR",
                   help="a bundle of the same order without the patch, given once per evaluation; its setups attribute "
                        "this report's prediction where they are this bundle's setup without the patch, or that setup "
                        "with plugins left out, plugin for plugin (at least one must hold that setup itself)")
    p.add_argument("--precedent", default=None, metavar="DIR",
                   help="the precedent stage's cards folder (its summary.json: each patch's census of its own records and "
                        "its cards' deciding reasons); the patch section counts the patch's own edits from it")
    p.add_argument("--patch-search", default=None, metavar="FILE",
                   help="the recorded Nexus patch search the review reel's patch sentences rest on (scene-policy.json "
                        "vignettes.patchSearch); without it the reel plan is refused and the report still compiles")
    p.add_argument("--mod-name", action="append", default=[], metavar="PLUGIN=NAME",
                   help="the name the review reel speaks for a plugin's mod, over its Nexus page's name and its MO2 mod folder")
    p.add_argument("--nexus-names", choices=("offline", "fetch"), default="offline",
                   help="the review reel's mod names from houseCARL's Nexus catalogue: offline reads the stored pages only; "
                        "fetch also asks Nexus once (housecarl_nexus_catalog add=) for each page the catalogue lacks, and "
                        "records each fetch in evidence/mod-names.json; a failed fetch leaves the folder name with its reason")
    p.add_argument("--nexus-reader-cmd", default=None, metavar="CMD",
                   help="the houseCARL server --nexus-names fetch asks through (default HOUSECARL_RECORD_READER_CMD, else "
                        "this checkout's built server)")
    p.add_argument("--captures-final", action="store_true",
                   help="the capture stage is closed: the review reel pictures only clashes with captured frames and "
                        "says in one sentence when a kind has none (the report's own plan after the last capture round)")
    p.add_argument("--capture-results", action="append", default=[], metavar="FILE",
                   help="capture_acquire.py capture results: a refused vignette becomes a still, its reason on the closing")
    p.add_argument("--declare-patch", action="append", default=[], metavar="PLUGIN",
                   help="declare a plugin as a patch under evaluation; repeat for several. Patch "
                        "identity is never inferred from plugin membership, so an arm that differs "
                        "only by an undeclared plugin is reported as an unstated difference. Use "
                        "this for a plan whose candidates predate the declaration field. A file that "
                        "shares its name with another (an author's same-named compatible version) is "
                        "declared by its bytes, PLUGIN=sha256:<64 hex>, or by its mod folder, "
                        "PLUGIN=folder:<folder>; a declaration that matches no setup's copy is refused.")
    p.set_defaults(fn=cmd_plan)
    p = sub.add_parser("triage", help="compile an annotated overlap manifest into a readable incident catalogue")
    p.add_argument("--manifest", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--policy", default=str(TRIAGE_POLICY))
    p.set_defaults(fn=cmd_triage)
    p = sub.add_parser("report", help="render report.md / report.html / report.pdf / evidence-index.md from the plan")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--no-print", action="store_true", help="skip the browser; print.status is not-rendered")
    p.set_defaults(fn=cmd_report)
    p = sub.add_parser("verify", help="re-hash the artifacts, resolve every link, recount the pages")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_verify)
    p = sub.add_parser("narrate-packet", help="write every finding's bound numbers and names for an optional narrator, "
                                              "or, with --answers, the fidelity judge's packet")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--answers", default=None,
                   help="a narrator's answers: write the fidelity judge's packet for them instead of the narrator's")
    p.set_defaults(fn=cmd_narrate_packet)
    p = sub.add_parser("narrate-apply", help="accept narrated sentences only where every number and name is bound and a judgement says the meaning is kept")
    p.add_argument("--presentation", required=True)
    p.add_argument("--answers", required=True)
    p.add_argument("--judgements", default=None,
                   help="the fidelity judge's answer to `narrate-packet --answers`; required: a rewrite is accepted only "
                        "where a judgement made for its exact text says it keeps the meaning")
    p.add_argument("--out", default=None)
    p.set_defaults(fn=cmd_narrate_apply)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return EXIT_REFUSED_INPUT


if __name__ == "__main__":
    sys.exit(main())
