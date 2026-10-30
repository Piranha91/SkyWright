"""Known answers for evalrecords.py's selection rules (the record capture for the registry's mcp-only checks).

Dependency-free: `python _geometry-harness/tests/test_evalrecords.py`. EVALRECORDS_PATH, if set, loads the module
from that file instead (the sabotage arms use it). Rows are synthetic, in the summary-row shape the record reader's
summary exports carry (formid, type, parent cell, override_depth).

Selection per arm: A = every record the loaded subject and keys touch; P = the loaded partners' records; within the
comparison cells H and the local types T, the local set is (P intersect T intersect H) union C, where C is the order's
contested records of types T in H. A record is contested when its override depth is above one; a row without a depth
is unjudged, never uncontested. The import itself is checked at Gate C against round 2's accepted Missives imports.
"""
import importlib.util
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


def case_1_local_union():
    partner = [row("p1", "REFR", "C1"), row("p2", "REFR", "C9"), row("p3", "QUST", None), row("p4", "NAVM", "C2")]
    contested = [row("c1", "REFR", "C1", 2), row("p1", "REFR", "C1", 2)]
    got = ER.local_union(partner, contested, CELLS, TYPES)
    assert got == {"p1", "p4", "c1"}, f"(P ∩ T ∩ H) ∪ C, got {sorted(got)}"


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


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
