"""Known answers for the configuration fingerprint's one owner (C2240-C2242).

    python tests/test_fingerprint_owner.py [--harness DIR]

The rule is `_shared/Configuration.cs`'s ConfigurationIdentity.Compute; `runcontrol fingerprint` answers it. The Python
that stays (switchflip, capture_acquire, evalrecords) asks it through `evalrun.configuration_fingerprint`.

    C2240  evalrun.configuration_fingerprint starts `runcontrol fingerprint` once and returns its answer, which is the
           sha256 of the owner's text worked here by hand (so a Python copy that drifted, or a call that never reached
           the controller, is red)
    C2241  a configuration of another schema, or with no state, is refused in one sentence, never hashed
    C2242  no harness module spells the fingerprint text itself (`plugin:` / `provider:` / `overlay:` lines beside
           `schema:`), so a second owner cannot come back unnoticed

The controller is HOUSECARL_RUNCONTROL, else the harness's own build (`runcontrol/bin/Release/net9.0`). Exit 0: every
case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import hashlib
import re
import subprocess
import sys
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument("--harness", default=str(Path(__file__).resolve().parents[1]))
a = ap.parse_args()
HARNESS = Path(a.harness).resolve()
sys.path.insert(0, str(HARNESS))
import evalrun  # noqa: E402

failed = 0


def check(name: str, ok: bool, detail: str) -> None:
    global failed
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    failed += 0 if ok else 1


# C2240
text = ("schema:1\npopulation:load-time\nstate:baseline\nplugin:skyrim.esm:ab\nplugin:beta.esp:\n"
        "provider:mod:beta shrine\noverlay:bos:cd\n")
want = hashlib.sha256(text.encode("utf-8")).hexdigest()
calls = []
real_run = subprocess.run


def spy(argv, *args, **kw):
    calls.append(list(map(str, argv)))
    return real_run(argv, *args, **kw)


subprocess.run = spy
try:
    got = evalrun.configuration_fingerprint([{"name": "Skyrim.ESM", "sha256": "ab"}, {"name": "Beta.esp", "sha256": None}],
                                            [{"kind": "mod", "name": "Beta Shrine"}], [{"kind": "bos", "sha256": "cd"}],
                                            "load-time", "baseline", schema=1)
except Exception as ex:  # noqa: BLE001 -- a crash is a failure
    got = f"raised {type(ex).__name__}: {ex}"
finally:
    subprocess.run = real_run
check("C2240 the fingerprint is the owner's answer", got == want, f"got {got!r}, the owner's text hashes to {want}")
check("C2240 it asked runcontrol fingerprint, once",
      len(calls) == 1 and Path(calls[0][0]).stem.lower() == "runcontrol" and calls[0][1:] == ["fingerprint"],
      f"the children started were {calls}")

# C2241
for label, args in (("schema 2", ([], [], [], "load-time", "baseline", 2)), ("no state", ([], [], [], "load-time", None))):
    try:
        out = evalrun.configuration_fingerprint(*args)
        check(f"C2241 {label} is refused", False, f"it was hashed to {out}")
    except RuntimeError as ex:
        msg = str(ex)
        check(f"C2241 {label} is refused in one sentence", "REFUSED" not in msg and msg.count("\n") == 0 and "fingerprint" in msg,
              f"the sentence was {msg!r}")

# C2242
spelled = []
for py in sorted(HARNESS.glob("*.py")):
    src = py.read_text(encoding="utf-8", errors="replace")
    if re.search(r"""f["']schema:\{""", src) and re.search(r"""f["']plugin:\{""", src):
        spelled.append(py.name)
check("C2242 no harness module spells the fingerprint text", not spelled, f"spelled in {spelled}")

print(f"{'all passed' if not failed else f'{failed} failed'}")
sys.exit(1 if failed else 0)
