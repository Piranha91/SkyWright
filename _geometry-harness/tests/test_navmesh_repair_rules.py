"""Known answers for the navmesh repair rules and the relink plan (C2450-C2489), on navownprobe/NavRepairFixture.cs.

    python _geometry-harness/tests/test_navmesh_repair_rules.py

The fixture (`navownprobe --write-navrepair-fixture DIR`) is laid out as an MO2 instance and the REAL navownprobe runs on it:
once plainly, once with --edge-tolerance 8,64 (the border rules, independent of the vanilla-floor default), and once with
--repair-plan (the relink plan). Its table is the expectation; every case names the wrong answer it rejects.

    2450  control: the fixture's five plugins are written in order.
    2451  a link into a navmesh a later plugin deleted is EDGE-NAV, naming the deleter.
    2476  two links of one navmesh into a deletion are two EDGE-NAV rows, each naming its source and target triangle, with
          distinct row identities (Gate B review finding 5).
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
    2477  a far edge 12 u off in plan (L12/R12, the 8-16 u band) is reported apart as unproven in game: no row, counted, listed
          in the plan and offered no repair (Gate B review finding 2).
    2478  the same for a far edge 100 u off in height (L13/R13, the 63-128 u band); 200 u (L8/R8) stays a finding.
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
    2486  plan: L11 is re-pointed at R11 triangle 1, and R11 triangle 1 gains its link back (the relink writes both ways).
    2487  ground plan: G1's five vertices follow the ground up 200 u, and the box's z becomes 200 to 200; x and y stay.
    2488  ground plan: G6's four vertices on the ground move; the one up on a rock, off its author's ground, does not.
    2489  ground plan: G4 (RpD's re-cut, blind to RpA's LAND) moves to 200 and G5 to 400; G2 (a deck) and G3 have no plan.
    2490-2493  the plan on in-memory navmeshes (navownprobe --navrepair-selftest): a cut renumbers a later table entry; a
          height move that would leave a matched link 200 u apart is refused; the same move with the neighbour moving too
          is planned; a vertex that cannot be told refuses its navmesh.
    2494  the write: the plan's ops through housecarl_apply (this checkout's server, out_path) make one new patch holding
          exactly the planned navmeshes.
    2495  the read-back: every planned navmesh in the patch is its winner with exactly the plan applied, and every record
          the patch carries (its CELL and WRLD containers) is its winner's.
    2498  the read-back refuses a carried container that is not the winner's: the plan's ops alone carry the border cell
          (0,0) from Skyrim.esm, the navmeshes' winner, while RpD.esp wins that cell (review of Gate C part 1, finding 1).
    2499  the forward the read-back names to fix it is exactly that cell from RpD.esp; after it, the cell is RpD's.
    2496  the read-back's arm: a patch written with one unplanned edit (L5's vertex 1 moved 1 u) fails it, naming the field.
    2503  plan, NAVI: L6's relink into R6n adds R6n to L6's NAVI entry and L6 to R6n's, both ways. L6's entry is stated only
          by Skyrim.esm, so the patch adds it whole from there (its door, island data and old neighbour kept); R6n's is in
          RpA.esp's list, the NAVI winner, and gains L6 in place. Relinks within one navmesh change no entry.
    2504  the read-back holds the patch's NAVI to RpA.esp's with exactly those two entries changed, and holds.
    2505  the read-back's NAVI arm: a patch whose NAVI also changes an entry the plan leaves (R5's) fails, naming that entry.
    2506  self-test: a relink whose new neighbour no plugin's NAVI states is refused by name with its link back; with both
          entries stated it names each navmesh in the other's entry.
    2497  the re-check, by the same rule on the order with the patch added: relinked links and the ground rows go
          Holds -> Gone (repaired), L7's and SL's cut links are reported as cut, L4's as refused, and no border, link or
          ground row is created.
    2551  self-test (RowsAt, navownprobe's --base summary): a row whose triangles a patch renumbered but did not move is
          unchanged, matched by where they stand (ConflictRecord.At), not by index
    2552  self-test: a row whose triangle moved counts gone, a new one new; a row on another navmesh is not counted
    2553  self-test: a base emit whose link rows carry no place is refused in one sentence
    2554  the fixture's emit: an EDGE-NAV, NOMATCH or ONEWAY row's source, and a NOMATCH or ONEWAY row's far end, names its
          triangle and where it stands; one triangle one place, one place one triangle; an EDGE-IDX row names no place
    2555  self-test: a row listing several navmeshes (CLOBBERED) keys on the navmesh asked about and its plugin pair; a
          NAV-GROUND row whose owner became the patch is unchanged
    2590  self-test: a border link inside the unproven height band is EDGE-ONEWAY, a finding, when the far triangle does
          not link back (the RV station: 3 of 3 forward, 0 of 3 back in game); with the link back it stays unproven
    2591  self-test: the plan writes no link back for a band one-way row: refused by name
    2592  self-test: a link back a lower version authored (its edge within the band) is restored, "restored from" it, "in
          the band"; one linking from beyond the band is not taken
    2593  self-test: a follow-the-ground plan on a NAVI island is refused by name until R7
    2594  self-test: a one-way refusal whose match joins its own triangle names it, never an empty target
    2563  navedit on the fixture: use_version of G4 from Skyrim.esm masters RpD.esp, whose version it supersedes (read from the
          written header), the read-back says so, and no staging folder is left
    2566  self-test: NAVI linked doors: added in place on the winner's entry with the base game's Unknown; a door already linked,
          an unstated navmesh, and a whole interior entry (the write engine's compose gap) are refused by name

Probes come from this checkout's Release builds unless HC_NR_HARNESS names another harness folder; the houseCARL server
and generator from this checkout's src (HC_NR_SRC another). HC_NR_CASES (e.g. "2463,2483") runs those cases only.
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
SRC = Path(os.environ.get("HC_NR_SRC") or HARNESS.parent / "external" / "houseCARL" / "src")
sys.path.insert(0, str(HARNESS))
ONLY = {s.strip() for s in (os.environ.get("HC_NR_CASES") or "").split(",") if s.strip()}
FAILURES, PASSED = [], []
PLUGINS = ["Skyrim.esm", "Dawnguard.esm", "RpA.esp", "RpB.esp", "RpD.esp"]
TOL = "8,64"


def sk(hexid):
    return f"{hexid}:Skyrim.esm"


N = {"TD": sk("000960"), "SL": sk("000961"), "SD": sk("000962"), "TT": sk("000963"), "TE": sk("000964"), "TV": sk("000965"),
     "SV": sk("000966"), "NV": sk("000C00"), "DR": sk("000C10"), "R4a": "000801:RpA.esp", "R4b": "000802:RpA.esp", "R6n": "000803:RpA.esp",
     "G1": sk("000B01"), "G2": sk("000B02"), "G3": sk("000B03"), "G4": sk("000B04"), "G5": sk("000B05"), "G6": sk("000B06")}
for k in range(1, 14):
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
        self.tmp, self.inst, self.game, self.cand, self.common = tmp, inst, game, cand, common
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
    c = one("plain", "EDGE-NAV", "SL", "TD", 0)
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


def t2476():
    """Rejects: EDGE-NAV rows keyed on their two navmeshes only, so SL's two links into TD read as one row identity, and a
    row that cannot say which source triangle a relink would edit."""
    import ordersweep
    sl = [c for c in lab().rows("plain", "EDGE-NAV") if src(c) == "SL" and nm(alt(c).get("FormKey")) == "TD"]
    got = sorted((c["Necessary"][0].get("Triangle"), alt(c).get("Triangle")) for c in sl)
    assert got == [(0, 0), (1, 1)], f"SL's EDGE-NAV rows as (source, target triangle): {got}; {show(sl)}{lab().why('plain')}"
    ids = {ordersweep.row_identity(c["Kind"], c["Necessary"][0]["FormKey"], alt(c)["FormKey"], c["A"], alt(c).get("Triangle")) for c in sl}
    assert len(ids) == 2, f"two links, {len(ids)} row identities: {ids}"


def unproven(*names):
    p = lab().plan
    assert p is not None, f"no repair plan{lab().why('plan')}"
    return {(nm(u["source"]), nm(u["target"])) for u in p.get("unproven") or [] if nm(u["source"]) in names}


def no_rows(names):
    for arm in ("plain", "tol"):
        assert lab().doc[arm] is not None, f"no emit{lab().why(arm)}"
        cs = [c for c in lab().rows(arm) if {src(c), nm(alt(c).get("FormKey"))} & set(names)]
        assert not cs, f"{arm}: {show(cs)}"
    assert not [p for p in lab().plan.get("plans") or [] if nm(p.get("source")) in names], "a repair is offered for an unproven link"


def t2477():
    """Rejects: a link 12 u off in plan (inside the band the fit added, unwalked in game) counted as broken or offered a
    repair, and one dropped unseen."""
    no_rows({"L12", "R12"})
    cov = [x for x in lab().coverage("tol") if x.get("Population") == "border links (geometry)"]
    assert cov and (cov[0].get("SetAside") or {}).get("UnprovenInGame") == 4, f"unproven links not counted as 4 (L12, R12, L13, R13): {cov}"
    assert unproven("L12", "R12") == {("L12", "R12"), ("R12", "L12")}, unproven("L12", "R12")


def t2478():
    """Rejects: a link 100 u off in height (the 63-128 u band) counted as broken; and a band with no upper edge (200 u is a finding)."""
    no_rows({"L13", "R13"})
    assert unproven("L13", "R13") == {("L13", "R13"), ("R13", "L13")}, unproven("L13", "R13")
    one("tol", "EDGE-NOMATCH", "L8", "R8", 0)


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



def t2486():
    """Rejects: a relink written one way only (R11 triangle 1 left linking nothing back)."""
    pl = plan_for("L11")
    assert pl.get("status") == "planned" and link_edits(pl, "L11") == [("L11", "Data.EdgeLinks[0].TriangleIndex", "Set", None, "1")], pl
    r = plan_for("R11")
    got = edits(r)
    assert ("R11", "Data.Triangles[1].EdgeLink_2_0", "Set", None, "0") in got and ("R11", "Data.Triangles[1].Flags", "Add", None, "EdgeLink_2_0") in got, got
    add = [e for e in r.get("edits") or [] if e["field_path"] == "Data.EdgeLinks" and e.get("op") == "Add"]
    f = (add[0].get("compose") or {}).get("fields") or {} if len(add) == 1 else {}
    assert f.get("Mesh", "").lower() == N["L11"].lower() and f.get("TriangleIndex") == "0", f"R11's new table entry: {add}"


def vertex_edits(pl):
    return sorted((e[3], e[4]) for e in edits(pl) if e[1] == "Data.Vertices" and e[2] == "SetAtIndex")


def t2487():
    """Rejects: vertices moved with the box's z left at the old ground (the grid and the game read the box)."""
    pl = plan_for("G1")
    assert pl.get("status") == "planned" and pl.get("class") == "ground", pl
    assert vertex_edits(pl) == [("0", "12800,500,200"), ("1", "13100,500,200"), ("2", "13100,800,200"), ("3", "12800,800,200"), ("4", "12600,650,200")], vertex_edits(pl)
    got = edits(pl)
    assert ("G1", "Data.Min", "Set", None, "12600,500,200") in got and ("G1", "Data.Max", "Set", None, "13100,800,200") in got, got


def t2488():
    """Rejects: every vertex of the navmesh moved, including one that never sat on its author's ground (a rock 300 u up)."""
    pl = plan_for("G6")
    assert pl.get("status") == "planned", pl
    assert vertex_edits(pl) == [("0", "14600,500,200"), ("1", "14900,500,200"), ("2", "14900,800,200"), ("3", "14600,800,200")], vertex_edits(pl)
    assert ("G6", "Data.Min", "Set", None, "14400,500,200") in edits(pl), edits(pl)


def t2489():
    """Rejects: the move taken from the first LAND or the author's own, and a plan for a navmesh that is no row."""
    assert {v for _, v in vertex_edits(plan_for("G4"))} == {"17600,500,200", "17900,500,200", "17900,800,200", "17600,800,200", "17400,650,200", "17750,950,200"}, vertex_edits(plan_for("G4"))
    assert all(v.endswith(",400") for _, v in vertex_edits(plan_for("G5"))) and len(vertex_edits(plan_for("G5"))) == 5, vertex_edits(plan_for("G5"))
    assert not [p for p in lab().plan.get("plans") or [] if nm(p.get("source")) in ("G2", "G3")], "a plan for G2 or G3"


SELF = None


def selftest(name):
    global SELF
    if SELF is None:
        SELF = run([exe("navownprobe"), "--navrepair-selftest"])
    lines = [l for l in SELF.stdout.splitlines() if l.startswith((f"ok {name} ", f"FAIL {name} "))]
    assert lines and lines[0].startswith("ok "), lines or f"no line for {name} (exit {SELF.returncode}): {SELF.stdout[-400:]} {SELF.stderr[-300:]}"


def t2503():
    """Rejects: a relink into another navmesh with no NAVI change, a change one way only, L6's entry taken from the winner
    (which does not state it) or rebuilt without its door, island and old neighbour, and NAVI touched by a same-mesh relink."""
    p = lab().plan
    assert p is not None, f"no repair plan{lab().why('plan')}"
    nv = p.get("navi") or {}
    assert nv.get("record") == N["NV"] and (nv.get("winner") or {}).get("plugin") == "RpA.esp", nv
    got = {nm(e["navmesh"]): (e["from"]["plugin"], e["winnerIndex"], [nm(x) for x in e["add"]]) for e in nv.get("entries") or []}
    want = {"L6": ("Skyrim.esm", None, ["R6n"]), "R6n": ("RpA.esp", 1, ["L6"])}
    assert got == want, f"NAVI entries {got}, want {want}; why {nv.get('why')}"
    ops = [o for o in p["ops"] if o["formid"] == N["NV"]]
    inplace = [(o["field_path"], o["op"], nm(o.get("value"))) for o in ops if "compose" not in o]
    assert inplace == [("MapInfos[1].MergedTo", "Add", "L6")], inplace
    whole = [o for o in ops if "compose" in o]
    assert len(whole) == 1 and whole[0]["field_path"] == "MapInfos" and whole[0]["op"] == "Add", whole
    c = whole[0]["compose"]
    sets = [(x["path"], x["verb"], nm(x.get("value")) if x.get("value") else (x.get("compose") or {}).get("type")) for x in c["sets"]]
    assert c["type"] == "NavigationMapInfo" and nm(c["fields"]["NavigationMesh"]) == "L6" and c["fields"]["Unknown2"] == str(0x1234), c["fields"]
    assert sets == [("MergedTo", "Add", "R6"), ("MergedTo", "Add", "R6n"), ("LinkedDoors", "Add", "LinkedDoor"),
                    ("Parent", "Set", "NavigationMapInfoWorldParent"), ("Island", "Set", "IslandData")], sets


def t2504():
    """Rejects: a read-back that does not check the patch's NAVI, or a written NAVI other than the winner's plus the plan."""
    f = flow()
    assert "error" not in f, f["error"]
    doc = f["NavRepair.readback.doc"] or {}
    nv = [c for c in doc.get("carried") or [] if c["type"] == "NavigationMeshInfoMap"]
    assert len(nv) == 1 and nv[0]["holds"] is True and nv[0].get("planned") is True and nv[0]["winner"] == "RpA.esp" and nv[0]["compared"] == 4,         f"the NAVI read-back: {nv}; apply said {f['NavRepair.answer'][:800]}"


def t2505():
    """Rejects: a NAVI read-back that misses a change to an entry the plan leaves alone (R5's, entry 0 of RpA's list)."""
    f = flow()
    assert "error" not in f, f["error"]
    rb, doc = f["NavRepairNaviArm.readback"], f["NavRepairNaviArm.readback.doc"] or {}
    nv = [c for c in doc.get("carried") or [] if c["type"] == "NavigationMeshInfoMap"]
    assert rb.returncode == 1 and len(nv) == 1 and nv[0]["holds"] is False and any(d.startswith("NAVI entry 0 (" + N["R5"]) for d in nv[0]["differences"]),         f"the NAVI arm's read-back exit {rb.returncode}: {nv}; apply said {f['NavRepairNaviArm.answer'][:500]}"


def t2506():
    """Rejects: a relink written although the coarse map cannot be told of its new neighbour, or NAVI ops one way only."""
    selftest("2506")


def t2554():
    """Rejects: an emit whose link rows do not say where their triangles stand (ConflictRecord.At), or says it
    inconsistently."""
    import re
    rows = [c for c in lab().rows() if c["Kind"] in ("EDGE-IDX", "EDGE-NAV", "EDGE-NOMATCH", "EDGE-ONEWAY")]
    assert rows, f"no link rows in the plain arm{lab().why('plain')}"
    place = re.compile(r"^-?\d+\.\d{2},-?\d+\.\d{2},-?\d+\.\d{2}$")
    seen = {}
    for c in rows:
        if c["Kind"] == "EDGE-IDX":
            # one row for every link naming the same missing triangle: no single source triangle, and the target index is
            # past the table, so neither end has a place and the row keys on the dangling index
            assert c["Necessary"][0].get("At") is None and alt(c).get("At") is None, f"EDGE-IDX {src(c)}: a place where none exists: {c}"
            continue
        recs = [c["Necessary"][0]] + ([alt(c)] if c["Kind"] in ("EDGE-NOMATCH", "EDGE-ONEWAY") else [])
        for r in recs:
            assert r.get("Triangle") is not None and place.match(r.get("At") or ""), f"{c['Kind']} {src(c)}: record {nm(r['FormKey'])} has no place: {r}"
            key = (r["FormKey"].lower(), r["Triangle"])
            assert seen.setdefault(key, r["At"]) == r["At"], f"{nm(r['FormKey'])} triangle {r['Triangle']} stands at two places"
    by_mesh = {}
    for (fk, tri), at in seen.items():
        by_mesh.setdefault((fk, at), set()).add(tri)
    clash = {k: v for k, v in by_mesh.items() if len(v) > 1}
    assert not clash, f"two triangles of one navmesh at one place: {clash}"


def t2563():
    """Rejects: a use_version patch that does not master the plugin whose version it supersedes (RpD.esp re-cuts G4 over
    Skyrim.esm's), or whose read-back does not check it."""
    import struct
    L = lab()
    out = Path(tempfile.mkdtemp(prefix="navedit-masters-"))
    edits = out / "edits.json"
    edits.write_text(json.dumps({"edits": [{"navmesh": N["G4"], "use_version": "Skyrim.esm"}]}), encoding="utf-8")
    navedit = PROBES / "navedit" / "bin" / "Release" / "net9.0" / "navedit.exe"
    plan = run([navedit, "plan", "--edits", edits, "--out", out / "plan.json", "--instance", L.inst, "--game", L.game, "--profile", "P"])
    assert plan.returncode == 0, f"plan exit {plan.returncode}: {(plan.stdout + plan.stderr)[-600:]}"
    row = json.loads((out / "plan.json").read_text(encoding="utf-8"))["edits"][0]
    assert row["useVersion"]["masters"] == ["RpD.esp"], row["useVersion"]
    w = run([navedit, "write", "--plan", out / "plan.json", "--out", out / "UseG4.esp"])
    assert w.returncode == 0 and "masters every plugin it supersedes (RpD.esp)" in w.stdout, f"write exit {w.returncode}: {(w.stdout + w.stderr)[-800:]}"
    # the master list straight from the TES4 header (MAST subrecords), not through the program under test
    b = (out / "UseG4.esp").read_bytes()
    size = struct.unpack_from("<I", b, 4)[0]
    i, masters = 24, []
    while i < 24 + size:
        sig, ln = b[i:i + 4], struct.unpack_from("<H", b, i + 4)[0]
        if sig == b"MAST":
            masters.append(b[i + 6:i + 6 + ln].rstrip(b"\0").decode("latin-1"))
        i += 6 + ln
    assert "RpD.esp" in masters and masters.index("Skyrim.esm") < masters.index("RpD.esp"), masters
    assert not (out / ".navedit-tmp").exists(), "the staging folder was left behind"


def t2490():
    """Rejects: a cut that leaves its table entry, or leaves a later entry's edge field naming the old index."""
    selftest("2490")


def t2491():
    """Rejects: a height move written although it leaves a matched border link 200 u apart."""
    selftest("2491")


def t2492():
    """Rejects: a link check that refuses a move whose neighbour moves with it."""
    selftest("2492")


def t2493():
    """Rejects: a move written around a vertex whose ground cannot be told."""
    selftest("2493")


# ---- the write, read-back and re-check, through this checkout's houseCARL server ----

def flow():
    """The plan's ops written by housecarl_apply into a new patch under an out folder; the arm's patch with one unplanned edit;
    each read back; the order with the patch re-checked. Done once, shared by 2494-2497."""
    L = lab()
    if getattr(L, "flow", None) is not None:
        return L.flow
    f = L.flow = {}
    assert L.plan is not None, f"no repair plan{L.why('plan')}"
    gen = SRC / "housecarl-generator" / "bin" / "Release" / "net9.0" / "housecarl-generator.exe"
    mcp = SRC / "housecarl-mcp" / "bin" / "Release" / "net9.0" / "housecarl-mcp.exe"
    corpus = L.tmp / "corpus" / "gen" / "corpus.json"
    g = run([gen, L.tmp / "corpus" / "gen", L.tmp / "corpus" / "ref"])
    if not corpus.exists():
        f["error"] = f"the rulebook was not generated by {gen} (exit {g.returncode}): {(g.stdout + g.stderr)[-300:]}"
        return f
    import evalrecords
    out = L.tmp / "out"
    out.mkdir()
    args = [mcp, f"--HouseCarl:DataDir={L.game / 'Data'}", f"--HouseCarl:ModsDir={L.inst / 'mods'}",
            f"--HouseCarl:ProfileDir={L.inst / 'profiles' / 'P'}", f"--HouseCarl:CorpusPath={corpus}"]
    assert not any(" " in str(a) for a in args), f"a path holds a space, which this test's command line does not quote: {args}"
    env = {"HOUSECARL_DATA_DIR": str(L.tmp / "hcdata"), "HOUSECARL_RUNLOG_DIR": str(L.tmp / "hcdata" / "runs")}
    v1 = (4097, 2500, 0)                                  # L5's vertex 1 is (4096, 2500, 0); the arm moves it 1 u
    arm_ops = L.plan["ops"] + [{"formid": N["L5"], "field_path": "Data.Vertices", "op": "SetAtIndex", "key": "1", "value": "%d,%d,%d" % v1}]
    navi_arm_ops = L.plan["ops"] + [{"formid": N["NV"], "field_path": "MapInfos[0].MergedTo", "op": "Add", "value": N["R6"]}]
    def readback(name, patch, tag):
        rb = L.tmp / f"readback-{name}{tag}.json"
        f[name + tag + ".readback"] = run([exe("navownprobe"), "--repair-readback", L.tmp / "plan.json", patch, rb])
        f[name + tag + ".readback.doc"] = json.loads(rb.read_text(encoding="utf-8")) if rb.exists() else None
    for name, ops in (("NavRepair", L.plan["ops"]), ("NavRepairArm", arm_ops), ("NavRepairNaviArm", navi_arm_ops)):
        s = evalrecords.Session(" ".join(str(a) for a in args), env, "navmesh-repair-test")
        try:
            f[name + ".answer"] = s.tool("housecarl_apply", {"ops": ops, "patch": name, "out_path": str(out)}, want_json=False)
            patch = out / f"houseCARL - {name}" / f"{name}.esp"
            f[name] = patch
            if name == "NavRepair":
                # the write as the lane makes it: the plan's ops, then each carried container the read-back names forwarded
                # from its winner into the same patch, then the read-back again
                readback(name, patch, ".applied")
                f["fixes"] = (f[name + ".applied.readback.doc"] or {}).get("carriedFixes") or []
                f["forward.answers"] = [s.tool("housecarl_forward", {"formids": x["formids"], "source": x["source"], "into": str(patch.parent)}, want_json=False)
                                        for x in f["fixes"]]
        except Exception as ex:
            f[name + ".answer"] = f"refused: {ex}"
        finally:
            s.close()
        f.setdefault(name, out / f"houseCARL - {name}" / f"{name}.esp")
        readback(name, f[name], "")
    # the order with the patch added: its plugin last, its folder at the top of the asset view (as the workbench's re-check)
    c = json.loads(L.cand.read_text(encoding="utf-8"))
    c["Plugins"] = c["Plugins"] + ["NavRepair.esp"]
    c["Mods"] = [str(f["NavRepair"].parent)] + c["Mods"]
    c["Name"] = "nr with the patch"
    pc = L.tmp / "candidate-patched.json"
    pc.write_text(json.dumps(c), encoding="utf-8")
    common = ["--order", pc, "--instance", L.inst, "--game", L.game, "--profile", "P"]
    emit, rc = L.tmp / "nr-patched.json", L.tmp / "recheck.json"
    f["recheck.log"] = run([exe("navownprobe"), *common, "--edge-tolerance", TOL, "--emit", emit, "--base", L.tmp / "nr-plan.json",
                            "--recheck-plan", L.tmp / "plan.json", "--recheck-out", rc])
    f["recheck"] = json.loads(rc.read_text(encoding="utf-8")) if rc.exists() else None
    return f


def t2494():
    """Rejects: a plan the write path cannot carry, or a patch holding other navmeshes than the planned ones."""
    f = flow()
    assert "error" not in f, f["error"]
    assert f["NavRepair"].exists(), f"no patch at {f['NavRepair']}: {f['NavRepair.answer'][:600]}"
    planned = sorted(nm(p["source"]) for p in lab().plan["plans"] if p["status"] != "refused")
    doc = f["NavRepair.readback.doc"] or {}
    got = sorted(nm(r["navmesh"]) for r in doc.get("records") or [])
    assert got == planned and not doc.get("unplannedNavmeshes"), f"patch navmeshes {got}, planned {planned}, unplanned {doc.get('unplannedNavmeshes')}"


def t2495():
    """Rejects: a written patch whose records differ from the winner plus the plan anywhere (Gate A review finding 4)."""
    f = flow()
    assert "error" not in f, f["error"]
    rb = f["NavRepair.readback"]
    assert rb.returncode == 0 and (f["NavRepair.readback.doc"] or {}).get("holds") is True, f"read-back exit {rb.returncode}: {rb.stdout[-1500:]} {rb.stderr[-300:]}"


def t2496():
    """Rejects: a read-back that misses one unplanned edit (L5's vertex 1 moved 1 u by the arm's extra op)."""
    f = flow()
    assert "error" not in f, f["error"]
    rb, doc = f["NavRepairArm.readback"], f["NavRepairArm.readback.doc"] or {}
    bad = [r for r in doc.get("records") or [] if not r["holds"]]
    assert rb.returncode == 1 and [nm(r["navmesh"]) for r in bad] == ["L5"] and any("Vertices[1]" in d for d in bad[0]["differences"]), \
        f"the arm's read-back exit {rb.returncode}, failing {[(nm(r['navmesh']), r['differences']) for r in bad]}: {rb.stdout[-800:]}"


def t2498():
    """Rejects: a read-back that passes a patch reverting another plugin's container (the cell's name back to Skyrim.esm's)."""
    f = flow()
    assert "error" not in f, f["error"]
    rb, doc = f["NavRepair.applied.readback"], f["NavRepair.applied.readback.doc"] or {}
    bad = [c for c in doc.get("carried") or [] if not c["holds"]]
    nav_bad = [r for r in doc.get("records") or [] if not r["holds"]]
    assert rb.returncode == 1 and doc.get("holds") is False and not nav_bad         and [(c["type"], c["record"], c["winner"]) for c in bad] == [("Cell", "000900:Skyrim.esm", "RpD.esp")]         and any(d.startswith("EditorID") for d in bad[0]["differences"]),         f"read-back of the plan's ops alone: exit {rb.returncode}, holds {doc.get('holds')}, failing navmeshes {nav_bad}, carried {doc.get('carried')}: {rb.stdout[-800:]}"


def t2499():
    """Rejects: a fix that forwards the wrong record or from another plugin than the winner, or leaves the cell reverted."""
    f = flow()
    assert "error" not in f, f["error"]
    assert f.get("fixes") == [{"source": "RpD.esp", "formids": ["000900:Skyrim.esm"]}], f"the read-back's fixes: {f.get('fixes')}"
    cells = [c for c in (f["NavRepair.readback.doc"] or {}).get("carried") or [] if c["record"] == "000900:Skyrim.esm"]
    assert len(cells) == 1 and cells[0]["holds"] and cells[0]["winner"] == "RpD.esp" and cells[0]["compared"] > 10,         f"after the forward ({f.get('forward.answers')}): {cells}"


def t2497():
    """Rejects: a re-check that credits a cut or refused link as repaired, misses a repaired one, or hides a row the patch made."""
    f = flow()
    assert "error" not in f, f["error"]
    r = f["recheck"]
    assert r is not None, f"no re-check (exit {f['recheck.log'].returncode}): {f['recheck.log'].stdout[-800:]} {f['recheck.log'].stderr[-300:]}"
    v = {}
    for l in r["links"]:
        v.setdefault((l["kind"], nm(l["row"])), set()).add(l["verdict"])
    want = {("EDGE-IDX", "L5"): {"repaired"}, ("EDGE-IDX", "L6"): {"repaired"}, ("EDGE-IDX", "L11"): {"repaired"},
            ("EDGE-NOMATCH", "L2"): {"repaired"}, ("EDGE-ONEWAY", "L3"): {"repaired"}, ("EDGE-ONEWAY", "R2"): {"repaired"},
            ("EDGE-IDX", "L7"): {"cut"}, ("EDGE-NAV", "SL"): {"cut"}, ("EDGE-NOMATCH", "L8"): {"cut"}, ("EDGE-NOMATCH", "R8"): {"cut"},
            ("EDGE-NOMATCH", "L4"): {"refused"}, ("EDGE-ONEWAY", "R4a"): {"refused"}, ("EDGE-ONEWAY", "R4b"): {"refused"},
            ("NAV-GROUND", "G1"): {"repaired"}, ("NAV-GROUND", "G4"): {"repaired"}, ("NAV-GROUND", "G5"): {"repaired"}, ("NAV-GROUND", "G6"): {"repaired"}}
    wrong = {k: (v.get(k), w) for k, w in want.items() if v.get(k) != w}
    assert not wrong, f"verdicts (got, want): {wrong}; all: {sorted((k, sorted(x)) for k, x in v.items())}"
    made = [c for c in r["created"] if c["kind"].startswith(("EDGE", "NAV-GROUND"))]
    assert not made, f"rows the patch made: {made}"


CASES = [
    ("2450 the fixture's five plugins in order", t2450),
    ("2451 a link into a deleted navmesh is EDGE-NAV naming the deleter", t2451),
    ("2476 EDGE-NAV rows name their source triangle, one identity per link", t2476),
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
    ("2477 unproven in game: the 8-16 u plan band (L12/R12)", t2477),
    ("2478 unproven in game: the 63-128 u height band (L13/R13)", t2478),
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
    ("2486 plan: L11 relinked and R11 links back", t2486),
    ("2487 ground plan: G1 up 200 u, box z recomputed", t2487),
    ("2488 ground plan: G6's rock vertex stays", t2488),
    ("2489 ground plan: G4 and G5 by the owner's view", t2489),
    ("2490 self-test: a cut renumbers a later entry", t2490),
    ("2491 self-test: a move that breaks a link is refused", t2491),
    ("2492 self-test: a move with its neighbour is planned", t2492),
    ("2493 self-test: a vertex that cannot be told refuses", t2493),
    ("2494 the write: one patch of the planned navmeshes", t2494),
    ("2495 the read-back holds", t2495),
    ("2496 the read-back's arm: one unplanned edit fails it", t2496),
    ("2497 the re-check: repaired, cut and refused", t2497),
    ("2503 plan, NAVI: L6 and R6n name each other, L6's entry whole from Skyrim.esm", t2503),
    ("2504 the read-back holds the patch's NAVI to the winner plus the plan", t2504),
    ("2505 the read-back's NAVI arm: an unplanned entry change fails it", t2505),
    ("2506 self-test: a relink with no NAVI entry to write is refused", t2506),
    ("2498 the read-back refuses a carried cell that is not the winner's", t2498),
    ("2551 self-test: a renumbered row is unchanged by place", lambda: selftest("2551")),
    ("2552 self-test: rows gone, new and on another navmesh", lambda: selftest("2552")),
    ("2553 self-test: a base emit without places is refused", lambda: selftest("2553")),
    ("2554 the emit says where each link row's triangles stand", t2554),
    ("2555 self-test: grouped rows keyed per navmesh", lambda: selftest("2555")),
    ("2590 self-test: a band link with no link back is EDGE-ONEWAY", lambda: selftest("2590")),
    ("2591 self-test: no link back is written across the band", lambda: selftest("2591")),
    ("2592 self-test: an authored link back is restored", lambda: selftest("2592")),
    ("2593 self-test: no ground plan on an island", lambda: selftest("2593")),
    ("2594 self-test: a one-way refusal names its own neighbour", lambda: selftest("2594")),
    ("2563 a use_version patch masters what it supersedes", t2563),
    ("2566 self-test: NAVI linked doors", lambda: selftest("2566")),
    ("2499 the fix forwards that cell from its winner, and it holds", t2499),
]

if __name__ == "__main__":
    for name, fn in CASES:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
