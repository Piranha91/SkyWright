"""contactprobe's anchor, surface, volume and motion outputs name the solid rule their results depend on (cases 1874-1879, 1886-1887).

    python _geometry-harness/tests/test_contact_solid_rule.py

The anchor body test (MeshAnchor) and the swept region (SweptGeometry, MemberMotion) validate solids through
MeshIntersection.PrepareVolumeSolid, so its rule (MeshIntersection.PolicyVersion) reaches their results while the policy
strings beside them (MeshAnchor.PolicyVersion; SweptGeometry.PolicyVersion|NifMotion.PolicyVersion) do not change with it.
A reader compares those strings by equality, so a stale output went unnoticed. Each output therefore names the rule in a
field of its own, as motionprobe sweep and lightprobe do (tests/test_solid_rule_named.py):
  --anchor            solidPolicy at the top: the rule when at least one anchor was tested, else "none".
  a motion object     solidPolicy beside its policy, in --anchor, --surface and --volume rows: the rule when the motion was
                      measured (a swept region was built, or a member decided), else "none" (a refusal).
A field that is absent is an output written before this change; "none" is a run that applied no solid rule.

The expected strings are read from src/housecarl-core, never from a probe, so a probe built from another core, or one that
writes a fixed string, fails here.

Fixtures: `stateprobe --write-modelless-light-fixture` laid out as an MO2 instance (the wall WL at the origin, half 70 u;
the lantern light LL at (80, 0, 0)), and `proofprobe --write-motion-fixture` for the meshes: mod folder SrCubes replaces
ml\\wall.nif and ml\\lantern.nif with closed lit cubes; SrMover puts the sliding box (mover.nif) at ml\\wall.nif. One --order
candidate per mesh set.

    1874  --anchor, LL against the cube wall: tested; solidPolicy is the source's rule, policy unchanged.
    1875  --anchor, every pair refused before a test: solidPolicy "none" (rejects naming a rule that was not applied).
    1876  --anchor --motion swept, LL against the sliding wall: each result's motion names the rule beside its policy.
    1877  --surface --motion swept, the sliding wall against LL: the row's motion names the rule beside its policy.
    1878  --surface --motion pose:NOSUCH, the sliding wall: the pose is refused; its motion names "none".
    1879  --volume --motion swept, the sliding wall against LL: the row's motion names the rule beside its policy.
    1886  --surface, the cube wall against the lantern cube: a row measured, solidPolicy at the top is the rule; with every
          pair refused, "none"; policy unchanged.
    1887  --volume, the same two runs: a member read as a solid, the rule; every pair refused, "none"; policy unchanged.

Probes under test come from this checkout's Release builds unless HC_CS_HARNESS names another build folder (a base build,
for the red run); the fixture writers (proofprobe, stateprobe) come from HC_CS_FIXTURES, else this checkout.
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
CORE = HARNESS.parent / "external" / "houseCARL" / "src" / "housecarl-core"
PROBES = Path(os.environ.get("HC_CS_HARNESS") or buildlayout.root())
FIXTURES = Path(os.environ.get("HC_CS_FIXTURES") or buildlayout.root())
FAILURES, PASSED = [], []

ORDER = ["MlRoom.esm", "MlLights.esp"]
WL, LL = "000902:MlRoom.esm", "000902:MlLights.esp"


def const(name, file, key="PolicyVersion"):
    m = re.findall(r'const\s+string\s+' + key + r'\s*=\s*"([^"]+)"', (CORE / file).read_text(encoding="utf-8"))
    if len(m) != 1:
        raise RuntimeError(f"{file}: expected one {key} constant, found {len(m)}")
    return m[0]


RULE = const("rule", "MeshIntersection.cs")
ANCHOR = const("anchor", "MeshAnchor.cs")
MOTION = const("swept", "SweptGeometry.cs") + "|" + const("nif", "NifMotion.cs")
SURFACE = const("surface", "MeshSurface.cs")
VOLUME = const("volume", "MeshVolume.cs")


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


def run(argv, env=None, **kw):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=900, env=env, **kw)


def named(doc, key, want, where):
    """The rejected answers: the key absent (an output from before this change), or naming another rule (stale)."""
    assert key in doc, f"{where}: no {key} field (keys: {sorted(doc)})"
    assert doc[key] == want, f"{where}: {key} is {doc[key]!r}, expected {want!r}"


class Lab:
    """The MO2 instance and the three candidates. Shared with test_swept_oom_sentence.py."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        self.logs = {}
        self.loose = tmp / "loose"
        w = run([exe(FIXTURES, "proofprobe"), "--write-motion-fixture", self.loose])
        if w.returncode != 0 or not (self.loose / "meshes" / "srfix" / "mover.nif").exists():
            raise RuntimeError(f"the motion fixture was not written (exit {w.returncode}): {w.stdout[-400:]} {w.stderr[-400:]}")
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
        srfix = self.loose / "meshes" / "srfix"
        for mod, files in {"SrCubes": {"lantern.nif": "lantern.nif", "wall.nif": "wall.nif"},
                           "SrMover": {"wall.nif": "mover.nif"}}.items():
            d = self.inst / "mods" / mod / "meshes" / "ml"
            d.mkdir(parents=True)
            for dst, src in files.items():
                shutil.copyfile(srfix / src, d / dst)
        mods = [Path(n).stem for n in reversed(ORDER)]
        (prof / "plugins.txt").write_text("".join("*" + n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "loadorder.txt").write_text("".join(n + "\n" for n in ORDER), encoding="utf-8")
        (prof / "modlist.txt").write_text("".join("+" + m + "\n" for m in ["SrCubes"] + mods), encoding="utf-8")

        def cand(name, mod_list):
            p = tmp / f"candidate-{name}.json"
            p.write_text(json.dumps({"SchemaVersion": 1, "Name": name, "BaseProfile": "P", "Plugins": ORDER, "Mods": mod_list,
                                     "Changes": []}), encoding="utf-8")
            return p
        # the first mod in a candidate's list wins a file conflict (MO2's modlist.txt order: top is highest priority)
        self.cand = {"cubes": cand("cubes", ["SrCubes"] + mods), "mover": cand("mover", ["SrMover", "SrCubes"] + mods)}

    def pairs(self, name, rows):
        p = self.tmp / f"pairs-{name}.json"
        p.write_text(json.dumps({"pairs": [{"a": a, "b": b} for a, b in rows]}), encoding="utf-8")
        return p

    def contact(self, label, cand, pairs, extra, env=None):
        """One contactprobe --overlap-json run; returns (process, {flag: doc or None})."""
        outs = {}
        argv = [exe(PROBES, "contactprobe"), "--order", self.cand[cand], "--instance", self.inst, "--game", self.game,
                "--profile", "P", "--overlap-json", pairs, "--json", self.tmp / f"{label}-pairs-out.json"]
        for flag in extra:
            if flag in ("--anchor", "--surface", "--volume"):
                outs[flag] = self.tmp / f"{label}{flag.replace('--', '-')}.json"
                argv += [flag, outs[flag]]
            else:
                argv.append(flag)
        p = run(argv, env=dict(os.environ, HOUSECARL_RUNLOG_DIR=str(self.tmp / "runlog"), **(env or {})))
        self.logs[label] = p
        docs = {}
        for flag, path in outs.items():
            docs[flag] = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
        return p, docs


def motions(doc):
    """Every motion object in a review document, wherever the review nests it."""
    found = []

    def walk(o):
        if isinstance(o, dict):
            if isinstance(o.get("motion"), dict):
                found.append(o["motion"])
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(doc)
    return found


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))
        good = L.pairs("good", [(LL, WL)])
        refused = L.pairs("refused", [("not-a-key", WL), (LL, "000999:MlRoom.esm")])
        mover = L.pairs("mover", [(WL, LL)])
        runs = {
            "anchor": L.contact("anchor", "cubes", good, ["--anchor"]),
            "anchor-refused": L.contact("anchor-refused", "cubes", refused, ["--anchor"]),
            "anchor-motion": L.contact("anchor-motion", "mover", good, ["--anchor", "--motion", "swept"]),
            "surface-motion": L.contact("surface-motion", "mover", mover, ["--surface", "--motion", "swept"]),
            "surface-badpose": L.contact("surface-badpose", "mover", mover, ["--surface", "--motion", "pose:NOSUCH"]),
            "volume-motion": L.contact("volume-motion", "mover", mover, ["--volume", "--motion", "swept"]),
            "static": L.contact("static", "cubes", mover, ["--surface", "--volume"]),
            "static-refused": L.contact("static-refused", "cubes", refused, ["--surface", "--volume"]),
        }

        def doc(label, flag):
            p, docs = runs[label]
            d = docs.get(flag)
            assert d is not None, f"{label}: contactprobe wrote no {flag} file (exit {p.returncode}): {(p.stdout + p.stderr)[-800:]}"
            return d

        def anchors(d):
            return [a for r in d.get("records") or [] for a in r.get("anchors") or []]

        def c1874():
            d = doc("anchor", "--anchor")
            assert d.get("decided", 0) + d.get("undecided", 0) >= 1, f"no anchor was tested, so this case cannot show the applied rule: {d.get('records')}"
            named(d, "solidPolicy", RULE, "an --anchor run that tested an anchor")
            assert d.get("policy") == ANCHOR, f"policy changed: {d.get('policy')!r}"

        def c1875():
            d = doc("anchor-refused", "--anchor")
            assert d.get("decided") == 0 and d.get("undecided") == 0 and d.get("refused") == 2, \
                f"expected two refused pairs and no test: {d.get('decided')}/{d.get('undecided')}/{d.get('refused')}"
            named(d, "solidPolicy", "none", "an --anchor run in which every pair was refused")

        def measured_motions(label, flag, want):
            d = doc(label, flag)
            ms = motions(d)
            assert ms, f"{label}: no motion object in the {flag} output, so the fixture did not reach the swept path: {json.dumps(d)[:1500]}"
            for m in ms:
                named(m, "solidPolicy", want, f"{label}: motion {m.get('mode')} {m.get('decision') or m.get('refusal')}")
            return d, ms

        def c1876():
            d, ms = measured_motions("anchor-motion", "--anchor", RULE)
            assert all(not m.get("refusal") for m in ms), f"a motion refused, so this is not the applied case: {ms}"
            named(d, "solidPolicy", RULE, "an --anchor --motion swept run that tested an anchor")

        def c1877():
            _, ms = measured_motions("surface-motion", "--surface", RULE)
            assert all(not m.get("refusal") for m in ms), f"a motion refused, so this is not the applied case: {ms}"
            assert all(m.get("policy") == MOTION for m in ms), f"motion policy changed: {[m.get('policy') for m in ms]}"

        def c1878():
            _, ms = measured_motions("surface-badpose", "--surface", "none")
            assert all(m.get("refusal") for m in ms), f"the pose that does not exist was not refused: {ms}"

        def c1879():
            _, ms = measured_motions("volume-motion", "--volume", RULE)
            assert all(not m.get("refusal") for m in ms), f"a motion refused, so this is not the applied case: {ms}"

        def top(flag, policy, counter):
            d = doc("static", flag)
            assert d.get(counter, 0) >= 1, f"static {flag}: nothing measured, so this case cannot show the applied rule: {json.dumps(d)[:1200]}"
            named(d, "solidPolicy", RULE, f"a {flag} run with a measured row")
            assert d.get("policy") == policy, f"{flag} policy changed: {d.get('policy')!r}"
            r = doc("static-refused", flag)
            assert r.get(counter, 0) == 0, f"static-refused {flag}: something was measured: {json.dumps(r)[:800]}"
            named(r, "solidPolicy", "none", f"a {flag} run in which every pair was refused")

        def c1886():
            top("--surface", SURFACE, "measured")

        def c1887():
            top("--volume", VOLUME, "measured")

        for name, fn in [("1874 --anchor with a tested anchor: solidPolicy names the rule; policy unchanged", c1874),
                         ("1875 --anchor with every pair refused: solidPolicy none", c1875),
                         ("1876 --anchor --motion swept: each motion names the rule", c1876),
                         ("1877 --surface --motion swept: the motion names the rule; policy unchanged", c1877),
                         ("1878 --surface --motion pose:NOSUCH, a refused motion: solidPolicy none", c1878),
                         ("1879 --volume --motion swept: the motion names the rule", c1879),
                         ("1886 --surface: solidPolicy names the rule when a row was measured, none when all refused", c1886),
                         ("1887 --volume: solidPolicy names the rule when a solid was read, none when all refused", c1887)]:
            check(name, fn)
        if os.environ.get("HC_CS_DUMP"):
            d = Path(os.environ["HC_CS_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in Path(td).glob("*.json"):
                shutil.copyfile(f, d / f.name)
            for k, p in L.logs.items():
                (d / f"{k}.log").write_text(f"exit {p.returncode}\n--- stdout\n{p.stdout}\n--- stderr\n{p.stderr}", encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
