"""Known answers for `runcontrol population`: the population file for an ad-hoc delta, by the rule `plan` freezes (C2400-C2402).

    python tests/test_evalrun_population_verb.py [--only C2400]

No game and no probe. A one-plugin delta (a minimal plugin file and its mod folder) is written by the built-in
controller's `population` verb, started as a child process. The expected file text is spelled here from the population
schema, not read back from the code under test. (That plan freezes the same file for the same delta is runcontrol's
`plan: population_file ...` self-test case.)

    C2400  the verb writes, byte for byte, the file the schema says (plugins by name, path and sha256; mod folders by
           name and path; the targets); it prints the identity <enable population>+population:<first 12 of its sha256>
    C2401  a delta mod folder that ships a Base Object Swapper ini is refused in one sentence and nothing is written
    C2402  --out is the only file written

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
os.environ["PYTHONUTF8"] = "1"

import subprocess  # noqa: E402
sys.path.insert(0, str(HERE))
from test_evalrun import CONTROLLER  # noqa: E402  (the built-in controller, runcontrol, as test_evalrun starts it)

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return cond


def plugin_bytes() -> bytes:
    """A TES4 header and nothing else: a plugin with no groups."""
    hedr = b"HEDR" + struct.pack("<H", 12) + struct.pack("<fII", 1.7, 0, 0x800)
    return b"TES4" + struct.pack("<I", len(hedr)) + b"\0" * 16 + hedr


def world(root: Path, swap: bool = False):
    inst = root / "instance"
    mod = inst / "mods" / "Patch Mod"
    mod.mkdir(parents=True)
    plugin = mod / "Patch.esp"
    plugin.write_bytes(plugin_bytes())
    if swap:
        (mod / "Patch_SWAP.ini").write_text("[Forms]\n", encoding="utf-8")
    return inst, plugin


def run_main(argv) -> tuple[int, str]:
    r = subprocess.run(CONTROLLER + [str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode, r.stdout + r.stderr


def c2400_same_file_as_plan(tmp: Path):
    root = tmp / "c2400"
    inst, plugin = world(root)
    sha = hashlib.sha256(plugin.read_bytes()).hexdigest()
    want = json.dumps({"schema": 1, "plugins": [{"name": "Patch.esp", "path": str(plugin), "sha256": sha}],
                       "mods": [{"name": "Patch Mod", "path": str(inst / "mods" / "Patch Mod")}],
                       "targets": ["Patch.esp"]}, indent=1, sort_keys=True) + "\n"
    out = root / "work" / "population.json"
    rc, said = run_main(["population", "--plugin", str(plugin), "--mod", "Patch Mod", "--instance", str(inst),
                         "--out", str(out)])
    got = out.read_text(encoding="utf-8") if out.is_file() else None
    want_sha = hashlib.sha256(want.encode("utf-8")).hexdigest()
    check("C2400", rc == 0 and got == want and f"identity load-time+population:{want_sha[:12]}" in said,
          "the verb is missing, or writes another file than the schema says for the delta, or prints another identity",
          f"exit {rc}; said {said.strip()[:200]!r}; file equals schema text: {got == want}")


def c2401_swap_refused(tmp: Path):
    root = tmp / "c2401"
    inst, plugin = world(root, swap=True)
    out = root / "work" / "population.json"
    rc, said = run_main(["population", "--plugin", str(plugin), "--mod", "Patch Mod", "--instance", str(inst),
                         "--out", str(out)])
    check("C2401", rc == 2 and not out.exists() and "Base Object Swapper" in said and said.strip().count("\n") == 0,
          "a delta mod folder with a _SWAP.ini is scoped anyway, or refused in more than one line, or a file is written",
          f"exit {rc}; said {said.strip()[:200]!r}; written {out.exists()}")


def c2402_writes_only_out(tmp: Path):
    root = tmp / "c2402"
    inst, plugin = world(root)
    before = {p: p.stat().st_mtime_ns for p in root.rglob("*") if p.is_file()}
    out = root / "work" / "population.json"
    rc, _ = run_main(["population", "--plugin", str(plugin), "--mod", "Patch Mod", "--instance", str(inst),
                      "--out", str(out)])
    after = {p: p.stat().st_mtime_ns for p in root.rglob("*") if p.is_file()}
    new = sorted(str(p.relative_to(root)) for p in after if p not in before)
    changed = sorted(str(p.relative_to(root)) for p in before if after.get(p) != before[p])
    check("C2402", rc == 0 and new == [str(Path("work") / "population.json")] and not changed,
          "the verb writes something besides --out, or touches the delta's files",
          f"exit {rc}; new {new}; changed {changed}")


CASES = {"C2400": c2400_same_file_as_plan, "C2401": c2401_swap_refused, "C2402": c2402_writes_only_out}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="popverb-"))
    for cid, fn in CASES.items():
        if only and cid not in only:
            continue
        try:
            fn(root)
        except Exception as e:  # noqa: BLE001  (a crash is reported as such, never as the case's own answer)
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:6} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
