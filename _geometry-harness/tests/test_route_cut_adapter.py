"""The route-cut check reads each arm's candidate (cases 452-455).

    python _geometry-harness/tests/test_route_cut_adapter.py

blockprobe (geometry.route-cut) used to run on the baseline arm only, without --order, so it judged whatever load order
the selected profile held and a mod not installed there lost the check. evalrun now passes each arm's candidate and
the plan targets that arm loads; an arm that loads none is not-applicable. Drives the REAL evalrun.py with the shipped
registry's own check objects (trimmed to masters-and-order and route-cut) over the evalrun fixtures, with stand-ins for
scopeprobe and blockprobe that record their argv. No game, instance or real probe is needed.
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
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
FIXT = HERE / "fixtures" / "evalrun"
REGISTRY = REPO / ".claude" / "skills" / "world-edit-compatibility" / "references" / "check-registry.json"
KEEP = ["loadability.masters-and-order", "geometry.route-cut"]

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


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
    drop = dict(base, Name="candidate-02-notarget", Changes=["removed FixtureTarget.esp"],
                Plugins=[p for p in base["Plugins"] if p != "FixtureTarget.esp"])
    (cands / "candidate-02-notarget.json").write_text(json.dumps(drop, indent=1), encoding="utf-8")
    return {"registry": tmp / "registry.json", "scope": case / "scope.json", "cands": cands,
            "scenario": case / "scenario.json"}


def evalrun(args, env):
    return subprocess.run([sys.executable, str(EVALRUN)] + [str(a) for a in args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env, timeout=300)


def case_455(tmp: Path):
    """blockprobe takes several targets positionally and refuses an argument it does not know (it used to skip it)."""
    exe = HARNESS / "blockprobe" / "bin" / "Release" / "net9.0" / ("blockprobe.exe" if os.name == "nt" else "blockprobe")
    print("\n=== case-455: blockprobe's arguments ===")
    if not exe.exists():
        check(False, f"455: blockprobe is not built at {exe}; build it (dotnet build -c Release). Not a skip.")
        return
    inst, game = tmp / "inst", tmp / "game"
    (inst / "profiles" / "P").mkdir(parents=True)
    (inst / "mods").mkdir()
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    for f in ("modlist.txt", "plugins.txt", "loadorder.txt"):
        (inst / "profiles" / "P" / f).write_text("", encoding="utf-8")
    env = dict(os.environ, HOUSECARL_MO2_INSTANCE=str(inst), HOUSECARL_MO2_PROFILE="P", HOUSECARL_GAME_PATH=str(game),
               HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"))
    env.pop("HOUSECARL_HARNESS_CONFIG", None)

    def run(*args):
        r = subprocess.run([str(exe), *args], capture_output=True, text=True, encoding="utf-8", errors="replace",
                           env=env, timeout=120)
        return r.returncode, (r.stdout + r.stderr).strip()

    rc, out = run("A.esp", "--bogus")
    check(rc == 2 and "does not know the argument '--bogus'" in out, f"455: an unknown flag is refused, exit 2 ({rc}: {out[-160:]})")
    rc, out = run("A.esp", "--profile", "Other")
    check(rc == 2 and "does not take --profile" in out and "HOUSECARL_MO2_PROFILE" in out,
          f"455: --profile is refused with where the profile comes from ({rc}: {out[-160:]})")
    rc, out = run("A.esp", "B.esp")
    check(rc == 1 and "'A.esp' is not in the active load order" in out and "does not know" not in out,
          f"455: two positional targets are both taken as targets ({rc}: {out[-160:]})")


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-route-cut-"))
    try:
        fx = setup(tmp)
        argv_dir = tmp / "argv"
        env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(fx["scenario"]), EVALRUN_FAKE_ARGV_DIR=str(argv_dir),
                   HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"), PYTHONIOENCODING="utf-8")
        for k in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH"):
            env.pop(k, None)
        run = tmp / "run"
        cmds = []
        for p in ("scopeprobe", "blockprobe"):
            cmds += ["--probe-cmd", f"{p}={sys.executable} {FIXT / 'fakeprobes' / p}.py"]
        p = evalrun(["plan", "--registry", fx["registry"], "--scope", fx["scope"], "--candidates", fx["cands"],
                     "--out", run, "--harness", HARNESS, "--depth", "standard"] + cmds, env)
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}

        print("\n=== case-452: route-cut is planned on every arm ===")
        check(p.returncode == 0, f"452: plan exit {p.returncode} {p.stderr[-300:]}")
        rc = next((c for c in plan.get("checks") or [] if c.get("checkId") == "geometry.route-cut"), {})
        statuses = {a.get("armId"): a.get("status") for a in rc.get("arms") or rc.get("perArm") or []}
        check(statuses and all(s == "planned" for s in statuses.values()) and len(statuses) == 3,
              f"452: every arm planned, none unsupported-for-arm ({statuses})")

        r = evalrun(["run", "--run", run], env)
        receipts = {f.stem: json.loads(f.read_text(encoding="utf-8")) for f in (run / "receipts").glob("geometry.route-cut__*.json")}
        argvs = [json.loads(f.read_text(encoding="utf-8")) for f in argv_dir.glob("*.json")] if argv_dir.exists() else []

        print("\n=== case-453: each arm's blockprobe reads that arm's candidate and the targets it loads ===")
        arms = {a["armId"]: a for a in plan.get("arms") or []}
        for aid in ("baseline", "candidate-01-reorder"):
            want = str(arms.get(aid, {}).get("candidatePath"))
            hit = [a for a in argvs if "--order" in a and a[a.index("--order") + 1] == want]
            pos = [x for i, x in enumerate(hit[0]) if not x.startswith("--") and (i == 0 or hit[0][i - 1] != "--order")] if hit else []
            check(len(hit) == 1 and pos == ["FixtureTarget.esp"] and "--instance" not in hit[0],
                  f"453: {aid}: one blockprobe run with --order <its candidate> and the target ({hit})")
            ex = (receipts.get(f"geometry.route-cut__{aid}") or {}).get("result", {}).get("execution")
            check(ex == "completed", f"453: {aid}: the route-cut receipt is completed ({ex})")
        check(len(argvs) == 2, f"453: blockprobe ran twice, not on the arm without the target ({len(argvs)} runs)")

        print("\n=== case-454: an arm that loads no target is not-applicable, with the reason ===")
        rec = receipts.get("geometry.route-cut__candidate-02-notarget") or {}
        res = rec.get("result") or {}
        check(res.get("execution") == "not-applicable" and "loads none of the plan's target plugins" in (res.get("note") or ""),
              f"454: not-applicable with the sentence ({res.get('execution')}: {res.get('note')}; run exit {r.returncode})")
        case_455(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
