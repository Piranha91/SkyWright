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
    "typeOrder": ["overlap", "contact", "record", "cell", "state", "navmesh", "other"],
    "types": {"geometry.object-overlap": "overlap", "geometry.object-contact": "contact",
              "geometry.fixture-empty-space": "contact", "records.contested-fields": "record",
              "records.cell-attributes": "cell", "state.conditional-geometry": "state",
              "geometry.navmesh-ownership": "navmesh", "geometry.placement-contact": "contact"},
    "pictureTypes": ["overlap", "contact", "record", "state"],          # phase 2: placements and world states are pictured
    "drawableFields": ["Placement"],                                     # a record clash is pictured only for these fields
    "groupKey": ["type", "pair", "cell", "field"],                       # phase 3: instances of one problem
    "structureShare": 0.5, "storeyTolerance": 96.0, "metresPerUnit": 0.01428,
    "captureAttemptsPerType": 5,                                         # Gate A2 Q3
    "volumeFactor": 50,                                                  # Gate A2 Q6: mark only the smaller participant above it
    "effectModelPattern": r"^(?:effects|sky)[\\/]",          # the report's own pattern (report-priorities.json)
    "stillReasons": {"effect": "effect", "record": "record", "cell": "cell", "state": "state", "navmesh": "navmesh",
                     "captureRefused": "captureRefused", "notDrawn": "notDrawn", "leftNotLoser": "leftNotLoser"},
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
    "stateClash": "{condition}, {modA}'s {objectA} and {modB}'s {objectB} occupy the same space.",
    "conditionQuest": "When {quest} reaches stage {stage}",
    "conditionFallback": "In one state of the world, when these objects appear in {place}",
    "placementSplit": "{loser} and {winner} place {object} in different spots: {loser}'s on the left, {winner}'s on the right.",
    "moverOne": "{mover} moves {object}; {keeper} keeps the original spot.",
    "moverBoth": "{a} and {b} both move {object}, to different spots.",
    "structureUp": "{mover} raises {place} and moves {object} with it; {keeper} keeps the original spot.",
    "structureDown": "{mover} lowers {place} and moves {object} with it; {keeper} keeps the original spot.",
    "structureReplaced": "{mover} replaces {place} with its own, turning the original off, and moves {object} into it; {keeper} keeps the original spot.",
    "structurePlace": "the building around {object}",
    "rightSpot": "The right half shows the spot with {mod} added, {where} the left one",
    "where": {"below": "about {m} metres below", "above": "about {m} metres above", "away": "about {m} metres away from",
              "close": "close to"},
    "spotExtra": {"noFloor": ", where there is no floor", "belowGround": ", below the ground", "inAir": ", in the air"},
    "groupOthers": {"one": "{names} is placed the same way.", "many": "{names} are placed the same way.",
                    "more": "{names} and {count} more are placed the same way."},
    "splitLabels": {"left": "Without {mod}", "right": "With {mod}"},
    "caseLabels": {"overlap": "objects in the same space", "contact": "objects touching or blocking",
                   "record": "record clashes", "state": "objects that clash when a quest or switch changes the world"},
    "cases": {"one": "There is {count} case of {label}.", "many": "There are {count} cases of {label}."},
    "lead": {"one": "Here is a representative example.", "some": "Here are {count} representative examples.",
             "all": "Here are all of them."},
    "unpictured": {"one": "{count} clash can't be visualized; find it in the accompanying report.",
                   "many": "{count} clashes can't be visualized; find them in the accompanying report."},
    "field": "{winner} replaces {loser}'s {field} of {object}.",
    "cell": "{winner} replaces {loser}'s settings for {place}.",
    "fieldLabels": {"Description": "description", "Conditions": "conditions", "Name": "name", "Placement": "position",
                    "OcclusionData": "occlusion settings"},
    "patchName": "{plugin} from {mod}",
    "stillTitles": {"types": "Clashes predicted from adding {mod}", "loadOrder": "Plugins analysed", "affected": "(affected)",
                    "checks": "Checks that could not run or could not decide",
                    "leftOut": "Not shown: {count} {noun} with no effect in play", "runtime": "Mods changing these scenes at run time"},
    "patch": {"fixes": ["Get {patch}; it fixes this.", "Installing {patch} fixes this.", "Install {patch} to fix this."],
              "keeps": ["Get {patch}; it keeps {kept}'s version.", "Installing {patch} keeps {kept}'s version."],
              "own": ["Get {patch}; it sets its own version.", "Installing {patch} sets its own version."],
              "leaves": ["{patch} does not fix this one.", "{patch} leaves this one as it is."],
              "listed": ["A patch for these two mods is listed on Nexus: {file}.", "Nexus lists a patch for these two mods: {file}."],
              "none": ["No patch exists for these two mods.", "There is no patch for these two mods."],
              "noneOther": ["No other patch exists for these two mods.", "There is no other patch for these two mods."],
              "notFound": ["No patch for these two mods was found.", "A patch for these two mods was not found."],
              "notFoundOther": ["No other patch for these two mods was found.", "Another patch for these two mods was not found."]},
    "playTime": "This was also recorded in play.",
    "closingLines": {"withSetup": "{check}, with {patch}", "withoutSetup": "{check}, without {patch}"},
    "leftOutMarkers": "Not shown: {count} {noun} on editor-only markers",
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
              rec("000405:Skyrim.esm", "Beta.esp", "Placement", "Position=0,0,0; Rotation=0,0,0",
                  "Position=500,0,0; Rotation=0,0,0", "left", "PlacedObject XMarkerHeading: Placement - Beta.esp's value lost to Target.esp's"),
              rec("000501:Skyrim.esm", "Beta.esp", "OcclusionData", "00ff", "ff00", "left",
                  "Cell CellFour: OcclusionData - Beta.esp's value lost to Target.esp's", check="records.cell-attributes"),
              {"checkId": "geometry.object-overlap", "kind": "OVERLAP", "A": "Target.esp", "B": "Target.esp",
               "formKey": "000901:Target.esp", "admission": "sameMod", "summary": "same mod"},
              # phase 2: an overlap with a visual effect (never pictured) and a world-state contact (pictured in its state)
              ov(6, "000303:Beta.esp", "CellThree", "left", b="Beta.esp"),
              {"checkId": "state.conditional-geometry", "kind": "STATE-CONTACT@000701:Skyrim.esm@Triggered",
               "A": "Target.esp", "B": "Beta.esp", "formKey": "000601:Target.esp", "other": ["000602:Beta.esp"],
               "admission": "admitted", "findingId": "F-S1", "withPatch": "left", "physical": "Holds",
               "summary": "Contact StateConditional under CampEnable (Skyrim.esm): 000601:Target.esp [Static Target_Cairn01] "
                          "arrives - Sunk: the arriving dependent sits 9.3u inside something enabled in the state in CellFive (ext)"}]
    verdict_counts = [
        {"checkId": "geometry.object-overlap", "kind": "OVERLAP", "count": 5, "unit": "relationships", "fieldNames": {}},
        {"checkId": "records.contested-fields", "kind": "LOST-EDIT", "count": 5, "unit": "objects",
         "fieldNames": {"Placement": 3, "Description": 1, "EditorID": 1}},
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
            "missingSummary": [{"reason": "ran, but could not decide",
                                "subjects": ["Whether a single object blocks an NPC route — Measured load order",
                                             "Whether a single object blocks an NPC route — Tested alternative setup",
                                             "Connected doors — Measured load order"]},
                               {"reason": "cannot yet be measured by this evaluator",
                                "subjects": ["Whether several changes together disconnect an NPC route — Measured load order",
                                             "Whether several changes together disconnect an NPC route — Tested alternative setup",
                                             "Connected doors — Tested alternative setup"]}],
            "prediction": {"subject": "Target.esp", "mods": ["Target.esp"], "predictionArm": "with-target",
                           "withoutArm": "sibling:without-target", "verdictCounts": verdict_counts, "record": record,
                           "items": []},
        },
        "scope": {"arms": [{"armId": "with-target", "displayLabel": "Tested alternative setup", "locationPluginOrder": order},
                           {"armId": "baseline", "displayLabel": "Measured load order", "locationPluginOrder": order[:-1]}]},
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
                      "000403:Skyrim.esm": {"base": "B5", "carriedBy": ["Skyrim.esm", "Beta.esp", "Target.esp"]},
                      "000405:Skyrim.esm": {"base": "B6", "carriedBy": ["Skyrim.esm", "Beta.esp", "Target.esp"]},
                      "000106:Target.esp": {"base": "B9", "carriedBy": ["Target.esp"]},
                      "000303:Beta.esp": {"base": "B3", "carriedBy": ["Beta.esp"]},
                      "000601:Target.esp": {"base": "B7", "carriedBy": ["Target.esp"], "scale": 1.0},
                      "000602:Beta.esp": {"base": "B8", "carriedBy": ["Beta.esp"], "scale": 1.0}},
             "bases": {"B1": {"type": "Activator", "editorId": "Target_Altar01", "model": "Target\\Altar01.nif"},
                       "B2": {"type": "Door", "editorId": "AlphaDoor01", "model": "Alpha\\Door01.nif"},
                       "B3": {"type": "Static", "editorId": "BetaPillar01", "model": "Beta\\Pillar01.nif"},
                       "B4": {"type": "Static", "editorId": "MAG_StoneBench02", "model": ""},
                       "B5": {"type": "Static", "editorId": "SomeLamp01", "model": "Clutter\\Lamp01.nif"},
                       "B6": {"type": "Static", "editorId": "XMarkerHeading", "model": "XMarkerHeading.nif"},
                       "B7": {"type": "Static", "editorId": "Target_Cairn01", "model": "Target\\Cairn01.nif",
                              "bounds": [-40, -40, 0, 40, 40, 90]},
                       "B8": {"type": "Static", "editorId": "BetaArch01", "model": "Beta\\Arch01.nif",
                              "bounds": [-3000, -6000, -4000, 3000, 6000, 2000]},
                       "B9": {"type": "MoveableStatic", "editorId": "FXMistLow", "model": "Effects\\FXMistLow.nif"}},
             "objects": {"Target_Altar01": {"name": "Altar"}, "SomeLamp01": {"name": "Lamp"}, "Target_Cairn01": {"name": "Cairn"},
                         "BetaArch01": {"name": "Rock Arch"}},
             "places": {"CellOne": {"name": "The First Hall"}, "CellFive": {"name": "The Camp"}}}
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


def says(text, case, words=None):
    """Whether a narration says a patch case, in any of its pool's phrasings (the patch filled in as anything)."""
    vs = (words or WORDS)["patch"][case]
    for v in (vs if isinstance(vs, list) else [vs]):
        rx = re.escape(v).replace(r"\{patch\}", ".+?").replace(r"\{kept\}", ".+?").replace(r"\{file\}", ".+?")
        if re.search(rx, text):
            return True
    return False


def patch_phrasings(text, words=None):
    """(case, index) of every patch phrasing a narration uses, in order of appearance."""
    out = []
    for case, vs in (words or WORDS)["patch"].items():
        for i, v in enumerate(vs if isinstance(vs, list) else [vs]):
            rx = re.escape(v).replace(r"\{patch\}", ".+?").replace(r"\{kept\}", ".+?").replace(r"\{file\}", ".+?")
            for m in re.finditer(rx, text):
                out.append((m.start(), case, i))
    return [(c, i) for _, c, i in sorted(out)]


def synthetic_group():
    """The synthetic evaluation plus a group: three placements of the Beta x Target pair in one cell (the lamp and two
    more), one in another cell; each version's placement and cell; the right half's probe on the lamp's split."""
    pres, names, masters, search, mods = synthetic()
    rec = pres["reader"]["prediction"]["record"]
    lamp = next(r for r in rec if r["formKey"] == "000403:Skyrim.esm")
    lamp["values"] = {"lost": "Position=0,0,1424; Rotation=0,0,0", "winner": "Position=0,0,0; Rotation=0,0,0"}
    for fk, edid, nm in (("000406:Skyrim.esm", "LampTwo", "Lamp Two"), ("000407:Skyrim.esm", "LampThree", "Lamp Three"),
                         ("000409:Skyrim.esm", "LampFour", "Lamp Four")):
        rec.append({**lamp, "formKey": fk, "findingId": "F-" + fk[:6],
                    "summary": f"PlacedObject {edid}: Placement - Beta.esp's value lost to Target.esp's"})
        names["refs"][fk] = {"base": "B-" + edid, "carriedBy": ["Skyrim.esm", "Beta.esp", "Target.esp"]}
        names["bases"]["B-" + edid] = {"type": "Static", "editorId": edid, "model": f"Clutter\\{edid}.nif"}
        names["objects"][edid] = {"name": nm}
    cells = {"000403:Skyrim.esm": "000A01:Skyrim.esm", "000406:Skyrim.esm": "000A01:Skyrim.esm",
             "000407:Skyrim.esm": "000A01:Skyrim.esm", "000409:Skyrim.esm": "000A02:Skyrim.esm"}
    for fk, cell in cells.items():
        names["refs"][fk]["cells"] = {"Skyrim.esm": cell, "Beta.esp": cell, "Target.esp": cell}
    # the base game and Target keep the lamp at z 0; Beta raised it to z 1424 (the lost value): Beta moved it
    names["refs"]["000403:Skyrim.esm"]["placements"] = {"Skyrim.esm": [0.0, 0.0, 0.0], "Beta.esp": [0.0, 0.0, 1424.0],
                                                        "Target.esp": [0.0, 0.0, 0.0]}
    names["places"]["LampRoom"] = {"name": "The Lamp Room", "formKey": "000A01:Skyrim.esm"}
    pres["media"] += [{"id": "M-right-403", "assessed": True, "mediaKind": "offline-render",
                       "subject": {"formKey": "000403:Skyrim.esm", "armId": "with-target"},
                       "provenance": {"vignetteState": "right",
                                      "support": {"down": None, "terrainAbove": False, "interior": True}}}]
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


MARKERS = {"000405:Skyrim.esm"}                       # what evalreport's marker rule says of the synthetic refs


def setup_words(pres, label, words=WORDS):
    """A setup said in plain words: with or without the supplied patch, from the setup's own plugin order."""
    arm = next((a for a in (pres.get("scope") or {}).get("arms") or [] if a.get("displayLabel") == label), {})
    patches = list((pres.get("reader") or {}).get("suppliedPatches") or [])
    has = [p for p in patches if p in (arm.get("locationPluginOrder") or [])]
    return has, patches


def merged_checks(pres, mods, words=WORDS):
    """The closing's rows: one line per check and reason; a setup is named only where the check's reasons differ."""
    reasons_of = {}
    for m in (pres.get("reader") or {}).get("missingSummary") or []:
        for subj in m.get("subjects") or []:
            check_, _, setup = str(subj).partition(" — ")
            reasons_of.setdefault(check_, {}).setdefault(m.get("reason"), []).append(setup)
    rows = []
    for m in (pres.get("reader") or {}).get("missingSummary") or []:
        subs = []
        for subj in m.get("subjects") or []:
            check_, _, setup = str(subj).partition(" — ")
            if len(reasons_of[check_]) == 1:
                line = check_
            else:
                has, patches = setup_words(pres, setup, words)
                name = patch_display(mods, (has or patches)[0], words) if patches else None
                line = (words["closingLines"]["withSetup" if has else "withoutSetup"].format(check=check_, patch=name)
                        if name else check_)
            if line not in subs:
                subs.append(line)
        rows.append({"reason": m.get("reason"), "subjects": subs})
    return rows


# the switch census rows the planner reads for a world-state clash's condition (state.switch-census, the prediction setup)
SWITCHES = {"000701:Skyrim.esm": {"edid": "CampEnable", "baselineEnabled": False,
                                  "trigger": {"status": "Triggered", "kind": "quest stage",
                                              "by": ["quest SomeQuest stage 10 (QF_SomeQuest_00012345.Fragment_1: "
                                                     "Alias_Camp.GetReference().Disable()"]}}}


def affected_plugins(pres, policy=POLICY, markers=MARKERS):
    """The plugins the load-order page draws in the highlight colour: the mod being added and every plugin named as A or B
    of a counted clash row (not a marker row, not a no-effect field)."""
    pred = pres["reader"]["prediction"]
    no_effect = set(policy["noInGameEffectFields"])
    out = {pred["subject"]}
    for r in pred.get("record") or []:
        if r.get("admission") != "admitted" or r.get("field") in no_effect:
            continue
        if {r["formKey"], *(r.get("other") or [])} & set(markers):
            continue
        if (policy.get("types") or {}).get(r.get("checkId")) is None:
            continue
        out |= {r["A"], r["B"]}
    return out


def condition_words(row, names, switches, place, words=WORDS):
    """A world-state clash's condition: the quest and stage by in-game name when the census names a trigger that flips the
    switch INTO the clash state (Enable on a switch disabled at load, Disable on one enabled at load) and the quest has an
    in-game name; otherwise the fallback sentence. Never an editor id."""
    m = re.match(r"^STATE-[A-Z]+@([0-9A-Fa-f]{6}:[^@]+)@", str(row.get("kind") or ""))
    sw = (switches or {}).get(m.group(1)) if m else None
    for by in ((sw or {}).get("trigger") or {}).get("by") or []:
        t = re.match(r"^quest (\S+) stage (\d+) .*\.(Enable|Disable)\(\)", by)
        if not t:
            continue
        into = (t.group(3) == "Enable") != bool(sw.get("baselineEnabled"))
        quest = ((names.get("quests") or {}).get(t.group(1)) or {}).get("name")
        if into and quest:
            return words["conditionQuest"].format(quest=quest, stage=t.group(2))
    return words["conditionFallback"].format(place=place)


def oracle(pres, names, masters, search, mods, policy=POLICY, words=WORDS, markers=MARKERS, switches=None, captures=None):
    """The expected reel for an evaluation (phase 2: Piranha's viewing notes and the Gate A2 answers): selection over the
    clashes a picture can show, the without/with/highlight vignette or the placement split, the "There are N cases" opening
    of each pictured type, one count of what cannot be pictured, and the closing. Written from the rules, not the planner."""
    switches = SWITCHES if switches is None else switches
    pred = pres["reader"]["prediction"]
    target = pred["subject"]
    rows = [r for r in pred["record"] if r.get("admission") == "admitted"]
    no_effect = set(policy["noInGameEffectFields"])
    marker_rows = [r for r in rows if r.get("field") not in no_effect and
                   ({r["formKey"], *(r.get("other") or [])} & set(markers))]
    kept_rows = [r for r in rows if r.get("field") not in no_effect and r not in marker_rows]
    left_out = len([r for r in rows if r.get("field") in no_effect])        # record fields left out, on screen only
    left_markers = len({r["formKey"] for r in marker_rows})
    clashes = {}                                           # one clash per record (record types) or per row (geometry)
    for r in kept_rows:
        t = policy["types"][r["checkId"]]
        key = (t, r["formKey"]) if t in ("record", "cell") else (t, r["formKey"], tuple(r.get("other") or []))
        clashes.setdefault(key, {"type": t, "rows": [], "pair": tuple(sorted({r["A"], r["B"]} - {target})) + (target,)})["rows"].append(r)
    order = next(a["locationPluginOrder"] for a in pres["scope"]["arms"] if a["armId"] == pred["predictionArm"])

    def ref(fk):
        return (names.get("refs") or {}).get(fk) or {}

    def base(fk):
        return (names.get("bases") or {}).get(ref(fk).get("base")) or {}

    def effect(fk):
        return bool(re.search(policy["effectModelPattern"], str(base(fk).get("model") or ""), re.I))

    def drawable(c):
        """None when a picture can show this clash; else why not."""
        if c["type"] not in policy["pictureTypes"]:
            return c["type"]
        r = c["rows"][0]
        if c["type"] == "record":
            if r.get("field") not in policy["drawableFields"]:
                return "record"
            b = base(r["formKey"])
            if b.get("type") not in policy["actorTypes"] and not b.get("model"):
                return "notDrawn"
            if effect(r["formKey"]):
                return "effect"
            # the left half shows the other mod's spot only when that mod's version wins once the mod being added is gone
            carriers = [p for p in ref(r["formKey"]).get("carriedBy") or [] if p in order and p != target]
            if not carriers or carriers[-1] != r["A"]:
                return "leftNotLoser"
            return None
        if any(effect(fk) for fk in [r["formKey"], *(r.get("other") or [])]):
            return "effect"
        return None

    refused = {str(x.get("formKey")) for x in captures or [] if x.get("status") == "refused"}
    chosen_by_type, unpictured, represented, candidates, others = {}, [], {}, {}, {}
    for t in policy["typeOrder"]:
        cs = [c for c in clashes.values() if c["type"] == t]
        if not cs:
            continue
        draw = [c for c in cs if drawable(c) is None]
        unpictured += [(c, drawable(c)) for c in cs if drawable(c) is not None]
        def gkey(c):
            """Instances of one problem (skill data groupKey): type, mod pair, cell, and the field of a record clash."""
            r = c["rows"][0]
            if c["type"] == "record":
                cell = (ref(r["formKey"]).get("cells") or {}).get(r["A"])
                return (c["type"], c["pair"], cell or r["formKey"], r.get("field"))
            m = re.search(r" in ([A-Za-z0-9_]+) \((?:int|ext)\)", r.get("summary") or "")
            return (c["type"], c["pair"], m.group(1) if m else r["formKey"], None)
        groups = {}
        for c in sorted(draw, key=lambda c: c["rows"][0]["formKey"]):
            groups.setdefault(gkey(c), []).append(c)
        pairs = {}
        for g in groups.values():
            pairs.setdefault(g[0]["pair"], []).append(g)
        pair_order = sorted(pairs, key=lambda p: (-len(pairs[p]), p))
        ordering, i = [], 0                                # round the pairs until every group has a place
        while any(pairs.values()):
            p = pair_order[i % len(pair_order)]
            if pairs[p]:
                ordering.append(pairs[p].pop(0))
            i += 1
        quota = len(ordering) if len(ordering) <= policy["allUpTo"] else policy["perType"]
        chosen, tried = [], 0
        for g in ordering:
            if len(chosen) >= quota or tried >= policy["captureAttemptsPerType"]:
                break
            for c in g:                                    # a group's next member is its capture fallback
                if tried >= policy["captureAttemptsPerType"]:
                    break
                tried += 1
                if c["rows"][0]["formKey"] in refused:
                    unpictured.append((c, "captureRefused"))
                    continue
                chosen.append(c)
                others[c["rows"][0]["formKey"]] = [x for x in g if x is not c]
                break
        chosen_by_type[t] = chosen
        if t in policy["pictureTypes"]:
            candidates[t] = [c["rows"][0]["formKey"] for g in ordering for c in g][:policy["captureAttemptsPerType"]]
        represented[t] = [c for c in draw if c not in chosen and (c, "captureRefused") not in unpictured]
    # maxVignettes caps the pictured scenes in type order; what it drops is represented
    budget = policy["maxVignettes"]
    for t in policy["typeOrder"]:
        keep = chosen_by_type.get(t, [])[:max(budget, 0)]
        represented.setdefault(t, [])
        represented[t] += [c for c in chosen_by_type.get(t, []) if c not in keep]
        chosen_by_type[t] = keep
        budget -= len(keep)

    used = {}

    def pick(case):
        """The k-th sentence of a case in reel order takes phrasing k mod the pool's size (skill data)."""
        pool = words["patch"][case]
        k = used.get(case, 0)
        used[case] = k + 1
        return pool[k % len(pool)]

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
            return pick(key).format(patch=patch_display(mods, own[0], words), kept=kept)
        lead = pick("leaves").format(patch=patch_display(mods, own[0], words)) + " " if own else ""
        if hits:
            return lead + pick("listed").format(file=hits[0]["file"])
        known = (search or {}).get("knownAnswer") or {}
        want = re.sub(r"\.(esp|esm|esl)$", "", str(known.get("patch") or ""), flags=re.I).lower()
        found = bool(want) and any(want in json.dumps(h).lower() for h in known.get("hits") or [])
        if found and not entry.get("unreadSources"):
            return lead + pick("noneOther" if own else "none")
        return lead + pick("notFoundOther" if own else "notFound")

    def owner(fk, r):
        """The pair's plugin a reference belongs to: the plugin that made it, else its last carrier in the pair."""
        pair = [r["A"], r["B"]]
        made = str(fk).split(":", 1)[-1].lower()
        hit = [m for m in pair if m.lower() == made]
        if hit:
            return hit[0]
        carried = [m for m in ref(fk).get("carriedBy") or [] if m in pair]
        return carried[-1] if carried else pair[0]

    def slots(c):
        r = c["rows"][0]
        o_fk = (r.get("other") or [None])[0]
        a_owner, o_owner = owner(r["formKey"], r), owner(o_fk, r)
        first = (r["formKey"], a_owner) if a_owner == target else (o_fk, o_owner)
        second = (o_fk, o_owner) if first[0] == r["formKey"] else (r["formKey"], a_owner)
        return {"place": place_of(names, r["summary"]), "modA": mod_name(mods, first[1]), "objectA": spoken_ref(names, first[0]),
                "modB": mod_name(mods, second[1]), "objectB": spoken_ref(names, second[0])}

    def volume(fk):
        b = base(fk).get("bounds")
        if not b:
            return None
        s = float(ref(fk).get("scale") or 1.0)
        return abs((b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])) * s ** 3

    def highlight(c):
        r = c["rows"][0]
        fks = [r["formKey"], *(r.get("other") or [])]
        if c["type"] == "record":
            return [r["formKey"]]
        vols = [volume(fk) for fk in fks]
        if len(fks) == 2 and all(vols) and max(vols) >= policy["volumeFactor"] * min(vols):
            return [fks[vols.index(min(vols))]]
        return fks

    def right_support(fk):
        for m in pres.get("media") or []:
            prov = m.get("provenance") or {}
            if (m.get("assessed") and prov.get("vignetteState") == "right" and (m.get("subject") or {}).get("formKey") == fk
                    and (m.get("subject") or {}).get("armId") == pred["predictionArm"]):
                return prov.get("support")
        return None

    def orient(c):
        """Who moved it (against the base game's value), whether the structure moved with it (the mover's overrides in
        the cell), where the other spot is (the row's positions) and what is there (the right half's probe)."""
        r = c["rows"][0]
        fk = r["formKey"]
        obj = spoken_ref(names, fk, r["summary"])
        A, B = r["A"], r["B"]
        pl = ref(fk).get("placements") or {}
        added = fk.split(":", 1)[1].lower() in {A.lower(), B.lower()}      # a new reference: no base-game spot
        basev, av, bv = (None if added else pl.get(fk.split(":", 1)[1])), pl.get(A), pl.get(B)
        tol = policy["storeyTolerance"]

        def same(u, v):
            return max(abs(float(u[i]) - float(v[i])) for i in range(3)) <= 1.0
        if basev and av and bv:
            moved = [m for m, v in ((A, av), (B, bv)) if not same(v, basev)]
        else:
            moved = None
        if moved is not None and len(moved) == 1:
            mover = moved[0]
            keeper = B if mover == A else A
            dz_obj = float((av if mover == A else bv)[2]) - float(basev[2])
            ov = (ref(fk).get("cellOverrides") or {}).get(mover) or {}
            dzs = ov.get("dz") or []
            share = len([d for d in dzs if abs(float(d) - dz_obj) <= tol]) / len(dzs) if dzs else 0.0
            off = (ov.get("disabled") or 0) / ov["count"] if ov.get("count") else 0.0
            cell = (ref(fk).get("cells") or {}).get(mover)
            place = next((v.get("name") for v in (names.get("places") or {}).values() if v.get("formKey") == cell), None) \
                or words["structurePlace"].format(object=obj)
            if off >= policy["structureShare"]:
                who = words["structureReplaced"].format(mover=mod_name(mods, mover), keeper=mod_name(mods, keeper),
                                                        place=place, object=obj)
            elif dzs and share >= policy["structureShare"] and abs(dz_obj) > tol:
                who = words["structureUp" if dz_obj > 0 else "structureDown"].format(
                    mover=mod_name(mods, mover), keeper=mod_name(mods, keeper), place=place, object=obj)
            else:
                who = words["moverOne"].format(mover=mod_name(mods, mover), keeper=mod_name(mods, keeper), object=obj)
        elif moved is not None and len(moved) == 2:
            who = words["moverBoth"].format(a=mod_name(mods, A), b=mod_name(mods, B), object=obj)
        else:
            who = words["placementSplit"].format(loser=mod_name(mods, A), winner=mod_name(mods, B), object=obj,
                                                 mod=mod_name(mods, target))
        lost, won = position((r.get("values") or {}).get("lost")), position((r.get("values") or {}).get("winner"))
        text = who
        if lost and won:
            dx, dy, dz = (won[i] - lost[i] for i in range(3))
            dist = (dx * dx + dy * dy + dz * dz) ** 0.5
            mpu = policy["metresPerUnit"]
            if dist * mpu < 5:
                where = words["where"]["close"]
            elif abs(dz) >= 0.5 * dist:
                where = words["where"]["below" if dz < 0 else "above"].format(m=int(round(abs(dz) * mpu / 5.0) * 5))
            else:
                where = words["where"]["away"].format(m=int(round(dist * mpu / 5.0) * 5))
            sup = right_support(fk)
            extra = ""
            if sup:
                if sup.get("terrainAbove") and not sup.get("interior"):
                    extra = words["spotExtra"]["belowGround"]
                elif sup.get("down") is None:
                    extra = words["spotExtra"]["noFloor" if sup.get("interior") else "inAir"]
            text += " " + words["rightSpot"].format(mod=mod_name(mods, target), where=where) + extra + "."
        rest = [spoken_ref(names, x["rows"][0]["formKey"], x["rows"][0]["summary"]) for x in others.get(fk) or []]
        if rest:
            named = rest[:3]
            nm = ", ".join(named) if len(rest) > 3 else joined(named)
            if len(rest) > 3:
                text += " " + words["groupOthers"]["more"].format(names=nm, count=len(rest) - 3)
            else:
                text += " " + words["groupOthers"]["one" if len(rest) == 1 else "many"].format(names=nm)
        return text

    def sentence(c):
        r = c["rows"][0]
        if c["type"] == "record":
            return orient(c)
        s = slots(c)
        if c["type"] == "state":
            return words["stateClash"].format(condition=condition_words(r, names, switches, s["place"], words), **{
                k: v for k, v in s.items() if k != "place"})
        return words["overlap"].format(**s)

    game = {(m.get("subject") or {}).get("formKey") for m in pres.get("media") or []
            if m.get("assessed") and m.get("mediaKind") == "game-video"}
    counts = {t: len([c for c in clashes.values() if c["type"] == t]) for t in policy["typeOrder"]
              if any(c["type"] == t for c in clashes.values())}
    chapters = [{"kind": "opening", "narration": words["opening"].format(mod=mod_name(mods, target)),
                 "types": {words["typeLabels"][t]: n for t, n in counts.items()},
                 "leftOut": left_out, "leftMarkers": left_markers, "loadOrder": order,
                 "affected": sorted(affected_plugins(pres, policy, markers))}]
    accounting = {}
    for t in policy["typeOrder"]:
        chosen = chosen_by_type.get(t) or []
        accounting[t] = {"pictured": [c["rows"][0]["formKey"] for c in chosen],
                         "unpictured": [c["rows"][0]["formKey"] for c, _ in unpictured if c["type"] == t],
                         "represented": [c["rows"][0]["formKey"] for c in represented.get(t, [])]}
        for i, c in enumerate(chosen):
            text = sentence(c) + " " + patch_sentence(c)
            if i == 0:
                n, shown = counts[t], len(chosen)
                intro = words["cases"]["one" if n == 1 else "many"].format(count=n, label=words["caseLabels"][t])
                lead = ("" if n == 1 else words["lead"]["all"] if shown == n else
                        words["lead"]["one"] if shown == 1 else words["lead"]["some"].format(count=shown))
                text = " ".join(x for x in (intro, lead, text) if x)
            if any(r["formKey"] in game for r in c["rows"]):
                text += " " + words["playTime"]
            r = c["rows"][0]
            chapters.append({"kind": "split" if t == "record" else "vignette", "type": t, "formKey": r["formKey"],
                             "pair": list(c["pair"]), "narration": text, "highlight": highlight(c)})
    n_un = len(unpictured)
    if n_un:
        chapters.append({"kind": "unpictured", "count": n_un,
                         "narration": words["unpictured"]["one" if n_un == 1 else "many"].format(count=n_un),
                         "clashes": [(c["rows"][0]["formKey"], c["type"], why) for c, why in unpictured]})
    chapters.append({"kind": "closing", "narration": words["closing"],
                     "checks": [(m["reason"], m["subjects"]) for m in merged_checks(pres, mods, words)]})
    chapters[0]["accounting"] = accounting
    chapters[0]["candidates"] = candidates
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
                        "rows": [r for p in pages for r in p.get("rows") or []] + list(ch.get("clashes") or []),
                        "count": ch.get("count")})
        return out
    out = []
    for ch in pres.get("chapters") or []:
        for cap in ch.get("captions") or []:
            out.append({"kind": {"finding": "clash", "comparison": "clash"}.get(ch.get("kind"), ch.get("kind")),
                        "narration": " ".join(str(cap.get("narration") or "").split()), "pages": [], "frames": [],
                        "clash": {}, "patch": {}, "patches": [], "rows": [], "count": None})
    return out


def report_sentences(pres):
    r = pres.get("reader") or {}
    xs = [r.get("verdict"), r.get("scopeLine"), pres.get("claimSentence")] + list(r.get("limitations") or [])
    xs += [f.get("readerPlain") for f in pres.get("findings") or []] + [f.get("readerProvenance") for f in pres.get("findings") or []]
    return [" ".join(str(x).split()) for x in xs if x and len(str(x)) >= 20]


PICTURED = ("vignette", "split")


# ------------------------------------------------------------------------------------------ the rule checks
def rule_checks(pres: dict, view: list[dict], words=WORDS, policy=POLICY, tag="", mods=None):
    pred = (pres.get("reader") or {}).get("prediction") or {}
    target = pred.get("subject") or ""
    reel = pres.get("reel") or {}
    mods = mods if mods is not None else (reel.get("modNames") or {})
    narr = [c["narration"] for c in view]
    clash_scenes = [c for c in view if c["kind"] in PICTURED + ("data-still", "clash")]
    # 1 opening still
    op = view[0] if view else {}
    layouts = [p.get("layout") for p in op.get("pages") or []]
    order = next((a.get("locationPluginOrder") for a in (pres.get("scope") or {}).get("arms") or []
                  if a.get("armId") == pred.get("predictionArm")), None)
    lo = next((p for p in op.get("pages") or [] if p.get("layout") == "load-order"), {})
    check("1-opening", op.get("kind") == "opening" and layouts[:2] == ["types", "load-order"]
          and bool(mod_name(mods, target)) and op.get("narration", "").startswith(words["opening"].format(mod=mod_name(mods, target)))
          and op.get("narration") == _pages_say(op.get("pages"))
          and lo.get("plugins") == order and lo.get("title") == words["stillTitles"]["loadOrder"]
          and lo.get("legend") == words["stillTitles"]["affected"],
          f"{tag}the opening is a two-page still (clash types, then 'Plugins analysed' with the '(affected)' legend) with "
          f"only the fixed sentence (narration {op.get('narration', '')[:90]!r}, pages {layouts}, load-order title "
          f"{lo.get('title')!r}, legend {lo.get('legend')!r})")
    # N2: the mod being added and every plugin of a counted clash drawn in the highlight colour, on the drawn page
    want_aff = affected_plugins(pres, policy, set(reel.get("markers") or [])) if pred.get("record") is not None else set()
    got_aff = set(lo.get("affected") or [])
    styles = {}
    try:
        import reel_stills
        for style, text, _ in reel_stills._blocks(lo):
            m = re.match(r"^\s*\d+\s+(.+)$", text)
            if m:
                styles[m.group(1)] = style
        legend_marked = any(style == "mark" and text == words["stillTitles"]["affected"] for style, text, _ in reel_stills._blocks(lo))
    except Exception as exc:  # noqa: BLE001 - a still that cannot be drawn is a failed check, recorded below
        legend_marked = False
        styles = {"<draw failed>": str(exc)}
    wrong = sorted(p for p in order or [] if (styles.get(p) == "mark") != (p in want_aff))
    check("N2-affected", bool(want_aff) and got_aff == want_aff and not wrong and legend_marked,
          f"{tag}the load-order page marks exactly the mod being added and the plugins of counted clashes, and draws its "
          f"'(affected)' legend in the same colour (want {sorted(want_aff)}, page {sorted(got_aff)}, drawn wrongly {wrong[:4]}, "
          f"legend marked {legend_marked})")
    # 2 plain wording: no report sentence, no internal term
    leaked = sorted({s[:60] for s in report_sentences(pres) for n in narr if s in n})
    bad = sorted({w for w in words["forbidden"] for n in narr if w.lower() in n.lower()}
                 | {m.group(0) for pat in words["forbiddenPatterns"] for n in narr for m in re.finditer(pat, n)})
    check("2-wording", any(narr) and not leaked and not bad,
          f"{tag}no report sentence and no internal term is spoken (narrated scenes {sum(1 for n in narr if n)}; an empty reel has "
          f"nothing to check and fails; report sentences {leaked[:3]}, terms {bad})")
    # 3 vignettes: without -> with -> highlight; a placement split shows left then right
    vig = [c for c in view if c["kind"] == "vignette"]
    spl = [c for c in view if c["kind"] == "split"]
    shapes = [[f.get("state") for f in c["frames"]] for c in vig]
    sshapes = [[f.get("state") for f in c["frames"]] for c in spl]
    check("3-vignettes", bool(vig) and all(s == policy["sequence"] for s in shapes)
          and all(s == ["left", "right"] for s in sshapes)
          and all(len({f.get("mediaId") for f in c["frames"] if f.get("mediaId")}) == len([f for f in c["frames"] if f.get("mediaId")]) for c in vig + spl),
          f"{tag}every vignette shows {policy['sequence']} once and every split shows left then right, one narration each "
          f"({len(vig)} vignettes, shapes {shapes[:3]}; {len(spl)} splits, shapes {sshapes[:2]})")
    # 4 patches: every clash scene ends with a patch sentence; a named patch is that pair's patch
    pat = [re.escape(v).replace(r"\{patch\}", ".+?").replace(r"\{kept\}", ".+?").replace(r"\{file\}", ".+?")
           for vs in words["patch"].values() for v in (vs if isinstance(vs, list) else [vs])]
    no_patch = [c["narration"][:60] for c in clash_scenes if not any(re.search(p, c["narration"]) for p in pat)]
    foreign = []
    for c in clash_scenes:
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
    # 6 closing still: the checks that could not run or decide, and nothing else (capture refusals are counted, Gate A2 Q4)
    cl = view[-1] if view else {}
    want = [(m.get("reason"), list(m.get("subjects") or [])) for m in merged_checks(pres, mods, words)]
    got = [(r.get("reason"), list(r.get("subjects") or [])) for p in cl.get("pages") or [] if p.get("layout") == "checks"
           for r in p.get("rows") or []]
    said_ = {s for _, subs in got for s in subs}
    check("6-closing", cl.get("kind") == "closing" and all(s in said_ for _, subs in want for s in subs)
          and cl.get("narration") == _pages_say(cl.get("pages")),
          f"{tag}the closing is a still listing each check that could not run or decide with its reason and nothing else, "
          f"and says only the fixed sentence (narration {cl.get('narration', '')[:90]!r}, {len(got)} rows, {len(want)} expected)")
    # C1: no still speaks the tool: no form key, no "request", no setup role label
    labels = {a.get("displayLabel") for a in (pres.get("scope") or {}).get("arms") or [] if a.get("displayLabel")}
    labels |= {"Measured load order", "Tested alternative setup"}
    import reel_stills                                     # the text a still actually draws, not its page data
    still_text = " | ".join(line for c in view for pg in c["pages"] for _, line, _ in reel_stills._blocks(pg))
    fk_hits = re.findall(r"\b[0-9A-Fa-f]{6}:[^\s\"]+?\.(?:esp|esm|esl)\b", still_text)
    bad_lbl = sorted(x for x in labels if x in still_text)
    check("C1-stills-plain", bool(view) and any(c["pages"] for c in view) and not fk_hits
          and "request" not in still_text.lower() and not bad_lbl,
          f"{tag}no still shows a form key, a capture request or a setup role label (form keys {fk_hits[:2]}, labels {bad_lbl})")
    # N3: no data stills; one statement of what cannot be pictured, after the pictured scenes, before the closing
    data = [c["kind"] for c in view if c["kind"] in ("data-still",)] + [p.get("layout") for c in view for p in c["pages"]
                                                                        if p.get("layout") == "rows"]
    refusal_rows = [s for p in cl.get("pages") or [] for r in p.get("rows") or [] for s in r.get("subjects") or []
                    if " and " in s and " in " in s and "camera" in str(r.get("reason")).lower()]
    check("N3-no-data-stills", bool(view) and not data and not refusal_rows,
          f"{tag}no clash is shown as a data still and the closing lists no capture refusal ({data[:3]}, {refusal_rows[:2]})")
    acc = reel.get("accounting") or {}
    n_un = sum(len((acc.get(t) or {}).get("unpictured") or []) for t in acc)
    un = [i for i, c in enumerate(view) if c["kind"] == "unpictured"]
    last_pic = max([i for i, c in enumerate(view) if c["kind"] in PICTURED], default=0)
    if un:
        u = view[un[0]]
        form = words["unpictured"]["one" if u.get("count") == 1 else "many"].format(count=u.get("count"))
        drawn = " ".join(line for pg in u["pages"] for _, line, _ in reel_stills._blocks(pg) if line)
        ok_un = (len(un) == 1 and u.get("count") == n_un == len(u["rows"]) and u["narration"] == form
                 and form in drawn and last_pic < un[0] == len(view) - 2)
    else:
        u, form, ok_un = {}, None, bool(acc) and n_un == 0
    check("N3-unpictured", ok_un,
          f"{tag}every clash a picture cannot show is counted once in one sentence, spoken and on screen, singular for one, "
          f"after the pictured scenes and before the closing (scenes {len(un)}, count {u.get('count')}, accounted {n_un}, "
          f"narration {u.get('narration', '')[:80]!r}, want {form!r})")
    # N3-partition: every count on the opening is pictured, unpictured or represented, exactly once
    tp0 = next((p for p in op.get("pages") or [] if p.get("layout") == "types"), {})
    lbl_type = {v: k for k, v in words["typeLabels"].items()}
    off = []
    for x in tp0.get("rows") or []:
        t = lbl_type.get(x.get("label"))
        a_ = acc.get(t) or {}
        sets = [list(a_.get(k) or []) for k in ("pictured", "unpictured", "represented")]
        flat = [fk for s_ in sets for fk in s_]
        # accounted per clash (its object and partner), so a pictured clash cannot also be counted unpictured
        scenes = ["|".join([str(c["clash"].get("formKey")), *map(str, c["clash"].get("other") or [])])
                  for c in view if c["kind"] in PICTURED and c["clash"].get("type") == t]
        if x.get("count") != len(flat) or len(set(flat)) != len(flat) or sorted(sets[0]) != sorted(scenes):
            off.append((x.get("label"), x.get("count"), [len(s_) for s_ in sets], len(scenes)))
    check("N3-partition", bool(tp0.get("rows")) and bool(acc) and not off,
          f"{tag}each opening count equals its pictured, unpictured and represented clashes, each clash once, and the "
          f"pictured ones are the scenes (mismatches: {off[:3]})")
    # N6: the first pictured scene of each type says how many cases there are, with the opening's count
    counts = {lbl_type.get(x.get("label")): x.get("count") for x in tp0.get("rows") or []}
    first = {}
    for c in view:
        if c["kind"] in PICTURED:
            first.setdefault(c["clash"].get("type"), c)
    bad6, bad_n = [], []
    for t, c in first.items():
        n = counts.get(t)
        want6 = words["cases"]["one" if n == 1 else "many"].format(count=n, label=words["caseLabels"].get(t, "?"))
        if not c["narration"].startswith(want6):
            bad6.append((t, c["narration"][:70]))
        m = re.match(r"^There (?:are|is) (\d+) cases? of ", c["narration"])
        if not m or int(m.group(1)) != n:
            bad_n.append((t, m.group(1) if m else None, n))
    check("N6-cases", bool(first) and not bad6,
          f"{tag}the first pictured scene of each type opens with 'There are N cases of ...' ({bad6[:2]})")
    check("N6-count-agree", bool(first) and not bad_n,
          f"{tag}each spoken case count equals the opening still's count for that type (type, spoken, opening: {bad_n[:3]})")
    # N8: one pictured scene per problem (type, mod pair, cell, and a record clash's field)
    keys = []
    for c in view:
        if c["kind"] in PICTURED:
            cl = c["clash"]
            cell = (cl.get("cells") or {}).get("left") if cl.get("type") == "record" else cl.get("place")
            keys.append((cl.get("type"), tuple(cl.get("pair") or []), cell or cl.get("formKey"), cl.get("checkId")))
    dup = sorted({k for k in keys if keys.count(k) > 1}, key=str)
    check("N8-once", bool(keys) and not dup, f"{tag}no two pictured scenes are the same problem ({dup[:2]})")
    # F2 no in-game effect
    no_eff = set(policy["noInGameEffectFields"])
    shown = [r for c in view for r in c["rows"] if r.get("field") in no_eff]
    spoken = [c["narration"][:60] for c in view if any(f in c["narration"] for f in no_eff)]
    rows = [r for r in pred.get("record") or [] if r.get("admission") == "admitted"]
    left = len([r for r in rows if r.get("field") in no_eff])
    counted = sum(int(n) for v in pred.get("verdictCounts") or [] for f, n in (v.get("fieldNames") or {}).items() if f in no_eff)
    tp = tp0
    marks = set(reel.get("markers") or [])
    rec_want = len({r["formKey"] for r in rows if (policy["types"].get(r.get("checkId")) == "record")
                    and r.get("field") not in no_eff and not ({r["formKey"], *(r.get("other") or [])} & marks)})
    rec_got = next((x.get("count") for x in tp.get("rows") or [] if x.get("label") == words["typeLabels"]["record"]), 0)
    check("F2-no-effect", counted == left and rec_got == rec_want and not shown and not spoken
          and tp.get("leftOut", {}).get("noInGameEffect") == left
          and not any(f in json.dumps(tp.get("rows") or []) for f in no_eff),
          f"{tag}fields with no in-game effect are left out of scenes and counts, and the number left out "
          f"({left} admitted rows; {counted} in verdictCounts.fieldNames) is on the opening still; record clashes counted "
          f"{rec_got}, {rec_want} expected (shown {len(shown)}, spoken "
          f"{spoken[:2]}, still says {tp.get('leftOut')})")


def _page_say(page):
    import reel_stills
    return reel_stills.page_say(page)


def _pages_say(pages):
    return " ".join(x for x in (_page_say(p) for p in pages or []) if x)


def oracle_checks(pres, view, expected, tag=""):
    got = [(c["kind"], c["clash"].get("type") if c["kind"] in PICTURED else None,
            c["clash"].get("formKey") if c["kind"] in PICTURED else None) for c in view]
    want = [(e["kind"], e.get("type") if e["kind"] in PICTURED else None,
             e.get("formKey") if e["kind"] in PICTURED else None) for e in expected]
    check("3-selection", got == want, f"{tag}the selection and order follow the rule (got {got}, want {want})")
    # a still says its pages' own words (the opening's lead first); the other scenes the oracle's sentences exactly
    diffs = [(i, g["narration"], e["narration"]) for i, (g, e) in enumerate(zip(view, expected))
             if (not g["narration"].startswith(e["narration"]) if g["kind"] == "opening" else
                 g["narration"] != _pages_say(g.get("pages")) if g["kind"] == "closing" else
                 g["narration"] != e["narration"])]
    check("sentences", len(view) == len(expected) and not diffs,
          f"{tag}every sentence is the skill template filled with the evaluation's names (first difference: {diffs[:1]})")
    op = view[0] if view else {}
    tp = next((p for p in op.get("pages") or [] if p.get("layout") == "types"), {})
    rows = {r.get("label"): r.get("count") for r in tp.get("rows") or []}
    check("1-counts", rows == expected[0]["types"],
          f"{tag}the opening still's counts per clash type are the prediction's, without no-effect fields (got {rows}, want {expected[0]['types']})")
    hl = [(c["clash"].get("formKey"), list(c["clash"].get("highlight") or [])) for c in view if c["kind"] in PICTURED]
    hl_want = [(e["formKey"], e["highlight"]) for e in expected if e["kind"] in PICTURED]
    check("Q6-highlight", hl == hl_want, f"{tag}each pictured clash marks the participants the volume rule names (got {hl}, want {hl_want})")
    un = next((c for c in view if c["kind"] == "unpictured"), None)
    un_want = next((e for e in expected if e["kind"] == "unpictured"), None)
    got_un = sorted((r.get("formKey"), r.get("type"), r.get("reason")) for r in (un or {}).get("rows") or [])
    want_un = sorted(un_want["clashes"]) if un_want else []
    check("N3-which", got_un == want_un, f"{tag}the clashes counted as unpictured, and why, follow the rule (got {got_un}, want {want_un})")
    cand = (pres.get("reel") or {}).get("captureCandidates")
    check("N3-candidates", cand == expected[0]["candidates"],
          f"{tag}the plan lists, per pictured type, the clashes capture may try in order, at most captureAttemptsPerType "
          f"(got {cand}, want {expected[0]['candidates']})")


# ------------------------------------------------------------------------------------------ the expected reel (reference)
def reference_presentation(pres, names, masters, search, mods, captures=None):
    """The expected reel written out in the planner's output shape, from the oracle: shows every check can pass."""
    exp = oracle(pres, names, masters, search, mods, captures=captures)
    target = pres["reader"]["prediction"]["subject"]
    out = copy.deepcopy(pres)
    chs = []
    for e in exp:
        if e["kind"] == "opening":
            chs.append({"kind": "opening", "narration": e["narration"], "still": {"pages": [
                {"layout": "types", "rows": [{"label": k, "count": v} for k, v in e["types"].items()],
                 "leftOut": {"noInGameEffect": e["leftOut"], "markers": e["leftMarkers"]}},
                {"layout": "load-order", "title": WORDS["stillTitles"]["loadOrder"], "legend": WORDS["stillTitles"]["affected"],
                 "plugins": e["loadOrder"], "targets": [target], "affected": e["affected"]}]}})
        elif e["kind"] in PICTURED:
            own = [p for p in pres["reader"]["suppliedPatches"] if set(x.lower() for x in e["pair"]) <= masters.get(p.lower(), set())]
            states = POLICY["sequence"] if e["kind"] == "vignette" else ["left", "right"]
            chs.append({"kind": e["kind"], "narration": e["narration"],
                        "clash": {"type": e["type"], "formKey": e["formKey"], "pair": e["pair"], "highlight": e["highlight"]},
                        "frames": [{"state": s, "mediaId": f"M-{e['formKey'][:6]}-{s}"} for s in states],
                        "patch": {"patch": own[0], "pair": e["pair"], "masters": sorted(masters[own[0].lower()])} if own and "Get " in e["narration"] else {}})
        elif e["kind"] == "unpictured":
            chs.append({"kind": "unpictured", "narration": e["narration"], "count": e["count"],
                        "clashes": [{"formKey": fk, "type": t, "reason": why} for fk, t, why in e["clashes"]],
                        "still": {"pages": [{"layout": "statement", "text": e["narration"]}]}})
        else:
            chs.append({"kind": "closing", "narration": e["narration"], "still": {"pages": [
                {"layout": "checks", "rows": [{"reason": r, "subjects": s} for r, s in e["checks"]]}]}})
    out["reel"] = {"schemaVersion": 1, "status": "planned", "modNames": mods, "markers": sorted(MARKERS), "chapters": chs,
                   "accounting": exp[0]["accounting"], "captureCandidates": exp[0]["candidates"]}
    return out


# ------------------------------------------------------------------------------------------ sabotage arms
def sabotage(pres: dict, name: str) -> tuple[dict, str]:
    """Break one rule in a planned reel. Returns the broken presentation and the check that must fail."""
    p = copy.deepcopy(pres)
    chs = p["reel"]["chapters"]
    vig = [c for c in chs if c["kind"] == "vignette"]
    pic = [c for c in chs if c["kind"] in PICTURED]
    unp = next((c for c in chs if c["kind"] == "unpictured"), None)
    lo = next(pg for pg in chs[0]["still"]["pages"] if pg.get("layout") == "load-order")
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
        unp["clashes"].append({"formKey": "000404:Skyrim.esm", "type": "record", "reason": "record", "field": "EditorID"})
        return p, "F2-no-effect"
    if name == "formkey-on-still":
        chs[-1]["still"]["pages"][0]["rows"].append({"reason": "ran, but could not decide",
                                                     "subjects": ["027582:Lux.esp shows a measurable face"]})
        return p, "C1-stills-plain"
    if name == "leftout-zero":                             # a reel that claims nothing was left out beside an EditorID clash
        chs[0]["still"]["pages"][0]["leftOut"]["noInGameEffect"] = 0; return p, "F2-no-effect"
    if name == "empty-reel":                               # nothing narrated: the wording and disclaimer checks must not pass
        for c in chs:
            c["narration"] = ""
        return p, "2-wording"
    # phase 2
    if name == "affected-drop":
        lo["affected"] = [x for x in lo["affected"] if x != "Beta.esp"]; return p, "N2-affected"
    if name == "old-title":
        lo["title"] = "Plugins analysed, in load order"; return p, "1-opening"
    if name == "unpictured-off":
        unp["count"] += 1
        unp["narration"] = WORDS["unpictured"]["many"].format(count=unp["count"])
        unp["still"]["pages"][0]["text"] = unp["narration"]; return p, "N3-unpictured"
    if name == "unpictured-plural":
        one = next(c for c in chs if c["kind"] == "unpictured")
        keep = one["clashes"][:1]
        dropped = {x["formKey"] for x in one["clashes"][1:]}
        one["clashes"], one["count"] = keep, 1
        one["narration"] = WORDS["unpictured"]["many"].format(count=1)
        one["still"]["pages"][0]["text"] = one["narration"]
        for t, a_ in p["reel"]["accounting"].items():
            a_["unpictured"] = [fk for fk in a_["unpictured"] if fk not in dropped]
        return p, "N3-unpictured"
    if name == "data-still-back":
        chs.insert(len(chs) - 2, {"kind": "data-still", "narration": "Beta Mod replaces Target Mod's settings. No patch for these two mods was found.",
                                  "clash": {"type": "cell"}, "still": {"pages": [{"layout": "rows", "rows": [{"formKey": "000501:Skyrim.esm"}]}]}})
        return p, "N3-no-data-stills"
    if name == "refusal-on-closing":
        chs[-1]["still"]["pages"][0]["rows"].append({"reason": "No camera could see both objects",
                                                     "subjects": ["Giant Root and Shadow in Pelagius Wing"]})
        return p, "N3-no-data-stills"
    if name == "split-swapped":
        s = next(c for c in chs if c["kind"] == "split")
        s["frames"] = list(reversed(s["frames"])); return p, "3-vignettes"
    if name == "spoken-shown-count":
        for c in pic:
            m = re.match(r"^There (?:are|is) (\d+) cases? of ", c["narration"])
            if m and int(m.group(1)) > 1:
                c["narration"] = c["narration"].replace(m.group(0), f"There are 1 cases of ", 1)
                break
        return p, "N6-count-agree"
    if name == "no-cases":
        for c in pic:
            c["narration"] = re.sub(r"^There (?:are|is) \d+ cases? of [^.]+\. (?:Here[^.]+\. )?", "", c["narration"])
        return p, "N6-cases"
    if name == "represented-lost":
        for a_ in p["reel"]["accounting"].values():
            if a_["represented"]:
                a_["represented"].pop(); break
        return p, "N3-partition"
    if name == "both-marked":
        st = next(c for c in chs if c["kind"] == "vignette" and c["clash"].get("type") == "state")
        st["clash"]["highlight"] = ["000601:Target.esp", "000602:Beta.esp"]; return p, "Q6-highlight"
    raise SystemExit(f"unknown sabotage arm {name}")


SABOTAGE = ["verdict-opening", "report-sentence", "hnh", "drop-pair", "patch-any-pair", "compared", "disclaimer",
            "closing-narrated", "editorid", "leftout-zero", "empty-reel", "formkey-on-still",
            "affected-drop", "old-title", "unpictured-off", "unpictured-plural", "data-still-back", "refusal-on-closing",
            "split-swapped", "spoken-shown-count", "no-cases", "represented-lost", "both-marked"]


# ------------------------------------------------------------------------------------------ the planner cases
def planned(pres, names, masters, search, mods, policy=POLICY, words=WORDS, captures=None, switches=None):
    """The planner's reel for an evaluation (`reel_plan.plan_reel`), given the switch census and capture results.
    An older planner that cannot read the newer words or arguments is called with what it can read -- each phrasing
    pool's first sentence, the shipped words at that head, no switch census -- so its actual output fails the newer
    checks by assertion rather than by a crash."""
    try:
        import reel_plan  # noqa: F401
    except ImportError:
        print("  NOTE  no reel planner on this head (module reel_plan absent): checking the reel the base renders")
        return copy.deepcopy(pres)
    out = copy.deepcopy(pres)
    switches = SWITCHES if switches is None else switches
    shipped = json.loads((SKILL / "references/report-priorities.json").read_text(encoding="utf-8"))["reel"]["words"]
    first = {**words, "patch": {k: (v[0] if isinstance(v, list) else v) for k, v in words["patch"].items()}}
    merged = {**shipped, **{k: ({**shipped.get(k, {}), **v} if isinstance(v, dict) else v) for k, v in first.items()}}
    attempts = [("as given", words, {"switches": switches}), ("each pool's first phrasing", first, {"switches": switches}),
                ("no switch census, the shipped words overlaid", merged, {})]
    last = None
    for label, w, kw in attempts:
        try:
            got = reel_plan.plan_reel(out, names, masters, policy, w, search, mods, captures or [], markers=MARKERS, **kw)
        except Exception as exc:  # noqa: BLE001 - try what an older planner can read
            last = exc
            continue
        if got.get("status") != "planned" and "patch" in str(got.get("reason")) and label == "as given":
            last = ValueError(got.get("reason"))
            continue
        if label != "as given":
            print(f"  NOTE  this planner cannot read the newer words or arguments ({type(last).__name__}): planned with {label}")
        out["reel"] = got
        return out
    print(f"  NOTE  the planner raised ({type(last).__name__}: {last}); checking an empty reel")
    out["reel"] = {"status": "refused", "reason": str(last), "chapters": []}
    return out


def case_planner():
    pres, names, masters, search, mods = synthetic()
    got = planned(pres, names, masters, search, mods)
    rule_checks(got, reel_view(got), tag="synthetic: ", mods=mods)
    oracle_checks(got, reel_view(got), oracle(pres, names, masters, search, mods), tag="synthetic: ")
    # F1 (Gate A review): the Beta overlap, a pair the supplied patch does not master, never names that patch
    beta = [c for c in reel_view(got) if c["kind"] == "vignette" and "Beta.esp" in (c["clash"].get("pair") or [])]
    check("F1-own-pair", bool(beta) and all("Alpha - Target patch" not in c["narration"] for c in beta)
          and all(says(c["narration"], "none") for c in beta),
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
          and any(says(x, "notFound") for x in texts),
          "a patch search that did not find its own known patch says a patch was not found, never that none exists")
    # a search file that states found=true with no hit behind it is not believed
    ks3 = copy.deepcopy(ks); ks3["knownAnswer"]["found"] = True
    gf = planned(kp, kn, km, ks3, kmods)
    texts3 = [c["narration"] for c in reel_view(gf)]
    check("known-answer-derived", bool(texts3) and not any("No patch exists" in x for x in texts3)
          and any(says(x, "notFound") for x in texts3),
          "a search file stating found=true with no matching hit still says a patch was not found")
    ks2 = copy.deepcopy(ks); ks2["knownAnswer"]["hits"] = [{"file": "Alpha - Target patch"}]
    ks2["pairs"][1]["unreadSources"] = [{"source": "a page description", "why": "cut short"}]
    gu = planned(kp, kn, km, ks2, kmods)
    beta = [c["narration"] for c in reel_view(gu) if c["kind"] == "vignette" and "Beta.esp" in (c["clash"].get("pair") or [])]
    check("unread-source", bool(beta) and all(says(x, "notFound") for x in beta),
          "a pair with an unread source says a patch was not found, even when the known answer was found")
    # C3a: a clash on an editor-only marker is left out of the counts, and how many is on the opening still
    op_ = next((c for c in reel_view(got) if c["kind"] == "opening"), {})
    lo_ = next((p_ for p_ in op_.get("pages") or [] if p_.get("layout") == "types"), {}).get("leftOut") or {}
    rec_n = next((x.get("count") for p_ in op_.get("pages") or [] if p_.get("layout") == "types" for x in p_.get("rows") or []
                  if x.get("label") == WORDS["typeLabels"]["record"]), None)
    check("C3-markers", lo_.get("markers") == 1 and rec_n == 3 and "000405" not in json.dumps(reel_view(got)),
          f"a marker's clash is out of the scenes and counts (record clashes {rec_n}, 3 expected), with the number left "
          f"out on the opening still ({lo_})")
    # S2 / N4: an actor's placement is a split screen that says the two mods place the NPC in different spots
    ap, an, am, as_, amods = synthetic()
    an["bases"]["B5"] = {"type": "Npc", "editorId": "SomeGuard", "model": None}
    ga = planned(ap, an, am, as_, amods)
    text = " ".join(c["narration"] for c in reel_view(ga))
    check("S2-actor", "Beta Mod and Target Mod place Some Guard in different spots" in text and "moves Some Guard" not in text
          and " stands" not in text,
          "an NPC placement clash says the two mods place the NPC in different spots, never where it stands in play")
    oracle_checks(ga, reel_view(ga), oracle(ap, an, am, as_, amods), tag="actor: ")
    # N4: a placement split takes its left half from the setup without the mod and its right half from the prediction, with
    # the row's two placements and the other mod's name for each half's label
    sp_ = next((ch for ch in (got.get("reel") or {}).get("chapters") or [] if ch.get("kind") == "split"), {})
    fr = {f.get("state"): f.get("armId") for f in sp_.get("frames") or []}
    pl = (sp_.get("clash") or {}).get("placements") or {}
    lab = (sp_.get("clash") or {}).get("labels") or {}
    check("N4-split", (sp_.get("clash") or {}).get("formKey") == "000403:Skyrim.esm"
          and fr == {"left": "without-target", "right": "with-target"}
          and pl == {"left": [0.0, 0.0, 0.0], "right": [10.0, 0.0, 0.0]}
          and lab == {"left": "Without Target Mod", "right": "With Target Mod"},
          f"a drawn object's placement clash is a split: left from the setup without the mod at the other mod's position, "
          f"right from the prediction at the winner's, labelled (frames {fr}, placements {pl}, labels {lab})")
    # N4: when another plugin's version would win without the mod, the left half cannot show the other mod's spot
    lp, ln_, lm, ls, lmods = synthetic()
    ln_["refs"]["000403:Skyrim.esm"]["carriedBy"] = ["Skyrim.esm", "Beta.esp", "Alpha.esp", "Target.esp"]
    gl = planned(lp, ln_, lm, ls, lmods)
    oracle_checks(gl, reel_view(gl), oracle(lp, ln_, lm, ls, lmods), tag="left not the loser's: ")
    # N5: a world-state clash is pictured in its state; its condition is spoken by in-game names only for a trigger into
    # the clash state, otherwise the fallback; never an editor id
    st_ = next((c for c in reel_view(got) if c["kind"] == "vignette" and c["clash"].get("type") == "state"), None)
    check("N5-state", st_ is not None and [f.get("state") for f in st_["frames"]] == POLICY["sequence"]
          and (st_["clash"].get("state") == "000701:Skyrim.esm")
          and WORDS["conditionFallback"].format(place="The Camp") in st_["narration"]
          and not re.search(r"Camp ?Enable|Some ?Quest", st_["narration"]),
          f"a world-state clash is a vignette in its state (000701:Skyrim.esm) and names its condition without an editor id "
          f"({(st_ or {}).get('narration', '')[:120]!r}, state {(st_ or {}).get('clash', {}).get('state')!r})")
    qn = copy.deepcopy(names)
    qn["quests"] = {"SomeQuest": {"name": "The Camp Quest"}}
    sw_en = copy.deepcopy(SWITCHES)
    sw_en["000701:Skyrim.esm"]["trigger"]["by"] = [b.replace("Disable()", "Enable()") for b in sw_en["000701:Skyrim.esm"]["trigger"]["by"]]
    gq = planned(pres, qn, masters, search, mods, switches=sw_en)
    gd = planned(pres, qn, masters, search, mods)
    tq = " ".join(c["narration"] for c in reel_view(gq))
    td = " ".join(c["narration"] for c in reel_view(gd))
    check("N5-condition", WORDS["conditionQuest"].format(quest="The Camp Quest", stage="10") in tq
          and WORDS["conditionFallback"].format(place="The Camp") in td and "The Camp Quest" not in td,
          "a quest that switches the clash on is named with its stage; one that only switches it off is not")
    # ---------------------------------------------------------------- phase 3
    # N7: patch phrasings rotate through the pool, never the same one twice in a row; the same plan every time
    phr = [x for c in reel_view(got) for x in patch_phrasings(c["narration"])]
    rep_ = [(a, b) for a, b in zip(phr, phr[1:]) if a == b]
    check("N7-pool", len(phr) >= 3 and len({c for c, _ in phr}) >= 1 and not rep_
          and any(i > 0 for _, i in phr),
          f"no two consecutive patch sentences use the same phrasing, and the pool is used (phrasings {phr}, repeats {rep_})")
    again = planned(pres, names, masters, search, mods)
    check("N7-deterministic", json.dumps((again.get("reel") or {}).get("chapters"), sort_keys=True)
          == json.dumps((got.get("reel") or {}).get("chapters"), sort_keys=True),
          "two plans of one presentation are identical")
    # N8: placements of one pair in one cell are one problem: one split, the count said, the others named once
    gp, gn, gm, gs, gmods = synthetic_group()
    gg = planned(gp, gn, gm, gs, gmods)
    gv = reel_view(gg)
    spl = [c for c in gv if c["kind"] == "split"]
    rec_n = next((x.get("count") for c in gv[:1] for pg in c["pages"] if pg.get("layout") == "types"
                  for x in pg.get("rows") or [] if x.get("label") == WORDS["typeLabels"]["record"]), None)
    first = spl[0]["narration"] if spl else ""
    check("N8-group", len(spl) == 2 and first.startswith(WORDS["cases"]["many"].format(count=rec_n, label=WORDS["caseLabels"]["record"]))
          and WORDS["lead"]["some"].format(count=2) in first
          and WORDS["groupOthers"]["many"].format(names="Lamp Two and Lamp Three") in first,
          f"three placements of one pair in one cell are one split naming the other two, with the count said "
          f"({len(spl)} splits; {first[:220]!r})")
    check("N8-key", len(spl) == 2 and (spl[1]["clash"].get("formKey") == "000409:Skyrim.esm")
          and "placed the same way" not in spl[1]["narration"],
          f"a placement of the same pair in another cell is its own split ({[c['clash'].get('formKey') for c in spl]})")
    oracle_checks(gg, gv, oracle(gp, gn, gm, gs, gmods), tag="grouped: ")

    # N9: the split orients the viewer from data, and says less when the data is missing
    def orient_case(tag, mut, fk=None):
        op_, on_, om_, os_, omods_ = synthetic_group()
        mut(op_, on_)
        g_ = planned(op_, on_, om_, os_, omods_)
        v_ = reel_view(g_)
        oracle_checks(g_, v_, oracle(op_, on_, om_, os_, omods_), tag=f"{tag}: ")
        return next((c["narration"] for c in v_ if c["kind"] == "split"
                     and (fk is None or c["clash"].get("formKey") == fk)), "")
    t_up = orient_case("structure up", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"].update(
        cellOverrides={"Beta.esp": {"dz": [1424.0, 1424.0, 1420.0, 0.0]}}))
    check("N9-structure", "Beta Mod raises The Lamp Room and moves Lamp with it; Target Mod keeps the original spot." in t_up,
          f"when most of the mover's overrides in the cell shift by the object's height, the structure moved ({t_up[:200]!r})")
    t_off = orient_case("structure replaced", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"].update(
        cellOverrides={"Beta.esp": {"dz": [0.0], "disabled": 9, "count": 10}}))
    check("N9-structure-replaced", WORDS["structureReplaced"].format(mover="Beta Mod", keeper="Target Mod",
                                                                   place="The Lamp Room", object="Lamp") in t_off,
          f"when the mover turns off most of the original references in the cell, it replaced the structure ({t_off[:200]!r})")
    t_alone = orient_case("structure still", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"].update(
        cellOverrides={"Beta.esp": {"dz": [0.0, 0.0, 0.0, 1424.0]}}))
    check("N9-structure-still", "raises" not in t_alone and "Beta Mod moves Lamp; Target Mod keeps the original spot." in t_alone,
          f"an object moved alone never gets the structure clause ({t_alone[:200]!r})")
    t_who = orient_case("who", lambda pr, nm: None)
    check("N9-who", "Beta Mod moves Lamp; Target Mod keeps the original spot." in t_who,
          f"the mover is the plugin whose value differs from the base game's ({t_who[:160]!r})")
    t_both = orient_case("both", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"]["placements"].update({"Target.esp": [5.0, 5.0, 0.0]}))
    check("N9-who-both", WORDS["moverBoth"].format(a="Beta Mod", b="Target Mod", object="Lamp") in t_both,
          f"when both versions differ from the base game's, both moved it ({t_both[:160]!r})")
    t_where = orient_case("where", lambda pr, nm: None)
    check("N9-where", WORDS["rightSpot"].format(mod="Target Mod", where=WORDS["where"]["below"].format(m=20)) in t_where
          and ", where there is no floor." in t_where,
          f"the other spot's height in metres and the probe's empty space ({t_where[:260]!r})")

    def ground(pr, nm):
        for m in pr["media"]:
            m["provenance"]["support"] = {"down": None, "terrainAbove": True, "interior": False}
    t_ground = orient_case("ground", ground)
    check("N9-ground", WORDS["spotExtra"]["belowGround"] + "." in t_ground and "no floor" not in t_ground,
          f"an exterior spot under the terrain is below the ground ({t_ground[-160:]!r})")

    def floor(pr, nm):
        for m in pr["media"]:
            m["provenance"]["support"] = {"down": 64.0, "terrainAbove": False, "interior": True}
    t_floor = orient_case("floor", floor)
    check("N9-empty", "no floor" not in t_floor and "below the ground" not in t_floor and "in the air" not in t_floor,
          f"a floor under the spot adds nothing ({t_floor[-160:]!r})")
    t_silent = orient_case("silent", lambda pr, nm: pr.update(media=[]))
    check("N9-silent", "no floor" not in t_silent and "below the ground" not in t_silent and "in the air" not in t_silent
          and "metres below" in t_silent,
          f"with no probe data the empty-space clause is not said ({t_silent[-160:]!r})")
    t_close = orient_case("close", lambda pr, nm: [r.update(values={"lost": "Position=0,0,0; Rotation=0,0,0",
                                                                   "winner": "Position=100,0,0; Rotation=0,0,0"})
                                                  for r in pr["reader"]["prediction"]["record"] if r["formKey"] == "000403:Skyrim.esm"])
    check("N9-where-close", WORDS["where"]["close"] in t_close, f"a spot under 5 metres away is close by ({t_close[-160:]!r})")
    t_base = orient_case("no base", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"].pop("placements"))
    check("N9-no-base", "keeps the original spot" not in t_base and "in different spots" in t_base,
          f"without the base game's value the mover is not named ({t_base[:160]!r})")
    # (d) the base game's value alone is unreadable (the two mods' values are read): no mover, the spots still said
    t_unread = orient_case("base unreadable", lambda pr, nm: nm["refs"]["000403:Skyrim.esm"]["placements"].pop("Skyrim.esm"))
    check("N9-base-unreadable", "keeps the original spot" not in t_unread and " moves Lamp" not in t_unread
          and "in different spots" in t_unread and "metres below" in t_unread,
          f"with the base game's value unreadable the mover clause is dropped and the spots still said ({t_unread[:220]!r})")

    # (c) a new reference one of the two mods adds has no base-game value: the two spots, no mover clause
    def new_ref(pr, nm):
        old, new = "000403:Skyrim.esm", "000803:Beta.esp"
        pr["reader"]["prediction"]["record"] = [r for r in pr["reader"]["prediction"]["record"] if r["formKey"]
                                                not in ("000406:Skyrim.esm", "000407:Skyrim.esm", "000409:Skyrim.esm")]
        for r in pr["reader"]["prediction"]["record"]:
            if r["formKey"] == old:
                r["formKey"] = new
        for m in pr["media"]:
            if (m.get("subject") or {}).get("formKey") == old:
                m["subject"]["formKey"] = new
        e = nm["refs"].pop(old)
        e["carriedBy"] = ["Beta.esp", "Target.esp"]
        e["cells"] = {"Beta.esp": "000A01:Skyrim.esm", "Target.esp": "000A01:Skyrim.esm"}
        e["placements"] = {"Beta.esp": [0.0, 0.0, 1424.0], "Target.esp": [0.0, 0.0, 0.0]}
        nm["refs"][new] = e
    t_new = orient_case("new reference", new_ref, fk="000803:Beta.esp")
    check("N9-new-ref", t_new != "" and "keeps the original spot" not in t_new and " moves Lamp" not in t_new
          and "in different spots" in t_new and "metres" in t_new,
          f"a reference one of the mods adds has no base-game spot: the two spots, no mover ({t_new[:220]!r})")

    # the remaining structure and where branches: lowered with the structure; the other spot above; beside
    def lowered(pr, nm):
        nm["refs"]["000403:Skyrim.esm"]["placements"]["Beta.esp"] = [0.0, 0.0, -1424.0]
        nm["refs"]["000403:Skyrim.esm"]["cellOverrides"] = {"Beta.esp": {"dz": [-1424.0, -1424.0, -1420.0, 0.0]}}
        for r in pr["reader"]["prediction"]["record"]:
            if r["formKey"] == "000403:Skyrim.esm":
                r["values"] = {"lost": "Position=0,0,-1424; Rotation=0,0,0", "winner": "Position=0,0,0; Rotation=0,0,0"}
    t_down = orient_case("structure down", lowered)
    check("N9-structure-down", WORDS["structureDown"].format(mover="Beta Mod", keeper="Target Mod", place="The Lamp Room",
                                                             object="Lamp") in t_down
          and WORDS["where"]["above"].format(m=20) in t_down,
          f"a mover that lowers the structure lowers it, and the kept spot is above ({t_down[:300]!r})")
    t_away = orient_case("away", lambda pr, nm: [r.update(values={"lost": "Position=1424,0,0; Rotation=0,0,0",
                                                                  "winner": "Position=0,0,0; Rotation=0,0,0"})
                                                 for r in pr["reader"]["prediction"]["record"] if r["formKey"] == "000403:Skyrim.esm"])
    check("N9-where-away", WORDS["where"]["away"].format(m=20) in t_away and "below" not in t_away and "above" not in t_away,
          f"a spot mostly beside the other is away from it, not above or below ({t_away[-200:]!r})")

    # the group's other members: one named alone; more than three named three and counted
    t_one = orient_case("others one", lambda pr, nm: pr["reader"]["prediction"].update(
        record=[r for r in pr["reader"]["prediction"]["record"] if r["formKey"] != "000407:Skyrim.esm"]))
    check("N8-others-one", WORDS["groupOthers"]["one"].format(names="Lamp Two") in t_one,
          f"one other member is named in the singular ({t_one[-160:]!r})")

    def five_more(pr, nm):
        rec_ = pr["reader"]["prediction"]["record"]
        lamp = next(r for r in rec_ if r["formKey"] == "000403:Skyrim.esm")
        for i in range(3):
            fk, edid = f"00041{i}:Skyrim.esm", f"LampX{i}"
            rec_.append({**lamp, "formKey": fk, "findingId": "F-" + fk[:6],
                         "summary": f"PlacedObject {edid}: Placement - Beta.esp's value lost to Target.esp's"})
            nm["refs"][fk] = {"base": "B-" + edid, "carriedBy": ["Skyrim.esm", "Beta.esp", "Target.esp"],
                              "cells": {"Skyrim.esm": "000A01:Skyrim.esm", "Beta.esp": "000A01:Skyrim.esm",
                                        "Target.esp": "000A01:Skyrim.esm"}}
            nm["bases"]["B-" + edid] = {"type": "Static", "editorId": edid, "model": f"Clutter\\{edid}.nif"}
            nm["objects"][edid] = {"name": f"Lamp X{i}"}
    t_more = orient_case("others more", five_more)
    check("N8-others-more", t_more.count(" and 2 more are placed the same way.") == 1 and "Lamp X2" not in t_more
          and ", Lamp Three, Lamp X0 and 2 more are placed the same way." in t_more,
          f"five other members: three named, two counted ({t_more[-200:]!r})")

    # exterior: nothing under the spot and no terrain above it is in the air; an interior never gets the ground clause
    def air(pr, nm):
        for m in pr["media"]:
            m["provenance"]["support"] = {"down": None, "terrainAbove": False, "interior": False}
    t_air = orient_case("air", air)
    check("N9-air", WORDS["spotExtra"]["inAir"] + "." in t_air and "no floor" not in t_air and "below the ground" not in t_air,
          f"an exterior spot with nothing under it is in the air ({t_air[-160:]!r})")

    def ground_interior(pr, nm):
        for m in pr["media"]:
            m["provenance"]["support"] = {"down": None, "terrainAbove": True, "interior": True}
    t_gi = orient_case("ground interior", ground_interior)
    check("N9-ground-interior", "below the ground" not in t_gi and WORDS["spotExtra"]["noFloor"] + "." in t_gi,
          f"an interior has no map: never below the ground, only no floor ({t_gi[-160:]!r})")

    # N3: one clash the reel cannot picture is said in the singular
    op1, on1, om1, os1, omods1 = synthetic()
    op1["reader"]["prediction"]["record"] = [r for r in op1["reader"]["prediction"]["record"]
                                             if r["formKey"] not in ("000106:Target.esp", "000401:Skyrim.esm", "000402:Skyrim.esm")]
    g1_ = planned(op1, on1, om1, os1, omods1)
    un1 = [c for c in reel_view(g1_) if c["kind"] == "unpictured"]
    check("N3-singular", len(un1) == 1 and un1[0]["narration"] == WORDS["unpictured"]["one"].format(count=1),
          f"one clash the reel cannot picture is said in the singular ({[c['narration'] for c in un1]})")
    # N3 / Q3: a refused capture moves that clash into the unpictured count and the next drawable clash takes its place
    caps = [{"formKey": "000101:Target.esp", "status": "refused", "reason": "no camera"}]
    gr = planned(pres, names, masters, search, mods, captures=caps)
    oracle_checks(gr, reel_view(gr), oracle(pres, names, masters, search, mods, captures=caps), tag="refused capture: ")
    rule_checks(gr, reel_view(gr), tag="refused capture: ", mods=mods)


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
        # phase 2: a placement split and a world-state vignette get their own requests, bound to their findings' evidence
        result = {"configuration": {"fingerprint": "f" * 64}, "checkId": "records.contested-fields", "armId": "with-target"}
        (root / "result.json").write_text(json.dumps(result))
        semit = {"Probe": "stateprobe", "State": "baseline",
                 "Configuration": {"Fingerprint": "s" * 64, "Population": "load-time", "State": "baseline", "Plugins": []},
                 "Conflicts": [{"Kind": "STATE-CONTACT@000701:Skyrim.esm@Triggered", "A": "Target.esp", "B": "Beta.esp",
                                "Necessary": [{"FormKey": "000601:Target.esp", "Physical": "Holds", "Label": "CellFive (ext)"}],
                                "Alternative": [{"FormKey": "000602:Beta.esp"}]}]}
        (root / "state.json").write_text(json.dumps(semit))
        def ev(i, path):
            return {"id": i, "kind": "emit", "part": "arm", "armId": "with-target", "path": path,
                    "sha256": hashlib.sha256((root / path).read_bytes()).hexdigest()}
        p2 = copy.deepcopy(p)
        p2["evidence"] += [ev("E-2", "result.json"), ev("E-3", "state.json")]
        p2["findings"] += [{"id": "F-R", "kind": "finding", "checkId": "records.contested-fields", "armId": "with-target",
                            "allRows": [{"formKey": "000403:Skyrim.esm"}], "evidenceIds": ["E-2"]},
                           {"id": "F-S1", "kind": "finding", "checkId": "state.conditional-geometry", "armId": "with-target",
                            "allRows": [{"formKey": "000601:Target.esp"}], "evidenceIds": ["E-3"],
                            "counts": {"A": "Target.esp", "B": "Beta.esp"}}]
        p2["reel"]["withoutArmId"] = "without-target"
        p2["reel"]["chapters"] += [
            {"kind": "split", "clash": {"type": "record", "formKey": "000403:Skyrim.esm", "findingId": "F-R", "armId": "with-target",
                                        "withoutArmId": "without-target", "actor": False, "highlight": ["000403:Skyrim.esm"],
                                        "placements": {"left": [0.0, 0.0, 0.0], "right": [10.0, 0.0, 0.0]},
                                        "pair": ["Beta.esp", "Target.esp"]},
             "frames": [{"state": "left"}, {"state": "right"}]},
            {"kind": "vignette", "clash": {"type": "state", "formKey": "000601:Target.esp", "other": ["000602:Beta.esp"],
                                           "findingId": "F-S1", "armId": "with-target", "withoutArmId": "without-target",
                                           "state": "000701:Skyrim.esm", "highlight": ["000601:Target.esp"],
                                           "pair": ["Beta.esp", "Target.esp"]},
             "frames": [{"state": s} for s in POLICY["sequence"]]}]
        try:
            reqs2 = report_scenes.capture_requests(p2, root)["requests"]
        except Exception as exc:  # noqa: BLE001 - a capture plan that cannot read these chapters fails the checks below
            print(f"  NOTE  capture_requests raised on the phase 2 chapters: {type(exc).__name__}: {exc}")
            reqs2 = []
        sp = next((r for r in reqs2 if r.get("requestType") == "placement-split"), {})
        check("N4-capture-placement", sp.get("recordKeys") == ["000403:Skyrim.esm"]
              and [h.get("formKey") for h in sp.get("highlights") or []] == ["000403:Skyrim.esm"]
              and (sp.get("sequence") or {}).get("states") == ["left", "right"]
              and (sp.get("sequence") or {}).get("setups") == {"left": "without-target", "right": "with-target"}
              and sp.get("placements") == {"left": [0.0, 0.0, 0.0], "right": [10.0, 0.0, 0.0]}
              and sp.get("configurationFingerprint") == "f" * 64 and (sp.get("source") or {}).get("path") == "result.json",
              f"a placement split is one request with one participant, both placements, the two setups and its finding's "
              f"recorded configuration ({ {k: sp.get(k) for k in ('requestType', 'recordKeys', 'placements')} })")
        st = next((r for r in reqs2 if r.get("checkId") == "state.conditional-geometry"), {})
        check("N5-capture-state", st.get("state") == "000701:Skyrim.esm" and st.get("focus") == "000601:Target.esp"
              and [h.get("formKey") for h in st.get("highlights") or []] == ["000601:Target.esp"]
              and (st.get("sequence") or {}).get("states") == POLICY["sequence"]
              and (st.get("source") or {}).get("path") == "state.json",
              f"a world-state vignette's request carries its switch, marks only the participants the plan names and is "
              f"bound to the stateprobe emit ({ {k: st.get(k) for k in ('state', 'highlights')} })")
        import capture_acquire
        try:
            one = capture_acquire.participants({"requestType": "placement-split", "recordKeys": ["000403:Skyrim.esm"],
                                                "highlights": [{"formKey": "000403:Skyrim.esm", "color": "cyan"}]})
        except Exception as exc:  # noqa: BLE001
            one = f"refused: {exc}"
        check("N4-one-participant", one == [{"formKey": "000403:Skyrim.esm", "color": "cyan"}],
              f"a placement split has one participant ({one})")
        argv_of = getattr(capture_acquire, "capture_argv", None)
        cam = {"cell": "CellFive", "viewprobe": "--eye 1.0,2.0,3.0 --yaw 10.0 --pitch -5.0 --fov 65 --aspect 16:9"}
        try:
            a_state = argv_of({**st, "requestType": st.get("requestType")}, cam, "highlight", "order.json", ["--profile", "P"], 1280) if argv_of else []
            a_left = argv_of({**sp, "actor": True}, cam, "left", "without.json", [], 638) if argv_of else []
        except Exception as exc:  # noqa: BLE001
            print(f"  NOTE  capture_argv raised: {type(exc).__name__}: {exc}")
            a_state, a_left = [], []
        def has(a_, *seq):
            return any(a_[i:i + len(seq)] == list(seq) for i in range(len(a_)))
        check("N5-capture-argv", has(a_state, "--state", "000701:Skyrim.esm") and "--no-cell-outline" in a_state
              and has(a_state, "--ref", "000601:Target.esp=cyan") and not any("000602" in x for x in a_state)
              and has(a_state, "--order", "order.json"),
              f"a state frame is rendered in its state, without the cell outline, marking only the named participant ({a_state})")
        check("N4-capture-argv", has(a_left, "--npc", "000403:Skyrim.esm") and has(a_left, "--npc-puck", "on")
              and has(a_left, "--size", "638") and has(a_left, "--aspect", "638:680") and "--no-cell-outline" in a_left
              and has(a_left, "--order", "without.json"),
              f"a split half of an actor draws the actor with its puck at the half's canvas ({a_left})")
        # N9: each split half probes straight down from its own placement, and the probe is read back
        a_right = []
        try:
            a_right = argv_of({**sp, "actor": True}, cam, "right", "with.json", [], 638) if argv_of else []
        except Exception as exc:  # noqa: BLE001
            print(f"  NOTE  capture_argv raised: {type(exc).__name__}: {exc}")
        check("N9-probe-argv", has(a_left, "--clear-at", "0,0,64") and has(a_right, "--clear-at", "10,0,64"),
              f"each split half carries a --clear-at at its own placement, 64 u up ({a_left[-6:]}, {a_right[-6:]})")
        head = ("   clear-at 10,0,64: nearest placed geometry on each world axis within 2048 u - 10 candidate(s) in the broad "
                "phase, 10 with a readable mesh, 0 unreadable, 0 physical ref(s) skipped for want of a usable OBND, 0 physical "
                "ref(s) with no model at all, 0 selected by record OBND because their mesh could not be read, 0 admitted by "
                "mesh bounds that the record box alone would have excluded; terrain: none (broad phase)")
        empty = "cell SomeCell (interior)  000A01:Skyrim.esm\n" + head + "\n      down    - nothing within 2048 u\n"
        under = ("cell SomeOut (exterior)  000A02:Skyrim.esm\n" + head +
                 "\n      up      40.0 u  000000:TERRAIN  LandHeightmap  at 10,0,104\n      down    - nothing within 2048 u\n")
        floor_ = "cell SomeCell (interior)  000A01:Skyrim.esm\n" + head + "\n      down    64.0 u  000501:Test.esp  Floor01  at 10,0,0\n"
        ps = getattr(capture_acquire, "parse_support", None)
        try:
            got_s = [ps(t_, [10.0, 0.0, 0.0]) for t_ in (empty, under, floor_)] if ps else []
        except Exception as exc:  # noqa: BLE001
            got_s = [f"raised {type(exc).__name__}: {exc}"]
        want_s = [{"down": None, "terrainAbove": False, "interior": True},
                  {"down": None, "terrainAbove": True, "interior": False},
                  {"down": 64.0, "terrainAbove": False, "interior": True}]
        check("N9-support-parse", len(got_s) == 3 and all(isinstance(g, dict) and all(g.get(k) == w[k] for k in w)
                                                          for g, w in zip(got_s, want_s)),
              f"the probe reads back as nothing below, terrain above, or a floor below ({got_s})")
        # the state scene identity: the stateprobe emit's configuration with the population read by the renderer and the
        # state the clash was measured in (Gate B first check: equal to the renderer's own for the same state)
        fp = capture_acquire._load_evalrun().configuration_fingerprint
        cfg = {"SchemaVersion": 1, "Plugins": [{"Name": "Target.esp", "Sha256": "a" * 64}], "Providers": [], "Overlays": [],
               "Population": "load-time+population:0123456789ab", "State": "baseline"}
        cfg["Fingerprint"] = fp([{"name": "Target.esp", "sha256": "a" * 64}], [], [], cfg["Population"], "baseline", 1)
        (root / "scoped.json").write_text(json.dumps({"Configuration": cfg}))
        want_fp = fp([{"name": "Target.esp", "sha256": "a" * 64}], [], [], "load-time", "000701:Skyrim.esm", 1)
        try:
            got_id = capture_acquire.scene_identity({"absolutePath": str(root / "scoped.json")}, cfg["Fingerprint"],
                                                    state="000701:Skyrim.esm")
        except Exception as exc:  # noqa: BLE001
            got_id = {"error": f"{type(exc).__name__}: {exc}"}
        # a state picture is admitted to its arm's bundle: the arm's own configuration in a state the run measured
        import evalrun
        smf = getattr(evalrun, "state_media_fingerprints", None)
        own = [{"plugins": [{"name": "Target.esp", "sha256": "a" * 64}], "providers": [], "overlays": [],
                "population": "load-time+population:0123456789ab", "state": "baseline", "schemaVersion": 1}]
        img = root / "state.png"
        img.write_bytes(b"png")
        item = {"path": str(img), "mediaKind": "offline-render", "state": "000701:Skyrim.esm",
                "subject": {"checkId": "state.conditional-geometry", "armId": "with-target", "description": "d"},
                "configurationFingerprint": want_fp, "fingerprintSource": "emitted",
                "provenance": {"tool": "viewprobe", "capturedUtc": "2026-10-26T00:00:00Z"}}
        try:
            ok_in = evalrun.media_item_refusal(item, img, {"with-target": {"x" * 64}}, {},
                                               lambda arm, st: smf(own, {"000701:Skyrim.esm"}, st))[0]
            ok_out = evalrun.media_item_refusal({**item, "state": "000799:Skyrim.esm"}, img, {"with-target": {"x" * 64}}, {},
                                                lambda arm, st: smf(own, {"000701:Skyrim.esm"}, st))[0]
        except Exception as exc:  # noqa: BLE001
            ok_in, ok_out = f"raised {type(exc).__name__}: {exc}", None
        check("N5-state-media", smf is not None and ok_in is None and ok_out == "fingerprint-mismatch",
              f"a picture in a measured world state is admitted with its arm's configuration in that state, and one in "
              f"an unmeasured state is not (measured: {ok_in!r}, unmeasured: {ok_out!r})")
        check("N5-identity", (got_id or {}).get("sceneFingerprint") == want_fp,
              f"a state request's scene identity is the emit's configuration with the renderer's population and the clash's "
              f"state ({got_id})")
    finally:
        shutil.rmtree(root, ignore_errors=True)


# ------------------------------------------------------------------------------------------ the shipped skill data
def case_skill_data():
    pr = json.loads((SKILL / "references/report-priorities.json").read_text(encoding="utf-8"))
    sp = json.loads((SKILL / "references/scene-policy.json").read_text(encoding="utf-8"))
    words = ((pr.get("reel") or {}).get("words")) or {}
    vign = sp.get("vignettes") or {}
    pools_ok = all(isinstance(v, list) and len(v) >= 2 for v in (words.get("patch") or {}).values()) and bool(words.get("patch"))
    check("data-words", set(WORDS) - {"forbidden", "forbiddenPatterns"} <= set(words) and set(WORDS["patch"]) <= set(words.get("patch") or {})
          and pools_ok,
          f"report-priorities.json reel.words carries every sentence and label the planner uses (missing "
          f"{sorted(set(WORDS) - {'forbidden', 'forbiddenPatterns'} - set(words))[:6]})")
    check("data-policy", all(k in vign for k in ("allUpTo", "perType", "maxVignettes", "noInGameEffectFields", "stillReasons",
                                                  "sequence", "holdSeconds", "patchSearch")) and vign.get("sequence") == POLICY["sequence"]
          and all(vign.get(k) == POLICY[k] for k in ("allUpTo", "perType", "maxVignettes", "typeOrder", "types", "pictureTypes",
                                                       "actorTypes", "noInGameEffectFields", "sequence", "drawableFields",
                                                       "captureAttemptsPerType", "volumeFactor")),
          f"scene-policy.json vignettes carries the selection numbers, the no-effect fields and the sequence (has {sorted(vign)})")
    check("data-length", (pr.get("reel") or {}).get("maxSeconds") == 600 and bool((pr.get("reel") or {}).get("stillFont")),
          f"reel.maxSeconds is 600 and the still font file is named ({(pr.get('reel') or {}).get('maxSeconds')}, "
          f"{(pr.get('reel') or {}).get('stillFont')!r})")
    docs = (SKILL / "SKILL.md").read_text(encoding="utf-8") + (SKILL / "references/reel-layout.md").read_text(encoding="utf-8")
    old = [x for x in ("highlighted -> unhighlighted -> highlighted", "highlighted → unhighlighted → highlighted",
                       "highlighted -> normal -> highlighted", "must start with `reader.verdict`", "starts with `reader.verdict` verbatim",
                       "no title cards", "become data stills", "and N more", "shown as data") if x in docs]
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
                     "narrationAudio": "media/n.wav", "pageAudio": {"0": "media/n.wav", "1": "media/n.wav"},
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
            # each still page is its own scene (the opening's two pages, then the vignette, then the closing page)
            check("render-kinds", kinds == ["still", "still", "sequence", "still"],
                  f"the map's scenes are still, still, sequence, still ({kinds})")
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
        # phase 2: a placement split (two halves side by side over a label strip) and the one unpictured statement
        def half(name, colour):
            subprocess.run([ff, "-y", "-v", "error", "-f", "lavfi", "-i", f"color=c={colour}:size=638x680", "-frames:v", "1",
                            str(root / "media" / name)], check=True, capture_output=True)
            return hashlib.sha256((root / "media" / name).read_bytes()).hexdigest()
        halves = [{"id": "M-left", "path": "media/left.png", "sha256": half("left.png", "red"), "assessed": True,
                   "mediaKind": "offline-render", "subject": {"armId": "without-target"},
                   "provenance": {"poseId": "pose-L", "relativePose": True, "state": "left", "argv": ["--report-mode"]}},
                  {"id": "M-right", "path": "media/right.png", "sha256": half("right.png", "blue"), "assessed": True,
                   "mediaKind": "offline-render", "subject": {"armId": "with-target"},
                   "provenance": {"poseId": "pose-R", "relativePose": True, "state": "right", "argv": ["--report-mode"]}}]
        unp = "1 clash can't be visualized; find it in the accompanying report."
        p2 = copy.deepcopy(pres)
        p2["media"] = halves
        p2["reel"]["media"] = []
        p2["reel"]["chapters"] = [pres["reel"]["chapters"][0],
            {"id": "R-02", "kind": "split", "narration": "Beta Mod and Target Mod place Lamp in different spots.",
             "narrationAudio": "media/n.wav",
             "clash": {"type": "record", "formKey": "000403:Skyrim.esm", "camera": "relative",
                       "labels": {"left": "Without Target Mod", "right": "With Target Mod"}},
             "frames": [{"state": "left", "mediaId": "M-left"}, {"state": "right", "mediaId": "M-right"}]},
            {"id": "R-03", "kind": "unpictured", "narration": unp, "narrationAudio": "media/n.wav", "count": 1,
             "clashes": [{"formKey": "000501:Skyrim.esm", "type": "cell", "reason": "cell"}],
             "still": {"pages": [{"layout": "statement", "text": unp}]}},
            pres["reel"]["chapters"][-1]]
        p2p = root / "split.json"
        p2p.write_text(json.dumps(p2))
        r2 = render(p2p, root / "reel2")
        ok2 = r2.returncode == 0 and (root / "reel2/reel-map.json").is_file()
        kinds2, px = [], {}
        if ok2:
            mp2 = json.loads((root / "reel2/reel-map.json").read_text(encoding="utf-8"))
            kinds2 = [s.get("kind") for s in mp2["segments"]]
            sg = next((s for s in mp2["segments"] if s.get("kind") == "split"), None)
            if sg:
                fr0 = (sg.get("frames") or [{}])[0].get("reelInterval") or sg.get("reelInterval") or {}
                n = int(fr0.get("startFrame", 0)) + int(fr0.get("frames", 2)) // 2
                full = subprocess.run([ff, "-v", "error", "-i", str(root / "reel2/reel.mp4"), "-vf", f"select=eq(n\\,{n})",
                                       "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True).stdout
                def at(x, y):
                    o = (y * 1280 + x) * 3
                    raw = full[o:o + 3]
                    return tuple(raw) if len(raw) == 3 else None
                px = {"left": at(300, 340), "right": at(980, 340), "strip": at(4, 716)}
        lr = px.get("left") and max(range(3), key=lambda i: px["left"][i]) == 0
        rb = px.get("right") and max(range(3), key=lambda i: px["right"][i]) == 2
        strip = px.get("strip") and max(px["strip"]) < 90
        check("N4-split-render", ok2 and kinds2 == ["still", "still", "split", "still", "still"] and bool(lr) and bool(rb) and bool(strip),
              f"a split renders as one 1280x720 scene: the left half from the setup without the mod, the right from the "
              f"prediction, over a dark label strip (exit {r2.returncode}: {' '.join((r2.stdout + r2.stderr).split())[-160:]}; "
              f"kinds {kinds2}; pixels {px})")
        if ok2:
            un_seg = next((s for s in mp2["segments"] if s.get("chapterKind") == "unpictured"), {})
            check("N3-unpictured-render", un_seg.get("kind") == "still" and un_seg.get("narration") == unp,
                  f"the unpictured statement renders as a still with the same sentence spoken ({un_seg.get('kind')}, "
                  f"{un_seg.get('narration')!r})")
        else:
            check("N3-unpictured-render", False, "the reel with the unpictured statement did not render")
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
        'kept_rows = [r for r in rows if r.get("field") not in no_effect and r not in marker_rows]',
        "kept_rows = [r for r in rows if r not in marker_rows]")]),
    "code-verdict-opening": ("planner", "1-opening", [("_geometry-harness/reel_plan.py",
        '"narration": (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),',
        '"narration": str(reader.get("verdict")) + " " + (words["opening"] if counts else words["openingNone"]).format(mod=mod(target)),')]),
    "code-c1-labels": ("planner", "C1-stills-plain", [("_geometry-harness/reel_plan.py",
        "            line = check_\n", "            line = str(subj)\n")]),
    # phase 2
    "code-affected": ("planner", "N2-affected", [("_geometry-harness/reel_plan.py",
        '    affected = {target} | {p for r in kept_rows if types.get(r.get("checkId")) is not None for p in (r["A"], r["B"])}',
        "    affected = {target}")]),
    "code-unpictured-plural": ("planner", "N3-singular", [("_geometry-harness/reel_plan.py",
        'words["unpictured"]["one" if n_un == 1 else "many"]', 'words["unpictured"]["many"]')]),
    "code-unpictured-dropped": ("planner", "N3-unpictured", [("_geometry-harness/reel_plan.py",
        "    if unpictured:\n        n_un = len(unpictured)", "    if False:\n        n_un = len(unpictured)")]),
    "code-cases-shown": ("planner", "N6-count-agree", [("_geometry-harness/reel_plan.py",
        'n, shown = counts[t]["count"], len(chosen)', "n, shown = len(chosen), len(chosen)")]),
    "code-left-loser": ("planner", "N3-which", [("_geometry-harness/reel_plan.py",
        'if not carriers or carriers[-1] != r["A"]:', "if False:")]),
    "code-attempts": ("planner", "N3-which", [("_geometry-harness/reel_plan.py",
        'if c["rows"][0]["formKey"] in refused:', "if False:")]),
    "code-volume": ("planner", "Q6-highlight", [("_geometry-harness/reel_plan.py",
        'if len(fks) == 2 and all(vols) and max(vols) >= float(policy.get("volumeFactor") or 0) * min(vols) > 0:', "if False:")]),
    "code-condition": ("planner", "N5-condition", [("_geometry-harness/reel_plan.py",
        'into = (t.group(3) == "Enable") != bool(sw.get("baselineEnabled"))', "into = True")]),
    "code-split-left-arm": ("planner", "N4-split", [("_geometry-harness/reel_plan.py",
        '"left": without_arm, "right": with_arm}', '"left": with_arm, "right": with_arm}')]),
    "code-state-token": ("capture", "N5-capture-argv", [("_geometry-harness/capture_acquire.py",
        '        argv += ["--state", str(request["state"])]', "        pass")]),
    "code-cell-outline": ("capture", "N5-capture-argv", [("_geometry-harness/capture_acquire.py",
        '"--report-mode", "--no-cell-outline"]', '"--report-mode"]')]),
    "code-identity-state": ("capture", "N5-identity", [("_geometry-harness/capture_acquire.py",
        'pop, cfg.get("State") if st is None else st, cfg.get("SchemaVersion"))', 'pop, cfg.get("State"), cfg.get("SchemaVersion"))')]),
    "code-split-order": ("render", "N4-split-render", [("_geometry-harness/evalreel.py",
        "[0:v][3:v][1:v]hstack", "[1:v][3:v][0:v]hstack")]),
    "code-c3-markers": ("planner", "C3-markers", [("_geometry-harness/reel_plan.py",
        "and r not in marker_rows]", "]")]),
    # phase 3
    "code-pool-fixed": ("planner", "N7-pool", [("_geometry-harness/reel_plan.py",
        "        return pool[k % len(pool)]", "        return pool[0]")]),
    "code-group-no-cell": ("planner", "N8-key", [("_geometry-harness/reel_plan.py",
        'return (c["type"], c["pair"], cell or r["formKey"], r.get("field"))', 'return (c["type"], c["pair"], None, r.get("field"))')]),
    "code-mover-from-row": ("planner", "N9-who", [("_geometry-harness/reel_plan.py",
        "            mover = moved[0]\n", "            mover = B\n")]),
    "code-empty-without-support": ("planner", "N9-silent", [("_geometry-harness/reel_plan.py",
        '            extra = ""\n            if sup:', '            extra = words["spotExtra"]["noFloor"]\n            if sup:')]),
    "code-structure-any-share": ("planner", "N9-structure-still", [("_geometry-harness/reel_plan.py",
        '            elif dzs and share >= float(policy.get("structureShare") or 1.0) and abs(dz_obj) > tol:',
        "            elif dzs and abs(dz_obj) > tol:")]),
    "code-new-ref-mover": ("planner", "N9-new-ref", [("_geometry-harness/reel_plan.py",
        "        basev = None if definer.lower() in (A.lower(), B.lower()) else pl.get(definer)\n",
        "        basev = pl.get(definer)\n")]),
    "code-base-fallback": ("planner", "N9-base-unreadable", [("_geometry-harness/reel_plan.py",
        "else pl.get(definer)\n", "else (pl.get(definer) or pl.get(B))\n")]),
    "code-both-dropped": ("planner", "N9-who-both", [("_geometry-harness/reel_plan.py",
        "        elif moved is not None and len(moved) == 2:\n", "        elif False:\n")]),
    "code-air-as-floor": ("planner", "N9-air", [("_geometry-harness/reel_plan.py",
        'extra = words["spotExtra"]["noFloor" if sup.get("interior") else "inAir"]', 'extra = words["spotExtra"]["noFloor"]')]),
    "code-ground-interior": ("planner", "N9-ground-interior", [("_geometry-harness/reel_plan.py",
        'if sup.get("terrainAbove") and not sup.get("interior"):', 'if sup.get("terrainAbove"):')]),
    "code-structure-down": ("planner", "N9-structure-down", [("_geometry-harness/reel_plan.py",
        'words["structureUp" if dz_obj > 0 else "structureDown"]', 'words["structureUp"]')]),
    "code-where-above": ("planner", "N9-structure-down", [("_geometry-harness/reel_plan.py",
        'words["where"]["below" if dz < 0 else "above"]', 'words["where"]["below"]')]),
    "code-where-away": ("planner", "N9-where-away", [("_geometry-harness/reel_plan.py",
        "            elif abs(dz) >= 0.5 * dist:\n", "            elif True:\n")]),
    "code-others-one": ("planner", "N8-others-one", [("_geometry-harness/reel_plan.py",
        'words["groupOthers"]["one" if len(rest) == 1 else "many"]', 'words["groupOthers"]["many"]')]),
    "code-others-more": ("planner", "N8-others-more", [("_geometry-harness/reel_plan.py",
        'nm = ", ".join(rest[:3]) if len(rest) > 3 else joined(rest)', "nm = joined(rest[:3])")]),
    "code-probe-dropped": ("capture", "N9-probe-argv", [("_geometry-harness/capture_acquire.py",
        '            argv += ["--clear-at", f"{float(spot[0]):g},{float(spot[1]):g},{float(spot[2]) + 64:g}"]', "            pass")]),
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


def capture_binding_fixture():
    """The synthetic evaluation with WRW's capture shape: two captured requests picture one object (000101, the first
    overlap clash's), and the first clash a plan picks in another group (000103) was never captured."""
    pres, names, masters, search, mods = synthetic()

    def media(req, fk, states=("without", "with", "highlight")):
        arm = {"without": "without-target", "with": "with-target", "highlight": "with-target"}
        return [{"id": f"M-{req}-{s}", "assessed": True, "mediaKind": "offline-render",
                 "subject": {"checkId": "geometry.object-overlap", "armId": arm[s], "formKey": fk},
                 "provenance": {"captureRequest": req, "vignetteState": s, "poseId": "pose-" + req}} for s in states]
    # req-other pictures 000101 with an object no clash here names; its frames come first in the media list
    pres["media"] = media("req-other", "000101:Target.esp") + media("req-own", "000101:Target.esp") \
        + media("req-cell-two", "000104:Target.esp")
    captures = [{"request": "req-other", "status": "captured", "formKey": "000101:Target.esp",
                 "recordKeys": ["000101:Target.esp", "000999:Alpha.esp"]},
                {"request": "req-own", "status": "captured", "formKey": "000101:Target.esp",
                 "recordKeys": ["000101:Target.esp", "000201:Alpha.esp"]},
                {"request": "req-cell-two", "status": "captured", "formKey": "000104:Target.esp",
                 "recordKeys": ["000104:Target.esp", "000204:Alpha.esp"]}]
    return pres, names, masters, search, mods, captures


def plan_final(pres, names, masters, search, mods, captures, final):
    """plan_reel with the capture stage closed when the planner takes `captures_final` (a planner without it is
    called as it can be, so its output fails the assertions rather than crashing)."""
    import inspect
    import reel_plan
    kw = {"captures_final": final} if "captures_final" in inspect.signature(reel_plan.plan_reel).parameters else {}
    return reel_plan.plan_reel(copy.deepcopy(pres), names, masters, POLICY, binding_words(), search, mods, captures,
                               markers=MARKERS, switches=SWITCHES, **kw)


def binding_words():
    """The test's words plus any the shipped skill data adds (the closed-capture sentence is skill data)."""
    shipped = json.loads((SKILL / "references/report-priorities.json").read_text(encoding="utf-8"))["reel"]["words"]
    return {**WORDS, **{k: v for k, v in shipped.items() if k not in WORDS}}


def case_capture_binding():
    pres, names, masters, search, mods, captures = capture_binding_fixture()
    reel = plan_final(pres, names, masters, search, mods, captures, final=False)
    vig = [ch for ch in reel.get("chapters") or [] if (ch.get("clash") or {}).get("formKey") == "000101:Target.esp"]
    ids = [f.get("mediaId") for ch in vig for f in ch.get("frames") or []]
    check("B1-one-request", bool(vig) and all(str(i).startswith("M-req-own-") for i in ids),
          f"a vignette's frames all come from the capture request about its clash, never from another request on the "
          f"same object (frames {ids})")
    # the capture stage is closed: a clash the plan would picture but that was never captured is unavailable
    dropped = [dict(c) for c in captures if c["request"] != "req-own"]
    pres2 = copy.deepcopy(pres)
    pres2["media"] = [m for m in pres["media"] if m["provenance"]["captureRequest"] != "req-own"]
    reel2 = plan_final(pres2, names, masters, search, mods, dropped, final=True)
    vigs = [ch for ch in reel2.get("chapters") or [] if ch.get("kind") in ("vignette", "split")]
    missing = [(ch.get("id"), f.get("state")) for ch in vigs for f in ch.get("frames") or [] if not f.get("mediaId")]
    check("B2-only-captured", reel2.get("status") == "planned" and bool(vigs) and not missing,
          f"with capture closed the reel pictures only clashes with captured frames and moves on past one never "
          f"captured (status {reel2.get('status')}, frames without media {missing})")
    picked = [(ch.get("clash") or {}).get("formKey") for ch in vigs if (ch.get("clash") or {}).get("type") == "overlap"]
    check("B3-moves-on", "000104:Target.esp" in picked and "000101:Target.esp" not in picked,
          f"the overlap vignette is the captured clash in the next group (picked {picked})")
    # WRW's shape: refusals ahead of a captured clash used up the capture attempts, so nothing was shown; with capture
    # closed nothing is tried again, so the captured clash is shown and the refusal is said
    refused = dropped + [{"request": "req-refused", "status": "refused", "formKey": "000101:Target.esp",
                          "recordKeys": ["000101:Target.esp", "000201:Alpha.esp"], "reason": "no camera"}]
    import reel_plan
    one_try = dict(POLICY, captureAttemptsPerType=1)
    kw = {"captures_final": True} if "captures_final" in __import__("inspect").signature(reel_plan.plan_reel).parameters else {}
    reel5 = reel_plan.plan_reel(copy.deepcopy(pres2), names, masters, one_try, binding_words(), search, mods, refused,
                                markers=MARKERS, switches=SWITCHES, **kw)
    picked5 = [(ch.get("clash") or {}).get("formKey") for ch in reel5.get("chapters") or [] if ch.get("kind") == "vignette"]
    un5 = [c.get("formKey") for ch in reel5.get("chapters") or [] if ch.get("kind") == "unpictured" for c in ch.get("clashes") or []]
    check("B5-refusals-do-not-hide-a-capture", "000104:Target.esp" in picked5 and "000101:Target.esp" in un5,
          f"a refusal ahead of a captured clash does not use up the attempts once capture is closed (picked {picked5}, "
          f"said unpictured {un5})")
    # nothing captured at all: one sentence says so, and no vignette asks for a frame
    reel3 = plan_final(pres2 | {"media": []}, names, masters, search, mods, [], final=True)
    vigs3 = [ch for ch in reel3.get("chapters") or [] if ch.get("kind") in ("vignette", "split")]
    said = " ".join(str(ch.get("narration") or "") for ch in reel3.get("chapters") or [])
    check("B4-none-captured", reel3.get("status") == "planned" and not vigs3
          and binding_words().get("noneCaptured", "(no closed-capture sentence in the skill data)")[:20] in said,
          f"with nothing captured the reel plans no vignette and says so in one sentence (status {reel3.get('status')}, "
          f"vignettes {len(vigs3)})")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--presentation")
    ap.add_argument("--reference", action="store_true")
    ap.add_argument("--sabotage")
    ap.add_argument("--code-sabotage", help="a CODE_SABOTAGE arm name, or all")
    ap.add_argument("--case", choices=("planner", "capture", "binding", "data", "render"))
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
        cases = {"planner": case_planner, "capture": case_capture_plan, "binding": case_capture_binding, "data": case_skill_data, "render": case_render}
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
