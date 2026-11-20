"""houseCARL's run log, loaded from the pinned submodule (external/houseCARL/scripts/runlog.py).

The run-event contract has one owner, houseCARL. The harness's Python finds `<repo>/scripts/runlog.py` either on
sys.path (`from runlog import Run`) or by file (importlib); both reach the real module's names through this file.
"""
import importlib.util as _util
import sys as _sys
from pathlib import Path as _Path

_REAL = _Path(__file__).resolve().parent.parent / "external" / "houseCARL" / "scripts" / "runlog.py"
if not _REAL.is_file():
    raise ImportError(f"houseCARL's runlog.py is not at {_REAL}; run `git submodule update --init` in the SkyWright checkout.")
_spec = _util.spec_from_file_location("_housecarl_runlog", _REAL)
_mod = _util.module_from_spec(_spec)
_sys.modules["_housecarl_runlog"] = _mod
_spec.loader.exec_module(_mod)
globals().update({k: v for k, v in vars(_mod).items() if not (k.startswith("__") and k.endswith("__"))})
