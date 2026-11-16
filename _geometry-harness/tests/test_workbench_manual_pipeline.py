"""Known answers for the workbench's manual mode in the pipeline and the controller: an in-place entry (a profile of
any MO2 instance, a selection of its mod folders) evaluated as "the profile without the selection" (evalrun's baseline)
against "the profile as it is" (the review arm), with no profile written.

Dependency-free: `python -I _geometry-harness/tests/test_workbench_manual_pipeline.py` (exit 0 pass, 1 fail).
EVALPIPELINE_PATH / EVALRUN_PATH load the modules from other files. Plugins are synthetic header-only files.
Cases that run a probe need the worktree's Release scopeprobe.

The in-place entry:
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
    # MO2 lists every active plugin in plugins.txt except the official masters it loads implicitly; a synthetic master
    # in the game's Data is not one of those, so it is listed (Round 4 correction of this fixture)
    (prof / "plugins.txt").write_text("".join("*" + p + "\n" for p in list(data) + plugins), encoding="utf-8")
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


SHADOW_MODS = {"Sel": {"Sel.esp": ["Base.esm"], "Dup.esp": ["Base.esm"], "Off.esp": ["Base.esm"]},
               "Other": {"Dup.esp": ["Base.esm"]}, "A": {"A.esp": ["Base.esm"]}}
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


def case_d1_a_selected_disabled_mod_is_enabled_in_the_candidate_only_with_its_plugins_last():
    """Piranha leaves a mod disabled until it is judged safe: selecting it tests the profile with it enabled at its MO2
    priority (its plugins at the end of the load order, a master-flagged one at the end of the masters) against the
    profile as it stands. The profile's files are not changed."""
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        mods = {"Top": {"Top.esp": ["Base.esm"]}, "Off": {"Off.esp": ["Base.esm"], "OffLib.esm": ["Base.esm"]},
                "Low": {"Low.esp": ["Base.esm"]}}
        inst, game = world(t, mods, ["Top", "Low"], ["Low.esp", "Top.esp"])
        prof = Path(inst) / "profiles" / "P"
        # MO2's own priority: Top strongest, then the disabled Off, then Low
        (prof / "modlist.txt").write_text("".join(x + chr(10) for x in ("+Top", "-Off", "+Low")), encoding="utf-8")
        before = listing(prof)
        ctx = EP.Context(in_place_entry(t, inst, game, ["Off"], ["geometry.object-overlap"]), None, t / "out")
        res = ctx.resolve()
        by = {a["id"]: a for a in res["arms"]}
        assert by["combined"]["mods"] == ["Top", "Off", "Low"], by["combined"]["mods"]
        assert by["combined"]["plugins"] == ["Base.esm", "OffLib.esm", "Low.esp", "Top.esp", "Off.esp"], by["combined"]["plugins"]
        assert by["combined"]["switchedOn"] == ["Off"]
        assert "Off" not in by["without-selection"]["mods"], by["without-selection"]["mods"]
        assert by["without-selection"]["plugins"] == ["Base.esm", "Low.esp", "Top.esp"], by["without-selection"]["plugins"]
        assert sorted(res["subject"]) == ["Off.esp", "OffLib.esm"], res["subject"]
        assert listing(prof) == before, "the profile's files changed"


def case_r1_an_earlier_preparation_is_reused_only_when_every_input_matches():
    """A manual run takes the census, setup and load-order checks over from an earlier run in the Work Folder only when
    the instance, profile, selection, profile files, every loaded folder's files and the code are the same; a touched
    plugin, a changed profile file or another selection each runs fresh and says which part differed."""
    import evalprepreuse as PR
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, SHADOW_MODS, SHADOW_ENABLED, SHADOW_PLUGINS)
        work = t / "work"
        # a stand-in harness and probe package, and a disabled mod's plugin the scope stage would also read
        harness = t / "harness"; harness.mkdir()
        (harness / "evalpipeline.py").write_text("x", encoding="utf-8")
        pkg = t / "pkg"; pkg.mkdir()
        (pkg / "scopeprobe.exe").write_bytes(b"exe")
        (pkg / "housecarl-core.dll").write_bytes(b"core")
        corpus = t / "corpus.json"; corpus.write_text("{}", encoding="utf-8")
        offplug = t / "offmod" / "Off.esp"
        write_plugin(offplug, ["Base.esm"])

        def keyed(sel):
            ctx = EP.Context(in_place_entry(t, inst, game, sel, ["geometry.object-overlap"]), None, t / "o")
            res = ctx.resolve()
            tools = PR.tools_digest(harness, pkg / "scopeprobe.exe", corpus)
            return PR.key(ctx.instance, ctx.profile, ctx.game, res["selection"], res["arms"], tools, also_scanned=[offplug]), res["arms"]

        def earlier(name, sel):
            run = work / name / "run"
            k, arms = keyed(sel)
            for st in PR.STAGES:
                (run / st).mkdir(parents=True)
                (run / st / "out.json").write_text(json.dumps({"path": str(run / st / "out.json")}), encoding="utf-8")
            (run / "stages.json").write_text(json.dumps({"stages": {st: {"accepted": True, "outputs": {str(run / st / "out.json"): "x"},
                                                                         "bound": {"at": str(run)}} for st in PR.STAGES}}), encoding="utf-8")
            (run / "arms.json").write_text(json.dumps(arms), encoding="utf-8")
            (run / PR.KEY_FILE).write_text(json.dumps(k), encoding="utf-8")
            return run

        old = earlier("20260101T000000Z", ["Sel"])
        new = work / "20260101T000100Z" / "run"
        new.mkdir(parents=True)
        k, arms = keyed(["Sel"])
        found, why = PR.find(work, new, k, arms)
        assert found is not None and found.resolve() == old.resolve(), f"a matching run was not reused: {why}"
        seeded = PR.seed(found, new)
        copied = json.loads((new / "census" / "out.json").read_text(encoding="utf-8"))["path"]
        assert str(new.resolve()) in copied and str(old.resolve()) not in copied, f"the earlier folder's path stayed: {copied}"
        assert all(str(new.resolve()) in p for p in seeded["scope"]["outputs"]), seeded["scope"]["outputs"]

        # another selection: refused, naming it
        k2, arms2 = keyed(["A"])
        found, why = PR.find(work, new, k2, arms2)
        assert found is None and "profile and selection" in why, why

        # a touched plugin in a loaded folder: refused
        plug = Path(inst) / "mods" / "Sel" / "Sel.esp"
        os.utime(plug, ns=(plug.stat().st_atime_ns, plug.stat().st_mtime_ns + 10_000_000_000))
        k3, arms3 = keyed(["Sel"])
        found, why = PR.find(work, new, k3, arms3)
        assert found is None and "mod folder" in why, why
        os.utime(plug, ns=(plug.stat().st_atime_ns, plug.stat().st_mtime_ns - 10_000_000_000))
        assert PR.find(work, new, *keyed(["Sel"]))[0] is not None, "restoring the plugin's time did not match again"

        # a disabled mod's plugin the scope stage reads changed: refused
        offplug.write_bytes(offplug.read_bytes() + bytes(1))
        found, why = PR.find(work, new, *keyed(["Sel"]))
        assert found is None and "disabled mod" in why, why
        write_plugin(offplug, ["Base.esm"])
        assert PR.find(work, new, *keyed(["Sel"]))[0] is not None, "restoring the disabled mod's plugin did not match again"

        # another file of the probe's package changed (not the exe): refused
        (pkg / "housecarl-core.dll").write_bytes(b"core2")
        found, why = PR.find(work, new, *keyed(["Sel"]))
        assert found is None and "scopeprobe build" in why, why
        (pkg / "housecarl-core.dll").write_bytes(b"core")

        # a plugin replaced keeping its size and mtime: refused (plugins are hashed)
        st = plug.stat()
        data = bytearray(plug.read_bytes()); data[-1] ^= 0xFF; plug.write_bytes(bytes(data))
        os.utime(plug, ns=(st.st_atime_ns, st.st_mtime_ns))
        found, why = PR.find(work, new, *keyed(["Sel"]))
        assert found is None and "mod folder" in why, why

        # a changed profile file: refused
        lo = Path(inst) / "profiles" / "P" / "loadorder.txt"
        lo.write_text(lo.read_text(encoding="utf-8") + chr(10), encoding="utf-8")
        found, why = PR.find(work, new, *keyed(["Sel"]))
        assert found is None and "profile file" in why, why


def case_s1_the_scope_line_names_the_cells_the_other_plugins_and_a_disabled_mod_to_tick():
    """The tab's scope line, from the combined arm's scope.json: the cells the ticked mod edits (worldspace with its
    named cells, interiors), the other enabled plugins touching them or the two cells around them (the base game and
    the mod's own plugins left out), and a disabled, unticked mod that also edits them as a hint to tick."""
    scope = {
        "targets": [{"name": "X.esp", "footprint": {"placed": {"cells": [
            {"cell": "000D74:Skyrim.esm", "kind": "persistent", "worldspace": "Tamriel"},
            {"cell": "009634:Skyrim.esm", "kind": "exterior", "name": "WatchExterior02", "worldspace": "Tamriel"},
            {"cell": "009616:Skyrim.esm", "kind": "exterior", "name": None, "worldspace": "Tamriel"},
            {"cell": "0151F9:Skyrim.esm", "kind": "interior", "name": "Watch01", "worldspace": None}]}}}],
        "locationContributors": {"locations": [
            {"cell": "009634:Skyrim.esm", "plugins": [{"name": "Skyrim.esm"}, {"name": "X.esp"}, {"name": "Y.esp"}]},
            {"cell": "0151F9:Skyrim.esm", "plugins": [{"name": "Skyrim.esm"}, {"name": "ccbgssse001-fish.esm"}]}]},
        "nearby": {"ring": 2, "plugins": [{"name": "Z.esp", "cells": ["009700:Skyrim.esm"]}, {"name": "Update.esm", "cells": []}],
                   "alsoScanned": [{"name": "Keys.esp", "footprintCells": ["009634:Skyrim.esm"], "ringCells": []},
                                   {"name": "Far.esp", "footprintCells": [], "ringCells": []}]}}
    line = EP.scope_line(scope, {"selection": ["Mod X"]}, {"keys.esp": "Mod Keys", "far.esp": "Mod Far"})
    assert line.startswith("Mod X edits 2 cells in Tamriel (WatchExterior02) and 1 interior (Watch01); "), line
    assert "2 other enabled plugins touch those cells or the 2 cells around them: Y.esp, Z.esp." in line, line
    assert "Skyrim.esm" not in line and "Update.esm" not in line and "ccbgssse001" not in line and "X.esp" not in line, line
    assert "Mod Keys (disabled in MO2) also edits these cells; tick it to test them together." in line, line
    assert "Mod Far" not in line, line
    empty = EP.scope_line({"targets": [{"footprint": {"placed": {"cells": []}}}]}, {"selection": ["Mod X"]}, {})
    assert "places nothing in any cell" in empty, empty
    # a contributor walk that did not finish never reads as "no other plugin"
    broken = dict(scope, locationContributors={"complete": False, "missing": ["'Skyrim.esm' could not be walked"], "locations": []})
    said = EP.scope_line(broken, {"selection": ["Mod X"]}, {})
    assert "is not known" in said and "no other enabled plugin" not in said, said


def case_c8_a_plugin_plugins_txt_does_not_list_is_force_loaded_and_an_unchecked_one_is_not():
    """Mo2LoadOrder's rule, the probes' own: MO2 leaves force-loaded plugins (Creation Club content) out of plugins.txt
    while listing them in loadorder.txt, so such a plugin is active; one listed without `*` is unchecked and is not."""
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, game = world(t, SHADOW_MODS, SHADOW_ENABLED, SHADOW_PLUGINS, data=("Base.esm", "ccFish.esm"))
        prof = Path(inst) / "profiles" / "P"
        (prof / "plugins.txt").write_text("".join(x + chr(10) for x in ("*Base.esm", "*A.esp", "*Sel.esp", "Dup.esp")), encoding="utf-8")
        ctx = EP.Context(in_place_entry(t, inst, game, ["Sel"], ["geometry.object-overlap"]), None, t / "out")
        by = {a["id"]: a for a in ctx.resolve()["arms"]}
        assert "ccFish.esm" in by["combined"]["plugins"] and "ccFish.esm" in by["without-selection"]["plugins"], by["combined"]["plugins"]
        assert "Dup.esp" not in by["combined"]["plugins"], "an unchecked plugin was loaded"


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
    # the derived checks: evalrun does not compute functional.linked-dependencies ("derived kind None is not one this controller
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
    """H3: no plugin outside the selection becomes a target. Tested basis: evalrun plan with no
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
        # the arms differ only by the folder, which is what evalrun's population takes (h3-asset-only-plan.log)
        by = {a["id"]: a for a in res["arms"]}
        assert ER.arm_delta([by["without-selection"]["mods"], by["combined"]["mods"]]) == ["Tex"]
        assert ER.arm_delta([by["without-selection"]["plugins"], by["combined"]["plugins"]]) == []


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
        # rows and receipts that must yield no pair: a row naming no target, a receipt not accepted, and a
        # baseline-arm receipt with another pair
        (run / "outputs" / "o2.json").write_text(json.dumps({"Conflicts": [
            {"Kind": "OVERLAP", "A": "Other.esp", "B": "Base.esm", "Summary": "s", "Necessary": [], "Alternative": []}]}), encoding="utf-8")
        (run / "receipts" / "geometry.navmesh-ownership__candidate-01__arm.json").write_text(json.dumps(
            {"checkId": "geometry.navmesh-ownership", "armId": "candidate-01", "validation": {"accepted": True},
             "outputs": [{"path": "outputs/o2.json"}]}), encoding="utf-8")
        (run / "outputs" / "o3.json").write_text(json.dumps({"Conflicts": [
            {"Kind": "OVERLAP", "A": "Sel.esp", "B": "Rejected.esp", "Summary": "s", "Necessary": [], "Alternative": []}]}), encoding="utf-8")
        (run / "receipts" / "geometry.placement-contact__candidate-01__arm.json").write_text(json.dumps(
            {"checkId": "geometry.placement-contact", "armId": "candidate-01", "validation": {"accepted": False},
             "outputs": [{"path": "outputs/o3.json"}]}), encoding="utf-8")
        (run / "outputs" / "o4.json").write_text(json.dumps({"Conflicts": [
            {"Kind": "OVERLAP", "A": "Sel.esp", "B": "BaseArm.esp", "Summary": "s", "Necessary": [], "Alternative": []}]}), encoding="utf-8")
        (run / "receipts" / "geometry.object-overlap__baseline__arm.json").write_text(json.dumps(
            {"checkId": "geometry.object-overlap", "armId": "baseline", "validation": {"accepted": True},
             "outputs": [{"path": "outputs/o4.json"}]}), encoding="utf-8")
        plan = {"reviewArm": "candidate-01", "targets": [{"name": "Sel.esp"}],
                "arms": [{"armId": "baseline", "kind": "baseline", "plugins": ["Base.esm", "Other.esp"]},
                         {"armId": "candidate-01", "kind": "candidate", "plugins": ["Base.esm", "Other.esp", "Sel.esp"]}]}
        ex = SimpleNamespace(plan=plan, run=run)
        pairs = ER.all_plan_pairs(ex)
        assert sorted(map(sorted, pairs)) == [["Other.esp", "Sel.esp"]], f"pairs {pairs}"


# ---- H1: a receipt's consumed configuration is the configuration of the arm it ran on ----

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


# ---- the review arm's pair source applies only when the plan names a review arm ----

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
        assert why == ("no geometry check ran on the review arm, so there is no clash to take a plugin pair from; select "
                       "overlap, contact or navmesh ownership with patch discovery."), why


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


def case_l4_an_inactive_plugin_stays_out_and_a_plugin_the_game_data_also_provides_stays_in():
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        mods = {"Sel": {"Sel.esp": ["Base.esm"], "Off.esp": ["Base.esm"], "Shared.esp": ["Base.esm"]}, "A": {"A.esp": ["Base.esm"]}}
        inst, game = world(t, mods, ["Sel", "A"], ["A.esp", "Sel.esp", "Shared.esp"], data=("Base.esm", "Shared.esp"))
        # Off.esp is in loadorder.txt and unticked in plugins.txt: MO2 lists an unticked plugin there without `*` (one it
        # leaves out of plugins.txt altogether is force-loaded, Mo2LoadOrder's rule; corrected 2026-11-16)
        lo = inst / "profiles" / "P" / "loadorder.txt"
        lo.write_text(lo.read_text(encoding="utf-8") + "Off.esp\n", encoding="utf-8")
        pl = inst / "profiles" / "P" / "plugins.txt"
        pl.write_text(pl.read_text(encoding="utf-8") + "Off.esp\n", encoding="utf-8")
        ctx = EP.Context(in_place_entry(t, inst, game, ["Sel"], ["geometry.object-overlap"]), None, t / "out")
        by = {a["id"]: a for a in ctx.resolve()["arms"]}
        assert "Off.esp" not in by["combined"]["plugins"], "an unticked plugin must not load"
        assert "Shared.esp" in by["without-selection"]["plugins"], "a plugin Data also provides stays loaded with Sel disabled"


# ---- the review arm in evalrun plan itself ----

def plan_run(t: Path, review=None):
    inst, game = world(t, {"A": {"A.esp": ["Base.esm"]}, "Sel": {"Sel.esp": ["Base.esm"]}}, ["Sel", "A"], ["A.esp", "Sel.esp"])
    c = t / "cands"
    c.mkdir()
    for name, pl, mods in (("candidate-00-baseline.json", ["Base.esm", "A.esp"], ["A"]),
                           ("candidate-01.json", ["Base.esm", "A.esp", "Sel.esp"], ["Sel", "A"])):
        (c / name).write_text(json.dumps({"SchemaVersion": 1, "Name": name, "BaseProfile": "P", "Plugins": pl, "Mods": mods,
                                          "Changes": []}), encoding="utf-8")
    argv = [sys.executable, str(HARNESS / "evalrun.py"), "plan", "--registry",
            str(HARNESS.parent / ".claude/skills/world-edit-compatibility/references/check-registry.json"), "--candidates", str(c),
            "--depth", "standard", "--select", "patch.discovery-fomod,geometry.object-overlap", "--out", str(t / "run"),
            "--instance", str(inst), "--profile", "P", "--game", str(game)]
    if review:
        argv += ["--review-arm", review]
    r = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env={**os.environ, "HOUSECARL_RUNLOG_DIR": str(t / "rl")})
    plan = json.loads((t / "run" / "plan.json").read_text(encoding="utf-8")) if (t / "run" / "plan.json").is_file() else None
    return r, plan


def fomod_arms(plan):
    c = next(x for x in plan["checks"] if x.get("checkId") == "patch.discovery-fomod")
    return [a["armId"] for a in c["arms"]]


def case_m3a_plan_review_arm_is_frozen_and_moves_the_baseline_only_checks():
    with tempfile.TemporaryDirectory() as t:
        r, plan = plan_run(Path(t), "candidate-01")
        assert r.returncode == 0, f"evalrun plan --review-arm exit {r.returncode}: {(r.stdout + r.stderr)[-400:]}"
        assert plan.get("reviewArm") == "candidate-01", plan.get("reviewArm")
        assert fomod_arms(plan) == ["candidate-01"], f"a baseline-only check must be planned on the review arm: {fomod_arms(plan)}"


def case_m3b_without_a_review_arm_the_plan_is_as_today():
    with tempfile.TemporaryDirectory() as t:
        r, plan = plan_run(Path(t))
        assert r.returncode == 0, (r.stdout + r.stderr)[-400:]
        assert "reviewArm" not in plan or plan["reviewArm"] in (None, "baseline"), plan.get("reviewArm")
        assert fomod_arms(plan) == ["baseline"], fomod_arms(plan)


def case_m3c_a_review_arm_the_plan_does_not_have_is_refused_in_one_sentence():
    with tempfile.TemporaryDirectory() as t:
        r, plan = plan_run(Path(t), "candidate-07")
        out = (r.stdout + r.stderr)
        assert r.returncode == 2 and "candidate-07" in out and "--review-arm" in out and "unrecognized" not in out, \
            f"exit {r.returncode}: {out[-400:]}"



def _discovery_run(run: Path, targets):
    (run / "receipts").mkdir(parents=True)
    (run / "outputs").mkdir()
    hits = [{"file": "SelFix.esp", "kind": "pool-archive", "masters": ["Sel.esp"], "sha256": "11" * 32},
            {"file": "Far1.esp", "kind": "pool-archive", "masters": ["Zed.esp"], "sha256": "22" * 32},
            {"file": "Far2.esp", "kind": "pool-archive", "masters": ["Skyrim.esm"], "sha256": "33" * 32}]
    out = run / "outputs" / "pool.json"
    out.write_text(json.dumps({"schemaVersion": 1, "probe": "poolprobe", "complete": True, "hits": hits}), encoding="utf-8")
    receipts = {"r-pool": {"receiptId": "r-pool", "armId": "candidate-01", "checkId": "patch.discovery-pool",
                           "validation": {"accepted": True}, "adapter": {"kind": "patchprobe", "exe": "poolprobe"},
                           "outputs": [{"path": "outputs/pool.json", "sha256": ER.sha256_file(out)}]}}
    plan = {"planId": "p", "reviewArm": "candidate-01", "targets": [{"name": t} for t in targets],
            "arms": [{"armId": "baseline", "kind": "baseline"}, {"armId": "candidate-01", "kind": "candidate"}], "checks": []}
    return SimpleNamespace(run=run, plan=plan), receipts


def case_b8i_finalize_records_discovery_relevance_by_evalruns_rule_for_every_accepted_discovery_receipt():
    """relevance is recorded whether or not candidate-qualification runs, by discovery_hit_rows' rule,
    and as not computed, with the sentence, when the plan has no target and no pair."""
    rel = getattr(ER, "discovery_relevance", None)
    assert rel is not None, "evalrun records no discovery relevance outside candidate-qualification (no discovery_relevance)"
    with tempfile.TemporaryDirectory() as t:
        ex, receipts = _discovery_run(Path(t) / "with-target", ["Sel.esp"])
        got = rel(ex, receipts)
        one = got["patch.discovery-pool"]["candidate-01"]
        assert one["computed"] is True, one
        assert one["relevant"] == ["SelFix.esp"], one
        assert one["notRelevant"]["count"] == 2 and one["notRelevant"]["why"], one
        ex, receipts = _discovery_run(Path(t) / "no-target", [])
        none = rel(ex, receipts)["patch.discovery-pool"]["candidate-01"]
        assert none["computed"] is False and "no target" in none["why"], none


PATCHPROBE = HARNESS / "patchprobe" / "bin" / "Release" / "net9.0" / "patchprobe.exe"


def case_e2e1_patchprobe_finds_the_run_instance_outside_its_search_roots():
    """A manual run picks any instance: patchprobe must find the run's instance even when none of its
    search roots holds it."""
    assert PATCHPROBE.exists(), f"build patchprobe first: {PATCHPROBE}"
    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        inst, roots, pool = t / "elsewhere" / "inst", t / "roots", t / "pool"
        for d in (inst / "profiles" / "P", inst / "mods" / "M", roots, pool):
            d.mkdir(parents=True)
        (inst / "ModOrganizer.ini").write_text("[General]\nselected_profile=@ByteArray(P)\n", encoding="utf-8")
        (inst / "profiles" / "P" / "modlist.txt").write_text("+M\n", encoding="utf-8")
        (inst / "profiles" / "P" / "plugins.txt").write_text("*A.esp\n", encoding="utf-8")
        write_plugin(inst / "mods" / "M" / "A.esp")
        env = dict(os.environ, HOUSECARL_MO2_INSTANCE=str(inst), HOUSECARL_MO2_PROFILE="P", HOUSECARL_SEARCH_ROOTS=str(roots),
                   HOUSECARL_DOWNLOAD_POOL=str(pool), HOUSECARL_HARNESS_CONFIG="", PYTHONIOENCODING="utf-8")
        r = subprocess.run([str(PATCHPROBE), "--pair", "A.esp", "B.esp", "--json", str(t / "out.json")], env=env,
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        said = r.stdout + r.stderr
        assert "target instance not found" not in said, "patchprobe did not find the run's instance: " + said[-400:]
        assert r.returncode == 0, f"patchprobe exited {r.returncode}: {said[-400:]}"


def case_c7_a_missing_loadorder_or_plugins_file_is_refused_not_read_as_an_empty_order():
    """A profile with no loadorder.txt or no plugins.txt has no order to read; the run refuses naming the file instead of
    carrying on as if no plugin were active."""
    for missing in ("loadorder.txt", "plugins.txt"):
        with tempfile.TemporaryDirectory() as t:
            t = Path(t)
            inst, game = world(t, SHADOW_MODS, SHADOW_ENABLED, SHADOW_PLUGINS)
            prof = next((Path(inst) / "profiles").iterdir())
            (prof / missing).unlink()
            ctx = EP.Context(in_place_entry(t, inst, game, ["Sel"], ["geometry.object-overlap"]), None, t / "out")
            try:
                ctx.resolve()
            except EP.Refusal as e:
                assert missing in str(e), f"the refusal does not name {missing}: {e}"
                continue
            raise AssertionError(f"a profile with no {missing} was read as an empty order")

def main():
    for name, fn in sorted((k, v) for k, v in globals().items() if k.startswith("case_")):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
