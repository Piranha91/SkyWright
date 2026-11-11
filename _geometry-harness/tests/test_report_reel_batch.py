#!/usr/bin/env python
"""Known answers for the report and reel wording batch (regeneration 3's smaller items and item 18's small defects),
C1780-C1798 (C1797 is held for the separate carried-value task). Each case drives the real compiler, templates, reel planner, reader page or record comparator on a small
fixture and asserts what a reader sees. A case marked MOOT is a guard that holds on the base: the item it covers is
already settled, and the case says by what.

    python _geometry-harness/tests/test_report_reel_batch.py            every case
    python _geometry-harness/tests/test_report_reel_batch.py C1785 ...   the named cases (a prefix match)
Exit 0 all pass, 1 a case failed.
"""
from __future__ import annotations

import copy
import json
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import test_evalreport_prediction as P  # noqa: E402  (sets the no-settings environment before evalreport loads)
import evalreport as R  # noqa: E402
import reel_plan  # noqa: E402

SKILL = HERE.parent.parent / ".claude/skills/evaluation-reporting/references"
PR = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
BASE, A, B, OTHER, PATCH = P.BASE, P.MOD_A, P.MOD_B, P.OTHER, P.PATCH
CONTACT, OVERLAP, RECORDS, CELLATTR, NAVOWN = P.CONTACT, P.OVERLAP, P.RECORDS, P.CELLATTR, P.NAVOWN
LINKED, ALIAS, GENOUT = "functional.linked-dependencies", "functional.quest-alias-split", \
    "environment.generated-output-regeneration"
for c_ in (LINKED, GENOUT):
    P.PROBE.setdefault(c_, None)


def pred_of(plan):
    return (plan.get("reader") or {}).get("prediction") or {}


# ------------------------------------------------------------------------------------------------- the reel

def c1780_reel_one_case_singular():
    """W3 (regeneration 3, WRW and Cut Content reels: "the 1 cases"): the reel's closed-capture sentence for a kind with
    one clash says "1 case", never "1 cases"."""
    import test_reel_vignettes as RV
    pres, names, masters, search, mods, _ = RV.capture_binding_fixture()
    reel = RV.plan_final(pres | {"media": []}, names, masters, search, mods, [], final=True)
    said = " ".join(str(c.get("narration") or "") for c in reel.get("chapters") or [])
    one = [n for n in reel.get("noneCaptured") or [] if n.get("count") == 1]
    ok = bool(one) and not re.search(r"\b1 cases\b", said)
    return ok, f"kinds with one clash {one}; said {re.findall(r'[^.]*\b1 cases?\b[^.;]*', said)}"


def c1791_reel_patch_line_keyed_to_its_own_pair():
    """M5 (Cut Content reels, retry 2 regression): a supplied patch for ModA and ModB whose masters also hold OtherMod
    (both mods are built on it) is not the patch of a clash between OtherMod and ModA. That clash's vignette names no
    supplied patch (neither "does not fix this one" nor "the search did not find it either")."""
    import test_reader_rules as RR
    fk, other = f"000D01:{A}", f"009301:{OTHER}"
    pres = RR.reel_pres()
    pred = pres["reader"]["prediction"]
    pred["record"].append({"admission": "admitted", "checkId": "geometry.object-overlap", "formKey": fk, "A": OTHER,
                           "B": A, "kind": "OVERLAP", "other": [other],
                           "summary": f"crossing in RoomOne (int) [mm] {fk} / {other}"})
    pres["reader"]["suppliedPatches"] = [PATCH]
    pres["media"] = [{"id": f"M-{st}", "assessed": True, "path": f"media/{st}.png",
                      "provenance": {"vignetteState": st, "captureRequest": "q1", "poseId": "p1"},
                      "subject": {"formKey": fk, "armId": "no-a" if st == "without" else "baseline"}}
                     for st in ("without", "with", "highlight")]
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p)} for p in RR.MODS + [B, PATCH]}
    search = {"pairs": [{"pair": [OTHER, A], "hits": [], "queries": [{"tool": t} for t in
                                                                    RR.POLICY["patchSearch"]["requiredTools"]]}]}
    low = lambda *xs: {x.lower() for x in xs}  # noqa: E731
    masters = {PATCH.lower(): low(BASE, OTHER, A, B), A.lower(): low(BASE, OTHER), B.lower(): low(BASE, OTHER),
               OTHER.lower(): low(BASE)}
    reel = reel_plan.plan_reel(pres, {}, masters, RR.POLICY, RR.WORDS, search, names,
                               capture_results=[{"status": "captured", "formKey": fk, "request": "q1"}],
                               captures_final=True)
    vig = next((c for c in reel.get("chapters") or [] if c.get("kind") == "vignette"
                and (c.get("clash") or {}).get("formKey") == fk), None)
    if vig is None:
        return False, f"no vignette for the clash; status {reel.get('status')} {reel.get('reason')}"
    ev = vig.get("patch") or {}
    said = str(vig.get("narration") or "")
    ok = ev.get("patch") is None and "ModA - ModB patch" not in said
    return ok, f"patch evidence {ev.get('case')!r} {ev.get('patch')!r}; vignette {said[-260:]!r}"


def c1792_closing_without_setup_branch_unchanged():
    """M6 (MOOT by construction, a guard for removing the dead branch): a gap on a setup that is not one of the arms,
    for a check with two reasons, with a supplied patch: `withSetup` is never chosen (its patch list is read from an arm
    that does not exist), so the line is the `withoutSetup` one. Removing the branch must leave this output as it is."""
    words = PR["reel"]["words"]
    reader = {"missingSummary": [{"reason": "was stopped when the evaluation's time budget ran out",
                                  "subjects": ["Check one — Some other setup"]},
                                 {"reason": "cannot yet be measured by this evaluator",
                                  "subjects": ["Check one — Another unnamed setup"]}],
              "suppliedPatches": [PATCH]}
    pres = {"scope": {"arms": [{"armId": "baseline", "displayLabel": "Measured load order",
                                "locationPluginOrder": [BASE, A, PATCH]}]}}
    rows = reel_plan.closing_checks(reader, pres, lambda x: x, lambda p: p.rsplit(".", 1)[0], words)
    subs = [s for r in rows for s in r["subjects"]]
    want = words["closingLines"]["withoutSetup"].format(check="Check one", patch=PATCH.rsplit(".", 1)[0])
    ok = subs == [want, want]
    return ok, f"lines {subs}"


# ----------------------------------------------------------------------------------------------- the report

def c1781_one_reason_per_heading_and_the_same_reason_everywhere():
    """W4 (Hroggar and Helgi: connected doors said "cannot yet be measured" in one place and "may apply" in another, two
    bullets with one heading): a check with two statuses on every setup is listed once under one heading with the other
    checks of that reason, and the patch section gives it the same reason."""
    arms = [P.arm("baseline", [BASE, A, B, PATCH], kind="baseline"), P.arm("without-patch", [BASE, A, B])]
    unp = [P.row("CONTACT", B, A, f"00080{i}:{A}") for i in (1, 2)]
    res = {(CONTACT, "baseline"): [unp[0]], (CONTACT, "without-patch"): unp}
    miss = ([(LINKED, a_, s_) for a_ in ("baseline", "without-patch") for s_ in ("unknown-applicability", "unsupported")]
            + [(ALIAS, a_, "unsupported") for a_ in ("baseline", "without-patch")])
    plan, md, _ = P.compile_and_render(arms, res, [A, B], patches=[PATCH], missing=miss)
    sec = P.section_text(md, "What the checks could not judge")
    heads = re.findall(r"^- \*\*(No result: [^*]+):\*\*", sec, re.M)
    dup = sorted({h for h in heads if heads.count(h) > 1})
    gap = next((ln for ln in md.splitlines() if "Not run or not decided for the setup with" in ln), "")
    m_ = re.search(r"Connected doors, cells, quests and scripts \(([^)]*)\)", gap)
    head_of_linked = next((h for h in heads for ln in sec.splitlines()
                           if ln.startswith(f"- **{h}:**") and "Connected doors" in ln), None)
    same = bool(m_) and head_of_linked is not None and m_.group(1) == head_of_linked.split("No result: ", 1)[1]
    ok = not dup and same
    return ok, f"repeated headings {dup}; patch section says {m_.group(1) if m_ else None!r}; listed under {head_of_linked!r}"


def c1782_offline_render_caption_says_render():
    """W5 (Ice and Cut Content: images not labelled as offline renders): a figure whose media kind is an offline render
    says, in its caption, that it is drawn from the game's files, not taken in the game; a game capture does not."""
    fn = getattr(R, "media_kind_caption", None)
    if fn is None:
        return False, "evalreport has no media-kind caption rule a test can call (captions ignore mediaKind)"
    words = (PR.get("reader") or {}).get("figureCaptions") or {}
    r_, g_ = fn({"mediaKind": "offline-render"}, words), fn({"mediaKind": "native-screenshot"}, words)
    ok = "render" in r_.lower() and "not" in r_.lower() and g_ == ""
    return ok, f"render {r_!r}; game capture {g_!r}"


def c1783_location_order_says_its_share_of_the_load_order():
    """W6 (Helgi, Cut Content: the report's location list has 9 or 11 plugins, the reel's load order 15 or 16): the
    report's location list says how many of the setup's plugins it lists."""
    arms = [P.arm("baseline", [BASE, OTHER, "Extra1.esp", "Extra2.esp", A, B], kind="baseline"),
            P.arm("dependency-only", [BASE, OTHER, "Extra1.esp", "Extra2.esp"])]
    res = {(CONTACT, "baseline"): [P.row("CONTACT", B, A, f"000801:{A}")], (CONTACT, "dependency-only"): []}
    plan, md, _ = P.compile_and_render(arms, res, [A, B])
    sec = P.section_text(md, "Load order for these locations")
    part = sec.split("**Measured load order**", 1)[-1].split("**Tested", 1)[0]
    ok = re.search(r"\b2 of the 6 plugins\b", part) is not None
    return ok, f"measured setup's paragraph {part.strip()[:300]!r}"


def c1784_rebuild_generated_output_in_what_to_do():
    """W7 (Cut Content: the LOD and occlusion rebuild missing from "What to do"): the generated-output check needs a
    modder's review on the measured setup and the mod places objects in exterior cells, so "What to do" says to rebuild
    generated output after adding it. Control: a mod with interior cells only gets no such step."""
    def todo(cells):
        arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("dependency-only", [BASE, OTHER])]
        res = {(CONTACT, "baseline"): [P.row("CONTACT", OTHER, A, f"000801:{A}")], (CONTACT, "dependency-only"): []}
        miss = [(GENOUT, a_, "manual") for a_ in ("baseline", "dependency-only")]
        plan, md, _ = P.compile_and_render(arms, res, [A], missing=miss, cells=cells)
        return " ".join(pred_of(plan).get("whatToDo") or [])
    ext = todo([{"cell": f"00A001:{BASE}", "kind": "exterior", "worldspace": "Tamriel", "x": 3, "y": 4, "refs": 5}])
    inn = todo([{"cell": f"00A002:{BASE}", "kind": "interior", "name": "RoomOne", "refs": 5}])
    ok = re.search(r"\brebuild\b", ext, re.I) is not None and not re.search(r"\brebuild\b", inn, re.I)
    return ok, f"exterior {ext[:260]!r}; interior {inn[:160]!r}"


def c1785_pdf_links_are_relative():
    """M1 (Ice: the PDF's evidence links are absolute file:/// paths): the printed PDF links the evidence index by a
    path relative to the report folder."""
    browser, _, why = R.resolve_browser()
    if browser is None:
        return False, f"NOT ATTEMPTED: no browser to print with ({why})"
    with tempfile.TemporaryDirectory() as td:
        d = Path(td)
        html = d / "report.html"
        html.write_text('<!doctype html><html><body><p><a href="evidence-index.html#F-001">Full evidence</a></p>'
                        "</body></html>", encoding="utf-8")
        (d / "evidence-index.html").write_text("<p id='F-001'>x</p>", encoding="utf-8")
        ok_, err = R.print_pdf(browser, html, d / "report.pdf")
        if not ok_:
            return False, f"NOT ATTEMPTED: the browser did not print ({err})"
        pages, root, note = R.pdf_page_count(d / "report.pdf")
        try:
            import pypdf
        except ImportError:
            return False, "NOT ATTEMPTED: pypdf is not installed, so the link annotations cannot be read"
        # the link annotations as a PDF reader reads them, in strict mode (a broken cross-reference raises)
        rd = pypdf.PdfReader(str(d / "report.pdf"), strict=True)
        uris = [str(a.get_object()["/A"]["/URI"]) for pg in rd.pages for a in (pg.get("/Annots") or [])
                if "/A" in a.get_object() and "/URI" in a.get_object()["/A"]]
        n_pages = len(rd.pages)
        # every in-use cross-reference entry points at its own object's header (a reader may repair a wrong one quietly)
        data = (d / "report.pdf").read_bytes()
        at = int(re.findall(rb"startxref\s+(\d+)", data)[-1])
        bad, num = [], 0
        for m_ in re.finditer(rb"(\d{10}) (\d{5}) ([nf])|(\d+) (\d+)\r?\n", data[at + 4:data.index(b"trailer", at)]):
            if m_.group(4) is not None:
                num = int(m_.group(4))
                continue
            if m_.group(3) == b"n" and not data[int(m_.group(1)):].startswith(b"%d %d obj" % (num, int(m_.group(2)))):
                bad.append(num)
            num += 1
    ok = (uris == ["evidence-index.html#F-001"] and note == "ok" and n_pages == pages == root and not bad)
    return ok, f"link annotations {uris}; pages {n_pages} (reader) {pages}/{root} (count) {note}; xref entries off {bad}"


def c1786_place_named_by_its_in_game_name():
    """M2 (Helgi: "All are in MorthalAlvasHouse (int)"): an item's place whose editor ID the names enrichment names is
    said by that name."""
    rows = [P.row("CONTACT", B, A, f"00080{i}:{A}", label="RoomOneInterior (int)") for i in (1, 2)]
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline"), P.arm("plus-a", [BASE, A]), P.arm("plus-b", [BASE, B])]
    res = {(CONTACT, "baseline"): rows, (CONTACT, "plus-a"): [], (CONTACT, "plus-b"): []}
    names = {"places": {"RoomOneInterior": {"name": "Alva's Room", "formKey": f"00A010:{BASE}", "from": A}}}
    plan, md, _ = P.compile_and_render(arms, res, [A, B], names=names)
    line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "supporting surface" in ln), "")
    ok = "Alva's Room" in line and "RoomOneInterior (int)" not in line
    return ok, f"item {line[-260:]!r}"


def c1787_exterior_cell_by_world_and_grid():
    """M2 (Helgi: "All are in 0093C0:Skyrim.esm (ext)", a navmesh item): an exterior cell named only by its form key is
    said with its worldspace and grid, from the mod's own footprint."""
    cell = f"0093C0:{BASE}"
    rows = [P.row("EDGE-IDX", B, A, f"00A0{i:02X}:{BASE}", label=f"{cell} (ext)") for i in (1, 2)]
    arms = [P.arm("baseline", [BASE, A, B], kind="baseline"), P.arm("plus-a", [BASE, A]), P.arm("plus-b", [BASE, B])]
    res = {(NAVOWN, "baseline"): rows, (NAVOWN, "plus-a"): [], (NAVOWN, "plus-b"): []}
    cells = [{"cell": cell, "kind": "exterior", "worldspace": "Tamriel", "worldspaceKey": f"00003C:{BASE}", "x": -12,
              "y": 15, "refs": 0, "navmesh": 1}]
    plan, md, _ = P.compile_and_render(arms, res, [A, B], cells=cells)
    line = next((ln for ln in md.splitlines() if ln.startswith("- **") and "navmesh" in ln.lower()), "")
    ok = "Tamriel" in line and "grid cell -12, 15" in line
    return ok, f"item {line[-300:]!r}"


def c1788_route_lead_cell_by_name_or_grid():
    """M2 (Ice: route-cut leads "in 003916:Skyrim.esm"): a lead's cell is said by its in-game name where the names
    enrichment has one, else by the worldspace and grid of the mod's footprint, never by a bare form key."""
    doc = {"Probe": "blockprobe", "Targets": [A], "Rows": [
        P.route_row("Blocked", 1, "HallA", 32), P.route_row("LikelyBlocked", 2, f"003916:{BASE}", 160, "about half the time")]}
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("dependency-only", [BASE, OTHER])]
    res = {(CONTACT, "baseline"): [P.row("CONTACT", OTHER, A, f"000801:{A}")], (CONTACT, "dependency-only"): []}
    cells = [{"cell": f"003916:{BASE}", "kind": "exterior", "worldspace": "Tamriel", "x": 27, "y": 35, "refs": 7}]
    names = {"places": {"HallA": {"name": "The Great Hall", "formKey": f"00A011:{BASE}", "from": A}}}
    plan, md, _, _ = P.compile_and_render(arms, res, [A], want_index=True, missing=[(P.ROUTE_CUT, "baseline", "completed", None)],
                                          route_rows={"baseline": (doc, None)}, cells=cells, names=names)
    lead = next((ln for ln in md.splitlines() if "Leads for an in-game check" in ln), "")
    ok = ("The Great Hall" in lead and "Tamriel, grid cell 27, 35" in lead
          and not re.search(r"in 003916:Skyrim\.esm\b(?! \(ext\) in)", lead) and "in HallA" not in lead)
    return ok, f"lead {lead[-400:]!r}"


def c1789_forward_where_the_winner_only_carries():
    """M3 (Helgi: 24 of 27 fields; "keeping both mods' changes needs a patch that carries both"): where the winner only
    carries an earlier value, the next step says to forward the losing mod's value; "carries both" only for the fields
    the winner changed itself."""
    d1, d2, d3 = f"00C722:{BASE}", f"00C723:{BASE}", f"00C724:{BASE}"
    chains = [(d1, "MagicEffect", [(BASE, {"Description": "v1"}), (OTHER, {"Description": "o1"}), (A, {"Description": "a1"})]),
              (d2, "MagicEffect", [(BASE, {"Description": "v2"}), (OTHER, {"Description": "o2"}), (A, {"Description": "v2"})]),
              (d3, "MagicEffect", [(BASE, {"Description": "v3"}), (OTHER, {"Description": "o3"}), (A, {"Description": "v3"})])]
    p_rows = [P.lost_row(OTHER, A, d1, "Description", "o1", "a1", "v1", before=BASE),
              P.lost_row(OTHER, A, d2, "Description", "o2", "v2", "v2", before=BASE),
              P.lost_row(OTHER, A, d3, "Description", "o3", "v3", "v3", before=BASE)]
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("without-a", [BASE, OTHER])]
    enr = {(RECORDS, "baseline"): p_rows, (RECORDS, "without-a"): []}
    plan, md, _ = P.compile_and_render(arms, {(CONTACT, a_): [] for a_ in ("baseline", "without-a")}, [A],
                                       count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr,
                                       transcripts={(RECORDS, "baseline"): P.records_transcript(chains)},
                                       masters={OTHER: [BASE], A: [BASE]})
    todo = " ".join(pred_of(plan).get("whatToDo") or [])
    parts = todo.split("; ")
    ok = (any(re.search(r"\b2 of them\b.*\bforwards OtherMod\.esp's value", x) for x in parts)
          and any(re.search(r"\bon 1\b.*\bcarries both", x) for x in parts)
          and not any(re.search(r"\b3\b.*carries both", x) for x in parts))
    return ok, f"what to do {todo!r}"


def placed_overlap_bundle(details, before):
    """C934's fixture (test_evalreport_prediction): two overlap pairs on X that the patch places, each with its
    triangle pairs before (its row without the patch) and after (its re-measure with the patch)."""
    X = f"000X34:{A}"
    ys = [f"000Y3{i}:{B}" for i in (1, 2)]
    p_rows = [P.row("OVERLAP", B, A, X, other=y) for y in ys]
    for i_, r_ in enumerate(p_rows):
        r_["Necessary"][0]["PhysicalDetail"] = before[i_]
    preds = [{"Kind": "OVERLAP", "A": B, "B": A, "FormKey": X, "Other": y, "Physical": "Holds", "Detail": details[i_]}
             for i_, y in enumerate(ys)]
    other_ = f"00C93A:{BASE}"
    enr = {(RECORDS, "baseline"): [], (RECORDS, "with-patch"): [P.lost_row(OTHER, PATCH, other_, "Owner", "o", "p", "v")]}
    tr = {(RECORDS, "with-patch"): P.tree_transcript([
        (X, "PlacedObject", [(A, {"Placement.Position": "1,1,1"}), (PATCH, {"Placement.Position": "9,9,9"})]),
        (other_, "PlacedObject", [(BASE, {"Owner": "v"}), (OTHER, {"Owner": "o"}), (PATCH, {"Owner": "p"})])])}
    return P.compile_and_render(P.PATCH_ADDED_ARMS, {(OVERLAP, "baseline"): p_rows, (OVERLAP, "with-patch"): []},
                                [A, B], patches=[PATCH], predicates={(OVERLAP, "with-patch"): preds},
                                enrichment=enr, count_only={k: max(1, len(v)) for k, v in enr.items()}, transcripts=tr)


def c1790_worse_placements_said():
    """M4 (WRW: 22 of 48 patch-placed pairs cross more with the patch, hidden in a falling total): the line says how
    many of the pairs the patch places cross more with it than without, with the largest rise."""
    plan, md, _ = placed_overlap_bundle(
        details=["crossing; 108 triangle pairs [mm]", "crossing; 10 triangle pairs [mm]"],
        before=["actual render surfaces: crossing; 1 triangle pairs [mm]",
                "actual render surfaces: crossing; 150 triangle pairs [mm]"])
    line = next((ln for ln in md.splitlines() if "where a patch places one of the two objects" in ln), "")
    ok = re.search(r"\b1 of (?:them|the 2)\b[^.]*\bmore\b", line) is not None and "1 to 108" in line
    return ok, f"line {line[-360:]!r}"


# ----------------------------------------------------------------------------------- item 18's small defects

def c1793_one_object_is_left_floating():
    """O1 (item 18: "1 object are left floating"): the reader's page agrees the verb with a count of one."""
    import test_readerdoc as TD
    p = TD.plan()
    it = p["reader"]["prediction"]["items"][0]
    keep = TD.contact_row("000801:ModA.esp", "PlaceOne", "Static", "Banner01", TD.A)
    p["findings"][0]["allRows"] = [keep]
    p["reader"]["prediction"]["record"] = [e for e in p["reader"]["prediction"]["record"]
                                           if e["checkId"] != TD.CONTACT] + [
        {"checkId": TD.CONTACT, "kind": "CONTACT", "A": TD.B, "B": TD.A, "formKey": keep["formKey"],
         "summary": keep["summary"], "admission": "admitted", "attribution": "needs-both", "attributionMod": None}]
    it.update(count=1, relationships=1, objects=1)
    p["reader"]["validation"]["byItem"][0] = {"hits": 1, "open": 0, "notMeasured": 0}
    p["reader"]["validation"]["hits"] = 1
    md = TD.build(p)["markdown"] if isinstance(TD.build(p), dict) else TD.build(p)[0]
    hit = re.findall(r"[^.]*\b1 object (?:is|are) left floating[^.]*", md)
    ok = bool(hit) and all("1 object is left floating" in h for h in hit)
    return ok, f"lines {hit}"


def p3_two_check_bundle():
    """test_reader_rules' p3 shape with the reversed order derived for two record checks on the measured setup."""
    m = {OTHER: [BASE], A: [BASE], B: [BASE]}
    rows = [P.lost_row(A, B, f"000801:{BASE}", "Name", "a1", "b1", "v1")]
    cell_rows = [P.lost_row(A, B, f"000901:{BASE}", "WaterHeight", "1", "2", "0")]
    rev = [P.lost_row(B, A, f"000802:{BASE}", "Model", "b2", "a2", "v2")]
    rev_cell = [P.lost_row(B, A, f"000902:{BASE}", "LightingTemplate", "b3", "a3", "v3")]
    enr = {(c_, a_): (rows if c_ == RECORDS else cell_rows) if a_ == "baseline" else []
           for c_ in (RECORDS, CELLATTR) for a_ in ("baseline", "dependency-only", "plus-a", "plus-b")}
    res = {(CONTACT, a_): [] for a_ in ("baseline", "dependency-only", "plus-a", "plus-b")}
    return P.compile_and_render(P.four_arms(), res, [A, B], want_index=True,
                                count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr, masters=m,
                                plugin_flags={A: {"MasterFlag": False}, B: {"MasterFlag": False}},
                                order_variant={(RECORDS, "baseline"): ([BASE, OTHER, B, A], rev),
                                               (CELLATTR, "baseline"): ([BASE, OTHER, B, A], rev_cell)})


def c1794_change_hands_names_every_file():
    """O2 (item 18: changeHands.rowsPath names only the first of the variant's files): the plan and the evidence index
    name every reversed-order file the change-hands count was drawn from."""
    plan, md, html, index = p3_two_check_bundle()
    ch = pred_of(plan).get("loadOrder", {}).get("changeHands") or {}
    paths = list(ch.get("rowsPaths") or ([ch["rowsPath"]] if ch.get("rowsPath") else []))
    want = [f"enrichment/order-variant/{c_}__baseline.json" for c_ in (CELLATTR, RECORDS)]
    sec = P.index_section(index, "load-order-pair")
    ok = ch.get("fields") == 4 and all(any(w in str(p_) for p_ in paths) for w in want) and all(w in sec for w in want)
    return ok, f"fields {ch.get('fields')}; paths {paths}; in the index's load-order section {[w in sec for w in want]}"


def c1795_variant_values_never_shown():
    """O3 (MOOT, a guard): no reader surface shows an order-variant row's values, so its "(the winning value)" (the
    measured order's winner) is never read as the reversed order's. The variant files are read for keys and counts
    only (evalreport._pair_lost_keys)."""
    plan, md, html, index = p3_two_check_bundle()
    seen = [n for n, t in (("report.md", md), ("report.html", html), ("evidence-index.md", index))
            if "(the winning value)" in t or "a2" in re.findall(r'"(a2)"', t)]
    # the variant's own values ("b2", "a2") are absent from every page
    leak = [n for n, t in (("report.md", md), ("report.html", html), ("evidence-index.md", index))
            if re.search(r'["\s]a2["\s;]', t) or re.search(r'["\s]b2["\s;]', t)]
    ok = not seen and not leak
    return ok, f"winning-value wording on {seen}; variant values on {leak}"


def c1798_variant_file_says_what_its_values_are_against():
    """O3 / D7: the order-variant file itself (linked from the evidence index) says its values are differences from
    the measured order's winner, so its "(the winning value)" is not read as the reversed order's winner."""
    import bundleenrich
    import test_reader_rules as RR
    with tempfile.TemporaryDirectory() as td:
        RR.rows_bundle(Path(td), [BASE], recorded={})
        root = Path(td) / "bundle"
        (root / "plan.json").write_text(json.dumps({"targets": [{"name": "P.esp"}, {"name": "W.esp"}]}), encoding="utf-8")
        bundleenrich.enrich_order_variant(root)
        doc = json.loads((root / "enrichment" / "order-variant" / f"{RECORDS}__baseline.json").read_text(encoding="utf-8"))
    said = str((doc.get("order") or {}).get("values") or "")
    ok = "measured order" in said and "(the winning value)" in said
    return ok, f"order block {doc.get('order')}"


def c1796_free_order_sentence_speaks_to_the_modder():
    """O4 (item 18: "Neither order is ranked here." speaks about the report): the sentence says why neither order is
    named, the choice depending on which changes matter, not on how many there are."""
    plan, md, html, index = p3_two_check_bundle()
    s = str(pred_of(plan).get("loadOrder", {}).get("sentence") or "")
    ok = bool(s) and "ranked here" not in s and re.search(r"\bdepends on\b", s) is not None and s in md
    return ok, f"sentence {s!r}"


CASES = [
    ("C1780-reel-one-case-singular", c1780_reel_one_case_singular),
    ("C1781-one-reason-per-heading", c1781_one_reason_per_heading_and_the_same_reason_everywhere),
    ("C1782-offline-render-caption", c1782_offline_render_caption_says_render),
    ("C1783-location-order-share", c1783_location_order_says_its_share_of_the_load_order),
    ("C1784-rebuild-generated-output", c1784_rebuild_generated_output_in_what_to_do),
    ("C1785-pdf-links-relative", c1785_pdf_links_are_relative),
    ("C1786-place-by-name", c1786_place_named_by_its_in_game_name),
    ("C1787-exterior-cell-grid", c1787_exterior_cell_by_world_and_grid),
    ("C1788-route-lead-cell", c1788_route_lead_cell_by_name_or_grid),
    ("C1789-forward-not-carry-both", c1789_forward_where_the_winner_only_carries),
    ("C1790-worse-placements", c1790_worse_placements_said),
    ("C1791-patch-line-own-pair", c1791_reel_patch_line_keyed_to_its_own_pair),
    ("C1792-closing-branch-guard", c1792_closing_without_setup_branch_unchanged),
    ("C1793-one-object-is", c1793_one_object_is_left_floating),
    ("C1794-change-hands-files", c1794_change_hands_names_every_file),
    ("C1795-variant-values-guard", c1795_variant_values_never_shown),
    ("C1796-free-order-sentence", c1796_free_order_sentence_speaks_to_the_modder),
    ("C1798-variant-file-tag", c1798_variant_file_says_what_its_values_are_against),
]


def main(argv=None) -> int:
    want = list(argv if argv is not None else sys.argv[1:])
    fails = 0
    for name, fn in CASES:
        if want and not any(name.startswith(w) for w in want):
            continue
        try:
            ok, detail = fn()
        except Exception:
            ok, detail = False, "CRASH " + traceback.format_exc(limit=4).replace("\n", " | ")
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
        fails += 0 if ok else 1
    print(f"{fails} failure(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
