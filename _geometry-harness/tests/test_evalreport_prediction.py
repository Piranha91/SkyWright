#!/usr/bin/env python
"""Checks that an evaluation report leads with the prediction: what adding the mods will break.

Every report the compiler writes has three parts, in this order, before any detail:
  1. the prediction -- for the mod being added (or the pair combined), the clashes between mods the checks find,
     by kind, with what each looks like in game; or "none found", with the coverage that rests on;
  2. the coverage -- what the checks could not judge, by named reason and count, on the report page itself;
  3. the validation against the patch, where one exists -- predicted and resolved (hit), resolved but not
     predicted (blind spot, with why), predicted and left (open), and what the bundle cannot say about the patch.
A difference between a mod and the base game is never part of the prediction; it is counted and kept in the
evidence record.

The bundles are built by hand, small enough to read, with per-setup conflict files so the four-setup comparison
(both mods, each mod alone, neither) is in the data. Each cell is named for the defect it catches and asserts on
the plan AND on the rendered report.md and report.html, because a field the templates never read passes a
plan-only check.

Usage:
  python _geometry-harness/tests/test_evalreport_prediction.py
Exit 0 all pass, 1 a cell failed.
"""
from __future__ import annotations

import hashlib
import html as _html
import json
import re
import sys
import tempfile
import traceback
import struct
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
import os as _os_settings, tempfile as _tf_settings  # noqa: E402
# The evaluation tools read the saved evaluation settings; these known answers use the built-in defaults, never
# the user's own file (a path nothing writes).
_os_settings.environ["HOUSECARL_EVAL_SETTINGS"] = str(Path(_tf_settings.gettempdir()) / "hc-tests-no-eval-settings"
                                                   / "houseCARL.evaluation.json")
sys.path.insert(0, str(HERE.parent))
import evalreport as R                                               # noqa: E402

PRIORITIES = HERE.parent.parent / ".claude/skills/evaluation-reporting/references/report-priorities.json"
CONTACT = "geometry.placement-contact"
SEAMS = "environment.land-seams"
WATER = "environment.water-boundaries"
RECORDS = "records.contested-fields"
CELLATTR = "records.cell-attributes"
FIXTURE = "geometry.fixture-empty-space"
OVERLAP = "geometry.object-overlap"
PROBE = {CONTACT: "contactprobe", SEAMS: "seamprobe", WATER: "waterprobe", RECORDS: None, CELLATTR: None,
         FIXTURE: None, OVERLAP: "overlapprobe", "geometry.navmesh-ownership": "navownprobe",
         "loadability.missing-assets": None, "state.conditional-geometry": "stateprobe",
         "functional.quest-alias-split": "aliasprobe", "geometry.route-cut": "blockprobe"}
NAVOWN = "geometry.navmesh-ownership"
ROUTE_CUT = "geometry.route-cut"
MOD_A, MOD_B = "ModA.esp", "ModB.esp"
OTHER = "OtherMod.esp"                       # a mod already in the order, not evaluated
PATCH = "ModA - ModB patch.esp"
BASE = "Skyrim.esm"


# ------------------------------------------------------------------------------------- bundle builder

def plugins(*names, shas=None):
    return [{"name": n, "sha256": (shas or {}).get(n, "0" * 64), "path": f"X:/x/{n}"} for n in names]


def arm(arm_id, names, kind="candidate", shas=None):
    return {"armId": arm_id, "kind": kind, "candidateFile": f"{arm_id}.json", "provenance": {},
            "requested": {"plugins": plugins(*names, shas=shas), "providers": [], "overlays": [],
                          "population": "load-time"},
            "expected": {"states": ["baseline"], "population": "load-time"}}


def row(kind, a, b, form_key, label=None, other=None):
    """other: the partner object (an overlap's other object, a fixture's removed host), as the probes write it."""
    return {"Kind": kind, "A": a, "B": b, "Summary": f"{kind} between {a} and {b}" + (f" with {other}" if other else ""),
            "Necessary": [{"FormKey": form_key, "Label": label or f"object {form_key[:6]}", "Physical": "Holds"}],
            **({"Alternative": [{"FormKey": other, "Role": "placed-ref"}]} if other else {})}


def lost_row(a, b, form_key, field, lost, winner, overridden, label=None, before=None):
    """A record lane row: `a`'s authored value of `field` (a change from `overridden`) is lost to `b`'s `winner`.
    before: the plugin whose value `overridden` is (the one before `a` in the record's chain); with it the row carries
    its reads, as the record-rows enrichment writes them (that plugin, `a`, `b`)."""
    reads = ([{"batch": "b1", "id": i, "plugin": p} for i, p in enumerate((before, a, b))] if before else None)
    return {"Kind": "LOST-EDIT", "A": a, "B": b, "Field": field,
            "Summary": f"PlacedObject {form_key[:6]}: {field} - {a}'s value lost to {b}'s",
            "Necessary": [{"FormKey": form_key, "Label": label or f"record {form_key[:6]}",
                           "Values": {"lost": lost, "winner": winner, "overridden": overridden},
                           **({"Reads": reads} if reads else {})}]}


def records_transcript(chains):
    """chains: [(formKey, record type, [(plugin, {field: value})])] in load order: the 2.0 by-name reads the record
    imports carry, one `housecarl_records` call per plugin and record."""
    req, resp, n = [{"batchMarker": "b1"}], [{"batchMarker": "b1"}], 0
    for fk, rtype, chain in chains:
        for plugin, fields in chain:
            n += 1
            req.append({"jsonrpc": "2.0", "id": n, "method": "tools/call", "params": {
                "name": "housecarl_records", "arguments": {"formids": [fk], "source": plugin, "format": "json",
                                                           "project": {"form": "fields", "fields": sorted({k.split(".")[0].split("[")[0] for k in fields})}}}})
            body = {"records": [{"formid": fk, "type": rtype, "editorid": None, "source": plugin,
                                 "fields": [{"path": k, "value": v} for k, v in fields.items()]}]}
            resp.append({"jsonrpc": "2.0", "id": n, "result": {"content": [{"type": "text",
                                                                            "text": json.dumps(body)}]}})
    return req, resp


def tree_transcript(chains):
    """chains: [(formKey, record type, [(plugin, {field: value})])] in load order: one 2.0 tree answer per record
    (`housecarl_records` form tree), each provider's differences from the winner with the winner's value in
    parentheses, as the record imports capture them."""
    req = [{"batchMarker": "b1"}, {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
        "name": "housecarl_records", "arguments": {"formids": [c[0] for c in chains], "project": {"form": "tree"}}}}]
    resp = [{"batchMarker": "b1"}]
    rows = []
    for fk, rtype, chain in chains:
        win_p, win = chain[-1]
        nodes = []
        for plugin, fields in chain[:-1]:
            deltas = [f"{k}={v} ({win_p} {win[k]})" if k in win else f"{k}={v}" for k, v in fields.items()
                      if win.get(k) != v]
            nodes.append({"plugin": plugin, "is_winner": False, "is_reference": False, "complete": True,
                          "deltas": deltas})
        nodes.append({"plugin": win_p, "is_winner": True, "is_reference": True})
        rows.append({"formid": fk, "type": rtype, "editorid": None, "reference": win_p,
                     "touchers": [p for p, _ in chain], "nodes": nodes})
    resp.append({"jsonrpc": "2.0", "id": 1, "result": {"content": [{"type": "text", "text": json.dumps({"rows": rows})}]}})
    return req, resp


def make_bundle(root: Path, arms, results, targets, patches=(), missing=(), unjudgeable=(), order=None,
                scope_unmeasured=(), count_only=None, cells=(), plan_id="pred00000000", tool_builds=None,
                enrichment=None, receipt_configs=None, names=None, enrichment_unjudged=None, predicates=None,
                diff_logs=None, states_not_explored=None, transcripts=None, masters=None, coverage_pops=None,
                single_run=(), route_rows=None, programs=None):
    """results: {(checkId, armId): [conflict rows]} -- an empty list is a completed check with no finding.
    missing: [(checkId, armId, status)]. unjudgeable: disposition unassessed.unjudgeablePopulations rows.
    order: {armId: {probe: perProbe counts}} -- the controller's order evaluation of that arm against the baseline.
    count_only: {(checkId, armId): n} -- a check that reported n findings with no conflict rows (no `Conflicts`).
    cells: the first target's placed cells [{worldspace, x, y}] -- the locations every setup's load-order list covers.
    tool_builds: {(checkId, armId): dllSha256} -- the probe build a receipt records; every other receipt records one
    shared build. None records no dll hash.
    programs: {(checkId, armId): {role: {program, kind, digest, checked}}} -- the program identities a receipt binds in
    `consumed.programs` (a receipt under the tool identity contract); every other receipt binds none (a legacy receipt).
    enrichment: {(checkId, armId): [conflict rows]} -- rows derived after measurement for a check `count_only` names,
    written as `enrichment/record-rows/<check>__<arm>.json` with a manifest row of kind `enrichment`.
    receipt_configs: {(checkId, armId): [plugin rows]} -- the configuration a receipt records it consumed; every other
    receipt records its arm's requested plugins.
    names: a names enrichment document, written as `enrichment/names/names.json`.
    enrichment_unjudged: {(checkId, armId): [{FormKey, Field, Reason, Plugins}]} -- the fields that enrichment left
    unjudged, written as its `Unjudged` list and counted in its coverage line.
    predicates: {(checkId, armId): [{Kind, A, B, FormKey, Other, Physical}]} -- the rows of the setup that arm was
    measured against, measured again in that arm's setup (an emit's `Predicates`).
    diff_logs: {(checkId, armId): text} -- the log of that arm's comparison with the baseline (a `diff` receipt).
    transcripts: {(checkId, armId): (request lines, response lines)} -- an MCP import's two transcripts for a check
    `count_only` names, written as `NN-request.jsonl` / `NN-response.jsonl` emits its receipt records.
    masters: {plugin: [its masters]} -- each emit's configuration lists every plugin of its setup with these master
    lists (none listed: no masters); False writes no configuration, as an emit that records no master lists.
    missing rows may carry a fourth element, the part (default `arm`).
    single_run: check ids run once per setup, whose receipts record no part (`<check>__<arm>`, part null), as the
    controller writes them for a check with no comparison step.
    route_rows: {armId: (rows document or None, detector)} -- the route-cut check (a text probe) run once on that arm: its
    receipt records the log and, when a document is given, the rows file blockprobe --rows writes
    (`outputs/geometry.route-cut/<arm>.route-cut-rows.json`); detector None is a run that reached no verdict (name it in
    `missing` with status "completed"), "no-finding" a run with a verdict (counted as a completed null check)."""
    root.mkdir(parents=True, exist_ok=True)
    arm_plugins = {a["armId"]: a["requested"]["plugins"] for a in arms}

    def config_of(check_id, arm_id):
        return list((receipt_configs or {}).get((check_id, arm_id)) or arm_plugins.get(arm_id) or [])
    (root / "receipts").mkdir(exist_ok=True)
    (root / "evidence").mkdir(exist_ok=True)
    count_only = dict(count_only or {})
    checks = sorted({c for c, _ in results} | {m[0] for m in missing} | {c for c, _ in count_only}
                    | ({ROUTE_CUT} if route_rows else set()))
    plan_targets = [{"name": t} for t in targets]
    if cells and plan_targets:
        plan_targets[0]["footprint"] = {"placed": {"cells": list(cells)}}
    plan = {"planId": plan_id, "createdUtc": "2026-01-01T00:00:00+00:00",
            "targets": plan_targets, "arms": arms, "states": {"selected": ["baseline"]},
            "checks": [{"checkId": c, "family": c.split(".")[0], "title": c,
                        "arms": [{"armId": a["armId"], "status": "probe"} for a in arms]} for c in checks]}
    if patches:
        plan["patches"] = list(patches)
    evidence, findings, nulls = [], [], []
    for n, ((check_id, arm_id), rows) in enumerate(sorted(results.items())):
        part_ = None if check_id in single_run else "arm"
        rid = f"{check_id}__{arm_id}" + ("" if part_ is None else "__arm")
        receipt = {"receiptId": rid, "checkId": check_id, "armId": arm_id, "part": part_, "schemaVersion": 1,
                   "exitCode": 0, "validation": {"accepted": True, "identityLimits": []},
                   "result": {"detector": "finding" if rows else "null", "findings": len(rows),
                              # coverage_pops: {(checkId, armId): [coverage lines]} replaces the one default line
                              "coverage": (coverage_pops or {}).get((check_id, arm_id))
                              or [{"Population": check_id, "Judged": 40, "Unjudgeable": {}}]},
                   "consumed": {"tool": {"exe": PROBE[check_id], "dllSha256": (tool_builds or {}).get(
                       (check_id, arm_id), "d" * 64), "coreDllSha256": "c" * 64},
                                "configuration": {"plugins": config_of(check_id, arm_id)},
                                **({"programs": programs[(check_id, arm_id)]} if (check_id, arm_id) in (programs or {})
                                   else {})}}
        emit = {"Population": "placed references", "State": "baseline", "Conflicts": rows,
                "Coverage": [{"Population": check_id, "Judged": 40, "Unjudgeable": {}}],
                **({"Predicates": predicates[(check_id, arm_id)]} if (check_id, arm_id) in (predicates or {}) else {}),
                **({} if masters is False else {"Configuration": {"Plugins": [
                    {"Name": x["name"], "Sha256": x["sha256"], "Masters": list((masters or {}).get(x["name"]) or [])}
                    for x in config_of(check_id, arm_id)]}})}
        (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipt), encoding="utf-8")
        (root / "evidence" / f"{rid}.json").write_text(json.dumps(emit), encoding="utf-8")
        evidence.append({"id": f"{2 * n:016x}", "kind": "receipt", "path": f"receipts/{rid}.json", "sha256": "0" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid})
        evidence.append({"id": f"{2 * n + 1:016x}", "kind": "emit", "path": f"evidence/{rid}.json", "sha256": "1" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid, "strength": "geometric-measurement"})
        if rows:
            findings.append({"checkId": check_id, "armId": arm_id, "part": part_, "findings": len(rows),
                             "verdict": None, "strength": "geometric-measurement"})
        else:
            nulls.append({"checkId": check_id, "armId": arm_id, "part": part_, "coverage": 1})
    for n, ((check_id, arm_id), count) in enumerate(sorted(count_only.items()), start=len(results)):
        rid = f"{check_id}__{arm_id}__arm"
        receipt = {"receiptId": rid, "checkId": check_id, "armId": arm_id, "part": "arm", "schemaVersion": 1,
                   "exitCode": 0, "validation": {"accepted": True, "identityLimits": []},
                   "result": {"detector": "finding", "findings": count},
                   "consumed": {"configuration": {"plugins": config_of(check_id, arm_id)}}}
        (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipt), encoding="utf-8")
        (root / "evidence" / f"{rid}.json").write_text(json.dumps({"Population": "placed references"}), encoding="utf-8")
        evidence.append({"id": f"{2 * n:016x}", "kind": "receipt", "path": f"receipts/{rid}.json", "sha256": "0" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid})
        evidence.append({"id": f"{2 * n + 1:016x}", "kind": "emit", "path": f"evidence/{rid}.json", "sha256": "1" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid, "strength": "geometric-measurement"})
        findings.append({"checkId": check_id, "armId": arm_id, "part": "arm", "findings": count,
                         "verdict": None, "strength": "geometric-measurement"})
        for j, (role, lines) in enumerate(zip(("request", "response"), (transcripts or {}).get((check_id, arm_id)) or ())):
            rel = f"evidence/{rid}-0{j + 1}-{role}.jsonl"
            (root / rel).write_text("\n".join(json.dumps(x) for x in lines) + "\n", encoding="utf-8")
            evidence.append({"id": f"t{2 * n + j:015x}", "kind": "emit", "path": rel, "sha256": "1" * 64,
                             "checkId": check_id, "armId": arm_id, "recordedBy": rid})
    for n, ((check_id, arm_id), text) in enumerate(sorted((diff_logs or {}).items())):
        rid = f"{check_id}__{arm_id}__diff"
        (root / "receipts" / f"{rid}.json").write_text(json.dumps({
            "receiptId": rid, "checkId": check_id, "armId": arm_id, "part": "diff", "schemaVersion": 1, "exitCode": 0,
            "validation": {"accepted": True, "identityLimits": []}, "result": {"detector": "inconclusive"}}),
            encoding="utf-8")
        (root / "evidence" / f"{rid}.txt").write_text(text, encoding="utf-8")
        evidence.append({"id": f"d{2 * n:015x}", "kind": "receipt", "path": f"receipts/{rid}.json", "sha256": "0" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid})
        evidence.append({"id": f"d{2 * n + 1:015x}", "kind": "log", "path": f"evidence/{rid}.txt", "sha256": "1" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid})
    for n, ((check_id, arm_id), rows) in enumerate(sorted((enrichment or {}).items())):
        rel = f"enrichment/record-rows/{check_id}__{arm_id}.json"
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        src = f"evidence/{check_id}__{arm_id}__arm.json"
        uj = list((enrichment_unjudged or {}).get((check_id, arm_id)) or [])
        uj_counts = {}
        for u in uj:
            uj_counts[u["Reason"]] = uj_counts.get(u["Reason"], 0) + 1
        (root / rel).write_text(json.dumps({"Population": "contested records", "State": "baseline", "Conflicts": rows,
                                            "Coverage": [{"Population": check_id, "Judged": 40,
                                                          "Unjudgeable": uj_counts}],
                                            **({"Unjudged": uj} if uj else {}),
                                            "derivedFrom": [{"path": src, "sha256": "1" * 64}]}), encoding="utf-8")
        evidence.append({"id": f"e{n:015x}", "kind": "enrichment", "enrichment": "record-rows", "path": rel,
                         "sha256": "2" * 64, "checkId": check_id, "armId": arm_id,
                         "recordedBy": "enrichment:record-rows", "derivedFrom": [{"path": src, "sha256": "1" * 64}]})
    for n, (arm_id, (doc, detector)) in enumerate(sorted((route_rows or {}).items())):
        rid = f"{ROUTE_CUT}__{arm_id}"
        log_rel = f"logs/{rid}.txt"
        outs = [{"path": log_rel, "sha256": "4" * 64}]
        (root / "evidence" / "logs").mkdir(parents=True, exist_ok=True)
        (root / "evidence" / log_rel).write_text("   ---- rule comparison, all on the same 9 references ----\n",
                                                 encoding="utf-8")
        evidence.append({"id": f"r{2 * n:015x}", "kind": "log", "path": f"evidence/{log_rel}", "sha256": "4" * 64,
                         "checkId": ROUTE_CUT, "armId": arm_id, "recordedBy": rid})
        if doc is not None:
            rows_rel = f"outputs/{ROUTE_CUT}/{arm_id}.route-cut-rows.json"
            (root / "evidence" / rows_rel).parent.mkdir(parents=True, exist_ok=True)
            (root / "evidence" / rows_rel).write_text(json.dumps(doc), encoding="utf-8")
            outs.append({"path": rows_rel, "sha256": "5" * 64})
            evidence.append({"id": f"r{2 * n + 1:015x}", "kind": "emit", "path": f"evidence/{rows_rel}",
                             "sha256": "5" * 64, "checkId": ROUTE_CUT, "armId": arm_id, "recordedBy": rid,
                             "strength": "calibrated-inference"})
        receipt = {"receiptId": rid, "checkId": ROUTE_CUT, "armId": arm_id, "part": None, "schemaVersion": 1,
                   "exitCode": 0, "validation": {"accepted": True, "identityLimits": []}, "outputs": outs, "log": log_rel,
                   "result": {"execution": "completed", "detector": detector, "findings": 0 if detector else None,
                              "coverage": [{"Population": "rows", "Judged": 9, "Unjudgeable": {}}]},
                   "consumed": {"tool": {"exe": "blockprobe", "dllSha256": "d" * 64, "coreDllSha256": "c" * 64},
                                "configuration": {"plugins": config_of(ROUTE_CUT, arm_id)}}}
        (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipt), encoding="utf-8")
        evidence.append({"id": f"q{n:015x}", "kind": "receipt", "path": f"receipts/{rid}.json", "sha256": "0" * 64,
                         "checkId": ROUTE_CUT, "armId": arm_id, "recordedBy": rid})
        if detector == "no-finding":
            nulls.append({"checkId": ROUTE_CUT, "armId": arm_id, "part": None, "coverage": 1})
    if names is not None:
        rel = "enrichment/names/names.json"
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        (root / rel).write_text(json.dumps(names), encoding="utf-8")
        evidence.append({"id": "f" * 16, "kind": "enrichment", "enrichment": "names", "path": rel, "sha256": "3" * 64,
                         "checkId": None, "armId": None, "recordedBy": "enrichment:names"})
    arms_disp = {}
    for a in arms:
        per = (order or {}).get(a["armId"]) or {}
        arms_disp[a["armId"]] = {"kind": a["kind"], "orderEvaluation": {
            "classification": None, "probes": sorted(per), "regression": any(v.get("newDefect") for v in per.values()),
            "perProbe": per, "scope": "test"}}
    disp = {"createdUtc": "2026-01-01T00:00:00+00:00",
            "evaluation": {"complete": not missing, "claim": "incomplete" if missing else "findings",
                           "findings": findings, "completedNullChecks": nulls,
                           "missing": [{"checkId": m[0], "armId": m[1], "part": (m[3] if len(m) > 3 else "arm"),
                                        "status": m[2], "cause": m[2], "required": True,
                                        "family": m[0].split(".")[0]} for m in missing]},
            "arms": arms_disp, "importedEvidence": [],
            "unassessed": {"unjudgeablePopulations": list(unjudgeable), "scopeUnmeasured": list(scope_unmeasured),
                           **({"statesNotExplored": list(states_not_explored)} if states_not_explored else {})}}
    registry = {"registryVersion": 1, "checks": [{"id": c, "family": c.split(".")[0], "title": c,
                                                   "adapter": {"exe": PROBE[c]}} for c in checks]}
    (root / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    (root / "disposition.json").write_text(json.dumps(disp), encoding="utf-8")
    (root / "ledger.json").write_text(json.dumps({"entries": []}), encoding="utf-8")
    (root / "registry").mkdir(exist_ok=True)
    (root / "registry" / "check-registry.json").write_text(json.dumps(registry), encoding="utf-8")
    (root / "manifest.json").write_text(json.dumps({
        "bundleSchema": 2, "plan": "plan.json", "disposition": "disposition.json", "ledger": "ledger.json",
        "registry": "registry/check-registry.json", "evidence": evidence}), encoding="utf-8")
    return root


def compile_and_render(arms, results, targets, want_index=False, sibling=None, settings_extra=None, order_setups=None,
                       also=None, **kw):
    """The real compiler and the real templates: (plan, report.md, report.html).

    sibling: (arms, results, targets, kw) -- a second bundle (the evaluation without the patch) built beside this one
    and handed to the compiler as `sibling_dir`; absent, the compiler is called exactly as before."""
    pr = json.loads(PRIORITIES.read_text(encoding="utf-8"))
    conc, page = pr.get("concise") or {}, pr.get("page") or {}
    settings = {"mode": "concise", "layout": conc.get("layout", "normal"), "maxPages": conc.get("maxPages", 8),
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "declaredPatches": [], "rowsPerGroup": conc.get("rowsPerGroup", 3),
                "figuresPerFinding": conc.get("figuresPerFinding", 1),
                "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height")}}
    settings.update(settings_extra or {})
    with tempfile.TemporaryDirectory() as td:
        bdir = make_bundle(Path(td) / "bundle", arms, results, targets, **kw)
        extra = {}
        if sibling is not None:
            s_arms, s_results, s_targets, s_kw = sibling
            extra["sibling_dir"] = make_bundle(Path(td) / "sibling", s_arms, s_results, s_targets, **s_kw)
        if order_setups is not None:
            extra["order_setups"] = order_setups
        if also:
            # (fifth review) further evaluations supplied with the report (--also-bundle): (arms, results, targets, kw)
            settings["alsoBundles"] = [str(make_bundle(Path(td) / f"also{i_}", a_[0], a_[1], a_[2], **a_[3]))
                                       for i_, a_ in enumerate(also)]
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir), **extra)
        view = R.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], pr, "normal")
        out = R.render_all(plan, view, Path(td) / "out", "")
    if want_index == "all":
        return plan, out                      # (tenth review) every rendered file, for the anchor test
    if want_index:
        return plan, out["report.md"], out["report.html"], out["evidence-index.md"]
    return plan, out["report.md"], out["report.html"]


# ------------------------------------------------------------------------------------------ helpers

def reader(plan):
    return plan.get("reader") or {}


def headings(plan):
    return reader(plan).get("sectionHeadings") or {}


def md_heading_order(md):
    return [ln[3:].strip() for ln in md.splitlines() if ln.startswith("## ")]


def html_heading_order(html):
    return [re.sub(r"<[^>]+>", "", h).strip() for h in re.findall(r"<h2[^>]*>(.*?)</h2>", html, re.S)]


def section_text(md, heading):
    """report.md text under `## heading` up to the next `## `; '' when the heading is absent."""
    lines, on, out = md.splitlines(), False, []
    for ln in lines:
        if ln.startswith("## "):
            if on:
                break
            on = ln[3:].strip() == heading
            continue
        if on:
            out.append(ln)
    return "\n".join(out)


def three_part_order(plan, md, html):
    """The three headings the plan names, and whether report.md and report.html put them first, in order."""
    h = headings(plan)
    want = [h.get("prediction"), h.get("coverage"), h.get("validation")]
    if not all(want):
        return False, f"the plan names no prediction/coverage/validation headings: {sorted(h)}"
    md_order, html_order = md_heading_order(md), html_heading_order(html)
    ok = md_order[:3] == want and html_order[:3] == want
    return ok, f"want {want}; report.md opens with {md_order[:4]}; report.html opens with {html_order[:4]}"


# ----------------------------------------------------------------------------------- the four setups

def four_setup_parts():
    """Both mods, each alone, and neither -- the unpatched four-setup comparison (no patch in this bundle)."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B], kind="baseline"),
            arm("dependency-only", [BASE, OTHER]),
            arm("plus-a", [BASE, OTHER, MOD_A]),
            arm("plus-b", [BASE, OTHER, MOD_B])]
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]       # only with both mods
    a_alone = [row("CONTACT", OTHER, MOD_A, f"000901:{MOD_A}")]                       # ModA against a mod already there
    base_game = [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")]                      # a mod differing from the game
    already = [row("CONTACT", BASE, OTHER, f"000B01:{OTHER}")]                        # present before either mod
    same_mod = [row("CONTACT", MOD_A, MOD_A, f"000C01:{MOD_A}")]                      # one mod against itself
    unsplit = [row("LAND-SEAM", OTHER, MOD_B, f"000D01:{MOD_B}")]                    # single setups not measured
    results = {(CONTACT, "baseline"): both + a_alone + base_game + already + same_mod,
               (CONTACT, "dependency-only"): already,
               (CONTACT, "plus-a"): a_alone + base_game + already + same_mod,
               (CONTACT, "plus-b"): already,
               (SEAMS, "baseline"): unsplit,
               (SEAMS, "dependency-only"): []}
    missing = [(SEAMS, "plus-a", "rejected(extraction-failed)"), (SEAMS, "plus-b", "rejected(extraction-failed)")]
    order = {"plus-a": {"contactprobe": {"verdict": "DIFFERENTIAL-VERIFIED", "repaired": 3, "reclassified": 0,
                                         "created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}}}
    return arms, results, missing, order


def four_setup_unpatched(settings_extra=None):
    arms, results, missing, order = four_setup_parts()
    return compile_and_render(arms, results, [MOD_A, MOD_B], missing=missing, order=order,
                              settings_extra=settings_extra)


def patched_bundle():
    """The patched order as the baseline and the same order without the declared patch."""
    arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
            arm("without-patch", [BASE, MOD_A, MOD_B])]
    unpatched = ([row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]
                 + [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")])
    patched = [row("CONTACT", MOD_B, MOD_A, f"000803:{MOD_A}"),                       # left by the patch
               row("CONTACT", PATCH, MOD_A, f"000E01:{PATCH}")]                       # made by the patch
    results = {(CONTACT, "baseline"): patched, (CONTACT, "without-patch"): unpatched}
    order = {"without-patch": {"contactprobe": {"verdict": "DIFFERENTIAL-VERIFIED", "repaired": 1, "reclassified": 0,
                                                "created": 3, "newDefect": 3, "attributed": 0, "unmeasured": 0}}}
    return compile_and_render(arms, results, [MOD_A, MOD_B], patches=[PATCH], order=order)


def none_found_bundle():
    """One mod added to an order; nothing between mods found; some objects could not be judged; one check missing."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("dependency-only", [BASE, OTHER])]
    results = {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}
    unjudgeable = [{"armId": "baseline", "checkId": CONTACT, "judged": 40, "population": 45,
                    "unjudgeable": {"no collision geometry": 3, "outside the measured cells": 2}}]
    missing = [(WATER, "baseline", "rejected(extraction-failed)")]
    return compile_and_render(arms, results, [MOD_A], unjudgeable=unjudgeable, missing=missing)


USSEP = "Unofficial Skyrim Special Edition Patch.esp"
OTHER2 = "SecondMod.esp"


def already_present_and_mod_partner_bundle():
    """One mod added to an order that holds two other mods and the Unofficial Patch. A clash between the two other
    mods is present with and without the evaluated mod (already present); a clash between the evaluated mod and the
    Unofficial Patch appears only with it (a clash between mods, from the evaluated mod)."""
    arms = [arm("baseline", [BASE, USSEP, OTHER, OTHER2, MOD_A], kind="baseline"),
            arm("dependency-only", [BASE, USSEP, OTHER, OTHER2])]
    already = [row("CONTACT", OTHER, OTHER2, f"000F01:{OTHER2}")]
    partner = [row("CONTACT", USSEP, MOD_A, f"000F02:{MOD_A}")]
    results = {(CONTACT, "baseline"): already + partner, (CONTACT, "dependency-only"): already}
    return compile_and_render(arms, results, [MOD_A])


def partner_alone_bundle():
    """(Missives final report review, finding 1) One mod added beside a compared mod OTHER2. OTHER2 against the base game
    is there without the evaluated mod and gone without OTHER2: OTHER2's own problem, not one already present without
    the evaluated mods. A clash between two third mods stays already present."""
    arms = [arm("baseline", [BASE, OTHER, OTHER2, MOD_A], kind="baseline"),
            arm("without-a", [BASE, OTHER, OTHER2]),
            arm("without-other2", [BASE, OTHER, MOD_A])]
    own = [row("CONTACT", BASE, OTHER2, f"000F03:{OTHER2}")]
    already = [row("CONTACT", BASE, OTHER, f"000F04:{OTHER}")]
    partner = [row("CONTACT", OTHER2, MOD_A, f"000F05:{MOD_A}")]
    results = {(CONTACT, "baseline"): own + already + partner, (CONTACT, "without-a"): own + already,
               (CONTACT, "without-other2"): already}
    return compile_and_render(arms, results, [MOD_A])


def coded_reason_bundle():
    """One mod added; nothing between mods; the probe could not judge some objects for a reason it gives only as a
    code -- one the skill names in words (NoGeometry) and one it does not (ZqxOddCode)."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("dependency-only", [BASE, OTHER])]
    results = {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}
    unjudgeable = [{"armId": "baseline", "checkId": CONTACT, "judged": 40, "population": 49,
                    "unjudgeable": {"NoGeometry": 5, "ZqxOddCode": 4}}]
    return compile_and_render(arms, results, [MOD_A], want_index=True, unjudgeable=unjudgeable)


def area_bundle():
    """One mod added; one cannot-tell reason with one count; two areas the controller did not measure -- one the skill
    names in words (script-placement) and one it does not (zqx-odd-extent)."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("dependency-only", [BASE, OTHER])]
    results = {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}
    unjudgeable = [{"armId": "baseline", "checkId": CONTACT, "judged": 40, "population": 47,
                    "unjudgeable": {"NoGeometry": 7}}]
    return compile_and_render(arms, results, [MOD_A], want_index=True, unjudgeable=unjudgeable,
                              scope_unmeasured=[{"extent": "script-placement", "why": "not run"},
                                                {"extent": "zqx-odd-extent", "why": "not run"}])


def base_game_only_bundle():
    """One mod whose only measured difference is against the base game, and no other setup measured."""
    arms = [arm("baseline", [BASE, MOD_A], kind="baseline")]
    results = {(CONTACT, "baseline"): [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")]}
    return compile_and_render(arms, results, [MOD_A])


def four_arms(shas=None):
    return [arm("baseline", [BASE, OTHER, MOD_A, MOD_B], kind="baseline", shas=shas),
            arm("dependency-only", [BASE, OTHER], shas=shas),
            arm("plus-a", [BASE, OTHER, MOD_A], shas=shas),
            arm("plus-b", [BASE, OTHER, MOD_B], shas=shas)]


def detail_bundle():
    """The four setups; on the setup with both mods a clash between them, a difference with the base game and one mod
    against itself; on the setup with ModA alone a clash with a mod already there, the same two non-clashes, and a
    check that reported a count with no rows."""
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]
    a_alone = [row("CONTACT", OTHER, MOD_A, f"000901:{MOD_A}")]
    base_game = [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")]
    same_mod = [row("CONTACT", MOD_A, MOD_A, f"000C01:{MOD_A}")]
    results = {(CONTACT, "baseline"): both + base_game + same_mod, (CONTACT, "dependency-only"): [],
               (CONTACT, "plus-a"): a_alone + base_game + same_mod, (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], want_index=True,
                              count_only={(WATER, "plus-a"): 5})


CELLS = [{"worldspace": "Tamriel", "x": x, "y": y} for x in (-17, -16) for y in (24, 25, 26)]


def shared_scope_bundle():
    """The four setups, all covering the same six cells: the list of locations is the same for every setup."""
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2)]
    results = {(CONTACT, "baseline"): both, (CONTACT, "dependency-only"): [], (CONTACT, "plus-a"): [],
               (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], cells=CELLS)


PATCHED_ARMS = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, MOD_A, MOD_B])]
UNPATCHED_ROWS = ([row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]
                  + [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")])


def patched_with_sibling(variant="same", main_extra=None, sib_extra=None):
    """The patched bundle (the patched order and the same order without the patch; no single-mod setup) compiled with
    a sibling: the evaluation of the unpatched order, which measured both mods, each alone and neither.
    variant `same`: the sibling's setup with both mods is the patched bundle's setup without the patch, plugin for
    plugin and hash for hash, and measured the same rows there. `foreign`: the sibling's ModA has other bytes, so no
    sibling setup is the one without the patch. `disagrees`: same plugins, but on that setup the sibling measured
    only two of the three clashes. `build`: same plugins and rows, but the sibling ran the check with another probe
    build on that setup. main_extra / sib_extra: further make_bundle arguments for the patched bundle / the sibling."""
    patched = [row("CONTACT", MOD_B, MOD_A, "000803:" + MOD_A), row("CONTACT", PATCH, MOD_A, f"000E01:{PATCH}")]
    results = {(CONTACT, "baseline"): patched, (CONTACT, "without-patch"): UNPATCHED_ROWS}
    shas = {MOD_A: "a" * 64} if variant == "foreign" else None
    s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline", shas=shas),
              arm("dependency-only", [BASE], shas=shas),
              arm("plus-a", [BASE, MOD_A], shas=shas),
              arm("plus-b", [BASE, MOD_B], shas=shas)]
    s_base = UNPATCHED_ROWS[1:] if variant == "disagrees" else UNPATCHED_ROWS
    s_results = {(CONTACT, "baseline"): s_base, (CONTACT, "dependency-only"): [],
                 (CONTACT, "plus-a"): [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")], (CONTACT, "plus-b"): []}
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], want_index=True, patches=[PATCH],
                              **(main_extra or {}),
                              sibling=(s_arms, s_results, [MOD_A, MOD_B], {
                                  "plan_id": "sibl00000000",
                                  "tool_builds": {(CONTACT, "baseline"): "e" * 64} if variant == "build" else None,
                                  **(sib_extra or {})}))


def busy_third_mod_bundle():
    """The four setups: two clashes need both mods; ModA alone has five clashes with a mod already in the order, so the
    third mod has more rows than the pair."""
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2)]
    a_alone = [row("CONTACT", OTHER, MOD_A, f"00090{i}:{MOD_A}") for i in range(1, 6)]
    results = {(CONTACT, "baseline"): both + a_alone, (CONTACT, "dependency-only"): [],
               (CONTACT, "plus-a"): a_alone, (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B])


def two_units_bundle(settings_extra=None):
    """The four setups: four needs-both rows on three objects (one object loses two supports)."""
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]
    second = dict(row("CONTACT", MOD_B, MOD_A, f"000801:{MOD_A}"),
                  Summary=f"CONTACT between {MOD_B} and {MOD_A}, second support")
    results = {(CONTACT, "baseline"): both + [second], (CONTACT, "dependency-only"): [],
               (CONTACT, "plus-a"): [], (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], settings_extra=settings_extra)


def record_rows_bundle():
    """The four setups; the record check reported counts only, and rows derived after measurement (an enrichment)
    sit beside each count: ModA's change lost to ModB on the setup with both (needs both), OtherMod's change lost to
    ModA on the setups with ModA (one mod alone), and ModA's change lost to the base game (set aside)."""
    nb = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    alone = [lost_row(OTHER, MOD_A, f"000711:{BASE}", "Placement.Position", "o", "a", "v")]
    base = [lost_row(MOD_A, BASE, f"000721:{BASE}", "Scale", "a", "v", "w")]
    enr = {(RECORDS, "baseline"): nb + alone + base, (RECORDS, "dependency-only"): [],
           (RECORDS, "plus-a"): alone + base, (RECORDS, "plus-b"): []}
    count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
    results = {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): [], (CONTACT, "plus-a"): [],
               (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], want_index=True, count_only=count_only,
                              enrichment=enr)


def record_repeat_bundle():
    """Two record checks report the same two lost edits (a cell's fields are in both populations)."""
    nb = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "Music", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    enr = {}
    for c in (RECORDS, CELLATTR):
        enr.update({(c, "baseline"): nb, (c, "dependency-only"): [], (c, "plus-a"): [], (c, "plus-b"): []})
    count_only = {k: max(1, len(v)) for k, v in enr.items()}
    results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr)


def record_validation_bundle():
    """The patched order and the same order without the patch; record rows on both. Without the patch, ModA's change
    to three records is lost to ModB. With the patch: on 000701 the patch sets ModA's value (ModB's change is lost to
    the patch) -- acted on; on 000702 nothing changes -- left; on 000703 the patch carries ModB's value unchanged
    (ModA's change is lost to the patch, with ModB's value) -- kept on purpose by the patch (C463: until the window-2
    review this was counted "left", which printed the patch author's choices as unfixed problems)."""
    p_rows = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v")
              for i in (1, 2, 3)]
    w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
              lost_row(MOD_A, MOD_B, f"000702:{BASE}", "LocationReference", "a2", "b2", "v"),
              lost_row(MOD_A, PATCH, f"000703:{BASE}", "LocationReference", "a3", "b3", "v")]
    enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): w_rows}
    count_only = {(RECORDS, "without-patch"): 3, (RECORDS, "baseline"): 3}
    results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], count_only=count_only,
                              enrichment=enr)


def sibling_only_bundle(variant="same"):
    """The patched bundle measured the record check only with the patch; the sibling (the unpatched evaluation)
    measured it on all four setups. `same`: the sibling's receipt on the shared setup records the same plugins, order
    and bytes as the setup without the patch. `other-bytes`: that receipt records ModB with other bytes, while the
    sibling's plan arm is unchanged (so the bundle-level anchor still matches)."""
    nb = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1")]
    main_enr = {(RECORDS, "baseline"): w_rows}
    results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
    s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("dependency-only", [BASE]),
              arm("plus-a", [BASE, MOD_A]), arm("plus-b", [BASE, MOD_B])]
    s_enr = {(RECORDS, "baseline"): nb, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): [],
             (RECORDS, "plus-b"): []}
    s_results = {(CONTACT, a["armId"]): [] for a in s_arms}
    cfg = None
    if variant == "other-bytes":
        cfg = {(RECORDS, "baseline"): plugins(BASE, MOD_A, MOD_B, shas={MOD_B: "b" * 64})}
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], want_index=True, patches=[PATCH],
                              count_only={(RECORDS, "baseline"): 1}, enrichment=main_enr,
                              sibling=(s_arms, s_results, [MOD_A, MOD_B], {
                                  "plan_id": "sibl00000000", "enrichment": s_enr, "receipt_configs": cfg,
                                  "count_only": {(RECORDS, a["armId"]): 1 for a in s_arms}}))


def sibling_record_join_bundle():
    """A check that runs no probe (the record check, read through the MCP server), measured by both evaluations on
    the shared setup with the same rows: neither receipt records a probe build, because there is none."""
    nb = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1")]
    main_enr = {(RECORDS, "baseline"): w_rows, (RECORDS, "without-patch"): nb}
    results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
    s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("dependency-only", [BASE]),
              arm("plus-a", [BASE, MOD_A]), arm("plus-b", [BASE, MOD_B])]
    s_enr = {(RECORDS, "baseline"): nb, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): [],
             (RECORDS, "plus-b"): []}
    s_results = {(CONTACT, a["armId"]): [] for a in s_arms}
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], want_index=True, patches=[PATCH],
                              count_only={(RECORDS, "baseline"): 1, (RECORDS, "without-patch"): 2},
                              enrichment=main_enr,
                              sibling=(s_arms, s_results, [MOD_A, MOD_B], {
                                  "plan_id": "sibl00000000", "enrichment": s_enr,
                                  "count_only": {(RECORDS, a["armId"]): max(1, len(s_enr[(RECORDS, a["armId"])]))
                                                 for a in s_arms}}))


NAMES_DOC = {"schema": "names-v1",
             "places": {"object 000801": {"name": "Castle Hall", "formKey": "000801:ModA.esp"}},
             "objects": {"CommonChair01": {"name": "Chair", "formKey": "000123:Skyrim.esm"}},
             "mods": {MOD_A: {"name": "Mod A Ordinary", "source": "declared"},
                      MOD_B: {"name": "ModB", "source": "plugin-name"}},
             "missing": [{"formKey": "000124:Skyrim.esm", "editorId": "SBanner", "type": "Static"}],
             "derivedFrom": [{"path": "X:/x/ModA.esp", "sha256": "0" * 64}]}


def names_bundle():
    both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2)]
    results = {(CONTACT, "baseline"): both, (CONTACT, "dependency-only"): [], (CONTACT, "plus-a"): [],
               (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], names=NAMES_DOC)


def mixed_kinds_bundle(settings_extra=None):
    """The four setups; two kinds of clash need both mods: four contact rows on three objects (one object loses two
    supports) and seven record fields on two records (ModA's change lost to ModB). Every sum across the two kinds
    (4 + 7, 4 + 2, 3 + 2, 3 + 7) is a number no single kind has."""
    contact = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2, 3)]
    contact.append(dict(row("CONTACT", MOD_B, MOD_A, f"000801:{MOD_A}"),
                        Summary=f"CONTACT between {MOD_B} and {MOD_A}, second support"))
    fields = [("000701", f) for f in ("Name", "Music", "Location", "Owner")] + [
        ("000702", f) for f in ("Name", "Music", "Location")]
    lost = [lost_row(MOD_A, MOD_B, f"{fk}:{BASE}", fld, "a", "b", "v") for fk, fld in fields]
    enr = {(RECORDS, "baseline"): lost, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): [],
           (RECORDS, "plus-b"): []}
    results = {(CONTACT, "baseline"): contact, (CONTACT, "dependency-only"): [], (CONTACT, "plus-a"): [],
               (CONTACT, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], settings_extra=settings_extra,
                              count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr)


TRUNC = "a field the read's expansion was cut off before"
NOT_READ = "a field the capture did not read"


def unjudged_fields_bundle():
    """record_rows_bundle's setups and rows, with fields the enrichment left unjudged: on the setup with both mods,
    three fields of a record both evaluated mods change (cut-off read) and two of a record only ModA changes (not
    read); on the setup with ModA alone, one more (not on the prediction setup, so not counted)."""
    nb = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    enr = {(RECORDS, "baseline"): nb, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): [],
           (RECORDS, "plus-b"): []}
    both = [{"FormKey": f"018A5A:{BASE}", "Type": "Location", "Field": f, "Reason": TRUNC,
             "Plugins": [BASE, MOD_A, MOD_B]} for f in ("Music", "Name", "ParentLocation")]
    one = [{"FormKey": f"000711:{BASE}", "Type": "Cell", "Field": f, "Reason": NOT_READ,
            "Plugins": [BASE, OTHER, MOD_A]} for f in ("Regions", "Water")]
    other_setup = [{"FormKey": f"000712:{BASE}", "Type": "Cell", "Field": "Water", "Reason": NOT_READ,
                    "Plugins": [BASE, MOD_A]}]
    results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only={k: max(1, len(v)) for k, v in enr.items()},
                              enrichment=enr, enrichment_unjudged={(RECORDS, "baseline"): both + one,
                                                                   (RECORDS, "plus-a"): other_setup})


def html_section(html, heading):
    """report.html text under `<h2>heading</h2>` up to the next `<h2`; '' when the heading is absent."""
    m = re.search(r"<h2[^>]*>\s*" + re.escape(heading) + r"\s*</h2>(.*?)(?=<h2|\Z)", html, re.S)
    return m.group(1) if m else ""


def index_section(index, anchor):
    """evidence-index.md text after the `<a id="anchor">` line, through its heading, up to the next anchor line or the
    next heading; '' when the anchor is absent."""
    lines = index.splitlines()
    start = next((i for i, ln in enumerate(lines) if f'<a id="{anchor}"></a>' in ln), None)
    if start is None:
        return ""
    out, seen_heading = [], False
    for ln in lines[start + 1:]:
        if ln.startswith('<a id="') and not ln.startswith("| "):
            break
        if ln.startswith("#"):
            if seen_heading:
                break
            seen_heading = True
        out.append(ln)
    return "\n".join(out)


# ------------------------------------------- window-2 review bundles (C460-C499: the report review of 2026-10-16)

PATCH_ADDED_ARMS = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"),
                    arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]


def object_identity_bundle():
    """C460. The four setups. A third mod's fixture X hangs with both mods loaded, and the probe names ModB as the
    remover of one of its hosts; with ModA alone the same fixture already hangs, ModA named as the remover of another
    host. The fixture is broken with ModA alone: it is not a clash the pair adds."""
    p_rows = [row("EMPTY-SPACE", OTHER, MOD_B, f"000F01:{OTHER}", other=f"000H02:{BASE}")]
    a_rows = [row("EMPTY-SPACE", OTHER, MOD_A, f"000F01:{OTHER}", other=f"000H01:{BASE}")]
    results = {(FIXTURE, "baseline"): p_rows, (FIXTURE, "dependency-only"): [], (FIXTURE, "plus-a"): a_rows,
               (FIXTURE, "plus-b"): []}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B])


def field_identity_bundle():
    """C461. The four setups. OtherMod's changes to two fields of one record are lost to ModA with both mods loaded;
    with ModA alone only the Scale field is lost. Placement needs both mods; Scale comes from ModA alone."""
    p_rows = [lost_row(OTHER, MOD_A, f"000771:{BASE}", "Placement", "o1", "a1", "v1"),
              lost_row(OTHER, MOD_A, f"000771:{BASE}", "Scale", "o2", "a2", "v2")]
    a_rows = [lost_row(OTHER, MOD_A, f"000771:{BASE}", "Scale", "o2", "a2", "v2")]
    enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): a_rows,
           (RECORDS, "plus-b"): []}
    count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
    results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
    return compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr)


def overlap_predicate_bundle():
    """C462. The order with both mods and the same order with the patch added. Two predicted overlaps on object X:
    with Y and with Z. With the patch, its emit holds X's overlap with a third object W (a new pair) and none of the
    two predicted ones, but its predicates say X still crosses Y (the patch now owns one of them) and no longer
    crosses Z. One resolved, one left, one new."""
    p_rows = [row("OVERLAP", MOD_B, MOD_A, f"000X01:{MOD_A}", other=f"000Y01:{MOD_B}"),
              row("OVERLAP", MOD_B, MOD_A, f"000X01:{MOD_A}", other=f"000Z01:{MOD_B}")]
    w_rows = [row("OVERLAP", MOD_B, MOD_A, f"000X01:{MOD_A}", other=f"000W01:{MOD_B}")]
    preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": f"000X01:{MOD_A}", "Other": f"000Y01:{MOD_B}",
              "Physical": "Holds"},
             {"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": f"000X01:{MOD_A}", "Other": f"000Z01:{MOD_B}",
              "Physical": "Gone"}]
    results = {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): w_rows}
    return compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                              predicates={(OVERLAP, "with-patch"): preds})


def one_target_bundle():
    """C468. One evaluated mod (ModA, with its own resources plugin) added to an order holding OtherMod. Setups: all
    of them; without ModA (its resources leave with it); without OtherMod. Object 1's clash with OtherMod is not there
    without OtherMod: it needs both. Object 2 is unsupported without OtherMod too: it comes from ModA."""
    res = "ModA Resources.esp"
    arms = [arm("baseline", [BASE, OTHER, res, MOD_A], kind="baseline"),
            arm("without-a", [BASE, OTHER]),
            arm("without-other", [BASE, res, MOD_A])]
    results = {(CONTACT, "baseline"): [row("CONTACT", OTHER, MOD_A, f"000G01:{MOD_A}", other=f"000S01:{OTHER}"),
                                       row("CONTACT", OTHER, MOD_A, f"000G02:{MOD_A}", other=f"000S02:{OTHER}")],
               (CONTACT, "without-a"): [],
               (CONTACT, "without-other"): [row("CONTACT", BASE, MOD_A, f"000G02:{MOD_A}", other=f"000S03:{BASE}")]}
    return compile_and_render(arms, results, [MOD_A])


def limits_bundle():
    """C469-C474. One mod added; the limits section's inputs as a real bundle carries them: the census's state classes
    as records; one check's two populations of different units, each listed twice (the setup's own receipt and its
    comparison's); a reason naming the check that judges those items instead; one check missing for two reasons on
    the same setups; a check that failed to run; a check with no pair list to search."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("dependency-only", [BASE, OTHER])]
    results = {(SEAMS, "baseline"): [], (SEAMS, "dependency-only"): [], (CONTACT, "baseline"): [],
               (CONTACT, "dependency-only"): []}
    pops = [{"checkId": SEAMS, "armId": "baseline", "population": "exterior cells with a LAND", "judged": 9,
             "unjudgeable": {"UnreadableLand": 0, "DeclaresNoHeights": 205}},
            {"checkId": SEAMS, "armId": "baseline", "population": "edge-adjacent pairs", "judged": 9,
             "unjudgeable": {"Undecodable": 0, "DeclaresNoHeights": 391}},
            {"checkId": CONTACT, "armId": "baseline", "population": "navmesh and landscape records", "judged": 0,
             "unjudgeable": {"navmesh geometry: judged by geometry.navmesh-ownership, not field-compared here": 73}}]
    states = [{"checkId": "state.conditional-geometry", "armId": "baseline", "subject": "disabled-singletons",
               "kind": "class", "code": "disabled-singletons", "reason": "Initially disabled references.", "count": 12916},
              {"checkId": "state.conditional-geometry", "armId": "baseline", "subject": "combinations", "kind": "class",
               "code": "combinations", "reason": "Combinations are not enumerated.", "count": None}]
    missing = [(WATER, "baseline", "unknown-applicability"), (WATER, "baseline", "unsupported"),
               (RECORDS, "baseline", "rejected(exit-nonzero)"), (CELLATTR, "baseline", "blocked(no-pair-source)")]
    return compile_and_render(arms, results, [MOD_A], unjudgeable=pops + pops, missing=missing,
                              states_not_explored=states)


def history_bundle():
    """C476. The order with both mods and the same order with the patch added. ModB re-cuts a navmesh ModA edited
    (overwrite history, not a predicted clash); the physical test holds (ModA's ground lost). The patch removes it."""
    results = {(NAVOWN, "baseline"): [row("CLOBBERED", MOD_A, MOD_B, f"000N01:{BASE}")], (NAVOWN, "with-patch"): []}
    return compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH])


def two_patch_bundle():
    """C479. Two declared patches, the one that loads first named second in the alphabet."""
    arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"),
            arm("with-patches", [BASE, MOD_A, MOD_B, "Zeta patch.esp", "Alpha patch.esp"])]
    results = {(CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, f"000801:{MOD_A}")], (CONTACT, "with-patches"): []}
    return compile_and_render(arms, results, [MOD_A, MOD_B], patches=["Zeta patch.esp", "Alpha patch.esp"])


def placeholder_value_bundle():
    """C464. As record_validation_bundle, but every winner value is the placeholder a hand-built import wrote instead
    of the value it read. Where the patch wins, whether it changed the outcome cannot be told; where it does not win
    (000702), the edit is left whatever the values say."""
    ph = "(the winner's value)"
    p_rows = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", ph, "v") for i in (1, 2, 3)]
    w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", ph, "a1"),
              lost_row(MOD_A, MOD_B, f"000702:{BASE}", "LocationReference", "a2", ph, "v"),
              lost_row(MOD_A, PATCH, f"000703:{BASE}", "LocationReference", "a3", ph, "v")]
    enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): w_rows}
    count_only = {(RECORDS, "without-patch"): 3, (RECORDS, "baseline"): 3}
    results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], count_only=count_only,
                              enrichment=enr)


def comparison_bundle(variant):
    """C465-C467. The order with both mods, and the same order with the patch added; two predicted clashes.
    `refused`: the contact comparison refused a verdict (UNJUDGED, its log's REFUSED line), though it counts 2
    repaired. `unaccepted`: the contact comparison counts 1 new problem but 1 row could not be measured in both
    setups, so the controller lists it as missing. `holding`: the overlap comparison repaired one predicted overlap
    and the other left the class still holding (the patch now owns it), so the patched setup reports none."""
    pair = [row("CONTACT" if variant != "holding" else "OVERLAP", MOD_B, MOD_A, f"00081{i}:{MOD_A}",
                other=f"00091{i}:{MOD_B}") for i in (1, 2)]
    check, probe = (CONTACT, "contactprobe") if variant != "holding" else (OVERLAP, "overlapprobe")
    results = {(check, "baseline"): pair, (check, "with-patch"): []}
    counts = {"refused": dict(verdict="DIFFERENTIAL-UNJUDGED", repaired=2, reclassified=0, created=0, newDefect=0,
                              attributed=0, unmeasured=0),
              "unaccepted": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0, created=1, newDefect=1,
                                 attributed=0, unmeasured=1),
              "holding": dict(verdict="DIFFERENTIAL-VERIFIED", repaired=1, reclassified=1, created=0, newDefect=0,
                              attributed=0, unmeasured=0)}[variant]
    log = {"refused": "   repaired     : 2 record(s)\n   ! REFUSED: dependent-host pairs: unjudgeable 8.33% -> 23.47% "
                      "(313 of 3,757 -> 1,461 of 6,226) — rose by more than 1.00%\n",
           "unaccepted": "   created      : 1 record(s)\n   unmeasured   : 1 record(s)\n",
           "holding": "   reclassified : 1 record(s)   (left the class still holding 1; left with no physical defect to "
                      "begin with 0; entered reading Gone 0)\n"}[variant]
    missing = [(check, "with-patch", "completed", "diff")] if variant != "holding" else []
    return compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], missing=missing,
                              order={"with-patch": {probe: counts}}, diff_logs={(check, "with-patch"): log})


def priorities():
    return json.loads(PRIORITIES.read_text(encoding="utf-8"))


# -------------------------------------------------------------------------------------------- cells

def route_row(verdict, n, cell_, passage=None, share=None, navcut=False, mesh="Clutter\\BarrelA.nif"):
    return {"Verdict": verdict, "OldRule": "Clipped", "FormKey": f"000A0{n}:{MOD_A}", "Cell": cell_, "Mesh": mesh,
            "By": [MOD_A], "Touched": 12, "Passage": passage, "PassageLeft": passage, "PassageRight": passage,
            "PassageCap": 160, "PassageShare": share, "Navcut": navcut, "Why": f"{verdict} by the route-cut rule"}


# blockprobe --rows, as written for one arm: one Blocked and two LikelyBlocked objects (one at the passage cap), and two rows
# that are not leads (a NoLine object, and a LikelyBlocked navcut box, an unconfirmed candidate of its own).
ROUTE_CUT_ROWS = {"Probe": "blockprobe", "Targets": [MOD_A], "Rows": [
    route_row("Blocked", 1, "HallA", 32),
    route_row("LikelyBlocked", 2, "YardB", 160, "about half the time"),
    route_row("LikelyBlocked", 3, "YardB", 72, "about half the time"),
    route_row("NoLine", 4, "HallA"),
    route_row("LikelyBlocked", 5, "YardB", 96, "about half the time", navcut=True, mesh="")]}


def route_cut_bundle(detector, doc):
    """MOD_A added to an order; one contact clash; the route-cut check run on the baseline arm with `detector` (None: no
    verdict) and the rows document `doc` (None: no rows file, as every bundle before blockprobe --rows)."""
    arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("dependency-only", [BASE, OTHER])]
    results = {(CONTACT, "baseline"): [row("CONTACT", OTHER, MOD_A, f"000801:{MOD_A}")], (CONTACT, "dependency-only"): []}
    missing = [(ROUTE_CUT, "baseline", "completed", None)] if detector is None else []
    return compile_and_render(arms, results, [MOD_A], want_index=True, missing=missing,
                              route_rows={"baseline": (doc, detector)})


def route_cut_two_arm_bundle():
    """(Gate A review M1, L1) The patched order and the order without the patch, the route-cut check without a verdict on
    both. The baseline's leads include a base-game object the evaluated mod changes (000B01:Skyrim.esm), which is not the
    mod's own object; the arm without the patch has one lead of its own."""
    doc_b = {"Probe": "blockprobe", "Targets": [MOD_A, MOD_B], "Rows": [
        route_row("LikelyBlocked", 2, "YardB", 72, "about half the time"),
        dict(route_row("Blocked", 1, "HallA", 32), FormKey=f"000B01:{BASE}", By=[MOD_A], Mesh="Plants\\Garlic01.nif")]}
    doc_w = {"Probe": "blockprobe", "Targets": [MOD_A, MOD_B], "Rows": [route_row("LikelyBlocked", 3, "YardB", 96, "about half the time")]}
    results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): [row("CONTACT", MOD_B, MOD_A, f"000801:{MOD_A}")]}
    missing = [(ROUTE_CUT, "baseline", "completed", None), (ROUTE_CUT, "without-patch", "completed", None)]
    return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], want_index=True, missing=missing,
                              route_rows={"baseline": (doc_b, None), "without-patch": (doc_w, None)})


def main():
    cells = []

    def cell(name, fn):
        only_ = _os_settings.environ.get("HC_CELLS")              # a development run of some cells only (comma list)
        if only_ and not any(x_ and x_ in name for x_ in only_.split(",")):
            return
        try:
            ok, detail = fn()
        except Exception as ex:                                       # a crash is a failure, never a skip
            ok, detail = False, f"raised {type(ex).__name__}: {ex}"
            traceback.print_exc()
        cells.append((name, bool(ok), detail))

    built = {}

    def get(name, fn):
        if name not in built:
            built[name] = fn()
        return built[name]

    # 1. prediction missing: the four-setup bundle must lead with what adding the pair will break
    def prediction_leads_four_setup():
        plan, md, html = get("four", four_setup_unpatched)
        ok, why = three_part_order(plan, md, html)
        return ok, why

    def four_setup_attribution():
        plan, md, html = get("four", four_setup_unpatched)
        p = reader(plan).get("prediction") or {}
        items = p.get("items") or []
        by = {(i.get("checkId"), tuple(sorted(i.get("mods") or []))): i for i in items}
        both = by.get((CONTACT, tuple(sorted([MOD_A, MOD_B])))) or {}
        alone = by.get((CONTACT, tuple(sorted([MOD_A, OTHER])))) or {}
        unsplit = by.get((SEAMS, tuple(sorted([MOD_B, OTHER])))) or {}
        ok = (p.get("status") == "found" and len(items) == 3
              and both.get("count") == 3 and both.get("attribution") == "needs-both"
              and alone.get("count") == 1 and alone.get("attribution") == "single-mod"
              and alone.get("attributionMod") == MOD_A
              and unsplit.get("count") == 1 and unsplit.get("attribution") == "not-separable"
              and bool(unsplit.get("attributionWhy")))
        return ok, f"prediction items: {[(i.get('checkId'), i.get('mods'), i.get('count'), i.get('attribution')) for i in items]}"

    def four_setup_in_game_words():
        plan, md, html = get("four", four_setup_unpatched)
        items = (reader(plan).get("prediction") or {}).get("items") or []
        text = section_text(md, headings(plan).get("prediction") or "\0")
        ok = bool(items) and all(i.get("inGame") and i["inGame"] in text for i in items if i.get("checkId") == CONTACT)
        return ok, f"each contact item carries what it looks like in game, printed under the prediction heading"

    def four_setup_set_aside_not_predicted():
        plan, md, html = get("four", four_setup_unpatched)
        p = reader(plan).get("prediction") or {}
        aside = p.get("setAside") or {}
        text = section_text(md, headings(plan).get("prediction") or "\0")
        ok = (aside.get("baseGame") == 1 and aside.get("sameMod") == 1 and aside.get("alreadyPresent") == 1
              and BASE not in text and bool(text))
        return ok, f"setAside {aside}; the prediction section names the base game: {BASE in text}"

    # 2. prediction after the patch tally: a patched bundle must lead with the unpatched prediction
    def patched_leads_with_prediction():
        plan, md, html = get("patched", patched_bundle)
        v = str(reader(plan).get("verdict") or "")
        ok, why = three_part_order(plan, md, html)
        ok = ok and v.startswith("Adding ") and PATCH not in v
        return ok, f"verdict {v[:110]!r}; {why}"

    def patched_validation_counts():
        plan, md, html = get("patched", patched_bundle)
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = (val.get("status") == "compared" and val.get("hits") == 2 and val.get("open") == 1
              and val.get("patchProblems") == 1 and PATCH in text
              and bool(val.get("missingFacts")))
        return ok, (f"validation hits {val.get('hits')}, open {val.get('open')}, patch problems "
                    f"{val.get('patchProblems')}, missing facts {len(val.get('missingFacts') or [])}")

    # 3. a blind spot without its reason: a resolution nothing predicted must say why it was not predicted
    def blind_spot_has_why():
        plan, md, html = get("patched", patched_bundle)
        spots = (reader(plan).get("validation") or {}).get("blindSpots") or []
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = (sum(int(s.get("count") or 0) for s in spots) == 1
              and all(str(s.get("why") or "").strip() and s["why"] in text for s in spots))
        return ok, f"blind spots {[(s.get('count'), s.get('why')) for s in spots]}"

    # 4. "none found" without coverage: the statement must rest on named cannot-tell reasons and counts, on the page
    def none_found_has_coverage():
        plan, md, html = get("none", none_found_bundle)
        p = reader(plan).get("prediction") or {}
        v = str(reader(plan).get("verdict") or "")
        cov_head = headings(plan).get("coverage") or "\0"
        cov = section_text(md, cov_head)
        ok_order, why = three_part_order(plan, md, html)
        ok = (p.get("status") == "none-found" and v.startswith("Adding ") and "will not introduce" in v
              and ok_order and "no collision geometry" in cov and re.search(r"\b3\b", cov)
              and "outside the measured cells" in cov and re.search(r"\b2\b", cov))
        return ok, f"status {p.get('status')!r}; verdict {v[:90]!r}; coverage on the page: {cov.strip()[:160]!r}; {why}"

    def none_found_html_coverage():
        plan, md, html = get("none", none_found_bundle)
        return ("no collision geometry" in html and "outside the measured cells" in html,
                "report.html names each cannot-tell reason")

    # 5. a base-game-only difference in the prediction
    def base_game_not_a_prediction():
        plan, md, html = get("basegame", base_game_only_bundle)
        p = reader(plan).get("prediction") or {}
        v = str(reader(plan).get("verdict") or "")
        text = section_text(md, headings(plan).get("prediction") or "\0")
        items = p.get("items") or []
        ok = (p.get("status") == "none-found" and not items and "conflict" not in v.lower()
              and v.startswith("Adding ") and BASE not in text and bool(text)
              and (p.get("setAside") or {}).get("baseGame") == 1)
        return ok, f"verdict {v[:120]!r}; items {len(items)}; prediction names the base game: {BASE in text}"

    def no_patch_says_so():
        plan, md, html = get("four", four_setup_unpatched)
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        return (val.get("status") == "no-patch" and bool(text.strip()),
                f"validation status {val.get('status')!r}; section text {text.strip()[:100]!r}")

    def reel_opens_with_prediction():
        plan, md, html = get("patched", patched_bundle)
        p = reader(plan).get("prediction") or {}
        opening = str(((plan.get("reelNarration") or {}).get("opening") or {}).get("narration") or "")
        ok = bool(p.get("verdict")) and opening.startswith(p["verdict"]) and reader(plan).get("verdict") == p["verdict"]
        return ok, f"opening narration {opening[:100]!r}"

    # 6. a clash already present without the evaluated mod is set aside, never predicted
    def already_present_set_aside():
        plan, md, html = get("already", already_present_and_mod_partner_bundle)
        p = reader(plan).get("prediction") or {}
        text = section_text(md, headings(plan).get("prediction") or "\0")
        items = p.get("items") or []
        ok = ((p.get("setAside") or {}).get("alreadyPresent") == 1 and bool(text) and OTHER2 not in text
              and not any(OTHER2 in (i.get("mods") or []) for i in items))
        return ok, f"setAside {p.get('setAside')}; prediction names {OTHER2}: {OTHER2 in text}"

    # 6b. a compared mod's own clash with the base game, gone without that mod, is said as that mod's, not as already
    # present without the evaluated mods (Missives final report review, finding 1)
    def partner_alone_set_aside():
        plan, md, html = get("partner-alone", partner_alone_bundle)
        p = reader(plan).get("prediction") or {}
        aside = p.get("setAside") or {}
        line = str(p.get("setAsideLine") or "")
        ok = (aside.get("partnerAlone") == 1 and aside.get("alreadyPresent") == 1
              and f"of {OTHER2} against the base game, there with {OTHER2} alone" in line)
        return ok, f"setAside {aside}; line {line[-200:]!r}"

    # 7. a non-target mod (not the base game) on one side is a clash between mods, attributed to the evaluated mod
    def mod_partner_admitted():
        plan, md, html = get("already", already_present_and_mod_partner_bundle)
        p = reader(plan).get("prediction") or {}
        items = p.get("items") or []
        hit = [i for i in items if sorted(i.get("mods") or []) == sorted([MOD_A, USSEP])]
        text = section_text(md, headings(plan).get("prediction") or "\0")
        ok = (p.get("status") == "found" and len(items) == 1 and len(hit) == 1 and hit[0].get("count") == 1
              # no setup without the Unofficial Patch was measured, so whether the clash needs it too is not
              # claimed (C468; until the window-2 review this read "comes from ModA alone")
              and hit[0].get("attribution") == "with-target" and hit[0].get("attributionMod") == MOD_A
              and USSEP in text and "alone" not in text)
        return ok, f"items {[(i.get('mods'), i.get('count'), i.get('attribution'), i.get('attributionMod')) for i in items]}"

    # 8. a probe's reason code reaches the reader as words, never as the code (the code stays in the evidence index)
    def unmapped_code_not_on_page():
        plan, md, html, index = coded_reason_bundle()
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        spaced = "zqx odd code"
        ok = (bool(cov) and "ZqxOddCode" not in md and spaced not in md.lower() and "ZqxOddCode" not in html
              and spaced not in html.lower() and "ZqxOddCode" in index and re.search(r"\b4\b", cov)
              and "no geometry" in cov.lower() and "NoGeometry" not in md)
        return ok, f"coverage on the page: {cov.strip()[:220]!r}"

    # 9. a coverage line about a check that also feeds the prediction names the setup that lacks the result
    def missing_names_its_setup():
        plan, md, html = get("four", four_setup_unpatched)
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        line = next((ln for ln in cov.splitlines() if "Landscape height changes" in ln), "")
        ok = "removes ModA.esp" in line and "removes ModB.esp" in line
        return ok, f"coverage line: {line!r}"

    # 10. the opening scope sentence does not name the base game as a conflict partner
    def scope_line_no_base_game():
        plan, md, html = get("basegame", base_game_only_bundle)
        s = str(reader(plan).get("scopeLine") or "")
        return BASE not in s, f"scope line {s!r}"

    # 11. an unmeasured area reaches the reader as words, never as the controller's id (the id stays in the index)
    def extent_ids_not_on_page():
        plan, md, html, index = area_bundle()
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        ok = (bool(cov) and "script-placement" not in md and "zqx-odd-extent" not in md
              and "script-placement" not in html and "zqx-odd-extent" not in html
              and "scripts" in cov.lower() and "script-placement" in index and "zqx-odd-extent" in index)
        return ok, f"coverage on the page: {cov.strip()[:300]!r}"

    # 12. a coverage line states its count once, and a setup name after "for" reads as part of the sentence
    def count_once_setup_lowercase():
        plan, md, html, index = area_bundle()
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        line = next((ln for ln in cov.splitlines() if "Could not be judged" in ln), "")
        plan4, md4, _ = get("four", four_setup_unpatched)
        cov4 = section_text(md4, headings(plan4).get("coverage") or "\0")
        ok = (line.count("7") == 1 and "for the tested alternative setup" in cov4 and "for Tested" not in cov4)
        return ok, f"line {line!r}; setup wording in four-setup coverage: {'for Tested' in cov4}"

    # 13. the detail list shows only clashes between two mods; the rest is in the evidence index with its reason
    detail_heading = "What the checks found"

    def detail_only_inter_mod():
        plan, md, html, index = get("detail", detail_bundle)
        sec, hsec = section_text(md, detail_heading), html_section(html, detail_heading)
        kept = [f"between {MOD_B} and {MOD_A}", f"between {OTHER} and {MOD_A}"]
        gone = [BASE, f"between {MOD_A} and {MOD_A}", "reported 5"]
        aside_line = next((ln for ln in sec.splitlines() if "detail-set-aside" in ln), "")
        ok = (all(k in sec and k in hsec for k in kept) and not any(g in sec or g in hsec for g in gone)
              and "evidence-index.md#detail-set-aside" in sec and "evidence-index.html#detail-set-aside" in hsec
              and re.search(r"\b5\b", aside_line) is not None)
        return ok, (f"kept on the page {[k in sec for k in kept]}; set aside still on the page "
                    f"{[g for g in gone if g in sec or g in hsec]}; set-aside line {aside_line.strip()[:160]!r}")

    def detail_set_aside_in_index():
        plan, md, html, index = get("detail", detail_bundle)
        words = ((priorities().get("reader") or {}).get("prediction") or {}).get("detailAside") or {}
        aside = [f for f in plan.get("findings") or [] if f.get("kind") == "finding" and f.get("readerRelevant")
                 and f.get("readerDetail") is False]
        reasons = sorted({str(f.get("readerDetailAside")) for f in aside})
        sec = index_section(index, "detail-set-aside")
        ok = (len(aside) == 5 and reasons == ["baseGame", "countOnly", "sameMod"]
              and all(f["id"] in sec for f in aside)
              and all(words.get(r) and words[r] in sec for r in reasons))
        return ok, (f"set aside {[(f.get('id'), f.get('readerDetailAside')) for f in aside]}; "
                    f"words {sorted(words)}; index section {sec.strip()[:160]!r}")

    def detail_already_present_not_listed():
        plan, md, html = get("already", already_present_and_mod_partner_bundle)
        sec = section_text(md, detail_heading)
        return OTHER2 not in sec and USSEP in sec, f"detail list names {OTHER2}: {OTHER2 in sec}"

    # 14. a patched bundle given the unpatched evaluation as its sibling attributes its prediction
    def sibling_attributes():
        plan, md, html, index = patched_with_sibling("same")
        p = reader(plan).get("prediction") or {}
        items = [(i.get("checkId"), i.get("count"), i.get("attribution")) for i in p.get("items") or []]
        words = (priorities().get("reader") or {}).get("prediction") or {}
        text = section_text(md, headings(plan).get("prediction") or "\0")
        src = str(p.get("siblingSource") or "")
        ok = (items == [(CONTACT, 3, "needs-both")] and words["attribution"]["needs-both"] in text
              and bool(src) and src in text and "sibl00000000" in index and "sibl00000000" not in md)
        return ok, f"items {items}; source sentence {src[:120]!r} on the page: {bool(src) and src in text}"

    def validation_by_item():
        plan, md, html, index = patched_with_sibling("same")
        p = reader(plan).get("prediction") or {}
        val = reader(plan).get("validation") or {}
        by = val.get("byItem")
        ok = (isinstance(by, list) and len(by) == len(p.get("items") or [])
              and len(by) == 1 and {k: by[0].get(k) for k in ("hits", "open", "notMeasured", "cannotTell")}
              == {"hits": 2, "open": 1, "notMeasured": 0, "cannotTell": 0})
        return ok, f"validation.byItem {by!r} for items {[(i.get('checkId'), i.get('count')) for i in p.get('items') or []]}"

    def sibling_foreign_refused():
        try:
            patched_with_sibling("foreign")
        except Exception as ex:                                       # noqa: BLE001
            ok = type(ex).__name__ == "InputRefused" and "sibling" in str(ex).lower() and str(ex).count(". ") == 0
            return ok, f"raised {type(ex).__name__}: {ex}"
        return False, "a sibling with no setup matching the one without the patch was accepted"

    def sibling_disagreement_unseparated(variant="disagrees"):
        plan, md, html, index = patched_with_sibling(variant)
        p = reader(plan).get("prediction") or {}
        words = (priorities().get("reader") or {}).get("prediction") or {}
        why = (words.get("notSeparableWhy") or {}).get("siblingDisagrees")
        items = p.get("items") or []
        text = section_text(md, headings(plan).get("prediction") or "\0")
        ok = (bool(why) and len(items) == 1 and items[0].get("attribution") == "not-separable"
              and items[0].get("attributionWhy") == why and why in text)
        return ok, f"items {[(i.get('attribution'), i.get('attributionWhy')) for i in items]}; why {why!r}"

    def sibling_cli_flag():
        try:
            a = R.build_parser().parse_args(["plan", "--bundle", "B", "--out", "O", "--sibling-bundle", "S"])
        except SystemExit as ex:                                      # argparse refuses an unknown option by exiting
            return False, f"plan refused the option (exit {ex.code})"
        return getattr(a, "sibling_bundle", None) == "S", f"parsed {vars(a).get('sibling_bundle')!r}"

    # 15. the measured load order takes its article mid-sentence
    def measured_load_order_article():
        plan, md, html = get("none", none_found_bundle)
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        ok = "(for the measured load order)" in cov and "for measured load order" not in md + html
        return ok, f"coverage on the page: {cov.strip()[:200]!r}"

    # 16. a list of locations every setup shares is printed once
    def shared_scope_printed_once():
        plan, md, html = shared_scope_bundle()
        lo = (reader(plan).get("locationOrder") or {}).get("heading") or "Load order for these locations"
        words = (priorities().get("reader") or {}).get("locationOrder") or {}
        sec, hsec = section_text(md, lo), html_section(html, lo)
        first = "Tamriel (-17, 24)"
        ok = (sec.count(first) == 1 and hsec.count(first) == 1 and bool(words.get("sameScope"))
              and sec.count(words["sameScope"]) == 3)
        return ok, (f"{first!r} in report.md {sec.count(first)} time(s), report.html {hsec.count(first)}; "
                    f"sameScope words {words.get('sameScope')!r}")

    # 47. a long list of locations is printed up to the skill's bound, with the rest counted and in the evidence index
    #     (Gate C round 1, H2: Group 6's footprint list alone took about seven pages, before this task too)
    def long_location_list_bounded():
        cells = [{"worldspace": "Tamriel", "x": x, "y": y} for x in range(-20, -10) for y in (24, 25, 26)]
        both = [row("CONTACT", MOD_B, MOD_A, f"00080{i}:{MOD_A}") for i in (1, 2)]
        results = {(CONTACT, "baseline"): both, (CONTACT, "dependency-only"): [], (CONTACT, "plus-a"): [],
                   (CONTACT, "plus-b"): []}
        plan, md, html, index = compile_and_render(four_arms(), results, [MOD_A, MOD_B], want_index=True, cells=cells)
        words = (priorities().get("reader") or {}).get("locationOrder") or {}
        k, more_t = words.get("maxLocations"), words.get("moreLocations")
        lo = words.get("heading") or "Load order for these locations"
        sec, hsec = section_text(md, lo), html_section(html, lo)
        labels = [f"Tamriel ({c['x']}, {c['y']})" for c in cells]
        if not (isinstance(k, int) and 0 < k < len(labels) and more_t):
            return False, f"skill data names no bound below {len(labels)}: maxLocations {k!r}, moreLocations {more_t!r}"
        more = R.bind(more_t, {"count": len(labels) - k})
        flat = " ".join(sec.split())
        ok = (all(x in flat for x in labels[:k]) and not any(x in flat or x in hsec for x in labels[k:])
              and more in flat and more in " ".join(hsec.split()) and all(x in index for x in labels))
        return ok, (f"printed {sum(1 for x in labels if x in flat)} of {len(labels)} (bound {k}); "
                    f"more-line on the page {more in flat}; all in the index {all(x in index for x in labels)}")

    # ---- 4.24 phase 1: the admission rule everywhere (cells 17-37)
    pw = (priorities().get("reader") or {}).get("prediction") or {}

    # 17. the verdict counts what needs both mods; single-mod and unseparated clashes are outside the number
    def per_kind(lst):
        """A per-kind count list as (checkId, relationships, objects); anything else (a sum) as itself."""
        if not isinstance(lst, list):
            return lst
        return [(x.get("checkId"), x.get("relationships"), x.get("objects")) for x in lst]

    def verdict_counts(p):
        return [(x.get("checkId"), x.get("unit"), x.get("count")) for x in p.get("verdictCounts") or []]

    def verdict_needs_both_only():
        plan, md, html = get("four", four_setup_unpatched)
        p = reader(plan).get("prediction") or {}
        v = str(p.get("verdict") or "")
        counts = p.get("counts") or {}
        clause = str(pw.get("verdictNotSeparable") or "")
        head = clause.split("{")[0]
        ok = ("total" not in p and verdict_counts(p) == [(CONTACT, "relationships", 3)]
              and re.search(r"\b3\b", v) is not None and re.search(r"\b5\b", v) is None
              and per_kind(counts.get("needsBoth")) == [(CONTACT, 3, 3)]
              and per_kind(counts.get("singleMod")) == [(CONTACT, 1, 1)]
              and per_kind(counts.get("notSeparable")) == [(SEAMS, 1, 1)]
              and bool(head) and head in v and re.search(r"\b1\b", v[v.find(head):]) is not None)
        return ok, (f"total {p.get('total')}; verdict counts {verdict_counts(p)}; counts "
                    f"{ {k: per_kind(c) for k, c in counts.items()} }; verdict {v!r}")

    # 19. a single-mod clash is shown under its own heading, after the pair's
    def single_mod_own_heading():
        plan, md, html = get("four", four_setup_unpatched)
        head = pw.get("singleModHeading")
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        # since P53 the single-mod clashes are one line per check: "from ModA, with OtherMod 1"
        line = f"from {MOD_A}, with {OTHER} 1"
        at_h, at_l = (sec.find(head) if head else -1), sec.find(line)
        item = next((i for i in (reader(plan).get("prediction") or {}).get("items") or []
                     if i.get("attribution") == "single-mod"), {})
        ok = bool(head) and at_h >= 0 and at_l > at_h and item.get("shownAs") == "alone" and head in html
        return ok, f"heading {head!r} at {at_h}; single-mod line at {at_l}; item shownAs {item.get('shownAs')!r}"

    # 21. --single-mod aside sets the single-mod clash aside with its own reason and count
    def single_mod_aside():
        plan, md, html = four_setup_unpatched({"singleMod": "aside"})
        p = reader(plan).get("prediction") or {}
        items = [(i.get("attribution"), i.get("count")) for i in p.get("items") or []]
        rec = [e for e in p.get("record") or [] if e.get("admission") == "singleMod"]
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        ok = (p.get("singleModMode") == "aside" and ("single-mod", 1) not in items and len(items) == 2
              and (p.get("setAside") or {}).get("singleMod") == 1 and len(rec) == 1
              and f"between {MOD_A} and {OTHER}" not in sec)
        return ok, f"mode {p.get('singleModMode')!r}; items {items}; setAside {p.get('setAside')}"

    # 23. with one evaluated mod every admitted clash is the subject and is counted (guards item 4.26)
    def one_mod_counts_its_clashes():
        plan, md, html = get("already", already_present_and_mod_partner_bundle)
        p = reader(plan).get("prediction") or {}
        v = str(p.get("verdict") or "")
        return (verdict_counts(p) == [(CONTACT, "relationships", 1)] and re.search(r"\b1\b", v) is not None,
                f"verdict counts {verdict_counts(p)}; verdict {v!r}")

    # 25. the scope line names the evaluated pair, not the third mod with the most rows
    def scope_line_names_pair():
        plan, md, html = busy_third_mod_bundle()
        r = reader(plan)
        s = str(r.get("scopeLine") or "")
        pair_t = ((priorities().get("reader") or {}).get("scopeWords") or {}).get("pair")
        want = R.bind(pair_t, {"modA": MOD_A, "modB": MOD_B}) if pair_t else None
        ok = (r.get("scopePair") == [MOD_A, MOD_B] and bool(want) and want in s and want in md
              and f"conflicts involving the target are with {OTHER}" not in s)
        return ok, f"scopePair {r.get('scopePair')}; scope line {s!r}"

    # 27. both units are counted per item; the report's default headline unit is relationships
    def both_units_counted():
        plan, md, html = two_units_bundle()
        p = reader(plan).get("prediction") or {}
        it = next((i for i in p.get("items") or [] if i.get("attribution") == "needs-both"), {})
        v = str(p.get("verdict") or "")
        ok = (it.get("relationships") == 4 and it.get("objects") == 3 and p.get("headlineUnit") == "relationships"
              and verdict_counts(p) == [(CONTACT, "relationships", 4)] and re.search(r"\b4\b", v) is not None)
        return ok, (f"item {({k: it.get(k) for k in ('count', 'relationships', 'objects')})}; "
                    f"unit {p.get('headlineUnit')!r}; verdict {v!r}")

    def objects_unit_switch():
        plan, md, html = two_units_bundle({"headlineUnit": "objects"})
        p = reader(plan).get("prediction") or {}
        v = str(p.get("verdict") or "")
        ok = (p.get("headlineUnit") == "objects" and verdict_counts(p) == [(CONTACT, "objects", 3)]
              and re.search(r"\b3\b", v) is not None)
        return ok, f"unit {p.get('headlineUnit')!r}; verdict counts {verdict_counts(p)}; verdict {v!r}"

    # 29. record rows derived after measurement are read as the check's rows; the index says they were derived
    def record_rows_read():
        plan, md, html, index = record_rows_bundle()
        p = reader(plan).get("prediction") or {}
        rec = {(i.get("checkId"), i.get("attribution")): i.get("count") for i in p.get("items") or []}
        cov = [ln for ln in (reader(plan).get("coverage") or {}).get("lines") or []
               if ln.get("reason") == (pw.get("coverageReasons") or {}).get("countOnly")]
        derived = (priorities().get("reader") or {}).get("enrichmentLine")
        ok = (rec.get((RECORDS, "needs-both")) == 2 and rec.get((RECORDS, "single-mod")) == 1
              and (p.get("setAside") or {}).get("baseGame", 0) >= 1 and not cov
              and bool(derived) and derived.split("{")[0] in index)
        return ok, (f"record items {rec}; count-only coverage lines {len(cov)}; "
                    f"skill line {derived!r} in the index: {bool(derived) and derived.split('{')[0] in index}")

    # 31. a record row is resolved when the patch sets a different winning value; the patch's choice is not a problem
    def record_validation_acted_on():
        plan, md, html = record_validation_bundle()
        val = reader(plan).get("validation") or {}
        # fifth blind review (W-S2): the patch keeping ModA's value drops ModB's change: "switched", not resolved
        ok = (val.get("switched") == 1 and val.get("hits") == 0 and val.get("open") == 1 and val.get("kept") == 1
              and val.get("patchProblems") == 0)
        return ok, (f"switched {val.get('switched')}, hits {val.get('hits')}, kept {val.get('kept')}, open {val.get('open')}, "
                    f"patch problems {val.get('patchProblems')}")

    # 33. a check only the sibling measured on the shared setup is joined when its receipt's configuration matches
    def sibling_only_joined():
        plan, md, html, index = sibling_only_bundle("same")
        p = reader(plan).get("prediction") or {}
        chk = next((c for c in (p.get("sibling") or {}).get("checks") or [] if c.get("checkId") == RECORDS), {})
        rec = [e for e in p.get("record") or [] if e.get("checkId") == RECORDS and e.get("admission") == "admitted"]
        ok = (chk.get("joined") is True and chk.get("how") == "from-sibling-only" and len(rec) == 2
              and all(e.get("source") == "sibling" for e in rec)
              and any(i.get("checkId") == RECORDS and i.get("attribution") == "needs-both"
                      for i in p.get("items") or []))
        return ok, f"sibling record for the check {chk}; admitted rows {len(rec)}"

    # 35. ... and refused, with the reason, when the sibling's receipt records other bytes for one plugin (C2)
    def sibling_only_refuses_other_bytes():
        plan, md, html, index = sibling_only_bundle("other-bytes")
        p = reader(plan).get("prediction") or {}
        chk = next((c for c in (p.get("sibling") or {}).get("checks") or [] if c.get("checkId") == RECORDS), {})
        why = str(chk.get("why") or "")
        ok = (chk.get("joined") is False and "configuration" in why
              and not any(i.get("checkId") == RECORDS for i in p.get("items") or []))
        return ok, f"sibling record for the check {chk}"

    # 37. the bundle's names reach the plan, with where each came from
    def names_in_plan():
        plan, md, html = names_bundle()
        n = reader(plan).get("names") or {}
        ok = ((n.get("places") or {}).get("object 000801", {}).get("name") == "Castle Hall"
              and (n.get("objects") or {}).get("CommonChair01", {}).get("name") == "Chair"
              and (n.get("mods") or {}).get(MOD_A, {}).get("source") == "declared"
              and len(n.get("missing") or []) == 1 and bool(n.get("derivedFrom")))
        return ok, f"reader.names keys {sorted(n)}"

    # 39. a lost edit two record checks both report is one relationship (found on Group 1 at Gate C)
    def record_repeat_counted_once():
        plan, md, html = record_repeat_bundle()
        p = reader(plan).get("prediction") or {}
        rel = sum(x[1] for x in per_kind(p["counts"]["needsBoth"])) if isinstance(
            (p.get("counts") or {}).get("needsBoth"), list) else None
        ok = rel == 2 and (p.get("setAside") or {}).get("repeat") == 2
        return ok, f"needs-both lost edits {rel}; setAside {p.get('setAside')}"

    # 41. each kind of clash is stated in its own unit and two kinds are never summed (Gate C round 1, H1)
    def kinds_never_summed(settings_extra=None, contact_n=4):
        plan, md, html = mixed_kinds_bundle(settings_extra)
        p = reader(plan).get("prediction") or {}
        v = str(p.get("verdict") or "")
        nums = {int(x) for x in re.findall(r"\b\d+\b", v)}
        unit = (settings_extra or {}).get("headlineUnit", "relationships")
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        ok = ("total" not in p
              and verdict_counts(p) == [(CONTACT, unit, contact_n), (RECORDS, "objects", 2)]
              # fifth blind review (W-S4): a record kind states its records and, in brackets, its fields (7)
              and {contact_n, 2} <= nums and not nums & {11, 6, 5, 10} and "(7 record fields)" in v
              and re.search(r"\b7 record fields\b", sec) is not None and re.search(r"\b7 record fields\b", html) is not None)
        return ok, f"verdict counts {verdict_counts(p)}; numbers in the verdict {sorted(nums)}; verdict {v!r}"

    # 43. the fields a record check left unjudged are counted on the page with why, and how many could hide a clash
    #     between the evaluated mods (Gate C round 1, M1)
    def unjudged_fields_counted():
        plan, md, html = unjudged_fields_bundle()
        reason = (pw.get("coverageReasons") or {}).get("fieldsUnjudged")
        both_t = pw.get("fieldsUnjudgedBoth")
        lines = [ln for ln in (reader(plan).get("coverage") or {}).get("lines") or [] if reason and ln.get("reason") == reason]
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        subj = str(lines[0].get("subject") or "") if len(lines) == 1 else ""
        ok = (bool(reason) and bool(both_t) and len(lines) == 1 and lines[0].get("count") == 5
              and lines[0].get("unit") == "fields" and TRUNC in subj and NOT_READ in subj
              and R.bind(both_t, {"count": 3}) in subj and subj in cov and subj in _html.unescape(html))
        return ok, (f"skill reason {reason!r}; lines {lines}; on report.md {bool(subj) and subj in cov}; "
                    f"on report.html {bool(subj) and subj in _html.unescape(html)}")

    # 45. a record group between one evaluated mod and another mod is not in the detail list (Gate C round 1, H2:
    #     listed once per pair the reports still broke the page limit); the evidence index lists it with why, and
    #     the page counts it in one sentence
    def single_mod_record_groups_to_index():
        plan, md, html, index = record_rows_bundle()
        words = pw.get("detailAside") or {}
        sec = section_text(md, detail_heading)
        pair_lines = [ln for ln in sec.splitlines() if f"between {OTHER} and {MOD_A}" in ln]
        groups = [f for f in plan.get("findings") or [] if f.get("kind") == "finding" and f.get("checkId") == RECORDS
                  and {(f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")} == {OTHER, MOD_A}]
        pair_kept = [f for f in plan.get("findings") or [] if f.get("kind") == "finding" and f.get("readerDetail")
                     and {(f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")} == {MOD_A, MOD_B}]
        line = str(reader(plan).get("detailSingleModLine") or "")
        aside_sec = index_section(index, "detail-set-aside")
        ok = (len(groups) == 2 and all(f.get("readerDetail") is False and f.get("readerDetailAside") == "singleMod"
                                       for f in groups)
              and not pair_lines and bool(pair_kept) and f"between {MOD_A} and {MOD_B}" in sec
              and bool(line) and line in sec and re.search(r"\b2\b", line) is not None
              and bool(words.get("singleMod")) and words["singleMod"] in aside_sec
              and all(f["id"] in aside_sec for f in groups))
        return ok, (f"groups {[(f.get('armId'), f.get('readerDetail'), f.get('readerDetailAside')) for f in groups]}; "
                    f"lines on the page {len(pair_lines)}; single-mod line {line!r}")

    for name, fn in [
            ("record-rows/a-lost-edit-two-checks-report-is-counted-once", record_repeat_counted_once),
            ("verdict-units/each-kind-in-its-own-unit-never-summed", kinds_never_summed),
            ("verdict-units/each-kind-in-its-own-unit-never-summed-objects",
             lambda: kinds_never_summed({"headlineUnit": "objects"}, 3)),
            ("coverage/unjudged-record-fields-counted-with-why", unjudged_fields_counted),
            ("detail-list/a-single-mod-record-group-goes-to-the-evidence-index", single_mod_record_groups_to_index),
            ("admission-everywhere/verdict-counts-needs-both-only", verdict_needs_both_only),
            ("admission-everywhere/single-mod-clash-under-its-own-heading", single_mod_own_heading),
            ("admission-everywhere/single-mod-aside-switch-sets-it-aside-with-its-reason", single_mod_aside),
            ("admission-everywhere/one-evaluated-mod-counts-its-clashes", one_mod_counts_its_clashes),
            ("admission-everywhere/scope-line-names-the-pair-not-the-busiest-third-mod", scope_line_names_pair),
            ("headline-unit/both-units-counted-relationships-by-default", both_units_counted),
            ("headline-unit/objects-switch", objects_unit_switch),
            ("record-rows/derived-rows-are-read-as-the-check-rows", record_rows_read),
            ("record-rows/patch-validation-by-acted-on", record_validation_acted_on),
            ("sibling-bundle/a-check-only-the-sibling-measured-is-joined-on-matching-configuration",
             sibling_only_joined),
            ("sibling-bundle/sibling-only-join-refuses-other-plugin-bytes", sibling_only_refuses_other_bytes),
            ("names/bundle-names-reach-the-plan", names_in_plan)]:
        cell(name, fn)

    for name, fn in [
            ("detail-list/only-clashes-between-mods-are-listed", detail_only_inter_mod),
            ("detail-list/set-aside-results-are-in-the-evidence-index-with-why", detail_set_aside_in_index),
            ("detail-list/already-present-pair-is-not-listed", detail_already_present_not_listed),
            ("sibling-bundle/patched-report-attributes-from-the-unpatched-evaluation", sibling_attributes),
            ("prediction-after-patch-tally/validation-counts-per-prediction-item", validation_by_item),
            ("sibling-bundle/a-sibling-without-the-setup-is-refused", sibling_foreign_refused),
            ("sibling-bundle/a-check-measured-differently-stays-unseparated", sibling_disagreement_unseparated),
            ("sibling-bundle/a-check-run-by-another-probe-build-stays-unseparated",
             lambda: sibling_disagreement_unseparated("build")),
            ("sibling-bundle/plan-takes-the-sibling-bundle-option", sibling_cli_flag),
            ("plain-language/the-measured-load-order-takes-its-article", measured_load_order_article),
            ("page-budget/a-shared-list-of-locations-is-printed-once", shared_scope_printed_once),
            ("page-budget/a-long-list-of-locations-is-bounded-and-the-rest-counted", long_location_list_bounded),
            ("plain-language/unmeasured-area-id-never-reaches-the-page", extent_ids_not_on_page),
            ("plain-language/count-once-and-setup-name-in-sentence-case", count_once_setup_lowercase),
            ("plain-language/unmapped-reason-code-never-reaches-the-page", unmapped_code_not_on_page),
            ("none-found-without-coverage/missing-result-names-the-setup-that-lacks-it", missing_names_its_setup),
            ("base-game-in-prediction/scope-line-does-not-name-the-base-game", scope_line_no_base_game),
            ("base-game-in-prediction/already-present-without-the-mod-is-set-aside", already_present_set_aside),
            ("prediction-missing/non-target-mod-partner-is-admitted-and-attributed", mod_partner_admitted),
            ("base-game-in-prediction/a-compared-mods-own-base-game-clash-is-its-own", partner_alone_set_aside),
            ("prediction-missing/four-setup-report-opens-with-prediction-coverage-validation", prediction_leads_four_setup),
            ("prediction-missing/needs-both-single-mod-and-not-separable-attribution", four_setup_attribution),
            ("prediction-missing/each-kind-says-what-it-looks-like-in-game", four_setup_in_game_words),
            ("base-game-in-prediction/set-aside-counted-not-predicted", four_setup_set_aside_not_predicted),
            ("prediction-after-patch-tally/patched-report-leads-with-unpatched-prediction", patched_leads_with_prediction),
            ("prediction-after-patch-tally/validation-hit-open-and-patch-problem-counts", patched_validation_counts),
            ("blind-spot-without-reason/each-blind-spot-says-why", blind_spot_has_why),
            ("none-found-without-coverage/reasons-and-counts-on-the-report-page", none_found_has_coverage),
            ("none-found-without-coverage/html-names-each-reason", none_found_html_coverage),
            ("base-game-in-prediction/base-game-only-difference-is-not-a-predicted-clash", base_game_not_a_prediction),
            ("prediction-missing/no-patch-validation-says-so", no_patch_says_so),
            ("prediction-missing/reel-opening-speaks-the-prediction", reel_opens_with_prediction)]:
        cell(name, fn)

    # C460. A third mod's object broken with one evaluated mod alone is not "needs both" because the pair names differ
    def c460_object_broken_with_one_mod_alone():
        plan, md, html = object_identity_bundle()
        items = (reader(plan).get("prediction") or {}).get("items") or []
        it = next((i for i in items if i.get("checkId") == FIXTURE), {})
        ok = (len(items) == 1 and it.get("attribution") == "single-mod" and it.get("attributionMod") == MOD_A
              and "already in this state with" in it.get("plain", "")
              and f"the same object already so with {MOD_A} alone, between {MOD_B} and {OTHER} 1" in md)
        return ok, f"items {[(i.get('mods'), i.get('attribution'), i.get('attributionMod')) for i in items]}"

    # C461. Single-mod attribution of a lost edit is per field, not per record
    def c461_lost_edit_attributed_per_field():
        plan, md, html = field_identity_bundle()
        rec = {e.get("field"): e.get("attribution") for e in (reader(plan).get("prediction") or {}).get("record") or []
               if e.get("admission") == "admitted"}
        ok = rec == {"Placement": "needs-both", "Scale": "single-mod"}
        return ok, f"attribution by field {rec}"

    # C462. A predicted overlap is resolved by its own pair's predicate with the patch, not by any row on its object
    def c462_overlap_judged_by_pair_predicate():
        plan, md, html = overlap_predicate_bundle()
        val = reader(plan).get("validation") or {}
        ok = val.get("hits") == 1 and val.get("open") == 1 and val.get("patchProblems") == 1
        return ok, f"hits {val.get('hits')}, open {val.get('open')}, new {val.get('patchProblems')}"

    # C463. A lost edit the patch keeps (it carries the winning value) is its own count, printed, never "left"; round 4
    # (ruling a): said as what the record data shows, with no claim that it was chosen
    def c463_patch_kept_winner_is_not_left():
        plan, md, html = record_validation_bundle()
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        # fourth blind review (W1): the sentence says the patch carries the winning value and the change stays lost
        said = ("carries the value that wins without it" in text and "stays lost" in text and "on purpose" not in text
                and "still lost" not in text)
        ok = val.get("kept") == 1 and val.get("open") == 1 and said
        return ok, f"kept {val.get('kept')}, open {val.get('open')}; printed: {said}"

    # C464. A placeholder winner value is never compared; round 4 (ruling f): the edit is classified by which side
    # loses to the patch, as with recorded values (701 ModB's loses: resolved; 703 ModA's loses: kept); cannot tell
    # only when neither side is known (C495)
    def c464_placeholder_value_cannot_tell():
        plan, md, html = placeholder_value_bundle()
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = (val.get("switched") == 1 and val.get("kept") == 1 and val.get("open") == 1 and val.get("cannotTell") == 0
              and "cannot be told" not in text)
        return ok, (f"switched {val.get('switched')}, kept {val.get('kept')}, open {val.get('open')}, "
                    f"cannot tell {val.get('cannotTell')}")

    # C465. A refused comparison prints no repair, and its predicted clashes are "cannot tell" against the patch
    def c465_refused_comparison_counts_nothing():
        plan, md, html = comparison_bundle("refused")
        val = reader(plan).get("validation") or {}
        ok = (val.get("hits") == 0 and val.get("cannotTell") == 2 and "repaired 2" not in md
              and "repaired" not in section_text(md, "Changes made by adding " + PATCH)
              and "rose from 8.33% to 23.47%" in md)
        return ok, (f"hits {val.get('hits')}, cannot tell {val.get('cannotTell')}; 'repaired 2' printed: "
                    f"{'repaired 2' in md}; refusal named: {'rose from 8.33% to 23.47%' in md}")

    # C466. A comparison measured but not accepted says why in both places, never bare "has no accepted result"
    def c466_unaccepted_comparison_one_statement():
        plan, md, html = comparison_bundle("unaccepted")
        ok = ("has no accepted result" not in md.lower() and md.count("could not be measured in both setups") >= 2
              and "1 new problem" in md)
        return ok, (f"bare 'no accepted result' {('has no accepted result' in md.lower())}; qualifier printed "
                    f"{md.count('could not be measured in both setups')} times")

    # C467. Overlaps that left the class with the defect still there are named beside the no-problem line
    def c467_still_holding_named():
        plan, md, html = comparison_bundle("holding")
        val = reader(plan).get("validation") or {}
        ok = "1 of the problems in the setup it was compared with is still physically there" in md
        return ok, f"still-holding sentence printed: {ok}; validation hits {val.get('hits')}, open {val.get('open')}"

    # C468. One evaluated mod: the setup without its partner decides "needs both"; its resources leave with it
    def c468_one_target_uses_setup_without_partner():
        plan, md, html = one_target_bundle()
        p = reader(plan).get("prediction") or {}
        attr = {(e.get("formKey") or "")[:6]: e.get("attribution") for e in p.get("record") or []
                if e.get("admission") == "admitted"}
        ok = (p.get("withoutArm") == "without-a" and attr == {"000G01": "needs-both", "000G02": "single-mod"}
              and f"It needs both {MOD_A} and {OTHER}" in md and "comes from" in md)
        return ok, f"without arm {p.get('withoutArm')}; attribution {attr}"

    def limits():
        plan, md, html = get("limits", limits_bundle)
        return plan, section_text(md, headings(plan).get("coverage") or "\0"), md

    # C469. World states are words and counts, never a printed record, and the context line agrees
    def c469_states_in_words():
        plan, cov, md = limits()
        ok = ("{'" not in md and "references disabled at the start that no switch names (12,916)" in cov
              and "extent unknown" in cov and "Additional states not examined: none named" not in md)
        return ok, f"raw record printed: {'{' + chr(39) in md}; states line: {[x for x in cov.splitlines() if 'World states' in x]}"

    # C470. Each population in its own unit, counted once, never summed with another population
    def c470_populations_counted_once_not_summed():
        plan, cov, md = limits()
        # population names in the skill's plain words since the fourth blind review (B1, C526)
        ok = ("205 exterior cells with landscape" in cov and "391 pairs of neighbouring cells" in cov
              and "1,192" not in cov and "596" not in cov and "the landscape declares no heights" in cov)
        return ok, f"seam line: {[x for x in cov.splitlines() if 'landscape' in x]}"

    # C471. A reason naming another check is "judged by" that check's title, never a check id or "could not judge"
    def c471_judged_elsewhere_named_by_title():
        plan, cov, md = limits()
        line = [x for x in cov.splitlines() if "navmesh geometry" in x]
        ok = (len(line) == 1 and "Judged by another check" in line[0] and "geometry.navmesh-ownership" not in md)
        return ok, f"line {line}"

    # C472. One check missing for two reasons on the same setups is one line
    def c472_one_line_per_check_reasons():
        plan, cov, md = limits()
        title = (priorities()["reader"]["checkTitles"]).get(WATER, WATER)
        lines = [x for x in cov.splitlines() if title in x and x.startswith("- **No result")]
        ok = len(lines) == 1 and "could not decide" in lines[0] and "cannot yet be measured" in lines[0]
        return ok, f"lines naming the check: {lines}"

    # C473. Statuses in words: a check that failed to run, a check with no pair list
    def c473_statuses_in_words():
        plan, cov, md = limits()
        ok = "failed to run" in cov and "no list of patch pairs was supplied" in cov
        return ok, f"failed-to-run worded: {'failed to run' in cov}; no-pair-source worded: {'no list of patch pairs' in cov}"

    # C474. No named reason code reaches the page as "a reason this report does not describe in words"
    def c474_named_codes_in_words():
        plan, cov, md = limits()
        ok = "does not describe in words" not in cov
        return ok, f"generic phrase on the page: {not ok}"

    # C475. The patch section says per item what the patch does, and names and links the new clashes
    def c475_per_item_outcomes_and_new_clashes_linked():
        plan, md, html = overlap_predicate_bundle()
        val = reader(plan).get("validation") or {}
        new = val.get("newByCheck") or []
        ok = (any("resolved 1, left 1" in x for x in val.get("itemLines") or [])
              and len(new) == 1 and new[0].get("count") == 1 and new[0].get("findingIds")
              and f"(evidence-index.md#{new[0]['findingIds'][0]})" in section_text(md, headings(plan).get("validation") or "\0"))
        return ok, f"item lines {val.get('itemLines')}; new by check {new}"

    # C476. Overwrite history the patch removes says how many rows held a measured loss
    def c476_history_with_measured_loss_named():
        plan, md, html = history_bundle()
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = "1 of which held a measured loss" in text and "removes" not in text   # W3: never "the patch removes"
        return ok, f"history line printed: {ok}"

    # C477. A record item says which way the edit is lost and gives example records
    def c477_direction_and_examples():
        plan, md, html, index = record_rows_bundle()
        ok = (f"In every one, {MOD_A}'s value is lost to {MOD_B}'s." in md
              and f"For example: 000701:{BASE} (LocationReference)" in md)
        return ok, f"direction printed: {MOD_A + chr(39) + 's value is lost' in md}; example printed: {'For example' in md}"

    # C478. A lost edit the patch wins is described as the patch's value winning in the detail list; round 4 (ruling
    # f): it points to the patch comparison, never "not a problem" (C498)
    def c478_patch_win_is_its_choice():
        plan, md, html = record_validation_bundle()
        # the concise page counts a setup's patch-won lost edits in one line (P53); the plan words each one
        f = next((x for x in plan.get("findings") or [] if x.get("readerPatchChoice")), {})
        ok = (f"where {PATCH}'s value wins over {MOD_B}'s; what this means" in (f.get("readerPlain") or "")
              and "where a declared patch's value wins; what this means" in md)
        return ok, f"finding wording {f.get('readerPlain')!r}"

    # C479. Patches are named in load order, not alphabetically
    def c479_patches_in_load_order():
        plan, md, html = two_patch_bundle()
        title = reader(plan).get("title") or ""
        ok = "with Zeta patch.esp, Alpha patch.esp" in title
        return ok, f"title {title!r}"

    # C480. Concise: a setup's several pair groups in one check are one line, each pair with its count and link
    def c480_setup_pairs_one_line():
        plan, md, html = busy_third_mod_bundle()
        sec = section_text(md, "What the checks found")
        lines = [x for x in sec.splitlines() if x.startswith("- **Measured load order:**")]
        ok = len(lines) == 1 and lines[0].count("(evidence-index.md#F-") == 2
        return ok, f"measured-load-order lines {lines}"

    for name, fn in [("w2-review/C480-concise-setup-pairs-one-line", c480_setup_pairs_one_line),
                     ("w2-review/C475-per-item-outcomes-and-new-clashes-linked", c475_per_item_outcomes_and_new_clashes_linked),
                     ("w2-review/C476-history-with-measured-loss-named", c476_history_with_measured_loss_named),
                     ("w2-review/C477-record-item-direction-and-examples", c477_direction_and_examples),
                     ("w2-review/C478-patch-win-is-its-own-choice-in-detail", c478_patch_win_is_its_choice),
                     ("w2-review/C479-patches-in-load-order", c479_patches_in_load_order),
                     ("w2-review/C469-world-states-in-words-not-records", c469_states_in_words),
                     ("w2-review/C470-populations-counted-once-not-summed", c470_populations_counted_once_not_summed),
                     ("w2-review/C471-judged-elsewhere-named-by-title", c471_judged_elsewhere_named_by_title),
                     ("w2-review/C472-one-line-per-check-and-its-reasons", c472_one_line_per_check_reasons),
                     ("w2-review/C473-statuses-in-words", c473_statuses_in_words),
                     ("w2-review/C474-named-codes-in-words", c474_named_codes_in_words),
                     ("w2-review/C468-one-target-needs-both-from-setup-without-partner",
                      c468_one_target_uses_setup_without_partner),
                     ("w2-review/C465-refused-comparison-prints-no-repair-and-cannot-tell",
                      c465_refused_comparison_counts_nothing),
                     ("w2-review/C466-unaccepted-comparison-says-why-everywhere", c466_unaccepted_comparison_one_statement),
                     ("w2-review/C467-overlaps-still-holding-named-beside-no-problem", c467_still_holding_named),
                     ("w2-review/C463-patch-kept-winner-is-its-own-count-not-left", c463_patch_kept_winner_is_not_left),
                     ("w2-review/C464-placeholder-winner-value-is-cannot-tell", c464_placeholder_value_cannot_tell),
                     ("w2-review/C460-third-mod-object-broken-with-one-mod-alone-is-single-mod",
                      c460_object_broken_with_one_mod_alone),
                     ("w2-review/C461-lost-edit-attributed-per-field", c461_lost_edit_attributed_per_field),
                     ("w2-review/C462-overlap-judged-by-its-pair-predicate-with-the-patch",
                      c462_overlap_judged_by_pair_predicate)]:
        cell(name, fn)

    # ---- window-2 review, round 3 (blind review of the regenerated reports)

    # C481. An item's place counts and "All are in" come from its own listed rows: every row's place is counted,
    # a place the probe names by form key included, and a navmesh row's triangle note is not part of its place
    def c481_place_counts_from_listed_rows():
        places = ["CityExterior02 (ext)", "CityExterior02 (ext)", "CityExterior02 (ext)", "CityExterior01 (ext)",
                  "CityExterior01 (ext)", f"008EC2:{BASE} (ext)", f"008E63:{BASE} (ext)", "CityDocks (ext)"]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, f"0008{i:02X}:{MOD_A}", label=pl, other=f"0009{i:02X}:{MOD_B}")
                  for i, pl in enumerate(places)]
        nav_places = ["CityExterior03 (ext)", "CityExterior03 (ext)", f"008EC2:{BASE} (ext)"]
        n_rows = [row("EDGE-IDX", MOD_B, MOD_A, f"00A0{i:02X}:{BASE}", label=pl) for i, pl in enumerate(nav_places)]
        n_rows.append(row("EDGE-IDX", MOD_B, MOD_A, f"00A0FF:{BASE}", label="CityExterior03 (ext)  689 tris -> 5"))
        nav_places.append("CityExterior03 (ext)")
        results = {(OVERLAP, "baseline"): p_rows, (OVERLAP, "dependency-only"): [], (OVERLAP, "plus-a"): [],
                   (OVERLAP, "plus-b"): [], (NAVOWN, "baseline"): n_rows, (NAVOWN, "dependency-only"): [],
                   (NAVOWN, "plus-a"): [], (NAVOWN, "plus-b"): []}
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        items = {i["checkId"]: i["plain"] for i in (reader(plan).get("prediction") or {}).get("items") or []}
        ov, nv = items.get(OVERLAP, ""), items.get(NAVOWN, "")
        want_more = len(set(places)) - 2           # the two most common places are named, the rest counted
        ok = (f"and {want_more} more places" in ov and "All are in" not in nv
              and f"008EC2:{BASE} (ext)" in nv and "tris" not in nv.split("Most are in")[-1])
        return ok, f"overlap item {ov!r}; navmesh item {nv!r}"

    # C482. verify does not say ok while the printed report is over its page limit
    def c482_verify_refuses_over_limit():
        import types
        plan = record_rows_bundle()[0]
        R.stamp(plan)
        with tempfile.TemporaryDirectory() as td:
            out = Path(td)
            R.write_json(out / "presentation.json", plan)
            pdf = (b"%PDF-1.4\n1 0 obj << /Type /Pages /Count 9 >> endobj\n"
                   + b"".join(f"{i + 2} 0 obj << /Type /Page >> endobj\n".encode() for i in range(9)))
            (out / "report.pdf").write_bytes(pdf)
            R.write_json(out / "presentation-manifest.json", {
                "schemaVersion": 1, "presentationId": plan.get("presentationId"), "artifacts": [], "media": [],
                "supportingEvidence": [], "print": {"status": "over-limit", "pages": 9, "maxPages": 8}})
            code = R.cmd_verify(types.SimpleNamespace(out=str(out)))
            res = json.loads((out / "presentation-verify.json").read_text(encoding="utf-8"))
        kinds = [d.get("kind") for d in res.get("defects") or []]
        ok = res.get("ok") is False and "over-limit" in kinds and code != 0
        return ok, f"exit {code}; ok {res.get('ok')}; defects {kinds}"

    # C483. A light whose base record has no model is not a physical clash, in the prediction and with the patch; a
    # light with a model is (decided from the record's model field, read by the names enrichment, never a name)
    def c483_light_without_model_is_not_a_clash():
        def light_row(fk, host):
            return row("CONTACT", MOD_B, MOD_A, fk, other=host)
        base = [light_row(f"000L01:{MOD_A}", f"000H01:{MOD_B}"), light_row(f"000L02:{MOD_A}", f"000H02:{MOD_B}"),
                light_row(f"000S01:{MOD_A}", f"000H03:{MOD_B}")]
        new = [light_row(f"000L03:{MOD_A}", f"000H04:{MOD_B}"), light_row(f"000S02:{MOD_A}", f"000H05:{MOD_B}")]
        refs = {f"000L01:{MOD_A}": {"base": f"0B0L01:{MOD_A}"}, f"000L03:{MOD_A}": {"base": f"0B0L01:{MOD_A}"},
                f"000L02:{MOD_A}": {"base": f"0B0L02:{MOD_A}"}, f"000S01:{MOD_A}": {"base": f"0B0S01:{MOD_A}"},
                f"000S02:{MOD_A}": {"base": f"0B0S01:{MOD_A}"},
                **{f"000H0{i}:{MOD_B}": {"base": f"0B0S01:{MOD_A}"} for i in range(1, 6)}}
        bases = {f"0B0L01:{MOD_A}": {"type": "Light", "model": ""},
                 f"0B0L02:{MOD_A}": {"type": "Light", "model": "lights\\lantern.nif"},
                 f"0B0S01:{MOD_A}": {"type": "Static", "model": "rock.nif"}}
        results = {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): base + new}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            names={"refs": refs, "bases": bases})
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission")) for e in p.get("record") or [])
        # (2026-10-25, station AD) the sentence names the types with no body, not "any record with no model"
        ok = (adm == [("000L01", "noBody"), ("000L02", "admitted"), ("000S01", "admitted")]
              and val.get("patchProblems") == 1 and "Also not counted: 1 clashes between the mods whose object is a light, activator or door whose record has no model" in md
              and "Not counted as new clashes" in md)
        return ok, f"admission {adm}; new clashes {val.get('patchProblems')}"

    # C484. Overlaps the patch leaves (still intersecting) are split by the record data: still reported between two
    # mods; no longer reported, a patch's version of one object loading last; no longer reported, not attributed
    def c484_left_overlaps_split_by_record_data():
        X, Z = f"000X01:{MOD_A}", f"000Z01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2, 3)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys] + [
            row("OVERLAP", MOD_B, MOD_A, Z, other=f"000W01:{MOD_B}")]
        w_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=ys[0])]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        preds.append({"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": Z, "Other": f"000W01:{MOD_B}",
                      "Physical": "Gone"})
        refs = {ys[1]: {"carriedBy": [MOD_B, PATCH]}, ys[2]: {"carriedBy": [MOD_B]}, X: {"carriedBy": [MOD_A]}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows,
                                                               (OVERLAP, "with-patch"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                            names={"refs": refs})
        val = reader(plan).get("validation") or {}
        bi = (val.get("byItem") or [{}])[0]
        line = next((x for x in val.get("itemLines") or [] if "Objects" in x or OVERLAP in x), "")
        # (third review, Clockwork S1) the pair a patch's version holds is the patch's placement, said apart, not left
        ok = (bi.get("open") == 2 and bi.get("openListed") == 1 and bi.get("placed") == 1
              and bi.get("openNotAttributed") == 1 and "1 still intersect, not attributed" in line
              and "1 still intersecting where a patch places one of them" in line
              # the page says an item line that repeats its kind line at the top once, at the top (last round)
              and "1 still reported as a clash between two mods" in line and line.split("): ", 1)[-1] in md)
        return ok, f"by item {bi}; line {line!r}"

    # C485. The navmesh one evaluated mod's navmesh replaces with a measured loss of ground is stated in the prediction
    # section as a measured fact, with the largest losses, and tied to the broken links that point into it; no claim
    # about NPCs
    def c485_navmesh_loss_stated_in_prediction():
        n1, n2 = f"000N01:{BASE}", f"000N02:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior02 (ext)  689 tris -> 5"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityHouse (int)  1 tris -> 165"),
                  row("EDGE-IDX", MOD_A, MOD_B, f"000E01:{BASE}", label="CityExterior03 (ext)", other=n1),
                  row("EDGE-IDX", MOD_A, MOD_B, f"000E02:{BASE}", label="CityExterior03 (ext)", other=n1)]
        plan, md, html = compile_and_render(PATCHED_ARMS, {(NAVOWN, "without-patch"): p_rows, (NAVOWN, "baseline"): []},
                                            [MOD_A, MOD_B], patches=[PATCH])
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        # (fourth review, Clockwork S4) the headline now says the replacement too: the measured line is the one read here
        line = next((x for x in sec.splitlines() if "navmesh replaces" in x and not x.startswith(">")), "")
        ok = (f"without the patch, {MOD_B}'s navmesh replaces {MOD_A}'s in 2 navmesh records, and in 2 of them" in line
              and "CityExterior02 (ext) 689 -> 5 triangles" in line
              and f"2 of the broken navmesh links counted above point to triangles of a navmesh this replacement cut "
                  f"down: {n1} (689 -> 5 triangles)" in line
              and "NPC" not in line and "navmesh replaces" in html)
        return ok, f"line {line!r}"

    # C486. One evaluated mod: the setup that decided "needs both" for a partner mod is named in the record
    def c486_setup_without_partner_named():
        plan, md, html = one_target_bundle()
        p = reader(plan).get("prediction") or {}
        ok = p.get("withoutPartnerArms") == {OTHER: "without-other"}
        return ok, f"without-partner setups {p.get('withoutPartnerArms')}"

    # C487. An object two by-object checks both name (a fixture hanging that is also left without support) is counted
    # once in the headline; the second check counts its other objects and says how many it shares
    def c487_headline_counts_each_object_once():
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2)]
        ct = [row("CONTACT", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 3)]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("dependency-only", "plus-a", "plus-b")}
        results.update({(FIXTURE, "baseline"): fx, (CONTACT, "baseline"): ct})
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        ok = ("3 cases of objects left floating" in v and "the same 2 objects also found by the check "
              f"\"{priorities()['reader']['checkTitles'].get(FIXTURE)}\" (counted once, above, under " in v)   # round 2 item 4
        return ok, f"verdict {v!r}"

    # C488. A lost edit whose two values name the same file, differing only in letter case or slashes, is not a lost
    # edit
    def c488_path_case_only_is_not_lost():
        rows_ = [lost_row(MOD_A, MOD_B, f"000701:{BASE}", "Texture", "data\\textures\\a\\rock01.dds",
                          "Data\\Textures\\A\\Rock01.dds", "x"),
                 lost_row(MOD_A, MOD_B, f"000702:{BASE}", "Texture", "data\\textures\\a\\rock01.dds",
                          "Data\\Textures\\A\\Rock02.dds", "x")]
        enr = {(RECORDS, a): (rows_ if a == "baseline" else []) for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr)
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission"))
                     for e in (reader(plan).get("prediction") or {}).get("record") or [])
        ok = (adm == [("000701", "pathCase"), ("000702", "admitted")]
              and "1 record fields whose two values name the same file" in md)
        return ok, f"admission {adm}"

    # C489. A check whose rows are still physically there is never listed under "found no problem"
    def c489_still_holding_not_no_problem():
        plan, md, html = comparison_bundle("holding")
        sec = section_text(md, "Checks that found no problem")
        title = (priorities()["reader"]["checkTitles"]).get(OVERLAP, OVERLAP)
        ok = (title not in sec and "1 of the problems in the setup it was compared with is still physically there" in md)
        return ok, f"no-problem section {sec.strip()[:300]!r}"

    # C490. The concise page says once what every shown item repeats (the shared attribution, a missing in-game
    # description); the plan keeps each item's full sentence
    def c490_concise_page_says_repeats_once():
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2)]
        ct = [row("CONTACT", MOD_B, MOD_A, f"000C0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 3)]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("dependency-only", "plus-a", "plus-b")}
        results.update({(FIXTURE, "baseline"): fx, (CONTACT, "baseline"): ct})
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        nb = priorities()["reader"]["prediction"]["attribution"]["needs-both"]
        items = (reader(plan).get("prediction") or {}).get("items") or []
        ok = (sec.count(priorities()["concise"]["onceNeedsBoth"]) == 1 and nb not in sec
              and all(nb in i.get("plain", "") for i in items) and len(items) == 2)
        return ok, f"once line printed {sec.count(priorities()['concise']['onceNeedsBoth'])}; per-item left {nb in sec}"

    # C491. A record item's first example is a field skill data prefers (a placed object's position), not whatever
    # record sorts first
    def c491_examples_prefer_placement():
        rows_ = [lost_row(MOD_A, MOD_B, f"00003C:{BASE}", "HdLodDiffuseTexture", "a", "(the winner's value)", "x"),
                 lost_row(MOD_A, MOD_B, f"000702:{BASE}", "Placement", "1,2,3", "(the winner's value)", "x")]
        enr = {(RECORDS, a): (rows_ if a == "baseline" else []) for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr)
        # after-review (Winterhold review 5): the field in plain words; the preference is unchanged
        ok = f"For example: 000702:{BASE} (position); 00003C:{BASE} (HdLodDiffuseTexture)" in md
        return ok, f"examples line: {[x for x in md.splitlines() if 'For example' in x][:1]}"

    def body_rows():
        base = [row("CONTACT", MOD_B, MOD_A, f"000L0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 3)]
        return {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): base + [
            row("CONTACT", MOD_B, MOD_A, f"000L09:{MOD_A}", other=f"000H09:{MOD_B}")]}

    # C492. With no names enrichment the base records were not read: the report says it cannot tell whether the
    # geometry clashes it counts have a body, with their count, never silently
    def c492_no_enrichment_says_body_not_read():
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, body_rows(), [MOD_A, MOD_B], patches=[PATCH])
        cov = section_text(md, headings(plan).get("coverage") or "\0")
        line = [x for x in cov.splitlines() if "Cannot tell whether the object has a body" in x]
        val = reader(plan).get("validation") or {}
        ok = (len(line) == 1 and "** 3" in line[0] and "no names enrichment" in line[0]
              and any("cannot be told: the base records were not read" in x for x in val.get("sentences") or []))
        return ok, f"coverage line {line}"

    # C493. With the enrichment, a clash whose object's base record it did not read is "cannot tell" and not counted
    def c493_base_not_read_is_cannot_tell():
        refs = {f"000L0{i}:{MOD_A}": {"base": f"0B0S01:{MOD_A}"} for i in (1, 2)}
        refs.update({f"000H0{i}:{MOD_B}": {"base": f"0B0S01:{MOD_A}"} for i in (1, 2, 3)})
        bases = {f"0B0S01:{MOD_A}": {"type": "Static", "model": "rock.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, body_rows(), [MOD_A, MOD_B], patches=[PATCH],
                                            names={"refs": refs, "bases": bases})
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission"))
                     for e in (reader(plan).get("prediction") or {}).get("record") or [])
        ok = (adm == [("000L01", "admitted"), ("000L02", "admitted"), ("000L03", "bodyUnread")]
              and "Cannot tell, so not counted: 1 clashes whose object's base record was not read" in md
              and "Cannot tell whether the object has a body" not in md)
        return ok, f"admission {adm}"

    for name, fn in [("w2-round3/C481-place-counts-from-listed-rows", c481_place_counts_from_listed_rows),
                     ("w2-round3/C492-no-enrichment-says-body-not-read", c492_no_enrichment_says_body_not_read),
                     ("w2-round3/C493-base-not-read-is-cannot-tell", c493_base_not_read_is_cannot_tell),
                     ("w2-round3/C491-examples-prefer-placement", c491_examples_prefer_placement),
                     ("w2-round3/C490-concise-page-says-repeats-once", c490_concise_page_says_repeats_once),
                     ("w2-round3/C486-setup-without-partner-named", c486_setup_without_partner_named),
                     ("w2-round3/C487-headline-counts-each-object-once", c487_headline_counts_each_object_once),
                     ("w2-round3/C488-path-case-only-is-not-a-lost-edit", c488_path_case_only_is_not_lost),
                     ("w2-round3/C489-still-holding-is-not-no-problem", c489_still_holding_not_no_problem),
                     ("w2-round3/C485-navmesh-loss-stated-in-prediction", c485_navmesh_loss_stated_in_prediction),
                     ("w2-round3/C483-light-without-model-is-not-a-clash", c483_light_without_model_is_not_a_clash),
                     ("w2-round3/C484-left-overlaps-split-by-record-data", c484_left_overlaps_split_by_record_data),
                     ("w2-round3/C482-verify-refuses-over-limit", c482_verify_refuses_over_limit)]:
        cell(name, fn)

    # ------------------------------------------- round 4: the second blind review of the regenerated reports
    def no_model_bundle():
        """C494. Without the patch ModA's static S1 and its activator T1 each lost their support; with the patch S3 and
        T3 did. The activator's base record has no model (read as absent): it has no body. The comparison of the setup
        without the patch with the measured order counts both of its rows as created."""
        S1, T1, S3, T3 = (f"000{x}:{MOD_A}" for x in ("S01", "T01", "S03", "T03"))
        hosts = [f"000H0{i}:{MOD_B}" for i in range(1, 5)]
        p_rows = [row("CONTACT", MOD_B, MOD_A, S1, other=hosts[0]), row("CONTACT", MOD_B, MOD_A, T1, other=hosts[1])]
        w_rows = [row("CONTACT", MOD_B, MOD_A, S3, other=hosts[2]), row("CONTACT", MOD_B, MOD_A, T3, other=hosts[3])]
        refs = {S1: {"base": f"0B0S01:{MOD_A}"}, S3: {"base": f"0B0S01:{MOD_A}"}, T1: {"base": f"0B0T01:{MOD_A}"},
                T3: {"base": f"0B0T01:{MOD_A}"}, **{h: {"base": f"0B0S01:{MOD_A}"} for h in hosts}}
        bases = {f"0B0S01:{MOD_A}": {"type": "Static", "model": "rock.nif"},
                 f"0B0T01:{MOD_A}": {"type": "Activator", "model": ""}}
        order = {"without-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                        created=2, newDefect=2, attributed=0, unmeasured=0)}}
        log = f"NEW     CONTACT    {MOD_B}  x  {MOD_A}   +2 record(s): created 2\n"
        return compile_and_render(PATCHED_ARMS, {(CONTACT, "without-patch"): p_rows, (CONTACT, "baseline"): w_rows},
                                  [MOD_A, MOD_B], patches=[PATCH], names={"refs": refs, "bases": bases}, order=order,
                                  diff_logs={(CONTACT, "without-patch"): log})

    # C494. An object whose base record has no model has no body, whatever its type: no section that counts objects
    # counts it (the prediction, the new clashes with the patch, the comparison's new problems, the detail list)
    def c494_no_model_counted_nowhere():
        plan, md, html = no_model_bundle()
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission")) for e in p.get("record") or [])
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        found = section_text(md, "What the checks found")
        bad = [x for x in md.splitlines() if re.search(r"\b2 (new problems|records|clashes|cases)\b", x)]
        ok = (adm == [("000S01", "admitted"), ("000T01", "noBody")]
              and val.get("patchProblems") == 1 and (reg.get("counts") or {}).get("readerCreated") == 1
              and "introduces 1 new problem in the check" in str((reader(plan).get("validation") or {}).get("headline", "")) and "introduced 1 new problem in the" in md
              and found.count("1 record shows") == 2 and not bad)
        return ok, (f"admission {adm}; new with patch {val.get('patchProblems')}; comparison "
                    f"{(reg.get('counts') or {}).get('readerCreated')}; lines still counting 2: {bad}")

    def lost_classes_bundle():
        """C495. Without the patch ModA's change to six records is lost to ModB; every winner value is a placeholder.
        With the patch: 701 ModB's value loses to the patch (the patch carries ModA's); 702 both lose to the patch (its
        own value); 703 ModA's loses to the patch (it carries ModB's); 704 ModA's still loses to ModB; 705 no change is
        lost; 706 only a third plugin loses to the patch: ModA's change is not lost to it, so the patch carries ModA's
        value (by the record lane's rule a change still lost has a row), resolved (Clockwork review 4, P101: three
        OcclusionData rows of this shape carry Grand Solitude's value; until lane cw this was "cannot tell")."""
        ph = "(the winner's value)"
        p_rows = [lost_row(MOD_A, MOD_B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", ph, "v") for i in range(1, 7)]
        w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", ph, "v"),
                  lost_row(MOD_A, PATCH, f"000702:{BASE}", "LocationReference", "a2", ph, "v"),
                  lost_row(MOD_B, PATCH, f"000702:{BASE}", "LocationReference", "b2", ph, "v"),
                  lost_row(MOD_A, PATCH, f"000703:{BASE}", "LocationReference", "a3", ph, "v"),
                  lost_row(MOD_A, MOD_B, f"000704:{BASE}", "LocationReference", "a4", ph, "v"),
                  lost_row(OTHER, PATCH, f"000706:{BASE}", "LocationReference", "o6", ph, "v")]
        enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): w_rows}
        count_only = {(RECORDS, "without-patch"): 6, (RECORDS, "baseline"): 6}
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        return compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], count_only=count_only,
                                  enrichment=enr)

    # C495. A lost edit is classified by which side loses to the patch, the same way whether or not values are
    # recorded: the patch carries the lost value (resolved), its own value (the change is still lost, never
    # "resolved"), the winning value (no claim about intent); neither side lost to the patch is resolved (lane cw, P101)
    def c495_lost_edit_classes_by_losing_side():
        plan, md, html = lost_classes_bundle()
        val = reader(plan).get("validation") or {}
        bi = (val.get("byItem") or [{}])[0]
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = (bi.get("hits") == 2 and bi.get("switched") == 1 and bi.get("own") == 1 and bi.get("kept") == 1
              and bi.get("open") == 1
              and bi.get("cannotTell") == 0 and val.get("own") == 1
              and "sets its own value" in text and "still lost" not in md
              and "on purpose" not in md and "author's" not in md and "own choice" not in md)
        return ok, f"by item {bi}; printed own value {'own value' in text}; intent words {'on purpose' in md}"

    # C496. "Left untouched" never counts a clash a patch edited: the objects still intersect with a patch's version
    # of one of them loaded last is still present, but not untouched
    def c496_untouched_excludes_patch_edited():
        X, Z = f"000X01:{MOD_A}", f"000Z01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2, 3)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys] + [
            row("OVERLAP", MOD_B, MOD_A, Z, other=f"000W01:{MOD_B}")]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        preds.append({"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": Z, "Other": f"000W01:{MOD_B}",
                      "Physical": "Gone"})
        refs = {ys[1]: {"carriedBy": [MOD_B, PATCH]}, ys[2]: {"carriedBy": [MOD_B]}, X: {"carriedBy": [MOD_A]}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): [
            row("OVERLAP", MOD_B, MOD_A, X, other=ys[0])]}, [MOD_A, MOD_B], patches=[PATCH],
            predicates={(OVERLAP, "with-patch"): preds}, names={"refs": refs})
        text = section_text(md, headings(plan).get("validation") or "\0")
        # (third review, Clockwork S1) the clash a patch edited is no longer in "still present": it is the patch's
        # placement, its own sentence; still present counts the 2 no patch places
        line = next((x for x in text.splitlines() if "Predicted and still present" in x), "")
        placed_ = next((x for x in text.splitlines() if "where a patch places one of the two objects" in x), "")
        ok = (re.search(r"Predicted and still present with [^:]*: 2\b", line) is not None and "untouched" not in line
              and "they still intersect there: 1" in placed_
              and not re.search(r"left untouched by [^:]*: 3\b", text))
        return ok, f"line {line!r}"

    # C497. The headline calls overlaps candidates, never inconsistencies; and the scope line says when clashes with a
    # third mod are counted because they appear only with both evaluated mods
    def c497_headline_candidates_and_scope():
        p_rows = [row("OVERLAP", MOD_B, MOD_A, f"000X01:{MOD_A}", other=f"000Y01:{MOD_B}"),
                  row("OVERLAP", OTHER, MOD_A, f"000X02:{MOD_A}", other=f"000Y02:{OTHER}")]
        results = {(OVERLAP, a): [] for a in ("dependency-only", "plus-a", "plus-b")}
        results[(OVERLAP, "baseline")] = p_rows
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        v, scope = reader(plan).get("verdict") or "", reader(plan).get("scopeLine") or ""
        ok = ("overlap candidate" in v and "inconsistenc" not in v and OTHER in scope and "counted" in scope
              and scope in md)
        return ok, f"verdict {v!r}; scope {scope!r}"

    # C498. Lost edits a patch wins are not called "not problems they leave", which the patch comparison contradicts;
    # and a refused comparison is said in the patch section's summary, not only further down
    def c498_patch_wins_and_refusal_in_summary():
        plan, md, html = record_validation_bundle()
        f = next((x for x in plan.get("findings") or [] if x.get("readerPatchChoice")), {})
        words_ok = ("not a problem" not in (f.get("readerPlain") or "") and "not problems" not in md
                    and "own choice" not in md and "patch's value wins" in md)
        plan2, md2, html2 = comparison_bundle("refused")
        summary = section_text(md2, headings(plan2).get("validation") or "\0").strip().split("\n\n")[0]
        ok = words_ok and "refused a verdict" in summary and "rose from 8.33% to 23.47%" in summary
        return ok, f"patch-win words ok {words_ok}; summary {summary[:300]!r}"

    # ------------------------------------------- round 5: the third blind review (claims against every setup's rows)
    # C499. A geometry clash is attributed from each single-mod setup's own re-measure of it, not only from that setup's
    # pair listing: X2 still Holds with ModA alone (listed nowhere there, because its partner is a base-game object at
    # its own position), so it is already there with ModA alone, not "needs both", and the patch is not credited with it
    def c499_attribution_from_single_mod_remeasure():
        X1, X2 = f"000X01:{MOD_A}", f"000X02:{MOD_A}"
        Y1, Y2 = f"000Y01:{MOD_B}", f"000Y02:{BASE}"
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X1, other=Y1), row("OVERLAP", MOD_B, MOD_A, X2, other=Y2)]

        def preds(p1, p2):
            return [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X1, "Other": Y1, "Physical": p1},
                    {"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X2, "Other": Y2, "Physical": p2}]
        arms = four_arms() + [arm("with-patch", [BASE, OTHER, MOD_A, MOD_B, PATCH])]
        results = {(OVERLAP, a): [] for a in ("dependency-only", "plus-a", "plus-b", "with-patch")}
        results[(OVERLAP, "baseline")] = p_rows
        plan, md, html = compile_and_render(arms, results, [MOD_A, MOD_B], patches=[PATCH], predicates={
            (OVERLAP, "plus-a"): preds("Unmeasured", "Holds"), (OVERLAP, "plus-b"): preds("Unmeasured", "Unmeasured"),
            (OVERLAP, "dependency-only"): preds("Unmeasured", "Unmeasured"), (OVERLAP, "with-patch"): preds("Gone", "Gone")})
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        attr = {(e.get("formKey") or "")[:6]: e.get("attribution") for e in p.get("record") or []
                if e.get("admission") == "admitted"}
        ok = (attr == {"000X01": "needs-both", "000X02": "single-mod"} and val.get("hits") == 1
              and "1 overlap candidate" in (p.get("verdict") or ""))
        return ok, f"attribution {attr}; credited to the patch {val.get('hits')}; verdict {p.get('verdict')!r}"

    # C500. A navmesh comparison counts navmesh records, the prediction broken links: the comparison says its unit
    def c500_navmesh_comparison_names_its_unit():
        n1 = f"000N01:{BASE}"
        # (round 3, Clockwork re-review B1) the three rows are of one navmesh record (three links to one target), so the
        # comparison, which counts distinct records from its rows, says 1; until round 3 the rows were three records and
        # the 1 came from the probe's group count
        p_rows = [row("EDGE-IDX", MOD_A, MOD_B, f"000E01:{BASE}", label="CityExterior03 (ext)", other=n1) for i in (1, 2, 3)]
        for i_, r_ in enumerate(p_rows):
            r_["Summary"] = (f"navmesh 000E01:{BASE} in CityExterior03 (ext) edge-links {n1} triangle {20 + i_} of 10   "
                             f"[record provided by: {BASE} -> {MOD_A}]")
        order = {"without-patch": {"navownprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                       created=1, newDefect=1, attributed=0, unmeasured=0)}}
        log = f"NEW     EDGE-IDX    {MOD_A}  x  {MOD_B}   +1 record(s): created 1\n"
        plan, md, html = compile_and_render(PATCHED_ARMS, {(NAVOWN, "without-patch"): p_rows, (NAVOWN, "baseline"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            diff_logs={(NAVOWN, "without-patch"): log})
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        ok = ("1 navmesh record with new broken links" in (reg.get("readerPlain") or "") and "3 broken navmesh links" in v
              and "1 navmesh record with new broken links" in md)
        return ok, f"comparison {reg.get('readerPlain')!r}; verdict {v!r}"

    # C501. An object two by-object checks name is counted once, and the second check's headline number is still its
    # own total, with how many of them are already counted (never a smaller number than its bullet)
    def c501_shared_objects_keep_their_total():
        ct = [row("CONTACT", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 3, 4)]
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 5)]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("dependency-only", "plus-a", "plus-b")}
        results.update({(FIXTURE, "baseline"): fx, (CONTACT, "baseline"): ct})
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        title = priorities()["reader"]["checkTitles"].get(FIXTURE)
        # (Snazzy re-review on regen-3) the same total and shared count, the shared objects now named
        ok = (f"3 inconsistencies found by the check \"{title}\" (sharing 2 objects already counted above: " in v)
        return ok, f"verdict {v!r}"

    # C502. A fixture whose nearest floor or wall's box reaches it (0u) although the contact walk finds no touch is not
    # counted as hanging: cannot tell, said with its count
    def c502_fixture_at_zero_is_cannot_tell():
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2)]
        fx[0]["Summary"] = "LeftInEmptySpace: Static Pelt; its assembly of 1 touches no floor or wall now (nearest floor or wall 0u)"
        fx[1]["Summary"] = "LeftInEmptySpace: Static Rack; its assembly of 1 touches no floor or wall now (nearest floor or wall 27u)"
        results = {(FIXTURE, a): [] for a in ("dependency-only", "plus-a", "plus-b")}
        results[(FIXTURE, "baseline")] = fx
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission"))
                     for e in (reader(plan).get("prediction") or {}).get("record") or [])
        ok = (adm == [("000F01", "anchorUnclear"), ("000F02", "admitted")]
              and "Cannot tell, so not counted: 1 fixtures whose nearest floor or wall" in md)
        return ok, f"admission {adm}"

    # C503. A lost edit on an object both mods disable (moved below the world) has no effect in game and is not counted;
    # an item whose winning value disables the object the losing mod's value leaves in place says so
    def c503_disables_said_and_both_disabled_set_aside():
        rows_ = [lost_row(MOD_A, MOD_B, f"000801:{BASE}", "Placement", "Position=1,2,-30000; Rotation=0,0,0",
                          "Position=5,6,-30000; Rotation=0,0,0", "Position=1,2,3; Rotation=0,0,0"),
                 lost_row(MOD_A, MOD_B, f"000801:{BASE}", "EnableParent", "Reference=000014:Skyrim.esm; Unknown=1",
                          "Reference=000014:Skyrim.esm; Unknown=0", "x"),
                 # (window 2, lane core: the overridden value differs from the lost one, as in every real row; the
                 # fixture had them equal, which says ModA made no change there)
                 lost_row(MOD_A, MOD_B, f"000802:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                          "Position=1,2,-30000; Rotation=0,0,0", "Position=9,9,3; Rotation=0,0,0")]
        enr = {(RECORDS, a): (rows_ if a == "baseline" else []) for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr)
        p = reader(plan).get("prediction") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("field"), e.get("admission")) for e in p.get("record") or [])
        item = next((i for i in p.get("items") or [] if i["checkId"] == RECORDS), {})
        ok = (adm == [("000801", "EnableParent", "bothDisabled"), ("000801", "Placement", "bothDisabled"),
                      ("000802", "Placement", "admitted")]
              # blind review (0A0315): said as off for good
              and "sets the object off for good" in item.get("plain", "")
              and "2 record fields on objects both mods set off for good" in md)
        return ok, f"admission {adm}; item {item.get('plain', '')[:200]!r}"

    # C504. What stays with the patch is said at the top, in the prediction section; a value a patch sets or keeps is
    # never called "still lost"
    def c504_with_patch_line_at_top():
        plan, md, html = record_validation_bundle()
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        line = (reader(plan).get("prediction") or {}).get("withPatchLine") or ""
        # the summary is a head, one line per kind and what stays (last round, units apart); each is on the page
        pr_ = reader(plan).get("prediction") or {}
        parts_ = [pr_.get("withPatchHead") or "\0", *(pr_.get("withPatchLines") or ["\0"])]
        ok = (bool(line) and all(x in sec for x in parts_) and PATCH in line and "still lost" not in md)
        return ok, f"with-patch line {line!r}; in the prediction section {bool(line) and line in sec}"

    # ------------------------------------------- report polish: the fourth blind review
    # C520. Where a patch only carries the value that wins without it, the losing mod's change stays lost exactly as
    # without the patch: the with-patch line and the item line say whose change it is, never "keeps the winning value"
    def c520_kept_says_whose_change_stays_lost():
        plan, md, html = record_validation_bundle()
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        line = p.get("withPatchLine") or ""
        items = " ".join(val.get("itemLines") or [])
        # (third review, Winterhold S1) the patch keeping the winner's value is its resolution, said with both mods named
        said_ = f"the patch keeps {MOD_B}'s value over {MOD_A}'s change on 1"
        ok = (said_ in line and said_ in items and "keeps the winning value" not in md and "stays lost 1" not in line)
        return ok, f"with-patch line {line!r}; item lines {items[:240]!r}"

    # C521. A lost value that is the base game's own value is no edit of that mod's: it only undoes the change of the mod
    # before it, so it is not counted against the winner (where the winner carries that earlier mod's value, nothing is
    # lost in game). The base value is read from another setup's row whose overridden plugin is the record's master. A
    # real edit (a value differing from the base game's) and a value whose base is not known stay counted.
    def c521_base_game_value_is_no_edit():
        # window 2, lane core: whose edit a value is reads the record's chain in the setup (the import's transcripts)
        # and the plugin's masters; R3's chain is not in the transcripts, so only its row's own values say anything
        R1, R2, R3 = f"000901:{BASE}", f"000902:{BASE}", f"000903:{BASE}"
        p_rows = [lost_row(OTHER, MOD_B, R1, "Placement", "v1", "a1", "a1", before=MOD_A),   # OTHER undoes ModA's change
                  lost_row(OTHER, MOD_B, R2, "Placement", "o2", "a2", "a2", before=MOD_A),   # OTHER's own edit
                  lost_row(OTHER, MOD_B, R3, "Placement", "v3", "a3", "a3", before=MOD_A)]   # base value not known
        a_rows = [lost_row(MOD_A, OTHER, R1, "Placement", "a1", "v1", "v1", before=BASE),
                  lost_row(MOD_A, OTHER, R2, "Placement", "a2", "o2", "v2", before=BASE)]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "plus-a"): a_rows, (RECORDS, "plus-b"): [],
               (RECORDS, "dependency-only"): []}
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        arms = [arm("baseline", [BASE, MOD_A, OTHER, MOD_B], kind="baseline"), arm("dependency-only", [BASE, OTHER]),
                arm("plus-a", [BASE, MOD_A, OTHER]), arm("plus-b", [BASE, OTHER, MOD_B])]
        chains = [(R1, "PlacedObject", [(BASE, {"Placement": "v1"}), (MOD_A, {"Placement": "a1"}),
                                        (OTHER, {"Placement": "v1"}), (MOD_B, {"Placement": "a1"})]),
                  (R2, "PlacedObject", [(BASE, {"Placement": "v2"}), (MOD_A, {"Placement": "a2"}),
                                        (OTHER, {"Placement": "o2"}), (MOD_B, {"Placement": "a2"})])]
        plan, md, html = compile_and_render(arms, results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr,
                                            transcripts={(RECORDS, "baseline"): records_transcript(chains)},
                                            masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        p = reader(plan).get("prediction") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("A"), e.get("admission")) for e in p.get("record") or []
                     if e.get("B") == MOD_B)
        ok = (adm == [("000901", OTHER, "notOwnEdit"), ("000902", OTHER, "admitted"), ("000903", OTHER, "admitted")]
              and "1 record field whose losing value only undoes the change of the mod loaded before it" in md
              and "in 1 of them the winning value is that earlier mod's own" in md)
        return ok, f"admission {adm}; set aside {(p.get('setAsideLine') or '')[-400:]!r}"

    # C522. Overwrite history a patch no longer flags is never said removed by the patch: a navmesh whose loss still holds
    # with the patch, flagged between other plugins, is named as still losing that ground, in the patch section and at
    # the top
    def c522_history_loss_persisting_is_named():
        n1, n2 = f"000N01:{BASE}", f"000N02:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  689 tris -> 5")]
        # fifth blind review (W-S1): N01 is flagged again with the patch but its re-measure says Gone; N02 is no longer
        # flagged (the patch's navmesh masters ModA) but its re-measure still Holds: N02 persists, N01 does not
        w_rows = [row("CLOBBERED", OTHER, PATCH, n1, label="CityExterior01 (ext)  465 tris -> 345")]
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n1, "Other": None, "Physical": "Gone",
                  "Detail": "no point of the loser's mesh is farther than 120u from the winner's"},
                 {"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n2, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 22597 sample(s) of the loser's mesh farther than 120u from the winner's"}]
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        p = reader(plan).get("prediction") or {}
        val_sec = section_text(md, headings(plan).get("validation") or "\0")
        top = " ".join(x.get("text", "") for x in p.get("historyLossLines") or [])
        persist = [ln for ln in val_sec.splitlines() if "1 of them still lose that ground" in ln]
        ok = ("rows the patch removes" not in md and "no longer flagged" in val_sec and bool(persist)
              and "1 of these navmesh records still loses that ground (CityExterior02 (ext) 22,597 samples" in top)
        return ok, f"persisting lines {persist}; top {top[-300:]!r}"

    # C523. Missing meshes the check measures are named, with the plugins whose records name them and the setups they are
    # missing in; the check no longer sits in the count-only line
    def c523_missing_meshes_named():
        MA = "loadability.missing-assets"

        def call(i, name, args):
            return {"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": name, "arguments": args}}

        def answer(i, doc):
            return {"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(doc)}]}}
        req = [{"batchMarker": "b6-models"}, call(1, "housecarl_records", {"fields": ["Model.File"]}),
               {"batchMarker": "b7-assets"}, call(1, "housecarl_asset_status", {"asset_paths": ["x"]})]
        resp = [{"batchMarker": "b6-models"},
                answer(1, {"matches": [{"source": MOD_A, "fields": [{"path": "Model.File", "value": "Arch\\House01.nif"}]},
                                       {"source": MOD_B, "fields": [{"path": "Model.File", "value": "arch\\Fine.nif"}]}]}),
                {"batchMarker": "b7-assets"},
                answer(1, {"results": [{"path": "meshes/Arch/House01.nif", "exists": False},
                                       {"path": "meshes/arch/Fine.nif", "exists": True}]})]
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        plan, md, html = compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            count_only={(MA, "baseline"): 1, (MA, "without-patch"): 1},
                                            transcripts={(MA, "baseline"): (req, resp), (MA, "without-patch"): (req, resp)})
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        line = next((ln for ln in sec.splitlines() if "House01.nif" in ln), "")
        lim = [ln for ln in md.splitlines() if "count without the records" in ln]
        ok = (bool(line) and MOD_A in line and "Fine.nif" not in md and "both setups" in line
              and not any(MA in ln or "Missing meshes" in ln for ln in lim))
        return ok, f"line {line!r}; count-only lines {lim}"

    # C524. With one evaluated mod, a clash that comes from it alone (no partner needed) is counted in the headline, so
    # the patch tally counts it too: the with-patch line adds up to the headline's clashes, and no line calls it
    # "already there with one mod alone"
    def c524_single_target_tally_matches_headline():
        res = "ModA Resources.esp"
        arms = [arm("baseline", [BASE, OTHER, res, MOD_A], kind="baseline"),
                arm("without-a", [BASE, OTHER]), arm("without-other", [BASE, res, MOD_A]),
                arm("with-patch", [BASE, OTHER, res, MOD_A, PATCH])]
        results = {(CONTACT, "baseline"): [row("CONTACT", OTHER, MOD_A, f"000G01:{MOD_A}", other=f"000S01:{OTHER}"),
                                           row("CONTACT", OTHER, MOD_A, f"000G02:{MOD_A}", other=f"000S02:{OTHER}")],
                   (CONTACT, "without-a"): [], (CONTACT, "with-patch"): [],
                   (CONTACT, "without-other"): [row("CONTACT", BASE, MOD_A, f"000G02:{MOD_A}", other=f"000S03:{BASE}")]}
        # coordinator D3: the clash from ModA alone is credited only on the patched setup's own re-measure (Gone);
        # with no re-measure, its absence from the patched listing is "cannot tell", never resolved
        preds = [{"Kind": "CONTACT", "A": OTHER, "B": MOD_A, "FormKey": f"000G0{i}:{MOD_A}", "Other": f"000S0{i}:{OTHER}",
                  "Physical": "Gone"} for i in (1, 2)]
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH], predicates={(CONTACT, "with-patch"): preds})
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        line = p.get("withPatchLine") or ""
        items = val.get("itemLines") or []
        plan2, md2, html2 = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        p2, val2 = reader(plan2).get("prediction") or {}, reader(plan2).get("validation") or {}
        ok = ("resolved 2" in line and val.get("hits") == 2 and not any("Already there" in x for x in items)
              and any(f"from {MOD_A} alone" in x for x in items)
              and val2.get("hits") == 1 and val2.get("cannotTell") == 1)
        return ok, (f"with-patch line {line!r}; item lines {items}; without re-measure hits {val2.get('hits')}, "
                    f"cannot tell {val2.get('cannotTell')}")

    # C525. With one evaluated mod, its navmesh replacing another mod's with a measured loss of ground is said near the
    # top as a measured fact, without a claim about NPCs
    def c525_single_target_navmesh_loss_at_top():
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER]),
                arm("with-patch", [BASE, OTHER, MOD_A, PATCH])]
        results = {(NAVOWN, "baseline"): [row("CLOBBERED", OTHER, MOD_A, f"000N01:{BASE}", label="Shop (int)  55 tris -> 93")],
                   (NAVOWN, "without-a"): [], (NAVOWN, "with-patch"): []}
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        p = reader(plan).get("prediction") or {}
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        lines = [x.get("text", "") for x in p.get("historyLossLines") or []]
        ok = (len(lines) == 1 and f"{MOD_A}'s navmesh replaces {OTHER}'s" in lines[0] and lines[0][:60] in sec
              and "NPC" not in lines[0] and "without the patch," in lines[0])
        return ok, f"lines {lines}"

    # C526. The limits are in words a modder reads: no probe population names (a LAND, edge-adjacent pairs, dependent-host
    # pairs) in the limits or in a refused comparison's reason, and no record signatures in a check title
    def c526_limits_in_plain_words():
        plan, md, html = limits_bundle()
        lim = section_text(md, headings(plan).get("coverage") or "\0")
        plan2, md2, html2 = comparison_bundle("refused")
        titles = " ".join(priorities()["reader"]["checkTitles"].values())
        bad = [t for t in ("a LAND", "edge-adjacent", "dependent-host") if t in lim or t in md2]
        ok = (not bad and "neighbouring cells" in lim and not re.search(r"\b(FLST|LCTN)\b", titles)
              and "records.registration-and-dialogue" in priorities()["reader"]["checkTitles"])
        return ok, f"tool words left {bad}; limits {lim[:300]!r}"

    # C527. The same where a mod loaded before the earlier mod changed the field first. Window 2, lane core: whose edit
    # a value is is read from the record's chain and the plugin's masters, not from other rows: ModA's R1 value is its
    # master's (the base game's), so it only undoes OtherMod's change; its R2 value is Early's, but Early is not its
    # master and the base game's differs, so it is ModA's own change (until lane core, the rows of other setups decided)
    def c527_undo_of_earlier_change_is_no_edit():
        early = "Early.esp"
        arms = [arm("baseline", [BASE, early, OTHER, MOD_A, MOD_B], kind="baseline"),
                arm("dependency-only", [BASE, early, OTHER]), arm("plus-a", [BASE, early, OTHER, MOD_A]),
                arm("plus-b", [BASE, early, OTHER, MOD_B]), arm("reordered", [BASE, OTHER, early, MOD_A, MOD_B])]
        R1, R2 = f"000911:{BASE}", f"000912:{BASE}"
        p_rows = [lost_row(OTHER, MOD_B, R1, "Owner", "e1", "x1", "o1", before=early),      # Early's change, OTHER's
                  lost_row(MOD_A, MOD_B, R1, "Owner", "o1", "x1", "e1", before=OTHER),      # ModA undoes OTHER's change
                  lost_row(MOD_A, MOD_B, R2, "Owner", "q2", "x2", "o2", before=OTHER)]      # only the reordered setup says q2
        r_rows = [lost_row(OTHER, MOD_B, R2, "Owner", "o2", "x2", "q2", before=early)]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "reordered"): r_rows}
        enr.update({(RECORDS, a): [] for a in ("dependency-only", "plus-a", "plus-b")})
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, a["armId"]): [] for a in arms}
        chains = [(R1, "PlacedObject", [(BASE, {"Owner": "o1"}), (early, {"Owner": "o1"}), (OTHER, {"Owner": "e1"}),
                                        (MOD_A, {"Owner": "o1"}), (MOD_B, {"Owner": "x1"})]),
                  (R2, "PlacedObject", [(BASE, {"Owner": "z2"}), (early, {"Owner": "q2"}), (OTHER, {"Owner": "o2"}),
                                        (MOD_A, {"Owner": "q2"}), (MOD_B, {"Owner": "x2"})])]
        plan, md, html = compile_and_render(arms, results, [MOD_A, MOD_B], count_only=count_only, enrichment=enr,
                                            transcripts={(RECORDS, "baseline"): records_transcript(chains)},
                                            masters={early: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        p = reader(plan).get("prediction") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("A"), e.get("admission")) for e in p.get("record") or []
                     if e.get("A") == MOD_A)
        ok = adm == [("000911", MOD_A, "notOwnEdit"), ("000912", MOD_A, "admitted")]
        return ok, f"admission {adm}"

    # ------------------------------------------- report polish round 2: the fifth blind review
    # C528. With no re-measure of the overwrite-history rows in the setup with the patch, nothing is said to still lose
    # ground, even when the same navmesh is flagged again there between other plugins: flagging is not a measurement
    def c528_navmesh_still_lost_only_from_remeasure():
        n1 = f"000N01:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345")]
        w_rows = [row("CLOBBERED", OTHER, PATCH, n1, label="CityExterior01 (ext)  465 tris -> 345")]
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH])
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        ok = "still lose" not in md and "Measured again" not in md and "navmesh replaces" in top
        return ok, f"top {top[-260:]!r}"

    # C529. A record field the patch gives to the losing mod drops the winning mod's change: said as "the patch keeps
    # ModA's value; ModB's change is dropped", never counted as resolved
    def c529_switched_said_as_change_dropped():
        plan, md, html = record_validation_bundle()
        p, val = reader(plan).get("prediction") or {}, reader(plan).get("validation") or {}
        line, items = p.get("withPatchLine") or "", " ".join(val.get("itemLines") or [])
        # after-review (Pilgrim review 2): reworded as one phrase; the outcome and its count are unchanged
        # (sixth review, BPT r5 S1) the switched outcome is worded apart from the kept one: "restores"
        want = f"the patch restores {MOD_A}'s value, dropping the winning {MOD_B}'s change on 1"
        ok = want in line and want in items and "resolved" not in line and val.get("hits") == 0
        return ok, f"with-patch line {line!r}; items {items[:240]!r}"

    # C530. A record change a patch newly undoes (no change to that field was lost without the patch) is counted with the
    # mod whose change it is and how many switch an object back on (the objects as examples); a field with a clash
    # without the patch is not counted again. Coordinator ruling: said in its own neutral line, never as a new clash
    # (that reprints the patch author's choices as problems, P52), so "new clashes" stays the geometry ones (C532)
    def c530_newly_undone_record_changes_are_new_clashes():
        p_rows = [lost_row(MOD_A, MOD_B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
                  lost_row(MOD_A, PATCH, f"000702:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0"),
                  # (eighth review) the patch puts back the value ModB's change replaced: undone (C1179's rule)
                  lost_row(MOD_B, PATCH, f"000703:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                           "Position=4,5,6; Rotation=0,0,0", "Position=4,5,6; Rotation=0,0,0")]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        count_only = {(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 3}
        results = {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            count_only=count_only, enrichment=enr)
        val = reader(plan).get("validation") or {}
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        new = next((ln for ln in md.splitlines() if ln.startswith("- New clashes in the setup")), "")
        ok = (val.get("undone") == 2 and val.get("undoneBy") == {MOD_A: 1, MOD_B: 1}
              # (fifth review) its unit named; (seventh review, BPT S1) each plugin's fields and records named
              and f"2 record fields the mods changed ({MOD_A} 1: record flags of 000702:{BASE}; {MOD_B} 1: position of "
                  f"000703:{BASE})" in line
              and f"1 of the 2, on 1 object, switch the object back on, for example 000702:{BASE}" in line   # round 2 item 5
              and new.endswith(": 0.") and "undo" not in new and val.get("patchProblems") == 0)
        return ok, f"undone {val.get('undone')} {val.get('undoneBy')}; line {line!r}; new clashes {new!r}"

    # C531. One unit per line or both stated: a record kind in the headline gives its records and its fields, and the
    # with-patch line says the total of the clashes it divides
    def c531_headline_and_tally_reconcile():
        plan, md, html = record_validation_bundle()
        p = reader(plan).get("prediction") or {}
        v, line = p.get("verdict") or "", p.get("withPatchLine") or ""
        # coordinator's last round: no total across units; each kind's line states its own total
        ok = ("(3 record fields)" in v and "clashes counted above" not in line
              and any("(3 record fields)" in x for x in p.get("withPatchLines") or []))
        return ok, f"verdict {v!r}; with-patch line {line!r}"

    for name, fn in [("w2-polish/C528-navmesh-still-lost-only-from-remeasure", c528_navmesh_still_lost_only_from_remeasure),
                     ("w2-polish/C529-switched-said-as-change-dropped", c529_switched_said_as_change_dropped),
                     ("w2-polish/C530-newly-undone-record-changes-are-new-clashes",
                      c530_newly_undone_record_changes_are_new_clashes),
                     ("w2-polish/C531-headline-and-tally-reconcile", c531_headline_and_tally_reconcile)]:
        cell(name, fn)

    for name, fn in [("w2-polish/C527-undo-of-earlier-change-is-no-edit", c527_undo_of_earlier_change_is_no_edit),
                     ("w2-polish/C520-kept-says-whose-change-stays-lost", c520_kept_says_whose_change_stays_lost),
                     ("w2-polish/C521-base-game-value-is-no-edit", c521_base_game_value_is_no_edit),
                     ("w2-polish/C522-history-loss-persisting-is-named", c522_history_loss_persisting_is_named),
                     ("w2-polish/C523-missing-meshes-named", c523_missing_meshes_named),
                     ("w2-polish/C524-single-target-tally-matches-headline", c524_single_target_tally_matches_headline),
                     ("w2-polish/C525-single-target-navmesh-loss-at-top", c525_single_target_navmesh_loss_at_top),
                     ("w2-polish/C526-limits-in-plain-words", c526_limits_in_plain_words)]:
        cell(name, fn)

    for name, fn in [("w2-round5/C499-attribution-from-single-mod-remeasure", c499_attribution_from_single_mod_remeasure),
                     ("w2-round5/C500-navmesh-comparison-names-its-unit", c500_navmesh_comparison_names_its_unit),
                     ("w2-round5/C501-shared-objects-keep-their-total", c501_shared_objects_keep_their_total),
                     ("w2-round5/C502-fixture-at-zero-is-cannot-tell", c502_fixture_at_zero_is_cannot_tell),
                     ("w2-round5/C503-disables-said-and-both-disabled-set-aside", c503_disables_said_and_both_disabled_set_aside),
                     ("w2-round5/C504-with-patch-line-at-top", c504_with_patch_line_at_top)]:
        cell(name, fn)

    for name, fn in [("w2-round4/C494-no-model-object-counted-nowhere", c494_no_model_counted_nowhere),
                     ("w2-round4/C495-lost-edit-classes-by-losing-side", c495_lost_edit_classes_by_losing_side),
                     ("w2-round4/C496-untouched-excludes-patch-edited", c496_untouched_excludes_patch_edited),
                     ("w2-round4/C497-headline-candidates-and-scope", c497_headline_candidates_and_scope),
                     ("w2-round4/C498-patch-wins-and-refusal-in-summary", c498_patch_wins_and_refusal_in_summary)]:
        cell(name, fn)

    # ------------------------------------------- P63/P64: the first new mod on the deployed tools (blind review)
    def own_master_bundle(masters=None):
        """C540/C541. One evaluated mod, ModA, whose plugin lists OtherMod as a master, added to an order holding
        OtherMod and ModB; a patch for ModA and ModB. OtherMod's value of record 701 is lost to ModA's, and OtherMod's
        object S1 is left without support by ModA (ModA building on its master); ModA's value of 702 is lost to ModB's,
        and ModB's object H2 is left without support by ModA (two different mods)."""
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"),
                arm("without-a", [BASE, OTHER, MOD_B]),
                arm("with-patch", [BASE, OTHER, MOD_B, MOD_A, PATCH])]
        p_rec = [lost_row(OTHER, MOD_A, f"000701:{BASE}", "Name", "o1", "a1", "v1"),
                 lost_row(MOD_A, MOD_B, f"000702:{BASE}", "Name", "a2", "b2", "v2")]
        w_rec = [lost_row(OTHER, MOD_A, f"000701:{BASE}", "Name", "o1", "a1", "v1")]
        enr = {(RECORDS, "baseline"): p_rec, (RECORDS, "without-a"): [], (RECORDS, "with-patch"): w_rec}
        count_only = {(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()}
        results = {(CONTACT, "baseline"): [row("CONTACT", OTHER, MOD_A, f"000S01:{OTHER}", other=f"000G01:{MOD_A}"),
                                           row("CONTACT", MOD_B, MOD_A, f"000H02:{MOD_B}", other=f"000G02:{MOD_A}")],
                   (CONTACT, "without-a"): [], (CONTACT, "with-patch"): []}
        m = {MOD_A: [BASE, OTHER], PATCH: [BASE, MOD_A, MOD_B]} if masters is None else masters
        return compile_and_render(arms, results, [MOD_A], patches=[PATCH], count_only=count_only, enrichment=enr,
                                  masters=m)

    # C540. A mod changing a record value of a plugin it lists as a master builds on it as intended: never a clash
    # between mods, never in a clash count or the patch tally; said as "<mod> changes <master>'s value (its master)".
    # Coordinator ruling (edict): the rule is for record values only; the geometry clash S01 stays counted (C548)
    def c540_own_master_override_is_not_a_clash():
        plan, md, html = own_master_bundle()
        p = reader(plan).get("prediction") or {}
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission")) for e in p.get("record") or [])
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        counted = sum(i["count"] for i in p.get("items") or [])
        ok = (adm == [("000701", "ownMaster"), ("000702", "admitted"), ("000H02", "admitted"), ("000S01", "admitted")]
              and counted == 3 and "clashes counted above" not in (p.get("withPatchLine") or "")
              and f"{MOD_A} changes {OTHER}'s value (its master) in 1 record field (" in sec
              and f"{MOD_A} changes {OTHER}'s value (its master)" in _html.unescape(html)
              and "Objects left without their supporting surface)" not in sec.split("(its master)", 1)[-1].split(".")[0]
              and (p.get("setAside") or {}).get("ownMaster") == 1)
        return ok, f"admission {adm}; counted {counted}; prediction {sec[:400]!r}"

    # C541. Decided from the plugins' master lists, never their names: a plugin that does not list the other is a
    # clash; with no master list recorded at all the report says it cannot tell, and counts the clash as found
    def c541_master_lists_decide_never_names():
        plan, md, html = own_master_bundle(masters={})
        adm = sorted(((e.get("formKey") or "")[:6], e.get("admission"))
                     for e in (reader(plan).get("prediction") or {}).get("record") or [])
        plan2, md2, html2 = own_master_bundle(masters=False)
        adm2 = sorted(((e.get("formKey") or "")[:6], e.get("admission"))
                      for e in (reader(plan2).get("prediction") or {}).get("record") or [])
        cov2 = section_text(md2, headings(plan2).get("coverage") or "\0")
        want = [("000701", "admitted"), ("000702", "admitted"), ("000H02", "admitted"), ("000S01", "admitted")]
        ok = (adm == want and adm2 == want and "its master" not in md
              and "the bundle records no plugin's master list" in cov2 and "master list" not in md)
        return ok, f"no master listed {adm}; no lists recorded {adm2}; coverage {cov2[-300:]!r}"

    def new_by_object_bundle(names=None, repaired=0):
        """C542/C543/C545. With the patch, object X1 is left without support against three hosts (three rows) and X2
        against two; without the patch only G0 is. The comparison counts the five rows as created."""
        X1, X2, G0 = f"000X01:{MOD_A}", f"000X02:{MOD_A}", f"000G00:{MOD_A}"
        base = [row("CONTACT", MOD_B, MOD_A, G0, other=f"000H00:{MOD_B}")]
        new = ([row("CONTACT", MOD_B, MOD_A, X1, other=f"000H1{i}:{MOD_B}") for i in (1, 2, 3)]
               + [row("CONTACT", MOD_B, MOD_A, X2, other=f"000H2{i}:{MOD_B}") for i in (1, 2)])
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=repaired, reclassified=0,
                                                     created=5, newDefect=5, attributed=0, unmeasured=0)}}
        log = (f"NEW     CONTACT    {MOD_B}  x  {MOD_A}   +5 record(s): created 5\n"
               + (f"SHRANK  CONTACT    {OTHER}  x  {MOD_A}   -{repaired} record(s): repaired {repaired}\n"
                  if repaired else ""))
        return compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): base + new},
                                  [MOD_A, MOD_B], patches=[PATCH], order=order, names=names,
                                  diff_logs={(CONTACT, "with-patch"): log})

    # (after the final round: the comparison's sentence stays in the plan; the page shows the patch summary instead)
    # C542. New problems with the patch count objects, not rows: an object left without support against three hosts is
    # one new problem, in the patch section, the comparison and the verdict alike
    def c542_new_problems_count_objects():
        plan, md, html = new_by_object_bundle()
        val = reader(plan).get("validation") or {}
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        nb = [n.get("count") for n in val.get("newByCheck") or []]
        created = (reg.get("counts") or {}).get("readerCreated")
        ok = (val.get("patchProblems") == 2 and nb == [2] and created == 2
              and "introduces 2 new problems in the check" in str((reader(plan).get("validation") or {}).get("headline", "")) and "New clashes in the setup with" in md
              and not re.search(r"\b5 new problems\b", md))
        return ok, f"new with patch {val.get('patchProblems')}; by check {nb}; comparison {created}"

    # C543. The body rule applies to the new problems by object: an object whose base has no model is left out once,
    # however many rows name it; a base with a model (an opaque plane included) stays counted
    def c543_new_problems_no_body_by_object():
        refs = {f"000X01:{MOD_A}": {"base": f"0B0L01:{MOD_A}"}, f"000X02:{MOD_A}": {"base": f"0B0P01:{MOD_A}"},
                f"000G00:{MOD_A}": {"base": f"0B0P01:{MOD_A}"},
                **{f"000H{i}:{MOD_B}": {"base": f"0B0S01:{MOD_B}"} for i in ("00", "11", "12", "13", "21", "22")}}
        bases = {f"0B0L01:{MOD_A}": {"type": "Light", "model": ""},
                 f"0B0P01:{MOD_A}": {"type": "Static", "model": "plane.nif"},
                 f"0B0S01:{MOD_B}": {"type": "Static", "model": "wall.nif"}}
        plan, md, html = new_by_object_bundle(names={"refs": refs, "bases": bases})
        val = reader(plan).get("validation") or {}
        c = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {}).get("counts") or {}
        ok = (val.get("patchProblems") == 1 and c.get("readerCreated") == 1 and c.get("readerNoBody") == 1
              and "1 whose object's record has no model" in md and "introduces 1 new problem in the check" in str((reader(plan).get("validation") or {}).get("headline", "")))
        return ok, (f"new with patch {val.get('patchProblems')}; comparison {c.get('readerCreated')} "
                    f"(no body {c.get('readerNoBody')})")

    # C544. A fixture listed once against each of two plugins with the patch is one object left hanging
    def c544_fixture_listed_twice_is_one_object():
        F = f"000F01:{MOD_A}"
        new = [row("EMPTY-SPACE", MOD_A, MOD_B, F, other=f"000H01:{BASE}"),
               row("EMPTY-SPACE", MOD_A, PATCH, F, other=f"000H02:{BASE}")]
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(FIXTURE, "baseline"): [], (FIXTURE, "with-patch"): new},
                                            [MOD_A, MOD_B], patches=[PATCH])
        val = reader(plan).get("validation") or {}
        nb = [n.get("count") for n in val.get("newByCheck") or []]
        ok = val.get("patchProblems") == 1 and nb == [1]
        return ok, f"new with patch {val.get('patchProblems')}; by check {nb}"

    # C545. A comparison that also repaired problems says its repairs do not cancel the new problems, in its own number
    def c545_repairs_sentence_agrees_in_number():
        plan, md, html = new_by_object_bundle(repaired=2)
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        rp = reg.get("readerPlain") or ""
        ok = "do not cancel the new problems." in rp and "introduced 2 new problems" in rp
        return ok, f"comparison {rp!r}"

    # C546. The changes the patch undoes are linked to their evidence, like every other count in the patch section
    def c546_undone_changes_linked():
        p_rows = [lost_row(MOD_A, MOD_B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_A, PATCH, f"000702:{BASE}", "Name", "a2", "p2", "p2")]   # put back (eighth review)
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        count_only = {(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1}
        results = {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}
        plan, md, html, index = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                                   count_only=count_only, enrichment=enr, want_index=True)
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        fids = re.findall(r"\(evidence-index\.md#(F-\d+)\)", line)
        anchors = set(re.findall(r'id="(F-\d+)"', index)) | set(re.findall(r"\{#(F-\d+)\}", index)) | set(
            re.findall(r"^#+ .*?(F-\d+)", index, re.M))
        ok = bool(fids) and set(fids) <= anchors
        return ok, f"undone line {line!r}; anchors {sorted(anchors)[:6]}"

    # C547. The page states a fact once: the set-aside sentence names only the kinds that occur (a list of zeros read as a
    # contradiction beside a clash counted elsewhere), and a check clean on several setups is one line naming them
    def c547_no_zero_set_aside_and_one_clean_line():
        arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        plan, md, html = compile_and_render(arms, {(WATER, "baseline"): [], (WATER, "with-patch"): [],
                                                   (CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, f"000G01:{MOD_A}"),
                                                                           row("CONTACT", BASE, MOD_A, f"000G02:{MOD_A}")],
                                                   (CONTACT, "with-patch"): []}, [MOD_A, MOD_B], patches=[PATCH])
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        water = priorities()["reader"]["checkTitles"][WATER]
        clean = [ln for ln in section_text(md, "Checks that found no problem").splitlines() if water in ln]
        ok = ("1 with the base game" in line and not re.search(r"(?<![\d,])0 (with|of|already|overwrite|not|between|repeated)", line)
              and len(clean) == 1 and "Measured load order" in clean[0] and " and " in clean[0])
        return ok, f"set-aside line {line[:200]!r}; clean lines {clean}"

    # C548. A geometry clash is never set aside for a master relationship (coordinator ruling, edict): objects that
    # intersect, lose their support or hang show in game whoever built on whom. In the prediction the clash between ModA
    # and its master's object is counted and labelled; with the patch, the patch's object left without support by its
    # master ModA is a counted new problem, labelled as between a plugin's object and its own master's
    def c548_geometry_clash_never_excluded_for_master():
        plan, md, html = own_master_bundle()
        p = reader(plan).get("prediction") or {}
        item = next((i for i in p.get("items") or [] if OTHER in i["mods"]), {})
        arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        new = [row("CONTACT", PATCH, MOD_A, f"000P01:{PATCH}", other=f"000G05:{MOD_A}"),
               row("EMPTY-SPACE", PATCH, MOD_A, f"000P02:{PATCH}", other=f"000G06:{MOD_A}")]
        plan2, md2, html2 = compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): new[:1],
                                                      (FIXTURE, "baseline"): [], (FIXTURE, "with-patch"): new[1:]},
                                               [MOD_A, MOD_B], patches=[PATCH], masters={PATCH: [BASE, MOD_A, MOD_B]})
        val = reader(plan2).get("validation") or {}
        nb = {n["title"]: (n["count"], n.get("masterNote")) for n in val.get("newByCheck") or []}
        line = next((ln for ln in md2.splitlines() if ln.startswith("- New clashes with the patch, by check")), "")
        ok = (item.get("count") == 1 and "lists OtherMod.esp as a master, but these are clashes between their objects"
              in item.get("plain", "") and val.get("patchProblems") == 2
              and all(v[0] == 1 and v[1] for v in nb.values()) and len(nb) == 2
              and "between a plugin's object and its own master's" in line
              and "between a plugin's object and its own master's" in _html.unescape(html2))
        return ok, f"item {item.get('plain', '')[:200]!r}; new {val.get('patchProblems')} {nb}; line {line!r}"

    for name, fn in [("w2-p63/C540-own-master-override-is-not-a-clash", c540_own_master_override_is_not_a_clash),
                     ("w2-p63/C548-geometry-clash-never-excluded-for-master", c548_geometry_clash_never_excluded_for_master),
                     ("w2-p64/C547-no-zero-set-aside-and-one-clean-line", c547_no_zero_set_aside_and_one_clean_line),
                     ("w2-p63/C541-master-lists-decide-never-names", c541_master_lists_decide_never_names),
                     ("w2-p64/C542-new-problems-count-objects", c542_new_problems_count_objects),
                     ("w2-p64/C543-new-problems-no-body-by-object", c543_new_problems_no_body_by_object),
                     ("w2-p64/C544-fixture-listed-twice-is-one-object", c544_fixture_listed_twice_is_one_object),
                     ("w2-p64/C545-repairs-sentence-agrees-in-number", c545_repairs_sentence_agrees_in_number),
                     ("w2-p64/C546-undone-changes-linked", c546_undone_changes_linked)]:
        cell(name, fn)

    # ------------------------------------------- last round: blind review of the regenerated BPT report
    # C549. The report says which setups the separate evaluation measured, in plugins, never "each mod on its own"
    def c549_sibling_setups_named():
        plan, md, html, index = patched_with_sibling("same")
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        ok = ("each mod on its own" not in md.lower() and f"only {MOD_A} besides the base game" in sec
              and f"only {MOD_B} besides the base game" in sec and "the base game alone" in sec)
        return ok, f"prediction {[ln for ln in sec.splitlines() if 'separate evaluation' in ln]}"

    def stays_bundle():
        """C551/C553/C554. One evaluated mod (ModA) and ModB; a patch. Overlaps: X x Y1, X x Y2, Z x Y1 with both
        loaded; with the patch X x Y1 and X x Y2 still hold, Z x Y1 is gone. Record fields: ModA's change to 701 is lost
        to ModB's, with the patch too; ModA's removal of 702 (initially disabled) is undone by ModB's value, with the
        patch too; ModA's change to 703 is resolved by the patch."""
        X, Z, Y1, Y2 = f"000X01:{MOD_A}", f"000Z01:{MOD_A}", f"000Y01:{MOD_B}", f"000Y02:{MOD_B}"
        p_ov = [row("OVERLAP", MOD_B, MOD_A, X, other=Y1, label="Hall (int)"),
                row("OVERLAP", MOD_B, MOD_A, X, other=Y2, label="Hall (int)"),
                row("OVERLAP", MOD_B, MOD_A, Z, other=Y1, label="Hall (int)")]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": fk, "Other": o, "Physical": ph}
                 for fk, o, ph in ((X, Y1, "Holds"), (X, Y2, "Holds"), (Z, Y1, "Gone"))]
        p_rec = [lost_row(MOD_A, MOD_B, f"000701:{BASE}", "Name", "a1", "b1", "v1"),
                 lost_row(MOD_A, MOD_B, f"000702:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0"),
                 lost_row(MOD_A, MOD_B, f"000703:{BASE}", "Name", "a3", "b3", "v3")]
        w_rec = p_rec[:2]
        enr = {(RECORDS, "baseline"): p_rec, (RECORDS, "with-patch"): w_rec}
        count_only = {(RECORDS, "baseline"): 3, (RECORDS, "with-patch"): 2}
        results = {(OVERLAP, "baseline"): p_ov, (OVERLAP, "with-patch"): p_ov[:2]}
        return compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A], patches=[PATCH], count_only=count_only,
                                  enrichment=enr, predicates={(OVERLAP, "with-patch"): preds})

    # C550. Overlaps in the headline are counted as objects too: 3 overlap pairs between 4 objects
    def c550_headline_overlaps_as_objects():
        plan, md, html = stays_bundle()
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        ok = "3 overlap candidates found by the check" in v and "(4 objects)" in v
        return ok, f"verdict {v!r}"

    # C551. No total adds overlap pairs to record fields: the with-patch summary gives each kind its own line in its own
    # unit, and the patch section's totals give one count per unit
    def c551_no_total_across_units():
        plan, md, html = stays_bundle()
        p = reader(plan).get("prediction") or {}
        lines = p.get("withPatchLines") or []
        hits = next((ln for ln in md.splitlines() if ln.startswith("- Predicted and resolved by")), "")
        ok = ("clashes counted above" not in md and len(lines) == 2
              and any("(3 overlap pairs)" in x and "resolved 1" in x and "left 2" in x for x in lines)
              and any("(3 record fields)" in x and "resolved 1" in x for x in lines)
              and all(x in md for x in lines) and "1 overlap pair, 1 record field" in hits)
        return ok, f"lines {lines}; totals {hits!r}"

    # C552. Cell-setting clashes are named by the fields they are, never "lighting, water" for fields that are neither
    def c552_cell_fields_named():
        p_rows = [lost_row(MOD_A, MOD_B, f"000C01:{BASE}", "Grid", ["Grid.Flags=8"], ["Grid.Flags=2"], ["Grid.Flags=0"]),
                  lost_row(MOD_A, MOD_B, f"000C02:{BASE}", "Grid", ["Grid.Flags=8"], ["Grid.Flags=2"], ["Grid.Flags=0"]),
                  lost_row(MOD_A, MOD_B, f"000C02:{BASE}", "OcclusionData", ["OcclusionData=1"], ["OcclusionData=2"], ["v"])]
        enr = {(CELLATTR, "baseline"): p_rows}
        results = {(CONTACT, "baseline"): []}
        plan, md, html = compile_and_render([arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline")], results, [MOD_A],
                                            count_only={(CELLATTR, "baseline"): 3}, enrichment=enr)
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        ok = ("Grid.Flags 2" in v and "OcclusionData 1" in v and "lighting, water" not in v
              and sec.count("Grid.Flags 2") >= 2)
        return ok, f"verdict {v!r}"

    # C553. What stays with the patch is said at the top in plain words, each with its count and unit: the overlaps
    # still there with their objects and place, the record changes still lost and to whom
    def c553_what_stays_at_top():
        plan, md, html = stays_bundle()
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        st = next((ln for ln in sec.splitlines() if "Still there with" in ln), "")
        ok = (f"2 overlap pairs between {MOD_A} and {MOD_B} (3 objects, all in Hall (int))" in st
              # after the final round (BPT review S5): each lost item says its unit
              and f"{MOD_A}'s changes to 2 record fields lost to {MOD_B}" in st)
        return ok, f"stays line {st!r}"

    # C554. An object one mod removes that the other still brings back with the patch is said at the top, named
    def c554_restored_object_named_at_top():
        plan, md, html = stays_bundle()
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        st = next((ln for ln in sec.splitlines() if "Still there with" in ln), "")
        ok = f"{MOD_B} still brings back 1 object {MOD_A} removes (for example 000702:{BASE})" in st
        return ok, f"stays line {st!r}"

    for name, fn in [("w2-last/C549-sibling-setups-named", c549_sibling_setups_named),
                     ("w2-last/C550-headline-overlaps-as-objects", c550_headline_overlaps_as_objects),
                     ("w2-last/C551-no-total-across-units", c551_no_total_across_units),
                     ("w2-last/C552-cell-fields-named", c552_cell_fields_named),
                     ("w2-last/C553-what-stays-at-top", c553_what_stays_at_top),
                     ("w2-last/C554-restored-object-named-at-top", c554_restored_object_named_at_top)]:
        cell(name, fn)

    # ------------------------------------------- window 2, lane p72: Pilgrim's blind review (P72, P73, order risk)
    STATE, ALIAS = "state.conditional-geometry", "functional.quest-alias-split"
    X, Y, S0 = f"000X01:{MOD_A}", f"000Y01:{MOD_B}", f"000S01:{BASE}"
    STATE_ARMS = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                  arm("without-patch", [BASE, MOD_A, MOD_B]), arm("without-b", [BASE, MOD_A]),
                  arm("without-a", [BASE, MOD_B])]

    def state_row(state, a, b, fk, other):
        r_ = row(f"STATE-CONTACT@{state}@Triggered", a, b, fk, label="Camp (ext)", other=other)
        r_["Summary"] = f"Contact StateConditional under CampEnable: {fk} arrives - Sunk: sits 9u inside something"
        return r_

    def state_bundle(alone=(), patched=()):
        """One evaluated mod (ModA) with partner ModB and a patch. ModA's object X sits inside ModB's Y in three world
        states (S1, S2, S3) with both loaded. `alone`: the rows of the setup without ModB; `patched`: the rows with the
        patch."""
        p_rows = [state_row(st, MOD_A, MOD_B, X, Y) for st in ("S1", "S2", "S3")]
        results = {(STATE, "baseline"): list(patched), (STATE, "without-patch"): p_rows,
                   (STATE, "without-b"): list(alone), (STATE, "without-a"): []}
        return compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])

    # C580. A world-state clash is one object: an object found in three world states is counted once, in one item, and
    # the item says in how many states it is found (Pilgrim: one cairn listed three times, "resolved 3" for one fix)
    def c580_state_object_counted_once():
        plan, md, html = state_bundle()
        p = reader(plan).get("prediction") or {}
        its = [i for i in p.get("items") or [] if i["checkId"] == STATE]
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        rec = [e for e in p.get("record") or [] if e["checkId"] == STATE]
        ok = (len(its) == 1 and its[0]["count"] == 1 and "3 world states" in its[0]["plain"]
              and (p.get("verdict") or "").count(title) == 1 and sum(e["admission"] == "admitted" for e in rec) == 1
              and "3 world states" in md)
        return ok, f"items {[(i['count'], i['plain'][:160]) for i in its]}; verdict {p.get('verdict')!r}"

    # C581 (coordinator ruling on Pilgrim's blind review). A world-state clash is the pair of objects: with ModB removed,
    # ModA's object X sunk into a base-game object is a different clash, so X sunk into ModB's Y needs ModB; with ModB
    # removed and X still in Y's place (the same pair), it does not
    def c581_state_attribution_by_pair():
        plan, md, html = state_bundle(alone=[state_row("S1", BASE, MOD_A, X, S0)])
        its = [i for i in (reader(plan).get("prediction") or {}).get("items") or [] if i["checkId"] == STATE]
        plan2, md2, html2 = state_bundle(alone=[state_row("S2", MOD_A, MOD_B, X, Y)])
        its2 = [i for i in (reader(plan2).get("prediction") or {}).get("items") or [] if i["checkId"] == STATE]
        ok = (len(its) == 1 and its[0]["attribution"] == "needs-both" and f"needs both {MOD_A} and {MOD_B}" in its[0]["plain"]
              and len(its2) == 1 and its2[0]["attribution"] == "single-mod")
        return ok, f"items {[(i['attribution'], i['plain'][:160]) for i in its + its2]}"

    # C582. With the patch the object is judged once across its states: still in one state is left 1; gone in all is
    # resolved 1, never once per state
    def c582_state_patch_outcome_once():
        plan, md, html = state_bundle(patched=[state_row("S2", MOD_A, MOD_B, X, Y)])
        lines = [x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or [] if "(1 clash)" in x]
        plan2, md2, html2 = state_bundle()
        lines2 = [x for x in (reader(plan2).get("prediction") or {}).get("withPatchLines") or [] if "clash" in x]
        ok = (len(lines) == 1 and "left 1" in lines[0] and "resolved" not in lines[0]
              and len(lines2) == 1 and "(1 clash): resolved 1" in lines2[0])
        return ok, f"still in S2: {lines}; gone: {lines2}"

    # C593 (coordinator ruling on Pilgrim's blind review, replacing C583). New with the patch is decided only by the same
    # setup with and without the patch: both splits the patched setup has and the setup without the patch lacks are new.
    # One of them a setup without the evaluated mod also has: the report says the patch brings it back from there, and
    # never calls it already there.
    def c593_new_with_patch_same_setup_only():
        R1, R2, R3, R4 = (f"00{i}A01:{BASE}" for i in range(1, 5))
        w_rows = [row("SPLIT", OTHER, PATCH, R1, other=R2, label="Temple (int)"),
                  row("SPLIT", OTHER, PATCH, R3, other=R4, label="Temple (int)")]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B]), arm("without-a", [BASE, OTHER, MOD_B])]
        a_rows = [row("SPLIT", OTHER, MOD_B, R1, other=R2, label="Temple (int)")]
        for r_ in w_rows + a_rows:      # the probe's words name the quest and aliases, never the plugins
            r_["Summary"] = f"quest 'Q' aliases 'X' and 'Y' were 137u apart, now 1,800u apart ({r_['Alternative'][0]['FormKey']})"
        results = {(ALIAS, "baseline"): w_rows, (ALIAS, "without-patch"): [], (ALIAS, "without-a"): a_rows}
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        val = reader(plan).get("validation") or {}
        line = next((ln for ln in md.splitlines() if "brings back" in ln), "")
        ok = (val.get("patchProblems") == 2 and [n["count"] for n in val.get("newByCheck") or []] == [2]
              and f"without {MOD_A}" in line and "1 " in line and "already there" not in md.lower()
              and "not counted as new" not in md.lower())
        return ok, f"new {val.get('patchProblems')} {val.get('newByCheck')}; line {line!r}"

    # C584. A load-order risk the record lane measured on the same plugins in another order is said as a measured fact:
    # with OtherMod loaded before ModA, OtherMod's changes are lost to ModA's; rows the measured order has too, or of a
    # pair whose order did not change, are not; an order setup with other plugins or bytes is refused
    def c584_order_risk_said():
        O2 = "Early.esp"
        p_rec = [lost_row(MOD_A, OTHER, f"000C01:{BASE}", "Lighting", "a", "o", "v"),
                 lost_row(O2, MOD_A, f"000C03:{BASE}", "Music", "e", "a", "v"),
                 lost_row(O2, OTHER, f"000C04:{BASE}", "Music", "e", "o", "v")]
        arms = [arm("baseline", [BASE, O2, MOD_A, OTHER], kind="baseline")]
        enr = {(CELLATTR, "baseline"): p_rec}
        results = {(CONTACT, "baseline"): []}
        o_rows = [dict(lost_row(OTHER, MOD_A, f"000C01:{BASE}", "Lighting", "o", "a", "v"), _check=CELLATTR, _type="Cell"),
                  dict(lost_row(OTHER, MOD_A, f"000C01:{BASE}", "ImageSpace", "o", "a", "v"), _check=CELLATTR, _type="Cell"),
                  dict(lost_row(OTHER, MOD_A, f"000C02:{BASE}", "Lighting", "o", "a", "v"), _check=CELLATTR, _type="Cell"),
                  dict(lost_row(O2, MOD_A, f"000C03:{BASE}", "Music", "e", "a", "v"), _check=CELLATTR, _type="Cell"),
                  # Early's change, lost to OtherMod in the measured order, is lost to ModA in the other: lost either way,
                  # and the order of Early and ModA did not change, so it is no order risk
                  dict(lost_row(O2, MOD_A, f"000C04:{BASE}", "Music", "e", "a", "v"), _check=CELLATTR, _type="Cell"),
                  # (lane core) Early's change lost to ModA in the other order only, but Early and ModA kept their order:
                  # no order risk, decided by the order rule alone (000C04 is now also caught as lost either way)
                  dict(lost_row(O2, MOD_A, f"000C06:{BASE}", "Music", "e", "a", "v"), _check=CELLATTR, _type="Cell")]
        setup = {"armId": "lux-first", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": plugins(BASE, O2, OTHER, MOD_A), "rows": o_rows}
        plan, md, html = compile_and_render(arms, results, [MOD_A], count_only={(CELLATTR, "baseline"): 3},
                                            enrichment=enr, order_setups=[setup])
        lines = [x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or []]
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        refused = ""
        try:
            compile_and_render(arms, results, [MOD_A], count_only={(CELLATTR, "baseline"): 3}, enrichment=enr,
                               order_setups=[dict(setup, plugins=plugins(BASE, O2, OTHER, MOD_A, shas={MOD_A: "a" * 64}))])
        except R.InputRefused as ex:
            refused = str(ex)
        ok = (len(lines) == 1 and f"3 of {OTHER}'s record fields are lost to {MOD_A}" in lines[0]
              and "2 records" in lines[0] and lines[0] in sec and O2 not in lines[0]
              and refused.startswith("order-setup-foreign"))
        return ok, f"lines {lines}; refused {refused[:80]!r}"

    for name, fn in [("w2-p72/C580-state-object-counted-once", c580_state_object_counted_once),
                     ("w2-p72/C581-state-attribution-by-pair", c581_state_attribution_by_pair),
                     ("w2-p72/C582-state-patch-outcome-once", c582_state_patch_outcome_once),
                     ("w2-p73/C593-new-with-patch-same-setup-only", c593_new_with_patch_same_setup_only),
                     ("w2-order/C584-order-risk-said", c584_order_risk_said)]:
        cell(name, fn)

    # ------------------------------------------- window 2, lane p72: Sewers' blind review (P93, P94, P95)
    # C585. Two world-state rows of one object against two different objects, whose words are the same, are two clashes:
    # none is lost to an identical summary (Sewers: a tower base against 13 objects was shown as 12)
    def c585_identical_words_other_partner_kept():
        Y2 = f"000Y02:{MOD_B}"
        p_rows = [state_row("S1", MOD_A, MOD_B, X, Y), state_row("S1", MOD_A, MOD_B, X, Y2)]
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): p_rows, (STATE, "without-b"): [],
                   (STATE, "without-a"): []}
        plan, md, html = compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])
        p = reader(plan).get("prediction") or {}
        its = [i for i in p.get("items") or [] if i["checkId"] == STATE]
        rec = [e for e in p.get("record") or [] if e["checkId"] == STATE]
        ok = len(its) == 1 and its[0]["count"] == 2 and len(rec) == 2 and "2 inconsistencies" in (p.get("verdict") or "")
        return ok, f"items {[i['count'] for i in its]}; record rows {len(rec)}; verdict {p.get('verdict')!r}"

    # C586. One object left without two supports is two clashes, never "2 objects" (Sewers: one object printed as 2 and
    # 4 objects); the headline's shared-object phrase is in the singular for one object
    def c586_one_object_two_supports_unit():
        Z1, H1, H2 = f"000Z11:{MOD_A}", f"000H11:{MOD_B}", f"000H12:{MOD_B}"
        p_c = [row("CONTACT", MOD_B, MOD_A, Z1, other=H1, label="Temple (int)"),
               row("CONTACT", MOD_B, MOD_A, Z1, other=H2, label="Temple (int)")]
        p_f = [row("EMPTY-SPACE", MOD_B, MOD_A, Z1, other=H1, label="Temple (int)")]
        preds = [{"Kind": "CONTACT", "A": MOD_B, "B": MOD_A, "FormKey": Z1, "Other": h, "Physical": "Gone"} for h in (H1, H2)]
        results = {(CONTACT, "baseline"): p_c, (CONTACT, "with-patch"): [], (FIXTURE, "baseline"): p_f,
                   (FIXTURE, "with-patch"): []}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A], patches=[PATCH],
                                            predicates={(CONTACT, "with-patch"): preds})
        p = reader(plan).get("prediction") or {}
        lines = [x for x in p.get("withPatchLines") or [] if x.startswith("Objects left without")]
        hits = next((ln for ln in md.splitlines() if ln.startswith("- Predicted and resolved by")), "")
        v = p.get("verdict") or ""
        ok = (len(lines) == 1 and "(2 object-and-support pairs)" in lines[0] and "(2 objects)" not in lines[0]
              # (seventh review, Sewers S2) the shared fixture pair is said as on the same objects, counted once: what
              # must not appear is a count of objects for pairs
              and re.search(r"[0-9] objects", hits) is None and "counted once" in hits
              and "the same object also found by the check" in v and "the same 1 objects" not in v)
        return ok, f"lines {lines}; resolved {hits!r}; verdict {v[-160:]!r}"

    # C587. A check this evaluation could not run on the measured order, counted from the separate evaluation without the
    # patch, is said as such where it is counted (with its file) and where the failure is listed (Sewers R20 vs R46)
    def c587_sibling_only_check_said():
        p_rows = [state_row("S1", MOD_A, MOD_B, X, Y)]
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B])]
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("plus-a", [BASE, MOD_A]),
                  arm("plus-b", [BASE, MOD_B]), arm("dependency-only", [BASE])]
        s_results = {(STATE, "baseline"): p_rows, (STATE, "plus-a"): [], (STATE, "plus-b"): [],
                     (STATE, "dependency-only"): [], (CONTACT, "baseline"): [], (CONTACT, "plus-a"): [],
                     (CONTACT, "plus-b"): [], (CONTACT, "dependency-only"): []}
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH],
                                            missing=[(STATE, "without-patch", "failed"), (STATE, "baseline", "failed")],
                                            sibling=(s_arms, s_results, [MOD_A], {"plan_id": "sibl00000000"}))
        p = reader(plan).get("prediction") or {}
        its = [i for i in p.get("items") or [] if i["checkId"] == STATE]
        miss = [ln for ln in md.splitlines() if "No result" in ln and STATE in ln or
                ("No result" in ln and (priorities()["reader"]["checkTitles"].get(STATE) or "\0") in ln)]
        ok = (len(its) == 1 and "Measured in the separate evaluation without the patch" in its[0]["plain"]
              and "state.conditional-geometry" in its[0]["plain"]
              and len(miss) == 1 and "come from the separate evaluation without the patch" in miss[0])
        return ok, f"items {[i['plain'][-260:] for i in its]}; missing {miss}"

    for name, fn in [("w2-p93/C585-identical-words-other-partner-kept", c585_identical_words_other_partner_kept),
                     ("w2-p94/C586-one-object-two-supports-unit", c586_one_object_two_supports_unit),
                     ("w2-p95/C587-sibling-only-check-said", c587_sibling_only_check_said)]:
        cell(name, fn)

    # ------------------------------------------- window 2, lane cw: Pilgrim's second blind review (B1) and Clockwork (P101,
    # P103, P104, P105, P108, P21/P53)
    WP_ARMS = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
               arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
    PH = "(the winner's value)"

    def cw_bundle(p_rows, w_rows=(), check=RECORDS, extra_results=None, arms=None):
        enr = {(check, "without-patch"): list(p_rows), (check, "baseline"): list(w_rows)}
        count_only = {k: max(1, len(v)) for k, v in enr.items()}
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        results.update(extra_results or {})
        return compile_and_render(arms or WP_ARMS, results, [MOD_A], patches=[PATCH], count_only=count_only,
                                  enrichment=enr)

    def rec_of(plan, fk, field=None):
        return [e for e in (reader(plan).get("prediction") or {}).get("record") or []
                if str(e.get("formKey")) == fk and (field is None or e.get("field") == field)]

    THIRD = "ThirdMod.esp"

    def core_bundle(p_rows, w_rows=(), check=RECORDS, arms=None, masters=None, p_chains=(), w_chains=(),
                    extra_results=None, targets=(MOD_A,), patches=(PATCH,), p_arm="without-patch", w_arm="baseline",
                    p_tr=None, names=None, w_tr=None):
        enr = {(check, p_arm): list(p_rows)}
        tr = {(check, p_arm): records_transcript(p_chains)} if p_chains else {}
        if p_tr is not None:
            tr[(check, p_arm)] = p_tr
        if w_arm:
            enr[(check, w_arm)] = list(w_rows)
            if w_chains:
                tr[(check, w_arm)] = records_transcript(w_chains)
            if w_tr is not None:
                tr[(check, w_arm)] = w_tr
        count_only = {k: max(1, len(v)) for k, v in enr.items()}
        results = {(CONTACT, a_): [] for (_, a_) in enr}
        results.update(extra_results or {})
        return compile_and_render(arms or WP_ARMS, results, list(targets), patches=list(patches),
                                  count_only=count_only, enrichment=enr, transcripts=tr, masters=masters,
                                  **({"names": names} if names is not None else {}))

    # C620 (Pilgrim's second blind review, B1). Where the winning mod's value equals the value the losing mod changed
    # (winner = overridden: it carries the earlier value, no change of its own), a patch that takes the losing mod's
    # value restores that change: resolved, never "the winning mod's change is dropped". Only a genuine change counts as
    # dropped.
    def c620_no_change_never_dropped():
        R1, R2 = f"00C201:{BASE}", f"00C202:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Placement", "o1", "v1", "v1"),      # ModA restates the earlier value
                  lost_row(OTHER, MOD_A, R2, "Placement", "o2", "a2", "v2")]      # ModA's own move
        w_rows = [lost_row(MOD_A, PATCH, R1, "Placement", "v1", "o1", "o1"),
                  lost_row(MOD_A, PATCH, R2, "Placement", "a2", "o2", "o2")]
        # window 2, lane core: the record's chain (the import's transcripts) and ModA's masters say whose change it is
        chains = [(R1, "PlacedObject", [(BASE, {"Placement": "v1"}), (OTHER, {"Placement": "o1"}),
                                        (MOD_A, {"Placement": "v1"})]),
                  (R2, "PlacedObject", [(BASE, {"Placement": "v2"}), (OTHER, {"Placement": "o2"}),
                                        (MOD_A, {"Placement": "a2"})])]
        plan, md, html = core_bundle(p_rows, w_rows, p_chains=chains,
                                     masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        w1 = [e.get("withPatch") for e in rec_of(plan, R1)]
        w2 = [e.get("withPatch") for e in rec_of(plan, R2)]
        # after-review (Pilgrim review 2): "X's change is dropped N" reads "keeps Y's value over X's change on N"
        dropped = [ln for ln in md.splitlines() if "'s change on " in ln]
        ok = (w1 == ["resolved"] and w2 == ["switched"] and dropped
              and all("change on 1" in ln and "change on 2" not in ln for ln in dropped))
        return ok, f"R1 {w1}, R2 {w2}; dropped lines {[ln[:140] for ln in dropped]}"

    # C621 (Clockwork P101). With the patch winning and neither mod's value lost to it (a third plugin's is), the patch
    # carries the losing mod's value and the winning mod made no change of its own there: resolved, not "cannot tell",
    # even where the import records the winner's value only as a placeholder.
    def c621_neither_lost_is_resolved():
        R3 = f"00C203:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R3, "OcclusionData", ["OcclusionData=AA"], PH, ["OcclusionData=00"])]
        w_rows = [lost_row(OTHER, PATCH, R3, "OcclusionData", ["OcclusionData=BB"], PH, ["OcclusionData=AA"])]
        arms = [arm("baseline", [BASE, MOD_A, OTHER, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, MOD_A, OTHER, MOD_B])]
        plan, md, html = cw_bundle(p_rows, w_rows, check=CELLATTR, arms=arms)
        got = [e.get("withPatch") for e in rec_of(plan, R3)]
        sec = section_text(md, headings(plan).get("validation") or "\0")
        ok = got == ["resolved"] and "cannot tell" not in md.lower().split("## what the checks could not judge")[0]
        return ok, f"withPatch {got}; {sec[:200]!r}"

    # C622 (Clockwork P103). A navmesh record's geometry (NavigationMesh Data) is judged by the navmesh check, by its
    # links and lost ground: where that check measured the setup, the record lane's row is not counted again as a record
    # field (it is kept in the record and said in the set-aside line); where it did not, it stays counted.
    def c622_navmesh_data_judged_by_navmesh_check():
        N1 = f"0C3901:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        q_ = lost_row(MOD_B, MOD_A, f"00C204:{BASE}", "Owner", "b", "a", "v")
        measured = {(NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}
        plan, md, _ = cw_bundle([r_, q_], extra_results=measured)
        adm = [e["admission"] for e in rec_of(plan, N1)]
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        plan2, md2, _ = cw_bundle([r_, q_])
        adm2 = [e["admission"] for e in rec_of(plan2, N1)]
        ok = adm == ["navmeshCheck"] and "navmesh" in line and adm2 == ["admitted"]
        return ok, f"with navmesh check {adm}; without {adm2}; line {line[-260:]!r}"

    # C1054 (Clockwork r2 S1): the set-aside line for navmesh geometry says what the navmesh check does not judge: its
    # door connections (door triangles), so a reader does not take lost doors as covered by the navmesh lines.
    def c1054_navmesh_doors_not_checked_said():
        N1 = f"0C3954:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        measured = {(NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}
        plan, md, _ = cw_bundle([r_], extra_results=measured)
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        return "door" in line and "not checked" in line, line[-300:]

    for name, fn in [("fourth-review/C1054-navmesh-doors-not-checked-said", c1054_navmesh_doors_not_checked_said)]:
        cell(name, fn)

    # C623 (Clockwork P104). A losing value equal to a base-game plugin's own value of the same field (its row in the
    # same setup) is no edit of that mod's: set aside as such, never "the same lost value carried by a second plugin"
    def c623_base_value_not_own_edit():
        F1 = f"00C205:{BASE}"
        p_rows = [lost_row("Update.esm", MOD_A, F1, "MaxHeight", ["MaxHeight.CellData=0A0B"], PH, ["MaxHeight.CellData=0C"]),
                  lost_row(OTHER, MOD_A, F1, "MaxHeight", ["MaxHeight.CellData=0A0B"], PH, ["MaxHeight.CellData=0D"])]
        arms = [arm("baseline", [BASE, "Update.esm", THIRD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, "Update.esm", THIRD, OTHER, MOD_A, MOD_B])]
        # window 2, lane core: OtherMod's value is Update.esm's, its master's: read from the record's chain
        chains = [(F1, "Worldspace", [(BASE, {"MaxHeight.CellData": "0C"}), ("Update.esm", {"MaxHeight.CellData": "0A0B"}),
                                      (THIRD, {"MaxHeight.CellData": "0D"}), (OTHER, {"MaxHeight.CellData": "0A0B"}),
                                      (MOD_A, {"MaxHeight.CellData": "0E"})])]
        plan, md, _ = core_bundle(p_rows, arms=arms, p_chains=chains,
                                  masters={"Update.esm": [BASE], THIRD: [BASE], OTHER: [BASE, "Update.esm"],
                                           MOD_A: [BASE], MOD_B: [BASE]})
        adm = {e["A"]: e["admission"] for e in rec_of(plan, F1)}
        ok = adm.get(OTHER) == "notOwnEdit" and adm.get("Update.esm") == "baseGame"
        return ok, f"admissions {adm}"

    # C624 (Clockwork P105). A list the winner holds in full, adding only items of its own (FormKeys of the winning
    # plugin), carries the losing mod's change forward: not a lost edit. A list missing an item of the losing list, or
    # adding an item an earlier plugin could have held, stays counted.
    def c624_carried_list_not_lost():
        L1, L2, L3 = (f"00C2{i}0:{BASE}" for i in (6, 7, 8))
        dash = "—"
        p_rows = [lost_row(OTHER, MOD_A, L1, "Items", [f"Items: 14 vs {MOD_A} 15 item(s) {dash} only in {MOD_A}: [14] 220EC9:{MOD_A}"], PH,
                           [f"Items: 11 vs {MOD_A} 15 item(s) {dash} only in {MOD_A}: [11] 0131F1:{BASE}; [12] 0131F2:{BASE} (+2 more element(s))"]),
                  lost_row(OTHER, MOD_A, L2, "Items", [f"Items: 14 vs {MOD_A} 15 item(s) {dash} only in {MOD_A}: [14] 0131F3:{BASE}"], PH, ["Items: 3"]),
                  lost_row(OTHER, MOD_A, L3, "Items", [f"Items: 14 vs {MOD_A} 14 item(s) {dash} only here: [3] 0131F4:{BASE}; only in {MOD_A}: [13] 220ECA:{MOD_A}"], PH, ["Items: 3"])]
        plan, md, _ = cw_bundle(p_rows)
        adm = [rec_of(plan, x)[0]["admission"] for x in (L1, L2, L3)]
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        ok = adm == ["carriedList", "admitted", "admitted"] and "keeps every item" in line
        return ok, f"admissions {adm}; line {line[-220:]!r}"

    # C625 (Clockwork P108). Lists are compared as sets of items (the record lane's rule): a list whose only difference
    # is the order of the same items is not a lost edit; a value with any other difference stays counted.
    def c625_order_only_not_lost():
        G1, G2 = f"00C211:{BASE}", f"00C212:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, G1, "Regions", [f"Regions: same 7 item(s), ORDER DIFFERS from {MOD_A}"], PH, [PH]),
                  lost_row(OTHER, MOD_A, G2, "Data", [f"Data.EdgeLinks: same 25 item(s), ORDER DIFFERS from {MOD_A}",
                                                      f"Data.Triangles: 238 vs {MOD_A} 238 item(s), contents differ"], PH, [PH])]
        plan, md, _ = cw_bundle(p_rows, check=CELLATTR)
        adm = [rec_of(plan, x)[0]["admission"] for x in (G1, G2)]
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        ok = adm == ["orderOnly", "admitted"] and "same items in another order" in line
        return ok, f"admissions {adm}; line {line[-200:]!r}"

    # C626 (Clockwork P21, P53). A field whose bytes the game reads only in part (a cell's Grid flags: one byte of land
    # flags, three unused) is compared on the bytes read: a change only in the unused bytes is not a lost edit; a change
    # in the read byte stays counted.
    def c626_unused_bits_not_lost():
        C1, C2 = f"00C221:{BASE}", f"00C222:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, C1, "Grid", ["Grid.Flags=327680"], PH, ["Grid.Flags=5504256"]),
                  lost_row(OTHER, MOD_A, C2, "Grid", ["Grid.Flags=1"], PH, ["Grid.Flags=0"])]
        for r_ in p_rows:
            r_["Summary"] = r_["Summary"].replace("PlacedObject", "Cell")
        plan, md, _ = cw_bundle(p_rows, check=CELLATTR)
        adm = [rec_of(plan, x)[0]["admission"] for x in (C1, C2)]
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        ok = adm == ["unusedBits", "admitted"] and "unused" in line
        return ok, f"admissions {adm}; line {line[-220:]!r}"

    # C627 (Pilgrim's second blind review, S3). The order-risk line states the order measured exactly: the plugin moved
    # and every plugin it moved past, never only one of them.
    def c627_order_line_names_the_move():
        O2, O3 = "Early.esp", "Late.esp"
        p_rec = [lost_row(O2, MOD_A, f"000C05:{BASE}", "Music", "e", "a", "v")]
        arms = [arm("baseline", [BASE, O2, MOD_A, OTHER, O3], kind="baseline")]
        o_rows = [dict(lost_row(OTHER, MOD_A, f"000C01:{BASE}", "Lighting", "o", "a", "v"), _check=CELLATTR, _type="Cell")]
        setup = {"armId": "late", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": plugins(BASE, O2, OTHER, O3, MOD_A), "rows": o_rows}
        plan, md, html = compile_and_render(arms, {(CONTACT, "baseline"): []}, [MOD_A],
                                            count_only={(CELLATTR, "baseline"): 1},
                                            enrichment={(CELLATTR, "baseline"): p_rec}, order_setups=[setup])
        lines = [x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or []]
        want = f"{MOD_A} moved after {OTHER} and {O3}"
        ok = len(lines) == 1 and want in lines[0] and f"{OTHER} moved before" not in lines[0]
        return ok, f"lines {lines}"

    # C628 (Pilgrim's second blind review, S4). The patch-wins line counts record fields, not rows: one field two
    # plugins lose to the patch is 1 record field in 2 plugin pairs.
    def c628_patch_wins_fields_once():
        F2 = f"00929C:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, F2, "OcclusionData", "o", "a", "v")]
        w_rows = [lost_row(OTHER, PATCH, F2, "OcclusionData", "o", "p", "v"),
                  lost_row(MOD_A, PATCH, F2, "OcclusionData", "a", "p", "o")]
        plan, md, _ = cw_bundle(p_rows, w_rows, check=CELLATTR)
        ln = next((x for x in md.splitlines() if "where a declared patch's value wins" in x), "")
        ok = "1 record field, lost by 2 plugin pairs in all" in ln     # (fourth review) the pairs read as the losers
        return ok, f"line {ln[:160]!r}"

    # C1043 (Clockwork review S3 / Minor 4): the patch-wins line says the pairs are the plugin pairs that lose its fields,
    # all told, so "4 record fields, in 6 plugin pairs" does not read as impossible.
    def c1043_patch_wins_pairs_read():
        F2 = f"00929C:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, F2, "OcclusionData", "o", "a", "v")]
        w_rows = [lost_row(OTHER, PATCH, F2, "OcclusionData", "o", "p", "v"),
                  lost_row(MOD_A, PATCH, F2, "OcclusionData", "a", "p", "o")]
        plan, md, _ = cw_bundle(p_rows, w_rows, check=CELLATTR)
        ln = next((x for x in md.splitlines() if "where a declared patch's value wins" in x), "")
        return "1 record field, lost by 2 plugin pairs in all," in ln, f"line {ln[:200]!r}"

    for name, fn in [("fourth-review/C1043-patch-wins-pairs-read", c1043_patch_wins_pairs_read)]:
        cell(name, fn)

    # C629 (Pilgrim's second blind review, S5). The base-game geometry line counts a pair of objects once however many
    # world states show it.
    def c629_base_game_state_pairs_once():
        X2 = f"000X02:{MOD_A}"
        p_rows = [state_row("S1", BASE, MOD_A, X, S0), state_row("S2", BASE, MOD_A, X, S0),
                  state_row("S1", BASE, MOD_A, X2, S0)]
        # two broken links in one navmesh are two links (rows of a check that is not by world state stay rows)
        links = [row("NAV-LINK", BASE, MOD_A, f"000N01:{BASE}"), row("NAV-LINK", BASE, MOD_A, f"000N01:{BASE}")]
        for i_, r_ in enumerate(links):
            r_["Summary"] = f"navmesh 000N01 triangle {i_} links to a removed triangle"
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): p_rows, (STATE, "without-b"): [],
                   (STATE, "without-a"): [], (NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): links,
                   (NAVOWN, "without-b"): [], (NAVOWN, "without-a"): []}
        plan, md, html = compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])
        ln = next((x for x in md.splitlines() if "against base-game objects" in x), "")
        title_ = (priorities()["reader"]["checkTitles"].get(STATE) or STATE)
        ok = (f"2 clashes ({title_})" in ln and "3 clashes" not in ln
              and re.search(r"(?<![\d,])2 [^;]*\((Navmesh|geometry\.navmesh)", ln) is not None)
        return ok, f"line {ln[:200]!r}"

    # C630 (Clockwork review 5, P53). "Whether it also needs X was not measured" on every item is said once on the page,
    # for the items it applies to; the plan keeps each item's full sentence
    def c630_with_target_said_once():
        O2 = "Second.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A, MOD_B])]
        p_rows = [lost_row(OTHER, MOD_A, f"00C301:{BASE}", "Owner", "o", "a", "v"),
                  lost_row(O2, MOD_A, f"00C302:{BASE}", "Owner", "s", "a", "v")]
        plan, md, html = cw_bundle(p_rows, arms=arms)
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        its = (reader(plan).get("prediction") or {}).get("items") or []
        n_md = sec.count("was not measured, because no setup without")
        ok = (len(its) == 2 and n_md == 1 and all("was not measured" in i["plain"] for i in its)
              and "For every item above" in sec)
        return ok, f"said {n_md} times on the page; items {len(its)}"

    # C631 (Pilgrim's second blind review, S1). The scope line's "main recorded conflicts" names only mods with a counted
    # clash; a mod whose only rows are set aside (here two values naming the same file) is not named there
    def c631_scope_names_counted_partners():
        CASE = "CaseOnly.esp"
        arms = [arm("baseline", [BASE, OTHER, CASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, CASE, MOD_A, MOD_B])]
        p_rows = [lost_row(OTHER, MOD_A, f"00C311:{BASE}", "Owner", "o", "a", "v")] + [
            lost_row(CASE, MOD_A, f"00C31{i}:{BASE}", "Model", "Meshes\\X\\A.nif", "meshes/x/a.nif", "v") for i in (2, 3)]
        plan, md, html = cw_bundle(p_rows, arms=arms)
        line = reader(plan).get("scopeLine") or ""
        ok = OTHER in line and CASE not in line and "main recorded conflicts" in line
        return ok, f"scope {line[:200]!r}"

    # C632 (coordinator D2 on lane cw). The patch-wins line in the detail applies the same set-aside rules as the
    # prediction: a row the patch wins whose change is only in unused bytes is not counted there, and is said apart
    def c632_patch_wins_line_sets_noise_aside():
        G9, O9 = f"00C321:{BASE}", f"00C322:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, O9, "Owner", "o", "a", "v")]
        w_rows = [lost_row(MOD_B, PATCH, G9, "Grid", ["Grid.Flags=327680"], PH, ["Grid.Flags=5504256"]),
                  lost_row(OTHER, PATCH, O9, "Owner", "o", "p", "v")]
        for r_ in p_rows + w_rows:
            r_["Summary"] = r_["Summary"].replace("PlacedObject", "Cell")
        plan, md, _ = cw_bundle(p_rows, w_rows, check=CELLATTR)
        ln = next((x for x in md.splitlines() if "where a declared patch's value wins" in x), "")
        # (fourth review) wording; (round 3, finding 7) the row set aside said in the line's one unit, rows
        ok = "1 record field, lost by 1 plugin pair in all," in ln and "1 counted, 1 with no change in game" in ln
        return ok, f"line {ln[:240]!r}"

    for name, fn in [("w2-cw/C620-no-change-never-dropped", c620_no_change_never_dropped),
                     ("w2-cw/C621-neither-lost-is-resolved", c621_neither_lost_is_resolved),
                     ("w2-cw/C622-navmesh-data-judged-by-navmesh-check", c622_navmesh_data_judged_by_navmesh_check),
                     ("w2-cw/C623-base-value-not-own-edit", c623_base_value_not_own_edit),
                     ("w2-cw/C624-carried-list-not-lost", c624_carried_list_not_lost),
                     ("w2-cw/C625-order-only-not-lost", c625_order_only_not_lost),
                     ("w2-cw/C626-unused-bits-not-lost", c626_unused_bits_not_lost),
                     ("w2-cw/C627-order-line-names-the-move", c627_order_line_names_the_move),
                     ("w2-cw/C628-patch-wins-fields-once", c628_patch_wins_fields_once),
                     ("w2-cw/C629-base-game-state-pairs-once", c629_base_game_state_pairs_once),
                     ("w2-cw/C630-with-target-said-once", c630_with_target_said_once),
                     ("w2-cw/C631-scope-names-counted-partners", c631_scope_names_counted_partners),
                     ("w2-cw/C632-patch-wins-line-sets-noise-aside", c632_patch_wins_line_sets_noise_aside)]:
        cell(name, fn)

    # ------------------------------------------- window 2, lane core: one function decides whose change a value is, one
    # decides what is new with a mod (Pilgrim's and Clockwork's blind reviews failed on each applied in some paths only)
    # C660 (Pilgrim review B1). Whose change a value is: ModA's water height is its masters' (the base game's) value;
    # OtherMod, loaded before it and not its master, changed it, and ModB puts OtherMod's value back. ModA made no change
    # of its own there, so nothing of ModA's is lost: set aside as no edit of ModA's (it only undoes OtherMod's), never a
    # clash. ModA's own change to another cell stays counted.
    def c660_masters_value_is_no_own_change():
        W1, W2 = f"0216B1:{BASE}", f"0216B2:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, W1, "WaterHeight", "0", "-2147483600", "-2147483600", before=OTHER),
                  lost_row(MOD_A, MOD_B, W2, "WaterHeight", "5", "-2147483600", "-2147483600", before=OTHER)]
        chains = [(fk, "Cell", [(BASE, {"WaterHeight": "0"}), (OTHER, {"WaterHeight": "-2147483600"}),
                                (MOD_A, {"WaterHeight": v}), (MOD_B, {"WaterHeight": "-2147483600"})])
                  for fk, v in ((W1, "0"), (W2, "5"))]
        masters = {OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, MOD_A, MOD_B]}
        plan, md, _ = core_bundle(p_rows, check=CELLATTR, masters=masters, p_chains=chains)
        e1, e2 = rec_of(plan, W1), rec_of(plan, W2)
        ok = ([e["admission"] for e in e1] == ["notOwnEdit"] and [e.get("undoes") for e in e1] == [OTHER]
              and [e["admission"] for e in e2] == ["admitted"])
        return ok, f"W1 {[(e['admission'], e.get('undoes')) for e in e1]}; W2 {[e['admission'] for e in e2]}"

    # C661. The value a plugin inherits is its nearest master's, not the plugin's before it: a patch built on OtherMod
    # that keeps OtherMod's value where ThirdMod (not its master) changed it makes no change of its own; with only the
    # base game as its master the same value is its own change.
    def c661_nearest_master_decides():
        F1 = f"00C661:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, THIRD, MOD_A, MOD_B])]
        p_rows = [lost_row(MOD_A, MOD_B, F1, "Owner", "2", "4", "3", before=THIRD)]
        chains = [(F1, "PlacedObject", [(BASE, {"Owner": "1"}), (OTHER, {"Owner": "2"}), (THIRD, {"Owner": "3"}),
                                        (MOD_A, {"Owner": "2"}), (MOD_B, {"Owner": "4"})])]
        got = []
        for m_a in ([BASE, OTHER], [BASE]):
            plan, _, _ = core_bundle(p_rows, arms=arms, p_chains=chains,
                                     masters={OTHER: [BASE], THIRD: [BASE], MOD_A: m_a, MOD_B: [BASE]})
            got.append([e["admission"] for e in rec_of(plan, F1)])
        return got == [["notOwnEdit"], ["admitted"]], f"masters [Base, Other] {got[0]}; [Base] {got[1]}"

    # C662 (Pilgrim review S6). A patch that takes ModA's value drops ModB's change only where ModB made one: ModB's
    # value is its master's (the base game's) though OtherMod changed it before, so nothing of ModB's is dropped
    # (resolved); where ModB's value is its own, the patch drops it (switched).
    def c662_dropped_only_an_own_change():
        S1, S2 = f"00C621:{BASE}", f"00C622:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, S1, "Placement", "a1", "v1", "o1", before=OTHER),
                  lost_row(MOD_A, MOD_B, S2, "Placement", "a2", "b2", "o2", before=OTHER)]
        w_rows = [lost_row(MOD_B, PATCH, S1, "Placement", "v1", "a1", "a1"),
                  lost_row(MOD_B, PATCH, S2, "Placement", "b2", "a2", "a2")]
        chains = [(S1, "PlacedObject", [(BASE, {"Placement": "v1"}), (OTHER, {"Placement": "o1"}),
                                        (MOD_A, {"Placement": "a1"}), (MOD_B, {"Placement": "v1"})]),
                  (S2, "PlacedObject", [(BASE, {"Placement": "v2"}), (OTHER, {"Placement": "o2"}),
                                        (MOD_A, {"Placement": "a2"}), (MOD_B, {"Placement": "b2"})])]
        masters = {OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, MOD_A, MOD_B]}
        plan, md, _ = core_bundle(p_rows, w_rows, masters=masters, p_chains=chains)
        w1, w2 = [e.get("withPatch") for e in rec_of(plan, S1)], [e.get("withPatch") for e in rec_of(plan, S2)]
        dropped = [ln for ln in md.splitlines() if "'s change on " in ln]     # after-review: reworded (Pilgrim 2)
        ok = w1 == ["resolved"] and w2 == ["switched"] and dropped and all("change on 2" not in ln for ln in dropped)
        return ok, f"S1 {w1}, S2 {w2}; {[ln[:120] for ln in dropped]}"

    # C663. "Undone by the patch: N changes the mods made" counts only changes a mod made: ModA's value lost to the patch
    # is its master's (the base game's), so the patch undoes nothing of ModA's there; ModB's own change it undoes is
    # counted.
    def c663_undone_counts_own_changes():
        U1, U2 = f"00C631:{BASE}", f"00C632:{BASE}"
        arms = [arm("baseline", [BASE, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, MOD_B])]
        w_rows = [lost_row(MOD_A, PATCH, U1, "Owner", "1", "7", "5", before=THIRD),
                  lost_row(MOD_B, PATCH, U2, "Owner", "8", "1", "1", before=MOD_A)]   # put back (eighth review)
        w_chains = [(U1, "PlacedObject", [(BASE, {"Owner": "1"}), (THIRD, {"Owner": "5"}), (MOD_A, {"Owner": "1"}),
                                          (PATCH, {"Owner": "7"})]),
                    (U2, "PlacedObject", [(BASE, {"Owner": "1"}), (MOD_A, {"Owner": "1"}), (MOD_B, {"Owner": "8"}),
                                          (PATCH, {"Owner": "1"})])]
        masters = {THIRD: [BASE], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, MOD_A, MOD_B]}
        p_rows = [lost_row(OTHER, MOD_A, f"00C633:{BASE}", "Owner", "o", "a", "v")]
        plan, md, _ = core_bundle(p_rows, w_rows, arms=arms, masters=masters, w_chains=w_chains)
        v = reader(plan).get("validation") or {}
        ok = v.get("undone") == 1 and v.get("undoneBy") == {MOD_B: 1}
        return ok, f"undone {v.get('undone')} by {v.get('undoneBy')}; {v.get('undoneSentence')!r}"

    # C670 (Clockwork review B1, records). New with ModA is decided field by field on the same setup with and without
    # ModA, whoever wins: OtherMod's change is already lost without ModA (to ModB), so it is not a clash ModA brings,
    # however the pair is labelled; ModB's change, which wins without ModA, is.
    def c670_already_lost_whoever_wins():
        R1, R2 = f"00C701:{BASE}", f"00C702:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"),
                arm("without-a", [BASE, OTHER, MOD_B])]
        p_rows = [lost_row(OTHER, MOD_A, R1, "Owner", "o", "a", "v"),
                  lost_row(MOD_B, MOD_A, R2, "Owner", "b", "a", "v")]
        w_rows = [lost_row(OTHER, MOD_B, R1, "Owner", "o", "b", "v")]
        plan, _, _ = core_bundle(p_rows, w_rows, arms=arms, patches=(), p_arm="baseline", w_arm="without-a")
        got = (rec_of(plan, R1)[0]["admission"], rec_of(plan, R2)[0]["admission"])
        return got == ("alreadyPresent", "admitted"), f"R1, R2 {got}"

    # C671 (Clockwork review B1, cells). The same rule for a cell's fields: Grand Solitude's occlusion value already lost
    # without ModA (to Landscape Fixes) is not new with ModA.
    def c671_cell_field_already_lost():
        C1 = f"00927C:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"),
                arm("without-a", [BASE, OTHER, MOD_B])]
        p_rows = [lost_row(OTHER, MOD_A, C1, "OcclusionData", "o", "a", "v")]
        w_rows = [lost_row(OTHER, MOD_B, C1, "OcclusionData", "o", "b", "v")]
        plan, _, _ = core_bundle(p_rows, w_rows, check=CELLATTR, arms=arms, patches=(), p_arm="baseline",
                                 w_arm="without-a")
        got = [e["admission"] for e in rec_of(plan, C1)]
        return got == ["alreadyPresent"], f"{got}"

    # C672. A geometry row of one object (no partner object) is the same object in the same state of clash with and
    # without ModA whichever plugins the probe names on each setup: already there, not new with ModA.
    def c672_object_row_whoever_named():
        SG = "state.conditional-geometry"
        kind = f"STATE-ROUTE@014656:{BASE}@Triggered"
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"),
                arm("without-a", [BASE, OTHER, MOD_B])]
        p = [row(kind, MOD_A, OTHER, f"190A95:{BASE}")]
        w = [row(kind, MOD_B, OTHER, f"190A95:{BASE}")]
        plan, _, _ = compile_and_render(arms, {(SG, "baseline"): p, (SG, "without-a"): w}, [MOD_A])
        got = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []
               if e["checkId"] == SG]
        return got == ["alreadyPresent"], f"{got}"

    # C673. "Does it need ModB?" is the same comparison: ModA's change lost to ModB is lost without ModB too (to
    # OtherMod), so it does not need ModB.
    def c673_needs_partner_whoever_wins():
        R3 = f"00C703:{BASE}"
        arms = [arm("baseline", [BASE, MOD_A, OTHER, MOD_B], kind="baseline"),
                arm("without-b", [BASE, MOD_A, OTHER])]
        p_rows = [lost_row(MOD_A, MOD_B, R3, "Owner", "a", "b", "v")]
        w_rows = [lost_row(MOD_A, OTHER, R3, "Owner", "a", "o", "v")]
        plan, _, _ = core_bundle(p_rows, w_rows, arms=arms, patches=(), p_arm="baseline", w_arm="without-b")
        got = [e.get("attribution") for e in rec_of(plan, R3)]
        return got == ["single-mod"], f"{got}"

    # C674. A load-order risk is a change the other order loses that the measured order does not: OtherMod's change is
    # lost in the measured order too (to ThirdMod), so moving ModA after it is no new risk for that field.
    def c674_order_risk_whoever_wins():
        O1 = f"000C74:{BASE}"
        arms = [arm("baseline", [BASE, MOD_A, OTHER, THIRD], kind="baseline")]
        p_rec = [lost_row(OTHER, THIRD, O1, "Lighting", "o", "t", "v")]
        o_rows = [dict(lost_row(OTHER, MOD_A, O1, "Lighting", "o", "a", "v"), _check=CELLATTR, _type="Cell")]
        setup = {"armId": "late", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": plugins(BASE, OTHER, THIRD, MOD_A), "rows": o_rows}
        plan, _, _ = compile_and_render(arms, {(CONTACT, "baseline"): []}, [MOD_A],
                                        count_only={(CELLATTR, "baseline"): 1},
                                        enrichment={(CELLATTR, "baseline"): p_rec}, order_setups=[setup])
        lines = [x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or []]
        return lines == [], f"lines {lines}"

    # C675. New with the patch is the same comparison: OtherMod's change lost to ModB with the patch was already lost
    # without the patch (to ModA), so it is no new clash of the patch's.
    def c675_new_with_patch_whoever_wins():
        N1 = f"00C705:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, N1, "Owner", "o", "a", "v")]
        w_rows = [lost_row(OTHER, MOD_B, N1, "Owner", "o", "b", "v")]
        plan, md, _ = core_bundle(p_rows, w_rows)
        v = reader(plan).get("validation") or {}
        return v.get("patchProblems") == 0, f"new with the patch {v.get('patchProblems')}"

    # C676 (Missives review, blocking 2). Whose change a water seam is: the probe names the two cells' plugins as a pair,
    # but where only one side differs from the base game's water (attributed to side A or B), the other side's plugin
    # carries the base game's value and made no change there: the seam is one mod against the base game, said in the
    # base-game line, never a clash between the two mods. A seam both sides make stays a clash between mods.
    def c676_one_sided_water_seam_is_base_game():
        WB = "environment.water-boundaries"
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]

        def seam(fk, other, side):
            r_ = row("WATER-SEAM", OTHER, MOD_A, fk, other=other)
            r_["Summary"] = (f"Tamriel (1,1) E Tamriel (2,1): joined under the truth (001232:Update.esm z -14000), apart "
                             f"under the winner (105CC3:Skyrim.esm z -14000 | 001232:Update.esm z -14000) — "
                             f"attributed to {side}")
            return r_
        p = [seam(f"009301:{BASE}", f"009302:{BASE}", "side A"), seam(f"009303:{BASE}", f"009304:{BASE}", "both sides")]
        plan, md, _ = compile_and_render(arms, {(WB, "baseline"): p, (WB, "without-a"): []}, [MOD_A])
        got = {e["formKey"][:6]: e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []
               if e["checkId"] == WB}
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        # after the final round (Missives review 1): heights equal on both sides, so it is said as the water type
        ok = (got == {"009301": "baseGame", "009303": "admitted"} and MOD_A in top
              and "1 cell boundaries where the water type no longer matches" in top)
        return ok, f"{got}; base-game line {top[:200]!r}"

    # ------------------------------------------- window 2, lane final: Pilgrim B1, Missives B2 and B1, Clockwork S6
    UPD = "Update.esm"

    def cond(fn_, flags="0"):
        return {"Data.Function": fn_, "Flags": flags, "CompareOperator": "EqualTo", "ComparisonValue": "1"}

    def conditions(*items):
        return {f"Conditions[{i}].{k}": v for i, it in enumerate(items) for k, v in it.items()}

    # C720 (Pilgrim review B1). A list is compared item by item: OtherMod adds IsUndead (and sets OR on the first
    # condition); ModA's winning list keeps both and adds its own HasPerk, so nothing of OtherMod's is lost (set aside,
    # said); where ModA's list drops OtherMod's IsUndead, OtherMod's change is lost and counted.
    def c720_list_item_kept_is_not_lost():
        K1, K2 = f"00C720:{BASE}", f"00C721:{BASE}"
        base_, other_ = conditions(cond("HasKeyword")), conditions(cond("HasKeyword", "OR"), cond("IsUndead"))
        chains = [(K1, "MagicEffect", [(BASE, base_), (OTHER, other_),
                                       (MOD_A, conditions(cond("HasKeyword", "OR"), cond("HasPerk", "OR"), cond("IsUndead")))]),
                  (K2, "MagicEffect", [(BASE, base_), (OTHER, other_),
                                       (MOD_A, conditions(cond("HasKeyword", "OR"), cond("HasPerk")))])]
        p_rows = [lost_row(OTHER, MOD_A, fk, "Conditions", "o", "a", "v", before=BASE) for fk in (K1, K2)]
        plan, md, _ = core_bundle(p_rows, p_chains=chains, masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        got = (rec_of(plan, K1)[0]["admission"], rec_of(plan, K2)[0]["admission"])
        said = "item by item" in md
        return got == ("listKept", "admitted") and said, f"K1, K2 {got}; said {said}"

    # C721 (Pilgrim review B1). A counted lost change whose winner changed the field itself too says so, with what each
    # side has, never only "lost to"; one the winner did not change (it carries the earlier value) is not said so.
    def c721_winner_also_changed_says_both_values():
        D1, D2 = f"00C722:{BASE}", f"00C723:{BASE}"
        chains = [(D1, "MagicEffect", [(BASE, {"Description": "v1"}), (OTHER, {"Description": "o1"}),
                                       (MOD_A, {"Description": "a1"})]),
                  (D2, "MagicEffect", [(BASE, {"Description": "v2"}), (OTHER, {"Description": "o2"}),
                                       (MOD_A, {"Description": "v2"})])]
        p_rows = [lost_row(OTHER, MOD_A, D1, "Description", "o1", "a1", "v1", before=BASE),
                  lost_row(OTHER, MOD_A, D2, "Description", "o2", "v2", "v2", before=BASE)]
        plan, md, _ = core_bundle(p_rows, p_chains=chains, masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "OtherMod.esp's value is lost" in ln), "")
        # after-review (Missives review 3): a small item also states each side's value per record in its own sentence;
        # the winner-changed-it-too sentence is the one that must not quote the row the winner did not change
        own_ = next((x for x in line.split(". ") if "changed the field itself too" in x), "")
        ok = (f"In 1 of them {MOD_A} changed the field itself too" in line and '"o1"' in own_ and '"a1"' in own_
              and '"o2"' not in own_)
        return ok, line[-300:]

    # C722 (Missives review B2). Whose change a value is, against the nearest master that carries the record: ModA lists
    # Update.esm, which sets the water type; ModA's value is the one from before Update.esm's change, so ModA changed it
    # (it puts the earlier value back), not the base game's value. Coordinator D1 (with P63): undoing its own master's
    # fix is said on its own line, not counted as a clash between mods. ModA's water set to a value of its own is a
    # change to the base game's value, set aside as before.
    def c722_putting_back_masters_earlier_value_is_counted():
        W1, W2 = f"00C724:{BASE}", f"00C725:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        chains = [(W1, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})]),
                  (W2, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "0AAAAA:ModA.esp"})])]
        p_rows = [lost_row(UPD, MOD_A, W1, "Water", ["Water=001232:Update.esm"], PH, PH),
                  lost_row(UPD, MOD_A, W2, "Water", ["Water=001232:Update.esm"], PH, PH)]
        w_rows = list(p_rows)
        plan, md, _ = core_bundle(p_rows, w_rows, check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        got = (rec_of(plan, W1)[0]["admission"], rec_of(plan, W2)[0]["admission"])
        top = md.split("\n## ", 2)[1] if "\n## " in md else md
        said = (f"{MOD_A} undoes its master {UPD}'s fix in 1 record field" in top
                and f"of {UPD}'s changes lost to {MOD_A}" not in top and "clashes between their objects" not in md)
        return got == ("undoesMaster", "baseGame") and said, f"W1, W2 {got}; said {said}"

    # C723 (Missives review B2, the same rule for a mod's master that is not the base game): ModA lists OtherMod as a
    # master and puts back the value from before OtherMod's fix: said as undoing its master's fix (coordinator D1), not
    # as "a plugin's value over its own master's".
    def c723_putting_back_mod_masters_earlier_value_is_counted():
        M1 = f"00C726:{BASE}"
        chains = [(M1, "PlacedObject", [(BASE, {"Placement": "v"}), (OTHER, {"Placement": "o"}),
                                        (MOD_A, {"Placement": "v"})])]
        p_rows = [lost_row(OTHER, MOD_A, M1, "Placement", "o", "v", "v", before=BASE)]
        plan, md, _ = core_bundle(p_rows, p_chains=chains, masters={OTHER: [BASE], MOD_A: [BASE, OTHER], MOD_B: [BASE]})
        got = [e["admission"] for e in rec_of(plan, M1)]
        said = f"{MOD_A} undoes its master {OTHER}'s fix" in md
        return got == ["undoesMaster"] and said, f"{got}; said {said}"

    # C724 (Missives review B2). A one-sided water seam on a cell where the mod puts back the water from before its
    # master's change is said as that, never as the mod against the base game's value on the other side.
    def c724_seam_on_put_back_cell_said_so():
        WB = "environment.water-boundaries"
        W1 = f"009311:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        chains = [(W1, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})])]
        p_rows = [lost_row(UPD, MOD_A, W1, "Water", ["Water=001232:Update.esm"], PH, PH)]
        s_ = row("WATER-SEAM", OTHER, MOD_A, W1, other=f"009312:{BASE}")
        s_["Summary"] = ("Tamriel (1,1) E Tamriel (2,1): joined under the truth (001232:Update.esm z -14000), apart under "
                         "the winner (000018:Skyrim.esm z -14000 | 001232:Update.esm z -14000) — attributed to side B")
        extra = {(WB, "without-patch"): [s_], (WB, "baseline"): [s_]}
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains, extra_results=extra,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if "cell boundar" in ln.lower() and ln.startswith("- Measured")), "")
        ok = f"undoes its master {UPD}'s fix" in line and "base game's value on the other side" not in line
        return ok, line[:300]

    # C725 (Missives review B1). Each row's status is decided before rows with the same lost value are counted once:
    # the base game's Location (HearthFires) and OtherMod's own Location, both lost to ModA, are not folded into the
    # base-game row and set aside with it: OtherMod's change is counted, and said still there with the patch.
    def c725_status_before_counting_once():
        HF = "HearthFires.esm"
        L1 = f"00C727:{BASE}"
        arms = [arm("baseline", [BASE, HF, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})])]
        p_rows = [lost_row(HF, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        got = {e["A"]: e["admission"] for e in rec_of(plan, L1)}
        # (third review, Missives S1) OtherMod only carries HearthFires' own change (it is built without HearthFires): the
        # loss is ModA's against HearthFires, said once on its own line, not OtherMod's change counted as a clash
        line = next((ln for ln in md.splitlines() if f"wins over {HF}'s own change of" in ln), "")
        ok = got == {HF: "baseGame", OTHER: "notOwnEdit"} and OTHER in line and MOD_A in line
        return ok, f"{got}; {line[:200]!r}"

    # C726 (Clockwork review S6). "Undone by the patch" applies the same set-aside rules and counts a field once: a list
    # whose items the patch only reorders is no change undone, and a field two record checks both list is one field.
    def c726_undone_same_set_asides_once():
        U1, U2 = f"00C728:{BASE}", f"00C729:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        reg = lost_row(OTHER, PATCH, U1, "Regions", ["Regions: same 2 item(s), ORDER DIFFERS from ModA - ModB patch.esp"],
                       PH, PH)
        own_ = lost_row(OTHER, PATCH, U2, "Owner", "o", "p", "p")      # put back (eighth review, C1179's rule)
        enr = {(RECORDS, "without-patch"): [lost_row(MOD_B, MOD_A, f"00C72A:{BASE}", "Owner", "b", "a", "v")],
               (RECORDS, "baseline"): [reg, own_], (CELLATTR, "without-patch"): [], (CELLATTR, "baseline"): [reg, own_]}
        count_only = {k: max(1, len(v)) for k, v in enr.items()}
        results = {(CONTACT, a_): [] for a_ in ("baseline", "without-patch")}
        plan, md, _ = compile_and_render(arms, results, [MOD_A], patches=[PATCH], count_only=count_only, enrichment=enr,
                                         masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, MOD_A, MOD_B]})
        v = reader(plan).get("validation") or {}
        return v.get("undone") == 1 and v.get("undoneBy") == {OTHER: 1}, f"undone {v.get('undone')} {v.get('undoneBy')}"

    # C727 (coordinator D1, with the P63 ruling). A mod putting back the value from before its OWN master's change undoes
    # that master's fix: a measured effect said on its own line ("ModA.esp undoes its master Update.esm's fix", the
    # count, examples, and whether it is still so with the patch), never a clash between mods in the headline or the
    # still-there line. Putting back the value from before a NON-master's change stays a clash between mods.
    def c727_undoing_own_masters_fix_is_its_own_line():
        W1, N1 = f"00C730:{BASE}", f"00C731:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        chains = [(W1, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})]),
                  (N1, "Cell", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (MOD_A, {"Owner": "v"})])]
        p_rows = [lost_row(UPD, MOD_A, W1, "Water", ["Water=001232:Update.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, N1, "Owner", "o", "v", "v", before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        got = (rec_of(plan, W1)[0]["admission"], rec_of(plan, N1)[0]["admission"])
        pred = reader(plan).get("prediction") or {}
        top = md.split("\n## ", 2)[1] if "\n## " in md else md
        line = next((ln for ln in top.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        ok = (got == ("undoesMaster", "admitted") and "Water" not in pred.get("verdict", "") and "Owner" in pred.get("verdict", "")
              and "1 record field" in line and "00C730" in line and "still" in line
              and f"of {UPD}'s changes lost to {MOD_A}" not in top)
        return ok, f"{got}; {line[:240]!r}"

    # C728 (coordinator D1). A one-sided water seam on a cell where the mod undoes its master's fix is said with that
    # line's words (the master named, the fix undone), never as counted above.
    def c728_seam_on_undone_cell_names_the_undone_fix():
        WB = "environment.water-boundaries"
        W1 = f"009321:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        chains = [(W1, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})])]
        p_rows = [lost_row(UPD, MOD_A, W1, "Water", ["Water=001232:Update.esm"], PH, PH)]
        s_ = row("WATER-SEAM", OTHER, MOD_A, W1, other=f"009322:{BASE}")
        s_["Summary"] = ("Tamriel (1,1) E Tamriel (2,1): joined under the truth (001232:Update.esm z -14000), apart under "
                         "the winner (000018:Skyrim.esm z -14000 | 001232:Update.esm z -14000) — attributed to side B")
        extra = {(WB, "without-patch"): [s_], (WB, "baseline"): [s_]}
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains, extra_results=extra,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if "cell boundar" in ln.lower() and ln.startswith("- Measured")), "")
        ok = f"{MOD_A} undoes its master {UPD}'s fix" in line and "counted above" not in line
        return ok, line[:300]

    for name, fn in [("w2-final/C727-undoing-own-masters-fix-is-its-own-line", c727_undoing_own_masters_fix_is_its_own_line),
                     ("w2-final/C728-seam-on-undone-cell-names-the-undone-fix", c728_seam_on_undone_cell_names_the_undone_fix)]:
        cell(name, fn)

    for name, fn in [("w2-final/C720-list-item-kept-is-not-lost", c720_list_item_kept_is_not_lost),
                     ("w2-final/C721-winner-also-changed-says-both-values", c721_winner_also_changed_says_both_values),
                     ("w2-final/C722-putting-back-masters-earlier-value-is-counted",
                      c722_putting_back_masters_earlier_value_is_counted),
                     ("w2-final/C723-putting-back-mod-masters-earlier-value-is-counted",
                      c723_putting_back_mod_masters_earlier_value_is_counted),
                     ("w2-final/C724-seam-on-put-back-cell-said-so", c724_seam_on_put_back_cell_said_so),
                     ("w2-final/C725-status-before-counting-once", c725_status_before_counting_once),
                     ("w2-final/C726-undone-same-set-asides-once", c726_undone_same_set_asides_once)]:
        cell(name, fn)

    for name, fn in [("w2-core/C676-one-sided-water-seam-is-base-game", c676_one_sided_water_seam_is_base_game),
                     ("w2-core/C660-masters-value-is-no-own-change", c660_masters_value_is_no_own_change),
                     ("w2-core/C661-nearest-master-decides", c661_nearest_master_decides),
                     ("w2-core/C662-dropped-only-an-own-change", c662_dropped_only_an_own_change),
                     ("w2-core/C663-undone-counts-own-changes", c663_undone_counts_own_changes),
                     ("w2-core/C670-already-lost-whoever-wins", c670_already_lost_whoever_wins),
                     ("w2-core/C671-cell-field-already-lost", c671_cell_field_already_lost),
                     ("w2-core/C672-object-row-whoever-named", c672_object_row_whoever_named),
                     ("w2-core/C673-needs-partner-whoever-wins", c673_needs_partner_whoever_wins),
                     ("w2-core/C674-order-risk-whoever-wins", c674_order_risk_whoever_wins),
                     ("w2-core/C675-new-with-patch-whoever-wins", c675_new_with_patch_whoever_wins)]:
        cell(name, fn)

    # ---------------------------------------- after the final round: a rebuilt record supersedes the loser's small fix
    # (Pilgrim final review B1). Decided from the record data alone: (a) the winner changed what the record is (a kind
    # field, skill data per record type) that the loser left alone; (b) the loser only seated the object (moved it
    # straight up or down, not turned) and the winner moved it across to another spot, at least ten times as far and at
    # least the object's size (64 u when its bounds are not read), without burying or disabling it.
    MASTERS_ = {OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]}

    def mgef(av, desc, name):
        return {"Archetype.ActorValue": av, "Archetype.Type": "PeakValueModifier", "Description": desc, "Name": name}

    def pl(x, y, z, rz="0"):
        return {"Placement.Position": f"{x},{y},{z}", "Placement.Rotation": f"0,0,{rz}"}

    def pl_row(fk, a, b, before, lost, win):
        def txt(v):
            return f"Position={v['Placement.Position']}; Rotation={v['Placement.Rotation']}"
        return lost_row(a, b, fk, "Placement", txt(lost), txt(win), txt(before), before=BASE)

    def superseded_line(md):
        top = md.split("\n## ", 2)[1] if "\n## " in md else md
        return next((ln for ln in top.splitlines() if "rebuilt" in ln), "")

    # C800: the winner turned the effect into another one (new actor value) that OtherMod left alone: OtherMod's text
    # describes the old effect. Set aside, out of the headline, said in plain words with the record facts.
    def c800_kind_change_supersedes():
        M = f"00C800:{BASE}"
        chains = [(M, "MagicEffect", [(BASE, mgef("Speech", "+<mag> Speechcraft.", "Fortify Persuasion")),
                                      (OTHER, mgef("Speech", "Increases Speech by <mag>.", "Fortify Persuasion")),
                                      (MOD_A, mgef("StaminaRateMult", "Stamina regenerates <mag>% faster.",
                                                   "Fortify Stamina Regeneration"))])]
        p_rows = [lost_row(OTHER, MOD_A, M, "Description", "Increases Speech by <mag>.",
                           "Stamina regenerates <mag>% faster.", "+<mag> Speechcraft.", before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, M)]
        pred = reader(plan).get("prediction") or {}
        line = superseded_line(md)
        ok = (got == ["superseded"] and "Description" not in pred.get("verdict", "") and "00C800" in line
              and "Speech" in line and "StaminaRateMult" in line and "Fortify Stamina Regeneration" in line
              and "old effect" in line)
        return ok, f"{got}; {line[:300]!r}"

    # C801: the winner rewrote the text but kept what the effect is: the records cannot decide whether OtherMod's text
    # still fits, so the row stays counted and the report says why.
    def c801_kind_kept_stays_counted_and_says_why():
        M = f"00C801:{BASE}"
        chains = [(M, "MagicEffect", [(BASE, mgef("Speech", "+<mag> Speechcraft.", "Fortify Persuasion")),
                                      (OTHER, mgef("Speech", "Increases Speech by <mag>.", "Fortify Persuasion")),
                                      (MOD_A, mgef("Speech", "Speech rises by <mag>.", "Fortify Speech"))])]
        p_rows = [lost_row(OTHER, MOD_A, M, "Description", "Increases Speech by <mag>.", "Speech rises by <mag>.",
                           "+<mag> Speechcraft.", before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, M)]
        said = "not decided from the records" in md and "still fits" in md
        return got == ["admitted"] and said, f"{got}; said {said}"

    # C802: OtherMod seats a candle 0.33 u lower; ModA moves it 97 u across to another spot (text reads).
    def c802_seat_then_relocation_supersedes():
        P = f"00C802:{BASE}"
        b_, o_, a_ = pl(-9153.3, 5547.1, -241.065), pl(-9153.3, 5547.1, -241.395), pl(-9170.2, 5452.3, -227.85)
        chains = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, P)]
        line = superseded_line(md)
        ok = got == ["superseded"] and "00C802" in line and "0.33 u" in line and "96 u" in line
        return ok, f"{got}; {line[:300]!r}"

    # C803: the same in a tree capture, where the winner's value is only in each difference's parenthesis.
    def c803_seat_then_relocation_tree_capture():
        P = f"00C803:{BASE}"
        b_, o_, a_ = pl(-9153.3, 5547.1, -241.065), pl(-9153.3, 5547.1, -241.395), pl(-9170.2, 5452.3, -227.85)
        p_rows = [lost_row(OTHER, MOD_A, P, "Placement", [f"Placement.Position={o_['Placement.Position']}"], PH,
                           [f"Placement.Position={b_['Placement.Position']}"])]
        plan, md, _ = core_bundle(p_rows, list(p_rows), masters=MASTERS_,
                                  p_tr=tree_transcript([(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]))
        got = [e["admission"] for e in rec_of(plan, P)]
        return got == ["superseded"], f"{got}"

    # C804 (the counter-example): the winner only flags the record and carries the earlier position; OtherMod's move is
    # a real lost change on a record the winner barely touched. Counted.
    def c804_winner_barely_touched_stays_counted():
        P = f"00C804:{BASE}"
        b_, o_ = pl(1561.1, 133.0, 0), pl(1561.1, 133.0, 12.0)
        a_ = dict(b_, SkyrimMajorRecordFlags="1024")
        chains = [(P, "PlacedObject", [(BASE, dict(b_, SkyrimMajorRecordFlags="0")),
                                       (OTHER, dict(o_, SkyrimMajorRecordFlags="0")), (MOD_A, a_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, b_)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, P)]
        return got == ["admitted"], f"{got}"

    # C805: the winner disables the object (below the world, or initially disabled): not a relocation. Counted.
    def c805_winner_disables_stays_counted():
        P1, P2 = f"00C805:{BASE}", f"00C806:{BASE}"
        b_, o_ = pl(100, 100, 0), pl(100, 100, -0.5)
        NOPARENT = {"EnableParent.Reference": "has no EnableParent.Reference", "EnableParent.Flags": "has no EnableParent.Flags"}
        # blind review (0A0315): off for good is below the world, or enabled opposite to the player
        a1, a2 = pl(400, 100, -30000), dict(pl(400, 100, 0), **{"EnableParent.Reference": "000014:Skyrim.esm",
                                                                 "EnableParent.Flags": "SetEnableStateToOppositeOfParent"})
        chains = [(P1, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a1)]),
                  (P2, "PlacedObject", [(BASE, dict(b_, **NOPARENT)), (OTHER, dict(o_, **NOPARENT)), (MOD_A, a2)])]
        p_rows = [pl_row(P1, OTHER, MOD_A, b_, o_, a1), pl_row(P2, OTHER, MOD_A, b_, o_, a2)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [rec_of(plan, x)[0]["admission"] for x in (P1, P2)]
        return got == ["admitted", "admitted"], f"{got}"

    # C806: OtherMod's change is no seat: it turns the object, or moves it sideways. Counted.
    def c806_turned_or_sideways_stays_counted():
        P1, P2 = f"00C807:{BASE}", f"00C808:{BASE}"
        b_ = pl(100, 100, 0)
        o1, o2, a_ = pl(100, 100, -0.5, rz="0.3"), pl(103, 100, 0), pl(600, 100, 0)
        chains = [(P1, "PlacedObject", [(BASE, b_), (OTHER, o1), (MOD_A, a_)]),
                  (P2, "PlacedObject", [(BASE, b_), (OTHER, o2), (MOD_A, a_)])]
        p_rows = [pl_row(P1, OTHER, MOD_A, b_, o1, a_), pl_row(P2, OTHER, MOD_A, b_, o2, a_)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [rec_of(plan, x)[0]["admission"] for x in (P1, P2)]
        return got == ["admitted", "admitted"], f"{got}"

    # C807: the winner moves the object under ten times as far as OtherMod's seat: it stands near the spot OtherMod
    # fixed. Counted.
    def c807_winner_under_ten_times_stays_counted():
        P = f"00C809:{BASE}"
        b_, o_, a_ = pl(100, 100, 0), pl(100, 100, -12.6), pl(186, 100, 0)
        chains = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, P)]
        return got == ["admitted"], f"{got}"

    # C808: the winner buries the object (moved down farther than across): hiding it, not a new spot. Counted.
    def c808_winner_buries_stays_counted():
        P = f"00C80A:{BASE}"
        b_, o_, a_ = pl(100, 100, -9027), pl(100, 100, -9046.6), pl(2375, 0, -29576)
        chains = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, P)]
        return got == ["admitted"], f"{got}"

    # C809 (coordinator, plan gate): an object moved less than its own size sits on the same spot, so the seat may still
    # apply: a 0.04 u seat against a 0.5 u move stays counted (64 u when the object's bounds are not read); with its
    # bounds read (half-extent 200 u) a 150 u move stays counted, and a 300 u move supersedes.
    def c809_winner_within_object_size_stays_counted():
        P1, P2, P3 = f"00C80B:{BASE}", f"00C80C:{BASE}", f"00C80D:{BASE}"
        b_, o_ = pl(100, 100, 0), pl(100, 100, -0.04)
        a1 = pl(100.5, 100, 0)
        b2, o2 = pl(100, 100, 0), pl(100, 100, -10)
        a2, a3 = pl(250, 100, 0), pl(400, 100, 0)
        chains = [(P1, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a1)]),
                  (P2, "PlacedObject", [(BASE, b2), (OTHER, o2), (MOD_A, a2)]),
                  (P3, "PlacedObject", [(BASE, b2), (OTHER, o2), (MOD_A, a3)])]
        p_rows = [pl_row(P1, OTHER, MOD_A, b_, o_, a1), pl_row(P2, OTHER, MOD_A, b2, o2, a2),
                  pl_row(P3, OTHER, MOD_A, b2, o2, a3)]
        big = f"0BB001:{BASE}"
        names = {"schema": "names-v1", "places": {}, "objects": {}, "mods": {}, "missing": [],
                 "refs": {P2: {"base": big, "carriedBy": [BASE]}, P3: {"base": big, "carriedBy": [BASE]}},
                 "bases": {big: {"type": "Static", "editorId": "BigRock", "model": "rock.nif",
                                 "bounds": {"first": "-200,-150,-50", "second": "200,150,50"}}}}
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_, names=names)
        got = [rec_of(plan, x)[0]["admission"] for x in (P1, P2, P3)]
        return got == ["admitted", "admitted", "superseded"], f"{got}"

    for name, fn in [("after-final/C800-kind-change-supersedes", c800_kind_change_supersedes),
                     ("after-final/C801-kind-kept-stays-counted-and-says-why", c801_kind_kept_stays_counted_and_says_why),
                     ("after-final/C802-seat-then-relocation-supersedes", c802_seat_then_relocation_supersedes),
                     ("after-final/C803-seat-then-relocation-tree-capture", c803_seat_then_relocation_tree_capture),
                     ("after-final/C804-winner-barely-touched-stays-counted", c804_winner_barely_touched_stays_counted),
                     ("after-final/C805-winner-disables-stays-counted", c805_winner_disables_stays_counted),
                     ("after-final/C806-turned-or-sideways-stays-counted", c806_turned_or_sideways_stays_counted),
                     ("after-final/C807-winner-under-ten-times-stays-counted", c807_winner_under_ten_times_stays_counted),
                     ("after-final/C808-winner-buries-stays-counted", c808_winner_buries_stays_counted),
                     ("after-final/C809-winner-within-object-size-stays-counted",
                      c809_winner_within_object_size_stays_counted)]:
        cell(name, fn)
    # ---------------------------------------- after the final round, group A: whose change a row is, and what disables
    HF_, CC_ = "HearthFires.esm", "ccFish.esm"

    # C810 (Clockwork review S1): OtherMod carries Update.esm's Location fix (a CC master before it had put the vanilla
    # value back); ModA puts back the vanilla value over its master Update.esm. One value is lost: said once, on the
    # line about undoing its master's fix, never also counted as OtherMod's clash.
    def c810_value_carried_from_undone_master_is_said_once():
        L = f"00C810:{BASE}"
        arms = [arm("baseline", [BASE, UPD, CC_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, CC_, OTHER, MOD_A, MOD_B])]
        chains = [(L, "Cell", [(BASE, {"Location": "022646:Skyrim.esm"}), (UPD, {"Location": "018A58:Skyrim.esm"}),
                               (CC_, {"Location": "022646:Skyrim.esm"}), (OTHER, {"Location": "018A58:Skyrim.esm"}),
                               (MOD_A, {"Location": "022646:Skyrim.esm"})])]
        p_rows = [lost_row(UPD, MOD_A, L, "Location", ["Location=018A58:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L, "Location", ["Location=018A58:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={UPD: [BASE], CC_: [BASE], OTHER: [BASE, UPD, CC_], MOD_A: [BASE, UPD],
                                           MOD_B: [BASE]})
        got = {e["A"]: e["admission"] for e in rec_of(plan, L)}
        pred = reader(plan).get("prediction") or {}
        # (third review, Missives S1) OtherMod's value is Update.esm's own change it carries: no edit of OtherMod's; the
        # loss is still said once, on the undoes-its-master line, and no base-fix line repeats it
        ok = (got == {UPD: "undoesMaster", OTHER: "notOwnEdit"} and "Location" not in pred.get("verdict", "")
              and "own change of" not in md and f"undoes its master {UPD}'s fix" in md)
        return ok, f"{got}; verdict {pred.get('verdict', '')[:160]!r}"

    # C811 (Clockwork review S2/S3, coordinator D on S3): what disables an object is read from each side's whole
    # record, not only the row's field. Both sides disable it (the winner by its flags): set aside, and never "still
    # brings back". Only the winner disables it: counted, with the sentence that the winning version disables it.
    def c811_disabled_by_the_whole_record():
        D1, D2 = f"00C811:{BASE}", f"00C812:{BASE}"
        base_ = {"Placement.Position": "100,100,0", "Placement.Rotation": "0,0,0", "SkyrimMajorRecordFlags": "0"}
        gone_ = dict(base_, **{"Placement.Position": "100,100,-30000"})
        moved_ = dict(base_, **{"Placement.Position": "300,100,0"})
        # the 2.0 tree answers say an absent enable parent in words ("has no EnableParent.Reference")
        off_ = dict(base_, **{"Placement.Position": "100,100,-14095", "SkyrimMajorRecordFlags": "InitiallyDisabled",
                              "EnableParent.Reference": "has no EnableParent.Reference"})
        chains = [(D1, "PlacedObject", [(BASE, base_), (OTHER, gone_), (MOD_A, off_)]),
                  (D2, "PlacedObject", [(BASE, base_), (OTHER, moved_), (MOD_A, off_)])]

        def txt(v):
            return f"Position={v['Placement.Position']}; Rotation=0,0,0"
        p_rows = [lost_row(OTHER, MOD_A, D1, "Placement", txt(gone_), txt(off_), txt(base_), before=BASE),
                  lost_row(OTHER, MOD_A, D2, "Placement", txt(moved_), txt(off_), txt(base_), before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = (rec_of(plan, D1)[0]["admission"], rec_of(plan, D2)[0]["admission"])
        # blind review (Winterhold 0A0315): ModA's version is only initially disabled, with no parent: it starts off and
        # a script or quest may enable it, so neither row is set aside and the items say it starts disabled
        said = "starts disabled" in md
        return got == ("admitted", "admitted") and said, f"{got}; said {said}"

    # C812 (BPT review S1): a patch's value lost to another mod's that names the same file in other letter case is no
    # new clash with the patch; a different file is.
    def c812_path_case_is_no_new_clash_with_the_patch():
        N0, W1, W2 = f"00C813:{BASE}", f"00C814:{BASE}", f"00C815:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, N0, "Owner", "o", "a", "v")]
        same_ = lost_row(PATCH, OTHER, W1, "HdLodDiffuseTexture", "data\\textures\\landscape\\a.dds",
                         "Data\\Textures\\Landscape\\A.dds", "Data\\Textures\\Landscape\\A.dds")
        diff_ = lost_row(PATCH, OTHER, W2, "HdLodDiffuseTexture", "data\\textures\\landscape\\b.dds",
                         "Data\\Textures\\Landscape\\A.dds", "Data\\Textures\\Landscape\\A.dds")
        plan, md, _ = core_bundle(p_rows, list(p_rows) + [same_, diff_])
        v = reader(plan).get("validation") or {}
        return v.get("patchProblems") == 1, f"new with the patch {v.get('patchProblems')}"

    # C813 (Missives review 3): OtherMod's value is HearthFires.esm's, which it only carries (no edit of its own), and
    # ModA's winning value is ThirdMod's: the earlier HearthFires change is still lost, so the value in game changes.
    # Never "nothing is lost in game"; said with the field.
    def c813_carried_value_still_changes_in_game():
        W = f"00C816:{BASE}"
        arms = [arm("baseline", [BASE, HF_, THIRD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, THIRD, OTHER, MOD_A, MOD_B])]
        chains = [(W, "Cell", [(BASE, {"WaterHeight": "0"}), (HF_, {"WaterHeight": "3.4028235E+38"}),
                               (THIRD, {"WaterHeight": "-2147483648"}), (OTHER, {"WaterHeight": "3.4028235E+38"}),
                               (MOD_A, {"WaterHeight": "-2147483648"})])]
        p_rows = [lost_row(HF_, MOD_A, W, "WaterHeight", ["WaterHeight=3.4028235E+38"], PH, PH),
                  lost_row(OTHER, MOD_A, W, "WaterHeight", ["WaterHeight=3.4028235E+38"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], THIRD: [BASE], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE]})
        got = {e["A"]: e["admission"] for e in rec_of(plan, W)}
        # (seventh review, Clockwork S2) every such field carries an earlier change: said with that one explanation
        line = next((ln for ln in md.splitlines() if "only undoes the change" in ln
                     or "only carries an earlier plugin's change" in ln), "")
        ok = (got == {HF_: "baseGame", OTHER: "notOwnEdit"} and "the value in game does change" in line
              and "00C816" in line and "nothing is lost in game" not in line)
        return ok, f"{got}; {line[-260:]!r}"

    for name, fn in [("after-final/C810-value-carried-from-undone-master-is-said-once",
                      c810_value_carried_from_undone_master_is_said_once),
                     ("after-final/C811-disabled-by-the-whole-record", c811_disabled_by_the_whole_record),
                     ("after-final/C812-path-case-is-no-new-clash-with-the-patch", c812_path_case_is_no_new_clash_with_the_patch),
                     ("after-final/C813-carried-value-still-changes-in-game", c813_carried_value_still_changes_in_game)]:
        cell(name, fn)
    # ---------------------------------------- after the final round, group C: the patch comparison's own sentences
    # C814 (Pilgrim review S1): the patch section opens with what the patch does, from the same counts as its lines;
    # no sentence undercuts it ("do not establish", "did not report a newly introduced", "No measured repair").
    def c814_patch_summary_never_undercuts_its_lines():
        R1 = f"00C820:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Owner", "o", "a", "v")]
        plan, md, _ = core_bundle(p_rows, [])
        val = reader(plan).get("validation") or {}
        ok = (str(val.get("summary", "")).startswith(f"In short, with {PATCH}: resolved 1 record field")
              and "do not establish" not in md and "did not report a newly introduced" not in md
              and "No measured repair or ownership change was recorded." not in md
              and f"Removing {PATCH} brings back the 1 record field whose outcome it changes" in md)  # wording, eighth review
        return ok, f"summary {val.get('summary')!r}; headline {val.get('headline')!r}"

    # C815 (BPT review S2/S3): the summary names every check with new problems; a by-object check counts objects, with
    # its pairs beside them.
    def c815_new_problems_named_per_check_in_objects():
        plan, md, _ = new_by_object_bundle()
        val = reader(plan).get("validation") or {}
        head = str(val.get("summary", ""))
        line = next((ln for ln in md.splitlines() if ln.startswith("- New clashes in the setup with")), "")
        ok = ("2 objects (5 object-and-support pairs)" in head and "Objects left without their supporting surface" in head
              and "2 objects (5 object-and-support pairs)" in line)
        return ok, f"{head[:200]!r}; {line[:160]!r}"

    for name, fn in [("after-final/C814-patch-summary-never-undercuts-its-lines", c814_patch_summary_never_undercuts_its_lines),
                     ("after-final/C815-new-problems-named-per-check-in-objects", c815_new_problems_named_per_check_in_objects)]:
        cell(name, fn)
    # ---------------------------------------- after the final round, group B: item lines, units and naming
    # C816 (Pilgrim review S3): a record is named by what it is: its editor ID, or a placed object's base.
    def c816_examples_name_the_record():
        R1, R2 = f"00C830:{BASE}", f"00C831:{BASE}"
        r1 = lost_row(OTHER, MOD_A, R1, "Placement", "o1", "a1", "v1")
        r1["Summary"] = f"PlacedNpc RorlundREF: Placement - {OTHER}'s value lost to {MOD_A}'s"
        r2 = lost_row(OTHER, MOD_A, R2, "Placement", "o2", "a2", "v2")
        names = {"schema": "names-v1", "places": {}, "objects": {}, "mods": {}, "missing": [],
                 "refs": {R2: {"base": f"0BC001:{BASE}", "carriedBy": [BASE]}},
                 "bases": {f"0BC001:{BASE}": {"type": "Static", "editorId": "COCMarkerHeading", "model": "MarkerCOCHeading.nif"}}}
        plan, md, _ = core_bundle([r1, r2], [], names=names)
        # after-review (Pilgrim review 3): the base's kind, from its record type, is said before its editor ID
        ok = f"{R1} (RorlundREF, position)" in md and f"{R2} (a static object COCMarkerHeading, position)" in md
        return ok, next((ln for ln in md.splitlines() if "For example" in ln), "")[-220:]

    # C817 (Missives review 6, BPT review S6): an overlap item names its objects by their bases, and rows that are all
    # against one object say so once.
    def c817_objects_named_and_one_partner_said():
        X1, X2, Y = f"000X31:{MOD_A}", f"000X32:{MOD_A}", f"000Y31:{OTHER}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, X1, other=Y), row("OVERLAP", OTHER, MOD_A, X2, other=Y)]
        names = {"schema": "names-v1", "places": {}, "objects": {}, "mods": {}, "missing": [],
                 "refs": {X1: {"base": f"0BC011:{BASE}", "carriedBy": [MOD_A]},
                          X2: {"base": f"0BC011:{BASE}", "carriedBy": [MOD_A]},
                          Y: {"base": f"0BC012:{BASE}", "carriedBy": [OTHER]}},
                 "bases": {f"0BC011:{BASE}": {"type": "Container", "editorId": "BarrelFood", "model": "b.nif"},
                           f"0BC012:{BASE}": {"type": "Static", "editorId": "WallPiece", "model": "w.nif"}}}
        extra = {(OVERLAP, "without-patch"): rows_, (OVERLAP, "baseline"): []}
        plan, md, _ = core_bundle([], [], extra_results=extra, names=names)
        line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "overlap pair" in ln), "")
        ok = f"All 2 are against one object, {Y} (a static object WallPiece)" in line   # after-review: kind said
        return ok, line[-260:]

    # C818 (Pilgrim review M1, Missives review 6): "most" only where one place holds more than half.
    def c818_most_only_over_half():
        rows_ = [row("OVERLAP", OTHER, MOD_A, f"000X4{i}:{MOD_A}", other=f"000Y4{i}:{OTHER}", label=lab)
                 for i, lab in enumerate(["Hall (int)", "Hall (int)", "Yard (ext)", "Yard (ext)", "Gate (ext)"])]
        extra = {(OVERLAP, "without-patch"): rows_, (OVERLAP, "baseline"): rows_[:4]}
        plan, md, _ = core_bundle([], [], extra_results=extra)
        line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "overlap pair" in ln), "")
        ok = "Most are in" not in line and "They are in" in line
        return ok, line[-200:]

    # C819 (BPT review S5): the still-there line gives each lost item its unit, and a single remaining item is said
    # rather than "1 more in 1 other item".
    def c819_still_there_units_and_last_item():
        recs = [lost_row(OTHER, MOD_A, f"00C84{i}:{BASE}", "Owner", "o", "a", "v") for i in range(3)]
        cells = [lost_row(OTHER, MOD_A, f"00C85{i}:{BASE}", "OcclusionData", "o", "a", "v") for i in range(2)]
        enr = {(RECORDS, "without-patch"): recs, (RECORDS, "baseline"): recs,
               (CELLATTR, "without-patch"): cells, (CELLATTR, "baseline"): cells}
        count_only = {k: len(v) for k, v in enr.items()}
        results = {(CONTACT, a_): [] for a_ in ("baseline", "without-patch")}
        plan, md, _ = compile_and_render(WP_ARMS, results, [MOD_A], patches=[PATCH], count_only=count_only,
                                         enrichment=enr)
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        ok = (f"{OTHER}'s changes to 3 record fields lost to {MOD_A}" in st
              and f"{OTHER}'s changes to 2 cell fields lost to {MOD_A}" in st)
        return ok, st[:300]

    # C820 (Missives review 5): the pointer to further record results says they were measured and set aside.
    def c820_further_results_said_set_aside():
        pr = json.loads(PRIORITIES.read_text(encoding="utf-8"))
        w_ = (pr.get("reader") or {}).get("prediction", {}).get("detailSingleModLine", "")
        return "set aside" in w_ and "hidden clash" in w_, w_

    for name, fn in [("after-final/C816-examples-name-the-record", c816_examples_name_the_record),
                     ("after-final/C817-objects-named-and-one-partner-said", c817_objects_named_and_one_partner_said),
                     ("after-final/C818-most-only-over-half", c818_most_only_over_half),
                     ("after-final/C819-still-there-units-and-last-item", c819_still_there_units_and_last_item),
                     ("after-final/C820-further-results-said-set-aside", c820_further_results_said_set_aside)]:
        cell(name, fn)
    # C821 (Clockwork review S6): with no names enrichment, an overlap measured as crossing mesh triangles still has
    # bodies on both sides: it is not under "cannot tell whether the object has a body"; one not measured on meshes is.
    def c821_mesh_crossing_has_bodies():
        a_ = row("OVERLAP", OTHER, MOD_A, f"000X51:{MOD_A}", other=f"000Y51:{OTHER}")
        a_["Summary"] = "Static from ModA.esp / Static from OtherMod.esp: crossing, 13 triangle pairs in Hall (int)"
        b_ = row("OVERLAP", OTHER, MOD_A, f"000X52:{MOD_A}", other=f"000Y52:{OTHER}")
        extra = {(OVERLAP, "without-patch"): [a_, b_], (OVERLAP, "baseline"): []}
        plan, md, _ = core_bundle([], [], extra_results=extra)
        line = next((ln for ln in md.splitlines() if "Cannot tell whether the object has a body" in ln), "")
        return "** 1 " in line or ":** 1 " in line, line[:240]

    for name, fn in [("after-final/C821-mesh-crossing-has-bodies", c821_mesh_crossing_has_bodies)]:
        cell(name, fn)
    # C822 (Missives review 2): a lost value several plugins carry names the plugin that first set it.
    def c822_carried_value_names_its_origin():
        L1 = f"00C860:{BASE}"
        arms = [arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        # (third review, Missives S1) the value's origin is said on the base-fix line: ModA drops HearthFires' fix, which
        # OtherMod only carries
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        # (fourth review) one carrier: "only carries" (the verb agrees; Snazzy r3 minor 7)
        ok = f"{OTHER} only carries" in line and "Location" in line and MOD_A in line
        return ok, line[-260:]

    for name, fn in [("after-final/C822-carried-value-names-its-origin", c822_carried_value_names_its_origin)]:
        cell(name, fn)
    # C823 (BPT review S4): a detail line on the prediction setup whose rows the top counts only in part says how many
    # it counts and why the rest are not.
    def c823_detail_count_reconciled_with_the_top():
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]
        rows_ = [row("OVERLAP", OTHER, MOD_A, f"000X6{i}:{BASE}", other=f"000Y6{i}:{OTHER}") for i in range(3)]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): rows_, (OVERLAP, "without-a"): rows_[:1]}, [MOD_A])
        found = section_text(md, "What the checks found")
        line = next((ln for ln in found.splitlines() if "3 overlap pairs" in ln), "")
        return "(2 counted at the top; 1 set aside)" in line, line[:260]

    for name, fn in [("after-final/C823-detail-count-reconciled-with-the-top", c823_detail_count_reconciled_with_the_top)]:
        cell(name, fn)
    # C824 (Clockwork review S5): a still-there overlap where a patch places one of the two objects says so, never only
    # the two mods' names.
    def c824_still_there_says_the_patch_places_it():
        X = f"000X01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2, 3)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        refs = {ys[1]: {"carriedBy": [MOD_B, PATCH]}, ys[2]: {"carriedBy": [MOD_B]}, X: {"carriedBy": [MOD_A]}}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): [
            row("OVERLAP", MOD_B, MOD_A, X, other=ys[0])]}, [MOD_A, MOD_B], patches=[PATCH],
            predicates={(OVERLAP, "with-patch"): preds}, names={"refs": refs})
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        # (third review, Clockwork S1) the pair a patch places is not still there; the with-patch line says it apart
        wl = str((reader(plan).get("prediction") or {}).get("withPatchLine") or "")
        ok = ("2 overlap pairs between" in st and "a patch places" not in st
              and "1 still intersecting where a patch places one of them" in wl)
        return ok, st[:300] + " | " + wl[:300]

    for name, fn in [("after-final/C824-still-there-says-the-patch-places-it", c824_still_there_says_the_patch_places_it)]:
        cell(name, fn)
    # C825 (Clockwork review S4): navmesh ground still lost with the patch is said in the still-there line at the top,
    # in its own unit, not only further down.
    def c825_ground_still_lost_said_at_the_top():
        n1, n2 = f"000N01:{BASE}", f"000N02:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  689 tris -> 5")]
        w_rows = [row("CLOBBERED", OTHER, PATCH, n1, label="CityExterior01 (ext)  465 tris -> 345")]
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n1, "Other": None, "Physical": "Gone",
                  "Detail": "no point of the loser's mesh is farther than 120u from the winner's"},
                 {"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n2, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 22597 sample(s) of the loser's mesh farther than 120u from the winner's"}]
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): w_rows},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        st = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        return "navmesh ground left uncovered" in st and "in 1 navmesh record under" in st, st[:300]

    for name, fn in [("after-final/C825-ground-still-lost-said-at-the-top", c825_ground_still_lost_said_at_the_top)]:
        cell(name, fn)
    # C826 (Missives review 4): a further evaluation supplied with the report is named, with the setups it measured.
    def c826_further_evaluation_named():
        with tempfile.TemporaryDirectory() as td:
            other_ = make_bundle(Path(td) / "eval-small", [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"),
                                                           arm("without-a", [BASE, OTHER])],
                                 {(CONTACT, "baseline"): [], (CONTACT, "without-a"): []}, [MOD_A])
            plan, md, _ = compile_and_render(WP_ARMS, {(CONTACT, a_): [] for a_ in ("baseline", "without-patch")}, [MOD_A],
                                             patches=[PATCH], settings_extra={"alsoBundles": [str(other_)]})
        line = next((ln for ln in md.splitlines() if ln.startswith("Also supplied with this report")), "")
        ok = "eval-small" in line and f"baseline ({OTHER}, {MOD_A})" in line and f"without-a ({OTHER})" in line
        return ok, line[:300]

    for name, fn in [("after-final/C826-further-evaluation-named", c826_further_evaluation_named)]:
        cell(name, fn)
    # C827 (BPT review S1): a row whose two values the record import's own chain reads as the same file (letter case
    # and slashes folded), though the row was written without that rule, is set aside as read the same, not counted.
    def c827_chain_equal_is_path_case():
        W = f"00C870:{BASE}"
        low_, up_ = "data\\textures\\landscape\\a.dds", "Data\\Textures\\Landscape\\A.dds"
        p_rows = [lost_row(OTHER, MOD_A, W, "HdLodDiffuseTexture", [f"HdLodDiffuseTexture={low_}"], PH, PH)]
        tr_ = tree_transcript([(W, "Worldspace", [(BASE, {"HdLodDiffuseTexture": up_}), (OTHER, {"HdLodDiffuseTexture": low_}),
                                                  (MOD_A, {"HdLodDiffuseTexture": up_})])])
        plan, md, _ = core_bundle(p_rows, list(p_rows), masters=MASTERS_, p_tr=tr_)
        got = [e["admission"] for e in rec_of(plan, W)]
        return got == ["sameAsRead"], f"{got}"

    for name, fn in [("after-final/C827-chain-equal-is-path-case", c827_chain_equal_is_path_case)]:
        cell(name, fn)
    # C828 (Clockwork review S5): with no names read, the record rows of the setup with the patch, where the patch's
    # placement of an object wins, say a still-there overlap crosses at the patch's position.
    def c828_patch_placement_from_record_rows():
        X = f"000X01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [
            lost_row(MOD_A, PATCH, X, "Placement", "Position=1,1,1; Rotation=0,0,0", "Position=9,9,9; Rotation=0,0,0",
                     "Position=0,0,0; Rotation=0,0,0")]}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): p_rows},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                         enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()})
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        return "2 still crossing where a patch places one of them" in st, st[:300]

    for name, fn in [("after-final/C828-patch-placement-from-record-rows", c828_patch_placement_from_record_rows)]:
        cell(name, fn)
    # C829 (Clockwork review S5): where no record row names it (the patch overrides an object the evaluated mod adds),
    # the record import's chain of the setup with the patch shows the patch's version of the object loads last and
    # moves it: a still-there overlap says so.
    def c829_patch_placement_from_the_chain():
        X = f"000X01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        other_ = f"00C890:{BASE}"
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", [(MOD_A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): p_rows},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                         enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         transcripts=tr)
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        return "2 still crossing where a patch places one of them" in st, st[:300]

    for name, fn in [("after-final/C829-patch-placement-from-the-chain", c829_patch_placement_from_the_chain)]:
        cell(name, fn)
    # ---------------------------------------- after the final round, blind review (Winterhold, 0A0315): what is gone
    # in game. A side disables an object only when it is off for good: moved below the world, or enabled opposite to
    # the player. A bare initially-disabled flag (no parent, or any other parent) starts it off; a script or quest may
    # enable it, so its position still matters.
    def offs(z, flags="0", parent=None):
        d_ = {"Placement.Position": f"100,100,{z}", "Placement.Rotation": "0,0,0", "SkyrimMajorRecordFlags": flags,
              "EnableParent.Reference": "has no EnableParent.Reference", "EnableParent.Flags": "has no EnableParent.Flags"}
        if parent:
            d_.update({"EnableParent.Reference": parent, "EnableParent.Flags": "SetEnableStateToOppositeOfParent"})
        return d_

    def off_txt(v):
        return f"Position={v['Placement.Position']}; Rotation=0,0,0"

    # C830: initially disabled with no parent on both sides: counted, and the item says it starts disabled and may be
    # enabled by a script or quest.
    def c830_initially_disabled_without_parent_stays_counted():
        D = f"00C830:{BASE}"
        b_, o_, a_ = offs(0), offs(5, "InitiallyDisabled"), offs(40, "InitiallyDisabled")
        o_["Placement.Position"], a_["Placement.Position"] = "300,100,5", "900,100,40"
        chains = [(D, "PlacedObject", [(BASE, offs(0, "InitiallyDisabled")), (OTHER, o_), (MOD_A, a_)])]
        p_rows = [lost_row(OTHER, MOD_A, D, "Placement", off_txt(o_), off_txt(a_), off_txt(b_), before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, D)]
        said = "starts disabled" in md and "script or quest" in md
        return got == ["admitted"] and said, f"{got}; said {said}"

    # C831: enabled opposite to the player on both sides: off for good, set aside.
    def c831_opposite_of_player_on_both_sides_is_set_aside():
        D = f"00C831:{BASE}"
        o_, a_ = offs(5, "0", "000014:Skyrim.esm"), offs(40, "0", "000014:Skyrim.esm")
        o_["Placement.Position"], a_["Placement.Position"] = "300,100,5", "900,100,40"
        chains = [(D, "PlacedObject", [(BASE, offs(0)), (OTHER, o_), (MOD_A, a_)])]
        p_rows = [lost_row(OTHER, MOD_A, D, "Placement", off_txt(o_), off_txt(a_), off_txt(offs(0)), before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        got = [e["admission"] for e in rec_of(plan, D)]
        return got == ["bothDisabled"], f"{got}"

    for name, fn in [("after-final/C830-initially-disabled-without-parent-stays-counted",
                      c830_initially_disabled_without_parent_stays_counted),
                     ("after-final/C831-opposite-of-player-on-both-sides-is-set-aside",
                      c831_opposite_of_player_on_both_sides_is_set_aside)]:
        cell(name, fn)


    # ---------------------------------------- after the review round (2026-10-21): the blind reviews of regen-14
    def off_said(text):
        """The numbers a sentence gives next to "off for good" (the counts of lost changes on objects set off for good)."""
        return sum(int(m_.group(1).replace(",", "")) for m_ in re.finditer(r"(\d[\d,]*) [^;(),]*?off for good", text))

    def left_said(summary):
        """The record fields the summary's "left" part counts (0 when it names none, None when there is no such part)."""
        lp_ = re.search(r"\bleft ([^:]*?)(?:, and new|\.$|$)", str(summary or ""))
        m_ = re.search(r"(\d[\d,]*) record fields?\b", lp_.group(1)) if lp_ else None
        return int(m_.group(1).replace(",", "")) if m_ else (0 if lp_ else None)

    def pair_parts(stays, a_, b_):
        return [x for x in re.split(r";\s+(?![^()]*\))", stays or "") if a_ in x and b_ in x]

    # C900, C901 (BPT review 1): ModA's changes still lost to ModB with the patch, on objects ModB's version (in the
    # setup with the patch) sets off for good, have no effect in game, and forwarding them brings back an object ModB
    # removes. D1: ModB enables it opposite to the player, nothing patches it (left); D2: ModB only moves it (left, not
    # off); D3: ModB moves it below the world and the patch carries ModB's version (kept). Two are off for good.
    def offwin_bundle():
        D1, D2, D3 = f"00C900:{BASE}", f"00C901:{BASE}", f"00C902:{BASE}"
        a_ = offs(0)
        a_["Placement.Position"] = "300,100,0"
        b_off, b_mv, b_low = offs(0, "0", "000014:Skyrim.esm"), offs(0), offs(-30000)
        b_mv["Placement.Position"] = "500,100,0"
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)
                  for D, b_ in ((D1, b_off), (D2, b_mv), (D3, b_low))]
        w_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)
                  for D, b_ in ((D1, b_off), (D2, b_mv))]
        w_rows.append(lost_row(MOD_A, PATCH, D3, "Placement", off_txt(a_), off_txt(b_low), off_txt(offs(0)), before=BASE))
        # (sixth review, BPT r6 S1) D1's removal is an earlier mod's that ModB carries: still left with the patch (a
        # winner's own removal standing is its own outcome, C1101)
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), *([(OTHER, b_)] if D == D1 else []), (MOD_A, a_), (MOD_B, b_)])
                for D, b_ in ((D1, b_off), (D2, b_mv), (D3, b_low))]
        w_ch = [(D1, "PlacedObject", [(BASE, offs(0)), (OTHER, b_off), (MOD_A, a_), (MOD_B, b_off)]),
                (D2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_mv)]),
                (D3, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_low), (PATCH, b_low)])]
        return core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch,
                           masters={**MASTERS_, MOD_B: [BASE, OTHER], PATCH: [BASE]})

    def c900_offwin_said_where_still_there():
        plan, md, _ = offwin_bundle()
        st = pair_parts((reader(plan).get("prediction") or {}).get("withPatchStays"), MOD_A, MOD_B)
        # (third review, Winterhold S1) D3, kept by the patch, is the patch's resolution and not still there: only D1
        return off_said(" ".join(st)) == 1, f"{st}"

    def c901_offwin_said_on_comparison_and_summary():
        plan, md, _ = offwin_bundle()
        val = reader(plan).get("validation") or {}
        il_ = [x for x in val.get("itemLines") or [] if x.startswith("Record fields") and MOD_B in x]
        summ = str(val.get("summary") or "")
        return off_said(" ".join(il_)) == 2 and off_said(summ) == 2, f"{il_}; {summ[:240]}"

    # C902 (Winterhold review 1, BPT review 3): "left" is one number everywhere: a lost change no patch touches. Where
    # the patch carries the winning value (000703), the losing mod's change stays lost by the patch's choice or copy, and
    # it is said apart in the still-there line and the summary, never counted as left.
    def c902_left_is_untouched_only():
        plan, md, html = record_validation_bundle()
        val, pr_ = reader(plan).get("validation") or {}, reader(plan).get("prediction") or {}
        st = pair_parts(pr_.get("withPatchStays"), MOD_A, MOD_B)
        n_left = left_said(val.get("summary"))
        left_part = any(re.search(re.escape(MOD_A) + r"'s changes to 1 record field\b", x) for x in st)
        # (third review, Winterhold S1) the carried field is the patch's resolution: in the summary, not still there
        carried = "carried by a patch on 1" in str(val.get("summary") or "") and not any("carr" in x for x in st)
        return n_left == 1 and left_part and carried, f"summary left {n_left}; {st}; {val.get('summary')}"

    # C903, C904 (Missives review 1): a cell's Grid row set aside because the losing mod changed only the three unused
    # bytes. Where the winner changed the land-flag byte (hiding land quarters), that is the winner's own change: not a
    # clash between mods, and not "a change the game does not read"; it is said on its own line, with the cell, the
    # winning mod and the land. Where the winner's land byte is the losing value's, the row stays "unused bytes".
    def land_bundle(win_flags):
        C1 = f"00C903:{BASE}"
        r_ = lost_row(OTHER, MOD_A, C1, "Grid", ["Grid.Flags=-412494080"], PH, ["Grid.Flags=-499936256"],
                      label="TestCellNorth")
        r_["Summary"] = f"Cell TestCellNorth: Grid - {OTHER}'s value lost to {MOD_A}'s"
        tr_ = tree_transcript([(C1, "Cell", [(BASE, {"Grid.Flags": "5504256"}), (OTHER, {"Grid.Flags": "-412494080"}),
                                             (MOD_A, {"Grid.Flags": win_flags})])])
        plan, md, _ = core_bundle([r_], [], check=CELLATTR, masters=MASTERS_, p_tr=tr_)
        return plan, md, C1

    def c903_winner_land_byte_said_not_unused():
        plan, md, C1 = land_bundle("-1223611121")           # low byte 0x0F: all four land quarters hidden
        adm = [e["admission"] for e in rec_of(plan, C1)]
        said = any("TestCellNorth" in ln and "land" in ln.lower() and MOD_A in ln for ln in md.splitlines())
        return adm and adm[0] not in ("unusedBits", "admitted") and said, f"{adm}; said {said}"

    def c904_same_land_byte_stays_unused():
        plan, md, C1 = land_bundle("-1223611136")           # low byte 0x00: the land flags the losing value has
        adm = [e["admission"] for e in rec_of(plan, C1)]
        return adm == ["unusedBits"], f"{adm}"

    for name, fn in [("after-review/C900-offwin-said-where-still-there", c900_offwin_said_where_still_there),
                     ("after-review/C901-offwin-said-on-comparison-and-summary", c901_offwin_said_on_comparison_and_summary),
                     ("after-review/C902-left-is-untouched-only", c902_left_is_untouched_only),
                     ("after-review/C903-winner-land-byte-said-not-unused", c903_winner_land_byte_said_not_unused),
                     ("after-review/C904-same-land-byte-stays-unused", c904_same_land_byte_stays_unused)]:
        cell(name, fn)


    # C923 (reviewer, BPT 1): every off-for-good count sits next to the count it is part of and is never larger; only
    # a lost value of the field that sets the object off (D3's position below the world) would bring it back if
    # forwarded, so only that is said: D1 (enabled opposite to the player, its Placement lost) has no effect in game.
    def part_le_whole(text):
        bad = []
        # (fourth review) the note names the losing version's state before "sets off for good": the same notes read
        for m_ in re.finditer(r"\((\d[\d,]*) on objects [^;()]*?sets? off for good", text):
            before = re.findall(r"(?<![\w.])(\d[\d,]*)(?!\w)", text[:m_.start()])
            whole = int(before[-1].replace(",", "")) if before else -1
            if int(m_.group(1).replace(",", "")) > whole:
                bad.append((m_.group(1), whole))
        return bad

    def c923_off_count_part_of_its_count_and_true_effect():
        # (reviewer on regen-q4) the on/off clause is said once per line, against the line's own notes
        plan, md, _ = offwin_bundle()
        line = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        bad = part_le_whole(md)
        # (blind reviews of regen-2) the forwarding clause is gone: no line says what forwarding would do (C931)
        # (fourth review, Winterhold S2) ModA has D1 on: its loss to ModB's removal is "not drawn", never "no effect"
        ok = (not bad and "off for good" in md and ", not drawn" in line and "no effect in game" not in line
              and "on/off field" not in md and "brings them back" not in md)
        return ok, f"bad {bad}; {line[:400]}"

    for name, fn in [("after-review/C923-off-count-part-of-its-count-and-true-effect",
                      c923_off_count_part_of_its_count_and_true_effect)]:
        cell(name, fn)


    # C905 (Pilgrim review 1): a quest-alias split is said by scene, with the probe's own distance sentence, and names
    # the plugin that first set the winning placement of the actors (SecondMod.esp, carried by the patch), never as
    # "records ... between" the probe's plugin pair.
    def c905_alias_split_names_placement_origin():
        A1, A2, B1, B2 = (f"00C9{i}5:{BASE}" for i in range(4))
        arms = [arm("baseline", [BASE, OTHER2, OTHER, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER2, OTHER, MOD_A])]
        w_rows = [row("SPLIT", OTHER, PATCH, x_, other=y_, label="Temple (int)") for x_, y_ in ((A1, A2), (A2, A1))]
        w_rows += [row("SPLIT", OTHER, PATCH, x_, other=y_, label="Temple (int)") for x_, y_ in ((B1, B2), (B2, B1))]
        for r_, q_ in zip(w_rows, ("Q1", "Q1", "Q2", "Q2")):
            r_["Summary"] = f"quest '{q_}' aliases 'X' and 'Y' were 137u apart in Temple (int), now 1,800u apart"
        results = {(ALIAS, "baseline"): w_rows, (ALIAS, "without-patch"): []}
        place = {fk_: [(BASE, {"Placement.Position": "0,0,0"}), (OTHER2, {"Placement.Position": "5,5,900"}),
                       (MOD_A, {"Placement.Position": "0,0,0"}), (PATCH, {"Placement.Position": "5,5,900"})]
                 for fk_ in (A1, A2, B1, B2)}
        other_ = f"00C9F5:{BASE}"
        enr = {(RECORDS, "baseline"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")],
               (RECORDS, "without-patch"): []}
        tr = {(RECORDS, "baseline"): tree_transcript([(fk_, "PlacedObject", ch_) for fk_, ch_ in place.items()]
                                                     + [(other_, "PlacedObject", [(BASE, {"Owner": "v"}),
                                                                                  (OTHER, {"Owner": "o"}),
                                                                                  (PATCH, {"Owner": "p"})])])}
        plan, md, html = compile_and_render(arms, {**results, (CONTACT, "baseline"): [], (CONTACT, "without-patch"): []},
                                            [MOD_A], patches=[PATCH], enrichment=enr,
                                            count_only={k: max(1, len(v)) for k, v in enr.items()}, transcripts=tr)
        sec_ = md.split("### Quest aliases split between mods", 1)[-1].split("###", 1)[0]
        line = next((ln for ln in sec_.splitlines() if "Measured load order" in ln), "")
        ok = ("2 quest scenes" in line and "first set by " + OTHER2 in line and "carried by " + PATCH in line
              and "1,800u apart" in line and "records show" not in line)
        return ok, line[:400]

    for name, fn in [("after-review/C905-alias-split-names-placement-origin", c905_alias_split_names_placement_origin)]:
        cell(name, fn)


    # C906 (Pilgrim review 5): the further-record-results line says how many of the results it leaves out of the detail
    # list hold clashes counted at the top; it never calls those "set aside".
    def c906_further_results_counted_at_top_said():
        R1 = f"00C906:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Placement", "Position=5,5,5", "Position=9,9,9", "Position=0,0,0")]
        plan, md, _ = core_bundle(p_rows, list(p_rows), masters=MASTERS_)
        adm = [e["admission"] for e in rec_of(plan, R1)]
        line = next((ln for ln in md.splitlines() if "further record result" in ln), "")
        m_ = re.match(r"^(\d+) further record results?", line)
        n_ = int(m_.group(1)) if m_ else 0
        ok = (adm == ["admitted"] and n_ >= 1 and "counted at the top" in line
              and re.search(r"\b1 of them hold", line) is not None and "all were measured and set aside" not in line)
        return ok, f"{adm}; {line[:300]}"

    for name, fn in [("after-review/C906-further-results-counted-at-top-said", c906_further_results_counted_at_top_said)]:
        cell(name, fn)


    # C907 (Winterhold review 2): a lost change already there with one evaluated mod alone names the plugin that wins
    # it in that mod's own setup (Late.esp), not the other evaluated mod it loses to when both are loaded.
    def c907_alone_line_names_alone_winner():
        LATE = "Late.esp"
        R1 = f"00C907:{BASE}"
        arms = [arm("baseline", [BASE, MOD_A, LATE, MOD_B], kind="baseline"), arm("dependency-only", [BASE, LATE]),
                arm("plus-a", [BASE, MOD_A, LATE]), arm("plus-b", [BASE, LATE, MOD_B])]
        enr = {(RECORDS, "baseline"): [lost_row(MOD_A, MOD_B, R1, "Placement", "Position=1,1,1", "Position=3,3,3",
                                                "Position=0,0,0")],
               (RECORDS, "plus-a"): [lost_row(MOD_A, LATE, R1, "Placement", "Position=1,1,1", "Position=2,2,2",
                                              "Position=0,0,0")],
               (RECORDS, "plus-b"): [], (RECORDS, "dependency-only"): []}
        results = {(CONTACT, a_["armId"]): [] for a_ in arms}
        plan, md, _ = compile_and_render(arms, results, [MOD_A, MOD_B], enrichment=enr,
                                         count_only={k: max(1, len(v)) for k, v in enr.items()})
        rec_ = [(e["admission"], e.get("attribution")) for e in rec_of(plan, R1)]
        line = next((ln for ln in md.splitlines() if "Record fields changed by more than one mod** (" in ln), "")
        ok = (f"from {MOD_A}, with {LATE} 1" in line and f"objects {MOD_B} also edits, still there without it" not in line)
        return ok, f"{rec_}; {line[:300]}"

    for name, fn in [("after-review/C907-alone-line-names-alone-winner", c907_alone_line_names_alone_winner)]:
        cell(name, fn)


    # C908 (Winterhold review 3): the undone line links the finding that holds the objects it names as switched back on,
    # not the first finding in check order (here a cell finding with no example in it).
    def c908_undone_link_holds_examples():
        C1 = f"00C908:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
                  lost_row(MOD_A, PATCH, f"000702:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0")]
        c_rows = [lost_row(MOD_B, PATCH, C1, "Lighting", "b9", "v9", "v9")]
        c_rows[0]["Summary"] = f"Cell TestCellSouth: Lighting - {MOD_B}'s value lost to {PATCH}'s"
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows,
               (CELLATTR, "baseline"): [], (CELLATTR, "with-patch"): c_rows}
        count_only = {k: max(1, len(v)) for k, v in enr.items()}
        results = {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            count_only=count_only, enrichment=enr)
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        m_ = re.search(r"\[(F-\d+)\]", line)
        fid = m_.group(1) if m_ else None
        f_ = next((f for f in plan.get("findings") or [] if f.get("id") == fid), {})
        holds = any(str(r_.get("formKey")) == f"000702:{BASE}" for r_ in f_.get("rows") or [])
        ok = "switch the object back on" in line and holds      # (fifth review) the wording names its unit
        return ok, f"link {fid} ({f_.get('checkId')}); {line[:200]}"

    for name, fn in [("after-review/C908-undone-link-holds-examples", c908_undone_link_holds_examples)]:
        cell(name, fn)


    # C909 (Missives review 2): in the detail list, water seams on cells where the mod undoes its master's water fix are
    # said as that undo, not as clashes with the mod owning the neighbouring cell, and the water check is not headed as
    # height changes alone (here only the type differs).
    def c909_water_seam_detail_names_the_undo():
        WB = "environment.water-boundaries"
        W1 = f"009331:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        chains = [(W1, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})])]
        p_rows = [lost_row(UPD, MOD_A, W1, "Water", ["Water=001232:Update.esm"], PH, PH)]
        s_ = row("WATER-SEAM", OTHER, MOD_A, W1, other=f"009332:{BASE}")
        s_["Summary"] = ("Tamriel (1,1) E Tamriel (2,1): joined under the truth (001232:Update.esm z -14000), apart under "
                         "the winner (000018:Skyrim.esm z -14000 | 001232:Update.esm z -14000) — attributed to side B")
        extra = {(WB, "without-patch"): [s_], (WB, "baseline"): [s_]}
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains, extra_results=extra,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        heads = [ln for ln in found.splitlines() if ln.startswith("### ") and "Water" in ln]
        sec_ = found.split(heads[0], 1)[1].split("###", 1)[0] if heads else ""
        lines = [ln for ln in sec_.splitlines() if ln.startswith("- **")]
        ok = (bool(heads) and "Water height changes" not in heads[0] and lines
              and all(f"{MOD_A} undoes its master {UPD}'s fix" in ln and f"between {OTHER}" not in ln for ln in lines))
        return ok, f"{heads}; {lines[:2]}"

    for name, fn in [("after-review/C909-water-seam-detail-names-the-undo", c909_water_seam_detail_names_the_undo)]:
        cell(name, fn)


    # C910 (Clockwork review 1): removing the patch, the overlap comparison counts as new only the crossings its probe
    # calls a new defect (1 of the 2 rows that entered the class); the other crosses in both setups and is said so.
    def c910_removing_patch_counts_new_defects():
        X1, X2 = f"000X11:{MOD_A}", f"000X12:{MOD_A}"
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X1, other=f"000Y11:{MOD_B}"),
                  row("OVERLAP", MOD_B, MOD_A, X2, other=f"000Y12:{MOD_B}")]
        w_rows = []
        order = {"without-patch": {"overlapprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                         created=2, newDefect=1, attributed=1, unmeasured=0)}}
        log = f"NEW     OVERLAP    {MOD_B}  x  {MOD_A}   +2 record(s): created 2\n"
        plan, md, html = compile_and_render(PATCHED_ARMS, {(OVERLAP, "without-patch"): p_rows, (OVERLAP, "baseline"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            diff_logs={(OVERLAP, "without-patch"): log})
        line = next((ln for ln in md.splitlines() if "new overlap candidate" in ln and "introduced" in ln), "")
        ok = "introduced 1 new overlap candidate " in line and re.search(r"\b1 more\b", line) is not None
        return ok, line[:300]

    for name, fn in [("after-review/C910-removing-patch-counts-new-defects", c910_removing_patch_counts_new_defects)]:
        cell(name, fn)


    # C924 (Clockwork review 1): where the still-there line says a patch places one of the two objects (read from the
    # record chain), the with-patch and comparison lines say the same, never "not attributed".
    def c924_patch_places_said_alike():
        X = f"000X01:{MOD_A}"
        ys = [f"000Y0{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        other_ = f"00C924:{BASE}"
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", [(MOD_A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): []},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                         enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         transcripts=tr)
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        # (third review, Clockwork S1) the patch's placement: not left and not still there, said alike on every line
        kl = [ln for ln in md.splitlines() if "overlap pairs)" in ln and "where a patch places" in ln]
        ok = ("overlap pairs between" not in st and kl and not any("left 2" in ln for ln in kl)
              and all("not attributed" not in ln and "2 still intersecting where a patch places one of them" in ln
                      for ln in kl))
        return ok, f"{st[:200]}; {kl}"

    for name, fn in [("after-review/C924-patch-places-said-alike", c924_patch_places_said_alike)]:
        cell(name, fn)


    # C911 (BPT review 2): the new problems with the patch name each new object by its base and model (from the
    # names enrichment) and give the measured gap to the nearest surface (from the row), never a role guessed from a name.
    def c911_new_problem_objects_named_with_gap():
        S1, S2 = f"000P11:{MOD_A}", f"000P12:{MOD_A}"
        arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        new = [row("CONTACT", PATCH, MOD_A, S1, other=f"000G11:{MOD_B}"),
               row("CONTACT", PATCH, MOD_A, S2, other=f"000G12:{MOD_B}")]
        for r_, gap_ in zip(new, ("4.9", "128")):
            r_["Necessary"][0]["PhysicalDetail"] = f"touches nothing within 4u (nearest {gap_}u)"
        refs = {S1: {"base": f"0B0P11:{MOD_A}"}, S2: {"base": f"0B0P11:{MOD_A}"},
                f"000G11:{MOD_B}": {"base": f"0B0G11:{MOD_B}"}, f"000G12:{MOD_B}": {"base": f"0B0G11:{MOD_B}"}}
        bases = {f"0B0P11:{MOD_A}": {"type": "Static", "editorId": "TestCard", "model": "test_card.nif"},
                 f"0B0G11:{MOD_B}": {"type": "Static", "editorId": "TestBeam", "model": "test_beam.nif"}}
        plan, md, html = compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): new},
                                            [MOD_A, MOD_B], patches=[PATCH], names={"refs": refs, "bases": bases})
        line = next((ln for ln in md.splitlines() if ln.startswith("- New clashes with the patch, by check")), "")
        ok = ("TestCard" in line and "test_card.nif" in line and "4.9" in line and "128" in line
              and "nearest" in line)
        return ok, line[:300]

    for name, fn in [("after-review/C911-new-problem-objects-named-with-gap", c911_new_problem_objects_named_with_gap)]:
        cell(name, fn)


    # C912 (Pilgrim review 2): the outcome where a patch keeps one side's value reads as one phrase ("the patch keeps
    # X's value over Y's change on N"), and the once-said "not measured" sentence names the items it covers by their
    # other mods instead of "the items above that say nothing else about it".
    def c912_outcome_and_once_sentence_parse():
        plan, md, html = record_validation_bundle()
        items = " ".join((reader(plan).get("validation") or {}).get("itemLines") or [])
        ok_a = (f"the patch restores {MOD_A}'s value, dropping the winning {MOD_B}'s change on 1" in items
                and "change is dropped" not in md)
        O2, O3 = "Second.esp", "Third.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, O3, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, O3, MOD_A]), arm("without-o2", [BASE, OTHER, O3, MOD_A])]
        p_rows = [lost_row(OTHER, MOD_A, f"00C912:{BASE}", "Owner", "o", "a", "v"),
                  lost_row(O3, MOD_A, f"00C913:{BASE}", "Owner", "t", "a", "v"),
                  lost_row(O2, MOD_A, f"00C914:{BASE}", "Owner", "s", "a", "v")]
        enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): [], (RECORDS, "without-o2"): p_rows[:2]}
        plan2, md2, _ = compile_and_render(arms, {(CONTACT, a_["armId"]): [] for a_ in arms}, [MOD_A], patches=[PATCH],
                                           enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()})
        once = next((ln for ln in md2.splitlines() if ln.startswith("For ") and "was not measured" in ln), "")
        ok_b = (OTHER in once and O3 in once and "say nothing else" not in once)
        return ok_a and ok_b, f"{items[:200]}; {once[:250]}"

    for name, fn in [("after-review/C912-outcome-and-once-sentence-parse", c912_outcome_and_once_sentence_parse)]:
        cell(name, fn)


    # C913 (Pilgrim review 3, Clockwork review 2): an object is named by what kind of thing it is, from its base's
    # record type or its model's folder (skill data), before its editor ID: a visual effect, the NPC, a container.
    def c913_objects_named_by_kind():
        X1, Y1, N1 = f"000X91:{MOD_A}", f"000Y91:{OTHER}", f"00C913:{BASE}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, X1, other=Y1)]
        r1 = lost_row(OTHER, MOD_A, N1, "Placement", "o1", "a1", "v1")
        r1["Summary"] = f"PlacedNpc TestPriestREF: Placement - {OTHER}'s value lost to {MOD_A}'s"
        names = {"schema": "names-v1", "places": {}, "objects": {}, "mods": {}, "missing": [],
                 "refs": {X1: {"base": f"0BC091:{BASE}", "carriedBy": [MOD_A]},
                          Y1: {"base": f"0BC092:{BASE}", "carriedBy": [OTHER]},
                          N1: {"base": f"0BC093:{BASE}", "carriedBy": [BASE]}},
                 "bases": {f"0BC091:{BASE}": {"type": "MoveableStatic", "editorId": "TestMist01",
                                               "model": "Effects\\Ambient\\TestMist01.nif"},
                           f"0BC092:{BASE}": {"type": "Container", "editorId": "TestChest", "model": "c.nif"},
                           f"0BC093:{BASE}": {"type": "Npc", "editorId": "TestPriest", "model": None}}}
        extra = {(OVERLAP, "without-patch"): rows_, (OVERLAP, "baseline"): []}
        plan, md, _ = core_bundle([r1], [], extra_results=extra, names=names)
        # (reviewer) the kind is the record type, never a role read from a model's path
        ok = ("a movable static TestMist01" in md and "a container TestChest" in md
              and "the NPC TestPriestREF" in md)
        lines = [ln for ln in md.splitlines() if "TestMist01" in ln or "TestPriestREF" in ln]
        return ok, " | ".join(x[-160:] for x in lines[:2])

    for name, fn in [("after-review/C913-objects-named-by-kind", c913_objects_named_by_kind)]:
        cell(name, fn)


    # C914 (Pilgrim review 4): the base-game line names, for a quest or switch check, the objects (by kind and editor
    # ID from the probe's own words) and their places, and links each of its findings.
    def c914_base_game_line_names_objects_and_places():
        X3, X4 = f"000X03:{MOD_A}", f"000X04:{MOD_A}"
        r1 = state_row("S1", BASE, MOD_A, X3, S0)
        r1["Summary"] = f"Route StateConditional under CampEnable: {X3} [Static TestCairn] arrives - Blocked: cuts the path"
        r1["Necessary"][0]["Label"] = "Farm (ext)"
        r2 = state_row("S2", BASE, MOD_A, X4, S0)
        r2["Summary"] = f"Route StateConditional under HallEnable: {X4} [Activator TestShrine] arrives - LikelyBlocked"
        r2["Necessary"][0]["Label"] = "Hall (int)"
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): [r1, r2], (STATE, "without-b"): [],
                   (STATE, "without-a"): []}
        plan, md, html = compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])
        ln = next((x for x in md.splitlines() if "against base-game objects" in x), "")
        ok = ("TestCairn" in ln and "Farm (ext)" in ln and "TestShrine" in ln and "Hall (int)" in ln
              and len(re.findall(r"\(evidence-index\.md#F-\d+\)", ln)) == 2)
        return ok, ln[:300]

    for name, fn in [("after-review/C914-base-game-line-names-objects-and-places",
                      c914_base_game_line_names_objects_and_places)]:
        cell(name, fn)


    # C925 (reviewer on objectKinds): no role word comes from a model's file name or folder; a Static whose model is
    # named like a marker or sits under effects is said as a static object.
    def c925_no_role_from_model_name():
        R1, R2 = f"00C925:{BASE}", f"00C926:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Placement", "o1", "a1", "v1"),
                  lost_row(OTHER, MOD_A, R2, "Placement", "o2", "a2", "v2")]
        names = {"schema": "names-v1", "places": {}, "objects": {}, "mods": {}, "missing": [],
                 "refs": {R1: {"base": f"0BC095:{BASE}", "carriedBy": [BASE]},
                          R2: {"base": f"0BC096:{BASE}", "carriedBy": [BASE]}},
                 "bases": {f"0BC095:{BASE}": {"type": "Static", "editorId": "TestPost", "model": "Marker_Post.nif"},
                           f"0BC096:{BASE}": {"type": "Static", "editorId": "TestGlow", "model": "Effects\\TestGlow.nif"}}}
        plan, md, _ = core_bundle(p_rows, [], names=names)
        ok = ("editor marker" not in md and "visual effect" not in md and "a static object TestPost" in md
              and "a static object TestGlow" in md)
        return ok, next((ln for ln in md.splitlines() if "For example" in ln), "")[-220:]

    for name, fn in [("after-review/C925-no-role-from-model-name", c925_no_role_from_model_name)]:
        cell(name, fn)


    # C915 (Pilgrim review 6): a quest or switch clash gives its in-game effect from the probe's own row (the switch
    # and the measured depth), and is not listed among the checks whose in-game effect is not described.
    def c915_state_clash_in_game_from_row():
        plan, md, html = state_bundle()
        sec = section_text(md, headings(plan).get("prediction") or "\0")
        it = next((ln for ln in sec.splitlines() if ln.startswith("- **Objects that change with quests or switches")), "")
        miss = next((ln for ln in sec.splitlines() if "show in game is not described" in ln), "")
        ok = ("In game:" in it and "CampEnable" in it and "9 u inside" in it
              and "Objects that change with quests or switches" not in miss)
        return ok, f"{it[-220:]} | {miss[:160]}"

    for name, fn in [("after-review/C915-state-clash-in-game-from-row", c915_state_clash_in_game_from_row)]:
        cell(name, fn)


    # C916 (Clockwork review 3): the largest navmesh losses are ranked by the measured samples of lost ground, never by
    # triangle counts (a navmesh can grow and still lose ground); C918 (Winterhold review 4): the sample is said as a
    # test point that ranks losses, not as an area.
    def nav_loss_bundle():
        n1, n2, n3 = f"000N31:{BASE}", f"000N32:{BASE}", f"000N33:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  8 tris -> 94"),
                  row("CLOBBERED", MOD_A, MOD_B, n3, label="CityExterior03 (ext)  300 tris -> 290")]
        for r_, n_ in zip(p_rows, (16, 1463, 300)):
            r_["Necessary"][0]["PhysicalDetail"] = (f"victim-only ground: {n_} sample(s) of the loser's mesh farther "
                                                    f"than 120u from the winner's")
        return compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                  [MOD_A, MOD_B], patches=[PATCH])

    def c916_largest_by_lost_ground():
        plan, md, _ = nav_loss_bundle()
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        m_ = re.search(r"The largest[^:]*: (.*?)\.", top)
        ex = m_.group(1) if m_ else ""
        ok = bool(ex) and ex.index("CityExterior02") < ex.index("CityExterior03") < ex.index("CityExterior01") \
            if all(x in ex for x in ("CityExterior01", "CityExterior02", "CityExterior03")) else False
        return ok and "8 -> 94" not in ex and "1,463" in ex, top[:400]

    def c918_sample_said_as_test_point():
        n2 = f"000N42:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  689 tris -> 5")]
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n2, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 22597 sample(s) of the loser's mesh farther than 120u from the winner's"}]
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        ok = "22,597" in top and "taken every 64 units" not in top and "not an area" in top
        return ok, top[-300:]

    for name, fn in [("after-review/C916-largest-by-lost-ground", c916_largest_by_lost_ground),
                     ("after-review/C918-sample-said-as-test-point", c918_sample_said_as_test_point)]:
        cell(name, fn)


    # C917 (Clockwork review 4, Winterhold minor 5): broken navmesh links are between navmesh records, never "objects",
    # in the still-there line and in "all are against one ...".
    def c917_navmeshes_not_called_objects():
        T1 = f"000N71:{BASE}"
        rows_ = [row("EDGE-IDX", MOD_B, MOD_A, f"000N7{i}:{MOD_A}", other=T1, label="Town (ext)") for i in (2, 3)]
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): rows_, (NAVOWN, "with-patch"): rows_},
                                         [MOD_A, MOD_B], patches=[PATCH])
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        it = next((ln for ln in md.splitlines() if ln.startswith("- **") and "navmesh link" in ln), "")
        ok = ("navmesh records" in st and " objects" not in st and "against one navmesh record" in it
              and "against one object" not in it)
        return ok, f"{st[:250]} | {it[-200:]}"

    for name, fn in [("after-review/C917-navmeshes-not-called-objects", c917_navmeshes_not_called_objects)]:
        cell(name, fn)


    # C919 (Winterhold review 5): fields are named in plain words (skill data) and a flag number is decoded (1024 on a
    # placed object is Persistent), never the record's raw field name or the bare number.
    def c919_fields_and_flags_in_plain_words():
        R1 = f"00C919:{BASE}"
        chains = [(R1, "PlacedObject", [(BASE, {"SkyrimMajorRecordFlags": "0"}),
                                        (OTHER, {"SkyrimMajorRecordFlags": "InitiallyDisabled"}),
                                        (MOD_A, {"SkyrimMajorRecordFlags": "1024"})])]
        p_rows = [lost_row(OTHER, MOD_A, R1, "SkyrimMajorRecordFlags", "InitiallyDisabled", "1024", "0", before=BASE)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chains, masters=MASTERS_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        ok = ("record flags" in it and "SkyrimMajorRecordFlags" not in it and "Persistent" in it
              and '"1024"' not in it)
        return ok, it[-300:]

    for name, fn in [("after-review/C919-fields-and-flags-in-plain-words", c919_fields_and_flags_in_plain_words)]:
        cell(name, fn)


    # C920 (Missives review 3): a small record item says each side's value (what to forward) and, where skill data has
    # it, what the field does in game.
    def c920_small_record_item_values_and_meaning():
        W1 = f"00C920:{BASE}"
        chains = [(W1, "Worldspace", [(BASE, {"Flags": "SmallWorld"}), (OTHER, {"Flags": "SmallWorld, NoGrass"}),
                                      (MOD_A, {"Flags": "SmallWorld"})])]
        r_ = lost_row(OTHER, MOD_A, W1, "Flags", ["Flags=SmallWorld, NoGrass"], PH, ["Flags=SmallWorld"], before=BASE)
        r_["Summary"] = f"Worldspace TestWorld: Flags - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], p_chains=chains, masters=MASTERS_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        ok = (f'{OTHER} has "SmallWorld, NoGrass"' in it and f'{MOD_A} has "SmallWorld"' in it and "grass" in it.lower()
              .replace("nograss", ""))
        return ok, it[-300:]

    for name, fn in [("after-review/C920-small-record-item-values-and-meaning", c920_small_record_item_values_and_meaning)]:
        cell(name, fn)


    # C921 (Pilgrim review, earlier S6): in the detail list, one object found in three world states is said as one
    # object in 3 world states, never "3 records".
    def c921_state_detail_counts_objects():
        plan, md, html = state_bundle()
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        sec_ = found.split("### Objects that change with quests or switches", 1)[-1].split("###", 1)[0]
        lines = [ln for ln in sec_.splitlines() if ln.startswith("- **")]
        ok = (any("1 object between" in ln and "in 3 world states" in ln for ln in lines)
              and not any("3 records" in ln for ln in lines))
        return ok, " | ".join(x[:200] for x in lines)

    for name, fn in [("after-review/C921-state-detail-counts-objects", c921_state_detail_counts_objects)]:
        cell(name, fn)


    # C926 (found in regen-1: a worldspace's height map quoted in full): a small record item quotes each side's value
    # only where it is short enough to read and forward; a long value (a table) is not quoted.
    def c926_long_values_not_quoted():
        W1 = f"00C926:{BASE}"
        big_a, big_b = "1A" * 400, "2B" * 400
        tr_ = tree_transcript([(W1, "Worldspace", [(BASE, {"MaxHeight.CellData": "00", "MaxHeight.Offset": "0"}),
                                                   (OTHER, {"MaxHeight.CellData": big_a, "MaxHeight.Offset": "1"}),
                                                   (MOD_A, {"MaxHeight.CellData": big_b, "MaxHeight.Offset": "2"})])])
        r_ = lost_row(OTHER, MOD_A, W1, "MaxHeight", ["MaxHeight.Offset=1"], PH, ["MaxHeight.Offset=0"], before=BASE)
        r_["Summary"] = f"Worldspace TestWorld: MaxHeight - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], p_tr=tr_, masters=MASTERS_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return len(it) < 1200 and "1A1A1A1A1A1A" not in it, f"{len(it)} chars: {it[-200:]}"

    for name, fn in [("after-review/C926-long-values-not-quoted", c926_long_values_not_quoted)]:
        cell(name, fn)


    # C927 retired (blind reviews of regen-2): it counted the forwarding clause, which is removed (C931)


    # C928 (found in regen-1, Missives review 2): where a setup's detail lines are folded into one line per plugin pair,
    # seams on cells where the mod undoes its master's water fix are still said as that undo, never by neighbour.
    def c928_folded_water_detail_names_the_undo():
        WB = "environment.water-boundaries"
        W1, W2 = f"009341:{BASE}", f"009343:{BASE}"
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, UPD, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, O2, MOD_A, MOD_B])]
        chains = [(w_, "Cell", [(BASE, {"Water": "000018:Skyrim.esm"}), (UPD, {"Water": "001232:Update.esm"}),
                                (MOD_A, {"Water": "000018:Skyrim.esm"})]) for w_ in (W1, W2)]
        p_rows = [lost_row(UPD, MOD_A, w_, "Water", ["Water=001232:Update.esm"], PH, PH) for w_ in (W1, W2)]
        seams = []
        for w_, nb_, o_ in ((W1, f"009342:{BASE}", OTHER), (W2, f"009344:{BASE}", O2)):
            s_ = row("WATER-SEAM", o_, MOD_A, w_, other=nb_)
            s_["Summary"] = ("Tamriel (1,1) E Tamriel (2,1): joined under the truth (001232:Update.esm z -14000), apart "
                             "under the winner (000018:Skyrim.esm z -14000 | 001232:Update.esm z -14000) — attributed to side B")
            seams.append(s_)
        extra = {(WB, "without-patch"): seams, (WB, "baseline"): seams}
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains, extra_results=extra,
                                  masters={UPD: [BASE], OTHER: [BASE], O2: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE]})
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        sec_ = found.split("### Water", 1)[-1].split("###", 1)[0]
        lines = [ln for ln in sec_.splitlines() if ln.startswith("- **")]
        ok = (bool(lines) and all(f"{MOD_A} undoes its master {UPD}'s fix" in ln for ln in lines)
              and not any(f"between {OTHER}" in ln or f"between {O2}" in ln for ln in lines))
        return ok, " | ".join(x[:220] for x in lines)

    for name, fn in [("after-review/C928-folded-water-detail-names-the-undo", c928_folded_water_detail_names_the_undo)]:
        cell(name, fn)


    # C929 (found in regen-1, Pilgrim review 4): the base-game line links every finding, also one whose object another
    # world state already counted (the shrine in two Dawnguard states).
    def c929_base_game_line_links_every_state():
        X3 = f"000X93:{MOD_A}"
        rows_ = []
        for st_ in ("S1", "S2"):
            r_ = state_row(st_, BASE, MOD_A, X3, S0)
            r_["Summary"] = f"Route StateConditional under Enable{st_}: {X3} [Activator TestShrine] arrives - LikelyBlocked"
            rows_.append(r_)
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): rows_, (STATE, "without-b"): [],
                   (STATE, "without-a"): []}
        plan, md, html = compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])
        ln = next((x for x in md.splitlines() if "against base-game objects" in x), "")
        return len(re.findall(r"\(evidence-index\.md#F-\d+\)", ln)) == 2 and "1 clash" in ln, ln[:300]

    for name, fn in [("after-review/C929-base-game-line-links-every-state", c929_base_game_line_links_every_state)]:
        cell(name, fn)


    # C930 (reviewer on regen-1: BPT's summary read as "left 0"): the summary's parts read as a list; a note inside one
    # part never carries a colon, so "left N record fields" is read as N where carried fields and notes come before it.
    def c930_summary_left_reads_with_notes():
        plan, md, _ = offwin_bundle()
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        parts_ = summ.split(": ", 1)[-1]
        return left_said(summ) == 2 and ":" not in parts_, summ[:300]

    for name, fn in [("after-review/C930-summary-left-reads-with-notes", c930_summary_left_reads_with_notes)]:
        cell(name, fn)


    # C931 (blind reviews of regen-2, BPT and Snazzy blocking): no line says that forwarding a lost change would bring
    # an object back. The records cannot tell: the winner may switch the object off by other fields as well, or a patch
    # may remove it on purpose. The off-for-good note keeps only its count and "no effect in game".
    def c931_no_forwarding_claim():
        plan, md, html = offwin_bundle()
        # (fourth review, Winterhold S2) the note says what holds for ModA's own version (on): "so not drawn"
        ok = ("would bring" not in md and "would bring" not in _html.unescape(html) and "on/off field" not in md
              and "sets off for good, not drawn" in md and "no effect in game" not in md)
        hits = [ln[:160] for ln in md.splitlines() if "would bring" in ln]
        return ok, f"{hits[:2]}"

    for name, fn in [("after-review/C931-no-forwarding-claim", c931_no_forwarding_claim)]:
        cell(name, fn)


    # ------------------------------------------------ after the third review round (C932-C999)
    # C932 (Winterhold review S1): a lost change a declared patch wins, keeping the value that wins without it (000703),
    # is the patch's resolution of that record: it is not in the still-there line, which holds only what no patch
    # touches (000702), and the with-patch line says the patch keeps ModB's value over ModA's change.
    def c932_patch_kept_value_not_still_there():
        plan, md, html = record_validation_bundle()
        pr_ = reader(plan).get("prediction") or {}
        st = pair_parts(pr_.get("withPatchStays"), MOD_A, MOD_B)
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        only_left = (any(re.search(re.escape(MOD_A) + r"'s changes to 1 record field lost to " + re.escape(MOD_B) + r"$",
                                   re.sub(r"^Still there with [^:]*: ", "", x).rstrip(".")) for x in st)
                     and not any("carr" in x for x in st))
        said = f"the patch keeps {MOD_B}'s value over {MOD_A}'s change on 1" in kl and f"{MOD_A}'s change stays lost" not in kl
        return only_left and said, f"{st}; {kl[:240]}"

    # C933 (Snazzy review S2): ModA moves two objects below the world and ModB keeps them in place. On 00C933 nothing
    # patches the record (left); on 00C934 the patch carries ModB's version (kept: the patch keeps the object on). Only
    # 00C933 is said as an object ModB still brings back; 00C934 is the patch's choice and is not in the still-there line.
    def c933_brings_back_only_untouched():
        D1, D2 = f"00C933:{BASE}", f"00C934:{BASE}"
        # (fourth review, Winterhold S1) ModB keeps the object at its spot: a move across its size would make it ModB's
        # own placement (C1020), not the object ModA removes brought back
        a_, b_ = offs(-30000), offs(0)
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)
                  for D in (D1, D2)]
        w_rows = [lost_row(MOD_A, MOD_B, D1, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE),
                  lost_row(MOD_A, PATCH, D2, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)]) for D in (D1, D2)]
        w_ch = [(D1, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)]),
                (D2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_), (PATCH, b_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]})
        line = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        # (seventh review, BPT r6 S3) the still-there part may say ModB only carries the base game's value (the
        # attribution read); what must not appear is a patch's carried value
        ok = (re.search(r"still brings back 1 object\b", line) is not None and D1 in line and D2 not in line
              and "carried by a patch" not in line and "patch carries" not in line)
        return ok, line[:400]

    # C934 (Clockwork review S1): an overlap pair still crossing with the patch, which the setup with the patch does not
    # list between two mods, where the patch places one of the objects (the record chain: the patch's version of X loads
    # last and moves it), is the patch's placement: not "left", not in the still-there line, said apart.
    def placed_overlap_bundle(patch_moves=True, listed=False, details=None, before=None):
        """before: each pair's PhysicalDetail in the setup without the patch (its own row)."""
        X = f"000X34:{MOD_A}"
        ys = [f"000Y3{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        for i_, r_ in enumerate(p_rows if before else []):
            r_["Necessary"][0]["PhysicalDetail"] = before[i_]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds",
                  **({"Detail": details[i_]} if details else {})} for i_, y in enumerate(ys)]
        other_ = f"00C93A:{BASE}"
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        x_chain = [(MOD_A, {"Placement.Position": "1,1,1"})] + ([(PATCH, {"Placement.Position": "9,9,9"})]
                                                                if patch_moves else [])
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", x_chain),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows,
                                                            (OVERLAP, "with-patch"): list(p_rows) if listed else []},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                         enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         transcripts=tr)
        return plan, md, X

    def c934_patch_placed_not_left():
        plan, md, X = placed_overlap_bundle()
        pr_ = reader(plan).get("prediction") or {}
        wp = sorted(str(e.get("withPatch")) for e in pr_.get("record") or [] if e.get("formKey") == X)
        kl = next((x for x in pr_.get("withPatchLines") or [] if "overlap pairs)" in x), "")
        st = str(pr_.get("withPatchStays") or "")
        ok = (wp == ["patchPlaced", "patchPlaced"] and not re.search(r"\bleft \d", kl)
              and re.search(r"\b2\b[^;]*a patch places", kl) is not None and "overlap pairs between" not in st)
        return ok, f"{wp}; {kl[:200]}; {st[:200]}"

    # C935, C936 (guards for C934): the same pair stays left where no patch places either object (C935) and where the
    # setup with the patch still lists it between the two mods (C936).
    def c935_unplaced_overlap_stays_left():
        plan, md, X = placed_overlap_bundle(patch_moves=False)
        wp = sorted(str(e.get("withPatch")) for e in (reader(plan).get("prediction") or {}).get("record") or []
                    if e.get("formKey") == X)
        return wp == ["left", "left"], f"{wp}"

    def c936_listed_overlap_stays_left():
        plan, md, X = placed_overlap_bundle(listed=True)
        wp = sorted(str(e.get("withPatch")) for e in (reader(plan).get("prediction") or {}).get("record") or []
                    if e.get("formKey") == X)
        return wp == ["left", "left"], f"{wp}"

    for name, fn in [("third-review/C932-patch-kept-value-not-still-there", c932_patch_kept_value_not_still_there),
                     ("third-review/C933-brings-back-only-untouched", c933_brings_back_only_untouched),
                     ("third-review/C934-patch-placed-not-left", c934_patch_placed_not_left),
                     ("third-review/C935-unplaced-overlap-stays-left", c935_unplaced_overlap_stays_left),
                     ("third-review/C936-listed-overlap-stays-left", c936_listed_overlap_stays_left)]:
        cell(name, fn)

    # C937 (Missives review S1): OtherMod's value of a cell's Location equals HearthFires' own change, which OtherMod (built
    # on Skyrim.esm only) only carries; ModA, also built without HearthFires, has the earlier value and wins. The loss is
    # ModA's against HearthFires', not a clash with OtherMod: set aside as no edit of OtherMod's and said once as ModA
    # dropping HearthFires' fix. C965 (guard): where OtherMod's value is its own (not HearthFires'), it stays counted.
    def base_fix_bundle(other_value):
        L1 = f"00C937:{BASE}"
        arms = [arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": other_value}), (MOD_A, {"Location": ""})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", [f"Location={other_value}"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        return plan, md, L1

    def c937_base_fix_carried_is_not_a_clash():
        plan, md, L1 = base_fix_bundle("018A59:Skyrim.esm")
        adm = [(e["A"], e["admission"]) for e in rec_of(plan, L1)]
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        ok = ((OTHER, "notOwnEdit") in adm and OTHER in line and MOD_A in line and "Location" in line
              and not any(ln.startswith("- **Cell settings") for ln in md.splitlines()))
        return ok, f"{adm}; {line[:300]}"

    def c965_own_value_stays_counted():
        plan, md, L1 = base_fix_bundle("0AAAAA:Skyrim.esm")
        adm = [(e["A"], e["admission"]) for e in rec_of(plan, L1)]
        return (OTHER, "admitted") in adm, f"{adm}"

    for name, fn in [("third-review/C937-base-fix-carried-is-not-a-clash", c937_base_fix_carried_is_not_a_clash),
                     ("third-review/C965-own-value-stays-counted", c965_own_value_stays_counted)]:
        cell(name, fn)

    # C966 (Missives review S1, consistency with 01A27A): OtherMod is built on HearthFires and only inherits its fix (no
    # change of its own, set aside as before); ModA drops that fix. The loss is said on the same base-fix line as C937's.
    def c966_inherited_base_fix_loss_said():
        L1 = f"00C966:{BASE}"
        arms = [arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE]})
        adm = [(e["A"], e["admission"]) for e in rec_of(plan, L1)]
        lines = [ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln]
        return (OTHER, "notOwnEdit") in adm and len(lines) == 1 and OTHER in lines[0], f"{adm}; {lines[:1]}"

    for name, fn in [("third-review/C966-inherited-base-fix-loss-said", c966_inherited_base_fix_loss_said)]:
        cell(name, fn)

    # C940 (Winterhold review S4, Clockwork S2, BPT S4): a detail line on the setup with the patch says how many of its
    # rows the top counts and how many were set aside, joined to the prediction's rows by identity, as the prediction
    # setup's lines do: D1 (counted, still lost with the patch) and D2 (both mods set the object off for good: set aside).
    def c940_patched_detail_split_by_identity():
        D1, D2 = f"00C940:{BASE}", f"00C941:{BASE}"
        a_ = offs(0)
        a_["Placement.Position"] = "300,100,0"
        b_ = offs(0)
        b_["Placement.Position"] = "500,100,0"
        a_low, b_low = offs(-30000), offs(-30000)
        a_low["Placement.Position"] = "300,100,-30000"
        rows_ = [lost_row(MOD_A, MOD_B, D1, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE),
                 lost_row(MOD_A, MOD_B, D2, "Placement", off_txt(a_low), off_txt(b_low), off_txt(offs(0)), before=BASE)]
        ch_ = [(D1, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)]),
               (D2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_low), (MOD_B, b_low)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        adm = sorted((e["formKey"][:6], e["admission"]) for e in (reader(plan).get("prediction") or {}).get("record") or [])
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        line = next((ln for ln in found.splitlines() if "2 record fields" in ln and "Measured load order" in ln), "")
        return "(1 counted at the top; 1 set aside)" in line, f"{adm}; {line[:300]}"

    for name, fn in [("third-review/C940-patched-detail-split-by-identity", c940_patched_detail_split_by_identity)]:
        cell(name, fn)

    # C967 (Clockwork review S2): a detail entry on the prediction setup whose rows are all set aside says so, so the
    # list adds up with the top (32 listed against 31 counted, the extra one set aside as already there).
    def c967_all_set_aside_entry_marked():
        D2 = f"00C967:{BASE}"
        a_low, b_low = offs(-30000), offs(-30000)
        a_low["Placement.Position"] = "300,100,-30000"
        rows_ = [lost_row(MOD_A, MOD_B, D2, "Placement", off_txt(a_low), off_txt(b_low), off_txt(offs(0)), before=BASE)]
        ch_ = [(D2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_low), (MOD_B, b_low)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        line = next((ln for ln in found.splitlines() if "Tested alternative setup" in ln and "record field" in ln), "")
        return "(1 set aside)" in line, line[:300]

    for name, fn in [("third-review/C967-all-set-aside-entry-marked", c967_all_set_aside_entry_marked)]:
        cell(name, fn)

    # C938 (Missives review S2): OtherMod and Neighbour.esp each change a worldspace's MaxHeight the same way (Neighbour
    # loads later; the row names OtherMod); ModA, which wins, keeps the base game's value. The item names both plugins
    # holding the lost value, says the lost value is an absence (OtherMod has no such data), and that ModA's is the base
    # game's value.
    def c938_carriers_absence_and_base_value_said():
        O2 = "Neighbour.esp"
        W1 = f"00C938:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A, MOD_B])]
        mh_b, mh_o = {"MaxHeight.CellData": "05", "MaxHeight.Offset": "5"}, {"MaxHeight.CellData": "00", "MaxHeight.Offset": "0"}
        tr_ = tree_transcript([(W1, "Worldspace", [(BASE, mh_b), (OTHER, mh_o), (O2, dict(mh_o)), (MOD_A, dict(mh_b))])])
        r_ = lost_row(OTHER, MOD_A, W1, "MaxHeight", [f"MaxHeight.CellData only in {MOD_A}: 00"], PH, PH)
        r_["Summary"] = f"Worldspace TestWorld: MaxHeight - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], p_tr=tr_, arms=arms,
                                  masters={OTHER: [BASE], O2: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        ok = (f"{O2} holds the same value" in it and f"{OTHER} has no " in it
              and f"{MOD_A}'s value" in it and "base game's" in it)
        return ok, it[-400:]

    for name, fn in [("third-review/C938-carriers-absence-and-base-value-said", c938_carriers_absence_and_base_value_said)]:
        cell(name, fn)

    # C942 (Pilgrim review S2): a clash new with the patch that the setup without the evaluated mod already has (the
    # patch brings back that setup's own state) is not counted against the patch in the summary: "new" there counts only
    # what no setup without the patch has; the brought-back clash is said apart.
    def c942_summary_new_excludes_brought_back():
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A]), arm("without-a", [BASE, OTHER, O2])]
        r_ = row("OVERLAP", OTHER, O2, f"000X42:{OTHER}", other=f"000Y42:{O2}")
        results = {(OVERLAP, "baseline"): [r_], (OVERLAP, "without-patch"): [], (OVERLAP, "without-a"): [r_]}
        plan, md, _ = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        ok = "new 1" not in summ and "brought back" in summ
        return ok, summ[:300]

    for name, fn in [("third-review/C942-summary-new-excludes-brought-back", c942_summary_new_excludes_brought_back)]:
        cell(name, fn)

    # C948 (Clockwork review S6): the separate evaluation's setups are named against the measured load order, which holds
    # the patch: its setup without ModA lacks the patch too, so it is "the measured load order without the patch and
    # ModA", never "without ModA" alone.
    def c948_sibling_setups_against_measured_order():
        O2, O3, O4 = "Neighbour.esp", "Third.esp", "Fourth.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, O3, O4, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, O3, O4, MOD_A])]
        rows_ = [row("CONTACT", OTHER, MOD_A, f"000C48:{MOD_A}")]
        s_arms = [arm("baseline", [BASE, OTHER, O2, O3, O4, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER, O2, O3, O4])]
        s_results = {(CONTACT, "baseline"): rows_, (CONTACT, "without-a"): []}
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): rows_, (CONTACT, "without-patch"): rows_},
                                         [MOD_A], patches=[PATCH],
                                         sibling=(s_arms, s_results, [MOD_A], {"plan_id": "sibl00000948"}))
        ln = next((x for x in md.splitlines() if "separate evaluation" in x and "measured these setups" in x), "")
        return f"the measured load order without {MOD_A}, {PATCH}" in ln, ln[:300]

    for name, fn in [("third-review/C948-sibling-setups-against-measured-order", c948_sibling_setups_against_measured_order)]:
        cell(name, fn)

    # C947 (Clockwork review S5): a clash the patch resolves that is already there without the evaluated mods is named
    # by its pair and said as an earlier clash the patch also fixes, never a bare count that reads as unrelated.
    def c947_unpredicted_fix_names_pairs():
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A]), arm("without-a", [BASE, OTHER, O2])]
        r_ = row("OVERLAP", OTHER, O2, f"000X47:{OTHER}", other=f"000Y47:{O2}")
        results = {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r_], (OVERLAP, "without-a"): [r_]}
        plan, md, _ = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        ln = next((x for x in md.splitlines() if "but not predicted" in x), "")
        ok = f"{OTHER} and {O2}" in ln and "also" in ln
        return ok, ln[:300]

    for name, fn in [("third-review/C947-unpredicted-fix-names-pairs", c947_unpredicted_fix_names_pairs)]:
        cell(name, fn)

    # C946 (Clockwork review S3): a patch-wins line counts fields over several plugin pairs; it links the evidence of
    # every pair it counts (here two: ModB's change lost to the patch on 000701, ModA's on 000703), so its count can be
    # recomputed from the linked rows, never from the first pair's alone.
    def c946_patch_wins_line_links_every_pair():
        plan, md, html = record_validation_bundle()
        ln = next((x for x in md.splitlines() if "where a declared patch's value wins" in x), "")
        links = re.findall(r"\(evidence-index\.md#(F-\d+)\)", ln)
        return len(set(links)) == 2 and "2 plugin pairs" in ln, ln[:300]

    for name, fn in [("third-review/C946-patch-wins-line-links-every-pair", c946_patch_wins_line_links_every_pair)]:
        cell(name, fn)

    # C949 (Clockwork review S4): the Markdown evidence index lists each prediction record row on its own line (the row
    # ends in a block tag, whose newline the template engine trims, so the rows ran together into one bullet).
    def c949_index_record_one_row_per_line():
        plan, md, html, index = patched_with_sibling("same")
        n_rec = len((reader(plan).get("prediction") or {}).get("record") or [])
        sec = index.split('<a id="prediction-record"></a>', 1)[-1].split("\n## ", 2)[1] if "prediction-record" in index else ""
        bullets = [ln for ln in sec.splitlines() if ln.startswith("- ")]
        return n_rec > 1 and len(bullets) == n_rec, f"{n_rec} rows, {len(bullets)} bullets"

    for name, fn in [("third-review/C949-index-record-one-row-per-line", c949_index_record_one_row_per_line)]:
        cell(name, fn)

    # C950 (BPT review S1): the headline's record-field count carries the split its item gives: how many of the fields
    # are on objects the winning version sets off for good (offwin_bundle: D1 and D3 of 3).
    def c950_headline_says_off_for_good():
        plan, md, _ = offwin_bundle()
        verdict = str((reader(plan).get("prediction") or {}).get("verdict") or "")
        # (fifth review, BPT S2) the headline says how many of ModB's removals are its own: D3's (D1's is an earlier
        # mod's that ModB carries, since the sixth review's fixture change for C900-C931)
        return re.search(r"\(3 record fields, 2 of them on objects the winning version sets off for good, 1 by the "
                         r"winner's own removal\)", verdict) is not None, verdict[:300]

    for name, fn in [("third-review/C950-headline-says-off-for-good", c950_headline_says_off_for_good)]:
        cell(name, fn)

    # C952 (Snazzy review S4, Gate A finding 1): a lost water height is not set aside as "no effect": the item gives both
    # values, the base game's no-water value said in words, and says the effect depends on the cell's water flag, which
    # this record data does not show (no provider's flags differ here, so none was read).
    def c952_water_height_values_and_flag():
        C1 = f"00C952:{BASE}"
        tr_ = tree_transcript([(C1, "Cell", [(BASE, {"WaterHeight": "-2147483600"}), (OTHER, {"WaterHeight": "0"}),
                                             (MOD_A, {"WaterHeight": "-2147483600"})])])
        r_ = lost_row(OTHER, MOD_A, C1, "WaterHeight", ["WaterHeight=0"], PH, PH, label="TestCellWet")
        r_["Summary"] = f"Cell TestCellWet: WaterHeight - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], check=CELLATTR, masters=MASTERS_, p_tr=tr_)
        adm = [e["admission"] for e in rec_of(plan, C1)]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Cell settings")), "")
        # (round 2, R) no file is named for ModA here, so the flag was not read: said so, never "not in the record data"
        ok = (adm == ["admitted"] and "no water" in it and "-2147483600" not in it and "HasWater" in it
              and "not read for this report" in it and "not in the record data" not in it)
        return ok, f"{adm}; {it[-400:]}"

    for name, fn in [("third-review/C952-water-height-values-and-flag", c952_water_height_values_and_flag)]:
        cell(name, fn)

    # C944 (Sewers review S2): the still-there line's per-place figure names its unit (overlap pairs), so it is not read
    # as part of the object count it follows.
    def c944_still_there_place_count_has_unit():
        X = f"000X44:{MOD_A}"
        ys = [f"000Y4{i}:{MOD_B}" for i in (1, 2, 3)]
        labels = ["PlaceNorth (int)", "PlaceNorth (int)", "PlaceSouth (int)"]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, label=l_, other=y) for y, l_ in zip(ys, labels)]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): p_rows},
                                         [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds})
        st = next((ln for ln in md.splitlines() if ln.startswith("Still there with")), "")
        return "most in PlaceNorth (int) (2 overlap pairs)" in st, st[:300]

    for name, fn in [("third-review/C944-still-there-place-count-has-unit", c944_still_there_place_count_has_unit)]:
        cell(name, fn)

    # C954 (Snazzy re-review on regen-3): "N of them objects already counted above" names the shared objects, so it is
    # not read as N of the check's own inconsistencies (C501's fixture: 000F01 and 000F02 are counted by both checks).
    def c954_shared_objects_named():
        ct = [row("CONTACT", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 3, 4)]
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F0{i}:{MOD_A}", other=f"000H0{i}:{MOD_B}") for i in (1, 2, 5)]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("dependency-only", "plus-a", "plus-b")}
        results.update({(FIXTURE, "baseline"): fx, (CONTACT, "baseline"): ct})
        plan, md, html = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        ok = f"000F01:{MOD_A}" in v and f"000F02:{MOD_A}" in v and "already counted above" in v
        return ok, f"verdict {v!r}"

    for name, fn in [("third-review/C954-shared-objects-named", c954_shared_objects_named)]:
        cell(name, fn)

    # C968 (guard for C937, the lane-core ruling on 077E16/17): OtherMod is built on Update.esm and puts the record's
    # original (Skyrim.esm) value back over Update.esm's change; ModA, which wins, carries Update.esm's value. Restoring
    # the base game's first value over one's own master is that mod's own change: it stays counted. Only a later
    # base-game plugin's own change (a DLC or update value) is set aside as carried (C937).
    def c968_restoring_first_value_over_master_stays_counted():
        L1 = f"00C968:{BASE}"
        UPD_ = "Update.esm"
        arms = [arm("baseline", [BASE, UPD_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": "011111:Skyrim.esm"}), (UPD_, {"Location": "022222:Skyrim.esm"}),
                                (OTHER, {"Location": "011111:Skyrim.esm"}), (MOD_A, {"Location": "022222:Skyrim.esm"})])]
        p_rows = [lost_row(OTHER, MOD_A, L1, "Location", ["Location=011111:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={UPD_: [BASE], OTHER: [BASE, UPD_], MOD_A: [BASE, UPD_], MOD_B: [BASE]})
        adm = [(e["A"], e["admission"]) for e in rec_of(plan, L1)]
        return (OTHER, "admitted") in adm, f"{adm}"

    for name, fn in [("third-review/C968-restoring-first-value-over-master-stays-counted",
                      c968_restoring_first_value_over_master_stays_counted)]:
        cell(name, fn)

    # C943 (Sewers review S1, Gate A answer 3 option b): a broken link from a base-game navmesh record that the other mod
    # only carries (the probe's provider chain: Skyrim.esm -> OtherMod) is not said as that mod's doing: the item says the
    # navmesh is the base game's, carried by it, and that whether it changed the link is not in the record data.
    def c943_base_navmesh_carried_said():
        n1 = f"000N43:{BASE}"
        rows_ = []
        for i in (1, 2):
            r_ = row("EDGE-IDX", OTHER, MOD_A, f"000E4{i}:{BASE}", label="CityExterior04 (ext)", other=n1)
            r_["Summary"] = (f"navmesh 000E4{i}:{BASE} in CityExterior04 (ext) edge-links {n1} triangle 50{i} of 432   "
                             f"[record provided by: {BASE} -> {OTHER}]")
            rows_.append(r_)
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]
        plan, md, _ = compile_and_render(arms, {(NAVOWN, "baseline"): rows_, (NAVOWN, "without-a"): []}, [MOD_A])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Navmesh")), "")
        # (reviewer on faa9ce47) the counted line names the pair as the evaluated mod against the base game's navmesh,
        # never as a clash with the mod that only carries it
        head_ = it.split(". ", 1)[0]
        # (round 2, B1) no plugin file is named here, so the links were not read: said so, never "not in the record data"
        ok = ("base-game navmesh" in it and f"{OTHER} carries" in it and "not read for this report" in it
              and f"the base game's navmesh records {OTHER} carries" in head_ and f"and {OTHER}" not in head_
              and f"between {OTHER}" not in head_)
        return ok, it[:300]

    for name, fn in [("third-review/C943-base-navmesh-carried-said", c943_base_navmesh_carried_said)]:
        cell(name, fn)

    # C941 (Pilgrim review S1, Gate A answer 2): a losing mod whose version only starts an object disabled (no enable
    # parent), where no record of the losing plugin refers to the object, keeps it off in its own game too; with the
    # winner's version off for good, the lost change has no effect: set aside. Decided by a read-only scan of the losing
    # plugin file the bundle names (its recorded bytes), FormIDs through the plugin's master list, compressed records
    # decompressed, script properties included. Any reference keeps the row counted; no read keeps it counted, with the
    # "may enable" clause said of the losing mod's version only.
    def plugin_bytes(masters, records):
        """A minimal plugin: a TES4 header with its masters, then one group holding (type, formId, [(sig, data)], compressed)."""
        def sub(sig, data):
            return sig.encode() + struct.pack("<H", len(data)) + data
        hdr = sub("HEDR", struct.pack("<fII", 1.7, len(records), 0x800)) + b"".join(
            sub("MAST", m.encode() + b"\x00") + sub("DATA", b"\x00" * 8) for m in masters)
        out = b"TES4" + struct.pack("<IIIIHH", len(hdr), 0, 0, 0, 44, 0) + hdr
        body = b""
        for typ, fid, subs, comp in records:
            data = b"".join(sub(s_, d_) for s_, d_ in subs)
            flags = 0
            if comp:
                data, flags = struct.pack("<I", len(data)) + zlib.compress(data), 0x00040000
            body += typ.encode() + struct.pack("<IIIIHH", len(data), flags, fid, 0, 44, 0) + data
        return out + b"GRUP" + struct.pack("<I4sIIHH", 24 + len(body), b"QUST", 0, 0, 0, 0) + body

    def write_plugin(name, referenced):
        d_ = Path(tempfile.mkdtemp(prefix="c941-"))
        recs = [("REFR", 0x0000C941, [("NAME", struct.pack("<I", 0x00000101)), ("XESP", b"")], False)]
        if referenced:   # a quest script's object property pointing at the light, in a compressed record
            recs.append(("QUST", 0x01000801, [("VMAD", b"\x05\x00\x02\x00\x01\x00" + struct.pack("<I", 0x0000C941))], True))
        path_ = d_ / name
        path_.write_bytes(plugin_bytes([BASE], recs))
        return path_

    def c941a_refscan_known_answers():
        import refscan
        yes_ = refscan.scan(write_plugin(OTHER, True), [f"00C941:{BASE}"])["refs"][f"00C941:{BASE}"]
        no_ = refscan.scan(write_plugin(OTHER, False), [f"00C941:{BASE}"])["refs"][f"00C941:{BASE}"]
        ok = [r_["sub"] for r_ in yes_] == ["VMAD"] and no_ == []
        return ok, f"with {yes_}; without {no_}"

    def start_off_bundle(plugin_path):
        D1 = f"00C941:{BASE}"
        a_ = offs(-3904, "InitiallyDisabled")
        b_ = offs(-30000)
        b_["Placement.Position"] = "100,100,-30000"
        p_rows = [lost_row(OTHER, MOD_A, D1, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        ch_ = [(D1, "PlacedObject", [(BASE, offs(0)), (OTHER, a_), (MOD_A, b_)])]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        if plugin_path is not None:
            sha_ = hashlib.sha256(Path(plugin_path).read_bytes()).hexdigest()
            for a__ in arms:
                for pl_ in a__["requested"]["plugins"]:
                    if pl_["name"] == OTHER:
                        pl_["path"], pl_["sha256"] = str(plugin_path), sha_
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=ch_, w_chains=ch_, arms=arms,
                                  masters={**MASTERS_, PATCH: [BASE]})
        return plan, md, D1

    def c941b_unreferenced_start_off_set_aside():
        plan, md, D1 = start_off_bundle(write_plugin(OTHER, False))
        adm = [e["admission"] for e in rec_of(plan, D1)]
        return adm == ["offUnreferenced"], f"{adm}"

    def c941c_referenced_or_unread_stays_counted():
        plan1, md1, D1 = start_off_bundle(write_plugin(OTHER, True))
        plan2, md2, _ = start_off_bundle(None)
        a1, a2 = [e["admission"] for e in rec_of(plan1, D1)], [e["admission"] for e in rec_of(plan2, D1)]
        it = next((ln for ln in md2.splitlines() if ln.startswith("- **Record fields")), "")
        ok = (a1 == ["admitted"] and a2 == ["admitted"] and f"{OTHER}'s version only starts the object disabled" in it
              and "In 1 of them the object starts disabled" not in it)
        return ok, f"{a1} {a2}; {it[-300:]}"

    for name, fn in [("third-review/C941a-refscan-known-answers", c941a_refscan_known_answers),
                     ("third-review/C941b-unreferenced-start-off-set-aside", c941b_unreferenced_start_off_set_aside),
                     ("third-review/C941c-referenced-or-unread-stays-counted", c941c_referenced_or_unread_stays_counted)]:
        cell(name, fn)

    # ------------------------------------------------ third review, in-game meaning and the BPT re-review (C955-C964, C969)
    # C955 (BPT review S2, Gate A finding 2): ModB's lost value switches the object off (below the world) and ModA's whole
    # version has it on when the game loads (not initially disabled, not deleted, no enable parent, not below the world):
    # the item says that without a patch the object is drawn. C969 (guard): where ModA's version has an enable parent
    # whose state the record data does not give, only that ModA's own record does not switch it off is said.
    def switch_off_bundle(win_parent=None):
        D1 = f"00C955:{BASE}"
        lost_ = offs(-30000)
        win_ = offs(0, "0", win_parent)
        if win_parent:
            win_["EnableParent.Flags"] = "0"
        rows_ = [lost_row(MOD_B, MOD_A, D1, "Placement", off_txt(lost_), off_txt(win_), off_txt(offs(0)), before=BASE)]
        ch_ = [(D1, "PlacedObject", [(BASE, offs(0)), (MOD_B, lost_), (MOD_A, win_)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        return plan, md

    def c955_switched_off_drawn_without_patch():
        plan, md = switch_off_bundle()
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return f"{MOD_B}'s value switches the object off" in it and "is drawn" in it, it[-400:]

    def c969_enable_parent_not_said_drawn():
        plan, md = switch_off_bundle(win_parent=f"000F69:{OTHER}")
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("is drawn" not in md and f"{MOD_B}'s value switches the object off" in it
                and f"{MOD_A}'s own record does not switch it off" in it), it[-400:]

    # C958 (Snazzy review S1): a record item that is mostly one field gives that field's in-game meaning from skill data
    # (position: where the object stands).
    def c958_main_field_in_game():
        plan, md, _ = offwin_bundle()
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return "where the object stands" in it, it[-300:]

    # C961 (Pilgrim review S4): the load-order risk line says what its main fields do in game (lighting, image space).
    def c961_order_risk_fields_in_game():
        O2 = "Early.esp"
        p_rec = [lost_row(MOD_A, OTHER, f"000C61:{BASE}", "Lighting", "a", "o", "v")]
        arms = [arm("baseline", [BASE, O2, MOD_A, OTHER], kind="baseline")]
        o_rows = [dict(lost_row(OTHER, MOD_A, f"000C61:{BASE}", f_, "o", "a", "v"), _check=CELLATTR, _type="Cell")
                  for f_ in ("Lighting", "ImageSpace")]
        setup = {"armId": "lux-first", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": plugins(BASE, O2, OTHER, MOD_A), "rows": o_rows}
        plan, md, html = compile_and_render(arms, {(CONTACT, "baseline"): []}, [MOD_A],
                                            count_only={(CELLATTR, "baseline"): 1},
                                            enrichment={(CELLATTR, "baseline"): p_rec}, order_setups=[setup])
        ln = next((x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or []), "")
        return "light and fog" in ln and "colour grading" in ln and "how it shows in game is not described" not in ln, ln[-300:]

    # C962 (BPT re-review S1): rows on a with-patch detail line whose two values name the same file but for letter case
    # are said so, never as new clashes with the patch.
    def c962_case_only_rows_said_on_detail_line():
        T1 = f"00C962:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, f"00C963:{BASE}", "Owner", "a", "b", "v")]
        w_rows = list(p_rows) + [lost_row(PATCH, OTHER, T1, "HdLodDiffuseTexture", ["HdLodDiffuseTexture=Textures\\Land\\A.dds"],
                                          ["HdLodDiffuseTexture=textures\\land\\a.dds"], "v")]
        plan, md, _ = core_bundle(p_rows, w_rows, targets=(MOD_A, MOD_B))
        ln = next((x for x in md.splitlines() if OTHER in x and PATCH in x and "whose change is lost" in x), "")
        return "letter case" in ln and "new with the patch" not in ln, ln[:300]

    # C963 (BPT re-review S2): a quest or switch item whose rows sink objects to different depths gives the range and
    # the deepest, never one row's depth for all.
    def c963_state_depth_range():
        rows_ = []
        for i_, d_ in ((1, "11.2"), (2, "83.1")):
            r_ = row(f"STATE-CONTACT@S1@Triggered", MOD_A, MOD_B, f"000X6{i_}:{MOD_A}", label="Camp (ext)", other=Y)
            r_["Summary"] = f"Contact StateConditional under CampEnable: 000X6{i_}:{MOD_A} arrives - Sunk: sits {d_}u inside something"
            rows_.append(r_)
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): rows_, (STATE, "without-b"): [], (STATE, "without-a"): []}
        plan, md, html = compile_and_render(STATE_ARMS, results, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects that change with quests or switches")), "")
        return "11.2" in it and "83.1" in it, it[-300:]

    # C964 (BPT re-review S3): an object of a light base with no model has no body, against a base-game object too: it
    # is set aside as no body, never counted on the base-game line.
    def c964_bodiless_light_against_base_game():
        L1, H1 = f"000L64:{MOD_A}", f"000H64:{BASE}"
        rows_ = [row("CONTACT", BASE, MOD_A, L1, other=H1)]
        refs = {L1: {"base": f"0B0L64:{MOD_A}"}, H1: {"base": f"0B0H64:{BASE}"}}
        bases = {f"0B0L64:{MOD_A}": {"type": "Light", "model": ""}, f"0B0H64:{BASE}": {"type": "Static", "model": "rock.nif"}}
        results = {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): rows_}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): rows_, (CONTACT, "with-patch"): rows_},
                                            [MOD_A, MOD_B], patches=[PATCH], names={"refs": refs, "bases": bases})
        adm = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or [] if e["formKey"] == L1]
        return adm == ["noBody"], f"{adm}"

    for name, fn in [("third-review/C955-switched-off-drawn-without-patch", c955_switched_off_drawn_without_patch),
                     ("third-review/C969-enable-parent-not-said-drawn", c969_enable_parent_not_said_drawn),
                     ("third-review/C958-main-field-in-game", c958_main_field_in_game),
                     ("third-review/C961-order-risk-fields-in-game", c961_order_risk_fields_in_game),
                     ("third-review/C962-case-only-rows-said-on-detail-line", c962_case_only_rows_said_on_detail_line),
                     ("third-review/C963-state-depth-range", c963_state_depth_range),
                     ("third-review/C964-bodiless-light-against-base-game", c964_bodiless_light_against_base_game)]:
        cell(name, fn)

    # C956 (BPT review S3): a new problem with the patch names who moved the support, from the probe's own rows of the
    # setup with the patch ("which X Moved"), so a support the patch moved is not read as the evaluated mod's doing.
    def c956_new_problem_names_mover():
        fk_ = f"000S56:{OTHER}"
        r1 = row("STRANDED", MOD_A, OTHER, fk_, other=f"000H56:{BASE}")
        r1["Summary"] = (f"Stranded: Static Shade Supported on Static Cap01 which {MOD_A} Moved; nearest thing now 128u  "
                         f"{fk_} / 000H56:{BASE} in Hall (int)")
        r2 = row("STRANDED", PATCH, OTHER, fk_, other=f"000H57:{BASE}")
        r2["Summary"] = (f"Stranded: Static Shade Supported on Static Win02 which {PATCH} Moved; nearest thing now 128u  "
                         f"{fk_} / 000H57:{BASE} in Hall (int)")
        refs = {fk_: {"base": f"0B0S56:{OTHER}"}}
        bases = {f"0B0S56:{OTHER}": {"type": "Static", "editorId": "Shade", "model": "shade.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): [r1, r2]},
                                            [MOD_A, MOD_B], patches=[PATCH], names={"refs": refs, "bases": bases})
        ln = next((x for x in md.splitlines() if x.startswith("- New clashes with the patch, by check")), "")
        return f"moved by {MOD_A} and {PATCH}" in ln or f"moved by {PATCH} and {MOD_A}" in ln, ln[:300]

    for name, fn in [("third-review/C956-new-problem-names-mover", c956_new_problem_names_mover)]:
        cell(name, fn)

    # C957 (Sewers review S3): an overlap the quest-or-switch check also finds says what that check found: the other
    # mod's object leaves when its switch is set, so the overlap holds only until then.
    def c957_also_found_switch_said():
        X, Yo = f"000X57:{MOD_A}", f"000Y57:{MOD_B}"
        ov_ = row("OVERLAP", MOD_B, MOD_A, X, other=Yo)
        st_ = row("STATE-CONTACT@000S57:ModB.esp@Unknown", MOD_B, MOD_A, Yo, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {Yo} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip")
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A]), arm("without-a", [BASE, MOD_B])]
        results = {(OVERLAP, a_): ([ov_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch", "without-b", "without-a")}
        results.update({(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch", "without-b", "without-a")})
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        return "PlankSwitch" in it and "leaves" in it, it[-400:]

    for name, fn in [("third-review/C957-also-found-switch-said", c957_also_found_switch_said)]:
        cell(name, fn)

    # C959 (Snazzy review S3): where the other mod's version of the base-game partner object uses a model of its own (the
    # names enrichment: the winning base is the other mod's record), the item does not say the other mod "only edits"
    # it: it says it replaces the model, and that the objects clash without it too.
    def c959_replaced_model_not_only_edits():
        X, H = f"000X59:{MOD_A}", f"000H59:{BASE}"
        r_ = row("OVERLAP", MOD_B, MOD_A, X, other=H)
        arms = [arm("baseline", [BASE, MOD_B, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_B, MOD_A]),
                arm("without-b", [BASE, MOD_A])]
        results = {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r_], (OVERLAP, "without-b"): [r_]}
        refs = {X: {"base": f"0B0X59:{MOD_A}"}, H: {"base": f"0B0H59:{MOD_B}"}}
        bases = {f"0B0X59:{MOD_A}": {"type": "Static", "editorId": "Shelf", "model": "shelf.nif"},
                 f"0B0H59:{MOD_B}": {"type": "Static", "editorId": "NewWall", "model": "newwall.nif"}}
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH], names={"refs": refs, "bases": bases})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        return "only edits" not in it and "model" in it and MOD_B in it, it[:400]

    for name, fn in [("third-review/C959-replaced-model-not-only-edits", c959_replaced_model_not_only_edits)]:
        cell(name, fn)

    # C970 (BPT re-review S1, found on regen-q4): the row's own winner value is a placeholder, so the record chain of the
    # setup with the patch decides that the two values name the same file but for letter case.
    def c970_case_only_from_chain():
        T1 = f"00C970:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, f"00C971:{BASE}", "Owner", "a", "b", "v")]
        lr_ = lost_row(PATCH, OTHER, T1, "HdLodDiffuseTexture", ["HdLodDiffuseTexture=data/textures/land/a.dds"], PH, PH)
        w_ch = [(T1, "Worldspace", [(BASE, {"HdLodDiffuseTexture": "x.dds"}),
                                    (PATCH, {"HdLodDiffuseTexture": "data/textures/land/a.dds"}),
                                    (OTHER, {"HdLodDiffuseTexture": "Data/Textures/Land/A.dds"})])]
        plan, md, _ = core_bundle(p_rows, list(p_rows) + [lr_], targets=(MOD_A, MOD_B), w_chains=w_ch)
        ln = next((x for x in md.splitlines() if OTHER in x and PATCH in x and "whose change is lost" in x), "")
        return "letter case" in ln and "new with the patch" not in ln, ln[:300]

    for name, fn in [("third-review/C970-case-only-from-chain", c970_case_only_from_chain)]:
        cell(name, fn)

    # C971 (Winterhold page limit, a generator rule for every report): the abridgement ladder's tail step moves the four
    # closing sections (what still needs checking, checks with no problem, reference information, evidence and limits)
    # into the evidence index, with a line in the report that says where they went; nothing is dropped. The ladder in
    # skill data has this step before any step that shows fewer rows or figures.
    def c971_tail_step_moves_closing_sections():
        plan, md0, html0 = record_validation_bundle()
        pr_ = R.load_priorities()
        ladder_ = pr_.get("abridgementLadder") or []
        tail_ = next((s_ for s_ in ladder_ if "tail" in str(s_.get("layout")).split()), None)
        if tail_ is None:
            return False, "no tail step in the ladder"
        out_ = Path(tempfile.mkdtemp(prefix="c971-"))
        v_ = R.effective_view(plan, tail_["rowsPerGroup"], tail_["figuresPerFinding"], pr_, tail_["layout"])
        got_ = R.render_all(plan, v_, out_, "")
        md_, idx_, html_, hidx_ = got_["report.md"], got_["evidence-index.md"], got_["report.html"], got_["evidence-index.html"]
        heads_ = ("What still needs checking", "Checks that found no problem", "Reference information collected",
                  "Evidence and limits")
        before_ = [i_ for i_, s_ in enumerate(ladder_) if s_ is tail_][0]
        ok = (all(f"## {h_}" not in md_ for h_ in heads_) and all(h_ in idx_ for h_ in heads_)
              and all(f"<h2>{h_}</h2>" not in html_ for h_ in heads_) and all(h_ in hidx_ for h_ in heads_)
              and "evidence-index.md#further-notes" in md_
              and all(s_["rowsPerGroup"] >= tail_["rowsPerGroup"] and s_["figuresPerFinding"] >= tail_["figuresPerFinding"]
                      for s_ in ladder_[:before_])
              and all(f"## {h_}" in md0 for h_ in heads_)
              # (found on regen-q5) the comparisons' summary lines move with them, said in the pointer and kept in the index
              and all(f"- {x_}" not in md_ and x_ in idx_ for x_ in (reader(plan).get("comparisonSummary") or []))
              and "other results" in md_)
        return ok, md_[-400:]

    for name, fn in [("third-review/C971-tail-step-moves-closing-sections", c971_tail_step_moves_closing_sections)]:
        cell(name, fn)

    # C972-C974 (Gate C review, finding 1): the paths C725, C810 and C822 guarded, reached by a master that is a mod, not
    # a base-game plugin, so the base-change rule (C937) does not set the rows aside first. Fix.esp changes the cell's
    # Location; OtherMod.esp (built on Skyrim.esm only) sets the same value as its own change.
    FIX_ = "Fix.esp"

    def mod_master_bundle(win_value, win_masters):
        L1 = f"00C972:{BASE}"
        arms = [arm("baseline", [BASE, FIX_, MID_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, FIX_, MID_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": "011111:Skyrim.esm"}), (FIX_, {"Location": "022222:Skyrim.esm"}),
                                (MID_, {"Location": "011111:Skyrim.esm"}), (OTHER, {"Location": "022222:Skyrim.esm"}),
                                (MOD_A, {"Location": win_value})])]
        p_rows = [lost_row(FIX_, MOD_A, L1, "Location", ["Location=022222:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=022222:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={FIX_: [BASE], MID_: [BASE], OTHER: [BASE, FIX_, MID_], MOD_A: win_masters,
                                           MOD_B: [BASE]})
        return plan, md, L1
    MID_ = "Mid.esp"

    # C972 (was C725's arm): ModA builds on Fix.esp with a value of its own, so Fix.esp's row is a value over its own
    # master (set aside); OtherMod's same value is its own change and stays counted, never folded into the set-aside row.
    def c972_status_before_fold_mod_master():
        plan, md, L1 = mod_master_bundle("033333:Skyrim.esm", [BASE, FIX_])
        got = {e["A"]: e["admission"] for e in rec_of(plan, L1)}
        return got.get(OTHER) == "admitted" and got.get(FIX_) not in (None, "admitted"), f"{got}"

    # C973 (was C810's arm): ModA puts back the value from before its master Fix.esp's change; OtherMod carries Fix.esp's
    # value as its own change (its nearest master Mid.esp restored the old one). One value is lost: said once, on the
    # undoes-its-master line; OtherMod's row is not counted again.
    def c973_carried_master_fix_said_once_mod_master():
        plan, md, L1 = mod_master_bundle("011111:Skyrim.esm", [BASE, FIX_])
        got = {e["A"]: e["admission"] for e in rec_of(plan, L1)}
        ok = (got.get(FIX_) == "undoesMaster" and got.get(OTHER) == "sameValue"
              and f"undoes its master {FIX_}'s fix" in md)
        return ok, f"{got}"

    # C974 (was C822's arm): the counted item says where OtherMod's lost value was first set (Fix.esp, a mod).
    def c974_carried_value_origin_mod_master():
        plan, md, L1 = mod_master_bundle("033333:Skyrim.esm", [BASE, FIX_])
        line = next((ln for ln in md.splitlines() if ln.startswith("- **") and f"{OTHER}'s value is lost" in ln), "")
        return f"was first set by {FIX_}" in line, line[-300:]

    for name, fn in [("third-review/C972-status-before-fold-mod-master", c972_status_before_fold_mod_master),
                     ("third-review/C973-carried-master-fix-said-once-mod-master",
                      c973_carried_master_fix_said_once_mod_master),
                     ("third-review/C974-carried-value-origin-mod-master", c974_carried_value_origin_mod_master)]:
        cell(name, fn)

    # ------------------------------------------------ third review, round 2 (C975-C986): the blind reviews of regen-final
    def plugin_file_of(name, masters, records, group="WRLD"):
        """A minimal plugin in its own folder: a TES4 header with its masters, then one top-level group (`group`) holding
        (type, formId, [(sig, data)], compressed) records."""
        def sub(sig, data):
            return sig.encode() + struct.pack("<H", len(data)) + data
        hdr = sub("HEDR", struct.pack("<fII", 1.7, len(records), 0x800)) + b"".join(
            sub("MAST", m.encode() + b"\x00") + sub("DATA", b"\x00" * 8) for m in masters)
        body = b""
        for typ, fid, subs, comp in records:
            data = b"".join(sub(s_, d_) for s_, d_ in subs)
            flags = 0
            if comp:
                data, flags = struct.pack("<I", len(data)) + zlib.compress(data), 0x00040000
            body += typ.encode() + struct.pack("<IIIIHH", len(data), flags, fid, 0, 44, 0) + data
        out = (b"TES4" + struct.pack("<IIIIHH", len(hdr), 0, 0, 0, 44, 0) + hdr
               + b"GRUP" + struct.pack("<I4sIIHH", 24 + len(body), group.encode(), 0, 0, 0, 0) + body)
        p_ = Path(tempfile.mkdtemp(prefix="r2-")) / name
        p_.write_bytes(out)
        return p_

    def nvnm(n_tris, links):
        """A navmesh's geometry: no vertices, n_tris empty triangles, then its edge links [(navmesh FormID, triangle)]."""
        return (struct.pack("<IIII", 12, 0xA5E9A03C, 0x3C, 0) + struct.pack("<I", 0) + struct.pack("<I", n_tris)
                + b"\x00" * 16 * n_tris + struct.pack("<I", len(links))
                + b"".join(struct.pack("<IIh", 0, m_, t_) for m_, t_ in links) + struct.pack("<I", 0))

    def with_files(arms_, files):
        """Point each named plugin of every setup at its file, with that file's sha256."""
        for a__ in arms_:
            for pl_ in a__["requested"]["plugins"]:
                if pl_["name"] in files:
                    pl_["path"] = str(files[pl_["name"]])
                    pl_["sha256"] = hashlib.sha256(Path(files[pl_["name"]]).read_bytes()).hexdigest()
        return arms_

    # C975 (round 2, B1 and R): the plugin reader's known answers. Navmesh edge links from a compressed and a plain NAVM
    # record, FormIDs through the plugin's master list (Skyrim.esm's 000D43 written 00000D43 by a plugin whose first
    # master it is, the plugin's own 000E99 written 01000E99); a cell's DATA flags by name; a record the plugin does not
    # hold is None.
    def c975_pluginread_known_answers():
        import pluginread
        f_ = plugin_file_of(OTHER, [BASE], [
            ("NAVM", 0x00000E41, [("NVNM", nvnm(3, [(0x00000D43, 501), (0x01000E99, 7)]))], True),
            ("NAVM", 0x00000E42, [("NVNM", nvnm(2, [(0x00000D43, 9)]))], False),
            ("CELL", 0x00000C11, [("DATA", struct.pack("<H", 0x0081))], False),
            ("CELL", 0x00000C12, [("DATA", struct.pack("<H", 0x0083))], True)])
        nv_ = pluginread.navmesh_links(f_, [f"000E41:{BASE}", f"000E42:{BASE}", f"000E43:{BASE}"])["records"]
        cf_ = pluginread.cell_flags(f_, [f"000C11:{BASE}", f"000C12:{BASE}"])["records"]
        # (sixth review) the reader also returns door triangles: none in these geometries
        ok = (nv_[f"000E41:{BASE}"] == {"triangles": 3, "links": [[0, f"000D43:{BASE}", 501], [0, f"000E99:{OTHER}", 7]],
                                         "doors": []}
              and nv_[f"000E42:{BASE}"] == {"triangles": 2, "links": [[0, f"000D43:{BASE}", 9]], "doors": []}
              and nv_[f"000E43:{BASE}"] is None
              and cf_[f"000C11:{BASE}"]["flags"] == ["IsInteriorCell", "ShowSky"]
              and cf_[f"000C12:{BASE}"]["flags"] == ["IsInteriorCell", "HasWater", "ShowSky"])
        return ok, f"{nv_}; {cf_}"

    # C976-C979 (round 2, B1: Sewers review Blocking 1, Clockwork review S1): a broken link is decided by the edge links
    # themselves, read from the plugin files the setup names. OtherMod.esp carries the base-game navmesh 000E41 (provider
    # chain Skyrim.esm -> OtherMod.esp); ModA, the evaluated mod, cut the target navmesh 000D43 to fewer triangles.
    N_LINK, N_TGT = f"000E41:{BASE}", f"000D43:{BASE}"

    def link_bundle(base_has, other_has=True, files=True, linker=OTHER):
        rows_ = []
        for t_ in (501, 502):
            r_ = row("EDGE-IDX", linker, MOD_A if linker == OTHER else OTHER, N_LINK, label="CityExterior04 (ext)",
                     other=N_TGT)
            r_["Summary"] = (f"navmesh {N_LINK} in CityExterior04 (ext) edge-links {N_TGT} triangle {t_} of 432   "
                             f"[record provided by: {BASE} -> {linker}]")
            rows_.append(r_)
        links_ = [(0x00000D43, 501), (0x00000D43, 502)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]
        if files:
            fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_ if base_has else []))],
                                                    True)]),
                   linker: plugin_file_of(linker, [BASE], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_ if other_has
                                                                                             else []))], False)])}
            with_files(arms, fs_)
        plan, md, _ = compile_and_render(arms, {(NAVOWN, "baseline"): rows_, (NAVOWN, "without-a"): []}, [MOD_A])
        return plan, md

    # C1093 (Sewers S2): the same base links, where the base game's own version of the target navmesh has the triangles
    # they point at (600 triangles) and the evaluated mod's has 432: the evaluated mod's cut breaks them, said so, never
    # "the base game's own".
    def c1093_base_link_broken_by_target_cut():
        rows_ = []
        for t_ in (501, 502):
            r_ = row("EDGE-IDX", OTHER, MOD_A, N_LINK, label="CityExterior04 (ext)", other=N_TGT)
            r_["Summary"] = (f"navmesh {N_LINK} in CityExterior04 (ext) edge-links {N_TGT} triangle {t_} of 432   "
                             f"[record provided by: {BASE} -> {OTHER}]")
            rows_.append(r_)
        links_ = [(0x00000D43, 501), (0x00000D43, 502)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_))], True),
                                               ("NAVM", 0x00000D43, [("NVNM", nvnm(600, []))], False)]),
               OTHER: plugin_file_of(OTHER, [BASE], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_))], False)])}
        with_files(arms, fs_)
        plan, md, _ = compile_and_render(arms, {(NAVOWN, "baseline"): rows_, (NAVOWN, "without-a"): []}, [MOD_A])
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        return ("2 broken navmesh links" in aside and f"{MOD_A} breaks" in aside
                and "base game's own" not in aside), aside[-400:]

    for name, fn in [("fifth-review/C1093-base-link-broken-by-target-cut", c1093_base_link_broken_by_target_cut)]:
        cell(name, fn)

    def c976_base_link_carried_set_aside():
        plan, md = link_bundle(base_has=True)
        adm = [e["admission"] for e in rec_of(plan, N_LINK)]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Navmesh")), "")
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        ok = (adm == ["baseLink", "baseLink"] and not it and "2 broken navmesh links" in aside
              and f"{OTHER} carries" in aside and "base game's own" in aside)
        return ok, f"{adm}; item {it[:120]!r}; aside {aside[-300:]!r}"

    def c977_carrier_added_links_named():
        plan, md = link_bundle(base_has=False)
        adm = [e["admission"] for e in rec_of(plan, N_LINK)]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Navmesh")), "")
        head_ = it.split(". ", 1)[0]
        ok = (adm == ["admitted", "admitted"] and f"{OTHER} added" in it and "not in the record data" not in it
              and "base game's navmesh records" not in head_ and OTHER in head_)
        return ok, f"{adm}; {it[:400]}"

    def c978_unread_links_said_not_read():
        plan, md = link_bundle(base_has=True, files=False)
        adm = [e["admission"] for e in rec_of(plan, N_LINK)]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Navmesh")), "")
        ok = (adm == ["admitted", "admitted"] and "not read for this report" in it and "not in the record data" not in it
              and f"the base game's navmesh records {OTHER} carries" in it.split(". ", 1)[0])
        return ok, f"{adm}; {it[:400]}"

    def c979_evaluated_linker_base_link_counted():
        plan, md = link_bundle(base_has=True, linker=MOD_A)
        adm = [e["admission"] for e in rec_of(plan, N_LINK)]
        return adm == ["admitted", "admitted"], f"{adm}"

    for name, fn in [("round-2/C975-pluginread-known-answers", c975_pluginread_known_answers),
                     ("round-2/C976-base-link-carried-set-aside", c976_base_link_carried_set_aside),
                     ("round-2/C977-carrier-added-links-named", c977_carrier_added_links_named),
                     ("round-2/C978-unread-links-said-not-read", c978_unread_links_said_not_read),
                     ("round-2/C979-evaluated-linker-base-link-counted", c979_evaluated_linker_base_link_counted)]:
        cell(name, fn)

    # C1052 (Clockwork S2): a navmesh the evaluated mod's version replaces with the base game's triangle count (read from
    # the base game's plugin file) is said so on the navmesh-replacement line, so a reader sees the city mod's navmesh
    # work undone rather than "cut down".
    def c1052_replacing_navmesh_base_count_said():
        import copy
        n1 = f"000E52:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  154 tris -> 104")]
        p_rows[0]["Necessary"][0]["PhysicalDetail"] = "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u"
        arms_ = with_files(copy.deepcopy(PATCH_ADDED_ARMS),
                           {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E52, [("NVNM", nvnm(104, []))], False)])})
        plan, md, html = compile_and_render(arms_, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH])
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        return ("the base game's triangle count" in top and n1 in top), top[-400:]

    # C1053 (Clockwork S4): a measured navmesh replacement with ground lost is said in the headline paragraph too (not
    # counted as a clash), with how many still lose it with the patch.
    def c1053_navmesh_loss_in_verdict():
        n1, n2 = f"000N53:{BASE}", f"000N54:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  689 tris -> 5")]
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n_, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u from the winner's"}
                 for n_ in (n1, n2)]
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        v = str((reader(plan).get("prediction") or {}).get("verdict") or "")
        return ("navmesh replaces" in v and "2 navmesh records" in v and "not counted" in v), v[-300:]

    for name, fn in [("fourth-review/C1053-navmesh-loss-in-verdict", c1053_navmesh_loss_in_verdict)]:
        cell(name, fn)

    # C1048 (Missives S3): a clash whose place is a worldspace's persistent cell (a bare form key) is located by the grid
    # cell of its object's position, read from the winning plugin's file (its recorded bytes); placements read known
    # answers (DATA: position and rotation).
    def c1048_persistent_place_located_by_grid():
        import copy
        import pluginread
        X, Y = f"000F48:{MOD_A}", f"000F49:{MOD_B}"
        f_ = plugin_file_of(MOD_A, [BASE], [("REFR", 0x01000F48, [("DATA", struct.pack("<6f", -31410.25, -88133.7, -3187.5,
                                                                                         0.0, 0.0, 6.18))], False),
                                            # (reviewer on ff85663c) another placed type in the same file
                                            ("ACHR", 0x01000F4A, [("DATA", struct.pack("<6f", 1.0, 2.0, 3.0, 0.0, 0.0, 0.0))], True)],
                            group="CELL")
        got_ = pluginread.placements(f_, [X])["records"][X]
        r_ = row("OVERLAP", MOD_B, MOD_A, X, other=Y, label="000D74:Skyrim.esm (ext)")
        arms_ = with_files([arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, MOD_A, MOD_B]), arm("without-b", [BASE, MOD_A])], {MOD_A: f_})
        plan, md, _ = compile_and_render(arms_, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r_],
                                                 (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        ok = (got_ and [round(x, 1) for x in got_["position"]] == [-31410.2, -88133.7, -3187.5]
              and "grid cell -8, -22" in it)
        return ok, f"{got_}; {it[-300:]}"

    # C1065 (reviewer on ff85663c): pluginread.placements' known answers, as C975 gives the other readers': a plain REFR
    # and a compressed ACHR (the reader indexes every placed type), FormIDs through the master list (the plugin's own
    # record written 01..., a master's 00...), and a record the plugin does not hold is None.
    def c1065_placements_known_answers():
        import pluginread
        f_ = plugin_file_of(MOD_A, [BASE], [
            ("REFR", 0x01000F65, [("NAME", struct.pack("<I", 0x00000101)), ("DATA", struct.pack("<6f", 10.5, -20.25, 30.0, 0.0, 0.0, 1.5))], False),
            ("ACHR", 0x00000F66, [("NAME", struct.pack("<I", 0x00000102)), ("DATA", struct.pack("<6f", -4096.0, 8192.5, -1.0, 0.0, 0.0, 3.0))], True)],
            group="CELL")
        got_ = pluginread.placements(f_, [f"000F65:{MOD_A}", f"000F66:{BASE}", f"000F67:{BASE}"])["records"]
        ok = (got_[f"000F65:{MOD_A}"] == {"position": [10.5, -20.25, 30.0], "rotation": [0.0, 0.0, 1.5]}
              and got_[f"000F66:{BASE}"] == {"position": [-4096.0, 8192.5, -1.0], "rotation": [0.0, 0.0, 3.0]}
              and got_[f"000F67:{BASE}"] is None)
        return ok, f"{got_}"

    for name, fn in [("fourth-review/C1065-placements-known-answers", c1065_placements_known_answers)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1048-persistent-place-located-by-grid", c1048_persistent_place_located_by_grid)]:
        cell(name, fn)

    # C1079 (Pilgrim S1, fifth review): an overlap pair whose need of the other mod was not measured, where that mod's
    # object is a base-game record it only moves (2 u, same turn: read from both plugin files), says how far it moves it.
    # (review5-sewers B1, review5-pilgrim B1, review5-missives S1) every partner the other mod carries is said with every
    # change it makes there (position, turn, base, scale, enable state), read from both plugin files: a 2 u nudge alone is
    # "only moves"; beside a partner moved 300 u and turned, both are named with their distances, no "only"; a partner
    # whose base alone the other mod changes is said as given another base, never as moved 0.0 u.
    def nudge_bundle(partners):
        """partners: [(form id low bits, base (x, y, z, rz, NAME id), other mod's (x, y, z, rz, NAME id))]"""
        X = f"000F79:{MOD_A}"
        rows_, refs_ = [], {X: {"base": f"0B0A79:{BASE}", "carriedBy": [MOD_A]}}
        recs_b, recs_o = [], []
        for low_, pb_, po_ in partners:
            Y = f"{low_:06X}:{BASE}"
            for recs_, v_ in ((recs_b, pb_), (recs_o, po_)):
                recs_.append(("REFR", low_, [("NAME", struct.pack("<I", v_[4])),
                                              ("DATA", struct.pack("<6f", v_[0], v_[1], v_[2], 0.0, 0.0, v_[3]))], False))
            rows_.append(row("OVERLAP", OTHER, MOD_A, X, other=Y))
            refs_[Y] = {"base": f"0B0A79:{BASE}", "carriedBy": [BASE, OTHER]}
        fb_ = plugin_file_of(BASE, [], recs_b, group="CELL")
        fo_ = plugin_file_of(OTHER, [BASE], recs_o, group="CELL")
        arms_ = with_files([arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])], {BASE: fb_, OTHER: fo_})
        names_ = {"refs": refs_, "bases": {f"0B0A79:{BASE}": {"type": "Static", "editorId": "Rock01", "model": "rocks\\rock01.nif"}}}
        plan, md, _ = compile_and_render(arms_, {(OVERLAP, "baseline"): list(rows_), (OVERLAP, "without-patch"): rows_}, [MOD_A],
                                         patches=[PATCH], names=names_)
        return md, next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")

    def c1079_other_mod_nudge_said():
        near_ = (0x00CA79, (100.0, 100.0, 0.0, 1.5, 0x0B0A79), (102.0, 100.0, 0.0, 1.5, 0x0B0A79))
        far_ = (0x00CB79, (500.0, 100.0, 0.0, 1.5, 0x0B0A79), (800.0, 100.0, 0.0, 2.0, 0x0B0A79))
        rebase_ = (0x00CC79, (900.0, 100.0, 0.0, 1.5, 0x0B0A79), (900.0, 100.0, 0.0, 1.5, 0x01000B7A))
        md1, it1 = nudge_bundle([near_])
        md2, it2 = nudge_bundle([near_, far_])
        md3, it3 = nudge_bundle([rebase_])
        one_ = re.search(re.escape(OTHER) + r" only moves [^:]*?by 2.0 u", it1) is not None and "was not measured" in md1
        two_ = (" only " not in it2.split("For example")[0] and "00CA79:Skyrim.esm" in it2 and "00CB79:Skyrim.esm" in it2
                and re.search(r"00CB79:Skyrim\.esm[^;.]*300\.0 u[^;.]*turn", it2) is not None and "2.0 u" in it2)
        three_ = ("00CC79:Skyrim.esm" in it3 and "another base" in it3 and "0.0 u" not in it3 and " only " not in it3)
        return one_ and two_ and three_, f"{one_} {two_} {three_} || {it2[:700]} || {it3[:700]}"

    for name, fn in [("fifth-review/C1079-other-mod-nudge-said", c1079_other_mod_nudge_said)]:
        cell(name, fn)

    # C1080 (Pilgrim S2): one actor pair split in two quests is said in one unit, naming both quests, in the detail line
    # and in the by-check line of new clashes; never one clash per actor.
    def c1080_alias_split_one_unit():
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A]), arm("without-a", [BASE, OTHER, O2])]
        A1, A2, Q1, Q2 = f"000A80:{BASE}", f"000B80:{BASE}", f"000Q80:{BASE}", f"000R80:{BASE}"
        rows_ = []
        for q_, qn_ in ((Q1, "Q one"), (Q2, "Q two")):
            for x_ in (A1, A2):
                r_ = row("SPLIT", OTHER, PATCH, x_, other=q_, label="Temple (int)")
                r_["Summary"] = f"quest '{qn_}' aliases 'Xa' and 'Ya' were 137u apart in Temple (int), now 1,800u apart"
                rows_.append(r_)
        names_ = {"refs": {A1: {"base": f"0B0A80:{BASE}"}, A2: {"base": f"0B0B80:{BASE}"}},
                  "bases": {f"0B0A80:{BASE}": {"type": "Npc", "editorId": "Xa", "model": "x.nif"},
                            f"0B0B80:{BASE}": {"type": "Npc", "editorId": "Ya", "model": "y.nif"}}}
        plan, md, _ = compile_and_render(arms, {(ALIAS, "baseline"): rows_, (ALIAS, "without-patch"): [],
                                                (ALIAS, "without-a"): []}, [MOD_A], patches=[PATCH], names=names_)
        det = next((ln for ln in md.splitlines() if "quest scene" in ln and "Measured load order" in ln), "")
        nb = next((ln for ln in md.splitlines() if ln.startswith("- New clashes with the patch, by check")), "")
        return ("1 actor pair split in 2 quest scenes ('Q one', 'Q two')" in det and "1 Xa; 1 Ya" not in nb
                and "1 actor pair" in nb), f"{det[-300:]} || {nb[-200:]}"

    for name, fn in [("fifth-review/C1080-alias-split-one-unit", c1080_alias_split_one_unit)]:
        cell(name, fn)

    # C1081 (Pilgrim S3): the load-order line says what a field means by the record type of its own rows: Flags lost on 2
    # cells and 1 worldspace is said as the cell's switches (ShowSky first), not the worldspace's grass switch.
    def c1081_flags_meaning_by_row_type():
        p_rec = [lost_row(MOD_A, OTHER, f"000C81:{BASE}", "Lighting", "a", "o", "v")]
        arms = [arm("baseline", [BASE, MOD_A, OTHER], kind="baseline")]
        o_rows = [dict(lost_row(OTHER, MOD_A, f"000W81:{BASE}", "Flags", "o", "a", "v"), _check=CELLATTR, _type="Worldspace")]
        o_rows += [dict(lost_row(OTHER, MOD_A, f"000C8{i_}:{BASE}", "Flags", "o", "a", "v"), _check=CELLATTR, _type="Cell")
                   for i_ in (2, 3)]
        setup = {"armId": "other-first", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": plugins(BASE, OTHER, MOD_A), "rows": o_rows}
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): []}, [MOD_A], count_only={(CELLATTR, "baseline"): 1},
                                         enrichment={(CELLATTR, "baseline"): p_rec}, order_setups=[setup])
        ln = " ".join(x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or [])
        return ("ShowSky" in ln and "NoGrass" not in ln), ln[-400:]

    for name, fn in [("fifth-review/C1081-flags-meaning-by-row-type", c1081_flags_meaning_by_row_type)]:
        cell(name, fn)

    # C1082 (Pilgrim S4, BPT S1): where effect-model pairs are counted, the unconditional in-game line speaks only of the
    # other pairs (an item of effect pairs only has none), and the still-there part says how many left pairs have one.
    def c1082_effect_pairs_in_game_and_still_there():
        X, Y, Z = f"000X82:{MOD_A}", f"000Y82:{MOD_B}", f"000Z82:{MOD_B}"
        refs = {X: {"base": f"0B0X82:{MOD_A}"}, Y: {"base": f"0B0Y82:{MOD_B}"}, Z: {"base": f"0B0Z82:{MOD_B}"}}
        bases = {f"0B0X82:{MOD_A}": {"type": "Static", "editorId": "Rock", "model": "Landscape\\Rock01.nif"},
                 f"0B0Y82:{MOD_B}": {"type": "MoveableStatic", "editorId": "FXMist", "model": "Effects\\Ambient\\FXMist01.nif"},
                 f"0B0Z82:{MOD_B}": {"type": "Static", "editorId": "Wall", "model": "Architecture\\Wall01.nif"}}
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]

        def run(others):
            rows_ = [row("OVERLAP", MOD_B, MOD_A, X, other=o_) for o_ in others]
            plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): list(rows_), (OVERLAP, "without-patch"): rows_,
                                                    (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH],
                                             names={"refs": refs, "bases": bases})
            it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
            st = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
            return it, st
        it1, st1 = run([Y])
        it2, st2 = run([Y, Z])
        return ("drawn through each other" not in it1 and "for the other 1 overlap pair" in it2 and "drawn through each other" in it2
                and "1 with a visual-effect model" in st2), f"{it1[-200:]} || {it2[-250:]} || {st2[-200:]}"

    for name, fn in [("fifth-review/C1082-effect-pairs-in-game-and-still-there", c1082_effect_pairs_in_game_and_still_there)]:
        cell(name, fn)

    # C1083 (BPT S3): a height difference between the two values is said with how far apart they are across the floor
    # where that is at least an object's spot (here 1,000 u), never as a height alone.
    def c1083_height_diff_with_across():
        rows_ = []
        for i_ in (1, 2):
            D_ = f"00CA8{i_}:{BASE}"
            rows_.append(lost_row(MOD_A, MOD_B, D_, "Placement", f"Position=100,{i_}00,0; Rotation=0,0,0",
                                  f"Position=1100,{i_}00,288; Rotation=0,0,0", f"Position=100,{i_}00,288; Rotation=0,0,0",
                                  before=BASE))
        plan, md, _ = core_bundle(rows_, list(rows_), targets=(MOD_A, MOD_B), masters={**MASTERS_, PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("288 u higher" in it and "1,000 u away across the floor" in it), it[-400:]

    for name, fn in [("fifth-review/C1083-height-diff-with-across", c1083_height_diff_with_across)]:
        cell(name, fn)

    # C1084 (BPT S5): the new problems a patch brings, by check, carry that check's in-game meaning as a candidate for a
    # visual check (no role read from a model's name), so a modder is not left to guess what a new problem does.
    def c1084_new_by_check_in_game():
        X1 = f"000X84:{MOD_A}"
        new = [row("CONTACT", MOD_B, MOD_A, X1, other=f"000H84:{MOD_B}")]
        new[0]["Summary"] += "; nearest floor or wall 4.9u"
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                     created=1, newDefect=1, attributed=0, unmeasured=0)}}
        log = f"NEW     CONTACT    {MOD_B}  x  {MOD_A}   +1 record(s): created 1\n"
        refs = {X1: {"base": f"0B0P84:{MOD_A}"}, f"000H84:{MOD_B}": {"base": f"0B0W84:{MOD_B}"}}
        bases = {f"0B0P84:{MOD_A}": {"type": "Static", "editorId": "ShadowPlane", "model": "plane.nif"},
                 f"0B0W84:{MOD_B}": {"type": "Static", "editorId": "Wall", "model": "wall.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): new},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            names={"refs": refs, "bases": bases}, diff_logs={(CONTACT, "with-patch"): log})
        nb = next((ln for ln in md.splitlines() if ln.startswith("- New clashes with the patch, by check")), "")
        return ("in game" in nb.lower() and "candidate for a visual check" in nb and "shadow" not in nb.lower()
                .replace("shadowplane", "")), nb[-300:]

    for name, fn in [("fifth-review/C1084-new-by-check-in-game", c1084_new_by_check_in_game)]:
        cell(name, fn)

    # C1085 (BPT S6): an object no longer listed with the patch whose own predicate there still Holds left the class with
    # its problem (the probe's "reclassified"); it is no repair. Three gone: G (body, Gone), L1 (no model, Gone), L2 (no
    # model, Holds): repaired 1 with a body plus 1 with no model, and the changed owner said once.
    def c1085_reclassified_not_repaired():
        G0, L1, L2, K = f"000G85:{MOD_A}", f"000L85:{MOD_A}", f"000M85:{MOD_A}", f"000K85:{MOD_A}"
        base = [row("CONTACT", MOD_B, MOD_A, x_, other=f"000H{i}5:{MOD_B}") for i, x_ in enumerate((G0, L1, L2, K))]
        withp = [base[3]]
        preds = [{"Kind": "CONTACT", "A": MOD_B, "B": MOD_A, "FormKey": x_, "Other": f"000H{i}5:{MOD_B}", "Physical": p_}
                 for i, (x_, p_) in enumerate(((G0, "Gone"), (L1, "Gone"), (L2, "Holds"), (K, "Holds")))]
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=2, reclassified=1,
                                                     created=0, newDefect=0, attributed=0, unmeasured=0)}}
        log = f"SHRANK  CONTACT    {MOD_B}  x  {MOD_A}   -3 record(s): repaired 2, reclassified 1\n"
        refs = {G0: {"base": f"0B0S85:{MOD_A}"}, K: {"base": f"0B0S85:{MOD_A}"}, L1: {"base": f"0B0L85:{MOD_A}"},
                L2: {"base": f"0B0L85:{MOD_A}"}, **{f"000H{i}5:{MOD_B}": {"base": f"0B0W85:{MOD_B}"} for i in range(4)}}
        bases = {f"0B0S85:{MOD_A}": {"type": "Static", "model": "lantern.nif"}, f"0B0L85:{MOD_A}": {"type": "Light", "model": ""},
                 f"0B0W85:{MOD_B}": {"type": "Static", "model": "table.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): withp},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order, predicates={(CONTACT, "with-patch"): preds},
                                            names={"refs": refs, "bases": bases}, diff_logs={(CONTACT, "with-patch"): log})
        pl_ = " ".join(str(f.get("readerPlain") or "") for f in plan.get("findings") or [] if "repaired" in str(f.get("readerPlain") or ""))
        return ("repaired 1 existing problem (not counted: 1 more whose object's record has no model" in pl_), (pl_[-300:] or md[-1500:])

    for name, fn in [("fifth-review/C1085-reclassified-not-repaired", c1085_reclassified_not_repaired)]:
        cell(name, fn)

    # C1086 (Winterhold S1): with two evaluated mods, a detail line whose rows the top counts only in part says the rows
    # already there with one mod alone apart from those counted at the top, never "N counted at the top" for all.
    def c1086_detail_split_counts_alone_apart():
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B], kind="baseline"), arm("without-b", [BASE, OTHER, MOD_A]),
                arm("without-a", [BASE, OTHER, MOD_B])]
        rows_ = [row("OVERLAP", MOD_B, MOD_A, f"000X8{i}:{MOD_A}", other=f"000Y8{i}:{MOD_B}") for i in range(3)]
        # one pair against a base-game object ModB also edits: already there with ModA alone
        rows_.append(row("OVERLAP", MOD_B, MOD_A, f"000Z86:{MOD_A}", other=f"000W86:{BASE}"))
        al_ = [row("OVERLAP", BASE, MOD_A, f"000Z86:{MOD_A}", other=f"000W86:{BASE}")]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): rows_, (OVERLAP, "without-b"): al_,
                                                (OVERLAP, "without-a"): []}, [MOD_A, MOD_B])
        line = next((ln for ln in md.split("## What the checks found", 1)[-1].splitlines()
                     if "Measured load order" in ln and "4 overlap pairs" in ln), "")
        return ("(3 at the top; 1 with one mod alone)" in line), line[:300]

    for name, fn in [("fifth-review/C1086-detail-split-counts-alone-apart", c1086_detail_split_counts_alone_apart)]:
        cell(name, fn)

    # C1087 (Winterhold S3): a quest-or-switch row of a fixture left in empty space whose nearest floor or wall is 0 u is
    # set aside as cannot tell, by the same rule as the fixture check's rows.
    def c1087_state_fixture_zero_gap_cannot_tell():
        arms = [arm("baseline", [BASE, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, MOD_B]), arm("without-b", [BASE, THIRD, MOD_A]),
                arm("without-a", [BASE, THIRD, MOD_B])]
        r2 = row("STATE-EMPTY@000S87:ThirdMod.esp@Triggered", THIRD, MOD_B, f"000Z87:{THIRD}", label="Hall (int)")
        r2["Summary"] = (f"LeftInEmptySpace: Furniture Cot (fixed) in Hall (int); {MOD_B} removed or moved 000H87:Skyrim.esm; "
                         f"its assembly of 1 touches no floor or wall now (nearest floor or wall 0u) @ 000S87:ThirdMod.esp")
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): [r2], (STATE, "without-b"): [],
                   (STATE, "without-a"): [r2]}
        plan, md, html = compile_and_render(arms, results, [MOD_A, MOD_B], patches=[PATCH])
        adm = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []
               if str(e.get("formKey")).startswith("000Z87")]
        return adm == ["anchorUnclear"], f"{adm}"

    for name, fn in [("fifth-review/C1087-state-fixture-zero-gap-cannot-tell", c1087_state_fixture_zero_gap_cannot_tell)]:
        cell(name, fn)

    # C1088 (Winterhold R2-3): the sentence that a winner's value of a field is the base game's names its own example (a
    # record of that field), so the item's examples (objects the winner moved) do not read as examples of it.
    def c1088_winner_base_sentence_names_its_example():
        R1, R2 = f"00CA88:{BASE}", f"00CB88:{BASE}"
        a1, b1 = offs(0), offs(0)
        a1["Placement.Position"], b1["Placement.Position"] = "300,100,0", "700,100,0"
        a2 = offs(0, "0", "000014:Skyrim.esm")
        b2 = offs(0)
        rows_ = [lost_row(MOD_A, MOD_B, R1, "Placement", off_txt(a1), off_txt(b1), off_txt(offs(0)), before=BASE),
                 lost_row(MOD_A, MOD_B, R2, "EnableParent", ["EnableParent.Reference=000014:Skyrim.esm",
                                                             "EnableParent.Flags=SetEnableStateToOppositeOfParent"],
                          PH, PH, before=BASE)]
        ch_ = [(R1, "PlacedObject", [(BASE, offs(0)), (MOD_A, a1), (MOD_B, b1)]),
               (R2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a2), (MOD_B, b2)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        s_ = next((x for x in re.split(r"(?<=[.)])\s+", it) if "is the base game's" in x), "")
        return ("00CB88" in s_ and "00CA88" not in s_), f"{s_} || {it[-400:]}"

    for name, fn in [("fifth-review/C1088-winner-base-sentence-names-its-example", c1088_winner_base_sentence_names_its_example)]:
        cell(name, fn)


    # C1090 (Snazzy earlier R2, pelts): a row set aside as cannot tell (fixture at 0 u) that the setup with the patch no
    # longer lists is "no longer found", never "resolved by the patch".
    def c1090_cannot_tell_not_resolved():
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F9{i}:{MOD_A}", other=f"000H9{i}:{MOD_B}") for i in (1, 2)]
        fx[0]["Summary"] = "LeftInEmptySpace: Static Pelt; its assembly of 1 touches no floor or wall now (nearest floor or wall 0u)"
        fx[1]["Summary"] = "LeftInEmptySpace: Static Rack; its assembly of 1 touches no floor or wall now (nearest floor or wall 27u)"
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        plan, md, _ = compile_and_render(arms, {(FIXTURE, "baseline"): [fx[1]], (FIXTURE, "without-patch"): fx,
                                                (FIXTURE, "without-b"): []}, [MOD_A], patches=[PATCH])
        sents = (reader(plan).get("validation") or {}).get("sentences") or []
        res_ = [x for x in sents if x.startswith("Resolved by") and "not predicted" in x]
        gone_ = [x for x in sents if "no longer found" in x.lower()]
        return (not any("0u" in x or "floor or wall" in x for x in res_) and any("1 " in x for x in gone_)), f"{res_} || {gone_}"

    for name, fn in [("fifth-review/C1090-cannot-tell-not-resolved", c1090_cannot_tell_not_resolved)]:
        cell(name, fn)

    # C1089 (Winterhold page limit, fifth review): the abridgement ladder is cumulative: every step after the one that moves
    # the closing sections and the location load order keeps those moves while it shows fewer rows or figures, so a
    # later step is never longer than the step before it for having put the sections back.
    def c1089_ladder_steps_cumulative():
        ladder_ = R.load_priorities().get("abridgementLadder") or []
        k_ = next((i_ for i_, s_ in enumerate(ladder_) if {"tail", "order"} <= set(str(s_.get("layout")).split())), None)
        if k_ is None:
            return False, "no tail-order step"
        bad_ = [s_ for s_ in ladder_[k_ + 1:] if not {"tail", "order"} <= set(str(s_.get("layout")).split())]
        # the step after it moves the set-aside line (what the counts leave out, and why) into the evidence index's
        # further notes, with a pointer in the report; the steps after keep that move too
        a_ = next((i_ for i_, s_ in enumerate(ladder_) if "aside" in str(s_.get("layout")).split()), None)
        if a_ is None or a_ != k_ + 1 or any("aside" not in str(s_.get("layout")).split() for s_ in ladder_[a_:]):
            return False, f"no cumulative aside step right after the tail-order step: {ladder_[k_:]}"
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F8{i}:{MOD_A}", other=f"000H8{i}:{MOD_B}") for i in (1, 2)]
        fx[0]["Summary"] = "LeftInEmptySpace: Static Pelt; its assembly of 1 touches no floor or wall now (nearest floor or wall 0u)"
        fx[1]["Summary"] = "LeftInEmptySpace: Static Rack; its assembly of 1 touches no floor or wall now (nearest floor or wall 27u)"
        results = {(FIXTURE, a): [] for a in ("dependency-only", "plus-a", "plus-b")}
        results[(FIXTURE, "baseline")] = fx
        plan, md0, html0 = compile_and_render(four_arms(), results, [MOD_A, MOD_B])
        line_ = str(((reader(plan).get("coverage") or {}).get("setAsideLine")) or "")
        st_ = ladder_[a_]
        out_ = Path(tempfile.mkdtemp(prefix="c1089-"))
        got_ = R.render_all(plan, R.effective_view(plan, st_["rowsPerGroup"], st_["figuresPerFinding"], R.load_priorities(),
                                                   st_["layout"]), out_, "")
        ok = (not bad_ and bool(line_) and line_ in md0 and line_ not in got_["report.md"]
              and line_ in got_["evidence-index.md"] and "evidence-index.md#further-notes" in got_["report.md"])
        return ok, f"later steps without the moves: {bad_}; line {line_[:80]!r}"

    for name, fn in [("fifth-review/C1089-ladder-steps-cumulative", c1089_ladder_steps_cumulative)]:
        cell(name, fn)

    # C1091 (Snazzy "173 / 53"): the undone line names its units: record fields, and how many objects the ones that switch
    # an object back on are on (two fields of one object switched back on: 2 fields on 1 object).
    def c1091_undone_line_units():
        p_rows = [lost_row(MOD_A, MOD_B, f"000791:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_A, PATCH, f"000792:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0"),
                  lost_row(MOD_A, PATCH, f"000792:{BASE}", "EnableParent",
                           ["EnableParent.Reference=000014:Skyrim.esm", "EnableParent.Flags=SetEnableStateToOppositeOfParent"],
                           "none", "none")]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], enrichment=enr,
                                            count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 2})
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        return ("2 record fields the mods changed" in line and "2 of the 2, on 1 object, switch" in line), line[:300]   # round 2 item 5

    for name, fn in [("fifth-review/C1091-undone-line-units", c1091_undone_line_units)]:
        cell(name, fn)

    # C1092 (Sewers S1): a further evaluation supplied with the report whose setup loads another version of the evaluated
    # plugin (same name, other bytes) says, for a counted overlap item, how many of its pairs that version's setup finds:
    # here none of the 2 against OtherMod's tower.
    def c1092_also_variant_finds_pairs():
        X1, X2, T_ = f"000X92:{MOD_A}", f"000X93:{MOD_A}", f"000T92:{OTHER}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, x_, other=T_) for x_ in (X1, X2)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-other", [BASE, MOD_A])]
        v_arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline", shas={MOD_A: "b" * 64})]
        v_res = {(OVERLAP, "baseline"): [row("OVERLAP", OTHER, MOD_A, f"000X99:{MOD_A}", other=f"000T99:{OTHER}")]}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): list(rows_), (OVERLAP, "without-patch"): rows_,
                                                (OVERLAP, "without-other"): []}, [MOD_A], patches=[PATCH],
                                         also=[(v_arms, v_res, [MOD_A], {})])
        also_ = " ".join((reader(plan).get("prediction") or {}).get("alsoEvaluations") or [])
        return (re.search(r"another version of " + re.escape(MOD_A), also_) is not None
                and re.search(r"finds (?:[^.]*?; )?0 of the 2 overlap pairs between " + re.escape(OTHER) + " and " + re.escape(MOD_A), also_)
                is not None), also_[-400:]

    for name, fn in [("fifth-review/C1092-also-variant-finds-pairs", c1092_also_variant_finds_pairs)]:
        cell(name, fn)

    # C1094 (Sewers S3): an overlap pair the probe reports as touching is said as surfaces in contact, counted apart in
    # the item, and the "drawn through each other" line speaks only of the crossing pairs.
    def c1094_touching_pairs_said_apart():
        X, Y, Z = f"000X94:{MOD_A}", f"000Y94:{MOD_B}", f"000Z94:{MOD_B}"
        r1 = row("OVERLAP", MOD_B, MOD_A, X, other=Y)
        r1["Summary"] = f"Static from {MOD_A} / Static from {MOD_B}: touching, containment unassessed, 992 triangle pairs"
        r2 = row("OVERLAP", MOD_B, MOD_A, X, other=Z)
        r2["Summary"] = f"Static from {MOD_A} / Static from {MOD_B}: 12 triangle pairs"
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r1, r2],
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        return ("1 of these pairs only touch" in it and "for the other 1 overlap pair" in it), it[-400:]

    for name, fn in [("fifth-review/C1094-touching-pairs-said-apart", c1094_touching_pairs_said_apart)]:
        cell(name, fn)

    # C1095 (Clockwork S1): the findings list of the setup with the patch, which does not list a pair still crossing where
    # a patch places one of its objects (found by the patch comparison's re-measure), says so beside its own results.
    def c1095_findings_list_names_patch_placed():
        X = f"000X95:{MOD_A}"
        ys = [f"000Y9{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        refs = {ys[1]: {"carriedBy": [MOD_B, PATCH]}, ys[0]: {"carriedBy": [MOD_B]}, X: {"carriedBy": [MOD_A]}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): [
            row("OVERLAP", MOD_B, MOD_A, X, other=ys[0])]}, [MOD_A, MOD_B], patches=[PATCH],
            predicates={(OVERLAP, "with-patch"): preds}, names={"refs": refs})
        found = md.split("## What the checks found", 1)[-1].split("\n## ", 1)[0]
        lines_ = [ln for ln in found.splitlines() if ln.startswith("- **") and "overlap pair" in ln]
        return any("where a patch places one of the objects" in ln and "1 overlap pair" in ln for ln in lines_), f"{lines_}"

    for name, fn in [("fifth-review/C1095-findings-list-names-patch-placed", c1095_findings_list_names_patch_placed)]:
        cell(name, fn)

    # C1096 (Missives S1): every record import whose own tool note says it narrowed a record type's fields is said, in
    # either of the note's wordings ("their" / "its compared fields", or "narrowed without"), not only the cell import's.
    def c1096_every_import_narrowing_said():
        pr_ = priorities()
        rows_ = [{"checkId": CELLATTR, "tool": {"name": "housecarl_records tree over the 21 CELL records narrowed to their "
                                                          "compared fields without MaxHeightData (N11), composed by a script"}},
                 {"checkId": RECORDS, "tool": {"name": "ordered housecarl_records calls (tree form over all 2,110 FormKeys; "
                                                         "CELL re-read narrowed to its compared fields without MaxHeightData "
                                                         "(c03; N11), WRLD narrowed without OffsetData), composed by a script"}}]
        facts = R.import_narrowed_facts(rows_, pr_, lambda c_: c_)
        joined = " | ".join(facts)
        return (any(f_.startswith(RECORDS) and "MaxHeightData" in f_ for f_ in facts)
                and any(f_.startswith(RECORDS) and "OffsetData" in f_ for f_ in facts)
                and any(f_.startswith(CELLATTR) and "MaxHeightData" in f_ for f_ in facts)), joined[:500]

    for name, fn in [("fifth-review/C1096-every-import-narrowing-said", c1096_every_import_narrowing_said)]:
        cell(name, fn)

    # C1097 (Missives S2): a winner's own change to a cell's land flags says what hidden quarters do in game: the land of
    # a hidden quarter is not drawn (skill data readBitsWords), beside the with-patch status.
    def c1097_land_flags_meaning_said():
        C1 = f"00CA97:{BASE}"
        r_ = lost_row(OTHER, MOD_A, C1, "Grid", ["Grid.Flags=-412494080"], PH, ["Grid.Flags=-499936256"], label="TestCellLand")
        r_["Summary"] = f"Cell TestCellLand: Grid - {OTHER}'s value lost to {MOD_A}'s"
        ch_ = [(BASE, {"Grid.Flags": "5504256"}), (OTHER, {"Grid.Flags": "-412494080"}), (MOD_A, {"Grid.Flags": "-1223611121"})]
        tr_ = tree_transcript([(C1, "Cell", ch_)])
        plan, md, _ = core_bundle([r_], [r_], check=CELLATTR, masters=MASTERS_, p_tr=tr_, w_chains=[(C1, "Cell", ch_)])
        line = next((ln for ln in md.splitlines() if "land flags" in ln and MOD_A in ln), "")
        return ("not drawn" in line and f"with {PATCH}, still so in 1 of the 1" in line), line[-300:]

    for name, fn in [("fifth-review/C1097-land-flags-meaning-said", c1097_land_flags_meaning_said)]:
        cell(name, fn)

    # C1098 (Missives line 107): the location list's coverage note names a plugin the evidence names outside the list as
    # named by the evidence index, never as "the findings above" (here OtherMod.esp is in a set-aside row only).
    def c1098_coverage_note_says_evidence_index():
        arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER])]
        arms[0]["locationContributors"] = {"complete": True, "locations": [{"name": "Loc (int)", "plugins": [
            {"name": BASE}, {"name": MOD_A}]}]}
        rows_ = [row("OVERLAP", OTHER, MOD_A, f"000X98:{MOD_A}", other=f"000Y98:{OTHER}")]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): rows_, (OVERLAP, "without-a"): rows_}, [MOD_A])
        ln = next((x for x in md.splitlines() if "also name" in x), "")
        return ("The findings above also name" not in md and f"The evidence index also names {OTHER}" in ln), ln[:300]

    for name, fn in [("fifth-review/C1098-coverage-note-says-evidence-index", c1098_coverage_note_says_evidence_index)]:
        cell(name, fn)

    # C1086b (blind round 1, Winterhold S1; the C1068-C1098 range is used up): a navmesh whose losing version is a
    # single-triangle stub (how a mod removes a navmesh; the base game's has 600) loses no ground of the losing mod's, read
    # from the plugin files: the loss line counts only the other navmesh and says the stub apart.
    def c1086b_stub_navmesh_loses_no_ground():
        n1, n2 = f"000E86:{BASE}", f"000E87:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  1 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  500 tris -> 345")]
        for r_ in p_rows:
            r_["Necessary"][0]["Detail"] = "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u from the winner's"
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n_, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u from the winner's"}
                 for n_ in (n1, n2)]
        arms_ = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E86, [("NVNM", nvnm(600, []))], False),
                                               ("NAVM", 0x00000E87, [("NVNM", nvnm(600, []))], False)]),
               MOD_A: plugin_file_of(MOD_A, [BASE], [("NAVM", 0x00000E86, [("NVNM", nvnm(1, []))], False),
                                                     ("NAVM", 0x00000E87, [("NVNM", nvnm(500, []))], False)])}
        with_files(arms_, fs_)
        plan, md, html = compile_and_render(arms_, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        return ("in 1 of them ground ModA.esp's navmesh covered" in top and "single triangle" in top
                and "000E86" in top), top[-500:]

    for name, fn in [("fifth-review/C1086b-stub-navmesh-loses-no-ground", c1086b_stub_navmesh_loses_no_ground)]:
        cell(name, fn)

    # C1091b (blind round 1, Snazzy S1): where the patch resolves a clash by switching an object off, the with-patch line
    # says how many, neutrally: a record field the patch sets to its own value that switches off an object both mods have
    # on, and an overlap pair resolved where the patch's version of one object is off at load.
    def c1091b_patch_resolves_by_switching_off():
        D = f"00CA91:{BASE}"
        a_, b_ = offs(0), offs(0)
        a_["Placement.Position"], b_["Placement.Position"] = "300,100,0", "500,100,0"
        pt_ = offs(-30000, "InitiallyDisabled")
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        w_rows = [lost_row(MOD_A, PATCH, D, "Placement", off_txt(a_), off_txt(pt_), off_txt(offs(0)), before=BASE),
                  lost_row(MOD_B, PATCH, D, "Placement", off_txt(b_), off_txt(pt_), off_txt(offs(0)), before=BASE)]
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)])]
        w_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_), (PATCH, pt_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or [] if x.startswith("Record fields")), "")
        own_ok = re.search(r"the patch's own value 1 \(1 where the patch switches off an object both mods have on\)", kl) is not None
        # an overlap pair: ModA's object X against ModB's Y, gone with the patch, whose version of Y is off at load
        X, Y = f"000X91:{MOD_A}", f"000Y91:{MOD_B}"
        rows_ = [row("OVERLAP", MOD_B, MOD_A, X, other=Y)]
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        tr = {(RECORDS, "baseline"): records_transcript([(Y, "PlacedObject", [(MOD_B, offs(0)), (PATCH, pt_)])]),
              (RECORDS, "without-patch"): records_transcript([(Y, "PlacedObject", [(MOD_B, offs(0))])])}
        # the record import also holds a row of its own, as every real import does
        q_ = lost_row(MOD_A, MOD_B, f"00CB91:{BASE}", "Owner", "a", "b", "v")
        enr = {(RECORDS, "baseline"): [q_], (RECORDS, "without-patch"): [q_]}
        plan2, md2, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): rows_,
                                                  (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH],
                                           enrichment=enr, transcripts=tr, count_only={k_: 1 for k_ in enr})
        kl2 = next((x for x in (reader(plan2).get("prediction") or {}).get("withPatchLines") or [] if x.startswith("Objects")), "")
        ov_ok = re.search(r"resolved 1 \(1 by the patch switching an object off\)", kl2) is not None
        return own_ok and ov_ok, f"{own_ok} {ov_ok} || {kl[:300]} || {kl2[:300]}"

    for name, fn in [("fifth-review/C1091b-patch-resolves-by-switching-off", c1091b_patch_resolves_by_switching_off)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1052-replacing-navmesh-base-count-said", c1052_replacing_navmesh_base_count_said)]:
        cell(name, fn)

    # C980 (round 2, B2: Snazzy review Blocking 1, Clockwork review S3): the switch-off sentence counts objects, as it
    # names them. ModB switches 2 objects off with three fields each (record flags, enable parent, position below the
    # world); ModA's versions have them on: 2 objects, not 6.
    def c980_switch_off_counts_objects():
        p_rows, chs = [], []
        full_ = offs(-30000, "InitiallyDisabled", "000014:Skyrim.esm")
        for D in (f"00C980:{BASE}", f"00C981:{BASE}"):
            p_rows += [lost_row(MOD_B, MOD_A, D, "Placement", off_txt(full_), PH, off_txt(offs(0)), before=BASE),
                       lost_row(MOD_B, MOD_A, D, "SkyrimMajorRecordFlags", ["SkyrimMajorRecordFlags=InitiallyDisabled"], PH,
                                ["SkyrimMajorRecordFlags=0"], before=BASE),
                       lost_row(MOD_B, MOD_A, D, "EnableParent", ["EnableParent.Flags=SetEnableStateToOppositeOfParent",
                                                                  "EnableParent.Reference=000014:Skyrim.esm"], PH, PH,
                                before=BASE)]
            chs.append((D, "PlacedObject", [(BASE, offs(0)), (MOD_B, full_), (MOD_A, offs(0))]))
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chs, w_chains=chs, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        ok = f"On 2 objects, {MOD_B}'s value switches the object off" in it and "In 6 of them" not in it
        return ok, it[-500:]

    # C987 (round 2, B2, found by the SWITCHOFF audit on Winterhold): a lost record-flags value written as a number
    # (3072: initially disabled, bit 2048, and bit 1024) switches the object off as the flag's name does.
    def c987_numeric_initially_disabled_switches_off():
        D = f"00C987:{BASE}"
        lost_ = offs(0, "3072")
        p_rows = [lost_row(MOD_B, MOD_A, D, "SkyrimMajorRecordFlags", "3072", PH, "0", before=BASE)]
        chs = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_B, lost_), (MOD_A, offs(0))])]
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=chs, w_chains=chs, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return f"On 1 object, {MOD_B}'s value switches the object off" in it, it[-400:]

    for name, fn in [("round-2/C980-switch-off-counts-objects", c980_switch_off_counts_objects),
                     ("round-2/C987-numeric-initially-disabled-switches-off", c987_numeric_initially_disabled_switches_off)]:
        cell(name, fn)

    # C981 (round 2, B3: Snazzy review Blocking 2): the headline's shared objects are every object of the entry that any
    # entry counted before it holds. The record-field entry (4 records, first) holds 000F01; the contact entry (3 objects,
    # second) holds 000F02; the fixture entry (000F01, 000F02, third) shares both.
    def c981_shared_objects_complete():
        F1, F2 = f"000F01:{MOD_A}", f"000F02:{MOD_A}"
        rec_rows = [lost_row(MOD_B, MOD_A, fk_, "Placement", "b", "a", "v") for fk_ in (F1, f"000F03:{MOD_A}",
                                                                                         f"000F04:{MOD_A}", f"000F05:{MOD_A}")]
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, fk_, other=f"000H0{i}:{MOD_B}") for i, fk_ in ((1, F1), (2, F2))]
        ct = [row("CONTACT", MOD_B, MOD_A, fk_, other=f"000H09:{MOD_B}") for fk_ in (F2, f"000F06:{MOD_A}", f"000F07:{MOD_A}")]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("dependency-only", "plus-a", "plus-b")}
        results.update({(FIXTURE, "baseline"): fx, (CONTACT, "baseline"): ct})
        arms_ = four_arms()
        enr = {(RECORDS, a): ([] if a != "baseline" else rec_rows) for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
        plan, md, html = compile_and_render(arms_, results, [MOD_A, MOD_B], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                            enrichment=enr)
        v = (reader(plan).get("prediction") or {}).get("verdict") or ""
        fx_part = next((x for x in re.split(r";\s+(?:and\s+)?", v) if "Fixtures left hanging" in x), "")
        ok = (F1 in fx_part and F2 in fx_part) or "the same 2 objects" in fx_part
        return ok, f"fixture part {fx_part!r}; verdict {v[:500]!r}"

    for name, fn in [("round-2/C981-shared-objects-complete", c981_shared_objects_complete)]:
        cell(name, fn)

    # C982, C983 (round 2, R: Snazzy review S1): a water height's dependence on the cell's HasWater flag is read from the
    # winning plugin's own cell record (its DATA flags) where the setup names that file; never "not in the record data".
    # Without a read, the item says the flag was not read for this report.
    def water_bundle(flags):
        C1 = f"000C82:{BASE}"
        tr_ = tree_transcript([(C1, "Cell", [(BASE, {"WaterHeight": "-2147483600"}), (OTHER, {"WaterHeight": "0"}),
                                             (MOD_A, {"WaterHeight": "-2147483600"})])])
        r_ = lost_row(OTHER, MOD_A, C1, "WaterHeight", ["WaterHeight=0"], PH, PH, label="TestCellWet")
        r_["Summary"] = f"Cell TestCellWet: WaterHeight - {OTHER}'s value lost to {MOD_A}'s"
        arms_ = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        if flags is not None:
            with_files(arms_, {MOD_A: plugin_file_of(MOD_A, [BASE], [("CELL", 0x00000C82, [("DATA", struct.pack(
                "<H", flags))], False)], group="CELL")})
        plan, md, _ = core_bundle([r_], [r_], check=CELLATTR, masters=MASTERS_, p_tr=tr_, arms=arms_)
        return next((ln for ln in md.splitlines() if ln.startswith("- **Cell settings")), "")

    def c982_water_flag_read_from_plugin():
        it = water_bundle(0x0081)
        ok = ("do not include HasWater" in it and "not in the record data" not in it and "read from" in it)
        return ok, it[-400:]

    def c983_water_flag_unread_said_not_read():
        it = water_bundle(None)
        ok = "not read for this report" in it and "not in the record data" not in it
        return ok, it[-400:]

    for name, fn in [("round-2/C982-water-flag-read-from-plugin", c982_water_flag_read_from_plugin),
                     ("round-2/C983-water-flag-unread-said-not-read", c983_water_flag_unread_said_not_read)]:
        cell(name, fn)

    # C984 (round 2, R1: BPT review S1): "the value lost is an absence" only where the losing mod has none of the field:
    # OtherMod's lost Data holds an entry of its own (a location it points at) beside entries only ModA has.
    def c984_absence_only_when_whole_value_absent():
        W1 = f"00C984:{BASE}"
        r_ = lost_row(OTHER, MOD_A, W1, "Data", [f"Data[0].Location.Target.Link=000984:{OTHER}",
                                                 f"Data[10] only in {MOD_A}: [APackageData] Name=Destination"], PH, PH)
        r_["Summary"] = f"Package TestPackage: Data - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], masters=MASTERS_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return f"{OTHER} has no" not in it and "absence" not in it, it[-400:]

    # C988 (round 2, R1 guard, Missives S2's SolitudeWorld): a lost value whose lines are only the winner's entries and the
    # loser's own "ABSENT here" for the field is still an absence.
    def c988_absent_here_line_is_absence():
        W1 = f"00C988:{BASE}"
        r_ = lost_row(OTHER, MOD_A, W1, "MaxHeight", [f"MaxHeight.CellData only in {MOD_A}: 00", "MaxHeight: ABSENT here"],
                      PH, PH)
        r_["Summary"] = f"Worldspace TestWorld: MaxHeight - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], masters=MASTERS_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return f"{OTHER} has no " in it and "absence" in it, it[-400:]

    for name, fn in [("round-2/C984-absence-only-when-whole-value-absent", c984_absence_only_when_whole_value_absent),
                     ("round-2/C988-absent-here-line-is-absence", c988_absent_here_line_is_absence)]:
        cell(name, fn)

    # C985, C986 (round 2, R2: Missives review S1, Snazzy review S2): the earlier clashes a patch also fixes are counted as
    # distinct changes in game: one field lost by two plugins is one; a Grid change only in bytes the game does not read
    # is none. Where the patch has no such field at all, the clash is said as the patch removing it, not as a fix.
    def earlier_fix_bundle(patch_has_field, whole=False, o2_carries=False, fourth=False, base_value=False,
                           later_carries=False):
        """Fix (patch_has_field): OtherMod and Neighbour set MaxHeight's cell data 05, ModA 09, the patch 05. Removal:
        OtherMod and Neighbour have no MaxHeight, ModA has 09, the patch has none. Without ModA both lose to Later.esp too (already
        there). A Grid row differing only in unused bytes is lost the same way and fixed too. fourth: Fourth.esp, after
        Neighbour, carries OtherMod's value too (built with OtherMod as a master)."""
        O2, O3 = "Neighbour.esp", "Later.esp"
        O4s = ["Fourth.esp"] if fourth else []
        # later_carries (eighth review): Walls.esp, before Later.esp, makes the 07 change; Later.esp only carries it
        O5s = ["Walls.esp"] if later_carries else []
        W1, C2 = f"00C985:{BASE}", f"00C986:{BASE}"
        arms_ = [arm("baseline", [BASE, OTHER, O2, *O4s, *O5s, O3, MOD_A, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, OTHER, O2, *O4s, *O5s, O3, MOD_A]),
                 arm("without-a", [BASE, OTHER, O2, *O4s, *O5s, O3])]
        mh_ = lambda x: {"MaxHeight.CellData": x, "MaxHeight.Offset": "0"} if x else {}  # noqa: E731
        # base_value (round 3): the earlier losers and the patch hold the base game's own value, 00
        lose_ = ("00" if base_value else "05") if patch_has_field else None
        chain_ = [(BASE, mh_("00")), (OTHER, mh_(lose_)), (O2, mh_(lose_)), *[(x_, mh_(lose_)) for x_ in O4s],
                  *[(x_, mh_("07")) for x_ in O5s], (O3, mh_("07")), (MOD_A, mh_("09"))]
        p_ch = [(W1, "Worldspace", chain_)]
        # a tree answer writes the field the patch lacks as the import captures it ("has X ABSENT", "has no X")
        w_ch = [(W1, "Worldspace", chain_ + [(PATCH, mh_(lose_) if patch_has_field else {
            "MaxHeight": "has MaxHeight ABSENT", "MaxHeight.CellData": "has no MaxHeight.CellData"})])]
        lost_ = ([f"MaxHeight.CellData={lose_}"] if patch_has_field else [f"MaxHeight.CellData only in {MOD_A}: 09"])
        mh_row = lambda a_, b_: lost_row(a_, b_, W1, "MaxHeight", lost_, PH, ["MaxHeight.CellData=00"])  # noqa: E731
        def grid_(a_, b_):
            r_ = lost_row(a_, b_, C2, "Grid", ["Grid.Flags=327680"], PH, ["Grid.Flags=5504256"])
            r_["Summary"] = f"Cell TestCellGrid: Grid - {a_}'s value lost to {b_}'s"
            return r_
        p_rows = [mh_row(OTHER, MOD_A), mh_row(O2, MOD_A), *[mh_row(x_, MOD_A) for x_ in O4s], grid_(OTHER, MOD_A)]
        n_rows = [mh_row(OTHER, O3), mh_row(O2, O3), *[mh_row(x_, O3) for x_ in O4s], grid_(OTHER, O3)]
        enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "without-a"): n_rows, (RECORDS, "baseline"): []}
        def absent_too(tr_):
            """A tree answer also lists a field the winner has and a provider lacks, as the record imports capture it."""
            req_, resp_ = tr_
            body_ = json.loads(resp_[1]["result"]["content"][0]["text"])
            for r_, (_, _, chain__) in zip(body_["rows"], [c for c in (p_ch if tr_ is p_tr_ else w_ch)]):
                win__ = chain__[-1][1]
                for n_, (_, f__) in zip(r_["nodes"], chain__):
                    if not n_["is_winner"]:
                        n_["deltas"] += [f"{k}: ABSENT here" for k in win__ if k not in f__]
            resp_[1]["result"]["content"][0]["text"] = json.dumps(body_)
            return req_, resp_
        p_tr_ = tree_transcript(p_ch)
        tr = {(RECORDS, "without-patch"): absent_too(p_tr_), (RECORDS, "baseline"): tree_transcript(w_ch)}
        plan, md, _ = compile_and_render(arms_, {(CONTACT, a_): [] for a_ in ("baseline", "without-patch", "without-a")},
                                         [MOD_A], patches=[PATCH], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         enrichment=enr, transcripts=tr, masters={**MASTERS_, O2: [BASE, OTHER] if o2_carries
                                                                                  else [BASE],
                                                   O3: [BASE, *O5s], PATCH: [BASE], **{x_: [BASE] for x_ in O5s},
                                                   **{x_: [BASE, OTHER] for x_ in O4s}})
        if whole:
            return md
        return next((x for x in md.splitlines() if "but not predicted" in x), "")

    def c985_earlier_fixes_counted_once():
        ln = earlier_fix_bundle(True)
        m_ = re.search(r"not predicted[^:]*: (\d+)", ln)
        return bool(m_) and m_.group(1) == "1" and "also fix" in ln, ln[:400]

    def c986_patch_removal_said_as_removal():
        # (fourth review, Snazzy r2/r3 S1) the removal is its own line now, never under "but not predicted" (C1046)
        md_ = earlier_fix_bundle(False, whole=True)
        ln = next((x for x in md_.splitlines() if "removes" in x and "max height data" in x), "")
        return "removes" in ln and "also fixes" not in ln, ln[:400]

    for name, fn in [("round-2/C985-earlier-fixes-counted-once", c985_earlier_fixes_counted_once),
                     ("round-2/C986-patch-removal-said-as-removal", c986_patch_removal_said_as_removal)]:
        cell(name, fn)

    # ------------------------------------------------ third review, round 3 (C1000-C1019): the re-reviews of regen-r2b
    # C1000-C1002 (round 3, B1: BPT re-review Blocking 1): which plugins' values lose to the patch is decided by the same
    # equality rule the prediction sets rows aside by (the import's chain reads both values the same: case, unused
    # bytes, rounding). OtherMod moves the object; the patch carries OtherMod's position with the rotation rounded in
    # the last float digit. Where ModA kept the base game's value, the patch resolves the clash; where ModA moved it
    # too, the patch keeps OtherMod's value over ModA's change; a patch value that really differs stays its own.
    def rounding_bundle(a_moves, patch_rz, patch_z="0"):
        P = f"00CA00:{BASE}"
        b_ = pl(100, 100, 0, "1.5707963")
        o_ = pl(160, 100, 0, "1.5707963")
        a_ = pl(100, 40, 0, "1.5707963") if a_moves else dict(b_)
        w_ = pl(160, 100, patch_z, patch_rz)
        p_ch = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        w_ch = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_), (PATCH, w_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        w_rows = [pl_row(P, OTHER, PATCH, b_, o_, w_), pl_row(P, MOD_A, PATCH, b_, a_, w_)]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch,
                                  masters={**MASTERS_, PATCH: [BASE, OTHER, MOD_A]})
        return [e.get("withPatch") for e in rec_of(plan, P) if e.get("admission") == "admitted"]

    def c1000_rounding_only_patch_value_resolves():
        got = rounding_bundle(False, "1.5707964", "2.5E-05")
        return got == ["resolved"], f"{got}"

    def c1001_rounding_only_patch_value_keeps_losing_value():
        got = rounding_bundle(True, "1.5707964")
        return got == ["switched"], f"{got}"

    def c1002_patch_value_really_different_stays_own():
        got = rounding_bundle(False, "1.6")
        return got == ["own"], f"{got}"

    for name, fn in [("round-3/C1000-rounding-only-patch-value-resolves", c1000_rounding_only_patch_value_resolves),
                     ("round-3/C1001-rounding-only-patch-value-keeps-losing-value",
                      c1001_rounding_only_patch_value_keeps_losing_value),
                     ("round-3/C1002-patch-value-really-different-stays-own", c1002_patch_value_really_different_stays_own)]:
        cell(name, fn)

    # C1005, C1006 (round 3b, review finding 1): where neither side's value loses to the patch (only a third plugin's
    # does), the recorded values decide by the same read-same rule: the patch's value read the same as the losing mod's
    # (rounding in the last digit) is resolved, as the winning mod's is kept; a real difference stays the patch's own.
    def fallback_bundle(patch_rz):
        P, T3 = f"00CA05:{BASE}", "ThirdMod.esp"
        b_ = pl(100, 100, 0, "0")
        t_ = pl(100, 100, 0, "0.5")
        o_ = pl(160, 100, 0, "1.5707963")
        a_ = pl(100, 40, 0, "0")
        w_ = pl(160, 100, 0, patch_rz)
        p_ch = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        w_ch = [(P, "PlacedObject", [(BASE, b_), (T3, t_), (OTHER, o_), (MOD_A, a_), (PATCH, w_)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        w_rows = [pl_row(P, T3, PATCH, b_, t_, w_)]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch,
                                  masters={**MASTERS_, T3: [BASE], PATCH: [BASE, T3, OTHER, MOD_A]})
        return [e.get("withPatch") for e in rec_of(plan, P) if e.get("admission") == "admitted"]

    def c1005_fallback_rounding_only_resolves():
        got = fallback_bundle("1.5707964")
        return got == ["resolved"], f"{got}"

    def c1006_fallback_real_difference_stays_own():
        got = fallback_bundle("1.6")
        return got == ["own"], f"{got}"

    for name, fn in [("round-3b/C1005-fallback-rounding-only-resolves", c1005_fallback_rounding_only_resolves),
                     ("round-3b/C1006-fallback-real-difference-stays-own", c1006_fallback_real_difference_stays_own)]:
        cell(name, fn)

    # C1003, C1004 (round 3, B2: Clockwork re-review Blocking 1): a navmesh comparison counts what its sentence names,
    # distinct navmesh records with new (or repaired) broken links, from the comparison's own rows, never the probe's
    # (record, target navmesh) pairs. N1 links two targets (three links), N2 one: the probe counts 3 pairs, there are
    # 2 records. Repaired: the same rows the other way round.
    def nav_link_row(a, b, nav, target, tri):
        return {"Kind": "EDGE-IDX", "A": a, "B": b,
                "Summary": (f"navmesh {nav} in CityExterior03 (ext) edge-links {target} triangle {tri} of 10   "
                            f"[record provided by: {BASE} -> {a}]"),
                "Necessary": [{"FormKey": nav, "Label": "CityExterior03 (ext)", "Physical": "Holds"}],
                "Alternative": [{"FormKey": target, "Role": "navmesh"}]}

    def nav_cmp_bundle(direction):
        n1, n2, t1, t2 = f"00CA31:{BASE}", f"00CA32:{BASE}", f"00CA41:{BASE}", f"00CA42:{BASE}"
        rows_ = [nav_link_row(MOD_A, MOD_B, n1, t1, 11), nav_link_row(MOD_A, MOD_B, n1, t1, 12),
                 nav_link_row(MOD_A, MOD_B, n1, t2, 13), nav_link_row(MOD_A, MOD_B, n2, t1, 14)]
        if direction == "created":
            order = {"without-patch": {"navownprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                           created=3, newDefect=3, attributed=0, unmeasured=0)}}
            log = f"NEW     EDGE-IDX    {MOD_A}  x  {MOD_B}   +3 record(s): created 3\n"
            results = {(NAVOWN, "without-patch"): rows_, (NAVOWN, "baseline"): []}
        else:
            order = {"without-patch": {"navownprobe": dict(verdict="DIFFERENTIAL-HELPFUL", repaired=3, reclassified=0,
                                                           created=0, newDefect=0, attributed=0, unmeasured=0)}}
            log = f"GONE    EDGE-IDX    {MOD_A}  x  {MOD_B}   -3 record(s): repaired 3\n"
            results = {(NAVOWN, "without-patch"): [], (NAVOWN, "baseline"): rows_}
        plan, md, html = compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            diff_logs={(NAVOWN, "without-patch"): log})
        kind_ = "regression" if direction == "created" else "repaired"
        f = next((x for x in plan.get("findings") or [] if x.get("kind") == kind_ and x.get("checkId") == NAVOWN), {})
        return f.get("readerPlain") or "", md

    def c1003_navmesh_comparison_counts_records():
        plain, md = nav_cmp_bundle("created")
        return "2 navmesh records with new broken links" in plain and "2 navmesh records" in md, plain

    def c1004_navmesh_repairs_count_records():
        plain, md = nav_cmp_bundle("repaired")
        return "repaired 2 existing navmesh records with broken links" in plain, plain

    for name, fn in [("round-3/C1003-navmesh-comparison-counts-records", c1003_navmesh_comparison_counts_records),
                     ("round-3/C1004-navmesh-repairs-count-records", c1004_navmesh_repairs_count_records)]:
        cell(name, fn)

    # ------------------------------------------------ after the fourth review (C1020-C1099): what a lost value means in
    # game is held against everything else the same plugins do to the same record.
    # C1020-C1023 (Winterhold S1): ModA's lost value switches the object off (below the world, opposite to the player)
    # and ModB's version has it on. Where ModB placed the object itself -- moved it across at least its size (no bounds
    # here: 64 u) from the spot ModA inherits, or gave it another base -- the object drawn is ModB's own placement, not the
    # one ModA removes: never "still brings back", and the still-there line says what ModB did. Where ModB left it at
    # the spot (C1021) or only nudged it (C1022), ModB still brings back the object ModA removes.
    def reuse_bundle(win_pos="100,100,0", win_base=None):
        D = f"00CA20:{BASE}"
        lost_ = offs(-30000, "InitiallyDisabled", "000014:Skyrim.esm")
        win_ = offs(0)
        win_["Placement.Position"] = win_pos
        base_ = offs(0)
        for d_ in (lost_, win_, base_):
            d_["Base"] = f"0B0A20:{BASE}"
        if win_base:
            win_["Base"] = win_base
        rows_ = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(lost_), off_txt(win_), off_txt(base_), before=BASE)]
        ch_ = [(D, "PlacedObject", [(BASE, base_), (MOD_A, lost_), (MOD_B, win_)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]})
        st = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        part = next((x for x in re.split(r";\s+(?![^()]*\))", st) if D in x), "")
        return D, st, part

    def c1020_moved_by_winner_not_brought_back():
        D, st, part = reuse_bundle(win_pos="1100,100,0")
        return ("brings back" not in part and bool(part) and "moved" in part and "1,000 u" in part), st[:400]

    def c1021_same_spot_still_brought_back():
        D, st, part = reuse_bundle()
        return re.search(r"still brings back 1 object\b", part) is not None, st[:400]

    def c1022_nudged_still_brought_back():
        D, st, part = reuse_bundle(win_pos="110,100,0")
        return re.search(r"still brings back 1 object\b", part) is not None, st[:400]

    def c1023_rebased_by_winner_not_brought_back():
        D, st, part = reuse_bundle(win_base=f"0B0A21:{MOD_B}")
        return ("brings back" not in part and bool(part) and "another base" in part), st[:400]

    # C1024, C1025 (Winterhold S2, Snazzy r3 S2, and the BPT shape): a lost change on an object the winning version (in
    # the setup with the patch) sets off for good, where ModA's own version has the object on, is not "no effect in
    # game": the object ModA shows is not drawn. C1024: the patch carries ModB's removal (kept); C1025: nothing patches
    # it (left), ModB enables it opposite to the player.
    def off_effect_bundle(kept):
        D = f"00CA24:{BASE}"
        a_ = offs(0)
        a_["Placement.Position"] = "300,100,0"
        b_ = offs(-30000) if kept else offs(0, "0", "000014:Skyrim.esm")
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        if kept:
            w_rows = [lost_row(MOD_A, PATCH, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
            w_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_), (PATCH, b_)])]
        else:
            # (sixth review, BPT r6 S1) ModB carries an earlier mod's removal: still left (an own removal standing is
            # its own outcome, C1101)
            w_rows = list(p_rows)
            w_ch = [(D, "PlacedObject", [(BASE, offs(0)), (OTHER, b_), (MOD_A, a_), (MOD_B, b_)])]
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), *([] if kept else [(OTHER, b_)]), (MOD_A, a_), (MOD_B, b_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch,
                                  masters={**MASTERS_, PATCH: [BASE], **({} if kept else {MOD_B: [BASE, OTHER]})})
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        return kl, str(pr_.get("withPatchStays") or ""), md

    def c1024_kept_removal_of_shown_object_not_drawn():
        kl, st, md = off_effect_bundle(kept=True)
        return (re.search(r"\b1 on objects [^;()]*not drawn", kl) is not None and "no effect in game" not in kl
                and "no effect in game" not in str(reader_summary(md))), kl[:400]

    def c1025_left_removal_of_shown_object_not_drawn():
        kl, st, md = off_effect_bundle(kept=False)
        return (re.search(r"\b1 on objects [^;()]*not drawn", st) is not None and "no effect in game" not in st
                and re.search(r"\b1 on objects [^;()]*not drawn", kl) is not None), st[:400]

    def reader_summary(md):
        return next((ln for ln in md.splitlines() if ln.startswith("In short, with")), "")

    # C1026-C1028 (BPT r3 S2): ModA undoes its master Update.esm's fix (puts back the value from before it). Where ModA
    # also changed the same record itself -- another base (C1026), or a new position while undoing the fix that switched
    # the object off (C1027) -- the loss is likely deliberate, said with its count; where it changed nothing else
    # (C1028), the line says the change is lost in game and nothing of intent.
    def undo_rework_bundle(kind):
        R = f"00CA26:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        b0, u0, a0 = offs(0), offs(0), offs(0)
        for d_ in (b0, u0, a0):
            d_["Base"] = f"0B0A26:{BASE}"
        if kind == "base":
            u0["Placement.Position"] = "100,100,12"         # Update.esm seats the object
            a0["Base"] = f"0B0A27:{MOD_A}"                   # ModA puts the old seat back and gives it its own base
            field, lost = "Placement", ["Placement.Position=100,100,12"]
        else:
            u0.update(offs(-30000, "InitiallyDisabled", "000014:Skyrim.esm"))   # Update.esm switches it off
            u0["Base"] = f"0B0A26:{BASE}"
            if kind == "moved":
                a0["Placement.Position"] = "100,140,0"       # ModA switches it back on and moves it itself
            field, lost = "EnableParent", ["EnableParent.Reference=000014:Skyrim.esm",
                                           "EnableParent.Flags=SetEnableStateToOppositeOfParent"]
        chains = [(R, "PlacedObject", [(BASE, b0), (UPD, u0), (MOD_A, a0)])]
        p_rows = [lost_row(UPD, MOD_A, R, field, lost, PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE], PATCH: [BASE]})
        adm = [e["admission"] for e in rec_of(plan, R)]
        line = next((ln for ln in md.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        return adm, line

    def c1026_undo_on_rebased_record_likely_deliberate():
        adm, line = undo_rework_bundle("base")
        return adm == ["undoesMaster"] and re.search(r"\b1 of them [^.]*likely deliberate", line) is not None, \
            f"{adm}; {line[:300]}"

    def c1027_undo_on_moved_record_likely_deliberate():
        adm, line = undo_rework_bundle("moved")
        return adm == ["undoesMaster"] and re.search(r"\b1 of them [^.]*likely deliberate", line) is not None, \
            f"{adm}; {line[:300]}"

    def c1028_undo_alone_says_lost_no_intent():
        adm, line = undo_rework_bundle("plain")
        return (adm == ["undoesMaster"] and "is lost in game" in line and "deliberate" not in line), f"{adm}; {line[:300]}"

    # C1061, C1062 (Gate A condition 1): the losing version's own state words the note. ModA's version under an enable
    # parent (C1061) is drawn when that parent is enabled, never "has on"; ModA's version only initially disabled (C1062)
    # may be enabled by a script or quest. Both: not drawn with ModB's removal, never "no effect in game".
    def off_state_bundle(loser):
        D = f"00CA61:{BASE}"
        a_ = offs(0, "InitiallyDisabled") if loser == "starts" else offs(0)
        a_["Placement.Position"] = "300,100,0"
        if loser == "parent":
            a_.update({"EnableParent.Reference": f"000F61:{OTHER}", "EnableParent.Flags": "0"})
        b_ = offs(-30000)
        rows_ = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        ch_ = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)])]
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]})
        return str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")

    def c1061_parent_loser_worded_apart():
        st = off_state_bundle("parent")
        return ("under an enable parent" in st and "has on" not in st and "no effect in game" not in st), st[:400]

    def c1062_starts_off_loser_worded_apart():
        st = off_state_bundle("starts")
        return ("only starts disabled" in st and "has on" not in st and "no effect in game" not in st), st[:400]

    # C1063 (Gate A condition 2, guard): ModA undoes Update.esm's switch-off and its position differs from Update.esm's
    # only in the seventh decimal: no change of its own as the record chain reads it, so nothing is "likely deliberate".
    def c1063_rounding_is_no_rework():
        R = f"00CA63:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        b0, a0 = offs(0), offs(0)
        u0 = offs(0, "0", "000014:Skyrim.esm")
        a0["Placement.Position"] = "100.0000001,100,0"
        chains = [(R, "PlacedObject", [(BASE, b0), (UPD, u0), (MOD_A, a0)])]
        p_rows = [lost_row(UPD, MOD_A, R, "EnableParent", ["EnableParent.Reference=000014:Skyrim.esm",
                                                            "EnableParent.Flags=SetEnableStateToOppositeOfParent"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE], PATCH: [BASE]})
        adm = [e["admission"] for e in rec_of(plan, R)]
        line = next((ln for ln in md.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        return adm == ["undoesMaster"] and "deliberate" not in line and "is lost in game" in line, f"{adm}; {line[:300]}"

    # C1029 (BPT r2/r3 S1): ModB switches off an object whose base has no model (a room marker) and ModA has it on: the
    # item says it is enabled, with nothing drawn, never "the object is drawn".
    def c1029_no_model_switched_on_not_drawn():
        D1 = f"00CA29:{BASE}"
        lost_, win_ = offs(-30000), offs(0)
        rows_ = [lost_row(MOD_B, MOD_A, D1, "Placement", off_txt(lost_), off_txt(win_), off_txt(offs(0)), before=BASE)]
        ch_ = [(D1, "PlacedObject", [(BASE, offs(0)), (MOD_B, lost_), (MOD_A, win_)])]
        names = {"refs": {D1: {"base": f"00001F:{BASE}"}},
                 "bases": {f"00001F:{BASE}": {"type": "Static", "editorId": "RoomMarker", "model": ""}}}
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B), names=names)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return (f"{MOD_B}'s value switches the object off" in it and "is drawn" not in it and "no model" in it), it[-400:]

    # C1035 (Sewers r2 S1, S4): the switch the other check finds is said with what that check found after the flip: the
    # overlap with the object that leaves ends, but the other object then sits N u inside something else; the trigger
    # was not found (the row's state is Unknown). Never "hold only until then".
    def c1035_switch_after_flip_said():
        X, Yo = f"000X35:{MOD_A}", f"000Y35:{MOD_B}"
        ov_ = row("OVERLAP", MOD_B, MOD_A, X, other=Yo)
        st_ = row("STATE-CONTACT@000S35:ModB.esp@Unknown", MOD_B, MOD_A, Yo, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {Yo} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip - hung from the host as authored (0u gap); under the winner it sits "
                          f"18.6u inside something else in Camp (ext)")
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A]), arm("without-a", [BASE, MOD_B])]
        results = {(OVERLAP, a_): ([ov_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch", "without-b", "without-a")}
        results.update({(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch", "without-b", "without-a")})
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        # (seventh review, Sewers S1) the depth is the row's own object's, against something the check does not name
        return ("hold only until" not in it and "18.6 u inside something it does not name" in it
                and "was not found" in it), it[-400:]

    # C1064 (Winterhold page limit, fourth review): the ladder step after the tail step also moves the location load
    # order section into the evidence index's further notes, says so in the pointer line, and comes before any step
    # that shows fewer rows or figures; the tail step itself keeps the section.
    def c1064_order_step_moves_location_order():
        plan, md0, html0 = record_validation_bundle()
        pr_ = R.load_priorities()
        ladder_ = pr_.get("abridgementLadder") or []
        st_ = next((s_ for s_ in ladder_ if {"tail", "order"} <= set(str(s_.get("layout")).split())), None)
        tail_ = next((s_ for s_ in ladder_ if str(s_.get("layout")).split()[-1:] == ["tail"]), None)
        if st_ is None or tail_ is None:
            return False, "no order step after the tail step in the ladder"
        out_ = Path(tempfile.mkdtemp(prefix="c1064-"))
        v_ = R.effective_view(plan, st_["rowsPerGroup"], st_["figuresPerFinding"], pr_, st_["layout"])
        got_ = R.render_all(plan, v_, out_, "")
        out2_ = Path(tempfile.mkdtemp(prefix="c1064t-"))
        v2_ = R.effective_view(plan, tail_["rowsPerGroup"], tail_["figuresPerFinding"], pr_, tail_["layout"])
        got2_ = R.render_all(plan, v2_, out2_, "")
        head_ = "Load order for these locations"
        i_ = ladder_.index(st_)
        ok = (f"## {head_}" not in got_["report.md"] and f"<h2>{head_}</h2>" not in got_["report.html"]
              and f"### {head_}" in got_["evidence-index.md"] and f"<h3>{head_}</h3>" in got_["evidence-index.html"]
              and "load order for these locations" in got_["report.md"].lower().split("## further notes", 1)[-1]
              and f"## {head_}" in got2_["report.md"] and ladder_.index(tail_) < i_
              and all(s_["rowsPerGroup"] >= st_["rowsPerGroup"] and s_["figuresPerFinding"] >= st_["figuresPerFinding"]
                      for s_ in ladder_[:i_]))
        return ok, got_["report.md"][-300:]

    for name, fn in [("fourth-review/C1064-order-step-moves-location-order", c1064_order_step_moves_location_order)]:
        cell(name, fn)

    # C1036 (Winterhold S3): two clashes of the quest-or-switch check already there with one mod alone, of different
    # kinds (ModA's object sunk in another; ModB's fixture left in empty space): the line gives each mod's clashes their
    # own meaning, never the first one's for both.
    def c1036_alone_state_meaning_per_mod():
        arms = [arm("baseline", [BASE, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, MOD_B]), arm("without-b", [BASE, THIRD, MOD_A]),
                arm("without-a", [BASE, THIRD, MOD_B])]
        r1 = row("STATE-CONTACT@000S36:ThirdMod.esp@Triggered", THIRD, MOD_A, f"000X36:{THIRD}", label="Hall (int)",
                 other=f"000Y36:{MOD_A}")
        r1["Summary"] = f"Contact StateConditional under GuardSwitch ({THIRD}): 000X36:{THIRD} arrives - Sunk: sits 9u inside something"
        r2 = row("STATE-EMPTY@000S36:ThirdMod.esp@Triggered", THIRD, MOD_B, f"000Z36:{THIRD}", label="Hall (int)")
        # (fifth review, Winterhold S3) the gap is 12 u: a 0 u row is now set aside as cannot tell (C1087), so this row keeps
        # the per-mod meaning this case tests
        r2["Summary"] = (f"LeftInEmptySpace: Furniture Cot (fixed) in Hall (int); {MOD_B} removed or moved 000H36:Skyrim.esm; "
                         f"its assembly of 1 touches no floor or wall now (nearest floor or wall 12u) @ 000S36:ThirdMod.esp")
        results = {(STATE, "baseline"): [], (STATE, "without-patch"): [r1, r2], (STATE, "without-b"): [r1],
                   (STATE, "without-a"): [r2]}
        plan, md, html = compile_and_render(arms, results, [MOD_A, MOD_B], patches=[PATCH])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- **{title}**")), "")
        # (seventh review, Sewers S1) the depth is the row's own object's, against something the check does not name
        ok = (re.search(r"from ModA\.esp: when GuardSwitch is set, 000X36:ThirdMod\.esp sits 9 u inside "
                        r"another", line) is not None
              and "In game: when" not in line)
        return ok, line[-400:]

    # C1037 (Winterhold S4): the top says 2 navmesh records still lose ground with the patch (both re-measured Holds); the
    # patch section's history line counts only the 1 no longer flagged. It says the other 1 is still flagged, so the two
    # figures reconcile in one sentence.
    def c1037_history_counts_reconciled():
        n1, n2 = f"000N71:{BASE}", f"000N72:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="CityExterior01 (ext)  598 tris -> 345"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="CityExterior02 (ext)  689 tris -> 5")]
        w_rows = [row("CLOBBERED", OTHER, PATCH, n1, label="CityExterior01 (ext)  465 tris -> 345")]
        preds = [{"Kind": "CLOBBERED", "A": MOD_A, "B": MOD_B, "FormKey": n_, "Other": None, "Physical": "Holds",
                  "Detail": "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u from the winner's"}
                 for n_ in (n1, n2)]
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(NAVOWN, "with-patch"): preds})
        val_sec = section_text(md, headings(plan).get("validation") or "\0")
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        ok = ("2 of these navmesh records still lose" in top and "The other 1 of the 2 navmesh records" in val_sec)
        return ok, f"{top[-200:]!r}; {[ln[-300:] for ln in val_sec.splitlines() if 'flagged' in ln]}"

    # C1039 (BPT r3 S3): a by-object comparison's repairs count in the unit of its new problems: objects with a body. Of
    # the 2 problems the patch repairs, one is an object whose base has no model: "repaired 1", the other said apart.
    def c1039_repairs_count_bodied_objects():
        G0, R1, R2, X1 = f"000G39:{MOD_A}", f"000R39:{MOD_A}", f"000L39:{MOD_A}", f"000X39:{MOD_A}"
        base = [row("CONTACT", MOD_B, MOD_A, x_, other=f"000H{i}9:{MOD_B}") for i, x_ in enumerate((G0, R1, R2))]
        withp = [base[0], row("CONTACT", MOD_B, MOD_A, X1, other=f"000H49:{MOD_B}")]
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=2, reclassified=0,
                                                     created=1, newDefect=1, attributed=0, unmeasured=0)}}
        log = (f"NEW     CONTACT    {MOD_B}  x  {MOD_A}   +1 record(s): created 1\n"
               f"SHRANK  CONTACT    {MOD_B}  x  {MOD_A}   -2 record(s): repaired 2\n")
        refs = {**{x_: {"base": f"0B0S39:{MOD_A}"} for x_ in (G0, R1, X1)}, R2: {"base": f"0B0L39:{MOD_A}"},
                **{f"000H{i}9:{MOD_B}": {"base": f"0B0W39:{MOD_B}"} for i in range(5)}}
        bases = {f"0B0S39:{MOD_A}": {"type": "Static", "model": "lantern.nif"}, f"0B0L39:{MOD_A}": {"type": "Light", "model": ""},
                 f"0B0W39:{MOD_B}": {"type": "Static", "model": "table.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): withp},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            names={"refs": refs, "bases": bases}, diff_logs={(CONTACT, "with-patch"): log})
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        pl_ = str(reg.get("readerPlain") or "")
        return ("repaired 1 existing problem" in pl_ and "repaired 2" not in pl_ and "no model" in pl_), pl_[-300:]

    # C1057 (BPT r2/r3 S5): a new object's nearest surface at the contact probe's search limit (128 u) is said as nothing
    # found within the limit, never as a distance; a measured gap below it stays a distance.
    def c1057_search_limit_not_a_distance():
        X1, X2 = f"000X57:{MOD_A}", f"000X58:{MOD_A}"
        new = [row("CONTACT", MOD_B, MOD_A, X1, other=f"000H57:{MOD_B}"), row("CONTACT", MOD_B, MOD_A, X2, other=f"000H58:{MOD_B}")]
        new[0]["Summary"] += "; nearest floor or wall 128u"
        new[1]["Summary"] += "; nearest floor or wall 4.9u"
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                     created=2, newDefect=2, attributed=0, unmeasured=0)}}
        log = f"NEW     CONTACT    {MOD_B}  x  {MOD_A}   +2 record(s): created 2\n"
        refs = {X1: {"base": f"0B0P57:{MOD_A}"}, X2: {"base": f"0B0P57:{MOD_A}"},
                **{f"000H5{i}:{MOD_B}": {"base": f"0B0W57:{MOD_B}"} for i in (7, 8)}}
        bases = {f"0B0P57:{MOD_A}": {"type": "Static", "editorId": "ShadowPlane", "model": "plane.nif"},
                 f"0B0W57:{MOD_B}": {"type": "Static", "editorId": "Wall", "model": "wall.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): new},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order,
                                            names={"refs": refs, "bases": bases}, diff_logs={(CONTACT, "with-patch"): log})
        notes = " ".join(str(n.get("objectsNote") or "") for n in (reader(plan).get("validation") or {}).get("newByCheck") or [])
        return ("128 u," not in notes and not notes.rstrip().endswith("128 u") and "within 128 u" in notes
                and "4.9 u" in notes), notes[:300]

    # C1045, C1047 (Clockwork S5, Missives S1 wording, BPT r2 S4): a change the patch also fixes that is already lost
    # without the evaluated mod is said as what it is: the losing plugin's change and the plugin it is lost to in the
    # setup without the evaluated mod, never "present without the evaluated mods" beside a pair naming that mod.
    def c1045_earlier_fix_names_loser_and_winner():
        ln = earlier_fix_bundle(True)
        return ("present without the evaluated mods" not in ln and f"{OTHER}'s change" in ln and "Later.esp" in ln), ln[:500]

    # C1046 (Snazzy r2 S1, r3 S1): a field the patch removes (it holds none of it) is a loss the patch introduces, said on
    # its own line with its in-game effect not established, never under "Resolved ... but not predicted".
    def c1046_patch_removal_own_line():
        md = earlier_fix_bundle(False, whole=True)
        nl = next((x for x in md.splitlines() if "but not predicted" in x), "")
        own = next((x for x in md.splitlines() if "removes" in x and "max height data" in x), "")
        return ("removes" not in nl and bool(own) and "but not predicted" not in own and "not established" in own),             f"{nl[:200]} || {own[:300]}"

    # C1041 (Clockwork r3 S2): the carried-value clause counts record fields, as the sentence it is part of does: two
    # plugins carrying HearthFires.esm's value on one cell and one on another are 3 fields, "in 3 of them", not 2 records.
    def c1041_carried_clause_counts_fields():
        W1, W2, O2 = f"00CA41:{BASE}", f"00CA42:{BASE}", "Neighbour.esp"
        arms = [arm("baseline", [BASE, HF_, THIRD, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, THIRD, OTHER, O2, MOD_A, MOD_B])]
        ch_ = [(BASE, {"WaterHeight": "0"}), (HF_, {"WaterHeight": "3.4028235E+38"}), (THIRD, {"WaterHeight": "-2147483648"}),
               (OTHER, {"WaterHeight": "3.4028235E+38"}), (O2, {"WaterHeight": "3.4028235E+38"}),
               (MOD_A, {"WaterHeight": "-2147483648"})]
        chains = [(W1, "Cell", ch_), (W2, "Cell", ch_)]
        p_rows = [lost_row(x_, MOD_A, w_, "WaterHeight", ["WaterHeight=3.4028235E+38"], PH, PH)
                  for x_, w_ in ((HF_, W1), (OTHER, W1), (O2, W1), (HF_, W2), (OTHER, W2))]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], THIRD: [BASE], OTHER: [BASE, HF_], O2: [BASE, HF_],
                                           MOD_A: [BASE], MOD_B: [BASE]})
        # (seventh review, Clockwork S2) the 3 rows are 2 record fields, every one carrying an earlier change: counted in
        # fields with their plugin pairs, with that one explanation (the old "in 3 of them" counted rows as fields)
        line = next((ln for ln in md.splitlines() if "only carries an earlier plugin's change" in ln), "")
        return re.search(r"2 record fields \(3 plugin pairs\) where the losing plugin only carries an earlier plugin's change",
                         line) is not None, line[-300:]

    # C1040 (Snazzy S3): the detail list's count of results that are not clashes between two mods names its population
    # (result rows of every setup measured, already-there ones included), so it is not read against the prediction's
    # "with the base game" count of new clashes.
    def c1040_detail_aside_population_named():
        found = []
        for fn_ in (base_game_only_bundle, already_present_and_mod_partner_bundle, detail_bundle):
            plan = fn_()[0]
            ln_ = str(reader(plan).get("detailAsideLine") or "")
            if ln_:
                found.append(ln_)
        return bool(found) and all("every setup measured" in x_ for x_ in found), f"{found[:1]}"

    for name, fn in [("fourth-review/C1040-detail-aside-population-named", c1040_detail_aside_population_named)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1041-carried-clause-counts-fields", c1041_carried_clause_counts_fields)]:
        cell(name, fn)

    # C1044 (Clockwork r3 S1): a base-game value the winner drops, carried by a later Creation Club plugin as well, is the
    # change of the base-game plugin that introduced it (Update.esm), never the later carrier's; the line says what the
    # setup with the patch has (the patch keeps Update.esm's value).
    def c1044_base_value_named_by_its_origin():
        W, CC1 = f"00CA44:{BASE}", "ccTestCarrier.esl"
        arms = [arm("baseline", [BASE, UPD, CC1, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, CC1, OTHER, MOD_A, MOD_B])]
        p_ch = [(W, "Cell", [(BASE, {"Owner": "v"}), (UPD, {"Owner": "u"}), (CC1, {"Owner": "u"}), (OTHER, {"Owner": "u"}),
                             (MOD_A, {"Owner": "v"})])]
        w_ch = [(W, "Cell", [(BASE, {"Owner": "v"}), (UPD, {"Owner": "u"}), (CC1, {"Owner": "u"}), (OTHER, {"Owner": "u"}),
                             (MOD_A, {"Owner": "v"}), (PATCH, {"Owner": "u"})])]
        p_rows = [lost_row(x_, MOD_A, W, "Owner", ["Owner=u"], PH, PH) for x_ in (UPD, CC1, OTHER)]
        w_rows = [lost_row(MOD_A, PATCH, W, "Owner", ["Owner=v"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, w_rows, check=CELLATTR, arms=arms, p_chains=p_ch, w_chains=w_ch,
                                  masters={UPD: [BASE], CC1: [BASE], OTHER: [BASE, UPD], MOD_A: [BASE], MOD_B: [BASE],
                                           PATCH: [BASE, UPD, MOD_A]})
        line = next((ln for ln in md.splitlines() if "own change of" in ln), "")
        return (f"wins over {UPD}'s own change" in line and f"{CC1}'s own change" not in line
                and f"with {PATCH}" in line and f"{UPD}'s value in 1 of the 1" in line), line[-400:]

    # C1038 (Pilgrim S3): an alias split is one clash, the quest scene: its two rows (one per actor, the quest as the other
    # record), new with the patch and already there without the evaluated mod, are 1 clash brought back; the
    # detail line's split counts 1, as its scene count does; the summary says nothing ELSE new.
    def c1038_alias_pair_one_clash():
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A]), arm("without-a", [BASE, OTHER, O2])]
        # (fixture corrected on regen-r4a: the probe's alias rows name an actor, with the quest as the other record; one
        # quest scene of two actors is two rows)
        A1, A2, Q = f"000A38:{BASE}", f"000B38:{BASE}", f"000Q38:{BASE}"
        rows_ = [row("SPLIT", OTHER, PATCH, x_, other=Q, label="Temple (int)") for x_ in (A1, A2)]
        nb_ = [row("SPLIT", OTHER, O2, x_, other=Q, label="Temple (int)") for x_ in (A1, A2)]
        for r_ in rows_ + nb_:
            r_["Summary"] = "quest 'Q1' aliases 'X' and 'Y' were 137u apart in Temple (int), now 1,800u apart"
        plan, md, _ = compile_and_render(arms, {(ALIAS, "baseline"): rows_, (ALIAS, "without-patch"): [],
                                                (ALIAS, "without-a"): nb_}, [MOD_A], patches=[PATCH])
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        det = next((ln for ln in md.splitlines() if "quest scene" in ln and "Measured load order" in ln), "")
        ok = ("1 clash brought back" in summ and "no other new clash" in summ and "(2 new with the patch)" not in det
              and "(1 new with the patch)" in det)
        return ok, f"{summ[:200]} || {det[-200:]}"

    # C1051 (Pilgrim S4): Placement rows where the winner puts the objects far lower than the loser (a straight vertical
    # difference) say the height difference, and that what it puts them inside or below was not measured.
    def c1051_height_difference_said():
        rows_ = []
        for i_ in (1, 2):
            D_ = f"00CA5{i_}:{BASE}"
            rows_.append(lost_row(MOD_A, MOD_B, D_, "Placement", f"Position=100,{i_}00,1424; Rotation=0,0,0",
                                  f"Position=100,{i_}00,0; Rotation=0,0,0", f"Position=100,{i_}00,0; Rotation=0,0,0",
                                  before=BASE))
        plan, md, _ = core_bundle(rows_, list(rows_), targets=(MOD_A, MOD_B), masters={**MASTERS_, PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("1,424 u lower" in it and "not measured" in it), it[-400:]

    # C1050 (Pilgrim S2): an overlap pair one of whose objects has a model in the effects folder (mist, fog, light beams)
    # is said as a visual effect, not a solid body, whose crossing may not show; the pair stays counted.
    def c1050_effect_model_pairs_said():
        X, Y, Z = f"000X50:{MOD_A}", f"000Y50:{MOD_B}", f"000Z50:{MOD_B}"
        rows_ = [row("OVERLAP", MOD_B, MOD_A, X, other=Y), row("OVERLAP", MOD_B, MOD_A, X, other=Z)]
        refs = {X: {"base": f"0B0X50:{MOD_A}"}, Y: {"base": f"0B0Y50:{MOD_B}"}, Z: {"base": f"0B0Z50:{MOD_B}"}}
        bases = {f"0B0X50:{MOD_A}": {"type": "Static", "editorId": "Rock", "model": "Landscape\\Rock01.nif"},
                 f"0B0Y50:{MOD_B}": {"type": "MoveableStatic", "editorId": "FXMistLow01", "model": "Effects\\Ambient\\FXMistLow01.nif"},
                 f"0B0Z50:{MOD_B}": {"type": "Static", "editorId": "Wall", "model": "Architecture\\Wall01.nif"}}
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        results = {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): rows_, (OVERLAP, "without-b"): []}
        plan, md, html = compile_and_render(arms, results, [MOD_A], patches=[PATCH], names={"refs": refs, "bases": bases})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        adm = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or [] if e["checkId"] == OVERLAP]
        return ("1 of these" in it and "visual-effect model" in it and adm == ["admitted", "admitted"]), f"{adm}; {it[-400:]}"

    # C1056 (Missives S5): a winner's own change to a cell's land flags says whether the setup with the patch still has it
    # (the patch leaves the cell to ModA: still so in 1 of the 1).
    def c1056_land_flags_with_patch_status():
        C1 = f"00CA56:{BASE}"
        r_ = lost_row(OTHER, MOD_A, C1, "Grid", ["Grid.Flags=-412494080"], PH, ["Grid.Flags=-499936256"], label="TestCellLand")
        r_["Summary"] = f"Cell TestCellLand: Grid - {OTHER}'s value lost to {MOD_A}'s"
        ch_ = [(BASE, {"Grid.Flags": "5504256"}), (OTHER, {"Grid.Flags": "-412494080"}), (MOD_A, {"Grid.Flags": "-1223611121"})]
        tr_ = tree_transcript([(C1, "Cell", ch_)])
        plan, md, _ = core_bundle([r_], [r_], check=CELLATTR, masters=MASTERS_, p_tr=tr_, w_chains=[(C1, "Cell", ch_)])
        line = next((ln for ln in md.splitlines() if "land flags" in ln and MOD_A in ln), "")
        return f"with {PATCH}, still so in 1 of the 1" in line, line[-300:]

    # C1049 (Missives S4): a lost value that differs from the winner's only in a flags leaf names that leaf and both
    # values; and where the plugin whose value is used without the winner is not the loser named, the item names it.
    def c1049_flags_leaf_and_previous_winner():
        W, O2 = f"00CA49:{BASE}", "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A, MOD_B])]
        par_ = lambda f_: {"Parent.Worldspace": "00003C:Skyrim.esm", "Parent.Flags": f_}  # noqa: E731
        chains = [(W, "Worldspace", [(BASE, par_("UseImageSpaceData")), (OTHER, par_("0")), (O2, par_("0")),
                                     (MOD_A, par_("UseImageSpaceData"))])]
        p_rows = [lost_row(OTHER, MOD_A, W, "Parent", ["Parent.Flags=0"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains,
                                  masters={**MASTERS_, O2: [BASE], PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("differ only in its flags" in it and "UseImageSpaceData" in it and f"Without {MOD_A}" in it
                and O2 in it), it[-500:]

    # C1055 (Missives S2): a record import whose own tool note says it narrowed a field out of the comparison gives a
    # coverage fact naming the check and the field, so a reader does not take the field as judged.
    def c1055_import_narrowing_said():
        fn_ = getattr(R, "import_narrowed_facts", None)
        if fn_ is None:
            return False, "no rule reads an import's narrowing note"
        rows_ = [{"checkId": CELLATTR, "armId": "baseline", "tool": {"name": "housecarl_records tree over the 21 CELL "
                  "records narrowed to their compared fields without MaxHeightData (N11), composed by a script"}},
                 {"checkId": CELLATTR, "armId": "with-patch", "tool": {"name": "housecarl_records tree over the 21 CELL "
                  "records narrowed to their compared fields without MaxHeightData (N11), composed by a script"}},
                 {"checkId": RECORDS, "armId": "baseline", "tool": {"name": "housecarl_records tree"}}]
        facts = fn_(rows_, R.load_priorities(), lambda c_: "Cell settings" if c_ == CELLATTR else c_)
        return (len(facts) == 1 and "MaxHeightData" in facts[0] and "Cell settings" in facts[0]
                and "not judged" in facts[0]), f"{facts}"

    for name, fn in [("fourth-review/C1055-import-narrowing-said", c1055_import_narrowing_said)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1049-flags-leaf-and-previous-winner", c1049_flags_leaf_and_previous_winner)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1056-land-flags-with-patch-status", c1056_land_flags_with_patch_status)]:
        cell(name, fn)

    # C1051b (found on regen-r4a): where the rows carry the winner's value as a placeholder (a tree answer), the height
    # difference is read from the record chain's positions.
    def c1051b_height_difference_from_chain():
        D_ = f"00CA5B:{BASE}"
        lo_, wi_ = offs(1424), offs(0)
        rows_ = [lost_row(MOD_A, MOD_B, D_, "Placement", ["Placement.Position=100,100,1424"], PH, PH)]
        ch_ = [(D_, "PlacedObject", [(BASE, offs(0)), (MOD_A, lo_), (MOD_B, wi_)])]
        plan, md, _ = core_bundle(rows_, list(rows_), targets=(MOD_A, MOD_B), p_chains=ch_, w_chains=ch_,
                                  masters={**MASTERS_, PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return "1,424 u lower" in it, it[-300:]

    # C1051c, C1051d (found on regen-r4c: Pilgrim's priests also moved across): every Placement row of the pair lower by
    # 64 u or more in the winner's value is a level difference, said with its range whatever the horizontal move (C1051c);
    # rows differing in height both ways are not (C1051d).
    def height_pair_bundle(zs):
        rows_ = []
        for i_, (zl_, zw_) in enumerate(zs):
            D_ = f"00CA6{i_}:{BASE}"
            rows_.append(lost_row(MOD_A, MOD_B, D_, "Placement", f"Position=100,{2000 + i_},{zl_}; Rotation=0,0,0",
                                  f"Position=150,{i_}00,{zw_}; Rotation=0,0,0", f"Position=150,{i_}00,{zw_}; Rotation=0,0,0",
                                  before=BASE))
        plan, md, _ = core_bundle(rows_, list(rows_), targets=(MOD_A, MOD_B), masters={**MASTERS_, PATCH: [BASE]})
        return next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")

    def c1051c_level_difference_with_horizontal_move():
        it = height_pair_bundle([(1424, 0), (1712, 0)])
        return "1,424 u to 1,712 u lower" in it, it[-300:]

    def c1051d_mixed_heights_not_said():
        it = height_pair_bundle([(1424, 0), (-500, 0)])
        return "lower" not in it and "higher" not in it, it[-300:]

    for name, fn in [("fourth-review/C1051c-level-difference-with-horizontal-move", c1051c_level_difference_with_horizontal_move),
                     ("fourth-review/C1051d-mixed-heights-not-said", c1051d_mixed_heights_not_said),
                     ("fourth-review/C1051b-height-difference-from-chain", c1051b_height_difference_from_chain),
                     ("fourth-review/C1050-effect-model-pairs-said", c1050_effect_model_pairs_said),
                     ("fourth-review/C1051-height-difference-said", c1051_height_difference_said)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1038-alias-pair-one-clash", c1038_alias_pair_one_clash)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1044-base-value-named-by-its-origin", c1044_base_value_named_by_its_origin)]:
        cell(name, fn)

    # C1066 (correction of 1dd9c3d9, found by claimaudit BGVALUE on regen-r4b): the origin of a carried base-game value is
    # searched from the first plugin after the record's own first value: Skyrim.esm holds u, Update.esm changes it to v,
    # HearthFires.esm (built on Update.esm) puts u back as its own change, and OtherMod (also built on Update.esm) carries
    # u. OtherMod's value is HearthFires' change it carries: set aside, the loss said as HearthFires.esm's; never the
    # record's first value counted as OtherMod's own change.
    def c1066_base_origin_after_first_value():
        W = f"00CA66:{BASE}"
        arms = [arm("baseline", [BASE, UPD, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, HF_, OTHER, MOD_A, MOD_B])]
        ch_ = [(W, "Cell", [(BASE, {"Owner": "u"}), (UPD, {"Owner": "v"}), (HF_, {"Owner": "u"}), (OTHER, {"Owner": "u"}),
                            (MOD_A, {"Owner": "w"})])]
        p_rows = [lost_row(x_, MOD_A, W, "Owner", ["Owner=u"], PH, PH) for x_ in (HF_, OTHER)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=ch_,
                                  masters={UPD: [BASE], HF_: [BASE, UPD], OTHER: [BASE, UPD], MOD_A: [BASE], MOD_B: [BASE],
                                           PATCH: [BASE]})
        got = {e["A"]: (e["admission"], e.get("baseValueOf")) for e in rec_of(plan, W)}
        return got.get(OTHER) == ("notOwnEdit", HF_), f"{got}"

    for name, fn in [("fourth-review/C1066-base-origin-after-first-value", c1066_base_origin_after_first_value)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1045-earlier-fix-names-loser-and-winner", c1045_earlier_fix_names_loser_and_winner),
                     ("fourth-review/C1046-patch-removal-own-line", c1046_patch_removal_own_line)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1057-search-limit-not-a-distance", c1057_search_limit_not_a_distance)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1039-repairs-count-bodied-objects", c1039_repairs_count_bodied_objects)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1037-history-counts-reconciled", c1037_history_counts_reconciled)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1036-alone-state-meaning-per-mod", c1036_alone_state_meaning_per_mod)]:
        cell(name, fn)

    # C1058 (Sewers S2): where the also-found check's rows come only from the separate evaluation (the check failed on
    # this bundle's setups), the item says its finding comes from that evaluation.
    def c1058_also_found_from_sibling_said():
        X, Yo = f"000X58:{MOD_A}", f"000Y58:{MOD_B}"
        ov_ = row("OVERLAP", MOD_B, MOD_A, X, other=Yo)
        st_ = row("STATE-CONTACT@000S58:ModB.esp@Unknown", MOD_B, MOD_A, Yo, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {Yo} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip")
        s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("dependency-only", [BASE]),
                  arm("plus-a", [BASE, MOD_A]), arm("plus-b", [BASE, MOD_B])]
        s_results = {(OVERLAP, "baseline"): [ov_], (OVERLAP, "dependency-only"): [], (OVERLAP, "plus-a"): [],
                     (OVERLAP, "plus-b"): [], (STATE, "baseline"): [st_], (STATE, "dependency-only"): [],
                     (STATE, "plus-a"): [], (STATE, "plus-b"): []}
        results = {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [ov_], (STATE, "baseline"): []}
        plan, md, html = compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            sibling=(s_arms, s_results, [MOD_A, MOD_B], {"plan_id": "sibl00001058"}))
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        return ("also found by the check" in it and "separate evaluation" in it.split("also found by the check", 1)[-1]), it[-400:]

    # C1059 (Sewers S3): the patch comparison says which other mods the patch covers (its masters besides the base game
    # and the evaluated mod), so what is still there with a mod it does not cover reads as outside what it set out to fix.
    def c1059_patch_scope_said():
        O2 = "Neighbour.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, MOD_A])]
        r_ = row("OVERLAP", O2, MOD_A, f"000X59:{MOD_A}", other=f"000Y59:{O2}")
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [r_], (OVERLAP, "without-patch"): [r_]}, [MOD_A],
                                         patches=[PATCH], masters={OTHER: [BASE], O2: [BASE], MOD_A: [BASE],
                                                                   PATCH: [BASE, OTHER, MOD_A]})
        val = reader(plan).get("validation") or {}
        txt = " ".join(val.get("sentences") or [])
        # (round 3) the scope line says what the records show, not the patch's intent or its masters
        ln = next((x for x in (val.get("sentences") or []) if "supplied patch edit" in x), "")
        return (O2 in ln and "lies on records, navmeshes or objects that no supplied patch edits" in ln), ln[:400] or txt[:300]

    # C1060 (Sewers S5): a clash set aside because its object's base record was not read names that object (and the
    # place its row gives), so a modder can look at it.
    def c1060_body_unread_object_named():
        refs = {f"000L0{i}:{MOD_A}": {"base": f"0B0S01:{MOD_A}"} for i in (1, 2)}
        refs.update({f"000H0{i}:{MOD_B}": {"base": f"0B0S01:{MOD_A}"} for i in (1, 2, 3)})
        bases = {f"0B0S01:{MOD_A}": {"type": "Static", "model": "rock.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, body_rows(), [MOD_A, MOD_B], patches=[PATCH],
                                            names={"refs": refs, "bases": bases})
        line = next((ln for ln in md.splitlines() if "base record was not read" in ln), "")
        seg = line.split("base record was not read", 1)[-1][:200]
        return f"000L03:{MOD_A}" in seg, seg

    for name, fn in [("fourth-review/C1060-body-unread-object-named", c1060_body_unread_object_named)]:
        cell(name, fn)

    # C1067 (found between regen-r4d and regen-r4e: Clockwork's line changed order): the covered masters are named in load
    # order, whatever order the patch's master list gives them in.
    def c1067_patch_scope_in_load_order():
        O2, O3, O4 = "Neighbour.esp", "Third.esp", "Fourth.esp"
        arms = [arm("baseline", [BASE, OTHER, O2, O3, O4, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, O2, O3, O4, MOD_A])]
        r_ = row("OVERLAP", O4, MOD_A, f"000X67:{MOD_A}", other=f"000Y67:{O4}")
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [r_], (OVERLAP, "without-patch"): [r_]}, [MOD_A],
                                         patches=[PATCH], masters={OTHER: [BASE], O2: [BASE], O3: [BASE], O4: [BASE],
                                                                   MOD_A: [BASE], PATCH: [BASE, O3, OTHER, O2, MOD_A]})
        # (round 3) the masters list is no longer said (the scope reads edits, and says what the records show): the
        # leftover with Fourth.esp, which no supplied patch edits, is named in the scope line
        val_ = (reader(plan).get("validation") or {}).get("sentences") or []
        ln = next((x for x in val_ if "supplied patch edit" in x), "")
        return (f"still there with {O4} lies on records" in ln and not any("masters" in x for x in val_)), ln[:300]

    for name, fn in [("fourth-review/C1059-patch-scope-said", c1059_patch_scope_said),
                     ("fourth-review/C1067-patch-scope-in-load-order", c1067_patch_scope_in_load_order)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1058-also-found-from-sibling-said", c1058_also_found_from_sibling_said)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1035-switch-after-flip-said", c1035_switch_after_flip_said)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1029-no-model-switched-on-not-drawn", c1029_no_model_switched_on_not_drawn)]:
        cell(name, fn)

    # C1031, C1032 (Clockwork r3 S3, Gate A answer 5 and condition 4): the mirror of C941. The WINNER's version only
    # starts the object disabled (no enable parent) and no record of the winning plugin refers to it (read from its file).
    # C1031: the losing version sets it off for good too: both off, set aside, the reason saying its reference check
    # covered the winning plugin's own records only. C1032: the losing version has it on: counted, the item names the
    # winner as the one that starts it disabled, and says the read found no reference, with no "may enable" hedge.
    def winner_start_off_bundle(loser_off, plugin_path):
        D1 = f"00C941:{BASE}"
        a_ = offs(-30000) if loser_off else offs(0)
        if not loser_off:
            a_["Placement.Position"] = "300,100,0"
        b_ = offs(-155, "InitiallyDisabled")
        p_rows = [lost_row(OTHER, MOD_A, D1, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        ch_ = [(D1, "PlacedObject", [(BASE, offs(0)), (OTHER, a_), (MOD_A, b_)])]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        if plugin_path is not None:
            sha_ = hashlib.sha256(Path(plugin_path).read_bytes()).hexdigest()
            for a__ in arms:
                for pl_ in a__["requested"]["plugins"]:
                    if pl_["name"] == MOD_A:
                        pl_["path"], pl_["sha256"] = str(plugin_path), sha_
        plan, md, _ = core_bundle(p_rows, list(p_rows), p_chains=ch_, w_chains=ch_, arms=arms,
                                  masters={**MASTERS_, PATCH: [BASE]})
        return plan, md, D1

    def c1031_both_off_winner_unreferenced_set_aside():
        plan, md, D1 = winner_start_off_bundle(True, write_plugin(MOD_A, False))
        adm = [e["admission"] for e in rec_of(plan, D1)]
        line = next((ln for ln in md.splitlines() if "only starts" in ln and "Also not counted" in ln), "")
        return (adm == ["offUnreferencedWinner"] and f"no record of {MOD_A} refers to" in line
                and "nothing in the load order" not in line), f"{adm}; {line[-300:]}"

    def c1032_winner_start_off_named_no_hedge():
        plan, md, D1 = winner_start_off_bundle(False, write_plugin(MOD_A, False))
        adm = [e["admission"] for e in rec_of(plan, D1)]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return (adm == ["admitted"] and f"{MOD_A} only starts" in it and f"no record of {MOD_A} refers to" in it
                and "a script or quest may enable it" not in it), f"{adm}; {it[-400:]}"

    for name, fn in [("fourth-review/C1031-both-off-winner-unreferenced-set-aside",
                      c1031_both_off_winner_unreferenced_set_aside),
                     ("fourth-review/C1032-winner-start-off-named-no-hedge", c1032_winner_start_off_named_no_hedge)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1061-parent-loser-worded-apart", c1061_parent_loser_worded_apart),
                     ("fourth-review/C1062-starts-off-loser-worded-apart", c1062_starts_off_loser_worded_apart),
                     ("fourth-review/C1063-rounding-is-no-rework", c1063_rounding_is_no_rework)]:
        cell(name, fn)

    for name, fn in [("fourth-review/C1020-moved-by-winner-not-brought-back", c1020_moved_by_winner_not_brought_back),
                     ("fourth-review/C1021-same-spot-still-brought-back", c1021_same_spot_still_brought_back),
                     ("fourth-review/C1022-nudged-still-brought-back", c1022_nudged_still_brought_back),
                     ("fourth-review/C1023-rebased-by-winner-not-brought-back", c1023_rebased_by_winner_not_brought_back),
                     ("fourth-review/C1024-kept-removal-of-shown-object-not-drawn",
                      c1024_kept_removal_of_shown_object_not_drawn),
                     ("fourth-review/C1025-left-removal-of-shown-object-not-drawn",
                      c1025_left_removal_of_shown_object_not_drawn),
                     ("fourth-review/C1026-undo-on-rebased-record-likely-deliberate",
                      c1026_undo_on_rebased_record_likely_deliberate),
                     ("fourth-review/C1027-undo-on-moved-record-likely-deliberate",
                      c1027_undo_on_moved_record_likely_deliberate),
                     ("fourth-review/C1028-undo-alone-says-lost-no-intent", c1028_undo_alone_says_lost_no_intent)]:
        cell(name, fn)

    # ---------------------------------------- fifth review (2026-10-22): what an object with no model does in game, and a
    # removal the winning mod makes itself.
    # C1068-C1073 (Snazzy line 17, BPT line 19): ModB switches off an object whose base has no model and ModA's version has
    # it on. What that object does in game depends on its base record's type (skill data), never "shows nothing": a
    # light lights the space around it (C1068), an idle marker is a spot NPCs can use (C1070); a static marker (C1069)
    # or an activator (C1071) may still act in game, which the record data read here does not say. Said after drawn
    # objects (C1072) and one meaning per type in one pair (C1073).
    def no_model_segments(objs, drawn=()):
        """objs: [(object id, base type, editor ID)] with no model; drawn: object ids whose base has a model. Returns the
        record-field item line and, per object, the sentence of it that names the object."""
        rows_, ch_, refs_, bases_ = [], [], {}, {}
        for fk_, typ_, eid_ in [*objs, *[(d_, "Static", "Crate01") for d_ in drawn]]:
            b_ = f"0B{fk_[2:6]}:{BASE}"
            lost_, win_ = offs(-30000), offs(0)
            rows_.append(lost_row(MOD_B, MOD_A, fk_, "Placement", off_txt(lost_), off_txt(win_), off_txt(offs(0)), before=BASE))
            ch_.append((fk_, "PlacedObject", [(BASE, offs(0)), (MOD_B, lost_), (MOD_A, win_)]))
            refs_[fk_] = {"base": b_}
            bases_[b_] = {"type": typ_, "editorId": eid_, "model": "" if fk_ not in drawn else "clutter\\crate01.nif"}
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B), names={"refs": refs_, "bases": bases_})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        sents = re.split(r"(?<=[.)])\s+(?=On |In |For |Most |\[)", it)
        return it, {fk_: next((s_ for s_ in sents if fk_ in s_ and "no model" in s_), "") for fk_, _, _ in objs}

    def c1068_no_model_light_lights():
        it, seg = no_model_segments([(f"00CA68:{BASE}", "Light", "ShadowLight01")])
        s_ = seg[f"00CA68:{BASE}"]
        return (bool(s_) and "nothing in game" not in it and "lights" in s_ and "is drawn" not in s_), it[-400:]

    def c1069_no_model_static_marker_not_read():
        it, seg = no_model_segments([(f"00CA69:{BASE}", "Static", "MarkerBox01")])
        s_ = seg[f"00CA69:{BASE}"]
        return (bool(s_) and "nothing in game" not in it and "not read here" in s_ and "is drawn" not in s_), it[-400:]

    def c1070_no_model_idle_marker_npcs():
        it, seg = no_model_segments([(f"00CA70:{BASE}", "IdleMarker", "LeanIdle01")])
        s_ = seg[f"00CA70:{BASE}"]
        return (bool(s_) and "nothing in game" not in it and "NPCs" in s_), it[-400:]

    def c1071_no_model_activator_not_read():
        it, seg = no_model_segments([(f"00CA71:{BASE}", "Activator", "TriggerBox01")])
        s_ = seg[f"00CA71:{BASE}"]
        return (bool(s_) and "nothing in game" not in it and "not read here" in s_), it[-400:]

    def c1072_no_model_light_after_drawn_lights():
        it, seg = no_model_segments([(f"00CA72:{BASE}", "Light", "ShadowLight01")], drawn=(f"00CB72:{BASE}",))
        s_ = seg[f"00CA72:{BASE}"]
        return (bool(s_) and "is drawn" in it and "nothing in game" not in it and "lights" in s_), it[-400:]

    def c1073_no_model_one_meaning_per_type():
        it, seg = no_model_segments([(f"00CA73:{BASE}", "Light", "ShadowLight01"),
                                     (f"00CB73:{BASE}", "IdleMarker", "LeanIdle01")])
        sl_, si_ = seg[f"00CA73:{BASE}"], seg[f"00CB73:{BASE}"]
        return (bool(sl_) and bool(si_) and sl_ != si_ and "lights" in sl_ and "NPCs" not in sl_ and "NPCs" in si_
                and "lights" not in si_ and "nothing in game" not in it), it[-500:]

    for name, fn in [("fifth-review/C1068-no-model-light-lights", c1068_no_model_light_lights),
                     ("fifth-review/C1069-no-model-static-marker-not-read", c1069_no_model_static_marker_not_read),
                     ("fifth-review/C1070-no-model-idle-marker-npcs", c1070_no_model_idle_marker_npcs),
                     ("fifth-review/C1071-no-model-activator-not-read", c1071_no_model_activator_not_read),
                     ("fifth-review/C1072-no-model-light-after-drawn-lights", c1072_no_model_light_after_drawn_lights),
                     ("fifth-review/C1073-no-model-one-meaning-per-type", c1073_no_model_one_meaning_per_type)]:
        cell(name, fn)

    # C1074-C1076 (BPT S2): ModA has the object on and moved; ModB, the winner, switches it off for good by its own change
    # (below the world, initially disabled; the version it inherits has it on). That is ModB's own removal: every
    # with-patch sentence says the winner removes the object itself (C1074, kept by the patch), and the still-there line
    # says it for the untouched ones, with the patch keeping the same removal on the others (C1075). Where the winner only
    # carries an earlier mod's removal (C1076, guard), it is not said as the winner's own.
    def own_removal_bundle(kept_ids, left_ids, inherited=False):
        p_rows, w_rows, p_ch, w_ch = [], [], [], []
        mas_ = {**MASTERS_, PATCH: [BASE]}
        for fk_ in [*kept_ids, *left_ids]:
            a_ = offs(0)
            a_["Placement.Position"] = "300,100,0"
            b_ = offs(-30000, "InitiallyDisabled")
            pre_ = [(BASE, offs(0))]
            if inherited:
                pre_.append((OTHER, b_))                     # an earlier mod removes it; ModB carries that removal
                mas_[MOD_B] = [BASE, OTHER]
            p_rows.append(lost_row(MOD_A, MOD_B, fk_, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE))
            p_ch.append((fk_, "PlacedObject", [*pre_, (MOD_A, a_), (MOD_B, b_)]))
            if fk_ in kept_ids:
                w_rows.append(lost_row(MOD_A, PATCH, fk_, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE))
                w_ch.append((fk_, "PlacedObject", [*pre_, (MOD_A, a_), (MOD_B, b_), (PATCH, b_)]))
            else:
                w_rows.append(p_rows[-1])
                w_ch.append(p_ch[-1])
        arms = None
        if inherited:
            arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                    arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters=mas_, arms=arms)
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        return kl, str(pr_.get("withPatchStays") or ""), plan, md

    def c1074_kept_own_removal_said_winners():
        kl, st, plan, md = own_removal_bundle([f"00CA74:{BASE}"], [])
        return (re.search(r"\b1 on objects [^;()]*winner's own change sets off", kl) is not None
                and "winner sets off for good" not in kl), kl[:400]

    def c1075_left_own_removal_patch_keeps_same():
        kl, st, plan, md = own_removal_bundle([f"00CA75:{BASE}"], [f"00CB75:{BASE}"])
        part = next((x for x in re.split(r";\s+(?![^()]*\))", st) if "record field" in x), "")
        # (sixth review, BPT r6 S1) the untouched row is the winner's own removal standing: out of the still-there line,
        # said beside the kept row on which the patch carries the same removal
        return (not part and "winner's own removal stands on 1" in kl
                and len(re.findall(r"\b1 on objects [^;()]*winner's own change sets off", kl)) == 1), f"{part[:300]} || {kl[:300]}"

    def c1076_inherited_removal_not_winners_own():
        kl, st, plan, md = own_removal_bundle([], [f"00CA76:{BASE}"], inherited=True)
        return ("winner's own change sets off" not in kl and "winner's own change sets off" not in st
                and re.search(r"\b1 on objects [^;()]*winner sets off for good", kl) is not None), f"{kl[:300]} || {st[:300]}"

    # C1078 (BPT S2, Gate A condition R3): the top of the report says it too: the headline's off-for-good count says how
    # many are the winner's own removal, and the record-field item says the winner removes the objects itself.
    def c1078_own_removal_at_the_top():
        kl, st, plan, md = own_removal_bundle([], [f"00CA78:{BASE}"])
        pr_ = reader(plan).get("prediction") or {}
        verdict = str(pr_.get("verdict") or "")
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("1 by the winner's own removal" in verdict and "own removal" in it), f"{verdict[:300]} || {it[-300:]}"

    # C1077 (Snazzy S2, Winterhold R2-2): a kept lost change gets its meaning in game too. D1: ModA switches the object
    # off, ModB has it on and the patch carries ModB's on version: the object ModA removes is drawn. D2: the same, but
    # the patch moves it 900 u across: drawn at the patch's own spot. D3: ModA's change of the base is lost; ModB's base
    # is kept: the object is ModB's.
    def c1077_kept_rows_meaning():
        D1, D2, D3 = f"00CA77:{BASE}", f"00CB77:{BASE}", f"00CC77:{BASE}"
        a_off = offs(-30000, "InitiallyDisabled")
        p_rows, w_rows, p_ch, w_ch = [], [], [], []
        for D, pat_pos in ((D1, None), (D2, "1000,100,0")):
            b_ = offs(0)
            pt_ = offs(0)
            if pat_pos:
                pt_["Placement.Position"] = pat_pos
            p_rows.append(lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_off), off_txt(b_), off_txt(offs(0)), before=BASE))
            w_rows.append(lost_row(MOD_A, PATCH, D, "Placement", off_txt(a_off), off_txt(pt_), off_txt(offs(0)), before=BASE))
            p_ch.append((D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_off), (MOD_B, b_)]))
            w_ch.append((D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_off), (MOD_B, b_), (PATCH, pt_)]))
        o_, a_, b_ = ({**offs(0), "Base": f"0B0C{n}:{BASE}"} for n in ("70", "71", "70"))
        p_rows.append(lost_row(MOD_A, MOD_B, D3, "Base", [f"Base=0B0C71:{BASE}"], [f"Base=0B0C70:{BASE}"],
                               [f"Base=0B0C70:{BASE}"], before=BASE))
        w_rows.append(lost_row(MOD_A, PATCH, D3, "Base", [f"Base=0B0C71:{BASE}"], [f"Base=0B0C70:{BASE}"],
                               [f"Base=0B0C70:{BASE}"], before=BASE))
        p_ch.append((D3, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_)]))
        w_ch.append((D3, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_), (PATCH, b_)]))
        # D4 (audit KEPTMEANING on regen-r5b): ModA's switch-off written as a flags number (3072: bit 2048 set)
        D4 = f"00CD77:{BASE}"
        a4_, b4_ = offs(0, "3072"), offs(0, "1024")
        p_rows.append(lost_row(MOD_A, MOD_B, D4, "SkyrimMajorRecordFlags", ["SkyrimMajorRecordFlags=3072"],
                               ["SkyrimMajorRecordFlags=1024"], ["SkyrimMajorRecordFlags=1024"], before=BASE))
        w_rows.append(lost_row(MOD_A, PATCH, D4, "SkyrimMajorRecordFlags", ["SkyrimMajorRecordFlags=3072"],
                               ["SkyrimMajorRecordFlags=1024"], ["SkyrimMajorRecordFlags=1024"], before=BASE))
        p_ch.append((D4, "PlacedObject", [(BASE, b4_), (MOD_A, a4_), (MOD_B, b4_)]))
        w_ch.append((D4, "PlacedObject", [(BASE, b4_), (MOD_A, a4_), (MOD_B, b4_), (PATCH, b4_)]))
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]})
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        return (re.search(r"\b2 drawn though the losing mod switches them off\b", kl) is not None
                and re.search(r"\b1 drawn at the patch's own spot though the losing mod switches them off", kl) is not None
                and re.search(r"\b1 in the base, so the object is the winning mod's", kl) is not None), kl[:500]

    for name, fn in [("fifth-review/C1077-kept-rows-meaning", c1077_kept_rows_meaning),
                     ("fifth-review/C1078-own-removal-at-the-top", c1078_own_removal_at_the_top),
                     ("fifth-review/C1074-kept-own-removal-said-winners", c1074_kept_own_removal_said_winners),
                     ("fifth-review/C1075-left-own-removal-patch-keeps-same", c1075_left_own_removal_patch_keeps_same),
                     ("fifth-review/C1076-inherited-removal-not-winners-own", c1076_inherited_removal_not_winners_own)]:
        cell(name, fn)

    # ------------------------------------------------ after the sixth review (C1101-C1139)
    # C1101-C1102 (BPT r6 S1): ModA has the object on and moved; ModB, the winner, removes it by its own change, and no
    # patch touches the record. In game the object is not drawn while ModB's removal stands, so ModA's lost change has no
    # effect there: nothing to patch. It is not a clash left with the patch: not "left", not in the still-there line, not
    # in "still present", said with the winner's own removal and "nothing to patch" (C1101). Where the winner only
    # carries an earlier mod's removal (C1102, guard), the losing mod may be undoing that removal on purpose: still left.
    def c1101_own_removal_stands_nothing_to_patch():
        D = f"00CA01:{BASE}"
        kl, st, plan, md = own_removal_bundle([], [D])
        w = [e.get("withPatch") for e in rec_of(plan, D)]
        part = next((x for x in re.split(r";\s+(?![^()]*\))", st) if "record field" in x), "")
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        left_ = summ.split(" left ", 1)[1].split(", and new")[0] if " left " in summ else ""
        open_ = next((ln for ln in md.splitlines() if ln.startswith("- Predicted and still present")), "")
        ok = (bool(w) and w != ["left"] and "nothing to patch" in kl and re.search(r"\bleft 1\b", kl) is None
              and not part and "record field" not in left_ and "record field" not in open_)
        return ok, f"withPatch {w}; kind {kl[:300]!r}; stays {part[:160]!r}; summary left {left_[:120]!r}; open {open_[:120]!r}"

    def c1102_inherited_removal_stays_left():
        D = f"00CA02:{BASE}"
        kl, st, plan, md = own_removal_bundle([], [D], inherited=True)
        w = [e.get("withPatch") for e in rec_of(plan, D)]
        return (w == ["left"] and "nothing to patch" not in kl), f"withPatch {w}; kind {kl[:300]!r}"

    for name, fn in [("sixth-review/C1101-own-removal-stands-nothing-to-patch", c1101_own_removal_stands_nothing_to_patch),
                     ("sixth-review/C1102-inherited-removal-stays-left", c1102_inherited_removal_stays_left)]:
        cell(name, fn)

    # C1103-C1106, C1132 (Winterhold r6 S1, gate A condition R1): an object of another mod rests on a support the
    # evaluated mod removes (below the world, enabled opposite to the player, initially disabled). The patch keeps the
    # support removed (its own version moves it 16 u, still below the world) and does not touch the object, so with the
    # patch the object still has no support; the setup with the patch does not list the pair. Read from the plugin
    # files, the with-patch outcome follows the support's state there where the check's re-measure found no support
    # (Holds: never "the patch's placement", C1103) or did not measure (C1104: left, never resolved). Where the patch
    # moves the object itself far away, nothing is overridden (C1105, guard: resolved); where the re-measure found the
    # object supported (Gone), it stands as measured (C1132, guard R1: resolved). A comparison never counts as new an
    # object whose support is removed in the setup it is compared with too (C1106: not "introduced", not new).
    def refr_file(name, masters, refs, group="CELL"):
        """A plugin of placed references: refs [(form id, base id, (x, y, z), off)]; off: below the world is in the
        position, and an off reference is also enabled opposite to the player and initially disabled."""
        def sub(sig, data):
            return sig.encode() + struct.pack("<H", len(data)) + data
        hdr = sub("HEDR", struct.pack("<fII", 1.7, len(refs), 0x800)) + b"".join(
            sub("MAST", m.encode() + b"\x00") + sub("DATA", b"\x00" * 8) for m in masters)
        body = b""
        for fid, base_id, pos, off in refs:
            data = sub("NAME", struct.pack("<I", base_id)) + sub("DATA", struct.pack("<6f", *pos, 0.0, 0.0, 0.0))
            if off:
                data += sub("XESP", struct.pack("<IB3x", 0x00000014, 1))
            body += b"REFR" + struct.pack("<IIIIHH", len(data), 0x800 if off else 0, fid, 0, 44, 0) + data
        out = (b"TES4" + struct.pack("<IIIIHH", len(hdr), 0, 0, 0, 44, 0) + hdr
               + b"GRUP" + struct.pack("<I4sIIHH", 24 + len(body), group.encode(), 0, 0, 0, 0) + body)
        p_ = Path(tempfile.mkdtemp(prefix="r6-")) / name
        p_.write_bytes(out)
        return p_

    def support_bundle(patch_moves_object=False, created=False, pred="Gone"):
        S, X = f"00CB04:{BASE}", f"000F04:{OTHER}"
        arms_ = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B], kind="baseline"),
                 arm("with-patch", [BASE, OTHER, MOD_A, MOD_B, PATCH])]
        p_refs = [(0x0000CB04, 0x00000B04, (100.0, 116.0, -30000.0), True)]
        if patch_moves_object:
            p_refs.append((0x01000F04, 0x00000B05, (2100.0, 2100.0, 1500.0), False))
        fs_ = {BASE: refr_file(BASE, [], [(0x0000CB04, 0x00000B04, (100.0, 100.0, 0.0), False)]),
               OTHER: refr_file(OTHER, [BASE], [(0x01000F04, 0x00000B05, (110.0, 100.0, 64.0), False)]),
               MOD_A: refr_file(MOD_A, [BASE], [(0x0000CB04, 0x00000B04, (100.0, 100.0, -30000.0), True)]),
               MOD_B: refr_file(MOD_B, [BASE], []),
               PATCH: refr_file(PATCH, [BASE, OTHER], p_refs)}
        with_files(arms_, fs_)
        names_ = {"refs": {X: {"base": f"000B05:{BASE}", "carriedBy": [OTHER] + ([PATCH] if patch_moves_object else [])},
                           S: {"base": f"000B04:{BASE}", "carriedBy": [BASE, MOD_A, PATCH]}},
                  "bases": {f"000B05:{BASE}": {"type": "Furniture", "editorId": "WoodPile", "model": "woodpickup.nif"},
                            f"000B04:{BASE}": {"type": "Static", "editorId": "FloorPiece", "model": "floor.nif"}}}
        r_ = row("CONTACT", OTHER, MOD_A, X, label="Longhouse (int)", other=S)
        if created:
            order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                         created=1, newDefect=1, attributed=0, unmeasured=0)}}
            log = f"NEW     CONTACT    {OTHER}  x  {MOD_A}   +1 record(s): created 1\n"
            return compile_and_render(arms_, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): [r_]},
                                      [MOD_A, MOD_B], patches=[PATCH], order=order, names=names_,
                                      diff_logs={(CONTACT, "with-patch"): log}), X
        preds = ({(CONTACT, "with-patch"): [{"Kind": "CONTACT", "A": OTHER, "B": MOD_A, "FormKey": X, "Other": S,
                                             "Physical": pred}]} if pred else None)
        return compile_and_render(arms_, {(CONTACT, "baseline"): [r_], (CONTACT, "with-patch"): []},
                                  [MOD_A, MOD_B], patches=[PATCH], names=names_, predicates=preds), X

    def c1103_support_removed_not_patch_placement():
        (plan, md, _), X = support_bundle(pred="Holds")
        w = [e.get("withPatch") for e in rec_of(plan, X)]
        return w == ["left"], f"withPatch {w}"

    def c1104_support_still_removed_with_patch_left():
        (plan, md, _), X = support_bundle(pred=None)
        w = [e.get("withPatch") for e in rec_of(plan, X)]
        kl = [x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or [] if "resolved" in x or "left" in x]
        return w == ["left"], f"withPatch {w}; {kl[:2]}"

    def c1105_object_moved_by_patch_stays_resolved():
        (plan, md, _), X = support_bundle(patch_moves_object=True, pred=None)
        w = [e.get("withPatch") for e in rec_of(plan, X)]
        return w == ["resolved"], f"withPatch {w}"

    def c1132_remeasure_found_support_stands():
        (plan, md, _), X = support_bundle(pred="Gone")
        w = [e.get("withPatch") for e in rec_of(plan, X)]
        return w == ["resolved"], f"withPatch {w}"

    def c1106_created_with_support_removed_before_not_new():
        (plan, md, _), X = support_bundle(created=True)
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression"), {})
        c = reg.get("counts") or {}
        val = reader(plan).get("validation") or {}
        return (c.get("readerCreated") == 0 and not val.get("patchProblems")), (
            f"comparison created {c.get('readerCreated')}; new with the patch {val.get('patchProblems')}; "
            f"{str(reg.get('readerPlain'))[:200]!r}")

    for name, fn in [("sixth-review/C1103-support-removed-not-patch-placement", c1103_support_removed_not_patch_placement),
                     ("sixth-review/C1104-support-still-removed-with-patch-left", c1104_support_still_removed_with_patch_left),
                     ("sixth-review/C1132-remeasure-found-support-stands", c1132_remeasure_found_support_stands),
                     ("sixth-review/C1105-object-moved-by-patch-stays-resolved", c1105_object_moved_by_patch_stays_resolved),
                     ("sixth-review/C1106-created-with-support-removed-before-not-new",
                      c1106_created_with_support_removed_before_not_new)]:
        cell(name, fn)

    # C1107-C1109 (Clockwork r6 S1): ModA's navmesh 0C3907 replaces ModB's, which links three doors of ModB's own (and
    # the base game's door the base navmesh links). ModA's version links only the base game's door. Read from the plugin
    # files: ModB's doors 000D01 and 000D02 have no door link in any navmesh of the setup; 000D03 is linked by ModB's
    # navmesh 0C3908 (C1108: not said lost). The patch's version links 000D01 again, so with the patch 000D02 alone is
    # still without one (C1107). The plugin reader reads a navmesh's door triangles (C1109).
    N_D1, N_D2 = 0x00000C3907, 0x00000C3908

    def nvnm_d(n_tris, doors):
        """A navmesh's geometry with door triangles [(triangle before the door, door FormID)] and no edge links."""
        return (struct.pack("<IIII", 12, 0xA5E9A03C, 0x3C, 0) + struct.pack("<I", 0) + struct.pack("<I", n_tris)
                + b"\x00" * 16 * n_tris + struct.pack("<I", 0) + struct.pack("<I", len(doors))
                + b"".join(struct.pack("<hII", t_, 0, d_) for t_, d_ in doors))

    def door_files():
        # (gate A condition R2) ModB's three doors lead somewhere (a teleport, XTEL); its fourth, 000D04, does not
        door_refs = [("REFR", 0x01000D01 + i_, [("NAME", struct.pack("<I", 0x00000B10)),
                                                 ("DATA", struct.pack("<6f", 100.0 * i_, 0.0, 0.0, 0.0, 0.0, 0.0))]
                      + ([("XTEL", struct.pack("<I", 0x00000C3A10) + b"\x00" * 28)] if i_ < 3 else []), False)
                     for i_ in range(4)]
        return {BASE: plugin_file_of(BASE, [], [("NAVM", N_D1, [("NVNM", nvnm_d(8, [(5, 0x00000C3A00)]))], False),
                                                ("NAVM", N_D2, [("NVNM", nvnm_d(8, []))], False)]),
                OTHER: plugin_file_of(OTHER, [BASE], []),
                MOD_B: plugin_file_of(MOD_B, [BASE], [
                    ("NAVM", N_D1, [("NVNM", nvnm_d(8, [(5, 0x00000C3A00), (6, 0x01000D01), (7, 0x01000D02),
                                                        (2, 0x01000D03), (1, 0x01000D04)]))], True),
                    ("NAVM", N_D2, [("NVNM", nvnm_d(8, [(3, 0x01000D03)]))], False)] + door_refs),
                MOD_A: plugin_file_of(MOD_A, [BASE], [("NAVM", N_D1, [("NVNM", nvnm_d(9, [(5, 0x00000C3A00)]))], False)]),
                PATCH: plugin_file_of(PATCH, [BASE, MOD_B], [
                    ("NAVM", N_D1, [("NVNM", nvnm_d(9, [(5, 0x00000C3A00), (6, 0x01000D01)]))], False)])}

    def door_bundle():
        N1 = f"0C3907:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        arms_ = with_files([arm("baseline", [BASE, OTHER, MOD_B, MOD_A, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, OTHER, MOD_B, MOD_A])], door_files())
        measured = {(NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}
        plan, md, _ = cw_bundle([r_], extra_results=measured, arms=arms_)
        sec = md.split("## What the checks could not judge")[0]
        return plan, sec, str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")

    def c1107_lost_door_links_said_with_patch():
        plan, sec, aside = door_bundle()
        d1, d2 = f"000D01:{MOD_B}", f"000D02:{MOD_B}"
        ok = ("door link" in sec and d1 in sec and d2 in sec
              and re.search(r"\b1 still ha(?:s|ve) no[^.]*" + re.escape(d2), sec) is not None
              and "not checked by any check here" not in aside)
        ln = next((x for x in sec.splitlines() if "door" in x), "")
        return ok, f"{ln[:400]!r} || aside {aside[-200:]!r}"

    def c1108_door_linked_elsewhere_not_lost():
        plan, sec, aside = door_bundle()
        return (f"000D03:{MOD_B}" not in sec and f"0C3A00:{BASE}" not in sec and f"000D04:{MOD_B}" not in sec), sec[-300:]

    def c1109_pluginread_door_triangles():
        import pluginread
        f_ = door_files()[MOD_B]
        got = pluginread.navmesh_links(f_, [f"0C3907:{BASE}", f"0C3908:{BASE}"])["records"]
        want1 = [[f"0C3A00:{BASE}", 5], [f"000D01:{MOD_B}", 6], [f"000D02:{MOD_B}", 7],
                 [f"000D03:{MOD_B}", 2], [f"000D04:{MOD_B}", 1]]
        ok = ((got.get(f"0C3907:{BASE}") or {}).get("doors") == want1
              and (got.get(f"0C3908:{BASE}") or {}).get("doors") == [[f"000D03:{MOD_B}", 3]]
              and (got.get(f"0C3907:{BASE}") or {}).get("triangles") == 8)
        return ok, f"{got}"

    for name, fn in [("sixth-review/C1107-lost-door-links-said-with-patch", c1107_lost_door_links_said_with_patch),
                     ("sixth-review/C1108-door-linked-elsewhere-not-lost", c1108_door_linked_elsewhere_not_lost),
                     ("sixth-review/C1109-pluginread-door-triangles", c1109_pluginread_door_triangles)]:
        cell(name, fn)

    # ------------------------------------------------ sixth review, later items (C1110-C1131)
    # C1110 (Pilgrim r6 S1, wording): the sentence that a crossing may come from the other mod's own placement names the
    # partners it moved across their size, turned or rebased, never a partner it only nudged.
    def c1110_own_placement_sentence_names_its_partners():
        near_ = (0x00CA10, (100.0, 100.0, 0.0, 1.5, 0x0B0A79), (102.0, 100.0, 0.0, 1.5, 0x0B0A79))
        far_ = (0x00CB10, (500.0, 100.0, 0.0, 1.5, 0x0B0A79), (800.0, 100.0, 0.0, 2.0, 0x0B0A79))
        md, it = nudge_bundle([near_, far_])
        # (seventh review, Pilgrim S2) the far partner (300 u, beyond its size) is now credited to the mod's own
        # placement, in its own sentence; the nudged one is in neither
        s_ = " ".join(x for x in re.split(r"(?<=\.)\s+", it) if "may come from its own placement" in x
                      or "own placement, not the base game's" in x)
        return ("00CB10:Skyrim.esm" in s_ and "00CA10:Skyrim.esm" not in s_), s_[:300]

    # C1114 (BPT r5 S1): a patch that takes the losing mod's value over the winner's change (switched) is worded apart
    # from a patch carrying the winner's value (kept): "restores", never the kept wording.
    def c1114_switched_worded_apart_from_kept():
        R2_ = f"00C214:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R2_, "Placement", "o2", "a2", "v2")]
        w_rows = [lost_row(MOD_A, PATCH, R2_, "Placement", "a2", "o2", "o2")]
        chains = [(R2_, "PlacedObject", [(BASE, {"Placement": "v2"}), (OTHER, {"Placement": "o2"}),
                                         (MOD_A, {"Placement": "a2"})])]
        plan, md, html = core_bundle(p_rows, w_rows, p_chains=chains, masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or [] if x.startswith("Record fields")), "")
        return ([e.get("withPatch") for e in rec_of(plan, R2_)] == ["switched"] and "restores" in kl
                and "keeps OtherMod.esp's value" not in kl), kl[:300]

    # C1120 (Snazzy S2, Missives line 157): a comparison's still-holding sentence gives the reason the prediction
    # records: a patch places one of the two objects (its patch-placed pairs account for every one), never "one plugin
    # now places both".
    def c1120_still_holding_reason_patch_places_one():
        X = f"000X20:{MOD_A}"
        ys = [f"000Y2{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": p_}
                 for y, p_ in zip(ys, ("Gone", "Holds"))]
        refs = {ys[1]: {"carriedBy": [MOD_B, PATCH]}, ys[0]: {"carriedBy": [MOD_B]}, X: {"carriedBy": [MOD_A]}}
        order = {"with-patch": {"overlapprobe": dict(verdict="DIFFERENTIAL-VERIFIED", repaired=1, reclassified=1, created=0,
                                                     newDefect=0, attributed=0, unmeasured=0)}}
        log = ("   reclassified : 1 record(s)   (left the class still holding 1; left with no physical defect to begin with 0;"
               " entered reading Gone 0)\n")
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                            names={"refs": refs}, order=order, diff_logs={(OVERLAP, "with-patch"): log})
        ln = next((x for x in md.splitlines() if "still physically there" in x), "")
        return ("a patch now places one of the two objects" in ln and "one plugin now places both" not in ln), ln[-300:]

    # C1123 (Sewers r5 S1): a further evaluation's other version of an evaluated plugin is named by the folder its plan
    # loads it from.
    def c1123_variant_named_by_folder():
        X1, T_ = f"000X23:{MOD_A}", f"000T23:{OTHER}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, X1, other=T_)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-other", [BASE, MOD_A])]
        v_arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline", shas={MOD_A: "b" * 64})]
        for q_ in v_arms[0]["requested"]["plugins"]:
            if q_["name"] == MOD_A:
                q_["path"] = f"X:/mods/ModA compatible variant/{MOD_A}"
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): list(rows_), (OVERLAP, "without-patch"): rows_,
                                                (OVERLAP, "without-other"): []}, [MOD_A], patches=[PATCH],
                                         also=[(v_arms, {(OVERLAP, "baseline"): []}, [MOD_A], {})])
        also_ = " ".join((reader(plan).get("prediction") or {}).get("alsoEvaluations") or [])
        return "ModA compatible variant" in also_, also_[-300:]

    # C1126 (Sewers r6 S4): a by-object comparison's repairs say their unit: the object and its pairs.
    def c1126_repairs_name_objects_and_pairs():
        G0 = f"000G26:{MOD_A}"
        base = [row("CONTACT", MOD_B, MOD_A, G0, other=f"000H{i}6:{MOD_B}") for i in (1, 2)]
        preds = [{"Kind": "CONTACT", "A": MOD_B, "B": MOD_A, "FormKey": G0, "Other": f"000H{i}6:{MOD_B}", "Physical": "Gone"}
                 for i in (1, 2)]
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-VERIFIED", repaired=2, reclassified=0,
                                                     created=0, newDefect=0, attributed=0, unmeasured=0)}}
        log = f"SHRANK  CONTACT    {MOD_B}  x  {MOD_A}   -2 record(s): repaired 2\n"
        refs = {G0: {"base": f"0B0S26:{MOD_A}"}, **{f"000H{i}6:{MOD_B}": {"base": f"0B0W26:{MOD_B}"} for i in (1, 2)}}
        bases = {f"0B0S26:{MOD_A}": {"type": "Static", "model": "lantern.nif"}, f"0B0W26:{MOD_B}": {"type": "Static", "model": "table.nif"}}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): base, (CONTACT, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH], order=order, predicates={(CONTACT, "with-patch"): preds},
                                            names={"refs": refs, "bases": bases}, diff_logs={(CONTACT, "with-patch"): log})
        ln = next((x for x in md.splitlines() if "repaired 1 existing" in x), "")
        return re.search(r"repaired 1 existing problem \(1 object, 2 object-and-support pairs\)", ln) is not None, ln[-300:]

    # C1129 (Missives r5 S3 / r6 S1): the narrowing note names the record type it covers, so the cells' MaxHeightData
    # is not taken for the worldspace field a report counts.
    def c1129_narrowing_names_record_type():
        pr_ = priorities()
        rows_ = [{"checkId": RECORDS, "tool": {"name": "ordered housecarl_records calls (CELL re-read narrowed to its compared "
                                                         "fields without MaxHeightData (c03; N11), WRLD narrowed without "
                                                         "OffsetData), composed by a script"}}]
        facts = R.import_narrowed_facts(rows_, pr_, lambda c_: c_)
        j_ = " | ".join(facts)
        return ("cell records' MaxHeightData" in j_ and "worldspace records' OffsetData" in j_), j_[:400]

    for name, fn in [("sixth-review/C1110-own-placement-sentence-names-its-partners", c1110_own_placement_sentence_names_its_partners),
                     ("sixth-review/C1114-switched-worded-apart-from-kept", c1114_switched_worded_apart_from_kept),
                     ("sixth-review/C1120-still-holding-reason-patch-places-one", c1120_still_holding_reason_patch_places_one),
                     ("sixth-review/C1123-variant-named-by-folder", c1123_variant_named_by_folder),
                     ("sixth-review/C1126-repairs-name-objects-and-pairs", c1126_repairs_name_objects_and_pairs),
                     ("sixth-review/C1129-narrowing-names-record-type", c1129_narrowing_names_record_type)]:
        cell(name, fn)

    # C1111 (Pilgrim r6 S2, BPT r5/r6 S2): the totals a modder sizes the job from say how many overlap pairs have an
    # object whose model is a visual effect: the headline, the kind line's total and its left count, and the summary.
    def c1111_effect_share_in_totals():
        X, Y, Z = f"000X11:{MOD_A}", f"000Y11:{MOD_B}", f"000Z11:{MOD_B}"
        r1, r2 = row("OVERLAP", MOD_B, MOD_A, X, other=Y), row("OVERLAP", MOD_B, MOD_A, X, other=Z)
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        names_ = {"refs": {X: {"base": f"0B0X11:{MOD_A}"}, Y: {"base": f"0B0Y11:{MOD_B}"}, Z: {"base": f"0B0Z11:{MOD_B}"}},
                  "bases": {f"0B0X11:{MOD_A}": {"type": "Static", "editorId": "Wall", "model": "arch\\wall.nif"},
                            f"0B0Y11:{MOD_B}": {"type": "MoveableStatic", "editorId": "Mist", "model": "effects\\fxmist.nif"},
                            f"0B0Z11:{MOD_B}": {"type": "Static", "editorId": "Rock", "model": "rocks\\rock.nif"}}}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [r1, r2], (OVERLAP, "without-patch"): [r1, r2],
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH], names=names_)
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Objects occupying")), "")
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        v = str(pr_.get("verdict") or "")
        return ("1 of the pairs with a visual-effect model" in v and kl.count("1 with a visual-effect model") == 2
                and "1 with a visual-effect model" in summ), f"{v[-200:]} || {kl} || {summ[-200:]}"

    # C1112, C1113 (Pilgrim r5 S1): where the winner's value is the base game's on some of an item's rows only, the item
    # says so with its count (it puts back what the losing mod changed); and an item whose rows are all one field with a
    # meaning said ("All of them are the position: ...") is not listed as one whose in-game effect is not described.
    def base_some_bundle():
        Rs = [f"00CA1{i}:{BASE}" for i in (2, 3, 4)]
        rows_, ch_ = [], []
        for i_, R in enumerate(Rs):
            a_, b_ = offs(0), offs(0)
            a_["Placement.Position"] = f"{300 + i_},100,0"
            if i_ == 2:
                b_["Placement.Position"] = "900,100,0"
            rows_.append(lost_row(MOD_A, MOD_B, R, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE))
            ch_.append((R, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)]))
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        return plan, md

    def c1112_winner_base_on_some_rows_counted():
        plan, md = base_some_bundle()
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return re.search(r"In 2 of them ModB\.esp's value is the base game's", it) is not None, it[-500:]

    def c1113_field_meaning_not_listed_undescribed():
        plan, md = base_some_bundle()
        bad = [ln for ln in md.splitlines() if "show in game is not described" in ln and "Record fields" in ln]
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("of them are the position" in it and not bad
                and "Its in-game effect is not described" not in it), f"{bad} || {it[-200:]}"

    # C1115 (BPT r5 S3): an overlap pair the probe reports as coplanar is said apart, like a touching pair, and the
    # "drawn through each other" line speaks only of the other pairs.
    def c1115_coplanar_pairs_said_apart():
        X, Y, Z = f"000X15:{MOD_A}", f"000Y15:{MOD_B}", f"000Z15:{MOD_B}"
        r1 = row("OVERLAP", MOD_B, MOD_A, X, other=Y)
        r1["Summary"] = f"Static from {MOD_A} / Static from {MOD_B}: coplanar_overlap, containment unassessed, 40 triangle pairs"
        r2 = row("OVERLAP", MOD_B, MOD_A, X, other=Z)
        r2["Summary"] = f"Static from {MOD_A} / Static from {MOD_B}: crossing, containment unassessed, 12 triangle pairs"
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r1, r2],
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH])
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        return ("1 of these pairs are coplanar" in it or "1 of these pairs is coplanar" in it) and "for the other 1 overlap pair" in it, it[-400:]

    for name, fn in [("sixth-review/C1111-effect-share-in-totals", c1111_effect_share_in_totals),
                     ("sixth-review/C1112-winner-base-on-some-rows-counted", c1112_winner_base_on_some_rows_counted),
                     ("sixth-review/C1113-field-meaning-not-listed-undescribed", c1113_field_meaning_not_listed_undescribed),
                     ("sixth-review/C1115-coplanar-pairs-said-apart", c1115_coplanar_pairs_said_apart)]:
        cell(name, fn)

    # C1118 (Winterhold r6 S2): an object the losing value switches off and the winner has on, whose base is a Static
    # carrying the editor-marker record flag (read from the base's plugin file), is not counted as drawn and never its
    # example; it is said apart as a marker the game does not draw. A Static without the flag stays drawn (guard part).
    def c1118_marker_base_not_drawn():
        import copy
        M, D = f"00CA18:{BASE}", f"00CB18:{BASE}"
        def stat_file():
            def sub(sig, data):
                return sig.encode() + struct.pack("<H", len(data)) + data
            hdr = sub("HEDR", struct.pack("<fII", 1.7, 2, 0x800))
            body = b""
            for fid, flags in ((0x00000B18, 0x00800000), (0x00000B19, 0)):
                data = sub("EDID", b"Thing" + bytes(1))
                body += b"STAT" + struct.pack("<IIIIHH", len(data), flags, fid, 0, 44, 0) + data
            out = (b"TES4" + struct.pack("<IIIIHH", len(hdr), 0, 0, 0, 44, 0) + hdr
                   + b"GRUP" + struct.pack("<I4sIIHH", 24 + len(body), b"STAT", 0, 0, 0, 0) + body)
            p_ = Path(tempfile.mkdtemp(prefix="r6m-")) / BASE
            p_.write_bytes(out)
            return p_
        rows_, ch_ = [], []
        for fk_ in (M, D):
            lost_, win_ = offs(-30000), offs(0)
            rows_.append(lost_row(MOD_B, MOD_A, fk_, "Placement", off_txt(lost_), off_txt(win_), off_txt(offs(0)), before=BASE))
            ch_.append((fk_, "PlacedObject", [(BASE, offs(0)), (MOD_B, lost_), (MOD_A, win_)]))
        names_ = {"refs": {M: {"base": f"000B18:{BASE}"}, D: {"base": f"000B19:{BASE}"}},
                  "bases": {f"000B18:{BASE}": {"type": "Static", "editorId": "XMarker", "model": "MarkerX.nif"},
                            f"000B19:{BASE}": {"type": "Static", "editorId": "Crate01", "model": "clutter\\crate01.nif"}}}
        arms_ = with_files(copy.deepcopy(WP_ARMS), {BASE: stat_file()})
        plan, md, _ = core_bundle(rows_, list(rows_), p_chains=ch_, w_chains=ch_, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B), names=names_, arms=arms_)
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        drawn_ = next((x for x in re.split(r"(?<=[.)])\s+(?=On |In |For )", it) if "is drawn" in x), "")
        return ("00CB18" in drawn_ and "00CA18" not in drawn_ and "editor marker" in it
                and re.search(r"On 1 object, ", drawn_) is not None), it[-500:]

    # C1119 (Snazzy S1, gate A condition R3): a lost water height on an interior cell whose losing and winning versions
    # both lack the HasWater flag (read from the plugin files) has no water in game: set aside, not counted. A cell whose
    # winning version has the flag stays counted (guard part).
    def c1119_water_height_without_water_set_aside():
        import copy
        C1, C2 = f"00C119:{BASE}", f"00C219:{BASE}"
        fs_ = {OTHER: plugin_file_of(OTHER, [BASE], [("CELL", 0x0000C119, [("DATA", struct.pack("<H", 0x0081))], False),
                                                     ("CELL", 0x0000C219, [("DATA", struct.pack("<H", 0x0083))], False)], group="CELL"),
               MOD_A: plugin_file_of(MOD_A, [BASE], [("CELL", 0x0000C119, [("DATA", struct.pack("<H", 0x0081))], False),
                                                     ("CELL", 0x0000C219, [("DATA", struct.pack("<H", 0x0083))], False)], group="CELL")}
        rows_ = []
        for C, lab_ in ((C1, "DryHall"), (C2, "WetHall")):
            r_ = lost_row(OTHER, MOD_A, C, "WaterHeight", ["WaterHeight=0"], PH, PH, label=lab_)
            r_["Summary"] = f"Cell {lab_}: WaterHeight - {OTHER}'s value lost to {MOD_A}'s"
            rows_.append(r_)
        arms_ = with_files(copy.deepcopy(WP_ARMS), fs_)
        plan, md, _ = core_bundle(rows_, rows_, check=CELLATTR, masters=MASTERS_, arms=arms_)
        a1, a2 = [e["admission"] for e in rec_of(plan, C1)], [e["admission"] for e in rec_of(plan, C2)]
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        return (a1 == ["noWater"] and a2 == ["admitted"] and "HasWater" in aside), f"{a1} {a2}; {aside[-300:]}"

    for name, fn in [("sixth-review/C1118-marker-base-not-drawn", c1118_marker_base_not_drawn),
                     ("sixth-review/C1119-water-height-without-water-set-aside", c1119_water_height_without_water_set_aside)]:
        cell(name, fn)

    # C1117 (BPT r6 S4): a light set aside as having no body in the support check (its support moved or removed by the
    # other mod) is named in the set-aside sentence, with what a light does there: it still lights that spot.
    def c1117_nobody_lights_named():
        X, S = f"000X17:{MOD_A}", f"000S17:{MOD_B}"
        rows_ = [row("CONTACT", MOD_B, MOD_A, X, other=S)]
        names_ = {"refs": {X: {"base": f"0B0X17:{MOD_A}"}, S: {"base": f"0B0S17:{MOD_B}"}},
                  "bases": {f"0B0X17:{MOD_A}": {"type": "Light", "editorId": "LampGlowNS", "model": ""},
                            f"0B0S17:{MOD_B}": {"type": "Static", "editorId": "Table", "model": "table.nif"}}}
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): rows_,
                                                (CONTACT, "without-b"): []}, [MOD_A], patches=[PATCH], names=names_)
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        return (X in aside and "LampGlowNS" in aside and "still lights" in aside), aside[-300:]

    for name, fn in [("sixth-review/C1117-nobody-lights-named", c1117_nobody_lights_named)]:
        cell(name, fn)

    # C1121 (Sewers r6 S1): where the losing version is a single-triangle stub (the losing mod's removal) and the winning
    # version has the base game's triangle count (600), the line says the winner puts back the base game's navmesh where
    # the losing mod removed it, and counts it among the base-game-sized ones.
    def c1121_stub_put_back_at_base_size():
        n1 = f"000E21:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="TempleInterior (int)  1 tris -> 600")]
        p_rows[0]["Necessary"][0]["Detail"] = "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u"
        arms_ = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E21, [("NVNM", nvnm(600, []))], False)]),
               MOD_A: plugin_file_of(MOD_A, [BASE], [("NAVM", 0x00000E21, [("NVNM", nvnm(1, []))], False)]),
               MOD_B: plugin_file_of(MOD_B, [BASE], [("NAVM", 0x00000E21, [("NVNM", nvnm(600, []))], False)])}
        with_files(arms_, fs_)
        plan, md, html = compile_and_render(arms_, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH])
        top = " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])
        return ("puts back" in top and "000E21" in top and "600 triangles" in top and "removed" in top), top[-500:]

    for name, fn in [("sixth-review/C1121-stub-put-back-at-base-size", c1121_stub_put_back_at_base_size)]:
        cell(name, fn)

    # C1122 (Sewers r6 S2): a partner whose reference the names enrichment did not read takes its base from its own
    # plugin file (the reference's NAME), judged by that base's record in the enrichment (a door with a model): it has a
    # body, so the row is counted, not set aside as "base not read".
    def c1122_unread_reference_base_from_plugin():
        import copy
        X, Y = f"000X22:{MOD_A}", f"000F22:{MOD_B}"
        rows_ = [row("OVERLAP", MOD_B, MOD_A, X, other=Y)]
        fy_ = plugin_file_of(MOD_B, [BASE], [("REFR", 0x01000F22, [("NAME", struct.pack("<I", 0x01000D62)),
                                                                   ("DATA", struct.pack("<6f", 1.0, 2.0, 3.0, 0.0, 0.0, 0.0))],
                                              False)], group="CELL")
        names_ = {"refs": {X: {"base": f"0B0X22:{MOD_A}"}, f"000F23:{MOD_B}": {"base": f"000D62:{MOD_B}"}},
                  "bases": {f"0B0X22:{MOD_A}": {"type": "Static", "editorId": "Wall", "model": "wall.nif"},
                            f"000D62:{MOD_B}": {"type": "Door", "editorId": "Manhole", "model": "door02.nif"}}}
        arms = with_files([arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, MOD_A, MOD_B]), arm("without-b", [BASE, MOD_A])], {MOD_B: fy_})
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): rows_,
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH], names=names_)
        adm = [e["admission"] for e in rec_of(plan, X)]
        return adm == ["admitted"], f"{adm}"

    for name, fn in [("sixth-review/C1122-unread-reference-base-from-plugin", c1122_unread_reference_base_from_plugin)]:
        cell(name, fn)

    # C1124 (Sewers r5 S2 / r6 S3): a check that failed in this evaluation, whose rows this report takes from the
    # separate evaluation without the patch though none is counted, says so where its failure is listed; the base-game
    # line says its clash comes from there, and names a Creation Club plugin's object with its plugin.
    def c1124_failed_check_rows_from_sibling_said():
        CC = "ccTestPack.esl"
        Xs, Ys = f"000X24:{MOD_A}", f"000Y24:{CC}"
        st_ = row("STATE-CONTACT@000S24:ModA.esp@Triggered", CC, MOD_A, Xs, label="Origin (ext)", other=Ys)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_A}): {Xs} [Activator CritterMoth] arrives - "
                          f"Sunk: sits 9u inside something")
        arms = [arm("baseline", [BASE, CC, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, CC, MOD_A])]
        s_arms = [arm("baseline", [BASE, CC, MOD_A], kind="baseline"), arm("plus-a", [BASE, CC, MOD_A]),
                  arm("dependency-only", [BASE, CC])]
        s_results = {(STATE, "baseline"): [st_], (STATE, "plus-a"): [st_], (STATE, "dependency-only"): [],
                     (CONTACT, "baseline"): [], (CONTACT, "plus-a"): [], (CONTACT, "dependency-only"): []}
        plan, md, html = compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}, [MOD_A],
                                            patches=[PATCH], missing=[(STATE, "without-patch", "failed"), (STATE, "baseline", "failed")],
                                            sibling=(s_arms, s_results, [MOD_A], {"plan_id": "sibl00001124"}))
        adm = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or [] if e.get("source") == "sibling"]
        miss = [ln for ln in md.splitlines() if "No result" in ln]
        bg = next((ln for ln in md.splitlines() if "against base-game objects" in ln), "")
        return (any("come from the separate evaluation" in ln for ln in miss)
                and "from the separate evaluation" in bg and f"of {CC}" in bg), f"{adm}; {miss}; {bg[:300]}"

    for name, fn in [("sixth-review/C1124-failed-check-rows-from-sibling-said", c1124_failed_check_rows_from_sibling_said)]:
        cell(name, fn)

    # C1125 (Sewers r5 S3): the evaluated mod's broken links of base-game navmeshes are said as one figure: the ones on
    # the base-game line (navmeshes no other mod edits) and those whose linking navmesh another mod carries, with how
    # many the setup with the patch still has.
    def c1125_base_links_one_figure():
        rows_ = []
        for t_ in (501, 502):
            r_ = row("EDGE-IDX", OTHER, MOD_A, N_LINK, label="CityExterior04 (ext)", other=N_TGT)
            r_["Summary"] = (f"navmesh {N_LINK} in CityExterior04 (ext) edge-links {N_TGT} triangle {t_} of 432   "
                             f"[record provided by: {BASE} -> {OTHER}]")
            rows_.append(r_)
        N2 = f"000E42:{BASE}"
        r3 = row("EDGE-IDX", BASE, MOD_A, N2, label="CityExterior05 (ext)", other=N_TGT)
        r3["Summary"] = (f"navmesh {N2} in CityExterior05 (ext) edge-links {N_TGT} triangle 503 of 432   "
                         f"[record provided by: {BASE}]")
        rows_.append(r3)
        links_ = [(0x00000D43, 501), (0x00000D43, 502)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-a", [BASE, OTHER])]
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_))], True),
                                               ("NAVM", 0x00000E42, [("NVNM", nvnm(4, [(0x00000D43, 503)]))], False),
                                               ("NAVM", 0x00000D43, [("NVNM", nvnm(600, []))], False)]),
               OTHER: plugin_file_of(OTHER, [BASE], [("NAVM", 0x00000E41, [("NVNM", nvnm(4, links_))], False)])}
        with_files(arms, fs_)
        plan, md, _ = compile_and_render(arms, {(NAVOWN, "baseline"): rows_, (NAVOWN, "without-patch"): rows_,
                                                (NAVOWN, "without-a"): []}, [MOD_A], patches=[PATCH])
        adm = [e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []]
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        return (f"{MOD_A} breaks 3 links of base-game navmeshes" in aside and "3 of the 3 are still broken" in aside), f"{adm}; {aside[-400:]}"

    for name, fn in [("sixth-review/C1125-base-links-one-figure", c1125_base_links_one_figure)]:
        cell(name, fn)

    # C1127 (Clockwork r5 S1): an overlap pair on an object whose own record change by one of the two mods is lost to
    # the other (a counted record-field row between them) is linked to that item in the overlap item.
    def c1127_overlap_linked_to_lost_field():
        T, W = f"00CA27:{BASE}", f"000W27:{MOD_B}"
        ov_ = row("OVERLAP", MOD_B, MOD_A, T, other=W)
        a_, b_ = offs(0), offs(0)
        a_["Placement.Position"], b_["Placement.Position"] = "300,100,0", "700,100,0"
        lr_ = lost_row(MOD_B, MOD_A, T, "Placement", off_txt(b_), off_txt(a_), off_txt(offs(0)), before=BASE)
        arms = [arm("baseline", [BASE, MOD_B, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_B, MOD_A]),
                arm("without-b", [BASE, MOD_A])]
        ch_ = [(T, "PlacedObject", [(BASE, offs(0)), (MOD_B, b_), (MOD_A, a_)])]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [ov_],
                                                (OVERLAP, "without-b"): [], (CONTACT, "without-patch"): [],
                                                (CONTACT, "without-b"): []}, [MOD_A], patches=[PATCH],
                                         count_only={(RECORDS, "without-patch"): 1, (RECORDS, "without-b"): 1},
                                         enrichment={(RECORDS, "without-patch"): [lr_], (RECORDS, "without-b"): []},
                                         transcripts={(RECORDS, "without-patch"): records_transcript(ch_)},
                                         masters={MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        adm = [e["admission"] for e in rec_of(plan, T)]
        return re.search(r"\b1 of these pairs is on an object whose own record change between the two mods is lost", it) is not None, f"{adm}; {it[-400:]}"

    for name, fn in [("sixth-review/C1127-overlap-linked-to-lost-field", c1127_overlap_linked_to_lost_field)]:
        cell(name, fn)

    # C1128 (Clockwork r5 S2): where another plugin carries the master's value the mod undoes (its row set aside as no
    # edit of its own), the undo line names it, so the loss is not read as the master's alone.
    def c1128_undo_line_names_carrier():
        R = f"00CA28:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, MOD_A, MOD_B])]
        b0, u0, a0 = offs(0), offs(0), offs(0)
        u0["Placement.Position"] = "100,100,12"
        chains = [(R, "PlacedObject", [(BASE, b0), (UPD, u0), (OTHER, u0), (MOD_A, a0)])]
        p_rows = [lost_row(UPD, MOD_A, R, "Placement", ["Placement.Position=100,100,12"], PH, PH),
                  lost_row(OTHER, MOD_A, R, "Placement", ["Placement.Position=100,100,12"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE, UPD], MOD_A: [BASE, UPD], MOD_B: [BASE], PATCH: [BASE]})
        adm = {e["A"]: e["admission"] for e in rec_of(plan, R)}
        line = next((ln for ln in md.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        return (adm.get(UPD) == "undoesMaster" and f"{OTHER} carries {UPD}'s value" in line), f"{adm}; {line[:400]}"

    for name, fn in [("sixth-review/C1128-undo-line-names-carrier", c1128_undo_line_names_carrier)]:
        cell(name, fn)

    # C1116 (BPT r6 S3, Missives Tamriel owners): ModA's value is lost to OtherMod.esp's, which is ThirdMod.esp's own
    # change that OtherMod.esp only carries. The item names ThirdMod.esp as the value's own, and the patch-scope line
    # judges by ThirdMod.esp, which the patch lists as a master: nothing is said outside what it sets out to fix.
    def c1116_carried_winner_value_named_by_owner():
        R = f"00CA16:{BASE}"
        arms = [arm("baseline", [BASE, THIRD, MOD_A, OTHER, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, OTHER])]
        chains = [(R, "Worldspace", [(BASE, {"MaxHeight.CellData": "00"}), (THIRD, {"MaxHeight.CellData": "0B"}),
                                     (MOD_A, {"MaxHeight.CellData": "0A"}), (OTHER, {"MaxHeight.CellData": "0B"})])]
        p_rows = [lost_row(MOD_A, OTHER, R, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=0B"],
                           ["MaxHeight.CellData=00"])]
        p_rows[0]["Summary"] = f"Worldspace Tamriel: MaxHeight - {MOD_A}'s value lost to {OTHER}'s"
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={THIRD: [BASE], MOD_A: [BASE], OTHER: [BASE, THIRD], PATCH: [BASE, THIRD]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        scope = next((ln for ln in md.splitlines() if "no supplied patch edits" in ln), "")
        # (seventh review round 2) one owner, said in the same terms everywhere: "one ThirdMod.esp made first"
        return (f"{OTHER} only carries a value {THIRD} made first" in it and OTHER not in scope), f"{it[-300:]} || {scope}"

    for name, fn in [("sixth-review/C1116-carried-winner-value-named-by-owner", c1116_carried_winner_value_named_by_owner)]:
        cell(name, fn)

    # C1133 (Missives, Tamriel max height owners): an earlier loss the patch also fixes names whose own change the
    # losing value is: Neighbour.esp only carries OtherMod.esp's value, so the change is OtherMod.esp's alone.
    def c1133_earlier_fix_names_value_owner():
        ln = earlier_fix_bundle(True, o2_carries=True)
        return ("OtherMod.esp's change" in ln and "Neighbour.esp's" not in ln), ln[:500]

    for name, fn in [("sixth-review/C1133-earlier-fix-names-value-owner", c1133_earlier_fix_names_value_owner)]:
        cell(name, fn)

    # C1131 (Missives S5): a base-game plugin's water height a mod's value wins over, which is far outside any height
    # in the world (3.4E+38), is said with that, and that what it and the winning value do in game is not read here.
    def c1131_water_height_outside_world_said():
        W1 = f"00CA31:{BASE}"
        arms = [arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])]
        ch_ = [(BASE, {"WaterHeight": "0"}), (HF_, {"WaterHeight": "3.4028235E+38"}), (OTHER, {"WaterHeight": "3.4028235E+38"}),
               (MOD_A, {"WaterHeight": "-2147483648"})]
        p_rows = [lost_row(x_, MOD_A, W1, "WaterHeight", ["WaterHeight=3.4028235E+38"], PH, PH) for x_ in (HF_, OTHER)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=[(W1, "Cell", ch_)],
                                  masters={HF_: [BASE], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if f"over {HF_}'s own change" in ln or "far outside" in ln), "")
        return ("far outside any height in the world" in line and "not read here" in line), line[-400:]

    for name, fn in [("sixth-review/C1131-water-height-outside-world-said", c1131_water_height_outside_world_said)]:
        cell(name, fn)

    # C1130 (Missives r5 S2, gate A condition R4): ModA's list of a cell's regions drops an item the base game's version
    # holds; OtherMod.esp's version holds the base game's items in another order. The loss is ModA's own change against
    # the base game, said in a measured line naming the dropped item, never filed as OtherMod.esp's clash.
    def c1130_evaluated_list_drop_said_as_own():
        C = f"00CA30:{BASE}"
        d_base = f"Regions: 3 vs {MOD_A} 2 item(s) — only here: [0] 00AAAA:Skyrim.esm"
        d_oth = f"Regions: 3 vs {MOD_A} 2 item(s) — only here: [2] 00AAAA:Skyrim.esm"
        nodes = [{"plugin": BASE, "is_winner": False, "is_reference": False, "complete": True, "deltas": [d_base]},
                 {"plugin": OTHER, "is_winner": False, "is_reference": False, "complete": True, "deltas": [d_oth]},
                 {"plugin": MOD_A, "is_winner": True, "is_reference": True}]
        req = [{"batchMarker": "b1"}, {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
            "name": "housecarl_records", "arguments": {"formids": [C], "project": {"form": "tree"}}}}]
        resp = [{"batchMarker": "b1"}, {"jsonrpc": "2.0", "id": 1, "result": {"content": [{"type": "text", "text": json.dumps(
            {"rows": [{"formid": C, "type": "Cell", "editorid": None, "reference": MOD_A, "touchers": [BASE, OTHER, MOD_A],
                       "nodes": nodes}]})}]}}]
        r_ = lost_row(OTHER, MOD_A, C, "Regions", [d_oth], PH, [d_base], label="OriginExt")
        r_["Summary"] = f"Cell OriginExt: Regions - {OTHER}'s value lost to {MOD_A}'s"
        plan, md, _ = core_bundle([r_], [r_], check=CELLATTR, masters=MASTERS_, p_tr=(req, resp))
        adm = [e["admission"] for e in rec_of(plan, C)]
        line = next((ln for ln in md.splitlines() if "drops items the base game" in ln), "")
        return (adm == ["winnerListDrop"] and "00AAAA:Skyrim.esm" in line and f"{MOD_A}'s own change" in line), f"{adm}; {line[:400]}"

    for name, fn in [("sixth-review/C1130-evaluated-list-drop-said-as-own", c1130_evaluated_list_drop_said_as_own)]:
        cell(name, fn)

    # ------------------------------------------------ after the seventh review (C1140-C1189)
    # The attribution class: every sentence that names a plugin as the source of a value, a removal or a crossing asks
    # one read of the record's versions: whose own change the value (or the off-for-good state, or the placement) is,
    # and who only carries it.
    # C1148 (the read itself): recordrows.attribute over a provider chain. Provider i changed the field when its value
    # differs from the one it inherits from its nearest master; the value's origin is found by walking back through the
    # versions it inherits while the value is unchanged (the plugin that made the change it carries); `sameAs` lists the
    # earlier providers holding the same value as a change of their own (the same change made independently, earliest
    # first); the carriers are the later providers whose value's origin is that plugin.
    def c1148_attribution_read():
        import recordrows as RR_
        fn_ = getattr(RR_, "attribute", None)
        if fn_ is None:
            return False, "recordrows has no attribution read (attribute)"
        provs_ = [BASE, UPD, "LaterFix.esp", OTHER, MOD_A, PATCH]
        vals_ = ["v0", "v1", "v2", "v1", "v3", "v2"]
        ms_ = {UPD.lower(): [BASE], "laterfix.esp": [BASE, UPD], OTHER.lower(): [BASE, UPD], MOD_A.lower(): [BASE, UPD],
               PATCH.lower(): [BASE, MOD_A]}
        p_, o_ = fn_(provs_, vals_, 5, ms_), fn_(provs_, vals_, 3, ms_)
        ok_ = (p_.get("own") is True and p_.get("origin") == 5 and p_.get("sameAs") == [2]
               and o_.get("own") is False and o_.get("origin") == 1 and o_.get("carriers") == [3])
        return ok_, f"patch {p_}; other {o_}"

    # C1140 (Pilgrim S2): the other mod moves a base-game partner farther than the object's size: the measured crossing
    # is where that mod places it (the base game's placement is elsewhere), so it is credited to that mod's own
    # placement, never "may come from its own placement as much as from the base game's". A partner it only turns keeps
    # that sentence (both placements share the spot).
    def c1140_far_move_credits_mover_placement():
        far_ = (0x00CA40, (500.0, 100.0, 0.0, 1.5, 0x0B0A79), (800.0, 100.0, 0.0, 1.5, 0x0B0A79))
        turn_ = (0x00CB40, (900.0, 100.0, 0.0, 1.5, 0x0B0A79), (900.0, 100.0, 0.0, 2.0, 0x0B0A79))
        md, it = nudge_bundle([far_, turn_])
        sents_ = re.split(r"(?<=\.)\s+", it)
        hedge_ = " ".join(x for x in sents_ if "as much as from the base game" in x)
        credit_ = next((x for x in sents_ if "00CA40:Skyrim.esm" in x and f"{OTHER}'s own placement" in x
                        and "as much as" not in x), "")
        return ("00CA40:Skyrim.esm" not in hedge_ and "00CB40:Skyrim.esm" in hedge_ and bool(credit_)), \
            f"hedge {hedge_[:240]!r} || credit {credit_[:240]!r}"

    # C1141 (Snazzy S1): a kept lost change (the patch keeps ModB's base over ModA's) on an object both mods have on,
    # which the patch's own version sets off for good: the removal is the patch's own change, said so, never "the winner
    # sets off for good".
    def c1141_patch_removal_credited_to_patch():
        D = f"00CA41:{BASE}"
        o_, a_, b_ = ({**offs(0), "Base": f"0B0C{n}:{BASE}"} for n in ("70", "71", "72"))
        pt_ = {**offs(-30000, "InitiallyDisabled"), "Base": f"0B0C72:{BASE}"}
        p_rows = [lost_row(MOD_A, MOD_B, D, "Base", [f"Base=0B0C71:{BASE}"], [f"Base=0B0C72:{BASE}"],
                           [f"Base=0B0C70:{BASE}"], before=BASE)]
        w_rows = [lost_row(MOD_A, PATCH, D, "Base", [f"Base=0B0C71:{BASE}"], [f"Base=0B0C72:{BASE}"],
                           [f"Base=0B0C70:{BASE}"], before=BASE)]
        p_ch = [(D, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_)])]
        w_ch = [(D, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_), (PATCH, pt_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE, MOD_B]})
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        w = [e.get("withPatch") for e in rec_of(plan, D)]
        return (w == ["kept"] and "winner sets off for good" not in kl
                and re.search(r"\b1 on objects [^;()]*patch's own change sets off for good", kl) is not None), f"{w}; {kl[:400]}"

    # C1142 (Missives S1): ModA undoes its master Update.esm's fix in two records; OtherMod.esp carries Update.esm's value
    # in the first only and ThirdMod.esp in the second only (read from each record's versions; ThirdMod's has no row of
    # its own). The undo line names each carrier for the fields it carries, never one plugin for all of them.
    def c1142_undo_carriers_per_field():
        R1, R2 = f"00CA42:{BASE}", f"00CB42:{BASE}"
        arms = [arm("baseline", [BASE, UPD, OTHER, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, OTHER, THIRD, MOD_A, MOD_B])]
        b0, a0 = offs(0), offs(0)
        u0 = offs(0)
        u0["Placement.Position"] = "100,100,12"
        chains = [(R1, "PlacedObject", [(BASE, b0), (UPD, u0), (OTHER, u0), (MOD_A, a0)]),
                  (R2, "PlacedObject", [(BASE, b0), (UPD, u0), (THIRD, u0), (MOD_A, a0)])]
        p_rows = [lost_row(UPD, MOD_A, R1, "Placement", ["Placement.Position=100,100,12"], PH, PH),
                  lost_row(UPD, MOD_A, R2, "Placement", ["Placement.Position=100,100,12"], PH, PH),
                  lost_row(OTHER, MOD_A, R1, "Placement", ["Placement.Position=100,100,12"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={UPD: [BASE], OTHER: [BASE, UPD], THIRD: [BASE, UPD], MOD_A: [BASE, UPD],
                                           MOD_B: [BASE], PATCH: [BASE]})
        line = next((ln for ln in md.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        return (THIRD in line and OTHER in line and f"{OTHER} carries {UPD}'s value there" not in line), line[:500]

    # C1143 (Clockwork S1): ModA's value wins over HearthFires.esm's own change (OtherMod.esp only carries it). With the
    # patch, the winning version holds LaterFix.esp's own change of that field, made over HearthFires.esm's value: the
    # line says the patch's winner has that later change, which replaces HearthFires.esm's, never only "has
    # HearthFires.esm's value in 0 of the 1".
    def c1143_with_patch_superseding_change_named():
        L1, LF = f"00CA43:{BASE}", "LaterFix.esp"
        arms = [arm("baseline", [BASE, HF_, LF, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, LF, OTHER, MOD_A, MOD_B])]
        pre_ = [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}), (LF, {"Location": "018A5A:Skyrim.esm"}),
                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})]
        p_ch = [(L1, "Cell", pre_)]
        w_ch = [(L1, "Cell", pre_ + [(PATCH, {"Location": "018A5A:Skyrim.esm"})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        w_rows = [lost_row(MOD_A, PATCH, L1, "Location", ["Location="], ["Location=018A5A:Skyrim.esm"], PH)]
        plan, md, _ = core_bundle(p_rows, w_rows, check=CELLATTR, arms=arms, p_chains=p_ch, w_chains=w_ch,
                                  masters={HF_: [BASE], LF: [BASE, HF_], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE],
                                           PATCH: [BASE, MOD_A]})
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        tail_ = line.split("; with ", 1)[1] if "; with " in line else ""
        return (LF in tail_ and re.search(r"replac|supersed", tail_) is not None), line[-400:]

    # C1144 (Missives r6 S2): an earlier loss the patch also fixes names only the plugin whose own change the losing value
    # is. Neighbour.esp and Fourth.esp, each built with OtherMod.esp as a master, only carry OtherMod.esp's value: the
    # change is OtherMod.esp's, whichever loser's row the unit is read from; neither carrier is said to change it.
    # (Cause on regen-r6b: the read was keyed by a row rebuilt from another loser's summary, found no field and walked
    # back to the base game, so each carrier was named as its own; logs/missives-own-of-debug.txt.)
    def c1144_earlier_fix_carriers_named_by_origin():
        ln = earlier_fix_bundle(True, o2_carries=True, fourth=True)
        return ("OtherMod.esp's change" in ln and "Neighbour.esp's" not in ln and "Fourth.esp's" not in ln), ln[:500]

    # C1145 (BPT r6 S3): ModA's value is lost to Partner.esp's own change; the only measured setup without Partner.esp
    # also lacks Later.esp, which loads after ModA and carries the base game's value. Read from the record's versions,
    # without Partner.esp ModA's value is still lost (to Later.esp's): the item never says it needs both because "the
    # setup without Partner.esp does not show it", and names Later.esp.
    def c1145_needs_both_judged_on_the_chain():
        PT_, LT_, R_ = "Partner.esp", "Later.esp", f"00CA45:{BASE}"
        arms_ = [arm("baseline", [BASE, MOD_A, LT_, PT_, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, MOD_A, LT_, PT_]), arm("without-partner", [BASE, MOD_A])]
        mh_ = lambda x: {"MaxHeight.CellData": x, "MaxHeight.Offset": "0"}  # noqa: E731
        ch_ = [(R_, "Worldspace", [(BASE, mh_("00")), (MOD_A, mh_("0A")), (LT_, mh_("00")), (PT_, mh_("0B"))])]
        r_ = lost_row(MOD_A, PT_, R_, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=0B"],
                      ["MaxHeight.CellData=00"])
        r_["Summary"] = f"Worldspace Tamriel: MaxHeight - {MOD_A}'s value lost to {PT_}'s"
        enr = {(RECORDS, "without-patch"): [r_], (RECORDS, "baseline"): [r_], (RECORDS, "without-partner"): []}
        tr = {(RECORDS, "without-patch"): records_transcript(ch_), (RECORDS, "baseline"): records_transcript(ch_)}
        plan, md, _ = compile_and_render(arms_, {(CONTACT, a_): [] for a_ in ("baseline", "without-patch", "without-partner")},
                                         [MOD_A], patches=[PATCH], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         enrichment=enr, transcripts=tr,
                                         masters={MOD_A: [BASE], LT_: [BASE], PT_: [BASE, LT_], PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        return ("does not show it" not in it and LT_ in it), it[:500]

    # C1146 (BPT r6 S3, line 16): a lost change no patch touches, lost to OtherMod.esp's value that is ThirdMod.esp's own
    # change OtherMod.esp only carries: the still-there line names ThirdMod.esp as the value's own, never OtherMod.esp
    # alone.
    def c1146_still_there_names_value_origin():
        R = f"00CA46:{BASE}"
        arms = [arm("baseline", [BASE, THIRD, MOD_A, OTHER, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, OTHER])]
        chains = [(R, "Worldspace", [(BASE, {"MaxHeight.CellData": "00"}), (THIRD, {"MaxHeight.CellData": "0B"}),
                                     (MOD_A, {"MaxHeight.CellData": "0A"}), (OTHER, {"MaxHeight.CellData": "0B"})])]
        p_rows = [lost_row(MOD_A, OTHER, R, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=0B"],
                           ["MaxHeight.CellData=00"])]
        p_rows[0]["Summary"] = f"Worldspace Tamriel: MaxHeight - {MOD_A}'s value lost to {OTHER}'s"
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={THIRD: [BASE], MOD_A: [BASE], OTHER: [BASE, THIRD], PATCH: [BASE]})
        st = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        part = next((x for x in re.split(r";\s+(?![^()]*\))", st) if "record field" in x), "")
        return (bool(part) and f"one {THIRD} made first" in part), f"{part[:300]!r} || {st[:300]!r}"

    # C1147 (Winterhold S1): a model in the sky folder (a cloud card: an effect-shaded, alpha-blended model with no
    # collision, like every model there) is not a solid body: counted with the visual-effect pairs in the headline and
    # the kind line, said so in the item, and in the line of new clashes with the patch.
    def c1147_sky_model_not_solid():
        X, Y, Z, W = f"000X47:{MOD_A}", f"000Y47:{MOD_B}", f"000Z47:{MOD_B}", f"000W47:{MOD_B}"
        r1, r2, r3 = (row("OVERLAP", MOD_B, MOD_A, X, other=o_) for o_ in (Y, Z, W))
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        names_ = {"refs": {X: {"base": f"0B0X47:{MOD_A}"}, Y: {"base": f"0B0Y47:{MOD_B}"}, Z: {"base": f"0B0Z47:{MOD_B}"},
                           W: {"base": f"0B0Y47:{MOD_B}"}},
                  "bases": {f"0B0X47:{MOD_A}": {"type": "Static", "editorId": "Wall", "model": "arch\\wall.nif"},
                            f"0B0Y47:{MOD_B}": {"type": "Static", "editorId": "CloudDistant",
                                                "model": "Sky\\INV_CloudDistant01_O.nif"},
                            f"0B0Z47:{MOD_B}": {"type": "Static", "editorId": "Rock", "model": "rocks\\rock.nif"}}}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [r1, r2, r3], (OVERLAP, "without-patch"): [r1, r2],
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH], names=names_)
        pr_ = reader(plan).get("prediction") or {}
        v = str(pr_.get("verdict") or "")
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Objects occupying")), "")
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        nw = next((ln for ln in md.splitlines() if "New clashes with the patch" in ln), "")
        return ("1 of the pairs with a visual-effect model" in v and "1 with a visual-effect model" in kl
                and re.search(r"1 of these pairs involve[^.]*sky", it) is not None
                and "1 with a visual-effect model" in nw), f"{v[-160:]} || {kl[:160]} || {it[-300:]} || {nw[:300]}"

    for name, fn in [("seventh-review/C1148-attribution-read", c1148_attribution_read),
                     ("seventh-review/C1140-far-move-credits-mover-placement", c1140_far_move_credits_mover_placement),
                     ("seventh-review/C1141-patch-removal-credited-to-patch", c1141_patch_removal_credited_to_patch),
                     ("seventh-review/C1142-undo-carriers-per-field", c1142_undo_carriers_per_field),
                     ("seventh-review/C1143-with-patch-superseding-change-named", c1143_with_patch_superseding_change_named),
                     ("seventh-review/C1144-earlier-fix-carriers-named-by-origin", c1144_earlier_fix_carriers_named_by_origin),
                     ("seventh-review/C1145-needs-both-judged-on-the-chain", c1145_needs_both_judged_on_the_chain),
                     ("seventh-review/C1146-still-there-names-value-origin", c1146_still_there_names_value_origin),
                     ("seventh-review/C1147-sky-model-not-solid", c1147_sky_model_not_solid)]:
        cell(name, fn)

    # ------------------------------------------------ seventh review, later items (C1150-C1161)
    # C1155 (Clockwork S2): two plugins carry HearthFires.esm's value on one field, both lost to ModA: the set-aside
    # sentence counts 1 record field (2 plugin pairs), and where every such field carries an earlier plugin's change,
    # gives that one explanation, never "only undoes the change of the mod loaded before it" beside it.
    def c1155_set_aside_carried_one_explanation():
        W, O2 = f"00CA55:{BASE}", "Neighbour.esp"
        arms = [arm("baseline", [BASE, HF_, THIRD, OTHER, O2, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, THIRD, OTHER, O2, MOD_A, MOD_B])]
        chains = [(W, "Cell", [(BASE, {"WaterHeight": "0"}), (HF_, {"WaterHeight": "5"}), (THIRD, {"WaterHeight": "7"}),
                               (OTHER, {"WaterHeight": "5"}), (O2, {"WaterHeight": "5"}), (MOD_A, {"WaterHeight": "7"})])]
        p_rows = [lost_row(x_, MOD_A, W, "WaterHeight", ["WaterHeight=5"], PH, PH) for x_ in (HF_, OTHER, O2)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], THIRD: [BASE], OTHER: [BASE, HF_], O2: [BASE, HF_], MOD_A: [BASE],
                                           MOD_B: [BASE]})
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        s_ = next((x for x in re.split(r"(?<=\.)\s+(?=Also)", line) if "00CA55" in x), "")
        return ("1 record field (2 plugin pairs)" in s_ and "only undoes the change" not in s_), s_[:400]

    # C1161 (Missives S3): a set-aside count in record fields counts fields, with its plugin pairs where more rows than
    # fields: two losers of one list field that differs only in order are 1 record field (2 plugin pairs).
    def c1161_set_aside_counts_fields():
        G1 = f"00CA61:{BASE}"
        p_rows = [lost_row(x_, MOD_A, G1, "Regions", [f"Regions: same 7 item(s), ORDER DIFFERS from {MOD_A}"], PH, [PH])
                  for x_ in (OTHER, THIRD)]
        plan, md, _ = cw_bundle(p_rows, check=CELLATTR)
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        s_ = next((x for x in re.split(r"(?<=\.)\s+(?=Also)", line) if "another order" in x), "")
        return ("1 record field (2 plugin pairs)" in s_), s_[:300]

    # C1149 (found at regen-r7dev, Winterhold): the one-mod-alone line of a check says each mod's in-game meaning; an
    # item whose pairs include visual-effect models leads its meaning with "for the other N pairs", which that line does
    # not split: the line says the shared meaning once, never "In game, from X: in game, for the other N ...".
    def c1149_alone_line_meaning_without_effect_lead():
        X1, X2, Y1 = f"000X49:{MOD_A}", f"000Y49:{MOD_A}", f"000Z49:{MOD_B}"
        O1, O2, O3 = f"000A49:{OTHER}", f"000B49:{OTHER}", f"000C49:{OTHER}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, X1, other=O1), row("OVERLAP", OTHER, MOD_A, X2, other=O2),
                 row("OVERLAP", OTHER, MOD_B, Y1, other=O3)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B], kind="baseline"), arm("only-a", [BASE, OTHER, MOD_A]),
                arm("only-b", [BASE, OTHER, MOD_B]), arm("neither", [BASE, OTHER])]
        b_ = lambda n_, m_: {"type": "Static", "editorId": n_, "model": m_}  # noqa: E731
        names_ = {"refs": {x_: {"base": f"0B{x_[:4]}:{BASE}"} for x_ in (X1, X2, Y1, O1, O2, O3)},
                  "bases": {f"0B{x_[:4]}:{BASE}": b_("Obj" + x_[:4], "effects\\fxmist.nif" if x_ == O1 else "arch\\wall.nif")
                            for x_ in (X1, X2, Y1, O1, O2, O3)}}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): rows_, (OVERLAP, "only-a"): rows_[:2],
                                                (OVERLAP, "only-b"): rows_[2:], (OVERLAP, "neither"): []},
                                         [MOD_A, MOD_B], names=names_)
        ln = next((x for x in md.splitlines() if "Objects occupying the same space** (overlap pairs)" in x), "")
        return (bool(ln) and "for the other" not in ln and "In game" in ln), ln[-400:]

    # C1150 (Winterhold S2): the patch keeps ModB's on version of one object over ModA's two switch-off fields (its
    # position below the world and its flags): the with-patch note counts the record fields with the objects they are
    # on, "2 (1 object) drawn though ...", never "2 drawn" as if two objects.
    def c1150_kept_note_says_objects():
        D = f"00CA50:{BASE}"
        a_, b_ = offs(-30000, "InitiallyDisabled"), offs(0)
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE),
                  lost_row(MOD_A, MOD_B, D, "SkyrimMajorRecordFlags", ["SkyrimMajorRecordFlags=InitiallyDisabled"],
                           ["SkyrimMajorRecordFlags=0"], ["SkyrimMajorRecordFlags=0"], before=BASE)]
        w_rows = [dict(r_, B=PATCH, Summary=r_["Summary"].replace(MOD_B, PATCH)) for r_ in p_rows]
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)])]
        w_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_), (PATCH, b_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]})
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or []
                   if x.startswith("Record fields")), "")
        return ("2 (1 object) drawn though the losing mod switches them off" in kl), kl[:400]

    # C1159 (BPT S3): every kept row of the with-patch line has its meaning: a kept position (both versions have the
    # object on; ModA moved it, the patch keeps ModB's spot) is said as the object standing where the winning mod puts it.
    def c1159_kept_position_has_meaning():
        D = f"00CA59:{BASE}"
        a_, b_ = offs(0), offs(0)
        a_["Placement.Position"], b_["Placement.Position"] = "300,100,0", "500,100,0"
        p_rows = [lost_row(MOD_A, MOD_B, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        w_rows = [lost_row(MOD_A, PATCH, D, "Placement", off_txt(a_), off_txt(b_), off_txt(offs(0)), before=BASE)]
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)])]
        w_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_), (PATCH, b_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]})
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or []
                   if x.startswith("Record fields")), "")
        return ("1 in the position, the winning mod's spot" in kl), kl[:400]

    # C1158 (BPT S1): the line of record changes the patch undoes names, per plugin, the fields and the records they are
    # on, so a modder can find them; the counts stay.
    def c1158_undone_line_itemised():
        p_rows = [lost_row(MOD_A, MOD_B, f"000781:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_B, PATCH, f"000781:{BASE}", "LocationReference", "b1", "a1", "a1"),
                  # (eighth review) the patch puts back the value each mod's change replaced: undone (C1179's rule)
                  lost_row(MOD_A, PATCH, f"000782:{BASE}", "Primitive", "Bounds=1,2,3", "Bounds=4,5,6", "Bounds=4,5,6"),
                  lost_row(MOD_B, PATCH, f"000783:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                           "Position=4,5,6; Rotation=0,0,0", "Position=4,5,6; Rotation=0,0,0")]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH],
                                            count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 3}, enrichment=enr)
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        return (f"{MOD_A} 1: " in line and f"000782:{BASE}" in line and f"{MOD_B} 1: " in line
                and f"000783:{BASE}" in line), line[:400]

    # C1160 (Missives S2): a lost value that names a record (a location) is said with that record's editor ID, read
    # from its defining plugin file, so a modder can tell what is lost without looking the FormID up.
    def c1160_lost_value_named_by_editor_id():
        L1 = f"00CA60:{BASE}"
        arms = with_files([arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])],
                          {BASE: plugin_file_of(BASE, [], [("LCTN", 0x018A59, [("EDID", b"TestCityLocation\x00")], False)],
                                                group="LCTN")})
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        return ("018A59:Skyrim.esm TestCityLocation" in line), line[:400]

    # C1151 (Sewers S1): the quest-or-switch check's row puts its depth on its own object (the one that leaves) and
    # names no other; its state is Unknown. The overlap item says the depth is that object's, and a switch clash item of
    # its own says the switch flips "which way was not found", never "when X is set", with the depth on that object.
    def c1151_switch_depth_on_its_object():
        X, Yo = f"000X51:{MOD_A}", f"000Y51:{MOD_B}"
        ov_ = row("OVERLAP", MOD_B, MOD_A, X, other=Yo)
        st_ = row("STATE-CONTACT@000S51:ModB.esp@Unknown", MOD_B, MOD_A, Yo, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {Yo} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip - hung from the host as authored (0u gap); under the winner it sits "
                          f"18.6u inside something else in Camp (ext)")
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A]), arm("without-a", [BASE, MOD_B])]
        sets_ = ("baseline", "without-patch", "without-b", "without-a")
        res1 = {(OVERLAP, a_): ([ov_] if a_ == "without-patch" else []) for a_ in sets_}
        res1.update({(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in sets_})
        _, md1, _ = compile_and_render(arms, res1, [MOD_A], patches=[PATCH])
        it1 = next((ln for ln in md1.splitlines() if ln.startswith("- **Objects occupying the same space")), "")
        res2 = {(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in sets_}
        res2.update({(CONTACT, a_): [] for a_ in sets_})
        plan2, md2, _ = compile_and_render(arms, res2, [MOD_A], patches=[PATCH])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        it2 = next((ln for ln in md2.splitlines() if ln.startswith(f"- **{title}")), "")
        ok = ("itself 18.6 u inside something it does not name" in it1 and "which way was not found" in it2
              and "is set," not in it2 and "000Y51" in it2.split("In game", 1)[-1])
        return ok, f"{it1[-300:]} || {it2[-300:]}"

    # C1152 (Sewers S2): the support check's 2 rows are on the 2 objects the fixture check counts; the headline counts
    # them once, and so do the with-patch kind line and the summary: said as the same objects, never added.
    def c1152_shared_objects_counted_once_in_comparison():
        fx = [row("EMPTY-SPACE", MOD_B, MOD_A, f"000F5{i}:{MOD_A}", other=f"000H5{i}:{MOD_B}") for i in (1, 2)]
        ct = [row("CONTACT", MOD_B, MOD_A, f"000F5{i}:{MOD_A}", other=f"000G5{i}:{MOD_B}") for i in (1, 2)]
        results = {(c, a): [] for c in (FIXTURE, CONTACT) for a in ("baseline",)}
        results.update({(FIXTURE, "without-patch"): fx, (CONTACT, "without-patch"): ct})
        plan, md, _ = compile_and_render(WP_ARMS, results, [MOD_A], patches=[PATCH])
        summ = str((reader(plan).get("validation") or {}).get("summary") or "")
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or []
                   if x.startswith(priorities()["reader"]["checkTitles"].get(CONTACT) or CONTACT)), "")
        return ("counted once" in summ and "2 object-and-support pairs," not in summ and "counted once there" in kl), \
            f"{summ[:300]} || {kl[:200]}"

    # C1153 (Sewers S4): a record check with no finding whose coverage judged navmesh records says their geometry is
    # judged by the navmesh check, so "no finding" is not read as no navmesh change lost.
    def c1153_null_says_navmesh_judged_elsewhere():
        cov_ = [{"Population": "contested NavigationMesh records", "Judged": 37, "Unjudgeable": {}},
                {"Population": "contested Cell records", "Judged": 48, "Unjudgeable": {}}]
        plan, md, html, idx = compile_and_render(WP_ARMS, {(RECORDS, "baseline"): [], (RECORDS, "without-patch"): []},
                                                 [MOD_A], patches=[PATCH], want_index=True,
                                                 coverage_pops={(RECORDS, a_): cov_ for a_ in ("baseline", "without-patch")})
        ln = next((x for x in idx.splitlines() if "No finding in the completed check" in x), "")
        return ("Of the 37 NavigationMesh records it judged, the geometry (Data) is judged by the check" in ln), ln[:400]

    # C1154 (Sewers R4 S4): an overlap pair whose object's base model is an editor marker the game does not draw (an
    # auto-load door's marker model, in skill data markerModels) has no body: set aside with the no-body pairs, never
    # counted or said "drawn through each other"; the other pair stays counted.
    def c1154_marker_model_has_no_body():
        X, Y, Z = f"000X54:{MOD_A}", f"000Y54:{MOD_B}", f"000Z54:{MOD_B}"
        r1, r2 = row("OVERLAP", MOD_B, MOD_A, X, other=Y), row("OVERLAP", MOD_B, MOD_A, X, other=Z)
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B]),
                arm("without-b", [BASE, MOD_A])]
        names_ = {"refs": {X: {"base": f"0B0X54:{MOD_A}"}, Y: {"base": f"0B0Y54:{MOD_B}"}, Z: {"base": f"0B0Z54:{MOD_B}"}},
                  "bases": {f"0B0X54:{MOD_A}": {"type": "Static", "editorId": "Wall", "model": "arch\\wall.nif"},
                            f"0B0Y54:{MOD_B}": {"type": "Door", "editorId": "AutoLoadDoor01", "model": "AutoLoadMarker01.nif"},
                            f"0B0Z54:{MOD_B}": {"type": "Static", "editorId": "Rock", "model": "rocks\\rock.nif"}}}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [r1, r2], (OVERLAP, "without-patch"): [r1, r2],
                                                (OVERLAP, "without-b"): []}, [MOD_A], patches=[PATCH], names=names_)
        adm = sorted(e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []
                     if e["checkId"] == OVERLAP)
        v = str((reader(plan).get("prediction") or {}).get("verdict") or "")
        return (adm == ["admitted", "noBody"] and "1 overlap candidate" in v), f"{adm}; {v[-200:]}"

    # C1156 (Clockwork S3): the pairs still crossing where a patch places one of the objects were measured again in the
    # setup with the patch: the line says so, with the triangle pairs the re-measure found.
    def c1156_patch_placed_says_remeasure():
        # (round 3, Missives 73) each pair's before and after from its own rows, summed: 40 + 3 -> 38 + 12
        plan, md, X = placed_overlap_bundle(details=["actual render surfaces: crossing; containment unassessed; 38 triangle pairs [mm]",
                                                     "actual render surfaces: crossing; containment unassessed; 12 triangle pairs [mm]"],
                                            before=["crossing; 40 triangle pairs [mm]", "crossing; 3 triangle pairs [mm]"])
        ln = next((x for x in md.splitlines() if x.startswith("- Predicted, where a patch places one")), "")
        return ("Re-measured there with" in ln and "43 triangle pairs without it, 50 with it" in ln), ln[:400]

    # C1176 (round 4, Winterhold 88): figures of a thousand or more are written with a separator ("2,682 triangle
    # pairs"); both sides read the whole number, never its last three digits.
    def c1176_triangle_pairs_with_thousands():
        plan, md, X = placed_overlap_bundle(details=["crossing; 1,200 triangle pairs [mm]", "crossing; 13 triangle pairs [mm]"],
                                            before=["crossing; 2,682 triangle pairs [mm]", "crossing; 1,961 triangle pairs [mm]"])
        ln = next((x for x in md.splitlines() if x.startswith("- Predicted, where a patch places one")), "")
        return ("4,643 triangle pairs without it, 1,213 with it" in ln), ln[:400]

    # C1170 (round 3, Missives 73): one pair, 21 triangle pairs in its own row without the patch and 1 in its re-measure
    # with it; its partner's other rows (another pair of the same object, 1) never give the before figure.
    def c1170_before_after_from_the_pair_own_rows():
        plan, md, X = placed_overlap_bundle(details=["crossing; 1 triangle pairs [mm]", "crossing; 1 triangle pairs [mm]"],
                                            before=["crossing; 21 triangle pairs [mm]", "crossing; 1 triangle pairs [mm]"])
        ln = next((x for x in md.splitlines() if x.startswith("- Predicted, where a patch places one")), "")
        return ("22 triangle pairs without it, 2 with it" in ln and "1 to 1" not in ln), ln[:400]

    # C1157 (Clockwork S4): the patch lists OtherMod.esp as a master, and a clash with it is still there with the patch,
    # beside one with Neighbour.esp, which it does not list: the scope line says the OtherMod one is inside what the
    # patch sets out to fix and stays with it, not only that the Neighbour one is outside.
    def scope_bundle(patch_edits_it, second_other=False, cell_only=False):
        """Neighbour.esp's pair (the patch neither lists nor edits it) and OtherMod.esp's pair (the patch lists OtherMod
        as a master; with patch_edits_it its own file holds a version of OtherMod's object)."""
        O2 = "Neighbour.esp"
        recs_ = ([("REFR", (1 << 24) | 0x000E58, [("NAME", struct.pack("<I", 0x0B0A79)),
                                                 ("DATA", struct.pack("<6f", 1.0, 2.0, 3.0, 0.0, 0.0, 0.0))], False)]
                 if patch_edits_it else [])
        # cell_only (eighth review, G1): the patch holds only the cell record the pairs stand in, no object of them
        recs_ += [("CELL", 0x000E5C, [("DATA", struct.pack("<H", 0x0001))], False)] if cell_only else []
        arms = with_files([arm("baseline", [BASE, OTHER, O2, MOD_A, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, OTHER, O2, MOD_A])],
                          {PATCH: plugin_file_of(PATCH, [BASE, OTHER, MOD_A], recs_, group="CELL")})
        r1 = row("OVERLAP", O2, MOD_A, f"000E57:{MOD_A}", other=f"000E59:{O2}")
        r2 = row("OVERLAP", OTHER, MOD_A, f"000E56:{MOD_A}", other=f"000E58:{OTHER}")
        rs_ = [r1, r2] + ([row("OVERLAP", OTHER, MOD_A, f"000E55:{MOD_A}", other=f"000E5A:{OTHER}")] if second_other else [])
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): rs_, (OVERLAP, "without-patch"): rs_},
                                         [MOD_A], patches=[PATCH], masters={OTHER: [BASE], O2: [BASE], MOD_A: [BASE],
                                                                            PATCH: [BASE, OTHER, MOD_A]})
        return next((x for x in ((reader(plan).get("validation") or {}).get("sentences") or []) if "supplied patch edit" in x), "")

    def c1157_scope_line_says_inside_left():
        # (round 2, Clockwork 77) inside only where the patch edits the pair's object
        ln = scope_bundle(True)
        return (f"still there with {OTHER} (1 overlap pair) lies on records, navmeshes or objects a supplied patch edits" in ln), ln[:500]

    # C1163 (round 2, Clockwork line 77): the patch lists OtherMod.esp as a master but edits none of the pair's objects:
    # the remainder is outside what the patch sets out to fix, said with Neighbour.esp's, never inside.
    def c1163_master_alone_is_not_scope():
        ln = scope_bundle(False)
        return ("lies on records, navmeshes or objects a supplied patch edits" not in ln and f"still there with Neighbour.esp and {OTHER} lies on records, navmeshes or objects that no supplied patch edits" in ln), ln[:500]

    for name, fn in [("seventh-review/C1160-lost-value-named-by-editor-id", c1160_lost_value_named_by_editor_id),
                     ("seventh-review/C1157-scope-line-says-inside-left", c1157_scope_line_says_inside_left),
                     ("seventh-review/C1156-patch-placed-says-remeasure", c1156_patch_placed_says_remeasure),
                     ("round3/C1170-before-after-from-the-pair-own-rows", c1170_before_after_from_the_pair_own_rows),
                     ("round4/C1176-triangle-pairs-with-thousands", c1176_triangle_pairs_with_thousands),
                     ("seventh-review/C1154-marker-model-has-no-body", c1154_marker_model_has_no_body),
                     ("seventh-review/C1153-null-says-navmesh-judged-elsewhere", c1153_null_says_navmesh_judged_elsewhere),
                     ("seventh-review/C1152-shared-objects-counted-once-in-comparison",
                      c1152_shared_objects_counted_once_in_comparison),
                     ("seventh-review/C1151-switch-depth-on-its-object", c1151_switch_depth_on_its_object),
                     ("seventh-review/C1158-undone-line-itemised", c1158_undone_line_itemised),
                     ("seventh-review/C1159-kept-position-has-meaning", c1159_kept_position_has_meaning),
                     ("seventh-review/C1150-kept-note-says-objects", c1150_kept_note_says_objects),
                     ("seventh-review/C1149-alone-line-meaning-without-effect-lead", c1149_alone_line_meaning_without_effect_lead),
                     ("seventh-review/C1155-set-aside-carried-one-explanation", c1155_set_aside_carried_one_explanation),
                     ("seventh-review/C1161-set-aside-counts-fields", c1161_set_aside_counts_fields)]:
        cell(name, fn)

    # ------------------------------------------------ seventh review, round 2 (C1162-C1189)
    # C1162 (BPT B1): an overlap pair the patch resolves by switching off the OTHER object (ModB's), which no record
    # import holds: read from the plugin files of each setup, counted as resolved by switching an object off, the same
    # rule as when the patch switches off the row's own object.
    def c1162_switch_off_either_object_counted():
        X, Y = f"000E61:{MOD_A}", f"000E62:{MOD_B}"
        def refr(low_, z_):
            return ("REFR", low_, [("NAME", struct.pack("<I", 0x0B0A79)),
                                   ("DATA", struct.pack("<6f", 100.0, 100.0, z_, 0.0, 0.0, 0.0))], False)
        fb_ = plugin_file_of(MOD_B, [BASE], [refr((1 << 24) | 0x000E62, 0.0)], group="CELL")
        fp_ = plugin_file_of(PATCH, [BASE, MOD_B], [refr((1 << 24) | 0x000E62, -30000.0)], group="CELL")
        arms_ = with_files([arm("baseline", [BASE, MOD_B, MOD_A, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, MOD_B, MOD_A])],
                           {MOD_B: fb_, PATCH: fp_, MOD_A: plugin_file_of(MOD_A, [BASE], [], group="CELL")})
        r_ = row("OVERLAP", MOD_B, MOD_A, X, other=Y)
        plan, md, _ = compile_and_render(arms_, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [r_]}, [MOD_A],
                                         patches=[PATCH])
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or []
                   if x.startswith("Objects occupying")), "")
        return ("resolved 1 (1 by the patch switching an object off)" in kl), kl[:300]

    # C1164-C1166 (round 2, the attribution class): one value has one owner, the first plugin that holds it as a change of
    # its own; a later plugin holding the same bytes carries it, whether it inherits them from a master or not.
    def owner_bundle(chain_vals, loser, winner, ms):
        R_ = f"00CA64:{BASE}"
        mh_ = lambda x: {"MaxHeight.CellData": x}  # noqa: E731
        names_ = [p_ for p_, _ in chain_vals]
        arms = [arm("baseline", names_ + [PATCH], kind="baseline"), arm("without-patch", names_)]
        ch_ = [(R_, "Worldspace", [(p_, mh_(v_)) for p_, v_ in chain_vals])]
        vals_ = dict(chain_vals)
        r_ = lost_row(loser, winner, R_, "MaxHeight", [f"MaxHeight.CellData={vals_[loser]}"],
                      [f"MaxHeight.CellData={vals_[winner]}"], ["MaxHeight.CellData=00"])
        r_["Summary"] = f"Worldspace Tamriel: MaxHeight - {loser}'s value lost to {winner}'s"
        plan, md, _ = core_bundle([r_], [r_], arms=arms, p_chains=ch_, w_chains=ch_, masters={**ms, PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        st = str((reader(plan).get("prediction") or {}).get("withPatchStays") or "")
        return it, st

    def c1164_same_bytes_have_one_owner():
        E_, L_ = "Earlier.esp", "LaterSame.esp"
        it, st = owner_bundle([(BASE, "00"), (E_, "0B"), (MOD_A, "0A"), (L_, "0B")], MOD_A, L_,
                              {E_: [BASE], MOD_A: [BASE], L_: [BASE]})
        return (f"{L_} only carries a value {E_} made first" in it
                and f"{L_} changed the field itself" not in it and f"one {E_} made first" in st), f"{it[-400:]} || {st[:200]}"

    def c1165_others_carry_the_losers_value():
        G_, P1_, P2_ = "GrandWalls.esp", "GrandPatchOne.esp", "GrandPatchTwo.esp"
        it, _ = owner_bundle([(BASE, "00"), (G_, "0B"), (P1_, "0B"), (P2_, "0B"), (MOD_A, "0A")], G_, MOD_A,
                             {G_: [BASE], P1_: [BASE, G_], P2_: [BASE], MOD_A: [BASE]})
        return (f"{P1_} and {P2_} hold the same value as {G_}, which {G_} made first" in it
                and "as their own change" not in it), it[-400:]

    def c1166_value_used_without_winner_named_by_owner():
        E_, M_, L_ = "Earlier.esp", "Middle.esp", "LaterOwn.esp"
        it, _ = owner_bundle([(BASE, "00"), (MOD_A, "0A"), (E_, "0B"), (M_, "0B"), (L_, "0C")], MOD_A, L_,
                             {MOD_A: [BASE], E_: [BASE], M_: [BASE], L_: [BASE]})
        return (f"Without {L_}, the value used is {E_}'s, which {M_} carries" in it), it[-400:]

    # C1167 (round 2, Snazzy 28 / 73): with the patch the winning version holds none of the field: the base-fix line says
    # so, never that it holds a later change of it.
    def c1167_patch_without_the_field_holds_no_later_change():
        L1, LF = f"00CA67:{BASE}", "LaterFix.esp"
        arms = [arm("baseline", [BASE, HF_, LF, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, LF, OTHER, MOD_A, MOD_B])]
        pre_ = [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}), (LF, {"Location": "has no Location"}),
                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})]
        p_ch = [(L1, "Cell", pre_)]
        w_ch = [(L1, "Cell", pre_ + [(PATCH, {"Location": "has no Location"})])]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, [], check=CELLATTR, arms=arms, p_chains=p_ch, w_chains=w_ch,
                                  masters={HF_: [BASE], LF: [BASE, HF_], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE],
                                           PATCH: [BASE, MOD_A]})
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        return ("holds none of that field" in line and "later value" not in line), line[-400:]

    # C1168 (round 2, regen-r7c Winterhold, Clockwork, Missives): a winning value that is the base game's has the base
    # game as its owner, which is no mod: the scope line never names the base game as a mod outside what the patch fixes.
    def c1168_base_game_owner_is_not_outside_scope():
        R_, L_ = f"00CA64:{BASE}", "LaterUndo.esp"
        mh_ = lambda x: {"MaxHeight.CellData": x}  # noqa: E731
        chain_vals = [(BASE, "00"), (MOD_A, "0A"), (L_, "00")]
        names_ = [p_ for p_, _ in chain_vals]
        arms = [arm("baseline", names_ + [PATCH], kind="baseline"), arm("without-patch", names_)]
        ch_ = [(R_, "Worldspace", [(p_, mh_(v_)) for p_, v_ in chain_vals])]
        r_ = lost_row(MOD_A, L_, R_, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=00"],
                      ["MaxHeight.CellData=00"])
        r_["Summary"] = f"Worldspace Tamriel: MaxHeight - {MOD_A}'s value lost to {L_}'s"
        plan, md, _ = core_bundle([r_], [r_], arms=arms, p_chains=ch_, w_chains=ch_,
                                  masters={MOD_A: [BASE], L_: [BASE], PATCH: [BASE, L_]})
        val_ = (reader(plan).get("validation") or {}).get("sentences") or []
        sc_ = [x for x in val_ if "supplied patch edit" in x or "out to fix" in x]
        return (val_ != [] and not any("the base game" in x.split("still there with", 1)[-1] for x in sc_)), " | ".join(sc_)[:500] or "no scope line"

    # C1169 (round 2, regen-r7c BPT line 75): OtherMod.esp has one pair the patch edits and one it does not: the scope line
    # names OtherMod.esp outside with the count of the part outside, beside the part inside, never the bare name in both.
    def c1169_mod_on_both_sides_says_each_part():
        ln = scope_bundle(True, second_other=True)
        return (f"{OTHER} (1 overlap pair) lies on records, navmeshes or objects that no supplied patch edits" in ln
                and f"still there with {OTHER} (1 overlap pair) lies on records, navmeshes or objects a supplied patch edits" in ln), ln[:600]

    # C1171 (round 3, BPT 75, Clockwork 77, Missives 75, Sewers 74): the scope line says only what the records show:
    # which leftovers lie on records, navmeshes or objects no supplied patch edits; never what a patch sets out to do.
    def c1171_scope_line_says_records_not_intent():
        ln = scope_bundle(True)
        return (ln != "" and not re.search(r"set out|sets out|intend|job|masters", ln)
                and "Neighbour.esp lies on records, navmeshes or objects that no supplied patch edits" in ln), ln[:500]

    # C1172 (round 3, Winterhold 17): two records of one item; on R1 the winner only carries a value Earlier.esp made
    # first, on R2 the winner's value is its own. The item's examples (R1 and R2) come before the carried sentence, so
    # they never read as its own; the sentence names no record of the other rows (the skill data's template may name one
    # of its own, {example}; the page budget leaves it out).
    def c1172_carried_example_from_its_rows():
        E_, L_ = "Earlier.esp", "LaterWin.esp"
        R1, R2 = f"00CA70:{BASE}", f"00CA71:{BASE}"
        mh_ = lambda x: {"MaxHeight.CellData": x}  # noqa: E731
        names_ = [BASE, E_, MOD_A, L_]
        arms = [arm("baseline", names_ + [PATCH], kind="baseline"), arm("without-patch", names_)]
        ch_ = [(R1, "Worldspace", [(BASE, mh_("00")), (E_, mh_("0B")), (MOD_A, mh_("0A")), (L_, mh_("0B"))]),
               (R2, "Worldspace", [(BASE, mh_("00")), (E_, mh_("00")), (MOD_A, mh_("0A")), (L_, mh_("0C"))])]
        rows_ = []
        for R_, w_ in ((R1, "0B"), (R2, "0C")):
            r_ = lost_row(MOD_A, L_, R_, "MaxHeight", ["MaxHeight.CellData=0A"], [f"MaxHeight.CellData={w_}"],
                          ["MaxHeight.CellData=00"])
            r_["Summary"] = f"Worldspace {R_[:6]}: MaxHeight - {MOD_A}'s value lost to {L_}'s"
            rows_.append(r_)
        plan, md, _ = core_bundle(rows_, rows_, arms=arms, p_chains=ch_, w_chains=ch_,
                                  masters={E_: [BASE], MOD_A: [BASE], L_: [BASE], PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Record fields")), "")
        s_ = next((x for x in re.split(r"(?<=\.) ", it) if "only carries a value" in x), "")
        nxt_ = it.split(s_, 1)[-1].lstrip()[:12] if s_ else ""
        return (s_ != "" and R2 not in s_ and not nxt_.startswith("For example")
                and it.index("For example") < it.index(s_)), f"{s_} || next: {nxt_} || {it[-500:]}"

    # C1173 (round 3, BPT 15 / 71 / 85 / 88): both mods' changes are lost to the patch, whose value is the base game's
    # (the record's own value, put back): said as the base game's value put back, never as the patch's own value.
    def c1173_patch_value_base_put_back(mixed=False):
        R_ = f"00CA73:{BASE}"
        mh_ = lambda x: {"MaxHeight.CellData": x}  # noqa: E731
        p_rows = [lost_row(MOD_A, MOD_B, R_, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=0B"],
                           ["MaxHeight.CellData=00"], before=BASE)]
        w_rows = [lost_row(m_, PATCH, R_, "MaxHeight", [f"MaxHeight.CellData={v_}"], ["MaxHeight.CellData=00"],
                           ["MaxHeight.CellData=00"], before=BASE) for m_, v_ in ((MOD_A, "0A"), (MOD_B, "0B"))]
        for r_ in p_rows + w_rows:
            r_["Summary"] = r_["Summary"].replace("PlacedObject", "Worldspace")
        p_ch = [(R_, "Worldspace", [(BASE, mh_("00")), (MOD_A, mh_("0A")), (MOD_B, mh_("0B"))])]
        w_ch = [(R_, "Worldspace", [(BASE, mh_("00")), (MOD_A, mh_("0A")), (MOD_B, mh_("0B")), (PATCH, mh_("00"))])]
        if mixed:
            # a second record where the patch's value (0C) is its own
            R2 = f"00CA75:{BASE}"
            p2 = [lost_row(MOD_A, MOD_B, R2, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=0B"],
                           ["MaxHeight.CellData=00"], before=BASE)]
            w2 = [lost_row(m_, PATCH, R2, "MaxHeight", [f"MaxHeight.CellData={v_}"], ["MaxHeight.CellData=0C"],
                           ["MaxHeight.CellData=00"], before=BASE) for m_, v_ in ((MOD_A, "0A"), (MOD_B, "0B"))]
            for r_ in p2 + w2:
                r_["Summary"] = r_["Summary"].replace("PlacedObject", "Worldspace")
            p_rows, w_rows = p_rows + p2, w_rows + w2
            p_ch = p_ch + [(R2, "Worldspace", [(BASE, mh_("00")), (MOD_A, mh_("0A")), (MOD_B, mh_("0B"))])]
            w_ch = w_ch + [(R2, "Worldspace", [(BASE, mh_("00")), (MOD_A, mh_("0A")), (MOD_B, mh_("0B")), (PATCH, mh_("0C"))])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]},
                                  targets=(MOD_A, MOD_B))
        pr_ = reader(plan).get("prediction") or {}
        kl = next((x for x in pr_.get("withPatchLines") or [] if x.startswith("Record fields")), "")
        vo = next((x for x in md.splitlines() if x.startswith("- Predicted, where a patch") and "both mods' values" in x), "")
        if mixed:
            return ("the patch's own value 1, the patch puts back the base game's value 1" in kl
                    and "(the patch's own 1; the base game's value put back 1)" in vo and "its own value" not in vo), f"{kl} || {vo}"
        return ("puts back the base game's value 1" in kl and "own value" not in kl
                and "puts back the base game's value over both mods' values: 1" in vo and "its own value" not in vo), f"{kl} || {vo}"

    # C1174 (round 3, BPT 78): an earlier loss whose lost value is the base game's own, which the patch holds: said as
    # the base game's value the patch puts back, never as "the base game's change" lost to the later plugin.
    def c1174_earlier_base_value_put_back():
        md_ = earlier_fix_bundle(True, base_value=True, whole=True)
        # (eighth review, BPT 78) putting the base game's value back over Later.esp's own change undoes that change: said
        # in the undone family, never as an earlier clash the patch fixes
        un_ = next((x for x in md_.splitlines() if x.startswith("- Undone by") and "as without the evaluated mods" in x), "")
        return ("the base game's change" not in md_ and "Later.esp's change" in un_
                and "also fix" not in md_), un_[:500]

    for name, fn in [("round2/C1164-same-bytes-have-one-owner", c1164_same_bytes_have_one_owner),
                     ("round2/C1167-patch-without-the-field-holds-no-later-change",
                      c1167_patch_without_the_field_holds_no_later_change),
                     ("round2/C1165-others-carry-the-losers-value", c1165_others_carry_the_losers_value),
                     ("round2/C1166-value-used-without-winner-named-by-owner", c1166_value_used_without_winner_named_by_owner),
                     ("round2/C1168-base-game-owner-is-not-outside-scope", c1168_base_game_owner_is_not_outside_scope),
                     ("round3/C1172-carried-example-from-its-rows", c1172_carried_example_from_its_rows),
                     ("round3/C1173-patch-value-base-put-back", c1173_patch_value_base_put_back),
                     ("round3/C1175-some-patch-values-base-put-back", lambda: c1173_patch_value_base_put_back(mixed=True)),
                     ("round3/C1174-earlier-base-value-put-back", c1174_earlier_base_value_put_back),
                     ("round2/C1169-mod-on-both-sides-says-each-part", c1169_mod_on_both_sides_says_each_part),
                     ("round3/C1171-scope-line-says-records-not-intent", c1171_scope_line_says_records_not_intent)]:
        cell(name, fn)

    for name, fn in [("round2/C1162-switch-off-either-object-counted", c1162_switch_off_either_object_counted),
                     ("round2/C1163-master-alone-is-not-scope", c1163_master_alone_is_not_scope)]:
        cell(name, fn)

    # ---------------------------------------------------------------- after the eighth review (2026-10-23)
    # A comparison that refused its verdict (the share it could not judge rose), whose emit still re-measured each
    # predicted row in the setup with the patch: three predicted support clashes, one measured Gone, one Holds, one with
    # no re-measure.
    def refused_measured_bundle():
        pair = [row("CONTACT", MOD_B, MOD_A, f"00C17{i}:{MOD_A}", other=f"00D17{i}:{MOD_B}") for i in (1, 2, 3)]
        preds = [{"Kind": "CONTACT", "A": MOD_B, "B": MOD_A, "FormKey": f"00C17{i}:{MOD_A}", "Other": f"00D17{i}:{MOD_B}",
                  "Physical": p_} for i, p_ in ((1, "Gone"), (2, "Holds"))]
        order = {"with-patch": {"contactprobe": dict(verdict="DIFFERENTIAL-UNJUDGED", repaired=1, reclassified=0,
                                                     created=0, newDefect=0, attributed=0, unmeasured=0)}}
        log = ("   repaired     : 1 record(s)\n   ! REFUSED: dependent-host pairs: unjudgeable 8.33% -> 23.47% "
               "(313 of 3,757 -> 1,461 of 6,226) — rose by more than 1.00%\n")
        return compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): pair, (CONTACT, "with-patch"): [pair[1]]},
                                  [MOD_A, MOD_B], patches=[PATCH], order=order,
                                  predicates={(CONTACT, "with-patch"): preds},
                                  missing=[(CONTACT, "with-patch", "completed", "diff")],
                                  diff_logs={(CONTACT, "with-patch"): log})

    # C1177 (eighth review, Snazzy 13 / 75): each row is judged by its own re-measure whatever the comparison's verdict:
    # Gone is resolved, Holds is left, and only the row with no re-measure cannot be told ("could not be measured: 1").
    def c1177_refused_comparison_rows_judged_by_remeasure():
        plan, md, _ = get("refused-measured", refused_measured_bundle)
        val = reader(plan).get("validation") or {}
        w = sorted(str(e.get("withPatch")) for e in (reader(plan).get("prediction") or {}).get("record") or []
                   if e.get("checkId") == CONTACT and e.get("admission") == "admitted")
        cant = re.search(r"could not be measured: (\d+)", md)
        return (w == ["cannot-tell", "left", "resolved"] and val.get("cannotTell") == 1
                and cant is not None and cant.group(1) == "1" and "cannot tell 3" not in md), \
            f"{w}; cannotTell {val.get('cannotTell')}; {cant.group(0) if cant else None}"

    # C1178 (eighth review, Snazzy 150): the refused comparison's own line no longer says it reports no repair where
    # predicted clashes were re-measured gone; it gives that count beside the refusal.
    def c1178_refused_line_gives_measured_gone():
        plan, md, _ = get("refused-measured", refused_measured_bundle)
        ln = next((x for x in md.splitlines() if "refused a verdict" in x and "rose from 8.33% to 23.47%" in x
                   and x.startswith("- ") and not x.startswith("- **") and " — " in x), "")
        return (bool(ln) and "no repair" not in ln and re.search(r"\b1 predicted clash[^.;]* gone", ln) is not None), ln[:400]

    # C1179 (eighth review, BPT 77 / Snazzy 70): "undone" is the patch putting back the value the mod's change replaced.
    # R1: the patch sets a value of its own over ModA's change (neither ModA's nor the base game's): never counted as
    # undone, said as the patch's own value. R2: the patch puts back the base game's value over ModB's change: undone,
    # and the line says what undone means.
    def c1179_patch_own_value_not_undone():
        R0, R1, R2 = f"00C079:{BASE}", f"00C179:{BASE}", f"00C279:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_A, PATCH, R1, "Primitive", ["Primitive.Bounds=2,2,2"], ["Primitive.Bounds=3,3,3"],
                           ["Primitive.Bounds=1,1,1"], before=BASE),
                  lost_row(MOD_B, PATCH, R2, "Primitive", ["Primitive.Bounds=5,5,5"], ["Primitive.Bounds=4,4,4"],
                           ["Primitive.Bounds=4,4,4"], before=BASE)]
        w_ch = [(R1, "PlacedObject", [(BASE, {"Primitive.Bounds": "1,1,1"}), (MOD_A, {"Primitive.Bounds": "2,2,2"}),
                                      (PATCH, {"Primitive.Bounds": "3,3,3"})]),
                (R2, "PlacedObject", [(BASE, {"Primitive.Bounds": "4,4,4"}), (MOD_B, {"Primitive.Bounds": "5,5,5"}),
                                      (PATCH, {"Primitive.Bounds": "4,4,4"})])]
        plan, md, _ = core_bundle(p_rows, w_rows, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]},
                                  targets=(MOD_A, MOD_B))
        # (page budget) the set-to-another-value part is a clause of the undone line, after its own marker
        line_ = next((x for x in md.splitlines() if x.startswith("- Undone by ")), "")
        un, _, own_ln = line_.partition("; set to another value")
        own_ln = own_ln + line_[-120:] if own_ln else ""
        return (": 1 record field" in un and MOD_B in un and MOD_A not in un and "put back" in un
                and MOD_A in own_ln and "(evidence-index.md#F-" in own_ln and f"each patch's own value: {PATCH} 1" in own_ln), f"undone {un[:300]!r}; own {own_ln[:300]!r}"

    # C1181 (eighth review, BPT 77, 0EB5B2): a value the patch puts back on an object its own version sets off for good
    # (enabled opposite to the player) has no effect in game while the object is off: the undone line says so.
    def c1181_put_back_on_object_patch_sets_off():
        R0, R4 = f"00C081:{BASE}", f"00C181:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_B, PATCH, R4, "Primitive", ["Primitive.Bounds=5,5,5"], ["Primitive.Bounds=4,4,4"],
                           ["Primitive.Bounds=4,4,4"], before=BASE)]
        b_, m_ = {**offs(0), "Primitive.Bounds": "4,4,4"}, {**offs(0), "Primitive.Bounds": "5,5,5"}
        pt_ = {**offs(0, "0", "000014:Skyrim.esm"), "Primitive.Bounds": "4,4,4"}
        w_ch = [(R4, "PlacedObject", [(BASE, b_), (MOD_B, m_), (PATCH, pt_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]},
                                  targets=(MOD_A, MOD_B))
        un = next((x for x in md.splitlines() if x.startswith("- Undone by ")), "")
        return (re.search(r"1 [^;]*sets? off for good", un) is not None), un[:400]

    # C1180 (eighth review, BPT 78): an earlier loss whose lost value is the base game's own, where the value it loses to
    # without the evaluated mods is another plugin's change (Walls.esp's, which Later.esp only carries): the patch putting
    # the base game's value back undoes that change. Said neutrally in the undone family, naming whose change it is and
    # who carries it, never as an earlier clash the patch also fixes.
    def c1180_base_put_back_over_carried_change():
        md_ = earlier_fix_bundle(True, base_value=True, whole=True, later_carries=True)
        un_ = next((x for x in md_.splitlines() if x.startswith("- Undone by") and "as without the evaluated mods" in x), "")
        return ("Walls.esp's change, which Later.esp carries" in un_ and "also fix" not in md_
                and "Whether that is intended is not recorded" in un_), un_[:500] or md_[-1500:]

    # C1202 (Gate A review of 225871bb): a value lost to the patch with neither the record's versions nor its values
    # recorded is not told as undone or as anyone's replacement: said as not told.
    def c1202_unread_value_not_called_replacement():
        R0, R1 = f"00C002:{BASE}", f"00C202:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_A, PATCH, R1, "Owner", "a1", PH, PH)]
        plan, md, _ = core_bundle(p_rows, w_rows, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]}, targets=(MOD_A, MOD_B))
        lines = [x for x in md.splitlines() if R1 in x]
        return (len(lines) == 1 and "not told" in lines[0] and "own value" not in lines[0]
                and not lines[0].startswith("- Undone by") and not lines[0].startswith("- Set by")), str(lines)[:500]

    # C1203 (eighth review, BPT 0EB5B2 in the regeneration): a tree answer writes a field the winner has and another
    # provider lacks only as "<field> only in <winner>: <value>"; that is the winner's value too. The patch's version
    # enabled opposite to the player, said so only that way, sets the object off for good: the undone line says it.
    def c1203_winner_value_from_only_in():
        R0, R4 = f"00C003:{BASE}", f"00C203:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_B, PATCH, R4, "Primitive", ["Primitive.Bounds=5,5,5"], ["Primitive.Bounds=4,4,4"],
                           ["Primitive.Bounds=4,4,4"], before=BASE)]
        b_, m_ = {"Primitive.Bounds": "4,4,4"}, {"Primitive.Bounds": "5,5,5"}
        pt_ = {"Primitive.Bounds": "4,4,4"}
        req_, resp_ = tree_transcript([(R4, "PlacedObject", [(BASE, b_), (MOD_B, m_), (PATCH, pt_)])])
        body_ = json.loads(resp_[1]["result"]["content"][0]["text"])
        for n_ in body_["rows"][0]["nodes"]:
            if not n_["is_winner"]:
                n_["deltas"] += [f"EnableParent.Reference only in {PATCH}: 000014:Skyrim.esm",
                                 f"EnableParent.Flags only in {PATCH}: SetEnableStateToOppositeOfParent"]
        resp_[1]["result"]["content"][0]["text"] = json.dumps(body_)
        plan, md, _ = core_bundle(p_rows, w_rows, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]},
                                  targets=(MOD_A, MOD_B), w_tr=(req_, resp_))
        un = next((x for x in md.splitlines() if x.startswith("- Undone by ")), "")
        return (re.search(r"1 [^;]*sets? off for good", un) is not None), un[:400]

    # C1182 (eighth review, Snazzy 63 / 69): the summary of what the patch does never says "nothing new" alone beside
    # record fields the patch undoes and sets to its own value: it says those counts, pointing to their lines.
    def c1182_summary_says_record_effects():
        R0, R1, R2 = f"00C082:{BASE}", f"00C182:{BASE}", f"00C282:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_A, PATCH, R1, "Primitive", ["Primitive.Bounds=2,2,2"], ["Primitive.Bounds=3,3,3"],
                           ["Primitive.Bounds=1,1,1"], before=BASE),
                  lost_row(MOD_B, PATCH, R2, "Primitive", ["Primitive.Bounds=5,5,5"], ["Primitive.Bounds=4,4,4"],
                           ["Primitive.Bounds=4,4,4"], before=BASE)]
        w_ch = [(R1, "PlacedObject", [(BASE, {"Primitive.Bounds": "1,1,1"}), (MOD_A, {"Primitive.Bounds": "2,2,2"}),
                                      (PATCH, {"Primitive.Bounds": "3,3,3"})]),
                (R2, "PlacedObject", [(BASE, {"Primitive.Bounds": "4,4,4"}), (MOD_B, {"Primitive.Bounds": "5,5,5"}),
                                      (PATCH, {"Primitive.Bounds": "4,4,4"})])]
        plan, md, _ = core_bundle(p_rows, w_rows, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]},
                                  targets=(MOD_A, MOD_B))
        sm = next((x for x in md.splitlines() if x.startswith("In short, with ")), "")
        return ("undoes 1 record field" in sm and "sets 1 to another value" in sm   # wording: page budget
                and "nothing new the checks can see" not in sm), sm[:500]

    # C1183 (eighth review, Missives 30): with the patches the winning version is still the mod's own, which holds none
    # of the field, and no patch holds the record: the line names that version, never "the patch removes it".
    def c1183_none_held_names_the_winner():
        L1, LF = f"00CA83:{BASE}", "LaterFix.esp"
        arms = [arm("baseline", [BASE, HF_, LF, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, LF, OTHER, MOD_A, MOD_B])]
        pre_ = [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}), (LF, {"Location": "has no Location"}),
                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": "has no Location"})]
        p_rows = [lost_row(HF_, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH),
                  lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, [], check=CELLATTR, arms=arms, p_chains=[(L1, "Cell", pre_)],
                                  w_chains=[(L1, "Cell", pre_)],
                                  masters={HF_: [BASE], LF: [BASE, HF_], OTHER: [BASE, HF_], MOD_A: [BASE], MOD_B: [BASE],
                                           PATCH: [BASE, MOD_A]})
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        return ("holds none of that field" in line and "the patch removes it" not in line
                and f"{MOD_A}'s version" in line), line[-400:]

    # C1197 (eighth review, Pilgrim 116): removing the patch brings back every predicted outcome it changes, the field
    # where it keeps the losing mod's value (switched) as well as the resolved one: 2 record fields, never 1.
    def c1197_removing_brings_back_switched_too():
        R1, R2 = f"00C297:{BASE}", f"00C298:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Placement", "o1", "v1", "v1"),
                  lost_row(OTHER, MOD_A, R2, "Placement", "o2", "a2", "v2")]
        w_rows = [lost_row(MOD_A, PATCH, R1, "Placement", "v1", "o1", "o1"),
                  lost_row(MOD_A, PATCH, R2, "Placement", "a2", "o2", "o2")]
        chains = [(R1, "PlacedObject", [(BASE, {"Placement": "v1"}), (OTHER, {"Placement": "o1"}),
                                        (MOD_A, {"Placement": "v1"})]),
                  (R2, "PlacedObject", [(BASE, {"Placement": "v2"}), (OTHER, {"Placement": "o2"}),
                                        (MOD_A, {"Placement": "a2"})])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=chains, masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        w = sorted(str(e.get("withPatch")) for e in (reader(plan).get("prediction") or {}).get("record") or [])
        ln = (reader(plan).get("validation") or {}).get("noRegressionLine") or ""
        return (w == ["resolved", "switched"] and "brings back the 2 record fields whose outcome it changes" in ln), f"{w}; {ln[:300]}"

    # C1185 (eighth review, BPT 75 / review10 S1): a lost change still there, whose winning value is the base game's (its
    # owner is no mod), on a record a supplied patch holds (the patch carries ModA's value but loads before the winner):
    # the scope line says it, with the plugin whose version wins, never drops it.
    def c1185_base_owned_remainder_in_scope_line():
        R_, L_ = f"00CA85:{BASE}", "LaterUndo.esp"
        mh_ = lambda x: {"MaxHeight.CellData": x}  # noqa: E731
        p_vals = [(BASE, "00"), (MOD_A, "0A"), (L_, "00")]
        w_vals = [(BASE, "00"), (MOD_A, "0A"), (PATCH, "0A"), (L_, "00")]
        arms = [arm("baseline", [p_ for p_, _ in w_vals], kind="baseline"), arm("without-patch", [p_ for p_, _ in p_vals])]
        r_ = lost_row(MOD_A, L_, R_, "MaxHeight", ["MaxHeight.CellData=0A"], ["MaxHeight.CellData=00"],
                      ["MaxHeight.CellData=00"])
        r_["Summary"] = f"Worldspace Tamriel: MaxHeight - {MOD_A}'s value lost to {L_}'s"
        plan, md, _ = core_bundle([r_], [r_], arms=arms,
                                  p_chains=[(R_, "Worldspace", [(p_, mh_(v_)) for p_, v_ in p_vals])],
                                  w_chains=[(R_, "Worldspace", [(p_, mh_(v_)) for p_, v_ in w_vals])],
                                  masters={MOD_A: [BASE], L_: [BASE], PATCH: [BASE, MOD_A]})
        val_ = (reader(plan).get("validation") or {}).get("sentences") or []
        sc_ = [x for x in val_ if "supplied patch edit" in x]
        return any(f"still there with {L_}" in x and "a supplied patch edits" in x for x in sc_), " | ".join(sc_)[:500] or str(val_)[:500]

    # The measured-load-order line of record fields a declared patch's value wins. R1: ModA's and ModB's own changes lost to the patch (1 field, 2 plugin-pair rows), listed by both record
    # checks. R2: OtherMod only carries the base game's value there, lost to the patch.
    def patch_wins_line_bundle():
        R0, R1, R2 = f"00C090:{BASE}", f"00C190:{BASE}", f"00C290:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        a_ = lost_row(MOD_A, PATCH, R1, "Owner", "a", "p", "v", before=BASE)
        b_ = lost_row(MOD_B, PATCH, R1, "Owner", "b", "p", "v", before=BASE)
        o_ = lost_row(OTHER, PATCH, R2, "Owner", "1", "p", "1", before=BASE)
        chains = [(R1, "PlacedObject", [(BASE, {"Owner": "v"}), (MOD_A, {"Owner": "a"}), (MOD_B, {"Owner": "b"}),
                                        (PATCH, {"Owner": "p"})]),
                  (R2, "PlacedObject", [(BASE, {"Owner": "1"}), (OTHER, {"Owner": "1"}), (PATCH, {"Owner": "p"})])]
        enr = {(RECORDS, "without-patch"): [lost_row(MOD_B, MOD_A, R0, "Owner", "b", "a", "v")],
               (RECORDS, "baseline"): [a_, b_, o_], (CELLATTR, "without-patch"): [], (CELLATTR, "baseline"): [a_, b_]}
        tr = {(RECORDS, "baseline"): records_transcript(chains), (CELLATTR, "baseline"): records_transcript(chains[:1])}
        return compile_and_render(arms, {(CONTACT, a2_): [] for a2_ in ("baseline", "without-patch")}, [MOD_A, MOD_B],
                                  patches=[PATCH], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                  enrichment=enr, transcripts=tr,
                                  masters={OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, OTHER, MOD_A, MOD_B]})

    def patch_wins_lines(md):
        return [x for x in md.splitlines() if "where a declared patch's value wins" in x]

    # C1190: a losing value the losing plugin only carries is no change of its own:
    # not counted as lost, said apart; a field two record checks list is counted once, the second line saying so.
    def c1190_patch_wins_line_carried_and_once():
        plan, md, _ = get("patch-wins-line", patch_wins_line_bundle)
        ls = patch_wins_lines(md)
        return (len(ls) == 2 and any(re.search(r"\b1 record field\b", x) and "only carried" in x for x in ls)  # wording: page budget
                and any("1 of the fields also counted above" in x for x in ls)), " || ".join(ls)[:700]   # round 3: its unit named

    # C1189 (eighth review, Winterhold 154 / 159): the line gives its rows beside its fields when they differ (a field
    # two plugins lose to the patch is one field, two rows), so the count matches what its links hold.
    def c1189_patch_wins_line_gives_rows():
        plan, md, _ = get("patch-wins-line", patch_wins_line_bundle)
        ls = patch_wins_lines(md)
        return any("2 rows, one per record field and plugin pair" in x for x in ls), " || ".join(ls)[:700]   # round 3 wording

    # C1188 (eighth review, Winterhold 88 / 145): the detail's note of pairs still crossing where a patch places one says,
    # with two evaluated mods, how many are counted at the top and how many were already there with one mod alone, so it
    # reconciles with the patch comparison's count. The record is rendered again with one of the two pairs marked as
    # already there with one mod alone (the attribution the prediction gives such a pair).
    def c1188_placed_note_splits_counted_and_alone():
        X = f"000X88:{MOD_A}"
        ys = [f"000Y8{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        extra = row("OVERLAP", OTHER, MOD_A, f"000Z88:{MOD_A}", other=f"000W88:{OTHER}")
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds"} for y in ys]
        other_ = f"00C988:{BASE}"
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", [(MOD_A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, _, _ = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): [extra]},
                                        [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                        enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()},
                                        transcripts=tr)
        rec = [e for e in (reader(plan).get("prediction") or {}).get("record") or []
               if e.get("formKey") == X and e.get("withPatch") == "patchPlaced"]
        if len(rec) != 2:
            return False, f"fixture: {[(e.get('formKey'), e.get('withPatch')) for e in rec]}"
        rec[0]["attribution"], rec[1]["attribution"] = "needs-both", "single-mod"
        pr_ = priorities()
        with tempfile.TemporaryDirectory() as td:
            view = R.effective_view(plan, 3, 1, pr_, "normal")
            md = R.render_all(plan, view, Path(td) / "out", "")["report.md"]
        ln = next((x for x in md.splitlines() if "still crossing where a patch places one of the objects" in x), "")
        return ("2 overlap pairs between" in ln and "1 counted at the top" in ln
                and "1 already there with one mod alone" in ln), ln[-400:] or md[-800:]

    # C1193 (eighth review, Missives 67 / 149, Pilgrim 28 / 63 / 142): the set-aside line names its unit (rows of the
    # measured load order), so it reads apart from the evidence index's results counted across every setup. Wording held
    # in skill data only: guarded by this test.
    def c1193_set_aside_line_names_its_unit():
        arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, f"000G93:{MOD_A}"),
                                                                       row("CONTACT", BASE, MOD_A, f"000G94:{MOD_A}")],
                                                (CONTACT, "with-patch"): []}, [MOD_A, MOD_B], patches=[PATCH])
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        return ("rows of the measured load order" in line and "1 with the base game" in line), line[:300]

    # C1184 (eighth review, Snazzy 14 / 63 / 64): a by-object item whose objects earlier items already count names, per
    # object, the item that counted it first: X under the support check, Y under the record item, never all under one.
    def c1184_counted_once_names_each_first_item():
        X, Y = f"000X84:{BASE}", f"000Y84:{BASE}"
        # (the items run largest first, as in the report the review read: records, then support, then fixtures)
        p_res = {(CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, X, other=f"000H{i}4:{MOD_B}") for i in (1, 2, 3)],
                 (FIXTURE, "baseline"): [row("FIXTURE", MOD_B, MOD_A, X, other=f"000H85:{MOD_B}"),
                                         row("FIXTURE", MOD_B, MOD_A, Y, other=f"000H86:{MOD_B}")],
                 (CONTACT, "with-patch"): [], (FIXTURE, "with-patch"): []}
        enr = {(RECORDS, "baseline"): [lost_row(MOD_A, MOD_B, r_, "Owner", "a", "b", "v")
                                       for r_ in (Y, f"000R84:{BASE}", f"000R85:{BASE}", f"000R86:{BASE}", f"000R87:{BASE}")],
               (RECORDS, "with-patch"): []}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, p_res, [MOD_A], patches=[PATCH], enrichment=enr,
                                         count_only={k: max(1, len(v)) for k, v in enr.items()})
        titles = priorities()["reader"]["checkTitles"]
        kl = next((x for x in (reader(plan).get("prediction") or {}).get("withPatchLines") or []
                   if x.startswith(titles[FIXTURE])), "")
        return (titles[CONTACT] in kl and titles[RECORDS] in kl), kl[:400]

    # C1186 (eighth review, BPT 19): an item's examples say their own state: an example whose winning version sets the
    # object off for good is said so, never shown bare beside a sentence about other rows.
    def c1186_examples_say_off_for_good():
        D1, D2 = f"00C186:{BASE}", f"00C286:{BASE}"
        o_, a_, b_off, b_mv = offs(0), offs(5), offs(-30000), offs(40)
        p_rows = [lost_row(MOD_A, MOD_B, D1, "Placement", off_txt(a_), off_txt(b_off), off_txt(o_), before=BASE),
                  lost_row(MOD_A, MOD_B, D2, "Placement", off_txt(a_), off_txt(b_mv), off_txt(o_), before=BASE)]
        p_ch = [(D1, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_off)]),
                (D2, "PlacedObject", [(BASE, o_), (MOD_A, a_), (MOD_B, b_mv)])]
        plan, md, _ = core_bundle(p_rows, [], p_chains=p_ch, masters=MASTERS_)
        ex = next((m.group(0) for ln in md.splitlines() for m in [re.search(r"For example: .*?\)\.", ln)] if m and D1 in m.group(0)), "")
        return (re.search(re.escape(D1) + r" \([^)]*off for good", ex) is not None), ex[:300] or md[:1500]

    # C1191 (eighth review, Missives 23): a switch clash whose row says "the arriving host encloses the dependent N u
    # deep" says the dependent (the row's other object) sits N u inside, never the arriving host itself, with no depth.
    def c1191_enclosed_dependent_is_the_subject():
        H, D = f"000H91:{BASE}", f"000D91:{MOD_A}"
        st_ = row("STATE-CONTACT@000S91:Skyrim.esm@Unknown", MOD_B, MOD_A, H, label="Market (ext)", other=D)
        st_["Summary"] = (f"Contact StateConditional under StallSwitch (Skyrim.esm): {H} [Static MarketStall] arrives - "
                          f"Sunk: the arriving host encloses the dependent 6.8u deep in Market (ext)")
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B])]
        res = {(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch")}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        it = next((ln for ln in md.splitlines() if ln.startswith(f"- **{title}")), "")
        ig = it.split("In game", 1)[-1]
        return ("000D91" in ig and "6.8 u inside" in ig and "000H91" not in ig.split("inside", 1)[0].split(",", 1)[-1]), ig[:300]

    # C1187 (eighth review, Winterhold 19): a fixture whose named host is no floor or wall (skill data: the host's model
    # is outside the building and dungeon folders) is never said to hang because a floor or wall was removed; the item
    # says the host's kind with an example. Hosts whose model is a building piece keep the floor-or-wall sentence.
    def c1187_fixture_host_kind_from_model():
        F1, F2, H1, H2 = f"000F87:{MOD_A}", f"000F88:{MOD_A}", f"000H87:{BASE}", f"000H88:{BASE}"
        rows_ = [row("EMPTY-SPACE", MOD_B, MOD_A, F1, other=H1), row("EMPTY-SPACE", MOD_B, MOD_A, F2, other=H2)]
        refs = {F1: {"base": f"0B0F87:{MOD_A}"}, F2: {"base": f"0B0F87:{MOD_A}"}, H1: {"base": f"0B0B87:{BASE}"},
                H2: {"base": f"0B0W87:{BASE}"}}
        # model paths as the names enrichment records them (backslashes); the editor IDs point the wrong way on purpose,
        # so only the model can decide (Gate A condition d)
        bases = {f"0B0F87:{MOD_A}": {"type": "Static", "editorId": "WeaponRack", "model": "Clutter\\Rack.nif"},
                 f"0B0B87:{BASE}": {"type": "Static", "editorId": "FloorWallPiece", "model": "Clutter\\Barrel02.nif"},
                 f"0B0W87:{BASE}": {"type": "Static", "editorId": "ClutterBarrel",
                                    "model": "Architecture\\Farmhouse\\Wall01.nif"}}
        res = {(FIXTURE, "without-patch"): rows_, (FIXTURE, "baseline"): []}
        plan, md, _ = compile_and_render(WP_ARMS, res, [MOD_A], patches=[PATCH], names={"refs": refs, "bases": bases})
        it = next((g for g in (reader(plan).get("prediction") or {}).get("items") or [] if g.get("checkId") == FIXTURE), {})
        ig = str(it.get("inGameText") or "")
        return ("000H87" in ig and "000H88" not in ig and "1 of them" in ig
                and "floor or wall it was fixed to" not in ig), ig[:400]

    # C1192 (eighth review, Missives persistent cells): a place that is a worldspace's persistent cell names its
    # worldspace (the world-children group the cell is filed in, read from the cell's own plugin file, by its editor
    # ID), beside the grid cell of the object's position.
    def c1192_persistent_cell_names_its_world():
        W_, C_, R_ = 0x000E92, 0x000E93, 0x000E94

        def sub(sig, data):
            return sig.encode() + struct.pack("<H", len(data)) + data

        def rec(typ, fid, subs):
            data = b"".join(sub(s_, d_) for s_, d_ in subs)
            return typ.encode() + struct.pack("<IIIIHH", len(data), 0, fid, 0, 44, 0) + data

        def grup(label, gtype, body):
            lab = label if isinstance(label, bytes) else struct.pack("<I", label)
            return b"GRUP" + struct.pack("<I", 24 + len(body)) + lab + struct.pack("<IIHH", gtype, 0, 0, 0) + body
        refr = rec("REFR", R_, [("NAME", struct.pack("<I", 0x000E95)), ("DATA", struct.pack("<6f", 8200.0, 4100.0, 0, 0, 0, 0))])
        cell = rec("CELL", C_, [("DATA", struct.pack("<H", 0))])
        body = rec("WRLD", W_, [("EDID", b"TestWorld\x00")]) + grup(W_, 1, cell + grup(C_, 6, grup(C_, 8, refr)))
        hdr = sub("HEDR", struct.pack("<fII", 1.7, 3, 0x800))
        data = b"TES4" + struct.pack("<IIIIHH", len(hdr), 0, 0, 0, 44, 0) + hdr + grup(b"WRLD", 0, body)
        f_ = Path(tempfile.mkdtemp(prefix="r8-")) / BASE
        f_.write_bytes(data)
        ref_, cell_ = f"{R_:06X}:{BASE}", f"{C_:06X}:{BASE}"
        arms = with_files([arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, MOD_A, MOD_B])], {BASE: f_})
        rows_ = [row("OVERLAP", MOD_B, MOD_A, ref_, label=f"{cell_} (ext)", other=f"000E96:{MOD_B}")]
        res = {(OVERLAP, "without-patch"): rows_, (OVERLAP, "baseline"): []}
        names_ = {"refs": {ref_: {"base": f"000E95:{BASE}", "carriedBy": [BASE]},
                           f"000E96:{MOD_B}": {"base": f"000E97:{MOD_B}", "carriedBy": [MOD_B]}},
                  "bases": {f"000E95:{BASE}": {"type": "Static", "editorId": "Rock", "model": "Landscape\\Rock.nif"},
                            f"000E97:{MOD_B}": {"type": "Static", "editorId": "Crate", "model": "Clutter\\Crate.nif"}}}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH], names=names_)
        ln = next((x for x in md.splitlines() if x.startswith("- **") and cell_ in x), "")
        return (f"{cell_} (ext) in TestWorld, grid cell 2, 1" in ln), ln[:500] or md[:800]

    # C1194 (eighth review, Sewers 27): a switch clash whose direction the check did not find is settled by the records
    # where they settle it: the object's enable parent is the switch, not opposite, and the switch is not initially
    # disabled and has no parent of its own, so the object is there from load and the overlap ends when the switch
    # turns it off; the line says so and names the other object, never "which way was not found".
    def c1194_switch_direction_from_records():
        X = f"000X94:{MOD_A}"
        C_, P_ = f"000E01:{MOD_B}", f"000E02:{MOD_B}"
        f_ = plugin_file_of(MOD_B, [BASE], [
            ("REFR", 0x01000E01, [("NAME", struct.pack("<I", 0x01000E03)), ("DATA", struct.pack("<6f", 0, 0, 0, 0, 0, 0)),
                                  ("XESP", struct.pack("<IB", 0x01000E02, 0))], False),
            ("REFR", 0x01000E02, [("NAME", struct.pack("<I", 0x01000E04)), ("DATA", struct.pack("<6f", 9, 9, 0, 0, 0, 0))],
             False)], group="CELL")
        st_ = row(f"STATE-CONTACT@{P_}@Unknown", MOD_B, MOD_A, C_, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {C_} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip - hung from the host as authored (0u gap); under the winner it sits "
                          f"26.9u inside something else in Camp (ext)")
        arms = with_files([arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, MOD_A, MOD_B])], {MOD_B: f_})
        res = {(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch")}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        it = next((ln for ln in md.splitlines() if ln.startswith(f"- **{title}")), "")
        ig = it.split("In game", 1)[-1]
        return ("which way was not found" not in ig and "from the moment the game loads" in ig and "000X94" in ig
                and "26.9 u" in ig), ig[:400]

    # C1195 / C1204 (eighth review, Sewers 30; Gate A condition b): a base-size navmesh the winner puts back where the
    # losing mod's own version is a single triangle says, from each setup's winning version, what it means: without the
    # patch NPCs path on the winner's base-size navmesh; with the patch the winning version is read again, and the line
    # says it is a single triangle again (C1195) or still base-size (C1204), whichever the files show.
    def stub_put_back_bundle(patch_tris):
        n1 = f"000E95:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="TempleInterior (int)  1 tris -> 600")]
        p_rows[0]["Necessary"][0]["Detail"] = "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u"
        arms_ = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E95, [("NVNM", nvnm(600, []))], False)]),
               MOD_A: plugin_file_of(MOD_A, [BASE], [("NAVM", 0x00000E95, [("NVNM", nvnm(1, []))], False)]),
               MOD_B: plugin_file_of(MOD_B, [BASE], [("NAVM", 0x00000E95, [("NVNM", nvnm(600, []))], False)]),
               PATCH: plugin_file_of(PATCH, [BASE], [("NAVM", 0x00000E95, [("NVNM", nvnm(patch_tris, []))], False)])}
        with_files(arms_, fs_)
        plan, md, html = compile_and_render(arms_, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH])
        return " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])

    def c1195_stub_put_back_meaning_patch_stub():
        top = stub_put_back_bundle(1)
        return ("NPCs there path on ModB.esp's navmesh" in top and "a single triangle again" in top
                and "still has" not in top), top[-600:]

    def c1204_stub_put_back_meaning_patch_full():
        top = stub_put_back_bundle(600)
        return ("NPCs there path on ModB.esp's navmesh" in top and "still has 600 triangles" in top
                and "single triangle again" not in top), top[-600:]

    # C1196 (eighth review, Sewers 39): where the other version's setup finds none of a counted group, the line says it
    # in plain words (with that version that group is not found), not only as "0 of the N".
    def c1196_also_variant_says_what_it_lacks():
        X1, X2, T_ = f"000X96:{MOD_A}", f"000X97:{MOD_A}", f"000T96:{OTHER}"
        rows_ = [row("OVERLAP", OTHER, MOD_A, x_, other=T_) for x_ in (X1, X2)]
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-other", [BASE, MOD_A])]
        v_arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline", shas={MOD_A: "b" * 64})]
        v_res = {(OVERLAP, "baseline"): [row("OVERLAP", OTHER, MOD_A, f"000X98:{MOD_A}", other=f"000T98:{OTHER}")]}
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): list(rows_), (OVERLAP, "without-patch"): rows_,
                                                (OVERLAP, "without-other"): []}, [MOD_A], patches=[PATCH],
                                         also=[(v_arms, v_res, [MOD_A], {})])
        also_ = " ".join((reader(plan).get("prediction") or {}).get("alsoEvaluations") or [])
        return (f"With that version, the 2 overlap pairs between {OTHER} and {MOD_A} are not found" in also_), also_[-400:]

    # C1198 (eighth review, Pilgrim 27): where the asset reads report every archive read (no archive failure), a path the
    # evaluated mod's records name and no file in the setup provides is said as searched in its folders and archives, the
    # mod's own included, never "may be missing from this install"; with an archive read failure the hedge stays.
    def c1198_missing_asset_says_what_was_searched():
        MA = "loadability.missing-assets"

        def call(i, name, args):
            return {"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": name, "arguments": args}}

        def answer(i, doc):
            return {"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(doc)}]}}

        def line_with(failures):
            req = [{"batchMarker": "b6-models"}, call(1, "housecarl_records", {"fields": ["Model.File"]}),
                   {"batchMarker": "b7-assets"}, call(1, "housecarl_asset_status", {"asset_paths": ["x"]})]
            resp = [{"batchMarker": "b6-models"},
                    answer(1, {"matches": [{"source": MOD_A, "fields": [{"path": "Model.File", "value": "Arch\\House98.nif"}]}]}),
                    {"batchMarker": "b7-assets"},
                    answer(1, {"results": [{"path": "meshes/Arch/House98.nif", "exists": False}],
                               "bsa_failures": failures, "bsa_failures_omitted": 0})]
            results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
            plan, md, _ = compile_and_render(PATCHED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                             count_only={(MA, "baseline"): 1, (MA, "without-patch"): 1},
                                             transcripts={(MA, "baseline"): (req, resp), (MA, "without-patch"): (req, resp)})
            return next((ln for ln in md.splitlines() if "House98.nif" in ln), "")
        ok_, bad_ = line_with([]), line_with(["Broken.bsa: unreadable"])
        return ("may be missing from this install" not in ok_ and "archives" in ok_
                and "may be missing from this install" in bad_), f"{ok_[-250:]} || {bad_[-200:]}"

    # C1199 (eighth review, Pilgrim 31): the load-order line applies the prediction's set-aside rules: a water height on
    # an interior cell neither plugin's version of which has water is no change in game and is not counted (1 of 2).
    def c1199_order_line_sets_aside_no_water():
        C1, C2 = 0x00000E99, 0x00000E9A
        fo_ = plugin_file_of(OTHER, [BASE], [("CELL", C1, [("DATA", struct.pack("<H", 0x0001))], False),
                                             ("CELL", C2, [("DATA", struct.pack("<H", 0x0003))], False)], group="CELL")
        fa_ = plugin_file_of(MOD_A, [BASE], [("CELL", C1, [("DATA", struct.pack("<H", 0x0001))], False),
                                             ("CELL", C2, [("DATA", struct.pack("<H", 0x0003))], False)], group="CELL")
        files_ = {OTHER: fo_, MOD_A: fa_}
        shas_ = {n_: hashlib.sha256(Path(f_).read_bytes()).hexdigest() for n_, f_ in files_.items()}
        arms = with_files([arm("baseline", [BASE, MOD_A, OTHER], kind="baseline")], files_)
        p_rec = [lost_row(MOD_A, OTHER, f"000C99:{BASE}", "Lighting", "a", "o", "v")]
        o_rows = [dict(lost_row(OTHER, MOD_A, f"{C1:06X}:{BASE}", "WaterHeight", "0", "-2147483600", "-2147483600"),
                       _check=CELLATTR, _type="Cell"),
                  dict(lost_row(OTHER, MOD_A, f"{C2:06X}:{BASE}", "WaterHeight", "0", "-2147483600", "-2147483600"),
                       _check=CELLATTR, _type="Cell")]
        pl_ = plugins(BASE, OTHER, MOD_A, shas=shas_)
        setup = {"armId": "other-first", "planPath": "order/plan.json", "rowsPath": "order/rows.json",
                 "plugins": pl_, "rows": o_rows}
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): []}, [MOD_A],
                                         count_only={(CELLATTR, "baseline"): 1}, enrichment={(CELLATTR, "baseline"): p_rec},
                                         order_setups=[setup])
        lines = [x["text"] for x in (reader(plan).get("prediction") or {}).get("orderRiskLines") or []]
        return (len(lines) == 1 and f"1 of {OTHER}'s record fields" in lines[0]), str(lines)[:400]

    # C1201 (eighth review, the standing nudge set-aside): a report with a pair whose base-game object another mod only
    # nudges says once, plainly, how such pairs are counted and why; the count does not change.
    def c1201_nudge_counting_said_once():
        near_ = (0x00CA01, (100.0, 100.0, 0.0, 1.5, 0x0B0A79), (102.0, 100.0, 0.0, 1.5, 0x0B0A79))
        md_, it_ = nudge_bundle([near_])
        n_ = md_.count("keeping its turn, is counted as a clash between")
        return (n_ == 1 and "1 overlap pair" in it_), f"{n_} || {it_[:200]}"

    # C1200 (eighth review, G1; a guard, accepted at Gate A): a patch that holds only the cell record a geometry remainder
    # stands in edits none of its objects: the remainder is said as on objects no supplied patch edits. Passes on the
    # code before this task: it pins the rule, it is no red.
    def c1200_cell_record_alone_is_not_scope():
        ln = scope_bundle(False, cell_only=True)
        return (f"still there with Neighbour.esp and {OTHER} lies on records, navmeshes or objects that no supplied patch "
                f"edits" in ln and "a supplied patch edits" not in ln.replace("no supplied patch edits", "")), ln[:500]

    # C1205 (eighth review, page budget; the coordinator's lever i): at the ladder's last step (layout with "aside") the
    # pointer to the evidence index's further notes is one short sentence with no heading of its own; before that
    # step the "Further notes" section keeps its heading.
    def c1205_last_step_tail_pointer_has_no_heading():
        plan, _, _ = core_bundle([lost_row(MOD_A, MOD_B, f"00C205:{BASE}", "Owner", "a", "b", "v")], [])
        pr_ = priorities()
        out_ = {}
        for lay_ in ("compact tail order", "compact tail order aside"):
            with tempfile.TemporaryDirectory() as td:
                view = R.effective_view(plan, 3, 1, pr_, lay_)
                o_ = R.render_all(plan, view, Path(td) / "out", "")
                out_[lay_] = (o_["report.md"], o_["report.html"])
        md4, html4 = out_["compact tail order aside"]
        md3, _ = out_["compact tail order"]
        return ("## Further notes" in md3 and "## Further notes" not in md4 and "<h2>Further notes</h2>" not in html4
                and "Everything else is in the evidence index." in md4), md4[-300:]

    # ---------------------------------------------------------------- eighth review, round 2 (2026-10-24)
    # C1206 (round 2 item 1, Pilgrim 63; a regression of C1193): the set-aside line's unit is the prediction setup by its own
    # label; in a bundle whose measured load order is the setup with the patch, it is not "the measured load order".
    def c1206_set_aside_unit_names_prediction_setup():
        plan, md, _ = compile_and_render(PATCHED_ARMS, {(CONTACT, "baseline"): [],
                                                        (CONTACT, "without-patch"): [row("CONTACT", MOD_B, MOD_A, f"000G06:{MOD_A}"),
                                                                                     row("CONTACT", BASE, MOD_A, f"000G07:{MOD_A}")]},
                                         [MOD_A, MOD_B], patches=[PATCH])
        line = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        return ("in rows of the measured load order" not in line and "in rows of the setup without the patch" in line), line[:300]

    # C1207 (round 2 item 3, Clockwork 139): a losing value that is the base game's original (absent), put back over the
    # loser's master's change, is owned by the base game: the loser only carries it, so the patch-wins line counts nothing
    # of the loser's own there.
    def c1207_patch_wins_base_owned_loser_is_carried():
        R1 = f"00C207:{BASE}"
        arms = [arm("baseline", [BASE, UPD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, MOD_A, MOD_B])]
        a_ = lost_row(MOD_A, PATCH, R1, "MaxHeight", ["MaxHeight: ABSENT here"], ["MaxHeight.CellData=0C"],
                      ["MaxHeight.CellData=0U"], before=UPD)
        chains = [(R1, "Worldspace", [(BASE, {"MaxHeight": "has MaxHeight ABSENT"}), (UPD, {"MaxHeight.CellData": "0U"}),
                                      (MOD_A, {"MaxHeight": "has MaxHeight ABSENT"}), (PATCH, {"MaxHeight.CellData": "0C"})])]
        enr = {(RECORDS, "without-patch"): [lost_row(MOD_B, MOD_A, f"00C208:{BASE}", "Owner", "b", "a", "v")],
               (RECORDS, "baseline"): [a_]}
        plan, md, _ = compile_and_render(arms, {(CONTACT, x_): [] for x_ in ("baseline", "without-patch")}, [MOD_A, MOD_B],
                                         patches=[PATCH], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                         enrichment=enr, transcripts={(RECORDS, "baseline"): records_transcript(chains)},
                                         masters={UPD: [BASE], MOD_A: [BASE, UPD], MOD_B: [BASE], PATCH: [BASE, UPD, MOD_A]})
        ls = [x for x in md.splitlines() if "where a declared patch's value wins" in x]
        return (len(ls) == 1 and "1 more only carried by the loser" in ls[0]), " || ".join(ls)[:500]

    # C1208 (round 2 item 4, Snazzy 7): the headline's shared-objects phrase names the earlier items that count them, as
    # the kind line does, never only the check that found them.
    def c1208_headline_same_objects_names_items():
        X, Y = f"000X08:{BASE}", f"000Y08:{BASE}"
        p_res = {(CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, X, other=f"000H{i}8:{MOD_B}") for i in (1, 2, 3)],
                 (FIXTURE, "baseline"): [row("FIXTURE", MOD_B, MOD_A, X, other=f"000H58:{MOD_B}"),
                                         row("FIXTURE", MOD_B, MOD_A, Y, other=f"000H68:{MOD_B}")],
                 (CONTACT, "with-patch"): [], (FIXTURE, "with-patch"): []}
        enr = {(RECORDS, "baseline"): [lost_row(MOD_A, MOD_B, r_, "Owner", "a", "b", "v")
                                       for r_ in (Y, f"000R08:{BASE}", f"000R18:{BASE}", f"000R28:{BASE}", f"000R38:{BASE}")],
               (RECORDS, "with-patch"): []}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, p_res, [MOD_A], patches=[PATCH], enrichment=enr,
                                         count_only={k: max(1, len(v)) for k, v in enr.items()})
        titles = priorities()["reader"]["checkTitles"]
        v_ = str((reader(plan).get("prediction") or {}).get("verdict") or "")
        return (f'counted once, above, under "{titles[CONTACT]}" (1) and "{titles[RECORDS]}" (1)' in v_), v_[-400:]

    # C1209 (round 2 item 5, Snazzy 70 / Winterhold 91): the objects switched back on are counted against the whole line,
    # never read as a subset of the off-for-good count before them.
    def c1209_switched_on_counted_of_the_line():
        p_rows = [lost_row(MOD_A, MOD_B, f"000709:{BASE}", "LocationReference", "a1", "b1", "v")]
        w_rows = [lost_row(MOD_A, PATCH, f"000719:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0"),
                  lost_row(MOD_B, PATCH, f"000729:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                           "Position=4,5,6; Rotation=0,0,0", "Position=4,5,6; Rotation=0,0,0")]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        plan, md, _ = compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []},
                                         [MOD_A, MOD_B], patches=[PATCH],
                                         count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 2}, enrichment=enr)
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        return ("1 of the 2, on 1 object, switch the object back on" in line and "1 of them" not in line), line[:400]

    # C1210 (round 2 item 6, Winterhold 154 / 159): the patch-wins line says how its rows relate to its fields.
    def c1210_patch_wins_rows_relation_said():
        plan, md, _ = get("patch-wins-line", patch_wins_line_bundle)
        ls = patch_wins_lines(md)
        # (round 3, review12 S1) a row is one record field in one plugin pair
        return any("2 rows, one per record field and plugin pair" in x for x in ls), " || ".join(ls)[:600]

    # C1211 (round 2 item 7, Sewers 39): with one evaluated mod the other version is compared on every counted group,
    # those already there with the mod alone too, and a counted check that version did not measure is named.
    def c1211_also_variant_every_counted_group():
        X1, T_, X3, U_ = f"000X11:{MOD_A}", f"000T11:{OTHER}", f"000X13:{MOD_A}", f"000U13:{BASE}"
        pair_ = row("OVERLAP", OTHER, MOD_A, X1, other=T_)
        alone_ = row("OVERLAP", OTHER, MOD_A, X3, other=U_)       # OtherMod only edits the base-game partner U
        alone_b_ = row("OVERLAP", BASE, MOD_A, X3, other=U_)      # the same pair without OtherMod: from ModA alone
        st_ = row("STATE-CONTACT@000S11:ModA.esp@Unknown", MOD_A, OTHER, f"000X15:{MOD_A}", label="Camp (ext)",
                  other=f"000T15:{OTHER}")
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-other", [BASE, MOD_A])]
        v_arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline", shas={MOD_A: "b" * 64})]
        v_res = {(OVERLAP, "baseline"): [pair_, row("OVERLAP", OTHER, MOD_A, f"000X19:{MOD_A}", other=f"000T19:{OTHER}")]}
        res = {(OVERLAP, "baseline"): [pair_, alone_], (OVERLAP, "without-patch"): [pair_, alone_],
               (OVERLAP, "without-other"): [alone_b_],
               (STATE, "baseline"): [st_], (STATE, "without-patch"): [st_], (STATE, "without-other"): []}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH], also=[(v_arms, v_res, [MOD_A], {})])
        also_ = " ".join((reader(plan).get("prediction") or {}).get("alsoEvaluations") or [])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        return ("of the 2 overlap pairs between OtherMod.esp and ModA.esp" in also_ and f'"{title}"' in also_
                and "not measured with that version" in also_.lower()), also_[-500:]

    # C1212 (round 2 item 8, Pilgrim 74): a set-aside row on a field an admitted predicted row already counts is not
    # counted again among the clashes resolved but not predicted.
    def c1212_resolved_not_predicted_counts_a_field_once():
        R1 = f"00C212:{BASE}"
        p_rows = [lost_row(OTHER, MOD_A, R1, "Owner", "o", "a", "v", before=BASE),
                  lost_row(THIRD, MOD_A, R1, "Owner", "o", "a", "o", before=OTHER)]
        chains = [(R1, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (THIRD, {"Owner": "o"}),
                                        (MOD_A, {"Owner": "a"})])]
        arms_ = [arm("baseline", [BASE, OTHER, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, OTHER, THIRD, MOD_A, MOD_B])]
        plan, md, _ = core_bundle(p_rows, [], arms=arms_, p_chains=chains,
                                  masters={OTHER: [BASE], THIRD: [BASE, OTHER], MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE]})
        adm = sorted(e["admission"] for e in rec_of(plan, R1))
        spots = (reader(plan).get("validation") or {}).get("blindSpots") or []
        return (adm == ["admitted", "notOwnEdit"] and not any(x.get("admission") == "notOwnEdit" for x in spots)), f"{adm} {spots}"

    # C1213 (round 2 item 2, Missives 67 / 149): the detail's set-aside count says what it counts (results, one check's
    # finding per pair of plugins in one setup), apart from the rows the set-aside line counts.
    def c1213_detail_aside_says_its_unit():
        arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        plan, md, _ = compile_and_render(arms, {(CONTACT, "baseline"): [row("CONTACT", MOD_B, MOD_A, f"000G13:{MOD_A}"),
                                                                       row("CONTACT", BASE, MOD_A, f"000G14:{MOD_A}")],
                                                (CONTACT, "with-patch"): [row("CONTACT", BASE, MOD_A, f"000G14:{MOD_A}")]},
                                         [MOD_A, MOD_B], patches=[PATCH])
        # (page budget) said with the rows' own count, in the set-aside line, so both counts read together
        ln = (reader(plan).get("prediction") or {}).get("setAsideLine") or ""
        return ("(the further results listed in the detail count one per check, plugin pair and setup)" in ln), ln[:400]

    # C1214 (round 3 item 1, review12 S1 and finding 7): the rows include rows the line does not count (a field the
    # losing plugin only carries); the line splits them, in one unit, so the parts add up to the whole.
    def c1214_patch_wins_rows_say_how_many_counted():
        R1, R3 = f"00C214:{BASE}", f"00C314:{BASE}"
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B])]
        a_ = lost_row(MOD_A, PATCH, R1, "Owner", "a", "p", "v", before=BASE)
        b_ = lost_row(MOD_B, PATCH, R1, "Owner", "b", "p", "v", before=BASE)
        c_ = lost_row(MOD_A, PATCH, R3, "Owner", "v", "p", "v", before=BASE)     # ModA only carries the base value
        chains = [(R1, "PlacedObject", [(BASE, {"Owner": "v"}), (MOD_A, {"Owner": "a"}), (MOD_B, {"Owner": "b"}),
                                        (PATCH, {"Owner": "p"})]),
                  (R3, "PlacedObject", [(BASE, {"Owner": "v"}), (MOD_A, {"Owner": "v"}), (PATCH, {"Owner": "p"})])]
        enr = {(RECORDS, "without-patch"): [lost_row(MOD_B, MOD_A, f"00C414:{BASE}", "Owner", "b", "a", "v")],
               (RECORDS, "baseline"): [a_, b_, c_]}
        plan, md, _ = compile_and_render(arms, {(CONTACT, a2_): [] for a2_ in ("baseline", "without-patch")},
                                         [MOD_A, MOD_B], patches=[PATCH],
                                         count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr,
                                         transcripts={(RECORDS, "baseline"): records_transcript(chains)},
                                         masters={MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE, MOD_A, MOD_B]})
        ls = patch_wins_lines(md)
        # (round 3, finding 7) every figure in rows, the parts adding up to the whole; no second unit beside them
        return any("(3 rows, one per record field and plugin pair: 2 counted, 1 only carried by the loser)" in x
                   for x in ls), " || ".join(ls)[:600]

    # C1215 (round 3 item 2, review12 S2): a check the other version ran once per setup (its receipt records no part)
    # is read and compared like the others, never called not measured.
    def c1215_also_variant_reads_single_run_checks():
        X1, T_ = f"000X21:{MOD_A}", f"000T21:{OTHER}"
        pair_ = row("OVERLAP", OTHER, MOD_A, X1, other=T_)
        st_ = row("STATE-CONTACT@000S21:ModA.esp@Unknown", MOD_A, OTHER, f"000X25:{MOD_A}", label="Camp (ext)",
                  other=f"000T25:{OTHER}")
        arms = [arm("baseline", [BASE, OTHER, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, OTHER, MOD_A]),
                arm("without-other", [BASE, MOD_A])]
        v_arms = [arm("baseline", [BASE, OTHER, MOD_A], kind="baseline", shas={MOD_A: "b" * 64})]
        v_res = {(OVERLAP, "baseline"): [pair_], (STATE, "baseline"): []}
        res = {(OVERLAP, "baseline"): [pair_], (OVERLAP, "without-patch"): [pair_], (OVERLAP, "without-other"): [],
               (STATE, "baseline"): [st_], (STATE, "without-patch"): [st_], (STATE, "without-other"): []}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH],
                                         also=[(v_arms, v_res, [MOD_A], {"single_run": (STATE,)})])
        also_ = " ".join((reader(plan).get("prediction") or {}).get("alsoEvaluations") or [])
        # the unit agrees with its count (Sewers 39 at regen-r8g read "0 of the 1 clashes")
        # a kind with no unit of its own names its check, so two checks' groups between one pair read apart
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        return (f'0 of the 1 clash of the check "{title}" between ModA.esp and OtherMod.esp' in also_
                and "1 clashes" not in also_
                and "not measured with that version" not in also_.lower()), also_[-500:]

    for name, fn in [("round3-8/C1214-patch-wins-rows-say-how-many-counted", c1214_patch_wins_rows_say_how_many_counted),
                     ("round3-8/C1215-also-variant-reads-single-run-checks", c1215_also_variant_reads_single_run_checks)]:
        cell(name, fn)

    for name, fn in [("round2-8/C1206-set-aside-unit-names-prediction-setup", c1206_set_aside_unit_names_prediction_setup),
                     ("round2-8/C1207-patch-wins-base-owned-loser-is-carried", c1207_patch_wins_base_owned_loser_is_carried),
                     ("round2-8/C1208-headline-same-objects-names-items", c1208_headline_same_objects_names_items),
                     ("round2-8/C1209-switched-on-counted-of-the-line", c1209_switched_on_counted_of_the_line),
                     ("round2-8/C1210-patch-wins-rows-relation-said", c1210_patch_wins_rows_relation_said),
                     ("round2-8/C1211-also-variant-every-counted-group", c1211_also_variant_every_counted_group),
                     ("round2-8/C1212-resolved-not-predicted-counts-a-field-once", c1212_resolved_not_predicted_counts_a_field_once),
                     ("round2-8/C1213-detail-aside-says-its-unit", c1213_detail_aside_says_its_unit)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1205-last-step-tail-pointer-has-no-heading", c1205_last_step_tail_pointer_has_no_heading)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1200-cell-record-alone-is-not-scope", c1200_cell_record_alone_is_not_scope)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1201-nudge-counting-said-once", c1201_nudge_counting_said_once)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1199-order-line-sets-aside-no-water", c1199_order_line_sets_aside_no_water)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1198-missing-asset-says-what-was-searched", c1198_missing_asset_says_what_was_searched)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1196-also-variant-says-what-it-lacks", c1196_also_variant_says_what_it_lacks)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1195-stub-put-back-meaning-patch-stub", c1195_stub_put_back_meaning_patch_stub),
                     ("eighth-review/C1204-stub-put-back-meaning-patch-full", c1204_stub_put_back_meaning_patch_full)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1194-switch-direction-from-records", c1194_switch_direction_from_records)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1192-persistent-cell-names-its-world", c1192_persistent_cell_names_its_world)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1187-fixture-host-kind-from-model", c1187_fixture_host_kind_from_model)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1191-enclosed-dependent-is-the-subject", c1191_enclosed_dependent_is_the_subject)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1186-examples-say-off-for-good", c1186_examples_say_off_for_good)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1184-counted-once-names-each-first-item", c1184_counted_once_names_each_first_item)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1193-set-aside-line-names-its-unit", c1193_set_aside_line_names_its_unit)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1188-placed-note-splits-counted-and-alone", c1188_placed_note_splits_counted_and_alone)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1190-patch-wins-line-carried-and-once", c1190_patch_wins_line_carried_and_once),
                     ("eighth-review/C1189-patch-wins-line-gives-rows", c1189_patch_wins_line_gives_rows)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1185-base-owned-remainder-in-scope-line", c1185_base_owned_remainder_in_scope_line)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1197-removing-brings-back-switched-too", c1197_removing_brings_back_switched_too)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1183-none-held-names-the-winner", c1183_none_held_names_the_winner)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1182-summary-says-record-effects", c1182_summary_says_record_effects)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1203-winner-value-from-only-in", c1203_winner_value_from_only_in)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1202-unread-value-not-called-replacement", c1202_unread_value_not_called_replacement)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1180-base-put-back-over-carried-change", c1180_base_put_back_over_carried_change)]:
        cell(name, fn)

    for name, fn in [("eighth-review/C1177-refused-comparison-rows-judged-by-remeasure",
                      c1177_refused_comparison_rows_judged_by_remeasure),
                     ("eighth-review/C1178-refused-line-gives-measured-gone", c1178_refused_line_gives_measured_gone),
                     ("eighth-review/C1179-patch-own-value-not-undone", c1179_patch_own_value_not_undone),
                     ("eighth-review/C1181-put-back-on-object-patch-sets-off", c1181_put_back_on_object_patch_sets_off)]:
        cell(name, fn)

    # ---------------------------------------- ninth review (blind reviews of regen-r8c / regen-r8f)
    def shared_new_object_bundle():
        """C1216-C1218. With the patch, X1 is left without support against two hosts and also hangs as a fixture against
        one; X2 is left without support against one host. Two objects, under two by-object checks."""
        X1, X2 = f"000X61:{MOD_A}", f"000X62:{MOD_A}"
        ct = [row("CONTACT", MOD_B, MOD_A, X1, other=f"000H61:{MOD_B}"), row("CONTACT", MOD_B, MOD_A, X1, other=f"000H62:{MOD_B}"),
              row("CONTACT", MOD_B, MOD_A, X2, other=f"000H63:{MOD_B}")]
        fx = [row("EMPTY-SPACE", MOD_A, MOD_B, X1, other=f"000H64:{MOD_B}")]
        return compile_and_render(PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): ct,
                                                     (FIXTURE, "baseline"): [], (FIXTURE, "with-patch"): fx},
                                  [MOD_A, MOD_B], patches=[PATCH])

    def stated_objects(text):
        """The objects a count line adds up: every "N object(s)" not said as already counted."""
        return sum(int(m_.group(1)) for m_ in re.finditer(r"\b(\d+) objects?(?![-\w])(?! already counted)", text))

    # C1216 (ninth review, BPT 68 / 76): an object two by-object checks find new with the patch is counted once: the
    # summary's and the new-clashes line's objects add up to the distinct objects, the later check's pairs said as on an
    # object already counted.
    def c1216_new_with_patch_object_counted_once():
        plan, md, _ = shared_new_object_bundle()
        val = reader(plan).get("validation") or {}
        head = str(val.get("summary", ""))
        new_part = head[head.rfind(" new "):] if " new " in head else head
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- New clashes in the setup with {PATCH}")), "")
        ok = (stated_objects(line) == 2 and stated_objects(new_part) == 2
              and "1 fixture-and-host pair on 1 object already counted" in line
              and "1 fixture-and-host pair on 1 object already counted" in new_part)
        return ok, f"line {line!r}; summary new part {new_part!r}"

    # C1217 (ninth review, BPT 79): the by-check line says a check's new object is one already counted under another.
    def c1217_new_by_check_names_shared_object():
        plan, md, _ = shared_new_object_bundle()
        fx_title = priorities()["reader"]["checkTitles"][FIXTURE]
        ct_title = priorities()["reader"]["checkTitles"][CONTACT]
        line = next((ln for ln in md.splitlines() if ln.startswith("- New clashes with the patch, by check")), "")
        seg = line[line.find(fx_title):] if fx_title in line else ""
        seg = seg[:seg.find(ct_title + " ")] if seg.find(ct_title + " ") > 0 else seg
        ok = f'the same object as one in "{ct_title}", counted once' in seg
        return ok, f"fixture segment {seg[:300]!r}"

    # C1218 (ninth review, BPT 68): the patch's own new-problem count (the install advice reads it) counts the object once.
    def c1218_patch_problems_count_object_once():
        plan, md, _ = shared_new_object_bundle()
        val = reader(plan).get("validation") or {}
        nb = {n["title"]: n["count"] for n in val.get("newByCheck") or []}
        return (val.get("patchProblems") == 2 and sorted(nb.values()) == [1, 2]), f"patchProblems {val.get('patchProblems')}; by check {nb}"

    # C1221 (ninth review, Winterhold F-039): a fixture whose winning version and whose removed host's winning version are
    # the same plugin's hangs as that plugin ships it: set aside, never a clash between the mods; a fixture whose host
    # another plugin's winning version removes stays counted.
    def c1221_fixture_own_layout_set_aside():
        FX, HO, FX2, HO2 = (f"000C21:{BASE}", f"000C22:{BASE}", f"000C23:{BASE}", f"000C24:{BASE}")
        dat = lambda z: ("DATA", struct.pack("<6f", 100.0, 200.0, z, 0.0, 0.0, 0.0))  # noqa: E731
        fa_ = plugin_file_of(MOD_A, [BASE], [("REFR", 0x00000C21, [("NAME", struct.pack("<I", 0x101)), dat(256.0)], False),
                                             ("REFR", 0x00000C22, [("NAME", struct.pack("<I", 0x102)), dat(-30000.0)], False),
                                             ("REFR", 0x00000C23, [("NAME", struct.pack("<I", 0x101)), dat(256.0)], False)],
                             group="CELL")
        fb_ = plugin_file_of(MOD_B, [BASE], [("REFR", 0x00000C22, [("NAME", struct.pack("<I", 0x102)), dat(-30000.0)], False),
                                             ("REFR", 0x00000C24, [("NAME", struct.pack("<I", 0x102)), dat(-30000.0)], False)],
                             group="CELL")
        # every version from the record's origin on is read (the base game's file too), as in a real bundle
        fs_ = plugin_file_of(BASE, [], [("REFR", 0x00000C21 + i_, [("NAME", struct.pack("<I", 0x101)), dat(0.0)], False)
                                        for i_ in range(4)], group="CELL")
        arms_ = with_files([arm("baseline", [BASE, MOD_B, MOD_A], kind="baseline"), arm("without-a", [BASE, MOD_B]),
                            arm("without-b", [BASE, MOD_A])], {MOD_A: fa_, MOD_B: fb_, BASE: fs_})
        rows_ = [row("EMPTY-SPACE", MOD_A, MOD_B, FX, other=HO), row("EMPTY-SPACE", MOD_A, MOD_B, FX2, other=HO2)]
        plan, md, _ = compile_and_render(arms_, {(FIXTURE, "baseline"): rows_, (FIXTURE, "without-a"): [],
                                                 (FIXTURE, "without-b"): []}, [MOD_A, MOD_B])
        p = reader(plan).get("prediction") or {}
        adm = {e["formKey"]: e["admission"] for e in p.get("record") or [] if e["checkId"] == FIXTURE}
        line = p.get("setAsideLine") or ""
        ok = (adm.get(FX) == "ownLayout" and adm.get(FX2) == "admitted"
              and f"one plugin's versions win for both the fixture and its removed host ({MOD_A}" in line)
        return ok, f"{adm}; {line[-400:]!r}"

    # C1219 / C1220 (ninth review, BPT 33; gate A answer 3): a missing mesh is said by what uses it, read from every plugin
    # file of each setup: its one placed user switched off for good in both setups (not drawn while off), or no placed
    # object in either setup using it, other uses not read. Never "could".
    def missing_mesh_users_line():
        import copy
        MA = "loadability.missing-assets"

        def call(i, name, args):
            return {"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": name, "arguments": args}}

        def answer(i, doc):
            return {"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(doc)}]}}
        req = [{"batchMarker": "b6-models"}, call(1, "housecarl_records", {"fields": ["Model.File"]}),
               {"batchMarker": "b7-assets"}, call(1, "housecarl_asset_status", {"asset_paths": ["x"]})]
        resp = [{"batchMarker": "b6-models"},
                # a query answers in "matches", a walk in "records" (BPT's import): both are read
                answer(1, {"matches": [{"formid": f"0C9801:{MOD_A}", "source": MOD_A,
                                        "fields": [{"path": "Model.File", "value": "Arch\\House98.nif"}]}],
                           "records": [{"formid": f"0C9802:{MOD_A}", "source": MOD_A,
                                        "fields": [{"path": "Model.File", "value": "Arch\\Wall98.nif"}]}]}),
                {"batchMarker": "b7-assets"},
                answer(1, {"results": [{"path": "meshes/Arch/House98.nif", "exists": False},
                                       {"path": "meshes/Arch/Wall98.nif", "exists": False}],
                           "bsa_failures": [], "bsa_failures_omitted": 0})]
        fa_ = plugin_file_of(MOD_A, [BASE], [("REFR", 0x010C9811, [("NAME", struct.pack("<I", 0x010C9801)),
                                                                   ("DATA", struct.pack("<6f", 1.0, 2.0, -30000.0, 0.0, 0.0, 0.0))],
                                              False)], group="CELL")
        files_ = {MOD_A: fa_, **{n_: plugin_file_of(n_, [BASE] if n_ != BASE else [], [], group="CELL")
                                 for n_ in (BASE, MOD_B, PATCH)}}
        arms_ = with_files(copy.deepcopy(PATCHED_ARMS), files_)
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        plan, md, _ = compile_and_render(arms_, results, [MOD_A, MOD_B], patches=[PATCH],
                                         count_only={(MA, "baseline"): 1, (MA, "without-patch"): 1},
                                         transcripts={(MA, "baseline"): (req, resp), (MA, "without-patch"): (req, resp)})
        return next((ln for ln in md.splitlines() if "House98.nif" in ln), "")

    def c1219_missing_mesh_only_user_off_for_good():
        ln = missing_mesh_users_line()
        return ("meshes/Arch/House98.nif: the one placed object that uses it (0C9811:ModA.esp) is switched off for good "
                "in both setups, so it is not drawn while off." in ln and "could" not in ln), ln[-500:]

    def c1220_missing_mesh_no_placed_user():
        ln = missing_mesh_users_line()
        return ("No placed object in either setup uses meshes/Arch/Wall98.nif; other uses (a script or a list creating the "
                "object) were not read." in ln and "could" not in ln), ln[-500:]

    for name, fn in [("ninth-review/C1219-missing-mesh-only-user-off-for-good", c1219_missing_mesh_only_user_off_for_good),
                     ("ninth-review/C1220-missing-mesh-no-placed-user", c1220_missing_mesh_no_placed_user)]:
        cell(name, fn)

    # C1224 (ninth review, page fit): with one alternative setup, the changes section's heading names it, so its entries
    # do not repeat the setup's label (BPT's two entries repeated three patch names each).
    def c1224_changes_entries_one_setup_unlabelled():
        plan, md, html = new_by_object_bundle(repaired=2)
        sec = section_text(md, (reader(plan).get("sectionHeadings") or {}).get("changes") or "Changes made by")
        title = priorities()["reader"]["checkTitles"][CONTACT]
        return (f"- **{title}:**" in sec and "Tested alternative setup" not in sec
                and f"<li><strong>{title}:</strong>" in html), sec.strip()[:300]

    # C1223 (ninth review, Sewers 27): an item counted from the separate evaluation links its rows: this index gives each
    # such finding of the separate evaluation an entry (its check, pair, file and rows), and the item line links it.
    def c1223_sibling_item_linked():
        X_, Y_ = f"000X23:{MOD_A}", f"000Y23:{MOD_B}"
        r_ = row("STATE-CONTACT@S1@Triggered", MOD_A, MOD_B, X_, label="Camp (ext)", other=Y_)
        arms = [arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_A, MOD_B])]
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}
        s_arms = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("plus-a", [BASE, MOD_A]),
                  arm("plus-b", [BASE, MOD_B]), arm("dependency-only", [BASE])]
        s_results = {(STATE, "baseline"): [r_], (STATE, "plus-a"): [], (STATE, "plus-b"): [],
                     (STATE, "dependency-only"): [], (CONTACT, "baseline"): [], (CONTACT, "plus-a"): [],
                     (CONTACT, "plus-b"): [], (CONTACT, "dependency-only"): []}
        plan, md, html, index = compile_and_render(arms, results, [MOD_A], patches=[PATCH], want_index=True,
                                                   missing=[(STATE, "without-patch", "failed"), (STATE, "baseline", "failed")],
                                                   sibling=(s_arms, s_results, [MOD_A], {"plan_id": "sibl00000023"}))
        it = next((ln for ln in md.splitlines() if "Measured in the separate evaluation" in ln), "")
        anchors_ = re.findall(r"\(evidence-index\.md#([^)]+)\)", it)
        has_ = [a_ for a_ in anchors_ if (f'id="{a_}"' in index or f"{{#{a_}}}" in index)]
        sec_ = index[index.find(f'id="{has_[0]}"'):][:600] if has_ else ""
        return bool(has_) and X_ in sec_ and Y_ in sec_, f"links {anchors_}; entry {sec_[:300]!r}; line {it[-200:]!r}"

    # C1222 (ninth review, Missives 30; gate A answer 1): the line saying a winner drops a base-game plugin's own change
    # says, of each field it names, what the field does in game where the skill data holds it (a cell's Location), and
    # that it is not read here where it does not (max height data).
    def c1222_dropped_base_change_field_meaning():
        L1, W1 = f"00C222:{BASE}", f"00C223:{BASE}"
        arms = [arm("baseline", [BASE, HF_, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, HF_, OTHER, MOD_A, MOD_B])]
        chains = [(L1, "Cell", [(BASE, {"Location": ""}), (HF_, {"Location": "018A59:Skyrim.esm"}),
                                (OTHER, {"Location": "018A59:Skyrim.esm"}), (MOD_A, {"Location": ""})]),
                  (W1, "Worldspace", [(BASE, {"MaxHeight": ""}), (HF_, {"MaxHeight": "MaxHeight.CellData=0B"}),
                                      (OTHER, {"MaxHeight": "MaxHeight.CellData=0B"}), (MOD_A, {"MaxHeight": ""})])]
        r1 = lost_row(OTHER, MOD_A, L1, "Location", ["Location=018A59:Skyrim.esm"], PH, PH)
        r1["Summary"] = f"Cell 00C222: Location - {OTHER}'s value lost to {MOD_A}'s"
        r2 = lost_row(OTHER, MOD_A, W1, "MaxHeight", ["MaxHeight.CellData=0B"], PH, PH)
        r2["Summary"] = f"Worldspace 00C223: MaxHeight - {OTHER}'s value lost to {MOD_A}'s"
        p_rows = [r1, r2]
        plan, md, _ = core_bundle(p_rows, list(p_rows), check=CELLATTR, arms=arms, p_chains=chains,
                                  masters={HF_: [BASE], OTHER: [BASE], MOD_A: [BASE], MOD_B: [BASE]})
        line = next((ln for ln in md.splitlines() if f"wins over {HF_}'s own change of" in ln), "")
        mean = priorities()["reader"]["prediction"]["fieldInGame"]["Cell|Location"]
        return (f"In game, Location is {mean}." in line
                and "What max height data does in game is not read here." in line), line[-500:]

    for name, fn in [("ninth-review/C1222-dropped-base-change-field-meaning", c1222_dropped_base_change_field_meaning)]:
        cell(name, fn)

    for name, fn in [("ninth-review/C1223-sibling-item-linked", c1223_sibling_item_linked)]:
        cell(name, fn)

    for name, fn in [("ninth-review/C1224-changes-entries-one-setup-unlabelled", c1224_changes_entries_one_setup_unlabelled)]:
        cell(name, fn)

    # C1225 (Gate C, the own-layout rule's order): a fixture whose nearest floor or wall's box reaches it (0u) cannot be
    # told to hang; it stays "cannot tell" even where one plugin's versions win for it and its removed host. The own-layout
    # rule runs after the cannot-tell rule (the first ordering took 18 such Winterhold rows).
    def c1225_cannot_tell_before_own_layout():
        FX, HO = f"000C25:{BASE}", f"000C26:{BASE}"
        dat = lambda z: ("DATA", struct.pack("<6f", 100.0, 200.0, z, 0.0, 0.0, 0.0))  # noqa: E731
        fa_ = plugin_file_of(MOD_A, [BASE], [("REFR", 0x00000C25, [("NAME", struct.pack("<I", 0x101)), dat(256.0)], False),
                                             ("REFR", 0x00000C26, [("NAME", struct.pack("<I", 0x102)), dat(-30000.0)], False)],
                             group="CELL")
        fb_ = plugin_file_of(MOD_B, [BASE], [("REFR", 0x00000C26, [("NAME", struct.pack("<I", 0x102)), dat(-30000.0)], False)],
                             group="CELL")
        fs_ = plugin_file_of(BASE, [], [("REFR", 0x00000C25 + i_, [("NAME", struct.pack("<I", 0x101)), dat(0.0)], False)
                                        for i_ in range(2)], group="CELL")
        arms_ = with_files([arm("baseline", [BASE, MOD_B, MOD_A], kind="baseline"), arm("without-a", [BASE, MOD_B]),
                            arm("without-b", [BASE, MOD_A])], {MOD_A: fa_, MOD_B: fb_, BASE: fs_})
        r_ = row("EMPTY-SPACE", MOD_A, MOD_B, FX, other=HO)
        r_["Summary"] = (f"LeftInEmptySpace: Static Fx (fixed) in Cell (int); {MOD_B} removed or moved {HO}; its assembly "
                         f"of 1 touches no floor or wall now (nearest floor or wall 0u)")
        plan, md, _ = compile_and_render(arms_, {(FIXTURE, "baseline"): [r_], (FIXTURE, "without-a"): [],
                                                 (FIXTURE, "without-b"): []}, [MOD_A, MOD_B])
        adm = {e["formKey"]: e["admission"] for e in (reader(plan).get("prediction") or {}).get("record") or []
               if e["checkId"] == FIXTURE}
        return adm.get(FX) == "anchorUnclear", f"{adm}"

    for name, fn in [("ninth-review/C1225-cannot-tell-before-own-layout", c1225_cannot_tell_before_own_layout)]:
        cell(name, fn)

    for name, fn in [("ninth-review/C1221-fixture-own-layout-set-aside", c1221_fixture_own_layout_set_aside)]:
        cell(name, fn)

    for name, fn in [("ninth-review/C1216-new-with-patch-object-counted-once", c1216_new_with_patch_object_counted_once),
                     ("ninth-review/C1217-new-by-check-names-shared-object", c1217_new_by_check_names_shared_object),
                     ("ninth-review/C1218-patch-problems-count-object-once", c1218_patch_problems_count_object_once)]:
        cell(name, fn)

    # ------------------------------------------------ tenth review (C1226-C1279)
    # C1226-C1228 (Snazzy S1): a door's navmesh door link (XNDP: a navmesh and its triangle at the door) only means
    # something against the version of that navmesh that wins. OtherMod rebuilt navmesh 0C1226 and links the base game's
    # door 00D226 at its own triangle 6; ModA's navmesh wins and lists the door at triangle 9, where ModA's door links it.
    # OtherMod's value fits only OtherMod's own navmesh, which does not win: no clash to forward (C1226). Where the losing
    # value fits the navmesh that wins (OtherMod's own new navmesh, which nothing overrides) and the winner's door has no
    # link, the loss stays counted (C1227). Where the plugin files are not read, nothing is decided from them (C1228).
    def door_link_files(win_nav_door_tri=9, loser_own_nav=False):
        D, N = 0x0000D226, 0x000C1226
        N_OWN = 0x010C1227                         # OtherMod's own navmesh (its own record)

        def nav(fid, tri):
            return ("NAVM", fid, [("NVNM", nvnm_d(16, [(tri, D)]))], False)

        def door(navfid, tri):
            subs = [("NAME", struct.pack("<I", 0x00000B10)), ("DATA", struct.pack("<6f", 10.0, 20.0, 0.0, 0.0, 0.0, 0.0))]
            if navfid is not None:
                subs.append(("XNDP", struct.pack("<IhH", navfid, tri, 0)))
            return ("REFR", D, subs, False)
        if loser_own_nav:
            # OtherMod links the door to its own new navmesh; ModA's door has no navmesh link at all
            return {BASE: plugin_file_of(BASE, [], [nav(N, 5), door(N, 5)]),
                    OTHER: plugin_file_of(OTHER, [BASE], [nav(N_OWN, 6), door(N_OWN, 6)]),
                    MOD_A: plugin_file_of(MOD_A, [BASE], [door(None, 0)]),
                    MOD_B: plugin_file_of(MOD_B, [BASE], []),
                    PATCH: plugin_file_of(PATCH, [BASE], [])}
        return {BASE: plugin_file_of(BASE, [], [nav(N, 5), door(N, 5)]),
                OTHER: plugin_file_of(OTHER, [BASE], [nav(N, 6), door(N, 6)]),
                MOD_A: plugin_file_of(MOD_A, [BASE], [nav(N, win_nav_door_tri), door(N, win_nav_door_tri)]),
                MOD_B: plugin_file_of(MOD_B, [BASE], []),
                PATCH: plugin_file_of(PATCH, [BASE], [])}

    def door_link_bundle(files=None, loser_own_nav=False, unread=None):
        D = f"00D226:{BASE}"
        nav_ = f"0C1227:{OTHER}" if loser_own_nav else f"0C1226:{BASE}"
        win_ = {"NavigationDoorLink.NavMesh": "(absent)"} if loser_own_nav else {
            "NavigationDoorLink.NavMesh": f"0C1226:{BASE}", "NavigationDoorLink.TeleportMarkerTriangle": "9"}
        chains = [(D, "PlacedObject", [
            (BASE, {"NavigationDoorLink.NavMesh": f"0C1226:{BASE}", "NavigationDoorLink.TeleportMarkerTriangle": "5"}),
            (OTHER, {"NavigationDoorLink.NavMesh": nav_, "NavigationDoorLink.TeleportMarkerTriangle": "6"}),
            (MOD_A, win_)])]
        r_ = lost_row(OTHER, MOD_A, D, "NavigationDoorLink",
                      [f"NavigationDoorLink.NavMesh={nav_}", "NavigationDoorLink.TeleportMarkerTriangle=6"], PH,
                      [f"NavigationDoorLink.NavMesh=0C1226:{BASE}", "NavigationDoorLink.TeleportMarkerTriangle=5"],
                      before=BASE)
        arms_ = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        if files is not False:
            arms_ = with_files(arms_, files or door_link_files(loser_own_nav=loser_own_nav))
        for a_ in arms_ if unread else []:     # (C1228b) one plugin's recorded bytes differ from its file: not read
            for pl_ in a_["requested"]["plugins"]:
                if pl_["name"] == unread:
                    pl_["sha256"] = "0" * 64
        plan, md, _ = core_bundle([r_], [], p_chains=chains, masters=MASTERS_, arms=arms_)
        pred = reader(plan).get("prediction") or {}
        return plan, md, pred, D

    def c1226_door_link_off_the_winning_navmesh_set_aside():
        plan, md, pred, D = door_link_bundle()
        got = [e["admission"] for e in rec_of(plan, D)]
        aside = str(pred.get("setAsideLine") or "")
        ok = (got == ["doorLinkNavmesh"] and "record field" not in str(pred.get("verdict") or "")
              and "door link" in aside and D[:6] in aside)
        return ok, f"{got}; verdict {str(pred.get('verdict'))[:200]!r}; aside {aside[-400:]!r}"

    def c1227_door_link_fitting_the_winning_navmesh_counted():
        plan, md, pred, D = door_link_bundle(loser_own_nav=True)
        got = [e["admission"] for e in rec_of(plan, D)]
        return got == ["admitted"], f"{got}"

    def c1228_door_link_files_unread_not_decided():
        plan, md, pred, D = door_link_bundle(files=False)
        got = [e["admission"] for e in rec_of(plan, D)]
        return got == ["admitted"], f"{got}"

    # C1228b (Gate C, the fail-safe's second gate): the losing door's version is read, but the plugin whose navmesh
    # version wins (ModA) is not (its recorded bytes differ from its file): nothing is decided, the row stays counted.
    def c1228b_winning_navmesh_unread_not_decided():
        plan, md, pred, D = door_link_bundle(unread=MOD_A)
        got = [e["admission"] for e in rec_of(plan, D)]
        return got == ["admitted"], f"{got}"

    for name, fn in [("tenth-review/C1228b-winning-navmesh-unread-not-decided", c1228b_winning_navmesh_unread_not_decided)]:
        cell(name, fn)

    for name, fn in [("tenth-review/C1226-door-link-off-the-winning-navmesh-set-aside",
                      c1226_door_link_off_the_winning_navmesh_set_aside),
                     ("tenth-review/C1227-door-link-fitting-the-winning-navmesh-counted",
                      c1227_door_link_fitting_the_winning_navmesh_counted),
                     ("tenth-review/C1228-door-link-files-unread-not-decided", c1228_door_link_files_unread_not_decided)]:
        cell(name, fn)

    # C1229-C1230 (Snazzy S2): a row set aside because ModA rebuilt the record (OtherMod only seated the object; ModA moved
    # it across) whose losing value the supplied patch puts back. The superseded line says the patch puts that value
    # back, names the record, and does not call the change one that "no longer applies"; the patch comparison names the
    # record (C1229). Where the patch keeps ModA's value, the line has no such clause (C1230).
    def superseded_patch_bundle(patch_back=True):
        P = f"00C229:{BASE}"
        b_, o_, a_ = pl(-9153.3, 5547.1, -241.065), pl(-9153.3, 5547.1, -241.395), pl(-9170.2, 5452.3, -227.85)
        chains = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_)])]
        w_val = o_ if patch_back else a_
        w_chains = [(P, "PlacedObject", [(BASE, b_), (OTHER, o_), (MOD_A, a_), (PATCH, w_val)])]
        p_rows = [pl_row(P, OTHER, MOD_A, b_, o_, a_)]
        w_rows = [pl_row(P, MOD_A, PATCH, o_, a_, w_val)] if patch_back else [pl_row(P, OTHER, PATCH, b_, o_, a_)]
        arms_ = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                 arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        plan, md, _ = core_bundle(p_rows, w_rows, p_chains=chains, w_chains=w_chains, masters=MASTERS_, arms=arms_)
        return plan, md, P

    def c1229_superseded_put_back_by_patch_said():
        plan, md, P = superseded_patch_bundle()
        got = [e["admission"] for e in rec_of(plan, P)]
        line = superseded_line(md)
        cmp_ = md.split("## How the patch compares", 1)[1] if "## How the patch compares" in md else ""
        ok = (got == ["superseded"] and PATCH in line and P[:6] in line and "no longer applies" not in line
              and P[:6] in cmp_)
        return ok, f"{got}; line {line[:400]!r}; comparison names it {P[:6] in cmp_}"

    def c1230_superseded_kept_by_patch_no_clause():
        plan, md, P = superseded_patch_bundle(patch_back=False)
        got = [e["admission"] for e in rec_of(plan, P)]
        line = superseded_line(md)
        return got == ["superseded"] and PATCH not in line and "rebuilt" in line, f"{got}; {line[:300]!r}"

    for name, fn in [("tenth-review/C1229-superseded-put-back-by-patch-said", c1229_superseded_put_back_by_patch_said),
                     ("tenth-review/C1230-superseded-kept-by-patch-no-clause", c1230_superseded_kept_by_patch_no_clause)]:
        cell(name, fn)

    # C1231 (Snazzy S4): the set-aside says the door links the navmesh replacements drop are "said in their own line
    # above" only when that line is printed. Here ModA's winning navmesh lists every door ModB's did: the door links were
    # read and none is dropped, so the set-aside says that, and promises no line.
    def c1231_no_dropped_door_link_no_promised_line():
        files_ = door_files()
        files_[MOD_A] = plugin_file_of(MOD_A, [BASE, MOD_B], [("NAVM", N_D1, [("NVNM", nvnm_d(9, [
            (5, 0x00000C3A00), (6, 0x01000D01), (7, 0x01000D02), (2, 0x01000D03), (1, 0x01000D04)]))], False)])  # ModB's doors
        N1 = f"0C3907:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        arms_ = with_files([arm("baseline", [BASE, OTHER, MOD_B, MOD_A, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, OTHER, MOD_B, MOD_A])], files_)
        plan, md, _ = cw_bundle([r_], extra_results={(NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}, arms=arms_)
        sec = md.split("## What the checks could not judge")[0]
        aside = str((reader(plan).get("prediction") or {}).get("setAsideLine") or "")
        ok = ("said in their own line above" not in aside and "none" in aside.split("door links", 1)[-1]
              and "no navmesh in this order links those" not in sec)
        return ok, f"aside {aside[-300:]!r}"

    for name, fn in [("tenth-review/C1231-no-dropped-door-link-no-promised-line",
                      c1231_no_dropped_door_link_no_promised_line)]:
        cell(name, fn)

    # C1234 (Clockwork S1): the door-links line links its own entry in the evidence index, which lists each door with the
    # navmesh record that dropped its link, the plugin whose version held it and the one whose version wins.
    def c1234_door_links_line_has_its_index_entry():
        N1 = f"0C3907:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        arms_ = with_files([arm("baseline", [BASE, OTHER, MOD_B, MOD_A, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, OTHER, MOD_B, MOD_A])], door_files())
        enr = {(RECORDS, "without-patch"): [r_], (RECORDS, "baseline"): []}
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): [],
                   (NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}
        plan, md, _, idx = compile_and_render(arms_, results, [MOD_A], patches=[PATCH], want_index=True,
                                              count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr)
        ln = next((x for x in md.splitlines() if "drop door links" in x), "")
        m_ = re.search(r"\(evidence-index\.md#([^)]+)\)\.?\s*$", ln.strip())
        anchor = m_.group(1) if m_ else ""
        sec = idx.split(f'<a id="{anchor}"></a>', 1)[1].split("\n<a id=", 1)[0] if anchor and f'<a id="{anchor}"></a>' in idx else ""
        d1 = next((x for x in sec.splitlines() if f"000D01:{MOD_B}" in x), "")
        ok = (anchor.startswith("door-links") and f"000D02:{MOD_B}" in sec
              and N1 in d1 and MOD_B in d1.replace(f"000D01:{MOD_B}", "") and MOD_A in d1)
        return ok, f"anchor {anchor!r}; line end {ln[-120:]!r}; entry {sec[:400]!r}"

    # C1235 (Clockwork 75, Winterhold 88): the re-measure of patch-placed pairs says its count is of the actual render
    # surfaces and links an index entry giving each pair's figures and the file each was read from; the sums unchanged.
    def c1235_placed_remeasure_says_basis_and_source():
        X = f"000X34:{MOD_A}"
        ys = [f"000Y3{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        for i_, r_ in enumerate(p_rows):
            r_["Summary"] += f"; {(39, 3)[i_]} triangle pairs"
            r_["Necessary"][0]["PhysicalDetail"] = ["crossing; 40 triangle pairs [mm]", "crossing; 3 triangle pairs [mm]"][i_]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds",
                  "Detail": [f"actual render surfaces: crossing; {n_} triangle pairs [mm]" for n_ in (38, 12)][i_]}
                 for i_, y in enumerate(ys)]
        other_ = f"00C93A:{BASE}"
        enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", [(MOD_A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, md, _, idx = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): []},
                                              [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                              enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()},
                                              transcripts=tr, want_index=True)
        ln = next((x for x in md.splitlines() if x.startswith("- Predicted, where a patch places one")), "")
        m_ = re.search(r"\(evidence-index\.md#(patch-placed[^)]*)\)", ln)
        anchor = m_.group(1) if m_ else ""
        sec = idx.split(f'<a id="{anchor}"></a>', 1)[1].split("\n<a id=", 1)[0] if anchor and f'<a id="{anchor}"></a>' in idx else ""
        y1 = next((x for x in sec.splitlines() if ys[0] in x), "")
        ok = ("43 triangle pairs" in ln and "50 with it" in ln and "actual render surfaces" in ln
              and "40" in y1 and "38" in y1 and "39" in y1 and ".json" in sec)
        return ok, f"line {ln[-300:]!r}; entry {sec[:500]!r}"

    for name, fn in [("tenth-review/C1234-door-links-line-has-its-index-entry", c1234_door_links_line_has_its_index_entry),
                     ("tenth-review/C1235-placed-remeasure-says-basis-and-source", c1235_placed_remeasure_says_basis_and_source)]:
        cell(name, fn)

    # C1232 (Snazzy S3): a by-object check's pairs on lost-record objects, where some count only through the partner,
    # are split by basis: the object itself (the headline's shared count) and only the object it rests on.
    def c1232_linked_lost_split_by_basis():
        X1, X2 = f"00CB31:{BASE}", f"00CB32:{BASE}"
        S1, S2 = f"000S31:{MOD_B}", f"00CB34:{BASE}"
        ct_ = [row("CONTACT", MOD_B, MOD_A, X1, other=S1), row("CONTACT", MOD_B, MOD_A, X2, other=S2)]
        lrs_, ch_ = [], []
        for T in (X1, S2):
            a_, b_ = offs(0), offs(0)
            a_["Placement.Position"], b_["Placement.Position"] = "300,100,0", "700,100,0"
            lrs_.append(lost_row(MOD_B, MOD_A, T, "Placement", off_txt(b_), off_txt(a_), off_txt(offs(0)), before=BASE))
            ch_.append((T, "PlacedObject", [(BASE, offs(0)), (MOD_B, b_), (MOD_A, a_)]))
        arms = [arm("baseline", [BASE, MOD_B, MOD_A, PATCH], kind="baseline"), arm("without-patch", [BASE, MOD_B, MOD_A]),
                arm("without-b", [BASE, MOD_A])]
        plan, md, _ = compile_and_render(arms, {(OVERLAP, "baseline"): [], (OVERLAP, "without-patch"): [],
                                                (OVERLAP, "without-b"): [], (CONTACT, "baseline"): [],
                                                (CONTACT, "without-patch"): ct_, (CONTACT, "without-b"): []},
                                         [MOD_A], patches=[PATCH],
                                         count_only={(RECORDS, "without-patch"): 2, (RECORDS, "without-b"): 1},
                                         enrichment={(RECORDS, "without-patch"): lrs_, (RECORDS, "without-b"): []},
                                         transcripts={(RECORDS, "without-patch"): records_transcript(ch_)},
                                         masters={MOD_A: [BASE], MOD_B: [BASE], PATCH: [BASE]})
        it = next((ln for ln in md.splitlines() if ln.startswith("- **Objects left without their supporting surface")), "")
        ok = (re.search(r"\b2 of these pairs are on an object whose own record change", it) is not None
              and re.search(r"\b1 the object itself\b", it) is not None and re.search(r"\b1 only\b", it) is not None)
        return ok, it[-500:]

    for name, fn in [("tenth-review/C1232-linked-lost-split-by-basis", c1232_linked_lost_split_by_basis)]:
        cell(name, fn)

    # C1233 (BPT S2): the summary's "undoes" counts only the fields the patch undoes; the base game's value put back over
    # another plugin's change is said apart (its own count), and the undone line's off-for-good part reads as part of
    # the undone fields ("of them").
    def c1233_summary_undone_parts_apart():
        md_ = earlier_fix_bundle(True, base_value=True, whole=True, later_carries=True)
        sm_ = next((x for x in md_.splitlines() if x.startswith("In short")), "")
        return ("undoes 1 record field" not in sm_ and re.search(r"base game's value back over another plugin's change on 1\b", sm_)
                is not None), sm_[-400:]

    def c1233b_undone_off_reads_as_part():
        R0, R4 = f"00C233:{BASE}", f"00C234:{BASE}"
        p_rows = [lost_row(MOD_A, MOD_B, R0, "LocationReference", "a0", "b0", "v0")]
        w_rows = [lost_row(MOD_B, PATCH, R4, "Primitive", ["Primitive.Bounds=5,5,5"], ["Primitive.Bounds=4,4,4"],
                           ["Primitive.Bounds=4,4,4"], before=BASE)]
        b_, m_ = {**offs(0), "Primitive.Bounds": "4,4,4"}, {**offs(0), "Primitive.Bounds": "5,5,5"}
        pt_ = {**offs(0, "0", "000014:Skyrim.esm"), "Primitive.Bounds": "4,4,4"}
        w_ch = [(R4, "PlacedObject", [(BASE, b_), (MOD_B, m_), (PATCH, pt_)])]
        plan, md, _ = core_bundle(p_rows, w_rows, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE, MOD_A, MOD_B]},
                                  targets=(MOD_A, MOD_B))
        un = next((x for x in md.splitlines() if x.startswith("- Undone by ")), "")
        return re.search(r"; 1 of them on objects [^;]*sets? off for good", un) is not None, un[:400]

    for name, fn in [("tenth-review/C1233-summary-undone-parts-apart", c1233_summary_undone_parts_apart),
                     ("tenth-review/C1233b-undone-off-reads-as-part", c1233b_undone_off_reads_as_part)]:
        cell(name, fn)

    # C1237 (Missives S2): the carriers a line names leave the base game's own plugins out of the names; how many of
    # them also carry the value is said, so the count is not read as every carrier. Dawnguard.esm carries Update.esm's
    # value in R1 beside OtherMod.esp.
    def c1237_base_game_carriers_counted():
        DG = "Dawnguard.esm"
        R1 = f"00CA37:{BASE}"
        arms = [arm("baseline", [BASE, UPD, DG, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, UPD, DG, OTHER, MOD_A, MOD_B])]
        b0, a0, u0 = offs(0), offs(0), offs(0)
        u0["Placement.Position"] = "100,100,12"
        chains = [(R1, "PlacedObject", [(BASE, b0), (UPD, u0), (DG, u0), (OTHER, u0), (MOD_A, a0)])]
        p_rows = [lost_row(UPD, MOD_A, R1, "Placement", ["Placement.Position=100,100,12"], PH, PH),
                  lost_row(OTHER, MOD_A, R1, "Placement", ["Placement.Position=100,100,12"], PH, PH)]
        plan, md, _ = core_bundle(p_rows, list(p_rows), arms=arms, p_chains=chains, w_chains=chains,
                                  masters={UPD: [BASE], DG: [BASE, UPD], OTHER: [BASE, UPD], MOD_A: [BASE, UPD],
                                           MOD_B: [BASE], PATCH: [BASE]})
        line = next((ln for ln in md.splitlines() if f"{MOD_A} undoes its master {UPD}'s fix" in ln), "")
        return (OTHER in line and "1 of the base game's own plugins" in line and DG not in line), line[:600]

    for name, fn in [("tenth-review/C1237-base-game-carriers-counted", c1237_base_game_carriers_counted)]:
        cell(name, fn)

    # C1238 (Sewers S1): where the check's row says the object sits inside "something else", the line does not name the
    # row's other object as what it is inside; it says the check does not name it (the other object is named as what
    # the row measures it against).
    def c1238_inside_something_else_not_named():
        X = f"000X38:{MOD_A}"
        C_, P_ = f"000E01:{MOD_B}", f"000E02:{MOD_B}"
        f_ = plugin_file_of(MOD_B, [BASE], [
            ("REFR", 0x01000E01, [("NAME", struct.pack("<I", 0x01000E03)), ("DATA", struct.pack("<6f", 0, 0, 0, 0, 0, 0)),
                                  ("XESP", struct.pack("<IB", 0x01000E02, 0))], False),
            ("REFR", 0x01000E02, [("NAME", struct.pack("<I", 0x01000E04)), ("DATA", struct.pack("<6f", 9, 9, 0, 0, 0, 0))],
             False)], group="CELL")
        st_ = row(f"STATE-CONTACT@{P_}@Unknown", MOD_B, MOD_A, C_, label="Camp (ext)", other=X)
        st_["Summary"] = (f"Contact StateConditional under PlankSwitch ({MOD_B}): {C_} [Static Scaffold] leaves - Sunk: the "
                          f"host leaves with the flip - hung from the host as authored (0u gap); under the winner it sits "
                          f"26.9u inside something else in Camp (ext)")
        arms = with_files([arm("baseline", [BASE, MOD_A, MOD_B, PATCH], kind="baseline"),
                           arm("without-patch", [BASE, MOD_A, MOD_B])], {MOD_B: f_})
        res = {(STATE, a_): ([st_] if a_ == "without-patch" else []) for a_ in ("baseline", "without-patch")}
        plan, md, _ = compile_and_render(arms, res, [MOD_A], patches=[PATCH])
        title = priorities()["reader"]["checkTitles"].get(STATE) or STATE
        it = next((ln for ln in md.splitlines() if ln.startswith(f"- **{title}")), "")
        ig = it.split("In game", 1)[-1]
        return ("inside something the check does not name" in ig and not re.search(r"inside (object )?000X38", ig)
                and "26.9 u" in ig), ig[:400]

    for name, fn in [("tenth-review/C1238-inside-something-else-not-named", c1238_inside_something_else_not_named)]:
        cell(name, fn)

    # C1236 (Missives S1): a patch-wins row of the detail line that is counted but holds no predicted clash, because the
    # same loss is already there without the evaluated mods (the prediction sets it aside so), is named on the line with
    # that, so its count can be found and is not read as a loss the patch makes. OtherMod's Owner of 00C362 is lost to
    # ModA in the measured order and to ModB without ModA (already there); with the patch it is lost to the patch.
    def c1236_patch_wins_counted_rows_named():
        G9, O9 = f"00C361:{BASE}", f"00C362:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_B, MOD_A]), arm("without-a", [BASE, OTHER, MOD_B])]
        p_rows = [lost_row(OTHER, MOD_A, O9, "Owner", "o", "a", "v")]
        n_rows = [lost_row(OTHER, MOD_B, O9, "Owner", "o", "b", "v")]
        w_rows = [lost_row(MOD_B, PATCH, G9, "Grid", ["Grid.Flags=327680"], PH, ["Grid.Flags=5504256"]),
                  lost_row(OTHER, PATCH, O9, "Owner", "o", "p", "v")]
        for r_ in p_rows + n_rows + w_rows:
            r_["Summary"] = r_["Summary"].replace("PlacedObject", "Cell")
        enr = {(CELLATTR, "without-patch"): p_rows, (CELLATTR, "without-a"): n_rows, (CELLATTR, "baseline"): w_rows}
        results = {(CONTACT, a_): [] for a_ in ("baseline", "without-patch", "without-a")}
        plan, md, _ = compile_and_render(arms, results, [MOD_A], patches=[PATCH], enrichment=enr,
                                         count_only={k: max(1, len(v)) for k, v in enr.items()})
        adm = [e["admission"] for e in rec_of(plan, O9)]
        ln = next((x for x in md.splitlines() if "where a declared patch's value wins" in x), "")
        ok = (adm == ["alreadyPresent"] and "1 counted" in ln
              and re.search(r"00C362:Skyrim\.esm[^;]*Owner[^;]*" + re.escape(OTHER) + r"[^;]*" + re.escape(PATCH)
                            + r"[^;]*without the evaluated mods", ln) is not None and "00C361" not in ln)
        return ok, f"{adm}; line {ln[:420]!r}"

    for name, fn in [("tenth-review/C1236-patch-wins-counted-rows-named", c1236_patch_wins_counted_rows_named)]:
        cell(name, fn)

    # C1239 (Winterhold S2; Gate A condition a). ModA's versions of two navmeshes are a single triangle. 000E95's base-game
    # version has 600 triangles: ModA's is a placeholder (how a mod removes a navmesh). 000E96's base-game version is
    # itself one triangle: ModA's is that version, not a removal, and is said apart (C1239). Where the removed navmesh's
    # cell holds a navmesh record of ModA's own (000E99, 50 triangles), loaded beside the winning version, the line says
    # so and that whether they overlap was not measured (C1239b).
    def nvnm_cell(n_tris, cell):
        return (struct.pack("<IIII", 12, 0xA5E9A03C, 0, cell) + struct.pack("<I", 0) + struct.pack("<I", n_tris)
                + b"\x00" * 16 * n_tris + struct.pack("<I", 0) + struct.pack("<I", 0))

    def stub_cell_bundle():
        n1, n2 = f"000E95:{BASE}", f"000E96:{BASE}"
        p_rows = [row("CLOBBERED", MOD_A, MOD_B, n1, label="TempleInterior (int)  1 tris -> 600"),
                  row("CLOBBERED", MOD_A, MOD_B, n2, label="TempleCellar (int)  1 tris -> 1")]
        for r_ in p_rows:
            r_["Necessary"][0]["Detail"] = "victim-only ground: 900 sample(s) of the loser's mesh farther than 120u"
        arms_ = [arm("baseline", [BASE, MOD_A, MOD_B], kind="baseline"), arm("with-patch", [BASE, MOD_A, MOD_B, PATCH])]
        C1, C2 = 0x00000C39, 0x00000C3A
        fs_ = {BASE: plugin_file_of(BASE, [], [("NAVM", 0x00000E95, [("NVNM", nvnm_cell(600, C1))], False),
                                               ("NAVM", 0x00000E96, [("NVNM", nvnm_cell(1, C2))], False)]),
               MOD_A: plugin_file_of(MOD_A, [BASE], [("NAVM", 0x00000E95, [("NVNM", nvnm_cell(1, C1))], False),
                                                      ("NAVM", 0x00000E96, [("NVNM", nvnm_cell(1, C2))], False),
                                                      ("NAVM", 0x01000E99, [("NVNM", nvnm_cell(50, C1))], False)]),
               MOD_B: plugin_file_of(MOD_B, [BASE], [("NAVM", 0x00000E95, [("NVNM", nvnm_cell(600, C1))], False),
                                                      ("NAVM", 0x00000E96, [("NVNM", nvnm_cell(1, C2))], False)]),
               PATCH: plugin_file_of(PATCH, [BASE], [])}
        with_files(arms_, fs_)
        plan, md, html = compile_and_render(arms_, {(NAVOWN, "baseline"): p_rows, (NAVOWN, "with-patch"): []},
                                            [MOD_A, MOD_B], patches=[PATCH])
        return " ".join(x.get("text", "") for x in (reader(plan).get("prediction") or {}).get("historyLossLines") or [])

    def c1239_base_single_triangle_not_a_removal():
        top = stub_cell_bundle()
        rem = next((x for x in re.split(r"(?<=\.) ", top) if "how a mod removes a navmesh" in x), "")
        return ("000E95" in rem and "000E96" not in rem and "000E96" in top), top[-700:]

    def c1239b_own_navmesh_beside_winner_said():
        top = stub_cell_bundle()
        return ("000E99:ModA.esp" in top and "not measured" in top), top[-700:]

    for name, fn in [("tenth-review/C1239-base-single-triangle-not-a-removal", c1239_base_single_triangle_not_a_removal),
                     ("tenth-review/C1239b-own-navmesh-beside-winner-said", c1239b_own_navmesh_beside_winner_said)]:
        cell(name, fn)

    # C1240 (Gate C check-in: anchors): every evidence-index anchor the report links, in both its Markdown and its HTML,
    # is written by the index in the same format. Run on the door-links and re-measure fixtures (C1234, C1235), whose
    # HTML anchors were once written inside the sibling block and so never printed.
    def report_anchors_resolve(out_):
        bad_ = []
        for rep_, idx_, pat_, idpat_ in (("report.md", "evidence-index.md", r"\(evidence-index\.md#([^)\s]+)\)", '<a id="{}"></a>'),
                                         ("report.html", "evidence-index.html", r'href="evidence-index\.html#([^"]+)"', 'id="{}"')):
            for a_ in set(re.findall(pat_, str(out_.get(rep_) or ""))):
                if idpat_.format(a_) not in str(out_.get(idx_) or ""):
                    bad_.append(f"{rep_} -> {idx_}#{a_}")
        return bad_

    def c1240_linked_anchors_written():
        bad_ = []
        N1 = f"0C3907:{BASE}"
        r_ = lost_row(MOD_B, MOD_A, N1, "Data", ["Data.Max=1,2,3"], PH, ["Data.Max=0,0,0"])
        r_["Summary"] = f"NavigationMesh {N1}: Data - {MOD_B}'s value lost to {MOD_A}'s"
        arms_ = with_files([arm("baseline", [BASE, OTHER, MOD_B, MOD_A, PATCH], kind="baseline"),
                            arm("without-patch", [BASE, OTHER, MOD_B, MOD_A])], door_files())
        enr = {(RECORDS, "without-patch"): [r_], (RECORDS, "baseline"): []}
        results = {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): [],
                   (NAVOWN, "baseline"): [], (NAVOWN, "without-patch"): []}
        plan, out = compile_and_render(arms_, results, [MOD_A], patches=[PATCH], want_index="all",
                                       count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr)
        door_ = "door-links-1" in str(out.get("report.html") or "")
        bad_ += report_anchors_resolve(out)
        X = f"000X34:{MOD_A}"
        ys = [f"000Y3{i}:{MOD_B}" for i in (1, 2)]
        p_rows = [row("OVERLAP", MOD_B, MOD_A, X, other=y) for y in ys]
        for i_, r2_ in enumerate(p_rows):
            r2_["Necessary"][0]["PhysicalDetail"] = ["crossing; 40 triangle pairs [mm]", "crossing; 3 triangle pairs [mm]"][i_]
        preds = [{"Kind": "OVERLAP", "A": MOD_B, "B": MOD_A, "FormKey": X, "Other": y, "Physical": "Holds",
                  "Detail": f"crossing; {(38, 12)[i_]} triangle pairs [mm]"} for i_, y in enumerate(ys)]
        other_ = f"00C93A:{BASE}"
        enr2 = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
        tr = {(RECORDS, "with-patch"): tree_transcript([
            (X, "PlacedObject", [(MOD_A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
            (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
        plan, out = compile_and_render(PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): []},
                                       [MOD_A, MOD_B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                       enrichment=enr2, count_only={k: max(1, len(v)) for k, v in enr2.items()},
                                       transcripts=tr, want_index="all")
        placed_ = "patch-placed-pairs" in str(out.get("report.html") or "")
        bad_ += report_anchors_resolve(out)
        return door_ and placed_ and not bad_, f"door link {door_}, placed link {placed_}; unresolved {bad_}"

    for name, fn in [("tenth-review/C1240-linked-anchors-written", c1240_linked_anchors_written)]:
        cell(name, fn)

    # C1241 (page fit, tenth review): at the ladder's aside step the set-aside notes of the detail list become one link
    # line (their counts are in the index's set-aside section), and a change entry keeps its check and its link, its text
    # in the index; at the normal step both are printed in full.
    def render_at(plan, layout):
        pr_ = json.loads(PRIORITIES.read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as td_:
            view_ = R.effective_view(plan, 3, 1, pr_, layout)
            return R.render_all(plan, view_, Path(td_) / "out", "")

    def c1241_aside_step_moves_notes_and_change_text():
        plan = detail_bundle()[0]
        note_ = str(reader(plan).get("detailAsideLine") or "")
        aside_ = render_at(plan, "compact tail order aside")
        normal_ = render_at(plan, "normal")
        rows_ = [{"Kind": "EDGE-IDX", "A": MOD_A, "B": MOD_B, "Summary": f"EDGE-IDX between {MOD_A} and {MOD_B}",
                  "Necessary": [{"FormKey": f"0C1{i}41:{BASE}", "Label": "Hall (int)", "Physical": "Holds"}]} for i in (1, 2, 3)]
        order = {"without-patch": {"navownprobe": dict(verdict="DIFFERENTIAL-HELPFUL", repaired=3, reclassified=0,
                                                       created=0, newDefect=0, attributed=0, unmeasured=0)}}
        log = f"GONE    EDGE-IDX    {MOD_A}  x  {MOD_B}   -3 record(s): repaired 3\n"
        plan2, _, _ = compile_and_render(PATCHED_ARMS, {(NAVOWN, "without-patch"): [], (NAVOWN, "baseline"): rows_},
                                         [MOD_A, MOD_B], patches=[PATCH], order=order,
                                         diff_logs={(NAVOWN, "without-patch"): log})
        ch_ = next((f for f in plan2.get("findings") or [] if f.get("kind") == "repaired"), {})
        plain_ = str(ch_.get("readerPlain") or "")
        aside2_, normal2_ = render_at(plan2, "compact tail order aside"), render_at(plan2, "normal")
        ok = (bool(note_) and note_ in normal_["report.html"] and note_ not in aside_["report.html"]
              and 'href="evidence-index.html#detail-set-aside"' in aside_["report.html"]
              and bool(plain_) and plain_ in normal2_["report.html"] and plain_ not in aside2_["report.html"]
              and f'href="evidence-index.html#{ch_.get("id")}"' in aside2_["report.html"])
        return ok, f"note {note_[:80]!r}; change {plain_[:80]!r}"

    for name, fn in [("tenth-review/C1241-aside-step-moves-notes-and-change-text", c1241_aside_step_moves_notes_and_change_text)]:
        cell(name, fn)

    # C1242 (Gate C, regen-r10dev2 Winterhold 25 read 11 records losing ground against regen-r9a's 9): a version that is
    # the base game's own single triangle loses no ground; it is not among the records said to lose ground (here none is).
    def c1242_base_single_not_losing_ground():
        top = stub_cell_bundle()
        return re.search(r"\bin 0 of them ground\b", top) is not None, top[:400]

    for name, fn in [("tenth-review/C1242-base-single-not-losing-ground", c1242_base_single_not_losing_ground)]:
        cell(name, fn)

    # C1444-C1448 (task 2026-10-17-report-route-cut-leads): when the route-cut check reaches no verdict, the objects
    # blockprobe rated as blocking or likely to block a route (its --rows file) are listed as leads for an in-game check,
    # never as clashes, with the object, its cell and the passage the probe states; a check with a verdict, and a bundle
    # with no rows file, are as before.
    def c1444_route_cut_leads_listed():
        plan, md, html, index = route_cut_bundle(None, ROUTE_CUT_ROWS)
        sec_ = section_text(md, "What the checks could not judge")
        want_ = [f"000A01:{MOD_A}", f"000A02:{MOD_A}", f"000A03:{MOD_A}"]
        ok = ("Leads for an in-game check, not clashes" in sec_
              and all(k_ in sec_ and k_ in html for k_ in want_)
              and "HallA" in sec_ and "YardB" in sec_
              and "32 u" in sec_ and "72 u" in sec_ and "about half the time" in sec_)
        return ok, sec_[-900:]

    def c1445_route_cut_leads_only_blocking_objects():
        plan, md, html, index = route_cut_bundle(None, ROUTE_CUT_ROWS)
        sec_ = section_text(md, "What the checks could not judge")
        lead_ = next((ln for ln in sec_.splitlines() if "Leads for an in-game check" in ln), "")
        pred_ = section_text(md, next(h for h in md_heading_order(md) if h.startswith("What adding")))
        leads_ = [r_.get("FormKey") for g_ in (reader(plan).get("coverage") or {}).get("routeCutLeads") or []
                  for r_ in g_.get("rows") or []]
        ok = (bool(lead_) and f"000A04:{MOD_A}" not in lead_ and f"000A05:{MOD_A}" not in lead_
              and sorted(leads_) == [f"000A0{i}:{MOD_A}" for i in (1, 2, 3)] and "rated 3 objects" in lead_
              and f"000A04:{MOD_A}" not in index and f"000A05:{MOD_A}" not in index
              and not any(f"000A0{i}:{MOD_A}" in pred_ for i in range(1, 6))
              and "at least 160 u" in lead_)
        return ok, f"leads {leads_}; lead {lead_[:600]!r}"

    def c1446_route_cut_leads_in_evidence_index():
        plan, md, html, index = route_cut_bundle(None, ROUTE_CUT_ROWS)
        part_ = index_section(index, "route-cut-leads")
        ok = (bool(part_) and all(f"000A0{i}:{MOD_A}" in part_ for i in (1, 2, 3))
              and "Clutter\\BarrelA.nif" in part_ and "evidence-index.md#route-cut-leads" in md)
        return ok, (part_ or index[-600:])[:600]

    def c1447_route_cut_with_verdict_unchanged():
        plan, md, html, index = route_cut_bundle("no-finding", ROUTE_CUT_ROWS)
        plan0, md0, html0, index0 = route_cut_bundle("no-finding", None)
        ok = (md == md0 and html == html0 and "Leads for an in-game check" not in md
              and not any(f"000A0{i}:{MOD_A}" in md for i in range(1, 6)))
        return ok, "identical" if md == md0 else "the report differs with the rows file present"

    def c1448_route_cut_no_rows_file_unchanged():
        plan, md, html, index = route_cut_bundle(None, None)
        sec_ = section_text(md, "What the checks could not judge")
        ok = ("ran, but could not decide" in sec_ and "Whether a single object blocks an NPC route" in sec_
              and "Leads for an in-game check" not in md)
        return ok, sec_[:600]

    def c1449_route_cut_leads_own_plugin_and_setup():
        # Rejects (Gate A review M1): a lead sentence that calls every object the evaluated mod's ("rated N objects of
        # ModA.esp"), when one is a base-game object the mod only changes. Rejects (L1): a lead that does not say which
        # setup it measured, in the report's own words for the two setups.
        plan, md, html, index = route_cut_two_arm_bundle()
        sec_ = section_text(md, "What the checks could not judge")
        leads_ = [ln for ln in sec_.splitlines() if "Leads for an in-game check" in ln]
        # the report's own words for each setup, as its coverage lines write them mid-sentence ("for the measured load order")
        setups_ = ["for the " + a_["readerLabel"][:1].lower() + a_["readerLabel"][1:]
                   for a_ in (plan.get("scope") or {}).get("arms") or [] if a_.get("readerLabel")]
        own_ = [ln for ln in leads_ if f"000B01:{BASE}" in ln]
        ok = (len(leads_) == 2 and len(setups_) == 2
              and all(sum(1 for ln in leads_ if su_ in ln) == 1 for su_ in setups_)
              and not any(f"objects of {MOD_A}" in ln for ln in leads_)
              and len(own_) == 1 and f"a {BASE} object {MOD_A} changes" in own_[0])
        return ok, f"setups {setups_}; leads {[ln[:300] for ln in leads_]}"

    for name, fn in [("route-cut-leads/C1449-lead-names-its-own-plugin-and-setup", c1449_route_cut_leads_own_plugin_and_setup)]:
        cell(name, fn)

    for name, fn in [("route-cut-leads/C1444-leads-listed-with-object-cell-passage", c1444_route_cut_leads_listed),
                     ("route-cut-leads/C1445-only-blocking-objects-never-clashes", c1445_route_cut_leads_only_blocking_objects),
                     ("route-cut-leads/C1446-leads-in-evidence-index", c1446_route_cut_leads_in_evidence_index),
                     ("route-cut-leads/C1447-control-verdict-unchanged", c1447_route_cut_with_verdict_unchanged),
                     ("route-cut-leads/C1448-control-no-rows-file", c1448_route_cut_no_rows_file_unchanged)]:
        cell(name, fn)

    # ------------------------------------------- the report's bodiless rule agrees with the probes' (cases 1450-1453)
    # A CollisionMarker is a Static with no model whose reference carries a collision primitive: it blocks in game (Part 3b
    # station Q: the player and an item stood on its box). Having no model is not having no body; the probes set aside only
    # the types measured to have none. The report sets aside the same set, never every type with no model.
    SG_ = "state.conditional-geometry"
    CM1, LT1, AM1 = (f"000{x}:{MOD_A}" for x in ("C01", "L01", "A01"))

    def body_rule_bundle():
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER, MOD_B])]
        p = [row(f"STATE-ROUTE@0S0W01:{MOD_A}@Triggered", MOD_A, MOD_B, CM1),
             row(f"STATE-CONTACT@0S0W01:{MOD_A}@Triggered", MOD_A, MOD_B, LT1, other=f"000H01:{MOD_B}"),
             row(f"STATE-CONTACT@0S0W02:{MOD_A}@Triggered", MOD_A, MOD_B, AM1, other=f"000H02:{MOD_B}")]
        refs = {CM1: {"base": f"000021:{BASE}"}, LT1: {"base": f"0B0L01:{MOD_A}"}, AM1: {"base": f"0B0A01:{MOD_A}"},
                f"000H01:{MOD_B}": {"base": f"0B0S01:{MOD_B}"}, f"000H02:{MOD_B}": {"base": f"0B0S01:{MOD_B}"}}
        bases = {f"000021:{BASE}": {"type": "Static", "editorId": "CollisionMarker", "model": ""},
                 f"0B0L01:{MOD_A}": {"type": "Light", "editorId": "TestLightNoModel", "model": ""},
                 f"0B0A01:{MOD_A}": {"type": "Activator", "editorId": "TestLever", "model": "lever.nif"},
                 f"0B0S01:{MOD_B}": {"type": "Static", "editorId": "TestWall", "model": "wall.nif"}}
        return compile_and_render(arms, {(SG_, "baseline"): p, (SG_, "without-a"): []}, [MOD_A],
                                  names={"refs": refs, "bases": bases})

    def body_rule_of(plan, fk):
        adm_ = [e.get("admission") for e in (reader(plan).get("prediction") or {}).get("record") or []
                if e.get("checkId") == SG_ and e.get("formKey") == fk]
        fs_ = [f for f in plan.get("findings") or [] if f.get("checkId") == SG_
               and any(r.get("formKey") == fk for r in (f.get("allRows") or f.get("rows") or []))]
        return adm_, [(f.get("id"), f.get("rowsTotal"), f.get("rowsNoBody"), f.get("readerRows")) for f in fs_]

    # C1450. Rejects: a route row of a CollisionMarker (no model, a blocking box) set aside as "no body" in the prediction or
    # left out of its finding's count.
    def c1450_collision_marker_route_row_kept():
        plan, md, html = get("bodyrule", body_rule_bundle)
        adm_, fs_ = body_rule_of(plan, CM1)
        ok = adm_ == ["admitted"] and len(fs_) >= 1 and all(not nb_ and rd_ == tot_ for _, tot_, nb_, rd_ in fs_)
        return ok, f"CollisionMarker route row: admission {adm_}; findings (id, rows, no body, reader rows) {fs_}"

    # C1451. Control: a light with no model is still set aside as having no body, in the prediction and in its finding.
    def c1451_modelless_light_still_set_aside():
        plan, md, html = get("bodyrule", body_rule_bundle)
        adm_, fs_ = body_rule_of(plan, LT1)
        ok = adm_ == ["noBody"] and len(fs_) >= 1 and all(nb_ == tot_ and rd_ == 0 for _, tot_, nb_, rd_ in fs_)
        return ok, f"light with no model: admission {adm_}; findings {fs_}"

    # C1452. Control: an activator with a model is unchanged: admitted and counted.
    def c1452_modelled_activator_unchanged():
        plan, md, html = get("bodyrule", body_rule_bundle)
        adm_, fs_ = body_rule_of(plan, AM1)
        ok = adm_ == ["admitted"] and len(fs_) >= 1 and all(not nb_ and rd_ == tot_ for _, tot_, nb_, rd_ in fs_)
        return ok, f"activator with a model: admission {adm_}; findings {fs_}"

    # C1453. Rejects: a CollisionMarker route row the patch comparison created, left out of the new problems as "no model".
    # Without the patch ModA's CollisionMarker and its static each cut a route that the measured order does not.
    def c1453_collision_marker_counted_in_comparison():
        C1, S1, C3, S3 = (f"000{x}:{MOD_A}" for x in ("C11", "S11", "C13", "S13"))
        kind = f"STATE-ROUTE@0S0W01:{MOD_A}@Triggered"
        p_rows = [row(kind, MOD_B, MOD_A, C1), row(kind, MOD_B, MOD_A, S1)]
        w_rows = [row(kind, MOD_B, MOD_A, C3), row(kind, MOD_B, MOD_A, S3)]
        refs = {C1: {"base": f"000021:{BASE}"}, C3: {"base": f"000021:{BASE}"}, S1: {"base": f"0B0S01:{MOD_A}"},
                S3: {"base": f"0B0S01:{MOD_A}"}}
        bases = {f"000021:{BASE}": {"type": "Static", "editorId": "CollisionMarker", "model": ""},
                 f"0B0S01:{MOD_A}": {"type": "Static", "editorId": "TestRock", "model": "rock.nif"}}
        order = {"without-patch": {"stateprobe": dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0,
                                                      created=2, newDefect=2, attributed=0, unmeasured=0)}}
        log = f"NEW     {kind}    {MOD_B}  x  {MOD_A}   +2 record(s): created 2\n"
        plan, md, html = compile_and_render(PATCHED_ARMS, {(SG_, "without-patch"): p_rows, (SG_, "baseline"): w_rows},
                                            [MOD_A, MOD_B], patches=[PATCH], names={"refs": refs, "bases": bases},
                                            order=order, diff_logs={(SG_, "without-patch"): log})
        reg = next((f for f in plan.get("findings") or [] if f.get("kind") == "regression" and f.get("checkId") == SG_), {})
        rc_ = (reg.get("counts") or {}).get("readerCreated")
        nm_ = [x for x in md.splitlines() if "has no model" in x or "have no model" in x]
        ok = rc_ == 2 and not nm_
        return ok, f"comparison readerCreated {rc_} (want 2); lines saying no model: {[x[:200] for x in nm_]}"

    # C1465. Control: a door with no model (station AD: no body in game) is set aside as having no body, as an activator with no
    # model is (C494).
    def c1465_modelless_door_set_aside():
        D1, H1 = f"000D01:{MOD_A}", f"000H05:{MOD_B}"
        arms = [arm("baseline", [BASE, OTHER, MOD_B, MOD_A], kind="baseline"), arm("without-a", [BASE, OTHER, MOD_B])]
        p = [row(f"STATE-CONTACT@0S0W03:{MOD_A}@Triggered", MOD_A, MOD_B, D1, other=H1)]
        refs = {D1: {"base": f"0B0D01:{MOD_A}"}, H1: {"base": f"0B0S01:{MOD_B}"}}
        bases = {f"0B0D01:{MOD_A}": {"type": "Door", "editorId": "TestPlaceholderDoor", "model": ""},
                 f"0B0S01:{MOD_B}": {"type": "Static", "editorId": "TestWall", "model": "wall.nif"}}
        plan, md, html = compile_and_render(arms, {(SG_, "baseline"): p, (SG_, "without-a"): []}, [MOD_A],
                                            names={"refs": refs, "bases": bases})
        adm_, fs_ = body_rule_of(plan, D1)
        ok = adm_ == ["noBody"] and len(fs_) >= 1 and all(nb_ == tot_ for _, tot_, nb_, _ in fs_)
        return ok, f"door with no model: admission {adm_}; findings {fs_}"

    # C1466. Rejects: the report's "no body" sentence naming markers as having no body (a CollisionMarker is a marker with no
    # model and a body); it names the types decided in game.
    def c1466_no_body_words_name_decided_types():
        plan, md, html = get("bodyrule", body_rule_bundle)
        lines = [x for x in md.splitlines() if "has no model" in x or "have no model" in x]
        bad = [x for x in lines if re.search(r"no model \([^)]*\bmarker\b", x)]
        ok = bool(lines) and not bad
        return ok, f"lines naming a marker as having no body: {[x[:220] for x in bad]}; no-model lines {len(lines)}"

    for name, fn in [("modelless-bodiless/C1450-collision-marker-route-row-kept", c1450_collision_marker_route_row_kept),
                     ("modelless-bodiless/C1465-control-modelless-door-set-aside", c1465_modelless_door_set_aside),
                     ("modelless-bodiless/C1466-no-body-words-name-decided-types", c1466_no_body_words_name_decided_types),
                     ("modelless-bodiless/C1451-control-modelless-light-set-aside", c1451_modelless_light_still_set_aside),
                     ("modelless-bodiless/C1452-control-modelled-activator-unchanged", c1452_modelled_activator_unchanged),
                     ("modelless-bodiless/C1453-collision-marker-counted-in-comparison", c1453_collision_marker_counted_in_comparison)]:
        cell(name, fn)

    # C1510-C1517. The sibling join compares probe builds by the program identity receipts bind (`consumed.programs`,
    # the emitter role), falls back to the dll fields only when both receipts are legacy, and refuses to join a check
    # whose two receipts cannot be compared, saying so in the join record.
    def prog_rows(emitter_digest, controller_digest="scripts:" + "5" * 64, emitter=True):
        rows_ = {"controller": {"program": "controller", "kind": "scripts", "digest": controller_digest,
                                "checked": ["launch", "acceptance"]}}
        if emitter:
            rows_["emitter"] = {"program": "probe:contactprobe", "kind": "package", "digest": emitter_digest,
                                "checked": ["launch", "acceptance"]}
        return rows_

    MAIN_ARMS_, SIB_ARMS_ = ("baseline", "without-patch"), ("baseline", "dependency-only", "plus-a", "plus-b")

    def bound(arms_, digest, **kw_):
        return {(CONTACT, a_): prog_rows(digest, **kw_) for a_ in arms_}

    def join_of(plan, check_id=CONTACT):
        sib_ = (reader(plan).get("prediction") or {}).get("sibling") or {}
        return next((c_ for c_ in sib_.get("checks") or [] if c_.get("checkId") == check_id), None)

    def contact_items(plan):
        return [(i.get("attribution"), i.get("count")) for i in (reader(plan).get("prediction") or {}).get("items") or []
                if i.get("checkId") == CONTACT]

    def index_line(index, check_id=CONTACT):
        return next((ln for ln in index.splitlines() if ln.startswith(f"- `{check_id}`:")), "")

    DIFFERENT_ = "the two evaluations ran this check with different probe builds"

    # C1510. Rejects: two evaluations whose probe dll is equal but whose emitter program differs (a changed dependency,
    # runtime file or copied data) joined as the same build.
    def c1510_equal_dll_different_program_not_joined():
        plan, md, html, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)},
            sib_extra={"programs": bound(SIB_ARMS_, "folder:" + "2" * 64)})
        j_ = join_of(plan) or {}
        ok = (j_.get("joined") is False and j_.get("why") == DIFFERENT_ and j_.get("buildBasis") == "program"
              and contact_items(plan) == [("not-separable", 3)])
        return ok, f"join record {j_}; contact items {contact_items(plan)}"

    # C1511. Rejects: a mixed pair (one evaluation under the tool identity contract, one legacy) joined with no word that
    # only the dlls were compared. Rule: the dll fallback, which every receipt still carries -- equal dlls join with
    # basis `dll` and a note that one side predates program identity (record and index line); different dlls do not.
    def c1511_mixed_pair_compared_by_dll():
        plan, md, html, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)})
        j_ = join_of(plan) or {}
        note_ = str(j_.get("buildBasisNote") or "")
        same_ok = (j_.get("joined") is True and j_.get("buildBasis") == "dll" and "predates program identity" in note_
                   and note_ in index_line(index) and contact_items(plan) == [("needs-both", 3)])
        plan2, _, _, index2 = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)},
            sib_extra={"tool_builds": {(CONTACT, "baseline"): "e" * 64}})
        j2_ = join_of(plan2) or {}
        diff_ok = (j2_.get("joined") is False and j2_.get("why") == DIFFERENT_ and j2_.get("buildBasis") == "dll"
                   and contact_items(plan2) == [("not-separable", 3)])
        return same_ok and diff_ok, (f"equal dlls: {j_}, index {index_line(index)!r}, items {contact_items(plan)}; "
                                     f"different dlls: {j2_}, items {contact_items(plan2)}")

    # C1512. Rejects: a legacy pair where the sibling's receipt on the shared setup records no dll hash, joined as the
    # same build (the old `mt[0] and st[0]` test fell through to a join).
    def c1512_missing_dll_not_comparable():
        plan, md, html, index = patched_with_sibling("same", sib_extra={"tool_builds": {(CONTACT, "baseline"): None}})
        j_ = join_of(plan) or {}
        why_ = str(j_.get("why") or "")
        ok = (j_.get("joined") is False and j_.get("buildBasis") == "not-comparable" and "cannot be told" in why_
              and why_ in index_line(index) and contact_items(plan) == [("not-separable", 3)])
        return ok, f"join record {j_}; index line {index_line(index)!r}; contact items {contact_items(plan)}"

    # C1513. Rejects: two receipts under the contract where the sibling's binds no emitter digest on the shared setup
    # (only the controller) and records no dll either, joined. Rule: missing hash -- neither basis is on both sides.
    def c1513_missing_emitter_digest_and_dll_not_comparable():
        sib_p = bound(SIB_ARMS_, "folder:" + "1" * 64)
        sib_p[(CONTACT, "baseline")] = prog_rows(None, emitter=False)
        plan, md, html, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)},
            sib_extra={"programs": sib_p, "tool_builds": {(CONTACT, "baseline"): None}})
        j_ = join_of(plan) or {}
        ok = (j_.get("joined") is False and j_.get("buildBasis") == "not-comparable"
              and "cannot be told" in str(j_.get("why") or "") and contact_items(plan) == [("not-separable", 3)])
        return ok, f"join record {j_}; contact items {contact_items(plan)}"

    # C1519. Rejects: two receipts under the contract where the sibling's binds no emitter digest but records its dll,
    # joined with no word that only the dlls were compared. Rule: the dll fallback -- basis `dll`, joined on equal dlls,
    # with a note that one side binds no program identity for its probe (record and index line).
    def c1519_contract_receipt_without_emitter_falls_back_to_dll():
        sib_p = bound(SIB_ARMS_, "folder:" + "1" * 64)
        sib_p[(CONTACT, "baseline")] = prog_rows(None, emitter=False)
        plan, md, html, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)}, sib_extra={"programs": sib_p})
        j_ = join_of(plan) or {}
        note_ = str(j_.get("buildBasisNote") or "")
        ok = (j_.get("joined") is True and j_.get("buildBasis") == "dll" and "binds no program identity" in note_
              and note_ in index_line(index) and contact_items(plan) == [("needs-both", 3)])
        return ok, f"join record {j_}; index line {index_line(index)!r}; contact items {contact_items(plan)}"

    # C1514. Control: identical emitter digests join, whatever the controller digest (it is not a build of the probe).
    def c1514_control_identical_programs_join():
        plan, md, html, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)},
            sib_extra={"programs": bound(SIB_ARMS_, "folder:" + "1" * 64, controller_digest="scripts:" + "6" * 64)})
        j_ = join_of(plan) or {}
        ok = j_.get("joined") is True and contact_items(plan) == [("needs-both", 3)]
        return ok, f"join record {j_}; contact items {contact_items(plan)}"

    # C1515. Control: two legacy receipts with equal dlls join as before.
    def c1515_control_legacy_equal_dll_join():
        plan, md, html, index = patched_with_sibling("same")
        j_ = join_of(plan) or {}
        ok = j_.get("joined") is True and contact_items(plan) == [("needs-both", 3)]
        return ok, f"join record {j_}; contact items {contact_items(plan)}"

    # C1516. Control: a check that runs no probe (the record check) joins on its rows as before: there is no probe
    # build to compare, and that is not a missing hash.
    def c1516_control_no_probe_check_joins():
        plan, md, html, index = sibling_record_join_bundle()
        j_ = join_of(plan, RECORDS) or {}
        return j_.get("joined") is True, f"record check join record {j_}"

    # C1517. Rejects: a join record that does not say which basis the build comparison used: program identity, the
    # legacy dll fields, or none because the check runs no probe; on the plan and on the evidence index line.
    def c1517_join_record_names_its_basis():
        got = {}
        plan, _, _, index = patched_with_sibling(
            "same", main_extra={"programs": bound(MAIN_ARMS_, "folder:" + "1" * 64)},
            sib_extra={"programs": bound(SIB_ARMS_, "folder:" + "1" * 64)})
        got["program"] = ((join_of(plan) or {}).get("buildBasis"), index_line(index))
        plan, _, _, index = patched_with_sibling("same")
        got["dll"] = ((join_of(plan) or {}).get("buildBasis"), index_line(index))
        plan, _, _, index = sibling_record_join_bundle()
        got["no-probe"] = ((join_of(plan, RECORDS) or {}).get("buildBasis"), index_line(index, RECORDS))
        words_ = {"program": "program identity", "dll": "dll", "no-probe": "no probe"}
        ok = all(b_ == k_ and words_[k_] in ln_ for k_, (b_, ln_) in got.items())
        return ok, f"basis and index line per case: {got}"

    # C1518. Rejects: a check left unseparated because the two builds cannot be compared, told on the page as the sibling
    # having "measured this check differently" (`siblingDisagrees`); it has its own words, and the page prints them.
    def c1518_page_says_builds_cannot_be_compared():
        plan, md, html, index = patched_with_sibling("same", sib_extra={"tool_builds": {(CONTACT, "baseline"): None}})
        nsw_ = (priorities().get("reader") or {}).get("prediction", {}).get("notSeparableWhy") or {}
        want_ = nsw_.get("siblingNotComparable")
        whys_ = [i.get("attributionWhy") for i in (reader(plan).get("prediction") or {}).get("items") or []
                 if i.get("checkId") == CONTACT]
        text = section_text(md, headings(plan).get("prediction") or "\0")
        ok = bool(want_) and want_ != nsw_.get("siblingDisagrees") and whys_ == [want_] and want_ in text
        return ok, f"words {want_!r}; contact item why {whys_}"

    for name, fn in [("sibling-build/C1510-equal-dll-different-program-not-joined", c1510_equal_dll_different_program_not_joined),
                     ("sibling-build/C1518-page-says-builds-cannot-be-compared", c1518_page_says_builds_cannot_be_compared),
                     ("sibling-build/C1511-mixed-legacy-and-new-pair-compared-by-dll", c1511_mixed_pair_compared_by_dll),
                     ("sibling-build/C1512-missing-dll-hash-not-comparable", c1512_missing_dll_not_comparable),
                     ("sibling-build/C1513-missing-emitter-digest-and-dll-not-comparable",
                      c1513_missing_emitter_digest_and_dll_not_comparable),
                     ("sibling-build/C1519-contract-receipt-without-emitter-falls-back-to-dll",
                      c1519_contract_receipt_without_emitter_falls_back_to_dll),
                     ("sibling-build/C1514-control-identical-programs-join", c1514_control_identical_programs_join),
                     ("sibling-build/C1515-control-legacy-equal-dll-join", c1515_control_legacy_equal_dll_join),
                     ("sibling-build/C1516-control-no-probe-check-joins", c1516_control_no_probe_check_joins),
                     ("sibling-build/C1517-join-record-names-its-basis", c1517_join_record_names_its_basis)]:
        cell(name, fn)

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
