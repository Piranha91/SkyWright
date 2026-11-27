"""Known answers for overlapprobe --reverse-of (overlap acceleration P1): a reverse that takes the baseline emit's rows.

    python _geometry-harness/tests/test_reverse_reconcile.py

On PopulationFixture (written by `overlapprobe --self-test` with PD_FIXTURE_KEEP, laid out as an MO2 instance the way
test_population_scope.py does), with the REAL overlapprobe, whole-order and with --population:
  - baseline B on the "with" order; candidate C on the "without" order with --base B;
  - the full reverse: the "with" order with --base C; the --reverse-of reverse: the same, plus --reverse-of B;
  - the two reverse emits must be equal in every field but GeneratedUtc, row for row and in order;
  - no coverage note in any of the four emits carries a wall-clock time (a timing made the two reverse emits differ on a
    busy machine; timings are printed, never written into the emit);
  - a baseline emit that is not this run's own must be refused by name, for each check: another configuration (the other
    order's emit, and B with its fingerprint changed), another population, another policy, another instance, an emit that
    was itself run with --base, a sweep-independent coverage line that differs, other pair limits (recorded, and a run
    given another --pair-seconds), and an emit recording no pair limits;
  - the fallback (ReverseFallbackFixture, written by the same self-test under rf/): a --base row naming a base-game
    reference whose base no candidate uses makes the --reverse-of run measure the full reverse, and its emit equals a plain
    full reverse.
Each test names the wrong answer it rejects. Probes come from this checkout's Release build unless HC_REVERSE_HARNESS names
another build folder (the sabotage run points it at a build whose refusal checks are short-circuited).
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
PROBES = Path(os.environ.get("HC_REVERSE_HARNESS") or buildlayout.root())
FAILURES, PASSED = [], []
ORDER = ["PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]


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
    def __init__(self, tmp: Path):
        self.tmp = tmp
        keep = tmp / "fixture"
        st = run([exe(PROBES, "overlapprobe"), "--self-test"], env=dict(os.environ, PD_FIXTURE_KEEP=str(keep)))
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
        self.population = tmp / "population.json"
        self.population.write_text(json.dumps({"schema": 1,
                                               "plugins": [{"name": "PpDelta.esp", "path": str(delta),
                                                            "sha256": hashlib.sha256(delta.read_bytes()).hexdigest()}],
                                               "mods": [{"name": "PpMeshMod", "path": str(self.inst / "mods" / "PpMeshMod")}],
                                               "targets": ["PpDelta.esp"]}, indent=1), encoding="utf-8")
        self.runs = {}
        for scope in ("whole", "scoped"):
            pop = ["--population", self.population] if scope == "scoped" else []
            b, c = tmp / f"{scope}-baseline.json", tmp / f"{scope}-candidate.json"
            full, fast = tmp / f"{scope}-candidate.reverse.json", tmp / f"{scope}-candidate.reverse-of.json"
            self.runs[(scope, "baseline")] = self.probe("with", ["--emit", b] + pop)
            self.runs[(scope, "candidate")] = self.probe("without", ["--emit", c, "--base", b] + pop)
            self.runs[(scope, "full")] = self.probe("with", ["--emit", full, "--base", c] + pop)
            self.runs[(scope, "fast")] = self.probe("with", ["--emit", fast, "--base", c, "--reverse-of", b] + pop)
            self.runs[(scope, "paths")] = (b, c, full, fast, pop)

    def probe(self, arm, extra):
        return run([exe(PROBES, "overlapprobe"), "--order", self.cand[arm], "--instance", self.inst, "--game", self.game,
                    "--profile", "P"] + extra)


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="reverse-of-")))
    return LAB


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def same_as_full_reverse(scope):
    """Rejects: a --reverse-of emit that differs from the full reverse's in any field but its timestamp."""
    def t():
        L = lab()
        for k in ("baseline", "candidate", "full", "fast"):
            r = L.runs[(scope, k)]
            assert r.returncode == 0, f"{scope} {k}: exit {r.returncode}: {r.stderr[-600:]} {r.stdout[-400:]}"
        b, c, full, fast, _ = L.runs[(scope, "paths")]
        assert "carried (no sweep run)" in L.runs[(scope, "fast")].stdout, "the --reverse-of run did not take the baseline's rows"
        f, g = load(full), load(fast)
        f.pop("GeneratedUtc"); g.pop("GeneratedUtc")
        assert list(f) == list(g), f"keys {list(f)} vs {list(g)}"
        for k in f:
            assert f[k] == g[k], f"{k} differs: full {json.dumps(f[k])[:500]} vs reverse-of {json.dumps(g[k])[:500]}"
        assert f.get("Predicates"), "the fixture's reverse re-measured no --base row: the case proves nothing"
        assert f.get("Conflicts"), "the fixture's baseline has no row: the case proves nothing"
    return t


def refused(scope, mutate, words, source="baseline"):
    """Rejects: carrying rows from a baseline emit that is not this run's own (the refusal must name what differs)."""
    def t():
        L = lab()
        b, c, full, fast, pop = L.runs[(scope, "paths")]
        doc = load({"baseline": b, "candidate": c}[source])
        mutate(doc)
        bad = L.tmp / f"{scope}-bad-{abs(hash(words)) % 10**8}.json"
        bad.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        out = L.tmp / f"{scope}-refused.json"
        r = L.probe("with", ["--emit", out, "--base", c, "--reverse-of", bad] + pop)
        assert r.returncode == 2, f"exit {r.returncode} (wanted 2, a refusal): {r.stderr[-400:]} {r.stdout[-300:]}"
        assert words in r.stderr, f"the refusal does not name it ({words!r}): {r.stderr[-400:]}"
        assert "Exception" not in r.stderr and "Unhandled" not in r.stderr, r.stderr[-400:]
    return t


def other_order_refused(scope):
    """Rejects: taking the rows of another order's emit (the candidate's own run on the "without" order)."""
    def t():
        L = lab()
        b, c, full, fast, pop = L.runs[(scope, "paths")]
        # the candidate emit carries --base; strip it so the configuration is the check that must refuse
        doc = load(c); doc.pop("Predicates", None); doc.pop("BaseEmit", None)
        bad = L.tmp / f"{scope}-other-order.json"
        bad.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        r = L.probe("with", ["--emit", L.tmp / f"{scope}-o.json", "--base", c, "--reverse-of", bad] + pop)
        assert r.returncode == 2 and "measured on configuration" in r.stderr, f"exit {r.returncode}: {r.stderr[-400:]}"
    return t


def hand_run_other_limits(scope):
    """Rejects: a hand-run --reverse-of under other pair limits than the baseline's, carrying rows measured under those."""
    def t():
        L = lab()
        b, c, full, fast, pop = L.runs[(scope, "paths")]
        r = L.probe("with", ["--emit", L.tmp / f"{scope}-limits.json", "--base", c, "--reverse-of", b, "--pair-seconds", "60"] + pop)
        assert r.returncode == 2 and "measured under pair limits" in r.stderr, f"exit {r.returncode}: {r.stderr[-400:]}"
    return t


def fallback_equals_full_reverse():
    """Rejects: carrying the baseline's base-record count when the --base rows reach a base the candidates did not (the full
    reverse's count includes that base, so a carried count would differ); and a fallback that differs from a full reverse.
    On ReverseFallbackFixture (rf): the --base row V1 x A1, where V1 is base-game-responsible (set aside before its base is
    looked up) and its base RfOther is no candidate's."""
    L = lab()
    rf = L.tmp / "fixture" / "rf"
    inst, game = L.tmp / "rf-instance", L.tmp / "rf-game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    shutil.copytree(rf / "mods" / "Skyrim", inst / "mods" / "Skyrim")
    shutil.copyfile(rf / "Skyrim.esm", inst / "mods" / "Skyrim" / "Skyrim.esm")
    (inst / "mods" / "CrA").mkdir(parents=True)
    shutil.copyfile(rf / "CrA.esp", inst / "mods" / "CrA" / "CrA.esp")
    order = ["Skyrim.esm", "CrA.esp"]
    (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in order), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(n + "\n" for n in order), encoding="utf-8")
    (prof / "modlist.txt").write_text("+CrA\n+Skyrim\n", encoding="utf-8")
    cand = L.tmp / "rf-candidate.json"
    cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "rf", "BaseProfile": "P", "Plugins": order, "Mods": ["CrA", "Skyrim"],
                                "Changes": []}), encoding="utf-8")

    def probe(extra):
        return run([exe(PROBES, "overlapprobe"), "--order", cand, "--instance", inst, "--game", game, "--profile", "P"] + extra)
    b = L.tmp / "rf-baseline.json"
    r0 = probe(["--emit", b])
    assert r0.returncode == 0, f"rf baseline: exit {r0.returncode}: {r0.stderr[-400:]} {r0.stdout[-400:]}"
    base_rows = L.tmp / "rf-base-rows.json"
    base_rows.write_text(json.dumps({"Probe": "fixture", "Instance": "fixture", "GeneratedUtc": "2026-10-19 00:00:00Z", "Conflicts": [
        {"Kind": "OVERLAP", "A": "Skyrim.esm", "B": "CrA.esp", "Summary": "fixture: V1 against A1",
         "Necessary": [{"FormKey": "000901:Skyrim.esm", "Role": "placed-ref", "Label": "fixture"}],
         "Alternative": [{"FormKey": "000901:CrA.esp", "Role": "placed-ref"}]}]}, indent=1), encoding="utf-8")
    plain, viaof = L.tmp / "rf.reverse.json", L.tmp / "rf.reverse-of.json"
    r1 = probe(["--emit", plain, "--base", base_rows])
    r2 = probe(["--emit", viaof, "--base", base_rows, "--reverse-of", b])
    assert r1.returncode == 0 and r2.returncode == 0, f"exits {r1.returncode} {r2.returncode}: {r1.stderr[-300:]} {r2.stderr[-300:]}"
    assert "measuring the full reverse" in r2.stdout, "the --base row reaching RfOther did not make the run measure the full reverse"
    f, g = load(plain), load(viaof)
    f.pop("GeneratedUtc"); g.pop("GeneratedUtc")
    assert list(f) == list(g), f"keys {list(f)} vs {list(g)}"
    for k in f:
        assert f[k] == g[k], f"{k} differs: full {json.dumps(f[k])[:500]} vs fallback {json.dumps(g[k])[:500]}"
    preds = g.get("Predicates") or []
    assert len(preds) == 1 and preds[0]["Physical"] == "Holds", f"the V1 x A1 row re-measured as {preds}: the case proves nothing unless it measured"
    bases = next(c for c in load(b)["Coverage"] if c["Population"].startswith("base records"))
    assert next(c for c in g["Coverage"] if c["Population"].startswith("base records"))["Judged"] == bases["Judged"] + 1,         "the full reverse's base-record count is not the baseline's plus RfOther: the fallback was not needed here"


TIMING = re.compile(r"\b\d+\.\d+ ?s\b")


def timing_in(doc):
    """The coverage notes of an emit that carry a wall-clock figure (seconds), as 'population: note'."""
    return [f"{c['Population']}: {c['Note']}" for c in doc.get("Coverage") or [] if TIMING.search(c.get("Note") or "")]


def no_timing_in_coverage(scope):
    """Rejects: a coverage note that carries a wall-clock time. Two runs of the same order then differ whenever the machine
    is busy (1 of 10 comparisons under 16 spinning processes on 2026-11-27: 'readmission 0.0s' vs '0.1s'), so the
    --reverse-of emit cannot be held equal to the full reverse's. A sabotage arm first shows the check sees such a note."""
    def t():
        planted = {"Coverage": [{"Population": "mesh responsibility", "Note": "7 model path(s); readmission 0.1s, counterfactuals 0.0s"}]}
        assert len(timing_in(planted)) == 1, "sabotage: the check did not see a planted timing note"
        L = lab()
        found = []
        for k in ("baseline", "candidate", "full", "fast"):
            assert L.runs[(scope, k)].returncode == 0, f"{scope} {k}: exit {L.runs[(scope, k)].returncode}"
        for p in L.runs[(scope, "paths")][:4]:
            found += [f"{Path(p).name} {x}" for x in timing_in(load(p))]
        assert not found, f"{len(found)} coverage note(s) carry a wall-clock time: " + " | ".join(found)[:900]
    return t


def fp(doc):
    doc["Configuration"]["Fingerprint"] = "0" * 64


def coverage_line(name):
    def m(doc):
        for c in doc["Coverage"]:
            if c["Population"] == name:
                c["Judged"] += 1
    return m


for scope in ("whole", "scoped"):
    check(f"{scope}: the --reverse-of emit equals the full reverse's in every field but GeneratedUtc", same_as_full_reverse(scope))
    check(f"{scope}: no coverage note in any emit carries a wall-clock time", no_timing_in_coverage(scope))
    check(f"{scope}: a baseline with another fingerprint is refused, naming the configuration", refused(scope, fp, "measured on configuration"))
    check(f"{scope}: another order's emit is refused, naming the configuration", other_order_refused(scope))
    check(f"{scope}: another population is refused", refused(scope, lambda d: d.__setitem__("Population", "flags"), "judged population"))
    check(f"{scope}: another measurement policy is refused", refused(scope, lambda d: d.__setitem__("MeasurementPolicy", "other-policy"), "under policy"))
    check(f"{scope}: another instance is refused", refused(scope, lambda d: d.__setitem__("Instance", "X:/elsewhere"), "measured on instance"))
    check(f"{scope}: an emit that was itself run with --base is refused", refused(scope, lambda d: None, "itself run with --base", source="candidate"))
    check(f"{scope}: a differing candidate-refs coverage line is refused", refused(scope, coverage_line("candidate refs"), "'candidate refs' coverage differs"))
    check(f"{scope}: a baseline recorded under other pair limits is refused", refused(scope, lambda d: d["PairLimits"].__setitem__("PairSeconds", 1.0), "measured under pair limits"))
    check(f"{scope}: a baseline recording no pair limits is refused", refused(scope, lambda d: d.pop("PairLimits"), "does not record the pair limits"))
    check(f"{scope}: a hand-run reverse under another --pair-seconds is refused", hand_run_other_limits(scope))

check("whole: --base rows reaching a base the candidates did not: the full reverse is measured, equal to a plain one", fallback_equals_full_reverse)

print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
sys.exit(1 if FAILURES else 0)
