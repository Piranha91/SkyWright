"""recordenrich - the rows derived after measurement from a record import, added to an AI-side overlay bundle.

    python integration/housecarl/recordenrich.py --bundle DIR --kind record-rows|order-variant

The record checks are the houseCARL integration module's (evalcontrol/ai-checks.json), so their imports, and the rows
derived from them, are in the overlay bundle evalcontrol made beside the app's bundle. The derivation itself is
SkyWright's bundleenrich (record-rows: one rows file per credited record import, from that import's own transcripts;
order-variant: the same with the evaluated pair's order swapped), given the plugin header facts the bound app bundle's
probe emits recorded, since an overlay bundle carries no emit. The bundle is checked with `evalcontrol verify` (the
overlay, the app bundle it is bound to and the binding) before anything is written and after.

Exit codes: 0 written (the result as one JSON line); 2 refused before writing (one sentence); 3 written but the bundle no
longer verifies. Opens one Run (scripts/runlog.py) per invocation.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools on sys.path
sys.path.insert(0, str(SW.HARNESS))
sys.path.insert(0, str(SW.SCRIPTS))
import bundleenrich  # noqa: E402  the derivation (SkyWright's)
from runlog import Run  # noqa: E402

KINDS = ("record-rows", "order-variant")


def load(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def verify(d: Path) -> int:
    """`evalcontrol verify d`, which writes d/verify.json: its exit code."""
    exe = SW.tool("evalcontrol")
    if not exe.is_file():
        raise bundleenrich.Refused(f"the module's evalcontrol is not at {exe}; build the module (scripts/build.ps1)")
    return subprocess.run([str(exe), "verify", str(d)], capture_output=True, text=True, timeout=900).returncode


def app_headers(d: Path, manifest: dict) -> dict:
    """The plugin header facts the app bundle this overlay is bound to recorded (bundleenrich.recorded_plugin_headers)."""
    rel = (manifest.get("overlay") or {}).get("appBundleFromBundle")
    app = (d / rel).resolve() if rel else None
    am = load(app / "manifest.json") if app else None
    if not isinstance(am, dict):
        raise bundleenrich.Refused(f"the app bundle this overlay is bound to is not readable at {app}")
    return bundleenrich.recorded_plugin_headers(app, am)


def enrich(d: Path, kind: str) -> tuple[int, str]:
    """(exit code, the line to print)."""
    manifest = load(d / "manifest.json")
    if not isinstance(manifest, dict) or "overlay" not in manifest:
        return 2, (f"REFUSED: enrich refused: {d} is not an AI-side overlay bundle (its manifest has no overlay block); "
                   f"the record rows are derived in the overlay bundle evalcontrol makes beside the app's.")
    try:
        if verify(d) != 0:
            return 2, (f"REFUSED: enrich refused: {d} does not verify (see its verify.json); an enrichment is added only "
                       f"to an intact bundle")
        headers = app_headers(d, manifest)
        out = (bundleenrich.enrich_record_rows(d, headers) if kind == "record-rows"
               else bundleenrich.enrich_order_variant(d, headers))
    except bundleenrich.Refused as ex:
        return 2, f"REFUSED: enrich refused: {ex}"
    if verify(d) != 0:
        return 3, f"REFUSED: enrich wrote {kind} but the bundle no longer verifies; see its verify.json"
    return 0, json.dumps(out)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Rows derived from a record import, added to an AI-side overlay bundle.")
    ap.add_argument("--bundle", required=True, help="the overlay bundle (evalcontrol bundle's --out)")
    ap.add_argument("--kind", required=True, choices=KINDS)
    a = ap.parse_args(argv)
    with Run("recordenrich", stage_source="counted") as r:
        r.stage("enrich", total=1, unit="enrichments")
        code, line = enrich(Path(a.bundle), a.kind)
        print(line, file=sys.stderr if code else sys.stdout)
        if code:
            r.refuse(line.removeprefix("REFUSED: "), code)
        else:
            r.advance()
    return code


if __name__ == "__main__":
    sys.exit(main())
