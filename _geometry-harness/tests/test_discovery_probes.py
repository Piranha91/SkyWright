"""Known answers for the discovery probes' machine-readable hit list (`--json`): patchprobe, poolprobe, fomodprobe.

    python _geometry-harness/tests/test_discovery_probes.py

Builds the three probes from this checkout into a temporary folder (never an older binary), writes a tiny scratch
machine - two MO2 instances, a download pool of two 7-Zip archives, hand-written plugin headers - and runs each REAL
probe against it with HOUSECARL_SEARCH_ROOTS / HOUSECARL_MO2_INSTANCE / HOUSECARL_MO2_PROFILE /
HOUSECARL_DOWNLOAD_POOL / HOUSECARL_SEVENZIP set for that run only, so no real instance or pool is read. Each run
prints that environment and the instances the probe says it found. Asserts every planted hit is listed with the
sha256 of the planted bytes, that a hit with no hash says why, and that the counts are real totals.

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
HARNESS = HERE.parent
PROBES = ("patchprobe", "poolprobe", "fomodprobe")
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
    for p in PROBES:
        csproj = HARNESS / p / f"{p}.csproj"
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


def main() -> int:
    sz = seven_zip()
    if not sz or not Path(sz).exists():
        print(f"  FAIL  7-Zip not found ({sz!r}); set HOUSECARL_SEVENZIP")
        return 1
    tmp = Path(tempfile.mkdtemp(prefix="hc-discovery-test-"))
    try:
        exes = build(tmp)
        m = machine(tmp / "machine", sz)
        p = m["planted"]
        out = tmp / "out"
        out.mkdir()

        # ---- patchprobe --pair
        r, _ = run(exes["patchprobe"], ["--pair", "A.esp", "B.esp", "--json", out / "patch.json"], m, "patchprobe --pair")
        check(r.returncode == 0, f"patchprobe --pair exits 0 (got {r.returncode})")
        check("MO2 instances found: 2" in r.stdout, "patchprobe found exactly the two scratch instances")
        doc = common_shape(load(out / "patch.json"), "patchprobe", "patchprobe --pair")
        inst_paths = [s.get("path") for s in doc.get("sourcesSearched") or [] if s.get("kind") == "instance"]
        check(len(inst_paths) == 2 and all(inside(x, m["roots"]) for x in inst_paths),
              f"patchprobe searched only scratch instances ({inst_paths})")
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
              f"patchprobe counts.judged is every plugin in both instances' mods ({doc.get('counts')})")
        check(doc.get("complete") is True, "patchprobe --pair is complete")
        check("AB Patch.esp" in r.stdout and "[Installed" in r.stdout, "patchprobe's printed answer is unchanged")

        # ---- patchprobe --verify with --json: refused, not silently ignored
        cf = out / "conf.json"
        cf.write_text(json.dumps({"Probe": "x", "GeneratedUtc": "2026-10-14T00:00:00Z", "Conflicts": []}), encoding="utf-8")
        r, _ = run(exes["patchprobe"], ["--verify", cf, "--json", out / "verify.json"], m, "patchprobe --verify --json")
        check(r.returncode == 2 and not (out / "verify.json").exists() and "--json" in (r.stdout + r.stderr),
              f"patchprobe refuses --json with --verify in one sentence (exit {r.returncode})")

        # ---- poolprobe
        r, _ = run(exes["poolprobe"], ["--json", out / "pool.json"], m, "poolprobe")
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
