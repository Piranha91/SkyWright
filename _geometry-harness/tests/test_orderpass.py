#!/usr/bin/env python
"""Known answers for orderpass, the whole-order pass driver, over stand-in programs.

The driver's stages are run through its `resolve` hook, so each probe here is a small script that writes the real file
shape (a conflict file, route rows, a census) for a synthetic order. Checks: a failed stage is recorded and named
unassessed while the pass goes on; a missing program is refused by its path; pass.json records what was added and the
pass's first read is that mod's view. Each case prints PASS, FAIL or ERROR; exit 1 if any case is not PASS.
"""
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "_geometry-harness"))
sys.path.insert(0, str(Path(__file__).resolve().parent))


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "_geometry-harness" / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


FAKE = r'''
import json, sys
args = sys.argv[1:]
probe = args[0]
def val(flag):
    return args[args.index(flag) + 1] if flag in args else None
if probe == "fail":
    sys.exit(1)
if probe == "ordermap":
    src = val("--fixture-map")
    json.dump(json.load(open(src)), open(val("--out"), "w"))
    sys.exit(0)
out = val("--emit") or val("--json") or val("--rows")
if probe == "blockprobe":
    json.dump({"Rows": [], "Tally": {}}, open(out, "w"))
elif probe == "switchprobe":
    json.dump({"plugins": [], "switches": []}, open(out, "w"))
elif probe == "depprobe":
    json.dump({"rows": []}, open(out, "w"))
else:
    rows = []
    if probe == "overlapprobe":
        rows = [{"Kind": "OVERLAP", "A": "ModA.esp", "B": "ModD.esp", "Summary": "Static from ModA.esp / Static from ModD.esp: crossing in CellOne (int)",
                 "Necessary": [{"FormKey": "000901:ModA.esp", "Label": "CellOne (int)"}], "Alternative": [{"FormKey": "000902:ModD.esp"}]},
                {"Kind": "OVERLAP", "A": "ModB.esp", "B": "ModC.esp", "Summary": "Static from ModB.esp / Static from ModC.esp: crossing in CellTwo (int)",
                 "Necessary": [{"FormKey": "000903:ModB.esp", "Label": "CellTwo (int)"}], "Alternative": [{"FormKey": "000904:ModC.esp"}]}]
    json.dump({"Conflicts": rows, "Coverage": []}, open(out, "w"))
'''


def fixture_map(path):
    p = lambda n, f, base=False: {"name": n, "folder": f, "bytes": 900, "baseGame": base, "error": None, "masters": [] if base else ["Base.esm"],
                                  "newRecords": 2, "overrides": 2, "interior": 1, "exterior": 0, "scriptCarriers": 0, "exteriorCells": []}
    m = {"tool": "ordermap", "schema": 2, "source": "synthetic", "plugins": [p("Base.esm", None, True), p("ModA.esp", "FolderA"), p("ModB.esp", "FolderB"),
                                                                              p("ModC.esp", "FolderC"), p("ModD.esp", "FolderD")],
         "edges": [], "folders": ["FolderA", "FolderB", "FolderC", "FolderD"], "assetEdges": [], "runtimeLayers": {}, "archivesUnreadable": [],
         "archives": 0, "assetPaths": 0, "assetPathsShared": 0, "hashed": True, "hashedBytes": 0, "cells": {}}
    path.write_text(json.dumps(m), encoding="utf-8")


def resolver(tmp, fail=(), missing=()):
    fake = tmp / "fake.py"
    fake.write_text(FAKE, encoding="utf-8")
    fixture_map(tmp / "fixture-map.json")

    def resolve(name):
        if name in missing:
            return [str(tmp / "absent" / f"{name}.exe")]
        if name in fail:
            return [sys.executable, str(fake), "fail"]
        extra = ["--fixture-map", str(tmp / "fixture-map.json")] if name == "ordermap" else []
        return [sys.executable, str(fake), name] + extra
    return resolve


def run(P, tmp, added=None, fail=(), missing=()):
    out = tmp / "pass"
    argv = ["--out", str(out), "--instance", str(tmp / "instance"), "--profile", "P", "--game", str(tmp / "game"), "--skip", "records"]
    for a in added or []:
        argv += ["--added", a]
    code = P.main(argv, resolve=resolver(tmp, fail, missing))
    return code, out


def case_P1_failed_stage(P, L, tmp):
    code, out = run(P, tmp, fail=("seamprobe",))
    stages = json.loads((out / "stages.json").read_text(encoding="utf-8"))
    assert stages["seam"]["exit"] == 1, f"seam stage recorded as {stages.get('seam')}"
    assert stages["overlap"]["exit"] == 0 and stages["water"]["exit"] == 0, "the pass stopped at the failed stage"
    idx = L.build(out)
    assert "seam" in json.dumps(idx.unassessed.get("stages")), f"the failed stage isn't named unassessed: {idx.unassessed.get('stages')}"
    assert "records" in json.dumps(idx.unassessed.get("stages")), "the skipped records stage isn't named unassessed"


def case_P2_missing_program(P, L, tmp):
    code, out = run(P, tmp, missing=("waterprobe",))
    stages = json.loads((out / "stages.json").read_text(encoding="utf-8"))
    w = stages.get("water") or {}
    assert w.get("exit") != 0 and "waterprobe.exe" in str(w.get("refused") or ""), f"a missing program was not refused by its path: {w}"


def case_P3_added_first(P, L, tmp):
    code, out = run(P, tmp, added=["ModD.esp"])
    assert code == 0, f"orderpass exit {code}"
    pj = json.loads((out / "pass.json").read_text(encoding="utf-8"))
    assert pj.get("added") == ["ModD.esp"], f"pass.json added {pj.get('added')}"
    first = (out / "LEADS.md").read_text(encoding="utf-8")
    whole = (out / "LEADS-whole.md").read_text(encoding="utf-8")
    assert "ModD.esp" in first and "ModB.esp and ModC.esp" not in first, "LEADS.md is not the added mod's view"
    assert "ModB.esp and ModC.esp" in whole, "LEADS-whole.md lacks the other pair's lead"


CASES = [case_P1_failed_stage, case_P2_missing_program, case_P3_added_first]


def main():
    try:
        P, L = load("orderpass"), load("orderleads")
    except Exception as ex:
        print(f"ERROR import: {type(ex).__name__}: {ex}")
        return 1
    bad = 0
    for case in CASES:
        name = case.__name__[len("case_"):]
        with tempfile.TemporaryDirectory() as t:
            try:
                case(P, L, Path(t))
                print(f"PASS {name}")
            except AssertionError as ex:
                bad += 1
                print(f"FAIL {name}: {ex}")
            except Exception as ex:
                bad += 1
                print(f"ERROR {name}: {type(ex).__name__}: {ex}")
                traceback.print_exc(limit=2, file=sys.stdout)
    print(f"orderpass: {len(CASES) - bad} of {len(CASES)} cases pass")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
