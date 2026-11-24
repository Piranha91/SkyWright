"""Known answers for a capture camera checked in every state it renders (C2168-C2172).

    python _geometry-harness/tests/test_capture_every_state.py [--only C2168]

BUVARP's F-322 vignette (round 2, 2026-11-13): the camera was planned and verified in the setup with the mod, and the
same camera rendered the setup without it, where a pine the mod moves away stood at the eye; the "without" frame was
snowy needles at point-blank range. A vignette's camera now passes the measured state's camera checks (the planner's
inside test at the eye, and each participant's sightline where the setup places that participant) in every other state
it renders; a camera that fails one tries the next pose of the chain (the witness station's camera, then the top-down)
and the capture is refused, naming the state and the object, when none passes.

Drives the real capture_acquire with its renderer answered per setup (the order file the probe names) in viewprobe's
own `inside-at` and `pick` line formats, judged by the real shotplan.

    C2168  the camera is blocked in the setup without the mod (a tree's box holds the eye and the plant's sightline stops
           at 120 u of 183): state_checks refuses with state-camera-blocked, naming the state and the tree; the
           composed pose's refusal goes to the witness station's camera, kept only when it is clear without the mod too,
           else on to the top-down, and a top-down blocked there ends the chain refused; acquire and the pipeline pass
           the setup without the mod (--state-order) for a vignette; the refusal has its own reel reason.
    C2169  guard: a camera clear in every state keeps its single camera, records the check per state, and does not test
           a participant the setup does not place; with no other state named nothing is rendered.
    C2170  the other state's check addresses the cell by its form key (a mod may give a base-game cell an editor ID the
           setup without it does not know; INIGO's LangleyWalkway1), as capture renders every state (C1932).
    C2171  guard: a setup that lacks the plugin defining the cell is not rendered; capture's own final refusal,
           place-only-with-mod, names the cell and the setup.
    C2172  with no form key known for the cell, no other setup is rendered by an editor ID; the check refuses and says so.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import tempfile
import traceback
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
SKILL = SW.CHECKOUT / "external" / "houseCARL" / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(SW.SCRIPTS))

FAILED: list[str] = []
HOUSE, PLANT, TREE = "00AA8C:House.esp", "0D2ED9:Skyrim.esm", "028CE2:Skyrim.esm"
FP = "ab" * 32
EYE = [142142.0, 27635.0, -12932.0]
CAMERA = {"cell": "BrandyMugFarmExterior", "eye": EYE, "yaw": 205.1, "pitch": 2.2, "fov": 65.0, "aspect": 1.7778,
          "viewKind": "eye-level", "viewFlags": [],
          "viewprobe": "--eye 142142.0,27635.0,-12932.0 --yaw 205.1 --pitch 2.2 --fov 65.0 --aspect 1.7778"}
PARTICIPANTS = [{"formKey": HOUSE, "color": "cyan", "label": "cyan-face", "markKind": "surface",
                 "point": [142141.0, 27507.0, -12903.0]},
                {"formKey": PLANT, "color": "magenta", "label": "magenta-face", "markKind": "coverage",
                 "point": [142066.0, 27473.0, -12993.0]}]
TARGETS = [{"label": "magenta-face", "kind": "nodepth", "point": [142066.0, 27473.0, -12893.0]},
           {"label": "cyan-face", "kind": "surface", "point": [142141.0, 27507.0, -12903.0]}]
COVER = "nominated by mesh bounds, 0 by record OBND alone (mesh unreadable), 0 physical ref(s) whose mesh could not be read at all"


def check(case, ok, wrong, detail=""):
    print(("  GREEN " if ok else "  RED   ") + f"{case}: " + ("" if ok else f"{wrong} ({detail})"))
    if not ok:
        FAILED.append(case)
    return ok


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


CELL_LINE = "cell BrandyMugFarmExterior (exterior)  00B4FB:Skyrim.esm"


def seed_cell(receipts: Path, line: str = CELL_LINE) -> Path:
    """An acquisition's receipts as the picker leaves them: its placement probe's console names the cell and its form
    key (the line acquisition_cell_form_key reads)."""
    d = receipts / "points-work" / "p0-placements"
    d.mkdir(parents=True, exist_ok=True)
    (d / "console.txt").write_text(line + "\n", encoding="utf-8")
    return receipts


def orders(tmp: Path) -> dict:
    tmp.mkdir(parents=True, exist_ok=True)
    w, wo = tmp / "with.json", tmp / "without.json"
    w.write_text(json.dumps({"Plugins": ["Skyrim.esm", "Update.esm", "House.esp"]}), encoding="utf-8")
    wo.write_text(json.dumps({"Plugins": ["Skyrim.esm", "Update.esm"]}), encoding="utf-8")
    return {"with": str(w), "without": str(wo)}


def probe_text(flags, blocked: bool) -> str:
    """viewprobe's answer to a state probe: the inside-at block (a two-sided tree whose box holds the eye, open on four
    axes) and one pick line per --pick, 120 u when blocked, 900 u when the sightline is clear."""
    i = flags.index("--inside-at")
    x, y, z = flags[i + 1].split(",")
    lines = []
    if blocked:
        lines += [f"   inside-at {x},{y},{z}: 1 ref(s) whose box encloses it (smallest first); {COVER}",
                  f"      {TREE}  Tree  TreePineForestSnow03  dims 558x588x1082 u  at 142075,27469,-13012  scale 1.14  "
                  "rays +x - -x 148 +y - -y 100 up - down -  facing +x - -x two +y - -y two up - down -  "
                  "[mesh bounds; record box agrees]  mesh Landscape\\Trees\\TreePineForestSnow03.nif"]
    else:
        lines += [f"   inside-at {x},{y},{z}: no physical ref's box encloses it; {COVER}"]
    picks = [flags[k + 1] for k, f in enumerate(flags) if f == "--pick"]
    for p in picks:
        d = 120.0 if blocked else 900.0
        lines.append(f"   pick {p}: 142100.0,27560.0,-12930.0  cell (BrandyMugFarmExterior)  {d:.1f} u from the eye")
    lines.append(f"   capture input configuration: {FP}")
    return "\n".join(lines) + "\n"


def env(blocked_states=(), witness_blocked=False, top_down_blocked=False):
    """capture_acquire with its renderer answered per call: a state probe (--inside-at) is blocked when its setup's
    order is one of `blocked_states` (for the witness camera when witness_blocked; for the top-down when
    top_down_blocked); a coverage probe answers in the mark."""
    CA = load(f"ces_{abs(hash((tuple(blocked_states), witness_blocked, top_down_blocked))) % 10 ** 8}",
              HARNESS / "capture_acquire.py")
    calls = []

    def fake_vp(a, shotplan_mod, cell, flags, out):
        flags = [str(f) for f in flags]
        order = next((a.passthrough[k + 1] for k, f in enumerate(a.passthrough) if f == "--order"), "")
        calls.append({"out": str(out), "flags": flags, "order": order})
        if "--inside-at" in flags:
            name = Path(order).stem
            tag = Path(str(out)).parent.name
            blocked = name in blocked_states and (
                ("witness" in str(out) and witness_blocked) or ("top-down" in str(out) and top_down_blocked)
                or ("witness" not in str(out) and "top-down" not in str(out)))
            return 0, probe_text(flags, blocked)
        return 0, f"coverage-at {PLANT} 142066,27473,-12993: pixel 160,95 in the mark, 6 px inside\n" \
                  f"   capture input configuration: {FP}\n"
    CA._vp_run = fake_vp
    return CA, calls


def a_ns(state_orders: dict, with_order: str):
    return types.SimpleNamespace(vp=None, passthrough=["--instance", "lab", "--order", with_order],
                                 state_order=[f"{k}={v}" for k, v in state_orders.items()])


def result_fixture():
    return {"camera": dict(CAMERA), "participants": [dict(p) for p in PARTICIPANTS], "viewKind": "eye-level",
            "verification": {"targets": [dict(t) for t in TARGETS]}}


def points_fixture():
    eye_step = {"kind": "eye-level", "status": "ok", "code": None, "detail": "station s25 (0 deg, 400 u)",
                "allNonOpaque": [PLANT]}
    doc = {"schemaVersion": 1, "requestId": "req-f322", "cell": "BrandyMugFarmExterior", "floorZ": -13052.0,
           "viewKind": "eye-level", "viewFlags": [], "fallbackChain": [eye_step],
           "topDown": {"cell": "BrandyMugFarmExterior", "floorZ": -13052.0, "subjects": [], "seeThrough": [PLANT],
                       "allNonOpaque": [PLANT]},
           "points": [{"formKey": HOUSE, "point": PARTICIPANTS[0]["point"], "markKind": "surface"},
                      {"formKey": PLANT, "point": PARTICIPANTS[1]["point"], "markKind": "coverage",
                       "coverage": {"alphaMin": 0.25, "alpha": 1.0, "pixels": 2185}}],
           "camera": {"fov": 65.0, "aspect": "16:9"},
           "witness": {"station": {"id": "s25", "bearing": 0.0, "standoff": 400.0, "eye": [142066.0, 27873.0, -12932.0],
                                   "look": [142066.0, 27473.0, -12993.0], "fov": 65.0, "size": 320}}}
    by_key = {p["formKey"]: {**p, "label": "face"} for p in doc["points"]}
    return {"doc": doc, "byKey": by_key, "path": "points.json", "sha256": "0" * 64, "camera": doc["camera"]}


def request_fixture():
    return {"id": "req-f322", "requestType": "overlap-pair", "checkId": "geometry.object-overlap", "armId": "with",
            "recordKeys": [HOUSE, PLANT], "configurationFingerprint": FP,
            "sequence": {"states": ["without", "with", "highlight"]}}


def c2168_blocked_state_refused_or_reposed(tmp: Path):
    o = orders(tmp / "c2168")
    CA, calls = env(blocked_states=("without",))
    S = CA._load_shotplan()
    fn = getattr(CA, "state_checks", None)
    if not check("C2168", fn is not None, "capture_acquire checks the published camera in no state but the measured one"):
        return
    try:
        fn(a_ns({"without": o["without"]}, o["with"]), result_fixture(), seed_cell(tmp / "c2168" / "r"), S)
        got = None
    except CA.Refused as e:
        got = e
    detail = getattr(got, "detail", "")
    check("C2168", got is not None and got.code == "state-camera-blocked" and got.extra.get("state") == "without"
          and TREE in detail and "TreePineForestSnow03" in detail and "without" in detail,
          "a camera blocked by a tree in the setup without the mod was kept, or its refusal does not name the state and the "
          "tree", f"{getattr(got, 'code', None)}: {detail[:300]}")
    probe = next((c for c in calls if "--inside-at" in c["flags"]), {})
    check("C2168", probe.get("order") == o["without"] and probe["flags"].count("--pick") == 1,
          "the state probe did not render the setup without the mod, or tested a participant that setup does not place",
          json.dumps(probe)[:300])
    # the chain: the composed pose's refusal goes to the witness camera, kept only when clear without the mod too
    pexc = CA.Refused("state-camera-blocked", "in the without setup the camera stands in a tree", final=False,
                      state="without")
    CA2, calls2 = env(blocked_states=("without",), witness_blocked=False)

    def no_top_down(*_a, **_k):
        raise CA2.Refused("top-down-not-expected", "the witness camera was clear without the mod")
    CA2.acquire_top_down = no_top_down
    try:
        kind, res = CA2.after_pose_refusal(a_ns({"without": o["without"]}, o["with"]), request_fixture(), PARTICIPANTS,
                                           points_fixture(), {}, seed_cell(tmp / "c2168" / "w1"), CA2._load_shotplan(), pexc)
    except CA2.Refused as e:
        kind, res = "refused:" + e.code, None
    check("C2168", kind == "witness" and ((res or {}).get("stateChecks") or [{}])[0].get("state") == "without",
          "a composed pose refused for the setup without the mod did not try the witness station's camera, or kept it "
          "without checking that setup", f"{kind}; {json.dumps((res or {}).get('stateChecks'))[:200]}")
    CA3, calls3 = env(blocked_states=("without",), witness_blocked=True)
    handoffs = []

    def fake_top_down(a, request, parts, source, receipts_dir, shotplan_mod, exc):
        handoffs.append(exc.extra)
        raise CA3.Refused("state-camera-blocked", "the top-down is blocked too", final=True,
                          chain=list(exc.extra.get("chain") or []))
    CA3.acquire_top_down = fake_top_down
    try:
        kind3, _r3 = CA3.after_pose_refusal(a_ns({"without": o["without"]}, o["with"]), request_fixture(), PARTICIPANTS,
                                            points_fixture(), {}, seed_cell(tmp / "c2168" / "w2"), CA3._load_shotplan(), pexc)
    except CA3.Refused as e:
        kind3 = "refused:" + e.code
    wit = next((c for c in (handoffs[-1].get("chain") if handoffs else []) if c.get("kind") == "witness"), {})
    check("C2168", kind3 == "refused:state-camera-blocked" and wit.get("code") == "state-camera-blocked",
          "a witness camera blocked without the mod was kept, or the chain did not go on to the top-down with its reason",
          f"{kind3}; witness step {wit}")
    # the top-down itself is checked in every state too, and a blocked one ends the chain, final
    src = (HARNESS / "capture_acquire.py").read_text(encoding="utf-8")
    pipe = (HARNESS / "evalpipeline.py").read_text(encoding="utf-8")
    check("C2168", 'state_checks(a, res, receipts_dir, shotplan_mod, tag="top-down")' in src
          and "state_checks(a, result, receipts_dir, shotplan_mod)" in src and '"--state-order"' in src
          and '"--state-order", f"without={cands[row[\'without\']]}"' in pipe,
          "the composed pose or the top-down is published unchecked in other states, or the pipeline does not name the "
          "setup without the mod")
    pol = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    key = (pol.get("refusalReasons") or {}).get("state-camera-blocked")
    import evalpipeline as EP
    check("C2168", key and key in pol.get("stillReasons", {}) and key in pol.get("stillReasonsShort", {})
          and "state-camera-blocked" in EP.FINAL_REFUSALS,
          "the refusal has no reason of its own in the reel's table, or a finished one is captured again every round",
          f"{key}")
    # F-322's real chain: the pose and the witness camera blocked without the mod, the top-down short from above. The
    # reel says the camera is blocked without the mod, not "seen but not kept" or "no camera could see" (the real
    # trial on BUVARP, 2026-11-13)
    import reel_plan as RP
    x = {"status": "refused", "formKey": HOUSE, "recordKeys": [HOUSE, PLANT],
         "refusal": {"code": "top-down-participant-unseen", "final": True, "chain": [
             {"kind": "eye-level", "status": "refused", "code": "state-camera-blocked"},
             {"kind": "cutaway", "status": "skipped", "code": "cutaway-not-applicable"},
             {"kind": "witness", "status": "refused", "code": "state-camera-blocked"},
             {"kind": "top-down", "status": "refused", "code": "top-down-participant-unseen"}]}}
    why = RP.unpictured_refused_reason([HOUSE, PLANT], [x], set(), pol.get("refusalReasons"))
    check("C2168", why == "stateBlocked", "a clash whose cameras were blocked without the mod is said by another reason",
          why)


def c2169_clear_states_keep_one_camera(tmp: Path):
    o = orders(tmp / "c2169")
    CA, calls = env(blocked_states=())
    S = CA._load_shotplan()
    fn = getattr(CA, "state_checks", None)
    res = result_fixture()
    try:
        rows = fn(a_ns({"without": o["without"]}, o["with"]), res, seed_cell(tmp / "c2169" / "r"), S) if fn else None
        err = None
    except Exception as e:  # a clear state must never refuse
        rows, err = None, e
    probe = next((c for c in calls if "--inside-at" in c["flags"]), {})
    check("C2169", rows is not None and err is None and res.get("camera") == CAMERA
          and (res.get("stateChecks") or [{}])[0].get("state") == "without"
          and (res.get("stateChecks") or [{}])[0].get("targets") == ["magenta-face"]
          and probe.get("order") == o["without"],
          "a camera clear in every state was refused, moved, or not recorded per state (or tested a participant the setup "
          "does not place)", f"{err!r}; {json.dumps(res.get('stateChecks'))[:300]}")
    n = len(calls)
    rows2 = fn(types.SimpleNamespace(vp=None, passthrough=["--order", o["with"]], state_order=[]), result_fixture(),
               tmp / "c2169" / "r2", S) if fn else None
    check("C2169", rows2 == [] and len(calls) == n,
          "with no other state named, a state probe was rendered anyway", f"{rows2}; renders {len(calls) - n}")


def cell_env(tmp: Path, cell_line: str):
    """capture_acquire whose renderer knows a cell only by the names the setup gives it: the base-game form key always,
    the editor ID only in the setup with the mod that names it (viewprobe's own refusal otherwise)."""
    CA = load(f"ces_cell_{abs(hash(cell_line)) % 10 ** 8}", HARNESS / "capture_acquire.py")
    calls = []
    rec = tmp / "receipts"
    (rec / "points-work" / "p0-placements").mkdir(parents=True, exist_ok=True)
    (rec / "points-work" / "p0-placements" / "console.txt").write_text(cell_line + "\n", encoding="utf-8")

    def fake_vp(a, shotplan_mod, cell, flags, out):
        flags = [str(f) for f in flags]
        order = next((a.passthrough[k + 1] for k, f in enumerate(a.passthrough) if f == "--order"), "")
        calls.append({"cell": cell, "order": order})
        if not str(cell)[:6].isalnum() or ":" not in str(cell):
            if "without" in Path(order).stem:
                return 1, f"--stderr--\ncell '{cell}' not found in the active load order.\n"
        return 0, probe_text(flags, False)
    CA._vp_run = fake_vp
    return CA, calls, rec


def c2170_state_cell_by_form_key(tmp: Path):
    """INIGO (2026-11-14): its F-request's state check rendered the setup without INIGO by the cell's editor ID
    LangleyWalkway1, which only INIGO gives 008FC3:Skyrim.esm; viewprobe refused the cell and the capture was refused as a
    renderer failure. Wrong answers: the editor ID handed to the setup without the mod; a refusal for a cell that setup
    has under its form key."""
    o = orders(tmp / "c2170")
    CA, calls, rec = cell_env(tmp / "c2170", "cell LangleyWalkway1 (exterior)  008FC3:Skyrim.esm")
    res = result_fixture()
    res["camera"]["cell"] = "LangleyWalkway1"
    try:
        rows = CA.state_checks(a_ns({"without": o["without"]}, o["with"]), res, rec, CA._load_shotplan())
        err = None
    except CA.Refused as e:
        rows, err = None, e
    probe = next((c for c in calls if "without" in Path(c["order"]).stem), {})
    check("C2170", err is None and probe.get("cell") == "008FC3:Skyrim.esm"
          and (rows or [{}])[0].get("cell") == "008FC3:Skyrim.esm",
          "the setup without the mod was rendered by an editor ID only the mod gives the cell, or refused",
          f"{getattr(err, 'code', None)}: {getattr(err, 'detail', '')[:200]}; calls {calls}")


def c2171_place_only_with_mod(tmp: Path):
    """Guard: the cell is defined by the mod itself, so the setup without it has no such place. Wrong answers: a render
    of that setup at all; any refusal but capture's own final place-only-with-mod naming the cell and the setup."""
    o = orders(tmp / "c2171")
    CA, calls, rec = cell_env(tmp / "c2171", "cell HouseInterior (interior)  000D62:House.esp")
    res = result_fixture()
    res["camera"]["cell"] = "HouseInterior"
    try:
        CA.state_checks(a_ns({"without": o["without"]}, o["with"]), res, rec, CA._load_shotplan())
        err = None
    except CA.Refused as e:
        err = e
    check("C2171", err is not None and err.code == "place-only-with-mod" and err.extra.get("final") is True
          and err.extra.get("cell") == "000D62:House.esp" and err.extra.get("states") == ["without"] and not calls,
          "a setup that lacks the plugin defining the cell was rendered, or refused for another reason",
          f"{getattr(err, 'code', None)} {getattr(err, 'extra', None)}; renders {len(calls)}")


def c2172_no_form_key_no_editor_id_render(tmp: Path):
    """No form key is known for the cell (the camera carries none and the receipts name none). Wrong answers: the other
    setup rendered by the editor ID, which only the measured setup is known to give the cell (INIGO's failure, made
    silent); a refusal that does not say why."""
    o = orders(tmp / "c2172")
    CA, calls = env(blocked_states=())
    rec = tmp / "c2172" / "receipts"
    rec.mkdir(parents=True, exist_ok=True)
    try:
        CA.state_checks(a_ns({"without": o["without"]}, o["with"]), result_fixture(), rec, CA._load_shotplan())
        err = None
    except CA.Refused as e:
        err = e
    check("C2172", err is not None and err.code == "points-renderer-failed" and "no cell form key" in err.detail
          and "without" in err.detail and not calls,
          "the setup without the mod was rendered by an editor ID when no form key was known, or refused without saying why",
          f"{getattr(err, 'code', None)}: {getattr(err, 'detail', '')[:200]}; renders {len(calls)}")


CASES = {"C2168": c2168_blocked_state_refused_or_reposed, "C2169": c2169_clear_states_keep_one_camera,
         "C2170": c2170_state_cell_by_form_key, "C2171": c2171_place_only_with_mod,
         "C2172": c2172_no_form_key_no_editor_id_render}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    with tempfile.TemporaryDirectory(prefix="every-state-") as td:
        for cid, fn in CASES.items():
            if only and cid not in only:
                continue
            try:
                fn(Path(td))
            except Exception as e:  # a case that cannot run is red, never skipped
                print(f"  CRASH {cid}: {type(e).__name__}: {e}")
                traceback.print_exc()
                FAILED.append(cid)
    print("ALL GREEN" if not FAILED else f"RED: {sorted(set(FAILED))}")
    return 0 if not FAILED else 1


if __name__ == "__main__":
    sys.exit(main())
