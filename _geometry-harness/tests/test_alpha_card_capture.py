"""Known answers for the interim alpha-card capture rule and the stale image key (C1890-C1898).

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

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import os
import sys
import tempfile
import traceback
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
SKILL = HARNESS.parent / ".claude" / "skills" / "evaluation-reporting" / "references"
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


CASES = {"C1890": c1890_witness_after_pose_miss, "C1891": c1891_witness_after_planner_refusal,
         "C1892": c1892_opaque_keeps_top_down, "C1893": c1893_witness_miss_goes_on,
         "C1894": c1894_station_view_caption, "C1895": c1895_old_pose_refusal_recaptured,
         "C1896": c1896_refused_reason_true, "C1897": c1897_stale_image_key,
         "C1898": c1898_plugin_case_in_capture_requests}


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
