"""A judged-0 import is "nothing to judge" only on two independent facts (cases 1340-1346).

    python integration/housecarl/tests/test_import_empty_population.py

`evalrun import-results` refused every import whose coverage judged 0 as `extraction-failed`, so a check stayed open
for ever on an arm that holds none of its population (a shakedown finding). evalrun cannot tell that from its own data
alone: an mcp-only import is bound to the arm's whole population, and no registry row narrows it to the targets. The
importer is the one party that knows how many members its population held, so an import may state `Members` per
coverage line. A judged-0 import is accepted as nothing to judge, and recorded not-applicable with one sentence, only
when both of these hold:
- every coverage line states `Members: 0`;
- the arm loads none of the plan's target plugins.

Anything else stays refused, and the refusal names a step that can succeed.

Drives SkyWright's controller and this module's evalcontrol over the evalrun fixtures:
- the app scan: SkyWright's registry trimmed to loadability.masters-and-order (the record rows' app prerequisite), run,
  finalized and bundled; the AI-side overlay over it (evalcontrol plan, evaluate) holds the two mcp-only checks of
  ai-checks.json used here: records.registration-and-dialogue (record-type trigger; the fixture target carries a QUST)
  and loadability.missing-assets (trigger `always`); every import is evalcontrol import, into the overlay;
- the fixture scopeprobe stand-in;
- three arms: the baseline and a reorder (both load FixtureTarget.esp) and one without it.

    1340  dialogue, judged 0, Members 0 on every line, on the arm without the target: accepted, not-applicable with one
          sentence naming the target, printed once; a second import of the answered row is a duplicate.
    1341  dialogue, judged 0 with unjudgeable members on that arm: refused, re-capture.
    1342  dialogue, judged 0 on the baseline, members unstated: refused, re-capture, no no-target remedy.
    1343  after 1340, resume keeps the answer and finalize holds the check open only on the two target arms.
    1344  missing-assets, judged 0 on the arm without the target, members unstated: refused, naming Members: 0 as the step.
    1345  dialogue, judged 0 on the arm without the target, members unstated: refused the same way (before 1340).
    1346  dialogue, judged 0, Members 0, on the baseline, which loads the target: refused (the target's records are the
          population there).
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
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is
HARNESS = SW.HARNESS
sys.path.append(str(HARNESS))
sys.path.append(str(HARNESS / "tests"))
import buildlayout  # noqa: E402  where the build put each program
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
from test_evalrun import CONTROLLER  # noqa: E402  (the built-in controller, runcontrol, as test_evalrun starts it)
FIXT = HARNESS / "tests" / "fixtures" / "evalrun"
EVALCONTROL = SW.tool("evalcontrol")
REGISTRY = SW.DATA / "check-registry.json"
DIALOGUE = "records.registration-and-dialogue"
ASSETS = "loadability.missing-assets"
KEEP = ["loadability.masters-and-order"]   # the app scan's trim; the two record rows are the overlay's (ai-checks.json)
NO_TARGET = "candidate-02-notarget"
STATE_MEMBERS = "state Members: 0 on each coverage line and import again"
RECAPTURE = "re-capture this arm's evidence and import it again"
ACCEPTED = "nothing to judge: the import states that its population held no member on this arm"

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def evalrun(args, env):
    """SkyWright's controller (runcontrol), for the app scan."""
    return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env, timeout=300)


def evalcontrol(args, env):
    """This module's evalcontrol, for the AI-side overlay the record rows are planned in."""
    return subprocess.run([str(EVALCONTROL)] + [str(a) for a in args], capture_output=True, text=True,
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


def make_import(folder: Path, plan: dict, cid: str, arm: dict, judged: int, unjudgeable: dict,
                members: int | None = None) -> Path:
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
    line = {"Population": "records", "Judged": judged, "Unjudgeable": unjudgeable}
    if members is not None:
        line["Members"] = members
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"], "state": "baseline",
           "origin": "fresh", "capturedUtc": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0, "coverage": [line]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha(p)}
                       for role, p in (("request", request), ("response", response))]}
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def setup(tmp: Path, keep: list[str] = KEEP) -> dict:
    gen = tmp / "fx"
    subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen)], check=True,
                   capture_output=True, text=True)
    real = json.loads(REGISTRY.read_text(encoding="utf-8"))
    kept = [c for c in real["checks"] if c["id"] in keep]
    reg = dict(real, checks=kept, families=[f for f in real["families"] if f["id"] in {c["family"] for c in kept}])
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
                     "--out", run, "--harness", buildlayout.root(), "--depth", "standard",
                     "--probe-cmd", f"scopeprobe={sys.executable} {FIXT / 'fakeprobes' / 'scopeprobe'}.py"], env)
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}
        arms = {a["armId"]: a for a in plan.get("arms") or []}
        rows = {(c.get("checkId"), a.get("armId")): a.get("status") for c in plan.get("checks") or []
                if c.get("checkId") in (DIALOGUE, ASSETS) for a in c.get("arms") or []}
        r = evalrun(["run", "--run", run], env)
        # the record rows are the AI-side overlay's, planned over the finished, bundled app scan
        ok = [evalrun(["finalize", "--run", run], env).returncode,
              evalrun(["bundle", "--run", run, "--out", run / "bundle", "--no-names"], env).returncode,
              evalcontrol(["plan", "--app", run, "--app-bundle", run / "bundle", "--out", run / "ai"], env).returncode,
              evalcontrol(["evaluate", "--run", run / "ai"], env).returncode]
        run = run / "ai"
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}
        arms = {a["armId"]: a for a in plan.get("arms") or []}
        rows = {(c.get("checkId"), a.get("armId")): a.get("status") for c in plan.get("checks") or []
                if c.get("checkId") in (DIALOGUE, ASSETS) for a in c.get("arms") or []}
        print("\n=== setup: both mcp-only checks wait for an import on all three arms ===")
        targets = [t.get("name") for t in plan.get("targets") or []]
        no_target = arms.get(NO_TARGET) or {}
        check(p.returncode == 0 and r.returncode == 0 and ok == [0, 0, 0, 0] and targets == ["FixtureTarget.esp"] and len(rows) == 6
              and set(rows.values()) == {"mcp-only"} and "FixtureTarget.esp" not in (no_target.get("plugins") or []),
              f"setup: plan {p.returncode}, run {r.returncode}, overlay {ok}, targets {targets}, rows {rows}")
        if not no_target:
            return 1

        def imp(tag, cid, arm, judged, unjudgeable, members=None):
            m = make_import(tmp / tag, plan, cid, arm, judged, unjudgeable, members)
            return evalcontrol(["import", "--run", run, "--file", m], env)

        print("\n=== case-1345: dialogue, judged 0 on the arm without the target, members unstated ===")
        r = imp("i1345", DIALOGUE, no_target, 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and msg.count(STATE_MEMBERS) == 1 and "'FixtureTarget.esp'" in msg
              and entry(run, DIALOGUE, NO_TARGET).get("status") == "mcp-only",
              f"1345: refused; the step it names is stating Members: 0 (exit {r.returncode}; {msg[-400:]!r})")

        print("\n=== case-1341: dialogue, judged 0 with unjudgeable members on that arm ===")
        r = imp("i1341", DIALOGUE, no_target, 0, {"unreadable record": 3}, 0)
        msg = r.stderr.strip()
        check(r.returncode == 2 and "extraction-failed" in msg and RECAPTURE in msg and STATE_MEMBERS not in msg
              and entry(run, DIALOGUE, NO_TARGET).get("status") == "mcp-only",
              f"1341: refused, re-capture, the check stays open (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1342: dialogue, judged 0 on the baseline, members unstated ===")
        r = imp("i1342", DIALOGUE, arms["baseline"], 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and "extraction-failed: imported evidence judged 0" in msg and RECAPTURE in msg
              and STATE_MEMBERS not in msg and entry(run, DIALOGUE, "baseline").get("status") == "mcp-only",
              f"1342: refused, re-capture, no no-target remedy (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1346: dialogue, judged 0, Members 0, on the baseline, which loads the target ===")
        r = imp("i1346", DIALOGUE, arms["baseline"], 0, {}, 0)
        msg = r.stderr.strip()
        check(r.returncode == 2 and "loads the plan's target 'FixtureTarget.esp'" in msg and RECAPTURE in msg
              and entry(run, DIALOGUE, "baseline").get("status") == "mcp-only",
              f"1346: refused although Members is 0 (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1344: missing-assets, judged 0 on the arm without the target, members unstated ===")
        r = imp("i1344", ASSETS, no_target, 0, {})
        msg = r.stderr.strip()
        check(r.returncode == 2 and msg.count(STATE_MEMBERS) == 1
              and entry(run, ASSETS, NO_TARGET).get("status") == "mcp-only",
              f"1344: refused the same way (exit {r.returncode}; {msg[-300:]!r})")

        print("\n=== case-1340: dialogue, judged 0, Members 0, on the arm without the target ===")
        r = imp("i1340", DIALOGUE, no_target, 0, {}, 0)
        rec_path = run / "receipts" / f"{DIALOGUE}__{NO_TARGET}.json"
        rec = json.loads(rec_path.read_text(encoding="utf-8")) if rec_path.exists() else {}
        res = rec.get("result") or {}
        e = entry(run, DIALOGUE, NO_TARGET)
        check(r.returncode == 0 and r.stdout.count(ACCEPTED) == 1,
              f"1340: accepted, one sentence printed (exit {r.returncode}; {(r.stdout + r.stderr).strip()[-300:]!r})")
        check(e.get("status") == "not-applicable" and res.get("execution") == "not-applicable"
              and res.get("detector") is None and ACCEPTED in (res.get("note") or "")
              and "'FixtureTarget.esp'" in (res.get("note") or "") and (rec.get("validation") or {}).get("accepted") is True,
              f"1340: entry and receipt not-applicable, the sentence naming the target ({e.get('status')}; "
              f"{res.get('execution')}: {res.get('note')!r})")
        r = imp("i1340b", DIALOGUE, no_target, 0, {}, 0)
        check(r.returncode == 2 and "duplicate-receipt" in r.stderr,
              f"1340: a second import of the answered row is a duplicate (exit {r.returncode})")

        print("\n=== case-1343: resume keeps the answer; finalize holds it open only where the target loads ===")
        r = evalcontrol(["evaluate", "--run", run], env)   # the overlay's resume: what is kept is re-checked and reused
        e = entry(run, DIALOGUE, NO_TARGET)
        check(r.returncode == 0 and e.get("status") == "not-applicable",
              f"1343: after resume the entry is still not-applicable (resume {r.returncode}; {e.get('status')})")
        evalcontrol(["finalize", "--run", run], env)
        disp = json.loads((run / "disposition.json").read_text(encoding="utf-8")) if (run / "disposition.json").exists() else {}
        missing = (disp.get("evaluation") or {}).get("missing")
        held = sorted(x.get("armId") for x in (missing or []) if x.get("checkId") == DIALOGUE)
        check(isinstance(missing, list) and held == ["baseline", "candidate-01-reorder"],
              f"1343: open on the two target arms only ({held})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
