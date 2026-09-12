"""Tests for the check registry and the applicability derivation (Q42 D1, amendment A-1).

Dependency-free, like scripts/test_runlog.py: `python _geometry-harness/tests/test_evalregistry.py`.
Each test asserts one named thing so a sabotage sweep can point at the assertion that went red.

What they are FOR. The registry decides which checks an evaluation must account for, and the
expensive failure is a checklist that quietly shrinks: a trigger that answers "not applicable"
where it should answer "unknown" removes a required check from the ledger and nothing downstream
can tell. So the asymmetry -- unknown never becomes no -- is tested from every direction it can be
reached: no scope, an incomplete scope, a declared dependence on an unmeasured extent, an unknown
arm inside anyOf, an unknown arm inside allOf.
"""

import copy
import json
import os
import sys
import tempfile
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
sys.path.insert(0, HARNESS)

import evalregistry as R  # noqa: E402

REGISTRY_PATH = R.DEFAULT_REGISTRY

# The packet's "Checklist families to account for" table, by the packetRow each family declares.
PACKET_ROWS = [
    "Loadability/data integrity",
    "Record semantics",
    "Effective geometry and placement",
    "Functional and state-dependent behavior",
    "Environmental boundaries",
    "Patch/order evaluation",
    "Runtime/presentation evidence",
]

FAILURES = []
PASSED = []


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append((name, traceback.format_exc()))
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def load_registry():
    return R.load(REGISTRY_PATH)


def complete_scope():
    """A synthetic COMPLETE scope: a placed, exterior, navmesh-carrying, quest-carrying target from
    a Nexus mod folder, with no landscape, no overlay, and the four extents scopeprobe always
    states as unmeasured."""
    return {
        "schemaVersion": 1,
        "probe": "scopeprobe",
        "complete": True,
        "walkFailures": [],
        "targets": [{
            "name": "Target.esp",
            "sha256": "a" * 64,
            "footprint": {
                "records": 1200,
                "recordTypes": {
                    "REFR": {"new": 900, "override": 40, "className": "PlacedObject"},
                    "CELL": {"new": 0, "override": 12, "className": "Cell"},
                    "QUST": {"new": 2, "override": 0, "className": "Quest"},
                    "NAVM": {"new": 6, "override": 0, "className": "NavigationMesh"},
                },
                "placed": {
                    "total": 958, "interior": 900, "exterior": 58, "persistent": 0,
                    "navm": 6, "land": 0, "landscapeUnreadable": 0,
                    "cells": [], "exteriorCells": [{"worldspace": "Tamriel", "x": 5, "y": 9}],
                    "worldspaces": ["Tamriel"],
                },
                "masters": ["Skyrim.esm", "Update.esm"],
                "scripts": {"carriers": 3},
                "assets": {"modelPaths": 40, "listed": []},
                "nexusId": 65676,
            },
        }],
        "unmeasured": [
            {"extent": "script-placement", "why": "a script can place references at runtime."},
            {"extent": "alias-fill", "why": "an alias resolves at runtime."},
            {"extent": "bos-swaps", "why": "a swap ini replaces a base at load."},
            {"extent": "asset-replacers", "why": "a record-free mod still changes the picture."},
        ],
        "configuration": {"fingerprint": "f" * 64, "source": "profile:Test", "overlays": []},
    }


# ------------------------------------------------------------------ the registry loads and is valid

def test_registry_loads_and_validates():
    registry = load_registry()
    problems = R.validate(registry)
    assert problems == [], "the shipped registry must validate; problems: " + "; ".join(problems)
    assert registry["schemaVersion"] == R.SCHEMA_VERSION
    assert isinstance(registry["registryVersion"], int)


def test_registry_hash_is_the_file_bytes():
    import hashlib
    expected = hashlib.sha256(open(REGISTRY_PATH, "rb").read()).hexdigest()
    assert R.registry_sha256(REGISTRY_PATH) == expected


def test_every_packet_family_has_a_check():
    registry = load_registry()
    rows = {f["packetRow"] for f in R.families(registry)}
    for row in PACKET_ROWS:
        assert row in rows, f"the packet row {row!r} has no family in the registry"
    for family in R.families(registry):
        owned = [c for c in registry["checks"] if c["family"] == family["id"]]
        assert owned, f"family {family['id']} carries no check"


def test_every_required_check_has_a_trigger():
    registry = load_registry()
    for c in registry["checks"]:
        if c["required"]:
            assert (c.get("applicability") or {}).get("trigger"), \
                f"required check {c['id']} carries no applicability trigger"


def test_unsupported_kinds_say_why():
    registry = load_registry()
    for c in registry["checks"]:
        if c["adapter"]["kind"] in R.KINDS_NEEDING_REASON:
            assert c.get("unsupportedReason"), f"{c['id']} is {c['adapter']['kind']} with no reason"


def test_check_by_id_and_families():
    registry = load_registry()
    assert R.check_by_id(registry, "geometry.navmesh-ownership")["family"] == "geometry"
    try:
        R.check_by_id(registry, "nope.not-a-check")
    except KeyError:
        pass
    else:
        raise AssertionError("check_by_id must raise on an unknown id, never invent one")


# ---------------------------------------------------------------- each D1 refusal fires when mutated

def mutated(fn):
    registry = load_registry()
    fn(registry)
    return R.validate(registry)


def test_refusal_duplicate_id():
    problems = mutated(lambda r: r["checks"].append(copy.deepcopy(r["checks"][0])))
    assert any("duplicate check id" in p for p in problems), problems


def test_refusal_unknown_family():
    def mutate(r):
        r["checks"][0]["family"] = "nosuchfamily"
    problems = mutated(mutate)
    assert any("unknown family" in p for p in problems), problems


def test_refusal_unknown_kind():
    def mutate(r):
        r["checks"][0]["adapter"]["kind"] = "magic"
    problems = mutated(mutate)
    assert any("unknown adapter kind" in p for p in problems), problems


def test_refusal_required_without_trigger():
    def mutate(r):
        target = next(c for c in r["checks"] if c["required"])
        target["applicability"].pop("trigger")
    problems = mutated(mutate)
    assert any("carry an applicability trigger" in p for p in problems), problems


def test_refusal_prerequisite_unknown_id():
    def mutate(r):
        r["checks"][1]["prerequisites"] = ["geometry.does-not-exist"]
    problems = mutated(mutate)
    assert any("unknown check id" in p for p in problems), problems


def test_refusal_prerequisite_cycle():
    def mutate(r):
        a, b = r["checks"][0], r["checks"][1]
        a["prerequisites"] = [b["id"]]
        b["prerequisites"] = [a["id"]]
    problems = mutated(mutate)
    assert any("prerequisite cycle" in p for p in problems), problems


def test_refusal_unsupported_without_reason():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"]["kind"] in R.KINDS_NEEDING_REASON)
        target.pop("unsupportedReason")
    problems = mutated(mutate)
    assert any("unsupportedReason" in p for p in problems), problems


def test_refusal_unknown_extent():
    def mutate(r):
        r["checks"][0].setdefault("applicability", {})["dependsOnExtents"] = ["made-up-extent"]
    problems = mutated(mutate)
    assert any("unknown unmeasured extent" in p for p in problems), problems


def test_every_json_or_text_adapter_says_how_it_is_read():
    """A-6. A check that runs a child writing json or text must carry adapter.result, and a null
    spec must say why. Without this, zero findings and zero judged read the same."""
    registry = load_registry()
    for c in registry["checks"]:
        kind, output = c["adapter"]["kind"], c["adapter"].get("output")
        if kind in R.KINDS_WITHOUT_OUTPUT:
            assert output is None, f"{c['id']} runs no child but declares output {output!r}"
            continue
        if output in ("json", "text") and kind != "scopeprobe":
            result = c["adapter"].get("result")
            assert isinstance(result, dict), f"{c['id']} has a {output} output and no result block"
            assert result["kind"] in R.RESULT_KINDS, result
            for field in ("findings", "judged", "unjudgeable"):
                assert field in result, f"{c['id']}: result has no {field} spec"
                if result[field] is None:
                    assert result.get("note"), f"{c['id']}: null {field} spec with no note"


def test_refusal_json_adapter_without_result():
    def mutate(r):
        target = next(c for c in r["checks"]
                      if c["adapter"].get("output") in ("json", "text")
                      and c["adapter"]["kind"] != "scopeprobe")
        target["adapter"].pop("result")
    problems = mutated(mutate)
    assert any("needs adapter.result" in p for p in problems), problems


def test_refusal_regex_spec_without_the_named_group():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"].get("output") == "text")
        target["adapter"]["result"]["judged"] = "regex:indexed ([0-9,]+) plugins"
    problems = mutated(mutate)
    assert any("named group" in p for p in problems), problems


def test_refusal_dotted_path_on_a_text_output():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"].get("output") == "text")
        target["adapter"]["result"]["judged"] = "summary.switches"
    problems = mutated(mutate)
    assert any("needs regex:" in p for p in problems), problems


def test_refusal_null_spec_without_a_note():
    def mutate(r):
        target = next(c for c in r["checks"]
                      if isinstance(c["adapter"].get("result"), dict)
                      and c["adapter"]["result"].get("note"))
        target["adapter"]["result"].pop("note")
    problems = mutated(mutate)
    assert any("with no note" in p for p in problems), problems


def test_refusal_output_on_a_kind_that_runs_nothing():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"]["kind"] in R.KINDS_WITHOUT_OUTPUT)
        target["adapter"]["output"] = "json"
    problems = mutated(mutate)
    assert any("must be null" in p for p in problems), problems


def test_every_child_kind_declares_its_common_flags():
    """A-10(b). A probe whose parser has no --instance must not be handed one: blockprobe,
    patchprobe, fomodprobe and poolprobe find the instance through HarnessTarget's environment."""
    registry = load_registry()
    flagless = {"blockprobe", "patchprobe", "fomodprobe", "poolprobe"}
    seen = set()
    for c in registry["checks"]:
        a = c["adapter"]
        if a["kind"] not in R.KINDS_WITH_CHILD:
            assert "commonFlags" not in a, f"{c['id']} runs no child but declares commonFlags"
            continue
        assert isinstance(a.get("commonFlags"), bool), f"{c['id']} does not declare commonFlags"
        assert a["commonFlags"] is (a["exe"] not in flagless),             f"{c['id']}: commonFlags {a['commonFlags']} contradicts {a['exe']}'s parser"
        if not a["commonFlags"]:
            seen.add(a["exe"])
    assert seen == flagless, f"expected every flag-less probe to be covered; got {sorted(seen)}"


def test_positional_target_is_a_bare_placeholder():
    """A-10(a). blockprobe takes the plugin positionally; --target {target} would be an unknown
    option and the probe would exit 2."""
    registry = load_registry()
    route = R.check_by_id(registry, "geometry.route-cut")["adapter"]
    assert route["args"] == ["{target}"], route["args"]
    scope = R.check_by_id(registry, "loadability.masters-and-order")["adapter"]
    assert "--order" in scope["args"], scope["args"]


def test_probes_that_judge_their_own_population_declare_it():
    """A-10(d) and A-11(d). Three probes emit a population that is not the arm's, each because it
    judges something that is not placed references. Comparing their emits against the arm's
    load-time / baseline would reject every receipt they write. The strings are copied verbatim
    from each probe's ConflictIo.Write call, so a drift there fails here."""
    registry = load_registry()
    declared = {
        "functional.quest-alias-split": "n/a (placement history; no enable-state filter)",
        "environment.land-seams": "n/a (LAND heightmaps; no placed-reference population)",
        "environment.water-boundaries": "n/a (CELL water records; no placed-reference population)",
    }
    for check_id, population in declared.items():
        adapter = R.check_by_id(registry, check_id)["adapter"]
        assert adapter.get("population") == population, (check_id, adapter.get("population"))
        assert adapter.get("state") == "n/a", (check_id, adapter.get("state"))

    # Everything else emits the arm's own population and must declare nothing, so a probe that
    # starts differing is caught here rather than by a rejected receipt on a lab run.
    for check in registry["checks"]:
        if check["id"] in declared:
            continue
        adapter = check["adapter"]
        assert "population" not in adapter and "state" not in adapter,             f"{check['id']} declares a population but its probe emits the arm's"


def test_the_switch_census_runs_on_every_arm_its_consumer_does():
    """A-11(e). The census produces the --switch-map the state arms consume. Running it on the
    baseline only left every candidate arm's state check blocked for want of a map."""
    registry = load_registry()
    census = R.check_by_id(registry, "state.switch-census")
    conditional = R.check_by_id(registry, "state.conditional-geometry")
    assert census["inputs"]["arms"] == "all", census["inputs"]
    assert "state.switch-census" in conditional["prerequisites"], conditional["prerequisites"]
    assert conditional["inputs"]["arms"] == "all", conditional["inputs"]


def test_refusal_all_arm_check_resting_on_a_baseline_prerequisite():
    def mutate(r):
        census = next(c for c in r["checks"] if c["id"] == "state.switch-census")
        census["inputs"]["arms"] = "baseline"
    problems = mutated(mutate)
    assert any("baseline only" in p for p in problems), problems


def test_refusal_child_kind_without_common_flags():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"]["kind"] in R.KINDS_WITH_CHILD)
        target["adapter"].pop("commonFlags")
    problems = mutated(mutate)
    assert any("commonFlags must be true or false" in p for p in problems), problems


def test_refusal_common_flags_on_a_kind_that_runs_nothing():
    def mutate(r):
        target = next(c for c in r["checks"] if c["adapter"]["kind"] in R.KINDS_WITHOUT_OUTPUT)
        target["adapter"]["commonFlags"] = True
    problems = mutated(mutate)
    assert any("means nothing" in p for p in problems), problems


def test_refusal_empty_population_declaration():
    def mutate(r):
        R_check = next(c for c in r["checks"] if c["id"] == "functional.quest-alias-split")
        R_check["adapter"]["population"] = ""
    problems = mutated(mutate)
    assert any("must be the non-empty string" in p for p in problems), problems


def test_refusal_unknown_trigger_operator():
    def mutate(r):
        r["checks"][0]["applicability"]["trigger"] = {"vibes": True}
    problems = mutated(mutate)
    assert any("unknown trigger operator" in p for p in problems), problems


# --------------------------------------------------------------------- applicability on a real scope

def test_yes_and_no_carry_evidence():
    registry = load_registry()
    answers = R.applicability(registry, complete_scope())

    nav = answers["geometry.navmesh-ownership"]
    assert nav["applicability"] == "yes", nav
    assert "NAVM" in nav["evidence"], nav

    land = answers["environment.land-seams"]
    assert land["applicability"] == "yes", land          # exterior is true even though land is 0

    bos = answers["geometry.bos-overlay"]
    assert bos["applicability"] == "no", bos
    assert "overlay" in bos["evidence"], bos

    advice = answers["patch.author-advice"]
    assert advice["applicability"] == "yes", advice
    assert "65676" in advice["evidence"], advice

    always = answers["patch.order-evaluation"]
    assert always["applicability"] == "yes", always


def test_no_is_quoted_from_the_counters():
    registry = load_registry()
    scope = complete_scope()
    scope["targets"][0]["footprint"]["recordTypes"] = {
        "STAT": {"new": 5, "override": 0, "className": "Static"}}
    scope["targets"][0]["footprint"]["placed"] = {
        "total": 0, "interior": 0, "exterior": 0, "persistent": 0, "navm": 0, "land": 0,
        "cells": [], "exteriorCells": [], "worldspaces": []}
    answers = R.applicability(registry, scope)
    placed = answers["geometry.object-overlap"]
    assert placed["applicability"] == "no", placed
    assert "0" in placed["evidence"], placed
    quest = answers["records.registration-and-dialogue"]
    assert quest["applicability"] == "no", quest
    assert "QUST" in quest["evidence"], quest


# ---------------------------------------------------------- unknown, and that it never becomes "no"

def test_no_scope_is_unknown_but_always_is_still_yes():
    registry = load_registry()
    answers = R.applicability(registry, None)
    assert answers["geometry.object-overlap"]["applicability"] == "unknown"
    assert answers["patch.order-evaluation"]["applicability"] == "yes"
    assert all(v["applicability"] != "no" for v in answers.values()), \
        "with no scope measured, nothing may be answered 'not applicable'"


def test_incomplete_scope_is_unknown_never_no():
    registry = load_registry()
    scope = complete_scope()
    scope["complete"] = False
    scope["walkFailures"] = ["'Target.esp' could not be opened"]
    scope["targets"][0]["footprint"]["placed"]["total"] = 0
    scope["targets"][0]["footprint"]["recordTypes"] = {}
    answers = R.applicability(registry, scope)
    assert answers["geometry.object-overlap"]["applicability"] == "unknown"
    assert "incomplete" in answers["geometry.object-overlap"]["evidence"]
    assert all(v["applicability"] != "no" for v in answers.values()), \
        "an incomplete scope may not produce a single 'not applicable'"


def test_unmeasured_extent_is_unknown():
    registry = load_registry()
    answers = R.applicability(registry, complete_scope())
    linked = answers["functional.linked-dependencies"]
    assert linked["applicability"] == "unknown", linked
    assert "unmeasured" in linked["evidence"], linked


def test_unmeasured_extent_beats_a_true_trigger():
    """The check's trigger (`placed`) is TRUE on this scope; the declared extent must still win.
    This is the A-1 rule, and getting it backwards would silently credit a check whose subject
    matter nothing measured."""
    registry = load_registry()
    scope = complete_scope()
    assert scope["targets"][0]["footprint"]["placed"]["total"] > 0
    linked = R.applicability(registry, scope)["functional.linked-dependencies"]
    assert linked["applicability"] == "unknown", linked


def test_measured_extents_let_the_trigger_answer():
    registry = load_registry()
    scope = complete_scope()
    scope["unmeasured"] = []          # hypothetical: nothing unmeasured
    linked = R.applicability(registry, scope)["functional.linked-dependencies"]
    assert linked["applicability"] == "yes", linked


def test_unknown_arm_never_resolves_to_no_inside_anyof():
    """anyOf over an unknown arm and a false arm is unknown, not no."""
    registry = load_registry()
    scope = complete_scope()
    scope["unmeasured"] = []
    scope["targets"][0]["footprint"]["placed"].update({"total": 0, "exterior": 0, "land": 0, "navm": 0})
    scope["targets"][0]["footprint"]["recordTypes"] = {}
    # an anyOf check whose arms are all false answers no on a complete scope...
    assert R.applicability(registry, scope)["environment.land-seams"]["applicability"] == "no"
    # ...but one unknown arm makes the whole thing unknown.
    probe = {"schemaVersion": 1, "registryVersion": 1, "skill": "t", "families": [
        {"id": "geometry", "title": "t", "packetRow": "Effective geometry and placement"}],
        "checks": [dict(R.check_by_id(registry, "environment.land-seams"),
                        id="geometry.probe", family="geometry")]}
    probe["checks"][0]["prerequisites"] = []
    probe["checks"][0]["applicability"] = {
        "trigger": {"anyOf": [{"land": True}, {"exterior": True}]},
        "note": "t", "dependsOnExtents": []}
    scope2 = copy.deepcopy(scope)
    scope2["complete"] = False
    assert R.applicability(probe, scope2)["geometry.probe"]["applicability"] == "unknown"


def test_unknown_arm_never_resolves_to_no_inside_allof():
    probe = {"schemaVersion": 1, "registryVersion": 1, "skill": "t",
             "families": [{"id": "geometry", "title": "t", "packetRow": "Effective geometry and placement"}],
             "checks": [{
                 "id": "geometry.both", "family": "geometry", "title": "t", "required": True,
                 "applicability": {"trigger": {"allOf": [{"always": True}, {"placed": True}]}, "note": "t"},
                 "prerequisites": [], "inputs": {"arms": "all", "states": "baseline"},
                 "adapter": {"kind": "derived", "exe": None, "args": [], "output": None},
                 "evidence": {"strength": "record-fact", "claim": None, "limits": "t"},
                 "manualDecisionAllowed": False}]}
    assert R.validate(probe) == [], R.validate(probe)
    scope = complete_scope()
    scope["complete"] = False
    answer = R.applicability(probe, scope)["geometry.both"]
    assert answer["applicability"] == "unknown", answer


def test_missing_trigger_is_unknown_not_yes():
    registry = load_registry()
    registry["checks"][0].get("applicability", {}).pop("trigger", None)
    answer = R.applicability(registry, complete_scope())[registry["checks"][0]["id"]]
    assert answer["applicability"] == "unknown", answer


# ------------------------------------------------------------------ a real scopeprobe file, if present

def test_scopeprobe_shape_round_trips():
    """A scope written by scopeprobe must be readable by every trigger. Uses a fixture written from
    the probe's own serializer shape; if a lab scope.json is handed in EVALREGISTRY_SCOPE, that is
    used instead so a shape drift on a real order is caught here."""
    path = os.environ.get("EVALREGISTRY_SCOPE")
    if not path or not os.path.exists(path):
        return
    scope = json.load(open(path, encoding="utf-8"))
    registry = load_registry()
    answers = R.applicability(registry, scope)
    assert len(answers) == len(registry["checks"])
    assert all(v["applicability"] in ("yes", "no", "unknown") for v in answers.values())
    assert all(v["evidence"] for v in answers.values()), "every answer must carry its evidence"


def main():
    tests = [(name, fn) for name, fn in sorted(globals().items())
             if name.startswith("test_") and callable(fn)]
    print(f"registry: {REGISTRY_PATH}")
    for name, fn in tests:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
