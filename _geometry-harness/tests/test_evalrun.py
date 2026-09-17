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

import hashlib
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


def do_plan(run_dir: Path, case: str, extra=None, registry: str | None = None,
            gen: Path | None = None, scenario: Path | None = None):
    """`registry` names a trim file other than the default (case 12 needs one with an extra check);
    `gen` points at a fixture root other than the suite's shared one (case 15 generates its own and
    then deletes it); `scenario` drives one of a case's damaged scenarios."""
    root = Path(gen) if gen else GEN
    d = root / "cases" / case
    args = ["plan", "--registry", root / "registry" / (registry or "fixture-check-registry.json"),
            "--scope", d / "scope.json", "--candidates", d / "candidates", "--out", run_dir,
            "--harness", REAL_HARNESS] + probe_cmds() + [str(x) for x in (extra or [])]
    return evalrun(args, Path(scenario) if scenario else d / "scenario.json", expect_exit=0)


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
    cand_arm = None
    for r in sorted((run / "receipts").glob("*.json")):
        d = load(r)
        if not ((d.get("validation") or {}).get("accepted")):
            continue
        got = (((d.get("consumed") or {}).get("configuration") or {}).get("fingerprint"))
        if d.get("armId") == "baseline" and d.get("part") in (None, "arm"):
            fp = fp or got
        elif d.get("armId") != "baseline":
            cand_arm = cand_arm or d.get("armId")
    media_dir = run / "supplied"
    media_dir.mkdir(parents=True, exist_ok=True)
    for n in ("matched-shot.png", "old-shot.png", "wrong-arm-shot.png", "unsourced-shot.png"):
        (media_dir / n).write_bytes(b"\x89PNG\r\n\x1a\n" + n.encode())
    mediajson = run / "media.json"
    # media item schema 2 (bundle standard §8): kind, a described subject, state, the fingerprint and where
    # it came from, provenance. The first item's fingerprint is an accepted baseline receipt's; the second's
    # matches nothing.
    subject = {"checkId": "geometry.navmesh-ownership", "armId": "baseline",
               "description": "the contested navmesh cell from the fixture's suggested camera"}
    mediajson.write_text(json.dumps([
        {"path": str(media_dir / "matched-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-09-11T00:00:00Z"}},
        {"path": str(media_dir / "old-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": "deadbeef" * 8, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-05-01T00:00:00Z"}},
        # A-3's trap: a baseline-order picture declared for the CANDIDATE arm with the baseline fingerprint.
        # The candidate's reverse part consumed the baseline order, so its receipt carries this fingerprint;
        # the comparison set is the arm's OWN configuration, so this is a mismatch, not evidence.
        {"path": str(media_dir / "wrong-arm-shot.png"), "mediaKind": "native-screenshot",
         "subject": {**subject, "armId": cand_arm or "candidate-01"},
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-09-11T00:00:00Z"}},
        # a matching item that names no provenance: refused by that field, never accepted with provenance null
        {"path": str(media_dir / "unsourced-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted"},
    ], indent=1), encoding="utf-8")
    evalrun(["bundle", "--run", run, "--media", mediajson], scen)
    man = load(run / "bundle" / "manifest.json") or {}
    unassessed = json.dumps(man.get("unassessedMedia", []))
    for n in o["disposition"]["unassessedMediaIncludes"]:
        check(n in unassessed, f"{case}: {n} is unassessedMedia, not evidence (D8)")
    for n in o["disposition"]["unassessedMediaExcludes"]:
        check(n not in unassessed, f"{case}: {n} is NOT unassessed (its fingerprint matched) (D8)")
    for n, reason in (o["disposition"].get("unassessedMediaReasons") or {}).items():
        rows = [u for u in man.get("unassessedMedia", []) if n in str(u.get("path"))]
        expect(rows[0].get("reason") if rows else None, reason,
               f"{case}: {n} is unassessed by ONE closed reason (D8, media item schema 2)")
    ev = json.dumps(man.get("evidence", []))
    check("supplied-media" in ev, f"{case}: the matching media is evidence with strength supplied-media (D8)")
    rows = [e for e in man.get("evidence", []) if e.get("kind") == "media"]
    row = rows[0] if rows else {}
    digest = hashlib.sha256((media_dir / "matched-shot.png").read_bytes()).hexdigest()
    expect(row.get("path"), f"media/{digest[:12]}-matched-shot.png",
           f"{case}: the accepted media is copied to media/<sha256[:12]>-<basename> (D8)")
    for k in o["disposition"].get("mediaRowCarries") or []:
        check(k in row, f"{case}: the media row carries {k} (D8, media item schema 2)")
    check((run / "bundle" / row.get("path", "missing")).exists(), f"{case}: the media copy is on disk (D8)")
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


# ------------------------------------------------------- Q43: the evidence-integrity contract (12-15)

def resolve_key(plan: dict, key: str) -> str:
    """The oracle names an arm by its CANDIDATE STEM (`candidate-01-reorder`); the controller's arm ids are
    its own (`candidate-01`). The plan carries the mapping, so the key is resolved rather than guessed."""
    cid, _, rest = key.partition("__")
    stem, _, part = rest.partition("__")
    m = {Path(a.get("candidateFile", "")).stem: a.get("armId") for a in plan.get("arms") or []}
    aid = m.get(stem, stem)
    return f"{cid}__{aid}" + (f"__{part}" if part else "")


def receipt(run: Path, plan: dict, key: str):
    return load(run / "receipts" / f"{resolve_key(plan, key)}.json")


def journal_rows(run: Path, since: int = 0) -> list[dict]:
    p = run / "journal.jsonl"
    if not p.exists():
        return []
    rows = []
    for line in p.read_text(encoding="utf-8", errors="replace").splitlines()[since:]:
        try:
            rows.append(json.loads(line))
        except ValueError:
            continue
    return rows


def journal_len(run: Path) -> int:
    p = run / "journal.jsonl"
    return len(p.read_text(encoding="utf-8", errors="replace").splitlines()) if p.exists() else 0


def split_key(resolved: str):
    cid, _, rest = resolved.partition("__")
    aid, _, part = rest.partition("__")
    return cid, aid, (part or None)


def transitions_to(rows, resolved_key: str, startswith: str):
    """Journal rows for ONE ledger entry whose new status starts with `startswith`. The armId is part of
    the filter: without it a candidate arm's transition answers for the baseline's, and the independence
    assertions would read another arm's work."""
    cid, aid, part = split_key(resolved_key)
    out = []
    for r in rows:
        if r.get("checkId") != cid or r.get("armId") != aid:
            continue
        if (r.get("part") or None) != part:
            continue
        if str(r.get("to", "")).startswith(startswith):
            out.append(r)
    return out


def scrub_paths(doc, res, d: Path) -> str:
    """One searchable blob of a tool's answer with the DIRECTORY IT RAN IN removed. A temp directory named
    after the damage makes `<defect kind> in blob` pass on the folder name; scrubbing is what keeps the
    assertion about the tool's words."""
    out = json.dumps(doc) + (res.stdout or "") + (res.stderr or "")
    for form in (str(d), str(d).replace("\\", "/"), d.name):
        out = out.replace(form, "<DIR>").replace(form.replace("\\", "\\\\"), "<DIR>")
    return out


def rejection_detail(rc: dict, cause: str) -> str:
    return " | ".join(x.get("detail", "") for x in ((rc.get("validation") or {}).get("rejections") or [])
                      if x.get("cause") == cause)


def case_12(tmp):
    case = "case-12-identity-binding"
    o = load(EXPECTED / f"{case}.json")
    reg = "fixture-check-registry-q43.json"
    d = GEN / "cases" / case
    run = tmp / case
    do_plan(run, case, registry=reg)
    plan = load(run / "plan.json") or {}
    expect(plan.get("schemaVersion"), o["plan"]["schemaVersion"], f"{case}: plan.schemaVersion", "A-1")
    # Keyed by BOTH the armId and the candidate stem: the oracle names the baseline arm "baseline" (its
    # armId) and the others by their candidate stem, and a stem-only map reads [] for the baseline.
    arms = {}
    for a in plan.get("arms") or []:
        arms[a.get("armId")] = a
        arms.setdefault(Path(a.get("candidateFile", "")).stem, a)
    for stem, want in o["plan"]["armsRequested"].items():
        req = (arms.get(stem) or {}).get("requested") or {}
        plugs = req.get("plugins") or []
        expect([p.get("name") for p in plugs], want["plugins"], f"{case}: {stem} requested plugin order", "D1")
        expect([p.get("hashSource") for p in plugs], want["hashSource"],
               f"{case}: {stem} requested hashSource per plugin", "D1")
        expect(sorted(p.get("name") for p in plugs if p.get("sha256") is None), sorted(want["sha256Null"]),
               f"{case}: {stem} requested sha256 null for", "D1")
    if o["plan"]["providersFromScope"]:
        check(bool((plan.get("configuration") or {}).get("providers")),
              f"{case}: plan.configuration keeps the scope's providers (D1)")

    evalrun(["run", "--run", run], d / "scenario.json")
    for key in o["healthy"]["acceptedEntries"]:
        rc = receipt(run, plan, key)
        check(bool(rc) and ((rc.get("validation") or {}).get("accepted") is True),
              f"{case}: {key} accepted (healthy control, D2)")
    lim = o["healthy"]["identityLimitsOn"]
    rc = receipt(run, plan, lim["entry"]) or {}
    check(lim["namesPlugin"] in json.dumps(rc.get("validation") or {}),
          f"{case}: the unstated-hash plugin {lim['namesPlugin']} is recorded under validation.identityLimits (D2c)")
    dp = o["healthy"]["differentPopulations"]
    a1 = receipt(run, plan, "functional.quest-alias-split__candidate-01-reorder") or {}
    a2 = receipt(run, plan, "geometry.navmesh-ownership__candidate-01-reorder__arm") or {}
    f1 = ((a1.get("consumed") or {}).get("configuration") or {}).get("fingerprint")
    f2 = ((a2.get("consumed") or {}).get("configuration") or {}).get("fingerprint")
    check((a1.get("validation") or {}).get("accepted") is True and (a2.get("validation") or {}).get("accepted") is True,
          f"{case}: two probes declaring different populations over the same plugins are BOTH accepted (D2g/h)")
    check(bool(f1) and bool(f2) and f1 != f2,
          f"{case}: their fingerprints differ ({str(f1)[:12]} vs {str(f2)[:12]}) and neither is a mismatch (D2g)")

    j = o["consumed"]
    for stem in ("baseline", "candidate-01-reorder"):
        want = j[stem]
        rc = receipt(run, plan, f"{j['jsonAdapterCheck']}__{stem}") or {}
        cfg = (rc.get("consumed") or {}).get("configuration") or {}
        expect((rc.get("consumed") or {}).get("configurationSource"), want["configurationSource"],
               f"{case}: {j['jsonAdapterCheck']} [{stem}] consumed.configurationSource", "D3")
        expect(cfg.get("fingerprint") is None, want["fingerprintIsNull"],
               f"{case}: {j['jsonAdapterCheck']} [{stem}] consumed fingerprint is null", "D3")
        expect([p.get("name", p.get("Name")) for p in cfg.get("plugins") or []], want["plugins"],
               f"{case}: {j['jsonAdapterCheck']} [{stem}] consumed plugin order", "D3")

    scope = load(d / "scope.json") or {}
    scope_hash = {str(p["Name"]).lower(): p["Sha256"]
                  for p in (scope.get("configuration") or {}).get("plugins") or []}
    for spec in o["rejections"]:
        r2 = tmp / f"{case}-{spec['id']}"
        scen = d / spec["scenario"]
        do_plan(r2, case, registry=reg, scenario=scen)
        evalrun(["run", "--run", r2], scen)
        plan2 = load(r2 / "plan.json") or {}
        rc = receipt(r2, plan2, spec["entry"])
        if rc is None:
            check(False, f"{case}/{spec['id']}: a receipt was written for {spec['entry']}")
            continue
        expect((rc.get("validation") or {}).get("accepted"), False,
               f"{case}/{spec['id']}: {spec['entry']} accepted", spec["rule"][:40])
        causes = [x.get("cause") for x in ((rc.get("validation") or {}).get("rejections") or [])]
        check(spec["cause"] in causes,
              f"{case}/{spec['id']}: rejected for {spec['cause']} (got {causes})")
        detail = rejection_detail(rc, spec["cause"])
        for tok in spec.get("detailContains", []):
            check(tok in detail, f"{case}/{spec['id']}: the detail names {tok!r} (got {detail[:160]!r})")
        if spec.get("detailNamesBothHashes"):
            want_h = scope_hash.get("fixturetarget.esp", "")
            got_h = ("1" if want_h[:1] != "1" else "2") + want_h[1:]
            check(want_h[:12] in detail and got_h[:12] in detail,
                  f"{case}/{spec['id']}: the detail names both hashes at 12 hex (got {detail[:160]!r})")
        base_key = spec["entry"].split("__")[0] + "__baseline__arm"
        rb = receipt(r2, plan2, base_key) or {}
        check((rb.get("validation") or {}).get("accepted") is True,
              f"{case}/{spec['id']}: the undamaged baseline arm is still accepted (not a blanket rejection)")


def case_13(tmp):
    case = "case-13-reuse-boundary"
    o = load(EXPECTED / f"{case}.json")
    d = GEN / "cases" / case
    scen = d / "scenario.json"
    base = tmp / case
    do_plan(base, case)
    evalrun(["run", "--run", base], scen)
    plan = load(base / "plan.json") or {}

    b = o["baseEmitDependency"]
    ca = receipt(base, plan, b["candidateArmReceipt"]["entry"]) or {}
    cons = ca.get("consumed") or {}
    expect(cons.get("baseEmitSha256") is None, b["candidateArmReceipt"]["baseEmitSha256IsNull"],
           f"{case}: the candidate arm receipt's consumed.baseEmitSha256 is null", "D4")
    check(b["candidateArmReceipt"]["baseEmitPathNames"] in str(cons.get("baseEmitPath") or ""),
          f"{case}: it names the baseline emit (got {cons.get('baseEmitPath')!r}) (D4)")
    rv = (receipt(base, plan, b["reverseReceipt"]["entry"]) or {}).get("consumed") or {}
    expect(rv.get("baseEmitSha256") is None, b["reverseReceipt"]["baseEmitSha256IsNull"],
           f"{case}: the reverse receipt's consumed.baseEmitSha256 is null", "D4")
    df = (receipt(base, plan, b["diffReceipt"]["entry"]) or {}).get("consumed") or {}
    ins = df.get("inputs") or []
    expect(len(ins), b["diffReceipt"]["inputsCount"], f"{case}: the diff receipt's consumed.inputs count", "D4/A-4")
    expect(sorted(str(x.get("part")) for x in ins), sorted(b["diffReceipt"]["inputParts"]),
           f"{case}: the diff receipt's input parts", "A-4")
    check(all(x.get("path") and x.get("sha256") for x in ins) if ins else False,
          f"{case}: every diff input row carries a path and a sha256 (A-4)")
    check(df.get("baseEmitPath") is None and df.get("baseEmitSha256") is None,
          f"{case}: the diff receipt's own baseEmit stays null (A-4)")

    steps = {s["id"]: s for s in o["steps"]}
    plug = GEN / "plugins" / "FixtureTarget.esp"
    body = plug.read_bytes()

    # (i) the plugin is GONE - absence is a change, not "no change"
    s = steps["plugin-gone"]
    r = tmp / f"{case}-plugin-gone"
    shutil.copytree(base, r)
    n0 = journal_len(r)
    try:
        plug.unlink()
        res = evalrun(["resume", "--run", r], scen)
    finally:
        plug.write_bytes(body)
    expect(res.returncode, s["exit"], f"{case}/plugin-gone: resume exit", "D5")
    rows = journal_rows(r, n0)
    led = ledger_entries(load(r / "ledger.json") or {})
    for key in s["invalidatedByName"]["entries"]:
        rk = resolve_key(plan, key)
        hits = [x for x in transitions_to(rows, rk, "invalidated")
                if s["invalidatedByName"]["namesPlugin"] in str(x.get("to", "")) + str(x.get("detail", ""))]
        check(bool(hits), f"{case}/plugin-gone: {key} invalidated by name, naming FixtureTarget.esp (D5b)")
        check(not transitions_to(rows, rk, "reused"),
              f"{case}/plugin-gone: {key} was never reused (D5b)")
    if s.get("detailNamesNextAction"):
        blob = json.dumps(led)
        check(any(w in blob for w in ("re-run", "restore", "re-plan")),
              f"{case}/plugin-gone: the ledger entry's detail names the next action (D5)")
    evalrun(["finalize", "--run", r], scen)
    disp = load(r / "disposition.json") or {}
    miss = names_of(field(disp, "missing", default=[]))
    for cid in s["afterRerunAndFinalize"]["affectedChecksNotCompleted"]:
        check(cid in miss, f"{case}/plugin-gone: {cid} is not completed after the re-run (D2c on a zero hash)")
    expect(field(disp, "complete"), False, f"{case}/plugin-gone: evaluation.complete", "D6")

    # (ii) one arm's input changed - the other arm's work survives
    s = steps["one-arm-input-changed"]
    r = tmp / f"{case}-one-arm"
    shutil.copytree(base, r)
    cand = d / "candidates" / "candidate-01-reorder.json"
    cbody = cand.read_bytes()
    n0 = journal_len(r)
    try:
        cand.write_bytes(cbody + b"\n")
        res = evalrun(["resume", "--run", r], scen)
    finally:
        cand.write_bytes(cbody)
    expect(res.returncode, s["exit"], f"{case}/one-arm: resume exit", "D5")
    rows = journal_rows(r, n0)
    for key in s["invalidatedByName"]["entries"]:
        rk = resolve_key(plan, key)
        check(bool(transitions_to(rows, rk, "invalidated")),
              f"{case}/one-arm: {key} invalidated by the changed candidate (D5b)")
        inv = s["invalidatedByName"]
        led_now = ledger_entries(load(r / "ledger.json") or {})
        if inv.get("partRestingStatusStartsWith"):
            got = str(execution_of(led_now.get(rk)))
            check(got.startswith(inv["partRestingStatusStartsWith"]),
                  f"{case}/one-arm: {key} rests at "
                  f"{inv['partRestingStatusStartsWith']}(...) - the truthful reason (A-7); got {got!r}")
        if inv.get("accountingEntry"):
            # The launch refusal is the PART-LESS accounting entry, not a status on the part (A-7).
            ak = resolve_key(plan, inv["accountingEntry"])
            expect(execution_of(led_now.get(ak)), inv["accountingStatus"],
                   f"{case}/one-arm: {inv['accountingEntry']} (no part) records the launch refusal", "A-7")
    led = ledger_entries(load(r / "ledger.json") or {})
    for key in s["independentStayReused"]["entries"]:
        rk = resolve_key(plan, key)
        e = led.get(rk)
        expect(execution_of(e), s["independentStayReused"]["restingStatus"],
               f"{case}/one-arm: {key} resting status", "D5 - independent entries stay reused")
        check(not transitions_to(rows, rk, "invalidated"),
              f"{case}/one-arm: {key} was not invalidated (D5)")
        check(not transitions_to(rows, rk, "running"),
              f"{case}/one-arm: {key} did no child work (D5)")

    # (iii) an identity that cannot be verified is refused-reuse, never reused
    s = steps["unverifiable-identity"]
    r = tmp / f"{case}-unverifiable"
    shutil.copytree(base, r)
    key = resolve_key(plan, s["expect"]["entry"])
    rp = r / "receipts" / f"{key}.json"
    doc = load(rp) or {}
    for p in ((doc.get("consumed") or {}).get("configuration") or {}).get("plugins") or []:
        if str(p.get("name", p.get("Name"))).lower() == s["expect"]["gapNamesPlugin"].lower():
            p["sha256" if "sha256" in p else "Sha256"] = ""
    rp.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    n0 = journal_len(r)
    res = evalrun(["resume", "--run", r], scen)
    expect(res.returncode, s["exit"], f"{case}/unverifiable: resume exit", "D5a")
    rows = journal_rows(r, n0)
    gapped = transitions_to(rows, key, s["expect"]["statusStartsWith"])
    check(bool(gapped), f"{case}/unverifiable: {s['expect']['entry']} went refused-reuse(<gap>) (D5a)")
    check(any(s["expect"]["gapNamesPlugin"] in str(x.get("to", "")) + str(x.get("detail", "")) for x in gapped),
          f"{case}/unverifiable: the gap names {s['expect']['gapNamesPlugin']} (D5a)")
    check(not transitions_to(rows, key, "invalidated"),
          f"{case}/unverifiable: it was not called invalidated - nobody measured a change (D5a)")

    # (iii-b) a comparison input recorded without a hash is unverifiable too (A-9, V's finding 2)
    s = steps["unverifiable-base-emit"]
    r = tmp / f"{case}-unverifiable-base"
    shutil.copytree(base, r)
    key = resolve_key(plan, s["expect"]["entry"])
    rp = r / "receipts" / f"{key}.json"
    doc = load(rp) or {}
    check(bool((doc.get("consumed") or {}).get("baseEmitSha256")),
          f"{case}/unverifiable-base: the candidate arm receipt records a base emit hash to null (D4)")
    doc.setdefault("consumed", {})["baseEmitSha256"] = None
    rp.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    n0 = journal_len(r)
    res = evalrun(["resume", "--run", r], scen)
    expect(res.returncode, s["exit"], f"{case}/unverifiable-base: resume exit", "A-9")
    rows = journal_rows(r, n0)
    gapped = transitions_to(rows, key, s["expect"]["statusStartsWith"])
    check(bool(gapped), f"{case}/unverifiable-base: {s['expect']['entry']} went refused-reuse(<gap>) (A-9)")
    check(any(s["expect"]["gapNames"] in str(x.get("to", "")) + str(x.get("detail", "")) for x in gapped),
          f"{case}/unverifiable-base: the gap names the {s['expect']['gapNames']} (A-9)")
    check(not transitions_to(rows, key, "invalidated"),
          f"{case}/unverifiable-base: it was not called invalidated (A-9)")

    # (iv) the unchanged control: a resume over a run nothing touched does no child work
    s = steps["unchanged-control"]
    r = tmp / f"{case}-unchanged"
    shutil.copytree(base, r)
    ids0 = field(load(r / "ledger.json") or {}, "runIds", default=[])
    n0 = journal_len(r)
    res = evalrun(["resume", "--run", r], scen)
    expect(res.returncode, s["exit"], f"{case}/unchanged: resume exit", "D5")
    rows = journal_rows(r, n0)
    led = ledger_entries(load(r / "ledger.json") or {})
    terminal = [k for k, v in led.items() if execution_of(v) in ("completed", "reused")]
    check(all(execution_of(led[k]) == "reused" for k in terminal) if terminal else False,
          f"{case}/unchanged: every terminal-ok entry is reused (D5)")
    check(len(field(load(r / "ledger.json") or {}, "runIds", default=[])) > len(ids0),
          f"{case}/unchanged: a new runId is recorded (D5)")
    check(not [x for x in rows if str(x.get("to", "")) == "running"],
          f"{case}/unchanged: no `running` transition under the new runId - zero children (D5)")

    # (v) the same guarantee from `run`, not only `resume`
    s = steps["run-not-only-resume"]
    r = tmp / f"{case}-run-verb"
    shutil.copytree(base, r)
    n0 = journal_len(r)
    try:
        plug.unlink()
        res = evalrun(["run", "--run", r], scen)
    finally:
        plug.write_bytes(body)
    expect(res.returncode, s["exit"], f"{case}/run-verb: run exit", "D5")
    rows = journal_rows(r, n0)
    for key in s["invalidatedByName"]["entries"]:
        rk = resolve_key(plan, key)
        hits = [x for x in transitions_to(rows, rk, "invalidated")
                if s["invalidatedByName"]["namesPlugin"] in str(x.get("to", "")) + str(x.get("detail", ""))]
        check(bool(hits), f"{case}/run-verb: {key} invalidated on `run` too - no verb difference (D5)")

    # (vi) the frozen plan's premise changed
    s = steps["scope-changed"]
    r = tmp / f"{case}-scope-changed"
    shutil.copytree(base, r)
    sp = d / "scope.json"
    sbody = sp.read_bytes()
    n0 = journal_len(r)
    try:
        sp.write_bytes(sbody + b"\n")
        res = evalrun(["resume", "--run", r], scen)
    finally:
        sp.write_bytes(sbody)
    expect(res.returncode, s["exit"], f"{case}/scope-changed: resume exit", "D5 / A-3")
    blob = res.stdout + res.stderr
    for tok in s.get("stderrContains") or s["expect"].get("stderrContains") or []:
        check(tok in blob, f"{case}/scope-changed: refused by name, {tok!r} in the output (A-3)")
    check(s["expect"]["nextActionContains"] in blob,
          f"{case}/scope-changed: the refusal names the next action (scopeprobe) (D5)")
    check(not [x for x in journal_rows(r, n0) if str(x.get("to", "")) == "running"],
          f"{case}/scope-changed: no work was done (A-3)")


def case_14(tmp):
    case = "case-14-evidence-chain"
    o = load(EXPECTED / f"{case}.json")
    d = GEN / "cases" / case
    scen = d / "scenario.json"
    base = tmp / case
    do_plan(base, case)
    evalrun(["run", "--run", base], scen)
    plan = load(base / "plan.json") or {}
    victim_rel = None
    for p in sorted((base / "sweep" / "navownprobe").glob("baseline.json")):
        victim_rel = p.relative_to(base).as_posix()
    check(victim_rel is not None, f"{case}: the baseline navmesh emit exists to be damaged")

    for spec in o["finalize"]:
        r = tmp / f"{case}-{spec['id']}"
        shutil.copytree(base, r)
        v = r / victim_rel
        if spec["id"] == "evidence-missing":
            v.unlink()
        else:
            v.write_bytes(v.read_bytes() + b"\n")
        res = evalrun(["finalize", "--run", r], scen)
        e = spec["expect"]
        expect(res.returncode, spec["exit"], f"{case}/{spec['id']}: finalize exit", "D6")
        disp = load(r / "disposition.json") or {}
        blob = json.dumps(disp)
        integ = deep_find(disp, "integrity") or {}
        expect(integ.get("ok") if isinstance(integ, dict) else None, e["integrityOk"],
               f"{case}/{spec['id']}: evaluation.integrity.ok", "D6")
        defects = json.dumps((integ or {}).get("defects") or deep_find(disp, "defects") or [])
        check(e["defectsNamePath"] in defects.replace("\\\\", "/"),
              f"{case}/{spec['id']}: the defect names {e['defectsNamePath']} (D6)")
        check(e["defectKind"] in defects, f"{case}/{spec['id']}: the defect kind is {e['defectKind']} (D6)")
        expect(deep_find(disp, "refused"), e["refused"], f"{case}/{spec['id']}: evaluation.refused", "D6")
        nulls = json.dumps(deep_find(disp, "completedNullChecks") or [])
        check(e["completedNullChecksExcludes"] not in nulls,
              f"{case}/{spec['id']}: {e['completedNullChecksExcludes']} is NOT a completed null check (D6)")
        miss = field(disp, "missing", default=[])
        if e.get("missingIncludes"):
            check(e["missingIncludes"] in json.dumps(miss),
                  f"{case}/{spec['id']}: {e['missingIncludes']} is listed under missing (D6)")
        if e.get("missingStatusStartsWith"):
            check(e["missingStatusStartsWith"] in json.dumps(miss),
                  f"{case}/{spec['id']}: its status is {e['missingStatusStartsWith']}(...) (D6)")
        expect(field(disp, "complete"), False, f"{case}/{spec['id']}: evaluation.complete", "D6")
        def missing_row(key):
            cid, aid, part = split_key(resolve_key(plan, key))
            return [m for m in (miss or []) if isinstance(m, dict) and m.get("checkId") == cid
                    and m.get("armId") == aid and (m.get("part") or None) == part]

        for key in e.get("dependentEntriesUnderMissing") or []:
            rows = missing_row(key)
            check(any(str(m.get("status", "")).startswith(e["dependencyStatus"]) for m in rows),
                  f"{case}/{spec['id']}: {key} is listed under missing as {e['dependencyStatus']}(...) "
                  f"(A-5, transitive like reuse); got {[m.get('status') for m in rows]}")
        # A-8: an entry whose OWN output or run-internal input is the damaged file is a DIRECT loss even
        # while it is also a dependent, so either status is a correct answer for the candidate arm.
        for key in e.get("eitherStatusEntriesUnderMissing") or []:
            rows = missing_row(key)
            check(any(str(m.get("status", "")).startswith(w) for m in rows for w in e["eitherStatus"]),
                  f"{case}/{spec['id']}: {key} is listed under missing as one of "
                  f"{e['eitherStatus']} (A-8); got {[m.get('status') for m in rows]}")
        if "findingsExcludes" in e:
            check(e["findingsExcludes"] not in json.dumps(deep_find(disp, "findings") or []),
                  f"{case}/{spec['id']}: it is not counted under findings either (D6)")
        tb = spec.get("thenBundle")
        if tb:
            man_before = load(r / "bundle" / "manifest.json")
            res = evalrun(["bundle", "--run", r], scen)
            expect(res.returncode, tb["exit"], f"{case}/{spec['id']}: bundle exit", "D7")
            out = res.stdout + res.stderr
            check(tb["namesPath"] in out.replace("\\", "/") or tb["namesPath"] in json.dumps(load(r / "disposition.json") or {}).replace("\\\\", "/"),
                  f"{case}/{spec['id']}: bundle names the lost path (D7)")
            if tb.get("namesReceipt"):
                rk = resolve_key(plan, "geometry.navmesh-ownership__baseline__arm")
                rid = (load(r / "receipts" / f"{rk}.json") or {}).get("receiptId") or ""
                blob2 = out + json.dumps(load(r / "disposition.json") or {})
                check(rk in blob2 or (rid and rid in blob2),
                      f"{case}/{spec['id']}: bundle names the receipt whose evidence is gone (D7)")
            man_after = load(r / "bundle" / "manifest.json")
            check(man_after is None or man_after == man_before,
                  f"{case}/{spec['id']}: no manifest was written over a broken evidence chain (D7)")
            vr = evalrun(["verify-bundle", r / "bundle"])
            vj = load(r / "bundle" / "verify.json") or {}
            check(vr.returncode != 0 or (vj.get("bundle") or {}).get("complete") is not True,
                  f"{case}/{spec['id']}: no verify.json can claim a complete bundle (D8)")

    # a HEALTHY bundle, then one damage per answer, each on its own copy
    good = tmp / f"{case}-good"
    shutil.copytree(base, good)
    evalrun(["finalize", "--run", good], scen)
    evalrun(["bundle", "--run", good], scen)
    vr = evalrun(["verify-bundle", good / "bundle"])
    expect(vr.returncode, o["verifyBundle"]["healthyInPlaceExit"],
           f"{case}: verify-bundle on the healthy bundle", "D8")

    # a control file missing from the RUN is a bundle refusal, never a quietly shorter required list (A-9)
    rc = o["runControlMissing"]
    lost = tmp / f"{case}-lost-control"
    shutil.copytree(good, lost)
    shutil.rmtree(lost / "bundle", ignore_errors=True)
    (lost / rc["namesFile"]).unlink()
    res = evalrun(["bundle", "--run", lost], scen)
    expect(res.returncode, rc["bundleExit"], f"{case}/lost-control: bundle exit", "A-9")
    blob = scrub_paths({}, res, lost)
    check(rc["refusedBy"] in blob, f"{case}/lost-control: refused by name {rc['refusedBy']} (A-9)")
    check(rc["namesFile"] in blob, f"{case}/lost-control: it names {rc['namesFile']} (A-9)")
    check(not (lost / "bundle" / "manifest.json").exists(),
          f"{case}/lost-control: no manifest was written (A-9)")

    for i, spec in enumerate(o["verifyBundle"]["cases"], start=1):
        # The directory must NOT be named after the damage: verify.json echoes its own path and the CLI
        # prints it, so `schema-unsupported in blob` would pass on the folder name alone. (It did, first try.)
        b = tmp / f"{case}-vb{i}"
        shutil.copytree(good / "bundle", b)
        (b / "verify.json").unlink(missing_ok=True)
        man = load(b / "manifest.json") or {}
        removed = None
        if spec["id"] == "required-file-deleted":
            (b / "disposition.json").unlink()
        elif spec["id"] == "manifest-omits-required":
            rows = [e for e in man.get("evidence", []) if str(e.get("path", "")).startswith("evidence/")]
            check(bool(rows), f"{case}/{spec['id']}: the manifest lists a receipt output to drop")
            if not rows:
                continue
            removed = rows[0]["path"]
            man["evidence"] = [e for e in man.get("evidence", []) if e.get("path") != removed]
            (b / "manifest.json").write_text(json.dumps(man, indent=1), encoding="utf-8")
            check((b / removed).exists(), f"{case}/{spec['id']}: the dropped file is still on disk")
        elif spec["id"] == "evidence-bytes-altered":
            rows = [e for e in man.get("evidence", []) if str(e.get("path", "")).startswith("evidence/")]
            removed = rows[0]["path"] if rows else None
            if removed:
                p = b / removed
                p.write_bytes(p.read_bytes() + b"\n")
        elif spec["id"] == "control-file-deleted":
            (b / "STATUS.md").unlink()
        elif spec["id"] == "schema-unsupported":
            man["bundleSchema"] = 1
            (b / "manifest.json").write_text(json.dumps(man, indent=1), encoding="utf-8")
        res = evalrun(["verify-bundle", b])
        expect(res.returncode, spec["exit"], f"{case}/{spec['id']}: verify-bundle exit", "D8")
        vj = load(b / "verify.json") or {}
        blob = scrub_paths({k: v for k, v in vj.items() if k != "dir"}, res, b)
        check(spec["defectKind"] in blob,
              f"{case}/{spec['id']}: the defect is named {spec['defectKind']} (D8)")
        if spec.get("namesPath") is True and removed:
            check(removed in blob.replace("\\\\", "/"), f"{case}/{spec['id']}: it names {removed} (D8)")
        if spec.get("namesRemovedPath") and removed:
            check(removed in blob.replace("\\\\", "/"), f"{case}/{spec['id']}: it names {removed} (D8)")
        if "namesPath" in spec and isinstance(spec["namesPath"], str):
            check(spec["namesPath"] in blob, f"{case}/{spec['id']}: it names {spec['namesPath']} (D8)")
        if spec.get("completeIsFalse"):
            check((vj.get("bundle") or {}).get("complete") is False or vj == {},
                  f"{case}/{spec['id']}: bundle.complete is false (D8)")
        if spec.get("schemaSupportedIsFalse"):
            expect((vj.get("bundle") or {}).get("schemaSupported"), False,
                   f"{case}/{spec['id']}: bundle.schemaSupported", "A-1 / D8")

    # A-1's integration input: Q42's own demo bundle, verified on a COPY (verify writes verify.json)
    q = o["q42DemoBundle"]
    src = Path(os.environ.get(q["env"], q["path"]))
    if src.is_dir():
        cp = tmp / "q42-demo-bundle-copy"
        shutil.copytree(src, cp)
        (cp / "verify.json").unlink(missing_ok=True)
        res = evalrun(["verify-bundle", cp])
        expect(res.returncode, q["exit"], f"{case}: verify-bundle on Q42's demo bundle", "A-1")
        blob = json.dumps(load(cp / "verify.json") or {}) + res.stdout + res.stderr
        check(q["defectKind"] in blob,
              f"{case}: the Q42 bundle is refused as {q['defectKind']} by name (A-1)")
    else:
        print(f"  NOTE  {case}: {src} is absent (the Q42 demo run is untracked and lives only in the main "
              f"checkout), so A-1's refusal is covered here only by the synthetic bundleSchema 1 copy above.")


def case_15(tmp):
    case = "case-15-portable-incomplete"
    o = load(EXPECTED / f"{case}.json")
    priv = tmp / "fixtures-case-15"
    gen = subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(priv)],
                         capture_output=True, text=True)
    check(gen.returncode == 0, f"{case}: its own fixture root was generated")
    if gen.returncode != 0:
        return
    d = priv / "cases" / case
    scen = d / "scenario.json"
    run = tmp / case
    do_plan(run, case, gen=priv)
    evalrun(["run", "--run", run], scen)
    fin = evalrun(["finalize", "--run", run], scen)
    bun = evalrun(["bundle", "--run", run], scen)
    inc = o["incompleteness"]
    expect(fin.returncode, inc["finalizeExit"], f"{case}: finalize exit on an incomplete evaluation", "D7")
    expect(bun.returncode, inc["bundleExit"], f"{case}: bundle exit on an incomplete evaluation", "D7")
    disp = load(run / "disposition.json") or {}
    expect(field(disp, "complete"), inc["complete"], f"{case}: evaluation.complete", "D7")
    expect(field(disp, "claim"), inc["claim"], f"{case}: evaluation.claim", "D7")
    check(bool(field(disp, "missing", default=[])), f"{case}: missing is non-empty (D7)")

    moved = tmp / f"{case}-bundle-copy"
    shutil.copytree(run / "bundle", moved)
    (moved / "verify.json").unlink(missing_ok=True)
    shutil.rmtree(priv, ignore_errors=True)
    check(not priv.exists(), f"{case}: every original source path (plugins, scope, candidates, registry) is gone")
    res = evalrun(["verify-bundle", moved])
    e = o["afterDeletingEverySourcePath"]["expect"]
    expect(res.returncode, o["afterDeletingEverySourcePath"]["exit"],
           f"{case}: verify-bundle exit with no live path on disk", "D8")
    vj = load(moved / "verify.json") or {}
    bl = vj.get("bundle") or {}
    expect(bl.get("schemaSupported"), e["bundleSchemaSupported"], f"{case}: bundle.schemaSupported", "D8")
    expect(bl.get("structurallyValid"), e["bundleStructurallyValid"], f"{case}: bundle.structurallyValid", "D8")
    expect(bl.get("complete"), e["bundleComplete"], f"{case}: bundle.complete", "D8")
    expect(bl.get("missing"), [], f"{case}: bundle.missing", "D8")
    expect(bl.get("mismatched"), [], f"{case}: bundle.mismatched", "D8")
    ev = vj.get("evaluation") or {}
    expect(ev.get("complete"), e["evaluationComplete"], f"{case}: verify.json evaluation.complete", "D8")
    check(bool(ev.get("missing")), f"{case}: verify.json evaluation.missing is non-empty, read from the "
                                   f"bundle's disposition (D8)")
    man = load(moved / "manifest.json") or {}
    req = man.get("required") or []
    check(bool(req), f"{case}: the manifest carries `required` (D7)")
    for name in e["requiredIncludes"]:
        check(any(name == str(x).split("/")[-1] or name == x for x in req),
              f"{case}: required lists {name} (D7)")
    # `not absent` is vacuously true when `required` is empty, which is exactly the broken state, so the
    # non-emptiness is part of this assertion rather than only of the one above.
    absent = [x for x in req if not (moved / str(x)).exists()]
    check(bool(req) and not absent,
          f"{case}: every required path is present in the bundle (D8); {len(req)} required, absent {absent}")
    bad = [p.name for p in moved.rglob("*") if p.suffix.lower() in (".esp", ".esm", ".esl", ".bsa")]
    expect(bad, [], f"{case}: no plugin or asset binary in the bundle", "D8")


# ------------------------------------------- Q44: a supplied plugin's identity at plan time (16)

def sha_of(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def import_fingerprint(cfg):
    lines = [f"schema:{cfg.get('schemaVersion', 1)}\n", f"population:{cfg.get('population')}\n",
             f"state:{cfg.get('state')}\n"]
    lines += [f"plugin:{str(p.get('name') or '').lower()}:{p.get('sha256') or ''}\n"
              for p in cfg.get("plugins") or []]
    lines += [f"provider:{p.get('kind') or ''}:{str(p.get('name') or '').lower()}\n"
              for p in cfg.get("providers") or []]
    lines += [f"overlay:{o.get('kind') or ''}:{o.get('sha256') or ''}\n"
              for o in cfg.get("overlays") or []]
    return hashlib.sha256("".join(lines).encode("utf-8")).hexdigest()


def arm_map(plan: dict) -> dict:
    """armId AND candidate stem -> the plan's arm record (the oracle names arms by stem)."""
    out = {}
    for a in plan.get("arms") or []:
        out[a.get("armId")] = a
        out.setdefault(Path(a.get("candidateFile", "")).stem, a)
    return out


def requested_row(plan: dict, stem: str, name: str) -> dict:
    req = ((arm_map(plan).get(stem) or {}).get("requested") or {}).get("plugins") or []
    return next((p for p in req if str(p.get("name", "")).lower() == name.lower()), {})


def norm(p) -> str:
    return str(p or "").replace("\\", "/")


def case_16(tmp):
    case = "case-16-supplied-identity"
    o = load(EXPECTED / f"{case}.json")
    d = GEN / "cases" / case
    scen = d / "scenario.json"
    inst, game = GEN / "instance", GEN / "game"
    plug = o["fixture"]["suppliedPlugin"]
    stem = o["fixture"]["armStem"]
    supplied_file = d / "supplied" / "candidate-02-supplied.json"

    def plan_args(instance: Path | None, profile=True, with_game=True, cand: Path | None = None):
        """The flags are separable on purpose: A-3 turns on a walk that is missing ONE of them."""
        args = ["--supplied", f"{plug}={cand or supplied_file}"]
        if instance is not None:
            args += ["--instance", instance]
            if profile:
                args += ["--profile", "fixture"]
            if with_game:
                args += ["--game", game]
        return args

    # ---------------------------------------------------------------- the plan (D1)
    run = tmp / case
    p = do_plan(run, case, plan_args(inst))
    expect(p.returncode, o["plan"]["exit"], f"{case}: plan exit", "D1")
    plan = load(run / "plan.json") or {}
    if not plan:
        check(False, f"{case}: plan.json written")
        return
    for s, want in o["plan"]["armsRequested"].items():
        req = ((arm_map(plan).get(s) or {}).get("requested") or {}).get("plugins") or []
        expect([x.get("name") for x in req], want["plugins"], f"{case}: {s} requested plugin order", "D1")
        expect([x.get("hashSource") for x in req], want["hashSource"],
               f"{case}: {s} requested hashSource per plugin", "D1")
        expect(sorted(x.get("name") for x in req if x.get("sha256") is None), sorted(want["sha256Null"]),
               f"{case}: {s} requested sha256 null for", "D1")

    rr = o["plan"]["resolvedRow"]
    row = requested_row(plan, rr["arm"], rr["plugin"])
    expect(row.get("hashSource"), rr["hashSource"], f"{case}: {rr['plugin']} hashSource", "D1")
    expect(row.get("sha256"), sha_of(inst / rr["sha256Of"]),
           f"{case}: {rr['plugin']} sha256 is the hash of {rr['sha256Of']}", "D1 - the precedence winner")
    check(norm(row.get("path")).endswith(rr["pathEndsWith"]),
          f"{case}: its path ends with {rr['pathEndsWith']} (got {row.get('path')!r}) (D1)")
    got_by = row.get("providedBy") or []
    expect([x.get("layer") for x in got_by], [x["layer"] for x in rr["providedBy"]],
           f"{case}: providedBy layers in precedence order", "D1")
    for i, want in enumerate(rr["providedBy"]):
        g = got_by[i] if i < len(got_by) else {}
        expect(g.get("sha256"), sha_of(inst / want["sha256Of"]),
               f"{case}: providedBy[{i}] ({want['layer']}) sha256", "D1 - every copy hashed")
        check(norm(g.get("path")).endswith(want["pathEndsWith"]),
              f"{case}: providedBy[{i}] path ends with {want['pathEndsWith']} (got {g.get('path')!r}) (D1)")

    sup = (arm_map(plan).get(stem) or {}).get("supplied") or {}
    expect(sup.get("payloadMissing"), o["plan"]["supplied"]["payloadMissing"],
           f"{case}: supplied.payloadMissing", "D1")
    expect(sup.get("outOfView"), o["plan"]["supplied"]["outOfView"],
           f"{case}: supplied.outOfView", "D1 - resolved from layers 4-5")
    blocked = [c.get("checkId", c.get("id")) for c in (plan.get("checks") or [])
               for r in (c.get("arms") or [])
               if r.get("armId") == stem and r.get("status") == o["plan"]["supplied"]["noStatus"]]
    expect(blocked, [], f"{case}: no check on the supplied arm is {o['plan']['supplied']['noStatus']}", "D1")

    # ---------------------------------------------------------------- the four plan variants (D1)
    for v in o["variants"]:
        vid = v["id"]
        fl = v.get("flags") or {"instance": vid != "no-instance", "profile": True, "game": True}
        vrun = tmp / f"{case}-{vid}"
        vinst = None
        if fl["instance"]:
            vinst = tmp / f"{case}-{vid}-instance"
            shutil.copytree(inst, vinst)
            if v.get("plant"):
                # A-4: a copy inside the view's OWN mod folder, so `Mods` absent and `Mods` empty
                # resolve differently over one tree.
                (vinst / "mods" / v["plant"]).mkdir(parents=True, exist_ok=True)
                (vinst / "mods" / v["plant"] / plug).write_bytes(
                    o["fixture"]["bytes"]["D"].encode("utf-8"))
            if vid == "overwrite-copy":
                (vinst / "overwrite" / plug).write_bytes(
                    o["fixture"]["bytes"]["C"].encode("utf-8"))
            elif vid == "modlist-removed":
                (vinst / "profiles" / "fixture" / "modlist.txt").unlink()
            elif vid == "modlist-reordered":
                ml = vinst / "profiles" / "fixture" / "modlist.txt"
                body = ml.read_text(encoding="utf-8")
                ml.write_text(body.replace("-Supplied Patch A\n-Supplied Patch B\n",
                                           "-Supplied Patch B\n-Supplied Patch A\n"),
                              encoding="utf-8", newline="\n")
                check("-Supplied Patch B\n-Supplied Patch A" in
                      ml.read_text(encoding="utf-8").replace("\r\n", "\n"),
                      f"{case}/{vid}: the modlist really was reordered")
        vcand = d / "supplied" / v["candidateFile"] if v.get("candidateFile") else None
        r = do_plan(vrun, case, plan_args(vinst, fl.get("profile", True), fl.get("game", True), vcand))
        expect(r.returncode, v["exit"], f"{case}/{vid}: plan exit", "D1")
        vplan = load(vrun / "plan.json") or {}
        vstem = Path(vcand).stem if vcand else stem     # the arm is named after ITS candidate file
        row = requested_row(vplan, vstem, plug)
        expect(row.get("hashSource"), v["hashSource"], f"{case}/{vid}: hashSource", "D1")
        if v.get("sha256IsNull"):
            expect(row.get("sha256"), None, f"{case}/{vid}: sha256", "D1")
            reason = str(row.get("unstatedReason") or "")
            check(v["reasonContains"] in reason,
                  f"{case}/{vid}: the unstatedReason names {v['reasonContains']!r} (got {reason[:120]!r}) (D1)")
        if v.get("sha256Of"):
            expect(row.get("sha256"), sha_of(vinst / v["sha256Of"]), f"{case}/{vid}: sha256", "D1")
            check(norm(row.get("path")).endswith(v["pathEndsWith"]),
                  f"{case}/{vid}: path ends with {v['pathEndsWith']} (got {row.get('path')!r}) (D1)")
        got_by = row.get("providedBy") or []
        if v.get("providedBy"):
            expect([x.get("layer") for x in got_by], [x["layer"] for x in v["providedBy"]],
                   f"{case}/{vid}: providedBy layers", "D1")
            for i, want in enumerate(v["providedBy"]):
                g = got_by[i] if i < len(got_by) else {}
                expect(g.get("sha256"), sha_of(vinst / want["sha256Of"]),
                       f"{case}/{vid}: providedBy[{i}] ({want['layer']}) sha256", "D1")
        if v.get("providedByLayers"):
            expect([x.get("layer") for x in got_by], v["providedByLayers"],
                   f"{case}/{vid}: providedBy layers", "D1")
        if v.get("providedByCount") is not None:
            expect(len(got_by), v["providedByCount"], f"{case}/{vid}: providedBy row count", "D1")
        if "outOfView" in v:
            expect(((arm_map(vplan).get(vstem) or {}).get("supplied") or {}).get("outOfView"),
                   v["outOfView"], f"{case}/{vid}: supplied.outOfView", "D1")
        if "payloadMissing" in v:
            expect(((arm_map(vplan).get(vstem) or {}).get("supplied") or {}).get("payloadMissing"),
                   v["payloadMissing"], f"{case}/{vid}: supplied.payloadMissing", "D1 / ruling A-1")

    # ---------------------------------------------------------------- the run (D2, D3)
    evalrun(["run", "--run", run], scen)
    sw = o["run"]["sweepArmReceipt"]
    rc = receipt(run, plan, sw["entry"]) or {}
    val = rc.get("validation") or {}
    expect(val.get("accepted"), sw["accepted"], f"{case}: {sw['entry']} accepted", "D2")
    expect(val.get("identityLimits"), sw["identityLimits"],
           f"{case}: {sw['entry']} validation.identityLimits", "D2 - a stated hash, met, leaves nothing to limit")
    want = o["run"]["consumedPluginRow"]
    for jr in o["run"]["jsonReceipts"]:
        rc = receipt(run, plan, jr["entry"]) or {}
        cons = rc.get("consumed") or {}
        cfg = cons.get("configuration") or {}
        expect(cons.get("configurationSource"), jr["configurationSource"],
               f"{case}: {jr['entry']} consumed.configurationSource", "D3")
        got = next((x for x in (cfg.get("plugins") or [])
                    if str(x.get("name", x.get("Name", ""))).lower() == want["name"].lower()), {})
        expect(got.get("sha256"), sha_of(inst / want["sha256Of"]),
               f"{case}: {jr['entry']} consumed {want['name']} sha256", "D3")
        check(norm(got.get("path")).endswith(want["pathEndsWith"]),
              f"{case}: {jr['entry']} consumed {want['name']} path ends with {want['pathEndsWith']} "
              f"(got {got.get('path')!r}) (D3)")
        expect(got.get("hashSource"), want["hashSource"],
               f"{case}: {jr['entry']} consumed {want['name']} hashSource", "D3")
        check(o["run"]["noIdentityLimitsEntryFor"] not in json.dumps(cfg.get("identityLimits") or []),
              f"{case}: {jr['entry']} records no identityLimits entry for "
              f"{o['run']['noIdentityLimitsEntryFor']} (D3)")
    bc = o["run"]["baselineControl"]
    rb = receipt(run, plan, bc["entry"]) or {}
    expect((rb.get("validation") or {}).get("accepted"), bc["accepted"],
           f"{case}: {bc['entry']} accepted (the baseline control)", "D2")

    # ---------------------------------------------------------------- resume, unchanged (D4)
    s = o["resume"]
    r = tmp / f"{case}-unchanged"
    shutil.copytree(run, r)
    n0 = journal_len(r)
    res = evalrun(["resume", "--run", r], scen)
    expect(res.returncode, s["exit"], f"{case}/unchanged: resume exit", "D4")
    rows = journal_rows(r, n0)
    led = ledger_entries(load(r / "ledger.json") or {})
    arm_keys = [k for k, v in led.items() if v.get("armId") == stem or f"__{stem}" in k]
    terminal = [k for k in arm_keys if execution_of(led[k]) in ("completed", "reused")]
    check(bool(terminal) and all(execution_of(led[k]) == "reused" for k in terminal),
          f"{case}/unchanged: every terminal-ok entry of the supplied arm is reused "
          f"(got {sorted({execution_of(led[k]) for k in terminal})}) (D4)")
    check(s["stdoutContains"] in (res.stdout + res.stderr),
          f"{case}/unchanged: the resume printed {s['stdoutContains']!r} (D4)")
    check(not [x for x in rows if str(x.get("to", "")) == "running" and x.get("armId") == stem],
          f"{case}/unchanged: no `running` transition on the supplied arm - zero children (D4)")
    check(not [x for x in rows if str(x.get("to", "")) == "running"],
          f"{case}/unchanged: no `running` transition at all under the new runId (D4)")

    # ---------------------------------------------------------------- the probe read another copy (D2)
    w = o["wrongCopy"]
    wscen = d / w["scenario"]
    r2 = tmp / f"{case}-wrong-copy"
    do_plan(r2, case, plan_args(inst), scenario=wscen)
    evalrun(["run", "--run", r2], wscen)
    plan2 = load(r2 / "plan.json") or {}
    rc = receipt(r2, plan2, w["entry"])
    if rc is None:
        check(False, f"{case}/wrong-copy: a receipt was written for {w['entry']}")
    else:
        expect((rc.get("validation") or {}).get("accepted"), w["accepted"],
               f"{case}/wrong-copy: {w['entry']} accepted", "D2")
        causes = [x.get("cause") for x in ((rc.get("validation") or {}).get("rejections") or [])]
        check(w["cause"] in causes, f"{case}/wrong-copy: rejected for {w['cause']} (got {causes})")
        detail = rejection_detail(rc, w["cause"])
        for tok in w["detailContains"]:
            check(tok in detail, f"{case}/wrong-copy: the detail names {tok!r} (got {detail[:200]!r})")
        h = w["detailNamesBothHashesAt12Hex"]
        for role, rel in h.items():
            check(sha_of(inst / rel)[:12] in detail,
                  f"{case}/wrong-copy: the detail names the {role} hash at 12 hex "
                  f"({sha_of(inst / rel)[:12]}); got {detail[:200]!r}")
        rb = receipt(r2, plan2, w["baselineStillAccepted"]) or {}
        check((rb.get("validation") or {}).get("accepted") is True,
              f"{case}/wrong-copy: the undamaged baseline arm is still accepted")

    # ------------------------------------------- the same content at a path the plan did not name (D2)
    ic = o["identicalCopyElsewhere"]
    icinst = tmp / f"{case}-identical-copy-instance"
    shutil.copytree(inst, icinst)
    twin = icinst / "mods" / "Supplied Patch A copy"
    twin.mkdir(parents=True, exist_ok=True)
    (twin / plug).write_bytes((inst / "mods" / "Supplied Patch A" / plug).read_bytes())
    ml = icinst / "profiles" / "fixture" / "modlist.txt"
    ml.write_text(ml.read_text(encoding="utf-8").replace(
        "-Supplied Patch A\n", "-Supplied Patch A\n-Supplied Patch A copy\n", 1),
        encoding="utf-8", newline="\n")
    check(sha_of(twin / plug) == sha_of(inst / "mods" / "Supplied Patch A" / plug),
          f"{case}/identical-copy: the twin folder's bytes really are identical to A's")
    icscen = d / ic["scenario"]
    icrun = tmp / f"{case}-identical-copy"
    do_plan(icrun, case, plan_args(icinst), scenario=icscen)
    icplan = load(icrun / "plan.json") or {}
    expect([x.get("layer") for x in (requested_row(icplan, stem, plug).get("providedBy") or [])],
           ic["planProvidedByLayers"], f"{case}/identical-copy: providedBy layers", "D1")
    evalrun(["run", "--run", icrun], icscen)
    rc = receipt(icrun, icplan, ic["entry"]) or {}
    val = rc.get("validation") or {}
    expect(val.get("accepted"), ic["accepted"],
           f"{case}/identical-copy: {ic['entry']} accepted", "D2 - the binding is to content")
    lims = val.get("identityLimits") or []
    expect(len(lims), ic["identityLimitsCount"],
           f"{case}/identical-copy: validation.identityLimits row count", "D2")
    w = ic["identityLimitsRow"]
    lim = next((x for x in lims if str(x.get("plugin", "")).lower() == w["plugin"].lower()), {})
    expect(lim.get("plugin"), w["plugin"], f"{case}/identical-copy: the limit names the plugin", "D2")
    expect(lim.get("recordedSha256"), sha_of(icinst / w["recordedSha256Of"]),
           f"{case}/identical-copy: the limit's recordedSha256", "D2")
    check(norm(lim.get("plannedPath")).endswith(w["plannedPathEndsWith"]),
          f"{case}/identical-copy: plannedPath ends with {w['plannedPathEndsWith']} "
          f"(got {lim.get('plannedPath')!r}) (D2)")
    check(norm(lim.get("emitPath")).endswith(w["emitPathEndsWith"]),
          f"{case}/identical-copy: emitPath ends with {w['emitPathEndsWith']} "
          f"(got {lim.get('emitPath')!r}) (D2)")
    for tok in w["detailContains"]:
        check(tok in str(lim.get("detail") or ""),
              f"{case}/identical-copy: the detail names {tok!r} (got {str(lim.get('detail'))[:160]!r}) (D2)")

    # ---------------------------------------------------------------- the resolved bytes changed (D4)
    b = o["bytesChanged"]
    r3 = tmp / f"{case}-bytes-changed"
    shutil.copytree(run, r3)
    victim = inst / "mods" / "Supplied Patch A" / plug
    body = victim.read_bytes()
    n0 = journal_len(r3)
    try:
        victim.write_bytes(body + b"a byte that changes the supplied copy's content hash\n")
        res = evalrun(["resume", "--run", r3], scen)
    finally:
        victim.write_bytes(body)
    expect(res.returncode, b["exit"], f"{case}/bytes-changed: resume exit", "D4")
    rows = journal_rows(r3, n0)
    inv = b["invalidated"]
    for key in inv["entries"]:
        rk = resolve_key(plan, key)
        hits = [x for x in transitions_to(rows, rk, inv["transitionStartsWith"])
                if all(t in str(x.get("to", "")) + str(x.get("detail", "")) for t in inv["detailContains"])]
        check(bool(hits), f"{case}/bytes-changed: {key} invalidated naming {inv['detailContains']} (D4); "
                          f"got {[str(x.get('to'))[:90] for x in transitions_to(rows, rk, '')][:3]}")
    led = ledger_entries(load(r3 / "ledger.json") or {})
    for key in b["baselineStaysReused"]["entries"]:
        rk = resolve_key(plan, key)
        expect(execution_of(led.get(rk)), b["baselineStaysReused"]["restingStatus"],
               f"{case}/bytes-changed: {key} resting status", "D4 - the baseline is untouched")
    check(b["stdoutContains"] in (res.stdout + res.stderr),
          f"{case}/bytes-changed: the resume printed {b['stdoutContains']!r} - a change, never a gap (D4)")


def case_17(tmp):
    """A generic MCP import is identity-bound, evidence-backed and relocatable."""
    case = "case-01-geometry-and-quest"
    run = tmp / "case-17-mcp-import"
    expect(do_plan(run, case).returncode, 0, "case-17: plan exit", "import")
    _, _, _, scen = case_paths(case)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, "case-17: accounting run", "import")
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((c for c in ("loadability.dangling-references", "loadability.missing-assets",
                            "records.contested-fields", "records.cell-attributes")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, "case-17: fixture registry contains an mcp-only check")
    planned_arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or [])]
    arms = [a for a in (plan.get("arms") or []) if a.get("armId") in planned_arm_ids]
    check(len(arms) >= 2, "case-17: mcp-only check applies to two arms")

    def make_import(folder, arm, detector="no-finding", findings=0):
        folder.mkdir(parents=True, exist_ok=True)
        request, response = folder / "request.json", folder / "response.json"
        request.write_text(json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}),
                           encoding="utf-8")
        response.write_text(json.dumps({"findings": findings}), encoding="utf-8")
        req = arm["requested"]
        cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
               "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                           for p in req["plugins"]],
               "providers": req["providers"], "overlays": req["overlays"],
               "population": req["population"], "state": "baseline"}
        cfg["fingerprint"] = import_fingerprint(cfg)
        doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid,
               "armId": arm["armId"], "state": "baseline", "capturedUtc": "2026-09-13T00:00:00Z",
               "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
               "result": {"detector": detector, "findings": findings,
                          "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
               "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                           for role, p in (("request", request), ("response", response))]}
        manifest = folder / "import.json"
        manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
        return manifest, doc

    manifest, _ = make_import(tmp / "case-17-good-null", arms[0])
    r = evalrun(["import-results", "--run", run, "--file", manifest])
    expect(r.returncode, 0, "case-17: accepted no-finding import", "import")
    imported = load(run / "receipts" / f"{cid}__{arms[0]['armId']}.json") or {}
    check((imported.get("validation") or {}).get("accepted") is True,
          "case-17: imported receipt accepted")
    expect((imported.get("result") or {}).get("detector"), "no-finding",
           "case-17: null conclusion retained", "import")
    expect(len(imported.get("outputs") or []), 3,
           "case-17: manifest plus raw request and response retained", "import")

    # Missing raw response, a foreign arm and a plugin/fingerprint mismatch all refuse before a receipt changes.
    missing, missing_doc = make_import(tmp / "case-17-missing", arms[1], "finding", 2)
    (missing.parent / "response.json").unlink()
    expect(evalrun(["import-results", "--run", run, "--file", missing]).returncode, 2,
           "case-17: missing raw response refuses", "import")
    wrong_arm, wrong_arm_doc = make_import(tmp / "case-17-wrong-arm", arms[1], "finding", 2)
    wrong_arm_doc["armId"] = "foreign-arm"
    wrong_arm.write_text(json.dumps(wrong_arm_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_arm]).returncode, 2,
           "case-17: foreign arm refuses", "import")
    wrong_order, wrong_order_doc = make_import(tmp / "case-17-wrong-order", arms[1], "finding", 2)
    wrong_order_doc["configuration"]["plugins"] = list(
        reversed(wrong_order_doc["configuration"]["plugins"]))
    wrong_order_doc["configuration"]["fingerprint"] = import_fingerprint(
        wrong_order_doc["configuration"])
    wrong_order.write_text(json.dumps(wrong_order_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_order]).returncode, 2,
           "case-17: wrong plugin order refuses", "import")
    wrong_hash, wrong_hash_doc = make_import(tmp / "case-17-wrong-hash", arms[1], "finding", 2)
    wrong_hash_doc["configuration"]["plugins"][0]["sha256"] = "0" * 64
    wrong_hash_doc["configuration"]["fingerprint"] = import_fingerprint(wrong_hash_doc["configuration"])
    wrong_hash.write_text(json.dumps(wrong_hash_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_hash]).returncode, 2,
           "case-17: wrong plugin bytes refuse", "import")
    stale, stale_doc = make_import(tmp / "case-17-stale-output", arms[1], "finding", 2)
    stale_doc["outputs"][1]["sha256"] = "0" * 64
    stale.write_text(json.dumps(stale_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", stale]).returncode, 2,
           "case-17: stale raw response hash refuses", "import")
    malformed, malformed_doc = make_import(tmp / "case-17-malformed-coverage", arms[1], "finding", 2)
    malformed_doc["result"]["coverage"] = [{"Population": "records", "Judged": -1,
                                                "Unjudgeable": {"parse": 0.5}}]
    malformed.write_text(json.dumps(malformed_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", malformed]).returncode, 2,
           "case-17: malformed coverage counts refuse", "import")
    zero, zero_doc = make_import(tmp / "case-17-zero-coverage", arms[1])
    zero_doc["result"]["coverage"][0]["Judged"] = 0
    zero.write_text(json.dumps(zero_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", zero]).returncode, 2,
           "case-17: zero-assessed evidence refuses", "import")
    no_population, no_population_doc = make_import(tmp / "case-17-no-population", arms[1])
    no_population_doc["result"]["coverage"][0]["Population"] = ""
    no_population.write_text(json.dumps(no_population_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", no_population]).returncode, 2,
           "case-17: coverage without a population refuses", "import")
    bad_path, bad_path_doc = make_import(tmp / "case-17-bad-path", arms[1])
    bad_path_doc["outputs"][0]["path"] = {"not": "a path"}
    bad_path.write_text(json.dumps(bad_path_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", bad_path]).returncode, 2,
           "case-17: malformed output path refuses without crashing", "import")
    bad_config, bad_config_doc = make_import(tmp / "case-17-bad-config", arms[1])
    bad_config_doc["configuration"] = ["not", "an", "object"]
    bad_config.write_text(json.dumps(bad_config_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", bad_config]).returncode, 2,
           "case-17: malformed configuration refuses without crashing", "import")
    pending_key = f"{cid}__{arms[1]['armId']}"
    expect(((load(run / "ledger.json") or {}).get("entries") or {}).get(pending_key, {}).get("status"),
           "mcp-only", "case-17: refused import remains explicitly mcp-only", "import")

    finding, _ = make_import(tmp / "case-17-good-finding", arms[1], "finding", 2)
    expect(evalrun(["import-results", "--run", run, "--file", finding]).returncode, 0,
           "case-17: accepted finding import", "import")
    expect(evalrun(["resume", "--run", run]).returncode, 0,
           "case-17: resume accepts and retains imported evidence", "import")
    resumed = load(run / "ledger.json") or {}
    expect(((resumed.get("entries") or {}).get(pending_key) or {}).get("status"), "reused",
           "case-17: imported finding is reused rather than rerun", "import")

    imported_response = run / "imports" / pending_key / "02-response.json"
    original_response = imported_response.read_bytes()
    imported_response.write_bytes(original_response + b"\nchanged")
    expect(evalrun(["finalize", "--run", run]).returncode, 3,
           "case-17: changed imported raw evidence refuses finalize", "import")
    imported_response.write_bytes(original_response)
    expect(evalrun(["finalize", "--run", run]).returncode, 0,
           "case-17: finalize accepts imported receipts", "import")
    bundle = tmp / "case-17-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0,
           "case-17: bundle includes imported evidence", "import")
    moved = tmp / "case-17-relocated"
    shutil.copytree(bundle, moved)
    expect(evalrun(["verify-bundle", moved]).returncode, 0,
           "case-17: relocated imported evidence verifies", "import")
    bundled_response = next((moved / "evidence" / "imports" / pending_key).glob("*response.json"))
    bundled_response.write_bytes(bundled_response.read_bytes() + b"\nchanged")
    expect(evalrun(["verify-bundle", moved]).returncode, 3,
           "case-17: changed bundled raw evidence refuses verification", "import")


def view_providers(mods):
    """HarnessOrderCli.Resolve's provider list for a configuration whose view is `mods`: the overwrite
    layer, the view's mod folders in order, the game's Data folder. Written from the C#, not from evalrun."""
    return ([{"name": "overwrite", "kind": "overwrite"}] + [{"name": m, "kind": "mod"} for m in mods]
            + [{"name": "Data", "kind": "data"}])


def mcp_import(folder: Path, plan: dict, cid: str, arm: dict, providers: list):
    """A schema-1 import of one no-finding MCP result for `arm`, stating `providers` as what it read and
    everything else from the arm's frozen request. Returns the manifest path."""
    folder.mkdir(parents=True, exist_ok=True)
    request, response = folder / "request.json", folder / "response.json"
    request.write_text(json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}),
                       encoding="utf-8")
    response.write_text(json.dumps({"findings": 0}), encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in providers], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"],
           "state": "baseline", "capturedUtc": "2026-09-16T00:00:00Z",
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                       for role, p in (("request", request), ("response", response))]}
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def case_18(tmp):
    """D1: an arm's requested providers are what THAT arm's configuration reads, and a plan frozen under
    the old meaning (the scope's providers on every arm) is refused by name wherever it would bind a
    new receipt."""
    case = "case-18-arm-view-providers"
    d, scope_path, cdir, scen = case_paths(case)
    scope = load(scope_path) or {}
    scope_prov = [{"name": v["Name"], "kind": v["Kind"]} for v in scope["configuration"]["providers"]]
    view = ["fixture-mods", "fixture-extra-mods"]
    expect(scope_prov, view_providers(view), f"{case}: the fixture scope's view is overwrite + two mods + Data")

    # ---------------------------------------------------------------- the plan (D1)
    run = tmp / case
    expect(do_plan(run, case).returncode, 0, f"{case}: plan exit", "D1")
    plan = load(run / "plan.json") or {}
    arms = arm_map(plan)
    want = {"candidate-00-baseline": view_providers(view),          # Mods == the scope's view
            "candidate-01-narrow-view": view_providers(["fixture-mods"]),
            "candidate-02-same-view": view_providers(view),
            "candidate-03-scope-view": scope_prov,                   # no Mods: the scope's view
            "candidate-04-empty-view": view_providers([])}           # Mods []: no mod folder at all
    for stem, providers in want.items():
        got = ((arms.get(stem) or {}).get("requested") or {}).get("providers")
        expect(got, providers, f"{case}: {stem} requested.providers", "D1 - the arm's own view")
    narrow = arms.get("candidate-01-narrow-view") or {}
    same = arms.get("candidate-02-same-view") or {}
    check("fixture-extra-mods" not in json.dumps(((narrow.get("requested") or {}).get("providers")) or []),
          f"{case}: the narrow arm's request excludes the provider only the scope's view has (D1)")

    checks = plan_checks(plan)
    cid = next((c for c in ("loadability.dangling-references", "records.cell-attributes")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, f"{case}: fixture registry contains an mcp-only check")
    if cid is None or not narrow or not same:
        return

    # ---------------------------------------------------------------- import-results against each request
    def imp(tag, arm, providers, want_exit, want_text=None):
        m = mcp_import(tmp / f"{case}-{tag}", plan, cid, arm, providers)
        r = evalrun(["import-results", "--run", run, "--file", m])
        expect(r.returncode, want_exit, f"{case}/{tag}: import-results exit", "D1 / §4.8")
        if want_text:
            check(want_text in r.stderr, f"{case}/{tag}: the refusal names {want_text!r} "
                                         f"(got {r.stderr.strip()[-220:]!r})")

    imp("narrow-with-scope-providers", narrow, scope_prov, 2, "identity-mismatch")
    imp("narrow-with-own-view", narrow, view_providers(["fixture-mods"]), 0)
    imp("same-with-narrower-providers", same, view_providers(["fixture-mods"]), 2, "identity-mismatch")
    imp("same-with-own-view", same, view_providers(view), 0)

    # ---------------------------------------------------------------- the sweep: arm, reverse, diff (§4.3/§4.4)
    srun = tmp / f"{case}-sweep"
    expect(do_plan(srun, case, ["--alt", "candidate-01-narrow-view.json", "--select", "navownprobe"]).returncode,
           0, f"{case}/sweep: plan exit", "D1")
    splan = load(srun / "plan.json") or {}
    expect(evalrun(["run", "--run", srun], scen).returncode, 0, f"{case}/sweep: run exit", "D2")
    sarms = arm_map(splan)
    sn, sb = sarms.get("candidate-01-narrow-view") or {}, sarms.get("baseline") or {}

    def request_fp(arm, state, schema):
        req = arm.get("requested") or {}
        return import_fingerprint({"schemaVersion": schema, "population": req.get("population"), "state": state,
                                   "plugins": req.get("plugins"), "providers": req.get("providers"),
                                   "overlays": req.get("overlays")})

    nav = "geometry.navmesh-ownership"
    parts = {p: load(srun / "receipts" / f"{nav}__{sn.get('armId')}__{p}.json") or {}
             for p in ("arm", "reverse", "diff")}
    for p, rc in parts.items():
        expect((rc.get("validation") or {}).get("accepted"), True, f"{case}/sweep: {p} part accepted", "D2")
    for p, ref, label in (("arm", sn, "the narrow arm's own"), ("reverse", sb, "the BASELINE arm's")):
        cfg = (parts[p].get("consumed") or {}).get("configuration") or {}
        expect(cfg.get("fingerprint"), request_fp(ref, cfg.get("state"), cfg.get("schemaVersion")),
               f"{case}/sweep: the {p} emit's fingerprint is the digest of {label} request", "§4.3 / §4.4")
    check("fixture-extra-mods" in json.dumps((parts["reverse"].get("consumed") or {}).get("configuration") or {}),
          f"{case}/sweep: the reverse emit read the baseline's view, the extra folder included (§4.4)")
    ins = {x.get("part"): x.get("sha256") for x in ((parts["diff"].get("consumed") or {}).get("inputs") or [])}
    expect(ins, {q: ((parts[q].get("outputs") or [{}])[0]).get("sha256") for q in ("arm", "reverse")},
           f"{case}/sweep: the diff's inputs are exactly the accepted arm and reverse emits", "§4.4")

    # ---------------------------------------------------------------- a plan frozen under the old meaning
    old = tmp / f"{case}-old-meaning"
    shutil.copytree(srun, old)
    op = load(old / "plan.json")
    op.pop("requestProviders", None)
    for a in op.get("arms") or []:
        a["requested"]["providers"] = [dict(v) for v in (op.get("configuration") or {}).get("providers") or []]
    (old / "plan.json").write_text(json.dumps(op, indent=1, ensure_ascii=False), encoding="utf-8")
    (old / "plan.sha256").write_text(sha_of(old / "plan.json") + "\n", encoding="utf-8")
    for verb in ("run", "resume"):
        r = evalrun([verb, "--run", old], scen)
        expect(r.returncode, 2, f"{case}/old-meaning: {verb} exit", "D1 - refused by name")
        check("plan-request-outdated" in r.stdout + r.stderr,
              f"{case}/old-meaning: {verb} names plan-request-outdated (got {(r.stdout + r.stderr).strip()[-200:]!r})")
    m = mcp_import(tmp / f"{case}-old-meaning-import", op, cid, next(a for a in op["arms"]
                                                                 if a["armId"] == sn.get("armId")), scope_prov)
    r = evalrun(["import-results", "--run", old, "--file", m])
    expect(r.returncode, 2, f"{case}/old-meaning: import-results exit", "D1 - refused by name")
    check("plan-request-outdated" in r.stderr,
          f"{case}/old-meaning: import-results names plan-request-outdated (got {r.stderr.strip()[-200:]!r})")
    expect(evalrun(["finalize", "--run", old], scen).returncode, 0,
           f"{case}/old-meaning: finalize still reads what was already bound", "compatibility")
    ob = tmp / f"{case}-old-meaning-bundle"
    expect(evalrun(["bundle", "--run", old, "--out", ob], scen).returncode, 0,
           f"{case}/old-meaning: bundle still packages it", "compatibility")
    expect(evalrun(["verify-bundle", ob]).returncode, 0,
           f"{case}/old-meaning: verify-bundle still verifies it", "compatibility")


CASES = {"case-01": case_01, "case-02": case_02, "case-03": case_03, "case-04": case_04, "case-05": case_05,
         "case-06": case_06, "case-07": case_07, "case-08": case_08, "case-09": case_09, "case-10": case_10,
         "case-11": case_11, "case-12": case_12, "case-13": case_13, "case-14": case_14, "case-15": case_15,
         "case-16": case_16, "case-17": case_17, "case-18": case_18}


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
