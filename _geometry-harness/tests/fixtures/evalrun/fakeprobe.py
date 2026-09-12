"""The fixture probe engine — a fake detector that writes the REAL conflict-file format.

One engine, one thin wrapper per probe name under `fakeprobes/`. It accepts the flag shape the real
probes accept (`--order FILE --emit FILE [--base FILE] [--json FILE] [--out DIR] [--instance --profile
--game] [extra]`) and takes its behaviour from a scenario JSON whose path is in `EVALRUN_FAKE_SCENARIO`:

    { "pluginDir": "...",                      # default: ../plugins beside the scenario file
      "population": "load-time", "state": "baseline",
      "arms": { "<probe>": { "<emit stem>": { "behaviour": "ok", "rows": [...], "coverage": [...],
                                              "predicates": {"<FormKey>": "Gone"}, ... } } } }

The emit stem is the `--emit` filename without `.json` — `baseline`, `candidate-01-reorder`, and for
ordersweep's reverse arm `candidate-01-reorder.reverse`. `"*"` is the fallback entry for a probe.

What is REAL here and not faked:
  - the file shape is ConflictReport.cs's (`Conflicts[{Kind, A, B, Summary, Necessary[...]}]`, `Coverage`,
    `Predicates`, `BaseEmit`, `Population`, `State`, `Configuration`), so the real conflictdiff.exe reads it;
  - the Configuration fingerprint is ConfigurationIdentity.Compute's canonical text over the fixture
    plugins' actual sha256s, so two arms that consumed different plugin content get different fingerprints
    by the same rule the C# uses.

Behaviours (PLAN.md's fixture list): ok, exit, refuse, illegal, stale-output, no-emit, wrong-probe-name,
no-configuration, wrong-state, extraction-failed, crash-after.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from pathlib import Path

SCHEMA = 1
DEFAULT_UNKNOWN = {
    "installerChoices": "not modelled: MO2 keeps no per-mod record of the FOMOD options taken; the installed files are the evidence",
    "gameBuild": "not read: the executable version and SKSE/plugin set are not part of a records run",
    "distributors": "not read: SPID / KID / SkyPatcher layers are not scanned; only BOS ini files are listed as overlays",
    "saveState": "not applicable: a records run judges a fresh load, never a save",
}


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fingerprint(plugins, providers, overlays, population, state) -> str:
    """ConfigurationIdentity.Compute's canonical text, line for line: schema, population, state, one
    `plugin:<lowercase name>:<sha256>` per plugin IN ORDER, one `provider:<kind>:<lowercase name>` per
    provider, one `overlay:<kind>:<sha256>` per overlay; every line newline-terminated; sha256 hex lower."""
    sb = [f"schema:{SCHEMA}\n", f"population:{population}\n", f"state:{state}\n"]
    for p in plugins:
        sb.append(f"plugin:{p['Name'].lower()}:{p['Sha256']}\n")
    for v in providers:
        sb.append(f"provider:{v['Kind']}:{v['Name'].lower()}\n")
    for o in overlays:
        sb.append(f"overlay:{o['Kind']}:{o['Sha256']}\n")
    return hashlib.sha256("".join(sb).encode("utf-8")).hexdigest()


def configuration(candidate: dict, candidate_path: Path, plugin_dir: Path, population: str, state: str, limits) -> dict:
    plugins = []
    for name in candidate.get("Plugins") or candidate.get("plugins") or []:
        path = plugin_dir / name
        ext = path.suffix.lower()
        plugins.append({
            "Name": name, "Path": str(path).replace("\\", "/"),
            "Sha256": sha256_file(path) if path.exists() else "0" * 64,
            "Bytes": path.stat().st_size if path.exists() else 0,
            "Provider": "fixture-mods", "MasterFlag": ext in (".esm", ".esl"),
            "LightFlag": ext == ".esl", "Masters": [],
        })
    providers = [{"Name": "overwrite", "Kind": "overwrite"},
                 {"Name": "fixture-mods", "Kind": "mod"},
                 {"Name": "Data", "Kind": "data"}]
    overlays = []
    return {
        "SchemaVersion": SCHEMA,
        "Fingerprint": fingerprint(plugins, providers, overlays, population, state),
        "Source": f"candidate:{candidate_path.name}",
        "Plugins": plugins, "Providers": providers, "Overlays": overlays,
        "Population": population, "State": state,
        "Limits": list(limits or []), "Unknown": dict(DEFAULT_UNKNOWN),
    }


def row_identity(kind: str, form_key: str, other, a: str) -> str:
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    other = other.lower() if other else None
    if other is None:
        return f"{kind}|{form_key}|{a}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def conflicts_from(rows) -> list:
    out = []
    for r in rows:
        out.append({
            "Kind": r.get("kind", "CLOBBERED"),
            "A": r.get("a", "FixtureTarget.esp"),
            "B": r.get("b", "FixtureBase.esp"),
            "Summary": r.get("summary", "fixture row"),
            "Necessary": [{"FormKey": r["formKey"], "Role": r.get("role", "navmesh"),
                           "Label": r.get("label", "fixture cell"),
                           "Physical": r.get("physical", "Holds"),
                           "PhysicalDetail": r.get("detail", "fixture predicate")}],
        })
    return out


def predicates_from(base_emit: Path, entry: dict) -> list:
    """Every row of the base emit, re-measured under THIS arm — what a real probe's `--base` writes."""
    doc = json.loads(base_emit.read_text(encoding="utf-8"))
    table = entry.get("predicates") or {}
    default = entry.get("predicateDefault", "Holds")
    out = []
    for c in doc.get("Conflicts", []):
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        for n in c.get("Necessary", []):
            out.append({"Kind": c["Kind"], "A": c["A"], "B": c["B"], "FormKey": n["FormKey"], "Other": other,
                        "Physical": table.get(n["FormKey"], default),
                        "Detail": "fixture re-measurement under this arm"})
    return out



def default_json(probe: str, entry: dict, scen: dict, order_name: str) -> dict:
    """What the REAL probe writes under --json, in the shape the registry's `adapter.result` spec reads:
    switchprobe findings=summary.switches / judged=plugins / unjudgeable=unreadable; bosprobe findings=rows
    / judged=plugins. A fake that writes its own shape is rejected schema-unsupported, correctly."""
    n = int(entry.get("plugins", 4))
    if probe == "switchprobe":
        rows = entry.get("switches", [{"switch": "0A0010:FixtureTarget.esp", "kind": "quest-stage",
                                       "children": 2, "set": "on"}] * int(entry.get("switchCount", 3)))
        return {"generated": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
                "instance": scen.get("instance", "fixture://evalrun"), "profile": order_name,
                "plugins": n, "unreadable": list(entry.get("unreadable", [])),
                "summary": {"switches": len(rows), "children": 2 * len(rows), "descendants": 2 * len(rows),
                            "onSet": len(rows), "offSet": 0, "chained": 0, "cycles": 0, "playerParentRefs": 0},
                "perPlugin": [], "switches": rows, "jointFlips": []}
    if probe == "bosprobe":
        return {"profile": order_name, "plugins": n, "inis": list(entry.get("inis", [])),
                "rows": list(entry.get("rows", []))}
    if probe == "adviceprobe":
        # The controller hands adviceprobe a --json sink beside the registry's --out, and reads the
        # result paths (rules / pagesRead / warnings) off it, so the same document goes to both.
        return advice_report(entry, str(entry.get("modId", "45686")))
    if probe == "scopeprobe":
        scope_path = scen.get("scopeFrom")
        if scope_path and Path(scope_path).exists():
            doc = json.loads(Path(scope_path).read_text(encoding="utf-8"))
            doc["generatedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            return doc
    return {"probe": probe, "plugins": n, "rows": list(entry.get("rows", []))}


def advice_report(entry: dict, mod_id: str) -> dict:
    """adviceprobe's report-<id>.json: the registry reads findings=rules, judged=pagesRead,
    unjudgeable=warnings, so all three keys are present and countable."""
    return {"modId": mod_id,
            "rules": list(entry.get("rules", [{"before": "FixtureTarget.esp", "after": "FixtureBase.esp",
                                               "hardness": "advice", "source": "stored page",
                                               "resolutionChain": ["the page states a load order"]}])),
            "resolutions": [], "notes": ["a fixture report; no page was fetched"],
            "staleness": ["the stored copy's age is not the date this report was assembled"],
            "noOrderStated": False,
            "pagesRead": list(entry.get("pagesRead", [{"url": "stored://fixture/page", "from": "snapshot"}])),
            "catalogue": {"dir": "fixture", "refresh": "offline", "maxRequests": 0, "requestsAttempted": 0,
                          "requestsSucceeded": 0, "waitedMs": 0, "pagesServedFromSnapshot": 1,
                          "pagesFetched": 0, "pagesReplayedFromArchive": 0, "imports": []},
            "warnings": list(entry.get("warnings", [])), "choices": []}


def parse(argv):
    a = {"extra": [], "target": []}
    i = 0
    while i < len(argv):
        t = argv[i]
        if t in ("--order", "--emit", "--base", "--json", "--out", "--instance", "--profile", "--game",
                 "--state", "--target", "--mod", "--page") and i + 1 < len(argv):
            if t == "--target":
                a["target"].append(argv[i + 1])
            else:
                a[t[2:]] = argv[i + 1]
            i += 2
            continue
        a["extra"].append(t)
        i += 1
    return a


def main(probe: str, argv=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    a = parse(argv)
    spath = os.environ.get("EVALRUN_FAKE_SCENARIO")
    if not spath:
        print("fakeprobe: EVALRUN_FAKE_SCENARIO is not set", file=sys.stderr)
        return 2
    scen = json.loads(Path(spath).read_text(encoding="utf-8"))
    plugin_dir = Path(scen.get("pluginDir") or (Path(spath).resolve().parent.parent / "plugins"))

    emit = Path(a["emit"]) if a.get("emit") else None
    stem = emit.name[:-5] if emit and emit.name.endswith(".json") else (emit.stem if emit else "-")
    arms = (scen.get("arms") or {}).get(probe, {})
    entry = arms.get(stem) or arms.get("*") or {}
    behaviour = entry.get("behaviour", "ok")
    population = entry.get("population", scen.get("population", "load-time"))
    state = entry.get("state", scen.get("state", "baseline"))

    print(f"fakeprobe {probe}: arm '{stem}' behaviour '{behaviour}'")

    # THE INTERRUPTION SEAM. A test that kills the controller on a timer kills it at a varying point, and
    # sometimes nothing is in flight when it dies. So the probe the test names blocks until the test removes
    # a sentinel file: the controller is then provably mid-check, and the kill lands where the test means it
    # to. Nothing else changes - this probe is an ordinary `ok` arm once the sentinel is gone.
    sentinel = os.environ.get("EVALRUN_FAKE_SENTINEL")
    if sentinel and os.environ.get("EVALRUN_FAKE_BLOCK_PROBE") == probe:
        print(f"fakeprobe {probe}: blocking while {sentinel} exists", flush=True)
        waited = 0.0
        while Path(sentinel).exists() and waited < 120:
            time.sleep(0.05)
            waited += 0.05
        print(f"fakeprobe {probe}: sentinel gone after {waited:.2f}s", flush=True)

    # ---- the non-emitting adapters (--json / --out): scopeprobe, switchprobe, adviceprobe, patchprobe, driftprobe
    if a.get("json") and not emit:
        if behaviour == "no-emit":
            return 0
        if behaviour == "refuse":
            print("REFUSED: fixture refusal", file=sys.stderr)
            return 1
        order_name = Path(a["order"]).stem if a.get("order") else "fixture"
        doc = dict(entry.get("json") or default_json(probe, entry, scen, order_name))
        p = Path(a["json"])
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        return int(entry.get("code", 0)) if behaviour == "exit" else 0
    if a.get("out") and not emit:
        d = Path(a["out"])
        d.mkdir(parents=True, exist_ok=True)
        # The real probe names the file after the mod id it was ASKED for (--mod), so the fake must too:
        # a report-<other id>.json is a file the reader cannot find, and it is rejected for that.
        arg = a.get("mod")
        mod = str(arg) if arg and not str(arg).startswith("{") else str(entry.get("modId", "45686"))
        (d / f"report-{mod}.json").write_text(
            json.dumps(entry.get("report") or advice_report(entry, mod), indent=1), encoding="utf-8")
        (d / f"rules-{mod}.json").write_text(json.dumps(entry.get("rules") or {"SchemaVersion": 1, "Rules": []}, indent=1), encoding="utf-8")
        return 0

    if emit is None:
        print("fakeprobe: no --emit and no --json/--out", file=sys.stderr)
        return 2

    # ---- refusals, before anything is written
    if behaviour == "refuse":
        print("REFUSED: the fixture scenario says this arm refuses")
        return int(entry.get("code", 1))
    if behaviour == "illegal":
        print("REFUSED: MISSING MASTER — the candidate loads a plugin before its master; the game would not load it")
        return 2
    if behaviour == "no-emit":
        print("fakeprobe: exiting 0 without writing the emit (the orchestration case)")
        return 0

    order = Path(a["order"]) if a.get("order") else None
    candidate = json.loads(order.read_text(encoding="utf-8")) if order else {"Plugins": []}
    limits = candidate.get("Limits") or []
    cfg = configuration(candidate, order, plugin_dir, population, state, limits)

    rows = entry.get("rows", [])
    coverage = entry.get("coverage")
    if behaviour == "extraction-failed":
        rows, coverage = [], [{"Population": entry.get("coveragePopulation", "reference pairs"), "Judged": 0,
                               "Unjudgeable": {"GeometryUnreadable": int(entry.get("unjudgeable", 17))}}]
    if coverage is None:
        coverage = [{"Population": entry.get("coveragePopulation", "fixture population"),
                     "Judged": int(entry.get("judged", 42)), "Unjudgeable": {}}]

    doc = {
        "Probe": entry.get("probeName", probe) if behaviour == "wrong-probe-name" else probe,
        "Instance": scen.get("instance", "fixture://evalrun"),
        "GeneratedUtc": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
        "Conflicts": conflicts_from(rows),
        "Coverage": coverage,
        "Population": population,
        "State": entry.get("stateName", "night") if behaviour == "wrong-state" else state,
    }
    if a.get("base"):
        base = Path(a["base"])
        if base.exists():
            doc["Predicates"] = predicates_from(base, entry)
            doc["BaseEmit"] = str(base).replace("\\", "/")
    if behaviour == "wrong-state":
        cfg["State"] = doc["State"]
        cfg["Fingerprint"] = fingerprint(cfg["Plugins"], cfg["Providers"], cfg["Overlays"], cfg["Population"], cfg["State"])
    if behaviour != "no-configuration":
        doc["Configuration"] = cfg
    doc["PopulationIdentity"] = f"{doc['Population']} / state {doc['State']}"

    text = json.dumps(doc, indent=1)
    emit.parent.mkdir(parents=True, exist_ok=True)

    if behaviour == "crash-after":
        n = int(entry.get("bytes", max(1, len(text) // 3)))
        with open(emit, "w", encoding="utf-8") as f:
            f.write(text[:n])
            f.flush()
        print("fakeprobe: exiting mid-write")
        sys.stdout.flush()
        os._exit(int(entry.get("code", 1)))

    emit.write_text(text, encoding="utf-8")

    if behaviour == "stale-output":
        # A zero exit over an output that is not this run's: the GeneratedUtc and the mtime both predate the child.
        stale = entry.get("staleUtc", "2026-09-01 00:00:00Z")
        doc["GeneratedUtc"] = stale
        emit.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        old = time.mktime(time.strptime(stale, "%Y-%m-%d %H:%M:%SZ"))
        os.utime(emit, (old, old))
        print(f"fakeprobe: wrote a STALE emit (GeneratedUtc {stale}) and exited 0")
        return 0

    if behaviour == "exit":
        return int(entry.get("code", 4))
    return 0
