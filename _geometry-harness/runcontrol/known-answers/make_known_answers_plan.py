"""Write plan-modules.json: the known answers for the modules runcontrol's `plan` and `run` commands need.

Every expected value comes from the real Python modules: evalregistry.py (validate, families, applicability),
evalsettings.py (load, value, record, sentences), stategen.py, switchflip_scope.py, modnames.meta_ini_mod_id, and the
`re` module for the pattern table. Run it again only when one of those rules, or the check registry, changes on
purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_plan.py

The scope documents under scopes/ beside this file are scopeprobe's output for the synthetic fixture world, with the
machine's folder replaced by C:/fixture; the rest of the inputs are written here. Nothing outside a temporary folder is read or written except the registry, the scopes and the output.
"""
from __future__ import annotations

import base64
import copy
import hashlib
import json
import os
import platform
import re
import shutil
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1]))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run                # noqa: E402
import evalregistry as REG            # noqa: E402
import evalsettings as SET            # noqa: E402
import stategen as SG                 # noqa: E402
import switchflip_scope as SFS        # noqa: E402
from modnames import meta_ini_mod_id  # noqa: E402

OUT = HERE / "plan-modules.json"
REGISTRY = Path(REG.DEFAULT_REGISTRY)
BACK, FWD = "<ROOT\\>", "<ROOT/>"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def canon(obj) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def mask(obj, root: Path):
    """The same value with the temporary root replaced by a placeholder, in both spellings."""
    if isinstance(obj, str):
        return obj.replace(str(root), BACK).replace(str(root).replace("\\", "/"), FWD)
    if isinstance(obj, dict):
        return {mask(k, root): mask(v, root) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [mask(v, root) for v in obj]
    return obj


def unmask(text: str, root: Path) -> str:
    return text.replace(BACK, str(root)).replace(FWD, str(root).replace("\\", "/"))


# ------------------------------------------------------------------ the registry

def apply(doc, ops):
    """A copy of doc with each op applied: [path, "set", value], [path, "del"] or [path, "append", value]."""
    doc = copy.deepcopy(doc)
    for path, verb, *value in ops:
        at = doc
        for step in path[:-1]:
            at = at[step]
        if verb == "set":
            at[path[-1]] = copy.deepcopy(value[0])
        elif verb == "del":
            del at[path[-1]]
        else:
            at[path[-1]].append(copy.deepcopy(value[0]))
    return doc


def registry_cases() -> dict:
    data = REGISTRY.read_bytes()
    reg = REG.load(str(REGISTRY))
    index = {c["id"]: i for i, c in enumerate(reg["checks"])}

    def c(check_id, *rest):
        return ["checks", index[check_id], *rest]

    masters, route, census, cond = "loadability.masters-and-order", "geometry.route-cut", "state.switch-census", "state.conditional-geometry"
    overlap, deps, bos, derived = "geometry.object-overlap", "geometry.declared-dependencies", "geometry.bos-overlay", "patch.order-evaluation"
    mcp, live, occlusion, linked = "loadability.missing-assets", "runtime.state-switch-flip", "environment.occlusion", "functional.linked-dependencies"
    twin = copy.deepcopy(reg["checks"][index[deps]])

    variants = {
        # values Python's == and isinstance let through
        "schemaVersion 1.0 is 1": [[["schemaVersion"], "set", 1.0]],
        "schemaVersion true is 1": [[["schemaVersion"], "set", True]],
        "registryVersion true is an int": [[["registryVersion"], "set", True]],
        # the head
        "schemaVersion 2": [[["schemaVersion"], "set", 2]],
        "schemaVersion text": [[["schemaVersion"], "set", "1"]],
        "schemaVersion absent": [[["schemaVersion"], "del"]],
        "registryVersion text": [[["registryVersion"], "set", "4"]],
        "registryVersion a float": [[["registryVersion"], "set", 4.0]],
        # families
        "families empty": [[["families"], "set", []]],
        "families not a list": [[["families"], "set", "x"]],
        "a family that is not an object": [[["families", 0], "set", 5]],
        "a family with no id": [[["families", 1, "id"], "set", ""]],
        "a duplicate family id": [[["families"], "append", reg["families"][2]]],
        "a family with no packet row": [[["families", 3, "packetRow"], "set", ""]],
        "a family with no check": [[["families"], "append", {"id": "spare", "title": "Spare", "packetRow": "none"}]],
        # checks, by row
        "checks empty": [[["checks"], "set", []]],
        "checks not a list": [[["checks"], "set", {"a": 1}]],
        "a check that is not an object": [[["checks", 4], "set", "x"]],
        "a check with no id": [[c(mcp, "id"), "set", ""]],
        "a check whose id is absent": [[c(derived, "id"), "del"]],
        "a duplicate check id": [[["checks"], "append", twin]],
        "an unknown family": [[c(deps, "family"), "set", "nope"]],
        "raises: a family that is a list": [[c(deps, "family"), "set", ["geometry"]]],
        "an id without its family prefix": [[c(deps, "id"), "set", "declared-dependencies"]],
        "no title": [[c(deps, "title"), "set", ""]],
        "required not a bool": [[c(deps, "required"), "set", "yes"]],
        "required 1 with no trigger": [[c(deps, "required"), "set", 1], [c(deps, "applicability", "trigger"), "del"]],
        # the adapter
        "no adapter": [[c(deps, "adapter"), "set", None]],
        "an unknown adapter kind": [[c(deps, "adapter", "kind"), "set", "weird"]],
        "an adapter kind that is a number": [[c(deps, "adapter", "kind"), "set", 7]],
        "no unsupportedReason": [[c(mcp, "unsupportedReason"), "del"]],
        "no exe": [[c(deps, "adapter", "exe"), "set", ""]],
        "args not a list": [[c(deps, "adapter", "args"), "set", "--json {json}"]],
        "commonFlags absent on a child kind": [[c(deps, "adapter", "commonFlags"), "del"]],
        "commonFlags text on a child kind": [[c(route, "adapter", "commonFlags"), "set", "false"]],
        "commonFlags on a kind with no child": [[c(derived, "adapter", "commonFlags"), "set", True]],
        "population empty": [[c(deps, "adapter", "population"), "set", ""]],
        "state not text": [[c(deps, "adapter", "state"), "set", 5]],
        "primitiveSolidityFlag on another kind": [[c(deps, "adapter", "primitiveSolidityFlag"), "set", "--primitive-solidity"]],
        "primitiveSolidityFlag misspelled": [[c(cond, "adapter", "primitiveSolidityFlag"), "set", "--solid"]],
        "populationFlag on another kind": [[c(deps, "adapter", "populationFlag"), "set", "--population"]],
        "populationFlag misspelled": [[c(overlap, "adapter", "populationFlag"), "set", "--pop"]],
        "files on a json adapter": [[c(deps, "adapter", "files"), "set", ["{out}/{arm}.x.json"]]],
        "files empty": [[c(route, "adapter", "files"), "set", []]],
        "files with a blank path": [[c(route, "adapter", "files"), "set", ["{out}/{arm}.json", " \t"]]],
        "files not naming the arm": [[c(route, "adapter", "files"), "set", ["{out}/rows.json"]]],
        "files not under the run": [[c(route, "adapter", "files"), "set", ["C:/x/{arm}.json"]]],
        "files under {run}": [[c(route, "adapter", "files"), "set", ["{run}/x/{arm}.json"]]],
        "an output on a kind with no child": [[c(derived, "adapter", "output"), "set", "json"]],
        # the result block
        "no result": [[c(deps, "adapter", "result"), "del"]],
        "an unknown result kind": [[c(deps, "adapter", "result", "kind"), "set", "guess"]],
        "a result with no findings spec": [[c(deps, "adapter", "result", "findings"), "del"]],
        "a null spec with no note": [[c(route, "adapter", "result", "note"), "del"]],
        "a list spec in a text output": [[c(route, "adapter", "result", "judged"), "set", ["a.b"]]],
        "a list spec holding a regex": [[c(deps, "adapter", "result", "judged"), "set", ["a.b", "regex:x"]]],
        "an empty list spec": [[c(deps, "adapter", "result", "judged"), "set", []]],
        "a spec that is a number": [[c(deps, "adapter", "result", "judged"), "set", 5]],
        "a spec that is empty text": [[c(deps, "adapter", "result", "judged"), "set", ""]],
        "a spec that is true": [[c(deps, "adapter", "result", "judged"), "set", True]],
        "a regex in a json output": [[c(deps, "adapter", "result", "judged"), "set", "regex:(?P<n>\\d+)"]],
        "a regex with no group n": [[c(route, "adapter", "result", "judged"), "set", "regex:(?P<count>\\d+) rows"]],
        "a dotted path in a text output": [[c(route, "adapter", "result", "judged"), "set", "summary.rows"]],
        # applicability
        "a required check with no trigger": [[c(deps, "required"), "set", True], [c(deps, "applicability", "trigger"), "del"]],
        "a required check with no applicability": [[c(mcp, "applicability"), "set", None]],
        "a trigger that is not an object": [[c(deps, "applicability", "trigger"), "set", "placed"]],
        "a trigger with no operator": [[c(deps, "applicability", "trigger"), "set", {}]],
        "a trigger with two operators": [[c(deps, "applicability", "trigger"), "set", {"placed": True, "land": True, "always": True}]],
        "an unknown trigger operator": [[c(deps, "applicability", "trigger"), "set", {"it's": True}]],
        "anyOf empty": [[c(deps, "applicability", "trigger"), "set", {"anyOf": []}]],
        "a nested bad trigger": [[c(deps, "applicability", "trigger"), "set",
                                  {"anyOf": [{"placed": True}, {"allOf": [{"land": False}, {"anyType": []}, 5, {"anyType": ["A", 1]}]}]}]],
        "placed not true": [[c(deps, "applicability", "trigger"), "set", {"placed": 1}]],
        "no trigger note": [[c(deps, "applicability", "note"), "del"]],
        "dependsOnExtents not a list": [[c(linked, "applicability", "dependsOnExtents"), "set", "bos-swaps"]],
        "an unknown extent": [[c(linked, "applicability", "dependsOnExtents"), "set", ["bos-swaps", "the weather", None]]],
        # inputs, evidence, the flags
        "no inputs": [[c(mcp, "inputs"), "del"]],
        "inputs with unknown values": [[c(deps, "inputs"), "set", {"arms": "some", "states": "none"}]],
        "no evidence": [[c(deps, "evidence"), "set", []]],
        "an unknown evidence strength": [[c(deps, "evidence", "strength"), "set", "hunch"]],
        "no claim key": [[c(deps, "evidence", "claim"), "del"], [c(deps, "evidence", "limits"), "set", "x"]],
        "no claim and no limits": [[c(deps, "evidence", "claim"), "set", None], [c(deps, "evidence", "limits"), "set", ""]],
        "manualDecisionAllowed not a bool": [[c(deps, "manualDecisionAllowed"), "set", 0]],
        "optIn not a bool": [[c(deps, "optIn"), "set", "true"]],
        "optIn null": [[c(deps, "optIn"), "set", None]],
        "an opt-in check that is required": [[c(live, "required"), "set", True]],
        "a live check without optIn": [[c(live, "optIn"), "del"]],
        # prerequisites
        "prerequisites null": [[c(deps, "prerequisites"), "set", None]],
        "prerequisites not a list": [[c(deps, "prerequisites"), "set", "ab"]],
        "prerequisites a dict": [[c(deps, "prerequisites"), "set", {masters: 1}]],
        "an unknown prerequisite": [[c(deps, "prerequisites"), "set", [masters, "nope.check", 5]]],
        "a prerequisite cycle": [[c(masters, "prerequisites"), "set", [census]]],
        "a check that requires itself": [[c(bos, "prerequisites"), "set", [bos]]],
        "two cycles through one check": [[c(masters, "prerequisites"), "set", [census, deps]]],
        "an all-arms check on a baseline-only prerequisite": [[c(deps, "prerequisites"), "set", [masters, occlusion]]],
        # where Python itself raises
        "raises: a kind that is a list": [[c(deps, "adapter", "kind"), "set", ["probe-json"]]],
        "raises: an id that is a number": [[c(deps, "id"), "set", 5]],
        "raises: an id that is true": [[c(deps, "id"), "set", True]],
        "raises: an id that is a list": [[c(deps, "id"), "set", ["x"]]],
        "raises: prerequisites that is a number": [[c(deps, "prerequisites"), "set", 5]],
        "raises: inputs that is text": [[c(deps, "inputs"), "set", "all"]],
        "raises: a result kind that is a dict": [[c(deps, "adapter", "result", "kind"), "set", {"a": 1}]],
        "raises: an extent that is a list": [[c(linked, "applicability", "dependsOnExtents"), "set", [["bos-swaps"]]]],
        "raises: a strength that is a list": [[c(deps, "evidence", "strength"), "set", []]],
        "raises: a prerequisite that is a dict": [[c(deps, "prerequisites"), "set", [{}]]],
        "raises: a family that is a dict, late": [[c(deps, "family"), "set", {}]],
    }
    rows = []
    for name, ops in variants.items():
        row = {"name": name, "ops": ops}
        try:
            row["problems"] = REG.validate(apply(reg, ops))
        except Exception as ex:  # the runtime's own error; its text is what evalrun would refuse with
            row["raises"] = str(ex)
        if name.startswith("raises:") != ("raises" in row):
            raise SystemExit(f"registry variant {name!r}: expected {'an error' if name.startswith('raises:') else 'problems'}, got {row}")
        rows.append(row)

    # A pattern Python's re refuses: the sentence ends with re's own words, which .NET cannot repeat.
    ops = [[c(route, "adapter", "result", "judged"), "set", "regex:(?P<n>[\\d,+ rows"]]
    said = REG.validate(apply(reg, ops))
    cut = said[0].index("compile: ") + len("compile: ")
    bad_regex = {"ops": ops, "count": len(said), "prefix": said[0][:cut]}

    not_objects = []
    for text in ("[]", "\"x\"", "5", "2.5", "true", "null"):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "r.json"
            p.write_text(text, encoding="utf-8")
            try:
                REG.load(str(p))
                raise SystemExit(f"load accepted {text}")
            except ValueError as ex:
                not_objects.append({"text": text, "message": str(ex).replace(str(p), "<FILE>")})

    specs = [reg["checks"][index[route]]["adapter"]["result"][k][len("regex:"):] for k in ("judged", "unjudgeable")]
    return {"bytes": b64(data), "sha256": REG.registry_sha256(str(REGISTRY)), "problems": REG.validate(reg),
            "families": canon(REG.families(reg)), "variants": rows, "badRegex": bad_regex, "notObjects": not_objects,
            "regexSpecs": specs, "applicability": applicability_cases(reg, index)}


def target(name="A.esp", **footprint):
    return {"name": name, "footprint": footprint}


def applicability_cases(reg, index) -> list:
    def c(check_id, *rest):
        return ["checks", index[check_id], *rest]

    real = {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in sorted((HERE / "scopes").glob("*.json"))}
    if len(real) < 2:
        raise SystemExit("the real scope documents under known-answers/scopes are missing")
    everything = {
        "complete": True, "walkFailures": [], "configuration": {"overlays": ["a_SWAP.ini", "b_SWAP.ini"]},
        "unmeasured": [],
        "targets": [
            target("A.esp", recordTypes={"CELL": {"new": 1, "override": 2}, "QUST": 3, "REFR": {"new": 4}, "PACK": {"override": 0},
                                         "NAVM": {"new": 0, "override": 0}, "FLST": True, "LAND": 1.5, "TXST": "many"},
                   placed={"total": 9, "exterior": 4, "land": 2, "navm": 1, "exteriorCells": [[0, 0], [0, 1], [1, 1]]},
                   masters=["Skyrim.esm", "Update.esm"], nexusId=12345),
            target("B.esp", recordTypes={"CELL": {"new": 1, "override": 0.5}, "ACHR": {"new": 1, "override": 1}},
                   placed={"total": "3", "exterior": 2.9, "land": True, "navm": " 1_0 ", "exteriorCells": [[2, 2]]},
                   masters=["Skyrim.esm"], nexusId="678"),
            "not a target", {"name": "C.esp", "footprint": None}, {"name": "D.esp"},
        ],
    }
    nothing = {"complete": True, "targets": [target("A.esp", recordTypes={}, placed={}, masters=[])]}
    cases = [
        ("no scope", [], None),
        ("an empty scope", [], {}),
        ("complete, and nothing in it", [], nothing),
        ("complete, with no targets", [], {"complete": True}),
        ("everything present", [], everything),
        ("not complete, two targets not walked", [], {**everything, "complete": False, "walkFailures": ["X.esp", "Y.esp"]}),
        ("not complete, no failures listed", [], {**everything, "complete": False}),
        ("complete is 1, not true", [], {**everything, "complete": 1}),
        ("an unmeasured extent the check depends on", [],
         {**everything, "unmeasured": [{"extent": "generated-output", "why": "not this one"}, "x", {"why": "no extent"},
                                       {"extent": "bos-swaps", "why": "a _SWAP.ini was found but not read"},
                                       {"extent": "script-placement", "why": "first declared wins"}]}),
        ("an unmeasured extent with no sentence", [], {**everything, "unmeasured": [{"extent": "alias-fill"}]}),
        ("placed only", [], {"complete": True, "targets": [target(placed={"total": 1}, recordTypes={"REFR": {"new": 1}})]}),
        ("land and navmesh only", [], {"complete": True, "targets": [target(placed={"land": 3, "navm": 2},
                                                                              recordTypes={"LAND": 3, "NAVM": 2})]}),
        ("exterior with no cells listed", [], {"complete": True, "targets": [target(placed={"exterior": 5})]}),
        ("several Nexus ids", [], {"complete": True, "targets": [target(nexusId=1), target("B.esp", nexusId="b"),
                                                                    target("C.esp", nexusId=0), target("D.esp", nexusId=2.5)]}),
        ("masters only", [], {"complete": True, "targets": [target(masters=["Skyrim.esm"])]}),
        ("overlays only", [], {"complete": True, "configuration": {"overlays": ["x_SWAP.ini"]}, "targets": []}),
        ("a check with no trigger", [[c("patch.discovery-pool", "applicability", "trigger"), "del"]], everything),
        ("a check with no applicability block", [[c("patch.discovery-pool", "applicability"), "set", "none"]], everything),
        ("allOf: yes, no and unknown arms", [
            [c("geometry.object-overlap", "applicability", "trigger"), "set", {"allOf": [{"placed": True}, {"masters": True}, {"always": True}]}],
            [c("geometry.placement-contact", "applicability", "trigger"), "set", {"allOf": [{"placed": True}, {"anyType": ["WRLD", "ZZZZ"]}, {"land": True}]}],
            [c("geometry.route-cut", "applicability", "trigger"), "set", {"anyOf": [{"anyType": ["WRLD"]}, {"allOf": [{"overlays": True}, {"nexusId": True}]}]}],
            [c("geometry.bos-overlay", "applicability", "trigger"), "set", {"anyOf": [{"anyType": ["ZZZZ", "WRLD", "AAAA"]}, {"anyType": ["TXST"]}]}],
        ], everything),
        ("allOf and anyOf with nothing present", [
            [c("geometry.object-overlap", "applicability", "trigger"), "set", {"allOf": [{"always": True}, {"placed": True}, {"masters": True}]}],
            [c("geometry.placement-contact", "applicability", "trigger"), "set", {"anyOf": [{"land": True}, {"navmesh": True}, {"overlays": True}, {"nexusId": True}, {"exterior": True}]}],
        ], nothing),
        ("allOf and anyOf on no scope", [
            [c("geometry.object-overlap", "applicability", "trigger"), "set", {"allOf": [{"always": True}, {"placed": True}]}],
            [c("geometry.placement-contact", "applicability", "trigger"), "set", {"anyOf": [{"placed": True}, {"always": True}]}],
            [c("geometry.route-cut", "applicability", "trigger"), "set", {"allOf": [{"always": True}, {"anyOf": [{"always": True}]}]}],
        ], None),
        ("allOf on an incomplete scope", [
            [c("geometry.object-overlap", "applicability", "trigger"), "set", {"allOf": [{"always": True}, {"placed": True}, {"land": True}]}],
        ], {"complete": False, "walkFailures": "ab"}),
        ("a repeated record type", [[c("records.cell-attributes", "applicability", "trigger"), "set", {"anyType": ["CELL", "ACHR", "CELL"]}]], everything),
    ]
    cases += [(f"real scope {name}", [], doc) for name, doc in real.items()]
    rows = []
    for name, ops, scope in cases:
        rows.append({"name": name, "ops": ops, "scope": None if scope is None else canon(scope),
                     "expected": canon(REG.applicability(apply(reg, ops), scope))})
    return rows


# ------------------------------------------------------------------ patterns

PATTERNS = [
    # (pattern, multiline, texts)
    (r"(?P<n>\d+) rows", False, ["12 rows", "x 7 rows y", "rows", "\u0663 rows", "\uff11\uff12 rows"]),
    (r"(?P<a>\w+)-(?P=a)", False, ["ab-ab", "ab-ac", "\u00e9t\u00e9-\u00e9t\u00e9", "a_1-a_1", "x\u00b2-x\u00b2", "a\u203f-a\u203f", "e\u0301-e\u0301"]),
    (r"end$", False, ["the end", "the end\n", "the end\n\n", "the end\r\n", "end\nmore"]),
    (r"end$", True, ["the end", "the end\n", "the end\n\n", "the end\r\n", "end\nmore", "end\rmore"]),
    (r"^(?P<k>\w+)=(?P<v>.*)$", True, ["a=1\nb=2", "x\nkey=value\r\nz", "\n=3\nq=", "a=1\u2028b=2"]),
    (r"^b", False, ["a\nb", "b"]),
    (r"^b", True, ["a\nb", "a\rb"]),
    (r"\Aab\Z", False, ["ab", "ab\n", "xab"]),
    (r"\Aab\Z", True, ["ab", "ab\n", "ab\nab"]),
    (r"(?i)total: (?P<n>[\d,]+)", False, ["TOTAL: 1,234", "Total: 5", "total 5"]),
    (r"(?i)stra\u00dfe|\u03c3\u03c2", False, ["STRASSE", "STRA\u00dfE", "\u03a3\u03a3", "\u03c3\u03c3"]),
    (r"(?i:ab)C", False, ["ABC", "abc", "ABc"]),
    (r"(?im)^done$", False, ["x\nDONE\ny", "undone"]),
    (r"(?s)a.b", False, ["a\nb", "a\rb"]),
    (r"a.b", False, ["a\nb", "a\rb", "a\u2028b"]),
    (r"\s+", False, ["a \t\n\r\x0b\x0cb", "a\x1c\x1d\x1e\x1fb", "a\x85\xa0b", "a\u2028\u2029\u3000b", "a\u200bb", "a\x00b"]),
    (r"\S+", False, [" \x1cab\x1f ", "\x85x\xa0"]),
    (r"[\s,]+", False, ["a, \x1e,b"]),
    (r"\d+", False, ["abc 123", "\u0661\u0662\u0663", "\u00b2\u00b3", "\u2460", "\u0be7", "1\u0662"]),
    (r"[\d,]+", False, ["x 1,2\u0663 y"]),
    (r"\w+", False, ["h\u00e9llo w", "a_b-c", "\u4e2d\u6587", "\u00b2\u00bd", "\u2167", "\u203f\u2040", "\u0301x", "x\u0301y", "\u200dab"]),
    (r"\W+", False, ["ab -\u00b2- cd", "a\u0301b", "a\u203fb"]),
    (r"[\w.]+", False, ["v1.2\u00b2 x", "a\u0301b"]),
    (r"\bcat\b", False, ["a cat sat", "concat", "cat\u0301 x", "\u00b2cat"]),
    (r"(?P<n>[\d,]+) references", False, ["all on the same 1,234 references", "0 references", ", references"]),
    (r"x{,2}y", False, ["xxy", "xxxy", "y", "x{,2}y"]),
    (r"x{2,}y", False, ["xy", "xxxy"]),
    (r"[]a]+", False, ["]a]b"]),
    (r"[^]a]+", False, ["]ab]"]),
    (r"[a-c[]+", False, ["x[ab]"]),
    (r"[[:alpha:]]+", False, ["x:al]", "abc"]),
    (r"[a-z-[aeiou]]+", False, ["be]", "bcd", "a-]"]),
    (r"(a)|b", False, ["b", "a"]),
    (r"(?:(?P<x>a)|b)+", False, ["ab", "ba", "bb"]),
    (r"(?P<first>a)(?P<second>b)?c", False, ["ac", "abc"]),
    (r"(a)(?P<n>b)(c)\3", False, ["abcc", "abcb"]),
    (r"(a)\1", False, ["aa", "ab"]),
    (r"(?P<q>['\"]).*?(?P=q)", False, ["say 'hi' \"there\"", "'a\"", "\"a'b\""]),
    (r"(?<=: )\d+(?= rows)", False, ["count: 42 rows", "count:42 rows"]),
    (r"(?<!\d)\d{2}(?!\d)", False, ["a 123 45 6"]),
    (r"\(inset consumed them\): (?P<n>[\d,]+)", False, ["judged (inset consumed them): 12,345 x", "(inset consumed them) 5"]),
    (r"a\.b\\c\/d", False, ["a.b\\c/d", "axb\\c/d"]),
    (r"\x41\u00e9\t\n", False, ["A\u00e9\t\n"]),
    (r"[\u0400-\u04ff]+", False, ["ab \u0416\u0438 cd"]),
    (r".", False, ["\U0001F600", "\n\U0001F600"]),
    (r"^.$", False, ["\U0001F600", "a"]),
    (r"[^a]{2}", False, ["\U0001F600", "bc"]),
    (r"(?P<n>\d+)", False, ["\U0001F600 12"]),
    (r"(?i)k|\u017f|i", False, ["\u212a", "S", "\u0130", "\u0131", "I"]),
    (r"(?a)\w+", False, ["h\u00e9llo"]),
    (r"(?L)a", False, ["a"]),
    (r"(?u)\w+", False, ["h\u00e9llo"]),
    (r"a(?i)b", False, ["aB"]),
    (r"a*+a", False, ["aaa"]),
    (r"(?>a+)a", False, ["aaa"]),
    (r"\N{BULLET}", False, ["\u2022"]),
    (r"\U0001F600", False, ["\U0001F600"]),
    (r"[\W]+", False, ["a -b"]),
    (r"[\S]+", False, ["a b"]),
    (r"(?P<n>[", False, ["x"]),
    (r"(?P<n>\d+", False, ["1"]),
    (r"(?P=n)", False, ["1"]),
    (r"*a", False, ["a"]),
    (r"a{2,1}", False, ["aa"]),
    (r"(?#note)a(?x: b  c )d", False, ["abcd", "a b c d"]),
    (r"(?x) a \s b  # the rest", False, ["a b", "ab"]),
    (r"\d+(?:,\d{3})*", False, ["total 1,234,567 and 12,34"]),
    (r"^\s*$", True, ["a\n\n b", "a\n \t\nb", "a\r\n\r\nb"]),
    (r"a|", False, ["b"]),
    (r"(?P<n>\d+)(?(n)x|y)", False, ["1x", "1y"]),
    (r"\Bat", False, ["cat at"]),
    (r"\0\07\101", False, ["\x00\x07A"]),
]


def pattern_cases(specs) -> list:
    table = list(PATTERNS)
    table += [(specs[0], True, ["$ blockprobe.exe X.esp\nrule comparison, all on the same 1,234 references\n", "rule comparison, all on the same  references",
                                "x\r\n   rule comparison, all on the same 7 references: 3 agree\r\n", "all on the same 12 references"]),
              (specs[1], True, ["navmesh triangles that could not be judged (inset consumed them): 0\n",
                                "   navmesh triangles that could not be judged (inset consumed them): 12,345\r\nnext",
                                "navmesh triangles that could not be judged: 3", "navmesh triangles that could not be judged (inset consumed them): \u0663"])]
    rows = []
    for pattern, multiline, texts in table:
        try:
            rx = re.compile(pattern, re.M if multiline else 0)
        except re.error:
            rows.append({"pattern": pattern, "multiline": multiline, "compiles": False})
            continue
        for text in texts:
            m = rx.search(text)
            row = {"pattern": pattern, "multiline": multiline, "compiles": True, "text": text, "match": None}
            if m:
                row["match"] = {"start": m.start(), "end": m.end(), "text": m.group(0), "named": m.groupdict(), "groups": list(m.groups())}
                if "n" in m.groupdict() and m.group("n") is not None:
                    try:
                        row["match"]["count"] = int(m.group("n").replace(",", ""))
                    except ValueError:
                        row["match"]["count"] = None
            rows.append(row)
    return rows


# ------------------------------------------------------------------ settings

VALID = {"schemaVersion": 1, "saved": {"utc": "2026-11-01T00:00:00Z", "by": "window", "houseCARL": "2.0.0"}}
ENV_KEYS = ("HOUSECARL_EVAL_SETTINGS", "HOUSECARL_DATA_DIR", "LOCALAPPDATA", "HOUSECARL_FFMPEG", "XDG_STATE_HOME")
F = "s/houseCARL.evaluation.json"


def js(doc, **kw) -> bytes:
    return json.dumps(doc, **kw).encode("utf-8")


def with_values(**sections) -> bytes:
    return js({**VALID, **sections}, indent=1)


def settings_table() -> list:
    """(name, files {relative path: bytes}, env {name: value with placeholders}, explicit path or None, flags or None)."""
    here = BACK + "\\s\\houseCARL.evaluation.json"
    one = lambda data: {F: data}  # noqa: E731
    model = sorted(SET.MODEL_AGENTS)[0] if SET.MODEL_AGENTS else "session"
    t = [
        ("no file", {}, {}, here, None),
        ("no file, forward slashes and a dot in the path", {}, {}, FWD + "/s/./x//houseCARL.evaluation.json", None),
        ("no file, found through LOCALAPPDATA", {}, {"LOCALAPPDATA": BACK + "\\local"}, None, None),
        ("no file, flags only", {}, {}, here, {"evaluation.depth": "standard", "evaluation.reverseComparison": False, "evaluation.budgetMinutes": 90.0}),
        ("no file, a flag that is null", {}, {}, here, {"evaluation.depth": None, "evaluation.budgetMinutes": 0.5}),
        ("no file, flags that cannot combine", {}, {}, here, {"evaluation.depth": "standard", "evaluation.liveSwitchCheck": "ask"}),
        ("a file that only names its schema", one(js({"schemaVersion": 1})), {}, here, None),
        ("a file holding null", one(b"null"), {}, here, None),
        ("reverse comparison off", one(with_values(evaluation={"reverseComparison": False})), {}, here, None),
        ("a time limit as a whole number", one(with_values(evaluation={"budgetMinutes": 45})), {}, here, None),
        ("a time limit as a float", one(with_values(evaluation={"budgetMinutes": 12.5})), {}, here, None),
        ("a time limit as 90.0", one(b'{"schemaVersion": 1, "evaluation": {"budgetMinutes": 90.0}}'), {}, here, None),
        ("live switch check ask", one(with_values(evaluation={"liveSwitchCheck": "ask"})), {}, here, None),
        ("live switch check always, with its machine", one(with_values(
            evaluation={"liveSwitchCheck": "always"},
            liveSwitchCheck={"instance": "D:/lab", "shortcut": "D:/lab/go.lnk", "gameExe": "D:/g/Skyrim.exe", "devbenchPort": 8921, "minFreeGb": 8.5})), {}, here, None),
        ("depth standard", one(with_values(evaluation={"depth": "standard"})), {}, here, None),
        ("every kind of value, nulls, and options newer than the file", one(js({
            "schemaVersion": 1,
            "saved": {"utc": "2026-10-01T00:00:00Z", "options": ["evaluation.depth", "evaluation.liveSwitchCheck", "report.mode", "video.make", "gone.option", 5]},
            "evaluation": {"depth": "deep", "liveSwitchCheck": None, "reverseComparison": True, "budgetMinutes": None, "nexusLookups": False},
            "report": {"mode": "exhaustive", "pdf": "off", "maxPages": 12},
            "video": {"make": "ask", "maxSeconds": 75.5, "narration": {"engine": "qwen3", "voice": "caf\u00e9", "device": None}, "ffmpeg": "C:/tools/ffmpeg.exe"},
            "models": {"recordChecks": model, "rewriteExplanations": "off", "checkExplanations": "session"}}, indent=2, ensure_ascii=False)), {}, here, None),
        ("saved options empty: every option is new", one(js({"schemaVersion": 1, "saved": {"options": []}})), {}, here, None),
        ("saved options not a list", one(js({"schemaVersion": 1, "saved": {"options": "all"}})), {}, here, None),
        ("saved is null", one(js({"schemaVersion": 1, "saved": None})), {}, here, None),
        ("a byte-order mark and CRLF", one(b"\xef\xbb\xbf" + with_values(evaluation={"depth": "standard"}).replace(b"\n", b"\r\n")), {}, here, None),
        ("ffmpeg from the environment beats the file", one(with_values(video={"ffmpeg": "C:/file/ffmpeg.exe"})), {"HOUSECARL_FFMPEG": "C:/env/ffmpeg.exe"}, here, None),
        ("ffmpeg from the environment, no file", {}, {"HOUSECARL_FFMPEG": " D:/ff.exe "}, here, None),
        # flags over the file
        ("a depth flag beats the file", one(with_values(evaluation={"depth": "deep", "reverseComparison": False, "budgetMinutes": 30})), {}, here,
         {"evaluation.depth": "standard"}),
        ("a reverse flag and a budget flag beat the file", one(with_values(evaluation={"reverseComparison": True, "budgetMinutes": 30})), {}, here,
         {"evaluation.reverseComparison": False, "evaluation.budgetMinutes": 5.0}),
        ("a depth flag that cannot combine with the file", one(with_values(evaluation={"liveSwitchCheck": "ask"})), {}, here, {"evaluation.depth": "standard"}),
        ("a depth flag that repairs the file's combination", one(with_values(evaluation={"depth": "standard", "liveSwitchCheck": "ask"})), {}, here,
         {"evaluation.depth": "deep"}),
        # where the file is looked for
        ("the given path beats every variable",
         {"given.json": with_values(evaluation={"budgetMinutes": 1}), "named.json": with_values(evaluation={"budgetMinutes": 2}),
          "data/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 3}), "local/houseCARL/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 4})},
         {"HOUSECARL_EVAL_SETTINGS": BACK + "\\named.json", "HOUSECARL_DATA_DIR": BACK + "\\data", "LOCALAPPDATA": BACK + "\\local"}, BACK + "\\given.json", None),
        ("HOUSECARL_EVAL_SETTINGS beats the data folder",
         {"named.json": with_values(evaluation={"budgetMinutes": 2}), "data/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 3}),
          "local/houseCARL/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 4})},
         {"HOUSECARL_EVAL_SETTINGS": "  " + FWD + "/named.json\t", "HOUSECARL_DATA_DIR": BACK + "\\data", "LOCALAPPDATA": BACK + "\\local"}, None, None),
        ("HOUSECARL_DATA_DIR beats LOCALAPPDATA, and a blank variable is skipped",
         {"data/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 3}), "local/houseCARL/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 4})},
         {"HOUSECARL_EVAL_SETTINGS": "  ", "HOUSECARL_DATA_DIR": " " + BACK + "\\data\\ ", "LOCALAPPDATA": BACK + "\\local"}, None, None),
        ("LOCALAPPDATA last, and an empty given path is no path",
         {"local/houseCARL/houseCARL.evaluation.json": with_values(evaluation={"budgetMinutes": 4})},
         {"HOUSECARL_EVAL_SETTINGS": "", "HOUSECARL_DATA_DIR": " ", "LOCALAPPDATA": BACK + "\\local"}, "", None),
        # refused files
        ("not JSON, line 1", one(b"{schemaVersion: 1}"), {}, here, None),
        ("not JSON, a trailing comma on line 4", one(b'{\n "schemaVersion": 1,\n "evaluation": {"depth": "deep"},\n}\n'), {}, here, None),
        ("not JSON, CRLF lines", one(b'{\r\n "schemaVersion": 1,\r\n\r\n "evaluation" {}\r\n}'), {}, here, None),
        ("not JSON, cut short", one(b'{\n "schemaVersion": 1,\n "evaluation": {\n  "depth": "de'), {}, here, None),
        ("not JSON, text after the document", one(b'{"schemaVersion": 1}\n\n}'), {}, here, None),
        ("not JSON, a bad escape", one(b'{"schemaVersion": 1,\n\n "a": "x\\q"}'), {}, here, None),
        ("not JSON, a raw line break in a string", one(b'{"schemaVersion": 1,\n "a": "x\ny"}'), {}, here, None),
        ("not JSON, empty", one(b""), {}, here, None),
        ("not JSON, only blank lines", one(b"\n\n  \n"), {}, here, None),
        ("not UTF-8", one(b'{"schemaVersion": 1, "x": "\xff\xfe"}'), {}, here, None),
        ("a list", one(b"[]"), {}, here, None),
        ("text", one(b'"deep"'), {}, here, None),
        ("no schemaVersion", one(js({"evaluation": {}})), {}, here, None),
        ("schemaVersion true", one(js({"schemaVersion": True})), {}, here, None),
        ("schemaVersion 1.0", one(b'{"schemaVersion": 1.0}'), {}, here, None),
        ("schemaVersion text", one(js({"schemaVersion": "1"})), {}, here, None),
        ("a newer schema", one(js({"schemaVersion": 2})), {}, here, None),
        ("schema 0", one(js({"schemaVersion": 0})), {}, here, None),
        ("a negative schema", one(js({"schemaVersion": -3})), {}, here, None),
        ("an unknown option", one(with_values(evaluation={"depth": "deep", "speed": "fast"})), {}, here, None),
        ("an unknown section holding a value", one(with_values(colour="blue")), {}, here, None),
        ("an unknown section holding a section", one(with_values(colour={"shade": {"of": "blue"}})), {}, here, None),
        ("an option given a section", one(with_values(evaluation={"depth": {"x": 1}})), {}, here, None),
        ("an enum value outside its list", one(with_values(evaluation={"depth": "shallow"})), {}, here, None),
        ("an enum given true", one(with_values(evaluation={"liveSwitchCheck": True})), {}, here, None),
        ("an enum given a list", one(with_values(report={"mode": ["concise"]})), {}, here, None),
        ("a bool given text", one(with_values(evaluation={"reverseComparison": "no"})), {}, here, None),
        ("a bool given 0", one(with_values(evaluation={"nexusLookups": 0})), {}, here, None),
        ("a whole number given 0", one(with_values(report={"maxPages": 0})), {}, here, None),
        ("a whole number given a float", one(with_values(report={"maxPages": 3.0})), {}, here, None),
        ("a whole number given true", one(with_values(liveSwitchCheck={"devbenchPort": True})), {}, here, None),
        ("a number below zero", one(with_values(evaluation={"budgetMinutes": -1})), {}, here, None),
        ("a number given text", one(with_values(evaluation={"budgetMinutes": "45"})), {}, here, None),
        ("a number given false", one(with_values(evaluation={"budgetMinutes": False})), {}, here, None),
        ("a number given NaN", one(b'{"schemaVersion": 1, "evaluation": {"budgetMinutes": NaN}}'), {}, here, None),
        ("a path that is blank", one(with_values(liveSwitchCheck={"instance": " \t"})), {}, here, None),
        ("a path given a number", one(with_values(video={"ffmpeg": 7})), {}, here, None),
        ("text that is empty", one(with_values(video={"narration": {"voice": ""}})), {}, here, None),
        ("text with a quote and an accent", one(js({"schemaVersion": 1, "video": {"narration": {"voice": {"a\"b": "caf\u00e9"}}}}, ensure_ascii=False)), {}, here, None),
        ("a model turned off where it cannot be", one(with_values(models={"recordChecks": "off"})), {}, here, None),
        ("a model by a short name", one(with_values(models={"imageReview": "haiku"})), {}, here, None),
        ("a model with no agent", one(with_values(models={"rewriteExplanations": "claude-nothing-9"})), {}, here, None),
        ("a model given a number", one(with_values(models={"flipPlan": 5})), {}, here, None),
        ("a model given a number, where off is allowed", one(with_values(models={"rewriteExplanations": 5})), {}, here, None),
        ("standard depth with the live check on ask", one(with_values(evaluation={"depth": "standard", "liveSwitchCheck": "ask"})), {}, here, None),
        ("standard depth with the live check on always", one(with_values(evaluation={"depth": "standard", "liveSwitchCheck": "always"})), {}, here, None),
        ("the live check always, with nothing set", one(with_values(evaluation={"liveSwitchCheck": "always"})), {}, here, None),
        ("the live check always, with only a shortcut", one(with_values(evaluation={"liveSwitchCheck": "always"}, liveSwitchCheck={"shortcut": "x.lnk"})), {}, here, None),
        ("the first bad value in file order wins", one(js({"schemaVersion": 1, "report": {"maxPages": -1}, "evaluation": {"depth": "x"}})), {}, here, None),
        ("raises: saved is text", one(js({"schemaVersion": 1, "saved": "yesterday"})), {}, here, None),
    ]
    return t


def settings_cases(tmp: Path) -> dict:
    keep = {k: os.environ.get(k) for k in ENV_KEYS}
    rows = []
    try:
        for n, (name, files, env, explicit, flags) in enumerate(settings_table()):
            root = tmp / f"c{n}"
            root.mkdir()
            for rel, data in files.items():
                p = root / rel
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_bytes(data)
            for k in ENV_KEYS:
                os.environ.pop(k, None)
            for k, v in env.items():
                os.environ[k] = unmask(v, root)
            row = {"name": name, "files": {rel: b64(data) for rel, data in files.items()}, "env": env, "explicit": explicit, "flags": flags}
            try:
                r = SET.load(unmask(explicit, root) if explicit is not None else None, flags)
                row["record"] = json.dumps(mask(r.record(), root), separators=(",", ":"), ensure_ascii=False)  # insertion order kept
                row["path"] = mask(str(r.path), root)
                row["values"] = {k: r.value(k) for k in ("evaluation.reverseComparison", "evaluation.budgetMinutes",
                                                          "evaluation.liveSwitchCheck", "evaluation.depth")}
                row["sentences"] = r.sentences()
            except SET.SettingsError as ex:
                row["error"] = mask(str(ex), root)
            except AttributeError as ex:
                row["raises"] = str(ex)
            if name.startswith("raises:") != ("raises" in row):
                raise SystemExit(f"settings case {name!r}: {row}")
            rows.append(row)
    finally:
        for k, v in keep.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    # The accepted model IDs come from a folder of agent definitions; the same reader on a folder made here.
    agents = {
        "b-second.md": "---\nname: model-b\nmodel: claude-b-1\n---\nbody\n",
        "A-first.MD": "---\r\n name : model-a \r\nmodel:claude-a-1\r\ndescription: has: colons\r\n---\r\nmodel: not-this\r\n",
        "c-repeat.md": "---\nname: model-a-again\nmodel: claude-a-1\n---\n",
        "d-no-name.md": "---\nmodel: claude-d-1\n---\n",
        "e-no-fence.md": "name: model-e\nmodel: claude-e-1\n",
        "f-open.md": " --- \nmodel: claude-f-1\nname: model-f",
        "g-empty.md": "",
        "notes.txt": "---\nname: n\nmodel: claude-n-1\n---\n",
        "h-\u00e9.md": "---\nname: mod\u00e8le\nmodel: claude-\u00e9-1\n---\n",
    }
    folder = tmp / "agents"
    folder.mkdir()
    for name, text in agents.items():
        (folder / name).write_bytes(text.encode("utf-8"))
    return {"modelIds": list(SET.MODEL_AGENTS), "agents": {"files": agents, "ids": list(SET._model_agents(folder)),
                                                           "missingFolder": list(SET._model_agents(tmp / "no-such-folder"))},
            "cases": rows}


# ------------------------------------------------------------------ states

def switch(key, edid=None, status=None, **more):
    s = {"key": key, "edid": edid, **more}
    if status is not False:
        s["trigger"] = {"status": status} if status else None
    return s


CENSUSES = {
    "an empty census": {},
    "a census as switchprobe writes one": {
        "generated": "2026-11-18 00:00:00Z", "instance": "D:/i", "profile": "p", "plugins": 3, "unreadable": [],
        "summary": {"switches": 4, "disabledSingletons": 7, "playerParentRefs": 0, "unplacedParentRefs": 2,
                    "triggers": {"triggered": 2, "boundUnread": 1, "unknown": 1, "jointFlips": 3}},
        "switches": [
            switch("000802:B.esp", "MarkerB", "Triggered", owner="B.esp", cellKey="000900:B.esp", baselineResolved=True, cycle=False),
            switch("000801:A.esp", "MarkerA", "BoundUnread", owner="A.esp", baselineResolved=True, cycle=False),
            switch("000803:A.esp", None, "Unknown", baselineResolved=True, cycle=False),
            switch("000804:A.esp", "caf\u00e9 \u2028 marker", None, baselineResolved=True, cycle=False),
        ],
        "jointFlips": [
            {"stage": "QuestB:20", "switches": ["MarkerA", "MarkerB"]},
            {"stage": "QuestA:10", "switches": ["MarkerA"]},
            {"stage": "QuestC:5", "switches": ["000803:A.esp", "MarkerB", "000803:A.esp", "caf\u00e9 \u2028 marker"]},
        ],
    },
    "every reason a switch or a stage is not explored": {
        "unreadable": ["Broken.esp", "Worse.esp"],
        "summary": {"disabledSingletons": 0, "playerParentRefs": 3.9, "unplacedParentRefs": "12", "triggers": None},
        "switches": [
            switch("0003:Z.esp", "Shared", "Triggered"),
            switch("0001:Z.esp", "Shared", "Triggered"),
            switch("0002:Z.esp", "Loop", "Triggered", cycle=True),
            switch("0004:Z.esp", "Unresolved", "Triggered", baselineResolved=False),
            switch(None, "NoKey", "Triggered"),
            switch("", None, "Triggered"),
            switch("0005:Z.esp", "Good", False),
            switch("0005:Z.esp", "Good", False),
            switch("0006:Z.esp", "", "Triggered", baselineResolved=None, cycle=0),
            switch("0007:Z.esp", "0006:Z.esp", "Triggered"),
        ],
        "jointFlips": [
            {"stage": "Q:9", "switches": ["Missing", "Good"]},
            {"stage": "Q:8", "switches": ["Shared"]},
            {"stage": "Q:7", "switches": ["Good", "Loop"]},
            {"stage": "Q:6", "switches": ["Good", "0005:Z.esp", "Good"]},
            {"stage": "Q:5", "switches": ["Good", "0001:Z.esp"]},
            {"stage": "Q:4", "switches": []},
            {"stage": "Q:3"},
            {"stage": None, "switches": ["Good"]},
            {"stage": 10, "switches": ["0006:Z.esp"]},
            {"stage": "Q:2", "switches": ["NoKey"]},
            {"stage": "Q:1", "switches": [5, "Good"]},
        ],
    },
    "a census with no summary and no trigger key": {
        "switches": [switch("00AA:M.esm", "One", "Triggered"), switch("00AB:M.esm", "Two", "BoundUnread")],
        "jointFlips": [{"stage": "S:1", "switches": ["One", "Two"]}, {"stage": "S:0", "switches": ["Two", "One", "Two"]}],
        "summary": {"disabledSingletons": True, "playerParentRefs": None},
    },
    "ordering by key, then editor id, by code point": {
        "switches": [switch("b", "x", "Triggered"), switch("B", "y", "Triggered"), switch("a", "\U0001F600", "Triggered"),
                     switch("a", "\uff5a", "Triggered"), switch("\u00e9", None, "Triggered"), switch("a", "Z", "Triggered")],
        "jointFlips": [{"stage": "b:1", "switches": ["x", "y"]}, {"stage": "B:1", "switches": ["y", "x"]},
                       {"stage": "\U0001F600", "switches": ["x", "\u00e9"]}, {"stage": "\uff5a", "switches": ["\u00e9", "x"]}],
        "summary": {"triggers": {}},
    },
}


def state_cases(tmp: Path) -> dict:
    rows = []
    for n, (name, census) in enumerate(CENSUSES.items()):
        data = json.dumps(census, ensure_ascii=(n % 2 == 0), separators=(",", ":")).encode("utf-8")
        rel = f"st{n}/sub/../census.json" if n == 1 else f"st{n}/census.json"
        p = tmp / f"st{n}" / "census.json"
        p.parent.mkdir(parents=True)
        (p.parent / "sub").mkdir()
        p.write_bytes(data)
        doc = SG.generate_file(Path(str(tmp) + "\\" + rel.replace("/", "\\")))
        rows.append({"name": name, "census": b64(data), "path": rel, "document": SG.dumps(mask(doc, tmp)), "specs": SG.specs_text(doc)})
    return {"generatorSha256": SG._sha_self(), "cases": rows}


# ------------------------------------------------------------------ the live check's population

def scope_of(*targets, complete=True):
    return {"complete": complete, "targets": list(targets)}


def placed_in(name, *cells):
    return {"name": name, "footprint": {"placed": {"cells": [{"cell": c} for c in cells]}}}


TRIGGERS = {"triggers": {"triggered": 1}}
POPULATIONS = {
    "nothing drives a switch": ({"summary": TRIGGERS, "switches": [switch("1:A.esp", "a", "Unknown", owner="A.esp"), switch("2:A.esp", "b", None, owner="A.esp"),
                                                                    switch("3:A.esp", "c", False, owner="A.esp"), "not a switch"]},
                                scope_of(placed_in("A.esp", "c1"))),
    "driven switches that touch no target": ({"summary": TRIGGERS, "switches": [switch("1:B.esp", "a", "Triggered", owner="B.esp", cellKey="c9"),
                                                                                 switch("2:B.esp", "b", "BoundUnread", owner="B.esp")]},
                                             scope_of(placed_in("A.esp", "c1"))),
    "touched by owner, placer, attacher and cell": ({"summary": TRIGGERS, "switches": [
        switch("1:a.ESP", "byOwner", "Triggered", owner="a.ESP"),
        switch("2:B.esp", "byPlacer", "BoundUnread", owner="B.esp", onSet=[{"placer": "X.esp"}, {"placer": "A.esp", "attacher": None}, "x"]),
        switch("3:B.esp", "byAttacher", "Triggered", owner=None, offSet=[{"attacher": "CAF\u00c9.esp"}]),
        switch("4:B.esp", "byCell", "Triggered", owner="B.esp", cellKey="c2"),
        switch("5:B.esp", "byMemberCell", "BoundUnread", owner="B.esp", cellKey="c9", onSet=[{"cellKey": "c8"}], offSet=[{"cellKey": "c1"}]),
        switch("6:B.esp", "untouched", "Triggered", owner="B.esp", cellKey="c9", onSet=None),
        switch("7:A.esp", "notDriven", "Unknown", owner="A.esp"),
    ]}, scope_of(placed_in("A.esp", "c1", "c2"), placed_in("caf\u00e9.esp"), {"name": ""}, "x", {"footprint": {}})),
    "a target whose cells are not listed": ({"summary": TRIGGERS, "switches": [
        switch("1:A.esp", "byOwner", "Triggered", owner="A.esp"), switch("2:B.esp", "elsewhere", "Triggered", owner="B.esp", cellKey="c1"),
        switch("3:B.esp", "elsewhere2", "BoundUnread", owner="B.esp")]},
        scope_of({"name": "A.esp", "footprint": {"placed": {"cells": None}}}, {"name": "C.esp"}, placed_in("D.esp", "c5"))),
    "cells not listed, nothing touched": ({"summary": TRIGGERS, "switches": [switch("2:B.esp", "elsewhere", "Triggered", owner="B.esp")]},
                                          scope_of({"name": "A.esp", "footprint": None})),
    "no trigger map": ({"summary": {"triggers": None}, "switches": []}, scope_of(placed_in("A.esp"))),
    "no summary at all": ({}, scope_of(placed_in("A.esp"))),
    "unreadable plugins, more than five": ({"summary": TRIGGERS, "unreadable": ["a.esp", "b.esp", 3, "d.esp", "e.esp", "f.esp", "g.esp"]}, scope_of(placed_in("A.esp"))),
    "scope not complete and no targets": ({"summary": TRIGGERS, "switches": [switch("1:A.esp", "a", "Triggered", owner="A.esp")]}, {"complete": "yes"}),
    "every cause at once": ({"unreadable": ["a.esp"], "switches": [switch("1:B.esp", "a", "Triggered", owner="B.esp")]},
                            scope_of({"name": "A.esp"}, complete=False)),
    "open with a population and a cause": ({"unreadable": ["a.esp"], "summary": TRIGGERS, "switches": [switch("1:A.esp", "a", "Triggered", owner="A.esp")]},
                                           scope_of(placed_in("A.esp"))),
    "nothing at all": ({"summary": TRIGGERS}, scope_of(placed_in("A.esp", "c1"))),
}


def population_cases() -> list:
    rows = []
    for name, (census, scope) in POPULATIONS.items():
        result = SFS.population(census, scope)
        rows.append({"name": name, "census": canon(census), "scope": canon(scope), "population": canon(result), "sentence": SFS.sentence(result)})
    return rows


# ------------------------------------------------------------------ meta.ini

META = {
    "a missing file": None,
    "an empty file": b"",
    "an ordinary file": b"[General]\r\ngameName=SkyrimSE\r\nmodid=12345\r\nversion=1.2\r\n[installedFiles]\r\n1\\modid=99\r\n1\\fileid=5\r\nsize=1\r\n",
    "no General section": b"modid=77\n",
    "modid 0": b"[General]\nmodid=0\n",
    "modid negative": b"[General]\nmodid=-5\n",
    "modid not a number": b"[General]\nmodid=abc\n",
    "modid empty": b"[General]\nmodid=\nversion=1\n",
    "modid under another section": b"[General]\nversion=1\n[Other]\nmodid=42\n",
    "modid before General": b"modid=11\n[General]\nmodid=22\n",
    "a byte-order mark": b"\xef\xbb\xbf[General]\nmodid=31\n",
    "CRLF line ends": b"[General]\r\nmodid=32\r\n",
    "an upper-case key": b"[General]\nModID=33\n",
    "an upper-case section": b"[GENERAL]\nmodid=34\n",
    "spaces around the equals sign": b"[General]\nmodid = 35\n",
    "spaces after the equals sign": b"[General]\nmodid=  36  \n",
    "an indented line": b"[General]\n   modid=37\n",
    "a quoted value": b"[General]\nmodid=\"38\"\n",
    "two modid lines": b"[General]\nmodid=abc\nmodid=39\n",
    "a plus sign": b"[General]\nmodid=+40\n",
    "a decimal point": b"[General]\nmodid=41.0\n",
    "digits that are not ASCII": "[General]\nmodid=\u0664\u0662\n".encode("utf-8"),
    "a value too large for 32 bits": b"[General]\nmodid=4294967297\n",
    "an installed file's modid only": b"[General]\nversion=1\n[installedFiles]\n1\\modid=43\n",
    "a comment after the value": b"[General]\nmodid=44 ; the id\n",
    "bytes that are not UTF-8": b"[General]\nname=\xff\xfe\nmodid=45\n",
    "a ByteArray value": b"[General]\nmodid=@ByteArray(46)\n",
    "a section with trailing text": b"[General] ; main\nmodid=47\n",
    "modidx key": b"[General]\nmodidx=48\n",
}


def meta_cases(tmp: Path) -> list:
    rows = []
    for n, (name, data) in enumerate(META.items()):
        folder = tmp / f"m{n}"
        folder.mkdir()
        if data is not None:
            (folder / "meta.ini").write_bytes(data)
        rows.append({"name": name, "bytes": None if data is None else b64(data), "python": meta_ini_mod_id(folder / "meta.ini")})
    return rows


# ------------------------------------------------------------------ Python's own rules for a JSON value

VALUES = [None, False, True, 0, 1, -1, 2, 1.0, 0.0, -0.0, 1.5, 2.9, -2.9, 10 ** 20, 1e20, float("inf"), "", "1", "a", " 12\n", "a\U0001F600",
          [], [1], [1.0], [True], [[]], {}, {"a": 1}, {"a": 1.0}, {"b": 1}, {"a": 1, "b": [2]}, {"b": [2.0], "a": True}]
INT_TEXTS = ["12", " 12\n", "+7", "-7", "1_000", "\u0663\u0664", "\uff11", "", "1__0", "_1", "1_", "1.0", "1,0", "- 1", "0x10", "\u00b2",
             "123456789012345678901234567890", "\x1c5\x1f", "1 2", "--1", "\U0001D7D8\U0001D7D9"]
STRIP_TEXTS = [" \t\x1cab\u3000\n", "\x85x\xa0", "\u200bx\u200b", "", "  ", "a b", "\x1fa\x1e", "\u2028a\u2029", "\ufeffa"]
DIGIT_TEXTS = ["12", "", "1a", "\u0663", "\u00b2", "\u2460", "\uff11\uff12", "1.0", "-1", " 1", "\U0001D7D8"]


def tried(f):
    try:
        return f()
    except (TypeError, ValueError, OverflowError):
        return None


def value_cases() -> dict:
    rows = []
    for v in VALUES:
        n = tried(lambda: int(v))
        rows.append({"v": json.dumps(v), "truthy": bool(v), "type": type(v).__name__, "len": tried(lambda: len(v)),
                     "int": None if n is None else str(n), "iter": tried(lambda: canon(list(v))),
                     "hashable": tried(lambda: hash(v)) is not None,
                     "eq": "".join("1" if v == w else "0" for w in VALUES)})
    numbers = [v for v in VALUES if isinstance(v, (int, float)) and v == v and abs(v) != float("inf")] + ["a", None, [1]]
    adds = [{"a": canon(a), "b": canon(b), "sum": tried(lambda: canon(a + b)) if not isinstance(a, (str, list)) and not isinstance(b, (str, list)) else None}
            for a in numbers for b in numbers]
    return {"values": rows,
            "intText": [{"text": t, "int": None if tried(lambda: int(t)) is None else str(int(t))} for t in INT_TEXTS],
            "strip": [{"text": t, "out": t.strip()} for t in STRIP_TEXTS],
            "isdigit": [{"text": t, "out": t.isdigit()} for t in DIGIT_TEXTS],
            "add": adds}


# ------------------------------------------------------------------ writing

def main() -> int:
    if os.name != "nt":
        print("make_known_answers_plan.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-plan", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-plan-")).resolve()
    try:
        registry = registry_cases()
        doc = {
            "python": platform.python_version(),
            "placeholders": {"backslash": BACK, "forward": FWD},
            "values": value_cases(),
            "registry": registry,
            "patterns": pattern_cases(registry["regexSpecs"]),
            "settings": settings_cases(tmp),
            "states": state_cases(tmp),
            "populations": population_cases(),
            "metaIni": meta_cases(tmp),
        }
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
