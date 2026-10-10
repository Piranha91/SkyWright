"""evalsettings — the first-run choices for the worldspace compatibility evaluation, and the window that sets them.

    python evalsettings.py path
    python evalsettings.py show [--json]
    python evalsettings.py ensure [--no-window] [--opener SCRIPT] [--timeout-seconds N]
    python evalsettings.py window [--opener SCRIPT] [--timeout-seconds N]
    python evalsettings.py set KEY=VALUE [KEY=VALUE ...]
    python evalsettings.py defaults --out FILE
    python evalsettings.py validate FILE

Every command takes `--settings FILE`. The file is `houseCARL.evaluation.json`, found by `--settings`, then
HOUSECARL_EVAL_SETTINGS, then HOUSECARL_DATA_DIR, then the per-user data folder. `--opener SCRIPT` runs
`python SCRIPT URL` instead of the browser (tests).

Gate B stub: the interface the tests drive exists; nothing is built yet.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

SCHEMA_VERSION = 1


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalsettings", description=__doc__.split("\n\n")[0])
    ap.add_argument("--settings")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("path")
    p = sub.add_parser("show")
    p.add_argument("--json", action="store_true")
    for name in ("ensure", "window"):
        p = sub.add_parser(name)
        p.add_argument("--opener")
        p.add_argument("--timeout-seconds", type=float)
        if name == "ensure":
            p.add_argument("--no-window", action="store_true")
    p = sub.add_parser("set")
    p.add_argument("pairs", nargs="+")
    p = sub.add_parser("defaults")
    p.add_argument("--out", required=True)
    p = sub.add_parser("validate")
    p.add_argument("file")
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    with Run("evalsettings", mode=a.cmd) as run:
        print(f"evalsettings {a.cmd} is not built yet", file=sys.stderr)
        run.refuse("not built yet", exit_code=3)
        return 3


if __name__ == "__main__":
    sys.exit(main())
