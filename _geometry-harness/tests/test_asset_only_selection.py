"""Known answers for a selection whose only effect on the world is its meshes: runcontrol evaluates it in place, with the
real probes.

    python _geometry-harness/tests/test_asset_only_selection.py

The world is PopulationFixture (_shared/PopulationFixture.cs; its table is the expectation), written by
`overlapprobe --self-test` with PD_FIXTURE_KEEP and laid out as an MO2 instance the way test_population_scope.py lays
it out. Its mod folder PpMeshMod ships meshes and no plugin; with its bigger pp/mshb.nif, m8 (PpA.esp) crosses b8
(PpB.esp), the table's row O8, "with only". Each run selects one folder in place, ticks Object Overlap (placement
contact rides on the same applicability) and stops after the evaluate stage. The folders, each in an instance of its own
beside the fixture's own folders (fold-review-fix/GATE-A.md section 6):
  - PpMeshMod (no plugin) with PpTexMod (one texture, nothing else) beside it; each selected once;
  - PpMeshModEsp: PpMeshMod's meshes and a plugin that places nothing (one GLOB): the commonest mesh mod;
  - PpSameEsp: a plugin that places nothing and a pp/mshb.nif byte-equal to the one it replaces;
  - PpBodyMod: one body mesh under meshes/actors/character, which no placed object uses;
  - PpMeshMod under PpCoverMod, a stronger folder holding another copy of every one of its meshes.
Checked: the load-order check completes on both arms; object overlap and placement contact apply exactly when a
selected folder's mesh wins for a placed reference and differs (FOLD-REVIEW finding 1), whatever the plugin; the rows
PpMeshMod's mesh alone creates name the folder; the scope line is built from the mesh reach, never from a folder listing
(finding 3). Each test names the wrong answer it rejects. The controller and the probes come from this checkout's
Release builds unless HC_ASSET_HARNESS names another build folder (the red run points it at a build of the base).
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
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
BUILDS = Path(os.environ.get("HC_ASSET_HARNESS") or buildlayout.root())
FAILURES, PASSED = [], []

ORDER = ["Skyrim.esm", "PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]
BASE_MODS = ["PpStrongMod", "PpDelta", "PpMover", "PpB", "PpA", "PpBase", "Skyrim"]   # highest priority first
M8, B8 = "000908:PpA.esp", "000907:PpB.esp"   # PopulationFixture.M8 and B8
S1, S2 = "00090B:PpA.esp", "00090C:PpA.esp"   # M1, same plugin
V1, V2 = "000901:Skyrim.esm", "000902:Skyrim.esm"   # M2, base game
C1, C2B = "00090D:PpA.esp", "00090A:PpB.esp"   # M3, crosses with either mesh
Q1, Q2 = "000911:PpA.esp", "000912:PpA.esp"     # M11, contained only inside PpMeshMod's closed shell
OVERLAP, CONTACT = "geometry.object-overlap", "geometry.placement-contact"

# The mesh reach of PpMeshMod in these instances (no Update.esm, no PpSkyMod): 12 placed objects use one of its meshes
# (m8, v1, s1, c1, t1, e1, e3, hs4, c5, q1, hs6, r1); t1's is PpStrongMod's, e1's and e3's are equal to the copies they
# replace, so 9 get a mesh it provides that wins and differs. The example is the first by FormKey: v1 in SkRoom.
NOT_A_FAULT = " With no plugin to read, the two checks cannot be ruled out, so they show as not complete; that is not a fault in the mod."
LINES = {
    "PpMeshMod": ("PpMeshMod has no plugin, so it places nothing; 9 placed objects use a mesh it provides that wins and differs from the "
                  "one it replaces (for example mshb.nif in SkRoom). Object overlap and placement contact compare those with and without "
                  "it. Worn and actor meshes, its textures (0 files) and other files are not measured."),
    "PpTexMod": ("PpTexMod has no plugin and no mesh (1 texture file), so no check here can measure it: textures, sounds and "
                 "scripts change nothing these checks read." + NOT_A_FAULT),
    "PpBodyMod": ("PpBodyMod has no plugin, and no placed object uses any of its 1 mesh file, so object overlap and placement contact "
                  "have nothing of it to compare. Worn and actor meshes (body and armour replacers) are not measured, nor are its "
                  "textures (0 files)." + NOT_A_FAULT),
    "PpMeshMod/cover": ("PpMeshMod has no plugin, and no placed object uses a mesh it changes: of the 12 placed objects that use one of its "
                        "meshes, 12 get the copy of a stronger folder (PpCoverMod 11, PpStrongMod 1). Worn and actor meshes, its textures (0 files) and other "
                        "files are not measured." + NOT_A_FAULT),
}
# A plugin selection's line ends with the mesh clause; the plugin part is ScopeRules.Line's, unchanged.
CLAUSES = {
    "PpMeshModEsp": " Its meshes: 9 placed objects use one that wins and differs from the one it replaces.",
    "PpSameEsp": " No placed object uses a mesh it changes: of the 4 placed objects that use one of its meshes, 4 get the same bytes "
                 "they had without it.",
}


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


class World:
    """The fixture as MO2 instances, one per layout, and one in-place run per selected folder."""

    def __init__(self, tmp: Path):
        keep = tmp / "fixture"
        env = dict(os.environ, PD_FIXTURE_KEEP=str(keep), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"))
        st = subprocess.run([str(exe(buildlayout.root(), "overlapprobe")), "--self-test"], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=600, env=env)
        self.pp = keep / "pp"
        if not all((self.pp / n).exists() for n in ORDER):
            raise RuntimeError(f"the fixture was not written (overlapprobe --self-test exit {st.returncode}): {st.stdout[-800:]}")
        self.tmp = tmp
        self.game = tmp / "game"
        (self.game / "Data").mkdir(parents=True)
        # never this machine's harness config: the evaluate stage would read its download pool
        (tmp / "harness.json").write_text("{}", encoding="utf-8")
        self.env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"), HOUSECARL_HARNESS_CONFIG=str(tmp / "harness.json"))
        self.runs = {}
        mesh = self.instance("mesh", ["PpTexMod", "PpMeshMod"])
        for mod in ("PpMeshMod", "PpTexMod"):
            self.runs[mod] = self.run(mesh, mod, mod)
        for mod in ("PpMeshModEsp", "PpSameEsp", "PpBodyMod"):
            self.runs[mod] = self.run(self.instance(mod, [mod], [mod + ".esp"] if mod.endswith("Esp") else []), mod, mod)
        self.runs["PpMeshMod/cover"] = self.run(self.instance("cover", ["PpCoverMod", "PpMeshMod"]), "PpMeshMod", "cover")

    def folder(self, inst, name):
        """Lay out one of the asset folders of this test in an instance."""
        dst = inst / "mods" / name
        mesh_mod = self.pp / "mods" / "PpMeshMod"
        if name in ("PpMeshMod", "PpStrongMod"):
            shutil.copytree(self.pp / "mods" / name, dst)
        elif name == "PpTexMod":
            (dst / "textures" / "pp").mkdir(parents=True)
            (dst / "textures" / "pp" / "box.dds").write_bytes(b"\0")
        elif name == "PpMeshModEsp":
            shutil.copytree(mesh_mod, dst)
            shutil.copyfile(self.pp / "extras" / "PpMeshModEsp.esp", dst / "PpMeshModEsp.esp")
        elif name == "PpSameEsp":
            (dst / "meshes" / "pp").mkdir(parents=True)
            shutil.copyfile(self.pp / "mods" / "PpBase" / "meshes" / "pp" / "mshb.nif", dst / "meshes" / "pp" / "mshb.nif")
            shutil.copyfile(self.pp / "extras" / "PpSameEsp.esp", dst / "PpSameEsp.esp")
        elif name == "PpBodyMod":
            body = dst / "meshes" / "actors" / "character" / "character assets"
            body.mkdir(parents=True)
            shutil.copyfile(mesh_mod / "meshes" / "pp" / "mshb.nif", body / "malebody_1.nif")
        elif name == "PpCoverMod":
            # another copy (PpBase's box) of every mesh PpMeshMod ships, at the same path
            for f in (mesh_mod / "meshes").rglob("*.nif"):
                out = dst / f.relative_to(mesh_mod)
                out.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(self.pp / "mods" / "PpBase" / "meshes" / "pp" / "box.nif", out)
        else:
            raise ValueError(name)

    def instance(self, name, folders, plugins=()):
        inst = self.tmp / ("instance-" + name)
        prof = inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (inst / "overwrite").mkdir()
        for n in ORDER:
            stem = Path(n).stem
            shutil.copytree(self.pp / "mods" / stem, inst / "mods" / stem)
            shutil.copyfile(self.pp / n, inst / "mods" / stem / n)
        for f in ["PpStrongMod"] + list(folders):
            self.folder(inst, f)
        order = ORDER + list(plugins)
        mods = BASE_MODS[:1] + list(folders) + BASE_MODS[1:]   # PpStrongMod above the selected folders, as in the fixture
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in order), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in order), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        return inst

    def run(self, inst, mod, label):
        out = self.tmp / ("run-" + label.replace("/", "-"))
        entry = self.tmp / f"entry-{label.replace('/', '-')}.json"
        entry.write_text(json.dumps({
            "schema": 1, "id": "asset-only-" + label.replace("/", "-"),
            "inPlace": {"instance": inst.as_posix(), "profile": "P", "game": self.game.as_posix()},
            "selection": {"mods": [mod]}, "evaluation": {"select": [OVERLAP], "depth": "standard"}}), encoding="utf-8")
        r = subprocess.run([str(exe(BUILDS, "runcontrol")), "run", "--entry", str(entry), "--out", str(out), "--through", "evaluate"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900, env=self.env)
        return {"exit": r.returncode, "stdout": r.stdout, "stderr": r.stderr, "out": out}

    def said(self, run):
        r = self.runs[run]
        return f"exit {r['exit']}\n{r['stdout'][-1500:]}\n{r['stderr'][-800:]}"

    def ledger(self, run):
        path = self.runs[run]["out"] / "evaluation" / "ledger.json"
        assert path.exists(), f"{run}: no ledger: {self.said(run)}"
        return json.loads(path.read_text(encoding="utf-8"))["entries"]

    def applicability(self, run, check_id):
        path = self.runs[run]["out"] / "evaluation" / "plan.json"
        assert path.exists(), f"{run}: no plan: {self.said(run)}"
        c = next(c for c in json.loads(path.read_text(encoding="utf-8"))["checks"] if c["checkId"] == check_id)
        return c["applicability"], c.get("applicabilityEvidence") or ""

    def doc(self, run, arm):
        path = self.runs[run]["out"] / "evaluation" / "sweep" / "inv-0001" / "overlapprobe" / (arm + ".json")
        assert path.exists(), f"{run}: object overlap wrote no result for {arm}: {self.said(run)}"
        return json.loads(path.read_text(encoding="utf-8"))

    def rows(self, run, arm):
        return sorted(sorted(k["FormKey"] for k in c["Necessary"] + c["Alternative"]) for c in self.doc(run, arm)["Conflicts"])

    def row(self, run, arm, pair):
        got = [c for c in self.doc(run, arm)["Conflicts"] if sorted(k["FormKey"] for k in c["Necessary"] + c["Alternative"]) == sorted(pair)]
        assert len(got) == 1, f"{run} {arm}: {len(got)} row(s) for {sorted(pair)}; rows {self.rows(run, arm)}"
        return got[0]

    def line(self, run):
        path = self.runs[run]["out"] / "scope" / "scope-line.json"
        assert path.exists(), f"{run}: no scope line: {self.said(run)}"
        return json.loads(path.read_text(encoding="utf-8"))["line"]


W = None


def got(run, key):
    e = W.ledger(run).get(key) or {}
    return f"{e.get('status')} {e.get('detector')}"


def the_runs_end_without_a_refusal():
    # rejects: a run that stops with a refusal or a crash, which the checks below would report less plainly
    for run, r in W.runs.items():
        assert r["exit"] == 0, f"{run}: {W.said(run)}"


def the_load_order_check_completes_on_both_arms():
    # rejects: blocked(no-target), which blocks every other check and leaves nothing measured
    for run in W.runs:
        for arm in ("baseline", "candidate-01"):
            assert got(run, f"loadability.masters-and-order__{arm}") == "completed no-finding", (run, arm, got(run, f"loadability.masters-and-order__{arm}"))


def the_mesh_checks_apply_to_a_mesh_mod_with_no_plugin():
    # rejects: "unknown" because no scope.json was measured (FOLD-REVIEW finding 1), which can never complete
    for cid in (OVERLAP, CONTACT):
        a, ev = W.applicability("PpMeshMod", cid)
        assert a == "yes" and ev.startswith("9 placed reference(s) use a mesh PpMeshMod provide(s)"), (cid, a, ev)


def the_mesh_checks_apply_to_a_mesh_mod_whose_plugin_places_nothing():
    # rejects: not-applicable from "placed: 0" for the commonest mesh mod, so the probes never see its meshes (finding 1)
    for cid in (OVERLAP, CONTACT):
        a, ev = W.applicability("PpMeshModEsp", cid)
        assert a == "yes" and ev.startswith("9 placed reference(s) use a mesh PpMeshModEsp provide(s)"), (cid, a, ev)
    for key in (f"{OVERLAP}__candidate-01__arm", f"{OVERLAP}__candidate-01__diff"):
        assert got("PpMeshModEsp", key) == "completed finding", (key, got("PpMeshModEsp", key))
    c = W.row("PpMeshModEsp", "candidate-01", [M8, B8])
    assert (c["A"], c["B"], (c.get("Mesh") or {}).get("Folder")) == ("PpB.esp", "PpMeshModEsp", "PpMeshModEsp"), (c["A"], c["B"], c.get("Mesh"))


def the_mesh_checks_do_not_apply_when_its_meshes_change_nothing():
    # rejects: "placed: 0" as the whole reason, which says nothing of the meshes; the answer is measured on both counts
    for cid in (OVERLAP, CONTACT):
        a, ev = W.applicability("PpSameEsp", cid)
        assert a == "no" and "4 use one of its meshes (4 equal to the copy they replace)" in ev, (cid, a, ev)


def no_credited_mesh_and_no_plugin_stays_unknown():
    # rejects: a not-applicable no measurement supports (with no plugin, placed cannot be read), and a yes for a mod that
    # changes no placed object's mesh
    for run in ("PpTexMod", "PpBodyMod", "PpMeshMod/cover"):
        a, _ = W.applicability(run, OVERLAP)
        assert a == "unknown", (run, a)


def a_mesh_only_mod_gets_its_new_crossings():   # O8, M1, M2, M11; M3 is a plugin row
    # rejects: an overlap check that is blocked, or that reads the same meshes on both arms and so finds nothing
    assert got("PpMeshMod", f"{OVERLAP}__baseline__arm") == "completed finding", got("PpMeshMod", f"{OVERLAP}__baseline__arm")
    assert got("PpMeshMod", f"{OVERLAP}__candidate-01__arm") == "completed finding", got("PpMeshMod", f"{OVERLAP}__candidate-01__arm")
    assert got("PpMeshMod", f"{OVERLAP}__candidate-01__diff") == "completed finding", got("PpMeshMod", f"{OVERLAP}__candidate-01__diff")
    # without the mod only M3, which crosses with the game's mesh too; with it the table's O8, M1, M2, M3 and M11 and no other
    # row: every other pair of the fixture is the same on both arms or outside the population
    assert W.rows("PpMeshMod", "baseline") == [sorted([C1, C2B])], W.rows("PpMeshMod", "baseline")
    want = sorted(sorted(p) for p in ([M8, B8], [S1, S2], [V1, V2], [C1, C2B], [Q1, Q2]))
    assert W.rows("PpMeshMod", "candidate-01") == want, (W.rows("PpMeshMod", "candidate-01"), want)


def the_rows_its_mesh_creates_name_the_folder():
    # rejects: O8 named by the plugins that placed the two objects (PpA.esp x PpB.esp), and the same-plugin and base-game
    # crossings not reported at all; and M3, which crosses without the mod's mesh, credited to the folder
    for pair, a, ref in (([M8, B8], "PpB.esp", M8), ([S1, S2], "PpA.esp", S1), ([V1, V2], "Skyrim.esm", V1), ([Q1, Q2], "PpA.esp", Q1)):
        c = W.row("PpMeshMod", "candidate-01", pair)
        g = (c["A"], c["B"], (c.get("Mesh") or {}).get("Folder"), c["Necessary"][0]["FormKey"])
        assert g == (a, "PpMeshMod", "PpMeshMod", ref), (pair, g)
    c = W.row("PpMeshMod", "candidate-01", [C1, C2B])
    assert {c["A"], c["B"]} == {"PpA.esp", "PpB.esp"} and "Mesh" not in c, (c["A"], c["B"], c.get("Mesh"))


def a_texture_only_mod_completes_with_no_finding():
    # rejects: a blocked check, and a finding credited to a mod that changes no mesh
    for key in (f"{OVERLAP}__baseline__arm", f"{OVERLAP}__candidate-01__arm", f"{OVERLAP}__candidate-01__diff"):
        assert got("PpTexMod", key) == "completed no-finding", (key, got("PpTexMod", key))
    assert W.rows("PpTexMod", "baseline") == [] and W.rows("PpTexMod", "candidate-01") == [], (W.rows("PpTexMod", "baseline"), W.rows("PpTexMod", "candidate-01"))


def the_scope_line_comes_from_the_mesh_reach():
    # rejects: "it holds N mesh files ... compare every placed object that uses one of its meshes", counted from the folder,
    # for a body replacer, a folder whose meshes a stronger one overrides, or one whose meshes change nothing (finding 3)
    for run, want in LINES.items():
        assert W.line(run) == want, (run, W.line(run))
    for run, clause in CLAUSES.items():
        assert W.line(run).endswith(clause), (run, W.line(run))


TESTS = [
    ("the runs end without a refusal", the_runs_end_without_a_refusal),
    ("the load-order check completes on both arms", the_load_order_check_completes_on_both_arms),
    ("C1 the mesh checks apply to a mesh mod with no plugin", the_mesh_checks_apply_to_a_mesh_mod_with_no_plugin),
    ("C2 the mesh checks apply to a mesh mod whose plugin places nothing, and find its crossing",
     the_mesh_checks_apply_to_a_mesh_mod_whose_plugin_places_nothing),
    ("C3 the mesh checks do not apply when its meshes change nothing, and say why", the_mesh_checks_do_not_apply_when_its_meshes_change_nothing),
    ("no credited mesh and no plugin stays unknown", no_credited_mesh_and_no_plugin_stays_unknown),
    ("a mesh-only mod gets its new crossings", a_mesh_only_mod_gets_its_new_crossings),
    ("the rows its mesh creates name the folder", the_rows_its_mesh_creates_name_the_folder),
    ("a texture-only mod completes with no finding", a_texture_only_mod_completes_with_no_finding),
    ("C4-C8 the scope line comes from the mesh reach", the_scope_line_comes_from_the_mesh_reach),
]


def main():
    global W
    with tempfile.TemporaryDirectory(prefix="hc-asset-only-") as tmp:
        W = World(Path(tmp))
        for name, fn in TESTS:
            check(name, fn)
        if os.environ.get("HC_ASSET_KEEP"):
            shutil.copytree(tmp, os.environ["HC_ASSET_KEEP"], dirs_exist_ok=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
