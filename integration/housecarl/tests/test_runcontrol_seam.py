"""The seam between the built-in controller and the module's pipeline on one run folder, black box: runcontrol carries
an in-place entry through scope with its real probes over a synthetic instance, then evalpipeline.py run reuses every
stage runcontrol accepted, recomputing each stage's inputs by its own code. Moved from runcontrol's self-test ("live:
the Python pipeline reuses the census, prepare and scope this program accepted"), where a stand-in scopeprobe stood in;
here the controller and its probes run whole.

    python integration/housecarl/tests/test_runcontrol_seam.py

    S1  runcontrol run --through scope is accepted on the synthetic instance (its probes are this checkout's build).
    S2  evalpipeline.py run --through scope on the same folder says each of census, prepare and scope is "reused (inputs
        and outputs unchanged)" and runs none of them.
    S3  a changed measuring key of the entry is not reused: the pipeline stops at census naming runcontrol.
"""
from __future__ import annotations

import json
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402
import buildlayout  # noqa: E402

FAILURES: list[str] = []
NUL = bytes([0])


def check(name, ok, why):
    print(f"  {'PASS' if ok else 'FAIL'}  {name}: {why}")
    if not ok:
        FAILURES.append(name)


def plugin(masters, esm=False, tag=""):
    """A minimal TES4 header: HEDR, CNAM (which makes each file's bytes its own), MAST/DATA per master."""
    def sub(t, data):
        return t + struct.pack("<H", len(data)) + data
    body = sub(b"HEDR", struct.pack("<fiI", 1.71, 0, 0x800)) + sub(b"CNAM", (tag or "fixture").encode() + NUL)
    for m in masters:
        body += sub(b"MAST", m.encode() + NUL) + sub(b"DATA", struct.pack("<Q", 0))
    return b"TES4" + struct.pack("<III", len(body), 1 if esm else 0, 0) + NUL * 8 + body


def world(t: Path) -> dict:
    """Three enabled mods, each with one plugin of its own bytes: Sel (the selection), Other and A."""
    inst, game = t / "instance", t / "game"
    (game / "Data").mkdir(parents=True)
    (game / "Data" / "Base.esm").write_bytes(plugin([], True, "Base.esm"))
    for mod, files in (("Sel", ["Sel.esp"]), ("Other", ["Other.esp"]), ("A", ["A.esp"])):
        (inst / "mods" / mod).mkdir(parents=True)
        for f in files:
            (inst / "mods" / mod / f).write_bytes(plugin(["Base.esm"], tag=mod + "/" + f))
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (prof / "modlist.txt").write_text("+Sel\r\n+Other\r\n+A\r\n", encoding="utf-8")
    (prof / "loadorder.txt").write_text("Base.esm\r\nA.esp\r\nOther.esp\r\nSel.esp\r\n", encoding="utf-8")
    (prof / "plugins.txt").write_text("*Base.esm\r\n*A.esp\r\n*Other.esp\r\n*Sel.esp\r\n", encoding="utf-8")
    (inst / "overwrite").mkdir()
    return {"schema": 1, "id": "wb-test", "inPlace": {"instance": str(inst), "profile": "P", "game": str(game)},
            "selection": {"mods": ["Sel"]}, "evaluation": {"select": ["geometry.object-overlap"]}, "modNames": {"Sel.esp": "The Selection"}}


def main() -> int:
    rc = buildlayout.runcontrol()
    if not rc.is_file():
        print(f"REFUSED: runcontrol is not built at {rc}; build the solution (scripts/build.ps1).", file=sys.stderr)
        return 2
    with tempfile.TemporaryDirectory(prefix="hc-seam-") as td:
        t = Path(td)
        entry = world(t)
        (t / "entry.json").write_text(json.dumps(entry), encoding="utf-8")
        env = {**os.environ, "HOUSECARL_RUNLOG_DIR": str(t / "runlog"), "PYTHONIOENCODING": "utf-8"}
        r = subprocess.run([str(rc), "run", "--entry", str(t / "entry.json"), "--out", str(t / "run"), "--through", "scope"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=600)
        check("S1", r.returncode == 0 and "scope: accepted" in r.stdout, f"runcontrol exit {r.returncode}: {(r.stdout + r.stderr).strip()[-400:]}")
        pipe = [sys.executable, "-B", str(HERE.parent / "evalpipeline.py"), "run", "--entry", str(t / "entry.json"), "--out", str(t / "run"), "--through", "scope"]
        p = subprocess.run(pipe, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=600)
        reused = [s for s in ("census", "prepare", "scope") if f"{s}: reused (inputs and outputs unchanged)" in p.stdout]
        check("S2", p.returncode == 0 and reused == ["census", "prepare", "scope"],
              f"evalpipeline exit {p.returncode}; reused {reused}: {(p.stdout + p.stderr).strip()[-400:]}")
        (t / "entry.json").write_text(json.dumps(dict(entry, evaluation={"select": ["geometry.object-overlap"], "depth": "deep"})), encoding="utf-8")
        q = subprocess.run(pipe, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=600)
        check("S3", q.returncode == 2 and "census" in q.stderr and "runcontrol run" in q.stderr,
              f"evalpipeline exit {q.returncode}: {q.stderr.strip()[-300:]}")
    print(f"\n{'all checks passed' if not FAILURES else f'{len(FAILURES)} failed: ' + ', '.join(FAILURES)}")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
