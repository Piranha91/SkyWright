"""Known answers for the opt-in live-import lane (`--opt-in`, `not-opted-in`, evalcontrol import's LiveImport).

    python integration/housecarl/tests/test_evallive.py

The live switch check (`runtime.state-switch-flip`) is an AI-side row: this module's ai-checks.json holds it, and the
overlay evalcontrol plans over a finished app scan takes it only with `--opt-in` (moved from SkyWright's suites with the
check, step 3 of the boundary; the cases keep their labels). Drives SkyWright's REAL controller on the evalrun fixtures
(generated into a temporary folder by make_fixtures.py, with the fake probes) for the app scan, run, finalized and
bundled, and this module's evalcontrol for the overlay over it: K14 the default is unchanged when the check is not
opted in; K15 opting in without a receipt keeps the evaluation incomplete; K13 an import is refused for each named wrong
input and accepted only when its launches re-read to a settled reading; K26 the arm's census (the app scan's, read from
its bundle) answers the check; K28 an import for an arm the census answered is refused by name. Two checks carry the
names of the app plan's cases they replace: "plan command: opt in to the live check" and "plan command: a time limit
and the live check opted in by flag". Synthetic launch receipts only; a recorded lab receipt is replayed outside the
tracked tree.

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
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is
import module_fakes  # noqa: E402  the module's own probe stand-ins
HARNESS = SW.HARNESS
FIXT = HARNESS / "tests" / "fixtures" / "evalrun"
EVALCONTROL = SW.tool("evalcontrol")
PROBES = ["navownprobe", "overlapprobe", "contactprobe", "bosprobe", "switchprobe", "stateprobe",
          "scopeprobe", "patchprobe", "driftprobe", "seamprobe", "waterprobe",
          "blockprobe", "depprobe", "fomodprobe", "poolprobe"]
CID = "runtime.state-switch-flip"
CASE = "case-01-geometry-and-quest"
REGISTRY = "fixture-check-registry.json"

sys.path.append(str(HARNESS))
sys.path.append(str(HARNESS.parent / "scripts"))
import evalrun as E  # noqa: E402  (pure helpers only: configuration_fingerprint)
sys.path.append(str(HARNESS / "tests"))
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


def ec(args):
    return subprocess.run([str(EVALCONTROL)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=600)


def probe_cmds():
    out = []
    for p in PROBES:
        out += ["--probe-cmd", f"{p}=python {FIXT / 'fakeprobes' / p}.py"]
    return out


def plan(gen, run, extra=(), d=None):
    """The app scan's plan, on the fixture registry (no AI-side row)."""
    d = d or gen / "cases" / CASE
    r = ev(["plan", "--registry", gen / "registry" / REGISTRY, "--scope", d / "scope.json",
            "--candidates", d / "candidates", "--out", run, "--harness", T.REAL_HARNESS] + probe_cmds() + list(extra),
           d / "scenario.json")
    if r.returncode:
        print(r.stdout[-2000:], r.stderr[-2000:])
    return r


def app_through_bundle(run, scen):
    """The app scan run, finalized and bundled (no names step) into RUN/app-bundle."""
    ev(["run", "--run", run], scen)
    ev(["finalize", "--run", run], scen)
    r = ev(["bundle", "--run", run, "--out", run / "app-bundle", "--no-names"], scen)
    if r.returncode:
        print(r.stdout[-1500:], r.stderr[-1500:])
    return run / "app-bundle"


def overlay(run, opt_in=(), name="ai", list_file=None):
    """The AI-side overlay over the app scan's bundle: planned (with --opt-in for each id) and evaluated. Returns
    (the plan's exit, the overlay's run folder)."""
    ai = run / name
    args = ["plan", "--app", run, "--app-bundle", run / "app-bundle", "--out", ai]
    args += module_fakes.probe_cmds(list_file)
    for o in opt_in:
        args += ["--opt-in", o]
    if list_file:
        args += ["--list", list_file]
    p = ec(args)
    if p.returncode:
        print(p.stdout[-1500:], p.stderr[-1500:])
        return p.returncode, ai
    e = ec(["evaluate", "--run", ai])
    if e.returncode:
        print(e.stdout[-1500:], e.stderr[-1500:])
    return p.returncode, ai


def finalize(ai):
    return ec(["finalize", "--run", ai])


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


def imp(ai, manifest):
    return ec(["import", "--run", ai, "--file", manifest])


def list_without_live(path: Path) -> Path:
    """The shipped ai-checks.json less the live row: K14's overlay without the check."""
    lst = load(SW.MODULE / "evalcontrol" / "ai-checks.json")
    lst["checks"] = [c for c in lst["checks"] if c["id"] != CID]
    used = {c["family"] for c in lst["checks"]}
    lst["families"] = [f for f in lst["families"] if f["id"] in used]
    path.write_text(json.dumps(lst, indent=2), encoding="utf-8")
    return path


# ------------------------------------------------------------------------------------------------ cases --

def main():
    tmp = Path(tempfile.mkdtemp(prefix="evallive-"))
    T.pin_standard_settings(tmp)
    try:
        if not EVALCONTROL.is_file():
            check(False, f"evalcontrol is built at {EVALCONTROL}")
            return 1
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
        scen = gen / "cases" / CASE / "scenario.json"

        # K14: the default is unchanged when the check is not opted in -----------------------------------------
        app = tmp / "app"
        check(plan(gen, app).returncode == 0, "K14: plan without the check")
        app_through_bundle(app, scen)
        rb, base = overlay(app, name="ai-base", list_file=list_without_live(tmp / "ai-checks-no-live.json"))
        rl, live = overlay(app, name="ai-live-not-opted")
        check(rb == 0 and rl == 0, "K14: plan with the opt-in check, no --opt-in")
        pl = load(live / "plan.json")
        row = plan_check(pl, CID)
        check(row is not None and {a["status"] for a in row["arms"]} == {"not-opted-in"} and row["required"] is False,
              "K14: the check is planned not-opted-in on every arm, not required")
        check("optIn" not in pl["explicitDecisions"], "K14: no optIn decision recorded when none was made")
        for r in (base, live):
            finalize(r)
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
        ro, opt = overlay(app, [CID], name="ai-opted")
        check(ro == 0, "K15: plan with --opt-in")
        plo = load(opt / "plan.json")
        rowo = plan_check(plo, CID)
        check(rowo["required"] is True and {a["status"] for a in rowo["arms"]} <= {"live-import", "not-applicable"},
              "K15: opted in, the check is required for this run and waits as live-import")
        check(plo["explicitDecisions"].get("optIn") == [CID], "K15: the optIn decision is recorded")
        check(ro == 0 and "live-import" in {a["status"] for a in rowo["arms"]},
              "plan command: opt in to the live check")
        finalize(opt)
        do = disposition(opt).get("evaluation") or {}
        check(do.get("complete") is False and any(m.get("checkId") == CID for m in do.get("missing") or []),
              "K15: no receipt -> incomplete, naming the check")

        bad = ec(["plan", "--app", app, "--app-bundle", app / "app-bundle", "--out", tmp / "bad",
                  "--opt-in", "records.cell-attributes"])
        check(bad.returncode == 2 and "not opt-in" in (bad.stdout + bad.stderr),
              "K15: --opt-in on a default check is refused by name")

        timed = tmp / "app-timed"
        check(plan(gen, timed, ["--budget-minutes", "45"]).returncode == 0, "K15: the app plan with a time limit")
        app_through_bundle(timed, scen)
        rt, ait = overlay(timed, [CID], name="ai-timed")
        plt = load(ait / "plan.json") if (ait / "plan.json").is_file() else {}
        rowt = plan_check(plt, CID) or {}
        check(rt == 0 and (plt.get("explicitDecisions") or {}).get("optInSource") == {CID: "flag"}
              and "live-import" in {a["status"] for a in rowt.get("arms") or []}
              and (load(timed / "plan.json").get("budgets") or {}).get("minutes") == 45,
              "plan command: a time limit and the live check opted in by flag")

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
        # K13s: what an accepted live import writes, strictly: its receipt, its ledger entry, and the imports folder (the
        # document, then each output in role order flip-plan, launch receipts, events, each a byte copy)
        key0 = f"{CID}__{a0['armId']}"
        src0 = k / "good-fail"
        rc0 = load(opt / "receipts" / f"{key0}.json") if (opt / "receipts" / f"{key0}.json").is_file() else {}
        cons, res0, imp0 = rc0.get("consumed") or {}, rc0.get("result") or {}, rc0.get("imported") or {}
        check((rc0.get("adapter") or {}) == {"kind": "live-import", "sourceKind": "live-import"},
              f"K13s: the receipt's adapter is live-import ({rc0.get('adapter')})")
        check(cons.get("configurationSource") == "live-driver" and (cons.get("configuration") or {}).get("fingerprint")
              == load(src0 / "import.json")["configuration"]["fingerprint"]
              and ((cons.get("programs") or {}).get("controller") or {}).get("checked") == ["preflight"]
              and (cons.get("tool") or {}).get("name") == "switchflip",
              "K13s: the receipt names the driver's configuration, the tool and the controller it checked")
        check((rc0.get("validation") or {}).get("accepted") is True and (rc0.get("validation") or {}).get("rejections") == [],
              "K13s: the receipt is accepted with no rejection")
        rd = (res0.get("readings") or {}).get("C1") or {}
        check(res0.get("execution") == "completed" and res0.get("detector") == "finding" and res0.get("findings") == 1
              and res0.get("coverage") == [{"Population": "switch-flip launches: C1", "Judged": 2, "Unjudgeable": {}}]
              and (rd.get("reading"), rd.get("failed"), rd.get("completed")) == ("finding", 2, 0),
              f"K13s: the result is the re-read one: a finding on C1 from two failures ({res0})")
        check(imp0.get("origin") == "fresh" and imp0.get("bindingRecordedAtLaunch") is True and imp0.get("launches") == 2
              and imp0.get("flipPlanSha256") == sha(src0 / "flipplan.json")
              and imp0.get("sourceSha256") == sha(src0 / "import.json"),
              f"K13s: the imported block names the fresh origin, the flip plan, two launches and the document ({imp0})")
        e0s = load(opt / "ledger.json")["entries"].get(key0) or {}
        check((e0s.get("status"), e0s.get("detector"), e0s.get("imported"), e0s.get("importOrigin"), e0s.get("receiptId"))
              == ("completed", "finding", True, "fresh", rc0.get("receiptId")),
              f"K13s: the ledger entry is completed/finding, imported fresh, and backs this receipt ({e0s})")
        dest0 = opt / "imports" / key0
        want_names = ["01-flipplan.json", "02-C1-1.receipt.json", "03-C1-2.receipt.json", "04-C1-1.events.jsonl",
                      "05-C1-2.events.jsonl", "result.json"]
        got_names = sorted(p.name for p in dest0.iterdir()) if dest0.is_dir() else []
        check(got_names == want_names, f"K13s: the imports folder holds the document and each output in role order ({got_names})")
        srcs = {"result.json": src0 / "import.json", "01-flipplan.json": src0 / "flipplan.json"}
        srcs.update({f"0{i}-{n}": src0 / n for i, n in ((2, "C1-1.receipt.json"), (3, "C1-2.receipt.json"),
                                                        (4, "C1-1.events.jsonl"), (5, "C1-2.events.jsonl"))})
        check(dest0.is_dir() and all(sha(dest0 / n) == sha(p) for n, p in srcs.items()),
              "K13s: each copy in the imports folder has its source's bytes")
        check(sorted(str(o) for o in rc0.get("outputs") or []) and len(rc0.get("outputs") or []) == 6,
              f"K13s: the receipt lists the six copied files ({len(rc0.get('outputs') or [])})")
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
        finalize(opt)
        dfin = disposition(opt)
        findings = (dfin.get("evaluation") or {}).get("findings") or []
        check(any(f.get("checkId") == CID and f.get("armId") == a0["armId"] for f in findings),
              "K13: finalize carries the live finding")
        check("accepted live receipts" in str((dfin.get("unassessed") or {}).get("runtimeObservations")),
              "K13: finalize no longer says no runtime observation was supplied")

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
            """The app scan of case d through its bundle, then the overlay opted in, evaluated and finalized."""
            r = plan(gen, run, d=d)
            app_through_bundle(run, d / "scenario.json")
            rp, ai = overlay(run, [CID])
            finalize(ai)
            return r.returncode or rp, ai

        vanilla = [base_switch("000A01:FixtureBase.esp", "Triggered"), base_switch("000A02:FixtureBase.esp", "Triggered"),
                   base_switch("000A03:FixtureBase.esp", "BoundUnread")]
        na_case = case_with("case-na", vanilla)
        rna, na_run = plan_run(na_case, tmp / "run-na")
        check(rna == 0, "K26: plan with --opt-in on a census whose driven switches touch no target")
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

        ctl_case = case_with("case-open", vanilla + [base_switch("000A0C:FixtureBase.esp", "BoundUnread",
                                                                 cell="00A001:FixtureTarget.esp")])
        rctl, ctl_run = plan_run(ctl_case, tmp / "run-open")
        check(rctl == 0, "K26 control: plan with a bound switch in a target's cell")
        led2 = load(ctl_run / "ledger.json")["entries"]
        rows2 = [e for e in led2.values() if e["checkId"] == CID]
        check(bool(rows2) and all(e["status"] == "live-import" for e in rows2),
              "K26 control: a script-driven switch in a target's cell keeps every arm waiting for a live receipt")
        dctl = disposition(ctl_run).get("evaluation") or {}
        check(any(m.get("checkId") == CID for m in dctl.get("missing") or []),
              "K26 control: and finalize still names the check missing")

        pl_na = load(na_run / "plan.json")
        arm_na = next(a for a in pl_na["arms"] if a["armId"] in [x["armId"] for x in plan_check(pl_na, CID)["arms"]
                                                                if x["status"] == "live-import"])
        r28 = imp(na_run, make_import(k / "na", pl_na, arm_na, ["crash", "crash"]))
        out28 = r28.stdout + r28.stderr
        check(r28.returncode == 2 and "not applicable on arm" in out28,
              "K28: an import for an arm the census answered not-applicable is refused by name"
              + ("" if r28.returncode == 2 and "not applicable on arm" in out28
                 else f" -- got {r28.returncode}: {out28.strip()[-300:]}"))

    except Exception:
        traceback.print_exc()
        FAILURES.append("exception")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
