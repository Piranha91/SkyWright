"""Known answers for a selection that has no plugin: runcontrol evaluates it in place, with the real probes.

    python _geometry-harness/tests/test_asset_only_selection.py

The world is PopulationFixture (_shared/PopulationFixture.cs; its table is the expectation), written by
`overlapprobe --self-test` with PD_FIXTURE_KEEP and laid out as an MO2 instance the way test_population_scope.py lays
it out. Its mod folder PpMeshMod ships one mesh and no plugin: a bigger pp/mshb.nif, with which m8 (PpA.esp) crosses
b8 (PpB.esp), the table's row O8, "with only". A second folder, PpTexMod, is written here: one texture, nothing else.
Each run selects one of the two folders in place, ticks Object Overlap, and stops after the evaluate stage. Checked:
  - the load-order check completes on both arms. It used to be blocked for want of a target plugin, and every other
    check waits on it, so nothing was measured;
  - with PpMeshMod selected, object overlap has only M3 without the mod (a crossing that exists with either mesh) and
    O8, M1, M2 and M3 with it, and the difference is a finding;
  - the rows PpMeshMod's mesh alone creates name the folder: O8 (two plugins), M1 (two objects of one plugin) and M2 (two
    base-game objects); M3 keeps its two plugins (mesh-responsibility/GATE-B.md);
  - with PpTexMod selected, object overlap completes with no row on either arm and no finding;
  - the scope line says what the folder holds and what is not measured.
Each test names the wrong answer it rejects. The controller and the probes come from this checkout's Release builds
unless HC_ASSET_HARNESS names another harness folder (the red run points it at a build from before the change).
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
BUILDS = Path(os.environ.get("HC_ASSET_HARNESS") or HARNESS)
FAILURES, PASSED = [], []

ORDER = ["Skyrim.esm", "PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]
M8, B8 = "000908:PpA.esp", "000907:PpB.esp"   # PopulationFixture.M8 and B8
S1, S2 = "00090B:PpA.esp", "00090C:PpA.esp"   # M1, same plugin
V1, V2 = "000901:Skyrim.esm", "000902:Skyrim.esm"   # M2, base game
C1, C2B = "00090D:PpA.esp", "00090A:PpB.esp"   # M3, crosses with either mesh
Q1, Q2 = "000911:PpA.esp", "000912:PpA.esp"     # M11, contained only inside PpMeshMod's closed shell
MESH_LINE = ("PpMeshMod has no plugin, so it places nothing; it holds 9 mesh files. The checks that read meshes compare every "
             "placed object that uses one of its meshes, with and without it. Its textures (0 files) and other files are not measured.")
TEX_LINE = ("PpTexMod has no plugin and no mesh (1 texture file), so no check here can measure it: textures, sounds and "
            "scripts change nothing these checks read.")


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


class World:
    """The fixture as an MO2 instance with both asset-only folders enabled, and one in-place run per selected folder."""

    def __init__(self, tmp: Path):
        keep = tmp / "fixture"
        env = dict(os.environ, PD_FIXTURE_KEEP=str(keep), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"))
        st = subprocess.run([str(exe(HARNESS, "overlapprobe")), "--self-test"], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=600, env=env)
        pp = keep / "pp"
        if not all((pp / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (overlapprobe --self-test exit {st.returncode}): {st.stdout[-800:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in ORDER:
            stem = Path(n).stem
            shutil.copytree(pp / "mods" / stem, self.inst / "mods" / stem)
            shutil.copyfile(pp / n, self.inst / "mods" / stem / n)
        shutil.copytree(pp / "mods" / "PpMeshMod", self.inst / "mods" / "PpMeshMod")
        shutil.copytree(pp / "mods" / "PpStrongMod", self.inst / "mods" / "PpStrongMod")
        tex = self.inst / "mods" / "PpTexMod" / "textures" / "pp"
        tex.mkdir(parents=True)
        (tex / "box.dds").write_bytes(b"\0")
        mods = ["PpTexMod", "PpStrongMod", "PpMeshMod", "PpDelta", "PpMover", "PpB", "PpA", "PpBase", "Skyrim"]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        # never this machine's harness config: the evaluate stage would read its download pool
        (tmp / "harness.json").write_text("{}", encoding="utf-8")
        self.env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"), HOUSECARL_HARNESS_CONFIG=str(tmp / "harness.json"))
        self.tmp = tmp
        self.runs = {mod: self.run(mod) for mod in ("PpMeshMod", "PpTexMod")}

    def run(self, mod):
        out = self.tmp / ("run-" + mod)
        entry = self.tmp / f"entry-{mod}.json"
        entry.write_text(json.dumps({
            "schema": 1, "id": "asset-only-" + mod,
            "inPlace": {"instance": self.inst.as_posix(), "profile": "P", "game": self.game.as_posix()},
            "selection": {"mods": [mod]}, "evaluation": {"select": ["geometry.object-overlap"], "depth": "standard"}}), encoding="utf-8")
        r = subprocess.run([str(exe(BUILDS, "runcontrol")), "run", "--entry", str(entry), "--out", str(out), "--through", "evaluate"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900, env=self.env)
        return {"exit": r.returncode, "stdout": r.stdout, "stderr": r.stderr, "out": out}

    def ledger(self, mod):
        r = self.runs[mod]
        path = r["out"] / "evaluation" / "ledger.json"
        assert path.exists(), f"no ledger: exit {r['exit']}\n{r['stdout'][-1500:]}\n{r['stderr'][-800:]}"
        return json.loads(path.read_text(encoding="utf-8"))["entries"]

    def doc(self, mod, arm):
        path = self.runs[mod]["out"] / "evaluation" / "sweep" / "inv-0001" / "overlapprobe" / (arm + ".json")
        assert path.exists(), f"object overlap wrote no result for {arm}: {self.runs[mod]['stdout'][-1500:]}"
        return json.loads(path.read_text(encoding="utf-8"))

    def rows(self, mod, arm):
        return sorted(sorted(k["FormKey"] for k in c["Necessary"] + c["Alternative"]) for c in self.doc(mod, arm)["Conflicts"])

    def row(self, mod, arm, pair):
        got = [c for c in self.doc(mod, arm)["Conflicts"] if sorted(k["FormKey"] for k in c["Necessary"] + c["Alternative"]) == sorted(pair)]
        assert len(got) == 1, f"{arm}: {len(got)} row(s) for {sorted(pair)}; rows {self.rows(mod, arm)}"
        return got[0]

    def line(self, mod):
        return json.loads((self.runs[mod]["out"] / "scope" / "scope-line.json").read_text(encoding="utf-8"))["line"]


W = None


def got(mod, key):
    e = W.ledger(mod).get(key) or {}
    return f"{e.get('status')} {e.get('detector')}"


def the_runs_end_without_a_refusal():
    # rejects: a run that stops with a refusal or a crash, which the checks below would report less plainly
    for mod, r in W.runs.items():
        assert r["exit"] == 0, f"{mod}: exit {r['exit']}\n{r['stdout'][-1500:]}\n{r['stderr'][-800:]}"


def the_load_order_check_completes_on_both_arms():
    # rejects: blocked(no-target), which blocks every other check and leaves nothing measured
    for mod in W.runs:
        for arm in ("baseline", "candidate-01"):
            assert got(mod, f"loadability.masters-and-order__{arm}") == "completed no-finding", (mod, arm, got(mod, f"loadability.masters-and-order__{arm}"))


def a_mesh_only_mod_gets_its_one_new_crossing():   # its new crossings: O8, M1, M2
    # rejects: an overlap check that is blocked, or that reads the same meshes on both arms and so finds nothing
    # the baseline arm has one row, M3, which crosses with the game's mesh too
    assert got("PpMeshMod", "geometry.object-overlap__baseline__arm") == "completed finding", got("PpMeshMod", "geometry.object-overlap__baseline__arm")
    assert got("PpMeshMod", "geometry.object-overlap__candidate-01__arm") == "completed finding", got("PpMeshMod", "geometry.object-overlap__candidate-01__arm")
    assert got("PpMeshMod", "geometry.object-overlap__candidate-01__diff") == "completed finding", got("PpMeshMod", "geometry.object-overlap__candidate-01__diff")
    # without the mod only M3, which crosses with the game's mesh too; with it the table's O8, M1, M2 and M3 and no other row:
    # every other pair of the fixture is the same on both arms or outside the population
    assert W.rows("PpMeshMod", "baseline") == [sorted([C1, C2B])], W.rows("PpMeshMod", "baseline")
    want = sorted(sorted(p) for p in ([M8, B8], [S1, S2], [V1, V2], [C1, C2B], [Q1, Q2]))
    assert W.rows("PpMeshMod", "candidate-01") == want, (W.rows("PpMeshMod", "candidate-01"), want)


def the_rows_its_mesh_creates_name_the_folder():
    # rejects: O8 named by the plugins that placed the two objects (PpA.esp x PpB.esp), and the same-plugin and base-game
    # crossings not reported at all; and M3, which crosses without the mod's mesh, credited to the folder
    for pair, a, ref in (([M8, B8], "PpB.esp", M8), ([S1, S2], "PpA.esp", S1), ([V1, V2], "Skyrim.esm", V1), ([Q1, Q2], "PpA.esp", Q1)):
        c = W.row("PpMeshMod", "candidate-01", pair)
        got = (c["A"], c["B"], (c.get("Mesh") or {}).get("Folder"), c["Necessary"][0]["FormKey"])
        assert got == (a, "PpMeshMod", "PpMeshMod", ref), (pair, got)
    c = W.row("PpMeshMod", "candidate-01", [C1, C2B])
    assert {c["A"], c["B"]} == {"PpA.esp", "PpB.esp"} and "Mesh" not in c, (c["A"], c["B"], c.get("Mesh"))


def a_texture_only_mod_completes_with_no_finding():
    # rejects: a blocked check, and a finding credited to a mod that changes no mesh
    for key in ("geometry.object-overlap__baseline__arm", "geometry.object-overlap__candidate-01__arm", "geometry.object-overlap__candidate-01__diff"):
        assert got("PpTexMod", key) == "completed no-finding", (key, got("PpTexMod", key))
    assert W.rows("PpTexMod", "baseline") == [] and W.rows("PpTexMod", "candidate-01") == [], (W.rows("PpTexMod", "baseline"), W.rows("PpTexMod", "candidate-01"))


def the_scope_line_says_what_is_and_is_not_measured():
    # rejects: "places nothing in any cell, so no other plugin shares its cells", which reads like a clean result
    assert W.line("PpMeshMod") == MESH_LINE, W.line("PpMeshMod")
    assert W.line("PpTexMod") == TEX_LINE, W.line("PpTexMod")


TESTS = [
    ("the runs end without a refusal", the_runs_end_without_a_refusal),
    ("the load-order check completes on both arms", the_load_order_check_completes_on_both_arms),
    ("a mesh-only mod gets its new crossings", a_mesh_only_mod_gets_its_one_new_crossing),
    ("the rows its mesh creates name the folder", the_rows_its_mesh_creates_name_the_folder),
    ("a texture-only mod completes with no finding", a_texture_only_mod_completes_with_no_finding),
    ("the scope line says what is and is not measured", the_scope_line_says_what_is_and_is_not_measured),
]


def main():
    global W
    with tempfile.TemporaryDirectory(prefix="hc-asset-only-") as tmp:
        W = World(Path(tmp))
        for name, fn in TESTS:
            check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
