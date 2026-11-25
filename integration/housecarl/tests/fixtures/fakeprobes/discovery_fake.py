"""Fixture stand-in for the module's discovery probes (patchfind, poolprobe, fomodprobe; moved with them from SkyWright's
evalrun fixtures, where fakeprobe.py's default_json held this branch): `evalcontrol plan --probe-cmd NAME=python FORWARDER`
in the module's suites (module_fakes.probe_cmds()). It takes its behaviour from the same scenario file as SkyWright's
fake probes (EVALRUN_FAKE_SCENARIO, its `arms.<probe>` entry, `"*"` the fallback; without the variable every arm is
`ok`) and parses the probe's flags with SkyWright's fakeprobe.parse.

  --json FILE  writes the hit list (schema 1, patchfind/DiscoveryHits.cs); the list reads counts.*.
"""
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[2]))  # the module's folder
import skywright_paths as SW  # noqa: E402
sys.path.insert(0, str(SW.HARNESS / "tests" / "fixtures" / "evalrun"))
from fakeprobe import parse  # noqa: E402  SkyWright's fake probes' flag reader


def hit_list(probe: str, entry: dict) -> dict:
    n = int(entry.get("plugins", 4))
    # The discovery probes' --json hit list (schema 1, patchfind/DiscoveryHits.cs); the list reads counts.*.
    hits = list(entry.get("hits", []))
    return {"schemaVersion": 1, "probe": probe, "mode": "fixture", "complete": True,
            "sourcesSearched": [{"kind": "instance", "path": "fixture://evalrun", "name": "fixture"}],
            "sourcesNotSearched": [], "counts": {"judged": n, "unjudgeable": int(entry.get("unreadableCount", 0)),
                                                 "hits": len(hits)},
            "hits": hits, "unjudgeableItems": [], "detail": {}}


def main(probe: str, argv) -> int:
    a = parse(argv)
    spath = os.environ.get("EVALRUN_FAKE_SCENARIO")
    scen = json.loads(Path(spath).read_text(encoding="utf-8")) if spath else {}
    arms = (scen.get("arms") or {}).get(probe, {})
    stem = Path(a["json"]).stem if a.get("json") else "-"
    entry = arms.get(stem) or arms.get("*") or {}
    behaviour = entry.get("behaviour", "ok")
    print(f"fakeprobe {probe}: arm '{stem}' behaviour '{behaviour}'")
    if not a.get("json"):
        print("fakeprobe: no --json", file=sys.stderr)
        return 2
    if behaviour == "no-emit":
        return 0
    if behaviour == "refuse":
        print("REFUSED: fixture refusal", file=sys.stderr)
        return 1
    doc = dict(entry.get("json") or hit_list(probe, entry))
    p = Path(a["json"])
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return int(entry.get("code", 0)) if behaviour == "exit" else 0
