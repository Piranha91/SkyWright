"""Known answers for evalpipeline.py, the driver of the stages after the bundle: stage reuse and outcomes, the child
launch and its machine slot, the capture rounds, the report and reel stages, the stage tools, the entry keys each stage
binds, the disk forecast, the patch-search pairs, and the continuation of a run folder the built-in controller carried
through the bundle.

Dependency-free: `python _geometry-harness/tests/test_evalpipeline.py`. EVALPIPELINE_PATH, if set, loads the module
from that file instead (the sabotage arms use it). The arms, their order, scope acceptance and the measuring stages are
the built-in controller's (runcontrol --self-test holds their known answers).
"""
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
_path = os.environ.get("EVALPIPELINE_PATH") or str(HERE.parent / "evalpipeline.py")
EP, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("evalpipeline_under_test", _path)
    EP = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(EP)
except (FileNotFoundError, ImportError, OSError) as _ex:
    EP, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"
PASSED, FAILURES = [], []


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


PLACED_FILES = {(None, "Base.esm"): ([], True), ("MS", "Subj.esp"): (["Base.esm"], False),
                ("MA", "PA.esp"): (["Base.esm"], False), ("MB", "PB.esp"): (["Base.esm"], False),
                ("ML", "Lab.esp"): (["Base.esm", "PB.esp"], False)}
PLACED_SOURCE = ["Base.esm", "PA.esp", "PB.esp", "Subj.esp"]


def case_57_the_report_stage_plans_with_the_capture_closed():
    import inspect
    import types
    with tempfile.TemporaryDirectory() as t:
        out = Path(t)
        (out / "prepare").mkdir()
        (out / "evaluation").mkdir()
        (out / "prepare" / "preparation.json").write_text('{"arms": []}', encoding="utf-8")
        (out / "evaluation" / "plan.json").write_text('{"arms": []}', encoding="utf-8")
        ctx = types.SimpleNamespace(out=out, entry={})
        try:
            closed = EP.report_plan_argv(ctx, {"keys": []}, out / "p", final=True)
        except TypeError:
            raise AssertionError("the report stage cannot tell evalreport plan that the capture stage is closed")
        assert "--captures-final" in closed, f"the report stage's plan says the capture is closed, got {closed}"
        assert "--captures-final" not in EP.report_plan_argv(ctx, {"keys": []}, out / "p"), \
            "a plan inside the capture rounds still asks for captures"
    assert "final=True" in inspect.getsource(EP.stage_report), "the report stage plans with the capture closed"


def case_60_a_heavy_child_holds_the_slot_for_its_own_hold_minutes():
    import types
    seen = []

    class FakeRunner:
        def __init__(self, *a, **k):
            pass

        def run(self, jobs, echo=False):
            seen.extend(jobs)
            return [{"exitCode": 0}]
    prefix = ["python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180",
              "--min-free-gb", "16"]
    saved = EP.Runner
    with tempfile.TemporaryDirectory() as t:
        ctx = types.SimpleNamespace(out=Path(t), id="m", machine={"heavyPrefix": prefix, "lockDir": t})
        try:
            EP.Runner = FakeRunner
            try:
                EP.child(ctx, Path(t), "records-capture", ["x"], heavy=True, hold_min=460)
            except TypeError:
                raise AssertionError("a heavy child cannot be given its own slot hold")
            EP.child(ctx, Path(t), "evaluate-01", ["x"], heavy=True)
        finally:
            EP.Runner = saved
    a1, a2 = seen[0]["argv"], seen[1]["argv"]
    i = a1.index("--max-min")
    assert a1[i + 1] == "460" and a1.count("--max-min") == 1, f"the hold is 460 min, once, got {a1[:12]}"
    assert a2[a2.index("--max-min") + 1] == "180", "a child with no hold of its own keeps the machine's hold"
    assert a1[a1.index("--") + 1:] == ["x"], "the job's own argv follows the prefix"


# ---- runner defects after round 2 (cases 62-67) ----

def _lock_log(camp: Path) -> list:
    p = camp / "idle_lock.log"
    return [json.loads(x) for x in p.read_text(encoding="utf-8").splitlines() if x.strip()] if p.is_file() else []


def case_62_a_heavy_child_stopped_from_outside_leaves_no_lock():
    """A real process tree (the slot command and its job) ending at the stage's limit leaves no lock and a release line
    after the take. With the stand-in holder, which ignores its own maximum (a slot command hung past its bounds), the
    real Runner's outer stop ends it, and the lock is removed with a release line naming the outer stop. With
    IDLE_LOCK_PATH (the real hold) the hold's own maximum ends it first. When the Runner cannot confirm the tree gone,
    the lock stays and the stage says so."""
    import types
    real = os.environ.get("IDLE_LOCK_PATH")
    holder = real or str(HERE / "fixtures" / "standin_hold.py")
    with tempfile.TemporaryDirectory() as t:
        root = Path(t) / "runs"
        camp = root / "campaign"
        camp.mkdir(parents=True)
        prefix = [sys.executable, holder, "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180",
                  "--slot-wait-min", "0", "--wait-timeout-min", "0"]
        if real:
            prefix += ["--runs-root", str(root), "--min-free-gb", "1", "--no-wait"]
        else:
            prefix.remove("hold")
        ctx = types.SimpleNamespace(out=camp, id="m", machine={"heavyPrefix": prefix, "lockDir": str(camp)})
        margin = getattr(EP, "HOLD_MARGIN_MIN", None)
        try:
            if margin is not None:
                EP.HOLD_MARGIN_MIN = 0.05      # the outer stop a few seconds past the hold, so the case runs quickly
            r = EP.child(ctx, Path(t) / "ledger", "evaluate-01", [sys.executable, "-c", "import time; time.sleep(120)"],
                         heavy=True, deadline_min=0.25)
        finally:
            if margin is not None:
                EP.HOLD_MARGIN_MIN = margin
        lock = camp / "HEAVY-LOCK-m-evaluate-01"
        steps = [(x.get("step"), x.get("reason")) for x in _lock_log(camp) if str(x.get("lock", "")).endswith(lock.name)]
        if real:
            assert r.get("stopped") or r.get("exitCode") == 124, f"the hold or the Runner ended the job: {r}"
        else:
            assert r.get("stopped") == "deadline" and r.get("treeStopped") is True, f"the Runner stopped the tree: {r}"
        problems = []
        if lock.exists():
            problems.append(f"a lock is left after a confirmed tree stop: {lock.name} (log steps {steps})")
        names = [s for s, _ in steps]
        if not ("taken" in names and "released" in names[names.index("taken") + 1:]):
            problems.append(f"idle_lock.log has no release line after the take: {steps}")
        elif not real and not any(s == "released" and ("outer" in str(w) or "deadline" in str(w)) for s, w in steps):
            problems.append(f"the release line does not name the outer stop: {steps}")

    class Unconfirmed:
        def __init__(self, *a, **k):
            pass

        def run(self, jobs, echo=False):
            argv = jobs[0]["argv"]
            Path(argv[argv.index("--lock") + 1]).write_text('{"pid": 999999}', encoding="utf-8")
            return [{"exitCode": None, "stopped": "deadline", "treeStopped": False}]
    saved = EP.Runner
    with tempfile.TemporaryDirectory() as t:
        camp = Path(t)
        ctx = types.SimpleNamespace(out=camp, id="m", machine={"heavyPrefix": ["slot", "--lock", "{lock}", "--purpose",
                                                                               "{purpose}", "--max-min", "180"],
                                                               "lockDir": str(camp)})
        try:
            EP.Runner = Unconfirmed
            try:
                refuses(lambda: EP.child(ctx, camp / "ledger", "evaluate-01", ["x"], heavy=True, deadline_min=1),
                        "not confirmed")
            except AssertionError as ex:
                problems.append(f"an unconfirmed tree stop must leave the lock and say so: {ex}")
        finally:
            EP.Runner = saved
        if not (camp / "HEAVY-LOCK-m-evaluate-01").exists():
            problems.append("an unconfirmed stop removed the lock")
    assert not problems, "; ".join(problems)


def case_63_the_outer_deadline_sits_past_the_slot_commands_own_bounds():
    """The Runner's deadline counts from launch, so it must cover the slot command's turn wait, idle wait and hold;
    the hold's own maximum is the stage's minutes, so the slot command stops its job and releases first."""
    import types
    seen = []

    class FakeRunner:
        def __init__(self, *a, **k):
            seen.append(k.get("deadline"))

        def run(self, jobs, echo=False):
            seen.append(jobs[0]["argv"])
            return [{"exitCode": 0}]
    prefix = ["python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180",
              "--wait-timeout-min", "240", "--min-free-gb", "16"]
    saved = EP.Runner
    with tempfile.TemporaryDirectory() as t:
        ctx = types.SimpleNamespace(out=Path(t), id="m", machine={"heavyPrefix": prefix, "lockDir": t})
        try:
            EP.Runner = FakeRunner
            import time as _t
            now = _t.perf_counter()
            EP.child(ctx, Path(t), "records-capture", ["x"], heavy=True, deadline_min=460, hold_min=460)
            EP.child(ctx, Path(t), "acquire", ["x"], heavy=True, deadline_min=60)
            EP.child(ctx, Path(t), "plan", ["x"], deadline_min=90)
        finally:
            EP.Runner = saved
    d1, a1, d2, a2, d3, _ = seen
    m1, m2, m3 = ((d - now) / 60 for d in (d1, d2, d3))
    assert a1[a1.index("--max-min") + 1] == "460", f"the hold is the stage's 460 min, got {a1[:12]}"
    assert m1 > 240 + 240 + 460, f"the outer deadline ({m1:.0f} min) must sit past slot wait 240 + idle wait 240 + hold 460"
    assert a2[a2.index("--max-min") + 1] == "60", f"a child with only a deadline holds the slot that long, got {a2[:12]}"
    assert m2 > 240 + 240 + 60, f"capture's outer deadline ({m2:.0f} min) must sit past its waits and its 60-min hold"
    assert 89 < m3 <= 90.1, f"a light child keeps its own deadline, got {m3:.1f} min"


def case_64_the_review_packet_names_no_media_bundle_the_report_did_not_use():
    import types
    def packet(media_items, media_dir):
        with tempfile.TemporaryDirectory() as t:
            out = Path(t)
            for d in ("report/report-01", "reel/reel-01/reel", "reel/reel-01/decoded", "image-observations/indexed-images",
                      "bundle/evidence", "precedent/cards"):
                (out / d).mkdir(parents=True)
            for f in ("report/report-01/report.md", "report/report-01/evidence-index.md", "reel/reel-01/reel/reel.mp4",
                      "reel/reel-01/reel/reel-map.json"):
                (out / f).write_text("x", encoding="utf-8")
            if media_dir:
                (out / "report" / "media-bundle" / "evidence").mkdir(parents=True)
            led = EP.StageLedger(out)
            led.record("report", {"a": 1}, [out / "report/report-01/report.md"], exit_code=0,
                       extra={"bound": {"captures": media_items, "mediaItems": media_items}})
            ctx = types.SimpleNamespace(out=out)
            EP.stage_deliver(ctx, {}, out / "deliver")
            doc = json.loads((out / "deliver" / "review-packet.json").read_text(encoding="utf-8"))
            missing = []
            for k, v in doc.items():
                if k.endswith("Absent"):        # a sentence saying why a path is not named, not a path
                    continue
                for p in (v if isinstance(v, list) else [v]):
                    if isinstance(p, str) and ("/" in p or "\\" in p) and not Path(p).exists():
                        missing.append(k)
            return doc, missing
    doc, missing = packet(0, False)
    assert not missing, f"the packet names paths that do not exist: {missing} ({doc.get('mediaBundle')})"
    assert doc.get("mediaBundle") is None and "no frame" in str(doc.get("mediaBundleAbsent")), \
        f"with no media the packet says so instead of naming a path, got {doc.get('mediaBundle')!r}, {doc.get('mediaBundleAbsent')!r}"
    doc, _ = packet(0, True)
    assert doc.get("mediaBundle") is None, "a media bundle left from an earlier generation is not named for this report"
    doc, missing = packet(3, True)
    assert doc.get("mediaBundle") and not missing, "a report that used its media bundle names it"


def case_65_a_run_with_no_frame_stops_for_a_person_with_the_true_reason():
    import types
    def stage(rows, obs=None):
        with tempfile.TemporaryDirectory() as t:
            out = Path(t)
            (out / "capture").mkdir()
            (out / "capture" / "captures.json").write_text(json.dumps(rows), encoding="utf-8")
            sdir = out / "image-observations"
            sdir.mkdir()
            if obs is not None:
                (sdir / "observations.json").write_text(json.dumps(obs), encoding="utf-8")
            try:
                return EP.stage_image_observations(types.SimpleNamespace(out=out), {}, sdir)
            except (EP.Awaiting, EP.Refusal) as ex:
                return ex
    got = stage([])
    assert "every capture request was refused" not in str(got), f"nothing was asked, yet the sentence says refused: {got}"
    assert isinstance(got, EP.Awaiting), f"a run with no frame stops for a person, got {type(got).__name__}: {got}"
    assert "asked for no" in str(got) and "nothing to observe" in str(got), f"the sentence says no picture was asked: {got}"
    refused = [{"id": "capture-a", "status": "refused", "reason": "points-empty-pick"},
               {"id": "capture-b", "refused": "capture wrote no result (exit None)"}]
    got = stage(refused)
    assert isinstance(got, EP.Awaiting) and "2 capture requests" in str(got) and "refused" in str(got), \
        f"when every request was refused the sentence counts them, got {type(got).__name__}: {got}"
    got = stage([], {"frames": [], "acknowledged": "no picture is right: the plan asked for none"})
    assert isinstance(got, tuple) and got[0].get("images") == 0, f"an acknowledged empty packet is accepted, got {got}"


def case_66_capture_holds_the_machine_slot_once_across_its_rounds():
    """The real stage_capture, its child() and its dispatch, with only the Runner faked: with a slot command the stage
    makes ONE heavy launch (its rounds inside the slot command's job, whose request children inherit the slot), held
    for every round; never a launch, and a slot turn, per request child."""
    import types
    jobs = []

    class FakeRunner:
        def __init__(self, *a, **k):
            pass

        def run(self, js, echo=False):
            job = js[0]
            jobs.append(job)
            argv = job["argv"]
            if "capture-rounds" in argv:      # what the subcommand writes when its rounds finish
                o = Path(argv[argv.index("--out") + 1]) / "capture"
                (o / "captures.json").write_text("[]", encoding="utf-8")
                (o / "rounds.json").write_text("[]", encoding="utf-8")
                (o / "capture-rounds.json").write_text('{"accepted": true, "bound": {"requests": 0}}', encoding="utf-8")
            if job["id"].startswith("storyboard-"):
                Path(argv[argv.index("--out") + 1]).write_text('{"capturePlan": {"requests": []}}', encoding="utf-8")
            return [{"exitCode": 0}]
    saved = EP.Runner, EP.report_plan_argv, EP.capture_identity
    with tempfile.TemporaryDirectory() as t:
        out = Path(t)
        (out / "evaluation").mkdir()
        (out / "report-plan").mkdir()
        (out / "evaluation" / "plan.json").write_text(json.dumps({"arms": [
            {"armId": "baseline", "candidateFile": "c0.json"}, {"armId": "candidate-01", "candidateFile": "c1.json"}]}),
            encoding="utf-8")
        req = [{"id": f"capture-{i}", "armId": "baseline", "vignette": {"withoutArmId": "candidate-01"},
                "camera": {"support": {"supported": True}}} for i in range(2)]
        (out / "report-plan" / "storyboard.json").write_text(json.dumps({"capturePlan": {"requests": req}}), encoding="utf-8")
        (out / "capture").mkdir()
        prefix = ["python", "idle_lock.py", "hold", "--lock", "{lock}", "--purpose", "{purpose}", "--max-min", "180"]
        ctx = types.SimpleNamespace(out=out, id="m", machine={"heavyPrefix": prefix, "lockDir": t}, entry={},
                                    instance=out, profile="p", game=out, entry_path=out / "entry.json",
                                    machine_path=out / "machine.json")
        try:
            EP.Runner = FakeRunner
            EP.report_plan_argv, EP.capture_identity = (lambda *a, **k: ["x"]), (lambda c: {})
            try:
                EP.stage_capture(ctx, {}, out / "capture")
            except EP.Refusal:
                pass
        finally:
            EP.Runner, EP.report_plan_argv, EP.capture_identity = saved
    heavy = [j for j in jobs if "idle_lock.py" in j["argv"]]
    bound = EP.MAX_CAPTURE_ROUNDS * EP.MAX_NEW_PER_ROUND * 2 * 60
    assert len(heavy) == 1, f"the stage takes the machine slot once, not per child: {[j['id'] for j in heavy]}"
    argv = heavy[0]["argv"]
    assert "capture-rounds" in argv, f"the one heavy launch runs the capture rounds, got {argv[argv.index('--') + 1:][:4]}"
    assert float(argv[argv.index("--max-min") + 1]) >= bound, f"its hold covers every round ({bound} min), got {argv[:12]}"


def case_68_a_stage_after_the_bundle_runs_again_when_its_tools_change():
    """F7: the capture stage binds the code it runs. The driver under test is copied into a scratch harness with stand-in
    tools beside it (tools resolve next to the driver), run through the capture, then a tool is edited. Wrong answers:
    the capture reused after capture_points.py changed (the hand edit of stages.json in the WRW rerun); a stage re-run
    after an edit to a file no stage runs; report-plan left reused after evalreel.py, which it runs, changed."""
    import shutil
    import types
    problems = []
    with tempfile.TemporaryDirectory() as t:
        root = Path(t) / "repo"
        harness = root / "_geometry-harness"
        module = root / "integration" / "housecarl"   # the driver and the tools it starts are the module's
        (harness / "viewprobe" / "tools").mkdir(parents=True)
        module.mkdir(parents=True)
        (root / "scripts").mkdir()
        shutil.copy(_path, module / "evalpipeline.py")
        for name in ("toolidentity.py",):
            shutil.copy(SW.HARNESS / name, harness / name)
        shutil.copy(SW.SCRIPTS / "runlog.py", root / "scripts" / "runlog.py")
        for rel in ("capture_acquire.py", "capture_points.py", "evalreport.py", "evalreel.py", "unrelated.py"):
            (module / rel).write_text("# stand-in tool\n", encoding="utf-8")
        for rel in ("viewprobe/tools/shotplan.py", "evalrun.py"):
            (harness / rel).write_text("# stand-in tool\n", encoding="utf-8")
        spec = importlib.util.spec_from_file_location("evalpipeline_copy68", module / "evalpipeline.py")
        ep = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(ep)
        out = Path(t) / "run"
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
        calls = []

        class FakeCtx:
            def __init__(self, entry, machine, o):
                self.entry, self.machine, self.out, self.id = {"evaluation": {"budgetMinutes": 5}}, {}, Path(o), "m"

            def resolve(self):
                return {"arms": [], "subject": ["S.esp"], "keys": []}

        def light(name):
            def body(ctx, res, sdir):
                calls.append(name)
                f = sdir / f"{name}.json"
                f.write_text("{}", encoding="utf-8")
                return {}, [f]
            return body
        try:
            ep.Context = FakeCtx
            for n in ep.STAGES:
                ep.STAGE_BODIES[n] = light(n)
            a = types.SimpleNamespace(entry="e.json", machine=None, out=str(out), through="capture")

            def run_once():
                calls.clear()
                rc = ep.cmd_run(a)
                return rc, list(calls)
            rc1, first = run_once()
            rc2, again = run_once()
            (module / "capture_points.py").write_text("# stand-in tool, changed\n", encoding="utf-8")
            rc3, after_tool = run_once()
            (module / "unrelated.py").write_text("# changed, but no stage runs it\n", encoding="utf-8")
            rc4, after_other = run_once()
            (module / "evalreel.py").write_text("# changed: report-plan and capture both run it\n", encoding="utf-8")
            rc5, after_reel = run_once()
        finally:
            os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
    if rc1 != EP.EXIT_OK or "capture" not in first:
        problems.append(f"the first run did not run through the capture (exit {rc1}, ran {first})")
    if again:
        problems.append(f"an unchanged run re-ran {again}")
    if after_tool != ["capture"]:
        problems.append(f"capture was reused after capture_points.py changed, or another stage re-ran with it: {after_tool}")
    if after_other:
        problems.append(f"an edit to a file no stage runs re-ran {after_other}")
    if after_reel != ["report-plan", "capture"]:
        problems.append(f"after evalreel.py changed, report-plan and capture must both run again, got {after_reel}")
    assert not problems, "; ".join(problems)


# ---- C2320-C2323: stage keys and kept receipts (item 39 follow-ups 2 and 3) ----

def _fake_pipeline(entry_box: dict, calls: list, refuse_when=None, res=None, machine_doc=None):
    """A FakeCtx whose entry is read from entry_box at each run, and light bodies for every stage that record their call;
    `refuse_when(name, ctx, sdir)` returning a sentence makes that stage refuse (the bundle's own 'exists' refusal)."""
    class FakeCtx:
        def __init__(self, entry, machine, o):
            self.entry, self.out, self.id = json.loads(json.dumps(entry_box["entry"])), Path(o), "m"
            self.machine = dict(machine_doc or {})

        def resolve(self):
            return json.loads(json.dumps(res)) if res is not None else {"arms": [], "subject": ["S.esp"], "keys": []}

    def light(name):
        def body(ctx, res_, sdir):
            calls.append(name)
            why = refuse_when(name, ctx, sdir) if refuse_when else None
            if why:
                raise EP.Refusal(why)
            f = sdir / f"{name}.json"
            f.write_text("{}", encoding="utf-8")
            return {}, [f]
        return body
    return FakeCtx, {n: light(n) for n in EP.STAGES}


def _run_pipeline(t, entry_box, calls, runs, refuse_when=None, res=None, machine=None):
    """Run the driver once per entry in `runs` (each replaces entry_box['entry']); returns [(exit, stages run)]."""
    import types
    saved = EP.Context, dict(EP.STAGE_BODIES)
    out = Path(t) / "run"
    os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
    got = []
    try:
        fake, bodies = _fake_pipeline(entry_box, calls, refuse_when, res, machine)
        EP.Context = fake
        EP.STAGE_BODIES.update(bodies)
        a = types.SimpleNamespace(entry="e.json", machine=None, out=str(out), through=None)
        for e in runs:
            entry_box["entry"] = e
            calls.clear()
            rc = EP.cmd_run(a)
            got.append((rc, list(calls)))
    finally:
        EP.Context = saved[0]
        EP.STAGE_BODIES.clear()
        EP.STAGE_BODIES.update(saved[1])
        os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
    return out, got


AFTER_REPORT_PLAN = ["report-plan", "capture", "image-observations", "report", "reel", "deliver"]


def case_2320_each_stage_binds_the_presentation_keys_its_code_reads():
    """C2320: which presentation key each stage reads is derived from the driver's code (the stage body and the module
    functions it calls), not a hand list. Wrong answers: the bundle bound to patchSearch (it never reads it); report-plan
    or capture not bound to patchSearch (report_plan_argv passes it); a measuring stage bound to any presentation key."""
    reads = EP.stage_entry_keys()
    want = {"bundle": ["modNames"],
            "report-plan": ["modNames", "nexusNames", "patchSearch", "report"],
            "capture": ["captureSize", "modNames", "nexusNames", "patchSearch", "report"],
            "report": ["modNames", "nexusNames", "patchSearch", "report"]}
    for s in EP.STAGES:
        assert sorted(reads.get(s, [])) == want.get(s, []), f"{s} reads {sorted(reads.get(s, []))}, expected {want.get(s, [])}"
    # a read the derivation cannot attribute to a key is refused, never left out
    head = "def stage_deliver(ctx: Context, res: dict, sdir: Path) -> tuple[dict, list]:\n"
    text = Path(_path).read_text(encoding="utf-8")
    assert head in text, "stage_deliver's signature moved; update this case"
    for bad in ('    k = "patchSearch"\n    print(ctx.entry[k])\n', "    print(json.dumps(ctx.entry))\n"):
        refuses(lambda: EP.stage_entry_keys(text.replace(head, head + bad, 1)), "cannot attribute")


def case_2321_a_patch_search_change_reruns_report_plan_and_later_not_the_bundle():
    """C2321 (item 2): after a run through deliver, adding a patch search reruns report-plan and the stages after it,
    and not the bundle or precedent; a mod-name change reruns the bundle. Wrong answer (Hroggar 2026-11-09): the bundle
    re-ran for a patch search it never reads, and refused over its own earlier folder."""
    base = {"evaluation": {"budgetMinutes": 5}, "modNames": {"S.esp": "S"}}
    with tempfile.TemporaryDirectory() as t:
        _, got = _run_pipeline(t, {}, [], [base, dict(base, patchSearch="X:/ps.json"),
                                           dict(base, patchSearch="X:/ps.json", modNames={"S.esp": "Ess"})])
    (rc1, first), (rc2, after_search), (rc3, after_names) = got
    assert rc1 == EP.EXIT_OK and first == list(EP.STAGES), f"the first run ran {first} (exit {rc1})"
    assert after_search == AFTER_REPORT_PLAN, f"a patch-search change re-ran {after_search}"
    assert after_names == ["bundle", "precedent"] + AFTER_REPORT_PLAN, f"a mod-name change re-ran {after_names}"


def case_2323_a_refused_stage_keeps_its_accepted_receipt_so_restoring_the_input_restores_it():
    """C2323 (item 3): the bundle refuses to write over its own folder; a mod-name change reruns it and it refuses;
    putting the name back reuses the earlier accepted bundle and every stage after it. Wrong answer (Hroggar): the
    refusal dropped the receipt (stages.json bundle null), so restoring the entry ran the bundle again into the same
    refusal."""
    def bundle_exists(name, ctx, sdir):
        if name != "bundle":
            return None
        if (sdir / "made").exists():
            return "bundle/evidence exists from an earlier bundle; move it aside rather than overwrite it."
        (sdir / "made").write_text("1", encoding="utf-8")
        return None
    e = {"evaluation": {"budgetMinutes": 5}, "modNames": {"S.esp": "S"}}
    with tempfile.TemporaryDirectory() as t:
        out, got = _run_pipeline(t, {}, [], [e, dict(e, modNames={"S.esp": "Ess"}), e], refuse_when=bundle_exists)
        stages = EP.StageLedger(out).doc["stages"]
    (rc1, first), (rc2, changed), (rc3, restored) = got
    assert rc1 == EP.EXIT_OK and first == list(EP.STAGES), f"the first run ran {first} (exit {rc1})"
    assert rc2 == EP.EXIT_REFUSED and changed == ["bundle"], f"the changed entry ran {changed} (exit {rc2})"
    assert rc3 == EP.EXIT_OK and restored == [], f"restoring the entry re-ran {restored} (exit {rc3})"
    assert stages.get("bundle", {}).get("accepted") is True, "the bundle's accepted receipt is not back in stages.json"


# ---- C2326-C2327: disk from measured per-record figures (item 39 follow-up 6) ----

def write_counted_plugin(path: Path, records: int):
    """A TES4 header with a HEDR subrecord stating `records` (version, record count, next object id)."""
    import struct as st
    body = b"HEDR" + (12).to_bytes(2, "little") + st.pack("<fII", 1.71, records, 0x800)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"TES4" + len(body).to_bytes(4, "little") + bytes(16) + body)


def _counted_res(t: Path) -> dict:
    heads = {}
    for name, n in (("Base.esm", 100), ("A.esp", 10), ("B.esp", 7)):
        write_counted_plugin(Path(t) / "plugins" / name, n)
        heads[name] = {"path": str(Path(t) / "plugins" / name)}
    return {"headers": heads, "baseline": ["Base.esm"], "subject": ["A.esp"], "keys": [],
            "arms": [{"id": "subject", "plugins": ["Base.esm", "A.esp"]},
                     {"id": "combined", "plugins": ["Base.esm", "A.esp", "B.esp"]}]}


def case_2326_a_stage_receipt_records_the_bytes_it_added_with_each_file_once():
    """C2326: a receipt's disk record counts a hard-linked file once and a new file fully, and the run's evaluated
    records are the arms' non-baseline HEDR counts summed over the arms. Wrong answers: summed file sizes (Hroggar's
    103.85 GB apparent against 8.76 GB stored); a count that includes the baseline or counts a plugin once overall."""
    with tempfile.TemporaryDirectory() as t:
        d = Path(t) / "u"
        d.mkdir()
        (d / "a.bin").write_bytes(b"x" * 1000)
        os.link(d / "a.bin", d / "b.bin")
        (d / "c.bin").write_bytes(b"y" * 500)
        assert EP.unique_bytes(d) == 1500, f"unique bytes {EP.unique_bytes(d)}, expected 1500 (a link is one file)"
        res = _counted_res(Path(t))
        assert EP.evaluated_records(res) == 27, f"evaluated records {EP.evaluated_records(res)}, expected 10 + 17"

        def write(name, ctx, sdir):
            if name == "records":
                (sdir / "transcript.bin").write_bytes(b"z" * 4000)
            if name == "bundle":
                os.link(ctx.out / "records" / "transcript.bin", sdir / "transcript.bin")
            return None
        out, got = _run_pipeline(t, {}, [], [{"evaluation": {}}], refuse_when=write, res=res)
        stages = EP.StageLedger(out).doc["stages"]
    assert got[0][0] == EP.EXIT_OK, f"the run did not finish: {got}"
    rec, bun = stages["records"].get("disk") or {}, stages["bundle"].get("disk") or {}
    assert rec == {"addedBytes": 4002, "evaluatedRecords": 27}, f"records stage disk {rec}, expected 4000 + its 2-byte receipt"
    assert bun.get("addedBytes") == 2, f"the bundle's linked transcript was counted again: {bun}"


def _figure_run(parent: Path, name: str, records_bytes: int, n: int = 100):
    d = parent / name
    d.mkdir(parents=True)
    (d / "stages.json").write_text(json.dumps({"schema": 1, "history": [], "stages": {
        "records": {"accepted": True, "disk": {"addedBytes": records_bytes, "evaluatedRecords": n}},
        "bundle": {"accepted": True, "disk": {"addedBytes": 500, "evaluatedRecords": n}}}}), encoding="utf-8")
    return d


def case_2327_the_disk_forecast_uses_measured_figures_names_them_and_keeps_the_floor():
    """C2327: the forecast is evaluated records x the largest measured per-record figure of each stage still to run,
    names the run and stage it was measured at, and refuses when free space minus the slot's --min-disk floor is short;
    with no figure it says so and the run goes on; the machine file's diskPerRecord fills in with its source named.
    Wrong answers (round 2 Gate A, six times low): a figure from one stage's files alone, a floor ignored, a silent skip."""
    import shutil as sh
    import types
    with tempfile.TemporaryDirectory() as t:
        parent = Path(t) / "runs"
        out = parent / "this"
        out.mkdir(parents=True)
        _figure_run(parent, "older", 2000)
        _figure_run(parent, "newer", 3000)
        drive = os.path.splitdrive(os.path.abspath(out))[0]
        ctx = types.SimpleNamespace(out=out, machine={})
        line, short = EP.disk_forecast(ctx, 1000, ["records", "bundle"])
        assert short is None, f"a 35 KB forecast refused: {short}"
        assert "about 34.2 KiB" in line and "newer" in line and "records" in line and "older" not in line, \
            f"the forecast does not use and name the larger figure (30 B per record, newer): {line}"
        need = 10 ** 9 * 35
        free = sh.disk_usage(out).free / EP.GIB
        floor = max(free - need / EP.GIB / 2, 0)
        ctx.machine = {"heavyPrefix": ["python", "idle_lock.py", "--min-disk", f"{drive}={floor}"]}
        line, short = EP.disk_forecast(ctx, 10 ** 9, ["records", "bundle"])
        assert short and "newer" in short and f"{floor:g} GiB floor" in short and "32.6 GiB" in short, \
            f"the floor was not kept, or the refusal does not name the figure's run: {short}"
        empty = Path(t) / "alone" / "this"
        empty.mkdir(parents=True)
        ctx = types.SimpleNamespace(out=empty, machine={})
        line, short = EP.disk_forecast(ctx, 1000, ["records"])
        assert short is None and "no measured per-record figure" in line, f"no figure must be said plainly: {line}"
        ctx.machine = {"diskPerRecord": {"bytes": 17920, "source": "Hroggar 2026-11-15 unique bytes"}}
        line, short = EP.disk_forecast(ctx, 1000, ["records"])
        assert short is None and "Hroggar 2026-11-15" in line and "17.5 KiB" in line, f"diskPerRecord not used: {line}"
        res = _counted_res(Path(t))
        calls = []
        o2, got = _run_pipeline(Path(t) / "alone", {}, calls, [{"evaluation": {}}], res=res, machine={
            "diskPerRecord": {"bytes": 10 ** 6, "source": "a test figure"},
            "heavyPrefix": ["--min-disk", f"{drive}=1000000000"]})
        status = json.loads((o2 / "status.json").read_text(encoding="utf-8"))
    assert got[0] == (EP.EXIT_REFUSED, []), f"a short disk must stop the run before any stage, got {got}"
    assert "a test figure" in status.get("sentence", ""), f"the refusal does not name its figure: {status}"


# ---- C2324: patch-search pairs recorded before a subject runs (item 39 follow-up 5) ----

TOOLS2 = ["housecarl_nexus_mod", "housecarl_nexus_file_search"]


def _search_doc(t: Path, pairs: dict) -> str:
    """A patch-search file: {(a, b): [tools]} -> pairs with one query per tool."""
    f = Path(t) / "patch-search.json"
    f.write_text(json.dumps({"schemaVersion": 1, "pairs": [
        {"pair": list(k), "queries": [{"tool": x} for x in v], "hits": []} for k, v in pairs.items()]}), encoding="utf-8")
    return str(f)


def case_2324_a_run_through_report_plan_needs_every_partner_pair_searched_before_census():
    """C2324 (item 5): before census, a run that goes through report-plan refuses when the entry's patchSearch is
    absent or does not cover the subject with every partner unit's plugins (with every required tool), naming every
    missing pair at once; a covered entry runs, and a run that stops before report-plan is not checked. Wrong answer
    (Hroggar round 2): no check, so report-plan stopped hours later on a pair the entry already named."""
    import types
    res = {"arms": [], "subject": ["S.esp"], "keys": [], "partners": [["PA.esp"], ["PB.esp", "PBm.esp"]]}
    with tempfile.TemporaryDirectory() as t:
        ctx = types.SimpleNamespace(entry={}, out=Path(t), machine={})
        why = EP.patch_search_refusal(ctx, res, "deliver")
        assert why and all(x in why for x in ("PA.esp and S.esp", "PB.esp and S.esp", "PBm.esp and S.esp")), \
            f"no patchSearch must refuse naming every partner pair: {why}"
        ctx.entry = {"patchSearch": _search_doc(t, {("PA.esp", "S.esp"): TOOLS2, ("S.esp", "PB.esp"): TOOLS2[:1],
                                                     ("PBm.esp", "S.esp"): TOOLS2})}
        why = EP.patch_search_refusal(ctx, res, "deliver")
        assert why and "PB.esp and S.esp" in why and "PA.esp" not in why and "PBm.esp" not in why, \
            f"a pair searched without every required tool is a gap, and only the gaps are named: {why}"
        ctx.entry = {"patchSearch": _search_doc(t, {("PA.esp", "S.esp"): TOOLS2, ("PB.esp", "s.esp"): TOOLS2,
                                                     ("PBm.esp", "S.esp"): TOOLS2})}
        assert EP.patch_search_refusal(ctx, res, "deliver") is None, "a covered entry (any case) must pass"
        ctx.entry = {}
        assert EP.patch_search_refusal(ctx, res, "records") is None, "a run that stops before report-plan is not checked"
        calls = []
        out, got = _run_pipeline(t, {}, calls, [{"evaluation": {}}], res=res)
        status = json.loads((out / "status.json").read_text(encoding="utf-8"))
    assert got[0] == (EP.EXIT_REFUSED, []), f"the run must stop before census, got {got}"
    assert "PB.esp and S.esp" in status.get("sentence", ""), f"the stop does not name the pairs: {status}"


def _continuation_folder(t, entry_doc):
    """A run folder as the built-in controller leaves it through the bundle: the entry, every census-to-bundle receipt
    accepted under the driver's own inputs rule, scenes refused and not blocking, and its arms and preparation."""
    import types
    out = Path(t) / "run"
    entry_p = Path(t) / "entry.json"
    entry_p.write_text(json.dumps(entry_doc), encoding="utf-8")
    ctx = EP.Context(entry_p, None, out)
    led = EP.StageLedger(out)
    reads = EP.stage_entry_keys()
    measured = {k: v for k, v in ctx.entry.items() if k not in EP.PRESENTATION_KEYS}
    for name in EP.MEASURING:
        earlier = EP.STAGES[:EP.STAGES.index(name)]
        inputs = {"entry": EP.stage_entry_hash(ctx.entry, measured, reads.get(name, [])),
                  "upstream": {s: (led.doc["stages"].get(s) or {}).get("inputsSha256") for s in earlier}}
        f = out / name / f"{name}.json"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text("{}", encoding="utf-8")
        if name == "scenes":
            led.record(name, inputs, [], exit_code=2)
            led.doc["history"][-1]["reason"] = "refused: overlap scene acquisition is not built in this version."
            led._save()
        else:
            led.record(name, inputs, [f], exit_code=0)
    (out / "prepare" / "preparation.json").write_text(json.dumps({
        "baseline": ["Base.esm"], "headers": {"S.esp": {"path": str(Path(t) / "S.esp"), "masters": ["Base.esm"]}},
        "providers": {"S.esp": "SMod"}, "arms": []}), encoding="utf-8")
    (out / "arms.json").write_text(json.dumps([
        {"id": "dependencies", "evaluated": [], "keys": [], "plugins": ["Base.esm"], "mods": []},
        {"id": "subject", "evaluated": ["S.esp", "Sc.esp"], "keys": [], "plugins": ["Base.esm", "S.esp", "Sc.esp"], "mods": ["SMod"]},
        {"id": "unit:0", "evaluated": ["PA.esp"], "keys": [], "plugins": ["Base.esm", "PA.esp"], "mods": []},
        {"id": "unit:1", "evaluated": ["PB.esp", "PBm.esp"], "keys": [], "plugins": ["Base.esm", "PBm.esp", "PB.esp"], "mods": []},
        {"id": "combined+key:K.esp", "evaluated": ["S.esp", "PA.esp", "K.esp"], "keys": ["K.esp"], "plugins": [], "mods": []},
        {"id": "combined+key:J.esp", "evaluated": ["S.esp", "PA.esp", "J.esp"], "keys": ["J.esp"], "plugins": [], "mods": []},
        {"id": "combined+keys", "evaluated": [], "keys": ["K.esp", "J.esp"], "plugins": [], "mods": []}]), encoding="utf-8")
    return out, entry_p, types


def case_2330_the_run_folders_own_record_is_what_the_later_stages_resolve():
    """C2330 (step 3): the driver no longer derives arms; it reads what the built-in controller resolved. Wrong answers:
    the subject taken from the entry rather than the subject arm (companions lost), a key listed twice, partner units
    merged, or a folder with no record read as an empty evaluation."""
    with tempfile.TemporaryDirectory() as t:
        out, entry_p, _ = _continuation_folder(t, {"schema": 1, "id": "m", "inPlace": {"instance": str(t), "profile": "P", "game": str(t)}})
        res = EP.Context(entry_p, None, out).resolve()
        assert res["subject"] == ["S.esp", "Sc.esp"], res["subject"]
        assert res["partners"] == [["PA.esp"], ["PB.esp", "PBm.esp"]], res["partners"]
        assert res["keys"] == ["K.esp", "J.esp"], res["keys"]
        assert res["baseline"] == ["Base.esm"] and "S.esp" in res["headers"] and res["providers"] == {"S.esp": "SMod"}
        assert [a["id"] for a in res["arms"]][:2] == ["dependencies", "subject"]
        # in place there is no subject arm: the profile with the selection is the combined arm
        (out / "arms.json").write_text(json.dumps([
            {"id": "combined", "evaluated": ["Sel.esp"], "keys": [], "plugins": ["Base.esm", "Sel.esp"], "mods": ["Sel"]},
            {"id": "without-selection", "evaluated": [], "keys": [], "plugins": ["Base.esm"], "mods": []}]), encoding="utf-8")
        res = EP.Context(entry_p, None, out).resolve()
        assert (res["subject"], res["partners"], res["keys"]) == (["Sel.esp"], [], []), res
        (out / "arms.json").unlink()
        refuses(lambda: EP.Context(entry_p, None, out).resolve(), "runcontrol run --entry")


def case_2331_a_fresh_run_folder_is_refused_naming_the_built_in_controller_and_nothing_runs():
    """C2331 (step 3): census to bundle are never run here. A run folder the controller has not carried through the
    bundle stops before any stage with one sentence naming the runcontrol command for this entry and folder."""
    import types
    with tempfile.TemporaryDirectory() as t:
        entry_p = Path(t) / "entry.json"
        entry_p.write_text(json.dumps({"schema": 1, "id": "m", "inPlace": {"instance": str(t), "profile": "P", "game": str(t)}}), encoding="utf-8")
        out = Path(t) / "run"
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
        try:
            rc = EP.cmd_run(types.SimpleNamespace(entry=str(entry_p), machine=None, out=str(out), through=None))
        finally:
            os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
        status = json.loads((out / "status.json").read_text(encoding="utf-8"))
        made = sorted(x.name for x in out.iterdir())
    assert rc == EP.EXIT_REFUSED, rc
    assert status["stoppedAt"] == "census" and "runcontrol run --entry" in status["sentence"] and "--through bundle" in status["sentence"], status
    assert made == ["runlog", "status.json"], f"a refused run wrote {made}"   # an in-place run keeps its run log here


def case_2332_a_controller_run_folder_is_continued_reusing_census_to_bundle_and_carrying_scenes():
    """C2332 (step 3): on a folder the controller carried through the bundle, every census-to-bundle receipt is reused,
    scenes' refusal is carried on as not blocking, and the first stage after the bundle runs. A changed entry is not
    measured here: the run stops at census naming runcontrol. Wrong answers: a measuring stage run or skipped, scenes
    blocking the run, or the bundle's receipt not reused because its entry keys were derived from no body."""
    import io, contextlib, types
    saved = dict(EP.STAGE_BODIES)
    ran = []

    def precedent(ctx, res, sdir):
        ran.append(("precedent", res["subject"]))
        f = sdir / "cards.json"
        f.write_text("{}", encoding="utf-8")
        return {}, [f]
    with tempfile.TemporaryDirectory() as t:
        doc = {"schema": 1, "id": "m", "inPlace": {"instance": str(t), "profile": "P", "game": str(t)}, "modNames": {"S.esp": "Ess"}}
        out, entry_p, _ = _continuation_folder(t, doc)
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
        try:
            EP.STAGE_BODIES["precedent"] = precedent
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                rc = EP.cmd_run(types.SimpleNamespace(entry=str(entry_p), machine=None, out=str(out), through="precedent"))
            text = buf.getvalue()
            st1 = json.loads((out / "status.json").read_text(encoding="utf-8"))
            entry_p.write_text(json.dumps(dict(doc, evaluation={"depth": "deep"})), encoding="utf-8")
            err = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                rc2 = EP.cmd_run(types.SimpleNamespace(entry=str(entry_p), machine=None, out=str(out), through="precedent"))
            st2 = json.loads((out / "status.json").read_text(encoding="utf-8"))
        finally:
            EP.STAGE_BODIES.clear()
            EP.STAGE_BODIES.update(saved)
            os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
    assert rc == EP.EXIT_OK, (rc, text)
    for name in ("census", "prepare", "scope", "plan", "evaluate", "records", "bundle"):
        assert f"{name}: reused (inputs and outputs unchanged)" in text, f"{name} was not reused: {text}"
    assert "scenes: refused, not blocking: overlap scene acquisition is not built in this version." in text, text
    assert ran == [("precedent", ["S.esp", "Sc.esp"])], ran
    assert st1.get("status") == "incomplete" and st1["refusals"][0]["stage"] == "scenes", st1
    assert rc2 == EP.EXIT_REFUSED and st2["stoppedAt"] == "census" and "runcontrol run" in st2["sentence"], (rc2, st2, err.getvalue())
    assert len(ran) == 1, "a stage ran after the entry changed under the measuring stages"


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
