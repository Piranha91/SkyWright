"""Known answers for the live driver when the reading rule's owner cannot be asked (C2243-C2245).

    python tests/test_switchflip_rule_unavailable.py

switchflip.py asks evalcontrol (`switchflip-outcome`) and runcontrol (`identity`) through switchflip_outcome.py. A controller that is
missing, locked by a rebuild or refusing must never skip a launch's cleanup (receipt, teardown, profile restore) or
leave the block's lock held. No game is started here.

    C2243  every call into the rule (O.classify, O.arm_reading, O.window_s, O.owner_identity, window_min) in switchflip.py
           sits in the body of a `try` that catches RuntimeError, or in a function whose own body does
    C2244  with HOUSECARL_EVALCONTROL naming no file, the client raises RuntimeError with one sentence naming the cause
    C2245  with the same, validate_plan reports that sentence as a plan problem, before any lock is taken

Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import ast
import os
import sys
from pathlib import Path

HARNESS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, str(HARNESS))
failed = 0


def check(name: str, ok: bool, detail: str) -> None:
    global failed
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    failed += 0 if ok else 1


# C2243
tree = ast.parse((HARNESS / "switchflip.py").read_text(encoding="utf-8"))
RULE = {("O", "classify"), ("O", "arm_reading"), ("O", "window_s"), ("O", "owner_identity")}
parents = {}
for node in ast.walk(tree):
    for child in ast.iter_child_nodes(node):
        parents[child] = node


def catches_runtime(t: ast.Try) -> bool:
    for h in t.handlers:
        names = [h.type] if not isinstance(h.type, ast.Tuple) else list(h.type.elts)
        if h.type is None or any(isinstance(n, ast.Name) and n.id in ("RuntimeError", "Exception") for n in names):
            return True
    return False


def guarded(node) -> bool:
    child, cur = node, parents.get(node)
    while cur is not None:
        if isinstance(cur, ast.Try) and child in cur.body and catches_runtime(cur):
            return True
        if isinstance(cur, (ast.FunctionDef, ast.Lambda)):
            # a wrapper whose own try catches it (rule_build), or window_min, whose callers guard it
            if isinstance(cur, ast.FunctionDef) and cur.name in ("rule_build", "window_min"):
                return True
            return False
        child, cur = cur, parents.get(cur)
    return False


bare = []
for node in ast.walk(tree):
    if isinstance(node, ast.Call):
        f = node.func
        is_rule = (isinstance(f, ast.Attribute) and isinstance(f.value, ast.Name) and (f.value.id, f.attr) in RULE) \
            or (isinstance(f, ast.Name) and f.id == "window_min")
        if is_rule and not guarded(node):
            bare.append(node.lineno)
calls = sum(1 for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
            and isinstance(n.func.value, ast.Name) and (n.func.value.id, n.func.attr) in RULE)
# cmd_export may refuse when the rule cannot be asked: it cleans nothing up
export_lines = {n.lineno for fn in ast.walk(tree) if isinstance(fn, ast.FunctionDef) and fn.name == "cmd_export"
                for n in ast.walk(fn) if hasattr(n, "lineno")}
bare = [ln for ln in bare if ln not in export_lines]
check("C2243 every rule call on a launch or block path is guarded", calls >= 5 and not bare,
      f"{calls} rule calls; unguarded at lines {bare}")

# C2244, C2245
os.environ["HOUSECARL_EVALCONTROL"] = str(HARNESS / "no-such-evalcontrol.exe")
import switchflip_outcome as O  # noqa: E402
try:
    O.classify([], 0.0)
    check("C2244 the client refuses when the controller is missing", False, "it answered")
except RuntimeError as ex:
    msg = str(ex)
    check("C2244 the client refuses in one sentence naming the missing controller",
          "\n" not in msg and "no-such-evalcontrol.exe" in msg, msg)

import switchflip  # noqa: E402
switchflip.window_min.cache_clear()
doc = {"schemaVersion": 1, "binding": None, "arms": [{"id": "a", "profile": "p"}],
       "conditions": [{"id": "C1", "arm": "a", "arrival": {"cell": "X"}, "windowSeconds": 60, "calls": [], "replicates": 2}]}
try:
    probs = switchflip.validate_plan(doc)
    check("C2245 validate_plan names the unreachable rule as a plan problem",
          any("no-such-evalcontrol.exe" in p for p in probs), f"problems: {probs}")
except Exception as ex:  # noqa: BLE001 -- a crash is a failure
    check("C2245 validate_plan names the unreachable rule as a plan problem", False, f"raised {type(ex).__name__}: {ex}")

print("all passed" if not failed else f"{failed} failed")
sys.exit(1 if failed else 0)
