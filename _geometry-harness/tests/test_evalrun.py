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
import struct
import subprocess
import sys
import tempfile
import time
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
FIXT = HERE / "fixtures" / "evalrun"          # tracked: the generator, the fakes, the expectations
EXPECTED = FIXT / "expected"
GEN = FIXT / "_generated"                     # generated per run; never tracked (set in main())
EVALRUN = HARNESS / "evalrun.py"
# conflictdiff is built from this checkout's source into the run's temp folder once per run (use_conflictdiff), so
# a stale binary is never tested; HOUSECARL_EVALRUN_TEST_HARNESS names a tree to use as given instead (reproducing
# an old run). Never a silent skip - a failed build or a missing binary fails the run and says so.
DIFF_NAME = "conflictdiff.exe" if os.name == "nt" else "conflictdiff"
REAL_HARNESS = Path(os.environ.get("HOUSECARL_EVALRUN_TEST_HARNESS", HARNESS))
DIFF_EXE = REAL_HARNESS / "conflictdiff" / "bin" / "Release" / "net9.0" / DIFF_NAME
DIFF_SOURCE: str | None = None                # set by use_conflictdiff(): where this run's conflictdiff came from

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


def sweep_launches(run: Path) -> list[Path]:
    """The run's ordersweep launch folders (`RUN/sweep/inv-NNNN`), oldest first: one per launch, never rewritten."""
    root = Path(run) / "sweep"
    if not root.is_dir():
        return []
    return sorted((p for p in root.iterdir() if p.is_dir() and p.name.startswith("inv-")),
                  key=lambda p: int(p.name.split("-", 1)[1]))


def last_sweep(run: Path) -> Path:
    """The newest launch folder (a path that does not exist when nothing was launched)."""
    launches = sweep_launches(run)
    return launches[-1] if launches else Path(run) / "sweep" / "inv-none"


def sweep_cache_lines(run: Path) -> list[str]:
    """Every launch's cache-log lines, oldest launch first."""
    out = []
    for f in sweep_launches(run):
        p = f / "cache-log.txt"
        if p.exists():
            out += p.read_text(encoding="utf-8").splitlines()
    return out


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
    blob = json.dumps(disp) + json.dumps(load(last_sweep(run) / "summary.json") or {})
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
    # The detectors that feed evaluation.findings: the combined arm's new navmesh defect is a finding on its diff part
    # and on its order evaluation; patch A alone creates none, so both of its parts are no-finding.
    led = ledger_entries(load(run / "ledger.json") or {})
    for key, want in [("geometry.navmesh-ownership__candidate-03-combined__diff", "finding"),
                      ("geometry.navmesh-ownership__candidate-01-patcha__diff", "no-finding"),
                      ("patch.order-evaluation__candidate-03-combined", "finding"),
                      ("patch.order-evaluation__candidate-01-patcha", "no-finding")]:
        expect((led.get(key) or {}).get("detector"), want, f"{case}: ledger detector of {key}")


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
    # This case's registry trim omits patch.candidate-qualification, so it qualifies nothing: case 47 covers
    # qualification. What the case does show, exactly: no qualification check planned and no candidate rows.
    disp = load(run / "disposition.json") or {}
    plan = load(run / "plan.json") or {}
    check(len(plan.get("checks") or []) > 0, f"{case}: plan.json was read and lists its checks")
    expect([c["checkId"] for c in plan.get("checks") or [] if c["checkId"] == "patch.candidate-qualification"], [],
           f"{case}: the trim plans no candidate-qualification derivation")
    expect(field(disp, "discovery", default={}).get("candidates"), [],
           f"{case}: discovery.candidates is empty (no qualification ran)")


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
        if spec["entry"].endswith("__arm"):
            # Review V2 H2's mechanism: reverse and diff were credited whatever the arm part's acceptance. An arm emit
            # rejected for its identity measured some other configuration, so nothing built on it is credited.
            for part in ("reverse", "diff"):
                rp = receipt(r2, plan2, spec["entry"][:-len("arm")] + part) or {}
                check(rp and not (rp.get("validation") or {}).get("accepted"),
                      f"{case}/{spec['id']}: the {part} built on the rejected arm emit is not accepted "
                      f"(got {(rp.get('result') or {}).get('execution')})")
        if spec["entry"].endswith("__reverse"):
            # Review R1 M1-S5: the diff's created rows were split by that rejected reverse emit, so it is not credited.
            rp = receipt(r2, plan2, spec["entry"][:-len("reverse")] + "diff") or {}
            check(rp and not (rp.get("validation") or {}).get("accepted"),
                  f"{case}/{spec['id']}: the diff split by the rejected reverse emit is not accepted "
                  f"(got {(rp.get('result') or {}).get('execution')})")


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
    for p in sorted((last_sweep(base) / "navownprobe").glob("baseline.json")):
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
    named = os.environ.get(q["env"]) or q.get("path")
    src = Path(named) if named else None
    if src is not None and src.is_dir():
        cp = tmp / "q42-demo-bundle-copy"
        shutil.copytree(src, cp)
        (cp / "verify.json").unlink(missing_ok=True)
        res = evalrun(["verify-bundle", cp])
        expect(res.returncode, q["exit"], f"{case}: verify-bundle on Q42's demo bundle", "A-1")
        blob = json.dumps(load(cp / "verify.json") or {}) + res.stdout + res.stderr
        check(q["defectKind"] in blob,
              f"{case}: the Q42 bundle is refused as {q['defectKind']} by name (A-1)")
    else:
        print(f"  NOTE  {case}: no demo bundle ({q['env']} {'names ' + str(src) + ', which is absent' if src else 'is unset'}), "
              f"so A-1's refusal is covered here only by the synthetic bundleSchema 1 copy above.")


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
        request.write_text(json.dumps(initialize(f"evalrun:{plan['planId']}:{arm['armId']}")) + "\n"
                           + json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}) + "\n",
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
               "armId": arm["armId"], "state": "baseline", "origin": "fresh", "capturedUtc": datetime_now_z(),
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
    request.write_text(json.dumps(initialize(f"evalrun:{plan['planId']}:{arm['armId']}")) + "\n"
                       + json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}) + "\n",
                       encoding="utf-8")
    response.write_text(json.dumps({"findings": 0}), encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in providers], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"],
           "state": "baseline", "origin": "fresh", "capturedUtc": datetime_now_z(),
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
    expect(do_plan(run, case, ["--select", "loadability.masters-and-order"]).returncode, 0, f"{case}: plan exit", "D1")
    # import-results credits evidence only where the check could run (review V2 L3): its prerequisite is measured first
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: prerequisite run exit", "§4.8")
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


def case_19(tmp):
    """§4.6 / §4.7: a derived check derives only over prerequisites that are settled. An unsettled one blocks
    it by name, keeps it out of completedNullChecks and keeps the evaluation incomplete on it."""
    case = "case-19-derived-prerequisites"
    reg = "fixture-check-registry-derived.json"
    qual, order = "patch.candidate-qualification", "patch.order-evaluation"
    disc, mao = "patch.discovery-installed", "loadability.masters-and-order"
    _, _, _, scen = case_paths(case)

    def run_case(tag, extra):
        run = tmp / f"{case}-{tag}"
        expect(do_plan(run, case, ["--select", qual] + extra, registry=reg).returncode, 0,
               f"{case}/{tag}: plan exit", "§4.6")
        expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/{tag}: run exit", "§4.6")
        expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}/{tag}: finalize exit", "D7")
        plan = load(run / "plan.json") or {}
        base = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "baseline"), "baseline")
        cand = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "candidate"), None)
        return (ledger_entries(load(run / "ledger.json") or {}), load(run / "disposition.json") or {},
                base, cand)

    def nulls(disp):
        return {(r.get("checkId"), r.get("armId")) for r in (field(disp, "completedNullChecks") or [])}

    def missing(disp):
        return {(r.get("checkId"), r.get("armId")): r.get("status") for r in (field(disp, "missing") or [])}

    def blocked_on(led, disp, cid, aid, pre, tag):
        e = led.get(f"{cid}__{aid}") or {}
        expect(execution_of(e), f"blocked(prerequisite:{pre})", f"{case}/{tag}: {cid} [{aid}] status", "§4.7")
        expect(e.get("cause"), "blocked-prerequisite", f"{case}/{tag}: {cid} [{aid}] cause", "§4.3")
        detail = " ".join(str(x.get("detail") or "") for x in ((e.get("validation") or {}).get("rejections") or []))
        check(pre in detail, f"{case}/{tag}: {cid} [{aid}] rejection detail names {pre} (got {detail[:120]!r})")
        check((cid, aid) not in nulls(disp), f"{case}/{tag}: {cid} [{aid}] is NOT a completed null check (D7)")
        expect(missing(disp).get((cid, aid)), f"blocked(prerequisite:{pre})",
               f"{case}/{tag}: {cid} [{aid}] is listed missing", "D7")
        expect(field(disp, "complete"), False, f"{case}/{tag}: evaluation.complete", "D7")

    # the discovery prerequisite unsettled: candidate-qualification blocks on every arm
    led, disp, base, cand = run_case("discovery-excluded",
                                     ["--exclude", f"{disc}=fixture: the prerequisite is deliberately unsettled"])
    for aid in (base, cand):
        blocked_on(led, disp, qual, aid, disc, "discovery-excluded")
    # ... while order-evaluation, whose own prerequisite IS settled there, still derives as before
    expect(execution_of(led.get(f"{order}__{cand}")), "completed",
           f"{case}/discovery-excluded: {order} [{cand}] still derives", "§4.6")
    expect(execution_of(led.get(f"{order}__{base}")), "not-applicable",
           f"{case}/discovery-excluded: {order} [{base}] stays not-applicable", "§4.6")

    # every prerequisite settled: candidate-qualification derives and is a completed null, as before
    led, disp, base, cand = run_case("settled", [])
    for aid in (base, cand):
        expect(execution_of(led.get(f"{disc}__{aid}")), "completed",
               f"{case}/settled: {disc} [{aid}] completed (the prerequisite really is settled)", "§4.5")
        expect(execution_of(led.get(f"{qual}__{aid}")), "completed", f"{case}/settled: {qual} [{aid}] status",
               "§4.6")
        check((qual, aid) in nulls(disp), f"{case}/settled: {qual} [{aid}] is a completed null check (D7)")

    # the order check's own prerequisite unsettled: order-evaluation blocks on the candidate arm; the baseline
    # keeps its answer (the reference is not measured against itself), and qualification blocks on discovery
    led, disp, base, cand = run_case("masters-excluded",
                                     ["--exclude", f"{mao}=fixture: the prerequisite is deliberately unsettled"])
    blocked_on(led, disp, order, cand, mao, "masters-excluded")
    expect(execution_of(led.get(f"{order}__{base}")), "not-applicable",
           f"{case}/masters-excluded: {order} [{base}] stays not-applicable", "§4.6")
    blocked_on(led, disp, qual, cand, disc, "masters-excluded")


# ------------------------------------------- defect iv: the budget is a deadline on the running tree (20)

DEADLINE_SLOWPROBE = r'''
"""A fake probe that, on ONE named arm, writes its complete emit and then never exits - and spawns a
descendant that never exits either. Both write their PIDs. Every other arm is the ordinary fake."""
import os, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, FIXT_DIR)
from fakeprobe import main
probe, argv = sys.argv[1], sys.argv[2:]
emit = argv[argv.index("--emit") + 1] if "--emit" in argv else ""
stem = Path(emit).name[:-5] if emit.endswith(".json") else ""
if stem and stem == os.environ.get("EVALRUN_SLOW_ARM"):
    code = main(probe, argv)
    pidfile = os.environ["EVALRUN_SLOW_PIDFILE"]
    with open(pidfile, "a") as f:
        f.write("parent %d\n" % os.getpid())
    child = "import os, sys, time\nopen(sys.argv[1], 'a').write('child %d\\n' % os.getpid())\ntime.sleep(900)\n"
    subprocess.Popen([sys.executable, "-c", child, pidfile])
    time.sleep(900)
    raise SystemExit(code)
raise SystemExit(main(probe, argv))
'''


def _pid_alive(pid: int) -> bool:
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        h = k32.OpenProcess(0x1000, False, pid)
        if not h:
            return False
        try:
            code = wintypes.DWORD()
            return bool(k32.GetExitCodeProcess(h, ctypes.byref(code))) and code.value == 259
        finally:
            k32.CloseHandle(h)
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def _pids(pidfile: Path) -> dict:
    out = {}
    if pidfile.exists():
        for line in pidfile.read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) == 2 and parts[1].isdigit():
                out[parts[0]] = int(parts[1])
    return out


def case_21(tmp):
    """Defect iv. `--budget-minutes` is a deadline on the process tree evalrun launched, not only a gate
    before launch: a sweep still running at the deadline is stopped (its probe AND the probe's descendant),
    nothing from the stopped sweep is accepted (a complete emit with no completion, a stale summary.json and
    a stale per-arm log from an 'earlier invocation' are all present), every affected entry is accounted
    with a named cause, finalize/bundle/verify-bundle report a valid incomplete evaluation, and resume
    recovers reusing only intact evidence. Expiry before launch still starts nothing."""
    import threading
    case = "case-21-deadline-stops-tree"
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    allowance = 15.0                     # the stated cleanup allowance (SEMANTICS; evalrun --budget-minutes)
    budget_min = 0.3
    budget_s = budget_min * 60
    nav = "geometry.navmesh-ownership"
    evidence_dir = os.environ.get("HOUSECARL_DEADLINE_EVIDENCE_DIR")

    run = tmp / case
    slow = tmp / "deadline-slowprobe.py"
    slow.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_SLOWPROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    store = run.parent / f"{case}-runlog"
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", str(budget_min),
              "--probe-cmd", f"navownprobe=python {slow} navownprobe"])
    expect(p.returncode, 0, f"{case}: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    plan = load(run / "plan.json") or {}
    cand = next((a for a in plan.get("arms") or [] if a.get("kind") == "candidate"), {})
    cand_id, cand_stem = cand.get("armId"), Path(cand.get("candidateFile") or "x.json").stem
    base_id = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "baseline"), "baseline")

    # an EARLIER invocation's leftovers that must never be adopted: a summary claiming the slow arm passed,
    # and a per-arm log carrying the Runner's completion trailer - in the older shared layout (RUN/sweep) and in an
    # earlier launch folder (RUN/sweep/inv-0001) alike
    row = {"probe": "navownprobe", "candidate": cand_stem, "probeExit": 0, "reverseExit": 0, "diffExit": 0,
           "probeRefused": False, "illegal": False,
           "diff": {"verdict": "DIFFERENTIAL-VERIFIED", "counts": {"created": 0}, "refusals": [], "delta": []},
           "created": {"created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}}
    for leftovers in (run / "sweep", run / "sweep" / "inv-0001"):
        (leftovers / "logs").mkdir(parents=True, exist_ok=True)
        (leftovers / "summary.json").write_text(json.dumps({"baseline": "stale", "probes": ["navownprobe"], "candidates": {
            cand_stem: {"classification": "PASSES-COMPLETED-CHECKS", "rows": [row]}}}), encoding="utf-8")
        (leftovers / "logs" / f"navownprobe-{cand_stem}.txt").write_text(
            "$ stale\n(exit 0, 1.0s)\n\nfrom an earlier invocation\n", encoding="utf-8")
        (leftovers / "jobs.jsonl").write_text(json.dumps(
            {"job": f"navownprobe-{cand_stem}", "kind": "probe", "outcome": "ran", "exitCode": 0,
             "emitWritten": True}) + "\n", encoding="utf-8")

    # ---- the interrupted run: the deadline must stop the sweep's tree with nobody's help
    fired = threading.Event()
    t_launch = time.monotonic()
    proc = subprocess.Popen([sys.executable, str(EVALRUN), "run", "--run", str(run)],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            env=dict(env, EVALRUN_SLOW_ARM=cand_stem, EVALRUN_SLOW_PIDFILE=str(pidfile)))

    def watchdog():                      # protects the machine ONLY; if it fires the control has FAILED
        fired.set()
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
            for pid in _pids(pidfile).values():
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        else:
            proc.kill()
            for pid in _pids(pidfile).values():
                try:
                    os.kill(pid, 9)
                except OSError:
                    pass

    dog = threading.Timer(budget_s + allowance + 30, watchdog)
    dog.daemon = True
    dog.start()
    out_text = []
    reader = threading.Thread(target=lambda: out_text.append(proc.stdout.read()), daemon=True)
    reader.start()
    gone_at, exited_at = None, None
    while time.monotonic() - t_launch < budget_s + allowance + 45:
        pids = _pids(pidfile)
        if exited_at is None and proc.poll() is not None:
            exited_at = time.monotonic() - t_launch
        if gone_at is None and "parent" in pids and "child" in pids and not any(_pid_alive(x) for x in pids.values()):
            gone_at = time.monotonic() - t_launch
        if gone_at is not None and exited_at is not None:
            break
        time.sleep(0.1)
    dog.cancel()
    if proc.poll() is None:
        proc.kill()
    reader.join(timeout=10)
    pids = _pids(pidfile)
    line = (f"budget {budget_s:.1f}s + allowance {allowance:.1f}s (measured from controller launch, which "
            f"precedes the deadline's start); pids {pids}; both gone at "
            f"{None if gone_at is None else round(gone_at, 2)}s; controller exited at "
            f"{None if exited_at is None else round(exited_at, 2)}s (exit {proc.returncode}); alive now "
            f"{ {k: _pid_alive(v) for k, v in pids.items()} }; watchdog fired {fired.is_set()}")
    print("   evidence: " + line)
    if evidence_dir:
        Path(evidence_dir).mkdir(parents=True, exist_ok=True)
        (Path(evidence_dir) / "evalrun-sweep-tree-stop.txt").write_text(
            line + "\n\n---- controller output ----\n" + "".join(out_text), encoding="utf-8")
    check("parent" in pids and "child" in pids,
          f"{case}: the slow sweep arm was reached before the deadline (probe and descendant recorded PIDs)")
    check(not fired.is_set(), f"{case}: the watchdog did not have to intervene (enforcement came from evalrun)")
    check(gone_at is not None and gone_at <= budget_s + allowance,
          f"{case}: probe AND descendant are gone within budget + allowance ({budget_s + allowance:.0f}s)")
    check(exited_at is not None and exited_at <= budget_s + allowance + 10,
          f"{case}: the controller finished its own accounting promptly after the stop")
    expect(proc.returncode, 0, f"{case}: the interrupted run exits 0 (an incomplete evaluation is not a crash)")

    led = ledger_entries(load(run / "ledger.json") or {})
    parts = [(base_id, "arm"), (cand_id, "arm"), (cand_id, "reverse"), (cand_id, "diff")]
    for aid, part in parts:
        e = led.get(f"{nav}__{aid}__{part}") or {}
        expect(execution_of(e), "stopped(deadline)", f"{case}: {nav} [{aid}/{part}] after the stop", "defect iv")
        expect(e.get("cause"), "budget-exhausted", f"{case}: {nav} [{aid}/{part}] named cause", "defect iv")
        rc = load(run / "receipts" / f"{nav}__{aid}__{part}.json") or {}
        check(rc and (rc.get("validation") or {}).get("accepted") is False,
              f"{case}: {nav} [{aid}/{part}] receipt exists and is NOT accepted")
    check(not any(execution_of(v) == "running" for v in led.values()),
          f"{case}: no entry is left running after the stop")
    runlog_rows = []
    hist = store / "history.jsonl"
    if hist.exists():
        for l in hist.read_text(encoding="utf-8").splitlines():
            try:
                runlog_rows.append(json.loads(l))
            except ValueError:
                pass
    sweep_rows = [r for r in runlog_rows if r.get("op") == "ordersweep" and r.get("mode") == "child"]
    check(bool(sweep_rows) and sweep_rows[-1].get("status") == "Cancelled"
          and sweep_rows[-1].get("stoppedBy") == "deadline",
          f"{case}: RunLog records the sweep child as Cancelled, stoppedBy deadline "
          f"(got {[(r.get('status'), r.get('stoppedBy')) for r in sweep_rows]})")
    slog = (run / "logs" / "ordersweep.txt")
    check(slog.exists() and "stopped at the deadline" in slog.read_text(encoding="utf-8", errors="replace"),
          f"{case}: the controller's sweep log says it was stopped at the deadline")

    fin = ev(["finalize", "--run", run])
    expect(fin.returncode, 0, f"{case}: finalize on the interrupted run exits 0 (a valid, incomplete disposition)")
    disp = load(run / "disposition.json") or {}
    expect(field(disp, "complete"), False, f"{case}: the interrupted evaluation is incomplete", "D7")
    miss = {(m.get("checkId"), m.get("armId")): m.get("status") for m in (field(disp, "missing") or [])}
    expect(miss.get((nav, cand_id)), "stopped(deadline)", f"{case}: missing names the stopped candidate arm", "D7")
    bun = ev(["bundle", "--run", run])
    expect(bun.returncode, 0, f"{case}: bundle on the interrupted run exits 0", "D8")
    vb = ev(["verify-bundle", run / "bundle"])
    expect(vb.returncode, 0, f"{case}: verify-bundle on the interrupted run exits 0", "D8")

    # ---- resume: the slow arm now behaves; completed work is reused, the rest re-run under a fresh deadline
    launches_before = sweep_launches(run)
    before_cache = "\n".join(sweep_cache_lines(run))
    journal_before = (run / "journal.jsonl").read_text(encoding="utf-8")
    res = ev(["resume", "--run", run])
    expect(res.returncode, 0, f"{case}: resume exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    for aid, part in parts:
        check(execution_of(led.get(f"{nav}__{aid}__{part}")) in ("completed", "reused"),
              f"{case}: after resume {nav} [{aid}/{part}] is terminal-ok "
              f"(got {execution_of(led.get(f'{nav}__{aid}__{part}'))})")
    new_launches = [f for f in sweep_launches(run) if f not in launches_before]
    check(len(new_launches) == 1, f"{case}: the resume launched the sweep once, into a folder of its own "
                                  f"(new launch folders: {[f.name for f in new_launches]})")
    new_cache = "\n".join(ln for f in new_launches if (f / "cache-log.txt").exists()
                          for ln in (f / "cache-log.txt").read_text(encoding="utf-8").splitlines())
    check("RECORDED navownprobe-baseline" in before_cache and f"REUSED navownprobe-baseline" in new_cache
          and "RECORDED navownprobe-baseline" not in new_cache,
          f"{case}: the baseline probe that finished before the stop is reused from the verified cache, "
          f"not re-measured")
    check(f"RECORDED navownprobe-{cand_stem}:" in new_cache,
          f"{case}: the stopped candidate arm is genuinely re-measured on resume")
    journal_after = (run / "journal.jsonl").read_text(encoding="utf-8")
    check(journal_after.startswith(journal_before) and "stopped(deadline)" in journal_after,
          f"{case}: resume appends; the stop's journal history is not erased")
    check(len(field(load(run / "ledger.json") or {}, "runIds", default=[])) >= 2,
          f"{case}: the ledger lists both invocations")
    summ = load(last_sweep(run) / "summary.json") or {}
    check(summ.get("baseline") not in (None, "stale"), f"{case}: the resume's credit comes from this sweep's own summary")
    check((load(run / "sweep" / "inv-0001" / "summary.json") or {}).get("baseline") == "stale"
          and (load(run / "sweep" / "summary.json") or {}).get("baseline") == "stale",
          f"{case}: the earlier leftovers were neither adopted nor rewritten")

    # ---- a clean re-invocation adopts nothing new and runs no child
    again = ev(["run", "--run", run])
    expect(again.returncode, 0, f"{case}: a clean re-run exits 0")
    check("every arm reused, no child run" in again.stdout, f"{case}: the clean re-run runs no sweep child")
    led = ledger_entries(load(run / "ledger.json") or {})
    for aid, part in parts:
        expect(execution_of(led.get(f"{nav}__{aid}__{part}")), "reused",
               f"{case}: clean re-run {nav} [{aid}/{part}]", "D6")

    # ---- expiry before launch: nothing starts
    run3 = tmp / f"{case}-expired"
    p3 = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
             "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
             "--out", run3, "--harness", REAL_HARNESS] + probe_cmds()
            + ["--select", "navownprobe", "--budget-minutes", "0.000001"])
    expect(p3.returncode, 0, f"{case}/expired: plan exit")
    r3 = ev(["run", "--run", run3])
    expect(r3.returncode, 0, f"{case}/expired: run exit")
    logs3 = [x.name for x in (run3 / "logs").glob("*.txt")] if (run3 / "logs").exists() else []
    expect(logs3, [], f"{case}/expired: no child log exists - no child was started", "defect iv")
    j3 = (run3 / "journal.jsonl").read_text(encoding="utf-8") if (run3 / "journal.jsonl").exists() else ""
    check('"to":"running"' not in j3.replace(" ", ""), f"{case}/expired: no entry ever went running")
    led3 = ledger_entries(load(run3 / "ledger.json") or {})
    expect(execution_of(led3.get(f"{nav}__{cand_id}")), "skipped-budget",
           f"{case}/expired: the sweep check is skipped-budget", "defect iv")


DEADLINE_EARLYEXIT_PROBE = r'''
"""A fake probe that, on ONE named arm, writes its complete emit, starts a descendant that sleeps (with
its own stdio, so no pipe keeps the probe's caller waiting) and exits at once. Every other arm is the
ordinary fake."""
import os, subprocess, sys
from pathlib import Path
sys.path.insert(0, FIXT_DIR)
from fakeprobe import main
probe, argv = sys.argv[1], sys.argv[2:]
emit = argv[argv.index("--emit") + 1] if "--emit" in argv else ""
stem = Path(emit).name[:-5] if emit.endswith(".json") else ""
if stem and stem == os.environ.get("EVALRUN_SLOW_ARM"):
    code = main(probe, argv)
    p = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(900)"], stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
    with open(os.environ["EVALRUN_SLOW_PIDFILE"], "a") as f:
        f.write("parent %d\nchild %d\n" % (os.getpid(), p.pid))
    raise SystemExit(code)
raise SystemExit(main(probe, argv))
'''


def case_22(tmp):
    """The deadline covers descendants that outlive the process they came from. A probe writes a valid emit
    and exits 0 at once but leaves a sleeping descendant; the sweep itself then finishes. The job is the unit
    of work, so the check must NOT be accepted: at the deadline the descendant is stopped, every planned part
    is `stopped(deadline)` / budget-exhausted, no entry is left running, and finalize reports it missing."""
    import threading
    case = "case-22-deadline-descendant-outlives-probe"
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    allowance = 15.0
    budget_min = 0.3
    budget_s = budget_min * 60
    nav = "geometry.navmesh-ownership"
    evidence_dir = os.environ.get("HOUSECARL_DEADLINE_EVIDENCE_DIR")

    run = tmp / case
    probe = tmp / "deadline-earlyexit-probe.py"
    probe.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_EARLYEXIT_PROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    store = run.parent / f"{case}-runlog"
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", str(budget_min),
              "--probe-cmd", f"navownprobe=python {probe} navownprobe"])
    expect(p.returncode, 0, f"{case}: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    plan = load(run / "plan.json") or {}
    cand = next((a for a in plan.get("arms") or [] if a.get("kind") == "candidate"), {})
    cand_id, cand_stem = cand.get("armId"), Path(cand.get("candidateFile") or "x.json").stem
    base_id = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "baseline"), "baseline")

    t_launch = time.monotonic()
    proc = subprocess.Popen([sys.executable, str(EVALRUN), "run", "--run", str(run)],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            env=dict(env, EVALRUN_SLOW_ARM=cand_stem, EVALRUN_SLOW_PIDFILE=str(pidfile)))
    out_text = []
    reader = threading.Thread(target=lambda: out_text.append(proc.stdout.read()), daemon=True)
    reader.start()
    gone_at, exited_at = None, None
    while time.monotonic() - t_launch < budget_s + allowance + 5:
        pids = _pids(pidfile)
        if exited_at is None and proc.poll() is not None:
            exited_at = time.monotonic() - t_launch
        if gone_at is None and "parent" in pids and "child" in pids and not any(_pid_alive(x) for x in pids.values()):
            gone_at = time.monotonic() - t_launch
        if gone_at is not None and exited_at is not None:
            break
        time.sleep(0.1)
    pids = _pids(pidfile)
    killed = []                          # protects the machine ONLY; anything killed here is a FAILED control
    if proc.poll() is None:
        killed.append(("controller", proc.pid))
        proc.kill()
    for name, pid in pids.items():
        if _pid_alive(pid):
            killed.append((name, pid))
            if os.name == "nt":
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
            else:
                try:
                    os.kill(pid, 9)
                except OSError:
                    pass
    reader.join(timeout=10)
    line = (f"budget {budget_s:.1f}s + allowance {allowance:.1f}s (measured from controller launch, which "
            f"precedes the deadline's start); pids {pids}; both gone at "
            f"{None if gone_at is None else round(gone_at, 2)}s; controller exited at "
            f"{None if exited_at is None else round(exited_at, 2)}s (exit {proc.returncode}); killed by the test "
            f"{killed}")
    print("   evidence: " + line)
    if evidence_dir:
        Path(evidence_dir).mkdir(parents=True, exist_ok=True)
        (Path(evidence_dir) / "evalrun-descendant-outlives-probe.txt").write_text(
            line + "\n\n---- controller output ----\n" + "".join(out_text), encoding="utf-8")
    check("parent" in pids and "child" in pids,
          f"{case}: the early-exit arm was reached before the deadline (probe and descendant recorded PIDs)")
    check(not killed, f"{case}: the test killed nothing (enforcement came from evalrun; killed {killed})")
    check(gone_at is not None and gone_at <= budget_s + allowance,
          f"{case}: probe AND descendant are gone within budget + allowance ({budget_s + allowance:.0f}s)")
    check(exited_at is not None and exited_at <= budget_s + allowance + 5,
          f"{case}: the controller finished its accounting promptly after the stop")
    expect(proc.returncode, 0, f"{case}: the run exits 0 (an incomplete evaluation is not a crash)")

    led = ledger_entries(load(run / "ledger.json") or {})
    for aid, part in [(base_id, "arm"), (cand_id, "arm"), (cand_id, "reverse"), (cand_id, "diff")]:
        e = led.get(f"{nav}__{aid}__{part}") or {}
        expect(execution_of(e), "stopped(deadline)", f"{case}: {nav} [{aid}/{part}] is not accepted as completed",
               "deadline covers the job")
        expect(e.get("cause"), "budget-exhausted", f"{case}: {nav} [{aid}/{part}] named cause", "deadline covers the job")
        rc = load(run / "receipts" / f"{nav}__{aid}__{part}.json") or {}
        check(rc and (rc.get("validation") or {}).get("accepted") is False,
              f"{case}: {nav} [{aid}/{part}] receipt exists and is NOT accepted")
    check(not any(execution_of(v) == "running" for v in led.values()),
          f"{case}: no entry is left running after the stop")
    rows = []
    hist = store / "history.jsonl"
    if hist.exists():
        for l in hist.read_text(encoding="utf-8").splitlines():
            try:
                rows.append(json.loads(l))
            except ValueError:
                pass
    sweep_rows = [r for r in rows if r.get("op") == "ordersweep" and r.get("mode") == "child"]
    check(bool(sweep_rows) and sweep_rows[-1].get("status") == "Cancelled"
          and sweep_rows[-1].get("stoppedBy") == "deadline",
          f"{case}: RunLog records the sweep child as Cancelled, stoppedBy deadline "
          f"(got {[(r.get('status'), r.get('stoppedBy')) for r in sweep_rows]})")
    fin = ev(["finalize", "--run", run])
    expect(fin.returncode, 0, f"{case}: finalize exits 0 (a valid, incomplete disposition)")
    disp = load(run / "disposition.json") or {}
    expect(field(disp, "complete"), False, f"{case}: the evaluation is incomplete", "D7")
    miss = {(m.get("checkId"), m.get("armId")): m.get("status") for m in (field(disp, "missing") or [])}
    expect(miss.get((nav, cand_id)), "stopped(deadline)", f"{case}: missing names the stopped candidate arm", "D7")


# Test-only, and only on the PYTHONPATH of the one evalrun subprocess below: when that process imports
# runlog, its kernel32 binding is wrapped so AssignProcessToJobObject refuses. Nothing in evalrun or runlog
# reads an environment switch for this; containment genuinely fails inside a real Runner.
REFUSE_CONTAINMENT_SITECUSTOMIZE = r'''
import importlib.abc, importlib.machinery, os, sys
class _RefuseContainment(importlib.abc.MetaPathFinder):
    def find_spec(self, name, path, target=None):
        if name != "runlog":
            return None
        spec = importlib.machinery.PathFinder.find_spec(name, path)
        if spec is None:
            return None
        original = spec.loader.exec_module
        def exec_module(module):
            original(module)
            real = module._kernel32
            class Refusing:
                def __init__(self, k):
                    self._k = k
                def __getattr__(self, n):
                    return getattr(self._k, n)
                def AssignProcessToJobObject(self, job, h):
                    if match[0]:
                        return False
                    return self._k.AssignProcessToJobObject(job, h)
            module._kernel32 = lambda: Refusing(real())
            match = [False]
            launch = module._ProcessTree.launch.__func__
            def refusing_launch(cls, argv, **kw):
                want = os.environ.get("EVALRUN_TEST_REFUSE_ARGV", "")
                match[0] = (not want) or any(want in str(a) for a in argv)
                return launch(cls, argv, **kw)
            module._ProcessTree.launch = classmethod(refusing_launch)
        spec.loader.exec_module = exec_module
        return spec
sys.meta_path.insert(0, _RefuseContainment())
'''


def case_23(tmp):
    """A child whose containment cannot be established under a deadline never runs, and the controller names
    that cause, on both launch paths: a single check child (the loadability prerequisite, run_child) and the
    ordersweep child (every planned part). Each is `blocked(uncontained)` with a sentence quoting the Runner's
    refusal - never `stopped(deadline)` / budget-exhausted, never accepted - finalize lists it missing, and
    resume (containment working again) re-runs it."""
    case = "case-23-deadline-containment-refused"
    if os.name != "nt":
        print(f"   {case}: Windows job objects only; not applicable on this platform")
        return
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    nav = "geometry.navmesh-ownership"
    run = tmp / case
    hook = tmp / f"{case}-site"
    hook.mkdir(parents=True, exist_ok=True)
    (hook / "sitecustomize.py").write_text(REFUSE_CONTAINMENT_SITECUSTOMIZE, encoding="utf-8")
    store = run.parent / f"{case}-runlog"
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", "0.3"])
    expect(p.returncode, 0, f"{case}: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    plan = load(run / "plan.json") or {}
    cand_id = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "candidate"), None)
    base_id = next((a["armId"] for a in plan.get("arms") or [] if a.get("kind") == "baseline"), "baseline")
    pre = "loadability.masters-and-order"
    run_a = tmp / f"{case}-single"
    shutil.copytree(run, run_a)
    ra = ev(["run", "--run", run_a], extra_env={"PYTHONPATH": str(hook), "EVALRUN_TEST_REFUSE_ARGV": ""})
    expect(ra.returncode, 0, f"{case}/single: run exits 0")
    led_a = ledger_entries(load(run_a / "ledger.json") or {})
    pre_rows = {k: v for k, v in led_a.items() if k.startswith(pre + "__")}
    check(bool(pre_rows) and all(execution_of(v) == "blocked(uncontained)" for v in pre_rows.values()),
          f"{case}/single: the run_child prerequisite is blocked(uncontained) "
          f"(got {[execution_of(v) for v in pre_rows.values()]})")
    check(bool(pre_rows) and all("never ran" in str(v.get("detail") or "")
                                 and "AssignProcessToJobObject refused" in str(v.get("detail") or "")
                                 and v.get("cause") != "budget-exhausted" for v in pre_rows.values()),
          f"{case}/single: its detail quotes the refusal and says it never ran; the budget is not blamed")
    fin_a = ev(["finalize", "--run", run_a])
    miss_a = {m.get("checkId"): m.get("status") for m in (field(load(run_a / "disposition.json") or {}, "missing") or [])}
    expect(miss_a.get(pre), "blocked(uncontained)", f"{case}/single: missing names the refused prerequisite", "D7")
    res_a = ev(["resume", "--run", run_a])
    led_a = ledger_entries(load(run_a / "ledger.json") or {})
    check(all(execution_of(v) in ("completed", "reused") for k, v in led_a.items() if k.startswith(pre + "__")),
          f"{case}/single: resume re-runs the refused prerequisite to terminal-ok")
    # phase B: refuse only the ordersweep launch -> every planned sweep part is blocked(uncontained)
    r = ev(["run", "--run", run], extra_env={"PYTHONPATH": str(hook), "EVALRUN_TEST_REFUSE_ARGV": "ordersweep"})
    expect(r.returncode, 0, f"{case}: run exits 0 (an incomplete evaluation is not a crash)")
    # Review R1 L1: a launch the Runner refused before the child ran creates no launch folder, and this invocation's
    # ledger row names none (`an invocation that launches nothing creates none`).
    check(not sweep_launches(run), f"{case}: the refused ordersweep launch left no RUN/sweep/inv-* folder "
                                   f"({[f.name for f in sweep_launches(run)]})")
    run_rows = (load(run / "ledger.json") or {}).get("runIds") or []
    named = (run_rows[-1].get("sweepFolders") or []) if run_rows else None
    check(named is not None and not any("inv-" in str(f) for f in named),
          f"{case}: the ledger's last runIds row names no launch folder for the refused launch (sweepFolders {named})")
    if (store / "history.jsonl").exists():
        rows = [json.loads(l) for l in (store / "history.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
    else:
        rows = []
    sweep_rows = [x for x in rows if x.get("op") == "ordersweep" and x.get("mode") == "child"]
    check(bool(sweep_rows) and sweep_rows[-1].get("stoppedBy") == "containment",
          f"{case}: the refusal really came from the Runner (RunLog stoppedBy "
          f"{[x.get('stoppedBy') for x in sweep_rows]})")
    check(not any((f / "summary.json").exists() for f in sweep_launches(run)),
          f"{case}: the sweep child never ran (no summary written)")
    led = ledger_entries(load(run / "ledger.json") or {})
    parts = [(base_id, "arm"), (cand_id, "arm"), (cand_id, "reverse"), (cand_id, "diff")]
    for aid, part in parts:
        e = led.get(f"{nav}__{aid}__{part}") or {}
        expect(execution_of(e), "blocked(uncontained)", f"{case}: {nav} [{aid}/{part}] status", "containment")
        check(e.get("cause") != "budget-exhausted",
              f"{case}: {nav} [{aid}/{part}] is not blamed on the budget (cause {e.get('cause')!r})")
        detail = str(e.get("detail") or "")
        check("never ran" in detail and "AssignProcessToJobObject refused" in detail,
              f"{case}: {nav} [{aid}/{part}] detail quotes the refusal and says the child never ran "
              f"(got {detail[:160]!r})")
        rc = load(run / "receipts" / f"{nav}__{aid}__{part}.json") or {}
        check(rc and (rc.get("validation") or {}).get("accepted") is False,
              f"{case}: {nav} [{aid}/{part}] receipt exists and is NOT accepted")
    check(not any(execution_of(v) == "running" for v in led.values()), f"{case}: no entry is left running")
    fin = ev(["finalize", "--run", run])
    expect(fin.returncode, 0, f"{case}: finalize exits 0")
    disp = load(run / "disposition.json") or {}
    expect(field(disp, "complete"), False, f"{case}: the evaluation is incomplete", "D7")
    miss = {(m.get("checkId"), m.get("armId")): m.get("status") for m in (field(disp, "missing") or [])}
    expect(miss.get((nav, cand_id)), "blocked(uncontained)", f"{case}: missing names the refused arm", "D7")
    nulls = {(n.get("checkId"), n.get("armId")) for n in (field(disp, "completedNullChecks") or [])}
    check((nav, cand_id) not in nulls, f"{case}: the refused arm is NOT a completed null")
    res = ev(["resume", "--run", run])
    expect(res.returncode, 0, f"{case}: resume exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    for aid, part in parts:
        check(execution_of(led.get(f"{nav}__{aid}__{part}")) in ("completed", "reused"),
              f"{case}: after resume {nav} [{aid}/{part}] is re-run to terminal-ok "
              f"(got {execution_of(led.get(f'{nav}__{aid}__{part}'))})")


CASES = {"case-01": case_01, "case-02": case_02, "case-03": case_03, "case-04": case_04, "case-05": case_05,
         "case-06": case_06, "case-07": case_07, "case-08": case_08, "case-09": case_09, "case-10": case_10,
         "case-11": case_11, "case-12": case_12, "case-13": case_13, "case-14": case_14, "case-15": case_15,
         "case-16": case_16, "case-17": case_17, "case-18": case_18, "case-19": case_19}


# ------------------------------- loadability on an add/remove arm: a legal target omission (20)

# The stand-in follows scopeprobe/Program.cs at HEAD, not evalrun.py: at least one --target or exit 2; an
# order the engine would refuse is REFUSED on stderr, exit 2, no JSON; otherwise the JSON is written, each
# named target the resolved configuration lacks is a walk failure in the probe's own wording, and the exit is
# 0 when complete, 1 when not. `loadability` in the scenario, keyed by order stem, makes an order refuse
# (`refuse`), makes resolution lose a plugin the order names (`lose`), or leaves a resolved target unmeasured
# without naming it (`silent` - a damaged output, so the controller's measured-target rule has an arm).
SCOPE_FAKE_20 = r'''
import json, os, sys
from pathlib import Path
a = sys.argv[1:]
targets, order, out = [], None, None
i = 0
while i < len(a):
    if a[i] == "--target": targets.append(a[i + 1]); i += 2; continue
    if a[i] == "--order": order = a[i + 1]; i += 2; continue
    if a[i] == "--json": out = a[i + 1]; i += 2; continue
    if a[i] in ("--instance", "--profile", "--game", "--assets"): i += 2; continue
    i += 1
if not targets:
    print("scopeprobe needs at least one --target Plugin.esp", file=sys.stderr); sys.exit(2)
scen = json.loads(Path(os.environ["EVALRUN_FAKE_SCENARIO"]).read_text(encoding="utf-8"))
pdir = Path(scen["pluginDir"])
stem = Path(order).stem
rule = (scen.get("loadability") or {}).get(stem) or {}
names = list(json.loads(Path(order).read_text(encoding="utf-8")).get("Plugins") or [])
if rule.get("refuse"):
    print("REFUSED: " + rule["refuse"], file=sys.stderr)
    print("no scope.json was written: a footprint measured in an order the engine would not load describes "
          "a game that cannot start.", file=sys.stderr)
    sys.exit(2)
for n in names:
    if not (pdir / n).exists():
        print(f"REFUSED: the order names '{n}' and no provider in the view supplies it", file=sys.stderr)
        sys.exit(2)
resolved = [n for n in names if n.lower() != str(rule.get("lose") or "").lower()]
src = "candidate:" + Path(order).name
print(f"configuration: {src} - {len(resolved)} plugins")
failures, measured = [], []
for t in targets:
    if t.lower() not in {n.lower() for n in resolved}:
        failures.append(f"'{t}' is not in the resolved configuration ({len(resolved)} plugins, {src}); "
                        "its footprint was not measured, so nothing below describes it")
    elif t.lower() != str(rule.get("silent") or "").lower():
        measured.append({"name": t, "footprint": {"recordTypes": {}}})
for f in failures:
    print("  WALK FAILURE: " + f)
complete = not failures and len(measured) == len(targets)
doc = {"schemaVersion": 1, "probe": "scopeprobe", "complete": complete, "walkFailures": failures,
       "targets": measured, "unmeasured": [],
       "configuration": {"source": src, "population": "load-time", "state": "baseline",
                         "plugins": [{"Name": n, "Path": str(pdir / n), "Provider": "fixture-mods"}
                                     for n in resolved]}}
Path(out).parent.mkdir(parents=True, exist_ok=True)
Path(out).write_text(json.dumps(doc, indent=1), encoding="utf-8")
sys.exit(0 if complete else 1)
'''


def scope_exe_20(tmp: Path) -> tuple[Path, str]:
    """The scopeprobe case 20 runs: HOUSECARL_EVALRUN_TEST_HARNESS's build as given, or a build of this checkout's
    scopeprobe into an empty folder under `tmp` (a failed build cannot leave an older binary to be tested)."""
    name = "scopeprobe.exe" if os.name == "nt" else "scopeprobe"
    explicit = os.environ.get("HOUSECARL_EVALRUN_TEST_HARNESS")
    if explicit:
        return Path(explicit) / "scopeprobe" / "bin" / "Release" / "net9.0" / name, f"explicit harness {explicit}"
    csproj = HARNESS / "scopeprobe" / "scopeprobe.csproj"
    build = Path(tmp) / "scopeprobe-build"
    out = build / "out"
    cmd = ["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
           "--artifacts-path", str(build / "artifacts"), "-o", str(out)]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
        why = None if r.returncode == 0 else f"dotnet exit {r.returncode}"
    except FileNotFoundError:
        why = "dotnet not found"
    if why is None and not (out / name).exists():
        why = f"dotnet exit 0 but no {name} in {out}"
    return out / name, (f"built from {csproj}" if why is None else f"NOT BUILT ({why})")


def scope_contract_20(tmp: Path, case: str) -> None:
    """The part of scopeprobe's contract the controller reads, held to the real probe on scopeprobe's own fixture
    (--self-test with PD_FIXTURE_KEEP writes MovedRefFixture): a complete scope exits 0; a named target the
    resolved configuration lacks is a walk failure starting with evalrun's Validator.SCOPE_ABSENT_TARGET for that
    name, and exits 1; a refused configuration exits 2 with REFUSED on stderr and no scope.json."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalrun_case20", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    absent_prefix = mod.Validator.SCOPE_ABSENT_TARGET
    exe, source = scope_exe_20(tmp)
    print(f"  scopeprobe for the contract: {exe} ({source})")
    if not exe.exists():
        check(False, f"{case}: scopeprobe contract: the real scopeprobe is available ({source})")
        return
    root = Path(tmp) / "scope-contract-20"
    keep = root / "fixture"
    env = dict(os.environ, PD_FIXTURE_KEEP=str(keep), PYTHONIOENCODING="utf-8")
    st = subprocess.run([str(exe), "--self-test"], capture_output=True, text=True, encoding="utf-8",
                        errors="replace", env=env, timeout=300)
    mr = keep / "mr"
    names = ["MrMaster.esm", "MrA.esp", "MrB.esp", "MrX.esp", "MrL.esp"]
    if not all((mr / n).exists() for n in names):
        check(False, f"{case}: scopeprobe contract: the fixture is written (--self-test exit {st.returncode})")
        return
    inst, game = root / "instance", root / "game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    for n in names:
        (inst / "mods" / Path(n).stem).mkdir(parents=True)
        shutil.copyfile(mr / n, inst / "mods" / Path(n).stem / n)
    (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in names), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(n + "\n" for n in names), encoding="utf-8")
    (prof / "modlist.txt").write_text("".join("+" + Path(n).stem + "\n" for n in reversed(names)), encoding="utf-8")
    base = [str(exe), "--instance", str(inst), "--game", str(game), "--profile", "P"]

    def scope(args, out):
        r = subprocess.run(base + args + ["--json", str(out)], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=300)
        return r, (load(out) if out.exists() else None)

    r, doc = scope(["--target", "MrA.esp"], root / "complete.json")
    expect(r.returncode, 0, f"{case}: scopeprobe contract: every named target measured exits 0 (complete)", "D4")
    check(isinstance(doc, dict) and doc.get("complete") is True and not doc.get("walkFailures"),
          f"{case}: scopeprobe contract: the complete scope says complete with no walk failure")

    r, doc = scope(["--target", "MrA.esp", "--target", "FixtureAbsent.esp"], root / "absent.json")
    expect(r.returncode, 1, f"{case}: scopeprobe contract: an absent named target exits 1 (incomplete)", "D4")
    fails = [str(f) for f in ((doc or {}).get("walkFailures") or [])]
    want = absent_prefix.format(name="FixtureAbsent.esp")
    check(len(fails) == 1 and fails[0].startswith(want),
          f"{case}: scopeprobe contract: the absent target's walk failure starts with the controller's "
          f"SCOPE_ABSENT_TARGET ({want!r}; got {fails[:1]!r})")

    out = root / "refused.json"
    r, doc = scope(["--order", str(root / "no-such-order.json"), "--enable", "MrA.esp", "--target", "MrA.esp"], out)
    expect(r.returncode, 2, f"{case}: scopeprobe contract: a refused configuration exits 2", "D4")
    check(r.stderr.startswith("REFUSED: ") and not out.exists(),
          f"{case}: scopeprobe contract: a refused configuration says REFUSED on stderr and writes no scope.json")


def case_20(tmp):
    """Defect iii: loadability.masters-and-order on an add/remove arm. A target the arm's own frozen request
    does not load is a legal omission - the order's loadability is measured and accepted, the omitted
    target's footprint is stated as not measured on that arm. A refused order (missing master, invalid
    order) and a target the arm DOES request but the configuration lacks still fail, on the receipt, on
    the derived prerequisite gate and in the disposition."""
    case = "case-20-loadability-omission"
    mao, order = "loadability.masters-and-order", "patch.order-evaluation"
    a_name, b_name = "FixtureTarget.esp", "FixtureTargetB.esp"
    src = GEN / "cases" / "case-19-derived-prerequisites"
    d = GEN / "cases" / case
    pdir = d / "plugins"
    pdir.mkdir(parents=True, exist_ok=True)
    for p in (GEN / "plugins").iterdir():
        shutil.copyfile(p, pdir / p.name)
    (pdir / b_name).write_bytes(b"fixture second target: another mod the plan names\n")
    scope = load(src / "scope.json")
    first = scope["targets"][0]
    scope["targets"].append(dict(first, name=b_name))
    rows = scope["configuration"]["plugins"]
    for r in rows:
        r["Path"] = str(pdir / r["Name"]).replace("\\", "/")
    rows.append({"Name": b_name, "Path": str(pdir / b_name).replace("\\", "/"),
                 "Sha256": sha_of(pdir / b_name), "Bytes": (pdir / b_name).stat().st_size,
                 "Provider": "fixture-mods"})
    (d / "scope.json").write_text(json.dumps(scope, indent=1), encoding="utf-8")

    deps = ["Skyrim.esm", "Update.esm", "FixtureBase.esp"]
    cands = {
        "candidate-00-baseline": (deps + [a_name, b_name], "the combined order: both targets"),
        "candidate-01-dependency-only": (list(deps), "removed both targets"),
        "candidate-02-plus-a": (deps + [a_name], "removed the second target"),
        "candidate-03-plus-b": (deps + [b_name], "removed the first target"),
        "candidate-04-missing-master": (["Skyrim.esm", "Update.esm", a_name, b_name], "removed FixtureBase.esp"),
        "candidate-05-invalid-order": (deps + ["FixtureAbsent.esp", a_name, b_name], "added an absent plugin"),
        "candidate-06-target-lost": (deps + [b_name, a_name], "moved the second target above the first"),
        "candidate-07-view-differs": (deps + [a_name], "removed the second target; resolution loses a master"),
        "candidate-08-target-unmeasured": (deps + [a_name], "removed the second target; first left unmeasured"),
        "candidate-09-silent-incomplete": (deps + [b_name, a_name], "every target; one left unmeasured"),
    }
    cdir = d / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)
    for stem, (plugins, change) in cands.items():
        (cdir / f"{stem}.json").write_text(json.dumps(
            {"SchemaVersion": 1, "Name": stem, "BaseProfile": "fixture", "Plugins": plugins,
             "Changes": [change], "Provenance": {"fixture": "evalrun test fixture"}}, indent=1), encoding="utf-8")
    (cdir / "candidates.json").write_text(json.dumps(
        {"SchemaVersion": 1, "Candidates": [{"File": f"{s}.json", "ViolatesAdvice": [], "SatisfiesAdvice": []}
                                            for s in cands],
         "Budget": {"evaluated": len(cands) - 1, "legalNotEvaluated": 0, "note": "fixture manifest"},
         "Contradictions": []}, indent=1), encoding="utf-8")
    missing_master = "MISSING MASTER - FixtureTarget.esp masters FixtureBase.esp, which the order does not load"
    scen = d / "scenario.json"
    scen.write_text(json.dumps({
        "pluginDir": str(pdir).replace("\\", "/"), "scopeFrom": str(d / "scope.json").replace("\\", "/"),
        "arms": {}, "loadability": {"candidate-04-missing-master": {"refuse": missing_master},
                                    "candidate-06-target-lost": {"lose": b_name},
                                    "candidate-07-view-differs": {"lose": "FixtureBase.esp"},
                                    "candidate-08-target-unmeasured": {"silent": a_name},
                                    "candidate-09-silent-incomplete": {"silent": b_name}}}, indent=1),
                    encoding="utf-8")
    fake = d / "scopeprobe_fake.py"
    fake.write_text(SCOPE_FAKE_20, encoding="utf-8")

    # The wording and the exit contract the controller relies on are scopeprobe's own, checked against the REAL
    # scopeprobe's behaviour (scope_contract_20), wherever in its sources they live: RED if either drifts.
    scope_contract_20(tmp, case)

    run = tmp / case
    reg = "fixture-check-registry-derived.json"
    expect(do_plan(run, case, ["--select", order, "--probe-cmd", f"scopeprobe=python {fake}"],
                   registry=reg).returncode, 0, f"{case}: plan exit", "§4.6")
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: run exit", "§4.6")
    expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}: finalize exit", "D7")
    plan = load(run / "plan.json") or {}
    led = ledger_entries(load(run / "ledger.json") or {})
    disp = load(run / "disposition.json") or {}
    arm_of = {Path(a.get("candidateFile") or "").stem: a["armId"] for a in plan.get("arms") or []}
    nulls = {(r.get("checkId"), r.get("armId")) for r in (field(disp, "completedNullChecks") or [])}
    missing = {(r.get("checkId"), r.get("armId")): r.get("status") for r in (field(disp, "missing") or [])}
    unassessed = field(disp, "unassessed") or {}
    unjudged = {(u.get("checkId"), u.get("armId")): u for u in (unassessed.get("unjudgeablePopulations") or [])}

    def rc(stem):
        return load(run / "receipts" / f"{mao}__{arm_of.get(stem)}.json") or {}

    def footprint_line(r):
        return next((c for c in ((r.get("result") or {}).get("coverage") or [])
                     if "footprint" in str(c.get("Population", "")).lower()), None)

    # 3 - the normal unchanged view: every target present, measured, completed, a null check as before
    s = "candidate-00-baseline"
    r, aid = rc(s), arm_of.get(s)
    expect(r.get("exitCode"), 0, f"{case}/{s}: scopeprobe exit (complete)", "D4")
    expect(execution_of(led.get(f"{mao}__{aid}")), "completed", f"{case}/{s}: {mao} status", "D4")
    expect((r.get("result") or {}).get("detector"), "no-finding", f"{case}/{s}: {mao} detector", "D4")
    expect((r.get("result") or {}).get("coverage"), [], f"{case}/{s}: coverage unchanged (no omission to state)", "D4")
    check((mao, aid) in nulls and (mao, aid) not in missing, f"{case}/{s}: {mao} is a completed null, not missing")
    expect(execution_of(led.get(f"{order}__{aid}")), "not-applicable", f"{case}/{s}: {order} as before", "§4.6")

    # 1 / 2 - a legal omission: accepted as the order's loadability; the omitted footprint is stated unmeasured
    for s, omitted, measured in (("candidate-01-dependency-only", [a_name, b_name], 0),
                                 ("candidate-02-plus-a", [b_name], 1),
                                 ("candidate-03-plus-b", [a_name], 1)):
        r, aid = rc(s), arm_of.get(s)
        expect(r.get("exitCode"), 1, f"{case}/{s}: scopeprobe exit (walk failures for the omitted targets)", "D4")
        expect(execution_of(led.get(f"{mao}__{aid}")), "completed",
               f"{case}/{s}: {mao} accepted, not rejected(exit-nonzero)", "defect iii")
        expect((r.get("validation") or {}).get("rejections"), [], f"{case}/{s}: no rejection", "defect iii")
        expect((r.get("result") or {}).get("detector"), "no-finding", f"{case}/{s}: {mao} detector", "defect iii")
        fp = footprint_line(r) or {}
        expect(fp.get("Judged"), measured, f"{case}/{s}: footprint line counts only the measured targets",
               "defect iii")
        expect(sum((fp.get("Unjudgeable") or {}).values()), len(omitted),
               f"{case}/{s}: footprint line counts the omitted targets as not measured", "defect iii")
        check(all(n in str(fp.get("Note", "")) for n in omitted),
              f"{case}/{s}: footprint line names every omitted target (got {str(fp.get('Note'))[:160]!r})")
        check((mao, aid) not in missing, f"{case}/{s}: {mao} is not listed missing/rejected in the disposition")
        check((mao, aid) in nulls, f"{case}/{s}: {mao} is a completed null (the order loads)")
        check((mao, aid) in unjudged, f"{case}/{s}: the unmeasured footprint reaches unassessed")
        status_md = (run / "STATUS.md").read_text(encoding="utf-8") if (run / "STATUS.md").exists() else ""
        check(any(f"[{aid}] target footprints: {measured} judged" in ln and "not-loaded-by-this-arm" in ln
                  for ln in status_md.splitlines()),
              f"{case}/{s}: STATUS.md states the unmeasured footprint, not only a null check")
        expect(execution_of(led.get(f"{order}__{aid}")), "completed",
               f"{case}/{s}: {order} is not blocked on the prerequisite", "§4.7")

    # 4 / 5 / 6 - real failures stay failures, each with its own cause; 07-09 pin the omission rule's other
    # conditions (the configuration is the request; every loaded target measured; a silent incompleteness)
    for s, status, needle in (("candidate-04-missing-master", "rejected(illegal-configuration)", "MISSING MASTER"),
                              ("candidate-05-invalid-order", "rejected(illegal-configuration)", "FixtureAbsent.esp"),
                              ("candidate-06-target-lost", "rejected(exit-nonzero)", b_name),
                              ("candidate-07-view-differs", "rejected(exit-nonzero)", "frozen request"),
                              ("candidate-08-target-unmeasured", "rejected(exit-nonzero)", "was not measured"),
                              ("candidate-09-silent-incomplete", "rejected(exit-nonzero)", "no walk failure")):
        r, aid = rc(s), arm_of.get(s)
        e = led.get(f"{mao}__{aid}") or {}
        expect(execution_of(e), status, f"{case}/{s}: {mao} status", "D4")
        detail = " ".join(str(x.get("detail") or "") for x in ((r.get("validation") or {}).get("rejections") or []))
        check(needle in detail, f"{case}/{s}: the rejection names its real cause {needle!r} (got {detail[:200]!r})")
        check((mao, aid) not in nulls, f"{case}/{s}: {mao} is NOT a completed null")
        expect(missing.get((mao, aid)), status, f"{case}/{s}: {mao} listed missing with its status", "D7")
        expect(execution_of(led.get(f"{order}__{aid}")), f"blocked(prerequisite:{mao})",
               f"{case}/{s}: {order} blocked on the arm's own failed prerequisite", "§4.7")
        check((order, aid) not in nulls, f"{case}/{s}: {order} is NOT a completed null")


def case_24(tmp):
    """`resume` after a stopped sweep adopts nothing the stopped invocation recorded. A probe writes its emit
    and exits 0 while its descendant keeps running, so ordersweep's cache records it; the sweep is then stopped
    at the deadline. The cache entry proves only that the launched process exited, not that its tree was empty,
    so `resume` must re-measure that arm (the cache log shows RECORDED, not REUSED) and accept only the work of
    the resuming invocation."""
    case = "case-24-resume-after-stop-remeasures"
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    nav = "geometry.navmesh-ownership"
    run = tmp / case
    probe = tmp / "deadline-earlyexit-probe-24.py"
    probe.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_EARLYEXIT_PROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(run.parent / f"{case}-runlog"))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", "0.3",
              "--probe-cmd", f"navownprobe=python {probe} navownprobe"])
    expect(p.returncode, 0, f"{case}: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    plan = load(run / "plan.json") or {}
    cand = next((a for a in plan.get("arms") or [] if a.get("kind") == "candidate"), {})
    cand_id, cand_stem = cand.get("armId"), Path(cand.get("candidateFile") or "x.json").stem
    job = f"navownprobe-{cand_stem}"
    try:
        r1 = ev(["run", "--run", run], {"EVALRUN_SLOW_ARM": cand_stem, "EVALRUN_SLOW_PIDFILE": str(pidfile)},
                timeout=120)
    finally:
        left = []                        # protects the machine ONLY: kill by recorded PID
        for name, pid in _pids(pidfile).items():
            if _pid_alive(pid):
                left.append((name, pid))
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True) if os.name == "nt" \
                    else os.kill(pid, 9)
    check(not left, f"{case}: the stopped run left no recorded process alive (killed {left})")
    expect(r1.returncode, 0, f"{case}: the stopped run exits 0")
    led1 = ledger_entries(load(run / "ledger.json") or {})
    stopped_entry = led1.get(f"{nav}__{cand_id}__arm") or {}
    expect(execution_of(stopped_entry), "stopped(deadline)", f"{case}: the early-exit arm was stopped at the deadline")
    before = sweep_cache_lines(run)
    check(not any(l.startswith(f"RECORDED {job}:") for l in before),
          f"{case}: the stopped invocation did not cache the probe whose descendant was still running "
          f"(cache log: {before})")

    r2 = ev(["resume", "--run", run], timeout=180)
    expect(r2.returncode, 0, f"{case}: resume exits 0")
    added = sweep_cache_lines(run)[len(before):]
    check(not any(l.startswith(f"REUSED {job}:") for l in added),
          f"{case}: resume does NOT reuse the stopped invocation's cache entry (added: {added})")
    check(any(l.startswith(f"RECORDED {job}:") for l in added),
          f"{case}: resume re-measures the arm (RECORDED in the cache log; added: {added})")
    led2 = ledger_entries(load(run / "ledger.json") or {})
    e2 = led2.get(f"{nav}__{cand_id}__arm") or {}
    expect(execution_of(e2), "completed", f"{case}: the re-measured arm is completed after resume")
    check(e2.get("runId") and e2.get("runId") != stopped_entry.get("runId"),
          f"{case}: the accepted entry belongs to the resuming invocation, not the stopped one")
    check(not e2.get("reusedFromRunId"), f"{case}: nothing is carried over from the stopped invocation")


def case_25(tmp):
    """A stopped attempt's receipt carries no exit code of its own, and a stopped sweep keeps each part's own
    cause. (a) In-process through `Executor.account_stop`: a Runner result stopped at the deadline, whether its
    code is the stop's own 124 or the launched process's 0, writes `exit_code` None; a `launchedExitCode` is
    named in the detail only. (b) End to end under `--no-reverse`: a sweep stopped at the deadline accounts
    the candidate's reverse part `blocked(no-reverse-measurement)` - no reverse was ever going to be measured -
    and its arm part's receipt has a null exit code although the probe itself exited 0."""
    import importlib.util
    case = "case-25-stopped-receipt-and-own-causes"
    spec = importlib.util.spec_from_file_location("evalrun_case25", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    class Stub:
        plan = {"budgets": {"minutes": 0.3}}

        def __init__(self):
            self.calls = []

        def account(self, check, arm, status, cause, detail, **kw):
            self.calls.append({"status": status, "cause": cause, "detail": detail, **kw})

        def stop_sentence(self, res, what):
            return mod.Executor.stop_sentence(self, res, what)

    for label, res, launched in [
            ("stop code 124", {"stopped": "deadline", "exitCode": 124, "treeStopped": True, "seconds": 18.0}, None),
            ("launched exited 0", {"stopped": "deadline", "exitCode": None, "launchedExitCode": 0,
                                   "treeStopped": True, "seconds": 18.0}, 0),
            ("old shape, exit 0", {"stopped": "deadline", "exitCode": 0, "treeStopped": True, "seconds": 18.0}, None)]:
        stub = Stub()
        mod.Executor.account_stop(stub, {"checkId": "x"}, {"armId": "baseline"}, "arm", res, "the child")
        c = stub.calls[0] if stub.calls else {}
        expect(c.get("status"), "stopped(deadline)", f"{case}/{label}: accounted stopped(deadline)")
        check("exit_code" in c and c.get("exit_code") is None,
              f"{case}/{label}: the receipt's exit_code is None (got {c.get('exit_code', '<absent>')!r})")
        if launched is not None:
            check(f"exited with code {launched}" in (c.get("detail") or ""),
                  f"{case}/{label}: the launched process's own code is named in the detail")
        else:
            check("launched process" not in (c.get("detail") or ""),
                  f"{case}/{label}: no launched-process code is invented")

    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    nav = "geometry.navmesh-ownership"
    run = tmp / case
    probe = tmp / "deadline-earlyexit-probe-25.py"
    probe.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_EARLYEXIT_PROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(run.parent / f"{case}-runlog"))
    p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                        str(GEN / "registry" / "fixture-check-registry.json"),
                        "--scope", str(GEN / "cases" / fixture / "scope.json"),
                        "--candidates", str(GEN / "cases" / fixture / "candidates"),
                        "--out", str(run), "--harness", str(REAL_HARNESS)] + probe_cmds()
                       + ["--select", "navownprobe", "--budget-minutes", "0.3", "--no-reverse",
                          "--probe-cmd", f"navownprobe=python {probe} navownprobe"],
                       capture_output=True, text=True, env=env, timeout=300)
    expect(p.returncode, 0, f"{case}: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    plan = load(run / "plan.json") or {}
    cand = next((a for a in plan.get("arms") or [] if a.get("kind") == "candidate"), {})
    cand_id, cand_stem = cand.get("armId"), Path(cand.get("candidateFile") or "x.json").stem
    try:
        r = subprocess.run([sys.executable, str(EVALRUN), "run", "--run", str(run)], capture_output=True, text=True,
                           env=dict(env, EVALRUN_SLOW_ARM=cand_stem, EVALRUN_SLOW_PIDFILE=str(pidfile)), timeout=120)
    finally:
        left = []                        # protects the machine ONLY: kill by recorded PID
        for name, pid in _pids(pidfile).items():
            if _pid_alive(pid):
                left.append((name, pid))
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True) if os.name == "nt" \
                    else os.kill(pid, 9)
    check(not left, f"{case}: the stopped run left no recorded process alive (killed {left})")
    expect(r.returncode, 0, f"{case}: the stopped run exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{nav}__{cand_id}__arm")), "stopped(deadline)", f"{case}: the arm part is stopped")
    expect(execution_of(led.get(f"{nav}__{cand_id}__diff")), "stopped(deadline)", f"{case}: the diff part is stopped")
    expect(execution_of(led.get(f"{nav}__{cand_id}__reverse")), "blocked(no-reverse-measurement)",
           f"{case}: under --no-reverse the reverse part keeps its own cause, not the deadline")
    check(not any(execution_of(v) == "running" for v in led.values()), f"{case}: no entry is left running")
    rc = load(run / "receipts" / f"{nav}__{cand_id}__arm.json") or {}
    check(bool(rc) and "exitCode" in rc and rc["exitCode"] is None,
          f"{case}: the stopped arm's receipt has a null exit code (got {rc.get('exitCode', '<absent>')!r})")

    # (c) A sweep stopped at the deadline while a candidate arm is kept out of it by its own prerequisite
    # (the baseline's is settled, so the sweep runs): that arm keeps blocked(prerequisite:...) and none of its
    # parts is left `running`. The arm's prerequisite result is imposed in-process on the one controller call.
    run_c = tmp / f"{case}-blocked-arm"
    pidfile_c = tmp / f"{case}-blocked-arm.pids"
    p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                        str(GEN / "registry" / "fixture-check-registry.json"),
                        "--scope", str(GEN / "cases" / fixture / "scope.json"),
                        "--candidates", str(GEN / "cases" / fixture / "candidates"),
                        "--out", str(run_c), "--harness", str(REAL_HARNESS)] + probe_cmds()
                       + ["--select", "navownprobe", "--budget-minutes", "0.3",
                          "--probe-cmd", f"navownprobe=python {probe} navownprobe"],
                       capture_output=True, text=True, env=env, timeout=300)
    expect(p.returncode, 0, f"{case}/blocked-arm: plan exit")
    if p.returncode != 0:
        return
    real_allowed = mod.sweep_arms_allowed

    def blocked_allowed(ex, sweep_checks):
        allowed = real_allowed(ex, sweep_checks)
        allowed[cand_id] = "blocked:loadability.masters-and-order"
        return allowed
    mod.sweep_arms_allowed = blocked_allowed
    saved = {k: os.environ.get(k) for k in ("EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR", "EVALRUN_SLOW_ARM",
                                            "EVALRUN_SLOW_PIDFILE")}
    os.environ.update(EVALRUN_FAKE_SCENARIO=env["EVALRUN_FAKE_SCENARIO"],
                      HOUSECARL_RUNLOG_DIR=env["HOUSECARL_RUNLOG_DIR"],
                      EVALRUN_SLOW_ARM="baseline", EVALRUN_SLOW_PIDFILE=str(pidfile_c))
    try:
        a = mod.build_parser().parse_args(["run", "--run", str(run_c)])
        rc_c = a.fn(a)
    finally:
        mod.sweep_arms_allowed = real_allowed
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        left = []
        for name, pid in _pids(pidfile_c).items():
            if _pid_alive(pid):
                left.append((name, pid))
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True) if os.name == "nt" \
                    else os.kill(pid, 9)
    check(not left, f"{case}/blocked-arm: the stopped run left no recorded process alive (killed {left})")
    expect(rc_c, 0, f"{case}/blocked-arm: the run exits 0")
    led_c = ledger_entries(load(run_c / "ledger.json") or {})
    expect(execution_of(led_c.get(f"{nav}__baseline__arm")), "stopped(deadline)",
           f"{case}/blocked-arm: premise - the sweep was stopped at the deadline")
    expect(execution_of(led_c.get(f"{nav}__{cand_id}")), "blocked(prerequisite:loadability.masters-and-order)",
           f"{case}/blocked-arm: the kept-out arm keeps its prerequisite cause")
    check(not any(execution_of(v) == "running" for v in led_c.values()),
          f"{case}/blocked-arm: no entry is left running "
          f"(running: {[k for k, v in led_c.items() if execution_of(v) == 'running']})")

    # (d) A check whose prerequisite was stopped by the deadline is named for the budget: switchprobe never
    # finishes, state.switch-census is stopped at the deadline, and state.conditional-geometry, which requires
    # it, is `skipped-budget` (budget-exhausted), not blocked(prerequisite:...).
    run_d = tmp / f"{case}-dependent"
    pidfile_d = tmp / f"{case}-dependent.pids"
    hang = tmp / "hang-probe-25.py"
    hang.write_text("import os, sys, time\nopen(%r, 'a').write('probe %%d\\n' %% os.getpid())\ntime.sleep(900)\n"
                    % str(pidfile_d), encoding="utf-8")
    p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                        str(GEN / "registry" / "fixture-check-registry.json"),
                        "--scope", str(GEN / "cases" / fixture / "scope.json"),
                        "--candidates", str(GEN / "cases" / fixture / "candidates"),
                        "--out", str(run_d), "--harness", str(REAL_HARNESS)] + probe_cmds()
                       + ["--select", "stateprobe", "--budget-minutes", "0.2",
                          "--probe-cmd", f"switchprobe=python {hang} switchprobe"],
                       capture_output=True, text=True, env=env, timeout=300)
    expect(p.returncode, 0, f"{case}/dependent: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    try:
        r = subprocess.run([sys.executable, str(EVALRUN), "run", "--run", str(run_d)], capture_output=True,
                           text=True, env=env, timeout=120)
    finally:
        left = []
        for line in (pidfile_d.read_text().splitlines() if pidfile_d.exists() else []):
            pid = int(line.split()[1])
            if _pid_alive(pid):
                left.append(pid)
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True) if os.name == "nt" \
                    else os.kill(pid, 9)
    check(not left, f"{case}/dependent: the stopped run left no recorded process alive (killed {left})")
    expect(r.returncode, 0, f"{case}/dependent: the run exits 0")
    led_d = ledger_entries(load(run_d / "ledger.json") or {})
    census = [v for k, v in led_d.items() if k.startswith("state.switch-census__")]
    geo = {k: v for k, v in led_d.items() if k.startswith("state.conditional-geometry__")}
    check(any(execution_of(v) == "stopped(deadline)" for v in census),
          f"{case}/dependent: premise - state.switch-census was stopped at the deadline "
          f"(got {[execution_of(v) for v in census]})")
    check(bool(geo) and all(execution_of(v) == "skipped-budget" and v.get("cause") == "budget-exhausted"
                            for v in geo.values()),
          f"{case}/dependent: state.conditional-geometry is skipped-budget / budget-exhausted after the stop "
          f"(got {[(execution_of(v), v.get('cause')) for v in geo.values()]})")


LAUNCH_PROBE = r'''
"""The ordinary fake PROBE, which first appends one line per launch (`<probe> <argv>`) to LAUNCHES."""
import sys
sys.path.insert(0, FIXT_DIR)
from fakeprobe import main
with open(LAUNCHES, "a", encoding="utf-8") as f:
    f.write(PROBE + " " + " ".join(sys.argv[1:]) + "\n")
raise SystemExit(main(PROBE))
'''


def case_26(tmp):
    """The ordinary (non-derived, non-sweep) prerequisite gate takes the derived gate's rule: a prerequisite
    MEASURED on an arm and failed there decides for that arm, and the dependent check is not launched on it;
    a prerequisite not attempted on the arm still falls back to the baseline's answer.
    (a) End to end: masters-and-order is refused on one candidate and accepted on the baseline and another
    candidate. patch.discovery-installed is blocked on the refused arm by that prerequisite, and patchprobe is
    launched only for the two arms whose own order loads (a launch log counts them).
    (b) In process: the same plan with the reordered arm's masters-and-order not attempted there (accounted
    blocked before any launch). Discovery still runs on that arm on the baseline's accepted answer."""
    import importlib.util
    case = "case-26-ordinary-prerequisite-gate"
    mao, disc = "loadability.masters-and-order", "patch.discovery-installed"
    a_name = "FixtureTarget.esp"
    src = GEN / "cases" / "case-19-derived-prerequisites"
    d = GEN / "cases" / case
    pdir = d / "plugins"
    pdir.mkdir(parents=True, exist_ok=True)
    for p in (GEN / "plugins").iterdir():
        shutil.copyfile(p, pdir / p.name)
    scope = load(src / "scope.json")
    for r in scope["configuration"]["plugins"]:
        r["Path"] = str(pdir / r["Name"]).replace("\\", "/")
    (d / "scope.json").write_text(json.dumps(scope, indent=1), encoding="utf-8")
    cands = {
        "candidate-00-baseline": (["Skyrim.esm", "Update.esm", "FixtureBase.esp", a_name], "the profile's order"),
        "candidate-01-missing-master": (["Update.esm", "Skyrim.esm", a_name], "removed FixtureBase.esp"),
        "candidate-02-reorder": (["Skyrim.esm", "Update.esm", a_name, "FixtureBase.esp"],
                                 "moved FixtureTarget.esp above FixtureBase.esp"),
    }
    cdir = d / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)
    for stem, (plugins, change) in cands.items():
        (cdir / f"{stem}.json").write_text(json.dumps(
            {"SchemaVersion": 1, "Name": stem, "BaseProfile": "fixture", "Plugins": plugins,
             "Changes": [change], "Provenance": {"fixture": "evalrun test fixture"}}, indent=1), encoding="utf-8")
    (cdir / "candidates.json").write_text(json.dumps(
        {"SchemaVersion": 1, "Candidates": [{"File": f"{s}.json", "ViolatesAdvice": [], "SatisfiesAdvice": []}
                                            for s in cands],
         "Budget": {"evaluated": len(cands) - 1, "legalNotEvaluated": 0, "note": "fixture manifest"},
         "Contradictions": []}, indent=1), encoding="utf-8")
    scen = d / "scenario.json"
    scen.write_text(json.dumps({
        "pluginDir": str(pdir).replace("\\", "/"), "scopeFrom": str(d / "scope.json").replace("\\", "/"),
        "arms": {}, "loadability": {"candidate-01-missing-master": {
            "refuse": "MISSING MASTER - FixtureTarget.esp masters FixtureBase.esp, which the order does not load"}}},
        indent=1), encoding="utf-8")
    scope_fake = d / "scopeprobe_fake.py"
    scope_fake.write_text(SCOPE_FAKE_20, encoding="utf-8")
    reg = "fixture-check-registry-derived.json"

    def plan_run(tag, select=disc, wrap=("patchprobe",), extra=()):
        run = tmp / f"{case}-{tag}"
        launches = tmp / f"{case}-{tag}.launches"
        cmds = ["--probe-cmd", f"scopeprobe=python {scope_fake}"]
        for probe in wrap:
            wrapper = tmp / f"launch-probe-26-{tag}-{probe}.py"
            wrapper.write_text(f"FIXT_DIR = {str(FIXT)!r}\nLAUNCHES = {str(launches)!r}\nPROBE = {probe!r}\n"
                               + LAUNCH_PROBE, encoding="utf-8")
            cmds += ["--probe-cmd", f"{probe}=python {wrapper}"]
        expect(do_plan(run, case, ["--select", select] + cmds + list(extra), registry=reg).returncode, 0,
               f"{case}/{tag}: plan exit", "§4.7")
        plan = load(run / "plan.json") or {}
        arm_of = {Path(a.get("candidateFile") or "").stem: a["armId"] for a in plan.get("arms") or []}
        dchk = next((c for c in plan.get("checks") or [] if c.get("checkId") == select), {})
        planned = {r.get("armId"): r.get("status") for r in dchk.get("arms") or []}
        check(all(planned.get(arm_of.get(s)) == "planned" for s in cands),
              f"{case}/{tag}: premise - {select} is planned on every arm (got {planned})")
        return run, launches, arm_of

    def launch_lines(p, probe="patchprobe"):
        return [ln for ln in (p.read_text(encoding="utf-8").splitlines() if p.exists() else [])
                if ln.startswith(probe + " ")]

    def n_launches(p, probe="patchprobe"):
        return len(launch_lines(p, probe))

    def scenario(tag, refuse):
        s = d / f"scenario-{tag}.json"
        s.write_text(json.dumps({
            "pluginDir": str(pdir).replace("\\", "/"), "scopeFrom": str(d / "scope.json").replace("\\", "/"),
            "arms": {}, "loadability": {stem: {"refuse": f"MISSING MASTER - fixture refusal of {stem}"}
                                        for stem in refuse}}, indent=1), encoding="utf-8")
        return s

    def inprocess(run, scen_path, override, verb="run"):
        """One controller invocation in process, with run_generic replaced for the (check, arm) `override` claims."""
        spec_ = importlib.util.spec_from_file_location("evalrun_case26", EVALRUN)
        mod_ = importlib.util.module_from_spec(spec_)
        spec_.loader.exec_module(mod_)
        real = mod_.run_generic

        def generic(ex, check_row, arm):
            if not override(ex, check_row, arm):
                real(ex, check_row, arm)
        mod_.run_generic = generic
        saved_ = os.environ.get("EVALRUN_FAKE_SCENARIO")
        os.environ["EVALRUN_FAKE_SCENARIO"] = str(scen_path)
        try:
            a_ = mod_.build_parser().parse_args([verb, "--run", str(run)])
            return a_.fn(a_)
        finally:
            mod_.run_generic = real
            if saved_ is None:
                os.environ.pop("EVALRUN_FAKE_SCENARIO", None)
            else:
                os.environ["EVALRUN_FAKE_SCENARIO"] = saved_

    def not_attempted_on(arm_id):
        def override(ex, check_row, arm):
            if check_row["checkId"] == mao and arm["armId"] == arm_id:
                ex.account(check_row, arm, "blocked(fixture-not-attempted)", "blocked-prerequisite",
                           "fixture: accounted before any launch, so this arm's answer was never measured")
                return True
            return False
        return override

    def block_detail(entry):
        return " ".join(str(x.get("detail") or "") for x in ((entry.get("validation") or {}).get("rejections") or []))

    # (a) a measured failure on the arm blocks the dependent check there, and nothing is launched for it
    run, launches, arm_of = plan_run("measured-failure")
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/measured-failure: run exit", "§4.7")
    led = ledger_entries(load(run / "ledger.json") or {})
    bad, good = arm_of.get("candidate-01-missing-master"), arm_of.get("candidate-02-reorder")
    expect(execution_of(led.get(f"{mao}__baseline")), "completed", f"{case}/measured-failure: premise - {mao} "
           "accepted on the baseline")
    expect(execution_of(led.get(f"{mao}__{bad}")), "rejected(illegal-configuration)",
           f"{case}/measured-failure: premise - {mao} measured and refused on {bad}")
    expect(execution_of(led.get(f"{mao}__{good}")), "completed", f"{case}/measured-failure: premise - {mao} "
           f"accepted on {good}")
    e = led.get(f"{disc}__{bad}") or {}
    expect(execution_of(e), f"blocked(prerequisite:{mao})",
           f"{case}/measured-failure: {disc} [{bad}] is blocked by its own arm's refused prerequisite", "§4.7")
    expect(e.get("cause"), "blocked-prerequisite", f"{case}/measured-failure: {disc} [{bad}] cause", "§4.3")
    detail = " ".join(str(x.get("detail") or "") for x in ((e.get("validation") or {}).get("rejections") or []))
    check(f"{mao} (rejected(illegal-configuration) on this arm)" in detail,
          f"{case}/measured-failure: the block names the arm's own refused prerequisite (got {detail[:160]!r})")
    for aid in ("baseline", good):
        expect(execution_of(led.get(f"{disc}__{aid}")), "completed",
               f"{case}/measured-failure: {disc} [{aid}] still runs where its own order loads", "§4.7")
    expect(n_launches(launches), 2, f"{case}/measured-failure: patchprobe launched for the baseline and "
           f"{good} only, never for {bad}", "§4.7")

    # (b) a prerequisite not attempted on the arm falls back to the baseline's accepted answer
    run, launches, arm_of = plan_run("not-attempted")
    good = arm_of.get("candidate-02-reorder")
    expect(inprocess(run, scen, not_attempted_on(good)), 0, f"{case}/not-attempted: run exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{mao}__{good}")), "blocked(fixture-not-attempted)",
           f"{case}/not-attempted: premise - {mao} was not attempted on {good}")
    expect(execution_of(led.get(f"{disc}__{good}")), "completed",
           f"{case}/not-attempted: {disc} [{good}] runs on the baseline's accepted {mao}", "§4.7")
    expect(n_launches(launches), 2, f"{case}/not-attempted: patchprobe launched for the baseline and {good}")

    # (c) one hop down: a prerequisite BLOCKED on this arm by the arm's own measured failure decides there too.
    # switch-census is blocked on the refused arm by masters-and-order, so conditional-geometry is not launched there.
    geo, census = "state.conditional-geometry", "state.switch-census"
    run, launches, arm_of = plan_run("one-hop", select=geo, wrap=("switchprobe", "stateprobe"),
                                     extra=["--states", "baseline,fixture-switched"])
    bad_stem, good_stem = "candidate-01-missing-master", "candidate-02-reorder"
    bad, good = arm_of.get(bad_stem), arm_of.get(good_stem)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/one-hop: run exit", "§4.7")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{census}__{bad}")), f"blocked(prerequisite:{mao})",
           f"{case}/one-hop: premise - {census} [{bad}] blocked by the arm's refused {mao}")
    expect(execution_of(led.get(f"{census}__baseline")), "completed",
           f"{case}/one-hop: premise - {census} accepted on the baseline")
    e = led.get(f"{geo}__{bad}") or {}
    expect(execution_of(e), f"blocked(prerequisite:{census})",
           f"{case}/one-hop: {geo} [{bad}] is blocked, not run on the baseline's {census}", "§4.6")
    check(f"blocked(prerequisite:{mao}) on this arm" in block_detail(e),
          f"{case}/one-hop: the block names the arm's own blocked prerequisite (got {block_detail(e)[:160]!r})")
    state_lines = launch_lines(launches, "stateprobe")
    check(any(good_stem in ln for ln in state_lines),
          f"{case}/one-hop: stateprobe still launches for {good}, and its lines name their arm's order "
          f"({len(state_lines)} lines)")
    check(not any(bad_stem in ln for ln in state_lines),
          f"{case}/one-hop: stateprobe was never launched for {bad} (got {[ln[:120] for ln in state_lines]})")

    # (d) reuse: a dependent accepted on the baseline's answer is not reused once its own arm's prerequisite is
    # measured and fails on resume - ordinary (discovery) and derived (order evaluation, qualification) alike -
    # while entries whose prerequisites still hold are reused.
    qual, order = "patch.candidate-qualification", "patch.order-evaluation"
    run, launches, arm_of = plan_run("reuse", select=qual)
    good = arm_of.get(good_stem)
    expect(inprocess(run, scen, not_attempted_on(good)), 0, f"{case}/reuse: first run exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    for cid in (disc, order, qual):
        expect(execution_of(led.get(f"{cid}__{good}")), "completed",
               f"{case}/reuse: premise - {cid} [{good}] completed on the baseline's answer")
    bad = arm_of.get(bad_stem)
    expect(execution_of(led.get(f"{qual}__{bad}")), f"blocked(prerequisite:{disc})",
           f"{case}/reuse: one hop down in a derived check - {qual} [{bad}] does not derive over {disc} blocked by "
           f"the arm's refused {mao}", "§4.6")
    before = n_launches(launches)
    scen_both = scenario("refuse-both", [bad_stem, good_stem])
    expect(evalrun(["resume", "--run", run], scen_both).returncode, 0, f"{case}/reuse: resume exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{mao}__{good}")), "rejected(illegal-configuration)",
           f"{case}/reuse: premise - resume measured {mao} on {good} and it was refused")
    for cid in (disc, order, qual):
        st = execution_of(led.get(f"{cid}__{good}"))
        check(str(st).startswith("blocked(prerequisite:"),
              f"{case}/reuse: {cid} [{good}] is blocked after its arm's prerequisite failed, not reused (got {st!r})")
    expect(execution_of(led.get(f"{disc}__baseline")), "reused",
           f"{case}/reuse: {disc} [baseline] whose prerequisite still holds is reused")
    expect(n_launches(launches), before, f"{case}/reuse: resume launched no patchprobe")

    # (e) `failed` is a measured failure like `rejected(...)`
    run, launches, arm_of = plan_run("failed-status")
    good = arm_of.get(good_stem)

    def failed_on_good(ex, check_row, arm):
        if check_row["checkId"] == mao and arm["armId"] == good:
            ex.account(check_row, arm, "failed", None, "fixture: the child ran and failed")
            return True
        return False
    expect(inprocess(run, scen, failed_on_good), 0, f"{case}/failed-status: run exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{mao}__{good}")), "failed", f"{case}/failed-status: premise - {mao} failed on {good}")
    expect(execution_of(led.get(f"{disc}__{good}")), f"blocked(prerequisite:{mao})",
           f"{case}/failed-status: {disc} [{good}] blocked by its arm's failed {mao}", "§4.6")
    expect(n_launches(launches), 1, f"{case}/failed-status: patchprobe launched for the baseline only")

    # (f) the sweep's all-reusable shortcut: an arm whose prerequisite failed on resume is not reused with the rest;
    # its existing parts are blocked, and the other arms' parts stay reused.
    nav = "geometry.navmesh-ownership"
    run, launches, arm_of = plan_run("sweep-reuse", select=nav, wrap=("navownprobe",))
    good = arm_of.get(good_stem)
    scen_none = scenario("refuse-none", [])
    expect(inprocess(run, scen_none, not_attempted_on(good)), 0, f"{case}/sweep-reuse: first run exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    parts_ok = {k: execution_of(v) for k, v in led.items() if k.startswith(f"{nav}__")}
    check(parts_ok and all(v == "completed" for v in parts_ok.values()),
          f"{case}/sweep-reuse: premise - every sweep part completed on the first run (got {parts_ok})")
    scen_good = scenario("refuse-good", [good_stem])
    expect(evalrun(["resume", "--run", run], scen_good).returncode, 0, f"{case}/sweep-reuse: resume exit")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{mao}__{good}")), "rejected(illegal-configuration)",
           f"{case}/sweep-reuse: premise - resume measured {mao} on {good} and it was refused")
    good_parts = {k: execution_of(v) for k, v in led.items() if k.startswith(f"{nav}__{good}")}
    check(good_parts and all(str(v).startswith(f"blocked(prerequisite:{mao})") for v in good_parts.values()),
          f"{case}/sweep-reuse: every {nav} entry on {good} is blocked, none reused (got {good_parts})")
    other = {k: execution_of(v) for k, v in led.items() if k.startswith(f"{nav}__") and f"__{good}" not in k}
    check(other and all(v == "reused" for v in other.values()),
          f"{case}/sweep-reuse: the other arms' sweep parts are reused (got {other})")


def case_27(tmp):
    """An ordersweep that was NOT stopped credits only what its own child produced or verifiably reused.
    Everything runs in-process against a planned fixture run, with the Runner's ordersweep job wrapped so the
    test controls what the child leaves on disk; the probes, ordersweep.py and conflictdiff stay real.
    Since 2026-09-17 every launch writes its own `RUN/sweep/inv-NNNN` folder and a job is credited only from the
    record that launch appended to its `jobs.jsonl`; the earlier invocations' files below sit in EARLIER launch
    folders, which is where they now are.
    (a) An arm kept out of the sweep by its prerequisite keeps blocked(prerequisite:...): the sweep does not
        measure it from a copy an earlier invocation left in RUN/candidates, and no part is written for it.
    (b) A sweep child that fails before writing anything: the summary, per-job logs, emits, diff log and
        cache-log REUSED lines left by EARLIER launches credit nothing - no part is terminal-ok. (The diff part's
        cause was `output-stale` while an earlier diff log sat at this launch's path; no earlier file is at any
        path this launch reads now, so its cause is that no diff ran on an unaccepted arm: `output-missing`.)
    (c) The control for (b): the next invocation's genuine cache reuse (REUSED by its own child from an earlier
        launch's verified cache) is still credited although the reused emits are an hour old.
    (d) A job this child started and did not finish has no record in its `jobs.jsonl`, whatever its log or emit
        holds: that arm is not credited, while a job of the same child that did complete is.
    (e) A reverse job this child ran that wrote nothing, after an earlier launch wrote that reverse emit: it is
        not this child's measurement. (Its cause was `output-stale` while the earlier emit sat at the same path; the
        earlier emit is in its own launch folder now, so the cause is `output-missing`.)"""
    import importlib.util
    import re
    case = "case-27-sweep-credits-own-results"
    spec = importlib.util.spec_from_file_location("evalrun_case27", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)
    nav = "geometry.navmesh-ownership"
    store = tmp / f"{case}-runlog"

    base_runner = mod.Runner

    class HookRunner(base_runner):
        hook = None

        def run(self, jobs, echo=True):
            if HookRunner.hook is not None and jobs and jobs[0].get("id") == "ordersweep":
                return HookRunner.hook(lambda js: base_runner.run(self, js, echo), jobs)
            return base_runner.run(self, jobs, echo)

    class Killed(Exception):
        """The controller dies after its sweep child finished, before the split wrote a receipt."""

    def plan(run):
        p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                            str(GEN / "registry" / "fixture-check-registry.json"),
                            "--scope", str(GEN / "cases" / fixture / "scope.json"),
                            "--candidates", str(GEN / "cases" / fixture / "candidates"),
                            "--out", str(run), "--harness", str(REAL_HARNESS)] + probe_cmds()
                           + ["--select", "navownprobe"],
                           capture_output=True, text=True, timeout=300,
                           env=dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store)))
        expect(p.returncode, 0, f"{case}: plan exit for {run.name}")
        if p.returncode != 0:
            print(p.stdout[-2000:], p.stderr[-2000:])
            return None
        pl = load(run / "plan.json") or {}
        return next((a for a in pl.get("arms") or [] if a.get("kind") == "candidate"), None)

    def inproc(args, hook=None, scenario=None):
        HookRunner.hook = hook
        saved = {k: os.environ.get(k) for k in ("EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR")}
        os.environ.update(EVALRUN_FAKE_SCENARIO=str(scenario or scen), HOUSECARL_RUNLOG_DIR=str(store))
        try:
            a = mod.build_parser().parse_args([str(x) for x in args])
            return a.fn(a)
        finally:
            HookRunner.hook = None
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

    def led_of(run):
        return ledger_entries(load(run / "ledger.json") or {})

    def status(led, key):
        return execution_of(led.get(key))

    def log_lines(p):
        return p.read_text(encoding="utf-8").splitlines() if p.exists() else []

    real_runner, real_allowed = mod.Runner, mod.sweep_arms_allowed
    mod.Runner = HookRunner
    try:
        # (a) ---------------------------------------------------------------- the prerequisite-blocked arm
        run_a = tmp / f"{case}-blocked"
        cand = plan(run_a)
        if cand is None:
            return
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        (run_a / "candidates").mkdir(parents=True, exist_ok=True)
        shutil.copyfile(cand["candidatePath"], run_a / "candidates" / cand["candidateFile"])  # an earlier copy

        def blocked_allowed(ex, sweep_checks):
            allowed = real_allowed(ex, sweep_checks)
            allowed[cid] = "blocked:loadability.masters-and-order"
            return allowed
        mod.sweep_arms_allowed = blocked_allowed
        try:
            expect(inproc(["run", "--run", run_a]), 0, f"{case}/blocked: the run exits 0")
        finally:
            mod.sweep_arms_allowed = real_allowed
        led = led_of(run_a)
        expect(status(led, f"{nav}__baseline__arm"), "completed",
               f"{case}/blocked: premise - the sweep ran to completion on the baseline")
        expect(status(led, f"{nav}__{cid}"), "blocked(prerequisite:loadability.masters-and-order)",
               f"{case}/blocked: the kept-out arm keeps its prerequisite cause")
        parts = {k: status(led, k) for k in led if k.startswith(f"{nav}__{cid}__")}
        expect(parts, {}, f"{case}/blocked: no sweep part is written for the kept-out arm")
        check(not (run_a / "candidates" / cand["candidateFile"]).exists(),
              f"{case}/blocked: the earlier copy of the kept-out arm's order is removed from RUN/candidates")
        check(not any((f / "logs" / f"navownprobe-{stem}.txt").exists() for f in sweep_launches(run_a)),
              f"{case}/blocked: ordersweep did not measure the kept-out arm")

        # (b) ------------------------------------------------- results left by earlier invocations
        run_b = tmp / f"{case}-stale"
        cand = plan(run_b)
        if cand is None:
            return
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem

        def kill_after(real, jobs):
            real(jobs)
            raise Killed()
        for verb in ("run", "resume"):              # measured (RECORDED), then served from the cache (REUSED)
            try:
                inproc([verb, "--run", run_b], kill_after)
                check(False, f"{case}/stale: premise - the {verb} invocation was killed after its sweep child")
            except Killed:
                pass
        earlier = sweep_launches(run_b)
        check(len(earlier) == 2 and f"REUSED navownprobe-{stem}:" in "\n".join(log_lines(earlier[-1] / "cache-log.txt")),
              f"{case}/stale: premise - the second of two earlier launches reused the candidate job from the cache")
        check(all((f / "summary.json").exists() and (f / "logs" / f"diff-navownprobe-{stem}.txt").exists()
                  for f in earlier) and (earlier[0] / "navownprobe" / f"{stem}.reverse.json").exists(),
              f"{case}/stale: premise - the earlier launches left summaries, diff logs and a reverse emit")
        old = time.time() - 3600
        for p in (run_b / "sweep").rglob("*"):
            if p.is_file():
                os.utime(p, (old, old))             # the earlier invocations were an hour ago

        def fail_early(real, jobs):
            empty = run_b / "no-candidates"
            empty.mkdir(exist_ok=True)
            argv = list(jobs[0]["argv"])
            argv[argv.index("--candidates") + 1] = str(empty)
            return real([dict(jobs[0], argv=argv)])  # the real ordersweep exits 1 before writing anything
        expect(inproc(["resume", "--run", run_b], fail_early), 0, f"{case}/stale: the failed-sweep resume exits 0")
        check(any(ln.startswith("(exit 1,") for ln in log_lines(run_b / "logs" / "ordersweep.txt")),
              f"{case}/stale: premise - this invocation's ordersweep child exited 1")
        failed = last_sweep(run_b)
        check(failed not in earlier and not log_lines(failed / "cache-log.txt") and not log_lines(failed / "jobs.jsonl"),
              f"{case}/stale: premise - this child wrote no cache line and no job record in its own folder")
        led = led_of(run_b)
        want = {f"{nav}__baseline__arm": "rejected(completion-missing)",
                f"{nav}__{cid}__arm": "rejected(completion-missing)",
                f"{nav}__{cid}__reverse": "blocked(no-reverse-measurement)",
                f"{nav}__{cid}__diff": "rejected(output-missing)"}
        for key, st in want.items():
            expect(status(led, key), st, f"{case}/stale: {key.split('__', 1)[1]} credits nothing earlier "
                                         f"invocations left")

        # (c) ------------------------------------------------------ genuine reuse is still credited
        expect(inproc(["resume", "--run", run_b]), 0, f"{case}/reuse: the next resume exits 0")
        added = log_lines(last_sweep(run_b) / "cache-log.txt")
        for job in (f"navownprobe-baseline", f"navownprobe-{stem}", f"navownprobe-{stem}-reverse"):
            check(any(ln.startswith(f"REUSED {job}:") for ln in added) and
                  not any(ln.startswith(f"RECORDED {job}:") for ln in added),
                  f"{case}/reuse: premise - this invocation's child reused {job} from its cache (added: {added})")
        led = led_of(run_b)
        for key in want:
            expect(status(led, key), "completed", f"{case}/reuse: {key.split('__', 1)[1]} is credited "
                                                  f"from this invocation's verified reuse")

        # (d) ------------------------------------------- a job this child started and did not finish
        run_d = tmp / f"{case}-unfinished"
        cand = plan(run_d)
        if cand is None:
            return
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        subs = []

        def unfinished(real, jobs):
            res = real(jobs)
            sweep = Path(jobs[0]["argv"][jobs[0]["argv"].index("--out") + 1])
            (sweep / "summary.json").unlink(missing_ok=True)
            log = sweep / "logs" / f"navownprobe-{stem}.txt"
            text, n = re.subn(r"^\(exit -?\d+, [^)\n]*\)$",
                              "(stopped at the deadline after 1.0s: launched process tree terminated and "
                              "confirmed gone (fixture); no completion)",
                              log.read_text(encoding="utf-8"), flags=re.M)
            subs.append(n)
            log.write_text(text, encoding="utf-8")
            # the job died before ordersweep could append its record, and nothing after it ran
            keep = [ln for ln in (sweep / "jobs.jsonl").read_text(encoding="utf-8").splitlines()
                    if json.loads(ln)["job"] == "navownprobe-baseline"]
            subs.append(len(keep))
            (sweep / "jobs.jsonl").write_text("".join(ln + "\n" for ln in keep), encoding="utf-8")
            for p in (sweep / "navownprobe" / f"{stem}.reverse.json", sweep / "logs" / f"navownprobe-{stem}-reverse.txt",
                      sweep / "logs" / f"diff-navownprobe-{stem}.txt"):
                p.unlink(missing_ok=True)
            return [dict(res[0], exitCode=1)]       # and ordersweep died before its summary
        expect(inproc(["run", "--run", run_d], unfinished), 0, f"{case}/unfinished: the run exits 0")
        expect(subs, [1, 1], f"{case}/unfinished: premise - one completion trailer replaced, one job record kept")
        check((last_sweep(run_d) / "navownprobe" / f"{stem}.json").exists(),
              f"{case}/unfinished: premise - the unfinished job's emit is on disk, written by this child")
        led = led_of(run_d)
        expect(status(led, f"{nav}__baseline__arm"), "completed",
               f"{case}/unfinished: the job this child completed is credited")
        expect(status(led, f"{nav}__{cid}__arm"), "rejected(completion-missing)",
               f"{case}/unfinished: a log with no completion trailer is not an exit 0")
        expect(status(led, f"{nav}__{cid}__reverse"), "blocked(no-reverse-measurement)",
               f"{case}/unfinished: the reverse part keeps its own cause")
        expect(status(led, f"{nav}__{cid}__diff"), "rejected(output-missing)",
               f"{case}/unfinished: the diff part keeps its own cause")

        # (e) ---------------- a reverse job this child ran that wrote nothing, over an earlier invocation's emit
        run_e = tmp / f"{case}-reverse-no-emit"
        cand = plan(run_e)
        if cand is None:
            return
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        try:
            inproc(["run", "--run", run_e], kill_after)
            check(False, f"{case}/reverse-no-emit: premise - the first invocation was killed after its sweep child")
        except Killed:
            pass
        first = last_sweep(run_e)
        rev = first / "navownprobe" / f"{stem}.reverse.json"
        check(rev.exists(), f"{case}/reverse-no-emit: premise - the earlier invocation wrote the reverse emit")
        (first / "cache.json").unlink(missing_ok=True)    # the cache is lost: every job is measured again
        old = time.time() - 3600
        os.utime(rev, (old, old))
        doc = load(scen)
        doc["arms"]["navownprobe"][f"{stem}.reverse"] = {"behaviour": "no-emit"}
        scen_e = tmp / f"{case}-reverse-no-emit.scenario.json"
        scen_e.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        expect(inproc(["resume", "--run", run_e], scenario=scen_e), 0, f"{case}/reverse-no-emit: resume exits 0")
        rl = last_sweep(run_e) / "logs" / f"navownprobe-{stem}-reverse.txt"
        check(last_sweep(run_e) != first and rl.exists() and "without writing the emit" in rl.read_text(encoding="utf-8")
              and any(ln.startswith("(exit 0,") for ln in log_lines(rl)),
              f"{case}/reverse-no-emit: premise - this child ran the reverse job, which exited 0 writing nothing")
        led = led_of(run_e)
        expect(status(led, f"{nav}__{cid}__arm"), "completed",
               f"{case}/reverse-no-emit: premise - this child measured the candidate arm")
        expect(status(led, f"{nav}__{cid}__reverse"), "rejected(output-missing)",
               f"{case}/reverse-no-emit: the earlier reverse emit is not this child's measurement")
        rc = load(run_e / "receipts" / f"{nav}__{cid}__reverse.json") or {}
        check(rev.exists() and not any(str(o.get("path", "")).startswith(f"sweep/{first.name}/")
                                       for o in rc.get("outputs") or []),
              f"{case}/reverse-no-emit: the reverse receipt names no file of the earlier launch, which is left in place")
    finally:
        mod.Runner, mod.sweep_arms_allowed = real_runner, real_allowed


def case_28(tmp):
    """ordersweep caches only an emit its own probe launch wrote. The real ordersweep.py over the case-08
    candidates, four invocations into one --out: (1) every job measured and RECORDED; the cache is then lost;
    (2) the candidate's probe exits 0 WITHOUT writing, while the earlier emit is still at its path - that job
    must not be RECORDED, while the baseline job, which did write, is; (3) the candidate is therefore measured
    again, writes a fresh emit and is RECORDED, not REUSED; (4) that fresh entry is REUSED. The earlier emit is
    never deleted: an accepted receipt may hash it. (5) In the controller: a sweep child that ran every job and
    died before writing its summary leaves the diff part rejected(completion-missing), not schema-unsupported."""
    case = "case-28-cache-records-own-emit"
    _, _, cands_src, scen = case_paths("case-08-interrupt-resume")
    out = tmp / case / "sweep"
    cdir = tmp / case / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)
    for f in cands_src.iterdir():
        shutil.copyfile(f, cdir / f.name)
    base = next(cdir.glob("candidate-*-baseline.json"))
    stem = next(f.stem for f in cdir.glob("candidate-*.json") if f != base)
    doc = load(scen)
    doc["arms"]["navownprobe"][stem] = {"behaviour": "no-emit"}
    scen_no_emit = tmp / case / "scenario-no-emit.json"
    scen_no_emit.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    clog = out / "cache-log.txt"
    emit = out / "navownprobe" / f"{stem}.json"
    job = f"navownprobe-{stem}"

    def sweep(scenario):
        n = len(clog.read_text(encoding="utf-8").splitlines()) if clog.exists() else 0
        r = subprocess.run([sys.executable, str(HARNESS / "ordersweep.py"), "--candidates", str(cdir),
                            "--out", str(out), "--probes", "navownprobe", "--harness", str(REAL_HARNESS),
                            "--baseline", str(base),
                            "--probe-cmd", f"navownprobe=python {FIXT / 'fakeprobes' / 'navownprobe.py'}"],
                           capture_output=True, text=True, timeout=300,
                           env=dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scenario),
                                    HOUSECARL_RUNLOG_DIR=str(tmp / f"{case}-runlog")))
        if r.returncode != 0:
            print(r.stdout[-2000:], r.stderr[-2000:])
        return r.returncode, clog.read_text(encoding="utf-8").splitlines()[n:] if clog.exists() else []

    def has(lines, word, j):
        return any(ln.startswith(f"{word} {j}:") for ln in lines)

    code, added = sweep(scen)
    expect(code, 0, f"{case}/1: ordersweep exits 0")
    check(has(added, "RECORDED", job) and has(added, "RECORDED", "navownprobe-baseline"),
          f"{case}/1: a fresh emit with nothing earlier at its path is recorded (added: {added})")
    (out / "cache.json").unlink(missing_ok=True)       # the cache is lost; the emits stay
    earlier = emit.read_bytes()
    earlier_mtime = emit.stat().st_mtime_ns            # a rewrite changes it even when the fake writes the same bytes

    code, added = sweep(scen_no_emit)
    expect(code, 0, f"{case}/2: ordersweep exits 0")
    log2 = (out / "logs" / f"{job}.txt").read_text(encoding="utf-8")
    check("without writing the emit" in log2 and "(exit 0," in log2 and emit.read_bytes() == earlier,
          f"{case}/2: premise - the probe exited 0 without writing, and the earlier emit is still at its path")
    check(has(added, "RECORDED", "navownprobe-baseline"),
          f"{case}/2: the baseline job, which wrote its emit over an earlier one, is still recorded (added: {added})")
    check(not has(added, "RECORDED", job) and has(added, "NOT-RECORDED", job),
          f"{case}/2: the job that wrote nothing is NOT recorded with the earlier emit, and says so (added: {added})")
    # Review V2 H2 inside ordersweep: a zero exit over a file this launch did not write is not a measurement, so no
    # reverse or diff is built on it and its classification is not a pass (evalrun gives every launch a fresh folder;
    # a hand-run ordersweep reusing one --out is where an earlier emit can still sit at the path).
    rec2 = {}
    for ln in ((out / "jobs.jsonl").read_text(encoding="utf-8").splitlines() if (out / "jobs.jsonl").exists() else []):
        rec2[json.loads(ln)["job"]] = json.loads(ln)
    summ2 = (load(out / "summary.json") or {}).get("candidates", {}).get(stem, {})
    row2 = next((r for r in summ2.get("rows", []) if r.get("probe") == "navownprobe"), {})
    check((rec2.get(job) or {}).get("outcome") == "ran" and (rec2.get(job) or {}).get("emitWritten") is False,
          f"{case}/2: the job record says the job ran and wrote nothing (record: {rec2.get(job)})")
    check(f"{job}-reverse" not in rec2 and f"diff-{job}" not in rec2 and row2.get("emitNotWritten") is True
          and (row2.get("diff") or {}).get("verdict") == "(not run)" and summ2.get("classification") == "UNRESOLVED",
          f"{case}/2: no reverse or diff is built on the earlier emit and the candidate reads UNRESOLVED "
          f"(jobs {sorted(rec2)}, classification {summ2.get('classification')}, diff {(row2.get('diff') or {}).get('verdict')})")

    code, added = sweep(scen)
    expect(code, 0, f"{case}/3: ordersweep exits 0")
    check(has(added, "REUSED", "navownprobe-baseline"), f"{case}/3: premise - the baseline job is reused (added: {added})")
    check(not has(added, "REUSED", job),
          f"{case}/3: the earlier emit is never served from the cache (added: {added})")
    check(has(added, "RECORDED", job) and emit.stat().st_mtime_ns != earlier_mtime,
          f"{case}/3: the job is measured again and its fresh emit recorded (added: {added})")

    code, added = sweep(scen)
    expect(code, 0, f"{case}/4: ordersweep exits 0")
    check(has(added, "REUSED", job), f"{case}/4: the fresh emit is reusable (added: {added})")

    # The controller side, in-process (case 27's technique): the real ordersweep, with the Runner's ordersweep
    # job and the split wrapped so the test controls what an interrupted or failed child leaves behind.
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalrun_case28", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    fixture = "case-08-interrupt-resume"
    store = tmp / f"{case}-runlog"
    base_runner = mod.Runner

    class HookRunner(base_runner):
        hook = None

        def run(self, jobs, echo=True):
            if HookRunner.hook is not None and jobs and jobs[0].get("id") == "ordersweep":
                return HookRunner.hook(lambda js: base_runner.run(self, js, echo), jobs)
            return base_runner.run(self, jobs, echo)

    def plan(run, extra):
        p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                            str(GEN / "registry" / "fixture-check-registry.json"),
                            "--scope", str(GEN / "cases" / fixture / "scope.json"),
                            "--candidates", str(GEN / "cases" / fixture / "candidates"),
                            "--out", str(run), "--harness", str(REAL_HARNESS)] + probe_cmds() + extra,
                           capture_output=True, text=True, timeout=300,
                           env=dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store)))
        expect(p.returncode, 0, f"{case}: plan exit for {run.name}")
        pl = load(run / "plan.json") or {}
        return next((a for a in pl.get("arms") or [] if a.get("kind") == "candidate"), None)

    def inproc(args, hook=None):
        HookRunner.hook = hook
        saved = {k: os.environ.get(k) for k in ("EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR")}
        os.environ.update(EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store))
        try:
            a = mod.build_parser().parse_args([str(x) for x in args])
            return a.fn(a)
        finally:
            HookRunner.hook = None
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

    nav = "geometry.navmesh-ownership"
    mod.Runner = HookRunner
    try:
        # (5) a sweep child that measured every job and died before its summary: the diff part's cause is the
        # missing summary row, not a conflictdiff without a verdict line
        run5 = tmp / f"{case}-no-summary"
        cand = plan(run5, ["--select", "navownprobe"])
        if cand is None:
            return
        cid, cstem = cand["armId"], Path(cand["candidateFile"]).stem

        def no_summary(real, jobs):
            res = real(jobs)
            (last_sweep(run5) / "summary.json").unlink()
            return [dict(res[0], exitCode=1)]
        expect(inproc(["run", "--run", run5], no_summary), 0, f"{case}/no-summary: run exits 0")
        led = ledger_entries(load(run5 / "ledger.json") or {})
        dlog = last_sweep(run5) / "logs" / f"diff-navownprobe-{cstem}.txt"
        check(dlog.exists() and "(exit 0," in dlog.read_text(encoding="utf-8"),
              f"{case}/no-summary: premise - this child ran the diff to completion")
        expect(execution_of(led.get(f"{nav}__{cid}__arm")), "completed",
               f"{case}/no-summary: premise - the probe job this child completed is credited")
        expect(execution_of(led.get(f"{nav}__{cid}__diff")), "rejected(completion-missing)",
               f"{case}/no-summary: a diff with no summary row names that cause")
    finally:
        mod.Runner = base_runner


CASES["case-20"] = case_20
CASES["case-21"] = case_21
CASES["case-22"] = case_22
CASES["case-23"] = case_23
CASES["case-24"] = case_24
CASES["case-25"] = case_25
CASES["case-26"] = case_26
CASES["case-27"] = case_27
CASES["case-28"] = case_28


def case_29(tmp):
    """Review V2 H1. patch.order-evaluation derives only from the arm's ACCEPTED diff parts: when the sweep's summary
    carries no verdict, the diff part is rejected and the order evaluation is blocked by it, not completed over it,
    and the disposition's per-arm order evaluation reads nothing from it; the same run without that damage still
    derives and names the probe."""
    import importlib.util
    case = "case-29-order-evaluation-accepted-diffs"
    nav, order = "geometry.navmesh-ownership", "patch.order-evaluation"
    fixture = "case-08-interrupt-resume"
    _, _, _, scen = case_paths(fixture)

    def oe_run(tag, damage):
        spec = importlib.util.spec_from_file_location(f"evalrun_case29_{tag}", EVALRUN)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        run = tmp / f"{case}-{tag}"
        store = tmp / f"{case}-{tag}-runlog"
        p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                            str(GEN / "registry" / "fixture-check-registry.json"),
                            "--scope", str(GEN / "cases" / fixture / "scope.json"),
                            "--candidates", str(GEN / "cases" / fixture / "candidates"),
                            "--out", str(run), "--harness", str(REAL_HARNESS)] + probe_cmds()
                           + ["--select", f"navownprobe,{order}"],
                           capture_output=True, text=True, timeout=300,
                           env=dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store)))
        expect(p.returncode, 0, f"{case}/{tag}: plan exit")
        cand = next(a for a in (load(run / "plan.json") or {}).get("arms", []) if a.get("kind") == "candidate")
        stem = Path(cand["candidateFile"]).stem
        base_runner = mod.Runner

        class HookRunner(base_runner):
            def run(self, jobs, echo=True):
                res = base_runner.run(self, jobs, echo)
                if damage and jobs and jobs[0].get("id") == "ordersweep":
                    s = load(last_sweep(run) / "summary.json")
                    for r in s["candidates"][stem]["rows"]:
                        r["diff"]["verdict"] = "(no verdict line)"
                    (last_sweep(run) / "summary.json").write_text(json.dumps(s, indent=1), encoding="utf-8")
                return res
        mod.Runner = HookRunner
        saved = {k: os.environ.get(k) for k in ("EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR")}
        os.environ.update(EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(store))
        try:
            a = mod.build_parser().parse_args(["run", "--run", str(run)])
            expect(a.fn(a), 0, f"{case}/{tag}: run exit")
        finally:
            mod.Runner = base_runner
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        return ledger_entries(load(run / "ledger.json") or {}), cand["armId"], run

    def finalized_probes(run, aid):
        expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}: finalize exit")
        arms = field(load(run / "disposition.json") or {}, "arms") or {}
        return ((arms.get(aid) or {}).get("orderEvaluation") or {}).get("probes")

    led, aid, run = oe_run("clean", damage=False)
    expect(execution_of(led.get(f"{nav}__{aid}__diff")), "completed", f"{case}/clean: premise - the diff is accepted")
    expect(execution_of(led.get(f"{order}__{aid}")), "completed", f"{case}/clean: {order} derives from accepted diffs")
    expect(finalized_probes(run, aid), ["navownprobe"], f"{case}/clean: the disposition's order evaluation names the probe")
    led, aid, run = oe_run("no-verdict", damage=True)
    expect(execution_of(led.get(f"{nav}__{aid}__diff")), "rejected(schema-unsupported)",
           f"{case}/no-verdict: premise - the diff part is rejected")
    e = led.get(f"{order}__{aid}") or {}
    expect(execution_of(e), f"blocked(prerequisite:{nav})",
           f"{case}/no-verdict: {order} is blocked by the rejected diff, not derived over it", "§4.6")
    rc = load(run / "receipts" / f"{order}__{aid}.json") or {}
    check(not (rc.get("validation") or {}).get("accepted"),
          f"{case}/no-verdict: the order evaluation's receipt is not accepted")
    expect(finalized_probes(run, aid), [],
           f"{case}/no-verdict: the disposition's order evaluation reads nothing from the rejected diff")


CASES["case-29"] = case_29


# ------------------------------------------------------ cases 30-36: sweep evidence per invocation
# Review V2's H2, M1, M2, M3, M4 and the reused-diff defect (3b), plus the verified-reuse guard. The assertions
# read what the controller records (ledger statuses, receipt exit codes, output hashes, finalize's integrity,
# the disposition, and how many children it launched), not where RUN/sweep keeps its files. A check that
# holds only for the per-invocation folder layout says `(layout)`.

TERMINAL = ("completed", "reused")

REFUSE_ORDER_PROBE = r'''
"""The ordinary fake PROBE, except that it refuses the order file whose stem is REFUSE_STEM (exit 4)."""
import sys
from pathlib import Path
sys.path.insert(0, FIXT_DIR)
from fakeprobe import main
argv = sys.argv[1:]
order = argv[argv.index("--order") + 1] if "--order" in argv else ""
if Path(order).stem == REFUSE_STEM:
    print("REFUSED: fixture - this order's masters do not resolve")
    raise SystemExit(4)
raise SystemExit(main(PROBE))
'''


def sweep_folders(run: Path) -> list[Path]:
    """The folders an ordersweep launch writes into, oldest first: RUN/sweep/inv-* when any exist, else
    RUN/sweep itself (the shared folder)."""
    s = Path(run) / "sweep"
    inv = sorted(p for p in s.glob("inv-*") if p.is_dir()) if s.is_dir() else []
    return inv or [s]


def text_lines(p: Path) -> list[str]:
    return Path(p).read_text(encoding="utf-8", errors="replace").splitlines() if Path(p).exists() else []


def cache_log_snapshot(run: Path) -> dict:
    return {str(f): len(text_lines(f / "cache-log.txt")) for f in sweep_folders(run)}


def cache_log_added(run: Path, snap: dict) -> list[str]:
    """The cache-log lines written since `snap`, in either layout: the new lines of a folder that existed, and
    every line of a folder that did not."""
    out = []
    for f in sweep_folders(run):
        out += text_lines(f / "cache-log.txt")[snap.get(str(f), 0):]
    return out


def receipt_paths(rc: dict) -> list[str]:
    """Every path a receipt names as evidence or as an input it was measured against."""
    c = rc.get("consumed") or {}
    out = [o.get("path") for o in rc.get("outputs") or []] + [rc.get("log"), c.get("baseEmitPath")]
    out += [i.get("path") for i in c.get("inputs") or []]
    return [p for p in out if p]


def inside(run: Path, rel: str) -> bool:
    p = Path(rel)
    p = p if p.is_absolute() else Path(run) / p
    try:
        p.resolve().relative_to(Path(run).resolve())
        return True
    except ValueError:
        return False


def kill_recorded(pidfile: Path) -> list:
    """Protects the machine ONLY: kill whatever a slow fake recorded and is still alive."""
    left = []
    for name, pid in _pids(pidfile).items():
        if _pid_alive(pid):
            left.append((name, pid))
            if os.name == "nt":
                subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True)
            else:
                os.kill(pid, 9)
    return left


class SweepInProc:
    """case_27's in-process technique, shared: a private copy of the evalrun module whose Runner counts every
    child it is handed, records the RUN/candidates listing at each ordersweep launch (before the child starts),
    and lets a case wrap the ordersweep job. The probes, ordersweep.py and conflictdiff stay real."""

    def __init__(self, tmp: Path, case: str, fixture: str = "case-08-interrupt-resume"):
        import importlib.util
        spec = importlib.util.spec_from_file_location("evalrun_" + case.replace("-", "_"), EVALRUN)
        self.mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.mod)
        self.case, self.fixture = case, fixture
        self.scen = case_paths(fixture)[3]
        self.store = tmp / f"{case}-runlog"
        self.launches: list = []           # one RUN/candidates listing per ordersweep launch
        self.jobs: list[str] = []          # every job id handed to a Runner
        self.hook = None
        base_runner = self.mod.Runner
        outer = self

        class HookRunner(base_runner):
            def run(self, jobs, echo=True):
                outer.jobs += [str(j.get("id")) for j in jobs]
                if jobs and jobs[0].get("id") == "ordersweep":
                    argv = list(jobs[0].get("argv") or [])
                    cdir = Path(argv[argv.index("--candidates") + 1]) if "--candidates" in argv else None
                    outer.launches.append(sorted(p.name for p in cdir.iterdir())
                                          if cdir is not None and cdir.is_dir() else [])
                    if outer.hook is not None:
                        return outer.hook(lambda js: base_runner.run(self, js, echo), jobs)
                return base_runner.run(self, jobs, echo)
        self.mod.Runner = HookRunner

    def plan(self, run: Path, extra=(), fixture: str | None = None):
        fx = fixture or self.fixture
        p = subprocess.run([sys.executable, str(EVALRUN), "plan", "--registry",
                            str(GEN / "registry" / "fixture-check-registry.json"),
                            "--scope", str(GEN / "cases" / fx / "scope.json"),
                            "--candidates", str(GEN / "cases" / fx / "candidates"),
                            "--out", str(run), "--harness", str(REAL_HARNESS)] + probe_cmds()
                           + [str(x) for x in extra],
                           capture_output=True, text=True, timeout=300,
                           env=dict(os.environ, EVALRUN_FAKE_SCENARIO=str(self.scen),
                                    HOUSECARL_RUNLOG_DIR=str(self.store)))
        expect(p.returncode, 0, f"{self.case}: plan exit for {run.name}")
        if p.returncode != 0:
            print(p.stdout[-2000:], p.stderr[-2000:])
            return None
        return load(run / "plan.json") or {}

    def inproc(self, args, hook=None, scenario=None, env=None):
        self.hook = hook
        names = ["EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR"] + list(env or {})
        saved = {k: os.environ.get(k) for k in names}
        os.environ.update(EVALRUN_FAKE_SCENARIO=str(scenario or self.scen), HOUSECARL_RUNLOG_DIR=str(self.store),
                          **{k: str(v) for k, v in (env or {}).items()})
        try:
            a = self.mod.build_parser().parse_args([str(x) for x in args])
            return a.fn(a)
        finally:
            self.hook = None
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v

    def scenario_with(self, path: Path, probe: str, stem: str, entry: dict) -> Path:
        doc = load(self.scen)
        doc["arms"].setdefault(probe, {})[stem] = entry
        path.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        return path


def use_conflictdiff(tmp: Path) -> None:
    """Choose this run's conflictdiff, once: the tree HOUSECARL_EVALRUN_TEST_HARNESS names, used as given, or else a
    build of this checkout's conflictdiff into `tmp`. The build's output folder starts empty, so a failed build
    cannot leave an older binary to be tested. Raises SystemExit with one sentence when the build fails."""
    global REAL_HARNESS, DIFF_EXE, DIFF_SOURCE
    if DIFF_SOURCE is not None:
        return
    explicit = os.environ.get("HOUSECARL_EVALRUN_TEST_HARNESS")
    if explicit:
        REAL_HARNESS = Path(explicit)
        DIFF_EXE = REAL_HARNESS / "conflictdiff" / "bin" / "Release" / "net9.0" / DIFF_NAME
        DIFF_SOURCE = (f"  conflictdiff source: explicit harness HOUSECARL_EVALRUN_TEST_HARNESS={explicit} "
                       f"(not rebuilt from this checkout's source)")
        return
    csproj = HARNESS / "conflictdiff" / "conflictdiff.csproj"
    build = Path(tmp) / "conflictdiff-build"
    harness = build / "harness"
    out = harness / "conflictdiff" / "bin" / "Release" / "net9.0"
    cmd = ["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
           "--artifacts-path", str(build / "artifacts"), "-o", str(out)]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
        why = None if r.returncode == 0 else f"dotnet exit {r.returncode}"
        log = r.stdout + r.stderr
    except FileNotFoundError:
        why, log = "dotnet not found", ""
    if why is None and not ((out / DIFF_NAME).exists() and (out / "conflictdiff.dll").exists()):
        why = f"dotnet exit 0 but no conflictdiff in {out}"
    if why:
        tail = "\n".join(log.strip().splitlines()[-15:])
        raise SystemExit((tail + "\n" if tail else "") +
                         f"  FAIL  conflictdiff did not build from {csproj} ({why}); the suite will not test an "
                         f"older binary - fix the build or set HOUSECARL_EVALRUN_TEST_HARNESS to the tree to test.")
    REAL_HARNESS, DIFF_EXE = harness, out / DIFF_NAME
    DIFF_SOURCE = f"  conflictdiff source: built from {csproj} into {out} for this run"


def diff_exe_identity() -> str:
    """The conflictdiff build a run compared with: path, sha256 and mtime of the exe and of its dll (the code)."""
    out = []
    for p in (DIFF_EXE, DIFF_EXE.with_suffix(".dll")):
        if p.exists():
            sha = hashlib.sha256(p.read_bytes()).hexdigest()
            when = time.strftime("%Y-%m-%d %H:%M:%S %z", time.localtime(p.stat().st_mtime))
            out.append(f"  conflictdiff: {p}  sha256 {sha}  mtime {when}")
        else:
            out.append(f"  conflictdiff: {p}  (absent)")
    return "\n".join(out)


def first_candidate(plan: dict) -> dict:
    return next((a for a in (plan or {}).get("arms") or [] if a.get("kind") == "candidate"), {})


def case_30(tmp):
    """Review V2 H2. An earlier invocation's sweep left the candidate's emit; the next invocation's candidate job
    exits 0 WITHOUT writing and nothing in the cache serves it. Nothing of that arm is credited: not the arm, not
    the reverse or diff that would rest on the earlier file, not the order evaluation, and no accepted diff receipt
    carries a classification for it. The control: the same sequence with a probe that writes credits all of it."""
    case = "case-30-no-emit-credits-nothing"
    nav, order = "geometry.navmesh-ownership", "patch.order-evaluation"

    class Killed(Exception):
        """The controller dies after its sweep child finished, before the split wrote a receipt."""

    def kill_after(real, jobs):
        real(jobs)
        raise Killed()

    def sequence(tag, writes):
        h = SweepInProc(tmp, f"{case}-{tag}")
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", f"navownprobe,{order}"])
        if pl is None:
            return None
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        oe_row = next((r for c in pl.get("checks") or [] if c["checkId"] == order
                       for r in c["arms"] if r["armId"] == cid), {})
        expect(oe_row.get("status"), "planned", f"{case}/{tag}: premise - {order} is planned on the candidate")
        try:
            h.inproc(["run", "--run", run], kill_after)
            check(False, f"{case}/{tag}: premise - the first invocation was killed after its sweep child")
        except Killed:
            pass
        check(any((f / "navownprobe" / f"{stem}.json").exists() for f in sweep_folders(run)),
              f"{case}/{tag}: premise - the earlier invocation left the candidate's emit")
        for p in (run / "sweep").rglob("cache.json"):
            p.unlink()                                  # the cache is lost: nothing can serve the candidate job
        old = time.time() - 3600
        for p in (run / "sweep").rglob("*"):
            if p.is_file():
                os.utime(p, (old, old))                 # the earlier invocation was an hour ago
        scen = h.scen if writes else h.scenario_with(tmp / f"{case}-{tag}.scenario.json", "navownprobe", stem,
                                                     {"behaviour": "no-emit"})
        snap, n = cache_log_snapshot(run), len(h.launches)
        expect(h.inproc(["resume", "--run", run], scenario=scen), 0, f"{case}/{tag}: resume exits 0")
        expect(len(h.launches), n + 1, f"{case}/{tag}: premise - the resume launched one sweep")
        added = cache_log_added(run, snap)
        check(not any(ln.startswith(f"REUSED navownprobe-{stem}:") for ln in added),
              f"{case}/{tag}: premise - no cache served the candidate job (added: {added})")
        return run, cid, stem

    got = sequence("no-emit", writes=False)
    if got is None:
        return
    run, cid, stem = got
    arm_log = sweep_folders(run)[-1] / "logs" / f"navownprobe-{stem}.txt"
    check("without writing the emit" in "\n".join(text_lines(arm_log))
          and any(ln.startswith("(exit 0,") for ln in text_lines(arm_log)),
          f"{case}/no-emit: premise - this launch's candidate job exited 0 writing nothing")
    led = ledger_entries(load(run / "ledger.json") or {})
    for part in ("arm", "reverse", "diff"):
        st = execution_of(led.get(f"{nav}__{cid}__{part}"))
        check(st not in TERMINAL, f"{case}/no-emit: the candidate's {part} part is not credited (got {st})")
    st = execution_of(led.get(f"{order}__{cid}"))
    check(st not in TERMINAL, f"{case}/no-emit: {order} is not completed over the unmeasured arm (got {st})")
    rc = load(run / "receipts" / f"{nav}__{cid}__diff.json") or {}
    cls = ((rc.get("orderEvaluation") or {}).get("classification"))
    check(not ((rc.get("validation") or {}).get("accepted") and cls),
          f"{case}/no-emit: no accepted diff receipt carries a classification for the arm (got {cls!r})")

    got = sequence("control", writes=True)
    if got is None:
        return
    run, cid, stem = got
    led = ledger_entries(load(run / "ledger.json") or {})
    for part in ("arm", "reverse", "diff"):
        st = execution_of(led.get(f"{nav}__{cid}__{part}"))
        check(st in TERMINAL, f"{case}/control: a candidate job that writes its emit credits the {part} part (got {st})")
    st = execution_of(led.get(f"{order}__{cid}"))
    check(st in TERMINAL, f"{case}/control: {order} derives from the measured arm (got {st})")


def case_31(tmp):
    """Review V2 M2. A copied RUN folder credits only evidence inside the copy. The original run is complete and
    copied; the ORIGINAL's candidate emit then changes (a created row that moves the diff's counts), and the copy
    loses its candidate receipts, so its resume launches the sweep. The copy's diff must compare the copy's own
    emits: its counts equal the original run's before the change, its diff log names no file of the original,
    and every path its receipts name resolves inside the copy."""
    import importlib.util
    case = "case-31-copied-run-own-evidence"
    nav = "geometry.navmesh-ownership"
    h = SweepInProc(tmp, case)
    orig, copy = tmp / f"{case}-original", tmp / f"{case}-copied"
    pl = h.plan(orig, ["--select", "navownprobe"])
    if pl is None:
        return
    cand = first_candidate(pl)
    cid = cand["armId"]
    expect(h.inproc(["run", "--run", orig]), 0, f"{case}: premise - the original run exits 0")
    led = ledger_entries(load(orig / "ledger.json") or {})
    for part in ("arm", "reverse", "diff"):
        expect(execution_of(led.get(f"{nav}__{cid}__{part}")), "completed",
               f"{case}: premise - the original run completed the candidate's {part}")
    keys = {part: f"{nav}__{cid}__{part}" for part in ("arm", "reverse", "diff")}
    want = (load(orig / "receipts" / f"{keys['diff']}.json") or {}).get("orderEvaluation") or {}
    want_counts, want_created = (want.get("diff") or {}).get("counts"), want.get("created")
    check(want_counts is not None and want_created is not None,
          f"{case}: premise - the original diff receipt records counts and created rows")

    shutil.copytree(orig, copy)

    # the ORIGINAL's candidate emit changes afterwards: one more conflict row, holding
    arm_rc = load(orig / "receipts" / f"{keys['arm']}.json") or {}
    emit = orig / arm_rc["outputs"][0]["path"]
    base_emit = orig / (arm_rc.get("consumed") or {}).get("baseEmitPath", "")
    doc = json.loads(emit.read_text(encoding="utf-8"))
    extra = json.loads(json.dumps(doc["Conflicts"][0]))
    extra["Necessary"][0]["FormKey"] = "0A00FF:FixtureBase.esp"
    extra["Summary"] = "a row the original gained after it was copied"
    doc["Conflicts"].append(extra)
    emit.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    spec = importlib.util.spec_from_file_location("ordersweep_case31", HARNESS / "ordersweep.py")
    osw = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(osw)
    d = subprocess.run([str(DIFF_EXE), str(base_emit), str(emit)], capture_output=True, text=True, timeout=120)
    changed = osw.parse_diff(d.stdout + d.stderr).get("counts")
    check(changed != want_counts,
          f"{case}: premise - the change moves the diff's counts (before {want_counts}, after {changed})")

    for k in keys.values():
        (copy / "receipts" / f"{k}.json").unlink()
    n = len(h.launches)
    expect(h.inproc(["resume", "--run", copy]), 0, f"{case}: the copy's resume exits 0")
    expect(len(h.launches), n + 1, f"{case}: premise - the copy's resume launched the sweep")
    led = ledger_entries(load(copy / "ledger.json") or {})
    for part, k in keys.items():
        st = execution_of(led.get(k))
        check(st in TERMINAL, f"{case}: premise - the copy credits the candidate's {part} (got {st})")

    outside = []
    for rp in sorted((copy / "receipts").glob("*.json")):
        for p in receipt_paths(load(rp) or {}):
            if not inside(copy, p):
                outside.append(f"{rp.name}: {p}")
    check(not outside, f"{case}: every path the copy's receipts name resolves inside the copy (outside: {outside})")
    rc = load(copy / "receipts" / f"{keys['diff']}.json") or {}
    got = rc.get("orderEvaluation") or {}
    expect((got.get("diff") or {}).get("counts"), want_counts,
           f"{case}: the copy's diff counts are those of the copy's own emits")
    expect(got.get("created"), want_created, f"{case}: the copy's created rows are those of the copy's own emits")
    dlog = copy / ((rc.get("outputs") or [{}])[0].get("path") or "missing-diff-log")
    # the Runner's command line and conflictdiff's own `base :` / `changed :` lines name the files it read
    read = [norm(ln).lower() for ln in text_lines(dlog) if ln.startswith(("$ ", "base ", "changed "))]
    check(dlog.exists() and bool(read) and not any(norm(orig).lower() in ln for ln in read),
          f"{case}: the diff log the copy's receipt names compared no file of the original run ({read[:1]})")

    def emits(doc):
        if isinstance(doc, dict):
            for k, v in doc.items():
                if k == "emit" and isinstance(v, str):
                    yield v
                else:
                    yield from emits(v)
        elif isinstance(doc, list):
            for v in doc:
                yield from emits(v)
    caches = list((copy / "sweep").rglob("cache.json"))
    absolute = [f"{cj.relative_to(copy)}: {e}" for cj in caches for e in emits(load(cj)) if Path(e).is_absolute()]
    check(bool(caches) and not absolute,
          f"{case}: (layout) no cache entry in the copy names an absolute path ({absolute[:3]})")


def case_32(tmp):
    """Review V2 M3. (a) A run whose candidate reverse job failed accepted the arm and not the reverse; `resume`
    with a healthy reverse repairs it: the sweep is launched, the reverse and the diff resting on it are credited,
    and the accepted arm is reused with its receipt untouched. (b) An arm kept out of the sweep by its
    prerequisite (its masters-and-order check really refused) does not relaunch ordersweep on every unchanged
    `run`: it stays blocked(prerequisite:...) without a child."""
    case = "case-32-resume-repairs-partial-sweep"
    nav, pre = "geometry.navmesh-ownership", "loadability.masters-and-order"

    # (a) --------------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-a")
    run = tmp / f"{case}-reverse-failed"
    pl = h.plan(run, ["--select", "navownprobe"])
    if pl is None:
        return
    cand = first_candidate(pl)
    cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
    scen = h.scenario_with(tmp / f"{case}-a.scenario.json", "navownprobe", f"{stem}.reverse",
                           {"behaviour": "refuse", "code": 5})
    expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}/a: run 1 exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    k_arm, k_rev, k_diff = (f"{nav}__{cid}__{p}" for p in ("arm", "reverse", "diff"))
    expect(execution_of(led.get(k_arm)), "completed", f"{case}/a: premise - run 1 accepted the candidate arm")
    check(execution_of(led.get(k_rev)) not in TERMINAL,
          f"{case}/a: premise - run 1 did not accept the reverse (got {execution_of(led.get(k_rev))})")
    arm_receipt = run / "receipts" / f"{k_arm}.json"
    arm_sha = sha_of(arm_receipt) if arm_receipt.exists() else None
    n = len(h.launches)
    expect(h.inproc(["resume", "--run", run]), 0, f"{case}/a: the resume exits 0")
    expect(len(h.launches), n + 1, f"{case}/a: the resume launches the sweep to repair the reverse")
    led = ledger_entries(load(run / "ledger.json") or {})
    st = execution_of(led.get(k_rev))
    check(st in TERMINAL, f"{case}/a: the reverse is credited from the resume (got {st})")
    st = execution_of(led.get(k_diff))
    check(st in TERMINAL, f"{case}/a: the diff is credited after the resume (got {st})")
    rev_rc = load(run / "receipts" / f"{k_rev}.json") or {}
    diff_rc = load(run / "receipts" / f"{k_diff}.json") or {}
    rev_sha = next((o.get("sha256") for o in rev_rc.get("outputs") or []), None)
    diff_rev = next((i.get("sha256") for i in (diff_rc.get("consumed") or {}).get("inputs") or []
                     if i.get("part") == "reverse"), None)
    check(rev_sha is not None and diff_rev == rev_sha,
          f"{case}/a: the diff rests on the reverse the resume measured (diff input {diff_rev}, reverse {rev_sha})")
    expect(execution_of(led.get(k_arm)), "reused", f"{case}/a: the accepted arm is reused, not re-measured")
    check(arm_sha is not None and arm_receipt.exists() and sha_of(arm_receipt) == arm_sha,
          f"{case}/a: the accepted arm's receipt is unchanged")

    # (b) --------------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-b")
    run = tmp / f"{case}-kept-out"
    cstem = next(f.stem for f in sorted((GEN / "cases" / h.fixture / "candidates").glob("candidate-*.json"))
                 if not f.stem.endswith("baseline"))
    wrapper = tmp / f"{case}-scopeprobe-refuses.py"
    wrapper.write_text(f"FIXT_DIR = {str(FIXT)!r}\nPROBE = 'scopeprobe'\nREFUSE_STEM = {cstem!r}\n"
                       + REFUSE_ORDER_PROBE, encoding="utf-8")
    pl = h.plan(run, ["--select", "navownprobe", "--probe-cmd", f"scopeprobe=python {wrapper}"])
    if pl is None:
        return
    cid = first_candidate(pl)["armId"]
    expect(h.inproc(["run", "--run", run]), 0, f"{case}/b: run 1 exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    check(execution_of(led.get(f"{pre}__{cid}")) not in TERMINAL and
          execution_of(led.get(f"{pre}__baseline")) in TERMINAL,
          f"{case}/b: premise - the candidate's {pre} was refused and the baseline's accepted "
          f"(got {execution_of(led.get(f'{pre}__{cid}'))}, {execution_of(led.get(f'{pre}__baseline'))})")
    expect(execution_of(led.get(f"{nav}__baseline__arm")), "completed",
           f"{case}/b: premise - run 1 measured the baseline")
    n = len(h.launches)
    expect(n, 1, f"{case}/b: premise - run 1 launched one sweep, for the baseline")
    for i in (2, 3):
        expect(h.inproc(["run", "--run", run]), 0, f"{case}/b: unchanged run {i} exits 0")
        expect(len(h.launches), n, f"{case}/b: unchanged run {i} launches no ordersweep child")
        led = ledger_entries(load(run / "ledger.json") or {})
        st = str(execution_of(led.get(f"{nav}__{cid}")))
        check(st.startswith(f"blocked(prerequisite:{pre}"), f"{case}/b: run {i} keeps the arm blocked (got {st})")


def case_33(tmp):
    """Review V2 M4. A sweep job's own exit code and cause survive, on the job's FIRST execution in a fresh RUN.
    (a) the candidate's reverse job refuses with exit 5 and writes nothing: the reverse part is rejected (not
    blocked(no-reverse-measurement)) and its receipt's exitCode is 5; (b) the reverse exits 0 writing nothing:
    rejected(output-missing), exitCode 0; (c) the candidate's ARM job refuses with exit 5: its receipt's exitCode
    is the job's 5, not ordersweep's; (d) the same for the baseline's arm job."""
    case = "case-33-sweep-job-exit-and-cause"
    nav = "geometry.navmesh-ownership"

    def fresh(tag, stem_of, entry):
        h = SweepInProc(tmp, f"{case}-{tag}")
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", "navownprobe"])
        if pl is None:
            return None, None
        cand = first_candidate(pl)
        stem = Path(cand["candidateFile"]).stem
        scen = h.scenario_with(tmp / f"{case}-{tag}.scenario.json", "navownprobe", stem_of(stem), entry)
        expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}/{tag}: the run exits 0")
        expect(len(h.launches), 1, f"{case}/{tag}: premise - one sweep was launched")
        check(not (run / "sweep").exists() or not any(
            ln.startswith("REUSED ") for f in sweep_folders(run) for ln in text_lines(f / "cache-log.txt")),
              f"{case}/{tag}: premise - a fresh RUN: nothing was served from a cache")
        return run, cand["armId"]

    def causes(e):
        return [r.get("cause") for r in ((e or {}).get("rejections") or ((e or {}).get("validation") or {})
                                         .get("rejections") or [])] + [(e or {}).get("cause")]

    # (a)
    run, cid = fresh("reverse-refuses", lambda s: f"{s}.reverse", {"behaviour": "refuse", "code": 5})
    if run is not None:
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__arm")), "completed", f"{case}/a: premise - the arm was accepted")
        e = led.get(f"{nav}__{cid}__reverse")
        st = str(execution_of(e))
        rc = load(run / "receipts" / f"{nav}__{cid}__reverse.json") or {}
        rc_causes = [r.get("cause") for r in (rc.get("validation") or {}).get("rejections") or []]
        check(st.startswith("rejected("), f"{case}/a: a reverse job that failed is rejected, not blocked (got {st})")
        check(any(c in ("exit-nonzero", "refused") for c in causes(e) + rc_causes),
              f"{case}/a: the reverse part names the job's own cause (ledger {causes(e)}, receipt {rc_causes})")
        expect(rc.get("exitCode"), 5, f"{case}/a: the reverse receipt carries the job's own exit code")

    # (b)
    run, cid = fresh("reverse-no-emit", lambda s: f"{s}.reverse", {"behaviour": "no-emit"})
    if run is not None:
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__arm")), "completed", f"{case}/b: premise - the arm was accepted")
        expect(execution_of(led.get(f"{nav}__{cid}__reverse")), "rejected(output-missing)",
               f"{case}/b: a reverse job that exited 0 writing nothing is rejected for its missing output")
        rc = load(run / "receipts" / f"{nav}__{cid}__reverse.json") or {}
        expect(rc.get("exitCode"), 0, f"{case}/b: the reverse receipt carries the job's own exit code")

    # (c)
    run, cid = fresh("arm-refuses", lambda s: s, {"behaviour": "refuse", "code": 5})
    if run is not None:
        led = ledger_entries(load(run / "ledger.json") or {})
        st = str(execution_of(led.get(f"{nav}__{cid}__arm")))
        check(st.startswith("rejected("), f"{case}/c: the failed candidate arm job is rejected (got {st})")
        rc = load(run / "receipts" / f"{nav}__{cid}__arm.json") or {}
        expect(rc.get("exitCode"), 5, f"{case}/c: the arm receipt carries the job's own exit code, not ordersweep's")

    # (d)
    run, cid = fresh("baseline-refuses", lambda s: "baseline", {"behaviour": "refuse", "code": 5})
    if run is not None:
        led = ledger_entries(load(run / "ledger.json") or {})
        st = str(execution_of(led.get(f"{nav}__baseline__arm")))
        check(st.startswith("rejected("), f"{case}/d: the failed baseline arm job is rejected (got {st})")
        rc = load(run / "receipts" / f"{nav}__baseline__arm.json") or {}
        expect(rc.get("exitCode"), 5, f"{case}/d: the baseline receipt carries the job's own exit code, not ordersweep's")

    # (e), (f) a job that WROTE its emit and then exited non-zero, with no refusal marker: only its exit code says it
    # failed, so that code must reach the validator (a zero there accepts the emit)
    for tag, stem_of, part in (("e", lambda s: f"{s}.reverse", "reverse"), ("f", lambda s: s, "arm")):
        run, cid = fresh(f"{part}-writes-then-exits", stem_of, {"behaviour": "exit", "code": 4})
        if run is None:
            continue
        led = ledger_entries(load(run / "ledger.json") or {})
        rc = load(run / "receipts" / f"{nav}__{cid}__{part}.json") or {}
        check(any(o.get("sha256") for o in rc.get("outputs") or []),
              f"{case}/{tag}: premise - the {part} job wrote its emit before exiting 4")
        expect(execution_of(led.get(f"{nav}__{cid}__{part}")), "rejected(exit-nonzero)",
               f"{case}/{tag}: a {part} job that wrote its emit and exited 4 is rejected for its exit code")
        expect(rc.get("exitCode"), 4, f"{case}/{tag}: the {part} receipt carries the job's own exit code")


def case_34(tmp):
    """Review V2 3b. A later invocation's sweep never rewrites the evidence of a diff part it keeps as reused. Run 1
    accepts navownprobe's candidate diff while overlapprobe's candidate job fails, so the resume relaunches the
    sweep, which runs conflictdiff again through a pass-through wrapper that prints the time (a re-run's log can
    never be byte-identical by chance). The navownprobe diff part stays reused, the file its receipt names still
    hashes to the receipt, evidence_integrity reports nothing for it, and finalize exits 0."""
    case = "case-34-reused-diff-evidence-kept"
    nav, ovl = "geometry.navmesh-ownership", "geometry.object-overlap"
    h = SweepInProc(tmp, case)
    run = tmp / case
    wrapper = tmp / f"{case}-conflictdiff-wrapper.py"
    wrapper.write_text("import subprocess, sys, time\n"
                       f"r = subprocess.run([{str(DIFF_EXE)!r}] + sys.argv[1:])\n"
                       "print('fixture wrapper: finished at', time.time_ns(), flush=True)\n"
                       "sys.exit(r.returncode)\n", encoding="utf-8")
    pl = h.plan(run, ["--select", "navownprobe,overlapprobe", "--probe-cmd", f"conflictdiff=python {wrapper}"])
    if pl is None:
        return
    cand = first_candidate(pl)
    cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
    scen = h.scenario_with(tmp / f"{case}.scenario.json", "overlapprobe", stem, {"behaviour": "exit", "code": 4})
    expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}: run 1 exits 0")
    key = f"{nav}__{cid}__diff"
    led = ledger_entries(load(run / "ledger.json") or {})
    rc = load(run / "receipts" / f"{key}.json") or {}
    out = (rc.get("outputs") or [{}])[0]
    expect(execution_of(led.get(key)), "completed", f"{case}: premise - run 1 accepted the navownprobe diff")
    check(out.get("sha256") and (run / out.get("path", "-")).exists() and sha_of(run / out["path"]) == out["sha256"],
          f"{case}: premise - its receipt hashes the diff log it names")
    check(execution_of(led.get(f"{ovl}__{cid}__arm")) not in TERMINAL,
          f"{case}: premise - run 1 did not accept overlapprobe's candidate arm")
    n, t0 = len(h.launches), time.time_ns()
    expect(h.inproc(["resume", "--run", run]), 0, f"{case}: the resume exits 0")
    expect(len(h.launches), n + 1, f"{case}: premise - the resume relaunched the sweep")
    stamps = [int(ln.rsplit(" ", 1)[1]) for ln in
              text_lines(sweep_folders(run)[-1] / "logs" / f"diff-navownprobe-{stem}.txt")
              if ln.startswith("fixture wrapper: finished at ")]
    check(any(s > t0 for s in stamps),
          f"{case}: premise - the relaunched sweep ran navownprobe's conflictdiff again (wrapper stamps {stamps})")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(key)), "reused", f"{case}: the navownprobe diff part is reused")
    rc2 = load(run / "receipts" / f"{key}.json") or {}
    out2 = (rc2.get("outputs") or [{}])[0]
    p = run / (out2.get("path") or "-")
    check(out2.get("sha256") and p.exists() and sha_of(p) == out2["sha256"],
          f"{case}: the file the reused diff receipt names still hashes to the receipt's sha256")
    plan = load(run / "plan.json")
    integ, bad = h.mod.evidence_integrity(run, plan, h.mod.Ledger(run, plan), h.mod.read_receipts(run))
    check(key not in bad, f"{case}: evidence_integrity reports no defect for the reused diff ({bad.get(key)})")
    fin = evalrun(["finalize", "--run", run], h.scen)
    disp = load(run / "disposition.json") or {}
    defects = [d for d in (field(disp, "integrity", default={}) or {}).get("defects") or []
               if d.get("key") == key]
    check(not any(d.get("kind") == "evidence-changed" for d in defects),
          f"{case}: finalize lists no evidence-changed defect for the reused diff ({defects})")
    expect(fin.returncode, 0, f"{case}: finalize exits 0")


def case_35(tmp):
    """Verified reuse is preserved (a guard; the defects above must not be fixed by re-measuring everything).
    (a) `run` twice on an unchanged RUN: the second launches no child at all, every sweep part reads reused, and
    no new launch folder appears. (b) A sweep stopped at the deadline after its baseline and candidate arm jobs
    finished and were cached; `resume` credits those jobs from that cache, and the resuming launch's cache log
    says REUSED for them."""
    case = "case-35-verified-reuse-kept"
    nav = "geometry.navmesh-ownership"

    # (a) --------------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-a")
    run = tmp / f"{case}-twice"
    pl = h.plan(run, ["--select", "navownprobe"])
    if pl is None:
        return
    cid = first_candidate(pl)["armId"]
    expect(h.inproc(["run", "--run", run]), 0, f"{case}/a: run 1 exits 0")
    expect(len(h.launches), 1, f"{case}/a: premise - run 1 launched the sweep")
    folders, n_jobs = [str(f) for f in sweep_folders(run)], len(h.jobs)
    expect(h.inproc(["run", "--run", run]), 0, f"{case}/a: run 2 exits 0")
    expect(len(h.launches), 1, f"{case}/a: run 2 launches no ordersweep child")
    expect(h.jobs[n_jobs:], [], f"{case}/a: run 2 launches no child at all")
    led = ledger_entries(load(run / "ledger.json") or {})
    parts = {k: execution_of(v) for k, v in led.items() if k.startswith(f"{nav}__")}
    check(len(parts) == 4 and all(v == "reused" for v in parts.values()),
          f"{case}/a: every sweep part reads reused ({parts})")
    expect([str(f) for f in sweep_folders(run)], folders, f"{case}/a: (layout) no new launch folder appears")

    # (b) --------------------------------------------------------------------------------------------------
    run = tmp / f"{case}-stopped"
    slow = tmp / f"{case}-slowprobe.py"
    slow.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_SLOWPROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    _, _, _, scen = case_paths("case-08-interrupt-resume")
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(tmp / f"{case}-b-runlog"))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / "case-08-interrupt-resume" / "scope.json",
            "--candidates", GEN / "cases" / "case-08-interrupt-resume" / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", "0.3", "--probe-cmd", f"navownprobe=python {slow} navownprobe"])
    expect(p.returncode, 0, f"{case}/b: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
        return
    cand = first_candidate(load(run / "plan.json") or {})
    cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
    try:
        r1 = ev(["run", "--run", run], {"EVALRUN_SLOW_ARM": f"{stem}.reverse", "EVALRUN_SLOW_PIDFILE": str(pidfile)},
                timeout=150)
    finally:
        left = kill_recorded(pidfile)
    check(not left, f"{case}/b: premise - the stopped run left no recorded process alive (killed {left})")
    expect(r1.returncode, 0, f"{case}/b: premise - the stopped run exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{nav}__{cid}__reverse")), "stopped(deadline)",
           f"{case}/b: premise - the sweep was stopped at the deadline in the candidate's reverse job")
    recorded = [ln for f in sweep_folders(run) for ln in text_lines(f / "cache-log.txt") if ln.startswith("RECORDED ")]
    for job in ("navownprobe-baseline", f"navownprobe-{stem}"):
        check(any(ln.startswith(f"RECORDED {job}:") for ln in recorded),
              f"{case}/b: premise - the stopped launch cached {job} (recorded: {recorded})")
    snap = cache_log_snapshot(run)
    r2 = ev(["resume", "--run", run], timeout=180)
    expect(r2.returncode, 0, f"{case}/b: resume exits 0")
    added = cache_log_added(run, snap)
    led = ledger_entries(load(run / "ledger.json") or {})
    for job, key in (("navownprobe-baseline", f"{nav}__baseline__arm"), (f"navownprobe-{stem}", f"{nav}__{cid}__arm")):
        check(any(ln.startswith(f"REUSED {job}:") for ln in added),
              f"{case}/b: the resuming launch's cache log says REUSED for {job} (added: {added})")
        st = execution_of(led.get(key))
        check(st in TERMINAL, f"{case}/b: {key.split('__', 1)[1]} served from the earlier launch's cache is credited "
                              f"(got {st})")
    for part in ("reverse", "diff"):
        st = execution_of(led.get(f"{nav}__{cid}__{part}"))
        check(st in TERMINAL, f"{case}/b: the resume completes the candidate's {part} (got {st})")
    # The bundle carries a sweep receipt's log at its own RUN-relative path: two launches write logs of the same name
    # (every launch's cache-log.txt names the jobs it reused), and a flattened logs/<name> copy kept the first and
    # attributed it to both.
    expect(ev(["finalize", "--run", run]).returncode, 0, f"{case}/b: premise - finalize exits 0")
    expect(ev(["bundle", "--run", run]).returncode, 0, f"{case}/b: premise - bundle exits 0")
    sweep_logs = sorted({(load(rp) or {}).get("log") for rp in (run / "receipts").glob("*.json")
                         if str((load(rp) or {}).get("log") or "").startswith("sweep/")})
    check(len(sweep_logs) >= 2 and all((run / "bundle" / rel).is_file()
                                       and (run / "bundle" / rel).read_bytes() == (run / rel).read_bytes()
                                       for rel in sweep_logs),
          f"{case}/b: every sweep receipt's log is in the bundle at its own path, byte for byte ({sweep_logs})")
    flat = sorted(p.name for p in (run / "bundle" / "logs").glob("*")
                  if any(Path(rel).name == p.name for rel in sweep_logs)) if (run / "bundle" / "logs").is_dir() else []
    check(not flat, f"{case}/b: no sweep log is flattened into the bundle's logs/ ({flat})")


def case_36(tmp):
    """Review V2 M1. `--max-candidates` bounds the sweep: an arm held back is never handed to ordersweep (its order
    is not in RUN/candidates when the child starts), never `running`, its sweep parts are skipped-budget, its
    required sweep checks are under evaluation.missing, and unassessed.candidatesHeldBack names it - on a sweep
    that runs to completion and on one stopped at the deadline."""
    case = "case-36-held-back-arm-not-measured"
    nav = "geometry.navmesh-ownership"
    fixture = "case-04-blocked-and-budget"
    common = ["--max-candidates", "1", "--exclude", "geometry.placement-contact=not-relevant-to-this-review",
              "--select", "navownprobe"]

    def assert_held_back(tag, h, run, pl):
        held = [a for a in pl.get("arms") or [] if a.get("heldBack")]
        expect(len(held), 1, f"{case}/{tag}: premise - the plan holds one candidate back")
        if not held:
            return
        hid, hfile, hstem = held[0]["armId"], held[0]["candidateFile"], Path(held[0]["candidateFile"]).stem
        row = next((r for c in pl.get("checks") or [] if c["checkId"] == nav for r in c["arms"]
                    if r["armId"] == hid), {})
        expect(row.get("status"), "skipped-budget", f"{case}/{tag}: premise - its {nav} row is skipped-budget")
        expect(len(h.launches), 1, f"{case}/{tag}: premise - one ordersweep child was launched")
        check(bool(h.launches) and all(hfile not in listing for listing in h.launches),
              f"{case}/{tag}: the held-back order is not in RUN/candidates when ordersweep starts ({h.launches})")
        running = [r for r in journal_rows(run) if r.get("armId") == hid and str(r.get("to", "")) == "running"]
        check(not running, f"{case}/{tag}: no entry of the held-back arm is ever running ({running[:2]})")
        led = ledger_entries(load(run / "ledger.json") or {})
        parts = {k: execution_of(v) for k, v in led.items() if k.startswith(f"{nav}__{hid}")}
        check(bool(parts) and all(v == "skipped-budget" for v in parts.values()),
              f"{case}/{tag}: the held-back arm's sweep entries are skipped-budget ({parts})")
        accepted = [rp.name for rp in (run / "receipts").glob(f"{nav}__{hid}*.json")
                    if ((load(rp) or {}).get("validation") or {}).get("accepted")]
        check(not accepted, f"{case}/{tag}: no accepted receipt for the held-back arm's sweep parts ({accepted})")
        fin = evalrun(["finalize", "--run", run], h.scen)
        check(fin.returncode in (0,), f"{case}/{tag}: premise - finalize exits 0 (got {fin.returncode})")
        disp = load(run / "disposition.json") or {}
        missing = [m for m in field(disp, "missing", default=[]) or [] if isinstance(m, dict)]
        check(any(m.get("checkId") == nav and m.get("armId") == hid for m in missing),
              f"{case}/{tag}: {nav} on the held-back arm is under evaluation.missing")
        blob = json.dumps(field(disp, "unassessed", default={}))
        check("candidatesHeldBack" in blob and (hstem in blob or hid in blob),
              f"{case}/{tag}: unassessed.candidatesHeldBack names the held-back candidate")

    # not stopped ------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-unstopped", fixture=fixture)
    run = tmp / f"{case}-unstopped"
    pl = h.plan(run, common)
    if pl is None:
        return
    expect(h.inproc(["run", "--run", run]), 0, f"{case}/unstopped: the run exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    cand = next((a for a in pl.get("arms") or [] if a.get("kind") == "candidate" and not a.get("heldBack")), {})
    expect(execution_of(led.get(f"{nav}__{cand.get('armId')}__diff")), "completed",
           f"{case}/unstopped: premise - the sweep ran to completion on the candidate within the bound")
    assert_held_back("unstopped", h, run, pl)

    # stopped at the deadline ------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-stopped", fixture=fixture)
    run = tmp / f"{case}-stopped"
    slow = tmp / f"{case}-slowprobe.py"
    slow.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_SLOWPROBE, encoding="utf-8")
    pidfile = tmp / f"{case}.pids"
    pl = h.plan(run, common + ["--budget-minutes", "0.3", "--probe-cmd", f"navownprobe=python {slow} navownprobe"])
    if pl is None:
        return
    cand = next((a for a in pl.get("arms") or [] if a.get("kind") == "candidate" and not a.get("heldBack")), {})
    stem = Path(cand.get("candidateFile") or "x.json").stem
    try:
        rc = h.inproc(["run", "--run", run],
                      env={"EVALRUN_SLOW_ARM": f"{stem}.reverse", "EVALRUN_SLOW_PIDFILE": str(pidfile)})
    finally:
        left = kill_recorded(pidfile)
    check(not left, f"{case}/stopped: premise - the stopped run left no recorded process alive (killed {left})")
    expect(rc, 0, f"{case}/stopped: the run exits 0")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{nav}__{cand.get('armId')}__reverse")), "stopped(deadline)",
           f"{case}/stopped: premise - the sweep was stopped at the deadline")
    assert_held_back("stopped", h, run, pl)


CASES["case-30"] = case_30
CASES["case-31"] = case_31
CASES["case-32"] = case_32
CASES["case-33"] = case_33
CASES["case-34"] = case_34
CASES["case-35"] = case_35
CASES["case-36"] = case_36


# ------------------------------------------------------ cases 37-38: review R1 of the per-invocation sweep

def jobs_of(folder: Path) -> dict:
    """One launch folder's job records (`jobs.jsonl`), by job id."""
    out = {}
    for ln in text_lines(Path(folder) / "jobs.jsonl"):
        if ln.strip():
            rec = json.loads(ln)
            out[rec["job"]] = rec
    return out


def drop_cache_entries(run: Path, label: str) -> int:
    """Remove every cache entry recorded for one job, in every launch folder; the other jobs stay servable. Returns
    how many were removed. (Deleting a whole cache.json re-measures the baseline too, whose bytes then differ from
    run to run by the fake probe's GeneratedUtc second - a timing-dependent change to what the arm rests on.)"""
    dropped = 0
    for cj in (Path(run) / "sweep").rglob("cache.json"):
        entries = load(cj) or {}
        kept = {k: v for k, v in entries.items() if (v or {}).get("label") != label}
        dropped += len(entries) - len(kept)
        cj.write_text(json.dumps(kept, indent=1), encoding="utf-8")
    return dropped


def case_37(tmp):
    """Review R1 H1. A candidate's sweep parts are credited only over an ACCEPTED baseline arm of the same check. The
    baseline navownprobe emit is written with exit 0 and rejected (extraction-failed; in the second variant, its
    identity); the candidate's probe is healthy. The candidate's arm, reverse and diff are not accepted - an otherwise
    acceptable part reads blocked(baseline-not-accepted), a part whose own validation fails may keep its own cause -
    the order evaluation is not completed, and the disposition carries no PASSES-COMPLETED-CHECKS for the candidate.
    The control: the same plan with a healthy baseline credits every candidate part and the order evaluation."""
    case = "case-37-baseline-not-accepted-credits-no-candidate"
    nav, order = "geometry.navmesh-ownership", "patch.order-evaluation"

    def sequence(tag, damage):
        h = SweepInProc(tmp, f"{case}-{tag}")
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", f"navownprobe,{order}"])
        if pl is None:
            return None
        cid = first_candidate(pl)["armId"]
        oe_row = next((r for c in pl.get("checks") or [] if c["checkId"] == order
                       for r in c["arms"] if r["armId"] == cid), {})
        expect(oe_row.get("status"), "planned", f"{case}/{tag}: premise - {order} is planned on the candidate")
        doc = load(h.scen)
        if damage == "extraction-failed":
            doc["arms"].setdefault("navownprobe", {})["baseline"] = {"behaviour": "extraction-failed"}
        elif damage == "identity":
            doc["identityDamage"] = list(doc.get("identityDamage") or []) + [{"emitStem": "baseline",
                                                                              "kind": "wrong-order"}]
        scen = tmp / f"{case}-{tag}.scenario.json"
        scen.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}/{tag}: the run exits 0")
        expect(len(h.launches), 1, f"{case}/{tag}: premise - one sweep was launched")
        return h, run, cid, scen

    got = sequence("control", None)
    if got is None:
        return
    h, run, cid, _ = got
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{nav}__baseline__arm")), "completed", f"{case}/control: premise - baseline accepted")
    for part in ("arm", "reverse", "diff"):
        expect(execution_of(led.get(f"{nav}__{cid}__{part}")), "completed",
               f"{case}/control: a healthy baseline credits the candidate's {part}")
    st = execution_of(led.get(f"{order}__{cid}"))
    check(st in TERMINAL, f"{case}/control: {order} derives over the accepted parts (got {st})")

    for tag, damage, want_base in (("extraction-failed", "extraction-failed", "rejected(extraction-failed)"),
                                   ("identity", "identity", "rejected(identity-mismatch)")):
        got = sequence(tag, damage)
        if got is None:
            continue
        h, run, cid, scen = got
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__baseline__arm")), want_base,
               f"{case}/{tag}: premise - the baseline arm emit was written and rejected")
        for part in ("arm", "reverse", "diff"):
            key = f"{nav}__{cid}__{part}"
            rc = load(run / "receipts" / f"{key}.json") or {}
            st = str(execution_of(led.get(key)))
            check((rc.get("validation") or {}).get("accepted") is not True and st not in TERMINAL,
                  f"{case}/{tag}: the candidate's {part} is not credited over the rejected baseline "
                  f"(status {st}, receipt accepted {(rc.get('validation') or {}).get('accepted')})")
            if part == "arm":
                expect(st, "blocked(baseline-not-accepted)",
                       f"{case}/{tag}: the healthy candidate arm reads blocked(baseline-not-accepted)")
            else:
                check(st == "blocked(baseline-not-accepted)" or st.startswith("rejected("),
                      f"{case}/{tag}: the {part} reads blocked(baseline-not-accepted) or its own rejection (got {st})")
        st = execution_of(led.get(f"{order}__{cid}"))
        check(st not in TERMINAL, f"{case}/{tag}: {order} is not completed over a baseline that was not accepted "
                                  f"(got {st})")
        evalrun(["finalize", "--run", run], scen)
        disp = load(run / "disposition.json") or {}
        check(bool(disp), f"{case}/{tag}: premise - finalize wrote the disposition")
        oe = (arm_block(disp, cid) or {}).get("orderEvaluation") or {}
        check(oe.get("classification") != "PASSES-COMPLETED-CHECKS",
              f"{case}/{tag}: the disposition's candidate orderEvaluation carries no PASSES-COMPLETED-CHECKS "
              f"(got {oe.get('classification')!r})")


def case_38(tmp):
    """Review R1 folds, each sub-part self-contained.
    (a) M1-S1: an emit an earlier launch cached, edited in place afterwards, is INVALIDATED by its recorded hash and
        re-measured by the resuming launch, never credited.
    (b) M1-S10: an accepted arm re-measured with DIFFERENT bytes gets a new receipt, and its reverse and diff are
        credited on that new emit, not on the earlier receipt's.
    (c) M2: an accepted arm whose job fails in a later launch stays reused; the reverse that could not run on it reads
        blocked(no-reverse-measurement) naming that job and its exit code, never completion-missing.
    (d) L2: an unchanged `run` after the candidate file changed launches no ordersweep child and no launch folder;
        the arm reads blocked(candidate-changed).
    (e) L4: a diff job recorded `stopped: before-launch` is skipped-budget, as the arm and reverse parts are."""
    import re
    case = "case-38-review-r1-folds"
    nav = "geometry.navmesh-ownership"
    fixture = "case-08-interrupt-resume"

    # (a) M1-S1 ---------------------------------------------------------------------------------------------
    run = tmp / f"{case}-a-edited-cached-emit"
    slow = tmp / f"{case}-slowprobe.py"
    slow.write_text(f"FIXT_DIR = {str(FIXT)!r}\n" + DEADLINE_SLOWPROBE, encoding="utf-8")
    pidfile = tmp / f"{case}-a.pids"
    _, _, _, scen = case_paths(fixture)
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(tmp / f"{case}-a-runlog"))

    def ev(args, extra_env=None, timeout=300):
        return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True,
                              text=True, env=dict(env, **(extra_env or {})), timeout=timeout)

    p = ev(["plan", "--registry", GEN / "registry" / "fixture-check-registry.json",
            "--scope", GEN / "cases" / fixture / "scope.json", "--candidates", GEN / "cases" / fixture / "candidates",
            "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
           + ["--select", "navownprobe", "--budget-minutes", "0.3", "--probe-cmd", f"navownprobe=python {slow} navownprobe"])
    expect(p.returncode, 0, f"{case}/a: plan exit")
    if p.returncode != 0:
        print(p.stdout[-2000:], p.stderr[-2000:])
    else:
        cand = first_candidate(load(run / "plan.json") or {})
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        try:
            r1 = ev(["run", "--run", run], {"EVALRUN_SLOW_ARM": f"{stem}.reverse", "EVALRUN_SLOW_PIDFILE": str(pidfile)},
                    timeout=150)
        finally:
            left = kill_recorded(pidfile)
        check(not left, f"{case}/a: premise - the stopped run left no recorded process alive (killed {left})")
        expect(r1.returncode, 0, f"{case}/a: premise - the stopped run exits 0")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__reverse")), "stopped(deadline)",
               f"{case}/a: premise - the sweep was stopped at the deadline in the candidate's reverse job")
        first = sweep_launches(run)
        check(len(first) == 1 and any(ln.startswith(f"RECORDED navownprobe-{stem}:")
                                      for ln in text_lines(first[-1] / "cache-log.txt")),
              f"{case}/a: premise - the stopped launch cached the candidate arm job")
        emit = (first[-1] if first else run / "sweep" / "inv-none") / "navownprobe" / f"{stem}.json"
        if emit.exists():
            doc = json.loads(emit.read_text(encoding="utf-8"))
            extra = json.loads(json.dumps(doc["Conflicts"][0]))
            extra["Necessary"][0]["FormKey"] = "0A00FF:FixtureBase.esp"
            extra["Summary"] = "a row written into the cached emit after its launch recorded it"
            doc["Conflicts"].append(extra)
            emit.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        edited = sha_of(emit) if emit.exists() else None
        check(edited is not None, f"{case}/a: premise - the cached candidate emit was edited in place (valid JSON)")
        snap = cache_log_snapshot(run)
        r2 = ev(["resume", "--run", run], timeout=180)
        expect(r2.returncode, 0, f"{case}/a: resume exits 0")
        new = [f for f in sweep_launches(run) if f not in first]
        expect(len(new), 1, f"{case}/a: premise - the resume launched one sweep")
        added = cache_log_added(run, snap)
        check(any(ln.startswith(f"INVALIDATED navownprobe-{stem}:") and "no longer hashes" in ln for ln in added),
              f"{case}/a: the resuming launch's cache log says INVALIDATED navownprobe-{stem} by its hash "
              f"(added: {added})")
        check(not any(ln.startswith(f"REUSED navownprobe-{stem}:") for ln in added),
              f"{case}/a: the edited emit is not REUSED")
        rec = jobs_of(new[-1]).get(f"navownprobe-{stem}") if new else None
        check(bool(rec) and rec.get("outcome") == "ran",
              f"{case}/a: the resuming launch re-measured the candidate arm job (record {rec})")
        rc = load(run / "receipts" / f"{nav}__{cid}__arm.json") or {}
        out = (rc.get("outputs") or [{}])[0]
        check(bool(out.get("sha256")) and out.get("sha256") != edited
              and Path(str(out.get("path"))).as_posix() != emit.relative_to(run).as_posix(),
              f"{case}/a: the arm receipt's output is not the edited file ({out.get('path')}, {str(out.get('sha256'))[:12]})")

    # (b) M1-S10 --------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-b")
    run = tmp / f"{case}-b-different-bytes"
    pl = h.plan(run, ["--select", "navownprobe"])
    if pl is not None:
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        k_arm, k_rev, k_diff = (f"{nav}__{cid}__{part}" for part in ("arm", "reverse", "diff"))
        expect(h.inproc(["run", "--run", run]), 0, f"{case}/b: run 1 exits 0")
        led = ledger_entries(load(run / "ledger.json") or {})
        for k in (k_arm, k_rev, k_diff):
            expect(execution_of(led.get(k)), "completed", f"{case}/b: premise - run 1 accepted {k.split('__', 1)[1]}")
        arm1 = load(run / "receipts" / f"{k_arm}.json") or {}
        sha1 = ((arm1.get("outputs") or [{}])[0]).get("sha256")
        emit1 = run / ((arm1.get("outputs") or [{}])[0]).get("path", "-")
        stamp = (load(emit1) or {}).get("GeneratedUtc") if emit1.is_file() else None
        # Only the candidate arm job loses its cache entry: the baseline stays served and reused, so a new arm receipt
        # can rest on nothing but the arm emit's own bytes differing.
        expect(drop_cache_entries(run, f"navownprobe-{stem}"), 1,
               f"{case}/b: premise - the candidate arm job's one cache entry was removed")
        (run / "receipts" / f"{k_rev}.json").unlink()
        t_end = time.time() + 5
        while time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()) == stamp and time.time() < t_end:
            time.sleep(0.05)                          # the fake probe stamps GeneratedUtc to the second
        n = len(h.launches)
        expect(h.inproc(["resume", "--run", run]), 0, f"{case}/b: the resume exits 0")
        expect(len(h.launches), n + 1, f"{case}/b: premise - the resume relaunched the sweep")
        jobs = jobs_of(sweep_launches(run)[-1]) if sweep_launches(run) else {}
        arec, brec = jobs.get(f"navownprobe-{stem}") or {}, jobs.get("navownprobe-baseline") or {}
        check(brec.get("outcome") == "reused", f"{case}/b: premise - the baseline job was served from the cache ({brec})")
        check(arec.get("outcome") == "ran" and arec.get("emitWritten") and arec.get("exitCode") == 0
              and arec.get("emitSha256") not in (None, sha1),
              f"{case}/b: premise - the arm job was re-measured with different bytes ({str(arec.get('emitSha256'))[:12]} "
              f"vs {str(sha1)[:12]})")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__baseline__arm")), "reused", f"{case}/b: premise - the baseline arm is reused")
        expect(execution_of(led.get(k_arm)), "completed", f"{case}/b: the re-measured arm is recorded anew, not reused")
        arm2 = load(run / "receipts" / f"{k_arm}.json") or {}
        sha2 = ((arm2.get("outputs") or [{}])[0]).get("sha256")
        check(bool(sha2) and sha2 != sha1 and sha2 == arec.get("emitSha256"),
              f"{case}/b: the arm's new receipt names the new emit ({str(sha2)[:12]}; run 1 {str(sha1)[:12]})")
        for k in (k_rev, k_diff):
            st = execution_of(led.get(k))
            check(st in TERMINAL, f"{case}/b: {k.split('__', 1)[1]} is credited after the re-measure (got {st})")
        rev = load(run / "receipts" / f"{k_rev}.json") or {}
        got = (rev.get("consumed") or {}).get("baseEmitSha256")
        check(bool(sha2) and got == sha2, f"{case}/b: the reverse rests on the new arm emit (base {str(got)[:12]})")
        dif = load(run / "receipts" / f"{k_diff}.json") or {}
        got = next((i.get("sha256") for i in (dif.get("consumed") or {}).get("inputs") or [] if i.get("part") == "arm"),
                   None)
        check(bool(sha2) and got == sha2, f"{case}/b: the diff rests on the new arm emit (arm input {str(got)[:12]})")

    # (c) M2 ------------------------------------------------------------------------------------------------
    # Two first runs that leave the reverse unaccepted. `refused`: the reverse job refuses and writes nothing, so run
    # 1's diff read no reverse and was accepted; the resume's diff job never runs (its arm job failed), so D5 keeps
    # that diff reused. `written-rejected`: the reverse emit is written and rejected, so run 1's diff (split by it) was
    # not accepted, and the resume has no diff to credit: it is not accepted and not blamed on a missing completion.
    for tag, rev_entry in (("refused", {"behaviour": "refuse", "code": 5}),
                           ("written-rejected", {"behaviour": "extraction-failed"})):
        h = SweepInProc(tmp, f"{case}-c-{tag}")
        run = tmp / f"{case}-c-arm-rerun-fails-{tag}"
        pl = h.plan(run, ["--select", "navownprobe"])
        if pl is None:
            continue
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        k_arm, k_rev, k_diff = (f"{nav}__{cid}__{part}" for part in ("arm", "reverse", "diff"))
        s1 = h.scenario_with(tmp / f"{case}-c1-{tag}.scenario.json", "navownprobe", f"{stem}.reverse", rev_entry)
        expect(h.inproc(["run", "--run", run], scenario=s1), 0, f"{case}/c/{tag}: run 1 exits 0")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(k_arm)), "completed", f"{case}/c/{tag}: premise - run 1 accepted the candidate arm")
        check(execution_of(led.get(k_rev)) not in TERMINAL,
              f"{case}/c/{tag}: premise - run 1 did not accept the reverse (got {execution_of(led.get(k_rev))})")
        diff1 = execution_of(led.get(k_diff))
        check((diff1 in TERMINAL) == (tag == "refused"),
              f"{case}/c/{tag}: premise - run 1's diff is {'accepted' if tag == 'refused' else 'not accepted'} "
              f"(got {diff1})")
        arm_receipt, diff_receipt = run / "receipts" / f"{k_arm}.json", run / "receipts" / f"{k_diff}.json"
        arm_sha = sha_of(arm_receipt) if arm_receipt.exists() else None
        diff_sha = sha_of(diff_receipt) if diff_receipt.exists() else None
        # nothing can serve the arm job, so it runs again; the baseline stays served, so the arm's receipt still rests
        # on the baseline emit this launch credits
        expect(drop_cache_entries(run, f"navownprobe-{stem}"), 1,
               f"{case}/c/{tag}: premise - the candidate arm job's one cache entry was removed")
        s2 = h.scenario_with(tmp / f"{case}-c2-{tag}.scenario.json", "navownprobe", stem,
                             {"behaviour": "refuse", "code": 5})
        n = len(h.launches)
        expect(h.inproc(["resume", "--run", run], scenario=s2), 0, f"{case}/c/{tag}: the resume exits 0")
        expect(len(h.launches), n + 1, f"{case}/c/{tag}: premise - the resume launched the sweep")
        arm_job = f"navownprobe-{stem}"
        jobs = jobs_of(sweep_launches(run)[-1]) if sweep_launches(run) else {}
        rec = jobs.get(arm_job) or {}
        check(rec.get("outcome") == "ran" and rec.get("exitCode") == 5,
              f"{case}/c/{tag}: premise - this launch's arm job ran and exited 5 ({rec})")
        check((jobs.get("navownprobe-baseline") or {}).get("outcome") == "reused",
              f"{case}/c/{tag}: premise - the baseline job was served from the cache")
        check(f"{arm_job}-reverse" not in jobs and f"diff-{arm_job}" not in jobs,
              f"{case}/c/{tag}: premise - no reverse or diff job ran in this launch ({sorted(jobs)})")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(k_arm)), "reused", f"{case}/c/{tag}: the accepted arm stays reused")
        check(arm_sha is not None and arm_receipt.exists() and sha_of(arm_receipt) == arm_sha,
              f"{case}/c/{tag}: the accepted arm's run-1 receipt is unchanged")
        e = led.get(k_rev) or {}
        expect(execution_of(e), "blocked(no-reverse-measurement)",
               f"{case}/c/{tag}: the reverse that could not run on this launch's failed arm job reads "
               f"no-reverse-measurement")
        # A status with no cause keeps its sentence on the receipt (`result.note`), not in the ledger's detail.
        rrc = load(run / "receipts" / f"{k_rev}.json") or {}
        detail = " | ".join(str(x) for x in [e.get("detail"), (rrc.get("result") or {}).get("note")]
                            + [r.get("detail") for r in (e.get("rejections") or [])
                               + ((rrc.get("validation") or {}).get("rejections") or [])] if x)
        check(re.search(re.escape(arm_job) + r"(?!-reverse)", detail) is not None
              and "exit" in detail and re.search(r"\b5\b", detail) is not None,
              f"{case}/c/{tag}: the reverse's detail names the arm job {arm_job} and its exit code 5 "
              f"(got {detail[:220]!r})")
        d = led.get(k_diff) or {}
        drc = load(diff_receipt) or {}
        dst = str(execution_of(d))
        if tag == "refused":
            expect(dst, "reused", f"{case}/c/{tag}: run 1's accepted diff, whose job did not run again, stays reused (D5)")
            check(diff_sha is not None and diff_receipt.exists() and sha_of(diff_receipt) == diff_sha,
                  f"{case}/c/{tag}: its run-1 receipt is unchanged")
            continue
        check(dst not in TERMINAL and (drc.get("validation") or {}).get("accepted") is not True,
              f"{case}/c/{tag}: the diff is not accepted (got {dst})")
        blob = " ".join([dst, str(d.get("cause")), str(d.get("detail"))]
                        + [f"{x.get('cause')} {x.get('detail')}" for x in (drc.get("validation") or {}).get("rejections") or []])
        check("completion-missing" not in blob, f"{case}/c/{tag}: the diff's cause and detail do not say "
                                                f"completion-missing ({blob[:220]!r})")

    # (d) L2 ------------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-d")
    run = tmp / f"{case}-d-candidate-changed"
    pl = h.plan(run, ["--select", "navownprobe"])
    if pl is not None:
        cand = first_candidate(pl)
        cid = cand["armId"]
        src = Path(cand["candidatePath"])
        original = src.read_bytes()
        try:
            expect(h.inproc(["run", "--run", run]), 0, f"{case}/d: run 1 exits 0")
            expect(len(h.launches), 1, f"{case}/d: premise - run 1 launched the sweep")
            src.write_bytes(original + b"\n ")        # the live candidate file changes after planning
            folders_before = [f.name for f in sweep_launches(run)]
            n = len(h.launches)
            for i in (2, 3):
                expect(h.inproc(["run", "--run", run]), 0, f"{case}/d: run {i} exits 0")
                expect(len(h.launches), n, f"{case}/d: run {i} after the candidate changed launches no ordersweep child")
                expect([f.name for f in sweep_launches(run)], folders_before,
                       f"{case}/d: run {i} creates no new launch folder")
                led = ledger_entries(load(run / "ledger.json") or {})
                expect(execution_of(led.get(f"{nav}__{cid}")), "blocked(candidate-changed)",
                       f"{case}/d: run {i} writes the arm's blocked(candidate-changed) entry")
        finally:
            src.write_bytes(original)                 # the fixture is shared by later cases

    # (e) L4 ------------------------------------------------------------------------------------------------
    h = SweepInProc(tmp, f"{case}-e")
    run = tmp / f"{case}-e-stopped-records"
    pl = h.plan(run, ["--select", "navownprobe"])
    if pl is not None:
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        rewrote = []

        def race(real, jobs):
            res = real(jobs)
            out = Path(jobs[0]["argv"][jobs[0]["argv"].index("--out") + 1])
            lines = []
            for ln in text_lines(out / "jobs.jsonl"):
                if not ln.strip():
                    continue
                r = json.loads(ln)
                if r["job"] == f"navownprobe-{stem}-reverse":
                    r.update(outcome="stopped", stopped="deadline", exitCode=None)
                    rewrote.append(r["job"])
                elif r["job"] == f"diff-navownprobe-{stem}":
                    r.update(outcome="stopped", stopped="before-launch", exitCode=None)
                    rewrote.append(r["job"])
                lines.append(json.dumps(r))
            (out / "jobs.jsonl").write_text("".join(x + "\n" for x in lines), encoding="utf-8")
            return res
        expect(h.inproc(["run", "--run", run], race), 0, f"{case}/e: the run exits 0")
        expect(sorted(rewrote), sorted([f"navownprobe-{stem}-reverse", f"diff-navownprobe-{stem}"]),
               f"{case}/e: premise - the reverse and diff records were rewritten to the race's outcome")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__arm")), "completed", f"{case}/e: premise - the arm was accepted")
        expect(execution_of(led.get(f"{nav}__{cid}__reverse")), "stopped(deadline)",
               f"{case}/e: a reverse record stopped at the deadline reads stopped(deadline)")
        expect(execution_of(led.get(f"{nav}__{cid}__diff")), "skipped-budget",
               f"{case}/e: a diff record stopped before launch reads skipped-budget")


CASES["case-37"] = case_37
CASES["case-38"] = case_38


def case_39(tmp):
    """Review R2 M-1. A sweep job that ordersweep's own Runner refuses to contain never ran and no deadline was reached:
    its part reads blocked(uncontained), never stopped(deadline) / budget-exhausted, and the parts resting on it name
    that job. Honest fixture: case_23's containment-refusal hook, aimed at ONE inner job of a real ordersweep child, with
    a budget far longer than the run. (a) the candidate's reverse job; (b) the candidate's arm job."""
    case = "case-39-sweep-job-uncontained"
    nav = "geometry.navmesh-ownership"
    site = tmp / f"{case}-site"
    site.mkdir(parents=True, exist_ok=True)
    (site / "sitecustomize.py").write_text(REFUSE_CONTAINMENT_SITECUSTOMIZE, encoding="utf-8")
    h = SweepInProc(tmp, case)
    for tag, want_of, part in (("a", lambda s: f"{s}.reverse.json", "reverse"),
                               ("b", lambda s: f"navownprobe{os.sep}{s}.json", "arm")):
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", "navownprobe", "--budget-minutes", "3"])
        if pl is None:
            continue
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(h.scen), HOUSECARL_RUNLOG_DIR=str(h.store),
                   PYTHONPATH=str(site), EVALRUN_TEST_REFUSE_ARGV=want_of(stem))
        t0 = time.monotonic()
        r = subprocess.run([sys.executable, str(EVALRUN), "run", "--run", str(run)], capture_output=True, text=True,
                           env=env, timeout=240)
        expect(r.returncode, 0, f"{case}/{tag}: premise - the run exits 0")
        check(time.monotonic() - t0 < 150, f"{case}/{tag}: premise - the run ended well inside its 3-minute budget")
        inv = last_sweep(run)
        jobs = {}
        for ln in text_lines(inv / "jobs.jsonl"):
            jobs[json.loads(ln)["job"]] = json.loads(ln)
        job = f"navownprobe-{stem}-reverse" if part == "reverse" else f"navownprobe-{stem}"
        rec = jobs.get(job) or {}
        check(rec.get("outcome") == "stopped" and rec.get("stopped") == "uncontained",
              f"{case}/{tag}: premise - the {part} job's record says ordersweep's Runner refused to contain it ({rec})")
        led = ledger_entries(load(run / "ledger.json") or {})
        e = led.get(f"{nav}__{cid}__{part}") or {}
        expect(execution_of(e), "blocked(uncontained)", f"{case}/{tag}: the {part} part reads blocked(uncontained)")
        check(e.get("cause") != "budget-exhausted",
              f"{case}/{tag}: the {part} part is not blamed on the budget (cause {e.get('cause')})")
        if part == "arm":
            rv = led.get(f"{nav}__{cid}__reverse") or {}
            rc = load(run / "receipts" / f"{nav}__{cid}__reverse.json") or {}
            said = " ".join(str(x) for x in (rv.get("detail"), rv.get("rejections"), (rc.get("result") or {}).get("note")))
            check("uncontained" in said or "blocked(uncontained)" in said,
                  f"{case}/{tag}: the reverse names the arm part's containment refusal, not a budget stop ({said[:200]})")
            check("budget" not in said, f"{case}/{tag}: nothing downstream mentions a budget ({said[:200]})")


def case_40(tmp):
    """Review R2 M-3. A diff that conflictdiff refuses before any verdict (exit 3: the two emits describe different
    populations, states or configurations) is rejected for its exit code with conflictdiff's own refusal line, and its
    receipt carries exit 3 - not rejected(schema-unsupported) "no verdict line". The same run's baseline arm stays
    accepted (the refusal is this pair's, not a blanket rejection).

    Task 2026-10-09-tooling-follow-ups item 5: the rejection carries conflictdiff's REASON for every refusal form it
    prints, not only a line starting with "!". Each tag names the words of its form the receipt must quote: the
    population/state gate prints "! ..." lines; the configuration gate prints indented lines ending "REFUSED." (the
    measurement-policy form is case-45's); an emit conflictdiff cannot read exits 2 with the reader's message on
    stderr, which the job log keeps under "---- stderr ----"."""
    case = "case-40-diff-refused-by-conflictdiff"
    nav = "geometry.navmesh-ownership"
    h = SweepInProc(tmp, case)
    forms = {"wrong-state": ("different populations or in different world states", "questions"),
             "wrong-probe": ("DIFFERENT probes", "REFUSED"),
             # the longest gate refusal (392 characters): its closing "REFUSED." must survive the receipt's bound
             "no-configuration": ("which configuration produced it", "Re-emit with the current probe. REFUSED."),
             "no-population": ("do not say which population or world state produced them",),
             "unreadable": ("could not be converted", "$.Instance")}
    codes = {"unreadable": 2}
    for tag, entry in (("wrong-state", {"behaviour": "wrong-state"}),
                       ("wrong-probe", {"behaviour": "wrong-probe-name", "probeName": "otherprobe"}),
                       ("no-configuration", {"behaviour": "no-configuration"}),
                       ("no-population", {"behaviour": "ok", "population": None}),
                       ("unreadable", {"behaviour": "ok", "emitFields": {"Instance": 5}})):
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", "navownprobe"])
        if pl is None:
            continue
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        scen = h.scenario_with(tmp / f"{case}-{tag}.scenario.json", "navownprobe", stem, entry)
        expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}/{tag}: premise - the run exits 0")
        inv = last_sweep(run)
        rec = {}
        for ln in text_lines(inv / "jobs.jsonl"):
            if json.loads(ln)["job"] == f"diff-navownprobe-{stem}":
                rec = json.loads(ln)
        check(rec.get("outcome") == "ran" and rec.get("exitCode") == codes.get(tag, 3),
              f"{case}/{tag}: premise - conflictdiff ran and exited {codes.get(tag, 3)} ({rec.get('outcome')}, {rec.get('exitCode')})")
        rc = load(run / "receipts" / f"{nav}__{cid}__diff.json") or {}
        causes = [x.get("cause") for x in (rc.get("validation") or {}).get("rejections") or []]
        details = " ".join(str(x.get("detail")) for x in (rc.get("validation") or {}).get("rejections") or [])
        check((rc.get("validation") or {}).get("accepted") is False, f"{case}/{tag}: the refused diff is not accepted")
        expect(rc.get("exitCode"), codes.get(tag, 3), f"{case}/{tag}: the diff receipt carries conflictdiff's exit code")
        check("exit-nonzero" in causes and "schema-unsupported" not in causes,
              f"{case}/{tag}: the diff is rejected for its exit code, not for a missing verdict line (causes {causes})")
        check("!" in details or f"exit code {codes.get(tag, 3)}" in details,
              f"{case}/{tag}: the rejection quotes conflictdiff's refusal or its exit ({details[:200]})")
        missing = [w for w in forms[tag] if w not in details]
        check(not missing, f"{case}/{tag}: the rejection carries conflictdiff's reason, missing {missing} "
                           f"({details[:400]})")
        rb = load(run / "receipts" / f"{nav}__baseline__arm.json") or {}
        check((rb.get("validation") or {}).get("accepted") is True,
              f"{case}/{tag}: premise - the baseline arm is still accepted")


def case_41(tmp):
    """Review R2 M-2. After a resume repairs one probe of a candidate, the order evaluation's classification word is
    CURRENT: taken from the one launch that measured every accepted diff of the arm (the repairing launch re-ran the
    kept diff with the same emits and rows), with that launch named - never the word an earlier launch gave while a
    probe was still failing. (b) When no single launch measured every accepted diff (the kept diff's probe did not run
    again in the repairing launch), there is no classification word; a note says why and the launches' words are kept
    as historical; the per-probe numbers are unchanged."""
    case = "case-41-order-classification-current"
    nav, ovl, oe_id = "geometry.navmesh-ownership", "geometry.object-overlap", "patch.order-evaluation"

    def classification_of(run, cid):
        rc = load(run / "receipts" / f"{oe_id}__{cid}.json") or {}
        return rc
    for tag in ("a", "b"):
        h = SweepInProc(tmp, f"{case}-{tag}")
        run = tmp / f"{case}-{tag}"
        pl = h.plan(run, ["--select", f"navownprobe,overlapprobe,{oe_id}"])
        if pl is None:
            continue
        cand = first_candidate(pl)
        cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
        s1 = h.scenario_with(tmp / f"{case}-{tag}-ovl-exit4.json", "overlapprobe", stem, {"behaviour": "exit", "code": 4})
        expect(h.inproc(["run", "--run", run], scenario=s1), 0, f"{case}/{tag}: premise - run 1 exits 0")
        first = last_sweep(run)
        s1_class = ((load(first / "summary.json") or {}).get("candidates", {}).get(stem) or {}).get("classification")
        check(s1_class not in (None, "PASSES-COMPLETED-CHECKS"),
              f"{case}/{tag}: premise - run 1's launch did not classify the candidate as passing ({s1_class})")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__diff")), "completed", f"{case}/{tag}: premise - navownprobe's diff accepted in run 1")
        scen2 = h.scen
        if tag == "b":
            # the repairing launch cannot serve or re-run navownprobe's candidate job: its diff stays kept from run 1
            cache = load(first / "cache.json") or {}
            (first / "cache.json").write_text(json.dumps({k: v for k, v in cache.items()
                                                         if v.get("label") != f"navownprobe-{stem}"}, indent=1),
                                              encoding="utf-8")
            scen2 = h.scenario_with(tmp / f"{case}-b-nav-refuses.json", "navownprobe", stem,
                                    {"behaviour": "refuse", "code": 5})
        expect(h.inproc(["resume", "--run", run], scenario=scen2), 0, f"{case}/{tag}: premise - the resume exits 0")
        second = last_sweep(run)
        check(second != first, f"{case}/{tag}: premise - the resume launched the sweep again")
        led = ledger_entries(load(run / "ledger.json") or {})
        expect(execution_of(led.get(f"{nav}__{cid}__diff")), "reused", f"{case}/{tag}: premise - navownprobe's diff is kept")
        check(execution_of(led.get(f"{ovl}__{cid}__diff")) in TERMINAL,
              f"{case}/{tag}: premise - overlapprobe's diff is repaired ({execution_of(led.get(f'{ovl}__{cid}__diff'))})")
        s2_class = ((load(second / "summary.json") or {}).get("candidates", {}).get(stem) or {}).get("classification")
        expect(evalrun(["finalize", "--run", run], h.scen).returncode, 0, f"{case}/{tag}: premise - finalize exits 0")
        blk = arm_block(load(run / "disposition.json") or {}, cid) or {}
        oe = blk.get("orderEvaluation") or {}
        derived = ((classification_of(run, cid).get("derived")) or {})
        if tag == "a":
            check(s2_class is not None and s2_class != s1_class,
                  f"{case}/a: premise - the repairing launch classified the candidate differently ({s1_class} -> {s2_class})")
            expect(oe.get("classification"), s2_class,
                   f"{case}/a: the disposition quotes the repairing launch's classification, not run 1's")
            expect((oe.get("classificationSource") or {}).get("launch"), f"sweep/{second.name}",
                   f"{case}/a: the disposition names the launch its classification came from")
            expect(derived.get("classification"), s2_class,
                   f"{case}/a: the order-evaluation receipt carries the same current classification")
        else:
            check(oe.get("classification") is None,
                  f"{case}/b: no classification word when no single launch measured every accepted diff "
                  f"(got {oe.get('classification')})")
            check("no single launch" in str(oe.get("classificationNote")),
                  f"{case}/b: the disposition says why ({oe.get('classificationNote')})")
            hist = {(x.get("launch"), x.get("classification")) for x in oe.get("historicalClassifications") or []}
            check((f"sweep/{first.name}", s1_class) in hist,
                  f"{case}/b: run 1's word is kept as historical ({sorted(hist)})")
            check(derived.get("classification") is None and derived.get("classificationNote"),
                  f"{case}/b: the order-evaluation receipt agrees ({derived.get('classification')})")
        check(set((oe.get("perProbe") or {})) == {"navownprobe", "overlapprobe"},
              f"{case}/{tag}: the per-probe numbers name both probes ({sorted(oe.get('perProbe') or {})})")


CASES["case-39"] = case_39
CASES["case-40"] = case_40
CASES["case-41"] = case_41


# ------------------------------------------- imported MCP evidence: origin (42) and prerequisites (43) --

IMPORT_CHECK = "loadability.dangling-references"
MAO = "loadability.masters-and-order"


def import_case_dir(case: str, cands: dict, refuse=()) -> tuple[Path, Path]:
    """A small derived-registry fixture (case 26's shape): the fixture plugins, a scope naming them, the given
    candidates `{stem: plugin list}` and a scenario whose scopeprobe fake refuses the stems in `refuse`.
    Returns (case dir, scenario path)."""
    src = GEN / "cases" / "case-19-derived-prerequisites"
    d = GEN / "cases" / case
    pdir = d / "plugins"
    pdir.mkdir(parents=True, exist_ok=True)
    for p in (GEN / "plugins").iterdir():
        shutil.copyfile(p, pdir / p.name)
    scope = load(src / "scope.json")
    for r in scope["configuration"]["plugins"]:
        r["Path"] = str(pdir / r["Name"]).replace("\\", "/")
    (d / "scope.json").write_text(json.dumps(scope, indent=1), encoding="utf-8")
    cdir = d / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)
    for stem, plugins in cands.items():
        (cdir / f"{stem}.json").write_text(json.dumps(
            {"SchemaVersion": 1, "Name": stem, "BaseProfile": "fixture", "Plugins": plugins,
             "Changes": [f"fixture arm {stem}"], "Provenance": {"fixture": "evalrun test fixture"}}, indent=1),
            encoding="utf-8")
    (cdir / "candidates.json").write_text(json.dumps(
        {"SchemaVersion": 1, "Candidates": [{"File": f"{s}.json", "ViolatesAdvice": [], "SatisfiesAdvice": []}
                                            for s in cands],
         "Budget": {"evaluated": len(cands) - 1, "legalNotEvaluated": 0, "note": "fixture manifest"},
         "Contradictions": []}, indent=1), encoding="utf-8")
    return d, import_scenario(d, "scenario.json", refuse)


def import_scenario(d: Path, name: str, refuse=()) -> Path:
    s = d / name
    s.write_text(json.dumps({
        "pluginDir": str(d / "plugins").replace("\\", "/"), "scopeFrom": str(d / "scope.json").replace("\\", "/"),
        "arms": {}, "loadability": {stem: {"refuse": f"MISSING MASTER - fixture refusal of {stem}"}
                                    for stem in refuse}}, indent=1), encoding="utf-8")
    return s


def import_plan(run: Path, case: str, tag: str) -> dict:
    """Plan `case` selecting only masters-and-order (scopeprobe fake), so `run` is fast and the mcp-only import
    check is planned on every arm with masters-and-order as its prerequisite."""
    scope_fake = GEN / "cases" / case / "scopeprobe_fake.py"
    scope_fake.write_text(SCOPE_FAKE_20, encoding="utf-8")
    expect(do_plan(run, case, ["--select", MAO, "--probe-cmd", f"scopeprobe=python {scope_fake}"],
                   registry="fixture-check-registry-derived.json").returncode, 0, f"{tag}: plan exit", "import")
    plan = load(run / "plan.json") or {}
    row = next((c for c in plan.get("checks") or [] if c.get("checkId") == IMPORT_CHECK), {})
    check((row.get("adapter") or {}).get("kind") == "mcp-only" and MAO in (row.get("prerequisites") or []),
          f"{tag}: premise - {IMPORT_CHECK} is mcp-only and requires {MAO} (got {row.get('prerequisites')})")
    return plan


def arm_by_stem(plan: dict, stem: str) -> dict:
    return next((a for a in plan.get("arms") or [] if Path(a.get("candidateFile") or "").stem == stem), {})


def capture_token(plan: dict, arm: dict) -> str:
    return f"evalrun:{plan['planId']}:{arm['armId']}"


def initialize(name) -> dict:
    ci = {"version": "1"} if name is None else {"name": name, "version": "fixture-capture 1"}
    return {"jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": ci}}


def tools_call(n: int, text: str = "") -> dict:
    return {"jsonrpc": "2.0", "id": n, "method": "tools/call",
            "params": {"name": "housecarl_records", "arguments": {"path": "FixtureTarget.esp",
                                                                          "note": text}}}


def origin_import(folder: Path, plan: dict, arm: dict, *, origin="fresh", request=None, captured=None,
                  reused_from=None, drop=(), response=None) -> tuple[Path, dict]:
    """A schema-1 import of a no-finding result for `arm` of `plan`. `request` is the raw request file's exact text
    (default: one JSON-RPC message per line, a tokened initialize, an initialized notification and a call)."""
    folder.mkdir(parents=True, exist_ok=True)
    req_p, resp_p = folder / "request.jsonl", folder / "response.jsonl"
    if request is None:
        request = "\n".join(json.dumps(m) for m in (
            initialize(capture_token(plan, arm)), {"jsonrpc": "2.0", "method": "notifications/initialized"},
            tools_call(1))) + "\n"
    req_p.write_bytes(request.encode("utf-8") if isinstance(request, str) else request)
    resp_p.write_text(response if response is not None else
                      json.dumps({"jsonrpc": "2.0", "id": 1, "result": {"findings": 0}}) + "\n", encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": req["providers"], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": IMPORT_CHECK, "armId": arm["armId"],
           "state": "baseline", "origin": origin,
           "capturedUtc": captured or datetime_now_z(),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                       for role, p in (("request", req_p), ("response", resp_p))]}
    if reused_from is not None:
        doc["reusedFrom"] = reused_from
    for k in drop:
        doc.pop(k, None)
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest, doc


def datetime_now_z() -> str:
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def shift_iso(text: str, seconds: float = 0, offset_hours: int = 0) -> str:
    """`text` moved by `seconds` and written in UTC`offset_hours` - the same instant when seconds is 0."""
    from datetime import datetime, timedelta, timezone
    t = datetime.fromisoformat(text.replace("Z", "+00:00")) + timedelta(seconds=seconds)
    return t.astimezone(timezone(timedelta(hours=offset_hours))).isoformat()


def write_doc(manifest: Path, doc: dict) -> Path:
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def import_state(run: Path, cid: str, aid: str) -> tuple[str | None, dict, bool]:
    """(ledger status, receipt, whether any imports/<key>* folder exists) for one import key."""
    key = f"{cid}__{aid}"
    status = ((load(run / "ledger.json") or {}).get("entries") or {}).get(key, {}).get("status")
    rc = load(run / "receipts" / f"{key}.json") if (run / "receipts" / f"{key}.json").exists() else {}
    # this key's folders only: `<key>` and its re-imports `<key>~<n>`, never an arm whose id starts with this one
    folders = [p for p in (run / "imports").glob(f"{key}*")
               if p.name == key or p.name.startswith(key + "~")] if (run / "imports").exists() else []
    return status, rc or {}, bool(folders)


def case_42(tmp):
    """Option A (2026-09-17): an import declares where its evidence came from. `fresh` evidence must have been
    captured for THIS plan's arm - every MCP initialize in its hash-checked raw request names
    evalrun:<planId>:<armId> in clientInfo.name and capturedUtc is not before the plan was made. `reused` evidence
    names its source plan, arm and result hash and is labelled reused end to end. The exact relabelling mistake
    (retained evidence with only planId, armId and configuration.source edited) is refused as fresh; every existing
    identity and hash check still applies to both origins."""
    case = "case-42-import-origin"
    stems1 = {"candidate-00-baseline": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
              "candidate-01-reorder": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"]}
    # plan 2: the same plugins under a different candidate file, so its arm has another id and source and the same
    # configuration fingerprint - the relabelling V2 reproduced.
    stems2 = {"candidate-00-baseline": stems1["candidate-00-baseline"],
              "candidate-01-renamed": stems1["candidate-01-reorder"],
              # a second setup with the SAME configuration (the destination of a sibling-arm reuse) and one with a
              # different order, whose configuration fingerprint differs (the mismatch control)
              "candidate-02-same": stems1["candidate-01-reorder"],
              "candidate-03-different": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"]}
    _, scen1 = import_case_dir(case + "-p1", stems1)
    _, scen2 = import_case_dir(case + "-p2", stems2)
    run1, run2 = tmp / f"{case}-run1", tmp / f"{case}-run2"
    plan1 = import_plan(run1, case + "-p1", f"{case}/p1")
    time.sleep(1.1)  # plan 2 is created at a later second than plan 1
    plan2 = import_plan(run2, case + "-p2", f"{case}/p2")
    for run, scen, tag in ((run1, scen1, "p1"), (run2, scen2, "p2")):
        expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/{tag}: run exit", "import")
    if not plan1 or not plan2:
        return
    check(plan1["planId"] != plan2["planId"], f"{case}: premise - the two plans have different ids")
    a1, a2 = arm_by_stem(plan1, "candidate-01-reorder"), arm_by_stem(plan2, "candidate-01-renamed")
    b1, b2 = arm_by_stem(plan1, "candidate-00-baseline"), arm_by_stem(plan2, "candidate-00-baseline")
    for run, arm, tag in ((run1, a1, "p1"), (run2, a2, "p2"), (run1, b1, "p1-base"), (run2, b2, "p2-base")):
        expect(execution_of((ledger_entries(load(run / "ledger.json") or {})).get(f"{MAO}__{arm.get('armId')}")),
               "completed", f"{case}/{tag}: premise - {MAO} accepted on {arm.get('armId')}")
    check(a1.get("armId") != a2.get("armId")
          and [(p["name"], p["sha256"]) for p in a1["requested"]["plugins"]]
          == [(p["name"], p["sha256"]) for p in a2["requested"]["plugins"]],
          f"{case}: premise - the relabel target arm differs in id and matches in plugin names and bytes")

    def imp(run, manifest, want, tag, text=None, absent=None):
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "option A")
        if text:
            check(text in r.stderr, f"{case}/{tag}: the refusal names {text!r} (got {r.stderr.strip()[-300:]!r})")
        if absent:
            check(absent not in r.stderr, f"{case}/{tag}: the refusal does not blame {absent!r} "
                                          f"(got {r.stderr.strip()[-300:]!r})")
        return r

    def untouched(run, arm, tag):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, "mcp-only", f"{case}/{tag}: the refused import leaves the entry mcp-only", "import")
        check(not (rc.get("validation") or {}).get("accepted") and not folder,
              f"{case}/{tag}: no accepted receipt and no imports folder after a refusal")

    # ---- a valid fresh capture for plan 1 is accepted and says so
    good1, good1_doc = origin_import(tmp / f"{case}-good1", plan1, a1)
    imp(run1, good1, 0, "fresh-valid")
    _, rc1, _ = import_state(run1, IMPORT_CHECK, a1["armId"])
    imported1 = rc1.get("imported") or {}
    expect(imported1.get("origin"), "fresh", f"{case}/fresh-valid: the receipt records origin fresh", "option A")
    expect((imported1.get("originCheck") or {}).get("token"), capture_token(plan1, a1),
           f"{case}/fresh-valid: the receipt records the token it checked", "option A")
    expect((imported1.get("originCheck") or {}).get("initializeRequests"), 1,
           f"{case}/fresh-valid: the receipt counts the initialize requests it read", "option A")
    source_sha = sha_of(run1 / "imports" / f"{IMPORT_CHECK}__{a1['armId']}" / "result.json")
    expect(imported1.get("sourceSha256"), source_sha, f"{case}: premise - sourceSha256 is the copied result.json")

    # ---- the exact V2 mistake: plan 1's accepted envelope with ONLY planId, armId and configuration.source edited
    relabel = json.loads(json.dumps(good1_doc))
    relabel["planId"], relabel["armId"] = plan2["planId"], a2["armId"]
    relabel["configuration"]["source"] = f"candidate:{a2['candidateFile']}"
    changed = sorted(k for k in good1_doc if good1_doc[k] != relabel[k])
    expect(changed, ["armId", "configuration", "planId"], f"{case}/relabel: premise - exactly the three labels differ")
    expect(import_fingerprint(relabel["configuration"]), relabel["configuration"]["fingerprint"],
           f"{case}/relabel: premise - the fingerprint is unchanged and still valid")
    rel_m = write_doc(good1.parent / "relabel.json", relabel)
    imp(run2, rel_m, 2, "relabel-as-fresh", text="evalrun:", absent="capturedUtc")
    untouched(run2, a2, "relabel-as-fresh")
    # the same relabelled envelope with no origin at all (as retained envelopes are) is refused by name too
    legacy = dict(relabel)
    legacy.pop("origin")
    imp(run2, write_doc(good1.parent / "relabel-no-origin.json", legacy), 2, "relabel-no-origin", text="origin")
    untouched(run2, a2, "relabel-no-origin")

    # ---- origin declarations
    def variant(tag, run, plan, arm, **kw):
        return origin_import(tmp / f"{case}-{tag}", plan, arm, **kw)[0]

    imp(run2, variant("no-origin", run2, plan2, a2, drop=("origin",)), 2, "origin-missing", text="origin-missing")
    imp(run2, variant("bad-origin", run2, plan2, a2, origin="measured"), 2, "origin-invalid", text="origin")
    imp(run2, variant("fresh-with-source", run2, plan2, a2,
                      reused_from={"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}),
        2, "fresh-declares-source", text="reusedFrom")

    # ---- the token: absent, wrong arm, wrong plan, mixed, only in unrelated text, no initialize, empty, unreadable
    t2 = capture_token(plan2, a2)

    def lines(*msgs):
        return "\n".join(m if isinstance(m, str) else json.dumps(m) for m in msgs) + "\n"

    token_cases = [
        ("token-absent", lines(initialize("another-capture-wrapper"), tools_call(1))),
        ("token-name-missing", lines(initialize(None), tools_call(1))),
        ("token-wrong-arm", lines(initialize(capture_token(plan2, b2)), tools_call(1))),
        ("token-wrong-plan", lines(initialize(capture_token(plan1, a2)), tools_call(1))),
        ("token-in-version", lines({**initialize("capture"), "params": {**initialize("capture")["params"],
                                    "clientInfo": {"name": "capture", "version": t2}}}, tools_call(1))),
        ("token-mixed", lines(initialize(t2), tools_call(1), initialize("another-capture-wrapper"),
                              tools_call(2))),
        ("token-mixed-batch", lines([initialize(t2), tools_call(1)], [initialize(t2 + "x"), tools_call(2)])),
        ("token-only-in-call", lines(tools_call(1, t2), {"batchMarker": t2})),
        ("token-only-in-marker", lines({"batchMarker": t2, "clientInfo": {"name": t2}},
                                       initialize("another-capture-wrapper"), tools_call(1))),
        ("token-empty-file", ""),
        ("token-unreadable-line", lines(initialize(t2), "{not json " + t2)),
    ]
    for tag, text in token_cases:
        imp(run2, variant(tag, run2, plan2, a2, request=text), 2, tag, text="evalrun:" if text else "initialize")
    untouched(run2, a2, "token-controls")

    # ---- capturedUtc against the plan's createdUtc, compared as instants
    created2 = plan2["createdUtc"]
    imp(run2, variant("time-old", run2, plan2, a2, captured=shift_iso(created2, -1)), 2, "time-before-plan",
        text="capturedUtc")
    imp(run2, variant("time-old-offset", run2, plan2, a2, captured=shift_iso(created2, -1, -7)), 2,
        "time-before-plan-other-offset", text="capturedUtc")
    imp(run2, variant("time-naive", run2, plan2, a2, captured=created2.replace("Z", "")), 2, "time-naive",
        text="capturedUtc")
    imp(run2, variant("time-invalid", run2, plan2, a2, captured="yesterday"), 2, "time-invalid", text="capturedUtc")
    untouched(run2, a2, "time-controls")
    # the boundary: exactly the plan's instant, written at UTC-07:00, is not earlier
    imp(run2, variant("time-boundary", run2, plan2, b2, captured=shift_iso(created2, 0, -7)), 0, "time-boundary-equal")
    # a JSON-RPC batch line (an array) is read as messages too
    imp(run1, variant("batch-array", run1, plan1, b1,
                      request=lines([initialize(capture_token(plan1, b1)), tools_call(1)])), 0, "fresh-batch-array")

    # ---- reused: the source must be declared, well formed and not this plan's own arm; identity still decides
    src = {"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}
    reuse_request = (good1.parent / "request.jsonl").read_text(encoding="utf-8")  # plan 1's token, by design

    def reused(tag, **kw):
        kw.setdefault("reused_from", src)
        kw.setdefault("request", reuse_request)
        kw.setdefault("captured", good1_doc["capturedUtc"])
        return variant(tag, run2, plan2, a2, origin="reused", **kw)

    imp(run2, reused("reuse-no-source", reused_from=None), 2, "reuse-without-source", text="reusedFrom")
    for tag, bad in (("reuse-short-hash", {**src, "resultSha256": "abc"}),
                     ("reuse-no-arm", {k: v for k, v in src.items() if k != "armId"}),
                     ("reuse-empty-plan", {**src, "planId": ""}),
                     ("reuse-not-object", "plan 1")):
        imp(run2, reused(tag, reused_from=bad), 2, tag, text="reusedFrom")
    imp(run2, reused("reuse-self", reused_from={**src, "planId": plan2["planId"], "armId": a2["armId"]}), 2,
        "reuse-names-itself", text="reusedFrom")
    # a sibling arm of THIS plan is a legitimate source when the two setups' configurations match: the evidence is
    # measured once and reused, and the receipt says which statement that is (decided 2026-09-17). The
    # configuration check is unchanged, so a sibling whose configuration does NOT match still refuses: that control
    # is below, on the plan-1 baseline arm, whose requested configuration differs from the candidate's.
    wrong_cfg = reused("reuse-wrong-config")
    wd = load(wrong_cfg)
    wd["configuration"]["plugins"][0]["sha256"] = "0" * 64
    wd["configuration"]["fingerprint"] = import_fingerprint(wd["configuration"])
    imp(run2, write_doc(wrong_cfg, wd), 2, "reuse-changed-configuration", text="identity-mismatch")
    wrong_fp = reused("reuse-wrong-fingerprint")
    wf = load(wrong_fp)
    wf["configuration"]["plugins"] = list(reversed(wf["configuration"]["plugins"]))
    wf["configuration"]["fingerprint"] = import_fingerprint(wf["configuration"])
    imp(run2, write_doc(wrong_fp, wf), 2, "reuse-changed-order", text="identity-mismatch")
    stale = reused("reuse-changed-evidence")
    (stale.parent / "response.jsonl").write_text('{"jsonrpc": "2.0", "id": 1, "result": {"findings": 7}}\n',
                                                 encoding="utf-8")
    imp(run2, stale, 2, "reuse-changed-evidence", text="output-stale")
    zero = reused("reuse-zero-coverage")
    zd = load(zero)
    zd["result"]["coverage"][0]["Judged"] = 0
    imp(run2, write_doc(zero, zd), 2, "reuse-zero-coverage", text="extraction-failed")
    untouched(run2, a2, "reuse-controls")

    ok = reused("reuse-valid")
    imp(run2, ok, 0, "reuse-valid")
    status, rc2, _ = import_state(run2, IMPORT_CHECK, a2["armId"])
    imported2 = rc2.get("imported") or {}
    expect(status, "completed", f"{case}/reuse-valid: ledger completed", "option A")
    expect(imported2.get("origin"), "reused", f"{case}/reuse-valid: the receipt records origin reused", "option A")
    expect(imported2.get("reusedFrom"), src, f"{case}/reuse-valid: the receipt retains the declared source", "option A")
    expect(imported2.get("reusedFromVerified"), False,
           f"{case}/reuse-valid: the declared source is not presented as verified", "option A")
    check("originCheck" not in imported2, f"{case}/reuse-valid: a reused import claims no capture-token check")
    expect(imported2.get("reusedWithin"), "another-evaluation",
           f"{case}/reuse-valid: the receipt says the source is another evaluation", "option A")

    # ---- reuse between two setups of THIS evaluation (decided 2026-09-17): measured once for identical
    # configurations, accepted, and labelled as reuse WITHIN this evaluation rather than from an earlier one.
    c2, d2 = arm_by_stem(plan2, "candidate-02-same"), arm_by_stem(plan2, "candidate-03-different")
    check(bool(c2) and bool(d2), f"{case}: premise - plan 2 has an identical-configuration arm and a different one")
    for arm, same in ((c2, True), (d2, False)):
        expect([(p["name"], p["sha256"]) for p in (arm.get("requested") or {}).get("plugins") or []]
               == [(p["name"], p["sha256"]) for p in a2["requested"]["plugins"]], same,
               f"{case}: premise - {arm.get('armId')} configuration matches the reuse source: {same}")
    sibling = {"planId": plan2["planId"], "armId": a2["armId"],
               "resultSha256": sha_of(run2 / "imports" / f"{IMPORT_CHECK}__{a2['armId']}" / "result.json")}
    # the configuration checks are untouched: an envelope for this arm carrying ANOTHER setup's configuration refuses,
    # sibling-arm source or not
    mism, mism_doc = origin_import(tmp / f"{case}-sibling-mismatch", plan2, c2, origin="reused",
                                   reused_from=sibling, request=reuse_request, captured=good1_doc["capturedUtc"])
    mism_doc["configuration"]["plugins"] = [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                                            for p in d2["requested"]["plugins"]]
    mism_doc["configuration"]["fingerprint"] = import_fingerprint(mism_doc["configuration"])
    imp(run2, write_doc(mism, mism_doc), 2, "sibling-reuse-wrong-configuration", text="identity-mismatch")
    untouched(run2, c2, "sibling-reuse-wrong-configuration")
    # and the arm's own prerequisite still has to be settled - it is, so the honest declaration is accepted
    sib = origin_import(tmp / f"{case}-sibling-valid", plan2, c2, origin="reused", reused_from=sibling,
                        request=reuse_request, captured=good1_doc["capturedUtc"])[0]
    imp(run2, sib, 0, "sibling-reuse-valid")
    status_c2, rc_c2, _ = import_state(run2, IMPORT_CHECK, c2["armId"])
    imported_c2 = rc_c2.get("imported") or {}
    expect(status_c2, "completed", f"{case}/sibling-reuse-valid: ledger completed", "reuse within this evaluation")
    expect(imported_c2.get("origin"), "reused", f"{case}/sibling-reuse-valid: origin reused")
    expect(imported_c2.get("reusedWithin"), "this-evaluation",
           f"{case}/sibling-reuse-valid: the receipt says the source is another setup of THIS evaluation",
           "reuse within this evaluation")
    expect(imported_c2.get("reusedFrom"), sibling,
           f"{case}/sibling-reuse-valid: the declared source is retained")
    expect(imported_c2.get("reusedFromVerified"), False,
           f"{case}/sibling-reuse-valid: still declared, not verified")
    # the destination's own (plan, arm) is still not a source
    imp(run2, origin_import(tmp / f"{case}-sibling-self", plan2, d2, origin="reused",
                            reused_from={**sibling, "armId": d2["armId"]}, request=reuse_request,
                            captured=good1_doc["capturedUtc"])[0], 2, "sibling-self-still-refused", text="reusedFrom")

    # ---- the label survives resume, a ledger rebuild, finalize, a relocated bundle and STATUS.md
    receipt_bytes = (run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json").read_bytes()
    expect(evalrun(["resume", "--run", run2], scen2).returncode, 0, f"{case}/resume: exit", "option A")
    # the ledger's own note that this entry came from an import, and with what origin, survives the transition that
    # re-uses it (review R1b F5); a --rebuild-ledger reads the journal, which carries no extras, and the receipt is
    # then the authority - asserted below.
    resumed_entry = ((load(run2 / "ledger.json") or {}).get("entries") or {}).get(
        f"{IMPORT_CHECK}__{a2['armId']}") or {}
    expect(resumed_entry.get("status"), "reused", f"{case}/ledger: premise - the executor re-used the entry")
    expect(resumed_entry.get("importOrigin"), "reused",
           f"{case}/ledger: the ledger still records the import's origin after resume", "option A")
    check(resumed_entry.get("imported") is True,
          f"{case}/ledger: the ledger still records that it was imported after resume")
    expect(evalrun(["resume", "--run", run2, "--rebuild-ledger"], scen2).returncode, 0,
           f"{case}/rebuild: exit", "option A")
    status, rc2b, _ = import_state(run2, IMPORT_CHECK, a2["armId"])
    expect(status, "reused", f"{case}/rebuild: the import is still credited (executor reuse)", "option A")
    check((run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json").read_bytes() == receipt_bytes,
          f"{case}/rebuild: the import receipt is byte-identical after resume and rebuild")
    expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0, f"{case}/finalize: exit", "option A")
    status_md = (run2 / "STATUS.md").read_text(encoding="utf-8")
    check("reused" in status_md and plan1["planId"] in status_md,
          f"{case}/finalize: STATUS.md names the reused import and its source plan")
    check("of THIS plan" in status_md and a2["armId"] in status_md,
          f"{case}/finalize: STATUS.md says the sibling-arm reuse came from another setup of this plan, "
          f"not from an earlier evaluation")
    rows_md = {r.get("armId"): r for r in (load(run2 / "disposition.json") or {}).get("importedEvidence") or []}
    expect((rows_md.get(c2["armId"]) or {}).get("reusedWithin"), "this-evaluation",
           f"{case}/finalize: the disposition carries the reuse scope", "reuse within this evaluation")
    expect((rows_md.get(a2["armId"]) or {}).get("reusedWithin"), "another-evaluation",
           f"{case}/finalize: and distinguishes the cross-evaluation one", "reuse within this evaluation")
    check("fresh" in status_md, f"{case}/finalize: STATUS.md names the fresh import's origin")
    bundle, moved = tmp / f"{case}-bundle", tmp / f"{case}-relocated"
    expect(evalrun(["bundle", "--run", run2, "--out", bundle], scen2).returncode, 0, f"{case}/bundle: exit")
    shutil.copytree(bundle, moved)
    expect(evalrun(["verify-bundle", moved]).returncode, 0, f"{case}/relocated: verify-bundle exit", "option A")
    brc = load(moved / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}
    expect((brc.get("imported") or {}).get("origin"), "reused", f"{case}/relocated: bundled receipt says reused")
    expect((brc.get("imported") or {}).get("reusedFrom"), src, f"{case}/relocated: bundled receipt keeps the source")

    # ---- a receipt accepted before origin existed: its `imported` block is exactly what the earlier importer wrote
    # ({sourceSha256, capturedUtc, tool}). It stays readable, credited and verifiable, and is reported as origin
    # unknown - never as fresh.
    legacy_p = run1 / "receipts" / f"{IMPORT_CHECK}__{a1['armId']}.json"
    lrc = load(legacy_p)
    lrc["imported"] = {k: lrc["imported"][k] for k in ("sourceSha256", "capturedUtc", "tool")}
    legacy_p.write_text(json.dumps(lrc, indent=1), encoding="utf-8")
    expect(evalrun(["resume", "--run", run1], scen1).returncode, 0, f"{case}/legacy: resume exit")
    status, _, _ = import_state(run1, IMPORT_CHECK, a1["armId"])
    expect(status, "reused", f"{case}/legacy: the legacy import is still credited after resume", "option A")
    expect(evalrun(["finalize", "--run", run1], scen1).returncode, 0, f"{case}/legacy: finalize exit")
    rows = {(x.get("armId")): x for x in (load(run1 / "disposition.json") or {}).get("importedEvidence") or []}
    expect((rows.get(a1["armId"]) or {}).get("origin"), "unknown",
           f"{case}/legacy: disposition reports the legacy import's origin as unknown", "option A")
    expect((rows.get(b1["armId"]) or {}).get("origin"), "fresh",
           f"{case}/legacy: the fresh import beside it stays fresh", "option A")
    s1 = (run1 / "STATUS.md").read_text(encoding="utf-8")
    check("origin not recorded" in s1, f"{case}/legacy: STATUS.md says the legacy import's origin is not recorded")
    lb, lmoved = tmp / f"{case}-legacy-bundle", tmp / f"{case}-legacy-relocated"
    expect(evalrun(["bundle", "--run", run1, "--out", lb], scen1).returncode, 0, f"{case}/legacy: bundle exit")
    shutil.copytree(lb, lmoved)
    expect(evalrun(["verify-bundle", lmoved]).returncode, 0, f"{case}/legacy: relocated verify-bundle exit")

    # ---- after a --rebuild-ledger the RECEIPT is the authority on origin, and the rebuilt entry now CARRIES
    # what the receipt says rather than a hole. This expectation was corrected on 2026-09-17: it previously
    # asserted the rebuilt entry claimed no origin, which encoded the defect Piranha's handback named - the
    # journal carries no extras, and `rebuild` was not consulting the receipts either, although its own
    # docstring and `--rebuild-ledger`'s help both say it is a view of the journal AND the receipts. The
    # consequence was not cosmetic: `finalize` reads `detector` from the ledger, so every rebuilt entry
    # finalized as `detector-undetermined` and an import with accepted evidence on disk was reported missing.
    # The authority is unchanged - the value below comes FROM the receipt, and a receipt that does not match
    # this entry donates nothing (`Ledger.receipt_facts`).
    rebuilt = ((load(run2 / "ledger.json") or {}).get("entries") or {}).get(f"{IMPORT_CHECK}__{a2['armId']}") or {}
    expect(rebuilt.get("importOrigin"), "reused",
           f"{case}/ledger: a rebuilt entry recovers the origin its receipt records", "option A")
    expect(rebuilt.get("detector"), (load(run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json")
                                     or {}).get("result", {}).get("detector"),
           f"{case}/ledger: and the detector, which is what finalize reads from the ledger", "option A")
    expect(((load(run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}).get("imported")
            or {}).get("origin"), "reused",
           f"{case}/ledger: and the receipt still carries it after the rebuild", "option A")

    # ---- the request file is JSON LINES as the contract defines it: a raw U+2028 inside a JSON string is part of
    # the line, not a line break, so a genuine capture carrying one is read rather than refused (review R1a 2 / R1b F3)
    ev = import_evalrun_module()
    sep_line = json.dumps({"jsonrpc": "2.0", "id": 0, "method": "initialize",
                           "params": {"clientInfo": {"name": "evalrun:P:A", "version": "note sep"}}},
                          ensure_ascii=False)
    inits, why = ev.initialize_requests((sep_line + "\r\n").encode("utf-8"))
    expect(why, None, f"{case}/lines: a U+2028 inside a JSON string does not make the file unreadable", "§4.8")
    expect(len(inits or []), 1, f"{case}/lines: the one initialize request is read", "§4.8")
    expect(ev.client_name((inits or [{}])[0]), "evalrun:P:A", f"{case}/lines: and its clientInfo.name is intact")
    bad_inits, bad_why = ev.initialize_requests(b'{"jsonrpc": "2.0"\n}\n')
    check(bad_inits is None and "not JSON" in str(bad_why),
          f"{case}/lines: a real newline inside a message is still refused (got {bad_why!r})")

    # ---- the superseded record names THIS key's import folders only. An arm id can be a prefix of another arm's
    # (`...-order` and `...-order-plus-patch`), and a prefix match would point a reader at that arm's evidence
    # (review R2a L1 / R2b F2).
    folder_run = tmp / f"{case}-folder-scope"
    (folder_run / "receipts").mkdir(parents=True, exist_ok=True)
    fcheck, farm = {"checkId": "loadability.x"}, {"armId": "cand-01-order"}
    fkey = f"{fcheck['checkId']}__{farm['armId']}"
    for name in (fkey, f"{fkey}~2", f"{fkey}-plus-patch", f"{fkey}-plus-patch~2"):
        (folder_run / "imports" / name).mkdir(parents=True, exist_ok=True)
    (folder_run / "receipts" / f"{fkey}.json").write_text(json.dumps(
        {"receiptId": "r1", "validation": {"accepted": True},
         "imported": {"origin": "fresh", "sourceSha256": "a" * 64, "capturedUtc": "2026-09-17T00:00:00Z"}}),
        encoding="utf-8")
    got = ev.superseded_import(types.SimpleNamespace(run=folder_run), fcheck, farm)
    expect(sorted((got or {}).get("supersededImport", {}).get("folders") or []),
           [f"imports/{fkey}", f"imports/{fkey}~2"],
           f"{case}/folders: only this key's import folders are named", "R2a-L1")


def import_evalrun_module():
    """The controller imported as a library, for function-level known-answer checks."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalrun_lib", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def evalrun_inprocess(run: Path, scen_path: Path, override, verb="run", extra=()):
    """One controller invocation in process with run_generic replaced for what `override` claims (case 26's tool)."""
    import importlib.util
    spec_ = importlib.util.spec_from_file_location("evalrun_inproc", EVALRUN)
    mod_ = importlib.util.module_from_spec(spec_)
    spec_.loader.exec_module(mod_)
    real = mod_.run_generic

    def generic(ex, check_row, arm):
        if not override(ex, check_row, arm):
            real(ex, check_row, arm)
    mod_.run_generic = generic
    saved_ = os.environ.get("EVALRUN_FAKE_SCENARIO")
    os.environ["EVALRUN_FAKE_SCENARIO"] = str(scen_path)
    try:
        a_ = mod_.build_parser().parse_args([verb, "--run", str(run)] + list(extra))
        return a_.fn(a_)
    finally:
        mod_.run_generic = real
        if saved_ is None:
            os.environ.pop("EVALRUN_FAKE_SCENARIO", None)
        else:
            os.environ["EVALRUN_FAKE_SCENARIO"] = saved_


def case_43(tmp):
    """Review V2 L3: import-results consults the controller's prerequisite gate (`prereq_satisfied`) for both
    origins before anything is written, and an import accepted earlier stops being credited when run/resume
    establishes a prerequisite failure on its arm. A failed arm is not rescued by the baseline; a prerequisite not
    attempted on the arm still falls back to the baseline; recovery is a re-import once the arm's prerequisite is
    legitimately satisfied."""
    case = "case-43-import-prerequisites"
    stems = {"candidate-00-baseline": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
             "candidate-01-missing-master": ["Update.esm", "Skyrim.esm", "FixtureTarget.esp"],
             "candidate-02-reorder": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"]}
    d, scen = import_case_dir(case, stems, refuse=("candidate-01-missing-master",))
    scen_ok = import_scenario(d, "scenario-recovered.json", ())

    def imp(run, manifest, want, tag, text=None):
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "V2 L3")
        if text:
            check(text in r.stderr, f"{case}/{tag}: the refusal names {text!r} (got {r.stderr.strip()[-300:]!r})")
        return r

    def refused_clean(run, arm, tag, want_status):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, want_status, f"{case}/{tag}: the ledger entry is unchanged by the refusal", "V2 L3")
        check(not (rc.get("validation") or {}).get("accepted"), f"{case}/{tag}: no accepted receipt for the key")
        check(not folder, f"{case}/{tag}: no imports folder was written")

    def nulls_and_missing(run):
        disp = load(run / "disposition.json") or {}
        nulls = {(r.get("checkId"), r.get("armId")) for r in (field(disp, "completedNullChecks") or [])}
        missing = {(r.get("checkId"), r.get("armId")): r.get("status") for r in (field(disp, "missing") or [])}
        return nulls, missing

    # ---- (a) prerequisite unsettled everywhere: nothing has run yet
    run = tmp / f"{case}-main"
    plan = import_plan(run, case, f"{case}/main")
    if not plan:
        return
    bad, good, base = (arm_by_stem(plan, "candidate-01-missing-master"), arm_by_stem(plan, "candidate-02-reorder"),
                       arm_by_stem(plan, "candidate-00-baseline"))
    source = {"planId": "0123456789ab", "armId": "candidate-02-other", "resultSha256": "a" * 64}
    m_good = origin_import(tmp / f"{case}-good-fresh", plan, good)[0]
    imp(run, m_good, 2, "unsettled-fresh", text=MAO)
    m_good_reused = origin_import(tmp / f"{case}-good-reused", plan, good, origin="reused", reused_from=source)[0]
    imp(run, m_good_reused, 2, "unsettled-reused", text=MAO)
    refused_clean(run, good, "unsettled", None)

    # ---- (b) run: the baseline and the reorder arm load, the missing-master arm is refused
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/main: run exit", "V2 L3")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{MAO}__{base['armId']}")), "completed", f"{case}: premise - {MAO} baseline")
    expect(execution_of(led.get(f"{MAO}__{bad['armId']}")), "rejected(illegal-configuration)",
           f"{case}: premise - {MAO} refused on {bad['armId']}")
    expect(execution_of(led.get(f"{MAO}__{good['armId']}")), "completed", f"{case}: premise - {MAO} {good['armId']}")

    # ---- (c) a failed arm with a passing baseline refuses, fresh and reused alike, naming the prerequisite
    m_bad = origin_import(tmp / f"{case}-bad-fresh", plan, bad)[0]
    r = imp(run, m_bad, 2, "failed-arm-fresh", text=MAO)
    check("rejected" in r.stderr, f"{case}/failed-arm-fresh: the refusal says the prerequisite was refused on this "
                                  f"arm (got {r.stderr.strip()[-300:]!r})")
    m_bad_reused = origin_import(tmp / f"{case}-bad-reused", plan, bad, origin="reused", reused_from=source)[0]
    imp(run, m_bad_reused, 2, "failed-arm-reused", text=MAO)
    refused_clean(run, bad, "failed-arm", "mcp-only")

    # ---- (d) the arms whose prerequisite is settled import normally
    imp(run, m_good, 0, "settled-fresh")
    imp(run, origin_import(tmp / f"{case}-base-reused", plan, base, origin="reused", reused_from=source)[0], 0,
        "settled-baseline-reused")
    expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}/main: finalize exit")
    nulls, missing = nulls_and_missing(run)
    check((IMPORT_CHECK, good["armId"]) in nulls and (IMPORT_CHECK, base["armId"]) in nulls,
          f"{case}/main: the accepted imports are completed null checks")
    check((IMPORT_CHECK, bad["armId"]) not in nulls and (IMPORT_CHECK, bad["armId"]) in missing,
          f"{case}/main: the refused import is missing, never a completed null (got {missing.get((IMPORT_CHECK, bad['armId']))})")

    # ---- (e) justified fallback: the arm's own prerequisite was NOT attempted, the baseline's was accepted
    run2 = tmp / f"{case}-fallback"
    plan2 = import_plan(run2, case, f"{case}/fallback")
    bad2, base2 = arm_by_stem(plan2, "candidate-01-missing-master"), arm_by_stem(plan2, "candidate-00-baseline")

    chain2 = arm_by_stem(plan2, "candidate-02-reorder")

    def not_attempted(ex, check_row, arm):
        """On one arm the prerequisite is never attempted (the baseline's answer counts); on another it is blocked one
        hop down by a prerequisite of its own, which decides for that arm (`decided_on_arm`)."""
        if check_row["checkId"] != MAO:
            return False
        if arm["armId"] == bad2["armId"]:
            ex.account(check_row, arm, "blocked(fixture-not-attempted)", "blocked-prerequisite",
                       "fixture: accounted before any launch, so this arm's answer was never measured")
            return True
        if arm["armId"] == chain2["armId"]:
            ex.account(check_row, arm, "blocked(prerequisite:patch.discovery-installed)", "blocked-prerequisite",
                       "fixture: this arm's prerequisite is blocked by a prerequisite of its own")
            return True
        return False

    expect(evalrun_inprocess(run2, scen, not_attempted), 0, f"{case}/fallback: run exit")
    led2 = ledger_entries(load(run2 / "ledger.json") or {})
    expect(execution_of(led2.get(f"{MAO}__{bad2['armId']}")), "blocked(fixture-not-attempted)",
           f"{case}/fallback: premise - {MAO} not attempted on {bad2['armId']}")
    expect(execution_of(led2.get(f"{MAO}__{chain2['armId']}")), "blocked(prerequisite:patch.discovery-installed)",
           f"{case}/chain: premise - {MAO} is blocked one hop down on {chain2['armId']}")
    expect(execution_of(led2.get(f"{MAO}__{base2['armId']}")), "completed",
           f"{case}/fallback: premise - {MAO} accepted on the baseline")
    m_fb = origin_import(tmp / f"{case}-fallback-fresh", plan2, bad2)[0]
    imp(run2, m_fb, 0, "fallback-accepted")
    # the chain arm is NOT rescued by that accepted baseline: a prerequisite blocked by its own prerequisite on this
    # arm decides for this arm
    m_chain = origin_import(tmp / f"{case}-chain-fresh", plan2, chain2)[0]
    imp(run2, m_chain, 2, "chain-blocked", text=MAO)
    refused_clean(run2, chain2, "chain-blocked", "mcp-only")

    # ---- (f) lifecycle: resume measures the arm's own prerequisite, it fails, and the import stops counting
    expect(evalrun(["resume", "--run", run2], scen).returncode, 0, f"{case}/lifecycle: resume exit")
    led2 = ledger_entries(load(run2 / "ledger.json") or {})
    expect(execution_of(led2.get(f"{MAO}__{bad2['armId']}")), "rejected(illegal-configuration)",
           f"{case}/lifecycle: premise - resume measured {MAO} on {bad2['armId']} and it was refused")
    status, rc, _ = import_state(run2, IMPORT_CHECK, bad2["armId"])
    expect(status, f"blocked(prerequisite:{MAO})", f"{case}/lifecycle: the earlier import is blocked, not reused",
           "V2 L3")
    check(not (rc.get("validation") or {}).get("accepted"), f"{case}/lifecycle: its receipt is no longer accepted")
    blocked_rc = rc
    expect((blocked_rc.get("supersededImport") or {}).get("origin"), "fresh",
           f"{case}/lifecycle: the blocked receipt records the import it replaced", "R1a-1")
    expect(evalrun(["resume", "--run", run2, "--rebuild-ledger"], scen).returncode, 0, f"{case}/rebuild: exit")
    status, rc, _ = import_state(run2, IMPORT_CHECK, bad2["armId"])
    expect(status, f"blocked(prerequisite:{MAO})", f"{case}/rebuild: still blocked after the ledger is rebuilt",
           "V2 L3")
    # the record of the superseded import survives the SECOND and later accounting passes, not only the first
    # (review R2a M1): by now this row has been accounted twice.
    sup2 = rc.get("supersededImport") or {}
    expect(sup2.get("origin"), "fresh",
           f"{case}/rebuild: the superseded import is still recorded after a second accounting pass", "R2a-M1")
    expect(sup2.get("folders"), (blocked_rc.get("supersededImport") or {}).get("folders"),
           f"{case}/rebuild: with the same evidence folders", "R2a-M1")
    expect(evalrun(["finalize", "--run", run2], scen).returncode, 0, f"{case}/lifecycle: finalize exit")
    nulls, missing = nulls_and_missing(run2)
    check((IMPORT_CHECK, bad2["armId"]) not in nulls, f"{case}/lifecycle: not a completed null check")
    expect(missing.get((IMPORT_CHECK, bad2["armId"])), f"blocked(prerequisite:{MAO})",
           f"{case}/lifecycle: listed missing by its prerequisite", "V2 L3")
    lb = tmp / f"{case}-lifecycle-bundle"
    expect(evalrun(["bundle", "--run", run2, "--out", lb], scen).returncode, 0, f"{case}/lifecycle: bundle exit")
    check(not list((lb / "evidence" / "imports").glob(f"{IMPORT_CHECK}__{bad2['armId']}*"))
          if (lb / "evidence" / "imports").exists() else True,
          f"{case}/lifecycle: the uncredited import's files do not travel in the bundle")
    imp(run2, m_fb, 2, "lifecycle-reimport-still-failed", text=MAO)

    # ---- (g) recovery: the arm's prerequisite is legitimately satisfied, and a re-import is credited again
    expect(evalrun(["resume", "--run", run2], scen_ok).returncode, 0, f"{case}/recovery: resume exit")
    led2 = ledger_entries(load(run2 / "ledger.json") or {})
    expect(execution_of(led2.get(f"{MAO}__{bad2['armId']}")), "completed",
           f"{case}/recovery: premise - {MAO} now accepted on {bad2['armId']}")
    imp(run2, m_fb, 0, "recovery-reimport")
    status, rc, _ = import_state(run2, IMPORT_CHECK, bad2["armId"])
    expect(status, "completed", f"{case}/recovery: the re-import is credited", "V2 L3")
    check(any("~2" in (o.get("path") or "") for o in rc.get("outputs") or []),
          f"{case}/recovery: the re-import is copied to a new folder, the old one untouched "
          f"(got {[o.get('path') for o in rc.get('outputs') or []]})")
    expect(evalrun(["finalize", "--run", run2], scen_ok).returncode, 0, f"{case}/recovery: finalize exit")
    nulls, _ = nulls_and_missing(run2)
    check((IMPORT_CHECK, bad2["armId"]) in nulls, f"{case}/recovery: a completed null check again")
    rb, rmoved = tmp / f"{case}-recovery-bundle", tmp / f"{case}-recovery-relocated"
    expect(evalrun(["bundle", "--run", run2, "--out", rb], scen_ok).returncode, 0, f"{case}/recovery: bundle exit")
    shutil.copytree(rb, rmoved)
    expect(evalrun(["verify-bundle", rmoved]).returncode, 0, f"{case}/recovery: relocated verify-bundle exit")

    # ---- (h) an import the resume INVALIDATES (its prerequisite's own evidence is gone) keeps that cause and says an
    # import was superseded, rather than reading as a row where nothing was ever imported (review R1a finding 1)
    run3 = tmp / f"{case}-invalidated"
    plan3 = import_plan(run3, case, f"{case}/invalidated")
    good3 = arm_by_stem(plan3, "candidate-02-reorder")
    expect(evalrun(["run", "--run", run3], scen).returncode, 0, f"{case}/invalidated: run exit")
    m3 = origin_import(tmp / f"{case}-invalidated-fresh", plan3, good3)[0]
    imp(run3, m3, 0, "invalidated-import-accepted")
    for p in (run3 / "outputs").rglob("*.json"):
        if MAO in p.as_posix():
            p.unlink()                      # a cleaned-up run folder: the prerequisite's own emit is gone
    expect(evalrun(["resume", "--run", run3], scen).returncode, 0, f"{case}/invalidated: resume exit")
    status, rc3, _ = import_state(run3, IMPORT_CHECK, good3["armId"])
    check(str(status).startswith("invalidated("),
          f"{case}/invalidated: the entry keeps the invalidation as its cause (got {status!r})")
    sup = rc3.get("supersededImport") or {}
    expect(sup.get("origin"), "fresh", f"{case}/invalidated: the accounting receipt records the superseded import's "
           f"origin", "R1a-1")
    check(any(f"{IMPORT_CHECK}__{good3['armId']}" in f for f in sup.get("folders") or []),
          f"{case}/invalidated: and where its copied evidence is (got {sup.get('folders')})")
    check(not (rc3.get("validation") or {}).get("accepted"), f"{case}/invalidated: it is not an accepted receipt")
    expect(evalrun(["finalize", "--run", run3], scen).returncode, 0, f"{case}/invalidated: finalize exit")
    nulls3, missing3 = nulls_and_missing(run3)
    check((IMPORT_CHECK, good3["armId"]) not in nulls3,
          f"{case}/invalidated: not a completed null check")
    check(str(missing3.get((IMPORT_CHECK, good3["armId"]))).startswith("invalidated("),
          f"{case}/invalidated: listed missing by its real cause (got "
          f"{missing3.get((IMPORT_CHECK, good3['armId']))!r})")
    disp3 = load(run3 / "disposition.json") or {}
    check(not [x for x in disp3.get("importedEvidence") or [] if x.get("armId") == good3["armId"]],
          f"{case}/invalidated: it is not listed as credited imported evidence")
    imp(run3, m3, 0, "invalidated-reimport")     # recovery is a re-import, as the contract says
    status, rc3b, _ = import_state(run3, IMPORT_CHECK, good3["armId"])
    expect(status, "completed", f"{case}/invalidated: the re-import is credited again", "R1a-1")

    # ---- (i) a row the plan did not leave open for an import is refused. The fixture registry has no inapplicable
    # mcp-only check, so the row status is written into the frozen plan (with its hash) - the shape the planner
    # writes for applicability `no`. Synthetic by construction, and labelled as such.
    run4 = tmp / f"{case}-not-applicable"
    plan4 = import_plan(run4, case, f"{case}/not-applicable")
    arm4 = arm_by_stem(plan4, "candidate-02-reorder")
    doc4 = load(run4 / "plan.json")
    for c in doc4["checks"]:
        if c["checkId"] == IMPORT_CHECK:
            for r in c["arms"]:
                r["status"] = "not-applicable"
            c["applicabilityEvidence"] = "fixture: the registry answered applicability no for this scope"
    (run4 / "plan.json").write_text(json.dumps(doc4, indent=1, ensure_ascii=False), encoding="utf-8")
    (run4 / "plan.sha256").write_text(sha_of(run4 / "plan.json") + "\n", encoding="utf-8")
    expect(evalrun(["run", "--run", run4], scen).returncode, 0, f"{case}/not-applicable: run exit")
    m4 = origin_import(tmp / f"{case}-not-applicable-fresh", plan4, arm4)[0]
    r4 = imp(run4, m4, 2, "not-applicable-row", text="not-applicable")
    check("wrong-check" in r4.stderr, f"{case}/not-applicable: refused as wrong-check "
                                      f"(got {r4.stderr.strip()[-200:]!r})")
    status4, rc4, folder4 = import_state(run4, IMPORT_CHECK, arm4["armId"])
    expect(status4, "not-applicable", f"{case}/not-applicable: the plan's answer stands", "R1b-F1")
    check(not (rc4.get("validation") or {}).get("accepted") and not folder4,
          f"{case}/not-applicable: nothing was written")


#: the ledger statuses that mean "this entry currently holds credit" (evalrun.TERMINAL_OK)
CREDITED = ("completed", "reused")


def case_44(tmp):
    """The candidate FILENAME is provenance, not this arm's identity (2026-09-17).

    A declared reuse carries the capture's own `Configuration.Source` - the file the SOURCE arm was measured
    against - because rewriting it would edit the capture's statement of what it measured, the one thing the
    contract says must not be edited. That name alone must not refuse the import when the configuration the emit
    STATES is this arm's frozen request; the difference is recorded as an identity limit, the source name is kept
    as captured, and the reader is told. Nothing else is relaxed: the same envelope declared FRESH is still
    refused by name, a changed configuration or order still refuses on its own cause, a bare `origin: reused`
    never reaches the exception, self-reuse still refuses, and the acceptance survives resume, a ledger rebuild,
    finalize and a relocated bundle while tampered evidence still loses credit. Case 42's reuse envelopes are all
    built by the DESTINATION arm's helper, so they name the destination's candidate and could not have caught
    this; every envelope here is built by the SOURCE arm's helper, as a retained capture really is."""
    case = "case-44-import-reuse-filename"
    plugins = ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"]
    stems1 = {"candidate-00-baseline": plugins, "candidate-01-source": plugins}
    stems2 = {"candidate-00-baseline": plugins,
              # the destination of the cross-evaluation reuse: same configuration, a DIFFERENT filename
              "candidate-01-destination": plugins,
              # a second setup of plan 2, for the sibling reuse whose source is plan 2's own other arm
              "candidate-02-sibling": plugins,
              # a setup whose plugin ORDER differs, so its configuration fingerprint differs
              "candidate-03-other-order": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"],
              # the OTHER arm of the filename conditional: a reuse whose capture names a file with THIS arm's
              # own name, which must be an ordinary reuse with no limit and nothing extra said about it
              "candidate-04-same-name": plugins,
              # destinations for the three inputs that used to skip, or must not trigger, the disclosure
              "candidate-05-no-source": plugins,
              "candidate-06-stem": plugins,
              "candidate-07-cross-unknown": plugins}
    _, scen1 = import_case_dir(case + "-p1", stems1)
    _, scen2 = import_case_dir(case + "-p2", stems2)
    run1, run2 = tmp / f"{case}-run1", tmp / f"{case}-run2"
    plan1 = import_plan(run1, case + "-p1", f"{case}/p1")
    time.sleep(1.1)
    plan2 = import_plan(run2, case + "-p2", f"{case}/p2")
    for run, scen, tag in ((run1, scen1, "p1"), (run2, scen2, "p2")):
        expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/{tag}: run exit", "reuse-filename")
    if not plan1 or not plan2:
        return
    a1 = arm_by_stem(plan1, "candidate-01-source")
    a2 = arm_by_stem(plan2, "candidate-01-destination")
    c2 = arm_by_stem(plan2, "candidate-02-sibling")
    d2 = arm_by_stem(plan2, "candidate-03-other-order")
    for arm, tag in ((a1, "p1-source"), (a2, "p2-destination"), (c2, "p2-sibling"), (d2, "p2-other-order")):
        check(bool(arm), f"{case}: premise - the fixture has a {tag} arm")
    if not (a1 and a2 and c2 and d2):
        return

    def rows(arm):
        return [(p["name"], p["sha256"]) for p in (arm.get("requested") or {}).get("plugins") or []]

    # ---- the premise this case rests on: the arms differ in FILENAME and in nothing identity is made of.
    for other, tag in ((a2, "destination"), (c2, "sibling")):
        check(a1["candidateFile"] != other["candidateFile"],
              f"{case}: premise - source and {tag} candidate FILENAMES differ "
              f"({a1['candidateFile']} vs {other['candidateFile']})")
    for arm, same, tag in ((a2, True, "destination"), (c2, True, "sibling"), (d2, False, "other-order")):
        expect(rows(arm) == rows(a1), same,
               f"{case}: premise - {tag} plugin names and bytes match the source: {same}", "reuse-filename")
    for key in ("providers", "overlays", "population"):
        expect((a2.get("requested") or {}).get(key), (a1.get("requested") or {}).get(key),
               f"{case}: premise - destination {key} equals the source's", "reuse-filename")

    def imp(run, manifest, want, tag, text=None, absent=None):
        """`text` may be one string or several: a refusal that would still be produced with the filename rule
        wrongly relaxed pins nothing on its own, so those controls name the clause that DISAPPEARS too."""
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "reuse-filename")
        for want_text in ([text] if isinstance(text, str) else (text or [])):
            check(want_text in r.stderr, f"{case}/{tag}: the refusal names {want_text!r} "
                                         f"(got {r.stderr.strip()[-300:]!r})")
        if absent:
            check(absent not in r.stderr, f"{case}/{tag}: the refusal does not blame {absent!r} "
                                          f"(got {r.stderr.strip()[-300:]!r})")
        return r

    def untouched(run, arm, tag):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, "mcp-only", f"{case}/{tag}: the refused import leaves the entry mcp-only", "reuse-filename")
        check(not (rc.get("validation") or {}).get("accepted") and not folder,
              f"{case}/{tag}: no accepted receipt and no imports folder after a refusal")

    def limit_row(rc):
        """The identity limit this change records, or None."""
        return next((x for x in (rc.get("validation") or {}).get("identityLimits") or []
                     if isinstance(x, dict) and x.get("capturedAgainst")), None)

    # ---- plan 1 measures the evidence once, freshly, on its own arm.
    src_manifest, src_doc = origin_import(tmp / f"{case}-source", plan1, a1)
    imp(run1, src_manifest, 0, "source-fresh")
    source_sha = sha_of(run1 / "imports" / f"{IMPORT_CHECK}__{a1['armId']}" / "result.json")
    src = {"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}
    src_request = (src_manifest.parent / "request.jsonl").read_text(encoding="utf-8")

    def retained(folder_tag, plan, arm, dest_arm, **kw):
        """A reuse envelope for `dest_arm` whose configuration is built by the SOURCE arm's helper, so it keeps
        the source capture's own `Configuration.Source`. The destination-arm helper cannot produce this shape,
        and a real retained capture always has it."""
        manifest, doc = origin_import(tmp / f"{case}-{folder_tag}", plan, arm, origin="reused",
                                      request=src_request, captured=src_doc["capturedUtc"],
                                      reused_from=kw.pop("reused_from", src))
        doc["planId"], doc["armId"] = plan2["planId"], dest_arm["armId"]
        for k, v in kw.items():
            doc[k] = v
        return write_doc(manifest, doc), doc

    # ---- the accepted case: cross-evaluation reuse whose capture names the SOURCE arm's candidate.
    ok, ok_doc = retained("cross-eval", plan1, a1, a2)
    expect(ok_doc["configuration"]["source"], f"candidate:{a1['candidateFile']}",
           f"{case}: premise - the envelope keeps the SOURCE capture's candidate name", "reuse-filename")
    check(ok_doc["configuration"]["source"] != f"candidate:{a2['candidateFile']}",
          f"{case}: premise - and that is not the destination arm's candidate name")
    expect(import_fingerprint(ok_doc["configuration"]), ok_doc["configuration"]["fingerprint"],
           f"{case}: premise - the envelope's fingerprint is the digest of what it states")
    imp(run2, ok, 0, "cross-eval-retained-source")
    status, rc, folder = import_state(run2, IMPORT_CHECK, a2["armId"])
    expect(status, "completed", f"{case}/cross-eval: ledger completed", "reuse-filename")
    check(folder, f"{case}/cross-eval: the evidence was copied into imports/")
    expect((rc.get("validation") or {}).get("accepted"), True, f"{case}/cross-eval: the receipt is accepted")
    expect((rc.get("validation") or {}).get("rejections"), [], f"{case}/cross-eval: with no rejections")
    # provenance: the capture's own statement survives, unedited
    expect(((rc.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a1['candidateFile']}",
           f"{case}/cross-eval: the receipt keeps the ORIGINAL capture source, not the destination's",
           "reuse-filename")
    lim = limit_row(rc)
    check(lim is not None, f"{case}/cross-eval: the filename difference is recorded as an identity LIMIT")
    if lim:
        expect(lim.get("capturedAgainst"), f"candidate:{a1['candidateFile']}",
               f"{case}/cross-eval: the limit names the capture's own source", "reuse-filename")
        expect(lim.get("armCandidate"), f"candidate:{a2['candidateFile']}",
               f"{case}/cross-eval: and this arm's candidate", "reuse-filename")
        expect((lim.get("declaredSource") or {}).get("planId"), plan1["planId"],
               f"{case}/cross-eval: and the declared source plan")
    imported = rc.get("imported") or {}
    expect(imported.get("origin"), "reused", f"{case}/cross-eval: origin reused")
    expect(imported.get("reusedFrom"), src, f"{case}/cross-eval: the declared source is retained verbatim")
    expect(imported.get("reusedFromVerified"), False, f"{case}/cross-eval: still declared, not verified")
    expect(imported.get("reusedWithin"), "another-evaluation", f"{case}/cross-eval: from another evaluation")
    check("originCheck" not in imported, f"{case}/cross-eval: a reused import claims no capture-token check")

    # ---- the SAME wrong source declared FRESH is still refused by name. This is the both-direction control: one
    #      field of the envelope decides, and the strict rule is untouched for everything that is not a reuse.
    fresh_wrong, fresh_doc = origin_import(tmp / f"{case}-fresh-wrong-source", plan2, c2)
    before_fp = fresh_doc["configuration"]["fingerprint"]
    fresh_doc["configuration"]["source"] = f"candidate:{a1['candidateFile']}"
    expect(import_fingerprint(fresh_doc["configuration"]), before_fp,
           f"{case}: premise - Source is not a fingerprint input, so nothing else changed", "reuse-filename")
    imp(run2, write_doc(fresh_wrong, fresh_doc), 2, "fresh-wrong-source", text="Configuration.Source")
    untouched(run2, c2, "fresh-wrong-source")

    # ---- a bare `origin: reused` with no source declaration never reaches the exception.
    bare, bare_doc = retained("bare-reuse", plan1, a1, c2)
    bare_doc.pop("reusedFrom", None)
    # Both clauses matter: `reusedFrom` shows the origin check refused it, and `Configuration.Source` shows
    # the strict filename rule still ran. With the exception wrongly reached, the second clause disappears and
    # the exit code and the first clause would not notice (review 2, 2026-09-17).
    imp(run2, write_doc(bare, bare_doc), 2, "bare-reused", text=["reusedFrom", "Configuration.Source"])
    untouched(run2, c2, "bare-reused")

    # ---- self-reuse still refuses, retained source or not.
    self_m, self_doc = retained("self-reuse", plan1, a1, c2,
                                reused_from={"planId": plan2["planId"], "armId": c2["armId"],
                                             "resultSha256": source_sha})
    imp(run2, write_doc(self_m, self_doc), 2, "self-reuse", text=["reusedFrom", "Configuration.Source"])
    untouched(run2, c2, "self-reuse")

    # ---- a changed configuration still refuses on its OWN cause, never on the filename: relaxing (f) did not
    #      swallow the content checks.
    bad_m, bad_doc = retained("changed-config", plan1, a1, c2)
    bad_doc["configuration"]["plugins"][0]["sha256"] = "0" * 64
    bad_doc["configuration"]["fingerprint"] = import_fingerprint(bad_doc["configuration"])
    imp(run2, write_doc(bad_m, bad_doc), 2, "changed-configuration", text="identity-mismatch",
        absent="Configuration.Source")
    untouched(run2, c2, "changed-configuration")

    # ---- a changed ORDER (the same bytes) still refuses.
    ord_m, ord_doc = retained("changed-order", plan1, a1, c2)
    ord_doc["configuration"]["plugins"] = list(reversed(ord_doc["configuration"]["plugins"]))
    ord_doc["configuration"]["fingerprint"] = import_fingerprint(ord_doc["configuration"])
    imp(run2, write_doc(ord_m, ord_doc), 2, "changed-order", text="identity-mismatch",
        absent="Configuration.Source")
    untouched(run2, c2, "changed-order")

    # ---- the raw evidence still has to hash to what the envelope says.
    stale_m, _ = retained("changed-evidence", plan1, a1, c2)
    (stale_m.parent / "response.jsonl").write_text(
        '{"jsonrpc": "2.0", "id": 1, "result": {"findings": 9}}\n', encoding="utf-8")
    imp(run2, stale_m, 2, "changed-evidence", text="output-stale")
    untouched(run2, c2, "changed-evidence")

    # ---- a configuration that is a DIFFERENT setup's is refused even with a retained source and a well-formed
    #      declaration: the destination's frozen request decides, not the source's name.
    wrong_dest, wrong_doc = retained("wrong-destination", plan1, a1, c2)
    wrong_doc["configuration"]["plugins"] = [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                                             for p in d2["requested"]["plugins"]]
    wrong_doc["configuration"]["fingerprint"] = import_fingerprint(wrong_doc["configuration"])
    imp(run2, write_doc(wrong_dest, wrong_doc), 2, "wrong-destination-configuration", text="identity-mismatch")
    untouched(run2, c2, "wrong-destination-configuration")

    # ---- the disclosure must not be skippable by shaping or omitting the source string. Both of these were
    #      credited with NOTHING recorded before the disclosure was separated from the refusal predicate: a source
    #      that CONTAINS this arm's candidate stem (the refusal predicate tolerates a substring), and an emit with
    #      no Configuration.Source at all (that predicate needs a truthy source).
    stem_arm = arm_by_stem(plan2, "candidate-06-stem")
    none_arm = arm_by_stem(plan2, "candidate-05-no-source")
    cross_arm = arm_by_stem(plan2, "candidate-07-cross-unknown")
    check(bool(stem_arm) and bool(none_arm) and bool(cross_arm),
          f"{case}: premise - plan 2 has the three extra destinations")
    if stem_arm:
        stem_m, stem_doc = retained("stem-swallow", plan1, a1, stem_arm)
        swallowed = f"candidate:{stem_arm['candidateFile'].rsplit('.', 1)[0]}-alternate.json"
        stem_doc["configuration"]["source"] = swallowed
        check(stem_arm["candidateFile"].rsplit(".", 1)[0].lower() in swallowed.lower()
              and swallowed != f"candidate:{stem_arm['candidateFile']}",
              f"{case}: premise - the source CONTAINS this arm's candidate stem and is still another file")
        imp(run2, write_doc(stem_m, stem_doc), 0, "stem-swallow-reuse")
        _, rc_stem, _ = import_state(run2, IMPORT_CHECK, stem_arm["armId"])
        lim_stem = limit_row(rc_stem)
        check(lim_stem is not None,
              f"{case}/stem-swallow: the difference is recorded even where the refusal predicate tolerates it")
        if lim_stem:
            expect(lim_stem.get("capturedAgainst"), swallowed,
                   f"{case}/stem-swallow: and names the capture's own source", "reuse-filename")
    if none_arm:
        none_m, none_doc = retained("no-source", plan1, a1, none_arm)
        none_doc["configuration"].pop("source", None)
        imp(run2, write_doc(none_m, none_doc), 0, "no-source-reuse")
        _, rc_none, _ = import_state(run2, IMPORT_CHECK, none_arm["armId"])
        stated = [x for x in (rc_none.get("validation") or {}).get("identityLimits") or []
                  if isinstance(x, dict) and "NO candidate source" in str(x.get("detail"))]
        check(bool(stated),
              f"{case}/no-source: a reuse stating no source is credited only with THAT recorded as a limit "
              f"(got {(rc_none.get('validation') or {}).get('identityLimits')!r})")
        check(limit_row(rc_none) is None,
              f"{case}/no-source: and it names no file, because the capture named none")

    # ---- a WITHIN-evaluation source must be a real arm of this plan whose frozen request is this arm's.
    #      Without that, a reader is told the source setup's tested configuration is the same while the plan
    #      itself says it is not. The cross-evaluation case is deliberately not checked this way.
    ghost = retained("ghost-source", plan1, a1, c2,
                     reused_from={"planId": plan2["planId"], "armId": "candidate-99-not-an-arm",
                                  "resultSha256": source_sha})[0]
    imp(run2, ghost, 2, "within-source-not-an-arm", text="no such arm")
    untouched(run2, c2, "within-source-not-an-arm")
    other = retained("other-config-source", plan1, a1, c2,
                     reused_from={"planId": plan2["planId"], "armId": d2["armId"],
                                  "resultSha256": source_sha})[0]
    imp(run2, other, 2, "within-source-other-configuration", text="FROZEN request is not this arm's")
    untouched(run2, c2, "within-source-other-configuration")
    if cross_arm:
        # the same unknown arm id, declared against ANOTHER plan, is accepted: that plan is not read here, and
        # the reader's wording claims correspondingly less.
        cross = retained("cross-unknown-arm", plan1, a1, cross_arm,
                         reused_from={"planId": plan1["planId"], "armId": "candidate-99-not-an-arm",
                                      "resultSha256": source_sha})[0]
        imp(run2, cross, 0, "cross-eval-unknown-arm-allowed")
        _, rc_cross, _ = import_state(run2, IMPORT_CHECK, cross_arm["armId"])
        expect((rc_cross.get("imported") or {}).get("reusedWithin"), "another-evaluation",
               f"{case}/cross-unknown-arm: recorded as reuse from another evaluation", "reuse-filename")

    # ---- sibling-setup reuse with a retained source: the same exception, the other `reusedWithin` statement.
    sib_src = {"planId": plan2["planId"], "armId": a2["armId"],
               "resultSha256": sha_of(run2 / "imports" / f"{IMPORT_CHECK}__{a2['armId']}" / "result.json")}
    sib, sib_doc = retained("sibling", plan2, a2, c2, reused_from=sib_src)
    expect(sib_doc["configuration"]["source"], f"candidate:{a2['candidateFile']}",
           f"{case}: premise - the sibling envelope names the SOURCE setup's candidate", "reuse-filename")
    imp(run2, sib, 0, "sibling-retained-source")
    status_c2, rc_c2, _ = import_state(run2, IMPORT_CHECK, c2["armId"])
    expect(status_c2, "completed", f"{case}/sibling: ledger completed", "reuse-filename")
    expect(((rc_c2.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a2['candidateFile']}",
           f"{case}/sibling: the receipt keeps the source setup's candidate name", "reuse-filename")
    check(limit_row(rc_c2) is not None, f"{case}/sibling: the filename difference is recorded as a limit")
    expect((rc_c2.get("imported") or {}).get("reusedWithin"), "this-evaluation",
           f"{case}/sibling: reuse WITHIN this evaluation", "reuse-filename")

    # ---- the other arm of the filename conditional: a reuse whose capture names a file with the DESTINATION
    #      arm's own name. Rule (f) is satisfied, so there is no limit to record and no reader is told anything
    #      extra - "not measured for this setup" must not be followed by "measured against this setup's own file".
    e2 = arm_by_stem(plan2, "candidate-04-same-name")
    check(bool(e2), f"{case}: premise - plan 2 has a same-name arm")
    if e2:
        same_name, same_doc = origin_import(tmp / f"{case}-same-name", plan2, e2, origin="reused",
                                            request=src_request, captured=src_doc["capturedUtc"],
                                            reused_from=sib_src)
        expect(same_doc["configuration"]["source"], f"candidate:{e2['candidateFile']}",
               f"{case}: premise - this envelope names the DESTINATION arm's own candidate", "reuse-filename")
        imp(run2, same_name, 0, "same-name-reuse")
        status_e2, rc_e2, _ = import_state(run2, IMPORT_CHECK, e2["armId"])
        expect(status_e2, "completed", f"{case}/same-name: an ordinary reuse is accepted", "reuse-filename")
        check(limit_row(rc_e2) is None,
              f"{case}/same-name: no filename limit is recorded when the names agree", )

    # ---- lifecycle: the acceptance survives resume and a ledger rebuild, and finalize reports it as reuse.
    for args, tag in ((["resume", "--run", run2], "resume"),
                      (["resume", "--run", run2, "--rebuild-ledger"], "rebuild-ledger")):
        expect(evalrun(args, scen2).returncode, 0, f"{case}/{tag}: exit", "reuse-filename")
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            st = import_state(run2, IMPORT_CHECK, arm["armId"])[0]
            check(st in CREDITED, f"{case}/{tag}: the {who} reuse still stands (got {st!r})")
        # A credited STATUS is not a surviving RESULT. `finalize` reads the detector from the ledger while
        # reading everything else from the receipt, so an entry that keeps `completed`/`reused` through a
        # reconstruction but loses its detector finalizes as `detector-undetermined` - an accepted import
        # reported as a missing check. The status assertion above cannot see that; this one can, and this is
        # the assertion whose absence let the reconstruction gap ship (Piranha's handback, 2026-09-17).
        expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0,
               f"{case}/{tag}: finalize exit", "reuse-filename")
        disp_after = load(run2 / "disposition.json") or {}
        missing_after = {(m.get("checkId"), m.get("armId"))
                         for m in (disp_after.get("evaluation") or {}).get("missing") or []}
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            check((IMPORT_CHECK, arm["armId"]) not in missing_after,
                  f"{case}/{tag}: after finalize the {who} reuse is NOT reported missing "
                  f"(cause: {next((m.get('cause') for m in (disp_after.get('evaluation') or {}).get('missing') or [] if (m.get('checkId'), m.get('armId')) == (IMPORT_CHECK, arm['armId'])), None)!r})")
        credited_after = {(x.get("checkId"), x.get("armId"))
                          for x in disp_after.get("importedEvidence") or []}
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            check((IMPORT_CHECK, arm["armId"]) in credited_after,
                  f"{case}/{tag}: and it is still listed as credited imported evidence")
    expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0, f"{case}/finalize: exit", "reuse-filename")
    disp_rows = {r.get("armId"): r for r in (load(run2 / "disposition.json") or {}).get("importedEvidence") or []}
    for arm, want_source, who in ((a2, a1["candidateFile"], "cross-eval"), (c2, a2["candidateFile"], "sibling")):
        row = disp_rows.get(arm["armId"]) or {}
        expect(row.get("origin"), "reused", f"{case}/finalize: {who} is listed as reused", "reuse-filename")
        expect(row.get("capturedAgainst"), f"candidate:{want_source}",
               f"{case}/finalize: {who} names the capture's own candidate in the disposition", "reuse-filename")
    if e2:
        row_e2 = disp_rows.get(e2["armId"]) or {}
        expect(row_e2.get("origin"), "reused", f"{case}/finalize: the same-name reuse is listed as reused")
        check("capturedAgainst" not in row_e2,
              f"{case}/finalize: and carries NO capturedAgainst, because there is no difference to report "
              f"(got {row_e2.get('capturedAgainst')!r})")
    status_text = (run2 / "STATUS.md").read_text(encoding="utf-8") if (run2 / "STATUS.md").exists() else ""
    check("not measured for this" in status_text,
          f"{case}/finalize: STATUS.md says the evidence was not measured for the setup")
    check(f"candidate:{a1['candidateFile']}" in status_text,
          f"{case}/finalize: STATUS.md names the capture's own candidate file")
    if e2:
        check(f"measured against `candidate:{e2['candidateFile']}`" not in status_text,
              f"{case}/finalize: STATUS.md never tells a reader the evidence was measured against the very "
              f"setup it also says it was not measured for")

    # ---- the bundle carries it, and a RELOCATED bundle still verifies.
    expect(evalrun(["bundle", "--run", run2], scen2).returncode, 0, f"{case}/bundle: exit", "reuse-filename")
    moved = tmp / f"{case}-bundle-moved"
    shutil.copytree(run2 / "bundle", moved)
    vr = evalrun(["verify-bundle", moved])
    expect(vr.returncode, 0, f"{case}/relocated-bundle: verify exit", "reuse-filename")
    vj = load(moved / "verify.json") or {}
    check((vj.get("bundle") or {}).get("complete") is True,
          f"{case}/relocated-bundle: the relocated bundle verifies complete")
    breceipt = load(moved / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}
    expect(((breceipt.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a1['candidateFile']}",
           f"{case}/relocated-bundle: the bundled receipt still names the original capture source",
           "reuse-filename")

    # ---- and tampered evidence still loses credit, reuse or not. Done LAST, on the sibling entry.
    victim = run2 / "imports" / f"{IMPORT_CHECK}__{c2['armId']}" / "02-response.jsonl"
    check(victim.exists(), f"{case}/tamper: premise - the copied raw response is where the receipt says")
    if victim.exists():
        victim.write_text('{"jsonrpc": "2.0", "id": 1, "result": {"findings": 99}}\n', encoding="utf-8")
        expect(evalrun(["resume", "--run", run2], scen2).returncode, 0, f"{case}/tamper: resume exit")
        st_bad = import_state(run2, IMPORT_CHECK, c2["armId"])[0]
        check(st_bad not in CREDITED and "invalidated" in str(st_bad),
              f"{case}/tamper: the tampered reuse loses credit (got {st_bad!r})")
        st_ok = import_state(run2, IMPORT_CHECK, a2["armId"])[0]
        check(st_ok in CREDITED, f"{case}/tamper: the untouched reuse keeps its credit (got {st_ok!r})")


def case_45(tmp):
    """Gate A review B1 (2026-10-09). conflictdiff refuses repair credit across overlapprobe emits that do not carry
    one shared measurement policy (3c9432e3). One candidate's fake overlapprobe omits the policy while the baseline's
    carries it: that arm's overlap diff is rejected(exit-nonzero) on conflictdiff's exit 3, the diff log carries the
    refusal, and the arm's order evaluation is blocked on the overlap check. The same arm's navownprobe diff and the
    baseline's overlap arm stay accepted (the refusal is this pair's)."""
    case = "case-45-overlap-policy-refused"
    nav, ovl, oe_id = "geometry.navmesh-ownership", "geometry.object-overlap", "patch.order-evaluation"
    h = SweepInProc(tmp, case)
    run = tmp / case
    pl = h.plan(run, ["--select", f"navownprobe,overlapprobe,{oe_id}"])
    if pl is None:
        return
    cand = first_candidate(pl)
    cid, stem = cand["armId"], Path(cand["candidateFile"]).stem
    arms = (load(h.scen) or {}).get("arms", {}).get("overlapprobe", {})
    entry = dict(arms.get(stem) or arms.get("*") or {"behaviour": "ok"})
    entry["measurementPolicy"] = None
    scen = h.scenario_with(tmp / f"{case}.scenario.json", "overlapprobe", stem, entry)
    expect(h.inproc(["run", "--run", run], scenario=scen), 0, f"{case}: premise - the run exits 0")
    inv = last_sweep(run)
    rec = {}
    for ln in text_lines(inv / "jobs.jsonl"):
        if json.loads(ln)["job"] == f"diff-overlapprobe-{stem}":
            rec = json.loads(ln)
    check(rec.get("outcome") == "ran" and rec.get("exitCode") == 3,
          f"{case}: conflictdiff ran the overlap diff and exited 3 ({rec.get('outcome')}, {rec.get('exitCode')})")
    log = "\n".join(text_lines(inv / "logs" / f"diff-overlapprobe-{stem}.txt"))
    refusal = [ln.strip() for ln in log.splitlines() if "measurement polic" in ln]
    check(any("REFUSED" in ln for ln in refusal),
          f"{case}: the diff log carries conflictdiff's measurement-policy refusal ({refusal})")
    rc = load(run / "receipts" / f"{ovl}__{cid}__diff.json") or {}
    causes = [x.get("cause") for x in (rc.get("validation") or {}).get("rejections") or []]
    check((rc.get("validation") or {}).get("accepted") is False and "exit-nonzero" in causes,
          f"{case}: the overlap diff receipt is rejected for its exit code (causes {causes})")
    # task 2026-10-09-tooling-follow-ups item 5: the receipt itself carries the reason, not only the diff log
    details = " ".join(str(x.get("detail")) for x in (rc.get("validation") or {}).get("rejections") or [])
    missing = [w for w in ("measurement policy: base 'fixture-overlap-v1', changed 'unknown'",
                           "cannot establish repair credit", "REFUSED") if w not in details]
    check(not missing, f"{case}: the overlap diff rejection carries conflictdiff's measurement-policy refusal, "
                       f"missing {missing} ({details[:400]})")
    led = ledger_entries(load(run / "ledger.json") or {})
    expect(execution_of(led.get(f"{ovl}__{cid}__diff")), "rejected(exit-nonzero)",
           f"{case}: the overlap diff's ledger status")
    expect(execution_of(led.get(f"{oe_id}__{cid}")), f"blocked(prerequisite:{ovl})",
           f"{case}: the arm's order evaluation is blocked on the overlap check")
    expect(execution_of(led.get(f"{nav}__{cid}__diff")), "completed",
           f"{case}: control - the same arm's navownprobe diff is accepted")
    rb = load(run / "receipts" / f"{ovl}__baseline__arm.json") or {}
    check((rb.get("validation") or {}).get("accepted") is True,
          f"{case}: control - the baseline's overlap arm is accepted")


CASES["case-42"] = case_42
CASES["case-43"] = case_43
CASES["case-44"] = case_44
CASES["case-45"] = case_45


def _suite_copy(root: Path) -> Path:
    """A minimal checkout copy: this suite plus conflictdiff's project and every compile input its csproj links.
    Returns the copy's `_geometry-harness`."""
    import xml.etree.ElementTree as ET
    h = root / "_geometry-harness"
    (h / "tests").mkdir(parents=True)
    shutil.copy2(Path(__file__).resolve(), h / "tests" / "test_evalrun.py")
    proj, dst = HARNESS / "conflictdiff", h / "conflictdiff"
    dst.mkdir()
    for f in [proj / "conflictdiff.csproj"] + sorted(proj.glob("*.cs")):
        shutil.copy2(f, dst / f.name)
    for item in ET.parse(proj / "conflictdiff.csproj").getroot().iter("Compile"):
        rel = item.get("Include").replace("\\", "/")
        out = (dst / rel).resolve()
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2((proj / rel).resolve(), out)
    if (HARNESS.parent / "nuget.config").exists():
        shutil.copy2(HARNESS.parent / "nuget.config", root / "nuget.config")
    return h


def case_46(tmp):
    """Lane C (2026-10-10, task 2026-10-09-evalreport-r4-overlap-fixture-failures, decision B2). The suite never
    tests a conflictdiff older than its own source. In a copy of the checkout with a planted stale conflictdiff on
    the harness path: with no HOUSECARL_EVALRUN_TEST_HARNESS the suite builds conflictdiff from the copy's source
    for this run and says so, never the plant; an explicit HOUSECARL_EVALRUN_TEST_HARNESS is honoured and labelled
    as explicit; and when the build fails the suite refuses in one sentence instead of falling back to the plant."""
    import re
    from datetime import datetime
    case = "case-46-suite-builds-conflictdiff"
    root = tmp / case
    h = _suite_copy(root / "checkout")
    exe_name = "conflictdiff.exe" if os.name == "nt" else "conflictdiff"
    planted = h / "conflictdiff" / "bin" / "Release" / "net9.0"
    planted.mkdir(parents=True)
    for f in (exe_name, "conflictdiff.dll"):
        (planted / f).write_bytes(b"planted stale conflictdiff\n")
        os.utime(planted / f, (946684800, 946684800))                        # 2000-01-01, older than any source
    plant_sha = hashlib.sha256(b"planted stale conflictdiff\n").hexdigest()
    sources = [p for p in (root / "checkout").rglob("*") if p.suffix in (".cs", ".csproj") and "bin" not in p.parts]
    newest_source = max(p.stat().st_mtime for p in sources)
    csproj = h / "conflictdiff" / "conflictdiff.csproj"
    # The suite's own main(), run from the copy; only fixture generation is stubbed (it is not what is under test).
    stub = root / "stub-fixtures"
    stub.mkdir()
    (stub / "make_fixtures.py").write_text("import sys\nsys.exit(0)\n", encoding="utf-8")
    driver = root / "driver.py"
    driver.write_text("import sys\nfrom pathlib import Path\nsys.path.insert(0, sys.argv[1])\n"
                      "import test_evalrun as T\nT.FIXT = Path(sys.argv[2])\n"
                      "sys.argv = ['test_evalrun.py', 'zz-no-such-case']\nraise SystemExit(T.main())\n",
                      encoding="utf-8")

    def suite(explicit: Path | None):
        env = {k: v for k, v in os.environ.items() if k != "HOUSECARL_EVALRUN_TEST_HARNESS"}
        if explicit is not None:
            env["HOUSECARL_EVALRUN_TEST_HARNESS"] = str(explicit)
        env["PYTHONIOENCODING"] = "utf-8"
        r = subprocess.run([sys.executable, str(driver), str(h / "tests"), str(stub)], capture_output=True,
                           text=True, encoding="utf-8", errors="replace", env=env, timeout=900)
        out = r.stdout + r.stderr
        rows = {}
        for line in out.splitlines():
            m = re.match(r"\s*conflictdiff: (.+?)  sha256 ([0-9a-f]{64})  mtime (.+)$", line)
            if m:
                rows[Path(m.group(1)).name.lower()] = (Path(m.group(1)), m.group(2), m.group(3).strip())
        return r.returncode, out, rows

    # 1. No explicit harness: this run's own build from the copy's source, labelled, never the plant.
    code, out, rows = suite(None)
    exe, dll = rows.get(exe_name), rows.get("conflictdiff.dll")
    check("no case matches" in out,
          f"{case}: premise - with no explicit harness the suite reached its cases (exit {code})"
          + ("" if "no case matches" in out else f": {out[-400:]!r}"))
    check(exe is not None and exe[0] != planted / exe_name and exe[1] != plant_sha,
          f"{case}: with no explicit harness the suite does not use the stale conflictdiff on the harness path "
          f"(header names {exe[0] if exe else None})")
    check(exe is not None and planted not in exe[0].parents and h not in exe[0].parents,
          f"{case}: the conflictdiff used was built outside the checkout's bin folders ({exe[0] if exe else None})")
    when = None
    if dll:
        try:
            when = datetime.strptime(dll[2], "%Y-%m-%d %H:%M:%S %z").timestamp()
        except ValueError:
            when = None
    check(when is not None and when >= int(newest_source),
          f"{case}: the conflictdiff used is not older than its newest compile input "
          f"({dll[2] if dll else None} vs source {datetime.fromtimestamp(newest_source)})")
    check(f"conflictdiff source: built from {csproj}" in out,
          f"{case}: the header says conflictdiff was built from this checkout's {csproj}")

    # 2. An explicit harness is honoured as given (reproducing an old run) and labelled as such.
    code, out, rows = suite(h)
    exe = rows.get(exe_name)
    check(exe is not None and exe[0] == planted / exe_name and exe[1] == plant_sha,
          f"{case}: an explicit HOUSECARL_EVALRUN_TEST_HARNESS is used as given (header names "
          f"{exe[0] if exe else None})")
    check(f"conflictdiff source: explicit harness HOUSECARL_EVALRUN_TEST_HARNESS={h}" in out,
          f"{case}: the header labels the explicit harness and says it was not built from this checkout")

    # 3. The build fails: one-sentence refusal, no fallback to the plant, no case runs.
    prog = h / "conflictdiff" / "Program.cs"
    prog.write_text(prog.read_text(encoding="utf-8") + "\nthis line is not C#\n", encoding="utf-8")
    code, out, rows = suite(None)
    refusals = [ln for ln in out.splitlines() if "conflictdiff did not build" in ln]
    check(code == 1, f"{case}: a failed conflictdiff build fails the suite (exit {code})")
    check(len(refusals) == 1,
          f"{case}: a failed build is refused in one sentence naming conflictdiff's build ({refusals or out[-400:]!r})"[:600])
    check(exe_name not in rows and "no case matches" not in out,
          f"{case}: after a failed build the suite neither falls back to the planted conflictdiff nor runs a case")


CASES["case-46"] = case_46


def case_47(tmp):
    """Defect D-1 (task 2026-10-14-evalrun-qualification-by-hash, test-hardening row B7). In-process through
    `run_derived`: a supplied plugin is `qualified` only when the sha256 its arm's request records equals the
    sha256 of a plugin an ACCEPTED receipt consumed; the file name is a label. Each scenario is its own run."""
    import importlib.util
    case = "case-47-qualification-by-hash"
    spec = importlib.util.spec_from_file_location("evalrun_case47", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    h_old, h_new, h_x = "a1" * 32, "b2" * 32, "c3" * 32

    class Stub:
        def __init__(self, run, arms):
            self.run, self.plan, self.calls, self.extra = run, {"arms": arms, "checks": []}, [], None

        def account(self, check, arm, status, cause, detail, **kw):
            self.calls.append(status)

        def finish(self, *a, extra=None, **kw):
            self.extra = extra

    def supplied(aid, files, requested):
        return {"armId": aid, "kind": "supplied-combination", "supplied": {"declaredFiles": files},
                "requested": {"plugins": requested}}

    def req(name, sha):
        row = {"name": name, "sha256": sha, "path": None, "hashSource": "instance:mod" if sha else "unstated"}
        if not sha:
            row["unstatedReason"] = "no --instance was given, so the plan cannot resolve " + name
        return row

    def receipt(rid, aid, accepted, plugins):
        return {"receiptId": rid, "armId": aid, "validation": {"accepted": accepted},
                "consumed": {"configuration": {"plugins": [{"name": n, "sha256": s} for n, s in plugins]}}}

    base_arm = {"armId": "baseline", "kind": "baseline"}
    base_ok = receipt("r-base", "baseline", True, [("Skyrim.esm", "00" * 32), ("Patch.esp", h_old)])
    scenarios = [
        # B7: same name as the baseline's plugin, different bytes, own arm's receipts all rejected
        ("differs", supplied("supplied-01-newer", ["Patch.esp"], [req("Patch.esp", h_new)]),
         [base_ok, receipt("r-s1", "supplied-01-newer", False, [("Patch.esp", h_new)])],
         "unqualified", h_new[:12]),
        # its own bytes were consumed by its own accepted receipt
        ("own-bytes-accepted", supplied("supplied-01-newer", ["Patch.esp"], [req("Patch.esp", h_new)]),
         [base_ok, receipt("r-s1", "supplied-01-newer", True, [("Patch.esp", h_new)])],
         "qualified", h_new[:12]),
        # the same bytes the baseline's accepted receipt consumed
        ("same-bytes", supplied("supplied-01-same", ["Patch.esp"], [req("Patch.esp", h_old)]),
         [base_ok, receipt("r-s1", "supplied-01-same", False, [("Patch.esp", h_old)])],
         "qualified", h_old[:12]),
        # a file only its own arm consumed, and every receipt of that arm rejected
        ("own-arm-rejected", supplied("supplied-01-only", ["NewPatch.esp"], [req("NewPatch.esp", h_x)]),
         [base_ok, receipt("r-s1", "supplied-01-only", False, [("NewPatch.esp", h_x)])],
         "unqualified", h_x[:12]),
        # the request records no hash for it: never qualified by default
        ("no-recorded-hash", supplied("supplied-01-unstated", ["Patch.esp"], [req("Patch.esp", None)]),
         [base_ok], "unqualified", "no content hash"),
        # the request does not list the declared file at all
        ("not-in-request", supplied("supplied-01-absent", ["Patch.esp"], [req("Other.esp", h_x)]),
         [base_ok], "unqualified", "no content hash"),
    ]
    check_def = {"checkId": "patch.candidate-qualification", "adapter": {"kind": "derived",
                                                                         "derives": "candidate-qualification"}}
    for tag, arm, receipts, want, why_has in scenarios:
        run = tmp / case / tag
        (run / "receipts").mkdir(parents=True)
        for r in receipts:
            (run / "receipts" / f"{r['receiptId']}.json").write_text(json.dumps(r), encoding="utf-8")
        (run / "candidates").mkdir()
        (run / "candidates" / "candidates.json").write_text(
            json.dumps({"Candidates": [{"File": "candidate-00-baseline.json"}]}), encoding="utf-8")
        stub = Stub(run, [base_arm, arm])
        mod.run_derived(stub, check_def, base_arm)
        rows = ((stub.extra or {}).get("derived") or {}).get("candidates")
        got = [(r.get("file"), r.get("armId"), r.get("qualification")) for r in rows or []]
        expect(got, [(arm["supplied"]["declaredFiles"][0], arm["armId"], want),
                     ("candidate-00-baseline.json", None, "catalogued")],
               f"{case}/{tag}: exact rows (file, arm, qualification)", "D7 / B7")
        why = next((r.get("why") or "" for r in rows or [] if r.get("armId") == arm["armId"]), "")
        check(why_has in why and "sha256" in why,
              f"{case}/{tag}: the why names what was compared ({why_has!r} in {why!r})")


CASES["case-47"] = case_47


def record_transcript(folder: Path, plan: dict, arm: dict, formid: str = "000801:Skyrim.esm"):
    """A record capture in the import shape: one batch, three of the arm's plugins read for one record, the second
    plugin's change to LocationReference lost to the third's. Returns (request, response, the three plugin names)."""
    folder.mkdir(parents=True, exist_ok=True)
    three = arm["requested"]["plugins"][:3]
    vals = ["02008A:Skyrim.esm", "0A0001:Skyrim.esm", "0B0001:Skyrim.esm"]
    req = [{"batchMarker": "rec-s00", "sourceFile": "raw/rec-s00.request.jsonl", "sourceSha256": "0" * 64},
           initialize(f"evalrun:{plan['planId']}:{arm['armId']}")]
    resp = [{"batchMarker": "rec-s00", "sourceFile": "raw/rec-s00.response.jsonl", "sourceSha256": "0" * 64},
            {"jsonrpc": "2.0", "id": 0, "result": {"serverInfo": {"name": "houseCARL"}}}]
    for i, (pl, v) in enumerate(zip(three, vals), start=1):
        req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {
            "name": "housecarl_read_plugin_file",
            "arguments": {"plugin": pl["path"], "formid": formid, "format": "text", "depth": 3}}})
        text = (f"read_plugin_file\nfile: {pl['path']}\ntype=PlacedObject  formid={formid}  editorid=<none>\n"
                f"fields (raw, from {pl['name']}):\n  FormKey = {formid}\n  FormVersion = 44\n"
                f"  LocationReference = {v}\n")
        resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": text}]}})
    request, response = folder / "request.jsonl", folder / "response.jsonl"
    request.write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
    response.write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
    return request, response, [x["name"] for x in three]


def record_import_doc(folder: Path, plan: dict, cid: str, arm: dict, rows: Path | None = None):
    request, response, names = record_transcript(folder, plan, arm)
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": req["providers"], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    outs = [("request", request), ("response", response)] + ([("rows", rows)] if rows else [])
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"],
           "state": "baseline", "origin": "fresh", "capturedUtc": datetime_now_z(),
           "tool": {"name": "fixture record capture", "version": "1"}, "configuration": cfg,
           "result": {"detector": "finding", "findings": 1,
                      "coverage": [{"Population": "contested PlacedObject records", "Judged": 1, "Unjudgeable": {}}]},
           "outputs": [{"role": role, "path": q.name, "sha256": sha_of(q)} for role, q in outs]}
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest, names


def recorded_bundle(tmp: Path, tag: str):
    """case-01's evaluation with one record import credited on its first mcp-only arm, finalized and bundled."""
    case = "case-01-geometry-and-quest"
    run = tmp / f"{tag}-run"
    expect(do_plan(run, case).returncode, 0, f"{tag}: plan exit", "setup")
    _, _, _, scen = case_paths(case)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{tag}: accounting run", "setup")
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((c for c in ("records.contested-fields", "records.cell-attributes", "loadability.dangling-references")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, f"{tag}: fixture registry has an mcp-only record check")
    arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or []) if x.get("status") == "mcp-only"]
    arm = next(a for a in plan.get("arms") or [] if a.get("armId") in arm_ids)
    m, names = record_import_doc(tmp / f"{tag}-capture", plan, cid, arm)
    expect(evalrun(["import-results", "--run", run, "--file", m]).returncode, 0, f"{tag}: record import", "setup")
    expect(evalrun(["finalize", "--run", run]).returncode, 0, f"{tag}: finalize", "setup")
    bundle = tmp / f"{tag}-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0, f"{tag}: bundle", "setup")
    return bundle, plan, cid, arm, names


def manifest_rows(bundle: Path, kind: str):
    return [e for e in (load(bundle / "manifest.json") or {}).get("evidence") or []
            if e.get("kind") == "enrichment" and e.get("enrichment") == kind]


def rehash_manifest_row(bundle: Path, rel: str) -> None:
    m = load(bundle / "manifest.json")
    for e in m.get("evidence") or []:
        if e.get("path") == rel:
            e["sha256"] = sha_of(bundle / rel)
            e["bytes"] = (bundle / rel).stat().st_size
    (bundle / "manifest.json").write_text(json.dumps(m, indent=2), encoding="utf-8")


def case_61(tmp):
    """C1 (4.24 phase 1): `enrich --kind record-rows` derives rows from the bundle's own import transcripts, every row
    citing the bytes it came from; it changes no disposition, claim or count, and verify-bundle checks all of that."""
    case = "case-61-enrich-record-rows"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    disp_before = (bundle / "disposition.json").read_bytes()
    b1 = tmp / f"{case}-b1"
    shutil.copytree(bundle, b1)
    r = evalrun(["enrich", "--bundle", b1, "--kind", "record-rows"])
    expect(r.returncode, 0, f"{case}: enrich exit", "C1")
    rows = [e for e in manifest_rows(b1, "record-rows") if e.get("checkId") == cid and e.get("armId") == arm["armId"]]
    expect(len(rows), 1, f"{case}: one enrichment row for the imported check and arm", "C1")
    for e in rows:
        src = e.get("derivedFrom") or []
        check(bool(src) and all((b1 / d["path"]).is_file() and sha_of(b1 / d["path"]) == d.get("sha256") for d in src),
              f"{case}: every source the enrichment cites is a bundle file with that sha256 ({src})")
        emit = load(b1 / e["path"]) or {}
        lost = [c for c in emit.get("Conflicts") or [] if c.get("Kind") == "LOST-EDIT"]
        expect([(c.get("A"), c.get("B"), c.get("Field")) for c in lost], [(names[1], names[2], "LocationReference")],
               f"{case}: the derived row", "C1")
    check((b1 / "disposition.json").read_bytes() == disp_before, f"{case}: the disposition is byte for byte unchanged")
    expect(evalrun(["verify-bundle", b1]).returncode, 0, f"{case}: the enriched copy verifies", "C1")
    again = evalrun(["enrich", "--bundle", b1, "--kind", "record-rows"])
    expect(again.returncode, 2, f"{case}: a second enrichment of the same kind is refused", "C1")
    check("already" in again.stderr and "record-rows" in again.stderr,
          f"{case}: the refusal says the bundle is already enriched ({again.stderr.strip()[-160:]!r})")
    if not rows:
        check(False, f"{case}: no enrichment row to tamper with, so the three verification arms did not run")
        return
    rel = rows[0]["path"]
    # a changed byte
    b2 = tmp / f"{case}-b2"
    shutil.copytree(b1, b2)
    (b2 / rel).write_bytes((b2 / rel).read_bytes() + b" ")
    expect(evalrun(["verify-bundle", b2]).returncode, 3, f"{case}: a changed enrichment byte fails verification", "C1")
    # a row without its source, re-hashed so only the missing source can be what verify refuses
    b3 = tmp / f"{case}-b3"
    shutil.copytree(b1, b3)
    emit = load(b3 / rel)
    emit.pop("derivedFrom", None)
    for c in emit.get("Conflicts") or []:
        for n in c.get("Necessary") or []:
            n.pop("Reads", None)
    (b3 / rel).write_text(json.dumps(emit), encoding="utf-8")
    rehash_manifest_row(b3, rel)
    v3 = evalrun(["verify-bundle", b3])
    expect(v3.returncode, 3, f"{case}: an enrichment row without its source fails verification", "C1")
    # a count changed after enrichment: the disposition no longer equals the one the enrichment was derived beside
    b4 = tmp / f"{case}-b4"
    shutil.copytree(b1, b4)
    d = load(b4 / "disposition.json")
    for f in (d.get("evaluation") or {}).get("findings") or []:
        if f.get("checkId") == cid:
            f["findings"] = int(f.get("findings") or 0) + 5
    (b4 / "disposition.json").write_text(json.dumps(d, indent=2), encoding="utf-8")
    rehash_manifest_row(b4, "disposition.json")
    expect(evalrun(["verify-bundle", b4]).returncode, 3,
           f"{case}: a count changed beside an enrichment fails verification", "C1")


def case_63(tmp):
    """C1 (4.24 phase 1): `enrich --kind names` reads only plugin files whose bytes equal the bundle's recorded hash,
    refusing by name otherwise, and records the source of every name (a stand-in reader answers the record reads)."""
    case = "case-63-enrich-names"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    fake = FIXT / "fake_records_mcp.py"
    reader_cmd = f"{sys.executable} {fake}"
    b1 = tmp / f"{case}-b1"
    shutil.copytree(bundle, b1)
    target_name = (plan.get("targets") or [{}])[0].get("name")
    r = evalrun(["enrich", "--bundle", b1, "--kind", "names", "--reader-cmd", reader_cmd,
                 "--mod-name", f"{target_name}=Declared Name"])
    expect(r.returncode, 0, f"{case}: names enrich exit ({r.stderr.strip()[-200:]})", "C1")
    row = next(iter(manifest_rows(b1, "names")), None)
    check(row is not None, f"{case}: a names enrichment row is in the manifest")
    doc = load(b1 / row["path"]) if row else {}
    mods = (doc or {}).get("mods") or {}
    expect((mods.get(target_name) or {}).get("source"), "declared", f"{case}: a declared mod name says so", "Q6")
    check(all((m or {}).get("source") in ("declared", "nexus-catalogue", "plugin-name") for m in mods.values()),
          f"{case}: every mod name has an offline source ({mods})")
    src = (doc or {}).get("derivedFrom") or []
    check(bool(src) and all(d.get("sha256") for d in src), f"{case}: every plugin read is cited with its sha256")
    capture = [e for e in (load(b1 / "manifest.json") or {}).get("evidence") or []
               if str(e.get("path") or "").startswith("enrichment/names/") and e.get("path") != (row or {}).get("path")]
    check(bool(capture), f"{case}: the record reads' request and response travel with the names")
    expect(evalrun(["verify-bundle", b1]).returncode, 0, f"{case}: the enriched copy verifies", "C1")
    # a plugin whose bytes on disk differ from the recorded hash: refused before any read, naming the plugin
    # an evaluated mod's file: the names enrichment always reads the evaluated mods
    target = next(Path(p["path"]) for p in arm["requested"]["plugins"]
                  if p.get("name") == target_name and p.get("path") and Path(p["path"]).is_file())
    original = target.read_bytes()
    b2 = tmp / f"{case}-b2"
    shutil.copytree(bundle, b2)
    try:
        target.write_bytes(original + b"\0")
        r2 = evalrun(["enrich", "--bundle", b2, "--kind", "names", "--reader-cmd", reader_cmd])
        expect(r2.returncode, 2, f"{case}: a plugin with other bytes is refused", "C1")
        check(target.name in r2.stderr, f"{case}: the refusal names the plugin ({r2.stderr.strip()[-200:]!r})")
        check(not manifest_rows(b2, "names"), f"{case}: nothing was written on refusal")
    finally:
        target.write_bytes(original)
    check(sha_of(target) == hashlib.sha256(original).hexdigest(), f"{case}: the plugin was restored by content")


def case_65(tmp):
    """4.24 phase 1: an import may carry a third output, `rows`, in the conflict-file shape; rows naming a plugin that
    is not in the arm are refused; an accepted rows output travels in the bundle."""
    case = "case-65-import-rows-output"
    c = "case-01-geometry-and-quest"
    run = tmp / f"{case}-run"
    expect(do_plan(run, c).returncode, 0, f"{case}: plan exit", "setup")
    _, _, _, scen = case_paths(c)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: accounting run", "setup")
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((x for x in ("records.contested-fields", "records.cell-attributes", "loadability.dangling-references")
                if ((checks.get(x) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or []) if x.get("status") == "mcp-only"]
    arms = [a for a in plan.get("arms") or [] if a.get("armId") in arm_ids]
    names = [p["name"] for p in arms[0]["requested"]["plugins"]]

    def rows_file(folder, a, b):
        folder.mkdir(parents=True, exist_ok=True)
        f = folder / "rows.json"
        f.write_text(json.dumps({"Population": "contested records", "State": "baseline",
                                 "Coverage": [{"Population": "contested records", "Judged": 1, "Unjudgeable": {}}],
                                 "Conflicts": [{"Kind": "LOST-EDIT", "A": a, "B": b, "Field": "LocationReference",
                                                "Summary": "x", "Necessary": [{"FormKey": "000801:Skyrim.esm"}]}]}),
                     encoding="utf-8")
        return f

    bad_dir = tmp / f"{case}-bad"
    bad, _ = record_import_doc(bad_dir, plan, cid, arms[0], rows=rows_file(bad_dir, names[1], "NotInTheArm.esp"))
    rb = evalrun(["import-results", "--run", run, "--file", bad])
    expect(rb.returncode, 2, f"{case}: rows naming a plugin outside the arm are refused", "§4.8 rows")
    check("NotInTheArm.esp" in rb.stderr, f"{case}: the refusal names the plugin ({rb.stderr.strip()[-200:]!r})")
    good_dir = tmp / f"{case}-good"
    good, _ = record_import_doc(good_dir, plan, cid, arms[0], rows=rows_file(good_dir, names[1], names[2]))
    expect(evalrun(["import-results", "--run", run, "--file", good]).returncode, 0,
           f"{case}: an import with a rows output is accepted", "§4.8 rows")
    rc = load(run / "receipts" / f"{cid}__{arms[0]['armId']}.json") or {}
    expect(len(rc.get("outputs") or []), 4, f"{case}: the manifest, request, response and rows are retained", "§4.8")
    expect(evalrun(["finalize", "--run", run]).returncode, 0, f"{case}: finalize", "§4.8")
    bundle = tmp / f"{case}-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0, f"{case}: bundle", "§4.8")
    check(any(str(e.get("path") or "").endswith("rows.json") for e in (load(bundle / "manifest.json") or {})
              .get("evidence") or []), f"{case}: the rows output is in the bundle")
    expect(evalrun(["verify-bundle", bundle]).returncode, 0, f"{case}: verify-bundle", "§4.8")


CASES["case-61"] = case_61
CASES["case-63"] = case_63
CASES["case-65"] = case_65


def _fixture_sub(sig: bytes, data: bytes) -> bytes:
    return sig + struct.pack("<H", len(data)) + data


def _fixture_rec(sig: bytes, fid: int, body: bytes, flags: int = 0) -> bytes:
    return sig + struct.pack("<IIIIHH", len(body), flags, fid, 0, 44, 0) + body


def _fixture_grup(label: bytes, gtype: int, content: bytes) -> bytes:
    return b"GRUP" + struct.pack("<I", 24 + len(content)) + label + struct.pack("<iHHI", gtype, 0, 0, 0) + content


def _fixture_plugin(records: int, tree: bytes) -> bytes:
    """A hand-written SSE plugin: a TES4 header with no masters, then the given group tree."""
    hedr = _fixture_sub(b"HEDR", struct.pack("<fiI", 1.71, records, 0x900)) + _fixture_sub(b"CNAM", b"fixture\0")
    return _fixture_rec(b"TES4", 0, hedr) + tree


def _fixture_instance(root: Path, plugin_name: str, data: bytes) -> list:
    """A scratch MO2 instance (profile P, one mod folder holding the plugin, an empty game Data folder); returns the
    probe flags that name it, so no real instance is read."""
    inst = root / "inst"
    (inst / "profiles" / "P").mkdir(parents=True, exist_ok=True)
    (inst / "mods" / "Fixture").mkdir(parents=True, exist_ok=True)
    (inst / "overwrite").mkdir(exist_ok=True)
    (root / "game" / "Data").mkdir(parents=True, exist_ok=True)
    (inst / "ModOrganizer.ini").write_text("[General]\nselected_profile=@ByteArray(P)\n", encoding="utf-8")
    (inst / "mods" / "Fixture" / plugin_name).write_bytes(data)
    (inst / "profiles" / "P" / "plugins.txt").write_text(f"*{plugin_name}\n", encoding="utf-8")
    (inst / "profiles" / "P" / "loadorder.txt").write_text(f"{plugin_name}\n", encoding="utf-8")
    (inst / "profiles" / "P" / "modlist.txt").write_text("+Fixture\n", encoding="utf-8")
    return ["--instance", str(inst), "--game", str(root / "game"), "--profile", "P"]


def _build_probe(tmp: Path, probe: str):
    """`probe` built from this checkout into an empty folder under `tmp`: (exe, None), or (None, one sentence why not).
    Never an older binary."""
    out = Path(tmp) / "probe-build" / probe
    exe = out / (f"{probe}.exe" if os.name == "nt" else probe)
    if exe.exists():
        return exe, None
    csproj = HARNESS / probe / f"{probe}.csproj"
    r = subprocess.run(["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
                        "--artifacts-path", str(Path(tmp) / "probe-build" / "artifacts" / probe), "-o", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    if r.returncode != 0 or not exe.exists():
        tail = "\n".join((r.stdout + r.stderr).strip().splitlines()[-8:])
        return None, f"{probe} did not build from {csproj} (exit {r.returncode}): {tail}"
    return exe, None


def _run_fixture_probe(exe: Path, args: list, root: Path):
    env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(root / "runlog"))
    env.pop("HOUSECARL_HARNESS_CONFIG", None)
    return subprocess.run([str(exe)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", env=env, timeout=600)


def _land_world_plugin(land_body: bytes, land_flags: int = 0) -> bytes:
    """One worldspace (0x800), one exterior cell at grid 0,0 (0x801), one LAND (0x802) with the given body and flags:
    the D-3 measurement's fixture (runs/2026-10-14-d2-d3-fixes/experiment/plugbytes.py)."""
    fid = lambda n: struct.pack("<I", n)  # noqa: E731
    wrld = _fixture_rec(b"WRLD", 0x800, _fixture_sub(b"EDID", b"FixtureWorld\0"))
    cell = _fixture_rec(b"CELL", 0x801, _fixture_sub(b"EDID", b"FixtureCell\0") + _fixture_sub(b"DATA", struct.pack("<H", 0))
                        + _fixture_sub(b"XCLC", struct.pack("<iiI", 0, 0, 0)))
    land = _fixture_rec(b"LAND", 0x802, land_body, land_flags)
    xy = struct.pack("<hh", 0, 0)
    tree = _fixture_grup(b"WRLD", 0, wrld + _fixture_grup(fid(0x800), 1, _fixture_grup(xy, 4, _fixture_grup(xy, 5,
                         cell + _fixture_grup(fid(0x801), 6, _fixture_grup(fid(0x801), 9, land))))))
    return _fixture_plugin(3, tree)


COMPRESSED = 0x40000


def _land_record_overrun() -> bytes:
    """The well-formed LAND fixture with the LAND's record length raised past the end of its group."""
    good = _land_world_plugin(_fixture_sub(b"DATA", struct.pack("<I", 0)))
    i = good.rfind(b"LAND")
    return good[:i + 4] + struct.pack("<I", struct.unpack_from("<I", good, i + 4)[0] + 40) + good[i + 8:]


def _corrupt_kinds() -> dict:
    """Each corruption kind: (plugin bytes, signature and FormKey of the record that cannot be read)."""
    import zlib
    return {
        # the compressed flag over bytes that are not zlib
        "land-badzlib": (_land_world_plugin(struct.pack("<I", 64) + b"not zlib at all, just bytes", COMPRESSED),
                         "LAND", "000802:CorruptLand.esp"),
        # compressed, valid zlib, inflating to a cut subrecord header
        "land-zlib-trunc": (_land_world_plugin(struct.pack("<I", 3) + zlib.compress(b"DAT"), COMPRESSED),
                            "LAND", "000802:CorruptLand.esp"),
        # FURTHER KINDS (case-82); each crashed fork head 747ea2f6 (runs/2026-10-14-scopeprobe-crash/explore-747ea2f6.log).
        # an uncompressed LAND whose body is a cut subrecord header
        "land-plain-trunc": (_land_world_plugin(b"DAT"), "LAND", "000802:CorruptLand.esp"),
        # compressed, valid zlib, but the declared inflated size is larger than what inflates
        "land-zlib-short": (_land_world_plugin(struct.pack("<I", 64) + zlib.compress(_fixture_sub(b"DATA", struct.pack("<I", 0))),
                                               COMPRESSED), "LAND", "000802:CorruptLand.esp"),
        # a LAND whose record length runs past the end of its group
        "land-record-overrun": (_land_record_overrun(), "LAND", "000802:CorruptLand.esp"),
        # not a LAND, and not nested in a worldspace: one top-level STAT with the compressed flag over bytes that are not zlib
        "stat-badzlib": (_fixture_plugin(1, _fixture_grup(b"STAT", 0, _fixture_rec(
            b"STAT", 0x800, struct.pack("<I", 64) + b"not zlib at all, just bytes", COMPRESSED))),
                         "STAT", "000800:CorruptLand.esp"),
    }


def _scope_on(tmp: Path, case: str, kind: str, data: bytes):
    """scopeprobe, built from this checkout, run on one hand-written plugin: (exe built?, result, scope doc)."""
    root = tmp / case / kind
    root.mkdir(parents=True, exist_ok=True)
    exe, why = _build_probe(tmp, "scopeprobe")
    check(exe is not None, f"{case}/{kind}: scopeprobe built from this checkout" + (f" - {why}" if why else ""))
    if exe is None:
        return None, None
    flags = _fixture_instance(root, "CorruptLand.esp", data)
    out = root / "scope.json"
    r = _run_fixture_probe(exe, flags + ["--target", "CorruptLand.esp", "--json", out], root)
    return r, load(out)


def _assert_refuses_record(case: str, kind: str, r, doc, sig: str, form_key: str):
    """An unreadable record: an incomplete scope written, that record named, one sentence, exit 1, no crash."""
    check("Unhandled exception" not in (r.stderr or ""),
          f"{case}/{kind}: no unhandled .NET exception (stderr tail {(r.stderr or '').strip()[-160:]!r})")
    expect(r.returncode, 1, f"{case}/{kind}: scopeprobe exit (1 = written but incomplete)")
    check(doc is not None, f"{case}/{kind}: scope.json is written")
    doc = doc or {}
    expect(doc.get("complete"), False, f"{case}/{kind}: the scope is incomplete")
    named = [u for u in doc.get("unreadableRecords") or []
             if u.get("formKey") == form_key and u.get("signature") == sig]
    check(len(named) == 1, f"{case}/{kind}: unreadableRecords names {sig} {form_key} once "
                           f"(got {doc.get('unreadableRecords')!r})")
    fails = [f for f in doc.get("walkFailures") or [] if form_key in f]
    check(len(fails) == 1 and "\n" not in fails[0] and fails[0].startswith("'CorruptLand.esp' "),
          f"{case}/{kind}: one walk-failure sentence names the record (got {doc.get('walkFailures')!r})")
    check(not doc.get("targets"), f"{case}/{kind}: no footprint is reported for the unread target")
    check(any(form_key in ln and "WALK FAILURE" in ln for ln in (r.stdout or "").splitlines()),
          f"{case}/{kind}: the console states the walk failure")


def case_81(tmp):
    """Task 2026-10-14-scopeprobe-crashes-on-corrupt-record. A REAL scopeprobe, built from this checkout, on a
    hand-written plugin whose one LAND carries the compressed flag over bytes that are not zlib, and on one whose zlib
    inflates to a cut subrecord header. On fork head 747ea2f6 both exit with an unhandled .NET exception and write no
    scope.json. Required: an incomplete scope, the LAND named, one sentence, exit 1. A well-formed LAND is the control."""
    case = "case-81-scopeprobe-corrupt-compressed-land"
    r, doc = _scope_on(tmp, case, "control-good-land", _land_world_plugin(_fixture_sub(b"DATA", struct.pack("<I", 0))))
    if r is None:
        return
    placed = (((doc or {}).get("targets") or [{}])[0].get("footprint") or {}).get("placed") or {}
    expect((r.returncode, (doc or {}).get("complete"), placed.get("land")), (0, True, 1),
           f"{case}/control-good-land: precondition - a well-formed LAND reads whole (exit, complete, land)")
    kinds = _corrupt_kinds()
    for kind in ("land-badzlib", "land-zlib-trunc"):
        data, sig, fk = kinds[kind]
        r, doc = _scope_on(tmp, case, kind, data)
        _assert_refuses_record(case, kind, r, doc, sig, fk)


def case_82(tmp):
    """The same task, further corruption kinds than compression on a LAND (the task asks for at least one): each must
    refuse the same way. Which kinds crash on 747ea2f6 is measured in runs/2026-10-14-scopeprobe-crash."""
    case = "case-82-scopeprobe-corrupt-other-kinds"
    kinds = _corrupt_kinds()
    for kind in [k for k in kinds if k not in ("land-badzlib", "land-zlib-trunc")]:
        data, sig, fk = kinds[kind]
        r, doc = _scope_on(tmp, case, kind, data)
        if r is None:
            return
        _assert_refuses_record(case, kind, r, doc, sig, fk)


def case_83(tmp):
    """The same task, end to end: on the scope scopeprobe writes for an unreadable LAND, the shipped registry answers
    unknown - never no - for every check whose trigger reads the scope (an `always` trigger reads nothing and stays
    yes), and the controller's Validator does not accept scopeprobe's receipt as a measured footprint. On the control's
    whole scope the land trigger answers yes, so the unknown is the corruption's, not the fixture's."""
    case = "case-83-scopeprobe-corrupt-land-registry"
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalregistry_case83", HARNESS / "evalregistry.py")
    reg_mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(reg_mod)
    registry = load(HARNESS.parent / ".claude" / "skills" / "world-edit-compatibility" / "references"
                    / "check-registry.json") or {}
    checks = registry.get("checks") or []
    check(len(checks) > 0, f"{case}: the shipped registry has checks")

    r, good = _scope_on(tmp, case, "control-good-land", _land_world_plugin(_fixture_sub(b"DATA", struct.pack("<I", 0))))
    if r is None:
        return
    seams = reg_mod.applicability(registry, good or {}).get("environment.land-seams") or {}
    expect(seams.get("applicability"), "yes", f"{case}/control-good-land: environment.land-seams on the whole scope")

    data, sig, fk = _corrupt_kinds()["land-badzlib"]
    r, doc = _scope_on(tmp, case, "land-badzlib", data)
    check(doc is not None, f"{case}/land-badzlib: scope.json is written (exit {r.returncode})")
    if doc is None:
        return
    answers = reg_mod.applicability(registry, doc)
    always = {c.get("id") for c in checks if ((c.get("applicability") or {}).get("trigger") or {}) == {"always": True}}
    wrong = {cid: a.get("applicability") for cid, a in answers.items()
             if a.get("applicability") != ("yes" if cid in always else "unknown")}
    expect(wrong, {}, f"{case}/land-badzlib: every scope-reading trigger answers unknown, each always-trigger yes "
                      f"({len(answers) - len(always)} unknown expected, {len(always)} yes)")
    expect(sorted(cid for cid, a in answers.items() if a.get("applicability") == "no"), [],
           f"{case}/land-badzlib: no check answers no")

    reg_check = next((c for c in checks if (c.get("adapter") or {}).get("kind") == "scopeprobe"), None)
    check(reg_check is not None, f"{case}: the shipped registry has a scopeprobe check")
    if reg_check is None:
        return
    spec = importlib.util.spec_from_file_location("evalrun_case83", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    arm = {"armId": "baseline", "kind": "baseline"}
    res = mod.Validator(tmp / case, {"arms": [arm]}).validate(
        check={"checkId": reg_check["id"]}, arm=arm, part=None, adapter=reg_check["adapter"],
        exit_code=r.returncode, log_text=r.stdout + "\n---- stderr ----\n" + r.stderr,
        outputs=[tmp / case / "land-badzlib" / "scope.json"], pre_hashes={}, started=None)
    # A rejected receipt gets no detector at all (Validator.validate sets one only when accepted): stricter than an
    # accepted "inconclusive", and never "no-finding".
    causes = [x["cause"] for x in res.get("rejections") or []]
    expect((res.get("accepted"), res.get("detector"), res.get("findings")), (False, None, None),
           f"{case}/land-badzlib: the scopeprobe receipt is not a measured footprint (accepted, detector, findings; "
           f"causes {causes})")
    detail = next((x.get("detail") or "" for x in res.get("rejections") or [] if x.get("cause") == "exit-nonzero"), "")
    check(fk in detail and "cannot be read" in detail,
          f"{case}/land-badzlib: the exit-nonzero rejection quotes the walk failure naming {fk} ({detail[:240]!r})")


CASES["case-81"] = case_81
CASES["case-82"] = case_82
CASES["case-83"] = case_83


def case_49(tmp):
    """Task 2026-10-14-discovery-hits-get-no-qualification-row. In-process through `run_derived`: every relevant
    hit in an accepted patchprobe / poolprobe / fomodprobe `--json` output becomes one qualification row under D-1's
    sha256 rule. Relevant = its masters, its FOMOD conditions or the pair it was found for name a plan target or a
    plugin of a plan pair (direct names only: a hit that masters only a DEPENDENT of a target is counted, not rowed).
    Every other hit is counted per probe with the reason. A missing, changed, text-only or unknown-schema output
    makes the derivation inconclusive and names the receipt."""
    import importlib.util
    case = "case-49-discovery-hit-rows"
    spec = importlib.util.spec_from_file_location("evalrun_case49", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    h_old, h_new, h_pool, h_cond, h_tr, h_un = ("a1" * 32, "b2" * 32, "c3" * 32, "d4" * 32, "e5" * 32, "f6" * 32)

    class Stub:
        def __init__(self, run, plan):
            self.run, self.plan, self.calls, self.extra, self.v = run, plan, [], None, None

        def account(self, check, arm, status, cause, detail, **kw):
            self.calls.append(status)

        def finish(self, *a, extra=None, **kw):
            self.extra, self.v = extra, (a[10] if len(a) > 10 else None)

    base = {"armId": "baseline", "kind": "baseline", "plugins": ["Skyrim.esm", "Target.esp", "Other.esp"]}
    cand = {"armId": "candidate-01-flip", "kind": "candidate", "plugins": ["Skyrim.esm", "Other.esp", "Target.esp"]}
    disc_checks = [{"checkId": "patch.discovery-installed", "status": "planned",
                    "adapter": {"kind": "patchprobe", "exe": "patchprobe"}},
                   {"checkId": "patch.discovery-pool", "status": "planned",
                    "adapter": {"kind": "patchprobe", "exe": "poolprobe"}},
                   {"checkId": "patch.discovery-fomod", "status": "planned",
                    "adapter": {"kind": "patchprobe", "exe": "fomodprobe"}}]

    def hit(file, kind, sha, masters, **detail):
        h = {"file": file, "kind": kind, "where": {}, "sha256": sha, "bytes": 10 if sha else None,
             "hashSource": "file" if sha else None, "masters": masters, "detail": detail}
        if not sha:
            h["unhashedReason"] = "matched by archive name only; patchprobe does not open archives"
        return h

    def doc(probe, hits, schema=1):
        return {"schemaVersion": schema, "probe": probe, "mode": "x", "complete": True, "sourcesSearched": [],
                "sourcesNotSearched": [], "counts": {"judged": 3, "unjudgeable": 0, "hits": len(hits)},
                "hits": hits, "unjudgeableItems": []}

    outputs = {
        "patchprobe": doc("patchprobe", [
            hit("Fix.esp", "installed", h_old, ["Target.esp", "Other.esp"], pair=["Other.esp", "Target.esp"]),
            hit("Fix.esp", "other-list", h_new, ["Target.esp"], pair=["Other.esp", "Target.esp"]),
            hit("Named Fix.7z", "download-name", None, [], pair=["Other.esp", "Target.esp"])]),
        "poolprobe": doc("poolprobe", [
            hit("PoolFix.esp", "pool-archive", h_pool, ["Other.esp", "Skyrim.esm"]),
            hit("Transitive.esp", "pool-archive", h_tr, ["Middle.esp"]),     # Middle.esp masters Target.esp
            hit("Unrelated.esp", "pool-archive", h_un, ["Zed.esp"])]),
        "fomodprobe": doc("fomodprobe", [
            hit("CondFix.esp", "fomod-option", h_cond, [], conditions=["Target.esp"], verdict="ABSENT")]),
    }
    exe_check = {c["adapter"]["exe"]: c["checkId"] for c in disc_checks}
    check_def = {"checkId": "patch.candidate-qualification",
                 "adapter": {"kind": "derived", "derives": "candidate-qualification"}}

    def setup(tag, damage=None, drop=None):
        run = tmp / case / tag
        (run / "receipts").mkdir(parents=True)
        (run / "outputs").mkdir()
        (run / "candidates").mkdir()
        (run / "candidates" / "candidates.json").write_text(json.dumps({"Candidates": []}), encoding="utf-8")
        recs = [{"receiptId": "r-base", "armId": "baseline", "checkId": "loadability.masters-and-order",
                 "validation": {"accepted": True}, "adapter": {"kind": "scopeprobe", "exe": "scopeprobe"},
                 "consumed": {"configuration": {"plugins": [{"name": "Target.esp", "sha256": "00" * 32},
                                                            {"name": "Fix.esp", "sha256": h_old}]}}}]
        for exe, d in outputs.items():
            if exe == drop:
                continue
            rid = f"{exe_check[exe]}__baseline"
            rel = f"outputs/{exe}.json"
            body = d
            if damage == ("schema", exe):
                body = {**d, "schemaVersion": 2}
            (run / rel).write_text(json.dumps(body), encoding="utf-8")
            row = {"path": rel, "sha256": mod.sha256_file(run / rel)}
            if damage == ("changed", exe):
                (run / rel).write_text(json.dumps({**d, "hits": []}), encoding="utf-8")
            if damage == ("missing", exe):
                (run / rel).unlink()
            outs = [] if damage == ("text", exe) else [row]
            recs.append({"receiptId": rid, "armId": "baseline", "checkId": exe_check[exe],
                         "validation": {"accepted": True}, "adapter": {"kind": "patchprobe", "exe": exe},
                         "outputs": outs, "consumed": {}})
        for r in recs:
            (run / "receipts" / f"{r['receiptId']}.json").write_text(json.dumps(r), encoding="utf-8")
        plan = {"arms": [base, cand], "checks": disc_checks, "targets": [{"name": "Target.esp"}]}
        stub = Stub(run, plan)
        mod.run_derived(stub, check_def, base)
        return stub, ((stub.extra or {}).get("derived") or {})

    stub, payload = setup("all-accepted")
    rows = [(r.get("file"), r.get("source"), r.get("qualification")) for r in payload.get("candidates") or []
            if r.get("source")]
    expect(sorted(rows), sorted([("Fix.esp", "patchprobe:installed", "qualified"),
                                 ("Fix.esp", "patchprobe:other-list", "unqualified"),
                                 ("Named Fix.7z", "patchprobe:download-name", "unqualified"),
                                 ("PoolFix.esp", "poolprobe:pool-archive", "unqualified"),
                                 ("CondFix.esp", "fomodprobe:fomod-option", "unqualified")]),
           f"{case}: exactly the relevant hits are rows, each qualified by its sha256", "D-1 / Gate A answer 1")
    by = {(r.get("file"), r.get("source")): r for r in payload.get("candidates") or []}
    why = (by.get(("Fix.esp", "patchprobe:installed")) or {}).get("why") or ""
    check(h_old[:12] in why and "baseline" in why, f"{case}: the qualified hit's why names its hash and arm ({why!r})")
    why = (by.get(("Named Fix.7z", "patchprobe:download-name")) or {}).get("why") or ""
    check("archive name only" in why, f"{case}: an unhashed hit's why carries the probe's reason ({why!r})")
    check(("CondFix.esp", "fomodprobe:fomod-option") in by,
          f"{case}: a hit relevant only through its FOMOD condition is a row")
    nr = payload.get("notRelevant") or {}
    expect((nr.get("poolprobe") or {}).get("count"), 2,
           f"{case}: the transitive-only and the unrelated pool hits are counted, not rowed", "Gate A answer 1")
    check(bool((nr.get("poolprobe") or {}).get("why")), f"{case}: the uncounted hits carry their reason")
    check(("Transitive.esp", "poolprobe:pool-archive") not in by and ("Unrelated.esp", "poolprobe:pool-archive") not in by,
          f"{case}: neither the transitive-only nor the unrelated hit is a row")
    expect((stub.v or {}).get("detector"), "no-finding", f"{case}: every output read, so the derivation is settled")

    for tag, damage in [("changed", ("changed", "poolprobe")), ("missing", ("missing", "fomodprobe")),
                        ("schema", ("schema", "patchprobe")), ("text", ("text", "poolprobe"))]:
        stub, payload = setup(tag, damage=damage)
        rid = f"{exe_check[damage[1]]}__baseline"
        expect((stub.v or {}).get("detector"), "inconclusive", f"{case}/{tag}: the derivation is inconclusive")
        said = " ".join(payload.get("inconclusive") or [])
        check(rid in said, f"{case}/{tag}: the inconclusive sentence names receipt {rid} ({said[:200]!r})")

    stub, payload = setup("pool-not-run", drop="poolprobe")
    said = " ".join(payload.get("notSearched") or [])
    check("patch.discovery-pool" in said, f"{case}: a planned discovery check with no accepted receipt is named "
                                          f"as not searched ({said[:200]!r})")


def case_51(tmp):
    """The held red arm of task 2026-10-14-discovery-hits-get-no-qualification-row, end to end: case 10's fixture
    on the trim that includes patch.discovery-installed and the derivation. The fake patchprobe writes the --json hit
    list; the catalogue hit CommunityPatch.esp (masters the fixture target, bytes f00d..., in no arm) is exactly one
    row, unqualified, its why naming its hash."""
    case = "case-51-discovery-end-to-end"
    run = tmp / case
    expect(do_plan(run, "case-10-discovery-media", registry="fixture-check-registry-derived.json").returncode, 0,
           f"{case}: plan exit")
    _, _, _, scen = case_paths("case-10-discovery-media")
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: run exit")
    expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}: finalize exit")
    disp = load(run / "disposition.json") or {}
    rows = [r for r in (field(disp, "discovery", default={}).get("candidates") or [])
            if r.get("file") == "CommunityPatch.esp"]
    expect([r.get("qualification") for r in rows], ["unqualified"],
           f"{case}: CommunityPatch.esp is one row, unqualified", "D7")
    check(bool(rows) and ("f00d" + "0" * 8) in (rows[0].get("why") or ""),
          f"{case}: its why names the hash compared ({(rows[0].get('why') if rows else None)!r})")


CASES["case-49"] = case_49
CASES["case-51"] = case_51


def _evalrun_module(name):
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def case_55(tmp):
    """The headline claim on a COMPLETE evaluation, which no fixture run reaches (every one is incomplete): one
    required check on one arm, completed, with detector `finding` gives `findings`; with `no-finding` it gives
    `no-findings-in-completed-checks`. In-process through `build_disposition` on a minimal plan."""
    case = "case-55-complete-claim"
    mod = _evalrun_module("evalrun_case55")
    run = tmp / case
    run.mkdir(parents=True, exist_ok=True)
    plan = {"planId": "p55", "registry": {"registryVersion": "t"}, "explicitDecisions": [],
            "states": {"notExplored": []}, "budgets": {"candidatesHeldBack": []}, "scope": {"unmeasured": []},
            "checks": [{"checkId": "geometry.x", "applicability": "yes", "applicabilityEvidence": "", "required": True,
                        "family": "geometry", "arms": [{"armId": "baseline"}], "adapter": {"kind": "probe-json"}}],
            "arms": [{"armId": "baseline", "kind": "baseline", "candidateFile": None, "loadedInGameNote": ""}]}
    for detector, want in [("finding", "findings"), ("no-finding", "no-findings-in-completed-checks")]:
        (run / "ledger.json").write_text(json.dumps({"planId": "p55", "entries": {"geometry.x__baseline": {
            "checkId": "geometry.x", "armId": "baseline", "part": None, "status": "completed",
            "detector": detector}}}), encoding="utf-8")
        disp = mod.build_disposition(run, plan, mod.Ledger(run, plan), {"geometry.x__baseline": {
            "result": {"findings": 1}, "validation": {"accepted": True}}})
        ev = disp["evaluation"]
        expect(ev["complete"], True, f"{case}/{detector}: the evaluation is complete")
        expect(ev["claim"], want, f"{case}/{detector}: the claim")


def case_57(tmp):
    """Three receipt refusals no fixture run reaches, in-process on the Validator: an emit with no Coverage is
    `coverage-missing`; a second emit of the same arm that consumed one plugin at a different hash is
    `mixed-snapshot`; an emit whose configuration states another population than the arm's is
    `population-mismatch`."""
    case = "case-57-receipt-refusals"
    mod = _evalrun_module("evalrun_case57")
    run = tmp / case
    run.mkdir(parents=True, exist_ok=True)
    h1, h2 = "1" * 64, "2" * 64
    arm = {"armId": "candidate-01", "kind": "candidate", "candidateFile": "",
           "requested": {"plugins": [{"name": "A.esp", "hashSource": "unstated"}], "overlays": []},
           "expected": {"population": "fixture population", "states": ["baseline"]}}
    plan = {"arms": [arm]}

    def identity(sha, population="fixture population"):
        plugins = [{"name": "A.esp", "path": None, "sha256": sha}]
        return {"plugins": plugins, "providers": [], "overlays": [], "population": population, "state": "baseline",
                "source": "", "schemaVersion": None,
                "fingerprint": mod.configuration_fingerprint(plugins, [], [], population, "baseline")}

    v = mod.Validator(run, plan)
    first = [r["cause"] for r in v._identity_checks(identity(h1), arm, {}, {})]
    expect(first, [], f"{case}: the first emit of the arm is accepted")
    second = [r["cause"] for r in v._identity_checks(identity(h2), arm, {}, {})]
    check("mixed-snapshot" in second, f"{case}: a second emit consuming A.esp at another hash is mixed-snapshot (got {second})")

    v = mod.Validator(run, plan)
    other = [r["cause"] for r in v._identity_checks(identity(h1, population="another population"), arm, {}, {})]
    check("population-mismatch" in other, f"{case}: an emit judging another population is population-mismatch (got {other})")

    emit = run / "no-coverage.json"
    emit.write_text(json.dumps({"Probe": "navownprobe", "Conflicts": []}), encoding="utf-8")
    res = mod.Validator(run, plan).validate(check={"checkId": "geometry.x"}, arm=arm, part=None,
                                            adapter={"kind": "probe-json", "exe": "navownprobe"}, exit_code=0,
                                            log_text="", outputs=[emit], pre_hashes={}, started=None)
    causes = [r["cause"] for r in res.get("rejections") or []]
    check("coverage-missing" in causes, f"{case}: an emit with no Coverage is coverage-missing (got {causes})")


CASES["case-55"] = case_55
CASES["case-57"] = case_57


def _build_probe(tmp: Path, probe: str):
    """`probe` built from this checkout into an empty folder under `tmp`: (exe, None), or (None, one sentence why not).
    Never an older binary."""
    out = Path(tmp) / "probe-build" / probe
    exe = out / (f"{probe}.exe" if os.name == "nt" else probe)
    if exe.exists():
        return exe, None
    csproj = HARNESS / probe / f"{probe}.csproj"
    r = subprocess.run(["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
                        "--artifacts-path", str(Path(tmp) / "probe-build" / "artifacts" / probe), "-o", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    if r.returncode != 0 or not exe.exists():
        tail = "\n".join((r.stdout + r.stderr).strip().splitlines()[-8:])
        return None, f"{probe} did not build from {csproj} (exit {r.returncode}): {tail}"
    return exe, None


def _run_fixture_probe(exe: Path, args: list, root: Path):
    env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(root / "runlog"))
    env.pop("HOUSECARL_HARNESS_CONFIG", None)
    return subprocess.run([str(exe)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", env=env, timeout=600)


def case_71(tmp):
    """Defect D-2 (task 2026-10-14-depprobe-judged-count-includes-unjudgeable). A REAL depprobe run, built from this
    checkout, on a hand-written plugin whose one declared edge is withheld (its enable parent is the player, 0x14):
    every edge is Unjudgeable. Its JSON, read through the shipped registry's `geometry.declared-dependencies`
    adapter by the Validator, must be refused `extraction-failed` with a coverage line whose Judged and Unjudgeable
    are disjoint (0 and 1): zero findings there is zero assessed edges, not a measured null."""
    case = "case-71-depprobe-all-unjudgeable"
    root = tmp / case
    root.mkdir(parents=True, exist_ok=True)
    exe, why = _build_probe(tmp, "depprobe")
    check(exe is not None, f"{case}: depprobe built from this checkout" + (f" - {why}" if why else ""))
    if exe is None:
        return
    cell = _fixture_rec(b"CELL", 0x800, _fixture_sub(b"EDID", b"DepCell\0") + _fixture_sub(b"DATA", struct.pack("<H", 1)))
    refr = _fixture_rec(b"REFR", 0x801, _fixture_sub(b"EDID", b"DepRef\0") + _fixture_sub(b"NAME", struct.pack("<I", 0x802))
                        + _fixture_sub(b"XESP", struct.pack("<IB3x", 0x14, 0))
                        + _fixture_sub(b"DATA", struct.pack("<6f", 0, 0, 0, 0, 0, 0)))
    tree = _fixture_grup(b"CELL", 0, _fixture_grup(struct.pack("<i", 0), 2, _fixture_grup(struct.pack("<i", 0), 3,
                         cell + _fixture_grup(struct.pack("<I", 0x800), 6, _fixture_grup(struct.pack("<I", 0x800), 9, refr)))))
    flags = _fixture_instance(root, "DepFixture.esp", _fixture_plugin(2, tree))
    out = root / "depprobe.json"
    r = _run_fixture_probe(exe, flags + ["--json", out], root)
    expect(r.returncode, 0, f"{case}: depprobe exit")
    doc = load(out) or {}
    # The fixture's own preconditions: one edge, withheld because its host is the player.
    expect((doc.get("edges"), (doc.get("verdicts") or {}).get("Unjudgeable"),
            (doc.get("unjudgeable") or {}).get("HostIsPlayer")), (1, 1, 1),
           f"{case}: precondition - one declared edge, Unjudgeable, blocker HostIsPlayer")

    registry = load(HARNESS.parent / ".claude" / "skills" / "world-edit-compatibility" / "references"
                    / "check-registry.json") or {}
    reg_check = next((c for c in registry.get("checks") or [] if c.get("id") == "geometry.declared-dependencies"), None)
    check(reg_check is not None, f"{case}: the shipped registry has geometry.declared-dependencies")
    if reg_check is None:
        return
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalrun_case71", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    arm = {"armId": "baseline", "kind": "baseline"}
    res = mod.Validator(root, {"arms": [arm]}).validate(
        check={"checkId": "geometry.declared-dependencies"}, arm=arm, part=None, adapter=reg_check["adapter"],
        exit_code=0, log_text=r.stdout, outputs=[out], pre_hashes={}, started=None)
    causes = [x["cause"] for x in res.get("rejections") or []]
    check("extraction-failed" in causes,
          f"{case}: an order whose every edge is Unjudgeable is refused extraction-failed (got {causes})")
    cov = (res.get("coverage") or [{}])[0]
    expect((cov.get("Judged"), sum((cov.get("Unjudgeable") or {}).values())), (0, 1),
           f"{case}: the coverage line's Judged and Unjudgeable are disjoint")


CASES["case-71"] = case_71


def pin_standard_settings(tmp: Path) -> None:
    """These known answers judge the load-time state: point every child at a private Standard-depth settings file,
    so the evaluation's Deep default and the user's own settings file never reach them (tests/test_stategen.py
    covers Deep)."""
    f = tmp / "evaluation-settings" / "houseCARL.evaluation.json"
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(json.dumps({"schemaVersion": 1, "evaluation": {"depth": "standard"}}), encoding="utf-8")
    os.environ["HOUSECARL_EVAL_SETTINGS"] = str(f)



# ------------------------------------------------------- the plan's population (P25): cases 412-416
#
# The plan names a population (the targets plus every plugin and mod folder its arms differ by) and the ordersweep
# adapter passes it to the probes whose registry row declares `populationFlag`; the others run on the whole order. The
# fake probes write what the real ones write for --population (`<population>+population:<sha12>`), so the receipts are
# validated exactly as a real run's would be.

P25_CASE = "case-01-geometry-and-quest"


def _p25_module():
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalrun_p25", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _p25_cmd(log: Path) -> str:
    """The command line a Runner job log records on its first line (`$ argv`)."""
    if not log.exists():
        return ""
    first = log.read_text(encoding="utf-8", errors="replace").splitlines()[:1]
    return first[0] if first else ""


def _p25_plan_run(tmp: Path, name: str, candidates: Path | None = None, scenario: Path | None = None,
                  run_it: bool = True, strip_population: bool = False):
    d = GEN / "cases" / P25_CASE
    run = tmp / name
    scen = scenario or d / "scenario.json"
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json", "--scope", d / "scope.json",
            "--candidates", candidates or d / "candidates", "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
    p = evalrun(args, scen, expect_exit=0)
    if strip_population and (run / "plan.json").exists():
        # A plan frozen by the controller before P25: no population block, no per-check population, no file.
        m = _p25_module()
        plan = load(run / "plan.json")
        plan.pop("population", None)
        for c in plan["checks"]:
            (c.get("adapter") or {}).pop("population", None)
        m.write_json(run / "plan.json", plan)
        (run / "plan.sha256").write_text(m.sha256_file(run / "plan.json") + "\n", encoding="utf-8")
        (run / "population.json").unlink(missing_ok=True)
    r = evalrun(["run", "--run", run], scen) if run_it and p.returncode == 0 else None
    return run, p, r


def _p25_ordersweep(run: Path, out: Path, population: Path | None, scen: Path):
    """ordersweep.py launched directly over the run's assembled candidates, reusing its first launch's cache."""
    cdir = run / "candidates"
    argv = [sys.executable, str(HARNESS / "ordersweep.py"), "--candidates", cdir, "--out", out,
            "--probes", "contactprobe,navownprobe,overlapprobe", "--harness", REAL_HARNESS,
            "--baseline", cdir / "candidate-00-baseline.json", "--max-candidates", "0",
            "--reuse-cache", last_sweep(run) / "cache.json"]
    if population is not None:
        argv += ["--population", population, "--population-probes", "contactprobe,overlapprobe"]
    for p in ("contactprobe", "navownprobe", "overlapprobe", "conflictdiff"):
        if p != "conflictdiff":
            argv += ["--probe-cmd", f"{p}=python {FIXT / 'fakeprobes' / p}.py"]
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen))
    r = subprocess.run([str(a) for a in argv], capture_output=True, text=True, env=env, timeout=300)
    log = out / "cache-log.txt"
    return r, (log.read_text(encoding="utf-8").splitlines() if log.exists() else [])


def case_412(tmp):
    """The adapter passes the population to the scoped probes only, and keys their reuse by the file's hash. Rejects: a
    navown run handed --population; a scoped receipt reused after the population changed; an unscoped one invalidated."""
    case = "case-412-population-adapter"
    d = GEN / "cases" / P25_CASE
    run, p, r = _p25_plan_run(tmp, case)
    plan = load(run / "plan.json") or {}
    pop = plan.get("population") or {}
    check(pop.get("scoped") is True, f"{case}: the plan is scoped ({pop.get('reason')})")
    pfile = run / "population.json"
    sweep = last_sweep(run)
    for probe, scoped in (("overlapprobe", True), ("contactprobe", True), ("navownprobe", False)):
        for arm in ("baseline", "candidate-01-reorder", "candidate-01-reorder-reverse"):
            cmd = _p25_cmd(sweep / "logs" / f"{probe}-{arm}.txt")
            check(bool(cmd), f"{case}: {probe} {arm} ran ({sweep / 'logs'})")
            if cmd:
                check(("--population " + str(pfile)) in cmd if scoped else "--population" not in cmd,
                      f"{case}: {probe} {arm} {'gets' if scoped else 'does not get'} --population ({cmd[-160:]})")
    led = ledger_entries(load(run / "ledger.json") or {})
    for cid in ("geometry.object-overlap", "geometry.placement-contact", "geometry.navmesh-ownership"):
        ent = [v for k, v in led.items() if k.startswith(cid + "__")]
        check(ent and all(execution_of(e) == "completed" for e in ent),
              f"{case}: every {cid} part is completed ({[execution_of(e) for e in ent]})")
    # Reuse: the same population file reuses every probe; a changed one reuses only the unscoped probe.
    same = tmp / f"{case}-same.json"
    shutil.copyfile(pfile, same)
    r1, lines1 = _p25_ordersweep(run, tmp / f"{case}-sweep-same", same, d / "scenario.json")
    reused1 = sorted(ln.split()[1].rstrip(":") for ln in lines1 if ln.startswith("REUSED"))
    expect(reused1, ["contactprobe-baseline", "navownprobe-baseline", "overlapprobe-baseline"],
           f"{case}: the same population (by content) reuses every baseline probe", "P25")
    changed = tmp / f"{case}-changed.json"
    changed.write_text(pfile.read_text(encoding="utf-8").replace('"schema": 1', '"schema": 1, "note": "changed"'),
                       encoding="utf-8")
    r2, lines2 = _p25_ordersweep(run, tmp / f"{case}-sweep-changed", changed, d / "scenario.json")
    reused2 = sorted(ln.split()[1].rstrip(":") for ln in lines2 if ln.startswith("REUSED"))
    miss2 = sorted(ln.split()[1].rstrip(":") for ln in lines2 if ln.startswith("MISS"))
    expect((reused2, miss2), (["navownprobe-baseline"], ["contactprobe-baseline", "overlapprobe-baseline"]),
           f"{case}: a changed population re-measures the scoped probes and reuses the unscoped one", "P25")
    bad = subprocess.run([sys.executable, str(HARNESS / "ordersweep.py"), "--candidates", run / "candidates",
                          "--out", tmp / f"{case}-bad", "--population-probes", "overlapprobe"],
                         capture_output=True, text=True, timeout=60)
    check(bad.returncode != 0 and "--population-probes needs --population" in (bad.stderr + bad.stdout),
          f"{case}: --population-probes without a file is refused ({bad.returncode})")


def case_413(tmp):
    """The plan's population: targets plus the arms' delta, frozen by hash; the unscoped fallback names its reason.
    Rejects: a delta computed from one arm; a population file whose bytes are not the plan's; a silent narrowing when
    a delta mod folder cannot be named."""
    case = "case-413-population-plan"
    m = _p25_module()
    expect(m.arm_delta([["A", "B", "C"], ["A", "C", "B"]]), ["B", "C"], f"{case}: a reorder's delta is the swapped pair")
    expect(m.arm_delta([["A", "B"], ["A", "B", "X"], ["a", "b"]]), ["X"], f"{case}: presence, case-insensitive")
    expect(m.arm_delta([["A", "B", "C", "D"], ["A", "B", "C", "D"]]), [], f"{case}: identical arms differ by nothing")
    run, p, _ = _p25_plan_run(tmp, case, run_it=False)
    plan = load(run / "plan.json") or {}
    pop = plan.get("population") or {}
    expect(pop.get("plugins"), ["FixtureBase.esp", "FixtureTarget.esp"], f"{case}: the reorder's two plugins are the population", "P25 D1")
    expect(pop.get("targets"), ["FixtureTarget.esp"], f"{case}: the target is recorded", "P25 D1")
    pf = run / "population.json"
    check(pf.exists() and hashlib.sha256(pf.read_bytes()).hexdigest() == pop.get("sha256"),
          f"{case}: population.json is frozen with the plan's sha256")
    ident = pop.get("identity") or ""
    expect(ident, "load-time+population:" + str(pop.get("sha256"))[:12], f"{case}: the identity a scoped emit carries", "P25 D3")
    for c in plan.get("checks", []):
        if (c.get("adapter") or {}).get("kind") == "ordersweep-probe":
            want = ident if c["adapter"].get("exe") in ("overlapprobe", "contactprobe") else None
            expect(c["adapter"].get("population"), want, f"{case}: {c['checkId']} expected population", "P25 D3")
    # Fallback: arms that differ by an asset provider, planned without --instance, cannot name the folder.
    cdir = tmp / f"{case}-mods-candidates"
    shutil.copytree(GEN / "cases" / P25_CASE / "candidates", cdir)
    for f, mods in (("candidate-00-baseline.json", ["fixture-mods"]), ("candidate-01-reorder.json", ["fixture-mods", "ExtraMeshes"])):
        doc = load(cdir / f)
        doc["Mods"] = mods
        (cdir / f).write_text(json.dumps(doc, indent=1), encoding="utf-8")
    run2, p2, _ = _p25_plan_run(tmp, case + "-fallback", candidates=cdir, run_it=False)
    pop2 = (load(run2 / "plan.json") or {}).get("population") or {}
    check(pop2.get("scoped") is False and "no --instance" in str(pop2.get("reason")) and not (run2 / "population.json").exists(),
          f"{case}: an unnameable mod folder leaves the plan unscoped and says why ({pop2.get('reason')})")
    check(set((pop2.get("wholeOrderChecks") or {})) >= {"geometry.object-overlap", "geometry.placement-contact"},
          f"{case}: the unscoped plan names the scoped checks as whole-order")
    # Plan-time refusals (Gate B review condition 2): a delta plugin carrying COLL records leaves the plan unscoped with
    # the reason at `plan`, instead of every scoped probe refusing hours into the run.
    import struct

    def plugin_bytes(groups):
        body = b""
        for label, nrec in groups:
            recs = b"".join(struct.pack("<4sIIIIHH", label.encode(), 0, 0, 0x800 + i, 0, 44, 0) for i in range(nrec))
            body += struct.pack("<4sI4siHHHH", b"GRUP", 24 + len(recs), label.encode(), 0, 0, 0, 44, 0) + recs
        return struct.pack("<4sIIIIHH", b"TES4", 0, 0, 0, 0, 44, 0) + body
    expect(m.plugin_top_groups_from_bytes(plugin_bytes([("STAT", 2), ("COLL", 1)])), {"STAT": 2, "COLL": 1},
           f"{case}: the top groups of a plugin and their record counts are read", "P25 condition 2")
    expect(m.plugin_top_groups_from_bytes(b"fixture text, not a plugin"), None, f"{case}: a file that is not a plugin reads as None")
    gen2 = tmp / f"{case}-coll-gen"
    subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen2)], capture_output=True, text=True, check=True)
    tgt = gen2 / "plugins" / "FixtureTarget.esp"
    tgt.write_bytes(plugin_bytes([("STAT", 1), ("COLL", 1)]))
    d2 = gen2 / "cases" / P25_CASE
    rc = evalrun(["plan", "--registry", gen2 / "registry" / "fixture-check-registry.json", "--scope", d2 / "scope.json",
                  "--candidates", d2 / "candidates", "--out", tmp / f"{case}-coll", "--harness", REAL_HARNESS] + probe_cmds(),
                 d2 / "scenario.json")
    popc = (load(tmp / f"{case}-coll" / "plan.json") or {}).get("population") or {}
    check(rc.returncode == 0 and popc.get("scoped") is False and "collision-layer" in str(popc.get("reason"))
          and "FixtureTarget.esp" in str(popc.get("reason")) and not (tmp / f"{case}-coll" / "population.json").exists(),
          f"{case}: a delta plugin with COLL records leaves the plan unscoped at plan time, naming it ({popc.get('reason')})")
    # The other two plan-time refusals, on plan_population directly: a delta mod folder shipping a *_SWAP.ini, and an
    # empty population.
    chk = [{"checkId": "geometry.object-overlap", "adapter": {"kind": "ordersweep-probe", "exe": "overlapprobe",
                                                               "populationFlag": "--population"},
            "arms": [{"armId": "baseline", "status": "planned"}]}]
    inst = tmp / f"{case}-inst"
    (inst / "mods" / "SwapMod").mkdir(parents=True)
    (inst / "mods" / "SwapMod" / "Thing_SWAP.ini").write_text("[Forms]" + chr(10), encoding="utf-8")
    arm_a = {"plugins": ["A.esp"], "requested": {"plugins": [], "providers": [{"name": "Base", "kind": "mod"}]}}
    arm_b = {"plugins": ["A.esp"], "requested": {"plugins": [], "providers": [{"name": "SwapMod", "kind": "mod"},
                                                                             {"name": "Base", "kind": "mod"}]}}
    blk, txt = m.plan_population(chk, [arm_a, arm_b], [], "load-time", str(inst))
    check(blk.get("scoped") is False and "Base Object Swapper" in str(blk.get("reason")) and txt is None,
          f"{case}: a delta mod folder shipping a *_SWAP.ini leaves the plan unscoped at plan time ({blk.get('reason')})")
    blk, txt = m.plan_population(chk, [arm_a, dict(arm_a)], [], "load-time", str(inst))
    check(blk.get("scoped") is False and "no population" in str(blk.get("reason")) and txt is None,
          f"{case}: an empty population leaves the plan unscoped at plan time ({blk.get('reason')})")
    # --population-from: a sibling evaluation adopts another plan's file when it covers this plan's own population.
    doc = json.loads(pf.read_text(encoding="utf-8"))
    wider = dict(doc, plugins=doc["plugins"] + [{"name": "Wider.esp", "path": doc["plugins"][0]["path"], "sha256": "0" * 64}])
    wf = tmp / f"{case}-wider.json"
    wf.write_text(json.dumps(wider, indent=1), encoding="utf-8")
    d = GEN / "cases" / P25_CASE
    base_args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json", "--scope", d / "scope.json",
                 "--candidates", d / "candidates", "--harness", REAL_HARNESS] + probe_cmds()
    r3 = evalrun(base_args + ["--out", tmp / f"{case}-adopt", "--population-from", wf], d / "scenario.json", expect_exit=0)
    pop3 = (load(tmp / f"{case}-adopt" / "plan.json") or {}).get("population") or {}
    check(r3.returncode == 0 and pop3.get("sha256") == hashlib.sha256(wf.read_bytes()).hexdigest()
          and (tmp / f"{case}-adopt" / "population.json").read_bytes() == wf.read_bytes(),
          f"{case}: --population-from a covering file is adopted byte for byte ({pop3.get('identity')})")
    narrow = dict(doc, plugins=[x for x in doc["plugins"] if x["name"] != "FixtureBase.esp"])
    nf = tmp / f"{case}-narrow.json"
    nf.write_text(json.dumps(narrow, indent=1), encoding="utf-8")
    r4 = evalrun(base_args + ["--out", tmp / f"{case}-narrow", "--population-from", nf], d / "scenario.json")
    check(r4.returncode == 2 and "does not name FixtureBase.esp" in (r4.stdout + r4.stderr),
          f"{case}: --population-from a file missing a delta plugin is refused ({r4.returncode})")


def case_414(tmp):
    """A scoped check whose emit judged another population is rejected by name. Rejects: crediting a whole-order emit
    (or an emit of another population) against the plan's scoped request."""
    case = "case-414-population-mismatch"
    d = GEN / "cases" / P25_CASE
    scen = load(d / "scenario.json")
    for stem, entry in (scen.get("arms") or {}).get("overlapprobe", {}).items():
        entry["ignorePopulation"] = True
    sp = tmp / f"{case}-scenario.json"
    sp.write_text(json.dumps(scen), encoding="utf-8")
    run, p, r = _p25_plan_run(tmp, case, scenario=sp)
    led = load(run / "ledger.json") or {}
    ent = [v for k, v in ledger_entries(led).items() if k.startswith("geometry.object-overlap__")]
    causes = sorted({x.get("cause") for e in ent for x in ((e.get("validation") or {}).get("rejections") or [])})
    check(ent and "population-mismatch" in causes, f"{case}: the overlap receipts are rejected for population-mismatch ({causes})")
    other = [v for k, v in ledger_entries(led).items() if k.startswith("geometry.navmesh-ownership__")]
    check(other and all(execution_of(e) == "completed" for e in other),
          f"{case}: the unscoped navmesh check is untouched ({[execution_of(e) for e in other]})")


def case_415(tmp):
    """Probes without a population flag stay on the whole order and the plan says so. Rejects: a whole-order check
    presented as scoped, or a scoped one presented as whole-order."""
    case = "case-415-whole-order-named"
    run, p, _ = _p25_plan_run(tmp, case, run_it=False)
    pop = (load(run / "plan.json") or {}).get("population") or {}
    who = pop.get("wholeOrderChecks") or {}
    check("geometry.navmesh-ownership" in who and "whole order" in who["geometry.navmesh-ownership"],
          f"{case}: navmesh ownership is named whole-order with its reason ({who})")
    expect(sorted(pop.get("scopedProbes") or []), ["contactprobe", "overlapprobe"], f"{case}: the scoped probes", "P25 D4")
    reg = load(GEN / "registry" / "fixture-check-registry.json")
    flagged = sorted(c["adapter"]["exe"] for c in reg["checks"] if (c.get("adapter") or {}).get("populationFlag"))
    expect(flagged, ["contactprobe", "overlapprobe", "stateprobe"],
           f"{case}: the overlap and contact rows (P25) and the state row (window 2) declare populationFlag; the navmesh row does not", "P25 D4")


def case_416(tmp):
    """A plan frozen before P25 resumes exactly as before: no --population anywhere, and every request key is the
    pre-P25 formula's. Rejects: a resumed old plan whose sweep gains a population term (its cached work re-measured,
    or worse, a scoped emit filed under a whole-order request)."""
    case = "case-416-old-plan"
    run, p, r = _p25_plan_run(tmp, case, strip_population=True)
    check(r is not None and r.returncode == 0, f"{case}: the frozen pre-P25 plan runs ({r.returncode if r else None})")
    plan = load(run / "plan.json") or {}
    check("population" not in plan and not (run / "population.json").exists(), f"{case}: the plan carries no population")
    ocmd = _p25_cmd(run / "logs" / "ordersweep.txt")
    check(bool(ocmd) and "--population" not in ocmd, f"{case}: the ordersweep command line has no population ({ocmd[-200:]})")
    sweep = last_sweep(run)
    cmds = [_p25_cmd(f) for f in sorted((sweep / "logs").glob("*probe-*.txt"))]
    check(cmds and not any("--population" in c for c in cmds), f"{case}: no probe command line has --population ({len(cmds)})")
    # The request key, recomputed with the pre-P25 formula (ordersweep.key_for at 77c414bf): probe, program key,
    # order sha, base sha or "-", the joined common+extra args. Every recorded key must be one of these.
    cache = load(sweep / "cache.json") or {}

    def sha(pth):
        return hashlib.sha256(Path(pth).read_bytes()).hexdigest()
    cdir = run / "candidates"
    want = set()
    jobs = [json.loads(ln) for ln in (sweep / "jobs.jsonl").read_text(encoding="utf-8").splitlines() if ln.strip()]
    for j in jobs:
        if j.get("kind") != "probe":
            continue
        probe = j["job"].split("-")[0]
        prog = "probe-cmd:" + hashlib.sha256(f"python {FIXT / 'fakeprobes' / probe}.py".encode("utf-8")).hexdigest()
        order = j["inputs"]["order"]
        base = j["inputs"].get("base")
        parts = [probe, prog, sha(order), sha(base) if base else "-", ""]
        want.add(hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest())
    check(cache and set(cache) <= want and len(cache) == len([j for j in jobs if j.get("kind") == "probe"]),
          f"{case}: every request key is the pre-P25 formula's ({len(set(cache) & want)} of {len(cache)} match)")


CASES["case-412"] = case_412
CASES["case-413"] = case_413
CASES["case-414"] = case_414
CASES["case-415"] = case_415
CASES["case-416"] = case_416


# ---------------------------------------------------------------------------------- window 2: probe-emit checks scoped
# A probe-emit check whose registry row declares `populationFlag` (state.conditional-geometry in the fixture trim;
# geometry.fixture-empty-space in the shipped registry) is scoped by check id: the plan sets its adapter.population, and
# run_generic hands the probe the plan's frozen population file itself, gated by the file's hash.

def _w2_plan_run(tmp: Path, name: str, run_it: bool = True, strip_population: bool = False, scenario: Path | None = None):
    d = GEN / "cases" / P25_CASE
    run = tmp / name
    scen = scenario or d / "scenario.json"
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json", "--scope", d / "scope.json",
            "--candidates", d / "candidates", "--out", run, "--harness", REAL_HARNESS, "--states", "baseline,fixture-switched",
            "--select", "state.conditional-geometry"] + probe_cmds()
    p = evalrun(args, scen, expect_exit=0)
    if strip_population and (run / "plan.json").exists():
        # A plan frozen before window 2: no scopedChecks, and the probe-emit adapter carries neither populationFlag nor
        # population (the frozen registry row predates them).
        m = _p25_module()
        plan = load(run / "plan.json")
        (plan.get("population") or {}).pop("scopedChecks", None)
        for c in plan["checks"]:
            if (c.get("adapter") or {}).get("kind") == "probe-emit":
                c["adapter"].pop("population", None)
                c["adapter"].pop("populationFlag", None)
        m.write_json(run / "plan.json", plan)
        (run / "plan.sha256").write_text(m.sha256_file(run / "plan.json") + "\n", encoding="utf-8")
    r = evalrun(["run", "--run", run], scen) if run_it and p.returncode == 0 else None
    return run, p, r


def _w2_state_cmds(run: Path) -> list[str]:
    return [_p25_cmd(f) for f in sorted((run / "logs").glob("state.conditional-geometry__*.txt"))]


def case_428(tmp):
    """The plan scopes a probe-emit check by id and run_generic passes the frozen file to it alone. Rejects: a scoped
    probe-emit check run without --population (whole order under a scoped request); --population handed to a check
    that does not declare it; a changed population file used anyway; an old plan resumed with a population."""
    case = "case-428-probe-emit-population"
    run, p, r = _w2_plan_run(tmp, case)
    plan = load(run / "plan.json") or {}
    pop = plan.get("population") or {}
    check(pop.get("scoped") is True and "state.conditional-geometry" in (pop.get("scopedChecks") or []),
          f"{case}: the plan names the state check among its scoped checks ({pop.get('scopedChecks')}, {pop.get('reason')})")
    geo = next((c for c in plan.get("checks", []) if c["checkId"] == "state.conditional-geometry"), {})
    expect((geo.get("adapter") or {}).get("population"), pop.get("identity"),
           f"{case}: the state check's expected population is the scoped identity", "window 2")
    census = next((c for c in plan.get("checks", []) if c["checkId"] == "state.switch-census"), {})
    expect((census.get("adapter") or {}).get("population"), None, f"{case}: the census (no populationFlag) is not scoped")
    cmds = _w2_state_cmds(run)
    pfile = run / "population.json"
    check(cmds and all(("--population " + str(pfile)) in c for c in cmds),
          f"{case}: every stateprobe launch gets --population {pfile.name} ({[c[-160:] for c in cmds]})")
    census_cmds = [_p25_cmd(f) for f in sorted((run / "logs").glob("state.switch-census__*.txt"))]
    check(census_cmds and not any("--population" in c for c in census_cmds),
          f"{case}: switchprobe never gets --population ({len(census_cmds)} launches)")
    led = ledger_entries(load(run / "ledger.json") or {})
    ent = [v for k, v in led.items() if k.startswith("state.conditional-geometry__")]
    check(ent and all(execution_of(e) == "completed" for e in ent),
          f"{case}: every state check part is accepted against the scoped identity ({[execution_of(e) for e in ent]})")
    # A population file changed after the plan froze blocks the check by name; it is never run on another population.
    run2, p2, _ = _w2_plan_run(tmp, case + "-changed", run_it=False)
    pf2 = run2 / "population.json"
    pf2.write_text(pf2.read_text(encoding="utf-8").replace('"schema": 1', '"schema": 1, "note": "changed"'), encoding="utf-8")
    evalrun(["run", "--run", run2], GEN / "cases" / P25_CASE / "scenario.json")
    led2 = ledger_entries(load(run2 / "ledger.json") or {})
    ent2 = [v for k, v in led2.items() if k.startswith("state.conditional-geometry__")]
    check(ent2 and all(execution_of(e) == "blocked(population-changed)" for e in ent2) and not _w2_state_cmds(run2),
          f"{case}: a changed population file blocks the state check and launches nothing ({[execution_of(e) for e in ent2]})")
    # A plan frozen before window 2 resumes whole-order for the probe-emit check.
    run3, p3, r3 = _w2_plan_run(tmp, case + "-old", strip_population=True)
    cmds3 = _w2_state_cmds(run3)
    check(r3 is not None and r3.returncode == 0 and cmds3 and not any("--population" in c for c in cmds3),
          f"{case}: an old plan's state check runs with no --population ({[c[-120:] for c in cmds3]})")
    led3 = ledger_entries(load(run3 / "ledger.json") or {})
    ent3 = [v for k, v in led3.items() if k.startswith("state.conditional-geometry__")]
    check(ent3 and all(execution_of(e) == "completed" for e in ent3),
          f"{case}: the old plan's whole-order state emits are accepted ({[execution_of(e) for e in ent3]})")


def case_429(tmp):
    """A scoped probe-emit check whose emit judged another population is rejected by name. Rejects: crediting a
    whole-order state emit against the plan's scoped request."""
    case = "case-429-probe-emit-population-mismatch"
    d = GEN / "cases" / P25_CASE
    scen = load(d / "scenario.json")
    for stem, entry in (scen.get("arms") or {}).get("stateprobe", {}).items():
        entry["ignorePopulation"] = True
    sp = tmp / f"{case}-scenario.json"
    sp.write_text(json.dumps(scen), encoding="utf-8")
    run, p, r = _w2_plan_run(tmp, case, scenario=sp)
    led = ledger_entries(load(run / "ledger.json") or {})
    ent = [v for k, v in led.items() if k.startswith("state.conditional-geometry__")]
    causes = sorted({x.get("cause") for e in ent for x in ((e.get("validation") or {}).get("rejections") or [])})
    check(ent and "population-mismatch" in causes, f"{case}: the state receipts are rejected for population-mismatch ({causes})")


def case_430(tmp):
    """Registry and plan rules for a scoped probe-emit check. Rejects: populationFlag accepted on a kind that runs no
    probe; a RACE-carrying delta planned with the state check scoped (stateprobe would refuse hours into the run)."""
    case = "case-430-probe-emit-registry-and-race"
    m = _p25_module()
    import struct

    def plugin_bytes(groups):
        body = b""
        for label, nrec in groups:
            recs = b"".join(struct.pack("<4sIIIIHH", label.encode(), 0, 0, 0x800 + i, 0, 44, 0) for i in range(nrec))
            body += struct.pack("<4sI4siHHHH", b"GRUP", 24 + len(recs), label.encode(), 0, 0, 0, 44, 0) + recs
        return struct.pack("<4sIIIIHH", b"TES4", 0, 0, 0, 0, 44, 0) + body
    race = tmp / f"{case}-Race.esp"
    race.write_bytes(plugin_bytes([("RACE", 1), ("STAT", 1)]))
    plain = tmp / f"{case}-Plain.esp"
    plain.write_bytes(plugin_bytes([("STAT", 1)]))
    sha = lambda f: hashlib.sha256(f.read_bytes()).hexdigest()  # noqa: E731
    chk = [{"checkId": "geometry.object-overlap", "adapter": {"kind": "ordersweep-probe", "exe": "overlapprobe", "populationFlag": "--population"},
            "arms": [{"armId": "baseline", "status": "planned"}]},
           {"checkId": "state.conditional-geometry", "adapter": {"kind": "probe-emit", "exe": "stateprobe", "populationFlag": "--population"},
            "arms": [{"armId": "baseline", "status": "planned"}]},
           {"checkId": "geometry.fixture-empty-space", "adapter": {"kind": "probe-emit", "exe": "contactprobe", "populationFlag": "--population"},
            "arms": [{"armId": "baseline", "status": "planned"}]}]

    def arms_with(f):
        a = {"plugins": ["A.esp"], "requested": {"plugins": [], "providers": []}}
        b = {"plugins": ["A.esp", f.name], "requested": {"plugins": [{"name": f.name, "path": str(f), "sha256": sha(f)}], "providers": []}}
        return [a, b]
    blk, txt = m.plan_population(chk, arms_with(race), [], "load-time", None)
    check(blk.get("scoped") is True and blk.get("scopedChecks") == ["geometry.fixture-empty-space", "geometry.object-overlap"]
          and "RACE" in str((blk.get("wholeOrderChecks") or {}).get("state.conditional-geometry")),
          f"{case}: a RACE-carrying delta leaves the state check whole-order with the reason, the others scoped "
          f"({blk.get('scopedChecks')}, {blk.get('wholeOrderChecks')})")
    blk2, _ = m.plan_population(chk, arms_with(plain), [], "load-time", None)
    expect(blk2.get("scopedChecks"), ["geometry.fixture-empty-space", "geometry.object-overlap", "state.conditional-geometry"],
           f"{case}: without RACE every flagged check is scoped", "window 2")
    # evalregistry: populationFlag on probe-emit is valid; on a kind that runs no probe it is not.
    reg = load(GEN / "registry" / "fixture-check-registry.json")
    import importlib.util
    spec = importlib.util.spec_from_file_location("evalregistry_w2", HARNESS / "evalregistry.py")
    er = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(er)
    bad = json.loads(json.dumps(reg))
    tgt = next(c for c in bad["checks"] if (c.get("adapter") or {}).get("kind") == "mcp-only")
    tgt["adapter"]["populationFlag"] = "--population"
    probs_ok = [x for x in er.validate(reg) if "populationFlag" in x] if hasattr(er, "validate") else None
    probs_bad = [x for x in er.validate(bad) if "populationFlag" in x] if hasattr(er, "validate") else None
    check(probs_ok == [] and probs_bad and "probe-emit" in probs_bad[0],
          f"{case}: evalregistry accepts populationFlag on probe-emit and refuses it on {tgt['adapter']['kind']} ({probs_ok}, {probs_bad})")


CASES["case-428"] = case_428
CASES["case-429"] = case_429
CASES["case-430"] = case_430


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="hc-evalrun-test-"))
    pin_standard_settings(tmp)
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
    try:
        use_conflictdiff(tmp)
    except SystemExit as ex:
        print(ex)
        shutil.rmtree(tmp, ignore_errors=True)
        return 1
    if not DIFF_EXE.exists():
        print(f"  FAIL  conflictdiff is not built at {DIFF_EXE} - build it (dotnet build -c Release) or set "
              f"HOUSECARL_EVALRUN_TEST_HARNESS to a tree that has it. The real diff is not optional here.")
        shutil.rmtree(tmp, ignore_errors=True)
        return 1
    print(DIFF_SOURCE)
    print(diff_exe_identity())
    ran = 0
    try:
        for name, fn in CASES.items():
            if want and not name.startswith(want) and not want.startswith(name):
                continue
            ran += 1
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
    if not ran:
        # A filter that selects nothing tested nothing; reporting it as passed would be a vacuous green.
        print(f"  FAIL  no case matches {want!r}; case names are " + ", ".join(CASES))
        return 2
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
