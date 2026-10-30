"""Known answers for editkinds.py, the patch edit-kind classifier.

Dependency-free: `python _geometry-harness/tests/test_editkinds.py`. EDITKINDS_PATH, if set, loads the module from that
file instead (the sabotage arms use it). Records and plugins are synthetic.
"""
import importlib.util
import os
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
_path = os.environ.get("EDITKINDS_PATH") or str(HERE.parent / "editkinds.py")
EK, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("editkinds_under_test", _path)
    EK = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(EK)
except (FileNotFoundError, ImportError, OSError) as _ex:
    EK, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"
PASSED, FAILURES = [], []
PLAYER = "000014:Skyrim.esm"


def check(name, fn):
    try:
        if EK is None:
            raise AssertionError(f"editkinds.py is not loadable: {LOAD_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def kinds(rt, cls, fields, **kw):
    return sorted(e["kind"] for e in EK.classify(rt, cls, fields, **kw))


def case_1_position_is_one_move():
    got = kinds("REFR", "substantive", [{"field": "Placement.Position", "before": [0, 0, 0], "after": [5, 0, 0]},
                                        {"field": "Placement.Rotation", "before": [0, 0, 0], "after": [0, 0, 1]}])
    assert got == ["MOVE", "ROTATE"], f"position and rotation are one MOVE and one ROTATE, got {got}"


def case_2_disable_idiom_absorbs_move_and_parent():
    got = kinds("REFR", "deletion-or-disable", [
        {"field": "RecordFlags", "before": "0x00000000", "after": "0x00000800",
         "decoded": {"before": "none", "after": "InitiallyDisabled"}},
        {"field": "EnableParent", "before": {"parent": "000AAA:ModA.esp", "flags": 1},
         "after": {"parent": PLAYER, "flags": 1}},
        {"field": "Placement.Position", "before": [0, 0, 0], "after": [0, 0, -30000]}])
    assert got == ["DISABLE"], f"a disable idiom is one DISABLE, got {got}"


def case_3_enable_when_flag_cleared():
    got = kinds("REFR", "substantive", [{"field": "RecordFlags", "before": "0x00000800", "after": "0x00000000",
                                         "decoded": {"before": "InitiallyDisabled", "after": "none"}}])
    assert got == ["ENABLE"], f"clearing InitiallyDisabled is ENABLE, got {got}"


def case_4_header_bookkeeping_and_identity_are_not_edits():
    got = kinds("CELL", "substantive", [{"field": "Header.version2", "before": 4, "after": 0},
                                        {"field": "VMAD", "before": "a", "after": "b"}], identity={"VMAD"})
    assert got == [], f"bookkeeping and identity forwards are not edits, got {got}"


def case_5_registration_list_is_bookkeeping_field():
    es = EK.classify("LCTN", "substantive", [{"field": "LCPR", "before": [], "after": [1]}],
                     canon={"LCPR": ["LocationCellPersistentReferences", "LCPR"]})
    assert [(e["kind"], e["bookkeepingKind"]) for e in es] == [("FIELD:LocationCellPersistentReferences", True)], es


def case_6_other_field_by_first_segment():
    got = kinds("CELL", "substantive", [{"field": "Lighting.AmbientColor", "before": 1, "after": 2},
                                        {"field": "Lighting.FogNear", "before": 1, "after": 2}])
    assert got == ["FIELD:Lighting"], f"one FIELD:<first segment> per kind, got {got}"


def case_7_addition_and_container():
    assert kinds("REFR", "addition", []) == ["ADD"]
    assert kinds("CELL", "container-bookkeeping", [{"field": "Temporary", "before": 1, "after": 2}]) == []


def case_8_absorb_disabled_carries_other_kinds():
    es = [{"formKey": "1", "kind": "DISABLE", "paths": ["RecordFlags"]},
          {"formKey": "1", "kind": "FIELD:Owner", "paths": ["Owner"]},
          {"formKey": "2", "kind": "MOVE", "paths": ["Placement.Position"]}]
    kept, carried = EK.absorb_disabled(es)
    assert [(e["formKey"], e["kind"]) for e in kept] == [("1", "DISABLE"), ("2", "MOVE")], kept
    assert [e["kind"] for e in carried] == ["FIELD:Owner"], carried


def case_9_persistent_flag_alone_is_bookkeeping():
    es = EK.classify("REFR", "substantive", [{"field": "RecordFlags", "before": "0x00000000", "after": "0x00000400",
                                              "decoded": {"before": "none", "after": "Persistent"}}])
    assert [(e["kind"], e["bookkeepingKind"]) for e in es] == [("RECORD-FLAGS", True)], es


def case_11_container_group_bookkeeping_is_not_an_edit():
    got = kinds("CELL", "substantive", [{"field": "PersistentTimestamp", "before": 1, "after": 2},
                                        {"field": "SubCells", "before": 1, "after": 2},
                                        {"field": "Persistent", "before": 1, "after": 2}])
    assert got == [], f"a container's child-group bookkeeping is not an edit, got {got}"
    got = kinds("WRLD", "substantive", [{"field": "SubCellsTimestamp", "before": 1, "after": 2},
                                        {"field": "Water", "before": 1, "after": 2}])
    assert got == ["FIELD:Water"], f"only the worldspace's own field is an edit, got {got}"
    got = kinds("REFR", "substantive", [{"field": "PersistentTimestamp", "before": 1, "after": 2}])
    assert got == ["FIELD:PersistentTimestamp"], f"the rule is for containers only, got {got}"


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
