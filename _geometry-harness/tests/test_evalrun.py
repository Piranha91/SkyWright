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
import shlex
import shutil
import struct
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
EVALRUN = HARNESS / "evalrun.py"                # enrich and verify-bundle's Python spelling (what stays of evalrun)
RUNCONTROL = HARNESS / "runcontrol" / "bin" / "Release" / "net9.0" / ("runcontrol.exe" if os.name == "nt" else "runcontrol")
# The controller every child-process case starts: the built-in one, runcontrol. EVALRUN_CONTROLLER_CMD names another
# build of it. `enrich` is not part of it and goes to evalrun.py.
CONTROLLER = (shlex.split(os.environ["EVALRUN_CONTROLLER_CMD"], posix=False) if os.environ.get("EVALRUN_CONTROLLER_CMD", "").strip()
              else [str(RUNCONTROL)])
PYTHON_VERBS = {"enrich"}
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


def evalrun(args, scenario: Path | None = None, expect_exit=None, timeout=300, cwd: Path | None = None):
    env = dict(os.environ)
    if scenario:
        env["EVALRUN_FAKE_SCENARIO"] = str(scenario)
    program = [sys.executable, str(EVALRUN)] if args and str(args[0]) in PYTHON_VERBS else CONTROLLER
    r = subprocess.run(program + [str(a) for a in args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=timeout, cwd=cwd)
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
    proc = subprocess.Popen([*CONTROLLER, "run", "--run", str(run)],
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
    nothing unfinished in the stopped sweep is accepted (a complete emit with no completion, a stale summary.json and
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
        return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True,
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
    proc = subprocess.Popen([*CONTROLLER, "run", "--run", str(run)],
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
    # the baseline finished before the stop (its RECORDED line is asserted below), so it keeps its result; this used to
    # read stopped(deadline), which was the defect of a stop losing every finished job
    expect(execution_of(led.get(f"{nav}__{base_id}__arm")), "completed", f"{case}: the baseline that finished keeps its result")
    for aid, part in parts[1:]:
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
    of work, so the check must NOT be accepted: at the deadline the descendant is stopped, every part of that candidate
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
        return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True,
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
    proc = subprocess.Popen([*CONTROLLER, "run", "--run", str(run)],
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
    # the baseline's job finished, tree empty, before the stop, so it keeps its result (it used to read stopped)
    expect(execution_of(led.get(f"{nav}__{base_id}__arm")), "completed", f"{case}: the baseline that finished keeps its result")
    for aid, part in [(cand_id, "arm"), (cand_id, "reverse"), (cand_id, "diff")]:
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
    resolved configuration lacks is a walk failure starting with the controller's ScopeAbsentTarget wording for that
    name, and exits 1; a refused configuration exits 2 with REFUSED on stderr and no scope.json."""
    absent_prefix = "'{name}' is not in the resolved configuration ("   # runcontrol's Validator.ScopeAbsentTarget
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
        return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True,
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












CASES["case-20"] = case_20
CASES["case-21"] = case_21
CASES["case-22"] = case_22
CASES["case-24"] = case_24






# ------------------------------------------------------ cases 30-36: sweep evidence per invocation
# Review V2's H2, M1, M2, M3, M4 and the reused-diff defect (3b), plus the verified-reuse guard. The assertions
# read what the controller records (ledger statuses, receipt exit codes, output hashes, finalize's integrity,
# the disposition, and how many children it launched), not where RUN/sweep keeps its files. A check that
# holds only for the per-invocation folder layout says `(layout)`.




















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




















# ------------------------------------------------------ cases 37-38: review R1 of the per-invocation sweep



















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




CASES["case-42"] = case_42
CASES["case-43"] = case_43
CASES["case-44"] = case_44


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


def recorded_bundle(tmp: Path, tag: str, bundle_args=("--no-names",)):
    """case-01's evaluation with one record import credited on its first mcp-only arm, finalized and bundled (by
    default without the names step, for the cases that enrich by hand)."""
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
    expect(evalrun(["bundle", "--run", run, "--out", bundle] + list(bundle_args)).returncode, 0, f"{tag}: bundle",
           "setup")
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
    # round 3 (W-B2, W-B3): each object a row names keeps its base and the plugins read that carry it; each base its
    # type and model, "" when the record reads the model as absent
    refs, bases = (doc or {}).get("refs") or {}, (doc or {}).get("bases") or {}
    check(bool(refs) and all(str(r.get("base") or "").startswith("0BB0") and r.get("carriedBy") for r in refs.values()),
          f"{case}: every named object has its base and its carriers ({list(refs.items())[:2]})")
    check(bool(bases) and all(b.get("model") == ("" if b.get("type") == "Light" else "fixture.nif")
                              for b in bases.values()),
          f"{case}: every base has its model, none for a light without one ({bases})")
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




CASES["case-81"] = case_81
CASES["case-82"] = case_82




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


CASES["case-51"] = case_51










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
    sys.path.insert(0, str(HARNESS))
    import toolidentity  # the program identity rule (imported here: case-46 copies only part of the harness)
    cdir = run / "candidates"
    want = set()
    jobs = [json.loads(ln) for ln in (sweep / "jobs.jsonl").read_text(encoding="utf-8").splitlines() if ln.strip()]
    for j in jobs:
        if j.get("kind") != "probe":
            continue
        probe = j["job"].split("-")[0]
        # the program key is the override's identity (toolidentity.probe_cmd: its text and the script it names); the
        # population term is this case's subject, not the program key's form
        prog = toolidentity.probe_cmd(f"python {FIXT / 'fakeprobes' / probe}.py")["digest"]
        order = j["inputs"]["order"]
        base = j["inputs"].get("base")
        parts = [probe, prog, sha(order), sha(base) if base else "-", ""]
        if j["inputs"].get("baselineEmit"):
            # a reverse measured from the baseline emit's rows (overlapprobe --reverse-of) keys that emit too, by design
            parts.append("reverse-of:" + sha(j["inputs"]["baselineEmit"]))
        want.add(hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest())
    check(cache and set(cache) <= want and len(cache) == len([j for j in jobs if j.get("kind") == "probe"]),
          f"{case}: every request key is the pre-P25 formula's ({len(set(cache) & want)} of {len(cache)} match)")
    # Overlap acceleration P1 (and contactprobe since 2026-10-21): each RECONCILE_REVERSE probe's reverse takes the measured
    # baseline emit's rows (--reverse-of); no other probe's does.
    taking = ("overlapprobe", "contactprobe")
    revs = [j for j in jobs if j.get("kind") == "probe" and j["job"].endswith("-reverse")]
    base_emit = {j["job"].split("-")[0]: j.get("emit") for j in jobs if j.get("kind") == "probe" and j["job"].endswith("-baseline")}
    ovl = [j for j in revs if j["job"].split("-")[0] in taking]
    check(ovl and {j["job"].split("-")[0] for j in ovl} == set(taking)
          and all(j["inputs"].get("baselineEmit") == base_emit.get(j["job"].split("-")[0])
                  and "--reverse-of" in _p25_cmd(sweep / "logs" / f"{j['job']}.txt") for j in ovl)
          and not any(j["inputs"].get("baselineEmit") for j in revs if j["job"].split("-")[0] not in taking),
          f"{case}: overlapprobe's and contactprobe's {len(ovl)} reverse job(s) name the baseline emit and pass --reverse-of; the "
          f"other {len(revs) - len(ovl)} reverse job(s) do not")
    # Overlap acceleration P3: every overlapprobe job writes its pair results and reads those of the jobs before it.
    ojobs = [j for j in jobs if j.get("kind") == "probe" and j["job"].startswith("overlapprobe-")]
    cmds = {j["job"]: _p25_cmd(sweep / "logs" / f"{j['job']}.txt") for j in ojobs}
    written = []
    ok_reads = True
    for j in ojobs:
        cmd = cmds[j["job"]]
        ok_reads = ok_reads and f"{j['job']}.pair-results.jsonl.gz" in cmd and all(w in cmd for w in written)
        written.append(f"{j['job']}.pair-results.jsonl.gz")
    others = [c for f in sorted((sweep / "logs").glob("*probe-*.txt")) if not f.name.startswith("overlapprobe-")
              for c in [_p25_cmd(f)]]
    check(len(ojobs) >= 3 and ok_reads and not any("--pair-cache" in c for c in others),
          f"{case}: each of overlapprobe's {len(ojobs)} jobs writes its pair results and reads every earlier job's; no other "
          f"probe's command line names a pair cache")


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




CASES["case-428"] = case_428
CASES["case-429"] = case_429


# ------------------------------------------------------------------ the names step of `bundle` (cases 560-569)

NAMES_STATUSES = ("enriched", "partial", "refused", "skipped")


def names_status(bundle: Path) -> dict:
    """The names step's statement in a bundle's manifest ({} when the bundle carries none)."""
    return (((load(bundle / "manifest.json") or {}).get("enrichmentStatus") or {}).get("names")) or {}


def names_doc(bundle: Path) -> dict | None:
    row = next((e for e in manifest_rows(bundle, "names") if not e.get("role")), None)
    return load(bundle / row["path"]) if row else None


def stated_or_enriched(bundle: Path, case: str, rule: str) -> None:
    """The step's one promise: a bundle carries the names enrichment, or one plain sentence saying why not."""
    st = names_status(bundle)
    sentence = str(st.get("sentence") or "")
    check(bool(manifest_rows(bundle, "names")) or (st.get("status") in ("refused", "skipped") and sentence),
          f"{case}: the bundle carries the names enrichment or a sentence saying why not ({rule}; status {st!r})")
    check(st.get("status") in NAMES_STATUSES and sentence.strip() and "\n" not in sentence
          and sentence.rstrip().endswith("."),
          f"{case}: the names step is stated as one plain sentence ({rule}; {sentence[:160]!r})")


def names_run(tmp: Path, tag: str):
    """recorded_bundle's run, bundled once without the names step; the cases bundle it again as they need."""
    bundle, plan, cid, arm, names = recorded_bundle(tmp, tag)
    return tmp / f"{tag}-run", plan, arm


def case_560(tmp):
    """C560: `bundle` runs the names enrichment by default with the configured reader: the bundle carries names.json
    with each object's base and each base's model, says so in one sentence, and verifies."""
    case = "case-560-bundle-runs-names"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case, bundle_args=())
    stated_or_enriched(bundle, case, "C560")
    st = names_status(bundle)
    expect(st.get("status"), "enriched", f"{case}: the names step read every object and base", "C560")
    doc = names_doc(bundle) or {}
    check(bool(doc.get("refs")) and bool(doc.get("bases")),
          f"{case}: names.json carries refs and bases, what the body rule needs ({list(doc)[:10]})")
    check(doc.get("unread") == {"objects": [], "bases": []},
          f"{case}: names.json lists nothing unread ({doc.get('unread')!r})")
    expect(st.get("reader"), "HOUSECARL_RECORD_READER_CMD", f"{case}: the statement says where the reader came from",
           "C560")
    expect(evalrun(["verify-bundle", bundle]).returncode, 0, f"{case}: the bundle verifies", "C560")
    # C567: the report then needs no hand step: its plan finds the names in the bundle as made
    out = tmp / f"{case}-report"
    r = subprocess.run([sys.executable, str(HARNESS / "evalreport.py"), "plan", "--bundle", str(bundle), "--out",
                        str(out), "--verify-copy"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    plans = [p for p in out.glob("*.json") if (load(p) or {}).get("reader") is not None] if out.is_dir() else []
    reader = (load(plans[0]) or {}).get("reader") if plans else {}
    check(r.returncode == 0 and isinstance((reader or {}).get("names"), dict),
          f"{case}: evalreport plan reads the names from the bundle with no hand step (C567; exit {r.returncode}, "
          f"{r.stderr.strip()[-200:]!r})")


def case_561(tmp):
    """C561: `--no-names` skips the step and says so in one sentence; `enrich --kind names` later adds it and the
    statement then says enriched; both bundles verify."""
    case = "case-561-bundle-no-names"
    run, plan, arm = names_run(tmp, case)
    b = tmp / f"{case}-b"
    r = evalrun(["bundle", "--run", run, "--out", b, "--no-names"])
    expect(r.returncode, 0, f"{case}: bundle exit", "C561")
    stated_or_enriched(b, case, "C561")
    st = names_status(b)
    expect(st.get("status"), "skipped", f"{case}: the skip is stated", "C561")
    check("--no-names" in str(st.get("sentence")), f"{case}: the sentence names the flag ({st.get('sentence')!r})")
    check(not manifest_rows(b, "names"), f"{case}: no names rows when skipped")
    expect(evalrun(["verify-bundle", b]).returncode, 0, f"{case}: the skipped bundle verifies", "C561")
    e = evalrun(["enrich", "--bundle", b, "--kind", "names"])
    expect(e.returncode, 0, f"{case}: enrich by hand with the configured reader ({e.stderr.strip()[-200:]})", "C561")
    expect(names_status(b).get("status"), "enriched", f"{case}: the statement follows the hand enrichment", "C561")
    expect(evalrun(["verify-bundle", b]).returncode, 0, f"{case}: the enriched bundle verifies", "C561")


def case_562(tmp):
    """C562: a reader that cannot start: the bundle is still made and verifies, carries no names, and states the
    refusal in one sentence in the manifest and on stderr."""
    case = "case-562-bundle-reader-absent"
    run, plan, arm = names_run(tmp, case)
    b = tmp / f"{case}-b"
    absent = tmp / "no-such-reader" / "housecarl-mcp.exe"
    r = evalrun(["bundle", "--run", run, "--out", b, "--reader-cmd", str(absent)])
    expect(r.returncode, 0, f"{case}: the bundle is made ({r.stderr.strip()[-200:]})", "C562")
    stated_or_enriched(b, case, "C562")
    st = names_status(b)
    expect(st.get("status"), "refused", f"{case}: the refusal is stated", "C562")
    check("could not start" in str(st.get("sentence")) and str(absent.name) in str(st.get("sentence")),
          f"{case}: the sentence says the reader could not start and names it ({st.get('sentence')!r})")
    check(str(st.get("sentence")) in r.stderr, f"{case}: the same sentence is on stderr ({r.stderr.strip()[-200:]!r})")
    check(not manifest_rows(b, "names"), f"{case}: no names rows on refusal")
    expect(evalrun(["verify-bundle", b]).returncode, 0, f"{case}: the bundle verifies", "C562")


def case_563(tmp):
    """C563: a plugin whose bytes differ from the plan's: the names step is refused naming the plugin, nothing of it
    is written, and the bundle still verifies."""
    case = "case-563-bundle-plugin-changed"
    run, plan, arm = names_run(tmp, case)
    target_name = (plan.get("targets") or [{}])[0].get("name")
    target = next(Path(p["path"]) for p in arm["requested"]["plugins"]
                  if p.get("name") == target_name and p.get("path") and Path(p["path"]).is_file())
    original = target.read_bytes()
    b = tmp / f"{case}-b"
    try:
        target.write_bytes(original + b"\0")
        r = evalrun(["bundle", "--run", run, "--out", b])
    finally:
        target.write_bytes(original)
    check(sha_of(target) == hashlib.sha256(original).hexdigest(), f"{case}: the plugin was restored by content")
    expect(r.returncode, 0, f"{case}: the bundle is made ({r.stderr.strip()[-200:]})", "C563")
    stated_or_enriched(b, case, "C563")
    st = names_status(b)
    expect(st.get("status"), "refused", f"{case}: the refusal is stated", "C563")
    check(target.name in str(st.get("sentence")), f"{case}: the sentence names the plugin ({st.get('sentence')!r})")
    check(not manifest_rows(b, "names"), f"{case}: nothing of the names step was written")
    expect(evalrun(["verify-bundle", b]).returncode, 0, f"{case}: the bundle verifies", "C563")


def case_564(tmp):
    """C564: a partial read (every base record unread): the names are kept, the statement says partial with the
    counts, names.json lists what was not read, and the bundle verifies."""
    case = "case-564-bundle-partial-read"
    run, plan, arm = names_run(tmp, case)
    b = tmp / f"{case}-b"
    reader = f"{sys.executable} {FIXT / 'fake_records_mcp.py'} --unread 0BB0"
    r = evalrun(["bundle", "--run", run, "--out", b, "--reader-cmd", reader])
    expect(r.returncode, 0, f"{case}: bundle exit ({r.stderr.strip()[-200:]})", "C564")
    stated_or_enriched(b, case, "C564")
    st = names_status(b)
    doc = names_doc(b) or {}
    unread = doc.get("unread") or {}
    expect(st.get("status"), "partial", f"{case}: a partial read is stated as partial", "C564")
    check(bool(unread.get("bases")) and all(str(x).startswith("0BB0") for x in unread["bases"]),
          f"{case}: names.json lists the unread bases ({unread!r})")
    expect((st.get("counts") or {}).get("basesUnread"), len(unread.get("bases") or []),
           f"{case}: the statement counts the unread bases", "C564")
    check(str(len(unread.get("bases") or [])) in str(st.get("sentence")),
          f"{case}: the sentence carries the count ({st.get('sentence')!r})")
    check(str(st.get("sentence")) in r.stderr + r.stdout, f"{case}: the sentence is printed")
    expect(evalrun(["verify-bundle", b]).returncode, 0, f"{case}: the bundle verifies", "C564")


def case_565(tmp):
    """C565: verify-bundle covers the statement and the new names fields: a statement contradicting the files, counts
    that disagree with names.json, an unknown status and a sentence-less statement each fail verification."""
    case = "case-565-verify-names-statement"
    run, plan, arm = names_run(tmp, case)
    good = tmp / f"{case}-good"
    expect(evalrun(["bundle", "--run", run, "--out", good]).returncode, 0, f"{case}: bundle", "setup")
    expect(evalrun(["verify-bundle", good]).returncode, 0, f"{case}: the untouched bundle verifies", "C565")
    skipped = tmp / f"{case}-skipped"
    expect(evalrun(["bundle", "--run", run, "--out", skipped, "--no-names"]).returncode, 0, f"{case}: bundle", "setup")

    def arm_(label, src, edit, want_kind):
        b = tmp / f"{case}-{label}"
        shutil.copytree(src, b)
        m = load(b / "manifest.json")
        if not isinstance((m.get("enrichmentStatus") or {}).get("names"), dict):
            check(False, f"{case}: {label}: the bundle carries no names statement to tamper with")
            return
        edit(m, b)
        (b / "manifest.json").write_text(json.dumps(m, indent=1), encoding="utf-8")
        v = evalrun(["verify-bundle", b])
        kinds = [x.get("kind") for x in ((load(b / "verify.json") or {}).get("bundle") or {}).get("defects") or []]
        check(v.returncode == 3 and want_kind in kinds,
              f"{case}: {label} fails verification as {want_kind} (exit {v.returncode}, defects {kinds})")

    def st(m):
        return m["enrichmentStatus"]["names"]

    def drop_rows(m, b):
        m["evidence"] = [e for e in m["evidence"] if e.get("enrichment") != "names"]
        m["required"] = [p for p in m["required"] if not p.startswith("enrichment/names/")]
    arm_("enriched-without-names", good, drop_rows, "enrichment-status-contradiction")
    arm_("skipped-with-names", good, lambda m, b: st(m).update(status="skipped"), "enrichment-status-contradiction")
    arm_("partial-counts-differ", good, lambda m, b: st(m).update(status="partial",
                                                                    counts={**st(m)["counts"], "basesUnread": 3}),
         "enrichment-status-mismatch")
    arm_("unknown-status", skipped, lambda m, b: st(m).update(status="maybe"), "enrichment-status-unstated")
    arm_("no-sentence", skipped, lambda m, b: st(m).update(sentence=""), "enrichment-status-unstated")


def case_566(tmp):
    """C566: the reader default: --reader-cmd, else HOUSECARL_RECORD_READER_CMD, else this checkout's built server;
    with none, one sentence naming where the server was looked for."""
    import importlib.util
    case = "case-566-reader-default"
    spec = importlib.util.spec_from_file_location("evalrun_case566", EVALRUN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    saved = os.environ.get("HOUSECARL_RECORD_READER_CMD")
    try:
        os.environ["HOUSECARL_RECORD_READER_CMD"] = "from-env"
        expect(mod.default_reader_cmd("from-flag"), ("from-flag", "--reader-cmd"), f"{case}: the flag wins", "C566")
        expect(mod.default_reader_cmd(None), ("from-env", "HOUSECARL_RECORD_READER_CMD"),
               f"{case}: the variable is next", "C566")
        os.environ.pop("HOUSECARL_RECORD_READER_CMD")
        fake_root = tmp / f"{case}-checkout"
        mod.HERE = fake_root / "_geometry-harness"
        cmd, why = mod.default_reader_cmd(None)
        exe = fake_root / "src" / "housecarl-mcp" / "bin" / "Release" / "net9.0" / mod.exe_name("housecarl-mcp")
        check(cmd is None and str(exe) in why and why.endswith("."),
              f"{case}: with no server built, one sentence naming where it was looked for ({cmd!r}, {why!r})")
        exe.parent.mkdir(parents=True, exist_ok=True)
        exe.write_bytes(b"")
        cmd, why = mod.default_reader_cmd(None)
        check(cmd is not None and str(exe) in cmd and why == "checkout",
              f"{case}: the checkout's built server is the default ({cmd!r}, {why!r})")
    finally:
        if saved is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = saved


CASES["case-560"] = case_560
CASES["case-561"] = case_561
CASES["case-562"] = case_562
CASES["case-563"] = case_563
CASES["case-564"] = case_564
CASES["case-565"] = case_565
CASES["case-566"] = case_566

# ---- rebundling a finished run after its registry changed (cases 590-593)
# Each case plans from its OWN copy of the fixture registry, so changing that copy disturbs no other case, and keeps a
# frozen copy aside: the file an earlier bundle's registry/check-registry.json would hold.

REBUNDLE_CASE = "case-11-bundle-relocate"  # a finished run with receipts, a disposition and a scope


def rebundle_setup(tmp: Path, case: str):
    reg = GEN / "registry" / f"{case}-registry.json"
    shutil.copy2(GEN / "registry" / "fixture-check-registry.json", reg)
    run = tmp / case
    _, _, _, scen = case_paths(REBUNDLE_CASE)
    do_plan(run, REBUNDLE_CASE, registry=reg.name)
    evalrun(["run", "--run", run], scen, expect_exit=0)
    evalrun(["finalize", "--run", run], scen, expect_exit=0)
    frozen = tmp / f"{case}-frozen" / "check-registry.json"
    frozen.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(reg, frozen)
    return run, reg, frozen


def change_registry(reg: Path) -> None:
    """The registry changes under the finished run, the way sibling work adds a check to the shipped one."""
    doc = load(reg)
    doc["rebundleTestEdit"] = "the registry changed after this plan froze"
    reg.write_text(json.dumps(doc, indent=1), encoding="utf-8")


def sha(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def bundle_defects(out: Path) -> list[str]:
    v = load(out / "verify.json") or {}
    return [d.get("kind") for d in (v.get("bundle") or {}).get("defects") or []]


def case_590(tmp):
    """A rebundle after the registry changed, with nothing named: refused by name before anything is written, never a
    bundle that only fails at verify."""
    case = "case-590"
    run, reg, _ = rebundle_setup(tmp, case)
    change_registry(reg)
    out = tmp / f"{case}-bundle"
    b = evalrun(["bundle", "--run", run, "--out", out])
    later = ""
    if (out / "manifest.json").exists():
        v = evalrun(["verify-bundle", out])
        later = f"; the bundle was written and verify-bundle exits {v.returncode} with {bundle_defects(out)}"
    expect(b.returncode, 3, f"{case}: bundle refuses a run whose registry changed{later}", "rebundle")
    check("registry-changed" in b.stderr and "--registry" in b.stderr,
          f"{case}: the refusal is registry-changed and names --registry ({b.stderr.strip()[-300:]!r})")
    check(not (out / "manifest.json").exists(), f"{case}: no bundle is written{later}")


def case_591(tmp):
    """Naming the frozen registry rebundles the run from an unrelated folder, and the bundle verifies."""
    case = "case-591"
    run, reg, frozen = rebundle_setup(tmp, case)
    change_registry(reg)
    elsewhere = tmp / f"{case}-elsewhere"
    elsewhere.mkdir()
    out = tmp / f"{case}-bundle"
    b = evalrun(["bundle", "--run", run, "--registry", frozen, "--out", out], cwd=elsewhere)
    expect(b.returncode, 0, f"{case}: bundle --registry FROZEN exit ({b.stderr.strip()[-300:]!r})", "rebundle")
    v = evalrun(["verify-bundle", out], cwd=elsewhere)
    expect(v.returncode, 0, f"{case}: verify-bundle exit", "rebundle")
    expect([k for k in bundle_defects(out) if k.startswith("registry")], [], f"{case}: no registry defect")
    plan = load(run / "plan.json") or {}
    copy = out / "registry" / "check-registry.json"
    expect(sha(copy) if copy.exists() else None, plan["registry"]["sha256"],
           f"{case}: the bundled registry is the one the plan froze")


def case_592(tmp):
    """A named file that is not what the plan froze is a refused input (exit 2), and nothing is written."""
    case = "case-592"
    run, reg, frozen = rebundle_setup(tmp, case)
    change_registry(reg)
    want = (load(run / "plan.json") or {})["registry"]["sha256"][:12]
    for label, args, cause, tokens in (
            ("changed file", ["--registry", reg], "registry-mismatch", [sha(reg)[:12], want]),
            ("missing file", ["--registry", tmp / f"{case}-nowhere.json"], "registry-missing", [want]),
            ("wrong scope", ["--registry", frozen, "--scope", frozen], "scope-mismatch", [])):
        out = tmp / f"{case}-{label.replace(' ', '-')}"
        b = evalrun(["bundle", "--run", run, "--out", out] + args)
        expect(b.returncode, 2, f"{case}/{label}: refused as an input", "rebundle")
        check(cause in b.stderr and all(t in b.stderr for t in tokens),
              f"{case}/{label}: the refusal is {cause} naming {tokens} ({b.stderr.strip()[-300:]!r})")
        check(not (out / "manifest.json").exists(), f"{case}/{label}: no bundle is written")


def case_593(tmp):
    """An older plan that stored its registry and scope relative to the folder it was planned from. From that folder
    it bundles as before; from any other it is refused by name, and naming both files bundles one that verifies."""
    case = "case-593"
    run, reg, _ = rebundle_setup(tmp, case)
    plan_path = run / "plan.json"
    plan = load(plan_path)
    scope = Path(plan["scope"]["path"])
    # the shape of a plan written before plans stored absolute paths, with its immutability hash rewritten to match
    plan["registry"]["path"] = os.path.relpath(reg, GEN)
    plan["scope"]["path"] = os.path.relpath(scope, GEN)
    plan_path.write_text(json.dumps(plan, indent=1), encoding="utf-8")
    (run / "plan.sha256").write_text(sha(plan_path) + "\n", encoding="utf-8")

    home = tmp / f"{case}-home-bundle"
    b = evalrun(["bundle", "--run", run, "--out", home], cwd=GEN)
    expect(b.returncode, 0, f"{case}/planned-from folder: bundle exit ({b.stderr.strip()[-300:]!r})", "rebundle")
    expect(evalrun(["verify-bundle", home]).returncode, 0, f"{case}/planned-from folder: verify-bundle exit")

    elsewhere = tmp / f"{case}-elsewhere"
    elsewhere.mkdir()
    out = tmp / f"{case}-bundle"
    b = evalrun(["bundle", "--run", run, "--out", out], cwd=elsewhere)
    later = ""
    if (out / "manifest.json").exists():
        v = evalrun(["verify-bundle", out])
        later = f"; the bundle was written and verify-bundle exits {v.returncode} with {bundle_defects(out)}"
    expect(b.returncode, 3, f"{case}/other folder: bundle refuses the unresolvable registry path{later}", "rebundle")
    check("plan-path-relative" in b.stderr and "--registry" in b.stderr,
          f"{case}/other folder: refused plan-path-relative naming --registry ({b.stderr.strip()[-300:]!r})")
    check(not (out / "manifest.json").exists(), f"{case}/other folder: no bundle is written{later}")

    named = tmp / f"{case}-named-bundle"
    b = evalrun(["bundle", "--run", run, "--registry", reg, "--scope", scope, "--out", named], cwd=elsewhere)
    expect(b.returncode, 0, f"{case}/named: bundle exit ({b.stderr.strip()[-300:]!r})", "rebundle")
    v = evalrun(["verify-bundle", named], cwd=elsewhere)
    expect(v.returncode, 0, f"{case}/named: verify-bundle exit ({bundle_defects(named)})", "rebundle")


CASES["case-590"] = case_590
CASES["case-591"] = case_591
CASES["case-592"] = case_592
CASES["case-593"] = case_593


def case_800(tmp):
    """Item 18 phase 3: `enrich --kind order-variant` is a kind evalrun takes. On case-01's evaluation it either derives
    the reversed order's rows (exit 0, a manifest row of that kind, and the copy still verifies) or refuses in one
    sentence that says why the order cannot be reversed (exit 2); never a usage error."""
    case = "case-800-enrich-order-variant"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    b1 = tmp / f"{case}-b1"
    shutil.copytree(bundle, b1)
    r = evalrun(["enrich", "--bundle", b1, "--kind", "order-variant"])
    check("invalid choice" not in r.stderr, f"{case}: the kind is accepted ({r.stderr.strip()[-200:]!r})")
    if r.returncode == 0:
        rows = manifest_rows(b1, "order-variant")
        check(bool(rows), f"{case}: an order-variant row is in the manifest")
        expect(evalrun(["verify-bundle", b1]).returncode, 0, f"{case}: the enriched copy verifies", "phase 3")
    else:
        expect(r.returncode, 2, f"{case}: a refusal, not a crash ({r.stderr.strip()[-200:]!r})", "phase 3")
        check("invalid choice" not in r.stderr and "order" in r.stderr.lower() and r.stderr.strip().count(". ") <= 1,
              f"{case}: one sentence saying why the order cannot be reversed ({r.stderr.strip()[-200:]!r})")


def case_801(tmp):
    """Item 18 phase 3: every bundle says whether its evaluated pair's order is a choice: the manifest's `order` status
    is derived, forced, unknown or refused, with a sentence, whatever the reader settings."""
    case = "case-801-bundle-order-status"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    st = (load(bundle / "manifest.json") or {}).get("order") or {}
    check(st.get("status") in ("derived", "forced", "unknown", "refused") and bool(st.get("sentence")),
          f"{case}: the manifest's order status ({st})")


CASES["case-800"] = case_800
CASES["case-801"] = case_801


# ------------------------------------------------------- the measured primitive table in a plan: cases 1730-1733
# stateprobe decides collision-marker primitives only when it is given station Q's table (--primitive-solidity). The plan
# finds it (the flag, else HOUSECARL_PRIMITIVE_SOLIDITY, else `primitiveSolidity` in the file HOUSECARL_HARNESS_CONFIG
# names), freezes a copy with its sha256, and the state check passes the copy; with none it says so and runs without.

SL_TABLE = [{"kind": "base", "value": "000804:MxRoom.esm", "layer": "none", "verdict": "solid", "stops": ["player", "items"],
             "evidence": "fixture"}]


def _sl_plan_run(tmp: Path, name: str, env_over: dict, run_it: bool = True, between=None):
    d = GEN / "cases" / P25_CASE
    run = tmp / name
    scen = d / "scenario.json"
    env = {k: v for k, v in os.environ.items() if k not in ("HOUSECARL_HARNESS_CONFIG", "HOUSECARL_PRIMITIVE_SOLIDITY")}
    env.update(env_over)
    env["EVALRUN_FAKE_SCENARIO"] = str(scen)
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json", "--scope", d / "scope.json",
            "--candidates", d / "candidates", "--out", run, "--harness", REAL_HARNESS, "--states", "baseline,fixture-switched",
            "--select", "state.switch-census,state.conditional-geometry"] + probe_cmds()
    p = subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True, text=True, env=env, timeout=300)
    if between and (run / "plan.json").exists():
        between(run)
    r = (subprocess.run([*CONTROLLER, "run", "--run", str(run)], capture_output=True, text=True, env=env, timeout=300)
         if run_it and p.returncode == 0 else None)
    return run, p, r


def _sl_table(tmp: Path, name: str) -> Path:
    f = tmp / f"{name}-table.json"
    f.write_text(json.dumps(SL_TABLE), encoding="utf-8")
    return f


def _sl_config(tmp: Path, name: str, table: Path) -> Path:
    cfg = tmp / f"{name}-harness.json"
    cfg.write_text(json.dumps({"primitiveSolidity": str(table).replace("\\", "/")}), encoding="utf-8")
    return cfg


def _sl_state_entries(run: Path) -> list:
    led = ledger_entries(load(run / "ledger.json") or {})
    return [v for k, v in led.items() if k.startswith("state.conditional-geometry__")]


def case_1730(tmp):
    """The harness config names the table: the plan freezes a copy with its sha256 and every stateprobe launch gets it.
    Rejects: an evaluation that never passes the table, or passes the live file rather than the plan's copy."""
    case = "case-1730-primitive-table-frozen"
    table = _sl_table(tmp, case)
    run, p, r = _sl_plan_run(tmp, case, {"HOUSECARL_HARNESS_CONFIG": str(_sl_config(tmp, case, table))})
    expect(p.returncode, 0, f"{case}: the plan is made ({p.stdout[-300:]} {p.stderr[-300:]})")
    rec = ((load(run / "plan.json") or {}).get("inputs") or {}).get("primitiveSolidity") or {}
    frozen = run / str(rec.get("file") or "missing")
    check(rec.get("sha256") == sha_of(table) and frozen.is_file() and sha_of(frozen) == sha_of(table) and not same_file(frozen, table),
          f"{case}: the plan records a frozen copy of the table and its sha256 ({rec})")
    cmds = _w2_state_cmds(run)
    check(cmds and all(("--primitive-solidity " + str(frozen)) in c for c in cmds),
          f"{case}: every stateprobe launch gets --primitive-solidity <the plan's copy> ({[c[-200:] for c in cmds]})")


def case_1731(tmp):
    """No table anywhere: the plan says so in its record and on its console, and the state check runs without the flag.
    Rejects: a silent run without the table, or a refusal that takes the check from a machine without one."""
    case = "case-1731-primitive-table-absent"
    run, p, r = _sl_plan_run(tmp, case, {})
    expect(p.returncode, 0, f"{case}: the plan is made without a table ({p.stderr[-300:]})")
    rec = ((load(run / "plan.json") or {}).get("inputs") or {}).get("primitiveSolidity") or {}
    check(rec.get("given") is False and "primitiveSolidity" in str(rec.get("why") or ""),
          f"{case}: the plan records that no table was given, and where it looked ({rec})")
    check("no primitive table" in p.stdout.lower(), f"{case}: the plan's console says no primitive table was given")
    cmds = _w2_state_cmds(run)
    check(cmds and not any("--primitive-solidity" in c for c in cmds),
          f"{case}: stateprobe runs without the flag ({[c[-160:] for c in cmds]})")


def case_1732(tmp):
    """The config names a table that is not there: the plan refuses in one sentence. Rejects: a silent fall-through to no table."""
    case = "case-1732-primitive-table-missing"
    run, p, r = _sl_plan_run(tmp, case, {"HOUSECARL_HARNESS_CONFIG": str(_sl_config(tmp, case, tmp / "no-such-table.json"))}, run_it=False)
    out = (p.stdout + p.stderr)
    check(p.returncode != 0 and "no-such-table.json" in out and "primitive" in out.lower() and not (run / "plan.json").exists(),
          f"{case}: the plan refuses and names the missing table (exit {p.returncode}: {out.strip()[-300:]})")


def case_1733(tmp):
    """The plan's copy changed after planning: the state check is blocked by name and launches nothing.
    Rejects: a run with a table other than the one the plan froze."""
    case = "case-1733-primitive-table-changed"
    table = _sl_table(tmp, case)

    def damage(run):
        rec = ((load(run / "plan.json") or {}).get("inputs") or {}).get("primitiveSolidity") or {}
        if rec.get("file"):
            (run / rec["file"]).write_text(json.dumps(SL_TABLE + [dict(SL_TABLE[0], layer="L_STATIC")]), encoding="utf-8")

    run, p, r = _sl_plan_run(tmp, case, {"HOUSECARL_HARNESS_CONFIG": str(_sl_config(tmp, case, table))}, between=damage)
    ent = _sl_state_entries(run)
    check(ent and all(execution_of(e) == "blocked(primitive-table-changed)" for e in ent) and not _w2_state_cmds(run),
          f"{case}: a changed copy blocks the state check and launches nothing ({[execution_of(e) for e in ent]})")


def same_file(a: Path, b: Path) -> bool:
    try:
        return a.resolve() == b.resolve()
    except OSError:
        return False


CASES["case-1730"] = case_1730
CASES["case-1731"] = case_1731
CASES["case-1732"] = case_1732
CASES["case-1733"] = case_1733


# ------------------------------------------------------- the candidate search frozen by the plan: cases 2328-2329
# search/candidates.json used to be copied unhashed and left out silently (2026-11-06-search-candidates-unhashed): the
# plan now freezes its hash or records it absent with a sentence, bundle copies it only when it hashes to the plan, and
# verify checks the copy.

def _search_fixture(tmp: Path, name: str, with_search: bool = True) -> tuple[Path, Path]:
    """A private copy of case-01's inputs (its candidates folder is edited here, never the shared one)."""
    src = GEN / "cases" / "case-01-geometry-and-quest"
    d = tmp / f"{name}-inputs"
    shutil.copytree(src, d)
    if not with_search:
        (d / "candidates" / "candidates.json").unlink()
    return d, tmp / name


def _search_plan(d: Path, run: Path):
    args = ["plan", "--registry", GEN / "registry" / "fixture-check-registry.json", "--scope", d / "scope.json",
            "--candidates", d / "candidates", "--out", run, "--harness", REAL_HARNESS] + probe_cmds()
    return evalrun(args, d / "scenario.json", expect_exit=0)


def _search_bundle(d: Path, run: Path):
    evalrun(["run", "--run", run], d / "scenario.json")
    evalrun(["finalize", "--run", run], d / "scenario.json")
    return evalrun(["bundle", "--run", run], d / "scenario.json")


def case_2328(tmp):
    """C2328: the plan freezes candidates.json's sha256 and the bundle carries that copy; with no candidates.json the
    plan and the manifest say so in a sentence. Rejects: a plan with no record of the search, a silent absence."""
    case = "case-2328-search-frozen"
    d, run = _search_fixture(tmp, case)
    p = _search_plan(d, run)
    rec = (load(run / "plan.json") or {}).get("search") or {}
    want = sha_of(d / "candidates" / "candidates.json")
    check(p.returncode == 0 and rec.get("sha256") == want, f"{case}: the plan freezes the search's sha256 ({rec})")
    b = _search_bundle(d, run)
    man = load(run / "bundle" / "manifest.json") or {}
    copy = run / "bundle" / "search" / "candidates.json"
    check(b.returncode == 0 and man.get("search") == "search/candidates.json" and copy.is_file()
          and sha_of(copy) == want, f"{case}: the bundle carries the frozen copy (exit {b.returncode}, {man.get('search')})")
    d2, run2 = _search_fixture(tmp, case + "-absent", with_search=False)
    p2 = _search_plan(d2, run2)
    rec2 = (load(run2 / "plan.json") or {}).get("search") or {}
    check(p2.returncode == 0 and rec2.get("sha256") is None and "holds no candidates.json" in str(rec2.get("absent"))
          and "holds no candidates.json" in p2.stdout,
          f"{case}: an absent search is recorded and said by the plan ({rec2}; {p2.stdout[-200:]!r})")
    b2 = _search_bundle(d2, run2)
    man2 = load(run2 / "bundle" / "manifest.json") or {}
    check(b2.returncode == 0 and man2.get("search") is None and "holds no candidates.json" in str(man2.get("searchAbsent")),
          f"{case}: the manifest says the search is absent rather than naming a file it lacks "
          f"(exit {b2.returncode}, search {man2.get('search')!r}, searchAbsent {man2.get('searchAbsent')!r})")


def case_2329(tmp):
    """C2329: a candidates.json changed after the plan refuses the bundle by name; a bundle copy changed after
    bundling is a verify defect. Rejects: a search copied under a hash the plan never froze."""
    case = "case-2329-search-changed"
    d, run = _search_fixture(tmp, case)
    _search_plan(d, run)
    f = d / "candidates" / "candidates.json"
    f.write_text(f.read_text(encoding="utf-8").replace('"evaluated"', '"evaluated" ', 1), encoding="utf-8")
    b = _search_bundle(d, run)
    out = b.stdout + b.stderr
    check(b.returncode != 0 and "search-changed" in out and not (run / "bundle" / "manifest.json").exists(),
          f"{case}: a changed search refuses the bundle by name (exit {b.returncode}: {out.strip()[-300:]})")
    d2, run2 = _search_fixture(tmp, case + "-copy")
    _search_plan(d2, run2)
    _search_bundle(d2, run2)
    copy = run2 / "bundle" / "search" / "candidates.json"
    if copy.is_file():
        copy.write_text(copy.read_text(encoding="utf-8") + " ", encoding="utf-8")
    evalrun(["verify-bundle", run2 / "bundle"])
    kinds = [x.get("kind") for x in (((load(run2 / "bundle" / "verify.json") or {}).get("bundle") or {}).get("defects") or [])]
    check(copy.is_file() and "search-mismatch" in kinds, f"{case}: verify reports the changed search copy ({kinds})")


CASES["case-2328"] = case_2328
CASES["case-2329"] = case_2329



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
    # every bundle the suite makes runs its names step with the stand-in reader, never a built houseCARL server
    os.environ["HOUSECARL_RECORD_READER_CMD"] = f"{sys.executable} {FIXT / 'fake_records_mcp.py'}"
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
