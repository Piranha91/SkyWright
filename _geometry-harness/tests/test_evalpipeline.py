"""Known answers for evalpipeline.py's rules: arms from a selection entry, arm order, scope acceptance, stage reuse
and stage outcomes.

Dependency-free: `python _geometry-harness/tests/test_evalpipeline.py`. EVALPIPELINE_PATH, if set, loads the module
from that file instead (the sabotage arms use it). Plugins are synthetic.

The arm rule (`derive_arms`): dependencies; subject; each partner unit; partners together (more than one unit);
combined; subject with each partner unit (more than one unit); per key, combined + key and, when the key masters
exactly one partner unit, subject + that unit + key; combined + every key (more than one key); then the entry's extra
arms and order variants. A partner unit is one plugin, or every partner sharing a `group`. Duplicates (same plugins,
same order rule) collapse to the first.
"""
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
_path = os.environ.get("EVALPIPELINE_PATH") or str(HERE.parent / "evalpipeline.py")
EP, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("evalpipeline_under_test", _path)
    EP = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(EP)
except (FileNotFoundError, ImportError, OSError) as _ex:
    EP, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"
PASSED, FAILURES = [], []

BASE = ["Base.esm", "Dlc.esm", "Fix.esp"]


def hdr(masters, esm=False):
    return {"masters": masters, "esm": esm}


HEADERS = {"Base.esm": hdr([], True), "Dlc.esm": hdr(["Base.esm"], True), "Fix.esp": hdr(["Base.esm", "Dlc.esm"]),
           "Subj.esp": hdr(["Base.esm"]), "PA.esp": hdr(["Base.esm"]), "PB.esp": hdr(["Base.esm"]),
           "PC1.esp": hdr(["Base.esm"]), "PC2.esp": hdr(["Base.esm"]), "PCP.esp": hdr(["PC1.esp", "PC2.esp"]),
           "KA.esp": hdr(["Base.esm", "Subj.esp", "PA.esp"]), "KB.esp": hdr(["Base.esm", "Subj.esp", "PB.esp"]),
           "KS.esp": hdr(["Base.esm", "Subj.esp"]), "SubjM.esp": hdr(["Base.esm"], True)}
SOURCE = ["Base.esm", "Dlc.esm", "PA.esp", "Fix.esp", "PB.esp", "PC1.esp", "PC2.esp", "PCP.esp",
          "Subj.esp", "KA.esp", "KB.esp", "KS.esp", "SubjM.esp"]


def entry(partners, keys, **kw):
    e = {"subject": {"plugin": "Subj.esp"}, "partners": [{"plugin": p} if isinstance(p, str) else p for p in partners],
         "keys": [{"plugin": k} for k in keys], "extraArms": [], "orderVariants": []}
    e.update(kw)
    return e


def sets(arms):
    return sorted((tuple(sorted(a["evaluated"])), a.get("variant") or "") for a in arms)


def check(name, fn):
    try:
        if EP is None:
            raise AssertionError(f"evalpipeline.py is not loadable: {LOAD_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def refuses(fn, needle):
    try:
        fn()
    except EP.Refusal as r:
        assert needle in str(r), f"refused, but the sentence lacks {needle!r}: {r}"
        return
    raise AssertionError(f"expected a refusal naming {needle!r}; it was accepted")


# ---- arms ----

def case_1_one_partner_one_key_is_five_arms():
    arms = EP.derive_arms(entry(["PA.esp"], ["KA.esp"]), HEADERS)
    want = sorted([((), ""), (("Subj.esp",), ""), (("PA.esp",), ""), (("PA.esp", "Subj.esp"), ""),
                   (("KA.esp", "PA.esp", "Subj.esp"), "")])
    assert sets(arms) == want, f"five arms (pair + key collapses into combined + key), got {sets(arms)}"


def case_2_two_partners_two_keys_is_thirteen_arms():
    arms = EP.derive_arms(entry(["PA.esp", "PB.esp"], ["KA.esp", "KB.esp"]), HEADERS)
    s = sets(arms)
    assert len(s) == 13, f"13 arms for two partners and two keys, got {len(s)}: {s}"
    for want in [(("PA.esp", "PB.esp"), ""), (("PA.esp", "Subj.esp"), ""), (("KA.esp", "PA.esp", "Subj.esp"), ""),
                 (("KA.esp", "KB.esp", "PA.esp", "PB.esp", "Subj.esp"), "")]:
        assert want in s, f"missing arm {want}"


def case_3_partner_group_is_one_unit():
    arms = EP.derive_arms(entry(["PA.esp", {"plugin": "PC1.esp", "group": "c"}, {"plugin": "PC2.esp", "group": "c"},
                                 {"plugin": "PCP.esp", "group": "c"}], []), HEADERS)
    s = sets(arms)
    assert (("PC1.esp", "PC2.esp", "PCP.esp"), "") in s, f"the group is one partner arm, got {s}"
    assert (("PC1.esp",), "") not in s, "a group member never stands alone"
    assert len(s) == 8, f"8 arms for two partner units and no key, got {len(s)}"


def case_4_key_without_partner_master_has_no_pair_arm():
    s = sets(EP.derive_arms(entry(["PA.esp", "PB.esp"], ["KS.esp"]), HEADERS))
    assert len(s) == 9, f"two units, one key mastering no partner: 8 + combined+key = 9, got {len(s)}: {s}"


def case_5_extra_arm_and_order_variant():
    e = entry(["PA.esp"], [], extraArms=[{"id": "x", "plugins": ["Subj.esp", "PA.esp"]}],
              orderVariants=[{"id": "combined-reverse", "of": "combined", "reverse": True}])
    s = sets(EP.derive_arms(e, HEADERS))
    assert (("PA.esp", "Subj.esp"), "combined-reverse") in s, f"the order variant is its own arm, got {s}"
    assert len(s) == 5, f"the extra arm duplicates combined and collapses: 4 + 1 variant = 5, got {len(s)}"


# ---- order ----

def case_7_order_follows_source_with_masters_first():
    arm = {"evaluated": ["Subj.esp", "PA.esp", "KA.esp"], "keys": ["KA.esp"]}
    got = EP.order_arm(arm, BASE, SOURCE, HEADERS)
    assert got == ["Base.esm", "Dlc.esm", "PA.esp", "Fix.esp", "Subj.esp", "KA.esp"], got


def case_9_esm_flagged_esp_loads_before_esps():
    arm = {"evaluated": ["SubjM.esp", "PA.esp"], "keys": []}
    got = EP.order_arm(arm, BASE, SOURCE, HEADERS)
    assert got.index("SubjM.esp") < got.index("Fix.esp"), f"an ESM-flagged plugin loads with the masters, got {got}"


def case_11_late_master_refused():
    src = [p for p in SOURCE if p != "KA.esp"]
    src.insert(src.index("Subj.esp"), "KA.esp")   # key before its master
    arm = {"evaluated": ["Subj.esp", "PA.esp", "KA.esp"], "keys": ["KA.esp"]}
    refuses(lambda: EP.order_arm(arm, BASE, src, HEADERS), "KA.esp")


def case_13_missing_from_source_refused():
    arm = {"evaluated": ["Subj.esp", "Gone.esp"], "keys": []}
    refuses(lambda: EP.order_arm(arm, BASE, SOURCE, {**HEADERS, "Gone.esp": hdr(["Base.esm"])}), "Gone.esp")


def case_15_reverse_variant_that_breaks_masters_refused():
    arm = {"evaluated": ["PC1.esp", "PC2.esp", "PCP.esp"], "keys": [], "reverse": True}
    refuses(lambda: EP.order_arm(arm, BASE, SOURCE, HEADERS), "PCP.esp")


def case_16_reverse_variant_swaps_independent_plugins():
    arm = {"evaluated": ["Subj.esp", "PA.esp"], "keys": [], "reverse": True}
    got = EP.order_arm(arm, BASE, SOURCE, HEADERS)
    assert got.index("Subj.esp") < got.index("PA.esp"), f"reverse puts the subject before the partner, got {got}"
    assert got[:2] == ["Base.esm", "Dlc.esm"], f"the baseline keeps its place, got {got}"


# ---- scope acceptance ----

def scope_doc(**over):
    d = {"complete": True, "walkFailures": [], "unreadableRecords": [],
         "configuration": {"fingerprint": "f", "plugins": [{"Name": n, "Path": f"X:/{n}", "Sha256": "h" + n}
                                                           for n in ["Base.esm", "Subj.esp"]],
                           "providers": [{"Name": "M1", "Kind": "mod"}]},
         "targets": [{"name": "Subj.esp", "sha256": "hSubj.esp",
                      "footprint": {"placed": {"cells": [{"cell": "000A:Base.esm", "kind": "interior"},
                                                         {"cell": "000B:Base.esm", "kind": "exterior"}]}}}]}
    d.update(over)
    return d


EXPECT = {"plugins": [{"name": n, "path": f"X:/{n}", "sha256": "h" + n} for n in ["Base.esm", "Subj.esp"]],
          "mods": ["M1"], "targets": ["Subj.esp"], "censusCells": {"Subj.esp": ["000A:Base.esm", "000B:Base.esm"]}}


def case_17_scope_accepted():
    try:
        r = EP.accept_scope(scope_doc(), EXPECT)
    except EP.Refusal as ex:
        raise AssertionError(f"a good scope must be accepted, refused: {ex}")
    assert r["targets"]["Subj.esp"]["missingCensusCells"] == [], r


def case_19_incomplete_scope_refused():
    refuses(lambda: EP.accept_scope(scope_doc(complete=False), EXPECT), "incomplete")
    refuses(lambda: EP.accept_scope(scope_doc(walkFailures=[{"x": 1}]), EXPECT), "incomplete")


def case_21_provider_or_bytes_swap_refused():
    d = scope_doc()
    d["configuration"]["plugins"][1]["Sha256"] = "other"
    refuses(lambda: EP.accept_scope(d, EXPECT), "Subj.esp")
    d = scope_doc()
    d["configuration"]["providers"] = [{"Name": "M2", "Kind": "mod"}]
    refuses(lambda: EP.accept_scope(d, EXPECT), "provider")


def case_23_missing_census_cell_refused():
    exp = dict(EXPECT, censusCells={"Subj.esp": ["000A:Base.esm", "000C:Base.esm"]})
    refuses(lambda: EP.accept_scope(scope_doc(), exp), "000C:Base.esm")


def case_25_empty_target_footprint_refused():
    d = scope_doc()
    d["targets"] = []
    refuses(lambda: EP.accept_scope(d, EXPECT), "target")


# ---- stage reuse and outcomes ----

def case_27_stage_reuse_by_inputs():
    with tempfile.TemporaryDirectory() as t:
        run = Path(t)
        out = run / "o.json"
        out.write_text("{}", encoding="utf-8")
        led = EP.StageLedger(run)
        assert led.decide("scope", {"a": "1"}) == "run"
        led.record("scope", {"a": "1"}, [out], exit_code=0)
        assert led.decide("scope", {"a": "1"}) == "reuse", "unchanged inputs and outputs are reused"
        assert led.decide("scope", {"a": "2"}) == "run", "a changed input reruns the stage"
        out.write_text('{"changed": 1}', encoding="utf-8")
        assert led.decide("scope", {"a": "1"}) == "run", "a changed output reruns the stage"


def case_29_exit_zero_without_output_not_accepted():
    with tempfile.TemporaryDirectory() as t:
        run = Path(t)
        led = EP.StageLedger(run)
        refuses(lambda: led.record("bundle", {"a": "1"}, [run / "missing.json"], exit_code=0), "missing.json")
        assert led.decide("bundle", {"a": "1"}) == "run"


def case_31_precedent_refusal_does_not_block_but_keeps_run_incomplete():
    st = EP.stage_outcome("precedent", exit_code=2, outputs_ok=False, sentence="no cards: nothing supported")
    assert st["status"] == "refused" and st["blocksLater"] is False, st
    assert "nothing supported" in st["sentence"], st
    assert EP.run_status([st, {"stage": "report", "status": "accepted", "blocksLater": False}]) == "incomplete"
    st2 = EP.stage_outcome("scope", exit_code=1, outputs_ok=False, sentence="probe failed")
    assert st2["blocksLater"] is True, "a failed measurement stage blocks the stages after it"


# ---- judgement and run-local stages ----

KEY = [{"image": "image-001.png", "sha256": "a" * 64}, {"image": "image-002.png", "sha256": "b" * 64}]


def case_33_observations_wait_until_banked():
    st, why = EP.observations_state(KEY, None)
    assert st == "awaiting", f"frames without banked observations must stop the run, got {st}: {why}"


def case_35_observations_must_match_this_capture():
    other = {"frames": [{"image": "image-001.png", "sha256": "c" * 64, "observation": "a doorway"},
                        {"image": "image-002.png", "sha256": "b" * 64, "observation": "a cart"}]}
    st, why = EP.observations_state(KEY, other)
    assert st == "refused" and "image-001.png" in why, f"observations of other bytes are refused, got {st}: {why}"
    empty = {"frames": [{"image": k["image"], "sha256": k["sha256"], "observation": ""} for k in KEY]}
    st, _ = EP.observations_state(KEY, empty)
    assert st == "refused", "a frame without an observation is refused"
    good = {"frames": [{"image": k["image"], "sha256": k["sha256"], "observation": "x"} for k in KEY]}
    assert EP.observations_state(KEY, good)[0] == "accepted"


def case_37_no_frames_is_a_stated_refusal_not_a_pass():
    st, why = EP.observations_state([], None)
    assert st == "nothing" and "nothing to observe" in why, f"no frames is said, never accepted, got {st}: {why}"


def case_39_precedent_script_absent_is_a_visible_refusal():
    import types
    with tempfile.TemporaryDirectory() as t:
        ctx = types.SimpleNamespace(machine={"precedentCmd": ["python", f"{t}/absent/precedent_stage.py", "--bundle",
                                                              "{bundle}", "--out", "{out}"]}, out=Path(t), id="x")
        refuses(lambda: EP.stage_precedent(ctx, {}, Path(t) / "precedent"), "stage script absent")


def case_41_unbuilt_scene_stage_is_a_stated_refusal():
    try:
        EP.stage_scenes(None, {}, Path("."))
    except EP.Refusal as r:
        assert "not built" in str(r), f"the refusal says what was not done, got {r}"
        assert isinstance(r, EP.NonBlockingRefusal), "it does not block the report"
        return
    raise AssertionError("an unbuilt stage must refuse visibly, never accept with nothing done")


def case_43_captured_frames_reach_the_media_bundle():
    with tempfile.TemporaryDirectory() as t:
        seq = Path(t) / "a" / "sequence"
        seq.mkdir(parents=True)
        (seq / "capture-result.json").write_text("{}", encoding="utf-8")
        (seq / "media-baseline.json").write_text(json.dumps({"items": [{"path": "w.png"}]}), encoding="utf-8")
        (seq / "media-candidate-03.json").write_text(json.dumps({"items": [{"path": "wo.png"}]}), encoding="utf-8")
        rows = [{"status": "captured", "result": str(seq / "capture-result.json")},
                {"status": "refused", "result": str(seq / "capture-result.json")}]
        got = [i["path"] for i in EP.capture_media_items(rows)]
        assert got == ["w.png", "wo.png"], f"every captured frame goes to the media bundle once, got {got}"


def case_45_reel_transitions_are_decoded_by_index():
    rmap = {"segments": [
        {"chapterId": "R-01", "kind": "still", "reelInterval": {"startFrame": 0, "frames": 10},
         "frames": [{"page": 0, "frames": 4}, {"page": 1, "frames": 6}]},
        {"chapterId": "R-02", "kind": "sequence", "reelInterval": {"startFrame": 10, "frames": 9},
         "frames": [{"state": "without", "frames": 3}, {"state": "with", "frames": 3}, {"state": "highlight", "frames": 3}]},
        {"chapterId": "R-03", "kind": "split", "reelInterval": {"startFrame": 19, "frames": 5},
         "frames": [{"state": "left", "frames": 5}, {"state": "right", "frames": 5}]}]}
    got = [r["frame"] for r in EP.reel_transition_frames(rmap)]
    assert got == [0, 3, 4, 9, 10, 12, 13, 15, 16, 18, 19, 23], f"every change of picture, both sides of it, got {got}"


def case_47_generations_go_to_new_numbered_folders():
    with tempfile.TemporaryDirectory() as t:
        first = EP.next_numbered(Path(t), "report")
        first.mkdir()
        second = EP.next_numbered(Path(t), "report")
        assert (first.name, second.name) == ("report-01", "report-02"), (first, second)
        second.mkdir()
        assert EP.latest_numbered(Path(t), "report") == second


def case_48_a_capture_the_final_plan_dropped_is_superseded():
    rows = [{"id": "a", "status": "captured", "result": "a.json"}, {"id": "b", "status": "captured", "result": "b.json"},
            {"id": "c", "status": "refused", "result": "c.json"}]
    got = {r["id"]: r["status"] for r in EP.mark_superseded(rows, ["a", "c"])}
    assert got == {"a": "captured", "b": "superseded", "c": "refused"}, got
    assert not [m for m in EP.capture_media_items(EP.mark_superseded(rows, ["a", "c"])) if m], "no media files here"


def case_49_a_finished_request_is_reused_with_its_outcome_and_an_unfinished_one_is_not():
    ident = {"capture_acquire.py": "a1", "capture_points.py": "p1", "viewprobe": "v1"}
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        docs = {"capture-a": {"status": "captured"},
                # (corrected 2026-10-31, capture fallbacks: a bare points-no-opaque-face is now answered by a coverage mark
                # and is captured again; a FINISHED chain's geometric refusal is what is reused)
                "capture-b": {"status": "refused", "refusal": {"code": "see-through-unmarked", "final": True,
                                                               "chain": [{"kind": "eye-level", "status": "refused"},
                                                                         {"kind": "top-down", "status": "refused"}]}},
                "capture-o": {"status": "captured", "toolIdent": "other"}}
        for rid, doc in docs.items():
            (root / rid / "sequence").mkdir(parents=True)
            body = dict(doc, request=rid, toolIdentity=ident if rid != "capture-o" else dict(ident, viewprobe="v0"))
            (root / rid / "sequence" / "capture-result.json").write_text(json.dumps(body), encoding="utf-8")
        a = EP.reusable_capture({"id": "capture-a", "armId": "baseline", "vignette": {"withoutArmId": "c1"}}, root / "capture-a", ident)
        b = EP.reusable_capture({"id": "capture-b"}, root / "capture-b", ident)
        c = EP.reusable_capture({"id": "capture-c"}, root / "capture-c", ident)
        o = EP.reusable_capture({"id": "capture-o"}, root / "capture-o", ident)
        assert a and a["reused"] and a["status"] == "captured" and a["arm"] == "baseline", a
        assert b and b["status"] == "refused" and b["reused"], b
        assert c is None, c
        assert o is None, f"a result taken with another renderer build is captured again, got {o}"


def case_50_a_transient_refusal_is_captured_again():
    ident = {"capture_acquire.py": "a1", "capture_points.py": "p1", "viewprobe": "v1"}
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        for rid, ref in (("capture-m", {"code": "memory-floor"}), ("capture-k", {"code": "renderer-exit"}),
                         ("capture-n", {})):
            (root / rid / "sequence").mkdir(parents=True)
            (root / rid / "sequence" / "capture-result.json").write_text(json.dumps(
                {"request": rid, "status": "refused", "refusal": ref, "toolIdentity": ident}), encoding="utf-8")
        got = {rid: EP.reusable_capture({"id": rid}, root / rid, ident) for rid in ("capture-m", "capture-k", "capture-n")}
        assert all(v is None for v in got.values()), f"a memory, crash or unexplained refusal is captured again: {got}"

def case_51_a_round_captures_at_most_the_cap():
    asked = [{"id": f"capture-{i}"} for i in range(7)]
    take, rest = EP.capped_requests(asked, 4)
    assert [q["id"] for q in take] == ["capture-0", "capture-1", "capture-2", "capture-3"], take
    assert [q["id"] for q in rest] == ["capture-4", "capture-5", "capture-6"], rest
    assert EP.MAX_NEW_PER_ROUND == 4 and EP.MAX_CAPTURE_ROUNDS == 3


# ---- plugin identity and placed plugins (Context.resolve over a small synthetic lab) ----

def write_plugin(path: Path, masters, esm=False):
    """A minimal TES4 header: the flags word and one MAST subrecord per master (all plugin_header reads)."""
    body = b""
    for m in masters:
        z = m.encode("cp1252") + b"\0"
        body += b"MAST" + len(z).to_bytes(2, "little") + z + b"DATA" + (8).to_bytes(2, "little") + bytes(8)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"TES4" + len(body).to_bytes(4, "little") + (1 if esm else 0).to_bytes(4, "little")
                     + bytes(12) + body)


def lab_context(t: Path, files: dict, source: list, ent: dict):
    """files: {(mod or None, plugin): (masters, esm)}; None is the game's Data."""
    for (mod, plugin), (masters, esm) in files.items():
        write_plugin((t / "game" / "Data" / plugin) if mod is None else (t / "lab" / "mods" / mod / plugin),
                     masters, esm)
    (t / "lab" / "mods").mkdir(parents=True, exist_ok=True)
    lo = t / "src" / "profiles" / "P" / "loadorder.txt"
    lo.parent.mkdir(parents=True, exist_ok=True)
    lo.write_text("".join(p + "\n" for p in source), encoding="utf-8")
    e = {"schema": 1, "id": "t", "source": {"instance": str(t / "src"), "profile": "P"},
         "lab": {"instance": str(t / "lab"), "game": str(t / "game")}, "extraArms": [], "orderVariants": [], **ent}
    (t / "entry.json").write_text(json.dumps(e), encoding="utf-8")
    return EP.Context(t / "entry.json", None, t / "out")


MIXED_FILES = {(None, "Base.esm"): ([], True), (None, "Dlc.esm"): (["Base.esm"], True),
               ("MFix", "Fix.esp"): (["Base.esm", "Dlc.esm"], False),
               # two partner headers name the same baseline plugins in other spellings
               ("MA", "PA.esp"): (["base.esm", "DLC.ESM", "fix.esp"], False),
               ("MB", "PB.esp"): (["BASE.ESM", "FIX.ESP"], False),
               ("MS", "Subj.esp"): (["Base.esm", "Fix.esp"], False),
               ("MK", "KA.esp"): (["Base.esm", "Subj.esp", "pa.esp"], False)}
MIXED_SOURCE = ["Base.esm", "Dlc.esm", "Fix.esp", "PA.esp", "PB.esp", "Subj.esp"]
MIXED_ENTRY = {"subject": {"plugin": "Subj.esp", "mod": "MS"},
               "partners": [{"plugin": "PA.esp", "mod": "MA"}, {"plugin": "PB.esp", "mod": "MB"}],
               "keys": [{"plugin": "KA.esp", "mod": "MK"}],
               "dependencies": [{"plugin": "Fix.esp", "mod": "MFix"}], "baseline": ["Base.esm", "Dlc.esm", "Fix.esp"]}


def case_52_a_master_named_in_another_case_is_one_plugin():
    with tempfile.TemporaryDirectory() as t:
        res = lab_context(Path(t), MIXED_FILES, MIXED_SOURCE, MIXED_ENTRY).resolve()
        assert len({p.lower() for p in res["baseline"]}) == len(res["baseline"]) == 3, (
            f"the baseline names each plugin once, got {res['baseline']}")
        assert sorted(res["headers"]) == sorted({p for (_, p) in MIXED_FILES}), (
            f"each plugin's header is read once, under one spelling, got {sorted(res['headers'])}")
        for arm in res["arms"]:
            low = [p.lower() for p in arm["plugins"]]
            assert len(low) == len(set(low)), f"arm {arm['id']} loads a plugin twice: {arm['plugins']}"
            assert all(p in res["headers"] for p in arm["plugins"]), (
                f"arm {arm['id']} names a plugin in another spelling: {arm['plugins']}")
        units = {a["id"]: a["evaluated"] for a in res["arms"]}
        assert units["unit:0"] == ["PA.esp"] and units["unit:1"] == ["PB.esp"], (
            f"a partner unit carries no baseline plugin under another spelling, got {units}")
        assert "pair:0+key:KA.esp" in units, f"the key's lower-case master still names partner PA, got {list(units)}"


PLACED_FILES = {(None, "Base.esm"): ([], True), ("MS", "Subj.esp"): (["Base.esm"], False),
                ("MA", "PA.esp"): (["Base.esm"], False), ("MB", "PB.esp"): (["Base.esm"], False),
                ("ML", "Lab.esp"): (["Base.esm", "PB.esp"], False)}
PLACED_SOURCE = ["Base.esm", "PA.esp", "PB.esp", "Subj.esp"]


def placed_entry(places):
    return {"subject": {"plugin": "Subj.esp", "mod": "MS"},
            "partners": [{"plugin": "PA.esp", "mod": "MA"}, {"plugin": "Lab.esp", "mod": "ML"}],
            "keys": [], "dependencies": [{"plugin": "PB.esp", "mod": "MB"}], "baseline": ["Base.esm", "PB.esp"],
            "places": places}


def case_53_a_plugin_the_source_does_not_load_takes_its_named_place():
    with tempfile.TemporaryDirectory() as t:
        res = lab_context(Path(t), PLACED_FILES, PLACED_SOURCE, placed_entry({"lab.esp": {"after": "PB.esp"}})).resolve()
        comb = next(a for a in res["arms"] if a["id"] == "combined")
        assert comb["plugins"] == ["Base.esm", "PA.esp", "PB.esp", "Lab.esp", "Subj.esp"], comb["plugins"]
    with tempfile.TemporaryDirectory() as t:
        refuses(lambda: lab_context(Path(t), PLACED_FILES, PLACED_SOURCE, placed_entry({})).resolve(), "Lab.esp")


def case_54_a_place_before_the_plugins_own_master_is_refused():
    with tempfile.TemporaryDirectory() as t:
        refuses(lambda: lab_context(Path(t), PLACED_FILES, PLACED_SOURCE,
                                    placed_entry({"Lab.esp": {"after": "PA.esp"}})).resolve(), "its master PB.esp")


def case_55_a_place_after_a_plugin_the_source_does_not_load_is_refused():
    with tempfile.TemporaryDirectory() as t:
        refuses(lambda: lab_context(Path(t), PLACED_FILES, PLACED_SOURCE,
                                    placed_entry({"Lab.esp": {"after": "Gone.esp"}})).resolve(), "Gone.esp")


def case_56_a_place_for_a_plugin_the_source_loads_is_refused():
    with tempfile.TemporaryDirectory() as t:
        refuses(lambda: lab_context(Path(t), PLACED_FILES, PLACED_SOURCE,
                                    placed_entry({"Lab.esp": {"after": "PB.esp"}, "PA.esp": {"after": "PB.esp"}})
                                    ).resolve(), "PA.esp is in the source profile's load order already")


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
