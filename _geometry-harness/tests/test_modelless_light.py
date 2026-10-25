"""A light whose base record has no model is not a physical object to any geometry probe (cases 1430-1438).

    python _geometry-harness/tests/test_modelless_light.py

A light record with no model has no body in game: nothing is drawn for it and nothing collides with it. The geometry probes
still treated such a light as a dependent: its object bounds stood in as a box, so stateprobe called it sunk inside a wall,
and contactprobe called it left floating (Orphaned) or left in empty space when the wall it stood against was moved. The
decision is the base record's model field, never a name. A light whose model names a file that could not be read is not
bodiless: what it is is unknown, so it keeps its row (on its labelled box), never dropped unsaid.

The fixture is _shared/ModellessLightFixture.cs (its table is the expectation), written by `stateprobe
--write-modelless-light-fixture`. This file lays it out as an MO2 instance and runs the REAL stateprobe (three switch
states) and contactprobe (the default pass, and --empty-space) on it:

    1430  stateprobe: no row names the bare light XB (no model) that arrives inside the slab under MlSwB.
    1431  contactprobe: no CONTACT row names the bare light LB, left beside the wall MlMover moved.
    1432  contactprobe --empty-space: no EMPTY-SPACE row names the bare light LB.
    1433  control: the lantern light XL (a model with a body) still has its Sunk row under MlSwL, 60 u inside the slab.
    1434  control: the lantern light LL still has its CONTACT Orphaned row.
    1435  control: the lantern light LL still has its EMPTY-SPACE row.
    1436  control: the light XM whose model file is missing keeps its Sunk row (unknown, not bodiless).
    1437  control: the light LM whose model file is missing keeps its CONTACT Orphaned row.
    1438  control: the light LM whose model file is missing keeps its EMPTY-SPACE row.
    1438b (Gate A review M2) conflictdiff refuses, both ways round and with a sentence, each probe's emit against the same
          emit as a build before this change records it (its rule's earlier policy version): the light rows that vanish
          with the change must never be credited as repaired, nor reappear as created.

Each test names the wrong answer it rejects. Probes come from this checkout's Release builds unless HC_ML_HARNESS names another
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
PROBES = Path(os.environ.get("HC_ML_HARNESS") or HARNESS)
FAILURES, PASSED = [], []

ORDER = ["MlRoom.esm", "MlLights.esp", "MlMover.esp"]
STATES = ["MlSwB", "MlSwL", "MlSwM"]


def fk(hexid, plugin):
    return f"{hexid}:{plugin}"


# The fixture's references by name (ModellessLightFixture.cs).
R = {"LB": fk("000901", "MlLights.esp"), "LL": fk("000902", "MlLights.esp"), "LM": fk("000903", "MlLights.esp"),
     "XB": fk("000905", "MlLights.esp"), "XL": fk("000907", "MlLights.esp"), "XM": fk("000909", "MlLights.esp"),
     "WB": fk("000901", "MlRoom.esm"), "WL": fk("000902", "MlRoom.esm"), "WM": fk("000903", "MlRoom.esm"),
     "SlabB": fk("000904", "MlRoom.esm"), "SlabL": fk("000905", "MlRoom.esm"), "SlabM": fk("000906", "MlRoom.esm")}


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
    """ModellessLightFixture laid out as an MO2 instance; one run of each probe, shared by the checks."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "stateprobe"), "--write-modelless-light-fixture", keep])
        ml = keep / "ml"
        if st.returncode != 0 or not all((ml / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in ORDER:
            stem = Path(n).stem
            src = ml / "mods" / stem
            if src.exists():
                shutil.copytree(src, self.inst / "mods" / stem)
            else:
                (self.inst / "mods" / stem).mkdir(parents=True)
            shutil.copyfile(ml / n, self.inst / "mods" / stem / n)
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        self.cand = tmp / "candidate-ml.json"
        self.cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "ml", "BaseProfile": "P", "Plugins": ORDER, "Mods": mods,
                                         "Changes": []}), encoding="utf-8")
        common = ["--order", self.cand, "--instance", self.inst, "--game", self.game, "--profile", "P"]
        self.logs = {}

        self.state_emit, self.state_json = tmp / "state-emit.json", tmp / "state-rows.json"
        argv = [exe(PROBES, "stateprobe"), *common, "--route", "off", "--emit", self.state_emit, "--json", self.state_json]
        for s in STATES:
            argv += ["--state", s]
        self.logs["state"] = run(argv)
        self.contact_emit = tmp / "contact-emit.json"
        self.logs["contact"] = run([exe(PROBES, "contactprobe"), *common, "--emit", self.contact_emit])
        self.empty_emit, self.empty_side = tmp / "empty-emit.json", tmp / "empty-side.json"
        self.logs["empty"] = run([exe(PROBES, "contactprobe"), *common, "--empty-space", self.empty_side, "--emit", self.empty_emit])
        for k, (lg, out) in {"state": (self.logs["state"], self.state_emit), "contact": (self.logs["contact"], self.contact_emit),
                             "empty": (self.logs["empty"], self.empty_emit)}.items():
            if lg.returncode != 0 or not out.exists():
                raise RuntimeError(f"{k} run exit {lg.returncode}: {lg.stdout[-1500:]} {lg.stderr[-800:]}")
        self.state = json.loads(self.state_emit.read_text(encoding="utf-8"))
        self.state_rows = json.loads(self.state_json.read_text(encoding="utf-8"))
        self.contact = json.loads(self.contact_emit.read_text(encoding="utf-8"))
        self.empty = json.loads(self.empty_emit.read_text(encoding="utf-8"))


def keys(c):
    return [x["FormKey"].lower() for x in (c.get("Necessary") or []) + (c.get("Alternative") or [])]


def naming(doc, name):
    """The emit's conflicts that name the fixture reference `name` in any role."""
    want = R[name].lower()
    return [c for c in doc["Conflicts"] if want in keys(c)]


def own(doc, name):
    """The emit's conflicts whose own object (first Necessary record) is `name`."""
    want = R[name].lower()
    return [c for c in doc["Conflicts"] if (c.get("Necessary") or [{}])[0].get("FormKey", "").lower() == want]


def show(rows):
    return " | ".join(f"{c['Kind']}: {c['Summary'][:160]}" for c in rows) or "(none)"


def json_rows_naming(rows_doc, name):
    """stateprobe --json rows that name `name` (member or other), whatever the emit withheld."""
    want = R[name].lower()
    out = []

    def walk(x):
        if isinstance(x, dict):
            vals = [str(v).lower() for v in x.values() if isinstance(v, str)]
            if any(want == v or v.startswith(want) for v in vals) and any(k in x for k in ("Why", "why", "Member", "member")):
                out.append(x)
            for v in x.values():
                walk(v)
        elif isinstance(x, list):
            for v in x:
                walk(v)
    walk(rows_doc)
    return out


# The policy version each probe's emit recorded before a light with no model stopped being a dependent (2026-10-24).
PRE_CHANGE_VERSION = {"state": "state-contact-route-v1", "contact": "placement_contact_v1", "empty": "empty_space_v1"}


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))

        def c1430():
            # Rejects: the bare light's object bounds read as a body sunk inside the slab.
            rows = naming(L.state, "XB")
            assert not rows, f"stateprobe wrote {len(rows)} row(s) naming the bare light XB: {show(rows)}"
            jr = json_rows_naming(L.state_rows, "XB")
            assert not jr, f"stateprobe --json holds {len(jr)} row(s) naming the bare light XB: {json.dumps(jr)[:600]}"

        def c1431():
            # Rejects: the bare light read as left floating beside the moved wall.
            rows = [c for c in naming(L.contact, "LB") if c["Kind"] == "CONTACT"]
            assert not rows, f"contactprobe wrote {len(rows)} CONTACT row(s) naming the bare light LB: {show(rows)}"

        def c1432():
            # Rejects: the bare light read as a fixture left in empty space.
            rows = naming(L.empty, "LB")
            assert not rows, f"contactprobe --empty-space wrote {len(rows)} row(s) naming the bare light LB: {show(rows)}"

        def c1433():
            # Rejects: a fix that drops every light, or every arriving dependent, whatever its model.
            rows = own(L.state, "XL")
            assert len(rows) == 1 and "Sunk" in rows[0]["Summary"] and "60" in rows[0]["Summary"], \
                f"want one Sunk row, 60 u, for the lantern light XL; got {show(rows)}"

        def c1434():
            rows = [c for c in own(L.contact, "LL") if c["Kind"] == "CONTACT"]
            assert len(rows) == 1 and rows[0]["Summary"].startswith("Orphaned"), \
                f"want one CONTACT Orphaned row for the lantern light LL; got {show(rows)}"

        def c1435():
            rows = own(L.empty, "LL")
            assert len(rows) == 1 and rows[0]["Kind"] == "EMPTY-SPACE", \
                f"want one EMPTY-SPACE row for the lantern light LL; got {show(rows)}"

        def c1436():
            # Rejects: a model that could not be read treated as no model (dropped silently).
            rows = own(L.state, "XM")
            assert len(rows) == 1 and "Sunk" in rows[0]["Summary"], \
                f"want one Sunk row for the light XM whose model file is missing; got {show(rows)}"

        def c1437():
            rows = [c for c in own(L.contact, "LM") if c["Kind"] == "CONTACT"]
            assert len(rows) == 1 and rows[0]["Summary"].startswith("Orphaned"), \
                f"want one CONTACT Orphaned row for the light LM whose model file is missing; got {show(rows)}"

        def c1438():
            rows = own(L.empty, "LM")
            assert len(rows) == 1 and rows[0]["Kind"] == "EMPTY-SPACE", \
                f"want one EMPTY-SPACE row for the light LM whose model file is missing; got {show(rows)}"

        def c1438b():
            # Rejects: a comparison across the change that proceeds, crediting the vanished light rows as repaired (old -> new)
            # or created (new -> old).
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
                    ok = r.returncode == 3 and "measurement policy" in r.stdout and "REFUSED" in r.stdout
                    out.append((k, a.name, ok, r.returncode))
            assert all(o[2] for o in out), f"conflictdiff did not refuse every pre-change pair: {out}"

        for name, fn in [("1430 stateprobe: no row for a light with no model", c1430),
                         ("1431 contactprobe: no CONTACT row for a light with no model", c1431),
                         ("1432 contactprobe --empty-space: no row for a light with no model", c1432),
                         ("1433 control: a light with a model keeps its stateprobe row", c1433),
                         ("1434 control: a light with a model keeps its CONTACT row", c1434),
                         ("1435 control: a light with a model keeps its EMPTY-SPACE row", c1435),
                         ("1436 control: a light whose model could not be read keeps its stateprobe row", c1436),
                         ("1437 control: a light whose model could not be read keeps its CONTACT row", c1437),
                         ("1438 control: a light whose model could not be read keeps its EMPTY-SPACE row", c1438),
                         ("1438b conflictdiff refuses a pre-change emit against a current one, both ways", c1438b)]:
            check(name, fn)
        if os.environ.get("HC_ML_DUMP"):
            d = Path(os.environ["HC_ML_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in (L.state_emit, L.state_json, L.contact_emit, L.empty_emit):
                shutil.copyfile(f, d / f.name)
            for k, lg in L.logs.items():
                (d / f"{k}.log").write_text(lg.stdout + "\n--- stderr\n" + lg.stderr, encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
