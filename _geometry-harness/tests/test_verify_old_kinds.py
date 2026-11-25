"""Known answers for what `runcontrol verify-bundle` does with evidence rows of kinds this SkyWright does not check (the
record-rows and order-variant enrichments, derived from record imports, and a record import's receipt: the record
checks left SkyWright's registry for the houseCARL integration module in step 3 of the boundary). The rule keys on the
registry version the bundle's own plan records (hashed, so bound), never on a list of kind names:

    V1  a bundle whose plan records a registry older than 5 (fixtures/old-bundles/lab-record-rows: case-01's fixture
        evaluation with one record import credited and a record-rows enrichment, made before the change): it verifies
        (exit 0, bundle.complete), every file still hashed; verify.json lists under bundle.notChecked the enrichment row
        and the import's receipt; one sentence, printed once, gives their count and who checks them
    V2  the same rows in a bundle whose plan records registry version 5 (the fixture's plan edited to 5, plan.sha256
        and the manifest's rows for the two files re-hashed): refused, exit 3, one defect evidence-kind-unknown on the
        enrichment row whose detail is one sentence, and no notChecked list or sentence (a receipt's checks do not
        depend on its kind, so the import's receipt is chained as any receipt is and is not refused by kind alone)
    V3  the fixture's record-rows row relabelled order-variant (Q-B3): in the old bundle it is listed the same way, and
        in the version-5 graft refused the same way, by its own kind's name

The controller is HOUSECARL_RUNCONTROL, else this checkout's build (buildlayout.runcontrol()). Every case works on a copy
(verify-bundle writes verify.json into the folder it checks). Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import hashlib
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
ROWS = "enrichment/record-rows/records.cell-attributes__baseline.json"
RECEIPT = "receipts/records.cell-attributes__baseline.json"
failed = 0


def check(name: str, ok: bool, detail: str) -> None:
    global failed
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    failed += 0 if ok else 1


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def copy(tmp: Path, name: str, version: int | None = None, relabel: str | None = None) -> Path:
    b = tmp / name
    shutil.copytree(OLD_LAB, b)
    m = json.loads((b / "manifest.json").read_text(encoding="utf-8"))
    if relabel:
        rows = [e for e in m["evidence"] if e.get("path") == ROWS]
        assert len(rows) == 1 and rows[0].get("enrichment") == "record-rows", rows
        rows[0]["enrichment"] = relabel
    if version is not None:
        plan_file = b / "plan.json"
        plan = json.loads(plan_file.read_text(encoding="utf-8"))
        assert plan["registry"]["registryVersion"] == 4, plan["registry"]
        plan["registry"]["registryVersion"] = version
        plan_file.write_text(json.dumps(plan, indent=1), encoding="utf-8")
        old = (b / "plan.sha256").read_text(encoding="utf-8")
        (b / "plan.sha256").write_text(sha(plan_file) + old[64:], encoding="utf-8")
        changed = 0
        for e in m["evidence"]:
            if e.get("path") in ("plan.json", "plan.sha256"):
                e["sha256"], e["bytes"] = sha(b / e["path"]), (b / e["path"]).stat().st_size
                changed += 1
        assert changed == 2, changed
    (b / "manifest.json").write_text(json.dumps(m, indent=1), encoding="utf-8")
    return b


def verify(b: Path):
    r = subprocess.run([RUNCONTROL, "verify-bundle", str(b)], capture_output=True, text=True, encoding="utf-8", timeout=300)
    v = json.loads((b / "verify.json").read_text(encoding="utf-8")) if (b / "verify.json").is_file() else {}
    return r, v.get("bundle") or {}


def sentences(r) -> list[str]:
    return [ln for ln in r.stdout.splitlines() if "does not check" in ln]


def old_listed(tmp: Path, name: str, kind: str, relabel: str | None) -> None:
    r, bundle = verify(copy(tmp, name, relabel=relabel))
    listed = {(x.get("path"), x.get("kind")) for x in bundle.get("notChecked") or []}
    said = sentences(r)
    check(f"{name}: the old bundle verifies, every file hashed", r.returncode == 0 and bundle.get("complete") is True
          and not bundle.get("missing") and not bundle.get("mismatched"),
          f"exit {r.returncode}; {json.dumps(bundle)[:300]}")
    check(f"{name}: verify.json lists the {kind} row and the import's receipt as not checked here",
          (ROWS, f"enrichment {kind}") in listed and any(p == RECEIPT for p, _ in listed), f"notChecked {sorted(listed)}")
    check(f"{name}: one sentence, printed once, counts them and names who checks them",
          len(said) == 1 and str(len(listed)) in said[0] and "houseCARL integration module" in said[0]
          and said[0].rstrip().endswith("."), f"lines {said!r}")


def current_refused(tmp: Path, name: str, kind: str, relabel: str | None) -> None:
    r, bundle = verify(copy(tmp, name, version=5, relabel=relabel))
    unknown = [d for d in bundle.get("defects") or [] if d.get("kind") == "evidence-kind-unknown"]
    check(f"{name}: the same rows in a bundle of registry version 5 are refused", r.returncode == 3
          and bundle.get("complete") is False and not bundle.get("mismatched"),
          f"exit {r.returncode}; {json.dumps(bundle)[:400]}")
    check(f"{name}: one defect, on the {kind} row, in one sentence naming its kind",
          len(unknown) == 1 and unknown[0].get("path") == ROWS and kind in unknown[0].get("detail", "")
          and "\n" not in unknown[0].get("detail", ""), f"defects {unknown}")
    check(f"{name}: nothing is listed as not checked, and no sentence says so", not bundle.get("notChecked") and not sentences(r),
          f"notChecked {bundle.get('notChecked')}; lines {sentences(r)!r}")


def main() -> int:
    if not Path(RUNCONTROL).is_file():
        print(f"FAIL runcontrol is not built at {RUNCONTROL}; build the solution (scripts/build.ps1).")
        return 1
    with tempfile.TemporaryDirectory(prefix="sw-verify-old-kinds-") as t:
        tmp = Path(t)
        old_listed(tmp, "V1", "record-rows", None)
        current_refused(tmp, "V2", "record-rows", None)
        old_listed(tmp, "V3-old", "order-variant", "order-variant")
        current_refused(tmp, "V3-current", "order-variant", "order-variant")
    print(f"\n{failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
