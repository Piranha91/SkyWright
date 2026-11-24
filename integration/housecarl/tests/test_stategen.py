"""Known answers for the Deep evaluation's state generator (stategen.py) and evalrun's evaluation depth.

    python _geometry-harness/tests/test_stategen.py

G1-G4 drive stategen.generate on synthetic switch censuses (switchprobe --json shape). D1-D5 drive the REAL controller
(evalrun.py plan/run/finalize) on the evalrun fixtures with the fake probes, running only the state checks
(`--select state.conditional-geometry`, which brings its prerequisites): D1 Deep does more than Standard; D2 the depth's
default and precedence; D3 each arm's list comes from its own census; D4 an arm whose census has no switch; D5 the
live check under each depth. No real probe starts and no conflictdiff is needed, so this suite is light.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
FIXT = SW.HARNESS / "tests" / "fixtures" / "evalrun"
EVALRUN = SW.HARNESS / "evalrun.py"
from test_evalrun import CONTROLLER  # noqa: E402  (the built-in controller, runcontrol, as test_evalrun starts it)
EVALSETTINGS = HARNESS / "evalsettings.py"
CASE = "case-01-geometry-and-quest"
PROBES = ["navownprobe", "overlapprobe", "contactprobe", "bosprobe", "switchprobe", "stateprobe",
          "scopeprobe", "adviceprobe", "patchprobe", "driftprobe", "seamprobe", "waterprobe",
          "blockprobe", "depprobe", "fomodprobe", "poolprobe"]
CG, CENSUS, LIVE = "state.conditional-geometry", "state.switch-census", "runtime.state-switch-flip"
BASE_ARM, CAND_ARM = "baseline", "candidate-01-reorder"

sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(SW.SCRIPTS))
import stategen  # noqa: E402

FAILURES, PASSED = [], []


def check(cond, label):
    (PASSED if cond else FAILURES).append(label)
    print(("ok    " if cond else "FAIL  ") + label)


def guarded(label, fn):
    """Run one known answer; an exception (a stub not built yet) is that answer's failure, named, not the suite's end."""
    try:
        fn()
    except Exception as ex:  # noqa: BLE001
        check(False, f"{label}: raised {type(ex).__name__}: {ex}")
        traceback.print_exc(limit=2)


# ------------------------------------------------------------------------------------------ census builder --

def sw(key, edid=None, *, cycle=False, resolved=True, trigger="Triggered"):
    row = {"key": key, "edid": edid, "baseType": "STAT", "baseEdid": "XMarker", "marker": True,
           "owner": key.split(":", 1)[1] if key else "FixtureTarget.esp", "ownerClass": "mod",
           "cell": "FixtureCell (int)", "cellKey": "000D62:FixtureTarget.esp", "interior": True,
           "position": "(0, 0, 0)", "baselineEnabled": True, "baselineViaParent": False,
           "baselineResolved": resolved, "parent": None, "parentOpposite": False, "cycle": cycle, "region": None,
           "onSet": [{"key": "000900:FixtureTarget.esp", "set": "on", "depth": 1, "placer": "FixtureTarget.esp",
                      "attacher": "FixtureTarget.esp", "cell": "FixtureCell (int)",
                      "cellKey": "000D62:FixtureTarget.esp"}],
           "offSet": [],
           "trigger": {"status": trigger, "kind": "quest stage", "by": []}}
    if key is None:
        row.pop("key")
    return row


def census(switches, joint, *, disabled=0, player=0, unplaced=0, unreadable=(), triggers=True):
    return {"generated": "2026-10-10 00:00:00Z", "instance": "fixture://evalrun", "profile": "fixture",
            "plugins": 4, "unreadable": list(unreadable),
            "summary": {"switches": len(switches), "children": len(switches), "descendants": len(switches),
                        "onSet": len(switches), "offSet": 0, "chained": 0,
                        "cycles": sum(1 for s in switches if s.get("cycle")),
                        "playerParentRefs": player, "unplacedParentRefs": unplaced,
                        "disabledSingletons": disabled, "baselineDisabled": 0,
                        "triggers": ({"triggered": len(switches), "boundUnread": 0, "unknown": 0,
                                      "jointFlips": len(joint)} if triggers else None)},
            "perPlugin": [], "switches": switches,
            "jointFlips": [{"stage": k, "switches": v} for k, v in joint.items()]}


A, B, C, E = "000801:FixtureTarget.esp", "000802:FixtureTarget.esp", "000803:FixtureTarget.esp", "000805:FixtureTarget.esp"
F1, F2, G = "000806:FixtureTarget.esp", "000807:FixtureTarget.esp", "000808:FixtureTarget.esp"


def census_g1():
    return census(
        [sw(A, "A_Marker"), sw(B, "B_Marker", cycle=True), sw(C, "C_Marker", resolved=False), sw(None, "D_Marker"),
         sw(E, "E_Marker"), sw(F1, "F_Dup"), sw(F2, "F_Dup"), sw(G, None)],
        {"Q:10": ["A_Marker", "E_Marker"], "Q:20": ["A_Marker"], "Q:30": ["A_Marker", "Nope_Marker"],
         "Q:40": ["E_Marker", "F_Dup"], "Q:50": ["A_Marker", "B_Marker"], "Q:60": [G, "E_Marker"]})


def codes(doc, kind=None):
    return {(r.get("subject"), r.get("code")) for r in doc.get("notExplored") or [] if kind is None or r.get("kind") == kind}


# ------------------------------------------------------------------------------------------------- G1-G4 --

def g1():
    cen = census_g1()
    doc = stategen.generate(cen)
    specs = [s["spec"] for s in doc["states"]]
    check(specs == [A, E, F1, F2, G, "stage:Q:10", "stage:Q:60"],
          f"G1: states are every explorable switch by FormKey, then the explorable joint flips ({specs})")
    singles = codes(doc, "single")
    check(singles == {(B, "chain-cycle"), (C, "chain-unresolved"), ("D_Marker", "no-key")},
          f"G1: B, C and D are not explored, each with its reason ({sorted(singles)})")
    joints = codes(doc, "joint")
    check(joints == {("Q:30", "label-unknown"), ("Q:40", "label-ambiguous"), ("Q:50", "member-not-explorable")},
          f"G1: Q:30, Q:40 and Q:50 are not explored, each with its reason ({sorted(joints)})")
    check([(c.get("stage"), c.get("by")) for c in doc.get("covered") or []] == [("Q:20", A)],
          "G1: a stage flipping one switch is covered by that switch alone, not unexplored")
    check(("combinations", "combinations") in codes(doc, "class"),
          "G1: combinations beyond one stage's joint flip are listed as not enumerated")
    n_single_states = sum(1 for s in doc["states"] if s.get("kind") == "single")
    n_single_rows = sum(int(r.get("count", 1)) for r in doc["notExplored"] if r.get("kind") == "single")
    check(n_single_states + n_single_rows == len(cen["switches"]),
          f"G1: every census switch is a state or a not-explored row ({n_single_states} + {n_single_rows} "
          f"of {len(cen['switches'])}); a switch dropped without a row fails here")
    n_joint = (sum(1 for s in doc["states"] if s.get("kind") == "joint") + len(doc.get("covered") or [])
               + sum(1 for r in doc["notExplored"] if r.get("kind") == "joint"))
    check(n_joint == len(cen["jointFlips"]),
          f"G1: every joint flip is a state, covered or not explored ({n_joint} of {len(cen['jointFlips'])})")
    joint_q10 = next((s for s in doc["states"] if s["spec"] == "stage:Q:10"), {})
    check(joint_q10.get("switches") == [A, E], "G1: a joint state names its switches by FormKey")
    check(all(r.get("reason") and r["reason"].endswith(".") for r in doc["notExplored"]),
          "G1: every not-explored row carries a one-sentence reason")


def g2():
    cen = census_g1()
    t1, t2 = stategen.dumps(stategen.generate(cen)), stategen.dumps(stategen.generate(json.loads(json.dumps(cen))))
    check(t1 == t2, "G2: the same census gives identical bytes")
    h = "000809:FixtureTarget.esp"
    cen2 = census_g1()
    cen2["switches"].append(sw(h, "H_Marker"))
    cen2["summary"]["switches"] += 1
    s1 = set(stategen.specs_text(stategen.generate(cen)).splitlines())
    s2 = set(stategen.specs_text(stategen.generate(cen2)).splitlines())
    check(s2 - s1 == {h} and not (s1 - s2),
          f"G2: censuses differing by one switch give lists differing by exactly it ({sorted(s1 ^ s2)})")


def g3():
    cen = census([sw(A, "A_Marker")], {}, disabled=3, player=2, unplaced=1, unreadable=["Broken.esp"], triggers=False)
    doc = stategen.generate(cen)
    rows = {r.get("code"): r for r in doc["notExplored"] if r.get("kind") == "class"}
    check(rows.get("disabled-singletons", {}).get("count") == 3, "G3: disabled singletons listed with the census count")
    check(rows.get("player-parent", {}).get("count") == 2, "G3: player-parented references listed with their count")
    check(rows.get("unplaced-parent", {}).get("count") == 1, "G3: links to unplaced parents listed with their count")
    check(rows.get("unreadable-plugins", {}).get("count") == 1 and "Broken.esp" in str(rows["unreadable-plugins"]),
          "G3: unreadable plugins listed by name")
    check("no-trigger-map" in rows, "G3: a census without the trigger map says its joint flips are unknown")
    zero = stategen.generate(census([sw(A, "A_Marker")], {}))
    zc = {r.get("code") for r in zero["notExplored"]}
    check(zc == {"combinations"}, f"G3: zero counts write no row but the combinations one ({sorted(zc)})")


def g4():
    doc = stategen.generate(census([], {}))
    check(doc["states"] == [] and {r.get("code") for r in doc["notExplored"]} == {"combinations"},
          "G4: a census with no switch gives no state and only the combinations row")


# ---------------------------------------------------------------------------------------- the controller --

def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def probe_cmds():
    out = []
    for p in PROBES:
        out += ["--probe-cmd", f"{p}=python {FIXT / 'fakeprobes' / p}.py"]
    return out


class Ctl:
    def __init__(self, tmp: Path):
        self.tmp = tmp
        self.gen = tmp / "gen"
        g = subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(self.gen)],
                           capture_output=True, text=True)
        if g.returncode:
            raise RuntimeError("fixtures not generated: " + g.stdout[-500:] + g.stderr[-500:])
        self.case = self.gen / "cases" / CASE
        self.n = 0

    def scenario(self, base_census, cand_census) -> Path:
        scen = load(self.case / "scenario.json")
        scen["arms"]["switchprobe"] = {BASE_ARM: {"behaviour": "ok", "json": base_census},
                                       CAND_ARM: {"behaviour": "ok", "json": cand_census}}
        self.n += 1
        p = self.tmp / f"scenario-{self.n}.json"
        p.write_text(json.dumps(scen, indent=1), encoding="utf-8")
        return p

    def ev(self, args, scen, env_extra=None):
        env = dict(os.environ)
        env["EVALRUN_FAKE_SCENARIO"] = str(scen)
        env.update(env_extra or {})
        return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True,
                              text=True, env=env, timeout=600)

    def plan(self, run, scen, extra=(), registry="fixture-check-registry.json", env_extra=None):
        return self.ev(["plan", "--registry", self.gen / "registry" / registry, "--scope", self.case / "scope.json",
                        "--candidates", self.case / "candidates", "--out", run, "--select", CG]
                       + probe_cmds() + list(extra), scen, env_extra)

    def run_all(self, run, scen):
        r1 = self.ev(["run", "--run", run], scen)
        r2 = self.ev(["finalize", "--run", run], scen)
        return r1, r2


def ledger_status(run, cid, arm):
    e = load(run / "ledger.json")["entries"]
    row = next((v for v in e.values() if v["checkId"] == cid and v["armId"] == arm and not v.get("part")), None)
    return row


def receipt(run, cid, arm):
    """The receipt with `argv` rebuilt from its recorded `argvShape` (`<RUN>/` is the run folder): None when no
    child was launched."""
    p = run / "receipts" / f"{cid}__{arm}.json"
    if not p.exists():
        return None
    rc = load(p)
    shape = rc.get("argvShape")
    rc["argv"] = [str(run / t[6:]) if isinstance(t, str) and t.startswith("<RUN>/") else t
                  for t in shape] if shape else None
    return rc


def argv_value(argv, flag):
    return argv[argv.index(flag) + 1] if argv and flag in argv and argv.index(flag) + 1 < len(argv) else None


def settings_file(path: Path, sections: dict) -> Path:
    doc = {"schemaVersion": 1, "saved": {"utc": "2026-10-10T00:00:00Z", "by": "set", "houseCARL": "test"}}
    doc.update(sections)
    path.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return path


def d1(ctl: Ctl):
    cen = census_g1()
    scen = ctl.scenario(cen, cen)
    std, deep = ctl.tmp / "d1-standard", ctl.tmp / "d1-deep"
    p1 = ctl.plan(std, scen, ["--depth", "standard"])
    p2 = ctl.plan(deep, scen, ["--depth", "deep"])
    check(p1.returncode == 0, f"D1: Standard plan written ({(p1.stdout + p1.stderr)[-300:]!r})")
    check(p2.returncode == 0, f"D1: Deep plan written ({(p2.stdout + p2.stderr)[-300:]!r})")
    if p1.returncode or p2.returncode:
        return
    ctl.run_all(std, scen)
    ctl.run_all(deep, scen)
    std_rows = [ledger_status(std, CG, a) for a in (BASE_ARM, CAND_ARM)]
    check(all(r and r["status"] == "skipped-user" for r in std_rows),
          "D1: Standard accounts the state check skipped-user on every arm")
    std_launched = [a for a in (BASE_ARM, CAND_ARM) if (receipt(std, CG, a) or {}).get("argv")]
    deep_launched, deep_states = [], 0
    for arm in (BASE_ARM, CAND_ARM):
        row = ledger_status(deep, CG, arm)
        check(row and row["status"] == "completed", f"D1: Deep completes the state check on {arm}")
        rc = receipt(deep, CG, arm) or {}
        argv = rc.get("argv") or []
        if argv:
            deep_launched.append(arm)
        sf, sm = argv_value(argv, "--states-file"), argv_value(argv, "--switch-map")
        check(sf is not None and Path(sf).exists(), f"D1: Deep hands stateprobe a states file on {arm}")
        census_out = deep / "outputs" / CENSUS / f"{arm}.json"
        check(sm is not None and Path(sm).resolve() == census_out.resolve(),
              f"D1: Deep hands stateprobe that arm's own census as --switch-map on {arm}")
        if sf and Path(sf).exists():
            got = [s for s in Path(sf).read_text(encoding="utf-8").splitlines() if s.strip()]
            want = [s["spec"] for s in stategen.generate(load(census_out))["states"]]
            check(got == want, f"D1: the states file on {arm} is the generator's list for its census")
            deep_states += len(got)
            check(sha(sf) in json.dumps(rc), f"D1: the receipt on {arm} records the states file's hash")
    disp = load(deep / "disposition.json")
    ne = (disp.get("unassessed") or {}).get("statesNotExplored") or []
    for arm in (BASE_ARM, CAND_ARM):
        check(any(r.get("armId") == arm and r.get("code") == "chain-cycle" and r.get("subject") == B for r in ne),
              f"D1: the disposition lists the generator's not-explored rows for {arm}")
    check(len(deep_launched) > len(std_launched) and deep_states > 0,
          f"D1: Deep does more than Standard (stateprobe launched on {len(deep_launched)} vs {len(std_launched)} "
          f"arms, {deep_states} states judged)")


def d2(ctl: Ctl):
    scen = ctl.scenario(census_g1(), census_g1())
    absent = ctl.tmp / "d2-absent" / "houseCARL.evaluation.json"
    env = {"HOUSECARL_EVAL_SETTINGS": str(absent)}
    cases = [("default", [], None, "deep", "built-in default"),
             ("flag-standard", ["--depth", "standard"], None, "standard", "flag"),
             ("file-standard", [], {"evaluation": {"depth": "standard"}}, "standard", "file"),
             ("flag-beats-file", ["--depth", "deep"], {"evaluation": {"depth": "standard"}}, "deep", "flag")]
    for name, flags, sections, want, source in cases:
        run = ctl.tmp / f"d2-{name}"
        e = dict(env)
        if sections is not None:
            f = settings_file(ctl.tmp / f"d2-{name}.json", sections)
            e["HOUSECARL_EVAL_SETTINGS"] = str(f)
        p = ctl.plan(run, scen, flags, env_extra=e)
        if p.returncode:
            check(False, f"D2 {name}: plan written ({(p.stdout + p.stderr)[-300:]!r})")
            continue
        pl = load(run / "plan.json")
        got = (pl.get("states") or {}).get("depth")
        src = (((pl.get("explicitDecisions") or {}).get("settings") or {}).get("options") or {}) \
            .get("evaluation.depth", {}).get("source")
        check(got == want and src == source, f"D2 {name}: depth {got!r} from {src!r} (want {want!r} from {source!r})")


def d3(ctl: Ctl):
    base = census_g1()
    cand = census_g1()
    cand["switches"] = [s for s in cand["switches"] if s.get("key") != E]
    cand["summary"]["switches"] -= 1
    scen = ctl.scenario(base, cand)
    run = ctl.tmp / "d3"
    p = ctl.plan(run, scen, ["--depth", "deep"])
    if p.returncode:
        check(False, f"D3: plan written ({(p.stdout + p.stderr)[-300:]!r})")
        return
    ctl.run_all(run, scen)
    lists = {}
    for arm in (BASE_ARM, CAND_ARM):
        sf = argv_value((receipt(run, CG, arm) or {}).get("argv") or [], "--states-file")
        lists[arm] = set(Path(sf).read_text(encoding="utf-8").split()) if sf and Path(sf).exists() else None
    check(lists[BASE_ARM] is not None and E in lists[BASE_ARM] and "stage:Q:10" in lists[BASE_ARM],
          "D3: the baseline's list carries E and its stage")
    check(lists[CAND_ARM] is not None and E not in lists[CAND_ARM] and "stage:Q:10" not in lists[CAND_ARM],
          "D3: the candidate's list, from its own census, lacks E and E's stage (no borrowing)")


def d4(ctl: Ctl):
    scen = ctl.scenario(census_g1(), census([], {}))
    run = ctl.tmp / "d4"
    p = ctl.plan(run, scen, ["--depth", "deep"])
    if p.returncode:
        check(False, f"D4: plan written ({(p.stdout + p.stderr)[-300:]!r})")
        return
    ctl.run_all(run, scen)
    row = ledger_status(run, CG, CAND_ARM) or {}
    detail = ((receipt(run, CG, CAND_ARM) or {}).get("result") or {}).get("note") or ""
    check(row.get("status") == "not-applicable" and "no switch" in detail,
          f"D4: an arm whose census has no switch is accounted 'no state to judge' ({row.get('status')})")
    check(not (receipt(run, CG, CAND_ARM) or {}).get("argv"), "D4: no stateprobe is launched for it")
    missing = ((load(run / "disposition.json").get("evaluation") or {}).get("missing") or [])
    check(not any(m.get("checkId") == CG and m.get("armId") == CAND_ARM for m in missing),
          "D4: it is not listed missing")


def d5(ctl: Ctl):
    scen = ctl.scenario(census_g1(), census_g1())
    paths = {"instance": str(ctl.tmp / "scratch-instance"), "shortcut": str(ctl.tmp / "game.lnk"),
             "gameExe": str(ctl.tmp / "game.exe")}
    deep = settings_file(ctl.tmp / "d5-deep.json",
                         {"evaluation": {"depth": "deep", "liveSwitchCheck": "always"}, "liveSwitchCheck": paths})
    run = ctl.tmp / "d5-deep"
    p = ctl.plan(run, scen, registry="fixture-check-registry-live.json",
                 env_extra={"HOUSECARL_EVAL_SETTINGS": str(deep)})
    if p.returncode:
        check(False, f"D5: Deep with the live check always plans ({(p.stdout + p.stderr)[-300:]!r})")
    else:
        row = next((c for c in load(run / "plan.json")["checks"] if c["checkId"] == LIVE), None)
        check(row is not None and row["required"] is True
              and {a["status"] for a in row["arms"]} <= {"live-import", "not-applicable"},
              "D5: Deep with the live check always plans it opted in and required")
    for live in ("always", "ask"):
        std = settings_file(ctl.tmp / f"d5-standard-{live}.json",
                            {"evaluation": {"depth": "standard", "liveSwitchCheck": live}, "liveSwitchCheck": paths})
        p = ctl.plan(ctl.tmp / f"d5-standard-{live}", scen, registry="fixture-check-registry-live.json",
                     env_extra={"HOUSECARL_EVAL_SETTINGS": str(std)})
        text = p.stdout + p.stderr
        check(p.returncode == 2 and "Standard" in text and "live switch check" in text and str(std) in text,
              f"D5: Standard with the live check {live} is refused at plan in one sentence naming the file")
        v = subprocess.run([sys.executable, str(EVALSETTINGS), "validate", str(std)], capture_output=True, text=True)
        check(v.returncode == 2 and "Standard" in (v.stdout + v.stderr),
              f"D5: evalsettings validate refuses Standard with the live check {live}")


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="stategen-"))
    os.environ["HOUSECARL_EVAL_SETTINGS"] = str(tmp / "no-settings" / "houseCARL.evaluation.json")
    try:
        for label, fn in (("G1", g1), ("G2", g2), ("G3", g3), ("G4", g4)):
            guarded(label, fn)
        ctl = Ctl(tmp)
        for label, fn in (("D1", d1), ("D2", d2), ("D3", d3), ("D4", d4), ("D5", d5)):
            guarded(label, lambda fn=fn: fn(ctl))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
