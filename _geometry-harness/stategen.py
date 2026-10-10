"""stategen — the world states a Deep evaluation judges, generated from one arm's switch census.

    python stategen.py --census CENSUS.json --out STATES.json [--specs STATES.txt]

Input: switchprobe's `--json` census for ONE arm (evalrun's `state.switch-census` output). Output: every switch
alone (named by FormKey), every quest-stage joint flip of two or more switches (`stage:<quest>:<n>`), and a
not-explored row, with its reason, for everything the census names that cannot be expressed as a state. Nothing the
census names is dropped without a row. evalrun imports `generate` for a Deep plan; the CLI prints the same list.

Gate B stub: the interface the tests drive exists; the generator is not built yet.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

SCHEMA_VERSION = 1


def generate(census: dict, *, census_path: str | None = None, census_sha256: str | None = None) -> dict:
    """The state document for one arm's census: {schemaVersion, census, generator, states, covered, notExplored}."""
    raise NotImplementedError("stategen.generate is not built yet")


def dumps(doc: dict) -> str:
    """The document's canonical text (the same census gives the same bytes)."""
    raise NotImplementedError("stategen.dumps is not built yet")


def specs_text(doc: dict) -> str:
    """One stateprobe `--state` spec per line, in the document's order."""
    raise NotImplementedError("stategen.specs_text is not built yet")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="stategen", description=__doc__.split("\n\n")[0])
    ap.add_argument("--census", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--specs")
    ap.parse_args(argv)
    with Run("stategen") as run:
        print("stategen is not built yet", file=sys.stderr)
        run.refuse("not built yet", exit_code=2)
        return 2


if __name__ == "__main__":
    sys.exit(main())
