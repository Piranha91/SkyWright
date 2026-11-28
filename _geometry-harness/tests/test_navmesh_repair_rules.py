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
    2595-2598  self-test: the ground plan refuses for a mover's own re-cut (2595); which mover qualifies (2596); refusal order
          and other movers named (2597); a band one-way link yields to an in-tolerance match (2598); a use_version row
          is refused beside a row touching what it changes or was matched against (2603).
    2599-2601  the mover case (navownprobe --write-mover-fixture: Mover.esp raises the ground under GM and re-cuts it, Revert.esp
          reverts it): the ground row is refused and a use_version row carries Mover's version (2599); R1 writes it, reads it
          back against the re-planned version and masters Revert.esp (2600); a tampered row and a stand-alone read-back of
          a use_version row are refused (2601).
    2490-2493  the plan on in-memory navmeshes (navownprobe --navrepair-selftest): a cut renumbers a later table entry; a
          height move that would leave a matched link 200 u apart is refused; the same move with the neighbour moving too
          is planned; a vertex that cannot be told refuses its navmesh.
    2503  plan, NAVI: L6's relink into R6n adds R6n to L6's NAVI entry and L6 to R6n's, both ways. L6's entry is stated only
          by Skyrim.esm, so the patch adds it whole from there (its door, island data and old neighbour kept); R6n's is in
          RpA.esp's list, the NAVI winner, and gains L6 in place. Relinks within one navmesh change no entry.
    2506  self-test: a relink whose new neighbour no plugin's NAVI states is refused by name with its link back; with both
          entries stated it names each navmesh in the other's entry.
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
    2593  self-test: a follow-the-ground plan on a NAVI island makes the same navmesh edits as on a plain one (corrected for R7)
    2594  self-test: a one-way refusal whose match joins its own triangle names it, never an empty target
    2563  navedit on the fixture: use_version of G4 from Skyrim.esm masters RpD.esp, whose version it supersedes (read from the
          written header), the read-back says so, and no staging folder is left
    2566  self-test: NAVI linked doors: added in place on the winner's entry with the base game's Unknown; a door already linked,
          an unstated navmesh, and a whole interior entry (the write engine's compose gap) are refused by name
    2658-2668  R7, islands of the navmesh info map (_shared/NavIsland.cs): following the ground on an island plans with its
          island data (2658) and is refused beside a relink of the same entry (2659); on the island variants of the mover
          fixture, read back by the raw-bytes reader tests/navi_bytes.py: use_version carries the version's own island data
          (2660), also where one side only is an island (2661), and is refused by name where the version's plugin states no
          entry (2662); following the ground moves the island box and mesh by the height rule, stated here apart from the
          C# (2663), also on an entry added whole (2664); a height-only move (2665); a sideways move refused with a mesh,
          written without (2666); a door leaves the island data (2667); a patch whose read-back fails is left unwritten (2668)
    2671-2673  review of Gate B: a version whose own island box is not its navmesh's is refused (2671); a plain navedit
          write whose read-back fails leaves nothing (2672, through navedit's test switch NAVEDIT_FAIL_READBACK=1); a patch
          already at --out is never touched, and a write that holds leaves no staging folder (2673)
    2679-2685  R10, carry a set of navmeshes (navownprobe --write-mover-fixture DIR set | set-ground): two re-cut navmeshes
          refused alone, planned as a set grown by need or stated (2679); the written set read back raw by navi_bytes.py,
          two-way borders by position, inner, outer, merged and ledge links (2680); the navmesh info map changed once, an
          island member, the masters (2681); each refusal in one sentence (2682); a member refused for another reason, and
          undo: a refused or failing set leaves the output folder as it was (2683); the repair plan carries a pair as one set,
          its second row carried with it, written by R1 (2684); self-test of the row carried with an earlier set (2685)
    2686-2688  review of Gate C: the two-way read-back judges only borders with a member at one end (2686); a link into a
          member its version does not return is refused at plan time and grown in (2687)

The write of the plan through houseCARL's server program, its read-back and the re-check (2494-2499, 2504, 2505) are the
module's suite integration/housecarl/tests/test_navmesh_repair_write.py, on this fixture: only the module holds that
program. Probes come from this checkout's Release builds unless HC_NR_HARNESS names another build folder. HC_NR_CASES
(e.g. "2463,2483") runs those cases only.
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
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
PROBES = Path(os.environ.get("HC_NR_HARNESS") or buildlayout.root())
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
    return buildlayout.program_under(PROBES, probe)


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
    navedit = buildlayout.program_under(PROBES, "navedit")
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


# ---- the mover case: a ground row whose mover ships its own re-cut, carried by a use_version row (2026-11-22) ----

MOVER_ORDER = ["Skyrim.esm", "Mover.esp", "Revert.esp"]
GM = sk("000B10")
_MOVER = None
_MOVER_DOORS = None


def _mover_instance(tmp: Path, flags, order=None):
    """navownprobe --write-mover-fixture (with FLAGS) laid out as an MO2 instance with profile P; returns (instance, game)."""
    MOVER_ORDER = order or globals()["MOVER_ORDER"]
    keep = tmp / "fixture"
    w = run([exe("navownprobe"), "--write-mover-fixture", keep] + list(flags))
    if w.returncode != 0 or not all((keep / n).exists() for n in MOVER_ORDER):
        raise RuntimeError(f"the mover fixture was not written (exit {w.returncode}): {(w.stdout + w.stderr)[-600:]}")
    inst, game = tmp / "instance", tmp / "game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True); (inst / "overwrite").mkdir(); (game / "Data").mkdir(parents=True)
    for n in MOVER_ORDER:
        d = inst / "mods" / Path(n).stem
        d.mkdir(parents=True)
        shutil.copyfile(keep / n, d / n)
    (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in MOVER_ORDER), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(n + "\n" for n in MOVER_ORDER), encoding="utf-8")
    (prof / "modlist.txt").write_text("".join("+" + Path(n).stem + "\n" for n in reversed(MOVER_ORDER)), encoding="utf-8")
    return inst, game


class MoverLab:
    """navownprobe --write-mover-fixture laid out as an MO2 instance; one plan run and one R1 write."""

    def __init__(self, tmp: Path, doors: bool = False):
        inst, game = _mover_instance(tmp, ["doors"] if doors else [])
        self.tmp, self.plan_path = tmp, tmp / "plan.json"
        self.log = run([exe("navownprobe"), "--instance", inst, "--game", game, "--profile", "P", "--emit", tmp / "emit.json", "--repair-plan", self.plan_path])
        self.plan = json.loads(self.plan_path.read_text(encoding="utf-8")) if self.log.returncode == 0 and self.plan_path.exists() else None
        navedit = buildlayout.program_under(PROBES, "navedit")
        self.navedit = navedit
        self.out = tmp / "patch" / "MoverFix.esp"
        self.write = run([navedit, "write", "--repair-plan", self.plan_path, "--out", self.out]) if self.plan else None


def mover():
    global _MOVER
    if _MOVER is None:
        _MOVER = MoverLab(Path(tempfile.mkdtemp(prefix="navmesh-repair-mover-")))
    return _MOVER


def mover_doors():
    global _MOVER_DOORS
    if _MOVER_DOORS is None:
        _MOVER_DOORS = MoverLab(Path(tempfile.mkdtemp(prefix="navmesh-repair-mover-doors-")), doors=True)
    return _MOVER_DOORS


def _rows(plan, nav):
    return [p for p in plan["plans"] if p["source"].lower() == nav.lower()]


def t2599():
    """Rejects: a ground plan that lays the winner's navmesh over a mover's ground when the mover ships its own re-cut, or no
    use_version row carrying that re-cut (planned, naming its source, its one-sentence why, and the CLOBBERED row)."""
    M = mover()
    assert M.plan is not None, f"no plan (exit {M.log.returncode}): {(M.log.stdout + M.log.stderr)[-800:]}"
    rows = _rows(M.plan, GM)
    ground = [p for p in rows if p["class"] == "ground"]
    uv = [p for p in rows if p["class"] == "use_version"]
    assert len(ground) == 1 and ground[0]["status"] == "refused", f"the ground row: {ground}"
    gw = ground[0]["why"]
    assert "Mover.esp moved the ground under" in gw and "use_version" in gw and "CLOBBERED row" in gw and "(Mover.esp > Revert.esp)" in gw, gw
    assert len(uv) == 1, f"want one use_version row for GM, got {uv}"
    u = uv[0]
    assert u["status"] == "planned" and u["winner"]["plugin"] == "Mover.esp" and u["winner"]["sha256"], u
    assert u["useVersion"]["masters"] == ["Revert.esp"], u["useVersion"]
    assert any(e["op"] == "CopyFrom" and e.get("from_plugin") == "Mover.esp" for e in u["edits"]), u["edits"]
    assert [c.lower() for c in u["changes"]] == [GM.lower()], u["changes"]
    assert not any(o["formid"].lower() == GM.lower() for o in M.plan["ops"]), "the use_version row's ops leaked into the plan's top-level ops"


def t2600():
    """Rejects: an R1 patch that does not carry the mover's version (read back against the re-planned version), or does not
    master the plugin whose version it supersedes."""
    import struct
    M = mover()
    allout = (M.write.stdout + M.write.stderr) if M.write else ""
    # one-line messages (the runner prints an assertion's last line): GM's read-back lines and R1's last line
    said = f"GM lines {[l.strip() for l in allout.splitlines() if '000B10' in l]}; last {[l.strip() for l in allout.splitlines()[-2:]]}"
    assert M.write is not None and M.write.returncode == 0, f"R1 exit {M.write.returncode if M.write else None}: {said}"
    out = M.write.stdout
    assert "holds  000B10:Skyrim.esm" in out, f"GM does not read back: {said}"
    assert "masters every winner it supersedes" in out and "Revert.esp" in out, f"masters: {said}"
    rb = json.loads(M.out.with_suffix("").with_name("MoverFix.readback.json").read_text(encoding="utf-8"))
    assert rb["holds"] is True, rb
    rec = [r for r in rb["records"] if r["navmesh"].lower() == GM.lower()]
    assert rec and rec[0].get("useVersion") and rec[0]["holds"], rb["records"]
    b = M.out.read_bytes()
    size = struct.unpack_from("<I", b, 4)[0]
    i, masters = 24, []
    while i < 24 + size:
        sig, ln = b[i:i + 4], struct.unpack_from("<H", b, i + 4)[0]
        if sig == b"MAST":
            masters.append(b[i + 6:i + 6 + ln].rstrip(b"\0").decode("latin-1"))
        i += 6 + ln
    assert "Mover.esp" in masters and "Revert.esp" in masters and masters.index("Mover.esp") < masters.index("Revert.esp"), masters


def t2601():
    """Rejects: an R1 that writes a use_version row whose ops no longer match what navedit re-plans (one edit tampered), or
    a read-back that passes a use_version row it cannot hold to the re-planned version (navownprobe --repair-readback)."""
    M = mover()
    assert M.plan is not None
    bad = json.loads(json.dumps(M.plan))
    u = [p for p in bad["plans"] if p["class"] == "use_version"][0]
    u["edits"][-1]["value"] = "0"
    bp = M.tmp / "tampered.json"
    bp.write_text(json.dumps(bad), encoding="utf-8")
    w = run([M.navedit, "write", "--repair-plan", bp, "--out", M.tmp / "patch2" / "Tampered.esp"])
    assert w.returncode != 0 and "no longer plans the same" in (w.stdout + w.stderr), f"exit {w.returncode}: {(w.stdout + w.stderr)[-600:]}"
    assert not (M.tmp / "patch2" / "Tampered.esp").exists(), "a patch was written from a tampered use_version row"
    r = run([exe("navownprobe"), "--repair-readback", M.plan_path, M.out, M.tmp / "standalone-readback.json"])
    assert r.returncode == 1 and "only navedit write --repair-plan can hold" in (r.stdout + r.stderr), f"exit {r.returncode}: {(r.stdout + r.stderr)[-600:]}"


DA, DB, DC = sk("000D31"), sk("000D32"), sk("000D33")


def t2604():
    """Rejects: a carried version whose door table differs from the winner's refused, or written without the navmesh info map
    following it (houseCARL's item 2). The mover's re-cut stands before [DB, DC, DA] (reordered, a door more); the winner's
    entry links [DA, DB]. The row must plan, its NAVI entry change take the version's table in order (nothing of the old
    kept: DA is not first), and R1's patch read back: the planned entries, and the entry the game takes with the patch last
    against the version's own door table, door for door (NavRepairReadBack.NaviFollowsVersion)."""
    M = mover_doors()
    assert M.plan is not None, f"no plan (exit {M.log.returncode}): {(M.log.stdout + M.log.stderr)[-800:]}"
    uv = [p for p in _rows(M.plan, GM) if p["class"] == "use_version"]
    assert len(uv) == 1 and uv[0]["status"] == "planned", f"the use_version row: {[(p['status'], p.get('why')) for p in uv]}"
    # the written patch first (its read-back holds the entry the game takes to the version's own door table), so a planner that
    # is wrong but self-consistent is caught here by the version, before the plan row's details
    allout = (M.write.stdout + M.write.stderr) if M.write else ""
    said = f"GM lines {[l.strip() for l in allout.splitlines() if '000B10' in l]}; last {[l.strip() for l in allout.splitlines()[-2:]]}"
    assert M.write is not None and M.write.returncode == 0, f"R1 exit {M.write.returncode if M.write else None}: {said}"
    assert "holds  000B10:Skyrim.esm" in M.write.stdout, f"GM does not read back: {said}"
    rb = json.loads(M.out.with_suffix("").with_name("MoverFix.readback.json").read_text(encoding="utf-8"))
    navi = [c for c in rb["carried"] if c.get("type") == "NavigationMeshInfoMap"]
    assert rb["holds"] is True and navi and navi[0]["holds"] and navi[0].get("planned"), f"read-back: holds {rb['holds']}, NAVI {navi}"
    u = uv[0]["useVersion"]
    assert [d["door"].lower() for d in u["doors"]["added"]] == [DC.lower()] and u["doors"]["dropped"] == [], u["doors"]
    entries = (u.get("navi") or {}).get("entries") or []
    assert len(entries) == 1 and entries[0]["navmesh"].lower() == GM.lower() and entries[0]["winnerIndex"] == 0, entries
    assert [d.lower() for d in entries[0]["doorsAfter"]] == [DB.lower(), DC.lower(), DA.lower()] and entries[0]["keepDoors"] == 0, entries[0]


GN = sk("000B11")
_NAVEDIT_MOVER = None


def navedit_mover():
    """The mover fixture with doors and a neighbour, planned and written by navedit itself (use_version of GM from Mover.esp)."""
    global _NAVEDIT_MOVER
    if _NAVEDIT_MOVER is None:
        tmp = Path(tempfile.mkdtemp(prefix="navmesh-repair-navedit-mover-"))
        inst, game = _mover_instance(tmp, ["doors", "neighbour"])
        navedit = buildlayout.program_under(PROBES, "navedit")
        (tmp / "edits.json").write_text(json.dumps({"edits": [{"navmesh": GM, "use_version": "Mover.esp"}]}), encoding="utf-8")
        plan = run([navedit, "plan", "--edits", tmp / "edits.json", "--out", tmp / "plan.json", "--instance", inst, "--game", game, "--profile", "P"])
        out = tmp / "patch" / "Carried.esp"
        write = run([navedit, "write", "--plan", tmp / "plan.json", "--out", out]) if plan.returncode == 0 else None
        _NAVEDIT_MOVER = (tmp, plan, write)
    return _NAVEDIT_MOVER


def t2606():
    """Rejects: a carried version that no longer links into a neighbour refused, or written with the navmesh info map still
    naming it (houseCARL's item 3), through navedit's own plan and write. GM's vanilla version links one way into GN, and its
    entry names GN; Mover's re-cut links nothing and stands before [DB, DC, DA]. The patch's entry for GM must name no
    neighbour and link [DB, DC, DA]; GN's entry is not touched. navedit's read-back holds both to the version itself."""
    tmp, plan, write = navedit_mover()
    # the row first, in one line (navedit plan exits 1 when an edit is refused, and its last output line is only the path)
    rows = json.loads((tmp / "plan.json").read_text(encoding="utf-8"))["edits"] if (tmp / "plan.json").exists() else []
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"the use_version row: {[(r.get('status'), r.get('why')) for r in rows]}"
    assert plan.returncode == 0, f"navedit plan exit {plan.returncode}: {(plan.stdout + plan.stderr).strip().splitlines()[-3:]}"
    allout = (write.stdout + write.stderr) if write else ""
    said = f"NAVI lines {[l.strip() for l in allout.splitlines() if 'NAVI' in l]}; last {[l.strip() for l in allout.splitlines()[-1:]]}"
    # the written patch first: navedit's read-back holds the entry the game takes to the version, then the plan's entries
    assert write is not None and write.returncode == 0, f"navedit write exit {write.returncode if write else None}: {said}"
    assert "ok   NAVI follows Mover.esp's version of 000B10:Skyrim.esm" in write.stdout, f"the version check: {said}"
    assert "ok   NAVI 000C00:Skyrim.esm is its winner's with exactly the planned entries changed" in write.stdout, f"the NAVI check: {said}"
    entries = rows[0]["navi"]["entries"]
    assert [e["navmesh"].lower() for e in entries] == [GM.lower()], f"entries {entries} (GN's must not be touched)"
    e = entries[0]
    assert [m.lower() for m in e["remove"]] == [GN.lower()] and e["add"] == [] and e["removePreferred"] == [], e
    assert [d.lower() for d in e["doorsAfter"]] == [DB.lower(), DC.lower(), DA.lower()] and e["keepDoors"] == 0, e


# ---- R7: islands of the navmesh info map (_shared/NavIsland.cs; Gate B C:/hc-runs/2026-11-27-navmesh-repair/r7/GATE-B.md) ----
# Each case runs the real probes on an island variant of the mover fixture (navownprobe --write-mover-fixture DIR <variant>;
# NavRepairFixture.IslandVariants gives each one's table) and reads the written patch back with navi_bytes.py, the raw-bytes
# second reader, which shares no code with the writer.

sys.path.insert(0, str(HERE))
from navi_bytes import Plugin as RawPlugin  # noqa: E402

GE, GQ, DT = sk("000B12"), sk("000B13"), sk("000D34")
_ISLAND = {}


def rk(key):
    """A FormKey string as the raw reader keys it."""
    i, p = key.split(":")
    return (p.lower(), int(i, 16))


def island_lab(variant):
    """The island variant laid out as an MO2 instance: (tmp, instance, game, fixture folder)."""
    if variant not in _ISLAND:
        tmp = Path(tempfile.mkdtemp(prefix=f"nr-{variant}-"))
        inst, game = _mover_instance(tmp, [variant])
        _ISLAND[variant] = (tmp, inst, game, tmp / "fixture")
    return _ISLAND[variant]


def navedit_edit(variant, edits, name):
    """navedit plan and (when it plans) write on the variant: (rows, plan run, write run or None, patch path)."""
    tmp, inst, game, _ = island_lab(variant)
    d = Path(tempfile.mkdtemp(prefix=f"{name}-", dir=tmp))
    (d / "edits.json").write_text(json.dumps({"edits": edits}), encoding="utf-8")
    navedit = buildlayout.program_under(PROBES, "navedit")
    plan = run([navedit, "plan", "--edits", d / "edits.json", "--out", d / "plan.json", "--instance", inst, "--game", game, "--profile", "P"])
    rows = json.loads((d / "plan.json").read_text(encoding="utf-8"))["edits"] if (d / "plan.json").exists() else []
    out = d / "out" / f"{name}.esp"
    write = run([navedit, "write", "--plan", d / "plan.json", "--out", out]) if plan.returncode == 0 else None
    return rows, plan, write, out


def row_said(rows):
    return [(r.get("status"), r.get("why")) for r in rows]


def write_said(w):
    if w is None:
        return "no write"
    lines = (w.stdout + w.stderr).strip().splitlines()
    return f"exit {w.returncode}: {[l.strip() for l in lines if 'FAIL' in l or 'refused' in l][:3] or lines[-2:]}"


def heights_by_rule(island, before, after):
    """THE HEIGHT RULE, stated independently of the C# (Gate A review condition 2): each island vertex moves in height by
    what the navmesh surface under it moved, at its own point in plan when a triangle of the navmesh holds it there (of
    several, the one whose plane is nearest the vertex in height), else at the nearest point of the navmesh in plan (of
    ties, the nearest in height). before and after are navi_bytes Navmesh objects with the same triangles."""
    import math
    out = []
    V0, V1 = before.vertices, after.vertices
    for (x, y, z) in island:
        best = None
        for t in before.triangles:
            a, b, c = t[0], t[1], t[2]
            A, B, C = V0[a], V0[b], V0[c]
            den = (B[1] - C[1]) * (A[0] - C[0]) + (C[0] - B[0]) * (A[1] - C[1])
            if den == 0:
                continue
            l1 = ((B[1] - C[1]) * (x - C[0]) + (C[0] - B[0]) * (y - C[1])) / den
            l2 = ((C[1] - A[1]) * (x - C[0]) + (A[0] - C[0]) * (y - C[1])) / den
            l3 = 1 - l1 - l2
            if min(l1, l2, l3) >= -1e-9:
                k = abs(l1 * A[2] + l2 * B[2] + l3 * C[2] - z)
                if best is None or k < best[0]:
                    best = (k, [(a, l1), (b, l2), (c, l3)])
        if best is None:
            for t in before.triangles:
                for p, q in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
                    P, Q = V0[p], V0[q]
                    ex, ey = Q[0] - P[0], Q[1] - P[1]
                    L = ex * ex + ey * ey
                    s = 0.0 if L == 0 else max(0.0, min(1.0, ((x - P[0]) * ex + (y - P[1]) * ey) / L))
                    k = (math.hypot(P[0] + ex * s - x, P[1] + ey * s - y), abs(P[2] + (Q[2] - P[2]) * s - z))
                    if best is None or k < best[0]:
                        best = (k, [(p, 1 - s), (q, s)])
        dz = sum(w * (V1[i][2] - V0[i][2]) for i, w in best[1])
        out.append((x, y, z + dz))
    return out


def close3(a, b, tol=1e-3):
    return len(a) == len(b) and all(abs(p - q) <= tol for u, v in zip(a, b) for p, q in zip(u, v))


def same_but_island(got, want):
    """True when two NVMI entries agree byte for byte outside the flag and the island block."""
    return got.raw[8:got.island_start - 1] == want.raw[8:want.island_start - 1] and got.raw[:4] == want.raw[:4] \
        and got.raw[got.island_end:] == want.raw[want.island_end:]


def holds_box(entry, nav):
    return entry.island is not None and entry.island["min_bits"] == nav.min_bits and entry.island["max_bits"] == nav.max_bits


def t2660():
    """Rejects: use_version on an island refused (the behaviour before R7), or written with the winner's island data left on
    the carried version, the flag not carried, the box not the carried navmesh's, another entry touched, or anything else
    left in the output folder. island-version: GM's entry, in place in Revert.esp's list (the winner), takes Mover.esp's own
    island block byte for byte and its flag; every other byte of NAVI is Revert's."""
    _, _, _, fx = island_lab("island-version")
    rows, plan, write, out = navedit_edit("island-version", [{"navmesh": GM, "use_version": "Mover.esp"}], "UseIsland")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"the use_version row: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"navedit write {write_said(write)}"
    assert sorted(p.name for p in out.parent.iterdir()) == [out.name], f"the output folder holds {sorted(p.name for p in out.parent.iterdir())}"
    pn, rn, mn = RawPlugin(out).navi(), RawPlugin(fx / "Revert.esp").navi(), RawPlugin(fx / "Mover.esp").navi()
    g = rk(GM)
    assert set(pn) == set(rn), f"the patch's NAVI states {sorted(pn)}, Revert's {sorted(rn)}"
    assert all(pn[k].raw == rn[k].raw for k in pn if k != g), "an entry other than GM's differs from the winner's"
    e = pn[g]
    assert e.island_bytes == mn[g].island_bytes, f"GM's island block is not Mover.esp's ({len(e.island_bytes)} bytes, Mover's {len(mn[g].island_bytes)})"
    assert e.unknown == mn[g].unknown == 32, f"GM's flag {e.unknown}, Mover.esp's {mn[g].unknown}"
    assert same_but_island(e, rn[g]), "GM's entry differs from the winner's outside the flag and the island data"
    assert holds_box(e, RawPlugin(out).navmeshes()[g]), "GM's island box is not the box of the navmesh the patch carries"


def t2661():
    """Rejects: use_version where only one side is an island written with the winner's flag or island data. island-plain-
    version: GM's entry ends plain (flag 0, no island data); island-plain-winner: it ends Mover.esp's island (flag 32, its
    block byte for byte, the box the carried navmesh's)."""
    g = rk(GM)
    rows, _, write, out = navedit_edit("island-plain-version", [{"navmesh": GM, "use_version": "Mover.esp"}], "PlainVersion")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"plain version: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"plain version: navedit write {write_said(write)}"
    fx = island_lab("island-plain-version")[3]
    e, w = RawPlugin(out).navi()[g], RawPlugin(fx / "Revert.esp").navi()[g]
    assert e.unknown == 0 and e.island is None, f"plain version: GM's flag {e.unknown}, island {'kept' if e.island else 'none'}"
    assert same_but_island(e, w), "plain version: GM's entry differs from the winner's outside the flag and the island data"
    rows, _, write, out = navedit_edit("island-plain-winner", [{"navmesh": GM, "use_version": "Mover.esp"}], "PlainWinner")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"plain winner: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"plain winner: navedit write {write_said(write)}"
    fx = island_lab("island-plain-winner")[3]
    e, m = RawPlugin(out).navi()[g], RawPlugin(fx / "Mover.esp").navi()[g]
    assert e.unknown == 32 and e.island_bytes == m.island_bytes, f"plain winner: GM's flag {e.unknown}, island block {'Mover.esp' if e.island_bytes == m.island_bytes else 'not Mover.esp'}'s"
    assert holds_box(e, RawPlugin(out).navmeshes()[g]), "plain winner: GM's island box is not the carried navmesh's"


def t2662():
    """Rejects: use_version from a plugin that states no navmesh info map entry for the island navmesh written (with the
    winner's island data or none), or refused for another reason or in more than one sentence, or a refused plan written.
    island-noentry: Mover.esp carries no NAVI."""
    rows, plan, write, out = navedit_edit("island-noentry", [{"navmesh": GM, "use_version": "Mover.esp"}], "NoEntry")
    why = " ".join(rows[0].get("why") or []) if rows else ""
    assert len(rows) == 1 and rows[0].get("status") == "refused" and plan.returncode == 1, f"the row: {row_said(rows)}, plan exit {plan.returncode}"
    assert "Mover.esp" in why and "states no" in why and why.count(". ") == 0 and why.endswith("."), f"the refusal: {why}"
    w = run([buildlayout.program_under(PROBES, "navedit"), "write", "--plan", out.parent.parent / "plan.json", "--out", out])
    left = sorted(p.name for p in out.parent.iterdir()) if out.parent.exists() else []
    assert w.returncode != 0 and left == [], f"writing the refused plan: exit {w.returncode}, the output folder holds {left}"


def island_r1(variant, tamper=None):
    """navownprobe --repair-plan on the variant, then navedit write --repair-plan (after TAMPER(plan) when given)."""
    tmp, inst, game, _ = island_lab(variant)
    d = Path(tempfile.mkdtemp(prefix="r1-", dir=tmp))
    log = run([exe("navownprobe"), "--instance", inst, "--game", game, "--profile", "P", "--emit", d / "emit.json", "--repair-plan", d / "plan.json"])
    plan = json.loads((d / "plan.json").read_text(encoding="utf-8")) if log.returncode == 0 and (d / "plan.json").exists() else None
    if plan is not None and tamper is not None:
        tamper(plan)
        (d / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    out = d / "out" / "IslandFix.esp"
    write = run([buildlayout.program_under(PROBES, "navedit"), "write", "--repair-plan", d / "plan.json", "--out", out]) if plan else None
    return plan, log, write, out


def ground_rows(plan, key):
    return [p for p in plan["plans"] if p["source"].lower() == key.lower() and p["class"] == "ground"]


def check_ground_islands(out, fx, whole):
    """The written ground patch's NAVI against the raw bytes: GM's island box the patch's GM box bit for bit and its mesh by
    the height rule from the winner's; GE's box its navmesh's, its mesh empty; every byte else as its source entry."""
    pp = RawPlugin(out)
    pn, pnav = pp.navi(), pp.navmeshes()
    src = RawPlugin(fx / ("Skyrim.esm" if whole else "Revert.esp")).navi()
    rn = RawPlugin(fx / "Revert.esp").navi()
    before = RawPlugin(fx / "Revert.esp").navmeshes()
    g, ge = rk(GM), rk(GE)
    want_keys = set(rn) | ({g, ge} if whole else set())
    assert set(pn) == want_keys, f"the patch's NAVI states {sorted(pn)}, want {sorted(want_keys)}"
    assert all(pn[k].raw == rn[k].raw for k in rn if k not in (g, ge)), "an entry the plan does not change differs from the winner's"
    e = pn[g]
    assert holds_box(e, pnav[g]), f"GM's island box {e.island and (e.island['min'], e.island['max'])} is not the patch's GM box {(pnav[g].min, pnav[g].max)}"
    want = heights_by_rule(src[g].island["vertices"], before.get(g) or RawPlugin(fx / "Skyrim.esm").navmeshes()[g], pnav[g])
    assert close3(e.island["vertices"], want), f"GM's island vertices {e.island['vertices']}, the height rule gives {want}"
    assert e.island["triangles"] == src[g].island["triangles"] and e.unknown == 32, "GM's island triangles or flag changed"
    assert same_but_island(e, src[g]), "GM's entry differs from its source outside the island data"
    f = pn[ge]
    gnav = pnav.get(ge)
    assert gnav is not None and holds_box(f, gnav) and not f.island["vertices"] and not f.island["triangles"], \
        f"GE's island: {f.island and (f.island['min'], f.island['max'], len(f.island['vertices']))}, want the patch's GE box and an empty mesh"
    assert same_but_island(f, src[ge]), "GE's entry differs from its source outside the island data"


def t2663():
    """Rejects: following the ground refused on an island (the behaviour before R7), or written with the island box left
    stale, an island vertex not moved by the height rule (stated here, apart from the C#), an empty mesh given vertices, or
    an entry outside the plan touched. island-ground: GM (a mesh) and GE (an empty mesh) follow Mover.esp's ground up 200
    u; their entries are Revert.esp's (the NAVI winner), changed in place; R1 writes and reads back."""
    plan, log, write, out = island_r1("island-ground")
    assert plan is not None, f"no plan (exit {log.returncode}): {(log.stdout + log.stderr)[-500:]}"
    for k in (GM, GE):
        r = ground_rows(plan, k)
        assert len(r) == 1 and r[0]["status"] == "planned", f"{k}'s ground row: {[(x['status'], x['why']) for x in r]}"
    assert write is not None and write.returncode == 0, f"R1 {write_said(write)}"
    assert sorted(p.name for p in out.parent.iterdir()) == ["IslandFix.esp", "IslandFix.readback.json"], f"the output folder holds {sorted(p.name for p in out.parent.iterdir())}"
    check_ground_islands(out, island_lab("island-ground")[3], whole=False)


def t2664():
    """Rejects (Gate A review condition 4): an island entry stated by an earlier plugin than the NAVI winner written with its
    old island data, or not at all, when the ground moves. island-ground-whole: Revert.esp's NAVI states only GQ, so GM's
    and GE's entries are Skyrim.esm's and the patch adds them whole, each with its new island data."""
    plan, log, write, out = island_r1("island-ground-whole")
    assert plan is not None, f"no plan (exit {log.returncode}): {(log.stdout + log.stderr)[-500:]}"
    for k in (GM, GE):
        r = ground_rows(plan, k)
        assert len(r) == 1 and r[0]["status"] == "planned", f"{k}'s ground row: {[(x['status'], x['why']) for x in r]}"
    assert write is not None and write.returncode == 0, f"R1 {write_said(write)}"
    check_ground_islands(out, island_lab("island-ground-whole")[3], whole=True)


def t2665():
    """Rejects: a height-only vertex move on an island refused, or written with the island box or mesh not following it.
    GM's vertex 1 rises 30 u: the box becomes (12300,500,0)-(12800,800,30); of the island's vertices only (12900,650,0),
    east of the navmesh, whose nearest point is the middle of edge 1-2, rises (15 u); the patch is read back raw."""
    rows, _, write, out = navedit_edit("island-ground", [{"navmesh": GM, "move": [{"vertex": 1, "by": [0, 0, 30]}]}], "MoveUp")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"the move: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"navedit write {write_said(write)}"
    fx = island_lab("island-ground")[3]
    pp = RawPlugin(out)
    e, nav = pp.navi()[rk(GM)], pp.navmeshes()[rk(GM)]
    src = RawPlugin(fx / "Revert.esp")
    assert holds_box(e, nav) and nav.max[2] == 30, f"GM's island box {e.island and (e.island['min'], e.island['max'])}, its navmesh's {(nav.min, nav.max)}"
    want = heights_by_rule(src.navi()[rk(GM)].island["vertices"], src.navmeshes()[rk(GM)], nav)
    assert close3(e.island["vertices"], want) and close3(want, [(12300, 650, 0), (12650, 650, 5), (12900, 650, 15)]), f"GM's island vertices {e.island['vertices']}, the rule gives {want}"


def t2666():
    """Rejects: a plan-changing edit allowed on an island with a mesh, refused for another reason or in more than one
    sentence; or refused on an island with an empty mesh, or written there with the box not following. A sideways move of
    GM's vertex 2 is refused by name; the same move of GE's vertex 2 (empty mesh) is written, its box (13300,500,0)-
    (13810,800,0) as its navmesh's."""
    rows, plan, _, _ = navedit_edit("island-ground", [{"navmesh": GM, "move": [{"vertex": 2, "by": [10, 0, 0]}]}], "SideMesh")
    why = " ".join(rows[0].get("why") or []) if rows else ""
    assert len(rows) == 1 and rows[0].get("status") == "refused", f"the sideways move on GM: {row_said(rows)}"
    assert "island" in why and "height only" in why and why.count(". ") == 0 and why.endswith("."), f"the refusal: {why}"
    rows, _, write, out = navedit_edit("island-ground", [{"navmesh": GE, "move": [{"vertex": 2, "by": [10, 0, 0]}]}], "SideEmpty")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"the sideways move on GE: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"navedit write {write_said(write)}"
    pp = RawPlugin(out)
    e, nav = pp.navi()[rk(GE)], pp.navmeshes()[rk(GE)]
    assert holds_box(e, nav) and nav.max[0] == 13810 and not e.island["vertices"], f"GE's island {e.island and (e.island['min'], e.island['max'], len(e.island['vertices']))}, its navmesh's box {(nav.min, nav.max)}"


def t2667():
    """Rejects: a door edit on an island refused, or written with the island data or flag touched. island-door: DT's
    arrival is on GM; the patch's GM entry gains DT and keeps Skyrim.esm's island block byte for byte."""
    rows, _, write, out = navedit_edit("island-door", [{"navmesh": GM, "door": {"ref": DT}}], "DoorIsland")
    assert len(rows) == 1 and rows[0].get("status") == "planned", f"the door: {row_said(rows)}"
    assert write is not None and write.returncode == 0, f"navedit write {write_said(write)}"
    e = RawPlugin(out).navi()[rk(GM)]
    s = RawPlugin(island_lab("island-door")[3] / "Skyrim.esm").navi()[rk(GM)]
    assert e.island_bytes == s.island_bytes and e.unknown == 32, "GM's island data or flag changed with the door"
    assert [d for _, d in e.doors] == [rk(DT)], f"GM's linked doors {e.doors}"


def t2668():
    """Rejects: a written patch whose read-back fails left in the output folder (Gate A section 4: a failing patch is left
    unwritten; section 6: undo). The island-ground repair plan with GQ's planned vertex op changed after planning (its
    "change" kept): the read-back fails, and the output folder must hold nothing, no staging folder either."""
    def tamper(plan):
        ops = [o for o in plan["ops"] if o["formid"].lower() == GQ.lower() and o["field_path"] == "Data.Vertices"]
        assert ops, "the plan has no vertex op for GQ to change"
        x, y, z = ops[0]["value"].split(",")
        ops[0]["value"] = f"{x},{y},{float(z) + 7}"
    plan, log, write, out = island_r1("island-ground", tamper)
    assert plan is not None and write is not None, f"no plan (exit {log.returncode})"
    left = sorted(p.name for p in out.parent.iterdir()) if out.parent.exists() else []
    assert write.returncode != 0, f"R1 passed a tampered patch: {write_said(write)}"
    assert left == [], f"a failing patch left {left} in the output folder"


def t2671():
    """Rejects (review F2): a version whose own island box is not its navmesh's box carried (the read-back would then fail
    after a faithful carry) or its box corrected silently; the refusal is one sentence naming the box and what to try.
    island-sloppy-version: Mover.esp's entry gives the island the top z 201, where its navmesh stores 200."""
    rows, plan, write, out = navedit_edit("island-sloppy-version", [{"navmesh": GM, "use_version": "Mover.esp"}], "Sloppy")
    why = " ".join(rows[0].get("why") or []) if rows else ""
    assert len(rows) == 1 and rows[0].get("status") == "refused" and plan.returncode == 1, f"the row: {row_said(rows)}, plan exit {plan.returncode}"
    assert "Mover.esp" in why and "box" in why and "201" in why and why.count(". ") == 0 and why.endswith("."), f"the refusal: {why}"


def t2672():
    """Rejects (review F4): plain navedit write (not --repair-plan) leaving a patch, or its staging folder, in the output
    folder when the read-back fails. The read-back is made to fail by navedit's test switch NAVEDIT_FAIL_READBACK=1 on an
    edit that otherwise writes (the door on island-door); the output folder must hold nothing."""
    tmp, inst, game, _ = island_lab("island-door")
    d = Path(tempfile.mkdtemp(prefix="failrb-", dir=tmp))
    (d / "edits.json").write_text(json.dumps({"edits": [{"navmesh": GM, "door": {"ref": DT}}]}), encoding="utf-8")
    navedit = buildlayout.program_under(PROBES, "navedit")
    plan = run([navedit, "plan", "--edits", d / "edits.json", "--out", d / "plan.json", "--instance", inst, "--game", game, "--profile", "P"])
    assert plan.returncode == 0, f"navedit plan exit {plan.returncode}: {(plan.stdout + plan.stderr).strip().splitlines()[-2:]}"
    out = d / "out" / "Failing.esp"
    env = dict(os.environ, NAVEDIT_FAIL_READBACK="1")
    w = subprocess.run([str(navedit), "write", "--plan", str(d / "plan.json"), "--out", str(out)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=600, env=env)
    left = sorted(p.name for p in out.parent.iterdir()) if out.parent.exists() else []
    assert w.returncode != 0 and "NAVEDIT_FAIL_READBACK" in w.stdout, f"the write: exit {w.returncode}: {(w.stdout + w.stderr).strip().splitlines()[-2:]}"
    assert left == [], f"a failing plain write left {left} in the output folder"


def t2673():
    """Rejects (review F4): a write that replaces or touches a patch already at --out (a second write over a first is
    refused, Gate A section 6), or leaves its staging folder after a write that holds. On island-door: a first write holds
    and leaves exactly the patch; a second to the same --out is refused and the first patch is byte for byte as it was."""
    rows, _, write, out = navedit_edit("island-door", [{"navmesh": GM, "door": {"ref": DT}}], "Twice")
    assert len(rows) == 1 and rows[0].get("status") == "planned" and write is not None and write.returncode == 0, f"the first write: {row_said(rows)} {write_said(write)}"
    assert sorted(p.name for p in out.parent.iterdir()) == [out.name], f"after a write that holds the output folder holds {sorted(p.name for p in out.parent.iterdir())}"
    before = out.read_bytes()
    again = run([buildlayout.program_under(PROBES, "navedit"), "write", "--plan", out.parent.parent / "plan.json", "--out", out])
    assert again.returncode != 0 and "already exists" in (again.stdout + again.stderr), f"the second write: exit {again.returncode}"
    assert out.read_bytes() == before and sorted(p.name for p in out.parent.iterdir()) == [out.name], "the first patch was touched, or something was left beside it"


# ---- R10: carry a set of navmeshes (Gate A C:/hc-runs/2026-11-27-navmesh-r10/GATE-A.md; navownprobe --write-mover-fixture DIR
# set | set-ground, NavRepairFixture.WriteSetCase gives the table). Every written patch is read back raw by navi_bytes.py
# (not_returned: two-way borders by position, its own measure), before anything about the plan row is asserted.

from navi_bytes import not_returned  # noqa: E402

SET_ORDER = ["Skyrim.esm", "SetP.esp", "Revert.esp", "Touch.esp"]
SA, SB, SO, SS, SR, SC, SX, SW, SDN, DZ = (sk(x) for x in ("000B20", "000B21", "000B22", "000B23", "000B24", "000B25", "000B26", "000B27", "000B28", "000D40"))
SQ, SK, SL = sk("000B29"), sk("000B2A"), sk("000B2B")
_SETLAB = {}


def set_lab(variant="set"):
    """The set variant laid out as an MO2 instance: (tmp, instance, game, fixture folder, order)."""
    if variant not in _SETLAB:
        tmp = Path(tempfile.mkdtemp(prefix=f"nr-{variant}-"))
        order = SET_ORDER if variant == "set" else MOVER_ORDER
        inst, game = _mover_instance(tmp, [variant], order)
        _SETLAB[variant] = (tmp, inst, game, tmp / "fixture", order)
    return _SETLAB[variant]


def set_plan(edits, name):
    """navedit plan on the set variant: (rows, plan run, folder)."""
    tmp, inst, game, _, _ = set_lab()
    d = Path(tempfile.mkdtemp(prefix=f"{name}-", dir=tmp))
    (d / "edits.json").write_text(json.dumps({"edits": edits}), encoding="utf-8")
    plan = run([buildlayout.program_under(PROBES, "navedit"), "plan", "--edits", d / "edits.json", "--out", d / "plan.json", "--instance", inst, "--game", game, "--profile", "P"])
    rows = json.loads((d / "plan.json").read_text(encoding="utf-8"))["edits"] if (d / "plan.json").exists() else []
    return rows, plan, d


def set_write(d, out, env=None):
    return subprocess.run([str(buildlayout.program_under(PROBES, "navedit")), "write", "--plan", str(d / "plan.json"), "--out", str(out)],
                          capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600, env=env)


def raw_taken(fx, order, patch):
    """{key: Navmesh} as the game takes them with the patch loaded last, read raw."""
    taken = {}
    for n in order:
        taken.update(RawPlugin(fx / n).navmeshes())
    pp = RawPlugin(patch)
    taken.update(pp.navmeshes())
    return taken, pp


def raw_two_way(fx, order, patch, carried=None):
    """navi_bytes' two-way check, as the game takes the navmeshes with the patch last. With carried (the members' keys), the
    borders with a member at one end (review of Gate C, decision 1); without, every border of every navmesh the patch holds
    and of every navmesh linking into one of them."""
    taken, pp = raw_taken(fx, order, patch)
    held = set(carried) if carried is not None else set(pp.navmeshes())
    sources = held | {k for k, n in taken.items() if any(l[1] in held for l in n.links)}
    return not_returned(taken, sorted(sources, key=str), held if carried is not None else None), pp


def members(row):
    return [m["navmesh"].lower() for m in ((row.get("useVersion") or {}).get("members") or [])]


def one_sentence(w):
    return w.endswith(".") and "\n" not in w and len(w.rstrip(".").split(". ")) == 1


_GROWN = None


def grown_set():
    """use_version SA from SetP.esp grown by need, planned and written once: (rows, plan run, folder, write run, patch)."""
    global _GROWN
    if _GROWN is None:
        rows, plan, d = set_plan([{"navmesh": SA, "use_version": "SetP.esp", "with_neighbours": True}], "Grown")
        out = d / "out" / "SetFix.esp"
        w = set_write(d, out) if plan.returncode == 0 else None
        _GROWN = (rows, plan, d, w, out)
    return _GROWN


def t2679():
    """Rejects: SA or SB carried alone (each one's border to the other would be one-way), a refusal that does not name the
    other and the edit that carries it, a grown set other than exactly [SA, SB] (growth that stops short, or adds SR, which
    SetP already wins, or SO or SS, which need nothing), or a stated set [SA, SB] planning other ops than the grown one."""
    for k, other in ((SA, SB), (SB, SA)):
        rows, _, _ = set_plan([{"navmesh": k, "use_version": "SetP.esp"}], "Single")
        whys = (rows[0].get("why") or []) if rows else []
        assert rows and rows[0]["status"] == "refused", f"{k} alone: {row_said(rows)}"
        assert any(other in w and "does not link back" in w and '"with_neighbours": true' in w and one_sentence(w) for w in whys), f"{k} alone: {whys}"
    rows, plan, _, _, _ = grown_set()
    assert rows and rows[0]["status"] == "planned", f"grown: {row_said(rows)}"
    assert members(rows[0]) == [SA.lower(), SB.lower()], f"grown set members {members(rows[0])}, want [SA, SB]"
    st, _, _ = set_plan([{"navmeshes": [SA, SB], "use_version": "SetP.esp"}], "Stated")
    assert st and st[0]["status"] == "planned", f"stated: {row_said(st)}"
    assert st[0]["ops"] == rows[0]["ops"], "the stated set [SA, SB] plans other ops than the grown one"


def t2680():
    """Rejects: a written set whose borders are not all two-way (read raw), members not carried as SetP has them, an inner
    link naming the winner's numbering, an outer link matched against SetP's own SO (which links nothing back) rather than
    Revert's, SS (linked into both members) re-pointed into one member only, SR (SetP wins it) carried or its ledge dropped,
    or anything but the patch left in the output folder."""
    rows, _, d, w, out = grown_set()
    assert w is not None and w.returncode == 0, f"navedit write {write_said(w)} (plan {row_said(rows)})"
    tmp, _, _, fx, order = set_lab()
    bad, pp = raw_two_way(fx, order, out, {rk(SA), rk(SB)})
    assert not bad, f"raw: borders not two-way: {bad}"
    assert sorted(p.name for p in out.parent.iterdir()) == [out.name], f"the output folder holds {sorted(p.name for p in out.parent.iterdir())}"
    nav, src = pp.navmeshes(), RawPlugin(fx / "SetP.esp").navmeshes()
    assert set(nav) == {rk(SA), rk(SB), rk(SO), rk(SS)}, f"the patch holds {sorted(nav)}"
    for k in (SA, SB):
        assert nav[rk(k)].vertices == src[rk(k)].vertices and [t[:3] for t in nav[rk(k)].triangles] == [t[:3] for t in src[rk(k)].triangles], f"{k} is not SetP's"
    L = {k: [(l[1], l[2], l[0]) for l in nav[rk(k)].links] for k in (SA, SB, SO, SS)}
    assert (rk(SB), 3, 0) in L[SA] and (rk(SA), 1, 0) in L[SB], f"inner links SA {L[SA]}, SB {L[SB]} (want SB 3 and SA 1, SetP's own numbering)"
    assert (rk(SO), 0, 0) in L[SA], f"SA's link into SO {L[SA]} (want Revert's SO triangle 0)"
    # CORRECTED 2026-11-27 (review of Gate C): SS also links one way into SQ now, a border the set does not touch
    assert [x for x in L[SS] if x[0] != rk(SQ)] == [(rk(SA), 2, 0), (rk(SB), 2, 0)], f"SS's links {L[SS]} (want both re-pointed to triangle 2: one merged change)"
    assert L[SO] == [(rk(SA), 3, 0)], f"SO's links {L[SO]} (want re-pointed to SA's triangle 3)"
    assert (rk(SR), 1, 2) in L[SB], f"SB's ledge into SR {L[SB]}"


def t2681():
    """Rejects: the navmesh info map changed member by member (a neighbour gained between two members written twice), a
    member's entry not following its version (SB takes SetP's island data byte for byte; SA names exactly SO, SS, SB; SB
    exactly SA, SS, SR), another entry touched, or a patch not mastering every plugin whose version of a member it
    supersedes (Revert.esp for SA, Touch.esp for SB), in load order, read from the written header."""
    rows, _, d, w, out = grown_set()
    assert w is not None and w.returncode == 0, f"navedit write {write_said(w)}"
    tmp, _, _, fx, order = set_lab()
    pn, rn, sn = RawPlugin(out).navi(), RawPlugin(fx / "Revert.esp").navi(), RawPlugin(fx / "SetP.esp").navi()
    for k, want in ((SA, {SO, SS, SB}), (SB, {SA, SS, SR})):
        e = pn[rk(k)]
        named = e.merged_to + e.preferred
        assert sorted(named) == sorted(rk(x) for x in want) and len(named) == len(set(named)), f"{k}'s entry names {named}, want each of {sorted(want)} once"
    assert pn[rk(SB)].island_bytes == sn[rk(SB)].island_bytes and pn[rk(SB)].unknown == 32, "SB's island data is not SetP's"
    assert pn[rk(SA)].island is None, "SA's entry gained island data"
    assert all(pn[k].raw == rn[k].raw for k in rn if k not in (rk(SA), rk(SB))), "an entry outside the set differs from the winner's"
    ops = [json.dumps(o, sort_keys=True) for o in rows[0]["ops"] if o["formid"].lower() == "000c00:skyrim.esm"]
    assert ops and len(ops) == len(set(ops)), f"the plan's NAVI ops repeat: {ops}"
    masters = RawPlugin(out).masters
    assert "revert.esp" in masters and "touch.esp" in masters and masters.index("revert.esp") < masters.index("touch.esp"), f"the patch's masters {masters}"


def t2682():
    """Rejects: a stated set that carries anything but what it names, a refusal that is not one sentence or misses the
    shape GATE-A section 3 gives: a navmesh named twice; one SetP already wins (SR); one SetP has no version of (SX); a set
    [SA] alone whose border needs SB (the sentence says to add it); and, grown from SC, a neighbour SetP has no version of
    (SX) and one whose version SetP already wins but does not fit (SW): growth stops at both, SW never a member."""
    cases = [
        ({"navmeshes": [SA, SB, SA], "use_version": "SetP.esp"}, f"{SA} is named twice in the set; name each navmesh once."),
        ({"navmeshes": [SA, SB, SR], "use_version": "SetP.esp"}, f"SetP.esp already wins {SR}, so there is nothing to carry; leave {SR} out of the set."),
        ({"navmeshes": [SA, SB, SX], "use_version": "SetP.esp"}, f"SetP.esp has no version of {SX}, or deletes it, so there is nothing to carry."),
        ({"navmeshes": [SA], "use_version": "SetP.esp"}, f"add {SB} to the set, or keep the winner's version of {SA}."),
        ({"navmesh": SC, "use_version": "SetP.esp", "with_neighbours": True}, f"SetP.esp has no version of {SX} to carry with it, so keep the winner's version of {SC}."),
        ({"navmesh": SC, "use_version": "SetP.esp", "with_neighbours": True}, f"SetP.esp's own version of {SW}, the one the game takes, does not fit it either, so keep the winner's version of {SC}."),
    ]
    for i, (edit, want) in enumerate(cases):
        rows, _, _ = set_plan([edit], f"Refuse{i}")
        whys = (rows[0].get("why") or []) if rows else []
        assert rows and rows[0]["status"] == "refused" and not rows[0]["ops"], f"{edit}: {row_said(rows)}"
        assert any(w.endswith(want) and one_sentence(w) for w in whys), f"{edit}: no one-sentence refusal ending '{want}' in {whys}"
        assert rk(SW) not in [rk(m) for m in members(rows[0])], f"{edit}: SW, which SetP already wins, was made a member"


def t2683():
    """Rejects: a set with a member refused for another reason written in part, or refused without that member's own
    sentence starting with which member (SD stands before door DZ, which no plugin places); a refused set touching the
    output folder (its listing and a file already there, byte for byte); or a set whose read-back fails (navedit's test
    switch NAVEDIT_FAIL_READBACK=1) leaving anything there."""
    rows, plan, d = set_plan([{"navmeshes": [SA, SB, SDN], "use_version": "SetP.esp"}], "DoorSet")
    outd = d / "out"
    outd.mkdir()
    (outd / "keep.txt").write_bytes(b"left as it was\n")
    w = set_write(d, outd / "DoorSet.esp")
    left = sorted(p.name for p in outd.iterdir())
    assert left == ["keep.txt"] and (outd / "keep.txt").read_bytes() == b"left as it was\n", f"the refused set left {left} in the output folder (write exit {w.returncode})"
    assert w.returncode != 0, f"writing a refused set: exit {w.returncode}"
    whys = (rows[0].get("why") or []) if rows else []
    assert rows and rows[0]["status"] == "refused", f"the set with SD: {row_said(rows)}"
    assert any(x.startswith(f"{SDN}: ") and DZ in x and "no plugin of this order provides" in x and one_sentence(x) for x in whys), f"SD's own sentence: {whys}"
    grows, gplan, gd = set_plan([{"navmesh": SA, "use_version": "SetP.esp", "with_neighbours": True}], "FailRb")
    gout = gd / "out"
    gout.mkdir()
    (gout / "keep.txt").write_bytes(b"left as it was\n")
    fw = set_write(gd, gout / "Failing.esp", env=dict(os.environ, NAVEDIT_FAIL_READBACK="1"))
    left = sorted(p.name for p in gout.iterdir())
    assert fw.returncode != 0 and "NAVEDIT_FAIL_READBACK" in fw.stdout, f"the failing write: {write_said(fw)}"
    assert left == ["keep.txt"] and (gout / "keep.txt").read_bytes() == b"left as it was\n", f"a set whose read-back failed left {left}"


def t2684():
    """Rejects: navownprobe's repair plan carrying one member of a re-cut pair alone (refused for the other's border, the
    behaviour before R10), planning the pair's second row again as its own set (two rows changing the same navmeshes), or
    an R1 patch of the set that does not hold or is not two-way read raw. set-ground: Mover re-cuts SA and SB on its raised
    ground, linked to each other; Revert reverts both."""
    tmp, inst, game, fx, order = set_lab("set-ground")
    d = Path(tempfile.mkdtemp(prefix="r1-", dir=tmp))
    log = run([exe("navownprobe"), "--instance", inst, "--game", game, "--profile", "P", "--emit", d / "emit.json", "--repair-plan", d / "plan.json"])
    assert log.returncode == 0 and (d / "plan.json").exists(), f"no plan (exit {log.returncode}): {(log.stdout + log.stderr)[-500:]}"
    plan = json.loads((d / "plan.json").read_text(encoding="utf-8"))
    uv = {p["source"].lower(): p for p in plan["plans"] if p["class"] == "use_version"}
    assert set(uv) == {SA.lower(), SB.lower()}, f"use_version rows {[(k, p['status']) for k, p in uv.items()]}"
    seed = [p for p in uv.values() if p["edits"]]
    assert len(seed) == 1 and seed[0]["status"] == "planned", f"rows: {[(p['source'], p['status'], p['why'][:160]) for p in uv.values()]}"
    s = seed[0]
    other = [p for p in uv.values() if p is not s][0]
    assert sorted(members(s)) == sorted([SA.lower(), SB.lower()]), f"the set's members {members(s)}"
    assert other["status"] == "planned" and f"carried with {s['source']}'s set" in other["why"] and other["edits"] == [] and other["changes"] == [], \
        f"the second row: {other['status']} '{other['why']}', {len(other['edits'])} edits, changes {other['changes']}"
    out = d / "out" / "SetGround.esp"
    w = run([buildlayout.program_under(PROBES, "navedit"), "write", "--repair-plan", d / "plan.json", "--out", out])
    assert w.returncode == 0, f"R1 {write_said(w)}"
    bad, pp = raw_two_way(fx, order, out, {rk(SA), rk(SB)})
    assert not bad, f"raw: borders not two-way: {bad}"
    assert set(pp.navmeshes()) == {rk(SA), rk(SB)}, f"the patch holds {sorted(pp.navmeshes())}"
    rb = json.loads(out.with_name("SetGround.readback.json").read_text(encoding="utf-8"))
    assert rb["holds"] is True, f"the read-back: {[r for r in rb['records'] if not r['holds']]}"



def t2686():
    """Rejects (review of Gate C, decision 1): a set refused at the two-way read-back for a border neither end of which is a
    member. SS, which the set re-points and so holds whole, links one way into SQ in Skyrim.esm itself; that border is as the
    order had it. The grown set [SA, SB] must write, navedit's read-back saying its borders are two-way, and the raw reader
    must find SS -> SQ broken (the fixture really has it) but every member border two-way. That a member's one-way border is
    still refused is C2677 (the read-back) and arm 8 (the raw reader)."""
    rows, _, d, w, out = grown_set()
    assert w is not None and w.returncode == 0, f"navedit write {write_said(w)} (plan {row_said(rows)})"
    assert "is two-way" in w.stdout, f"no two-way line in the read-back: {write_said(w)}"
    tmp, _, _, fx, order = set_lab()
    every, _ = raw_two_way(fx, order, out)
    assert any(f"{rk(SS)} triangle 0 links into {rk(SQ)}" in x for x in every), f"the fixture's SS -> SQ border is not broken as written: {every}"
    mine, _ = raw_two_way(fx, order, out, {rk(SA), rk(SB)})
    assert not mine, f"raw: member borders not two-way: {mine}"


def t2687():
    """Rejects (review of Gate C, decision 2): an outside navmesh's link into a member that the member's version does not
    return planned (it would be one-way), refused without naming that navmesh in one sentence, or not taken in by growth when
    the plugin has its own version of it. SL links into SK at its west and its north; SetP's SK links back only west. SK
    alone is refused naming SL's north link; grown, SetP's own SL (which links only west) joins, and the set plans."""
    rows, _, _ = set_plan([{"navmesh": SK, "use_version": "SetP.esp"}], "InboundAlone")
    whys = (rows[0].get("why") or []) if rows else []
    assert rows and rows[0]["status"] == "refused", f"SK alone: {row_said(rows)}"
    assert any(x.startswith(f"{SL}'s triangle 2 links into {SK} triangle 0 of SetP.esp's version, whose edge there does not link back to {SL}, so carrying it would leave that border one-way")
               and one_sentence(x) for x in whys), f"SK alone: {whys}"
    rows, _, _ = set_plan([{"navmesh": SK, "use_version": "SetP.esp", "with_neighbours": True}], "InboundGrown")
    assert rows and rows[0]["status"] == "planned", f"SK grown: {row_said(rows)}"
    assert members(rows[0]) == [SK.lower(), SL.lower()], f"SK grown: members {members(rows[0])}"


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
    ("2503 plan, NAVI: L6 and R6n name each other, L6's entry whole from Skyrim.esm", t2503),
    ("2506 self-test: a relink with no NAVI entry to write is refused", t2506),
    ("2551 self-test: a renumbered row is unchanged by place", lambda: selftest("2551")),
    ("2552 self-test: rows gone, new and on another navmesh", lambda: selftest("2552")),
    ("2553 self-test: a base emit without places is refused", lambda: selftest("2553")),
    ("2554 the emit says where each link row's triangles stand", t2554),
    ("2555 self-test: grouped rows keyed per navmesh", lambda: selftest("2555")),
    ("2590 self-test: a band link with no link back is EDGE-ONEWAY", lambda: selftest("2590")),
    ("2591 self-test: no link back is written across the band", lambda: selftest("2591")),
    ("2592 self-test: an authored link back is restored", lambda: selftest("2592")),
    ("2593 self-test: a ground plan on an island makes the same navmesh edits", lambda: selftest("2593")),
    ("2594 self-test: a one-way refusal names its own neighbour", lambda: selftest("2594")),
    ("2563 a use_version patch masters what it supersedes", t2563),
    ("2566 self-test: NAVI linked doors", lambda: selftest("2566")),
    ("2595 self-test: the ground plan prefers the mover's own re-cut", lambda: selftest("2595")),
    ("2596 self-test: which mover's version replaces following the ground", lambda: selftest("2596")),
    ("2597 self-test: the mover refusal keeps the refusal order", lambda: selftest("2597")),
    ("2598 self-test: a band one-way link yields to an in-tolerance match", lambda: selftest("2598")),
    ("2603 self-test: a use_version row shares a patch only with rows it does not touch", lambda: selftest("2603")),
    ("2604 R1, mover door case: the navmesh info map's linked doors follow the version's door table", t2604),
    ("2605 self-test: the navmesh info map change for a carried version", lambda: selftest("2605")),
    ("2606 navedit, mover neighbour case: the navmesh info map drops a neighbour the version no longer links into", t2606),
    ("2599 plan, mover case: ground refused, use_version planned", t2599),
    ("2600 R1, mover case: the mover's version written, read back, its superseded plugin mastered", t2600),
    ("2601 R1, mover case: a tampered use_version row and a stand-alone read-back are refused", t2601),
    ("2658 self-test: following the ground on an island plans, with its island data", lambda: selftest("2658")),
    ("2659 self-test: a ground row's island change beside a relink of the same entry is refused", lambda: selftest("2659")),
    ("2660 navedit, island-version: use_version carries the version's own island data", t2660),
    ("2661 navedit: use_version where one side only is an island", t2661),
    ("2662 navedit, island-noentry: a version whose plugin states no entry is refused, nothing written", t2662),
    ("2663 R1, island-ground: following the ground moves the island box and mesh", t2663),
    ("2664 R1, island-ground-whole: an entry stated by an earlier plugin is added whole with its new island data", t2664),
    ("2665 navedit: a height-only move on an island", t2665),
    ("2666 navedit: a sideways move refused on an island with a mesh, written on one without", t2666),
    ("2667 navedit, island-door: a door on an island leaves its island data", t2667),
    ("2668 R1: a patch whose read-back fails is left unwritten", t2668),
    ("2671 navedit, island-sloppy-version: a version whose island box is not its navmesh's is refused", t2671),
    ("2672 navedit write: a plain write whose read-back fails is left unwritten", t2672),
    ("2673 navedit write: a patch already at --out is never touched; a write that holds leaves no staging folder", t2673),
    ("2679 navedit, set: SA and SB refused alone, planned as a set grown by need or stated", t2679),
    ("2680 navedit, set: the written set read back raw: two-way borders, inner, outer, merged and ledge links", t2680),
    ("2681 navedit, set: the navmesh info map changed once for the set, the island member, the masters", t2681),
    ("2682 navedit, set: each refusal of a stated or grown set in one sentence", t2682),
    ("2683 navedit, set: a member refused for another reason refuses the set; a refused or failing set leaves the output folder as it was", t2683),
    ("2684 R1, set-ground: the repair plan carries the pair as one set, the second row carried with it", t2684),
    ("2685 self-test: a repair plan row carried with an earlier set of the same plugin; another plugin's set clashes", lambda: selftest("2685")),
    ("2686 navedit, set: a re-pointed neighbour's broken border to a third navmesh does not refuse the set", t2686),
    ("2687 navedit, set: a link into a member its version does not return is refused, and grown in", t2687),
]

if __name__ == "__main__":
    for name, fn in CASES:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
