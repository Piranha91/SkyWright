"""Light-clash fold over lightprobe outputs (lane L, 2026-10-04) -- RED STUB.

The API the known answers in tests/test_lightclash.py are written against; every function raises until the
implementation lands.
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402,F401

POLICY = "light_clash_fold_v1 (stub)"


@dataclass(frozen=True)
class Params:
    host_radius: float
    leak_threshold: float


def _todo(*_a, **_k):
    raise NotImplementedError("lightclash: not implemented yet")


is_fixture = c1_orphan = c1b_unlit = c3a_doubled = c2_leak = c4_limit = attribute = _todo
placement_kind = floor_rule_applies = verdict = _todo


def main(argv: list[str]) -> int:
    with Run("lightclash", stage_source="single-stage"):
        return _todo()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
