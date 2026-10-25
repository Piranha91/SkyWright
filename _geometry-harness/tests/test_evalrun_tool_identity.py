"""evalrun checks, binds and finalizes the identity of the programs that ran (cases 1470-1490).

    python _geometry-harness/tests/test_evalrun_tool_identity.py            # every case
    python _geometry-harness/tests/test_evalrun_tool_identity.py 1476       # one case (a prefix match)

A plan freezes the identity of every program it can launch: each probe's Release package (its top-level non-PDB files
and runtimes/), conflictdiff, a --probe-cmd override (its command text and the files it names) and the controller's
own scripts. `run` and `resume` compare those with what is on disk before anything is written, check them again at
each child's launch and before its result is accepted, and every accepted receipt names the identity of the code that
ran, per role. `finalize`, `bundle` and `verify-bundle` compare the stored identities offline, so a consistent old
evaluation still packages after its tools change, and a mixed one never certifies.

Cases 1470-1487 are the design review's eighteen acceptance cases in order (2026-10-21 review, cases 1-18); 1488-1490
are added arms. Dependency-free; no game, no instance, no production binary. Every harness is a disposable folder: a
copy of conflictdiff built from this checkout's source (or HOUSECARL_EVALRUN_TEST_HARNESS's), and fake probe packages
whose launches the in-process controller's Runner maps to the evalrun fixture probes. That Runner also records every
job before it starts, which is the child-start sentinel; children started by a controller run from the command line
append to a sentinel file instead.

    1470  own dll changed, launcher identical: run and resume refuse before any child; RUN bytes unchanged.
    1471  launcher changed, dlls identical: the same refusal, naming the launcher.
    1472  core dll, a dependency, a runtimes/ dll, deps.json, runtimeconfig.json, a copied data file, an added dll, a
          deleted dll, an unreadable file: each refuses before work.
    1473  A completed, B pending; B's dll changed: refused before reusing A or starting B.
    1474  A completed, B pending; A's dll changed: refused, no re-run of A; restored, the continuation keeps A's receipt.
    1475  every swept part completed; a shared dependency of the swept probe changed: refused.
    1476  swept arm accepted, reverse pending; conflictdiff.dll changed: refused before the sweep; arm receipt unchanged.
    1477  each controller script changed: refused naming it; plan bytes unchanged.
    1478  same --probe-cmd text, changed script: refused; never equal by text.
    1479  mismatch + resume --rebuild-ledger: refused before the rebuild; ledger, journal, receipts unchanged.
    1480  an accepted receipt naming another emitter, comparator or controller: finalize 3, bundle 3, crafted bundle
          fails verify with no tool present.
    1481  consistent receipts, tools later changed and deleted: finalize/bundle/verify succeed and say verified; resume
          refuses.
    1482  copied RUN reuses with no child; relocated bundle verifies identically and reads nothing outside itself.
    1483  the identity primitive: same package at two roots, PDB, mtime and run-time subfolder changes are equal; a stray
          top-level file is not; an identical replacement runs; a deleted root refuses.
    1484  an excluded tool changed: no refusal; an included one changed: refusal.
    1485  legacy plan, partial identity, unreadable file, legacy bundle; absent at plan and run (blocked, nothing
          accepted); crafted receipts with a null or missing role digest fail finalize.
    1486  unchanged tools: run, run, resume make no child and keep every receipt.
    1487  (a) a change between two launches stops the second; (b) a change during a child rejects its result; (c) a
          comparator change during the sweep rejects the diffs and keeps the emits.
    1488  ordersweep's cache key for an override follows the script's bytes, not only the command text.
    1489  accepted receipts bind the measured identity per role, equal to the plan's.
    1490  import-results with a changed controller script refuses; RUN bytes unchanged.

Cases 1500-1503 are the stability of accounting receipts (rows that ran nothing) across invocations:

    1500  run, run, resume on an unchanged plan keep every receipt byte-identical, accounting receipts included.
    1501  control: an accounting row whose state changes between invocations (candidate changed, then gone, then
          restored) gets a new receipt that says so each time.
    1502  control: an mcp-only row later imported keeps the import's receipt on the next run and resume.
    1503  a candidate changed after planning and left changed: run, resume keep both blocked receipts byte-identical.
"""
from __future__ import annotations

import builtins
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import test_evalrun as T  # noqa: E402  (fixture generator, conflictdiff build, fake probes)
import test_import_empty_population as IMP  # noqa: E402  (the import manifest builder)

HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
FAKES = T.FIXT / "fakeprobes"
CASE = "case-08-interrupt-resume"
MAO = "loadability.masters-and-order"
BOS = "state.switch-census"
NOTAPP = "geometry.bos-overlay"
NAV = "geometry.navmesh-ownership"
DANGLING = "loadability.dangling-references"
CAND = "candidate-01-reorder"
ACTION = "new plan in a new directory"
#: The controller's scripts, repository-relative.
CONTROLLER = ["_geometry-harness/evalrun.py", "_geometry-harness/evalregistry.py", "_geometry-harness/evalsettings.py",
              "_geometry-harness/stategen.py", "_geometry-harness/switchflip_scope.py",
              "_geometry-harness/switchflip_outcome.py", "_geometry-harness/evallive.py", "_geometry-harness/ordersweep.py",
              "_geometry-harness/toolidentity.py", "scripts/runlog.py"]

FAILURES: list[str] = []
_n = [0]


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def sha(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def load(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def save(p: Path, doc) -> None:
    Path(p).write_text(json.dumps(doc, indent=1), encoding="utf-8")


def snap(d: Path) -> dict:
    """Every file under d, by relative path, with its sha256."""
    d = Path(d)
    return {p.relative_to(d).as_posix(): sha(p) for p in sorted(d.rglob("*")) if p.is_file()}


def snap_diff(a: dict, b: dict) -> str:
    changed = sorted(k for k in set(a) | set(b) if a.get(k) != b.get(k))
    return ", ".join(changed[:8]) + (" ..." if len(changed) > 8 else "") if changed else "none"


def exe(p: str) -> str:
    return f"{p}.exe" if os.name == "nt" else p


def pkg(h: Path, probe: str) -> Path:
    return Path(h) / probe / "bin" / "Release" / "net9.0"


def pkg_files(probe: str) -> dict:
    """A fake Release folder: distinct small bytes per file, in the shapes a real build puts there."""
    return {exe(probe): b"MZ fake apphost for " + probe.encode(),
            f"{probe}.dll": b"fake managed assembly " + probe.encode(),
            "housecarl-core.dll": b"fake core assembly",
            "Mutagen.Bethesda.Core.dll": b"fake dependency",
            f"{probe}.deps.json": b'{"fake": "deps"}',
            f"{probe}.runtimeconfig.json": b'{"runtimeOptions": {"tfm": "net9.0"}}',
            "load-settle-exemptions.json": b"[]",
            f"{probe}.pdb": b"fake symbols",
            "runtimes/win-x64/native/nifly.dll": b"fake native"}


def touch(path: Path, mode: str = "change") -> None:
    path = Path(path)
    if mode == "change":
        path.write_bytes(path.read_bytes() + b"\x00changed")
    elif mode == "delete":
        path.unlink()
    elif mode == "add":
        path.write_bytes(b"an added file")


def evidence_file(rel: str) -> bool:
    """A receipt, measured or accounting, or a measured output."""
    return rel.startswith(("receipts/", "outputs/", "sweep/"))


def reseal(run: Path) -> None:
    """plan.json's seal after a deliberate edit (only where the case is about the plan's content, never tool drift)."""
    (Path(run) / "plan.sha256").write_text(sha(Path(run) / "plan.json") + "\n", encoding="utf-8")


def entries(run: Path) -> dict:
    return (load(Path(run) / "ledger.json") or {}).get("entries") or {}


def status(run: Path, cid: str, aid: str, part: str | None = None) -> str:
    k = f"{cid}__{aid}" + (f"__{part}" if part else "")
    return str((entries(run).get(k) or {}).get("status"))


def receipt(run: Path, cid: str, aid: str, part: str | None = None) -> dict:
    k = f"{cid}__{aid}" + (f"__{part}" if part else "")
    return load(Path(run) / "receipts" / f"{k}.json") or {}


@contextlib.contextmanager
def unreadable(target: Path):
    """Reading `target` fails with PermissionError for code in this process (a file the tool cannot read)."""
    target = os.path.normcase(os.path.abspath(str(target)))
    real = builtins.open

    def guarded(file, *a, **k):
        if isinstance(file, (str, os.PathLike)) and os.path.normcase(os.path.abspath(os.fspath(file))) == target:
            raise PermissionError(13, "simulated: access denied", os.fspath(file))
        return real(file, *a, **k)
    builtins.open, io.open = guarded, guarded
    try:
        yield
    finally:
        builtins.open, io.open = real, real


# ------------------------------------------------------------------------------------------ one case's world

class Env:
    """A disposable harness (conflictdiff copy + fake packages), a fixture plan, and an in-process controller whose
    Runner maps a fake package's launch to the fixture probe and records every job before it starts."""

    def __init__(self, root: Path, packages=(), select=(MAO,), extra=(), evalrun: Path = EVALRUN,
                 absent=(), registry: str = "fixture-check-registry.json", scenario: Path | None = None):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.h = self.root / "harness"
        src = T.REAL_HARNESS / "conflictdiff" / "bin" / "Release" / "net9.0"
        shutil.copytree(src, pkg(self.h, "conflictdiff"))
        self.packages = list(packages)
        for p in self.packages:
            for rel, data in pkg_files(p).items():
                f = pkg(self.h, p) / rel
                f.parent.mkdir(parents=True, exist_ok=True)
                f.write_bytes(data)
        self.absent = set(absent)
        self.evalrun = Path(evalrun)
        self.store = self.root / "store"
        self.sentinel = self.root / "children.txt"
        self.scen = scenario or T.case_paths(CASE)[3]
        self.fakes = self.root / "fakes"
        self.fakes.mkdir()
        for f in sorted(FAKES.glob("*.py")):
            p = f.stem
            (self.fakes / f"{p}.py").write_text(
                "import os, sys\n"
                "s = os.environ.get('EVALRUN_TI_SENTINEL')\n"
                "if s:\n"
                f"    open(s, 'a', encoding='utf-8').write({p!r} + '\\n')\n"
                f"sys.path.insert(0, {str(T.FIXT)!r})\n"
                "from fakeprobe import main\n"
                f"raise SystemExit(main({p!r}))\n", encoding="utf-8")
        self.run = self.root / "run"
        self.select, self.extra, self.registry = list(select), list(extra), registry
        self.mod = None
        self.jobs: list[str] = []
        self.hook = None

    def wrapper(self, probe: str) -> Path:
        return self.fakes / f"{probe}.py"

    def cmds(self) -> list[str]:
        out = []
        for f in sorted(self.fakes.glob("*.py")):
            if f.stem not in self.packages and f.stem not in self.absent:
                out += ["--probe-cmd", f"{f.stem}=python {f}"]
        return out

    def env(self, **more) -> dict:
        e = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(self.scen), HOUSECARL_RUNLOG_DIR=str(self.store),
                 EVALRUN_TI_SENTINEL=str(self.sentinel), PYTHONIOENCODING="utf-8")
        e.update({k: str(v) for k, v in more.items()})
        return e

    def plan(self, run: Path | None = None, extra=()) -> dict | None:
        run = Path(run or self.run)
        d = T.GEN / "cases" / CASE
        args = [sys.executable, str(self.evalrun), "plan", "--registry", str(T.GEN / "registry" / self.registry),
                "--scope", str(d / "scope.json"), "--candidates", str(d / "candidates"), "--out", str(run),
                "--harness", str(self.h), "--select", ",".join(self.select)] + self.cmds() + \
            [str(x) for x in list(self.extra) + list(extra)]
        r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", env=self.env(),
                           timeout=300)
        if r.returncode != 0:
            print(r.stdout[-1500:], r.stderr[-1500:])
        check(r.returncode == 0, f"fixture: plan exit 0 for {run.name}")
        return load(run / "plan.json")

    def cli(self, args, scenario: Path | None = None) -> subprocess.CompletedProcess:
        """The controller as a separate process (used where the controller's own files are the subject)."""
        return subprocess.run([sys.executable, str(self.evalrun)] + [str(a) for a in args], capture_output=True,
                              text=True, encoding="utf-8", errors="replace",
                              env=self.env(EVALRUN_FAKE_SCENARIO=scenario or self.scen), timeout=300)

    def _module(self):
        if self.mod is not None:
            return self.mod
        _n[0] += 1
        spec = importlib.util.spec_from_file_location(f"evalrun_ti_{_n[0]}", self.evalrun)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        base = mod.Runner
        outer = self
        pkg_exes = {os.path.normcase(str(pkg(self.h, p) / exe(p))): p for p in self.packages}

        class HookRunner(base):
            def run(self, jobs, echo=True):
                mapped = []
                for j in jobs:
                    j = dict(j)
                    outer.jobs.append(str(j.get("id")))
                    argv = list(j.get("argv") or [])
                    p = pkg_exes.get(os.path.normcase(str(argv[0]))) if argv else None
                    if p:
                        argv = [sys.executable, str(outer.wrapper(p))] + argv[1:]
                    if j.get("id") == "ordersweep":
                        for q in outer.packages:
                            argv += ["--probe-cmd", f"{q}=python {outer.wrapper(q)}"]
                    j["argv"] = argv
                    mapped.append(j)

                def call(js):
                    return base.run(self, js, echo)
                return outer.hook(call, mapped) if outer.hook is not None else call(mapped)
        mod.Runner = HookRunner
        self.mod = mod
        return mod

    def inproc(self, args, hook=None, scenario: Path | None = None):
        """(exit code or 'raised ...', stdout, stderr) of one verb run in this process."""
        mod = self._module()
        self.hook, self.jobs = hook, []
        names = ["EVALRUN_FAKE_SCENARIO", "HOUSECARL_RUNLOG_DIR", "EVALRUN_TI_SENTINEL", "PYTHONIOENCODING"]
        saved = {k: os.environ.get(k) for k in names}
        os.environ.update({k: v for k, v in self.env(EVALRUN_FAKE_SCENARIO=scenario or self.scen).items() if k in names})
        out, err = io.StringIO(), io.StringIO()
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                a = mod.build_parser().parse_args([str(x) for x in args])
                rc = a.fn(a)
        except Exception as ex:                       # recorded as an outcome; a case decides what it means
            rc = f"raised {type(ex).__name__}: {ex}"
        finally:
            self.hook = None
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        return rc, out.getvalue(), err.getvalue()

    def copy(self, name: str, src: Path | None = None) -> Path:
        dst = self.root / name
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(src or self.run, dst)
        return dst

    def save_harness(self) -> Path:
        keep = self.root / "harness-pristine"
        if not keep.exists():
            shutil.copytree(self.h, keep)
        return keep

    def restore_harness(self) -> None:
        keep = self.save_harness()
        shutil.rmtree(self.h)
        shutil.copytree(keep, self.h)


def refused_before_work(env: Env, run: Path, verb: str, label: str, words: list[str], *, rebuild=False, open_guard=None):
    """`verb` on `run` exits 2 naming every word, starts no child and leaves every file under the RUN as it was."""
    before = snap(run)
    args = [verb, "--run", run] + (["--rebuild-ledger"] if rebuild else [])
    if open_guard is not None:
        with unreadable(open_guard):
            rc, out, err = env.inproc(args)
    else:
        rc, out, err = env.inproc(args)
    msg = (err + out).replace("\n", " ")
    check(rc == 2, f"{label}: {verb} exits 2 (got {rc!r})")
    check(not env.jobs, f"{label}: {verb} starts no child (jobs: {env.jobs[:4]})")
    missing = [w for w in words if w.lower() not in msg.lower()]
    check(not missing, f"{label}: {verb}'s refusal names {words} (missing {missing}; said {msg[-300:]!r})")
    after = snap(run)
    check(after == before, f"{label}: {verb} leaves every RUN file unchanged (changed: {snap_diff(before, after)})")


def both_verbs(env: Env, label: str, words: list[str], mutate=None, *, src: Path | None = None, open_guard=None):
    """Prepare (mutate) once, then `run` and `resume` each on a fresh copy of the prepared RUN."""
    if mutate:
        mutate()
    for verb in ("run", "resume"):
        r = env.copy(f"{label.replace(' ', '-')}-{verb}", src)
        refused_before_work(env, r, verb, f"{label} [{verb}]", words, open_guard=open_guard)


def budget_hook(probe: str):
    """The Runner refuses every job of `probe` before launch (the deadline's own path): those entries stay pending."""
    def hook(call, jobs):
        if any(str(j.get("id", "")).find(probe) >= 0 or probe in " ".join(map(str, j.get("argv") or [])) for j in jobs):
            return [{"id": j.get("id"), "exitCode": None, "stopped": "before-launch", "runId": None, "seconds": 0.0}
                    for j in jobs]
        return call(jobs)
    return hook


def first_arm_completed(env: Env, run: Path) -> None:
    """MAO completed on every arm, BOS pending (refused before launch by the budget hook)."""
    rc, out, err = env.inproc(["run", "--run", run], hook=budget_hook("switchprobe"))
    check(rc == 0, f"fixture: first run exits 0 (got {rc!r}; {err[-300:]!r})")
    check(all(status(run, MAO, a) == "completed" for a in ("baseline", CAND)),
          f"fixture: {MAO} completed on both arms ({status(run, MAO, 'baseline')}, {status(run, MAO, CAND)})")
    check(status(run, BOS, "baseline") == "skipped-budget",
          f"fixture: {BOS} pending (skipped-budget) on the baseline (got {status(run, BOS, 'baseline')})")


# --------------------------------------------------------------------------------------------- the cases

def case_1470(tmp):
    env = Env(tmp / "1470", packages=["scopeprobe"])
    env.plan()
    both_verbs(env, "1470 own dll", ["scopeprobe", "scopeprobe.dll", ACTION],
               lambda: touch(pkg(env.h, "scopeprobe") / "scopeprobe.dll"))


def case_1471(tmp):
    env = Env(tmp / "1471", packages=["scopeprobe"])
    env.plan()
    both_verbs(env, "1471 launcher", ["scopeprobe", exe("scopeprobe"), ACTION],
               lambda: touch(pkg(env.h, "scopeprobe") / exe("scopeprobe")))


def case_1472(tmp):
    env = Env(tmp / "1472", packages=["scopeprobe"])
    env.plan()
    env.save_harness()
    d = pkg(env.h, "scopeprobe")
    arms = [("core dll", "housecarl-core.dll", "change"), ("dependency", "Mutagen.Bethesda.Core.dll", "change"),
            ("runtimes dll", "runtimes/win-x64/native/nifly.dll", "change"),
            ("deps json", "scopeprobe.deps.json", "change"), ("runtimeconfig", "scopeprobe.runtimeconfig.json", "change"),
            ("copied data", "load-settle-exemptions.json", "change"), ("added dll", "Added.Dependency.dll", "add"),
            ("deleted dll", "Mutagen.Bethesda.Core.dll", "delete")]
    for label, rel, mode in arms:
        env.restore_harness()
        word = Path(rel).name
        both_verbs(env, f"1472 {label}", ["scopeprobe", word, ACTION], lambda: touch(d / rel, mode))
    env.restore_harness()
    both_verbs(env, "1472 unreadable", ["scopeprobe", "scopeprobe.dll", "read"], None,
               open_guard=d / "scopeprobe.dll")


def case_1473(tmp):
    env = Env(tmp / "1473", packages=["scopeprobe", "switchprobe"], select=(MAO, BOS))
    env.plan()
    first_arm_completed(env, env.run)
    both_verbs(env, "1473 pending tool", ["switchprobe", "switchprobe.dll", ACTION],
               lambda: touch(pkg(env.h, "switchprobe") / "switchprobe.dll"))


def case_1474(tmp):
    env = Env(tmp / "1474", packages=["scopeprobe", "switchprobe"], select=(MAO, BOS))
    env.plan()
    first_arm_completed(env, env.run)
    env.save_harness()
    kept = receipt(env.run, MAO, "baseline")
    kept_bytes = (env.run / "receipts" / f"{MAO}__baseline.json").read_bytes()
    both_verbs(env, "1474 completed tool", ["scopeprobe", "scopeprobe.dll", ACTION],
               lambda: touch(pkg(env.h, "scopeprobe") / "scopeprobe.dll"))
    env.restore_harness()
    r = env.copy("1474-restored")
    rc, out, err = env.inproc(["resume", "--run", r])
    check(rc == 0, f"1474 restored: resume exits 0 (got {rc!r}; {err[-300:]!r})")
    check(env.jobs and all("switchprobe" in j or BOS in j for j in env.jobs) and not any(MAO in j for j in env.jobs),
          f"1474 restored: only {BOS}'s jobs ran (jobs: {env.jobs})")
    check((r / "receipts" / f"{MAO}__baseline.json").read_bytes() == kept_bytes
          and receipt(r, MAO, "baseline").get("receiptId") == kept.get("receiptId"),
          f"1474 restored: {MAO}'s receipt bytes and id unchanged")


def sweep_env(tmp: Path, name: str, scenario: Path | None = None, packages=("scopeprobe", "navownprobe")) -> Env:
    return Env(tmp / name, packages=list(packages), select=(MAO, NAV), scenario=scenario)


def case_1475(tmp):
    env = sweep_env(tmp, "1475")
    env.plan()
    rc, out, err = env.inproc(["run", "--run", env.run])
    check(rc == 0, f"1475 fixture: full run exits 0 (got {rc!r}; {err[-300:]!r})")
    parts = [status(env.run, NAV, CAND, p) for p in ("arm", "reverse", "diff")]
    check(all(p == "completed" for p in parts) and status(env.run, NAV, "baseline", "arm") == "completed",
          f"1475 fixture: every swept part completed ({parts})")
    both_verbs(env, "1475 shared dependency", ["navownprobe", "Mutagen.Bethesda.Core.dll", ACTION],
               lambda: touch(pkg(env.h, "navownprobe") / "Mutagen.Bethesda.Core.dll"))


def exit_reverse_scenario(root: Path) -> Path:
    doc = load(T.case_paths(CASE)[3])
    doc["arms"]["navownprobe"]["candidate-01-reorder.reverse"] = {"behaviour": "exit", "code": 3}
    p = Path(root) / "scenario-reverse-exit.json"
    save(p, doc)
    return p


def case_1476(tmp):
    root = tmp / "1476"
    root.mkdir(parents=True)
    env = sweep_env(tmp, "1476")
    env.plan()
    rc, out, err = env.inproc(["run", "--run", env.run], scenario=exit_reverse_scenario(root))
    check(rc == 0, f"1476 fixture: first run exits 0 (got {rc!r})")
    check(status(env.run, NAV, CAND, "arm") == "completed"
          and status(env.run, NAV, CAND, "reverse") not in ("completed", "reused"),
          f"1476 fixture: arm completed, reverse pending ({status(env.run, NAV, CAND, 'arm')}, "
          f"{status(env.run, NAV, CAND, 'reverse')})")
    arm_bytes = (env.run / "receipts" / f"{NAV}__{CAND}__arm.json").read_bytes()
    both_verbs(env, "1476 comparator", ["conflictdiff", "conflictdiff.dll", ACTION],
               lambda: touch(pkg(env.h, "conflictdiff") / "conflictdiff.dll"))
    r = env.root / "1476-comparator-resume"
    check((r / "receipts" / f"{NAV}__{CAND}__arm.json").read_bytes() == arm_bytes,
          "1476: the emitted arm's receipt is unchanged")


def controller_copy(dst: Path) -> Path:
    """A disposable controller: the harness's top-level scripts and scripts/runlog.py. Returns its evalrun.py."""
    g = dst / "_geometry-harness"
    g.mkdir(parents=True)
    for f in HARNESS.glob("*.py"):
        shutil.copy2(f, g / f.name)
    (dst / "scripts").mkdir()
    shutil.copy2(REPO / "scripts" / "runlog.py", dst / "scripts" / "runlog.py")
    return g / "evalrun.py"


def case_1477(tmp):
    ctl = tmp / "1477" / "controller"
    evalrun = controller_copy(ctl)
    env = Env(tmp / "1477" / "case", select=(MAO,), evalrun=evalrun)
    env.plan()
    plan_bytes = (env.run / "plan.json").read_bytes()
    for rel in CONTROLLER:
        f = ctl / rel
        if not f.exists():
            check(False, f"1477 {Path(rel).name}: the controller file exists in this checkout")
            continue
        orig = f.read_bytes()
        try:
            f.write_bytes(orig + b"\n# a change after the plan was made\n")
            for verb in ("run", "resume"):
                r = env.copy(f"1477-{Path(rel).stem}-{verb}")
                before = snap(r)
                if env.sentinel.exists():
                    env.sentinel.unlink()
                p = env.cli([verb, "--run", r])
                msg = (p.stderr + p.stdout).replace("\n", " ")
                label = f"1477 {Path(rel).name} [{verb}]"
                check(p.returncode == 2, f"{label}: exits 2 (got {p.returncode})")
                check(not env.sentinel.exists(), f"{label}: starts no child")
                check(Path(rel).name in msg and ACTION in msg,
                      f"{label}: the refusal names {Path(rel).name} and the action (said {msg[-300:]!r})")
                after = snap(r)
                check(after == before, f"{label}: RUN bytes unchanged (changed: {snap_diff(before, after)})")
                check((r / "plan.json").read_bytes() == plan_bytes, f"{label}: the frozen plan (settings) unchanged")
        finally:
            f.write_bytes(orig)


def case_1478(tmp):
    env = Env(tmp / "1478", select=(MAO,))
    script = env.root / "override" / "scopeprobe.py"
    script.parent.mkdir()
    shutil.copy2(env.wrapper("scopeprobe"), script)
    env.plan(extra=["--probe-cmd", f"scopeprobe=python {script}"])
    plan = load(env.run / "plan.json") or {}
    check((plan.get("options") or {}).get("probeCmd", {}).get("scopeprobe") == f"python {script}",
          "1478 fixture: the plan's override is the copied script")
    both_verbs(env, "1478 override", ["scopeprobe", "scopeprobe.py", ACTION],
               lambda: script.write_bytes(script.read_bytes() + b"\n# a different program, the same command text\n"))


def case_1479(tmp):
    env = Env(tmp / "1479", packages=["scopeprobe", "switchprobe"], select=(MAO, BOS))
    env.plan()

    def interrupt(call, jobs):
        if any("switchprobe" in " ".join(map(str, j.get("argv") or [])) for j in jobs):
            raise RuntimeError("simulated interruption while a child runs")
        return call(jobs)
    rc, out, err = env.inproc(["run", "--run", env.run], hook=interrupt)
    check(status(env.run, MAO, "baseline") == "completed" and status(env.run, BOS, "baseline") == "running",
          f"1479 fixture: one completed and one running entry ({status(env.run, MAO, 'baseline')}, "
          f"{status(env.run, BOS, 'baseline')})")
    touch(pkg(env.h, "scopeprobe") / "scopeprobe.dll")
    r = env.copy("1479-rebuild")
    refused_before_work(env, r, "resume", "1479 rebuild", ["scopeprobe", "scopeprobe.dll", ACTION], rebuild=True)


def completed_run(tmp: Path, name: str, packages=("scopeprobe", "navownprobe")) -> Env:
    env = sweep_env(tmp, name, packages=packages)
    env.plan()
    rc, out, err = env.inproc(["run", "--run", env.run])
    check(rc == 0, f"{name} fixture: run exits 0 (got {rc!r}; {err[-300:]!r})")
    check(status(env.run, NAV, CAND, "diff") == "completed" and status(env.run, MAO, "baseline") == "completed",
          f"{name} fixture: the sweep diff and {MAO} completed")
    return env


def craft_role(rc_doc: dict, role: str, program: str | None, digest) -> None:
    progs = rc_doc.setdefault("consumed", {}).setdefault("programs", {})
    row = progs.setdefault(role, {})
    if program is not None:
        row.setdefault("program", program)
    row["digest"] = digest


def disp_of(run: Path) -> dict:
    return load(Path(run) / "disposition.json") or {}


def defect_kinds(disp: dict) -> set:
    return {d.get("kind") for d in ((disp.get("evaluation") or {}).get("integrity") or {}).get("defects") or []}


def claimed(disp: dict, cid: str, aid: str, part="any") -> bool:
    ev = disp.get("evaluation") or {}
    rows = (ev.get("findings") or []) + (ev.get("completedNullChecks") or []) + (ev.get("completedCatalogues") or [])
    return any(r.get("checkId") == cid and r.get("armId") == aid and (part == "any" or r.get("part") == part)
               for r in rows)


def reseal_bundle(env: Env, d: Path) -> None:
    """Make an edited bundle self-consistent again: plan seal, the disposition's derivedFrom, the manifest's hashes."""
    mod = env._module()
    (d / "plan.sha256").write_text(sha(d / "plan.json") + "\n", encoding="utf-8")
    disp = load(d / "disposition.json")
    disp["derivedFrom"] = mod.derived_from(d, mod.read_receipts(d))
    save(d / "disposition.json", disp)
    man = load(d / "manifest.json")
    for e in man.get("evidence") or []:
        p = d / e["path"]
        if p.is_file():
            e["sha256"], e["bytes"] = sha(p), p.stat().st_size
    save(d / "manifest.json", man)


def case_1480(tmp):
    env = completed_run(tmp, "1480")
    rc, out, err = env.inproc(["finalize", "--run", env.run])
    check(rc == 0, f"1480 fixture: finalize of the untouched run exits 0 (got {rc!r})")
    rc, out, err = env.inproc(["bundle", "--run", env.run, "--out", env.root / "bundle-good", "--no-names"])
    check(rc == 0, f"1480 fixture: bundle of the untouched run exits 0 (got {rc!r}; {err[-300:]!r})")
    arms = [("emitter", MAO, "baseline", None, "probe:scopeprobe"),
            ("comparator", NAV, CAND, "diff", "helper:conflictdiff"),
            ("controller", MAO, "baseline", None, "controller")]
    for role, cid, aid, part, program in arms:
        r = env.copy(f"1480-{role}")
        k = f"{cid}__{aid}" + (f"__{part}" if part else "")
        doc = load(r / "receipts" / f"{k}.json")
        craft_role(doc, role, program, "folder:" + "e" * 64)
        save(r / "receipts" / f"{k}.json", doc)
        rc, out, err = env.inproc(["finalize", "--run", r])
        disp = disp_of(r)
        label = f"1480 {role}"
        check(rc == 3, f"{label}: finalize exits 3 (got {rc!r})")
        check("tool-identity-mismatch" in defect_kinds(disp),
              f"{label}: the disposition names a tool-identity-mismatch defect (kinds {sorted(map(str, defect_kinds(disp)))})")
        # info only: this fixture's evaluation is incomplete anyway (mcp-only checks), so this line cannot discriminate
        check((disp.get("evaluation") or {}).get("complete") is False, f"{label}: evaluation.complete is false (info)")
        miss = [m for m in (disp.get("evaluation") or {}).get("missing") or []
                if m.get("checkId") == cid and m.get("armId") == aid and m.get("part") == part]
        check(any(m.get("status") == f"tool-identity-mismatch({role})" for m in miss),
              f"{label}: evaluation.missing names {k} as tool-identity-mismatch({role}) "
              f"(got {[m.get('status') for m in miss]})")
        check(not claimed(disp, cid, aid, part), f"{label}: {k} is credited nowhere")
        if role != "comparator":
            check(not claimed(disp, NAV, "baseline"), f"{label}: {NAV} on the baseline, which rests on it, is credited nowhere")
        rc, out, err = env.inproc(["bundle", "--run", r, "--out", env.root / f"bundle-{role}", "--no-names"])
        check(rc == 3, f"{label}: bundle exits 3 (got {rc!r})")
    # the crafted transferred copy: a valid bundle with one receipt's identity changed and every hash made consistent
    b = env.copy("1480-crafted-bundle", env.root / "bundle-good")
    doc = load(b / "receipts" / f"{MAO}__baseline.json")
    craft_role(doc, "emitter", "probe:scopeprobe", "folder:" + "e" * 64)
    save(b / "receipts" / f"{MAO}__baseline.json", doc)
    reseal_bundle(env, b)
    hidden = env.root / "harness-hidden"
    env.h.rename(hidden)
    try:
        rc, out, err = env.inproc(["verify-bundle", b])
    finally:
        hidden.rename(env.h)
    v = load(b / "verify.json") or {}
    kinds = {d.get("kind") for d in ((v.get("bundle") or {}).get("defects") or [])}
    check(rc == 3, f"1480 crafted bundle: verify-bundle exits 3 with no tool present (got {rc!r})")
    check("tool-identity-mismatch" in kinds, f"1480 crafted bundle: a tool-identity-mismatch defect (kinds {sorted(map(str, kinds))})")


def case_1481(tmp):
    env = completed_run(tmp, "1481")
    touch(pkg(env.h, "scopeprobe") / "scopeprobe.dll")
    rc, out, err = env.inproc(["finalize", "--run", env.run])
    disp = disp_of(env.run)
    check(rc == 0, f"1481 changed tools: finalize exits 0 (got {rc!r}; {err[-300:]!r})")
    check(((disp.get("evaluation") or {}).get("toolIdentity") or {}).get("guarantee") == "verified",
          f"1481 changed tools: the disposition says the tool identity is verified "
          f"(got {(disp.get('evaluation') or {}).get('toolIdentity')!r})")
    check("tool-identity" not in str(disp.get("evaluation", {}).get("integrity")), "1481: no identity defect")
    r = env.copy("1481-resume")
    refused_before_work(env, r, "resume", "1481 changed tools", ["scopeprobe", "scopeprobe.dll", ACTION])
    shutil.rmtree(env.h)
    rc, out, err = env.inproc(["finalize", "--run", env.run])
    check(rc == 0, f"1481 deleted tools: finalize exits 0 (got {rc!r})")
    rc, out, err = env.inproc(["bundle", "--run", env.run, "--out", env.root / "bundle", "--no-names"])
    check(rc == 0, f"1481 deleted tools: bundle exits 0 (got {rc!r}; {err[-300:]!r})")
    rc, out, err = env.inproc(["verify-bundle", env.root / "bundle"])
    v = load(env.root / "bundle" / "verify.json") or {}
    check(rc == 0, f"1481 deleted tools: verify-bundle exits 0 (got {rc!r})")
    check((v.get("toolIdentity") or {}).get("guarantee") == "verified",
          f"1481: verify.json says verified (got {v.get('toolIdentity')!r})")


def case_1482(tmp):
    env = completed_run(tmp, "1482")
    rc, out, err = env.inproc(["finalize", "--run", env.run])
    rc, out, err = env.inproc(["bundle", "--run", env.run, "--out", env.root / "bundle", "--no-names"])
    check(rc == 0, f"1482 fixture: bundle exits 0 (got {rc!r})")
    r = env.copy("1482-run-copy")
    before = {k: v for k, v in snap(r).items() if evidence_file(k)}
    next_second(env._module())
    rc, out, err = env.inproc(["run", "--run", r])
    check(rc == 0 and not env.jobs, f"1482: the copied RUN reuses with no child (exit {rc!r}, jobs {env.jobs[:4]})")
    after = {k: v for k, v in snap(r).items() if evidence_file(k)}
    check(after == before, f"1482: the copy's receipts and evidence unchanged ({snap_diff(before, after)})")
    rc, out, err = env.inproc(["verify-bundle", env.root / "bundle"])
    v1 = load(env.root / "bundle" / "verify.json") or {}
    moved = tmp / "1482-elsewhere" / "bundle"
    moved.parent.mkdir()
    shutil.move(str(env.root / "bundle"), str(moved))
    for p in (env.run, env.h):
        p.rename(p.with_name(p.name + "-hidden"))
    import bundleenrich  # noqa: F401  (imported before reads are watched)
    seen: list[str] = []
    real = builtins.open

    def watch(file, *a, **k):
        if isinstance(file, (str, os.PathLike)):
            seen.append(os.path.abspath(os.fspath(file)))
        return real(file, *a, **k)
    builtins.open, io.open = watch, watch
    try:
        rc, out, err = env.inproc(["verify-bundle", moved])
    finally:
        builtins.open, io.open = real, real
    v2 = load(moved / "verify.json") or {}
    check(rc == 0, f"1482: the relocated bundle verifies (exit {rc!r})")
    check((v1.get("bundle") or {}).get("complete") == (v2.get("bundle") or {}).get("complete")
          and (v1.get("bundle") or {}).get("verified") == (v2.get("bundle") or {}).get("verified"),
          "1482: the relocated bundle verifies identically")
    root = os.path.normcase(os.path.abspath(str(moved)))
    outside = sorted({p for p in seen if not os.path.normcase(p).startswith(root)})
    check(not outside, f"1482: verify-bundle read nothing outside the bundle ({outside[:3]})")


def case_1483(tmp):
    import ordersweep as O
    root = tmp / "1483"
    a, b = root / "a", root / "b"
    for h in (a, b):
        for rel, data in pkg_files("fakeprobe").items():
            f = pkg(h, "fakeprobe") / rel
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_bytes(data)
    k = lambda h: O.program_key(h, "fakeprobe", {})  # noqa: E731
    check(k(a) == k(b), "1483: the same package at two roots has one identity")
    touch(pkg(b, "fakeprobe") / "fakeprobe.pdb")
    os.utime(pkg(b, "fakeprobe") / "fakeprobe.dll", (1_000_000_000, 1_000_000_000))
    (pkg(b, "fakeprobe") / "shots").mkdir()
    (pkg(b, "fakeprobe") / "shots" / "view.png").write_bytes(b"a picture written at run time")
    check(k(a) == k(b), "1483: symbols, timestamps and run-time subfolders do not change the identity")
    (pkg(b, "fakeprobe") / "notes.txt").write_bytes(b"a stray top-level file")
    check(k(a) != k(b), "1483: a stray top-level non-PDB file is part of the identity")
    env = Env(root / "case", packages=["scopeprobe"])
    env.plan()
    d = pkg(env.h, "scopeprobe")
    tmpcopy = root / "replacement"
    shutil.copytree(d, tmpcopy)
    shutil.rmtree(d)
    shutil.copytree(tmpcopy, d, copy_function=shutil.copy)        # identical bytes, new timestamps
    r = env.copy("1483-identical")
    rc, out, err = env.inproc(["run", "--run", r])
    check(rc == 0 and any(MAO in j for j in env.jobs),
          f"1483: an identical replacement runs without a refusal (exit {rc!r}; {err[-200:]!r})")
    gone = env.copy("1483-root-gone")
    shutil.rmtree(env.h / "scopeprobe")
    refused_before_work(env, gone, "resume", "1483 root gone", ["scopeprobe", ACTION])


def case_1484(tmp):
    env = Env(tmp / "1484", packages=["scopeprobe", "switchprobe", "bosprobe"], select=(MAO, BOS, NOTAPP),
              extra=["--exclude", f"{BOS}=not part of this evaluation"])
    plan = env.plan() or {}
    row = next((c for c in plan.get("checks") or [] if c.get("checkId") == NOTAPP), {})
    check(all(a.get("status") == "not-applicable" for a in row.get("arms") or [{}]),
          f"1484 fixture: {NOTAPP} is not applicable here")
    env.save_harness()
    touch(pkg(env.h, "switchprobe") / "switchprobe.dll")
    touch(pkg(env.h, "bosprobe") / "bosprobe.dll")
    r = env.copy("1484-excluded")
    rc, out, err = env.inproc(["run", "--run", r])
    check(rc == 0 and any(MAO in j for j in env.jobs),
          f"1484: a changed excluded tool and a changed not-applicable tool are no refusal (exit {rc!r}; {err[-200:]!r})")
    env.restore_harness()
    both_verbs(env, "1484 included", ["scopeprobe", "scopeprobe.dll", ACTION],
               lambda: touch(pkg(env.h, "scopeprobe") / "scopeprobe.dll"))


def case_1485(tmp):
    env = Env(tmp / "1485", packages=["scopeprobe"])
    plan = env.plan() or {}
    # legacy: a plan without the identity contract
    leg = env.copy("1485-legacy")
    doc = load(leg / "plan.json")
    doc.pop("toolIdentity", None)
    save(leg / "plan.json", doc)
    reseal(leg)
    for verb in ("run", "resume"):
        refused_before_work(env, env.copy(f"1485-legacy-{verb}", leg), verb, "1485 legacy plan",
                            ["plan-identity-outdated", ACTION])
    # partial identity: the contract, without the program this plan launches
    part = env.copy("1485-partial")
    doc = load(part / "plan.json")
    progs = (doc.get("toolIdentity") or {}).get("programs") or {}
    check("probe:scopeprobe" in progs, "1485 partial: the plan records probe:scopeprobe under toolIdentity.programs")
    progs.pop("probe:scopeprobe", None)
    save(part / "plan.json", doc)
    reseal(part)
    refused_before_work(env, part, "run", "1485 partial identity", ["tool-identity-missing", "scopeprobe"])
    # hashing fails
    unr = env.copy("1485-unreadable")
    refused_before_work(env, unr, "run", "1485 unreadable", ["tool-unreadable", "scopeprobe.dll"],
                        open_guard=pkg(env.h, "scopeprobe") / "scopeprobe.dll")
    # F3: absent at plan and at run: no refusal, blocked(tool-missing), nothing accepted
    ab = Env(tmp / "1485-absent", packages=["scopeprobe"], select=(MAO, BOS), absent=["switchprobe"])
    ab.plan()
    rc, out, err = ab.inproc(["run", "--run", ab.run])
    check(rc == 0, f"1485 absent: the run exits 0 (got {rc!r}; {err[-300:]!r})")
    check(status(ab.run, BOS, "baseline") == "blocked(tool-missing)",
          f"1485 absent: {BOS} is blocked(tool-missing) (got {status(ab.run, BOS, 'baseline')})")
    check(not (receipt(ab.run, BOS, "baseline").get("validation") or {}).get("accepted"),
          f"1485 absent: no {BOS} receipt is accepted")
    check(not any("switchprobe" in j for j in ab.jobs), "1485 absent: no switchprobe child")
    # F3: a crafted accepted receipt with an absent or null role digest never compares equal
    full = completed_run(tmp, "1485-crafted")
    for label, edit_receipt, edit_plan in (
            ("null digest", lambda d: craft_role(d, "emitter", "probe:scopeprobe", None), None),
            ("no programs block", lambda d: d.get("consumed", {}).pop("programs", None), None),
            ("absent in plan and receipt", lambda d: craft_role(d, "emitter", "probe:scopeprobe", None),
             lambda p: p.setdefault("toolIdentity", {"contract": 1, "programs": {}}).setdefault("programs", {})
             .update({"probe:scopeprobe": {"kind": "package", "state": "absent", "digest": None}}))):
        r = full.copy(f"1485-crafted-{label.replace(' ', '-')}")
        doc = load(r / "receipts" / f"{MAO}__baseline.json")
        edit_receipt(doc)
        save(r / "receipts" / f"{MAO}__baseline.json", doc)
        if edit_plan:
            p = load(r / "plan.json")
            edit_plan(p)
            save(r / "plan.json", p)
            reseal(r)
        rc, out, err = full.inproc(["finalize", "--run", r])
        kinds = defect_kinds(disp_of(r))
        check(rc == 3, f"1485 crafted {label}: finalize exits 3 (got {rc!r})")
        check(bool(kinds & {"tool-identity-missing", "tool-identity-mismatch"}),
              f"1485 crafted {label}: an identity defect, never equality (kinds {sorted(map(str, kinds))})")
    # legacy bundle: a bundle whose plan and receipts carry no identity verifies under the legacy guarantee only
    rc, out, err = full.inproc(["finalize", "--run", full.run])
    rc, out, err = full.inproc(["bundle", "--run", full.run, "--out", full.root / "bundle", "--no-names"])
    lb = full.copy("1485-legacy-bundle", full.root / "bundle")
    p = load(lb / "plan.json")
    p.pop("toolIdentity", None)
    save(lb / "plan.json", p)
    for f in (lb / "receipts").glob("*.json"):
        d = load(f)
        (d.get("consumed") or {}).pop("programs", None)
        save(f, d)
    disp = load(lb / "disposition.json")
    (disp.get("evaluation") or {}).pop("toolIdentity", None)
    save(lb / "disposition.json", disp)
    reseal_bundle(full, lb)
    rc, out, err = full.inproc(["verify-bundle", lb])
    v = load(lb / "verify.json") or {}
    check(rc == 0, f"1485 legacy bundle: verifies under its legacy guarantee (exit {rc!r})")
    check((v.get("toolIdentity") or {}).get("guarantee") == "legacy",
          f"1485 legacy bundle: verify.json says legacy, never verified (got {v.get('toolIdentity')!r})")


def case_1486(tmp):
    env = completed_run(tmp, "1486")
    ids = {k: (load(env.run / "receipts" / f"{k}.json") or {}).get("receiptId") for k in entries(env.run)}
    before = {k: v for k, v in snap(env.run).items() if evidence_file(k)}
    for verb in ("run", "resume"):
        next_second(env._module())
        rc, out, err = env.inproc([verb, "--run", env.run])
        check(rc == 0 and not env.jobs, f"1486 {verb}: exit 0 and no child (exit {rc!r}, jobs {env.jobs[:4]})")
    after = {k: v for k, v in snap(env.run).items() if evidence_file(k)}
    check(after == before, f"1486: receipts and evidence bytes unchanged ({snap_diff(before, after)})")
    ids2 = {k: (load(env.run / "receipts" / f"{k}.json") or {}).get("receiptId") for k in entries(env.run)}
    check(ids == ids2, "1486: every receipt id is stable")
    check(all(e.get("status") in ("reused", "completed") for e in entries(env.run).values()
              if e.get("checkId") in (MAO, NAV)), "1486: the measured entries read reused")


def case_1487(tmp):
    # (a) a change between two launches: the second is not started
    env = Env(tmp / "1487a", packages=["scopeprobe", "switchprobe"], select=(MAO, BOS))
    env.plan()
    fired = []

    def change_b_after_first(call, jobs):
        res = call(jobs)
        if not fired and any(MAO in str(j.get("id")) for j in jobs):
            fired.append(1)
            touch(pkg(env.h, "switchprobe") / "switchprobe.dll")
        return res
    rc, out, err = env.inproc(["run", "--run", env.run], hook=change_b_after_first)
    check(rc == 2, f"1487a: the run stops with exit 2 (got {rc!r})")
    check(not any("switchprobe" in j or BOS in j for j in env.jobs), f"1487a: no {BOS} child starts (jobs {env.jobs})")
    check(status(env.run, MAO, "baseline") == "completed"
          and (receipt(env.run, MAO, "baseline").get("validation") or {}).get("accepted") is True,
          f"1487a: the earlier {MAO} result stays accepted")
    check("switchprobe" in (err + out), "1487a: the stop names switchprobe")
    # (b) a change during a child: its result is not accepted; earlier evidence intact
    env = Env(tmp / "1487b", packages=["scopeprobe", "switchprobe"], select=(MAO, BOS))
    env.plan()
    first_arm_completed(env, env.run)
    mao_bytes = (env.run / "receipts" / f"{MAO}__baseline.json").read_bytes()

    def change_own_during(call, jobs):
        res = call(jobs)
        if any(BOS in str(j.get("id")) for j in jobs):
            touch(pkg(env.h, "switchprobe") / "switchprobe.dll")
        return res
    rc, out, err = env.inproc(["resume", "--run", env.run], hook=change_own_during)
    st = status(env.run, BOS, "baseline")
    check(rc == 2, f"1487b: the resume stops with exit 2 (got {rc!r})")
    check(st.startswith("rejected(tool-changed"), f"1487b: the in-flight result is rejected(tool-changed) (got {st})")
    check(status(env.run, BOS, CAND) not in ("completed", "reused"),
          f"1487b: the next {BOS} arm is not credited (got {status(env.run, BOS, CAND)})")
    check((env.run / "receipts" / f"{MAO}__baseline.json").read_bytes() == mao_bytes,
          f"1487b: the earlier {MAO} receipt is byte-for-byte intact")
    # (c) the comparator changes during the sweep: diffs rejected, emits kept
    env = sweep_env(tmp, "1487c")
    env.plan()

    def change_comparator_during(call, jobs):
        res = call(jobs)
        if any(str(j.get("id")) == "ordersweep" for j in jobs):
            touch(pkg(env.h, "conflictdiff") / "conflictdiff.dll")
        return res
    rc, out, err = env.inproc(["run", "--run", env.run], hook=change_comparator_during)
    check(rc == 2, f"1487c: the run stops with exit 2 (got {rc!r})")
    check(status(env.run, NAV, CAND, "diff").startswith("rejected(tool-changed"),
          f"1487c: the diff is rejected(tool-changed) (got {status(env.run, NAV, CAND, 'diff')})")
    check(status(env.run, NAV, CAND, "arm") == "completed"
          and status(env.run, NAV, "baseline", "arm") == "completed",
          f"1487c: the emits measured by the unchanged probe stay completed "
          f"({status(env.run, NAV, 'baseline', 'arm')}, {status(env.run, NAV, CAND, 'arm')})")


def case_1488(tmp):
    import ordersweep as O
    script = tmp / "1488" / "fake.py"
    script.parent.mkdir(parents=True)
    script.write_text("print('one program')\n", encoding="utf-8")
    text = {"fakeprobe": f"python {script}"}
    k1 = O.program_key(Path("."), "fakeprobe", text)
    check(O.program_key(Path("."), "fakeprobe", text) == k1, "1488: the same override and script give one key")
    script.write_text("print('another program')\n", encoding="utf-8")
    check(O.program_key(Path("."), "fakeprobe", text) != k1, "1488: the same command text over a changed script is another key")


def case_1489(tmp):
    env = completed_run(tmp, "1489")
    plan = load(env.run / "plan.json") or {}
    progs = (plan.get("toolIdentity") or {}).get("programs") or {}

    def bound(rc_doc, role):
        return ((rc_doc.get("consumed") or {}).get("programs") or {}).get(role) or {}
    mao = receipt(env.run, MAO, "baseline")
    arm = receipt(env.run, NAV, CAND, "arm")
    diff = receipt(env.run, NAV, CAND, "diff")
    want = lambda p: (progs.get(p) or {}).get("digest")  # noqa: E731
    check(want("probe:scopeprobe") and bound(mao, "emitter").get("digest") == want("probe:scopeprobe"),
          "1489: a generic receipt binds its emitter's measured identity, equal to the plan's")
    check(want("controller") and bound(mao, "controller").get("digest") == want("controller"),
          "1489: and the controller's")
    check(want("probe:navownprobe") and bound(arm, "emitter").get("digest") == want("probe:navownprobe"),
          "1489: a swept arm binds the swept probe as its emitter")
    check(want("helper:conflictdiff") and bound(diff, "comparator").get("digest") == want("helper:conflictdiff")
          and not bound(diff, "emitter"), "1489: a diff binds conflictdiff as its comparator and no emitter")
    dll = sha(pkg(env.h, "scopeprobe") / "scopeprobe.dll")
    check(((mao.get("consumed") or {}).get("tool") or {}).get("dllSha256") == dll,
          "1489: consumed.tool keeps the measured dll hash")


def case_1490(tmp):
    ctl = tmp / "1490" / "controller"
    evalrun = controller_copy(ctl)
    env = Env(tmp / "1490" / "case", select=(MAO, DANGLING), evalrun=evalrun)
    plan = env.plan() or {}
    row = next((c for c in plan.get("checks") or [] if c.get("checkId") == DANGLING), {})
    check(any(a.get("status") == "mcp-only" for a in row.get("arms") or []),
          f"1490 fixture: {DANGLING} waits for an import ({[a.get('status') for a in row.get('arms') or []]})")
    p = env.cli(["run", "--run", env.run])
    check(p.returncode == 0, f"1490 fixture: the run exits 0 (got {p.returncode}; {p.stderr[-300:]!r})")
    arm = next(a for a in plan["arms"] if a["armId"] == "baseline")
    manifest = IMP.make_import(env.root / "import", plan, DANGLING, arm, 5, {})
    ok = env.copy("1490-control")
    p = env.cli(["import-results", "--run", ok, "--file", manifest])
    check(p.returncode == 0, f"1490 control: the import is accepted with the controller unchanged "
                             f"(exit {p.returncode}; {p.stderr[-300:]!r})")
    f = ctl / "_geometry-harness" / "evalregistry.py"
    orig = f.read_bytes()
    try:
        f.write_bytes(orig + b"\n# a change after the plan was made\n")
        r = env.copy("1490-changed")
        before = snap(r)
        p = env.cli(["import-results", "--run", r, "--file", manifest])
        msg = (p.stderr + p.stdout).replace("\n", " ")
        check(p.returncode == 2, f"1490: import-results with a changed controller exits 2 (got {p.returncode})")
        check("evalregistry.py" in msg and ACTION in msg, f"1490: the refusal names evalregistry.py (said {msg[-300:]!r})")
        after = snap(r)
        check(after == before, f"1490: RUN bytes unchanged ({snap_diff(before, after)})")
    finally:
        f.write_bytes(orig)


def receipt_docs(run: Path) -> dict:
    return {p.stem: load(p) or {} for p in sorted((Path(run) / "receipts").glob("*.json"))}


def changed_fields(a: dict, b: dict, pre: str = "") -> list[str]:
    out = []
    for k in sorted(set(a) | set(b)):
        if a.get(k) == b.get(k):
            continue
        if isinstance(a.get(k), dict) and isinstance(b.get(k), dict):
            out += changed_fields(a[k], b[k], pre + k + ".")
        else:
            out.append(pre + k)
    return out


def next_second(mod) -> None:
    """Wait until the controller's own clock (whole seconds) reads a later value, so a time-stamped field can differ."""
    t = mod.utcnow()
    while mod.utcnow() == t:
        time.sleep(0.05)


ACCOUNTING_KINDS = ("manual", "unsupported", "skipped-user", "not-applicable", "mcp-only")


def case_1500(tmp):
    env = completed_run(tmp, "1500")
    mod = env._module()
    docs = receipt_docs(env.run)
    acct = {k: d for k, d in docs.items() if not (d.get("validation") or {}).get("accepted")}
    kinds = {s: sum(1 for d in acct.values() if (d.get("result") or {}).get("execution") == s) for s in ACCOUNTING_KINDS}
    check(all(kinds.values()), f"1500 fixture: accounting receipts of every kind are present ({kinds})")
    before = {k: v for k, v in snap(env.run).items() if k.startswith("receipts/")}
    print(f"  receipts: {len(before)} ({len(acct)} accounting)")
    for verb in ("run", "resume"):
        next_second(mod)
        rc, out, err = env.inproc([verb, "--run", env.run])
        check(rc == 0 and not env.jobs, f"1500 {verb}: exit 0 and no child (exit {rc!r}, jobs {env.jobs[:4]})")
        after = {k: v for k, v in snap(env.run).items() if k.startswith("receipts/")}
        now = receipt_docs(env.run)
        moved = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
        why = sorted({f for k in moved for f in changed_fields(docs.get(Path(k).stem) or {}, now.get(Path(k).stem) or {})})
        check(not moved, f"1500 {verb}: every receipt byte-identical, accounting included ({len(moved)} changed: "
                         f"{', '.join(Path(k).stem for k in moved[:3])}{' ...' if len(moved) > 3 else ''}; fields {why})")
        check({k: d.get("receiptId") for k, d in now.items()} == {k: d.get("receiptId") for k, d in docs.items()},
              f"1500 {verb}: every receipt id is stable")
        check(all(status(env.run, d["checkId"], d["armId"], d.get("part")) == (d.get("result") or {}).get("execution")
                  for d in acct.values()), f"1500 {verb}: each accounting entry still reads its receipt's status")


def case_1501(tmp):
    env = sweep_env(tmp, "1501")
    plan = env.plan() or {}
    mod = env._module()
    arm = next(a for a in plan["arms"] if a["armId"] == CAND)
    src = Path(arm["candidatePath"])
    original = src.read_bytes()
    keys = (f"{MAO}__{CAND}", f"{NAV}__{CAND}")

    def rid(k):
        return (load(env.run / "receipts" / f"{k}.json") or {}).get("receiptId")

    def execution(k):
        return ((load(env.run / "receipts" / f"{k}.json") or {}).get("result") or {}).get("execution")
    try:
        src.write_bytes(original + b"\n ")              # the live candidate changes after planning
        rc, out, err = env.inproc(["run", "--run", env.run])
        check(rc == 0, f"1501 changed: run exits 0 (got {rc!r}; {err[-300:]!r})")
        changed = {k: rid(k) for k in keys}
        check(all(execution(k) == "blocked(candidate-changed)" for k in keys),
              f"1501 changed: both rows read blocked(candidate-changed) ({[execution(k) for k in keys]})")
        src.unlink()                                     # then it is gone
        next_second(mod)
        rc, out, err = env.inproc(["run", "--run", env.run])
        check(rc == 0, f"1501 gone: run exits 0 (got {rc!r}; {err[-300:]!r})")
        nav = f"{NAV}__{CAND}"
        check(execution(nav) == "blocked(candidate-missing)" and status(env.run, NAV, CAND) == execution(nav),
              f"1501 gone: the swept row's receipt and entry read blocked(candidate-missing) ({execution(nav)})")
        check(rid(nav) != changed[nav], f"1501 gone: the swept row's receipt is a new one ({changed[nav]} -> {rid(nav)})")
        mao = f"{MAO}__{CAND}"
        note = str(((load(env.run / "receipts" / f"{mao}.json") or {}).get("result") or {}).get("note"))
        check(rid(mao) != changed[mao] and "gone" in note,
              f"1501 gone: {MAO}'s receipt is a new one naming the missing file ({changed[mao]} -> {rid(mao)}; {note[:120]!r})")
    finally:
        src.write_bytes(original)                        # the fixture is shared by later cases
    rc, out, err = env.inproc(["run", "--run", env.run])
    check(rc == 0, f"1501 restored: run exits 0 (got {rc!r}; {err[-300:]!r})")
    done = load(env.run / "receipts" / f"{MAO}__{CAND}.json") or {}
    check((done.get("result") or {}).get("execution") == "completed" and (done.get("validation") or {}).get("accepted"),
          f"1501 restored: {MAO} on {CAND} is measured and accepted ({(done.get('result') or {}).get('execution')})")


def case_1502(tmp):
    env = completed_run(tmp, "1502")
    plan = load(env.run / "plan.json") or {}
    arm = next(a for a in plan["arms"] if a["armId"] == "baseline")
    k = f"{DANGLING}__baseline"
    before = load(env.run / "receipts" / f"{k}.json") or {}
    check((before.get("result") or {}).get("execution") == "mcp-only", f"1502 fixture: {DANGLING} reads mcp-only")
    manifest = IMP.make_import(env.root / "import", plan, DANGLING, arm, 5, {})
    rc, out, err = env.inproc(["import-results", "--run", env.run, "--file", manifest])
    check(rc == 0, f"1502: the import is accepted (exit {rc!r}; {err[-300:]!r})")
    imported = (env.run / "receipts" / f"{k}.json").read_bytes()
    doc = load(env.run / "receipts" / f"{k}.json") or {}
    check(isinstance(doc.get("imported"), dict) and (doc.get("validation") or {}).get("accepted")
          and doc.get("receiptId") != before.get("receiptId"),
          f"1502: the row's receipt is the import's, under a new id ({before.get('receiptId')} -> {doc.get('receiptId')})")
    for verb in ("run", "resume"):
        next_second(env._module())
        rc, out, err = env.inproc([verb, "--run", env.run])
        check(rc == 0, f"1502 {verb}: exit 0 (got {rc!r})")
        check((env.run / "receipts" / f"{k}.json").read_bytes() == imported,
              f"1502 {verb}: the import's receipt is kept byte-identical")
        check(status(env.run, DANGLING, "baseline") in ("reused", "completed"),
              f"1502 {verb}: the entry reads reused ({status(env.run, DANGLING, 'baseline')})")


def case_1503(tmp):
    env = sweep_env(tmp, "1503")
    plan = env.plan() or {}
    mod = env._module()
    arm = next(a for a in plan["arms"] if a["armId"] == CAND)
    src = Path(arm["candidatePath"])
    original = src.read_bytes()
    keys = (f"{MAO}__{CAND}", f"{NAV}__{CAND}")
    try:
        src.write_bytes(original + b"\n ")              # changed after planning, and left changed
        rc, out, err = env.inproc(["run", "--run", env.run])
        check(rc == 0, f"1503 fixture: run exits 0 (got {rc!r})")
        first = {k: (env.run / "receipts" / f"{k}.json").read_bytes() for k in keys}
        for verb in ("run", "resume"):
            next_second(mod)
            rc, out, err = env.inproc([verb, "--run", env.run])
            check(rc == 0 and not env.jobs, f"1503 {verb}: exit 0 and no child (exit {rc!r}, jobs {env.jobs[:4]})")
            same = [k for k in keys if (env.run / "receipts" / f"{k}.json").read_bytes() == first[k]]
            check(len(same) == len(keys), f"1503 {verb}: the blocked(candidate-changed) receipts are byte-identical "
                                          f"({len(same)} of {len(keys)} unchanged; changed: {sorted(set(keys) - set(same))})")
    finally:
        src.write_bytes(original)                        # the fixture is shared by later cases


CASES = {f"{n}": globals()[f"case_{n}"] for n in list(range(1470, 1491)) + [1500, 1501, 1502, 1503]}


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="evalrun-ti-"))
    try:
        T.GEN = tmp / "fixtures"
        gen = subprocess.run([sys.executable, str(T.FIXT / "make_fixtures.py"), "--out", str(T.GEN)],
                             capture_output=True, text=True)
        if gen.returncode != 0:
            print("  FAIL  make_fixtures.py did not run:\n" + gen.stdout + gen.stderr)
            return 1
        os.environ["PYTHONIOENCODING"] = "utf-8"
        try:
            T.use_conflictdiff(tmp)
        except SystemExit as ex:
            print(ex)
            return 1
        print(T.DIFF_SOURCE)
        ran = 0
        for name, fn in CASES.items():
            if want and not name.startswith(want):
                continue
            ran += 1
            print(f"\n=== {name} ===")
            t0 = time.time()
            try:
                fn(tmp)
            except Exception as ex:                                  # a crash is a failure, never a red
                import traceback
                traceback.print_exc()
                check(False, f"{name}: raised {type(ex).__name__}: {ex}")
            print(f"  ({time.time() - t0:.1f}s)")
        if not ran:
            print(f"  FAIL  no case matches {want!r}")
            return 2
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
