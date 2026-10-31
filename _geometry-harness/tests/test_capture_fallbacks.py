"""Known answers for the capture fallbacks: cutaway, top-down and see-through marks (CF1-CF23).

    python tests/test_capture_fallbacks.py [--vp VIEWPROBE.exe] [--only CF1,CF2] [--keep DIR]

Two kinds of case.

RENDER CASES (CF1-CF12) run the REAL viewprobe (--vp; default the in-tree production build), the REAL capture_points,
capture_acquire and shotplan on CfFixture.esp (viewprobe/CaptureFixture.cs), laid out as an MO2 instance over a Skyrim SE
game's own archives (tests/capture_fixture_lab.py). The game folder is HOUSECARL_FIXTURE_GAME or `fixtureGame` in the
harness config. Without it these cases are NOT RUN, the run says so and exits 3: never a pass.

CODE CASES (CF20-CF23) call the real evalpipeline, reel_plan, capture_acquire and evalreport functions on hand-built
inputs: the reuse rule, the reel's unmarkable skip, the top-down acceptance count and the view-kind caption words.

Each case names the wrong answer it exists to reject. Exit 0: every case that ran passed and none was skipped; 1: a
case failed; 3: render cases not run.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"
import capture_fixture_lab as LAB  # noqa: E402

PROD_VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"

# Frozen at Gate B (2026-10-31): the top-down acceptance count. A participant must show at least this many of its own
# marked pixels (--ref-surface colour or --coverage-of mask) in the top-down probe frame, 320 px wide at the request's
# aspect; fewer and the top-down refuses for it.
TOP_DOWN_MIN_PIXELS = 25

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    """`wrong` names the wrong answer the assertion rejects; it is what a red case prints."""
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", f"{wrong}" + (f" -- {detail}" if detail else "")))
    return cond


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def run(argv, **kw):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)


# ---------------------------------------------------------------------------------------------------------------------
# the render rig: the fixture instance, one request per cell, the real acquire

class Rig:
    def __init__(self, root: Path, vp: Path, game: Path):
        self.root, self.vp = root, vp
        self.lab = LAB.Lab(root / "lab", vp, game)
        self._fp = {}
        self.fingerprint = self.fingerprint_of(self.lab.cand)

    def fingerprint_of(self, order: Path) -> str:
        """The renderer's own configuration fingerprint for a setup: a request must carry it (capture_acquire gate)."""
        if order not in self._fp:
            out = self.root / "fingerprint" / Path(order).stem
            out.mkdir(parents=True, exist_ok=True)
            cmd = [str(self.vp), "CfGlow", "--eye", "0,-300,120", "--look", "0,0,40", "--size", "32", "--msaa", "0", "--nav",
                   "off", "--out", str(out), *self.lab.target(order)]
            r = run(cmd)
            m = re.search(r"capture input configuration: ([0-9a-f]{64})", r.stdout)
            if not m:
                raise RuntimeError(f"no configuration line from the fixture render (exit {r.returncode}): {r.stderr[-400:]}")
            self._fp[order] = m.group(1)
        return self._fp[order]

    def bundle(self, cell: str, a: str, b: str, fingerprint: str | None = None, plugins=None) -> tuple[Path, str]:
        """A one-finding bundle and storyboard packet whose single overlap-pair request names A (cyan) and B (magenta)."""
        import report_scenes
        root = self.root / "bundles" / cell
        root.mkdir(parents=True, exist_ok=True)
        emit = root / "overlap-emit.json"
        emit.write_text(json.dumps({"Probe": "overlapprobe", "State": "baseline",
                                    "Configuration": {"Fingerprint": fingerprint or self.fingerprint,
                                                      "Plugins": [{"Name": n} for n in (plugins or [LAB.PLUGIN])]},
                                    "Conflicts": [{"Kind": "OVERLAP", "A": LAB.PLUGIN, "B": LAB.PLUGIN,
                                                   "Necessary": [{"FormKey": a, "Label": f"{cell} (int)", "Physical": "Holds",
                                                                  "PhysicalDetail": "fixture"}],
                                                   "Alternative": [{"FormKey": b}]}]}, indent=1), encoding="utf-8")
        pres = {"presentationId": f"cf-{cell}", "chapters": [], "source": {"bundleDir": str(root)},
                "findings": [{"id": "F-cf", "kind": "finding", "checkId": "geometry.object-overlap", "armId": "baseline",
                              "part": "arm", "counts": {"A": LAB.PLUGIN, "B": LAB.PLUGIN}, "allRows": [{"formKey": a}],
                              "evidenceIds": ["E-cf"]}],
                "evidence": [{"id": "E-cf", "kind": "emit", "part": "arm", "armId": "baseline", "path": emit.name,
                              "sha256": sha(emit)}]}
        plan = report_scenes.capture_requests(pres, root)
        if len(plan["requests"]) != 1:
            raise RuntimeError(f"the selector made {len(plan['requests'])} requests for {cell}")
        req = plan["requests"][0]
        # the selector highlights the finding's own row first; pin the colours to A cyan, B magenta for the cases
        req["highlights"] = [{"formKey": a, "color": "cyan"}, {"formKey": b, "color": "magenta"}]
        req["recordKeys"] = [a, b]
        # the vignette the capture step renders: without the evaluated mod, with it, and with both marked
        req["vignette"] = {"chapterId": f"cf-{cell}", "withoutArmId": "without", "states": ["without", "with", "highlight"]}
        req["sequence"] = {**(req.get("sequence") or {}), "states": ["without", "with", "highlight"],
                           "setups": {"without": "without", "with": "baseline", "highlight": "baseline"}}
        packet = root / "storyboard-packet.json"
        packet.write_text(json.dumps({"schemaVersion": 1, "presentationId": pres["presentationId"], "scenes": [],
                                      "capturePlan": plan}, indent=1), encoding="utf-8")
        return packet, req["id"]

    def acquire(self, cell: str, a: str | None = None, b: str | None = None, order: Path | None = None) -> dict:
        i = LAB.ids(cell)
        order = order or self.lab.cand
        packet, rid = self.bundle(cell, a or i["A"], b or i["B"], self.fingerprint_of(order),
                                  [LAB.PLUGIN] + ([LAB.PLUGIN_MOD] if order == self.lab.cand_mod else []))
        out = self.root / "acq" / cell
        out.mkdir(parents=True, exist_ok=True)
        r = run([sys.executable, HARNESS / "capture_acquire.py", "acquire", "--packet", packet, "--request", rid,
                 "--points", "auto", "--bundle", packet.parent, "--out", out / "acquisition.json",
                 "--receipts", out / "receipts", "--vp", Path(self.vp).as_posix(), "--", *self.lab.target(order)])
        # (--vp is shlex-split by shotplan: a backslashed Windows path would lose its separators)
        (out / "acquire-console.txt").write_text(r.stdout + "\n--stderr--\n" + r.stderr, encoding="utf-8")
        doc = json.loads((out / "acquisition.json").read_text(encoding="utf-8")) if (out / "acquisition.json").is_file() else {}
        doc["_exit"], doc["_dir"], doc["_tail"] = r.returncode, str(out), (r.stderr or r.stdout)[-600:]
        doc["_packet"], doc["_rid"] = str(packet), rid
        return doc


def refusal_text(doc: dict) -> str:
    ref = doc.get("refusal") or {}
    return f"{doc.get('status')} {ref.get('pickerCode') or ref.get('code')}: {str(ref.get('detail'))[:300]}"


def hidden_keys(doc: dict) -> list[str]:
    return [h.get("formKey") for h in ((doc.get("cutaway") or {}).get("hidden") or [])]


# ---------------------------------------------------------------------------------------------------------------------
# render cases

def cf1_cutaway(rig: Rig):
    """A pair walled in on four sides: no eye-level station sees it, so the cutaway hides the wall(s) in the way and keeps
    the pair and the floor it rests on. Wrong answers: a refusal, or a camera with a participant or the floor hidden."""
    i = LAB.ids("CfCutaway")
    doc = rig.acquire("CfCutaway")
    floor = set(i["floor"])
    if not check("CF1", doc.get("status") == "camera-ready", "the walled-in pair was refused instead of cut away",
                 refusal_text(doc)):
        return
    check("CF1", doc.get("viewKind") == "cutaway", "the camera is not labelled a cutaway", str(doc.get("viewKind")))
    hid = hidden_keys(doc)
    check("CF1", bool(hid) and set(hid) <= set(i["allWalls"]), "the hidden list is empty or names something other than a wall",
          str(hid))
    check("CF1", not ({i["A"], i["B"]} | floor) & set(hid), "a participant or the floor it rests on was hidden", str(hid))
    flags = [str(x) for x in (doc.get("camera") or {}).get("viewFlags") or []]
    check("CF1", all(h in flags for h in hid) and "--protect" in flags,
          "the camera's own flags do not carry the hide and protect list, so a capture would render another picture", str(flags))


def cf2_touching_blockers(rig: Rig):
    """The same pair with every wall touching it: no wall may be hidden, the cutaway refuses naming that, and the request
    falls to the labelled top-down. Wrong answers: a touching wall hidden; a bare eye-level refusal with no top-down."""
    i = LAB.ids("CfTouch")
    doc = rig.acquire("CfTouch")
    hid = hidden_keys(doc)
    check("CF2", not set(hid) & set(i["lowWalls"]), "a wall that touches the pair was hidden", str(hid))
    if not check("CF2", doc.get("status") == "camera-ready" and doc.get("viewKind") == "top-down",
                 "the touching-walls pair did not fall to the top-down", f"{refusal_text(doc)} viewKind={doc.get('viewKind')}"):
        return
    chain = doc.get("fallbackChain") or []
    cut = next((s for s in chain if s.get("kind") == "cutaway"), None)
    check("CF2", cut is not None and cut.get("status") == "refused" and any(p in str(cut.get("detail")) for p in i["lowWalls"]),
          "the record does not say the cutaway was refused because the walls touch the pair", str(cut))
    check("CF2", (doc.get("camera") or {}).get("cullAbove") is not None,
          "the top-down camera carries no slice height", str(doc.get("camera")))


def cf3_buried(rig: Rig):
    """B is sunk under the floor A rests on, its top on the tiles' underside. Its NIF has two opaque shapes and one
    alpha-tested decal, so it is not 'see-through'; the tiles over it touch it, so no cutaway may hide them; from above
    it shows nothing, so the top-down refuses. Wrong answers: 'no opaque face' (the mislabel), a picture, or the floor
    hidden. (Fixture changed after Gate B: with a 22 u gap under the floor, the tiles over B touch nothing, the design
    lets a cutaway hide them, and B was pictured through the hole: the rule's own answer, not this case's question.)"""
    i = LAB.ids("CfBuried")
    doc = rig.acquire("CfBuried")
    ref = doc.get("refusal") or {}
    code = ref.get("pickerCode") or ref.get("code")
    check("CF3", code != "points-no-opaque-face", "a participant with opaque shapes was refused as having no opaque face",
          refusal_text(doc))
    check("CF3", doc.get("status") == "refused" and code == "top-down-participant-unseen" and i["B"] in str(ref.get("detail")),
          "the buried participant was not refused as unseen from above, by name", refusal_text(doc))
    check("CF3", ref.get("final") is True, "the refusal does not say every fallback was tried", str(ref.get("final")))


def cf4_see_through(rig: Rig):
    """B is an effect-shader glow (additive blend, no depth write) around A. Its mark is measured from its own drawn
    coverage, and the acquisition says so. Wrong answer: 'no opaque face', or a mark with no coverage measurement."""
    i = LAB.ids("CfGlow")
    doc = rig.acquire("CfGlow")
    if not check("CF4", doc.get("status") == "camera-ready", "the see-through participant got no camera", refusal_text(doc)):
        return
    pb = next((p for p in doc.get("participants") or [] if p.get("formKey") == i["B"]), {})
    check("CF4", pb.get("markKind") == "coverage", "B's mark is not recorded as a coverage measurement", str(pb))
    cov = pb.get("coverage") or {}
    check("CF4", cov.get("alphaMin") == 0.25 and (cov.get("pixels") or 0) > 0 and cov.get("atPose") is True,
          "the coverage threshold, pixel count or the check at the final pose is missing", str(cov))


def cf5_alpha_recovery(rig: Rig):
    """The recovered alpha predicts the real frame: over pixels in B's coverage mask, frame = black + hidden * (1 - a),
    where `black` is B alone over black and `hidden` is the same view with B hidden. Wrong answer: an alpha that does
    not composite back to what the renderer draws (a tone map, the sky gradient or the overlay leaking into the pass)."""
    import numpy as np
    from PIL import Image
    i = LAB.ids("CfGlow")
    cam = ["--eye", "0,-260,120", "--look", "0,0,50", "--fov", "65", "--size", "320", "--msaa", "0", "--nav", "off",
           "--report-mode", "--no-cell-outline"]
    a = rig.lab.render(rig.root / "cf5" / "cov", "CfGlow", *cam, "--coverage-of", i["B"], "--coverage-dump")
    h = rig.lab.render(rig.root / "cf5" / "hid", "CfGlow", *cam, "--hide", i["B"])
    if not check("CF5", a.returncode == 0 and h.returncode == 0, "the coverage or the hidden render did not run",
                 (a.stderr or h.stderr)[-300:]):
        return
    d = rig.root / "cf5" / "cov"
    try:
        frame = np.asarray(Image.open(next(d.glob("*-fp.png"))).convert("RGB")).astype(float)
        black = np.asarray(Image.open(next(d.glob("*-coverage-*-black.png"))).convert("RGB")).astype(float)
        alpha = np.asarray(Image.open(next(d.glob("*-coverage-*-alpha.png"))).convert("L")).astype(float) / 255.0
        mask = np.asarray(Image.open(next(d.glob("*-coverage-*-mask.png"))).convert("L")) > 0
        hidden = np.asarray(Image.open(next((rig.root / "cf5" / "hid").glob("*-fp.png"))).convert("RGB")).astype(float)
    except StopIteration:
        check("CF5", False, "the coverage run wrote no black, alpha or mask picture")
        return
    m = re.search(rf"^coverage-of {re.escape(i['B'])}: blend (additive|alpha|mixed)", a.stdout, re.M)
    if not check("CF5", m is not None and m.group(1) != "mixed", "the coverage line does not say how the shape blends",
                 a.stdout[-300:]):
        return
    # alpha blend: frame = black + behind * (1 - a); additive (dst ONE): frame = black + behind (a is then c * a)
    pred = black + hidden * (1.0 - alpha[..., None]) if m.group(1) == "alpha" else np.minimum(255.0, black + hidden)
    err = np.abs(pred - frame).max(axis=2)[mask]
    ok = float((err <= 3).mean()) if err.size else 0.0
    check("CF5", err.size > 500 and ok >= 0.95, "the recovered alpha does not composite back to the rendered frame",
          f"{err.size} mask px, {ok:.3f} within 3 levels")
    # The composite above does not read `a` for an additive shape, so it cannot see the alpha itself go wrong (the
    # A-alpha-sign arm at Gate C came back green). For an additive shape the recovered value is the colour it adds:
    # over black it draws c * a, over white the clear saturates, so a = 1 - (255 - black) / 255 = black / 255. Every
    # pixel the shape draws (its black-clear frame not black) must say so.
    if m.group(1) == "additive":
        drawn = black.max(axis=2) > 2
        diff = np.abs(alpha * 255.0 - black.mean(axis=2))[drawn]
        good = float((diff <= 3).mean()) if diff.size else 0.0
        check("CF5", diff.size > 500 and good >= 0.95,
              "the recovered alpha is not the colour the additive shape adds over black",
              f"{diff.size} drawn px, {good:.3f} within 3 levels")


def cf6_focus(rig: Rig):
    """B is a see-through glow six times its size, 900 u aside and 1400 u above A. The ring and the floor come from A,
    the smaller participant. Wrong answer: a ring centred between the two (the base picker's midpoint)."""
    rig.acquire("CfFocus")
    wl = rig.root / "acq" / "CfFocus" / "receipts" / "points-work" / "witness-worklist.json"
    if not check("CF6", wl.is_file(), "no witness ring was written for the focus pair"):
        return
    shots = json.loads(wl.read_text(encoding="utf-8"))["cells"][0]["shots"]
    lx, ly = shots[0]["look"][0], shots[0]["look"][1]
    check("CF6", abs(lx) <= 1 and abs(ly) <= 1, "the ring is not centred on A (0,0)", f"centred at {lx},{ly}")
    check("CF6", abs(shots[0]["floorZ"]) <= 8, "the floor is not A's floor (z 0)", str(shots[0]["floorZ"]))


def cf7_no_station(rig: Rig):
    """No navmesh: no eye-level station stands, so no cutaway can stand either; the request takes the top-down directly.
    Wrong answer: a points-no-station refusal with no picture."""
    doc = rig.acquire("CfNoNav")
    check("CF7", doc.get("status") == "camera-ready" and doc.get("viewKind") == "top-down",
          "the no-station pair did not take the top-down", refusal_text(doc))


def cf8_not_drawn(rig: Rig):
    """B is initially disabled. The coverage line says it is not drawn, and the refusal says so too. Wrong answer: a
    'see-through-unmarked' refusal (or line) for an object the renderer never drew."""
    i = LAB.ids("CfDisabled")
    r = rig.lab.render(rig.root / "cf8", "CfDisabled", "--eye", "0,-260,120", "--look", "0,0,40", "--size", "64",
                       "--msaa", "0", "--nav", "off", "--coverage-of", i["B"])
    check("CF8", re.search(rf"coverage-of {re.escape(i['B'])}: not-drawn", r.stdout) is not None,
          "the coverage line does not say the disabled participant is not drawn", r.stdout[-300:] + r.stderr[-200:])
    doc = rig.acquire("CfDisabled")
    code = (doc.get("refusal") or {}).get("pickerCode") or (doc.get("refusal") or {}).get("code")
    check("CF8", code == "points-not-drawn", "the disabled participant's refusal is not 'not drawn'", refusal_text(doc))


def cf9_hide_protect_banner(rig: Rig):
    """viewprobe: --hide drops a wall from the picture and prints it; --protect refuses to hide a wall touching a
    participant; a hidden-object frame carries the cutaway banner, kept in report mode. Wrong answers: the wall still
    drawn, a touching wall hidden, an unlabelled cutaway frame."""
    import numpy as np
    from PIL import Image
    c, t = LAB.ids("CfCutaway"), LAB.ids("CfTouch")
    cam = ["--eye", "15,-450,120", "--look", "15,-13,40", "--size", "320", "--msaa", "0", "--nav", "off", "--report-mode",
           "--ref", f"{c['A']}=cyan", "--ref", f"{c['B']}=magenta", "--ref-surface"]
    south = c["walls"]["S"]
    r = rig.lab.render(rig.root / "cf9" / "cut", "CfCutaway", *cam, *[x for k in south for x in ("--hide", k)],
                       "--protect", c["A"], "--protect", c["B"])
    if check("CF9", r.returncode == 0, "the render with a non-touching wall hidden did not run", r.stderr[-300:]):
        check("CF9", all(re.search(rf"^hidden: {re.escape(k)}\b", r.stdout, re.M) for k in south),
              "the hidden walls are not printed", r.stdout[-300:])
        m = re.search(r"^view-label: (CUTAWAY: 8 objects hidden[^|]*)\| rect (\d+),(\d+),(\d+),(\d+)", r.stdout, re.M)
        if check("CF9", m is not None, "the cutaway frame carries no view label in report mode", r.stdout[-300:]):
            img = np.asarray(Image.open(next((rig.root / "cf9" / "cut").glob("*-fp.png"))).convert("RGB"))
            cyan = (np.abs(img.astype(int) - (19, 255, 255)).max(axis=2) <= 2).sum()
            check("CF9", cyan > 200, "A is not seen with the south wall hidden", f"{cyan} cyan px")
    # the S wall's second piece in tier 1 spans B's x range (5..55) whichever way the quarter turn swings it, and its
    # inner face is B's south face (y -94): it touches B
    r2 = rig.lab.render(rig.root / "cf9" / "touch", "CfTouch", *cam, "--hide", t["walls"]["S"][3], "--protect", t["A"],
                        "--protect", t["B"])
    check("CF9", r2.returncode != 0 and t["walls"]["S"][3] in (r2.stderr + r2.stdout) and "touch" in (r2.stderr + r2.stdout),
          "a wall touching the pair was hidden, or the refusal does not name it", f"exit {r2.returncode}: {r2.stderr[-300:]}")


def cf10_contacts(rig: Rig):
    """--contacts-of: A touches B and rests on the floor; the clear walls are neither; the touching walls touch. Wrong
    answers: a wall 25 u away called touching, the floor missed, a touching wall missed."""
    c, t = LAB.ids("CfCutaway"), LAB.ids("CfTouch")
    floor = set(c["floor"]) | set(t["floor"])

    def contacts(cell, key, tag):
        r = rig.lab.render(rig.root / "cf10" / tag, cell, "--eye", "15,-450,120", "--look", "15,-13,40", "--size", "32",
                           "--msaa", "0", "--nav", "off", "--contacts-of", key)
        m = re.search(rf"^contacts-of {re.escape(key)}: touches \[(.*?)\]; rests on \[(.*?)\]; unassessed \[(.*?)\]", r.stdout, re.M)
        return (None if m is None else ({x.strip() for x in m[1].split(",") if x.strip()},
                                         {x.strip() for x in m[2].split(",") if x.strip()},
                                         {x.strip() for x in m[3].split(",") if x.strip()})), r
    got, r = contacts("CfCutaway", c["A"], "cut")
    if check("CF10", got is not None, "no contacts line for A", r.stdout[-200:] + r.stderr[-200:]):
        touches, rests, unk = got
        check("CF10", c["B"] in touches, "A does not touch B, which crosses it", str(touches))
        check("CF10", bool(rests & floor), "A rests on no floor tile", str(rests))
        check("CF10", not (touches | rests) & set(c["allWalls"]), "a wall 25 u clear was called touching or a support",
              str(touches | rests))
    got, r = contacts("CfTouch", t["A"], "touch")
    if check("CF10", got is not None, "no contacts line for A in the touching cell", r.stderr[-200:]):
        touches = got[0]
        check("CF10", bool(set(t["walls"]["W"][2:4]) & touches) and bool(set(t["walls"]["N"][2:4]) & touches),
              "the walls on A's faces were not called touching", str(touches))


def cf11_occluders(rig: Rig):
    """--occluders-of from the south: the south wall covers A; the north wall, behind A, does not. Wrong answers: the
    blocker missed, or a wall behind the participant listed as in front of it."""
    c = LAB.ids("CfCutaway")
    r = rig.lab.render(rig.root / "cf11", "CfCutaway", "--eye", "15,-450,120", "--look", "15,-13,40", "--size", "320",
                       "--msaa", "0", "--nav", "off", "--occluders-of", c["A"])
    rows = dict(re.findall(rf"^occluders-of {re.escape(c['A'])}: occluder (\S+:\S+) (\d+) px", r.stdout, re.M))
    check("CF11", sum(int(rows.get(k, 0)) for k in c["walls"]["S"]) > 0,
          "the south wall is not listed in front of A", str(rows) + r.stderr[-200:])
    check("CF11", not set(c["walls"]["N"]) & set(rows), "the north wall, behind A, is listed as in front of it",
          str(rows))


def cf12_capture_states(rig: Rig):
    """B1/B2: the real capture of a cutaway acquisition renders EVERY state with the camera's hide and protect flags. Each
    state's console prints its hidden objects and the view label, and the evidence lists the hidden objects per state.
    The without-the-mod state has no B (it comes with CfFixtureMod.esp): its protect check says so, runs without error,
    and hides nothing that touches or holds up B in the with-state. Wrong answers: a "cutaway" frame rendered without the
    hide flags (the wall drawn, no label), or a without-state that errors or hides B's support."""
    i = LAB.ids("CfCutMod")
    doc = rig.acquire("CfCutMod", i["A"], LAB.CUTMOD_B, order=rig.lab.cand_mod)
    if not check("CF12", doc.get("status") == "camera-ready" and doc.get("viewKind") == "cutaway",
                 "the CfCutMod pair got no cutaway camera to capture", refusal_text(doc)):
        return
    hid = set(hidden_keys(doc))
    out = rig.root / "cap" / "CfCutMod"
    r = run([sys.executable, HARNESS / "capture_acquire.py", "capture", "--acquisition", Path(doc["_dir"]) / "acquisition.json",
             "--packet", doc["_packet"], "--request", doc["_rid"], "--with-order", rig.lab.cand_mod,
             "--without-order", rig.lab.cand, "--out", out, "--size", "320", "--vp", Path(rig.vp).as_posix(), "--",
             *rig.lab.target(with_order=False)])
    res_p = out / "capture-result.json"
    res = json.loads(res_p.read_text(encoding="utf-8")) if res_p.is_file() else {}
    if not check("CF12", res.get("status") == "captured", "the cutaway's states were not captured",
                 f"{res.get('status')}: {res.get('reason')} {r.stderr[-300:]}"):
        return
    rows = {st["state"]: st for st in res.get("states") or []}
    for state in ("without", "with", "highlight"):
        st = rows.get(state) or {}
        con = (out / state / "console.txt").read_text(encoding="utf-8") if (out / state / "console.txt").is_file() else ""
        printed = {ln.split()[1] for ln in con.splitlines() if ln.startswith("hidden: ")}
        check("CF12", hid <= printed and re.search(r"^view-label: CUTAWAY", con, re.M) is not None,
              f"the {state} state's console does not print every hidden object and the cutaway label",
              f"printed {sorted(printed)} of {sorted(hid)}")
        listed = {h.split()[0] for h in st.get("hiddenObjects") or []}
        check("CF12", hid <= listed, f"the evidence does not list the {state} state's hidden objects", str(sorted(listed)))
    wo = rows.get("without") or {}
    b_said = any(LAB.CUTMOD_B in ln and "not drawn in this setup" in ln for ln in wo.get("protect") or [])
    prot_b = (doc.get("cutaway") or {}).get("protected", {}).get(LAB.CUTMOD_B) or {}
    holds_b = set(prot_b.get("touches") or []) | set(prot_b.get("restsOn") or [])
    hid_wo = {h.split()[0] for h in wo.get("hiddenObjects") or [] if "not drawn in this setup" not in h}
    check("CF12", wo.get("exit") == 0 and b_said and not hid_wo & holds_b,
          "the without state's protect check errored, did not say B is absent, or hid what holds B up in the with-state",
          f"exit {wo.get('exit')}, protect {wo.get('protect')}, hidden {sorted(hid_wo)}, B's {sorted(holds_b)}")


RENDER_CASES = [cf1_cutaway, cf2_touching_blockers, cf3_buried, cf4_see_through, cf5_alpha_recovery, cf6_focus,
                cf7_no_station, cf8_not_drawn, cf9_hide_protect_banner, cf10_contacts, cf11_occluders,
                cf12_capture_states]


# ---------------------------------------------------------------------------------------------------------------------
# code cases

def result_doc(root: Path, rid: str, refusal: dict, identity: dict) -> Path:
    d = root / rid / "sequence"
    d.mkdir(parents=True, exist_ok=True)
    (d / "capture-result.json").write_text(json.dumps({"schemaVersion": 1, "kind": "vignette-capture", "request": rid,
                                                       "status": "refused", "reason": "x", "refusal": refusal,
                                                       "toolIdentity": identity}), encoding="utf-8")
    return root / rid


def cf20_reuse(tmp: Path):
    """The reuse rule. A bare eye-level refusal is what a fallback now answers: captured again. A finished chain whose
    final code is a property of the geometry: reused. A chain whose top-down step crashed (A3), or one never marked
    finished: captured again. Wrong answers: a fallback-answerable refusal reused; a crashed step reused."""
    EP = load("cf_evalpipeline", HARNESS / "evalpipeline.py")
    ident = {"capture_acquire.py": "1", "capture_points.py": "2", "viewprobe": "3"}
    root = tmp / "cf20"
    cases = [
        ("bare-no-visible-face", {"code": "points-unpickable", "pickerCode": "points-no-visible-face"}, False),
        ("bare-no-station", {"code": "points-unpickable", "pickerCode": "points-no-station"}, False),
        ("bare-no-opaque-face", {"code": "points-unpickable", "pickerCode": "points-no-opaque-face"}, False),
        ("final-top-down-unseen", {"code": "top-down-participant-unseen", "final": True,
                                   "chain": [{"kind": "eye-level", "status": "refused", "code": "points-no-visible-face"},
                                             {"kind": "cutaway", "status": "refused", "code": "cutaway-refused"},
                                             {"kind": "top-down", "status": "refused", "code": "top-down-participant-unseen"}]},
         True),
        ("final-see-through-unmarked", {"code": "see-through-unmarked", "final": True,
                                        "chain": [{"kind": "eye-level", "status": "refused", "code": "see-through-unmarked"}]},
         True),
        ("top-down-crashed", {"code": "points-renderer-failed", "final": False,
                              "chain": [{"kind": "eye-level", "status": "refused", "code": "points-no-visible-face"},
                                        {"kind": "cutaway", "status": "refused", "code": "cutaway-refused"},
                                        {"kind": "top-down", "status": "failed", "code": "points-renderer-failed"}]}, False),
        ("final-but-crash-code", {"code": "points-renderer-failed", "final": True,
                                  "chain": [{"kind": "top-down", "status": "failed", "code": "points-renderer-failed"}]}, False),
    ]
    for rid, refusal, want in cases:
        q = {"id": rid, "armId": "a", "vignette": {"withoutArmId": "b"}}
        got = EP.reusable_capture(q, result_doc(root, rid, refusal, ident), ident) is not None
        check("CF20", got == want, f"{rid}: {'reused' if got else 'captured again'}, which is wrong",
              f"want {'reused' if want else 'captured again'}")


def cf21_reel_unmarkable(tmp: Path):
    """The reel skips a clash for an unmarkable object only on a FINISHED see-through refusal. An old bare
    points-no-opaque-face (the mislabel included) no longer removes the clash. Wrong answer: a clash dropped on it."""
    RP = load("cf_reel_plan", HARNESS / "reel_plan.py")
    fn = getattr(RP, "unmarkable_objects", None)
    if not check("CF21", fn is not None, "reel_plan has no unmarkable-object rule a test can call"):
        return
    old = [{"status": "refused", "recordKeys": ["A:x.esp", "B:x.esp"],
            "refusal": {"pickerCode": "points-no-opaque-face", "participant": "B:x.esp"}}]
    new = [{"status": "refused", "recordKeys": ["A:x.esp", "B:x.esp"],
            "refusal": {"code": "see-through-unmarked", "final": True, "participant": "B:x.esp"}}]
    check("CF21", fn(old) == set(), "a bare points-no-opaque-face still marks the object unmarkable", str(fn(old)))
    check("CF21", fn(new) == {"B:x.esp"}, "a finished see-through refusal does not mark the object", str(fn(new)))


def cf22_top_down_count(tmp: Path):
    """A4: the top-down accepts a participant at TOP_DOWN_MIN_PIXELS marked pixels and refuses it at one fewer."""
    CA = load("cf_capture_acquire", HARNESS / "capture_acquire.py")
    fn = getattr(CA, "top_down_accepts", None)
    if not check("CF22", fn is not None, "capture_acquire has no top-down acceptance rule"):
        return
    check("CF22", getattr(CA, "TOP_DOWN_MIN_PIXELS", None) == TOP_DOWN_MIN_PIXELS,
          "capture_acquire's top-down count is not the frozen one", str(getattr(CA, "TOP_DOWN_MIN_PIXELS", None)))
    ok, _ = fn({"A": TOP_DOWN_MIN_PIXELS, "B": TOP_DOWN_MIN_PIXELS})
    bad, why = fn({"A": TOP_DOWN_MIN_PIXELS - 1, "B": TOP_DOWN_MIN_PIXELS})
    check("CF22", ok is True, f"{TOP_DOWN_MIN_PIXELS} px each was refused")
    check("CF22", bad is False and "A" in str(why), f"{TOP_DOWN_MIN_PIXELS - 1} px was accepted, or the refusal does not name A",
          str(why))


def cf23_caption_words(tmp: Path):
    """A2: a cutaway frame's report caption and reel still text say it is a cutaway and how many objects are hidden; a
    top-down's say it is a sliced view from above. Wrong answer: a caption calling a cutaway or a top-down 'the view'."""
    ER = load("cf_evalreport", HARNESS / "evalreport.py")
    RP = load("cf_reel_plan2", HARNESS / "reel_plan.py")
    words = json.loads((HARNESS.parent / ".claude" / "skills" / "evaluation-reporting" / "references"
                        / "report-priorities.json").read_text(encoding="utf-8"))["reader"]["figureCaptions"]
    cut = {"viewKind": "cutaway", "hidden": [{"formKey": "1:x.esp"}, {"formKey": "2:x.esp"}]}
    top = {"viewKind": "top-down", "cullAbove": 512.0}
    fe, fr = getattr(ER, "view_kind_caption", None), getattr(RP, "view_kind_line", None)
    if not check("CF23", fe is not None and fr is not None, "evalreport or reel_plan has no view-kind caption rule"):
        return
    for name, f in (("report caption", lambda p: fe(p, words)), ("reel still", fr)):
        c, t, e = f(cut), f(top), f({"viewKind": "eye-level"})
        check("CF23", "cutaway" in c.lower() and "2 objects hidden" in c, f"the {name} for a cutaway does not say so", c)
        check("CF23", "top-down" in t.lower() and "512" in t, f"the {name} for a top-down does not say so", t)
        check("CF23", e == "", f"the {name} labels an eye-level view", e)


CODE_CASES = [cf20_reuse, cf21_reel_unmarkable, cf22_top_down_count, cf23_caption_words]


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--vp", default=str(PROD_VP))
    ap.add_argument("--only", default="")
    ap.add_argument("--keep", default=None, help="keep the work folder here")
    a = ap.parse_args(argv)
    only = {x.strip().upper() for x in a.only.split(",") if x.strip()}
    tmp = Path(a.keep) if a.keep else Path(tempfile.mkdtemp(prefix="cf-"))
    tmp.mkdir(parents=True, exist_ok=True)

    def wanted(fn):
        return not only or fn.__name__.split("_")[0].upper() in only

    for fn in CODE_CASES:
        if wanted(fn):
            before = len(FAILS)
            try:
                fn(tmp)
            except Exception as exc:  # a crash is reported as a crash, never as the case's own red
                FAILS.append(fn.__name__)
                RESULTS.append((fn.__name__.split("_")[0].upper(), "CRASH", f"{type(exc).__name__}: {exc}"))
            if len(FAILS) == before:
                RESULTS.append((fn.__name__.split("_")[0].upper(), "GREEN", ""))
    render = [fn for fn in RENDER_CASES if wanted(fn)]
    not_run = None
    if render:
        game, why = LAB.game_folder()
        if game is None:
            not_run = why
        else:
            rig = Rig(tmp / "rig", Path(a.vp), game)
            for fn in render:
                before = len(FAILS)
                try:
                    fn(rig)
                except Exception as exc:
                    FAILS.append(fn.__name__)
                    RESULTS.append((fn.__name__.split("_")[0].upper(), "CRASH",
                                    f"{type(exc).__name__}: {exc} | {traceback.format_exc().splitlines()[-3:]}"))
                if len(FAILS) == before:
                    RESULTS.append((fn.__name__.split("_")[0].upper(), "GREEN", ""))
    for case, verdict, text in RESULTS:
        print(f"{case:5} {verdict:5} {text}")
    if not_run:
        print(f"NOT RUN: render cases CF1-CF12 -- {not_run}")
    print(f"work folder: {tmp}")
    if FAILS:
        return 1
    return 3 if not_run else 0


if __name__ == "__main__":
    sys.exit(main())
