"""Known answers for pictures of clashes new with the supplied patches, the one-label count of a world-state check, and
the terrain-station label (C2340-C2349; task 2026-11-14-picture-patch-introduced-clashes and the label part of
2026-11-12-standing-on-non-planar-terrain).

    python tests/test_patch_clash_pictures.py [--only C2340,C2342] [--harness DIR]

No game and no renderer. The cases call the real evalreport, reel_plan, report_scenes and capture_acquire functions with
the SHIPPED skill data, on the fixtures of test_reel_vignettes.py and test_evalreport_prediction.py. A function the fix
adds is looked up with getattr, so HEAD fails the case's own assertion, never by a crash.

    C2340  a patched setup's 5 new world-state clashes under two switches are said "5 clashes in <check>" once, never
           "3 clashes in <check>, 2 clashes in <check>" (KWF round 2)
    C2341  a row's kind (a world state's names its switch by form key) is kept as written when the plan's sentences are
           said in words; a sentence quoting the same key is still said in words
    C2342  a clash new with the patches, of a kind a capture can request, is a reel vignette on the with-patch setup and
           the setup without the patches, said as such, and its capture request renders those two setups
    C2343  guard: a new clash of a kind no camera can show gets no vignette and no request; no supplied patch, no change
    C2344  the report figure of that clash says "With/Without <patches>" and why it is pictured, and stands under the
           "New clashes with the patch, by check" line
    C2345  capture closed: an example never captured has no vignette and is listed as not pictured; a captured one has
           its frames
    C2346  capture_acquire keeps what the camera stands on: the planner's chosen standing and the witness station's
    C2347  a terrain station's caption and reel note say its label; a navmesh station says nothing new
    C2348  one example per check, inside maxVignettes, after the prediction's own vignettes, which are unchanged
    C2349  a refused pick that falls back to the top-down leaves its per-station verdicts in points-refused.json

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

ap0 = argparse.ArgumentParser(add_help=False)
ap0.add_argument("--harness", default=None)
_a0, _ = ap0.parse_known_args()
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = Path(_a0.harness).resolve() if _a0.harness else HERE.parent
import reporting_data  # noqa: E402
SKILL = reporting_data.DATA
sys.path.insert(0, str(HARNESS))
sys.path.insert(1, str(HERE))
os.environ["PYTHONUTF8"] = "1"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

# the harness under test is loaded first, so the fixture modules (which put their own folder on the path) reuse it
import evalreport  # noqa: E402,F401
import reel_plan  # noqa: E402,F401
import report_scenes  # noqa: E402,F401
import capture_acquire  # noqa: E402,F401

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []
STATE_CHECK = "state.conditional-geometry"
PATCH = "Alpha - Target patch.esp"
PFK, POTHER = "000801:Alpha - Target patch.esp", "000602:Beta.esp"
RAW_KIND = "STATE-CONTACT@000702:Skyrim.esm@Triggered"


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return bool(cond)


def shipped():
    pol = reporting_data.scene_policy()
    pri = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
    return pol, pri


# ------------------------------------------------------------------------------------------------ fixtures --

def patch_fixture(with_cell=False, extra_state=0):
    """test_reel_vignettes' synthetic evaluation plus a clash new with the supplied patch: the patch's object sinks into
    Beta's arch in one world state of the with-patch setup, which the report lists under "New clashes with the patch,
    by check". with_cell adds a new cell-settings clash (no camera path); extra_state adds more rows to the same check."""
    import test_reel_vignettes as V
    pres, names, masters, search, mods = V.synthetic()
    rows = [{"formKey": PFK, "other": [POTHER], "label": "CellFive (ext)", "physical": "Holds",
             "summary": f"Contact StateConditional under CampEnable (Skyrim.esm): {PFK} [Static Target_Cairn01] arrives - "
                        "Sunk: the arriving dependent sits 12.0u inside something enabled in the state in CellFive (ext)"}]
    for i in range(extra_state):
        fk = f"00081{i}:Alpha - Target patch.esp"
        rows.append({**rows[0], "formKey": fk, "summary": rows[0]["summary"].replace(PFK, fk)})
        names["refs"][fk] = {"base": "B7", "carriedBy": [PATCH], "scale": 1.0}
    names["refs"][PFK] = {"base": "B7", "carriedBy": [PATCH], "scale": 1.0}
    finding = {"id": "F-P1", "kind": "finding", "checkId": STATE_CHECK, "armId": "with-patch", "part": None,
               "counts": {"groupKind": RAW_KIND, "A": PATCH, "B": "Beta.esp"}, "allRows": rows, "rows": rows[:3],
               "evidenceIds": ["E-P"], "readerRelevant": True}
    pres["findings"].append(finding)
    by_check = [{"title": "Objects that change with quests or switches", "count": len(rows), "findingIds": ["F-P1"]}]
    if with_cell:
        pres["findings"].append({"id": "F-P2", "kind": "finding", "checkId": "records.cell-attributes", "armId": "with-patch",
                                 "counts": {"groupKind": "LOST-EDIT", "A": PATCH, "B": "Beta.esp"},
                                 "allRows": [{"formKey": "000501:Skyrim.esm", "field": "Music",
                                              "summary": "Cell CellFour: Music - Beta.esp's value lost to the patch's"}]})
        by_check.append({"title": "Cell settings", "count": 1, "findingIds": ["F-P2"]})
        # a kind a picture could show but no capture rule requests (scene-policy captureChecks lacks it)
        fk3 = "000802:Alpha - Target patch.esp"
        names["refs"][fk3] = {"base": "B7", "carriedBy": [PATCH], "scale": 1.0}
        pres["findings"].append({"id": "F-P3", "kind": "finding", "checkId": "geometry.placement-contact",
                                 "armId": "with-patch", "counts": {"groupKind": "CONTACT", "A": PATCH, "B": "Beta.esp"},
                                 "allRows": [{"formKey": fk3, "other": [POTHER], "physical": "Holds",
                                              "summary": f"Contact: {fk3} sits 30u inside 000602:Beta.esp in CellFive (ext)"}]})
        by_check.append({"title": "Objects resting on something", "count": 1, "findingIds": ["F-P3"]})
    pres["reader"]["validation"] = {"status": "compared", "patches": [PATCH], "patchLabels": [PATCH],
                                    "withPatchArm": "with-patch", "withoutPatchArm": "with-target", "newByCheck": by_check}
    pres["scope"]["arms"].append({"armId": "with-patch", "displayLabel": "Tested alternative setup",
                                  "locationPluginOrder": pres["scope"]["arms"][0]["locationPluginOrder"]})
    return pres, names, masters, search, mods


def plan(pres, names, masters, search, mods, captures=(), final=False, pol_over=None):
    import reel_plan as RP
    pol, pri = shipped()
    vp = dict(pol["vignettes"], **(pol_over or {}))
    return RP.plan_reel(pres, names, masters, vp, pri["reel"]["words"], search, mods, list(captures),
                        captures_final=final)


def patch_chapters(reel):
    return [ch for ch in reel.get("chapters") or [] if ch.get("kind") == "vignette"
            and (ch.get("clash") or {}).get("armId") == "with-patch"]


def patch_media(fk=PFK, req="req-p"):
    return [{"id": f"M-p-{s}", "assessed": True, "mediaKind": "offline-render",
             "subject": {"checkId": STATE_CHECK, "armId": "with-target" if s == "without" else "with-patch", "formKey": fk},
             "provenance": {"captureRequest": req, "vignetteState": s, "poseId": "p1",
                            "argv": ["viewprobe.exe"] + (["--ref", f"{fk}=cyan"] if s == "highlight" else [])}}
            for s in ("without", "with", "highlight")]


# ------------------------------------------------------------------------------------------------ C2340 --

def c2340_one_label_per_check(tmp: Path):
    """KWF: "new 3 clashes in "Objects that change with quests or switches", 2 clashes in "Objects that change with
    quests or switches"" and "New clashes in the setup with ...: 3 clashes, 2 clashes." Wrong answer: one world-state
    check said twice, split by the switch its rows are found under."""
    import test_evalreport_prediction as T
    STATE = STATE_CHECK
    arms = T.PATCH_ADDED_ARMS
    rows = []
    for i, sw in ((1, "000S01"), (2, "000S01"), (3, "000S01"), (4, "000S02"), (5, "000S02")):
        rows.append(T.row(f"STATE-CONTACT@{sw}:{T.MOD_A}@Triggered", T.PATCH, T.MOD_A, f"000P0{i}:{T.PATCH}",
                          label="Camp (ext)", other=f"000Q0{i}:{T.MOD_A}"))
    plan_, md, _html = T.compile_and_render(arms, {(STATE, "baseline"): [], (STATE, "with-patch"): rows},
                                            [T.MOD_A, T.MOD_B], patches=[T.PATCH])
    val = T.reader(plan_).get("validation") or {}
    summary = str(val.get("summary") or "")
    title = "Objects that change with quests or switches"
    check("C2340", summary.count(title) == 1 and "5 clashes" in summary and "3 clashes" not in summary,
          "the summary names the world-state check once per switch instead of once with all its new clashes", summary[:400])
    line = next((ln for ln in md.splitlines() if ln.startswith("- New clashes in the setup with")), "")
    check("C2340", ": 5 clashes" in line and "3 clashes" not in line and "2 clashes" not in line,
          "the setup line counts one check's new clashes in two parts", line[:300])


# ------------------------------------------------------------------------------------------------ C2341 --

def c2341_kind_kept_as_written(tmp: Path):
    """KWF's presentation said every state kind's switch in words ("STATE-CONTACT@a static object (XMarker)@BoundUnread"),
    so the reel could not read the switch and no world-state picture was requested. Wrong answers: a kind or groupKind
    rewritten; a sentence quoting the key left unsaid."""
    import evalreport as ER
    fk = "000702:Skyrim.esm"
    obj = {"rows": [{"kind": RAW_KIND, "summary": "x"}], "counts": {"groupKind": RAW_KIND},
           "readerPlain": f"A clash under the switch {fk} in the camp."}
    out = ER.say_form_keys(obj, lambda f: ("a static object (Camp Marker)", None, None), ["Skyrim.esm"], {})
    check("C2341", out["rows"][0]["kind"] == RAW_KIND and out["counts"]["groupKind"] == RAW_KIND,
          "a row's kind is rewritten in words, so its world state's switch is lost", json.dumps(out)[:300])
    check("C2341", fk not in out["readerPlain"] and "Camp Marker" in out["readerPlain"],
          "a sentence quoting the switch's key is no longer said in words", out["readerPlain"])


# ------------------------------------------------------------------------------------------------ C2342 --

def c2342_patch_vignette_and_request(tmp: Path):
    """KWF: 5 new world-state clashes with both patches loaded, a kind with a camera path, and no picture requested.
    Wrong answers: no vignette; a vignette on the prediction's setups (without/with the evaluated mod), which would not
    show the patches; no words saying it is the with-patch state; a request rendering other setups."""
    import report_scenes as RS
    pres, names, masters, search, mods = patch_fixture()
    reel = plan(pres, names, masters, search, mods)
    chs = patch_chapters(reel)
    if not check("C2342", len(chs) == 1, "no reel vignette for the clash new with the patch (or not on its setup)",
                 json.dumps([(c.get("kind"), (c.get("clash") or {}).get("armId")) for c in reel.get("chapters") or []])):
        return
    ch = chs[0]
    c = ch["clash"]
    check("C2342", c.get("withoutArmId") == "with-target" and c.get("formKey") == PFK and c.get("state") == "000702:Skyrim.esm"
          and c.get("kind") == RAW_KIND,
          "the vignette does not compare the with-patch setup with the setup without the patches, or lost its switch",
          json.dumps(c)[:400])
    said = str(ch.get("narration") or "")
    check("C2342", said.endswith(" This clash appears only when Alpha - Target patch from Alpha Patches is loaded, so it "
                                 "is shown with it loaded. Check it before you use it.") and "Cairn" in said,
          "the narration does not say the clash appears only with the patch loaded and is shown with it", said[:400])
    # two patches: the sentence agrees with them
    p2_ = copy.deepcopy(pres)
    p2_["reader"]["validation"]["patches"] = [PATCH, "Beta - Target patch.esp"]
    m2_ = dict(mods, **{"Beta - Target patch.esp": {"name": "Beta Patches", "source": "nexus-catalogue"}})
    two_ = " ".join(str(c_.get("narration")) for c_ in patch_chapters(plan(p2_, names, masters, search, m2_)))
    check("C2342", " are loaded, so it is shown with them loaded. Check it before you use them." in two_
          and "Beta - Target patch" in two_,
          "the sentence does not agree with two patches", two_[-300:])
    labs = {f.get("state"): f.get("label") for f in ch.get("frames") or []}
    check("C2342", str(labs.get("without") or "").startswith("Without ") and str(labs.get("with") or "").startswith("With ")
          and "Target Mod" not in str(labs.get("with")) and "with " in str(labs.get("highlight") or ""),
          "the frame strips name the evaluated mod, not the patches", json.dumps(labs))
    kinds_ = [c_.get("kind") for c_ in reel["chapters"]]
    check("C2342", kinds_.index("verdict") < reel["chapters"].index(ch) if "verdict" in kinds_ else True,
          "the patch vignette is shown before the advice to check the new clashes", json.dumps(kinds_))
    # the request: a state pair rendering the with-patch setup and the setup without the patches, from their own emit
    emit = {"Probe": "stateprobe", "State": "baseline",
            "Configuration": {"Fingerprint": "p" * 64, "Population": "load-time", "State": "baseline", "Plugins": []},
            "Conflicts": [{"Kind": RAW_KIND, "A": PATCH, "B": "Beta.esp",
                           "Necessary": [{"FormKey": PFK, "Physical": "Holds", "Label": "CellFive (ext)"}],
                           "Alternative": [{"FormKey": POTHER}]}]}
    (tmp / "patch-state.json").write_text(json.dumps(emit))
    p2 = copy.deepcopy(pres)
    p2["evidence"] = [{"id": "E-P", "kind": "emit", "part": "arm", "armId": "with-patch", "path": "patch-state.json",
                       "sha256": hashlib.sha256((tmp / "patch-state.json").read_bytes()).hexdigest()}]
    p2["reel"] = reel
    reqs = RS.capture_requests(p2, tmp)["requests"]
    st = next((r for r in reqs if r.get("armId") == "with-patch"), {})
    check("C2342", st.get("requestType") == "state-pair" and st.get("state") == "000702:Skyrim.esm"
          and (st.get("sequence") or {}).get("setups", {}).get("with") == "with-patch"
          and (st.get("sequence") or {}).get("setups", {}).get("without") == "with-target"
          and (st.get("vignette") or {}).get("withoutArmId") == "with-target"
          and st.get("configurationFingerprint") == "p" * 64,
          "the capture request does not render the with-patch setup beside the setup without the patches",
          json.dumps({k: st.get(k) for k in ("requestType", "armId", "state", "sequence", "configurationFingerprint")})[:500])


# ------------------------------------------------------------------------------------------------ C2343 --

def c2343_guards(tmp: Path):
    """Wrong answers: a vignette for a new cell-settings clash (no camera draws it); any change to a reel with no clash
    new with the patches."""
    import test_reel_vignettes as V
    pres, names, masters, search, mods = patch_fixture(with_cell=True)
    reel = plan(pres, names, masters, search, mods)
    ids = [(ch.get("clash") or {}).get("findingId") for ch in patch_chapters(reel)]
    check("C2343", "F-P2" not in ids and "F-P3" not in ids and ids == ["F-P1"],
          "a clash no capture can request is given a patch vignette", json.dumps(ids))
    base = V.synthetic()
    r0 = plan(*base)
    no_new = copy.deepcopy(base[0])
    no_new["reader"]["validation"] = {"status": "compared", "patches": [PATCH], "withPatchArm": "with-patch",
                                      "withoutPatchArm": "with-target", "newByCheck": []}
    r1 = plan(no_new, *base[1:])
    strip = lambda r: json.dumps({k: v for k, v in r.items() if k != "patchNew"}, sort_keys=True)  # noqa: E731
    check("C2343", strip(r0) == strip(r1) and not patch_chapters(r0),
          "a reel with no clash new with the patches changed", "")


# ------------------------------------------------------------------------------------------------ C2344 --

def c2344_report_figure(tmp: Path):
    """Wrong answers: the patch clash's frames captioned "With <evaluated mod>"; no sentence saying why it is pictured;
    the figure placed anywhere but under the "New clashes with the patch, by check" line."""
    import test_evalreport_prediction as T
    import evalreport as ER
    from PIL import Image
    STATE = STATE_CHECK
    pk = f"000P01:{T.PATCH}"
    rows = [T.row(f"STATE-CONTACT@000S01:{T.MOD_A}@Triggered", T.PATCH, T.MOD_A, pk, label="Camp (ext)",
                  other=f"000Q01:{T.MOD_A}")]
    pr = json.loads(T.PRIORITIES.read_text(encoding="utf-8"))
    conc = pr.get("concise") or {}
    settings = {"mode": "concise", "layout": conc.get("layout", "normal"), "maxPages": conc.get("maxPages", 8),
                "page": {"size": "A4", "marginMm": 16, "baseFontPt": 10.5}, "declaredPatches": [],
                "rowsPerGroup": conc.get("rowsPerGroup", 3), "figuresPerFinding": conc.get("figuresPerFinding", 1),
                "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height")}}
    bdir = T.make_bundle(tmp / "c2344", T.PATCH_ADDED_ARMS, {(STATE, "baseline"): [], (STATE, "with-patch"): rows},
                         [T.MOD_A, T.MOD_B], patches=[T.PATCH])
    man_p = bdir / "manifest.json"
    man = json.loads(man_p.read_text(encoding="utf-8"))
    (bdir / "media").mkdir(exist_ok=True)
    for i, (st, arm) in enumerate((("without", "baseline"), ("with", "with-patch"), ("highlight", "with-patch"))):
        p = bdir / "media" / f"{st}.png"
        Image.new("RGB", (8, 8), (i * 40, 90, 90)).save(p)
        man["evidence"].append({"id": f"m{st:0<15}", "kind": "media", "strength": "supplied-media",
                                "path": f"media/{st}.png", "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                                "mediaKind": "offline-render",
                                "subject": {"formKey": pk, "armId": arm, "checkId": STATE},
                                "provenance": {"vignetteState": st, "captureRequest": "q-p", "poseId": "p1",
                                               "argv": ["viewprobe.exe"] + (["--ref", f"{pk}=cyan"] if st == "highlight" else [])}})
    man_p.write_text(json.dumps(man), encoding="utf-8")
    plan_ = ER.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
    ER.copy_media(plan_, bdir, tmp / "c2344-out")
    view = ER.effective_view(plan_, settings["rowsPerGroup"], settings["figuresPerFinding"], pr, "normal")
    out = ER.render_all(plan_, view, tmp / "c2344-out", "")
    caps = [m.get("caption") or "" for m in plan_.get("media") or []]
    check("C2344", bool(caps) and all(c.startswith(("With ModA - ModB patch.esp", "Without ModA - ModB patch.esp"))
                                      for c in caps),
          "the patch clash's frames are not captioned with the patch's setups", json.dumps(caps)[:500])
    check("C2344", sum("This clash appears only when ModA - ModB patch.esp is loaded" in c for c in caps) == 1,
          "the figure does not say once why it is pictured", json.dumps(caps)[:500])
    # (plugin-name-case fold-in 4, patch-clash-pictures Gate C) the why goes with the frame that shows the clash: the
    # first frame with the patches loaded, never the Without frame
    why_ = [c for c in caps if "This clash appears only when ModA - ModB patch.esp is loaded" in c]
    check("C2344", len(why_) == 1 and why_[0].startswith("With ModA - ModB patch.esp"),
          "the why sentence sits on a frame without the patch", json.dumps(why_)[:300])
    md = out["report.md"]
    lines = md.splitlines()
    at = next((i for i, ln in enumerate(lines) if ln.startswith("- New clashes with the patch, by check")), None)
    img = next((i for i, ln in enumerate(lines) if ln.startswith("![") and "media/" in ln or ln.startswith("![") and ".png" in ln), None)
    check("C2344", at is not None and img is not None and img > at
          and not any(ln.startswith("#") for ln in lines[at:img]),
          "the figure is not shown under the new-clashes-by-check line", f"line {at}, first image {img}")


# ------------------------------------------------------------------------------------------------ C2345 --

def c2345_capture_closed(tmp: Path):
    """Wrong answers: a frame-less vignette for an example never captured; no record of it; a captured example without
    its frames."""
    pres, names, masters, search, mods = patch_fixture()
    reel = plan(pres, names, masters, search, mods, final=True)
    un = ((reel.get("patchNew") or {}).get("unpictured")) or []
    check("C2345", not patch_chapters(reel) and [u.get("findingId") for u in un] == ["F-P1"],
          "with capture closed, an uncaptured patch clash is shown without frames, or not recorded as not pictured",
          json.dumps(reel.get("patchNew")))
    pres["media"] = patch_media()
    caps = [{"request": "req-p", "status": "captured", "formKey": PFK, "recordKeys": [PFK, POTHER], "requestType": "state-pair"}]
    reel2 = plan(pres, names, masters, search, mods, captures=caps, final=True)
    chs = patch_chapters(reel2)
    mids = [f.get("mediaId") for ch in chs for f in ch.get("frames") or []]
    check("C2345", len(chs) == 1 and mids == ["M-p-without", "M-p-with", "M-p-highlight"],
          "a captured patch clash does not show its own three frames", json.dumps(mids))


# ------------------------------------------------------------------------------------------------ C2346 --

TERRAIN = {"kind": "terrain", "ground": -222.4, "slope": 40.0, "spread": 0.0, "limit": 43.71,
           "label": "terrain, slope 40.0°, measured limit 43.7°"}


def c2346_acquire_keeps_standing(tmp: Path):
    """Wrong answer: the camera the capture renders from drops what it stands on, so the terrain label never reaches
    the report or reel."""
    import capture_acquire as CA
    fn = getattr(CA, "witness_camera")
    doc = {"cell": "CellFive", "floorZ": -222.4, "camera": {"aspect": "16:9"},
           "witness": {"station": {"id": "s13", "eye": [0.0, 250.0, -102.4], "look": [0.0, 0.0, -150.0], "fov": 65.0,
                                   "standing": TERRAIN}}}
    import importlib.util
    spec = importlib.util.spec_from_file_location("hc_shotplan_t", SW.HARNESS / "viewprobe" / "tools" / "shotplan.py")
    SP = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(SP)
    cam = fn(doc, SP)
    check("C2346", (cam.get("standing") or {}).get("label") == TERRAIN["label"],
          "the station-view camera drops its station's standing", json.dumps(cam)[:300])
    request = {"id": "req-t", "configurationFingerprint": "f" * 64, "sequence": {"states": ["without", "with", "highlight"]}}
    plan_ = {"status": "verified", "poseId": "pose-1", "cell": "CellFive",
             "chosen": {"ok": True, "eye": [-338.0, -91.0, 172.5], "yaw": 10.0, "pitch": -5.0, "standing": TERRAIN},
             "verification": {"pose": {"fov": 65, "aspect": 1.7778}}, "scene": {"renderer": "f" * 64}}
    points = {"doc": {}, "byKey": {}, "path": "points.json", "sha256": "0" * 64, "camera": {}}
    try:
        res = CA.build_result(request, [], points, {}, plan_, {"exitCode": 0, "argv": [], "own": True}, {}, SP)
        got = (res.get("camera") or {}).get("standing")
    except Exception as e:  # noqa: BLE001 - a binding refusal here is a fixture gap, said in the detail
        got = f"{type(e).__name__}: {e}"
    check("C2346", isinstance(got, dict) and got.get("label") == TERRAIN["label"],
          "the planner's chosen camera loses what it stands on", str(got)[:300])


# ------------------------------------------------------------------------------------------------ C2347 --

def c2347_terrain_label_said(tmp: Path):
    """Wrong answers: a terrain station's picture that does not say where its camera stands; a navmesh station that
    says something new."""
    import evalreport as ER
    import reel_plan as RP
    _pol, pri = shipped()
    cw = pri["reader"]["figureCaptions"]
    ter = {"viewKind": "eye-level", "camera": {"standing": TERRAIN}}
    nav = {"viewKind": "eye-level", "camera": {"standing": {"kind": "navmesh"}}}
    cap_t, cap_n = ER.view_kind_caption(ter, cw, first=True), ER.view_kind_caption(nav, cw, first=True)
    check("C2347", TERRAIN["label"] in cap_t and "no navmesh" in cap_t and cap_n == "",
          "the terrain station's caption does not say its label, or a navmesh station's says something", repr((cap_t, cap_n)))
    check("C2347", ER.view_kind_caption(ter, cw, first=False) == "",
          "the terrain label is said on every frame of the figure, not once", "")
    st_view = {"viewKind": "eye-level", "camera": {"standing": TERRAIN, "cameraSource": "witness-station"}}
    sv = ER.view_kind_caption(st_view, cw, first=True)
    check("C2347", "Station view" in sv and TERRAIN["label"] in sv, "a station view on terrain loses one of its two notes", sv)
    vt, vn = RP.view_kind_line(ter), RP.view_kind_line(nav)
    check("C2347", TERRAIN["label"] in vt and vn == "",
          "the reel note does not say the terrain label, or says something for a navmesh station", repr((vt, vn)))


# ------------------------------------------------------------------------------------------------ C2348 --

def c2348_cap_and_order(tmp: Path):
    """Wrong answers: more than one example per check; patch vignettes beyond maxVignettes; any change to the
    prediction's own vignettes."""
    import test_reel_vignettes as V
    pres, names, masters, search, mods = patch_fixture(extra_state=2)
    reel = plan(pres, names, masters, search, mods)
    check("C2348", len(patch_chapters(reel)) == 1, "a check's new clashes get more than one example",
          str(len(patch_chapters(reel))))
    base = plan(*V.synthetic())
    pred_of = lambda r: [json.dumps({k: v for k, v in ch.items() if k != "id"}, sort_keys=True)  # noqa: E731
                         for ch in r.get("chapters") or [] if ch.get("kind") in ("vignette", "split")
                         and (ch.get("clash") or {}).get("armId") != "with-patch"]
    check("C2348", pred_of(reel) == pred_of(base), "the prediction's own vignettes changed", "")
    n_pred = len(pred_of(base))
    capped = plan(pres, names, masters, search, mods, pol_over={"maxVignettes": n_pred})
    check("C2348", not patch_chapters(capped), "patch vignettes are added past maxVignettes",
          str(len(patch_chapters(capped))))


# ------------------------------------------------------------------------------------------------ C2349 --

def c2349_refused_pick_keeps_stations(tmp: Path):
    """terrain-stations RESULT section 5.2: a refused pick left no points document, so its per-station verdicts were
    recomputed from consoles. Wrong answer: no points-refused.json with the stations, or written only when no top-down
    follows."""
    import capture_acquire as CA
    fn = getattr(CA, "keep_refused_points", None)
    if not check("C2349", fn is not None, "capture_acquire has no step that keeps a refused pick's stations"):
        return

    class Unp(Exception):
        code, detail = "points-no-station", "none."
        extra = {"stations": [{"id": "s13", "valid": False, "why": "the LAND under it slopes 50.0 deg"}], "chain": [],
                 "topDown": {"cell": "CellFive"}}
    rd = tmp / "c2349"
    rd.mkdir(exist_ok=True)
    fn(rd, {"id": "req-t"}, Unp())
    got = json.loads((rd / "points-refused.json").read_text(encoding="utf-8")) if (rd / "points-refused.json").is_file() else {}
    check("C2349", (got.get("stations") or [{}])[0].get("why", "").startswith("the LAND under it slopes")
          and got.get("requestId") == "req-t",
          "points-refused.json does not hold the refused stations' verdicts", json.dumps(got)[:300])
    src = (HARNESS / "capture_acquire.py").read_text(encoding="utf-8")
    seg = src[src.find("except CP.Unpickable as exc:"):][:600]
    check("C2349", "keep_refused_points(" in seg and seg.find("keep_refused_points(") < seg.find("acquire_top_down"),
          "the stations are not kept before the top-down fallback runs", seg[:300])


CASES = {"C2340": c2340_one_label_per_check, "C2341": c2341_kind_kept_as_written,
         "C2342": c2342_patch_vignette_and_request, "C2343": c2343_guards, "C2344": c2344_report_figure,
         "C2345": c2345_capture_closed, "C2346": c2346_acquire_keeps_standing, "C2347": c2347_terrain_label_said,
         "C2348": c2348_cap_and_order, "C2349": c2349_refused_pick_keeps_stations}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--harness", default=None)
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="pcp-"))
    for cid, fn in CASES.items():
        if only and cid not in only:
            continue
        try:
            fn(root)
        except Exception as e:  # noqa: BLE001
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:6} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
