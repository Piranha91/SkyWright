"""Known answers for the navmesh repair rules and the relink plan (C2450-C2489), on navownprobe/NavRepairFixture.cs.

    python _geometry-harness/tests/test_navmesh_repair_rules.py

The fixture (`navownprobe --write-navrepair-fixture DIR`) is laid out as an MO2 instance and the REAL navownprobe runs on it:
once plainly, once with --edge-tolerance 8,64 (the border rules, independent of the vanilla-floor default), and once with
--repair-plan (the relink plan). Its table is the expectation; every case names the wrong answer it rejects.

    2450  control: the fixture's five plugins are written in order.
    2451  a link into a navmesh a later plugin deleted is EDGE-NAV, naming the deleter.
    2452  a navmesh's own links stop counting once a later plugin deletes it (no EDGE-IDX from SD).
    2453  a mod's deletion is a DELETED row, naming the deleter and the last version with geometry: TD and SD.
    2454  a mod's emptied navmesh is an EMPTIED row (TE, 2 -> 0).
    2455  control: the shipped game's deletion (Dawnguard.esm deletes TV) is no row.
    2456  the shipped game's deletion is counted on the emit (coverage "navmesh deletions", ShippedGame 1).
    2457  a link into the shipped game's deletion with both ends vanilla (SV -> TV) is floor: withheld, counted (FloorDangles 1).
    2460  an in-range link whose triangle has no edge on the source's edge is EDGE-NOMATCH, with its unique candidate (R2 1).
    2461  a link whose far triangle links elsewhere is EDGE-ONEWAY (R2 1 -> L2 0, while L2 0 names R2 0).
    2462  a link with no link back is EDGE-ONEWAY (L3 -> R3 0).
    2463  two candidate edges within the tolerance make the match ambiguous; the row names both and takes neither.
    2464  an EDGE-IDX row names its unique candidate in the same navmesh (R5 1).
    2465  an EDGE-IDX row names a candidate in another navmesh (R6n 0) and says the link moves to another navmesh.
    2466  an EDGE-IDX row with no candidate says so (L7).
    2467  control: a pair linked both ways and never changed (L1/R1) is no row of any kind.
    2468  a far edge in the same plan but 200 u higher (R8, lifted by RpA) is EDGE-NOMATCH both ways.
    2469  control: a far triangle whose slanted edge overlaps L9's edge fully but lies off, beside its true edge, is matched.
    2475  control: a ledge link pair (L10/R10, Types 2 and 1, 100 u apart in plan) is counted and not judged: no row.
    2470  a navmesh on its author's ground that a later LAND moved is NAV-GROUND (G1: Skyrim.esm x RpA.esp).
    2471  control: a deck above the ground (G2) is no row.
    2472  control: a navmesh whose owner saw the new ground (G3, moved with it) is no row.
    2473  a re-cut by a plugin that never saw another's LAND is NAV-GROUND (G4: RpD.esp x RpA.esp).
    2474  the author's ground is the owner's view, not the first LAND (G5: RpA.esp x RpD.esp).
    2480  plan: L5's link is re-pointed at R5 triangle 1, one edit.
    2481  plan: L6's link moves to R6n triangle 0, and the plan says the navmeshes adjacent to L6 change.
    2482  plan: L7's link is cut, and the plan says so.
    2483  plan: L4's link is refused by name as ambiguous (R4a and R4b), with no edit for it.
    2484  plan: L2's link is re-pointed at R2 triangle 1.
    2485  plan: R3 gains its link back to L3 triangle 0.

Probes come from this checkout's Release builds unless HC_NR_HARNESS names another harness folder. HC_NR_CASES (e.g.
"2463,2483") runs those cases only.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
PROBES = Path(os.environ.get("HC_NR_HARNESS") or HARNESS)
ONLY = {s.strip() for s in (os.environ.get("HC_NR_CASES") or "").split(",") if s.strip()}
FAILURES, PASSED = [], []
PLUGINS = ["Skyrim.esm", "Dawnguard.esm", "RpA.esp", "RpB.esp", "RpD.esp"]
TOL = "8,64"


def sk(hexid):
    return f"{hexid}:Skyrim.esm"


N = {"TD": sk("000960"), "SL": sk("000961"), "SD": sk("000962"), "TT": sk("000963"), "TE": sk("000964"), "TV": sk("000965"),
     "SV": sk("000966"), "R4a": "000801:RpA.esp", "R4b": "000802:RpA.esp", "R6n": "000803:RpA.esp",
     "G1": sk("000B01"), "G2": sk("000B02"), "G3": sk("000B03"), "G4": sk("000B04"), "G5": sk("000B05")}
for k in range(1, 11):
    N[f"L{k}"] = sk(f"000A{k:02X}")
    N[f"R{k}"] = sk(f"000A{0x10 + k:02X}")
NAME = {v.lower(): k for k, v in N.items()}


def check(name, fn):
    if ONLY and name.split()[0] not in ONLY:
        return
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().splitlines()[-1])
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def exe(probe):
    return PROBES / probe / "bin" / "Release" / "net9.0" / (probe + ".exe")


def run(argv):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)


class Lab:
    """The fixture as an MO2 instance; three runs of the real probe on the whole order."""

    def __init__(self, tmp: Path):
        keep = tmp / "fixture"
        self.write = run([exe("navownprobe"), "--write-navrepair-fixture", keep])
        if self.write.returncode != 0 or not all((keep / n).exists() for n in PLUGINS):
            raise RuntimeError(f"the fixture was not written (exit {self.write.returncode}): {self.write.stdout[-600:]} {self.write.stderr[-600:]}")
        inst, game = tmp / "instance", tmp / "game"
        prof = inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (inst / "overwrite").mkdir()
        (game / "Data").mkdir(parents=True)
        for n in PLUGINS:
            d = inst / "mods" / Path(n).stem
            d.mkdir(parents=True)
            shutil.copyfile(keep / n, d / n)
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in PLUGINS), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in PLUGINS), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + Path(n).stem + "\n" for n in reversed(PLUGINS)), encoding="utf-8")
        cand = tmp / "candidate.json"
        cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "nr", "BaseProfile": "P", "Plugins": PLUGINS,
                                    "Mods": [Path(n).stem for n in reversed(PLUGINS)], "Changes": []}), encoding="utf-8")
        common = ["--order", cand, "--instance", inst, "--game", game, "--profile", "P"]
        self.doc, self.log = {}, {}
        for arm, extra in (("plain", []), ("tol", ["--edge-tolerance", TOL]), ("plan", ["--edge-tolerance", TOL, "--repair-plan", tmp / "plan.json"])):
            emit = tmp / f"nr-{arm}.json"
            self.log[arm] = run([exe("navownprobe"), *common, "--emit", emit, *extra])
            self.doc[arm] = json.loads(emit.read_text(encoding="utf-8")) if self.log[arm].returncode == 0 and emit.exists() else None
        p = tmp / "plan.json"
        self.plan = json.loads(p.read_text(encoding="utf-8")) if self.log["plan"].returncode == 0 and p.exists() else None

    def why(self, arm):
        lg = self.log[arm]
        return "" if lg.returncode == 0 else f" (the {arm} run exited {lg.returncode}: {(lg.stdout + lg.stderr).strip()[-300:]})"

    def rows(self, arm="plain", kind=None):
        d = self.doc[arm]
        return [c for c in (d or {}).get("Conflicts") or [] if kind is None or c["Kind"] == kind]

    def coverage(self, arm="plain"):
        return (self.doc[arm] or {}).get("Coverage") or []


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="navmesh-repair-rules-")))
    return LAB


def nm(fk):
    return NAME.get((fk or "").lower(), fk)


def src(c):
    return nm(c["Necessary"][0]["FormKey"])


def alt(c, i=0):
    a = c.get("Alternative") or []
    return a[i] if len(a) > i else {}


def show(cs):
    return "; ".join(f"{c['Kind']} {src(c)}->{nm(alt(c).get('FormKey'))} tri {alt(c).get('Triangle')} [{c['A']} x {c['B']}] "
                     f"'{c['Summary'][:110]}'" for c in cs) or "none"


def one(arm, kind, s, t=None, tri=None):
    cs = [c for c in lab().rows(arm, kind) if src(c) == s and (t is None or nm(alt(c).get("FormKey")) == t)
          and (tri is None or alt(c).get("Triangle") == tri)]
    assert len(cs) == 1, f"expected one {kind} {s}->{t} tri {tri}, got {len(cs)}{lab().why(arm)}; {kind} rows: {show(lab().rows(arm, kind))}"
    return cs[0]


def t2450():
    """Rejects: a fixture that is not the order the table describes (every later answer would be about another order)."""
    paths = [Path(p.strip()).name for p in lab().write.stdout.splitlines() if p.strip()]
    assert paths == PLUGINS, paths
    assert lab().doc["plain"] is not None, f"the plain run did not emit{lab().why('plain')}"


def t2451():
    """Rejects: a deleted navmesh keeping its earlier triangle count, so a link into it reads valid (the blind spot)."""
    c = one("plain", "EDGE-NAV", "SL", "TD")
    assert c["B"] == "RpB.esp", f"the row should name the deleter RpB.esp as its second party: {show([c])}"
    assert "delete" in c["Summary"].lower(), c["Summary"]


def t2452():
    """Rejects: a deleted navmesh's stale links checked as if it stood (EDGE-IDX SD -> TT at 7 from RpA's version)."""
    cs = [c for c in lab().rows("plain") if src(c) == "SD" and c["Kind"].startswith(("EDGE", "DOOR"))]
    assert lab().doc["plain"] is not None and not cs, f"rows from the deleted SD: {show(cs)}"


def t2453():
    """Rejects: a mod's deletion printed but never emitted (no bundle or Workbench can see it)."""
    cs = lab().rows("plain", "DELETED")
    got = sorted((src(c), c["A"], c["B"], c["Necessary"][0].get("Physical")) for c in cs)
    assert got == [("SD", "RpB.esp", "RpA.esp", "Holds"), ("TD", "RpB.esp", "Skyrim.esm", "Holds")], \
        f"DELETED rows {got}{lab().why('plain')}"


def t2454():
    """Rejects: an emptied navmesh (a version with no triangles) left out of the emit."""
    cs = lab().rows("plain", "EMPTIED")
    got = sorted((src(c), c["A"], c["B"]) for c in cs)
    assert got == [("TE", "RpA.esp", "Skyrim.esm")], f"EMPTIED rows {got}{lab().why('plain')}"


def t2455():
    """Rejects: the shipped game's own deletion reported as a mod clash."""
    cs = [c for c in lab().rows("plain") if src(c) == "TV" or nm(alt(c).get("FormKey")) == "TV"]
    assert lab().doc["plain"] is not None and not cs, show(cs)


def t2456():
    """Rejects: a shipped-game deletion that leaves no trace on the emit (the floor must be counted, not silent)."""
    lines = [c for c in lab().coverage() if c.get("Population") == "navmesh deletions"]
    assert lines and (lines[0].get("SetAside") or {}).get("ShippedGame") == 1, f"coverage: {lines or lab().coverage()}"
    assert lines[0].get("Judged") == 3, f"judged deletions should be TD, SD and TE: {lines[0]}"


def t2457():
    """Rejects: a vanilla link into a vanilla deletion dropped unseen, or emitted as a mod clash."""
    lines = [c for c in lab().coverage() if c.get("Population") == "dangling-link checks"]
    assert lines and (lines[0].get("SetAside") or {}).get("FloorDangles") == 1, f"coverage: {lines}"


def t2460():
    """Rejects: an in-range index taken as a valid link although its triangle has no edge on the source's edge."""
    c = one("tol", "EDGE-NOMATCH", "L2", "R2", 0)
    assert f"{N['R2']} triangle 1" in c["Summary"], f"the row should name its unique candidate: {c['Summary']}"


def t2461():
    """Rejects: a matched link whose far triangle links back somewhere else read as two-way."""
    one("tol", "EDGE-ONEWAY", "R2", "L2", 0)


def t2462():
    """Rejects: a link the far side never returns read as two-way."""
    one("tol", "EDGE-ONEWAY", "L3", "R3", 0)


def t2463():
    """Rejects: a nearest-wins match (R4a is 2 u off, R4b 5 u): two candidates within the tolerance are ambiguous."""
    c = one("tol", "EDGE-NOMATCH", "L4", "R4", 0)
    s = c["Summary"]
    assert "ambiguous" in s.lower() and N["R4a"] in s and N["R4b"] in s, f"the row should name both candidates and take neither: {s}"
    assert len(c.get("Alternative") or []) == 1, f"an ambiguous row carries no candidate record: {c.get('Alternative')}"


def t2464():
    """Rejects: an EDGE-IDX row that says nothing of where the edge went (the relink has no target)."""
    c = one("plain", "EDGE-IDX", "L5", "R5", 3)
    assert f"{N['R5']} triangle 1" in c["Summary"], c["Summary"]
    assert alt(c, 1).get("Role") == "candidate" and nm(alt(c, 1).get("FormKey")) == "R5" and alt(c, 1).get("Triangle") == 1, c.get("Alternative")


def t2465():
    """Rejects: a candidate search that looks only in the navmesh the link names (RpA moved R6's border into R6n)."""
    c = one("plain", "EDGE-IDX", "L6", "R6", 2)
    assert f"{N['R6n']} triangle 0" in c["Summary"] and "another navmesh" in c["Summary"], c["Summary"]


def t2466():
    """Rejects: a row that invents a candidate where no edge meets the border."""
    c = one("plain", "EDGE-IDX", "L7", "R7", 1)
    assert "no matching edge" in c["Summary"].lower() and len(c.get("Alternative") or []) == 1, c["Summary"]


def t2467():
    """Rejects: a matched two-way border reported (the rule must be quiet where nothing changed)."""
    d = lab().doc["tol"]
    assert d is not None, f"no emit{lab().why('tol')}"
    cs = [c for c in lab().rows("tol") if {src(c), nm(alt(c).get("FormKey"))} & {"L1", "R1"}]
    assert not cs, show(cs)


def t2468():
    """Rejects: an edge match measured in plan only (R8's border edge is 200 u above L8's)."""
    one("tol", "EDGE-NOMATCH", "L8", "R8", 0)
    one("tol", "EDGE-NOMATCH", "R8", "L8", 0)


def t2469():
    """Rejects: the far edge chosen by its largest projected overlap (the slanted edge, up to 274 u off) over the true one."""
    d = lab().doc["tol"]
    assert d is not None, f"no emit{lab().why('tol')}"
    cs = [c for c in lab().rows("tol") if {src(c), nm(alt(c).get("FormKey"))} & {"L9", "R9"}]
    assert not cs, show(cs)


def t2475():
    """Rejects: a ledge link judged by its edge (in plan or in full), which calls the ledge EDGE-NOMATCH both ways."""
    for arm in ("plain", "tol"):
        d = lab().doc[arm]
        assert d is not None, f"no emit{lab().why(arm)}"
        cs = [c for c in lab().rows(arm) if {src(c), nm(alt(c).get("FormKey"))} & {"L10", "R10"}]
        assert not cs, f"{arm}: {show(cs)}"
        cov = [x for x in lab().coverage(arm) if x.get("Population") == "border links (geometry)"]
        assert cov and (cov[0].get("SetAside") or {}).get("LedgeLinks") == 2, f"{arm}: ledge links not counted as 2: {cov}"


def ground(s):
    return [c for c in lab().rows("plain", "NAV-GROUND") if src(c) == s]


def t2470():
    """Rejects: navmesh judged against nothing, or against the winning ground only (the author's ground is the measure)."""
    cs = ground("G1")
    assert len(cs) == 1 and (cs[0]["A"], cs[0]["B"]) == ("Skyrim.esm", "RpA.esp"), f"G1: {show(cs)}{lab().why('plain')}"
    m = re.search(r"(\d+) vert", cs[0]["Summary"])
    assert m and int(m.group(1)) == 5, f"all five of G1's vertices sat on the ground RpA moved 200 u: {cs[0]['Summary']}"


def t2471():
    """Rejects: navmesh compared with the ground although it never sat on it (a deck 600 u up)."""
    assert lab().doc["plain"] is not None and not ground("G2"), show(ground("G2"))


def t2472():
    """Rejects: a row where the navmesh's owner saw the new ground and moved with it."""
    assert lab().doc["plain"] is not None and not ground("G3"), show(ground("G3"))


def t2473():
    """Rejects: the authoring view taken from the LAND winner rather than the navmesh owner's masters."""
    cs = ground("G4")
    assert len(cs) == 1 and (cs[0]["A"], cs[0]["B"]) == ("RpD.esp", "RpA.esp"), f"G4: {show(cs)}{lab().why('plain')}"


def t2474():
    """Rejects: the author's ground taken as the first LAND (Skyrim's z 0) rather than the owner's view (RpA's 200)."""
    cs = ground("G5")
    assert len(cs) == 1 and (cs[0]["A"], cs[0]["B"]) == ("RpA.esp", "RpD.esp"), f"G5: {show(cs)}{lab().why('plain')}"


def plan_for(s):
    p = lab().plan
    assert p is not None, f"no repair plan{lab().why('plan')}"
    ps = [x for x in p.get("plans") or [] if nm(x.get("source")) == s]
    assert len(ps) == 1, f"expected one plan for {s}, got {len(ps)}: {[ (nm(x.get('source')), x.get('status')) for x in p.get('plans') or []]}"
    return ps[0]


def edits(pl):
    return [(nm(e.get("formid")), e.get("field_path"), e.get("op") or "Set", e.get("key"), e.get("value")) for e in pl.get("edits") or []]


def link_edits(pl, s):
    return [e for e in edits(pl) if e[0] == s and e[1].startswith("Data.EdgeLinks")]


def t2480():
    """Rejects: a plan that leaves L5's broken index, or rewrites more than the one link."""
    pl = plan_for("L5")
    assert pl.get("status") == "planned", pl
    assert link_edits(pl, "L5") == [("L5", "Data.EdgeLinks[0].TriangleIndex", "Set", None, "1")], edits(pl)


def t2481():
    """Rejects: a relink into another navmesh that does not say the adjacency changes (the NAVI question)."""
    pl = plan_for("L6")
    assert pl.get("status") == "planned" and pl.get("adjacencyChange") is True, pl
    got = sorted(link_edits(pl, "L6"))
    assert got == [("L6", "Data.EdgeLinks[0].Mesh", "Set", None, N["R6n"]), ("L6", "Data.EdgeLinks[0].TriangleIndex", "Set", None, "0")], got


def t2482():
    """Rejects: a broken link left in place when nothing matches, or cut without saying so."""
    pl = plan_for("L7")
    assert pl.get("status") == "cut" and "cut" in (pl.get("why") or "").lower(), pl
    got = edits(pl)
    assert ("L7", "Data.Triangles[0].EdgeLink_1_2", "Set", None, "-1") in got and ("L7", "Data.Triangles[0].Flags", "Remove", None, "EdgeLink_1_2") in got, got
    assert ("L7", "Data.EdgeLinks", "Remove", "0", None) in got, f"the table entry must go too, or the re-check still reads EDGE-IDX: {got}"


def t2483():
    """Rejects: a nearest-wins relink (R4a) where two candidates match within the tolerance."""
    pl = plan_for("L4")
    assert pl.get("status") == "refused" and "ambiguous" in (pl.get("why") or "").lower(), pl
    assert N["R4a"] in pl.get("why", "") and N["R4b"] in pl.get("why", "") and not edits(pl), pl


def t2484():
    """Rejects: an in-range mismatch left alone because its index is valid."""
    pl = plan_for("L2")
    assert pl.get("status") == "planned" and link_edits(pl, "L2") == [("L2", "Data.EdgeLinks[0].TriangleIndex", "Set", None, "1")], pl


def t2485():
    """Rejects: a one-way link left one-way (the relink writes links both ways)."""
    pl = plan_for("R3")
    assert pl.get("status") == "planned", pl
    got = edits(pl)
    assert ("R3", "Data.Triangles[0].EdgeLink_2_0", "Set", None, "0") in got and ("R3", "Data.Triangles[0].Flags", "Add", None, "EdgeLink_2_0") in got, got
    assert any(e[0] == "R3" and e[1] == "Data.EdgeLinks" and e[2] == "Add" for e in got), got


CASES = [
    ("2450 the fixture's five plugins in order", t2450),
    ("2451 a link into a deleted navmesh is EDGE-NAV naming the deleter", t2451),
    ("2452 a deleted navmesh's own links stop counting", t2452),
    ("2453 a mod's deletion is a DELETED row (TD, SD)", t2453),
    ("2454 a mod's emptied navmesh is an EMPTIED row (TE)", t2454),
    ("2455 control: the shipped game's deletion is no row", t2455),
    ("2456 the shipped game's deletion is counted on the emit", t2456),
    ("2457 a vanilla link into a vanilla deletion is floor, counted", t2457),
    ("2460 EDGE-NOMATCH L2->R2 0 with its candidate R2 1", t2460),
    ("2461 EDGE-ONEWAY R2->L2 0", t2461),
    ("2462 EDGE-ONEWAY L3->R3 0", t2462),
    ("2463 ambiguous candidates are named and neither taken", t2463),
    ("2464 EDGE-IDX L5 names its candidate R5 1", t2464),
    ("2465 EDGE-IDX L6 names a candidate in another navmesh", t2465),
    ("2466 EDGE-IDX L7 says no matching edge", t2466),
    ("2467 control: an unchanged two-way border is no row", t2467),
    ("2468 EDGE-NOMATCH both ways where heights differ (L8/R8)", t2468),
    ("2469 control: a slanted far edge does not hide the true one (L9/R9)", t2469),
    ("2475 control: a ledge link is counted, not judged (L10/R10)", t2475),
    ("2470 NAV-GROUND G1", t2470),
    ("2471 control: a deck is no row", t2471),
    ("2472 control: the owner saw the ground", t2472),
    ("2473 NAV-GROUND G4 (RpD x RpA)", t2473),
    ("2474 NAV-GROUND G5: the owner's view, not the first LAND", t2474),
    ("2480 plan: L5 re-pointed at R5 1", t2480),
    ("2481 plan: L6 moves to R6n 0, adjacency change said", t2481),
    ("2482 plan: L7 cut and said", t2482),
    ("2483 plan: L4 refused as ambiguous", t2483),
    ("2484 plan: L2 re-pointed at R2 1", t2484),
    ("2485 plan: R3 gains its link back", t2485),
]

if __name__ == "__main__":
    for name, fn in CASES:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
