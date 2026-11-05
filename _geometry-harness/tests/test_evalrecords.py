"""Known answers for evalrecords.py's selection rules (the record capture for the registry's mcp-only checks).

Dependency-free: `python _geometry-harness/tests/test_evalrecords.py`. EVALRECORDS_PATH, if set, loads the module
from that file instead (the sabotage arms use it). Rows are synthetic, in the summary-row shape the record reader's
summary exports carry (formid, type, parent cell, override_depth).

Selection per arm: A = every record the arm's evaluated plugins touch. A record is contested when its override depth is
above one; a row without a depth is unjudged, never uncontested. test_records_keep_work.py drives the whole capture.
"""
import importlib.util
import json
import os
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
_path = os.environ.get("EVALRECORDS_PATH") or str(HERE.parent / "evalrecords.py")
ER, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("evalrecords_under_test", _path)
    ER = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(ER)
except (FileNotFoundError, ImportError, OSError) as _ex:
    ER, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        if ER is None:
            raise AssertionError(f"evalrecords.py is not loadable: {LOAD_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def row(fid, rtype, cell, depth=1):
    return {"formid": fid, "type": rtype, "parentCell": cell, "override_depth": depth}


CELLS = {"C1", "C2"}
TYPES = {"REFR", "ACHR", "NAVM", "LAND"}


def case_3_empty_touched_with_subject_loaded_refused():
    try:
        ER.touched(["Base.esm", "Subj.esp"], ["Subj.esp"], [])
    except ER.Refusal as r:
        assert "Subj.esp" in str(r), r
        return
    raise AssertionError("an empty touched enumeration while the subject loads must refuse")


def case_5_empty_touched_without_subject_is_empty():
    try:
        got = ER.touched(["Base.esm"], ["Subj.esp"], [])
    except ER.Refusal as ex:
        raise AssertionError(f"a view without the subject is an honest empty view, refused: {ex}")
    assert got == set(), f"a view without the subject touches nothing, got {got}"


def case_7_contested_by_depth_and_unjudged_kept_apart():
    rows = [row("a", "REFR", "C1", 3), row("b", "REFR", "C1", 1), {"formid": "c", "type": "REFR", "parentCell": "C1"}]
    contested, uncontested, unjudged = ER.classify_depth(rows)
    assert (contested, uncontested, unjudged) == ({"a"}, {"b"}, {"c"}), (contested, uncontested, unjudged)


def case_9_short_export_refused():
    try:
        ER.complete_rows({"requested": 5, "rows": [row("a", "REFR", "C1")] * 4})
    except ER.Refusal as r:
        assert "4 of 5" in str(r), r
        return
    raise AssertionError("an export returning fewer rows than it selected must refuse")


def case_11_lost_edit_rows_carry_the_record_lane_value_shape():
    plugins = ["Base.esm", "PA.esp", "Subj.esp"]
    authored = ["Placement.Position=10,20,30 (Base.esm 1,2,3)", "Placement.Rotation=0,0,1 (Base.esm 0,0,0)"]
    vs_winner = ["Placement.Position=10,20,30 (Subj.esp 7,8,9)", "Placement.Rotation=0,0,1 (Subj.esp 0,0,2)"]
    rows, judged, _ = ER.lost_edits("PlacedObject", "000001:Base.esm", "PA.esp", authored, vs_winner, "Subj.esp",
                                    plugins, ER.FieldScope())
    assert judged == 2 and len(rows) == 1, (judged, rows)
    v = rows[0]["Necessary"][0]["Values"]
    assert v == {"lost": "Position=10,20,30; Rotation=0,0,1", "winner": "Position=7,8,9; Rotation=0,0,2",
                 "overridden": "Position=1,2,3; Rotation=0,0,0"}, f"the report reads Values.lost/winner, got {v}"


def case_13_a_defining_plugin_authored_every_field_it_holds():
    plugins = ["Subj.esp", "Key.esp"]
    node = ["LocationReference: ABSENT here (Key.esp has 0143B9:Base.esm)",
            "Placement.Position=1,2,3 (Key.esp 9,9,9)", "Placement.Rotation=0,0,1 (Key.esp 0,0,2)"]
    rows, judged, _ = ER.lost_edits("PlacedObject", "000899:Subj.esp", "Subj.esp", [], node, "Key.esp", plugins,
                                    ER.FieldScope(), defines=True)
    assert [r["Field"] for r in rows] == ["Placement"], f"the definer's moved object is a lost edit, got {rows}"
    v = rows[0]["Necessary"][0]["Values"]
    assert v["lost"] == "Position=1,2,3; Rotation=0,0,1" and v["winner"] == "Position=9,9,9; Rotation=0,0,2", v
    assert judged == 2, f"its two held fields are judged; the field it never had is not, got {judged}"


def case_41_only_in_reads_as_this_side_lacking_the_leaf():
    # lines from a real capture: FieldsDiff's wording, the reference holds the leaf
    plugins = ["City Walls Patch.esp", "Restored.esp", "PA.esp"]
    p = ER.parse_delta("EnableParent.Reference only in City Walls Patch.esp: 000014:Skyrim.esm", plugins)
    assert p == ("EnableParent.Reference", None, plugins[0], "000014:Skyrim.esm", "own-absent"), p
    q = ER.parse_delta("Landscape.Flags only in Restored.esp: 31", plugins)
    assert q == ("Landscape.Flags", None, plugins[1], "31", "own-absent"), q
    assert ER.parse_delta("Odd line only in Nowhere.esp: 1", plugins) is None, "an unknown plugin stays unread"


def case_42_an_enable_parent_a_later_mod_drops_is_lost_from_the_adders_side():
    plugins = ["Base.esm", "Adder.esp", "Dropper.esp"]
    authored = ["EnableParent.Reference=000014:Skyrim.esm (Base.esm has no EnableParent.Reference)"]
    vs_winner = ["EnableParent.Reference=000014:Skyrim.esm (Dropper.esp has no EnableParent.Reference)"]
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Base.esm", "Adder.esp", authored, vs_winner, "Dropper.esp", plugins, ER.FieldScope())
    assert [r["Field"] for r in rows] == ["EnableParent"] and judged == 1 and not unj, (rows, judged, unj)


def case_43_a_plugin_that_never_saw_a_leaf_did_not_remove_it():
    plugins = ["Base.esm", "Other.esp", "Late.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Other.esp: 000014:Skyrim.esm"]
    vs_winner = ["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"]
    prov = {"base.esm": (["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"], True), "other.esp": (["EnableParent.Reference=000014:Skyrim.esm (Win.esp 000015:Skyrim.esm)"], True)}
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Base.esm", "Late.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"base.esm"}, providers=prov)
    assert rows == [] and judged == 0 and not unj, (rows, judged, unj)


def case_44_a_removal_of_a_masters_leaf_is_an_edit_and_can_be_lost():
    plugins = ["Base.esm", "Remover.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Base.esm: 000014:Skyrim.esm"]
    vs_winner = ["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"]
    prov = {"base.esm": ([], True)}
    rows, judged, _ = ER.lost_edits("PlacedObject", "000001:Base.esm", "Remover.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"base.esm"}, providers=prov)
    assert [r["Field"] for r in rows] == ["EnableParent"] and judged == 1, (rows, judged)
    assert rows[0]["Necessary"][0]["Values"]["lost"] == "Reference=(absent)", rows[0]["Necessary"][0]["Values"]
    rows, _, _ = ER.lost_edits("PlacedObject", "000001:Base.esm", "Remover.esp", authored, [], "Win.esp", plugins, ER.FieldScope(), masters={"base.esm"})
    assert rows == [], "the winner also lacks it, so the removal stands"


def case_45_a_cells_landscape_is_a_child_record_not_a_lost_edit():
    plugins = ["Restored.esp", "Win.esp", "Base.esm"]
    authored = ["Landscape.Flags=31 (Base.esm has no Landscape.Flags)"]
    vs_winner = ["Landscape.Flags=31 (Win.esp has no Landscape.Flags)", "Landscape.VertexHeightMap only in Win.esp: [LandscapeVertexHeightMap]"]
    rows, judged, unj = ER.lost_edits("Cell", "000D44:Dawnguard.esm", "Restored.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"base.esm"})
    assert rows == [] and judged == 0 and not unj, (rows, judged, unj)


def case_46_a_reorder_is_read_and_is_a_clash_only_where_the_game_reads_order():
    plugins = ["Light Two.esp", "Mine.esp", "Base.esm"]
    regions = ["Regions: same 3 item(s), ORDER DIFFERS from Light Two.esp"]
    p = ER.parse_delta(regions[0], plugins)
    assert p == ("Regions", None, "Light Two.esp", None, "order"), p
    rows, judged, unj = ER.lost_edits("Cell", "000001:Base.esm", "Mine.esp", regions, regions, "Light Two.esp", plugins, ER.FieldScope())
    assert rows == [] and judged == 0 and not unj, (rows, judged, unj)
    edge = ["Data.EdgeLinks: same 4 item(s), ORDER DIFFERS from Light Two.esp"]
    rows, judged, unj = ER.lost_edits("NavigationMesh", "000001:Base.esm", "Mine.esp", edge, edge, "Light Two.esp", plugins, ER.FieldScope())
    assert rows == [] and unj == {"a list whose order the game reads, reordered": 1}, (rows, unj)


def case_48_a_masters_leaf_removed_with_a_non_master_between_is_an_edit():
    plugins = ["Skyrim.esm", "Light.esp", "Late.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Light.esp: 000014:Skyrim.esm"]
    vs_winner = ["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"]
    prov = {"skyrim.esm": ([], True), "light.esp": ([], True), "late.esp": (vs_winner, True), "win.esp": ([], True)}
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Skyrim.esm", "Late.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"skyrim.esm"}, providers=prov)
    assert [r["Field"] for r in rows] == ["EnableParent"] and judged == 1 and not unj, (rows, judged, unj)


def case_49_a_master_that_lacks_the_leaf_means_no_removal():
    plugins = ["Skyrim.esm", "Light.esp", "Late.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Light.esp: 000014:Skyrim.esm"]
    vs_winner = ["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"]
    prov = {"skyrim.esm": (["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"], True), "light.esp": ([], True)}
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Skyrim.esm", "Late.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"skyrim.esm"}, providers=prov)
    assert rows == [] and judged == 0 and not unj, (rows, judged, unj)


def case_50_a_master_that_cannot_be_read_is_counted_by_name():
    plugins = ["Skyrim.esm", "Light.esp", "Late.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Light.esp: 000014:Skyrim.esm"]
    vs_winner = ["EnableParent.Reference only in Win.esp: 000014:Skyrim.esm"]
    prov = {"skyrim.esm": ([], False), "light.esp": ([], True)}
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Skyrim.esm", "Late.esp", authored, vs_winner, "Win.esp", plugins, ER.FieldScope(), masters={"skyrim.esm"}, providers=prov)
    assert rows == [] and unj == {"a removal whose master's copy could not be settled": 1}, (rows, unj)


def case_51_a_winner_that_also_lacks_the_leaf_leaves_nothing_to_settle():
    plugins = ["Skyrim.esm", "Light.esp", "Late.esp", "Win.esp"]
    authored = ["EnableParent.Reference only in Light.esp: 000014:Skyrim.esm"]
    rows, judged, unj = ER.lost_edits("PlacedObject", "000001:Skyrim.esm", "Late.esp", authored, [], "Win.esp", plugins, ER.FieldScope(), masters={"skyrim.esm"}, providers={"skyrim.esm": ([], False)})
    assert rows == [] and judged == 0 and not unj, (rows, judged, unj)


def case_52_the_same_list_name_is_decided_by_record_type():
    plugins = ["A.esp", "Win.esp"]
    line = ["Items: same 3 item(s), ORDER DIFFERS from Win.esp"]
    key = "a list whose order the game reads, reordered"
    _, _, unj = ER.lost_edits("FormList", "000001:Base.esm", "A.esp", line, line, "Win.esp", plugins, ER.FieldScope())
    assert unj == {key: 1}, f"a form list is read by index, got {unj}"
    _, _, unj = ER.lost_edits("Container", "000001:Base.esm", "A.esp", line, line, "Win.esp", plugins, ER.FieldScope())
    assert not unj, f"a container's contents have no order the game reads, got {unj}"


def case_47_a_line_of_no_known_form_stays_unread():
    plugins = ["A.esp", "Win.esp"]
    bad = ["SubCells: NOT COMPARED on both sides (here (not compared: element [0] passed))"]
    _, _, unj = ER.lost_edits("Worldspace", "000001:Base.esm", "A.esp", bad, [], "Win.esp", plugins, ER.FieldScope())
    assert unj == {"a delta line the comparator could not read": 1}, unj


def case_15_an_arm_without_evaluated_plugins_has_no_members():
    trees = {"a": {"type": "PlacedObject", "touchers": ["Base.esm", "Fix.esp"]},
             "b": {"type": "Cell", "touchers": ["Base.esm", "Subj.esp"]}}
    none = ER.population_members({"a", "b"}, trees, {}, [])
    assert none == {"records.contested-fields": 0, "records.cell-attributes": 0, "records.registration-and-dialogue": 0}, none
    some = ER.population_members({"a", "b"}, trees, {}, ["Subj.esp"])
    assert some["records.cell-attributes"] == 1 and some["records.contested-fields"] == 0, some
    untreed = ER.population_members({"a", "c"}, trees, {"c": {"type": "PlacedObject"}}, ["Subj.esp"])
    assert untreed["records.contested-fields"] == 1, f"a record the reader returned no tree for stays a member: {untreed}"


PLAN = {"planId": "p1"}
ARM = {"armId": "baseline", "plugins": ["Base.esm", "Subj.esp", "Legacy.esm"], "mods": ["M1", "M2"],
       "candidateSha256": "c" * 64}
READER = {"cmd": "reader.exe", "sha256": "r" * 64}


INPUTS = {"plugins": {"Base.esm": "b" * 64}, "assetView": {"data": "d" * 64}}


def key_of(arm=ARM, code="e" * 64, reader=READER, inputs=INPUTS):
    return ER.arm_key(PLAN, arm, ["Base.esm"], {"skyrim.ini": "i" * 64}, code, reader, inputs)


def saved(t: Path, key):
    imp = t / "imports" / "records.contested-fields__baseline" / "import.json"
    imp.parent.mkdir(parents=True)
    imp.write_text("{}", encoding="utf-8")
    summary = {"imports": [str(imp)], "findings": {"records.contested-fields": 3}, "judged": {}}
    ER.save_checkpoint(t / "baseline", key, summary)
    return summary


def case_17_a_finished_arm_with_the_same_key_is_reused():
    import tempfile
    with tempfile.TemporaryDirectory() as t:
        s = saved(Path(t), key_of())
        got = ER.reusable_checkpoint(Path(t) / "baseline", key_of())
        assert got == s, f"a finished arm with the same key is reused as saved, got {got}"


def case_19_a_changed_arm_code_or_reader_is_read_again():
    import tempfile
    with tempfile.TemporaryDirectory() as t:
        saved(Path(t), key_of())
        changed = {"arm order": key_of(arm=dict(ARM, plugins=["Base.esm", "Legacy.esm", "Subj.esp"])),
                   "arm mods": key_of(arm=dict(ARM, mods=["M2", "M1"])),
                   "candidate bytes": key_of(arm=dict(ARM, candidateSha256="d" * 64)),
                   "evalrecords code": key_of(code="f" * 64),
                   "reader": key_of(reader=dict(READER, sha256="s" * 64)),
                   "plugin bytes": key_of(inputs=dict(INPUTS, plugins={"Base.esm": "c" * 64})),
                   "asset view": key_of(inputs=dict(INPUTS, assetView={"data": "e" * 64}))}
        for what, k in changed.items():
            assert k != key_of(), f"a changed {what} must change the arm's key"
            assert ER.reusable_checkpoint(Path(t) / "baseline", k) is None, f"a changed {what} reused the old arm"
        # an import the checkpoint names that is gone is not reused either
        for p in (Path(t) / "imports").rglob("import.json"):
            p.unlink()
        assert ER.reusable_checkpoint(Path(t) / "baseline", key_of()) is None, "a checkpoint whose import is gone was reused"


def case_21_a_hung_reader_is_stopped_at_the_arm_deadline():
    import time
    cmd = '"' + sys.executable + '" -c "import time; time.sleep(60)"'
    t0 = time.perf_counter()
    try:
        ER.Session(cmd, {}, "evalrun:p1:baseline", deadline_s=1.0)
    except ER.Refusal as r:
        assert "deadline" in str(r), f"the refusal names the deadline: {r}"
        assert time.perf_counter() - t0 < 20, "the hung reader was stopped near its deadline"
        return
    raise AssertionError("a reader that never answers was not stopped at the arm's deadline")


def asset_answer(t: Path, paths, inline, spill_rows=None, complete=True):
    """An asset-status answer in the reader's shape: `inline` rows in the document, the rest spilled to a results file
    (a header line, then one row per line), as the reader writes a long answer."""
    rows = [{"path": p, "exists": True, "error": None} for p in paths]
    doc = {"read_incomplete": False, "bsa_failures": [], "results": rows[:inline],
           "truncated": inline < len(rows)}
    if spill_rows is not None:
        f = t / "asset_status_spill.jsonl"
        head = {"housecarl_artifact": 1, "tool": "housecarl_asset_status", "row_count": len(spill_rows)}
        f.write_text("\n".join(json.dumps(x) for x in [head] + spill_rows) + "\n", encoding="utf-8")
        doc["spilled"] = {"path": str(f), "complete": complete, "row_count": len(spill_rows)}
    return doc, rows


def case_23_a_cut_asset_answer_is_read_whole_from_its_spill():
    import tempfile
    with tempfile.TemporaryDirectory() as t:
        paths = [f"meshes/a/{i:04d}.nif" for i in range(300)]
        doc, rows = asset_answer(Path(t), paths, inline=208, spill_rows=None)
        doc2, _ = asset_answer(Path(t), paths, inline=208,
                               spill_rows=[{"path": p, "exists": True, "error": None} for p in paths])
        got = ER.asset_rows(doc2, paths)
        assert [r["path"] for r in got] == paths, f"the spilled answer is read whole, got {len(got)} of {len(paths)} rows"


def case_25_a_cut_asset_answer_without_a_complete_spill_is_refused():
    import tempfile
    with tempfile.TemporaryDirectory() as t:
        paths = [f"meshes/a/{i:04d}.nif" for i in range(300)]
        for what, (doc, _) in {"no spill": asset_answer(Path(t), paths, inline=208),
                               "an incomplete spill": asset_answer(Path(t), paths, inline=208,
                                                                   spill_rows=[{"path": p} for p in paths], complete=False),
                               "a spill of other paths": asset_answer(Path(t), paths, inline=208,
                                                                      spill_rows=[{"path": p + "x"} for p in paths])}.items():
            try:
                ER.asset_rows(doc, paths)
            except ER.Refusal:
                continue
            raise AssertionError(f"a cut asset answer with {what} was accepted")


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
