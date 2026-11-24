"""Known answers for the capture follow-ups after item 28 (C1830-C1836).

    python tests/test_capture_follow_ups.py [--only C1830,C1835]

No game and no renderer. The picker cases (C1830, C1831, C1833) run the REAL capture_points.py over
`viewprobe/tools/fake_viewprobe.py`'s toy box scenes, with the scene's `contacts` key on so the stand-in prints each
participant's drawn box as viewprobe's `--contacts-of` does. The reel and report cases (C1832, C1834, C1835) call the
real reel_plan and evalreport on hand-built inputs (test_reel_vignettes' synthetic evaluation, test_capture_fallbacks'
refused capture result). Coordinates and counts are made up; nothing here is evidence about a real cell.

    C1830  item 1: a tall focus whose see-through partner contains it, with a slab 1,800 u up over the ring's spots and
           the navmesh at z 0: the ring stands on the navmesh storey, not on the slab far above a player's eye
    C1831  item 2: a tall focus whose see-through partner's drawn box is flat (no height) and 1,000 u above the floor:
           the floor at z 0 under the focus is still found, and the ring stands there
    C1832  item 3: the reel's reason line for buried clashes says how many camera spots each rests on
    C1833  item 4: the picker's eye-level step says which participant is drawn only by see-through shapes
    C1834  item 4: a buried participant whose partner is see-through is not said to be buried inside that partner
    C1835  item 7: of a group's clashes, the reel pictures the strongest crossing (most triangle pairs), not the first
    C1836  item 7: a clash a capture found buried is said as buried even when its group was pictured before it was tried

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
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
import reporting_data  # noqa: E402
SKILL = reporting_data.DATA
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


# ---------------------------------------------------------------- picker scenes (fake renderer) --

def picker_env():
    import test_capture_points as TCP   # noqa: E402  (the picker driver, its toy-scene helpers and the request bundle)
    return TCP


def ring_floors(fx, tag: str) -> list[float] | None:
    wl = fx.root / f"work-{tag}" / "witness-worklist.json"
    if not wl.is_file():
        return None
    return sorted({float(s["floorZ"]) for s in json.loads(wl.read_text(encoding="utf-8"))["cells"][0]["shots"]})


def c1830_storey_not_from_the_box_top(fx, TCP):
    """A tower 2,000 u tall (the focus: its partner is a see-through cloud whose box contains it, as WRW 708f's cloud
    contains the tower) with a slab at z 1,800-1,820 over every ring spot and the navmesh at z 0. Wrong answer: the ring
    on the slab, 1,700 u above the eye of a player on the navmesh, because the ground was cast down from the box top."""
    tower = TCP.box(fx.cy, [-100, -100, 0], [100, 100, 2000])
    cloud = TCP.box(fx.mg, [-6000, -6000, -2000], [6000, 6000, 8000], alpha=True, physical=False)
    slab = {"id": "000401:Test.esp", "edid": "Walkway", "min": [-500, -500, 1800], "max": [500, 500, 1820]}
    sc = TCP.scene(tower, cloud, slab, shell=False, contacts=True)
    proc, _doc = TCP.pick(fx, sc, "c1830")
    floors = ring_floors(fx, "c1830")
    check("C1830", floors is not None and all(-8 <= f <= 8 for f in floors),
          "the ring stands on a slab far above the navmesh storey (the ground cast from the top of the focus box)",
          f"ring floors {floors}; {TCP.tail(proc)}")


def c1831_flat_partner_box(fx, TCP):
    """A tower 2,000 u tall (the focus) and a see-through partner whose drawn box is flat at z 1,000 and 10,000 u wide
    (an effect card). Wrong answer: the storey searched only around z 1,000, so the floor at z 0 is missed and the
    picker refuses for want of a floor, or the ring stands anywhere but z 0."""
    tower = TCP.box(fx.cy, [-100, -100, 0], [100, 100, 2000])
    card = TCP.box(fx.mg, [-5000, -5000, 1000], [5000, 5000, 1000], alpha=True, physical=False)
    sc = TCP.scene(tower, card, shell=False, contacts=True)
    proc, _doc = TCP.pick(fx, sc, "c1831")
    floors = ring_floors(fx, "c1831")
    check("C1831", floors is not None and all(-8 <= f <= 8 for f in floors),
          "a flat partner box narrowed the storey to a plane and the floor under the focus was missed",
          f"ring floors {floors}; {TCP.tail(proc)}")


def c1833_eye_level_names_see_through(fx, TCP):
    """A crate beside a see-through glow (alpha only). The stand-in draws no coverage, so no station sees the glow and
    the eye-level step refuses. Wrong answer: the eye-level step does not say the glow is drawn only by see-through
    shapes, so a later reader cannot tell a see-through partner from a solid one."""
    crate = TCP.box(fx.cy, [-80, -40, 0], [-10, 40, 60])
    glow = TCP.box(fx.mg, [10, -40, 0], [80, 40, 60], alpha=True, physical=False)
    sc = TCP.scene(crate, glow, contacts=True)
    proc, doc = TCP.pick(fx, sc, "c1833")
    chain = ((doc or {}).get("refusal") or {}).get("chain") or (doc or {}).get("fallbackChain") or []
    eye = next((c for c in chain if c.get("kind") == "eye-level"), {})
    check("C1833", eye.get("allNonOpaque") == [fx.mg],
          "the eye-level step does not list the participant drawn only by see-through shapes",
          f"eye-level step {json.dumps(eye)[:300]}; {TCP.tail(proc)}")


# ---------------------------------------------------------------- reel and report (hand-built inputs) --

def c1832_reel_buried_reason_counts(tmp: Path):
    """Two buried clashes, resting on 8 and on 22 camera spots. Wrong answer: the reel's reason line (the report's
    'About the review reel') says buried with no count of the camera spots either rests on."""
    RV = load("fu_reel_vignettes", HERE / "test_reel_vignettes.py")
    CF = load("fu_capture_fallbacks", HERE / "test_capture_fallbacks.py")
    import reel_plan as RP
    pres, names, masters, search, mods, captures = RV.capture_binding_fixture()
    shipped = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    pol = dict(RV.POLICY, captureAttemptsPerType=1, stillReasons=shipped["stillReasons"],
               stillReasonsCount=shipped.get("stillReasonsCount"))
    keep = [c for c in captures if c["request"] not in ("req-own", "req-cell-two")]
    pres2 = copy.deepcopy(pres)
    pres2["media"] = [m for m in pres["media"] if m["provenance"]["captureRequest"] not in ("req-own", "req-cell-two")]
    a1, b1 = "000101:Target.esp", "000201:Alpha.esp"
    a2, b2 = "000104:Target.esp", "000204:Alpha.esp"
    r1 = CF.buried_result({a1: 8, b1: 0}, 8, b1, keys=(a1, b1))
    r2 = dict(CF.buried_result({a2: 22, b2: 0}, 22, b2, keys=(a2, b2)), request="req-buried-2")
    reel = RP.plan_reel(copy.deepcopy(pres2), names, masters, pol, RV.binding_words(), search, mods, keep + [r1, r2],
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    lines = " ".join(str(x) for x in reel.get("reportLines") or [])
    said = [s for s in lines.replace("? ", ". ").split(". ") if "Buried" in s or "buried" in s]
    sent = " ".join(said)
    check("C1832", bool(said) and "camera spot" in sent and "8" in sent and "22" in sent,
          "the reel's buried reason does not say how many camera spots the buried clashes rest on (8 and 22)",
          f"buried reason: {sent[:300]!r}")


def c1834_buried_partner_see_through(tmp: Path):
    """WRW fb17's shape: Ryn's rock no station sees (0 of 22), partnered with a cloud the renderer draws only by
    see-through shapes. Wrong answer: the report says the rock is buried inside the cloud."""
    CF = load("fu_capture_fallbacks2", HERE / "test_capture_fallbacks.py")
    import evalreport as ER
    rock, cloud = "000802:Ryn.esp", "000989:WRW.esp"
    x = CF.buried_result({rock: 0, cloud: 22}, 22, cloud, keys=(rock, cloud))
    eye = next(c for c in x["refusal"]["chain"] if c["kind"] == "eye-level")
    eye["allNonOpaque"] = [cloud]                     # as the picker states it (C1833)
    words = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))["reader"]["figureCaptions"]
    plan = {"reader": {"prediction": {"record": [{"findingId": "F-1", "formKey": rock, "other": [cloud]}],
                                      "items": [{"findingIds": ["F-1"]}]}}}
    names = {"refs": {rock: {"base": "r"}, cloud: {"base": "c"}},
             "bases": {"r": {"editorId": "RockCliff01"}, "c": {"editorId": "CloudFog01"}},
             "objects": {"RockCliff01": {"name": "Rock"}, "CloudFog01": {"name": "Cloud"}}}
    ER.buried_lines(plan, [x], names, words)
    line = " ".join(plan["reader"]["prediction"]["items"][0].get("buriedLines") or [])
    check("C1834", "Rock" in line and "buried" in line and "22 camera spots" in line and "inside Cloud" not in line,
          "a buried object is said to be buried inside a see-through partner (or the line lost its count)",
          f"line {line!r}")


def c1835_strongest_crossing_pictured(tmp: Path):
    """One group (one mod pair, one cell) holds three crossings: 000101 against 000201 (1 triangle pair), 000101 against
    000209 (900) and 000102 against 000202 (9). Wrong answer: the reel's vignette for the group is the first clash in
    record order (the 1-pair crossing), not the strongest (WRW: the 2-pair rubble shown while the tower's 676-pair
    crossing with the lighthouse, also captured, went unpictured)."""
    RV = load("fu_reel_vignettes2", HERE / "test_reel_vignettes.py")
    import reel_plan as RP
    pres, names, masters, search, mods = RV.synthetic()
    rec = pres["reader"]["prediction"]["record"]
    first = next(r for r in rec if r.get("formKey") == "000101:Target.esp")
    first["summary"] = first["summary"].replace("9 triangle pairs", "1 triangle pairs")
    strong = copy.deepcopy(first)
    strong["other"] = ["000209:Alpha.esp"]
    strong["summary"] = first["summary"].replace("1 triangle pairs", "900 triangle pairs").replace("000201:Alpha.esp",
                                                                                                   "000209:Alpha.esp")
    rec.insert(rec.index(first) + 1, strong)
    names["refs"]["000209:Alpha.esp"] = {"base": "B2", "carriedBy": ["Alpha.esp"]}
    shipped = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    pol = dict(RV.POLICY, strengthPattern=shipped.get("strengthPattern"))
    reel = RP.plan_reel(copy.deepcopy(pres), names, masters, pol, RV.binding_words(), search, mods, [],
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=False)
    vig = [ch.get("clash") or {} for ch in reel.get("chapters") or [] if ch.get("kind") == "vignette"
           and (ch.get("clash") or {}).get("type") == "overlap"]
    cell_one = [c for c in vig if c.get("formKey") in ("000101:Target.esp", "000102:Target.esp")]
    check("C1835", len(cell_one) == 1 and cell_one[0].get("other") == ["000209:Alpha.esp"],
          "the reel pictures the group's first clash in record order, not its strongest crossing (900 triangle pairs)",
          f"CellOne vignettes {[(c.get('formKey'), c.get('other')) for c in cell_one]}")


def c1836_untried_buried_said(tmp: Path):
    """CellOne's group: 000101 x 000201 is captured and pictured; 000102 x 000202 was refused as buried (0 of 9 stations
    see 000202) but is never tried, because its group is already pictured (item 7 puts the strongest first, so this is
    the usual order). Wrong answer: the buried clash counted as represented by the other member's picture, so the reel
    says nothing of it being buried."""
    RV = load("fu_reel_vignettes3", HERE / "test_reel_vignettes.py")
    CF = load("fu_capture_fallbacks3", HERE / "test_capture_fallbacks.py")
    import reel_plan as RP
    pres, names, masters, search, mods, captures = RV.capture_binding_fixture()
    shipped = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))["vignettes"]
    pol = dict(RV.POLICY, stillReasons=shipped["stillReasons"], stillReasonsCount=shipped.get("stillReasonsCount"))
    a2, b2 = "000102:Target.esp", "000202:Alpha.esp"
    buried = dict(CF.buried_result({a2: 9, b2: 0}, 9, b2, keys=(a2, b2)), request="req-buried-102")
    reel = RP.plan_reel(copy.deepcopy(pres), names, masters, pol, RV.binding_words(), search, mods, captures + [buried],
                        markers=RV.MARKERS, switches=RV.SWITCHES, captures_final=True)
    un = [c for ch in reel.get("chapters") or [] if ch.get("kind") == "unpictured" for c in ch.get("clashes") or []]
    row = next((c for c in un if c.get("formKey") == a2), None)
    check("C1836", row is not None and row.get("reason") == "buried",
          "a clash a capture found buried is counted as represented by its group's picture instead of said as buried",
          f"unpictured rows for 000102: {row}; accounting {json.dumps(reel.get('accounting'))[:300]}")


PICKER_CASES = {"C1830": c1830_storey_not_from_the_box_top, "C1831": c1831_flat_partner_box,
                "C1833": c1833_eye_level_names_see_through}
CODE_CASES = {"C1832": c1832_reel_buried_reason_counts, "C1834": c1834_buried_partner_see_through,
              "C1835": c1835_strongest_crossing_pictured, "C1836": c1836_untried_buried_said}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    ap.add_argument("--keep", default=None, help="keep the work folder here")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="cfu-"))
    root.mkdir(parents=True, exist_ok=True)
    fx = TCP = None
    for cid, fn in {**PICKER_CASES, **CODE_CASES}.items():
        if only and cid not in only:
            continue
        try:
            if cid in PICKER_CASES:
                if fx is None:
                    TCP = picker_env()
                    fx = TCP.Fixture(root / "picker")
                fn(fx, TCP)
            else:
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
