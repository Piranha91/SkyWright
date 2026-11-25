"""The controller suite's record cases (test_evalrun.py's cases 17, 18, 42, 43, 44, 61, 65 and 800 and the helpers only
they used), moved here when the record checks left SkyWright's registry for this module (step 3 of the SkyWright /
houseCARL boundary, 9B). They run over test_evalrun.py's engine (its fixtures, fake probes and helpers, reached through
the star import below), on the AI-side overlay: each case's app run is planned, run, finalized and bundled by
SkyWright's controller as before, `to_overlay` plans and evaluates the overlay over it with this module's evalcontrol,
and every later step on the overlay (an import, a resume, a finalize, a bundle, a verify) is evalcontrol's, through
`evalrun` below. test_evalcontrol_run.py runs them under their names; test_records_keep_work.py uses their helpers.
"""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is
sys.path.append(str(SW.HARNESS / "tests"))
import test_evalrun as ER  # noqa: E402
from test_evalrun import *  # noqa: E402,F401,F403  the suite's engine: fixtures, fake probes, helpers

EVALCONTROL = SW.tool("evalcontrol")
_APP = ER.evalrun
#: app plan id -> the app bundle an overlay was planned over (a relocated overlay bundle is verified against it)
APP_BUNDLES: dict[str, Path] = {}


def _is_overlay_run(p) -> bool:
    plan = ER.load(Path(p) / "plan.json") if p else None
    return isinstance(plan, dict) and "overlay" in plan


def _is_overlay_bundle(p) -> bool:
    m = ER.load(Path(p) / "manifest.json") if p else None
    return isinstance(m, dict) and "overlay" in m


def _evalcontrol(args, cwd=None, timeout=300):
    return subprocess.run([str(EVALCONTROL)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", cwd=cwd, timeout=timeout)


def evalrun(args, scenario: Path | None = None, expect_exit=None, timeout=300, cwd: Path | None = None):
    """test_evalrun's controller call; on an overlay (its run folder or its bundle) the step is evalcontrol's:
    import-results -> import, run and resume -> evaluate (what is kept is re-checked and reused), finalize, bundle
    (--out, else RUN/bundle), verify-bundle -> verify (against the app bundle the overlay was planned over), and an
    enrich of an overlay bundle -> the module's recordenrich.py."""
    a = [str(x) for x in args]
    verb = a[0] if a else ""
    run = a[a.index("--run") + 1] if "--run" in a else None
    if run and _is_overlay_run(run):
        if verb == "import-results":
            r = _evalcontrol(["import", "--run", run, "--file", a[a.index("--file") + 1]], cwd, timeout)
        elif verb in ("run", "resume"):
            r = _evalcontrol(["evaluate", "--run", run, *(["--rebuild-ledger"] if "--rebuild-ledger" in a else [])], cwd, timeout)
        elif verb == "finalize":
            r = _evalcontrol(["finalize", "--run", run], cwd, timeout)
        elif verb == "bundle":
            out = a[a.index("--out") + 1] if "--out" in a else str(Path(run) / "bundle")
            r = _evalcontrol(["bundle", "--run", run, "--out", out], cwd, timeout)
        else:
            raise AssertionError(f"no evalcontrol step for {verb} on an overlay")
    elif verb == "enrich" and "--bundle" in a and _is_overlay_bundle(a[a.index("--bundle") + 1]):
        # the rows derived from a record import: the module's recordenrich, on the overlay bundle
        r = subprocess.run([sys.executable, str(SW.MODULE / "recordenrich.py"), "--bundle", a[a.index("--bundle") + 1],
                            "--kind", a[a.index("--kind") + 1]], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", cwd=cwd, timeout=timeout)
    elif verb == "verify-bundle" and _is_overlay_bundle(a[1]):
        app = APP_BUNDLES.get(str(((ER.load(Path(a[1]) / "manifest.json") or {}).get("overlay") or {}).get("appPlanId")))
        extra = ["--app", app] if app else []
        r = _evalcontrol(["verify", a[1], *extra, *(["--out", a[a.index("--out") + 1]] if "--out" in a else [])], cwd, timeout)
    else:
        return _APP(args, scenario, expect_exit=expect_exit, timeout=timeout, cwd=cwd)
    if expect_exit is not None and r.returncode != expect_exit:
        print(r.stdout[-3000:])
        print(r.stderr[-2000:])
    return r


def to_overlay(run: Path, scen: Path | None = None, tag: str = "overlay", name: str = "ai") -> Path:
    """The app run finalized and bundled (RUN/<name>-app-bundle, no names step) by SkyWright's controller, and the
    AI-side overlay planned over it and evaluated by evalcontrol (RUN/<name>). Returns the overlay's run folder."""
    run = Path(run)
    ai, app = run / name, run / f"{name}-app-bundle"
    ER.expect(_APP(["finalize", "--run", run], scen).returncode, 0, f"{tag}: the app run finalizes", "setup")
    ER.expect(_APP(["bundle", "--run", run, "--out", app, "--no-names"], scen).returncode, 0, f"{tag}: the app run bundles", "setup")
    for step in (["plan", "--app", run, "--app-bundle", app, "--out", ai], ["evaluate", "--run", ai]):
        r = _evalcontrol(step)
        ER.expect(r.returncode, 0, f"{tag}: evalcontrol {step[0]} ({r.stderr.strip()[-200:]})", "setup")
    APP_BUNDLES[str((ER.load(run / "plan.json") or {}).get("planId"))] = app
    return ai


def recorded_bundle(tmp: Path, tag: str, bundle_args=("--no-names",)):
    """test_evalrun's recorded_bundle on the overlay: case-01's evaluation by SkyWright's controller, the overlay over
    it with one record import credited on the first arm its record check leaves open, finalized and bundled by
    evalcontrol (bundle_args are the app suite's names-step choices and are not the overlay's)."""
    case = "case-01-geometry-and-quest"
    run = tmp / f"{tag}-run"
    expect(do_plan(run, case).returncode, 0, f"{tag}: plan exit", "setup")
    _, _, _, scen = case_paths(case)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{tag}: accounting run", "setup")
    run = to_overlay(run, scen, tag)
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((c for c in ("records.cell-attributes", "records.contested-fields", "loadability.dangling-references")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, f"{tag}: fixture registry has an mcp-only record check")
    arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or []) if x.get("status") == "mcp-only"]
    arm = next(a for a in plan.get("arms") or [] if a.get("armId") in arm_ids)
    m, names = record_import_doc(tmp / f"{tag}-capture", plan, cid, arm)
    expect(evalrun(["import-results", "--run", run, "--file", m]).returncode, 0, f"{tag}: record import", "setup")
    expect(evalrun(["finalize", "--run", run]).returncode, 0, f"{tag}: finalize", "setup")
    bundle = tmp / f"{tag}-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0, f"{tag}: bundle", "setup")
    return bundle, plan, cid, arm, names


# ---- moved from test_evalrun.py (begin)
def case_17(tmp):
    """A generic MCP import is identity-bound, evidence-backed and relocatable."""
    case = "case-01-geometry-and-quest"
    run = tmp / "case-17-mcp-import"
    expect(do_plan(run, case).returncode, 0, "case-17: plan exit", "import")
    _, _, _, scen = case_paths(case)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, "case-17: accounting run", "import")
    run = to_overlay(run, scen, "case-17")
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((c for c in ("loadability.dangling-references", "loadability.missing-assets",
                            "records.contested-fields", "records.cell-attributes")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, "case-17: fixture registry contains an mcp-only check")
    planned_arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or [])]
    arms = [a for a in (plan.get("arms") or []) if a.get("armId") in planned_arm_ids]
    check(len(arms) >= 2, "case-17: mcp-only check applies to two arms")

    def make_import(folder, arm, detector="no-finding", findings=0):
        folder.mkdir(parents=True, exist_ok=True)
        request, response = folder / "request.json", folder / "response.json"
        request.write_text(json.dumps(initialize(f"evalrun:{plan['planId']}:{arm['armId']}")) + "\n"
                           + json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}) + "\n",
                           encoding="utf-8")
        response.write_text(json.dumps({"findings": findings}), encoding="utf-8")
        req = arm["requested"]
        cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
               "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                           for p in req["plugins"]],
               "providers": req["providers"], "overlays": req["overlays"],
               "population": req["population"], "state": "baseline"}
        cfg["fingerprint"] = import_fingerprint(cfg)
        doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid,
               "armId": arm["armId"], "state": "baseline", "origin": "fresh", "capturedUtc": datetime_now_z(),
               "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
               "result": {"detector": detector, "findings": findings,
                          "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
               "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                           for role, p in (("request", request), ("response", response))]}
        manifest = folder / "import.json"
        manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
        return manifest, doc

    manifest, _ = make_import(tmp / "case-17-good-null", arms[0])
    r = evalrun(["import-results", "--run", run, "--file", manifest])
    expect(r.returncode, 0, "case-17: accepted no-finding import", "import")
    imported = load(run / "receipts" / f"{cid}__{arms[0]['armId']}.json") or {}
    check((imported.get("validation") or {}).get("accepted") is True,
          "case-17: imported receipt accepted")
    expect((imported.get("result") or {}).get("detector"), "no-finding",
           "case-17: null conclusion retained", "import")
    expect(len(imported.get("outputs") or []), 3,
           "case-17: manifest plus raw request and response retained", "import")

    # Missing raw response, a foreign arm and a plugin/fingerprint mismatch all refuse before a receipt changes.
    missing, missing_doc = make_import(tmp / "case-17-missing", arms[1], "finding", 2)
    (missing.parent / "response.json").unlink()
    expect(evalrun(["import-results", "--run", run, "--file", missing]).returncode, 2,
           "case-17: missing raw response refuses", "import")
    wrong_arm, wrong_arm_doc = make_import(tmp / "case-17-wrong-arm", arms[1], "finding", 2)
    wrong_arm_doc["armId"] = "foreign-arm"
    wrong_arm.write_text(json.dumps(wrong_arm_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_arm]).returncode, 2,
           "case-17: foreign arm refuses", "import")
    wrong_order, wrong_order_doc = make_import(tmp / "case-17-wrong-order", arms[1], "finding", 2)
    wrong_order_doc["configuration"]["plugins"] = list(
        reversed(wrong_order_doc["configuration"]["plugins"]))
    wrong_order_doc["configuration"]["fingerprint"] = import_fingerprint(
        wrong_order_doc["configuration"])
    wrong_order.write_text(json.dumps(wrong_order_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_order]).returncode, 2,
           "case-17: wrong plugin order refuses", "import")
    wrong_hash, wrong_hash_doc = make_import(tmp / "case-17-wrong-hash", arms[1], "finding", 2)
    wrong_hash_doc["configuration"]["plugins"][0]["sha256"] = "0" * 64
    wrong_hash_doc["configuration"]["fingerprint"] = import_fingerprint(wrong_hash_doc["configuration"])
    wrong_hash.write_text(json.dumps(wrong_hash_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", wrong_hash]).returncode, 2,
           "case-17: wrong plugin bytes refuse", "import")
    stale, stale_doc = make_import(tmp / "case-17-stale-output", arms[1], "finding", 2)
    stale_doc["outputs"][1]["sha256"] = "0" * 64
    stale.write_text(json.dumps(stale_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", stale]).returncode, 2,
           "case-17: stale raw response hash refuses", "import")
    malformed, malformed_doc = make_import(tmp / "case-17-malformed-coverage", arms[1], "finding", 2)
    malformed_doc["result"]["coverage"] = [{"Population": "records", "Judged": -1,
                                                "Unjudgeable": {"parse": 0.5}}]
    malformed.write_text(json.dumps(malformed_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", malformed]).returncode, 2,
           "case-17: malformed coverage counts refuse", "import")
    zero, zero_doc = make_import(tmp / "case-17-zero-coverage", arms[1])
    zero_doc["result"]["coverage"][0]["Judged"] = 0
    zero.write_text(json.dumps(zero_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", zero]).returncode, 2,
           "case-17: zero-assessed evidence refuses", "import")
    no_population, no_population_doc = make_import(tmp / "case-17-no-population", arms[1])
    no_population_doc["result"]["coverage"][0]["Population"] = ""
    no_population.write_text(json.dumps(no_population_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", no_population]).returncode, 2,
           "case-17: coverage without a population refuses", "import")
    bad_path, bad_path_doc = make_import(tmp / "case-17-bad-path", arms[1])
    bad_path_doc["outputs"][0]["path"] = {"not": "a path"}
    bad_path.write_text(json.dumps(bad_path_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", bad_path]).returncode, 2,
           "case-17: malformed output path refuses without crashing", "import")
    bad_config, bad_config_doc = make_import(tmp / "case-17-bad-config", arms[1])
    bad_config_doc["configuration"] = ["not", "an", "object"]
    bad_config.write_text(json.dumps(bad_config_doc), encoding="utf-8")
    expect(evalrun(["import-results", "--run", run, "--file", bad_config]).returncode, 2,
           "case-17: malformed configuration refuses without crashing", "import")
    pending_key = f"{cid}__{arms[1]['armId']}"
    expect(((load(run / "ledger.json") or {}).get("entries") or {}).get(pending_key, {}).get("status"),
           "mcp-only", "case-17: refused import remains explicitly mcp-only", "import")

    finding, _ = make_import(tmp / "case-17-good-finding", arms[1], "finding", 2)
    expect(evalrun(["import-results", "--run", run, "--file", finding]).returncode, 0,
           "case-17: accepted finding import", "import")
    expect(evalrun(["resume", "--run", run]).returncode, 0,
           "case-17: resume accepts and retains imported evidence", "import")
    resumed = load(run / "ledger.json") or {}
    expect(((resumed.get("entries") or {}).get(pending_key) or {}).get("status"), "reused",
           "case-17: imported finding is reused rather than rerun", "import")

    imported_response = run / "imports" / pending_key / "02-response.json"
    original_response = imported_response.read_bytes()
    imported_response.write_bytes(original_response + b"\nchanged")
    expect(evalrun(["finalize", "--run", run]).returncode, 3,
           "case-17: changed imported raw evidence refuses finalize", "import")
    imported_response.write_bytes(original_response)
    expect(evalrun(["finalize", "--run", run]).returncode, 0,
           "case-17: finalize accepts imported receipts", "import")
    bundle = tmp / "case-17-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0,
           "case-17: bundle includes imported evidence", "import")
    moved = tmp / "case-17-relocated"
    shutil.copytree(bundle, moved)
    expect(evalrun(["verify-bundle", moved]).returncode, 0,
           "case-17: relocated imported evidence verifies", "import")
    bundled_response = next((moved / "evidence" / "imports" / pending_key).glob("*response.json"))
    bundled_response.write_bytes(bundled_response.read_bytes() + b"\nchanged")
    expect(evalrun(["verify-bundle", moved]).returncode, 3,
           "case-17: changed bundled raw evidence refuses verification", "import")


# view_providers: the app suite's test helper (test_evalrun), which its half of case 18 uses too
view_providers = ER.view_providers


def mcp_import(folder: Path, plan: dict, cid: str, arm: dict, providers: list):
    """A schema-1 import of one no-finding MCP result for `arm`, stating `providers` as what it read and
    everything else from the arm's frozen request. Returns the manifest path."""
    folder.mkdir(parents=True, exist_ok=True)
    request, response = folder / "request.json", folder / "response.json"
    request.write_text(json.dumps(initialize(f"evalrun:{plan['planId']}:{arm['armId']}")) + "\n"
                       + json.dumps({"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}) + "\n",
                       encoding="utf-8")
    response.write_text(json.dumps({"findings": 0}), encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in providers], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"],
           "state": "baseline", "origin": "fresh", "capturedUtc": datetime_now_z(),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                       for role, p in (("request", request), ("response", response))]}
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def case_18(tmp):
    """D1: an arm's requested providers are what THAT arm's configuration reads, and a plan frozen under
    the old meaning (the scope's providers on every arm) is refused by name wherever it would bind a
    new receipt."""
    case = "case-18-arm-view-providers"
    d, scope_path, cdir, scen = case_paths(case)
    scope = load(scope_path) or {}
    scope_prov = [{"name": v["Name"], "kind": v["Kind"]} for v in scope["configuration"]["providers"]]
    view = ["fixture-mods", "fixture-extra-mods"]
    expect(scope_prov, view_providers(view), f"{case}: the fixture scope's view is overwrite + two mods + Data")

    # ---------------------------------------------------------------- the plan (D1)
    run = tmp / case
    expect(do_plan(run, case, ["--select", "loadability.masters-and-order"]).returncode, 0, f"{case}: plan exit", "D1")
    # import-results credits evidence only where the check could run (review V2 L3): its prerequisite is measured first
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: prerequisite run exit", "§4.8")
    run = to_overlay(run, scen, case)
    plan = load(run / "plan.json") or {}
    arms = arm_map(plan)
    want = {"candidate-00-baseline": view_providers(view),          # Mods == the scope's view
            "candidate-01-narrow-view": view_providers(["fixture-mods"]),
            "candidate-02-same-view": view_providers(view),
            "candidate-03-scope-view": scope_prov,                   # no Mods: the scope's view
            "candidate-04-empty-view": view_providers([])}           # Mods []: no mod folder at all
    for stem, providers in want.items():
        got = ((arms.get(stem) or {}).get("requested") or {}).get("providers")
        expect(got, providers, f"{case}: {stem} requested.providers", "D1 - the arm's own view")
    narrow = arms.get("candidate-01-narrow-view") or {}
    same = arms.get("candidate-02-same-view") or {}
    check("fixture-extra-mods" not in json.dumps(((narrow.get("requested") or {}).get("providers")) or []),
          f"{case}: the narrow arm's request excludes the provider only the scope's view has (D1)")

    checks = plan_checks(plan)
    cid = next((c for c in ("loadability.dangling-references", "records.cell-attributes")
                if ((checks.get(c) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    check(cid is not None, f"{case}: fixture registry contains an mcp-only check")
    if cid is None or not narrow or not same:
        return

    # ---------------------------------------------------------------- import-results against each request
    def imp(tag, arm, providers, want_exit, want_text=None):
        m = mcp_import(tmp / f"{case}-{tag}", plan, cid, arm, providers)
        r = evalrun(["import-results", "--run", run, "--file", m])
        expect(r.returncode, want_exit, f"{case}/{tag}: import-results exit", "D1 / §4.8")
        if want_text:
            check(want_text in r.stderr, f"{case}/{tag}: the refusal names {want_text!r} "
                                         f"(got {r.stderr.strip()[-220:]!r})")

    imp("narrow-with-scope-providers", narrow, scope_prov, 2, "identity-mismatch")
    imp("narrow-with-own-view", narrow, view_providers(["fixture-mods"]), 0)
    imp("same-with-narrower-providers", same, view_providers(["fixture-mods"]), 2, "identity-mismatch")
    imp("same-with-own-view", same, view_providers(view), 0)

    # ---------------------------------------------------------------- an import into a plan frozen under the old meaning
    old = tmp / f"{case}-old-meaning"
    shutil.copytree(run, old)
    op = load(old / "plan.json")
    op.pop("requestProviders", None)
    for a in op.get("arms") or []:
        a["requested"]["providers"] = [dict(v) for v in (op.get("configuration") or {}).get("providers") or []]
    (old / "plan.json").write_text(json.dumps(op, indent=1, ensure_ascii=False), encoding="utf-8")
    (old / "plan.sha256").write_text(sha_of(old / "plan.json") + "\n", encoding="utf-8")
    m = mcp_import(tmp / f"{case}-old-meaning-import", op, cid, next(a for a in op["arms"]
                                                                 if a["armId"] == narrow.get("armId")), scope_prov)
    r = evalrun(["import-results", "--run", old, "--file", m])
    expect(r.returncode, 2, f"{case}/old-meaning: import-results exit", "D1 - refused by name")
    check("plan-request-outdated" in r.stderr,
          f"{case}/old-meaning: import-results names plan-request-outdated (got {r.stderr.strip()[-200:]!r})")


def import_case_dir(case: str, cands: dict, refuse=()) -> tuple[Path, Path]:
    """A small derived-registry fixture (case 26's shape): the fixture plugins, a scope naming them, the given
    candidates `{stem: plugin list}` and a scenario whose scopeprobe fake refuses the stems in `refuse`.
    Returns (case dir, scenario path)."""
    src = ER.GEN / "cases" / "case-19-derived-prerequisites"
    d = ER.GEN / "cases" / case
    pdir = d / "plugins"
    pdir.mkdir(parents=True, exist_ok=True)
    for p in (ER.GEN / "plugins").iterdir():
        shutil.copyfile(p, pdir / p.name)
    scope = load(src / "scope.json")
    for r in scope["configuration"]["plugins"]:
        r["Path"] = str(pdir / r["Name"]).replace("\\", "/")
    (d / "scope.json").write_text(json.dumps(scope, indent=1), encoding="utf-8")
    cdir = d / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)
    for stem, plugins in cands.items():
        (cdir / f"{stem}.json").write_text(json.dumps(
            {"SchemaVersion": 1, "Name": stem, "BaseProfile": "fixture", "Plugins": plugins,
             "Changes": [f"fixture arm {stem}"], "Provenance": {"fixture": "evalrun test fixture"}}, indent=1),
            encoding="utf-8")
    (cdir / "candidates.json").write_text(json.dumps(
        {"SchemaVersion": 1, "Candidates": [{"File": f"{s}.json", "ViolatesAdvice": [], "SatisfiesAdvice": []}
                                            for s in cands],
         "Budget": {"evaluated": len(cands) - 1, "legalNotEvaluated": 0, "note": "fixture manifest"},
         "Contradictions": []}, indent=1), encoding="utf-8")
    return d, import_scenario(d, "scenario.json", refuse)


def import_scenario(d: Path, name: str, refuse=()) -> Path:
    s = d / name
    s.write_text(json.dumps({
        "pluginDir": str(d / "plugins").replace("\\", "/"), "scopeFrom": str(d / "scope.json").replace("\\", "/"),
        "arms": {}, "loadability": {stem: {"refuse": f"MISSING MASTER - fixture refusal of {stem}"}
                                    for stem in refuse}}, indent=1), encoding="utf-8")
    return s


def import_plan(run: Path, case: str, tag: str) -> dict:
    """Plan `case` selecting only masters-and-order (scopeprobe fake), so `run` is fast and the mcp-only import
    check is planned on every arm with masters-and-order as its prerequisite."""
    scope_fake = ER.GEN / "cases" / case / "scopeprobe_fake.py"
    scope_fake.write_text(SCOPE_FAKE_20, encoding="utf-8")
    expect(do_plan(run, case, ["--select", MAO, "--probe-cmd", f"scopeprobe=python {scope_fake}"],
                   registry="fixture-check-registry-derived.json").returncode, 0, f"{tag}: plan exit", "import")
    return load(run / "plan.json") or {}


def import_overlay(run: Path, scen: Path, tag: str, name: str = "ai") -> tuple[Path, dict]:
    """The overlay over import_plan's app run (to_overlay), and import_plan's premise asked of the overlay plan, where
    the record row now is: IMPORT_CHECK mcp-only, with MAO (an app check) as its app prerequisite."""
    ai = to_overlay(run, scen, tag, name)
    plan = load(ai / "plan.json") or {}
    row = next((c for c in plan.get("checks") or [] if c.get("checkId") == IMPORT_CHECK), {})
    check((row.get("adapter") or {}).get("kind") == "mcp-only" and MAO in (row.get("appPrerequisites") or []),
          f"{tag}: premise - {IMPORT_CHECK} is mcp-only and requires {MAO} (got {row.get('appPrerequisites')})")
    return ai, plan


def arm_by_stem(plan: dict, stem: str) -> dict:
    return next((a for a in plan.get("arms") or [] if Path(a.get("candidateFile") or "").stem == stem), {})


def capture_token(plan: dict, arm: dict) -> str:
    return f"evalrun:{plan['planId']}:{arm['armId']}"


def tools_call(n: int, text: str = "") -> dict:
    return {"jsonrpc": "2.0", "id": n, "method": "tools/call",
            "params": {"name": "housecarl_records", "arguments": {"path": "FixtureTarget.esp",
                                                                          "note": text}}}


def origin_import(folder: Path, plan: dict, arm: dict, *, origin="fresh", request=None, captured=None,
                  reused_from=None, drop=(), response=None) -> tuple[Path, dict]:
    """A schema-1 import of a no-finding result for `arm` of `plan`. `request` is the raw request file's exact text
    (default: one JSON-RPC message per line, a tokened initialize, an initialized notification and a call)."""
    folder.mkdir(parents=True, exist_ok=True)
    req_p, resp_p = folder / "request.jsonl", folder / "response.jsonl"
    if request is None:
        request = "\n".join(json.dumps(m) for m in (
            initialize(capture_token(plan, arm)), {"jsonrpc": "2.0", "method": "notifications/initialized"},
            tools_call(1))) + "\n"
    req_p.write_bytes(request.encode("utf-8") if isinstance(request, str) else request)
    resp_p.write_text(response if response is not None else
                      json.dumps({"jsonrpc": "2.0", "id": 1, "result": {"findings": 0}}) + "\n", encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": req["providers"], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = import_fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": IMPORT_CHECK, "armId": arm["armId"],
           "state": "baseline", "origin": origin,
           "capturedUtc": captured or datetime_now_z(),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": 3, "Unjudgeable": {}}]},
           "outputs": [{"role": role, "path": p.name, "sha256": sha_of(p)}
                       for role, p in (("request", req_p), ("response", resp_p))]}
    if reused_from is not None:
        doc["reusedFrom"] = reused_from
    for k in drop:
        doc.pop(k, None)
    manifest = folder / "import.json"
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest, doc


def shift_iso(text: str, seconds: float = 0, offset_hours: int = 0) -> str:
    """`text` moved by `seconds` and written in UTC`offset_hours` - the same instant when seconds is 0."""
    from datetime import datetime, timedelta, timezone
    t = datetime.fromisoformat(text.replace("Z", "+00:00")) + timedelta(seconds=seconds)
    return t.astimezone(timezone(timedelta(hours=offset_hours))).isoformat()


def write_doc(manifest: Path, doc: dict) -> Path:
    manifest.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    return manifest


def import_state(run: Path, cid: str, aid: str) -> tuple[str | None, dict, bool]:
    """(ledger status, receipt, whether any imports/<key>* folder exists) for one import key."""
    key = f"{cid}__{aid}"
    status = ((load(run / "ledger.json") or {}).get("entries") or {}).get(key, {}).get("status")
    rc = load(run / "receipts" / f"{key}.json") if (run / "receipts" / f"{key}.json").exists() else {}
    # this key's folders only: `<key>` and its re-imports `<key>~<n>`, never an arm whose id starts with this one
    folders = [p for p in (run / "imports").glob(f"{key}*")
               if p.name == key or p.name.startswith(key + "~")] if (run / "imports").exists() else []
    return status, rc or {}, bool(folders)


def case_42(tmp):
    """Option A (2026-09-17): an import declares where its evidence came from. `fresh` evidence must have been
    captured for THIS plan's arm - every MCP initialize in its hash-checked raw request names
    evalrun:<planId>:<armId> in clientInfo.name and capturedUtc is not before the plan was made. `reused` evidence
    names its source plan, arm and result hash and is labelled reused end to end. The exact relabelling mistake
    (retained evidence with only planId, armId and configuration.source edited) is refused as fresh; every existing
    identity and hash check still applies to both origins."""
    case = "case-42-import-origin"
    stems1 = {"candidate-00-baseline": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
              "candidate-01-reorder": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"]}
    # plan 2: the same plugins under a different candidate file, so its arm has another id and source and the same
    # configuration fingerprint - the relabelling V2 reproduced.
    stems2 = {"candidate-00-baseline": stems1["candidate-00-baseline"],
              "candidate-01-renamed": stems1["candidate-01-reorder"],
              # a second setup with the SAME configuration (the destination of a sibling-arm reuse) and one with a
              # different order, whose configuration fingerprint differs (the mismatch control)
              "candidate-02-same": stems1["candidate-01-reorder"],
              "candidate-03-different": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"]}
    _, scen1 = import_case_dir(case + "-p1", stems1)
    _, scen2 = import_case_dir(case + "-p2", stems2)
    run1, run2 = tmp / f"{case}-run1", tmp / f"{case}-run2"
    plan1 = import_plan(run1, case + "-p1", f"{case}/p1")
    time.sleep(1.1)  # plan 2 is created at a later second than plan 1
    plan2 = import_plan(run2, case + "-p2", f"{case}/p2")
    for run, scen, tag in ((run1, scen1, "p1"), (run2, scen2, "p2")):
        expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/{tag}: run exit", "import")
    (run1, plan1), (run2, plan2) = import_overlay(run1, scen1, f"{case}/p1"), import_overlay(run2, scen2, f"{case}/p2")
    if not plan1 or not plan2:
        return
    check(plan1["planId"] != plan2["planId"], f"{case}: premise - the two plans have different ids")
    a1, a2 = arm_by_stem(plan1, "candidate-01-reorder"), arm_by_stem(plan2, "candidate-01-renamed")
    b1, b2 = arm_by_stem(plan1, "candidate-00-baseline"), arm_by_stem(plan2, "candidate-00-baseline")
    for run, arm, tag in ((run1, a1, "p1"), (run2, a2, "p2"), (run1, b1, "p1-base"), (run2, b2, "p2-base")):
        expect(execution_of((ledger_entries(load(run.parent / "ledger.json") or {})).get(f"{MAO}__{arm.get('armId')}")),
               "completed", f"{case}/{tag}: premise - {MAO} accepted on {arm.get('armId')}")
    check(a1.get("armId") != a2.get("armId")
          and [(p["name"], p["sha256"]) for p in a1["requested"]["plugins"]]
          == [(p["name"], p["sha256"]) for p in a2["requested"]["plugins"]],
          f"{case}: premise - the relabel target arm differs in id and matches in plugin names and bytes")

    def imp(run, manifest, want, tag, text=None, absent=None):
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "option A")
        if text:
            check(text in r.stderr, f"{case}/{tag}: the refusal names {text!r} (got {r.stderr.strip()[-300:]!r})")
        if absent:
            check(absent not in r.stderr, f"{case}/{tag}: the refusal does not blame {absent!r} "
                                          f"(got {r.stderr.strip()[-300:]!r})")
        return r

    def untouched(run, arm, tag):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, "mcp-only", f"{case}/{tag}: the refused import leaves the entry mcp-only", "import")
        check(not (rc.get("validation") or {}).get("accepted") and not folder,
              f"{case}/{tag}: no accepted receipt and no imports folder after a refusal")

    # ---- a valid fresh capture for plan 1 is accepted and says so
    good1, good1_doc = origin_import(tmp / f"{case}-good1", plan1, a1)
    imp(run1, good1, 0, "fresh-valid")
    _, rc1, _ = import_state(run1, IMPORT_CHECK, a1["armId"])
    imported1 = rc1.get("imported") or {}
    expect(imported1.get("origin"), "fresh", f"{case}/fresh-valid: the receipt records origin fresh", "option A")
    expect((imported1.get("originCheck") or {}).get("token"), capture_token(plan1, a1),
           f"{case}/fresh-valid: the receipt records the token it checked", "option A")
    expect((imported1.get("originCheck") or {}).get("initializeRequests"), 1,
           f"{case}/fresh-valid: the receipt counts the initialize requests it read", "option A")
    source_sha = sha_of(run1 / "imports" / f"{IMPORT_CHECK}__{a1['armId']}" / "result.json")
    expect(imported1.get("sourceSha256"), source_sha, f"{case}: premise - sourceSha256 is the copied result.json")

    # ---- the exact V2 mistake: plan 1's accepted envelope with ONLY planId, armId and configuration.source edited
    relabel = json.loads(json.dumps(good1_doc))
    relabel["planId"], relabel["armId"] = plan2["planId"], a2["armId"]
    relabel["configuration"]["source"] = f"candidate:{a2['candidateFile']}"
    changed = sorted(k for k in good1_doc if good1_doc[k] != relabel[k])
    expect(changed, ["armId", "configuration", "planId"], f"{case}/relabel: premise - exactly the three labels differ")
    expect(import_fingerprint(relabel["configuration"]), relabel["configuration"]["fingerprint"],
           f"{case}/relabel: premise - the fingerprint is unchanged and still valid")
    rel_m = write_doc(good1.parent / "relabel.json", relabel)
    imp(run2, rel_m, 2, "relabel-as-fresh", text="evalrun:", absent="capturedUtc")
    untouched(run2, a2, "relabel-as-fresh")
    # the same relabelled envelope with no origin at all (as retained envelopes are) is refused by name too
    legacy = dict(relabel)
    legacy.pop("origin")
    imp(run2, write_doc(good1.parent / "relabel-no-origin.json", legacy), 2, "relabel-no-origin", text="origin")
    untouched(run2, a2, "relabel-no-origin")

    # ---- origin declarations
    def variant(tag, run, plan, arm, **kw):
        return origin_import(tmp / f"{case}-{tag}", plan, arm, **kw)[0]

    imp(run2, variant("no-origin", run2, plan2, a2, drop=("origin",)), 2, "origin-missing", text="origin-missing")
    imp(run2, variant("bad-origin", run2, plan2, a2, origin="measured"), 2, "origin-invalid", text="origin")
    imp(run2, variant("fresh-with-source", run2, plan2, a2,
                      reused_from={"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}),
        2, "fresh-declares-source", text="reusedFrom")

    # ---- the token: absent, wrong arm, wrong plan, mixed, only in unrelated text, no initialize, empty, unreadable
    t2 = capture_token(plan2, a2)

    def lines(*msgs):
        return "\n".join(m if isinstance(m, str) else json.dumps(m) for m in msgs) + "\n"

    token_cases = [
        ("token-absent", lines(initialize("another-capture-wrapper"), tools_call(1))),
        ("token-name-missing", lines(initialize(None), tools_call(1))),
        ("token-wrong-arm", lines(initialize(capture_token(plan2, b2)), tools_call(1))),
        ("token-wrong-plan", lines(initialize(capture_token(plan1, a2)), tools_call(1))),
        ("token-in-version", lines({**initialize("capture"), "params": {**initialize("capture")["params"],
                                    "clientInfo": {"name": "capture", "version": t2}}}, tools_call(1))),
        ("token-mixed", lines(initialize(t2), tools_call(1), initialize("another-capture-wrapper"),
                              tools_call(2))),
        ("token-mixed-batch", lines([initialize(t2), tools_call(1)], [initialize(t2 + "x"), tools_call(2)])),
        ("token-only-in-call", lines(tools_call(1, t2), {"batchMarker": t2})),
        ("token-only-in-marker", lines({"batchMarker": t2, "clientInfo": {"name": t2}},
                                       initialize("another-capture-wrapper"), tools_call(1))),
        ("token-empty-file", ""),
        ("token-unreadable-line", lines(initialize(t2), "{not json " + t2)),
    ]
    for tag, text in token_cases:
        imp(run2, variant(tag, run2, plan2, a2, request=text), 2, tag, text="evalrun:" if text else "initialize")
    untouched(run2, a2, "token-controls")

    # ---- capturedUtc against the plan's createdUtc, compared as instants
    created2 = plan2["createdUtc"]
    imp(run2, variant("time-old", run2, plan2, a2, captured=shift_iso(created2, -1)), 2, "time-before-plan",
        text="capturedUtc")
    imp(run2, variant("time-old-offset", run2, plan2, a2, captured=shift_iso(created2, -1, -7)), 2,
        "time-before-plan-other-offset", text="capturedUtc")
    imp(run2, variant("time-naive", run2, plan2, a2, captured=created2.replace("Z", "")), 2, "time-naive",
        text="capturedUtc")
    imp(run2, variant("time-invalid", run2, plan2, a2, captured="yesterday"), 2, "time-invalid", text="capturedUtc")
    untouched(run2, a2, "time-controls")
    # the boundary: exactly the plan's instant, written at UTC-07:00, is not earlier
    imp(run2, variant("time-boundary", run2, plan2, b2, captured=shift_iso(created2, 0, -7)), 0, "time-boundary-equal")
    # a JSON-RPC batch line (an array) is read as messages too
    imp(run1, variant("batch-array", run1, plan1, b1,
                      request=lines([initialize(capture_token(plan1, b1)), tools_call(1)])), 0, "fresh-batch-array")

    # ---- reused: the source must be declared, well formed and not this plan's own arm; identity still decides
    src = {"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}
    reuse_request = (good1.parent / "request.jsonl").read_text(encoding="utf-8")  # plan 1's token, by design

    def reused(tag, **kw):
        kw.setdefault("reused_from", src)
        kw.setdefault("request", reuse_request)
        kw.setdefault("captured", good1_doc["capturedUtc"])
        return variant(tag, run2, plan2, a2, origin="reused", **kw)

    imp(run2, reused("reuse-no-source", reused_from=None), 2, "reuse-without-source", text="reusedFrom")
    for tag, bad in (("reuse-short-hash", {**src, "resultSha256": "abc"}),
                     ("reuse-no-arm", {k: v for k, v in src.items() if k != "armId"}),
                     ("reuse-empty-plan", {**src, "planId": ""}),
                     ("reuse-not-object", "plan 1")):
        imp(run2, reused(tag, reused_from=bad), 2, tag, text="reusedFrom")
    imp(run2, reused("reuse-self", reused_from={**src, "planId": plan2["planId"], "armId": a2["armId"]}), 2,
        "reuse-names-itself", text="reusedFrom")
    # a sibling arm of THIS plan is a legitimate source when the two setups' configurations match: the evidence is
    # measured once and reused, and the receipt says which statement that is (decided 2026-09-17). The
    # configuration check is unchanged, so a sibling whose configuration does NOT match still refuses: that control
    # is below, on the plan-1 baseline arm, whose requested configuration differs from the candidate's.
    wrong_cfg = reused("reuse-wrong-config")
    wd = load(wrong_cfg)
    wd["configuration"]["plugins"][0]["sha256"] = "0" * 64
    wd["configuration"]["fingerprint"] = import_fingerprint(wd["configuration"])
    imp(run2, write_doc(wrong_cfg, wd), 2, "reuse-changed-configuration", text="identity-mismatch")
    wrong_fp = reused("reuse-wrong-fingerprint")
    wf = load(wrong_fp)
    wf["configuration"]["plugins"] = list(reversed(wf["configuration"]["plugins"]))
    wf["configuration"]["fingerprint"] = import_fingerprint(wf["configuration"])
    imp(run2, write_doc(wrong_fp, wf), 2, "reuse-changed-order", text="identity-mismatch")
    stale = reused("reuse-changed-evidence")
    (stale.parent / "response.jsonl").write_text('{"jsonrpc": "2.0", "id": 1, "result": {"findings": 7}}\n',
                                                 encoding="utf-8")
    imp(run2, stale, 2, "reuse-changed-evidence", text="output-stale")
    zero = reused("reuse-zero-coverage")
    zd = load(zero)
    zd["result"]["coverage"][0]["Judged"] = 0
    imp(run2, write_doc(zero, zd), 2, "reuse-zero-coverage", text="extraction-failed")
    untouched(run2, a2, "reuse-controls")

    ok = reused("reuse-valid")
    imp(run2, ok, 0, "reuse-valid")
    status, rc2, _ = import_state(run2, IMPORT_CHECK, a2["armId"])
    imported2 = rc2.get("imported") or {}
    expect(status, "completed", f"{case}/reuse-valid: ledger completed", "option A")
    expect(imported2.get("origin"), "reused", f"{case}/reuse-valid: the receipt records origin reused", "option A")
    expect(imported2.get("reusedFrom"), src, f"{case}/reuse-valid: the receipt retains the declared source", "option A")
    expect(imported2.get("reusedFromVerified"), False,
           f"{case}/reuse-valid: the declared source is not presented as verified", "option A")
    check("originCheck" not in imported2, f"{case}/reuse-valid: a reused import claims no capture-token check")
    expect(imported2.get("reusedWithin"), "another-evaluation",
           f"{case}/reuse-valid: the receipt says the source is another evaluation", "option A")

    # ---- reuse between two setups of THIS evaluation (decided 2026-09-17): measured once for identical
    # configurations, accepted, and labelled as reuse WITHIN this evaluation rather than from an earlier one.
    c2, d2 = arm_by_stem(plan2, "candidate-02-same"), arm_by_stem(plan2, "candidate-03-different")
    check(bool(c2) and bool(d2), f"{case}: premise - plan 2 has an identical-configuration arm and a different one")
    for arm, same in ((c2, True), (d2, False)):
        expect([(p["name"], p["sha256"]) for p in (arm.get("requested") or {}).get("plugins") or []]
               == [(p["name"], p["sha256"]) for p in a2["requested"]["plugins"]], same,
               f"{case}: premise - {arm.get('armId')} configuration matches the reuse source: {same}")
    sibling = {"planId": plan2["planId"], "armId": a2["armId"],
               "resultSha256": sha_of(run2 / "imports" / f"{IMPORT_CHECK}__{a2['armId']}" / "result.json")}
    # the configuration checks are untouched: an envelope for this arm carrying ANOTHER setup's configuration refuses,
    # sibling-arm source or not
    mism, mism_doc = origin_import(tmp / f"{case}-sibling-mismatch", plan2, c2, origin="reused",
                                   reused_from=sibling, request=reuse_request, captured=good1_doc["capturedUtc"])
    mism_doc["configuration"]["plugins"] = [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                                            for p in d2["requested"]["plugins"]]
    mism_doc["configuration"]["fingerprint"] = import_fingerprint(mism_doc["configuration"])
    imp(run2, write_doc(mism, mism_doc), 2, "sibling-reuse-wrong-configuration", text="identity-mismatch")
    untouched(run2, c2, "sibling-reuse-wrong-configuration")
    # and the arm's own prerequisite still has to be settled - it is, so the honest declaration is accepted
    sib = origin_import(tmp / f"{case}-sibling-valid", plan2, c2, origin="reused", reused_from=sibling,
                        request=reuse_request, captured=good1_doc["capturedUtc"])[0]
    imp(run2, sib, 0, "sibling-reuse-valid")
    status_c2, rc_c2, _ = import_state(run2, IMPORT_CHECK, c2["armId"])
    imported_c2 = rc_c2.get("imported") or {}
    expect(status_c2, "completed", f"{case}/sibling-reuse-valid: ledger completed", "reuse within this evaluation")
    expect(imported_c2.get("origin"), "reused", f"{case}/sibling-reuse-valid: origin reused")
    expect(imported_c2.get("reusedWithin"), "this-evaluation",
           f"{case}/sibling-reuse-valid: the receipt says the source is another setup of THIS evaluation",
           "reuse within this evaluation")
    expect(imported_c2.get("reusedFrom"), sibling,
           f"{case}/sibling-reuse-valid: the declared source is retained")
    expect(imported_c2.get("reusedFromVerified"), False,
           f"{case}/sibling-reuse-valid: still declared, not verified")
    # the destination's own (plan, arm) is still not a source
    imp(run2, origin_import(tmp / f"{case}-sibling-self", plan2, d2, origin="reused",
                            reused_from={**sibling, "armId": d2["armId"]}, request=reuse_request,
                            captured=good1_doc["capturedUtc"])[0], 2, "sibling-self-still-refused", text="reusedFrom")

    # ---- the label survives resume, a ledger rebuild, finalize, a relocated bundle and STATUS.md
    receipt_bytes = (run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json").read_bytes()
    expect(evalrun(["resume", "--run", run2], scen2).returncode, 0, f"{case}/resume: exit", "option A")
    # the ledger's own note that this entry came from an import, and with what origin, survives the transition that
    # re-uses it (review R1b F5); a --rebuild-ledger reads the journal, which carries no extras, and the receipt is
    # then the authority - asserted below.
    resumed_entry = ((load(run2 / "ledger.json") or {}).get("entries") or {}).get(
        f"{IMPORT_CHECK}__{a2['armId']}") or {}
    expect(resumed_entry.get("status"), "reused", f"{case}/ledger: premise - the executor re-used the entry")
    expect(resumed_entry.get("importOrigin"), "reused",
           f"{case}/ledger: the ledger still records the import's origin after resume", "option A")
    check(resumed_entry.get("imported") is True,
          f"{case}/ledger: the ledger still records that it was imported after resume")
    expect(evalrun(["resume", "--run", run2, "--rebuild-ledger"], scen2).returncode, 0,
           f"{case}/rebuild: exit", "option A")
    status, rc2b, _ = import_state(run2, IMPORT_CHECK, a2["armId"])
    expect(status, "reused", f"{case}/rebuild: the import is still credited (executor reuse)", "option A")
    check((run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json").read_bytes() == receipt_bytes,
          f"{case}/rebuild: the import receipt is byte-identical after resume and rebuild")
    expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0, f"{case}/finalize: exit", "option A")
    status_md = (run2 / "STATUS.md").read_text(encoding="utf-8")
    check("reused" in status_md and plan1["planId"] in status_md,
          f"{case}/finalize: STATUS.md names the reused import and its source plan")
    check("of THIS plan" in status_md and a2["armId"] in status_md,
          f"{case}/finalize: STATUS.md says the sibling-arm reuse came from another setup of this plan, "
          f"not from an earlier evaluation")
    rows_md = {r.get("armId"): r for r in (load(run2 / "disposition.json") or {}).get("importedEvidence") or []}
    expect((rows_md.get(c2["armId"]) or {}).get("reusedWithin"), "this-evaluation",
           f"{case}/finalize: the disposition carries the reuse scope", "reuse within this evaluation")
    expect((rows_md.get(a2["armId"]) or {}).get("reusedWithin"), "another-evaluation",
           f"{case}/finalize: and distinguishes the cross-evaluation one", "reuse within this evaluation")
    check("fresh" in status_md, f"{case}/finalize: STATUS.md names the fresh import's origin")
    bundle, moved = tmp / f"{case}-bundle", tmp / f"{case}-relocated"
    expect(evalrun(["bundle", "--run", run2, "--out", bundle], scen2).returncode, 0, f"{case}/bundle: exit")
    shutil.copytree(bundle, moved)
    expect(evalrun(["verify-bundle", moved]).returncode, 0, f"{case}/relocated: verify-bundle exit", "option A")
    brc = load(moved / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}
    expect((brc.get("imported") or {}).get("origin"), "reused", f"{case}/relocated: bundled receipt says reused")
    expect((brc.get("imported") or {}).get("reusedFrom"), src, f"{case}/relocated: bundled receipt keeps the source")

    # ---- a receipt accepted before origin existed: its `imported` block is exactly what the earlier importer wrote
    # ({sourceSha256, capturedUtc, tool}). It stays readable, credited and verifiable, and is reported as origin
    # unknown - never as fresh.
    legacy_p = run1 / "receipts" / f"{IMPORT_CHECK}__{a1['armId']}.json"
    lrc = load(legacy_p)
    lrc["imported"] = {k: lrc["imported"][k] for k in ("sourceSha256", "capturedUtc", "tool")}
    legacy_p.write_text(json.dumps(lrc, indent=1), encoding="utf-8")
    expect(evalrun(["resume", "--run", run1], scen1).returncode, 0, f"{case}/legacy: resume exit")
    status, _, _ = import_state(run1, IMPORT_CHECK, a1["armId"])
    expect(status, "reused", f"{case}/legacy: the legacy import is still credited after resume", "option A")
    expect(evalrun(["finalize", "--run", run1], scen1).returncode, 0, f"{case}/legacy: finalize exit")
    rows = {(x.get("armId")): x for x in (load(run1 / "disposition.json") or {}).get("importedEvidence") or []}
    expect((rows.get(a1["armId"]) or {}).get("origin"), "unknown",
           f"{case}/legacy: disposition reports the legacy import's origin as unknown", "option A")
    expect((rows.get(b1["armId"]) or {}).get("origin"), "fresh",
           f"{case}/legacy: the fresh import beside it stays fresh", "option A")
    s1 = (run1 / "STATUS.md").read_text(encoding="utf-8")
    check("origin not recorded" in s1, f"{case}/legacy: STATUS.md says the legacy import's origin is not recorded")
    lb, lmoved = tmp / f"{case}-legacy-bundle", tmp / f"{case}-legacy-relocated"
    expect(evalrun(["bundle", "--run", run1, "--out", lb], scen1).returncode, 0, f"{case}/legacy: bundle exit")
    shutil.copytree(lb, lmoved)
    expect(evalrun(["verify-bundle", lmoved]).returncode, 0, f"{case}/legacy: relocated verify-bundle exit")

    # ---- after a --rebuild-ledger the RECEIPT is the authority on origin, and the rebuilt entry now CARRIES
    # what the receipt says rather than a hole. This expectation was corrected on 2026-09-17: it previously
    # asserted the rebuilt entry claimed no origin, which encoded the defect Piranha's handback named - the
    # journal carries no extras, and `rebuild` was not consulting the receipts either, although its own
    # docstring and `--rebuild-ledger`'s help both say it is a view of the journal AND the receipts. The
    # consequence was not cosmetic: `finalize` reads `detector` from the ledger, so every rebuilt entry
    # finalized as `detector-undetermined` and an import with accepted evidence on disk was reported missing.
    # The authority is unchanged - the value below comes FROM the receipt, and a receipt that does not match
    # this entry donates nothing (`Ledger.receipt_facts`).
    rebuilt = ((load(run2 / "ledger.json") or {}).get("entries") or {}).get(f"{IMPORT_CHECK}__{a2['armId']}") or {}
    expect(rebuilt.get("importOrigin"), "reused",
           f"{case}/ledger: a rebuilt entry recovers the origin its receipt records", "option A")
    expect(rebuilt.get("detector"), (load(run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json")
                                     or {}).get("result", {}).get("detector"),
           f"{case}/ledger: and the detector, which is what finalize reads from the ledger", "option A")
    expect(((load(run2 / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}).get("imported")
            or {}).get("origin"), "reused",
           f"{case}/ledger: and the receipt still carries it after the rebuild", "option A")


def case_43(tmp):
    """Review V2 L3: import-results consults the controller's prerequisite gate (`prereq_satisfied`) for both
    origins before anything is written, and an import accepted earlier stops being credited when run/resume
    establishes a prerequisite failure on its arm. A failed arm is not rescued by the baseline; a prerequisite not
    attempted on the arm still falls back to the baseline; recovery is a re-import once the arm's prerequisite is
    legitimately satisfied."""
    case = "case-43-import-prerequisites"
    stems = {"candidate-00-baseline": ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"],
             "candidate-01-missing-master": ["Update.esm", "Skyrim.esm", "FixtureTarget.esp"],
             "candidate-02-reorder": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"]}
    d, scen = import_case_dir(case, stems, refuse=("candidate-01-missing-master",))
    scen_ok = import_scenario(d, "scenario-recovered.json", ())

    def imp(run, manifest, want, tag, text=None):
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "V2 L3")
        if text:
            check(text in r.stderr, f"{case}/{tag}: the refusal names {text!r} (got {r.stderr.strip()[-300:]!r})")
        return r

    def refused_clean(run, arm, tag, want_status):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, want_status, f"{case}/{tag}: the ledger entry is unchanged by the refusal", "V2 L3")
        check(not (rc.get("validation") or {}).get("accepted"), f"{case}/{tag}: no accepted receipt for the key")
        check(not folder, f"{case}/{tag}: no imports folder was written")

    def nulls_and_missing(run):
        disp = load(run / "disposition.json") or {}
        nulls = {(r.get("checkId"), r.get("armId")) for r in (field(disp, "completedNullChecks") or [])}
        missing = {(r.get("checkId"), r.get("armId")): r.get("status") for r in (field(disp, "missing") or [])}
        return nulls, missing

    # ---- (a) prerequisite unsettled everywhere: nothing has run yet
    app = tmp / f"{case}-main"
    import_plan(app, case, f"{case}/main")
    run, plan = import_overlay(app, scen, f"{case}/main", "ai-before-run")
    if not plan:
        return
    bad, good, base = (arm_by_stem(plan, "candidate-01-missing-master"), arm_by_stem(plan, "candidate-02-reorder"),
                       arm_by_stem(plan, "candidate-00-baseline"))
    source = {"planId": "0123456789ab", "armId": "candidate-02-other", "resultSha256": "a" * 64}
    m_good = origin_import(tmp / f"{case}-good-fresh", plan, good)[0]
    imp(run, m_good, 2, "unsettled-fresh", text=MAO)
    m_good_reused = origin_import(tmp / f"{case}-good-reused", plan, good, origin="reused", reused_from=source)[0]
    imp(run, m_good_reused, 2, "unsettled-reused", text=MAO)
    # the overlay is evaluated before any import (to_overlay), so the row's entry is there, mcp-only, and stays so
    refused_clean(run, good, "unsettled", "mcp-only")

    # ---- (b) run: the baseline and the reorder arm load, the missing-master arm is refused
    expect(evalrun(["run", "--run", app], scen).returncode, 0, f"{case}/main: run exit", "V2 L3")
    led = ledger_entries(load(app / "ledger.json") or {})
    run, plan = import_overlay(app, scen, f"{case}/main", "ai")
    bad, good, base = (arm_by_stem(plan, "candidate-01-missing-master"), arm_by_stem(plan, "candidate-02-reorder"),
                       arm_by_stem(plan, "candidate-00-baseline"))
    m_good = origin_import(tmp / f"{case}-good-fresh-2", plan, good)[0]
    expect(execution_of(led.get(f"{MAO}__{base['armId']}")), "completed", f"{case}: premise - {MAO} baseline")
    expect(execution_of(led.get(f"{MAO}__{bad['armId']}")), "rejected(illegal-configuration)",
           f"{case}: premise - {MAO} refused on {bad['armId']}")
    expect(execution_of(led.get(f"{MAO}__{good['armId']}")), "completed", f"{case}: premise - {MAO} {good['armId']}")

    # ---- (c) a failed arm with a passing baseline refuses, fresh and reused alike, naming the prerequisite
    m_bad = origin_import(tmp / f"{case}-bad-fresh", plan, bad)[0]
    r = imp(run, m_bad, 2, "failed-arm-fresh", text=MAO)
    check("rejected" in r.stderr, f"{case}/failed-arm-fresh: the refusal says the prerequisite was refused on this "
                                  f"arm (got {r.stderr.strip()[-300:]!r})")
    m_bad_reused = origin_import(tmp / f"{case}-bad-reused", plan, bad, origin="reused", reused_from=source)[0]
    imp(run, m_bad_reused, 2, "failed-arm-reused", text=MAO)
    refused_clean(run, bad, "failed-arm", "mcp-only")

    # ---- (d) the arms whose prerequisite is settled import normally
    imp(run, m_good, 0, "settled-fresh")
    imp(run, origin_import(tmp / f"{case}-base-reused", plan, base, origin="reused", reused_from=source)[0], 0,
        "settled-baseline-reused")
    expect(evalrun(["finalize", "--run", run], scen).returncode, 0, f"{case}/main: finalize exit")
    nulls, missing = nulls_and_missing(run)
    check((IMPORT_CHECK, good["armId"]) in nulls and (IMPORT_CHECK, base["armId"]) in nulls,
          f"{case}/main: the accepted imports are completed null checks")
    check((IMPORT_CHECK, bad["armId"]) not in nulls and (IMPORT_CHECK, bad["armId"]) in missing,
          f"{case}/main: the refused import is missing, never a completed null (got {missing.get((IMPORT_CHECK, bad['armId']))})")

    # ---- (i) a row the plan did not leave open for an import is refused. The fixture registry has no inapplicable
    # mcp-only check, so the row status is written into the frozen plan (with its hash) - the shape the planner
    # writes for applicability `no`. Synthetic by construction, and labelled as such.
    app4 = tmp / f"{case}-not-applicable"
    import_plan(app4, case, f"{case}/not-applicable")
    expect(evalrun(["run", "--run", app4], scen).returncode, 0, f"{case}/not-applicable: the app run exit")
    run4, plan4 = import_overlay(app4, scen, f"{case}/not-applicable")
    arm4 = arm_by_stem(plan4, "candidate-02-reorder")
    doc4 = load(run4 / "plan.json")
    for c in doc4["checks"]:
        if c["checkId"] == IMPORT_CHECK:
            for r in c["arms"]:
                r["status"] = "not-applicable"
            c["applicabilityEvidence"] = "fixture: the registry answered applicability no for this scope"
    (run4 / "plan.json").write_text(json.dumps(doc4, indent=1, ensure_ascii=False), encoding="utf-8")
    (run4 / "plan.sha256").write_text(sha_of(run4 / "plan.json") + "\n", encoding="utf-8")
    plan4 = load(run4 / "plan.json")
    # the overlay evaluated again over its edited plan, as the app suite ran its edited plan
    expect(evalrun(["run", "--run", run4], scen).returncode, 0, f"{case}/not-applicable: run exit")
    m4 = origin_import(tmp / f"{case}-not-applicable-fresh", plan4, arm4)[0]
    r4 = imp(run4, m4, 2, "not-applicable-row", text="not-applicable")
    check("wrong-check" in r4.stderr, f"{case}/not-applicable: refused as wrong-check "
                                      f"(got {r4.stderr.strip()[-200:]!r})")
    status4, rc4, folder4 = import_state(run4, IMPORT_CHECK, arm4["armId"])
    expect(status4, "not-applicable", f"{case}/not-applicable: the plan's answer stands", "R1b-F1")
    check(not (rc4.get("validation") or {}).get("accepted") and not folder4,
          f"{case}/not-applicable: nothing was written")


def case_44(tmp):
    """The candidate FILENAME is provenance, not this arm's identity (2026-09-17).

    A declared reuse carries the capture's own `Configuration.Source` - the file the SOURCE arm was measured
    against - because rewriting it would edit the capture's statement of what it measured, the one thing the
    contract says must not be edited. That name alone must not refuse the import when the configuration the emit
    STATES is this arm's frozen request; the difference is recorded as an identity limit, the source name is kept
    as captured, and the reader is told. Nothing else is relaxed: the same envelope declared FRESH is still
    refused by name, a changed configuration or order still refuses on its own cause, a bare `origin: reused`
    never reaches the exception, self-reuse still refuses, and the acceptance survives resume, a ledger rebuild,
    finalize and a relocated bundle while tampered evidence still loses credit. Case 42's reuse envelopes are all
    built by the DESTINATION arm's helper, so they name the destination's candidate and could not have caught
    this; every envelope here is built by the SOURCE arm's helper, as a retained capture really is."""
    case = "case-44-import-reuse-filename"
    plugins = ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"]
    stems1 = {"candidate-00-baseline": plugins, "candidate-01-source": plugins}
    stems2 = {"candidate-00-baseline": plugins,
              # the destination of the cross-evaluation reuse: same configuration, a DIFFERENT filename
              "candidate-01-destination": plugins,
              # a second setup of plan 2, for the sibling reuse whose source is plan 2's own other arm
              "candidate-02-sibling": plugins,
              # a setup whose plugin ORDER differs, so its configuration fingerprint differs
              "candidate-03-other-order": ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"],
              # the OTHER arm of the filename conditional: a reuse whose capture names a file with THIS arm's
              # own name, which must be an ordinary reuse with no limit and nothing extra said about it
              "candidate-04-same-name": plugins,
              # destinations for the three inputs that used to skip, or must not trigger, the disclosure
              "candidate-05-no-source": plugins,
              "candidate-06-stem": plugins,
              "candidate-07-cross-unknown": plugins}
    _, scen1 = import_case_dir(case + "-p1", stems1)
    _, scen2 = import_case_dir(case + "-p2", stems2)
    run1, run2 = tmp / f"{case}-run1", tmp / f"{case}-run2"
    plan1 = import_plan(run1, case + "-p1", f"{case}/p1")
    time.sleep(1.1)
    plan2 = import_plan(run2, case + "-p2", f"{case}/p2")
    for run, scen, tag in ((run1, scen1, "p1"), (run2, scen2, "p2")):
        expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}/{tag}: run exit", "reuse-filename")
    (run1, plan1), (run2, plan2) = import_overlay(run1, scen1, f"{case}/p1"), import_overlay(run2, scen2, f"{case}/p2")
    if not plan1 or not plan2:
        return
    a1 = arm_by_stem(plan1, "candidate-01-source")
    a2 = arm_by_stem(plan2, "candidate-01-destination")
    c2 = arm_by_stem(plan2, "candidate-02-sibling")
    d2 = arm_by_stem(plan2, "candidate-03-other-order")
    for arm, tag in ((a1, "p1-source"), (a2, "p2-destination"), (c2, "p2-sibling"), (d2, "p2-other-order")):
        check(bool(arm), f"{case}: premise - the fixture has a {tag} arm")
    if not (a1 and a2 and c2 and d2):
        return

    def rows(arm):
        return [(p["name"], p["sha256"]) for p in (arm.get("requested") or {}).get("plugins") or []]

    # ---- the premise this case rests on: the arms differ in FILENAME and in nothing identity is made of.
    for other, tag in ((a2, "destination"), (c2, "sibling")):
        check(a1["candidateFile"] != other["candidateFile"],
              f"{case}: premise - source and {tag} candidate FILENAMES differ "
              f"({a1['candidateFile']} vs {other['candidateFile']})")
    for arm, same, tag in ((a2, True, "destination"), (c2, True, "sibling"), (d2, False, "other-order")):
        expect(rows(arm) == rows(a1), same,
               f"{case}: premise - {tag} plugin names and bytes match the source: {same}", "reuse-filename")
    for key in ("providers", "overlays", "population"):
        expect((a2.get("requested") or {}).get(key), (a1.get("requested") or {}).get(key),
               f"{case}: premise - destination {key} equals the source's", "reuse-filename")

    def imp(run, manifest, want, tag, text=None, absent=None):
        """`text` may be one string or several: a refusal that would still be produced with the filename rule
        wrongly relaxed pins nothing on its own, so those controls name the clause that DISAPPEARS too."""
        r = evalrun(["import-results", "--run", run, "--file", manifest])
        expect(r.returncode, want, f"{case}/{tag}: import-results exit", "reuse-filename")
        for want_text in ([text] if isinstance(text, str) else (text or [])):
            check(want_text in r.stderr, f"{case}/{tag}: the refusal names {want_text!r} "
                                         f"(got {r.stderr.strip()[-300:]!r})")
        if absent:
            check(absent not in r.stderr, f"{case}/{tag}: the refusal does not blame {absent!r} "
                                          f"(got {r.stderr.strip()[-300:]!r})")
        return r

    def untouched(run, arm, tag):
        status, rc, folder = import_state(run, IMPORT_CHECK, arm["armId"])
        expect(status, "mcp-only", f"{case}/{tag}: the refused import leaves the entry mcp-only", "reuse-filename")
        check(not (rc.get("validation") or {}).get("accepted") and not folder,
              f"{case}/{tag}: no accepted receipt and no imports folder after a refusal")

    def limit_row(rc):
        """The identity limit this change records, or None."""
        return next((x for x in (rc.get("validation") or {}).get("identityLimits") or []
                     if isinstance(x, dict) and x.get("capturedAgainst")), None)

    # ---- plan 1 measures the evidence once, freshly, on its own arm.
    src_manifest, src_doc = origin_import(tmp / f"{case}-source", plan1, a1)
    imp(run1, src_manifest, 0, "source-fresh")
    source_sha = sha_of(run1 / "imports" / f"{IMPORT_CHECK}__{a1['armId']}" / "result.json")
    src = {"planId": plan1["planId"], "armId": a1["armId"], "resultSha256": source_sha}
    src_request = (src_manifest.parent / "request.jsonl").read_text(encoding="utf-8")

    def retained(folder_tag, plan, arm, dest_arm, **kw):
        """A reuse envelope for `dest_arm` whose configuration is built by the SOURCE arm's helper, so it keeps
        the source capture's own `Configuration.Source`. The destination-arm helper cannot produce this shape,
        and a real retained capture always has it."""
        manifest, doc = origin_import(tmp / f"{case}-{folder_tag}", plan, arm, origin="reused",
                                      request=src_request, captured=src_doc["capturedUtc"],
                                      reused_from=kw.pop("reused_from", src))
        doc["planId"], doc["armId"] = plan2["planId"], dest_arm["armId"]
        for k, v in kw.items():
            doc[k] = v
        return write_doc(manifest, doc), doc

    # ---- the accepted case: cross-evaluation reuse whose capture names the SOURCE arm's candidate.
    ok, ok_doc = retained("cross-eval", plan1, a1, a2)
    expect(ok_doc["configuration"]["source"], f"candidate:{a1['candidateFile']}",
           f"{case}: premise - the envelope keeps the SOURCE capture's candidate name", "reuse-filename")
    check(ok_doc["configuration"]["source"] != f"candidate:{a2['candidateFile']}",
          f"{case}: premise - and that is not the destination arm's candidate name")
    expect(import_fingerprint(ok_doc["configuration"]), ok_doc["configuration"]["fingerprint"],
           f"{case}: premise - the envelope's fingerprint is the digest of what it states")
    imp(run2, ok, 0, "cross-eval-retained-source")
    status, rc, folder = import_state(run2, IMPORT_CHECK, a2["armId"])
    expect(status, "completed", f"{case}/cross-eval: ledger completed", "reuse-filename")
    check(folder, f"{case}/cross-eval: the evidence was copied into imports/")
    expect((rc.get("validation") or {}).get("accepted"), True, f"{case}/cross-eval: the receipt is accepted")
    expect((rc.get("validation") or {}).get("rejections"), [], f"{case}/cross-eval: with no rejections")
    # provenance: the capture's own statement survives, unedited
    expect(((rc.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a1['candidateFile']}",
           f"{case}/cross-eval: the receipt keeps the ORIGINAL capture source, not the destination's",
           "reuse-filename")
    lim = limit_row(rc)
    check(lim is not None, f"{case}/cross-eval: the filename difference is recorded as an identity LIMIT")
    if lim:
        expect(lim.get("capturedAgainst"), f"candidate:{a1['candidateFile']}",
               f"{case}/cross-eval: the limit names the capture's own source", "reuse-filename")
        expect(lim.get("armCandidate"), f"candidate:{a2['candidateFile']}",
               f"{case}/cross-eval: and this arm's candidate", "reuse-filename")
        expect((lim.get("declaredSource") or {}).get("planId"), plan1["planId"],
               f"{case}/cross-eval: and the declared source plan")
    imported = rc.get("imported") or {}
    expect(imported.get("origin"), "reused", f"{case}/cross-eval: origin reused")
    expect(imported.get("reusedFrom"), src, f"{case}/cross-eval: the declared source is retained verbatim")
    expect(imported.get("reusedFromVerified"), False, f"{case}/cross-eval: still declared, not verified")
    expect(imported.get("reusedWithin"), "another-evaluation", f"{case}/cross-eval: from another evaluation")
    check("originCheck" not in imported, f"{case}/cross-eval: a reused import claims no capture-token check")

    # ---- the SAME wrong source declared FRESH is still refused by name. This is the both-direction control: one
    #      field of the envelope decides, and the strict rule is untouched for everything that is not a reuse.
    fresh_wrong, fresh_doc = origin_import(tmp / f"{case}-fresh-wrong-source", plan2, c2)
    before_fp = fresh_doc["configuration"]["fingerprint"]
    fresh_doc["configuration"]["source"] = f"candidate:{a1['candidateFile']}"
    expect(import_fingerprint(fresh_doc["configuration"]), before_fp,
           f"{case}: premise - Source is not a fingerprint input, so nothing else changed", "reuse-filename")
    imp(run2, write_doc(fresh_wrong, fresh_doc), 2, "fresh-wrong-source", text="Configuration.Source")
    untouched(run2, c2, "fresh-wrong-source")

    # ---- a bare `origin: reused` with no source declaration never reaches the exception.
    bare, bare_doc = retained("bare-reuse", plan1, a1, c2)
    bare_doc.pop("reusedFrom", None)
    # Both clauses matter: `reusedFrom` shows the origin check refused it, and `Configuration.Source` shows
    # the strict filename rule still ran. With the exception wrongly reached, the second clause disappears and
    # the exit code and the first clause would not notice (review 2, 2026-09-17).
    imp(run2, write_doc(bare, bare_doc), 2, "bare-reused", text=["reusedFrom", "Configuration.Source"])
    untouched(run2, c2, "bare-reused")

    # ---- self-reuse still refuses, retained source or not.
    self_m, self_doc = retained("self-reuse", plan1, a1, c2,
                                reused_from={"planId": plan2["planId"], "armId": c2["armId"],
                                             "resultSha256": source_sha})
    imp(run2, write_doc(self_m, self_doc), 2, "self-reuse", text=["reusedFrom", "Configuration.Source"])
    untouched(run2, c2, "self-reuse")

    # ---- a changed configuration still refuses on its OWN cause, never on the filename: relaxing (f) did not
    #      swallow the content checks.
    bad_m, bad_doc = retained("changed-config", plan1, a1, c2)
    bad_doc["configuration"]["plugins"][0]["sha256"] = "0" * 64
    bad_doc["configuration"]["fingerprint"] = import_fingerprint(bad_doc["configuration"])
    imp(run2, write_doc(bad_m, bad_doc), 2, "changed-configuration", text="identity-mismatch",
        absent="Configuration.Source")
    untouched(run2, c2, "changed-configuration")

    # ---- a changed ORDER (the same bytes) still refuses.
    ord_m, ord_doc = retained("changed-order", plan1, a1, c2)
    ord_doc["configuration"]["plugins"] = list(reversed(ord_doc["configuration"]["plugins"]))
    ord_doc["configuration"]["fingerprint"] = import_fingerprint(ord_doc["configuration"])
    imp(run2, write_doc(ord_m, ord_doc), 2, "changed-order", text="identity-mismatch",
        absent="Configuration.Source")
    untouched(run2, c2, "changed-order")

    # ---- the raw evidence still has to hash to what the envelope says.
    stale_m, _ = retained("changed-evidence", plan1, a1, c2)
    (stale_m.parent / "response.jsonl").write_text(
        '{"jsonrpc": "2.0", "id": 1, "result": {"findings": 9}}\n', encoding="utf-8")
    imp(run2, stale_m, 2, "changed-evidence", text="output-stale")
    untouched(run2, c2, "changed-evidence")

    # ---- a configuration that is a DIFFERENT setup's is refused even with a retained source and a well-formed
    #      declaration: the destination's frozen request decides, not the source's name.
    wrong_dest, wrong_doc = retained("wrong-destination", plan1, a1, c2)
    wrong_doc["configuration"]["plugins"] = [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]}
                                             for p in d2["requested"]["plugins"]]
    wrong_doc["configuration"]["fingerprint"] = import_fingerprint(wrong_doc["configuration"])
    imp(run2, write_doc(wrong_dest, wrong_doc), 2, "wrong-destination-configuration", text="identity-mismatch")
    untouched(run2, c2, "wrong-destination-configuration")

    # ---- the disclosure must not be skippable by shaping or omitting the source string. Both of these were
    #      credited with NOTHING recorded before the disclosure was separated from the refusal predicate: a source
    #      that CONTAINS this arm's candidate stem (the refusal predicate tolerates a substring), and an emit with
    #      no Configuration.Source at all (that predicate needs a truthy source).
    stem_arm = arm_by_stem(plan2, "candidate-06-stem")
    none_arm = arm_by_stem(plan2, "candidate-05-no-source")
    cross_arm = arm_by_stem(plan2, "candidate-07-cross-unknown")
    check(bool(stem_arm) and bool(none_arm) and bool(cross_arm),
          f"{case}: premise - plan 2 has the three extra destinations")
    if stem_arm:
        stem_m, stem_doc = retained("stem-swallow", plan1, a1, stem_arm)
        swallowed = f"candidate:{stem_arm['candidateFile'].rsplit('.', 1)[0]}-alternate.json"
        stem_doc["configuration"]["source"] = swallowed
        check(stem_arm["candidateFile"].rsplit(".", 1)[0].lower() in swallowed.lower()
              and swallowed != f"candidate:{stem_arm['candidateFile']}",
              f"{case}: premise - the source CONTAINS this arm's candidate stem and is still another file")
        imp(run2, write_doc(stem_m, stem_doc), 0, "stem-swallow-reuse")
        _, rc_stem, _ = import_state(run2, IMPORT_CHECK, stem_arm["armId"])
        lim_stem = limit_row(rc_stem)
        check(lim_stem is not None,
              f"{case}/stem-swallow: the difference is recorded even where the refusal predicate tolerates it")
        if lim_stem:
            expect(lim_stem.get("capturedAgainst"), swallowed,
                   f"{case}/stem-swallow: and names the capture's own source", "reuse-filename")
    if none_arm:
        none_m, none_doc = retained("no-source", plan1, a1, none_arm)
        none_doc["configuration"].pop("source", None)
        imp(run2, write_doc(none_m, none_doc), 0, "no-source-reuse")
        _, rc_none, _ = import_state(run2, IMPORT_CHECK, none_arm["armId"])
        stated = [x for x in (rc_none.get("validation") or {}).get("identityLimits") or []
                  if isinstance(x, dict) and "NO candidate source" in str(x.get("detail"))]
        check(bool(stated),
              f"{case}/no-source: a reuse stating no source is credited only with THAT recorded as a limit "
              f"(got {(rc_none.get('validation') or {}).get('identityLimits')!r})")
        check(limit_row(rc_none) is None,
              f"{case}/no-source: and it names no file, because the capture named none")

    # ---- a WITHIN-evaluation source must be a real arm of this plan whose frozen request is this arm's.
    #      Without that, a reader is told the source setup's tested configuration is the same while the plan
    #      itself says it is not. The cross-evaluation case is deliberately not checked this way.
    ghost = retained("ghost-source", plan1, a1, c2,
                     reused_from={"planId": plan2["planId"], "armId": "candidate-99-not-an-arm",
                                  "resultSha256": source_sha})[0]
    imp(run2, ghost, 2, "within-source-not-an-arm", text="no such arm")
    untouched(run2, c2, "within-source-not-an-arm")
    other = retained("other-config-source", plan1, a1, c2,
                     reused_from={"planId": plan2["planId"], "armId": d2["armId"],
                                  "resultSha256": source_sha})[0]
    imp(run2, other, 2, "within-source-other-configuration", text="FROZEN request is not this arm's")
    untouched(run2, c2, "within-source-other-configuration")
    if cross_arm:
        # the same unknown arm id, declared against ANOTHER plan, is accepted: that plan is not read here, and
        # the reader's wording claims correspondingly less.
        cross = retained("cross-unknown-arm", plan1, a1, cross_arm,
                         reused_from={"planId": plan1["planId"], "armId": "candidate-99-not-an-arm",
                                      "resultSha256": source_sha})[0]
        imp(run2, cross, 0, "cross-eval-unknown-arm-allowed")
        _, rc_cross, _ = import_state(run2, IMPORT_CHECK, cross_arm["armId"])
        expect((rc_cross.get("imported") or {}).get("reusedWithin"), "another-evaluation",
               f"{case}/cross-unknown-arm: recorded as reuse from another evaluation", "reuse-filename")

    # ---- sibling-setup reuse with a retained source: the same exception, the other `reusedWithin` statement.
    sib_src = {"planId": plan2["planId"], "armId": a2["armId"],
               "resultSha256": sha_of(run2 / "imports" / f"{IMPORT_CHECK}__{a2['armId']}" / "result.json")}
    sib, sib_doc = retained("sibling", plan2, a2, c2, reused_from=sib_src)
    expect(sib_doc["configuration"]["source"], f"candidate:{a2['candidateFile']}",
           f"{case}: premise - the sibling envelope names the SOURCE setup's candidate", "reuse-filename")
    imp(run2, sib, 0, "sibling-retained-source")
    status_c2, rc_c2, _ = import_state(run2, IMPORT_CHECK, c2["armId"])
    expect(status_c2, "completed", f"{case}/sibling: ledger completed", "reuse-filename")
    expect(((rc_c2.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a2['candidateFile']}",
           f"{case}/sibling: the receipt keeps the source setup's candidate name", "reuse-filename")
    check(limit_row(rc_c2) is not None, f"{case}/sibling: the filename difference is recorded as a limit")
    expect((rc_c2.get("imported") or {}).get("reusedWithin"), "this-evaluation",
           f"{case}/sibling: reuse WITHIN this evaluation", "reuse-filename")

    # ---- the other arm of the filename conditional: a reuse whose capture names a file with the DESTINATION
    #      arm's own name. Rule (f) is satisfied, so there is no limit to record and no reader is told anything
    #      extra - "not measured for this setup" must not be followed by "measured against this setup's own file".
    e2 = arm_by_stem(plan2, "candidate-04-same-name")
    check(bool(e2), f"{case}: premise - plan 2 has a same-name arm")
    if e2:
        same_name, same_doc = origin_import(tmp / f"{case}-same-name", plan2, e2, origin="reused",
                                            request=src_request, captured=src_doc["capturedUtc"],
                                            reused_from=sib_src)
        expect(same_doc["configuration"]["source"], f"candidate:{e2['candidateFile']}",
               f"{case}: premise - this envelope names the DESTINATION arm's own candidate", "reuse-filename")
        imp(run2, same_name, 0, "same-name-reuse")
        status_e2, rc_e2, _ = import_state(run2, IMPORT_CHECK, e2["armId"])
        expect(status_e2, "completed", f"{case}/same-name: an ordinary reuse is accepted", "reuse-filename")
        check(limit_row(rc_e2) is None,
              f"{case}/same-name: no filename limit is recorded when the names agree", )

    # ---- lifecycle: the acceptance survives resume and a ledger rebuild, and finalize reports it as reuse.
    for args, tag in ((["resume", "--run", run2], "resume"),
                      (["resume", "--run", run2, "--rebuild-ledger"], "rebuild-ledger")):
        expect(evalrun(args, scen2).returncode, 0, f"{case}/{tag}: exit", "reuse-filename")
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            st = import_state(run2, IMPORT_CHECK, arm["armId"])[0]
            check(st in CREDITED, f"{case}/{tag}: the {who} reuse still stands (got {st!r})")
        # A credited STATUS is not a surviving RESULT. `finalize` reads the detector from the ledger while
        # reading everything else from the receipt, so an entry that keeps `completed`/`reused` through a
        # reconstruction but loses its detector finalizes as `detector-undetermined` - an accepted import
        # reported as a missing check. The status assertion above cannot see that; this one can, and this is
        # the assertion whose absence let the reconstruction gap ship (Piranha's handback, 2026-09-17).
        expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0,
               f"{case}/{tag}: finalize exit", "reuse-filename")
        disp_after = load(run2 / "disposition.json") or {}
        missing_after = {(m.get("checkId"), m.get("armId"))
                         for m in (disp_after.get("evaluation") or {}).get("missing") or []}
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            check((IMPORT_CHECK, arm["armId"]) not in missing_after,
                  f"{case}/{tag}: after finalize the {who} reuse is NOT reported missing "
                  f"(cause: {next((m.get('cause') for m in (disp_after.get('evaluation') or {}).get('missing') or [] if (m.get('checkId'), m.get('armId')) == (IMPORT_CHECK, arm['armId'])), None)!r})")
        credited_after = {(x.get("checkId"), x.get("armId"))
                          for x in disp_after.get("importedEvidence") or []}
        for arm, who in ((a2, "cross-eval"), (c2, "sibling")):
            check((IMPORT_CHECK, arm["armId"]) in credited_after,
                  f"{case}/{tag}: and it is still listed as credited imported evidence")
    expect(evalrun(["finalize", "--run", run2], scen2).returncode, 0, f"{case}/finalize: exit", "reuse-filename")
    disp_rows = {r.get("armId"): r for r in (load(run2 / "disposition.json") or {}).get("importedEvidence") or []}
    for arm, want_source, who in ((a2, a1["candidateFile"], "cross-eval"), (c2, a2["candidateFile"], "sibling")):
        row = disp_rows.get(arm["armId"]) or {}
        expect(row.get("origin"), "reused", f"{case}/finalize: {who} is listed as reused", "reuse-filename")
        expect(row.get("capturedAgainst"), f"candidate:{want_source}",
               f"{case}/finalize: {who} names the capture's own candidate in the disposition", "reuse-filename")
    if e2:
        row_e2 = disp_rows.get(e2["armId"]) or {}
        expect(row_e2.get("origin"), "reused", f"{case}/finalize: the same-name reuse is listed as reused")
        check("capturedAgainst" not in row_e2,
              f"{case}/finalize: and carries NO capturedAgainst, because there is no difference to report "
              f"(got {row_e2.get('capturedAgainst')!r})")
    status_text = (run2 / "STATUS.md").read_text(encoding="utf-8") if (run2 / "STATUS.md").exists() else ""
    check("not measured for this" in status_text,
          f"{case}/finalize: STATUS.md says the evidence was not measured for the setup")
    check(f"candidate:{a1['candidateFile']}" in status_text,
          f"{case}/finalize: STATUS.md names the capture's own candidate file")
    if e2:
        check(f"measured against `candidate:{e2['candidateFile']}`" not in status_text,
              f"{case}/finalize: STATUS.md never tells a reader the evidence was measured against the very "
              f"setup it also says it was not measured for")

    # ---- the bundle carries it, and a RELOCATED bundle still verifies.
    expect(evalrun(["bundle", "--run", run2], scen2).returncode, 0, f"{case}/bundle: exit", "reuse-filename")
    moved = tmp / f"{case}-bundle-moved"
    shutil.copytree(run2 / "bundle", moved)
    vr = evalrun(["verify-bundle", moved])
    expect(vr.returncode, 0, f"{case}/relocated-bundle: verify exit", "reuse-filename")
    vj = load(moved / "verify.json") or {}
    check((vj.get("bundle") or {}).get("complete") is True,
          f"{case}/relocated-bundle: the relocated bundle verifies complete")
    breceipt = load(moved / "receipts" / f"{IMPORT_CHECK}__{a2['armId']}.json") or {}
    expect(((breceipt.get("consumed") or {}).get("configuration") or {}).get("source"),
           f"candidate:{a1['candidateFile']}",
           f"{case}/relocated-bundle: the bundled receipt still names the original capture source",
           "reuse-filename")

    # ---- and tampered evidence still loses credit, reuse or not. Done LAST, on the sibling entry.
    victim = run2 / "imports" / f"{IMPORT_CHECK}__{c2['armId']}" / "02-response.jsonl"
    check(victim.exists(), f"{case}/tamper: premise - the copied raw response is where the receipt says")
    if victim.exists():
        victim.write_text('{"jsonrpc": "2.0", "id": 1, "result": {"findings": 99}}\n', encoding="utf-8")
        expect(evalrun(["resume", "--run", run2], scen2).returncode, 0, f"{case}/tamper: resume exit")
        st_bad = import_state(run2, IMPORT_CHECK, c2["armId"])[0]
        check(st_bad not in CREDITED and "invalidated" in str(st_bad),
              f"{case}/tamper: the tampered reuse loses credit (got {st_bad!r})")
        st_ok = import_state(run2, IMPORT_CHECK, a2["armId"])[0]
        check(st_ok in CREDITED, f"{case}/tamper: the untouched reuse keeps its credit (got {st_ok!r})")


def rehash_manifest_row(bundle: Path, rel: str) -> None:
    m = load(bundle / "manifest.json")
    for e in m.get("evidence") or []:
        if e.get("path") == rel:
            e["sha256"] = sha_of(bundle / rel)
            e["bytes"] = (bundle / rel).stat().st_size
    (bundle / "manifest.json").write_text(json.dumps(m, indent=2), encoding="utf-8")


def case_61(tmp):
    """C1 (4.24 phase 1): `enrich --kind record-rows` derives rows from the bundle's own import transcripts, every row
    citing the bytes it came from; it changes no disposition, claim or count, and verify-bundle checks all of that."""
    case = "case-61-enrich-record-rows"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    disp_before = (bundle / "disposition.json").read_bytes()
    b1 = tmp / f"{case}-b1"
    shutil.copytree(bundle, b1)
    r = evalrun(["enrich", "--bundle", b1, "--kind", "record-rows"])
    expect(r.returncode, 0, f"{case}: enrich exit", "C1")
    rows = [e for e in manifest_rows(b1, "record-rows") if e.get("checkId") == cid and e.get("armId") == arm["armId"]]
    expect(len(rows), 1, f"{case}: one enrichment row for the imported check and arm", "C1")
    for e in rows:
        src = e.get("derivedFrom") or []
        check(bool(src) and all((b1 / d["path"]).is_file() and sha_of(b1 / d["path"]) == d.get("sha256") for d in src),
              f"{case}: every source the enrichment cites is a bundle file with that sha256 ({src})")
        emit = load(b1 / e["path"]) or {}
        lost = [c for c in emit.get("Conflicts") or [] if c.get("Kind") == "LOST-EDIT"]
        expect([(c.get("A"), c.get("B"), c.get("Field")) for c in lost], [(names[1], names[2], "LocationReference")],
               f"{case}: the derived row", "C1")
    check((b1 / "disposition.json").read_bytes() == disp_before, f"{case}: the disposition is byte for byte unchanged")
    expect(evalrun(["verify-bundle", b1]).returncode, 0, f"{case}: the enriched copy verifies", "C1")
    again = evalrun(["enrich", "--bundle", b1, "--kind", "record-rows"])
    expect(again.returncode, 2, f"{case}: a second enrichment of the same kind is refused", "C1")
    check("already" in again.stderr and "record-rows" in again.stderr,
          f"{case}: the refusal says the bundle is already enriched ({again.stderr.strip()[-160:]!r})")
    if not rows:
        check(False, f"{case}: no enrichment row to tamper with, so the three verification arms did not run")
        return
    rel = rows[0]["path"]
    # a changed byte
    b2 = tmp / f"{case}-b2"
    shutil.copytree(b1, b2)
    (b2 / rel).write_bytes((b2 / rel).read_bytes() + b" ")
    expect(evalrun(["verify-bundle", b2]).returncode, 3, f"{case}: a changed enrichment byte fails verification", "C1")
    # a row without its source, re-hashed so only the missing source can be what verify refuses
    b3 = tmp / f"{case}-b3"
    shutil.copytree(b1, b3)
    emit = load(b3 / rel)
    emit.pop("derivedFrom", None)
    for c in emit.get("Conflicts") or []:
        for n in c.get("Necessary") or []:
            n.pop("Reads", None)
    (b3 / rel).write_text(json.dumps(emit), encoding="utf-8")
    rehash_manifest_row(b3, rel)
    v3 = evalrun(["verify-bundle", b3])
    expect(v3.returncode, 3, f"{case}: an enrichment row without its source fails verification", "C1")
    # a count changed after enrichment: the disposition no longer equals the one the enrichment was derived beside
    b4 = tmp / f"{case}-b4"
    shutil.copytree(b1, b4)
    d = load(b4 / "disposition.json")
    for f in (d.get("evaluation") or {}).get("findings") or []:
        if f.get("checkId") == cid:
            f["findings"] = int(f.get("findings") or 0) + 5
    (b4 / "disposition.json").write_text(json.dumps(d, indent=2), encoding="utf-8")
    rehash_manifest_row(b4, "disposition.json")
    expect(evalrun(["verify-bundle", b4]).returncode, 3,
           f"{case}: a count changed beside an enrichment fails verification", "C1")


def case_65(tmp):
    """4.24 phase 1: an import may carry a third output, `rows`, in the conflict-file shape; rows naming a plugin that
    is not in the arm are refused; an accepted rows output travels in the bundle."""
    case = "case-65-import-rows-output"
    c = "case-01-geometry-and-quest"
    run = tmp / f"{case}-run"
    expect(do_plan(run, c).returncode, 0, f"{case}: plan exit", "setup")
    _, _, _, scen = case_paths(c)
    expect(evalrun(["run", "--run", run], scen).returncode, 0, f"{case}: accounting run", "setup")
    run = to_overlay(run, scen, case)
    plan = load(run / "plan.json") or {}
    checks = plan_checks(plan)
    cid = next((x for x in ("records.contested-fields", "records.cell-attributes", "loadability.dangling-references")
                if ((checks.get(x) or {}).get("adapter") or {}).get("kind") == "mcp-only"), None)
    arm_ids = [x.get("armId") for x in ((checks.get(cid) or {}).get("arms") or []) if x.get("status") == "mcp-only"]
    arms = [a for a in plan.get("arms") or [] if a.get("armId") in arm_ids]
    names = [p["name"] for p in arms[0]["requested"]["plugins"]]

    def rows_file(folder, a, b):
        folder.mkdir(parents=True, exist_ok=True)
        f = folder / "rows.json"
        f.write_text(json.dumps({"Population": "contested records", "State": "baseline",
                                 "Coverage": [{"Population": "contested records", "Judged": 1, "Unjudgeable": {}}],
                                 "Conflicts": [{"Kind": "LOST-EDIT", "A": a, "B": b, "Field": "LocationReference",
                                                "Summary": "x", "Necessary": [{"FormKey": "000801:Skyrim.esm"}]}]}),
                     encoding="utf-8")
        return f

    bad_dir = tmp / f"{case}-bad"
    bad, _ = record_import_doc(bad_dir, plan, cid, arms[0], rows=rows_file(bad_dir, names[1], "NotInTheArm.esp"))
    rb = evalrun(["import-results", "--run", run, "--file", bad])
    expect(rb.returncode, 2, f"{case}: rows naming a plugin outside the arm are refused", "§4.8 rows")
    check("NotInTheArm.esp" in rb.stderr, f"{case}: the refusal names the plugin ({rb.stderr.strip()[-200:]!r})")
    good_dir = tmp / f"{case}-good"
    good, _ = record_import_doc(good_dir, plan, cid, arms[0], rows=rows_file(good_dir, names[1], names[2]))
    expect(evalrun(["import-results", "--run", run, "--file", good]).returncode, 0,
           f"{case}: an import with a rows output is accepted", "§4.8 rows")
    rc = load(run / "receipts" / f"{cid}__{arms[0]['armId']}.json") or {}
    expect(len(rc.get("outputs") or []), 4, f"{case}: the manifest, request, response and rows are retained", "§4.8")
    expect(evalrun(["finalize", "--run", run]).returncode, 0, f"{case}: finalize", "§4.8")
    bundle = tmp / f"{case}-bundle"
    expect(evalrun(["bundle", "--run", run, "--out", bundle]).returncode, 0, f"{case}: bundle", "§4.8")
    check(any(str(e.get("path") or "").endswith("rows.json") for e in (load(bundle / "manifest.json") or {})
              .get("evidence") or []), f"{case}: the rows output is in the bundle")
    expect(evalrun(["verify-bundle", bundle]).returncode, 0, f"{case}: verify-bundle", "§4.8")


def case_800(tmp):
    """Item 18 phase 3: `enrich --kind order-variant` is a kind the module's recordenrich takes. On case-01's evaluation it either derives
    the reversed order's rows (exit 0, a manifest row of that kind, and the copy still verifies) or refuses in one
    sentence that says why the order cannot be reversed (exit 2); never a usage error."""
    case = "case-800-enrich-order-variant"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    b1 = tmp / f"{case}-b1"
    shutil.copytree(bundle, b1)
    r = evalrun(["enrich", "--bundle", b1, "--kind", "order-variant"])
    check("invalid choice" not in r.stderr, f"{case}: the kind is accepted ({r.stderr.strip()[-200:]!r})")
    if r.returncode == 0:
        rows = manifest_rows(b1, "order-variant")
        check(bool(rows), f"{case}: an order-variant row is in the manifest")
        expect(evalrun(["verify-bundle", b1]).returncode, 0, f"{case}: the enriched copy verifies", "phase 3")
    else:
        expect(r.returncode, 2, f"{case}: a refusal, not a crash ({r.stderr.strip()[-200:]!r})", "phase 3")
        check("invalid choice" not in r.stderr and "order" in r.stderr.lower() and r.stderr.strip().count(". ") <= 1,
              f"{case}: one sentence saying why the order cannot be reversed ({r.stderr.strip()[-200:]!r})")


# ---- moved from test_evalrun.py (end)


def case_802(tmp):
    """The report's load-order split ("fields on records the declared patch has a version of") reads the patched
    setup's record transcripts. Since the record checks left the app's registry those imports are in the AI-side
    overlay, not in the app bundle's folder: on a joined bundle evalreport.record_imports must find the overlay's
    credited import (the wrong answer it rejects: reading only the app bundle's folder, finding none, and the split
    silently counting nothing)."""
    import importlib.util
    case = "case-802-report-reads-overlay-record-imports"
    bundle, plan, cid, arm, names = recorded_bundle(tmp, case)
    app = tmp / f"{case}-run" / "ai-app-bundle"
    check((app / "manifest.json").is_file(), f"{case}: premise - the app bundle the overlay was planned over is at {app}")
    spec = importlib.util.spec_from_file_location("evalreport_case_802", SW.MODULE / "evalreport.py")
    er = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(er)
    er.AI_BUNDLES[str(app.resolve())] = bundle.resolve()
    joined = er.Bundle(app)
    check(joined.overlay is not None, f"{case}: premise - the app bundle is read joined with its overlay")
    found = [(rc.get("checkId"), rc.get("armId")) for rc, *_ in er.record_imports(joined)]
    expect(found, [(cid, arm["armId"])],
           f"{case}: the joined bundle's credited record imports are the overlay's one import", "load-order split")
    er.AI_BUNDLES.clear()
    expect([(rc.get("checkId"), rc.get("armId")) for rc, *_ in er.record_imports(er.Bundle(app))], [],
           f"{case}: the app bundle read alone holds no record import (the premise of the overlay read)", "load-order split")
