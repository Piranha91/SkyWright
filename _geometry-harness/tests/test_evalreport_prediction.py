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

import html as _html
import json
import re
import sys
import tempfile
import traceback
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
         "functional.quest-alias-split": "aliasprobe"}
NAVOWN = "geometry.navmesh-ownership"
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
                diff_logs=None, states_not_explored=None, transcripts=None, masters=None):
    """results: {(checkId, armId): [conflict rows]} -- an empty list is a completed check with no finding.
    missing: [(checkId, armId, status)]. unjudgeable: disposition unassessed.unjudgeablePopulations rows.
    order: {armId: {probe: perProbe counts}} -- the controller's order evaluation of that arm against the baseline.
    count_only: {(checkId, armId): n} -- a check that reported n findings with no conflict rows (no `Conflicts`).
    cells: the first target's placed cells [{worldspace, x, y}] -- the locations every setup's load-order list covers.
    tool_builds: {(checkId, armId): dllSha256} -- the probe build a receipt records; every other receipt records one
    shared build.
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
    missing rows may carry a fourth element, the part (default `arm`)."""
    root.mkdir(parents=True, exist_ok=True)
    arm_plugins = {a["armId"]: a["requested"]["plugins"] for a in arms}

    def config_of(check_id, arm_id):
        return list((receipt_configs or {}).get((check_id, arm_id)) or arm_plugins.get(arm_id) or [])
    (root / "receipts").mkdir(exist_ok=True)
    (root / "evidence").mkdir(exist_ok=True)
    count_only = dict(count_only or {})
    checks = sorted({c for c, _ in results} | {m[0] for m in missing} | {c for c, _ in count_only})
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
        rid = f"{check_id}__{arm_id}__arm"
        receipt = {"receiptId": rid, "checkId": check_id, "armId": arm_id, "part": "arm", "schemaVersion": 1,
                   "exitCode": 0, "validation": {"accepted": True, "identityLimits": []},
                   "result": {"detector": "finding" if rows else "null", "findings": len(rows),
                              "coverage": [{"Population": check_id, "Judged": 40, "Unjudgeable": {}}]},
                   "consumed": {"tool": {"exe": PROBE[check_id], "dllSha256": (tool_builds or {}).get(
                       (check_id, arm_id), "d" * 64), "coreDllSha256": "c" * 64},
                                "configuration": {"plugins": config_of(check_id, arm_id)}}}
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
            findings.append({"checkId": check_id, "armId": arm_id, "part": "arm", "findings": len(rows),
                             "verdict": None, "strength": "geometric-measurement"})
        else:
            nulls.append({"checkId": check_id, "armId": arm_id, "part": "arm", "coverage": 1})
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
                       **kw):
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
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir), **extra)
        view = R.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], pr, "normal")
        out = R.render_all(plan, view, Path(td) / "out", "")
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


def patched_with_sibling(variant="same"):
    """The patched bundle (the patched order and the same order without the patch; no single-mod setup) compiled with
    a sibling: the evaluation of the unpatched order, which measured both mods, each alone and neither.
    variant `same`: the sibling's setup with both mods is the patched bundle's setup without the patch, plugin for
    plugin and hash for hash, and measured the same rows there. `foreign`: the sibling's ModA has other bytes, so no
    sibling setup is the one without the patch. `disagrees`: same plugins, but on that setup the sibling measured
    only two of the three clashes. `build`: same plugins and rows, but the sibling ran the check with another probe
    build on that setup."""
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
                              sibling=(s_arms, s_results, [MOD_A, MOD_B], {
                                  "plan_id": "sibl00000000",
                                  "tool_builds": {(CONTACT, "baseline"): "e" * 64} if variant == "build" else None}))


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

def main():
    cells = []

    def cell(name, fn):
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
        ok = (adm == [("000L01", "noBody"), ("000L02", "admitted"), ("000S01", "admitted")]
              and val.get("patchProblems") == 1 and "Also not counted: 1 clashes between the mods whose object's record has no model" in md
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
        ok = (bi.get("open") == 3 and bi.get("openListed") == 1 and bi.get("openPatchHeld") == 1
              and bi.get("openNotAttributed") == 1 and "1 still intersect, not attributed" in line
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
        line = next((x for x in sec.splitlines() if "navmesh replaces" in x), "")
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
              f"\"{priorities()['reader']['checkTitles'].get(FIXTURE)}\" (counted once, above)" in v)
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
        ok = f"For example: 000702:{BASE} (Placement); 00003C:{BASE} (HdLodDiffuseTexture)" in md
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
        line = next((x for x in text.splitlines() if "untouched" in x), "")
        ok = ("untouched" in line and re.search(r"\b2 left untouched\b", line) is not None
              and re.search(r"\b1 where a patch edits one of the two objects\b", line) is not None
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
        p_rows = [row("EDGE-IDX", MOD_A, MOD_B, f"000E0{i}:{BASE}", label="CityExterior03 (ext)", other=n1) for i in (1, 2, 3)]
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
        ok = (f"3 inconsistencies found by the check \"{title}\" (2 of them objects already counted above)" in v)
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
        ok = (f"{MOD_A}'s change stays lost" in line and f"{MOD_A}'s change stays lost" in items
              and "keeps the winning value" not in md and f"{MOD_A}'s change stays lost" in md)
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
        want = f"the patch keeps {MOD_A}'s value; {MOD_B}'s change is dropped 1"
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
                  lost_row(MOD_B, PATCH, f"000703:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                           "Position=4,5,6; Rotation=0,0,0", "v")]
        enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
        count_only = {(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 3}
        results = {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}
        plan, md, html = compile_and_render(PATCH_ADDED_ARMS, results, [MOD_A, MOD_B], patches=[PATCH],
                                            count_only=count_only, enrichment=enr)
        val = reader(plan).get("validation") or {}
        line = next((ln for ln in md.splitlines() if ln.startswith(f"- Undone by {PATCH}")), "")
        new = next((ln for ln in md.splitlines() if ln.startswith("- New clashes in the setup")), "")
        ok = (val.get("undone") == 2 and val.get("undoneBy") == {MOD_A: 1, MOD_B: 1}
              and f"2 changes the mods made ({MOD_A} 1, {MOD_B} 1)" in line
              and f"1 of them switch an object back on, for example 000702:{BASE}" in line
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
        w_rows = [lost_row(MOD_A, PATCH, f"000702:{BASE}", "Name", "a2", "p2", "v2")]
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
              and "objects" not in hits and "the same object also found by the check" in v and "the same 1 objects" not in v)
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
                    p_tr=None, names=None):
        enr = {(check, p_arm): list(p_rows)}
        tr = {(check, p_arm): records_transcript(p_chains)} if p_chains else {}
        if p_tr is not None:
            tr[(check, p_arm)] = p_tr
        if w_arm:
            enr[(check, w_arm)] = list(w_rows)
            if w_chains:
                tr[(check, w_arm)] = records_transcript(w_chains)
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
        dropped = [ln for ln in md.splitlines() if "change is dropped" in ln]
        ok = (w1 == ["resolved"] and w2 == ["switched"] and dropped
              and all("dropped 1" in ln and "dropped 2" not in ln for ln in dropped))
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
        ok = "1 record field, in 2 plugin pairs" in ln
        return ok, f"line {ln[:160]!r}"

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
            lost_row(CASE, MOD_A, f"00C31{i}:{BASE}", "Model", "Meshes\X\A.nif", "meshes/x/a.nif", "v") for i in (2, 3)]
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
        ok = "1 record field, in 1 plugin pair," in ln and "1 more" in ln
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
        dropped = [ln for ln in md.splitlines() if "change is dropped" in ln]
        ok = w1 == ["resolved"] and w2 == ["switched"] and dropped and all("dropped 2" not in ln for ln in dropped)
        return ok, f"S1 {w1}, S2 {w2}; {[ln[:120] for ln in dropped]}"

    # C663. "Undone by the patch: N changes the mods made" counts only changes a mod made: ModA's value lost to the patch
    # is its master's (the base game's), so the patch undoes nothing of ModA's there; ModB's own change it undoes is
    # counted.
    def c663_undone_counts_own_changes():
        U1, U2 = f"00C631:{BASE}", f"00C632:{BASE}"
        arms = [arm("baseline", [BASE, THIRD, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, THIRD, MOD_A, MOD_B])]
        w_rows = [lost_row(MOD_A, PATCH, U1, "Owner", "1", "7", "5", before=THIRD),
                  lost_row(MOD_B, PATCH, U2, "Owner", "8", "7", "1", before=MOD_A)]
        w_chains = [(U1, "PlacedObject", [(BASE, {"Owner": "1"}), (THIRD, {"Owner": "5"}), (MOD_A, {"Owner": "1"}),
                                          (PATCH, {"Owner": "7"})]),
                    (U2, "PlacedObject", [(BASE, {"Owner": "1"}), (MOD_A, {"Owner": "1"}), (MOD_B, {"Owner": "8"}),
                                          (PATCH, {"Owner": "7"})])]
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
        ok = (f"In 1 of them {MOD_A} changed the field itself too" in line and '"o1"' in line and '"a1"' in line
              and '"o2"' not in line)
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
        top = md.split("\n## ", 2)[1] if "\n## " in md else md
        still = next((ln for ln in top.splitlines() if ln.startswith("Still there")), "")
        ok = got == {HF: "baseGame", OTHER: "admitted"} and f"{OTHER}'s change" in still
        return ok, f"{got}; {still[:200]!r}"

    # C726 (Clockwork review S6). "Undone by the patch" applies the same set-aside rules and counts a field once: a list
    # whose items the patch only reorders is no change undone, and a field two record checks both list is one field.
    def c726_undone_same_set_asides_once():
        U1, U2 = f"00C728:{BASE}", f"00C729:{BASE}"
        arms = [arm("baseline", [BASE, OTHER, MOD_A, MOD_B, PATCH], kind="baseline"),
                arm("without-patch", [BASE, OTHER, MOD_A, MOD_B])]
        reg = lost_row(OTHER, PATCH, U1, "Regions", ["Regions: same 2 item(s), ORDER DIFFERS from ModA - ModB patch.esp"],
                       PH, PH)
        own_ = lost_row(OTHER, PATCH, U2, "Owner", "o", "p", "v")
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
        ok = got == {UPD: "undoesMaster", OTHER: "sameValue"} and "Location" not in pred.get("verdict", "")
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
        line = next((ln for ln in md.splitlines() if "only undoes the change" in ln), "")
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
              and f"Removing {PATCH} brings back the 1 record field it resolves" in md)
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
                 "bases": {f"0BC001:{BASE}": {"type": "Static", "editorId": "COCMarkerHeading", "model": "m.nif"}}}
        plan, md, _ = core_bundle([r1, r2], [], names=names)
        ok = f"{R1} (RorlundREF, Placement)" in md and f"{R2} (a COCMarkerHeading, Placement)" in md
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
        ok = f"All 2 are against one object, {Y} (a WallPiece)" in line
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
        line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "OtherMod.esp's value is lost" in ln), "")
        ok = f"was first set by {HF_} and is carried by {OTHER}" in line and "018A59" in line
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
        return "1 still crossing where a patch places one of them" in st, st[:300]

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
        p_ch = [(D, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_)])
                for D, b_ in ((D1, b_off), (D2, b_mv), (D3, b_low))]
        w_ch = [(D1, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_off)]),
                (D2, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_mv)]),
                (D3, "PlacedObject", [(BASE, offs(0)), (MOD_A, a_), (MOD_B, b_low), (PATCH, b_low)])]
        return core_bundle(p_rows, w_rows, p_chains=p_ch, w_chains=w_ch, masters={**MASTERS_, PATCH: [BASE]})

    def c900_offwin_said_where_still_there():
        plan, md, _ = offwin_bundle()
        st = pair_parts((reader(plan).get("prediction") or {}).get("withPatchStays"), MOD_A, MOD_B)
        return off_said(" ".join(st)) == 2, f"{st}"

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
        carried = any(re.search(r"(?<![\d,])1(?![\d,])", x) and "carr" in x for x in st if not
                      re.search(re.escape(MOD_A) + r"'s changes to 1 record field\b", x)) or any(
                      "carr" in x and re.search(r"(?<![\d,])1(?![\d,])", x.split("record field", 1)[-1]) for x in st)
        return n_left == 1 and left_part and carried, f"summary left {n_left}; {st}"

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
        for m_ in re.finditer(r"\((\d[\d,]*) on objects set off for good", text):
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
        ok = (not bad and "off for good" in md and len(re.findall(r"forwarding the \d+ on/off field", line)) == 1
              and re.search(r"forwarding the 1 on/off field among these\b", line) is not None
              and "brings them back" not in md)
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
        ok = "switch an object back on" in line and holds
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
        kl = [ln for ln in md.splitlines() if "overlap pairs)" in ln and "left 2" in ln]
        ok = ("2 still crossing where a patch places one of them" in st and kl
              and all("not attributed" not in ln and "where a patch places one of them" in ln for ln in kl))
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

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
