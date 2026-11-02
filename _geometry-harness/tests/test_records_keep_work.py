"""Known answers for the records stage's reads and for keeping finished work across a stop or a budget change.

    python test_records_keep_work.py [PREFIX]     KEEP_HARNESS names the _geometry-harness copy under test.

Real evalrun, ordersweep and conflictdiff over the fixtures with fake probes; real evalrecords with a stand-in reader;
real evalpipeline.cmd_run with recording stage bodies. No game, no lab, no houseCARL server.
"""
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = Path(os.environ.get("KEEP_HARNESS") or HERE.parent).resolve()
sys.dont_write_bytecode = True
sys.path[:0] = [str(HARNESS / "tests"), str(HARNESS), str(HARNESS.parent / "scripts")]
import test_evalrun as T  # noqa: E402

T.EVALRUN = HARNESS / "evalrun.py"
FAKE = HERE / "fake_capture_mcp.py" if (HERE / "fake_capture_mcp.py").is_file() else HERE / "fixtures/evalrun/fake_capture_mcp.py"
NAV = "geometry.navmesh-ownership"
BUDGET = 0.3
CHECKS = ["loadability.dangling-references", "records.contested-fields", "records.cell-attributes",
          "records.registration-and-dialogue", "loadability.missing-assets"]


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    sys.modules[name] = m
    spec.loader.exec_module(m)
    return m


def sha(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


# ---------------------------------------------------------------- the record capture, against a stand-in reader

class Lab:
    """A plan with one arm, its plugin and mod files, and a capture command that counts reader sessions."""

    def __init__(self, tmp: Path, evaluated=("Subj.esp",)):
        self.t = tmp
        self.data, self.mods = tmp / "Data", tmp / "inst" / "mods"
        self.data.mkdir(parents=True)
        (self.mods / "M1").mkdir(parents=True)
        (tmp / "inst" / "overwrite").mkdir()
        (self.mods / "M1" / "a.nif").write_text("mesh", encoding="utf-8")
        (self.data / "b.dds").write_text("texture", encoding="utf-8")
        (tmp / "inst" / "overwrite" / "c.nif").write_text("over", encoding="utf-8")
        self.profile = tmp / "profile"
        self.profile.mkdir()
        (self.profile / "skyrim.ini").write_text("[General]\n", encoding="utf-8")
        self.plugins = ["Base.esm", "Win.esp", *evaluated]
        for p in self.plugins:
            (self.data / p).write_bytes(p.encode())
        self.run_dir = tmp / "run"
        self.run_dir.mkdir()
        (tmp / "prep.json").write_text(json.dumps({"baseline": ["Base.esm", "Win.esp"]}), encoding="utf-8")
        (tmp / "cells.json").write_text(json.dumps(["0000D1:Base.esm"]), encoding="utf-8")
        self.log = tmp / "reader-requests.jsonl"
        self.write_plan()
        self.n = 0

    def write_plan(self, order=None):
        plugins = order or self.plugins
        req = {"plugins": [{"name": p, "path": str(self.data / p), "sha256": sha(self.data / p)} for p in self.plugins],
               "providers": [{"name": "M1", "kind": "mod"}], "overlays": [], "population": "load-time"}
        arm = {"armId": "baseline", "kind": "baseline", "plugins": plugins, "mods": ["M1"], "candidateFile": "c.json",
               "candidateSha256": "c" * 64, "requested": req}
        plan = {"planId": "p" * 12, "arms": [arm],
                "checks": [{"checkId": c, "adapter": {"kind": "mcp-only"},
                            "arms": [{"armId": "baseline", "status": "mcp-only"}]} for c in CHECKS]}
        (self.run_dir / "plan.json").write_text(json.dumps(plan), encoding="utf-8")

    def requests(self):
        return [json.loads(l) for l in self.log.read_text(encoding="utf-8").splitlines()] if self.log.is_file() else []

    def capture(self):
        """Returns how many reader sessions this capture started."""
        before = sum(1 for r in self.requests() if r.get("method") == "initialize")
        self.n += 1
        er = str(HARNESS / "evalrecords.py")
        args = [sys.executable, er, "capture", "--run", str(self.run_dir), "--baseline-from", str(self.t / "prep.json"),
                "--reader-cmd", f'"{sys.executable}" "{FAKE}"', "--data", str(self.data), "--mods", str(self.mods),
                "--profile-dir", str(self.profile), "--out", str(self.t / f"out-{self.n}"),
                "--checkpoints", str(self.t / "ckpt")]
        if "--cells" in subprocess.run([sys.executable, er, "capture", "--help"], capture_output=True, text=True).stdout:
            args += ["--cells", str(self.t / "cells.json")]
        p = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                           env=dict(os.environ, FAKE_CAPTURE_LOG=str(self.log), HOUSECARL_RUNLOG_DIR=str(self.t / "runlog")))
        self.last = p
        T.check(p.returncode == 0, f"the capture finished (exit {p.returncode}: {p.stderr[-200:]} {p.stdout[-200:]})")
        return sum(1 for r in self.requests() if r.get("method") == "initialize") - before


def case_r1_reads_only_what_an_evaluated_plugin_touches(tmp):
    lab = Lab(tmp / "r1")
    lab.capture()
    calls = [r["params"]["arguments"] for r in lab.requests() if r.get("method") == "tools/call"]
    T.check(not [a for a in calls if a.get("where")], "r1: no comparison-cell query is asked")
    asked = {f for a in calls if (a.get("project") or {}).get("form") == "tree" for f in a["formids"]}
    T.expect(sorted(asked), ["0000A1:Base.esm"], "r1: trees are read only for contested records an evaluated plugin touches")


def case_r2_the_row_and_its_coverage_stand(tmp):
    lab = Lab(tmp / "r2")
    lab.capture()
    summary = json.loads((tmp / "r2" / "out-1" / "capture-summary.json").read_text(encoding="utf-8"))
    rows = next(Path(p).parent / "rows.json" for p in summary["baseline"]["imports"] if "records.contested-fields" in p)
    doc = json.loads(rows.read_text(encoding="utf-8"))
    T.expect([c["Field"] for c in doc["Conflicts"]], ["Name"], "r2: the lost edit is the one row")
    T.expect(doc["Coverage"][0].get("Members"), 1, "r2: Members counts the contested record the plugin touches")
    T.expect(doc["Coverage"][0].get("Unjudgeable"), {}, "r2: nothing is unjudgeable")


def case_r3_an_arm_with_no_evaluated_plugin_reads_no_tree(tmp):
    lab = Lab(tmp / "r3", evaluated=())
    lab.capture()
    calls = [r["params"]["arguments"] for r in lab.requests() if r.get("method") == "tools/call"]
    T.expect([a for a in calls if (a.get("project") or {}).get("form") == "tree"], [],
             "r3: the dependencies arm reads no tree")


def case_r4_key_what_rereads(tmp):
    lab = Lab(tmp / "r4")
    T.expect(lab.capture(), 1, "r4: the first capture reads")
    T.expect(lab.capture(), 0, "r4: an unchanged arm is reused with no reader session")
    # same size, same last write: only the plugin's hash can see this one, not the asset listing
    st = (lab.data / "Subj.esp").stat()
    (lab.data / "Subj.esp").write_bytes(b"X" + (lab.data / "Subj.esp").read_bytes()[1:])
    os.utime(lab.data / "Subj.esp", ns=(st.st_atime_ns, st.st_mtime_ns))
    T.expect(lab.capture(), 1, "r4: a plugin's changed bytes reread the arm")
    T.expect(lab.capture(), 0, "r4: then it is reused again")
    (lab.mods / "M1" / "new.nif").write_text("x", encoding="utf-8")
    T.expect(lab.capture(), 1, "r4: a loose file added to the arm's mod rereads the arm")
    (lab.data / "b.dds").write_text("texture, longer", encoding="utf-8")
    T.expect(lab.capture(), 1, "r4: a changed file in Data rereads the arm")
    (tmp / "r4" / "inst" / "overwrite" / "c.nif").unlink()
    T.expect(lab.capture(), 1, "r4: a file gone from overwrite rereads the arm")
    (lab.profile / "skyrim.ini").write_text("[General]\nx=1\n", encoding="utf-8")
    T.expect(lab.capture(), 1, "r4: a changed INI rereads the arm")
    lab.write_plan(order=["Win.esp", "Base.esm", "Subj.esp"])
    T.expect(lab.capture(), 1, "r4: a changed order rereads the arm")


def case_r6_what_a_game_launch_leaves_behind_does_not_reread(tmp):
    """Round 2's overwrite took 327 screenshots and a few settings files from lab launches; none is an asset a row reads."""
    lab = Lab(tmp / "r6")
    T.expect(lab.capture(), 1, "r6: the first capture reads")
    over = tmp / "r6" / "inst" / "overwrite"
    (over / "Root").mkdir()
    (over / "Root" / "ScreenShot1.png").write_bytes(b"png")
    (over / "SKSE.log").write_text("log", encoding="utf-8")
    (over / "settings.ini").write_text("[x]", encoding="utf-8")
    T.expect(lab.capture(), 0, "r6: a screenshot, a log and a settings file in overwrite do not reread the arm")
    (over / "textures").mkdir()
    (over / "textures" / "new.dds").write_bytes(b"dds")
    T.expect(lab.capture(), 1, "r6: a new asset in overwrite does reread the arm")
    (over / "x.bsa").write_bytes(b"bsa")
    T.expect(lab.capture(), 1, "r6: a new archive in overwrite does reread the arm")


def case_r5_a_reader_rebuilt_beside_its_exe_is_another_reader(tmp):
    er = load_module("er_under_test", HARNESS / "evalrecords.py")
    d = tmp / "r5"
    d.mkdir()
    (d / "reader.exe").write_bytes(b"apphost")
    (d / "reader.dll").write_bytes(b"build one")
    one = er.reader_identity(f'"{d / "reader.exe"}"')
    (d / "reader.dll").write_bytes(b"build two")
    two = er.reader_identity(f'"{d / "reader.exe"}"')
    T.check(one != two, "r5: a changed dll beside an unchanged exe changes the reader's identity")


# ---------------------------------------------------------------- the pipeline's stage bindings

ENTRY = {"id": "t", "source": {"instance": "i", "profile": "p"}, "lab": {"instance": "i", "game": "g"},
         "subject": {"plugin": "S.esp", "mod": "M"}, "evaluation": {"depth": "deep", "budgetMinutes": 105, "optIn": []}}


class Pipe:
    def __init__(self, tmp: Path):
        self.EP = load_module("ep_under_test", HARNESS / "evalpipeline.py")
        self.out, self.ran, self.argv = tmp / "out", [], []
        self.entry = tmp / "entry.json"
        self.out.mkdir(parents=True)
        EP = self.EP
        EP.Context.resolve = lambda s: {"arms": [], "subject": ["S.esp"], "partners": [], "keys": [], "baseline": []}
        for stage in EP.STAGES:
            if stage != "scenes":
                EP.STAGE_BODIES[stage] = self.recorder(stage)
        real = EP.stage_evaluate

        def evaluate(ctx, res, sdir):
            self.ran.append("evaluate")
            return real(ctx, res, sdir)
        EP.STAGE_BODIES["evaluate"] = evaluate
        EP.evalrun = lambda *a: [str(x) for x in a]

        def child(ctx, ledger_dir, op, argv, **k):
            self.argv.append(argv)
            led = json.loads((self.out / "evaluation/ledger.json").read_text(encoding="utf-8"))
            led.setdefault("runIds", []).append({"runId": "x"})
            (self.out / "evaluation/ledger.json").write_text(json.dumps(led), encoding="utf-8")
            return {"exitCode": 0, "stopped": None}
        EP.child = child

    def recorder(self, stage):
        def body(ctx, res, sdir):
            self.ran.append(stage)
            if stage == "plan":
                (self.out / "evaluation").mkdir(exist_ok=True)
                minutes = ctx.entry["evaluation"]["budgetMinutes"]
                (self.out / "evaluation/plan.json").write_text(json.dumps({"budgets": {"minutes": float(minutes)}}), encoding="utf-8")
                (self.out / "evaluation/ledger.json").write_text('{"runIds": [], "entries": {}}', encoding="utf-8")
            return {}, []
        return body

    def run(self, budget):
        self.ran.clear()
        self.argv.clear()
        e = json.loads(json.dumps(ENTRY))
        e["evaluation"]["budgetMinutes"] = budget
        self.entry.write_text(json.dumps(e), encoding="utf-8")
        a = types.SimpleNamespace(entry=str(self.entry), machine=None, out=str(self.out), through="records")
        rc = self.EP.cmd_run(a)
        T.expect(rc, 0, "the pipeline run finished")
        return list(self.ran)


def case_p1_a_budget_only_change_keeps_the_plan(tmp):
    p = Pipe(tmp / "p1")
    first = p.run(105)
    T.expect(first, ["census", "prepare", "scope", "plan", "evaluate", "records"], "p1: a first run runs every stage")
    T.check(not any("--budget-minutes" in x for x in p.argv), "p1: an unchanged budget passes no limit")
    T.expect(p.run(105), [], "p1: the same entry reuses every stage")
    second = p.run(425)
    T.expect(second, ["evaluate", "records"], "p1: a raised budget reruns evaluate and what follows it, not the plan")
    T.check(p.argv and p.argv[0][:1] == ["resume"] and p.argv[0][-2:] == ["--budget-minutes", "425"],
            f"p1: evaluate resumes under the raised limit (argv {p.argv[:1]})")
    plan = json.loads((p.out / "evaluation/plan.json").read_text(encoding="utf-8"))
    T.expect(plan["budgets"]["minutes"], 105.0, "p1: the plan is not rewritten")


def case_p2_an_unchanged_budget_hashes_as_it_always_did(tmp):
    """Round 2's stages.json files were written by the old hash; a different shape makes them rerun from census."""
    p = Pipe(tmp / "p2")
    p.run(105)
    EP = p.EP
    stages = json.loads((p.out / "stages.json").read_text(encoding="utf-8"))["stages"]
    measured = json.loads(json.dumps(ENTRY))
    want = EP.sha256_json({"entry": EP.sha256_json(measured), "upstream": {}})
    T.expect(stages["census"]["inputsSha256"], want, "p2: census's binding is the entry's plain hash")
    p.run(425)
    stages = json.loads((p.out / "stages.json").read_text(encoding="utf-8"))["stages"]
    T.expect(stages["census"]["inputsSha256"], want, "p2: and stays so after a raised budget")


def case_p3_the_records_stage_names_no_comparison_cells(tmp):
    p = Pipe(tmp / "p3")
    EP, out = p.EP, p.out
    (out / "evaluation").mkdir()
    (out / "scope").mkdir()
    (out / "records").mkdir()
    (out / "scope/scopes.json").write_text(json.dumps({"a": {"targets": {"S.esp": {"measuredCells": ["0000D1:Base.esm"]}}}}), encoding="utf-8")
    (out / "evaluation/ledger.json").write_text(json.dumps({"entries": {"records.contested-fields__baseline": {"status": "mcp-only"}}}), encoding="utf-8")
    ctx = types.SimpleNamespace(out=out, id="t", machine={"readerCmd": "reader"}, game=Path("g"), instance=Path("i"), profile="p", entry={})
    res = {"subject": ["S.esp"], "partners": [], "keys": []}
    try:
        EP.stage_records(ctx, res, out / "records")
    except EP.Refusal:
        pass    # the stand-in child writes no summary; only the command line is read
    T.check(p.argv and "--cells" not in p.argv[0], "p3: the capture command names no comparison cells")
    T.check(not (out / "records/cells.json").exists(), "p3: no cells.json is written")


# ---------------------------------------------------------------- a stopped sweep

def ev(args, env, timeout=300):
    return subprocess.run([sys.executable, str(T.EVALRUN)] + [str(a) for a in args], capture_output=True, text=True,
                          env=dict(os.environ, **env), timeout=timeout)


class Sweep:
    """Case 8's fixture with a second candidate; navownprobe only; the slow fake on a named emit."""

    def __init__(self, tmp: Path, name: str, budget=BUDGET):
        self.tmp, self.run = tmp, tmp / name
        src = T.GEN / "cases" / "case-08-interrupt-resume"
        self.case = tmp / (name + "-case")
        shutil.copytree(src, self.case)
        cdir = self.case / "candidates"
        one = json.loads((cdir / "candidate-01-reorder.json").read_text(encoding="utf-8"))
        (cdir / "candidate-02-again.json").write_text(json.dumps(dict(one, Name="candidate-02-again")), encoding="utf-8")
        man = json.loads((cdir / "candidates.json").read_text(encoding="utf-8"))
        first = next(c for c in man["Candidates"] if "candidate-01" in c["File"])
        man["Candidates"].append(dict(first, File="candidate-02-again.json"))
        (cdir / "candidates.json").write_text(json.dumps(man), encoding="utf-8")
        self.slow = tmp / (name + "-slow.py")
        self.slow.write_text(f"FIXT_DIR = {str(T.FIXT)!r}\n" + T.DEADLINE_SLOWPROBE, encoding="utf-8")
        self.pid = tmp / (name + ".pids")
        self.env = {"EVALRUN_FAKE_SCENARIO": str(self.case / "scenario.json"), "HOUSECARL_RUNLOG_DIR": str(tmp / (name + "-log")),
                    "EVALRUN_SLOW_PIDFILE": str(self.pid)}
        p = ev(["plan", "--registry", T.GEN / "registry" / "fixture-check-registry.json", "--scope", self.case / "scope.json",
                "--candidates", cdir, "--out", self.run, "--harness", T.REAL_HARNESS] + T.probe_cmds()
               + ["--select", "navownprobe", "--budget-minutes", str(budget),
                  "--probe-cmd", f"navownprobe=python {self.slow} navownprobe"], self.env)
        T.expect(p.returncode, 0, f"{name}: plan exit ({p.stderr[-300:]})")
        self.plan = json.loads((self.run / "plan.json").read_text(encoding="utf-8"))
        self.arm = {Path(a["candidateFile"]).stem: a["armId"] for a in self.plan["arms"]}

    def go(self, verb, slow=None, *more):
        env = dict(self.env, **({"EVALRUN_SLOW_ARM": slow} if slow else {}))
        return ev([verb, "--run", self.run, *more], env)

    def status(self, stem, part):
        e = (json.loads((self.run / "ledger.json").read_text(encoding="utf-8")).get("entries") or {}).get(f"{NAV}__{self.arm[stem]}__{part}")
        return T.execution_of(e)

    def receipt(self, stem, part):
        p = self.run / "receipts" / f"{NAV}__{self.arm[stem]}__{part}.json"
        return json.loads(p.read_text(encoding="utf-8")) if p.is_file() else {}


BASE, C1, C2 = "candidate-00-baseline", "candidate-01-reorder", "candidate-02-again"


def classification(sweep, stem):
    p = ev(["finalize", "--run", sweep.run], sweep.env)
    T.check(p.returncode in (0, 3), f"finalize ran (exit {p.returncode}: {p.stderr[-200:]})")
    d = json.loads((sweep.run / "disposition.json").read_text(encoding="utf-8"))
    return ((d.get("arms") or {}).get(sweep.arm[stem]) or {}).get("orderEvaluation") or {}


def case_e1_a_stop_keeps_the_arms_that_finished_whole(tmp):
    s = Sweep(tmp, "e1")
    r = s.go("run", C2)
    T.expect(r.returncode, 0, "e1: the stopped run exits 0")
    T.check(s.status(BASE, "arm") == "completed", f"e1: the baseline that finished is credited (got {s.status(BASE, 'arm')})")
    for part in ("arm", "reverse", "diff"):
        T.check(s.status(C1, part) == "completed", f"e1: the candidate that finished whole keeps its {part} (got {s.status(C1, part)})")
        T.expect(s.status(C2, part), "stopped(deadline)", f"e1: the candidate in flight has its {part} stopped")
    inv = T.last_sweep(s.run)
    jobs_before = {json.loads(l)["job"] for l in (inv / "jobs.jsonl").read_text(encoding="utf-8").splitlines()}
    T.check(f"diff-navownprobe-{C1}" in jobs_before, "e1: premise - the first candidate's diff finished before the stop")
    r = s.go("resume")
    T.expect(r.returncode, 0, "e1: resume exits 0")
    for stem, parts in ((BASE, ("arm",)), (C1, ("arm", "reverse", "diff")), (C2, ("arm", "reverse", "diff"))):
        for part in parts:
            T.check(s.status(stem, part) in ("completed", "reused"), f"e1: after resume {stem}/{part} is credited (got {s.status(stem, part)})")
    log = (T.last_sweep(s.run) / "cache-log.txt").read_text(encoding="utf-8")
    T.check("REUSED navownprobe-baseline" in log and f"REUSED navownprobe-{C1}:" in log and f"REUSED navownprobe-{C1}-reverse" in log,
            "e1: the resume reruns no finished probe")
    # the same words as a sweep nobody stopped
    plain = Sweep(tmp, "e1-plain", budget=5)
    plain.go("run")
    got, want = classification(s, C1), classification(plain, C1)
    T.expect(got.get("classification"), want.get("classification"), "e1: the first candidate's classification matches an unstopped sweep's")
    T.check(want.get("classification") is not None, "e1: premise - the unstopped sweep gives a classification")


def case_e2_a_stop_between_forward_and_reverse_credits_none_of_it(tmp):
    """A1. The forward finished; the reverse and the diff did not. The forward emit goes to the cache, not to the ledger."""
    s = Sweep(tmp, "e2")
    r = s.go("run", C1 + ".reverse")
    T.expect(r.returncode, 0, "e2: the stopped run exits 0")
    jobs = {json.loads(l)["job"]: json.loads(l) for l in (T.last_sweep(s.run) / "jobs.jsonl").read_text(encoding="utf-8").splitlines()}
    T.check((jobs.get(f"navownprobe-{C1}") or {}).get("outcome") == "ran", "e2: premise - the forward job finished")
    T.check(s.status(BASE, "arm") == "completed", f"e2: the baseline that finished is credited (got {s.status(BASE, 'arm')})")
    for part in ("arm", "reverse", "diff"):
        T.expect(s.status(C1, part), "stopped(deadline)", f"e2: {C1}/{part} is not credited on a forward run alone")
        T.check((s.receipt(C1, part).get("validation") or {}).get("accepted") is not True, f"e2: {C1}/{part} has no accepted receipt")
    r = s.go("resume")
    log = (T.last_sweep(s.run) / "cache-log.txt").read_text(encoding="utf-8")
    T.check(f"REUSED navownprobe-{C1}:" in log, "e2: the resume reuses the forward emit from the cache")
    T.check(s.status(C1, "reverse") in ("completed", "reused"), "e2: the resume measures the reverse")


def case_e3_every_stated_budget_is_the_one_in_force(tmp):
    """A3. The plan freezes 0.3; this invocation runs at 0.4, then at 0.000001."""
    s = Sweep(tmp, "e3")
    plan_sha = sha(s.run / "plan.json")
    r = s.go("resume", C1, "--budget-minutes", "0.4")
    T.expect(r.returncode, 0, f"e3: resume takes a limit for this invocation ({r.stderr[-160:]})")
    words = json.dumps(s.receipt(C1, "arm"))
    T.check("budget of 0.4 minute" in words, "e3: the stop's sentence states the 0.4 in force")
    T.check("budget of 0.3 minute" not in words, "e3: the stop's sentence does not state the plan's 0.3")
    T.check("budget of 0.4 minute" in r.stdout and "budget of 0.3 minute" not in r.stdout, "e3: the run's own line states 0.4")
    led = json.loads((s.run / "ledger.json").read_text(encoding="utf-8"))
    T.expect((led.get("runIds") or [{}])[-1].get("budgetMinutes"), 0.4, "e3: the ledger records this invocation's limit")
    T.expect(sha(s.run / "plan.json"), plan_sha, "e3: the plan is not rewritten")
    ev(["finalize", "--run", s.run], s.env)
    d = json.loads((s.run / "disposition.json").read_text(encoding="utf-8"))
    T.expect([b.get("budgetMinutes") for b in d.get("budgetsInForce") or []], [0.4], "e3: the disposition states the limit in force")
    status = (s.run / "STATUS.md").read_text(encoding="utf-8")
    T.check("limits in force: resume 0.4" in status and "froze 0.3" in status, "e3: STATUS.md states both the frozen value and the one in force")
    r = s.go("resume", None, "--budget-minutes", "0.000001")
    # a launch the limit refused is accounted per check and arm, with no part
    entry = json.loads((s.run / "ledger.json").read_text(encoding="utf-8"))["entries"].get(f"{NAV}__{s.arm[C2]}") or {}
    T.expect(T.execution_of(entry), "skipped-budget", "e3: a limit that has already passed launches nothing")
    f = s.run / "receipts" / f"{NAV}__{s.arm[C2]}.json"
    words = json.dumps(entry) + (f.read_text(encoding="utf-8") if f.is_file() else "")
    T.check("budget of 1e-06 minute" in words, "e3: the sentence for a launch the limit refused states that limit")


def case_e3b_the_raised_limit_is_what_stops_the_sweep(tmp):
    """The plan's 0.05 minutes would stop the slow arm after 3 s; this invocation's 0.3 lets it run on to 18 s."""
    s = Sweep(tmp, "e3b", budget=0.05)
    import time
    t0 = time.perf_counter()
    r = s.go("resume", C1, "--budget-minutes", "0.3")
    took = time.perf_counter() - t0
    T.expect(r.returncode, 0, f"e3b: resume takes a limit ({r.stderr[-120:]})")
    T.check(took >= 18, f"e3b: the raised limit is what stops the sweep (took {took:.1f} s against 18 s)")


def case_e5_nothing_is_credited_before_the_baseline_finishes(tmp):
    """A1: a candidate's parts read the baseline's emit, so a stop during the baseline leaves everything stopped."""
    s = Sweep(tmp, "e5")
    r = s.go("run", "baseline")
    T.expect(r.returncode, 0, "e5: the stopped run exits 0")
    T.expect(s.status(BASE, "arm"), "stopped(deadline)", "e5: the unfinished baseline is stopped")
    for part in ("arm", "reverse", "diff"):
        T.expect(s.status(C1, part), "stopped(deadline)", f"e5: {C1}/{part} is stopped while the baseline's emit is unfinished")


def case_e6_a_finished_job_that_fails_validation_earns_no_credit(tmp):
    """A1: finished is not enough. The baseline's emit is written and rejected, and the first candidate's jobs all finish."""
    s = Sweep(tmp, "e6")
    f = s.case / "scenario.json"
    doc = json.loads(f.read_text(encoding="utf-8"))
    doc["identityDamage"] = list(doc.get("identityDamage") or []) + [{"emitStem": "baseline", "kind": "wrong-order"}]
    f.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    r = s.go("run", C2)
    T.expect(r.returncode, 0, "e6: the stopped run exits 0")
    T.check(str(s.status(BASE, "arm")).startswith("rejected("), f"e6: the baseline is rejected on its own cause (got {s.status(BASE, 'arm')})")
    for part in ("arm", "reverse", "diff"):
        T.check((s.receipt(C1, part).get("validation") or {}).get("accepted") is not True and s.status(C1, part) not in ("completed", "reused"),
                f"e6: {C1}/{part} earns no credit over a rejected baseline (got {s.status(C1, part)})")


def case_e7_a_sweep_nobody_stopped_writes_the_same_summary(tmp):
    """Against KEEP_REFERENCE_HARNESS when set: the writes along the way leave the finished summary.json as it was."""
    ref = os.environ.get("KEEP_REFERENCE_HARNESS")
    if not ref:
        print("   (no reference harness named: nothing to compare)")
        return
    docs = []
    mine = T.EVALRUN
    for tag, evalrun_py in (("ref", Path(ref) / "evalrun.py"), ("new", mine)):
        T.EVALRUN = evalrun_py
        d = tmp / "same" / tag
        d.mkdir(parents=True)
        s = Sweep(d, "e7", budget=5)
        T.expect(s.go("run").returncode, 0, f"e7: the {tag} run exits 0")
        text = (T.last_sweep(s.run) / "summary.json").read_text(encoding="utf-8")
        for spelling in (str(d), str(d).replace(chr(92), chr(92) * 2), str(d).replace(chr(92), "/")):
            text = text.replace(spelling, "ROOT")
        docs.append(json.loads(text))
    T.EVALRUN = mine
    T.check(docs[0] == docs[1], f"e7: the finished summary is unchanged ({json.dumps(docs[0])[:300]} against {json.dumps(docs[1])[:300]})")


def case_e4_an_import_survives_a_raised_budget(tmp):
    """The path round 2's note left untested: imported evidence and a resume under another limit."""
    d = T.GEN / "cases" / "case-19-derived-prerequisites"
    plugins = json.loads((d / "candidates" / sorted(x.name for x in (d / "candidates").glob("candidate-*baseline*"))[0]).read_text(encoding="utf-8"))["Plugins"]
    T.import_case_dir("keep-import", {"candidate-00-baseline": plugins})
    run = tmp / "e4"
    plan = T.import_plan(run, "keep-import", "e4")
    r = T.evalrun(["run", "--run", run], T.GEN / "cases" / "keep-import" / "scenario.json")
    T.expect(r.returncode, 0, "e4: run exit")
    arm = plan["arms"][0]
    manifest, _ = T.origin_import(tmp / "e4-import", plan, arm)
    r = T.evalrun(["import-results", "--run", run, "--file", manifest], T.GEN / "cases" / "keep-import" / "scenario.json")
    T.expect(r.returncode, 0, f"e4: import accepted ({r.stderr[-200:]})")
    r = T.evalrun(["resume", "--run", run, "--budget-minutes", "5"], T.GEN / "cases" / "keep-import" / "scenario.json")
    T.expect(r.returncode, 0, f"e4: resume under a raised limit exit ({r.stderr[-200:]})")
    led = json.loads((run / "ledger.json").read_text(encoding="utf-8"))["entries"]
    e = led.get(f"{T.IMPORT_CHECK}__{arm['armId']}") or {}
    T.check(T.execution_of(e) in ("completed", "reused"), f"e4: the imported row stands (got {T.execution_of(e)})")
    T.expect(led and e.get("importOrigin"), "fresh", "e4: its origin stays fresh")


CASES = {k: v for k, v in list(globals().items()) if k.startswith("case_")}


def main():
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="hc-keep-"))
    T.pin_standard_settings(tmp)
    T.GEN = tmp / "fixtures"
    gen = subprocess.run([sys.executable, str(T.FIXT / "make_fixtures.py"), "--out", str(T.GEN)], capture_output=True, text=True)
    if gen.returncode != 0:
        print("make_fixtures did not run:", gen.stderr[-400:])
        return 2
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
    os.environ["PYTHONIOENCODING"] = "utf-8"
    T.use_conflictdiff(tmp)
    try:
        for name, fn in CASES.items():
            if want and not name.startswith("case_" + want):
                continue
            print(f"\n=== {name} ===", flush=True)
            before = len(T.FAILURES)
            d = tmp / name
            d.mkdir()
            try:
                fn(d)
            except Exception as ex:  # a crash is reported as such: a sabotage arm red by crash is not red
                T.check(False, f"{name} CRASHED {type(ex).__name__}: {ex}")
            print(f"   -> {'RED' if len(T.FAILURES) > before else 'green'}", flush=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(T.FAILURES)} failed checks")
    return 1 if T.FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
