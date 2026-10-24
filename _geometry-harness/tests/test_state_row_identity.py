"""stateprobe writes one row per pair of references in a state, whichever contact pass found it (cases 1370-1381).

    python _geometry-harness/tests/test_state_row_identity.py

Two of the state class's contact passes can each write a row for the same two references in one state: "the arriving host
encloses the dependent" (the member is the host) and "the arriving dependent sits inside something" (the member is the
dependent). They are one interpenetration in the game, but every consumer that keys rows by ConflictIo.RowIdentity (kind
and the unordered pair) kept one of them and silently lost the other (conflictdiff, ordersweep, a scope audit). The two
passes read the same pair through the same primitive, sometimes in the two directions: one reference's deepest vertex inside
the other's solid, and the reverse. Both are lower bounds on how far one must move to clear the other, so the merged row
keeps the deeper; when the two read the same direction they are the same number, and the row is the dependent's.

The fixture is _shared/RowIdentityFixture.cs (its table is the expectation), written by `stateprobe
--write-row-identity-fixture`. This file lays it out as an MO2 instance and runs the REAL stateprobe, conflictdiff and
ordersweep's emit reader on it:

    1370  every row identity of the emit is carried by one conflict.
    1371  R1, one arriving slab both host and dependent against a cup already there: one --json row for the pair, and its depth
          is the deeper direction (the cup 38 u inside the slab, not the slab 18 u inside the cup).
    1372  R1 in the emit: one conflict for the pair, which says 38u.
    1373  R2, an arriving host and an arriving dependent under two switches of one state, read the same way (38 u): one row,
          and its member is the dependent.
    1374  R3, two arriving boxes each host and dependent of the other (four rows today): one row, the deeper (59 u), the
          dependent pass's.
    1375  R2S, the R2 shape inside one set: one --json row, and still withheld from the emit (same set).
    1376  conflictdiff reads as many rows as the emit has conflicts.
    1377  ordersweep's emit reader keeps one entry per conflict.
    1378  the --json tally counts the rows merged into another (7 in this fixture).
    1379  control: a dependent sunk into something other than the host keeps its own row (C1: the slab encloses the cup 38 u,
          and the slab sits 30 u in the floor: two rows, two identities, each as written today).
    1380  control: one pair in two states is two rows, one per state.
    1381  control: a row no other row shares (the C1 floor row) is written exactly as it is today.

Each test names the wrong answer it rejects. Probes come from this checkout's Release builds unless HC_RI_HARNESS names another
harness folder.
"""

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
HARNESS = HERE.parent
PROBES = Path(os.environ.get("HC_RI_HARNESS") or HARNESS)
sys.path.insert(0, str(HARNESS))
FAILURES, PASSED = [], []

ORDER = ["RiBase.esm", "RiA.esp", "RiB.esp"]
STATES = ["RiSwR1", "RiSwC1", "RiSwR2h,RiSwR2d", "RiSwR2s", "RiSwR3a,RiSwR3b", "RiSwR1,RiSwX"]


def fk(hexid, plugin):
    return f"{hexid}:{plugin}"


# The fixture's references by name (RowIdentityFixture.cs).
R = {"O1": fk("000901", "RiBase.esm"), "O2": fk("000902", "RiBase.esm"), "F2": fk("000903", "RiBase.esm"),
     "M1": fk("000902", "RiA.esp"), "M2": fk("000904", "RiA.esp"), "H2": fk("000906", "RiA.esp"), "H2s": fk("000908", "RiA.esp"),
     "A3": fk("00090A", "RiA.esp"), "D2": fk("000902", "RiB.esp"), "D2s": fk("000903", "RiB.esp"), "B3": fk("000905", "RiB.esp")}
NAME = {v.lower(): k for k, v in R.items()}


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
    return root / probe / "bin" / "Release" / "net9.0" / (probe + ".exe")


def run(argv, **kw):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=600, **kw)


def identity(kind, form_key, other, a):
    """ConflictIo.RowIdentity, mirrored (ordersweep.row_identity is the shipped Python copy; 1377 drives it)."""
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    if other is None:
        return f"{kind}|{form_key}|{a}"
    other = other.lower()
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


class Lab:
    """RowIdentityFixture laid out as an MO2 instance; one stateprobe run over every state, shared by the checks."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "stateprobe"), "--write-row-identity-fixture", keep])
        ri = keep / "ri"
        if st.returncode != 0 or not all((ri / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in ORDER:
            stem = Path(n).stem
            src = ri / "mods" / stem
            if src.exists():
                shutil.copytree(src, self.inst / "mods" / stem)
            else:
                (self.inst / "mods" / stem).mkdir(parents=True)
            shutil.copyfile(ri / n, self.inst / "mods" / stem / n)
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        self.cand = tmp / "candidate-ri.json"
        self.cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "ri", "BaseProfile": "P", "Plugins": ORDER, "Mods": mods,
                                         "Changes": []}), encoding="utf-8")
        self.emit, self.json = tmp / "ri-emit.json", tmp / "ri-rows.json"
        argv = [exe(PROBES, "stateprobe"), "--order", self.cand, "--instance", self.inst, "--game", self.game, "--profile", "P",
                "--route", "off", "--emit", self.emit, "--json", self.json]
        for s in STATES:
            argv += ["--state", s]
        self.log = run(argv)
        if self.log.returncode != 0 or not self.emit.exists() or not self.json.exists():
            raise RuntimeError(f"stateprobe exit {self.log.returncode}: {self.log.stdout[-1500:]} {self.log.stderr[-800:]}")
        self.doc = json.loads(self.emit.read_text(encoding="utf-8"))
        self.rows = json.loads(self.json.read_text(encoding="utf-8"))

    def conflicts(self):
        return self.doc.get("Conflicts") or []

    def contact_rows(self, state, a, b):
        """The --json contact rows of one state whose two references are fixture names a and b, either way round."""
        pair = {R[a].lower(), R[b].lower()}
        return [r for r in self.rows["rows"] if r["kind"] == "Contact" and r["state"] == state
                and {r["member"].lower(), (r["other"] or "").lower()} == pair]

    def pair_conflicts(self, state, a, b):
        pair = {R[a].lower(), R[b].lower()}
        out = []
        for c in self.conflicts():
            if c["Kind"].split("@")[1] != state:
                continue
            other = (c.get("Alternative") or [{}])[0].get("FormKey", "")
            for n in c["Necessary"]:
                if {n["FormKey"].lower(), other.lower()} == pair:
                    out.append(c)
        return out


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="state-row-identity-")))
    return LAB


def show(rows):
    return "; ".join(f"{NAME.get(r['member'].lower(), r['member'])}/{NAME.get((r['other'] or '').lower(), r['other'])} "
                     f"{r['penetration']} '{r['Why']}'" for r in rows)


def t1370():
    """Rejects: an emit in which one row identity names two conflicts (a keyed consumer keeps one)."""
    L = lab()
    ids = Counter(identity(c["Kind"], n["FormKey"], (c.get("Alternative") or [{}])[0].get("FormKey"), c["A"])
                  for c in L.conflicts() for n in c["Necessary"])
    dup = {k: v for k, v in ids.items() if v > 1}
    assert not dup, f"{len(dup)} identities carry more than one conflict: {dup}"


def t1371():
    """Rejects: two rows for R1's pair, or one carrying the shallower direction (the slab 18 u inside the cup)."""
    rs = lab().contact_rows("RiSwR1", "M1", "O1")
    assert len(rs) == 1, f"{len(rs)} rows for the pair: {show(rs)}"
    assert abs(rs[0]["penetration"] - 38.0) < 0.5, f"depth {rs[0]['penetration']}, want 38 (the cup's vertex inside the slab): {show(rs)}"
    assert "38" in rs[0]["Why"] and "38" in rs[0]["physicalDetail"], show(rs)


def t1372():
    """Rejects: R1's pair as two conflicts in the emit, or one that does not say the deeper 38 u."""
    cs = lab().pair_conflicts("RiSwR1", "M1", "O1")
    assert len(cs) == 1, f"{len(cs)} conflicts: {[c['Summary'] for c in cs]}"
    assert "38u" in cs[0]["Summary"], cs[0]["Summary"]


def t1373():
    """Rejects: two rows for R2's pair, or the one row naming the host as its member (the dependent is what sits inside)."""
    rs = lab().contact_rows("RiSwR2h,RiSwR2d", "H2", "D2")
    assert len(rs) == 1, f"{len(rs)} rows: {show(rs)}"
    r = rs[0]
    assert r["member"].lower() == R["D2"].lower() and r["other"].lower() == R["H2"].lower(), show(rs)
    assert abs(r["penetration"] - 38.0) < 0.5 and r["Why"].startswith("Sunk: the arriving dependent sits 38u inside"), show(rs)
    cs = lab().pair_conflicts("RiSwR2h,RiSwR2d", "H2", "D2")
    assert len(cs) == 1 and cs[0]["Necessary"][0]["FormKey"].lower() == R["D2"].lower(), [c["Summary"] for c in cs]


def t1374():
    """Rejects: R3's four rows (each box as host and as dependent), or a merged row that is not the deeper 59 u."""
    rs = lab().contact_rows("RiSwR3a,RiSwR3b", "A3", "B3")
    assert len(rs) == 1, f"{len(rs)} rows: {show(rs)}"
    r = rs[0]
    assert abs(r["penetration"] - 59.0) < 0.5, show(rs)
    assert r["member"].lower() == R["B3"].lower() and r["Why"].startswith("Sunk: the arriving dependent sits 59u inside"), show(rs)


def t1375():
    """Rejects: two --json rows for a same-set pair, or the merged row reaching the emit (a set against its own members)."""
    rs = lab().contact_rows("RiSwR2s", "H2s", "D2s")
    assert len(rs) == 1 and rs[0]["sameSet"], f"{len(rs)} rows: {show(rs)}"
    assert not lab().pair_conflicts("RiSwR2s", "H2s", "D2s"), "a same-set row reached the emit"


def t1376():
    """Rejects: conflictdiff counting fewer rows than the emit has conflicts (one conflict silently dropped)."""
    L = lab()
    r = run([exe(PROBES, "conflictdiff"), L.emit, L.emit])
    line = next((x for x in r.stdout.splitlines() if x.startswith("base")), "")
    n = len(L.conflicts())
    assert f"({n:,} conflicts, {n:,} rows;" in line, f"exit {r.returncode}: {line!r} (emit has {n} conflicts)"


def t1377():
    """Rejects: ordersweep's emit reader keeping fewer entries than the emit has necessary records."""
    import ordersweep
    L = lab()
    n = sum(len(c["Necessary"]) for c in L.conflicts())
    got = ordersweep.emit_rows(L.emit)
    assert len(got) == n, f"{len(got)} entries for {n} necessary records"


def t1378():
    """Rejects: rows merged without being counted (R1 twice, R2, R2S: one each; R3: three)."""
    t = lab().rows["tally"]
    assert t.get("MergedContactRows") == 5 + 2, f"tally {t}"


def t1379():
    """Rejects: a merge that folds the slab's floor row into its cup row because they share the slab (two findings)."""
    L = lab()
    cup, floor = L.contact_rows("RiSwC1", "M2", "O2"), L.contact_rows("RiSwC1", "M2", "F2")
    assert len(cup) == 1 and len(floor) == 1, f"cup {show(cup)} | floor {show(floor)}"
    assert cup[0]["Why"] == "Sunk: the arriving host encloses the dependent 38u deep" and abs(cup[0]["penetration"] - 38.0) < 0.5, show(cup)
    assert floor[0]["Why"] == "Sunk: the arriving dependent sits 30u inside something enabled in the state" and abs(floor[0]["penetration"] - 30.0) < 0.5, show(floor)
    assert len(L.pair_conflicts("RiSwC1", "M2", "O2")) == 1 and len(L.pair_conflicts("RiSwC1", "M2", "F2")) == 1


def t1380():
    """Rejects: a merge across states (R1's pair is a finding in each state that brings the slab in)."""
    L = lab()
    ids = {identity(c["Kind"], n["FormKey"], (c.get("Alternative") or [{}])[0].get("FormKey"), c["A"])
           for st in ("RiSwR1", "RiSwR1,RiSwX") for c in L.pair_conflicts(st, "M1", "O1") for n in c["Necessary"]}
    assert len(ids) == 2, ids


def t1381():
    """Rejects: a change to a row no other row shares (the C1 floor row, every field of its conflict)."""
    cs = lab().pair_conflicts("RiSwC1", "M2", "F2")
    assert len(cs) == 1, cs
    want = {"Kind": "STATE-CONTACT@RiSwC1@(no map)", "A": "RiA.esp", "B": "RiBase.esm",
            "Summary": f"Contact StateConditional under RiSwC1 (RiA.esp): {R['M2']} [Static RiSlab] arrives — Sunk: the arriving "
                       "dependent sits 30u inside something enabled in the state in RiRoomC1 (int)",
            "Necessary": [{"FormKey": R["M2"], "Role": "placed-ref", "Label": "RiRoomC1 (int)", "Physical": "Holds",
                           "PhysicalDetail": f"30u inside {R['F2']}"}],
            "Alternative": [{"FormKey": R["F2"], "Role": "placed-ref"}]}
    got = {k: cs[0].get(k) for k in want}
    got["Necessary"] = [{k: n.get(k) for k in want["Necessary"][0]} for n in got["Necessary"]]
    got["Alternative"] = [{k: n.get(k) for k in want["Alternative"][0]} for n in got["Alternative"]]
    assert got == want, json.dumps(got, ensure_ascii=False)


CASES = [
    ("1370 every identity of the emit is one conflict", t1370),
    ("1371 R1: one row, the deeper direction (38 u)", t1371),
    ("1372 R1 in the emit: one conflict, 38u", t1372),
    ("1373 R2: one row, the dependent's", t1373),
    ("1374 R3: four rows become one, the deeper (59 u)", t1374),
    ("1375 R2S: one row, still withheld as the same set", t1375),
    ("1376 conflictdiff reads every conflict", t1376),
    ("1377 ordersweep's emit reader keeps every conflict", t1377),
    ("1378 the tally counts merged rows", t1378),
    ("1379 control: a dependent sunk into something else keeps its own row", t1379),
    ("1380 control: one pair in two states is two rows", t1380),
    ("1381 control: an unshared row is written as today", t1381),
]

if __name__ == "__main__":
    for name, fn in CASES:
        check(name, fn)
    print(f"\n{len(PASSED)} ok, {len(FAILURES)} FAIL")
    sys.exit(1 if FAILURES else 0)
