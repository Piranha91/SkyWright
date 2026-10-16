"""Known answers for the plan's population (P25): overlapprobe and contactprobe with --population, on PopulationFixture.

    python _geometry-harness/tests/test_population_scope.py

The fixture (_shared/PopulationFixture.cs, its table is the expectation) is written by `overlapprobe --self-test` with
PD_FIXTURE_KEEP; this file lays it out as an MO2 instance, runs the REAL probes on the arm with the delta and the arm
without it, whole-order and with --population, and checks:
  - the scoped emit is exactly the whole-order emit's rows that the probe's own audit puts in the population (every field);
  - the rows in and out of the population are the ones the fixture's table names, in both arms;
  - conflictdiff refuses a scoped emit against an unscoped one, and two scoped emits give the whole-order diff's counts.
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


ORDER = ["PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]


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
}
OVERLAP = {"O1": ("a1", "b1"), "O2": ("a2", "b2"), "O3": ("d3", "a3"), "O3b": ("b4", "a4"), "O4": ("dE", "hA"),
           "O4b": ("hA", "bE"), "O5": ("pA", "bP"), "O5b": ("pA2", "bP2"), "O6": ("Kc", "b6"), "O7": ("x7", "b7"),
           "O8": ("m8", "b8"), "O9": ("a9", "b9")}
CONTACT = {"K1": ("dc1", "hs1"), "K2": ("c2", "hs2"), "K3": ("c3", "hs3")}
# What each arm holds, whole-order and in the population (the fixture's table).
WANT = {
    ("overlapprobe", "with", "whole"): {"O1", "O2", "O3", "O3b", "O4", "O4b", "O5", "O5b", "O7", "O8", "O9"},
    ("overlapprobe", "with", "scoped"): {"O1", "O3", "O3b", "O4", "O5", "O7", "O8"},
    ("overlapprobe", "without", "whole"): {"O1", "O2", "O4b", "O5b", "O6", "O9"},
    ("overlapprobe", "without", "scoped"): {"O1", "O6"},
    ("contactprobe", "with", "whole"): {"K1", "K3"},
    ("contactprobe", "with", "scoped"): {"K1"},
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
        mods_with = ["PpMeshMod", "PpDelta", "PpMover", "PpB", "PpA", "PpBase"]
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
                                               "mods": [{"name": "PpMeshMod", "path": str(self.inst / "mods" / "PpMeshMod")}],
                                               "targets": ["PpDelta.esp"]}, indent=1), encoding="utf-8")
        self.out = {}
        self.logs = {}
        for probe in ("overlapprobe", "contactprobe"):
            for arm in ("with", "without"):
                whole = tmp / f"{probe}-{arm}-whole.json"
                r = self.probe(probe, arm, ["--emit", whole])
                self.logs[(probe, arm, "whole")] = r
                scoped = tmp / f"{probe}-{arm}-scoped.json"
                audit = tmp / f"{probe}-{arm}-audit.json"
                r2 = self.probe(probe, arm, ["--emit", scoped, "--population", self.population,
                                             "--population-audit", whole, audit])
                self.logs[(probe, arm, "scoped")] = r2
                self.out[(probe, arm)] = (whole, scoped, audit)

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
]


def main():
    for name, fn in TESTS:
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
