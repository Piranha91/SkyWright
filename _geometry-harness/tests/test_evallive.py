"""Known answers for the opt-in live-import lane (evalrun `--opt-in`, `not-opted-in`, evallive.py).

    python _geometry-harness/tests/test_evallive.py

Drives the REAL controller on the evalrun fixtures (generated into a temporary folder by make_fixtures.py, with the
fake probes): K14 the default is unchanged when the check is not opted in; K15 opting in without a receipt keeps the
evaluation incomplete; K13 an import is refused for each named wrong input and accepted only when its launches re-read
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
    return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True, text=True,
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

        # K13: imports ---------------------------------------------------------------------------------------
        arms = [a for a in plo["arms"] if a["armId"] in
                [x["armId"] for x in rowo["arms"] if x["status"] == "live-import"]]
        check(len(arms) >= 2, "K13: two arms wait for a live receipt")
        a0, a1 = arms[0], arms[1]
        k = tmp / "imports"

        def refused(tag, manifest, needle, run=None):
            r = imp(run or opt, manifest)
            out = r.stdout + r.stderr
            check(r.returncode == 2 and needle in out, f"K13: {tag} refused ({needle})"
                  + ("" if r.returncode == 2 and needle in out else f" -- got {r.returncode}: {out.strip()[-300:]}"))

        refused("foreign plan", make_import(k / "foreign", plo, a0, ["crash", "crash"], plan_id="000000000000"),
                "foreign-run")
        refused("launched for another arm", make_import(k / "otherarm", plo, a0, ["crash", "crash"],
                                                        bind_arm=a1["armId"]), "foreign-run")

        def wrong_hash(cfg):
            cfg["plugins"][0]["sha256"] = "0" * 64
        refused("fingerprint not the arm's request", make_import(k / "fp", plo, a0, ["crash", "crash"],
                                                                 cfg_mutate=wrong_hash), "identity-mismatch")
        refused("tampered events", make_import(k / "tamper", plo, a0, ["crash", "crash"], tamper_events=True),
                "output-stale")
        refused("stored label over crash events",
                make_import(k / "label", plo, a0, ["crash", "crash"], stored={"outcome": "complete", "branch": None}),
                "never preferred")
        refused("unsettled (one crash, one completion)", make_import(k / "unsettled", plo, a0, ["crash", "complete"],
                                                                     detector="finding"), "unsettled")
        refused("one crash only", make_import(k / "one", plo, a0, ["crash"], detector="finding"), "unsettled")
        refused("detector contradicting the launches",
                make_import(k / "contra", plo, a0, ["crash", "crash"], detector="no-finding", findings=0),
                "re-read as finding")
        not_opted_arm = next(a for a in pl["arms"])
        refused("row not opted in", make_import(k / "notopted", pl, not_opted_arm, ["crash", "crash"]),
                "not opted in", run=live)

        good = imp(opt, make_import(k / "good-fail", plo, a0, ["crash", "crash"]))
        check(good.returncode == 0, "K13: two re-read crashes import as a finding"
              + ("" if good.returncode == 0 else f" -- {(good.stdout + good.stderr)[-400:]}"))
        good2 = imp(opt, make_import(k / "good-ok", plo, a1, ["complete", "complete"]))
        check(good2.returncode == 0, "K13: two re-read completions import as no finding"
              + ("" if good2.returncode == 0 else f" -- {(good2.stdout + good2.stderr)[-400:]}"))
        dup = imp(opt, make_import(k / "dup", plo, a0, ["crash", "crash"]))
        check(dup.returncode == 2 and "duplicate-receipt" in dup.stdout + dup.stderr, "K13: a second import is refused")
        led = load(opt / "ledger.json")["entries"]
        e0 = next(e for e in led.values() if e["checkId"] == CID and e["armId"] == a0["armId"])
        e1 = next(e for e in led.values() if e["checkId"] == CID and e["armId"] == a1["armId"])
        check((e0["status"], e0.get("detector")) == ("completed", "finding"), "K13: arm 0 ledger completed/finding")
        check((e1["status"], e1.get("detector")) == ("completed", "no-finding"), "K13: arm 1 ledger completed/no-finding")
        scen = gen / "cases" / CASE / "scenario.json"
        ev(["finalize", "--run", opt], scen)
        dfin = disposition(opt)
        findings = (dfin.get("evaluation") or {}).get("findings") or []
        check(any(f.get("checkId") == CID and f.get("armId") == a0["armId"] for f in findings),
              "K13: finalize carries the live finding")
        check("accepted live receipts" in str((dfin.get("unassessed") or {}).get("runtimeObservations")),
              "K13: finalize no longer says no runtime observation was supplied")
    except Exception:
        traceback.print_exc()
        FAILURES.append("exception")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
