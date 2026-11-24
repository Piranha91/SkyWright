"""Known answers for switchflip.py's launch-free parts: the flip-plan validator, the schedule, the block lock and the
export of one arm's import document. Nothing here launches the game.

Dependency-free: `python _geometry-harness/tests/test_switchflip_plan.py`. Each test names the wrong answer it rejects.
"""

import copy
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, os.path.dirname(HERE))

import switchflip as S  # noqa: E402

FAILURES, PASSED = [], []


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


GOOD = {
    "schemaVersion": 1, "binding": None,
    "arms": [{"id": "patched", "profile": "P1"}, {"id": "unpatched", "profile": "P2"}],
    "conditions": [
        {"id": "R", "arm": "patched", "arrival": {"cell": "SomeCell"}, "settleSeconds": 0, "windowSeconds": 60,
         "replicates": 2, "expect": "fail",
         "calls": [{"ref": "000ABC:Some.esp", "target": "enabled", "source": "Script.Fn, call 1"},
                   {"ref": "000ABD:Some.esp", "target": "enabled", "source": "Script.Fn, call 2"}]},
        {"id": "G", "arm": "unpatched", "arrival": {"cell": "SomeCell"}, "settleSeconds": 0, "windowSeconds": 60,
         "replicates": 1, "expect": "complete", "canaryAfter": True,
         "calls": [{"ref": "000ABC:Some.esp", "target": "enabled", "source": "Script.Fn, call 1"}]}],
    "canary": {"consoleCommand": "known crash", "label": "detector check"},
}


def bad(mutate):
    d = copy.deepcopy(GOOD)
    mutate(d)
    return S.validate_plan(d)


def test_good_plan_is_valid():
    assert S.validate_plan(GOOD) == [], S.validate_plan(GOOD)


# K12 -- rejects: accepting a plan the driver could not honour
def test_k12_call_without_target_refused():
    p = bad(lambda d: d["conditions"][0]["calls"][0].pop("target"))
    assert any("target must be" in x for x in p), p


def test_k12_call_without_source_refused():
    p = bad(lambda d: d["conditions"][0]["calls"][0].update(source=" "))
    assert any("source" in x for x in p), p


def test_k12_short_window_refused():
    p = bad(lambda d: d["conditions"][0].update(windowSeconds=30))
    assert any("windowSeconds" in x for x in p), p


def test_k12_unknown_arm_refused():
    p = bad(lambda d: d["conditions"][0].update(arm="nope"))
    assert any("unknown arm" in x for x in p), p


def test_k12_malformed_ref_refused():
    p = bad(lambda d: d["conditions"][0]["calls"][0].update(ref="0xABC"))
    assert any("is not XXXXXX:Plugin.esp" in x for x in p), p


def test_k12_canary_without_command_refused():
    p = bad(lambda d: d.pop("canary"))
    assert any("canaryAfter needs" in x for x in p), p


def test_k12_bound_plan_names_eval_arms():
    p = bad(lambda d: d.update(binding={"planId": "abc", "checkId": "runtime.state-switch-flip"}))
    assert any("evalArmId" in x for x in p), p


def test_k12_duplicate_schedule_refused():
    p = bad(lambda d: d.update(schedule=[["R", 1], ["R", 1]]))
    assert any("twice" in x for x in p), p


def test_schedule_interleaves_replicates():
    assert S.schedule_of(GOOD) == [["R", 1], ["G", 1], ["R", 2]], S.schedule_of(GOOD)


# rejects: a lock taken over another owner's, or a lock left behind
def test_lock_taken_only_when_absent_and_released():
    with tempfile.TemporaryDirectory() as d:
        lock = Path(d) / "GAME-LOCK.json"
        S.take_lock(lock, "test")
        assert json.loads(lock.read_text())["pid"] == os.getpid()
        try:
            S.take_lock(lock, "again")
            raise AssertionError("a second take succeeded")
        except FileExistsError:
            pass
        S.release_lock(lock)
        assert not lock.exists()


def test_release_never_removes_another_owners_lock():
    with tempfile.TemporaryDirectory() as d:
        lock = Path(d) / "GAME-LOCK.json"
        lock.write_text(json.dumps({"owner": "another lane", "pid": 1}))
        S.release_lock(lock)
        assert lock.exists()


# rejects: an export that hides an unsettled arm, or exports a standalone block
def _block(d: Path, kinds, bound=True):
    plan = copy.deepcopy(GOOD)
    if bound:
        plan["binding"] = {"planId": "p1", "checkId": "runtime.state-switch-flip"}
        for a in plan["arms"]:
            a["evalArmId"] = "arm-" + a["id"]
    (d / "flipplan.json").write_text(json.dumps(plan))
    for i, k in enumerate(kinds, start=1):
        f = d / f"R-{i}"
        f.mkdir()
        (f / "events.jsonl").write_text("{}\n")
        cls = {"outcome": k, "branch": "crash" if k == "fail" else None, "why": "x"}
        (f / "receipt.json").write_text(json.dumps({"condition": "R", "replicate": i, "classification": cls,
                                                    "configuration": {"fingerprint": "f"}, "crashLogs": []}))


def _export(d, arm="patched"):
    out = d / "import.json"
    rc = S.cmd_export(type("A", (), {"block": str(d), "arm": arm, "out": str(out)})())
    return rc, (json.loads(out.read_text()) if out.exists() else None)


def test_export_two_failures_is_finding():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        _block(d, ["fail", "fail"])
        rc, doc = _export(d)
        assert rc == 0 and doc["result"]["detector"] == "finding" and doc["armId"] == "arm-patched", doc
        assert sum(1 for o in doc["outputs"] if o["role"] == "launch-receipt") == 2


def test_export_marks_unsettled():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        _block(d, ["fail", "complete"])
        rc, doc = _export(d)
        assert doc["readings"]["R"]["reading"] == "unsettled", doc["readings"]


def test_export_refuses_standalone_block():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        _block(d, ["fail", "fail"], bound=False)
        rc, doc = _export(d)
        assert rc == S.EXIT_REFUSED_INPUT and doc is None


# P50: a bound arm's candidate may state its own asset priority; the game follows the launched profile's modlist.txt.
def _priority_instance(d: Path, modlist: str) -> Path:
    (d / "profiles" / "P1").mkdir(parents=True)
    (d / "profiles" / "P1" / "modlist.txt").write_text(modlist, encoding="utf-8")
    return d


def _earm(mods):
    return {"armId": "patched", "requested": {"providers": [{"name": "overwrite", "kind": "overwrite"}]
                                              + [{"name": m, "kind": "mod"} for m in mods] + [{"name": "Data", "kind": "data"}]}}


# rejects: launching a profile that holds a different asset priority than the arm's candidate states
def test_p50_profile_with_other_priority_refused():
    with tempfile.TemporaryDirectory() as d:
        inst = _priority_instance(Path(d), "# header\n+High\n+Other\n+Low\n")
        why = S.profile_priority_refusal(str(inst), "P1", _earm(["Low", "High"]))
        assert why and "orders 'High' above 'Low'" in why and "\n" not in why, why


# rejects: launching a profile where a folder the arm's view needs is off
def test_p50_profile_with_folder_disabled_refused():
    with tempfile.TemporaryDirectory() as d:
        inst = _priority_instance(Path(d), "+Low\n-High\n")
        why = S.profile_priority_refusal(str(inst), "P1", _earm(["Low", "High"]))
        assert why and "does not enable mod folder 'High'" in why, why


# rejects: refusing a profile that holds the candidate's priority (other folders between are allowed)
def test_p50_profile_holding_priority_passes():
    with tempfile.TemporaryDirectory() as d:
        inst = _priority_instance(Path(d), "+Low\n-Other\n+High\n")
        assert S.profile_priority_refusal(str(inst), "P1", _earm(["Low", "High"])) is None
        assert S.profile_priority_refusal(str(inst), "P1", _earm([])) is None


# rejects: a bound block that launches before the priority check (refusal must come before any lock or launch)
def test_p50_bound_batch_refuses_before_the_lock():
    import argparse
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        inst = _priority_instance(d / "inst", "+High\n+Low\n")
        plan = copy.deepcopy(GOOD)
        plan["binding"] = {"planId": "abc", "checkId": "runtime.state-switch-flip"}
        plan["arms"] = [{"id": "patched", "profile": "P1", "evalArmId": "patched"},
                        {"id": "unpatched", "profile": "P1", "evalArmId": "unpatched"}]
        (d / "flip.json").write_text(json.dumps(plan), encoding="utf-8")
        eplan = {"planId": "abc", "arms": [_earm(["Low", "High"]), {**_earm(["High", "Low"]), "armId": "unpatched"}]}
        (d / "plan.json").write_text(json.dumps(eplan), encoding="utf-8")
        a = argparse.Namespace(plan=str(d / "flip.json"), out=str(d / "out"), instance=str(inst), lock=str(d / "LOCK"),
                               refuse_if=[], evalrun_plan=str(d / "plan.json"), dry=False, min_free_gb=0)
        rc = S.cmd_batch(a)
        assert rc == S.EXIT_REFUSED_INPUT and not (d / "LOCK").exists() and not (d / "out").exists(), rc


# rejects: a launch job the shared Runner cannot run (a block that dies before its first launch)
def test_launch_job_is_accepted_by_the_runner():
    sys.path.insert(0, str(SW.SCRIPTS))
    from runlog import Runner
    with tempfile.TemporaryDirectory() as d:
        os.environ["HOUSECARL_RUNLOG_DIR"] = d
        job = S.launch_job("R-1", [sys.executable, "-c", "pass"], GOOD["conditions"][0])
        res = Runner("switchflip-launch-test", out_dir=Path(d) / "runner").run([job], echo=False)[0]
        assert res.get("exitCode") == 0, res


def test_c1915_the_arrival_reading_records_position_and_grid():
    # rejects: a receipt that names the cell only, and a grid rounded instead of floored at a cell edge
    ral = {"cell": "SomeCell", "fresh_game": True, "rows": [{"t": 0.0, "player": (-4096.0, 8191.96, -120.04)}]}
    assert S.arrival_reading(ral) == {"cell": "SomeCell", "position": [-4096.0, 8192.0, -120.0], "grid": [-1, 1]}
    ral["rows"][0]["player"] = (-4095.4, 2047.6, 0.0)
    assert S.arrival_reading(ral)["grid"] == [-1, 0]
    assert S.arrival_reading({"cell": "SomeCell", "rows": [{"player": None}]}) ==         {"cell": "SomeCell", "position": None, "grid": None}


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
