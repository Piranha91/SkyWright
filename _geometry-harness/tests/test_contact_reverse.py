"""Known answers for contactprobe --reverse-of and the sweep emit's measurement policy (tasks 2026-10-20-contactprobe-evaluation-time
and 2026-10-20-contactprobe-sweep-emit-policy).

    python _geometry-harness/tests/test_contact_reverse.py

On PopulationFixture (written by `overlapprobe --self-test` with PD_FIXTURE_KEEP, laid out as an MO2 instance the way
test_reverse_reconcile.py does), with the REAL contactprobe, whole-order and with --population:
  - baseline B on the "with" order; candidate C on the "without" order with --base B;
  - the full reverse: the "with" order with --base C; the --reverse-of reverse: the same, plus --reverse-of B;
  - the two reverse emits must be equal in every field but GeneratedUtc, row for row and in order;
  - a baseline emit that is not this run's own must be refused by name, for each check: another probe, another configuration
    (the other order's emit, and B with its fingerprint changed), another population, another policy (recorded, missing, and a
    run given another --eps), another instance, an emit that was itself run with --base, each coverage line fixed before the
    host loop differing, and an emit recording pair limits;
  - --reverse-of outside a plain default-pass emit run is refused;
  - conflictdiff refuses two contactprobe emits whose policies differ, or where one records none, and accepts equal ones;
  - the same for the --terrain and --empty-space emits (task 2026-10-21-contactprobe-terrain-empty-space-policy): each records
    its own policy, a run under one changed option records another, and either mode's emit is refused against the sweep's.
Each test names the wrong answer it rejects. Probes come from this checkout's Release build unless HC_REVERSE_HARNESS names
another harness folder (the sabotage run points it at a build whose refusal checks are short-circuited).
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
PROBES = Path(os.environ.get("HC_REVERSE_HARNESS") or HARNESS)
FAILURES, PASSED = [], []
ORDER = ["PpBase.esm", "PpA.esp", "PpB.esp", "PpMover.esp", "PpDelta.esp"]
PRE_LOOP_LINES = ["plugins", "moved hosts", "dependent snaps hashed", "winner-arm physical refs"]


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
        return run([exe(PROBES, "contactprobe"), "--order", self.cand[arm], "--instance", self.inst, "--game", self.game,
                    "--profile", "P"] + extra)


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="contact-reverse-of-")))
    return LAB


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def same_as_full_reverse(scope):
    """Rejects: a --reverse-of emit that differs from the full reverse's in any field but its timestamp; a run that judged hosts."""
    def t():
        L = lab()
        for k in ("baseline", "candidate", "full", "fast"):
            r = L.runs[(scope, k)]
            assert r.returncode == 0, f"{scope} {k}: exit {r.returncode}: {r.stderr[-600:]} {r.stdout[-400:]}"
        b, c, full, fast, _ = L.runs[(scope, "paths")]
        out = L.runs[(scope, "fast")].stdout
        assert "carried (no host judged)" in out, "the --reverse-of run did not take the baseline's rows"
        assert "==== 2. RELATIONSHIPS" not in out, "the --reverse-of run judged the hosts anyway"
        f, g = load(full), load(fast)
        f.pop("GeneratedUtc"); g.pop("GeneratedUtc")
        assert list(f) == list(g), f"keys {list(f)} vs {list(g)}"
        for k in f:
            assert f[k] == g[k], f"{k} differs: full {json.dumps(f[k])[:500]} vs reverse-of {json.dumps(g[k])[:500]}"
        assert f.get("Predicates"), "the fixture's reverse re-measured no --base row: the case proves nothing"
        assert f.get("Conflicts"), "the fixture's baseline has no row: the case proves nothing"
        assert f.get("MeasurementPolicy", "").startswith("placement_contact_v2|"), f"no sweep policy recorded: {f.get('MeasurementPolicy')}"
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
        doc = load(c); doc.pop("Predicates", None); doc.pop("BaseEmit", None)
        bad = L.tmp / f"{scope}-other-order.json"
        bad.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        r = L.probe("with", ["--emit", L.tmp / f"{scope}-o.json", "--base", c, "--reverse-of", bad] + pop)
        assert r.returncode == 2 and "measured on configuration" in r.stderr, f"exit {r.returncode}: {r.stderr[-400:]}"
    return t


def hand_run_other_eps(scope):
    """Rejects: a hand-run --reverse-of under another --eps than the baseline's, carrying rows judged at the old eps."""
    def t():
        L = lab()
        b, c, full, fast, pop = L.runs[(scope, "paths")]
        r = L.probe("with", ["--emit", L.tmp / f"{scope}-eps.json", "--base", c, "--reverse-of", b, "--eps", "6"] + pop)
        assert r.returncode == 2 and "under policy" in r.stderr and "eps 6" in r.stderr, f"exit {r.returncode}: {r.stderr[-400:]}"
    return t


def flag_clashes():
    """Rejects: --reverse-of accepted where it would skip output the caller asked for, or without the inputs it needs."""
    L = lab()
    b, c, full, fast, pop = L.runs[("whole", "paths")]
    o = L.tmp / "clash.json"
    for extra, words in ((["--emit", o, "--reverse-of", b], "--reverse-of needs --base"),
                         (["--base", c, "--reverse-of", b], "--reverse-of needs --emit"),
                         (["--emit", o, "--base", c, "--reverse-of", b, "--json", L.tmp / "j.json"], "writes no judged rows"),
                         (["--emit", o, "--base", c, "--reverse-of", b, "--phase-timers", L.tmp / "t.json"], "writes no judged rows"),
                         (["--emit", o, "--base", c, "--reverse-of", b, "--absolute"], "only in the default moved-host pass")):
        r = L.probe("with", extra)
        assert r.returncode == 2 and words in r.stderr, f"{extra[-2:]}: exit {r.returncode}: {r.stderr[-300:]}"


def diff_gate_policy():
    """Rejects: conflictdiff crediting repair across two contactprobe emits measured under different or unknown contact rules."""
    L = lab()
    cd = exe(PROBES, "conflictdiff")
    b, c, full, fast, pop = L.runs[("whole", "paths")]
    same = run([cd, b, c])
    assert same.returncode == 0 and "measurement policy: placement_contact_v2|" in same.stdout, f"equal policies: exit {same.returncode}: {same.stdout[-400:]}"
    for name, mutate in (("other", lambda d: d.__setitem__("MeasurementPolicy", "placement_contact_v0|eps 4")),
                         ("missing", lambda d: d.pop("MeasurementPolicy"))):
        doc = load(c); mutate(doc)
        bad = L.tmp / f"diff-{name}.json"
        bad.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        r = run([cd, b, bad])
        assert r.returncode == 3 and "measurement policies cannot establish repair credit" in r.stdout, f"{name}: exit {r.returncode}: {r.stdout[-400:]}"


TERRAIN_FLAGS = ["--terrain", "--float-threshold", "8", "--burial-threshold", "8", "--burial-share", "0.5"]
MODE_POLICY = {
    "terrain": "terrain_support_v1|eps 4|float 8|burial 8|burial-share 0.5|sample 1|absolute off|support-reach 256|radius on|bos on|force-enable none",
    "empty-space": "empty_space_v2|eps 4|reach 128|min-anchor 64|max-members 256|anchor-report 512|radius on|bos on|force-enable none",
}
# a real run under one changed option per mode, and the words its policy then carries
MODE_OTHER = {"terrain": (["--float-threshold", "16"], "|float 16|"), "empty-space": (["--eps", "6"], "|eps 6|")}


def mode_emit(mode, arm, extra=()):
    """The --terrain or --empty-space emit of one arm, run once and kept."""
    L = lab()
    key = (mode, arm, tuple(map(str, extra)))
    if key not in L.runs:
        tag = f"{mode}-{arm}-{len(L.runs)}"
        out = L.tmp / f"{tag}.emit.json"
        flags = TERRAIN_FLAGS if mode == "terrain" else ["--empty-space", L.tmp / f"{tag}.side.json"]
        r = L.probe(arm, flags + ["--emit", out] + list(extra))
        assert r.returncode == 0, f"{mode} {arm} {list(extra)}: exit {r.returncode}: {r.stderr[-600:]} {r.stdout[-400:]}"
        L.runs[key] = out
    return L.runs[key]


def mode_policy_recorded(mode):
    """Rejects: a --terrain / --empty-space emit with no policy, or one that leaves out an option that changes its rows."""
    def t():
        doc = load(mode_emit(mode, "with"))
        assert doc.get("MeasurementPolicy") == MODE_POLICY[mode], f"recorded {doc.get('MeasurementPolicy')!r}, wanted {MODE_POLICY[mode]!r}"
        extra, words = MODE_OTHER[mode]
        other = load(mode_emit(mode, "with", extra)).get("MeasurementPolicy", "")
        assert words in other and other != MODE_POLICY[mode], f"{extra}: the policy did not change: {other!r}"
    return t


def mode_diff_gate(mode):
    """Rejects: conflictdiff crediting repair across two emits of one mode measured under different or unknown rules, or across modes."""
    def t():
        L = lab()
        cd = exe(PROBES, "conflictdiff")
        a, b = mode_emit(mode, "with"), mode_emit(mode, "without")
        same = run([cd, a, b])
        version = MODE_POLICY[mode].split("|")[0]
        assert same.returncode == 0 and f"measurement policy: {version}|" in same.stdout, f"equal policies: exit {same.returncode}: {same.stdout[-500:]}"
        refusal = "measurement policies cannot establish repair credit"
        # another rule measured for real (one option changed), and an emit written before the policy existed
        r = run([cd, mode_emit(mode, "with", MODE_OTHER[mode][0]), b])
        assert r.returncode == 3 and refusal in r.stdout, f"other option: exit {r.returncode}: {r.stdout[-500:]}"
        doc = load(b); doc.pop("MeasurementPolicy")
        bad = L.tmp / f"{mode}-no-policy.json"
        bad.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        for pair in ((a, bad), (bad, a)):
            r = run([cd, *pair])
            assert r.returncode == 3 and refusal in r.stdout and "'unknown'" in r.stdout, f"missing: exit {r.returncode}: {r.stdout[-500:]}"
        # against the sweep emit, past the population gate: the policy alone refuses it
        sweep = L.runs[("whole", "paths")][0]
        r = run([cd, sweep, a, "--compare-states"])
        assert r.returncode == 3 and refusal in r.stdout, f"against the sweep: exit {r.returncode}: {r.stdout[-500:]}"
    return t


def fp(doc):
    doc["Configuration"]["Fingerprint"] = "0" * 64


def coverage_line(name):
    def m(doc):
        hit = [c for c in doc["Coverage"] if c["Population"] == name]
        assert hit, f"the baseline has no '{name}' line: the case proves nothing"
        hit[0]["Judged"] += 1
    return m


for scope in ("whole", "scoped"):
    check(f"{scope}: the --reverse-of emit equals the full reverse's in every field but GeneratedUtc", same_as_full_reverse(scope))
    check(f"{scope}: another probe's emit is refused", refused(scope, lambda d: d.__setitem__("Probe", "overlapprobe"), "not contactprobe"))
    check(f"{scope}: a baseline with another fingerprint is refused, naming the configuration", refused(scope, fp, "measured on configuration"))
    check(f"{scope}: another order's emit is refused, naming the configuration", other_order_refused(scope))
    check(f"{scope}: another population is refused", refused(scope, lambda d: d.__setitem__("Population", "flags"), "judged population"))
    check(f"{scope}: another measurement policy is refused", refused(scope, lambda d: d.__setitem__("MeasurementPolicy", "other-policy"), "under policy"))
    check(f"{scope}: an emit recording no policy (written before 2026-10-21) is refused", refused(scope, lambda d: d.pop("MeasurementPolicy"), "policy 'unknown'"))
    check(f"{scope}: a hand-run reverse under another --eps is refused", hand_run_other_eps(scope))
    check(f"{scope}: another instance is refused", refused(scope, lambda d: d.__setitem__("Instance", "X:/elsewhere"), "measured on instance"))
    check(f"{scope}: an emit that was itself run with --base is refused", refused(scope, lambda d: None, "itself run with --base", source="candidate"))
    check(f"{scope}: an emit recording pair limits is refused", refused(scope, lambda d: d.__setitem__("PairLimits", {"MaxTrianglePairs": 1, "PairSeconds": 1.0}), "records pair limits"))
    for line in PRE_LOOP_LINES:
        check(f"{scope}: a differing '{line}' coverage line is refused", refused(scope, coverage_line(line), f"'{line}' coverage differs"))

check("--reverse-of outside a plain default-pass emit run is refused by name", flag_clashes)
check("conflictdiff: equal contactprobe policies accepted; another or a missing policy refused", diff_gate_policy)
for mode in ("terrain", "empty-space"):
    check(f"--{mode}: the emit records its policy, and a changed option changes it", mode_policy_recorded(mode))
    check(f"--{mode}: conflictdiff accepts equal policies, refuses another, a missing one and the sweep's", mode_diff_gate(mode))

print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
sys.exit(1 if FAILURES else 0)
