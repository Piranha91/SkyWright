"""Known answers: viewprobe's point queries answer or refuse, never print nothing (C1940-C1945).

    python tests/test_point_queries_answer_or_refuse.py [--vp VIEWPROBE.exe] [--keep DIR]

The REAL viewprobe on CfFixture.esp (viewprobe/CaptureFixture.cs), laid out over a Skyrim SE game's own archives
(tests/capture_fixture_lab.py). Cell CfCutaway has a navmesh floor under (0,0). Without game data
(HOUSECARL_FIXTURE_GAME or `fixtureGame` in the harness config) the cases are NOT RUN, exit 3.

    C1940  --nav-at on a run with no first-person camera (no --eye): an answer line, or a one-sentence refusal naming
           --nav-at (exit not 0); never exit 0 with neither
    C1941  the same for --clear-at
    C1942  the same for --inside-at
    C1943  the same for --refs-at
    C1944  a malformed --nav-at value (three numbers; not a number) is refused in one sentence naming the value, not
           dropped and not a stack trace
    C1945  a malformed --clear-at value (two numbers; not a number) is refused the same way

The control (not a case): with --eye, --nav-at 0,0 answers "covers it" on the same fixture, so a red case is the
query's defect and not a fixture that has no navmesh.

Exit 0: every case passed; 1: a case failed (or the control did); 3: not run.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
CASES = ("C1940", "C1941", "C1942", "C1943", "C1944", "C1945")
CELL = "CfCutaway"
EYE = ["--eye", "0,-300,120", "--look", "0,0,40", "--size", "64", "--msaa", "0", "--nav", "off"]
FLAT = ["--size", "64", "--msaa", "0"]
FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def run(rig, name, *flags):
    r = rig.lab.render(rig.tmp / "renders" / name, CELL, *flags)
    return r.returncode, r.stdout, r.stderr


def one_sentence(stderr: str) -> str | None:
    """The refusal: one non-empty stderr line, no .NET stack trace. None when stderr is not that."""
    if "Unhandled exception" in stderr or re.search(r"^\s+at \S", stderr, re.M):
        return None
    lines = [ln.strip() for ln in stderr.splitlines() if ln.strip()]
    return lines[-1] if lines else None


def answers_or_refuses(case, rig, option, value, answer_prefix):
    code, out, err = run(rig, f"{case}-flat", *FLAT, option, value)
    answered = any(ln.strip().startswith(answer_prefix) for ln in out.splitlines())
    said = one_sentence(err) if code != 0 else None
    check(case, answered or (said is not None and option in said),
          f"{option} {value} without --eye printed no answer and no refusal",
          f"exit {code}; stderr {err.strip()[-300:]!r}; answer lines {answered}")


def refuses_value(case, rig, option, value):
    code, out, err = run(rig, f"{case}-{re.sub(r'[^0-9a-z]+', '_', value)}", *EYE, option, value)
    said = one_sentence(err)
    check(case, code != 0 and said is not None and value in said and option in said,
          f"{option} '{value}' was not refused in one sentence naming it",
          f"exit {code}; stderr {err.strip()[-300:]!r}")


def run_all(rig):
    code, out, _ = run(rig, "control", *EYE, "--nav-at", "0,0")
    if not re.search(r"^\s*nav-at 0,0: .+ tri \d+ covers it", out, re.M):
        FAILS.append("CONTROL")
        RESULTS.append(("CONTROL", "RED", f"with --eye, --nav-at 0,0 did not answer 'covers it' (exit {code})"))
    answers_or_refuses("C1940", rig, "--nav-at", "0,0", "nav-at 0,0:")
    answers_or_refuses("C1941", rig, "--clear-at", "0,0,60", "clear-at 0,0,60:")
    answers_or_refuses("C1942", rig, "--inside-at", "0,0,60", "inside-at 0,0,60:")
    answers_or_refuses("C1943", rig, "--refs-at", "0,0", "---- refs within 256 u of 0,0:")
    refuses_value("C1944", rig, "--nav-at", "0,0,5")
    refuses_value("C1944", rig, "--nav-at", "x,0")
    refuses_value("C1945", rig, "--clear-at", "0,0")
    refuses_value("C1945", rig, "--clear-at", "0,y,60")


class Rig:
    def __init__(self, tmp: Path, vp: Path, game: Path):
        self.tmp = tmp
        self.lab = LAB.Lab(tmp / "lab", vp, game)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vp", default=str(PROD_VP))
    ap.add_argument("--keep", default=None)
    a = ap.parse_args(argv)
    game, why = LAB.game_folder()
    if game is None:
        print("NOT RUN:", why)
        return 3
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="pointq-"))
    tmp.mkdir(parents=True, exist_ok=True)
    try:
        run_all(Rig(tmp, Path(a.vp), game))
    except Exception as exc:  # a crash is reported as a crash, never as a case's own red
        FAILS.append("CRASH")
        RESULTS.append(("CRASH", "CRASH", f"{type(exc).__name__}: {exc} | {traceback.format_exc().splitlines()[-3:]}"))
    for case in CASES:
        if case not in FAILS:
            RESULTS.append((case, "GREEN", ""))
    for case, verdict, text in RESULTS:
        print(f"{case:7} {verdict:5} {text}")
    print(f"work folder: {tmp}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
