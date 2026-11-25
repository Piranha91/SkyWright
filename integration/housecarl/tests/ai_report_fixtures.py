"""The AI-side half of the report fixtures: what an AI-side evaluation adds to an app bundle the app-side maker
(_geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py) made. Passed to its generate() as ai_hook.

R5's supplied media goes to an overlay (evalcontrol, this module's controller of the AI-side rows): planned over R5's
app run, evaluated, finalized, bundled with --media beside the app bundle, and verified with it. The overlay run is
RUN/ai, its bundle RUN/ai-bundle (bound to RUN/bundle as ../bundle), recorded in the case as aiBundle.

R1 (UNANSWERED_OVERLAY_CASES) gets the same overlay with nothing supplied: its AI-side rows (the record and
dangling-reference rows among them) are left unanswered, so the joined report must list each as a missing finding.

R7 (imported evidence: where it came from) is made here whole, since its subject is an import: the app run (a small
derived-registry setup, masters-and-order only, its last arm refused) is planned, run, finalized and bundled by
SkyWright's controller; the overlay over it imports one record result fresh on the first arm and one reused, declaring
IMPORT_REUSED_FROM as its source and reporting 2 findings, on the second; the third arm gets no import (the pre-origin
receipt it once carried had only the Python controller as its writer) and the fourth's prerequisite is refused, so
nothing is credited there. After the resume (evalcontrol evaluate) the credited imports' ledger entries read `reused`
(the controller re-using this run's own receipts), the status a report must not describe as reused evidence.
"""
from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
import sys
sys.path.insert(0, str(HERE.parent))
import skywright_paths as SW  # noqa: E402
import evalrun_record_cases as RC  # noqa: E402  the record cases' helpers over test_evalrun's engine

IMPORT_CASE = "R7-import-origin"
IMPORT_STEMS = {
    # every arm but the last loads; the last one's masters come after its dependant, so masters-and-order refuses it
    "candidate-00-baseline": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
    "candidate-01-reorder": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"],
    "candidate-02-legacy": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
    "candidate-03-missing-master": ["Update.esm", "Skyrim.esm", "FixtureTarget.esp"],
}
# the source another evaluation is declared to have captured the reused result for (never this plan)
IMPORT_REUSED_FROM = {"planId": "0123456789ab", "armId": "candidate-01-earlier", "resultSha256": "ab" * 32}


def evalcontrol(*args) -> subprocess.CompletedProcess:
    return subprocess.run([str(SW.tool("evalcontrol")), *map(str, args)], capture_output=True, text=True, encoding="utf-8",
                          timeout=600)


def _must(r, what: str):
    if r.returncode != 0:
        raise SystemExit(f"{IMPORT_CASE}: {what} exited {r.returncode}\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
    return r


def make_import_origin_case(root: Path) -> dict:
    T = RC.ER
    case = IMPORT_CASE
    _, scen = RC.import_case_dir(case, IMPORT_STEMS, refuse=("candidate-03-missing-master",))
    run = Path(root) / "runs" / case
    if run.exists():
        shutil.rmtree(run)
    scope_fake = T.GEN / "cases" / case / "scopeprobe_fake.py"
    scope_fake.write_text(T.SCOPE_FAKE_20, encoding="utf-8")
    exits = {}
    exits["plan"] = _must(T.do_plan(run, case, ["--select", T.MAO, "--probe-cmd", f"scopeprobe=python {scope_fake}"],
                                    registry="fixture-check-registry-derived.json"), "the controller's plan").returncode
    exits["run"] = T.evalrun(["run", "--run", run], scen).returncode
    exits["finalize"] = _must(T.evalrun(["finalize", "--run", run], scen), "the controller's finalize").returncode
    exits["bundle"] = _must(T.evalrun(["bundle", "--run", run, "--no-names"], scen), "the controller's bundle").returncode
    app_bundle, ai, ai_bundle = run / "bundle", run / "ai", run / "ai-bundle"
    _must(evalcontrol("plan", "--app", run, "--app-bundle", app_bundle, "--out", ai), "evalcontrol plan")
    _must(evalcontrol("evaluate", "--run", ai), "evalcontrol evaluate")
    plan = T.load(ai / "plan.json")
    arms = {stem: RC.arm_by_stem(plan, stem) for stem in IMPORT_STEMS}
    work = Path(root) / "imports" / case
    envelopes = {
        "fresh": (arms["candidate-00-baseline"], {}),
        "reused": (arms["candidate-01-reorder"], {"origin": "reused", "reused_from": IMPORT_REUSED_FROM}),
    }
    for tag, (arm, kw) in envelopes.items():
        manifest, doc = RC.origin_import(work / tag, plan, arm, **kw)
        if tag == "reused":
            # the reused import reports findings, so the reader's finding sentence carries the qualification too
            doc["result"] = {"detector": "finding", "findings": 2,
                             "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]}
            RC.write_doc(manifest, doc)
        exits[f"import-{tag}"] = _must(evalcontrol("import", "--run", ai, "--file", manifest),
                                       f"the {tag} import").returncode
    for name, argv in (("resume", ["evaluate", "--run", ai]), ("ai-finalize", ["finalize", "--run", ai]),
                       ("ai-bundle", ["bundle", "--run", ai, "--out", ai_bundle]), ("verify", ["verify", ai_bundle])):
        exits[name] = _must(evalcontrol(*argv), f"evalcontrol {argv[0]}").returncode
    (ai_bundle / "verify.json").unlink(missing_ok=True)
    (ai_bundle / "verify-app.json").unlink(missing_ok=True)
    return {"run": str(run), "bundle": str(app_bundle), "aiBundle": str(ai_bundle), "fixtureCase": case, "exits": exits,
            "arms": {stem: a["armId"] for stem, a in arms.items()}, "importCheck": T.IMPORT_CHECK,
            "prerequisite": T.MAO, "reusedFrom": IMPORT_REUSED_FROM, "planId": plan["planId"]}


#: cases whose app bundle gets an overlay with nothing supplied: its AI-side rows stay unanswered, so the joined report
#: lists them as missing (R1: the record and dangling-reference rows R1's report listed before they left the app)
UNANSWERED_OVERLAY_CASES = ("R1-intact-incomplete",)


def add_overlays(root: Path, index: dict, only: str | None = None) -> None:
    for case, entry in index.items():
        if not isinstance(entry, dict) or not (entry.get("media") or case in UNANSWERED_OVERLAY_CASES):
            continue
        run = Path(entry["run"])
        ai, ai_bundle = run / "ai", run / "ai-bundle"
        steps = [("plan", ["plan", "--app", run, "--app-bundle", entry["bundle"], "--out", ai]),
                 ("evaluate", ["evaluate", "--run", ai]),
                 ("finalize", ["finalize", "--run", ai]),
                 ("bundle", ["bundle", "--run", ai, "--out", ai_bundle, *(["--media", entry["media"]] if entry.get("media") else [])]),
                 ("verify", ["verify", ai_bundle])]
        for name, argv in steps:
            r = evalcontrol(*argv)
            if r.returncode != 0:
                raise SystemExit(f"{case}: evalcontrol {name} exited {r.returncode}\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
        (ai_bundle / "verify.json").unlink(missing_ok=True)
        (ai_bundle / "verify-app.json").unlink(missing_ok=True)
        entry["aiBundle"] = str(ai_bundle)
        print(f"  {case}: AI-side overlay bundle at {ai_bundle}")
    if not only or only.startswith("R7"):
        index[IMPORT_CASE] = make_import_origin_case(root)
        print(f"  {IMPORT_CASE}: app bundle at {index[IMPORT_CASE]['bundle']}, overlay at {index[IMPORT_CASE]['aiBundle']}")
