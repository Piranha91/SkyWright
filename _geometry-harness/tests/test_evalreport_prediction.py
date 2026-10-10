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

import json
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import evalreport as R                                               # noqa: E402

PRIORITIES = HERE.parent.parent / ".claude/skills/evaluation-reporting/references/report-priorities.json"
CONTACT = "geometry.placement-contact"
SEAMS = "environment.land-seams"
WATER = "environment.water-boundaries"
PROBE = {CONTACT: "contactprobe", SEAMS: "seamprobe", WATER: "waterprobe"}
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


def row(kind, a, b, form_key, label=None):
    return {"Kind": kind, "A": a, "B": b, "Summary": f"{kind} between {a} and {b}",
            "Necessary": [{"FormKey": form_key, "Label": label or f"object {form_key[:6]}", "Physical": "Holds"}]}


def make_bundle(root: Path, arms, results, targets, patches=(), missing=(), unjudgeable=(), order=None,
                scope_unmeasured=(), count_only=None, cells=(), plan_id="pred00000000"):
    """results: {(checkId, armId): [conflict rows]} -- an empty list is a completed check with no finding.
    missing: [(checkId, armId, status)]. unjudgeable: disposition unassessed.unjudgeablePopulations rows.
    order: {armId: {probe: perProbe counts}} -- the controller's order evaluation of that arm against the baseline.
    count_only: {(checkId, armId): n} -- a check that reported n findings with no conflict rows (no `Conflicts`).
    cells: the first target's placed cells [{worldspace, x, y}] -- the locations every setup's load-order list covers."""
    root.mkdir(parents=True, exist_ok=True)
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
                              "coverage": [{"Population": check_id, "Judged": 40, "Unjudgeable": {}}]}}
        emit = {"Population": "placed references", "State": "baseline", "Conflicts": rows,
                "Coverage": [{"Population": check_id, "Judged": 40, "Unjudgeable": {}}]}
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
                   "result": {"detector": "finding", "findings": count}}
        (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipt), encoding="utf-8")
        (root / "evidence" / f"{rid}.json").write_text(json.dumps({"Population": "placed references"}), encoding="utf-8")
        evidence.append({"id": f"{2 * n:016x}", "kind": "receipt", "path": f"receipts/{rid}.json", "sha256": "0" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid})
        evidence.append({"id": f"{2 * n + 1:016x}", "kind": "emit", "path": f"evidence/{rid}.json", "sha256": "1" * 64,
                         "checkId": check_id, "armId": arm_id, "recordedBy": rid, "strength": "geometric-measurement"})
        findings.append({"checkId": check_id, "armId": arm_id, "part": "arm", "findings": count,
                         "verdict": None, "strength": "geometric-measurement"})
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


def compile_and_render(arms, results, targets, want_index=False, sibling=None, **kw):
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

def four_setup_unpatched():
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
    return compile_and_render(arms, results, [MOD_A, MOD_B], missing=missing, order=order)


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
    only two of the three clashes."""
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
                              sibling=(s_arms, s_results, [MOD_A, MOD_B], {"plan_id": "sibl00000000"}))


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
        ok = (line.count("7") == 1 and "for tested alternative setup" in cov4 and "for Tested" not in cov4)
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

    def sibling_foreign_refused():
        try:
            patched_with_sibling("foreign")
        except Exception as ex:                                       # noqa: BLE001
            ok = type(ex).__name__ == "InputRefused" and "sibling" in str(ex).lower() and str(ex).count(". ") == 0
            return ok, f"raised {type(ex).__name__}: {ex}"
        return False, "a sibling with no setup matching the one without the patch was accepted"

    def sibling_disagreement_unseparated():
        plan, md, html, index = patched_with_sibling("disagrees")
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

    for name, fn in [
            ("detail-list/only-clashes-between-mods-are-listed", detail_only_inter_mod),
            ("detail-list/set-aside-results-are-in-the-evidence-index-with-why", detail_set_aside_in_index),
            ("detail-list/already-present-pair-is-not-listed", detail_already_present_not_listed),
            ("sibling-bundle/patched-report-attributes-from-the-unpatched-evaluation", sibling_attributes),
            ("sibling-bundle/a-sibling-without-the-setup-is-refused", sibling_foreign_refused),
            ("sibling-bundle/a-check-measured-differently-stays-unseparated", sibling_disagreement_unseparated),
            ("sibling-bundle/plan-takes-the-sibling-bundle-option", sibling_cli_flag),
            ("plain-language/the-measured-load-order-takes-its-article", measured_load_order_article),
            ("page-budget/a-shared-list-of-locations-is-printed-once", shared_scope_printed_once),
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

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
