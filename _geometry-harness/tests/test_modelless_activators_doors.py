"""Activators and doors with no model, and editor markers, in the geometry probes (cases 1450-1469; the report's cells are
1450-1453 in test_evalreport_prediction.py).

    python _geometry-harness/tests/test_modelless_activators_doors.py

The fixture is _shared/ModellessActivatorFixture.cs (its table is the layout), written by `stateprobe
--write-modelless-activator-fixture`. This file lays it out as an MO2 instance and runs the REAL stateprobe and contactprobe (the
default pass, and --empty-space) on it.

Editor markers (a base with the Is-Marker record flag) are drawn as nothing and collide with nothing; stateprobe gives them no
box. A changed marker is therefore not something the check failed to judge: it is set aside by name, as a light with no model is.

    1454  a setup that adds three editor markers (MaMoreMarkers.esp, arriving with MaSwG) leaves stateprobe's "changed
          references with a box" exactly as unjudged as the setup without them, the three set aside by name; conflictdiff does
          not refuse the pair for it.
    1455  the two markers arriving with MaSwK are set aside by name, not counted with the references the check could not judge.
    1457  a marker-flagged static with no model whose reference carries a Box primitive (the CollisionMarker shape, which station
          Q measured to block when it states no layer) arriving with MaSwP is NOT set aside: it stays unjudged, under its own
          name (MarkerPrimitive), never in NoBox and never in the markers' set-aside entry (Gate A review, option A).
    1456  control: the static whose model file is missing and that has no bounds (arriving with MaSwG) is still counted as
          unjudged (NoBox): what the check cannot measure is never set aside.

Activators and doors with no model have no body in game (station AD, 2026-10-25: a salt pile and the player went through every
activator primitive layer that occurs on them, a sphere, an activator with no primitive and both modelless door bases, while a
CollisionMarker box held both). So no probe measures them, and stateprobe sets them aside by name:

    1458  stateprobe: no row names the activators with no model (absent layer, L_NONCOLLIDABLE, no primitive) arriving inside
          their slabs.
    1459  contactprobe: no CONTACT row names them beside their moved walls.
    1460  contactprobe --empty-space: no row names them.
    1461  control: the activator with a model (lever) keeps its row in each probe.
    1462  stateprobe: "changed references with a box" sets the three activators and the door aside by name
          (ActivatorWithNoModel 3, DoorWithNoModel 1), never as NoBox.
    1463  contactprobe: the door with no model is not counted as a dependent the pass could not judge (trace: no Unjudgeable
          NoGeometry event; its rule names it as having no model).
    1467  (Gate B review B1) a crate against the moved wall whose only link to the wall that stays is a modelless activator's
          bounds is left in empty space: one EMPTY-SPACE row names it, its assembly of 1. Today the activator's bounds join its
          assembly to the second wall and no row is written; in game the activator is nothing (station AD).
    1468  contactprobe's console counts the references it leaves out as having no body, by type: the fixture's 7 activator
          references and 2 door references with no model are named as dependents not judged (no coverage key carries them).
    1464  conflictdiff refuses, both ways round and with a sentence, each probe's emit against the same emit as a build before
          this change records it (its rule's earlier policy version).

HC_MA_DUMP=DIR keeps every emit and log.

Each test names the wrong answer it rejects. Probes come from this checkout's Release builds unless HC_MA_HARNESS names another
harness folder.
"""

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
PROBES = Path(os.environ.get("HC_MA_HARNESS") or HARNESS)
FAILURES, PASSED = [], []

ORDER = ["MaRoom.esm", "MaObjects.esp", "MaMover.esp"]
OBJECTS = ["A", "N", "B", "M", "D"]          # act-box, act-nc, act-bare, act-model, door-bare (ModellessActivatorFixture)
STATES = [f"MaSw{o}" for o in OBJECTS] + ["MaSwK", "MaSwG", "MaSwP"]
BOX_LINE = "changed references with a box"


def fk(hexid, plugin):
    return f"{hexid}:{plugin}"


# The fixture's references by name.
R = {**{f"C{o}": fk(f"{0x901 + i:06X}", "MaObjects.esp") for i, o in enumerate(OBJECTS)},
     **{f"S{o}": fk(f"{0x921 + i:06X}", "MaObjects.esp") for i, o in enumerate(OBJECTS)},
     "XMarker": fk("000926", "MaObjects.esp"), "BareMarker": fk("000927", "MaObjects.esp"),
     "NoGeom": fk("000928", "MaObjects.esp"), "Crate": fk("00092A", "MaObjects.esp"), "UnderAct": fk("00092B", "MaObjects.esp")}


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


class Lab:
    """ModellessActivatorFixture laid out as an MO2 instance; one run of each probe, shared by the checks."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "stateprobe"), "--write-modelless-activator-fixture", keep])
        ma = keep / "ma"
        if st.returncode != 0 or not all((ma / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in ORDER:
            stem = Path(n).stem
            src = ma / "mods" / stem
            if src.exists():
                shutil.copytree(src, self.inst / "mods" / stem)
            else:
                (self.inst / "mods" / stem).mkdir(parents=True)
            shutil.copyfile(ma / n, self.inst / "mods" / stem / n)
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods + ["MaMoreMarkers"]), encoding="utf-8")
        (self.inst / "mods" / "MaMoreMarkers").mkdir(parents=True)
        shutil.copyfile(ma / "MaMoreMarkers.esp", self.inst / "mods" / "MaMoreMarkers" / "MaMoreMarkers.esp")
        self.cand = tmp / "candidate-ma.json"
        self.cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "ma", "BaseProfile": "P", "Plugins": ORDER, "Mods": mods,
                                         "Changes": []}), encoding="utf-8")
        self.cand_more = tmp / "candidate-ma-more.json"
        self.cand_more.write_text(json.dumps({"SchemaVersion": 1, "Name": "ma-more", "BaseProfile": "P",
                                              "Plugins": ORDER + ["MaMoreMarkers.esp"], "Mods": ["MaMoreMarkers"] + mods,
                                              "Changes": ["added MaMoreMarkers.esp"]}), encoding="utf-8")
        common = ["--order", self.cand, "--instance", self.inst, "--game", self.game, "--profile", "P"]
        tail = ["--instance", self.inst, "--game", self.game, "--profile", "P", "--route", "off"]
        self.logs = {}
        self.sm_base, self.sm_more, self.sk, self.sp = tmp / "sm-base.json", tmp / "sm-more.json", tmp / "sk.json", tmp / "sp.json"
        self.logs["sp"] = run([exe(PROBES, "stateprobe"), "--order", self.cand, "--emit", self.sp, *tail, "--state", "MaSwP"])
        self.logs["sm-base"] = run([exe(PROBES, "stateprobe"), "--order", self.cand, "--emit", self.sm_base, *tail, "--state", "MaSwG"])
        self.logs["sm-more"] = run([exe(PROBES, "stateprobe"), "--order", self.cand_more, "--emit", self.sm_more, *tail, "--state", "MaSwG"])
        self.logs["sk"] = run([exe(PROBES, "stateprobe"), "--order", self.cand, "--emit", self.sk, *tail, "--state", "MaSwK"])
        self.state_emit, self.state_json = tmp / "state-emit.json", tmp / "state-rows.json"
        argv = [exe(PROBES, "stateprobe"), *common, "--route", "off", "--emit", self.state_emit, "--json", self.state_json]
        for s in STATES:
            argv += ["--state", s]
        self.logs["state"] = run(argv)
        self.contact_emit, self.trace, keys = tmp / "contact-emit.json", tmp / "contact-trace.json", tmp / "trace-keys.txt"
        keys.write_text(R["CD"] + "\n" + R["SD"] + "\n", encoding="utf-8")
        self.logs["contact"] = run([exe(PROBES, "contactprobe"), *common, "--emit", self.contact_emit, "--trace", self.trace,
                                    "--trace-keys", keys])
        self.empty_emit, self.empty_side = tmp / "empty-emit.json", tmp / "empty-side.json"
        self.logs["empty"] = run([exe(PROBES, "contactprobe"), *common, "--empty-space", self.empty_side, "--emit", self.empty_emit])
        for k, out in {"state": self.state_emit, "contact": self.contact_emit, "empty": self.empty_emit, "sm-base": self.sm_base,
                       "sm-more": self.sm_more, "sk": self.sk, "sp": self.sp}.items():
            lg = self.logs[k]
            if lg.returncode != 0 or not out.exists():
                raise RuntimeError(f"{k} run exit {lg.returncode}: {lg.stdout[-1500:]} {lg.stderr[-800:]}")
        self.state = json.loads(self.state_emit.read_text(encoding="utf-8"))
        self.contact = json.loads(self.contact_emit.read_text(encoding="utf-8"))
        self.empty = json.loads(self.empty_emit.read_text(encoding="utf-8"))


def keys(c):
    return [x["FormKey"].lower() for x in (c.get("Necessary") or []) + (c.get("Alternative") or [])]


def naming(doc, name):
    want = R[name].lower()
    return [c for c in doc["Conflicts"] if want in keys(c)]


def show(rows):
    return " | ".join(f"{c['Kind']}: {c['Summary'][:160]}" for c in rows) or "(none)"


def cov(path, line=BOX_LINE):
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    return next(c for c in doc["Coverage"] if c["Population"] == line)


# The policy version each probe's emit recorded before activators and doors with no model stopped being dependents (2026-10-25).
PRE_CHANGE_VERSION = {"state": "state-contact-route-v2", "contact": "placement_contact_v2", "empty": "empty_space_v2"}


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))

        def c1454():
            # Rejects: editor markers counted as references the check could not judge (NoBox), so a setup that adds them reads
            # as less judged than one without, and a comparison is refused for a correct decision.
            a, b = cov(L.sm_base), cov(L.sm_more)
            ua, ub = sum((a.get("Unjudgeable") or {}).values()), sum((b.get("Unjudgeable") or {}).values())
            assert ua == ub, f"unjudged moved: without the markers {a}; with them {b}"
            sa, sb = a.get("SetAside") or {}, b.get("SetAside") or {}
            assert any(sb.get(k, 0) - sa.get(k, 0) == 3 for k in sb), f"the three added markers are not set aside by name: {a} -> {b}"
            r = run([exe(PROBES, "conflictdiff"), L.sm_base, L.sm_more])
            assert BOX_LINE not in "".join(x for x in r.stdout.splitlines(True) if "rose by more than" in x), \
                f"conflictdiff refused the pair on that line: {r.stdout[-800:]}"

        def c1455():
            # Rejects: the two markers arriving with MaSwK counted as unjudged instead of set aside by name.
            c = cov(L.sk)
            nobox = (c.get("Unjudgeable") or {}).get("NoBox", 0)
            aside = c.get("SetAside") or {}
            marker_keys = [k for k in aside if "marker" in k.lower()]
            assert marker_keys and sum(aside[k] for k in marker_keys) == 2, f"the two markers are not set aside by name: {c}"
            # the switch itself (a static with no model and no bounds) is the one reference left without a box
            assert nobox == 1, f"want only the switch counted as unjudged, the markers set aside: {c}"

        def c1457():
            # Rejects: a marker whose reference carries a primitive (a box that may block) set aside as bodiless with the
            # other markers, or left in the unnamed NoBox entry.
            c = cov(L.sp)
            unj, aside = c.get("Unjudgeable") or {}, c.get("SetAside") or {}
            assert unj.get("MarkerPrimitive") == 1, f"the marker with a primitive is not unjudged under MarkerPrimitive: {c}"
            assert unj.get("NoBox", 0) == 1 and not aside.get("EditorMarker"), f"want NoBox only the switch, nothing set aside as a marker: {c}"

        def c1458():
            # Rejects: an activator with no model (a phantom in game, whatever its layer) read as sunk inside the slab.
            bad = {n: show(naming(L.state, n)) for n in ("SA", "SN", "SB") if naming(L.state, n)}
            assert not bad, f"stateprobe rows name activators with no model: {bad}"

        def c1459():
            bad = {n: show(r) for n in ("CA", "CN", "CB") if (r := [c for c in naming(L.contact, n) if c["Kind"] == "CONTACT"])}
            assert not bad, f"contactprobe CONTACT rows name activators with no model: {bad}"

        def c1460():
            bad = {n: show(naming(L.empty, n)) for n in ("CA", "CN", "CB") if naming(L.empty, n)}
            assert not bad, f"--empty-space rows name activators with no model: {bad}"

        def c1461():
            # Rejects: a fix that drops every activator, whatever its model.
            got = {"state": naming(L.state, "SM"), "contact": [c for c in naming(L.contact, "CM") if c["Kind"] == "CONTACT"],
                   "empty": naming(L.empty, "CM")}
            assert all(len(v) == 1 for v in got.values()), f"want one row each for the lever: { {k: show(v) for k, v in got.items()} }"

        def c1462():
            # Rejects: bodiless activators and doors counted as references the state check could not judge.
            c = cov(L.state_emit)
            aside = c.get("SetAside") or {}
            assert aside.get("ActivatorWithNoModel") == 3 and aside.get("DoorWithNoModel") == 1, \
                f"want the three activators and the door set aside by name: {c}"

        def c1463():
            # Rejects: the door with no model counted as a dependent with no geometry (unjudged) instead of having no body.
            doc = json.loads(L.trace.read_text(encoding="utf-8"))
            ev = [(o["formKey"], r["role"], r["outcome"], r["rule"]) for o in doc.get("objects") or [] for r in o["roles"]]
            assert ev and not [e for e in ev if e[2].lower() == "unjudgeable"], f"a door is unjudged: {[e for e in ev if e[2].lower() == 'unjudgeable']}"
            assert {e[0].lower() for e in ev if "NoModel" in e[3]} == {R["CD"].lower(), R["SD"].lower()}, f"trace events {ev}"

        def c1464():
            # Rejects: a comparison across the change that proceeds, crediting vanished activator rows as repaired or created.
            out = []
            for k, emit in (("state", L.state_emit), ("contact", L.contact_emit), ("empty", L.empty_emit)):
                doc = json.loads(emit.read_text(encoding="utf-8"))
                cur = str(doc.get("MeasurementPolicy") or "")
                version = cur.split("|", 1)[0]
                old = dict(doc, MeasurementPolicy=PRE_CHANGE_VERSION[k] + cur[len(version):])
                old_path = L.tmp / f"{k}-pre-change.json"
                old_path.write_text(json.dumps(old), encoding="utf-8")
                for a, b in ((old_path, emit), (emit, old_path)):
                    r = run([exe(PROBES, "conflictdiff"), a, b])
                    out.append((k, a.name, r.returncode == 3 and "measurement policy" in r.stdout and "REFUSED" in r.stdout, r.returncode))
            assert all(o[2] for o in out), f"conflictdiff did not refuse every pre-change pair: {out}"

        def c1467():
            # Rejects: a thing with no body counted as joining a fixture to a wall, so the crate the moved wall left reads as held.
            rows = naming(L.empty, "Crate")
            assert len(rows) == 1 and "assembly of 1 " in rows[0]["Summary"], \
                f"want one EMPTY-SPACE row for the crate, its assembly of 1: {show(rows)}"
            assert not naming(L.empty, "UnderAct"), f"a row names the modelless activator: {show(naming(L.empty, 'UnderAct'))}"

        def c1468():
            # Rejects: activators and doors leaving the dependent coverage's NoGeometry without a trace in the run's output.
            out = L.logs["contact"].stdout
            line = next((x for x in out.splitlines() if "no body, not judged: dependents" in x), "")
            assert "0 lights, 7 activators, 2 doors with no model" in line, f"console line: {line or '(none)'}"

        def c1456():
            # Rejects: a fix that sets aside every reference without a box, so what the check cannot measure disappears.
            c = cov(L.sm_base)
            assert (c.get("Unjudgeable") or {}).get("NoBox", 0) >= 1, f"the static with a missing model is no longer unjudged: {c}"

        for name, fn in [("1454 adding editor markers does not move what the state check could not judge", c1454),
                         ("1455 editor markers that arrive are set aside by name", c1455),
                         ("1457 a marker with a primitive stays unjudged under its own name", c1457),
                         ("1458 stateprobe: no row for an activator with no model", c1458),
                         ("1459 contactprobe: no CONTACT row for an activator with no model", c1459),
                         ("1460 contactprobe --empty-space: no row for an activator with no model", c1460),
                         ("1461 control: an activator with a model keeps its row in each probe", c1461),
                         ("1462 stateprobe sets activators and doors with no model aside by name", c1462),
                         ("1463 contactprobe: a door with no model is not an unjudged dependent", c1463),
                         ("1467 a crate whose only other support has no body is unsupported", c1467),
                         ("1468 contactprobe's console counts bodiless dependents by type", c1468),
                         ("1464 conflictdiff refuses a pre-change emit against a current one, both ways", c1464),
                         ("1456 control: a reference with no geometry that is not a marker stays unjudged", c1456)]:
            check(name, fn)
        if os.environ.get("HC_MA_DUMP"):
            d = Path(os.environ["HC_MA_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in (L.state_emit, L.state_json, L.contact_emit, L.empty_emit, L.sm_base, L.sm_more, L.sk, L.sp):
                shutil.copyfile(f, d / f.name)
            for k, lg in L.logs.items():
                (d / f"{k}.log").write_text(lg.stdout + "\n--- stderr\n" + lg.stderr, encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
