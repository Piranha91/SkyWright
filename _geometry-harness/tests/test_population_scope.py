"""Known answers for the plan's population (P25): overlapprobe and contactprobe with --population, on PopulationFixture;
and (window 2) contactprobe --empty-space and stateprobe with --population, on ScopeFixture.

    python _geometry-harness/tests/test_population_scope.py

The fixture (_shared/PopulationFixture.cs, its table is the expectation) is written by `overlapprobe --self-test` with
PD_FIXTURE_KEEP; this file lays it out as an MO2 instance, runs the REAL probes on the arm with the delta and the arm
without it, whole-order and with --population, and checks:
  - the scoped emit is exactly the whole-order emit's rows that the probe's own audit puts in the population (every field);
  - the rows in and out of the population are the ones the fixture's table names, in both arms;
  - conflictdiff refuses a scoped emit against an unscoped one, and two scoped emits give the whole-order diff's counts.
Mesh responsibility (cases M1-M8, K4, K5 of the fixture's table; mesh-responsibility/GATE-B.md): the rows a folder's mesh alone
creates name the folder; the whole-order runs credit the population's folders (--mesh-folder, so scoped = whole holds), and one
more whole-order run on the arm with the delta credits every folder. HC_POP_NO_MESH_FOLDERS=1 leaves --mesh-folder off (the red
run on a build from before the option, whose whole-order runs would otherwise stop on an unknown argument).
Each test names the wrong answer it rejects. Probes come from this checkout's Release builds unless HC_POP_HARNESS names
another harness folder (the red run points it at a build without --population).
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
PROBES = Path(os.environ.get("HC_POP_HARNESS") or HARNESS)
FAILURES, PASSED = [], []


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


ORDER = ["Skyrim.esm", "PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]
MESH_FOLDERS = ["PpMeshMod", "PpDelta"]   # the population's folders
NO_MESH_FLAG = bool(os.environ.get("HC_POP_NO_MESH_FOLDERS"))


def fk(hexid, plugin):
    return f"{hexid}:{plugin}"


# The fixture's references by name (PopulationFixture.cs).
R = {
    "b4": fk("000901", "PpBase.esm"), "hs1": fk("000902", "PpBase.esm"), "pA": fk("000903", "PpBase.esm"),
    "c2": fk("000906", "PpBase.esm"), "hs2": fk("000905", "PpBase.esm"), "hs3": fk("000907", "PpBase.esm"),
    "c3": fk("000908", "PpBase.esm"),
    "a1": fk("000901", "PpA.esp"), "a2": fk("000902", "PpA.esp"), "a3": fk("000903", "PpA.esp"), "a4": fk("000904", "PpA.esp"),
    "hA": fk("000905", "PpA.esp"), "Kc": fk("000906", "PpA.esp"), "x7": fk("000907", "PpA.esp"), "m8": fk("000908", "PpA.esp"),
    "a9": fk("000909", "PpA.esp"), "pA2": fk("00090A", "PpA.esp"),
    "b1": fk("000901", "PpB.esp"), "b2": fk("000902", "PpB.esp"), "bE": fk("000903", "PpB.esp"), "bP": fk("000904", "PpB.esp"),
    "bP2": fk("000905", "PpB.esp"), "b7": fk("000906", "PpB.esp"), "b8": fk("000907", "PpB.esp"), "b9": fk("000908", "PpB.esp"),
    "b6": fk("000909", "PpB.esp"),
    "d3": fk("000902", "PpDelta.esp"), "dE": fk("000903", "PpDelta.esp"), "dc1": fk("000904", "PpDelta.esp"),
    # mesh responsibility
    "s1": fk("00090B", "PpA.esp"), "s2": fk("00090C", "PpA.esp"), "c1": fk("00090D", "PpA.esp"), "t1": fk("00090E", "PpA.esp"),
    "t2": fk("00090F", "PpA.esp"), "u2": fk("000910", "PpA.esp"), "c2b": fk("00090A", "PpB.esp"),
    "o1": fk("000906", "PpDelta.esp"), "n1": fk("000907", "PpDelta.esp"), "n2": fk("000908", "PpDelta.esp"), "u1": fk("000909", "PpDelta.esp"),
    "o2": fk("000909", "PpBase.esm"), "hs4": fk("00090A", "PpBase.esm"), "c4": fk("00090B", "PpBase.esm"),
    "hs5": fk("00090C", "PpBase.esm"), "c5": fk("00090D", "PpBase.esm"),
    "v1": fk("000901", "Skyrim.esm"), "v2": fk("000902", "Skyrim.esm"), "e1": fk("000903", "Skyrim.esm"),
    "e2": fk("000904", "Skyrim.esm"), "e3": fk("000905", "Skyrim.esm"), "e4": fk("000906", "Skyrim.esm"),
}
OVERLAP = {"O1": ("a1", "b1"), "O2": ("a2", "b2"), "O3": ("d3", "a3"), "O3b": ("b4", "a4"), "O4": ("dE", "hA"),
           "O4b": ("hA", "bE"), "O5": ("pA", "bP"), "O5b": ("pA2", "bP2"), "O6": ("Kc", "b6"), "O7": ("x7", "b7"),
           "O8": ("m8", "b8"), "O9": ("a9", "b9"),
           "M1": ("s1", "s2"), "M2": ("v1", "v2"), "M3": ("c1", "c2b"), "M4": ("t1", "t2"), "M5": ("e1", "e2"), "M5b": ("e3", "e4"),
           "M6": ("o2", "o1"), "M7": ("n1", "n2"), "M8": ("u1", "u2")}
CONTACT = {"K1": ("dc1", "hs1"), "K2": ("c2", "hs2"), "K3": ("c3", "hs3"), "K4": ("c4", "hs4"), "K5": ("c5", "hs5")}
# What each arm holds, whole-order and in the population (the fixture's table).
WANT = {
    ("overlapprobe", "with", "whole"): {"O1", "O2", "O3", "O3b", "O4", "O4b", "O5", "O5b", "O7", "O8", "O9", "M1", "M2", "M3", "M8"},
    ("overlapprobe", "with", "scoped"): {"O1", "O3", "O3b", "O4", "O5", "O7", "O8", "M1", "M2", "M3", "M8"},
    ("overlapprobe", "without", "whole"): {"O1", "O2", "O4b", "O5b", "O6", "O9", "M3"},
    ("overlapprobe", "without", "scoped"): {"O1", "O6", "M3"},
    ("contactprobe", "with", "whole"): {"K1", "K3", "K4", "K5"},
    ("contactprobe", "with", "scoped"): {"K1", "K4", "K5"},
    ("contactprobe", "without", "whole"): {"K2", "K3"},
    ("contactprobe", "without", "scoped"): {"K2"},
}


def row_name(probe, row):
    """The fixture case a row is, by its two references (unordered), or the row's own text when it is none of them."""
    keys = {r["FormKey"].lower() for r in row["Necessary"] + (row.get("Alternative") or [])}
    table = OVERLAP if probe == "overlapprobe" else CONTACT
    for name, (x, y) in table.items():
        if {R[x].lower(), R[y].lower()} == keys:
            return name
    return "UNEXPECTED " + row["Summary"]


def identity(kind, form_key, other, a):
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    other = other.lower() if other else None
    if other is None:
        return f"{kind}|{form_key}|{a}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def rows_of(doc):
    out = {}
    for c in doc.get("Conflicts", []):
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        for r in c["Necessary"]:
            out[identity(c["Kind"], r["FormKey"], other, c["A"])] = c
    return out


class Lab:
    """The fixture laid out as an MO2 instance, and every probe run this file reads (run once, shared by the checks)."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(HARNESS, "overlapprobe"), "--self-test"], env=dict(os.environ, PD_FIXTURE_KEEP=str(keep)))
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
        mods_with = ["PpStrongMod", "PpMeshMod", "PpDelta", "PpMover", "PpB", "PpA", "PpBase", "Skyrim"]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods_with), encoding="utf-8")
        self.cand = {}
        for arm, plugins, mods in (("with", ORDER, mods_with),
                                   ("without", [n for n in ORDER if n != "PpDelta.esp"],
                                    [m for m in mods_with if m not in ("PpDelta", "PpMeshMod")])):
            p = tmp / f"candidate-{arm}.json"
            p.write_text(json.dumps({"SchemaVersion": 1, "Name": arm, "BaseProfile": "P", "Plugins": plugins, "Mods": mods,
                                     "Changes": []}), encoding="utf-8")
            self.cand[arm] = p
        delta = self.inst / "mods" / "PpDelta" / "PpDelta.esp"
        import hashlib
        sha = hashlib.sha256(delta.read_bytes()).hexdigest()
        self.population = tmp / "population.json"
        self.population.write_text(json.dumps({"schema": 1,
                                               "plugins": [{"name": "PpDelta.esp", "path": str(delta), "sha256": sha}],
                                               "mods": [{"name": m, "path": str(self.inst / "mods" / m)} for m in MESH_FOLDERS],
                                               "targets": ["PpDelta.esp"]}, indent=1), encoding="utf-8")
        self.out = {}
        self.logs = {}
        for probe in ("overlapprobe", "contactprobe"):
            for arm in ("with", "without"):
                whole = tmp / f"{probe}-{arm}-whole.json"
                r = self.probe(probe, arm, ["--emit", whole] + self.mesh_flags())
                self.logs[(probe, arm, "whole")] = r
                scoped = tmp / f"{probe}-{arm}-scoped.json"
                audit = tmp / f"{probe}-{arm}-audit.json"
                r2 = self.probe(probe, arm, ["--emit", scoped, "--population", self.population,
                                             "--population-audit", whole, audit])
                self.logs[(probe, arm, "scoped")] = r2
                self.out[(probe, arm)] = (whole, scoped, audit)
            # the arm with the delta, whole-order, every folder credited (no --mesh-folder)
            every = tmp / f"{probe}-with-every.json"
            self.logs[(probe, "with", "every")] = self.probe(probe, "with", ["--emit", every])
            self.out[(probe, "every")] = every

    @staticmethod
    def mesh_flags():
        return [] if NO_MESH_FLAG else [x for m in MESH_FOLDERS for x in ("--mesh-folder", m)]

    def probe(self, probe, arm, extra):
        return run([exe(PROBES, probe), "--order", self.cand[arm], "--instance", self.inst, "--game", self.game,
                    "--profile", "P"] + extra)

    def load(self, path):
        return json.loads(Path(path).read_text(encoding="utf-8")) if Path(path).exists() else None


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="pop-scope-")))
    return LAB


def names(probe, doc):
    return {row_name(probe, c) for c in doc.get("Conflicts", [])}


def expect_rows(probe, arm, which):
    def t():
        L = lab()
        whole, scoped, _ = L.out[(probe, arm)]
        path = whole if which == "whole" else scoped
        log = L.logs[(probe, arm, which)]
        doc = L.load(path)
        assert log.returncode == 0 and doc is not None, f"{probe} {arm} {which}: exit {log.returncode}: {log.stderr[-600:]}"
        got = names(probe, doc)
        want = WANT[(probe, arm, which)]
        assert got == want, f"{probe} {arm} {which}: rows {sorted(got)}, the fixture's table says {sorted(want)}"
    return t


def scoped_is_whole_in_scope(probe, arm):
    """Rejects: a scoped run that drops, adds or alters a row the whole-order run judges for the population."""
    def t():
        L = lab()
        whole, scoped, audit = L.out[(probe, arm)]
        w, s, a = L.load(whole), L.load(scoped), L.load(audit)
        assert w and s and a, f"{probe} {arm}: missing output (whole {bool(w)}, scoped {bool(s)}, audit {bool(a)})"
        wr, sr = rows_of(w), rows_of(s)
        ins = {x["identity"] for x in a["rows"] if x["inScope"]}
        assert len(a["rows"]) == len(wr), f"the audit judged {len(a['rows'])} rows of {len(wr)}"
        assert set(sr) == ins, f"scoped rows {sorted(set(sr) ^ ins)} differ from the audit's in-scope set"
        for i in sr:
            assert sr[i] == wr[i], f"row {i} differs: scoped {json.dumps(sr[i])[:400]} vs whole {json.dumps(wr[i])[:400]}"
        pop = s.get("Population") or ""
        assert pop.startswith("load-time+population:") and len(pop) == len("load-time+population:") + 12, pop
        assert (w.get("Population") or "") == "load-time", w.get("Population")
        assert s["Configuration"]["Population"] == pop, "the configuration identity carries the scoped population"
    return t


def diff_gate_and_counts():
    """Rejects: crediting a scoped emit against an unscoped one; a scoped differential whose counts differ from the whole's."""
    L = lab()
    cd = exe(PROBES, "conflictdiff")
    for probe in ("overlapprobe", "contactprobe"):
        ww, sw_, _ = L.out[(probe, "with")]
        wo, so, _ = L.out[(probe, "without")]
        mixed = run([cd, ww, so])
        assert mixed.returncode == 3, f"{probe}: unscoped vs scoped must be refused (exit 3), got {mixed.returncode}: {mixed.stdout[-400:]}"
        whole = run([cd, ww, wo])
        scoped = run([cd, sw_, so])
        assert whole.returncode == 0 and scoped.returncode == 0, f"{probe}: diff exits {whole.returncode}/{scoped.returncode}"

        def counts(text):
            out = {}
            for ln in text.splitlines():
                parts = ln.split(":")
                k = parts[0].strip()
                if k in ("repaired", "reclassified", "created", "unmeasured") and len(parts) > 1:
                    out[k] = int(parts[1].split()[0].replace(",", ""))
                if ln.strip().startswith("verdict"):
                    out["verdict"] = ln.split(":", 1)[1].split()[0]
            return out
        cw, cs = counts(whole.stdout), counts(scoped.stdout)
        assert cw == cs and cw, f"{probe}: whole diff {cw}, scoped diff {cs}"


def refused_outside_default_pass():
    """Rejects: --population quietly accepted where it does not scope anything."""
    L = lab()
    r = L.probe("contactprobe", "with", ["--absolute", "--population", L.population])
    assert r.returncode == 2 and "default moved-host pass" in r.stderr, (r.returncode, r.stderr[-300:])
    r = L.probe("overlapprobe", "with", ["--responsible-pair", "PpA.esp", "PpB.esp", "--population", L.population])
    assert r.returncode == 2 and "--population scopes the emit pass" in r.stderr, (r.returncode, r.stderr[-300:])


def row_of(probe, doc, case):
    rows = [c for c in doc.get("Conflicts", []) if row_name(probe, c) == case]
    assert len(rows) == 1, f"{case}: {len(rows)} row(s) where the fixture's table has one: {[c['Summary'][:160] for c in rows]}"
    return rows[0]


def emit_of(probe, arm, which):
    L = lab()
    path = L.out[(probe, "every")] if which == "every" else L.out[(probe, arm)][0 if which == "whole" else 1]
    doc = L.load(path)
    assert doc is not None, f"{probe} {arm} {which}: no emit; {L.logs[(probe, arm, which)].stderr[-600:]}"
    return doc


# (case, A, B, the reference whose mesh the folder provides) for every row a folder's mesh alone creates
FOLDER_ROWS = {"overlapprobe": [("O8", "PpB.esp", "PpMeshMod", "m8"), ("M1", "PpA.esp", "PpMeshMod", "s1"),
                                ("M2", "Skyrim.esm", "PpMeshMod", "v1")],
               "contactprobe": [("K4", "PpBase.esm", "PpMeshMod", "c4"), ("K5", "PpBase.esm", "PpMeshMod", "c5")]}


def folder_rows_name_the_folder(probe, which):
    """Rejects: a row a provided mesh alone creates named by the plugins that placed the objects (O8 today: PpA.esp x
    PpB.esp), or not reported at all (a same-plugin or base-game pair, a reshaped host or dependent)."""
    def t():
        doc = emit_of(probe, "with", which)
        for case, a, b, ref in FOLDER_ROWS[probe]:
            c = row_of(probe, doc, case)
            mesh = c.get("Mesh") or {}
            assert (c["A"], c["B"], mesh.get("Folder")) == (a, b, b), \
                f"{case}: named {c['A']} x {c['B']} (mesh folder {mesh.get('Folder')}); the table says {a} x {b}, the mesh from {b}"
            assert c["Necessary"][0]["FormKey"].lower() == R[ref].lower(), f"{case}: the fix target is {c['Necessary'][0]['FormKey']}, not {ref}"
    return t


def plugin_rows_stay_plugin_rows(which):
    """Rejects: a folder credited for a crossing that exists without its mesh (M3), or for a folder outside the population (M8)."""
    def t():
        doc = emit_of("overlapprobe", "with", which)
        for case, pair in (("M3", {"PpA.esp", "PpB.esp"}), ("M8", {"PpA.esp", "PpDelta.esp"})):
            c = row_of("overlapprobe", doc, case)
            assert {c["A"], c["B"]} == pair and "Mesh" not in c, f"{case}: {c['A']} x {c['B']}, mesh {c.get('Mesh')}; the table says {sorted(pair)}, no mesh"
    return t


def every_folder_credits_the_outside_folder():
    """Rejects: a whole-order run that names no folder, or the folder a stronger one overrides (M4), when every folder may be
    credited: M8's crossing exists only with PpStrongMod's mesh."""
    doc = emit_of("overlapprobe", "with", "every")
    c = row_of("overlapprobe", doc, "M8")
    assert (c["A"], c["B"], (c.get("Mesh") or {}).get("Folder")) == ("PpA.esp", "PpStrongMod", "PpStrongMod"), \
        f"M8 with every folder credited: {c['A']} x {c['B']}, mesh {(c.get('Mesh') or {}).get('Folder')}; the table says PpA.esp x PpStrongMod"
    got = names("overlapprobe", doc)
    want = WANT[("overlapprobe", "with", "whole")]
    assert got == want, f"every-folder rows {sorted(got)}, the table says {sorted(want)} (the same cases, M8 renamed)"


def coverage(doc, population):
    lines = [c for c in doc.get("Coverage") or [] if c.get("Population") == population]
    return lines[0] if lines else {}


def mesh_counts_are_the_tables(arm, which):
    """Rejects: equal bytes read as a replacement (M5 loose, M5b loose against a compressed archive entry), a new mesh read
    as one (M7), and a base-game pair never readmitted (M2): the counts the fixture's table gives."""
    def t():
        doc = emit_of("overlapprobe", arm, which)
        m = coverage(doc, "mesh responsibility")
        aside = m.get("SetAside") or {}
        want = {"with": (5, {"CrossesWithoutTheFolderMesh": 1, "MeshEqualsReplaced": 2, "NewMeshNoFallback": 1, "BaseGameReadmitted": 2}),
                "without": (0, {"CrossesWithoutTheFolderMesh": 0, "MeshEqualsReplaced": 0, "NewMeshNoFallback": 0, "BaseGameReadmitted": 0})}[arm]
        cand = coverage(doc, "candidate refs").get("SetAside") or {}
        # six Skyrim.esm references set aside; with the mod, v1 and v2 come back in
        want = want + ({"with": 4, "without": 6}[arm],)
        got = (m.get("Judged", -1), {k: aside.get(k, -1) for k in want[1]}, cand.get("BaseGameResponsible"))
        assert got == want, (f"{arm} {which}: (pairs given a mesh counterfactual, counts, base-game references set aside) {got}, "
                             f"the table says {want}")
    return t


TESTS = [
    ("C409 overlap, arm with the delta, whole-order rows are the fixture's", expect_rows("overlapprobe", "with", "whole")),
    ("C409 overlap, arm without the delta, whole-order rows are the fixture's", expect_rows("overlapprobe", "without", "whole")),
    ("C402/C403/C405/C406 overlap, arm with the delta, scoped rows: the large partner, the persistent ref, the base override and the mod mesh are kept; out-of-population rows are not",
     expect_rows("overlapprobe", "with", "scoped")),
    ("C404/C400 overlap, arm without the delta, scoped rows: the enable child of a delta-disabled parent is kept",
     expect_rows("overlapprobe", "without", "scoped")),
    ("C409 overlap, arm with: scoped = whole restricted to the probe's audit, every field", scoped_is_whole_in_scope("overlapprobe", "with")),
    ("C409 overlap, arm without: scoped = whole restricted to the probe's audit, every field", scoped_is_whole_in_scope("overlapprobe", "without")),
    ("C410 contact, arm with, whole-order rows", expect_rows("contactprobe", "with", "whole")),
    ("C410 contact, arm without, whole-order rows", expect_rows("contactprobe", "without", "whole")),
    ("C408 contact, arm with, scoped: the delta's stranded cup on a non-delta host is kept; the far one is not",
     expect_rows("contactprobe", "with", "scoped")),
    ("C407 contact, arm without, scoped: the cup the delta's slab re-supports is kept where the slab does not exist",
     expect_rows("contactprobe", "without", "scoped")),
    ("C410 contact, arm with: scoped = whole restricted to the probe's audit", scoped_is_whole_in_scope("contactprobe", "with")),
    ("C410 contact, arm without: scoped = whole restricted to the probe's audit", scoped_is_whole_in_scope("contactprobe", "without")),
    ("C411 conflictdiff refuses scoped vs unscoped; two scoped emits give the whole-order diff's verdict and counts", diff_gate_and_counts),
    ("C411 --population is refused outside the pass it scopes", refused_outside_default_pass),
    ("C412 overlap, arm with, whole: a row a provided mesh alone creates names the folder (O8, M1 same plugin, M2 base game)",
     folder_rows_name_the_folder("overlapprobe", "whole")),
    ("C412 overlap, arm with, scoped: the same rows name the folder", folder_rows_name_the_folder("overlapprobe", "scoped")),
    ("C413 contact, arm with, whole: a host (K4) and a dependent (K5) a provided mesh reshapes name the folder",
     folder_rows_name_the_folder("contactprobe", "whole")),
    ("C413 contact, arm with, scoped: the same rows name the folder", folder_rows_name_the_folder("contactprobe", "scoped")),
    ("C414 overlap, arm with, whole: a crossing without the mesh (M3) and one a non-population folder makes (M8) keep the plugins",
     plugin_rows_stay_plugin_rows("whole")),
    ("C414 overlap, arm with, scoped: the same", plugin_rows_stay_plugin_rows("scoped")),
    ("C415 overlap, every folder credited: M8 names PpStrongMod", every_folder_credits_the_outside_folder),
    ("C416 overlap, arm with, whole: mesh counts and base-game readmission are the table's", mesh_counts_are_the_tables("with", "whole")),
    ("C416 overlap, arm with, scoped: the same", mesh_counts_are_the_tables("with", "scoped")),
    ("C416 overlap, arm without, whole: nothing credited, nothing readmitted", mesh_counts_are_the_tables("without", "whole")),
]



# ======================================================================================================================
# Window 2, task 1: contactprobe --empty-space and stateprobe with --population, on ScopeFixture (_shared/ScopeFixture.cs,
# its table is the expectation). Cases C420-C430.
# ======================================================================================================================

SC_ORDER = ["ScBase.esm", "ScA.esp", "ScReloc.esp", "ScMover.esp", "ScDelta.esp"]
S = {"f1": fk("000901", "ScA.esp"), "f2": fk("000902", "ScA.esp"), "f3": fk("000903", "ScA.esp"), "f4": fk("000904", "ScA.esp"),
     "c1": fk("000921", "ScA.esp"), "c2": fk("000922", "ScA.esp"), "c3": fk("000923", "ScA.esp"), "c4": fk("000924", "ScA.esp"),
     "c5": fk("000925", "ScA.esp"), "c6": fk("000926", "ScA.esp")}
SC_RULE = {"c1": "contact", "c2": None, "c3": "route-ring", "c4": None, "c5": "route-ring", "c6": "large-ref"}


class ScLab:
    """ScopeFixture laid out as an MO2 instance; every run the checks below read (run once, shared)."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(HARNESS, "stateprobe"), "--write-scope-fixture", keep])
        sc = keep / "sc"
        if st.returncode != 0 or not all((sc / n).exists() for n in SC_ORDER):
            raise RuntimeError(f"the scope fixture was not written (exit {st.returncode}): {st.stdout[-600:]} {st.stderr[-600:]}")
        self.inst, self.game = tmp / "instance", tmp / "game"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True)
        (self.inst / "overwrite").mkdir()
        (self.game / "Data").mkdir(parents=True)
        for n in SC_ORDER:
            stem = Path(n).stem
            shutil.copytree(sc / "mods" / stem, self.inst / "mods" / stem)
            shutil.copyfile(sc / n, self.inst / "mods" / stem / n)
        mods = [Path(n).stem for n in reversed(SC_ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in SC_ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in SC_ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in mods), encoding="utf-8")
        self.cand = {}
        for arm, plugins in (("with", SC_ORDER), ("without", [n for n in SC_ORDER if n != "ScDelta.esp"])):
            p = tmp / f"sc-candidate-{arm}.json"
            p.write_text(json.dumps({"SchemaVersion": 1, "Name": arm, "BaseProfile": "P", "Plugins": plugins,
                                     "Mods": [m for m in mods if m + ".esp" in plugins or m + ".esm" in plugins], "Changes": []}),
                         encoding="utf-8")
            self.cand[arm] = p
        import hashlib

        def popfile(name, plugin_path):
            sha = hashlib.sha256(Path(plugin_path).read_bytes()).hexdigest()
            p = tmp / name
            p.write_text(json.dumps({"schema": 1, "plugins": [{"name": Path(plugin_path).name, "path": str(plugin_path), "sha256": sha}],
                                     "mods": [], "targets": [Path(plugin_path).name]}, indent=1), encoding="utf-8")
            return p
        self.population = popfile("sc-population.json", self.inst / "mods" / "ScDelta" / "ScDelta.esp")
        self.race_population = popfile("sc-race-population.json", sc / "ScRace.esp")
        self.out, self.logs = {}, {}
        for probe in ("contactprobe", "stateprobe"):
            for arm in ("with", "without"):
                whole = tmp / f"sc-{probe}-{arm}-whole.json"
                side_w = tmp / f"sc-{probe}-{arm}-whole.side.json"
                r = self.probe(probe, arm, ["--emit", whole, "--empty-space", side_w])
                self.logs[(probe, arm, "whole")] = r
                scoped = tmp / f"sc-{probe}-{arm}-scoped.json"
                side_s = tmp / f"sc-{probe}-{arm}-scoped.side.json"
                audit = tmp / f"sc-{probe}-{arm}-audit.json"
                r2 = self.probe(probe, arm, ["--emit", scoped, "--empty-space", side_s, "--population", self.population,
                                             "--population-audit", whole, audit])
                self.logs[(probe, arm, "scoped")] = r2
                self.out[(probe, arm)] = (whole, scoped, audit, side_w, side_s)

    def probe(self, probe, arm, extra):
        mode = ["--all"] if probe == "stateprobe" else []
        return run([exe(PROBES, probe), "--order", self.cand[arm], "--instance", self.inst, "--game", self.game,
                    "--profile", "P"] + mode + extra)


SC_LAB = None


def sclab():
    global SC_LAB
    if SC_LAB is None:
        SC_LAB = ScLab(Path(tempfile.mkdtemp(prefix="pop-scope-sc-")))
    return SC_LAB


def sc_keys(doc):
    """The necessary record of every row, by fixture name."""
    names = {v.lower(): k for k, v in S.items()}
    return {names.get(r["FormKey"].lower(), "UNEXPECTED " + c["Summary"][:120])
            for c in doc.get("Conflicts", []) for r in c["Necessary"]}


def sc_expect(probe, arm, which, want):
    """Rejects: a scoped run that keeps a row the fixture puts outside the population, or drops one inside it."""
    def t():
        L = sclab()
        whole, scoped, *_ = L.out[(probe, arm)]
        log = L.logs[(probe, arm, which)]
        doc = json.loads(Path(whole if which == "whole" else scoped).read_text(encoding="utf-8")) if Path(whole if which == "whole" else scoped).exists() else None
        assert log.returncode == 0 and doc is not None, f"{probe} {arm} {which}: exit {log.returncode}: {log.stderr[-600:]} {log.stdout[-600:]}"
        got = sc_keys(doc)
        assert got == want, f"{probe} {arm} {which}: rows {sorted(got)}, the fixture's table says {sorted(want)}"
    return t


def sc_scoped_is_whole_in_scope(probe, arm):
    """Rejects: a scoped row that differs in any field from the whole-order run's; a kept or dropped row the audit disagrees
    with; a side-file row that differs."""
    def t():
        L = sclab()
        whole, scoped, audit, side_w, side_s = L.out[(probe, arm)]
        w, s, a = (json.loads(Path(p).read_text(encoding="utf-8")) for p in (whole, scoped, audit))
        wr, sr = rows_of(w), rows_of(s)
        ins = {x["identity"] for x in a["rows"] if x["inScope"]}
        assert len(a["rows"]) == len(wr), f"the audit judged {len(a['rows'])} rows of {len(wr)}"
        assert set(sr) == ins, f"scoped rows {sorted(set(sr) ^ ins)} differ from the audit's in-scope set"
        for i in sr:
            assert sr[i] == wr[i], f"row {i} differs: scoped {json.dumps(sr[i])[:400]} vs whole {json.dumps(wr[i])[:400]}"
        pop = s.get("Population") or ""
        assert pop.startswith("load-time+population:") and len(pop) == len("load-time+population:") + 12, pop
        assert (w.get("Population") or "") == "load-time", w.get("Population")
        assert s["Configuration"]["Population"] == pop, "the configuration identity carries the scoped population"
        sw_, ss = (json.loads(Path(p).read_text(encoding="utf-8")) for p in (side_w, side_s))
        assert "Population" not in sw_ and ss.get("Population") == pop, (sw_.get("Population"), ss.get("Population"))
        kept = {r["Fixture"].lower() for r in ss["Rows"]}
        want_side = [r for r in sw_["Rows"] if r["Fixture"].lower() in kept]
        assert sorted(json.dumps(r, sort_keys=True) for r in ss["Rows"]) == sorted(json.dumps(r, sort_keys=True) for r in want_side), \
            "a side-file row of a kept fixture differs from the whole-order run's"
    return t


def sc_rules():
    """Rejects: a changed reference kept by the wrong part of the route rule (ring 2 for 5, no delta-terrain cell, no large-
    reference list), or one kept that the table drops."""
    L = sclab()
    for arm in ("with", "without"):
        a = json.loads(Path(L.out[("stateprobe", arm)][2]).read_text(encoding="utf-8"))
        got = {}
        names = {v.lower(): k for k, v in S.items()}
        for row in a["rows"]:
            got[names.get(row["key"].lower(), row["key"])] = row["rule"]
        missing = [k for k in SC_RULE if k not in got]
        wrong = {k: got.get(k) for k in SC_RULE if k in got and got[k] != SC_RULE[k]}
        assert not missing and not wrong, f"{arm}: rules {got}; missing {missing}, wrong {wrong}; the fixture's table says {SC_RULE}"


def sc_closure_and_exterior():
    """Rejects: an empty-space scope without the relocation closure (IFar's f3 dropped), or one that does not judge the fixture
    once placed outside (f4: its exterior assembly's readings would then come from another order of judgements)."""
    L = sclab()
    for arm in ("with", "without"):
        log = L.logs[("contactprobe", arm, "scoped")].stdout
        line = next((ln for ln in log.splitlines() if "population scope:" in ln and "interior cells" in ln), "")
        assert line.strip().startswith("population scope: 2 interior cells"), f"{arm}: {line!r} (IPop and, by r's relocation, IFar)"
        ex = next((ln for ln in log.splitlines() if "judged only to keep the exterior memos exact" in ln), "")
        n = int(ex.split(";")[1].split()[0].replace(",", "")) if ";" in ex else -1
        assert n >= 1, f"{arm}: f4 must be judged and not reported ({ex!r})"


def sc_race_refused():
    """Rejects: stateprobe accepting a population whose delta carries RACE records (every actor band reads them)."""
    L = sclab()
    r = run([exe(PROBES, "stateprobe"), "--order", L.cand["with"], "--instance", L.inst, "--game", L.game, "--profile", "P",
             "--all", "--emit", L.tmp / "race.json", "--population", L.race_population])
    assert r.returncode == 2 and "RACE records" in r.stderr, (r.returncode, r.stderr[-300:])
    r2 = run([exe(PROBES, "contactprobe"), "--order", L.cand["with"], "--instance", L.inst, "--game", L.game, "--profile", "P",
              "--empty-space", L.tmp / "es.json", "--emit", L.tmp / "es-emit.json", "--base", L.out[("contactprobe", "with")][0],
              "--population", L.population])
    assert r2.returncode == 2 and "takes no --base" in r2.stderr, (r2.returncode, r2.stderr[-300:])


TESTS += [
    ("C420 empty space, arm with, whole-order rows are the fixture's", sc_expect("contactprobe", "with", "whole", {"f1", "f2", "f3", "f4"})),
    ("C420 empty space, arm with, scoped: f1 (a delta cell) and f3 (pulled in by the relocation closure) kept, f2 dropped",
     sc_expect("contactprobe", "with", "scoped", {"f1", "f3"})),
    ("C420 empty space, arm without, scoped: the same fixtures where the delta is absent", sc_expect("contactprobe", "without", "scoped", {"f1", "f3"})),
    ("C420 empty space, arm with: scoped = whole restricted to the probe's audit, every field, side file too", sc_scoped_is_whole_in_scope("contactprobe", "with")),
    ("C420 empty space, arm without: scoped = whole restricted to the probe's audit", sc_scoped_is_whole_in_scope("contactprobe", "without")),
    ("C421/C423 the closure pulls IFar in; the once-exterior fixture is judged and not reported", sc_closure_and_exterior),
    ("C424 state, arm with, whole-order rows are the fixture's", sc_expect("stateprobe", "with", "whole", {"c1", "c2", "c3", "c4", "c5", "c6"})),
    ("C424/C425 state, arm with, scoped: c1 (population cell), c3 (4 cells from a seed), c5 (4 from a delta terrain cell), c6 (3 from a listed large ref)",
     sc_expect("stateprobe", "with", "scoped", {"c1", "c3", "c5", "c6"})),
    ("C425 state, arm without, scoped: the same rows where the delta is absent", sc_expect("stateprobe", "without", "scoped", {"c1", "c3", "c5", "c6"})),
    ("C425 each kept changed reference is kept by the rule the table names", sc_rules),
    ("C427 state, arm with: scoped = whole restricted to the probe's audit, every field", sc_scoped_is_whole_in_scope("stateprobe", "with")),
    ("C427 state, arm without: scoped = whole restricted to the probe's audit", sc_scoped_is_whole_in_scope("stateprobe", "without")),
    ("C426 stateprobe refuses a RACE-carrying delta; --population with --empty-space refuses --base", sc_race_refused),
]


def main():
    for name, fn in TESTS:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
