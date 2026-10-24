"""An arm whose plugins name meshes that only a folder outside its asset view provides is refused (cases 1350-1365).

    python _geometry-harness/tests/test_plan_view_mesh_gaps.py [case-13NN]

A mod variant shipped as a plugin alone reuses the main mod's meshes. An arm whose candidate Mods listed only the
variant's folder loaded the variant's records without those meshes, and nothing said so before the probes measured.
overlapprobe's scene preflight (run by `evalrun plan` on every arm, and by every scene capture) now reads the meshes
the arm's plugins name and refuses when its view lacks some that an installed folder outside it has, naming the arm,
the plugin, the count and the folder, and saying what to try. A mesh no installed folder has is the missing-assets
check's, not this one's; a variant that ships its own meshes passes.

Every case builds a temporary MO2 instance (never a real install), writes its plugins and archives byte by byte, and
drives the REAL overlapprobe and evalrun.py built in this checkout
(`dotnet build _geometry-harness/overlapprobe/overlapprobe.csproj -c Release`). A missing exe is a FAILURE, never a skip.
"""
from __future__ import annotations

import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
REGISTRY = REPO / ".claude" / "skills" / "world-edit-compatibility" / "references" / "check-registry.json"
EXE = HARNESS / "overlapprobe" / "bin" / "Release" / "net9.0" / ("overlapprobe.exe" if os.name == "nt" else "overlapprobe")
CODE = "view-lacks-installed-meshes"

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def clean_env() -> dict:
    env = {k: v for k, v in os.environ.items()
           if k not in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH")}
    env["PYTHONIOENCODING"] = "utf-8"
    return env


# ---- byte-level fixtures -------------------------------------------------------------------------------------------

def sub(sig: bytes, data: bytes) -> bytes:
    return sig + struct.pack("<H", len(data)) + data


def zstr(text: str) -> bytes:
    return text.encode("ascii") + b"\0"


def record(sig: bytes, form_id: int, body: bytes, flags: int = 0) -> bytes:
    return sig + struct.pack("<IIIIHH", len(body), flags, form_id, 0, 44, 0) + body


def plugin(path: Path, models: list[str]) -> None:
    """A plugin with no masters and one STAT per model path (EDID, OBND, MODL), the shape a variant's new bases take."""
    stats = b""
    for i, model in enumerate(models):
        body = sub(b"EDID", zstr(f"FixtureStat{i:02d}")) + sub(b"OBND", struct.pack("<6h", -8, -8, -8, 8, 8, 8)) \
            + sub(b"MODL", zstr(model))
        stats += record(b"STAT", 0x800 + i, body)
    hedr = sub(b"HEDR", struct.pack("<fiI", 1.7, len(models), 0x800 + len(models)))
    out = record(b"TES4", 0, hedr)
    if stats:
        out += b"GRUP" + struct.pack("<I", 24 + len(stats)) + b"STAT" + struct.pack("<iHHI", 0, 0, 0, 0) + stats
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(out)


def bsa(path: Path, files: dict[str, bytes]) -> None:
    """An uncompressed version-105 BSA with folder and file names: the layout src/housecarl-generator/BsaBuilder.cs
    authors for the C# tests, ported here so a test can put a mesh inside an archive."""
    folders: dict[str, list[tuple[str, bytes]]] = {}
    for rel, data in files.items():
        folder, name = rel.replace("/", "\\").rsplit("\\", 1)
        folders.setdefault(folder, []).append((name, data))
    items = list(folders.items())
    fr_size = 24
    total_folder_names = sum(len(f) + 1 for f, _ in items)
    total_file_names = sum(len(n) + 1 for _, fs in items for n, _ in fs)
    file_count = sum(len(fs) for _, fs in items)
    records_end = 36 + len(items) * fr_size
    block_sizes = [1 + len(f) + 1 + len(fs) * 16 for f, fs in items]
    name_block = records_end + sum(block_sizes)
    data_start = name_block + total_file_names
    out = bytearray()
    out += struct.pack("<IIIIIIIII", 0x00415342, 105, 36, 0x3, len(items), file_count,
                       total_folder_names, total_file_names, 0)
    offset = records_end
    for i, (_, fs) in enumerate(items):
        out += struct.pack("<QIIQ", 0, len(fs), 0, offset + total_file_names)
        offset += block_sizes[i]
    cursor = data_start
    blobs = []
    for folder, fs in items:
        fn = folder.encode("ascii")
        out += bytes([len(fn) + 1]) + fn + b"\0"
        for _, data in fs:
            out += struct.pack("<QII", 0, len(data), cursor)
            cursor += len(data)
            blobs.append(data)
    for _, fs in items:
        for name, _ in fs:
            out += name.encode("ascii") + b"\0"
    for b in blobs:
        out += b
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(out))


def mesh(folder: Path, rel: str) -> None:
    p = folder / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(b"fixture nif " + rel.encode("ascii"))


M1 = "meshes/fixture/parentonly01.nif"
M2 = "meshes/fixture/parentonly02.nif"
NOWHERE = "meshes/fixture/nowhere01.nif"


def model_of(rel: str) -> str:
    """The MODL spelling: meshes-relative, backslashes, as a plugin stores it."""
    return rel[len("meshes/"):].replace("/", "\\")


def make_instance(root: Path, modlist: list[str], plugins: list[str]) -> tuple[Path, Path]:
    """Profile P with the given modlist rows ('+Name' or '-Name', strongest first); every named folder exists."""
    inst, game = root / "instance", root / "game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    for row in modlist:
        (inst / "mods" / row[1:]).mkdir(parents=True, exist_ok=True)
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    (prof / "modlist.txt").write_text("".join(r + "\n" for r in modlist), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(p + "\n" for p in plugins), encoding="utf-8")
    (prof / "plugins.txt").write_text("".join("*" + p + "\n" for p in plugins), encoding="utf-8")
    return inst, game


def candidate(folder: Path, stem: str, plugins: list[str], mods) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    doc = {"SchemaVersion": 1, "Name": stem, "BaseProfile": "P", "Plugins": plugins,
           "Changes": ["fixture"], "Provenance": {"why": "fixture"}}
    if mods is not None:
        doc["Mods"] = mods
    p = folder / f"{stem}.json"
    p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return p


def preflight(cand: Path, inst: Path, game: Path, out: Path):
    r = subprocess.run([str(EXE), "--instance", str(inst), "--game", str(game), "--profile", "P",
                        "--order", str(cand), "--scene-preflight", "--json", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=clean_env(), timeout=120)
    doc = json.loads(out.read_text(encoding="utf-8")) if out.exists() else None
    return r, doc or {}


def plan(root: Path, cands: Path, inst: Path, game: Path):
    out = root / "run"
    if out.exists():
        shutil.rmtree(out)
    args = [sys.executable, str(EVALRUN), "plan", "--registry", str(REGISTRY), "--candidates", str(cands),
            "--out", str(out), "--harness", str(HARNESS), "--depth", "standard",
            "--instance", str(inst), "--game", str(game), "--profile", "P"]
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env=clean_env(), timeout=300)
    doc = json.loads((out / "plan.json").read_text(encoding="utf-8")) if (out / "plan.json").exists() else None
    return r, doc


def bare_fixture(root: Path) -> tuple[Path, Path]:
    """The SW14 shape: Variant ships Variant.esp alone; Parent (installed, enabled) has the two meshes it names."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(M1), model_of(M2)])
    mesh(inst / "mods" / "Parent", M1)
    mesh(inst / "mods" / "Parent", M2)
    return inst, game


def passed(doc: dict, r) -> bool:
    return r.returncode == 0 and doc.get("verdict") == "passed"


def refused_here(doc: dict, r) -> bool:
    return r.returncode == 2 and doc.get("verdict") == "refused" and doc.get("code") == CODE


def say(doc: dict, r) -> str:
    return f"(exit {r.returncode}, verdict {doc.get('verdict')}, code {doc.get('code')}, reason {doc.get('reason')!r})"


# ---- cases ---------------------------------------------------------------------------------------------------------

def case_1350(root: Path):
    """Bare variant, parent's meshes loose: refused with the plugin, its folder, the count and the parent folder."""
    inst, game = bare_fixture(root)
    c = candidate(root / "c", "bare", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(refused_here(doc, r), f"1350: refused with code {CODE} {say(doc, r)}")
    reason, remedy = doc.get("reason") or "", doc.get("remedy") or ""
    check("plugin 'Variant.esp' (from 'Variant') names 2 meshes that no folder in the candidate's Mods provides" in reason,
          f"1350: the reason names the plugin, its folder and the count ({reason!r})")
    check("installed folder 'Parent', which the Mods list leaves out, has them" in reason,
          f"1350: the reason names the installed folder that has them ({reason!r})")
    check(remedy == "add 'Parent' at the end of the candidate's Mods list, so the arm's own folders keep priority, then try again",
          f"1350: the remedy names the step ({remedy!r})")
    lines = [ln for ln in r.stderr.splitlines() if "scene asset preflight refused" in ln]
    check(len(lines) == 1 and lines[0].endswith("then try again."), f"1350: stderr says it in one sentence ({lines})")


def case_1351(root: Path):
    """The real shape: the parent ships the meshes inside an archive named for the plugin the variant replaces."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(M1), model_of(M2)])
    bsa(inst / "mods" / "Parent" / "Variant.bsa", {M1: b"nif one", M2: b"nif two"})
    c = candidate(root / "c", "bare", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(refused_here(doc, r), f"1351: refused {say(doc, r)}")
    check("names 2 meshes" in (doc.get("reason") or "") and "installed folder 'Parent'" in (doc.get("reason") or ""),
          f"1351: the archive's two meshes are counted and its folder named ({doc.get('reason')!r})")


def case_1352(root: Path):
    """evalrun plan refuses the arm in one sentence naming the arm, the plugin, the count and the folder; nothing planned."""
    inst, game = bare_fixture(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["Variant.esp"], ["Variant"])
    r, doc = plan(root, c, inst, game)
    check(r.returncode == 2, f"1352: plan refuses (exit {r.returncode})")
    check(doc is None, "1352: no plan.json is written")
    want = ("arm baseline (candidate-00-x-baseline.json): overlapprobe's scene preflight refused it: plugin 'Variant.esp' "
            "(from 'Variant') names 2 meshes that no folder in the candidate's Mods provides, and installed folder "
            "'Parent', which the Mods list leaves out, has them. To fix: add 'Parent' at the end of the candidate's "
            "Mods list, so the arm's own folders keep priority, then try again.")
    hits = [ln for ln in r.stderr.splitlines() if "arm baseline" in ln]
    check(len(hits) == 1 and hits[0].strip().lstrip("- ").strip() == want,
          f"1352: the arm's one sentence ({hits} / stdout {r.stdout.strip()[-200:]!r})")


def case_1353(root: Path):
    """Two variants whose meshes sit in two parents: both folders are named, and adding both passes."""
    inst, game = make_instance(root, ["+VariantA", "+VariantB", "+ParentA", "+ParentB"], ["A.esp", "B.esp"])
    plugin(inst / "mods" / "VariantA" / "A.esp", [model_of(M1)])
    plugin(inst / "mods" / "VariantB" / "B.esp", [model_of(M2)])
    mesh(inst / "mods" / "ParentA", M1)
    mesh(inst / "mods" / "ParentB", M2)
    c = candidate(root / "c", "two", ["A.esp", "B.esp"], ["VariantA", "VariantB"])
    r, doc = preflight(c, inst, game, root / "v.json")
    reason, remedy = doc.get("reason") or "", doc.get("remedy") or ""
    check(refused_here(doc, r), f"1353: refused {say(doc, r)}")
    check("plugins 'A.esp' (from 'VariantA') and 'B.esp' (from 'VariantB') name 2 meshes" in reason
          and "installed folders 'ParentA' and 'ParentB', which the Mods list leaves out, have them" in reason,
          f"1353: both plugins and both folders are named ({reason!r})")
    check(remedy.startswith("add 'ParentA' and 'ParentB' at the end of the candidate's Mods list"),
          f"1353: the remedy adds both ({remedy!r})")
    fixed = candidate(root / "c", "fixed", ["A.esp", "B.esp"], ["VariantA", "VariantB", "ParentA", "ParentB"])
    r2, doc2 = preflight(fixed, inst, game, root / "v2.json")
    check(passed(doc2, r2), f"1353: the remedy's configuration passes {say(doc2, r2)}")


def case_1354(root: Path):
    """Control: a variant that ships its own copies of the meshes passes, though the parent has them too."""
    inst, game = bare_fixture(root)
    mesh(inst / "mods" / "Variant", M1)
    mesh(inst / "mods" / "Variant", M2)
    c = candidate(root / "c", "own", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1354: a variant shipping its meshes passes {say(doc, r)}")


def case_1355(root: Path):
    """Control: a mesh no installed folder provides is the missing-assets check's, not this one's: passes."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(NOWHERE)])
    c = candidate(root / "c", "nowhere", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1355: a mesh nobody provides passes {say(doc, r)}")


def case_1356(root: Path):
    """One mesh only the parent has and one nobody has: refused, counting only the first."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(M1), model_of(NOWHERE)])
    mesh(inst / "mods" / "Parent", M1)
    c = candidate(root / "c", "mixed", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    reason = doc.get("reason") or ""
    check(refused_here(doc, r), f"1356: refused {say(doc, r)}")
    check("names 1 mesh that no folder in the candidate's Mods provides" in reason
          and "installed folder 'Parent', which the Mods list leaves out, has it" in reason,
          f"1356: only the mesh the parent has is counted ({reason!r})")


def case_1357(root: Path):
    """Control: the SW14 fix (the parent in the arm's Mods, after the variant) passes."""
    inst, game = bare_fixture(root)
    c = candidate(root / "c", "fixed", ["Variant.esp"], ["Variant", "Parent"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1357: the parent in Mods passes {say(doc, r)}")


def case_1358(root: Path):
    """Control: an outside copy inside an archive no arm plugin loads is not offered (adding it would not help)."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(M1)])
    bsa(inst / "mods" / "Parent" / "Unrelated.bsa", {M1: b"nif one"})
    c = candidate(root / "c", "unloaded", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1358: an archive the arm would not load is not a provider {say(doc, r)}")


def case_1359(root: Path):
    """Control: a folder under mods/ that the base profile's modlist.txt does not list is not offered."""
    inst, game = make_instance(root, ["+Variant"], ["Variant.esp"])
    plugin(inst / "mods" / "Variant" / "Variant.esp", [model_of(M1)])
    mesh(inst / "mods" / "Unlisted", M1)
    c = candidate(root / "c", "unlisted", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1359: an unlisted folder is not offered {say(doc, r)}")


def case_1360(root: Path):
    """Control: a mesh the game's Data folder provides is in every view: passes though the parent has it too."""
    inst, game = bare_fixture(root)
    mesh(game / "Data", M1)
    mesh(game / "Data", M2)
    c = candidate(root / "c", "data", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    check(passed(doc, r), f"1360: Data provides it {say(doc, r)}")


def case_1361(root: Path):
    """Control at plan level: the fixed arm plans as before, its preflight passed."""
    inst, game = bare_fixture(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["Variant.esp"], ["Variant", "Parent"])
    r, doc = plan(root, c, inst, game)
    pf = ((doc or {}).get("arms") or [{}])[0].get("scenePreflight") or {}
    check(r.returncode == 0 and doc is not None and pf.get("status") == "passed",
          f"1361: the fixed arm plans (exit {r.returncode}, preflight {pf.get('status')}) {r.stderr.strip()[-300:]}")


def case_1362(root: Path):
    """A plugin the walk cannot read is refused by name, not skipped."""
    inst, game = make_instance(root, ["+Variant", "+Parent"], ["Variant.esp"])
    p = inst / "mods" / "Variant" / "Variant.esp"
    p.write_bytes(b"this is not a plugin")
    c = candidate(root / "c", "unreadable", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    reason = doc.get("reason") or ""
    check(r.returncode == 2 and doc.get("code") == "plugin-unreadable", f"1362: refused as unreadable {say(doc, r)}")
    check("plugin 'Variant.esp' (from 'Variant') could not be read" in reason, f"1362: the plugin is named ({reason!r})")


def case_1363(root: Path):
    """An archive in the view that will not read is named in the sentence, which then speaks of readable folders."""
    inst, game = bare_fixture(root)
    (inst / "mods" / "Variant" / "Variant.bsa").write_bytes(b"BSA\0 broken")
    c = candidate(root / "c", "broken", ["Variant.esp"], ["Variant"])
    r, doc = preflight(c, inst, game, root / "v.json")
    reason = doc.get("reason") or ""
    check(refused_here(doc, r), f"1363: refused {say(doc, r)}")
    check("no readable folder in the candidate's Mods provides (archive 'Variant.bsa' in the view could not be read)" in reason,
          f"1363: the unreadable archive is named ({reason!r})")


def case_1364(root: Path):
    """A scene capture on the bare arm refuses with the same sentence before measuring, and writes no manifest."""
    inst, game = bare_fixture(root)
    c = candidate(root / "c", "bare", ["Variant.esp"], ["Variant"])
    manifest = root / "scene.json"
    r = subprocess.run([str(EXE), "--instance", str(inst), "--game", str(game), "--profile", "P", "--order", str(c),
                        "--scene-id", "s1", "--scene-role", "baseline", "--geometry-manifest", str(manifest),
                        "--candidate-plugin", "Variant.esp"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=clean_env(), timeout=120)
    check(r.returncode == 2 and "scene asset preflight refused: plugin 'Variant.esp' (from 'Variant') names 2 meshes" in r.stderr,
          f"1364: the capture refuses with the sentence (exit {r.returncode}) {r.stderr.strip()[-300:]!r}")
    check(not manifest.exists(), "1364: no manifest is written")


def case_1365(root: Path):
    """A plugin whose only copy is in a folder the Mods list leaves out loads its records, not its meshes: refused."""
    inst, game = make_instance(root, ["+High", "-Outside"], ["Other.esp"])
    plugin(inst / "mods" / "Outside" / "Other.esp", [model_of(M1)])
    mesh(inst / "mods" / "Outside", M1)
    c = candidate(root / "c", "outside", ["Other.esp"], ["High"])
    r, doc = preflight(c, inst, game, root / "v.json")
    reason = doc.get("reason") or ""
    check(refused_here(doc, r), f"1365: refused {say(doc, r)}")
    check("plugin 'Other.esp' (from 'Outside', a folder the Mods list leaves out) names 1 mesh" in reason
          and "installed folder 'Outside', which the Mods list leaves out, has it" in reason,
          f"1365: the plugin's own folder is named ({reason!r})")


CASES = {f"case-{n}": globals()[f"case_{n}"] for n in range(1350, 1366)}


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    if not EXE.exists():
        print(f"  FAIL  overlapprobe is not built at {EXE}; build it (dotnet build -c Release). Not a skip.")
        return 1
    tmp = Path(tempfile.mkdtemp(prefix="hc-view-mesh-gaps-"))
    ran = 0
    try:
        for name, fn in CASES.items():
            if want and not name.startswith(want):
                continue
            ran += 1
            print(f"\n=== {name} ===")
            try:
                fn(tmp / name)
            except Exception as ex:
                check(False, f"{name}: raised {type(ex).__name__}: {ex}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if not ran:
        print(f"  FAIL  no case matches {want!r}")
        return 2
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
