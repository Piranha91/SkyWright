"""Known answers for what the app's controller checks after the AI-side rows left it (step 3 of the SkyWright / houseCARL
boundary): a bundle made before the change is still accepted, and nothing in it goes silently unchecked; the commands
that only served an AI-side evaluation are refused, naming where they went.

    python tests/test_app_bundle_boundary.py

    B1  an old lab bundle (fixtures/old-bundles/lab-record-rows: case-01's fixture evaluation with one record import
        credited and a record-rows enrichment, made by runcontrol and evalrun before the change) verifies: exit 0,
        bundle.complete true, every file still hashed
    B2  ...and verify-bundle says in one sentence how many of its evidence rows are of kinds this SkyWright does not
        check, and that the houseCARL integration module checks them (runcontrol names no module program:
        the fence); verify.json lists each (the record-rows
        enrichment row and the imported record receipt among them)
    B3  runcontrol import-results, bundle --media and switchflip-outcome are refused in one sentence saying they are the
        houseCARL integration module's now (exit 2), never run and never a usage error

The controller is HOUSECARL_RUNCONTROL, else this checkout's build (buildlayout.runcontrol()). Each case works on a copy
(verify-bundle writes verify.json into the folder it checks). Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import buildlayout  # noqa: E402

RUNCONTROL = os.environ.get("HOUSECARL_RUNCONTROL") or str(buildlayout.runcontrol())
OLD_LAB = HERE / "fixtures" / "old-bundles" / "lab-record-rows"
failed = 0


def check(name: str, ok: bool, detail: str) -> None:
    global failed
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    failed += 0 if ok else 1


def rc(args, stdin: str | None = None):
    return subprocess.run([RUNCONTROL, *map(str, args)], capture_output=True, text=True, encoding="utf-8",
                          input=stdin, timeout=300)


def b1_b2(tmp: Path) -> None:
    b = tmp / "old-lab"
    shutil.copytree(OLD_LAB, b)
    r = rc(["verify-bundle", b])
    v = json.loads((b / "verify.json").read_text(encoding="utf-8")) if (b / "verify.json").is_file() else {}
    bundle = v.get("bundle") or {}
    check("B1 the old lab bundle verifies", r.returncode == 0 and bundle.get("complete") is True
          and not bundle.get("missing") and not bundle.get("mismatched"),
          f"exit {r.returncode}; bundle {json.dumps(bundle)[:300]}; stderr {r.stderr.strip()[-300:]!r}")
    lines = [ln for ln in r.stdout.splitlines() if "does not check" in ln]
    check("B2 one sentence says how many evidence rows are of kinds this SkyWright does not check, and who checks them",
          len(lines) == 1 and "houseCARL integration module" in lines[0] and lines[0].rstrip().endswith(".")
          # an older bundle has no overlay: the sentence says nothing judges its rows now (t14 item 4)
          and "with no overlay are judged by nothing now" in lines[0],
          f"lines {lines!r}; stdout {r.stdout.strip()[-400:]!r}")
    listed = {(x.get("path") or "") for x in (bundle.get("notChecked") or [])}
    want = {"enrichment/record-rows/records.cell-attributes__baseline.json", "receipts/records.cell-attributes__baseline.json"}
    check("B2 verify.json lists each row it does not check, the record rows and the record import among them",
          want <= listed and str(len(listed)) in (lines[0] if lines else ""),
          f"notChecked {sorted(listed)}; want at least {sorted(want)}")


def b3(tmp: Path) -> None:
    run = tmp / "no-run"
    (tmp / "doc.json").write_text("{}", encoding="utf-8")
    for label, args, stdin in (("import-results", ["import-results", "--run", run, "--file", tmp / "doc.json"], None),
                               ("bundle --media", ["bundle", "--run", run, "--media", tmp / "doc.json"], None),
                               ("switchflip-outcome", ["switchflip-outcome"], "{}")):
        r = rc(args, stdin)
        err = r.stderr.strip()
        check(f"B3 runcontrol {label} is refused in one sentence naming the houseCARL integration module",
              r.returncode == 2 and "houseCARL integration module" in err and "\n" not in err and err.endswith("."),
              f"exit {r.returncode}; stderr {err[-300:]!r}")


def main() -> int:
    if not Path(RUNCONTROL).is_file():
        print(f"FAIL runcontrol is not built at {RUNCONTROL}; build the solution (scripts/build.ps1).")
        return 1
    with tempfile.TemporaryDirectory(prefix="sw-app-bundle-boundary-") as t:
        b1_b2(Path(t))
        b3(Path(t))
    print(f"\n{failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
