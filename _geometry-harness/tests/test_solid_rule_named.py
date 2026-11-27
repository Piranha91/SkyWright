"""motionprobe sweep and lightprobe outputs name the solid rule their results depend on (cases 1960-1967; 1968 retired with lightclash.py).

    python _geometry-harness/tests/test_solid_rule_named.py

Both probes validate solids through MeshIntersection.PrepareVolumeSolid, so its rule (MeshIntersection.PolicyVersion) reaches
their results, but their own policy strings do not change with it. Each output therefore names it:
  motionprobe sweep   solidPolicy: the rule when the region built (Build validates the first sample, which decides Solid; Volume
                      validates every sample of a solid region), else "none" (Build refused, the asset is missing, an error first).
  lightprobe          identity.solidPolicy and identity.anchorPolicy (MeshAnchor.PolicyVersion, the point reading): named when the
                      run wrote at least one pointInSolid row (identity.pointInSolidRows counts them, refused ones included),
                      else "none" (no --geometry, --list-interiors, or no light inside any placed box).
A field that is absent is an output written before this change; "none" is a run that applied no solid rule.

The expected strings are read from src/SkyWright.Engine/MeshIntersection.cs and MeshAnchor.cs, never from a probe, so a probe built from
another core, or one that writes a fixed string, fails here.

Fixtures: `proofprobe --write-motion-fixture` (MotionProof.SolidRuleFixture.cs: a sliding closed box, the same slide with an open
square, a rig with no animation, and two closed lit cubes) and `stateprobe --write-modelless-light-fixture` (ModellessLightFixture.cs),
laid out as an MO2 instance as tests/test_modelless_light.py does. That fixture's models are collision boxes, which lightprobe's render
reader reads no triangles from; a later mod folder ("SrCubes") replaces ml\\lantern.nif and ml\\wall.nif with the lit cubes for the
case that needs point readings.

    1960  sweep of the closed sliding box: built, solidPolicy is the source's rule (rejects absent and stale), policy unchanged.
    1961  sweep of the open square: built with Solid false, solidPolicy is still the rule (Solid false is the rule's answer).
    1962  sweep of the rig with no animation: refused, solidPolicy "none" (rejects naming a rule that was not applied).
    1963  sweep of an absent mesh: refused missing_asset, solidPolicy "none"; inspect of it carries no solidPolicy (other verbs keep
          their shape).
    1964  lightprobe --geometry with the lit cubes: pointInSolidRows >= 1 and equal to the rows in the file; solidPolicy and
          anchorPolicy are the source's.
    1965  lightprobe --geometry on the collision-only fixture: no rows; both "none".
    1966  lightprobe without --geometry: no rows; both "none".
    1967  lightprobe --list-interiors: no rows; both "none"; identity.policy still LightReach's.

Probes under test come from this checkout's Release builds unless HC_SR_HARNESS names another build folder (a base build, for the
red run); the fixture writers (proofprobe, stateprobe) come from HC_SR_FIXTURES, else this checkout.
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
CORE = HARNESS.parent / "src" / "SkyWright.Engine"   # the engine the probes are built from
PROBES = Path(os.environ.get("HC_SR_HARNESS") or buildlayout.root())
FIXTURES = Path(os.environ.get("HC_SR_FIXTURES") or buildlayout.root())
FAILURES, PASSED = [], []

ORDER = ["MlRoom.esm", "MlLights.esp"]
CELL = "MlRoomCL"   # the wall WL at the origin (half 70 u) and the lantern light LL at (80, 0, 0), enabled at load


def const(path, name="PolicyVersion"):
    m = re.findall(r'const\s+string\s+' + name + r'\s*=\s*"([^"]+)"', path.read_text(encoding="utf-8"))
    if len(m) != 1:
        raise RuntimeError(f"{path.name}: expected one {name} constant, found {len(m)}")
    return m[0]


RULE = const(CORE / "MeshIntersection.cs")
ANCHOR = const(CORE / "MeshAnchor.cs")
SWEPT = const(CORE / "SweptGeometry.cs")


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
                          timeout=900, **kw)


def named(doc, key, want, where):
    """The rejected answers: the key absent (an output from before this change), or naming another rule (stale)."""
    assert key in doc, f"{where}: no {key} field (keys: {sorted(doc)})"
    assert doc[key] == want, f"{where}: {key} is {doc[key]!r}, expected {want!r}"


class Lab:
    def __init__(self, tmp: Path):
        self.tmp = tmp
        self.logs = {}
        # ---- motionprobe: the loose root ----
        self.loose = tmp / "loose"
        w = run([exe(FIXTURES, "proofprobe"), "--write-motion-fixture", self.loose])
        if w.returncode != 0 or not (self.loose / "meshes" / "srfix" / "mover.nif").exists():
            raise RuntimeError(f"the motion fixture was not written (exit {w.returncode}): {w.stdout[-400:]} {w.stderr[-400:]}")
        self.sweep = {}
        for n in ("mover", "open", "still", "absent"):
            out = tmp / f"sweep-{n}.json"
            self.logs[f"sweep-{n}"] = run([exe(PROBES, "motionprobe"), "--loose-root", self.loose, "sweep", f"meshes/srfix/{n}.nif", "--out", out])
            self.sweep[n] = json.loads(out.read_text(encoding="utf-8")) if out.exists() else None
        out = tmp / "inspect-absent.json"
        self.logs["inspect-absent"] = run([exe(PROBES, "motionprobe"), "--loose-root", self.loose, "inspect", "meshes/srfix/absent.nif", "--out", out])
        self.inspect_absent = json.loads(out.read_text(encoding="utf-8")) if out.exists() else None

        # ---- lightprobe: the modelless-light fixture as an MO2 instance ----
        keep = tmp / "fixture"
        st = run([exe(FIXTURES, "stateprobe"), "--write-modelless-light-fixture", keep])
        ml = keep / "ml"
        if st.returncode != 0 or not all((ml / n).exists() for n in ORDER):
            raise RuntimeError(f"the light fixture was not written (exit {st.returncode}): {st.stdout[-400:]} {st.stderr[-400:]}")
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
        cubes = self.inst / "mods" / "SrCubes" / "meshes" / "ml"
        cubes.mkdir(parents=True)
        shutil.copyfile(self.loose / "meshes" / "srfix" / "lantern.nif", cubes / "lantern.nif")
        shutil.copyfile(self.loose / "meshes" / "srfix" / "wall.nif", cubes / "wall.nif")
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in ["SrCubes"] + mods), encoding="utf-8")

        def cand(name, mod_list):
            p = tmp / f"candidate-{name}.json"
            p.write_text(json.dumps({"SchemaVersion": 1, "Name": name, "BaseProfile": "P", "Plugins": ORDER, "Mods": mod_list,
                                     "Changes": []}), encoding="utf-8"); return p
        self.cand_cubes, self.cand_plain = cand("cubes", ["SrCubes"] + mods), cand("plain", mods)
        pin = ["--instance", self.inst, "--game", self.game, "--profile", "P"]
        self.light = {}
        for k, c, extra in (("cubes", self.cand_cubes, ["--cell", CELL, "--geometry"]),
                            ("collision", self.cand_plain, ["--cell", CELL, "--geometry"]),
                            ("nogeometry", self.cand_cubes, ["--cell", CELL]),
                            ("interiors", self.cand_cubes, ["--list-interiors"])):
            out = tmp / f"light-{k}.json"
            self.logs[f"light-{k}"] = run([exe(PROBES, "lightprobe"), *pin, "--order", c, *extra, "--json", out])
            self.light[k] = json.loads(out.read_text(encoding="utf-8")) if out.exists() else None


def rows_in(doc):
    return sum(len(l.get("pointInSolid") or []) for c in doc.get("cells") or [] for l in c.get("lights") or [])


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))

        def doc(d, what):
            assert d is not None, f"{what}: the probe wrote no JSON ({L.logs.get(what).returncode if what in L.logs else '?'}: " \
                                  f"{(L.logs[what].stdout + L.logs[what].stderr)[-500:] if what in L.logs else ''})"
            return d

        def c1960():
            d = doc(L.sweep["mover"], "sweep-mover")
            assert d.get("Status") == "built", f"the fixture did not build: {d.get('Status')} {d.get('Reason')}"
            assert d.get("Solid") is True, f"the closed box's region is not a solid (Solid={d.get('Solid')}), so this case would not cover the solid path"
            named(d, "solidPolicy", RULE, "sweep of a built closed region")
            assert d.get("policy") == SWEPT, f"policy changed: {d.get('policy')!r}"

        def c1961():
            d = doc(L.sweep["open"], "sweep-open")
            assert d.get("Status") == "built" and d.get("Solid") is False, f"not a built open region: {d.get('Status')} Solid={d.get('Solid')}"
            named(d, "solidPolicy", RULE, "sweep of a built region that is not a solid")

        def c1962():
            d = doc(L.sweep["still"], "sweep-still")
            assert d.get("Status") == "refused", f"the rig with no animation was not refused: {d.get('Status')}"
            named(d, "solidPolicy", "none", "a refused sweep")

        def c1963():
            d = doc(L.sweep["absent"], "sweep-absent")
            assert d.get("status") == "refused" and str(d.get("reason", "")).startswith("missing_asset"), f"not a missing asset: {d}"
            named(d, "solidPolicy", "none", "a sweep of a missing asset")
            i = doc(L.inspect_absent, "inspect-absent")
            assert "solidPolicy" not in i, f"inspect gained solidPolicy: {i}"

        def light(k):
            d = doc(L.light[k], f"light-{k}")
            assert "identity" in d, f"light-{k}: no identity"
            return d, d["identity"]

        def c1964():
            d, ident = light("cubes")
            n = rows_in(d)
            assert n >= 1, "the lit-cube run made no point readings, so it cannot show the applied case"
            named(ident, "pointInSolidRows", n, "a --geometry run with point readings")
            named(ident, "solidPolicy", RULE, "a --geometry run with point readings")
            named(ident, "anchorPolicy", ANCHOR, "a --geometry run with point readings")

        def none_case(k, what):
            d, ident = light(k)
            assert rows_in(d) == 0, f"{what}: {rows_in(d)} pointInSolid rows, expected none"
            named(ident, "pointInSolidRows", 0, what)
            named(ident, "solidPolicy", "none", what)
            named(ident, "anchorPolicy", "none", what)
            return ident

        def c1965():
            none_case("collision", "a --geometry run in which no light lies inside a read model")

        def c1966():
            none_case("nogeometry", "a run without --geometry")

        def c1967():
            ident = none_case("interiors", "--list-interiors")
            assert "emitters v1" in str(ident.get("policy")), f"identity.policy changed: {ident.get('policy')!r}"

        for name, fn in [("1960 motionprobe sweep, built solid region: solidPolicy names the rule", c1960),
                         ("1961 motionprobe sweep, built region that is not a solid: solidPolicy names the rule", c1961),
                         ("1962 motionprobe sweep refused: solidPolicy none", c1962),
                         ("1963 motionprobe sweep of a missing asset: solidPolicy none; inspect unchanged", c1963),
                         ("1964 lightprobe --geometry with point readings: rule and anchor named, rows counted", c1964),
                         ("1965 lightprobe --geometry without point readings: none", c1965),
                         ("1966 lightprobe without --geometry: none", c1966),
                         ("1967 lightprobe --list-interiors: none; policy unchanged", c1967)]:
            check(name, fn)
        if os.environ.get("HC_SR_DUMP"):
            d = Path(os.environ["HC_SR_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in Path(td).glob("*.json"):
                shutil.copyfile(f, d / f.name)
            for k, lg in L.logs.items():
                (d / f"{k}.log").write_text(lg.stdout + "\n--- stderr\n" + lg.stderr, encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
