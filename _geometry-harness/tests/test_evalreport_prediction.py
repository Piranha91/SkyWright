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
         FIXTURE: None, OVERLAP: "overlapprobe"}
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


def lost_row(a, b, form_key, field, lost, winner, overridden, label=None):
    """A record lane row: `a`'s authored value of `field` (a change from `overridden`) is lost to `b`'s `winner`."""
    return {"Kind": "LOST-EDIT", "A": a, "B": b, "Field": field,
            "Summary": f"PlacedObject {form_key[:6]}: {field} - {a}'s value lost to {b}'s",
            "Necessary": [{"FormKey": form_key, "Label": label or f"record {form_key[:6]}",
                           "Values": {"lost": lost, "winner": winner, "overridden": overridden}}]}


def make_bundle(root: Path, arms, results, targets, patches=(), missing=(), unjudgeable=(), order=None,
                scope_unmeasured=(), count_only=None, cells=(), plan_id="pred00000000", tool_builds=None,
                enrichment=None, receipt_configs=None, names=None, enrichment_unjudged=None, predicates=None):
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
    measured against, measured again in that arm's setup (an emit's `Predicates`)."""
    root.mkdir(parents=True, exist_ok=True)
    arm_plugins = {a["armId"]: a["requested"]["plugins"] for a in arms}

    def config_of(check_id, arm_id):
        return list((receipt_configs or {}).get((check_id, arm_id)) or arm_plugins.get(arm_id) or [])
    (root / "receipts").mkdir(exist_ok=True)
    (root / "evidence").mkdir(exist_ok=True)
    count_only = dict(count_only or {})
    checks = sorted({c for c, _ in results} | {c for c, _, _ in missing} | {c for c, _ in count_only})
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
                **({"Predicates": predicates[(check_id, arm_id)]} if (check_id, arm_id) in (predicates or {}) else {})}
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
                           "missing": [{"checkId": c, "armId": a, "part": "arm", "status": s, "cause": s,
                                        "required": True, "family": c.split(".")[0]} for c, a, s in missing]},
            "arms": arms_disp, "importedEvidence": [],
            "unassessed": {"unjudgeablePopulations": list(unjudgeable), "scopeUnmeasured": list(scope_unmeasured)}}
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


def compile_and_render(arms, results, targets, want_index=False, sibling=None, settings_extra=None, **kw):
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
              and hit[0].get("attribution") == "single-mod" and hit[0].get("attributionMod") == MOD_A
              and USSEP in text)
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
        line = f"between {MOD_A} and {OTHER}"
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
        ok = (val.get("hits") == 1 and val.get("open") == 1 and val.get("kept") == 1 and val.get("patchProblems") == 0)
        return ok, (f"hits {val.get('hits')}, kept {val.get('kept')}, open {val.get('open')}, "
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
              and {contact_n, 2} <= nums and not nums & {11, 6, 5, 10, 7}
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
              and f"already in this state with {MOD_A} alone" in md)
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

    # C463. A lost edit the patch keeps on purpose is its own count, printed, never "left"
    def c463_patch_kept_winner_is_not_left():
        plan, md, html = record_validation_bundle()
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = val.get("kept") == 1 and val.get("open") == 1 and "kept on purpose" in text
        return ok, f"kept {val.get('kept')}, open {val.get('open')}; printed: {'kept on purpose' in text}"

    # C464. A placeholder winner value is never compared: cannot tell, said on the page
    def c464_placeholder_value_cannot_tell():
        plan, md, html = placeholder_value_bundle()
        val = reader(plan).get("validation") or {}
        text = section_text(md, headings(plan).get("validation") or "\0")
        ok = (val.get("hits") == 0 and val.get("kept") == 0 and val.get("open") == 1 and val.get("cannotTell") == 2
              and "does not record the winning value" in text)
        return ok, (f"hits {val.get('hits')}, kept {val.get('kept')}, open {val.get('open')}, "
                    f"cannot tell {val.get('cannotTell')}")

    for name, fn in [("w2-review/C463-patch-kept-winner-is-its-own-count-not-left", c463_patch_kept_winner_is_not_left),
                     ("w2-review/C464-placeholder-winner-value-is-cannot-tell", c464_placeholder_value_cannot_tell),
                     ("w2-review/C460-third-mod-object-broken-with-one-mod-alone-is-single-mod",
                      c460_object_broken_with_one_mod_alone),
                     ("w2-review/C461-lost-edit-attributed-per-field", c461_lost_edit_attributed_per_field),
                     ("w2-review/C462-overlap-judged-by-its-pair-predicate-with-the-patch",
                      c462_overlap_judged_by_pair_predicate)]:
        cell(name, fn)

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
