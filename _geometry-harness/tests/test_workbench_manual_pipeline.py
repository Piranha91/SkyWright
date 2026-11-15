"""Known answers for the workbench's manual mode in the pipeline and the controller: an in-place entry (a profile of
any MO2 instance, a selection of its mod folders) evaluated as "the profile without the selection" (evalrun's baseline)
against "the profile as it is" (the review arm), with no profile written.

Dependency-free: `python -I _geometry-harness/tests/test_workbench_manual_pipeline.py` (exit 0 pass, 1 fail).
EVALPIPELINE_PATH / EVALRUN_PATH load the modules from other files. Plugins are synthetic header-only files.
Cases that run a probe need the worktree's Release scopeprobe.

The in-place entry (DESIGN.md 2.1 and Round 2):
    {"id", "inPlace": {"instance", "profile", "game"}, "selection": {"mods": [...]},
     "evaluation": {"select": [...], "depth"?, "primitiveSolidity"?}}
"""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HARNESS.parent / "scripts"))


def load(name, env, default):
    spec = importlib.util.spec_from_file_location(name, os.environ.get(env) or str(default))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


EP = load("evalpipeline_under_test", "EVALPIPELINE_PATH", HARNESS / "evalpipeline.py")
ER = load("evalrun_under_test", "EVALRUN_PATH", HARNESS / "evalrun.py")
SCOPEPROBE = HARNESS / "scopeprobe" / "bin" / "Release" / "net9.0" / "scopeprobe.exe"
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {str(e)[:600]}")
        if not isinstance(e, AssertionError):
            traceback.print_exc(limit=3)


def write_plugin(path: Path, masters=(), esm=False):
    body = b""
    for m in masters:
        z = m.encode("cp1252") + b"\0"
        body += b"MAST" + len(z).to_bytes(2, "little") + z + b"DATA" + (8).to_bytes(2, "little") + bytes(8)
    hedr = b"HEDR" + (12).to_bytes(2, "little") + bytes.fromhex("48e1da3f") + bytes(4) + (0x800).to_bytes(4, "little")
    body = hedr + body
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"TES4" + len(body).to_bytes(4, "little") + (1 if esm else 0).to_bytes(4, "little")
                     + bytes(8) + (44).to_bytes(2, "little") + bytes(2) + body)


def world(t: Path, mods: dict, enabled: list, plugins: list, data=("Base.esm",)):
    """An MO2 instance at t/instance with game t/game: mods {folder: {plugin: masters}}; enabled folders strongest
    first; plugins in load order (all active). Returns (instance, game)."""
    inst, game = t / "instance", t / "game"
    for p in data:
        write_plugin(game / "Data" / p, esm=True)
    for folder, files in mods.items():
        (inst / "mods" / folder).mkdir(parents=True, exist_ok=True)
        for p, masters in files.items():
            write_plugin(inst / "mods" / folder / p, masters, esm=p.lower().endswith(".esm"))
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (prof / "modlist.txt").write_text("".join(f"+{m}\n" for m in enabled)
                                      + "".join(f"-{m}\n" for m in mods if m not in enabled), encoding="utf-8")
    order = list(data) + plugins
    (prof / "loadorder.txt").write_text("".join(p + "\n" for p in order), encoding="utf-8")
    (prof / "plugins.txt").write_text("".join("*" + p + "\n" for p in plugins), encoding="utf-8")
    (inst / "overwrite").mkdir(parents=True, exist_ok=True)
    (inst / "ModOrganizer.ini").write_text("[General]\ngameName=Skyrim Special Edition\nselected_profile=@ByteArray(P)\n",
                                           encoding="utf-8")
    return inst, game


def in_place_entry(t: Path, inst, game, mods, select, **evaluation):
    e = {"schema": 1, "id": "wb-test", "inPlace": {"instance": str(inst), "profile": "P", "game": str(game)},
         "selection": {"mods": mods}, "evaluation": {"select": select, **evaluation}}
    p = t / "entry.json"
    p.write_text(json.dumps(e), encoding="utf-8")
    return p


SHADOW_MODS = {"Sel": {"Sel.esp": ["Base.esm"], "Dup.esp": ["Base.esm"]}, "Other": {"Dup.esp": ["Base.esm"]},
               "A": {"A.esp": ["Base.esm"]}}
SHADOW_ENABLED = ["Sel", "Other", "A"]
SHADOW_PLUGINS = ["A.esp", "Sel.esp", "Dup.esp"]


def listing(*roots):
    out = {}
    for r in roots:
        for d, _, files in os.walk(r):
            out[d + os.sep] = "dir"
            for f in files:
                st = os.stat(os.path.join(d, f))
                out[os.path.join(d, f)] = (st.st_size, st.st_mtime_ns)
    return out


# ---- B5: the arms of an in-place entry ----

def case_b5_in_place_arms_are_the_profile_and_the_profile_without_the_selected_folders():
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, SHADOW_MODS, SHADOW_ENABLED, SHADOW_PLUGINS)
        ctx = EP.Context(in_place_entry(t, inst, game, ["Sel"], ["geometry.object-overlap"]), None, t / "out")
        res = ctx.resolve()
        by = {a["id"]: a for a in res["arms"]}
        assert set(by) == {"combined", "without-selection"}, f"arms {sorted(by)}"
        assert by["combined"]["plugins"] == ["Base.esm", "A.esp", "Sel.esp", "Dup.esp"], by["combined"]["plugins"]
        # Dup.esp is also provided by the unselected folder Other: MO2 with Sel disabled still loads it, from Other
        assert by["without-selection"]["plugins"] == ["Base.esm", "A.esp", "Dup.esp"], by["without-selection"]["plugins"]
        assert "Sel" not in by["without-selection"]["mods"] and "Other" in by["without-selection"]["mods"]
        cands = {name: (arm["id"], doc) for name, arm, doc in EP.candidate_files(ctx, res)}
        assert cands["candidate-00-baseline.json"][0] == "without-selection", "evalrun's baseline must be the profile without the selection"
        assert cands["candidate-01.json"][0] == "combined"
        assert all(doc["BaseProfile"] == "P" for _, doc in cands.values()), "candidates must name the chosen profile"


def case_b4a_prepare_writes_no_profile_into_the_instance():
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, SHADOW_MODS, SHADOW_ENABLED, SHADOW_PLUGINS)
        ctx = EP.Context(in_place_entry(t, inst, game, ["Sel"], ["geometry.object-overlap"]), None, t / "out")
        before = listing(inst, game)
        res = ctx.resolve()
        sdir = t / "out" / "prepare"
        sdir.mkdir(parents=True)
        EP.stage_prepare(ctx, res, sdir)
        after = listing(inst, game)
        changed = sorted(set(before) ^ set(after)) + sorted(p for p in before if p in after and before[p] != after[p])
        assert not changed, f"prepare wrote into the instance or game: {changed[:5]}"


# ---- B5 (shadowed case through plan), B7, B17: what the plan stage asks evalrun for ----

def plan_argv(entry_kw, select, mods=("Sel",), world_kw=None):
    """Run stage_plan with the child captured; returns (argv, env) evalrun was started with."""
    seen = {}
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, **(world_kw or dict(mods=SHADOW_MODS, enabled=SHADOW_ENABLED, plugins=SHADOW_PLUGINS)))
        ctx = EP.Context(in_place_entry(t, inst, game, list(mods), select, **entry_kw), None, t / "out")
        res = ctx.resolve()
        (t / "out" / "scope").mkdir(parents=True)
        (t / "out" / "prepare").mkdir(parents=True)
        EP.write_json(t / "out" / "scope" / "scopes.json",
                      {a["id"]: {"scope": str(t / "scope.json"), "file": f"x-{a['id']}.json"} for a in res["arms"]})
        EP.write_json(t / "out" / "prepare" / "preparation.json", {"arms": []})
        saved = EP.child
        EP.child = lambda c, d, op, argv, **k: seen.update(argv=[str(a) for a in argv], env=k.get("env")) or {"exitCode": 9}
        try:
            EP.stage_plan(ctx, res, t / "out" / "plan")
        except EP.Refusal:
            pass
        finally:
            EP.child = saved
    assert "argv" in seen, "stage_plan never started evalrun"
    return seen["argv"], seen.get("env") or {}


def flag(argv, name):
    return [argv[i + 1] for i, a in enumerate(argv) if a == name]


def case_b7_select_is_closed_over_the_registry_prerequisites_and_derived_checks():
    argv, _ = plan_argv({}, ["state.conditional-geometry", "loadability.masters-and-order"])
    sel = {c for s in flag(argv, "--select") for c in s.split(",")}
    for need in ("state.conditional-geometry", "state.switch-census", "loadability.masters-and-order", "patch.order-evaluation"):
        assert need in sel, f"--select lacks {need}: {sorted(sel)}"
    assert "patch.candidate-qualification" not in sel, "a derived check whose prerequisite (discovery-installed) is not selected was added"
    # M5 (run s5): evalrun does not compute functional.linked-dependencies ("derived kind None is not one this controller
    # computes"), so adding it would only plan an `unsupported` row; the launcher greys it instead
    assert "functional.linked-dependencies" not in sel, "a derived check evalrun does not compute was added"
    assert flag(argv, "--depth") == ["deep"], f"a selected states:selected check needs depth deep, got {flag(argv, '--depth')}"


def case_b5b_the_plan_names_the_review_arm():
    argv, _ = plan_argv({}, ["geometry.object-overlap"])
    assert flag(argv, "--review-arm") == ["candidate-01"], f"--review-arm {flag(argv, '--review-arm')}"


def case_b17_the_primitive_table_is_explicit_never_this_machines_harness_config():
    argv, env = plan_argv({"primitiveSolidity": "C:/tables/p.json"}, ["geometry.object-overlap"])
    assert flag(argv, "--primitive-solidity") == ["C:/tables/p.json"], f"argv {argv}"
    argv, env = plan_argv({}, ["geometry.object-overlap"])
    assert not flag(argv, "--primitive-solidity")
    assert env.get("HOUSECARL_HARNESS_CONFIG") == "" and env.get("HOUSECARL_PRIMITIVE_SOLIDITY") == "", \
        f"with no table named, evalrun must not read one from this machine's harness config (env {env})"


# ---- B6b: a selection that provides no plugin ----

def case_b6b_an_asset_only_selection_has_no_target_and_a_population_of_its_folders_only():
    """H3 (Gate B design review): no plugin outside the selection becomes a target. Tested basis: evalrun plan with no
    --scope over two candidates that differ only by a folder gives targets [] and population mods [Tex], plugins []
    (receipts/h3-asset-only-plan.log). So an asset-only in-place entry plans without --scope."""
    mods = {"Tex": {}, "A": {"A.esp": ["Base.esm"]}}
    argv, _ = plan_argv({}, ["geometry.object-overlap"], mods=("Tex",),
                        world_kw=dict(mods=mods, enabled=["Tex", "A"], plugins=["A.esp"]))
    assert "--scope" not in argv, f"an asset-only selection must plan without a scope (whose targets would be another plugin): {argv}"
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, mods, ["Tex", "A"], ["A.esp"])
        ctx = EP.Context(in_place_entry(t, inst, game, ["Tex"], ["geometry.object-overlap"]), None, t / "out")
        res = ctx.resolve()
        assert res["subject"] == [] and res["keys"] == [], f"targets {res['subject']} {res['keys']}"


# ---- B6: a selected master with a dependent left behind is refused by the probes' own order check ----

def case_b6_a_selected_master_with_an_active_dependent_is_refused_naming_it():
    assert SCOPEPROBE.is_file(), f"build scopeprobe first: {SCOPEPROBE}"
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        mods = {"Lib": {"Lib.esm": ["Base.esm"]}, "Uses": {"Uses.esp": ["Base.esm", "Lib.esm"]}}
        inst, game = world(t, mods, ["Uses", "Lib"], ["Lib.esm", "Uses.esp"])
        entry = in_place_entry(t, inst, game, ["Lib"], ["geometry.object-overlap"])
        r = subprocess.run([sys.executable, str(HARNESS / "evalpipeline.py"), "run", "--entry", str(entry), "--out",
                            str(t / "out"), "--through", "scope"], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env={**os.environ, "HOUSECARL_RUNLOG_DIR": str(t / "runlog")})
        text = r.stdout + r.stderr
        assert r.returncode == EP.EXIT_REFUSED, f"exit {r.returncode}: {text[-600:]}"
        assert "MISSING MASTER" in text and "Uses.esp" in text, \
            f"the refusal must come from the order check and name the dependent Uses.esp: {text[-600:]}"


# ---- B15 (D1 a): a candidate may name a mod folder by absolute path, and the probes read it ----

def case_b15_a_candidate_mod_folder_outside_the_instance_is_read_by_the_probes():
    assert SCOPEPROBE.is_file(), f"build scopeprobe first: {SCOPEPROBE}"
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, {"A": {"A.esp": ["Base.esm"]}}, ["A"], ["A.esp"])
        out_patch = t / "output" / "houseCARL - WbPatch"
        write_plugin(out_patch / "WbPatch.esp", ["Base.esm", "A.esp"])
        cand = t / "cand.json"
        cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "with patch", "BaseProfile": "P",
                                    "Plugins": ["Base.esm", "A.esp", "WbPatch.esp"], "Mods": [str(out_patch), "A"],
                                    "Changes": ["added the patch from the output folder"]}), encoding="utf-8")
        r = subprocess.run([str(SCOPEPROBE), "--order", str(cand), "--target", "WbPatch.esp", "--json", str(t / "s.json"),
                            "--instance", str(inst), "--profile", "P", "--game", str(game)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        assert r.returncode == 0, f"scopeprobe exit {r.returncode}: {(r.stdout + r.stderr)[-500:]}"
        cfg = json.loads((t / "s.json").read_text(encoding="utf-8-sig"))["configuration"]
        got = {p["Name"]: p for p in cfg["plugins"]}
        assert Path(got["WbPatch.esp"]["Path"]).resolve() == (out_patch / "WbPatch.esp").resolve(), got
        # The folder is in the candidate's asset view, so the identity must say the plugin came from that view entry,
        # not record it as resolved "outside the MO2 layout" and NOT in the view (a false limit on every result).
        view = [v["Name"] for v in cfg["providers"] if v["Kind"] == "mod"]
        assert got["WbPatch.esp"]["Provider"] in view, f"provider {got['WbPatch.esp']['Provider']!r} is not a view entry {view}"
        false_limit = [x for x in cfg["limits"] if "NOT in this configuration's asset view" in x]
        assert not false_limit, f"a false out-of-view limit: {false_limit[0][:200]}"


# ---- B16: a manual plan's patch pairs ----

def case_b16_the_review_arms_pairs_are_each_target_with_each_plugin_it_clashes_with():
    with tempfile.TemporaryDirectory() as t:
        run = Path(t)
        (run / "receipts").mkdir()
        (run / "outputs").mkdir()
        emit = run / "outputs" / "overlap-candidate-01.json"
        emit.write_text(json.dumps({"Conflicts": [{"Kind": "OVERLAP", "A": "Sel.esp", "B": "Other.esp", "Summary": "s",
                                                   "Necessary": [], "Alternative": []}]}), encoding="utf-8")
        (run / "receipts" / "geometry.object-overlap__candidate-01__arm.json").write_text(json.dumps(
            {"checkId": "geometry.object-overlap", "armId": "candidate-01", "validation": {"accepted": True},
             "outputs": [{"path": "outputs/overlap-candidate-01.json"}]}), encoding="utf-8")
        plan = {"reviewArm": "candidate-01", "targets": [{"name": "Sel.esp"}],
                "arms": [{"armId": "baseline", "kind": "baseline", "plugins": ["Base.esm", "Other.esp"]},
                         {"armId": "candidate-01", "kind": "candidate", "plugins": ["Base.esm", "Other.esp", "Sel.esp"]}]}
        ex = SimpleNamespace(plan=plan, run=run)
        pairs = ER.all_plan_pairs(ex)
        assert sorted(map(sorted, pairs)) == [["Other.esp", "Sel.esp"]], f"pairs {pairs}"


# ---- H1 (Gate B design review): a receipt's consumed configuration is the configuration of the arm it ran on ----

def consumed(scope_source):
    plan = {"tools": {}, "registry": {"sha256": "r"}, "scope": {"sha256": "s"}, "harness": {}, "states": {},
            "configuration": {"fingerprint": "scopefp", "source": scope_source,
                                           "plugins": [{"name": "Base.esm"}, {"name": "Sel.esp"}], "providers": [],
                                           "overlays": [], "population": "load-time"},
            "arms": []}
    arm = {"armId": "baseline", "kind": "baseline", "candidateFile": "candidate-00-baseline.json",
           "requested": {"plugins": [{"name": "Base.esm", "path": "x", "sha256": "0" * 64, "hashSource": "scope"}],
                         "providers": [], "overlays": []}}
    with tempfile.TemporaryDirectory() as t:
        return ER.Executor.consumed_for(SimpleNamespace(plan=plan, run=Path(t), options_sha="o"), arm, {"exe": "depprobe"})


def case_h1_the_scopes_configuration_is_not_stamped_on_a_baseline_arm_the_scope_was_not_measured_on():
    got = consumed("candidate:candidate-01.json")          # manual mode: the scope is the profile as it is
    cfg = got["configuration"]
    names = [p.get("name") for p in cfg.get("plugins") or []]
    assert got["configurationSource"] != "scope" and cfg.get("fingerprint") != "scopefp" and "Sel.esp" not in names, \
        f"the baseline arm's receipt records the review arm's scope ({got['configurationSource']}, {cfg.get('fingerprint')}, {names})"


def case_h1b_a_scope_measured_on_the_baseline_candidate_is_still_recorded_verbatim():
    got = consumed("candidate:candidate-00-baseline.json")  # the pipeline: the scope is the baseline (combined) arm's
    assert got["configurationSource"] == "scope" and got["configuration"]["fingerprint"] == "scopefp", got["configurationSource"]


# ---- M4: the review arm's pair source applies only when the plan names a review arm ----

def pair_ex(t: Path, review, with_receipt=True):
    run = t / ("run-" + str(review))
    (run / "receipts").mkdir(parents=True)
    (run / "outputs").mkdir()
    if with_receipt:
        (run / "outputs" / "o.json").write_text(json.dumps({"Conflicts": [{"Kind": "OVERLAP", "A": "Sel.esp", "B": "Other.esp",
                                                                          "Summary": "s", "Necessary": [], "Alternative": []}]}))
        (run / "receipts" / "geometry.object-overlap__candidate-01__arm.json").write_text(json.dumps(
            {"checkId": "geometry.object-overlap", "armId": "candidate-01", "validation": {"accepted": True},
             "outputs": [{"path": "outputs/o.json"}]}))
    plan = {"targets": [{"name": "Sel.esp"}],
            "arms": [{"armId": "baseline", "kind": "baseline", "plugins": ["Base.esm", "Other.esp", "Sel.esp"]},
                     {"armId": "candidate-01", "kind": "candidate", "plugins": ["Base.esm", "Other.esp", "Sel.esp"]}]}
    if review:
        plan["reviewArm"] = review
    return SimpleNamespace(plan=plan, run=run)


def case_m4a_a_plan_without_a_review_arm_keeps_todays_pairs():
    with tempfile.TemporaryDirectory() as t:
        assert ER.all_plan_pairs(pair_ex(Path(t), None)) == [], "a pipeline plan with no order flip must stay without pairs"


def case_m4b_with_no_geometry_receipt_the_launcher_gets_a_sentence_naming_the_missing_input():
    with tempfile.TemporaryDirectory() as t:
        ex = pair_ex(Path(t), "candidate-01", with_receipt=False)
        assert ER.all_plan_pairs(ex) == []
        why = ER.pair_source_sentence(ex)
        assert "geometry" in why and why.endswith(".") and "\n" not in why, why


# ---- L1: the Python twins of the order reader agree on an absolute Mods entry ----

def case_l1a_plugin_layers_finds_a_plugin_in_an_absolute_view_folder_first():
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, {"A": {"A.esp": ["Base.esm"]}}, ["A"], ["A.esp"])
        out_patch = t / "output" / "houseCARL - WbPatch"
        write_plugin(out_patch / "WbPatch.esp", ["Base.esm", "A.esp"])
        got = ER.plugin_layers("WbPatch.esp", [str(out_patch), "A"], instance=str(inst), profile="P", game=str(game))
        rows = got["rows"]
        assert rows and Path(rows[0]["path"]).resolve() == (out_patch / "WbPatch.esp").resolve(), rows
        assert got["views"][0] == "in-view", got["views"]


def case_l1b_a_population_takes_a_mod_folder_named_by_absolute_path():
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, {"A": {"A.esp": ["Base.esm"]}}, ["A"], ["A.esp"])
        out_patch = t / "output" / "houseCARL - WbPatch"
        write_plugin(out_patch / "WbPatch.esp", ["Base.esm", "A.esp"])
        f = out_patch / "WbPatch.esp"
        made = ER.population_file([{"name": f.name, "path": str(f), "sha256": ER.sha256_file(f)}], [str(out_patch)],
                                  [f.name], str(inst))
        assert not isinstance(made, str), f"refused: {made}"
        assert Path(made[3][0]["path"]).resolve() == out_patch.resolve(), made[3]


def main():
    for name, fn in sorted((k, v) for k, v in globals().items() if k.startswith("case_")):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
