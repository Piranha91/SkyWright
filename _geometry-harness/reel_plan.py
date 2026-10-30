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

SCHEMA = 1


class Refused(ValueError):
    pass


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
    arms = {a.get("displayLabel"): a for a in (pres.get("scope") or {}).get("arms") or []}
    rows = []
    for m in reader.get("missingSummary") or []:
        subs = []
        for subj in m.get("subjects") or []:
            check_, _, setup = str(subj).partition(" — ")
            line = check_
            if len(reasons_of[check_]) > 1 and patches:
                has = [p for p in patches if p in ((arms.get(setup) or {}).get("locationPluginOrder") or [])]
                line = words["closingLines"]["withSetup" if has else "withoutSetup"].format(
                    check=check_, patch=patch_name((has or patches)[0]))
            if line not in subs:
                subs.append(line)
        rows.append({"reason": m.get("reason"), "subjects": subs})
    return rows


def _sha(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()


def plan_reel(pres: dict, names: dict | None, masters: dict | None, policy: dict, words: dict,
              patch_search: dict | None, mod_names: dict | None = None, capture_results: list | None = None,
              markers: set | None = None, switches: dict | None = None) -> dict:
    """`markers`: the references evalreport's marker rule calls editor-only (skill data markerModels / markerFlags,
    read from the records); a clash on one is no clash a player meets, so it is left out with a count. `switches`: the
    prediction setup's switch census rows by switch FormKey (edid, baselineEnabled, trigger), for a world-state clash's
    condition."""
    try:
        return _plan(pres, names or {}, masters or {}, policy, words, patch_search, mod_names or {}, capture_results or [],
                     set(markers or ()), switches or {})
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


def _plan(pres, names, masters, policy, words, patch_search, mod_names, capture_results, markers, switches):
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

    def place_of(summary, fks=()):
        m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", summary or "") or re.search(r"^Cell ([A-Za-z0-9_]+):", summary or "")
        cell = m.group(1) if m else next((cap_cell[str(f)] for f in fks if str(f) in cap_cell), None)
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
        t = types.get(r.get("checkId"))
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

    refused = {str(x.get("formKey")) for x in capture_results if x.get("status") == "refused" and x.get("formKey")}
    attempts = int(policy.get("captureAttemptsPerType") or policy.get("perType") or 1)
    chosen_by_type, unpictured, represented, candidates, others = {}, [], {}, {}, {}

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
                tried += 1
                if c["rows"][0]["formKey"] in refused:
                    tried_refused.append(c)
                    continue
                chosen.append(c)
                others[c["rows"][0]["formKey"]] = [x for x in g if x is not c]
                break
        unpictured += [(c, "captureRefused") for c in tried_refused]
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
            return lead + pick("listed").format(file=hits[0].get("file")), {**ev, "case": "listed", "hit": hits[0]}
        # "No patch exists" only on a search that found its own known answer and read every source of this pair
        unread = entry.get("unreadSources") or []
        if known_found and not unread:
            return lead + pick("noneOther" if own else "none"), {**ev, "case": "none-searched"}
        return (lead + pick("notFoundOther" if own else "notFound"),
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
                "modB": mod(second[1]), "objectB": spoken_ref(second[0])}

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
        rest = [spoken_ref(x["rows"][0]["formKey"], x["rows"][0].get("summary")) for x in others.get(fk) or [] if moved_row(x)]
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
                [lw_[k_].format(objects=joined(list(dict.fromkeys(v_)))) for k_, v_ in lost_items.items()]))
        for g_ in in_split.get(fk) or []:
            text += " " + words["removedInSplit"].format(
                winner=mod(g_["rows"][0]["B"]), loser=mod(g_["rows"][0]["A"]), subject=obj,
                object=spoken_ref(g_["rows"][0]["formKey"], g_["rows"][0].get("summary")))
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
        fks = {c["rows"][0]["formKey"], *(c["rows"][0].get("other") or [])}
        for m in pres.get("media") or []:
            prov = m.get("provenance") or {}
            if (m.get("assessed") and prov.get("vignetteState") == state and (m.get("subject") or {}).get("formKey") in fks
                    and (m.get("subject") or {}).get("armId") == arm_of[state]):
                return m
        return None

    type_labels = words["typeLabels"]
    counts = {}
    for c in clashes.values():
        counts.setdefault(c["type"], {"count": 0, "pairs": set()})
        counts[c["type"]]["count"] += 1
        counts[c["type"]]["pairs"].add(" and ".join(mod(m) for m in c["pair"]))
    affected = {target} | {p for r in kept_rows if types.get(r.get("checkId")) is not None for p in (r["A"], r["B"])}
    runtime_mods = sorted({o for m in pres.get("media") or [] for o in ((m.get("provenance") or {}).get("runtimeOverlayMods") or [])})
    opening_pages = [
        {"layout": "types", "title": words["stillTitles"]["types"].format(mod=mod(target)),
         "rows": [{"label": type_labels[t], "count": counts[t]["count"], "pairs": sorted(counts[t]["pairs"])}
                  for t in policy["typeOrder"] if t in counts],
         "leftOut": {"noInGameEffect": left_out, "markers": left_markers,
                     "line": words["stillTitles"]["leftOut"].format(count=left_out, noun="record field" if left_out == 1 else "record fields")
                     if left_out else None,
                     "markersLine": words["leftOutMarkers"].format(count=left_markers, noun="clash" if left_markers == 1 else "clashes")
                     if left_markers else None}},
        {"layout": "load-order", "title": words["stillTitles"]["loadOrder"], "legend": words["stillTitles"]["affected"],
         "plugins": full_order or order, "targets": [target], "affected": [p for p in order if p in affected] or sorted(affected),
         "runtimeMods": runtime_mods, "runtimeTitle": words["stillTitles"]["runtime"] if runtime_mods else None}]
    chapters = [{"id": "R-01", "kind": "opening",
                 "narration": (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),
                 "still": {"pages": opening_pages}}]
    accounting = {}
    for t in policy.get("typeOrder") or []:
        if t not in counts:
            continue
        chosen = chosen_by_type.get(t) or []
        accounting[t] = {"pictured": [c["rows"][0]["formKey"] for c in chosen]
                         + [g_["rows"][0]["formKey"] for c in chosen for g_ in in_split.get(c["rows"][0]["formKey"]) or []],
                         "unpictured": [c["rows"][0]["formKey"] for c, _ in unpictured if c["type"] == t],
                         "represented": [c["rows"][0]["formKey"] for c in represented.get(t, [])]}
        for i, c in enumerate(chosen):
            patch, evidence = patch_sentence(c)
            text = sentence(c) + " " + patch
            if i == 0:
                n, shown = counts[t]["count"], len(chosen)
                intro = words["cases"]["one" if n == 1 else "many"].format(count=n, label=words["caseLabels"][t])
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
                frames.append({"state": state, "armId": arm_of[state], "mediaId": (m or {}).get("id")})
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
    if unpictured:
        n_un = len(unpictured)
        sentence_un = words["unpictured"]["one" if n_un == 1 else "many"].format(count=n_un)
        # a removal is said, never only counted: which mod takes which object out of the world
        gone = [c for c, why in unpictured if why == "removed"]
        if gone and words.get("placementRemoved"):
            sentence_un += " " + " ".join(words["placementRemoved"].format(
                winner=mod(c["rows"][0]["B"]), loser=mod(c["rows"][0]["A"]),
                object=spoken_ref(c["rows"][0]["formKey"], c["rows"][0].get("summary"))) for c in gone)
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "unpictured", "narration": sentence_un, "count": n_un,
                         "clashes": [{"formKey": c["rows"][0]["formKey"], "type": c["type"], "reason": why,
                                      "field": c["rows"][0].get("field"), "pair": [mod(m) for m in c["pair"]],
                                      "reasonText": (policy.get("stillReasons") or {}).get(why)}
                                     for c, why in unpictured],
                         "still": {"pages": [{"layout": "statement", "text": sentence_un}]}})
    checks = closing_checks(reader, pres, mod, patch_name, words)
    chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "closing", "narration": words["closing"],
                     "still": {"pages": [{"layout": "checks", "title": words["stillTitles"]["checks"], "rows": checks}]}})
    return {"schemaVersion": SCHEMA, "status": "planned", "reason": None,
            "policySha256": _sha(policy), "wordsSha256": _sha(words),
            "patchSearchSha256": _sha(patch_search) if patch_search is not None else None,
            "modNames": {k: v for k, v in mod_names.items()}, "markers": sorted(markers),
            "sequence": list(policy["sequence"]), "holdSeconds": dict(policy.get("holdSeconds") or {}),
            "captureCandidates": candidates, "accounting": accounting,
            "chapters": chapters}
