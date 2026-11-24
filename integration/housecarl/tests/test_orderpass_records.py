"""Known answers for orderpass_records.py, a whole-order pass's records stage run over the pass folder orderpass.py
finished: the leads name the records check unassessed until it has read, then hold its rows.

    python integration/housecarl/tests/test_orderpass_records.py

Built on orderpass's own suite fixture (tests/test_orderpass.py: a synthetic map and stand-in probes). The record
reader is stood in at evalrecords.capture_arm: one lost-edit row between two of the pass's mods.

    OR1  before the record pass the leads name the records stage unassessed; after it, they do not, and the whole-order
         leads hold the lost-edit row; records.json and stages.json say so.
    OR2  no record reader: the stage is recorded refused with the reader's path, and the leads still name it unassessed.
    OR3  a folder orderpass never finished is refused in one sentence.
"""
from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402
sys.path.insert(0, str(SW.HARNESS / "tests"))
import orderleads as L  # noqa: E402
import orderpass as P  # noqa: E402
import orderpass_records as ORP  # noqa: E402
import test_orderpass as TP  # noqa: E402
import evalrecords as ER  # noqa: E402

ROW = {"Kind": "LOST-EDIT", "A": "ModB.esp", "B": "ModC.esp", "Necessary": [{"FormKey": "000801:ModB.esp"}],
       "Summary": "Quest MQ101: Stages - ModB.esp's edit lost to ModC.esp"}
FAILURES: list[str] = []


def check(name, ok, why):
    print(f"  {'PASS' if ok else 'FAIL'}  {name}: {why}")
    if not ok:
        FAILURES.append(name)


def unassessed_stages(out: Path) -> str:
    return json.dumps(L.build(out).unassessed.get("stages"))


def records_pass(out: Path, tmp: Path, reader: str | None):
    saved = ER.capture_arm, ER.write_view
    ER.capture_arm = lambda *a, **k: {"records.contested-fields": ([ROW], [{"Population": "records", "Judged": 1, "Unjudgeable": 0}])}
    ER.write_view = lambda *a, **k: None
    try:
        argv = ["--pass", str(out), "--instance", str(tmp / "instance"), "--profile", "P", "--game", str(tmp / "game")]
        if reader:
            argv += ["--reader", reader]
        with contextlib.redirect_stdout(io.StringIO()):
            return ORP.main(argv)
    finally:
        ER.capture_arm, ER.write_view = saved


def main() -> int:
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        with contextlib.redirect_stdout(io.StringIO()):
            code, out = TP.run(P, tmp)
        before = unassessed_stages(out)
        check("OR1-before", code == 0 and "records" in before, f"orderpass exit {code}; unassessed before the record pass: {before}")
        whole_before = (out / "LEADS-whole.md").read_text(encoding="utf-8")
        rc = records_pass(out, tmp, sys.executable)
        after = unassessed_stages(out)
        stages = json.loads((out / "stages.json").read_text(encoding="utf-8"))
        whole = (out / "LEADS-whole.md").read_text(encoding="utf-8")
        check("OR1-after", rc == 0 and "records" not in after and stages["records"].get("exit") == 0 and (out / "records.json").is_file(),
              f"exit {rc}; unassessed after: {after}; stage {stages.get('records')}")
        check("OR1-leads", "lost to ModC.esp" in whole and "lost to ModC.esp" not in whole_before,
              "the whole-order leads hold the lost-edit row only after the record pass")
    with tempfile.TemporaryDirectory() as t:
        tmp = Path(t)
        with contextlib.redirect_stdout(io.StringIO()):
            _, out = TP.run(P, tmp)
        missing = str(tmp / "absent" / "housecarl-mcp.exe")
        rc = records_pass(out, tmp, missing)
        st = json.loads((out / "stages.json").read_text(encoding="utf-8"))["records"]
        check("OR2", missing in str(st.get("refused") or "") and "records" in unassessed_stages(out),
              f"exit {rc}; the stage {st}; unassessed {unassessed_stages(out)}")
    with tempfile.TemporaryDirectory() as t:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = ORP.main(["--pass", t, "--instance", t, "--profile", "P", "--game", t, "--reader", sys.executable])
        said = err.getvalue().strip()
        check("OR3", rc == 2 and said.startswith("REFUSED: ") and "orderpass.py" in said and "\n" not in said, f"exit {rc}: {said}")
    print(f"\n{'all checks passed' if not FAILURES else f'{len(FAILURES)} failed: ' + ', '.join(FAILURES)}")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
