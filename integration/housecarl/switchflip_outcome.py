"""switchflip_outcome — the live driver's client for the runtime state-switch check's reading rule.

The rule (`runtime.state-switch-flip`: classify one launch's events; read an arm's replicates of one condition) has one
owner, this module's evalcontrol (`evalcontrol/SwitchFlipOutcome.cs`). The live import there
re-derives every receipt's classification from its recorded events with the same code, so a stored label is never
trusted on its own. This file holds no copy of the rule: each function asks `evalcontrol switchflip-outcome`
(HOUSECARL_EVALCONTROL, else the module's build) and returns its answer.

  classify(events, flip_start, window_s=None) -> {"outcome": complete|fail|not-measured, "branch", "why"}
  arm_reading(classifications)                -> {"reading": finding|no-finding|unsettled, "failed", "completed",
                                                  "unjudgeable", "why"}
  window_s()                                  -> the shortest observation window a completion may claim (seconds)
  owner_identity()                            -> the package digest of the evalcontrol build that answers, for a receipt

The event shapes and the outcomes are documented at the owner. A controller that cannot be started, or that refuses
the request, raises RuntimeError with one sentence; nothing here answers in its place.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

OUTCOMES = ("complete", "fail", "not-measured")
READINGS = ("finding", "no-finding", "unsettled")


def _exe() -> Path:
    import evalpipeline
    named = os.environ.get("HOUSECARL_EVALCONTROL")
    try:
        return evalpipeline.evalcontrol_exe({"evalcontrol": named} if named else None)
    except evalpipeline.Refusal as ex:
        raise RuntimeError(f"the switch-flip reading rule could not be asked: {ex}") from None


def _ask(request: dict):
    exe = _exe()
    try:
        r = subprocess.run([str(exe), "switchflip-outcome"], input=json.dumps(request, default=str), capture_output=True,
                           text=True, encoding="utf-8", timeout=120)
    except (OSError, subprocess.SubprocessError) as ex:
        raise RuntimeError(f"the switch-flip reading rule could not be asked: evalcontrol switchflip-outcome could not run "
                           f"({ex}); rebuild the module and try again.") from None
    try:
        got = json.loads(r.stdout) if r.returncode == 0 else None
    except ValueError:
        got = None
    if not isinstance(got, dict):
        why = (r.stderr or r.stdout or "").strip().splitlines()
        raise RuntimeError("the switch-flip reading rule could not be asked: "
                           + (why[-1].removeprefix("REFUSED: ") if why else
                              f"evalcontrol switchflip-outcome exited {r.returncode} with no answer"))
    return got


def classify(events: list[dict], flip_start: float, window_s: float | None = None) -> dict:
    req = {"events": events, "flipStart": flip_start}
    if window_s is not None:
        req["windowSeconds"] = window_s
    return _ask({"classify": req})


def arm_reading(classifications: list[dict]) -> dict:
    """Read one arm's replicates of one condition. Each item is a classify() result."""
    return _ask({"armReading": classifications})


def window_s() -> float:
    return float(_ask({"rules": True})["windowSeconds"])


def owner_identity() -> str:
    """The package digest (runcontrol's `identity` of a build folder, the identity rule's owner) of the evalcontrol
    build whose rule classified a launch: a receipt names it, as it names the driver's own scripts, so what read the
    launch can be shown later."""
    import evalpipeline
    exe = _exe()
    try:
        r = subprocess.run([str(evalpipeline.runcontrol_exe(None)), "identity", str(exe.parent)], capture_output=True, text=True, encoding="utf-8",
                           timeout=300)
        digest = json.loads(r.stdout).get("digest") if r.returncode == 0 else None
    except (OSError, subprocess.SubprocessError, ValueError, AttributeError, evalpipeline.Refusal) as ex:
        raise RuntimeError(f"the switch-flip reading rule's build could not be identified ({ex}); rebuild the app "
                           "and the module and try again.") from None
    if not isinstance(digest, str) or not digest:
        raise RuntimeError("the switch-flip reading rule's build could not be identified: runcontrol identity gave no "
                           "digest; rebuild the app and the module and try again.")
    return digest
