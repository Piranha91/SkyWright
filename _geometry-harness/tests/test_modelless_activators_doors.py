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
    1456  control: the static whose model file is missing and that has no bounds (arriving with MaSwG) is still counted as
          unjudged (NoBox): what the check cannot measure is never set aside.

Activators and doors with no model (cases 1457-1469) are decided from in-game behaviour at Gate B; until then this file only
records what the probes write for them (HC_MA_DUMP=DIR keeps every emit and log).

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
STATES = [f"MaSw{o}" for o in OBJECTS] + ["MaSwK", "MaSwG"]
BOX_LINE = "changed references with a box"


def fk(hexid, plugin):
    return f"{hexid}:{plugin}"


# The fixture's references by name.
R = {**{f"C{o}": fk(f"{0x901 + i:06X}", "MaObjects.esp") for i, o in enumerate(OBJECTS)},
     **{f"S{o}": fk(f"{0x921 + i:06X}", "MaObjects.esp") for i, o in enumerate(OBJECTS)},
     "XMarker": fk("000926", "MaObjects.esp"), "BareMarker": fk("000927", "MaObjects.esp"),
     "NoGeom": fk("000928", "MaObjects.esp")}


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
        self.sm_base, self.sm_more, self.sk = tmp / "sm-base.json", tmp / "sm-more.json", tmp / "sk.json"
        self.logs["sm-base"] = run([exe(PROBES, "stateprobe"), "--order", self.cand, "--emit", self.sm_base, *tail, "--state", "MaSwG"])
        self.logs["sm-more"] = run([exe(PROBES, "stateprobe"), "--order", self.cand_more, "--emit", self.sm_more, *tail, "--state", "MaSwG"])
        self.logs["sk"] = run([exe(PROBES, "stateprobe"), "--order", self.cand, "--emit", self.sk, *tail, "--state", "MaSwK"])
        self.state_emit, self.state_json = tmp / "state-emit.json", tmp / "state-rows.json"
        argv = [exe(PROBES, "stateprobe"), *common, "--route", "off", "--emit", self.state_emit, "--json", self.state_json]
        for s in STATES:
            argv += ["--state", s]
        self.logs["state"] = run(argv)
        self.contact_emit = tmp / "contact-emit.json"
        self.logs["contact"] = run([exe(PROBES, "contactprobe"), *common, "--emit", self.contact_emit])
        self.empty_emit, self.empty_side = tmp / "empty-emit.json", tmp / "empty-side.json"
        self.logs["empty"] = run([exe(PROBES, "contactprobe"), *common, "--empty-space", self.empty_side, "--emit", self.empty_emit])
        for k, out in {"state": self.state_emit, "contact": self.contact_emit, "empty": self.empty_emit, "sm-base": self.sm_base,
                       "sm-more": self.sm_more, "sk": self.sk}.items():
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

        def c1456():
            # Rejects: a fix that sets aside every reference without a box, so what the check cannot measure disappears.
            c = cov(L.sm_base)
            assert (c.get("Unjudgeable") or {}).get("NoBox", 0) >= 1, f"the static with a missing model is no longer unjudged: {c}"

        for name, fn in [("1454 adding editor markers does not move what the state check could not judge", c1454),
                         ("1455 editor markers that arrive are set aside by name", c1455),
                         ("1456 control: a reference with no geometry that is not a marker stays unjudged", c1456)]:
            check(name, fn)
        if os.environ.get("HC_MA_DUMP"):
            d = Path(os.environ["HC_MA_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in (L.state_emit, L.state_json, L.contact_emit, L.empty_emit, L.sm_base, L.sm_more, L.sk):
                shutil.copyfile(f, d / f.name)
            for k, lg in L.logs.items():
                (d / f"{k}.log").write_text(lg.stdout + "\n--- stderr\n" + lg.stderr, encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
