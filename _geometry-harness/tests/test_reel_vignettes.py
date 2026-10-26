"""The review reel as clash vignettes: Piranha's six points (task 2026-10-19-review-reel-vignettes) and the Gate A
review's F1-F4, checked on GENERATED output.

    python _geometry-harness/tests/test_reel_vignettes.py                      all cases (planner, capture plan, render, data)
    python _geometry-harness/tests/test_reel_vignettes.py --presentation FILE  the rule checks on a compiled presentation's reel
    python _geometry-harness/tests/test_reel_vignettes.py --reference          the rule and oracle checks on the hand-built
                                                                               expected reel (shows the checks can pass)
    python _geometry-harness/tests/test_reel_vignettes.py --sabotage NAME      break one rule in the checked reel; the named
                                                                               check must fail on its own assertion

The reel a presentation renders is `presentation["reel"]["chapters"]` when the planner wrote one, else the old `chapters`
(what the base renders). The checks read that reel, so on the base they fail on their assertions, not on a missing name.
The expected values come from an independent oracle in this file (selection, spoken names, sentences), never from the
planner. Words and selection numbers are this file's own copies of the proposed skill data, so a change to the shipped
skill data does not change what these cases expect; the data case checks the shipped files separately.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

os.environ["PYTHONUTF8"] = "1"
HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
ROOT = HARNESS.parent
SKILL = ROOT / ".claude/skills/evaluation-reporting"
sys.path.insert(0, str(HARNESS))
os.environ["HOUSECARL_EVAL_SETTINGS"] = str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json")

RESULTS: list[tuple[str, bool, str]] = []          # (check, ok, message)


def check(name: str, ok: bool, msg: str) -> bool:
    print(("  PASS  " if ok else "  FAIL  ") + f"[{name}] {msg}")
    RESULTS.append((name, bool(ok), msg))
    return bool(ok)


# ------------------------------------------------------------------------------------------ the proposed skill data
POLICY = {
    "allUpTo": 3, "perType": 3, "maxVignettes": 24,
    "typeOrder": ["overlap", "contact", "record", "cell", "state", "navmesh"],
    "types": {"geometry.object-overlap": "overlap", "geometry.object-contact": "contact",
              "geometry.fixture-empty-space": "contact", "records.contested-fields": "record",
              "records.cell-attributes": "cell", "state.conditional-geometry": "state",
              "geometry.navmesh-ownership": "navmesh"},
    "pictureTypes": ["overlap", "contact"],
    "effectModelPattern": r"^(?:effects|sky)[\\/]",          # the report's own pattern (report-priorities.json)
    "stillReasons": {"effect": "effect", "record": "record", "cell": "cell", "state": "state", "navmesh": "navmesh"},
    "actorTypes": ["Npc"], "disabledZ": -30000.0, "showValuesFields": ["Description", "Name"],
    "patchSearch": {"requiredTools": ["housecarl_nexus_mod", "housecarl_nexus_file_search"]},
    "noInGameEffectFields": ["EditorID"],
    "sequence": ["without", "with", "highlight"],
    "holdSeconds": {"without": 2.5, "with": 2.5, "highlightMin": 4.0, "stillPause": 1.5, "loadOrderPage": 6.0},
}
WORDS = {
    "opening": "Here are the types of clashes that are predicted from adding {mod}.",
    "openingNone": "No clashes between mods are predicted from adding {mod}.",
    "closing": "These are the checks that could not run or could not decide.",
    "typeLabels": {"overlap": "Objects in the same space", "contact": "Objects touching or blocking",
                   "record": "Record clashes", "cell": "Cell setting clashes",
                   "state": "Objects that clash when a quest or switch changes the world", "navmesh": "Navmesh clashes"},
    "overlap": "In {place}, {modA}'s {objectA} and {modB}'s {objectB} occupy the same space.",
    "placement": "{winner} moves {object} that {loser} places.",
    "placementRemoved": "{winner} moves {object} that {loser} places out of the world.",
    "placementNpc": "{loser} and {winner} place {object} in different spots.",
    "state": "When a quest or switch changes {place}, {modA}'s {objectA} and {modB}'s {objectB} occupy the same space.",
    "field": "{winner} replaces {loser}'s {field} of {object}.",
    "cell": "{winner} replaces {loser}'s settings for {place}.",
    "fieldLabels": {"Description": "description", "Conditions": "conditions", "Name": "name", "Placement": "position",
                    "OcclusionData": "occlusion settings"},
    "patchName": "{plugin} from {mod}",
    "stillTitles": {"types": "Clashes predicted from adding {mod}", "loadOrder": "Plugins analysed, in load order",
                    "checks": "Checks that could not run or could not decide",
                    "leftOut": "Not shown: {count} {noun} with no effect in play", "runtime": "Mods changing these scenes at run time"},
    "patch": {"fixes": "Get {patch}; it fixes this.", "keeps": "Get {patch}; it keeps {kept}'s version.",
              "own": "Get {patch}; it sets its own version.", "leaves": "{patch} does not fix this one.",
              "listed": "A patch for these two mods is listed on Nexus: {file}.",
              "none": "No patch exists for these two mods.", "noneOther": "No other patch exists for these two mods.",
              "notFound": "No patch for these two mods was found.", "notFoundOther": "No other patch for these two mods was found."},
    "playTime": "This was also recorded in play.",
    "forbidden": ["compared", "candidate", "offline", "render", "gameplay", "not tested", "testing is incomplete",
                  "OcclusionData", "EditorID", ".esp", ".esm", ".esl"],
    "forbiddenPatterns": [r"\b(geometry|records|state|environment|functional|patch|runtime)\.[a-z-]+\b",
                          r"\b[A-Z][A-Z-]{3,}\b"],                # check ids; detector codes such as LOST-EDIT, OVERLAP
}
DISCLAIMER_WORDS = ["offline", "not gameplay", "not tested", "testing is incomplete", "no play-time", "was not compared"]


# ------------------------------------------------------------------------------------------ the synthetic evaluation
def synthetic(play_time: bool = False, sentinel: bool = False, patch_search: bool = True, known_found: bool = True):
    """A presentation-shaped evaluation (the fields the real one carries, checked against Pilgrim's), its names
    enrichment, the plugins' masters and a recorded patch search."""
    S = "SENTINEL-REPORT-SENTENCE"
    def ov(n, other, cell, wp, b="Alpha.esp"):
        return {"checkId": "geometry.object-overlap", "kind": "OVERLAP", "A": "Target.esp", "B": b,
                "formKey": f"0001{n:02d}:Target.esp", "other": [other], "admission": "admitted", "findingId": f"F-O{n}",
                "physical": "Holds", "withPatch": wp,
                "summary": f"Static from {b} / Static from Target.esp: crossing, containment unassessed, 9 triangle pairs "
                           f"in {cell} (int) [mm] 0001{n:02d}:Target.esp / {other}"}
    def rec(fk, a, field, lost, winner, wp, summary, check="records.contested-fields"):
        return {"checkId": check, "kind": "LOST-EDIT", "A": a, "B": "Target.esp", "formKey": fk, "field": field,
                "values": {"lost": lost, "winner": winner}, "admission": "admitted", "findingId": "F-" + fk[:6],
                "withPatch": wp, "summary": summary}
    record = [ov(1, "000201:Alpha.esp", "CellOne", "resolved"), ov(2, "000202:Alpha.esp", "CellOne", "resolved"),
              ov(3, "000203:Alpha.esp", "CellTwo", "resolved"), ov(4, "000204:Alpha.esp", "CellTwo", "resolved"),
              ov(5, "000301:Beta.esp", "CellThree", "left", b="Beta.esp"),
              rec("000401:Skyrim.esm", "Alpha.esp", "Placement", "Position=0,0,0; Rotation=0,0,0",
                  "Position=200,0,0; Rotation=0,0,0", "kept", "PlacedObject MAG_StoneBench02: Placement - Alpha.esp's value lost to Target.esp's"),
              rec("000402:Skyrim.esm", "Beta.esp", "Description", "Beta words.", "Target words.", "left",
                  "MagicEffect FortifyThingFFSelf: Description - Beta.esp's value lost to Target.esp's"),
              rec("000403:Skyrim.esm", "Beta.esp", "Placement", "Position=0,0,0; Rotation=0,0,0",
                  "Position=10,0,0; Rotation=0,0,0", "left", "PlacedObject SomeLamp01: Placement - Beta.esp's value lost to Target.esp's"),
              rec("000404:Skyrim.esm", "Beta.esp", "EditorID", "BetaName", "TargetName", "left",
                  "PlacedObject BetaName: EditorID - Beta.esp's value lost to Target.esp's"),
              rec("000501:Skyrim.esm", "Beta.esp", "OcclusionData", "00ff", "ff00", "left",
                  "Cell CellFour: OcclusionData - Beta.esp's value lost to Target.esp's", check="records.cell-attributes"),
              {"checkId": "geometry.object-overlap", "kind": "OVERLAP", "A": "Target.esp", "B": "Target.esp",
               "formKey": "000901:Target.esp", "admission": "sameMod", "summary": "same mod"}]
    verdict_counts = [
        {"checkId": "geometry.object-overlap", "kind": "OVERLAP", "count": 5, "unit": "relationships", "fieldNames": {}},
        {"checkId": "records.contested-fields", "kind": "LOST-EDIT", "count": 4, "unit": "objects",
         "fieldNames": {"Placement": 2, "Description": 1, "EditorID": 1}},
        {"checkId": "records.cell-attributes", "kind": "LOST-EDIT", "count": 1, "unit": "objects", "fieldNames": {"OcclusionData": 1}}]
    order = ["Skyrim.esm", "Update.esm", "Alpha.esp", "Beta.esp", "Target.esp", "Alpha - Target patch.esp"]
    pres = {
        "schemaVersion": 1, "presentationId": "synthetic", "source": {"bundleDir": "."},
        "claimSentence": (S if sentinel else "The evaluation is incomplete."),
        "reader": {
            "verdict": (S if sentinel else "Adding Target.esp will introduce these clashes between mods unless a patch resolves them."),
            "scopeLine": (S if sentinel else "This review compares Target.esp within the measured load order."),
            "limitations": [S] if sentinel else ["Testing is incomplete; outstanding areas are listed below.",
                                                 "No matching play-time evidence was supplied, so this does not show what happened during play."],
            "suppliedPatches": ["Alpha - Target patch.esp"],
            "missingSummary": [{"reason": "ran, but could not decide", "subjects": ["Whether a single object blocks an NPC route — Measured load order"]},
                               {"reason": "cannot yet be measured by this evaluator", "subjects": ["Whether several changes together disconnect an NPC route — Measured load order"]}],
            "prediction": {"subject": "Target.esp", "mods": ["Target.esp"], "predictionArm": "with-target",
                           "withoutArm": "sibling:without-target", "verdictCounts": verdict_counts, "record": record,
                           "items": []},
        },
        "scope": {"arms": [{"armId": "with-target", "locationPluginOrder": order},
                           {"armId": "baseline", "locationPluginOrder": order[:-1]}]},
        "findings": [{"id": "F-O1", "kind": "finding", "readerPlain": S if sentinel else "4 overlap pairs between Alpha.esp and Target.esp."}],
        "media": [], "chapters": [],
    }
    if play_time:
        pres["media"].append({"id": "M-game", "assessed": True, "mediaKind": "game-video",
                              "subject": {"checkId": "geometry.object-overlap", "armId": "with-target", "formKey": "000101:Target.esp"}})
    names = {"refs": {"000101:Target.esp": {"base": "B1", "carriedBy": ["Target.esp"]},
                      "000201:Alpha.esp": {"base": "B2", "carriedBy": ["Alpha.esp"]},
                      "000102:Target.esp": {"base": "B1", "carriedBy": ["Target.esp"]},
                      "000202:Alpha.esp": {"base": "B2", "carriedBy": ["Alpha.esp"]},
                      "000105:Target.esp": {"base": "B1", "carriedBy": ["Target.esp"]},
                      "000301:Beta.esp": {"base": "B3", "carriedBy": ["Beta.esp"]},
                      "000401:Skyrim.esm": {"base": "B4", "carriedBy": ["Skyrim.esm", "Alpha.esp", "Target.esp"]},
                      "000403:Skyrim.esm": {"base": "B5", "carriedBy": ["Skyrim.esm", "Beta.esp", "Target.esp"]}},
             "bases": {"B1": {"type": "Activator", "editorId": "Target_Altar01"}, "B2": {"type": "Door", "editorId": "AlphaDoor01"},
                       "B3": {"type": "Static", "editorId": "BetaPillar01"}, "B4": {"type": "Static", "editorId": "MAG_StoneBench02"},
                       "B5": {"type": "Static", "editorId": "SomeLamp01"}},
             "objects": {"Target_Altar01": {"name": "Altar"}, "SomeLamp01": {"name": "Lamp"}},
             "places": {"CellOne": {"name": "The First Hall"}}}
    masters = {"alpha - target patch.esp": {"skyrim.esm", "alpha.esp", "target.esp"}, "target.esp": {"skyrim.esm"},
               "alpha.esp": {"skyrim.esm"}, "beta.esp": {"skyrim.esm"}}
    search = None
    if patch_search:
        search = {"schemaVersion": 1, "tool": "housecarl_nexus_search + housecarl_nexus_file_search",
                  "pairs": [{"pair": [x, "Target.esp"], "hits": [],
                             "queries": [{"tool": t, "query": f"{x} {t}", "responseSha256": "0" * 64, "date": "2026-10-26"}
                                         for t in POLICY["patchSearch"]["requiredTools"]]} for x in ("Alpha.esp", "Beta.esp")],
                  "knownAnswer": {"pair": ["Alpha.esp", "Target.esp"], "patch": "Alpha - Target patch.esp",
                                  "hits": [{"file": "Alpha - Target patch", "page": "Alpha Patches"}] if known_found else []}}
    mods = {"Target.esp": {"name": "Target Mod", "source": "mo2-folder"}, "Alpha.esp": {"name": "Alpha Mod", "source": "mo2-folder"},
            "Beta.esp": {"name": "Beta Mod", "source": "mo2-folder"},
            "Alpha - Target patch.esp": {"name": "Alpha Patches", "source": "mo2-folder"}}
    return pres, names, masters, search, mods


# ------------------------------------------------------------------------------------------ the independent oracle
def humanize(eid: str | None) -> str | None:
    if not eid:
        return None
    s = re.sub(r"^[A-Za-z0-9]{1,5}_", "", eid)
    s = s.replace("_", " ")
    s = re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])", " ", s)
    return " ".join(w for w in s.split() if not w.isdigit())


def mod_name(mods, plugin):
    return (mods.get(plugin) or {}).get("name")


def patch_display(mods, plugin, words=None):
    stem = re.sub(r"\.(esp|esm|esl)$", "", plugin, flags=re.I)
    folder = mod_name(mods, plugin)
    return stem if folder.lower() == stem.lower() else (words or WORDS)["patchName"].format(plugin=stem, mod=folder)


def spoken_ref(names, fk, summary=None):
    ref = (names.get("refs") or {}).get(fk) or {}
    base = (names.get("bases") or {}).get(ref.get("base")) or {}
    eid = base.get("editorId")
    if eid:
        return ((names.get("objects") or {}).get(eid) or {}).get("name") or humanize(eid)
    m = re.match(r"^\w+ (\S+?):", summary or "")
    return humanize(m.group(1)) if m else "record"


def place_of(names, summary):
    m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", summary) or re.search(r"^Cell ([A-Za-z0-9_]+):", summary)
    cell = m.group(1) if m else None
    return ((names.get("places") or {}).get(cell) or {}).get("name") or humanize(cell)


def position(v):
    m = re.search(r"Position=([-\d.]+),([-\d.]+),([-\d.]+)", v or "")
    return tuple(float(x) for x in m.groups()) if m else None


def joined(xs):
    return xs[0] if len(xs) == 1 else ", ".join(xs[:-1]) + " and " + xs[-1]


def oracle(pres, names, masters, search, mods, policy=POLICY, words=WORDS):
    """The expected reel for an evaluation: selection, kinds, sentences. Written from the Gate B rules (with the Gate B
    review's S1 and S2), not from the planner."""
    pred = pres["reader"]["prediction"]
    target = pred["subject"]
    rows = [r for r in pred["record"] if r.get("admission") == "admitted"]
    no_effect = set(policy["noInGameEffectFields"])
    kept_rows = [r for r in rows if r.get("field") not in no_effect]
    left_out = len([r for r in rows if r.get("field") in no_effect])        # record fields left out, on screen only
    clashes = {}                                           # one clash per record (record types) or per row (geometry)
    for r in kept_rows:
        t = policy["types"][r["checkId"]]
        key = (t, r["formKey"]) if t in ("record", "cell") else (t, r["formKey"], tuple(r.get("other") or []))
        clashes.setdefault(key, {"type": t, "rows": [], "pair": tuple(sorted({r["A"], r["B"]} - {target})) + (target,)})["rows"].append(r)

    def base(fk):
        return (names.get("bases") or {}).get(((names.get("refs") or {}).get(fk) or {}).get("base")) or {}

    def picture(c):                                        # S1: only object pairs without a visual effect are pictured
        if c["type"] not in policy["pictureTypes"]:
            return False
        r = c["rows"][0]
        return not any(re.search(policy["effectModelPattern"], str(base(fk).get("model") or ""), re.I)
                       for fk in [r["formKey"], *(r.get("other") or [])])
    selected = []
    for t in policy["typeOrder"]:
        cs = [c for c in clashes.values() if c["type"] == t]
        if not cs:
            continue
        pairs = {}
        for c in cs:
            pairs.setdefault(c["pair"], []).append(c)
        pair_order = sorted(pairs, key=lambda p: (-len(pairs[p]), p))
        for p in pair_order:
            pairs[p].sort(key=lambda c: (not picture(c), c["rows"][0]["formKey"]))
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

    def patch_sentence(c):
        other = [m for m in c["pair"] if m != target][0]
        own = [p for p in pres["reader"]["suppliedPatches"]
               if {other.lower(), target.lower()} <= set(masters.get(p.lower(), set()))]
        wp = c["rows"][0].get("withPatch")
        entry = next((x for x in (search or {}).get("pairs", []) if set(x["pair"]) == {other, target}), {})
        hits = entry.get("hits") or []
        if own and wp in ("resolved", "kept", "switched", "own"):
            r = c["rows"][0]
            kept = mod_name(mods, r["B"] if wp == "kept" else r["A"])
            key = {"resolved": "fixes", "kept": "keeps", "switched": "keeps", "own": "own"}[wp]
            return words["patch"][key].format(patch=patch_display(mods, own[0], words), kept=kept)
        lead = words["patch"]["leaves"].format(patch=patch_display(mods, own[0], words)) + " " if own else ""
        if hits:
            return lead + words["patch"]["listed"].format(file=hits[0]["file"])
        known = (search or {}).get("knownAnswer") or {}
        want = re.sub(r"\.(esp|esm|esl)$", "", str(known.get("patch") or ""), flags=re.I).lower()
        found = bool(want) and any(want in json.dumps(h).lower() for h in known.get("hits") or [])
        if found and not entry.get("unreadSources"):
            return lead + words["patch"]["noneOther" if own else "none"]
        return lead + words["patch"]["notFoundOther" if own else "notFound"]

    def owner(fk, r):
        """The pair's plugin a reference belongs to: the plugin that made it, else its last carrier in the pair."""
        pair = [r["A"], r["B"]]
        made = str(fk).split(":", 1)[-1].lower()
        hit = [m for m in pair if m.lower() == made]
        if hit:
            return hit[0]
        carried = [m for m in ((names.get("refs") or {}).get(fk) or {}).get("carriedBy") or [] if m in pair]
        return carried[-1] if carried else pair[0]

    def geometry(c):
        r = c["rows"][0]
        o_fk = (r.get("other") or [None])[0]
        a_owner, o_owner = owner(r["formKey"], r), owner(o_fk, r)
        first = (r["formKey"], a_owner) if a_owner == target else (o_fk, o_owner)
        second = (o_fk, o_owner) if first[0] == r["formKey"] else (r["formKey"], a_owner)
        return words["state" if c["type"] == "state" else "overlap"].format(
            place=place_of(names, r["summary"]), modA=mod_name(mods, first[1]), objectA=spoken_ref(names, first[0]),
            modB=mod_name(mods, second[1]), objectB=spoken_ref(names, second[0]))

    def record_parts(c):
        out = []
        for r in c["rows"]:
            winner, loser = mod_name(mods, r["B"]), mod_name(mods, r["A"])
            if c["type"] == "cell":
                out.append(("cell", winner, loser, place_of(names, r["summary"]), None))
            elif r.get("field") == "Placement":
                b = position((r.get("values") or {}).get("winner"))
                key = ("placementRemoved" if b and b[2] <= policy["disabledZ"] else
                       "placementNpc" if base(r["formKey"]).get("type") in policy["actorTypes"] else "placement")
                out.append((key, winner, loser, None, spoken_ref(names, r["formKey"], r["summary"])))
            else:
                out.append(("field", winner, loser, words["fieldLabels"].get(r["field"], "settings"),
                            spoken_ref(names, r["formKey"], r["summary"])))
        return out

    game = {(m.get("subject") or {}).get("formKey") for m in pres.get("media") or []
            if m.get("assessed") and m.get("mediaKind") == "game-video"}
    chapters = [{"kind": "opening", "narration": words["opening"].format(mod=mod_name(mods, target)),
                 "types": {words["typeLabels"][t]: len([c for c in clashes.values() if c["type"] == t]) for t in policy["typeOrder"]
                           if any(c["type"] == t for c in clashes.values())},
                 "leftOut": left_out,
                 "loadOrder": next(a["locationPluginOrder"] for a in pres["scope"]["arms"] if a["armId"] == pred["predictionArm"])}]
    stills = {}
    for c in selected:
        if picture(c):
            text = geometry(c) + " " + patch_sentence(c)
            if any(r["formKey"] in game for r in c["rows"]):
                text += " " + words["playTime"]
            chapters.append({"kind": "vignette", "type": c["type"], "formKey": c["rows"][0]["formKey"],
                             "pair": list(c["pair"]), "narration": text})
        else:
            parts = record_parts(c) if c["type"] in ("record", "cell") else [("geometry", None, None, None, geometry(c))]
            stills.setdefault(c["type"], []).append((c, parts, patch_sentence(c)))
    for t in policy["typeOrder"]:
        if t not in stills:
            continue
        groups = {}                                        # per pair and patch sentence; one sentence per template
        for c, parts, patch in stills[t]:
            groups.setdefault((c["pair"], patch), []).extend(parts)
        said = []
        for (_pair, patch), parts in groups.items():
            merged = {}
            for key, winner, loser, detail, obj in parts:
                merged.setdefault((key, obj) if key == "geometry" else (key, winner, loser, detail), []).append(obj)
            for k, objs in merged.items():
                if k[0] == "geometry":
                    said.append(k[1])
                elif k[0] == "cell":
                    said.append(words["cell"].format(winner=k[1], loser=k[2], place=k[3]))
                elif k[0] == "field":
                    said.append(words["field"].format(winner=k[1], loser=k[2], field=k[3], object=joined(objs)))
                else:
                    said.append(words[k[0]].format(winner=k[1], loser=k[2], object=joined(objs)))
            said.append(patch)
        chapters.append({"kind": "data-still", "type": t, "formKeys": [c["rows"][0]["formKey"] for c, _, _ in stills[t]],
                         "narration": " ".join(said)})
    chapters.append({"kind": "closing", "narration": words["closing"],
                     "checks": [(m["reason"], m["subjects"]) for m in pres["reader"]["missingSummary"]]})
    return chapters


# ------------------------------------------------------------------------------------------ reading a reel
def reel_view(pres: dict) -> list[dict]:
    """What a presentation's reel says, in one shape: the planner's `reel.chapters`, else the base's `chapters`."""
    reel = pres.get("reel") or {}
    if reel.get("chapters"):
        out = []
        for ch in reel["chapters"]:
            pages = ((ch.get("still") or {}).get("pages")) or []
            out.append({"kind": ch.get("kind"), "narration": " ".join(str(ch.get("narration") or "").split()),
                        "pages": pages, "frames": ch.get("frames") or [], "clash": ch.get("clash") or {},
                        "patch": ch.get("patch") or {}, "patches": ch.get("patches") or [],
                        "rows": [r for p in pages for r in p.get("rows") or []]})
        return out
    out = []
    for ch in pres.get("chapters") or []:
        for cap in ch.get("captions") or []:
            out.append({"kind": {"finding": "clash", "comparison": "clash"}.get(ch.get("kind"), ch.get("kind")),
                        "narration": " ".join(str(cap.get("narration") or "").split()), "pages": [], "frames": [],
                        "clash": {}, "patch": {}, "patches": [], "rows": []})
    return out


def report_sentences(pres):
    r = pres.get("reader") or {}
    xs = [r.get("verdict"), r.get("scopeLine"), pres.get("claimSentence")] + list(r.get("limitations") or [])
    xs += [f.get("readerPlain") for f in pres.get("findings") or []] + [f.get("readerProvenance") for f in pres.get("findings") or []]
    return [" ".join(str(x).split()) for x in xs if x and len(str(x)) >= 20]


# ------------------------------------------------------------------------------------------ the rule checks
def rule_checks(pres: dict, view: list[dict], words=WORDS, policy=POLICY, tag="", mods=None):
    pred = (pres.get("reader") or {}).get("prediction") or {}
    target = pred.get("subject") or ""
    mods = mods if mods is not None else ((pres.get("reel") or {}).get("modNames") or {})
    narr = [c["narration"] for c in view]
    clash_scenes = [c for c in view if c["kind"] in ("vignette", "data-still", "clash")]
    # 1 opening still
    op = view[0] if view else {}
    layouts = [p.get("layout") for p in op.get("pages") or []]
    order = next((a.get("locationPluginOrder") for a in (pres.get("scope") or {}).get("arms") or []
                  if a.get("armId") == pred.get("predictionArm")), None)
    lo = next((p for p in op.get("pages") or [] if p.get("layout") == "load-order"), {})
    check("1-opening", op.get("kind") == "opening" and layouts[:2] == ["types", "load-order"]
          and bool(mod_name(mods, target)) and op.get("narration") == words["opening"].format(mod=mod_name(mods, target))
          and lo.get("plugins") == order,
          f"{tag}the opening is a two-page still (clash types, then the load order) with only the fixed sentence "
          f"(narration {op.get('narration', '')[:90]!r}, pages {layouts})")
    # 2 plain wording: no report sentence, no internal term
    leaked = sorted({s[:60] for s in report_sentences(pres) for n in narr if s in n})
    bad = sorted({w for w in words["forbidden"] for n in narr if w.lower() in n.lower()}
                 | {m.group(0) for pat in words["forbiddenPatterns"] for n in narr for m in re.finditer(pat, n)})
    check("2-wording", any(narr) and not leaked and not bad,
          f"{tag}no report sentence and no internal term is spoken (narrated scenes {sum(1 for n in narr if n)}; an empty reel has "
          f"nothing to check and fails; report sentences {leaked[:3]}, terms {bad})")
    # 3 vignettes: without -> with -> highlight, one narration each
    vig = [c for c in view if c["kind"] == "vignette"]
    shapes = [[f.get("state") for f in c["frames"]] for c in vig]
    check("3-vignettes", bool(vig) and all(s == policy["sequence"] for s in shapes)
          and all(len({f.get("mediaId") for f in c["frames"] if f.get("mediaId")}) == len([f for f in c["frames"] if f.get("mediaId")]) for c in vig),
          f"{tag}every vignette shows {policy['sequence']} once, with one narration ({len(vig)} vignettes, shapes {shapes[:3]})")
    # 4 patches: every clash scene ends with a patch sentence; a named patch is that pair's patch
    pat = [re.escape(v).replace(r"\{patch\}", ".+?").replace(r"\{kept\}", ".+?").replace(r"\{file\}", ".+?") for v in words["patch"].values()]
    no_patch = [c["narration"][:60] for c in clash_scenes if not any(re.search(p, c["narration"]) for p in pat)]
    foreign = []
    for c in vig + [c for c in view if c["kind"] == "data-still"]:
        for p in [c.get("patch") or {}] + list(c.get("patches") or []):
            if p.get("patch") and not {m.lower() for m in p.get("pair") or []} <= {m.lower() for m in p.get("masters") or []}:
                foreign.append(c["narration"][:60])
    check("4-patches", bool(clash_scenes) and not no_patch and not foreign and not any("compared" in n.lower() for n in narr),
          f"{tag}every clash scene says which patch to get or that none exists, a named patch masters both plugins of "
          f"its pair, and nothing says 'compared' (missing {no_patch[:2]}, foreign {foreign[:2]})")
    # 5 no disclaimers; play-time only where supplied
    limits = [" ".join(str(x).split()) for x in (pres.get("reader") or {}).get("limitations") or []]
    disc = sorted({w for w in DISCLAIMER_WORDS + limits for n in narr if w and w.lower() in n.lower()})
    game = any(m.get("assessed") and m.get("mediaKind") == "game-video" for m in pres.get("media") or [])
    play = [c for c in view if words["playTime"] in c["narration"]]
    check("5-disclaimers", any(narr) and not disc and (game or not play),
          f"{tag}no disclaimer or limitation is spoken and play-time is mentioned only with play-time media (narrated scenes "
          f"{sum(1 for n in narr if n)}; an empty reel fails; {disc[:3]})")
    # 6 closing still
    cl = view[-1] if view else {}
    want = [(m.get("reason"), list(m.get("subjects") or [])) for m in (pres.get("reader") or {}).get("missingSummary") or []]
    got = [(r.get("reason"), list(r.get("subjects") or [])) for p in cl.get("pages") or [] if p.get("layout") == "checks"
           for r in p.get("rows") or []]
    check("6-closing", cl.get("kind") == "closing" and got[:len(want)] == want and cl.get("narration") == words["closing"],
          f"{tag}the closing is a still listing each check that could not run or decide with its reason, and says only "
          f"the fixed sentence (narration {cl.get('narration', '')[:90]!r}, {len(got)} of {len(want)} rows)")
    # F2 no in-game effect
    no_eff = set(policy["noInGameEffectFields"])
    shown = [r for c in view for r in c["rows"] if r.get("field") in no_eff]
    spoken = [c["narration"][:60] for c in view if any(f in c["narration"] for f in no_eff)]
    rows = [r for r in pred.get("record") or [] if r.get("admission") == "admitted"]
    left = len([r for r in rows if r.get("field") in no_eff])
    # the counted clashes' own field tally (verdictCounts.fieldNames) must agree with the admitted rows, so the expected
    # number cannot be 0 on a presentation whose counted clashes include a no-effect field
    counted = sum(int(n) for v in pred.get("verdictCounts") or [] for f, n in (v.get("fieldNames") or {}).items() if f in no_eff)
    tp = next((p for p in op.get("pages") or [] if p.get("layout") == "types"), {})
    # the record clashes the opening counts are the records with a counted field that has an effect in play
    rec_want = len({r["formKey"] for r in rows if (policy["types"].get(r.get("checkId")) == "record")
                    and r.get("field") not in no_eff})
    rec_got = next((x.get("count") for x in tp.get("rows") or [] if x.get("label") == words["typeLabels"]["record"]), 0)
    check("F2-no-effect", counted == left and rec_got == rec_want and not shown and not spoken
          and tp.get("leftOut", {}).get("noInGameEffect") == left
          and not any(f in json.dumps(tp.get("rows") or []) for f in no_eff),
          f"{tag}fields with no in-game effect are left out of scenes and counts, and the number left out "
          f"({left} admitted rows; {counted} in verdictCounts.fieldNames) is on the opening still; record clashes counted "
          f"{rec_got}, {rec_want} expected (shown {len(shown)}, spoken "
          f"{spoken[:2]}, still says {tp.get('leftOut')})")


def oracle_checks(pres, view, expected, tag=""):
    got = [(c["kind"], c["clash"].get("type") if c["kind"] != "data-still" else c.get("type") or (c["clash"] or {}).get("type"),
            c["clash"].get("formKey") if c["kind"] == "vignette" else None) for c in view]
    want = [(e["kind"], e.get("type"), e.get("formKey")) for e in expected]
    check("3-selection", [g[0] for g in got] == [w[0] for w in want] and [g[2] for g in got] == [w[2] for w in want],
          f"{tag}the selection and order follow the rule (got {got}, want {want})")
    diffs = [(i, g["narration"], e["narration"]) for i, (g, e) in enumerate(zip(view, expected)) if g["narration"] != e["narration"]]
    check("sentences", len(view) == len(expected) and not diffs,
          f"{tag}every sentence is the skill template filled with the evaluation's names (first difference: {diffs[:1]})")
    op = view[0] if view else {}
    tp = next((p for p in op.get("pages") or [] if p.get("layout") == "types"), {})
    rows = {r.get("label"): r.get("count") for r in tp.get("rows") or []}
    check("1-counts", rows == expected[0]["types"],
          f"{tag}the opening still's counts per clash type are the prediction's, without no-effect fields (got {rows}, want {expected[0]['types']})")


# ------------------------------------------------------------------------------------------ the expected reel (reference)
def reference_presentation(pres, names, masters, search, mods):
    """The expected reel written out in the planner's output shape, from the oracle: shows every check can pass."""
    exp = oracle(pres, names, masters, search, mods)
    target = pres["reader"]["prediction"]["subject"]
    out = copy.deepcopy(pres)
    chs = []
    for e in exp:
        if e["kind"] == "opening":
            chs.append({"kind": "opening", "narration": e["narration"], "still": {"pages": [
                {"layout": "types", "rows": [{"label": k, "count": v} for k, v in e["types"].items()],
                 "leftOut": {"noInGameEffect": e["leftOut"]}},
                {"layout": "load-order", "plugins": e["loadOrder"], "targets": [target]}]}})
        elif e["kind"] == "vignette":
            own = [p for p in pres["reader"]["suppliedPatches"] if set(x.lower() for x in e["pair"]) <= masters.get(p.lower(), set())]
            chs.append({"kind": "vignette", "narration": e["narration"],
                        "clash": {"type": e["type"], "formKey": e["formKey"], "pair": e["pair"]},
                        "frames": [{"state": s, "mediaId": f"M-{e['formKey'][:6]}-{s}"} for s in POLICY["sequence"]],
                        "patch": {"patch": own[0], "pair": e["pair"], "masters": sorted(masters[own[0].lower()])} if own and "Get " in e["narration"] else {}})
        elif e["kind"] == "data-still":
            chs.append({"kind": "data-still", "type": e["type"], "narration": e["narration"], "clash": {"type": e["type"]},
                        "still": {"pages": [{"layout": "rows", "rows": [{"formKey": fk} for fk in e["formKeys"]]}]}})
        else:
            chs.append({"kind": "closing", "narration": e["narration"], "still": {"pages": [
                {"layout": "checks", "rows": [{"reason": r, "subjects": s} for r, s in e["checks"]]}]}})
    out["reel"] = {"schemaVersion": 1, "status": "planned", "modNames": mods, "chapters": chs}
    return out


# ------------------------------------------------------------------------------------------ sabotage arms
def sabotage(pres: dict, name: str) -> tuple[dict, str]:
    """Break one rule in a planned reel. Returns the broken presentation and the check that must fail."""
    p = copy.deepcopy(pres)
    chs = p["reel"]["chapters"]
    vig = [c for c in chs if c["kind"] == "vignette"]
    if name == "verdict-opening":
        chs[0]["narration"] = p["reader"]["verdict"] + " " + chs[0]["narration"]; return p, "1-opening"
    if name == "report-sentence":
        vig[0]["narration"] += " " + p["findings"][0]["readerPlain"]; return p, "2-wording"
    if name == "hnh":
        vig[0]["frames"] = [{"state": "highlight", "mediaId": "M-a"}, {"state": "with", "mediaId": "M-b"},
                            {"state": "highlight", "mediaId": "M-c"}]; return p, "3-vignettes"
    if name == "drop-pair":
        beta = next(c for c in vig if "Beta.esp" in (c["clash"].get("pair") or []))
        chs.remove(beta); return p, "3-selection"
    if name == "patch-any-pair":                           # Gate A 2.6 rule 2 as written (finding F1)
        beta = next(c for c in vig if "Beta.esp" in (c["clash"].get("pair") or []))
        beta["narration"] = beta["narration"].replace("No patch exists for these two mods.",
                                                      "Alpha - Target patch from Alpha Patches does not fix this one. "
                                                      "No other patch exists for these two mods.")
        beta["patch"] = {"patch": "Alpha - Target patch.esp", "pair": beta["clash"]["pair"],
                         "masters": ["alpha.esp", "skyrim.esm", "target.esp"]}
        return p, "4-patches"
    if name == "compared":
        vig[0]["narration"] += " No patch for this pair was compared."; return p, "4-patches"
    if name == "disclaimer":
        vig[0]["narration"] += " " + p["reader"]["limitations"][1]; return p, "5-disclaimers"
    if name == "closing-narrated":
        chs[-1]["narration"] = "Testing is incomplete; outstanding areas are listed below."; return p, "6-closing"
    if name == "editorid":
        st = next(c for c in chs if c["kind"] == "data-still" and c["type"] == "record")
        st["still"]["pages"][0]["rows"].append({"formKey": "000404:Skyrim.esm", "field": "EditorID"}); return p, "F2-no-effect"
    if name == "leftout-zero":                             # a reel that claims nothing was left out beside an EditorID clash
        chs[0]["still"]["pages"][0]["leftOut"]["noInGameEffect"] = 0; return p, "F2-no-effect"
    if name == "empty-reel":                               # nothing narrated: the wording and disclaimer checks must not pass
        for c in chs:
            c["narration"] = ""
        return p, "2-wording"
    raise SystemExit(f"unknown sabotage arm {name}")


SABOTAGE = ["verdict-opening", "report-sentence", "hnh", "drop-pair", "patch-any-pair", "compared", "disclaimer",
            "closing-narrated", "editorid", "leftout-zero", "empty-reel"]


# ------------------------------------------------------------------------------------------ the planner cases
def planned(pres, names, masters, search, mods, policy=POLICY, words=WORDS):
    """The planner's reel for an evaluation (the new `reel_plan.plan_reel`); on a head without it, the reel the base
    renders for that evaluation (its `chapters`, empty here), so every check below fails on its own assertion."""
    try:
        import reel_plan  # noqa: F401
    except ImportError:
        print("  NOTE  no reel planner on this head (module reel_plan absent): checking the reel the base renders")
        return copy.deepcopy(pres)
    out = copy.deepcopy(pres)
    out["reel"] = reel_plan.plan_reel(out, names, masters, policy, words, search, mods)
    return out


def case_planner():
    pres, names, masters, search, mods = synthetic()
    got = planned(pres, names, masters, search, mods)
    rule_checks(got, reel_view(got), tag="synthetic: ", mods=mods)
    oracle_checks(got, reel_view(got), oracle(pres, names, masters, search, mods), tag="synthetic: ")
    # F1 (Gate A review): the Beta overlap, a pair the supplied patch does not master, never names that patch
    beta = [c for c in reel_view(got) if c["kind"] == "vignette" and "Beta.esp" in (c["clash"].get("pair") or [])]
    check("F1-own-pair", bool(beta) and all("Alpha - Target patch" not in c["narration"] for c in beta)
          and all(WORDS["patch"]["none"] in c["narration"] for c in beta),
          "a supplied patch for another pair is not spoken over this pair's clash; it says no patch exists")
    # 3 the numbers are data: a policy allowing one per type selects one per type
    one = dict(POLICY, allUpTo=1, perType=1)
    g1 = planned(pres, names, masters, search, mods, policy=one)
    oracle_checks(g1, reel_view(g1), oracle(pres, names, masters, search, mods, policy=one), tag="policy perType 1: ")
    # 2 sentinels: every report sentence replaced; the narration does not change and carries none of them
    sp, sn, sm, ss, smods = synthetic(sentinel=True)
    gs = planned(sp, sn, sm, ss, smods)
    sent = [c["narration"] for c in reel_view(gs)]
    check("2-sentinel", bool(reel_view(gs)) and not any("SENTINEL" in n for n in sent)
          and sent == [c["narration"] for c in reel_view(got)],
          "with every report sentence replaced by a sentinel the narration is unchanged and sentinel-free")
    # 5 play-time: mentioned in that vignette only, and only when supplied
    pp, pn, pm, ps, pmods = synthetic(play_time=True)
    gp = planned(pp, pn, pm, ps, pmods)
    vp = reel_view(gp)
    with_play = [c for c in vp if WORDS["playTime"] in c["narration"]]
    check("5-play-time", len(with_play) == 1 and with_play[0]["clash"].get("formKey") == "000101:Target.esp",
          f"play-time evidence is mentioned once, in the vignette it was supplied for ({len(with_play)} scenes mention it)")
    # Q1 (Gate A review): no patch-search file -> the reel plan refuses as a whole, naming the file
    np_, nn, nm, _, nmods = synthetic(patch_search=False)
    gn = planned(np_, nn, nm, None, nmods)
    reel = gn.get("reel") or {}
    check("Q1-patch-search", reel.get("status") == "refused" and "patch-search" in str(reel.get("reason")) and not reel.get("chapters"),
          f"without a recorded patch search the reel plan refuses as a whole, naming the file (status {reel.get('status')!r})")
    # Gate B review, decision 3: a spoken mod with no recorded or declared name refuses the reel, naming the plugin
    mp_, mn, mm, ms, mmods = synthetic()
    mmods.pop("Beta.esp")
    gm = planned(mp_, mn, mm, ms, mmods)
    reel = gm.get("reel") or {}
    check("mod-name-refusal", reel.get("status") == "refused" and "Beta.esp" in str(reel.get("reason")) and not reel.get("chapters"),
          f"a mod the reel would speak about with no name refuses the reel in one sentence naming the plugin ({reel.get('reason')!r})")
    # Gate B review, patch rule: a search that missed its own known answer never says "No patch exists"
    kp, kn, km, ks, kmods = synthetic(known_found=False)
    gk = planned(kp, kn, km, ks, kmods)
    texts = [c["narration"] for c in reel_view(gk)]
    check("known-answer", bool(texts) and not any("No patch exists" in x or "No other patch exists" in x for x in texts)
          and any(WORDS["patch"]["notFound"] in x for x in texts),
          "a patch search that did not find its own known patch says a patch was not found, never that none exists")
    # a search file that states found=true with no hit behind it is not believed
    ks3 = copy.deepcopy(ks); ks3["knownAnswer"]["found"] = True
    gf = planned(kp, kn, km, ks3, kmods)
    texts3 = [c["narration"] for c in reel_view(gf)]
    check("known-answer-derived", bool(texts3) and not any("No patch exists" in x for x in texts3)
          and any(WORDS["patch"]["notFound"] in x for x in texts3),
          "a search file stating found=true with no matching hit still says a patch was not found")
    ks2 = copy.deepcopy(ks); ks2["knownAnswer"]["hits"] = [{"file": "Alpha - Target patch"}]
    ks2["pairs"][1]["unreadSources"] = [{"source": "a page description", "why": "cut short"}]
    gu = planned(kp, kn, km, ks2, kmods)
    beta = [c["narration"] for c in reel_view(gu) if c["kind"] == "vignette" and "Beta.esp" in (c["clash"].get("pair") or [])]
    check("unread-source", bool(beta) and all(WORDS["patch"]["notFound"] in x for x in beta),
          "a pair with an unread source says a patch was not found, even when the known answer was found")
    # S2: an actor's placement says the two mods place the NPC in different spots, never where it stands in play
    ap, an, am, as_, amods = synthetic()
    an["bases"]["B5"] = {"type": "Npc", "editorId": "SomeGuard"}
    ga = planned(ap, an, am, as_, amods)
    text = " ".join(c["narration"] for c in reel_view(ga))
    check("S2-actor", "Beta Mod and Target Mod place Some Guard in different spots." in text and "moves Some Guard" not in text,
          "an NPC placement clash says the two mods place the NPC in different spots, not that it stands elsewhere")


def case_reference():
    pres, names, masters, search, mods = synthetic()
    ref = reference_presentation(pres, names, masters, search, mods)
    rule_checks(ref, reel_view(ref), tag="reference: ")
    oracle_checks(ref, reel_view(ref), oracle(pres, names, masters, search, mods), tag="reference: ")
    return ref


def case_sabotage(name, base_pres):
    broken, target = sabotage(base_pres, name)
    pres, names, masters, search, mods = synthetic()
    RESULTS.clear()
    try:
        rule_checks(broken, reel_view(broken), tag=f"sabotage {name}: ")
        oracle_checks(broken, reel_view(broken), oracle(pres, names, masters, search, mods), tag=f"sabotage {name}: ")
    except Exception:
        traceback.print_exc()
        print(f"SABOTAGE {name}: crashed, which is not red")
        return 1
    failed = [n for n, ok, _ in RESULTS if not ok]
    ok = target in failed
    print(f"SABOTAGE {name}: target check {target} {'FAILED as it must' if ok else 'did NOT fail'}; failed checks {failed}")
    return 0 if ok else 1


# ------------------------------------------------------------------------------------------ capture plan (F3)
def case_capture_plan():
    import report_scenes
    root = Path(tempfile.mkdtemp(prefix="hc-reel-capture-"))
    try:
        conflicts = [{"Kind": "OVERLAP", "A": "Alpha.esp", "B": "Target.esp",
                      "Necessary": [{"FormKey": f"0001{i:02d}:Target.esp", "Label": "CellOne", "Physical": "Holds",
                                     "PhysicalDetail": "crossing"}], "Alternative": [{"FormKey": f"0002{i:02d}:Alpha.esp"}]}
                     for i in range(1, 5)]
        emit = {"Probe": "overlapprobe", "State": "baseline",
                "Configuration": {"Fingerprint": "fixture", "Plugins": [{"Name": "Alpha.esp"}, {"Name": "Target.esp"}]},
                "Conflicts": conflicts}
        src = root / "emit.json"
        src.write_text(json.dumps(emit))
        f = {"id": "F-O", "kind": "finding", "checkId": "geometry.object-overlap", "armId": "with-target", "part": "arm",
             "counts": {"A": "Alpha.esp", "B": "Target.esp"}, "allRows": [{"formKey": c["Necessary"][0]["FormKey"]} for c in conflicts],
             "evidenceIds": ["E-1"]}
        p = {"findings": [f], "evidence": [{"id": "E-1", "kind": "emit", "part": "arm", "armId": "with-target", "path": src.name,
                                            "sha256": hashlib.sha256(src.read_bytes()).hexdigest()}],
             "reel": {"status": "planned", "chapters": [
                 {"kind": "vignette", "clash": {"type": "overlap", "formKey": "000102:Target.esp", "other": ["000202:Alpha.esp"],
                                                "pair": ["Alpha.esp", "Target.esp"], "armId": "with-target"},
                  "frames": [{"state": s} for s in POLICY["sequence"]]}]}}
        policy = json.loads(report_scenes.POLICY.read_text(encoding="utf-8"))
        rule = next(r for r in policy["rules"] if r["checkId"] == "geometry.object-overlap")
        reqs = report_scenes.capture_requests(p, root)["requests"]
        states = [r.get("sequence", {}).get("states") for r in reqs]
        check("F3-states", rule.get("sequence", {}).get("states") == POLICY["sequence"] and bool(reqs)
              and all(s == POLICY["sequence"] for s in states) and "unhighlightedAppearance" not in json.dumps(rule),
              f"the overlap policy and its capture requests ask for {POLICY['sequence']} and nothing else (policy "
              f"{rule.get('sequence', {}).get('states')}, requests {states[:2]})")
        check("F3-selected", [r["recordKeys"][0] for r in reqs] == ["000102:Target.esp"],
              f"the capture plan asks for exactly the selected vignettes ({[r['recordKeys'][0] for r in reqs]})")
    finally:
        shutil.rmtree(root, ignore_errors=True)


# ------------------------------------------------------------------------------------------ the shipped skill data
def case_skill_data():
    pr = json.loads((SKILL / "references/report-priorities.json").read_text(encoding="utf-8"))
    sp = json.loads((SKILL / "references/scene-policy.json").read_text(encoding="utf-8"))
    words = ((pr.get("reel") or {}).get("words")) or {}
    vign = sp.get("vignettes") or {}
    check("data-words", set(WORDS) - {"forbidden", "forbiddenPatterns"} <= set(words) and set(WORDS["patch"]) <= set(words.get("patch") or {}),
          f"report-priorities.json reel.words carries every sentence and label the planner uses (missing "
          f"{sorted(set(WORDS) - {'forbidden', 'forbiddenPatterns'} - set(words))[:6]})")
    check("data-policy", all(k in vign for k in ("allUpTo", "perType", "maxVignettes", "noInGameEffectFields", "stillReasons",
                                                  "sequence", "holdSeconds", "patchSearch")) and vign.get("sequence") == POLICY["sequence"]
          and all(vign.get(k) == POLICY[k] for k in ("allUpTo", "perType", "maxVignettes", "typeOrder", "types", "pictureTypes",
                                                       "actorTypes", "noInGameEffectFields", "sequence")),
          f"scene-policy.json vignettes carries the selection numbers, the no-effect fields and the sequence (has {sorted(vign)})")
    check("data-length", (pr.get("reel") or {}).get("maxSeconds") == 600 and bool((pr.get("reel") or {}).get("stillFont")),
          f"reel.maxSeconds is 600 and the still font file is named ({(pr.get('reel') or {}).get('maxSeconds')}, "
          f"{(pr.get('reel') or {}).get('stillFont')!r})")
    docs = (SKILL / "SKILL.md").read_text(encoding="utf-8") + (SKILL / "references/reel-layout.md").read_text(encoding="utf-8")
    old = [x for x in ("highlighted -> unhighlighted -> highlighted", "highlighted → unhighlighted → highlighted",
                       "highlighted -> normal -> highlighted", "must start with `reader.verdict`", "starts with `reader.verdict` verbatim",
                       "no title cards") if x in docs]
    check("data-docs", not old, f"the skill no longer states the rules the vignette format supersedes (still present: {old})")


# ------------------------------------------------------------------------------------------ render (stills, sequence, F4)
def case_render():
    import evalreel
    found = evalreel.ffmpeg_find()
    if not check("render-ffmpeg", found is not None, "ffmpeg is resolvable"):
        return
    ff = found["exe"]
    root = Path(tempfile.mkdtemp(prefix="hc-reel-render-"))
    try:
        (root / "media").mkdir()
        def png(name, colour):
            subprocess.run([ff, "-y", "-v", "error", "-f", "lavfi", "-i", f"color=c={colour}:size=1280x720", "-frames:v", "1",
                            str(root / "media" / name)], check=True, capture_output=True)
            return hashlib.sha256((root / "media" / name).read_bytes()).hexdigest()
        subprocess.run([ff, "-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", str(root / "media/n.wav")],
                       check=True, capture_output=True)
        media = []
        for state, colour in zip(POLICY["sequence"], ("red", "green", "blue")):
            media.append({"id": f"M-{state}", "path": f"media/{state}.png", "sha256": png(f"{state}.png", colour), "assessed": True,
                          "mediaKind": "offline-render", "state": "baseline",
                          "subject": {"armId": "without-target" if state == "without" else "with-target"},
                          "provenance": {"poseId": "pose-1", "request": "capture-1", "state": state}})
        clean = {"cleanFrame": True, "visualReview": {"subjectVisible": True, "addedText": "none"}}
        pres = {"schemaVersion": 1, "presentationId": "render-fixture", "source": {"bundleDir": "."},
                "reader": {"verdict": "Verdict sentence."}, "settings": {"reel": {"maxSeconds": 600, "stillFont": "C:/Windows/Fonts/arial.ttf"}},
                "media": media[1:], "chapters": [],
                # the "without" frame comes from the sibling bundle, as the plan joins it
                "reel": {"schemaVersion": 1, "status": "planned", "media": media[:1], "chapters": [
                    {"id": "R-01", "kind": "opening", "narration": "Here are the types of clashes that are predicted from adding Target Mod.",
                     "narrationAudio": "media/n.wav",
                     "still": {"pages": [{"layout": "types", "rows": [{"label": "Objects in the same space", "count": 1}],
                                          "leftOut": {"noInGameEffect": 0}},
                                         {"layout": "load-order", "plugins": ["Skyrim.esm", "Target.esp"], "targets": ["Target.esp"]}]}},
                    {"id": "R-02", "kind": "vignette", "narration": "In The First Hall, two objects occupy the same space.",
                     "narrationAudio": "media/n.wav", "clash": {"type": "overlap", "formKey": "000101:Target.esp"},
                     "frames": [{"state": m["provenance"]["state"], "mediaId": m["id"], "storyboard": clean} for m in media]},
                    {"id": "R-03", "kind": "closing", "narration": "These are the checks that could not run or could not decide.",
                     "narrationAudio": "media/n.wav",
                     "still": {"pages": [{"layout": "checks", "rows": [{"reason": "ran, but could not decide", "subjects": ["A check"]}]}]}}]}}
        pp = root / "presentation.json"
        pp.write_text(json.dumps(pres))
        def render(p, out, env=None):
            return subprocess.run([sys.executable, str(HARNESS / "evalreel.py"), "render", "--presentation", str(p), "--out", str(out)],
                                  capture_output=True, text=True, env={**os.environ, **(env or {})})
        r = render(pp, root / "reel")
        ok = check("render-reel", r.returncode == 0, f"a reel of stills and a vignette renders (exit {r.returncode}: "
                                                     f"{' '.join((r.stdout + r.stderr).split())[-200:]})")
        if ok:
            mp = json.loads((root / "reel/reel-map.json").read_text(encoding="utf-8"))
            kinds = [s.get("kind") for s in mp["segments"]]
            check("render-kinds", kinds == ["still", "sequence", "still"], f"the map's scenes are still, sequence, still ({kinds})")
            seq = next(s for s in mp["segments"] if s.get("kind") == "sequence")
            colours = []
            for fr in seq.get("frames") or []:
                n = fr["reelInterval"]["startFrame"] + fr["reelInterval"]["frames"] // 2
                full = subprocess.run([ff, "-v", "error", "-i", str(root / "reel/reel.mp4"), "-vf", f"select=eq(n\\,{n})",
                                       "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True).stdout
                at = (360 * 1280 + 640) * 3
                raw = full[at:at + 3]
                colours.append(max(range(3), key=lambda i: raw[i]) if len(raw) == 3 else None)
            check("render-sequence", colours == [0, 1, 2], f"the vignette shows without, with, highlight in that order at its mapped frames ({colours})")
            font = mp.get("stillFont") or {}
            stills = [s for s in mp["segments"] if s.get("kind") == "still"]
            check("F4-font-hash", font.get("sha256") == hashlib.sha256(Path("C:/Windows/Fonts/arial.ttf").read_bytes()).hexdigest()
                  and all(s.get("dataSha256") for s in stills),
                  f"reel-map.json records the still font's hash and each still's data hash ({font}, {[s.get('dataSha256') for s in stills]})")
        rf = render(pp, root / "nofont", env={"HOUSECARL_REEL_FONT": str(root / "absent.ttf")})
        msg = " ".join((rf.stdout + rf.stderr).split())
        check("F4-font-missing", rf.returncode == 2 and "font" in msg.lower() and "absent.ttf" in msg,
              f"an absent still font refuses in one sentence naming the file, never a default font (exit {rf.returncode}: {msg[-160:]})")
        bad = copy.deepcopy(pres)
        bad["media"][1]["provenance"]["poseId"] = "pose-2"
        bp = root / "pose.json"
        bp.write_text(json.dumps(bad))
        rp = render(bp, root / "pose")
        check("3-same-pose", rp.returncode == 2 and "pose" in (rp.stdout + rp.stderr).lower(),
              f"a vignette whose frames were taken at different poses refuses (exit {rp.returncode})")
    finally:
        shutil.rmtree(root, ignore_errors=True)



# ------------------------------------------------------------------------------------------ code sabotage arms
# Each arm breaks one rule in a copy of the shipped code or skill data (one exact replacement each, counted), runs the
# case that protects it against the copy, and passes only when the named check fails on its own assertion.
REF = ".claude/skills/evaluation-reporting/references"
CODE_SABOTAGE = {
    "code-f1": ("planner", "F1-own-pair", [("_geometry-harness/reel_plan.py",
        "return [p for p in supplied if {other.lower(), target.lower()} <= set(masters.get(p.lower(), set()))]",
        "return list(supplied)")]),
    "code-q1": ("planner", "Q1-patch-search", [
        ("_geometry-harness/reel_plan.py", "if selected and patch_search is None:", "if False:"),
        ("_geometry-harness/reel_plan.py", "if entry is None or not need_tools <= tools:", "if False:")]),
    "code-known-answer": ("planner", "known-answer", [("_geometry-harness/reel_plan.py",
        "if known_found and not unread:", "if True:")]),
    "code-known-stated": ("planner", "known-answer-derived", [("_geometry-harness/reel_plan.py",
        "    known_found = bool(want) and any(", '    known_found = known.get("found") is True or bool(want) and any(')]),
    "code-no-effect": ("planner", "F2-no-effect", [("_geometry-harness/reel_plan.py",
        'kept_rows = [r for r in rows if r.get("field") not in no_effect]', "kept_rows = list(rows)")]),
    "code-verdict-opening": ("planner", "1-opening", [("_geometry-harness/reel_plan.py",
        '"narration": (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),',
        '"narration": str(reader.get("verdict")) + " " + (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),')]),
    "code-f3-states": ("capture", "F3-states", [(REF + "/scene-policy.json",
        '"states": ["without", "with", "highlight"],', '"states": ["highlighted", "unhighlighted", "highlighted"],')]),
    "code-f3-selected": ("capture", "F3-selected", [("_geometry-harness/report_scenes.py",
        'if reel.get("status") == "planned":', "if False:")]),
    "code-font-default": ("render", "F4-font-missing", [("_geometry-harness/reel_stills.py",
        "            raise FontMissing(",
        "            return Path(regular or bold or 'C:/Windows/Fonts/arial.ttf'), Path('C:/Windows/Fonts/arial.ttf')\n            raise FontMissing(")]),
    "code-font-hash": ("render", "F4-font-hash", [("_geometry-harness/evalreel.py",
        '"generatedUtc": utcnow(), "stillFont": font_doc,', '"generatedUtc": utcnow(),')]),
    "code-pose": ("render", "3-same-pose", [("_geometry-harness/evalreel.py",
        "if len(poses) != 1 or None in poses or len(sizes) != 1:", "if False:")]),
    "code-sequence-order": ("render", "render-sequence", [("_geometry-harness/evalreel.py",
        '[(it["path"], it["frames"]) for it in s["items"]], s["audioPath"]',
        'list(reversed([(it["path"], it["frames"]) for it in s["items"]])), s["audioPath"]')]),
}


def code_sabotage(name: str) -> int:
    case, target, edits = CODE_SABOTAGE[name]
    tmp = Path(tempfile.mkdtemp(prefix="hc-reel-sabotage-"))
    try:
        for d in ("_geometry-harness", "scripts"):
            (tmp / d).mkdir(parents=True)
            for f in (ROOT / d).glob("*.py"):
                shutil.copy2(f, tmp / d / f.name)
        (tmp / "_geometry-harness/tests").mkdir()
        shutil.copy2(Path(__file__), tmp / "_geometry-harness/tests" / Path(__file__).name)
        shutil.copytree(SKILL, tmp / ".claude/skills/evaluation-reporting")
        for rel, old, new in edits:
            f = tmp / rel
            text = f.read_text(encoding="utf-8")
            n = text.count(old)
            if n != 1:
                print(f"SABOTAGE {name}: the replacement occurs {n} times in {rel}, not once; the arm is not run")
                return 1
            f.write_text(text.replace(old, new), encoding="utf-8")
        r = subprocess.run([sys.executable, "-B", str(tmp / "_geometry-harness/tests" / Path(__file__).name), "--case", case],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        out = r.stdout + r.stderr
        failed = re.findall(r"^  FAIL  \[([^\]]+)\]", out, re.M)
        crashed = "Traceback" in out
        ok = target in failed and not crashed
        print(f"SABOTAGE {name}: target check {target} {'FAILED as it must' if ok else 'did NOT fail'}"
              f"{' (crashed, which is not red)' if crashed else ''}; failed checks {failed}")
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--presentation")
    ap.add_argument("--reference", action="store_true")
    ap.add_argument("--sabotage")
    ap.add_argument("--code-sabotage", help="a CODE_SABOTAGE arm name, or all")
    ap.add_argument("--case", choices=("planner", "capture", "data", "render"))
    a = ap.parse_args(argv)
    if a.code_sabotage:
        arms = list(CODE_SABOTAGE) if a.code_sabotage == "all" else [a.code_sabotage]
        return max(code_sabotage(n) for n in arms)
    if a.presentation:
        pres = json.loads(Path(a.presentation).read_text(encoding="utf-8"))
        rule_checks(pres, reel_view(pres), tag=f"{Path(a.presentation).name}: ")
    elif a.reference:
        case_reference()
    elif a.sabotage:
        pres, names, masters, search, mods = synthetic()
        got = planned(pres, names, masters, search, mods) if os.environ.get("HC_REEL_SABOTAGE_ON") == "planner" else \
            reference_presentation(pres, names, masters, search, mods)
        arms = SABOTAGE if a.sabotage == "all" else [a.sabotage]
        return max(case_sabotage(n, got) for n in arms)
    else:
        cases = {"planner": case_planner, "capture": case_capture_plan, "data": case_skill_data, "render": case_render}
        for case in ([cases[a.case]] if a.case else cases.values()):
            try:
                case()
            except Exception:
                traceback.print_exc()
                check(case.__name__, False, "raised an exception (a crash, not a red assertion)")
    failed = [n for n, ok, _ in RESULTS if not ok]
    print(f"{len(failed)} failure(s) of {len(RESULTS)}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
