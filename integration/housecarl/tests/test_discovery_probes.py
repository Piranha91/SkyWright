"""Known answers for the discovery probes' machine-readable hit list (`--json`): patchfind, poolprobe, fomodprobe.

    python integration/housecarl/tests/test_discovery_probes.py [--installed [CONFIGURATION]]

The three probes are the houseCARL integration module's (moved from SkyWright's harness with the patch-discovery
rows in step 3 of the boundary; patchfind is patchprobe's discovery modes). Verifying a candidate stays SkyWright's
patchprobe --verify, which this suite also builds, from SkyWright's harness, for its one --json refusal.

--installed runs the same cases against the copies the overlay runs (the module build's probes for CONFIGURATION,
default the one buildlayout picks, and the app build's patchprobe) instead of a fresh build: the deploy smoke, and the check that would have caught a stale deployed
fomodprobe that ignored --json and exited 0 with no hit list. fomodprobe must also refuse an argument it does not know.

Builds the three probes (and patchprobe) from this checkout into a temporary folder (never an older binary), writes a tiny scratch
machine - two MO2 instances, a download pool of two 7-Zip archives, hand-written plugin headers - and runs each REAL
probe against it with HOUSECARL_SEARCH_ROOTS / HOUSECARL_MO2_INSTANCE / HOUSECARL_MO2_PROFILE /
HOUSECARL_DOWNLOAD_POOL / HOUSECARL_SEVENZIP set for that run only, so no real instance or pool is read. Each run
prints that environment and the instances the probe says it found. Asserts every planted hit is listed with the
sha256 of the planted bytes, that a hit with no hash says why, and that the counts are real totals. A second pool
of seven archives checks that poolprobe writes its hits and unjudgeable items in the same documented order at any
--jobs.

Needs dotnet and 7-Zip (HOUSECARL_SEVENZIP, or `sevenZip` in the file HOUSECARL_HARNESS_CONFIG names). A missing tool
is a FAILURE, never a skip. Exits 1 on failure.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  where the module's build put each program, and SkyWright's checkout
import buildlayout  # noqa: E402  where SkyWright's build put patchprobe
PROBES = ("patchfind", "poolprobe", "fomodprobe")
# the module's three probes, and SkyWright's patchprobe (verify-only) for its --json refusal
CSPROJ = {p: SW.MODULE / p / f"{p}.csproj" for p in PROBES} | {"patchprobe": SW.HARNESS / "patchprobe" / "patchprobe.csproj"}
FAILURES: list[str] = []


def check(ok, label: str) -> None:
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def plugin(masters: list[str], tag: str = "") -> bytes:
    """A minimal TES4 header (HEDR, CNAM, MAST/DATA per master): enough for Mutagen to read the masters."""
    def sub(t: bytes, data: bytes) -> bytes:
        return t + struct.pack("<H", len(data)) + data
    body = sub(b"HEDR", struct.pack("<fiI", 1.71, 0, 0x800)) + sub(b"CNAM", (tag or "fixture").encode() + b"\0")
    for m in masters:
        body += sub(b"MAST", m.encode() + b"\0") + sub(b"DATA", struct.pack("<Q", 0))
    return b"TES4" + struct.pack("<IIIHHHH", len(body), 0, 0, 0, 0, 44, 0) + body


def seven_zip() -> str | None:
    env = os.environ.get("HOUSECARL_SEVENZIP", "").strip()
    if env:
        return env
    cfg = os.environ.get("HOUSECARL_HARNESS_CONFIG", "").strip()
    if cfg and Path(cfg).exists():
        try:
            return (json.loads(Path(cfg).read_text(encoding="utf-8")) or {}).get("sevenZip")
        except (OSError, ValueError):
            return None
    return None


def build(tmp: Path) -> dict[str, Path]:
    """Each probe built from this checkout into an empty folder; a failed build raises SystemExit with one sentence."""
    exes = {}
    for p, csproj in CSPROJ.items():
        out = tmp / "bin" / p
        r = subprocess.run(["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
                            "--artifacts-path", str(tmp / "artifacts" / p), "-o", str(out)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
        exe = out / (f"{p}.exe" if os.name == "nt" else p)
        if r.returncode != 0 or not exe.exists():
            tail = "\n".join((r.stdout + r.stderr).strip().splitlines()[-12:])
            raise SystemExit(f"{tail}\n  FAIL  {p} did not build from {csproj} (exit {r.returncode}); "
                             f"the suite will not test an older binary.")
        exes[p] = exe
        print(f"  {p}: built from {csproj}  sha256 {sha((out / f'{p}.dll').read_bytes())}")
    return exes


FOMOD_XML = """<?xml version="1.0" encoding="utf-8"?>
<config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <moduleName>Fixture Hub</moduleName>
  <requiredInstallFiles><file source="core\\Core.esp" destination="Core.esp"/></requiredInstallFiles>
  <installSteps order="Explicit">
    <installStep name="Patches">
      <optionalFileGroups order="Explicit">
        <group name="Patches" type="SelectAny">
          <plugins order="Explicit">
            <plugin name="B patch">
              <description>for B</description>
              <files><file source="opt\\BPatch.esp" destination="BPatch.esp"/></files>
              <typeDescriptor><dependencyType><defaultType name="Optional"/><patterns><pattern>
                <dependencies operator="And"><fileDependency file="B.esp" state="Active"/></dependencies>
                <type name="Recommended"/>
              </pattern></patterns></dependencyType></typeDescriptor>
            </plugin>
          </plugins>
        </group>
      </optionalFileGroups>
    </installStep>
  </installSteps>
</config>
"""


def machine(root: Path, sz: str) -> dict:
    """The scratch machine. Returns the planted bytes by name and the paths the runs need."""
    roots = root / "roots"
    pool = root / "pool"
    pool.mkdir(parents=True)
    planted = {}

    def instance(name, profile, mods, active, modlist):
        inst = roots / name
        (inst / "profiles" / profile).mkdir(parents=True)
        (inst / "ModOrganizer.ini").write_text(f"[General]\nselected_profile=@ByteArray({profile})\n", encoding="utf-8")
        order = []
        for mod, files, meta in mods:
            d = inst / "mods" / mod
            d.mkdir(parents=True)
            for fname, data in files.items():
                (d / fname).write_bytes(data)
                order.append(fname)
            if meta:
                (d / "meta.ini").write_text(f"[General]\ninstallationFile={meta}\n", encoding="utf-8")
        (inst / "profiles" / profile / "plugins.txt").write_text(
            "".join(("*" if n in active else "") + n + "\n" for n in order), encoding="utf-8")
        (inst / "profiles" / profile / "loadorder.txt").write_text("".join(n + "\n" for n in order), encoding="utf-8")
        (inst / "profiles" / profile / "modlist.txt").write_text("".join("+" + m + "\n" for m in modlist),
                                                                   encoding="utf-8")
        return inst

    planted["A.esp"], planted["B.esp"] = plugin([], "A"), plugin([], "B")
    planted["AB Patch.esp"] = plugin(["A.esp", "B.esp"], "installed patch")
    planted["AB Patch2.esp"] = plugin(["A.esp", "B.esp"], "other list patch")
    planted["Core.esp"] = plugin(["B.esp"], "hub core")
    planted["BPatch.esp"] = plugin(["B.esp"], "hub option")
    planted["PoolPatch.esp"] = plugin(["A.esp", "B.esp"], "pool patch")
    target = instance("Target", "P",
                      [("A", {"A.esp": planted["A.esp"]}, None), ("B", {"B.esp": planted["B.esp"]}, None),
                       ("Patch", {"AB Patch.esp": planted["AB Patch.esp"]}, None),
                       ("FomodHub", {"Core.esp": planted["Core.esp"]}, "fomod.7z")],
                      {"A.esp", "B.esp", "Core.esp"}, ["A", "B", "Patch", "FomodHub"])
    other = instance("Other", "Q",
                     [("A", {"A.esp": planted["A.esp"]}, None), ("B", {"B.esp": planted["B.esp"]}, None),
                      ("Patch2", {"AB Patch2.esp": planted["AB Patch2.esp"]}, None)],
                     {"A.esp", "B.esp", "AB Patch2.esp"}, ["A", "B", "Patch2"])

    def archive(name, entries):
        stage = root / "stage" / name
        for rel, data in entries.items():
            (stage / rel).parent.mkdir(parents=True, exist_ok=True)
            (stage / rel).write_bytes(data)
        r = subprocess.run([sz, "a", "-bso0", "-bsp0", str(pool / name)] + sorted({rel.split("/")[0] for rel in entries}),
                           cwd=stage, capture_output=True, text=True)
        if r.returncode != 0:
            raise SystemExit(f"  FAIL  7-Zip could not write {name}: {r.stderr.strip()[:300]}")

    archive("pool-patch.7z", {"PoolPatch.esp": planted["PoolPatch.esp"]})
    (pool / "pool-patch.7z.meta").write_text("[General]\nname=A and B patch\nmodName=A B Patch\n", encoding="utf-8")
    archive("fomod.7z", {"fomod/ModuleConfig.xml": FOMOD_XML.encode("utf-8"), "core/Core.esp": planted["Core.esp"],
                         "opt/BPatch.esp": planted["BPatch.esp"]})
    env = {"HOUSECARL_SEARCH_ROOTS": str(roots), "HOUSECARL_MO2_INSTANCE": str(target), "HOUSECARL_MO2_PROFILE": "P",
           "HOUSECARL_DOWNLOAD_POOL": str(pool), "HOUSECARL_SEVENZIP": sz,
           "HOUSECARL_RUNLOG_DIR": str(root / "runlog")}
    return {"planted": planted, "env": env, "root": root, "roots": roots, "target": target, "other": other}


def run(exe: Path, args: list, m: dict, label: str):
    env = dict(os.environ, **m["env"])
    print(f"\n--- {label}: {exe.name} {' '.join(str(a) for a in args)}")
    for k in ("HOUSECARL_SEARCH_ROOTS", "HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_DOWNLOAD_POOL",
              "HOUSECARL_SEVENZIP"):
        print(f"    env {k}={env[k]}")
    t0 = time.perf_counter()
    r = subprocess.run([str(exe)] + [str(a) for a in args], env=env, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=600)
    secs = time.perf_counter() - t0
    for ln in (r.stdout + r.stderr).splitlines():
        if any(w in ln for w in ("instances found", "instances scanned", "TARGET", "target:", "hash extraction",
                                 "REFUSED", "--json")) or ln.startswith("       "):
            print("    | " + ln)
    print(f"    exit {r.returncode}  wall {secs:.2f}s")
    return r, secs


def load(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None
    except ValueError:
        return None


def hits_named(doc, name):
    return [h for h in ((doc or {}).get("hits") or []) if str(h.get("file", "")).lower() == name.lower()]


def common_shape(doc, probe, label):
    check(isinstance(doc, dict), f"{label}: --json wrote a JSON object")
    doc = doc or {}
    check(doc.get("schemaVersion") == 1 and doc.get("probe") == probe,
          f"{label}: schemaVersion 1 and probe {probe!r} (got {doc.get('schemaVersion')!r}, {doc.get('probe')!r})")
    for h in doc.get("hits") or []:
        if h.get("sha256") is None:
            check(bool(h.get("unhashedReason")), f"{label}: unhashed hit {h.get('file')} says why")
    counts = doc.get("counts") or {}
    check(all(isinstance(counts.get(k), int) for k in ("judged", "unjudgeable", "hits"))
          and counts.get("hits") == len(doc.get("hits") or []),
          f"{label}: counts.judged/unjudgeable/hits are integers and hits equals the list ({counts})")
    return doc


def inside(path, root: Path) -> bool:
    try:
        Path(str(path)).resolve().relative_to(root.resolve())
        return True
    except (ValueError, OSError):
        return False


def installed(config: str | None) -> dict[str, Path]:
    """The copies the overlay runs (each module probe where the module build's marker puts it; patchprobe where the
    app build's does), not a fresh build: a deploy check."""
    exes = {}
    for p in (*PROBES, "patchprobe"):
        exe = SW.program(p, config) if p in PROBES else buildlayout.program_under(buildlayout.root(), p)
        if not exe.exists():
            raise SystemExit(f"  FAIL  no installed {p} at {exe}")
        dll = exe.with_suffix(".dll")
        print(f"  {p}: installed {exe}  sha256 {sha(dll.read_bytes()) if dll.exists() else '?'}")
        exes[p] = exe
    return exes


def pool_order(exe: Path, m: dict, out: Path, sz: str) -> None:
    """poolprobe writes its hits sorted by (archive, entry) ordinal and its unjudgeable items in ordinal order, at any
    --jobs. The pool is built so that no other order passes: the one-master-short hit's archive sorts FIRST (a list
    that puts applicable hits before near misses fails), and inside m-multi.7z the subfolder entry sorts before the
    root entry (an extraction-enumeration order, root files first, fails). Neither depends on thread timing. The
    unjudgeable names and reasons are chosen so that ordinal order is the reverse of NTFS name order and of
    one-worker processing order."""
    pool = m["root"] / "pool-order"
    pool.mkdir()
    p = m["planted"]

    def archive(name, entries):
        stage = m["root"] / "stage-order" / name
        for rel, data in entries.items():
            (stage / rel).parent.mkdir(parents=True, exist_ok=True)
            (stage / rel).write_bytes(data)
        r = subprocess.run([sz, "a", "-bso0", "-bsp0", str(pool / name)] + sorted({rel.split("/")[0] for rel in entries}),
                           cwd=stage, capture_output=True, text=True)
        if r.returncode != 0:
            raise SystemExit(f"  FAIL  7-Zip could not write {name}: {r.stderr.strip()[:300]}")

    archive("a-nearmiss.7z", {"NearMiss.esp": plugin(["A.esp", "C.esp"], "one master short")})
    archive("m-multi.7z", {"zeta.esp": plugin(["A.esp", "B.esp"], "root entry"),
                           "sub/alpha.esp": plugin(["A.esp", "B.esp"], "subfolder entry")})
    archive("pool-patch.7z", {"PoolPatch.esp": p["PoolPatch.esp"]})
    archive("c-empty.7z", {"Empty.esp": b""})
    archive("d-corrupt.7z", {"Corrupt.esp": b"not a plugin header"})
    (pool / "Broken.7z").write_bytes(b"not an archive")
    (pool / "a-broken.7z").write_bytes(b"not an archive either")

    want_hits = [("a-nearmiss.7z", "NearMiss.esp"), ("m-multi.7z", "sub\\alpha.esp"), ("m-multi.7z", "zeta.esp"),
                 ("pool-patch.7z", "PoolPatch.esp")]
    want_unj = [("Broken.7z", "7-Zip could not list the archive"), ("a-broken.7z", "7-Zip could not list the archive"),
                ("1 plugin(s)", "MalformedDataException"), ("1 plugin(s)", "zero-byte file")]
    mo = dict(m, env=dict(m["env"], HOUSECARL_DOWNLOAD_POOL=str(pool)))
    first = None
    for i, jobs in enumerate((1, 2, 8, 8, 8)):
        label = f"poolprobe order, --jobs {jobs} (run {i + 1})"
        j = out / f"pool-order-{i}.json"
        r, _ = run(exe, ["--jobs", jobs, "--json", j], mo, label)
        doc = load(j)
        check(r.returncode == 0 and isinstance(doc, dict),
              f"{label}: exits 0 and writes a JSON object (exit {r.returncode})")
        doc = doc or {}
        got = [(str((h.get("where") or {}).get("archive")), str((h.get("where") or {}).get("entry")).replace("/", "\\"))
               for h in doc.get("hits") or []]
        check(got == want_hits and len(set(got)) == len(got),
              f"{label}: hits in (archive, entry) ordinal order, keys unique\n        got  {got}\n        want {want_hits}")
        unj = [(u.get("what"), u.get("why")) for u in doc.get("unjudgeableItems") or []]
        check(unj == want_unj, f"{label}: unjudgeable items in ordinal order\n        got  {unj}\n        want {want_unj}")
        same = json.dumps([doc.get("hits"), doc.get("unjudgeableItems")], sort_keys=True)
        if first is None:
            first = same
        else:
            check(same == first, f"{label}: hits and unjudgeable items identical to run 1")


def hub_xml(core: str, options: list[tuple[str, list[str]]]) -> str:
    """A FOMOD whose required file is `core` and whose SelectAny options, in this order, each install their plugins (in
    this order) when B.esp is active."""
    plugs = "".join(
        f'<plugin name="{name}"><description>for B</description><files>'
        + "".join(f'<file source="opt\\{f}" destination="{f}"/>' for f in files)
        + '</files><typeDescriptor><dependencyType><defaultType name="Optional"/><patterns><pattern>'
          '<dependencies operator="And"><fileDependency file="B.esp" state="Active"/></dependencies>'
          '<type name="Recommended"/></pattern></patterns></dependencyType></typeDescriptor></plugin>'
        for name, files in options)
    return ('<?xml version="1.0" encoding="utf-8"?><config><moduleName>Order Hub</moduleName>'
            f'<requiredInstallFiles><file source="core\\{core}" destination="{core}"/></requiredInstallFiles>'
            '<installSteps order="Explicit"><installStep name="Patches"><optionalFileGroups order="Explicit">'
            f'<group name="Patches" type="SelectAny"><plugins order="Explicit">{plugs}</plugins></group>'
            '</optionalFileGroups></installStep></installSteps></config>')


def fomod_order(exe: Path, m: dict, out: Path, sz: str) -> None:
    """Cases 1871-1873: fomodprobe writes its hits sorted by (archive, option, entry) ordinal, keeps the ordinal-first mod
    folder when one archive is installed into several, and lists an INACTIVE hit's folders in ordinal order, at any --jobs.
    The fixture is built so that no other order passes, and none depends on thread timing: at --jobs 1 the probe reads the
    mod folders in directory order, which on NTFS ignores case, while ordinal order puts upper case first.
      mod folder   installed from   (directory order: a-home, a-hub, a-hub2, B-home, B-hub; ordinal: B-home, B-hub, a-home, ...)
      a-hub        b-hub.7z         options in installer order "Z patch" (ZPatch.esp), "A patch" (Y2.esp, then X1.esp)
      a-hub2       A-hub.7z         options "Only" (OnlyPatch.esp), "Inactive" (InPatch.esp)
      B-hub        A-hub.7z         the same archive again: its hits are the same hits, reported once, for B-hub
      a-home, B-home                each holds InPatch.esp, not active: the INACTIVE hit's onDiskIn is "B-home, a-home"
    Completion order, installer order, file order and directory order each fail at least one row."""
    root = m["root"] / "fomod-order"
    pool, inst = root / "pool", root / "roots" / "FomodOrder"
    pool.mkdir(parents=True)
    plug = {n: plugin(["B.esp"], n) for n in ("CoreA.esp", "CoreB.esp", "ZPatch.esp", "Y2.esp", "X1.esp", "OnlyPatch.esp",
                                              "InPatch.esp")}
    plug["A.esp"], plug["B.esp"] = m["planted"]["A.esp"], m["planted"]["B.esp"]

    def archive(name, core, options):
        stage = root / "stage" / name
        entries = {"fomod/ModuleConfig.xml": hub_xml(core, options).encode("utf-8"), f"core/{core}": plug[core]}
        for _, files in options:
            for f in files:
                entries[f"opt/{f}"] = plug[f]
        for rel, data in entries.items():
            (stage / rel).parent.mkdir(parents=True, exist_ok=True)
            (stage / rel).write_bytes(data)
        r = subprocess.run([sz, "a", "-bso0", "-bsp0", str(pool / name), "fomod", "core", "opt"], cwd=stage,
                           capture_output=True, text=True)
        if r.returncode != 0:
            raise SystemExit(f"  FAIL  7-Zip could not write {name}: {r.stderr.strip()[:300]}")

    archive("b-hub.7z", "CoreB.esp", [("Z patch", ["ZPatch.esp"]), ("A patch", ["Y2.esp", "X1.esp"])])
    archive("A-hub.7z", "CoreA.esp", [("Only", ["OnlyPatch.esp"]), ("Inactive", ["InPatch.esp"])])
    folders = {"A": ({"A.esp"}, None), "B": ({"B.esp"}, None), "a-hub": ({"CoreB.esp"}, "b-hub.7z"),
               "a-hub2": ({"CoreA.esp"}, "A-hub.7z"), "B-hub": ({"CoreA.esp"}, "A-hub.7z"),
               "a-home": ({"InPatch.esp"}, None), "B-home": ({"InPatch.esp"}, None)}
    (inst / "profiles" / "P").mkdir(parents=True)
    (inst / "ModOrganizer.ini").write_text("[General]\nselected_profile=@ByteArray(P)\n", encoding="utf-8")
    for mod, (files, meta) in folders.items():
        d = inst / "mods" / mod
        d.mkdir(parents=True)
        for f in files:
            (d / f).write_bytes(plug[f])
        if meta:
            (d / "meta.ini").write_text(f"[General]\ninstallationFile={meta}\n", encoding="utf-8")
    order = ["A.esp", "B.esp", "CoreA.esp", "CoreB.esp", "InPatch.esp"]
    active = {"A.esp", "B.esp", "CoreA.esp", "CoreB.esp"}
    prof = inst / "profiles" / "P"
    (prof / "plugins.txt").write_text("".join(("*" if n in active else "") + n + "\n" for n in order), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(n + "\n" for n in order), encoding="utf-8")
    (prof / "modlist.txt").write_text("".join("+" + mod + "\n" for mod in ("A", "B", "a-hub", "a-hub2", "B-hub")),
                                      encoding="utf-8")

    want = [("A-hub.7z", "Inactive", "opt\\InPatch.esp", "B-hub", "INACTIVE"), ("A-hub.7z", "Only", "opt\\OnlyPatch.esp", "B-hub", "ABSENT"),
            ("b-hub.7z", "A patch", "opt\\X1.esp", "a-hub", "ABSENT"), ("b-hub.7z", "A patch", "opt\\Y2.esp", "a-hub", "ABSENT"),
            ("b-hub.7z", "Z patch", "opt\\ZPatch.esp", "a-hub", "ABSENT")]
    mo = dict(m, env=dict(m["env"], HOUSECARL_SEARCH_ROOTS=str(root / "roots"), HOUSECARL_MO2_INSTANCE=str(inst),
                          HOUSECARL_MO2_PROFILE="P", HOUSECARL_DOWNLOAD_POOL=str(pool)))
    first = None
    for i, jobs in enumerate((1, 2, 8, 8)):
        label = f"fomodprobe order, --jobs {jobs} (run {i + 1})"
        j = out / f"fomod-order-{i}.json"
        r, _ = run(exe, ["--jobs", jobs, "--json", j], mo, label)
        doc = load(j)
        check(r.returncode == 0 and isinstance(doc, dict), f"{label}: exits 0 and writes a JSON object (exit {r.returncode})")
        hits = (doc or {}).get("hits") or []
        got = [(str((h.get("where") or {}).get("archive")), str((h.get("detail") or {}).get("option")),
                str((h.get("where") or {}).get("entry")).replace("/", "\\"), str((h.get("detail") or {}).get("mod")),
                str((h.get("detail") or {}).get("verdict"))) for h in hits]
        check([g[:3] for g in got] == [w[:3] for w in want],
              f"{label}: 1871 hits in (archive, option, entry) ordinal order\n        got  {got}\n        want {want}")
        check([g[3] for g in got] == [w[3] for w in want],
              f"{label}: 1872 an archive installed into two folders reports the ordinal-first folder\n        got  {[g[3] for g in got]}")
        where = [str((h.get("detail") or {}).get("onDiskIn")) for h in hits if (h.get("detail") or {}).get("verdict") == "INACTIVE"]
        check(where == ["B-home, a-home"], f"{label}: 1873 the INACTIVE hit lists its folders in ordinal order (got {where})")
        same = json.dumps(hits, sort_keys=True)
        if first is None:
            first = same
        else:
            check(same == first, f"{label}: hits identical to run 1")


def main() -> int:
    sz = seven_zip()
    if not sz or not Path(sz).exists():
        print(f"  FAIL  7-Zip not found ({sz!r}); set HOUSECARL_SEVENZIP")
        return 1
    tmp = Path(tempfile.mkdtemp(prefix="hc-discovery-test-"))
    try:
        if "--installed" in sys.argv:
            i = sys.argv.index("--installed")
            exes = installed(sys.argv[i + 1] if len(sys.argv) > i + 1 else None)
        else:
            exes = build(tmp)
        m = machine(tmp / "machine", sz)
        p = m["planted"]
        out = tmp / "out"
        out.mkdir()

        # ---- patchfind --pair
        run(exes["patchfind"], ["--pair", "A.esp", "B.esp"], m, "patchfind --pair, warm-up (start-up cost, not timed)")
        _, plain = run(exes["patchfind"], ["--pair", "A.esp", "B.esp"], m, "patchfind --pair, no --json (cost baseline)")
        r, secs = run(exes["patchfind"], ["--pair", "A.esp", "B.esp", "--json", out / "patch.json"], m, "patchfind --pair")
        print(f"    patchfind wall with --json {secs:.2f}s, without {plain:.2f}s (one run each, scratch machine)")
        check(r.returncode == 0, f"patchfind --pair exits 0 (got {r.returncode})")
        check("MO2 instances found: 2" in r.stdout, "patchfind found exactly the two scratch instances")
        doc = common_shape(load(out / "patch.json"), "patchfind", "patchfind --pair")
        inst_paths = [s.get("path") for s in doc.get("sourcesSearched") or [] if s.get("kind") == "instance"]
        check(len(inst_paths) == 2 and all(inside(x, m["roots"]) for x in inst_paths),
              f"patchfind searched only scratch instances ({inst_paths})")
        h = hits_named(doc, "AB Patch.esp")
        check(len(h) == 1 and h[0].get("kind") == "installed" and h[0].get("sha256") == sha(p["AB Patch.esp"]),
              f"the installed patch is one hit, kind installed, sha256 of its bytes ({h})")
        check(bool(h) and sorted(x.lower() for x in h[0].get("masters") or []) == ["a.esp", "b.esp"],
              "the installed patch's hit carries its masters")
        h = hits_named(doc, "AB Patch2.esp")
        check(len(h) == 1 and h[0].get("kind") == "other-list" and h[0].get("sha256") == sha(p["AB Patch2.esp"]),
              f"the other list's patch is one hit, kind other-list, sha256 of its bytes ({h})")
        dl = [x for x in doc.get("hits") or [] if x.get("kind") == "download-name"]
        check(len(dl) == 1 and dl[0].get("sha256") is None and "name" in str(dl[0].get("unhashedReason", "")),
              f"the pool archive matched by name is a download-name hit with no hash and a reason ({dl})")
        on_disk = sum(1 for f in m["roots"].glob("*/mods/*/*") if f.suffix.lower() in (".esp", ".esm", ".esl"))
        check((doc.get("counts") or {}).get("judged") == on_disk and (doc.get("counts") or {}).get("unjudgeable") == 0,
              f"patchfind counts.judged is every plugin in both instances' mods ({doc.get('counts')})")
        check(doc.get("complete") is True, "patchfind --pair is complete")
        check("AB Patch.esp" in r.stdout and "[Installed" in r.stdout, "patchfind's printed answer is unchanged")

        # ---- patchprobe --verify with --json: refused, not silently ignored
        cf = out / "conf.json"
        cf.write_text(json.dumps({"Probe": "x", "GeneratedUtc": "2026-10-14T00:00:00Z", "Conflicts": []}), encoding="utf-8")
        r, _ = run(exes["patchprobe"], ["--verify", cf, "--json", out / "verify.json"], m, "patchprobe --verify --json")
        check(r.returncode == 2 and not (out / "verify.json").exists() and "--json" in (r.stdout + r.stderr),
              f"patchprobe refuses --json with --verify in one sentence (exit {r.returncode})")
        r, _ = run(exes["patchfind"], ["--verify", cf], m, "patchfind --verify")
        check(r.returncode == 2 and "patchprobe --verify" in (r.stdout + r.stderr),
              f"patchfind refuses --verify in one sentence naming SkyWright's patchprobe --verify (exit {r.returncode})")

        # ---- no search roots / no download pool on this machine (the workbench's M7h greyed these rows in the app's
        # launcher until they left the app's registry, 9D-2; the module's probes now say it themselves)
        empty_cfg = tmp / "empty-harness.json"
        empty_cfg.write_text("{}", encoding="utf-8")
        for probe, unset, word in (("patchfind", "HOUSECARL_SEARCH_ROOTS", "search roots"),
                                   ("poolprobe", "HOUSECARL_DOWNLOAD_POOL", "download pool")):
            bare = {**m, "env": {**m["env"], unset: "", "HOUSECARL_HARNESS_CONFIG": str(empty_cfg)}}
            r, _ = run(exes[probe], ["--json", out / f"{probe}-unset.json"] + (["--pair", "A.esp", "B.esp"] if probe == "patchfind" else []),
                       bare, f"{probe} with {unset} empty")
            check(r.returncode == 2 and not (out / f"{probe}-unset.json").exists() and word in (r.stdout + r.stderr).lower(),
                  f"M7h: {probe} refuses when this machine names no {word}, saying so (exit {r.returncode})")

        # ---- poolprobe
        run(exes["poolprobe"], [], m, "poolprobe, warm-up (start-up cost, not timed)")
        _, plain = run(exes["poolprobe"], [], m, "poolprobe, no --json (cost baseline)")
        r, secs = run(exes["poolprobe"], ["--json", out / "pool.json"], m, "poolprobe")
        print(f"    poolprobe wall with --json {secs:.2f}s, without {plain:.2f}s (one run each, scratch machine)")
        check(r.returncode == 0, f"poolprobe exits 0 (got {r.returncode})")
        check("2 MO2 instances scanned" in r.stdout, "poolprobe scanned exactly the two scratch instances")
        doc = common_shape(load(out / "pool.json"), "poolprobe", "poolprobe")
        h = hits_named(doc, "PoolPatch.esp")
        check(len(h) == 1 and h[0].get("kind") == "pool-archive" and h[0].get("sha256") == sha(p["PoolPatch.esp"])
              and (h[0].get("where") or {}).get("archive") == "pool-patch.7z",
              f"the never-installed pool patch is one hit with the sha256 of its bytes and its archive ({h})")
        check((doc.get("counts") or {}).get("judged") == 1 and (doc.get("counts") or {}).get("unjudgeable") == 0,
              f"poolprobe judged the one never-installed archive ({doc.get('counts')})")
        check(not hits_named(doc, "BPatch.esp"), "an archive some instance installed is not a pool hit")

        # ---- poolprobe hit order: a second pool, so the checks above keep their one-archive pool
        pool_order(exes["poolprobe"], m, out, sz)

        # ---- fomodprobe
        r, secs = run(exes["fomodprobe"], ["--json", out / "fomod.json"], m, "fomodprobe")
        check(r.returncode == 0, f"fomodprobe exits 0 (got {r.returncode})")
        doc = common_shape(load(out / "fomod.json"), "fomodprobe", "fomodprobe")
        h = hits_named(doc, "BPatch.esp")
        check(len(h) == 1 and h[0].get("kind") == "fomod-option" and h[0].get("sha256") == sha(p["BPatch.esp"])
              and str((h[0].get("where") or {}).get("entry", "")).replace("/", "\\").lower() == "opt\\bpatch.esp",
              f"the unselected option's plugin is one hit with the sha256 of the archive entry ({h})")
        check(bool(h) and "B.esp" in ((h[0].get("detail") or {}).get("conditions") or [])
              and (h[0].get("detail") or {}).get("verdict") == "ABSENT",
              "the option hit carries its condition plugins and its tier")
        cost = (doc.get("detail") or {}).get("hashExtraction") or {}
        check(isinstance(cost.get("plugins"), int) and isinstance(cost.get("seconds"), (int, float)),
              f"fomodprobe reports its added extraction cost ({cost})")
        if cost.get("plugins"):
            print(f"    fomodprobe added extraction: {cost['plugins']} plugin(s), {cost['seconds']:.3f}s, "
                  f"{cost['seconds'] / cost['plugins']:.3f}s per hit plugin (whole run {secs:.2f}s)")
        check((doc.get("counts") or {}).get("judged") == 1, f"fomodprobe judged the one parsed FOMOD ({doc.get('counts')})")

        # an argument the probe does not know is refused, never dropped (a stale build once ignored --json and exited 0)
        r, _ = run(exes["fomodprobe"], ["--no-such-flag", "x"], m, "fomodprobe with an unknown argument")
        check(r.returncode == 2 and "--no-such-flag" in (r.stdout + r.stderr),
              f"fomodprobe refuses an unknown argument with exit 2 and names it (exit {r.returncode})")

        # ---- fomodprobe hit order: its own instance and pool, so the checks above keep theirs
        fomod_order(exes["fomodprobe"], m, out, sz)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
