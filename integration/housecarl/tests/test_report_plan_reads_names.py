"""C567, moved from the controller oracle's case 560 (tests/test_evalrun.py) when the report left SkyWright's side: a
bundle the built-in controller makes with its names step is reported with no hand step, because evalreport plan finds
the names in the bundle as made.

    python integration/housecarl/tests/test_report_plan_reads_names.py

Built on test_evalrun's own fixture engine (its tiny plugins, trimmed registry and fake probes, generated into a
scratch folder, and its stand-in record reader for the names step), as case 560 builds its bundle.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402
sys.path.insert(0, str(SW.HARNESS / "tests"))
import test_evalrun as T  # noqa: E402  the controller oracle's fixture engine


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-c567-"))
    try:
        # the oracle's own set-up (test_evalrun.main): settings pinned, fixtures generated here, the stand-in reader
        T.pin_standard_settings(tmp)
        T.GEN = tmp / "fixtures"
        gen = subprocess.run([sys.executable, str(T.FIXT / "make_fixtures.py"), "--out", str(T.GEN)], capture_output=True, text=True)
        if gen.returncode != 0:
            print("  FAIL  make_fixtures.py did not run:\n" + gen.stdout + gen.stderr)
            return 1
        os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
        os.environ["PYTHONIOENCODING"] = "utf-8"
        os.environ["HOUSECARL_RECORD_READER_CMD"] = f"{sys.executable} {T.FIXT / 'fake_records_mcp.py'}"
        T.use_conflictdiff(tmp)
        case = "case-560-bundle-runs-names"
        bundle, plan, cid, arm, names = T.recorded_bundle(tmp, case, bundle_args=())
        T.expect(T.names_status(bundle).get("status"), "enriched", f"{case}: the names step read every object and base", "C560")
        out = tmp / f"{case}-report"
        r = subprocess.run([sys.executable, str(HERE.parent / "evalreport.py"), "plan", "--bundle", str(bundle), "--out",
                            str(out), "--verify-copy"], capture_output=True, text=True, encoding="utf-8", errors="replace")
        plans = [p for p in out.glob("*.json") if (T.load(p) or {}).get("reader") is not None] if out.is_dir() else []
        reader = (T.load(plans[0]) or {}).get("reader") if plans else {}
        T.check(r.returncode == 0 and isinstance((reader or {}).get("names"), dict),
                f"{case}: evalreport plan reads the names from the bundle with no hand step (C567; exit {r.returncode}, "
                f"{r.stderr.strip()[-200:]!r})")
    except SystemExit as ex:
        print(f"  FAIL  {ex}")
        return 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{'all checks passed' if not T.FAILURES else f'{len(T.FAILURES)} failed'}")
    return 1 if T.FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
