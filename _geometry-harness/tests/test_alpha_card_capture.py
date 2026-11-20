"""Known answers for the interim alpha-card capture rule and the stale image key (C1890-C1899, C1930-C1933, C1935-C1939, C1960-C1961).

    python tests/test_alpha_card_capture.py [--only C1890,C1897]

No game and no renderer. The acquisition cases (C1890-C1893) call capture_acquire's real routing after a refused composed
pose (`after_pose_refusal`) with its one viewprobe call (`_vp_run`) answered by a stand-in that prints viewprobe's own
`coverage-at` and `capture input configuration:` lines, and the top-down replaced by a recorder. The other cases call the
real evalreport, reel_plan and evalpipeline functions on hand-built inputs. Coordinates, pixels and fingerprints are made
up; nothing here is evidence about a real cell.

    C1890  a plant drawn only by see-through shapes (alpha cards) misses its mark by a pixel at the composed pose: the
           witness station's own camera is tried before the top-down and kept, since the plant's point lands in its
           mark there and the render names the request's configuration
    C1891  the same plant when the planner itself refused the composed pose: the witness camera is tried as well
    C1892  a participant with opaque shapes, refused at the composed pose: no witness camera, straight to the top-down
    C1893  the witness camera misses the mark too: the step is refused and the top-down is tried, its outcome unchanged
    C1894  a picture from the witness camera is captioned as a station view in the report; the reel adds no words for it
    C1895  a final refusal from before the rule (a composed pose refused, no witness step) is captured again, not reused
    C1896  a refused clash a station at eye height saw is never "No camera could see the clash"; nor is one whose object
           the renderer cannot mark
    C1897  an image key kept from an earlier capture (an empty one) does not stand for a capture that has frames
    C1898  a finding whose counts spell a plugin in another case than its probe's evidence (Ice Blade's re-scored
           evaluation against its overlapprobe output) still gets its capture request
    C1899  a marked view with one mark names that mark (never "None"), and the station view line is said once per figure
    C1930  the report stage's media bundle names the evidence bundle's frozen check registry, so a registry changed since
           the run does not refuse the report
    C1931  a still page's narration speaks no form key or editor ID; the page itself still shows them
    C1932  frames address the cell by its form key (a mod may rename it); a cell only the mod defines is refused as a
           place only the mod has; after a ready eye-level camera a refusal is never "No camera could see the clash"
    C1933  the report stage's media bundle hands every pinned plan input whose original no longer hashes to the plan as
           the evidence bundle's copy (the scope on a deleted folder), and refuses a pinned input it does not know
    C1935  a vignette's capture request is its own clash pair, never every pair sharing its first form key
    C1936  every refusal code the capture side can emit has its own reason in one table; captureRefused only for an
           unknown code; a reference switched off for good is said so
    C1937  a sliced top-down's (or cutaway's) note stays on its frame and is never narrated
    C1938  "overlap candidate" never reaches the report or the reel; the overlap what-to-do line is in plain words
    C1939  the picker's not-drawn reason travels as fields into the capture result and the reel's reason line
    C1960  a not-drawn refusal without viewprobe's cause fields gets the neutral line, never "no model to draw"
    C1961  the final string handed to speech and subtitles holds no form-key or editor-ID shape (Cut Content's real text)

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
import re
import os
import sys
import tempfile
import traceback
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
SKILL = HARNESS.parent / "external" / "houseCARL" / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


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


# ---------------------------------------------------------------- the acquisition (stand-in renderer) --

HOUSE, PLANT = "000803:House.esp", "001706:Update.esm"
FP = "ab" * 32
EYE, LOOK = [1000.0, 2000.0, 120.0], [1000.0, 2250.0, 60.0]


def points_fixture(plant_all_see_through: bool = True) -> dict:
    """read_points' shape for a pair whose plant the picker marked by coverage at witness station r1s3."""
    eye_step = {"kind": "eye-level", "status": "ok", "code": None, "detail": "station r1s3 (0 deg, 250 u)",
                "allNonOpaque": [PLANT] if plant_all_see_through else []}
    doc = {"schemaVersion": 1, "requestId": "req-alpha", "cell": "MorthalExterior06", "floorZ": 0.0, "viewKind": "eye-level",
           "viewFlags": [], "fallbackChain": [eye_step],
           "topDown": {"cell": "MorthalExterior06", "floorZ": 0.0, "subjects": [], "seeThrough": [PLANT],
                       "allNonOpaque": [PLANT] if plant_all_see_through else []},
           "points": [{"formKey": HOUSE, "point": [1000.0, 2250.0, 80.0], "markKind": "surface"},
                      {"formKey": PLANT, "point": [1010.0, 2240.0, 50.0], "markKind": "coverage",
                       "coverage": {"alphaMin": 0.25, "alpha": 0.6, "pixels": 40}}],
           "camera": {"fov": 65.0, "aspect": "16:9"},
           "witness": {"station": {"id": "r1s3", "bearing": 0.0, "standoff": 250.0, "eye": EYE, "frame": "p2-witness/x.png",
                                   "look": LOOK, "fov": 65.0, "size": 320}}}
    by_key = {p["formKey"]: {**p, "label": "face"} for p in doc["points"]}
    return {"doc": doc, "byKey": by_key, "path": "points.json", "sha256": "0" * 64, "camera": doc["camera"]}


def request_fixture() -> dict:
    return {"id": "req-alpha", "requestType": "overlap-pair", "checkId": "geometry.object-overlap", "armId": "with",
            "recordKeys": [HOUSE, PLANT], "configurationFingerprint": FP,
            "sequence": {"states": ["without", "with", "highlight"]}}


PARTS = [{"formKey": HOUSE, "color": "cyan"}, {"formKey": PLANT, "color": "magenta"}]


def acquisition_env(coverage_line: str):
    """capture_acquire with its renderer call answered by `coverage_line` (plus the configuration line) and its
    top-down recorded instead of planned. Returns (module, calls, top-down handoffs)."""
    CA = load("ac_capture_acquire_" + str(abs(hash(coverage_line)) % 10 ** 8), HARNESS / "capture_acquire.py")
    calls, handoffs = [], []

    def fake_vp(a, shotplan_mod, cell, flags, out):
        calls.append({"cell": cell, "flags": [str(f) for f in flags], "out": str(out)})
        return 0, f"{coverage_line}\n   capture input configuration: {FP}\n"

    def fake_top_down(a, request, parts, source, receipts_dir, shotplan_mod, exc):
        handoffs.append(exc.extra)
        chain = list(exc.extra.get("chain") or []) + [
            {"kind": "top-down", "status": "refused", "code": "top-down-participant-unseen", "detail": "1 px"}]
        raise CA.Refused("top-down-participant-unseen", "1 marked pixel from above", final=True, chain=chain,
                         participant=PLANT)
    CA._vp_run = fake_vp
    CA.acquire_top_down = fake_top_down
    return CA, calls, handoffs


def route(CA, pexc, points, tmp: Path):
    """after_pose_refusal's answer: ("witness" | "top-down", result) or ("refused", the Refused it raised)."""
    fn = getattr(CA, "after_pose_refusal", None)
    a = types.SimpleNamespace(passthrough=[], vp=None)
    shotplan_mod = CA._load_shotplan()
    if fn is None:
        # the base has no routing function: its cmd_acquire goes from the refused pose straight to the top-down
        handed = CA.after_pick_refusal(pexc, points["doc"])
        if handed is None:
            return "refused", pexc
        h = type("PoseRefusal", (), {})()
        h.code, h.extra = pexc.code, handed
        try:
            return "top-down", CA.acquire_top_down(a, request_fixture(), PARTS, {}, tmp, shotplan_mod, h)
        except CA.Refused as e:
            return "refused", e
    try:
        return fn(a, request_fixture(), PARTS, points, {}, tmp, shotplan_mod, pexc)
    except CA.Refused as e:
        return "refused", e


INSIDE = f"coverage-at {PLANT} 1010,2240,50: pixel 284,160 in the mark, 4 px inside"
OUTSIDE = f"coverage-at {PLANT} 1010,2240,50: pixel 284,160 outside the mark"


def c1890_witness_after_pose_miss(tmp: Path):
    """Hroggar d82b's shape: the plant's point lands 1 px inside its mark at the composed pose (the rule needs 2). Wrong
    answers: straight to the top-down (which shows a card plant as 1-5 px and refuses); a witness camera other than the
    station's own eye and look; a check frame that does not keep the station's 320 px height or asks no configuration."""
    CA, calls, handoffs = acquisition_env(INSIDE)
    pexc = CA.Refused("see-through-not-covered-at-pose", f"{PLANT}'s measured point does not land in its own coverage "
                      "mark at the final camera (pixel 160,134 in the mark, 1 px inside); no camera is published", final=False)
    kind, res = route(CA, pexc, points_fixture(), tmp / "c1890")
    cam = (res or {}).get("camera") if isinstance(res, dict) else {}
    chain = (res or {}).get("fallbackChain") if isinstance(res, dict) else []
    flags = calls[-1]["flags"] if calls else []
    check("C1890", kind == "witness" and not handoffs, "the refused composed pose went to the top-down, not to the "
          "witness station's own camera", f"route {kind}, top-down calls {len(handoffs)}")
    check("C1890", bool(cam) and cam.get("eye") == EYE and cam.get("cameraSource") == "witness-station"
          and abs(float(cam.get("yaw", -1)) - 0.0) < 0.05 and abs(float(cam.get("pitch", -99)) - 13.5) < 0.05,
          "the witness camera is not the station's own eye looking at its own look point (yaw 0, pitch 13.5)", str(cam))
    check("C1890", "--size" in flags and flags[flags.index("--size") + 1] == "569" and "--config-identity" in flags
          and any(f.startswith(f"{PLANT}@") for f in flags),
          "the witness check frame does not keep the station's 320 px height at 16:9, ask the configuration, or check "
          "the plant's own point", " ".join(flags))
    check("C1890", [c.get("kind") for c in chain or []] == ["eye-level", "witness"]
          and chain[0].get("status") == "refused" and chain[1].get("status") == "ok"
          and (res.get("configuration") or {}).get("observed") == FP,
          "the chain does not record the composed pose refused and the witness step ok, bound to the configuration",
          json.dumps(chain)[:400] if chain else str(res))


def c1891_witness_after_planner_refusal(tmp: Path):
    """Hroggar 0264's shape: the planner probed the composed pose and refused it. Wrong answer: the witness camera is
    not tried for a plant the planner could not frame."""
    CA, calls, handoffs = acquisition_env(INSIDE)
    pexc = CA.Refused("planner-not-verified", "the planner probed this pose and refused it (exit 1)",
                      plannerStatus="refused", plannerExitCode=1)
    kind, res = route(CA, pexc, points_fixture(), tmp / "c1891")
    check("C1891", kind == "witness" and not handoffs and ((res or {}).get("camera") or {}).get("cameraSource") == "witness-station",
          "a pose the planner refused for an alpha-card plant does not try the witness station's camera",
          f"route {kind}, top-down calls {len(handoffs)}")


def c1892_opaque_keeps_top_down(tmp: Path):
    """Guard: the refused participant has opaque shapes (not in allNonOpaque). Wrong answers: a witness camera for it
    (the rule is for alpha-card shapes only); a witness render at all; the top-down not tried."""
    CA, calls, handoffs = acquisition_env(INSIDE)
    pexc = CA.Refused("see-through-not-covered-at-pose", "outside its mark", final=False)
    kind, res = route(CA, pexc, points_fixture(plant_all_see_through=False), tmp / "c1892")
    check("C1892", kind != "witness" and len(handoffs) == 1 and not calls,
          "a participant with opaque shapes got the witness camera instead of the top-down",
          f"route {kind}, renders {len(calls)}, top-down calls {len(handoffs)}")


def c1893_witness_miss_goes_on(tmp: Path):
    """The plant's point lands outside its mark at the witness camera too. Wrong answers: the witness frame kept anyway;
    the top-down not tried after it; the witness step missing from the chain the top-down receives; the final code
    changed from the top-down's own."""
    CA, calls, handoffs = acquisition_env(OUTSIDE)
    pexc = CA.Refused("see-through-not-covered-at-pose", "outside its mark", final=False)
    kind, res = route(CA, pexc, points_fixture(), tmp / "c1893")
    chain = (handoffs[-1].get("chain") if handoffs else None) or []
    wit = next((c for c in chain if c.get("kind") == "witness"), None)
    check("C1893", len(calls) == 1 and kind == "refused" and getattr(res, "code", None) == "top-down-participant-unseen"
          and len(handoffs) == 1, "a witness camera that misses the mark was kept, or the top-down was not tried after it",
          f"route {kind} {getattr(res, 'code', None)}, renders {len(calls)}, top-down calls {len(handoffs)}")
    check("C1893", wit is not None and wit.get("status") == "refused",
          "the chain the top-down receives does not say the witness camera was tried and refused", json.dumps(chain)[:400])


def c1894_station_view_caption(tmp: Path):
    """Wrong answers: a picture from the witness camera captioned as an ordinary composed eye-level picture; a reel
    words for it (the reel carries no disclaimers); an ordinary eye-level picture given the station words."""
    import evalreport as ER
    import reel_plan as RP
    words = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))["reader"]["figureCaptions"]
    prov = {"viewKind": "eye-level", "camera": {"viewKind": "eye-level", "cameraSource": "witness-station"}}
    cap = ER.view_kind_caption(prov, words)
    plain = ER.view_kind_caption({"viewKind": "eye-level", "camera": {"viewKind": "eye-level"}}, words)
    check("C1894", "station view" in cap.lower() and not plain,
          "a witness-camera picture is not captioned as a station view (or a composed one is)", f"{cap!r} / {plain!r}")
    check("C1894", RP.view_kind_line(prov) == "", "the reel adds words to a station view", RP.view_kind_line(prov))


def c1895_old_pose_refusal_recaptured(tmp: Path):
    """Wrong answers: a final refusal from before the rule (a composed pose refused for a see-through mark, no witness
    step) reused as the geometry's; one with the witness step tried not reused."""
    EP = load("ac_evalpipeline", HARNESS / "evalpipeline.py")
    old = {"code": "top-down-participant-unseen", "final": True, "chain": [
        {"kind": "eye-level", "status": "refused", "code": "see-through-not-covered-at-pose", "detail": "1 px inside"},
        {"kind": "cutaway", "status": "skipped", "code": "cutaway-not-applicable", "detail": "a station saw both"},
        {"kind": "top-down", "status": "refused", "code": "top-down-participant-unseen", "detail": "1 px"}]}
    new = copy.deepcopy(old)
    new["chain"].insert(2, {"kind": "witness", "status": "refused", "code": "witness-not-covered", "detail": "outside"})
    check("C1895", EP.deterministic_refusal(old) is False and EP.deterministic_refusal(new) is True,
          "a refusal from before the witness step is reused as the geometry's (or one that tried it is not)",
          f"old {EP.deterministic_refusal(old)}, new {EP.deterministic_refusal(new)}")


def refused_chain(eye_code: str, final_code: str, participant: str, keys, witness: bool) -> dict:
    chain = [{"kind": "eye-level", "status": "refused", "code": eye_code, "detail": "x"}]
    if witness:
        chain += [{"kind": "cutaway", "status": "skipped", "code": "cutaway-not-applicable",
                   "detail": "a station saw both participants, so nothing stood in front to hide"},
                  {"kind": "witness", "status": "refused", "code": "witness-not-covered", "detail": "outside"}]
    else:
        chain += [{"kind": "cutaway", "status": "refused", "code": "cutaway-refused", "detail": "x"}]
    chain.append({"kind": "top-down", "status": "refused", "code": final_code, "detail": "1 px"})
    return {"status": "refused", "formKey": keys[0], "recordKeys": list(keys), "reason": f"refused ({final_code})",
            "refusal": {"code": final_code, "participant": participant, "final": True, "chain": chain}}


def c1896_refused_reason_true(tmp: Path):
    """Two refused, unburied clashes: 000101 x 000201, whose chain says a station at eye height saw both (the alpha-card
    plant), and 000104 x 000204, whose 000204 the renderer cannot mark (see-through-unmarked). Wrong answer: either is
    given "No camera could see the clash" in the reel's reason line (the report's 'About the review reel')."""
    RV = load("ac_reel_vignettes", HERE / "test_reel_vignettes.py")
    import reel_plan as RP
    pres, names, masters, search, mods, captures = RV.capture_binding_fixture()
    shipped = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    pol = dict(RV.POLICY, captureAttemptsPerType=1, stillReasons=shipped["stillReasons"],
               stillReasonsCount=shipped.get("stillReasonsCount"), stillReasonsShort=shipped.get("stillReasonsShort"))
    keep = [c for c in captures if c["request"] not in ("req-own", "req-cell-two")]
    pres2 = copy.deepcopy(pres)
    pres2["media"] = [m for m in pres["media"] if m["provenance"]["captureRequest"] not in ("req-own", "req-cell-two")]
    a1, b1 = "000101:Target.esp", "000201:Alpha.esp"
    a2, b2 = "000104:Target.esp", "000204:Alpha.esp"
    r1 = dict(refused_chain("see-through-not-covered-at-pose", "top-down-participant-unseen", b1, (a1, b1), True),
              request="req-saw")
    r2 = dict(refused_chain("points-no-visible-face", "see-through-unmarked", b2, (a2, b2), False), request="req-unmarked")
    reel = RP.plan_reel(copy.deepcopy(pres2), names, masters, pol, RV.binding_words(), search, mods, keep + [r1, r2],
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    un = {c.get("formKey"): c.get("reason") for ch in reel.get("chapters") or [] if ch.get("kind") == "unpictured"
          for c in ch.get("clashes") or []}
    lines = " ".join(str(x) for x in reel.get("reportLines") or [])
    false_ = shipped["stillReasons"]["captureRefused"].rstrip(".")
    check("C1896", un.get(a1) not in (None, "captureRefused") and un.get(a2) not in (None, "captureRefused"),
          "a clash a station saw, or one whose object cannot be marked, is counted as one no camera could see",
          f"reasons {un}")
    check("C1896", bool(lines) and false_ not in lines and "eye height" in lines and "see-through" in lines,
          "the reel's reason line says 'No camera could see the clash' for them, or does not say why truly",
          f"lines {lines[:500]!r}")


def c1897_stale_image_key(tmp: Path):
    """Hroggar 11-09's shape: image-observations/ keeps an empty image-key.json and the 'no picture' acknowledgement,
    and the capture is rerun and now has a frame. Wrong answer: the stage accepted with 0 images, no blind review."""
    EP = load("ac_evalpipeline2", HARNESS / "evalpipeline.py")
    from PIL import Image
    out = tmp / "c1897"
    seq = out / "capture" / "capture-x" / "sequence"
    (seq / "with").mkdir(parents=True, exist_ok=True)
    Image.new("RGB", (8, 8), (10, 200, 30)).save(seq / "with" / "frame.png")
    (seq / "capture-result.json").write_text("{}", encoding="utf-8")
    (out / "capture" / "captures.json").write_text(json.dumps([{"id": "capture-x", "status": "captured",
                                                                 "result": str(seq / "capture-result.json")}]),
                                                    encoding="utf-8")
    sdir = out / "image-observations"
    (sdir / "indexed-images").mkdir(parents=True, exist_ok=True)
    (sdir / "image-key.json").write_text("[]", encoding="utf-8")
    (sdir / "observations.json").write_text(json.dumps({"frames": [], "acknowledged": "every capture was refused"}),
                                            encoding="utf-8")
    ctx = types.SimpleNamespace(out=out)
    got = None
    try:
        got = EP.stage_image_observations(ctx, None, sdir)
    except (EP.Refusal, EP.Awaiting) as e:
        got = e
    key = json.loads((sdir / "image-key.json").read_text(encoding="utf-8"))
    check("C1897", not isinstance(got, tuple), "a kept empty image key let the stage accept a capture that has frames "
          "with no blind review", str(got)[:300])
    check("C1897", len(key) == 1 and (sdir / "indexed-images" / "image-001.png").is_file(),
          "the image key and packet were not rebuilt for the capture's frame", f"key {key}")


def c1898_plugin_case_in_capture_requests(tmp: Path):
    """Ice Blade's re-scored shape: the finding's counts name 'Unofficial Skyrim Special Edition Patch.esp', the
    overlapprobe evidence 'unofficial skyrim special edition patch.esp'. Plugin names are case-insensitive. Wrong answer:
    no capture request, so the reel's one vignette is never captured (Ice Blade, round 2 rerun)."""
    import hashlib
    import report_scenes as RS
    bundle = tmp / "c1898" / "bundle"
    rel = "evidence/sweep/overlapprobe/baseline.json"
    doc = {"Probe": "overlapprobe", "Configuration": {"Fingerprint": "cd" * 32, "Plugins": []},
           "Conflicts": [{"Kind": "OVERLAP", "A": "unofficial skyrim special edition patch.esp", "B": "icebladeofthemonarch.esp",
                          "Necessary": [{"FormKey": "000D75:icebladeofthemonarch.esp", "Physical": "Holds", "Label": "Cave"}],
                          "Alternative": [{"FormKey": "0FC98D:Skyrim.esm"}]}]}
    (bundle / rel).parent.mkdir(parents=True, exist_ok=True)
    raw = json.dumps(doc).encode("utf-8")
    (bundle / rel).write_bytes(raw)
    pres = {"evidence": [{"id": "E-1", "kind": "emit", "part": "arm", "armId": "baseline", "path": rel,
                          "sha256": hashlib.sha256(raw).hexdigest()}],
            "findings": [{"id": "F-110", "kind": "finding", "part": "arm", "checkId": "geometry.object-overlap",
                          "armId": "baseline", "evidenceIds": ["E-1"], "readerRelevant": True,
                          "allRows": [{"formKey": "000D75:icebladeofthemonarch.esp"}],
                          "counts": {"groupKind": "OVERLAP", "A": "Unofficial Skyrim Special Edition Patch.esp",
                                     "B": "icebladeofthemonarch.esp"}}]}
    got = RS.capture_requests(pres, bundle)
    keys = [r.get("recordKeys") for r in got.get("requests") or []]
    check("C1898", any(k and k[0] == "000D75:icebladeofthemonarch.esp" for k in keys),
          "a plugin spelled in another case in the finding's counts drops the finding's capture request",
          f"requests {keys}, limitations {got.get('limitations')}")
    # the form key carries the plugin name too: a finding row spelled in another case than the probe's record
    pres["findings"][0]["allRows"] = [{"formKey": "000D75:IceBladeOfTheMonarch.esp"}]
    got2 = RS.capture_requests(pres, bundle)
    keys2 = [r.get("recordKeys") for r in got2.get("requests") or []]
    check("C1898", any(k and k[0] == "000D75:icebladeofthemonarch.esp" for k in keys2),
          "a form key whose plugin part is spelled in another case drops the finding's capture request",
          f"requests {keys2}")


def c1899_marked_view_captions(tmp: Path):
    """Hroggar's report-02: a world-state vignette marks only its focus (magenta), so the marked view's caption read
    "None () marked in cyan"; and the station view line was said on every frame of a figure. Wrong answers: a missing
    mark named "None"; a single mark not named; the station line on a figure's later frames."""
    import evalreport as ER
    words = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))["reader"]["figureCaptions"]
    fn = getattr(ER, "highlight_caption", None)
    if not check("C1899", fn is not None, "evalreport has no marked-view caption rule a test can call"):
        return
    obj = {PLANT: "Tree Thicket Snow", HOUSE: "Farmhouse Snow"}.get
    key, slots = fn({"magenta": PLANT}, obj, lambda p: p.rsplit(".", 1)[0])
    text = str(words.get(key) or "").format(setup="With the mod", **slots)
    check("C1899", key == "highlightOne" and "None" not in text and "Tree Thicket Snow" in text and "magenta" in text,
          "a marked view with one mark names a missing participant or not the marked one", f"{key}: {text!r}")
    key2, slots2 = fn({"cyan": HOUSE, "magenta": PLANT}, obj, lambda p: p.rsplit(".", 1)[0])
    check("C1899", key2 == "highlight" and slots2.get("a") == "Farmhouse Snow" and slots2.get("b") == "Tree Thicket Snow",
          "a marked view with both marks lost one", f"{key2}: {slots2}")
    prov = {"viewKind": "eye-level", "camera": {"cameraSource": "witness-station"}}
    first, later = ER.view_kind_caption(prov, words, first=True), ER.view_kind_caption(prov, words, first=False)
    check("C1899", "station view" in first.lower() and later == "",
          "the station view line is not said once per figure (on its first frame only)", f"{first!r} / {later!r}")


def c1930_media_bundle_frozen_registry(tmp: Path):
    """Hroggar's report stage: the check registry changed after the evaluation was planned, so `evalrun bundle --media`
    refused (registry-changed) and the pipeline delivered no report. Wrong answer: the media-bundle command does not
    name the evidence bundle's own registry copy, the one the plan froze."""
    EP = load("ac_evalpipeline3", HARNESS / "evalpipeline.py")
    out = tmp / "c1930"
    reg = out / "bundle" / "evidence" / "registry" / "check-registry.json"
    reg.parent.mkdir(parents=True, exist_ok=True)
    reg.write_text("{}", encoding="utf-8")
    live = out / "live-registry.json"
    live.write_text('{"changed": true}', encoding="utf-8")
    (out / "evaluation").mkdir(parents=True, exist_ok=True)
    (out / "evaluation" / "plan.json").write_text(json.dumps({"registry": {"path": str(live), "sha256": hashlib.sha256(
        b"{}").hexdigest()}}), encoding="utf-8")
    seq = out / "capture" / "capture-x" / "sequence"
    seq.mkdir(parents=True, exist_ok=True)
    (seq / "capture-result.json").write_text("{}", encoding="utf-8")
    (seq / "media-with.json").write_text(json.dumps({"items": [{"path": "x.png"}]}), encoding="utf-8")
    (out / "capture" / "captures.json").write_text(json.dumps([{"id": "capture-x", "status": "captured",
                                                                 "result": str(seq / "capture-result.json")}]),
                                                    encoding="utf-8")
    seen = []

    class Stop(Exception):
        pass

    def fake_child(ctx, cdir, name, argv, **kw):
        seen.append((name, [str(x) for x in argv]))
        raise Stop()
    EP.child = fake_child
    EP.reader_cmd = lambda ctx: "reader"
    EP.names_env = lambda ctx: {}
    ctx = types.SimpleNamespace(out=out, entry={}, machine={})
    try:
        EP.stage_report(ctx, None, out / "report")
    except Stop:
        pass
    argv = next((a for n, a in seen if n == "media-bundle"), [])
    i = argv.index("--registry") if "--registry" in argv else -1
    check("C1930", i >= 0 and Path(argv[i + 1]) == reg,
          "the media bundle is built without the evidence bundle's own frozen registry, so a changed registry refuses "
          "the report stage", f"media-bundle argv {argv[:20]}")


def c1931_reel_speaks_no_ids(tmp: Path):
    """Hroggar's reel-02 read "0138CE:Skyrim.esm" and "BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100" aloud on its
    What to do page. Wrong answers: a form key or an underscore editor ID in a still page's narration; a bracketed
    editor ID after a place name spoken; the page's own text changed; tool names (DynDOLOD, xLODGen) or a plugin file
    name altered."""
    import reel_stills as RS
    row = ("It also puts back the earlier value of 3 record fields that Lux changes, on Moorside Inn, 0138CE:Skyrim.esm; "
           "if you use Lux, check that you want this. It switches off BUV_EisaFollowerRef (a placed BUV_EisaFollower, in "
           "Moorside Inn). It removes the scripts from BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100 and "
           "BUV_ControlQuestEffect_dunFrostmereCryptQST_16. Rebuild generated output (DynDOLOD, xLODGen, Occlusion.esp). "
           "It is in Alva's House (MorthalAlvasHouse). See Finding_Helgi_and_Laelette.esp.")
    page = {"layout": "list", "title": "What to do", "rows": [row]}
    said = RS.page_say(page)
    bad = [x for x in ("0138CE", "Skyrim.esm", "BUV_", "_dun", "MorthalAlvasHouse") if x in said]
    check("C1931", not bad and "Moorside Inn" in said and "Alva's House" in said and "(Morthal" not in said,
          "a still page's narration speaks a form key or an editor ID", f"spoken {bad}: {said[:400]!r}")
    check("C1931", page["rows"] == [row] and "DynDOLOD, xLODGen, Occlusion.esp" in said
          and "Finding_Helgi_and_Laelette.esp" in said,
          "the page's text, a tool name or a plugin file name was changed by the spoken wording", said[-200:])


def c1932_cell_by_form_key_and_true_reasons(tmp: Path):
    """Cut Content 0b31: a verified eye-level camera in RiverwatchExterior01, Cut Content Restoration's editor ID for the
    base-game cell 00B51E:Skyrim.esm; the setup without the mod has no cell of that editor ID, so viewprobe refused the
    without frame and the reel said "No camera could see the clash". Wrong answers: a frame addressing the cell by an
    editor ID a mod can change instead of its form key; a cell whose defining plugin the without order lacks not refused
    as a place only the mod has (decided from the order, not viewprobe's words); after a ready eye-level camera, a
    refusal said as one no camera could see; a refusal of the acquisition itself (no camera ready) said as seen."""
    CA = load("ac_capture_acquire_cell", HARNESS / "capture_acquire.py")
    import reel_plan as RP
    root = tmp / "c1932"
    rd = root / "receipts"
    (rd / "points-work" / "p0-placements").mkdir(parents=True, exist_ok=True)
    (rd / "points-work" / "p0-placements" / "console.txt").write_text(
        "viewprobe ...\ncell RiverwatchExterior01 (exterior)  00B51E:Skyrim.esm\nmore\n", encoding="utf-8")
    acq = {"status": "camera-ready", "receipts": {"receiptsDir": str(rd)}}
    with_o, without_o, nomaster_o = root / "with.json", root / "without.json", root / "nomaster.json"
    with_o.write_text(json.dumps({"Plugins": ["Skyrim.esm", "Update.esm", "CutContentRestoration.esp"]}), encoding="utf-8")
    without_o.write_text(json.dumps({"Plugins": ["Skyrim.esm", "Update.esm"]}), encoding="utf-8")
    fk, ref = CA.capture_cell(acq, {"without": str(without_o), "with": str(with_o)}) if hasattr(CA, "capture_cell") else (None, None)
    cam = {"cell": "RiverwatchExterior01", "viewprobe": "--eye 1,2,3 --yaw 0 --pitch 0 --fov 65"}
    argv = CA.capture_argv({"requestType": "overlap-pair"}, {**cam, **({"cellFormKey": fk} if fk else {})}, "without",
                           str(without_o), [], 320)
    check("C1932", argv[0] == "00B51E:Skyrim.esm" and ref is None,
          "a frame addresses the cell by an editor ID a mod can change, not by its form key", f"cell argument {argv[0]!r}")
    (rd / "points-work" / "p0-placements" / "console.txt").write_text(
        "cell CCRNewPlace (exterior)  000801:CutContentRestoration.esp\n", encoding="utf-8")
    nomaster_o.write_text(json.dumps({"Plugins": ["Skyrim.esm"]}), encoding="utf-8")
    fk2, ref2 = CA.capture_cell(acq, {"without": str(nomaster_o), "with": str(with_o)}) if hasattr(CA, "capture_cell") else (None, None)
    check("C1932", (ref2 or {}).get("code") == "place-only-with-mod" and (ref2 or {}).get("states") == ["without"],
          "a cell only the mod defines is not refused as a place only the mod has", str(ref2))
    a1, b1 = "000101:Target.esp", "000201:Alpha.esp"
    ready = {"status": "refused", "formKey": a1, "recordKeys": [a1, b1], "request": "r1",
             "acquisition": {"status": "camera-ready", "viewKind": "eye-level"}, "reason": "viewprobe could not render"}
    only = dict(ready, request="r2", refusal={"code": "place-only-with-mod", "final": True})
    acq_refused = {"status": "refused", "formKey": a1, "recordKeys": [a1, b1], "request": "r3",
                   "refusal": {"code": "top-down-participant-unseen", "final": True,
                               "chain": [{"kind": "eye-level", "status": "refused", "code": "points-no-station"}]}}
    got = [RP.unpictured_refused_reason([a1, b1], [x], set()) for x in (ready, only, acq_refused)]
    check("C1932", got == ["stationSaw", "placeOnlyWithMod", "captureRefused"],
          "a refusal after a ready eye-level camera, or of a place only the mod has, is said as one no camera could see "
          "(or a refused acquisition is said as seen)", str(got))


def c1933_media_bundle_frozen_scope(tmp: Path):
    """Ice Blade's report stage: the plan's scope path was on a folder since deleted, so `evalrun bundle --media` refused
    (scope-missing). Wrong answers: the media-bundle command does not name the evidence bundle's own scope copy; an
    input the plan still holds unchanged is replaced by the copy; a plan pinning an input the report stage does not
    know is passed on silently instead of refused."""
    EP = load("ac_evalpipeline4", HARNESS / "evalpipeline.py")
    out = tmp / "c1933"
    ev = out / "bundle" / "evidence"
    (ev / "registry").mkdir(parents=True, exist_ok=True)
    (ev / "registry" / "check-registry.json").write_text("{}", encoding="utf-8")
    (ev / "scope").mkdir(parents=True, exist_ok=True)
    (ev / "scope" / "scope.json").write_text("{}", encoding="utf-8")
    live = out / "registry-live.json"
    live.write_text("{}", encoding="utf-8")                  # the registry's original still hashes to the plan
    h = hashlib.sha256(b"{}").hexdigest()
    plan = {"registry": {"path": str(live), "sha256": h}, "scope": {"path": str(out / "gone" / "scope.json"), "sha256": h},
            "explicitDecisions": {"settings": {"path": "x", "sha256": h}}}
    (out / "evaluation").mkdir(parents=True, exist_ok=True)
    (out / "evaluation" / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    seq = out / "capture" / "capture-x" / "sequence"
    seq.mkdir(parents=True, exist_ok=True)
    (seq / "capture-result.json").write_text("{}", encoding="utf-8")
    (seq / "media-with.json").write_text(json.dumps({"items": [{"path": "x.png"}]}), encoding="utf-8")
    (out / "capture" / "captures.json").write_text(json.dumps([{"id": "capture-x", "status": "captured",
                                                                 "result": str(seq / "capture-result.json")}]),
                                                    encoding="utf-8")
    seen = []

    class Stop(Exception):
        pass

    def fake_child(ctx, cdir, name, argv, **kw):
        seen.append((name, [str(x) for x in argv]))
        raise Stop()
    EP.child = fake_child
    EP.reader_cmd = lambda ctx: "reader"
    EP.names_env = lambda ctx: {}
    ctx = types.SimpleNamespace(out=out, entry={}, machine={})
    try:
        EP.stage_report(ctx, None, out / "report")
    except Stop:
        pass
    argv = next((a for n, a in seen if n == "media-bundle"), [])
    i = argv.index("--scope") if "--scope" in argv else -1
    check("C1933", i >= 0 and Path(argv[i + 1]) == ev / "scope" / "scope.json" and "--registry" not in argv,
          "the media bundle is built without the evidence bundle's own scope copy (or replaces an input the plan still "
          "holds unchanged)", f"media-bundle argv {argv[:24]}")
    # a plan pinning an input the report stage does not know fails in one sentence, not silently
    plan["population"] = {"path": str(out / "gone" / "population.json"), "sha256": h}
    (out / "evaluation" / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    seen.clear()
    said = None
    try:
        EP.stage_report(ctx, None, out / "report")
    except Stop:
        said = "went on to bundle"
    except EP.Refusal as e:
        said = str(e)
    check("C1933", bool(said) and "population" in said and "went on" not in said,
          "a plan pinning an input the report stage does not know is passed over silently", str(said))


def c1935_vignette_requests_its_own_pair(tmp: Path):
    """Helgi: the vignette's clash is 000941 x 000826, and 18 candidates start with the COTN static 000941; matched by the
    first key alone, all 18 became the vignette's capture requests. Wrong answers: a request for a pair the vignette
    does not show; no request for the vignette's own pair."""
    import hashlib as _h
    import report_scenes as RS
    bundle = tmp / "c1935" / "bundle"
    rel = "evidence/sweep/overlapprobe/baseline.json"
    a, own, other = "000941:COTN - Morthal.esp", "000826:Finding_Helgi.esp", "00083D:Finding_Helgi.esp"
    conflicts = [{"Kind": "OVERLAP", "A": "COTN - Morthal.esp", "B": "Finding_Helgi.esp",
                  "Necessary": [{"FormKey": a, "Physical": "Holds", "Label": "Morthal"}],
                  "Alternative": [{"FormKey": b}]} for b in (own, other)]
    doc = {"Probe": "overlapprobe", "Configuration": {"Fingerprint": "ef" * 32, "Plugins": []}, "Conflicts": conflicts}
    (bundle / rel).parent.mkdir(parents=True, exist_ok=True)
    raw = json.dumps(doc).encode("utf-8")
    (bundle / rel).write_bytes(raw)
    pres = {"evidence": [{"id": "E-1", "kind": "emit", "part": "arm", "armId": "baseline", "path": rel,
                          "sha256": _h.sha256(raw).hexdigest()}],
            "findings": [{"id": "F-1", "kind": "finding", "part": "arm", "checkId": "geometry.object-overlap",
                          "armId": "baseline", "evidenceIds": ["E-1"], "readerRelevant": True,
                          "allRows": [{"formKey": a}],
                          "counts": {"groupKind": "OVERLAP", "A": "COTN - Morthal.esp", "B": "Finding_Helgi.esp"}}],
            "reel": {"status": "planned", "chapters": [{"id": "R-02", "kind": "vignette", "frames": [],
                                                         "clash": {"type": "overlap", "armId": "baseline", "formKey": a,
                                                                   "other": [own], "withoutArmId": "c-1"}}]}}
    RS.reel_requests = lambda presentation, bundle_, policy: ([], [])
    got = RS.capture_requests(pres, bundle)
    pairs = [r.get("recordKeys") for r in got.get("requests") or []]
    check("C1935", len(pairs) == 1 and pairs[0][:1] == [a] and own in pairs[0] and other not in pairs[0],
          "a vignette gets capture requests for pairs it does not show (or none for its own pair)", f"requests {pairs}")


CODE_RE = re.compile(r"^[a-z]+(?:-[a-z0-9]+)+$")


def emittable_refusal_codes() -> set:
    """Every refusal code the capture side can emit: the first argument of a Refused / Unpickable call, a string
    assigned to `code`, and a "code" value in a chain step or refusal dict, in capture_acquire, capture_points and
    capture_navmesh (codes only: lower-case words joined by hyphens)."""
    import ast
    out = set()
    for f in ("capture_acquire.py", "capture_points.py", "capture_navmesh.py"):
        tree = ast.parse((HARNESS / f).read_text(encoding="utf-8"))
        for n in ast.walk(tree):
            vals = []
            if isinstance(n, ast.Call) and (getattr(n.func, "id", None) or getattr(n.func, "attr", None)) in ("Refused", "Unpickable") and n.args:
                vals = [n.args[0]]
            elif isinstance(n, ast.Assign) and any(getattr(t, "id", None) == "code" for t in n.targets):
                vals = [n.value]
            elif isinstance(n, ast.Dict):
                vals = [v for k, v in zip(n.keys, n.values) if isinstance(k, ast.Constant) and k.value == "code"]
            for v in vals:
                for x in ast.walk(v):
                    if isinstance(x, ast.Constant) and isinstance(x.value, str) and CODE_RE.match(x.value):
                        out.add(x.value)
    return out


def c1936_refusal_reasons_by_table(tmp: Path):
    """Four refusal paths fell into "No camera could see the clash" in one day. Wrong answers: a refusal code the capture
    side can emit with no reason of its own in the table (or one naming a reason with no words); a reference switched
    off for good said as one no camera could see; a camera-spot refusal said as one; an unknown code not falling back
    to the generic line."""
    import reel_plan as RP
    pol = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    table = pol.get("refusalReasons") or {}
    codes = emittable_refusal_codes()
    missing = sorted(c for c in codes if c not in table)
    unworded = sorted(k for k in set(table.values()) if k not in (pol.get("stillReasons") or {})
                      or k not in (pol.get("stillReasonsShort") or {}))
    check("C1936", len(codes) > 40 and not missing and not unworded,
          "a refusal code the capture side can emit has no reason of its own (or names a reason with no words)",
          f"{len(codes)} codes; missing {missing[:8]}; unworded {unworded}")
    fn = getattr(RP, "refusal_reason", None)
    if not check("C1936", fn is not None, "reel_plan has no table-driven refusal reason"):
        return
    off = {"refusal": {"code": "points-unpickable", "pickerCode": "points-not-drawn", "final": True,
                       "notDrawn": {"formKey": "0DFDA9:Skyrim.esm", "why": "enableParent", "enableParent": "000014:Skyrim.esm",
                                    "forGood": True}}}
    floor = {"refusal": {"code": "points-unpickable", "pickerCode": "points-no-floor"}}
    unknown = {"refusal": {"code": "never-heard-of-it"}}
    got = [fn(x, table) for x in (off, floor, unknown)]
    words = pol["stillReasons"].get(got[0], "")
    check("C1936", got == ["switchedOff", "noPlace", "captureRefused"] and "switched off" in words and "for good" in words,
          "a reference switched off for good, or a camera-spot refusal, is not said by its own reason", f"{got}; {words!r}")


def c1937_top_down_note_not_spoken(tmp: Path):
    """Helgi's reel narrated "Sliced top-down view from above, everything above z -13444 removed; not the player's view."
    Wrong answers: the frame's note is in the vignette's narration (and so its subtitles); the frames lose the note."""
    RV = load("ac_reel_vignettes2", HERE / "test_reel_vignettes.py")
    import reel_plan as RP
    pres, names, masters, search, mods, captures = RV.capture_binding_fixture()
    for m in pres["media"]:
        if m["provenance"]["captureRequest"] == "req-own":
            m["provenance"].update(viewKind="top-down", cullAbove=-13444.0)
    reel = RP.plan_reel(copy.deepcopy(pres), names, masters, RV.POLICY, RV.binding_words(), search, mods, captures,
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    vig = [ch for ch in reel.get("chapters") or [] if (ch.get("clash") or {}).get("formKey") == "000101:Target.esp"]
    said = " ".join(str(ch.get("narration") or "") for ch in vig)
    notes = [f.get("viewNote") for ch in vig for f in ch.get("frames") or [] if f.get("viewNote")]
    check("C1937", bool(vig) and "top-down" not in said.lower() and "player's view" not in said and bool(notes),
          "the top-down note is read in the narration (or the frames lost it)", f"narration {said[:300]!r}; notes {notes[:1]}")


def c1938_no_overlap_candidate_words(tmp: Path):
    """Ice Blade's and Helgi's What to do said "1 overlap candidate" / "37 overlap candidates" on screen and aloud.
    Wrong answers: the words "overlap candidate" anywhere in the report and reel words or the code that builds them; the
    what-to-do line for overlaps not in plain words."""
    hits = []
    for f in [SKILL / "report-priorities.json", SKILL / "scene-policy.json", HARNESS / "reel_plan.py", HARNESS / "evalreport.py",
              HARNESS / "reel_stills.py", HARNESS / "evalreel.py", *sorted((HARNESS / "evalreport_templates").glob("*"))]:
        if f.is_file() and "overlap candidate" in f.read_text(encoding="utf-8", errors="replace").lower():
            hits.append(f.name)
    ns = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
    def find(o):
        if isinstance(o, dict):
            if "geometry.object-overlap" in o and "candNoun" in o:
                return o
            for v in o.values():
                r = find(v)
                if r:
                    return r
        return None
    step = find(ns) or {}
    line = str(step.get("geometry.object-overlap") or "").format(title="Objects occupying the same space", count=1,
                                                                  candNoun=(step.get("candNoun") or ["?"])[0],
                                                                  state="is left with every supplied patch", them="it")
    check("C1938", not hits and "candidate" not in line and "pair of objects" in line,
          "the words 'overlap candidate' reach the report or reel", f"files {hits}; line {line!r}")
    # nor built from parts: a template "overlap {noun}" with a noun "candidate(s)" says it too
    words = (SKILL / "report-priorities.json").read_text(encoding="utf-8")
    nouns = re.findall(r'"(?:candidateSingular|candidatePlural)": "([^"]*)"', words) + sum(
        [re.findall(r'"([^"]*)"', m) for m in re.findall(r'"candNoun": \[([^\]]*)\]', words)], [])
    built = re.findall(r"overlap \{(?:candNoun|candidateNoun)\}", words)
    check("C1938", not built and nouns and not any("candidate" in n for n in nouns),
          "'overlap candidate' is still built from a template and a noun", f"templates {built}; nouns {nouns}")


def c1939_not_drawn_carried(tmp: Path):
    """The picker's not-drawn refusal said why only in its sentence. Wrong answers: viewprobe's reason not carried as
    fields (enable parent, for good); the capture result dropping them; the reel's reason line saying "No camera could
    see the clash" for a reference switched off for good."""
    CP = load("ac_capture_points_nd", HARNESS / "capture_points.py")
    CA = load("ac_capture_acquire_nd", HARNESS / "capture_acquire.py")
    fn = getattr(CP, "not_drawn_detail", None)
    nd = fn("0DFDA9:Skyrim.esm", "NOT DRAWN: disabled via enable parent 000014:Skyrim.esm OPPOSITE") if fn else {}
    check("C1939", nd.get("forGood") is True and nd.get("enableParent") == "000014:Skyrim.esm"
          and "notDrawn" in getattr(CA, "RESULT_REFUSAL_KEYS", ()),
          "the not-drawn refusal's reason is not carried as fields into the capture result", f"{nd}")
    # the player as enable parent without viewprobe's opposite flag is not "for good": the flag is never inferred
    plain = fn("0DFDA9:Skyrim.esm", "NOT DRAWN: disabled via enable parent 000014:Skyrim.esm") if fn else {}
    check("C1939", plain.get("why") == "enableParent" and plain.get("forGood") is False,
          "switched off for good is inferred from the enable parent alone, without the opposite flag", f"{plain}")
    RV = load("ac_reel_vignettes3", HERE / "test_reel_vignettes.py")
    import reel_plan as RP
    pres, names, masters, search, mods, captures = RV.capture_binding_fixture()
    shipped = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    pol = dict(RV.POLICY, captureAttemptsPerType=1, stillReasons=shipped["stillReasons"],
               stillReasonsShort=shipped.get("stillReasonsShort"), refusalReasons=shipped.get("refusalReasons"))
    keep = [c for c in captures if c["request"] not in ("req-own", "req-cell-two")]
    pres2 = copy.deepcopy(pres)
    pres2["media"] = [m for m in pres["media"] if m["provenance"]["captureRequest"] not in ("req-own", "req-cell-two")]
    a1, b1 = "000101:Target.esp", "000201:Alpha.esp"
    off = {"request": "req-off", "status": "refused", "formKey": a1, "recordKeys": [a1, b1],
           "refusal": {"code": "points-unpickable", "pickerCode": "points-not-drawn", "final": True,
                       "notDrawn": {"formKey": b1, "why": "enableParent", "enableParent": "000014:Skyrim.esm", "forGood": True}}}
    reel = RP.plan_reel(copy.deepcopy(pres2), names, masters, pol, RV.binding_words(), search, mods, keep + [off],
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    lines = " ".join(str(x) for x in reel.get("reportLines") or [])
    check("C1939", "switched off in the game for good" in lines and "No camera could see the clash" not in lines,
          "a reference switched off for good is said as one no camera could see", f"lines {lines[:400]!r}")
    # the same refusal without the opposite flag is not said as switched off for good
    off2 = copy.deepcopy(off)
    off2["refusal"]["notDrawn"].update(opposite=None, forGood=False)
    reel2 = RP.plan_reel(copy.deepcopy(pres2), names, masters, pol, RV.binding_words(), search, mods, keep + [off2],
                         markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    lines2 = " ".join(str(x) for x in reel2.get("reportLines") or [])
    check("C1939", "for good" not in lines2 and "starts switched off" in lines2,
          "a reference not drawn without the opposite flag is said as switched off for good", f"lines {lines2[:400]!r}")


def c1960_not_drawn_without_fields(tmp: Path):
    """Helgi's regeneration 2: its captures predate the not-drawn fields, so the three switched-off pairs fell to the
    no-model line, "The placed object has no model to draw", which is false (COTN switches them off). Wrong answers: a
    not-drawn refusal without viewprobe's cause fields (or with an unknown cause) said as having no model; the no-model
    line missing where viewprobe's fields say the base carries no model."""
    import reel_plan as RP
    pol = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    table = pol.get("refusalReasons") or {}
    base = {"code": "points-unpickable", "pickerCode": "points-not-drawn", "final": True}
    got = [RP.refusal_reason({"refusal": dict(base, **extra)}, table) for extra in (
        {}, {"notDrawn": {"why": "other", "forGood": False}}, {"notDrawn": {"why": "noModel", "forGood": False}},
        {"notDrawn": {"why": "noBase", "forGood": False}})]
    words = pol["stillReasons"]
    check("C1960", got[:2] == ["notDrawnUnknown", "notDrawnUnknown"] and got[2:] == ["notDrawn", "notDrawn"]
          and "model" not in words.get("notDrawnUnknown", "model") and "notDrawnUnknown" in (pol.get("stillReasonsShort") or {}),
          "a not-drawn refusal without viewprobe's cause fields is said as having no model (or the no-model line is lost)",
          f"{got}; {words.get('notDrawnUnknown')!r}")



CC_WHAT_TO_DO = ("Lux - Skyrim Cut Content Restoration is a patch for Skyrim Cut Content Restoration and Lux. None of the "
                 "predicted clashes is between them, so this evaluation does not show whether you need it. It switches off "
                 "15E420:Skyrim Cut Content Restoration (a placed FarmInteriorFireLight01, in Frost River Meadery), "
                 "15E455:Skyrim Cut Content Restoration (a placed FarmInteriorLight01, in Frost River Meadery) and "
                 "15E475:Skyrim Cut Content Restoration (a placed FarmInteriorShadowLight01, in Frost River Meadery) and 49 "
                 "more, which Skyrim Cut Content Restoration places (read from the patch's own records).")
CC_PATCH = ("It switches off 1AA4FA:Skyrim Cut Content Restoration (a placed CollisionMarker, in Castle Dour), "
            "1AA4F8:Skyrim Cut Content Restoration (a placed ImpCandelabraCandle02, in Castle Dour) and "
            "1AA4F9:Skyrim Cut Content Restoration (a placed SolitudeInnCandleDefault, in Castle Dour), which Skyrim Cut "
            "Content Restoration places (read from the patch's own records).")
SYNTHETIC = ["It also puts back the earlier value of 3 record fields that Lux changes, on Moorside Inn, 0138CE:Skyrim.esm; if you use Lux.",
             "It removes the scripts from BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100 and BUV_ControlQuestEffect_dunFrostmereCryptQST_16.",
             "It is in Alva's House (MorthalAlvasHouse).",
             "In Morthal, 000941:COTN - Morthal.esp's MorthalLonghouse01 crosses a tree."]


def c1961_final_speech_has_no_ids(tmp: Path):
    """Cut Content's reel said "15E420:Skyrim Cut Content Restoration (a placed FarmInteriorFireLight01 ...)": the reel
    had put the mod's name in place of the plugin, so the form-key rule no longer matched, and a one-word editor ID had
    no underscore. Wrong answers: a form-key shape (six hex digits and a colon) or an editor-ID shape in the final string
    the storyboard hands to speech (still pages and vignettes) or the render hands to speech and subtitles; the page's
    own text changed; tool names or a plugin file name altered."""
    import subprocess
    import reel_stills as RS
    shapes = getattr(RS, "speech_shapes", None) or (lambda t: re.findall(r"\b[0-9A-Fa-f]{6}:|\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b|\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b", t))
    rows = [CC_WHAT_TO_DO, CC_PATCH, *SYNTHETIC, "If you use generated distant scenery or occlusion (DynDOLOD, xLODGen, Occlusion.esp), rebuild it."]
    reel_words = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8")).get("reel") or {}
    fonts = {k: reel_words[k] for k in ("stillFont", "stillFontBold") if reel_words.get(k)}
    pres = {"presentationId": "c1961", "chapters": [], "findings": [], "evidence": [], "settings": {"reel": fonts},
            "reel": {"status": "planned", "chapters": [
                {"id": "R-01", "kind": "verdict", "narration": " ".join(rows),
                 "still": {"pages": [{"layout": "list", "title": "What to do", "rows": list(rows)}]}},
                {"id": "R-02", "kind": "vignette", "narration": SYNTHETIC[3] + " " + CC_PATCH, "frames": []}]}}
    root = tmp / "c1961"
    (root / "bundle").mkdir(parents=True, exist_ok=True)
    (root / "presentation.json").write_text(json.dumps(pres), encoding="utf-8")
    r = subprocess.run([sys.executable, str(HARNESS / "evalreel.py"), "storyboard-packet", "--presentation",
                        str(root / "presentation.json"), "--bundle", str(root / "bundle"), "--out", str(root / "sb.json")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    sb = json.loads((root / "sb.json").read_text(encoding="utf-8")) if (root / "sb.json").is_file() else {}
    said = [s_.get("narration") or "" for s_ in sb.get("scenes") or []]
    bad = [(s_[:60], shapes(s_)) for s_ in said if shapes(s_)]
    joined = " ".join(said)
    check("C1961", r.returncode == 0 and said and not bad and "switches off a placed Farm Interior Fire Light" in joined
          and "Castle Dour" in joined,
          "the storyboard's speech text holds a form key or an editor ID (or lost the placed objects' words)",
          f"exit {r.returncode} {r.stderr[-200:]!r}; bad {bad[:3]}")
    check("C1961", pres["reel"]["chapters"][0]["still"]["pages"][0]["rows"] == rows and "DynDOLOD, xLODGen, Occlusion.esp" in joined,
          "the page's own text, or a tool name, was changed by the speech rule", joined[-200:])
    # the render's speech and subtitles pass the same rule: every narration it uses is spoken_text's
    src = (HARNESS / "evalreel.py").read_text(encoding="utf-8")
    check("C1961", 'narration = reel_stills.spoken_text(" ".join(str(ch.get("narration") or "").split()))' in src
          and 'say = reel_stills.spoken_text(page["say"] or narration)' in src,
          "the render hands speech and subtitles a narration that has not passed the final speech rule")


CASES = {"C1890": c1890_witness_after_pose_miss, "C1891": c1891_witness_after_planner_refusal,
         "C1892": c1892_opaque_keeps_top_down, "C1893": c1893_witness_miss_goes_on,
         "C1894": c1894_station_view_caption, "C1895": c1895_old_pose_refusal_recaptured,
         "C1896": c1896_refused_reason_true, "C1897": c1897_stale_image_key,
         "C1898": c1898_plugin_case_in_capture_requests,
         "C1899": c1899_marked_view_captions,
         "C1930": c1930_media_bundle_frozen_registry, "C1931": c1931_reel_speaks_no_ids,
         "C1932": c1932_cell_by_form_key_and_true_reasons,
         "C1933": c1933_media_bundle_frozen_scope,
         "C1935": c1935_vignette_requests_its_own_pair,
         "C1936": c1936_refusal_reasons_by_table, "C1937": c1937_top_down_note_not_spoken,
         "C1938": c1938_no_overlap_candidate_words, "C1939": c1939_not_drawn_carried,
         "C1960": c1960_not_drawn_without_fields,
         "C1961": c1961_final_speech_has_no_ids}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    ap.add_argument("--keep", default=None, help="keep the work folder here")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="acc-"))
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
