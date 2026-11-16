"""The review reel's plan: which clashes it shows, how, and what it says (Piranha's vignette format, task
2026-10-19-review-reel-vignettes).

    plan_reel(pres, names, masters, policy, words, patch_search, mod_names, capture_results=None, markers=, switches=) -> dict

`pres` is the presentation evalreport is compiling (its `reader.prediction`, `reader.missingSummary`,
`reader.suppliedPatches`, `scope.arms` and `media`); `names` the bundle's names enrichment (refs, bases, objects,
places); `masters` each plugin's own master list (lower case); `policy` the skill's `scene-policy.json` `vignettes`
block (plus the report's `effectModelPattern`); `words` the skill's `report-priorities.json` `reel.words`;
`patch_search` the recorded Nexus search (`evalreport plan --patch-search`); `mod_names` {plugin: {name, source}}
(modnames.py: evalreport --mod-name, else the Nexus page's name, else the MO2 mod folder's); `capture_results` the
capture step's per-request results: a refused clash is counted as one that cannot be pictured and the next drawable
clash of its type takes its place (at most `captureAttemptsPerType` are tried); `switches` the prediction setup's
switch census, for a world-state clash's condition.

Every kept clash is pictured (an overlap, contact or world-state vignette: without, with, highlight; a placement split:
the other mod's spot on the left, the winner's with the mod added on the right), represented by its type's "There are
N cases" sentence, or counted in the one statement of clashes the reel cannot picture. `accounting` records which.

The narration is built only from the skill's templates and typed slots: mod names, in-game place and object names
(else the editor id in words), field labels and patch names. No report sentence (verdict, scope, limitations, finding
text) is read here, so none can be spoken. Selection reads only the prediction record, never which media exists, so
the plan before capture and after rebundling selects the same clashes.

Returns {"status": "planned", chapters, ...} or {"status": "refused", "reason": one sentence}. A refusal leaves the
report untouched: evalreel refuses to render a refused reel.
"""
from __future__ import annotations

import collections
import hashlib
import json
import re

import reel_stills

SCHEMA = 1


class Refused(ValueError):
    pass


def pair_search_gap(search: dict | None, pair, policy: dict) -> str | None:
    """The pair as said in a refusal ("A and B") when the recorded patch search does not cover it: no entry for the pair
    (names matched without case), or its queries lack a tool the skill requires (scene-policy.json
    vignettes.patchSearch.requiredTools). None when it is covered. The one coverage rule: the reel plan and evalpipeline's
    check before a run both ask it."""
    need = set((policy.get("patchSearch") or {}).get("requiredTools") or [])
    want = frozenset(str(m).lower() for m in pair)
    entry = None
    for e in (search or {}).get("pairs") or []:
        if frozenset(str(x).lower() for x in e.get("pair") or []) == want:
            entry = e                                  # the last entry for a pair stands, as the plan's lookup reads it
    tools = {q.get("tool") for q in (entry or {}).get("queries") or []}
    return None if entry is not None and need <= tools else " and ".join(pair)


def pair_search_refusal(gaps: list[str], policy: dict, what: str = "the patch-search file") -> str:
    """One sentence naming every uncovered pair and the tools each needs."""
    need = ", ".join(sorted(set((policy.get("patchSearch") or {}).get("requiredTools") or [])))
    if len(gaps) == 1:
        return f"{what} does not cover the pair {gaps[0]} with {need}; search it and record the queries"
    return f"{what} does not cover the pairs {'; '.join(gaps)} with {need}; search them and record the queries"


def possessive(name) -> str:
    """A name's possessive: an apostrophe alone after a final s ("Interesting NPCs'"), else "'s" ("Lux's")."""
    s = str(name)
    return s + "'" if s[-1:].casefold() == "s" else s + "'s"


POSSESSIVE_SLOT = re.compile(r"\{(\w+)\}'s\b")


def fill(template, **slots) -> str:
    """Every report and reel template is filled here: a `{slot}'s` takes the possessive rule for the value filled in, so
    a name ending in s never reads "s's"; then the slots are filled as str.format does."""
    t = str(template)

    def one(m):
        v = slots.get(m.group(1))
        return m.group(0)[:-1] if isinstance(v, str) and v[-1:].casefold() == "s" else m.group(0)
    return POSSESSIVE_SLOT.sub(one, t).format(**slots)



def unmarkable_objects(capture_results) -> set:
    """Objects no capture can mark, from FINISHED see-through refusals only: every fallback (eye-level with a measured
    coverage mark, cutaway, top-down) was tried and the object's own drawn coverage never reached the mark's threshold.
    A bare points-no-opaque-face is not enough: that code also named objects with opaque shapes that simply stood out
    of sight (two such objects in one evaluation), which the fallbacks now picture."""
    out = set()
    for x in capture_results or []:
        ref = x.get("refusal") or {}
        if x.get("status") == "refused" and ref.get("final") is True and ref.get("code") == "see-through-unmarked"                 and ref.get("participant"):
            out.add(str(ref["participant"]))
    return out


def view_kind_line(provenance: dict) -> str:
    """The words a reel still adds for a picture that is not what the player sees ("" for one that is)."""
    kind = (provenance or {}).get("viewKind") or "eye-level"
    if kind == "cutaway":
        n = len((provenance or {}).get("hidden") or [])
        return f"Cutaway: {n} object{'' if n == 1 else 's'} hidden so the pair shows; the player's view has them."
    if kind == "top-down":
        cut = (provenance or {}).get("cullAbove")
        n = len((provenance or {}).get("hidden") or [])
        return ("Sliced top-down view from above" + (f", everything above z {float(cut):.0f} removed" if cut is not None else "")
                + (f", {n} object{'' if n == 1 else 's'} over the pair hidden" if n else "") + "; not the player's view.")
    return ""

def humanize(eid: str | None) -> str | None:
    """An editor id in words: a leading mod prefix (`MAG_`) and standalone digits dropped, camel case split."""
    if not eid:
        return None
    s = re.sub(r"^[A-Za-z0-9]{1,5}_", "", str(eid)).replace("_", " ")
    s = re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])", " ", s)
    ws = [w for w in s.split() if not w.isdigit()]
    # a mod prefix written in capitals with no underscore ("XJKRRDLC2RRBuilding03...", "DLC2Tree..."): the leading
    # all-capital words are dropped while a word in ordinary case follows (Missives reel review V3)
    while len(ws) > 1 and ws[0].isupper() and len(ws[0]) >= 2 and any(not w.isupper() for w in ws[1:]):
        ws = ws[1:]
    return " ".join(ws) or None


NUMBER_WORDS = {2: "two", 3: "three", 4: "four", 5: "five"}


def account_counts(words: dict, pictured: int, represented: int, unpictured: int) -> str:
    """The unpictured account's counts in words: a clause per count that is not 0, joined with commas and a last "and"
    ("2 pictured and 2 not pictured", never "0 represented by the pictured example"; C2167)."""
    wc = words.get("unpicturedCounts") or {}
    cl = [fill(wc[k], count=n) for k, n in (("pictured", pictured), ("represented", represented),
                                             ("unpictured", unpictured)) if n]
    return cl[0] if len(cl) == 1 else ", ".join(cl[:-1]) + wc.get("and", " and ") + cl[-1] if cl else ""


def say_names(names: list[str]) -> list[str]:
    """One phrase per distinct spoken name, in first-seen order, with how many records share it (Missives final reel
    review, finding 2: two barrels with one editor name sounded like one barrel moved, re-modelled and removed)."""
    counts = {}
    for n in names:
        counts[n] = counts.get(n, 0) + 1
    return [n if k == 1 else f"{n} ({NUMBER_WORDS.get(k, str(k))} of them)" for n, k in counts.items()]


def result_about(x: dict, fks) -> bool:
    """Whether a capture result is about this clash (its first key and its partners): the result's record keys start
    with the clash's first key and hold every partner. A result without record keys falls back to its first key."""
    fks = [str(k) for k in fks]
    if not x.get("formKey"):
        return False
    keys = [str(k) for k in x.get("recordKeys") or []]
    if keys:
        return keys[0] == fks[0] and set(fks) <= set(keys)
    return str(x["formKey"]) == fks[0]


def clash_refused(fks, capture_results) -> bool:
    """Whether a capture result refused this clash. A clash is refused only when the request was about it
    (`result_about`): a refusal of one request never marks another request on the same object refused (WRW attempt 5:
    one object's refused pair dropped its two captured pairs)."""
    return any(x.get("status") == "refused" and result_about(x, fks) for x in capture_results or [])


def buried_participant(x: dict) -> dict | None:
    """{formKey, partner, stations, partnerSeeThrough} when a refused capture shows a participant buried: the top-down refused it as unseen
    from above after every fallback, no cutaway step succeeded, and the eye-level step's own counts (fields, never its
    sentence) say that participant showed a measurable face from none of the N >= 1 placed stations. Else None: a pose
    the planner refused after a station saw both, a request with no station placed, or an older result without the
    counts is never called buried."""
    if x.get("status") != "refused":
        return None
    ref = x.get("refusal") or {}
    if ref.get("final") is not True or ref.get("code") != "top-down-participant-unseen":
        return None
    chain = ref.get("chain") or []
    if any(c.get("kind") == "cutaway" and c.get("status") == "ok" for c in chain):
        return None
    eye = next((c for c in chain if c.get("kind") == "eye-level"), None) or {}
    vis, n = eye.get("visibleFrom"), eye.get("stationsPlaced")
    if not isinstance(vis, dict) or not isinstance(n, int) or isinstance(n, bool) or n < 1:
        return None
    unseen = sorted(str(k) for k, v in vis.items() if v == 0)
    if not unseen:
        return None
    k = str(ref.get("participant")) if str(ref.get("participant")) in unseen else unseen[0]
    keys = [str(o) for o in (x.get("recordKeys") or vis)]
    partner = next((o for o in keys if o != k), None)
    # the partner drawn only by see-through shapes (the picker's own measurement) hides nothing, so it is not what buries k
    clear = {str(o) for o in eye.get("allNonOpaque") or []}
    return {"formKey": k, "partner": partner, "stations": n, "partnerSeeThrough": partner in clear}


def station_saw(x: dict) -> bool:
    """Whether a refused capture's own chain says an eye-level station saw both participants: the cutaway step skipped
    because a station saw both, or a witness step (the alpha-card rule) tried; or an eye-level camera was ready and the
    capture refused after it (C1932). Such a clash is never one no camera could see (C1896). A refusal of the
    acquisition itself (no camera ready) keeps its own reasons."""
    if x.get("status") != "refused":
        return False
    acq = x.get("acquisition") or {}
    if acq.get("status") == "camera-ready" and acq.get("viewKind") == "eye-level":
        return True
    chain = (x.get("refusal") or {}).get("chain") or []
    return any(c.get("code") == "cutaway-not-applicable" or c.get("kind") == "witness" for c in chain)


def refusal_reason(x: dict, table: dict | None) -> str:
    """The stillReasons key for one refused capture result, from the skill's one table of reasons by refusal code
    (scene-policy vignettes.refusalReasons; the picker's code first, then the acquisition's): every code the capture side
    can emit has its own entry (C1936). A reference not drawn is said by viewprobe's own reason (notDrawn: switched off
    for good, starts switched off, deleted, no model). `captureRefused` only for a code the table does not know."""
    ref = x.get("refusal") or {}
    code = ref.get("pickerCode") or ref.get("code")
    why = (table or {}).get(str(code)) if code else None
    if why == "notDrawn":
        nd = ref.get("notDrawn") or {}
        # each line only on viewprobe's own cause fields; a refusal without them (an older capture) gets the neutral
        # true line, never "no model to draw" (C1960)
        return ("switchedOff" if nd.get("forGood") else "startsOff" if nd.get("why") in ("enableParent", "initiallyDisabled")
                else "deleted" if nd.get("why") == "deleted" else "notDrawn" if nd.get("why") in ("noModel", "noBase")
                else "notDrawnUnknown")
    return why or "captureRefused"


def unpictured_refused_reason(fks, capture_results, unmarkable: set, table: dict | None = None) -> str:
    """Why a refused, unburied clash has no picture (stillReasons keys): a place only the mod has
    (`placeOnlyWithMod`), a camera that sees it with the mod stands inside another object without it (`stateBlocked`),
    a station at a player's eye height saw it but no picture could be kept (`stationSaw`), the
    renderer cannot mark one of its objects' see-through shapes (`unmarkable`), else its refusal code's own reason from
    the table (refusal_reason)."""
    if any(result_about(x, fks) and (x.get("refusal") or {}).get("code") == "place-only-with-mod"
           for x in capture_results or []):
        return "placeOnlyWithMod"
    # a camera that sees the clash with the mod but stands inside another object without it: said as that, not as
    # "seen but not kept", whatever the last fallback refused for (C2168)
    if any(result_about(x, fks) and x.get("status") == "refused"
           and any(c.get("code") == "state-camera-blocked" for c in (x.get("refusal") or {}).get("chain") or [])
           for x in capture_results or []):
        return "stateBlocked"
    if any(result_about(x, fks) and station_saw(x) for x in capture_results or []):
        return "stationSaw"
    if {str(k) for k in fks} & set(unmarkable or ()):
        return "unmarkable"
    x = next((x for x in capture_results or [] if x.get("status") == "refused" and result_about(x, fks)), None)
    return refusal_reason(x, table) if x is not None else "captureRefused"


def clash_buried(fks, capture_results) -> dict | None:
    """The buried participant of the refused capture about this clash (`result_about`), or None."""
    return next((b for x in capture_results or [] if result_about(x, fks) and (b := buried_participant(x))), None)


def captured_request(fks, capture_results) -> str | None:
    """The id of the captured request about this clash (`result_about`), or None."""
    return next((x.get("request") for x in capture_results or []
                 if x.get("status") == "captured" and result_about(x, fks)), None)


def short_mod_names(names, words: dict, subject=(), same=()) -> dict:
    """{full name: short form} for each long mod name (item 5): the part before its first separator, taken from its own
    name (skill data `shortForm`: "BUVARP RE - Barely Used ..." -> "BUVARP RE"). None for a name that is not long, has no
    separator, names itself a patch (its head is the mod it patches), or whose short form is not short, or equals
    another name, another name's head or another candidate (casefolded): such a name is said in full every time.
    `names` holds every name the text can say, plugin file stems included, so a collision is seen.

    (report words) A patch named after the evaluated mod (`subject`: its mod name and plugin stem), "<subject> - <rest>",
    is said by its rest when the rest still names a patch ("Environs - Hroggars House - BUVARP Patch" -> "BUVARP
    Patch"), at any length: the dropped words repeat the subject. A name that names a patch is never said by its own head,
    so its head blocks no other name's short form; its own short form does. `same` holds groups of names that are one
    plugin (its mod name and its file stem): they never collide with each other."""
    sf = words.get("shortForm") or {}
    if not sf:
        return {}
    seps = list(sf.get("separators") or [" - "])
    patch_words = [str(x).casefold() for x in sf.get("patchWords") or []]
    names = list(dict.fromkeys(str(n) for n in names if str(n or "").strip()))
    group = {}
    for i, g in enumerate(same or ()):
        for n in g or ():
            if str(n or "").strip():
                group.setdefault(str(n).casefold(), set()).add(i)
    subjects = [str(x) for x in subject or () if str(x or "").strip()]

    def head(n):
        cut = [n.find(s) for s in seps if n.find(s) > 0]
        return (n[:min(cut)].strip(), n[min(cut):]) if cut else (None, "")

    def patchy(text):
        return any(re.search(r"\b" + re.escape(w) + r"\b", text.casefold()) for w in patch_words)
    heads = {n: head(n) for n in names}
    names_patch = {n for n in names if patchy(heads[n][1])}
    cand = {}
    for n in names:
        h, tail = heads[n]
        if n in names_patch:
            rest = next((n[len(sj) + len(sp):].strip() for sj in subjects for sp in seps
                         if n.casefold().startswith((sj + sp).casefold())), None)
            if rest and patchy(rest) and rest.casefold() != n.casefold():
                cand[n] = rest
            continue
        if len(n) <= int(sf.get("minLength") or 40) or not h or len(h) < 2 or len(h) * 2 > len(n):
            continue
        cand[n] = h

    def one_plugin(a, b):
        return bool(group.get(a.casefold(), set()) & group.get(b.casefold(), set()))
    out = {}
    for n, h in cand.items():
        hk = h.casefold()
        clash = any(o != n and not one_plugin(o, n) and (
            o.casefold() == hk or (o not in names_patch and (heads[o][0] or "").casefold() == hk)
            or (cand.get(o) or "").casefold() == hk) for o in names)
        if not clash:
            out[n] = h
    return out


#: a still page's data fields, never text: left as they are when short forms are said
SHORT_FORM_DATA_KEYS = frozenset({"id", "layout", "path", "png", "sha256", "mediaId", "formKey", "plugins", "affected",
                                  "seconds", "maxWords", "kind"})


def plugin_name_groups(mod_names: dict) -> list:
    """Each plugin's own names (its mod name and its file stem): one plugin, so they never collide in short_mod_names."""
    return [{re.sub(r"\.(esp|esm|esl)$", "", str(k), flags=re.I), str((v or {}).get("name") if isinstance(v, dict) else v)}
            for k, v in (mod_names or {}).items()]


def short_form_pattern(short: dict, ext: bool = False):
    """A name of `short` (longest first, so a longer name holding a shorter one is matched whole), then, with `ext`, its
    plugin extension, then a possessive the filler wrote for it ("'s", or "'" after a final s)."""
    names_ = "|".join(re.escape(n) for n in sorted(short, key=len, reverse=True))
    return re.compile(r"(?P<name>" + names_ + r")(?P<ext>\.(?:esp|esm|esl))" + ("?" if ext else "{0}")
                      + r"(?P<own>'s\b|(?<=s)'(?![A-Za-z]))?")


def said_short(short_form: str, own) -> str:
    """A short form in place of a name, its possessive said again by the one rule (possessive), so "Foo Patches'"
    shortened to "Foo" reads "Foo's"."""
    return possessive(short_form) if own else short_form


def say_short_forms(chapters: list, short: dict) -> None:
    """(item 5) Each long mod name said in full at its first mention and by its short form after, in the reel's own
    order: once for the narration, once for the pages' spoken lines (say, sayLead) and once for what the pages and frame
    labels show. A longer name holding a shorter one is matched whole first. Edits the chapters in place."""
    if not short:
        return
    pat = short_form_pattern(short)
    spoken_seen, shown_seen = set(), set()

    def fix(s, seen):
        def one(m):
            n = m.group("name")
            if n in seen:
                return said_short(short[n], m.group("own"))
            seen.add(n)
            return m.group(0)
        return pat.sub(one, s) if isinstance(s, str) else s

    def page(x, key=None):
        """A still page: its title on the shown channel; its lead, fixed say and body (what reel_stills.page_say speaks,
        and the page also shows) on the spoken one, in page order; its data fields left as they are."""
        if isinstance(x, str):
            return x if key in SHORT_FORM_DATA_KEYS else fix(x, shown_seen if key == "title" else spoken_seen)
        if isinstance(x, list):
            return [page(v, key) for v in x]
        if isinstance(x, dict):
            order = sorted(x, key=lambda k: {"title": 0, "sayLead": 1, "say": 2}.get(k, 3))
            done = {k: page(x[k], k) for k in order}
            return {k: done[k] for k in x}
        return x
    for ch in chapters:
        pages = (ch.get("still") or {}).get("pages") if isinstance(ch.get("still"), dict) else None
        if pages:
            # a still chapter's narration is its pages' words: said with the decisions its pages get
            if isinstance(ch.get("narration"), str):
                ch["narration"] = fix(ch["narration"], set(spoken_seen))
            ch["still"] = {**ch["still"], "pages": [page(p) for p in pages]}
        elif isinstance(ch.get("narration"), str):
            ch["narration"] = fix(ch["narration"], spoken_seen)
        if isinstance(ch.get("frames"), list):
            ch["frames"] = [{**f, "label": fix(f["label"], shown_seen)} if isinstance(f, dict) and isinstance(
                f.get("label"), str) else f for f in ch["frames"]]


def joined(xs: list[str]) -> str:
    return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


def navmesh_earlier_plugin(request_media: list[dict]) -> str | None:
    """The plugin whose earlier version of the source navmesh a navmesh comparison's historical frame draws, read from
    that frame's own renderer command (--nav-version SOURCE=PLUGIN), or None."""
    for m in request_media:
        av = [str(x) for x in ((m or {}).get("provenance") or {}).get("argv") or []]
        for i, x in enumerate(av[:-1]):
            if x == "--nav-version" and "=" in av[i + 1]:
                return av[i + 1].rsplit("=", 1)[1]
    return None


def camera_of(argv) -> dict | None:
    """The renderer camera a capture state ran with, from its argv: eye, yaw (degrees clockwise from +Y), pitch (positive
    looks down), vertical fov and aspect (width:height)."""
    a = [str(x) for x in argv or []]

    def val(flag):
        return a[a.index(flag) + 1] if flag in a and a.index(flag) + 1 < len(a) else None
    eye, yaw = val("--eye"), val("--yaw")
    if not eye or yaw is None:
        return None
    w, _, h = (val("--aspect") or "1:1").partition(":")
    return {"eye": [float(x) for x in eye.split(",")], "yaw": float(yaw), "pitch": float(val("--pitch") or 0.0),
            "fov": float(val("--fov") or 65.0), "aspect": float(w) / float(h or 1)}


def in_view(cam: dict, pos, margin: float = 0.9) -> bool:
    """Whether a world point projects inside the camera's frame (a pinhole projection; `margin` keeps it off the
    edges). Occlusion is not judged: a point inside the frame may still be hidden behind something."""
    import math
    y, p = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    fwd = (math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), -math.sin(p))
    right = (math.cos(y), -math.sin(y), 0.0)
    up = (right[1] * fwd[2] - right[2] * fwd[1], right[2] * fwd[0] - right[0] * fwd[2], right[0] * fwd[1] - right[1] * fwd[0])
    d = [float(pos[i]) - float(cam["eye"][i]) for i in range(3)]
    z = sum(d[i] * fwd[i] for i in range(3))
    if z <= 1.0:
        return False
    t = math.tan(math.radians(cam["fov"]) / 2.0)
    x_, y_ = sum(d[i] * right[i] for i in range(3)) / z, sum(d[i] * up[i] for i in range(3)) / z
    return abs(y_) <= t * margin and abs(x_) <= t * cam["aspect"] * margin


def _position(v):
    m = re.search(r"Position=([-\d.]+),([-\d.]+),([-\d.]+)", str(v or ""))
    return tuple(float(x) for x in m.groups()) if m else None


def closing_checks(reader: dict, pres: dict, mod, patch_name, words: dict) -> list[dict]:
    """The closing's rows: one line per check and reason, the report's setup labels dropped. A setup is named only where
    a check's reasons differ between setups, in plain words: with or without the supplied patch, from the setup's own
    plugin order."""
    reasons_of: dict = {}
    for m in reader.get("missingSummary") or []:
        for subj in m.get("subjects") or []:
            check_, _, setup = str(subj).partition(" — ")
            reasons_of.setdefault(check_, {}).setdefault(m.get("reason"), []).append(setup)
    patches = list(reader.get("suppliedPatches") or [])
    # a missing row names its setup by the arm's reader label (the display label in older plans): both are keys
    arms = {}
    for a in (pres.get("scope") or {}).get("arms") or []:
        for key_ in (a.get("displayLabel"), a.get("readerLabel")):
            if key_:
                arms.setdefault(key_, a)
    pred_arm_ = ((reader.get("prediction") or {}).get("predictionArm")) or "baseline"
    base_arm_ = next((a for a in (pres.get("scope") or {}).get("arms") or [] if a.get("armId") == pred_arm_), {})
    rows = []
    for m in reader.get("missingSummary") or []:
        subs = []
        for subj in m.get("subjects") or []:
            check_, _, setup = str(subj).partition(" — ")
            line = check_
            arm_ = arms.get(setup) or {}
            if arm_ and arm_ is not base_arm_ and words["closingLines"].get("setupWithout"):
                # each gap named by its own setup: what that setup leaves out or adds against the measured one
                have_ = [str(x) for x in arm_.get("plugins") or arm_.get("locationPluginOrder") or []]
                base_ = [str(x) for x in base_arm_.get("plugins") or base_arm_.get("locationPluginOrder") or []]
                gone_ = [mod(x) for x in (arm_.get("pluginsRemoved") if "pluginsRemoved" in arm_ else
                                          [x for x in base_ if x.lower() not in {y.lower() for y in have_}]) or []]
                new_ = [mod(x) for x in (arm_.get("pluginsAdded") if "pluginsAdded" in arm_ else
                                         [x for x in have_ if x.lower() not in {y.lower() for y in base_}]) or []]
                if gone_ or new_:
                    line = fill(words["closingLines"]["setupWithout" if gone_ and not new_ else
                                                 "setupWith" if new_ and not gone_ else "setupBoth"],
                        check=check_, without=joined(gone_) if gone_ else "", with_=joined(new_) if new_ else "")
            elif not arm_ and len(reasons_of[check_]) > 1 and patches:
                # a setup that is no arm of the evaluation holds none of the supplied patches
                line = fill(words["closingLines"]["withoutSetup"], check=check_, patch=patch_name(patches[0]))
            if line not in subs:
                subs.append(line)
        # a check named on several other setups is said once, with how many (the report lists each setup)
        if words["closingLines"].get("setupMany"):
            by_check_ = collections.OrderedDict()
            for s_ in subs:
                by_check_.setdefault(check_title_of(s_), []).append(s_)
            subs = [ls_[0] if len(ls_) == 1 else fill(words["closingLines"]["setupMany"], check=c_, count=len(ls_))
                    for c_, ls_ in by_check_.items()]
        reason, comparison = plain_reason(m.get("reason"), words)
        same = next((r_ for r_ in rows if r_["reason"] == reason), None)
        if same is not None:
            same["subjects"] += [x for x in subs if x not in same["subjects"]]
            same["comparison"] = same["comparison"] or comparison
        else:
            rows.append({"reason": reason, "subjects": subs, "comparison": comparison})
    return rows


def top_level_parts(text: str, sep: str = ";") -> list[str]:
    """`text` split at `sep` outside parentheses."""
    out, depth, cur = [], 0, ""
    for ch in str(text):
        depth += (ch == "(") - (ch == ")")
        if ch == sep and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    return [x.strip() for x in out + [cur] if x.strip()]


def largest_population(text: str):
    """(count, what, more) for the largest counted population a coverage subject lists ("26,532 placed objects (no
    geometry to measure)"): the count, its noun with a short reason kept, and whether others are listed; None when none."""
    best = None
    parts = top_level_parts(text)
    for part in parts:
        m = re.match(r"([\d,]+) (.*)$", part)
        if not m:
            continue
        n = int(m.group(1).replace(",", ""))
        noun, _, reason = m.group(2).partition(" (")
        reason = reason[:-1] if reason.endswith(")") else ""
        what = noun.strip() + (f" ({reason})" if reason and "(" not in reason and ";" not in reason and len(reason) <= 40 else "")
        if best is None or n > best[0]:
            best = (n, what)
    return (f"{best[0]:,}", best[1], len(parts) > 1) if best else None


def patch_pair(patch: str, masters: dict) -> set:
    """The plugins a patch is built for: its masters that are not themselves masters of its other masters (a shared
    dependency is no side of the pair), as evalreport's what_to_do reads it. Lower case. (Cut Content r3 reels: a patch
    whose masters reach the Unofficial Patch was taken as the patch of that mod's clashes.)"""
    pm = {str(m).lower() for m in masters.get(str(patch).lower(), set())}
    return {m for m in pm if not any(m in {str(x).lower() for x in masters.get(o, set())} for o in pm if o != m)}


def check_title_of(line: str) -> str:
    """A closing line's check title, without the setup it names."""
    return re.split(r", (?:with|without|on the setup|on \d+ other) ", str(line), maxsplit=1)[0]


def gap_title(line: str) -> str:
    """The check or area a moved gap line is about, as the report names it: without the setup or the counts said with it."""
    return check_title_of(str(line)).split(": ", 1)[0].strip()


def strip_caveats(text: str, words: dict) -> tuple[str, list[dict]]:
    """A sentence without the caveat clauses the skill's `caveats` list names: the reel says none (Piranha, 2026-11-10);
    each removed clause is returned with the text the report must carry for it."""
    removed = []
    for c_ in [x_ for x_ in words.get("caveats") or [] if x_.get("pattern")]:
        for m_ in list(re.finditer(c_["pattern"], text)):
            removed.append({"caveat": c_.get("id"), "text": m_.group(0).strip(" ;,()"), "report": [c_["report"]] if c_.get("report") else []})
        text = re.sub(c_["pattern"], "", text)
    return re.sub(r"\s{2,}", " ", text).strip(), removed


def plain_reason(reason, words: dict) -> tuple[str, bool]:
    """A closing group's reason in plain words, each clause once: a clause the skill's `closingShort` patterns match is
    said by its short text (a comparison's row counts are in the report), and whether it is a comparison's."""
    out, comparison = [], False
    for ph in str(reason or "").split("; and "):
        for x in words.get("closingShort") or []:
            if re.search(x["pattern"], ph):
                ph, comparison = x["text"], comparison or bool(x.get("comparison"))
                break
        if ph not in out:
            out.append(ph)
    return "; and ".join(out), comparison


def without_arm_of(pred: dict) -> str | None:
    """The prediction's setup without the evaluated mod, as the plan names it: a sibling evaluation's setup keeps its
    prefix ("sibling2:dependency-only"), which its frames carry too, so it is never matched to this bundle's own."""
    return str(pred.get("withoutArm") or "") or None


def hit_paths(hit: dict) -> list:
    """The archive paths a hit was found by: the curated `contentPaths`, else the tool's own `matches[].contentPath`."""
    paths = list(hit.get("contentPaths") or [])
    paths += [m.get("contentPath") for m in hit.get("matches") or [] if isinstance(m, dict) and m.get("contentPath")]
    return list(dict.fromkeys(str(p) for p in paths if str(p or "").strip()))


def said_file(hit: dict, words: dict) -> str:
    """A listed file as said: its name, and where the search found it by a path inside the file's archive (a hub), the
    plugins those paths name."""
    hit = flat_hit(hit)
    names = list(dict.fromkeys(re.split(r"[\\/]", p)[-1] for p in hit_paths(hit)))
    lw = words.get("listedHolds") or {}
    if not names or not lw.get("one"):
        return hit["file"]
    shown = joined(names) if len(names) <= 2 else fill(lw["more"], names=", ".join(names[:2]), count=len(names) - 2)
    return fill(lw["one"], file=hit["file"], plugins=shown)


def unread_sources(entry: dict):
    """The sources a pair's search did not read, as recorded; None where the entry does not record them, which is
    not known, never "everything was read"."""
    got = entry.get("unreadSources")
    return list(got) if isinstance(got, list) else None


def flat_hit(hit: dict) -> dict:
    """A patch-search hit with its file's name at `file` and the file's own fields (status, category, version, fileId)
    beside it: housecarl_nexus_file_search records the file as an object, a hand-curated search as its name. A hit
    whose file has no name is refused, never said raw."""
    f = hit.get("file")
    if isinstance(f, dict):
        if not str(f.get("name") or "").strip():
            raise Refused(f"a patch-search hit on page {hit.get('modId') or hit.get('pageId')} names no file; the search "
                          f"file is not in a shape the reel can say")
        return {**{k: v for k, v in f.items() if k != "name"}, **{k: v for k, v in hit.items() if k != "file"},
                "file": str(f["name"]).strip()}
    if not str(f or "").strip():
        raise Refused("a patch-search hit names no file; the search file is not in a shape the reel can say")
    return hit


def lead_qualifiers(hit: dict, words: dict, loaded: set, evaluated: bool) -> str:
    """What a reader must know about a patch file the Nexus search lists, from its own recorded fields: retired,
    archived or an old version; other mods it names that this setup does not load; that it may be the evaluated patch
    (Nexus does not name a file's plugins). The report and the reel say the same clauses."""
    hit = flat_hit(hit)
    lw = words.get("leadCautions") or {}
    out = []
    for key in (str(hit.get("status") or ""), str(hit.get("category") or "")):
        if key and lw.get(key) and lw[key] not in out:
            out.append(lw[key])
    other = hit.get("otherMentions")
    other = [other] if isinstance(other, str) else list(other or [])
    other = [m for m in other if str(m).lower() not in loaded]
    if other and lw.get("other"):
        out.append(fill(lw["other"], mods=joined([str(m) for m in other])))
    if evaluated and lw.get("maybeEvaluated"):
        out.append(lw["maybeEvaluated"])
    return "".join(" " + x for x in out)


def usable_first(hits: list, loaded: set) -> list:
    """Patch files in the order a reader can use them: a file naming no mod this setup lacks before one that does, a live
    file before a retired or archived one; otherwise the search's own order."""
    def rank(h):
        other = h.get("otherMentions")
        other = [other] if isinstance(other, str) else list(other or [])
        lacks = any(str(m).lower() not in loaded for m in other)
        stale = str(h.get("status") or "").lower() not in ("", "live") or str(h.get("category") or "") in ("ARCHIVED", "OLD_VERSION")
        return (lacks, stale)
    return sorted([flat_hit(h) for h in hits], key=rank)


def page_words(page: dict) -> int:
    """The words a still page puts on screen (its title, rows and lines)."""
    bits = [page.get("title"), page.get("text"), page.get("incomplete")]
    for r in page.get("rows") or []:
        if isinstance(r, dict):
            bits += [r.get("label"), r.get("reason"), *(r.get("subjects") or []), *(r.get("pairs") or [])]
        else:
            bits.append(r)
    return sum(len(str(b).split()) for b in bits if b)


def page_seconds(page: dict, rate: float, policy: dict) -> float:
    """How long a still page holds so it can be read: its words at the skill's reading rate, at least the minimum."""
    return round(max(float((policy.get("holdSeconds") or {}).get("pageMin") or 4.0), page_words(page) / rate), 2)


def _sha(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()


def plan_reel(pres: dict, names: dict | None, masters: dict | None, policy: dict, words: dict,
              patch_search: dict | None, mod_names: dict | None = None, capture_results: list | None = None,
              markers: set | None = None, switches: dict | None = None, captures_final: bool = False) -> dict:
    """`captures_final`: the capture stage is closed (the report's own plan, after the last capture round): a clash is
    pictured only from a captured request about it; one never captured is unavailable and the planner moves on, and a
    kind with nothing captured is said in one sentence. Without it (a plan inside the capture rounds) an uncaptured
    clash is what the next capture request asks for. `markers`: the references evalreport's marker rule calls editor-only (skill data markerModels / markerFlags,
    read from the records); a clash on one is no clash a player meets, so it is left out with a count. `switches`: the
    prediction setup's switch census rows by switch FormKey (edid, baselineEnabled, trigger), for a world-state clash's
    condition."""
    try:
        return _plan(pres, names or {}, masters or {}, policy, words, patch_search, mod_names or {}, capture_results or [],
                     set(markers or ()), switches or {}, captures_final)
    except Refused as e:
        return {"schemaVersion": SCHEMA, "status": "refused", "reason": str(e), "chapters": []}


STATE_KIND = re.compile(r"^STATE-[A-Z]+@([0-9A-Fa-f]{6}:[^@]+)@")
TRIGGER = re.compile(r"^quest (\S+) stage (\d+) .*\.(Enable|Disable)\(\)")


def condition(row: dict, names: dict, switches: dict, place: str, words: dict) -> str:
    """A world-state clash's condition: the quest and stage by in-game name when the census names a trigger that flips the
    switch INTO the clash state (Enable on a switch disabled at load, Disable on one enabled at load) and the quest has an
    in-game name; otherwise the fallback sentence. An editor id is never spoken."""
    m = STATE_KIND.match(str(row.get("kind") or ""))
    sw = switches.get(m.group(1)) if m else None
    for by in ((sw or {}).get("trigger") or {}).get("by") or []:
        t = TRIGGER.match(str(by))
        if not t:
            continue
        into = (t.group(3) == "Enable") != bool(sw.get("baselineEnabled"))
        quest = ((names.get("quests") or {}).get(t.group(1)) or {}).get("name")
        if into and quest:
            return fill(words["conditionQuest"], quest=quest, stage=t.group(2))
    return fill(words["conditionFallback"], place=place)


def _plan(pres, names, masters, policy, words, patch_search, mod_names, capture_results, markers, switches,
          captures_final=False):
    reader = pres.get("reader") or {}
    pred = reader.get("prediction") or {}
    target = pred.get("subject")
    if not target:
        raise Refused("the presentation names no evaluated mod (reader.prediction.subject), so the reel has no subject")
    refs, bases = names.get("refs") or {}, names.get("bases") or {}
    objects, places = names.get("objects") or {}, names.get("places") or {}
    names_mods = names.get("mods") or {}

    def mod(plugin: str) -> str:
        got = (mod_names.get(plugin) or {}).get("name")
        if not got:
            got = next((v.get("name") for k, v in mod_names.items() if k.lower() == str(plugin).lower()), None)
        if not got:
            raise Refused(f"the reel would speak about {plugin}, but no mod name is known for it: no --mod-name was "
                          f"given, and the evaluation records no MO2 mod folder for it, so no Nexus page or folder name")
        return got

    def patch_name(plugin: str) -> str:
        nm = names_mods.get(plugin) or {}
        if nm.get("source") == "declared":
            return nm["name"]
        stem = re.sub(r"\.(esp|esm|esl)$", "", plugin, flags=re.I)
        folder = mod(plugin)
        got = (mod_names.get(plugin) or {}) or next((v for k, v in mod_names.items() if k.lower() == plugin.lower()), {})
        if got.get("source") == "mo2-folder":
            return stem                    # no Nexus name: the folder's own name (a lab label) is not said (item 36 F3)
        return stem if folder.lower() == stem.lower() else fill(words["patchName"], plugin=stem, mod=folder)

    def ref_of(fk):
        return refs.get(str(fk)) or {}

    def base_of(fk):
        return bases.get(ref_of(fk).get("base")) or {}

    def spoken_ref(fk, summary=None):
        eid = base_of(fk).get("editorId")
        if eid:
            return (objects.get(eid) or {}).get("name") or humanize(eid)
        m = re.match(r"^\w+ (\S+?):", summary or "")
        return humanize(m.group(1)) if m else "record"

    # (Missives r3 reel review, minors 6) the cell a capture rendered, by its editor id (the renderer names its frame
    # after it), where the clash row names no cell
    cap_cell = {}
    for x in capture_results:
        for st in x.get("states") or []:
            m_ = re.search(r"([A-Za-z][A-Za-z0-9_]*)-fp\.png$", str(st.get("png") or "").replace("\\", "/"))
            if m_ and x.get("formKey"):
                cap_cell.setdefault(str(x["formKey"]), m_.group(1))

    place_labels = pred.get("placeLabels") or {}

    def place_of(summary, fks=()):
        m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", summary or "") or re.search(r"^Cell ([A-Za-z0-9_]+):", summary or "")
        cell = m.group(1) if m else None
        if cell and (places.get(cell) or {}).get("name"):
            return places[cell]["name"]
        # a cell known only by form key is said as the report says it (its worldspace and grid), never as a file name
        lab = next((place_labels[str(f)] for f in fks if str(f) in place_labels), None)
        if lab and re.match(r"^[0-9A-Fa-f]{6}:\S+", str(lab)):
            rest = re.sub(r"^[0-9A-Fa-f]{6}:\S+(?: \((?:int|ext)\))?(?:,| in)?\s*", "", str(lab)).strip()
            return rest or words.get("placeUnnamed") or "an unnamed place"
        if lab:
            # the report already says the place as a name (N6: a name, a worldspace and grid, an editor ID in words)
            return str(lab)
        cell = cell or next((cap_cell[str(f)] for f in fks if str(f) in cap_cell), None)
        if cell and re.search(r"(?:^|_)es[mpl]$", cell, re.I):
            return words.get("placeUnnamed") or "an unnamed place"
        return (places.get(cell) or {}).get("name") or humanize(cell) or "this place"

    with_arm = pred.get("predictionArm")
    # the setup without the mod as the plan names it, a sibling's prefixed, so frames are matched to the right
    # evaluation's setup; `without_arm` (the bare id) is what the capture requests carry
    without_full = without_arm_of(pred)
    without_arm = str(pred.get("withoutArm") or "").split(":", 1)[-1] or None
    order = next((a.get("locationPluginOrder") for a in (pres.get("scope") or {}).get("arms") or []
                  if a.get("armId") == with_arm), None) or []
    full_order = next((a.get("plugins") for a in (pres.get("scope") or {}).get("arms") or []
                       if a.get("armId") == with_arm), None) or []

    rows = [r for r in pred.get("record") or [] if r.get("admission") == "admitted"]
    no_effect = set(policy.get("noInGameEffectFields") or [])
    marker_rows = [r for r in rows if r.get("field") not in no_effect
                   and ({str(r["formKey"]), *map(str, r.get("other") or [])} & markers)]
    kept_rows = [r for r in rows if r.get("field") not in no_effect and r not in marker_rows]
    left_out = len([r for r in rows if r.get("field") in no_effect])
    left_markers = len({r["formKey"] for r in marker_rows})
    types = policy.get("types") or {}
    clashes = {}
    for r in kept_rows:
        t = types.get(r.get("checkId")) or policy.get("otherType")
        if t is None:
            continue
        key = (t, r["formKey"]) if t in ("record", "cell") else (t, r["formKey"], tuple(r.get("other") or []))
        clashes.setdefault(key, {"type": t, "rows": [],
                                 "pair": tuple(sorted({r["A"], r["B"]} - {target})) + (target,)})["rows"].append(r)
    drawable = set(policy.get("drawableFields") or [])
    disabled_z = float(policy.get("disabledZ") if policy.get("disabledZ") is not None else -30000.0)

    def removed(r):
        """A placement row whose winning position is the disable idiom's (z at disabledZ): the winner takes the object
        out of the world rather than moving it (Missives reel review V1)."""
        if r.get("field") != "Placement":
            return False
        won = _position((r.get("values") or {}).get("winner"))
        return bool(won) and won[2] <= disabled_z + 1.0

    # a record clash is classified by every lost edit on its record: a removal first, then a field a picture can show
    for c in clashes.values():
        if c["type"] == "record":
            c["rows"].sort(key=lambda r: (0 if removed(r) else 1 if r.get("field") in drawable else 2))
    effect_re = re.compile(policy["effectModelPattern"], re.I) if policy.get("effectModelPattern") else None

    def effect(fk):
        return bool(effect_re and effect_re.search(str(base_of(fk).get("model") or "")))

    # a navmesh one plugin re-cut over another's (the navmesh check's CLOBBERED history rows): (formKey, overwriter) ->
    # the earlier plugin. Item 24's camera draws a broken link only from such a navmesh, beside its earlier version
    recut = {}
    for r_ in pred.get("record") or []:
        if r_.get("checkId") == "geometry.navmesh-ownership" and r_.get("kind") == "CLOBBERED" and r_.get("formKey"):
            recut.setdefault((str(r_["formKey"]).casefold(), str(r_.get("B") or "").casefold()), r_.get("A"))

    def earlier_navmesh(r):
        """The plugin whose version of the row's linking navmesh the row's winner (A) re-cut, or None."""
        return recut.get((str(r.get("formKey")).casefold(), str(r.get("A") or "").casefold()))

    def navmesh_camera(r):
        """Whether item 24's camera can draw this broken link: an edge-index link, outdoors, from a navmesh its winner
        re-cut over another plugin's version."""
        return (r.get("kind") == "EDGE-IDX" and " (ext)" in str(r.get("summary") or "")
                and earlier_navmesh(r) is not None)

    def unpictured_reason(c):
        """None for a clash a picture can show; else the skill's reason it cannot (stillReasons keys)."""
        if c["type"] not in (policy.get("pictureTypes") or []):
            return c["type"]
        r = c["rows"][0]
        if c["type"] == "navmesh":
            return None if navmesh_camera(r) else "navmesh"
        if c["type"] == "record":
            if removed(r):
                return "removed"
            if r.get("field") not in (policy.get("drawableFields") or []):
                return "record"
            b = base_of(r["formKey"])
            if b.get("type") not in (policy.get("actorTypes") or []) and not b.get("model"):
                return "notDrawn"
            if effect(r["formKey"]):
                return "effect"
            # the left half shows the other mod's spot only when its version wins once the mod being added is gone
            carriers = [p for p in ref_of(r["formKey"]).get("carriedBy") or [] if p in order and p != target]
            if not carriers or carriers[-1] != r["A"]:
                return "leftNotLoser"
            return None
        if any(effect(fk) for fk in [r["formKey"], *(r.get("other") or [])]):
            return "effect"
        return None

    # an object no camera can mark fails every request it is in: a clash with it is not tried again under another request
    # (WRW capture: one such object refused two requests)
    unmarkable = unmarkable_objects(capture_results)
    attempts = int(policy.get("captureAttemptsPerType") or policy.get("perType") or 1)
    chosen_by_type, unpictured, represented, candidates, others = {}, [], {}, {}, {}
    none_captured = []      # (type, count, why) of kinds with clashes to picture and no captured frame, capture closed

    def capture_why(cs_):
        """Why clashes a picture could show have no frame once capture is closed (R1): captureNoRule when none of them is
        of a check capture can request (skill data vignettes.captureChecks, kept equal to the capture rules), else
        captureLimit (the rounds, the per-round cap or the tries per kind ran out first)."""
        if "captureChecks" not in policy:
            raise Refused("the skill's scene-policy vignettes.captureChecks is missing: the reel cannot say why a clash "
                          "was not captured")
        can_ = set(policy["captureChecks"])
        return "captureLimit" if any(str(c_["rows"][0].get("checkId")) in can_ for c_ in cs_) else "captureNoRule"

    strength_re = re.compile(policy["strengthPattern"]) if policy.get("strengthPattern") else None

    def strength(c):
        """How much of the pair crosses, from the row's own summary (skill data strengthPattern: its triangle pairs), or
        None. It orders which example is pictured; it ranks no finding and changes no count."""
        m = strength_re.search(str(c["rows"][0].get("summary") or "")) if strength_re else None
        return int(m.group(1).replace(",", "")) if m else None

    def group_key(c):
        """Instances of one problem (skill data vignettes.groupKey): the type, the mod pair, the cell, and a record
        clash's field. A record clash's cell is the one the other mod's version is filed in (names enrichment); a
        geometry clash's is the cell its row names."""
        r = c["rows"][0]
        if c["type"] == "record":
            cell = (ref_of(r["formKey"]).get("cells") or {}).get(r["A"])
            return (c["type"], c["pair"], cell or r["formKey"], r.get("field"))
        m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", r.get("summary") or "")
        return (c["type"], c["pair"], m.group(1) if m else r["formKey"], None)
    for t in policy.get("typeOrder") or []:
        cs = [c for c in clashes.values() if c["type"] == t]
        if not cs:
            continue
        draw = [c for c in cs if unpictured_reason(c) is None]
        unpictured += [(c, unpictured_reason(c)) for c in cs if unpictured_reason(c) is not None]
        groups: dict = {}
        for c in sorted(draw, key=lambda c: c["rows"][0]["formKey"]):
            groups.setdefault(group_key(c), []).append(c)
        # the strongest crossing of a group is its example, and a pair's groups go strongest first (item 7); a clash with
        # no stated strength keeps its place after those with one, ties keep the order above
        for g in groups.values():
            g.sort(key=lambda c: (strength(c) is None, -(strength(c) or 0)))
        pairs: dict = {}
        for g in groups.values():
            pairs.setdefault(g[0]["pair"], []).append(g)
        for gs in pairs.values():
            gs.sort(key=lambda g: (strength(g[0]) is None, -(strength(g[0]) or 0)))
        pair_order = sorted(pairs, key=lambda p: (-len(pairs[p]), p))
        ordering, i = [], 0                                 # round the pairs until every group has a place
        while any(pairs.values()):
            p = pair_order[i % len(pair_order)]
            if pairs[p]:
                ordering.append(pairs[p].pop(0))
            i += 1
        quota = len(ordering) if len(ordering) <= policy["allUpTo"] else policy["perType"]
        chosen, tried_refused, tried, shown_groups = [], [], 0, []
        for g in ordering:
            if len(chosen) >= quota or tried >= attempts:
                break
            for c in g:                                     # a group's next member is its capture fallback
                if tried >= attempts:
                    break
                fks_ = [c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])]
                if (captures_final and captured_request(fks_, capture_results) is None
                        and not clash_refused(fks_, capture_results)):
                    continue                                # never captured: unavailable, not a try
                # once capture is closed nothing is tried again: a captured clash is shown, a refused one said
                tried += 0 if captures_final else 1
                if clash_refused([c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])], capture_results) or ({str(c["rows"][0]["formKey"]),
                                                           *map(str, c["rows"][0].get("other") or [])} & unmarkable):
                    tried_refused.append(c)
                    continue
                chosen.append(c)
                shown_groups.append(g)
                others[c["rows"][0]["formKey"]] = [x for x in g if x is not c]
                break

        def buried_(c):
            return bool(clash_buried([c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])], capture_results))
        # a refused member whose group another member pictures is represented by that picture (and named with it), never
        # also counted as one the reel cannot picture; a buried clash is said as buried (F8), counted like every refused
        # one, never dropped
        stands_in = [c for c in tried_refused if not buried_(c) and any(c in g_ for g_ in shown_groups)]
        unpictured += [(c, "buried" if buried_(c) else unpictured_refused_reason(
            [c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])], capture_results, unmarkable,
            policy.get("refusalReasons")))
            for c in tried_refused if c not in stands_in]
        # a clash a capture found buried is said as buried even when its group was pictured before it was tried (the
        # strongest member first, item 7): no picture of another member stands for what no player sees
        untried_buried = [c for c in draw if c not in chosen and c not in tried_refused and buried_(c)]
        unpictured += [(c, "buried") for c in untried_buried]
        if captures_final and not chosen and t in (policy.get("pictureTypes") or []) and len(draw) > len(tried_refused):
            none_captured.append((t, len(draw) - len(tried_refused),
                                  capture_why([c for c in draw if c not in tried_refused])))
        chosen_by_type[t] = chosen
        if t in (policy.get("pictureTypes") or []):
            candidates[t] = [c["rows"][0]["formKey"] for g in ordering for c in g][:attempts]
        represented[t] = [c for c in draw if c not in chosen and c not in untried_buried
                          and (c not in tried_refused or c in stands_in)]
    budget = int(policy["maxVignettes"])
    for t in policy.get("typeOrder") or []:
        keep = chosen_by_type.get(t, [])[:max(budget, 0)]
        represented.setdefault(t, [])
        represented[t] += [c for c in chosen_by_type.get(t, []) if c not in keep]
        chosen_by_type[t] = keep
        budget -= len(keep)
    selected = [c for t in policy.get("typeOrder") or [] for c in chosen_by_type.get(t, [])]

    # (Missives r3 reel review, finding 4) a removal is pictured where its object stands in a captured split's frame: the
    # left half (the other mod's spot, with the mod being added gone) shows the object, the right half shows it gone.
    # Whether it is in frame is decided by projecting its spot into that split's capture camera (read from the capture's
    # own argv); a removal no split frames stays in the one statement of clashes the reel cannot picture.
    split_cams = {}
    for x in capture_results:
        if x.get("status") == "captured" and x.get("formKey"):
            for st in x.get("states") or []:
                if st.get("state") == "left" and camera_of(st.get("argv")):
                    split_cams[str(x["formKey"])] = camera_of(st.get("argv"))
    in_split = {}
    for c, why in list(unpictured):
        if why != "removed":
            continue
        spot = _position((c["rows"][0].get("values") or {}).get("lost"))
        for sp in chosen_by_type.get("record") or []:
            cam = split_cams.get(str(sp["rows"][0]["formKey"]))
            if spot and cam and in_view(cam, spot):
                in_split.setdefault(sp["rows"][0]["formKey"], []).append(c)
                unpictured.remove((c, why))
                break

    # the patch search covers every pair the reel speaks about (skill data: patchSearch.requiredTools)
    searched = {}
    for entry in (patch_search or {}).get("pairs") or []:
        searched[frozenset(str(x).lower() for x in entry.get("pair") or [])] = entry
    if selected and patch_search is None:
        raise Refused("the reel's patch sentences need the recorded Nexus patch search: pass evalreport plan "
                      "--patch-search FILE (the skill's scene-policy.json vignettes.patchSearch says what it covers)")
    gaps = list(dict.fromkeys(g for c in selected if (g := pair_search_gap(patch_search, c["pair"], policy))))
    if gaps:
        raise Refused(pair_search_refusal(gaps, policy))

    supplied = list(reader.get("suppliedPatches") or [])
    # the search's known answer is derived here from its recorded hits, never taken from a flag the file states:
    # found means a recorded hit names the expected patch (its plugin name without the extension)
    known = (patch_search or {}).get("knownAnswer") or {}
    want = re.sub(r"\.(esp|esm|esl)$", "", str(known.get("patch") or ""), flags=re.I).lower()
    known_found = bool(want) and any(want in json.dumps(h, ensure_ascii=False).lower() for h in known.get("hits") or [])

    def own_patches(c):
        other = [m for m in c["pair"] if m != target][0]
        return [p for p in supplied if {other.lower(), target.lower()} <= patch_pair(p, masters)]

    def pick(case):
        """A patch case's one phrasing (skill data reel.words.patch): one fact is said one way in every vignette (R2);
        a case given several phrasings is refused, never rotated."""
        pool = words["patch"][case]
        if isinstance(pool, list):
            if len(pool) != 1:
                raise Refused(f"the skill's reel.words.patch.{case} has {len(pool)} phrasings; one fact is said one way, "
                              f"so give it one")
            pool = pool[0]
        return pool

    def needs(patch, c):
        """(Missives r3 reel review, finding 6) the plugins the patch needs beyond the clash's two mods and the base game."""
        base_ = {x.lower() for x in policy.get("baseGameMasters") or []}
        pair_ = {x.lower() for x in c["pair"]}
        extra_ = [m for m in sorted(masters.get(patch.lower(), set())) if m not in base_ and m not in pair_]
        if not extra_ or not words.get("patchNeeds"):
            return ""
        nm_ = []
        for m in extra_:
            try:
                nm_.append(mod(next((k for k in mod_names if k.lower() == m), m)))
            except Refused:
                nm_.append(m)
        return " " + fill(words["patchNeeds"], patch=patch_name(patch), mods=joined(nm_))

    def patch_sentence(c):
        own = own_patches(c)
        r = c["rows"][0]
        wp = r.get("withPatch")
        entry = searched.get(frozenset(m.lower() for m in c["pair"])) or {}
        hits = entry.get("hits") or []
        evidence = {"pair": list(c["pair"]), "withPatch": wp,
                    "searchQueries": [q.get("query") or q.get("args") for q in entry.get("queries") or []]}
        if own and wp in ("resolved", "kept", "switched", "own"):
            kept = mod(r["B"] if wp == "kept" else r["A"])
            key = {"resolved": "fixes", "kept": "keeps", "switched": "keeps", "own": "own"}[wp]
            if key == "fixes" and c["type"] in ("overlap", "contact") and words["patch"].get("fixesOverlap"):
                # say what the patch did, from its own placement of an object in the pair (names enrichment), never
                # that it "fixes" a clash nobody saw (Missives reel review V2)
                moved = None
                for fk in [r["formKey"], *(r.get("other") or [])]:
                    pl = ref_of(fk).get("placements") or {}
                    mine = next((v for k, v in pl.items() if k.lower() == own[0].lower()), None)
                    before = next((v for k, v in pl.items() if k.lower() == str(target).lower()), None) or next(
                        (v for k, v in pl.items() if k.lower() in {m.lower() for m in c["pair"]}), None)
                    if mine and before:
                        d = sum((float(a) - float(b)) ** 2 for a, b in zip(mine[:3], before[:3])) ** 0.5
                        if d * float(policy.get("metresPerUnit") or 0.01428) >= 0.5:
                            moved = (fk, d * float(policy.get("metresPerUnit") or 0.01428))
                            break
                if moved and words["patch"].get("fixesOverlapMoved"):
                    key = "fixesOverlapMoved"
                    text_ = fill(pick(key), patch=patch_name(own[0]), object=spoken_ref(moved[0]),
                                             m=max(1, round(moved[1])))
                else:
                    key = "fixesOverlap"
                    text_ = fill(pick(key), patch=patch_name(own[0]))
                return (text_ + needs(own[0], c), {**evidence, "case": "own-" + key, "patch": own[0],
                                "masters": sorted(masters.get(own[0].lower(), set())),
                                "moved": [moved[0], round(moved[1], 1)] if moved else None})
            return (fill(pick(key), patch=patch_name(own[0]), kept=kept) + needs(own[0], c),
                    {**evidence, "case": "own-" + key, "patch": own[0], "masters": sorted(masters.get(own[0].lower(), set()))})
        lead = fill(pick("leaves"), patch=patch_name(own[0])) + " " if own else ""
        ev = {**evidence, "patch": own[0] if own else None,
              "masters": sorted(masters.get(own[0].lower(), set())) if own else None}
        if hits:
            loaded_ = {str(x).lower() for x in full_order or order}
            hit_ = usable_first(hits, loaded_)[0]
            q_ = lead_qualifiers(hit_, words, loaded_, bool(own))
            return (lead + fill(pick("listed"), file=said_file(hit_, words)) + q_,
                    {**ev, "case": "listed", "hit": hit_, "qualifiers": q_.strip() or None})
        # "No patch exists" only on a search that found its own known answer and read every source of this pair
        unread = unread_sources(entry)
        if known_found and unread == []:
            return lead + pick("noneOther" if own else "none"), {**ev, "case": "none-searched"}
        return (lead + fill(pick("notFoundOther" if own else "notFound"), patch=patch_name(own[0]) if own else ""),
                {**ev, "case": "not-found", "knownAnswerFound": known_found, "unreadSources": unread})

    def owner(fk, r):
        pair = [r["A"], r["B"]]
        made = str(fk).split(":", 1)[-1].lower()
        hit = [m for m in pair if m.lower() == made]
        if hit:
            return hit[0]
        carried = [m for m in ref_of(fk).get("carriedBy") or [] if m in pair]
        return carried[-1] if carried else pair[0]

    def geometry_slots(c):
        r = c["rows"][0]
        o_fk = (r.get("other") or [None])[0]
        a_owner, o_owner = owner(r["formKey"], r), owner(o_fk, r)
        first = (r["formKey"], a_owner) if a_owner == target else (o_fk, o_owner)
        second = (o_fk, o_owner) if first[0] == r["formKey"] else (r["formKey"], a_owner)
        return {"place": place_of(r.get("summary"), [r["formKey"], *(r.get("other") or [])]), "modA": mod(first[1]), "objectA": spoken_ref(first[0]),
                "modB": mod(second[1]), "objectB": spoken_ref(second[0]),
                "thingA": thing(first[0], first[1]), "thingB": thing(second[0], second[1])}

    def spoken(text):
        """A report sentence as the reel says it: each plugin by its mod's name, a place pointer to the report."""
        for p_ in sorted({str(k) for k in mod_names} | {str(target)}, key=len, reverse=True):
            if p_ and p_ in text:
                try:
                    text = text.replace(p_, mod(p_))
                except Refused:
                    pass
        for a_, b_ in (words.get("reportToReel") or {}).items():
            text = text.replace(a_, b_)
        return text

    def clash_id(c):
        r_ = c["rows"][0]
        return "|".join([str(r_["formKey"]), *map(str, r_.get("other") or [])])

    def frame_label(c, state, m):
        """The label strip under a vignette frame: which setup it shows, and on the marked view which shape is which
        colour (from the renderer's own --ref FORMKEY=colour marks)."""
        lw = words.get("vignetteLabels") or {}
        if not lw.get(state):
            return None
        if c.get("patchNew") and state in ("without", "with") and lw.get(state + "Patches"):
            return fill(lw[state + "Patches"], patches=c["patchNew"]["said"])
        if c["type"] == "navmesh":
            # the earlier version's plugin is the frame's own --nav-version, never the plan's history (C2241)
            drawn = navmesh_earlier_plugin([m]) if state == "historical-navmesh-context" else None
            if state == "historical-navmesh-context" and drawn is None:
                return lw.get(state + "Unknown") or None
            return fill(lw[state], mod=mod(target), earlier=mod(drawn) if drawn else "",
                                    winner=mod(c["rows"][0]["A"]))
        if state != "highlight":
            return fill(lw[state], mod=mod(target))
        argv = [str(x) for x in ((m or {}).get("provenance") or {}).get("argv") or []]
        marks = {}
        for i_, x_ in enumerate(argv[:-1]):
            if x_ == "--ref" and "=" in argv[i_ + 1]:
                fk_, col_ = argv[i_ + 1].rsplit("=", 1)
                marks[col_] = fk_
        parts = [fill(lw["mark"], colour=col_, object=spoken_ref(fk_)) for col_, fk_ in marks.items()]
        # (R13) the pair's other object, left unmarked (too large: highlight's volume rule), is named on the strip too
        marked_ = {str(v_).casefold() for v_ in marks.values()}
        r_ = c["rows"][0]
        parts += [fill(lw["unmarked"], object=spoken_ref(fk_)) for fk_ in [r_["formKey"], *(r_.get("other") or [])]
                  if parts and lw.get("unmarked") and str(fk_).casefold() not in marked_]
        if c.get("patchNew") and lw.get("highlightPatches"):
            return (fill(lw["highlightPatches"], patches=c["patchNew"]["said"], marks="; ".join(parts)) if parts
                    else fill(lw.get("highlightNoMarksPatches") or lw.get("highlightNoMarks") or "",
                              patches=c["patchNew"]["said"]))
        return fill(lw["highlight"], marks="; ".join(parts)) if parts else lw.get("highlightNoMarks")

    def thing(fk, owner_mod):
        """The object as the plugin that made it: "<mod>'s <object>" only when the mod defines it; an object of the base
        game (or of a third plugin) the mod only changes is said as that, never as the mod's own."""
        obj = spoken_ref(fk)
        made = str(fk).split(":", 1)[-1]
        if made.lower() == str(owner_mod).lower() or not words.get("thingChanged"):
            return fill(words.get("thingOwn", "{mod}'s {object}"), mod=mod(owner_mod), object=obj)
        base_ = made.lower() in {x.lower() for x in policy.get("baseGameMasters") or []}
        origin = words.get("baseGameName", "the base game") if base_ else re.sub(r"\.(esp|esm|esl)$", "", made, flags=re.I)
        return fill(words["thingChanged"], origin=origin, object=obj, mod=mod(owner_mod))

    def volume(fk):
        b = base_of(fk).get("bounds")
        if not b or len(b) != 6:
            return None
        s = float(ref_of(fk).get("scale") or 1.0)
        return abs((b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])) * s ** 3

    def highlight(c):
        r = c["rows"][0]
        if c["type"] == "record":
            return [r["formKey"]]
        fks = [r["formKey"], *(r.get("other") or [])]
        vols = [volume(fk) for fk in fks]
        if len(fks) == 2 and all(vols) and max(vols) >= float(policy.get("volumeFactor") or 0) * min(vols) > 0:
            return [fks[vols.index(min(vols))]]
        return fks

    def orient(c):
        """A placement split's narration, from data only (Piranha's note 3): who moved the object (each version against
        the base game's, from the names enrichment's per-carrier placements), whether the structure moved with it (the
        mover's own overrides in that cell shifting by the object's height), where the other spot is (the row's two
        positions, in metres) and what is there (the right half's downward probe, from its capture provenance). A clause
        whose data is missing is not said."""
        r = c["rows"][0]
        fk = r["formKey"]
        obj = spoken_ref(fk, r.get("summary"))
        A, B = r["A"], r["B"]
        pl = ref_of(fk).get("placements") or {}
        definer = fk.split(":", 1)[1]
        # a reference one of the two mods adds has no base-game spot to keep, so no mover is named
        basev = None if definer.lower() in (A.lower(), B.lower()) else pl.get(definer)
        av, bv = pl.get(A), pl.get(B)
        tol = float(policy.get("storeyTolerance") or 96.0)

        def same(u, v):
            return max(abs(float(u[i]) - float(v[i])) for i in range(3)) <= 1.0
        moved = [m for m, v in ((A, av), (B, bv)) if not same(v, basev)] if (basev and av and bv) else None
        if moved is not None and len(moved) == 1:
            mover = moved[0]
            keeper = B if mover == A else A
            dz_obj = float((av if mover == A else bv)[2]) - float(basev[2])
            ov = (ref_of(fk).get("cellOverrides") or {}).get(mover) or {}
            dzs = ov.get("dz") or []
            share = len([d for d in dzs if abs(float(d) - dz_obj) <= tol]) / len(dzs) if dzs else 0.0
            off = float(ov.get("disabled") or 0) / float(ov["count"]) if ov.get("count") else 0.0
            cell = (ref_of(fk).get("cells") or {}).get(mover)
            place = next((v.get("name") for v in places.values() if v.get("formKey") == cell), None) \
                or fill(words["structurePlace"], object=obj)
            if off >= float(policy.get("structureShare") or 1.0):
                who = fill(words["structureReplaced"], mover=mod(mover), keeper=mod(keeper), place=place, object=obj)
            elif dzs and share >= float(policy.get("structureShare") or 1.0) and abs(dz_obj) > tol:
                who = fill(words["structureUp" if dz_obj > 0 else "structureDown"],
                    mover=mod(mover), keeper=mod(keeper), place=place, object=obj)
            else:
                who = fill(words["moverOne"], mover=mod(mover), keeper=mod(keeper), object=obj)
        elif moved is not None and len(moved) == 2:
            who = fill(words["moverBoth"], a=mod(A), b=mod(B), object=obj)
        else:
            who = fill(words["placementSplit"], loser=mod(A), winner=mod(B), object=obj, mod=mod(target))
        text = who
        lost, won = _position((r.get("values") or {}).get("lost")), _position((r.get("values") or {}).get("winner"))
        if lost and won:
            dx, dy, dz = (won[i] - lost[i] for i in range(3))
            dist = (dx * dx + dy * dy + dz * dz) ** 0.5
            mpu = float(policy.get("metresPerUnit") or 0.01428)
            if dist * mpu < 5:
                where = words["where"]["close"]
            elif abs(dz) >= 0.5 * dist:
                where = fill(words["where"]["below" if dz < 0 else "above"], m=int(round(abs(dz) * mpu / 5.0) * 5))
            else:
                where = fill(words["where"]["away"], m=int(round(dist * mpu / 5.0) * 5))
            sup = ((frame_media(c, "right") or {}).get("provenance") or {}).get("support")
            extra = ""
            if sup:
                if sup.get("terrainAbove") and not sup.get("interior"):
                    extra = words["spotExtra"]["belowGround"]
                elif sup.get("down") is None:
                    extra = words["spotExtra"]["noFloor" if sup.get("interior") else "inAir"]
            text += " " + fill(words["rightSpot"], mod=mod(target), where=where) + extra + "."
        # (Missives r3 reel review, finding 5) the others are named by what they lose: a move is "moved the same way";
        # a model, a size or a turn alone is said as such, never folded into "placed the same way"
        group = [c, *(others.get(fk) or []), *(in_split.get(fk) or [])]

        def moved_row(x):
            pr_ = next((r_ for r_ in x["rows"] if r_.get("field") == "Placement"), None)
            lo_, wo_ = (_position((pr_.get("values") or {}).get(k_)) for k_ in ("lost", "winner")) if pr_ else (None, None)
            return bool(lo_ and wo_ and max(abs(lo_[i] - wo_[i]) for i in range(3)) > 1.0)
        rest = say_names([spoken_ref(x["rows"][0]["formKey"], x["rows"][0].get("summary"))
                          for x in others.get(fk) or [] if moved_row(x)])
        lost_items = collections.OrderedDict()
        for x in group:
            nm_ = spoken_ref(x["rows"][0]["formKey"], x["rows"][0].get("summary"))
            for r_ in x["rows"]:
                f_ = r_.get("field")
                if f_ == "Placement":
                    if not moved_row(x):
                        lost_items.setdefault("turn", []).append(nm_)
                elif f_ in (words.get("lostFieldWords") or {}):
                    lost_items.setdefault(f_, []).append(nm_)
        if rest:
            # with more to count, the three names are a list the count ends: "A, B, C and 2 more"
            nm = ", ".join(rest[:3]) if len(rest) > 3 else joined(rest)
            if len(rest) > 3:
                text += " " + fill(words["groupOthers"]["more"], names=nm, count=len(rest) - 3)
            else:
                text += " " + fill(words["groupOthers"]["one" if len(rest) == 1 else "many"], names=nm)
        if lost_items and words.get("alsoLost"):
            lw_ = dict(words.get("lostFieldWords") or {}, turn=words.get("lostTurnWord") or "the turn of {objects}")
            text += " " + fill(words["alsoLost"], loser=mod(A), winner=mod(B), items=joined(
                [fill(lw_[k_], objects=joined(say_names(v_))) for k_, v_ in lost_items.items()]))
        said_names_ = [spoken_ref(x["rows"][0]["formKey"], x["rows"][0].get("summary")) for x in group]
        for g_ in in_split.get(fk) or []:
            nm_g_ = spoken_ref(g_["rows"][0]["formKey"], g_["rows"][0].get("summary"))
            # a name another record of this chapter shares is said as "another" one, so the two are not heard as one
            if said_names_.count(nm_g_) > 1 and words.get("anotherObject"):
                nm_g_ = fill(words["anotherObject"], object=nm_g_)
            text += " " + fill(words["removedInSplit"],
                winner=mod(g_["rows"][0]["B"]), loser=mod(g_["rows"][0]["A"]), subject=obj, object=nm_g_)
        return text

    def sentence(c):
        r = c["rows"][0]
        if c["type"] == "record":
            return orient(c)
        if c["type"] == "navmesh":
            return navmesh_sentence(c)
        s = geometry_slots(c)
        if c["type"] == "state":
            return fill(words["stateClash"], condition=condition(r, names, switches, s["place"], words),
                                              **{k: v for k, v in s.items() if k != "place"})
        return fill(words["overlap"], **s)

    def navmesh_sentence(c):
        """A navmesh vignette's words: which mod re-cut the navmesh over whose, where, and what the links point at; then
        the control's own result (the setup without the mod, measured at the same camera), from the capture result."""
        r = c["rows"][0]
        text = fill(words["navmeshClash"], place=place_of(r.get("summary"), [r["formKey"]]), modA=mod(r["A"]),
                                            modB=mod(earlier_navmesh(r)))
        fks_ = [r["formKey"], *(r.get("other") or [])]
        res_ = next((x for x in capture_results if x.get("status") == "captured" and result_about(x, fks_)), None)
        if ((res_ or {}).get("navmeshControl") or {}).get("status") == "no-broken-crossing" and words.get("navmeshControlClean"):
            text += " " + fill(words["navmeshControlClean"], mod=mod(target))
        return text

    game = {(m.get("subject") or {}).get("formKey") for m in pres.get("media") or []
            if m.get("assessed") and m.get("mediaKind") == "game-video"}
    arm_of = {"without": without_full, "with": with_arm, "highlight": with_arm, "left": without_full, "right": with_arm}
    # both navmesh states are rendered in the setup with the mod; only the source's version changes
    arm_of.update({s_: with_arm for s_ in policy.get("navmeshSequence") or []})
    bare_of = {k_: (str(v_).split(":", 1)[-1] if v_ else v_) for k_, v_ in arm_of.items()}   # as capture requests name it

    def frame_media(c, state):
        """A clash's frame in one state, from ONE capture request: the captured request about the clash, else the
        request of its first matching frame. Two requests can picture one object (WRW: the tower in two pairs), and
        frames from both would mix two poses in one vignette. A clash new with the supplied patches has its own setups
        (c["arms"]): its frames are the with-patch setup's and the setup's without them."""
        fks = {c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])}
        arms_ = c.get("arms") or arm_of

        def matches(m, st):
            prov = m.get("provenance") or {}
            return (m.get("assessed") and prov.get("vignetteState") == st and (m.get("subject") or {}).get("formKey") in fks
                    and (m.get("subject") or {}).get("armId") == arms_.get(st))
        req = captured_request([c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])], capture_results)
        if req is None:
            first = next((m for st in states_of(c) for m in pres.get("media") or [] if matches(m, st)), None)
            req = ((first or {}).get("provenance") or {}).get("captureRequest")
        for m in pres.get("media") or []:
            if matches(m, state) and (req is None or (m.get("provenance") or {}).get("captureRequest") == req):
                return m
        return None

    def states_of(c):
        if c["type"] == "navmesh":
            return list(policy.get("navmeshSequence") or [])
        return ["left", "right"] if c["type"] == "record" else list(policy["sequence"])

    def patch_new_chapters():
        """The vignettes of clashes new with every supplied patch loaded (reader.validation.newByCheck): for each check
        a capture can request (vignettes.captureChecks) whose kind a picture shows, up to patchNewPerCheck examples, the
        strongest first, inside maxVignettes. Frames come from the with-patch setup and the setup without the patches,
        never the prediction's setups. With capture closed, an example never captured has no chapter; it is listed in
        `unpictured` with its reason, and the report's What to do already names it."""
        val_ = reader.get("validation") or {}
        out_ = {"chapters": [], "pictured": [], "unpictured": []}
        per_ = int(policy.get("patchNewPerCheck") or 0)
        with_p, without_p = val_.get("withPatchArm"), val_.get("withoutPatchArm")
        if not per_ or not with_p or not without_p or not val_.get("newByCheck"):
            return out_
        can_ = set(policy.get("captureChecks") or [])
        room_ = int(policy["maxVignettes"]) - len(selected)
        by_id_ = {f_.get("id"): f_ for f_ in pres.get("findings") or []}
        patches_ = [str(p_) for p_ in val_.get("patches") or reader.get("suppliedPatches") or []]
        try:
            said_ = joined([patch_name(p_) for p_ in patches_])
        except Refused:
            said_ = joined([re.sub(r"\.(esp|esm|esl)$", "", p_, flags=re.I) for p_ in patches_])
        one_ = len(patches_) == 1
        slots_ = {"patches": said_, "verb": "is" if one_ else "are", "them": "it" if one_ else "them"}
        arms_ = {"without": without_p, "with": with_p, "highlight": with_p}
        for n_ in val_.get("newByCheck") or []:
            cs_ = []
            for fid_ in n_.get("findingIds") or []:
                f_ = by_id_.get(fid_) or {}
                if f_.get("armId") != with_p:
                    continue
                t_ = types.get(f_.get("checkId"))
                if t_ not in (policy.get("pictureTypes") or []) or f_.get("checkId") not in can_:
                    continue
                cnt_ = f_.get("counts") or {}
                for r_ in f_.get("allRows") or f_.get("rows") or []:
                    if not r_.get("formKey"):
                        continue
                    row_ = {"checkId": f_["checkId"], "kind": cnt_.get("groupKind"), "A": cnt_.get("A"), "B": cnt_.get("B"),
                            "formKey": r_["formKey"], "other": list(r_.get("other") or []), "summary": r_.get("summary"),
                            "findingId": fid_, "physical": r_.get("physical")}
                    c_ = {"type": t_, "rows": [row_], "pair": tuple(x for x in (cnt_.get("A"), cnt_.get("B")) if x),
                          "arms": arms_, "patchNew": {"patches": patches_, "said": said_, "findingIds": list(n_.get("findingIds") or [])}}
                    if unpictured_reason(c_) is None:
                        cs_.append(c_)
            cs_.sort(key=lambda c_: (strength(c_) is None, -(strength(c_) or 0), str(c_["rows"][0]["formKey"])))
            taken_ = 0
            for c_ in cs_:
                if taken_ >= per_ or room_ <= 0:
                    break
                r_ = c_["rows"][0]
                fks_ = [r_["formKey"], *(r_.get("other") or [])]
                if clash_refused(fks_, capture_results) or set(map(str, fks_)) & unmarkable:
                    out_["unpictured"].append({"findingId": r_["findingId"], "formKey": r_["formKey"], "reason": "captureRefused"})
                    continue
                if captures_final and captured_request(fks_, capture_results) is None:
                    out_["unpictured"].append({"findingId": r_["findingId"], "formKey": r_["formKey"], "reason": "notCaptured"})
                    continue
                try:
                    # the clash first, then what makes it this kind of picture: "This clash" points back at it
                    text_ = sentence(c_) + " " + fill(words["patchNewLead"], **slots_)
                    place_ = geometry_slots(c_)["place"] if c_["type"] != "navmesh" else place_of(r_.get("summary"), [r_["formKey"]])
                    frames_ = []
                    for st_ in states_of(c_):
                        m_ = frame_media(c_, st_)
                        lab_ = frame_label(c_, st_, m_)
                        frames_.append({"state": st_, "armId": str(arms_.get(st_) or "").split(":", 1)[-1] or None,
                                        "mediaId": (m_ or {}).get("id"),
                                        "viewNote": view_kind_line((m_ or {}).get("provenance") or {}) or None,
                                        **({"label": lab_} if lab_ else {})})
                except Refused as e_:
                    out_["unpictured"].append({"findingId": r_["findingId"], "formKey": r_["formKey"], "reason": "names",
                                               "detail": str(e_)})
                    continue
                clash_ = {"type": c_["type"], "checkId": r_["checkId"], "formKey": r_["formKey"], "other": list(r_.get("other") or []),
                          "pair": list(c_["pair"]), "armId": str(with_p).split(":", 1)[-1],
                          "withoutArmId": str(without_p).split(":", 1)[-1], "findingId": r_["findingId"],
                          "highlight": highlight(c_), "place": place_,
                          "patchNew": {"patches": patches_, "findingIds": c_["patchNew"]["findingIds"]}}
                if c_["type"] == "state":
                    m_ = STATE_KIND.match(str(r_.get("kind") or ""))
                    clash_.update(kind=r_.get("kind"), state=m_.group(1) if m_ else None)
                text_, _cut = strip_caveats(text_, words)
                out_["chapters"].append({"kind": "vignette", "narration": text_, "clash": clash_, "frames": frames_,
                                         "patch": {"case": "patch-new", "patches": patches_}})
                out_["pictured"].append({"findingId": r_["findingId"], "formKey": r_["formKey"]})
                taken_ += 1
                room_ -= 1
        return out_

    # one name for each kind of clash, wherever it is said: a kind whose clashes all come from one check is called by
    # that check's title (the report's, and the closing's), never by a second name
    titles_ = reader.get("checkTitles") or {}
    type_labels, case_labels = dict(words["typeLabels"]), dict(words["caseLabels"])
    for t_ in {c["type"] for c in clashes.values()}:
        ck_ = {c["rows"][0]["checkId"] for c in clashes.values() if c["type"] == t_}     # the check that counts each
        if len(ck_) == 1 and titles_.get(next(iter(ck_))):
            ttl_ = str(titles_[next(iter(ck_))])
            type_labels[t_] = ttl_
            case_labels[t_] = ttl_[:1].lower() + ttl_[1:]
    counts = {}
    for c in clashes.values():
        counts.setdefault(c["type"], {"count": 0, "pairs": set()})
        counts[c["type"]]["count"] += 1
        counts[c["type"]]["pairs"].add(" and ".join(mod(m) for m in c["pair"]))
    affected = {target} | {p for r in kept_rows if (types.get(r.get("checkId")) or policy.get("otherType")) is not None
                            for p in (r["A"], r["B"])}
    affected_low = {str(p).lower() for p in affected}
    runtime_mods = sorted({o for m in pres.get("media") or [] for o in ((m.get("provenance") or {}).get("runtimeOverlayMods") or [])})
    unit_words = words.get("typeUnits") or {}
    rows_n = collections.Counter(c["type"] for c in clashes.values() for _ in c["rows"])

    def card_count(t):
        # a type counted by its rows (a navmesh clash is one record with many broken links) says the rows, as the report
        return rows_n[t] if t in (policy.get("countRows") or []) else counts[t]["count"]

    def card_unit(t, n):
        u = unit_words.get(t) or unit_words.get("default")
        return (u[0] if n == 1 else u[1]) if u else None
    # the opening's "testing is incomplete" line is a caveat: the report says it at its top, the reel's closing sentence
    # points there (Piranha, 2026-11-10)
    incomplete = None
    opening_pages = [
        {"layout": "types", "title": fill(words["stillTitles"]["types"], mod=mod(target)),
         "rows": [{"label": type_labels[t], "count": card_count(t), "unit": card_unit(t, card_count(t)),
                   "pairs": sorted(counts[t]["pairs"])}
                  for t in policy["typeOrder"] if t in counts],
         **({"incomplete": incomplete} if incomplete else {}),
         "leftOut": {"noInGameEffect": left_out, "markers": left_markers,
                     "line": fill(words["stillTitles"]["leftOut"], count=left_out, noun="record field" if left_out == 1 else "record fields")
                     if left_out else None,
                     "markersLine": fill(words["leftOutMarkers"], count=left_markers, noun="clash" if left_markers == 1 else "clashes")
                     if left_markers else None}},
        {"layout": "load-order", "title": words["stillTitles"]["loadOrder"], "legend": words["stillTitles"]["affected"],
         "plugins": full_order or order, "targets": [target],
         "affected": [p for p in (full_order or order) if str(p).lower() in affected_low] or sorted(affected),
         "runtimeMods": runtime_mods, "runtimeTitle": words["stillTitles"]["runtime"] if runtime_mods else None}]
    opening_say = fill((words["opening"] if counts else words["openingNone"]), mod=mod(target))
    if incomplete:
        opening_say += " " + incomplete
    # each page says its own words: the types page its lead and its rows, the load-order page what it highlights
    opening_pages[0]["sayLead"] = opening_say
    if words.get("loadOrderSay"):
        aff_ = [mod(p) for p in opening_pages[1]["affected"]]
        opening_pages[1]["say"] = fill(words["loadOrderSay"],
            count=len(opening_pages[1]["plugins"]), marked=len(aff_), names=joined(aff_))
    rate = float(policy.get("readingWordsPerSecond") or 0) or None
    for pg in opening_pages:
        if rate:
            pg["seconds"] = (float((policy.get("holdSeconds") or {}).get("loadOrderPage") or 6.0)
                             if pg["layout"] == "load-order" else page_seconds(pg, rate, policy))
    chapters = [{"id": "R-01", "kind": "opening", "narration": " ".join(x_ for x_ in (reel_stills.page_say(p_) for p_ in opening_pages) if x_),
                 "still": {"pages": opening_pages}}]
    # what the reel leaves to the report (Piranha, 2026-11-10: no caveat but one closing sentence): each entry names the
    # text the report carries for it; `report_lines` are the explanations the report prints because the reel does not
    moved, report_lines = [], []
    if pred.get("incompleteReel"):
        moved.append({"from": "opening", "text": str(pred["incompleteReel"]), "report": [str(pred.get("incompleteLine")
                      or pred["incompleteReel"]).split(".")[0]]})
    accounting = {}
    for t in policy.get("typeOrder") or []:
        if t not in counts:
            continue
        chosen = chosen_by_type.get(t) or []
        # one entry per clash (its object and partner), never per object: a pictured clash is never also unpictured
        accounting[t] = {"pictured": [clash_id(c) for c in chosen]
                         + [clash_id(g_) for c in chosen for g_ in in_split.get(c["rows"][0]["formKey"]) or []],
                         "unpictured": [clash_id(c) for c, _ in unpictured if c["type"] == t],
                         "represented": [clash_id(c) for c in represented.get(t, [])]}
        for i, c in enumerate(chosen):
            patch, evidence = patch_sentence(c)
            text = sentence(c) + " " + patch
            # what the report says of which mod the clash needs, from the same field, so the reel pins it no harder
            item_ = next((i_ for i_ in pred.get("items") or [] if i_.get("attributionText")
                          and i_.get("checkId") == c["rows"][0]["checkId"]
                          and {str(m_).lower() for m_ in i_.get("mods") or []} == {str(m_).lower() for m_ in c["pair"]}), None)
            if item_:
                text += " " + spoken(str(item_["attributionText"]))
            if i == 0:
                n, shown = card_count(t), len(chosen)
                intro = fill(words["cases"]["one" if n == 1 else "many"], count=n, label=case_labels[t])
                lead = ("" if n == 1 else words["lead"]["all"] if shown == n else
                        words["lead"]["one"] if shown == 1 else fill(words["lead"]["some"], count=shown))
                if t in (policy.get("countRows") or []) and words["cases"].get("rows"):
                    # (N2) a type counted in rows (navmesh links) says its unit, and the groups its examples come from
                    g_n = len({group_key(c_) for c_ in clashes.values() if c_["type"] == t})
                    intro = fill(words["cases"]["rowsOne" if n == 1 else "rows"],
                        count=n, unit=card_unit(t, n) or case_labels[t], groups=g_n,
                        groupNoun="group" if g_n == 1 else "groups")
                    lead = ("" if g_n == 1 else words["lead"]["groupsAll"] if shown == g_n else
                            words["lead"]["groupsOne"] if shown == 1 else fill(words["lead"]["groupsSome"], count=shown))
                text = " ".join(x for x in (intro, lead, text) if x)
            if any(r["formKey"] in game for r in c["rows"]):
                text += " " + words["playTime"]
            r = c["rows"][0]
            split = t == "record"
            states = states_of(c)
            frames = []
            for state in states:
                m = frame_media(c, state)
                # a cutaway or a sliced top-down says so on the reel too, not only in the frame's own label
                frames.append({"state": state, "armId": bare_of[state], "mediaId": (m or {}).get("id"),
                               "viewNote": view_kind_line((m or {}).get("provenance") or {}) or None,
                               **({"label": frame_label(c, state, m)} if not split and frame_label(c, state, m) else {})})
            # the frame's own label says how the picture was made; the narrator never reads it (no disclaimers, C1937)
            clash = {"type": t, "checkId": r["checkId"], "formKey": r["formKey"], "other": list(r.get("other") or []),
                     "pair": list(c["pair"]), "armId": with_arm, "withoutArmId": without_arm,
                     "findingId": r.get("findingId"), "highlight": highlight(c)}
            if split:
                lost, won = _position((r.get("values") or {}).get("lost")), _position((r.get("values") or {}).get("winner"))
                clash.update(place=None, actor=base_of(r["formKey"]).get("type") in (policy.get("actorTypes") or []),
                             placements={"left": list(lost) if lost else None, "right": list(won) if won else None},
                             # the cell each version is filed in (names enrichment, read from the plugin files)
                             cells={"left": (ref_of(r["formKey"]).get("cells") or {}).get(r["A"]),
                                    "right": (ref_of(r["formKey"]).get("cells") or {}).get(r["B"])},
                             labels={k: fill(v, loser=mod(r["A"]), winner=mod(r["B"]), mod=mod(target))
                                     for k, v in words["splitLabels"].items()},
                             # removals this split frames: marked on the left half, where they still stand
                             alsoMarked=[{"formKey": g_["rows"][0]["formKey"], "state": "left",
                                          "color": policy.get("removedMarkColour") or "magenta"}
                                         for g_ in in_split.get(r["formKey"]) or []])
            elif t == "navmesh":
                clash.update(place=place_of(r.get("summary"), [r["formKey"]]), earlier=earlier_navmesh(r))
            else:
                clash["place"] = geometry_slots(c)["place"]
                if t == "state":
                    m_ = STATE_KIND.match(str(r.get("kind") or ""))
                    clash.update(kind=r.get("kind"), state=m_.group(1) if m_ else None)
            text, cut_ = strip_caveats(text, words)
            moved += [{**x_, "from": "split" if split else "vignette", "formKey": r["formKey"]} for x_ in cut_]
            chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "split" if split else "vignette", "narration": text,
                             "clash": clash, "frames": frames, "patch": evidence})
    def where_(key, **slots):
        """Where in the report to find the clashes not pictured: under the prediction section's heading, or, when the
        presentation names none, the report itself (key + "Report"); never an empty heading in quotes (C2130)."""
        heading_ = spoken((reader.get("sectionHeadings") or {}).get("prediction") or "").strip()
        if heading_:
            return fill(words.get(key, ""), heading=heading_, **slots)
        return fill(words.get(key + "Report", ""), **slots)

    if unpictured or none_captured or any(represented.get(t_) for t_ in counts):
        n_un = len(unpictured)
        sentence_un = fill(words["unpictured"]["one" if n_un == 1 else "many"], count=n_un) if unpictured else ""
        if words.get("unpicturedAccount"):
            # per kind, in the opening card's unit: pictured + represented by the example + not pictured = the card's
            # count, so every clash on the card is accounted for; then what "represented" means and why, as sentences
            def units_(t_, cs_):
                return sum(len(c_["rows"]) if t_ in (policy.get("countRows") or []) else 1 for c_ in cs_)
            parts_, any_rep_, moved_ = [], False, collections.Counter()
            for t_ in policy.get("typeOrder") or []:
                if t_ not in counts:
                    continue
                shown_ = list(chosen_by_type.get(t_) or []) + [g_ for c_ in chosen_by_type.get(t_) or []
                                                               for g_ in in_split.get(c_["rows"][0]["formKey"]) or []]
                pic_, rep_ = units_(t_, shown_), units_(t_, represented.get(t_) or [])
                unp_ = units_(t_, [c_ for c_, _ in unpictured if c_["type"] == t_])
                tot_ = card_count(t_)
                if not pic_:
                    if rep_:
                        moved_[capture_why(represented.get(t_) or [])] += rep_
                    unp_, rep_ = unp_ + rep_, 0                 # no example shown: nothing stands for them
                if not (rep_ or unp_):
                    continue
                any_rep_ = any_rep_ or bool(rep_)
                other_ = tot_ - pic_ - rep_ - unp_
                if not pic_ and not rep_ and not other_ and words.get("unpicturedNone"):
                    parts_.append(fill(words["unpicturedNone"], label=type_labels[t_], total=tot_,
                                                                 unit=card_unit(t_, tot_) or "") + ".")
                    continue
                parts_.append(fill(words["unpicturedAccount"],
                    label=type_labels[t_], total=tot_, unit=card_unit(t_, tot_) or "",
                    counts=account_counts(words, pic_, rep_, unp_))
                    + (fill(words.get("unpicturedOther", ""), count=other_) if other_ else "") + ".")
            sentence_un = " ".join(parts_)
            if any_rep_ and words.get("representedMeans"):
                report_lines.append(words["representedMeans"])
            why_n = collections.Counter()
            for c_, why_ in unpictured:
                why_n[why_] += units_(c_["type"], [c_])
            why_n.update(moved_)
            reasons_ = policy.get("stillReasons") or {}
            # a buried clash's reason says how many camera spots each rests on (skill data stillReasonsCount)
            spots_ = sorted({b_["stations"] for c_, w_ in unpictured if w_ == "buried"
                             if (b_ := clash_buried([c_["rows"][0]["formKey"], *(c_["rows"][0].get("other") or [])],
                                                    capture_results))})

            def count_(w_, n_):
                tmpl_ = (policy.get("stillReasonsCount") or {}).get(w_)
                if not (tmpl_ and w_ == "buried" and spots_):
                    return f"{n_} not pictured"
                said_ = (f"{spots_[0]} to {spots_[-1]}" if len(spots_) > 3
                         else joined([str(x_) for x_ in spots_]))
                return fill(tmpl_, count=n_, spots=said_, plural="" if spots_ == [1] else "s",
                                     rest="it rests" if n_ == 1 else "they rest")
            if why_n and words.get("unpicturedReasons"):
                report_lines.append(fill(words["unpicturedReasons"], reasons=" ".join(
                    str(reasons_.get(w_, w_)).rstrip(".") + f" ({count_(w_, n_)})." for w_, n_ in why_n.most_common())))
            sentence_un += " " + where_("unpicturedWhere")
            sentence_un = sentence_un.strip()
        elif unpictured and words.get("unpicturedTyped"):
            # in the opening card's units, by type, and where in the report to find them; then why, by reason
            by_t = collections.Counter()
            for c, _ in unpictured:
                by_t[c["type"]] += len(c["rows"]) if c["type"] in (policy.get("countRows") or []) else 1
            parts = [f"{n_} {card_unit(t_, n_) or words['caseLabels'].get(t_, t_)}" for t_, n_ in by_t.items()]
            sentence_un = where_("unpicturedTyped", parts=joined(parts),
                                 verb="isn't" if sum(by_t.values()) == 1 else "aren't")
            why_n = collections.Counter()
            for c_, why_ in unpictured:
                why_n[why_] += len(c_["rows"]) if c_["type"] in (policy.get("countRows") or []) else 1
            short = policy.get("stillReasonsShort") or {}
            if short and words.get("unpicturedWhy"):
                report_lines.append(fill(words["unpicturedWhy"], reasons=joined(
                    [f"{n_} {short.get(w_, w_)}" for w_, n_ in why_n.most_common()])))
        for t_, n_, w_ in none_captured:
            # which limit stopped the picture (R1): said, never "no picture was captured" alone
            sentence_un = (sentence_un + " " + fill(words["noneCapturedOne" if n_ == 1 and words.get("noneCapturedOne")
                                                      else "noneCaptured"],
                count=n_, label=case_labels[t_], why=(words.get("noneCapturedWhy") or {})[w_])).strip()
        # a removal is said, never only counted: which mod takes which object out of the world
        gone = [c for c, why in unpictured if why == "removed"]
        def verb_(c):
            return (words.get("placesVerb") or {}).get(
                "own" if str(c["rows"][0]["formKey"]).split(":", 1)[-1].lower() == str(c["rows"][0]["A"]).lower()
                else "changes", "places")
        groups_ = {}
        for c in gone:
            groups_.setdefault((mod(c["rows"][0]["B"]), mod(c["rows"][0]["A"]), verb_(c)), []).append(c)
        for (win_, lose_, vb_), cs_ in groups_.items():
            if len(cs_) == 1 or not words.get("placementRemovedMany"):
                if words.get("placementRemoved"):
                    sentence_un += " " + " ".join(fill(words["placementRemoved"],
                        winner=win_, loser=lose_, places=vb_,
                        object=spoken_ref(c["rows"][0]["formKey"], c["rows"][0].get("summary"))) for c in cs_)
                continue
            # several objects one mod takes out of the world: said once, with a few named and the rest counted
            names_ = list(dict.fromkeys(spoken_ref(c["rows"][0]["formKey"], c["rows"][0].get("summary")) for c in cs_))
            sentence_un += " " + fill(words["placementRemovedMany"],
                winner=win_, loser=lose_, places=vb_, count=len(cs_),
                objects=joined(names_[:4] + ([f"{len(names_) - 4} more"] if len(names_) > 4 else [])))
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "unpictured", "narration": sentence_un, "count": n_un,
                         "clashes": [{"formKey": c["rows"][0]["formKey"], "type": c["type"], "reason": why,
                                      **({"buried": clash_buried([c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])],
                                                                 capture_results)} if why == "buried" else {}),
                                      "field": c["rows"][0].get("field"), "pair": [mod(m) for m in c["pair"]],
                                      "reasonText": (policy.get("stillReasons") or {}).get(why)}
                                     for c, why in unpictured],
                         "still": {"pages": [{"layout": "statement", "text": sentence_un,
                                              **({"seconds": page_seconds({"layout": "statement", "text": sentence_un},
                                                                          rate, policy)} if rate else {})}]}})
    # what to do: the report's own verdicts and next steps, the same sentences, before the closing list
    todo = []
    for x in pred.get("whatToDo") or []:
        x_, cut_ = strip_caveats(str(x), words)
        moved += [{**c_, "from": "verdict", "report": [str(x)]} for c_ in cut_]
        if x_:
            todo.append(spoken(x_))
    if todo:
        tpage = {"layout": "list", "title": pred.get("whatToDoHeading") or words["stillTitles"].get("whatToDo") or "",
                 "rows": todo, **({"maxWords": policy["pageMaxWords"]} if policy.get("pageMaxWords") else {})}
        if rate:
            tpage["seconds"] = page_seconds(tpage, rate, policy)
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "verdict", "narration": " ".join(todo),
                         "still": {"pages": [tpage]}})
    # a clash that appears only with the supplied patches loaded is pictured with them loaded, after the advice to check
    # it (rule: GATE-A of the patch-clash-pictures lane); one example per check (skill data patchNewPerCheck)
    patch_new = patch_new_chapters()
    for ch_ in patch_new["chapters"]:
        chapters.append({**ch_, "id": f"R-{len(chapters) + 1:02d}"})
    # the gaps the closing used to list, each in the report: computed as before, kept in `movedToReport`, never shown;
    # the closing itself is one sentence, said only when some check has no result
    # 1. the opening's own count and the checks behind it, each under its own reason (the same list, by construction)
    ic_ = pred.get("incompleteChecks") or {}
    checks, measured_titles = [], set()
    if ic_.get("checks") and words.get("closingLead"):
        checks.append({"reason": fill(words["closingLead"], missing=len(ic_["checks"]), total=ic_.get("total")),
                       "subjects": [], "comparison": False, "lead": True})
    for c_ in ic_.get("checks") or []:
        reason_, comparison_ = plain_reason(c_.get("reason"), words)
        row_ = next((r_ for r_ in checks if r_["reason"] == reason_), None)
        if row_ is None:
            row_ = {"reason": reason_, "subjects": [], "comparison": comparison_}
            checks.append(row_)
        if c_["title"] not in row_["subjects"]:
            row_["subjects"].append(c_["title"])
        measured_titles.add(c_["title"])
    # 2. what has no result only on the other tested setups (a comparison), said as that
    other_ = []
    for r_ in closing_checks(reader, pres, mod, patch_name, words):
        subs_ = [s_ for s_ in r_["subjects"] if check_title_of(s_) not in measured_titles]
        if subs_:
            other_.append({**r_, "reason": fill((words.get("closingOtherSetups") or "{reason}"), reason=r_["reason"]),
                           "subjects": subs_})
    head_titles = {str(i.get("title")): i for i in pred.get("items") or [] if i.get("shownAs") != "alone"}
    reconcile = []
    for row in other_:
        for subj in row.get("subjects") or []:
            it = head_titles.get(check_title_of(str(subj)))
            if it and words.get("closingReconcile") and row.get("comparison"):
                reconcile.append(fill(words["closingReconcile"], title=it.get("title"), count=it.get("count"),
                                                                  cases="case" if it.get("count") == 1 else "cases",
                                                                  verb="is" if it.get("count") == 1 else "are"))
    checks += other_
    if reconcile:
        checks.append({"reason": " ".join(dict.fromkeys(reconcile)), "subjects": [], "comparison": True,
                       "reconcile": [check_title_of(str(s_)) for r_ in other_ for s_ in r_["subjects"]
                                     if check_title_of(str(s_)) in head_titles]})
    # 3. checks that ran and could not judge some of their items: parts of checks that ran, with what they did judge
    skip_ = tuple(words.get("closingCoverageSkip") or ())
    partial_ = words.get("closingPartial") or {}
    for line_ in (reader.get("coverage") or {}).get("lines") or []:
        rsn_ = str(line_.get("reason") or "")
        if not rsn_ or (skip_ and rsn_.startswith(skip_)):
            continue
        subj_full_ = str(line_.get("subject") or "")
        if partial_ and rsn_ == partial_.get("reason"):
            check_, _, rest_ = subj_full_.partition(":")
            judged_ = re.search(r"beside ([\d,]+) the check did judge", rest_)
            cnt_ = line_.get("count")
            if isinstance(cnt_, int):
                item_ = fill(partial_["counted"], check=check_.strip(), count=f"{cnt_:,}", unit=line_.get("unit") or "items",
                                                   judged=judged_.group(1) if judged_ else "")
                if not judged_:
                    item_ = item_.split(partial_["judgedCut"])[0] if partial_.get("judgedCut") else item_
            else:
                # the largest population it could not judge, as a count, in one short phrase; the rest are in the report
                big_ = largest_population(rest_)
                item_ = (fill(partial_["uncountedOne"], check=check_.strip(), count=big_[0], what=big_[1],
                                                         more=partial_.get("uncountedMore", "") if big_[2] else "")
                         if big_ and partial_.get("uncountedOne") else fill(partial_["uncounted"], check=check_.strip()))
            row_ = next((r_ for r_ in checks if r_["reason"] == partial_["heading"]), None)
            if row_ is None:
                row_ = {"reason": partial_["heading"], "subjects": [], "comparison": False}
                checks.append(row_)
            row_["subjects"].append(item_)
            continue
        # 4. areas and states the evaluation did not measure, each its own item
        row_ = next((r_ for r_ in checks if r_["reason"] == rsn_), None)
        if row_ is None:
            row_ = {"reason": rsn_, "subjects": [], "comparison": False}
            checks.append(row_)
        for x_ in subj_full_.split("; "):
            if x_.strip() and x_.strip() not in row_["subjects"]:
                row_["subjects"].append(x_.strip())
    play_ = words.get("closingPlayTime")
    if play_ and any(play_ in str(n_) for n_ in (reader.get("coverage") or {}).get("notes") or []):
        checks.append({"reason": next(str(n_) for n_ in reader["coverage"]["notes"] if play_ in str(n_)),
                       "subjects": [], "comparison": False})
    for r_ in checks:
        if r_.get("lead"):
            trace_ = [f'{len(ic_["checks"])} of the {ic_.get("total")} checks']
        elif r_.get("reconcile") is not None:
            trace_ = list(dict.fromkeys(r_["reconcile"]))
        elif r_["subjects"]:
            trace_ = list(dict.fromkeys(gap_title(x_) for x_ in r_["subjects"]))
        else:
            trace_ = [r_["reason"]]
        moved.append({"from": "closing", "reason": r_["reason"], "subjects": list(r_["subjects"]), "report": trace_})
    moved += [{"from": "unpictured", "text": x_, "report": [x_]} for x_ in report_lines]
    if (ic_.get("checks") or other_) and words.get("closing"):
        cpage = {"layout": "statement", "text": words["closing"]}
        if rate:
            cpage["seconds"] = page_seconds(cpage, rate, policy)
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "closing", "narration": words["closing"],
                         "still": {"pages": [cpage]}})
    # (item 5) a long mod name said in full once, then by its short form; every name the reel can say is in the pool,
    # plugin stems too, so a short form never collides
    pool_ = [v.get("name") if isinstance(v, dict) else v for v in mod_names.values()] + [
        re.sub(r"\.(esp|esm|esl)$", "", str(k), flags=re.I) for k in mod_names] + [
        (v or {}).get("name") for v in names_mods.values() if isinstance(v, dict)] + [
        re.sub(r"\.(esp|esm|esl)$", "", str(x), flags=re.I) for x in full_order]
    short_forms = short_mod_names(pool_, words, subject=[mod(target), re.sub(r"\.(esp|esm|esl)$", "", str(target), flags=re.I)],
                                  same=plugin_name_groups(mod_names))
    say_short_forms(chapters, short_forms)
    if short_forms and rate:
        # a page's reading time is its text's, as shortened: the render holds it for that and refuses a long silence
        for ch_ in chapters:
            for pg_ in ((ch_.get("still") or {}).get("pages") or []) if isinstance(ch_.get("still"), dict) else []:
                if isinstance(pg_, dict) and "seconds" in pg_:
                    pg_["seconds"] = page_seconds(pg_, rate, policy)
    return {"schemaVersion": SCHEMA, "status": "planned", "reason": None,
            "policySha256": _sha(policy), "wordsSha256": _sha(words), "shortForms": short_forms,
            "patchSearchSha256": _sha(patch_search) if patch_search is not None else None,
            "modNames": {k: v for k, v in mod_names.items()}, "markers": sorted(markers),
            "sequence": list(policy["sequence"]), "holdSeconds": dict(policy.get("holdSeconds") or {}),
            "captureCandidates": candidates, "accounting": accounting, "capturesFinal": bool(captures_final),
            "patchNew": {"pictured": patch_new["pictured"], "unpictured": patch_new["unpictured"]},
            "noneCaptured": [{"type": t_, "count": n_, "why": w_} for t_, n_, w_ in none_captured],
            "movedToReport": moved, "reportLines": report_lines,
            "chapters": chapters}
