"""Known answers for the records stage's reads and for keeping finished work across a stop or a budget change.

    python test_records_keep_work.py [PREFIX]     KEEP_HARNESS names the _geometry-harness copy under test.

The built-in controller (runcontrol), real ordersweep and conflictdiff over the fixtures with fake probes; real
evalrecords with a stand-in reader. No game, no lab, no houseCARL server.
"""
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is
MODULE = SW.MODULE   # evalrecords.py, the record capture under test
HARNESS = Path(os.environ.get("KEEP_HARNESS") or SW.HARNESS).resolve()   # SkyWright's tools: evalrun.py and its suite
sys.dont_write_bytecode = True
sys.path[:0] = [str(HARNESS / "tests"), str(HARNESS), str(HARNESS.parent / "scripts")]
import test_evalrun as T  # noqa: E402

T.EVALRUN = HARNESS / "evalrun.py"
FAKE = HERE / "fixtures" / "record_capture" / "fake_capture_mcp.py"
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
        er = str(MODULE / "evalrecords.py")
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
    er = load_module("er_under_test", MODULE / "evalrecords.py")
    d = tmp / "r5"
    d.mkdir()
    (d / "reader.exe").write_bytes(b"apphost")
    (d / "reader.dll").write_bytes(b"build one")
    one = er.reader_identity(f'"{d / "reader.exe"}"')
    (d / "reader.dll").write_bytes(b"build two")
    two = er.reader_identity(f'"{d / "reader.exe"}"')
    T.check(one != two, "r5: a changed dll beside an unchanged exe changes the reader's identity")


# ---------------------------------------------------------------- authored removals, judged against the plugin's own masters

def write_plugin(path: Path, masters, esm=False):
    """A minimal TES4 header: the flags word and one MAST subrecord per master (all evalpipeline.plugin_header reads)."""
    body = b""
    for m in masters:
        z = m.encode("cp1252") + b"\0"
        body += b"MAST" + len(z).to_bytes(2, "little") + z + b"DATA" + (8).to_bytes(2, "little") + bytes(8)
    path.write_bytes(b"TES4" + len(body).to_bytes(4, "little") + (1 if esm else 0).to_bytes(4, "little") + bytes(12) + body)


REMOVAL_MASTERS = {"Base.esm": [], "Mid.esm": ["Base.esm"], "Solo.esm": [], "Lux.esp": ["Base.esm"],
                   "Late.esp": ["Mid.esm"], "Other.esp": ["Solo.esm"], "Win.esp": ["Base.esm"]}
REMOVED_BY_NAME = "a removal by a plugin whose masters' headers could not all be read"


class RemovalLab(Lab):
    """The stand-in reader's removal scenario over plugin files with real TES4 headers. Late.esp and Other.esp are evaluated."""

    def __init__(self, tmp: Path):
        super().__init__(tmp, evaluated=())
        self.plugins = list(REMOVAL_MASTERS)
        for p, ms in REMOVAL_MASTERS.items():
            write_plugin(self.data / p, ms, esm=p.endswith(".esm"))
        (tmp / "prep.json").write_text(json.dumps({"baseline": ["Base.esm", "Mid.esm", "Solo.esm", "Lux.esp", "Win.esp"]}), encoding="utf-8")
        self.write_plan()

    def capture(self):
        os.environ["FAKE_CAPTURE_SCENARIO"] = "removal"
        try:
            return super().capture()
        finally:
            os.environ.pop("FAKE_CAPTURE_SCENARIO", None)


def contested(summary: dict, arm="baseline") -> dict:
    """The contested-fields rows document the capture wrote for one arm."""
    f = next(Path(p).parent / "rows.json" for p in summary[arm]["imports"] if "records.contested-fields" in p)
    return json.loads(f.read_text(encoding="utf-8"))


def removal_rows(doc: dict) -> list:
    return sorted((r["Necessary"][0]["FormKey"], r["A"], r["B"], r["Field"], r["Necessary"][0]["Values"]["lost"]) for r in doc["Conflicts"])


def check_removals(doc, tag):
    rows = removal_rows(doc)
    T.check(("0000B1:Base.esm", "Late.esp", "Win.esp", "EnableParent", "Reference=(absent)") in rows,
            f"{tag}: Late.esp's removal of the leaf its master's master holds ('only in' wording) is a lost edit, got {rows}")
    T.check(("0000B2:Base.esm", "Late.esp", "Win.esp", "EnableParent", "Reference=(absent)") in rows,
            f"{tag}: the same removal in the 'ABSENT here' wording is a lost edit, got {rows}")
    T.check(not [r for r in rows if r[0] == "0000B3:Base.esm"],
            f"{tag}: a leaf the plugin's only master also lacks was never seen, so it is no removal, got {rows}")
    T.check(not [r for r in rows if r[0] == "0000B4:Base.esm"],
            f"{tag}: a plugin not built on the leaf's holders did not remove it, got {rows}")
    T.expect(doc["Coverage"][0]["Judged"], 2, f"{tag}: the two removals are judged, nothing else")
    T.expect(doc["Coverage"][0]["Unjudgeable"], {}, f"{tag}: nothing is unjudgeable")


def case_r7_a_masters_removal_lost_to_the_winner_is_found_by_the_capture(tmp):
    lab = RemovalLab(tmp / "r7")
    lab.capture()
    check_removals(contested(json.loads((tmp / "r7" / "out-1" / "capture-summary.json").read_text(encoding="utf-8"))), "r7")


def case_r8_a_finished_arm_reused_keeps_its_removal_rows(tmp):
    lab = RemovalLab(tmp / "r8")
    T.expect(lab.capture(), 1, "r8: the first capture reads")
    T.expect(lab.capture(), 0, "r8: the unchanged arm is reused")
    check_removals(contested(json.loads((tmp / "r8" / "out-2" / "capture-summary.json").read_text(encoding="utf-8"))), "r8")


def case_r9_a_master_missing_from_disk_is_counted_by_name(tmp):
    lab = RemovalLab(tmp / "r9")
    (lab.data / "Mid.esm").unlink()
    lab.capture()
    doc = contested(json.loads((tmp / "r9" / "out-1" / "capture-summary.json").read_text(encoding="utf-8")))
    T.expect(removal_rows(doc), [], "r9: with Late.esp's master unreadable, none of its removals is judged a lost edit")
    T.expect(doc["Coverage"][0]["Unjudgeable"], {REMOVED_BY_NAME: 3},
             "r9: each of its three removals is counted under its own reason, not dropped as 'no masters'")


# ---------------------------------------------------------------- a stopped sweep

def ev(args, env, timeout=300):
    return subprocess.run(T.CONTROLLER + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8", errors="replace",
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
