"""Tests for readerdoc.py — the reader's document rendered from a presentation plan.

Dependency-free: `python _geometry-harness/tests/test_readerdoc.py`. Case numbers are odd. Plans are built by hand
(small dicts in the presentation plan's shape) except case 15, which compiles a bundle with the real compiler through
test_evalreport_prediction's builders. READERDOC_PATH, if set, loads the module from that file instead (the sabotage
arms use it to show each case failing on its own assertion).
"""
import copy
import importlib.util
import json
import os
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS", str(Path(os.environ.get("TEMP", "/tmp")) / "hc-tests-no-eval-settings"
                                                     / "houseCARL.evaluation.json"))
_path = os.environ.get("READERDOC_PATH") or str(HERE.parent / "readerdoc.py")
_spec = importlib.util.spec_from_file_location("readerdoc_under_test", _path)
RD = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(RD)
WORDS = json.loads((HERE.parent.parent / ".claude/skills/evaluation-reporting/references/report-priorities.json")
                   .read_text(encoding="utf-8"))["readerDoc"]

A, B, OTHER, PATCH = "ModA.esp", "ModB.esp", "Other.esp", "ModA - ModB patch.esp"
CONTACT, ALIAS = "geometry.placement-contact", "functional.quest-alias-split"
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def contact_row(fk, place, kind, eid, mover):
    return {"formKey": fk, "label": f"{place} (int)",
            "summary": f"Stranded: {kind} {eid} <x.nif> Supported on Static Host01 <h.nif> which {mover} Removed; "
                       f"nearest thing now 128u"}


def plan(patch=True, by_item=True, patch_problems=0, hits=4, open_=0):
    """Needs-both contact: 4 rows over 3 objects (one object loses two supports) in two places; a single-mod alias
    item (ModA against Other, 2 rows); a not-separable item (1 row); set aside 5 base-game rows."""
    rows = [contact_row("000801:ModA.esp", "PlaceOne", "Static", "Banner01", A),
            contact_row("000801:ModA.esp", "PlaceOne", "Static", "Banner01", A).copy(),
            contact_row("000802:ModA.esp", "PlaceOne", "Furniture", "Chair01", A),
            contact_row("000803:ModA.esp", "PlaceTwo", "Light", "Candle01", B)]
    rows[1]["summary"] = rows[1]["summary"].replace("Host01", "Host02")
    alias_rows = [{"formKey": "000901:ModA.esp", "label": "PlaceThree (int)", "summary": "SPLIT a"},
                  {"formKey": "000902:ModA.esp", "label": "PlaceThree (int)", "summary": "SPLIT b"}]
    rec = ([{"checkId": CONTACT, "kind": "CONTACT", "A": B, "B": A, "formKey": r["formKey"], "summary": r["summary"],
             "admission": "admitted", "attribution": "needs-both", "attributionMod": None} for r in rows]
           + [{"checkId": ALIAS, "kind": "SPLIT", "A": OTHER, "B": A, "formKey": r["formKey"], "summary": r["summary"],
               "admission": "admitted", "attribution": "single-mod", "attributionMod": A} for r in alias_rows]
           + [{"checkId": "environment.land-seams", "kind": "LAND-SEAM", "A": OTHER, "B": B, "formKey": "000D01:ModB.esp",
               "summary": "seam", "admission": "admitted", "attribution": "not-separable", "attributionMod": None}])
    items = [{"checkId": CONTACT, "kind": "CONTACT", "mods": [A, B], "count": 4, "attribution": "needs-both",
              "attributionMod": None, "findingIds": ["F-1"], "title": "Objects left without their supporting surface"},
             {"checkId": ALIAS, "kind": "SPLIT", "mods": [A, OTHER], "count": 2, "attribution": "single-mod",
              "attributionMod": A, "findingIds": ["F-2"], "title": "Quest aliases split between mods"},
             {"checkId": "environment.land-seams", "kind": "LAND-SEAM", "mods": [B, OTHER], "count": 1,
              "attribution": "not-separable", "attributionMod": None, "findingIds": [], "title": "Seams"}]
    val = {"status": "compared" if patch else "no-patch", "patches": [PATCH] if patch else [],
           "hits": hits if patch else None, "open": (open_ + 2) if patch else None,
           "patchProblems": patch_problems if patch else None}
    if by_item:
        val["byItem"] = ([{"hits": hits, "open": open_, "notMeasured": 0}, {"hits": 0, "open": 2, "notMeasured": 0},
                          {"hits": 0, "open": 0, "notMeasured": 1}] if patch else [])
    return {"findings": [{"id": "F-1", "allRows": rows}, {"id": "F-2", "allRows": alias_rows}],
            "reader": {"prediction": {"mods": [A, B], "items": items, "record": rec,
                                      "setAside": {"baseGame": 5, "sameMod": 0}},
                       "validation": val,
                       "coverage": {"lines": [{"subject": "Missing meshes and textures (for the measured load order; "
                                                          "the tested alternative setup); Objects occupying the same space"}]}}}


def build(p, names=None, order=None, single="show", mod_names=None):
    return RD.build(p, WORDS, mod_names or {A: "Mod A", B: "Mod B"}, names, order, single, "evidence-index.html")


def section(md, heading):
    out, on = [], False
    for ln in md.splitlines():
        if ln.startswith("## "):
            on = ln[3:].strip() == heading
            continue
        if on:
            out.append(ln)
    return "\n".join(out)


H = WORDS["headings"]


def case_1_admission_lists_only_needs_both():
    md, _, man = build(plan())
    probs = section(md, H["problems"])
    assert "Mod A" in probs and "Mod B" in probs, probs
    assert "quest" not in probs.lower() and "Other" not in probs, f"a non-pair item reached the problem list: {probs}"
    assert [x["checkId"] for x in man["admitted"]] == [CONTACT], man["admitted"]


def case_3_single_mod_item_under_its_own_heading_or_aside():
    md, _, man = build(plan())
    assert "Other" in section(md, H["alone"]), section(md, H["alone"])
    md2, _, man2 = build(plan(), single="aside")
    assert H["alone"] not in md2, "aside mode still printed the single-mod heading"
    assert man2["setAsideCounted"] == man["setAsideCounted"] + 2, (man["setAsideCounted"], man2["setAsideCounted"])


def case_5_objects_are_distinct_subjects_not_rows():
    md, _, man = build(plan())
    a = man["admitted"][0]
    assert (a["rows"], a["distinct"]) == (4, 3), a
    assert "3 objects" in section(md, H["problems"]), section(md, H["problems"])


def case_7_install_all_only_when_every_needs_both_case_is_fixed():
    md, s, _ = build(plan(hits=4, open_=0))
    assert WORDS["install"]["all"] in s, s
    md, s, _ = build(plan(hits=3, open_=1))
    assert WORDS["install"]["all"] not in s and "3 of 4" in s, s


def case_9_patch_problems_withhold_the_recommendation():
    md, s, _ = build(plan(patch_problems=2))
    assert WORDS["install"]["all"] not in s and "2 problems of its own" in s, s


def case_11_no_patch_and_old_plan():
    md, s, _ = build(plan(patch=False))
    assert WORDS["install"]["noPatch"] in s and H["patch"] not in md, s
    try:
        build(plan(by_item=False))
    except RD.Refused as ex:
        assert "byItem" in str(ex), ex
    else:
        raise AssertionError("a plan without validation.byItem was not refused")


def case_13_names_used_and_missing_ones_listed():
    names = {"places": {"PlaceOne": "First Hall"}, "objects": {"Chair01": "Chair"}}
    md, _, man = build(plan(), names=names)
    probs = section(md, H["problems"])
    assert "First Hall" in probs and "chair" in probs, probs
    assert man["namesMissing"]["places"] == ["PlaceThree", "PlaceTwo"], man["namesMissing"]
    assert set(man["namesMissing"]["objects"]) == {"Banner01", "Candle01"}, man["namesMissing"]


def case_15_real_compiler_plan_renders_hits_and_open():
    sys.path.insert(0, str(HERE))
    import test_evalreport_prediction as T  # noqa: E402
    p, *_ = T.patched_with_sibling("same")
    md, s, man = RD.build(p, WORDS, {}, None, None, "show", None)
    assert man["admitted"] and man["admitted"][0]["byItem"] == {"hits": 2, "open": 1, "notMeasured": 0}, man["admitted"]
    assert "1 problem of its own" in s, s


def case_17_order_sentences():
    od = WORDS["order"]
    md, _, _ = build(plan(), order={"forced": True, "first": A, "rule": "ESM flag"})
    assert od["forced"].split("{")[0] in md, md
    md, _, _ = build(plan(), order={"forced": True, "first": A, "rule": "master dependency"})
    assert od["forcedMaster"].split("{")[0] in md, md
    md, _, _ = build(plan(), order={"forced": False, "first": B, "rule": "free"})
    assert od["free"].split("(")[0] in md, md
    md, _, _ = build(plan())
    assert od["unknown"] in md, md


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
