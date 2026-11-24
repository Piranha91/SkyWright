"""Known answers for a lab run's record capture (evalpipeline.capture_records), the step between runcontrol's evaluate
and its records stage: which arms it reads, the capture's command line and machine slot, which documents it imports
and in what order, what it writes, prints and refuses.

    python integration/housecarl/tests/test_record_capture.py

The recorded rows (fixtures/record_capture/records-stage.json) are the ones runcontrol's self-test held while the
capture was its records stage (known-answers/import.json, recordsStage); they were made from this step's Python before
the port. Each row runs the step with its children stood in for at `evalpipeline.child` (the capture writes the row's
summary, workers file and log; each import exits as the row says) and compares the children started, what was
printed, records.json, the folders made, and what the step returned or the sentence it refused with. Cases 58 and 59
are the facts of the capture's launch: finished arms kept, a per-arm deadline, a backstop that scales with the arms,
and the machine slot held for that backstop.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path

_spec = importlib.util.spec_from_file_location("evalpipeline_capture_under_test", HERE.parent / "evalpipeline.py")
EP = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(EP)

ROWS = json.loads((HERE / "fixtures" / "record_capture" / "records-stage.json").read_text(encoding="utf-8"))["recordsStage"]
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        why = fn()
        if why:
            raise AssertionError(why)
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


class Root:
    """A scratch folder, masked as runcontrol's self-test masked it: <ROOT\\> with backslashes, <ROOT/> with forward ones."""

    def __init__(self):
        self._t = tempfile.TemporaryDirectory(prefix="hc-capture-")
        self.path = Path(self._t.name).resolve()

    def mask(self, v):
        if isinstance(v, str):
            return v.replace(str(self.path), "<ROOT\\>").replace(EP.fwd(self.path), "<ROOT/>")
        if isinstance(v, list):
            return [self.mask(x) for x in v]
        if isinstance(v, dict):
            return {self.mask(k): self.mask(x) for k, x in v.items()}
        return v

    def close(self):
        self._t.cleanup()


def context(root: Root, entry: dict, machine: dict):
    (root.path / "entry.json").write_text(json.dumps(entry), encoding="utf-8")
    (root.path / "machine.json").write_text(json.dumps(machine), encoding="utf-8")
    return EP.Context(root.path / "entry.json", root.path / "machine.json", root.path / "run")


def same(what, got, want):
    return None if got == want else f"{what}: got {json.dumps(got, ensure_ascii=False)[:600]}, the recorded answer is {json.dumps(want, ensure_ascii=False)[:600]}"


def run_row(c: dict):
    root = Root()
    try:
        run, sdir = root.path / "run", root.path / "run" / "records"
        (run / "evaluation").mkdir(parents=True)
        (run / "evaluation" / "ledger.json").write_text(json.dumps({"entries": c["entries"]}), encoding="utf-8")
        sdir.mkdir(parents=True)
        for i in range(c["earlier"]):
            (sdir / f"capture-{i + 1:02d}").mkdir()
        # the capture's Python is a file of this test's own, so the row does not need one on this machine
        python = root.path / "python.exe"
        python.write_bytes(b"")
        machine = dict(c["machine"], python=str(python))
        ctx = context(root, c["entry"], machine)
        started, wrong = [], []
        imports = iter(c["importExits"])
        script = EP.fwd(SW.HARNESS / "evalrecords.py")
        controller = EP.fwd(EP.runcontrol_exe(machine))

        def child(ctx_, ledger_dir, op, argv, *, heavy=False, purpose=None, deadline_min=None, env=None, hold_min=None):
            more = {k: v for k, v in (("deadline_min", deadline_min), ("heavy", heavy), ("hold_min", hold_min), ("purpose", purpose)) if v}
            capture = op == "records-capture"
            if capture:
                if argv[0] != str(python):
                    wrong.append(f"the capture was started with {argv[0]}")
                if argv[1] != script:
                    wrong.append(f"the capture's script was {argv[1]}")
            elif argv[0] != controller:
                wrong.append(f"{op} was started with {argv[0]}, not runcontrol")
            started.append({"op": op, "dir": root.mask(str(ledger_dir)), "script": "evalrecords.py" if capture else "evalrun.py",
                            "argv": root.mask(list(argv[2:] if capture else argv[1:])), "more": more})
            if capture:
                cap = Path(argv[argv.index("--out") + 1])
                if c["summary"] is not None:
                    cap.mkdir(parents=True, exist_ok=True)
                    (cap / "capture-summary.json").write_text(json.dumps(c["summary"]).replace("{cap}", EP.fwd(cap)), encoding="utf-8")
                if c["workers"] is not None:
                    cap.mkdir(parents=True, exist_ok=True)
                    (cap / "capture-workers.json").write_text(json.dumps(c["workers"]), encoding="utf-8")
                if c["captureLog"] is not None:
                    Path(ledger_dir).mkdir(parents=True, exist_ok=True)
                    (Path(ledger_dir) / "records-capture.txt").write_text(c["captureLog"], encoding="utf-8")
                return {"exitCode": c["captureExit"]}
            if c["importLogs"] is True:
                Path(ledger_dir).mkdir(parents=True, exist_ok=True)
                (Path(ledger_dir) / f"{op}.txt").write_text("$ import\n(exit 0, 0.1s)\n\n", encoding="utf-8")
            return {"exitCode": next(imports)}
        saved, saved_env = EP.child, os.environ.get("HOUSECARL_RECORD_READER_CMD")
        if c["envReader"] is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = c["envReader"]
        out, refusal, got = io.StringIO(), None, (None, [])
        EP.child = child
        try:
            with contextlib.redirect_stdout(out):
                got = EP.capture_records(ctx, {}, sdir)
        except EP.Refusal as r:
            refusal = root.mask(str(r))
        finally:
            EP.child = saved
            if saved_env is None:
                os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
            else:
                os.environ["HOUSECARL_RECORD_READER_CMD"] = saved_env
        record = (sdir / "records.json").read_text(encoding="utf-8") if (sdir / "records.json").is_file() else None
        folders = sorted(EP.fwd(p.relative_to(run)) for p in sdir.rglob("*") if p.is_dir())
        why = (wrong[0] if wrong else None) or same("refusal", refusal, c["refusal"]) or same("children", started, c["started"]) \
            or same("printed", root.mask(out.getvalue()), c["printed"]) or same("records.json", root.mask(record), c["record"]) \
            or same("folders", folders, sorted(c["folders"]))
        if why or refusal is not None:
            return why
        return same("bound", got[0], c["bound"]) or same("outputs", root.mask([str(p) for p in got[1]]), c["outputs"])
    finally:
        root.close()


LAB = {"schema": 1, "id": "m", "subject": {"plugin": "S.esp", "mod": "MS"},
       "source": {"instance": "S:/src", "profile": "P"}, "lab": {"instance": "L:/lab", "game": "G:/game"}}


def launch(arms: int, more_machine: dict | None = None):
    """The capture's launch over `arms` open arms, through the real child() with the Runner stood in: (argv after any
    slot command, the slot command, the deadline in minutes)."""
    root = Root()
    try:
        run = root.path / "run"
        (run / "evaluation").mkdir(parents=True)
        entries = {f"row{i}__{'baseline' if i == 0 else f'candidate-{i:02d}'}": {"status": "mcp-only"} for i in range(arms)}
        (run / "evaluation" / "ledger.json").write_text(json.dumps({"entries": entries}), encoding="utf-8")
        (run / "records").mkdir()
        machine = {"readerCmd": "reader", "python": str(root.path / "python.exe"), **(more_machine or {})}
        ctx = context(root, LAB, machine)
        seen = {}

        class FakeRunner:
            def __init__(self, op, out_dir=None, deadline=None):
                self.op, self.deadline = op, deadline

            def run(self, jobs, echo=False):
                if self.op == "evalpipeline-records-capture":
                    seen["argv"], seen["deadline"] = list(jobs[0]["argv"]), self.deadline
                return [{"exitCode": 0}]
        saved = EP.Runner
        EP.Runner = FakeRunner
        t0 = EP.time.perf_counter()
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                EP.capture_records(ctx, {}, run / "records")
        except EP.Refusal:
            pass   # the stand-in writes no summary
        finally:
            EP.Runner = saved
        argv = seen.get("argv") or []
        cut = argv.index("--") if "heavyPrefix" in machine and "--" in argv else -1
        minutes = round((seen["deadline"] - t0) / 60) if seen.get("deadline") else None
        return argv[cut + 1:], argv[:max(cut, 0)], minutes
    finally:
        root.close()


def case_58():
    if not 60 <= EP.RECORDS_ARM_MINUTES <= 600:
        return f"a per-arm record deadline is set, got {EP.RECORDS_ARM_MINUTES}"
    one, _, one_min = launch(1)
    three, _, three_min = launch(3)
    at = three.index("--arm-deadline-min") if "--arm-deadline-min" in three else -1
    if "--checkpoints" not in three or at < 0 or three[at + 1] != str(EP.RECORDS_ARM_MINUTES):
        return "the records stage keeps finished arms and stops a hung arm: " + " ".join(three)
    return same("the backstop for one open arm", one_min, EP.RECORDS_ARM_MINUTES * 1 + 10) \
        or same("the backstop scales with the open arms", three_min, EP.RECORDS_ARM_MINUTES * 3 + 10) \
        or ("a fixed stage deadline" if three_min == 240 else None) \
        or same("one --arm for each open arm", three.count("--arm"), 3) or same("and one for one", one.count("--arm"), 1)


def case_59():
    want = EP.RECORDS_ARM_MINUTES * 3 + 10
    plain, _, plain_min = launch(3)
    if len(plain) < 3 or plain[2] != "capture":
        return "the records stage reads its arms through one child: " + " ".join(plain)
    _, slot, slot_min = launch(3, {"heavyPrefix": ["slot.exe", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "5"]})
    hold = slot.index("--max-min") if "--max-min" in slot else -1
    return (None if slot and slot[0] == "slot.exe" else "the records stage takes the machine slot: its capture was not started through the slot command") \
        or same("the slot is held for the stage's backstop", slot[hold + 1] if hold >= 0 else None, str(want)) \
        or same("the slot is asked for by the stage's purpose", slot[slot.index("--purpose") + 1], "evalpipeline m records") \
        or same("the stage's backstop", plain_min, want) \
        or same("under the slot the deadline sits past its waits and its hold", slot_min, 240 + want + EP.HOLD_MARGIN_MIN)


def case_capture_python():
    """The capture runs under the machine file's `python` when it names one, else this driver's own interpreter (the
    driver is Python, so there is no python.exe to look for on PATH)."""
    named, _, _ = launch(1, {"python": "N:/named/python.exe"})
    root = Root()
    try:
        ctx = context(root, LAB, {"readerCmd": "reader"})
        own = EP.capture_python(ctx)
    finally:
        root.close()
    return same("the machine file's python", named[0] if named else None, "N:/named/python.exe") or same("without one, this Python", own, sys.executable)


def case_measure_order():
    """A lab entry's census to bundle, as the module measures it: runcontrol through evaluate, the capture, runcontrol
    on through the bundle; a run that stops before the records measures with one runcontrol run; a runcontrol stop is
    refused with its own sentence. Wrong answers: the capture before evaluate, or after the bundle (the bundle would not
    hold the imports), or a second runcontrol run that is not asked to go on."""
    root = Root()
    try:
        run = root.path / "run"
        (run / "evaluation").mkdir(parents=True)
        (run / "evaluation" / "ledger.json").write_text(json.dumps({"entries": {}}), encoding="utf-8")
        ctx = context(root, LAB, {"readerCmd": "reader"})
        order = []

        def child(ctx_, ledger_dir, op, argv, **kw):
            order.append(op if op != "records-capture" else "capture")
            return {"exitCode": 0}
        saved_child, saved_capture = EP.child, EP.capture_records
        EP.child = child
        EP.capture_records = lambda c, r, s: order.append("capture") or ({}, [])
        try:
            EP.measure(ctx, "deliver")
            whole = list(order)
            order.clear()
            EP.measure(ctx, "plan")
            short = list(order)
            EP.child = lambda *a, **k: {"exitCode": 2}
            (run / "status.json").write_text(json.dumps({"sentence": "the census scope is incomplete."}), encoding="utf-8")
            try:
                EP.measure(ctx, "bundle")
                stopped = None
            except EP.Refusal as r:
                stopped = str(r)
        finally:
            EP.child, EP.capture_records = saved_child, saved_capture
    finally:
        root.close()
    return same("through the reel", whole, ["runcontrol-through-evaluate", "capture", "runcontrol-through-bundle"]) \
        or same("through the plan", short, ["runcontrol-through-plan"]) \
        or (None if stopped and "the census scope is incomplete." in stopped and "evaluate" in stopped else f"a runcontrol stop said: {stopped}")


def case_in_place_not_measured():
    """An in-place entry (the app's) is not measured by the module: the app carried it through the bundle."""
    import types
    calls = []
    saved = EP.MEASURE
    with tempfile.TemporaryDirectory() as t:
        entry = Path(t) / "entry.json"
        entry.write_text(json.dumps({"schema": 1, "id": "m", "inPlace": {"instance": t, "profile": "P", "game": t}}), encoding="utf-8")
        EP.MEASURE = lambda c, th: calls.append(th)
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
        try:
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                EP.cmd_run(types.SimpleNamespace(entry=str(entry), machine=None, out=str(Path(t) / "run"), through=None))
        finally:
            EP.MEASURE = saved
            os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
    return same("measured", calls, [])


def main() -> int:
    for row in ROWS:
        check(f"records stage: {row['name']}", lambda row=row: run_row(row))
    check("case 58 the records stage keeps finished arms and scales its deadline", case_58)
    check("case 59 the records stage takes the machine slot for its backstop", case_59)
    check("records stage: the capture's Python is the machine file's, else this driver's own", case_capture_python)
    check("measure: a lab entry runs runcontrol through evaluate, the capture, then runcontrol through the bundle", case_measure_order)
    check("measure: an in-place entry is not measured by the module", case_in_place_not_measured)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
