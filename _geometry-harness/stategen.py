"""stategen — the world states a Deep evaluation judges, generated from one arm's switch census.

    python stategen.py --census CENSUS.json --out STATES.json [--specs STATES.txt]

Input: switchprobe's `--json` census for ONE arm (evalrun's `state.switch-census` output). Output: every switch
alone (named by FormKey), every quest-stage joint flip of two or more switches (`stage:<quest>:<n>`), and a
not-explored row, with its reason, for everything the census names that cannot be expressed as a state. Nothing the
census names is dropped without a row. evalrun imports `generate` for a Deep plan; the CLI prints the same list.

Rules:
  - a switch alone, by its FormKey (an editor id can repeat, and the name must be the same on every arm); not explored
    when its census row has no key, its enable chain loops, or its chain is unresolved;
  - a quest stage that flips two or more switches, as stateprobe's `stage:<quest>:<n>` (resolved through the same
    arm's `--switch-map`); not explored when a member label names no switch or several, or a member is not explorable;
    a stage that flips one switch is the same state as that switch alone and is recorded as covered by it;
  - census-level rows for what is not a switch or not readable (initially disabled references no parent names,
    player-parented references, links to unplaced parents, unreadable plugins, a census without the trigger map),
    with the census's counts, and always one row for the combinations no census enumerates.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

SCHEMA_VERSION = 1

REASONS = {
    "no-key": "The census row names no FormKey for this switch, so no state can name it.",
    "chain-cycle": "The switch's enable-parent chain loops, so its flipped state is undefined.",
    "chain-unresolved": "The switch's enable-parent chain is unresolved, so the opposite of its load-time state is unknown.",
    "label-unknown": "A switch this quest stage flips is named by no switch in this arm's census.",
    "label-ambiguous": "A switch this quest stage flips is named by several switches in this arm's census.",
    "member-not-explorable": "A switch this quest stage flips cannot be explored alone (see its own row).",
    "disabled-singletons": "Initially disabled references that no enable parent names are not switches; nothing flips them as a set.",
    "player-parent": "References whose enable parent is the player change with the player, not with a switch.",
    "unplaced-parent": "References linked to an enable parent that is not placed in this order cannot be flipped.",
    "unreadable-plugins": "Plugins the census could not read: their switches are absent from this list, not zero.",
    "no-trigger-map": "The census ran without the trigger map, so which switches quest stages flip together is unknown.",
    "combinations": ("Combinations of switches other than one quest stage's joint flip (stages in sequence, a scene or "
                     "event flipping several, unrelated switches together) are not enumerated; their extent is unknown, not empty."),
}


def _sha_self() -> str:
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def generate(census: dict, *, census_path: str | None = None, census_sha256: str | None = None) -> dict:
    """The state document for one arm's census: {schemaVersion, census, generator, states, covered, notExplored}."""
    switches = list(census.get("switches") or [])
    labels: dict[str, list[dict]] = {}
    for s in switches:
        if s.get("key"):
            labels.setdefault(str(s.get("edid") or s["key"]), []).append(s)
            if s.get("edid"):
                labels.setdefault(str(s["key"]), []).append(s)
    for k in list(labels):
        seen, uniq = set(), []
        for s in labels[k]:
            if s["key"] not in seen:
                seen.add(s["key"])
                uniq.append(s)
        labels[k] = uniq

    def why_not(s: dict) -> str | None:
        if not s.get("key"):
            return "no-key"
        if s.get("cycle"):
            return "chain-cycle"
        if s.get("baselineResolved") is False:
            return "chain-unresolved"
        return None

    singles, not_explored = [], []
    explorable = set()
    for s in sorted(switches, key=lambda r: (str(r.get("key") or ""), str(r.get("edid") or ""))):
        code = why_not(s)
        subject = s.get("key") or s.get("edid") or "(unnamed switch)"
        if code:
            not_explored.append({"subject": subject, "kind": "single", "code": code, "reason": REASONS[code],
                                 "count": 1})
            continue
        explorable.add(s["key"])
        singles.append({"spec": s["key"], "kind": "single", "label": s.get("edid") or s["key"],
                        "trigger": (s.get("trigger") or {}).get("status") or "Unknown"})

    joints, covered = [], []
    for j in sorted(census.get("jointFlips") or [], key=lambda r: str(r.get("stage"))):
        stage = str(j.get("stage"))
        members = list(j.get("switches") or [])
        resolved, code = [], None
        for lab in members:
            hits = labels.get(str(lab), [])
            if not hits:
                code = "label-unknown"
                break
            if len(hits) > 1:
                code = "label-ambiguous"
                break
            resolved.append(hits[0]["key"])
        if code is None:
            keys = list(dict.fromkeys(resolved))
            if any(k not in explorable for k in keys):
                code = "member-not-explorable"
            elif len(keys) == 1:
                covered.append({"stage": stage, "by": keys[0]})
                continue
            else:
                joints.append({"spec": f"stage:{stage}", "kind": "joint", "switches": keys})
                continue
        not_explored.append({"subject": stage, "kind": "joint", "code": code, "reason": REASONS[code], "count": 1,
                             "switches": members})

    summary = census.get("summary") or {}
    unreadable = list(census.get("unreadable") or [])
    for code, n, extra in (("disabled-singletons", summary.get("disabledSingletons"), None),
                           ("player-parent", summary.get("playerParentRefs"), None),
                           ("unplaced-parent", summary.get("unplacedParentRefs"), None),
                           ("unreadable-plugins", len(unreadable), {"plugins": unreadable})):
        if n:
            row = {"subject": code, "kind": "class", "code": code, "reason": REASONS[code], "count": int(n)}
            row.update(extra or {})
            not_explored.append(row)
    if "triggers" in summary and summary.get("triggers") is None:
        not_explored.append({"subject": "no-trigger-map", "kind": "class", "code": "no-trigger-map",
                             "reason": REASONS["no-trigger-map"], "count": 1})
    not_explored.append({"subject": "combinations", "kind": "class", "code": "combinations",
                         "reason": REASONS["combinations"], "count": None})

    return {"schemaVersion": SCHEMA_VERSION,
            "census": {"path": census_path, "sha256": census_sha256, "switches": len(switches),
                       "jointFlips": len(census.get("jointFlips") or [])},
            "generator": {"name": "stategen", "sha256": _sha_self()},
            "states": singles + joints, "covered": covered, "notExplored": not_explored}


def dumps(doc: dict) -> str:
    """The document's canonical text (the same census gives the same bytes)."""
    return json.dumps(doc, indent=1, ensure_ascii=False, sort_keys=False) + "\n"


def specs_text(doc: dict) -> str:
    """One stateprobe `--state` spec per line, in the document's order."""
    return "".join(s["spec"] + "\n" for s in doc.get("states") or [])


def generate_file(census_path: Path) -> dict:
    data = census_path.read_bytes()
    return generate(json.loads(data.decode("utf-8")), census_path=str(census_path),
                    census_sha256=hashlib.sha256(data).hexdigest())


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="stategen", description=__doc__.split("\n\n")[0])
    ap.add_argument("--census", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--specs")
    a = ap.parse_args(argv)
    with Run("stategen") as run:
        src = Path(a.census)
        try:
            doc = generate_file(src)
        except (OSError, ValueError) as ex:
            msg = f"The census {src} could not be read ({ex}): pass switchprobe's --json output for one arm."
            print(msg, file=sys.stderr)
            run.refuse(msg, exit_code=2)
            return 2
        Path(a.out).write_text(dumps(doc), encoding="utf-8")
        if a.specs:
            Path(a.specs).write_text(specs_text(doc), encoding="utf-8")
        n_s = sum(1 for s in doc["states"] if s["kind"] == "single")
        n_j = len(doc["states"]) - n_s
        print(f"{len(doc['states'])} states ({n_s} switches alone, {n_j} quest-stage joint flips); "
              f"{len(doc['covered'])} stages covered by a single switch; {len(doc['notExplored'])} not-explored rows")
        for r in doc["notExplored"]:
            n = "extent unknown" if r["count"] is None else f"count {r['count']}"
            print(f"   not explored: {r['subject']} ({r['code']}, {n}): {r['reason']}")
        run.complete()
        return 0


if __name__ == "__main__":
    sys.exit(main())
