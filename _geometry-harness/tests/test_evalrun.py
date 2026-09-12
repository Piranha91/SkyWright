"""The evaluation controller against the oracle (Q42, roadmap 4.23).

    python _geometry-harness/tests/test_evalrun.py            # from the checkout root
    python _geometry-harness/tests/test_evalrun.py case-06    # one case (a prefix match)

Dependency-free; exits 1 on failure. No game, no network, no lab instance: every check runs against the
fixtures under `fixtures/evalrun/` — tiny plugin files, a trimmed copy of the shipped check registry, one
scope/candidate/scenario set per case, and fake probes that write the REAL conflict-file format (so the
REAL conflictdiff.exe and the REAL ordersweep.py do the comparing).

What is asserted comes from `fixtures/evalrun/expected/case-*.json`, written by hand from PLAN.md's D1-D8
BEFORE any of the controller's code existed. Where the code and the oracle disagree, the oracle is not
edited to match: the test fails and the disagreement is reported with both values and the D-rule.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
FIXT = HERE / "fixtures" / "evalrun"          # tracked: the generator, the fakes, the expectations
EXPECTED = FIXT / "expected"
GEN = FIXT / "_generated"                     # generated per run; never tracked (set in main())
EVALRUN = HARNESS / "evalrun.py"
# conflictdiff.exe must exist somewhere: the checkout's own harness when it is built, else the tree named by
# HOUSECARL_EVALRUN_TEST_HARNESS (a worktree borrowing the main checkout's Release binaries). Never a silent
# skip - a missing binary fails the run and says so.
REAL_HARNESS = Path(os.environ.get("HOUSECARL_EVALRUN_TEST_HARNESS", HARNESS))
DIFF_EXE = REAL_HARNESS / "conflictdiff" / "bin" / "Release" / "net9.0" / ("conflictdiff.exe" if os.name == "nt" else "conflictdiff")

PROBES = ["navownprobe", "overlapprobe", "contactprobe", "bosprobe", "switchprobe", "stateprobe",
          "scopeprobe", "adviceprobe", "patchprobe", "driftprobe", "seamprobe", "waterprobe",
          "blockprobe", "depprobe", "fomodprobe", "poolprobe"]

FAILURES: list[str] = []
DISAGREEMENTS: list[str] = []


def check(condition, label):
    print(("  PASS  " if condition else "  FAIL  ") + label)
    if not condition:
        FAILURES.append(label)


def expect(got, want, label, rule=""):
    """An oracle comparison. A miss is recorded as a DISAGREEMENT with both values and the rule, never
    silently re-based on what the code did."""
    ok = got == want
    print(("  PASS  " if ok else "  FAIL  ") + f"{label}: expected {want!r}, got {got!r}")
    if not ok:
        FAILURES.append(label)
        DISAGREEMENTS.append(f"{label}: oracle {want!r} vs code {got!r}{('  [' + rule + ']') if rule else ''}")


def probe_cmds() -> list[str]:
    out = []
    for p in PROBES:
        out += ["--probe-cmd", f"{p}=python {FIXT / 'fakeprobes' / p}.py"]
    return out


def evalrun(args, scenario: Path | None = None, expect_exit=None, timeout=300):
    env = dict(os.environ)
    if scenario:
        env["EVALRUN_FAKE_SCENARIO"] = str(scenario)
    r = subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args],
                       capture_output=True, text=True, env=env, timeout=timeout)
    if expect_exit is not None and r.returncode != expect_exit:
        print(r.stdout[-3000:])
        print(r.stderr[-2000:])
    return r


def load(p: Path):
    return json.loads(Path(p).read_text(encoding="utf-8")) if Path(p).exists() else None


# ------------------------------------------------------------------ tolerant accessors over B's shapes

def as_map(blob, key_names=("checkId", "id")):
    """A list of objects or a dict of objects -> dict keyed by the object's id."""
    if blob is None:
        return {}
    if isinstance(blob, dict):
        return blob
    out = {}
    for e in blob:
        for k in key_names:
            if isinstance(e, dict) and k in e:
                out[e[k]] = e
                break
    return out


def plan_checks(plan):
    return as_map(plan.get("checks"))


def ledger_entries(ledger):
    """key '<checkId>__<armId>[__<part>]' -> entry, whatever the ledger's own keying is."""
    blob = ledger.get("entries", ledger.get("checks"))
    if isinstance(blob, dict):
        return blob
    out = {}
    for e in blob or []:
        part = e.get("part")
        key = f"{e.get('checkId')}__{e.get('armId')}" + (f"__{part}" if part else "")
        out[key] = e
    return out


def execution_of(entry):
    if entry is None:
        return None
    for k in ("execution", "status", "state"):
        if k in entry:
            return entry[k]
    return None


def field(doc, *names, default=None):
    for n in names:
        if isinstance(doc, dict) and n in doc:
            return doc[n]
    ev = doc.get("evaluation") if isinstance(doc, dict) else None
    if isinstance(ev, dict):
        for n in names:
            if n in ev:
                return ev[n]
    return default


def names_of(blob):
    """A `missing` / `unknownApplicability` list of strings or of objects -> set of check ids."""
    out = set()
    for e in blob or []:
        if isinstance(e, str):
            out.add(e.split(" ")[0].strip("`"))
        elif isinstance(e, dict):
            for k in ("checkId", "id", "check"):
                if k in e:
                    out.add(e[k])
    return out


def arm_block(disp, arm_id):
    arms = disp.get("arms") or field(disp, "arms") or {}
    items = arms.items() if isinstance(arms, dict) else [(a.get("armId"), a) for a in arms]
    for k, v in items:
        cf = (v or {}).get("candidateFile", "")
        if k == arm_id or arm_id in str(k) or str(k) in arm_id or arm_id in cf:
            return v
    return {}


def deep_find(obj, key):
    """First value under `key` anywhere in a nested structure (the loose assertions: D1-D8 fix the value,
    not where B spells it)."""
    if isinstance(obj, dict):
        if key in obj:
            return obj[key]
        for v in obj.values():
            r = deep_find(v, key)
            if r is not None:
                return r
    elif isinstance(obj, list):
        for v in obj:
            r = deep_find(v, key)
            if r is not None:
                return r
    return None


def deep_numbers(obj, key):
    """Every value under `key` anywhere in a nested structure."""
    out = []
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == key and isinstance(v, (int, float)):
                out.append(v)
            else:
                out += deep_numbers(v, key)
    elif isinstance(obj, list):
        for v in obj:
            out += deep_numbers(v, key)
    return out


# ------------------------------------------------------------------------------------ the case driver

def case_paths(case: str):
    d = GEN / "cases" / case
    return d, d / "scope.json", d / "candidates", d / "scenario.json"


def do_plan(run_dir: Path, case: str, extra=None):
    d, scope, cands, scen = case_paths(case)
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", scope, "--candidates", cands, "--out", run_dir,
            "--harness", REAL_HARNESS] + probe_cmds() + [str(x) for x in (extra or [])]
    return evalrun(args, scen, expect_exit=0)


def full_run(run_dir: Path, case: str, plan_extra=None, media: Path | None = None):
    _, _, _, scen = case_paths(case)
    r = do_plan(run_dir, case, plan_extra)
    if r.returncode != 0:
        return r, None, None, None
    run = evalrun(["run", "--run", run_dir], scen)
    fin = evalrun(["finalize", "--run", run_dir], scen)
    bargs = ["bundle", "--run", run_dir] + (["--media", media] if media else [])
    bun = evalrun(bargs, scen)
    return r, run, fin, bun


def assert_plan(plan, oracle, case):
    checks = plan_checks(plan)
    for cid, want in (oracle["plan"]["checks"] or {}).items():
        got = checks.get(cid)
        if got is None:
            check(False, f"{case}: {cid} is in the plan")
            continue
        if want.get("applicability") is not None:
            a = got.get("applicability")
            a = a.get("answer") if isinstance(a, dict) else a
            expect(a, want["applicability"], f"{case}: {cid} applicability", "D1 / PLAN A-1")
        if want.get("status") is not None:
            expect(got.get("status"), want["status"], f"{case}: {cid} status", "D3 / PLAN A-2")


def assert_ledger(ledger, oracle, case):
    got = ledger_entries(ledger)
    for key, want in (oracle["ledger"] or {}).items():
        e = got.get(key)
        if e is None:
            # The controller's arm ids are short (`candidate-01`) where the oracle names the candidate
            # stem (`candidate-01-reorder`); the check id and the part are what must match.
            cid, _, rest = key.partition("__")
            arm, _, part = rest.partition("__")
            armk = "-".join(arm.split("-")[:2])
            cands = [v for k, v in got.items()
                     if k.startswith(cid + "__") and armk in k and (not part or k.endswith("__" + part))]
            e = cands[0] if len(cands) == 1 else None
        if e is None:
            check(False, f"{case}: ledger has {key}")
            continue
        if want.get("execution") is not None:
            expect(execution_of(e), want["execution"], f"{case}: {key} execution", "D4")
        if want.get("detector") is not None:
            det = (e.get("result") or {}).get("detector", e.get("detector"))
            expect(det, want["detector"], f"{case}: {key} detector", "D4")
        if want.get("rejection") is not None:
            causes = [r.get("cause") for r in ((e.get("validation") or {}).get("rejections") or [])]
            check(want["rejection"] in causes,
                  f"{case}: {key} rejected for {want['rejection']} (got {causes})")


def assert_disposition(disp, oracle, case):
    o = oracle["disposition"]
    if "complete" in o:
        expect(field(disp, "complete"), o["complete"], f"{case}: evaluation.complete", "D7")
    if "claim" in o:
        expect(field(disp, "claim"), o["claim"], f"{case}: evaluation.claim", "D7")
    missing = names_of(field(disp, "missing", default=[]))
    for cid in o.get("missingIncludes", []):
        check(cid in missing, f"{case}: {cid} is listed under missing (D7)")
    for cid in o.get("missingExcludes", []):
        check(cid not in missing, f"{case}: {cid} is NOT under missing (D7)")
    if "unknownApplicability" in o:
        expect(sorted(names_of(field(disp, "unknownApplicability", default=[]))),
               sorted(o["unknownApplicability"]), f"{case}: unknownApplicability", "D7 / PLAN A-2")
    for arm, want in (o.get("arms") or {}).items():
        blk = arm_block(disp, arm)
        oe = blk.get("orderEvaluation") or blk
        if want.get("classification") is not None:
            expect(deep_find(oe, "classification"), want["classification"],
                   f"{case}: {arm} classification", "D7 - ordersweep's verbatim")
        if want.get("regression") is not None:
            expect(blk.get("regression", deep_find(blk, "regression")), want["regression"],
                   f"{case}: {arm} regression", "D7")
        if want.get("probes"):
            probes = deep_find(oe, "probes") or []
            check(all(p in probes for p in want["probes"]),
                  f"{case}: {arm} orderEvaluation is scoped to {want['probes']} (got {probes})")
    for arm, per in (o.get("armCounts") or {}).items():
        blk = arm_block(disp, arm)
        for probe, counts in per.items():
            sub = deep_find(blk, probe) or blk
            for cname, cval in counts.items():
                expect(deep_find(sub, cname), cval, f"{case}: {arm}/{probe} {cname}", "D7 - retained per arm")
    for arm, n in (o.get("newDefect") or {}).items():
        blk = arm_block(disp, arm)
        nums = deep_numbers(blk, "newDefect")
        expect(max(nums) if nums else None, n, f"{case}: {arm} newDefect", "D7 / ordersweep created_breakdown")


# -------------------------------------------------------------------------------------------- cases

def case_01(tmp, case="case-01-geometry-and-quest"):
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    p, r, f, b = full_run(run, case)
    check(p.returncode == 0, f"{case}: plan exits 0")
    plan = load(run / "plan.json")
    if plan is None:
        check(False, f"{case}: plan.json written")
        return run
    assert_plan(plan, o, case)
    assert_ledger(load(run / "ledger.json") or {}, o, case)
    disp = load(run / "disposition.json") or {}
    assert_disposition(disp, o, case)
    check((run / "STATUS.md").exists(), f"{case}: STATUS.md written (D7)")
    v = evalrun(["verify-bundle", run / "bundle"])
    expect(v.returncode, o["verifyBundleExit"], f"{case}: verify-bundle exit", "D8")
    return run


def case_02(tmp):
    case = "case-02-select-three-probes"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    # PLAN A-3 expands exe names to check ids; A-8 pulls in their transitive prerequisites.
    sel = "navownprobe,overlapprobe,contactprobe"
    p, r, f, b = full_run(run, case, plan_extra=["--select", sel])
    plan = load(run / "plan.json") or {}
    assert_plan(plan, o, case)
    disp = load(run / "disposition.json") or {}
    assert_disposition(disp, o, case)
    txt = json.dumps(field(disp, "explicitDecisions", default={}))
    for cid in o["disposition"].get("explicitDecisionsMention", []):
        check(cid in txt or cid in json.dumps(plan.get("explicitDecisions", {})),
              f"{case}: explicitDecisions records {cid} (D7 / PLAN A-2)")


def case_03(tmp):
    case = "case-03-null-and-extraction"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case)
    assert_ledger(load(run / "ledger.json") or {}, o, case)
    assert_disposition(load(run / "disposition.json") or {}, o, case)


def case_04(tmp):
    case = "case-04-blocked-and-budget"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case, plan_extra=["--max-candidates", "1",
                                    "--exclude", "geometry.placement-contact=not-relevant-to-this-review"])
    plan = load(run / "plan.json") or {}
    assert_plan(plan, o, case)
    assert_ledger(load(run / "ledger.json") or {}, o, case)
    disp = load(run / "disposition.json") or {}
    assert_disposition(disp, o, case)
    budgets = plan.get("budgets") or {}
    expect(budgets.get("maxCandidates"), o["disposition"]["maxCandidates"], f"{case}: budgets.maxCandidates", "D3")
    blob = json.dumps(field(disp, "unassessed", default={}))
    check(o["disposition"]["candidatesHeldBackMentions"] in blob,
          f"{case}: unassessed names the held-back candidate (D7 / PLAN A-4)")
    led = ledger_entries(load(run / "ledger.json") or {})
    held = [k for k, v in led.items() if "candidate-02-reorder-b" in k and execution_of(v) == "skipped-budget"]
    check(bool(held), f"{case}: the held-back arm's checks are skipped-budget (PLAN A-4)")
    check((run / "bundle" / "manifest.json").exists(),
          f"{case}: the bundle is written while the evaluation is incomplete (D8)")


def case_05(tmp):
    case = "case-05-rejections"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case)
    assert_ledger(load(run / "ledger.json") or {}, o, case)
    assert_disposition(load(run / "disposition.json") or {}, o, case)


def case_06(tmp):
    case = "case-06-attribution"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case)
    assert_ledger(load(run / "ledger.json") or {}, o, case)
    disp = load(run / "disposition.json") or {}
    assert_disposition(disp, o, case)
    # the real conflictdiff's own verdict, retained verbatim somewhere in the run
    blob = json.dumps(disp) + json.dumps(load(run / "sweep" / "summary.json") or {})
    check("DIFFERENTIAL-RECLASSIFIED" in blob, f"{case}: the RECLASSIFIED verdict is retained verbatim (D4/D7)")
    check("DIFFERENTIAL-VERIFIED" not in blob, f"{case}: nothing on this arm was credited as a repair")


def case_07(tmp):
    case = "case-07-combined-regression"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case)
    disp = load(run / "disposition.json") or {}
    assert_disposition(disp, o, case)
    blk = arm_block(disp, "candidate-03-combined")
    check("espOnly" in json.dumps(blk) or "not staged" in json.dumps(load(run / "plan.json") or {}),
          f"{case}: the supplied-combination arm carries its ESP-only limit (D3)")


def case_08(tmp):
    case = "case-08-interrupt-resume"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    _, _, _, scen = case_paths(case)
    do_plan(run, case)
    # The kill has to land while something is genuinely in flight, or the case tests nothing. One fake
    # blocks on a sentinel file, and the kill waits on the JOURNAL showing that check `running` - not on a
    # timer, which lands at a different point every run.
    blocked = "state.switch-census"
    sentinel = run.parent / f"{case}.sentinel"
    sentinel.write_text("held", encoding="utf-8")
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen),
               EVALRUN_FAKE_SENTINEL=str(sentinel), EVALRUN_FAKE_BLOCK_PROBE="switchprobe")
    proc = subprocess.Popen([sys.executable, str(EVALRUN), "run", "--run", str(run)],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, text=True)
    deadline = time.time() + 120
    running = False
    while time.time() < deadline and proc.poll() is None:
        jf = run / "journal.jsonl"
        if jf.exists():
            for line in jf.read_text(encoding="utf-8", errors="replace").splitlines():
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                if row.get("checkId") == blocked and row.get("to") == "running":
                    running = True
        if running:
            break
        time.sleep(0.1)
    check(running, f"{case}: the controller was killed with {blocked} genuinely in flight")
    proc.terminate()
    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        proc.kill()
    sentinel.unlink(missing_ok=True)
    journal = (run / "journal.jsonl").read_text(encoding="utf-8") if (run / "journal.jsonl").exists() else ""
    evalrun(["resume", "--run", run], scen)
    journal2 = (run / "journal.jsonl").read_text(encoding="utf-8") if (run / "journal.jsonl").exists() else ""
    check("interrupted" in journal2,
          f"{case}: a running entry with no terminal line became interrupted (D5)")
    # D6: "Running `run` twice on an unchanged RUN performs no child work (every entry `reused`)." The
    # resume that follows the kill re-runs what was in flight, so the reuse evidence is the NEXT one,
    # over a run nothing has touched.
    evalrun(["resume", "--run", run], scen)
    led = ledger_entries(load(run / "ledger.json") or {})
    reused = [k for k, v in led.items() if execution_of(v) == "reused"]
    check(bool(reused), f"{case}: valid work is reused after a resume over an unchanged run (D6)")
    check(all("reusedFromRunId" in json.dumps(led[k]) for k in reused) if reused else False,
          f"{case}: every reused entry names the run it was reused from (D6)")
    for key, e in led.items():
        if execution_of(e) == "completed":
            rp = e.get("receipt") or e.get("receiptPath")
            if rp:
                check((run / rp).exists() or Path(rp).exists(), f"{case}: {key} completed with its receipt on disk")
    # change one consumed plugin and resume again
    plug = GEN / "plugins" / "FixtureBase.esp"
    body = plug.read_bytes()
    try:
        plug.write_bytes(body + b"a byte that changes the content hash\n")
        evalrun(["resume", "--run", run], scen)
        led2 = ledger_entries(load(run / "ledger.json") or {})
        j3 = (run / "journal.jsonl").read_text(encoding="utf-8")
        check("invalidated" in j3, f"{case}: the changed plugin invalidated dependent work (D6)")
        for cid in o["disposition"]["invalidatedAfterPluginChange"]:
            # The entry ITSELF must carry the invalidation: a journal that merely contains the word
            # somewhere passes for the wrong reason (the sabotage arm caught exactly that).
            # The TRANSITION is what D6 fixes; whether the resume then re-runs the check in the same
            # invocation it does not say, so the ledger's final status is not the evidence. A journal
            # line for THIS check going to invalidated(...) is.
            hits = []
            for line in j3.splitlines():
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                if row.get("checkId") == cid and str(row.get("to", "")).startswith("invalidated"):
                    hits.append(row)
            check(bool(hits), f"{case}: {cid} was invalidated by the plugin change (D6)")
    finally:
        plug.write_bytes(body)
    check(len(field(load(run / "ledger.json") or {}, "runIds", default=[])) >= o["disposition"]["runIdsAtLeast"],
          f"{case}: the ledger head lists a runId per run/resume (D5)")

    # D6's other half: a receipt whose consumer cannot state an input is never reused - it is
    # refused-reuse(<gap>), NAMED. A scope that lists no plugin paths is exactly that case.
    run2 = tmp / f"{case}-no-plugin-paths"
    d = GEN / "cases" / case
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", d / "scope-no-plugins.json", "--candidates", d / "candidates", "--out", run2,
            "--harness", REAL_HARNESS] + probe_cmds()
    evalrun(args, scen, expect_exit=0)
    evalrun(["run", "--run", run2], scen)
    plug2 = GEN / "plugins" / "FixtureBase.esp"
    body2 = plug2.read_bytes()
    try:
        plug2.write_bytes(body2 + b"changed again\n")
        evalrun(["resume", "--run", run2], scen)
    finally:
        plug2.write_bytes(body2)
    j4 = (run2 / "journal.jsonl").read_text(encoding="utf-8") if (run2 / "journal.jsonl").exists() else ""
    led3 = ledger_entries(load(run2 / "ledger.json") or {})
    gapped = [k for k, v in led3.items() if str(execution_of(v)).startswith("refused-reuse")] \
        or [l for l in j4.splitlines() if "refused-reuse" in l]
    check(bool(gapped),
          f"{case}: a scope naming no plugin paths gives refused-reuse(<gap>), named, not a silent reuse (D6)")


def case_09(tmp):
    case = "case-09-finalize-refusals"
    o = load(EXPECTED / f"{case}.json")
    base = tmp / case
    _, _, _, scen = case_paths(case)
    full_run(base, case)
    for spec in o["disposition"]["refusals"]:
        run = tmp / f"{case}-{spec['damage']}"
        shutil.copytree(base, run)
        if spec["damage"] == "registry-edited":
            reg = GEN / "registry" / "fixture-check-registry.json"
            body = reg.read_text(encoding="utf-8")
            reg.write_text(body + "\n", encoding="utf-8")
        elif spec["damage"] == "receipt-deleted":
            rs = sorted((run / "receipts").glob("*.json"))
            rs[0].unlink()
        else:
            rs = sorted((run / "receipts").glob("*.json"))
            d = json.loads(rs[0].read_text(encoding="utf-8"))
            d["planId"] = "0000deadbeef"
            rs[0].write_text(json.dumps(d, indent=1), encoding="utf-8")
        r = evalrun(["finalize", "--run", run], scen)
        if spec["damage"] == "registry-edited":
            reg.write_text(body, encoding="utf-8")
        expect(r.returncode, spec["finalizeExit"], f"{case}/{spec['damage']}: finalize exit", "D7")
        disp = load(run / "disposition.json") or {}
        blob = json.dumps(disp) + r.stdout + r.stderr
        check(spec["cause"] in blob, f"{case}/{spec['damage']}: refused by name '{spec['cause']}' (D7)")
        if disp:
            expect(field(disp, "complete"), False, f"{case}/{spec['damage']}: complete", "D7")


def case_10(tmp):
    case = "case-10-discovery-media"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    do_plan(run, case)
    _, _, _, scen = case_paths(case)
    evalrun(["run", "--run", run], scen)
    evalrun(["finalize", "--run", run], scen)
    # one media item whose fingerprint matches an accepted receipt, one that matches nothing
    fp = None
    for r in sorted((run / "receipts").glob("*.json")):
        d = load(r)
        fp = fp or (((d.get("consumed") or {}).get("configuration") or {}).get("fingerprint"))
    media_dir = run / "supplied"
    media_dir.mkdir(parents=True, exist_ok=True)
    for n in ("matched-shot.png", "old-shot.png"):
        (media_dir / n).write_bytes(b"\x89PNG\r\n\x1a\n" + n.encode())
    mediajson = run / "media.json"
    mediajson.write_text(json.dumps([
        {"path": str(media_dir / "matched-shot.png"), "configurationFingerprint": fp,
         "subject": {"checkId": "geometry.navmesh-ownership", "armId": "baseline"}, "state": "baseline",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-09-11T00:00:00Z"}},
        {"path": str(media_dir / "old-shot.png"), "configurationFingerprint": "deadbeef" * 8,
         "subject": {"checkId": "geometry.navmesh-ownership", "armId": "baseline"}, "state": "baseline",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-05-01T00:00:00Z"}},
    ], indent=1), encoding="utf-8")
    evalrun(["bundle", "--run", run, "--media", mediajson], scen)
    man = load(run / "bundle" / "manifest.json") or {}
    unassessed = json.dumps(man.get("unassessedMedia", []))
    for n in o["disposition"]["unassessedMediaIncludes"]:
        check(n in unassessed, f"{case}: {n} is unassessedMedia, not evidence (D8)")
    for n in o["disposition"]["unassessedMediaExcludes"]:
        check(n not in unassessed, f"{case}: {n} is NOT unassessed (its fingerprint matched) (D8)")
    ev = json.dumps(man.get("evidence", []))
    check("supplied-media" in ev, f"{case}: the matching media is evidence with strength supplied-media (D8)")
    disp = load(run / "disposition.json") or {}
    quals = [q for q in json.dumps(field(disp, "discovery", default={})).split('"') if q in ("qualified", "unqualified")]
    check("qualified" not in quals or "unqualified" in quals,
          f"{case}: a catalogue hit in no evaluated arm is unqualified (D7)")


def case_11(tmp):
    case = "case-11-bundle-relocate"
    o = load(EXPECTED / f"{case}.json")
    run = tmp / case
    full_run(run, case)
    steps = {s["step"]: s for s in o["disposition"]["verifySteps"]}
    b = run / "bundle"
    r = evalrun(["verify-bundle", b])
    expect(r.returncode, steps["in-place"]["exit"], f"{case}: verify in place", "D8")
    moved = tmp / "relocated-bundle"
    shutil.copytree(b, moved)
    r = evalrun(["verify-bundle", moved])
    expect(r.returncode, steps["relocated"]["exit"], f"{case}: verify after relocation", "D8")
    ev = sorted(p for p in moved.rglob("*") if p.is_file() and p.name != "manifest.json")
    victim = ev[len(ev) // 2]
    victim.unlink()
    r = evalrun(["verify-bundle", moved])
    expect(r.returncode, steps["file-deleted"]["exit"], f"{case}: verify after a deletion", "D8")
    v = load(moved / "verify.json") or {}
    check(field(v, "complete", "bundleComplete") is False or v.get("bundle", {}).get("complete") is False,
          f"{case}: verify.json says bundle.complete false (D8)")
    check(victim.name in json.dumps(v), f"{case}: the missing file is listed by name (D8)")
    bad = [p.name for p in moved.rglob("*") if p.suffix.lower() in (".esp", ".esm", ".esl", ".bsa")]
    check(not bad, f"{case}: no plugin or asset binary in the bundle (D8), found {bad}")
    man = load(b / "manifest.json") or {}
    check("binaries" in json.dumps(man.get("excluded", [])),
          f"{case}: the manifest says plugin and asset binaries are excluded (D8)")


CASES = {"case-01": case_01, "case-02": case_02, "case-03": case_03, "case-04": case_04, "case-05": case_05,
         "case-06": case_06, "case-07": case_07, "case-08": case_08, "case-09": case_09, "case-10": case_10,
         "case-11": case_11}


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="hc-evalrun-test-"))
    # Every path-bearing fixture is generated into this run's own directory: the plugin files a receipt
    # hashes, the scope that names them, the candidates and the scenarios, and the registry trim derived
    # from the shipped registry. Nothing under the checkout is written, so the suite leaves no tracked
    # file changed (PLAN A-9, and the rule that a test never rewrites tracked files).
    global GEN
    GEN = tmp / "fixtures"
    gen = subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(GEN)],
                         capture_output=True, text=True)
    if gen.returncode != 0:
        print("  FAIL  make_fixtures.py did not run:\n" + gen.stdout + gen.stderr)
        return 1
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
    os.environ["PYTHONIOENCODING"] = "utf-8"
    if not DIFF_EXE.exists():
        print(f"  FAIL  conflictdiff is not built at {DIFF_EXE} - build it (dotnet build -c Release) or set "
              f"HOUSECARL_EVALRUN_TEST_HARNESS to a tree that has it. The real diff is not optional here.")
        return 1
    try:
        for name, fn in CASES.items():
            if want and not name.startswith(want) and not want.startswith(name):
                continue
            print(f"\n=== {name} ===")
            t0 = time.time()
            try:
                fn(tmp)
            except Exception as ex:                                  # a crash is a failure, never a skip
                check(False, f"{name}: raised {type(ex).__name__}: {ex}")
            print(f"  ({time.time() - t0:.1f}s)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print()
    if DISAGREEMENTS:
        print("ORACLE vs CODE disagreements (the oracle is NOT re-based on the code; these go to Fable):")
        for d in DISAGREEMENTS:
            print("  - " + d)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
