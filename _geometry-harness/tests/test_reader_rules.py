#!/usr/bin/env python
"""Known answers for the report and reel rules a mod user acts on: a verdict and a next step per patch, unfinished
testing said up front and never read as clean, one named patch per outcome, objects named by the plugin that made
them, every produced picture shown or named as left out, the patch's own edits named as they are, a claim checked
against the setups that answer it, and the reel's counts, labels, pages and closing in the report's own terms.

Each case drives the real compiler, templates and reel planner on a small bundle-shaped fixture (the prediction
suite's builder) or on a recorded presentation, and asserts what a reader sees: the plan's field, report.md,
report.html and the reel plan. Each rule has a case where it must not fire.

    python _geometry-harness/tests/test_reader_rules.py            every case
    python _geometry-harness/tests/test_reader_rules.py NAME...    the named cases (a prefix match)
Exit 0 all pass, 1 a case failed.
"""
from __future__ import annotations

import html as _html
import json
import os
import re
import sys
import tempfile
import traceback

NL = chr(10)
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import test_evalreport_prediction as P  # noqa: E402  (sets the no-settings environment before evalreport loads)
import evalreport as R  # noqa: E402
import reel_plan  # noqa: E402
import reel_stills  # noqa: E402

SKILL = HERE.parent.parent / ".claude/skills/evaluation-reporting/references"
PR = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
POLICY = dict(json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"])
POLICY["effectModelPattern"] = ((PR.get("reader") or {}).get("prediction") or {}).get("effectModelPattern")
WORDS = PR["reel"]["words"]
BASE, A, B, OTHER = P.BASE, P.MOD_A, P.MOD_B, P.OTHER
PATCH = P.PATCH
LEG = "Partner.esm"
PATCH_L = "ModA - Partner patch.esp"
P1, P2 = "First patch.esp", "Second patch.esp"
OVERLAP, CONTACT, RECORDS = P.OVERLAP, P.CONTACT, P.RECORDS
FONT, BOLD = reel_stills.font_files(None, PR.get("reel") or {})


def unesc(h):
    return _html.unescape(re.sub(r"<[^>]+>", " ", h))


def pred_of(plan):
    return (plan.get("reader") or {}).get("prediction") or {}


def reel_of(pres, mods=None):
    """The reel the planner makes of a presentation, with every plugin named."""
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p)} for p in mods or []}
    return reel_plan.plan_reel(pres, {}, {}, POLICY, WORDS, None, names, captures_final=True)


def chapter(reel, kind):
    return next((c for c in reel.get("chapters") or [] if c.get("kind") == kind), None)


# --------------------------------------------------------------------- a verdict and a next step for each patch

def contact_patch_bundle(patch=PATCH, masters=None, base_extra=(), left=1, missing=()):
    """Three objects of ModA left without support by ModB; the setup adding `patch` leaves `left` of them."""
    arms = [P.arm("baseline", [BASE, *base_extra, A, B], kind="baseline"),
            P.arm("with-patch", [BASE, *base_extra, A, B, patch])]
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2, 3)]
    results = {(CONTACT, "baseline"): rows, (CONTACT, "with-patch"): rows[3 - left:]}
    return P.compile_and_render(arms, results, [A, B], patches=[patch], missing=list(missing),
                                masters=masters if masters is not None else {patch: [BASE, A, B], A: [BASE], B: [BASE]})


def case_verdict_names_what_the_patch_resolves():
    plan, md, html = contact_patch_bundle()
    todo = pred_of(plan).get("whatToDo") or []
    want = f"Use {PATCH} if you load {A} and {B}: it resolves 2 of the 3 predicted clashes between them; 1 is left."
    line = next((x for x in todo if x.startswith(want)), "")
    ok = (bool(line) and "It adds no new clash the completed checks see." in line and line in md and line in unesc(html))
    return ok, f"what to do {todo}"


def case_verdict_on_a_patch_for_another_pair():
    """A patch built for the mod and another (its masters) is not judged by clashes it was never made for."""
    plan, md, html = contact_patch_bundle(patch=PATCH_L, masters={PATCH_L: [BASE, A, LEG], A: [BASE], B: [BASE]},
                                          base_extra=(LEG,), left=3)
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    want = f"{PATCH_L} is a patch for {A} and {LEG}. None of the predicted clashes is between them"
    ok = want in todo and "resolves none" not in todo and want in md
    return ok, f"what to do {todo[:300]!r}"


def records_patch_bundle(missing=()):
    """ModA's change to three records lost to ModB; the patch puts one back, keeps one, and leaves one."""
    p_rows = [P.lost_row(A, B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2, 3)]
    w_rows = [P.lost_row(B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
              P.lost_row(A, B, f"000702:{BASE}", "LocationReference", "a2", "b2", "v"),
              P.lost_row(A, PATCH, f"000703:{BASE}", "LocationReference", "a3", "b3", "v")]
    enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows}
    count_only = {(RECORDS, "baseline"): 3, (RECORDS, "with-patch"): 3}
    return P.compile_and_render(P.PATCH_ADDED_ARMS, {}, [A], patches=[PATCH], count_only=count_only, enrichment=enr,
                                missing=list(missing), masters={PATCH: [BASE, A, B], A: [BASE], B: [BASE]})


def case_verdict_over_unfinished_world_checks_is_not_clean():
    """No check of objects in the world finished with the patch: the verdict and the summary never read as clean."""
    miss = [(OVERLAP, "baseline", "stopped(deadline)"), (OVERLAP, "with-patch", "stopped(deadline)"),
            (CONTACT, "with-patch", "skipped-budget")]
    plan, md, html = records_patch_bundle(missing=miss)
    todo = pred_of(plan).get("whatToDo") or []
    summ = ((plan.get("reader") or {}).get("validation") or {}).get("summary") or ""
    reel = reel_of(plan, [BASE, A, B, PATCH])
    verdict = chapter(reel, "verdict") or {}
    patch_line = next((x for x in todo if PATCH in x and "predicted" in x), "")
    spoken = patch_line
    for p_ in sorted([BASE, A, B, PATCH], key=len, reverse=True):
        spoken = spoken.replace(p_, re.sub(r"\.(esp|esm|esl)$", "", p_))
    ok = (bool(patch_line) and "No check of objects in the world finished on the setup with it" in patch_line
          and "adds no new clash" not in md and "adds no new clash" not in unesc(html)
          and "no check of objects in the world finished with the patch" in summ
          and "no new clash the checks can see" not in md
          and spoken in str(verdict.get("narration")) and ".esp" not in str(verdict.get("narration"))
          and "adds no new clash" not in str(verdict.get("narration")))
    return ok, f"patch line {patch_line!r}; summary {summ[-200:]!r}; reel verdict {str(verdict.get('narration'))[:160]!r}"


def case_verdict_over_finished_checks_says_no_new_clash():
    plan, md, html = contact_patch_bundle()
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = ("It adds no new clash the completed checks see." in todo and "did not finish" not in todo
          and not pred_of(plan).get("incompleteLine"))
    return ok, f"what to do {todo[:300]!r}; incomplete {pred_of(plan).get('incompleteLine')!r}"


def case_no_patch_no_verdict_line_but_next_step():
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows}, [A])
    todo = pred_of(plan).get("whatToDo") or []
    ok = (len(todo) == 1 and "check them in game at the places its item below names" in todo[0]
          and "Use " not in todo[0] and todo[0] in md)
    return ok, f"what to do {todo}"


# ----------------------------------------------------------- unfinished testing said up front, never as clean

def budget_bundle():
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("dependency-only", [BASE, OTHER])]
    p_rows = [P.lost_row(OTHER, A, f"00070{i}:{BASE}", "LocationReference", "o", "a", "v") for i in (1, 2)]
    miss = [(OVERLAP, "baseline", "stopped(deadline)"), (CONTACT, "baseline", "skipped-budget"),
            (OVERLAP, "dependency-only", "stopped(deadline)"), (CONTACT, "dependency-only", "skipped-budget")]
    return P.compile_and_render(arms, {}, [A], count_only={(RECORDS, "baseline"): 2, (RECORDS, "dependency-only"): 0},
                                enrichment={(RECORDS, "baseline"): p_rows, (RECORDS, "dependency-only"): []},
                                missing=miss)


def case_incomplete_testing_said_first_with_its_cause():
    plan, md, html = budget_bundle()
    line = pred_of(plan).get("incompleteLine") or ""
    head = P.section_text(md, P.headings(plan).get("prediction") or "\0")
    val = (plan.get("reader") or {}).get("validation") or {}
    reel = reel_of(plan, [BASE, OTHER, A])
    opening = chapter(reel, "opening") or {}
    ok = ("Testing is incomplete: 2 of the 3 checks on" in line
          and "2 of them because the evaluation's time budget ran out" in line
          and "None of the checks of objects in the world finished there" in line
          and line in head and line in unesc(html)
          and "No new physical defect" not in md and "No new physical defect" not in unesc(html)
          and "No check of objects in the world finished on the tested alternative setups" in str(val.get("noRegressionLine"))
          and "Testing is incomplete" in str(opening.get("narration")))
    return ok, f"line {line!r}; regression line {val.get('noRegressionLine')!r}; opening {str(opening.get('narration'))[:200]!r}"


def case_finished_world_checks_keep_their_all_clear():
    plan, md, html = P.none_found_bundle()
    line = pred_of(plan).get("incompleteLine") or ""
    val = (plan.get("reader") or {}).get("validation") or {}
    ok = ("None of the checks of objects in the world finished" not in line
          and val.get("noRegressionLine") in (None, "No new physical defect was reported by the completed comparisons."))
    return ok, f"line {line!r}; regression line {val.get('noRegressionLine')!r}"


# ------------------------------------------------------------------------------- one outcome, one named patch

def two_patch_bundle():
    """The setup adds two patches; only the first carries the records whose values it puts back or sets."""
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline"), P.arm("with-patches", [BASE, A, B, P1, P2])]
    p_rows = [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]
    w_rows = [P.lost_row(B, P1, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
              P.lost_row(A, P1, f"000702:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0"),
              P.lost_row(B, P1, f"000703:{BASE}", "Placement", "Position=1,2,3; Rotation=0,0,0",
                         "Position=4,5,6; Rotation=0,0,0", "Position=4,5,6; Rotation=0,0,0")]
    enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "with-patches"): w_rows}
    count_only = {(RECORDS, "baseline"): 1, (RECORDS, "with-patches"): 3}
    results = {(CONTACT, "baseline"): [], (CONTACT, "with-patches"): []}
    return P.compile_and_render(arms, results, [A, B], patches=[P1, P2], count_only=count_only, enrichment=enr)


def case_undone_changes_name_the_patch_that_carries_them():
    plan, md, html = two_patch_bundle()
    lines = [ln for ln in md.splitlines() if ln.startswith("- Undone by ")]
    ok = (bool(lines) and all(ln.startswith(f"- Undone by {P1} ") for ln in lines)
          and f"{P1}, {P2}" not in " ".join(lines) and P2 not in " ".join(lines))
    return ok, f"undone lines {lines}"


def case_single_patch_undone_line_unchanged():
    """With one patch, the undone line names it as before (the prediction suite's single-patch cases hold the bytes)."""
    p_rows = [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]
    w_rows = [P.lost_row(B, PATCH, f"000701:{BASE}", "LocationReference", "b1", "a1", "a1"),
              P.lost_row(A, PATCH, f"000702:{BASE}", "SkyrimMajorRecordFlags", "InitiallyDisabled", "0", "0")]
    plan, md, html = P.compile_and_render(P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []},
                                          [A, B], patches=[PATCH], count_only={(RECORDS, "baseline"): 1,
                                                                                (RECORDS, "with-patch"): 2},
                                          enrichment={(RECORDS, "baseline"): p_rows, (RECORDS, "with-patch"): w_rows})
    lines = [ln for ln in md.splitlines() if ln.startswith("- Undone by ")]
    ok = len(lines) == 1 and lines[0].startswith(f"- Undone by {PATCH} (earlier value put back): 1 record field")
    return ok, f"undone lines {lines}"


# --------------------------------------------------------------------- the reel's own terms (synthetic plan)

def reel_pres(subjects=1, extra_rows=(), summary_reason=None):
    rows = [
        {"admission": "admitted", "checkId": "records.contested-fields", "formKey": f"000901:{BASE}", "A": OTHER, "B": A,
         "field": "AIData", "summary": f"Npc 000901: AIData - {OTHER}'s value lost to {A}'s", "values": {}},
        *[{"admission": "admitted", "checkId": "geometry.navmesh-ownership", "formKey": f"000A01:{BASE}", "A": OTHER,
           "B": A, "kind": "EDGE-IDX", "other": [f"000A02:{BASE}"], "summary": f"NavigationMesh 000A01: link {i}"}
          for i in range(3)],
        {"admission": "admitted", "checkId": "geometry.placement-contact", "formKey": f"000C01:{A}", "A": OTHER, "B": A,
         "kind": "CONTACT", "other": [f"000C02:{OTHER}"], "summary": "contact"},
        {"admission": "admitted", "checkId": "records.contested-fields", "formKey": f"000B01:{BASE}", "A": OTHER, "B": A,
         "field": "Placement", "summary": f"PlacedObject 000B01: Placement - {OTHER}'s value lost to {A}'s",
         "values": {"lost": "Position=1,2,3; Rotation=0,0,0", "winner": "Position=1,2,-30000; Rotation=0,0,0"}},
        {"admission": "admitted", "checkId": "records.contested-fields", "formKey": f"000B02:{OTHER}", "A": OTHER, "B": A,
         "field": "Placement", "summary": f"PlacedObject 000B02: Placement - {OTHER}'s value lost to {A}'s",
         "values": {"lost": "Position=1,2,3; Rotation=0,0,0", "winner": "Position=1,2,-30000; Rotation=0,0,0"}},
        *extra_rows]
    reason = summary_reason or ("was measured, but 3 rows could not be measured in both setups, so it is not counted as "
                                "an accepted result (its counts are given with it)")
    return {"reader": {"prediction": {"subject": A, "predictionArm": "baseline", "withoutArm": "no-a", "record": rows,
                                      "items": [{"title": "Objects occupying the same space", "count": 5, "shownAs": "pair"}],
                                      "whatToDo": ["Look at the 5 overlap candidates in game."],
                                      "whatToDoHeading": "What to do"},
                       "missingSummary": [{"reason": reason,
                                           "subjects": [f"Objects occupying the same space — setup {i}" for i in range(subjects)]},
                                          {"reason": "was stopped when the evaluation's time budget ran out",
                                           "subjects": [f"Check number {i} with a long title for the closing page"
                                                        for i in range(subjects)]}],
                       "sectionHeadings": {"prediction": f"What adding {A} will do"}, "suppliedPatches": []},
            "scope": {"arms": [{"armId": "baseline", "plugins": [BASE, OTHER, A], "locationPluginOrder": [BASE, A],
                                "displayLabel": "Measured load order"}]},
            "media": []}


MODS = [BASE, OTHER, A]


def case_reel_marks_every_clash_plugin():
    reel = reel_of(reel_pres(), MODS)
    lo = next(p for p in chapter(reel, "opening")["still"]["pages"] if p["layout"] == "load-order")
    ok = OTHER in lo.get("affected") and A in lo.get("affected") and BASE not in lo.get("affected")
    return ok, f"affected {lo.get('affected')} of {lo.get('plugins')}"


def case_reel_names_an_object_by_the_plugin_that_made_it():
    reel = reel_of(reel_pres(), MODS)
    said = str((chapter(reel, "unpictured") or {}).get("narration"))
    other = re.sub(r"\.esp$", "", OTHER)
    ok = (f"that {other} changes" in said and f"that {other} places" in said)
    return ok, f"unpictured {said!r}"


def case_reel_counts_in_the_reports_units():
    reel = reel_of(reel_pres(), MODS)
    types = next(p for p in chapter(reel, "opening")["still"]["pages"] if p["layout"] == "types")
    rows = {r["label"]: (r.get("count"), r.get("unit")) for r in types["rows"]}
    acc = reel.get("accounting") or {}
    flat = [x for t in acc.values() for k in ("pictured", "unpictured", "represented") for x in t.get(k) or []]
    un = str((chapter(reel, "unpictured") or {}).get("narration"))
    ok = (rows.get(WORDS["typeLabels"]["navmesh"]) == (3, "broken navmesh links")
          and WORDS["typeLabels"]["contact"] in rows and len(flat) == len(set(flat))
          and "not pictured" in un and "can't be visualized" not in un
          and 'under "What adding ModA will do"' in un and ".esp" not in un)
    return ok, f"card {rows}; unpictured {un[:220]!r}"


def case_reel_closing_reconciled_and_readable():
    reel = reel_of(reel_pres(subjects=40), MODS)
    closing = chapter(reel, "closing") or {}
    page = closing["still"]["pages"][0]
    rows = page.get("rows") or []
    pages = (reel_stills.paginate(page, FONT, BOLD) if hasattr(reel_stills, "paginate") else [page])
    rate = float(POLICY.get("readingWordsPerSecond") or 3.0)
    words_ = [reel_plan.page_words(p) if hasattr(reel_plan, "page_words") else
              sum(len(str(x).split()) for r in p.get("rows") or [] for x in [r.get("reason"), *(r.get("subjects") or [])])
              for p in pages]
    ok = (any("are from the measured load order" in str(r.get("reason")) for r in rows)
          and len(pages) > 1 and max(words_) <= 160
          and float(page.get("seconds") or 0) >= (reel_plan.page_words(page) if hasattr(reel_plan, "page_words") else 1e9) / rate - 0.01
          and "could not be measured in both setups, so it is not counted" not in json.dumps(rows))
    return ok, f"pages {len(pages)} words {words_}; seconds {page.get('seconds')}; first rows {rows[:1]}"


def case_reel_closing_without_a_headline_check_has_no_reconciling_line():
    pres = reel_pres()
    pres["reader"]["prediction"]["items"] = [{"title": "Something else", "count": 2, "shownAs": "pair"}]
    rows = (chapter(reel_of(pres, MODS), "closing") or {})["still"]["pages"][0].get("rows") or []
    ok = not any("are from the measured load order" in str(r.get("reason")) for r in rows)
    return ok, f"rows {rows[:2]}"


def case_reel_load_order_page_is_narrated_and_timed():
    reel = reel_of(reel_pres(), MODS)
    op = chapter(reel, "opening")
    ok = ("load order of the measured setup" in op["narration"]
          and all(p.get("seconds") for p in op["still"]["pages"]))
    return ok, f"narration {op['narration']!r}; seconds {[p.get('seconds') for p in op['still']['pages']]}"


def case_reel_verdict_page_says_the_reports_sentences():
    reel = reel_of(reel_pres(), MODS)
    v = chapter(reel, "verdict") or {}
    kinds = [c["kind"] for c in reel.get("chapters") or []]
    ok = (v.get("narration") == "Look at the 5 overlap candidates in game." and kinds[-1] == "closing"
          and kinds[-2] == "verdict")
    return ok, f"kinds {kinds}; verdict {v.get('narration')!r}"


def case_vignette_frame_label_strip():
    """The reel's label strip under a vignette frame: drawn below the evidence frame, which stays text-free."""
    if not hasattr(reel_stills, "label_frame"):
        return False, "no label strip for vignette frames"
    from PIL import Image
    with tempfile.TemporaryDirectory() as td:
        src = Path(td) / "f.png"
        Image.new("RGB", (1280, 720), (200, 30, 30)).save(src)
        img = reel_stills.label_frame(src, "Without Mod A", FONT, BOLD)
        strip = img.crop((0, 690, 1280, 720)).getcolors(1 << 16) or []
        top = img.getpixel((640, 300))
    ok = img.size == (1280, 720) and top[0] > 150 and len(strip) > 1 and all(c[1] != (200, 30, 30) for c in strip[:1])
    return ok, f"size {img.size}; centre {top}; strip colours {len(strip)}"


# -------------------------------------------------------------------- pictures produced are pictures shown

def figure_plan(vignettes=3, finding_media=True):
    """A finding with `vignettes` captured vignettes, each its without, with and marked frames: shaped as a
    presentation's media rows and finding."""
    med = [{"id": f"M-{r}{st}", "assessed": True, "path": f"media/{r}{st}.png",
            "provenance": {"captureRequest": f"r{r}", "vignetteState": st}}
           for r in range(1, vignettes + 1) for st in ("without", "with", "highlight")]
    return {"settings": {"mode": "concise"}, "media": med, "reader": {}, "scope": {"arms": []}, "groups": [],
            "unassessed": [], "omissions": [], "chapters": [],
            "findings": [{"id": "F-1", "kind": "finding", "rowsTotal": 0,
                          "mediaIds": [m["id"] for m in med] if finding_media else []}]}


def case_every_produced_picture_is_shown_or_named():
    """Nine produced pictures of three vignettes, at the page-fit ladder's last step: each one shown or named as left out
    (this test counts it from the view's figures and omission lines)."""
    plan = figure_plan()
    step = PR["abridgementLadder"][-1]
    view = R.effective_view(plan, step["rowsPerGroup"], step["figuresPerFinding"], PR, step.get("layout", "normal"))
    shown = {m["id"] for f in view["findings"] for m in f.get("figures") or []}
    said = " ".join(str(o.get("what")) for o in view.get("omissions") or [])
    named = {m["id"] for m in plan["media"] if m["id"] not in shown and m["id"] in said}
    ok = len(shown) + len(named) == 9 and len(shown) == 9
    return ok, f"shown {len(shown)}, named as left out {len(named)} of 9"


def case_the_reels_vignettes_are_in_the_report():
    """Four vignettes and room for three: the one the reel shows is among those the report shows."""
    plan = figure_plan(vignettes=4)
    plan["reel"] = {"chapters": [{"kind": "vignette", "frames": [{"mediaId": f"M-4{st}"} for st in ("without", "with", "highlight")]}]}
    view = R.effective_view(plan, 3, 3, PR, "normal")
    shown = {m["id"] for f in view["findings"] for m in f.get("figures") or []}
    ok = {"M-4without", "M-4with", "M-4highlight"} <= shown and len(shown) == 9
    return ok, f"shown {sorted(shown)}"


def case_a_vignette_is_shown_whole():
    m = lambda i, st: {"id": i, "provenance": {"captureRequest": "r1", "vignetteState": st}}  # noqa: E731
    units = R.figure_units([m("w", "with"), m("h", "highlight"), m("o", "without")])
    ok = [[x["id"] for x in u] for u in units] == [["o", "w", "h"]]
    return ok, f"units {units}"


def case_no_pictures_is_said():
    plan, md, html = P.none_found_bundle()
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    plan2, md2, _ = P.compile_and_render(arms, {(CONTACT, "baseline"): [P.row("CONTACT", B, A, f"000801:{A}")]}, [A])
    line = pred_of(plan2).get("noPicturesLine") or ""
    ok = bool(line) and line in md2 and not pred_of(plan).get("noPicturesLine")
    return ok, f"line {line!r}"


# ------------------------------------------------------------------------------------------- the patch's own edits

def precedent_dir(td, patch=PATCH):
    d = Path(td) / "cards"
    d.mkdir(parents=True, exist_ok=True)
    (d / "summary.json").write_text(json.dumps({"sources": [{
        "patch": patch, "census": {"patchRecords": 12, "additions": 4, "edits": 8,
                                   "editKinds": {"DISABLE": 3, "FIELD:Lighting": 4, "MOVE": 1}},
        "cardPass": {"decidingConstraint": {"lighting-intent": 5, "not-established": 3}}}]}), encoding="utf-8")
    return d


def case_patch_census_replaces_the_stock_lines():
    with tempfile.TemporaryDirectory() as td:
        plan, md, html = P.compile_and_render(
            P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A, B], patches=[PATCH],
            count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1},
            enrichment={(RECORDS, "baseline"): [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")],
                        (RECORDS, "with-patch"): [P.lost_row(A, PATCH, f"000702:{BASE}", "SkyrimMajorRecordFlags",
                                                             "InitiallyDisabled", "0", "0")]},
            settings_extra={"precedentDir": str(precedent_dir(td))})
    ok = (f"{PATCH} holds 12 records: 4 new and 8 overrides of other plugins' records, which make 8 changes" in md
          and "3 switched off" in md
          and "model-written reason for 5" in md
          and "does not list the patch's own edits" not in md and "Whether that is intended is not recorded" not in md)
    return ok, f"validation {P.section_text(md, P.headings(plan).get('validation') or chr(0))[:600]!r}"


def case_without_census_the_stock_line_stays():
    plan, md, html = P.compile_and_render(
        P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A, B], patches=[PATCH])
    stock = PR["reader"]["prediction"]["validationMissingFacts"][0]
    ok = stock in md and "holds" not in P.section_text(md, P.headings(plan).get("validation") or "\0")
    return ok, f"stock line present {stock in md}"


# ---------------------------------------------------------------- a claim is checked against its comparison

def pair_setups_bundle(with_q=True):
    arms = [P.arm("baseline", [BASE, A, B, OTHER, "Fourth.esp"], kind="baseline"), P.arm("a-b", [BASE, A, B])]
    if with_q:
        arms.append(P.arm("a-other", [BASE, A, OTHER]))
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
    results = {(CONTACT, "baseline"): rows, (CONTACT, "a-b"): rows}
    if with_q:
        results[(CONTACT, "a-other")] = []
    return P.compile_and_render(arms, results, [A])


def case_pair_setups_answer_whether_a_clash_needs_the_other_mod():
    plan, md, html = pair_setups_bundle()
    item = next(iter(pred_of(plan).get("items") or []), {})
    ok = item.get("attribution") == "needs-both" and "was not measured" not in md
    return ok, f"attribution {item.get('attribution')}; text {item.get('plain', '')[:200]!r}"


def case_without_a_setup_lacking_the_other_mod_it_stays_unmeasured():
    plan, md, html = pair_setups_bundle(with_q=False)
    item = next(iter(pred_of(plan).get("items") or []), {})
    ok = item.get("attribution") == "with-target"
    return ok, f"attribution {item.get('attribution')}"


def removal_comparison_bundle():
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("no-a", [BASE, OTHER])]
    rows = [P.row("CONTACT", OTHER, A, f"00081{i}:{A}") for i in (1, 2)]
    counts = dict(verdict="DIFFERENTIAL-HARMFUL", repaired=0, reclassified=0, created=0, newDefect=0, attributed=0,
                  unmeasured=2)
    return P.compile_and_render(arms, {(CONTACT, "baseline"): rows, (CONTACT, "no-a"): []}, [A],
                                missing=[(CONTACT, "no-a", "completed", "diff")],
                                order={"no-a": {"contactprobe": counts}},
                                diff_logs={(CONTACT, "no-a"): "   unmeasured   : 2 record(s)\n"})


def case_unmeasured_rows_in_a_setup_without_the_mod_are_explained():
    plan, md, html = removal_comparison_bundle()
    ok = f"that setup does not load {A}, so {A}'s own objects cannot be measured in it" in md
    return ok, f"md mentions {md.count('does not load')}"


def case_unmeasured_rows_in_a_patch_comparison_keep_the_plain_reason():
    plan, md, html = P.comparison_bundle("unaccepted")
    ok = "could not be measured in both setups" in md and "own objects cannot be measured" not in md
    return ok, "plain reason kept"


# ------------------------------------------------------------------------------------------ the rest, by part

def missing_assets_bundle(same_without=True):
    MA = "loadability.missing-assets"

    def call(i, name, args):
        return {"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": name, "arguments": args}}

    def answer(i, doc):
        return {"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(doc)}]}}

    def transcript(missing):
        req = [{"batchMarker": "b6-models"}, call(1, "housecarl_records", {"fields": ["Model.File"]}),
               {"batchMarker": "b7-assets"}, call(1, "housecarl_asset_status", {"asset_paths": ["x"]})]
        resp = [{"batchMarker": "b6-models"},
                answer(1, {"matches": [{"source": A, "fields": [{"path": "Model.File", "value": "Arch/House01.nif"},
                                                                {"path": "Model.File", "value": "Sky/Cloud01.dds"},
                                                                {"path": "Model.File", "value": "Sky/Cloud02.dds"}]}]}),
                {"batchMarker": "b7-assets"},
                answer(1, {"results": [{"path": p_, "exists": False} for p_ in missing]})]
        return req, resp
    every = ["meshes/Arch/House01.nif", "meshes/Sky/Cloud01.dds", "meshes/Sky/Cloud02.dds"]
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("no-a", [BASE, OTHER])]
    return P.compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "no-a"): []}, [A],
                                count_only={(MA, "baseline"): 3, (MA, "no-a"): 3 if same_without else 0},
                                transcripts={(MA, "baseline"): transcript(every),
                                             (MA, "no-a"): transcript(every if same_without else [])})


def case_missing_files_named_by_kind_and_owner():
    plan, md, html = missing_assets_bundle()
    line = next((ln for ln in md.splitlines() if "House01.nif" in ln), "")
    ok = ("1 mesh and 2 textures" in line or "2 textures and 1 mesh" in line) and "mesh files" not in line         and f"missing in the setup without {A}, so they do not come from it" in line
    return ok, f"line {line[:400]!r}"


def case_missing_files_of_the_mod_keep_their_attribution():
    plan, md, html = missing_assets_bundle(same_without=False)
    line = next((ln for ln in md.splitlines() if "House01.nif" in ln), "")
    ok = bool(line) and "do not come from it" not in line
    return ok, f"line {line[:300]!r}"


def case_order_only_setup_named_by_its_order():
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline"), P.arm("swapped", [BASE, B, A])]
    rows = [P.row("CONTACT", B, A, f"000801:{A}")]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows, (CONTACT, "swapped"): rows}, [A, B])
    arm_ = next(a for a in plan["scope"]["arms"] if a["armId"] == "swapped")
    ok = arm_.get("orderChange") == f"loads {B} before {A}" and f"loads {B} before {A}" in md
    return ok, f"swapped arm {arm_.get('orderChange')!r}; label {arm_.get('readerLabel')!r}"


def case_add_remove_setups_keep_their_labels():
    plan, md, html = P.none_found_bundle()
    ok = not any(a.get("orderChange") for a in plan["scope"]["arms"])
    return ok, "no order change on add/remove setups"


def case_state_finding_has_an_in_game_meaning():
    ig = PR["reader"]["inGame"].get("state.conditional-geometry|STATE-EMPTY") or ""
    return "hang in empty space" in ig, ig


def case_lost_record_fields_have_an_in_game_meaning():
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    rows = [P.lost_row(B, A, f"000701:{BASE}", "TeleportDestination", "x", "y", "v")]
    plan, md, html = P.compile_and_render(arms, {}, [A], count_only={(RECORDS, "baseline"): 1},
                                          enrichment={(RECORDS, "baseline"): rows})
    item = next(iter(pred_of(plan).get("items") or []), {})
    ok = "where a door leads" in str(item.get("inGameText")) and "where a door leads" in md
    return ok, f"in game {item.get('inGameText')!r}"


def case_unmapped_field_still_not_described():
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    rows = [P.lost_row(B, A, f"000701:{BASE}", "SomeUnmappedField", "x", "y", "v")]
    plan, md, html = P.compile_and_render(arms, {}, [A], count_only={(RECORDS, "baseline"): 1},
                                          enrichment={(RECORDS, "baseline"): rows})
    item = next(iter(pred_of(plan).get("items") or []), {})
    ok = str(item.get("inGameText")) == PR["reader"]["prediction"]["inGameMissing"]
    return ok, f"in game {item.get('inGameText')!r}"


def case_zero_count_never_printed_as_a_result():
    P.PROBE.setdefault("loadability.dangling-references", None)
    arms = [P.arm("baseline", [BASE, A], kind="baseline")]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): []}, [A],
                                          count_only={("loadability.dangling-references", "baseline"): 0})
    hit = re.search(r"count without the records:\W*0\b", md)
    return hit is None, f"zero count line {hit.group(0) if hit else 'absent'}"


def case_markdown_and_html_say_the_same_regression_line():
    plan, md, html = P.none_found_bundle()
    line = ((plan.get("reader") or {}).get("validation") or {}).get("noRegressionLine")
    ok = bool(line) and line in md and line in unesc(html)
    return ok, f"line {line!r}"


def case_patch_search_said_in_report_with_its_cautions():
    hit = {"file": "Old patch collection", "status": "retired", "category": "OLD_VERSION", "otherMentions": ["Third.esp"]}
    if not hasattr(reel_plan, "lead_qualifiers"):
        return False, "no shared patch-lead cautions"
    q = reel_plan.lead_qualifiers(hit, WORDS, {BASE.lower(), A.lower()}, True)
    ok = ("retired" in q and "old version" in q and "Third.esp" in q and "may be the patch evaluated here" in q
          and reel_plan.lead_qualifiers({"file": "x", "status": "live", "category": "MAIN"}, WORDS, set(), False) == "")
    return ok, f"cautions {q!r}"


# ------------------------------------------------------------------- record rows judged against each plugin's masters

def tes4(path: Path, masters) -> Path:
    """A plugin file holding only its TES4 header with these masters."""
    import struct
    body = b"".join(b"MAST" + struct.pack("<H", len(m) + 1) + m.encode("cp1252") + b"\0"
                    + b"DATA" + struct.pack("<H", 8) + b"\0" * 8 for m in masters)
    path.write_bytes(b"TES4" + struct.pack("<IIIII", len(body), 0, 0, 0, 0) + body)
    return path


def rows_bundle(td: Path, p_masters, extra=(), p_file=True, p_changed=False, recorded=None, flags=None,
                override=None):
    """One record import without a rows file, its configuration on real plugin files: the record's chain is the base
    game, M, Q, P and the winner W; P holds M's value, which differs from Q's. P's own masters decide whether P made a
    change (with M among them, it made none)."""
    import hashlib
    import bundleenrich
    root = td / "bundle"
    (root / "registry").mkdir(parents=True)
    (root / "receipts").mkdir()
    imp = root / "evidence" / "imports" / "rc"
    imp.mkdir(parents=True)
    files = td / "plugins"
    files.mkdir()
    fk = f"000901:{BASE}"
    heads = {BASE: [], "M.esp": [BASE], "Q.esp": [BASE], "P.esp": list(p_masters), "W.esp": [BASE], **dict(extra), **dict(override or {})}
    order = [BASE, "M.esp", *[k for k in dict(extra)], "Q.esp", "P.esp", "W.esp"]
    plugins = []
    for n in order:
        f = tes4(files / n, heads[n])
        plugins.append({"name": n, "path": str(f) if (n != "P.esp" or p_file) else str(files / "gone.esp"),
                        "sha256": ("0" * 64 if (n == "P.esp" and p_changed) else hashlib.sha256(f.read_bytes()).hexdigest())})
    loc = {BASE: "02008A:Skyrim.esm", "M.esp": "0A0001:M.esp", "Q.esp": "0A0002:Q.esp", "P.esp": "0A0001:M.esp",
           "W.esp": "0A0003:W.esp"}
    path_of = {x["name"]: x["path"] for x in plugins}
    req = [{"batchMarker": "b1"}]
    resp = [{"batchMarker": "b1"}]
    for i, n in enumerate([BASE, "M.esp", "Q.esp", "P.esp", "W.esp"], start=1):
        req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {
            "name": "housecarl_read_plugin_file",
            "arguments": {"plugin": path_of[n], "formid": fk, "format": "text", "depth": 3}}})
        text = NL.join([f"file: {path_of[n]}  [direct path; NOT active]", "type=PlacedObject  formid=" + fk
                          + "  editorid=<none>", f"fields (raw, from {n}):", "  FormKey = " + fk,
                          "  Base = 0136D6:Skyrim.esm", "  IsDeleted = False", f"  LocationReference = {loc[n]}",
                          "  Placement = [Placement]", "  Placement.Position = 10,20,30"]) + NL
        resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": text}]}})
    (imp / "01-request.jsonl").write_text("".join(json.dumps(x) + NL for x in req), encoding="utf-8")
    (imp / "02-response.jsonl").write_text("".join(json.dumps(x) + NL for x in resp), encoding="utf-8")
    (imp / "result.json").write_text(json.dumps({"outputs": [{"role": "request"}, {"role": "response"}]}), encoding="utf-8")
    (root / "registry" / "check-registry.json").write_text(json.dumps(
        {"checks": [{"id": RECORDS, "family": "records"}]}), encoding="utf-8")
    (root / "receipts" / "rc.json").write_text(json.dumps({
        "adapter": {"kind": "mcp-import"}, "validation": {"accepted": True}, "checkId": RECORDS, "armId": "baseline",
        "outputs": [{"path": "imports/rc/result.json"}, {"path": "imports/rc/01-request.jsonl"},
                    {"path": "imports/rc/02-response.jsonl"}],
        "consumed": {"configuration": {"plugins": plugins}}}), encoding="utf-8")
    (root / "disposition.json").write_text("{}", encoding="utf-8")
    evidence = []
    if recorded is not None:
        # a probe emit of the same setup recording each plugin's header as measured ({name: masters}), for the
        # bytes the configuration recorded
        sw = root / "evidence" / "sweep" / "probe"
        sw.mkdir(parents=True)
        (sw / "baseline.json").write_text(json.dumps({"Configuration": {"Plugins": [
            {"Name": x["name"], "Sha256": x["sha256"], "Masters": list(recorded.get(x["name"], heads[x["name"]])),
             "MasterFlag": False, "LightFlag": False, **((flags or {}).get(x["name"]) or {})}
            for x in plugins]}}), encoding="utf-8")
        evidence.append({"kind": "emit", "path": "evidence/sweep/probe/baseline.json", "armId": "baseline"})
    (root / "manifest.json").write_text(json.dumps({"evidence": evidence, "registry": "registry/check-registry.json"}),
                                        encoding="utf-8")
    bundleenrich.enrich_record_rows(root)
    return json.loads((root / "enrichment" / "record-rows" / f"{RECORDS}__baseline.json").read_text(encoding="utf-8"))


def losers(emit):
    return sorted({c.get("A") for c in emit.get("Conflicts") or []})


def case_record_rows_judge_a_change_against_the_plugins_masters():
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE, "M.esp"])
    ok = losers(emit) == ["M.esp", "Q.esp"]
    return ok, f"losing plugins {losers(emit)} (P holds its master's value, so it lost no change of its own)"


def case_record_rows_close_the_masters_transitively():
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), ["M2.esp"], extra=[("M2.esp", [BASE, "M.esp"])])
    ok = losers(emit) == ["M.esp", "Q.esp"]
    return ok, f"losing plugins {losers(emit)} (P is built on M through M2)"


def case_record_rows_without_a_masters_relation_keep_the_change():
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE])
    ok = losers(emit) == ["M.esp", "P.esp", "Q.esp"]
    return ok, f"losing plugins {losers(emit)} (P is not built on M: its value is a change over Q's)"


def case_record_rows_count_an_unread_header_unjudged():
    import recordrows
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE, "M.esp"], p_file=False)
    reasons = {k: v for c in emit.get("Coverage") or [] for k, v in (c.get("Unjudgeable") or {}).items()}
    want = getattr(recordrows, "MASTERS_UNREAD", "a removal by a plugin whose masters' headers could not all be read")
    ok = "P.esp" not in losers(emit) and reasons.get(want) == 1 and emit.get("mastersUnread") == ["p.esp"]
    return ok, f"losing plugins {losers(emit)}; unjudged {reasons}; masters unread {emit.get('mastersUnread')}"


def case_record_rows_treat_a_changed_file_as_unread():
    """A plugin file whose bytes are not the ones the configuration recorded is not read for its masters."""
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE, "M.esp"], p_changed=True)
    ok = emit.get("mastersUnread") == ["p.esp"] and "P.esp" not in losers(emit)
    return ok, f"masters unread {emit.get('mastersUnread')}; losing plugins {losers(emit)}"


def case_item18_record_rows_take_masters_the_bundle_recorded():
    """Item 18 A1: P's file changed after the measurement (its live bytes are not the recorded ones), but the bundle's
    probe emit recorded P's masters for the recorded bytes: P is judged against those, not blinded. P is not built on
    M, so its value is its own change, lost to the winner."""
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE], p_changed=True, recorded={})
    ok = emit.get("mastersUnread") == [] and losers(emit) == ["M.esp", "P.esp", "Q.esp"] \
        and "p.esp" in (emit.get("mastersRecorded") or [])
    return ok, (f"masters unread {emit.get('mastersUnread')}; recorded {emit.get('mastersRecorded')}; "
                f"losing plugins {losers(emit)}")


def case_item18_record_rows_recorded_masters_decide_over_the_header():
    """Item 18 A1: the recorded list is the measurement's fact; here it says P is built on M, while P's file header (at
    the recorded bytes) says it is not. The recorded list decides: P holds its master's value, so it lost no change."""
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE], recorded={"P.esp": [BASE, "M.esp"]})
    ok = emit.get("mastersUnread") == [] and losers(emit) == ["M.esp", "Q.esp"]
    return ok, f"masters unread {emit.get('mastersUnread')}; losing plugins {losers(emit)}"


def item18_coverage_bundle(listed=True, extra=None):
    """The prediction suite's unjudged-fields setups; the baseline's rows file lists 5 fields (or none) and its coverage
    line counts, besides, the reasons in `extra` without listing their fields."""
    nb = [P.lost_row(A, B, f"00070{i}:{BASE}", "LocationReference", f"a{i}", f"b{i}", "v") for i in (1, 2)]
    enr = {(RECORDS, "baseline"): nb, (RECORDS, "dependency-only"): [], (RECORDS, "plus-a"): [], (RECORDS, "plus-b"): []}
    both = [{"FormKey": f"018A5A:{BASE}", "Type": "Location", "Field": f, "Reason": P.TRUNC, "Plugins": [BASE, A, B]}
            for f in ("Music", "Name", "ParentLocation")]
    one = [{"FormKey": f"000711:{BASE}", "Type": "Cell", "Field": f, "Reason": P.NOT_READ, "Plugins": [BASE, OTHER, A]}
           for f in ("Regions", "Water")]
    results = {(CONTACT, a): [] for a in ("baseline", "dependency-only", "plus-a", "plus-b")}
    return P.compile_and_render(P.four_arms(), results, [A, B], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                enrichment=enr, enrichment_unjudged={(RECORDS, "baseline"): (both + one) if listed else []},
                                enrichment_coverage={(RECORDS, "baseline"): dict(extra or {})})


def item18_fields_line(plan):
    reason = ((PR["reader"].get("prediction") or {}).get("coverageReasons") or {}).get("fieldsUnjudged")
    return [ln for ln in (plan.get("reader") or {}).get("coverage", {}).get("lines") or [] if ln.get("reason") == reason]


def case_item18_every_coverage_reason_reaches_the_report():
    """Item 18 A2: a reason the rows file counts on its coverage line without listing the fields (here one no wording
    names, and the masters one) is counted on the report's unjudged-fields line with its count, beside the listed ones."""
    import recordrows
    mu = recordrows.MASTERS_UNREAD
    plan, md, html = item18_coverage_bundle(extra={"a reason no listing names": 4, mu: 7})
    lines = item18_fields_line(plan)
    subj = str(lines[0].get("subject") or "") if len(lines) == 1 else ""
    fw = ((PR["reader"].get("prediction") or {}).get("fieldUnjudgedWords") or {}).get(mu)
    ok = (len(lines) == 1 and lines[0].get("count") == 5 + 4 + 7 and "a reason no listing names 4" in subj
          and bool(fw) and f"{fw} 7" in subj and subj in md)
    return ok, f"lines {lines}; field words {fw!r}; on report.md {bool(subj) and subj in md}"


def case_item18_coverage_reason_reaches_the_report_with_nothing_listed():
    """Item 18 A2: a rows file that lists no unjudged field but counts some on its coverage line still gets the line."""
    plan, md, html = item18_coverage_bundle(listed=False, extra={"a reason no listing names": 3})
    lines = item18_fields_line(plan)
    ok = len(lines) == 1 and lines[0].get("count") == 3 and "a reason no listing names 3" in str(lines[0].get("subject"))
    return ok, f"lines {lines}"


def case_item18_listed_and_counted_reasons_are_not_counted_twice():
    """Item 18 A2, the must-not-fire case: coverage counts equal to the listed fields add nothing (5 listed, 5 counted)."""
    plan, md, html = item18_coverage_bundle()
    lines = item18_fields_line(plan)
    ok = len(lines) == 1 and lines[0].get("count") == 5
    return ok, f"lines {lines}"


def case_item18_masters_unread_fields_are_listed():
    """Item 18 A2: the rows comparator lists every field it counts unjudged for an unreadable master list, with the
    plugins compared, so its coverage count and its list agree by construction."""
    import recordrows
    with tempfile.TemporaryDirectory() as td:
        emit = rows_bundle(Path(td), [BASE, "M.esp"], p_file=False)
    counted = sum(n for c in emit.get("Coverage") or [] for r, n in (c.get("Unjudgeable") or {}).items()
                  if r == recordrows.MASTERS_UNREAD)
    listed = [u for u in emit.get("Unjudged") or [] if u.get("Reason") == recordrows.MASTERS_UNREAD]
    ok = counted == 1 and len(listed) == 1 and listed[0].get("Field") == "LocationReference" and listed[0].get("Plugins")
    return ok, f"counted {counted}; listed {listed}"


def item18_derive(transcript, order):
    """recordrows.derive, the function the record-rows and order-variant enrichments call, on a 2.0 capture's
    transcripts with the configuration in `order`."""
    import recordrows
    req, resp = transcript
    with tempfile.TemporaryDirectory() as td:
        rq, rs = Path(td) / "request.jsonl", Path(td) / "response.jsonl"
        rq.write_text("".join(json.dumps(x) + NL for x in req), encoding="utf-8")
        rs.write_text("".join(json.dumps(x) + NL for x in resp), encoding="utf-8")
        return recordrows.derive(rq, rs, {"plugins": [{"name": n} for n in order]}, recordrows.load_scope())


def item18_chain():
    fk = f"000801:{BASE}"
    return [(fk, "PlacedObject", [(BASE, {"Base": "000101:Skyrim.esm", "Scale": "1"}),
                                  (A, {"Base": "000102:ModA.esp", "Scale": "1"}),
                                  (B, {"Base": "000103:ModB.esp", "Scale": "2"})])]


def case_item18_b1_derive_reads_a_2_0_tree_capture():
    """Item 18 B1: a 2.0 capture holds tree answers, not pre-2.0 text reads. derive must read them: in the measured order
    A's Base is lost to B; with the pair reversed, B's Base and Scale are lost to A (A's Scale is its master's)."""
    emit = item18_derive(P.tree_transcript(item18_chain()), [BASE, A, B])
    rev = item18_derive(P.tree_transcript(item18_chain()), [BASE, B, A])
    got = sorted((c["A"], c["B"], c["Field"]) for c in emit.get("Conflicts") or [])
    got_r = sorted((c["A"], c["B"], c["Field"]) for c in rev.get("Conflicts") or [])
    # the row says the lost value as the tree gives it (the provider's difference from the winner), whole
    lost = [n["Values"]["lost"] for c in emit.get("Conflicts") or [] for n in c.get("Necessary") or []]
    ok = (got == [(A, B, "Base")] and got_r == [(B, A, "Base"), (B, A, "Scale")]
          and len(lost) == 1 and "000102:ModA.esp" in lost[0])
    return ok, f"measured {got}; reversed {got_r}; lost value {lost}"


def case_item18_b1_derive_reads_2_0_by_name_reads():
    """Item 18 B1: the same through the 2.0 by-name reads (housecarl_records source= form fields)."""
    emit = item18_derive(P.records_transcript(item18_chain()), [BASE, A, B])
    got = sorted((c["A"], c["B"], c["Field"]) for c in emit.get("Conflicts") or [])
    ok = got == [(A, B, "Base")]
    return ok, f"measured {got}"


def json19_transcript(chain, fk=f"000801:{BASE}", rtype="PlacedObject"):
    """houseCARL 1.9.0's record reads: `housecarl_read_plugin_file` answered in JSON (`record.fields[] {path, value |
    note}`), as Groups 2 and 3's retained bundles hold them."""
    req, resp = [{"batchMarker": "b1"}], [{"batchMarker": "b1"}]
    for n, (plugin, fields) in enumerate(chain, start=1):
        req.append({"jsonrpc": "2.0", "id": n, "method": "tools/call", "params": {
            "name": "housecarl_read_plugin_file", "arguments": {"plugin": f"X:/m/{plugin}", "formid": fk, "depth": 3}}})
        body = {"out_of_load_order": True, "file": f"X:/m/{plugin}", "where": "direct path", "masters": [], "mode": "read",
                "record": {"formid": fk, "type": rtype, "editorid": None,
                           "fields": [{"path": k, "value": v} if v is not None else {"path": k, "note": "(absent)"}
                                      for k, v in fields.items()]}}
        resp.append({"jsonrpc": "2.0", "id": n, "result": {"content": [{"type": "text", "text": json.dumps(body, indent=2)}]}})
    return req, resp


def case_item18_g2_record_rows_read_the_1_9_json_form():
    """Item 18 ruling 2: a 1.9.0 JSON record read is read like a text read: A's Base is lost to B; a field absent in
    every version is no change."""
    import recordrows
    chain = [(BASE, {"Base": "000101:Skyrim.esm", "Scale": None}), (A, {"Base": "000102:ModA.esp", "Scale": None}),
             (B, {"Base": "000103:ModB.esp", "Scale": None})]
    req, resp = json19_transcript(chain)
    with tempfile.TemporaryDirectory() as td:
        rq, rs = Path(td) / "request.jsonl", Path(td) / "response.jsonl"
        rq.write_text("".join(json.dumps(x) + NL for x in req), encoding="utf-8")
        rs.write_text("".join(json.dumps(x) + NL for x in resp), encoding="utf-8")
        emit = recordrows.derive(rq, rs, {"plugins": [{"name": n, "path": f"X:/m/{n}"} for n in (BASE, A, B)]},
                                 recordrows.load_scope())
    got = sorted((c["A"], c["B"], c["Field"]) for c in emit.get("Conflicts") or [])
    failed = sum(n for c in emit.get("Coverage") or [] for r, n in (c.get("Unjudgeable") or {}).items())
    ok = got == [(A, B, "Base")] and failed == 0
    return ok, f"rows {got}; unjudgeable {failed}"


def case_record_rows_unread_reason_is_the_captures():
    """The enrichment and the record capture count an unreadable master header under the same words."""
    import recordrows
    src = (HERE.parent / "evalrecords.py").read_text(encoding="utf-8")
    want = getattr(recordrows, "MASTERS_UNREAD", None)
    ok = bool(want) and f'"{want}"' in src
    return ok, f"reason {want!r} in the capture: {bool(want) and want in src}"


# ------------------------------------------------------------------------ the parts finished before the re-render

def case_every_place_is_named():
    """Clashes in three places: the two with most first, then every other place by name, never "N more places"."""
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}", label=cell) for i, cell in
            enumerate(["CellA (ext)"] * 3 + ["CellB (ext)"] * 2 + ["CellC (int)"])]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows}, [A])
    ok = "1 more place: CellC (int) (1)" in md and "more places." not in md
    return ok, f"places line {next((ln for ln in md.splitlines() if 'CellA' in ln), '')[:300]!r}"


def case_unjudged_count_beside_what_was_judged():
    plan, md, html = P.none_found_bundle()
    line = next((ln for ln in md.splitlines() if "no collision geometry" in ln), "")
    ok = "beside 40 the check did judge" in line
    return ok, f"line {line!r}"


def overlap_reduction_bundle():
    arms = [P.arm("baseline", [BASE, OTHER, A, B], kind="baseline"), P.arm("no-b", [BASE, OTHER, A])]
    rows = [P.row("OVERLAP", B, A, f"00081{i}:{A}", other=f"00091{i}:{B}") for i in (1, 2)]
    counts = dict(verdict="DIFFERENTIAL-VERIFIED", repaired=1, reclassified=0, created=0, newDefect=0, attributed=0,
                  unmeasured=0)
    log = "   GONE     OVERLAP   " + OTHER + "  x  " + B + "  -1 record(s): repaired 1" + NL
    return P.compile_and_render(arms, {(OVERLAP, "baseline"): rows, (OVERLAP, "no-b"): rows[:1]}, [A],
                                order={"no-b": {"overlapprobe": counts}}, diff_logs={(OVERLAP, "no-b"): log})


def case_a_repaired_line_names_its_pair():
    plan, md, html = overlap_reduction_bundle()
    line = next((ln for ln in md.splitlines() if "no longer present" in ln), "")
    ok = f"(between {OTHER} and {B}: 1)" in line
    return ok, f"line {line[:300]!r}"


def case_one_reason_label_per_check():
    """A check missing for two reasons on two setups is said under one label, never two glued with "; and"."""
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("dependency-only", [BASE, OTHER])]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}, [A],
                                          missing=[(OVERLAP, "baseline", "unknown-applicability"),
                                                   (OVERLAP, "dependency-only", "unsupported")])
    glued = [ln for ln in md.splitlines() if "; and " in ln and ln.startswith("- **")]
    ok = not glued and "cannot yet be measured by this evaluator" in md
    return ok, f"glued labels {glued[:2]}"


def case_what_to_do_points_the_right_way():
    """The "What to do" list sits above the items: it never sends the reader to places "listed above"."""
    plan, md, html = contact_patch_bundle()
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = "listed above" not in todo and "its item below" in todo
    return ok, f"what to do {todo[-200:]!r}"


def case_a_state_example_is_an_object_and_its_lost_support():
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline")]
    fk, host = f"0008A3:{A}", f"0DFDA9:{BASE}"
    names = {"refs": {fk: {"base": f"0B0001:{BASE}"}, host: {"base": f"0B0002:{BASE}"}},
             "bases": {f"0B0001:{BASE}": {"editorId": "CommonWardrobe01", "type": "Container", "model": "a.nif"},
                       f"0B0002:{BASE}": {"editorId": "FarmIntBasement", "type": "Static", "model": "b.nif"}}}
    rows = [P.row(f"STATE-EMPTY@0008A8:{A}@Triggered", B, A, fk, other=host)]
    plan, md, html = P.compile_and_render(arms, {("state.conditional-geometry", "baseline"): rows}, [A], names=names)
    line = next((ln for ln in md.splitlines() if "CommonWardrobe01" in ln), "")
    ok = "left in empty space because" in line and " against " not in line
    return ok, f"line {line[:300]!r}"


def kept_remainder_bundle():
    """Two lost changes the patch keeps lost: one on an object the losing mod switches off (a state the line names),
    one on an ordinary field (no state): the breakdown names the first and says the other."""
    WP = [P.arm("baseline", [BASE, OTHER, A, B, PATCH], kind="baseline"), P.arm("without-patch", [BASE, OTHER, A, B])]

    def offs(z, flags="0"):
        return {"Placement.Position": f"100,100,{z}", "Placement.Rotation": "0,0,0", "SkyrimMajorRecordFlags": flags,
                "EnableParent.Reference": "has no EnableParent.Reference", "EnableParent.Flags": "has no EnableParent.Flags"}

    def txt(v):
        return f"Position={v['Placement.Position']}; Rotation=0,0,0"
    D1, D5 = f"00CA77:{BASE}", f"00CA78:{BASE}"
    a_off, b_ = offs(-30000, "InitiallyDisabled"), offs(0)
    p_rows = [P.lost_row(A, B, D1, "Placement", txt(a_off), txt(b_), txt(offs(0)), before=BASE),
              P.lost_row(A, B, D5, "LocationReference", "a5", "b5", "v5", before=BASE)]
    w_rows = [P.lost_row(A, PATCH, D1, "Placement", txt(a_off), txt(b_), txt(offs(0)), before=BASE),
              P.lost_row(A, PATCH, D5, "LocationReference", "a5", "b5", "v5", before=BASE)]
    p_ch = [(D1, "PlacedObject", [(BASE, offs(0)), (A, a_off), (B, b_)]),
            (D5, "PlacedObject", [(BASE, {"LocationReference": "v5"}), (A, {"LocationReference": "a5"}),
                                  (B, {"LocationReference": "b5"})])]
    w_ch = [(D1, "PlacedObject", [(BASE, offs(0)), (A, a_off), (B, b_), (PATCH, b_)]),
            (D5, "PlacedObject", [(BASE, {"LocationReference": "v5"}), (A, {"LocationReference": "a5"}),
                                  (B, {"LocationReference": "b5"}), (PATCH, {"LocationReference": "b5"})])]
    enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): w_rows}
    tr = {(RECORDS, "without-patch"): P.records_transcript(p_ch), (RECORDS, "baseline"): P.records_transcript(w_ch)}
    return P.compile_and_render(WP, {(CONTACT, "baseline"): [], (CONTACT, "without-patch"): []}, [A], patches=[PATCH],
                                count_only={k: len(v) for k, v in enr.items()}, enrichment=enr, transcripts=tr,
                                masters={OTHER: [BASE], A: [BASE], B: [BASE], PATCH: [BASE]})


def case_a_breakdown_says_its_remainder():
    plan, md, html = kept_remainder_bundle()
    kl = next((x for x in pred_of(plan).get("withPatchLines") or [] if x.startswith("Record fields")), "")
    ok = "on 2 (" in kl and "and 1 other" in kl
    return ok, f"line {kl[:400]!r}"


def case_a_breakdown_that_adds_up_has_no_remainder():
    plan, md, html = P.compile_and_render(
        P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A], patches=[PATCH],
        count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1},
        enrichment={(RECORDS, "baseline"): [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")],
                    (RECORDS, "with-patch"): [P.lost_row(A, PATCH, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")]})
    ok = "and 1 other" not in md
    return ok, "no remainder clause where nothing is left over"


def case_the_closing_lists_the_other_unjudged_areas():
    pres = reel_pres()
    pres["reader"]["coverage"] = {"lines": [
        {"reason": "No result: cannot yet be measured by this evaluator", "count": 1, "subject": "A check"},
        {"reason": "Could not be judged", "count": 398, "unit": "items", "subject": "Cell settings changed by more than one mod: x"}],
        "notes": ["No matching play-time evidence was supplied, so this does not show what happened during play."]}
    rows = (chapter(reel_of(pres, MODS), "closing") or {})["still"]["pages"][0].get("rows") or []
    flat = json.dumps(rows)
    ok = ("Cell settings changed by more than one mod: 398 items it could not judge" in flat and "play-time evidence" in flat
          and "A check" not in flat)
    return ok, f"rows {flat[:400]}"


def case_unjudged_reasons_in_plain_words():
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("dependency-only", [BASE, OTHER])]
    pops = {(CONTACT, "baseline"): [{"Population": "placed references", "Judged": 10,
                                     "Unjudgeable": {"a delta line the comparator could not read": 2}}]}
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}, [A],
                                          coverage_pops=pops)
    ok = "a change in the record that houseCARL could not read" in md and "comparator could not read" not in md
    return ok, f"line {next((ln for ln in md.splitlines() if 'could not read' in ln), '')[:300]!r}"


def case_reel_says_the_reports_place():
    """A clash in a cell with no name of its own is said where the report says it (its worldspace and grid), never as
    the plugin file the cell's form key names."""
    fk, other = f"000D01:{A}", f"009301:{BASE}"
    pres = reel_pres()
    pres["reader"]["prediction"]["record"].append(
        {"admission": "admitted", "checkId": "geometry.object-overlap", "formKey": fk, "A": OTHER, "B": A,
         "kind": "OVERLAP", "other": [other], "summary": f"crossing in 00ABCD:{BASE} (ext) [mm] {fk} / {other}"})
    pres["reader"]["prediction"]["placeLabels"] = {fk: f"00ABCD:{BASE} (ext) in Tamriel, grid cell -21, 18"}
    pres["media"] = [{"id": f"M-{st}", "assessed": True, "path": f"media/{st}.png",
                      "provenance": {"vignetteState": st, "captureRequest": "q1", "poseId": "p1"},
                      "subject": {"formKey": fk, "armId": "no-a" if st == "without" else "baseline"}}
                     for st in ("without", "with", "highlight")]
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p)} for p in MODS}
    search = {"pairs": [{"pair": [OTHER, A], "hits": [], "queries": [{"tool": t} for t in
                                                                    POLICY["patchSearch"]["requiredTools"]]}]}
    reel = reel_plan.plan_reel(pres, {}, {}, POLICY, WORDS, search, names,
                               capture_results=[{"status": "captured", "formKey": fk, "request": "q1"}],
                               captures_final=True)
    said = " ".join(str(c.get("narration")) for c in reel.get("chapters") or [] if c.get("kind") == "vignette")
    ok = "Tamriel, grid cell -21, 18" in said and "esm" not in said.lower().replace("skyrim.esm", "")
    return ok, f"vignette {said[:300]!r}; status {reel.get('status')} {reel.get('reason')}"


def case_a_patch_pair_leaves_out_shared_dependencies():
    """A patch built on the two mods and on a master they share: its pair is the two mods, so a clash of one of them with
    that shared master is not the patch's to resolve."""
    DEP = "Shared.esm"
    arms = [P.arm("baseline", [BASE, DEP, A, B], kind="baseline"), P.arm("with-patch", [BASE, DEP, A, B, PATCH])]
    rows_ab = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
    rows_dep = [P.row("CONTACT", DEP, A, f"00090{i}:{A}") for i in (1, 2, 3)]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows_ab + rows_dep,
                                                 (CONTACT, "with-patch"): rows_dep}, [A], patches=[PATCH],
                                          masters={PATCH: [BASE, DEP, A, B], A: [BASE, DEP], B: [BASE, DEP], DEP: [BASE]})
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = (f"if you load {A} and {B}: it resolves 2 of the 2 predicted clashes between them" in todo
          and DEP not in todo.split(":")[0])
    return ok, f"what to do {todo[:300]!r}"


def case_a_patch_pair_from_headers_when_no_probe_recorded_masters():
    """No measured check recorded master lists: the patch's masters are read from its own file's header (when the file
    is the recorded bytes), so it still gets its pair."""
    import hashlib
    with tempfile.TemporaryDirectory() as td:
        files = {}
        for n, ms in ((BASE, []), (A, [BASE]), (B, [BASE]), (PATCH, [BASE, A, B])):
            f = tes4(Path(td) / n, ms)
            files[n] = {"name": n, "path": str(f), "sha256": hashlib.sha256(f.read_bytes()).hexdigest()}

        def arm_(i, names, kind="candidate"):
            a_ = P.arm(i, names, kind=kind)
            a_["requested"]["plugins"] = [files[n] for n in names]
            return a_
        arms = [arm_("baseline", [BASE, A, B], "baseline"), arm_("with-patch", [BASE, A, B, PATCH])]
        rows = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
        plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows, (CONTACT, "with-patch"): rows[1:]},
                                              [A], patches=[PATCH], masters=False)
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = f"Use {PATCH} if you load {A} and {B}: it resolves 1 of the 2" in todo
    return ok, f"what to do {todo[:300]!r}"


def case_record_clashes_judged_against_header_masters_when_no_emit_records_them():
    """No measured check recorded master lists, but each plugin file is the recorded bytes: the report reads the masters
    from the headers, so a mod changing its own master's value is set aside, and it never says it cannot tell."""
    import hashlib
    with tempfile.TemporaryDirectory() as td:
        files = {}
        for n, ms in ((BASE, []), (OTHER, [BASE]), (A, [BASE, OTHER]), (B, [BASE])):
            f = tes4(Path(td) / n, ms)
            files[n] = {"name": n, "path": str(f), "sha256": hashlib.sha256(f.read_bytes()).hexdigest()}

        def arm_(i, names, kind="candidate"):
            a_ = P.arm(i, names, kind=kind)
            a_["requested"]["plugins"] = [files[n] for n in names]
            return a_
        arms = [arm_("baseline", [BASE, OTHER, B, A], "baseline"), arm_("without-a", [BASE, OTHER, B])]
        rec = [P.lost_row(OTHER, A, f"000701:{BASE}", "Name", "o1", "a1", "v1"),
               P.lost_row(A, B, f"000702:{BASE}", "Name", "a2", "b2", "v2")]
        enr = {(RECORDS, "baseline"): rec, (RECORDS, "without-a"): []}
        plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "without-a"): []}, [A],
                                              count_only={(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()},
                                              enrichment=enr, masters=False)
    adm = sorted(((e.get("formKey") or "")[:6], e.get("admission")) for e in pred_of(plan).get("record") or [])
    ok = "records no plugin's master list" not in md and ("000701", "admitted") not in adm and ("000702", "admitted") in adm
    return ok, f"admission {adm}; master-list line {'records no plugin' in md}"


# --------------------------------------------------------------------- retry 1: the closing, pages and subtitles

def closing_pres():
    """A reel whose opening counts 3 of 21 checks without a result, with one check that has no result only on another
    setup, and coverage lines for parts of checks that ran."""
    pres = reel_pres(subjects=12)
    pres["reader"]["prediction"]["incompleteChecks"] = {"total": 21, "checks": [
        {"checkId": "c.one", "title": "Other installed patches for the same mods",
         "reason": "was not run: no list of patch pairs was supplied for it to search"},
        {"checkId": "c.two", "title": "Connected doors, cells, quests and scripts",
         "reason": "cannot yet be measured by this evaluator"},
        {"checkId": "c.three", "title": "Whether a single object blocks an NPC route", "reason": "ran, but could not decide"}]}
    pres["reader"]["coverage"] = {"lines": [
        {"reason": "No result: ran, but could not decide", "count": 1, "unit": "check", "subject": "x"},
        {"reason": "Could not be judged", "count": 9490, "unit": "items",
         "subject": "Quest aliases split between mods: no position in the world to measure (beside 6,810 the check did judge)"},
        {"reason": "Could not be judged", "count": None,
         "subject": "Objects occupying the same space: 1,144 object types whose mesh was read (the mesh could be read only "
                    "in part 963); 18,510 placed objects (no geometry to measure)"},
        {"reason": "Areas not measured", "count": 2, "subject": "objects scripts create during play; mesh replacements"}],
        "notes": ["No matching play-time evidence was supplied, so this does not show what happened during play."]}
    return pres


def case_reel_closing_counts_the_openings_checks_and_parts():
    """The closing opens with the opening's own count and lists exactly those checks, each under its own reason; a check
    with no result only on another setup is said as that; items a check that ran could not judge are parts of that
    check, with what it did judge, never counted as failed checks; the narration is the page's own words in order."""
    reel = reel_of(closing_pres(), MODS)
    page = chapter(reel, "closing")["still"]["pages"][0]
    rows = page.get("rows") or []
    flat = json.dumps(rows)
    measured = [s for r in rows[1:4] for s in r.get("subjects") or []]
    say = chapter(reel, "closing")["narration"]
    ok = (rows and rows[0]["reason"] == "On the measured load order, 3 of the 21 checks have no result."
          and len(measured) == 3 and "Connected doors, cells, quests and scripts" in measured
          and "On another tested setup only" in flat
          and "Quest aliases split between mods: 9,490 items it could not judge, beside 6,810 it did" in flat
          and "Objects occupying the same space: 18,510 placed objects (no geometry to measure) it could not judge, among others" in flat
          and '"Could not be judged"' not in flat and " 1 check." not in say and " checks." not in say
          and hasattr(reel_stills, "page_say") and say == reel_stills.page_say(page))
    return ok, f"rows {flat[:500]}; say {say[:300]!r}"


def tone_wav(path: Path, seconds: float):
    import math
    import struct
    import wave
    rate = 16000
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * 330 * i / rate)))
                               for i in range(int(rate * seconds))))


def render_still_reel(td: Path):
    """The storyboard packet, one tone per scene as long as its words take to say, and the render: (map, status, srt)."""
    import evalreel
    pres = closing_pres()
    reel = reel_of(pres, MODS)
    pres.update({"schemaVersion": 1, "presentationId": "pres-r1", "reel": reel, "chapters": [{"id": "C-1", "captions": []}],
                 "settings": {"reel": {**{k: v for k, v in (PR.get("reel") or {}).items() if k != "words"}, "maxSeconds": 900}}})
    pj = td / "presentation.json"
    pj.write_text(json.dumps(pres), encoding="utf-8")
    sb = td / "storyboard.json"
    if evalreel.main(["storyboard-packet", "--presentation", str(pj), "--out", str(sb)]) != 0:
        return None, None, None
    board = json.loads(sb.read_text(encoding="utf-8"))
    for i, sc in enumerate(board["scenes"]):
        w = td / f"scene-{i:02d}.wav"
        tone_wav(w, max(1.0, len(str(sc.get("narration") or "").split()) / 2.6))
        sc["narrationAudio"] = str(w)
    sb.write_text(json.dumps(board), encoding="utf-8")
    out = td / "reel"
    ff = evalreel.ffmpeg_find(None)
    evalreel.main(["render", "--presentation", str(pj), "--out", str(out), "--storyboard", str(sb),
                   *(["--ffmpeg", ff["exe"]] if ff else [])])
    st = json.loads((out / "reel-status.json").read_text(encoding="utf-8")) if (out / "reel-status.json").is_file() else {}
    mp = json.loads((out / "reel-map.json").read_text(encoding="utf-8")) if (out / "reel-map.json").is_file() else {}
    srt = (out / "reel.srt").read_text(encoding="utf-8") if (out / "reel.srt").is_file() else ""
    return mp, st, srt


def case_reel_pages_are_spoken_in_order_and_never_silent():
    """Decoded audio: every still page is its own scene with its own narration, in page order, held for its own speech;
    the rendered reel has no silence longer than the allowed pause (ffmpeg silencedetect on reel.mp4)."""
    import evalreel
    closing_parts_ = ([" ".join(x["say"].split()) for x in reel_stills.still_parts(
        chapter(reel_of(closing_pres(), MODS), "closing")["still"]["pages"], FONT, BOLD)]
        if hasattr(reel_stills, "still_parts") else None)
    with tempfile.TemporaryDirectory() as td:
        mp, st, srt = render_still_reel(Path(td))
    if mp is None:
        return False, "storyboard packet refused"
    segs = [s for s in mp.get("segments") or [] if s.get("chapterKind") == "closing"]
    held = all(abs(s["reelInterval"]["endSeconds"] - s["reelInterval"]["startSeconds"] - float(s.get("speechSeconds") or 0)
                   - 1.5) < 0.6 for s in segs if s.get("speechSeconds"))
    hold_ = float((POLICY.get("holdSeconds") or {}).get("maxSilence") or 3.0)
    said_ = [" ".join(str(s.get("narration") or "").split()) for s in segs]
    ok = (st.get("status") == "rendered" and len(segs) > 1 and [s.get("page") for s in segs] == list(range(len(segs)))
          and said_ == closing_parts_
          and said_[0].startswith("On the measured load order, 3 of the 21 checks have no result.")
          and held and "silencesOnPages" in st and not st["silencesOnPages"] and not st["silences"] and hold_ <= 3.0)
    return ok, (f"status {st.get('status')} {st.get('errors')}; closing pages {[s.get('page') for s in segs]}; "
                f"silences {st.get('silences')}")


def case_reel_subtitles_one_cue_per_sentence():
    """A subtitle cue per sentence, capped in words and seconds; a long sentence split at its clause marks."""
    import evalreel
    with tempfile.TemporaryDirectory() as td:
        seg = {"narration": "First short sentence. " + " ".join(f"word{i}," for i in range(40)) + " end. Last one.",
               "reelInterval": {"startSeconds": 0.0, "endSeconds": 40.0}, "speechSeconds": 38.0}
        n = evalreel.write_srt(Path(td) / "a.srt", [seg])
        text = (Path(td) / "a.srt").read_text(encoding="utf-8")
    cues = [c.split("\n") for c in text.strip().split("\n\n")]

    def secs(ts):
        h, m, rest = ts.split(":")
        s, ms = rest.split(",")
        return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000
    words_ = [len(" ".join(c[2:]).split()) for c in cues]
    durs = [secs(c[1].split(" --> ")[1]) - secs(c[1].split(" --> ")[0]) for c in cues]
    ok = n >= 5 and max(words_) <= 14 and max(durs) <= 7.0 and cues[0][2] == "First short sentence."
    return ok, f"cues {n}; words {words_}; seconds {[round(d, 1) for d in durs]}"


def case_reel_load_order_page_speaks_what_it_highlights():
    """The load-order page carries its own narration (what it highlights), so it is never shown silent."""
    reel = reel_of(reel_pres(), MODS)
    lo = next(p for p in chapter(reel, "opening")["still"]["pages"] if p["layout"] == "load-order")
    if not hasattr(reel_stills, "page_say"):
        return False, "no narration of a page's own"
    say = reel_stills.page_say(lo)
    ok = ("load order of the measured setup" in say and "take part in a predicted clash" in say
          and re.sub(r"\.esp$", "", A) in say and "next page" not in say)
    return ok, f"say {say!r}"


# --------------------------------------------------------------------- retry 1: the report

def case_no_problem_is_never_said_where_the_comparison_has_no_result():
    """One status source: a check and setup listed as having no accepted result is not also listed as finding no
    problem."""
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("no-other", [BASE, A]), P.arm("no-a", [BASE, OTHER])]
    rows = [P.row("CONTACT", OTHER, A, f"00080{i}:{A}") for i in (1, 2)]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows, (CONTACT, "no-other"): [],
                                                 (CONTACT, "no-a"): []}, [A],
                                          missing=[(CONTACT, "no-other", "unjudged", "diff")])
    nulls = list(P.reader(plan).get("nullSummary") or [])
    no_res = [s for m in P.reader(plan).get("missingSummary") or [] for s in m.get("subjects") or []]
    label = next((s.split(" — ", 1)[1] for s in no_res if " — " in s), None)
    ok = bool(label) and not any(x.split(":")[0].endswith(label) for x in nulls)
    return ok, f"null {nulls}; no result {no_res}"


def case_the_intro_counts_and_describes_setups_from_the_arms():
    """The setup sentence counts the setups that leave out or add another plugin (not one removing only the evaluated mod,
    not a reordering) and says what they do."""
    arms = [P.arm("baseline", [BASE, OTHER, LEG, A], kind="baseline"), P.arm("no-other", [BASE, LEG, A]),
            P.arm("no-other-a", [BASE, LEG]), P.arm("no-a", [BASE, OTHER, LEG])]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, a_["armId"]): [] for a_ in arms}, [A])
    ok = (f"2 tested setups leave out {OTHER}; none of these" in md and "leave out or add" not in md)
    return ok, f"intro {next((ln for ln in md.splitlines() if 'tested setup' in ln), '')[:400]!r}"


def case_a_patch_census_sums_to_its_records_and_changes():
    """Records are the new ones plus the overrides; the overrides' changes are every edit but an addition, and the kinds
    listed sum to them, the rest said as other."""
    with tempfile.TemporaryDirectory() as td:
        d = Path(td) / "cards"
        d.mkdir()
        (d / "summary.json").write_text(json.dumps({"sources": [{
            "patch": PATCH, "census": {"patchRecords": 30, "additions": 5, "edits": 20,
                                       "editKinds": {"FIELD:Lighting": 4, "MOVE": 6, "ROTATE": 3, "ADD": 5, "WEIRD": 2}},
            "cardPass": {"decidingConstraint": {}}}]}), encoding="utf-8")
        plan, md, html = P.compile_and_render(
            P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A, B], patches=[PATCH],
            count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1},
            enrichment={(RECORDS, "baseline"): [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")],
                        (RECORDS, "with-patch"): []},
            settings_extra={"precedentDir": str(d)})
    line = next((ln for ln in md.splitlines() if f"{PATCH} holds" in ln), "")
    m = re.search(r"which make (\d+) changes \(([^)]*)\)", line)
    parts = sum(int(x) for x in re.findall(r"(\d+) ", m.group(2))) if m else -1
    ok = ("holds 30 records: 5 new and 25 overrides" in line and m is not None and m.group(1) == "15" and parts == 15
          and "2 other" in line)
    return ok, f"line {line[:400]!r}"


def case_a_patch_measured_alone_gets_its_own_verdict():
    """Two supplied patches and a setup with each alone: each patch's verdict is measured on its own setup, never said as
    not separated."""
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline"), P.arm("with-both", [BASE, A, B, P1, P2]),
            P.arm("with-p1", [BASE, A, B, P1]), P.arm("with-p2", [BASE, A, B, P2])]
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
    plan, md, html = P.compile_and_render(
        arms, {(CONTACT, "baseline"): rows, (CONTACT, "with-both"): [], (CONTACT, "with-p1"): [],
               (CONTACT, "with-p2"): rows}, [A], patches=[P1, P2],
        masters={P1: [BASE, A, B], P2: [BASE, A, B], A: [BASE], B: [BASE]})
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = (f"Use {P1} if you load {A} and {B}: it resolves 2 of the 2" in todo
          and f"{P2} resolves none of the 2" in todo and "not separated" not in todo)
    return ok, f"what to do {todo[:500]!r}"


def two_patch_chain_bundle():
    """A patch for ModA and ModB (P1) that puts back the base game's lighting over OtherMod's change, and a patch for
    ModA and OtherMod (P2) that sets its own value over ModA's change: the shape of a real two-patch chain."""
    R0, R1, R2 = f"00C301:{BASE}", f"00C302:{BASE}", f"00C303:{BASE}"
    arms = [P.arm("baseline", [BASE, OTHER, A, B, P1, P2], kind="baseline"), P.arm("without-patch", [BASE, OTHER, A, B])]
    p_rows = [P.lost_row(A, B, R0, "LocationReference", "a0", "b0", "v0")]
    w_rows = [P.lost_row(OTHER, P1, R1, "Lighting", ["Lighting=2"], ["Lighting=1"], ["Lighting=1"], before=BASE),
              P.lost_row(A, P2, R2, "Name", ["Name=a"], ["Name=p"], ["Name=v"], before=BASE)]
    w_ch = [(R1, "Cell", [(BASE, {"Lighting": "1"}), (OTHER, {"Lighting": "2"}), (P1, {"Lighting": "1"})]),
            (R2, "Npc", [(BASE, {"Name": "v"}), (A, {"Name": "a"}), (P2, {"Name": "p"})])]
    enr = {(RECORDS, "without-patch"): p_rows, (RECORDS, "baseline"): w_rows}
    tr = {(RECORDS, "baseline"): P.records_transcript(w_ch)}
    return P.compile_and_render(arms, {(CONTACT, "without-patch"): [], (CONTACT, "baseline"): []}, [A, B],
                                patches=[P1, P2], count_only={k: max(1, len(v)) for k, v in enr.items()},
                                enrichment=enr, transcripts=tr,
                                masters={OTHER: [BASE], A: [BASE], B: [BASE], P1: [BASE, A, B], P2: [BASE, A, OTHER]})


def case_each_patchs_changes_said_under_that_patch():
    """In a two-patch chain, the value a patch sets over a mod's change is said under that patch, never under the other
    patch's undone line."""
    plan, md, html = two_patch_chain_bundle()
    un1 = next((x for x in md.splitlines() if x.startswith(f"- Undone by {P1}")), "")
    own2 = next((x for x in md.splitlines() if x.startswith(f"- Set by {P2}")), "")
    ok = bool(un1) and OTHER in un1 and P2 not in un1 and A not in un1.split("(earlier value put back)")[1] and bool(own2) \
        and A in own2
    return ok, f"undone {un1[:300]!r}; own {own2[:300]!r}"


def case_a_patchs_side_effect_is_in_its_verdict():
    """A patch that puts back another mod's changes says so beside its verdict, on the records it touches."""
    plan, md, html = two_patch_chain_bundle()
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = f"puts back the earlier value of 1 record field that {OTHER} changes, on 00C302:{BASE}" in todo
    return ok, f"what to do {todo[:500]!r}"


def case_resolves_reconciles_with_its_total():
    """"Resolves x of y": the resolved, the left, the unmeasured and the patch's own changes add up to y, one unit."""
    if not hasattr(R, "what_to_do"):
        return False, "no verdict per patch"
    lead = {"prediction": {"items": [{"title": "Objects occupying the same space", "count": 10, "mods": [A, B],
                                      "checkId": "geometry.object-overlap", "kind": "OVERLAP", "shownAs": "pair"}]},
            "validation": {"status": "compared", "patches": [PATCH],
                           "byItem": [{"hits": 4, "open": 2, "kept": 1, "own": 2, "placed": 1}]}}
    pw = PR["reader"]["prediction"]
    todo = " ".join(R.what_to_do(lead, {PATCH.lower(): {A.lower(), B.lower()}, A.lower(): set(), B.lower(): set()},
                                 pw, {}, [], {}))
    m = re.search(r"resolves (\d+) of the (\d+)[^;]*; (\d+) (?:is|are) left(?:; on (\d+) it keeps one mod's value[^;]*)?"
                  r"; the other (\d+)", todo)
    ok = m is not None and int(m.group(1)) + int(m.group(3)) + int(m.group(4) or 0) + int(m.group(5)) == int(m.group(2)) \
        and "2 set to the patch's own value" in todo
    return ok, f"what to do {todo[:400]!r}"


def case_a_clash_counted_under_an_earlier_check_is_not_counted_again():
    if not hasattr(R, "what_to_do"):
        return False, "no verdict per patch"
    lead = {"prediction": {"items": [
        {"title": "Fixtures", "count": 1, "mods": [A, B], "checkId": "geometry.fixture-empty-space", "kind": "F", "shownAs": "pair"},
        {"title": "Support", "count": 1, "mods": [A, B], "checkId": "geometry.placement-contact", "kind": "CONTACT",
         "shownAs": "pair", "countedElsewhere": True}]},
        "validation": {"status": "compared", "patches": [PATCH], "byItem": [{"hits": 1}, {"hits": 1}]}}
    todo = " ".join(R.what_to_do(lead, {PATCH.lower(): {A.lower(), B.lower()}}, PR["reader"]["prediction"], {}, [], {}))
    ok = "resolves 1 of the 1 predicted clash" in todo
    return ok, f"what to do {todo[:300]!r}"


def case_plugin_names_in_their_load_order_spelling():
    """A probe that reports a plugin by its file name in another case is said in the load order's spelling."""
    low = B.lower()
    arms = [P.arm("baseline", [BASE, B, A], kind="baseline"), P.arm("no-b", [BASE, A])]
    rows = [P.row("CONTACT", low, A, f"00080{i}:{A}") for i in (1, 2)]
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): rows, (CONTACT, "no-b"): []}, [A])
    ok = low not in md and B in md
    return ok, f"lower-case left {md.count(low)}; proper {md.count(B)}"


def case_records_and_places_said_by_name():
    """A record or place the report gives by form key is said with its name, read from the names enrichment's own read
    of the plugin files (an NPC's or a cell's name); the pointer to precedent cards is said only where they are listed."""
    class B_:
        pass
    with tempfile.TemporaryDirectory() as td:
        d = Path(td) / "enrichment" / "names"
        d.mkdir(parents=True)
        (d / "names.json").write_text("{}", encoding="utf-8")
        recs = {"records": [{"formid": f"00C401:{BASE}", "type": "Cell", "editorid": "TownInnCell",
                             "fields": [{"path": "Name", "value": "The Town Inn"}]},
                            {"formid": f"00C402:{BASE}", "type": "Npc", "editorid": "TownGuardNpc", "fields": []}]}
        (d / "02-response.jsonl").write_text(json.dumps({"result": {"content": [{"type": "text", "text": json.dumps(recs)}]}}),
                                             encoding="utf-8")
        b = B_()
        b.dir = Path(td)
        b.enrichment = lambda: [{"enrichment": "names", "path": "enrichment/names/names.json"}]
        if not (hasattr(R, "record_labels") and hasattr(R, "label_form_keys")):
            return False, "no record names read for the report"
        labels = R.record_labels(b)
    said = R.label_form_keys({"line": f"it puts back the Lighting of 00C401:{BASE}, as on 00C402:{BASE} (AIData)",
                              "formKey": f"00C401:{BASE}"}, labels)
    ok = (said["line"] == f"it puts back the Lighting of The Town Inn, 00C401:{BASE}, as on TownGuardNpc, 00C402:{BASE} (AIData)"
          and said["formKey"] == f"00C401:{BASE}")
    return ok, f"labels {labels}; said {said}"


def case_precedent_pointer_only_where_the_index_lists_them():
    with tempfile.TemporaryDirectory() as td:
        d = Path(td) / "cards"
        (d / re.sub(r"\.esp$", "", PATCH)).mkdir(parents=True)
        (d / "summary.json").write_text(json.dumps({"sources": [{
            "patch": PATCH, "census": {"patchRecords": 2, "additions": 0, "edits": 2, "editKinds": {"DISABLE": 2}},
            "cardPass": {"decidingConstraint": {"lighting-intent": 2}}}]}), encoding="utf-8")
        (d / re.sub(r"\.esp$", "", PATCH) / "cards.jsonl").write_text(json.dumps(
            {"whatThePatchDid": "2 edits on 2 records (DISABLE 2).", "reason": "keeps the inn dark",
             "decidingConstraint": "lighting-intent",
             "whatItTouched": {"cell": {"formKey": f"00C401:{BASE}", "editorId": "TownInnCell", "name": "The Town Inn"}}}) + "\n",
            encoding="utf-8")
        plan, md, html, idx = P.compile_and_render(
            P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A, B], patches=[PATCH],
            want_index=True, count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1},
            enrichment={(RECORDS, "baseline"): [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")],
                        (RECORDS, "with-patch"): []},
            settings_extra={"precedentDir": str(d)})
    ok = ('"Precedent cards"' in md and "## Precedent cards (1)" in idx and "The Town Inn (TownInnCell)" in idx
          and "keeps the inn dark" in idx)
    return ok, f"md {('Precedent' in md)}; index {idx[idx.find('Precedent cards'):][:300]!r}"


# --------------------------------------------------------------------- retry 2

def case_a_value_the_patch_keeps_is_its_choice():
    """A supplied patch that carries the contested fields with one side's value: its author's choice, said as the patch
    keeping that mod's value, never as a clash that needs another patch."""
    if not hasattr(R, "what_to_do"):
        return False, "no verdict per patch"
    lead = {"prediction": {"items": [{"title": "Record fields changed by more than one mod", "count": 3, "mods": [A, B],
                                      "checkId": "records.contested-fields", "kind": "LOST-EDIT", "shownAs": "pair"}]},
            "validation": {"status": "compared", "patches": [PATCH],
                           "byItem": [{"hits": 0, "open": 0, "kept": 3, "keptBy": {f"{A}|{B}": 3}}]}}
    todo = " ".join(R.what_to_do(lead, {PATCH.lower(): {A.lower(), B.lower()}}, PR["reader"]["prediction"], {}, [], {}))
    ok = f"the supplied patch keeps {B}'s value" in todo and "needs a patch that carries both" not in todo
    return ok, f"what to do {todo[:400]!r}"


def case_a_patch_that_switches_off_another_mods_object_says_so():
    """A patch that switches off an object another mod places, or removes a record's scripts, says so in its verdict,
    read from the patch's own records (the precedent stage's edit list)."""
    with tempfile.TemporaryDirectory() as td:
        d = Path(td) / "cards"
        (d / re.sub(r"\.esp$", "", PATCH)).mkdir(parents=True)
        (d / "summary.json").write_text(json.dumps({"sources": [{
            "patch": PATCH, "census": {"patchRecords": 2, "additions": 0, "edits": 2, "editKinds": {"DISABLE": 1, "FIELD:VirtualMachineAdapter": 1}},
            "cardPass": {"decidingConstraint": {}}}]}), encoding="utf-8")
        edits = [{"formKey": f"000901:{B}", "kind": "DISABLE", "editorId": "ModBGuardRef",
                  "values": {"patch": {"values": {"MajorRecordFlagsRaw": "3072"}}},
                  "touched": {"base": {"editorId": "ModBGuard", "type": "Npc"}, "cell": {"name": "The Town Inn"}}},
                 {"formKey": f"000902:{B}", "kind": "FIELD:VirtualMachineAdapter", "editorId": "ModBQuestEffect",
                  "values": {"patch": {"values": {"VirtualMachineAdapter.Version": None}}}, "touched": {}}]
        (d / re.sub(r"\.esp$", "", PATCH) / "edits.jsonl").write_text("\n".join(json.dumps(e) for e in edits) + "\n",
                                                                       encoding="utf-8")
        plan, md, html = P.compile_and_render(
            P.PATCH_ADDED_ARMS, {(CONTACT, "baseline"): [], (CONTACT, "with-patch"): []}, [A, B], patches=[PATCH],
            count_only={(RECORDS, "baseline"): 1, (RECORDS, "with-patch"): 1},
            enrichment={(RECORDS, "baseline"): [P.lost_row(A, B, f"000701:{BASE}", "LocationReference", "a1", "b1", "v")],
                        (RECORDS, "with-patch"): []},
            settings_extra={"precedentDir": str(d)})
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    ok = ("It switches off ModBGuardRef (a placed ModBGuard, in The Town Inn), which " + B in todo
          and "removes the scripts from ModBQuestEffect" in todo)
    return ok, f"what to do {todo[:500]!r}"


def case_the_unpictured_page_accounts_for_every_clash():
    """Per kind, in the card's unit: pictured + represented + not pictured = the card's count; the reasons are sentences,
    counted in the same unit."""
    reel = reel_of(reel_pres(), MODS)
    types = next(p for p in chapter(reel, "opening")["still"]["pages"] if p["layout"] == "types")
    card = {r["label"]: r["count"] for r in types["rows"]}
    un = str((chapter(reel, "unpictured") or {}).get("narration"))
    parts = re.findall(r"([^.:]+): of (\d+) [^,]*, (\d+) pictured, (\d+) represented by the pictured example and (\d+) not pictured", un)
    parts += [(lab, n, "0", "0", n) for lab, n in re.findall(r"([^.:]+): (\d+) [^,.]*, none pictured", un)]
    ok_parts = parts and all(int(n) == int(a) + int(b) + int(c) and card.get(lab.strip()) == int(n) for lab, n, a, b, c in parts)
    why = sum(int(x) for x in re.findall(r"\((\d+) not pictured\)", un))
    ok = bool(ok_parts) and why == sum(int(c) for *_, c in parts) and "Why some are not pictured:" in un
    return ok, f"parts {parts}; reasons {why}; {un[:300]!r}"


def case_closing_names_each_setup_by_its_own_arm():
    """A gap on another tested setup is named by what that setup leaves out or adds, never by a supplied patch it does not
    concern."""
    pres = reel_pres()
    pres["scope"]["arms"].append({"armId": "no-other", "plugins": [BASE, A], "locationPluginOrder": [BASE, A],
                                  "displayLabel": "Tested alternative setup", "pluginsRemoved": [OTHER], "pluginsAdded": [],
                                  "readerLabel": "Tested alternative setup (removes OtherMod.esp)"})
    pres["reader"]["suppliedPatches"] = [PATCH]
    pres["reader"]["missingSummary"] = [
        {"reason": "ran, but could not decide", "subjects": ["Objects occupying the same space — Tested alternative setup (removes OtherMod.esp)"]},
        {"reason": "was not run", "subjects": ["Objects occupying the same space — Measured load order"]}]
    flat = json.dumps(((chapter(reel_of(pres, MODS + [PATCH]), "closing") or {}).get("still") or {"pages": [{}]})["pages"][0].get("rows") or [])
    other = re.sub(r"\.esp$", "", OTHER)
    ok = f"on the setup without {other}" in flat and re.sub(r"\.esp$", "", PATCH) not in flat
    return ok, f"rows {flat[:400]}"


def case_subtitles_never_overlap_and_never_flash():
    """The written .srt: cues in order, none overlapping the next, none shorter than the minimum (short ones merged)."""
    import evalreel
    segs = [{"narration": "Yes. No. Maybe so. A longer sentence follows here to fill the time. Ok.",
             "reelInterval": {"startSeconds": 0.0, "endSeconds": 8.0}, "speechSeconds": 7.5},
            {"narration": "One. Two words. " + " ".join(f"w{i}" for i in range(30)) + ".",
             "reelInterval": {"startSeconds": 8.0, "endSeconds": 20.0}, "speechSeconds": 11.0}]
    with tempfile.TemporaryDirectory() as td:
        evalreel.write_srt(Path(td) / "a.srt", segs)
        text = (Path(td) / "a.srt").read_text(encoding="utf-8")

    def secs(ts):
        h, m, rest = ts.split(":")
        s, ms = rest.split(",")
        return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000
    cues = [c.split("\n") for c in text.strip().split("\n\n")]
    spans = [(secs(c[1].split(" --> ")[0]), secs(c[1].split(" --> ")[1])) for c in cues]
    overlap = sum(1 for (a, e), (b, _) in zip(spans, spans[1:]) if e > b + 1e-6)
    short = [round(e - a, 2) for a, e in spans if e - a < 1.0]
    ok = not overlap and not short and all(a < e for a, e in spans) and len(spans) >= 4
    return ok, f"cues {len(spans)}; overlaps {overlap}; short {short}"


def case_each_clash_says_its_own_in_game_meaning():
    """Two record clashes of different kinds: each one's in-game sentence comes from its own fields."""
    arms = [P.arm("baseline", [BASE, OTHER, B, A], kind="baseline"), P.arm("no-a", [BASE, OTHER, B])]
    rec = [P.lost_row(B, A, f"000801:{BASE}", "AIData", "b1", "a1", "v1"),
           P.lost_row(OTHER, A, f"000802:{BASE}", "EnableParent", "o2", "a2", "v2")]
    enr = {(RECORDS, "baseline"): rec, (RECORDS, "no-a"): []}
    plan, md, html = P.compile_and_render(arms, {(CONTACT, "baseline"): [], (CONTACT, "no-a"): []}, [A],
                                          count_only={(RECORDS, a): max(1, len(r)) for (_, a), r in enr.items()},
                                          enrichment=enr)
    fm = PR["reader"]["prediction"].get("fieldMeanings")
    if not fm:
        return False, "no field meanings"
    items = {tuple(sorted(i.get("mods") or [])): str(i.get("inGameText") or "") for i in pred_of(plan).get("items") or []
             if i.get("kind") == "LOST-EDIT"}
    ab, ao = items.get(tuple(sorted([A, B]))) or "", items.get(tuple(sorted([A, OTHER]))) or ""
    ok = (fm["AIData"] in ab and fm["EnableParent"] not in ab and fm["EnableParent"] in ao and fm["AIData"] not in ao)
    return ok, f"A/B {ab[:200]!r}; A/Other {ao[:200]!r}"


def case_one_name_for_each_kind_of_clash():
    """A kind of clash that comes from one check is called by that check's title on the opening card too."""
    pres = reel_pres()
    pres["reader"]["checkTitles"] = {"geometry.placement-contact": "Objects left without their supporting surface"}
    types = next(p for p in chapter(reel_of(pres, MODS), "opening")["still"]["pages"] if p["layout"] == "types")
    labels = [r["label"] for r in types["rows"]]
    ok = "Objects left without their supporting surface" in labels and WORDS["typeLabels"]["contact"] not in labels
    return ok, f"labels {labels}"


def vignette_pres(**reader_extra):
    fk, other = f"000D01:{A}", f"009301:{BASE}"
    pres = reel_pres()
    pres["reader"]["prediction"]["record"].append(
        {"admission": "admitted", "checkId": "geometry.object-overlap", "formKey": fk, "A": OTHER, "B": A,
         "kind": "OVERLAP", "other": [other], "summary": f"crossing in 00A366:{BASE} (ext) [mm] {fk} / {other}"})
    pres["media"] = [{"id": f"M-{st}", "assessed": True, "path": f"media/{st}.png",
                      "provenance": {"vignetteState": st, "captureRequest": "q1", "poseId": "p1"},
                      "subject": {"formKey": fk, "armId": "no-a" if st == "without" else "baseline"}}
                     for st in ("without", "with", "highlight")]
    for k, v in reader_extra.items():
        pres["reader"]["prediction"][k] = v
    return pres, fk


def vignette_said(pres, fk, masters=None):
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p)} for p in MODS + [PATCH]}
    search = {"pairs": [{"pair": [OTHER, A], "hits": [], "queries": [{"tool": t} for t in
                                                                    POLICY["patchSearch"]["requiredTools"]]}]}
    reel = reel_plan.plan_reel(pres, {}, masters or {}, POLICY, WORDS, search, names,
                               capture_results=[{"status": "captured", "formKey": fk, "request": "q1"}],
                               captures_final=True)
    return " ".join(str(c.get("narration")) for c in reel.get("chapters") or [] if c.get("kind") == "vignette")


def case_a_supplied_patch_the_search_missed_is_said_as_a_miss():
    """Where a supplied patch is for the pair and the name search found nothing, the reel says the search missed that
    patch, never that no other patch exists."""
    pres, fk = vignette_pres()
    pres["reader"]["suppliedPatches"] = [PATCH]
    said = vignette_said(pres, fk, masters={PATCH.lower(): {OTHER.lower(), A.lower()}})
    pname = re.sub(r"\.esp$", "", PATCH)
    ok = f"did not find {pname}" in said and "No other patch" not in said and "No patch for these" not in said
    return ok, f"vignette {said[:400]!r}"


def case_the_reel_keeps_the_reports_attribution_caveat():
    """The report's "whether it also needs X was not measured" is said with the reel's vignette of that clash."""
    cav = f"It appears when {A} is added; whether it also needs {OTHER} was not measured, because no setup without {OTHER} was measured for this check."
    pres, fk = vignette_pres(items=[{"title": "Objects occupying the same space", "count": 1, "shownAs": "pair",
                                     "checkId": "geometry.object-overlap", "mods": [OTHER, A], "attributionText": cav}])
    said = vignette_said(pres, fk)
    other = re.sub(r"\.esp$", "", OTHER)
    ok = f"whether it also needs {other} was not measured" in said
    return ok, f"vignette {said[:400]!r}"


def case_a_check_on_many_setups_is_said_once():
    """A check with no result on several other setups is said once, with how many, not once per setup."""
    pres = reel_pres()
    for i_, gone in enumerate(([OTHER], [B], [OTHER, B])):
        pres["scope"]["arms"].append({"armId": f"alt-{i_}", "plugins": [BASE, A], "displayLabel": "Tested alternative setup",
                                      "readerLabel": f"Tested alternative setup {i_}", "pluginsRemoved": gone,
                                      "pluginsAdded": []})
    pres["reader"]["missingSummary"] = [{"reason": "ran, but could not decide", "subjects": [
        f"Which tested load order has fewer measured problems — Tested alternative setup {i_}" for i_ in range(3)]}]
    rows = ((chapter(reel_of(pres, MODS + [B]), "closing") or {}).get("still") or {"pages": [{}]})["pages"][0].get("rows") or []
    subs = [s for r in rows for s in r.get("subjects") or [] if "fewer measured problems" in s]
    ok = subs == ["Which tested load order has fewer measured problems, on 3 other tested setups (the report names each)"]
    return ok, f"subjects {subs}"


CASES = [(k, v) for k, v in list(globals().items()) if k.startswith("case_") and callable(v)]


def main(argv=None) -> int:
    want = list(argv if argv is not None else sys.argv[1:])
    fails = 0
    for name, fn in CASES:
        short = name[len("case_"):]
        if want and not any(short.startswith(w) or name.startswith(w) for w in want):
            continue
        try:
            ok, detail = fn()
        except Exception:
            ok, detail = False, "CRASH " + traceback.format_exc(limit=3).replace("\n", " | ")
        print(f"[{'PASS' if ok else 'FAIL'}] {short}: {detail}")
        fails += 0 if ok else 1
    print(f"{fails} failure(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
