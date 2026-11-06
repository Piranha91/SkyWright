#!/usr/bin/env python
"""Known answers for orderleads: a whole-order pass's rows as leads, queried by added mod, place and pair.

A synthetic pass folder in the real file shapes (ordermap's map, the probes' conflict files, blockprobe's route rows, the
record stage's rows) carries one planted row per behaviour. Each case checks one behaviour and names it. Cases run one
by one; a case prints PASS, FAIL (its assertion) or ERROR (anything else), and the run exits 1 if any case is not PASS.

ORDERLEADS_IMPL=<file> runs the cases against another module with the same interface (used to show them red against
the pilot's code, and by the sabotage arms).
"""
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMPL = Path(os.environ.get("ORDERLEADS_IMPL") or ROOT / "_geometry-harness/orderleads.py")


def load_impl():
    spec = importlib.util.spec_from_file_location("orderleads_under_test", IMPL)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


BASE = "Base.esm"
CELL1, CELL1X, CELL2, CELL3, EXT = "000801:Base.esm", "000802:Base.esm", "000803:Base.esm", "000804:Base.esm", "000A01:Base.esm"


def plugin(name, folder, bytes_=900, new=3, over=4, base=False, masters=None):
    return {"name": name, "folder": folder, "bytes": bytes_, "baseGame": base, "error": None, "masters": masters or ([] if base else [BASE]),
            "newRecords": new, "overrides": over, "interior": 1, "exterior": 0, "scriptCarriers": 0, "exteriorCells": []}


def conflict(kind, a, b, summary, fk, label, alt=None):
    c = {"Kind": kind, "A": a, "B": b, "Summary": summary, "Necessary": [{"FormKey": fk, "Role": "placed-ref", "Label": label}]}
    if alt:
        c["Alternative"] = [{"FormKey": alt, "Role": "placed-ref"}]
    return c


def lost(rtype, fk, field, loser, winner, lost_v="1", win_v="2"):
    return {"Kind": "LOST-EDIT", "A": loser, "B": winner, "Field": field,
            "Summary": f"{rtype} {fk}: {field} - {loser}'s value lost to {winner}'s",
            "Necessary": [{"FormKey": fk, "Label": None, "Values": {"lost": lost_v, "winner": win_v, "overridden": None}}]}


def write_pass(d, added=None, sizes=True, stages_fail=("seam",)):
    d.mkdir(parents=True, exist_ok=True)
    plugins = [plugin(BASE, None, 5000, 100, 0, base=True),
               plugin("ModA.esp", "FolderA"), plugin("ModB.esp", "FolderB"), plugin("ModC.esp", "FolderC"),
               plugin("ModD.esp", "FolderD"), plugin("ModE.esp", "FolderE"), plugin("ModF.esp", "FolderF"), plugin("ModG.esp", "FolderG"),
               plugin("Dummy.esp", "FolderDummy", 79, 0, 0), plugin("DummyRec.esp", "FolderDummyRec", 79, 1, 0),
               plugin("Empty300.esp", "FolderEmpty", 300, 0, 0)]
    if not sizes:
        for p in plugins:
            p.pop("bytes")
    cells = {CELL1: {"edid": "CellOne"}, CELL1X: {"edid": "CellOneAnnex"}, CELL2: {"edid": "CellTwo"}, CELL3: {"edid": "CellThree"},
             EXT: {"edid": None, "worldspace": "World", "grid": [3, -2]}}
    edge = lambda a, b, recs=1: {"a": a, "b": b, "records": recs, "interior": 1, "exterior": 0, "neighbours": 0, "baseLinks": 0, "formLinks": 0, "recordExamples": [], "cellExamples": []}
    m = {"tool": "ordermap", "schema": 2, "source": "synthetic", "plugins": plugins,
         "edges": [edge("ModA.esp", "ModB.esp"), edge("ModA.esp", "ModC.esp"), edge("ModB.esp", "ModC.esp"), edge("ModB.esp", "ModD.esp"),
                   edge("ModC.esp", "ModD.esp"), edge("ModF.esp", "ModG.esp")],
         "folders": ["FolderA", "FolderB", "FolderC", "FolderD", "FolderE", "FolderF", "FolderG", "FolderDummy", "FolderDummyRec", "FolderEmpty"],
         "assetEdges": [{"a": "FolderA", "b": "FolderE", "identical": 0, "different": 4, "unknown": 0, "winsA": 4, "winsB": 0,
                         "differentByTop": {"meshes": 2, "textures": 2}, "examples": ["meshes\\x.nif (wins: FolderA)"]},
                        {"a": "FolderB", "b": "FolderE", "identical": 3, "different": 0, "unknown": 0, "winsA": 0, "winsB": 0, "differentByTop": {}, "examples": []}],
         "runtimeLayers": {"FolderE": {"SPID": 1}}, "archivesUnreadable": [], "archives": 0, "assetPaths": 10, "assetPathsShared": 2,
         "hashed": True, "hashedBytes": 1, "cells": cells}
    (d / "map.json").write_text(json.dumps(m), encoding="utf-8")
    ov = [conflict("OVERLAP", "ModA.esp", "ModC.esp", "Static from ModA.esp / Static from ModC.esp: crossing, 12 triangle pairs in CellOne (int) 000901:ModA.esp / 000902:ModC.esp",
                   "000901:ModA.esp", "CellOne (int)", "000902:ModC.esp"),
          conflict("OVERLAP", "ModB.esp", "ModC.esp", "Static from ModB.esp / Static from ModC.esp: crossing, 5 triangle pairs in CellTwo (int) 000903:ModB.esp / 000904:ModC.esp",
                   "000903:ModB.esp", "CellTwo (int)", "000904:ModC.esp"),
          conflict("OVERLAP", "ModA.esp", "ModB.esp", "Static from ModA.esp / Static from ModB.esp: crossing, 7 triangle pairs in CellOneAnnex (int) 000905:ModA.esp / 000906:ModB.esp",
                   "000905:ModA.esp", "CellOneAnnex (int)", "000906:ModB.esp")]
    (d / "overlap.json").write_text(json.dumps({"Conflicts": ov, "Coverage": []}), encoding="utf-8")
    seam = [{"Kind": "LAND-SEAM", "A": "ModB.esp", "B": "ModD.esp", "Summary": "World (3,-2) N World (3,-1): 24 u over 1 vertices",
             "Necessary": [{"FormKey": EXT, "Role": "cell", "Label": "World (3,-2)"}], "Alternative": [{"FormKey": "000A02:Base.esm", "Role": "cell", "Label": "World (3,-1)"}]}]
    (d / "seam.json").write_text(json.dumps({"Conflicts": seam, "Coverage": []}), encoding="utf-8")
    route = {"Rows": [{"Verdict": "Blocked", "FormKey": "000907:ModE.esp", "Cell": "CellThree", "Mesh": "rubble.nif", "By": ["ModE.esp"], "Why": "cuts the path line"}],
             "Tally": {"Blocked": 1}}
    (d / "route.json").write_text(json.dumps(route), encoding="utf-8")
    rows = {"records.cell-attributes": [lost("Cell", CELL1, "LightingTemplate", "ModB.esp", "ModA.esp"),
                                        lost("Cell", EXT, "Water", "ModD.esp", "ModB.esp")],
            "records.contested-fields": [lost("PlacedObject", "000911:Base.esm", "MajorFlags", "ModC.esp", "ModD.esp", "0", "262144"),
                                         lost("PlacedObject", "000912:Base.esm", "SkyrimMajorRecordFlags", "ModC.esp", "ModD.esp", "0", "Compressed"),
                                         lost("PlacedObject", "000913:Base.esm", "SkyrimMajorRecordFlags", "ModB.esp", "ModD.esp", "0", "Compressed, InitiallyDisabled"),
                                         lost("PlacedObject", "000914:Base.esm", "Position", "ModE.esp", BASE),
                                         {"Kind": "DANGLING-REFERENCE", "A": "ModE.esp", "B": None, "Summary": "ModE.esp: 000915:ModE.esp points at a missing record",
                                          "Necessary": [{"FormKey": "000915:ModE.esp"}]}],
            "records.registration-and-dialogue": []}
    (d / "records.json").write_text(json.dumps({k: {"rows": v, "coverage": []} for k, v in rows.items()}), encoding="utf-8")
    stages = {s: {"seconds": 1.0, "exit": 0, "stopped": None} for s in ("map", "overlap", "route", "records")}
    for s in stages_fail:
        stages[s] = {"seconds": 1.0, "exit": 1, "stopped": None}
    (d / "stages.json").write_text(json.dumps(stages), encoding="utf-8")
    if added is not None:
        (d / "pass.json").write_text(json.dumps({"added": added}), encoding="utf-8")
    return d


def key(lead):
    return (tuple(sorted(lead["mods"])), lead["family"])


def keys(leads):
    return [key(g) for g in leads]


def place_labels(lead):
    return [str(p[0]) for p in lead["places"]]


# ---- cases -------------------------------------------------------------------------------------------------------------

def case_L1_mod(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    by_plugin = keys(idx.query(added=["ModA.esp"]).leads)
    want = {(("ModA.esp", "ModB.esp"), "records"), (("ModA.esp", "ModC.esp"), "overlap"), (("ModA.esp", "ModB.esp"), "overlap"), (("FolderA", "FolderE"), "asset")}
    assert set(by_plugin) == want, f"--added ModA.esp kept {by_plugin}, want {sorted(want)}"
    order = [k for k in keys(idx.leads) if k in want]
    assert by_plugin == order, f"the mod's leads are not in the whole list's order: {by_plugin} against {order}"
    by_folder = keys(idx.query(added=["FolderA"]).leads)
    assert by_folder == by_plugin, f"--added FolderA kept {by_folder}, not the same as ModA.esp's {by_plugin}"


def case_L2_place(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    named = idx.query(place="CellOne").leads
    assert set(keys(named)) == {(("ModA.esp", "ModB.esp"), "records"), (("ModA.esp", "ModC.esp"), "overlap")}, f"--place CellOne kept {keys(named)}"
    for g in named:
        assert all(p in ("CellOne (int)", "CellOne", CELL1) for p in place_labels(g)), f"--place CellOne lead still shows other places: {place_labels(g)}"
    by_key = keys(idx.query(place=CELL1).leads)
    assert set(by_key) == set(keys(named)), f"--place {CELL1} kept {by_key}, not the same as by name"
    prefix = set(keys(idx.query(place_prefix="CellOne").leads))
    assert (("ModA.esp", "ModB.esp"), "overlap") in prefix and set(keys(named)) <= prefix, f"--place-prefix CellOne kept {prefix}"
    ext = set(keys(idx.query(place="World (3,-2)").leads))
    assert ext == {(("ModB.esp", "ModD.esp"), "seam"), (("ModB.esp", "ModD.esp"), "records")}, f"an exterior cell with no EditorID, by worldspace and grid, kept {ext}"


def case_L3_pair(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    got = set(keys(idx.query(pair=("ModA.esp", "ModB.esp")).leads))
    assert got == {(("ModA.esp", "ModB.esp"), "records"), (("ModA.esp", "ModB.esp"), "overlap")}, f"--pair ModA ModB kept {got}"
    folders = set(keys(idx.query(pair=("FolderA", "FolderE")).leads))
    assert folders == {(("FolderA", "FolderE"), "asset")}, f"--pair FolderA FolderE kept {folders}"


def case_L4_one_plugin(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    offered = [g for g in idx.leads if len(g["mods"]) < 2]
    assert not offered, f"leads naming fewer than two plugins: {keys(offered)}"
    reasons = idx.set_aside["rows"]
    assert any("one plugin" in r for r in reasons), f"no 'one plugin' reason counted: {reasons}"
    assert any("base game" in r for r in reasons), f"no 'base game' reason counted: {reasons}"
    route = [g for g in idx.record if g["family"] == "route"]
    assert route and all(g["tier"] == 4 for g in route), f"a one-plugin Blocked route group's tier is {[g['tier'] for g in route]}, not 4"


def case_L5_compressed(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    cd = [g for g in idx.leads if set(g["mods"]) == {"ModC.esp", "ModD.esp"} and g["family"] == "records"]
    assert not cd, "rows differing only by the compressed-storage flag are still a lead"
    reasons = idx.set_aside["rows"]
    n = sum(v for r, v in reasons.items() if "compressed" in r)
    assert n == 2, f"compressed-only rows counted {n}, want 2: {reasons}"
    kept = [g for g in idx.leads if set(g["mods"]) == {"ModB.esp", "ModD.esp"} and any("000913" in e for e in g["examples"])]
    assert kept, "the row with InitiallyDisabled as well as Compressed was masked"


def case_L6_dummy(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    assert idx.dummies == ["Dummy.esp"], f"dummies {idx.dummies}, want ['Dummy.esp']"
    assert idx.dummy_source == "map", f"dummy source {idx.dummy_source!r}, want 'map'"
    assert "Dummy.esp" not in idx.no_signal and "Empty300.esp" in idx.no_signal, f"no-signal list {idx.no_signal}"
    text = idx.render(whole=True)
    assert "Dummy.esp" in text, "the dummy has no line in the output"
    bare = M.build(write_pass(tmp / "q", sizes=False))
    assert bare.dummies == [] and bare.dummy_source == "none", f"a map without sizes and no list gave {bare.dummies}, source {bare.dummy_source!r}"
    assert "size" in bare.render(whole=True).lower(), "a map without sizes doesn't say dummies could not be told"


def case_L7_edge(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    assert not [g for g in idx.leads if g["family"] == "edge"], "a map edge with no row is offered as a lead"
    assert any("no row" in r for r in idx.set_aside["groups"]), f"edges not counted: {idx.set_aside['groups']}"


def case_L8_unassessed(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    text = idx.render(whole=True)
    assert "seam" in json.dumps(idx.unassessed.get("stages")), f"the failed seam stage isn't on the unassessed list: {idx.unassessed}"
    assert "FolderE" in text and "SPID" in text, "the runtime-layer folder isn't named in the output"
    assert idx.unassessed.get("registry"), "no registry check is named unassessed"


def case_L9_default(M, tmp):
    idx = M.build(write_pass(tmp / "p", added=["ModD.esp"]))
    assert [a.lower() for a in idx.added] == ["modd.esp"], f"pass.json's added mod not read: {idx.added}"
    first = idx.render()
    whole = idx.render(whole=True)
    assert "ModD.esp" in first and "--whole" in first, "the default output isn't the added mod's view, pointing at --whole"
    assert first != whole and "FolderA and FolderE" in whole.replace(" / ", " and "), "--whole does not give the whole-order list"
    assert "FolderA and FolderE" not in first.replace(" / ", " and "), "the added view shows a lead that doesn't name the added mod"


def case_L10_wording(M, tmp):
    text = M.build(write_pass(tmp / "p")).render(whole=True).lower()
    assert "leads to open, not verdicts" in text, "the output doesn't say its rows are leads, not verdicts"
    assert "not a joint cause" in text, "the output doesn't say a pair is where to look, not a joint cause"


def case_L11_no_hit(M, tmp):
    idx = M.build(write_pass(tmp / "p"))
    r = idx.query(pair=("ModA.esp", "ModZ.esp"))
    assert not r.leads and r.message and "ModZ.esp" in r.message and "not in" in r.message, f"pair with an absent plugin: {r.message!r}"
    r = idx.query(place="Nowhere")
    assert not r.leads and r.message and "Nowhere" in r.message, f"unknown place: {r.message!r}"
    r = idx.query(added=["Dummy.esp"])
    assert not r.leads and r.message and "placeholder" in r.message.lower(), f"a dummy asked for: {r.message!r}"


CASES = [case_L1_mod, case_L2_place, case_L3_pair, case_L4_one_plugin, case_L5_compressed, case_L6_dummy, case_L7_edge,
         case_L8_unassessed, case_L9_default, case_L10_wording, case_L11_no_hit]


def main():
    try:
        M = load_impl()
    except Exception as ex:
        print(f"ERROR import: {IMPL}: {type(ex).__name__}: {ex}")
        return 1
    bad = 0
    for case in CASES:
        name = case.__name__[len("case_"):]
        with tempfile.TemporaryDirectory() as t:
            try:
                case(M, Path(t))
                print(f"PASS {name}")
            except AssertionError as ex:
                bad += 1
                print(f"FAIL {name}: {ex}")
            except Exception as ex:
                bad += 1
                print(f"ERROR {name}: {type(ex).__name__}: {ex}")
                traceback.print_exc(limit=2, file=sys.stdout)
    print(f"orderleads: {len(CASES) - bad} of {len(CASES)} cases pass")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
