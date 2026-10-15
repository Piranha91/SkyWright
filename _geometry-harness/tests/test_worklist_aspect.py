"""The capture canvas SHAPE travels with the shot: writer, schema, validator, precedence.

    python tests/test_worklist_aspect.py

Regression for the propagation gap found on 2026-09-15. Fixing viewprobe's canvas and the two
printed capture commands still left `viewprobe --worklist` -- the render shotplan advertises FIRST
-- rendering a square whatever the plan said, because `WorklistShot` had a `Fov` and no aspect at
all. A handoff that says "use the exported camera/worklist verbatim" is then impossible to obey.

Three populations, because the failure modes differ:

    non-square   a shot that carries an aspect must keep it
    legacy       a shot with no aspect INHERITS THE RUN'S setting, which defaults to square --
                 absent, not 1.0-recorded, so a file written before the field is unchanged
    mixed        one worklist, both kinds, each shot getting its own frame

No game and no renderer are needed for any of it. The planner runs for real against
`fake_viewprobe.py`; the schema and validator are the REAL ones, exercised by handing the real
viewprobe binary a worklist -- it reads and validates before the load walk, so a refusal costs no
cell. The end-to-end canvas proof (three shots, three canvases, one render) is evidence in the run
directory, not a unit test, because it needs the lab.
"""
from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
TOOLS = HARNESS / "viewprobe" / "tools"
sys.path.insert(0, str(TOOLS))
import shotplan as S  # noqa: E402

FAKE = (sys.executable.replace("\\", "/") + " "
        + str(TOOLS / "fake_viewprobe.py").replace("\\", "/"))
VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
FAILS: list[str] = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


# The toy scene is the planner suite's OWN fixture, imported rather than re-guessed: its shape is
# the fake renderer's contract, and a second hand-rolled copy would drift from it silently.
import test_shotplan_plan as _planner_suite  # noqa: E402

room = _planner_suite.room


def plan(argv, scene):
    """Drive shotplan's own main() end to end, capturing stdout."""
    import contextlib
    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    Path(path).write_text(json.dumps(scene), encoding="utf-8")
    os.environ["FAKE_VIEWPROBE_SCENE"] = path
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(io.StringIO()):
            try:
                code = S.main(argv)
            except SystemExit as e:
                code = e.code if isinstance(e.code, int) else 1
    finally:
        os.unlink(path)
    return code, buf.getvalue()


def plan_worklist(tmp: Path, *extra, tag="wl"):
    wl = tmp / f"{tag}.json"
    jp = tmp / f"{tag}-plan.json"
    code, out = plan(["plan", "TestCell", "--subject", "0,0,0=target", "--vp", FAKE, "--quiet",
                      "--json", str(jp), "--worklist", str(wl), *extra], room())
    doc = json.loads(wl.read_text(encoding="utf-8")) if wl.is_file() else None
    return code, out, doc


# --------------------------------------------------------------------------------------
# the writer: a non-square plan puts its shape in the worklist, a square one writes nothing
# --------------------------------------------------------------------------------------

def case_writer(tmp: Path):
    _, _, wide = plan_worklist(tmp, "--aspect", "16:9", tag="wide")
    check("a non-square plan writes a worklist at all", bool(wide))
    if wide:
        shot = wide["cells"][0]["shots"][0]
        check("the non-square plan writes the shot's aspect",
              "aspect" in shot, f"shot keys: {sorted(shot)}")
        check("the written aspect is the RESOLVED ratio, not the spelling",
              "aspect" in shot and abs(float(shot["aspect"]) - 16 / 9) < 1e-4,
              f"aspect = {shot.get('aspect')!r}")

    _, _, square = plan_worklist(tmp, tag="square")
    check("a square plan writes a worklist at all", bool(square))
    if square:
        shot = square["cells"][0]["shots"][0]
        # Absent, not 1.0. A worklist that RECORDS 1.0 is indistinguishable from one that was
        # never asked, and the whole legacy population depends on that difference staying visible.
        check("a SQUARE plan writes no aspect key at all, so legacy files stay legacy",
              "aspect" not in shot, f"shot keys: {sorted(shot)}")


# --------------------------------------------------------------------------------------
# the schema and validator: the REAL ones, via the real binary, before any load walk
# --------------------------------------------------------------------------------------

def worklist_file(tmp: Path, name: str, shots: list[dict]) -> Path:
    p = tmp / name
    p.write_text(json.dumps({
        "label": name,
        "eyeHeight": 120.0,
        "cells": [{"cell": "SomeCellThatNeedNotExist", "shots": shots}],
    }, indent=1), encoding="utf-8")
    return p


def shot(label, **over):
    s = {"label": label, "eye": [0.0, 0.0, 120.0], "yaw": 0.0, "pitch": 0.0, "fov": 65.0}
    s.update(over)
    return s


def run_vp(path: Path):
    """Hand the real binary a worklist. Validation precedes the load walk, so this is cheap. The probes refuse a run
    that names no MO2 instance (`instance-not-explicit`), so the run names an empty synthetic one beside the worklist:
    validation never reads it, and a shot that passes validation then fails to find its cell, as before."""
    inst, game = path.parent / "synthetic-instance", path.parent / "synthetic-game"
    inst.mkdir(exist_ok=True)
    (game / "Data").mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, HOUSECARL_MO2_INSTANCE=str(inst), HOUSECARL_GAME_PATH=str(game), HOUSECARL_MO2_PROFILE="Default")
    p = subprocess.run([str(VP), str(path)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=180, env=env)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def case_schema_and_validator(tmp: Path):
    if not VP.is_file():
        check("the viewprobe binary is present for the schema arm", False, f"missing {VP}")
        return

    # A degenerate aspect must be refused BY NAME, like every other camera number, before a cell
    # is loaded -- not carried into the projection to make a degenerate frame.
    # Assert the REFUSAL'S OWN WORDS, not a substring that anything else could supply. The first
    # version of this check accepted `"aspect" in out`, which the temp DIRECTORY NAME satisfied --
    # so it passed with the range check deleted. The RED-check caught it; the lesson is that a
    # refusal test must match text only the refusal can produce (2026-09-15).
    RANGE = "is out of range (0.01..100)"
    for bad in (0, -1.5, 500):
        p = worklist_file(tmp, f"bad-{bad}.json", [shot("bad", aspect=bad)])
        code, out = run_vp(p)
        check(f"an aspect of {bad} is refused by name, not rendered",
              code != 0 and RANGE in out and f"aspect {bad:g}" in out,
              f"exit {code}: " + " ".join(out.split())[:160])

    # Legacy: no aspect key anywhere. It must PARSE -- the failure to guard against is a schema
    # change that makes every worklist ever written invalid. Validation precedes the load walk, so
    # reaching the shot banner IS the proof it validated.
    legacy = worklist_file(tmp, "legacy.json", [shot("legacy-square")])
    code, out = run_vp(legacy)
    check("a legacy worklist with no aspect key still parses and validates",
          RANGE not in out and "worklist cell 1/1 shot 1/1" in out,
          " ".join(out.split())[:160])

    # Mixed: one worklist, one shot with and one without. Both must survive validation.
    mixed = worklist_file(tmp, "mixed.json",
                          [shot("wide", aspect=16 / 9), shot("legacy-square")])
    code, out = run_vp(mixed)
    check("a MIXED worklist (one shot with an aspect, one without) validates",
          RANGE not in out and "2 shots over 1 cell" in out,
          " ".join(out.split())[:160])


# --------------------------------------------------------------------------------------
# precedence, read off the source: the run's value first, then this shot's override
# --------------------------------------------------------------------------------------

def case_precedence_is_wired_like_its_neighbours(tmp: Path):
    """The override must follow the SAME shape as nav/planes/occlusion, and reset per shot.

    Asserted against the source rather than a render because the canvas is only observable from a
    loaded cell. The end-to-end proof (three shots, three canvases, one render) lives in the run
    directory. What is checked here is the property a second shot depends on: the run's value is
    restored before each shot, so shot 2 cannot inherit shot 1's override.
    """
    src = (HARNESS / "viewprobe" / "Program.cs").read_text(encoding="utf-8", errors="replace")
    check("the run's canvas aspect is captured before any shot can change it",
          "float cliCanvasAspect = canvasAspect;" in src)
    check("each worklist shot RESETS to the run's value first",
          "canvasAspect = cliCanvasAspect;" in src)
    check("and then applies its own aspect if it has one",
          "if (wshot.Aspect is { } shotAspect) canvasAspect = shotAspect;" in src)
    # The reset must come BEFORE the override, or every shot after the first keeps shot 1's frame.
    reset, override = src.find("canvasAspect = cliCanvasAspect;"), src.find("wshot.Aspect is")
    check("the reset precedes the override, so shot 2 cannot inherit shot 1's frame",
          0 < reset < override, f"reset at {reset}, override at {override}")

    sp = (TOOLS / "shotplan.py").read_text(encoding="utf-8", errors="replace")
    check("the planner writes the shot aspect only when the plan is non-square",
          'shot["aspect"] = round(float(args.aspect), 6)' in sp
          and 'abs(float(getattr(args, "aspect", 1.0)) - 1.0) > 1e-6' in sp)


def main():
    with tempfile.TemporaryDirectory(prefix="worklist-aspect-") as t:
        tmp = Path(t)
        print("-- the writer: non-square records its shape, square records nothing")
        case_writer(tmp)
        print("\n-- the schema and validator: non-square, legacy, mixed, and degenerate")
        case_schema_and_validator(tmp)
        print("\n-- precedence: the run's value first, then the shot's")
        case_precedence_is_wired_like_its_neighbours(tmp)

    print()
    if FAILS:
        print(f"{len(FAILS)} FAILED:")
        for f in FAILS:
            print("  - " + f)
        return 1
    print("all worklist-aspect checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
