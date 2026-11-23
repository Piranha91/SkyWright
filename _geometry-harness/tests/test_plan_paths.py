"""A plan runs the same way from any working folder (cases 1330-1334).

    python _geometry-harness/tests/test_plan_paths.py

`evalrun plan` used to store --registry, --scope, --candidates, --settings and each arm's candidate path exactly as
given. Planned from one folder with relative paths and run from another (the machine-queue wrapper runs from the
repository root), the run refused hours later: "scope-missing: the plan's scope scope\\... does not exist". Drives the
REAL evalrun.py over the evalrun fixtures, with the shipped registry trimmed to loadability.masters-and-order and the
fixture scopeprobe stand-in. No game, instance or real probe is needed.

    1330  plan.json stores every input path absolute, each naming the file that was given.
    1331  `run` from another folder completes: no premise refusal, every arm's masters-and-order receipt accepted.
    1332  `resume` from a third folder is accepted.
    1333  a plan written before this change (relative paths stored) and run from a folder where they do not resolve is
          refused in one sentence that names the relative path and says what to try.
    1334  control: that older plan still runs from the folder it was planned in.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
from test_evalrun import CONTROLLER  # noqa: E402  (the built-in controller, runcontrol, as test_evalrun starts it)
FIXT = HERE / "fixtures" / "evalrun"
REGISTRY = REPO / "data" / "check-registry.json"
KEEP = ["loadability.masters-and-order"]
MAO = "loadability.masters-and-order"

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def evalrun(args, env, cwd):
    return subprocess.run(CONTROLLER + [str(a) for a in args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env, cwd=str(cwd), timeout=300)


def setup(tmp: Path) -> dict:
    """The planning folder holds every input under a relative name, as a lane's run folder does."""
    gen = tmp / "fx"
    subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen)], check=True,
                   capture_output=True, text=True)
    real = json.loads(REGISTRY.read_text(encoding="utf-8"))
    keep = [c for c in real["checks"] if c["id"] in KEEP]
    reg = dict(real, checks=keep, families=[f for f in real["families"] if f["id"] in {c["family"] for c in keep}])
    plan_dir = tmp / "planning"
    (plan_dir / "inputs").mkdir(parents=True)
    (plan_dir / "inputs" / "registry.json").write_text(json.dumps(reg, indent=1), encoding="utf-8")
    case = gen / "cases" / "case-01-geometry-and-quest"
    (plan_dir / "scope").mkdir()
    shutil.copy2(case / "scope.json", plan_dir / "scope" / "scope.json")
    shutil.copytree(case / "candidates", plan_dir / "candidates")
    (plan_dir / "inputs" / "settings.json").write_text(
        json.dumps({"schemaVersion": 1, "evaluation": {"depth": "standard"}}), encoding="utf-8")
    return {"dir": plan_dir, "scenario": case / "scenario.json"}


def plan_args(run: Path) -> list:
    return ["plan", "--registry", Path("inputs") / "registry.json", "--scope", Path("scope") / "scope.json",
            "--candidates", "candidates", "--settings", Path("inputs") / "settings.json", "--out", run,
            "--harness", buildlayout.root(), "--probe-cmd", f"scopeprobe={sys.executable} {FIXT / 'fakeprobes' / 'scopeprobe'}.py"]


def stored_paths(plan: dict) -> dict:
    out = {"registry": (plan.get("registry") or {}).get("path"), "scope": (plan.get("scope") or {}).get("path"),
           "candidatesDir": plan.get("candidatesDir"),
           "settings": (((plan.get("explicitDecisions") or {}).get("settings")) or {}).get("path")}
    for a in plan.get("arms") or []:
        out[f"arm {a.get('armId')}"] = a.get("candidatePath")
    return out


def mao_accepted(run: Path, plan: dict) -> list[str]:
    led = json.loads((run / "ledger.json").read_text(encoding="utf-8")) if (run / "ledger.json").exists() else {}
    entries = led.get("entries") or {}
    return [a["armId"] for a in plan.get("arms") or []
            if (entries.get(f"{MAO}__{a['armId']}") or {}).get("status") in ("completed", "reused")]


def relativise(run: Path, planning: Path) -> None:
    """Rewrite a plan as a planner before this change stored it: the given relative strings, not absolute paths."""
    p = run / "plan.json"
    text = p.read_text(encoding="utf-8")
    plan = json.loads(text)
    root = str(planning.resolve())

    def rel(v):
        return os.path.relpath(v, root) if isinstance(v, str) and os.path.isabs(v) and v.startswith(root) else v
    plan["registry"]["path"] = rel(plan["registry"]["path"])
    plan["scope"]["path"] = rel(plan["scope"]["path"])
    plan["candidatesDir"] = rel(plan["candidatesDir"])
    plan["explicitDecisions"]["settings"]["path"] = rel(plan["explicitDecisions"]["settings"]["path"])
    for a in plan["arms"]:
        a["candidatePath"] = rel(a["candidatePath"])
    p.write_text(json.dumps(plan, indent=1), encoding="utf-8")
    import hashlib
    (run / "plan.sha256").write_text(hashlib.sha256(p.read_bytes()).hexdigest() + "\n", encoding="utf-8")


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-plan-paths-"))
    try:
        fx = setup(tmp)
        env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(fx["scenario"]), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"),
                   PYTHONIOENCODING="utf-8")
        for k in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH", "HOUSECARL_EVAL_SETTINGS"):
            env.pop(k, None)
        elsewhere, third = tmp / "elsewhere", tmp / "third"
        elsewhere.mkdir()
        third.mkdir()

        run = tmp / "run"
        p = evalrun(plan_args(run), env, fx["dir"])
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}
        print("\n=== case-1330: the plan stores every input path absolute ===")
        check(p.returncode == 0, f"1330: plan from the planning folder exits 0 ({p.returncode}: {p.stderr[-300:]})")
        paths = stored_paths(plan)
        bad = {k: v for k, v in paths.items() if not (isinstance(v, str) and os.path.isabs(v) and Path(v).exists())}
        check(len(paths) == 6 and not bad, f"1330: registry, scope, candidates, settings and both arms' candidate paths "
                                           f"are absolute and exist ({len(paths)} paths; not so: {bad})")
        want = {"registry": fx["dir"] / "inputs" / "registry.json", "scope": fx["dir"] / "scope" / "scope.json",
                "candidatesDir": fx["dir"] / "candidates", "settings": fx["dir"] / "inputs" / "settings.json"}
        same = all(isinstance(paths.get(k), str) and os.path.isabs(paths[k]) and Path(paths[k]).resolve() == v.resolve()
                   for k, v in want.items())
        check(same, f"1330: each stored path names the file that was given ({paths})")

        print("\n=== case-1331: run from another folder ===")
        r = evalrun(["run", "--run", run], env, elsewhere)
        accepted = mao_accepted(run, plan)
        check(r.returncode == 0 and "-missing:" not in r.stderr and len(accepted) == len(plan.get("arms") or []) == 2,
              f"1331: run from another folder exits 0 and accepts masters-and-order on both arms "
              f"(exit {r.returncode}; accepted {accepted}; {r.stderr.strip()[-300:]!r})")

        print("\n=== case-1332: resume from a third folder ===")
        r = evalrun(["resume", "--run", run], env, third)
        check(r.returncode == 0 and "-missing:" not in r.stderr,
              f"1332: resume from a third folder exits 0 (exit {r.returncode}; {r.stderr.strip()[-300:]!r})")

        print("\n=== case-1333: an older plan with relative paths, run from elsewhere ===")
        old = tmp / "run-old"
        p = evalrun(plan_args(old), env, fx["dir"])
        relativise(old, fx["dir"])
        r = evalrun(["run", "--run", old], env, elsewhere)
        msg = r.stderr.strip()
        check(r.returncode == 2 and "relative to the folder it was planned from" in msg
              and str(Path("inputs") / "registry.json") in msg and "plan again" in msg
              and msg.count("relative to the folder it was planned from") == 1,
              f"1333: refused in one sentence naming the relative path and what to try (exit {r.returncode}; {msg[-400:]!r})")

        print("\n=== case-1334: control - that older plan from its own planning folder ===")
        r = evalrun(["run", "--run", old], env, fx["dir"])
        check(r.returncode == 0 and len(mao_accepted(old, json.loads((old / "plan.json").read_text(encoding="utf-8")))) == 2,
              f"1334: the older plan still runs from the folder it was planned in (exit {r.returncode}; {r.stderr.strip()[-300:]!r})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
