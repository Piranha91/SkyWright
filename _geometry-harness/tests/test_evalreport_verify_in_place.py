"""Known answers for evalreport plan verifying a bundle without copying it or writing into it (C2230-C2234).

    python tests/test_evalreport_verify_in_place.py [--only C2230,C2233] [--harness DIR] [--work DIR]

No game and no renderer. The bundle is a real schema-2 bundle from fixtures/evalreport/make_report_fixtures.py (R1),
verified once in place as a built bundle is, plus a `linked/` folder of hard links to one 16 MiB blob outside it
(128 MiB of names, 16 MiB on disk), the shape of a bundle whose large evidence is hard-linked. `--work` is where the
fixtures go (default: the system temp folder); it needs about 200 MiB.

    C2230  plan with default flags on --bundle and --sibling-bundle leaves every file's bytes and mtime and every
           directory's mtime unchanged in both bundles, adds no file, and writes both verdicts under OUT
    C2231  the same command with --verify-copy (recorded plan commands pass it) still runs, changes neither bundle and
           leaves no OUT/.verify-copy
    C2232  on the linked bundle, with the pipeline's flags, the bytes under OUT when verify-bundle runs stay under 1 MiB
           against 128 MiB linked: no copy in proportion to the bundle
    C2233  a failure mid-verify leaves no large tree and a one-sentence refusal: (i) ENOSPC once 64 MiB is copied, the
           2026-11-14 04:18 failure; (ii) verify-bundle timing out; (iii) verify-bundle dying without a result while
           an earlier complete verdict sits in OUT, which must not be read as this run's
    C2234  runcontrol verify-bundle DIR --out FILE writes the verdict to FILE only: the same bundle verdict as an
           in-place run, DIR unchanged; on a damaged copy exit 3, complete false, the file under mismatched, DIR unchanged
    C2235  every caller checks a bundle with the built-in controller, its one owner: evalreport plan starts
           `runcontrol verify-bundle`, the pipeline's media check is the same command, evalrun.py has no verify-bundle,
           and no harness module starts the Python verb

The controller is HOUSECARL_RUNCONTROL, else this checkout's build (`buildlayout.runcontrol()`).

Each case names the wrong answer it rejects. A crash is a failure, never a skip. Exit 0: every case passed; 1: a case
failed.
"""
from __future__ import annotations

import argparse
import contextlib
import errno
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
from pathlib import Path
from unittest import mock

ap0 = argparse.ArgumentParser()
ap0.add_argument("--harness", default=None)
ap0.add_argument("--work", default=None)
ap0.add_argument("--only", default="")
A0 = ap0.parse_args()
HERE = Path(__file__).resolve().parent
HARNESS = Path(A0.harness).resolve() if A0.harness else (
    HERE.parent if (HERE.parent / "evalreport.py").is_file() else Path("S:/Dev/houseCARL/_geometry-harness"))
TESTS = HARNESS / "tests"
for p_ in (HARNESS, TESTS, TESTS / "fixtures" / "evalreport"):
    sys.path.insert(0, str(p_))
os.environ["PYTHONUTF8"] = "1"
os.environ["PYTHONIOENCODING"] = "utf-8"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

import evalreport as R                                     # noqa: E402
import evalpipeline as P                                   # noqa: E402

EVALREPORT = HARNESS / "evalreport.py"
EVALRUN = HARNESS / "evalrun.py"


def verify_cmd(*args) -> list[str]:
    """`runcontrol verify-bundle ARGS`, the command every caller starts."""
    return [str(P.runcontrol_exe(None)), "verify-bundle", *[str(a) for a in args]]
MIB = 1024 * 1024
BLOB_MIB, LINKS = 16, 8
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "ok", wrong))
    return cond


def load(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def sha(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(MIB), b""):
            h.update(b)
    return h.hexdigest()


def snapshot(root: Path) -> dict:
    """Every file's size, mtime and hash and every directory's mtime, by path relative to root."""
    snap = {".": ("dir", root.stat().st_mtime_ns)}
    for dirpath, dirs, files in os.walk(root):
        d = Path(dirpath)
        for n in dirs:
            snap[(d / n).relative_to(root).as_posix()] = ("dir", (d / n).stat().st_mtime_ns)
        for n in files:
            st = (d / n).stat()
            snap[(d / n).relative_to(root).as_posix()] = ("file", st.st_size, st.st_mtime_ns, sha(d / n))
    return snap


def diff_snap(a: dict, b: dict) -> list[str]:
    out = [f"added {k}" for k in sorted(set(b) - set(a))] + [f"removed {k}" for k in sorted(set(a) - set(b))]
    out += [f"changed {k}" for k in sorted(set(a) & set(b)) if a[k] != b[k]]
    return out


def bytes_under(root: Path) -> int:
    """Bytes of the distinct files under root (a hard link counts once)."""
    seen, total = set(), 0
    if not root.exists():
        return 0
    for dirpath, _, files in os.walk(root):
        for n in files:
            try:
                st = (Path(dirpath) / n).stat()
            except OSError:
                continue
            key = (st.st_dev, st.st_ino)
            if key not in seen:
                seen.add(key)
                total += st.st_size
    return total


def plan_cli(args: list) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(EVALREPORT), "plan"] + [str(x) for x in args],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


def plan_in_process(args: list) -> tuple[int | None, str, str | None]:
    """The real plan in this process: (exit code, stderr, the exception's name if it raised)."""
    err, out = io.StringIO(), io.StringIO()
    code, raised = None, None
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
        try:
            code = R.main(["plan"] + [str(x) for x in args])
        except BaseException as ex:                          # noqa: BLE001 - recorded, then judged by the case
            raised = f"{type(ex).__name__}: {str(ex)[:160]}"
    return code, err.getvalue(), raised


def one_sentence_refusal(stderr: str) -> bool:
    lines = [ln for ln in stderr.splitlines() if ln.startswith("REFUSED:")]
    return len(lines) == 1 and "Traceback" not in stderr


class Fixture:
    def __init__(self, work: Path):
        import make_report_fixtures as MF
        self.work = work
        MF.T.use_conflictdiff(work)
        index = MF.generate(work / "fixtures", "R1")
        self.source = Path(index["R1-intact-incomplete"]["bundle"])
        # a built bundle has been verified in place, so it carries a verify.json of its own
        r = subprocess.run(verify_cmd(self.source),
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0 or not (load(self.source / "verify.json") or {}).get("bundle", {}).get("complete"):
            raise SystemExit(f"the R1 fixture does not verify complete (exit {r.returncode}): {r.stderr[-300:]}")
        self.blob = work / "outside" / "blob.bin"
        self.blob.parent.mkdir(parents=True, exist_ok=True)
        with open(self.blob, "wb") as f:
            for i in range(BLOB_MIB):
                f.write(bytes([i % 251]) * MIB)
        self.n = 0

    def bundle(self, linked: bool = True) -> Path:
        self.n += 1
        d = self.work / f"b{self.n:02d}"
        shutil.copytree(self.source, d)
        if linked:
            (d / "linked").mkdir()
            for i in range(LINKS):
                os.link(self.blob, d / "linked" / f"blob{i + 1}.bin")
        return d

    def out(self, tag: str) -> Path:
        return self.work / f"out-{tag}"


def case_2230(fx: Fixture):
    case = "C2230"
    b, s, o = fx.bundle(), fx.bundle(linked=False), fx.out("2230")
    before_b, before_s = snapshot(b), snapshot(s)
    r = plan_cli(["--bundle", b, "--sibling-bundle", s, "--out", o])
    check(case, r.returncode == 0, "plan with --bundle and --sibling-bundle exits 0", f"exit {r.returncode}: {r.stderr[-300:]}")
    db, ds = diff_snap(before_b, snapshot(b)), diff_snap(before_s, snapshot(s))
    check(case, not db, "plan changes no byte, mtime or file in --bundle", "; ".join(db[:6]))
    check(case, not ds, "plan changes no byte, mtime or file in --sibling-bundle", "; ".join(ds[:6]))
    check(case, ((load(o / "verify.json") or {}).get("bundle") or {}).get("complete") is True,
          "OUT/verify.json carries the bundle's complete verdict")
    check(case, ((load(o / "sibling-verify" / "verify.json") or {}).get("bundle") or {}).get("complete") is True,
          "OUT/sibling-verify/verify.json carries the sibling's complete verdict")


def case_2231(fx: Fixture):
    case = "C2231"
    b, s, o = fx.bundle(), fx.bundle(linked=False), fx.out("2231")
    before_b, before_s = snapshot(b), snapshot(s)
    r = plan_cli(["--bundle", b, "--sibling-bundle", s, "--out", o, "--verify-copy"])
    check(case, r.returncode == 0, "a recorded plan command with --verify-copy still runs and exits 0",
          f"exit {r.returncode}: {r.stderr[-300:]}")
    db, ds = diff_snap(before_b, snapshot(b)), diff_snap(before_s, snapshot(s))
    check(case, not db and not ds, "--verify-copy changes neither bundle", "; ".join((db + ds)[:6]))
    check(case, not (o / ".verify-copy").exists(), "no OUT/.verify-copy is left")


def case_2232(fx: Fixture):
    case = "C2232"
    b, o = fx.bundle(), fx.out("2232")
    seen: list[int] = []
    real_run = subprocess.run
    free0 = shutil.disk_usage(fx.work).free

    def spy(argv, *a, **k):
        if any(str(x) == "verify-bundle" for x in argv):
            seen.append(bytes_under(o))
            seen.append(free0 - shutil.disk_usage(fx.work).free)
        return real_run(argv, *a, **k)

    with mock.patch.object(R.subprocess, "run", spy):
        code, err, raised = plan_in_process(["--bundle", b, "--out", o, "--verify-copy",
                                             "--mode", "concise"])
    check(case, code == 0 and raised is None, "plan with the pipeline's flags completes", f"exit {code}, {raised}, {err[-300:]}")
    check(case, bool(seen), "verify-bundle was invoked")
    if seen:
        print(f"  {case}: bytes under OUT at verify {seen[0]:,}; disk-free drop at verify {seen[1]:,} (informational)")
        check(case, seen[0] < MIB, f"bytes under OUT when verify-bundle runs are under 1 MiB, against "
              f"{LINKS * BLOB_MIB} MiB linked", f"{seen[0]:,} bytes")


def case_2233(fx: Fixture):
    case = "C2233"
    # (i) the disk fills once 64 MiB has been copied, as X: did at 04:18
    b, o = fx.bundle(), fx.out("2233i")
    real_copy2, real_copytree = shutil.copy2, shutil.copytree
    copied = [0]

    # copy2 on Windows goes straight to CopyFile2, and copytree binds copy2 when it is defined, so the fault is put on
    # copy2 and handed to every copytree
    def filling(src, dst, *a, **k):
        if copied[0] >= 64 * MIB:
            raise OSError(errno.ENOSPC, "No space left on device", str(dst))
        r = real_copy2(src, dst, *a, **k)
        copied[0] += Path(src).stat().st_size
        return r

    def filling_tree(src, dst, *a, **k):            # copytree recurses through this name with copy_function third
        if len(a) >= 3:
            a = a[:2] + (filling,) + a[3:]
        else:
            k["copy_function"] = filling
        return real_copytree(src, dst, *a, **k)

    with mock.patch.object(shutil, "copy2", filling), mock.patch.object(shutil, "copytree", filling_tree):
        code, err, raised = plan_in_process(["--bundle", b, "--out", o, "--verify-copy"])
    left = bytes_under(o)
    check(case, not (o / ".verify-copy").exists() and left < MIB,
          "(i) a disk-full failure mid-verify leaves no large tree under OUT", f"{left:,} bytes left, raised {raised}")
    check(case, raised is None, "(i) plan answers rather than raising", str(raised))

    # (ii) verify-bundle times out
    b, o = fx.bundle(), fx.out("2233ii")
    real_run = subprocess.run

    def timing_out(argv, *a, **k):
        if any(str(x) == "verify-bundle" for x in argv):
            raise subprocess.TimeoutExpired(argv, 900)
        return real_run(argv, *a, **k)

    with mock.patch.object(R.subprocess, "run", timing_out):
        code, err, raised = plan_in_process(["--bundle", b, "--out", o, "--verify-copy"])
    left = bytes_under(o)
    check(case, raised is None and code not in (None, 0) and one_sentence_refusal(err),
          "(ii) a timed-out verify is refused in one sentence", f"exit {code}, {raised}, {err[-300:]!r}")
    check(case, not (o / ".verify-copy").exists() and left < MIB, "(ii) it leaves no large tree", f"{left:,} bytes")

    # (iii) verify-bundle dies without a result while an earlier complete verdict sits in OUT
    b, o = fx.bundle(), fx.out("2233iii")
    o.mkdir(parents=True)
    shutil.copyfile(fx.source / "verify.json", o / "verify.json")

    def dying(argv, *a, **k):
        if any(str(x) == "verify-bundle" for x in argv):
            return subprocess.CompletedProcess(argv, 1, "", "killed")
        return real_run(argv, *a, **k)

    with mock.patch.object(R.subprocess, "run", dying):
        code, err, raised = plan_in_process(["--bundle", b, "--out", o])
    check(case, raised is None and code not in (None, 0) and one_sentence_refusal(err),
          "(iii) a verify that wrote no result is refused, never answered by an earlier verdict",
          f"exit {code}, {raised}, {err[-300:]!r}")


def case_2234(fx: Fixture):
    case = "C2234"
    d1, d2 = fx.bundle(linked=False), fx.bundle(linked=False)
    f1 = fx.out("2234") / "v1.json"
    before = snapshot(d1)
    r1 = subprocess.run(verify_cmd(d1, "--out", f1),
                        capture_output=True, text=True, encoding="utf-8", errors="replace")
    r2 = subprocess.run(verify_cmd(d2),
                        capture_output=True, text=True, encoding="utf-8", errors="replace")
    v1, v2 = load(f1) or {}, load(d2 / "verify.json") or {}
    check(case, r1.returncode == 0 and bool(v1), "verify-bundle DIR --out FILE exits 0 and writes FILE",
          f"exit {r1.returncode}: {r1.stderr.strip()[-200:]}")
    check(case, r2.returncode == 0 and v1.get("bundle") == v2.get("bundle"),
          "FILE holds the same bundle verdict as an in-place run on an identical copy")
    d = diff_snap(before, snapshot(d1))
    check(case, not d, "verify-bundle --out leaves DIR unchanged", "; ".join(d[:6]))
    # a damaged copy: one evidence byte changed
    d3 = fx.bundle(linked=False)
    man = load(d3 / "manifest.json") or {}
    target = next((e["path"] for e in reversed(man.get("evidence") or [])
                   if e.get("path") and (d3 / e["path"]).is_file() and (d3 / e["path"]).stat().st_size > 0), None)
    if not check(case, target is not None, "the fixture lists an evidence file to damage"):
        return
    p = d3 / target
    data = bytearray(p.read_bytes())
    data[0] ^= 0x01
    p.write_bytes(bytes(data))
    before3 = snapshot(d3)
    f3 = fx.out("2234") / "v3.json"
    r3 = subprocess.run(verify_cmd(d3, "--out", f3),
                        capture_output=True, text=True, encoding="utf-8", errors="replace")
    b3 = (load(f3) or {}).get("bundle") or {}
    mism = [m.get("path") for m in b3.get("mismatched") or []]
    check(case, r3.returncode == 3 and b3.get("complete") is False and target in mism,
          "a damaged copy exits 3 and FILE says complete false with the file under mismatched",
          f"exit {r3.returncode}, mismatched {mism[:3]}")
    d = diff_snap(before3, snapshot(d3))
    check(case, not d, "verify-bundle --out leaves a damaged DIR unchanged too", "; ".join(d[:6]))


def case_2235(fx: Fixture):
    case = "C2235"
    exe, b, o = P.fwd(P.runcontrol_exe(None)), fx.bundle(linked=False), fx.out("2235")
    seen: list[list[str]] = []
    real_run = subprocess.run

    def spy(argv, *a, **k):
        if any(str(x) == "verify-bundle" for x in argv):
            seen.append([str(x) for x in argv])
        return real_run(argv, *a, **k)

    with mock.patch.object(R.subprocess, "run", spy):
        code, err, raised = plan_in_process(["--bundle", b, "--out", o])
    check(case, code == 0 and raised is None, "plan completes", f"exit {code}, {raised}, {err[-300:]}")
    check(case, len(seen) == 1 and seen[0] == [exe, "verify-bundle", P.fwd(b), "--out", str(o / "verify.json")],
          "evalreport plan checks the bundle with `runcontrol verify-bundle DIR --out OUT/verify.json`", f"started {seen}")
    check(case, not any(x.endswith("evalrun.py") or x == sys.executable for argv in seen for x in argv),
          "evalreport plan starts no Python for the bundle check", f"started {seen}")
    build = getattr(P, "verify_bundle_argv", None)
    m = build(None, b) if build else None
    check(case, m == [exe, "verify-bundle", P.fwd(b)], "the pipeline's media check is `runcontrol verify-bundle DIR`",
          f"{m}" if build else "evalpipeline builds no verify_bundle_argv")
    r = subprocess.run([sys.executable, str(EVALRUN), "verify-bundle", str(b)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    check(case, r.returncode == 2 and "invalid choice" in r.stderr,
          "evalrun.py has no verify-bundle: Python's copy of the check is gone", f"exit {r.returncode}: {r.stderr[-200:]}")
    # a module that still builds `python evalrun.py verify-bundle` would be caught here before it ran
    starts = [f"{f.name}:{i}" for f in sorted(HARNESS.glob("*.py")) if f.name != "evalrun.py"
              for i, ln in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1)
              if "verify-bundle" in ln and ("evalrun" in ln.lower() or "EVALRUN" in ln)]
    check(case, not starts, "no harness module names evalrun beside verify-bundle", "; ".join(starts[:6]))


CASES = {"C2230": case_2230, "C2231": case_2231, "C2232": case_2232, "C2233": case_2233, "C2234": case_2234,
         "C2235": case_2235}


def main() -> int:
    only = {x.strip() for x in A0.only.split(",") if x.strip()}
    base = Path(A0.work) if A0.work else Path(tempfile.gettempdir())
    base.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="hc-verify-in-place-", dir=base))
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(work / "runlog"))
    try:
        t0 = time.time()
        try:
            fx = Fixture(work)
        except SystemExit as ex:
            print(f"fixture failed: {ex}")
            return 1
        print(f"fixture built in {time.time() - t0:.1f}s under {work}")
        for name, fn in CASES.items():
            if only and name not in only:
                continue
            t0 = time.time()
            try:
                fn(fx)
            except Exception as ex:                          # a crash is a failure, never a skip
                check(name, False, f"{name} raised {type(ex).__name__}: {ex}", traceback.format_exc()[-600:])
            print(f"  ({name} {time.time() - t0:.1f}s)")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    for case, state, what in RESULTS:
        print(f"{state:>4}  {case}  {what}")
    red = sorted(set(FAILS))
    print(f"\n{len(red)} case(s) red: {', '.join(red)}" if red else "\nall cases passed")
    return 1 if red else 0


if __name__ == "__main__":
    sys.exit(main())
