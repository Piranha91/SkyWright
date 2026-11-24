"""evalpipeline - the stages of one mod's evaluation after its evidence bundle, on the run folder the built-in
controller made.

    runcontrol run --entry ENTRY.json --out RUN [--machine MACHINE.json] --through bundle      (census to bundle)
    python _geometry-harness/evalpipeline.py run    --entry ENTRY.json --out RUN [--machine MACHINE.json] [--through STAGE]
    python _geometry-harness/evalpipeline.py status --out RUN

What this is for. Census to bundle (census, prepare, scope, plan, evaluate, records, scenes, bundle) are run by the
built-in controller, runcontrol (_geometry-harness/runcontrol), which derives the arms and their order from the entry and
writes RUN/stages.json, RUN/arms.json and RUN/prepare/preparation.json. This driver runs what follows on the same run
folder: precedent, report-plan, capture, image-observations, report, reel, deliver.

  stages    Each stage is a receipt in RUN/stages.json bound to the hashes of its inputs and outputs; `run` reuses an
            accepted stage whose bindings are unchanged and reruns one that changed. A stage from census to bundle is
            never run here: its receipt must be accepted and unchanged (the same entry, the same upstream), or the run
            stops with one sentence naming the runcontrol command; scenes, which runcontrol records refused and not
            blocking, carries its sentence on. A stage that exits 0 without its outputs is not accepted. The stages
            after the bundle also bind the code of the tools they run (STAGE_TOOLS: each script, the harness modules it
            imports, the renderer's build, and runcontrol's build for the report's media bundle), so a tool change runs
            that stage again.

What it does not do. It measures nothing itself and decides nothing about a mod: every finding comes from the probes,
the controller's ledger and the record reader. It never certifies absence on an empty read: a missing output or a stage
refusal stops that stage with one sentence, and the run stays `incomplete`. A precedent refusal does not block the
report or reel. Where a judgement must be made by a person or a model (blind image observations, the final reviews) the
run stops with that status and continues when the judgement file exists.

The machine file (machine-local, never tracked) names the lab instance, the game, a command prefix for heavy stages
(the machine's slot and idle queue), the built-in controller when it is not this checkout's build (runcontrol) and the
commands of run-local stages such as the precedent cards.
Exit codes: 0 the requested stages accepted or reused; 2 a refusal (one sentence on stderr); 3 the run stopped for a
judgement (status names it).
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
import shutil
import struct
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT / "scripts"))
sys.path.insert(0, str(HERE))
from runlog import Run, Runner  # noqa: E402
import buildlayout  # noqa: E402  where the build put runcontrol and viewprobe
# shared with the record capture and bundle enrichment outside the module: the same objects (evalbase)
from evalbase import (Refusal, sha256_file, fwd, _header_fields, plugin_header, plugin_records,  # noqa: E402,F401
                      RUNCONTROL_ENV, runcontrol_exe, verify_bundle_argv)

STAGES = ("census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle", "precedent",
          "report-plan", "capture", "image-observations", "report", "reel", "deliver")
NON_BLOCKING = frozenset({"precedent"})
#: entry fields that change only what is presented (names, patch search, report shape); measuring stages ignore them
PRESENTATION_KEYS = frozenset({"modNames", "nexusNames", "patchSearch", "report", "captureSize"})
#: census to bundle are the built-in controller's (runcontrol): this driver reuses their receipts and never runs them
MEASURING = STAGES[:STAGES.index("bundle") + 1]
#: a measuring stage the controller records refused and non-blocking, with no receipt; its sentence is carried on
MEASURING_NON_BLOCKING = frozenset({"scenes"})
#: the presentation keys each measuring stage reads, as runcontrol declares them (runcontrol/StageLedger.cs,
#: PresentationReads); a stage's entry hash binds them, so a receipt runcontrol wrote is reused only under the same rule
MEASURING_READS = {"bundle": ["modNames"]}
EXIT_OK, EXIT_REFUSED, EXIT_JUDGEMENT = 0, 2, 3
MAX_PATH = 240  # leave room under Windows' 260-character limit for the probes' own file names


# ---------------------------------------------------------------- small helpers

def read_json(p):
    return json.loads(Path(p).read_text(encoding="utf-8-sig"))


def write_json(p, obj):
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_name(p.name + ".tmp")
    tmp.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, p)


def sha256_json(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def same_path(a, b) -> bool:
    return os.path.normcase(os.path.normpath(str(a))) == os.path.normcase(os.path.normpath(str(b)))


def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# ---------------------------------------------------------------- plugin headers

# ---------------------------------------------------------------- plugin identity

class PluginNames:
    """One spelling per plugin. The game matches plugin names without regard to case, and headers often name a master
    in another case than the file ("ccbgssse001-fish.esm"); the first spelling seen is the plugin's name everywhere
    after: the entry's first, then the source profile's, then the headers'. Every name the runner compares passes
    through here once, when it is read."""

    def __init__(self):
        self._by = {}

    def __call__(self, p):
        return p if p is None else self._by.setdefault(str(p).lower(), str(p))

    def many(self, ps):
        return [self(p) for p in ps or []]


def normalise_entry(e: dict, name: PluginNames) -> dict:
    """The entry with every plugin name it holds in its one spelling (roles, baseline, companions, extra arms, order
    variants, places, mod names)."""
    e = json.loads(json.dumps(e))

    def role(r):
        if r.get("plugin") is not None:
            r["plugin"] = name(r["plugin"])
        if r.get("plugins") is not None:
            r["plugins"] = name.many(r["plugins"])
    for r in [e.get("subject") or {}] + list(e.get("partners") or []) + list(e.get("keys") or []) \
            + list(e.get("dependencies") or []) + list(e.get("companionsOf") or []):
        role(r)
    if "baseline" in e:
        e["baseline"] = name.many(e["baseline"])
    if e.get("baselineScopeTarget"):
        e["baselineScopeTarget"] = name(e["baselineScopeTarget"])
    if e.get("companions"):
        e["companions"] = {name(k): name.many(v) for k, v in e["companions"].items()}
    for x in e.get("extraArms") or []:
        x["plugins"] = name.many(x.get("plugins"))
    for v in e.get("orderVariants") or []:
        if "last" in v:
            v["last"] = name.many(v["last"])
    if e.get("places"):
        e["places"] = {name(k): dict(v, after=name(v.get("after"))) for k, v in e["places"].items()}
    if e.get("modNames"):
        e["modNames"] = {name(k): v for k, v in e["modNames"].items()}
    return e


# ---------------------------------------------------------------- the arm rule


# ---------------------------------------------------------------- the order rule


# ---------------------------------------------------------------- scope acceptance


# ---------------------------------------------------------------- stage receipts

#: the skill data and templates the report and reel words come from (evalreport's own compiler_hashes)
REPORT_DATA = ("_geometry-harness/evalreport_templates", "external/houseCARL/.claude/skills/evaluation-reporting/references/report-priorities.json",
               "external/houseCARL/.claude/skills/evaluation-reporting/references/scene-policy.json")
#: the code each stage after the bundle runs, repository-relative: the scripts it launches and those they load by path
#: (a harness module a listed script imports is followed by stage_tools itself). The measuring stages (census to
#: bundle) are not here: the controller's plan (runcontrol) freezes every program it launches and refuses a resume under a changed one, and
#: plan and bundle refuse to write over an earlier folder, so a measuring tool change is a new run folder.
STAGE_TOOLS = {
    "report-plan": ("_geometry-harness/evalreport.py", "_geometry-harness/evalreel.py", *REPORT_DATA),
    "capture": ("_geometry-harness/capture_acquire.py", "_geometry-harness/capture_points.py",
                "_geometry-harness/viewprobe/tools/shotplan.py", "_geometry-harness/evalrun.py",
                "_geometry-harness/evalreport.py", "_geometry-harness/evalreel.py", *REPORT_DATA),
    "report": ("_geometry-harness/evalreport.py", *REPORT_DATA),
    "reel": ("_geometry-harness/evalreel.py", "_geometry-harness/evalspeech.py", *REPORT_DATA),
}
#: stages whose renderer build is part of their tools (capture_acquire's own default build when the machine names none)
RENDERER_STAGES = frozenset({"capture"})
#: stages that start the built-in controller (the report's media bundle, and evalreport plan's bundle check in every stage
#: that runs it): its build is part of their tools
CONTROLLER_STAGES = frozenset({"report-plan", "capture", "report"})
#: this driver: modules a tool imports may import it, and its code is recorded on every receipt, not bound
DRIVER = "_geometry-harness/evalpipeline.py"


def _local_imports(path: Path, roots: list[Path]) -> list[Path]:
    """The harness modules a script imports anywhere in its text (a function-level import included), found in `roots`."""
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"))
    except (OSError, SyntaxError, ValueError):
        return []
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names |= {a.name.split(".")[0] for a in node.names}
        elif isinstance(node, ast.ImportFrom) and node.module and not node.level:
            names.add(node.module.split(".")[0])
    out = []
    for n in sorted(names):
        hit = next((r / f"{n}.py" for r in [path.parent, *roots] if (r / f"{n}.py").is_file()), None)
        if hit is not None:
            out.append(hit)
    return out


def _tool_digest(p: Path):
    if not p.exists():
        return None
    if p.is_file():
        return sha256_file(p)
    return "dir:" + sha256_json([[fwd(f.relative_to(p)), sha256_file(f)] for f in sorted(p.rglob("*")) if f.is_file()])


def stage_tools(stage: str, machine: dict | None = None, root: Path | None = None) -> dict:
    """{repository-relative path: sha256} of the code a stage runs: its STAGE_TOOLS (or the precedent stage's own
    script), every harness module those scripts import, followed transitively, and for the capture the renderer's build
    (toolidentity's package digest). An absent file is None, so its appearing later is a change. {} for a stage with no
    tools of its own (its code is this driver's, which is recorded, not bound)."""
    root = Path(root or ROOT)
    machine = machine or {}
    listed = [root / x for x in STAGE_TOOLS.get(stage, ())]
    if stage == "precedent":
        script = script_of([str(x) for x in machine.get("precedentCmd") or []])
        if script is not None:
            listed.append(script if script.is_absolute() else root / script)
    roots = [root / "_geometry-harness", root / "scripts"]
    seen, todo = {}, list(listed)
    while todo:
        p = todo.pop(0)
        key = fwd(p.relative_to(root)) if p.is_relative_to(root) else fwd(p)
        if key in seen or key == DRIVER:
            continue
        seen[key] = _tool_digest(p)
        if p.suffix == ".py" and p.is_file():
            todo += _local_imports(p, roots)
    if stage in RENDERER_STAGES:
        import toolidentity
        vp = machine.get("viewprobe")
        folder = Path(vp).parent if vp else buildlayout.program_dir("viewprobe")
        seen["viewprobe"] = toolidentity.package(folder)["digest"]
    if stage in CONTROLLER_STAGES:
        import toolidentity
        rc = machine.get("runcontrol")
        exe = Path(rc) if rc else buildlayout.runcontrol()
        seen["runcontrol"] = toolidentity.package(exe.parent)["digest"] if exe.is_file() else None
    return dict(sorted(seen.items()))

def _fingerprint(p: Path):
    if not p.exists():
        return None
    if p.is_file():
        return sha256_file(p)
    rows = []
    for f in sorted(p.rglob("*")):
        if f.is_file():
            st = f.stat()
            rows.append([fwd(f.relative_to(p)), st.st_size, st.st_mtime_ns])
    return "dir:" + sha256_json(rows)


class StageLedger:
    """RUN/stages.json: one receipt per accepted stage, bound to its inputs and outputs."""

    def __init__(self, run):
        self.path = Path(run) / "stages.json"
        self.doc = read_json(self.path) if self.path.is_file() else {"schema": 1, "stages": {}, "history": []}

    def _save(self):
        write_json(self.path, self.doc)

    @staticmethod
    def _intact(rec: dict) -> bool:
        return all(_fingerprint(Path(path)) == fp for path, fp in (rec.get("outputs") or {}).items())

    def decide(self, stage: str, inputs: dict) -> str:
        """'reuse' when the accepted receipt matches these inputs and its outputs are unchanged, else 'run'. One more way
        to reuse, with a history line: a receipt a later refusal set aside (`kept`) is put back when the inputs are again
        the ones it was accepted under, so restoring an input restores the stage."""
        want = sha256_json(inputs)
        rec = self.doc["stages"].get(stage)
        if rec and rec.get("inputsSha256") == want:
            return "reuse" if self._intact(rec) else "run"
        kept = (self.doc.get("kept") or {}).get(stage)
        if kept and kept.get("inputsSha256") == want and self._intact(kept):
            self.doc["stages"][stage] = self.doc["kept"].pop(stage)
            self.doc["history"].append({"stage": stage, "at": utcnow(), "restored": True, "inputsSha256": want,
                                        "reason": "the inputs are again the ones this receipt was accepted under and its "
                                                  "outputs are unchanged, so the receipt a refusal set aside is restored"})
            self._save()
            return "reuse"
        return "run"

    def record(self, stage: str, inputs: dict, outputs, exit_code: int, extra: dict | None = None):
        outs = [Path(o) for o in outputs]
        entry = {"stage": stage, "at": utcnow(), "exitCode": exit_code, "inputsSha256": sha256_json(inputs)}
        missing = [fwd(o) for o in outs if not o.exists()]
        if exit_code != 0 or missing:
            entry["accepted"] = False
            entry["reason"] = (f"exit {exit_code}" if exit_code != 0 else "missing outputs: " + ", ".join(missing))
            # the accepted receipt is set aside, not dropped: restoring the input restores the stage (decide)
            prev = self.doc["stages"].pop(stage, None)
            if prev is not None:
                self.doc.setdefault("kept", {})[stage] = prev
            self.doc["history"].append(entry)
            self._save()
            if missing and exit_code == 0:
                raise Refusal(f"stage {stage} exited 0 but did not write {', '.join(missing)}; it is not accepted.")
            return entry
        entry.update(accepted=True, inputs=inputs, outputs={fwd(o): _fingerprint(o) for o in outs}, **(extra or {}))
        self.doc["stages"][stage] = entry
        (self.doc.get("kept") or {}).pop(stage, None)   # a new acceptance supersedes one set aside
        self.doc["history"].append({k: entry[k] for k in ("stage", "at", "exitCode", "inputsSha256", "accepted")})
        self._save()
        return entry


def stage_outcome(stage: str, exit_code: int, outputs_ok: bool, sentence: str = "") -> dict:
    accepted = exit_code == 0 and outputs_ok
    return {"stage": stage, "status": "accepted" if accepted else "refused",
            "blocksLater": (not accepted) and stage not in NON_BLOCKING, "sentence": sentence}


def run_status(outcomes: list[dict]) -> str:
    if any(o["status"] == "awaiting" for o in outcomes):
        return "awaiting-judgement"
    return "complete" if outcomes and all(o["status"] == "accepted" for o in outcomes) else "incomplete"


# ---------------------------------------------------------------- the entry, resolved

class Context:
    """A selection entry resolved against the lab: plugin files, headers, baseline, arms and their views."""

    def __init__(self, entry_path, machine_path, out):
        self.entry_path = Path(entry_path)
        self.machine_path = Path(machine_path) if machine_path else None
        # one spelling per plugin from here on (PluginNames); an entry that spells each plugin one way is unchanged
        self.names = PluginNames()
        self.entry = normalise_entry(read_json(entry_path), self.names)
        self.machine = read_json(machine_path) if machine_path else {}
        self.out = Path(out)
        # an in-place entry (the workbench's manual mode): a chosen profile of any MO2 instance, read where it is; no lab,
        # no generated profile, no mod copy
        self.in_place = self.entry.get("inPlace")
        if self.in_place:
            ip = self.in_place
            self.instance, self.game, self.template = Path(ip["instance"]), Path(ip["game"]), None
            self.source_instance, self.source_profile, self.source_game = self.instance, ip["profile"], self.game
            self.id = self.entry["id"]
            self.profile = ip["profile"]
            return
        lab = {**self.machine.get("lab", {}), **self.entry.get("lab", {})}
        self.instance = Path(lab["instance"])
        self.game = Path(lab["game"])
        self.template = lab.get("templateProfile")
        src = self.entry["source"]
        self.source_instance, self.source_profile = Path(src["instance"]), src["profile"]
        self.source_game = Path(src.get("game") or self.game)
        self.id = self.entry["id"]
        self.profile = self.entry.get("profile") or f"EP-{self.id}"

    def resolve(self) -> dict:
        """What the census-to-bundle run resolved, from its own record in this run folder (runcontrol wrote it):
        prepare/preparation.json (headers, providers, baseline) and arms.json. The subject is the subject arm's evaluated
        plugins (the combined arm's in place), the partners each partner unit's, the keys every arm's in arm order."""
        prep_p, arms_p = self.out / "prepare" / "preparation.json", self.out / "arms.json"
        if not prep_p.is_file() or not arms_p.is_file():
            raise Refusal(measuring_refusal(self, "census"))
        prep, arms = read_json(prep_p), read_json(arms_p)
        by_id = {a.get("id"): a for a in arms}
        subject = list((by_id.get("subject") or by_id.get("combined") or {}).get("evaluated") or [])
        partners = [list(a.get("evaluated") or []) for a in arms if str(a.get("id") or "").startswith("unit:")]
        keys = list(dict.fromkeys(k for a in arms for k in a.get("keys") or []))
        return {"headers": prep.get("headers") or {}, "providers": prep.get("providers") or {},
                "baseline": prep.get("baseline") or [], "arms": arms, "subject": subject, "partners": partners, "keys": keys}


def measuring_refusal(ctx, stage: str) -> str:
    """The one sentence for a run folder whose census-to-bundle stage is not accepted (or no longer matches the entry)."""
    machine = f" --machine {fwd(ctx.machine_path)}" if getattr(ctx, "machine_path", None) else ""
    return (f"{stage} is run by the built-in controller, and this run folder has no accepted {stage} for this entry; run "
            f"`runcontrol run --entry {fwd(ctx.entry_path)} --out {fwd(ctx.out)}{machine} --through bundle` first, then "
            f"this command again.")


# ---------------------------------------------------------------- preparation


# ---------------------------------------------------------------- stage bodies

#: how far past the slot command's own waits and hold a heavy child's outer deadline sits
HOLD_MARGIN_MIN = 5


def _prefix_minutes(prefix: list, flag: str, default: float) -> float:
    try:
        return float(prefix[prefix.index(flag) + 1]) if flag in prefix else default
    except (IndexError, ValueError):
        return default


def child(ctx, ledger_dir, op, argv, *, heavy=False, purpose=None, deadline_min=None, env=None, hold_min=None):
    """One child through the shared Runner; a heavy child goes through the machine's slot command, held for
    `hold_min` minutes when given (the slot command's --max-min), else for the stage's `deadline_min`, else for the
    machine's own hold. The slot command stops its own job at that hold and releases; the Runner's deadline sits past
    the slot command's own waits and hold, so it never stops the slot command first."""
    lock = None
    if heavy and ctx.machine.get("heavyPrefix"):
        # one lock file per mod and step: two runs of different mods never name the same lock
        lock = Path(ctx.machine.get("lockDir") or ctx.out) / f"HEAVY-LOCK-{ctx.id}-{op}"
        prefix = [x.replace("{purpose}", purpose or op).replace("{lock}", fwd(lock)) for x in ctx.machine["heavyPrefix"]]
        hold = hold_min if hold_min is not None else deadline_min
        if hold is not None:
            if "--max-min" in prefix:
                prefix[prefix.index("--max-min") + 1] = str(hold)
            else:
                prefix += ["--max-min", str(hold)]
        if deadline_min:
            # the deadline counts from launch, the hold from when the lock is taken: cover the turn and idle waits too
            deadline_min = (_prefix_minutes(prefix, "--slot-wait-min", 240) + _prefix_minutes(prefix, "--wait-timeout-min", 0)
                            + _prefix_minutes(prefix, "--max-min", deadline_min) + HOLD_MARGIN_MIN)
        argv = prefix + ["--"] + argv
    job = {"id": op, "argv": [str(a) for a in argv], "cwd": str(ROOT)}
    if ctx.machine.get("runcontrol"):
        # the Python tools a stage starts (evalreport) verify bundles with the controller the machine file names
        env = {RUNCONTROL_ENV: fwd(Path(ctx.machine["runcontrol"])), **(env or {})}
    if env:
        job["env"] = {**os.environ, **env}
    lock_before = lock is not None and lock.exists()
    deadline = time.perf_counter() + 60 * deadline_min if deadline_min else None
    res = Runner("evalpipeline-" + op, out_dir=Path(ledger_dir), deadline=deadline).run([job], echo=False)
    if lock is not None and not lock_before and res[0].get("stopped") and lock.exists():
        release_stopped_lock(lock, res[0])
    return res[0]


def release_stopped_lock(lock: Path, res: dict):
    """The lock a slot command took before a stop from outside killed it, so its own release never ran. Removed, with
    a release line in its log, only when the Runner confirmed the whole tree gone; otherwise left, and the stage refuses."""
    try:
        held = (json.loads(lock.read_text(encoding="utf-8")) or {}).get("pid")
    except (OSError, ValueError):
        held = None
    if res.get("treeStopped") is not True:
        raise Refusal(f"the stage's slot command was stopped from outside ({res.get('stopped')}) and its process tree was "
                      f"not confirmed gone, so its lock {fwd(lock)} is left in place; check nothing it started still runs, "
                      f"then delete it.")
    lock.unlink()
    rec = {"step": "released", "reason": f"outer-stop: the stage's {res.get('stopped')} stopped the slot command; its process "
                                         f"tree was confirmed gone", "by": "evalpipeline", "heldPid": held,
           "t": time.strftime("%Y-%m-%d %H:%M:%S %Z"), "lock": str(lock)}
    with open(lock.parent / "idle_lock.log", "a", encoding="utf-8") as fh:
        fh.write(json.dumps(rec) + "\n")
    print(f"released the stopped slot command's lock {fwd(lock)} (its tree was confirmed gone)")


def planned_budget(out: Path):
    """The budget an existing plan froze, or None when there is no plan yet."""
    f = Path(out) / "evaluation" / "plan.json"
    return ((read_json(f).get("budgets") or {}).get("minutes")) if f.is_file() else None


def budget_differs(ctx_entry: dict, out: Path):
    """The entry's budget when it differs from the existing plan's, else None: a budget is a limit on an invocation, so
    changing only it keeps the plan, and evaluate continues under the new limit."""
    planned = planned_budget(out)
    want = (ctx_entry.get("evaluation") or {}).get("budgetMinutes")
    return want if planned is not None and want is not None and float(want) != float(planned) else None


def reader_cmd(ctx) -> str:
    cmd = ctx.machine.get("readerCmd") or os.environ.get("HOUSECARL_RECORD_READER_CMD")
    if not cmd:
        raise Refusal("no record reader is named: set readerCmd in the machine file (a houseCARL server whose folder "
                      "carries its generated corpus.json).")
    return cmd


def names_env(ctx) -> dict:
    """A private, empty instance for the names read (bundleenrich's own rule: every record is read by absolute path,
    never from an active copy of the same plugin)."""
    import bundleenrich
    return {k: fwd(v) for k, v in bundleenrich.private_instance(ctx.out / "names-instance").items()}


def bundle_dir(ctx) -> Path:
    return ctx.out / "bundle" / "evidence"


def stage_precedent(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The run-local precedent cards (machine file `precedentCmd`, with {bundle} and {out}); a refusal does not block."""
    tmpl = ctx.machine.get("precedentCmd")
    if not tmpl:
        raise Refusal("no precedent stage is named in the machine file (precedentCmd); the cards were not written.")
    out = sdir / "cards"
    argv = [x.replace("{bundle}", fwd(bundle_dir(ctx))).replace("{out}", fwd(out)) for x in tmpl]
    script = script_of(argv)
    if script is not None and not script.is_file():
        raise Refusal(f"stage script absent: {fwd(script)}; no precedent cards were written.")
    r = child(ctx, sdir / "commands", "precedent", argv)
    log = sdir / "commands" / "precedent.txt"
    if r.get("exitCode") != 0:
        tail = log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] if log.is_file() else []
        raise Refusal(f"the precedent stage refused (exit {r.get('exitCode')}): {' '.join(tail)[:300]}")
    return {"bundle": sha256_file(bundle_dir(ctx) / "manifest.json")}, [out]


def report_plan_argv(ctx, res, out: Path, captures=(), bundle: Path | None = None, final: bool = False) -> list:
    """evalreport plan's argv; `final` once the capture stage is closed (the report stage), so the reel plans only from
    captured clashes instead of asking for captures no later round will take."""
    run = ctx.out / "evaluation"
    argv = [sys.executable, fwd(HERE / "evalreport.py"), "plan", "--bundle", fwd(bundle or bundle_dir(ctx)), "--out", fwd(out),
            "--mode", ctx.entry.get("report", {}).get("mode", "concise")]
    if ctx.entry.get("report", {}).get("maxPages"):
        argv += ["--max-pages", str(ctx.entry["report"]["maxPages"])]
    arms = read_json(ctx.out / "prepare" / "preparation.json")["arms"]
    plan = read_json(run / "plan.json")
    by_file = {a["candidateFile"]: a["armId"] for a in plan["arms"]}
    for row in arms:
        if row["arm"] == "combined" or row["keys"]:
            loc = run / "outputs" / "loadability.masters-and-order" / f"{by_file[row['file']]}.json"
            if loc.is_file():
                argv += ["--location-scope", f"{by_file[row['file']]}={fwd(loc)}"]
    for k in res["keys"]:
        argv += ["--declare-patch", k]
    for p, n in (ctx.entry.get("modNames") or {}).items():
        argv += ["--mod-name", f"{p}={n}"]
    # the reel's mod names from the Nexus catalogue: a page it lacks is fetched once unless the entry says offline
    nexus_names = ctx.entry.get("nexusNames") or "fetch"
    argv += ["--nexus-names", nexus_names]
    server_ = (getattr(ctx, "machine", None) or {}).get("readerCmd") or os.environ.get("HOUSECARL_RECORD_READER_CMD")
    if nexus_names == "fetch" and server_:
        argv += ["--nexus-reader-cmd", server_]
    if ctx.entry.get("patchSearch"):
        argv += ["--patch-search", ctx.entry["patchSearch"]]
    # the precedent stage's census of each patch's own edits, where that stage wrote it
    if (ctx.out / "precedent" / "cards" / "summary.json").is_file():
        argv += ["--precedent", fwd(ctx.out / "precedent" / "cards")]
    for c in captures:
        argv += ["--capture-results", fwd(c)]
    if final:
        argv += ["--captures-final"]
    return argv


def stage_report_plan(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    out = sdir / "plan"
    if out.exists():
        shutil.rmtree(out)
    r = child(ctx, sdir / "commands", "report-plan", report_plan_argv(ctx, res, out))
    if r.get("exitCode") != 0 or not (out / "presentation.json").is_file():
        raise Refusal(f"evalreport plan refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / 'report-plan.txt')}.")
    sb = sdir / "storyboard.json"
    r = child(ctx, sdir / "commands", "storyboard", [sys.executable, fwd(HERE / "evalreel.py"), "storyboard-packet",
                                                     "--presentation", fwd(out / "presentation.json"),
                                                     "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(sb)])
    if r.get("exitCode") != 0 or not sb.is_file():
        raise Refusal(f"evalreel storyboard-packet refused (exit {r.get('exitCode')}).")
    reqs = (read_json(sb).get("capturePlan") or {}).get("requests") or []
    write_json(sdir / "requests.json", [{"id": q["id"], "type": q.get("requestType"), "arm": q.get("armId"),
                                         "without": (q.get("vignette") or {}).get("withoutArmId"),
                                         "supported": ((q.get("camera") or {}).get("support") or {}).get("supported")}
                                        for q in reqs])
    return {"bundle": sha256_file(bundle_dir(ctx) / "manifest.json")}, [out / "presentation.json", sb, sdir / "requests.json"]


def capture_request(ctx, q, sb: Path, qdir: Path, cands: dict) -> dict:
    """One capture request: a verified camera (capture_acquire acquire --points auto), then its frames (capture), in
    the with- and without-setups the request names. capture runs even on a refused acquisition: it writes the refusal
    as a capture result the report and reel state."""
    lab = ["--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game)]
    # capture_acquire's own default is the production viewprobe build; a machine file may name another one
    vp_args = ["--vp", ctx.machine["viewprobe"]] if ctx.machine.get("viewprobe") else []
    row = {"id": q["id"], "arm": q.get("armId"), "without": (q.get("vignette") or {}).get("withoutArmId")}
    if ((q.get("camera") or {}).get("support") or {}).get("supported") is False:
        row["refused"] = "the request says its camera is not supported by capture_acquire"
        return row
    if row["arm"] not in cands or row["without"] not in cands:
        row["refused"] = "the request names no planned with- or without-arm"
        return row
    acq = qdir / "acquisition.json"
    # a vignette renders its camera in the setup without the mod too, so the camera must pass the same checks there (C2168)
    states = (["--state-order", f"without={cands[row['without']]}"]
              if q.get("requestType") in ("overlap-pair", "state-pair") else [])
    a = child(ctx, qdir / "commands", "acquire", [
        sys.executable, fwd(HERE / "capture_acquire.py"), "acquire", "--packet", fwd(sb), "--request", q["id"],
        "--points", "auto", "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(acq), "--receipts",
        fwd(qdir / "camera-receipts"), *states, *vp_args, "--", *lab, "--order", cands[row["arm"]]],
        heavy=True, purpose=f"evalpipeline {ctx.id} capture", deadline_min=60)
    row["acquireExit"] = a.get("exitCode")
    if not acq.is_file():
        row["refused"] = f"the camera acquisition wrote no result (exit {a.get('exitCode')}); see {fwd(qdir / 'commands')}"
        return row
    if q.get("requestType") == "navmesh-version-comparison":
        # a navmesh comparison renders both its frames in the setup with the mod; the setup without it is its control,
        # measured first at the same camera and handed to capture, which keeps no frame unless that control is clean.
        # The control's folder is outside sequence/, so the blind image packet holds only the frames the reel shows
        ctl_dir = qdir / "control"
        k = child(ctx, qdir / "commands", "navmesh-control", [
            sys.executable, fwd(HERE / "capture_acquire.py"), "navmesh-control", "--packet", fwd(sb), "--request", q["id"],
            "--acquisition", fwd(acq), "--order", cands[row["without"]], "--out", fwd(ctl_dir), *vp_args, "--", *lab],
            heavy=True, purpose=f"evalpipeline {ctx.id} capture", deadline_min=60)
        row["controlExit"] = k.get("exitCode")
        ctl = ctl_dir / "control-result.json"
        if ctl.is_file():
            got = read_json(ctl)
            row["control"] = {"status": got.get("status"), "brokenCrossings": got.get("brokenCrossings"),
                              "result": fwd(ctl)}
        tail = ["--with-order", cands[row["arm"]], "--control", fwd(ctl)]
    else:
        tail = ["--with-order", cands[row["arm"]], "--without-order", cands[row["without"]]]
    c = child(ctx, qdir / "commands", "capture", [
        sys.executable, fwd(HERE / "capture_acquire.py"), "capture", "--packet", fwd(sb), "--request", q["id"],
        "--acquisition", fwd(acq), *tail,
        "--out", fwd(qdir / "sequence"), "--size", str(ctx.entry.get("captureSize", 1280)), *vp_args, "--", *lab],
        heavy=True, purpose=f"evalpipeline {ctx.id} capture", deadline_min=60)
    row["captureExit"] = c.get("exitCode")
    result = qdir / "sequence" / "capture-result.json"
    if not result.is_file():
        row["refused"] = f"capture wrote no result (exit {c.get('exitCode')}); see {fwd(qdir / 'commands')}"
    else:
        doc = read_json(result)
        row.update(result=fwd(result), status=doc.get("status"), reason=doc.get("reason"))
    acqd = read_json(acq)
    if acqd.get("refusal"):
        row["acquisitionRefusal"] = acqd["refusal"]
    return row


MAX_CAPTURE_ROUNDS = 3
MAX_NEW_PER_ROUND = 4      # a refusal costs about 5 minutes of the heavy slot; a re-plan may not ask for more per round


def capped_requests(asked: list, cap: int) -> tuple[list, list]:
    """The requests a round captures: the plan's first `cap` new ones, in its order; the rest are deferred to the next
    round's plan (which may no longer ask for them)."""
    return list(asked[:cap]), list(asked[cap:])


def capture_identity(ctx) -> dict:
    """The capture tools' identity as capture_acquire records it on a result (its own capture_tool_identity):
    capture_acquire, the point picker and viewprobe. shotplan is left out on purpose: it chooses the camera, and a
    finished result records the camera its frames were rendered at, so a shotplan change can alter only which camera a
    new acquisition picks, never what a recorded one renders."""
    import capture_acquire
    return capture_acquire.capture_tool_identity(ctx.machine.get("viewprobe"))


# a refusal that is a property of the request's geometry: the same request, tools and data give it again. The three bare
# picker codes a capture fallback now answers (no visible face -> cutaway, then top-down; no station -> top-down; no
# opaque face -> a measured coverage mark) are not here: such a refusal is reused only as the FINAL code of a finished
# chain (FINAL_REFUSALS), never on its own.
DETERMINISTIC_REFUSALS = {"placement-unknown", "points-empty-pick", "request-participants-unusable"}
# the final codes of a finished fallback chain (capture_acquire marks the refusal `final`): every fallback was tried and
# each step's own verdict was the geometry's
FINAL_REFUSALS = {"top-down-participant-unseen", "see-through-unmarked", "points-not-drawn", "points-not-placed",
                  "state-camera-blocked"}
# a chain step that failed for a reason that is not the geometry's (a crash, memory, a lock or slot timeout, a renderer
# exit) makes the whole refusal transient, whatever the other steps said
TRANSIENT_STEP = {"failed"}
# the eye-level codes capture_acquire's witness step answers (its WITNESS_FROM)
POSE_REFUSAL_CODES = {"see-through-not-covered-at-pose", "planner-not-verified"}


def deterministic_refusal(ref_: dict) -> bool:
    """Whether a capture refusal is a property of the request's geometry, so the same tools give it again: one of the
    plain deterministic codes, or a FINISHED fallback chain whose final code is one of FINAL_REFUSALS and none of whose
    steps failed for a reason of its own (A3: a crashed top-down step is captured again)."""
    code = ref_.get("pickerCode") or ref_.get("code")
    chain = ref_.get("chain") or []
    if any(str(st.get("status")) in TRANSIENT_STEP for st in chain):
        return False
    # a composed eye-level pose refused at the final camera, in a chain from before the alpha-card rule (no witness step):
    # the witness station's own camera was never tried, so the refusal is not yet the geometry's (C1895)
    if (any(st.get("kind") == "eye-level" and st.get("code") in POSE_REFUSAL_CODES for st in chain)
            and not any(st.get("kind") == "witness" for st in chain)):
        return False
    if ref_.get("final") is True:
        return (ref_.get("code") in FINAL_REFUSALS or code in FINAL_REFUSALS)
    return code in DETERMINISTIC_REFUSALS


def reusable_capture(q: dict, qdir: Path, identity: dict | None = None) -> dict | None:
    """A request already finished in this run folder (same id: the id hashes the request's identity, its setups and its
    source) is reused rather than rendered again, with its outcome, when the capture tools are the same build
    (`identity`: capture_acquire, the point picker, viewprobe). A refusal is reused only when its reason is a property
    of the request's geometry (DETERMINISTIC_REFUSALS); a transient one (memory, a lock or slot timeout, a crash or
    kill, a renderer exit without a reason) is captured again, as is an unfinished one. The row says so."""
    result = qdir / "sequence" / "capture-result.json"
    if not result.is_file():
        return None
    doc = read_json(result)
    if doc.get("status") not in ("captured", "refused") or doc.get("request") not in (None, q["id"]):
        return None
    if identity is None or doc.get("toolIdentity") != identity:
        return None
    if doc.get("status") == "refused":
        ref_ = doc.get("refusal") or {}
        if not deterministic_refusal(ref_):
            return None
    return {"id": q["id"], "arm": q.get("armId"), "without": (q.get("vignette") or {}).get("withoutArmId"),
            "result": fwd(result), "status": doc.get("status"), "reason": doc.get("reason"), "reused": True}


def mark_superseded(rows: list[dict], final_ids) -> list[dict]:
    """A capture the final plan no longer asks for (a later round replaced it, say a split re-captured with a mark) is
    kept on record as superseded, never handed on: the blind packet, the media bundle and the report read only
    `captured` rows (Missives retry 2: the unmarked split rode along beside the marked one)."""
    final_ids = set(final_ids)
    out = []
    for r in rows:
        r = dict(r)
        if r.get("status") == "captured" and r["id"] not in final_ids:
            r.update(status="superseded", reason="the final plan no longer asks for this request")
        out.append(r)
    return out


#: the capture stage's one hold on the machine slot: every round's requests (acquire and capture, 60 min each at most)
#: and its re-plan
CAPTURE_HOLD_MIN = MAX_CAPTURE_ROUNDS * (MAX_NEW_PER_ROUND * 2 * 60 + 15)


def stage_capture(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The capture rounds hold the machine slot once, from the first request to the last re-plan: with a slot command
    they run as one heavy child (`capture-rounds`), whose request children inherit the slot instead of queueing for it
    between requests, where another subject's heavy stage could take it."""
    if not ctx.machine.get("heavyPrefix"):
        return capture_rounds(ctx, res, sdir)
    done = sdir / "capture-rounds.json"
    if done.exists():
        done.unlink()
    argv = [sys.executable, fwd(HERE / "evalpipeline.py"), "capture-rounds", "--entry", fwd(ctx.entry_path),
            *(["--machine", fwd(ctx.machine_path)] if getattr(ctx, "machine_path", None) else []), "--out", fwd(ctx.out)]
    r = child(ctx, sdir / "commands", "capture-rounds", argv, heavy=True, purpose=f"evalpipeline {ctx.id} capture",
              deadline_min=CAPTURE_HOLD_MIN, hold_min=CAPTURE_HOLD_MIN)
    got = read_json(done) if done.is_file() else {}
    if got.get("refused"):
        raise Refusal(got["refused"])
    if r.get("exitCode") != 0 or not got.get("accepted"):
        raise Refusal(f"the capture rounds did not finish (exit {r.get('exitCode')}, stopped: {r.get('stopped')}); "
                      f"read {fwd(sdir / 'commands' / 'capture-rounds.txt')}.")
    return got["bound"], [sdir / "captures.json", sdir / "rounds.json"]


def capture_rounds(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """Capture every request the storyboard asks for, then plan again with the results: a refused capture makes the
    reel planner take the next member of that clash's group, which needs its own capture. Repeat until a plan asks
    for nothing new (at most MAX_CAPTURE_ROUNDS rounds; a plan still asking after that is a refusal)."""
    plan = read_json(ctx.out / "evaluation" / "plan.json")
    cands = {a["armId"]: fwd(ctx.out / "prepare" / "candidates" / a["candidateFile"]) for a in plan["arms"]}
    sb = ctx.out / "report-plan" / "storyboard.json"
    rows, done, rounds = [], set(), []
    for rnd in range(1, MAX_CAPTURE_ROUNDS + 1):
        asked = [q for q in (read_json(sb).get("capturePlan") or {}).get("requests") or [] if q["id"] not in done]
        reqs, deferred = capped_requests(asked, MAX_NEW_PER_ROUND)
        rounds.append({"round": rnd, "storyboard": fwd(sb), "newRequests": [q["id"] for q in reqs],
                       "deferred": [q["id"] for q in deferred]})
        if not reqs:
            break
        for q in reqs:
            kept = reusable_capture(q, sdir / q["id"], capture_identity(ctx))
            rows.append(dict(kept or capture_request(ctx, q, sb, sdir / q["id"], cands), round=rnd))
            done.add(q["id"])
        # plan again with every result so far; its storyboard names what the reel would now picture
        pdir = sdir / f"replan-{rnd:02d}"
        if pdir.exists():
            shutil.rmtree(pdir)
        results = [Path(r["result"]) for r in rows if r.get("result")]
        r = child(ctx, sdir / "commands", f"replan-{rnd:02d}", report_plan_argv(ctx, res, pdir, results))
        if r.get("exitCode") != 0:
            raise Refusal(f"evalreport plan with the round-{rnd} capture results refused (exit {r.get('exitCode')}).")
        sb = sdir / f"storyboard-{rnd:02d}.json"
        r = child(ctx, sdir / "commands", f"storyboard-{rnd:02d}", [
            sys.executable, fwd(HERE / "evalreel.py"), "storyboard-packet", "--presentation",
            fwd(pdir / "presentation.json"), "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(sb)])
        if r.get("exitCode") != 0:
            raise Refusal(f"evalreel storyboard-packet after round {rnd} refused (exit {r.get('exitCode')}).")
    else:
        # the bound is strict: after the last round the stage is accepted with what it has, and what the plan still
        # asks for is recorded as not captured (never a further round)
        left = [q["id"] for q in (read_json(sb).get("capturePlan") or {}).get("requests") or [] if q["id"] not in done]
        if left:
            rounds.append({"round": None, "storyboard": fwd(sb), "notCaptured": left,
                           "why": f"the {MAX_CAPTURE_ROUNDS}-round bound was reached"})
            print(f"capture: the {MAX_CAPTURE_ROUNDS}-round bound was reached; {len(left)} request(s) not captured: "
                  + ", ".join(left))
    rows = mark_superseded(rows, [q["id"] for q in (read_json(sb).get("capturePlan") or {}).get("requests") or []])
    write_json(sdir / "captures.json", rows)
    write_json(sdir / "rounds.json", rounds)
    return {"requests": len(rows)}, [sdir / "captures.json", sdir / "rounds.json"]


class Awaiting(Exception):
    """The run waits for a judgement a person or a model makes; one sentence says which file it waits for."""


class NonBlockingRefusal(Refusal):
    """A refusal recorded with its sentence that does not stop the stages after it."""


def observations_state(key: list[dict], obs: dict | None) -> tuple[str, str]:
    """('awaiting' | 'refused' | 'accepted' | 'nothing', sentence) for the blind image observations of one capture.
    Accepted only when the banked file lists exactly the packet's frames with the same hashes and an observation each."""
    if not key:
        return "nothing", "no frame was captured (every capture request was refused), so there is nothing to observe."
    if obs is None:
        return "awaiting", "the blind image observations are not banked yet."
    want = {k["image"]: k["sha256"] for k in key}
    got = {f.get("image"): f.get("sha256") for f in obs.get("frames") or []}
    if got != want:
        missing = sorted(set(want) - set(got))
        wrong = sorted(i for i in set(want) & set(got) if want[i] != got[i])
        extra = sorted(set(got) - set(want))
        return "refused", (f"the banked observations do not match this capture's frames (missing {missing[:5]}, "
                           f"other bytes {wrong[:5]}, not in this packet {extra[:5]}); observe this packet again.")
    empty = [f.get("image") for f in obs.get("frames") or [] if not str(f.get("observation") or "").strip()]
    if empty:
        return "refused", f"frames {empty[:5]} have no observation; every frame needs one."
    return "accepted", f"{len(want)} frames observed blind."


def no_frame_sentence(rows: list[dict]) -> str:
    """Why a capture stage left no frame, from its rows: nothing asked for, every request refused, or none produced one."""
    if not rows:
        return "capture asked for no picture (the reel plan requested none), so there is nothing to observe."
    refused = [r for r in rows if r.get("status") == "refused" or r.get("refused")]
    if len(refused) == len(rows):
        why = sorted({str(r.get("reason") or r.get("refused") or "no reason given") for r in refused})
        return (f"all {len(rows)} capture requests were refused ({'; '.join(why[:3])}{'; ...' if len(why) > 3 else ''}), "
                f"so there is nothing to observe.")
    return f"none of the {len(rows)} capture requests produced a frame, so there is nothing to observe."


def stage_image_observations(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """A neutral, indexed copy of every captured frame for the blind image review (no names, arms or expectations),
    then a stop until the reviewer's agent banks RUN/image-observations/observations.json for exactly these frames."""
    caps = read_json(ctx.out / "capture" / "captures.json")
    packet = sdir / "indexed-images"
    # the key is derived from this capture's frames every time; a kept key stands only when it lists exactly these frames,
    # so a capture rerun after an earlier key (an empty one, say) never inherits it (C1897)
    key, n = [], 0
    for row in caps:
        if row.get("status") != "captured":
            continue
        for png in sorted(Path(row["result"]).parent.rglob("*.png")):
            n += 1
            key.append({"image": f"image-{n:03d}.png", "source": fwd(png), "sha256": sha256_file(png),
                        "request": row["id"]})
    kept = read_json(sdir / "image-key.json") if (sdir / "image-key.json").is_file() else None
    if kept != key or not packet.is_dir():
        if packet.exists():
            shutil.rmtree(packet)
        packet.mkdir(parents=True)
        for k in key:
            shutil.copy2(k["source"], packet / k["image"])
        write_json(sdir / "image-key.json", key)  # the key stays out of the packet a blind reviewer is given
        (packet / "README.txt").write_text("Indexed frames for a blind review. Describe what each image shows; no "
                                           "names or expectations are supplied.\n", encoding="utf-8")
    key = read_json(sdir / "image-key.json")
    obs_path = sdir / "observations.json"
    state, sentence = observations_state(key, read_json(obs_path) if obs_path.is_file() else None)
    if state == "awaiting":
        raise Awaiting(f"{sentence} Bank them at {fwd(obs_path)} as {{frames: [{{image, sha256, observation}}]}} for "
                       f"the {len(key)} frames in {fwd(packet)}, then run again.")
    if state == "nothing":
        # no frame to observe blind: the run stops for a person to confirm that no picture is right, with the reason
        # read from the capture rows (none asked for, every request refused, or none produced a frame)
        obs = read_json(obs_path) if obs_path.is_file() else {}
        if obs.get("frames") == [] and str(obs.get("acknowledged") or "").strip():
            return {"images": 0, "observations": sha256_file(obs_path)}, [sdir / "image-key.json", obs_path]
        raise Awaiting(f"{no_frame_sentence(caps)} Confirm that no picture is right by banking {fwd(obs_path)} as "
                       f'{{"frames": [], "acknowledged": "<why>"}}, then run again.')
    if state == "refused":
        raise Refusal(sentence)
    return {"images": len(key), "observations": sha256_file(obs_path)}, [sdir / "image-key.json", obs_path]


def script_of(argv: list[str]) -> Path | None:
    for a in argv:
        if a.lower().endswith(".py"):
            return Path(a)
    return None


def reel_transition_frames(rmap: dict) -> list[dict]:
    """Every frame index where the reel changes what it shows, from its own map: each segment's first and last frame,
    and inside a sequence the last frame of one state and the first of the next."""
    out = []
    for seg in rmap.get("segments") or []:
        iv = seg.get("reelInterval") or {}
        start, n = iv.get("startFrame"), iv.get("frames")
        if not isinstance(start, int) or not isinstance(n, int):
            continue
        out.append({"frame": start, "segment": seg.get("chapterId"), "at": "segment start"})
        if seg.get("kind") != "split":   # a split's halves are on screen together; a still's pages and a
                                           # sequence's states follow one another
            pos = start
            for f in seg.get("frames") or []:
                k = f.get("frames")
                if not isinstance(k, int):
                    continue
                if pos != start:
                    out.append({"frame": pos, "segment": seg.get("chapterId"),
                                "at": f"first frame of {f.get('state') or 'page ' + str(f.get('page'))}"})
                pos += k
                out.append({"frame": pos - 1, "segment": seg.get("chapterId"),
                            "at": f"last frame of {f.get('state') or 'page ' + str(f.get('page'))}"})
        out.append({"frame": start + n - 1, "segment": seg.get("chapterId"), "at": "segment end"})
    seen, rows = set(), []
    for r in out:
        if r["frame"] not in seen:
            seen.add(r["frame"])
            rows.append(r)
    return rows


def next_numbered(parent: Path, stem: str) -> Path:
    """parent/stem-NN for the first NN not yet used (01 upward)."""
    n = 1
    while (parent / f"{stem}-{n:02d}").exists():
        n += 1
    return parent / f"{stem}-{n:02d}"


def latest_numbered(parent: Path, stem: str) -> Path | None:
    got = sorted(p for p in parent.glob(f"{stem}-[0-9][0-9]") if p.is_dir())
    return got[-1] if got else None


def capture_media_items(rows: list[dict]) -> list[dict]:
    """The media items of every captured request (capture_acquire capture's media-<arm>.json); a refused request
    contributes none."""
    items = []
    for r in rows:
        if r.get("status") == "captured" and r.get("result"):
            for m in sorted(Path(r["result"]).parent.glob("media-*.json")):
                items += read_json(m).get("items") or []
    return items


def media_bundle_dir(ctx) -> Path:
    return ctx.out / "report" / "media-bundle" / "evidence"


def frozen_input_args(ctx) -> list[str]:
    """The media bundle's frozen inputs, by construction (C1930, C1933): every input the evaluation's plan pins by path
    and hash at its top level (evalrun's PREMISE_COPIES: the check registry, the scope and the candidate search) whose
    original no longer hashes to the plan (gone, or changed since) is handed to runcontrol bundle as the evidence bundle's own copy, which
    runcontrol checks against the plan's hash and refuses if it differs. A pinned input this function does not know is
    refused in one sentence rather than left for the bundle to trip on (Hroggar: registry-changed; Ice Blade:
    scope-missing, its plan naming a scope on a deleted folder)."""
    import evalrun as ER
    plan = read_json(ctx.out / "evaluation" / "plan.json")
    out = []
    for label, frozen in plan.items():
        if not (isinstance(frozen, dict) and frozen.get("path") and frozen.get("sha256")):
            continue
        dest = ER.PREMISE_COPIES.get(label)
        if dest is None:
            raise Refusal(f"the plan pins {label} ({frozen['path']}), which the report stage does not know how to hand "
                          f"the media bundle; add it to frozen_input_args before reporting.")
        if ER.premise_file_refusal(plan, label, "") is None:
            continue
        copy = bundle_dir(ctx) / dest
        if copy.is_file():
            out += [f"--{label}", fwd(copy)]
    return out


def stage_report(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The captured frames go into a second evidence bundle (runcontrol bundle --media, every item checked against the
    arm's own configuration fingerprint), and the report is planned and compiled from it with the capture results."""
    rows = read_json(ctx.out / "capture" / "captures.json")
    caps = [Path(r["result"]) for r in rows if r.get("result") and r.get("status") != "superseded"]
    # every generation in its own numbered folder: a reviewed report is never written over (review pinning)
    out = next_numbered(sdir, "report")
    items = capture_media_items(rows)
    bundle = bundle_dir(ctx)
    if items:
        mdir = sdir / "media-bundle"
        if mdir.exists():
            shutil.rmtree(mdir)
        mdir.mkdir(parents=True)
        write_json(mdir / "media.json", {"schemaVersion": 2, "items": items})
        bundle = media_bundle_dir(ctx)
        run = ctx.out / "evaluation"
        for name, argv in (("media-bundle", [fwd(runcontrol_exe(ctx.machine)), "bundle", "--run", fwd(run), "--out", fwd(bundle), "--media",
                                                    fwd(mdir / "media.json"), *frozen_input_args(ctx),
                                                    "--reader-cmd", reader_cmd(ctx),
                                                    *[x for k, v in names_env(ctx).items() for x in ("--reader-env", f"{k}={v}")],
                                                    *[x for p, n in (ctx.entry.get("modNames") or {}).items()
                                                      for x in ("--mod-name", f"{p}={n}")]]),
                           ("media-verify", verify_bundle_argv(ctx.machine, bundle))):
            r = child(ctx, sdir / "commands", name, argv)
            if r.get("exitCode") != 0:
                raise Refusal(f"{name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.txt'))}.")
    steps = [("plan", report_plan_argv(ctx, res, out, caps, bundle, final=True)),
             ("report", [sys.executable, fwd(HERE / "evalreport.py"), "report", "--presentation",
                         fwd(out / "presentation.json"), "--out", fwd(out)]),
             ("verify", [sys.executable, fwd(HERE / "evalreport.py"), "verify", "--out", fwd(out)])]
    for name, argv in steps:
        r = child(ctx, sdir / "commands", name, argv)
        if r.get("exitCode") != 0:
            raise Refusal(f"evalreport {name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.txt'))}.")
    outs = [out / "presentation.json"] + [p for p in (out / "report.md", out / "report.html", out / "report.pdf") if p.is_file()]
    return {"captures": len(caps), "mediaItems": len(items)}, outs


def stage_reel(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    m = ctx.machine
    reel_bundle = media_bundle_dir(ctx) if media_bundle_dir(ctx).is_dir() else bundle_dir(ctx)
    rep = latest_numbered(ctx.out / "report", "report")
    if rep is None:
        raise Refusal("no report generation exists to cut a reel from.")
    pres = rep / "presentation.json"
    gen = next_numbered(sdir, "reel")
    gen.mkdir(parents=True)
    sb = gen / "storyboard.json"
    reel = gen / "reel"
    steps = [("storyboard", [sys.executable, fwd(HERE / "evalreel.py"), "storyboard-packet", "--presentation", fwd(pres),
                             "--bundle", fwd(reel_bundle), "--out", fwd(sb)])]
    board = sb
    if m.get("speech"):
        sp = m["speech"]
        steps.append(("speech", [sp["python"], fwd(HERE / "evalspeech.py"), "--storyboard", fwd(sb), *sp.get("args", []),
                                 "--out", fwd(gen / "speech")]))
        board = gen / "speech" / "storyboard-with-audio.json"
    render = [sys.executable, fwd(HERE / "evalreel.py"), "render", "--presentation", fwd(pres), "--out", fwd(reel),
              "--bundle", fwd(reel_bundle), "--storyboard", fwd(board)]
    if m.get("ffmpeg"):
        render += ["--ffmpeg", m["ffmpeg"]]
    steps.append(("render", render))
    for name, argv in steps:
        r = child(ctx, sdir / "commands", name, argv, deadline_min=90)
        if r.get("exitCode") != 0:
            raise Refusal(f"reel step {name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.txt'))}.")
    status = read_json(reel / "reel-status.json") if (reel / "reel-status.json").is_file() else {}
    if status.get("status") != "rendered":
        raise Refusal(f"the reel status is {status.get('status')!r}, not rendered; read {fwd(reel / 'reel-status.json')}.")
    # decode the frames the reel map names, for the reel review
    dec = gen / "decoded"
    if m.get("ffmpeg") and (reel / "reel-map.json").is_file():
        dec.mkdir(exist_ok=True)
        rmap = read_json(reel / "reel-map.json")
        frames = reel_transition_frames(rmap)
        write_json(dec / "frames.json", frames)
        for i, row in enumerate(frames[:80]):
            child(ctx, gen / "commands", f"decode-{i:02d}", [
                m["ffmpeg"], "-hide_banner", "-nostdin", "-loglevel", "error", "-i", fwd(reel / "reel.mp4"),
                "-vf", "select=eq(n\\,%d)" % row["frame"], "-vsync", "0", "-frames:v", "1",
                fwd(dec / f"frame-{row['frame']:05d}.png")])
    return {"presentation": sha256_file(pres)}, [reel / "reel.mp4", reel / "reel-status.json"]


def stage_deliver(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The review packet paths for the reviewer's clean-context reviews; the run then waits for their verdicts."""
    rep = latest_numbered(ctx.out / "report", "report") or ctx.out / "report"
    gen = latest_numbered(ctx.out / "reel", "reel") or ctx.out / "reel"
    paths = {"report": [fwd(p) for p in (rep / "report.pdf", rep / "report.html", rep / "report.md") if p.is_file()],
             "evidenceIndex": fwd(rep / "evidence-index.md") if (rep / "evidence-index.md").is_file() else None,
             "reel": fwd(gen / "reel" / "reel.mp4"),
             "reelMap": fwd(gen / "reel" / "reel-map.json"),
             "decodedFrames": fwd(gen / "decoded"),
             "blindImages": fwd(ctx.out / "image-observations" / "indexed-images"),
             "bundle": fwd(bundle_dir(ctx)), "mediaBundle": None,
             "precedentCards": fwd(ctx.out / "precedent" / "cards")}
    # the media bundle only when the accepted report was built from one; a folder left by an earlier generation is not it
    report = StageLedger(ctx.out).doc["stages"].get("report") or {}
    if ((report.get("bound") or {}).get("mediaItems") or 0) > 0 and media_bundle_dir(ctx).is_dir():
        paths["mediaBundle"] = fwd(media_bundle_dir(ctx))
    else:
        paths["mediaBundleAbsent"] = (f"no frame was captured, so the report has no media bundle; it reads the evidence "
                                      f"bundle at {fwd(bundle_dir(ctx))}")
    write_json(sdir / "review-packet.json", paths)
    print(json.dumps(paths, indent=1))
    return {}, [sdir / "review-packet.json"]


STAGE_BODIES = {"precedent": stage_precedent, "report-plan": stage_report_plan,
                "capture": stage_capture, "image-observations": stage_image_observations, "report": stage_report,
                "reel": stage_reel, "deliver": stage_deliver}


# ---------------------------------------------------------------- which entry keys each stage reads

def _is_entry_expr(node) -> bool:
    """`ctx.entry`, `self.entry` or a name ending in `entry` (a parameter such as `ctx_entry`)."""
    return ((isinstance(node, ast.Attribute) and node.attr == "entry")
            or (isinstance(node, ast.Name) and node.id.endswith("entry") and isinstance(node.ctx, ast.Load)))


def stage_entry_keys(source: str | None = None) -> dict[str, list[str]]:
    """{stage: [presentation keys its code reads]}, derived from this driver's code: each STAGE_BODIES function and
    every module function or Context method it calls (transitively), scanned for `<entry>.get("key")` and
    `<entry>["key"]`. The entry passed whole to a followed function is read there, under its parameter's name. Any
    other use of the entry inside a stage's code (a computed key, the entry handed to something not followed) is
    refused in one sentence, never left out, so a stage cannot read a key its hash does not bind."""
    text = source if source is not None else Path(__file__).read_text(encoding="utf-8")
    tree = ast.parse(text)
    funcs = {n.name: n for n in tree.body if isinstance(n, ast.FunctionDef)}
    ctx_cls = next((n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == "Context"), None)
    methods = {n.name: n for n in (ctx_cls.body if ctx_cls else []) if isinstance(n, ast.FunctionDef)}
    bodies = next((n.value for n in tree.body if isinstance(n, ast.Assign)
                   and any(isinstance(t, ast.Name) and t.id == "STAGE_BODIES" for t in n.targets)), None)
    if not isinstance(bodies, ast.Dict):
        raise Refusal("the key derivation found no STAGE_BODIES dict in the driver; it cannot tell which stage reads "
                      "which entry key.")
    for parent in ast.walk(tree):
        for c in ast.iter_child_nodes(parent):
            c._parent = parent
    scanned: dict[str, tuple[set, set]] = {}

    def callee(call):
        f = call.func
        if isinstance(f, ast.Name) and f.id in funcs:
            return f.id, funcs[f.id]
        if isinstance(f, ast.Attribute) and isinstance(f.value, ast.Name) and f.value.id in ("ctx", "self") \
                and f.attr in methods:
            return "Context." + f.attr, methods[f.attr]
        return None, None

    def scan(name, fn):
        if name in scanned:
            return scanned[name]
        keys, calls = set(), set()
        scanned[name] = (keys, calls)
        for node in ast.walk(fn):
            if isinstance(node, ast.Call):
                cname, _ = callee(node)
                if cname:
                    calls.add(cname)
            if not _is_entry_expr(node):
                continue
            up = node._parent
            if isinstance(up, ast.Call) and up.func is node:
                continue
            if isinstance(up, ast.Subscript) and up.value is node:
                k = up.slice
                if isinstance(k, ast.Constant) and isinstance(k.value, str):
                    keys.add(k.value)
                    continue
            elif isinstance(up, ast.Attribute) and up.value is node and up.attr == "get" \
                    and isinstance(up._parent, ast.Call) and up._parent.func is up:
                args = up._parent.args
                if args and isinstance(args[0], ast.Constant) and isinstance(args[0].value, str):
                    keys.add(args[0].value)
                    continue
            elif isinstance(up, ast.Call) and node in up.args:
                cname, cfn = callee(up)
                params = [p.arg for p in cfn.args.args] if cfn is not None else []
                if cname and cname.startswith("Context."):
                    params = params[1:]
                i = up.args.index(node)
                if i < len(params) and params[i].endswith("entry"):
                    continue
            raise Refusal(f"{name} (line {node.lineno}) uses the entry as `{ast.unparse(up)[:80]}`, which the key "
                          f"derivation cannot attribute to a key; read it as entry.get(\"<key>\") or entry[\"<key>\"] "
                          f"so the stage's hash binds what it reads.")
        return keys, calls

    def closure(name, fn):
        seen, todo, keys = set(), [(name, fn)], set()
        while todo:
            n, f = todo.pop()
            if n in seen:
                continue
            seen.add(n)
            k, calls = scan(n, f)
            keys |= k
            for c in calls:
                todo.append((c, methods[c.split(".", 1)[1]] if c.startswith("Context.") else funcs[c]))
        return keys

    out = {k: list(v) for k, v in MEASURING_READS.items()}
    for k, v in zip(bodies.keys, bodies.values):
        if isinstance(k, ast.Constant) and isinstance(v, ast.Name) and v.id in funcs:
            out[k.value] = sorted(closure(v.id, funcs[v.id]) & PRESENTATION_KEYS)
    return out


def stage_entry_hash(entry: dict, measured: dict, reads: list[str]) -> str:
    """A stage's entry binding: the entry without its presentation keys, plus the presentation keys the stage reads.
    A stage that reads none hashes exactly what the measuring stages always hashed."""
    return sha256_json({**measured, **{k: entry[k] for k in reads if k in entry}})


# ---------------------------------------------------------------- disk: measured per-record figures

GIB = 2 ** 30


def _size(b: float) -> str:
    """Bytes in the largest binary unit that keeps them above 1 (GiB, MiB or KiB), to one decimal."""
    for unit, n in (("GiB", GIB), ("MiB", 2 ** 20)):
        if b >= n:
            return f"{b / n:.1f} {unit}"
    return f"{b / 1024:.1f} KiB"


def unique_bytes(folder, top_files: bool = True) -> int:
    """Bytes under a folder with each file counted once: a hard-linked file (item 1's links) is one file. Without
    `top_files` the folder's own files are left out (a run folder's ledger and status, which the driver rewrites)."""
    seen, total = set(), 0
    for dirpath, _, files in os.walk(folder):
        if not top_files and same_path(dirpath, folder):
            continue
        for f in files:
            p = os.path.join(dirpath, f)
            try:
                st = os.stat(p)
            except OSError:
                continue
            key = (st.st_dev, st.st_ino) if st.st_ino else p
            if key not in seen:
                seen.add(key)
                total += st.st_size
    return total


def evaluated_records(res: dict) -> int | None:
    """The records an evaluation reads: each arm's non-baseline plugins' HEDR counts, summed over the arms. None when
    a header cannot be read."""
    headers, base, counts, total = res.get("headers") or {}, set(res.get("baseline") or []), {}, 0
    try:
        for a in res.get("arms") or []:
            for p in a.get("plugins") or []:
                if p in base:
                    continue
                if p not in counts:
                    counts[p] = plugin_records(headers[p]["path"]) or 0
                total += counts[p]
    except (KeyError, OSError, Refusal, struct.error):
        return None
    return total


def disk_floor(machine: dict, out: Path) -> float:
    """The GiB the machine slot keeps free on the run's volume: heavyPrefix's `--min-disk VOLUME=GIB` for that
    volume (idle_lock's argument), else 0."""
    prefix = [str(x) for x in machine.get("heavyPrefix") or []]
    drive = os.path.splitdrive(os.path.abspath(out))[0].upper()
    floor = 0.0
    for i, tok in enumerate(prefix[:-1]):
        if tok == "--min-disk" and "=" in prefix[i + 1]:
            vol, gib = prefix[i + 1].rsplit("=", 1)
            if os.path.splitdrive(os.path.abspath(vol))[0].upper() == drive:
                try:
                    floor = max(floor, float(gib))
                except ValueError:
                    pass
    return floor


def disk_figures(ctx, stages: list[str]) -> dict[str, tuple[float, str]]:
    """{stage: (bytes per evaluated record, where it was measured)}: the largest figure among the accepted (or set
    aside) receipts of that stage in this run, the run folders the machine file lists under diskFigures, else the run
    folders beside this one."""
    listed = ctx.machine.get("diskFigures")
    if listed:
        others = [Path(p) for p in listed]
    else:
        try:
            others = sorted(p for p in Path(ctx.out).parent.iterdir()
                            if p.is_dir() and not same_path(p, ctx.out) and (p / "stages.json").is_file())
        except OSError:
            others = []
    best = {}
    for src in [Path(ctx.out)] + others:
        try:
            doc = read_json(src / "stages.json") if (src / "stages.json").is_file() else {}
        except (OSError, ValueError):
            continue
        for s in stages:
            for rec in ((doc.get("stages") or {}).get(s), (doc.get("kept") or {}).get(s)):
                d = (rec or {}).get("disk") or {}
                n, b = d.get("evaluatedRecords"), d.get("addedBytes")
                if not n or b is None:
                    continue
                per = max(float(b), 0.0) / n
                if s not in best or per > best[s][0]:
                    best[s] = (per, f"{fwd(src)} {s} ({max(b, 0) / GIB:.2f} GiB over {n} records)")
    return best


def disk_forecast(ctx, records: int | None, stages: list[str]) -> tuple[str, str | None]:
    """(the forecast line, a refusal sentence or None) for the stages about to run, from measured per-record figures
    (disk_figures, else the machine file's diskPerRecord {bytes, source}); the floor is the slot's --min-disk."""
    span = f"{stages[0]}..{stages[-1]}" if len(stages) > 1 else stages[0]
    if records is None:
        return (f"disk not forecast for {span}: the run's evaluated records could not be counted from its plugin "
                f"headers."), None
    figs = disk_figures(ctx, stages)
    known = {s: figs[s] for s in stages if s in figs}
    unknown = [s for s in stages if s not in figs]
    per_rec = ctx.machine.get("diskPerRecord") or {}
    whole = float(per_rec["bytes"]) if per_rec.get("bytes") else None
    if not known and whole is None:
        return (f"disk not forecast for {span}: no measured per-record figure exists (no earlier receipt of these "
                f"stages carries one, and the machine file names no diskPerRecord)."), None
    need = records * sum(p for p, _ in known.values())
    sources = [w for _, w in known.values()]
    if whole is not None and (unknown or records * whole > need):
        need = max(need, records * whole)
        sources.append(f"diskPerRecord {whole / 1024:.1f} KiB per record ({per_rec.get('source') or 'source not named'})")
    gaps = f"; no figure for {', '.join(unknown)}" if unknown and whole is None else ""
    usage = shutil.disk_usage(ctx.out)
    floor = disk_floor(ctx.machine, ctx.out)
    drive = os.path.splitdrive(os.path.abspath(ctx.out))[0] or fwd(ctx.out)
    each = need / records / 1024 if records else 0.0
    line = (f"disk forecast for {span}: about {_size(need)} more on {drive} ({records} evaluated records at "
            f"{each:.1f} KiB each, measured at: {'; '.join(sources)}){gaps}; {drive} has {usage.free / GIB:.1f} GiB "
            f"free and keeps a {floor:g} GiB floor.")
    if usage.free - floor * GIB < need:
        return line, (f"the run needs about {_size(need)} more on {drive} for {span} ({records} evaluated "
                      f"records at {each:.1f} KiB each, measured at: {'; '.join(sources)}), and {drive} has "
                      f"{usage.free / GIB:.1f} GiB free, {max(usage.free / GIB - floor, 0):.1f} GiB above its "
                      f"{floor:g} GiB floor; free space there or move the "
                      f"run folder, then run again.")
    return line, None


# ---------------------------------------------------------------- patch-search pairs before a run

SCENE_POLICY = ROOT / "external" / "houseCARL" / ".claude/skills/evaluation-reporting/references/scene-policy.json"


def patch_search_pairs(res: dict) -> list[tuple[str, str]]:
    """The pairs a vignette can be known to name before a run: each plugin of each partner unit with the subject."""
    subject = (res.get("subject") or [None])[0]
    out = []
    for unit in res.get("partners") or []:
        for p in unit:
            if subject and p != subject and (p, subject) not in out:
                out.append((p, subject))
    return out


def patch_search_refusal(ctx, res: dict, through: str) -> str | None:
    """Before census: a run that goes through report-plan needs the entry's patchSearch to cover every partner pair
    (reel_plan.pair_search_gap, the reel plan's own rule), or report-plan stops on it hours later. One sentence naming
    every missing pair, or None."""
    if STAGES.index(through) < STAGES.index("report-plan"):
        return None
    pairs = patch_search_pairs(res)
    if not pairs:
        return None
    import reel_plan
    policy = (read_json(SCENE_POLICY) or {}).get("vignettes") or {}
    said = "; ".join(" and ".join(p) for p in pairs)
    how = ("record them with patch_search.py and name the file in the entry's patchSearch, or run with --through "
           "a stage before report-plan")
    sp = ctx.entry.get("patchSearch")
    if not sp:
        return f"the run goes through report-plan and the entry names no patchSearch for the pairs {said}; {how}."
    try:
        doc = read_json(sp)
    except (OSError, ValueError) as ex:
        return f"the entry's patchSearch {sp} cannot be read ({ex}); {how}."
    if not isinstance(doc, dict) or not isinstance(doc.get("pairs"), list):
        return f"the entry's patchSearch {sp} is not a patch-search document with a `pairs` list; {how}."
    gaps = [g for pair in pairs if (g := reel_plan.pair_search_gap(doc, pair, policy))]
    if not gaps:
        return None
    return reel_plan.pair_search_refusal(gaps, policy, what=f"the entry's patchSearch {sp}") + f" ({how})."


# ---------------------------------------------------------------- driver


def cmd_run(a) -> int:
    out = Path(a.out)
    if len(fwd(out.resolve())) > MAX_PATH - 80:
        raise Refusal(f"the run folder path is {len(fwd(out.resolve()))} characters; probes write deeper files and "
                      f"Windows refuses past 260, so choose a shorter --out.")
    out.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", fwd(out / "runlog"))
    ctx = Context(a.entry, a.machine, out)
    if getattr(ctx, "in_place", None):
        os.environ["HOUSECARL_RUNLOG_DIR"] = fwd(out / "runlog")     # a manual run's log stays in its work folder, set
    ledger = StageLedger(out)
    through = a.through or STAGES[-1]
    refusals = []      # non-blocking refusals: the run goes on, and its final status still names each one
    with Run("evalpipeline", mode=ctx.id, stage_source="counted") as run:
        try:
            res = ctx.resolve()
        except Refusal as r:
            write_json(out / "status.json", {"status": "incomplete", "stoppedAt": STAGES[0], "sentence": str(r), "at": utcnow()})
            print(f"REFUSED: {r}", file=sys.stderr)
            return EXIT_REFUSED
        # the pairs a vignette can name are searched before anything runs, not found missing at report-plan
        why = patch_search_refusal(ctx, res, through)
        if why:
            write_json(out / "status.json", {"status": "incomplete", "stoppedAt": STAGES[0], "sentence": why,
                                             "at": utcnow()})
            print(f"REFUSED: {why}", file=sys.stderr)
            return EXIT_REFUSED
        reads = stage_entry_keys()
        records = evaluated_records(res)
        forecast_line, size_before = None, None
        run.stage("stages", "pipeline stages", STAGES.index(through) + 1, "stages")
        for name in STAGES[:STAGES.index(through) + 1]:
            body = STAGE_BODIES.get(name)
            sdir = out / name
            # which stage is going, and since when, for a reader of the run record (the workbench's Start tab)
            run.note(f"stage {name} started {utcnow()}")
            run.liveness()
            # the entry, every earlier stage's binding and, after the bundle, the stage's own tools decide reuse; the
            # driver's own code is recorded, not bound, because the measurements' tool identities are bound inside
            # the controller's plan
            measured = {k: v for k, v in ctx.entry.items() if k not in PRESENTATION_KEYS}
            # a budget-only change touches evaluate and what follows it: earlier stages hash the budget their plan froze,
            # so a run with an unchanged budget hashes byte for byte as it always did
            limit = budget_differs(ctx.entry, out)
            if limit is not None:
                frozen = float(planned_budget(out))
                measured["evaluation"] = dict(measured["evaluation"], budgetMinutes=int(frozen) if frozen == int(frozen) else frozen)
            # each stage binds the presentation keys its own code reads (stage_entry_keys), so a patch search reruns
            # report-plan and later, never the bundle
            entry_hash = stage_entry_hash(ctx.entry, measured, reads.get(name, []))
            earlier = STAGES[:STAGES.index(name)]
            inputs = {"entry": entry_hash,
                      "upstream": {s: (ledger.doc["stages"].get(s) or {}).get("inputsSha256") for s in earlier}}
            if limit is not None and name == "evaluate":
                inputs["budgetInForce"] = limit
            tools = stage_tools(name, ctx.machine)
            if tools:
                inputs["tools"] = tools
            if name == "plan" and (out / "scope" / "scope-line.json").is_file():
                print("scope: " + str(read_json(out / "scope" / "scope-line.json").get("line") or ""))
            if ledger.decide(name, inputs) == "reuse":
                print(f"{name}: reused (inputs and outputs unchanged)")
                run.advance()
                continue
            if body is None and name in MEASURING_NON_BLOCKING:
                # the controller recorded this stage refused and not blocking: its sentence is carried on, as it was
                said = next((h.get("reason") for h in reversed(ledger.doc["history"]) if h.get("stage") == name
                             and str(h.get("reason") or "").startswith("refused: ")), None)
                sentence = said[len("refused: "):] if said else measuring_refusal(ctx, name)
                print(f"{name}: refused, not blocking: {sentence}")
                refusals.append({"stage": name, "sentence": sentence})
                run.advance()
                continue
            if body is None:
                sentence = measuring_refusal(ctx, name) if name in MEASURING else f"{name} is not built in this version"
                write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "sentence": sentence, "at": utcnow()})
                print(f"{name}: REFUSED: {sentence}", file=sys.stderr)
                return EXIT_REFUSED
            sdir.mkdir(exist_ok=True)
            if forecast_line is None:
                # once, before the first stage that will run: the disk this run still needs, from measured figures
                todo = list(STAGES[STAGES.index(name):STAGES.index(through) + 1])
                forecast_line, short = disk_forecast(ctx, records, todo)
                write_json(out / "disk-forecast.json", {"line": forecast_line, "refused": short, "stages": todo,
                                                        "evaluatedRecords": records, "at": utcnow()})
                print(forecast_line)
                if short:
                    write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "sentence": short,
                                                     "at": utcnow()})
                    print(f"{name}: REFUSED: {short}", file=sys.stderr)
                    return EXIT_REFUSED
            if size_before is None:
                size_before = unique_bytes(out, top_files=False)
            t0 = time.perf_counter()
            try:
                extra, outputs = body(ctx, res, sdir)
            except Awaiting as w:
                ledger.record(name, inputs, [], exit_code=3, extra=None)
                ledger.doc["history"][-1]["reason"] = "awaiting: " + str(w)
                ledger._save()
                write_json(out / "status.json", {"status": "awaiting-judgement", "stoppedAt": name, "sentence": str(w),
                                                 "at": utcnow()})
                print(f"{name}: AWAITING: {w}")
                return EXIT_JUDGEMENT
            except Refusal as r:
                ledger.record(name, inputs, [], exit_code=2, extra=None)
                ledger.doc["history"][-1]["reason"] = "refused: " + str(r)
                ledger._save()
                size_before = None   # a refused stage's writes are not the next stage's
                outcome = stage_outcome(name, 2, False, str(r))
                if isinstance(r, NonBlockingRefusal):
                    outcome["blocksLater"] = False
                write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "sentence": str(r),
                                                 "at": utcnow()})
                if outcome["blocksLater"]:
                    print(f"{name}: REFUSED: {r}", file=sys.stderr)
                    return EXIT_REFUSED
                print(f"{name}: refused, not blocking: {r}")
                refusals.append({"stage": name, "sentence": str(r)})
                run.advance()
                continue
            secs = round(time.perf_counter() - t0, 1)
            size_after = unique_bytes(out, top_files=False)
            ledger.record(name, inputs, outputs, exit_code=0,
                          extra={"bound": extra, "seconds": secs, "driverSha256": sha256_file(Path(__file__)),
                                 # what this stage added to the run folder, each file once, for later forecasts
                                 "disk": {"addedBytes": size_after - size_before, "evaluatedRecords": records}})
            size_before = size_after
            print(f"{name}: accepted in {secs} s")
            run.advance()
        if refusals:
            write_json(out / "status.json", {"status": "incomplete", "through": through, "sentence": refusals[0]["sentence"],
                                             "refusals": refusals, "at": utcnow()})
        else:
            write_json(out / "status.json", {"status": "through " + through, "at": utcnow()})
        if forecast_line:
            st = read_json(out / "status.json")
            write_json(out / "status.json", dict(st, diskForecast=forecast_line))
    return EXIT_OK


def cmd_capture_rounds(a) -> int:
    """The capture stage's rounds, run inside the one machine-slot hold the stage took; its outcome is written to
    RUN/capture/capture-rounds.json for the stage to read."""
    out = Path(a.out)
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", fwd(out / "runlog"))
    ctx = Context(a.entry, a.machine, out)
    sdir = out / "capture"
    sdir.mkdir(exist_ok=True)
    with Run("evalpipeline", mode=ctx.id + ":capture-rounds", stage_source="counted"):
        try:
            bound, _ = capture_rounds(ctx, ctx.resolve(), sdir)
        except Refusal as r:
            write_json(sdir / "capture-rounds.json", {"refused": str(r), "at": utcnow()})
            print(f"capture-rounds: REFUSED: {r}", file=sys.stderr)
            return EXIT_REFUSED
        write_json(sdir / "capture-rounds.json", {"accepted": True, "bound": bound, "at": utcnow()})
    return EXIT_OK


def cmd_status(a) -> int:
    led = StageLedger(a.out)
    for s in STAGES:
        rec = led.doc["stages"].get(s)
        last = next((h for h in reversed(led.doc["history"]) if h.get("stage") == s), None)
        if rec:
            print(f"{s:20s} accepted {rec['at']} ({rec.get('seconds')} s)")
        elif last:
            print(f"{s:20s} not accepted ({last['at']}): {last.get('reason') or 'exit ' + str(last.get('exitCode'))}")
        else:
            print(f"{s:20s} not run")
    st = Path(a.out) / "status.json"
    if st.is_file():
        print(json.dumps(read_json(st)))
    return EXIT_OK


def abs_path(s: str) -> Path:
    """A path argument made absolute at parse time (N4): children run from the repo root, so a relative --entry
    would be read there (the held capture-rounds child failed on one)."""
    return Path(s).expanduser().resolve()


def build_parser():
    ap = argparse.ArgumentParser(prog="evalpipeline", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("run", help="run every stage after the bundle not yet accepted, in order, on a run folder the "
                                   "built-in controller (runcontrol) carried through the bundle")
    p.add_argument("--entry", type=abs_path, required=True, help="the mod's selection entry (JSON)")
    p.add_argument("--out", type=abs_path, required=True, help="the run folder")
    p.add_argument("--machine", type=abs_path, help="the machine file: lab, game, heavy-stage prefix, run-local stage commands")
    p.add_argument("--through", choices=STAGES, help="stop after this stage")
    p.set_defaults(fn=cmd_run)
    p = sub.add_parser("capture-rounds", help="the capture stage's rounds, inside the machine-slot hold the stage took "
                                              "(the stage runs it; not for direct use)")
    p.add_argument("--entry", type=abs_path, required=True)
    p.add_argument("--out", type=abs_path, required=True)
    p.add_argument("--machine", type=abs_path)
    p.set_defaults(fn=cmd_capture_rounds)
    p = sub.add_parser("status", help="print the accepted stages")
    p.add_argument("--out", type=abs_path, required=True)
    p.set_defaults(fn=cmd_status)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except Refusal as r:
        print(f"REFUSED: {r}", file=sys.stderr)
        return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
