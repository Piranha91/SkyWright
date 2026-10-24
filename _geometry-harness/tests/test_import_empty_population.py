"""An import that judged nothing stays refused, and says why and what to try (cases 1340-1344).

    python _geometry-harness/tests/test_import_empty_population.py

`evalrun import-results` refuses an import whose coverage judged 0 as `extraction-failed`. A shakedown found it refusing
on arms that load none of the plan's targets, where 0 looked like the honest count. The Gate A review asked for the
accept to be shown per check from what the import covers. It cannot be shown for any of the five mcp-only checks from
what evalrun holds:
- every such import is bound to the arm's requested population ('load-time' here), not to the targets;
- no registry row declares a population, and their triggers differ (always, masters, record types).

So a judged-0 import is refused on every arm, target or not. The refusal now says that 0 cannot be told from a capture
that read nothing and what to try. On an arm without the targets it also says why that does not make 0 the answer.

Drives the REAL evalrun.py over the evalrun fixtures: the shipped registry trimmed to loadability.masters-and-order (the
prerequisite), records.registration-and-dialogue (record-type trigger; the fixture target carries a QUST) and
loadability.missing-assets (trigger `always`), both mcp-only. Also the fixture scopeprobe stand-in, and three arms: the
baseline and a reorder (both load FixtureTarget.esp) and one without it.

    1340  dialogue, judged 0 on the arm without the target: refused, one sentence naming the target, the population the
          import is bound to and what to try; the check stays open.
    1341  dialogue, judged 0 with unjudgeable members on that arm: refused.
    1342  dialogue, judged 0 on the baseline, which loads the target: refused with what to try, without the no-target note.
    1343  finalize holds the dialogue check open on all three arms.
    1344  missing-assets, judged 0 on the arm without the target: refused the same way (a second check whose
          population evalrun cannot show empty).
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
FIXT = HERE / "fixtures" / "evalrun"
REGISTRY = REPO / ".claude" / "skills" / "world-edit-compatibility" / "references" / "check-registry.json"
DIALOGUE = "records.registration-and-dialogue"
ASSETS = "loadability.missing-assets"
KEEP = ["loadability.masters-and-order", DIALOGUE, ASSETS]
NO_TARGET = "candidate-02-notarget"
NOTE = "this arm loads none of the plan's target plugins, 'FixtureTarget.esp', but this import is bound to the population 'load-time'"
REMEDY = "check that the capture read this arm's records and import it again"

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def evalrun(args, env):
    return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env, timeout=300)


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def fingerprint(cfg: dict) -> str:
    """The configuration fingerprint as the C# writes it (the same function test_evalrun.py's import_fingerprint is)."""
    lines = [f"schema:{cfg.get('schemaVersion', 1)}\n", f"population:{cfg.get('population')}\n",
             f"state:{cfg.get('state')}\n"]
    lines += [f"plugin:{str(p.get('name') or '').lower()}:{p.get('sha256') or ''}\n" for p in cfg.get("plugins") or []]
    lines += [f"provider:{p.get('kind') or ''}:{str(p.get('name') or '').lower()}\n" for p in cfg.get("providers") or []]
    lines += [f"overlay:{o.get('kind') or ''}:{o.get('sha256') or ''}\n" for o in cfg.get("overlays") or []]
    return hashlib.sha256("".join(lines).encode("utf-8")).hexdigest()


def make_import(folder: Path, plan: dict, cid: str, arm: dict, judged: int, unjudgeable: dict) -> Path:
    """A schema-1 no-finding import of `cid` for `arm`, everything stated from the arm's frozen request."""
    folder.mkdir(parents=True, exist_ok=True)
    request, response = folder / "request.json", folder / "response.json"
    init = {"jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                       "clientInfo": {"name": f"evalrun:{plan['planId']}:{arm['armId']}", "version": "fixture-capture 1"}}}
    request.write_text(json.dumps(init) + "\n" + json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}})
                       + "\n", encoding="utf-8")
    response.write_text(json.dumps({"findings": 0}), encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in req["providers"]], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"], "state": "baseline",
           "origin": "fresh", "capturedUtc": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": judged, "Unjudgeable": unjudgeable}]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha(p)}
                       for role, p in (("request", request), ("response", response))]}
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def setup(tmp: Path) -> dict:
    gen = tmp / "fx"
    subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen)], check=True,
                   capture_output=True, text=True)
    real = json.loads(REGISTRY.read_text(encoding="utf-8"))
    keep = [c for c in real["checks"] if c["id"] in KEEP]
    reg = dict(real, checks=keep, families=[f for f in real["families"] if f["id"] in {c["family"] for c in keep}])
    (tmp / "registry.json").write_text(json.dumps(reg, indent=1), encoding="utf-8")
    case = gen / "cases" / "case-01-geometry-and-quest"
    cands = tmp / "candidates"
    cands.mkdir()
    for f in ("candidate-00-baseline.json", "candidate-01-reorder.json"):
        shutil.copy2(case / "candidates" / f, cands / f)
    base = json.loads((case / "candidates" / "candidate-00-baseline.json").read_text(encoding="utf-8"))
    drop = dict(base, Name=NO_TARGET, Changes=["removed FixtureTarget.esp"],
                Plugins=[p for p in base["Plugins"] if p != "FixtureTarget.esp"])
    (cands / f"{NO_TARGET}.json").write_text(json.dumps(drop, indent=1), encoding="utf-8")
    return {"registry": tmp / "registry.json", "scope": case / "scope.json", "cands": cands,
            "scenario": case / "scenario.json"}


def entry(run: Path, cid: str, aid: str) -> dict:
    led = json.loads((run / "ledger.json").read_text(encoding="utf-8")) if (run / "ledger.json").exists() else {}
    return (led.get("entries") or {}).get(f"{cid}__{aid}") or {}


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-import-empty-"))
    try:
        fx = setup(tmp)
        env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(fx["scenario"]), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"),
                   PYTHONIOENCODING="utf-8")
        for k in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH"):
            env.pop(k, None)
        run = tmp / "run"
        p = evalrun(["plan", "--registry", fx["registry"], "--scope", fx["scope"], "--candidates", fx["cands"],
                     "--out", run, "--harness", HARNESS, "--depth", "standard",
                     "--probe-cmd", f"scopeprobe={sys.executable} {FIXT / 'fakeprobes' / 'scopeprobe'}.py"], env)
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}
        arms = {a["armId"]: a for a in plan.get("arms") or []}
        rows = {(c.get("checkId"), a.get("armId")): a.get("status") for c in plan.get("checks") or []
                if c.get("checkId") in (DIALOGUE, ASSETS) for a in c.get("arms") or []}
        r = evalrun(["run", "--run", run], env)
        print("\n=== setup: both mcp-only checks wait for an import on all three arms ===")
        targets = [t.get("name") for t in plan.get("targets") or []]
        no_target = arms.get(NO_TARGET) or {}
        check(p.returncode == 0 and r.returncode == 0 and targets == ["FixtureTarget.esp"] and len(rows) == 6
              and set(rows.values()) == {"mcp-only"} and "FixtureTarget.esp" not in (no_target.get("plugins") or []),
              f"setup: plan {p.returncode}, run {r.returncode}, targets {targets}, rows {rows}")
        if not no_target:
            return 1

        def imp(tag, cid, arm, judged, unjudgeable):
            m = make_import(tmp / tag, plan, cid, arm, judged, unjudgeable)
            return evalrun(["import-results", "--run", run, "--file", m], env)

        print("\n=== case-1340: dialogue, judged 0 on the arm without the target ===")
        r = imp("i1340", DIALOGUE, no_target, 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and "extraction-failed: imported evidence judged 0" in msg and msg.count(NOTE) == 1
              and REMEDY in msg and entry(run, DIALOGUE, NO_TARGET).get("status") == "mcp-only",
              f"1340: refused in one sentence naming the target, the bound population and what to try; still open "
              f"(exit {r.returncode}; {msg[-500:]!r})")

        print("\n=== case-1341: dialogue, judged 0 with unjudgeable members on that arm ===")
        r = imp("i1341", DIALOGUE, no_target, 0, {"unreadable record": 3})
        check(r.returncode == 2 and "extraction-failed" in r.stderr and entry(run, DIALOGUE, NO_TARGET).get("status") == "mcp-only",
              f"1341: refused, the check stays open (exit {r.returncode}; {r.stderr.strip()[-240:]!r})")

        print("\n=== case-1342: dialogue, judged 0 on the baseline, which loads the target ===")
        r = imp("i1342", DIALOGUE, arms["baseline"], 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and "extraction-failed: imported evidence judged 0" in msg and REMEDY in msg
              and "loads none of the plan's target plugins" not in msg
              and entry(run, DIALOGUE, "baseline").get("status") == "mcp-only",
              f"1342: refused with what to try and no no-target note (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1344: missing-assets, judged 0 on the arm without the target ===")
        r = imp("i1344", ASSETS, no_target, 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and msg.count(NOTE) == 1 and REMEDY in msg
              and entry(run, ASSETS, NO_TARGET).get("status") == "mcp-only",
              f"1344: refused the same way (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1343: finalize holds the dialogue check open on every arm ===")
        evalrun(["finalize", "--run", run], env)
        disp = json.loads((run / "disposition.json").read_text(encoding="utf-8")) if (run / "disposition.json").exists() else {}
        missing = (disp.get("evaluation") or {}).get("missing")
        held = sorted(x.get("armId") for x in (missing or []) if x.get("checkId") == DIALOGUE)
        check(isinstance(missing, list) and held == ["baseline", "candidate-01-reorder", NO_TARGET],
              f"1343: open on all three arms ({held})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
