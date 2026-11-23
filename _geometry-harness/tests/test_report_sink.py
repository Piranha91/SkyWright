"""The planes report goes to the file as it is serialised, and never half-written under its name.

A campaign report is ~600 MB of indented JSON. Building it as one string first cost a UTF-16 copy
of the whole thing and a UTF-8 copy to write it, both live at the end of the heaviest step the
renderer has. Serialising straight to the stream removes both - but a streamed file exists while it
is still being written, so two things have to hold:

  * the bytes are the SAME bytes, because a reader of a city report scans its text rather than
    decoding it and would notice a separator respelled;
  * a run that fails or is killed mid-report leaves nothing that anything globbing for a report can
    find, and the refusal to replace an existing report still stands.

The second is checked by killing a real process in the middle of a write, not by reasoning about it.

  python _geometry-harness/tests/test_report_sink.py
  VIEWPROBE_EXE=... python _geometry-harness/tests/test_report_sink.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HARNESS = Path(__file__).resolve().parents[1]
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
VP = buildlayout.program("viewprobe")
VP = Path(os.environ.get("VIEWPROBE_EXE", str(VP)))

OK = FAILED = 0


def check(name, cond, detail=""):
    global OK, FAILED
    if cond:
        OK += 1
        print(f"  ok   {name}  {detail}")
    else:
        FAILED += 1
        print(f"  FAIL {name}  {detail}")


def env_for(work):
    e = dict(os.environ)
    e["HOUSECARL_RUNLOG_DIR"] = str(Path(work) / "runlog")
    return e


def main():
    if not VP.exists():
        print(f"FAIL: no viewprobe at {VP}")
        return 1
    work = Path(tempfile.mkdtemp(prefix="hc-report-sink-"))
    try:
        p = subprocess.run([str(VP), "--report-sink-selftest", str(work / "self")],
                           capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=env_for(work))
        # THE GATE: a build without the sink refuses, and nothing below can pass vacuously.
        check("the build has the report-sink self-test entry point", p.returncode == 0,
              f"exit {p.returncode}; {(p.stderr or p.stdout).strip().splitlines()[-1][:160] if (p.stderr or p.stdout).strip() else ''}")
        if p.returncode != 0:
            return 1
        r = json.loads(p.stdout)

        check("a streamed report is byte-for-byte what the string route produced",
              r["sameBytesAsOldRoute"] is True, json.dumps(r["bytes"]))
        check("it still refuses to replace a report that is already there",
              r["refusedToReplace"] is True and "Refusing to replace" in (r["refusalMessage"] or ""),
              str(r["refusalMessage"])[:120])
        check("a write that fails part way through throws", r["failedWriteThrew"] is not None,
              str(r["failedWriteThrew"]))
        check("...and leaves no report under the real name", r["failedWriteLeftAReport"] is False,
              json.dumps(r["filesLeftInDirectory"]))
        check("...and leaves no partial behind either", r["failedWriteLeftAPartial"] is False,
              json.dumps(r["filesLeftInDirectory"]))
        check("only the report that was finished is report-shaped",
              r["reportShapedFilesLeft"] == 1, json.dumps(r["filesLeftInDirectory"]))

        # --- killed mid-write: a real process, really killed ---------------------------------
        kill_dir = work / "kill"
        kill_dir.mkdir()
        target = kill_dir / "killed-planes.json"
        partial = Path(str(target) + r["partialSuffix"])
        proc = subprocess.Popen([str(VP), "--report-sink-kill-arm", str(target), "400000"],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                encoding="utf-8", errors="replace", env=env_for(work))
        grew = False
        deadline = time.time() + 60
        while time.time() < deadline:
            if partial.exists() and partial.stat().st_size > 1_000_000:
                grew = True
                break
            if proc.poll() is not None:
                break
            time.sleep(0.05)
        check("the kill arm was caught with a report part-written", grew,
              f"partial exists={partial.exists()} size={partial.stat().st_size if partial.exists() else 0} "
              f"exited={proc.poll()}")
        proc.kill()
        proc.wait(timeout=30)
        check("a killed run leaves NO file under the report's own name", not target.exists(),
              str(sorted(p.name for p in kill_dir.iterdir())))
        report_shaped = [p.name for p in kill_dir.iterdir() if p.name.endswith("-planes.json")]
        check("...and nothing a reader globbing for a report would find", report_shaped == [],
              str(sorted(p.name for p in kill_dir.iterdir())))
        check("...while what it does leave says it was being written",
              all(p.name.endswith(r["partialSuffix"]) for p in kill_dir.iterdir()),
              str(sorted(p.name for p in kill_dir.iterdir())))
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print(f"\n{OK} ok, {FAILED} failed")
    print("PASS" if FAILED == 0 else "FAILED")
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
