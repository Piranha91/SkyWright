"""flipplan — draft the flip plan of the runtime state-switch check (`runtime.state-switch-flip`) from the census.

    python flipplan.py --arm ID PROFILE CENSUS.json [--arm ...] --scope SCOPE.json --scripts DIR
                       --out FLIPPLAN.json --groups GROUPS.json
                       [--neutral-cell CELL] [--evalrun-plan RUN/plan.json] [--canary-command CMD]

It drafts; the evaluator reviews. It launches nothing, and evalrun never runs it: the plan is the evaluator's statement
of what is flipped and where, and `switchflip.py validate` must accept it before any launch.

Inputs: per arm its switch census (switchprobe --json) and the MO2 profile the driver launches; the scope (targets
and their placed cells); a folder of decompiled scripts, one `<Script>.psc` per bound script, as
`switchprobe --show-script <Script>` prints it.

Rules:
  - the population is switchflip_scope's: switches a script drives that touch a target by plugin or by cell;
  - a group is one script function's calls on one branch path, in source order: an If/ElseIf/Else chain gives one
    path per arm (an If without Else also a path that skips it), so calls in different branches are never sent
    together. A path is planned when one of its calls flips a switch in the population; its other mapped calls
    stay in it, because they are part of the same sequence;
  - a call maps to switches through the census `trigger.by` text (`Script.Function: call text`); a call that maps to
    no switch is listed under `unmapped`, never dropped silently;
  - the calls are switchprobe's (_shared/ScriptFlipCalls.cs): a flip is Enable, Disable, their NoWait forms and the
    TryToEnable/TryToDisable forms; a bare call is on Self (`Self.Enable()`); a whole-line comment is no code;
    EnableLinkChain/DisableLinkChain change the object's linked chain, not the object, so they are listed under
    `unmapped` with that reason and never planned;
  - an enabling call (Enable, EnableNoWait, TryToEnable, TryToEnableNoWait) sets `enabled`, a disabling one
    `disabled`; a call that would not change its switch from the state it has
    at that point of the path (its load-time state, then each earlier call) is dropped and named;
  - flagged, not dropped: an enable-parented switch (its own Enable() may be inert; the driver's readback settles
    it), a list element (its order inside the loop is not read), a load-time state the census could not resolve;
  - a branch path whose every call is dropped makes no group; its drops are kept under `skippedPaths` (present
    only when there is one), and a switch never planned whose every recorded call was found and dropped is not
    planned as "every recorded call is a no-op from its load-time state";
  - not planned, with the reason: a bound-but-unread switch, a script whose source was not supplied, a call the
    supplied source does not contain, a switch credited only from the raw bytecode of a function the decompiler
    refused (its branch structure is not read);
  - two branch paths that send the same calls are one group (`samePaths` names the others);
  - conditions per group and arm: `loaded` at the `coc`-reachable cell (an interior or a named exterior cell, not a
    worldspace's persistent cell) holding most of the flipped switches' members, of the switches' own kind: a path
    with any exterior switch arrives only at an exterior cell (an interior does not load the exterior), and is
    omitted with the reason when its members name none; a path of interior switches arrives at an interior; an
    interior switch on an exterior path is flagged; `away` at the neutral cell when one is given (otherwise omitted
    and named); settle 0, window 60 s, two replicates. The arrival is a cell, not a position: the exterior cells
    loaded are the grid around the player's cell, and the driver records where the player arrived.
The groups document beside the plan says what was planned, dropped, flagged, unmapped or not planned, and why.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import re
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
sys.path.insert(0, str(HERE))
from runlog import Run  # noqa: E402
import switchflip_scope  # noqa: E402

SCHEMA_VERSION = 1
CHECK_ID = "runtime.state-switch-flip"
WINDOW_SECONDS, REPLICATES, MAX_PATHS = 60, 2, 32

# switchprobe's call rules (_shared/ScriptFlipCalls.cs), so a call's text here is the text the census recorded;
# test_flipplan 310-311 pin the names and directions to the C# definition
ENABLES = frozenset({"enable", "enablenowait", "trytoenable", "trytoenablenowait"})
DISABLES = frozenset({"disable", "disablenowait", "trytodisable", "trytodisablenowait"})
FLIP = ENABLES | DISABLES
LINK_CHAIN = frozenset({"enablelinkchain", "disablelinkchain"})
NAMES = ("TryToEnableNoWait|TryToDisableNoWait|TryToEnable|TryToDisable|EnableLinkChain|DisableLinkChain|EnableNoWait|"
         "DisableNoWait|Enable|Disable")
HEADER = re.compile(r"^\s*(?:[\w\[\]:]+\s+)?(?:Function|Event)\s+([\w:]+)\s*\(", re.I)
END = re.compile(r"^\s*End(?:Function|Event)\b", re.I)
CALL = re.compile(r"(?<![\w.])(?:Self\.)?(?P<obj>[A-Za-z_]\w*)(?:\[[^\]]*\])?(?:\.(?:GetReference|GetRef)\(\))?\."
                  r"(?P<call>" + NAMES + r")\s*\(", re.I)
LINKED = re.compile(r"(?<![\w.])(?:Self\.)?GetLinkedRef\([^)]*\)\.(?P<call>" + NAMES + r")\s*\(", re.I)
SELF_CALL = re.compile(r"(?<![\w.])(?:Self\.)?(?P<call>" + NAMES + r")\s*\(", re.I)
IF = re.compile(r"^\s*If\b", re.I)
ELSEIF = re.compile(r"^\s*ElseIf\b", re.I)
ELSE = re.compile(r"^\s*Else\s*$", re.I)
ENDIF = re.compile(r"^\s*EndIf\b", re.I)
BY = re.compile(r"\((?P<script>[\w:]+)\.(?P<fn>[\w:]+): (?P<text>.+?)(?:; via alias [^)]*)?\)$")
CELL_LABEL = re.compile(r"^(.+) \((?:int|ext)\)$")
FORMKEY = re.compile(r"^[0-9A-Fa-f]{6}:")
STAGE = re.compile(r"^quest (?P<quest>\S+) stage (?P<stage>\S+) \(")


# ---------------------------------------------------------------------------------------------- source --

def function_lines(source: str, fn: str) -> list[str] | None:
    """The code lines of one function or event (comments stripped as switchprobe strips them), or None."""
    out, inside = [], False
    for raw in source.split("\n"):
        line = raw.rstrip("\r")
        h = HEADER.match(line)
        if h:
            inside = h.group(1).lower() == fn.lower()
            continue
        if END.match(line):
            if inside:
                return out
            continue
        if inside:
            semi = line.find(";")
            out.append(line[:semi] if semi >= 0 else line)   # code only: a whole-line comment is no code
    return out if inside else None


def parse_block(lines: list[str], i: int = 0, stop=None) -> tuple[list, int]:
    """A sequence of items: ('call', text, name, line) or ('if', [arm sequences], has_else). Returns (items, next index)."""
    items = []
    while i < len(lines):
        line = lines[i]
        if stop and stop(line):
            return items, i
        if IF.match(line) and not ELSEIF.match(line):
            arms, has_else = [], False
            i += 1
            while True:
                body, i = parse_block(lines, i, lambda s: bool(ELSEIF.match(s) or ELSE.match(s) or ENDIF.match(s)))
                arms.append(body)
                if i >= len(lines) or ENDIF.match(lines[i]):
                    i += 1
                    break
                if ELSE.match(lines[i]):
                    has_else = True
                i += 1
            items.append(("if", arms, has_else))
            continue
        found = [("call", m.group(0).strip(), m.group("call"), i) for rx in (CALL, LINKED) for m in rx.finditer(line)]
        if not found and (m := SELF_CALL.search(line)):   # a bare call is on Self, recorded as switchprobe records it
            found = [("call", f"Self.{m.group('call')}()", m.group("call"), i)]
        items += found
        i += 1
    return items, i


def paths(items: list) -> list[list[tuple]]:
    """Every branch path through a sequence, each a list of ('call', text, name, line) in source order."""
    acc = [[]]
    for it in items:
        if it[0] == "call":
            acc = [p + [it] for p in acc]
            continue
        _, arms, has_else = it
        options = [q for arm in arms for q in paths(arm)]
        if not has_else:
            options.append([])
        acc = [p + q for p in acc for q in options]
        if len(acc) > MAX_PATHS:
            raise ValueError(f"more than {MAX_PATHS} branch paths")
    return acc


def find_script(scripts_dir: Path, script: str) -> Path | None:
    want = {script.lower() + ".psc", script.replace(":", "_").lower() + ".psc"}
    for p in sorted(scripts_dir.glob("*.psc")):
        if p.name.lower() in want:
            return p
    return None


# ---------------------------------------------------------------------------------------------- groups --

def arm_groups(census: dict, scope: dict, scripts_dir: Path) -> dict:
    """One arm's groups, unmapped calls and not-planned switches."""
    pop = switchflip_scope.population(census, scope)
    switches = {s["key"]: s for s in census.get("switches") or [] if isinstance(s, dict) and s.get("key")}
    in_pop = {p["key"] for p in pop["population"]}
    by_text, stages = {}, {}
    for key, s in switches.items():
        for line in (s.get("trigger") or {}).get("by") or []:
            m = BY.search(str(line))
            if not m:
                continue
            fk = (m.group("script").lower(), m.group("fn").lower())
            by_text.setdefault(fk + (m.group("text"),), set()).add(key)
            st = STAGE.match(str(line))
            if st:
                stages[fk] = f"{st.group('quest')}:{st.group('stage')}"
    functions = sorted({(k[0], k[1]) for k, keys in by_text.items() if keys & in_pop})
    names = {}
    for key in in_pop:
        for line in (switches[key].get("trigger") or {}).get("by") or []:
            m = BY.search(str(line))
            if m:
                names[(m.group("script").lower(), m.group("fn").lower())] = (m.group("script"), m.group("fn"))

    groups, unmapped, not_planned, planned_keys = [], [], [], set()
    skipped, found, dropped_keys = [], set(), set()   # all-dropped paths; census calls met in source; refs dropped
    for p in pop["population"]:
        if p["trigger"] == "BoundUnread":
            not_planned.append({"ref": p["key"], "reason": "bound, but the call was not read, so its order and target are unknown"})
    for fk in functions:
        script, fn = names.get(fk, fk)
        path = find_script(scripts_dir, script)
        src = path.read_text(encoding="utf-8", errors="replace") if path else None
        body = function_lines(src, fn) if src is not None else None
        keys_here = sorted(set().union(*(v for k, v in by_text.items() if k[:2] == fk)) & in_pop)
        if body is None:
            why = (f"the source of {script} was not supplied (switchprobe --show-script {script})" if src is None
                   else f"{script} has no function or event {fn}")
            not_planned += [{"ref": k, "reason": why} for k in keys_here]
            continue
        try:
            route = paths(parse_block(body)[0])
        except ValueError as ex:
            not_planned += [{"ref": k, "reason": f"{script}.{fn}: {ex}"} for k in keys_here]
            continue
        seen_unmapped = set()
        for n, pth in enumerate(route, start=1):
            calls, dropped, flags, state = [], [], [], {}
            for _, text, name, line in pth:
                if name.lower() in LINK_CHAIN:
                    if (text, line) not in seen_unmapped:
                        seen_unmapped.add((text, line))
                        unmapped.append({"source": f"{script}.{fn}", "line": line + 1, "call": text,
                                         "reason": "a link-chain call changes the references linked to its object, not "
                                                   "the object: not a flip"})
                    continue
                keys = sorted(by_text.get(fk + (text,), set()))
                if not keys:
                    if (text, line) not in seen_unmapped:
                        seen_unmapped.add((text, line))
                        unmapped.append({"source": f"{script}.{fn}", "line": line + 1, "call": text,
                                         "reason": "the census maps this call to no switch"})
                    continue
                found.add(fk + (text,))
                target = "enabled" if name.lower() in ENABLES else "disabled"
                for key in keys:
                    s = switches[key]
                    src_txt = f"{script}.{fn}, line {line + 1}: {text}" + (f" (branch path {n})" if len(route) > 1 else "")
                    now = state.get(key, s.get("baselineEnabled") if s.get("baselineResolved", True) is not False else None)
                    if now is not None and (now is True) == (target == "enabled"):
                        dropped.append({"ref": key, "target": target, "source": src_txt,
                                        "reason": f"already {target} at this point (load-time state, then earlier calls)"})
                        dropped_keys.add(key)
                        continue
                    state[key] = target == "enabled"
                    calls.append({"ref": key, "target": target, "source": src_txt})
                    if s.get("parent"):
                        flags.append({"ref": key, "flag": "enable-parented: its own Enable()/Disable() may be inert; the "
                                                          "driver's readback settles it"})
                    if "[" in text:
                        flags.append({"ref": key, "flag": "a list element: its order inside the loop is not read; the "
                                                          "census key order is used"})
                    if s.get("baselineResolved") is False:
                        flags.append({"ref": key, "flag": "the load-time state is unresolved, so no call is dropped as a no-op"})
            if not any(c["ref"] in in_pop for c in calls):
                if any(d["ref"] in in_pop for d in dropped):   # every call here is a no-op: no group, the drops kept
                    same = next((x for x in skipped if x["script"] == script and x["function"] == fn and
                                 [(d["ref"], d["target"]) for d in x["dropped"]] == [(d["ref"], d["target"]) for d in dropped]), None)
                    if same is not None:
                        same["samePaths"].append(n)
                    else:
                        skipped.append({"script": script, "function": fn, "path": n, "samePaths": [], "paths": len(route),
                                        "dropped": dropped})
                continue
            same = next((g for g in groups if g["script"] == script and g["function"] == fn
                         and [(c["ref"], c["target"]) for c in g["calls"]] == [(c["ref"], c["target"]) for c in calls]), None)
            if same is not None:
                same["samePaths"].append(n)   # another branch path sends the same calls: one group, both paths named
                continue
            planned_keys.update(c["ref"] for c in calls)
            arrival = arrival_choice([switches[c["ref"]] for c in calls])
            flags += [{"ref": k, "flag": "an interior switch on an exterior path: the exterior arrival does not load "
                                         "its interior members"} for k in arrival.pop("interiorSwitches")]
            groups.append({"id": f"{script}.{fn}" + (f"#{n}" if len(route) > 1 else ""), "script": script,
                           "function": fn, "stage": stages.get(fk), "path": n, "samePaths": [], "paths": len(route),
                           "calls": calls, "dropped": dropped, "flags": flags,
                           "arrival": arrival.pop("cell"), "arrivalNote": arrival})
    for k in sorted(in_pop):
        if k not in planned_keys and not any(x["ref"] == k for x in not_planned):
            raw, other, unfound = set(), False, False
            for line in (switches[k].get("trigger") or {}).get("by") or []:
                m = BY.search(str(line))
                if m and m.group("text").startswith("raw bytecode "):
                    raw.add(f"{m.group('script')}.{m.group('fn')}")
                else:
                    other = True
                    unfound |= not m or (m.group("script").lower(), m.group("fn").lower(), m.group("text")) not in found
            not_planned += [{"ref": k, "reason": f"credited from the raw bytecode of {f}, a function the decompiler "
                                                 "refused: its branch structure is not read, so it is not planned"}
                            for f in sorted(raw)]
            if other and not unfound and k in dropped_keys:
                not_planned.append({"ref": k, "reason": "every recorded call is a no-op from its load-time state"})
            elif other or not raw:
                not_planned.append({"ref": k, "reason": "no supplied source line carries its recorded call"})
    out = {"population": pop["counts"], "causes": pop["causes"], "groups": groups, "unmapped": unmapped,
           "notPlanned": sorted({(x["ref"], x["reason"]): x for x in not_planned}.values(),
                                key=lambda x: (x["ref"], x["reason"]))}
    if skipped:   # only when there is one, so a document without an all-dropped path keeps its bytes
        out["skippedPaths"] = skipped
    return out


def coc_name(label) -> str | None:
    """The `coc` name of a census cell label (`EDID (int)` / `EDID (ext)`); None for a persistent (worldspace) cell or a
    cell with no editor id, which `coc` cannot reach."""
    m = CELL_LABEL.match(str(label or ""))
    return m.group(1) if m and not FORMKEY.match(m.group(1)) else None


def is_interior(label) -> bool | None:
    """A census cell label's kind from its `(int)` / `(ext)` suffix; None when it has neither."""
    text = str(label or "")
    return True if text.endswith(" (int)") else False if text.endswith(" (ext)") else None


def switch_interior(s: dict) -> bool | None:
    """Where a switch itself stands: the census `interior` field, else its cell label's suffix."""
    return s["interior"] if isinstance(s.get("interior"), bool) else is_interior(s.get("cell"))


def arrival_choice(sw: list[dict]) -> dict:
    """{cell, why, members, of, interiorSwitches}: the cell `coc` can reach, of the switches' own kind, that holds
    most of the flipped switches' members (the switch's own cell when it has none). A path with any exterior switch
    arrives only at an exterior cell, since an interior does not load the exterior; `why` says why there is no cell."""
    exterior = any(switch_interior(s) is False for s in sw)
    tally, total = Counter(), 0
    for s in sw:
        members = [m for side in ("onSet", "offSet") for m in s.get(side) or [] if isinstance(m, dict)]
        labels = [m.get("cell") for m in members] or [s.get("cell")]
        total += len(labels)
        tally.update(coc_name(c) for c in labels if coc_name(c) and (not exterior or is_interior(c) is False))
    inner = [s["key"] for s in sw if exterior and switch_interior(s) is True]
    if not tally:
        why = ("an exterior switch path whose members name no exterior cell coc can reach" if exterior
               else "no member names a cell")
        return {"cell": None, "why": why, "members": 0, "of": total, "interiorSwitches": inner}
    best = max(tally.values())
    cell = sorted(c for c, n in tally.items() if n == best)[0]
    return {"cell": cell, "why": None, "members": best, "of": total, "interiorSwitches": inner}


def arrival_cell(sw: list[dict]) -> str | None:
    """The arrival cell of `arrival_choice`, or None."""
    return arrival_choice(sw)["cell"]


# ------------------------------------------------------------------------------------------------ plan --

def build(arms: list[tuple[str, str, dict]], scope: dict, scripts_dir: Path, *, neutral_cell: str | None = None,
          binding: dict | None = None, canary_command: str | None = None) -> tuple[dict, dict]:
    """(flip plan, groups document)."""
    plan_arms, conditions, doc_arms = [], [], []
    for arm_id, profile, census in arms:
        plan_arms.append({"id": arm_id, "profile": profile, **({"evalArmId": arm_id} if binding else {})})
        g = arm_groups(census, scope, scripts_dir)
        omitted = []
        first = True
        for k, grp in enumerate(g["groups"], start=1):
            base = f"{arm_id}-g{k:02d}"
            if grp["arrival"] is None:
                omitted.append({"group": grp["id"], "condition": "loaded",
                                "reason": (grp.get("arrivalNote") or {}).get("why") or "no member names a cell"})
            else:
                conditions.append({"id": f"{base}-loaded", "arm": arm_id, "arrival": {"cell": grp["arrival"]},
                                   "settleSeconds": 0, "windowSeconds": WINDOW_SECONDS, "replicates": REPLICATES,
                                   **({"canaryAfter": True} if canary_command and first else {}),
                                   "calls": grp["calls"]})
                first = False
            if neutral_cell:
                conditions.append({"id": f"{base}-away", "arm": arm_id, "arrival": {"cell": neutral_cell},
                                   "settleSeconds": 0, "windowSeconds": WINDOW_SECONDS, "replicates": REPLICATES,
                                   "calls": grp["calls"]})
            else:
                omitted.append({"group": grp["id"], "condition": "away",
                                "reason": "no neutral cell was given (--neutral-cell), so the not-loaded condition is omitted"})
            grp["conditions"] = [c["id"] for c in conditions if c["id"].startswith(base + "-")]
        doc_arms.append({"armId": arm_id, "profile": profile, **g, "omittedConditions": omitted})
    plan = {"schemaVersion": 1, "binding": binding, "arms": plan_arms, "conditions": conditions}
    if canary_command:
        plan["canary"] = {"consoleCommand": canary_command, "label": "detector check: a known crash, not a mod finding"}
    doc = {"schemaVersion": SCHEMA_VERSION, "generator": {"name": "flipplan", "sha256": _sha_self()},
           "neutralCell": neutral_cell, "arms": doc_arms}
    return plan, doc


def _sha_self() -> str:
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def dumps(doc: dict) -> str:
    return json.dumps(doc, indent=1, ensure_ascii=False) + "\n"


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="flipplan", description=__doc__.split("\n\n")[0])
    ap.add_argument("--arm", nargs=3, action="append", required=True, metavar=("ID", "PROFILE", "CENSUS"))
    ap.add_argument("--scope", required=True)
    ap.add_argument("--scripts", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--groups", required=True)
    ap.add_argument("--neutral-cell")
    ap.add_argument("--evalrun-plan")
    ap.add_argument("--canary-command")
    a = ap.parse_args(argv)
    with Run("flipplan") as run:
        def refuse(msg: str) -> int:
            print(msg, file=sys.stderr)
            run.refuse(msg, exit_code=2)
            return 2
        try:
            scope = json.loads(Path(a.scope).read_text(encoding="utf-8"))
            arms = [(i, p, json.loads(Path(c).read_text(encoding="utf-8"))) for i, p, c in a.arm]
        except (OSError, ValueError) as ex:
            return refuse(f"An input could not be read ({ex}): pass each arm's switchprobe --json census and the scope.")
        if not Path(a.scripts).is_dir():
            return refuse(f"--scripts {a.scripts} is not a folder: pass a folder of switchprobe --show-script output, one <Script>.psc each.")
        binding = None
        if a.evalrun_plan:
            try:
                ep = json.loads(Path(a.evalrun_plan).read_text(encoding="utf-8"))
            except (OSError, ValueError) as ex:
                return refuse(f"The evalrun plan {a.evalrun_plan} could not be read ({ex}).")
            known = {x.get("armId") for x in ep.get("arms") or []}
            unknown = [i for i, _, _ in arms if i not in known]
            if unknown:
                return refuse(f"--arm {', '.join(unknown)} is not an arm of evalrun plan {ep.get('planId')}: use the plan's arm ids.")
            binding = {"planId": ep.get("planId"), "checkId": CHECK_ID}
        plan, doc = build(arms, scope, Path(a.scripts), neutral_cell=a.neutral_cell, binding=binding,
                          canary_command=a.canary_command)
        Path(a.out).write_text(dumps(plan), encoding="utf-8")
        Path(a.groups).write_text(dumps(doc), encoding="utf-8")
        for arm in doc["arms"]:
            print(f"arm {arm['armId']}: {len(arm['groups'])} group(s), {len(arm['unmapped'])} unmapped call(s), "
                  f"{len(arm['notPlanned'])} switch(es) not planned, {len(arm['omittedConditions'])} condition(s) omitted; "
                  f"population {arm['population']['population']} of {arm['population']['scriptDriven']} script-driven")
            for c in arm["causes"]:
                print(f"   open cause: {c['code']}: {c['detail']}")
        if not plan["conditions"]:
            print("no condition was planned: nothing to flip on any arm (see the groups document)")
        print("a draft: review it, then `switchflip.py validate --plan` before any launch")
        run.complete()
        return 0


if __name__ == "__main__":
    sys.exit(main())
