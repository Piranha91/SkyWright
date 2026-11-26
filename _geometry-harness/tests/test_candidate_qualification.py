"""Known answers for patch.candidate-qualification once patch discovery is not the app's (step 3 of the SkyWright /
houseCARL boundary): the row qualifies the patch files an arm supplies by their bytes, needs no discovery, and on a plan
whose arms supply no patch file it says so and is not-applicable, never "no finding" for something it did not look at.

    python tests/test_candidate_qualification.py

    Q1  the app's registry gives patch.candidate-qualification one prerequisite, patch.order-evaluation, and holds no
        patch.discovery-installed row
    Q2  case-19's fixture evaluation (no arm supplies a patch file), planned on a registry whose candidate-qualification
        row is the app registry's own: every arm's row is not-applicable with the sentence "no arm of this plan supplies
        a patch file, so there is no payload to qualify", in neither completedNullChecks nor missing
    Q3  case-16's fixture evaluation (one arm supplies a patch file), on the same registry: the row completes on every
        arm, and its derived candidates name the supplied file with a qualification (qualified or unqualified, by bytes)

Builds the fixtures as tests/test_evalrun.py does (its helpers, its fake probes, its stand-in record reader), each run
in a temporary folder; runcontrol is that suite's controller (HOUSECARL_RUNCONTROL, else this checkout's build).
Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.argv = sys.argv[:1]
import test_evalrun as E  # noqa: E402

QUAL, ORDER, DISC = "patch.candidate-qualification", "patch.order-evaluation", "patch.discovery-installed"
NO_PAYLOAD = "no arm of this plan supplies a patch file, so there is no payload to qualify"
REAL = HERE.parent.parent / "data" / "check-registry.json"
failed = 0


def check(name: str, ok: bool, detail: str) -> None:
    global failed
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    failed += 0 if ok else 1


def registry_with_app_row(tmp: Path) -> Path:
    """The case-19 trim with its candidate-qualification row replaced by the app registry's own, and the discovery row
    left out when the app registry has none."""
    real = json.loads(REAL.read_text(encoding="utf-8"))
    row = next(c for c in real["checks"] if c["id"] == QUAL)
    reg = json.loads((E.GEN / "registry" / "fixture-check-registry-derived.json").read_text(encoding="utf-8"))
    has_disc = any(c["id"] == DISC for c in real["checks"])
    reg["checks"] = [row if c["id"] == QUAL else c for c in reg["checks"] if has_disc or c["id"] != DISC]
    out = E.GEN / "registry" / "fixture-check-registry-app-qualification.json"
    out.write_text(json.dumps(reg, indent=1), encoding="utf-8")
    return out


def run_case(tmp: Path, case: str, tag: str, extra: list) -> tuple[dict, dict, dict]:
    run = tmp / f"{case}-{tag}"
    _, _, _, scen = E.case_paths(case)
    p = E.do_plan(run, case, ["--select", QUAL] + extra, registry="fixture-check-registry-app-qualification.json")
    if p.returncode != 0:
        return {"refused": p.stderr.strip()[-300:]}, {}, {}
    E.evalrun(["run", "--run", run], scen)
    E.evalrun(["finalize", "--run", run], scen)
    return (E.load(run / "plan.json") or {}, E.ledger_entries(E.load(run / "ledger.json") or {}),
            E.load(run / "disposition.json") or {})


def main() -> int:
    real = json.loads(REAL.read_text(encoding="utf-8"))
    row = next((c for c in real["checks"] if c["id"] == QUAL), {})
    check("Q1 candidate qualification's one prerequisite is the order evaluation, and the app has no discovery row",
          row.get("prerequisites") == [ORDER] and not any(c["id"] == DISC for c in real["checks"]),
          f"prerequisites {row.get('prerequisites')}; discovery row present: {any(c['id'] == DISC for c in real['checks'])}")
    with tempfile.TemporaryDirectory(prefix="sw-candidate-qualification-") as t:
        t = Path(t)
        E.pin_standard_settings(t)
        E.GEN = t / "fixtures"
        gen = subprocess.run([sys.executable, str(E.FIXT / "make_fixtures.py"), "--out", str(E.GEN)], capture_output=True, text=True)
        if gen.returncode != 0:
            check("fixtures", False, (gen.stdout + gen.stderr)[-400:])
            return 1
        os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(t / "runlog"))
        os.environ["HOUSECARL_RECORD_READER_CMD"] = f"{sys.executable} {E.FIXT / 'fake_records_mcp.py'}"
        E.use_conflictdiff(t)
        registry_with_app_row(t)

        plan, led, disp = run_case(t, "case-19-derived-prerequisites", "no-supplied", [])
        arms = [a["armId"] for a in plan.get("arms") or []]
        ev = disp.get("evaluation") or {}
        nulls = {(r.get("checkId"), r.get("armId")) for r in ev.get("completedNullChecks") or []}
        miss = {(r.get("checkId"), r.get("armId")) for r in ev.get("missing") or []}
        # the sentence of an accounted row is its receipt's result.note (Executor.Account), as the order evaluation's
        # baseline sentence is; the ledger entry carries a detail only for a stopped or uncontained row
        rec = lambda a: E.load(t / "case-19-derived-prerequisites-no-supplied" / "receipts" / f"{QUAL}__{a}.json") or {}
        got = {a: (E.execution_of(led.get(f"{QUAL}__{a}") or {}), str((rec(a).get("result") or {}).get("note"))) for a in arms}
        check("Q2 with no supplied patch file the row is not-applicable on every arm, with its sentence",
              bool(arms) and all(s == "not-applicable" and NO_PAYLOAD in text for s, text in got.values()),
              f"{plan.get('refused') or {a: (s, text[:200]) for a, (s, text) in got.items()}}")
        check("Q2 ...and it is neither a completed null check nor missing",
              bool(arms) and not any((QUAL, a) in nulls or (QUAL, a) in miss for a in arms),
              f"null on {[a for a in arms if (QUAL, a) in nulls]}; missing on {[a for a in arms if (QUAL, a) in miss]}")

        case16 = "case-16-supplied-identity"
        o = E.load(E.EXPECTED / f"{case16}.json")
        d = E.GEN / "cases" / case16
        extra = ["--supplied", f"{o['fixture']['suppliedPlugin']}={d / 'supplied' / 'candidate-02-supplied.json'}",
                 "--instance", E.GEN / "instance", "--profile", "fixture", "--game", E.GEN / "game"]
        plan, led, disp = run_case(t, case16, "supplied", extra)
        arms = [a["armId"] for a in plan.get("arms") or []]
        st = {a: E.execution_of(led.get(f"{QUAL}__{a}") or {}) for a in arms}
        rows = []
        for f in (t / f"{case16}-supplied" / "receipts").glob(f"{QUAL}__*.json"):
            rows += ((E.load(f) or {}).get("derived") or {}).get("candidates") or []
        sup = [r for r in rows if r.get("file") == o["fixture"]["suppliedPlugin"]]
        check("Q3 with a supplied patch file the row completes on every arm and qualifies the file by its bytes",
              bool(arms) and all(s == "completed" for s in st.values()) and bool(sup)
              and all(r.get("qualification") in ("qualified", "unqualified") and r.get("why") for r in sup),
              f"{plan.get('refused') or st}; supplied rows {sup[:2]}")
    print(f"\n{failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
