"""Known answers for a navmesh clash pictured in the review reel through the pipeline's own stages (C1970-C1978).

    python tests/test_navmesh_reel.py [--only C1970,C1974]

No game and no renderer. The planner, request and report cases call the real reel_plan, report_scenes, evalreport and
evalreel functions on hand-built inputs with the SHIPPED skill data (scene-policy.json, report-priorities.json), so a
skill-data change counts. The pipeline case calls the real evalpipeline.capture_request with its child runner replaced
by a recorder. The capture cases run the real capture_acquire.py with fake_viewprobe.py, on test_capture_navmesh.py's
synthetic scene. Form keys, plugins, pixels and fingerprints are made up; nothing here is evidence about a real cell.

The navmesh shape is Clockwork's (round 2 shakedown presentation, item 24): an EDGE-IDX row names the linking navmesh
(its formKey), the navmesh it links into (other) and, as A, the plugin whose version of the linking navmesh wins. A
camera exists only where the linking navmesh is one the row's A re-cut over another plugin's version (a CLOBBERED row on
the same record with B = the row's A), outdoors: that is the source item 24's camera draws, with its earlier version.

    C1970  a camera-shaped navmesh clash is a reel vignette: its frames are the two navmesh states, both from the setup
           with the mod, bound to the captured navmesh request; it is not counted as one the reel cannot picture
    C1971  a navmesh clash with no camera (its linking navmesh not re-cut by its winner) stays unpictured, by a reason
           that is true now (never "no picture yet"), and a camera-shaped one is never given that reason
    C1972  in reel mode the navmesh request is bound to its vignette (chapter, setup without the mod, states), one request
           for a navmesh whose links reach several targets
    C1973  the pipeline captures a navmesh request with no without-arm frames: acquire and capture in the setup with the
           mod, navmesh-control in the setup without it, before capture, and capture is handed that control
    C1974  capture refuses a navmesh comparison without its control, with a control that still has a broken crossing, or
           with a control at another pose; with a clean control it captures and records the control on the result
    C1975  every new capture refusal code has its own reason in the table (C1936's rule), and the new reasons have words
    C1976  a two-frame vignette's last frame holds for its narration (it is not cut at 2.5 + 2.5 s)
    C1977  the report shows both navmesh frames as one figure, captioned in words with no form key
    C1978  the navmesh vignette's narration names the place and both mods, says the control's result, and speaks no
           form key or editor ID; its frame labels say which version each frame shows

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
SKILL = HARNESS.parent / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []

STATES = ["winning-linked-navmeshes", "historical-navmesh-context"]
TARGET, EARLIER, OTHER = "Later.esp", "Earlier.esp", "Third.esp"
SRC, TGT = "000100:World.esm", "000200:World.esm"          # camera-shaped: Later.esp re-cut SRC over Earlier.esp's
NEIGH = "000400:World.esm"                                 # no camera: Earlier.esp's own navmesh links into SRC
WITH, WITHOUT = "with-later", "without-later"
FORM_KEY = re.compile(r"\b[0-9A-Fa-f]{6}:[^\s,;.]+")


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    """`wrong` names the wrong answer the assertion rejects; it is what a red case prints."""
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return cond


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def shipped():
    pol = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))
    pri = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
    return pol, pri


# ---------------------------------------------------------------- the planner fixture (Clockwork's shape) --

def nav_row(fk, a, b, other, cell, k, n, chain):
    return {"checkId": "geometry.navmesh-ownership", "kind": "EDGE-IDX", "A": a, "B": b, "formKey": fk,
            "summary": f"navmesh {fk} in {cell} (ext) edge-links {other} triangle {k} of {n}   [record provided by: {chain}]",
            "admission": "admitted", "findingId": "F-" + fk[:6], "other": [other], "physical": "Holds",
            "attribution": "with-target", "attributionMod": TARGET, "withPatch": "left"}


def planner_fixture(captured: bool = True):
    record = [
        # history: Later.esp re-cuts SRC over Earlier.esp's version
        {"checkId": "geometry.navmesh-ownership", "kind": "CLOBBERED", "A": EARLIER, "B": TARGET, "formKey": SRC,
         "summary": f"1 navmesh record(s) re-cut by {EARLIER} and then re-cut again by {TARGET}", "admission": "history",
         "findingId": "F-H1", "physical": "Holds"},
        # camera-shaped: SRC (won by Later.esp, which re-cut it) links past TGT's triangle count
        nav_row(SRC, TARGET, EARLIER, TGT, "RiverTownExterior01", 14, 10, f"World.esm -> {EARLIER} -> {TARGET}"),
        # no camera: Earlier.esp's own navmesh NEIGH links into the re-cut SRC
        nav_row(NEIGH, EARLIER, TARGET, SRC, "RiverTownExterior04", 748, 549, f"World.esm -> {EARLIER}"),
        nav_row(NEIGH, EARLIER, TARGET, SRC, "RiverTownExterior04", 753, 549, f"World.esm -> {EARLIER}"),
    ]
    order = ["World.esm", EARLIER, TARGET]
    pres = {"schemaVersion": 1, "presentationId": "navmesh-reel", "source": {"bundleDir": "."},
            "reader": {"prediction": {"subject": TARGET, "mods": [TARGET], "predictionArm": WITH,
                                      "withoutArm": WITHOUT, "record": record, "items": []},
                       "checkTitles": {"geometry.navmesh-ownership": "Navmesh links"}},
            "scope": {"arms": [{"armId": WITH, "locationPluginOrder": order, "plugins": order},
                               {"armId": WITHOUT, "locationPluginOrder": order[:-1], "plugins": order[:-1]}]},
            "findings": [], "media": [], "chapters": []}
    captures = []
    if captured:
        pres["media"] = [{"id": f"M-nav-{s}", "assessed": True, "mediaKind": "offline-render",
                          "subject": {"checkId": "geometry.navmesh-ownership", "armId": WITH, "formKey": SRC},
                          "provenance": {"captureRequest": "req-nav", "vignetteState": s, "poseId": "pose-nav",
                                         "viewKind": "top-down", "viewLabel": "SLICED TOP-DOWN",
                                         # the historical frame's own renderer command names the version it draws
                                         **({"argv": ["--nav", "both", "--nav-version", f"{SRC}={EARLIER}"]}
                                            if s == STATES[1] else {})}} for s in STATES]
        captures = [{"request": "req-nav", "status": "captured", "formKey": SRC, "recordKeys": [SRC, TGT],
                     "requestType": "navmesh-version-comparison",
                     "navmeshControl": {"status": "no-broken-crossing", "brokenCrossings": 0, "poseId": "pose-nav"}}]
    search = {"schemaVersion": 1, "pairs": [{"pair": [EARLIER, TARGET], "hits": [], "unreadSources": [],
                                             "queries": [{"tool": t, "query": t, "responseSha256": "0" * 64, "date": "2026-11-12"}
                                                         for t in ("housecarl_nexus_mod", "housecarl_nexus_file_search")]}],
              "knownAnswer": {}}
    mods = {TARGET: {"name": "Later Mod", "source": "nexus-catalogue"}, EARLIER: {"name": "Earlier Mod", "source": "nexus-catalogue"},
            "World.esm": {"name": "The World", "source": "nexus-catalogue"}}
    return pres, {}, {}, search, mods, captures


def plan(captured=True, final=True):
    import reel_plan as RP
    pol, pri = shipped()
    pres, names, masters, search, mods, captures = planner_fixture(captured)
    return RP.plan_reel(copy.deepcopy(pres), names, masters, pol["vignettes"], pri["reel"]["words"], search, mods,
                        captures, captures_final=final), pol, pri


def nav_chapters(reel):
    return [ch for ch in reel.get("chapters") or [] if (ch.get("clash") or {}).get("type") == "navmesh"]


def unpictured_of(reel):
    return [c for ch in reel.get("chapters") or [] if ch.get("kind") == "unpictured" for c in ch.get("clashes") or []]


def c1970_navmesh_vignette(tmp: Path):
    """Item 24 left navmesh a counted row: the reel said "A navmesh clash has no picture yet" for a clash a camera now
    pictures. Wrong answers: no navmesh vignette; frames other than the two navmesh states; a frame from the setup
    without the mod; a frame without the captured request's media; the pictured clash also counted unpictured."""
    reel, _pol, _pri = plan()
    chs = nav_chapters(reel)
    if not check("C1970", reel.get("status") == "planned" and len(chs) == 1 and chs[0].get("kind") == "vignette",
                 "a camera-shaped navmesh clash is not planned as one reel vignette",
                 f"status {reel.get('status')} {reel.get('reason') or ''}; navmesh chapters {[c.get('kind') for c in chs]}"):
        return
    ch = chs[0]
    fr = ch.get("frames") or []
    check("C1970", [f.get("state") for f in fr] == STATES and all(f.get("armId") == WITH for f in fr),
          "the navmesh vignette's frames are not the winning then historical state, both in the setup with the mod",
          json.dumps(fr)[:300])
    check("C1970", [f.get("mediaId") for f in fr] == [f"M-nav-{s}" for s in STATES],
          "the navmesh vignette's frames are not the captured request's media", str([f.get("mediaId") for f in fr]))
    check("C1970", (ch.get("clash") or {}).get("formKey") == SRC and (ch.get("clash") or {}).get("withoutArmId") == WITHOUT,
          "the vignette's clash is not the re-cut navmesh with its setup without the mod", json.dumps(ch.get("clash"))[:300])
    check("C1970", SRC not in [c.get("formKey") for c in unpictured_of(reel)],
          "the pictured navmesh clash is also counted as one the reel cannot picture")


def c1971_navmesh_reason_true(tmp: Path):
    """Wrong answers: a navmesh clash with no camera said as "no picture yet" (or pictured); a camera-shaped clash given
    the no-camera reason; the shipped reason still promising a picture to come."""
    reel, pol, _pri = plan()
    un = {c.get("formKey"): c for c in unpictured_of(reel)}
    text = str((pol["vignettes"].get("stillReasons") or {}).get("navmesh") or "")
    check("C1971", NEIGH in un and "yet" not in str(un[NEIGH].get("reasonText") or "yet").lower()
          and "navmesh" in pol["vignettes"].get("pictureTypes", []),
          "a navmesh clash with no camera is not said by a reason true now, or navmesh is not a picture type",
          f"unpictured {sorted(un)}; reason {json.dumps(un.get(NEIGH))[:200]}; pictureTypes {pol['vignettes'].get('pictureTypes')}")
    check("C1971", "yet" not in text.lower() and text,
          "the shipped navmesh still reason still promises a picture to come", repr(text))
    reel2, _p, _w = plan(captured=False, final=False)
    asked = [ch for ch in nav_chapters(reel2) if (ch.get("clash") or {}).get("formKey") == SRC]
    check("C1971", bool(asked) and (un.get(SRC) is None),
          "inside the capture rounds the camera-shaped clash is not asked for as a vignette",
          f"chapters {[(c.get('kind'), (c.get('clash') or {}).get('formKey')) for c in nav_chapters(reel2)]}")


# ---------------------------------------------------------------- the request (reel mode) --

def c1972_reel_binds_navmesh_request(tmp: Path):
    """Wrong answers: the navmesh candidate dropped in reel mode (the reel decides what is pictured and had no navmesh
    vignette); a request with no vignette, no setup without the mod or other states; two requests for one source whose
    links reach two targets."""
    TN = load("nir_capture_navmesh", HERE / "test_capture_navmesh.py")
    import report_scenes
    root = tmp / "c1972"
    pp, req = TN.build(root)
    emit = json.loads((root / "navown-emit.json").read_text(encoding="utf-8"))
    # a second target for the same source (Solitude's shape: one navmesh, several neighbours)
    emit["Conflicts"][1]["Alternative"].append({"FormKey": TN.T2})
    (root / "navown-emit.json").write_text(json.dumps(emit, indent=1), encoding="utf-8")
    pres = {"presentationId": "navmesh-reel-requests", "source": {"bundleDir": str(root)},
            "findings": [{"id": "F-nav", "kind": "finding", "checkId": "geometry.navmesh-ownership", "armId": "baseline",
                          "part": "arm", "counts": {"groupKind": "EDGE-IDX", "A": "Later.esp", "B": "Earlier.esp"},
                          "allRows": [{"formKey": TN.S}], "evidenceIds": ["E-nav"]}],
            "evidence": [{"id": "E-nav", "kind": "emit", "part": "arm", "armId": "baseline", "path": "navown-emit.json",
                          "sha256": TN.sha(root / "navown-emit.json")}],
            "reel": {"status": "planned", "chapters": [
                {"id": "R-02", "kind": "vignette",
                 "clash": {"type": "navmesh", "checkId": "geometry.navmesh-ownership", "formKey": TN.S, "other": [TN.T],
                           "pair": ["Earlier.esp", "Later.esp"], "armId": "baseline", "withoutArmId": "without-later",
                           "findingId": "F-nav"},
                 "frames": [{"state": s, "armId": "baseline"} for s in STATES]}]}}
    plan_ = report_scenes.capture_requests(pres, root)
    navs = [q for q in plan_["requests"] if q.get("requestType") == "navmesh-version-comparison"]
    if not check("C1972", len(navs) == 1, "the reel's navmesh vignette gets no capture request (or more than one)",
                 f"{len(navs)} navmesh requests; limitations {plan_.get('limitations')}"):
        return
    q = navs[0]
    vig = q.get("vignette") or {}
    check("C1972", vig.get("chapterId") == "R-02" and vig.get("withoutArmId") == "without-later" and vig.get("states") == STATES,
          "the navmesh request is not bound to its vignette's chapter, setup without the mod and states", json.dumps(vig))
    check("C1972", q.get("recordKeys") == [TN.S, TN.T, TN.T2] and (q.get("sequence") or {}).get("states") == STATES,
          "the bound request does not carry the source, every target and the navmesh sequence",
          f"{q.get('recordKeys')} {(q.get('sequence') or {}).get('states')}")


# ---------------------------------------------------------------- the pipeline --

def c1973_pipeline_navmesh_branch(tmp: Path):
    """Today evalpipeline captures a navmesh request like a vignette: capture gets --without-order (a navmesh comparison
    renders no frame without the mod) and no control runs. Wrong answers: no navmesh-control step, or one in the setup
    with the mod; capture before the control, or with --without-order, or without the control's result."""
    import evalpipeline as EP
    calls = []

    def child(ctx, ledger_dir, op, argv, **kw):
        calls.append((op, [str(a) for a in argv]))
        a = [str(x) for x in argv]
        out = a[a.index("--out") + 1] if "--out" in a else None
        if op == "acquire" and out:
            Path(out).parent.mkdir(parents=True, exist_ok=True)
            Path(out).write_text(json.dumps({"status": "camera-ready"}), encoding="utf-8")
        if op in ("capture", "navmesh-control") and out:
            Path(out).mkdir(parents=True, exist_ok=True)
            name = "capture-result.json" if op == "capture" else "control-result.json"
            Path(out, name).write_text(json.dumps({"status": "captured" if op == "capture" else "no-broken-crossing"}),
                                       encoding="utf-8")
        return {"exitCode": 0}
    ctx = type("Ctx", (), {})()
    ctx.instance, ctx.profile, ctx.game, ctx.id = Path("X:/Lab"), "EP-x", Path("X:/Game"), "x"
    ctx.machine, ctx.entry, ctx.out = {}, {}, tmp / "c1973"
    q = {"id": "req-nav", "requestType": "navmesh-version-comparison", "armId": WITH,
         "vignette": {"chapterId": "R-02", "withoutArmId": WITHOUT, "states": STATES},
         "camera": {"support": {"supported": True}}}
    cands = {WITH: "X:/c/with.json", WITHOUT: "X:/c/without.json"}
    old = EP.child
    EP.child = child
    try:
        row = EP.capture_request(ctx, q, tmp / "sb.json", tmp / "c1973" / "req-nav", cands)
    finally:
        EP.child = old
    ops = [op for op, _ in calls]
    argv = dict(calls)
    cap, ctl = argv.get("capture") or [], argv.get("navmesh-control") or []
    check("C1973", ops == ["acquire", "navmesh-control", "capture"],
          "the navmesh request is not acquired, controlled in the setup without the mod, then captured", str(ops))
    check("C1973", "--order" in ctl and ctl[ctl.index("--order") + 1] == cands[WITHOUT] if ctl else False,
          "the control is not rendered in the setup without the mod", " ".join(ctl)[-200:])
    check("C1973", bool(cap) and "--without-order" not in cap and "--control" in cap
          and cap[cap.index("--with-order") + 1] == cands[WITH],
          "capture renders a without-the-mod frame for a navmesh comparison, or is not handed the control",
          " ".join(cap)[-300:])
    check("C1973", (row or {}).get("control", {}).get("status") == "no-broken-crossing" if isinstance((row or {}).get("control"), dict) else False,
          "the pipeline's capture row does not carry the control's result", json.dumps(row)[:300])


# ---------------------------------------------------------------- capture with its control --

def c1974_capture_needs_clean_control(tmp: Path):
    """Item 24 ran navmesh-control by hand after capture, and nothing tied the two: a capture whose control still showed
    the broken crossing, or ran at another camera, was as good as one whose control was clean. Wrong answers: a capture
    without a control; a capture over a control with a broken crossing or at another pose; a clean capture that does not
    record its control."""
    TN = load("nir_capture_navmesh2", HERE / "test_capture_navmesh.py")
    root = tmp / "c1974"
    pp, req = TN.build(root)
    rid = req["id"]
    proc, doc, _calls, acq = TN.acquire(root, pp, rid, TN.scene(), "ready")
    if not check("C1974", doc.get("status") == "camera-ready", "the fixture's navmesh acquisition is not camera-ready",
                 TN.why(proc, doc)):
        return

    def control(tag, status="no-broken-crossing", pose=None):
        p = root / f"control-{tag}.json"
        p.write_text(json.dumps({"schemaVersion": 1, "kind": "navmesh-control", "request": rid, "status": status,
                                 "poseId": pose or doc.get("poseId"), "brokenCrossings": 0 if status == "no-broken-crossing" else 1,
                                 "png": str(root / "control.png")}), encoding="utf-8")
        return p

    def capture(tag, ctl):
        out = root / f"cap-{tag}"
        argv = ["capture", "--acquisition", str(acq), "--packet", str(pp), "--request", rid,
                "--with-order", str(root / "order.json"), "--out", str(out), "--size", "320", "--vp", TN.FAKE]
        if ctl is not None:
            argv += ["--control", str(ctl)]
        p_, _c = TN.run(root, TN.scene(), f"cap-{tag}", argv)
        res = json.loads((out / "capture-result.json").read_text(encoding="utf-8")) if (out / "capture-result.json").is_file() else {}
        return p_, res

    def code(res):
        return (res.get("refusal") or {}).get("code")
    _p, none_ = capture("none", None)
    check("C1974", none_.get("status") == "refused" and code(none_) == "navmesh-control-missing",
          "a navmesh comparison is captured without its control", f"{none_.get('status')} {code(none_)} {none_.get('reason')}")
    _p, broken = capture("broken", control("broken", "broken-crossing-present"))
    check("C1974", broken.get("status") == "refused" and code(broken) == "navmesh-control-crossing-present",
          "a navmesh comparison is captured over a control that still has the broken crossing",
          f"{broken.get('status')} {code(broken)} {broken.get('reason')}")
    _p, moved = capture("moved", control("moved", pose="another-pose"))
    check("C1974", moved.get("status") == "refused" and code(moved) == "navmesh-control-pose-mismatch",
          "a navmesh comparison is captured over a control rendered at another camera",
          f"{moved.get('status')} {code(moved)} {moved.get('reason')}")
    p_ok, ok = capture("ok", control("ok"))
    check("C1974", ok.get("status") == "captured" and (ok.get("navmeshControl") or {}).get("status") == "no-broken-crossing"
          and (ok.get("navmeshControl") or {}).get("poseId") == doc.get("poseId"),
          "a navmesh comparison over a clean control is not captured with its control recorded",
          f"exit {p_ok.returncode} {ok.get('status')} {ok.get('reason')} {json.dumps(ok.get('navmeshControl'))}")


def c1975_new_codes_in_table(tmp: Path):
    """C1936's rule for the new codes. Wrong answers: a new navmesh refusal code with no reason of its own; a reason with
    no words or short words."""
    AC = load("nir_alpha_card", HERE / "test_alpha_card_capture.py")
    pol, _pri = shipped()
    v = pol["vignettes"]
    table = v.get("refusalReasons") or {}
    new = ("navmesh-control-missing", "navmesh-control-crossing-present", "navmesh-control-pose-mismatch")
    codes = AC.emittable_refusal_codes()
    check("C1975", all(c in codes for c in new),
          "capture_acquire does not emit the navmesh control refusal codes", str(sorted(c for c in new if c not in codes)))
    check("C1975", all(c in table and table[c] in (v.get("stillReasons") or {}) and table[c] in (v.get("stillReasonsShort") or {})
                       for c in new),
          "a navmesh control refusal code has no reason of its own with words", str({c: table.get(c) for c in new}))
    check("C1975", table.get("navmesh-control-crossing-present") not in (None, "captureFailed", "captureRefused"),
          "a control that keeps the broken crossing is said as a tool problem or a camera that could not see",
          str(table.get("navmesh-control-crossing-present")))


# ---------------------------------------------------------------- the render --

def c1976_two_frame_hold(tmp: Path):
    """evalreel holds a vignette's frames for [without, with, highlight] and cuts the list to the frames it has: a
    two-frame navmesh vignette held 2.5 + 2.5 s whatever its narration. Wrong answer: the frames end before the words."""
    import evalreel as ER
    fn = getattr(ER, "vignette_seconds", None)
    if not check("C1976", fn is not None, "evalreel has no one rule for a vignette's frame holds"):
        return
    hold = {"without": 2.5, "with": 2.5, "highlightMin": 4.0}
    two, three = fn(2, 9.0, hold), fn(3, 9.0, hold)
    check("C1976", len(two) == 2 and sum(two) >= 9.0 + 0.25 - 1e-6 and two[0] == 2.5,
          "a two-frame vignette's frames end before its narration", str(two))
    check("C1976", three == [2.5, 2.5, max(4.0, 9.0 + 0.25 - 5.0)], "the three-frame hold changed", str(three))


def c1977_report_figure(tmp: Path):
    """evalreport's figure_units keeps one frame of a request whose states it does not know (the winning one), and the
    caption falls back to "{setup}: {object}." with the navmesh's form key as the object. Wrong answers: one navmesh frame
    in the report; a caption holding a form key; no navmesh caption words."""
    import evalreport as ER
    figs = [{"captureRequest": "req-nav", "captureState": s, "id": f"M-{s}"} for s in STATES]
    units = ER.figure_units(figs)
    check("C1977", [[m["id"] for m in u] for u in units] == [[f"M-{s}" for s in STATES]],
          "the report does not show both navmesh frames, winning then historical, as one figure",
          str([[m["id"] for m in u] for u in units]))
    # the earlier plugin a caption names is the historical frame's own --nav-version, never the finding's pair (Clockwork
    # run, regeneration 1: the Falkreath figure named the finding's first pair as the winning navmesh's maker)
    fn = getattr(ER, "navmesh_earlier_plugin", None)
    media = [{"provenance": {"argv": ["--nav", "both", "--red", "000100:World.esm:7"]}},
             {"provenance": {"argv": ["--nav", "both", "--nav-version", "000100:World.esm=Earlier Patch.esp"]}}]
    check("C1977", fn is not None and fn(media) == "Earlier Patch.esp" and fn(media[:1]) is None,
          "the report's navmesh caption does not take the earlier version's plugin from the frame's own renderer command",
          str(fn(media) if fn else None))
    _pol, pri = shipped()
    caps = (pri.get("reader") or {}).get("figureCaptions") or {}
    words = [caps.get(s) for s in STATES]
    check("C1977", all(words) and not any(FORM_KEY.search(str(w)) or "{object}" in str(w) or "{modA}" in str(w) for w in words),
          "the navmesh frames have no caption words of their own (the fallback says the navmesh's form key)",
          str(words))


def c1978_narration_and_labels(tmp: Path):
    """Wrong answers: the navmesh vignette spoken with the overlap sentence (or none); a form key or editor ID in the
    spoken string; the place, a mod or the control's result left unsaid; frame labels that do not say which version."""
    import reel_stills
    reel, _pol, pri = plan()
    chs = [ch for ch in nav_chapters(reel) if ch.get("kind") == "vignette"]
    if not check("C1978", bool(chs), "no navmesh vignette to narrate", reel.get("reason") or ""):
        return
    said = reel_stills.spoken_text(" ".join(str(chs[0].get("narration") or "").split()))
    w = pri["reel"]["words"]
    check("C1978", bool(w.get("navmeshClash")) and "occupy the same space" not in said and "cross." not in said,
          "the navmesh vignette is spoken with the overlap sentence, or the skill data has no navmesh sentence", said)
    check("C1978", "Later Mod" in said and "Earlier Mod" in said and "River Town Exterior" in said
          and not FORM_KEY.search(said),
          "the navmesh narration misses the place or a mod, or speaks a form key", said)
    clean = str(w.get("navmeshControlClean") or "").format(mod="Later Mod")
    check("C1978", bool(w.get("navmeshControlClean")) and clean in said,
          "the navmesh narration does not say the control's result", said)
    # a captured result that carries no clean control never earns the clause (the capture refuses such a result; a
    # planner that said it from nothing would speak an unmeasured claim)
    import reel_plan as RP
    pol, _ = shipped()
    pres, names, masters, search, mods, captures = planner_fixture()
    for x in captures:
        x["navmeshControl"] = {"status": "broken-crossing-present"}
    reel2 = RP.plan_reel(pres, names, masters, pol["vignettes"], w, search, mods, captures, captures_final=True)
    said2 = " ".join(str(ch.get("narration") or "") for ch in nav_chapters(reel2) if ch.get("kind") == "vignette")
    check("C1978", bool(said2) and clean not in said2,
          "the control's clean result is said for a result whose control is not clean", said2)
    labels = [f.get("label") for f in chs[0].get("frames") or []]
    check("C1978", len(labels) == 2 and all(labels) and "Earlier Mod" in str(labels[1]) and "Later Mod" in str(labels[0]),
          "the navmesh frames' labels do not say which version each shows", str(labels))


CASES = {"C1970": c1970_navmesh_vignette, "C1971": c1971_navmesh_reason_true,
         "C1972": c1972_reel_binds_navmesh_request, "C1973": c1973_pipeline_navmesh_branch,
         "C1974": c1974_capture_needs_clean_control, "C1975": c1975_new_codes_in_table,
         "C1976": c1976_two_frame_hold, "C1977": c1977_report_figure, "C1978": c1978_narration_and_labels}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    ap.add_argument("--keep", default=None, help="keep the work folder here")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="nir-"))
    root.mkdir(parents=True, exist_ok=True)
    for cid, fn in CASES.items():
        if only and cid not in only:
            continue
        try:
            fn(root)
        except Exception as e:  # noqa: BLE001  (a crash is reported as such, never as the case's own answer)
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:6} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
