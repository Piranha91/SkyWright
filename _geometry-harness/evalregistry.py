"""The check registry: load it, validate it, and derive what applies to a measured scope.

Q42 (roadmap 4.23), D1 of `_claude/runs/2026-09-12-evaluation-controller/PLAN.md`.

WHAT THIS IS FOR. The evaluation controller must not decide which checks a location mod needs;
that decision is domain knowledge and lives as skill DATA, in
`.claude/skills/world-edit-compatibility/references/check-registry.json`. This module is the only
code that reads that file: it loads it, refuses a malformed one by name, and answers one question
per check — does this check apply to THIS target, given what scopeprobe measured?

WHAT IT NEVER DOES. It never enumerates record types for coverage. A trigger names record-type
families because a check is ABOUT that kind of content (domain knowledge); what a target actually
contains comes from scopeprobe, by construction, out of Mutagen's own record enumeration. It runs
nothing, opens no plugin, reaches no network. Pure functions, standard library only, so the oracle
and the tests can re-derive the same answers without the controller.

THE THREE ANSWERS, and why there are three. Applicability is `yes`, `no` (with the scope counters
that made it false, quoted, as evidence) or `unknown`. Unknown is returned whenever the scope is
missing, whenever `complete` is false, and whenever the check declares a dependence on an extent
the scope itself lists under `unmeasured`. **Unknown never becomes no.** A check that cannot be shown inapplicable
is not a check that does not apply — it is a check whose applicability was not measured, and the
finalizer keeps the evaluation incomplete on it (PLAN D7). That asymmetry is the whole point of
the module: the cheap failure mode here is silently narrowing a checklist.

PUBLIC API
    load(path)                  -> dict          the registry JSON
    registry_sha256(path)       -> str           hash of the file's bytes, as the plan records it
    validate(registry)          -> list[str]     problems; an empty list means valid
    applicability(registry, scope) -> dict       checkId -> {"applicability", "evidence"}
    check_by_id(registry, id)   -> dict          the check, KeyError if absent
    families(registry)          -> list[dict]    the family rows in registry order

A NOTE ON `scope`. The scope argument is a parsed `scope.json` as scopeprobe writes it (PLAN D2),
or None for "no scope measured yet" — which makes every trigger unknown, including `{"always": true}`
is NOT excepted: `always` is true without reading the scope at all, so it stays `yes` even with no
scope. Everything that reads a counter needs the measurement.
"""

from __future__ import annotations

import hashlib
import json
import os
import re

SCHEMA_VERSION = 1

#: Adapter kinds, closed (PLAN D1). A kind outside this set is a validation failure, not a warning:
#: the controller dispatches on it, and an unknown kind would be silently skipped work.
ADAPTER_KINDS = frozenset({
    "scopeprobe",        # the footprint probe itself
    "ordersweep-probe",  # an emitting probe with --order, --emit AND --base: run through ordersweep.py
    "probe-emit",        # --order and --emit but no --base: per arm, no reverse arm, no physical diff
    "probe-json",        # --json, per arm or baseline
    "probe-target",      # the target plugin positionally and no --order: baseline profile only
    "adviceprobe",       # the Nexus author-advice reader (its own flag shape and refresh policy)
    "patchprobe",        # the local patch/installer discovery probes (patchprobe, fomodprobe, poolprobe)
    "derived",           # computed by evalrun from other checks' receipts; runs no child
    "media",             # supplied observations, admitted only with an identity (PLAN D8)
    "manual",            # a human decision, admissible only where manualDecisionAllowed is true
    "mcp-only",          # the capability exists, but only behind an MCP tool the controller cannot call
    "live-import",       # a live game driver's receipt, imported and re-read by evallive.py; opt-in only
    "unsupported",       # no adapter and no MCP tool: an open limit, named
})

#: Kinds that cannot produce a receipt by running something, and therefore must say why in
#: `unsupportedReason` (PLAN D1's last validation rule).
KINDS_NEEDING_REASON = frozenset({"manual", "mcp-only", "unsupported"})

#: Detector readings a result block can declare (PLAN amendment A-6). `detector` reads finding /
#: no-finding from the findings count; `catalogue` and `discovery` read `catalogue` — a completed
#: measurement whose rows are evidence, not defects.
RESULT_KINDS = frozenset({"detector", "catalogue", "discovery"})

#: Adapter kinds that run no child, so there is no child output to read a result out of. Their
#: `adapter.output` is null and A-6's rule does not reach them.
KINDS_WITHOUT_OUTPUT = frozenset({"derived", "media", "mcp-only", "manual", "unsupported", "live-import"})

#: Kinds a check may only carry with `optIn: true`: the controller never launches the game, so a live check
#: is something the caller asks for (`plan --opt-in`), never a default the evaluation silently waits on.
KINDS_REQUIRING_OPT_IN = frozenset({"live-import"})

#: Adapter kinds that DO run a child process. These must say whether the child takes the common
#: target flags (PLAN amendment A-10(b)): a probe whose parser has no --instance finds the instance
#: through HarnessTarget's environment instead, and handing it the flags makes it exit 2.
KINDS_WITH_CHILD = frozenset({
    "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target",
    "adviceprobe", "patchprobe",
})

#: Evidence strengths, closed (PLAN D1/D4). Strength is NEVER folded into execution status.
EVIDENCE_STRENGTHS = frozenset({
    "record-fact", "calibrated-inference", "engine-observation", "supplied-media",
})

#: Trigger operators, closed (PLAN D1). Domain knowledge lives in the OPERANDS (which record-type
#: families a check is about); the vocabulary itself is fixed here so a registry cannot smuggle in
#: a new rule the controller has never seen.
TRIGGER_KEYS = frozenset({
    "always", "anyType", "placed", "exterior", "land", "navmesh", "overlays", "nexusId",
    "masters", "anyOf", "allOf",
})

#: Unmeasured extents, closed (PLAN amendment A-1). scope.json's `unmeasured` is a list of
#: `{"extent": <one of these>, "why": "<one sentence>"}`; a check that declares `dependsOnExtents`
#: naming one that appears there answers `unknown`, whatever its trigger says.
EXTENTS = frozenset({
    "script-placement",   # a script places, moves or deletes references at runtime
    "alias-fill",         # a quest alias resolves to a reference chosen at runtime
    "bos-swaps",          # a *_SWAP.ini replaces a reference's base object at load
    "asset-replacers",    # a mod with no records at all still changes what the game draws
    "door-destinations",  # a door names a destination the winning order files nowhere
    "generated-output",   # LOD / occlusion / patcher output built from an earlier placement
})

#: The ONE scope field each trigger key reads (PLAN amendment A-1), and nothing else. `always`
#: reads nothing. Fixing this mapping here is what stops a trigger from quietly acquiring a second
#: source of truth about what a target contains.
_TRIGGER_FIELD = {
    "anyType": "recordTypes",
    "placed": "placed.total",
    "exterior": "placed.exterior",
    "land": "placed.land",
    "navmesh": "placed.navm",
    "overlays": "configuration.overlays",
    "nexusId": "nexusId",
    "masters": "masters",
}


# --------------------------------------------------------------------------------------- loading

def load(path):
    """Read the registry JSON at `path`. Raises on unreadable or non-object content — a registry
    that cannot be read is a refusal, not an empty checklist."""
    with open(path, "r", encoding="utf-8") as handle:
        registry = json.load(handle)
    if not isinstance(registry, dict):
        raise ValueError(f"{path}: the registry must be a JSON object, got {type(registry).__name__}")
    return registry


def registry_sha256(path):
    """The sha256 of the registry FILE's bytes — what the plan records and what finalize compares
    against to refuse `registry-changed` (PLAN D3/D7). Hashing the file, not a re-serialization,
    so a whitespace-only edit is still a changed registry."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 16), b""):
            digest.update(block)
    return digest.hexdigest()


def check_by_id(registry, check_id):
    """The check with this id. KeyError when absent — callers must not invent one."""
    for check in registry.get("checks") or []:
        if check.get("id") == check_id:
            return check
    raise KeyError(check_id)


def families(registry):
    """The family rows, in registry order."""
    return list(registry.get("families") or [])


# ------------------------------------------------------------------------------------ validation

def validate(registry):
    """Every problem with this registry, as sentences. An empty list means valid.

    The refusals PLAN D1 names: a duplicate check id, an unknown family, an unknown adapter kind,
    a required check with no trigger, a prerequisite naming an unknown id, a prerequisite cycle,
    and a manual / mcp-only / unsupported check with no `unsupportedReason`. Everything else the
    schema needs to be usable is checked too (a trigger outside the closed vocabulary, an unknown
    evidence strength, a family with no checks), because a controller that dispatches on a field
    cannot treat that field as optional."""
    problems = []

    if registry.get("schemaVersion") != SCHEMA_VERSION:
        problems.append(
            f"schemaVersion must be {SCHEMA_VERSION}; got {registry.get('schemaVersion')!r}")
    if not isinstance(registry.get("registryVersion"), int):
        problems.append("registryVersion must be an integer (the plan records it and finalize compares it)")

    family_rows = registry.get("families")
    if not isinstance(family_rows, list) or not family_rows:
        problems.append("families must be a non-empty list")
        family_rows = []
    family_ids = []
    for index, family in enumerate(family_rows):
        if not isinstance(family, dict):
            problems.append(f"families[{index}] must be an object")
            continue
        fid = family.get("id")
        if not isinstance(fid, str) or not fid:
            problems.append(f"families[{index}] has no id")
            continue
        if fid in family_ids:
            problems.append(f"duplicate family id: {fid}")
        family_ids.append(fid)
        if not family.get("packetRow"):
            problems.append(f"family {fid} does not name the packet row it accounts for")

    checks = registry.get("checks")
    if not isinstance(checks, list) or not checks:
        problems.append("checks must be a non-empty list")
        checks = []

    seen = set()
    ids = set()
    for index, check in enumerate(checks):
        if not isinstance(check, dict):
            problems.append(f"checks[{index}] must be an object")
            continue
        cid = check.get("id")
        if not isinstance(cid, str) or not cid:
            problems.append(f"checks[{index}] has no id")
            continue
        if cid in seen:
            problems.append(f"duplicate check id: {cid}")
        seen.add(cid)
        ids.add(cid)

    for check in checks:
        if not isinstance(check, dict):
            continue
        cid = check.get("id") or "<unnamed>"
        family = check.get("family")
        if family not in family_ids:
            problems.append(f"{cid}: unknown family {family!r}")
        if cid.split(".", 1)[0] != family:
            problems.append(f"{cid}: the id must be prefixed with its family ({family!r})")
        if not check.get("title"):
            problems.append(f"{cid}: no title")
        required = check.get("required")
        if not isinstance(required, bool):
            problems.append(f"{cid}: required must be true or false")

        adapter = check.get("adapter")
        if not isinstance(adapter, dict):
            problems.append(f"{cid}: no adapter")
            adapter = {}
        kind = adapter.get("kind")
        if kind not in ADAPTER_KINDS:
            problems.append(f"{cid}: unknown adapter kind {kind!r}")
        if kind in KINDS_NEEDING_REASON and not check.get("unsupportedReason"):
            problems.append(f"{cid}: kind {kind} needs an unsupportedReason saying what is missing")
        if kind in ("ordersweep-probe", "probe-emit", "probe-json", "probe-target",
                    "scopeprobe", "adviceprobe", "patchprobe") and not adapter.get("exe"):
            problems.append(f"{cid}: adapter kind {kind} names no exe")
        if adapter.get("args") is not None and not isinstance(adapter.get("args"), list):
            problems.append(f"{cid}: adapter args must be a list of argv tokens")

        # A-6: a json or text output must say how it is READ. Without this a check can exit 0 with a
        # file nobody can count, and "no findings" and "nothing was judged" become the same answer.
        # A-10(b): whether the child takes --instance/--profile/--game. Not a default: a wrong guess
        # either refuses the run (flags the parser rejects) or points a probe at another instance.
        if kind in KINDS_WITH_CHILD and not isinstance(adapter.get("commonFlags"), bool):
            problems.append(f"{cid}: adapter.commonFlags must be true or false — a child either takes "
                            "--instance/--profile/--game or finds them through the environment")
        if kind not in KINDS_WITH_CHILD and "commonFlags" in adapter:
            problems.append(f"{cid}: kind {kind} runs no child, so adapter.commonFlags means nothing")

        # A-10(d): a probe that judges a population other than the arm's declares it, so the receipt
        # validator compares the emit against the probe's own words and not the plan's.
        for field in ("population", "state"):
            if field in adapter and not (isinstance(adapter[field], str) and adapter[field]):
                problems.append(f"{cid}: adapter.{field}, when present, must be the non-empty string "
                                "the probe's emit actually carries")

        # P25: a probe that can be scoped to the plan's population names the flag that takes the population file.
        # ordersweep passes it to a sweep probe and evalrun to a probe-emit check (window 2); no other kind runs a probe
        # that could take it, so the key means nothing there.
        if "primitiveSolidityFlag" in adapter:
            if kind != "probe-emit" or adapter.get("exe") != "stateprobe":
                problems.append(f"{cid}: adapter.primitiveSolidityFlag is passed only to stateprobe's probe-emit check, not by kind {kind}")
            elif adapter["primitiveSolidityFlag"] != "--primitive-solidity":
                problems.append(f"{cid}: adapter.primitiveSolidityFlag must be \"--primitive-solidity\", the flag stateprobe takes")
        if "populationFlag" in adapter:
            if kind not in ("ordersweep-probe", "probe-emit"):
                problems.append(f"{cid}: adapter.populationFlag is passed only by the ordersweep and probe-emit adapters, not by kind {kind}")
            elif adapter["populationFlag"] != "--population":
                problems.append(f"{cid}: adapter.populationFlag must be \"--population\", the flag the scoped probes take")

        output = adapter.get("output")
        result = adapter.get("result")
        # adapter.files: the files a text probe writes besides its printed output (blockprobe --rows), named with the
        # {out}, {run} and {arm} placeholders; evalrun records each as an output and the bundle carries it.
        if "files" in adapter:
            files = adapter["files"]
            if output != "text" or kind != "probe-target":
                problems.append(f"{cid}: adapter.files is read only for a probe-target adapter whose output is text")
            elif not (isinstance(files, list) and files and all(isinstance(f, str) and f.strip() for f in files)):
                problems.append(f"{cid}: adapter.files must be a non-empty list of file paths")
            elif not all(any(p in f for p in ("{out}", "{run}")) and "{arm}" in f for f in files):
                problems.append(f"{cid}: each adapter.files path names the arm ({{arm}}) under the run ({{out}} or {{run}}), "
                                "so two arms never write one file")
        if kind in KINDS_WITHOUT_OUTPUT and output is not None:
            problems.append(f"{cid}: kind {kind} runs no child, so adapter.output must be null")
        elif output in ("json", "text") and kind != "scopeprobe":
            if not isinstance(result, dict):
                problems.append(f"{cid}: a {output} adapter needs adapter.result "
                                "{kind, findings, judged, unjudgeable} saying how its output is read")
            else:
                if result.get("kind") not in RESULT_KINDS:
                    problems.append(f"{cid}: unknown result kind {result.get('kind')!r}")
                for field in ("findings", "judged", "unjudgeable"):
                    if field not in result:
                        problems.append(f"{cid}: adapter.result has no {field} spec (use null and "
                                        "say in result.note that the probe prints no such count)")
                        continue
                    spec = result[field]
                    if spec is None:
                        if not result.get("note"):
                            problems.append(f"{cid}: adapter.result.{field} is null with no note "
                                            "saying why the probe prints no such count")
                    elif not isinstance(spec, str) or not spec:
                        problems.append(f"{cid}: adapter.result.{field} must be a dotted path, "
                                        "regex:<pattern>, or null")
                    elif spec.startswith("regex:"):
                        if output != "text":
                            problems.append(f"{cid}: adapter.result.{field} is a regex but the "
                                            f"output is {output}, not text")
                        elif "(?P<n>" not in spec:
                            problems.append(f"{cid}: adapter.result.{field} regex has no named "
                                            "group `n` to read the count out of")
                        else:
                            try:
                                re.compile(spec[len("regex:"):])
                            except re.error as err:
                                problems.append(f"{cid}: adapter.result.{field} regex does not "
                                                f"compile: {err}")
                    elif output != "json":
                        problems.append(f"{cid}: adapter.result.{field} is a dotted path but the "
                                        f"output is {output}; a text output needs regex:<pattern>")

        applic = check.get("applicability")
        trigger = (applic or {}).get("trigger") if isinstance(applic, dict) else None
        if required and not trigger:
            problems.append(f"{cid}: a required check must carry an applicability trigger")
        if trigger is not None:
            problems.extend(f"{cid}: {p}" for p in _validate_trigger(trigger))
        if isinstance(applic, dict) and trigger is not None and not applic.get("note"):
            problems.append(f"{cid}: the trigger carries no note saying why it is the right trigger")
        depends = (applic or {}).get("dependsOnExtents") if isinstance(applic, dict) else None
        if depends is not None:
            if not isinstance(depends, list):
                problems.append(f"{cid}: applicability.dependsOnExtents must be a list of extent keys")
            else:
                for extent in depends:
                    if extent not in EXTENTS:
                        problems.append(f"{cid}: unknown unmeasured extent {extent!r}")

        inputs = check.get("inputs")
        if not isinstance(inputs, dict):
            problems.append(f"{cid}: no inputs block ({{arms, states}})")
        else:
            if inputs.get("arms") not in ("all", "baseline"):
                problems.append(f"{cid}: inputs.arms must be 'all' or 'baseline'")
            if inputs.get("states") not in ("baseline", "selected", "all"):
                problems.append(f"{cid}: inputs.states must be 'baseline', 'selected' or 'all'")

        evidence = check.get("evidence")
        if not isinstance(evidence, dict):
            problems.append(f"{cid}: no evidence block")
        else:
            if evidence.get("strength") not in EVIDENCE_STRENGTHS:
                problems.append(f"{cid}: unknown evidence strength {evidence.get('strength')!r}")
            if "claim" not in evidence:
                problems.append(f"{cid}: evidence names no claim row (use null and say why in limits)")
            if evidence.get("claim") is None and not evidence.get("limits"):
                problems.append(
                    f"{cid}: no claim row and no limits — a check with no ledger row must say so")

        if not isinstance(check.get("manualDecisionAllowed"), bool):
            problems.append(f"{cid}: manualDecisionAllowed must be true or false")

        opt_in = check.get("optIn", False)
        if not isinstance(opt_in, bool):
            problems.append(f"{cid}: optIn, when present, must be true or false")
        elif opt_in and required:
            problems.append(f"{cid}: an opt-in check cannot be required by default (required must be false; "
                            "opting in makes it required for that run)")
        if kind in KINDS_REQUIRING_OPT_IN and opt_in is not True:
            problems.append(f"{cid}: adapter kind {kind} needs optIn: true (the controller never launches the game)")

        prereqs = check.get("prerequisites")
        if prereqs is None:
            problems.append(f"{cid}: prerequisites must be a list (use [] for none)")
        elif not isinstance(prereqs, list):
            problems.append(f"{cid}: prerequisites must be a list")
        else:
            for prereq in prereqs:
                if prereq not in ids:
                    problems.append(f"{cid}: prerequisite names an unknown check id: {prereq!r}")

    problems.extend(_cycles(checks, ids))

    # A-11(e): a check that runs on every arm cannot rest on a prerequisite that only runs on the
    # baseline — on a candidate arm the prerequisite has no receipt, and the dependent is blocked for
    # want of an input the registry promised it. The switch census and the state arms were exactly
    # this shape until the demonstration surfaced it.
    arms_of = {c["id"]: (c.get("inputs") or {}).get("arms")
               for c in checks if isinstance(c, dict) and isinstance(c.get("id"), str)}
    for check in checks:
        if not isinstance(check, dict) or arms_of.get(check.get("id")) != "all":
            continue
        for prereq in check.get("prerequisites") or []:
            if arms_of.get(prereq) == "baseline":
                problems.append(
                    f"{check['id']} runs on every arm but its prerequisite {prereq} runs on the "
                    "baseline only, so on a candidate arm it would be blocked for want of an input")

    with_checks = {c.get("family") for c in checks if isinstance(c, dict)}
    for fid in family_ids:
        if fid not in with_checks:
            problems.append(f"family {fid} carries no check — every family must be accounted for")

    return problems


def _validate_trigger(trigger, path="trigger"):
    """Problems with one trigger expression, recursively."""
    problems = []
    if not isinstance(trigger, dict):
        return [f"{path} must be an object"]
    if len(trigger) != 1:
        return [f"{path} must carry exactly one operator; got {sorted(trigger)}"]
    (key, value), = trigger.items()
    if key not in TRIGGER_KEYS:
        return [f"{path}: unknown trigger operator {key!r}"]
    if key in ("anyOf", "allOf"):
        if not isinstance(value, list) or not value:
            return [f"{path}.{key} must be a non-empty list of triggers"]
        for i, sub in enumerate(value):
            problems.extend(_validate_trigger(sub, f"{path}.{key}[{i}]"))
    elif key == "anyType":
        if not isinstance(value, list) or not value or not all(isinstance(t, str) for t in value):
            problems.append(f"{path}.anyType must be a non-empty list of record type names")
    else:
        if value is not True:
            problems.append(f"{path}.{key} must be true (the vocabulary has no negation)")
    return problems


def _cycles(checks, ids):
    """Prerequisite cycles, named by the ids that form them."""
    graph = {}
    for check in checks:
        if isinstance(check, dict) and isinstance(check.get("id"), str):
            graph[check["id"]] = [p for p in (check.get("prerequisites") or []) if p in ids]
    problems, state = [], {}

    def walk(node, stack):
        if state.get(node) == "done":
            return
        if state.get(node) == "open":
            cycle = stack[stack.index(node):] + [node]
            problems.append("prerequisite cycle: " + " -> ".join(cycle))
            return
        state[node] = "open"
        for nxt in graph.get(node, []):
            walk(nxt, stack + [node])
        state[node] = "done"

    for node in sorted(graph):
        walk(node, [])
    return sorted(set(problems))


# --------------------------------------------------------------------------------- applicability

def applicability(registry, scope):
    """Per check: `{"applicability": "yes" | "no" | "unknown", "evidence": "<one sentence>"}`.

    `scope` is a parsed scope.json (PLAN D2) or None. The rules, in order:

      * a check declaring `dependsOnExtents` naming an extent scope.json lists under `unmeasured`
        -> unknown, whatever its trigger says (PLAN amendment A-1);
      * no scope at all, or `complete` is false -> unknown for every trigger that reads the scope;
      * otherwise the trigger's own answer, with the counters that decided it quoted as evidence.

    `anyOf` is yes on the first yes, unknown if any arm is unknown and none is yes, else no.
    `allOf` is no on the first no, unknown if any arm is unknown and none is no, else yes. So an
    unknown arm can never be resolved into a no by its neighbours."""
    out = {}
    for check in registry.get("checks") or []:
        cid = check.get("id")
        applic = check.get("applicability") if isinstance(check.get("applicability"), dict) else {}
        trigger = applic.get("trigger")

        # A-1: a declared dependence on an unmeasured extent wins over the trigger. A check whose
        # subject matter is partly unmeasured is unknown even when its trigger reads a measured
        # counter, because the counter does not cover what the check is for.
        depends = applic.get("dependsOnExtents") or []
        hit = _unmeasured_hit(scope, depends)
        if hit is not None:
            extent, why = hit
            out[cid] = {
                "applicability": "unknown",
                "evidence": f"the scope lists {extent} under unmeasured: \"{why}\".",
            }
            continue

        if trigger is None:
            out[cid] = {
                "applicability": "unknown",
                "evidence": "the registry gives this check no trigger, so its applicability is not derived.",
            }
            continue
        verdict, evidence = _evaluate(trigger, scope)
        out[cid] = {"applicability": verdict, "evidence": evidence}
    return out


def _unmeasured_hit(scope, depends):
    """The first declared extent that the scope lists under `unmeasured`, with its sentence, or
    None. A scope that is missing entirely lists nothing — the `complete` rule in `_evaluate`
    already answers unknown there, and answering twice would hide which rule fired."""
    if not depends:
        return None
    listed = {}
    for entry in (scope or {}).get("unmeasured") or []:
        if isinstance(entry, dict) and entry.get("extent"):
            listed[entry["extent"]] = entry.get("why") or ""
    for extent in depends:
        if extent in listed:
            return extent, listed[extent]
    return None


def _evaluate(trigger, scope):
    (key, value), = trigger.items()

    if key == "always":
        return "yes", "this check applies to every evaluation."
    if key == "anyOf":
        return _combine(value, scope, any_mode=True)
    if key == "allOf":
        return _combine(value, scope, any_mode=False)

    if scope is None:
        return "unknown", "no scope.json was measured, so this trigger could not be read."
    if scope.get("complete") is not True:
        failures = scope.get("walkFailures") or []
        detail = f" ({len(failures)} target(s) not walked)" if failures else ""
        return "unknown", f"the scope is incomplete{detail}, so this trigger could not be read."

    totals = _totals(scope)

    if key == "anyType":
        present = [t for t in value if totals["types"].get(t, 0) > 0]
        if present:
            return "yes", ("the target carries " + ", ".join(
                f"{t} x{totals['types'][t]}" for t in sorted(present)) + ".")
        return "no", ("none of " + ", ".join(sorted(value))
                      + f" is present among the {len(totals['types'])} record types measured.")
    if key == "placed":
        n = totals["placed"]
        return ("yes", f"{n} placed record(s) filed under cells.") if n > 0 else (
            "no", "placed: 0 records filed under any cell.")
    if key == "exterior":
        n = totals["exterior"]
        return ("yes", f"placed.exterior is {n}: the target files records in exterior cells "
                       f"({totals['exteriorCells']} grid cell(s) listed).") if n > 0 else (
            "no", "placed.exterior is 0: the target files nothing in an exterior cell.")
    if key == "land":
        n = totals["land"]
        return ("yes", f"{n} LAND record(s).") if n > 0 else ("no", "land: 0 LAND records.")
    if key == "navmesh":
        n = totals["navmesh"]
        return ("yes", f"{n} NAVM record(s).") if n > 0 else ("no", "navmesh: 0 NAVM records.")
    if key == "overlays":
        n = totals["overlays"]
        return ("yes", f"the configuration lists {n} overlay file(s).") if n > 0 else (
            "no", "the configuration lists no overlay (BOS ini) files.")
    if key == "nexusId":
        ids = totals["nexusIds"]
        return ("yes", "the provider's meta.ini names Nexus mod id(s) "
                + ", ".join(str(i) for i in ids) + ".") if ids else (
            "no", "no target resolves to a mod folder with a Nexus id in its meta.ini.")
    if key == "masters":
        n = totals["masters"]
        return ("yes", f"the target(s) declare {n} master(s).") if n > 0 else (
            "no", "the target(s) declare no masters.")

    return "unknown", f"unknown trigger operator {key!r}."


def _combine(subs, scope, any_mode):
    results = [_evaluate(sub, scope) for sub in subs]
    verdicts = [v for v, _ in results]
    win, lose = ("yes", "no") if any_mode else ("no", "yes")
    for verdict, evidence in results:
        if verdict == win:
            return win, evidence
    if "unknown" in verdicts:
        for verdict, evidence in results:
            if verdict == "unknown":
                return "unknown", evidence
    return lose, "; ".join(e for _, e in results)


def _totals(scope):
    """The scope counters the triggers read, summed over every target. Absent sections count as
    zero only because `complete` was already checked above — on an incomplete scope nothing here
    is reached."""
    types, nexus_ids = {}, []
    placed = land = navmesh = masters = ext_cells = exterior = 0
    for target in scope.get("targets") or []:
        footprint = target.get("footprint") if isinstance(target, dict) else None
        if not isinstance(footprint, dict):
            continue
        for name, counts in (footprint.get("recordTypes") or {}).items():
            total = (counts.get("new", 0) + counts.get("override", 0)) if isinstance(counts, dict) \
                else (counts if isinstance(counts, int) else 0)
            types[name] = types.get(name, 0) + total
        placed_block = footprint.get("placed") or {}
        placed += int(placed_block.get("total", 0) or 0)
        exterior += int(placed_block.get("exterior", 0) or 0)
        land += int(placed_block.get("land", 0) or 0)
        navmesh += int(placed_block.get("navm", 0) or 0)
        ext_cells += len(placed_block.get("exteriorCells") or [])
        masters += len(footprint.get("masters") or [])
        if footprint.get("nexusId"):
            nexus_ids.append(footprint["nexusId"])
    overlays = len((scope.get("configuration") or {}).get("overlays") or [])
    return {
        "types": types, "placed": placed, "exterior": exterior, "land": land, "navmesh": navmesh,
        "exteriorCells": ext_cells, "masters": masters, "overlays": overlays,
        "nexusIds": nexus_ids,
    }


DEFAULT_REGISTRY = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    ".claude", "skills", "world-edit-compatibility", "references", "check-registry.json")
