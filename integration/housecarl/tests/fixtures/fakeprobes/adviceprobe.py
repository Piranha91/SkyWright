"""Fixture stand-in for adviceprobe, the module's probe (moved with patch.author-advice from SkyWright's evalrun fixtures,
where fakeprobe.py held its two branches): `evalcontrol plan --probe-cmd adviceprobe=python THIS` in the module's
suites (module_fakes.probe_cmds()). It takes its behaviour from the same scenario file as SkyWright's fake probes
(EVALRUN_FAKE_SCENARIO, its `arms.adviceprobe` entry, `"*"` the fallback; without the variable every arm is `ok`) and
parses the probe's flags with SkyWright's fakeprobe.parse.

  --out DIR   writes report-<mod>.json (the --mod id it was asked for) and rules-<mod>.json, as the real probe does;
  --json FILE writes the same report there (a registry that names a --json sink).
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


def advice_report(entry: dict, mod_id: str) -> dict:
    """adviceprobe's report-<id>.json: the registry reads findings=rules+clashes, judged=pagesRead,
    unjudgeable=warnings, so every key is present and countable."""
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
            "clashes": list(entry.get("clashes", [])),
            "warnings": list(entry.get("warnings", [])), "choices": []}


def main(argv) -> int:
    a = parse(argv)
    # the overlay's evaluate is started by the module's suites without the app suite's scenario variable: no
    # scenario is then every arm `ok` with the default report
    spath = os.environ.get("EVALRUN_FAKE_SCENARIO")
    scen = json.loads(Path(spath).read_text(encoding="utf-8")) if spath else {}
    arms = (scen.get("arms") or {}).get("adviceprobe", {})
    stem = Path(a["json"]).stem if a.get("json") else "-"
    entry = arms.get(stem) or arms.get("*") or {}
    behaviour = entry.get("behaviour", "ok")
    print(f"fakeprobe adviceprobe: arm '{stem}' behaviour '{behaviour}'")
    if a.get("json"):
        if behaviour == "no-emit":
            return 0
        if behaviour == "refuse":
            print("REFUSED: fixture refusal", file=sys.stderr)
            return 1
        doc = dict(entry.get("json") or advice_report(entry, str(entry.get("modId", "45686"))))
        p = Path(a["json"])
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        return int(entry.get("code", 0)) if behaviour == "exit" else 0
    if a.get("out"):
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
    print("fakeprobe: no --json and no --out", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
