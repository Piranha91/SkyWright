"""Known answers for reading a capture's arms side by side: what arms share, how many run, and what a failure leaves.

    python test_records_parallel.py [PREFIX]     KEEP_HARNESS names the _geometry-harness copy under test.

Real evalrecords capture processes, a real Runner and real evalpipeline.stage_records with a stand-in reader; memory
readings are injected through a file. The slot cases use the real idle_lock on a private runs root, from KEEP_IDLE_LOCK or
the repository's _claude/tools, and say so when it isn't there. No game, no lab, no houseCARL server.
"""
import ctypes
import contextlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
HARNESS = Path(os.environ.get("KEEP_HARNESS") or HERE.parent).resolve()
sys.path[:0] = [str(HERE), str(HARNESS), str(HARNESS.parent / "scripts")]
import test_records_keep_work as K  # noqa: E402

T = K.T
FAKE = K.FAKE
ER = str(HARNESS / "evalrecords.py")
PLENTY = {"availMB": 60000, "commitMB": 80000, "totalMB": 64000}
ARMS = ["baseline", "candidate-01", "candidate-02", "candidate-03"]
HAS_WORKERS = "--workers" in subprocess.run([sys.executable, ER, "capture", "--help"], capture_output=True, text=True).stdout
print(f"capture takes --workers: {HAS_WORKERS}", flush=True)


def alive(pid: int) -> bool:
    if os.name != "nt":
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False
    k = ctypes.WinDLL("kernel32")
    k.OpenProcess.restype = ctypes.c_void_p
    h = k.OpenProcess(0x1000, False, pid)
    if not h:
        return False
    code = ctypes.c_ulong()
    k.GetExitCodeProcess(ctypes.c_void_p(h), ctypes.byref(code))
    k.CloseHandle(ctypes.c_void_p(h))
    return code.value == 259


class Lab(K.Lab):
    def __init__(self, tmp: Path, arms=None):
        self.arm_ids = list(arms or ARMS)
        super().__init__(tmp)
        self.events = tmp / "events"
        self.events.mkdir()
        self.mem = tmp / "memory.json"
        self.set_memory(**PLENTY)
        self.runlog = tmp / "runlog"

    def write_plan(self, order=None):
        req = {"plugins": [{"name": p, "path": str(self.data / p), "sha256": K.sha(self.data / p)} for p in self.plugins],
               "providers": [{"name": "M1", "kind": "mod"}], "overlays": [], "population": "load-time"}
        arms = [{"armId": a, "kind": "baseline" if a == "baseline" else "candidate", "plugins": list(self.plugins), "mods": ["M1"], "candidateFile": f"{a}.json",
                 "candidateSha256": "c" * 64, "requested": req} for a in self.arm_ids]
        plan = {"planId": "p" * 12, "arms": arms,
                "checks": [{"checkId": c, "adapter": {"kind": "mcp-only"}, "arms": [{"armId": a, "status": "mcp-only"} for a in self.arm_ids]} for c in K.CHECKS]}
        (self.run_dir / "plan.json").write_text(json.dumps(plan), encoding="utf-8")

    def set_memory(self, **d):
        tmp = self.mem.with_suffix(".tmp")
        tmp.write_text(json.dumps(d), encoding="utf-8")
        os.replace(tmp, self.mem)

    def env(self, script=None, **more):
        e = dict(os.environ, FAKE_CAPTURE_DIR=str(self.events), FAKE_CAPTURE_SCRIPT=json.dumps(script or {}), EVALRECORDS_MEMORY_FILE=str(self.mem),
                 HOUSECARL_RUNLOG_DIR=str(self.runlog), PYTHONIOENCODING="utf-8")
        e.update(more)
        return e

    def argv(self, workers=None, deadline=None, reader_data=True):
        self.n += 1
        self.out = getattr(self, "fixed_out", None) or self.t / f"out-{self.n}"
        args = [sys.executable, ER, "capture", "--run", str(self.run_dir), "--baseline-from", str(self.t / "prep.json"),
                "--reader-cmd", f'"{sys.executable}" "{FAKE}"', "--data", str(self.data), "--mods", str(self.mods),
                "--profile-dir", str(self.profile), "--out", str(self.out), "--checkpoints", str(self.t / "ckpt")]
        if reader_data:
            args += ["--reader-data", str(self.t / "reader-data")]
        if deadline:
            args += ["--arm-deadline-min", str(deadline)]
        # the capture of today has no such option: it then runs its one way, and each case fails on its own assertion
        if workers is not None and HAS_WORKERS:
            args += ["--workers", str(workers)]
        return args

    def capture(self, workers=None, script=None, deadline=None, **more):
        p = subprocess.run(self.argv(workers, deadline), capture_output=True, text=True, encoding="utf-8", errors="replace", env=self.env(script, **more))
        self.last = p
        return p

    def popen(self, workers=None, script=None, **more):
        return subprocess.Popen(self.argv(workers), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=self.env(script, **more))

    def event_rows(self):
        rows = []
        for f in self.events.glob("*.jsonl"):
            rows += [json.loads(x) for x in f.read_text(encoding="utf-8").splitlines() if x.strip()]
        return sorted(rows, key=lambda r: r["t"])

    def starts(self, arm=None):
        return [r for r in self.event_rows() if r["event"] == "start" and (arm is None or r["arm"] == arm)]

    def wait_starts(self, n, seconds=30, keep_alive=True) -> bool:
        end = time.time() + seconds
        while time.time() < end:
            if len(self.starts()) >= n:
                return True
            time.sleep(0.1)
        return False

    def most_at_once(self):
        most = now = 0
        for r in self.event_rows():
            now += {"start": 1, "end": -1}.get(r["event"], 0)
            most = max(most, now)
        return most

    def summary(self):
        f = self.out / "capture-summary.json"
        return json.loads(f.read_text(encoding="utf-8")) if f.is_file() else {}

    def record(self):
        f = self.out / "capture-workers.json"
        return json.loads(f.read_text(encoding="utf-8")) if f.is_file() else {}

    def attempts(self, arm):
        return [a.get("outcome") for a in ((self.record().get("arms") or {}).get(arm) or {}).get("attempts", [])]

    def checkpoint(self, arm):
        return (self.t / "ckpt" / arm / "checkpoint.json").is_file()

    def session_files(self, arm):
        return list((self.t / "ckpt" / arm).glob(f"*/sessions/{arm}/*"))


def stage_ctx(lab: Lab, machine):
    """The real stage over the lab: a run folder, a ledger with every record row open, and the lab as instance and game."""
    out = lab.t / "stage"
    (out / "evaluation").mkdir(parents=True)
    (out / "prepare").mkdir()
    shutil.copy2(lab.run_dir / "plan.json", out / "evaluation" / "plan.json")
    shutil.copy2(lab.t / "prep.json", out / "prepare" / "preparation.json")
    entries = {f"{c}__{a}": {"status": "mcp-only"} for c in K.CHECKS for a in lab.arm_ids}
    (out / "evaluation" / "ledger.json").write_text(json.dumps({"entries": entries}), encoding="utf-8")
    prof = lab.t / "inst" / "profiles" / "p"
    prof.mkdir(parents=True)
    (prof / "skyrim.ini").write_text("[General]\n", encoding="utf-8")
    (out / "records").mkdir()
    return types.SimpleNamespace(out=out, game=lab.t, instance=lab.t / "inst", profile="p", id="m", machine={"readerCmd": f'"{sys.executable}" "{FAKE}"', **machine})


def run_stage(lab, machine, script=None, arm_minutes=None):
    """stage_records in this process, the stand-in's switches in the environment. Returns (printed text, refusal or None)."""
    import evalpipeline as EP
    saved_env, saved_min = dict(os.environ), EP.RECORDS_ARM_MINUTES
    os.environ.update(lab.env(script))
    if arm_minutes:
        EP.RECORDS_ARM_MINUTES = arm_minutes
    ctx = stage_ctx(lab, machine)
    buf = io.StringIO()
    refusal = None
    try:
        with contextlib.redirect_stdout(buf):
            EP.stage_records(ctx, {}, ctx.out / "records")
    except EP.Refusal as ex:
        refusal = str(ex)
    finally:
        os.environ.clear()
        os.environ.update(saved_env)
        EP.RECORDS_ARM_MINUTES = saved_min
    return buf.getvalue(), refusal, ctx


def idle_lock():
    named = os.environ.get("KEEP_IDLE_LOCK")
    f = Path(named) if named else HARNESS.parent / "_claude" / "tools" / "idle_lock.py"
    return f if f.is_file() else None


# ---------------------------------------------------------------- one worker

def case_01_one_worker_reads_in_this_process(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:2])
    p = lab.popen(workers=1)
    pid = p.pid
    p.wait(timeout=120)
    T.check(p.returncode == 0, f"01: the capture finished, exit {p.returncode}")
    T.expect(sorted({r["ppid"] for r in lab.starts()}), [pid], "01: every reader is a child of the capture itself, so no worker process stands between")
    T.check(not (lab.out / "workers").exists(), "01: one worker leaves no workers folder")
    for arm in ARMS[:2]:
        sent = [r["method"] for r in lab.event_rows() if r["event"] == "request" and r["arm"] == arm]
        kept = [json.loads(x)["method"] for f in (lab.t / "ckpt" / arm).glob(f"*/sessions/{arm}/request.jsonl") for x in f.read_text(encoding="utf-8").splitlines()]
        T.expect(kept, sent, f"01: {arm}'s transcript holds what its reader was sent, in order")


def case_18_the_stage_asks_for_one_worker_unless_the_machine_file_says(tmp):
    import evalpipeline as EP
    seen = []
    lab = Lab(tmp / "a", arms=ARMS[:2])

    def ask(machine):
        saved = EP.child, EP.reader_cmd
        ctx = stage_ctx(lab, machine)
        try:
            EP.child = lambda c, d, op, argv, **k: seen.append((op, argv, k)) or {"exitCode": 0}
            EP.reader_cmd = lambda c: "reader"
            try:
                EP.stage_records(ctx, {}, ctx.out / "records")
            except EP.Refusal:
                pass
        finally:
            EP.child, EP.reader_cmd = saved
            shutil.rmtree(ctx.out)
            shutil.rmtree(lab.t / "inst" / "profiles")
        op, argv, k = next(x for x in seen if x[0] == "records-capture")
        seen.clear()
        T.check(k.get("heavy") is True, "18: the capture takes the machine slot, once, whatever the workers")
        return argv[argv.index("--workers") + 1] if "--workers" in argv else None
    T.expect(ask({}), "1", "18: with no recordWorkers in the machine file the stage asks for one worker")
    T.expect(ask({"recordWorkers": "auto"}), "auto", "18: the machine file's recordWorkers reaches the capture")


# ---------------------------------------------------------------- what arms must not share

def case_02_each_reader_has_its_own_view_and_data_folder(tmp):
    lab = Lab(tmp / "a")
    lab.capture(workers=3)
    T.check(lab.last.returncode == 0, f"02: the capture finished, exit {lab.last.returncode}")
    rows = lab.starts()
    T.expect(sorted(Path(r["profile"]).name for r in rows), sorted(ARMS), "02: each reader's view is its own arm's")
    T.expect(sorted(Path(r["dataDir"] or "").name for r in rows), sorted(ARMS), "02: each reader has its own data folder")
    T.check(len({r["profile"] for r in rows}) == 4 and len({r["dataDir"] for r in rows}) == 4, "02: no two readers share a folder")
    T.expect(sorted({str(Path(r["profile"]).parent.parent) for r in rows}), [str(lab.out)], "02: the views stay where one worker puts them")


def case_03_each_arm_holds_its_own_evidence(tmp):
    lab = Lab(tmp / "a")
    lab.capture(workers=3)
    s = lab.summary()
    for arm in ARMS:
        req = list((lab.t / "ckpt" / arm).glob(f"*/sessions/{arm}/request.jsonl"))
        T.check(len(req) == 1, f"03: {arm} has one request transcript")
        if req:
            first = json.loads(req[0].read_text(encoding="utf-8").splitlines()[0])
            T.expect(first["params"]["clientInfo"]["name"], f"evalrun:{'p' * 12}:{arm}", f"03: {arm}'s transcript carries its own token")
        for doc in (s.get(arm) or {}).get("imports", []):
            d = json.loads(Path(doc).read_text(encoding="utf-8"))
            T.expect(d["armId"], arm, f"03: {arm}'s import names it")
            T.check(all(arm in o["path"] for o in d["outputs"] if o["role"] != "rows"), f"03: {arm}'s import rests on its own transcript")
    T.check(len(lab.starts()) == 4, f"03: one reader per arm, got {len(lab.starts())}")


def case_04_the_summary_keeps_plan_order(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    # the first arm in the plan is slowest, and the capture is asked to read them together
    lab.capture(workers=3, script={"baseline": {"delay": 4}, "candidate-01": {"delay": 2}})
    ends = [r["arm"] for r in lab.event_rows() if r["event"] == "end"]
    T.expect(ends and ends[-1], "baseline", "04: the slowest arm, first in the plan, ended last")
    T.expect(list(lab.summary()), ARMS[:3], "04: the summary is in plan order whatever order arms finished")
    T.check(all("imports" in v for v in lab.summary().values()), "04: every arm has its imports")


def case_05_arms_really_overlap(tmp):
    lab = Lab(tmp / "a")
    lab.capture(workers=3, script={a: {"delay": 2} for a in ARMS})
    T.expect(lab.most_at_once(), 3, "05: three readers ran at once, no more")
    T.expect(lab.record().get("workers"), 3, "05: the record says three")
    T.check(all(a.get("reader", {}).get("pid") or a.get("reader") == {} for arm in ARMS for a in lab.record().get("arms", {}).get(arm, {}).get("attempts", [])), "05: attempts name their reader")
    rec = lab.record().get("arms", {})
    T.expect(sorted(rec), sorted(ARMS), "05: every arm is in the record")
    for arm in ARMS:
        pids = [a["reader"].get("pid") for a in rec.get(arm, {}).get("attempts", []) if a.get("reader")]
        T.expect(pids, [r["pid"] for r in lab.starts(arm)], f"05: {arm}'s record names the reader that answered it")


# ---------------------------------------------------------------- failure

def case_06_a_reader_that_dies_beside_others_reads_again_alone(tmp):
    lab = Lab(tmp / "a")
    p = lab.capture(workers=3, script={"candidate-01": {"exitOnce": True, "delay": 1}, "baseline": {"delay": 3}})
    T.check(p.returncode == 0, f"06: the capture ends clean once the arm reads alone, exit {p.returncode}: {p.stdout[-300:]}")
    T.expect(lab.attempts("candidate-01"), ["refused-beside-others", "finished"], "06: one failed attempt beside others, then one alone")
    T.expect(lab.attempts("baseline"), ["finished"], "06: the arm beside it is not disturbed")
    T.check(all(lab.checkpoint(a) for a in ARMS), "06: every arm ends with a checkpoint")
    T.check("imports" in lab.summary().get("candidate-01", {}), "06: only the second attempt's answer stands")


def case_07_a_refusal_cured_alone_stays_visible(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:2])
    text, refusal, ctx = run_stage(lab, {"recordWorkers": 2}, script={"candidate-01": {"refuseBeside": 4}, "baseline": {"delay": 3}})
    rec = json.loads((ctx.out / "records" / "records.json").read_text(encoding="utf-8"))
    T.check("refused" not in rec["capture"].get("candidate-01", {"refused": "absent"}), "07: the arm's final answer is the one read alone")
    T.check("candidate-01" in ((rec.get("workers") or {}).get("curedAlone") or {}), "07: records.json names the arm a retry cured")
    T.check("candidate-01" in text and "alone" in text, f"07: the stage says so to its caller, got {text[-300:]!r}")
    cap = json.loads(next((ctx.out / "records").glob("capture-*/capture-workers.json")).read_text(encoding="utf-8")) if list((ctx.out / "records").glob("capture-*/capture-workers.json")) else {}
    T.check("candidate-01" in (cap.get("curedAlone") or {}), "07: capture-workers.json names it")


def case_06b_a_capture_process_that_dies_is_named_and_read_again(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    p = lab.capture(workers=3, script={"candidate-01": {"killParentOnce": True}, "baseline": {"delay": 3}})
    T.check(p.returncode == 0, f"06b: the capture ends clean, exit {p.returncode}: {p.stdout[-300:]}")
    T.expect(lab.attempts("candidate-01"), ["refused-beside-others", "finished"], "06b: the dead process counts as one failed attempt")
    rec = (lab.record().get("arms") or {}).get("candidate-01", {}).get("attempts", [{}])
    T.check("no result" in str(rec[0].get("reason")), f"06b: its reason says it left no result, got {rec[0].get('reason')}")


def case_08_an_arm_refused_alone_too_is_refused(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    p = lab.capture(workers=3, script={"candidate-01": {"refuse": True}, "baseline": {"delay": 2}})
    T.expect(p.returncode, 2, "08: a refused arm makes the capture's exit 2")
    v = lab.summary().get("candidate-01", {})
    T.check("refused" in v and "imports" not in v, f"08: the summary says refused and no imports, got {v}")
    T.check(not lab.checkpoint("candidate-01"), "08: no checkpoint for an arm that did not finish")
    T.check(lab.checkpoint("baseline") and lab.checkpoint("candidate-02"), "08: the others finish and are kept")
    T.expect(lab.attempts("candidate-01"), ["refused-beside-others", "refused"], "08: tried beside others, then alone, and no more")


def case_09_a_deadline_is_named_and_not_retried(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    text, refusal, ctx = run_stage(lab, {"recordWorkers": 3}, script={"candidate-01": {"hang": True}}, arm_minutes=0.1)
    T.check(refusal is not None and "candidate-01" in refusal and "deadline" in refusal, f"09: the stage names the arm and the deadline, got {refusal}")
    T.check(refusal is not None and "baseline" not in refusal and "candidate-02" not in refusal, "09: only that arm is named")
    T.expect(len(lab.starts("candidate-01")), 1, "09: a deadline is not retried")
    root = ctx.out / "records" / "checkpoints"
    T.check((root / "baseline" / "checkpoint.json").is_file() and (root / "candidate-02" / "checkpoint.json").is_file(), "09: finished arms are kept")
    T.check(not (root / "candidate-01" / "checkpoint.json").is_file(), "09: the stopped arm has no checkpoint")
    if lab.most_at_once() < 2:
        T.check(False, "09: arms did not overlap")


def case_10_a_reader_gone_between_calls_is_still_the_deadline(tmp):
    import evalrecords as er
    s = er.Session(f'"{sys.executable}" "{FAKE}"', {}, "evalrun:p:baseline", deadline_s=60)
    s.timer.cancel()
    s.timed_out = True
    s.p.kill()
    s.p.wait()
    got = None
    try:
        s.call("tools/call", {"name": "x", "arguments": {}})
    except BaseException as ex:
        got = ex
    T.check(isinstance(got, er.Refusal) and "deadline" in str(got), f"10: a write to a stopped reader is the deadline's refusal, got {type(got).__name__}: {got}")


def case_11_a_stop_from_outside_ends_the_whole_tree_and_keeps_finished_work(tmp):
    from runlog import Runner
    lab = Lab(tmp / "a")
    argv = lab.argv(workers=3)
    env = lab.env({"candidate-03": {"hang": True}})
    end = time.perf_counter() + 12
    job = {"id": "outer", "argv": argv, "env": {k: env[k] for k in ("FAKE_CAPTURE_DIR", "FAKE_CAPTURE_SCRIPT", "EVALRECORDS_MEMORY_FILE", "HOUSECARL_RUNLOG_DIR")}}
    res = Runner("outer-stop", deadline=end).run([job], echo=False)[0]
    T.expect(res.get("stopped"), "deadline", "11: the outer stop reached its deadline with the hung arm still reading")
    T.check(res.get("treeStopped") is True, "11: the whole tree is confirmed gone")
    pids = [r["pid"] for r in lab.starts()]
    T.check(pids and not [p for p in pids if alive(p)], "11: no reader is left")
    T.check(not (lab.out / "capture-summary.json").is_file(), "11: a stopped capture writes no summary")
    done = [a for a in ARMS if lab.checkpoint(a)]
    T.expect(sorted(done), sorted(ARMS[:3]), "11: finished arms are kept, the hung one not")
    before = len(lab.starts())
    p = lab.capture(workers=3)
    T.check(p.returncode == 0, f"11: the rerun finished, exit {p.returncode}")
    T.expect(sorted(r["arm"] for r in lab.starts()[before:]), ["candidate-03"], "11: the rerun reads only what did not finish")


def case_11b_killing_the_capture_alone_ends_its_arms(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    p = lab.popen(workers=3, script={a: {"delay": 60} for a in ARMS})
    started = lab.wait_starts(3, 20)
    T.check(started, f"11b: three readers started, got {len(lab.starts())}")
    p.kill()
    p.wait()
    time.sleep(6)
    left = [r["pid"] for r in lab.starts() if alive(r["pid"])]
    T.expect(left, [], "11b: killing the capture leaves no reader running")


def case_12_finished_arms_are_reused_at_any_worker_count(tmp):
    lab = Lab(tmp / "a")
    lab.capture(workers=1)
    n = len(lab.starts())
    T.expect(n, 4, "12: the first capture reads every arm")
    p = lab.capture(workers=3)
    T.expect(len(lab.starts()), n, "12: one worker's arms are reused at three")
    T.check(all(v.get("reused") for v in lab.summary().values()), "12: the summary says reused")
    lab2 = Lab(tmp / "b")
    lab2.capture(workers=3)
    n = len(lab2.starts())
    lab2.capture(workers=1)
    T.expect(len(lab2.starts()), n, "12: three workers' arms are reused at one")
    T.check(p.returncode == 0, "12: the reuse is a clean exit")


# ---------------------------------------------------------------- memory

def case_13a_memory_free(tmp):
    lab = Lab(tmp / "a")
    lab.capture(workers="auto", script={a: {"delay": 2} for a in ARMS})
    r = lab.record()
    T.expect(r.get("case"), "free", "13a: the record says memory was free")
    T.check(r.get("workers", 0) > 1, f"13a: more than one worker, got {r.get('workers')}")
    T.check(lab.most_at_once() > 1, "13a: more than one reader really ran at once")
    T.check(not r.get("warning"), "13a: no warning")
    T.check(bool(r.get("estimates")), "13a: the estimates are recorded")


def case_13b_memory_held_by_others_warns(tmp):
    lab = Lab(tmp / "a")
    lab.set_memory(availMB=13000, commitMB=80000, totalMB=64000)
    p = lab.capture(workers="auto", script={a: {"delay": 2} for a in ARMS})
    r = lab.record()
    T.expect(r.get("case"), "held-by-others", "13b: the record says others hold it")
    T.expect(r.get("workers"), 2, "13b: what fits now runs")
    w = str(r.get("warning"))
    T.check("13000 MB" in w and "Close" in w and "recordWorkers" in w, f"13b: the warning says how much, what to do and how to refuse, got {w}")
    T.check("WARNING" in p.stdout and "Close" in p.stdout, "13b: the warning is printed too")
    T.expect(lab.most_at_once(), 2, "13b: two at once, as chosen")


def case_13c_memory_absent_is_one_worker_in_process(tmp):
    lab = Lab(tmp / "a")
    lab.set_memory(availMB=7000, commitMB=9000, totalMB=8000)
    p = lab.capture(workers="auto")
    r = lab.record()
    T.check(p.returncode == 0, f"13c: it still reads, exit {p.returncode}")
    T.expect(r.get("case"), "absent", "13c: the record says the machine hasn't the memory")
    T.expect(r.get("workers"), 1, "13c: one worker")
    T.check(not r.get("warning"), "13c: nothing to warn about: closing programs wouldn't help")
    T.check(not (lab.out / "workers").exists(), "13c: today's route, in process")


def case_13d_commit_counts_as_well_as_physical(tmp):
    lab = Lab(tmp / "a")
    lab.set_memory(availMB=60000, commitMB=9000, totalMB=64000)
    lab.capture(workers="auto")
    T.expect(lab.record().get("workers"), 1, "13d: too little commit means one worker, however much physical is free")


def case_26_the_warning_names_the_limit_that_applied(tmp):
    lab = Lab(tmp / "commit")
    lab.set_memory(availMB=60000, commitMB=14000, totalMB=64000)
    lab.capture(workers="auto")
    r = lab.record()
    T.expect(r.get("limit"), "commit", "26: commit was the limit, and the record says so")
    w = str(r.get("warning"))
    T.check("14000 MB of commit" in w, f"26: the warning names commit, got {w}")
    T.check("60000" not in w, f"26: and doesn't call the plentiful physical memory the reason, got {w}")
    lab = Lab(tmp / "physical")
    lab.set_memory(availMB=13000, commitMB=80000, totalMB=64000)
    lab.capture(workers="auto")
    r = lab.record()
    T.expect(r.get("limit"), "physical", "26: physical was the limit, and the record says so")
    w = str(r.get("warning"))
    T.check("13000 MB of physical memory" in w and "80000" not in w, f"26: the warning names physical, got {w}")


def case_27_the_warning_leaves_out_what_nobody_can_close(tmp):
    import evalrecords_workers as W
    listing = '"Memory Compression","3","Services","0","5,000,000 K"\n"System","4","Services","0","4,000,000 K"\n"svchost.exe","9","Services","0","3,000,000 K"\n' \
              '"MyEditor.exe","10","Console","1","2,048,000 K"\n"MyEditor.exe","11","Console","1","1,024,000 K"\n"Browser.exe","12","Console","1","1,024 K"\n'
    T.expect(W.programs_in(listing), [("MyEditor.exe", 3000), ("Browser.exe", 1)], "27: only programs a user could close, largest first, summed by name")
    lab = Lab(tmp / "a")
    lab.set_memory(availMB=13000, commitMB=80000, totalMB=64000)
    lab.capture(workers="auto")
    T.check("Memory Compression" not in str(lab.record().get("warning")), f"27: a real warning doesn't name it, got {lab.record().get('warning')}")


def case_14_with_nothing_running_an_arm_always_starts(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:2])
    lab.set_memory(availMB=100, commitMB=100, totalMB=64000)
    p = lab.capture(workers="auto")
    T.check(p.returncode == 0, f"14: no bare failure for lack of memory, exit {p.returncode}: {p.stderr[-200:]}")
    T.check(all("imports" in v for v in lab.summary().values()) and lab.summary(), "14: every arm read")
    T.expect(lab.record().get("case"), "held-by-others", "14: and the record says why it went one at a time")


def _when_started(lab, n, fn):
    def body():
        if lab.wait_starts(n, 40):
            fn()
    t = threading.Thread(target=body, daemon=True)
    t.start()
    return t


def case_15_low_memory_stops_the_newest_and_it_reads_again_alone(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])

    def low_then_back():
        lab.set_memory(availMB=1000, commitMB=80000, totalMB=64000)
        time.sleep(3)
        lab.set_memory(**PLENTY)
    _when_started(lab, 3, low_then_back)
    p = lab.capture(workers=3, script={a: {"delay": 8} for a in ARMS})
    T.check(p.returncode == 0, f"15: the capture ends clean, exit {p.returncode}: {p.stdout[-300:]}")
    firsts = {arm: (lab.record().get("arms") or {}).get(arm, {}).get("attempts", [{}])[0].get("launched", 0) for arm in ARMS[:3]}
    order = sorted(firsts, key=firsts.get)
    newest = order[2]
    T.expect(lab.attempts(newest), ["stopped-for-memory", "finished"], "15: the newest arm is stopped, then reads alone")
    for arm in order[:2]:
        T.expect(lab.attempts(arm), ["finished"], f"15: {arm}, older, keeps reading")
    # what the second attempt leaves holds only that attempt
    req = [f for f in lab.session_files(newest) if f.name == "request.jsonl"]
    T.check(len(req) == 1, "15: one request transcript")
    if req:
        n = sum(1 for x in req[0].read_text(encoding="utf-8").splitlines() if json.loads(x).get("method") == "initialize")
        T.expect(n, 1, "15: the transcript holds one session, the second attempt's")
    T.check(not [f for f in lab.session_files(newest) if f.name.endswith(".partial")], "15: no partial file is left beside it")
    T.expect(len(lab.starts(newest)), 2, "15: it had two readers, one per attempt")
    doc = json.loads(Path(lab.summary()[newest]["imports"][0]).read_text(encoding="utf-8"))
    resp = next(o for o in doc["outputs"] if o["role"] == "response")
    T.expect(resp["sha256"], K.sha(Path(resp["path"])), "15: the import's transcript is the file that's there")


def case_15b_memory_that_stays_low_ends(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    _when_started(lab, 3, lambda: lab.set_memory(availMB=1000, commitMB=80000, totalMB=64000))
    p = subprocess.Popen(lab.argv(3), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", env=lab.env({a: {"delay": 8} for a in ARMS}))
    try:
        out, _ = p.communicate(timeout=120)
    except subprocess.TimeoutExpired:
        p.kill()
        out, _ = p.communicate()
        T.check(False, "15b: repeated stops did not end within two minutes")
    T.check(p.returncode == 0, f"15b: the capture ends clean, exit {p.returncode}: {out[-300:]}")
    outcomes = {a: lab.attempts(a) for a in ARMS[:3]}
    T.check(all(o and o[-1] == "finished" and len(o) <= 2 for o in outcomes.values()), f"15b: every arm finishes, in at most two attempts, got {outcomes}")
    T.check(sum(1 for o in outcomes.values() if o == ["finished"]) >= 1, f"15b: the last one running is never stopped, got {outcomes}")
    T.check(sum(1 for o in outcomes.values() if o and o[0] == "stopped-for-memory") >= 1, f"15b: and low memory did stop arms, got {outcomes}")


# ---------------------------------------------------------------- transcripts as they arrive

def case_16_transcripts_as_they_arrive_are_the_same_bytes(tmp):
    import evalrecords as er
    cmd = f'"{sys.executable}" "{FAKE}"'

    def drive(**k):
        s = er.Session(cmd, {}, "evalrun:p:baseline", **k)
        s.tool("housecarl_check", {"findings": ["dangling"]})
        s.close()
        return s
    (tmp / "lists").mkdir()
    drive().write(tmp / "lists")
    try:
        s = drive(stream_to=tmp / "stream")
        s.write(tmp / "stream")
    except TypeError:
        T.check(False, "16: a session can write its transcript as messages arrive")
        return
    for f in ("request.jsonl", "response.jsonl"):
        a, b = (tmp / "lists" / f).read_bytes(), (tmp / "stream" / f).read_bytes()
        T.check(a == b, f"16: {f} is byte for byte what the lists write ({len(a)} and {len(b)} bytes)")
        T.check(not (tmp / "stream" / (f + ".partial")).exists(), f"16: {f} has its real name once closed")


def case_16b_a_killed_arm_leaves_no_finished_transcript(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:1])
    p = lab.popen(workers=1, script={"baseline": {"delay": 30}})
    T.check(lab.wait_starts(1, 20), "16b: the reader started")
    time.sleep(1)
    names = {f.name for f in lab.session_files("baseline")}
    T.check("response.jsonl.partial" in names, f"16b: the response is on disk as it arrives, under its working name, got {names}")
    T.check("response.jsonl" not in names, "16b: no finished name while reading")
    p.kill()
    p.wait()
    names = {f.name for f in lab.session_files("baseline")}
    T.check("response.jsonl" not in names and "request.jsonl" not in names, f"16b: a hard kill leaves no file an import could name, got {names}")
    time.sleep(1)
    for r in lab.starts():
        if alive(r["pid"]):
            subprocess.run(["taskkill", "/F", "/PID", str(r["pid"])], capture_output=True)


def case_16c_a_refused_arm_keeps_its_partial_transcript(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:1])
    p = lab.capture(workers=1, script={"baseline": {"refuse": True}})
    T.expect(p.returncode, 2, "16c: refused")
    names = {f.name for f in lab.session_files("baseline")}
    T.check({"request.jsonl", "response.jsonl"} <= names, f"16c: the refused arm keeps what it read, as today, got {names}")


# ---------------------------------------------------------------- the slot

def case_17_workers_stay_under_one_slot(tmp):
    tool = idle_lock()
    if tool is None:
        print("   17: idle_lock.py is not in this checkout: not run", flush=True)
        return
    lab = Lab(tmp / "a")
    root = tmp / "runs"
    root.mkdir()
    lock = root / "x" / "HEAVY-LOCK-capture"
    lock.parent.mkdir()
    hold = [sys.executable, str(tool), "hold", "--lock", str(lock), "--purpose", "records", "--max-min", "5", "--wait-timeout-min", "1", "--runs-root", str(root), "--no-wait", "--"]
    p = subprocess.Popen(hold + lab.argv(workers=3), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=lab.env({a: {"delay": 5} for a in ARMS}))
    T.check(lab.wait_starts(3, 30), f"17: three readers ran under the hold, got {len(lab.starts())}")
    locks = [f.name for f in root.rglob("HEAVY-LOCK*")]
    T.expect(locks, ["HEAVY-LOCK-capture"], "17: one lock on the machine while workers read")
    T.check((root / "HEAVY-SLOT").is_file(), "17: one slot, held")
    st = subprocess.run([sys.executable, str(tool), "status", "--runs-root", str(root), "--text"], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    T.check("records" in st and "holder: pid" in st, f"17: the queue names one holder and its purpose, got {st[-200:]}")
    second = subprocess.run([sys.executable, str(tool), "hold", "--lock", str(root / "x" / "HEAVY-LOCK-second"), "--purpose", "second", "--max-min", "1", "--wait-timeout-min", "1", "--runs-root", str(root),
                             "--no-wait", "--slot-wait-min", "0.03", "--", sys.executable, "-c", "pass"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    T.expect(second.returncode, 3, "17: a second heavy job waits for the slot, and gives up while the capture reads")
    p.wait(timeout=120)
    T.expect(p.returncode, 0, "17: the capture finished")
    T.check(not (root / "HEAVY-SLOT").exists(), "17: the slot is free once it ends")


def case_19_a_slot_wait_that_runs_out_says_so(tmp):
    tool = idle_lock()
    if tool is None:
        print("   19: idle_lock.py is not in this checkout: not run", flush=True)
        return
    lab = Lab(tmp / "a", arms=ARMS[:1])
    root = tmp / "runs"
    (root / "locks").mkdir(parents=True)
    prefix = [sys.executable, str(tool), "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "5", "--wait-timeout-min", "0.01", "--min-free-gb", "999999",
              "--runs-root", str(root), "--checks", "1", "--ok-interval-s", "0.1", "--busy-interval-s", "0.1"]
    text, refusal, ctx = run_stage(lab, {"heavyPrefix": prefix, "lockDir": str(root / "locks")})
    T.check(refusal is not None and "wait for memory ran out" in refusal, f"19: the stage says the wait for memory ran out, got {refusal}")
    T.check(refusal is not None and "999999" in refusal, f"19: it says how much was needed, got {refusal}")
    T.check(refusal is not None and "close" in refusal and "--min-free-gb" in refusal, f"19: it says what to try, got {refusal}")
    T.expect(lab.starts(), [], "19: no reader started")


def case_20_a_childs_run_store_is_its_parents(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:3])
    env = lab.env()
    env.pop("HOUSECARL_RUNLOG_DIR", None)
    env["HOUSECARL_DATA_DIR"] = str(tmp / "parent-data")
    p = subprocess.run(lab.argv(3), capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    T.check(p.returncode == 0, f"20: finished, exit {p.returncode}: {p.stderr[-200:]}")
    want = str(tmp / "parent-data" / "runs")
    T.expect(sorted({r["runlogDir"] for r in lab.starts()}), [want], "20: every reader writes the parent's run store, not one under its private data folder")
    hist = tmp / "parent-data" / "runs" / "history.jsonl"
    ops = [json.loads(x)["op"] for x in hist.read_text(encoding="utf-8").splitlines()] if hist.is_file() else []
    T.expect(ops.count("evalrecords"), 4, "20: the parent's and each child's own run are in that one store")


def case_21_an_earlier_captures_document_is_never_read(tmp):
    lab = Lab(tmp / "a", arms=ARMS[:2])
    lab.fixed_out = tmp / "a" / "same-out"
    lab.capture(workers=2)
    T.check(all("imports" in v for v in lab.summary().values()) and lab.summary(), "21: the first capture read both arms")
    # new plugin bytes reread both; this time one arm's capture process dies every time, in the folder holding the earlier documents
    (lab.data / "Subj.esp").write_bytes(b"changed")
    p = lab.capture(workers=2, script={"candidate-01": {"killParent": True}, "baseline": {"delay": 3}})
    v = lab.summary().get("candidate-01", {})
    T.check("refused" in v and "imports" not in v, f"21: the arm that didn't finish is refused, whatever an earlier capture left, got {v}")
    T.expect(p.returncode, 2, "21: and the capture's exit says so")


def case_22_finished_means_exit_imports_and_checkpoint(tmp):
    import evalrecords_workers as W
    f = tmp / "one.json"
    f.write_text("{}", encoding="utf-8")
    ok = {"summary": {"a": {"imports": [str(f)]}}}
    yes, no = (lambda arm, v: True), (lambda arm, v: False)
    T.expect(W.judge("a", ok, 0, yes)[0], "finished", "22: exit 0, imports on disk and a checkpoint is finished")
    T.expect(W.judge("a", ok, 1, yes)[0], "none", "22: a nonzero exit is not finished, whatever the document says")
    T.expect(W.judge("a", ok, None, yes)[0], "none", "22: a stopped process is not finished")
    T.expect(W.judge("a", ok, 0, no)[0], "none", "22: no checkpoint is not finished")
    gone = {"summary": {"a": {"imports": [str(tmp / "gone.json")]}}}
    T.expect(W.judge("a", gone, 0, yes)[0], "none", "22: an import that isn't on disk is not finished")
    T.expect(W.judge("a", {"summary": {"a": {"findings": {}}}}, 0, yes)[0], "none", "22: a document with no imports is not finished")
    T.expect(W.judge("a", {"summary": {"b": ok["summary"]["a"]}}, 0, yes)[0], "none", "22: another arm's result is not this arm's")
    T.expect(W.judge("a", None, 0, yes)[0], "none", "22: no document is not finished")
    T.expect(W.judge("a", {"summary": {"a": {"refused": "why"}}}, 2, yes)[0], "refused", "22: a refusal is a refusal")


def case_23_an_unreadable_document_is_no_result(tmp):
    import evalrecords_workers as W
    f = tmp / "half.json"
    f.write_text('{"summary": {"a": {"imports"', encoding="utf-8")
    try:
        got = W.read_doc(f)
    except Exception as ex:
        got = f"raised {type(ex).__name__}"
    T.expect(got, None, "23: a document cut short counts as no result and raises nothing")
    T.expect(W.read_doc(tmp / "absent.json"), None, "23: so does an absent one")
    lab = Lab(tmp / "a", arms=ARMS[:1])
    lab.set_memory(**PLENTY)
    lab.capture(workers=1)
    T.check(not list((lab.out).rglob("*.json.tmp")), "23: nothing is left under a working name")


def case_24_a_bad_worker_value_is_one_plain_sentence(tmp):
    for bad in ("0", "-1", "2.5", "many"):
        lab = Lab(tmp / f"v{bad}", arms=ARMS[:1])
        p = lab.capture(workers=bad)
        T.expect(p.returncode, 2, f"24: --workers {bad} is refused")
        T.check("auto or a whole number" in p.stderr and "Traceback" not in p.stderr, f"24: --workers {bad} gets one sentence, got {p.stderr[-160:]!r}")
        T.expect(lab.starts(), [], f"24: --workers {bad} reads no arm")
    lab = Lab(tmp / "stage", arms=ARMS[:1])
    text, refusal, ctx = run_stage(lab, {"recordWorkers": "many"})
    T.check(refusal is not None and "recordWorkers" in refusal and "auto or a whole number" in refusal, f"24: the stage's sentence names the machine file's value, got {refusal}")
    T.expect(lab.starts(), [], "24: and the stage reads no arm")
    T.check(not list((ctx.out / "records").glob("capture-*")), "24: before any capture starts")


def case_25_a_failed_measurement_costs_no_transcript(tmp):
    import evalrecords as er
    import evalrecords_workers as W
    lab = Lab(tmp / "a", arms=ARMS[:1])
    plan = json.loads((lab.run_dir / "plan.json").read_text(encoding="utf-8"))
    view = tmp / "view"
    er.write_view(view, plan["arms"][0], lab.profile)
    saved = W.process_counters

    def boom(_p=None):
        raise RuntimeError("no counters here")
    W.process_counters = boom
    counters, got = {}, None
    session = tmp / "session"
    try:
        got = er.capture_arm(plan, plan["arms"][0], ["Base.esm", "Win.esp"], f'"{sys.executable}" "{FAKE}"', {"HouseCarl__ProfileDir": str(view)}, session, er.FieldScope(), lambda m: None, counters=counters)
    except Exception as ex:
        T.check(False, f"25: a failed measurement must not fail the arm, got {type(ex).__name__}: {ex}")
    finally:
        W.process_counters = saved
    T.check(got is not None, "25: the arm's answer stands")
    T.check((session / "request.jsonl").is_file() and (session / "response.jsonl").is_file(), f"25: its transcript has its real names, got {sorted(p.name for p in session.iterdir())}")


CASES = {k: v for k, v in sorted(globals().items()) if k.startswith("case_")}


def main():
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="hc-par-"))
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
    os.environ["PYTHONIOENCODING"] = "utf-8"
    reds = []
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
            if len(T.FAILURES) > before:
                reds.append(name)
            print(f"   -> {'RED' if len(T.FAILURES) > before else 'green'}", flush=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(T.FAILURES)} failed checks; red: {' '.join(reds)}")
    return 1 if T.FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
