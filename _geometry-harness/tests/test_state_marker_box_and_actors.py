"""stateprobe: the measured primitive table's identity in the emit, collision-marker boxes measured solid as route obstacles,
and placed actors a state brings in (cases 1734-1747; the evaluation half, 1730-1733, is in test_evalrun.py).

    python _geometry-harness/tests/test_state_marker_box_and_actors.py

The fixture is _shared/StateLaneFixture.cs (its table is the layout), written by `stateprobe --write-state-lane-fixture`. This
file lays it out as an MO2 instance and runs the REAL stateprobe (route on) and conflictdiff on it. The test writes its own
primitive table naming the fixture's CollisionMarker-shaped base: solid with no layer, not solid on L_NONCOLLIDABLE.

The table's identity:
    1734  the "changed references with a box" line's note names the table (its sha256) when one is given, and says no table
          was given otherwise.
    1735  the emit's measurement policy carries the table: state-contact-route-v5|...|primitive-table=<sha12> or =none.
    1736  conflictdiff refuses an emit measured with the table against one measured without it, both ways round.
Collision-marker boxes (station Q measured Box primitives with no layer to stop the player):
    1737  the route row for the solid marker box says it is a collision marker's box and that NPC pathing is not measured.
    1738  the solid marker box arriving across a corridor (MxSwRB) writes one STATE-ROUTE row naming it, with the verdict the
          barrier static of the same size reads there (MxSwRW, the control).
    1739  guard: no row names the marker measured not solid (MxSwRN) or the Sphere (MxSwRS); without the table the box writes
          no row either.
Placed actors a state brings in ("changed placed actors"; no actor is ever a row):
    1741  an actor the switch disables (MxSwAD): set aside, ActorDeparts.
    1742  an actor arriving over the navmesh, on the floor, its body clear (MxSwAC): judged from placement (ActorClear), and the
          line says it is judged from placement, not checked in game; the with-box line no longer carries Actor.
    1743  arriving 300 u beyond the navmesh's edge (MxSwAO): unjudged, ActorOffNavmesh.
    1744  arriving 200 u above the floor (MxSwAF): unjudged, ActorFloating; 200 u below it (MxSwAB): ActorBuried. Both are over
          the navmesh, so neither is read as off it.
    1745  arriving with its body through a barrier (MxSwAS): unjudged, ActorInSolid.
    1746  a creature (MxSwAK), a leveled actor (MxSwAL), and the clear actor with the route off: unjudged, ActorUnmeasured.
    1747  guard: no row names an actor in any state, the creature's included.
Exterior actors (the MxOutside worldspace: EA and EB side by side, each with its own navmesh):
    1748  an actor arriving in EA 104 u inside EB's navmesh (MxSwEN): judged from placement (ActorClear). The neighbouring
          cells' navmesh counts; the actor's own cell's alone would read it off the navmesh.
    1749  guard: an actor arriving in EA beyond both cells' navmeshes (MxSwEO): unjudged, ActorOffNavmesh.

HC_SL_DUMP=DIR keeps every emit and log. Probes come from this checkout's Release builds unless HC_SL_HARNESS names another
build folder. Each test names the wrong answer it rejects.
"""

import hashlib
import json
import os
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
PROBES = Path(os.environ.get("HC_SL_HARNESS") or buildlayout.root())
FAILURES, PASSED = [], []

ORDER = ["MxRoom.esm", "MxObjects.esp"]
CELLS = ["RW", "RB", "RN", "RS", "AC", "AO", "AF", "AS", "AD", "AK", "AL", "AB"]   # StateLaneFixture.Cells
EXTERIOR = ["EN", "EO"]   # StateLaneFixture.ExteriorCases
COLLISION_BOX = "000804:MxRoom.esm"                                            # StateLaneFixture.CollisionBox
TABLE = [{"kind": "base", "value": COLLISION_BOX, "layer": "none", "verdict": "solid", "stops": ["player", "items"], "evidence": "fixture"},
         {"kind": "base", "value": COLLISION_BOX, "layer": "L_NONCOLLIDABLE", "verdict": "not_solid", "stops": [], "evidence": "fixture"}]
BOX_LINE, ACTOR_LINE = "changed references with a box", "changed placed actors"


def member(c):
    return f"{0x920 + CELLS.index(c):06X}:MxObjects.esp".lower()


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


class Lab:
    """StateLaneFixture laid out as an MO2 instance; one stateprobe run per state, with the table and without, shared by the checks."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "stateprobe"), "--write-state-lane-fixture", keep])
        mx = keep / "mx"
        if st.returncode != 0 or not all((mx / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in ORDER:
            stem = Path(n).stem
            src = mx / "mods" / stem
            if src.exists():
                shutil.copytree(src, self.inst / "mods" / stem)
            else:
                (self.inst / "mods" / stem).mkdir(parents=True)
            shutil.copyfile(mx / n, self.inst / "mods" / stem / n)
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        self.cand = tmp / "candidate-mx.json"
        self.cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "mx", "BaseProfile": "P", "Plugins": ORDER, "Mods": mods,
                                         "Changes": []}), encoding="utf-8")
        self.table = tmp / "table.json"
        self.table.write_text(json.dumps(TABLE), encoding="utf-8")
        self.table_sha12 = hashlib.sha256(self.table.read_bytes()).hexdigest()[:12]
        tail = ["--order", self.cand, "--instance", self.inst, "--game", self.game, "--profile", "P"]
        self.logs, self.out = {}, {}

        def go(key, *extra):
            self.out[key] = tmp / f"{key}.json"
            self.logs[key] = run([exe(PROBES, "stateprobe"), *tail, "--emit", self.out[key], "--json", tmp / f"{key}.rows.json", *extra])
            lg = self.logs[key]
            if lg.returncode != 0 or not self.out[key].exists():
                raise RuntimeError(f"{key} run exit {lg.returncode}: {lg.stdout[-1500:]} {lg.stderr[-800:]}")

        for c in CELLS:
            go("tab-" + c, "--state", f"MxSw{c}", "--primitive-solidity", self.table)
        for c in EXTERIOR:
            go("tab-" + c, "--state", f"MxSw{c}", "--primitive-solidity", self.table)
        for c in ("RB", "AC"):
            go("none-" + c, "--state", f"MxSw{c}")
        go("off-AC", "--state", "MxSwAC", "--primitive-solidity", self.table, "--route", "off")
        states = [x for c in CELLS for x in ("--state", f"MxSw{c}")]
        go("all-tab", *states, "--primitive-solidity", self.table)
        go("all-none", *states)

    def doc(self, key):
        return json.loads(self.out[key].read_text(encoding="utf-8"))

    def rows_json(self, key):
        return json.loads((self.tmp / f"{key}.rows.json").read_text(encoding="utf-8"))


def keys(c):
    return [x["FormKey"].lower() for x in (c.get("Necessary") or []) + (c.get("Alternative") or [])]


def naming(doc, cell):
    return [c for c in doc["Conflicts"] if member(cell) in keys(c)]


def show(rows):
    return " | ".join(f"{c['Kind']}: {c['Summary'][:200]}" for c in rows) or "(none)"


def cov(doc, line):
    return next((c for c in doc["Coverage"] if c["Population"] == line), None)


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))

        def c1734():
            # Rejects: an emit that does not say which table decided its marker primitives, or that none did.
            got = cov(L.doc("tab-RB"), BOX_LINE) or {}
            none = cov(L.doc("none-RB"), BOX_LINE) or {}
            assert L.table_sha12 in str(got.get("Note") or ""), f"with the table, the note does not name it ({L.table_sha12}): {got}"
            assert "no primitive table" in str(none.get("Note") or "").lower(), f"without the table, the note does not say so: {none}"

        def c1735():
            # Rejects: a policy that does not change with the table, so emits measured differently compare silently.
            a, b = str(L.doc("tab-RB").get("MeasurementPolicy")), str(L.doc("none-RB").get("MeasurementPolicy"))
            assert a.startswith("state-contact-route-v5|") and a.endswith(f"|primitive-table={L.table_sha12}"), a
            assert b.startswith("state-contact-route-v5|") and b.endswith("|primitive-table=none"), b

        def c1736():
            # Rejects: a with-table emit diffed against a without-table one, crediting the marker rows as created or repaired.
            out = []
            for a, b in ((L.out["all-tab"], L.out["all-none"]), (L.out["all-none"], L.out["all-tab"])):
                r = run([exe(PROBES, "conflictdiff"), a, b])
                out.append((a.name, r.returncode, r.returncode == 3 and "measurement policy" in r.stdout and "REFUSED" in r.stdout))
            assert all(o[2] for o in out), f"conflictdiff did not refuse both ways: {out}"

        def route_rows(key, cell):
            return [c for c in naming(L.doc(key), cell) if c["Kind"].startswith("STATE-ROUTE@")]

        def c1737():
            # Rejects: a marker-box row that reads as an ordinary object, or claims a measured NPC block.
            rows = route_rows("tab-RB", "RB")
            assert len(rows) == 1, f"want one STATE-ROUTE row for the marker box: {show(naming(L.doc('tab-RB'), 'RB'))}"
            detail = " ".join(str(n.get("PhysicalDetail") or "") for n in rows[0].get("Necessary") or []).lower() + " " + rows[0]["Summary"].lower()
            assert "collision marker" in detail and "npc" in detail and "not measured" in detail and "invisible" in detail, detail

        def c1738():
            # Rejects: a solid collision box the state brings in across a corridor left without a route judgement.
            ctl = route_rows("tab-RW", "RW")
            assert len(ctl) == 1, f"control: want one STATE-ROUTE row for the barrier static: {show(naming(L.doc('tab-RW'), 'RW'))}"
            rows = route_rows("tab-RB", "RB")
            assert len(rows) == 1, f"want one STATE-ROUTE row for the marker box: {show(naming(L.doc('tab-RB'), 'RB'))}"
            verdict = lambda r: r["Summary"].split(" — ", 1)[-1].split(":", 1)[0]
            assert verdict(rows[0]) == verdict(ctl[0]), f"marker {show(rows)}; barrier {show(ctl)}"

        def c1739():
            # Rejects: a box given to a marker the table calls not solid, does not measure (a Sphere), or with no table at all.
            bad = {k: show(naming(L.doc(k), c)) for k, c in (("tab-RN", "RN"), ("tab-RS", "RS"), ("none-RB", "RB")) if naming(L.doc(k), c)}
            assert not bad, f"rows name markers that have no measured body: {bad}"

        def actor(key):
            c = cov(L.doc(key), ACTOR_LINE)
            assert c is not None, f"no '{ACTOR_LINE}' coverage line: {[x['Population'] for x in L.doc(key)['Coverage']]}"
            return c.get("Unjudgeable") or {}, c.get("SetAside") or {}, c

        def one(key, want, side):
            unj, aside, c = actor(key)
            mine = aside if side == "aside" else unj
            assert mine.get(want) == 1 and sum(unj.values()) + sum(aside.values()) + int(c.get("Judged") or 0) == 1, f"want {want} 1 ({side}) and nothing else: {c}"

        def c1742():
            # Rejects: a clear arrival left unjudged, a judgement that claims an in-game check, or Actor still on the box line.
            unj, aside, c = actor("tab-AC")
            assert int(c.get("Judged") or 0) == 1 and not any(unj.values()) and not any(aside.values()), f"want the clear actor judged: {c}"
            note = str(c.get("Note") or "").lower()
            assert "judged from placement" in note and "not checked in game" in note, f"the note: {note}"
            box = cov(L.doc("tab-AC"), BOX_LINE) or {}
            assert "Actor" not in (box.get("Unjudgeable") or {}), f"the box line still carries Actor: {box}"

        def c1748():
            # Rejects: an exterior arrival read off the navmesh because only its own cell's navmesh was read.
            unj, aside, c = actor("tab-EN")
            assert int(c.get("Judged") or 0) == 1 and not any(unj.values()) and not any(aside.values()), f"want the actor over EB's navmesh judged: {c}"

        def c1746():
            for key in ("tab-AK", "tab-AL", "off-AC"):
                one(key, "ActorUnmeasured", "unj")

        def c1747():
            # Rejects: an actor turned into a row from an unmeasured consequence.
            doc = L.doc("all-tab")
            bad = {c: show(naming(doc, c)) for c in CELLS if c[0] == "A" and naming(doc, c)}
            assert not bad, f"rows name actors: {bad}"

        for name, fn in [("1734 the box line's note names the table, or says none was given", c1734),
                         ("1735 the policy carries the table's identity", c1735),
                         ("1736 conflictdiff refuses with-table against without-table", c1736),
                         ("1737 the marker-box row says what it is and what is not measured", c1737),
                         ("1738 a solid marker box across a corridor reads as the barrier does", c1738),
                         ("1739 guard: no box for markers without a measured body", c1739),
                         ("1741 an actor that leaves is set aside", lambda: one("tab-AD", "ActorDeparts", "aside")),
                         ("1742 a clear arrival is judged from placement, and says so", c1742),
                         ("1743 an arrival off the navmesh is unjudged by name", lambda: one("tab-AO", "ActorOffNavmesh", "unj")),
                         ("1744 a floating arrival is unjudged by name", lambda: (one("tab-AF", "ActorFloating", "unj"), one("tab-AB", "ActorBuried", "unj"))),
                         ("1745 an arrival inside a solid is unjudged by name", lambda: one("tab-AS", "ActorInSolid", "unj")),
                         ("1746 a creature, a leveled actor and the route off are unmeasured", c1746),
                         ("1747 guard: no row names an actor", c1747),
                         ("1748 an exterior arrival over the neighbouring cell's navmesh is judged", c1748),
                         ("1749 guard: an exterior arrival beyond every navmesh is off it", lambda: one("tab-EO", "ActorOffNavmesh", "unj"))]:
            check(name, fn)
        if os.environ.get("HC_SL_DUMP"):
            d = Path(os.environ["HC_SL_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for k, p in L.out.items():
                shutil.copyfile(p, d / p.name)
                rj = L.tmp / f"{k}.rows.json"
                if rj.exists():
                    shutil.copyfile(rj, d / rj.name)
            for k, lg in L.logs.items():
                (d / f"{k}.log").write_text(lg.stdout + "\n--- stderr\n" + lg.stderr, encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
