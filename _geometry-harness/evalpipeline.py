"""evalpipeline - one mod's evaluation, end to end, from its selection entry (one command per mod).

    python _geometry-harness/evalpipeline.py run    --entry ENTRY.json --out RUN [--machine MACHINE.json] [--through STAGE]
    python _geometry-harness/evalpipeline.py arms   --entry ENTRY.json [--machine MACHINE.json]
    python _geometry-harness/evalpipeline.py status --out RUN

What this is for. A shakedown evaluates dozens of mods. Each mod used to get its own preparation, scope, controller and
scene scripts, each reviewed on its own; the method was the same every time and only the roles changed. This driver
holds the method once. A selection entry names the roles (subject, partner units, keys, companions, extra arms, order
variants) and the machine; everything else is derived or refused:

  arms      `derive_arms`: dependencies; subject; each partner unit; partners together (more than one unit);
            combined; subject with each unit (more than one unit); per key, combined + key and, when the key masters
            exactly one partner unit, subject + that unit + key; combined + every key (more than one key); then the
            entry's extra arms and order variants. Arms with the same plugins and order rule collapse to the first.
  order     `order_arm`: the source profile's load order (plus the entry's `places` for plugins it does not load),
            ESM-flagged plugins first (the game's rule), keys absent from the source after everything; a late or
            missing master refuses by name. Asset providers follow the plugin order (a later plugin's folder wins),
            keys' folders strongest.
  stages    census, prepare, scope, plan, evaluate, records, scenes, bundle, precedent, report-plan, capture,
            image-observations, report, reel, deliver. Each stage is a receipt in RUN/stages.json bound to the hashes
            of its inputs and outputs; `run` reuses an accepted stage whose bindings are unchanged and reruns one that
            changed. A stage that exits 0 without its outputs is not accepted. The stages after the bundle also bind
            the code of the tools they run (STAGE_TOOLS: each script, the harness modules it imports, the renderer's
            build), so a tool change runs that stage again; the measuring stages' tools are bound by evalrun's plan,
            which refuses a resume under a changed program.

What it does not do. It measures nothing itself and decides nothing about a mod: every finding comes from the probes,
evalrun's ledger and the record reader. It never certifies absence on an empty read: an incomplete scope, an empty
enumeration where a touching plugin loads, a missing output or a stage refusal stops that stage with one sentence, and
the run stays `incomplete`. A precedent refusal does not block the report or reel. Where a judgement must be made by
a person or a model (blind image observations, the final reviews) the run stops with that status and continues when
the judgement file exists.

The machine file (machine-local, never tracked) names the lab instance, the game, a command prefix for heavy stages
(the machine's slot and idle queue), how many arms the records stage reads at once (recordWorkers: a number or auto, from
available memory; auto by default, 1 for one at a time) and the commands of run-local stages such as the precedent cards.
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

REGISTRY = ROOT / ".claude/skills/world-edit-compatibility/references/check-registry.json"
STAGES = ("census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle", "precedent",
          "report-plan", "capture", "image-observations", "report", "reel", "deliver")
NON_BLOCKING = frozenset({"precedent"})
#: entry fields that change only what is presented (names, patch search, report shape); measuring stages ignore them
PRESENTATION_KEYS = frozenset({"modNames", "nexusNames", "patchSearch", "report", "captureSize"})
EXIT_OK, EXIT_REFUSED, EXIT_JUDGEMENT = 0, 2, 3
MAX_PATH = 240  # leave room under Windows' 260-character limit for the probes' own file names


class Refusal(Exception):
    """One sentence: what went wrong and what to try."""


# ---------------------------------------------------------------- small helpers

def read_json(p):
    return json.loads(Path(p).read_text(encoding="utf-8-sig"))


def write_json(p, obj):
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_name(p.name + ".tmp")
    tmp.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, p)


def sha256_file(p) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def sha256_json(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def fwd(p) -> str:
    """A path in forward-slash spelling: argument parsers downstream have lost backslashes before."""
    return str(p).replace("\\", "/")


def same_path(a, b) -> bool:
    return os.path.normcase(os.path.normpath(str(a))) == os.path.normcase(os.path.normpath(str(b)))


def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# ---------------------------------------------------------------- plugin headers

def plugin_header(path) -> dict:
    """The TES4 header of one plugin: masters, ESM/ESL flags and the file's sha256. Refuses a file that is not a plugin."""
    p = Path(path)
    with open(p, "rb") as f:
        head = f.read(24)
        if len(head) < 24 or head[:4] != b"TES4":
            raise Refusal(f"{p.name} is not a plugin (no TES4 header); check the file at {fwd(p)}.")
        size, flags = struct.unpack_from("<II", head, 4)
        body = f.read(size)
    masters, i, big = [], 0, None
    while i + 6 <= len(body):
        sig = body[i:i + 4]
        n = struct.unpack_from("<H", body, i + 4)[0]
        if big is not None:
            n, big = big, None
        data = body[i + 6:i + 6 + n]
        if sig == b"XXXX":
            big = struct.unpack_from("<I", data, 0)[0]
        elif sig == b"MAST":
            masters.append(data.split(b"\0", 1)[0].decode("cp1252"))
        i += 6 + n
    return {"path": fwd(p), "sha256": sha256_file(p), "masters": masters, "esm": bool(flags & 0x1),
            "esl": bool(flags & 0x200)}


def is_master(name: str, headers: dict) -> bool:
    h = headers.get(name) or {}
    return bool(h.get("esm")) or name.lower().endswith((".esm", ".esl"))


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

def _units(entry) -> list[list[str]]:
    units, by_group = [], {}
    for p in entry.get("partners", []):
        plugins = list(p.get("plugins") or [p["plugin"]])
        g = p.get("group")
        if g is None:
            units.append(plugins)
        elif g in by_group:
            by_group[g].extend(x for x in plugins if x not in by_group[g])
        else:
            by_group[g] = plugins
            units.append(plugins)
    return units


def derive_arms(entry: dict, headers: dict) -> list[dict]:
    """The arms of one evaluation from its selection entry (see the module docstring for the rule)."""
    subj = list(entry["subject"].get("plugins") or [entry["subject"]["plugin"]])
    units = _units(entry)
    keys = [k["plugin"] for k in entry.get("keys", [])]
    allp = subj + [p for u in units for p in u]
    arms, seen = [], set()

    def add(aid, evaluated, ks=(), variant=None, rule=None):
        ev = list(dict.fromkeys(evaluated))
        sig = (frozenset(ev), json.dumps(rule or {}, sort_keys=True))
        if sig in seen:
            return
        seen.add(sig)
        role = ("vanilla" if not ev else "patched" if ks else "combined" if set(ev) == set(allp)
                else "source" if set(ev) == set(subj) or any(set(ev) == set(u) for u in units) else "combined")
        arm = {"id": aid, "evaluated": ev, "keys": list(ks), "role": role, "variant": variant}
        arm.update(rule or {})
        arms.append(arm)

    add("dependencies", [])
    add("subject", subj)
    for i, u in enumerate(units):
        add(f"unit:{i}", u)
    if len(units) > 1:
        add("partners", [p for u in units for p in u])
    add("combined", allp)
    if len(units) > 1:
        for i, u in enumerate(units):
            add(f"subject+unit:{i}", subj + u)
    for k in keys:
        add(f"combined+key:{k}", allp + [k], [k])
        masters = set((headers.get(k) or {}).get("masters", []))
        owners = [i for i, u in enumerate(units) if masters & set(u)]
        if len(owners) == 1:
            add(f"pair:{owners[0]}+key:{k}", subj + units[owners[0]] + [k], [k])
    if len(keys) > 1:
        add("combined+keys", allp + keys, keys)
    for x in entry.get("extraArms", []):
        ks = [p for p in x["plugins"] if p in keys]
        add(x["id"], x["plugins"], ks)
    by_id = {a["id"]: a for a in arms}
    for v in entry.get("orderVariants", []):
        base = by_id.get(v["of"])
        if base is None:
            raise Refusal(f"order variant {v['id']} names arm {v['of']}, which this entry does not derive; "
                          f"name one of: {', '.join(by_id)}.")
        rule = {k: v[k] for k in ("reverse", "last") if k in v}
        add(v["id"], base["evaluated"], base["keys"], variant=v["id"], rule=rule)
    return arms


# ---------------------------------------------------------------- the order rule

def order_arm(arm: dict, baseline: list[str], source_order: list[str], headers: dict) -> list[str]:
    """The arm's plugins in load order; refuses an illegal order by naming the plugin."""
    keys = set(arm.get("keys") or [])
    plugins = list(dict.fromkeys(list(baseline) + list(arm["evaluated"])))
    index = {p.lower(): i for i, p in enumerate(source_order)}
    for p in plugins:
        if p.lower() not in index and p not in keys:
            raise Refusal(f"{p} is not in the source profile's load order; add it there or name its place in the entry's places ({{plugin: {{after: plugin}}}}).")
    late = len(source_order)

    def pos(p):
        return index.get(p.lower(), late + list(arm.get("keys") or []).index(p) if p in keys else late)

    out = sorted(plugins, key=lambda p: (0 if is_master(p, headers) else 1, pos(p)))
    if arm.get("reverse"):
        slots = [i for i, p in enumerate(out) if p in arm["evaluated"] and p not in keys]
        moved = [out[i] for i in slots][::-1]
        for i, p in zip(slots, moved):
            out[i] = p
    if arm.get("last"):
        tail = [p for p in arm["last"] if p in out]
        out = [p for p in out if p not in tail] + tail
    where = {p.lower(): i for i, p in enumerate(out)}
    seen_plain = None
    for i, p in enumerate(out):
        h = headers.get(p)
        if h is None:
            raise Refusal(f"{p} has no header read; the preparation could not find its file.")
        for m in h.get("masters", []):
            j = where.get(m.lower())
            if j is None:
                raise Refusal(f"{p} needs master {m}, which arm {arm.get('id', '?')} does not load.")
            if j > i:
                raise Refusal(f"{p} loads before its master {m} in arm {arm.get('id', '?')}; fix the source order or the variant.")
        if is_master(p, headers):
            if seen_plain is not None:
                raise Refusal(f"{p} is ESM-flagged but loads after {seen_plain}, which the game would reorder; "
                              f"drop the variant that moves it.")
        elif seen_plain is None:
            seen_plain = p
    return out


# ---------------------------------------------------------------- scope acceptance

def _named(row, key):
    return row.get(key) if key in row else row.get(key[:1].upper() + key[1:])


def accept_scope(scope: dict, expect: dict) -> dict:
    """Accept one arm's scopeprobe output against what the arm prepared, or refuse with the reason."""
    if scope.get("complete") is not True or scope.get("walkFailures") or scope.get("unreadableRecords"):
        raise Refusal("the scope is incomplete (complete flag, walk failures or unreadable records); "
                      "read its log and rerun the scope after fixing the cause.")
    cfg = scope.get("configuration") or {}
    rows = cfg.get("plugins") or []
    want = expect["plugins"]
    if [_named(r, "name") for r in rows] != [w["name"] for w in want]:
        raise Refusal("the scope's plugin order differs from the arm's prepared order; the scope read another view.")
    for r, w in zip(rows, want):
        if not same_path(_named(r, "path") or "", w["path"]) or _named(r, "sha256") != w["sha256"]:
            raise Refusal(f"{w['name']}: the scope read other bytes or another provider than the prepared copy.")
    mods = [_named(r, "name") for r in cfg.get("providers", []) if str(_named(r, "kind")).lower() == "mod"]
    if mods != list(expect["mods"]) or not cfg.get("fingerprint"):
        raise Refusal("the scope's asset provider view differs from the arm's prepared view.")
    targets = scope.get("targets") or []
    if sorted(t.get("name") for t in targets) != sorted(expect["targets"]):
        raise Refusal(f"the scope's target population {sorted(t.get('name') for t in targets)} differs from the "
                      f"requested target {sorted(expect['targets'])}.")
    shas = {w["name"]: w["sha256"] for w in want}
    out = {}
    for t in targets:
        if t["name"] in shas and t.get("sha256") != shas[t["name"]]:
            raise Refusal(f"{t['name']}: the scope's target bytes differ from the prepared copy.")
        placed = (t.get("footprint") or {}).get("placed") or {}
        cells = {c["cell"] for c in placed.get("cells", [])
                 if c.get("kind") != "persistent" and not str(c["cell"]).startswith("grid:")}
        census = set((expect.get("censusCells") or {}).get(t["name"], []))
        missing = sorted(census - cells)
        if missing:
            raise Refusal(f"{t['name']}: the scope misses census cells {', '.join(missing)}; reconcile the census and "
                          f"the arm's view before evaluating.")
        out[t["name"]] = {"measuredCells": sorted(cells), "censusCells": sorted(census), "missingCensusCells": missing,
                          "additionalCells": sorted(cells - census) if census else []}
    return {"targets": out}


# ---------------------------------------------------------------- stage receipts

#: the skill data and templates the report and reel words come from (evalreport's own compiler_hashes)
REPORT_DATA = ("_geometry-harness/evalreport_templates", ".claude/skills/evaluation-reporting/references/report-priorities.json",
               ".claude/skills/evaluation-reporting/references/scene-policy.json")
#: the code each stage after the bundle runs, repository-relative: the scripts it launches and those they load by path
#: (a harness module a listed script imports is followed by stage_tools itself). The measuring stages (census to
#: bundle) are not here: evalrun's plan freezes every program it launches and refuses a resume under a changed one, and
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
#: this driver: modules a tool imports may import it, and its code is recorded on every receipt, not bound
DRIVER = "_geometry-harness/evalpipeline.py"
DEFAULT_VIEWPROBE_DIR = Path("_geometry-harness/viewprobe/bin/Release/net9.0-windows10.0.19041.0")


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
        folder = Path(vp).parent if vp else root / DEFAULT_VIEWPROBE_DIR
        seen["viewprobe"] = toolidentity.package(folder)["digest"]
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

    def decide(self, stage: str, inputs: dict, legacy: dict | None = None) -> str:
        """'reuse' when the accepted receipt matches these inputs and its outputs are unchanged, else 'run'. Two more
        ways to reuse, each with a history line: a receipt keyed by the earlier entry rule (`legacy`: the inputs as the
        02d9ea09 driver hashed them) is re-keyed in place when that rule matches, so a rule change alone reruns nothing;
        and a receipt a later refusal set aside (`kept`) is put back when the inputs are again the ones it was accepted
        under, so restoring an input restores the stage."""
        want = sha256_json(inputs)
        rec = self.doc["stages"].get(stage)
        if rec and rec.get("inputsSha256") == want:
            return "reuse" if self._intact(rec) else "run"
        if rec and legacy is not None and rec.get("inputsSha256") == sha256_json(legacy) and self._intact(rec):
            old = rec["inputsSha256"]
            rec.update(inputs=inputs, inputsSha256=want, upgradedFrom=old)
            self.doc["history"].append({"stage": stage, "at": utcnow(), "rekeyed": True, "from": old, "to": want,
                                        "reason": "the receipt was re-keyed under the rule that binds each stage to the "
                                                  "presentation keys it reads; its outputs are unchanged"})
            self._save()
            return "reuse"
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

    # -- plugin files
    def provider_of(self, plugin: str) -> tuple[str | None, Path]:
        """(mod folder or None for the game's Data, file) for one plugin in the lab; refuses an absent or ambiguous one."""
        named = {}
        for role in [self.entry["subject"]] + self.entry.get("partners", []) + self.entry.get("keys", []) \
                + self.entry.get("dependencies", []) + [c for c in self.entry.get("companionsOf", [])]:
            for p in (role.get("plugins") or [role.get("plugin")]):
                if p and role.get("mod"):
                    named.setdefault(p.lower(), role["mod"])
        mod = named.get(plugin.lower())
        if mod:
            f = self.instance / "mods" / mod / plugin
            if not f.is_file():
                raise Refusal(f"{plugin} is not in the lab mod folder {mod}; copy it in (copy-in) or fix the entry.")
            return mod, f
        data = self.game / "Data" / plugin
        if data.is_file():
            return None, data
        hits = [d.name for d in (self.instance / "mods").iterdir() if (d / plugin).is_file()]
        if len(hits) == 1:
            return hits[0], self.instance / "mods" / hits[0] / plugin
        if not hits:
            raise Refusal(f"{plugin} is in no lab mod folder and not in the game's Data; copy it into the lab first.")
        raise Refusal(f"{plugin} is in {len(hits)} lab mod folders ({', '.join(hits[:4])}); name its folder in the "
                      f"entry's dependencies.")

    def source_order(self) -> list[str]:
        lo = self.source_instance / "profiles" / self.source_profile / "loadorder.txt"
        if not lo.is_file():
            raise Refusal(f"the source profile has no loadorder.txt at {fwd(lo)}.")
        return self.names.many(l.strip() for l in lo.read_text(encoding="utf-8-sig").splitlines()
                               if l.strip() and not l.startswith("#"))

    def resolve(self) -> dict:
        """Headers, baseline and every arm's ordered plugins, providers and mods."""
        if self.in_place:
            return resolve_in_place(self)
        e = self.entry
        subj = list(e["subject"].get("plugins") or [e["subject"]["plugin"]])
        keys = [k["plugin"] for k in e.get("keys", [])]
        partner_plugins = [p for u in _units(e) for p in u]
        companions = {k.lower(): v for k, v in (e.get("companions") or {}).items()}
        source = self.source_order()   # read before the headers: its spellings outrank theirs
        places = e.get("places") or {}
        for p, where in places.items():
            if p in source:
                raise Refusal(f"{p} is in the source profile's load order already; drop it from the entry's places.")
            after = (where or {}).get("after")
            if after not in source:
                raise Refusal(f"places: {p} is to load after {after}, which the source profile does not load; "
                              f"name a plugin it loads.")
            source.insert(source.index(after) + 1, p)
        headers, providers, todo = {}, {}, list(subj + partner_plugins + keys + list(e.get("baseline", [])))
        for c in companions.values():
            todo += c
        while todo:
            p = self.names(todo.pop(0))
            if p in headers:
                continue
            mod, f = self.provider_of(p)
            headers[p] = plugin_header(f)
            headers[p]["masters"] = self.names.many(headers[p]["masters"])
            providers[p] = mod
            todo += headers[p]["masters"]
        for p in places:
            # a place is checked against the plugin's own masters: one that loads later would be a broken order
            late = sorted((m for m in (headers.get(p) or {}).get("masters", [])
                           if m in source and source.index(m) > source.index(p)), key=source.index)
            if late:
                raise Refusal(f"places: {p} after {places[p].get('after')} loads before its master {late[0]}; "
                              f"place it after {late[-1]}.")
        evaluated = set(subj + partner_plugins + keys)
        # baseline: the entry's pins plus every master closure of the subject and keys outside the evaluated set
        base = list(e.get("baseline", []))

        def closure(p, acc):
            for m in headers[p]["masters"]:
                if m not in acc:
                    acc.append(m)
                    closure(m, acc)
            return acc
        for p in subj + keys:
            for m in closure(p, []):
                if m not in evaluated and m not in base:
                    base.append(m)
        # a partner unit carries its own masters that are not baseline, and its companions
        entry_units = []
        for p in e.get("partners", []):
            plugins = list(p.get("plugins") or [p["plugin"]])
            for q in list(plugins):
                for m in closure(q, []):
                    if m not in base and m not in evaluated and m not in plugins:
                        plugins.append(m)
                for c in companions.get(q.lower(), []):
                    if c not in plugins:
                        plugins.append(c)
            entry_units.append(dict(p, plugins=plugins))
        subj_full = list(subj)
        for q in subj:
            subj_full += [c for c in companions.get(q.lower(), []) if c not in subj_full]
        resolved = dict(e, partners=entry_units, subject=dict(e["subject"], plugins=subj_full))
        arms = derive_arms(resolved, headers)
        key_mods = {k["plugin"]: k.get("mod") for k in e.get("keys", [])}
        for a in arms:
            a["plugins"] = order_arm(a, base, source, headers)
            mods = []
            for k in reversed([p for p in a["plugins"] if p in keys]):
                if key_mods.get(k):
                    mods.append(key_mods[k])
            for p in reversed(a["plugins"]):
                m = providers.get(p)
                if m and m not in mods and p not in keys:
                    mods.append(m)
            a["mods"] = mods
        return {"headers": headers, "providers": providers, "baseline": base, "arms": arms,
                "subject": subj, "partners": [u["plugins"] for u in entry_units], "keys": keys}


# ---------------------------------------------------------------- preparation

#: the plugins the game loads whether or not plugins.txt lists them (Mo2LoadOrder's implicit masters)
OFFICIAL_MASTERS = ("Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm")


def _profile_lines(path: Path) -> list[str]:
    try:
        return [l.strip() for l in path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
                if l.strip() and not l.lstrip().startswith("#")]
    except OSError:
        return []


def _mods_by_priority(mpath) -> list[str]:
    """Every mod folder of a modlist.txt, enabled or not, strongest first (the file's own order); separators skipped."""
    out = []
    for line in _profile_lines(Path(mpath)):
        if line[0] in "+-" and line[1:].strip() and not line[1:].strip().lower().endswith("_separator"):
            out.append(line[1:].strip())
    return out


def resolve_in_place(ctx: Context) -> dict:
    """An in-place entry's two arms, from the chosen profile as MO2 resolves it: `combined` = the profile with the
    selected mod folders enabled at their MO2 priority, `without-selection` = the profile with them disabled (for a
    selection of mods MO2 has disabled, that is the profile as it stands). A plugin only selected folders provide drops
    from the baseline; one an unselected folder (or the game's Data) also provides stays, from there. A selected
    disabled folder's plugins that the profile does not already load are loaded the way MO2 places a newly enabled
    plugin: at the end of the load order, a master-flagged one at the end of the masters. The profile's files are never
    changed; both arms are candidates over it. The plugin providers come from evalrun.plugin_layers, the one Python
    spelling of the probes' layer map; the census stage cross-checks the result against scopeprobe's own resolution."""
    import evalrun as ER
    inst, prof, game = str(ctx.instance), ctx.profile, str(ctx.game)
    pdir = ctx.instance / "profiles" / prof
    enabled, disabled, read, mpath = ER._modlist(inst, prof)
    if not read:
        raise Refusal(f"the profile {prof} has no readable modlist.txt at {fwd(pdir)}; name a profile of this instance.")
    sel = list((ctx.entry.get("selection") or {}).get("mods") or [])
    if not sel:
        raise Refusal("the selection names no mod folder; tick at least one of the profile's mods.")
    known = {m.lower() for m in list(enabled) + list(disabled)}
    for m in sel:
        if m.lower() not in known:
            raise Refusal(f"{m} is not a mod folder of profile {prof}; pick one of the mods its modlist.txt lists.")
    sel_low = {m.lower() for m in sel}
    enabled_low = {m.lower() for m in enabled}
    switched_on = [m for m in disabled if m.lower() in sel_low]           # selected mods MO2 has disabled
    for need in ("loadorder.txt", "plugins.txt"):
        if not (pdir / need).is_file():
            raise Refusal(f"the profile {prof} has no {need} at {fwd(pdir)}, so its load order cannot be read; open the "
                          f"profile in MO2 once so it writes the file, or pick another profile.")
    order = ctx.names.many(_profile_lines(pdir / "loadorder.txt"))
    # Mo2LoadOrder.ReadComposition's rule exactly: a name plugins.txt lists without `*` is unchecked and does not load;
    # every other name in loadorder.txt loads, starred or not listed at all (force-loaded: base game, Creation Club)
    listed = _profile_lines(pdir / "plugins.txt")
    unchecked = {l.strip().lower() for l in listed if not l.startswith("*")}
    active = [p for p in order if p.lower() not in unchecked]
    without_mods = [m for m in enabled if m.lower() not in sel_low]
    candidate_mods = [m for m in _mods_by_priority(mpath) if m.lower() in enabled_low or m.lower() in sel_low]

    def view(mods, names):
        plugins, providers, headers = [], {}, {}
        for p in names:
            rows = ER.plugin_layers(p, mods, instance=inst, profile=prof, game=game, hash_rows=False)
            in_view = [r for r, v in zip(rows["rows"], rows["views"]) if v == "in-view"]
            if not in_view:
                continue                                   # nothing in this view provides it: MO2 would not load it
            layer, path = in_view[0]["layer"], in_view[0]["path"]
            plugins.append(p)
            providers[p] = None if layer in ("Data", "overwrite") else layer
            headers[p] = plugin_header(path)
            headers[p]["masters"] = ctx.names.many(headers[p]["masters"])
        return plugins, providers, headers

    # a switched-on folder's plugins MO2 does not already load, weakest folder first, in name order within a folder
    active_low = {p.lower() for p in active}
    added = []
    for m in reversed([x for x in candidate_mods if x.lower() in {y.lower() for y in switched_on}]):
        folder = ctx.instance / "mods" / m
        files = sorted((f.name for f in folder.iterdir() if f.is_file() and f.suffix.lower() in (".esp", ".esm", ".esl")),
                       key=str.lower) if folder.is_dir() else []
        for f in ctx.names.many(files):
            if f.lower() not in active_low and f.lower() not in {a.lower() for a in added}:
                added.append(f)
    c_plugins, c_providers, c_headers = view(candidate_mods, active + added)
    if added:
        # MO2 appends a newly enabled plugin; the engine loads every master-flagged plugin before any other, so a
        # master-flagged one goes at the end of the masters
        new = [p for p in c_plugins if p in added]
        kept = [p for p in c_plugins if p not in added]
        last = max((i for i, p in enumerate(kept) if is_master(p, c_headers)), default=-1)
        c_plugins = (kept[:last + 1] + [p for p in new if is_master(p, c_headers)] + kept[last + 1:]
                     + [p for p in new if not is_master(p, c_headers)])
    w_plugins, w_providers, w_headers = view(without_mods, active)
    subject = [p for p in c_plugins if (c_providers.get(p) or "").lower() in sel_low and p not in w_plugins]
    arms = [{"id": "combined", "role": "the profile with the selected mods enabled" if switched_on else "the profile as it is",
             "keys": [], "evaluated": subject, "plugins": c_plugins, "mods": candidate_mods,
             "switchedOn": switched_on, "appendedPlugins": [p for p in added if p in c_plugins]},
            {"id": "without-selection", "role": "the profile without the selected mods", "keys": [], "evaluated": [],
             "plugins": w_plugins, "mods": without_mods}]
    headers = {**w_headers, **c_headers}
    providers = {**w_providers, **c_providers}
    return {"headers": headers, "providers": providers, "baseline": w_plugins, "arms": arms, "subject": subject,
            "partners": [], "keys": [], "selection": sel, "withoutProviders": w_providers}


def in_place_select(ctx: Context) -> tuple[list[str], str]:
    """The entry's ticked checks closed over the registry: every prerequisite of a ticked check, every derived check
    whose prerequisites are all selected and which evalrun computes, and depth deep when a ticked check judges a state
    other than the baseline. Returns (check ids, depth)."""
    reg = {c["id"]: c for c in read_json(REGISTRY)["checks"]}
    sel = set((ctx.entry.get("evaluation") or {}).get("select") or [])
    unknown = sorted(c for c in sel if c not in reg)
    if unknown:
        raise Refusal(f"the selection names {', '.join(unknown)}, which the check registry does not have.")
    todo = list(sel)
    while todo:
        c = todo.pop()
        for pre in reg[c].get("prerequisites") or []:
            if pre not in sel:
                sel.add(pre)
                todo.append(pre)
    computed_derived = {"patch.order-evaluation", "patch.candidate-qualification"}   # evalrun's own derived set
    changed = True
    while changed:
        changed = False
        for cid, c in reg.items():
            if ((c.get("adapter") or {}).get("kind") == "derived" and cid in computed_derived and cid not in sel
                    and all(p in sel for p in c.get("prerequisites") or [])):
                sel.add(cid)
                changed = True
    deep = any((reg[c].get("inputs") or {}).get("states") == "selected" for c in sel)
    depth = "deep" if deep else ((ctx.entry.get("evaluation") or {}).get("depth") or "standard")
    return sorted(sel), depth


def candidate_files(ctx: Context, res: dict) -> list[dict]:
    """Candidate configurations: the combined arm is candidate-00-baseline.json (evalrun's baseline), the rest in order."""
    first = "without-selection" if getattr(ctx, "in_place", None) else "combined"
    arms = sorted(res["arms"], key=lambda a: 0 if a["id"] == first else 1)
    out = []
    for i, a in enumerate(arms):
        name = "candidate-00-baseline.json" if i == 0 else f"candidate-{i:02d}.json"
        out.append((name, a, {"SchemaVersion": 1, "Name": f"{ctx.id}: {a['id']}", "BaseProfile": ctx.profile,
                              "Plugins": a["plugins"], "Mods": a["mods"],
                              "Changes": [f"evalpipeline arm {a['id']}"],
                              "Provenance": {"arm": a["id"], "role": a["role"], "keys": a["keys"],
                                             "variant": a.get("variant"), "entry": ctx.id}}))
    return out


def profile_payload(ctx: Context, res: dict) -> dict[str, bytes]:
    combined = next(a for a in res["arms"] if a["id"] == "combined")
    universe = list(dict.fromkeys(m for a in res["arms"] for m in a["mods"]))
    modlist = "# evalpipeline base profile; strongest first\n" + \
        "".join("-" + m + "\n" for m in universe if m not in combined["mods"]) + \
        "".join("+" + m + "\n" for m in combined["mods"])
    files = {"modlist.txt": modlist.encode("utf-8"),
             "plugins.txt": "".join("*" + p + "\n" for p in combined["plugins"]).encode("utf-8"),
             "loadorder.txt": ("\n".join(combined["plugins"]) + "\n").encode("utf-8"),
             # the profile's own INIs are read only with LocalSettings on (a configured projection depends on it)
             "settings.ini": b"[General]\r\nLocalSettings=true\r\n"}
    if ctx.template:
        tdir = ctx.instance / "profiles" / ctx.template
        for f in sorted(tdir.glob("*.ini")):
            if f.name.lower() != "settings.ini":
                files[f.name] = f.read_bytes()
    return files


def write_profile(ctx: Context, files: dict[str, bytes]) -> Path:
    """Create the base profile, or accept an existing one with exactly these files; never overwrite another one."""
    d = ctx.instance / "profiles" / ctx.profile
    if d.exists():
        have = {f.name: f.read_bytes() for f in d.iterdir() if f.is_file()}
        if have != files:
            raise Refusal(f"lab profile {ctx.profile} exists with other contents; name another profile in the entry "
                          f"rather than overwrite it.")
        return d
    d.mkdir(parents=True)
    for name, data in files.items():
        with open(d / name, "xb") as f:
            f.write(data)
    return d


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


def scopeprobe_exe() -> Path:
    return HERE / "scopeprobe" / "bin" / "Release" / "net9.0" / "scopeprobe.exe"


def stage_census(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The subject's census on the source profile: cells and record types it touches, for the scope reconciliation."""
    out = sdir / "source-scope.json"
    if getattr(ctx, "in_place", None) and not res["subject"]:
        write_json(sdir / "census.json", {"source": None, "cells": {}, "recordTypes": {},
                                          "note": "the selection provides no plugin, so there is no subject to census"})
        print("census: the selection provides no plugin; skipped, and the plan runs without a scope")
        return {"subject": []}, [sdir / "census.json"]
    argv = [scopeprobe_exe(), "--instance", fwd(ctx.source_instance), "--profile", ctx.source_profile,
            "--game", fwd(ctx.source_game), "--assets", "1000000", "--json", fwd(out)]
    if getattr(ctx, "in_place", None) and any(a.get("switchedOn") for a in res["arms"]):
        # a selected mod MO2 has disabled is not in the profile as it stands: the census reads the order with it enabled
        name, _arm, doc = next(c for c in candidate_files(ctx, res) if c[1]["id"] == "combined")
        write_json(sdir / "census-order.json", doc)
        argv += ["--order", fwd(sdir / "census-order.json")]
    for p in res["subject"][:1]:
        argv += ["--target", p]
    r = child(ctx, sdir / "commands", "census-scope", argv)
    if r.get("exitCode") != 0 or not out.is_file():
        raise Refusal(f"the census scope did not complete (exit {r.get('exitCode')}); read {fwd(sdir / 'commands')}.")
    doc = read_json(out)
    if doc.get("complete") is not True or doc.get("walkFailures") or doc.get("unreadableRecords"):
        raise Refusal("the census scope is incomplete; it cannot give the subject's cells.")
    cells = {}
    for t in doc["targets"]:
        placed = t["footprint"]["placed"]
        cells[t["name"]] = sorted(c["cell"] for c in placed["cells"]
                                  if c.get("kind") != "persistent" and not str(c["cell"]).startswith("grid:"))
    write_json(sdir / "census.json", {"source": fwd(out), "sourceSha256": sha256_file(out), "cells": cells,
                                      "recordTypes": {t["name"]: t["footprint"]["recordTypes"] for t in doc["targets"]}})
    return {"subject": res["subject"]}, [sdir / "census.json", out]


def stage_prepare(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    cdir = sdir / "candidates"
    if cdir.exists():
        shutil.rmtree(cdir)
    cdir.mkdir(parents=True)
    rows = []
    for name, arm, cand in candidate_files(ctx, res):
        write_json(cdir / name, cand)
        rows.append({"file": name, "arm": arm["id"], "role": arm["role"], "variant": arm.get("variant"),
                     "keys": arm["keys"], "plugins": arm["plugins"], "mods": arm["mods"],
                     "sha256": sha256_file(cdir / name)})
    if getattr(ctx, "in_place", None):
        files, pdir = {}, ctx.instance / "profiles" / ctx.profile          # the chosen profile, read in place, never written
    else:
        files = profile_payload(ctx, res)
        pdir = write_profile(ctx, files)
    write_json(sdir / "preparation.json", {"profile": ctx.profile, "profileDir": fwd(pdir),
                                           "profileFiles": {n: hashlib.sha256(b).hexdigest() for n, b in files.items()},
                                           "baseline": res["baseline"], "arms": rows, "headers": res["headers"],
                                           "providers": res["providers"]})
    return {"headers": {p: h["sha256"] for p, h in res["headers"].items()}}, [cdir, sdir / "preparation.json"]


def scope_targets(ctx: Context, res: dict, arm: dict) -> list[str]:
    ev = set(arm["plugins"])
    if getattr(ctx, "in_place", None):
        # the selection's plugins this arm loads; an arm that loads none (the without-selection arm, or an asset-only
        # selection) scopes its last plugin only to prove the order loads: the plan's targets come from the combined
        # arm's scope, and an asset-only selection plans without one
        t = [p for p in res["subject"] if p in ev]
        return t or ([arm["plugins"][-1]] if arm["plugins"] else [])
    if res["subject"][0] in ev:
        t = [res["subject"][0]]
    else:
        heads = [p.get("plugin") for p in ctx.entry.get("partners", [])]
        t = [p for p in heads if p in ev]
        if not t:
            t = [ctx.entry.get("baselineScopeTarget") or res["baseline"][-1]]
    return t + [k for k in res["keys"] if k in ev]


def order_refusal(logs: Path) -> str | None:
    """The order reader's own refusal (MISSING MASTER, CANNOT ENABLE, PROFILE MISMATCH) from a probe's job logs, as one
    line, or None."""
    for log in sorted(Path(logs).glob("*.txt")) if Path(logs).is_dir() else []:
        try:
            lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError:
            continue
        for i, line in enumerate(lines):
            if any(k in line for k in ("MISSING MASTER", "CANNOT ENABLE", "PROFILE MISMATCH")):
                more = [x.strip() for x in lines[i + 1:i + 4] if x.strip().startswith(("-", "'")) or "master" in x.lower()]
                return " ".join([" ".join(line.split())] + more).rstrip(".") + "."
    return None


def stage_scope(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    prep = read_json(ctx.out / "prepare" / "preparation.json")
    census = read_json(ctx.out / "census" / "census.json")
    accepted, outs = {}, []
    for row in prep["arms"]:
        arm = next(a for a in res["arms"] if a["id"] == row["arm"])
        adir = sdir / row["file"].replace(".json", "")
        adir.mkdir(parents=True, exist_ok=True)
        out = adir / "scope.json"
        targets = scope_targets(ctx, res, arm)
        argv = [scopeprobe_exe(), "--order", fwd(ctx.out / "prepare" / "candidates" / row["file"]), "--json", fwd(out),
                "--assets", "1000000", "--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game)]
        for t in targets:
            argv += ["--target", t]
        if getattr(ctx, "in_place", None) and arm["id"] == "combined":
            # the scope line: who else touches the footprint or the loaded grid around it, and which disabled mods would
            argv += ["--ring", "2"]
            for path in disabled_plugin_files(ctx, res):
                argv += ["--also-scan", fwd(path)]
        r = child(ctx, adir / "commands", "scope-" + row["file"].replace(".json", ""), argv)
        if r.get("exitCode") != 0 or not out.is_file():
            why = order_refusal(adir / "commands")
            if why and getattr(ctx, "in_place", None) and arm["id"] == "without-selection":
                raise Refusal(f"the profile without the selected mods would not load: {why} Tick the mods that need the "
                              f"selection too, or pick another mod.")
            raise Refusal(f"scope of arm {arm['id']} did not complete (exit {r.get('exitCode')}); read {fwd(adir)}.")
        expect = {"plugins": [{"name": p, "path": prep["headers"][p]["path"], "sha256": prep["headers"][p]["sha256"]}
                              for p in arm["plugins"]],
                  "mods": arm["mods"], "targets": targets,
                  "censusCells": {res["subject"][0]: census["cells"].get(res["subject"][0], [])}
                  if res["subject"][0] in targets else {}}
        rec = accept_scope(read_json(out), expect)
        accepted[row["arm"]] = {"file": row["file"], "scope": fwd(out), "sha256": sha256_file(out), "targets": targets,
                                **rec}
        outs.append(out)
    write_json(sdir / "scopes.json", accepted)
    if getattr(ctx, "in_place", None) and "combined" in accepted:
        write_json(sdir / "scope-line.json", {"line": scope_line(read_json(Path(accepted["combined"]["scope"])), res,
                                                                 disabled_mod_of(ctx, res))})
        outs.append(sdir / "scope-line.json")
    return {"preparation": sha256_file(ctx.out / "prepare" / "preparation.json")}, outs + [sdir / "scopes.json"]


def disabled_mod_of(ctx: Context, res: dict) -> dict:
    """{plugin file name (lower): mod folder} for the plugins of the profile's disabled mod folders nobody ticked."""
    import evalrun as ER
    _enabled, disabled, _read, _path = ER._modlist(str(ctx.instance), ctx.profile)
    sel = {m.lower() for m in res.get("selection") or []}
    out = {}
    for m in disabled:
        if m.lower() in sel:
            continue
        folder = ctx.instance / "mods" / m
        if folder.is_dir():
            for f in sorted(folder.iterdir(), key=lambda x: x.name.lower()):
                if f.is_file() and f.suffix.lower() in (".esp", ".esm", ".esl"):
                    out.setdefault(f.name.lower(), m)
    return out


def disabled_plugin_files(ctx: Context, res: dict) -> list:
    """The plugin files of the profile's disabled, unticked mod folders (scopeprobe reads them for CELL records only)."""
    out = []
    for low, m in disabled_mod_of(ctx, res).items():
        out += [f for f in (ctx.instance / "mods" / m).iterdir() if f.is_file() and f.name.lower() == low]
    return out


def _base_game(name: str) -> bool:
    n = name.lower()
    return name in OFFICIAL_MASTERS or (n.startswith("cc") and n.endswith((".esm", ".esp", ".esl")))


def _names(items, most=6) -> str:
    items = list(items)
    return ", ".join(items[:most]) + (f" and {len(items) - most} more" if len(items) > most else "")


def scope_line(scope: dict, res: dict, disabled_mods: dict) -> str:
    """The tab's plain sentence on what the ticked mods touch, from the combined arm's scope.json: the cells their plugins
    edit (worldspace and named cells, interiors), the other enabled plugins with records in those cells or within two
    cells around them (the loaded grid), and the disabled, unticked mods whose plugins also edit them."""
    ticked = list(res.get("selection") or [])
    mine = {str(t.get("name")).lower() for t in scope.get("targets") or []}
    cells = [c for t in scope.get("targets") or [] for c in (((t.get("footprint") or {}).get("placed") or {}).get("cells") or [])]
    seen, ext, interiors = set(), {}, []
    for c in cells:
        if c.get("kind") == "persistent" or c.get("cell") in seen:
            continue
        seen.add(c.get("cell"))
        if c.get("kind") == "interior":
            interiors.append(c.get("name") or c.get("cell"))
        else:
            ext.setdefault(c.get("worldspace") or c.get("worldspaceKey") or "a worldspace", []).append(c.get("name"))
    who = _names(ticked, 3) if ticked else "the selection"
    parts = []
    for ws, names in ext.items():
        named = [n for n in names if n]
        parts.append(f"{len(names)} cell{'s' if len(names) != 1 else ''} in {ws}" + (f" ({_names(named, 3)})" if named else ""))
    if interiors:
        parts.append(f"{len(interiors)} interior{'s' if len(interiors) != 1 else ''} ({_names(interiors, 3)})")
    if not parts:
        return f"{who} places nothing in any cell, so no other plugin shares its cells."
    inside = {p["name"] for loc in ((scope.get("locationContributors") or {}).get("locations") or [])
              for p in loc.get("plugins") or []}
    near = {p["name"] for p in ((scope.get("nearby") or {}).get("plugins") or [])}
    others = sorted({n for n in inside | near if n.lower() not in mine and not _base_game(n)}, key=str.lower)
    ring = (scope.get("nearby") or {}).get("ring") or 0
    lc = scope.get("locationContributors") or {}
    if lc.get("complete") is False:
        # never "no other plugin" from a walk that did not finish: say what could not be read
        missing = lc.get("missing") or []
        return (f"{who} edits " + " and ".join(parts) + "; which other plugins touch those cells is not known: "
                + (f"{len(missing)} plugin(s) could not be read for it ({str(missing[0])[:160]})" if missing
                   else "the probe's footprint walk was incomplete") + ".")
    line = f"{who} edits " + " and ".join(parts) + "; "
    line += (f"{len(others)} other enabled plugin{'s touch' if len(others) != 1 else ' touches'} those cells"
             + (f" or the {ring} cells around them" if ring else "") + f": {_names(others, 6)}." if others
             else "no other enabled plugin besides the base game touches those cells"
             + (f" or the {ring} cells around them (persistent references placed into those cells are not read)" if ring else "") + ".")
    hint_mods = []
    for x in ((scope.get("nearby") or {}).get("alsoScanned") or []):
        if x.get("footprintCells") or x.get("ringCells"):
            m = disabled_mods.get(str(x.get("name")).lower())
            if m and m not in hint_mods:
                hint_mods.append(m)
    for m in hint_mods[:3]:
        line += f" {m} (disabled in MO2) also edits these cells; tick it to test them together."
    if len(hint_mods) > 3:
        line += f" {len(hint_mods) - 3} more disabled mods also edit them."
    unread = [x for x in ((scope.get("nearby") or {}).get("alsoScanned") or []) if x.get("unreadable")]
    if unread:
        line += (f" {len(unread)} disabled plugin{'s' if len(unread) != 1 else ''} could not be read for this "
                 f"({_names([str(x.get('name')) for x in unread], 3)}), so whether they edit these cells is not known.")
    return line



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


def evalrun(*args) -> list[str]:
    return [sys.executable, fwd(HERE / "evalrun.py"), *[str(a) for a in args]]


def stage_plan(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    scopes = read_json(ctx.out / "scope" / "scopes.json")
    run = ctx.out / "evaluation"
    if run.exists():
        raise Refusal(f"{fwd(run)} exists from an earlier plan; move it aside to replan, never plan over it.")
    ev = ctx.entry.get("evaluation", {})
    env = None
    if getattr(ctx, "in_place", None):
        select, depth = in_place_select(ctx)
        # an asset-only selection plans without a scope: a scope needs a target, and any target would be a plugin
        # outside the selection (receipts/h3-asset-only-plan.log: no scope gives targets [] and a population of the folders)
        scope_args = ["--scope", scopes["combined"]["scope"]] if res["subject"] else []
        argv = evalrun("plan", "--registry", fwd(REGISTRY), *scope_args,
                       "--candidates", fwd(ctx.out / "prepare" / "candidates"),
                       "--depth", depth, "--budget-minutes", ev.get("budgetMinutes", 90),
                       "--max-candidates", len(scopes), "--out", fwd(run), "--select", ",".join(select),
                       "--review-arm", "candidate-01",
                       "--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game))
        if ev.get("primitiveSolidity"):
            argv += ["--primitive-solidity", ev["primitiveSolidity"]]
        else:
            # never this machine's harness config: a manual run on another machine would get another table or none
            env = {"HOUSECARL_HARNESS_CONFIG": "", "HOUSECARL_PRIMITIVE_SOLIDITY": ""}
    else:
        argv = evalrun("plan", "--registry", fwd(REGISTRY), "--scope", scopes["combined"]["scope"],
                       "--candidates", fwd(ctx.out / "prepare" / "candidates"),
                       "--depth", ev.get("depth", "deep"), "--budget-minutes", ev.get("budgetMinutes", 90),
                       "--max-candidates", len(scopes), "--out", fwd(run),
                       "--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game))
    for o in ev.get("optIn", []):
        argv += ["--opt-in", o]
    r = child(ctx, sdir / "commands", "plan", argv, env=env)
    if r.get("exitCode") != 0 or not (run / "plan.json").is_file():
        raise Refusal(f"evalrun plan refused or failed (exit {r.get('exitCode')}); its sentence is in {fwd(sdir / 'commands')}.")
    plan = read_json(run / "plan.json")
    prep = read_json(ctx.out / "prepare" / "preparation.json")
    files = {row["file"] for row in prep["arms"]}
    arms = plan.get("arms") or []
    held = (plan.get("budgets") or {}).get("candidatesHeldBack") or {}
    if {Path(str(a.get("candidateFile"))).name for a in arms} != files or held.get("count"):
        raise Refusal("the frozen plan does not hold every prepared arm (some were held back or missing); "
                      "raise --max-candidates or fix the candidates.")
    reg = read_json(REGISTRY)
    if len(plan.get("checks", [])) != len(reg["checks"]):
        raise Refusal("the frozen plan does not carry the full check registry.")
    return {"scopes": sha256_file(ctx.out / "scope" / "scopes.json")}, [run / "plan.json"]


def ledger_counts(run: Path) -> dict:
    led = read_json(run / "ledger.json") if (run / "ledger.json").is_file() else {}
    from collections import Counter
    return dict(Counter((v.get("status") or "?").split("(")[0] for v in (led.get("entries") or {}).values()))


def stage_evaluate(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    run = ctx.out / "evaluation"
    history = []
    verb = "resume" if (run / "ledger.json").is_file() and read_json(run / "ledger.json").get("runIds") else "run"
    # evalrun stops itself at the plan's budget and releases the slot; a stop from outside leaves a stale lock that
    # keeps the machine busy for the next attempt, so the deadline and the slot's hold sit past the budget.
    backstop = int(ctx.entry.get("evaluation", {}).get("budgetMinutes", 90)) + 10
    for attempt in range(1, 6):
        before = ledger_counts(run)
        limit = budget_differs(ctx.entry, ctx.out)
        argv = evalrun(verb, "--run", fwd(run), *(["--budget-minutes", limit] if limit is not None else []))
        r = child(ctx, sdir / "commands", f"evaluate-{attempt:02d}", argv, heavy=True,
                  purpose=f"evalpipeline {ctx.id} evaluate", deadline_min=backstop, hold_min=backstop)
        after = ledger_counts(run)
        history.append({"attempt": attempt, "verb": verb, "exitCode": r.get("exitCode"), "stopped": r.get("stopped"),
                        "before": before, "after": after})
        verb = "resume"
        if r.get("exitCode") not in (0,) and not r.get("stopped"):
            break
        if after == before or not r.get("stopped"):
            break
    write_json(sdir / "evaluate.json", {"history": history, "ledgerCounts": ledger_counts(run)})
    last = history[-1]
    if last["exitCode"] != 0:
        raise Refusal(f"evalrun {last['verb']} ended with exit {last['exitCode']} (stopped: {last['stopped']}); "
                      f"read {fwd(sdir / 'commands')} and `evalrun status`.")
    # evalrun ends 0 when it stops itself at its budget: rows it stopped or never started keep the stage open, so the
    # next run resumes them instead of reusing a partial evaluation; the stages after it still run on what finished
    counts = ledger_counts(run)
    stopped, skipped = counts.get("stopped", 0), counts.get("skipped-budget", 0)
    if stopped or skipped:
        minutes = budget_differs(ctx.entry, ctx.out)
        minutes = minutes if minutes is not None else planned_budget(ctx.out)
        minutes = int(minutes) if isinstance(minutes, float) and minutes.is_integer() else minutes
        raise NonBlockingRefusal(f"evaluation stopped at its budget of {minutes} min with {stopped + skipped} rows open "
                                 f"({stopped} stopped, {skipped} not started); run again to resume them (each run gets a "
                                 f"fresh budget), or raise the entry's budgetMinutes.")
    # the ledger is not an output binding: imports and finalize change it in later stages
    return {"plan": sha256_file(run / "plan.json")}, [sdir / "evaluate.json"]


def reader_cmd(ctx) -> str:
    cmd = ctx.machine.get("readerCmd") or os.environ.get("HOUSECARL_RECORD_READER_CMD")
    if not cmd:
        raise Refusal("no record reader is named: set readerCmd in the machine file (a houseCARL server whose folder "
                      "carries its generated corpus.json).")
    return cmd


# one arm's record read: about twice the slowest arm measured (Ice Blade's Legacy-carrying baseline arm, 73.5 min)
RECORDS_ARM_MINUTES = 150


def slot_wait_sentence(log: Path, ctx) -> str | None:
    """The one sentence for a capture that never started because the machine slot's wait for idle ran out; None for any other end."""
    if not log.is_file():
        return None
    line = next((x for x in log.read_text(encoding="utf-8", errors="replace").splitlines() if "gave up waiting for idle" in x), None)
    if line is None:
        return None
    why = line.split(": ", 1)[-1].strip()
    prefix = ctx.machine.get("heavyPrefix") or []
    floor = prefix[prefix.index("--min-free-gb") + 1] if "--min-free-gb" in prefix else None
    if "memory" in why:
        return (f"the record capture never started: the machine slot's wait for memory ran out ({why}); it needs {floor} GB free, so close other programs "
                f"or lower --min-free-gb in the machine file's heavyPrefix, then run again.")
    return f"the record capture never started: the machine slot's wait for an idle machine ran out ({why}); end what it names, then run again."


def stage_records(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    run = ctx.out / "evaluation"
    if getattr(ctx, "in_place", None):
        write_json(sdir / "records.json", {"note": "a manual run offers no model-driven record check; mcp-only rows stay "
                                                   "open and the bundle says they were not assessed"})
        return {"openArms": 0}, [sdir / "records.json"]
    led = read_json(run / "ledger.json").get("entries") or {}
    open_arms = sorted({k.split("__")[1] for k, v in led.items() if str(v.get("status")) == "mcp-only" and "__" in k})
    if not open_arms:
        write_json(sdir / "records.json", {"note": "no mcp-only row is open; nothing to capture"})
        return {"openArms": 0}, [sdir / "records.json"]
    import evalrecords_workers
    try:
        record_workers = evalrecords_workers.parse_workers(ctx.machine.get("recordWorkers", "auto"), "the machine file's recordWorkers")
    except ValueError as ex:
        raise Refusal(str(ex))
    n = len(list(sdir.glob("capture-*")))
    cap = sdir / f"capture-{n + 1:02d}"
    argv = [sys.executable, fwd(HERE / "evalrecords.py"), "capture", "--run", fwd(run),
            *[x for a in open_arms for x in ("--arm", a)],
            "--baseline-from", fwd(ctx.out / "prepare" / "preparation.json"), "--reader-cmd", reader_cmd(ctx),
            "--data", fwd(ctx.game / "Data"), "--mods", fwd(ctx.instance / "mods"),
            "--profile-dir", fwd(ctx.instance / "profiles" / ctx.profile),
            "--reader-data", fwd(ctx.out / "reader-data"), "--out", fwd(cap)]
    # an arm's read is stopped at RECORDS_ARM_MINUTES (a hung reader); the stage's backstop scales with the open arms.
    # Finished arms are kept in the checkpoints folder, so a stop or a rerun reads only the arms not yet finished.
    (sdir / "checkpoints").mkdir(exist_ok=True)
    argv += ["--checkpoints", fwd(sdir / "checkpoints"), "--arm-deadline-min", str(RECORDS_ARM_MINUTES)]
    # how many arms read at once: the machine file's recordWorkers, a number or auto; workers run inside the one slot below
    argv += ["--workers", str(record_workers)]
    # the read takes the machine slot, held as long as the stage's own backstop so the slot never stops it first
    backstop = RECORDS_ARM_MINUTES * len(open_arms) + 10
    r = child(ctx, sdir / "commands", "records-capture", argv, heavy=True, purpose=f"evalpipeline {ctx.id} records",
              deadline_min=backstop, hold_min=backstop)
    summary = read_json(cap / "capture-summary.json") if (cap / "capture-summary.json").is_file() else {}
    workers = read_json(cap / "capture-workers.json") if (cap / "capture-workers.json").is_file() else {}
    if workers.get("warning"):
        print(f"records: {workers['warning']}")
    for arm, why in (workers.get("curedAlone") or {}).items():
        print(f"records: {arm} was refused beside other arms and read fine alone: {why}")
    imports, refusals = [], {}
    for arm, v in summary.items():
        if "refused" in v:
            refusals[arm] = v["refused"]
        imports += v.get("imports", [])
    results = []
    imports = [d for d in imports if str((led.get(Path(d).parent.name) or {}).get("status")) == "mcp-only"]
    for i, doc in enumerate(sorted(imports)):
        rr = child(ctx, sdir / "commands", f"import-{cap.name}-{i:02d}", evalrun("import-results", "--run", fwd(run), "--file", doc))
        log = sdir / "commands" / f"import-{cap.name}-{i:02d}.txt"
        results.append({"file": doc, "exitCode": rr.get("exitCode"),
                        "log": fwd(log) if log.is_file() else None})
    write_json(sdir / "records.json", {"capture": summary, "captureExit": r.get("exitCode"), "imports": results,
                                       "refusedArms": refusals,
                                       "workers": {k: workers.get(k) for k in ("requested", "workers", "case", "warning", "memory", "curedAlone")}})
    bad = [x for x in results if x["exitCode"] != 0]
    if r.get("exitCode") not in (0, 2) or not summary:
        raise Refusal(slot_wait_sentence(sdir / "commands" / "records-capture.txt", ctx)
                      or f"the record capture did not finish (exit {r.get('exitCode')}); read {fwd(sdir / 'commands')}.")
    stopped = sorted(a_ for a_, why in refusals.items() if "deadline" in str(why))
    if stopped:
        raise Refusal(f"the record read of arm(s) {', '.join(stopped)} reached its {RECORDS_ARM_MINUTES}-minute deadline; "
                      f"run again: finished arms are kept in {fwd(sdir / 'checkpoints')} and only these are read again.")
    if bad or refusals:
        # recorded, not hidden: the evaluation stays incomplete for those rows, and the bundle says so
        print(f"records: {len(bad)} import(s) refused and {len(refusals)} arm(s) refused; see {fwd(sdir / 'records.json')}")
    return {"imports": len(results), "refused": len(bad) + len(refusals)}, [sdir / "records.json", cap, sdir / "checkpoints"]


def stage_scenes(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """Overlap scene acquisition (overlapprobe --geometry-manifest per arm with its role, then overlap_attribution,
    overlap_triage and evalreport triage) is not built in this version. It feeds the incident catalogue and the
    reviewers' reconciliation, not the report or reel, so its absence is a stated, non-blocking refusal, never a pass."""
    raise NonBlockingRefusal("overlap scene acquisition is not built in this version (overlapprobe --geometry-manifest "
                             "per arm, attribution and triage); no incident catalogue was made. The report and reel "
                             "do not read it.")


def names_env(ctx) -> dict:
    """A private, empty instance for the names read (bundleenrich's own rule: every record is read by absolute path,
    never from an active copy of the same plugin)."""
    import bundleenrich
    return {k: fwd(v) for k, v in bundleenrich.private_instance(ctx.out / "names-instance").items()}


def stage_bundle(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    run = ctx.out / "evaluation"
    bundle = bundle_dir(ctx)
    if bundle.exists():
        raise Refusal(f"{fwd(bundle)} exists from an earlier bundle; move it aside rather than overwrite it.")
    steps = [("finalize", evalrun("finalize", "--run", fwd(run)), (0, 3)),
             ("bundle", evalrun("bundle", "--run", fwd(run), "--out", fwd(bundle), "--reader-cmd", reader_cmd(ctx),
                                *[x for k, v in names_env(ctx).items() for x in ("--reader-env", f"{k}={v}")],
                                *[x for p, n in (ctx.entry.get("modNames") or {}).items()
                                  for x in ("--mod-name", f"{p}={n}")]), (0,)),
             ("verify-bundle", evalrun("verify-bundle", fwd(bundle)), (0,))]
    done = []
    for name, argv, ok in steps:
        r = child(ctx, sdir / "commands", name, argv)
        done.append({"step": name, "exitCode": r.get("exitCode")})
        if r.get("exitCode") not in ok:
            write_json(sdir / "bundle.json", {"steps": done})
            raise Refusal(f"evalrun {name} ended with exit {r.get('exitCode')}; read {fwd(sdir / 'commands' / (name + '.txt'))}.")
    log = (sdir / "commands" / "bundle.txt").read_text(encoding="utf-8", errors="replace")         if (sdir / "commands" / "bundle.txt").is_file() else ""
    names = [l for l in log.splitlines() if l.startswith("names:")]
    if names and "refused" in names[-1]:
        raise Refusal("the bundle was written but its names enrichment " + names[-1][len("names:"):].strip()[:300])
    disp = read_json(run / "disposition.json")
    write_json(sdir / "bundle.json", {"steps": done, "bundle": fwd(bundle), "evaluation": disp.get("evaluation", {}).get("claim"),
                                      "complete": disp.get("evaluation", {}).get("complete"),
                                      "ledgerCounts": disp.get("ledgerCounts")})
    return {"disposition": sha256_file(run / "disposition.json")}, [sdir / "bundle.json", bundle / "manifest.json"]


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
    and hash at its top level (evalrun's PREMISE_COPIES: the check registry and the scope) whose original no longer
    hashes to the plan (gone, or changed since) is handed to evalrun bundle as the evidence bundle's own copy, which
    evalrun checks against the plan's hash and refuses if it differs. A pinned input this function does not know is
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
    """The captured frames go into a second evidence bundle (evalrun bundle --media, every item checked against the
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
        for name, argv in (("media-bundle", evalrun("bundle", "--run", fwd(run), "--out", fwd(bundle), "--media",
                                                    fwd(mdir / "media.json"), *frozen_input_args(ctx),
                                                    "--reader-cmd", reader_cmd(ctx),
                                                    *[x for k, v in names_env(ctx).items() for x in ("--reader-env", f"{k}={v}")],
                                                    *[x for p, n in (ctx.entry.get("modNames") or {}).items()
                                                      for x in ("--mod-name", f"{p}={n}")])),
                           ("media-verify", evalrun("verify-bundle", fwd(bundle)))):
            r = child(ctx, sdir / "commands", name, argv)
            if r.get("exitCode") != 0:
                raise Refusal(f"evalrun {name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.txt'))}.")
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


STAGE_BODIES = {"census": stage_census, "prepare": stage_prepare, "scope": stage_scope, "plan": stage_plan,
                "evaluate": stage_evaluate, "records": stage_records, "scenes": stage_scenes,
                "bundle": stage_bundle, "precedent": stage_precedent, "report-plan": stage_report_plan,
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

    out = {}
    for k, v in zip(bodies.keys, bodies.values):
        if isinstance(k, ast.Constant) and isinstance(v, ast.Name) and v.id in funcs:
            out[k.value] = sorted(closure(v.id, funcs[v.id]) & PRESENTATION_KEYS)
    return out


def stage_entry_hash(entry: dict, measured: dict, reads: list[str]) -> str:
    """A stage's entry binding: the entry without its presentation keys, plus the presentation keys the stage reads.
    A stage that reads none hashes exactly what the measuring stages always hashed."""
    return sha256_json({**measured, **{k: entry[k] for k in reads if k in entry}})


def legacy_entry_hash(entry: dict, measured: dict, stage: str) -> str:
    """The binding the 02d9ea09 driver used (the whole entry from the bundle on), kept only to re-key its receipts."""
    return sha256_json(entry if STAGES.index(stage) >= STAGES.index("bundle") else measured)


# ---------------------------------------------------------------- driver

def cmd_arms(a) -> int:
    ctx = Context(a.entry, a.machine, a.out or ".")
    res = ctx.resolve()
    print(f"baseline ({len(res['baseline'])}): {', '.join(res['baseline'])}")
    for arm in res["arms"]:
        ev = [p for p in arm["plugins"] if p not in res["baseline"]]
        print(f"{arm['id']:40s} {arm['role']:8s} {'; '.join(ev) or '(baseline only)'}")
        print(f"{'':40s} mods: {'; '.join(arm['mods'])}")
    return EXIT_OK


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
        res = ctx.resolve()
        write_json(out / "arms.json", [{k: v for k, v in arm.items()} for arm in res["arms"]])
        seeded, reused_from = {}, None
        if getattr(ctx, "in_place", None):
            import evalprepreuse as PR
            key = PR.key(ctx.instance, ctx.profile, ctx.game, res["selection"], res["arms"],
                         PR.tools_digest(HERE, scopeprobe_exe()), also_scanned=disabled_plugin_files(ctx, res))
            if getattr(a, "reuse_preparation_from", None):
                found, why = PR.find(a.reuse_preparation_from, out, key, res["arms"])
                if found is not None:
                    seeded, reused_from = PR.seed(found, out), found
                    print(f"preparation: reused from {found.parent.name} (same profile, selection, files and probe build)")
                else:
                    print(f"preparation: not reused: {why}")
            write_json(out / PR.KEY_FILE, key)
        reads = stage_entry_keys()
        # the receipts as they stood before this run: the earlier rule's upstream bindings (a re-key changes them)
        before = {s: (rec or {}).get("inputsSha256") for s, rec in ledger.doc["stages"].items()}
        ran_any = False
        run.stage("stages", "pipeline stages", STAGES.index(through) + 1, "stages")
        for name in STAGES[:STAGES.index(through) + 1]:
            body = STAGE_BODIES.get(name)
            if body is None:
                print(f"{name}: not built in this version; the run stops here.")
                write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "at": utcnow()})
                return EXIT_REFUSED
            sdir = out / name
            sdir.mkdir(exist_ok=True)
            # which stage is going, and since when, for a reader of the run record (the workbench's Start tab)
            run.note(f"stage {name} started {utcnow()}")
            run.liveness()
            # the entry, every earlier stage's binding and, after the bundle, the stage's own tools decide reuse; the
            # driver's own code is recorded, not bound, because the measurements' tool identities are bound inside
            # evalrun's plan
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
            # the same inputs as the 02d9ea09 driver hashed them, with the receipts as they stood before this run; only
            # while every earlier stage was reused, since a stage that ran this run changed what later stages bind
            legacy = None if ran_any else {"entry": legacy_entry_hash(ctx.entry, measured, name),
                                           "upstream": {s: before.get(s) for s in earlier}}
            if limit is not None and name == "evaluate":
                inputs["budgetInForce"] = limit
            tools = stage_tools(name, ctx.machine)
            if tools:
                inputs["tools"] = tools
            if name == "plan" and (out / "scope" / "scope-line.json").is_file():
                print("scope: " + str(read_json(out / "scope" / "scope-line.json").get("line") or ""))
            if name in seeded:
                # an earlier run's preparation, taken over whole: recorded under this run's inputs, its own record kept
                old = seeded[name]
                ledger.record(name, inputs, list(old.get("outputs") or {}), exit_code=0,
                              extra={"bound": old.get("bound"), "seconds": 0.0, "driverSha256": sha256_file(Path(__file__)),
                                     "reusedFrom": fwd(reused_from), "reusedSeconds": old.get("seconds")})
                print(f"{name}: reused from an earlier run")
                ran_any = True
                run.advance()
                continue
            if legacy is not None:
                legacy.update({k: inputs[k] for k in ("budgetInForce", "tools") if k in inputs})
            if ledger.decide(name, inputs, legacy) == "reuse":
                print(f"{name}: reused (inputs and outputs unchanged)")
                run.advance()
                continue
            ran_any = True
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
            ledger.record(name, inputs, outputs, exit_code=0,
                          extra={"bound": extra, "seconds": secs, "driverSha256": sha256_file(Path(__file__))})
            print(f"{name}: accepted in {secs} s")
            run.advance()
        if refusals:
            write_json(out / "status.json", {"status": "incomplete", "through": through, "sentence": refusals[0]["sentence"],
                                             "refusals": refusals, "at": utcnow()})
        else:
            write_json(out / "status.json", {"status": "through " + through, "at": utcnow()})
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
    p = sub.add_parser("run", help="run every stage not yet accepted, in order")
    p.add_argument("--entry", type=abs_path, required=True, help="the mod's selection entry (JSON)")
    p.add_argument("--out", type=abs_path, required=True, help="the run folder")
    p.add_argument("--machine", type=abs_path, help="the machine file: lab, game, heavy-stage prefix, run-local stage commands")
    p.add_argument("--through", choices=STAGES, help="stop after this stage")
    p.add_argument("--reuse-preparation-from", type=abs_path, metavar="WORK_FOLDER",
                   help="a manual run: take the census, setup and load-order checks over from an earlier run under this folder "
                        "when every input they were built from is the same (evalprepreuse.py); otherwise run them")
    p.set_defaults(fn=cmd_run)
    p = sub.add_parser("arms", help="print the arms the entry derives, before anything runs")
    p.add_argument("--entry", type=abs_path, required=True)
    p.add_argument("--machine", type=abs_path)
    p.add_argument("--out", type=abs_path)
    p.set_defaults(fn=cmd_arms)
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
