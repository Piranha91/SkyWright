"""Write plan-modules.json and plan-command.json: the known answers for runcontrol's `plan` command and for the
modules its `plan` and `run` commands need.

Every expected value comes from the real Python modules: evalregistry.py (validate, families, applicability),
evalsettings.py (load, value, record, sentences), stategen.py, switchflip_scope.py, modnames.meta_ini_mod_id, and the
`re` module for the pattern table; and, for plan-command.json, evalrun.py's plan functions and `evalrun.py plan` itself,
run as a child process over a small instance written here. Run it again only when one of those rules, or the check registry, changes on
purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_plan.py

The scope documents under scopes/ beside this file are scopeprobe's output for the synthetic fixture world, with the
machine's folder replaced by C:/fixture; the rest of the inputs are written here. Nothing outside a temporary folder is read or written except the registry, the scopes and the output.
"""
from __future__ import annotations

import base64
import copy
import gzip
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
OUT_PLAN = HERE / "plan-command.json"
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


# ------------------------------------------------------------------ the plan command

def plugin_bytes(**groups) -> bytes:
    """A plugin of a TES4 header and one top-level group per name, each holding that many empty records."""
    def record(sig: bytes, body: bytes = b"") -> bytes:
        return sig + len(body).to_bytes(4, "little") + bytes(16) + body
    data = record(b"TES4", b"HEDR" + (12).to_bytes(2, "little") + bytes(12))
    for label, count in groups.items():
        body = b"".join(record(label.encode("ascii")) for _ in range(count))
        data += b"GRUP" + (24 + len(body)).to_bytes(4, "little") + label.encode("ascii") + bytes(12) + body
    return data


def nested_plugin() -> bytes:
    """A CELL group of one record and a nested group holding two more; then a group cut short and bytes that are no group."""
    rec = b"CELL" + (0).to_bytes(4, "little") + bytes(16)
    inner = b"GRUP" + (24 + 2 * 24).to_bytes(4, "little") + bytes(16) + b"REFR" + bytes(20) + b"REFR" + bytes(20)
    body = rec + inner
    top = b"GRUP" + (24 + len(body)).to_bytes(4, "little") + b"CELL" + bytes(12) + body
    short = b"GRUP" + (8).to_bytes(4, "little") + b"W\xffTR" + bytes(12)   # a size below a header's own, and a label that is not ASCII
    return plugin_bytes() + top + short + b"JUNKJUNKJUNKJUNKJUNKJUNKJUNK"


ARM_DELTAS = [
    [],
    [["A.esp", "B.esp"]],
    [["A.esp", "B.esp"], ["A.esp", "B.esp"]],
    [["A.esp", "B.esp"], ["a.ESP", "C.esp"]],
    [["A.esp", "B.esp", "C.esp"], ["C.esp", "A.esp", "B.esp"]],
    [["A.esp", "B.esp", "C.esp", "D.esp"], ["A.esp", "C.esp", "B.esp", "D.esp"], ["d.esp", "A.esp"]],
    [["Z.esp", "b.esp", "A.esp"], ["b.esp", "Z.esp", "E.esp"], []],
    [["A.esp", "A.esp", "B.esp"], ["B.esp", "A.esp"]],
    [["\u00c9.esp", "e.esp"], ["e.esp", "\u00e9.esp", "Zz.esp"]],
    [[], ["A.esp"]],
]

TOPO = [
    [],
    [{"id": "a"}, {"id": "b", "prerequisites": ["a"]}],
    [{"id": "b", "prerequisites": ["a"]}, {"id": "a"}],
    [{"id": "d", "prerequisites": ["c", "b"]}, {"id": "c", "prerequisites": ["a"]}, {"id": "b", "prerequisites": ["a", "ghost"]}, {"id": "a"}, {"id": "e"}],
    [{"id": "a", "prerequisites": None}, {"id": "a"}, {"id": "b", "prerequisites": ["a", "a"]}],
]

FINGERPRINTS = [
    {"plugins": [{"name": "Skyrim.esm", "sha256": "ab" * 32}, {"name": "CAF\u00c9.esp", "sha256": "cd" * 32}],
     "providers": [{"name": "overwrite", "kind": "overwrite"}, {"name": "Some Mod", "kind": "mod"}, {"name": "Data", "kind": "data"}],
     "overlays": [{"kind": "bos-ini", "sha256": "ef" * 32}], "population": "load-time", "state": "baseline"},
    {"plugins": [], "providers": [], "overlays": [], "population": "load-time+population:0123456789ab", "state": "STATE-x"},
    {"plugins": [{"name": "A.esp", "sha256": "01" * 32}], "providers": [], "overlays": [], "population": None, "state": None},
]


def tree_of(root: Path) -> dict:
    """Every file under root as {relative path: base64}, and the folders that hold none."""
    files, dirs = {}, []
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root).as_posix()
        if p.is_file():
            files[rel] = b64(p.read_bytes())
        elif not any(p.iterdir()):
            dirs.append(rel)
    return {"files": files, "dirs": dirs}


def plan_unit_cases(tmp: Path) -> dict:
    import evalrun as ER
    root = tmp / "units"
    plugins = {"plain.esp": plugin_bytes(), "coll.esp": plugin_bytes(STAT=2, COLL=1), "race.esp": plugin_bytes(RACE=3, STAT=1),
               "emptycoll.esp": plugin_bytes(COLL=0), "nested.esp": nested_plugin(), "nothing.esp": b"", "text.esp": b"not a plugin at all, just text",
               "twice.esp": plugin_bytes(RACE=1) + b"GRUP" + (48).to_bytes(4, "little") + b"RACE" + bytes(12) + b"RACE" + bytes(20)}
    for name, data in plugins.items():
        (root / "p").mkdir(parents=True, exist_ok=True)
        (root / "p" / name).write_bytes(data)
    inst = root / "inst"
    for rel in ("mods/Plain/a.txt", "mods/Swap/meshes/x.nif", "mods/Swap/z/Thing_SWAP.ini", "mods/Swap/a/b/other_swap.INI", "mods/TopSwap/sub/deep_swap.ini",
                "mods/TopSwap/top_swap.ini", "mods/NotSwap/swap.ini", "mods/NotSwap/x_swap.ini.bak"):
        (inst / rel).parent.mkdir(parents=True, exist_ok=True)
        (inst / rel).write_bytes(b"x")
    outside = root / "elsewhere" / "Patch Folder"
    outside.mkdir(parents=True)
    (outside / "keep.txt").write_bytes(b"x")

    def row(name):
        return {"name": name, "path": str(root / "p" / name), "sha256": hashlib.sha256(plugins[name]).hexdigest()}
    table = [
        ("one plain plugin, no folder", [row("plain.esp")], [], ["plain.esp"], None),
        ("a missing plugin file is read as no groups", [{"name": "gone.esp", "path": str(root / "p" / "gone.esp"), "sha256": "0" * 64}], [], [], None),
        ("fixed paths, nothing on disk", [{"name": "Caf\u00e9 \U0001F600.esp", "path": "Q:\\nowhere\\Caf\u00e9 \U0001F600.esp", "sha256": "ab" * 32}], [], ["T.esp", "Caf\u00e9.esp"], None),
        ("a plugin with collision layers", [row("plain.esp"), row("coll.esp"), row("race.esp")], ["Plain"], [], str(inst)),
        ("an empty collision-layer group is not a carrier", [row("emptycoll.esp")], [], [], None),
        ("race carriers, a nested group, bytes that are not a plugin", [row("race.esp"), row("nested.esp"), row("nothing.esp"), row("text.esp"), row("twice.esp")], ["Plain"], ["race.esp"], str(inst)),
        ("a folder with no instance", [row("plain.esp")], ["Plain"], [], None),
        ("a folder that is not there", [row("plain.esp")], ["Plain", "Ghost"], [], str(inst)),
        ("a swap ini below the top", [], ["Plain", "Swap"], [], str(inst)),
        ("a swap ini at the top is named first", [], ["TopSwap"], [], str(inst)),
        ("names that only look like a swap ini", [], ["NotSwap", "Plain"], ["x.esp"], str(inst)),
        ("case l1b a population takes a mod folder named by absolute path", [row("plain.esp")], [str(outside), "Plain"], [], str(inst)),
    ]
    files = []
    for name, rows, mods, targets, instance in table:
        made = ER.population_file(rows, mods, targets, instance)
        rooted = not isinstance(made, str) and str(root).replace("\\", "\\\\") in made[0]
        files.append({"name": name, "plugins": mask(rows, root), "mods": mask(mods, root), "targets": targets, "instance": mask(instance, root),
                      "refusal": mask(made, root) if isinstance(made, str) else None,
                      "document": None if isinstance(made, str) else mask(json.loads(made[0]), root),
                      "rawSha256": None if isinstance(made, str) or rooted else made[1],
                      "raceCarriers": None if isinstance(made, str) else made[4]})
    fingerprints = [{**f, "python": ER.configuration_fingerprint(f["plugins"], f["providers"], f["overlays"], f["population"], f["state"])} for f in FINGERPRINTS]
    return {
        "tree": tree_of(root),
        "armDelta": [{"lists": lists, "delta": ER.arm_delta(lists)} for lists in ARM_DELTAS],
        "topGroups": [{"name": name, "groups": ER.plugin_top_groups_from_bytes(data)} for name, data in plugins.items()],
        "populationFiles": files, "fingerprints": fingerprints,
        "topoOrder": [{"checks": checks, "order": [c["id"] for c in ER.topo_order(checks)]} for checks in TOPO],
    }


FAKE_PREFLIGHT = ('@echo off\r\n'
                  'if exist "%~3.verdict" (type "%~3.verdict" > "%~5") else '
                  '(echo {"verdict":"passed","providers":["overwrite","Data"],"omittedProviderCount":0,"reorderedProviders":[]}> "%~5")\r\n')
SILENT_PREFLIGHT = '@echo off\r\necho first line\r\necho the probe could not start today\r\necho.\r\nexit /b 3\r\n'


def plan_world(root: Path) -> dict:
    """The fixture every plan case starts from: an instance, a game, an empty harness, the registry, a scope whose
    plugins are the instance's files, candidate sets and a stand-in for overlapprobe's scene preflight."""
    files = {
        "instance/mods/WorldMod/HcWbWorld.esm": plugin_bytes(CELL=1),
        "instance/mods/SelMod/HcWbSel.esp": plugin_bytes(CELL=1, REFR=1),
        "instance/mods/SelMod/meshes/sel.nif": b"nif",
        "instance/mods/OffMod/Extra.esp": plugin_bytes(STAT=1),
        "instance/mods/OffMod/Sup.esp": plugin_bytes(STAT=2),
        "instance/mods/DisMod/Sup.esp": plugin_bytes(STAT=3),
        "instance/mods/AltMod/HcWbWorld.esm": plugin_bytes(CELL=2),
        "instance/mods/CopyMod/HcWbWorld.esm": plugin_bytes(CELL=1),
        "instance/mods/Stray/Stray.esp": plugin_bytes(STAT=4),
        "instance/overwrite/Over.esp": plugin_bytes(STAT=5),
        "instance/profiles/P/modlist.txt": b"# This file was automatically generated by Mod Organizer.\r\n+SelMod\r\n+Looks_separator\r\n+WorldMod\r\n+OffMod\r\n-DisMod\r\n-AltMod\r\n-CopyMod\r\n*Unmanaged\r\n",
        "instance/profiles/NoList/note.txt": b"a profile folder with no modlist.txt",
        "instance/profiles/Bom/modlist.txt": b"\xef\xbb\xbf+OffMod\r\n+SelMod\r\n+WorldMod\r\n",
        "game/Data/Data.esm": plugin_bytes(STAT=6),
        "game/Data/Sup.esp": plugin_bytes(STAT=7),
        "harness/readme.txt": b"no probe is built here",
        "fake.cmd": FAKE_PREFLIGHT.encode("ascii"),
        "silent.cmd": SILENT_PREFLIGHT.encode("ascii"),
        "reg/check-registry.json": REGISTRY.read_bytes(),
        "settings/standard.json": js({"schemaVersion": 1, "evaluation": {"depth": "standard"}}),
        "table.json": js([{"base": "0001:Skyrim.esm", "layer": 5, "solid": True, "evidence": "station Q run 7"}, {"evidence": " frame 12 "}], indent=1),
        "table2.json": js([{"evidence": "another table"}]),
        "bad-scope.json": b"{ not json",
    }
    for rel, data in files.items():
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        (root / rel).write_bytes(data)
    sha = {rel: hashlib.sha256(data).hexdigest() for rel, data in files.items()}
    scope = json.loads((HERE / "scopes" / "candidate-01.json").read_text(encoding="utf-8"))
    for p in scope["configuration"]["plugins"]:
        rel = f"instance/mods/{p['Provider']}/{p['Name']}"
        p["Path"], p["Sha256"] = f"{BACK}\\" + rel.replace("/", "\\"), sha[rel]
    for t in scope["targets"]:
        t["sha256"] = sha["instance/mods/SelMod/HcWbSel.esp"]
    bare = {"complete": True, "targets": [{"name": "HcWbSel.esp"}, "x", {"name": ""}],
            "Configuration": {"Plugins": [{"Name": "HcWbWorld.esm", "Path": f"{BACK}\\instance\\mods\\WorldMod\\HcWbWorld.esm", "Sha256": "not a hash", "Provider": "WorldMod"},
                                          {"Name": "HcWbSel.esp", "Path": f"{BACK}\\instance\\mods\\SelMod\\HcWbSel.esp", "Sha256": sha["instance/mods/SelMod/HcWbSel.esp"].upper()}],
                              "Providers": [{"Name": "WorldMod", "Kind": "Mod"}], "Population": "", "Fingerprint": "F", "Source": "S"}}
    # the two scopes name the case's own folder: they are kept with the placeholder and written out per case
    templates = {"scope.json": json.dumps(scope, indent=1), "bare-scope.json": json.dumps(bare)}

    def cand(folder, name, **doc):
        (root / "cands" / folder).mkdir(parents=True, exist_ok=True)
        (root / "cands" / folder / name).write_text(json.dumps(doc, indent=1), encoding="utf-8", newline="\n")
    world, sel = "HcWbWorld.esm", "HcWbSel.esp"
    for folder in ("two", "three", "sup", "dup", "search"):
        cand(folder, "candidate-00-baseline.json", Plugins=[world], Mods=["WorldMod"], Changes=["the profile without the selection"],
             Provenance={"arm": "without-selection", "keys": []})
        cand(folder, "candidate-01.json", Plugins=[world, sel], Mods=["SelMod", "WorldMod"], Changes=[], Provenance={"arm": "combined"})
    cand("three", "candidate-02.json", plugins=[sel, world], changes=["swapped"], provenance={"arm": "swapped"})
    cand("three", "Candidate-03.JSON", Plugins=[world, sel], mods=[], Mods=None)
    cand("sup", "candidate-09-sup.json", Plugins=[world, sel, "Sup.esp", "Extra.esp", "Nowhere.esp", "Over.esp", "Data.esm"], Mods=["SelMod", "WorldMod"])
    cand("dup", "candidate-02.json", Plugins=[world, sel], Mods=["AltMod", "SelMod", "WorldMod"])
    cand("dup", "candidate-03.json", Plugins=[world, sel], Mods=["CopyMod", "SelMod", "WorldMod"])
    cand("dup", "candidate-04.json", Plugins=[world, sel], Mods=["SelMod", "GhostMod", "WorldMod", " "])
    (root / "cands" / "search" / "candidates.json").write_text(json.dumps({"Budget": {"Asked": 8, "Kept": 2}, "Candidates": []}), encoding="utf-8")
    (root / "cands" / "empty").mkdir(parents=True)
    (root / "cands" / "nobase").mkdir(parents=True)
    (root / "cands" / "nobase" / "candidate-01.json").write_text("{}", encoding="utf-8")
    (root / "cands" / "twobase").mkdir(parents=True)
    for n in ("candidate-00-baseline.json", "candidate-01-baseline.json"):
        (root / "cands" / "twobase" / n).write_text("{}", encoding="utf-8")
    (root / "cands" / "afile").write_text("not a folder", encoding="utf-8")
    return {**tree_of(root), "templates": templates}


class Template(str):
    """A file's text that names the case's folder by the placeholder, as JSON spells it; written out per case."""


def J(doc, **kw) -> Template:
    return Template(json.dumps(doc, **kw))


def materialise(text: str, root: Path) -> bytes:
    return text.replace(json.dumps(BACK)[1:-1], json.dumps(str(root))[1:-1]).replace(FWD, str(root).replace("\\", "/")).encode("utf-8")


def plan_case_table() -> list:
    """(name, argv after `plan`, extra files {relative path: bytes or Template}, environment with placeholders)."""
    R, S, H, OUT = f"{BACK}\\reg\\check-registry.json", f"{BACK}\\scope.json", f"{BACK}\\harness", f"{BACK}\\run"
    INST, GAME, FAKE = f"{BACK}\\instance", f"{BACK}\\game", f"{BACK}\\fake.cmd"

    def base(cands="two", scope=S, registry=R):
        return ["--registry", registry, *(["--scope", scope] if scope else []), "--candidates", f"{BACK}\\cands\\{cands}", "--out", OUT, "--harness", H]
    here = ["--instance", INST, "--profile", "P", "--game", GAME, "--probe-cmd", f"overlapprobe={FAKE}"]
    mesh = {"verdict": "refused", "code": "view-lacks-installed-meshes", "reason": "the view lacks 3 installed meshes", "remedy": "add the folders"}
    live = ["--opt-in", "runtime.state-switch-flip"]
    one = "cands/two/candidate-01.json.verdict"
    ask = {"settings/ask.json": with_values(evaluation={"liveSwitchCheck": "ask"})}
    return [
        ("no instance: the scene preflight is not run and the population needs its folder", base(), {}, {}),
        ("an instance, a profile and a game: a scoped population", base() + here, {}, {}),
        ("no scope: applicability is unknown and nothing is targeted", base(scope=None), {}, {}),
        ("a scope in the other key spelling, with a hash that is not one", base(scope=f"{BACK}\\bare-scope.json") + here, {}, {}),
        ("flags joined with =, paths with forward slashes and a dot",
         [f"--registry={FWD}/reg/./check-registry.json", f"--candidates={FWD}/cands/two/", f"--out={FWD}/run", f"--harness={FWD}/harness",
          "--probe-args=--threads 4", "--diff-tolerance=0.5"], {}, {}),
        # one start each: with several, which check is named as implying a shared prerequisite follows Python's set order
        ("select by exe name, with its implied prerequisite", base() + ["--select", " overlapprobe,", "--select", ","], {}, {}),
        ("select by id, with the chain of prerequisites it implies", base() + ["--select", "patch.order-evaluation"], {}, {}),
        ("select every check an exe runs and one more that implies nothing new",
         base() + ["--select", "contactprobe", "--select", "loadability.masters-and-order,geometry.placement-contact"], {}, {}),
        ("select names nothing the registry has", base() + ["--select", "nope,overlapprobe", "--select", "also-nope"], {}, {}),
        ("exclude with reasons, one for a check that could not run",
         base() + ["--exclude", "geometry.object-overlap=too slow today", "--exclude", " records.contested-fields = no reader = here "], {}, {}),
        ("exclude without a reason", base() + ["--exclude", "geometry.object-overlap"], {}, {}),
        ("exclude with no check", base() + ["--exclude", " =because"], {}, {}),
        ("exclude names checks the registry does not define", base() + ["--exclude", "zz.check=why", "--exclude", "aa.check=why"], {}, {}),
        ("opt in to the live check", base() + ["--opt-in", "runtime.state-switch-flip, "], {}, {}),
        ("opt in to a check the registry does not have", base() + ["--opt-in", "it's.not-there"], {}, {}),
        ("opt in to a check that is not opt-in", base() + ["--opt-in", "geometry.object-overlap"], {}, {}),
        ("max candidates 0 holds every arm back", base("three") + ["--max-candidates", "0"], {}, {}),
        ("max candidates 1 holds the later arms back", base("three") + ["--max-candidates", "1"], {}, {}),
        ("max candidates -1 holds the last arm back", base("three") + ["--max-candidates", "-1"], {}, {}),
        ("max candidates above the arms", base("three") + ["--max-candidates", "12"], {}, {}),
        ("alt keeps the named arms", base("three") + ["--alt", "candidate-02.json", "--alt", "somewhere/else/Candidate-03.JSON"], {}, {}),
        ("alt names files that are not there", base("three") + ["--alt", "z.json", "--alt", "candidate-02.json", "--alt", "a.json"], {}, {}),
        ("a supplied arm: out of the view, missing, in the view",
         base("sup") + here + ["--alt", "candidate-01.json", "--supplied", "Sup.esp, Extra.esp ,sub/Nowhere.esp,HcWbSel.esp,Over.esp,Data.esm=candidate-09-sup.json"], {}, {}),
        ("a supplied arm with no instance", base("sup") + ["--alt", "candidate-01.json", "--supplied", "Sup.esp,HcWbSel.esp:candidate-09-sup.json"], {}, {}),
        ("a supplied arm with no game",
         base("sup") + ["--instance", INST, "--profile", "P", "--probe-cmd", f"overlapprobe={FAKE}", "--alt", "candidate-01.json", "--supplied", "Sup.esp=candidate-09-sup.json"], {}, {}),
        ("supplied without a separator", base("sup") + ["--supplied", "Sup.esp"], {}, {}),
        ("supplied with a file that is not json", base("sup") + ["--supplied", "Sup.esp=candidate-09-sup.txt"], {}, {}),
        ("supplied with a candidate that is not there", base("sup") + ["--supplied", "Sup.esp=sub/candidate-77.JSON"], {}, {}),
        ("supplied with no plugin", base("sup") + ["--supplied", "=candidate-09-sup.json"], {}, {}),
        ("case m3a plan --review-arm is frozen and moves the baseline-only checks", base() + ["--review-arm", "candidate-01"], {}, {}),
        ("case m3c a review arm the plan does not have is refused in one sentence", base("three") + ["--review-arm", "candidate-9"], {}, {}),
        ("states named by hand", base() + ["--states", " a, b,,baseline "], {}, {}),
        ("states named as nothing", base() + ["--states", ""], {}, {}),
        ("depth deep", base() + ["--depth", "deep"], {}, {}),
        ("no saved settings: the built-in depth", base(), {}, {"HOUSECARL_EVAL_SETTINGS": f"{BACK}\\settings\\none.json"}),
        ("settings by flag: a time limit, the live check always", base() + ["--settings", f"{FWD}/settings/live.json"],
         {"settings/live.json": with_values(evaluation={"liveSwitchCheck": "always", "budgetMinutes": 45},
                                            liveSwitchCheck={"instance": "D:/lab", "shortcut": "D:/lab/go.lnk", "gameExe": "D:/g/Skyrim.exe", "devbenchPort": 8921, "minFreeGb": 8.5})}, {}),
        ("settings: the live check asks, and nothing opted in", base(), ask, {"HOUSECARL_EVAL_SETTINGS": f"{BACK}\\settings\\ask.json"}),
        ("settings: the live check asks, and the flag opts in", base() + live, ask, {"HOUSECARL_EVAL_SETTINGS": f"{BACK}\\settings\\ask.json"}),
        ("settings: a file that is not valid is refused", base(), {"settings/bad.json": js({"schemaVersion": 1, "evaluation": {"depth": "shallow"}})},
         {"HOUSECARL_EVAL_SETTINGS": f"{BACK}\\settings\\bad.json"}),
        ("a time limit of 90 by flag is 90.0", base() + ["--budget-minutes", "90"], {}, {}),
        ("a time limit of 12.5 and no reverse comparison", base() + ["--budget-minutes", "12.5", "--no-reverse"], {}, {}),
        ("a time limit of 0", base() + ["--budget-minutes", "0"], {}, {}),
        ("the primitive table by flag", base() + ["--primitive-solidity", f"{FWD}/table.json"], {}, {"HOUSECARL_PRIMITIVE_SOLIDITY": f"{BACK}\\table2.json"}),
        ("the primitive table by its variable, over the harness config", base(), {"cfg.json": J({"primitiveSolidity": f"{BACK}\\table.json"})},
         {"HOUSECARL_PRIMITIVE_SOLIDITY": f"  {BACK}\\table2.json ", "HOUSECARL_HARNESS_CONFIG": f"{BACK}\\cfg.json"}),
        ("case 1730 the harness config's table is frozen with its sha256", base(), {"cfg.json": J({"primitiveSolidity": f" {BACK}\\table.json "})},
         {"HOUSECARL_HARNESS_CONFIG": f" {BACK}\\cfg.json "}),
        ("case 1731 a harness config that names no table: said in the plan", base(), {"cfg.json": js({"primitiveSolidity": "  ", "other": 1})},
         {"HOUSECARL_HARNESS_CONFIG": f"{BACK}\\cfg.json"}),
        ("case 1732 a named table that is absent refuses the plan", base() + ["--primitive-solidity", f"{BACK}\\no-table.json"], {}, {}),
        ("a primitive table that is not JSON", base() + ["--primitive-solidity", f"{BACK}\\bad-scope.json"], {}, {}),
        ("a primitive table that is not a list of measured entries", base() + ["--primitive-solidity", f"{BACK}\\t.json"], {"t.json": js([{"evidence": "x"}, {"evidence": "  "}])}, {}),
        ("a primitive table that is an empty list", base(), {"t.json": b"[]"}, {"HOUSECARL_PRIMITIVE_SOLIDITY": f"{BACK}\\t.json"}),
        ("a harness config that is not there", base(), {}, {"HOUSECARL_HARNESS_CONFIG": f"{BACK}\\no-cfg.json"}),
        ("a harness config that is not JSON", base(), {"cfg.json": b"[1, 2"}, {"HOUSECARL_HARNESS_CONFIG": f"{BACK}\\cfg.json"}),
        ("a harness config that is a list", base(), {"cfg.json": b"[1, 2]"}, {"HOUSECARL_HARNESS_CONFIG": f"{BACK}\\cfg.json"}),
        ("a delta plugin with collision layers leaves the plan unscoped", base() + here, {"instance/mods/SelMod/HcWbSel.esp": plugin_bytes(CELL=1, COLL=2)}, {}),
        ("a delta plugin with races keeps the state check on the whole order", base() + here, {"instance/mods/SelMod/HcWbSel.esp": plugin_bytes(CELL=1, RACE=2)}, {}),
        ("a delta mod folder with a swap ini leaves the plan unscoped", base() + here,
         {"instance/mods/SelMod/z/Later_SWAP.ini": b"x", "instance/mods/SelMod/Sub/first_swap.ini": b"x", "instance/mods/SelMod/meshes/zz_swap.ini": b"x"}, {}),
        ("arms with their own mod lists: another copy first, the same bytes first, a folder that is not there", base("dup") + here, {}, {}),
        ("arms with their own mod lists and no profile", base("dup") + ["--instance", INST, "--game", GAME, "--probe-cmd", f"overlapprobe={FAKE}"], {}, {}),
        ("the candidate search is frozen with its budget", base("search"), {}, {}),
        ("candidates that is not a folder", base("afile"), {}, {}),
        ("candidates with no candidate file", base("empty"), {}, {}),
        ("candidates with no baseline", base("nobase"), {}, {}),
        ("candidates with two baselines", base("twobase"), {}, {}),
        ("a scope that is not JSON", base(scope=f"{BACK}\\bad-scope.json"), {}, {}),
        ("a registry that is not valid", base(registry=f"{BACK}\\reg\\invalid.json"),
         {"reg/invalid.json": ("registry", [[["checks", 0, "family"], "set", "no-such-family"], [["registryVersion"], "set", "four"]])}, {}),
        ("a registry that is not there", base(registry=f"{BACK}\\reg\\missing.json"), {}, {}),
        ("a registry placeholder the controller cannot fill", base(registry=f"{BACK}\\reg\\placeholder.json"), {"reg/placeholder.json": ("registry-args", "{order} {no_such}")}, {}),
        ("no overlapprobe build", base() + ["--instance", INST, "--profile", "P", "--game", GAME], {}, {}),
        ("the scene preflight refuses an arm", base() + here, {one: js({"verdict": "refused", "code": "bad-view", "reason": "the view is wrong", "remedy": "fix the view"})}, {}),
        ("the scene preflight writes no verdict", base() + ["--instance", INST, "--profile", "P", "--game", GAME, "--probe-cmd", f"overlapprobe={BACK}\\silent.cmd"], {}, {}),
        ("the scene preflight writes a verdict that is not one", base() + here, {"cands/two/candidate-00-baseline.json.verdict": js({"verdict": "maybe"})}, {}),
        ("a mesh gap on an arm no planned check captures scenes on", base() + here + ["--select", "geometry.route-cut"], {one: js(mesh)}, {}),
        ("a mesh gap on an arm overlapprobe runs on", base() + here, {one: js(mesh)}, {}),
        ("a mesh gap with the live check opted in and no folders named", base() + here + live, {one: js(mesh)}, {}),
        ("a mesh gap with the live check opted in: the profile lacks a folder", base() + here + live, {one: js({**mesh, "addFolders": ["DisMod"]})}, {}),
        ("a mesh gap with the live check opted in: the profile orders them the other way", base() + here + live,
         {one: js({**mesh, "addFolders": ["OffMod"]}), "cands/two/candidate-01.json": js({"Plugins": ["HcWbWorld.esm", "HcWbSel.esp"], "Mods": ["WorldMod", "SelMod"]})}, {}),
        ("a mesh gap with the live check opted in: the profile holds the order", base() + here + live, {one: js({**mesh, "addFolders": ["OffMod"]})}, {}),
        ("an arm with no asset view, and one that states its own priority", base() + here,
         {"cands/two/candidate-00-baseline.json.verdict": js({"verdict": "refused", "code": "no-explicit-mods", "reason": "the candidate names no Mods"}),
          one: js({"verdict": "passed", "providers": None, "omittedProviderCount": 2, "reorderedProviders": ["SelMod", "WorldMod"]})}, {}),
        ("the instance from the environment", base() + ["--probe-cmd", f"overlapprobe={FAKE}"], {}, {"HOUSECARL_MO2_INSTANCE": INST}),
    ]


def owner_plan_cases() -> list:
    """Plans where the probes' own readers answer otherwise than evalrun.py's copies; Python's answer is recorded."""
    R, S, H, OUT = f"{BACK}\\reg\\check-registry.json", f"{BACK}\\scope.json", f"{BACK}\\harness", f"{BACK}\\run"
    common = ["--registry", R, "--scope", S, "--candidates", f"{BACK}\\cands\\sup", "--out", OUT, "--harness", H, "--game", f"{BACK}\\game",
              "--instance", f"{BACK}\\instance", "--probe-cmd", f"overlapprobe={BACK}\\fake.cmd", "--alt", "candidate-01.json"]

    def sup(plugin):
        return {"cands/sup/candidate-09-sup.json": js({"Plugins": ["HcWbWorld.esm", plugin], "Mods": ["SelMod", "WorldMod"]})}
    return [
        ("owner: with no profile named, a mod folder outside the view is not a layer", common + ["--supplied", "Stray.esp=candidate-09-sup.json"], sup("Stray.esp"), {}),
        ("owner: a profile with no modlist.txt has no mod folders outside the view",
         common + ["--profile", "NoList", "--supplied", "Stray.esp=candidate-09-sup.json"], sup("Stray.esp"), {}),
        ("owner: a modlist.txt with a byte-order mark keeps its first mod", common + ["--profile", "Bom", "--supplied", "Extra.esp=candidate-09-sup.json"], sup("Extra.esp"), {}),
    ]


def norm_plan(plan: dict, root: Path) -> str:
    """The plan as two controllers must agree on it: what names the controller, a moment or this machine's folder is
    replaced, key order kept."""
    plan["planId"], plan["createdUtc"], plan["runner"] = "<id>", "<utc>", "<runner>"
    if plan["scope"]["sha256"]:
        plan["scope"]["sha256"] = "<sha>"   # the scope file names this folder
    for pid, rec in plan["toolIdentity"]["programs"].items():
        if pid == "controller":
            plan["toolIdentity"]["programs"][pid] = "<controller>"
        elif rec.get("digest"):
            rec["digest"] = "<digest>"
    census = plan["states"].get("fromCensus")
    if census:
        census["generator"], census["sha256"] = "<generator>", "<sha>"
    pop = plan["population"]
    if pop.get("scoped"):
        pop["sha256"], pop["identity"] = "<sha>", pop["identity"][:-12] + "<sha12>"
    for c in plan["checks"]:
        if "population" in c["adapter"]:
            c["adapter"]["population"] = c["adapter"]["population"][:-12] + "<sha12>"
    return json.dumps(mask(plan, root), indent=1, ensure_ascii=False)


def run_plan_case(world: Path, templates: dict, root: Path, name: str, argv: list, files: dict, env: dict) -> dict:
    """One `evalrun.py plan` as a child process over a fresh copy of the world; what it printed and wrote."""
    import subprocess
    shutil.copytree(world, root)
    reg = json.loads(REGISTRY.read_text(encoding="utf-8"))
    recorded = {}
    for rel, data in {**templates, **files}.items():
        if isinstance(data, tuple) and data[0] == "registry":
            data = js(apply(reg, data[1]), indent=1)
        elif isinstance(data, tuple):
            doc = copy.deepcopy(reg)
            next(c for c in doc["checks"] if (c.get("adapter") or {}).get("args"))["adapter"]["args"] = data[1].split()
            data = js(doc, indent=1)
        if isinstance(data, str):
            if rel not in templates:
                recorded[rel] = {"template": str(data)}
            data = materialise(data, root)
        else:
            recorded[rel] = {"b64": b64(data)}
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        (root / rel).write_bytes(data)
    child = {k: v for k, v in os.environ.items() if not k.upper().startswith("HOUSECARL_")}
    child.update({"HOUSECARL_RUNLOG_DIR": str(root / "runlog"), "HOUSECARL_EVAL_SETTINGS": str(root / "settings" / "standard.json"),
                  "PYTHONIOENCODING": "utf-8", "PYTHONDONTWRITEBYTECODE": "1"})
    child.update({k: unmask(v, root) for k, v in env.items()})
    r = subprocess.run([sys.executable, str(HERE.parents[1] / "evalrun.py"), "plan", *[unmask(a, root) for a in argv]],
                       capture_output=True, text=True, encoding="utf-8", env=child, cwd=str(root), timeout=120)
    run = root / "run"
    out = {"name": name, "argv": argv, "env": env, "files": recorded, "exit": r.returncode,
           "stdout": re.sub(r"(?m)^plan [0-9a-f]{12}:", "plan <id>:", mask(r.stdout, root)), "stderr": mask(r.stderr, root),
           "written": sorted(p.relative_to(run).as_posix() for p in run.rglob("*") if p.is_file() and "runlog" not in p.parts) if run.is_dir() else []}
    if "Traceback (most recent call last)" in r.stderr:
        raise SystemExit(f"plan case {name!r} crashed evalrun.py:\n{r.stderr}")
    for marker in ("is not JSON (", "could not be read as JSON (", "No such file or directory: "):
        if marker in out["stderr"]:
            # what follows is the Python runtime's own wording, which another controller cannot print
            out["stderrHead"] = out["stderr"] = out["stderr"][:out["stderr"].index(marker) + len(marker)]
    if (run / "plan.json").is_file():
        # the plan's text is long and mostly the registry, so it is kept gzipped (the same bytes on every run)
        out["planGz"] = b64(gzip.compress(norm_plan(json.loads((run / "plan.json").read_text(encoding="utf-8")), root).encode("utf-8"), mtime=0))
        ledger = json.loads((run / "ledger.json").read_text(encoding="utf-8"))
        ledger["planId"] = "<id>"
        out["ledger"] = json.dumps(ledger, indent=1, ensure_ascii=False)
        out["population"] = None
        if (run / "population.json").is_file():
            out["population"] = json.dumps(mask(json.loads((run / "population.json").read_text(encoding="utf-8")), root), indent=1, sort_keys=True) + "\n"
        table = run / "inputs" / "primitive-solidity.json"
        out["table"] = hashlib.sha256(table.read_bytes()).hexdigest() if table.is_file() else None
    return out


def plan_command_cases(tmp: Path) -> dict:
    world = tmp / "world"
    tree = plan_world(world)
    templates = {rel: Template(text) for rel, text in tree["templates"].items()}
    cases = []

    def add(rows, into):
        for name, argv, files, env in rows:
            into.append(run_plan_case(world, templates, tmp / f"c{len(cases):03d}-{len(into):03d}", name, argv, files, env))
    add(plan_case_table(), cases)
    # a population handed over from another plan: the file is the second case's own
    given = json.loads(next(c for c in cases if c["name"].startswith("an instance, a profile and a game"))["population"])
    base = ["--registry", f"{BACK}\\reg\\check-registry.json", "--scope", f"{BACK}\\scope.json", "--candidates", f"{BACK}\\cands\\two", "--out", f"{BACK}\\run",
            "--harness", f"{BACK}\\harness"]
    here = base + ["--instance", f"{BACK}\\instance", "--profile", "P", "--game", f"{BACK}\\game", "--probe-cmd", f"overlapprobe={BACK}\\fake.cmd",
                   "--population-from", f"{BACK}\\given.json"]

    def changed(fn) -> Template:
        doc = copy.deepcopy(given)
        fn(doc)
        return Template(json.dumps(doc, indent=1, sort_keys=True) + "\n")

    def more(d):
        d.update(targets=["Other.esp"], plugins=d["plugins"] + [{"name": "More.esp", "path": "Q:\\More.esp", "sha256": "ab" * 32}], mods=d["mods"] + [{"name": "More", "path": "Q:\\More"}])
    add([
        ("a population from another plan is used byte for byte", here, {"given.json": changed(more)}, {}),
        ("a population file that is not there", here, {}, {}),
        ("a population handed to a plan that cannot state its own", base + ["--population-from", f"{BACK}\\given.json"], {"given.json": changed(lambda d: None)}, {}),
        ("a population file that is not JSON", here, {"given.json": b"{ nope"}, {}),
        ("a population file of another schema", here, {"given.json": changed(lambda d: d.update(schema=2))}, {}),
        ("a population file whose schema is 1.0 and whose folder is spelled in capitals", here,
         {"given.json": changed(lambda d: d.update(schema=1.0, mods=[{"name": "SELMOD"}]))}, {}),
        ("a population file that lacks a plugin", here, {"given.json": changed(lambda d: d.update(plugins=[]))}, {}),
        ("a population file with another copy", here, {"given.json": changed(lambda d: d["plugins"][0].update(sha256="ab" * 32))}, {}),
        ("a population file that lacks a mod folder", here, {"given.json": changed(lambda d: d.update(mods=[]))}, {}),
    ], cases)
    owner = []
    add(owner_plan_cases(), owner)
    return {"tree": {"files": tree["files"], "dirs": tree["dirs"]}, "templates": tree["templates"], "cases": cases, "owner": owner}


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
        plan = {"python": platform.python_version(), "placeholders": {"backslash": BACK, "forward": FWD},
                "units": plan_unit_cases(tmp), "command": plan_command_cases(tmp)}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    OUT_PLAN.write_text(json.dumps(plan, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT_PLAN} ({OUT_PLAN.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
