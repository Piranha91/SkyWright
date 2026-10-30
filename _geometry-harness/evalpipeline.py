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
  order     `order_arm`: the source profile's load order, ESM-flagged plugins first (the game's rule), keys absent from
            the source after everything; a late or missing master refuses by name. Asset providers follow the plugin
            order (a later plugin's folder wins), keys' folders strongest.
  stages    census, prepare, scope, plan, evaluate, records, scenes, bundle, precedent, report-plan, capture,
            image-observations, report, reel, deliver. Each stage is a receipt in RUN/stages.json bound to the hashes
            of its inputs and outputs; `run` reuses an accepted stage whose bindings are unchanged and reruns one that
            changed. A stage that exits 0 without its outputs is not accepted.

What it does not do. It measures nothing itself and decides nothing about a mod: every finding comes from the probes,
evalrun's ledger and the record reader. It never certifies absence on an empty read: an incomplete scope, an empty
enumeration where a touching plugin loads, a missing output or a stage refusal stops that stage with one sentence, and
the run stays `incomplete`. A precedent refusal does not block the report or reel. Where a judgement must be made by
a person or a model (blind image observations, the final reviews) the run stops with that status and continues when
the judgement file exists.

The machine file (machine-local, never tracked) names the lab instance, the game, a command prefix for heavy stages
(the machine's slot and idle queue) and the commands of run-local stages such as the precedent cards.
Exit codes: 0 the requested stages accepted or reused; 2 a refusal (one sentence on stderr); 3 the run stopped for a
judgement (status names it).
"""
from __future__ import annotations

import argparse
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
from runlog import Run, Runner  # noqa: E402

REGISTRY = ROOT / ".claude/skills/world-edit-compatibility/references/check-registry.json"
STAGES = ("census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle", "precedent",
          "report-plan", "capture", "image-observations", "report", "reel", "deliver")
NON_BLOCKING = frozenset({"precedent"})
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
            raise Refusal(f"{p} is not in the source profile's load order; add it there or name its place in the entry.")
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

    def decide(self, stage: str, inputs: dict) -> str:
        rec = self.doc["stages"].get(stage)
        if not rec or rec.get("inputsSha256") != sha256_json(inputs):
            return "run"
        for path, fp in rec["outputs"].items():
            if _fingerprint(Path(path)) != fp:
                return "run"
        return "reuse"

    def record(self, stage: str, inputs: dict, outputs, exit_code: int, extra: dict | None = None):
        outs = [Path(o) for o in outputs]
        entry = {"stage": stage, "at": utcnow(), "exitCode": exit_code, "inputsSha256": sha256_json(inputs)}
        missing = [fwd(o) for o in outs if not o.exists()]
        if exit_code != 0 or missing:
            entry["accepted"] = False
            entry["reason"] = (f"exit {exit_code}" if exit_code != 0 else "missing outputs: " + ", ".join(missing))
            self.doc["stages"].pop(stage, None)
            self.doc["history"].append(entry)
            self._save()
            if missing and exit_code == 0:
                raise Refusal(f"stage {stage} exited 0 but did not write {', '.join(missing)}; it is not accepted.")
            return entry
        entry.update(accepted=True, inputs=inputs, outputs={fwd(o): _fingerprint(o) for o in outs}, **(extra or {}))
        self.doc["stages"][stage] = entry
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
        self.entry = read_json(entry_path)
        self.machine = read_json(machine_path) if machine_path else {}
        self.out = Path(out)
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
        return [l.strip() for l in lo.read_text(encoding="utf-8-sig").splitlines() if l.strip() and not l.startswith("#")]

    def resolve(self) -> dict:
        """Headers, baseline and every arm's ordered plugins, providers and mods."""
        e = self.entry
        subj = list(e["subject"].get("plugins") or [e["subject"]["plugin"]])
        keys = [k["plugin"] for k in e.get("keys", [])]
        partner_plugins = [p for u in _units(e) for p in u]
        companions = {k.lower(): v for k, v in (e.get("companions") or {}).items()}
        headers, providers, todo = {}, {}, list(subj + partner_plugins + keys + list(e.get("baseline", [])))
        for c in companions.values():
            todo += c
        while todo:
            p = todo.pop(0)
            if p in headers:
                continue
            mod, f = self.provider_of(p)
            headers[p] = plugin_header(f)
            providers[p] = mod
            todo += headers[p]["masters"]
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
        source = self.source_order()
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

def candidate_files(ctx: Context, res: dict) -> list[dict]:
    """Candidate configurations: the combined arm is candidate-00-baseline.json (evalrun's baseline), the rest in order."""
    arms = sorted(res["arms"], key=lambda a: 0 if a["id"] == "combined" else 1)
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

def child(ctx, ledger_dir, op, argv, *, heavy=False, purpose=None, deadline_min=None, env=None):
    """One child through the shared Runner; a heavy child goes through the machine's slot command."""
    if heavy and ctx.machine.get("heavyPrefix"):
        lock = Path(ctx.machine.get("lockDir") or ctx.out) / ("HEAVY-LOCK-" + op)
        prefix = [x.replace("{purpose}", purpose or op).replace("{lock}", fwd(lock)) for x in ctx.machine["heavyPrefix"]]
        argv = prefix + ["--"] + argv
    job = {"id": op, "argv": [str(a) for a in argv], "cwd": str(ROOT)}
    if env:
        job["env"] = {**os.environ, **env}
    deadline = time.perf_counter() + 60 * deadline_min if deadline_min else None
    res = Runner("evalpipeline-" + op, out_dir=Path(ledger_dir), deadline=deadline).run([job], echo=False)
    return res[0]


def scopeprobe_exe() -> Path:
    return HERE / "scopeprobe" / "bin" / "Release" / "net9.0" / "scopeprobe.exe"


def stage_census(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The subject's census on the source profile: cells and record types it touches, for the scope reconciliation."""
    out = sdir / "source-scope.json"
    argv = [scopeprobe_exe(), "--instance", fwd(ctx.source_instance), "--profile", ctx.source_profile,
            "--game", fwd(ctx.source_game), "--assets", "1000000", "--json", fwd(out)]
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
    files = profile_payload(ctx, res)
    pdir = write_profile(ctx, files)
    write_json(sdir / "preparation.json", {"profile": ctx.profile, "profileDir": fwd(pdir),
                                           "profileFiles": {n: hashlib.sha256(b).hexdigest() for n, b in files.items()},
                                           "baseline": res["baseline"], "arms": rows, "headers": res["headers"],
                                           "providers": res["providers"]})
    return {"headers": {p: h["sha256"] for p, h in res["headers"].items()}}, [cdir, sdir / "preparation.json"]


def scope_targets(ctx: Context, res: dict, arm: dict) -> list[str]:
    ev = set(arm["plugins"])
    if res["subject"][0] in ev:
        t = [res["subject"][0]]
    else:
        heads = [p.get("plugin") for p in ctx.entry.get("partners", [])]
        t = [p for p in heads if p in ev]
        if not t:
            t = [ctx.entry.get("baselineScopeTarget") or res["baseline"][-1]]
    return t + [k for k in res["keys"] if k in ev]


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
        r = child(ctx, adir / "commands", "scope-" + row["file"].replace(".json", ""), argv)
        if r.get("exitCode") != 0 or not out.is_file():
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
    return {"preparation": sha256_file(ctx.out / "prepare" / "preparation.json")}, outs + [sdir / "scopes.json"]


def evalrun(*args) -> list[str]:
    return [sys.executable, fwd(HERE / "evalrun.py"), *[str(a) for a in args]]


def stage_plan(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    scopes = read_json(ctx.out / "scope" / "scopes.json")
    run = ctx.out / "evaluation"
    if run.exists():
        raise Refusal(f"{fwd(run)} exists from an earlier plan; move it aside to replan, never plan over it.")
    ev = ctx.entry.get("evaluation", {})
    argv = evalrun("plan", "--registry", fwd(REGISTRY), "--scope", scopes["combined"]["scope"],
                   "--candidates", fwd(ctx.out / "prepare" / "candidates"),
                   "--depth", ev.get("depth", "deep"), "--budget-minutes", ev.get("budgetMinutes", 90),
                   "--max-candidates", len(scopes), "--out", fwd(run),
                   "--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game))
    for o in ev.get("optIn", []):
        argv += ["--opt-in", o]
    r = child(ctx, sdir / "commands", "plan", argv)
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
    for attempt in range(1, 6):
        before = ledger_counts(run)
        r = child(ctx, sdir / "commands", f"evaluate-{attempt:02d}", evalrun(verb, "--run", fwd(run)), heavy=True,
                  purpose=f"evalpipeline {ctx.id} evaluate", deadline_min=110)
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
    return {"plan": sha256_file(run / "plan.json")}, [sdir / "evaluate.json", run / "ledger.json"]


def comparison_cells(res: dict, scopes: dict) -> list[str]:
    """The record capture's comparison cells H: every non-persistent cell a scope target places in, over all arms,
    except the baseline's own target."""
    evaluated = set(res["subject"]) | {p for u in res["partners"] for p in u} | set(res["keys"])
    cells = set()
    for arm in scopes.values():
        for t, x in arm["targets"].items():
            if t in evaluated:
                cells |= set(x["measuredCells"])
    return sorted(cells)


def reader_cmd(ctx) -> str:
    cmd = ctx.machine.get("readerCmd") or os.environ.get("HOUSECARL_RECORD_READER_CMD")
    if not cmd:
        raise Refusal("no record reader is named: set readerCmd in the machine file (a houseCARL server whose folder "
                      "carries its generated corpus.json).")
    return cmd


def stage_records(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    scopes = read_json(ctx.out / "scope" / "scopes.json")
    cells = comparison_cells(res, scopes)
    write_json(sdir / "cells.json", cells)
    run = ctx.out / "evaluation"
    cap = sdir / "capture"
    argv = [sys.executable, fwd(HERE / "evalrecords.py"), "capture", "--run", fwd(run),
            "--baseline-from", fwd(ctx.out / "prepare" / "preparation.json"), "--reader-cmd", reader_cmd(ctx),
            "--data", fwd(ctx.game / "Data"), "--mods", fwd(ctx.instance / "mods"),
            "--profile-dir", fwd(ctx.instance / "profiles" / ctx.profile), "--cells", fwd(sdir / "cells.json"),
            "--reader-data", fwd(ctx.out / "reader-data"), "--out", fwd(cap)]
    r = child(ctx, sdir / "commands", "records-capture", argv, deadline_min=240)
    summary = read_json(cap / "capture-summary.json") if (cap / "capture-summary.json").is_file() else {}
    imports, refusals = [], {}
    for arm, v in summary.items():
        if "refused" in v:
            refusals[arm] = v["refused"]
        imports += v.get("imports", [])
    results = []
    for i, doc in enumerate(sorted(imports)):
        rr = child(ctx, sdir / "commands", f"import-{i:02d}", evalrun("import-results", "--run", fwd(run), "--file", doc))
        log = sdir / "commands" / f"import-{i:02d}.log"
        results.append({"file": doc, "exitCode": rr.get("exitCode"),
                        "log": fwd(log) if log.is_file() else None})
    write_json(sdir / "records.json", {"capture": summary, "captureExit": r.get("exitCode"), "imports": results,
                                       "refusedArms": refusals, "cells": len(cells)})
    bad = [x for x in results if x["exitCode"] != 0]
    if r.get("exitCode") not in (0, 2) or not summary:
        raise Refusal(f"the record capture did not finish (exit {r.get('exitCode')}); read {fwd(sdir / 'commands')}.")
    if bad or refusals:
        # recorded, not hidden: the evaluation stays incomplete for those rows, and the bundle says so
        print(f"records: {len(bad)} import(s) refused and {len(refusals)} arm(s) refused; see {fwd(sdir / 'records.json')}")
    return {"imports": len(results), "refused": len(bad) + len(refusals)}, [sdir / "records.json", cap]


def stage_scenes(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """Overlap scene acquisition feeds the incident catalogue and the reviewers' reconciliation, not the report or reel
    (evalreport plans from the bundle and the capture results). Not built in this version; recorded as such."""
    write_json(sdir / "scenes.json", {"status": "not-built", "sentence": "Overlap scene acquisition is not part of this "
                                      "version's pipeline; the report and reel do not read it."})
    return {"status": "not-built"}, [sdir / "scenes.json"]


def stage_bundle(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    run = ctx.out / "evaluation"
    bundle = ctx.out / "bundle"
    if bundle.exists():
        raise Refusal(f"{fwd(bundle)} exists from an earlier bundle; move it aside rather than overwrite it.")
    steps = [("finalize", evalrun("finalize", "--run", fwd(run)), (0, 3)),
             ("bundle", evalrun("bundle", "--run", fwd(run), "--out", fwd(bundle), "--reader-cmd", reader_cmd(ctx),
                                "--reader-env", "HOUSECARL_DATA_DIR=" + fwd(ctx.out / "reader-data"),
                                *[x for p, n in (ctx.entry.get("modNames") or {}).items()
                                  for x in ("--mod-name", f"{p}={n}")]), (0,)),
             ("verify-bundle", evalrun("verify-bundle", fwd(bundle)), (0,))]
    done = []
    for name, argv, ok in steps:
        r = child(ctx, sdir / "commands", name, argv)
        done.append({"step": name, "exitCode": r.get("exitCode")})
        if r.get("exitCode") not in ok:
            write_json(sdir / "bundle.json", {"steps": done})
            raise Refusal(f"evalrun {name} ended with exit {r.get('exitCode')}; read {fwd(sdir / 'commands' / (name + '.log'))}.")
    disp = read_json(run / "disposition.json")
    write_json(sdir / "bundle.json", {"steps": done, "bundle": fwd(bundle), "evaluation": disp.get("evaluation", {}).get("claim"),
                                      "complete": disp.get("evaluation", {}).get("complete"),
                                      "ledgerCounts": disp.get("ledgerCounts")})
    return {"disposition": sha256_file(run / "disposition.json")}, [sdir / "bundle.json", bundle / "manifest.json"]


def bundle_dir(ctx) -> Path:
    return ctx.out / "bundle"


def stage_precedent(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The run-local precedent cards (machine file `precedentCmd`, with {bundle} and {out}); a refusal does not block."""
    tmpl = ctx.machine.get("precedentCmd")
    if not tmpl:
        raise Refusal("no precedent stage is named in the machine file (precedentCmd); the cards were not written.")
    out = sdir / "cards"
    argv = [x.replace("{bundle}", fwd(bundle_dir(ctx))).replace("{out}", fwd(out)) for x in tmpl]
    r = child(ctx, sdir / "commands", "precedent", argv)
    log = sdir / "commands" / "precedent.log"
    if r.get("exitCode") != 0:
        tail = log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] if log.is_file() else []
        raise Refusal(f"the precedent stage refused (exit {r.get('exitCode')}): {' '.join(tail)[:300]}")
    return {"bundle": sha256_file(bundle_dir(ctx) / "manifest.json")}, [out]


def report_plan_argv(ctx, res, out: Path, captures=()) -> list:
    run = ctx.out / "evaluation"
    argv = [sys.executable, fwd(HERE / "evalreport.py"), "plan", "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(out),
            "--verify-copy", "--mode", ctx.entry.get("report", {}).get("mode", "concise")]
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
    if ctx.entry.get("patchSearch"):
        argv += ["--patch-search", ctx.entry["patchSearch"]]
    for c in captures:
        argv += ["--capture-results", fwd(c)]
    return argv


def stage_report_plan(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    out = sdir / "plan"
    if out.exists():
        shutil.rmtree(out)
    r = child(ctx, sdir / "commands", "report-plan", report_plan_argv(ctx, res, out))
    if r.get("exitCode") != 0 or not (out / "presentation.json").is_file():
        raise Refusal(f"evalreport plan refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / 'report-plan.log')}.")
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


def stage_capture(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """Each supported capture request: a verified camera (capture_acquire acquire --points auto) and its frames
    (capture_acquire capture) in the with- and without-setups the request names. A refused request is kept, not hidden."""
    sb = ctx.out / "report-plan" / "storyboard.json"
    plan = read_json(ctx.out / "evaluation" / "plan.json")
    cands = {a["armId"]: fwd(ctx.out / "prepare" / "candidates" / a["candidateFile"]) for a in plan["arms"]}
    lab = ["--instance", fwd(ctx.instance), "--profile", ctx.profile, "--game", fwd(ctx.game)]
    vp = ctx.machine.get("viewprobe") or fwd(HERE / "viewprobe" / "bin" / "Release" / "net9.0" / "viewprobe.exe")
    rows = []
    for q in (read_json(sb).get("capturePlan") or {}).get("requests") or []:
        qdir = sdir / q["id"]
        row = {"id": q["id"], "arm": q.get("armId"), "without": (q.get("vignette") or {}).get("withoutArmId")}
        if not ((q.get("camera") or {}).get("support") or {}).get("supported"):
            row["refused"] = "the request says its camera is not supported by capture_acquire"
            rows.append(row)
            continue
        if row["arm"] not in cands or row["without"] not in cands:
            row["refused"] = "the request names no planned with- or without-arm"
            rows.append(row)
            continue
        acq = qdir / "acquisition.json"
        a = child(ctx, qdir / "commands", "acquire", [
            sys.executable, fwd(HERE / "capture_acquire.py"), "acquire", "--packet", fwd(sb), "--request", q["id"],
            "--points", "auto", "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(acq), "--receipts",
            fwd(qdir / "camera-receipts"), "--vp", vp, "--", *lab, "--order", cands[row["arm"]]],
            heavy=True, purpose=f"evalpipeline {ctx.id} capture", deadline_min=60)
        row["acquireExit"] = a.get("exitCode")
        if a.get("exitCode") != 0 or not acq.is_file():
            row["refused"] = f"the camera was not acquired (exit {a.get('exitCode')}); see {fwd(qdir / 'commands')}"
            rows.append(row)
            continue
        c = child(ctx, qdir / "commands", "capture", [
            sys.executable, fwd(HERE / "capture_acquire.py"), "capture", "--packet", fwd(sb), "--request", q["id"],
            "--acquisition", fwd(acq), "--with-order", cands[row["arm"]], "--without-order", cands[row["without"]],
            "--out", fwd(qdir / "sequence"), "--size", str(ctx.entry.get("captureSize", 1280)), "--vp", vp, "--", *lab],
            heavy=True, purpose=f"evalpipeline {ctx.id} capture", deadline_min=60)
        row["captureExit"] = c.get("exitCode")
        result = qdir / "sequence" / "capture-result.json"
        if c.get("exitCode") != 0 or not result.is_file():
            row["refused"] = f"the frames were not rendered (exit {c.get('exitCode')}); see {fwd(qdir / 'commands')}"
        else:
            row["result"] = fwd(result)
        rows.append(row)
    write_json(sdir / "captures.json", rows)
    return {"storyboard": sha256_file(sb)}, [sdir / "captures.json"]


def stage_image_observations(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """A neutral, indexed copy of every captured frame for the blind image review (no names, arms or expectations)."""
    caps = read_json(ctx.out / "capture" / "captures.json")
    packet = sdir / "indexed-images"
    if packet.exists():
        shutil.rmtree(packet)
    packet.mkdir(parents=True)
    key, n = [], 0
    for row in caps:
        if not row.get("result"):
            continue
        for png in sorted(Path(row["result"]).parent.rglob("*.png")):
            n += 1
            shutil.copy2(png, packet / f"image-{n:03d}.png")
            key.append({"image": f"image-{n:03d}.png", "source": fwd(png), "sha256": sha256_file(png), "request": row["id"]})
    write_json(sdir / "image-key.json", key)  # the key stays out of the packet a blind reviewer is given
    (packet / "README.txt").write_text("Indexed frames for a blind review. Describe what each image shows; no names or "
                                       "expectations are supplied.\n", encoding="utf-8")
    return {"images": n}, [packet, sdir / "image-key.json"]


def stage_report(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    caps = [Path(r["result"]) for r in read_json(ctx.out / "capture" / "captures.json") if r.get("result")]
    out = sdir / "report"
    if out.exists():
        raise Refusal(f"{fwd(out)} exists from an earlier generation; reports keep every attempt, so move it aside first.")
    steps = [("plan", report_plan_argv(ctx, res, out, caps)),
             ("report", [sys.executable, fwd(HERE / "evalreport.py"), "report", "--presentation",
                         fwd(out / "presentation.json"), "--out", fwd(out)]),
             ("verify", [sys.executable, fwd(HERE / "evalreport.py"), "verify", "--out", fwd(out)])]
    for name, argv in steps:
        r = child(ctx, sdir / "commands", name, argv)
        if r.get("exitCode") != 0:
            raise Refusal(f"evalreport {name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.log'))}.")
    outs = [out / "presentation.json"] + [p for p in (out / "report.md", out / "report.html", out / "report.pdf") if p.is_file()]
    return {"captures": len(caps)}, outs


def stage_reel(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    m = ctx.machine
    pres = ctx.out / "report" / "report" / "presentation.json"
    sb = sdir / "storyboard.json"
    reel = sdir / "reel"
    if reel.exists():
        raise Refusal(f"{fwd(reel)} exists from an earlier render; move it aside first.")
    steps = [("storyboard", [sys.executable, fwd(HERE / "evalreel.py"), "storyboard-packet", "--presentation", fwd(pres),
                             "--bundle", fwd(bundle_dir(ctx)), "--out", fwd(sb)])]
    board = sb
    if m.get("speech"):
        sp = m["speech"]
        steps.append(("speech", [sp["python"], fwd(HERE / "evalspeech.py"), "--storyboard", fwd(sb), *sp.get("args", []),
                                 "--out", fwd(sdir / "speech")]))
        board = sdir / "speech" / "storyboard-with-audio.json"
    render = [sys.executable, fwd(HERE / "evalreel.py"), "render", "--presentation", fwd(pres), "--out", fwd(reel),
              "--bundle", fwd(bundle_dir(ctx)), "--storyboard", fwd(board)]
    if m.get("ffmpeg"):
        render += ["--ffmpeg", m["ffmpeg"]]
    steps.append(("render", render))
    for name, argv in steps:
        r = child(ctx, sdir / "commands", name, argv, deadline_min=90)
        if r.get("exitCode") != 0:
            raise Refusal(f"reel step {name} refused (exit {r.get('exitCode')}); read {fwd(sdir / 'commands' / (name + '.log'))}.")
    status = read_json(reel / "reel-status.json") if (reel / "reel-status.json").is_file() else {}
    if status.get("status") != "rendered":
        raise Refusal(f"the reel status is {status.get('status')!r}, not rendered; read {fwd(reel / 'reel-status.json')}.")
    # decode the frames the reel map names, for the reel review
    dec = sdir / "decoded"
    if m.get("ffmpeg") and (reel / "reel-map.json").is_file():
        dec.mkdir(exist_ok=True)
        rmap = read_json(reel / "reel-map.json")
        times = []
        for seg in rmap.get("segments") or rmap.get("scenes") or []:
            for k in ("start", "startSeconds"):
                if isinstance(seg.get(k), (int, float)):
                    times.append(float(seg[k]) + 0.5)
                    break
        for i, t in enumerate(times[:60]):
            child(ctx, sdir / "commands", f"decode-{i:02d}", [m["ffmpeg"], "-hide_banner", "-nostdin", "-loglevel", "error",
                                                              "-ss", f"{t:.2f}", "-i", fwd(reel / "reel.mp4"),
                                                              "-frames:v", "1", fwd(dec / f"frame-{i:02d}.png")])
    return {"presentation": sha256_file(pres)}, [reel / "reel.mp4", reel / "reel-status.json"]


def stage_deliver(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:
    """The review packet paths for the reviewer's clean-context reviews; the run then waits for their verdicts."""
    rep = ctx.out / "report" / "report"
    paths = {"report": [fwd(p) for p in (rep / "report.pdf", rep / "report.html", rep / "report.md") if p.is_file()],
             "evidenceIndex": fwd(rep / "evidence-index.md") if (rep / "evidence-index.md").is_file() else None,
             "reel": fwd(ctx.out / "reel" / "reel" / "reel.mp4"),
             "reelMap": fwd(ctx.out / "reel" / "reel" / "reel-map.json"),
             "decodedFrames": fwd(ctx.out / "reel" / "decoded"),
             "blindImages": fwd(ctx.out / "image-observations" / "indexed-images"),
             "bundle": fwd(bundle_dir(ctx)), "precedentCards": fwd(ctx.out / "precedent" / "cards")}
    write_json(sdir / "review-packet.json", paths)
    print(json.dumps(paths, indent=1))
    return {}, [sdir / "review-packet.json"]


STAGE_BODIES = {"census": stage_census, "prepare": stage_prepare, "scope": stage_scope, "plan": stage_plan,
                "evaluate": stage_evaluate, "records": stage_records, "scenes": stage_scenes,
                "bundle": stage_bundle, "precedent": stage_precedent, "report-plan": stage_report_plan,
                "capture": stage_capture, "image-observations": stage_image_observations, "report": stage_report,
                "reel": stage_reel, "deliver": stage_deliver}


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
    ledger = StageLedger(out)
    through = a.through or STAGES[-1]
    with Run("evalpipeline", mode=ctx.id, stage_source="counted") as run:
        res = ctx.resolve()
        write_json(out / "arms.json", [{k: v for k, v in arm.items()} for arm in res["arms"]])
        run.stage("stages", "pipeline stages", STAGES.index(through) + 1, "stages")
        for name in STAGES[:STAGES.index(through) + 1]:
            body = STAGE_BODIES.get(name)
            if body is None:
                print(f"{name}: not built in this version; the run stops here.")
                write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "at": utcnow()})
                return EXIT_REFUSED
            sdir = out / name
            sdir.mkdir(exist_ok=True)
            # the entry and every earlier stage's binding decide reuse; the driver's own code is recorded, not bound,
            # because the measurements' tool identities are bound inside evalrun's plan
            inputs = {"entry": sha256_file(ctx.entry_path),
                      "upstream": {s: (ledger.doc["stages"].get(s) or {}).get("inputsSha256")
                                   for s in STAGES[:STAGES.index(name)]}}
            if ledger.decide(name, inputs) == "reuse":
                print(f"{name}: reused (inputs and outputs unchanged)")
                run.advance()
                continue
            t0 = time.perf_counter()
            try:
                extra, outputs = body(ctx, res, sdir)
            except Refusal as r:
                ledger.record(name, inputs, [], exit_code=2, extra=None)
                outcome = stage_outcome(name, 2, False, str(r))
                write_json(out / "status.json", {"status": "incomplete", "stoppedAt": name, "sentence": str(r),
                                                 "at": utcnow()})
                if outcome["blocksLater"]:
                    print(f"{name}: REFUSED: {r}", file=sys.stderr)
                    return EXIT_REFUSED
                print(f"{name}: refused, not blocking: {r}")
                run.advance()
                continue
            secs = round(time.perf_counter() - t0, 1)
            ledger.record(name, inputs, outputs, exit_code=0,
                          extra={"bound": extra, "seconds": secs, "driverSha256": sha256_file(Path(__file__))})
            print(f"{name}: accepted in {secs} s")
            run.advance()
        write_json(out / "status.json", {"status": "through " + through, "at": utcnow()})
    return EXIT_OK


def cmd_status(a) -> int:
    led = StageLedger(a.out)
    for s in STAGES:
        rec = led.doc["stages"].get(s)
        print(f"{s:20s} {'accepted ' + rec['at'] + ' (' + str(rec.get('seconds')) + ' s)' if rec else '-'}")
    st = Path(a.out) / "status.json"
    if st.is_file():
        print(json.dumps(read_json(st)))
    return EXIT_OK


def build_parser():
    ap = argparse.ArgumentParser(prog="evalpipeline", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("run", help="run every stage not yet accepted, in order")
    p.add_argument("--entry", required=True, help="the mod's selection entry (JSON)")
    p.add_argument("--out", required=True, help="the run folder")
    p.add_argument("--machine", help="the machine file: lab, game, heavy-stage prefix, run-local stage commands")
    p.add_argument("--through", choices=STAGES, help="stop after this stage")
    p.set_defaults(fn=cmd_run)
    p = sub.add_parser("arms", help="print the arms the entry derives, before anything runs")
    p.add_argument("--entry", required=True)
    p.add_argument("--machine")
    p.add_argument("--out")
    p.set_defaults(fn=cmd_arms)
    p = sub.add_parser("status", help="print the accepted stages")
    p.add_argument("--out", required=True)
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
