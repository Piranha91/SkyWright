"""The review reel's plan: which clashes it shows, how, and what it says (Piranha's vignette format, task
2026-10-19-review-reel-vignettes).

    plan_reel(pres, names, masters, policy, words, patch_search, mod_names, capture_results=None) -> dict

`pres` is the presentation evalreport is compiling (its `reader.prediction`, `reader.missingSummary`,
`reader.suppliedPatches`, `scope.arms` and `media`); `names` the bundle's names enrichment (refs, bases, objects,
places); `masters` each plugin's own master list (lower case); `policy` the skill's `scene-policy.json` `vignettes`
block (plus the report's `effectModelPattern`); `words` the skill's `report-priorities.json` `reel.words`;
`patch_search` the recorded Nexus search (`evalreport plan --patch-search`); `mod_names` {plugin: {name, source}}
(evalreport --mod-name, else the MO2 mod folder the evaluation recorded for the plugin); `capture_results` the
capture step's per-request results, whose refusals turn a vignette into a still and are listed on the closing.

The narration is built only from the skill's templates and typed slots: mod names, in-game place and object names
(else the editor id in words), field labels and patch names. No report sentence (verdict, scope, limitations, finding
text) is read here, so none can be spoken. Selection reads only the prediction record, never which media exists, so
the plan before capture and after rebundling selects the same clashes.

Returns {"status": "planned", chapters, ...} or {"status": "refused", "reason": one sentence}. A refusal leaves the
report untouched: evalreel refuses to render a refused reel.
"""
from __future__ import annotations

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
    return " ".join(w for w in s.split() if not w.isdigit()) or None


def joined(xs: list[str]) -> str:
    return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


def _position(v):
    m = re.search(r"Position=([-\d.]+),([-\d.]+),([-\d.]+)", str(v or ""))
    return tuple(float(x) for x in m.groups()) if m else None


def _sha(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()


def plan_reel(pres: dict, names: dict | None, masters: dict | None, policy: dict, words: dict,
              patch_search: dict | None, mod_names: dict | None = None, capture_results: list | None = None) -> dict:
    try:
        return _plan(pres, names or {}, masters or {}, policy, words, patch_search, mod_names or {}, capture_results or [])
    except Refused as e:
        return {"schemaVersion": SCHEMA, "status": "refused", "reason": str(e), "chapters": []}


def _plan(pres, names, masters, policy, words, patch_search, mod_names, capture_results):
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

    def base_of(fk):
        return bases.get((refs.get(str(fk)) or {}).get("base")) or {}

    def spoken_ref(fk, summary=None):
        eid = base_of(fk).get("editorId")
        if eid:
            return (objects.get(eid) or {}).get("name") or humanize(eid)
        m = re.match(r"^\w+ (\S+?):", summary or "")
        return humanize(m.group(1)) if m else "a record"

    def place_of(summary):
        m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", summary or "") or re.search(r"^Cell ([A-Za-z0-9_]+):", summary or "")
        cell = m.group(1) if m else None
        return (places.get(cell) or {}).get("name") or humanize(cell) or "this place"

    rows = [r for r in pred.get("record") or [] if r.get("admission") == "admitted"]
    no_effect = set(policy.get("noInGameEffectFields") or [])
    kept_rows = [r for r in rows if r.get("field") not in no_effect]
    left_out = len([r for r in rows if r.get("field") in no_effect])
    clashes = {}
    for r in kept_rows:
        t = (policy.get("types") or {}).get(r.get("checkId"))
        if t is None:
            continue
        key = (t, r["formKey"]) if t in ("record", "cell") else (t, r["formKey"], tuple(r.get("other") or []))
        clashes.setdefault(key, {"type": t, "rows": [],
                                 "pair": tuple(sorted({r["A"], r["B"]} - {target})) + (target,)})["rows"].append(r)
    effect_re = re.compile(policy["effectModelPattern"], re.I) if policy.get("effectModelPattern") else None

    def still_reason(c):
        """None for a clash the reel can picture; else the skill's reason it is shown as data."""
        if c["type"] in (policy.get("pictureTypes") or []):
            r = c["rows"][0]
            if effect_re and any(effect_re.search(str(base_of(fk).get("model") or "")) for fk in [r["formKey"], *(r.get("other") or [])]):
                return "effect"
            return None
        return c["type"] if c["type"] in (policy.get("stillReasons") or {}) else "record"

    selected = []
    for t in policy.get("typeOrder") or []:
        cs = [c for c in clashes.values() if c["type"] == t]
        if not cs:
            continue
        pairs: dict = {}
        for c in cs:
            pairs.setdefault(c["pair"], []).append(c)
        pair_order = sorted(pairs, key=lambda p: (-len(pairs[p]), p))
        for p in pair_order:
            pairs[p].sort(key=lambda c: (still_reason(c) is not None, c["rows"][0]["formKey"]))
        if len(cs) <= policy["allUpTo"]:
            chosen = [c for p in pair_order for c in pairs[p]]
        else:
            chosen, i = [], 0
            while len(chosen) < policy["perType"] and any(pairs.values()):
                p = pair_order[i % len(pair_order)]
                if pairs[p]:
                    chosen.append(pairs[p].pop(0))
                i += 1
        selected += chosen
    selected = selected[:policy["maxVignettes"]]

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

    def own_patches(c):
        other = [m for m in c["pair"] if m != target][0]
        return [p for p in supplied if {other.lower(), target.lower()} <= set(masters.get(p.lower(), set()))]

    def patch_sentence(c):
        other = [m for m in c["pair"] if m != target][0]
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
            return (words["patch"][key].format(patch=patch_name(own[0]), kept=kept),
                    {**evidence, "case": "own-" + key, "patch": own[0], "masters": sorted(masters.get(own[0].lower(), set()))})
        lead = words["patch"]["leaves"].format(patch=patch_name(own[0])) + " " if own else ""
        ev = {**evidence, "patch": own[0] if own else None,
              "masters": sorted(masters.get(own[0].lower(), set())) if own else None}
        if hits:
            return lead + words["patch"]["listed"].format(file=hits[0].get("file")), {**ev, "case": "listed", "hit": hits[0]}
        # "No patch exists" only on a search that found its own known answer and read every source of this pair
        known = (patch_search or {}).get("knownAnswer") or {}
        unread = entry.get("unreadSources") or []
        if known.get("found") is True and not unread:
            return lead + words["patch"]["noneOther" if own else "none"], {**ev, "case": "none-searched"}
        return (lead + words["patch"]["notFoundOther" if own else "notFound"],
                {**ev, "case": "not-found", "knownAnswerFound": known.get("found"), "unreadSources": unread})

    def owner(fk, r):
        pair = [r["A"], r["B"]]
        made = str(fk).split(":", 1)[-1].lower()
        hit = [m for m in pair if m.lower() == made]
        if hit:
            return hit[0]
        carried = [m for m in (refs.get(str(fk)) or {}).get("carriedBy") or [] if m in pair]
        return carried[-1] if carried else pair[0]

    def geometry_slots(c):
        r = c["rows"][0]
        o_fk = (r.get("other") or [None])[0]
        a_owner, o_owner = owner(r["formKey"], r), owner(o_fk, r)
        first = (r["formKey"], a_owner) if a_owner == target else (o_fk, o_owner)
        second = (o_fk, o_owner) if first[0] == r["formKey"] else (r["formKey"], a_owner)
        return {"place": place_of(r.get("summary")), "modA": mod(first[1]), "objectA": spoken_ref(first[0]),
                "modB": mod(second[1]), "objectB": spoken_ref(second[0])}

    def record_parts(c):
        """(template key, winner, loser, detail, object) per row of a record or cell clash."""
        out = []
        for r in c["rows"]:
            winner, loser = mod(r["B"]), mod(r["A"])
            if c["type"] == "cell":
                out.append(("cell", winner, loser, place_of(r.get("summary")), None))
            elif r.get("field") == "Placement":
                b = _position((r.get("values") or {}).get("winner"))
                base = base_of(r["formKey"])
                key = ("placementRemoved" if b and b[2] <= float(policy.get("disabledZ", -30000.0)) else
                       "placementNpc" if base.get("type") in (policy.get("actorTypes") or []) else "placement")
                out.append((key, winner, loser, None, spoken_ref(r["formKey"], r.get("summary"))))
            else:
                out.append(("field", winner, loser, (words.get("fieldLabels") or {}).get(r.get("field"), "settings"),
                            spoken_ref(r["formKey"], r.get("summary"))))
        return out

    def say(key, winner, loser, detail, objs):
        if key == "cell":
            return words["cell"].format(winner=winner, loser=loser, place=detail)
        if key == "field":
            return words["field"].format(winner=winner, loser=loser, field=detail, object=joined(objs))
        return words[key].format(winner=winner, loser=loser, object=joined(objs))

    def still_row(c):
        r = c["rows"][0]
        row = {"formKey": r["formKey"], "type": c["type"], "pair": [mod(m) for m in c["pair"]],
               "field": (words.get("fieldLabels") or {}).get(r.get("field"), r.get("field")) if r.get("field") else None,
               "object": spoken_ref(r["formKey"], r.get("summary")), "reason": (policy.get("stillReasons") or {}).get(still_reason(c) or c["type"])}
        if c["type"] in ("overlap", "contact", "state"):
            s = geometry_slots(c)
            row.update(object=f"{s['objectA']} ({s['modA']}) and {s['objectB']} ({s['modB']})", place=s["place"])
        elif c["type"] == "cell":
            row["place"] = place_of(r.get("summary"))
        if r.get("field") in (policy.get("showValuesFields") or []):
            row["values"] = {mod(r["A"]): (r.get("values") or {}).get("lost"), mod(r["B"]): (r.get("values") or {}).get("winner")}
        row["winner"] = mod(r["B"]) if c["type"] in ("record", "cell") else None
        return row

    refusals = {}
    for res in capture_results:
        if res.get("status") == "refused" and res.get("formKey"):
            refusals[str(res["formKey"])] = res
    game = {(m.get("subject") or {}).get("formKey") for m in pres.get("media") or []
            if m.get("assessed") and m.get("mediaKind") == "game-video"}
    with_arm = pred.get("predictionArm")
    without_arm = str(pred.get("withoutArm") or "").split(":", 1)[-1] or None
    arm_of = {"without": without_arm, "with": with_arm, "highlight": with_arm}

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
    order = next((a.get("locationPluginOrder") for a in (pres.get("scope") or {}).get("arms") or []
                  if a.get("armId") == with_arm), None) or []
    runtime_mods = sorted({o for m in pres.get("media") or [] for o in ((m.get("provenance") or {}).get("runtimeOverlayMods") or [])})
    opening_pages = [
        {"layout": "types", "title": words["stillTitles"]["types"].format(mod=mod(target)),
         "rows": [{"label": type_labels[t], "count": counts[t]["count"], "pairs": sorted(counts[t]["pairs"])}
                  for t in policy["typeOrder"] if t in counts],
         "leftOut": {"noInGameEffect": left_out,
                     "line": words["stillTitles"]["leftOut"].format(count=left_out, noun="record field" if left_out == 1 else "record fields")
                     if left_out else None}},
        {"layout": "load-order", "title": words["stillTitles"]["loadOrder"], "plugins": order, "targets": [target],
         "runtimeMods": runtime_mods, "runtimeTitle": words["stillTitles"]["runtime"] if runtime_mods else None}]
    chapters = [{"id": "R-01", "kind": "opening",
                 "narration": (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),
                 "still": {"pages": opening_pages}}]
    stills: dict = {}
    closing_extra = []
    for c in selected:
        reason = still_reason(c)
        fk = c["rows"][0]["formKey"]
        if reason is None and fk in refusals:
            closing_extra.append({"reason": "picture could not be captured: " + str(refusals[fk].get("reason")),
                                  "subjects": [geometry_slots(c)["objectA"] + " and " + geometry_slots(c)["objectB"]
                                               + " in " + geometry_slots(c)["place"]]})
            reason = "captureRefused"
        if reason is None:
            sentence = words["overlap"].format(**geometry_slots(c))
            patch, evidence = patch_sentence(c)
            text = sentence + " " + patch
            if any(r["formKey"] in game for r in c["rows"]):
                text += " " + words["playTime"]
            r = c["rows"][0]
            frames = []
            for state in policy["sequence"]:
                m = frame_media(c, state)
                frames.append({"state": state, "armId": arm_of[state], "mediaId": (m or {}).get("id")})
            chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "vignette", "narration": text,
                             "clash": {"type": c["type"], "checkId": r["checkId"], "formKey": r["formKey"],
                                       "other": list(r.get("other") or []), "pair": list(c["pair"]), "armId": with_arm,
                                       "withoutArmId": without_arm, "place": geometry_slots(c)["place"],
                                       "findingId": r.get("findingId")},
                             "frames": frames, "patch": evidence})
        else:
            if c["type"] in ("record", "cell"):
                parts = record_parts(c)
            else:
                parts = [("geometry", None, None, None, (words["state" if c["type"] == "state" else "overlap"]).format(**geometry_slots(c)))]
            patch, evidence = patch_sentence(c)
            stills.setdefault(c["type"], []).append((c, parts, patch, evidence))
    for t in policy["typeOrder"]:
        if t not in stills:
            continue
        groups: dict = {}
        for c, parts, patch, evidence in stills[t]:
            groups.setdefault((c["pair"], patch), {"parts": [], "evidence": evidence, "clashes": []})
            groups[(c["pair"], patch)]["parts"] += parts
            groups[(c["pair"], patch)]["clashes"].append(c)
        said = []
        for (_pair, patch), g in groups.items():
            merged: dict = {}
            for key, winner, loser, detail, obj in g["parts"]:
                if key == "geometry":
                    merged.setdefault((key, obj), [])
                else:
                    merged.setdefault((key, winner, loser, detail), []).append(obj)
            for k, objs in merged.items():
                said.append(k[1] if k[0] == "geometry" else say(k[0], k[1], k[2], k[3], [o for o in objs if o] or ["this"]))
            said.append(patch)
        chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "data-still", "type": t, "narration": " ".join(said),
                         "clash": {"type": t},
                         "still": {"pages": [{"layout": "rows", "title": type_labels[t],
                                              "rows": [still_row(c) for c, _, _, _ in stills[t]]}]},
                         "patches": [g["evidence"] for g in groups.values()]})
    checks = [{"reason": m.get("reason"), "subjects": list(m.get("subjects") or [])} for m in reader.get("missingSummary") or []]
    chapters.append({"id": f"R-{len(chapters) + 1:02d}", "kind": "closing", "narration": words["closing"],
                     "still": {"pages": [{"layout": "checks", "title": words["stillTitles"]["checks"],
                                          "rows": checks + closing_extra}]}})
    return {"schemaVersion": SCHEMA, "status": "planned", "reason": None,
            "policySha256": _sha(policy), "wordsSha256": _sha(words),
            "patchSearchSha256": _sha(patch_search) if patch_search is not None else None,
            "modNames": {k: v for k, v in mod_names.items()},
            "sequence": list(policy["sequence"]), "holdSeconds": dict(policy.get("holdSeconds") or {}),
            "chapters": chapters}
