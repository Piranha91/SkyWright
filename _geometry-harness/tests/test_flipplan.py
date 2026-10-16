"""Known answers for the flip-plan generator (flipplan.py).

Dependency-free: `python _geometry-harness/tests/test_flipplan.py`.
Each test names the wrong answer it rejects. Synthetic census, scope and script text only; a lab case is replayed
outside the tracked tree.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import flipplan as F  # noqa: E402
import switchflip  # noqa: E402

FAILURES, PASSED = [], []
CELL_KEY = "00A001:Target.esp"


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


SCOPE = {"complete": True, "targets": [{"name": "Target.esp", "footprint": {"placed": {"cells": [{"cell": CELL_KEY}]}}}]}

SCRIPT = """Scriptname TownScript extends Quest
ObjectReference Property GuardParent Auto
ObjectReference Property CampParent Auto
ObjectReference Property BannerParent Auto
ObjectReference Property LooseRef Auto

Function Setup(int side)
  If side == 1
    GuardParent.Enable()
    CampParent.Disable()
  Else
    CampParent.Enable()
  EndIf
  BannerParent.Enable() ; both sides
  LooseRef.Enable()
EndFunction

Function Fragment_3()
  GuardParent.Enable()
  BannerParent.Enable()
EndFunction
"""


def switch(key, edid, *, baseline=False, by=(), status="Triggered", parent=None, cell="TownInterior (int)"):
    return {"key": key, "edid": edid, "owner": "Target.esp", "cell": cell, "cellKey": CELL_KEY,
            "baselineEnabled": baseline, "baselineResolved": True, "parent": parent,
            "onSet": [{"key": key[:5] + "F:Target.esp", "cell": cell, "cellKey": CELL_KEY, "placer": "Target.esp",
                       "attacher": "Target.esp"}], "offSet": [],
            "trigger": {"status": status, "kind": "quest stage", "by": list(by)}}


def by(fn, text, stage=None):
    head = f"quest TownQuest stage {stage}" if stage else "quest TownQuest"
    return f"{head} (TownScript.{fn}: {text})"


def census(extra=()):
    sw = [
        switch("000A01:Target.esp", "GuardParent", by=[by("Setup", "GuardParent.Enable("),
                                                        by("Fragment_3", "GuardParent.Enable(", stage=30)]),
        switch("000A02:Target.esp", "CampParent", baseline=True, by=[by("Setup", "CampParent.Disable("),
                                                                     by("Setup", "CampParent.Enable(")]),
        switch("000A03:Target.esp", "BannerParent", by=[by("Setup", "BannerParent.Enable("),
                                                         by("Fragment_3", "BannerParent.Enable(", stage=30)],
               parent="000A01:Target.esp"),
    ] + list(extra)
    return {"summary": {"switches": len(sw), "triggers": {"triggered": len(sw)}}, "unreadable": [], "switches": sw,
            "jointFlips": [{"stage": "TownQuest:30", "switches": ["GuardParent", "BannerParent"]}]}


def scripts_dir(tmp, text=SCRIPT):
    d = Path(tmp) / "scripts"
    d.mkdir(exist_ok=True)
    (d / "TownScript.psc").write_text(text, encoding="utf-8")
    return d


def build(tmp, cen=None, **kw):
    return F.build([("patched", "Prof-P", cen or census())], SCOPE, scripts_dir(tmp), **kw)


def group(doc, gid):
    hits = [g for g in doc["arms"][0]["groups"] if g["id"] == gid]
    assert len(hits) == 1, f"group {gid} not found once; groups: {[g['id'] for g in doc['arms'][0]['groups']]}"
    return hits[0]


def calls(g):
    return [(c["ref"], c["target"]) for c in g["calls"]]


def test_g2_two_branch_function_gives_two_groups_in_source_order():
    # rejects: the two branches flattened into one sequence (CampParent disabled and enabled in one launch)
    with tempfile.TemporaryDirectory() as tmp:
        _, doc = build(tmp)
        setup = [g["id"] for g in doc["arms"][0]["groups"] if g["function"] == "Setup"]
        assert setup == ["TownScript.Setup#1", "TownScript.Setup#2"], setup
        g1, g2 = group(doc, "TownScript.Setup#1"), group(doc, "TownScript.Setup#2")
        assert calls(g1) == [("000A01:Target.esp", "enabled"), ("000A02:Target.esp", "disabled"),
                             ("000A03:Target.esp", "enabled")], calls(g1)
        # the Else arm: CampParent is already enabled at load, so its Enable() is dropped, not sent
        assert calls(g2) == [("000A03:Target.esp", "enabled")], calls(g2)


def test_g4_stage_fragment_is_one_group_of_its_switches():
    with tempfile.TemporaryDirectory() as tmp:
        _, doc = build(tmp)
        g = group(doc, "TownScript.Fragment_3")
        assert g["stage"] == "TownQuest:30" and calls(g) == [("000A01:Target.esp", "enabled"),
                                                           ("000A03:Target.esp", "enabled")], g


def test_g6_call_mapping_to_no_switch_is_listed_unmapped():
    # rejects: LooseRef.Enable() silently dropped
    with tempfile.TemporaryDirectory() as tmp:
        _, doc = build(tmp)
        un = doc["arms"][0]["unmapped"]
        assert [(u["source"], u["call"]) for u in un] == [("TownScript.Setup", "LooseRef.Enable(")], un


def test_g8_no_change_call_is_dropped_and_named():
    # rejects: a call to a switch already in its target state sent as a flip
    with tempfile.TemporaryDirectory() as tmp:
        _, doc = build(tmp)
        d = group(doc, "TownScript.Setup#2")["dropped"]
        assert [(x["ref"], x["target"]) for x in d] == [("000A02:Target.esp", "enabled")] and "already enabled" in d[0]["reason"], d


def test_g10_enable_parented_target_is_flagged():
    # rejects: an enable-parented switch's call planned as if it must take effect, with no flag
    with tempfile.TemporaryDirectory() as tmp:
        _, doc = build(tmp)
        f = group(doc, "TownScript.Fragment_3")["flags"]
        assert [x["ref"] for x in f] == ["000A03:Target.esp"] and "enable-parented" in f[0]["flag"], f


def test_g12_bound_unread_switch_is_named_not_planned():
    # rejects: a bound-but-unread switch dropped without a word, or planned with a guessed call
    with tempfile.TemporaryDirectory() as tmp:
        cen = census([switch("000A09:Target.esp", "Mystery", status="BoundUnread",
                             by=[])])
        _, doc = build(tmp, cen)
        np_ = doc["arms"][0]["notPlanned"]
        assert [(x["ref"], "not read" in x["reason"]) for x in np_] == [("000A09:Target.esp", True)], np_
        assert all(c["ref"] != "000A09:Target.esp" for g in doc["arms"][0]["groups"] for c in g["calls"])


def test_g12b_missing_source_is_named_not_planned():
    with tempfile.TemporaryDirectory() as tmp:
        d = Path(tmp) / "empty"
        d.mkdir()
        _, doc = F.build([("patched", "Prof-P", census())], SCOPE, d)
        np_ = doc["arms"][0]["notPlanned"]
        assert sorted(x["ref"] for x in np_) == ["000A01:Target.esp", "000A02:Target.esp", "000A03:Target.esp"], np_
        assert all("not supplied" in x["reason"] for x in np_) and not doc["arms"][0]["groups"], np_


def test_g14_output_validates_and_is_deterministic():
    # rejects: a draft switchflip would refuse, or bytes that change between runs on the same inputs
    with tempfile.TemporaryDirectory() as tmp:
        plan, doc = build(tmp, neutral_cell="NeutralTestCell", canary_command="known crash")
        assert switchflip.validate_plan(plan) == [], switchflip.validate_plan(plan)
        assert [c["id"] for c in plan["conditions"]] == [
            "patched-g01-loaded", "patched-g01-away", "patched-g02-loaded", "patched-g02-away",
            "patched-g03-loaded", "patched-g03-away"], [c["id"] for c in plan["conditions"]]
        assert all(c["arrival"]["cell"] == ("NeutralTestCell" if c["id"].endswith("away") else "TownInterior")
                   for c in plan["conditions"])
        assert all(c["replicates"] == 2 and c["windowSeconds"] == 60 for c in plan["conditions"])
        plan2, doc2 = build(tmp, neutral_cell="NeutralTestCell", canary_command="known crash")
        assert F.dumps(plan) == F.dumps(plan2) and F.dumps(doc) == F.dumps(doc2)


def test_g14b_no_neutral_cell_omits_the_away_condition_by_name():
    with tempfile.TemporaryDirectory() as tmp:
        plan, doc = build(tmp)
        assert all(c["id"].endswith("-loaded") for c in plan["conditions"])
        om = doc["arms"][0]["omittedConditions"]
        assert len(om) == 3 and all("neutral cell" in x["reason"] for x in om), om


def test_g14c_cli_writes_both_files_and_binds_to_an_evalrun_plan():
    with tempfile.TemporaryDirectory() as tmp:
        t = Path(tmp)
        (t / "census.json").write_text(json.dumps(census()), encoding="utf-8")
        (t / "scope.json").write_text(json.dumps(SCOPE), encoding="utf-8")
        (t / "plan.json").write_text(json.dumps({"planId": "abc123", "arms": [{"armId": "patched"}]}), encoding="utf-8")
        scripts_dir(tmp)
        env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(t / "runlog"))
        base = [sys.executable, str(Path(HERE).parent / "flipplan.py"), "--arm", "patched", "Prof-P", str(t / "census.json"),
                "--scope", str(t / "scope.json"), "--scripts", str(t / "scripts"),
                "--out", str(t / "flip.json"), "--groups", str(t / "groups.json")]
        r = subprocess.run(base + ["--evalrun-plan", str(t / "plan.json")], capture_output=True, text=True, env=env)
        assert r.returncode == 0, r.stdout + r.stderr
        plan = json.loads((t / "flip.json").read_text(encoding="utf-8"))
        assert plan["binding"] == {"planId": "abc123", "checkId": "runtime.state-switch-flip"}
        assert plan["arms"] == [{"id": "patched", "profile": "Prof-P", "evalArmId": "patched"}]
        assert switchflip.validate_plan(plan) == []
        bad = list(base)
        bad[bad.index("patched")] = "nosuch"
        r2 = subprocess.run(bad + ["--evalrun-plan", str(t / "plan.json")], capture_output=True, text=True, env=env)
        assert r2.returncode == 2 and "not an arm of evalrun plan" in r2.stderr, r2.stderr


def test_g18_paths_sending_the_same_calls_are_one_group():
    # rejects: a branch that flips nothing (an If with no switch call) doubling every group
    with tempfile.TemporaryDirectory() as tmp:
        text = SCRIPT.replace("Function Fragment_3()\n", "Function Fragment_3()\n  If Ready\n    Ready = False\n  EndIf\n")
        _, doc = F.build([("patched", "Prof-P", census())], SCOPE, scripts_dir(tmp, text))
        frag = [g for g in doc["arms"][0]["groups"] if g["function"] == "Fragment_3"]
        assert len(frag) == 1 and frag[0]["paths"] == 2 and frag[0]["samePaths"] == [2], frag


def test_g20_arrival_is_a_cell_coc_can_reach():
    # rejects: a worldspace's persistent cell (or a label with its "(int)" suffix) handed to coc
    with tempfile.TemporaryDirectory() as tmp:
        cen = census()
        for s in cen["switches"]:
            s["onSet"] += [{"key": "0001FF:Target.esp", "cell": "Tamriel (persistent cell)", "cellKey": "000D74:Skyrim.esm"}] * 3
        plan, _ = F.build([("patched", "Prof-P", cen)], SCOPE, scripts_dir(tmp))
        assert {c["arrival"]["cell"] for c in plan["conditions"]} == {"TownInterior"}, plan["conditions"]


# ---- 310-316: the call rules follow switchprobe's (_shared/ScriptFlipCalls.cs) ----

CS_RULES = Path(F.__file__).resolve().parent / "_shared" / "ScriptFlipCalls.cs"
GATE = "000B01:Target.esp"


def cs_rules():
    """(flip, link-chain, enables, disables, proper-case names) read from the C# definition; each found exactly once."""
    src = CS_RULES.read_text(encoding="utf-8")

    def one(rx, what):
        hits = re.findall(rx, src)
        assert len(hits) == 1, f"{what}: {len(hits)} matches in {CS_RULES}"
        names = re.findall(r'"(\w+)"', hits[0]) if "|" not in hits[0] else hits[0].split("|")
        assert names, f"{what}: no names in {CS_RULES}"
        return names
    flip = {n.lower() for n in one(r"\bFlip\s*=\s*new HashSet<string>\([^)]*\)\s*\{([^}]*)\}", "Flip")}
    chain = {n.lower() for n in one(r"\bLinkChain\s*=\s*new HashSet<string>\([^)]*\)\s*\{([^}]*)\}", "LinkChain")}
    en = {n.lower() for n in one(r"\bbool Enables\(string call\)\s*=>\s*call is ([^;]+);", "Enables")}
    dis = {n.lower() for n in one(r"\bbool Disables\(string call\)\s*=>\s*call is ([^;]+);", "Disables")}
    proper = one(r'const string Names = "([^"]+)"', "Names")
    assert en | dis == flip and not en & dis, f"C# Enables {sorted(en)} / Disables {sorted(dis)} do not split Flip {sorted(flip)}"
    assert {n.lower() for n in proper} == flip | chain, f"C# Names {proper} is not Flip + LinkChain"
    return flip, chain, en, dis, {n.lower(): n for n in proper}


def by_s(script, fn, text, head="quest GateQ"):
    return f"{head} ({script}.{fn}: {text})"


def one_census(*switches):
    sw = list(switches)
    return {"summary": {"switches": len(sw), "triggers": {"triggered": len(sw)}}, "unreadable": [], "switches": sw,
            "jointFlips": []}


def build_one(tmp, cen, script, text):
    d = Path(tmp) / "scripts"
    d.mkdir(exist_ok=True)
    (d / f"{script}.psc").write_text(text, encoding="utf-8")
    return F.build([("patched", "Prof-P", cen)], SCOPE, d)[1]["arms"][0]


def test_c310_every_flip_switchprobe_credits_is_planned_in_its_direction():
    # rejects: a name in switchprobe's flip family that flipplan does not plan, or plans in the wrong direction
    flip, _, en, _, proper = cs_rules()
    wrong = []
    for name in sorted(flip):
        call = proper[name]
        want = "enabled" if name in en else "disabled"
        with tempfile.TemporaryDirectory() as tmp:
            cen = one_census(switch(GATE, "Gate", baseline=want != "enabled", by=[by_s("GateScript", "F", f"Gate.{call}(")]))
            arm = build_one(tmp, cen, "GateScript", f"Function F()\n  Gate.{call}()\nEndFunction\n")
            got = [(c["ref"], c["target"]) for g in arm["groups"] for c in g["calls"]]
            if got != [(GATE, want)]:
                wrong.append(f"{call}: planned {got}, switchprobe credits {want}")
    assert not wrong, "; ".join(wrong)


def test_c311_a_link_chain_call_is_never_a_flip_of_its_object():
    # rejects: Gate.EnableLinkChain() planned as a flip of Gate (it flips Gate's linked chain), or dropped without a word
    _, chain, _, _, proper = cs_rules()
    wrong = []
    for name in sorted(chain):
        call = proper[name]
        with tempfile.TemporaryDirectory() as tmp:
            # a forged census credit: switchprobe never writes one, and flipplan must not trust it if it does
            cen = one_census(switch(GATE, "Gate", baseline=name.startswith("disable"),
                                    by=[by_s("GateScript", "F", f"Gate.{call}(")]))
            arm = build_one(tmp, cen, "GateScript", f"Function F()\n  Gate.{call}()\nEndFunction\n")
            planned = [c for g in arm["groups"] for c in g["calls"]]
            listed = [u for u in arm["unmapped"] if u["call"] == f"Gate.{call}(" and "link" in u["reason"]]
            if planned or len(listed) != 1:
                wrong.append(f"{call}: planned {planned}, listed {arm['unmapped']}")
    assert not wrong, "; ".join(wrong)


def test_c312_tryto_direction_follows_the_state_of_the_path():
    # rejects: TryToEnable read as `disabled` (a startswith("enable") rule), or the source order lost
    door = "000B02:Target.esp"
    with tempfile.TemporaryDirectory() as tmp:
        cen = one_census(
            switch(GATE, "Gate", baseline=True, by=[by_s("GateScript", "F", "Alias_Gate.TryToDisable("),
                                                    by_s("GateScript", "G", "Alias_Gate.TryToDisable("),
                                                    by_s("GateScript", "G", "Alias_Gate.TryToEnableNoWait(")]),
            switch(door, "Door", baseline=True, by=[by_s("GateScript", "F", "Alias_Door.TryToEnable(")]))
        arm = build_one(tmp, cen, "GateScript", "Function F()\n  Alias_Door.TryToEnable()\n  Alias_Gate.TryToDisable()\n"
                                                "EndFunction\n\nFunction G()\n  Alias_Gate.TryToDisable()\n"
                                                "  Alias_Gate.TryToEnableNoWait()\nEndFunction\n")
        f, g = group({"arms": [arm]}, "GateScript.F"), group({"arms": [arm]}, "GateScript.G")
        assert calls(f) == [(GATE, "disabled")], calls(f)
        assert [(x["ref"], x["target"]) for x in f["dropped"]] == [(door, "enabled")] and "already enabled" in f["dropped"][0]["reason"], f["dropped"]
        assert calls(g) == [(GATE, "disabled"), (GATE, "enabled")], calls(g)


def test_c313_a_bare_call_is_a_call_on_self():
    # rejects: a ref script's bare Enable() (switchprobe credits it as Self.Enable()) never planned; a bare link-chain
    # call planned
    lever = "000B03:Target.esp"
    with tempfile.TemporaryDirectory() as tmp:
        cen = one_census(switch(lever, "Lever", baseline=False,
                                by=[by_s("LeverScript", "OnActivate", "Self.Enable()", head="ref Lever")]))
        arm = build_one(tmp, cen, "LeverScript", "Scriptname LeverScript extends ObjectReference\n"
                                                 "Event OnActivate(ObjectReference akActionRef)\n  Enable()\n"
                                                 "  EnableLinkChain()\nEndEvent\n")
        g = group({"arms": [arm]}, "LeverScript.OnActivate")
        assert calls(g) == [(lever, "enabled")], calls(g)
        assert [(u["call"], "link" in u["reason"]) for u in arm["unmapped"]] == [("Self.EnableLinkChain()", True)], arm["unmapped"]


def test_c314_a_whole_line_comment_is_not_a_call():
    # rejects: a commented-out `; Gate.Enable()` read as code (the live call then dropped as already enabled)
    with tempfile.TemporaryDirectory() as tmp:
        cen = one_census(switch(GATE, "Gate", baseline=False, by=[by_s("GateScript", "F", "Gate.Enable(")]))
        arm = build_one(tmp, cen, "GateScript", "Function F()\n  ; Gate.Enable()\n  ;Gate.Disable()\n  Gate.Enable()\nEndFunction\n")
        g = group({"arms": [arm]}, "GateScript.F")
        assert calls(g) == [(GATE, "enabled")] and "line 3:" in g["calls"][0]["source"], g["calls"]
        assert g["dropped"] == [] and arm["unmapped"] == [], (g["dropped"], arm["unmapped"])


RAW = "raw bytecode CALLMETHOD id:TryToEnable id:::Alias_Door9_var id:::NoneVar int:0"


def test_c315_a_raw_bytecode_credit_is_not_planned_with_its_reason():
    # rejects: a flip seen only in a refused function's bytecode left under the generic reason (or planned without its
    # branch structure)
    with tempfile.TemporaryDirectory() as tmp:
        cen = one_census(switch(GATE, "Gate", baseline=False,
                                by=[by_s("GateScript", "Fragment_0", RAW, head="quest GateQ stage 10")]))
        arm = build_one(tmp, cen, "GateScript", "Function Fragment_0()\n  ;   " + RAW[len("raw bytecode "):] + "\nEndFunction\n")
        assert not arm["groups"], arm["groups"]
        np_ = [x for x in arm["notPlanned"] if x["ref"] == GATE]
        assert len(np_) == 1 and "raw bytecode" in np_[0]["reason"] and "GateScript.Fragment_0" in np_[0]["reason"], np_


def test_c316_a_credit_the_source_lacks_keeps_the_generic_reason():
    # guard: 315's rule must not take over a real missing-source case
    with tempfile.TemporaryDirectory() as tmp:
        cen = one_census(switch(GATE, "Gate", baseline=False, by=[by_s("GateScript", "F", "Gate.Enable(")]))
        arm = build_one(tmp, cen, "GateScript", "Function F()\n  Gate.Disable()\nEndFunction\n")
        assert [(x["ref"], x["reason"]) for x in arm["notPlanned"]] == [
            (GATE, "no supplied source line carries its recorded call")], arm["notPlanned"]
        assert [u["call"] for u in arm["unmapped"]] == ["Gate.Disable("], arm["unmapped"]


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
