"""evalreport's reel refuses a --capture-results file it cannot read (task 2026-11-11-evalreport-unreadable-capture-results).

    python _geometry-harness/tests/test_capture_results_unreadable.py

Render 13 ran with 11 capture-results files missing and said nothing, and its reports came out with no figures. A file
the plan was told to read and cannot read is refused in one sentence naming the file and what to try: a missing file,
one that is not JSON, and JSON that is neither a list nor an object of capture results. A plan given no capture results,
or a readable empty list, still plans its reel.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

import test_reel_vignettes as TRV  # noqa: E402  (the names fixture: a small bundle the reel planner accepts)

RESULTS: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, msg: str) -> bool:
    print(("  PASS  " if ok else "  FAIL  ") + f"[{name}] {msg}")
    RESULTS.append((name, bool(ok), msg))
    return bool(ok)


def reel_with(tmp: Path, captures: list[str]):
    """reel_block on the names fixture with these --capture-results files: ("planned", reel) or ("refused", reason)."""
    import evalreport
    pres, bundle, store, sp, stub = TRV.names_fixture(tmp)
    out = tmp / "plan"
    out.mkdir()
    a = argparse.Namespace(mod_name=[], patch_search=str(sp), capture_results=list(captures), captures_final=False,
                           nexus_names="offline", nexus_reader_cmd=None)
    old = os.environ.get("HOUSECARL_NEXUS_CATALOG_DIR")
    os.environ["HOUSECARL_NEXUS_CATALOG_DIR"] = str(store)
    try:
        return "planned", evalreport.reel_block(pres, bundle, None, evalreport.load_priorities(), a, out)
    except evalreport.InputRefused as ex:
        return "refused", str(ex)
    finally:
        if old is None:
            os.environ.pop("HOUSECARL_NEXUS_CATALOG_DIR", None)
        else:
            os.environ["HOUSECARL_NEXUS_CATALOG_DIR"] = old


def one_sentence(reason: str) -> bool:
    """One sentence: it ends in a full stop and holds no sentence break before it."""
    return reason.endswith(".") and ". " not in reason[:-1]


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="hc-capres-") as t:
        tmp = Path(t)
        missing = tmp / "gone" / "capture-results-07.json"
        corrupt = tmp / "capture-results-corrupt.json"
        corrupt.write_text('[{"status": "captured", "formKey": ', encoding="utf-8")
        scalar = tmp / "capture-results-scalar.json"
        scalar.write_text("5", encoding="utf-8")
        empty = tmp / "capture-results-empty.json"
        empty.write_text("[]", encoding="utf-8")
        for i, (label, files, bad) in enumerate((("CR1-missing", [str(empty), str(missing)], missing),
                                                  ("CR2-not-json", [str(corrupt)], corrupt),
                                                  ("CR3-not-capture-results", [str(scalar)], scalar))):
            got, what = reel_with(tmp / f"case{i}", files)
            check(label, got == "refused" and str(bad) in what and what.startswith("capture-results-unreadable: ")
                  and one_sentence(what),
                  f"an unreadable --capture-results file is refused in one sentence naming it ({got}: "
                  f"{what if got == 'refused' else 'the reel planned without it'})")
        got, what = reel_with(tmp / "none", [])
        check("CR4-none-given", got == "planned" and (what or {}).get("status") == "planned",
              f"a plan given no capture results still plans its reel ({got})")
        got, what = reel_with(tmp / "empty", [str(empty)])
        check("CR5-readable-empty", got == "planned" and (what or {}).get("status") == "planned",
              f"a readable empty capture-results list still plans its reel ({got})")
    failed = [n for n, ok, _ in RESULTS if not ok]
    print(f"{len(failed)} failure(s) of {len(RESULTS)}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
