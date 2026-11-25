"""The AI-side evaluation's checks run end to end over SkyWright's controller suite (test_evalrun.py): the same generated
fixtures, fake probes and helpers, with the overlay planned, evaluated, finalized, bundled and verified by this module's
evalcontrol over the finished app run. These are the cases whose subject left the app's registry; each keeps the name it
had in test_evalrun.py, so the accounting finds it here by name.

`python integration/housecarl/tests/test_evalcontrol_run.py [case]` (SKYWRIGHT_CONFIG picks the build, else Release;
both SkyWright's build and this module's must be built). The oracles are the hand-derived values the app suite held for
these cases, moved unchanged into tests/fixtures/evalcontrol-run/; the record checks' cases (RECORD_CASES) are
evalrun_record_cases.py's, on the overlay.
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
import test_evalrun as ER  # noqa: E402
import module_fakes  # noqa: E402  the module's own probe stand-ins

ORACLES = HERE / "fixtures" / "evalcontrol-run"
EVALCONTROL = SW.tool("evalcontrol")
check, expect, load = ER.check, ER.expect, ER.load


def evalcontrol(args, cwd: Path | None = None):
    r = subprocess.run([str(EVALCONTROL)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=cwd, timeout=300)
    if r.returncode != 0:
        print(r.stdout[-2000:])
        print(r.stderr[-2000:])
    return r


def overlay(run: Path) -> Path:
    """The overlay over a finished, bundled app run: planned, evaluated and finalized under run/ai."""
    ai = run / "ai"
    for what, args in (("plan", ["plan", "--app", run, "--app-bundle", run / "bundle", "--out", ai, *module_fakes.probe_cmds()]),
                       ("evaluate", ["evaluate", "--run", ai]), ("finalize", ["finalize", "--run", ai])):
        r = evalcontrol(args, cwd=run)
        if r.returncode != 0:
            raise RuntimeError(f"evalcontrol {what} exit {r.returncode}: {r.stderr.strip()[:300]}")
    return ai


def case_10(tmp):
    """The media half of the app suite's case 10 (D8): supplied pictures are evidence only when their fingerprint is one
    an accepted receipt of the app scan measured on that arm, and the item names its provenance."""
    case = "case-10-discovery-media"
    o = load(ORACLES / f"{case}.json")
    run = tmp / case
    ER.do_plan(run, case)
    _, _, _, scen = ER.case_paths(case)
    ER.evalrun(["run", "--run", run], scen)
    ER.evalrun(["finalize", "--run", run], scen)
    ER.evalrun(["bundle", "--run", run], scen)
    # one media item whose fingerprint matches an accepted receipt, one that matches nothing
    fp = None
    cand_arm = None
    for r in sorted((run / "receipts").glob("*.json")):
        d = load(r)
        if not ((d.get("validation") or {}).get("accepted")):
            continue
        got = (((d.get("consumed") or {}).get("configuration") or {}).get("fingerprint"))
        if d.get("armId") == "baseline" and d.get("part") in (None, "arm"):
            fp = fp or got
        elif d.get("armId") != "baseline":
            cand_arm = cand_arm or d.get("armId")
    media_dir = run / "supplied"
    media_dir.mkdir(parents=True, exist_ok=True)
    for n in ("matched-shot.png", "old-shot.png", "wrong-arm-shot.png", "unsourced-shot.png"):
        (media_dir / n).write_bytes(b"\x89PNG\r\n\x1a\n" + n.encode())
    mediajson = run / "media.json"
    # media item schema 2 (bundle standard §8): kind, a described subject, state, the fingerprint and where
    # it came from, provenance. The first item's fingerprint is an accepted baseline receipt's; the second's
    # matches nothing.
    subject = {"checkId": "geometry.navmesh-ownership", "armId": "baseline",
               "description": "the contested navmesh cell from the fixture's suggested camera"}
    mediajson.write_text(json.dumps([
        {"path": str(media_dir / "matched-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-09-11T00:00:00Z"}},
        {"path": str(media_dir / "old-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": "deadbeef" * 8, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-05-01T00:00:00Z"}},
        # A-3's trap: a baseline-order picture declared for the CANDIDATE arm with the baseline fingerprint.
        # The candidate's reverse part consumed the baseline order, so its receipt carries this fingerprint;
        # the comparison set is the arm's OWN configuration, so this is a mismatch, not evidence.
        {"path": str(media_dir / "wrong-arm-shot.png"), "mediaKind": "native-screenshot",
         "subject": {**subject, "armId": cand_arm or "candidate-01"},
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted",
         "provenance": {"tool": "devbench", "capturedUtc": "2026-09-11T00:00:00Z"}},
        # a matching item that names no provenance: refused by that field, never accepted with provenance null
        {"path": str(media_dir / "unsourced-shot.png"), "mediaKind": "native-screenshot", "subject": subject,
         "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted"},
    ], indent=1), encoding="utf-8")
    ai = overlay(run)
    aib = ai / "bundle"
    evalcontrol(["bundle", "--run", ai, "--out", aib, "--media", mediajson], cwd=run)
    man = load(aib / "manifest.json") or {}
    unassessed = json.dumps(man.get("unassessedMedia", []))
    for n in o["disposition"]["unassessedMediaIncludes"]:
        check(n in unassessed, f"{case}: {n} is unassessedMedia, not evidence (D8)")
    for n in o["disposition"]["unassessedMediaExcludes"]:
        check(n not in unassessed, f"{case}: {n} is NOT unassessed (its fingerprint matched) (D8)")
    for n, reason in (o["disposition"].get("unassessedMediaReasons") or {}).items():
        rows = [u for u in man.get("unassessedMedia", []) if n in str(u.get("path"))]
        expect(rows[0].get("reason") if rows else None, reason,
               f"{case}: {n} is unassessed by ONE closed reason (D8, media item schema 2)")
    ev = json.dumps(man.get("evidence", []))
    check(o["disposition"]["mediaEvidenceStrength"] in ev,
          f"{case}: the matching media is evidence with strength supplied-media (D8)")
    rows = [e for e in man.get("evidence", []) if e.get("kind") == "media"]
    row = rows[0] if rows else {}
    digest = hashlib.sha256((media_dir / "matched-shot.png").read_bytes()).hexdigest()
    expect(row.get("path"), f"media/{digest[:12]}-matched-shot.png",
           f"{case}: the accepted media is copied to media/<sha256[:12]>-<basename> (D8)")
    for k in o["disposition"].get("mediaRowCarries") or []:
        check(k in row, f"{case}: the media row carries {k} (D8, media item schema 2)")
    check((aib / row.get("path", "missing")).exists(), f"{case}: the media copy is on disk (D8)")
    # new with the move: the overlay bundle verifies together with the app bundle it is bound to
    v = evalcontrol(["verify", aib], cwd=run)
    expect(v.returncode, 0, f"{case}: evalcontrol verify accepts the overlay with its media and the app bundle")


def c1933(tmp):
    """Ice Blade (the module's C1933, with its report-stage half in test_alpha_card_capture.py): the overlay's plan takes
    every pinned input from the app bundle's copies, never the originals, and an app plan pinning an input the overlay
    does not know is refused in one sentence. Wrong answers: the overlay plan names the app plan's scope path; an
    unknown pinned input is carried on unchecked."""
    case = "case-01-geometry-and-quest"
    run = tmp / "c1933"
    ER.full_run(run, case)
    ai = overlay(run)
    plan = load(ai / "plan.json") or {}
    scope = Path(((plan.get("scope") or {}).get("path")) or "")
    reg = Path(((plan.get("registry") or {}).get("path")) or "")
    check(scope.is_file() and scope.resolve().is_relative_to((run / "bundle").resolve()),
          "C1933: the overlay plan's scope is the app bundle's copy")
    check(reg.is_file() and reg.resolve().is_relative_to(ai.resolve()) and reg.name == "ai-checks.json",
          "C1933: the overlay plan's registry is its own list copy")
    # an app plan that pins an input the overlay does not know: the app run and its bundle carry the same plan
    pinned = tmp / "c1933-pinned"
    import shutil
    shutil.copytree(run, pinned, ignore=shutil.ignore_patterns("ai"))
    app_plan = load(pinned / "plan.json")
    app_plan["population"] = {"path": str(tmp / "gone" / "population.json"), "sha256": "0" * 64}
    body = json.dumps(app_plan, indent=1).encode("utf-8")
    for f in (pinned / "plan.json", pinned / "bundle" / "plan.json"):
        f.write_bytes(body)
    (pinned / "plan.sha256").write_text(hashlib.sha256(body).hexdigest(), encoding="utf-8")
    r = evalcontrol(["plan", "--app", pinned, "--app-bundle", pinned / "bundle", "--out", pinned / "ai", *module_fakes.probe_cmds()], cwd=pinned)
    said = (r.stderr + r.stdout).strip()
    expect(r.returncode, 2, "C1933: an app plan pinning an input the overlay does not know is refused (exit)")
    check("population" in said and said.count("\n") == 0,
          "C1933: the refusal is one sentence naming the input")
    if "population" not in said:
        print("    said: " + said[:300])


def plan_oracles(tmp):
    """The plan oracles the app suite held for the media row (cases 01 and 04, D1 / A-1 and D3 / A-2): planned over the
    app plan of each case, the overlay's runtime.supplied-observations applies (`always`) and is planned. Case 02's
    `skipped-user` for this row does not carry over: the overlay takes no --select, so an AI-side row is never skipped
    by the app's selection (raised for the reviewer, not homed here)."""
    oracle = {"applicability": "yes", "status": "planned"}
    for case, extra in (("case-01-geometry-and-quest", None),
                        ("case-04-blocked-and-budget", ["--max-candidates", "1", "--exclude",
                                                        "geometry.placement-contact=not-relevant-to-this-review"])):
        run = tmp / f"oracle-{case}"
        ER.full_run(run, case, plan_extra=extra)
        plan = load(overlay(run) / "plan.json") or {}
        ER.assert_plan(plan, {"plan": {"checks": {"runtime.supplied-observations": oracle}}}, case)


RECORD_IDS = ("loadability.dangling-references", "records.cell-attributes")
RECORD_EXTRAS = {"case-02-select-three-probes": ["--select", "navownprobe,overlapprobe,contactprobe"],
                 "case-04-blocked-and-budget": ["--max-candidates", "1", "--exclude",
                                                "geometry.placement-contact=not-relevant-to-this-review"]}


def record_oracles(tmp):
    """The oracles the app suite held for its trim's two record rows (fixtures/evalrun/expected, before 9B; rules 1-3 of
    work/rerecord_evalrun_oracles_9b.py took them out), under the same labels, on the overlay over each case's app run:
    plan (cases 01, 02, 04: applicability yes, status mcp-only; the overlay takes no --select, and an mcp-only row is
    planned mcp-only under the app's --select as before), ledger (case 01's baseline: execution mcp-only) and missing
    (every case whose oracle listed them: required and never completable without an import). Each case's app run is
    test_evalrun's full_run with that case's plan arguments (cases 08 and 10 included: their own flows after the plan
    do not touch the overlay's rows)."""
    plan_oracle = {"applicability": "yes", "status": "mcp-only"}
    for case in ("case-01-geometry-and-quest", "case-02-select-three-probes", "case-03-null-and-extraction",
                 "case-04-blocked-and-budget", "case-05-rejections", "case-06-attribution", "case-07-combined-regression",
                 "case-08-interrupt-resume", "case-10-discovery-media"):
        run = tmp / f"records-{case}"
        ER.full_run(run, case, plan_extra=RECORD_EXTRAS.get(case))
        ai = overlay(run)
        if case in ("case-01-geometry-and-quest", "case-02-select-three-probes", "case-04-blocked-and-budget"):
            ER.assert_plan(load(ai / "plan.json") or {}, {"plan": {"checks": {i: plan_oracle for i in RECORD_IDS}}}, case)
        if case == "case-01-geometry-and-quest":
            ER.assert_ledger(load(ai / "ledger.json") or {},
                             {"ledger": {f"{i}__baseline": {"execution": "mcp-only", "detector": None, "rejection": None}
                                         for i in RECORD_IDS}}, case)
        ER.assert_disposition(load(ai / "disposition.json") or {}, {"disposition": {"missingIncludes": list(RECORD_IDS)}}, case)


ADVICE = "patch.author-advice"


def advice_oracles(tmp):
    """The oracles the app suite held for its trim's patch.author-advice (fixtures/evalrun/expected, before 9D-1), under
    the same labels, on the overlay over each case's app run: plan (cases 01, 02, 04: applicability yes - the fixture
    scope records a Nexus id -, status planned) and ledger (case 01's baseline: completed, a catalogue). Case 02's
    "patch.author-advice is listed under missing (D7)" does not carry over: it was missing because the app's --select
    skipped it, and the overlay takes no --select (raised for the reviewer, not homed here, as plan_oracles says for the
    media row)."""
    for case in ("case-01-geometry-and-quest", "case-02-select-three-probes", "case-04-blocked-and-budget"):
        run = tmp / f"advice-{case}"
        ER.full_run(run, case, plan_extra=RECORD_EXTRAS.get(case))
        ai = overlay(run)
        ER.assert_plan(load(ai / "plan.json") or {}, {"plan": {"checks": {ADVICE: {"applicability": "yes", "status": "planned"}}}}, case)
        if case == "case-01-geometry-and-quest":
            ER.assert_ledger(load(ai / "ledger.json") or {},
                             {"ledger": {f"{ADVICE}__baseline": {"execution": "completed", "detector": "catalogue", "rejection": None}}}, case)


def case_803(tmp):
    """The report's author advice (evalreport reads the disposition's discovery block: each advice row's rules and
    stated clashes) on a joined bundle. Since patch.author-advice left the app's registry its receipts and the advice
    rows are the overlay's: the overlay's finalize writes the discovery block, and evalreport's Bundle._join must carry
    it. The wrong answer it rejects: the joined bundle keeps the app's block, whose advice is empty, and the report
    silently states no advice."""
    import importlib.util
    case = "case-803-report-reads-overlay-advice"
    run = tmp / case
    ER.full_run(run, "case-01-geometry-and-quest")
    ai = overlay(run)
    aib = ai / "bundle"
    r = evalcontrol(["bundle", "--run", ai, "--out", aib], cwd=run)
    expect(r.returncode, 0, f"{case}: premise - the overlay bundles")
    rows = [(a.get("checkId"), a.get("armId")) for a in ((load(ai / "disposition.json") or {}).get("discovery") or {}).get("advice") or []]
    expect(rows, [("patch.author-advice", "baseline")], f"{case}: premise - the overlay's disposition carries the advice of its adviceprobe receipt")
    spec = importlib.util.spec_from_file_location("evalreport_case_803", SW.MODULE / "evalreport.py")
    er = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(er)
    app = run / "bundle"
    er.AI_BUNDLES[str(app.resolve())] = aib.resolve()
    joined = er.Bundle(app)
    check(joined.overlay is not None, f"{case}: premise - the app bundle is read joined with its overlay")
    got = [(a.get("checkId"), a.get("armId")) for a in (joined.disposition.get("discovery") or {}).get("advice") or []]
    expect(got, [("patch.author-advice", "baseline")], f"{case}: the joined bundle's discovery advice is the overlay's", "author advice")
    er.AI_BUNDLES.clear()
    alone = (er.Bundle(app).disposition.get("discovery") or {}).get("advice")
    expect(alone, [], f"{case}: the app bundle read alone holds no advice (the premise of the overlay read)", "author advice")


CASES = {"case-10": case_10, "C1933": c1933, "plan-oracles": plan_oracles, "record-oracles": record_oracles,
         "case-803": case_803, "advice-oracles": advice_oracles}


# ------------------------------------------------------------------ the record checks (9B)
# The app suite's cases about importing a record check's evidence (17, 42, 43, 44, 65, and case 18's import half) and
# about the rows derived from an import (61, 800) moved here with the record checks (evalrun_record_cases.py), on the
# AI-side overlay; they run under their names over the app suite's engine.
RECORD_CASES = ["case-17", "case-18", "case-42", "case-43", "case-44", "case-61", "case-65", "case-800", "case-802"]


def run_record_cases(argv) -> int:
    import evalrun_record_cases as RC
    # a second ER.main in this process makes its own temporary folder: the conflictdiff the first one built is gone
    # with its folder, so this run's conflictdiff is chosen again
    ER.DIFF_SOURCE = None
    return ER.main({k: getattr(RC, "case_" + k.split("-")[1]) for k in RECORD_CASES}, argv)


if __name__ == "__main__":
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    if not EVALCONTROL.is_file():
        print(f"  FAIL  evalcontrol is not built at {EVALCONTROL}: {SW.why_no_build() or 'build the module (scripts/build.ps1)'}")
        raise SystemExit(1)
    argv = sys.argv[1:]
    want = argv[0] if argv else ""
    def wanted(keys):
        return not want or any(k.startswith(want) or want.startswith(k) for k in keys)
    rc = 0 if wanted(RECORD_CASES) and not wanted(CASES) else ER.main(CASES, argv)
    if wanted(RECORD_CASES):
        rc = max(rc, run_record_cases(argv))
    raise SystemExit(rc)
