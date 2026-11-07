"""The review reel's plan: which clashes it shows, how, and what it says (Piranha's vignette format, task
2026-10-19-review-reel-vignettes).

    plan_reel(pres, names, masters, policy, words, patch_search, mod_names, capture_results=None, markers=, switches=) -> dict

`pres` is the presentation evalreport is compiling (its `reader.prediction`, `reader.missingSummary`,
`reader.suppliedPatches`, `scope.arms` and `media`); `names` the bundle's names enrichment (refs, bases, objects,
places); `masters` each plugin's own master list (lower case); `policy` the skill's `scene-policy.json` `vignettes`
block (plus the report's `effectModelPattern`); `words` the skill's `report-priorities.json` `reel.words`;
`patch_search` the recorded Nexus search (`evalreport plan --patch-search`); `mod_names` {plugin: {name, source}}
(evalreport --mod-name, else the MO2 mod folder the evaluation recorded for the plugin); `capture_results` the
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
        return ("Sliced top-down view from above" + (f", everything above z {float(cut):.0f} removed" if cut is not None else "")
                + "; not the player's view.")
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


def captured_request(fks, capture_results) -> str | None:
    """The id of the captured request about this clash (`result_about`), or None."""
    return next((x.get("request") for x in capture_results or []
                 if x.get("status") == "captured" and result_about(x, fks)), None)


def joined(xs: list[str]) -> str:
    return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


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
                    line = words["closingLines"]["setupWithout" if gone_ and not new_ else
                                                 "setupWith" if new_ and not gone_ else "setupBoth"].format(
                        check=check_, without=joined(gone_) if gone_ else "", with_=joined(new_) if new_ else "")
            elif not arm_ and len(reasons_of[check_]) > 1 and patches:
                has = [p for p in patches if p in ((arms.get(setup) or {}).get("locationPluginOrder") or [])]
                line = words["closingLines"]["withSetup" if has else "withoutSetup"].format(
                    check=check_, patch=patch_name((has or patches)[0]))
            if line not in subs:
                subs.append(line)
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


def check_title_of(line: str) -> str:
    """A closing line's check title, without the setup it names."""
    return re.split(r", (?:with|without|on the setup) ", str(line), maxsplit=1)[0]


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


def lead_qualifiers(hit: dict, words: dict, loaded: set, evaluated: bool) -> str:
    """What a reader must know about a patch file the Nexus search lists, from its own recorded fields: retired,
    archived or an old version; other mods it names that this setup does not load; that it may be the evaluated patch
    (Nexus does not name a file's plugins). The report and the reel say the same clauses."""
    lw = words.get("leadCautions") or {}
    out = []
    for key in (str(hit.get("status") or ""), str(hit.get("category") or "")):
        if key and lw.get(key) and lw[key] not in out:
            out.append(lw[key])
    other = hit.get("otherMentions")
    other = [other] if isinstance(other, str) else list(other or [])
    other = [m for m in other if str(m).lower() not in loaded]
    if other and lw.get("other"):
        out.append(lw["other"].format(mods=joined([str(m) for m in other])))
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
    return sorted(hits, key=rank)


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
            return words["conditionQuest"].format(quest=quest, stage=t.group(2))
    return words["conditionFallback"].format(place=place)


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
                          f"given and the evaluation records no mod folder for it")
        return got

    def patch_name(plugin: str) -> str:
        nm = names_mods.get(plugin) or {}
        if nm.get("source") == "declared":
            return nm["name"]
        stem = re.sub(r"\.(esp|esm|esl)$", "", plugin, flags=re.I)
        folder = mod(plugin)
        return stem if folder.lower() == stem.lower() else words["patchName"].format(plugin=stem, mod=folder)

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
        cell = cell or next((cap_cell[str(f)] for f in fks if str(f) in cap_cell), None)
        if cell and re.search(r"(?:^|_)es[mpl]$", cell, re.I):
            return words.get("placeUnnamed") or "an unnamed place"
        return (places.get(cell) or {}).get("name") or humanize(cell) or "this place"

    with_arm = pred.get("predictionArm")
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

    def unpictured_reason(c):
        """None for a clash a picture can show; else the skill's reason it cannot (stillReasons keys)."""
        if c["type"] not in (policy.get("pictureTypes") or []):
            return c["type"]
        r = c["rows"][0]
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
    none_captured = []      # (type, count) of kinds with clashes to picture and no captured frame, capture closed

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
        pairs: dict = {}
        for g in groups.values():
            pairs.setdefault(g[0]["pair"], []).append(g)
        pair_order = sorted(pairs, key=lambda p: (-len(pairs[p]), p))
        ordering, i = [], 0                                 # round the pairs until every group has a place
        while any(pairs.values()):
            p = pair_order[i % len(pair_order)]
            if pairs[p]:
                ordering.append(pairs[p].pop(0))
            i += 1
        quota = len(ordering) if len(ordering) <= policy["allUpTo"] else policy["perType"]
        chosen, tried_refused, tried = [], [], 0
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
                others[c["rows"][0]["formKey"]] = [x for x in g if x is not c]
                break
        unpictured += [(c, "captureRefused") for c in tried_refused]
        if captures_final and not chosen and t in (policy.get("pictureTypes") or []) and len(draw) > len(tried_refused):
            none_captured.append((t, len(draw) - len(tried_refused)))
        chosen_by_type[t] = chosen
        if t in (policy.get("pictureTypes") or []):
            candidates[t] = [c["rows"][0]["formKey"] for g in ordering for c in g][:attempts]
        represented[t] = [c for c in draw if c not in chosen and c not in tried_refused]
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
    need_tools = set((policy.get("patchSearch") or {}).get("requiredTools") or [])
    for c in selected:
        key = frozenset(m.lower() for m in c["pair"])
        entry = searched.get(key)
        tools = {q.get("tool") for q in (entry or {}).get("queries") or []}
        if entry is None or not need_tools <= tools:
            raise Refused(f"the patch-search file does not cover the pair {' and '.join(c['pair'])} with "
                          f"{', '.join(sorted(need_tools))}; search it and record the queries")

    supplied = list(reader.get("suppliedPatches") or [])
    # the search's known answer is derived here from its recorded hits, never taken from a flag the file states:
    # found means a recorded hit names the expected patch (its plugin name without the extension)
    known = (patch_search or {}).get("knownAnswer") or {}
    want = re.sub(r"\.(esp|esm|esl)$", "", str(known.get("patch") or ""), flags=re.I).lower()
    known_found = bool(want) and any(want in json.dumps(h, ensure_ascii=False).lower() for h in known.get("hits") or [])

    def own_patches(c):
        other = [m for m in c["pair"] if m != target][0]
        return [p for p in supplied if {other.lower(), target.lower()} <= set(masters.get(p.lower(), set()))]

    used: dict = {}

    def pick(case):
        """The k-th patch sentence of a case in reel order takes phrasing k mod the pool's size (skill data
        reel.words.patch): deterministic, and never the same phrasing twice in a row for one case."""
        pool = words["patch"][case]
        pool = pool if isinstance(pool, list) else [pool]
        k = used.get(case, 0)
        used[case] = k + 1
        return pool[k % len(pool)]

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
        return " " + words["patchNeeds"].format(patch=patch_name(patch), mods=joined(nm_))

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
                    text_ = pick(key).format(patch=patch_name(own[0]), object=spoken_ref(moved[0]),
                                             m=max(1, round(moved[1])))
                else:
                    key = "fixesOverlap"
                    text_ = pick(key).format(patch=patch_name(own[0]))
                return (text_ + needs(own[0], c), {**evidence, "case": "own-" + key, "patch": own[0],
                                "masters": sorted(masters.get(own[0].lower(), set())),
                                "moved": [moved[0], round(moved[1], 1)] if moved else None})
            return (pick(key).format(patch=patch_name(own[0]), kept=kept) + needs(own[0], c),
                    {**evidence, "case": "own-" + key, "patch": own[0], "masters": sorted(masters.get(own[0].lower(), set()))})
        lead = pick("leaves").format(patch=patch_name(own[0])) + " " if own else ""
        ev = {**evidence, "patch": own[0] if own else None,
              "masters": sorted(masters.get(own[0].lower(), set())) if own else None}
        if hits:
            loaded_ = {str(x).lower() for x in full_order or order}
            hit_ = usable_first(hits, loaded_)[0]
            q_ = lead_qualifiers(hit_, words, loaded_, bool(own))
            return (lead + pick("listed").format(file=hit_.get("file")) + q_,
                    {**ev, "case": "listed", "hit": hit_, "qualifiers": q_.strip() or None})
        # "No patch exists" only on a search that found its own known answer and read every source of this pair
        unread = entry.get("unreadSources") or []
        if known_found and not unread:
            return lead + pick("noneOther" if own else "none"), {**ev, "case": "none-searched"}
        return (lead + pick("notFoundOther" if own else "notFound").format(patch=patch_name(own[0]) if own else ""),
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
        if state != "highlight":
            return lw[state].format(mod=mod(target))
        argv = [str(x) for x in ((m or {}).get("provenance") or {}).get("argv") or []]
        marks = {}
        for i_, x_ in enumerate(argv[:-1]):
            if x_ == "--ref" and "=" in argv[i_ + 1]:
                fk_, col_ = argv[i_ + 1].rsplit("=", 1)
                marks[col_] = fk_
        parts = [lw["mark"].format(colour=col_, object=spoken_ref(fk_)) for col_, fk_ in marks.items()]
        return lw["highlight"].format(marks="; ".join(parts)) if parts else lw.get("highlightNoMarks")

    def thing(fk, owner_mod):
        """The object as the plugin that made it: "<mod>'s <object>" only when the mod defines it; an object of the base
        game (or of a third plugin) the mod only changes is said as that, never as the mod's own."""
        obj = spoken_ref(fk)
        made = str(fk).split(":", 1)[-1]
        if made.lower() == str(owner_mod).lower() or not words.get("thingChanged"):
            return words.get("thingOwn", "{mod}'s {object}").format(mod=mod(owner_mod), object=obj)
        base_ = made.lower() in {x.lower() for x in policy.get("baseGameMasters") or []}
        origin = words.get("baseGameName", "the base game") if base_ else re.sub(r"\.(esp|esm|esl)$", "", made, flags=re.I)
        return words["thingChanged"].format(origin=origin, object=obj, mod=mod(owner_mod))

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
                or words["structurePlace"].format(object=obj)
            if off >= float(policy.get("structureShare") or 1.0):
                who = words["structureReplaced"].format(mover=mod(mover), keeper=mod(keeper), place=place, object=obj)
            elif dzs and share >= float(policy.get("structureShare") or 1.0) and abs(dz_obj) > tol:
                who = words["structureUp" if dz_obj > 0 else "structureDown"].format(
                    mover=mod(mover), keeper=mod(keeper), place=place, object=obj)
            else:
                who = words["moverOne"].format(mover=mod(mover), keeper=mod(keeper), object=obj)
        elif moved is not None and len(moved) == 2:
            who = words["moverBoth"].format(a=mod(A), b=mod(B), object=obj)
        else:
            who = words["placementSplit"].format(loser=mod(A), winner=mod(B), object=obj, mod=mod(target))
        text = who
        lost, won = _position((r.get("values") or {}).get("lost")), _position((r.get("values") or {}).get("winner"))
        if lost and won:
            dx, dy, dz = (won[i] - lost[i] for i in range(3))
            dist = (dx * dx + dy * dy + dz * dz) ** 0.5
            mpu = float(policy.get("metresPerUnit") or 0.01428)
            if dist * mpu < 5:
                where = words["where"]["close"]
            elif abs(dz) >= 0.5 * dist:
                where = words["where"]["below" if dz < 0 else "above"].format(m=int(round(abs(dz) * mpu / 5.0) * 5))
            else:
                where = words["where"]["away"].format(m=int(round(dist * mpu / 5.0) * 5))
            sup = ((frame_media(c, "right") or {}).get("provenance") or {}).get("support")
            extra = ""
            if sup:
                if sup.get("terrainAbove") and not sup.get("interior"):
                    extra = words["spotExtra"]["belowGround"]
                elif sup.get("down") is None:
                    extra = words["spotExtra"]["noFloor" if sup.get("interior") else "inAir"]
            text += " " + words["rightSpot"].format(mod=mod(target), where=where) + extra + "."
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
                text += " " + words["groupOthers"]["more"].format(names=nm, count=len(rest) - 3)
            else:
                text += " " + words["groupOthers"]["one" if len(rest) == 1 else "many"].format(names=nm)
        if lost_items and words.get("alsoLost"):
            lw_ = dict(words.get("lostFieldWords") or {}, turn=words.get("lostTurnWord") or "the turn of {objects}")
            text += " " + words["alsoLost"].format(loser=mod(A), winner=mod(B), items=joined(
                [lw_[k_].format(objects=joined(say_names(v_))) for k_, v_ in lost_items.items()]))
        said_names_ = [spoken_ref(x["rows"][0]["formKey"], x["rows"][0].get("summary")) for x in group]
        for g_ in in_split.get(fk) or []:
            nm_g_ = spoken_ref(g_["rows"][0]["formKey"], g_["rows"][0].get("summary"))
            # a name another record of this chapter shares is said as "another" one, so the two are not heard as one
            if said_names_.count(nm_g_) > 1 and words.get("anotherObject"):
                nm_g_ = words["anotherObject"].format(object=nm_g_)
            text += " " + words["removedInSplit"].format(
                winner=mod(g_["rows"][0]["B"]), loser=mod(g_["rows"][0]["A"]), subject=obj, object=nm_g_)
        return text

    def sentence(c):
        r = c["rows"][0]
        if c["type"] == "record":
            return orient(c)
        s = geometry_slots(c)
        if c["type"] == "state":
            return words["stateClash"].format(condition=condition(r, names, switches, s["place"], words),
                                              **{k: v for k, v in s.items() if k != "place"})
        return words["overlap"].format(**s)

    game = {(m.get("subject") or {}).get("formKey") for m in pres.get("media") or []
            if m.get("assessed") and m.get("mediaKind") == "game-video"}
    arm_of = {"without": without_arm, "with": with_arm, "highlight": with_arm, "left": without_arm, "right": with_arm}

    def frame_media(c, state):
        """A clash's frame in one state, from ONE capture request: the captured request about the clash, else the
        request of its first matching frame. Two requests can picture one object (WRW: the tower in two pairs), and
        frames from both would mix two poses in one vignette."""
        fks = {c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])}

        def matches(m, st):
            prov = m.get("provenance") or {}
            return (m.get("assessed") and prov.get("vignetteState") == st and (m.get("subject") or {}).get("formKey") in fks
                    and (m.get("subject") or {}).get("armId") == arm_of[st])
        req = captured_request([c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])], capture_results)
        if req is None:
            first = next((m for st in states_of(c) for m in pres.get("media") or [] if matches(m, st)), None)
            req = ((first or {}).get("provenance") or {}).get("captureRequest")
        for m in pres.get("media") or []:
            if matches(m, state) and (req is None or (m.get("provenance") or {}).get("captureRequest") == req):
                return m
        return None

    def states_of(c):
        return ["left", "right"] if c["type"] == "record" else list(policy["sequence"])

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
    incomplete = pred.get("incompleteReel")
    opening_pages = [
        {"layout": "types", "title": words["stillTitles"]["types"].format(mod=mod(target)),
         "rows": [{"label": type_labels[t], "count": card_count(t), "unit": card_unit(t, card_count(t)),
                   "pairs": sorted(counts[t]["pairs"])}
                  for t in policy["typeOrder"] if t in counts],
         **({"incomplete": incomplete} if incomplete else {}),
         "leftOut": {"noInGameEffect": left_out, "markers": left_markers,
                     "line": words["stillTitles"]["leftOut"].format(count=left_out, noun="record field" if left_out == 1 else "record fields")
                     if left_out else None,
                     "markersLine": words["leftOutMarkers"].format(count=left_markers, noun="clash" if left_markers == 1 else "clashes")
                     if left_markers else None}},
        {"layout": "load-order", "title": words["stillTitles"]["loadOrder"], "legend": words["stillTitles"]["affected"],
         "plugins": full_order or order, "targets": [target],
         "affected": [p for p in (full_order or order) if str(p).lower() in affected_low] or sorted(affected),
         "runtimeMods": runtime_mods, "runtimeTitle": words["stillTitles"]["runtime"] if runtime_mods else None}]
    opening_say = (words["opening"] if counts else words["openingNone"]).format(mod=mod(target))
    if incomplete:
        opening_say += " " + incomplete
    # each page says its own words: the types page its lead and its rows, the load-order page what it highlights
    opening_pages[0]["sayLead"] = opening_say
    if words.get("loadOrderSay"):
        aff_ = [mod(p) for p in opening_pages[1]["affected"]]
        opening_pages[1]["say"] = words["loadOrderSay"].format(
            count=len(opening_pages[1]["plugins"]), marked=len(aff_), names=joined(aff_))
    rate = float(policy.get("readingWordsPerSecond") or 0) or None
    for pg in opening_pages:
        if rate:
            pg["seconds"] = (float((policy.get("holdSeconds") or {}).get("loadOrderPage") or 6.0)
                             if pg["layout"] == "load-order" else page_seconds(pg, rate, policy))
    chapters = [{"id": "R-01", "kind": "opening", "narration": " ".join(x_ for x_ in (reel_stills.page_say(p_) for p_ in opening_pages) if x_),
                 "still": {"pages": opening_pages}}]
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
                intro = words["cases"]["one" if n == 1 else "many"].format(count=n, label=case_labels[t])
                lead = ("" if n == 1 else words["lead"]["all"] if shown == n else
                        words["lead"]["one"] if shown == 1 else words["lead"]["some"].format(count=shown))
                text = " ".join(x for x in (intro, lead, text) if x)
            if any(r["formKey"] in game for r in c["rows"]):
                text += " " + words["playTime"]
            r = c["rows"][0]
            split = t == "record"
            states = ["left", "right"] if split else list(policy["sequence"])
            frames = []
            for state in states:
                m = frame_media(c, state)
                # a cutaway or a sliced top-down says so on the reel too, not only in the frame's own label
                frames.append({"state": state, "armId": arm_of[state], "mediaId": (m or {}).get("id"),
                               "viewNote": view_kind_line((m or {}).get("provenance") or {}) or None,
                               **({"label": frame_label(c, state, m)} if not split and frame_label(c, state, m) else {})})
            notes_ = list(dict.fromkeys(f_["viewNote"] for f_ in frames if f_.get("viewNote")))
            if notes_:
                text = (text + " " + " ".join(notes_)).strip()
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
                             labels={k: v.format(loser=mod(r["A"]), winner=mod(r["B"]), mod=mod(target))
                                     for k, v in words["splitLabels"].items()},
                             # removals this split frames: marked on the left half, where they still stand
                             alsoMarked=[{"formKey": g_["rows"][0]["formKey"], "state": "left",
                                          "color": policy.get("removedMarkColour") or "magenta"}
                                         for g_ in in_split.get(r["formKey"]) or []])
            else:
                clash["place"] = geometry_slots(c)["place"]
                if t == "state":
                    m_ = STATE_KIND.match(str(r.get("kind") or ""))
                    clash.update(kind=r.get("kind"), state=m_.group(1) if m_ else None)
            chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "split" if split else "vignette", "narration": text,
                             "clash": clash, "frames": frames, "patch": evidence})
    if unpictured or none_captured or any(represented.get(t_) for t_ in counts):
        n_un = len(unpictured)
        sentence_un = words["unpictured"]["one" if n_un == 1 else "many"].format(count=n_un) if unpictured else ""
        if words.get("unpicturedAccount"):
            # per kind, in the opening card's unit: pictured + represented by the example + not pictured = the card's
            # count, so every clash on the card is accounted for; then what "represented" means and why, as sentences
            def units_(t_, cs_):
                return sum(len(c_["rows"]) if t_ in (policy.get("countRows") or []) else 1 for c_ in cs_)
            parts_, any_rep_, moved_ = [], False, 0
            for t_ in policy.get("typeOrder") or []:
                if t_ not in counts:
                    continue
                shown_ = list(chosen_by_type.get(t_) or []) + [g_ for c_ in chosen_by_type.get(t_) or []
                                                               for g_ in in_split.get(c_["rows"][0]["formKey"]) or []]
                pic_, rep_ = units_(t_, shown_), units_(t_, represented.get(t_) or [])
                unp_ = units_(t_, [c_ for c_, _ in unpictured if c_["type"] == t_])
                tot_ = card_count(t_)
                if not pic_:
                    moved_ += rep_
                    unp_, rep_ = unp_ + rep_, 0                 # no example shown: nothing stands for them
                if not (rep_ or unp_):
                    continue
                any_rep_ = any_rep_ or bool(rep_)
                other_ = tot_ - pic_ - rep_ - unp_
                if not pic_ and not rep_ and not other_ and words.get("unpicturedNone"):
                    parts_.append(words["unpicturedNone"].format(label=type_labels[t_], total=tot_,
                                                                 unit=card_unit(t_, tot_) or "") + ".")
                    continue
                parts_.append(words["unpicturedAccount"].format(
                    label=type_labels[t_], total=tot_, unit=card_unit(t_, tot_) or "", pictured=pic_, represented=rep_,
                    unpictured=unp_) + (words.get("unpicturedOther", "").format(count=other_) if other_ else "") + ".")
            sentence_un = " ".join(parts_)
            if any_rep_ and words.get("representedMeans"):
                sentence_un += " " + words["representedMeans"]
            why_n = collections.Counter()
            for c_, why_ in unpictured:
                why_n[why_] += units_(c_["type"], [c_])
            if moved_:
                why_n["notCaptured"] += moved_
            reasons_ = policy.get("stillReasons") or {}
            if why_n and words.get("unpicturedReasons"):
                sentence_un += " " + words["unpicturedReasons"].format(reasons=" ".join(
                    str(reasons_.get(w_, w_)).rstrip(".") + f" ({n_} not pictured)." for w_, n_ in why_n.most_common()))
            sentence_un += " " + words.get("unpicturedWhere", "").format(
                heading=spoken((reader.get("sectionHeadings") or {}).get("prediction") or ""))
            sentence_un = sentence_un.strip()
        elif unpictured and words.get("unpicturedTyped"):
            # in the opening card's units, by type, and where in the report to find them; then why, by reason
            by_t = collections.Counter()
            for c, _ in unpictured:
                by_t[c["type"]] += len(c["rows"]) if c["type"] in (policy.get("countRows") or []) else 1
            parts = [f"{n_} {card_unit(t_, n_) or words['caseLabels'].get(t_, t_)}" for t_, n_ in by_t.items()]
            sentence_un = words["unpicturedTyped"].format(
                parts=joined(parts), verb="isn't" if sum(by_t.values()) == 1 else "aren't",
                heading=spoken((reader.get("sectionHeadings") or {}).get("prediction") or ""))
            why_n = collections.Counter()
            for c_, why_ in unpictured:
                why_n[why_] += len(c_["rows"]) if c_["type"] in (policy.get("countRows") or []) else 1
            short = policy.get("stillReasonsShort") or {}
            if short and words.get("unpicturedWhy"):
                sentence_un += " " + words["unpicturedWhy"].format(reasons=joined(
                    [f"{n_} {short.get(w_, w_)}" for w_, n_ in why_n.most_common()]))
        for t_, n_ in none_captured:
            sentence_un = (sentence_un + " " + words["noneCaptured"].format(
                count=n_, label=case_labels[t_])).strip()
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
                    sentence_un += " " + " ".join(words["placementRemoved"].format(
                        winner=win_, loser=lose_, places=vb_,
                        object=spoken_ref(c["rows"][0]["formKey"], c["rows"][0].get("summary"))) for c in cs_)
                continue
            # several objects one mod takes out of the world: said once, with a few named and the rest counted
            names_ = list(dict.fromkeys(spoken_ref(c["rows"][0]["formKey"], c["rows"][0].get("summary")) for c in cs_))
            sentence_un += " " + words["placementRemovedMany"].format(
                winner=win_, loser=lose_, places=vb_, count=len(cs_),
                objects=joined(names_[:4] + ([f"{len(names_) - 4} more"] if len(names_) > 4 else [])))
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "unpictured", "narration": sentence_un, "count": n_un,
                         "clashes": [{"formKey": c["rows"][0]["formKey"], "type": c["type"], "reason": why,
                                      "field": c["rows"][0].get("field"), "pair": [mod(m) for m in c["pair"]],
                                      "reasonText": (policy.get("stillReasons") or {}).get(why)}
                                     for c, why in unpictured],
                         "still": {"pages": [{"layout": "statement", "text": sentence_un,
                                              **({"seconds": page_seconds({"layout": "statement", "text": sentence_un},
                                                                          rate, policy)} if rate else {})}]}})
    # what to do: the report's own verdicts and next steps, the same sentences, before the closing list
    todo = [spoken(str(x)) for x in pred.get("whatToDo") or []]
    if todo:
        tpage = {"layout": "list", "title": pred.get("whatToDoHeading") or words["stillTitles"].get("whatToDo") or "",
                 "rows": todo, **({"maxWords": policy["pageMaxWords"]} if policy.get("pageMaxWords") else {})}
        if rate:
            tpage["seconds"] = page_seconds(tpage, rate, policy)
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "verdict", "narration": " ".join(todo),
                         "still": {"pages": [tpage]}})
    # the closing, in the order a viewer reads it, each page saying its own words:
    # 1. the opening's own count and the checks behind it, each under its own reason (the same list, by construction)
    ic_ = pred.get("incompleteChecks") or {}
    checks, measured_titles = [], set()
    if ic_.get("checks") and words.get("closingLead"):
        checks.append({"reason": words["closingLead"].format(missing=len(ic_["checks"]), total=ic_.get("total")),
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
            other_.append({**r_, "reason": (words.get("closingOtherSetups") or "{reason}").format(reason=r_["reason"]),
                           "subjects": subs_})
    head_titles = {str(i.get("title")): i for i in pred.get("items") or [] if i.get("shownAs") != "alone"}
    reconcile = []
    for row in other_:
        for subj in row.get("subjects") or []:
            it = head_titles.get(check_title_of(str(subj)))
            if it and words.get("closingReconcile") and row.get("comparison"):
                reconcile.append(words["closingReconcile"].format(title=it.get("title"), count=it.get("count"),
                                                                  cases="case" if it.get("count") == 1 else "cases",
                                                                  verb="is" if it.get("count") == 1 else "are"))
    checks += other_
    if reconcile:
        checks.append({"reason": " ".join(dict.fromkeys(reconcile)), "subjects": [], "comparison": True})
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
                item_ = partial_["counted"].format(check=check_.strip(), count=f"{cnt_:,}", unit=line_.get("unit") or "items",
                                                   judged=judged_.group(1) if judged_ else "")
                if not judged_:
                    item_ = item_.split(partial_["judgedCut"])[0] if partial_.get("judgedCut") else item_
            else:
                # the largest population it could not judge, as a count, in one short phrase; the rest are in the report
                big_ = largest_population(rest_)
                item_ = (partial_["uncountedOne"].format(check=check_.strip(), count=big_[0], what=big_[1],
                                                         more=partial_.get("uncountedMore", "") if big_[2] else "")
                         if big_ and partial_.get("uncountedOne") else partial_["uncounted"].format(check=check_.strip()))
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
    cpage = {"layout": "checks", "title": words["stillTitles"]["checks"], "rows": checks,
             **({"maxWords": policy["pageMaxWords"]} if policy.get("pageMaxWords") else {})}
    if rate:
        cpage["seconds"] = page_seconds(cpage, rate, policy)
    chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "closing", "narration": reel_stills.page_say(cpage),
                     "still": {"pages": [cpage]}})
    return {"schemaVersion": SCHEMA, "status": "planned", "reason": None,
            "policySha256": _sha(policy), "wordsSha256": _sha(words),
            "patchSearchSha256": _sha(patch_search) if patch_search is not None else None,
            "modNames": {k: v for k, v in mod_names.items()}, "markers": sorted(markers),
            "sequence": list(policy["sequence"]), "holdSeconds": dict(policy.get("holdSeconds") or {}),
            "captureCandidates": candidates, "accounting": accounting, "capturesFinal": bool(captures_final),
            "noneCaptured": [{"type": t_, "count": n_} for t_, n_ in none_captured],
            "chapters": chapters}
