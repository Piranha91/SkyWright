"""Known answers for report record labels and What-to-do length (C2200-C2208).

    python tests/test_report_record_labels.py [--only C2200,C2204] [--harness DIR]

No game and no renderer. The cases call the real evalreport, reel_plan and test_reel_vignettes code with the SHIPPED
skill data, on the fixtures of test_evalreport_prediction.py, test_navmesh_reel.py and test_report_reel_wording.py. A
function the fix adds is looked up with getattr, so HEAD fails the case's own assertion, never by a crash.

    C2200  1   a record is said in words: an object by its kind and its base's name or editor ID in words, a named
               record by its name, an editor-ID-only record by the editor ID in words; never a form key, never a bare
               editor ID; a record nothing names is said with its plugin by said_record, and keeps its form key in a
               sentence (C2208), the one thing that tells it from another
    C2201  1   the compiled report's text prints no form key and no bare editor ID for an object; the evidence index
               keeps the form keys
    C2202  2   What to do says each "check them in game" check once across mod pairs, with the summed count, and one
               line per lost-field check whose pairs share one step; every check title is still said
    C2203  2   a patch verdict in the reel keeps its verdict and counts but leaves the switched-off objects' names, the
               removed-script records and "measured on the setup ... alone" to the report, which keeps them
    C2204  4   an unread cell says why it was not read; never "a cell with no name"; the compiled report never prints it
    C2205  5   a long mod name has a short form taken from its own name; none when it would be ambiguous, collide with
               another mod's name, or name a patch after the mods it patches; a short name has none
    C2206  5   the reel says a long mod name in full once, then by its short form, in speech and on its still pages
    C2207  3   test_reel_vignettes' arms code-known-answer and code-split-left-arm target strings that occur once in
               current code
    C2208  1   two different values in one sentence never read the same: a value whose words another shares keeps its
               form key (Gate A review, answer 2); two records named alike are numbered; a record nothing names keeps
               its form key

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
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
HARNESS = Path(_a0.harness).resolve() if _a0.harness else (
    HERE.parent if (HERE.parent / "reel_plan.py").is_file() else Path("S:/Dev/houseCARL/_geometry-harness"))
TESTS = HARNESS / "tests"
SKILL = HARNESS.parent / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(TESTS))
os.environ["PYTHONUTF8"] = "1"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []
FORM_KEY = re.compile(r"\b[0-9A-Fa-f]{6}:")
FORM_KEY_FULL = re.compile(r"\b[0-9A-Fa-f]{6}:[^\s,;()\[\]]+?\.es[mpl]\b")
PASCAL = re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")
UNDERSCORE_ID = re.compile(r"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b(?!\.(?:esp|esm|esl)\b)")
LONG = "BUVARP RE - Barely Used Vanilla Actors Recycle Project Special Edition Revamped"


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


def prose(md: str) -> str:
    """The report's text without link targets, image paths, code blocks and the load-order scope block."""
    md = re.sub(r"```.*?```", "", md, flags=re.S)
    md = re.sub(r"\]\([^)]*\)", "]", md)
    return md


# ------------------------------------------------------------------------------------------------ C2200 --

def c2200_said_record(tmp: Path):
    """Pass 4 printed "09A3C0:Skyrim.esm (a tree TreeThicket01Snow)", "Eisa Blackthorn, 0D001A:Skyrim.esm" and
    "BUV_ControlQuestEffect_dunFrostmereCryptQST_16, 0DE5D9:BUVARP SE RE.esp". Wrong answers: no record builder; a
    form key or a bare editor ID returned; the object's kind or base lost; a named record not said by its name; a record
    nothing names said as a form key or as having no name."""
    import evalreport as ER
    _pol, pri = shipped()
    fn = getattr(ER, "said_record", None)
    if not check("C2200", fn is not None, "the report has no record builder that says a record in words"):
        return
    names = {"refs": {"09A3C0:Skyrim.esm": {"base": "0A1111:Skyrim.esm"}},
             "bases": {"0A1111:Skyrim.esm": {"type": "Tree", "editorId": "TreeThicket01Snow", "model": "x.nif"}},
             "objects": {}, "places": {}}
    labels = {"0D001A:Skyrim.esm": "Eisa Blackthorn"}
    eids = {"0DE5D9:BUVARP SE RE.esp": "BUV_ControlQuestEffect_dunFrostmereCryptQST_16"}

    def reader(what, fk):
        return eids.get(fk) if what == "editorId" else None
    words = pri["reader"]["prediction"]
    got = {fk: fn(fk, names, labels, words, reader) for fk in
           ("09A3C0:Skyrim.esm", "0D001A:Skyrim.esm", "0DE5D9:BUVARP SE RE.esp", "054405:Skyrim.esm")}
    bad = {k: v for k, v in got.items() if FORM_KEY.search(str(v)) or PASCAL.search(str(v))
           or UNDERSCORE_ID.search(str(v))}
    check("C2200", not bad, "a record is said as a form key or an editor ID", json.dumps(got))
    t = str(got["09A3C0:Skyrim.esm"])
    check("C2200", "tree" in t.lower() and "Tree Thicket 01 Snow" in t, "a placed object loses its kind or its base",
          json.dumps(got))
    check("C2200", got["0D001A:Skyrim.esm"] == "Eisa Blackthorn", "a named record is not said by its name",
          json.dumps(got))
    check("C2200", "Control Quest Effect" in str(got["0DE5D9:BUVARP SE RE.esp"]),
          "an editor-ID-only record is not said by its editor ID in words", json.dumps(got))
    u = str(got["054405:Skyrim.esm"])
    check("C2200", "Skyrim.esm" in u and "no name" not in u.lower(),
          "a record nothing names is not said with its plugin, or is said to have no name", u)


# ------------------------------------------------------------------------------------------------ C2201 --

def c2201_compiled_report_records(tmp: Path):
    """The compiled report's example lists printed objects as "<form key> (a static object <EditorID>)". Wrong
    answers: a form key or the base's bare editor ID in the report text; the base said nowhere; the evidence index
    losing the form keys."""
    import test_evalreport_prediction as P
    X, Y = f"000A01:{P.MOD_A}", f"000B01:{P.MOD_B}"
    refs = {X: {"base": f"0B0A01:{P.MOD_A}"}, Y: {"base": f"0B0B01:{P.MOD_B}"}}
    bases = {f"0B0A01:{P.MOD_A}": {"type": "Static", "editorId": "SPatioWallEntrance", "model": "a.nif"},
             f"0B0B01:{P.MOD_B}": {"type": "Tree", "editorId": "TreeThicket01Snow", "model": "b.nif"}}
    rows = [P.row("OVERLAP", P.MOD_B, P.MOD_A, X, other=Y)]
    plan, md, _html, idx = P.compile_and_render([P.arm("baseline", [P.BASE, P.MOD_A, P.MOD_B], kind="baseline")],
                                                {(P.OVERLAP, "baseline"): rows}, [P.MOD_A], want_index=True,
                                                names={"refs": refs, "bases": bases})
    text = prose(md)
    fks = sorted(set(FORM_KEY_FULL.findall(text)))
    eids = sorted(set(re.findall(r"\b(?:SPatioWallEntrance|TreeThicket01Snow)\b", text)))
    check("C2201", not fks and not eids, "the report text prints a form key or a base's bare editor ID",
          f"form keys {fks}; editor ids {eids}")
    check("C2201", "Tree Thicket 01 Snow" in text or "S Patio Wall Entrance" in text,
          "the report says neither object's base at all", text[:300])
    check("C2201", X in idx, "the evidence index no longer carries the object's form key")


# ------------------------------------------------------------------------------------------------ C2202 --

def c2202_one_line_per_check(tmp: Path):
    """Clockwork's What to do said "Navmesh edits and broken links: N are predicted; check them in game ..." five
    times, "Objects occupying the same space" four times and "Cell settings changed by more than one mod" three times,
    one line per mod pair. Wrong answers: a check's step said more than once; the count not the sum; a check left out."""
    import evalreport as ER
    _pol, pri = shipped()
    w = pri["reader"]["prediction"]
    nav, ovl, cel = "Navmesh edits and broken links", "Objects occupying the same space", "Cell settings changed by more than one mod"
    tgt = "Clockwork.esp"
    items = ([{"checkId": "geometry.navmesh-ownership", "kind": "NAV", "title": nav, "count": n, "mods": [tgt, m]}
              for n, m in ((20, "A.esp"), (13, "B.esp"), (8, "C.esp"))]
             + [{"checkId": "geometry.object-overlap", "kind": "OVERLAP", "title": ovl, "count": n, "mods": [tgt, m]}
                for n, m in ((9, "A.esp"), (6, "B.esp"))]
             + [{"checkId": "records.cell-attributes", "kind": "LOST-EDIT", "title": cel, "count": n, "mods": [tgt, m],
                 "lostSplit": {"own": 0, "loser": m, "winner": tgt}} for n, m in ((6, "A.esp"), (2, "B.esp"))])
    lead = {"prediction": {"items": items, "predictionArm": "baseline"}}
    out = ER.what_to_do(lead, None, w, {}, [], {}, plan_targets=[])
    said = " | ".join(out)
    per = {t: sum(1 for x in out if x.startswith(t)) for t in (nav, ovl, cel)}
    check("C2202", all(v == 1 for v in per.values()), "a check's next step is said once per mod pair",
          json.dumps(per) + " :: " + said[:600])
    navl = next((x for x in out if x.startswith(nav)), "")
    ovll = next((x for x in out if x.startswith(ovl)), "")
    cell = next((x for x in out if x.startswith(cel)), "")
    check("C2202", "41" in navl and "15" in ovll and "8" in cell, "a merged line does not carry the summed count",
          said[:600])
    check("C2202", all(t in said for t in (nav, ovl, cel)), "a check is left out of What to do", said[:600])


# ------------------------------------------------------------------------------------------------ C2203 --

def c2203_patch_verdict_short_in_reel(tmp: Path):
    """Hroggar's reel said the BUVARP patch verdict in 141 words: "This is measured on the setup with ... alone",
    each switched-off object and script record by name, "(read from the patch's own records)". Wrong answers: the reel
    still says those; the reel drops the verdict or the counts; the report loses the names or the alone setup."""
    import evalreport as ER
    import reel_plan as RP
    _pol, pri = shipped()
    w = pri["reader"]["prediction"]["whatToDo"]
    acts = pri["reader"]["prediction"].get("patchActs") or {}
    p, mod = "Env - BUV Patch.esp", "BUVARP SE RE.esp"
    objs = ["BUV_EisaFollowerRef (a placed BUV_EisaFollower, in Moorside Inn)"]
    recs = ["BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100", "BUV_ControlQuestEffect_dunFrostmereCryptQST_16"]
    s = ER.bind(w["resolvesNone"], {"patch": p, "pair": "A.esp and B.esp", "count": 7, "clashNoun": "clashes"})
    s += ER.bind(w["aloneMeasured"], {"patch": p})
    s += ER.bind(acts["switchesOff"], {"mod": mod, "objects": ER.and_list(objs), "count": len(objs),
                                        "noun": "object"})
    s += ER.bind(acts["stripsScripts"], {"mod": mod, "records": ER.and_list(recs), "count": len(recs),
                                          "noun": "records"})
    reel_, _cut = RP.strip_caveats(s, pri["reel"]["words"])
    check("C2203", "alone" not in reel_ and "read from the patch's own records" not in reel_
          and "EisaFollower" not in reel_ and "ControlQuestEffect" not in reel_,
          "the reel's patch verdict still says the alone setup, the object or record names, or where they were read",
          reel_)
    check("C2203", "resolves none of the 7" in reel_ and "switches off 1" in reel_ and "2 records" in reel_
          and mod in reel_, "the reel's verdict loses the verdict, the counts or the mod", reel_)
    check("C2203", "alone" in s and "EisaFollower" in s and "ControlQuestEffect_dunFrostmereCryptQST_16" in s,
          "the report's verdict no longer carries the alone setup or the names", s)


# ------------------------------------------------------------------------------------------------ C2204 --

def c2204_unread_cell_says_why(tmp: Path):
    """A cell named by form key whose plugin bytes are not the recorded ones was printed "a cell with no name". Wrong
    answers: that phrase; a sentence that does not say the cell's plugin could not be read; a form key."""
    import evalreport as ER
    _pol, pri = shipped()
    words = pri["reader"]["prediction"]

    def reader(what, fk):
        return "changed" if what == "why" else None
    got = ER.said_place("00929E:Skyrim.esm (ext)", {}, words, reader)
    check("C2204", "no name" not in got.lower() and not FORM_KEY.search(got) and "Skyrim.esm" in got
          and ("not read" in got.lower() or "could not be read" in got.lower()),
          "an unread cell is said to have no name, or is not said as unread", got)
    import test_evalreport_prediction as P
    rows = [P.row("CONTACT", P.MOD_B, P.MOD_A, f"00080{i}:{P.MOD_A}", label="00929E:Skyrim.esm (ext)") for i in (1, 2)]
    _plan, md, _html = P.compile_and_render([P.arm("baseline", [P.BASE, P.MOD_A, P.MOD_B], kind="baseline")],
                                            {(P.CONTACT, "baseline"): rows}, [P.MOD_A])
    check("C2204", "a cell with no name" not in md, "the compiled report prints an unread cell as one with no name",
          re.findall(r"[^.]{0,80}a cell with no name[^.]{0,40}", md)[:2].__repr__())


# ------------------------------------------------------------------------------------------------ C2205 --

def c2205_short_form_rule(tmp: Path):
    """BUVARP's reel said its 80-character name in full about 20 times. Wrong answers: no short-form rule; BUVARP not
    "BUVARP RE"; a short form that two mods share, that names another mod, or that turns a patch into the mod it
    patches; a short name shortened."""
    import reel_plan as RP
    _pol, pri = shipped()
    fn = getattr(RP, "short_mod_names", None)
    if not check("C2205", fn is not None, "the reel has no short-form rule for long mod names"):
        return
    words = pri["reel"]["words"]
    gs1, gs2 = "Grand Solitude - The Walls of High King Erling", "Grand Solitude - Skyrim Cut Content Restoration patch"
    snozz = "Snozz's White River Watch - Ryn's White River Watch Patch"
    names = [LONG, gs1, gs2, snozz, "Lux", "Cities of the North - Falkreath", "Unofficial Skyrim Special Edition Patch"]
    got = fn(names, words)
    check("C2205", got.get(LONG) == "BUVARP RE", "BUVARP's short form is not taken from its own name", json.dumps(got))
    # (report words, Gate A Q1) a name that names a patch is never said by its own head, so its head blocks no other
    # name: Grand Solitude keeps its short form beside "Grand Solitude - ... patch", which is said in full
    check("C2205", got.get(gs1) == "Grand Solitude" and gs2 not in got, "two mods share a short form", json.dumps(got))
    check("C2205", snozz not in got, "a patch named after the mods it patches is shortened to one of them",
          json.dumps(got))
    check("C2205", not any(k in got for k in ("Lux", "Cities of the North - Falkreath",
                                               "Unofficial Skyrim Special Edition Patch")),
          "a name that is not long gets a short form", json.dumps(got))
    got2 = fn([LONG, "BUVARP RE"], words)
    check("C2205", LONG not in got2, "a short form that is another mod's whole name is used", json.dumps(got2))


# ------------------------------------------------------------------------------------------------ C2206 --

def c2206_reel_says_long_name_once(tmp: Path):
    """Wrong answers: the long name said in full more than once in the reel's speech or on its pages; the short form
    never said; the mod never named."""
    import test_navmesh_reel as T
    import reel_plan as RP
    pol, pri = shipped()
    pres, names, masters, search, mods, captures = T.planner_fixture(False)
    mods = copy.deepcopy(mods)
    mods[T.TARGET] = {"name": LONG, "source": "declared"}
    pres["reader"]["prediction"]["whatToDo"] = [
        f"Navmesh links: 3 are predicted between {T.TARGET} and {T.EARLIER}; check them in game.",
        f"Nexus search for {T.TARGET} and {T.EARLIER}: no file naming both mods was found."]
    reel = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, captures,
                        captures_final=True)
    chs = reel.get("chapters") or []
    said = " ".join(str(ch.get("narration") or "") for ch in chs)
    # what is spoken from the pages themselves (reel_stills.page_say: lead, fixed say, body) and from the vignettes
    body = []
    for ch in chs:
        pages = (ch.get("still") or {}).get("pages") or []
        if not pages:
            body.append(str(ch.get("narration") or ""))
        for p in pages:
            body += [json.dumps({k: v for k, v in p.items() if k != "title"}, ensure_ascii=False)]
    titles = json.dumps([[p.get("title") for p in (ch.get("still") or {}).get("pages") or []] +
                         [f.get("label") for f in ch.get("frames") or [] if isinstance(f, dict)] for ch in chs],
                        ensure_ascii=False)
    n_said, n_body, n_shown = said.count(LONG), " ".join(body).count(LONG), titles.count(LONG)
    check("C2206", n_said == 1 and "BUVARP RE" in said.replace(LONG, ""),
          "the reel says the long name in full more than once, or never by its short form",
          f"full {n_said}x; {said[:500]}")
    check("C2206", n_body == 1, "the reel's pages speak the long name in full more than once", f"full {n_body}x")
    # (BUVARP after pass: render refused "reading time 28.1 s outruns its narration 24.7 s") a page's reading time is
    # its shortened text's, never the full names' it was planned with
    rate = float(pol["vignettes"].get("readingWordsPerSecond") or 0)
    stale = [(p.get("seconds"), RP.page_seconds(p, rate, pol["vignettes"])) for ch in chs
             for p in (ch.get("still") or {}).get("pages") or [] if rate and "seconds" in p
             and p.get("seconds") != RP.page_seconds(p, rate, pol["vignettes"])]
    check("C2206", not stale, "a page's reading time is still the one planned for the full names", json.dumps(stale))
    check("C2206", n_shown <= 1, "the reel's page titles and frame labels show the long name in full more than once",
          f"full {n_shown}x")


# ------------------------------------------------------------------------------------------------ C2207 --

def c2207_dead_arms_live(tmp: Path):
    """Pass 4 found both arms' target strings absent on HEAD, so they ran nothing. Wrong answer: an arm's replacement
    string occurs other than once in the file it edits."""
    import test_reel_vignettes as V
    root = HARNESS.parent
    for arm in ("code-known-answer", "code-split-left-arm"):
        _stage, _target, edits = V.CODE_SABOTAGE[arm]
        counts = [(rel, (root / rel).read_text(encoding="utf-8").count(old)) for rel, old, _new in edits]
        check("C2207", all(n == 1 for _r, n in counts), f"arm {arm} targets a string not found once in current code",
              json.dumps(counts))


# ------------------------------------------------------------------------------------------------ C2208 --

def c2208_different_values_stay_different(tmp: Path):
    """Hroggar's field-clash line read 'BUVARP SE RE.esp has "01CE17:Skyrim.esm" and Environs ... has
    "013176:Skyrim.esm"'. Said in words, two values whose names were not read would both read "a record of Skyrim.esm
    whose name was not read": "A has X, B has X" for different values, a false sentence (Gate A review, answer 2).
    Wrong answers: no sentence-level record pass; two different values said the same; a value with a name read left as
    a form key; a label not said in words."""
    import evalreport as ER
    _pol, pri = shipped()
    fn = getattr(ER, "say_form_keys", None)
    if not check("C2208", fn is not None, "the report has no sentence pass that says form keys in words"):
        return
    words = pri["reader"]["prediction"]
    # two different classes whose names read the same ("Warrior"), as two plugins' versions can
    named = {"0D001A:Skyrim.esm": "Eisa Blackthorn", "01CE20:Skyrim.esm": "Class Warrior",
             "01CE17:Skyrim.esm": "Warrior", "013176:Skyrim.esm": "Warrior"}

    def sayer(fk):
        return (named.get(fk) or ER.said_record(fk, {}, {}, words, lambda w, f: None), None, None)
    s = ('In 3 of them Env.esp changed the field too, as on 0D001A:Skyrim.esm (Class), where BUVARP SE RE.esp has '
         '"01CE17:Skyrim.esm" and Env.esp has "013176:Skyrim.esm"; elsewhere one has "01CE20:Skyrim.esm" and the other '
         '"013176:Skyrim.esm".')
    got = fn({"text": s}, sayer, ["Skyrim.esm", "BUVARP SE RE.esp", "Env.esp"], words)["text"]
    check("C2208", '"01CE17:Skyrim.esm"' in got and got.count('"013176:Skyrim.esm"') == 2,
          "two different values that read alike are said the same", got)
    check("C2208", "Class Warrior" in got and "01CE20" not in got,
          "a value whose name was read is left as a form key", got)
    check("C2208", "Eisa Blackthorn (Class)" in got and "0D001A" not in got, "a record label is not said in words", got)
    s2 = "moved: 0A0001:Skyrim.esm (a tree TreeThicket01Snow) 41 u; 0A0002:Skyrim.esm (a tree TreeThicket01Snow) 95 u; 054405:Skyrim.esm."
    got2 = fn({"text": s2}, sayer, ["Skyrim.esm"], words)["text"]
    check("C2208", "a tree (Tree Thicket 01 Snow #1) 41 u" in got2 and "a tree (Tree Thicket 01 Snow #2) 95 u" in got2,
          "two records named alike read as one", got2)
    check("C2208", "054405:Skyrim.esm" in got2, "a record nothing names loses the one thing that tells it apart", got2)
    # (Cut Content after pass 1: 'has "... Door=A34CB2:Grand…"') a quoted value is never cut inside a form key
    v = ER.shown_value("TeleportDestination=[TeleportDestination] Door=A34CB2:Grand Solitude - The Walls of High King "
                       "Erling.esp", 60)
    check("C2208", not re.search(r"[0-9A-Fa-f]{6}:[^.]*…$", v), "a value is cut inside a form key", v)


CASES = {"C2200": c2200_said_record, "C2201": c2201_compiled_report_records, "C2202": c2202_one_line_per_check,
         "C2203": c2203_patch_verdict_short_in_reel, "C2204": c2204_unread_cell_says_why,
         "C2205": c2205_short_form_rule, "C2206": c2206_reel_says_long_name_once, "C2207": c2207_dead_arms_live,
         "C2208": c2208_different_values_stay_different}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--harness", default=None)
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="rrl-"))
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
