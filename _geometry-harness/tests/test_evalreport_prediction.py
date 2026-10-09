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

def plugins(*names):
    return [{"name": n, "sha256": "0" * 64, "path": f"X:/x/{n}"} for n in names]


def arm(arm_id, names, kind="candidate"):
    return {"armId": arm_id, "kind": kind, "candidateFile": f"{arm_id}.json", "provenance": {},
            "requested": {"plugins": plugins(*names), "providers": [], "overlays": [], "population": "load-time"},
            "expected": {"states": ["baseline"], "population": "load-time"}}


def row(kind, a, b, form_key, label=None):
    return {"Kind": kind, "A": a, "B": b, "Summary": f"{kind} between {a} and {b}",
            "Necessary": [{"FormKey": form_key, "Label": label or f"object {form_key[:6]}", "Physical": "Holds"}]}


def make_bundle(root: Path, arms, results, targets, patches=(), missing=(), unjudgeable=(), order=None):
    """results: {(checkId, armId): [conflict rows]} -- an empty list is a completed check with no finding.
    missing: [(checkId, armId, status)]. unjudgeable: disposition unassessed.unjudgeablePopulations rows.
    order: {armId: {probe: perProbe counts}} -- the controller's order evaluation of that arm against the baseline."""
    root.mkdir(parents=True, exist_ok=True)
    (root / "receipts").mkdir(exist_ok=True)
    (root / "evidence").mkdir(exist_ok=True)
    checks = sorted({c for c, _ in results} | {c for c, _, _ in missing})
    plan = {"planId": "pred00000000", "createdUtc": "2026-01-01T00:00:00+00:00",
            "targets": [{"name": t} for t in targets], "arms": arms, "states": {"selected": ["baseline"]},
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
            "unassessed": {"unjudgeablePopulations": list(unjudgeable)}}
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


def compile_and_render(arms, results, targets, want_index=False, **kw):
    """The real compiler and the real templates: (plan, report.md, report.html)."""
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
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
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


def base_game_only_bundle():
    """One mod whose only measured difference is against the base game, and no other setup measured."""
    arms = [arm("baseline", [BASE, MOD_A], kind="baseline")]
    results = {(CONTACT, "baseline"): [row("CONTACT", BASE, MOD_A, f"000A01:{MOD_A}")]}
    return compile_and_render(arms, results, [MOD_A])


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

    for name, fn in [
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
