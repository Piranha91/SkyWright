"""Known answers for the opt-in live-import lane (evalrun `--opt-in`, `not-opted-in`, evallive.py).

    python _geometry-harness/tests/test_evallive.py

Drives the REAL controller on the evalrun fixtures (generated into a temporary folder by make_fixtures.py, with the
fake probes), on the built-in controller: K14 the default is unchanged when the check is not opted in; K15 opting in
without a receipt keeps the evaluation incomplete; K13 an import is refused in one sentence, since the built-in controller
does not read a live driver's export until evallive.py is ported (the earlier K13, refusals by wrong input and acceptance
when its launches re-read, and K28 judged the Python controller's import, which is gone)
to a settled reading. Synthetic launch receipts only; a recorded lab receipt is replayed outside the tracked tree.

conflictdiff is built from this checkout's source once per run (test_evalrun.use_conflictdiff), or taken as given
from the tree named by HOUSECARL_EVALRUN_TEST_HARNESS; a failed build fails the run.
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
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
FIXT = HERE / "fixtures" / "evalrun"
EVALRUN = HARNESS / "evalrun.py"
PROBES = ["navownprobe", "overlapprobe", "contactprobe", "bosprobe", "switchprobe", "stateprobe",
          "scopeprobe", "adviceprobe", "patchprobe", "driftprobe", "seamprobe", "waterprobe",
          "blockprobe", "depprobe", "fomodprobe", "poolprobe"]
CID = "runtime.state-switch-flip"
CASE = "case-01-geometry-and-quest"

sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HARNESS.parent / "scripts"))
import evalrun as E  # noqa: E402  (pure helpers only: configuration_fingerprint)
sys.path.insert(0, str(HERE))
import test_evalrun as T  # noqa: E402  (use_conflictdiff: this run's conflictdiff)

FAILURES, PASSED = [], []


def check(cond, label):
    (PASSED if cond else FAILURES).append(label)
    print(("ok    " if cond else "FAIL  ") + label)


def ev(args, scenario=None):
    env = dict(os.environ)
    if scenario:
        env["EVALRUN_FAKE_SCENARIO"] = str(scenario)
    return subprocess.run(T.CONTROLLER + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8", errors="replace",
                          env=env, timeout=600)


def probe_cmds():
    out = []
    for p in PROBES:
        out += ["--probe-cmd", f"{p}=python {FIXT / 'fakeprobes' / p}.py"]
    return out


def plan(gen, run, registry, extra=()):
    d = gen / "cases" / CASE
    r = ev(["plan", "--registry", gen / "registry" / registry, "--scope", d / "scope.json",
            "--candidates", d / "candidates", "--out", run, "--harness", T.REAL_HARNESS] + probe_cmds() + list(extra),
           d / "scenario.json")
    if r.returncode:
        print(r.stdout[-2000:], r.stderr[-2000:])
    return r


def run_and_finalize(gen, run):
    scen = gen / "cases" / CASE / "scenario.json"
    r1 = ev(["run", "--run", run], scen)
    r2 = ev(["finalize", "--run", run], scen)
    return r1, r2


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def disposition(run):
    return load(run / "disposition.json")


def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def launched(rc) -> bool:
    """Whether a receipt records a launched child: receipts store the argv as `argvShape`, never as `argv`."""
    return bool(rc.get("argvShape"))


def plan_check(pl, cid):
    return next((c for c in pl["checks"] if c["checkId"] == cid), None)


# ------------------------------------------------------------------------------------- synthetic launches --

def events_crash(t0):
    return [{"kind": "call", "name": "m0", "ok": True, "t": t0 + 1},
            {"kind": "readback", "name": "m0", "ok": True, "t": t0 + 1.3, "target_reached": True},
            {"kind": "alive", "exe": False, "t": t0 + 5},
            {"kind": "crashlog", "path": "crash.log", "mtime": t0 + 4}]


def events_complete(t0):
    ev_ = [{"kind": "call", "name": "m0", "ok": True, "t": t0 + 1},
           {"kind": "readback", "name": "m0", "ok": True, "t": t0 + 1.3, "target_reached": True}]
    ev_ += [{"kind": "heartbeat", "ok": True, "t": t0 + 2 + i} for i in range(62)]
    return ev_ + [{"kind": "alive", "exe": True, "t": t0 + 65}]


def configuration(arm, mutate=None):
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": req["providers"], "overlays": req["overlays"], "population": req["population"],
           "state": "baseline"}
    if mutate:
        mutate(cfg)
    cfg["fingerprint"] = E.configuration_fingerprint(cfg["plugins"], cfg["providers"], cfg["overlays"],
                                                     cfg["population"], cfg["state"], schema=1)
    return cfg


def make_import(folder: Path, pl: dict, arm: dict, kinds, *, plan_id=None, bind_arm=None, stored=None,
                cfg_mutate=None, tamper_events=False, detector=None, findings=None):
    """kinds: one 'crash' / 'complete' per launch. Returns the import manifest path."""
    folder.mkdir(parents=True, exist_ok=True)
    flip = folder / "flipplan.json"
    flip.write_text(json.dumps({"schemaVersion": 1, "conditions": [{"id": "C1"}]}), encoding="utf-8")
    cfg = configuration(arm, cfg_mutate)
    started = (datetime.now(timezone.utc) + timedelta(seconds=1)).isoformat()
    outputs = [{"role": "flip-plan", "path": flip.name, "sha256": sha(flip)}]
    for i, kind in enumerate(kinds, start=1):
        t0 = 1_000_000.0 + 1000 * i
        events = events_crash(t0) if kind == "crash" else events_complete(t0)
        ep = folder / f"C1-{i}.events.jsonl"
        ep.write_text("".join(json.dumps(e) + "\n" for e in events), encoding="utf-8")
        cls = ({"outcome": "fail", "branch": "crash"} if kind == "crash" else {"outcome": "complete", "branch": None})
        if stored and i == 1:
            cls = stored
        rc = {"schemaVersion": 1, "condition": "C1", "replicate": i,
              "binding": {"planId": plan_id or pl["planId"], "checkId": CID, "armId": bind_arm or arm["armId"]},
              "launchStartedUtc": started, "flipPlanSha256": sha(flip), "flipStart": t0, "windowSeconds": 60,
              "configuration": cfg, "eventsSha256": sha(ep), "classification": cls}
        rp = folder / f"C1-{i}.receipt.json"
        rp.write_text(json.dumps(rc), encoding="utf-8")
        outputs += [{"role": "launch-receipt", "path": rp.name, "sha256": sha(rp)},
                    {"role": "events", "path": ep.name, "sha256": sha(ep)}]
        if tamper_events and i == 1:
            ep.write_text(ep.read_text(encoding="utf-8") + "\n", encoding="utf-8")
    n_fail = sum(1 for k in kinds if k == "crash")
    det = detector or ("finding" if n_fail >= 2 else "no-finding")
    doc = {"schemaVersion": 1, "planId": plan_id or pl["planId"], "checkId": CID, "armId": arm["armId"],
           "capturedUtc": datetime.now(timezone.utc).isoformat(), "tool": {"name": "switchflip", "version": "test"},
           "state": "baseline", "configuration": cfg,
           "result": {"detector": det, "findings": findings if findings is not None else (1 if det == "finding" else 0),
                      "coverage": [{"Population": "switch-flip launches: C1", "Judged": len(kinds),
                                    "Unjudgeable": {}}]},
           "outputs": outputs}
    m = folder / "import.json"
    m.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return m


def imp(run, manifest):
    return ev(["import-results", "--run", run, "--file", manifest])


# ------------------------------------------------------------------------------------------------ cases --

def main():
    tmp = Path(tempfile.mkdtemp(prefix="evallive-"))
    T.pin_standard_settings(tmp)
    try:
        try:
            T.use_conflictdiff(tmp)
        except SystemExit as ex:
            print(ex)
            check(False, "conflictdiff built for this run")
            return 1
        print(T.DIFF_SOURCE)
        print(T.diff_exe_identity())
        gen = tmp / "gen"
        g = subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen)],
                           capture_output=True, text=True)
        if g.returncode:
            print(g.stdout, g.stderr)
            check(False, "fixtures generated")
            return 1

        # K14: the default is unchanged when the check is not opted in -----------------------------------------
        base, live = tmp / "base", tmp / "live-not-opted"
        check(plan(gen, base, "fixture-check-registry.json").returncode == 0, "K14: plan without the check")
        check(plan(gen, live, "fixture-check-registry-live.json").returncode == 0,
              "K14: plan with the opt-in check, no --opt-in")
        pl = load(live / "plan.json")
        row = plan_check(pl, CID)
        check(row is not None and {a["status"] for a in row["arms"]} == {"not-opted-in"} and row["required"] is False,
              "K14: the check is planned not-opted-in on every arm, not required")
        check("optIn" not in pl["explicitDecisions"], "K14: no optIn decision recorded when none was made")
        for r in (base, live):
            run_and_finalize(gen, r)
        d0, d1 = disposition(base), disposition(live)
        ev0, ev1 = d0.get("evaluation") or {}, d1.get("evaluation") or {}
        check(ev0.get("claim") == ev1.get("claim") and ev0.get("complete") == ev1.get("complete"),
              f"K14: same claim and completeness with and without the not-opted-in check "
              f"({ev0.get('claim')}/{ev0.get('complete')} vs {ev1.get('claim')}/{ev1.get('complete')})")
        miss1 = [m for m in (ev1.get("missing") or []) if m.get("checkId") == CID]
        check(not miss1, "K14: the not-opted-in check is never missing (non-blocking)")
        check(len(ev0.get("missing") or []) == len(ev1.get("missing") or []),
              "K14: the missing list is the same length")
        un = (d1.get("unassessed") or {})
        check(un.get("notOptedIn") == [CID], "K14: the disposition lists the check under notOptedIn")
        led = load(live / "ledger.json")["entries"]
        rows = [e for e in led.values() if e["checkId"] == CID]
        check(rows and all(e["status"] == "skipped-user" for e in rows),
              "K14: the ledger accounts it skipped-user (opt-in, not requested)")
        rcs = [load(p) for p in (live / "receipts").glob(f"{CID}*.json")]
        check(bool(rcs) and not any(launched(rc) for rc in rcs),
              "K14: no child process was launched for it (no receipt records an argvShape)")
        planted = dict(rcs[0]) if rcs else {}
        planted["argvShape"] = ["<abs>/stateprobe.exe", "--emit", "<RUN>/outputs/planted.json"]
        check(launched(planted) and not planted.get("argv"),
              "K14 (planted launch): a receipt recording a launch is read as one, where the old argv read saw none")

        # K15: opted in without a receipt keeps the evaluation incomplete ---------------------------------------
        opt = tmp / "opted"
        check(plan(gen, opt, "fixture-check-registry-live.json", ["--opt-in", CID]).returncode == 0,
              "K15: plan with --opt-in")
        plo = load(opt / "plan.json")
        rowo = plan_check(plo, CID)
        check(rowo["required"] is True and {a["status"] for a in rowo["arms"]} <= {"live-import", "not-applicable"},
              "K15: opted in, the check is required for this run and waits as live-import")
        check(plo["explicitDecisions"].get("optIn") == [CID], "K15: the optIn decision is recorded")
        run_and_finalize(gen, opt)
        do = disposition(opt).get("evaluation") or {}
        check(do.get("complete") is False and any(m.get("checkId") == CID for m in do.get("missing") or []),
              "K15: no receipt -> incomplete, naming the check")

        bad = ev(["plan", "--registry", gen / "registry" / "fixture-check-registry-live.json",
                  "--scope", gen / "cases" / CASE / "scope.json", "--candidates", gen / "cases" / CASE / "candidates",
                  "--out", tmp / "bad", "--opt-in", "state.switch-census"] + probe_cmds())
        check(bad.returncode == 2 and "not opt-in" in (bad.stdout + bad.stderr),
              "K15: --opt-in on a default check is refused by name")

        # K13: importing a live receipt is not available on the built-in engine until evallive.py is ported ----------
        arms = [a for a in plo["arms"] if a["armId"] in
                [x["armId"] for x in rowo["arms"] if x["status"] == "live-import"]]
        check(len(arms) >= 2, "K13: two arms wait for a live receipt")
        r13 = imp(opt, make_import(tmp / "imports" / "good-fail", plo, arms[0], ["crash", "crash"]))
        out13 = r13.stdout + r13.stderr
        check(r13.returncode == 2 and "is not available on the built-in engine until evallive.py's validator is ported" in out13,
              "K13: a live receipt's import is refused in one sentence saying it is not available yet"
              + ("" if r13.returncode == 2 else f" -- got {r13.returncode}: {out13.strip()[-300:]}"))
        led = load(opt / "ledger.json")["entries"]
        check(all(e["status"] == "live-import" for e in led.values() if e["checkId"] == CID),
              "K13: and every arm still waits for its live receipt")

        # K26/K28: the arm's own accepted census answers the live check (switchflip_scope) --------------------
        def case_with(name, switches):
            d = tmp / name
            shutil.copytree(gen / "cases" / CASE, d)
            sc = load(d / "scope.json")
            for t in sc["targets"]:
                t["footprint"]["placed"]["cells"] = [{"cell": "00A001:FixtureTarget.esp", "kind": "interior"}]
            (d / "scope.json").write_text(json.dumps(sc, indent=1), encoding="utf-8")
            scen_ = load(d / "scenario.json")
            if scen_.get("scopeFrom"):
                scen_["scopeFrom"] = str(d / "scope.json")
            scen_["arms"]["switchprobe"] = {"*": {"behaviour": "ok", "judged": 42, "switches": switches,
                                                  "triggers": {"triggered": 2, "boundUnread": 1, "unknown": 0}}}
            (d / "scenario.json").write_text(json.dumps(scen_, indent=1), encoding="utf-8")
            return d

        def base_switch(key, status, cell="00B002:FixtureBase.esp"):
            return {"key": key, "edid": key, "owner": "FixtureBase.esp", "cellKey": cell,
                    "onSet": [{"key": "000111:FixtureBase.esp", "placer": "FixtureBase.esp",
                               "attacher": "FixtureBase.esp", "cellKey": cell}], "offSet": [],
                    "trigger": {"status": status, "kind": "quest stage", "by": []}}

        def plan_run(d, run):
            r = ev(["plan", "--registry", gen / "registry" / "fixture-check-registry-live.json",
                    "--scope", d / "scope.json", "--candidates", d / "candidates", "--out", run,
                    "--harness", T.REAL_HARNESS, "--opt-in", CID] + probe_cmds(), d / "scenario.json")
            if r.returncode:
                print(r.stdout[-2000:], r.stderr[-2000:])
            ev(["run", "--run", run], d / "scenario.json")
            ev(["finalize", "--run", run], d / "scenario.json")
            return r.returncode

        vanilla = [base_switch("000A01:FixtureBase.esp", "Triggered"), base_switch("000A02:FixtureBase.esp", "Triggered"),
                   base_switch("000A03:FixtureBase.esp", "BoundUnread")]
        na_case, na_run = case_with("case-na", vanilla), tmp / "run-na"
        check(plan_run(na_case, na_run) == 0, "K26: plan with --opt-in on a census whose driven switches touch no target")
        led = load(na_run / "ledger.json")["entries"]
        rows = [e for e in led.values() if e["checkId"] == CID]
        check(bool(rows) and all(e["status"] == "not-applicable" for e in rows),
              "K26: every opted-in arm is accounted not-applicable from its own census"
              + ("" if rows and all(e["status"] == "not-applicable" for e in rows)
                 else f" -- got {[e['status'] for e in rows]}"))
        rcs = [load(p) for p in (na_run / "receipts").glob(f"{CID}__*.json")]
        check(bool(rcs) and all(((rc.get("switchScope") or {}).get("census") or {}).get("sha256") for rc in rcs)
              and all("nothing to flip" in str((rc.get("result") or {}).get("note")) for rc in rcs),
              "K26: each receipt records the census it read and says why")
        dna = disposition(na_run).get("evaluation") or {}
        check(not any(m.get("checkId") == CID for m in dna.get("missing") or []),
              "K26: finalize counts the not-applicable rows as answered (the opted-in check is not missing)")

        ctl_case, ctl_run = case_with("case-open", vanilla + [base_switch("000A0C:FixtureBase.esp", "BoundUnread",
                                                                         cell="00A001:FixtureTarget.esp")]), tmp / "run-open"
        check(plan_run(ctl_case, ctl_run) == 0, "K26 control: plan with a bound switch in a target's cell")
        led2 = load(ctl_run / "ledger.json")["entries"]
        rows2 = [e for e in led2.values() if e["checkId"] == CID]
        check(bool(rows2) and all(e["status"] == "live-import" for e in rows2),
              "K26 control: a script-driven switch in a target's cell keeps every arm waiting for a live receipt")
        dctl = disposition(ctl_run).get("evaluation") or {}
        check(any(m.get("checkId") == CID for m in dctl.get("missing") or []),
              "K26 control: and finalize still names the check missing")

    except Exception:
        traceback.print_exc()
        FAILURES.append("exception")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
