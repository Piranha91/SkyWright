"""Known answers for modcensus.py: a census row reports a count only when its read completed, and novelty ranks a
candidate by the features the covered set lacks.

Dependency-free: `python _geometry-harness/tests/test_modcensus.py`. MODCENSUS_PATH, if set, loads the module from
that file instead (the sabotage arms use it). Plugins and reads are synthetic.
"""
import importlib.util
import os
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
_path = os.environ.get("MODCENSUS_PATH") or str(HERE.parent / "modcensus.py")
MC, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("modcensus_under_test", _path)
    MC = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(MC)
except (FileNotFoundError, ImportError, OSError) as _ex:
    MC, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        if MC is None:
            raise AssertionError(f"modcensus.py is not loadable: {LOAD_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


CAND = {"id": "m1", "mod": "Mod One", "plugins": ["One.esp"]}


def scope(complete=True, types=("REFR", "NAVM", "CELL")):
    return {"complete": complete, "walkFailures": [], "unreadableRecords": [],
            "targets": [{"name": "One.esp", "sha256": "h", "footprint": {
                "records": 12, "recordTypes": {t: {"new": 3, "override": 1, "className": t} for t in types},
                "placed": {"total": 8, "interior": 0, "exterior": 8, "persistent": 0, "navm": 1, "land": 0,
                           "cells": [{"cell": "000A:Base.esm", "kind": "exterior"}]},
                "scripts": {"carriers": 0}, "assets": {"modelPaths": 2}}}]}


def deltas(returned=3, requested=3):
    return {"One.esp": {"requested": requested, "returned": returned,
                        "edits": [{"formKey": "1", "kind": "MOVE"}, {"formKey": "2", "kind": "DISABLE"},
                                  {"formKey": "3", "kind": "MOVE"}][:returned]}}


ASSETS = {"complete": True, "overrides": [{"path": "meshes/a.nif", "class": "mesh", "others": ["Other Mod"]}]}


def case_1_full_row_counts():
    r = MC.census_row(CAND, scope(), deltas(), ASSETS)
    assert r["refused"] is None, r
    assert r["editKinds"] == {"MOVE": 2, "DISABLE": 1}, r["editKinds"]
    assert r["recordTypes"]["NAVM"] == {"new": 3, "override": 1} and r["assetOverrides"] == {"mesh": 1}, r


def case_3_incomplete_scope_is_refusal_not_zero():
    r = MC.census_row(CAND, scope(complete=False), deltas(), ASSETS)
    assert r["refused"] and "scope" in r["refused"], f"an incomplete scope refuses the row, got {r.get('refused')}"
    assert r.get("recordTypes") is None and r.get("placed") is None, "no counts from an incomplete scope"


def case_5_short_delta_read_refuses_edit_kinds():
    r = MC.census_row(CAND, scope(), deltas(returned=2, requested=3), ASSETS)
    assert r["editKinds"] is None, f"a short read gives no edit-kind counts, got {r['editKinds']}"
    assert r["editKindsRefused"] and "2 of 3" in r["editKindsRefused"], r.get("editKindsRefused")


def case_7_unread_assets_refused_not_empty():
    r = MC.census_row(CAND, scope(), deltas(), {"complete": False, "overrides": []})
    assert r["assetOverrides"] is None and r["assetOverridesRefused"], r


def case_9_features():
    f = MC.features(MC.census_row(CAND, scope(), deltas(), ASSETS))
    for want in [("editKind", "DISABLE"), ("recordType", "NAVM"), ("placed", "exterior"), ("assetOverride", "mesh")]:
        assert want in f, f"missing feature {want}: {sorted(f)}"


def case_11_novelty_ranks_uncovered_and_skips_covered():
    covered = [MC.census_row(dict(CAND, id="old"), scope(types=("REFR", "CELL")), deltas(), ASSETS)]
    novel = MC.census_row(dict(CAND, id="new"), scope(), deltas(), ASSETS)
    same = MC.census_row(dict(CAND, id="same"), scope(types=("REFR", "CELL")), deltas(), ASSETS)
    out = {r["id"]: r for r in MC.novelty([novel, same], covered)}
    assert out["new"]["decision"] == "full" and ("recordType", "NAVM") in [tuple(x) for x in out["new"]["uncovered"]], out["new"]
    assert out["same"]["decision"] == "census-only" and out["same"]["uncovered"] == [], out["same"]


def case_13_refused_row_is_neither_novel_nor_covered():
    bad = MC.census_row(dict(CAND, id="bad"), scope(complete=False), deltas(), ASSETS)
    out = MC.novelty([bad], [])
    assert out[0]["decision"] == "refused", out


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
