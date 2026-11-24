"""Known answers for report and reel words: possessives, landscape by cell, long names (C2310-C2319).

    python tests/test_report_words.py [--only C2310,C2316] [--harness DIR]

No game and no renderer. The cases call the real reel_plan, evalreport, readerdoc and pluginread code with the SHIPPED
skill data, on the fixtures of test_reel_vignettes.py and test_evalreport_prediction.py. A function the fix adds is
looked up with getattr, so the base fails the case's own assertion, never by a crash.

    C2310  1   each of the seven reel strings that fill a mod name before "'s" (thingOwn, thingChanged, placementSplit,
               field, cell, patch.keeps), filled through the reel's filler, says "Interesting NPCs'" and "Lux's"
    C2311  1   the planned reel, with every mod named "... NPCs", never says "NPCs's" (thingOwn, placementSplit,
               patch.keeps reached)
    C2312  1   the report's filler (bind), its caption filler and the reader document's say "NPCs'" over a name ending
               in s, "X.esp's" over a file name, and leave a literal "Boss's" in a value as it is
    C2313  2   pluginread.record_cells reads the cell a landscape record is filed under from the group headers; a
               record filed under no cell answers None
    C2314  2   a landscape record is said as "the ground of <its cell>", and its form key leaves the sentence; a
               landscape whose cell nothing names keeps its form key
    C2315  3   short forms: a patch named after the evaluated mod is said by the rest ("BUVARP Patch"); two such patches
               with one rest, a patch not named after it, and R1's refusals get none; a patch's head blocks nothing
               (Gate A Q1); a plugin's mod name and stem never collide with each other
    C2316  3   the report says a long plugin file name in full once with the gloss, then by its short form, possessive
               included; link targets, attributes, code, image alt text and a quoted in-game name are left as
               rendered; the compiled report shortens and its evidence index keeps every name in full
    C2316h 3   (plugin-name-case fold-in 2) a heading never carries the gloss: a long name in a heading before its
               first body mention is said in full, the gloss goes on that body mention, and a heading after it says
               the short form (Markdown and HTML)
    C2317  3  the reel says a patch named after the evaluated mod in full once, then by the rest
    C2318  4   evalreport has no label_form_keys; test_reader_rules' fact holds on the current path
    C2319  5   a buried line names the object with the item's words (its numbers kept)

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import re
import string
import struct
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
NPCS = "Interesting NPCs"
GS = "Grand Solitude - The Walls of High King Erling"


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


def slots_of(t: str) -> list[str]:
    return [f for _l, f, _s, _c in string.Formatter().parse(t) if f]


# ------------------------------------------------------------------------------------------------ C2310 --

def c2310_seven_reel_strings(tmp: Path):
    """The navmesh-caption lane listed seven reel strings filled with mod names before "'s". Wrong answers: no filler;
    "Interesting NPCs's"; "Lux'" for a name not ending in s."""
    import reel_plan as RP
    _pol, pri = shipped()
    fill = getattr(RP, "fill", None)
    if not check("C2310", fill is not None, "the reel has no filler with a possessive rule"):
        return
    w = pri["reel"]["words"]
    strings = {"thingOwn": w["thingOwn"], "thingChanged": w["thingChanged"], "placementSplit": w["placementSplit"],
               "field": w["field"], "cell": w["cell"], "patch.keeps": w["patch"]["keeps"]}
    strings = {k: (v if isinstance(v, str) else v[0]) for k, v in strings.items()}
    for key, t in strings.items():
        poss = set(re.findall(r"\{(\w+)\}'s", t))
        check("C2310", bool(poss), f"{key} no longer fills a name before 's (the list is stale)", t)
        for name, want in ((NPCS, f"{NPCS}'"), ("Lux", "Lux's")):
            vals = {f: (name if f in poss else "X") for f in slots_of(t)}
            got = fill(t, **vals)
            check("C2310", f"{NPCS}'s" not in got and got.count(want) >= len(poss) if name == NPCS else
                  got.count("Lux's") >= len(poss), f"{key} fills a possessive wrongly over {name!r}", got)


# ------------------------------------------------------------------------------------------------ C2311 --

def c2311_planned_reel_has_no_s_s(tmp: Path):
    """Wrong answer: the planned reel says "NPCs's" for a mod named "... NPCs"."""
    import reel_plan as RP
    import test_reel_vignettes as V
    pol, pri = shipped()
    for case in ("listed", "keeps"):
        pres, names, masters, search, mods, _w = V.pool_fixture(case)
        mods = {k: ({**v, "name": v["name"] + " NPCs"} if isinstance(v, dict) else v) for k, v in mods.items()}
        got = RP.plan_reel(copy.deepcopy(pres), names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, [],
                           markers=V.MARKERS, switches=V.SWITCHES)
        said = " ".join(str(ch.get("narration") or "") for ch in got.get("chapters") or [])
        check("C2311", got.get("status") == "planned" and "NPCs'" in said, f"the reel ({case}) says no possessive "
              "of a mod named ... NPCs, so the case reaches no possessive template", str(got.get("reason")) + said[:300])
        check("C2311", "NPCs's" not in said, f"the reel ({case}) says \"NPCs's\"",
              "; ".join(m.group(0) for m in re.finditer(r".{40}NPCs's.{20}", said)))


# ------------------------------------------------------------------------------------------------ C2312 --

def c2312_report_fillers(tmp: Path):
    """Wrong answers: the report's filler says "NPCs's"; a file name loses its "'s"; a literal "Boss's" in a value
    changes; the reader document's filler or the caption filler says "NPCs's"."""
    import evalreport as ER
    import readerdoc as RD
    got = ER.bind("{plugin}'s version of {field}", {"plugin": NPCS, "field": "Boss's Chest"})
    check("C2312", got == f"{NPCS}' version of Boss's Chest", "the report's filler says s's, or changed a value", got)
    got = ER.bind("{plugin}'s version", {"plugin": "3DNPC.esp"})
    check("C2312", got == "3DNPC.esp's version", "the report's filler drops a file name's 's", got)
    got = ER.bind("{plugin}'s version", {})
    check("C2312", got == "not supplied's version", "the report's filler changed a missing value's words", got)
    cap = getattr(ER, "fill", None)
    check("C2312", cap is not None and cap("{mod}'s {object}", mod=NPCS, object="Altar") == f"{NPCS}' Altar",
          "the report's caption filler says s's", str(cap and cap("{mod}'s {object}", mod=NPCS, object="Altar")))
    got = RD.bind("{plugin}'s version", plugin=NPCS)
    check("C2312", got == f"{NPCS}' version", "the reader document's filler says s's", got)


# ------------------------------------------------------------------------------------------------ C2313 --

def rec(sig: bytes, fid: int, payload: bytes = b"") -> bytes:
    return sig + struct.pack("<IIIIHH", len(payload), 0, fid, 0, 44, 0) + payload


def grup(label: bytes | int, gtype: int, body: bytes) -> bytes:
    lab = label if isinstance(label, bytes) else struct.pack("<I", label)
    return b"GRUP" + struct.pack("<I", 24 + len(body)) + lab + struct.pack("<IIHH", gtype, 0, 44, 0) + body


def sub(sig: bytes, data: bytes) -> bytes:
    return sig + struct.pack("<H", len(data)) + data


def land_plugin(path: Path) -> Path:
    """Skyrim.esm-shaped: WRLD 00003C > block > sub-block > CELL 009CA2 > its temporary children > LAND 00ACA2;
    a LAND 00ACA3 in a top-level group, under no cell."""
    head = rec(b"TES4", 0, sub(b"HEDR", struct.pack("<fII", 1.7, 3, 0)))
    cell = rec(b"CELL", 0x009CA2, sub(b"DATA", b"\x00\x00"))
    kids = grup(0x009CA2, 6, grup(0x009CA2, 9, rec(b"LAND", 0x00ACA2, sub(b"DATA", b"\x00" * 4))))
    world = grup(b"WRLD", 0, rec(b"WRLD", 0x00003C, sub(b"EDID", b"Tamriel\x00"))
                 + grup(0x00003C, 1, grup(0, 4, grup(0, 5, cell + kids))))
    stray = grup(b"LAND", 0, rec(b"LAND", 0x00ACA3))
    path.write_bytes(head + world + stray)
    return path


def c2313_record_cells(tmp: Path):
    """Wrong answers: no read of a record's cell; a record answered with the wrong cell or type; a record filed under
    no cell given one."""
    import pluginread as PR
    fn = getattr(PR, "record_cells", None)
    if not check("C2313", fn is not None, "pluginread has no read of the cell a record is filed under"):
        return
    p = land_plugin(tmp / "Skyrim.esm")
    got = fn(p, ["00ACA2:Skyrim.esm", "00ACA3:Skyrim.esm", "00FFFF:Skyrim.esm"])
    check("C2313", got.get("00ACA2:Skyrim.esm") == {"type": "LAND", "cell": "009CA2:Skyrim.esm"},
          "a landscape record is not answered with its cell", json.dumps(got))
    check("C2313", got.get("00ACA3:Skyrim.esm") is None and got.get("00FFFF:Skyrim.esm") is None,
          "a record under no cell, or absent, is given a cell", json.dumps(got))


# ------------------------------------------------------------------------------------------------ C2314 --

def c2314_landscape_said_by_cell(tmp: Path):
    """BUVARP's report printed 00ACA2:Skyrim.esm four times. Wrong answers: the form key left in the sentence; the
    landscape said with no place; a landscape whose cell nothing names said as some generic ground."""
    import evalreport as ER
    pw = ER.load_priorities()["reader"]["prediction"]

    def reader(named=True):
        def r(what, fk):
            return {("recordCell", "00ACA2:Skyrim.esm"): {"type": "LAND", "cell": "009CA2:Skyrim.esm"},
                    ("cellWorld", "009CA2:Skyrim.esm"): "00003C:Skyrim.esm" if named else None,
                    ("cellGrid", "009CA2:Skyrim.esm"): [-9, -23] if named else None,
                    ("editorId", "00003C:Skyrim.esm"): "Tamriel"}.get((what, fk), "read" if what == "why" else None)
        return r
    said = ER.said_record("00ACA2:Skyrim.esm", {}, {}, pw, reader())
    check("C2314", said.startswith("the ground of ") and "Tamriel" in said and "-23" in said,
          "a landscape record is not said by its cell", said)
    sayer = lambda fk: (ER.said_record(fk, {}, {}, pw, reader()), None, None)  # noqa: E731
    line = ER.say_form_keys({"line": "the base game's value of VertexHeightMap, as on 00ACA2:Skyrim.esm (Layers)"},
                            sayer, ["Skyrim.esm"], pw)["line"]
    check("C2314", "00ACA2" not in line and f"as on {said} (Layers)" in line,
          "the sentence keeps the landscape's form key", line)
    sayer2 = lambda fk: (ER.said_record(fk, {}, {}, pw, reader(False)), None, None)  # noqa: E731
    line2 = ER.say_form_keys({"line": "as on 00ACA2:Skyrim.esm (Layers)"}, sayer2, ["Skyrim.esm"], pw)["line"]
    check("C2314", "00ACA2:Skyrim.esm" in line2, "a landscape whose cell nothing names loses the one thing that tells it "
          "from another", line2)


# ------------------------------------------------------------------------------------------------ C2315 --

def c2315_short_form_rules(tmp: Path):
    """Hroggar's reel said 'Environs - Hroggars House - BUVARP Patch' and '... - Lux Patch' five times. Wrong answers:
    no rest for a patch named after the evaluated mod; a rest two patches share; a rest for a patch named after another
    mod; a patch's head blocking another name (Gate A Q1); a plugin's own mod name blocking its stem."""
    import reel_plan as RP
    _pol, pri = shipped()
    w = pri["reel"]["words"]
    subj = ["Environs - Hroggars House", "Environs - Hroggar's House"]
    bp, lp = "Environs - Hroggars House - BUVARP Patch", "Environs - Hroggars House - Lux Patch"
    try:
        got = RP.short_mod_names([*subj, bp, lp, "BUVARP RE", "Lux"], w, subject=subj)
    except TypeError as e:
        check("C2315", False, "short_mod_names knows no evaluated mod, so no patch named after it is shortened", str(e))
        return
    check("C2315", got.get(bp) == "BUVARP Patch" and got.get(lp) == "Lux Patch",
          "a patch named after the evaluated mod is not said by the rest", json.dumps(got))
    two = RP.short_mod_names([*subj, bp, "Environs - Hroggars House: BUVARP Patch"], w, subject=subj)
    check("C2315", bp not in two, "two patches with one rest share a short form", json.dumps(two))
    other = RP.short_mod_names(["Clockwork", "Lux - BUVARP patch", bp], w, subject=["Clockwork"])
    check("C2315", not other, "a patch not named after the evaluated mod is shortened", json.dumps(other))
    q1 = RP.short_mod_names([GS, "Grand Solitude - Skyrim Cut Content Restoration patch", "Skyrim Cut Content Restoration"],
                            w, subject=["Skyrim Cut Content Restoration", "CutContentRestoration"])
    check("C2315", q1 == {GS: "Grand Solitude"}, "a patch's head blocks another name, or the patch is shortened to "
          "its head", json.dumps(q1))
    long_stem, long_name = "Foo Bar - The Very Long Name Of A Mod Here", "Foo Bar - The Very Long Name Of A Mod Here SE"
    same = RP.short_mod_names([long_stem, long_name], w, same=[{long_stem, long_name}])
    check("C2315", same.get(long_stem) == "Foo Bar" and same.get(long_name) == "Foo Bar",
          "a plugin's mod name and its stem block each other", json.dumps(same))
    apart = RP.short_mod_names([long_stem, long_name], w)
    check("C2315", not apart, "two different plugins share a short form", json.dumps(apart))


# ------------------------------------------------------------------------------------------------ C2316 --

def c2316_report_short_forms(tmp: Path):
    """Clockwork's report printed the 46-character file name 179 times. Wrong answers: no report pass; the full name
    said again after the first mention; no gloss; a link target, attribute, code, alt text or quoted in-game name
    rewritten; a possessive left as "Grand Solitude.esp's"; the evidence index shortened."""
    import evalreport as ER
    fn = getattr(ER, "say_report_short_forms", None)
    if not check("C2316", fn is not None, "the report has no short-form pass"):
        return
    short = {GS: "Grand Solitude"}
    gloss = ER.load_priorities()["reel"]["words"]["shortForm"]["reportFirst"]
    f = f"{GS}.esp"
    md = (f"Conflicts with {f}'s cells and with {f}. Then {f} ([evidence](evidence-index.md#{GS.replace(' ', '%20')})) "
          f"and `{f}` and ![{f}](media/x.png) and the inn \"{GS} Inn\" and {f}'s value; {GS} too.")
    got = fn(md, short, gloss)
    want = (f"Conflicts with {f}'s cells and with {f} (Grand Solitude below). Then Grand Solitude ([evidence]"
            f"(evidence-index.md#{GS.replace(' ', '%20')})) and `{f}` and ![{f}](media/x.png) and the inn \"{GS} Inn\" "
            f"and Grand Solitude's value; Grand Solitude too.")
    check("C2316", got == want, "the report's long name is not said in full once with its gloss and short after, or "
          "a link, code, alt text or quoted in-game name was rewritten", got)
    # a possessive said again for the short form: a full name ending in s whose short form does not, and the reverse
    rivers, docks = "Foo Bar - The Very Long Name Of Many Rivers", "Old Docks - The Very Long Name Of A Harbour"
    pos = fn(f"With {rivers}.esp and {docks}.esp. Then {rivers}' cells and {docks}'s cells.",
             {rivers: "Foo Bar", docks: "Old Docks"}, gloss)
    check("C2316", pos.endswith("Then Foo Bar's cells and Old Docks' cells."),
          "a possessive after a shortened name is not said again for its short form", pos)
    html_ = (f"<html><head><title>{f}</title></head><body><p>With {f}.</p><a href=\"{f}\" title=\"{f}\">{f}</a>"
             f"<p>Then {f}&#39;s cells and &quot;{GS} Inn&quot;.</p></body></html>")
    goth = fn(html_, short, gloss, as_html=True)
    wanth = (f"<html><head><title>{f}</title></head><body><p>With {f} (Grand Solitude below).</p><a href=\"{f}\" "
             f"title=\"{f}\">Grand Solitude</a><p>Then Grand Solitude's cells and \"{GS} Inn\".</p></body></html>")
    check("C2316", goth == wanth, "the HTML pass rewrote a tag or attribute, said a quoted in-game name short, or "
          "missed a mention", goth)
    # the report says short only a plugin whose file name is long: a short patch file named after the evaluated mod stays
    # in full there (a modder looks it up), while the long one is said by its rest
    plan_ = {"scope": {"targets": [{"name": "Hroggars House.esp"}],
                       "arms": [{"plugins": ["Skyrim.esm", "Hroggars House.esp", "Hroggars House - Lux patch.esp",
                                             "Hroggars House - The Very Long Name Of A BUVARP patch.esp"]}]},
             "reader": {"names": {"mods": {}}}}
    rs = ER.report_short_forms(plan_, ER.load_priorities())
    check("C2316", rs == {"Hroggars House - The Very Long Name Of A BUVARP patch": "The Very Long Name Of A BUVARP patch"},
          "the report shortens a short file name, or not a long one named after the evaluated mod", json.dumps(rs))
    import test_evalreport_prediction as P
    a_, b_ = f, "Clockwork.esp"
    arms = [P.arm("baseline", [P.BASE, a_, b_], kind="baseline"), P.arm("no-b", [P.BASE, a_])]
    rows = [P.row("CONTACT", a_, b_, f"00080{i}:{b_}") for i in (1, 2)]
    plan, md2, _h, idx = P.compile_and_render(arms, {(P.CONTACT, "baseline"): rows, (P.CONTACT, "no-b"): []}, [b_],
                                              want_index=True)
    check("C2316", md2.count(f) == 1 and "Grand Solitude below" in md2 and "Grand Solitude" in md2.replace(f, ""),
          "the compiled report says the long file name in full more than once", f"full {md2.count(f)}x")
    check("C2316", idx.count(f) >= 1 and "Grand Solitude below" not in idx,
          "the evidence index is shortened", f"index full {idx.count(f)}x")


def c2316h_gloss_not_in_headings(tmp: Path):
    """Hroggar's H1 read "... BUVARP Patch.esp (BUVARP Patch below) ...". Wrong answers: the gloss in a heading; the
    first body mention said short with no gloss (the heading took it); a heading's full name shortened before the
    reader met the gloss; a later heading still in full."""
    import evalreport as ER
    fn = getattr(ER, "say_report_short_forms", None)
    if not check("C2316h", fn is not None, "the report has no short-form pass"):
        return
    short = {GS: "Grand Solitude"}
    gloss = ER.load_priorities()["reel"]["words"]["shortForm"]["reportFirst"]
    f = f"{GS}.esp"
    md = f"# Clashes of {f}\n\nThe pairs with {f} are below.\n\n## More of {f}\n\nAnd {f} again.\n"
    want = (f"# Clashes of {f}\n\nThe pairs with {f} (Grand Solitude below) are below.\n\n## More of Grand Solitude\n\n"
            f"And Grand Solitude again.\n")
    got = fn(md, short, gloss)
    check("C2316h", got == want, "a Markdown heading carries the gloss, or the body's first mention lost it", got)
    html_ = (f"<html><body><h1 class=\"t\">Clashes of {f}</h1><p>The pairs with {f}.</p><h2>More of {f}</h2>"
             f"<p>And {f}.</p></body></html>")
    wanth = (f"<html><body><h1 class=\"t\">Clashes of {f}</h1><p>The pairs with {f} (Grand Solitude below).</p>"
             f"<h2>More of Grand Solitude</h2><p>And Grand Solitude.</p></body></html>")
    goth = fn(html_, short, gloss, as_html=True)
    check("C2316h", goth == wanth, "an HTML heading carries the gloss, or the body's first mention lost it", goth)


# ------------------------------------------------------------------------------------------------ C2317 --

def c2317_reel_patch_named_after_subject(tmp: Path):
    """Wrong answer: the reel says a patch named after the evaluated mod in full every time, or never by its rest."""
    import reel_plan as RP
    import test_reel_vignettes as V
    pol, pri = shipped()
    pres, names, masters, search, mods = V.synthetic()
    patch = "Alpha - Target patch.esp"
    full = "Target Mod - Alpha Patch"
    names = copy.deepcopy(names)
    names.setdefault("mods", {})[patch] = {"name": full, "source": "declared"}
    got = RP.plan_reel(copy.deepcopy(pres), names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, [],
                       markers=V.MARKERS, switches=V.SWITCHES)
    said = " ".join(str(ch.get("narration") or "") for ch in got.get("chapters") or [])
    check("C2317", said.count(full) == 1 and "Alpha Patch" in said.replace(full, ""),
          "the reel says the patch named after the evaluated mod in full more than once, or never by its rest",
          f"full {said.count(full)}x; {said[:400]}")


# ------------------------------------------------------------------------------------------------ C2318 --

def c2318_label_form_keys_retired(tmp: Path):
    """Wrong answers: label_form_keys still in evalreport; the reader-rules fact no longer holds on the current path."""
    import evalreport as ER
    check("C2318", not hasattr(ER, "label_form_keys"), "evalreport still carries label_form_keys")
    import test_reader_rules as T
    ok, detail = T.case_records_and_places_said_by_name()
    check("C2318", ok, "a record is not said by name on the current path", detail)


# ------------------------------------------------------------------------------------------------ C2319 --

def c2319_buried_line_one_name(tmp: Path):
    """Hroggar's line named 'Mine Scaffold Base 0 Sided 01' and then 'Mine Scaffold Base Sided'. Wrong answer: the
    buried line drops the numbers the item keeps."""
    import evalreport as ER
    names = {"refs": {"000801:Skyrim.esm": {"base": "000901:Skyrim.esm"}, "000802:Skyrim.esm": {"base": "000902:Skyrim.esm"}},
             "bases": {"000901:Skyrim.esm": {"editorId": "LoadScreenIronShieldHeavy", "type": "Static"},
                       "000902:Skyrim.esm": {"editorId": "MineScaffoldBase0Sided01", "type": "Static"}},
             "objects": {}}
    plan = {"reader": {"prediction": {"record": [{"findingId": "F-1", "formKey": "000801:Skyrim.esm",
                                                  "other": ["000802:Skyrim.esm"]}],
                                      "items": [{"findingIds": ["F-1"]}]}}}
    captures = [{"buried": [{"formKey": "000801:Skyrim.esm", "partner": "000802:Skyrim.esm", "stations": 7}]}]
    import reel_plan as RP
    real = RP.clash_buried
    RP.clash_buried = lambda fks, caps: {"formKey": "000801:Skyrim.esm", "partner": "000802:Skyrim.esm", "stations": 7}
    try:
        words = ER.load_priorities()["reader"]["figureCaptions"]
        ER.buried_lines(plan, captures, names, words)
    finally:
        RP.clash_buried = real
    lines = " ".join(plan["reader"]["prediction"]["items"][0].get("buriedLines") or [])
    item_name = ER.record_object_name("000802:Skyrim.esm", names, ER.load_priorities()["reader"]["prediction"])
    check("C2319", "Mine Scaffold Base 0 Sided 01" in lines and "Mine Scaffold Base 0 Sided 01" in str(item_name),
          "the buried line names the object differently from its item", f"line {lines!r}; item {item_name!r}")


CASES = {"C2310": c2310_seven_reel_strings, "C2311": c2311_planned_reel_has_no_s_s, "C2312": c2312_report_fillers,
         "C2313": c2313_record_cells, "C2314": c2314_landscape_said_by_cell, "C2315": c2315_short_form_rules,
         "C2316": c2316_report_short_forms, "C2316h": c2316h_gloss_not_in_headings, "C2317": c2317_reel_patch_named_after_subject,
         "C2318": c2318_label_form_keys_retired, "C2319": c2319_buried_line_one_name}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--harness", default=None)
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="rw-"))
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
