"""An import that judged nothing on an arm holding none of the check's population is an answer (cases 1340-1343).

    python _geometry-harness/tests/test_import_empty_population.py

`evalrun import-results` refuses an import whose coverage judged 0 as `extraction-failed`. That is right where the arm
loads a target plugin: the population is there and nothing was read. It was wrong on an arm that loads none of the
plan's targets: the check's population (the target's records) does not exist there, so 0 is the honest count, and the
check stayed open for ever (a shakedown finding). The rule is the one the route-cut check already uses on such
an arm (P51): the plan's targets against the arm's frozen request.

Drives the REAL evalrun.py over the evalrun fixtures: the shipped registry trimmed to loadability.masters-and-order (its
prerequisite) and records.registration-and-dialogue (mcp-only; the fixture target carries a QUST), the fixture
scopeprobe stand-in, and three arms: the baseline and a reorder (both load FixtureTarget.esp) and one without it.

    1340  judged 0, nothing unjudgeable, on the arm without the target: accepted, recorded not-applicable on that arm with
          one sentence saying why, and import-results prints it.
    1341  judged 0 with unjudgeable members on the arm without the target: still refused (members existed).
    1342  judged 0 on the baseline, which loads the target: still refused as extraction-failed (a genuine failure).
    1343  after 1340, resume keeps the answer and finalize does not count the check open on that arm.
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
CID = "records.registration-and-dialogue"
KEEP = ["loadability.masters-and-order", CID]
NO_TARGET = "candidate-02-notarget"
SENTENCE = "this arm loads none of the plan's target plugins"

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


def make_import(folder: Path, plan: dict, arm: dict, judged: int, unjudgeable: dict) -> Path:
    """A schema-1 no-finding import for `arm`, everything stated from the arm's frozen request."""
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
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": CID, "armId": arm["armId"], "state": "baseline",
           "origin": "fresh", "capturedUtc": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "contested DialogTopic records", "Judged": judged,
                                    "Unjudgeable": unjudgeable}]},
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


def entry(run: Path, aid: str) -> dict:
    led = json.loads((run / "ledger.json").read_text(encoding="utf-8")) if (run / "ledger.json").exists() else {}
    return (led.get("entries") or {}).get(f"{CID}__{aid}") or {}


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
        rows = {a.get("armId"): a.get("status") for c in plan.get("checks") or [] if c.get("checkId") == CID
                for a in c.get("arms") or []}
        r = evalrun(["run", "--run", run], env)
        print("\n=== setup: the dialogue check waits for an import on all three arms ===")
        targets = [t.get("name") for t in plan.get("targets") or []]
        no_target = arms.get(NO_TARGET) or {}
        check(p.returncode == 0 and r.returncode == 0 and targets == ["FixtureTarget.esp"]
              and set(rows) == {"baseline", "candidate-01-reorder", NO_TARGET} and set(rows.values()) == {"mcp-only"}
              and "FixtureTarget.esp" not in (no_target.get("plugins") or []),
              f"setup: plan {p.returncode}, run {r.returncode}, targets {targets}, rows {rows}")
        if not no_target:
            return 1

        print("\n=== case-1341: judged 0 with unjudgeable members on the arm without the target ===")
        m = make_import(tmp / "i1341", plan, no_target, 0, {"unreadable record": 3})
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        check(r.returncode == 2 and "extraction-failed" in r.stderr and entry(run, NO_TARGET).get("status") == "mcp-only",
              f"1341: refused, the check stays open (exit {r.returncode}; {r.stderr.strip()[-240:]!r})")

        print("\n=== case-1342: judged 0 on the baseline, which loads the target ===")
        m = make_import(tmp / "i1342", plan, arms["baseline"], 0, {})
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        check(r.returncode == 2 and "extraction-failed: imported evidence judged 0" in r.stderr
              and entry(run, "baseline").get("status") == "mcp-only",
              f"1342: a genuine failure is still refused as extraction-failed (exit {r.returncode}; {r.stderr.strip()[-240:]!r})")

        print("\n=== case-1340: judged 0, nothing unjudgeable, on the arm without the target ===")
        m = make_import(tmp / "i1340", plan, no_target, 0, {})
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        rec_path = run / "receipts" / f"{CID}__{NO_TARGET}.json"
        rec = json.loads(rec_path.read_text(encoding="utf-8")) if rec_path.exists() else {}
        res = rec.get("result") or {}
        e = entry(run, NO_TARGET)
        check(r.returncode == 0 and SENTENCE in r.stdout and r.stdout.count(SENTENCE) == 1,
              f"1340: accepted, and import-results says why in one sentence (exit {r.returncode}; "
              f"{(r.stdout + r.stderr).strip()[-300:]!r})")
        check(e.get("status") == "not-applicable" and res.get("execution") == "not-applicable"
              and SENTENCE in (res.get("note") or "") and "FixtureTarget.esp" in (res.get("note") or "")
              and (rec.get("validation") or {}).get("accepted") is True,
              f"1340: the arm's entry and receipt read not-applicable with the sentence naming the target "
              f"({e.get('status')}; {res.get('execution')}: {res.get('note')!r})")

        print("\n=== case-1343: resume keeps the answer; finalize does not count it open ===")
        r = evalrun(["resume", "--run", run], env)
        e = entry(run, NO_TARGET)
        evalrun(["finalize", "--run", run], env)
        disp = json.loads((run / "disposition.json").read_text(encoding="utf-8")) if (run / "disposition.json").exists() else {}
        missing = (disp.get("evaluation") or {}).get("missing")
        held = sorted(x.get("armId") for x in (missing or []) if x.get("checkId") == CID)
        check(r.returncode == 0 and e.get("status") == "not-applicable",
              f"1343: after resume the entry is still not-applicable (resume {r.returncode}; {e.get('status')})")
        check(isinstance(missing, list) and NO_TARGET not in held and held == ["baseline", "candidate-01-reorder"],
              f"1343: finalize holds the check open on the two arms with the target, not on the one without "
              f"(open on {held})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
