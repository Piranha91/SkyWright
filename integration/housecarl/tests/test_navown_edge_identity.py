"""navownprobe writes one EDGE-IDX row per broken edge link, and every identity-keyed reader keeps each one (1390-1405).

    python _geometry-harness/tests/test_navown_edge_identity.py

A navmesh's edge-link table holds one entry per crossing into a neighbouring navmesh: the neighbour and its triangle on the
far side. The game reaches an entry only through the source triangle edge that names it, so each entry is one crossing at
one place on the seam, and a link whose triangle is past the end of the neighbour's winning triangles is one broken
crossing. navownprobe writes one EDGE-IDX row per such link, but ConflictIo.RowIdentity keyed the row on (kind, the two
navmeshes) only, so the links of one navmesh into one neighbour shared an identity: conflictdiff and ordersweep kept one
and dropped the rest, and a patch that repaired some of them read as no change. The row's identity is the link: the two
navmeshes in their roles (direction kept) and the target triangle, which a patch that renumbers the source leaves alone.

The fixture is navownprobe/EdgeLinkFixture.cs (its table is the expectation), written by `navownprobe
--write-edge-link-fixture`. This file lays it out as an MO2 instance and runs the REAL navownprobe, conflictdiff,
ordersweep's emit readers and evalreport's row identity on it:

    1390  every row identity of the base emit is carried by one necessary record.
    1391  S's three broken links (target triangles 5, 6, 7) are three EDGE-IDX conflicts, each naming its target triangle.
    1392  S3's two links into the same missing triangle 7 are one conflict, which says "(2 links)".
    1393  P links Q at 4 and Q links P at 4: two conflicts with two identities (the link's direction is part of it).
    1394  conflictdiff reads as many rows as the emit has necessary records.
    1395  ordersweep's emit reader keeps one entry per necessary record.
    1396  the partial fix's --base predicates, one per link: S at 5 and 6 Gone, S at 7 and S2 at 6 Holds.
    1397  conflictdiff, base to partial fix: repaired 2, created 0, reclassified 0, unmeasured 0.
    1398  ordersweep's predicate reader on the partial fix: one entry per base link, S at 5 Gone.
    1399  evalreport's row identity divides the EDGE-IDX rows exactly as ConflictIo.RowIdentity does.
    1400  control: a patch that renumbers the source (same links, new order, new triangles) is IDENTICAL to conflictdiff.
    1401  control: under that patch every base EDGE-IDX predicate Holds.
    1402  control: another kind's rows (DOOR-TRI) are written and keyed exactly as today; an EDGE-NAV row, since 2026-11-16
          (the navmesh repair's Gate B review, finding 5), names the triangle its link names and is keyed by it as an
          EDGE-IDX row is.
    1403  control: another probe's emit (stateprobe on its row-identity fixture) is byte-identical in its rows to today's.
    1404  control: an in-range link is no row, and two sources into one target triangle are two rows.
    1405  conflictdiff refuses (exit 3, one sentence) an emit whose EDGE-IDX rows name no triangle (written before the change)
          against one whose rows do; two such emits still compare.

Each test names the wrong answer it rejects. Probes come from this checkout's Release builds unless HC_EI_HARNESS names another
build folder.
"""

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import traceback
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
PROBES = Path(os.environ.get("HC_EI_HARNESS") or buildlayout.root())
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE))
FAILURES, PASSED = [], []

PLUGINS = ["ElBase.esm", "ElA.esp", "ElFix.esp", "ElRen.esp"]
ARMS = {"base": ["ElBase.esm", "ElA.esp"], "fix": ["ElBase.esm", "ElA.esp", "ElFix.esp"],
        "ren": ["ElBase.esm", "ElA.esp", "ElRen.esp"]}


def fk(hexid):
    return f"{hexid}:ElBase.esm"


# The fixture's navmeshes by name (EdgeLinkFixture.cs).
N = {"T": fk("000960"), "S": fk("000961"), "S2": fk("000962"), "S3": fk("000963"), "P": fk("000964"), "Q": fk("000965"),
     "NX": fk("000966"), "ND": fk("000967"), "DR": fk("000901"), "ABSENT": fk("000BAE")}
NAME = {v.lower(): k for k, v in N.items()}


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def exe(root, probe):
    return buildlayout.program_under(root, probe)


def run(argv, **kw):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=600, **kw)


def identity(kind, form_key, other, a, part=None):
    """ConflictIo.RowIdentity as it is to be: unchanged without a part; with one (an EDGE-IDX row's target triangle), the
    record, the other record in their roles, and the part (ordersweep.row_identity is the shipped Python copy)."""
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    if other is None:
        return f"{kind}|{form_key}|{a}"
    other = other.lower()
    if part is not None:
        return f"{kind}|{form_key}|{other}|{part}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def alt0(c):
    return (c.get("Alternative") or [{}])[0]


def row_id(c, n):
    return identity(c["Kind"], n["FormKey"], alt0(c).get("FormKey"), c["A"], alt0(c).get("Triangle"))


class Lab:
    """EdgeLinkFixture laid out as an MO2 instance; one navownprobe run per arm (fix and ren with --base the base emit)."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "navownprobe"), "--write-edge-link-fixture", keep])
        if st.returncode != 0 or not all((keep / n).exists() for n in PLUGINS):
            raise RuntimeError(f"the fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in PLUGINS:
            d = self.inst / "mods" / Path(n).stem
            d.mkdir(parents=True)
            shutil.copyfile(keep / n, d / n)
        base = ARMS["base"]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in base), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in base), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + Path(n).stem + "\n" for n in reversed(base)), encoding="utf-8")
        self.emit, self.log, self.doc = {}, {}, {}
        for arm, plugins in ARMS.items():
            cand = tmp / f"candidate-{arm}.json"
            cand.write_text(json.dumps({"SchemaVersion": 1, "Name": f"el-{arm}", "BaseProfile": "P", "Plugins": plugins,
                                        "Mods": [Path(n).stem for n in reversed(plugins)], "Changes": []}), encoding="utf-8")
            self.emit[arm] = tmp / f"el-{arm}.json"
            argv = [exe(PROBES, "navownprobe"), "--order", cand, "--instance", self.inst, "--game", self.game, "--profile", "P",
                    "--emit", self.emit[arm]]
            if arm != "base":
                argv += ["--base", self.emit["base"]]
            self.log[arm] = run(argv)
            if self.log[arm].returncode != 0 or not self.emit[arm].exists():
                raise RuntimeError(f"navownprobe {arm} exit {self.log[arm].returncode}: {self.log[arm].stdout[-1500:]} {self.log[arm].stderr[-800:]}")
            self.doc[arm] = json.loads(self.emit[arm].read_text(encoding="utf-8"))
        self.diffs = {}

    def conflicts(self, arm="base", kind=None):
        return [c for c in self.doc[arm].get("Conflicts") or [] if kind is None or c["Kind"] == kind]

    def edge(self, src, arm="base"):
        """The EDGE-IDX conflicts whose linking navmesh is fixture name src."""
        return [c for c in self.conflicts(arm, "EDGE-IDX") if c["Necessary"][0]["FormKey"].lower() == N[src].lower()]

    def diff(self, a, b):
        if (a, b) not in self.diffs:
            self.diffs[(a, b)] = run([exe(PROBES, "conflictdiff"), self.emit[a], self.emit[b]])
        return self.diffs[(a, b)]


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="navown-edge-identity-")))
    return LAB


def show(cs):
    return "; ".join(f"{NAME.get(c['Necessary'][0]['FormKey'].lower(), c['Necessary'][0]['FormKey'])}->"
                     f"{NAME.get(alt0(c).get('FormKey', '').lower(), alt0(c).get('FormKey'))} tri {alt0(c).get('Triangle')} "
                     f"'{c['Summary'][:90]}'" for c in cs)


def records(doc):
    return sum(len(c.get("Necessary") or []) for c in doc.get("Conflicts") or [])


def line_value(text, label):
    """The number on conflictdiff's '   label : N record(s)' line."""
    for ln in text.splitlines():
        s = ln.strip()
        if s.startswith(label) and ":" in s:
            return int(s.split(":", 1)[1].split()[0].replace(",", ""))
    raise AssertionError(f"no '{label}' line in conflictdiff's output")


def t1390():
    """Rejects: an emit in which one row identity names two necessary records (a keyed reader keeps one)."""
    ids = Counter(row_id(c, n) for c in lab().conflicts() for n in c["Necessary"])
    dup = {k: v for k, v in ids.items() if v > 1}
    assert not dup, f"{len(dup)} identities carry more than one record: {dup}"


def t1391():
    """Rejects: S's three broken links not each a conflict that names its own target triangle."""
    cs = lab().edge("S")
    got = sorted((alt0(c).get("Triangle"), alt0(c).get("FormKey", "").lower()) for c in cs)
    assert got == [(5, N["T"].lower()), (6, N["T"].lower()), (7, N["T"].lower())], f"{len(cs)} conflicts: {show(cs)}"
    for c in cs:
        t = alt0(c)["Triangle"]
        assert f"edge-links {N['T']} triangle {t} of 5   [" in c["Summary"], c["Summary"]


def t1392():
    """Rejects: two conflicts for S3's two links into the one missing triangle 7, or one that does not say there are two."""
    cs = lab().edge("S3")
    assert len(cs) == 1, f"{len(cs)} conflicts: {show(cs)}"
    assert alt0(cs[0]).get("Triangle") == 7 and f"triangle 7 of 5 (2 links)   [" in cs[0]["Summary"], show(cs)


def t1393():
    """Rejects: P's link into Q and Q's link into P under one identity (the unordered pair)."""
    p, q = lab().edge("P"), lab().edge("Q")
    assert len(p) == 1 and len(q) == 1, f"P {show(p)} | Q {show(q)}"
    a, b = row_id(p[0], p[0]["Necessary"][0]), row_id(q[0], q[0]["Necessary"][0])
    assert a != b, f"one identity for both directions: {a}"


def t1394():
    """Rejects: conflictdiff counting fewer rows than the emit has necessary records (one link silently dropped)."""
    L = lab()
    r = L.diff("base", "base")
    line = next((x for x in r.stdout.splitlines() if x.startswith("base")), "")
    n, k = records(L.doc["base"]), len(L.conflicts())
    assert f"({k:,} conflicts, {n:,} rows;" in line, f"exit {r.returncode}: {line!r} (emit has {n} necessary records)"


def t1395():
    """Rejects: ordersweep's emit reader keeping fewer entries than the emit has necessary records."""
    import ordersweep
    L = lab()
    got = ordersweep.emit_rows(L.emit["base"])
    assert len(got) == records(L.doc["base"]), f"{len(got)} entries for {records(L.doc['base'])} necessary records"


def t1396():
    """Rejects: a partial repair re-measured as one question per navmesh pair (all Holds while two links are fixed)."""
    ps = [p for p in lab().doc["fix"].get("Predicates") or [] if p["Kind"] == "EDGE-IDX"]
    got = {(NAME.get(p["FormKey"].lower()), p.get("OtherTriangle")): p["Physical"] for p in ps}
    want = {("S", 5): "Gone", ("S", 6): "Gone", ("S", 7): "Holds", ("S2", 6): "Holds", ("S3", 7): "Holds",
            ("P", 4): "Holds", ("Q", 4): "Holds"}
    assert got == want, f"predicates {got}"


def t1397():
    """Rejects: the partial fix read as no repair, or as repair plus created rows."""
    r = lab().diff("base", "fix")
    got = {k: line_value(r.stdout, k) for k in ("repaired", "created", "reclassified", "unmeasured")}
    assert got == {"repaired": 2, "created": 0, "reclassified": 0, "unmeasured": 0}, f"exit {r.returncode}: {got}\n{r.stdout[-1200:]}"


def t1398():
    """Rejects: ordersweep's predicate reader folding the partial fix's links into one entry per pair."""
    import ordersweep
    L = lab()
    got = ordersweep.emit_predicates(L.emit["fix"])
    base_edge = [(c, n) for c in L.conflicts("base", "EDGE-IDX") for n in c["Necessary"]]
    edge_keys = [k for k in got if k.startswith("edge-idx|")]
    assert len(edge_keys) == len(base_edge), f"{len(edge_keys)} EDGE-IDX entries for {len(base_edge)} base links: {edge_keys}"
    s5 = identity("EDGE-IDX", N["S"], N["T"], "", 5)
    assert got.get(s5) == "Gone", f"S at 5: {got.get(s5)}"


def t1399():
    """Rejects: evalreport and ConflictIo.RowIdentity dividing the EDGE-IDX rows differently (a report row per link, a diff
    row per pair)."""
    import evalreport
    cs = lab().conflicts("base", "EDGE-IDX")
    ev = [evalreport.row_identity(x) for c in cs for x in evalreport.conflict_rows(c, None)]
    ci = [row_id(c, n) for c in cs for n in c["Necessary"]]
    assert len(ev) == len(ci), (len(ev), len(ci))
    assert len(set(ev)) == len(ev) and len(set(ci)) == len(ci) and len(set(zip(ev, ci))) == len(ev), \
        f"{len(cs)} conflicts: evalreport {len(set(ev))} identities, RowIdentity {len(set(ci))}"


def t1400():
    """Rejects: an identity a renumbered source changes (the link's table position or the source triangle): the same broken
    links read as repaired and created."""
    r = lab().diff("base", "ren")
    got = {k: line_value(r.stdout, k) for k in ("repaired", "created", "reclassified", "unmeasured")}
    assert "IDENTICAL" in r.stdout and got == {"repaired": 0, "created": 0, "reclassified": 0, "unmeasured": 0}, \
        f"exit {r.returncode}: {got}\n{r.stdout[-1200:]}"


def t1401():
    """Rejects: a base link re-measured as gone under a patch that only renumbers its source."""
    ps = [p for p in lab().doc["ren"].get("Predicates") or [] if p["Kind"] == "EDGE-IDX"]
    assert ps and all(p["Physical"] == "Holds" for p in ps), [(p["FormKey"], p.get("OtherTriangle"), p["Physical"]) for p in ps]


def t1402():
    """Rejects: a change to another kind's row: its fields, or its identity (EDGE-NAV's per-link identity is the one change)."""
    import ordersweep
    L = lab()
    nav, door = L.conflicts("base", "EDGE-NAV"), L.conflicts("base", "DOOR-TRI")
    assert len(nav) == 1 and len(door) == 1, f"EDGE-NAV {len(nav)}, DOOR-TRI {len(door)}"
    want_nav = {"Kind": "EDGE-NAV", "A": "ElBase.esm", "B": "",
                "Summary": f"navmesh {N['NX']} in 000840:ElBase.esm (int) edge-links {N['ABSENT']} triangle 0, which is not in the "
                           "winning state   [record provided by: ElBase.esm]",
                "Necessary": [{"FormKey": N["NX"], "Role": "navmesh", "Label": "000840:ElBase.esm (int)", "Physical": "Holds",
                               "PhysicalDetail": "the edge link still points into a navmesh the winning state lacks"}],
                "Alternative": [{"FormKey": N["ABSENT"], "Role": "navmesh", "Triangle": 0}]}
    want_door = {"Kind": "DOOR-TRI", "A": "ElBase.esm", "B": "",
                 "Summary": f"navmesh {N['ND']} in 000840:ElBase.esm (int) points at triangle 1 of 1   [record provided by: ElBase.esm]",
                 "Necessary": [{"FormKey": N["ND"], "Role": "navmesh", "Label": "000840:ElBase.esm (int)", "Physical": "Holds",
                                "PhysicalDetail": "the door triangle index still exceeds the winning count (1)"}],
                 "Alternative": [{"FormKey": N["DR"], "Role": "door"}]}
    assert nav[0] == want_nav, json.dumps(nav[0], ensure_ascii=False)
    assert door[0] == want_door, json.dumps(door[0], ensure_ascii=False)
    keys = set(ordersweep.emit_rows(L.emit["base"]))
    for c, other, part in ((nav[0], N["ABSENT"], 0), (door[0], N["DR"], None)):
        key = identity(c["Kind"], c["Necessary"][0]["FormKey"], other, c["A"], part)
        assert key in keys, f"{key} not among ordersweep's keys"


# The canonical sha256 of stateprobe's row-identity fixture emit (Conflicts, Predicates, Coverage, sorted keys). Was
# fe4fa6b9... before 2026-10-24; since 2026-10-24 (lights with no model set aside by name,
# Gate C review M3) "changed references with a box" also carries SetAside {"LightWithNoModel": 0}, and nothing else differs.
# Was d87e7975... until 2026-10-25 (activators, doors and editor markers set aside by name; a marker carrying a primitive unjudged
# apart): the same line also carries SetAside ActivatorWithNoModel, DoorWithNoModel, EditorMarker and Unjudgeable MarkerPrimitive,
# all 0 on this fixture, and nothing else differs.
# Was f30ce37c... until 2026-11-08 (item 30: marker primitives by station Q's table, the rest of NoBox named by cause): the
# same line also carries Unjudgeable MarkerPrimitiveSolid, Actor, PlacedTrap, EffectVolume, TypeOutsideRoles, OverSizeCap and
# SetAside MarkerPrimitiveNotSolid, Decal, all 0 on this fixture; the old emit with those keys added hashes to this value.
# Was e72e787e... until 2026-11-10 (stateprobe lane: solid collision-marker boxes, placed actors read from placement, the table
# named in the emit). Its Conflicts and Predicates are unchanged; in Coverage only: "changed references with a box" drops the
# Unjudgeable keys Actor and MarkerPrimitiveSolid (both 0 here) and gains the note that no primitive table was given, and a new
# line "changed placed actors" (all 0 here) follows it. The base emit with exactly those changes hashes to this value.
STATE_ROWS_SHA = "32771cec94bc5b94496b8f782bb99c6309eb5dc88038ba619bd95e126d53c458"


def t1403():
    """Rejects: a change to another probe's rows through the shared format (stateprobe on its own fixture)."""
    import test_state_row_identity as SR
    SR.PROBES = PROBES
    L = SR.lab()
    canon = json.dumps({k: L.doc.get(k) for k in ("Conflicts", "Predicates", "Coverage")}, sort_keys=True, ensure_ascii=False)
    got = hashlib.sha256(canon.encode("utf-8")).hexdigest()
    assert got == STATE_ROWS_SHA, f"stateprobe fixture rows sha256 {got}, today's {STATE_ROWS_SHA}"
    assert '"Triangle"' not in canon and '"OtherTriangle"' not in canon, "a stateprobe row carries a triangle"


def t1404():
    """Rejects: an in-range link written as a row, or two sources into one target triangle folded into one row."""
    L = lab()
    s1 = [c for c in L.edge("S") if f"triangle 1 of" in c["Summary"]]
    assert not s1, show(s1)
    six = [c for c in L.conflicts("base", "EDGE-IDX") if "triangle 6 of 5" in c["Summary"]]
    assert sorted(NAME.get(c["Necessary"][0]["FormKey"].lower()) for c in six) == ["S", "S2"], show(six)


def t1405():
    """Rejects: a pre-change emit (EDGE-IDX rows with no Triangle) compared row for row with a per-link one, which reads its
    pair rows as vanished and the links as created; and a refusal of two pre-change emits, which key alike."""
    L = lab()
    old = {}
    for arm in ("base", "fix"):
        d = json.loads(L.emit[arm].read_text(encoding="utf-8"))
        for c in d["Conflicts"]:
            for x in c.get("Alternative") or []:
                x.pop("Triangle", None)
        for p in d.get("Predicates") or []:
            p.pop("OtherTriangle", None)
        old[arm] = L.tmp / f"el-{arm}.pre-change.json"
        old[arm].write_text(json.dumps(d), encoding="utf-8")
    r = run([exe(PROBES, "conflictdiff"), old["base"], L.emit["fix"]])
    refused = [x for x in r.stdout.splitlines() if "REFUSED" in x]
    assert r.returncode == 3 and len(refused) == 1 and "re-run that arm on the current build" in refused[0],         f"exit {r.returncode}: {refused or r.stdout[-600:]}"
    r2 = run([exe(PROBES, "conflictdiff"), old["base"], old["fix"]])
    assert r2.returncode == 0, f"two pre-change emits: exit {r2.returncode}: {r2.stdout[-600:]}"


CASES = [
    ("1390 every identity of the emit is one record", t1390),
    ("1391 S's three broken links: three conflicts, each naming its triangle", t1391),
    ("1392 S3's two links into one missing triangle: one conflict, '(2 links)'", t1392),
    ("1393 P into Q and Q into P: two identities", t1393),
    ("1394 conflictdiff reads every link", t1394),
    ("1395 ordersweep's emit reader keeps every link", t1395),
    ("1396 partial fix: one predicate per link", t1396),
    ("1397 partial fix: conflictdiff repaired 2, created 0", t1397),
    ("1398 partial fix: ordersweep's predicate reader keeps every link", t1398),
    ("1399 evalreport and RowIdentity divide the rows alike", t1399),
    ("1400 control: a renumbered source is IDENTICAL", t1400),
    ("1401 control: a renumbered source keeps every link Holds", t1401),
    ("1402 control: another kind's rows and identities as today", t1402),
    ("1403 control: another probe's rows byte-identical", t1403),
    ("1404 control: an in-range link is no row; two sources are two rows", t1404),
    ("1405 conflictdiff refuses a pre-change emit against a per-link one", t1405),
]

if __name__ == "__main__":
    for name, fn in CASES:
        check(name, fn)
    print(f"\n{len(PASSED)} ok, {len(FAILURES)} FAIL")
    sys.exit(1 if FAILURES else 0)
