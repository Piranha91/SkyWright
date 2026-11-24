"""Known answers for which switches the runtime state-switch check is about (switchflip_scope.py).

Dependency-free: `python _geometry-harness/tests/test_switchflip_scope.py`.
Each test names the wrong answer it rejects. Synthetic census and scope documents only.
"""

import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, os.path.dirname(HERE))

import switchflip_scope as S  # noqa: E402

FAILURES, PASSED = [], []
TARGET_CELL, OTHER_CELL = "00A001:Target.esp", "00B002:Base.esm"


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def scope(cells=True, complete=True, targets=("Target.esp",)):
    placed = {"total": 3}
    if cells:
        placed["cells"] = [{"cell": TARGET_CELL, "kind": "interior"}]
    return {"complete": complete, "targets": [{"name": t, "footprint": {"placed": placed}} for t in targets]}


def switch(key, status, owner="Base.esm", cell=OTHER_CELL, member=None):
    m = member or {"key": "000111:Base.esm", "placer": "Base.esm", "attacher": "Base.esm", "cellKey": OTHER_CELL}
    return {"key": key, "edid": key, "owner": owner, "cellKey": cell, "onSet": [m], "offSet": [],
            "trigger": None if status is None else {"status": status, "kind": "quest stage", "by": []}}


def census(switches, triggers=True, unreadable=()):
    return {"summary": {"switches": len(switches), "triggers": {"triggered": 0} if triggers else None},
            "unreadable": list(unreadable), "switches": switches, "jointFlips": []}


VANILLA = [switch("000A01:Base.esm", "Triggered"), switch("000A02:Base.esm", "Triggered"),
           switch("000A03:Base.esm", "BoundUnread"), switch("000A04:Base.esm", "Unknown")]


def test_k18_only_vanilla_driven_switches_is_not_applicable():
    # rejects: open because the whole-order census has script-driven switches the targets never touch
    r = S.population(census(VANILLA), scope())
    assert r["status"] == "not-applicable", r
    assert r["counts"] == {"switches": 4, "scriptDriven": 3, "triggered": 2, "boundUnread": 1, "population": 0,
                           "undecided": 0}, r["counts"]
    assert "nothing to flip" in S.sentence(r)


def test_k18b_an_undriven_target_switch_is_not_applicable():
    # rejects: a target-owned switch nothing drives counted as something to flip
    r = S.population(census(VANILLA + [switch("000C01:Target.esp", "Unknown", owner="Target.esp")]), scope())
    assert r["status"] == "not-applicable", r


def test_k20_a_member_a_target_attaches_keeps_it_open():
    # rejects: not-applicable because the switch's owner is not a target
    m = {"key": "000D01:Target.esp", "placer": "Base.esm", "attacher": "Target.esp", "cellKey": OTHER_CELL}
    r = S.population(census(VANILLA + [switch("000A09:Base.esm", "Triggered", member=m)]), scope())
    assert r["status"] == "open" and [p["by"] for p in r["population"]] == ["plugin"], r


def test_k20b_a_switch_in_a_target_cell_keeps_it_open():
    # rejects: the cell clause ignored (a vanilla quest's set in a cell the mod rebuilds)
    r = S.population(census(VANILLA + [switch("000A0A:Base.esm", "Triggered", cell=TARGET_CELL)]), scope())
    assert r["status"] == "open" and [p["by"] for p in r["population"]] == ["cell"], r


def test_k20c_a_member_in_a_target_cell_keeps_it_open():
    m = {"key": "000111:Base.esm", "placer": "Base.esm", "attacher": "Base.esm", "cellKey": TARGET_CELL}
    r = S.population(census(VANILLA + [switch("000A0B:Base.esm", "Triggered", member=m)]), scope())
    assert r["status"] == "open" and [p["by"] for p in r["population"]] == ["cell"], r


def test_k22_bound_unread_in_a_target_cell_keeps_it_open():
    # rejects: reading "bound, call not read" as "nothing drives it"
    r = S.population(census(VANILLA + [switch("000A0C:Base.esm", "BoundUnread", cell=TARGET_CELL)]), scope())
    assert r["status"] == "open", r
    assert [p["trigger"] for p in r["population"]] == ["BoundUnread"], r


def test_k24_no_trigger_map_keeps_it_open():
    # rejects: an empty population read from a census that never looked for triggers
    r = S.population(census(VANILLA, triggers=False), scope())
    assert r["status"] == "open" and [c["code"] for c in r["causes"]] == ["no-trigger-map"], r
    r2 = S.population({"summary": {"switches": 0}, "switches": []}, scope())
    assert r2["status"] == "open" and [c["code"] for c in r2["causes"]] == ["no-trigger-map"], r2


def test_k24b_unreadable_plugins_keep_it_open():
    r = S.population(census(VANILLA, unreadable=["Broken.esp"]), scope())
    assert r["status"] == "open" and [c["code"] for c in r["causes"]] == ["unreadable-plugins"], r


def test_k24c_incomplete_scope_or_no_targets_keep_it_open():
    r = S.population(census(VANILLA), scope(complete=False))
    assert r["status"] == "open" and [c["code"] for c in r["causes"]] == ["scope-incomplete"], r
    r2 = S.population(census(VANILLA), scope(targets=()))
    assert r2["status"] == "open" and "no-targets" in [c["code"] for c in r2["causes"]], r2


def test_k24d_unlisted_target_cells_keep_a_driven_switch_open():
    # rejects: a driven switch ruled out by cell when the scope never listed the cells
    r = S.population(census(VANILLA), scope(cells=False))
    assert r["status"] == "open" and [c["code"] for c in r["causes"]] == ["cells-unknown"], r
    assert r["counts"]["undecided"] == 3, r["counts"]
    # with nothing script-driven at all, unlisted cells do not matter
    r2 = S.population(census([switch("000A04:Base.esm", "Unknown")]), scope(cells=False))
    assert r2["status"] == "not-applicable", r2


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
