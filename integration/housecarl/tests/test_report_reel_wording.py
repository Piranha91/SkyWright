"""Known answers for the report and reel wording batch (C2120-C2131).

    python tests/test_report_reel_wording.py [--only C2120,C2124] [--harness DIR]

No game and no renderer. C2125's real-record check reads Skyrim.esm from the SSE install when it is present (else that
check is reported SKIP, never green). The cases call the real reel_plan, reel_stills, evalreport and
evalpipeline functions with the SHIPPED skill data, on the fixtures of test_navmesh_reel.py and test_reel_vignettes.py.
A function the fix adds is looked up with getattr, so HEAD fails the case's own assertion, never by a crash.

    C2120  R1   a clash the reel could not picture for want of a capture says which limit: no camera for that kind,
                or the capture limit; never "No picture of this kind was captured" / "No picture was captured of"
    C2121  R2   one wording per patch case: the shipped pools hold one phrasing each, so two vignettes with no patch
                found say the same sentence
    C2122  R3+N1 What to do lists each Nexus patch page once (its usable version), naming the pairs it covers; never one
                line per file version or per pair that shares the page
    C2123  R10  no "candidate for a visual check" / "candidates for visual review" in the report's words or the code
    C2124  R11  the text a reel still page draws holds no form key or editor ID; the load order's plugin files are kept
    C2125  N6   a place the report prints is a name: never a form key, never a bare PascalCase editor ID
    C2126  R13  a marked view that marks one object names the other one too, in the reel label and the report caption
    C2127  N2   the navmesh intro counts what it counts (links) and the groups its examples are taken from
    C2128  N4   evalpipeline resolves --entry, --machine and --out to absolute paths at parse time
    C2129  N7   the "needs some of the left-out mods" sentence reads in short sentences that name the mod, the left-out
                mod and the smaller setup
    C2130       the statement of clashes not pictured never sends a modder to an empty section name
    C2131  N6   the reel says the place the report names for a clash (found reading regeneration pass 1) ("under \"\"")

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import inspect
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
HARNESS = Path(_a0.harness).resolve() if _a0.harness else (
    HERE.parent if (HERE.parent / "reel_plan.py").is_file() else Path("S:/Dev/houseCARL/_geometry-harness"))
TESTS = HARNESS / "tests"
import reporting_data  # noqa: E402
SKILL = reporting_data.DATA
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(TESTS))
os.environ["PYTHONUTF8"] = "1"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []
FORM_KEY = re.compile(r"\b[0-9A-Fa-f]{6}:")
PASCAL = re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")
UNDERSCORE_ID = re.compile(r"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return bool(cond)


def shipped():
    pol = json.loads((SKILL / "scene-policy.json").read_text(encoding="utf-8"))
    pri = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8"))
    return pol, pri


def unpictured_chapter(reel):
    return next((ch for ch in reel.get("chapters") or [] if ch.get("kind") == "unpictured"), {})


# ------------------------------------------------------------------------------------------------ C2120 --

def c2120_capture_limit_reason(tmp: Path):
    """Cut Content's About-the-reel line said "No picture of this kind was captured (1 not pictured)" for a fixture
    clash no capture rule can request, and the reel said "No picture was captured of the 1 case". Wrong answers: either
    phrase; a reason that does not say which limit; the no-rule clash and the never-reached clash given one reason."""
    import test_navmesh_reel as T
    import reel_plan as RP
    pol, pri = shipped()
    pres, names, masters, search, mods, captures = T.planner_fixture(False)
    pres["reader"]["prediction"]["record"].append({
        "checkId": "geometry.fixture-empty-space", "kind": "FIXTURE", "A": T.EARLIER, "B": T.TARGET,
        "formKey": "000500:Later.esp", "other": ["000600:World.esm"], "summary": "a fixture in RiverTownExterior02 (ext)",
        "admission": "admitted", "findingId": "F-C1", "physical": "Holds", "attribution": "with-target",
        "attributionMod": T.TARGET, "withPatch": "left"})
    reel = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, captures,
                        captures_final=True)
    said = " ".join([str(unpictured_chapter(reel).get("narration") or "")]
                    + [str(x.get("text") or "") for x in reel.get("movedToReport") or []])
    check("C2120", "No picture of this kind was captured" not in said and "No picture was captured of" not in said,
          "the reel or the report still says a picture of this kind was not captured without saying why", said[:600])
    reasons = pol["vignettes"].get("stillReasons") or {}
    limit_ = [k for k, v in reasons.items() if "capture limit" in str(v).lower()]
    norule_ = [k for k, v in reasons.items() if "no camera for this kind" in str(v).lower()]
    check("C2120", bool(limit_) and bool(norule_) and not reasons.get("notCaptured"),
          "the shipped reasons do not name the capture limit and the kind capture has no camera for (one by one)",
          json.dumps({k: v for k, v in reasons.items() if "captur" in str(v).lower()})[:400])
    check("C2120", "capture limit" in said.lower() and "no camera for this kind" in said.lower(),
          "the fixture clash with no capture rule and the never-captured navmesh clash are not each said by their limit",
          said[:600])
    # captureChecks must not drift from what capture can request: the skill's rules plus the reel's split and state
    import report_scenes as RS
    fn = getattr(RS, "capturable_checks", None)
    want = sorted(fn(pol)) if fn else None
    have = sorted(pol["vignettes"].get("captureChecks") or [])
    check("C2120", want is not None and have == want,
          "scene-policy captureChecks names a check capture cannot request, or leaves out one it can",
          f"captureChecks {have}; capture can request {want}")


# ------------------------------------------------------------------------------------------------ C2121 --

def c2121_one_patch_wording(tmp: Path):
    """The reel rotated phrasings ("No patch for these two mods was found." / "A patch for these two mods was not
    found."), so two vignettes said one fact two ways. Wrong answer: any patch case with more than one phrasing."""
    _pol, pri = shipped()
    pools = (pri["reel"]["words"].get("patch") or {})
    many = {k: v for k, v in pools.items() if isinstance(v, list) and len(v) != 1}
    check("C2121", not many, "a patch case still has several phrasings, so vignettes say one fact differently",
          json.dumps(many, ensure_ascii=False)[:400])


# ------------------------------------------------------------------------------------------------ C2122 --

def c2122_patch_page_once(tmp: Path):
    """Clockwork's What to do said the COTN Falkreath Patch Collection nine times (three pairs x current, retired and old
    version) and Winterhold's and Grand Solitude's three times each (about 430 s of a 776 s reel). Wrong answers: no
    one-line-per-page builder; a page named more than once; a retired or old-version caution when a live version
    exists; a pair the page covers left unnamed; the no-hit pair dropped."""
    import evalreport as ER
    _pol, pri = shipped()
    words = pri["reel"]["words"]
    fn = getattr(ER, "patch_search_leads", None)
    if not check("C2122", fn is not None, "the report has no one-line-per-patch-page builder for What to do"):
        return

    def h(page, fid, ver, status, cat, paths, name):
        return {"file": name, "pageId": page, "fileId": fid, "version": ver, "status": status, "category": cat,
                "contentPaths": paths}
    cotn = "COTN Falkreath Patch Collection"
    hits_c = [h(56734, 3, "1.29", "live", "MAIN", ["COTN Falkreath - Clockwork Patch.esp"], cotn),
              h(56734, 2, "1.28.1", "retired", "OLD_VERSION", ["COTN Falkreath - Clockwork Patch.esp"], cotn),
              h(56734, 1, "1.28", "retired", "OLD_VERSION", ["COTN Falkreath - Clockwork Patch.esp"], cotn)]
    tgc = "The Great City of Winterhold Patch Collection"
    hits_w = [h(74560, 5, "2.11", "live", "MAIN", ["TGC Winterhold - Clockwork Patch.esp"], tgc),
              h(74560, 4, "2.10", "retired", "ARCHIVED", ["TGC Winterhold - Clockwork Patch.esp"], tgc)]
    search = {"pairs": [{"pair": ["COTN - Falkreath.esp", "Clockwork.esp"], "hits": hits_c},
                        {"pair": ["The Great City of Falkreath.esp", "Clockwork.esp"], "hits": copy.deepcopy(hits_c)},
                        {"pair": ["COTN Falkreath - TGCF patch.esp", "Clockwork.esp"], "hits": copy.deepcopy(hits_c)},
                        {"pair": ["The Great City of Winterhold v4.esp", "Clockwork.esp"], "hits": hits_w},
                        {"pair": ["Unofficial Skyrim Special Edition Patch.esp", "Clockwork.esp"], "hits": []}]}
    loaded = {p.lower() for e in search["pairs"] for p in e["pair"]}
    lines = fn(search, words, loaded, [], {})
    text = "\n".join(lines)
    check("C2122", len(lines) == 3,
          "What to do does not say one line per patch page plus one for the pair with no hit", f"{len(lines)} lines: {text[:600]}")
    check("C2122", text.count(cotn) == 1 and text.count(tgc) == 1,
          "a patch page is named more than once", text[:600])
    check("C2122", "retired" not in text.lower() and "old version" not in text.lower() and "archived" not in text.lower(),
          "a retired, archived or old-version caution is said for a page that has a live version", text[:600])
    cl = next((x for x in lines if cotn in x), "")
    check("C2122", all(p in cl for p in ("COTN - Falkreath.esp", "The Great City of Falkreath.esp", "COTN Falkreath - TGCF patch.esp")),
          "the page's line does not name every pair it covers", cl)
    check("C2122", any("Unofficial Skyrim Special Edition Patch.esp" in x for x in lines),
          "the pair the search found nothing for is dropped", text[:600])


# ------------------------------------------------------------------------------------------------ C2123 --

def c2123_no_candidate_wording(tmp: Path):
    """Helgi report-21 page 2 said "a candidate for a visual check". Wrong answer: either phrase in the shipped words or
    in the code that builds the report and the reel."""
    phrases = ("candidate for a visual check", "candidates for visual review")
    hits = []
    for f in [SKILL / "report-priorities.json", SKILL / "scene-policy.json", HARNESS / "evalreport.py",
              HARNESS / "reel_plan.py", HARNESS / "evalreel.py", HARNESS / "readerdoc.py"]:
        if f.is_file():
            low = f.read_text(encoding="utf-8", errors="replace").lower()
            hits += [f"{f.name}: {p}" for p in phrases if p in low]
    check("C2123", not hits, "the report or reel still says a clash is a candidate for visual review", "; ".join(hits))


# ------------------------------------------------------------------------------------------------ C2124 --

def c2124_still_page_text(tmp: Path):
    """Cut Content's What to do page showed "15E420:Skyrim Cut Content Restoration (a placed FarmInteriorFireLight01 ...)"
    and Hroggar's "BUV_EisaFollowerRef"; C1961 cleaned speech only. Wrong answers: a form key, an underscore editor ID
    or a PascalCase editor ID in the text a still page draws; a load-order plugin file rewritten."""
    import reel_stills as RS
    row = ("Lux - Skyrim Cut Content Restoration switches off 15E420:Skyrim Cut Content Restoration (a placed "
           "FarmInteriorFireLight01, in Frost River Meadery) and runs BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100.")
    pages = [{"layout": "list", "title": "What to do", "rows": [row, "Check BUV_EisaFollowerRef in game."]},
             {"layout": "statement", "text": "In SolitudeExterior01, 0C397F:Skyrim.esm is cut."},
             {"layout": "checks", "title": "Checks", "rows": [{"reason": "ran, but could not decide",
                                                                "subjects": ["Connected doors at WhiterunJorrvaskr"]}]}]
    drawn = [t for p in pages for _s, t, _k in RS._blocks(p)]
    bad = [t for t in drawn if FORM_KEY.search(t) or UNDERSCORE_ID.search(t) or PASCAL.search(t)]
    check("C2124", not bad, "a still page draws a form key or an editor ID", " || ".join(bad)[:600])
    lo = {"layout": "load-order", "title": "Load order", "plugins": ["Skyrim.esm", "SkyUI_SE.esp", "JKsSkyrim.esp"],
          "affected": ["JKsSkyrim.esp"]}
    shown = " ".join(t for _s, t, _k in RS._blocks(lo))
    check("C2124", "SkyUI_SE.esp" in shown and "JKsSkyrim.esp" in shown,
          "the load-order page's plugin files are rewritten", shown)


# ------------------------------------------------------------------------------------------------ C2125 --

def c2125_report_place_is_a_name(tmp: Path):
    """Clockwork report-02 printed "00929E:Skyrim.esm (ext) (4)" and "SolitudeExterior01 (ext) (6)" as places. Wrong
    answers: no place builder; a form key or bare PascalCase editor ID returned; a named cell not said by its name; an
    unnamed exterior cell not said by its worldspace and grid where those were read."""
    import evalreport as ER
    _pol, pri = shipped()
    fn = getattr(ER, "said_place", None)
    if not check("C2125", fn is not None, "the report has no place builder that says a place by its name"):
        return
    places = {"CellOne": {"name": "The First Hall"}}

    def reader(what, fk):
        return {("editorId", "00929E:Skyrim.esm"): None, ("cellWorld", "00929E:Skyrim.esm"): "00003C:Skyrim.esm",
                ("editorId", "00003C:Skyrim.esm"): "Tamriel", ("cellGrid", "00929E:Skyrim.esm"): [-4, 25]}.get((what, fk))
    got = {lab: fn(lab, places, pri["reader"]["prediction"], reader)
           for lab in ("CellOne (int)", "SolitudeExterior01 (ext)", "00929E:Skyrim.esm (ext)")}
    bad = {k: v for k, v in got.items() if FORM_KEY.search(str(v)) or PASCAL.search(str(v))}
    check("C2125", not bad, "a place is printed as a form key or an editor ID", json.dumps(got))
    check("C2125", got["CellOne (int)"] == "The First Hall", "a named cell is not said by its name", json.dumps(got))
    check("C2125", "Tamriel" in str(got["00929E:Skyrim.esm (ext)"]) and "-4" in str(got["00929E:Skyrim.esm (ext)"])
          and "25" in str(got["00929E:Skyrim.esm (ext)"]),
          "an unnamed exterior cell is not said by its worldspace and grid", json.dumps(got))
    # the compiled report: an item's places and a finding's summary print no bare editor ID as a place (Clockwork
    # report-02 printed "SolitudeExterior01 (ext) (6)" in the item and "including CellA (ext)" in the summary)
    import test_evalreport_prediction as P
    rows = [P.row("CONTACT", P.MOD_B, P.MOD_A, f"00080{i}:{P.MOD_A}", label=cell) for i, cell in
            enumerate(["CellA (ext)"] * 3 + ["CellB (ext)"] * 2 + ["CellC (int)"])]
    _plan, md, _html = P.compile_and_render([P.arm("baseline", [P.BASE, P.MOD_A, P.MOD_B], kind="baseline")],
                                            {(P.CONTACT, "baseline"): rows}, [P.MOD_A])
    raw = sorted(set(re.findall(r"\bCell[ABC] \((?:ext|int)\)|\bCell[ABC]\b", md)))
    check("C2125", not raw and "Cell A (3)" in md, "the compiled report prints a cell's editor ID as a place", str(raw))


# ------------------------------------------------------------------------------------------------ C2126 --

def c2125_cell_grid_real_record(tmp: Path):
    """The grid an unnamed Tamriel cell is said by is read from its own record. Expected value: houseCARL
    housecarl_records formids=[00929E:Skyrim.esm] source=Skyrim.esm project.fields=[Grid] on 2026-11-13 (epoch
    e2-1fb5d14f70a27793, Mutagen's reading): Grid.Point = -19,24; no EditorID, no Name. Wrong answers: another grid, or
    no grid read for an exterior cell."""
    import pluginread
    esm = Path("C:/Games/Steam/steamapps/common/Skyrim Special Edition/Data/Skyrim.esm")
    fn = getattr(pluginread, "cell_grids", None)
    if not check("C2125", fn is not None, "pluginread cannot read a cell's grid"):
        return
    if not esm.is_file():
        RESULTS.append(("C2125", "SKIP", f"no {esm}: the real-record grid check did not run"))
        return
    got = fn(str(esm), ["00929E:Skyrim.esm"])["records"].get("00929E:Skyrim.esm")
    check("C2125", got == [-19, 24], "the cell's grid read from Skyrim.esm is not houseCARL's -19, 24", str(got))


def c2130_no_empty_heading(tmp: Path):
    """The fixture reel said 'Find them all in the report under ""' when the presentation names no prediction heading.
    Wrong answers: an empty heading in quotes; the report not named at all; the heading not used when there is one."""
    import test_navmesh_reel as T
    import reel_plan as RP
    pol, pri = shipped()
    reel, _p, _w = T.plan(captured=False, final=True)
    said = str(unpictured_chapter(reel).get("narration") or "")
    check("C2130", '""' not in said and "accompanying report" in said,
          "the statement of clashes not pictured sends a modder to an empty section name", said[:400])
    pres, names, masters, search, mods, captures = T.planner_fixture(False)
    pres["reader"]["sectionHeadings"] = {"prediction": "What adding Later Mod predicts"}
    reel2 = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, captures,
                         captures_final=True)
    said2 = str(unpictured_chapter(reel2).get("narration") or "")
    check("C2130", '"What adding Later Mod predicts"' in said2, "a named heading is not said", said2[:400])


def c2131_reel_says_report_place(tmp: Path):
    """Pass 1 of the regeneration: once the report said places as names, Cut Content's reel said "In an unnamed place"
    where it had said "In Tamriel, grid cell -21, 18" (the reel parsed only form-key labels). Wrong answers: the reel's
    vignette not saying the report's place for its clash; "an unnamed place" for a place the report names."""
    import test_navmesh_reel as T
    import reel_plan as RP
    pol, pri = shipped()
    pres, names, masters, search, mods, captures = T.planner_fixture(True)
    pres["reader"]["prediction"]["placeLabels"] = {T.SRC: "Tamriel, grid cell 3, 4"}
    reel = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, captures,
                        captures_final=True)
    said = " ".join(str(c.get("narration") or "") for c in T.nav_chapters(reel) if c.get("kind") == "vignette")
    check("C2131", "Tamriel, grid cell 3, 4" in said and "unnamed place" not in said,
          "the reel does not say the place the report names for its clash", said[:300])


def c2126_marked_view_names_both(tmp: Path):
    """Hroggar's station views marked only the plant in magenta; the house's object it meets was named only in the
    narration. Wrong answers: a one-mark label or caption that does not name the other object of the pair."""
    import test_navmesh_reel as T
    import test_reel_vignettes as V
    import reel_plan as RP
    pol, pri = shipped()
    pres, names, masters, search, mods = V.synthetic()
    fk, ot = "000601:Target.esp", "000602:Beta.esp"
    pres["media"] = [{"id": f"M-s-{s}", "assessed": True, "mediaKind": "offline-render",
                      "subject": {"checkId": "state.conditional-geometry", "armId": "with-target", "formKey": fk},
                      "provenance": {"captureRequest": "req-s", "vignetteState": s, "poseId": "p1",
                                     "argv": ["viewprobe.exe"] + (["--ref", f"{fk}=magenta"] if s == "highlight" else [])}}
                     for s in ("without", "with", "highlight")]
    caps = [{"request": "req-s", "status": "captured", "formKey": fk, "recordKeys": [fk, ot], "requestType": "state-pair"}]
    reel = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, caps,
                        captures_final=True)
    lab = next((f.get("label") for ch in reel.get("chapters") or [] if (ch.get("clash") or {}).get("type") == "state"
                for f in ch.get("frames") or [] if f.get("state") == "highlight"), None)
    check("C2126", bool(lab) and "Cairn" in str(lab) and "Rock Arch" in str(lab),
          "the reel's one-mark label does not name the other object of the pair", repr(lab))
    import evalreport as ER
    sig = inspect.signature(ER.highlight_caption)
    if not check("C2126", "participants" in sig.parameters,
                 "the report's marked-view caption is not given the pair, so it cannot name the unmarked object",
                 str(sig)):
        return
    key, slots = ER.highlight_caption({"magenta": fk}, lambda f: {fk: "Cairn", ot: "Rock Arch"}.get(f, f),
                                      lambda p: p, participants=[fk, ot])
    cap = str(pri["reader"]["figureCaptions"].get(key) or "").format(setup="With Target", **slots)
    check("C2126", "Cairn" in cap and "Rock Arch" in cap, "the report's one-mark caption does not name the other object", cap)


# ------------------------------------------------------------------------------------------------ C2127 --

def c2127_navmesh_intro_counts(tmp: Path):
    """Clockwork's intro said "There are 52 cases of navmesh edits and broken links. Here are 3 examples." while the 52
    are links and the examples groups. Wrong answers: the intro calls links "cases"; the group count left unsaid."""
    import test_navmesh_reel as T
    reel, _pol, _pri = T.plan()
    ch = next((c for c in T.nav_chapters(reel) if c.get("kind") == "vignette"), None)
    said = str((ch or {}).get("narration") or "")
    check("C2127", "broken navmesh link" in said and "case" not in said.split(".")[0],
          "the navmesh intro does not count links as links", said[:300])
    check("C2127", re.search(r"\b2 groups\b|\btwo groups\b", said) is not None,
          "the navmesh intro does not say how many groups its examples are taken from", said[:300])


# ------------------------------------------------------------------------------------------------ C2128 --

def c2128_entry_absolute(tmp: Path):
    """A relative --entry broke the held capture-rounds child (FileNotFoundError: the child runs from the repo root).
    Wrong answer: any of --entry, --machine, --out left relative after parsing."""
    import evalpipeline as EP
    a = EP.build_parser().parse_args(["run", "--entry", "rel/entry.json", "--machine", "rel/machine.json", "--out", "rel/out"])
    got = {k: str(getattr(a, k)) for k in ("entry", "machine", "out")}
    check("C2128", all(Path(v).is_absolute() for v in got.values()),
          "a path argument stays relative after parsing", json.dumps(got))
    b = EP.build_parser().parse_args(["capture-rounds", "--entry", "e.json", "--out", "o"])
    check("C2128", Path(str(b.entry)).is_absolute() and Path(str(b.out)).is_absolute(),
          "the capture-rounds child's own parse leaves a path relative", f"{b.entry} {b.out}")


# ------------------------------------------------------------------------------------------------ C2129 --

def c2129_needs_some_readable(tmp: Path):
    """The Solitude item read "It needs Clockwork.esp together with one or more of the mods that the setup measured with
    only ... besides the base game leaves out, Grand Solitude ... among them; which of them it needs is not separated."
    Wrong answers: a sentence over 25 words with Clockwork's values; the mod, the left-out mod or the setup unsaid."""
    _pol, pri = shipped()
    t = str(((pri["reader"]["prediction"].get("attribution") or {}).get("needs-some")) or "")
    slots = {"mod": "Clockwork.esp", "other": "Grand Solitude - The Walls of High King Erling.esp",
             "setup": "only Unofficial Skyrim Special Edition Patch.esp and Resources - The Great Cities.esp and "
                      "Clockwork.esp besides the base game"}
    try:
        text = t.format(**slots)
    except (KeyError, IndexError) as e:
        text = f"<format error {e}>"
    sents = [s for s in re.split(r"(?<=[.;:])\s+", text) if s.strip()]
    long_ = [s for s in sents if len(re.sub(r"\S+\.es[pml]\b", "X", s).split()) > 25]
    check("C2129", text and not long_, "a needs-some sentence is still over 25 words", " | ".join(long_)[:400])
    check("C2129", all(v in text for v in slots.values()) or (slots["mod"] in text and slots["other"] in text
                                                              and "Resources - The Great Cities.esp" in text),
          "the sentence leaves out the mod, the left-out mod or the smaller setup", text[:400])


CASES = {"C2120": c2120_capture_limit_reason, "C2121": c2121_one_patch_wording, "C2122": c2122_patch_page_once,
         "C2123": c2123_no_candidate_wording, "C2124": c2124_still_page_text, "C2125": c2125_report_place_is_a_name,
         "C2126": c2126_marked_view_names_both, "C2127": c2127_navmesh_intro_counts, "C2128": c2128_entry_absolute,
         "C2129": c2129_needs_some_readable, "C2130": c2130_no_empty_heading,
         "C2131": c2131_reel_says_report_place}
CASES_EXTRA = {"C2125": c2125_cell_grid_real_record}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--harness", default=None)
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="rrw-"))
    for cid, fn in CASES.items():
        if only and cid not in only:
            continue
        for f_ in [fn] + ([CASES_EXTRA[cid]] if cid in CASES_EXTRA else []):
            try:
                f_(root)
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
