"""Write rows.json: the known answers for the swept rows, the derived rows and the pipeline's bundle stage, computed by
evalrun.py and evalpipeline.py.

Every expected value comes from the real modules: sweep_parts, sweep_arms_allowed and sweep_needed over ledger states;
conflictdiff_refusal over job logs; `execute` over synthetic run folders with the order sweep child stood in for (it
writes a launch folder of job records, emits, logs and a summary, and returns the runner's result), which exercises the
sweep block of execute, run_ordersweep, split_sweep_receipts and run_derived and leaves receipts, a ledger and a journal;
and stage_bundle with its three children stood in for. The clock is replaced by a counter. Run it again only when one of
those rules changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_rows.py

Nothing outside a temporary folder is read or written except the harness sources and the output.
"""
from __future__ import annotations

import copy
import json
import os
import platform
import re
import shutil
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run   # noqa: E402
import make_known_answers_finalize as F   # noqa: E402  (its plan, builder and masking helpers)
import make_known_answers_evaluate as V   # noqa: E402  (its evaluation plan and emits)
from make_known_answers_finalize import E   # noqa: E402

sys.path.insert(0, str(F.HARNESS))
import evalpipeline as P   # noqa: E402

OUT = HERE / "rows.json"
STEM = "candidate-01-flip"
MTIME = re.compile(r'"mtimeUtc": "[^"]+"')
PLUGINS = "P:/plugins/"   # the same on every machine, so an emit's bytes do not depend on the temporary folder


class Stop(Exception):
    """The stand-in was asked for a sweep the case did not expect."""


def sweep_check(cid, probe, **more):
    return F.check_row(cid, "ordersweep-probe", exe=probe, evidence={"strength": "measured"}, **more)


def plan_of(root: Path, checks: list, *, arms=None, **over) -> dict:
    """V.eval_plan's plan with plugin paths that do not name the temporary folder, and any further arms."""
    plan = V.eval_plan(root, checks, **over)
    for c in plan["checks"]:
        c.setdefault("status", "planned")
    plan["explicitDecisions"]["exclude"] = {"p.order-evaluation": "left out by the caller"}
    for a in plan["arms"]:
        for p in a["requested"]["plugins"]:
            p["path"] = PLUGINS + p["name"]
    for p in plan["configuration"]["plugins"]:
        p["path"] = PLUGINS + p["name"]
    for i, extra in enumerate(arms or [], start=2):
        arm = copy.deepcopy(plan["arms"][1])
        arm["armId"] = f"candidate-{i:02d}"
        arm["candidateFile"] = f"candidate-{i:02d}-x.json"
        arm["candidatePath"] = str(root / "candidates" / arm["candidateFile"])
        V.put(root, f"candidates/{arm['candidateFile']}", json.dumps({"Plugins": V.NAMES["candidate-01"], "n": i}))
        arm["candidateSha256"] = F.sha((root / "candidates" / arm["candidateFile"]).read_bytes())
        arm.update(extra)
        plan["arms"].append(arm)
    return plan


def stem_of(plan: dict, aid: str) -> str:
    return Path(next(a["candidateFile"] for a in plan["arms"] if a["armId"] == aid)).stem


def emit_text(root: Path, probe: str, arm: str = "baseline", conflicts=(), **more) -> str:
    doc = V.emit_doc(root, probe, arm, conflicts=conflicts, **more)
    for p in doc["Configuration"]["Plugins"]:
        p["Path"] = PLUGINS + p["Name"]
    return json.dumps(doc, indent=1)


class Launch:
    """One stand-in sweep launch: job records, emits, logs and summary rows written into the launch folder."""

    def __init__(self, root: Path, inv: Path):
        self.root, self.inv, self.jobs, self.cands = root, inv, [], {}

    def probe(self, probe, stem=None, *, reverse=False, outcome="ran", exit=0, text=None, written=True, record=True,
              stopped=None, stop=None, reused_from=None, log="probe ran\n", conflicts=(), sha=None, base="auto"):
        """A probe job: the baseline's (stem None), a candidate's forward, or its reverse."""
        name = "baseline" if stem is None else stem + (".reverse" if reverse else "")
        job = f"{probe}-" + ("baseline" if stem is None else stem + ("-reverse" if reverse else ""))
        emit = self.inv / probe / f"{name}.json"
        source = Path(reused_from).parent / probe / f"{name}.json" if reused_from else emit
        if written and outcome != "stopped":
            forward = stem is not None and not reverse
            F.write(self.root, str(source.relative_to(self.root)), text if text is not None else
                    emit_text(self.root, probe, "candidate-01" if forward else "baseline", conflicts,
                              source=f"candidate:{stem}.json" if forward else None))
        logf = self.inv / "logs" / f"{job}.txt"
        if log is not None and not reused_from:
            F.write(self.root, str(logf.relative_to(self.root)), log)
        if base == "auto":
            base = None if stem is None else str(self.inv / probe / (f"{stem}.json" if reverse else "baseline.json"))
        if record:
            self.jobs.append({"job": job, "kind": "probe", "outcome": outcome, "exitCode": exit, "stopped": stopped, "stop": stop,
                              "emit": str(source), "emitWritten": written and outcome != "stopped",
                              "emitSha256": sha or (F.sha(source.read_bytes()) if source.exists() else None),
                              "reusedFrom": reused_from, "log": str(logf), "inputs": {"order": "ignored", "base": base}})
        return emit

    def diff(self, probe, stem, *, outcome="ran", exit=0, written=True, record=True, row=True, verdict="DIFFERENTIAL-INERT",
             counts=None, created=None, log="verdict line\n", reverse=True, stopped=None, stop=None, word="PASSES-COMPLETED-CHECKS"):
        job = f"diff-{probe}-{stem}"
        logf = self.inv / "logs" / f"{job}.txt"
        if log is not None:
            F.write(self.root, str(logf.relative_to(self.root)), log)
        if record:
            self.jobs.append({"job": job, "kind": "diff", "outcome": outcome, "exitCode": exit, "stopped": stopped, "stop": stop,
                              "emit": str(logf), "emitWritten": written and log is not None, "log": str(logf),
                              "inputs": {"base": str(self.inv / probe / "baseline.json"), "arm": str(self.inv / probe / f"{stem}.json"),
                                         "reverse": str(self.inv / probe / f"{stem}.reverse.json") if reverse else None}})
        cand = self.cands.setdefault(stem, {"classification": word, "rows": []})
        if row:
            cand["rows"].append({"probe": probe, "diff": {"verdict": verdict, "counts": counts if counts is not None else
                                                          {"repaired": 0, "reclassified": 0, "created": 0, "unmeasured": 0}},
                                 "created": created if created is not None else {"created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}})

    def whole(self, probe, stems, **diff):
        """Every job of a probe for the baseline and each candidate, all accepted."""
        self.probe(probe)
        for stem in stems:
            self.probe(probe, stem)
            self.probe(probe, stem, reverse=True)
            self.diff(probe, stem, **diff)

    def close(self, *, summary=True, log="$ ordersweep\n(exit 0, 1.0s)\n\n"):
        F.write(self.root, str((self.inv / "jobs.jsonl").relative_to(self.root)),
                "".join(json.dumps(j) + "\n" for j in self.jobs) + "{broken\n")
        if summary:
            F.write(self.root, str((self.inv / "summary.json").relative_to(self.root)), json.dumps({"candidates": self.cands}))
        F.write(self.root, str((self.inv / "cache.json").relative_to(self.root)), "{}")
        if log is not None:
            F.write(self.root, "logs/ordersweep.txt", log)


class Runner:
    """In place of the shared runner: the case's stand-in writes the launch folder and names the result."""

    def __init__(self, root: Path, fake):
        self.root, self.fake, self.argv, self.env, self.result, self.wrote = root, fake, None, None, None, []

    def run(self, jobs, echo=True):
        if self.fake is None:
            raise Stop("a sweep child was started")
        job = jobs[0]
        self.argv, self.env = job["argv"], job.get("env")
        inv = Path(self.argv[self.argv.index("--out") + 1])
        before = {p: p.stat().st_mtime_ns for p in self.root.rglob("*") if p.is_file()}
        self.result = dict({"id": job["id"], "exitCode": 0, "seconds": 1.0, "runId": "sweep-run", "stopped": None}, **(self.fake(self.root, inv) or {}))
        for p in sorted(self.root.rglob("*")):
            if p.is_file() and before.get(p) != p.stat().st_mtime_ns:
                rel = p.relative_to(self.root).as_posix()
                self.wrote.append({"path": rel, "text": F.pack(F.mask(p.read_bytes().decode("utf-8"), self.root))})
        return [self.result]


class Parent:
    def __init__(self):
        self.n = 0

    def advance(self):
        self.n += 1


def state(root: Path) -> dict:
    """What a step left: every receipt, the ledger and the journal as text with the folder masked, and the file list."""
    def text(p):
        return MTIME.sub('"mtimeUtc": "<mtime>"', F.mask(p.read_text(encoding="utf-8"), root))
    return {"receipts": {p.stem: F.pack(text(p)) for p in sorted((root / "receipts").glob("*.json"))},
            "ledger": F.pack(text(root / "ledger.json")), "journal": F.pack(text(root / "journal.jsonl")),
            "files": sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() and "receipts" not in p.parts)}


def run_step(root: Path, plan: dict, n: int, name: str, fake, *, budget=None, drift=None, before=None) -> dict:
    """One invocation of execute over the folder, the sweep child stood in for by `fake` (None: none may start)."""
    if before:
        before(root)
    step = {"name": name, "budget": budget, "drift": drift, "edits": V.snapshot(root) if before else None}
    real = E.measure_program
    with F.fake_clock(), F.captured() as (out, _err):
        led = E.Ledger(root, plan)
        led.open_invocation("run", f"run-{n}", None)
        ex = E.Executor(root, plan, led, resume=False, budget_minutes=budget)
        ex.parent_run_id = f"parent-{n}"
        ex.runner = runner = Runner(root, fake)
        ex.preflight = copy.deepcopy(plan["toolIdentity"]["programs"])
        launched = []

        def measure(_opts, _harness, pid):
            now = dict(plan["toolIdentity"]["programs"][pid])
            if drift and pid == drift and runner.result is not None:
                now["digest"] = "folder:" + "d" * 64   # it changed while the sweep ran
            launched.append(pid)
            return now
        E.measure_program = measure
        parent = Parent()
        try:
            E.execute(ex, parent)
            step["raised"] = None
        except E.ToolChanged as error:
            step["raised"] = str(error)
        finally:
            E.measure_program = real
    argv = list(runner.argv[2:]) if runner.argv else None
    if argv and "--deadline-seconds" in argv:
        argv[argv.index("--deadline-seconds") + 1] = "<seconds>"
    step.update(argv=F.mask(argv, root), env=runner.env, result=runner.result, wrote=runner.wrote, measured=launched,
                advanced=parent.n, stdout=F.mask(out.getvalue(), root), children=ex.children, deadlineStops=ex.deadline_stops,
                want=state(root))
    return step


CASES = []


def case(tmp: Path, name: str, checks, steps, *, settle=None, tweak=None, **plan_over):
    """A run folder, then the steps over it one after the other: (name, stand-in, options)."""
    root = tmp / f"x{len(CASES)}"
    root.mkdir()
    plan = plan_of(root, copy.deepcopy(checks), **plan_over)
    if tweak:
        tweak(plan)
    with F.fake_clock():
        b = F.Builder(root, plan)
        if settle:
            settle(b, root)
    row = {"name": name, "tree": V.snapshot(root), "steps": []}
    for n, (step_name, fake, opts) in enumerate(steps, start=2):
        row["steps"].append(run_step(root, plan, n, step_name, fake, **opts))
    CASES.append(row)


MASTERS = dict(F.check_row("l.masters", "probe-json", exe="depprobe"), evidence={"strength": "measured"})
# a prerequisite execute never touches (a derived check the plan holds back), so a case fixes its ledger entries
FIXED = dict(F.check_row("l.masters", "derived"), status="held")
ORDER = F.check_row("p.order", "derived", arms=("baseline", "candidate-01"), prerequisites=["l.masters"])
ORDER["adapter"]["derives"] = "order-evaluation"
QUAL = F.check_row("p.candidate-qualification", "derived")
ODD = F.check_row("p.odd", "derived")
ODD["adapter"]["derives"] = "something-else"
DEADLINE = {"exitCode": None, "stopped": "deadline", "seconds": 61.25, "treeStopped": True, "runId": "sweep-run"}


def masters_ok(b, _root, arms=("baseline", "candidate-01")):
    for aid in arms:
        b.settle("l.masters", aid, None, "completed", detector="no-finding")


def execute_cases(tmp: Path) -> list:
    two = [FIXED, sweep_check("g.overlap", "overlapprobe", prerequisites=["l.masters"]), sweep_check("g.contact", "contactprobe"),
           ORDER, QUAL, ODD]

    def fresh(root, inv):
        launch = Launch(root, inv)
        launch.whole("overlapprobe", [STEM], verdict="DIFFERENTIAL", counts={"repaired": 2, "reclassified": 1, "created": 1, "unmeasured": 0},
                     created={"created": 1, "newDefect": 1, "attributed": 0, "unmeasured": 0}, word="REGRESSES")
        launch.whole("contactprobe", [STEM], word="REGRESSES")
        launch.close()

    def again(root, inv):
        # the same emits served from the first launch's cache, the diff run again; contact's forward emit has other bytes
        launch = Launch(root, inv)
        first = str(root / "sweep" / "inv-0001" / "cache.json")
        launch.probe("overlapprobe", outcome="reused", reused_from=first)
        launch.probe("overlapprobe", STEM, outcome="reused", reused_from=first)
        launch.probe("overlapprobe", STEM, reverse=True, outcome="reused", reused_from=first)
        launch.diff("overlapprobe", STEM)
        launch.probe("contactprobe")
        launch.probe("contactprobe", STEM, conflicts=[{"Key": "000801:A.esp"}])
        launch.probe("contactprobe", STEM, reverse=True)
        launch.diff("contactprobe", STEM, verdict="DIFFERENTIAL", counts={"repaired": 0, "reclassified": 0, "created": 2, "unmeasured": 0})
        F.write(root, str((inv / "cache-log.txt").relative_to(root)), "cache: HIT\n")
        launch.close()

    def drop_diff(root):
        # an accepted diff whose receipt is gone is measured again by the next launch
        led = json.loads((root / "ledger.json").read_text(encoding="utf-8"))
        for key in ("g.overlap__candidate-01__diff", "g.contact__candidate-01__arm"):
            led["entries"][key]["status"] = "rejected(output-missing)"
        (root / "ledger.json").write_text(json.dumps(led, indent=1), encoding="utf-8")

    case(tmp, "two probes measured and accepted; run again; then two parts repaired from the cache", two,
         [("a fresh sweep: every part accepted, the order evaluation finds a new defect", fresh, {}),
          ("nothing to settle: every arm reused, no child", None, {}),
          ("two parts no longer accepted: a launch serves the cache, keeps what stands and measures the rest", again, {"before": drop_diff})],
         settle=masters_ok)

    # every way one candidate's parts can end, one candidate arm per way, one probe
    ways = ["no-arm-record", "arm-exit-3", "arm-stopped", "reverse-uncontained", "diff-refused", "diff-usage", "diff-no-log", "diff-no-row",
            "no-verdict", "unjudged", "created-unmeasured", "counts-unmeasured", "counts-created", "inert", "reverse-finds",
            "diff-stopped", "arm-not-launched", "no-reverse-record", "no-diff-record", "reverse-exit-1", "diff-no-reverse-input",
            "arm-refusal-marker"]
    many = [sweep_check("g.overlap", "overlapprobe", arms=["baseline"] + [f"candidate-{i:02d}" for i in range(1, len(ways) + 1)]),
            F.check_row("p.order-evaluation", "derived", arms=["baseline"] + [f"candidate-{i:02d}" for i in range(1, len(ways) + 1)]), QUAL]

    def each_way(root, inv, plan_arms=len(ways)):
        launch = Launch(root, inv)
        pr = "overlapprobe"
        launch.probe(pr)
        stop = {"seconds": 12.5, "treeStopped": True, "refused": None, "launchedExitCode": None}
        for i, way in enumerate(ways, start=1):
            stem = STEM if i == 1 else f"candidate-{i:02d}-x"
            if way == "no-arm-record":
                continue
            if way == "arm-exit-3":
                launch.probe(pr, stem, exit=3, written=False, log="REFUSED: MISSING MASTER X.esm\n")
                launch.probe(pr, stem, reverse=True)
                launch.diff(pr, stem)
                continue
            if way == "arm-stopped":
                launch.probe(pr, stem, outcome="stopped", exit=None, stopped="deadline", stop=dict(stop, launchedExitCode=4294967295))
                continue
            if way == "arm-not-launched":
                launch.probe(pr, stem, outcome="stopped", exit=None, stopped="before-launch", stop={"seconds": 0.0, "treeStopped": None})
                continue
            if way == "arm-refusal-marker":
                launch.probe(pr, stem, log="   REFUSED: the order is not loadable\n", written=False)
                continue
            launch.probe(pr, stem)
            if way == "reverse-uncontained":
                launch.probe(pr, stem, reverse=True, outcome="stopped", exit=None, stopped="uncontained",
                             stop={"seconds": 0.0, "treeStopped": True, "refused": "AssignProcessToJobObject failed (5)"})
            elif way == "reverse-exit-1":
                launch.probe(pr, stem, reverse=True, exit=1, written=False)
            elif way == "reverse-finds":
                launch.probe(pr, stem, reverse=True, conflicts=[{"Key": "000801:A.esp"}, {"Key": "000802:A.esp"}])
            elif way != "no-reverse-record":
                launch.probe(pr, stem, reverse=True)
            if way == "diff-refused":
                launch.diff(pr, stem, exit=3, row=False, log="comparing\n   the two emits were made under different configurations:\n"
                                                              "   base 1111 arm 2222\n   REFUSED.\n")
            elif way == "diff-usage":
                launch.diff(pr, stem, exit=2, row=False, log="nothing\n! populations differ:\n     base load-time\n     arm flags\n! second\n"
                                                              "\n---- stderr ----\nusage: conflictdiff A B\n")
            elif way == "diff-no-log":
                launch.diff(pr, stem, log=None)
            elif way == "diff-no-row":
                launch.diff(pr, stem, row=False)
            elif way == "no-verdict":
                launch.diff(pr, stem, verdict="(no verdict line)")
            elif way == "unjudged":
                launch.diff(pr, stem, verdict="DIFFERENTIAL-UNJUDGED")
            elif way == "created-unmeasured":
                launch.diff(pr, stem, verdict="DIFFERENTIAL", created={"created": 2, "newDefect": 1, "attributed": 0, "unmeasured": 1})
            elif way == "counts-unmeasured":
                launch.diff(pr, stem, verdict="DIFFERENTIAL", counts={"repaired": 0, "reclassified": 0, "created": 0, "unmeasured": 3})
            elif way == "counts-created":
                launch.diff(pr, stem, verdict="DIFFERENTIAL", counts={"repaired": 1, "reclassified": 0, "created": 4, "unmeasured": 0})
            elif way == "diff-stopped":
                launch.diff(pr, stem, outcome="stopped", exit=None, stopped="deadline", stop=dict(stop, treeStopped=False), row=False)
            elif way == "no-diff-record":
                launch.diff(pr, stem, record=False, row=False)
            elif way == "diff-no-reverse-input":
                launch.diff(pr, stem, reverse=False)
            elif way in ("inert", "reverse-finds", "reverse-uncontained", "reverse-exit-1", "no-reverse-record"):
                launch.diff(pr, stem)
        launch.close()
        return {"exitCode": 1}
    case(tmp, "every way one candidate's parts end, one candidate per way", many,
         [("one launch: " + ", ".join(ways), each_way, {})], arms=[{} for _ in ways[1:]])

    # the baseline's own emit not accepted: nothing measured against it is credited
    def no_baseline(root, inv):
        launch = Launch(root, inv)
        launch.probe("overlapprobe", exit=1, written=False)
        launch.probe("contactprobe", text="{not json")
        launch.probe("contactprobe", STEM)
        launch.probe("contactprobe", STEM, reverse=True)
        launch.diff("contactprobe", STEM)
        launch.close(summary=False)
        return {"exitCode": 1}
    case(tmp, "a baseline whose emit is not accepted", two, [("the baseline job failed or wrote no emit", no_baseline, {})], settle=masters_ok)

    # --no-reverse, a scoped population, probe arguments and stand-in probes on the command line
    def forward_only(root, inv):
        launch = Launch(root, inv)
        launch.probe("overlapprobe")
        launch.probe("overlapprobe", STEM)
        launch.diff("overlapprobe", STEM, reverse=False)
        launch.close()

    over = {"population": {"scoped": True, "file": "population.json", "sha256": F.sha(b'{"records": []}'),
                           "scopedProbes": ["overlapprobe", "navownprobe"]}}

    def put_population(b, root):
        V.put(root, "population.json", '{"records": []}')
        masters_ok(b, root)

    def options(plan):
        plan["options"].update(noReverse=True, probeArgs="--lean --depth 2", diffTolerance=0.5, instance="I:/inst", game="G:/game",
                               probeCmd={"overlapprobe": "python fake.py", "conflictdiff": "python diff.py"})
    case(tmp, "--no-reverse with a scoped population and every option the sweep is handed", two[:2] + [ORDER],
         [("forward and diff only", forward_only, {}), ("again: nothing to settle", None, {}),
          ("the population file changed: nothing is launched",
           None, {"before": lambda root: ((root / "receipts" / "g.overlap__candidate-01__diff.json").unlink(), _unsettle(root, "g.overlap__candidate-01__diff"),
                                           (root / "population.json").write_text('{"records": [1]}', encoding="utf-8"))})],
         settle=put_population, tweak=options, **over)

    # arms the plan does not measure, and arms a prerequisite keeps out
    rows = [{"armId": "baseline", "status": "planned"}, {"armId": "candidate-01", "status": "planned"}, {"armId": "candidate-02", "status": "skipped-budget"},
            {"armId": "candidate-03", "status": "blocked-payload-missing"}, {"armId": "candidate-04", "status": "excluded"},
            {"armId": "candidate-05", "status": "not-applicable"}, {"armId": "candidate-06", "status": "unsupported-for-arm"},
            {"armId": "candidate-07", "status": "planned"}, {"armId": "candidate-08", "status": "planned"}, {"armId": "candidate-09", "status": "planned"}]
    kept = [FIXED, dict(sweep_check("g.overlap", "overlapprobe", prerequisites=["l.masters"]), arms=copy.deepcopy(rows)),
            dict(F.check_row("p.order-evaluation", "derived"), arms=copy.deepcopy(rows))]
    kept[0] = dict(kept[0], arms=[{"armId": r["armId"], "status": "planned"} for r in rows])

    def kept_settle(b, root):
        for r in rows:
            if r["armId"] not in ("candidate-07",):
                b.settle("l.masters", r["armId"], None, "completed", detector="no-finding")
        b.settle("l.masters", "candidate-07", None, "rejected(exit-nonzero)", cause="exit-nonzero", accepted=False)
        (root / "candidates" / "candidate-08-x.json").write_text("{}", encoding="utf-8")   # no longer the plan's order
        (root / "candidates" / "candidate-09-x.json").unlink()

    def kept_fake(root, inv):
        launch = Launch(root, inv)
        launch.whole("overlapprobe", [STEM])
        launch.close()
    case(tmp, "arms the plan does not measure, an arm its prerequisite keeps out, a changed and a missing candidate file", kept,
         [("one planned candidate is measured; every other arm keeps its own cause", kept_fake, {}),
          ("again: nothing to measure, the kept-out arm named", None, {})],
         settle=kept_settle, arms=[{"supplied": {"payloadMissing": ["Gone.esp", "Also.esp"]}} if i == 3 else {} for i in range(2, 10)])

    # the baseline's prerequisite unsettled, and the baseline's candidate file changed
    def base_blocked(b, _root):
        b.settle("l.masters", "baseline", None, "rejected(exit-nonzero)", cause="exit-nonzero", accepted=False)
        b.settle("l.masters", "candidate-01", None, "completed", detector="no-finding")
        b.settle("g.overlap", "candidate-01", "arm", "completed", detector="no-finding")
    case(tmp, "a baseline whose prerequisite failed keeps every arm out", two[:2] + [ORDER],
         [("nothing is launched; a part settled earlier is blocked too", None, {})], settle=base_blocked)
    case(tmp, "a baseline candidate file that changed", two[:2],
         [("nothing is launched", None, {"before": lambda root: (root / "candidates" / "candidate-00-baseline.json").write_text("{}", encoding="utf-8")})],
         settle=masters_ok)

    # stops
    def stopped_part(root, inv):
        launch = Launch(root, inv)
        launch.whole("overlapprobe", [STEM])
        launch.probe("contactprobe")
        launch.probe("contactprobe", STEM)
        launch.close()
        return dict(DEADLINE)

    def finish_contact(root, inv):
        launch = Launch(root, inv)
        launch.whole("contactprobe", [STEM])
        launch.close()
    case(tmp, "a sweep stopped at the deadline, then resumed", two,
         [("one probe's jobs all finished and are credited; the other's parts are stopped", stopped_part, {}),
          ("the next launch settles the rest and reads the first launch's cache", finish_contact, {})], settle=masters_ok)
    case(tmp, "a sweep the runner would not start, and one it could not contain", two[:3],
         [("refused by the deadline: no launch folder is kept", lambda root, inv: {"exitCode": None, "stopped": "before-launch", "runId": None}, {}),
          ("not contained", lambda root, inv: {"exitCode": None, "stopped": "uncontained", "refused": "CreateJobObject failed (5)", "runId": "sweep-2"}, {}),
          ("stopped at the deadline, its tree not confirmed gone, the launched process ended",
           lambda root, inv: dict(DEADLINE, treeStopped=False, launchedExitCode=3), {})], settle=masters_ok)
    case(tmp, "a budget that is gone before the sweep", two[:3] + [ORDER], [("every row skipped for the budget", None, {"budget": 1e-9})], settle=masters_ok)

    # a program that changed while the sweep ran
    case(tmp, "a probe that changed while the sweep ran", two[:3],
         [("its parts are rejected, the other probe's stand, and the run stops", fresh, {"drift": "probe:contactprobe"})], settle=masters_ok)
    case(tmp, "the comparator changed while the sweep ran", two[:2],
         [("the diff is rejected", fresh_one, {"drift": "helper:conflictdiff"})], settle=masters_ok)

    # the derived rows: qualification over supplied files and discovery hits
    def discovery(b, root):
        masters_ok(b, root)
        a_sha, t_sha = V.plugin_sha("A.esp"), V.plugin_sha("Target.esp")
        hits = {"schemaVersion": 1, "hits": [
            {"file": "Patch-A.esp", "kind": "pool", "sha256": a_sha.upper(), "masters": ["Target.esp"]},
            {"file": "Patch-A.esp", "kind": "pool", "sha256": a_sha, "masters": ["target.ESP"]},
            {"file": "A.esp", "kind": "pool", "sha256": "e" * 64, "masters": [], "detail": {"pair": ["A.esp", "B.esp"]}},
            {"file": "Lead.esp", "kind": "fomod", "sha256": "f" * 64, "detail": {"conditions": ["B.esp"]}},
            {"file": "Unhashed.esp", "kind": "pool", "sha256": "short", "unhashedReason": "inside a nested archive", "masters": ["A.esp"]},
            {"file": "NoReason.esp", "masters": ["A.esp"]},
            {"file": "Elsewhere.esp", "kind": "pool", "sha256": t_sha, "masters": ["Unrelated.esm"]},
            {"file": "", "masters": ["A.esp"]}, "odd"]}
        F.write(root, "outputs/d.pool/baseline.json", json.dumps(hits))
        F.write(root, "outputs/d.pool/candidate-01.json", json.dumps(hits))
        F.write(root, "outputs/d.fomod/baseline.json", json.dumps({"schemaVersion": 2, "hits": []}))
        F.write(root, "outputs/d.fomod/candidate-01.json", "changed after the receipt")
        consumed = {"configuration": {"plugins": [{"name": "mods/X/A.esp", "sha256": a_sha}, {"name": "Target.esp", "sha256": t_sha},
                                                  {"name": "B.esp", "sha256": "not a hash"}, {"sha256": "9" * 64}]}}
        pool = {"kind": "probe-json", "exe": "poolprobe"}
        b.settle("d.pool", "baseline", None, "completed", detector="catalogue", outputs=["outputs/d.pool/baseline.json"], adapter=pool, consumed=consumed)
        b.settle("d.pool", "candidate-01", None, "completed", detector="catalogue", outputs=["outputs/d.pool/candidate-01.json"], adapter=pool)
        fomod = {"kind": "probe-json", "exe": "fomodprobe"}
        b.settle("d.fomod", "baseline", None, "completed", detector="catalogue", outputs=["outputs/d.fomod/baseline.json"], adapter=fomod)
        b.settle("d.fomod", "candidate-01", None, "completed", detector="catalogue", outputs=["outputs/d.fomod/candidate-01.json"], adapter=fomod)
        F.write(root, "outputs/d.fomod/candidate-01.json", "changed after the receipt, again")
        b.settle("d.none", "baseline", None, "completed", detector="catalogue", adapter={"kind": "patchprobe", "exe": "patchprobe"})
        b.settle("d.gone", "baseline", None, "completed", detector="catalogue", outputs=["outputs/d.gone/baseline.json"], adapter=pool)
        F.write(root, "candidates/candidates.json", json.dumps({"Candidates": [{"File": "sub/candidate-01-flip.json"}, {"File": ""}, {}]}))

    def supplied(plan):
        arm = plan["arms"][1]
        arm["supplied"] = {"declaredFiles": ["mods/P/A.esp", "Target.esp", "Other.esp", "Named.esp"]}
        arm["requested"]["plugins"] += [{"name": "Named.esp", "sha256": None, "unstatedReason": "the payload was not unpacked"},
                                        {"name": "x/Other.esp", "sha256": "0" * 64}]
    def unsupported(row, **more):
        return dict(row, arms=[{"armId": a, "status": "unsupported"} for a in ("baseline", "candidate-01")], **more)
    disc = [FIXED, F.check_row("d.pool", "probe-json", exe="poolprobe"), F.check_row("d.fomod", "probe-json", exe="fomodprobe"),
            unsupported(F.check_row("d.patch", "patchprobe", exe="patchprobe"), status="unsupported"),
            unsupported(F.check_row("d.unstated", "patchprobe", exe="patchprobe"), status=None), QUAL, ODD,
            dict(F.check_row("p.blocked-qualification", "derived", prerequisites=["l.none"]), adapter={"kind": "derived", "derives": "candidate-qualification"})]
    case(tmp, "candidate qualification over supplied files and discovery hits", disc,
         [("qualified by bytes, unqualified, catalogued, not relevant, unreadable hit lists", None, {}), ("again: reused", None, {})],
         settle=discovery, tweak=supplied)
    return CASES


def fresh_one(root, inv):
    launch = Launch(root, inv)
    launch.whole("overlapprobe", [STEM])
    launch.close()


def _unsettle(root: Path, key: str):
    led = json.loads((root / "ledger.json").read_text(encoding="utf-8"))
    led["entries"][key]["status"] = "rejected(output-missing)"
    (root / "ledger.json").write_text(json.dumps(led, indent=1), encoding="utf-8")


# ------------------------------------------------------------------------------------------ small tables

def small_cases(tmp: Path) -> dict:
    """sweep_parts, sweep_arms_allowed and sweep_needed over ledger states of one small plan."""
    checks = [FIXED, sweep_check("g.overlap", "overlapprobe", prerequisites=["l.masters"]), sweep_check("g.contact", "contactprobe")]
    rows = []

    def row(name, settle, no_reverse=False, edit=None):
        root = tmp / f"s{len(rows)}"
        root.mkdir()
        plan = plan_of(root, copy.deepcopy(checks))
        plan["options"]["noReverse"] = no_reverse
        with F.fake_clock():
            b = F.Builder(root, plan)
            settle(b)
        if edit:
            edit(root)
        ex = E.Executor(root, plan, E.Ledger(root, plan), resume=False)
        sweep = [c for c in plan["checks"] if c["adapter"]["kind"] == "ordersweep-probe"]
        allowed = E.sweep_arms_allowed(ex, sweep)
        rows.append({"name": name, "tree": V.snapshot(root), "allowed": allowed, "neededAll": E.sweep_needed(ex, sweep, {}),
                     "needed": E.sweep_needed(ex, sweep, allowed),
                     "parts": {a["armId"]: list(E.sweep_parts(ex, a)) for a in plan["arms"]}})

    def parts(b, cid, aid, *names):
        for part in names:
            b.settle(cid, aid, part, "completed", detector="no-finding")

    def done(b, skip=()):
        for cid in ("g.overlap", "g.contact"):
            parts(b, cid, "baseline", "arm")
            parts(b, cid, "candidate-01", *[p for p in ("arm", "reverse", "diff") if (cid, p) not in skip])
    row("nothing settled, the prerequisite absent", lambda b: None)
    row("the prerequisite accepted on both arms", lambda b: masters_ok(b, None))
    row("the prerequisite accepted on the baseline only", lambda b: masters_ok(b, None, ("baseline",)))
    row("the prerequisite failed on the candidate", lambda b: (masters_ok(b, None, ("baseline",)),
                                                              b.settle("l.masters", "candidate-01", None, "rejected(exit-nonzero)", accepted=False)))
    row("the prerequisite failed on the baseline", lambda b: (b.settle("l.masters", "baseline", None, "failed", accepted=False),
                                                             masters_ok(b, None, ("candidate-01",))))
    row("the prerequisite not applicable", lambda b: b.settle("l.masters", "baseline", None, "not-applicable"))
    row("every part accepted", lambda b: (masters_ok(b, None), done(b)))
    row("one diff missing", lambda b: (masters_ok(b, None), done(b, skip=[("g.contact", "diff")])))
    row("one reverse missing", lambda b: (masters_ok(b, None), done(b, skip=[("g.overlap", "reverse")])))
    row("one reverse missing under --no-reverse", lambda b: (masters_ok(b, None), done(b, skip=[("g.overlap", "reverse")])), no_reverse=True)
    row("a part missing on an arm whose candidate file changed", lambda b: (masters_ok(b, None), done(b, skip=[("g.overlap", "diff"), ("g.contact", "diff")])),
        edit=lambda root: (root / "candidates" / "candidate-01-flip.json").write_text("{}", encoding="utf-8"))
    row("a part reused, the rest completed", lambda b: (masters_ok(b, None), done(b), b.settle("g.overlap", "baseline", "arm", "reused", detector="no-finding")))
    return {"rows": rows}


def refusal_cases(_tmp) -> list:
    texts = ["", "nothing here\n",
             "comparing\n   the two emits were made under different configurations:\n   base 1111\n\n   arm 2222\n   REFUSED.\nafter\n",
             "REFUSED.\n", " one line REFUSED.  \n", "\ta\n\tb REFUSED.\n  later\n  second REFUSED.\n",
             "x\n! populations differ:\n     base load-time\n     arm flags\n   not deeper\n", "  ! indented\n     more\n\n     after a blank\n",
             "! first\n! second\n", "out\n---- stderr ----\n\n  usage: conflictdiff A B\nmore\n", "out\n---- stderr ----\n",
             "   REFUSED. not at the end\n", "a\r\n   b REFUSED.\r\n", "\n---- stderr ----\nonly stderr\n"]
    return [{"text": t, "want": E.conflictdiff_refusal(t)} for t in texts] + [{"text": None, "want": E.conflictdiff_refusal(None)}]


# ------------------------------------------------------------------------------------------ the bundle stage

def bundle_stage_cases(tmp: Path) -> list:
    """stage_bundle with its children stood in for: the command lines it starts, what it refuses, what it writes."""
    cases = []
    real_child, real_env = P.child, os.environ.get("HOUSECARL_RECORD_READER_CMD")

    def case(name, *, exits=(0, 0, 0), machine=None, mod_names=None, exists=False, log="names: enriched 2 plugins\n", env_reader=None):
        out = tmp / f"b{len(cases)}" / "run"
        (out / "evaluation").mkdir(parents=True)
        F.write(out, "evaluation/disposition.json", json.dumps({"evaluation": {"claim": "findings", "complete": False}, "ledgerCounts": {"completed": 3}}))
        if exists:
            (out / "bundle" / "evidence").mkdir(parents=True)
        entry = {"schema": 1, "id": "m", "inPlace": {"instance": "I:/i", "profile": "Default", "game": "G:/g"}, "selection": {"mods": ["S"]}}
        if mod_names is not None:
            entry["modNames"] = mod_names
        F.write(out.parent, "entry.json", json.dumps(entry))
        F.write(out.parent, "machine.json", json.dumps(machine if machine is not None else {"readerCmd": '"R:/reader.exe" --stdio'}))
        ctx = P.Context(out.parent / "entry.json", out.parent / "machine.json", out)
        started, codes = [], list(exits)

        def child(_ctx, ledger_dir, op, argv, **kw):
            started.append({"op": op, "dir": F.mask(str(ledger_dir), out.parent), "argv": F.mask([str(a) for a in argv[2:]], out.parent), "more": sorted(kw)})
            if op == "bundle" and log is not None:
                F.write(Path(ledger_dir), "bundle.txt", "$ bundle\n(exit 0, 1.0s)\n\n" + log)
            return {"exitCode": codes[len(started) - 1]}
        P.child = child
        if env_reader is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = env_reader
        row = {"name": name, "exits": list(exits), "machine": machine, "modNames": mod_names, "exists": exists, "log": log, "envReader": env_reader}
        try:
            bound, outputs = P.stage_bundle(ctx, {}, out / "bundle-stage")
            row["bound"], row["outputs"], row["refusal"] = bound, F.mask([str(o) for o in outputs], out.parent), None
        except P.Refusal as error:
            row["refusal"] = F.mask(str(error), out.parent)
        finally:
            P.child = real_child
        record = out / "bundle-stage" / "bundle.json"
        row["started"] = started
        row["record"] = F.mask(record.read_text(encoding="utf-8"), out.parent) if record.exists() else None
        row["instance"] = sorted(p.relative_to(out).as_posix() for p in (out / "names-instance").rglob("*")) if (out / "names-instance").exists() else None
        cases.append(row)

    try:
        case("accepted: three children, the record names the claim")
        case("mod names from the entry, in its order", mod_names={"Sel.esp": "The Selection", "other.ESP": "Another = name"})
        case("finalize ending 3 is accepted", exits=(3, 0, 0))
        case("finalize ending 2 stops the stage", exits=(2, 0, 0))
        case("finalize that never ended stops the stage", exits=(None, 0, 0))
        case("bundle ending 2 stops the stage", exits=(0, 2, 0))
        case("bundle ending 3 stops the stage", exits=(0, 3, 0))
        case("verify-bundle ending 1 stops the stage", exits=(0, 0, 1))
        case("an earlier bundle folder", exists=True)
        case("no record reader named", machine={})
        case("the record reader named by the environment", machine={}, env_reader="E:/env-reader.exe")
        case("a names enrichment that was refused", log="names: started\nnames:   refused: the reader answered nothing for 3 plugins " + "x" * 400 + "\n")
        case("an earlier refused line, a later good one", log="names: refused once\nnames: enriched\n")
        case("no log of the bundle step", log=None)
    finally:
        P.child = real_child
        if real_env is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = real_env
    return cases


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_rows.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-rows", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-rows-")).resolve()
    os.environ["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")   # so no run folder here becomes the run-log store
    try:
        doc = {"python": platform.python_version(), "placeholders": {"backslash": F.BACK, "forward": F.FWD, "doubled": F.DOUBLED},
               "small": small_cases(tmp), "refusal": refusal_cases(tmp), "execute": execute_cases(tmp), "bundleStage": bundle_stage_cases(tmp)}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    text = json.dumps(doc, indent=1) + "\n"
    for leak in (tmp.name, Path.home().name):
        assert leak not in text, f"the answers would keep a local path ({leak})"
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
