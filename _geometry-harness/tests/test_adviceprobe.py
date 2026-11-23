"""Known answers for adviceprobe's neighbour pages (`--with`) and the evaluation's wiring of them.

    python _geometry-harness/tests/test_adviceprobe.py [--installed [HARNESS_DIR]]

A mod page can state a clash with ANOTHER mod ("-Other Mod: the trapdoor is blocked"). When the evaluated mod is the
other mod, that statement sits on the neighbour's page, not on its own. This suite builds adviceprobe from this checkout
into a temporary folder (never an older binary; --installed runs the deployed copy instead), writes a scratch MO2
instance with two hand-written mods (Nexus ids 9001 and 9002) and two hand-written archived pages, and runs the REAL
probe offline with --max-requests 0 against them. It then checks evalrun's two pieces of wiring: the --with list built
from a plan, and the clash rows read back out of a receipt's report.

Every page text here is hand-written; no real page is copied. Needs dotnet. Exits 1 on failure.
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
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
FAILURES: list[str] = []
URL = "https://www.nexusmods.com/skyrimspecialedition/mods/{}"


def check(ok, label: str) -> None:
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def plugin(masters: list[str], tag: str) -> bytes:
    """A minimal TES4 header: enough for the probe to read a master list."""
    def sub(t: bytes, data: bytes) -> bytes:
        return t + struct.pack("<H", len(data)) + data
    body = sub(b"HEDR", struct.pack("<fiI", 1.71, 0, 0x800)) + sub(b"CNAM", tag.encode() + b"\0")
    for m in masters:
        body += sub(b"MAST", m.encode() + b"\0") + sub(b"DATA", struct.pack("<Q", 0))
    return b"TES4" + struct.pack("<IIIHHHH", len(body), 0, 0, 0, 0, 44, 0) + body


def build(tmp: Path) -> Path:
    csproj = HARNESS / "adviceprobe" / "adviceprobe.csproj"
    out = tmp / "bin" / "adviceprobe"
    r = subprocess.run(["dotnet", "build", str(csproj), "-c", "Release", "--nologo", "--disable-build-servers",
                        "--artifacts-path", str(tmp / "artifacts"), "-o", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
    exe = out / ("adviceprobe.exe" if os.name == "nt" else "adviceprobe")
    if r.returncode != 0 or not exe.exists():
        tail = "\n".join((r.stdout + r.stderr).strip().splitlines()[-12:])
        raise SystemExit(f"{tail}\n  FAIL  adviceprobe did not build from {csproj} (exit {r.returncode}); "
                         f"the suite will not test an older binary.")
    print(f"  adviceprobe: built from {csproj}  sha256 {hashlib.sha256((out / 'adviceprobe.dll').read_bytes()).hexdigest()}")
    return exe


def installed(harness: Path) -> Path:
    exe = buildlayout.program_under(harness, "adviceprobe")
    if not exe.exists():
        raise SystemExit(f"  FAIL  no installed adviceprobe at {exe}")
    print(f"  adviceprobe: installed {exe}")
    return exe


#: The neighbour's page (9001): a compatibility section naming the evaluated mod (9002), and one order sentence.
ALPHA_PAGE = ("[b]Compatibility:[/b]\n\n"
              "This mod should be compatible with almost everything. The following mods, however, do have some "
              "minor incompatibilities:\n\n"
              "-Gamma College: Two placed notes will not spawn.\n\n"
              "-Beta - A Shrine Overhaul: Trapdoor entrance to the catacombs is blocked\n\n"
              "____________________\n\nLoad this mod after Beta - A Shrine Overhaul.\n")
BETA_PAGE = "A shrine overhaul. It adds shrines.\n"


def page(mod_id: int, name: str, description: str) -> dict:
    return {"readDate": "2026-11-08", "modId": mod_id,
            "data": {"mod": {"modId": mod_id, "name": name, "description": description,
                             "updatedAt": "2022-04-05T00:00:00Z",
                             "modRequirements": {"nexusRequirements": {"nodes": []}}},
                     "modFiles": [{"fileId": mod_id * 10, "name": name, "version": "1.0", "category": "MAIN",
                                   "date": 1641427200}]}}


def machine(root: Path) -> dict:
    inst = root / "instance"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (inst / "ModOrganizer.ini").write_text("[General]\nselected_profile=@ByteArray(P)\n", encoding="utf-8")
    mods = [("Alpha Halls", "AlphaHalls.esp", 9001), ("Beta Shrine", "Beta.esp", 9002)]
    for folder, esp, mid in mods:
        d = inst / "mods" / folder
        d.mkdir(parents=True)
        (d / esp).write_bytes(plugin([], folder))
        (d / "meta.ini").write_text(f"[General]\nmodid={mid}\nversion=1.0\n\n[installedFiles]\n"
                                    f"1\\modid={mid}\n1\\fileid={mid * 10}\nsize=1\n", encoding="utf-8")
    (prof / "modlist.txt").write_text("".join("+" + f + "\n" for f, _, _ in reversed(mods)), encoding="utf-8")
    (prof / "plugins.txt").write_text("".join("*" + e + "\n" for _, e, _ in mods), encoding="utf-8")
    (prof / "loadorder.txt").write_text("".join(e + "\n" for _, e, _ in mods), encoding="utf-8")
    game = root / "game"
    (game / "Data").mkdir(parents=True)
    pages = root / "pages"
    pages.mkdir()
    (pages / "9001.json").write_text(json.dumps(page(9001, "Alpha Halls Expanded", ALPHA_PAGE)), encoding="utf-8")
    (pages / "9002.json").write_text(json.dumps(page(9002, "Beta - A Shrine Overhaul", BETA_PAGE)), encoding="utf-8")
    return {"instance": inst, "game": game, "pages": pages, "catalog": root / "catalog", "runlog": root / "runlog"}


def run(exe: Path, m: dict, out: Path, extra: list[str], label: str):
    args = ["--mod", "9002", "--page", f"9002={m['pages'] / '9002.json'}", "--no-import", "--offline",
            "--max-requests", "0", "--catalog", str(m["catalog"]), "--out", str(out),
            "--instance", str(m["instance"]), "--profile", "P", "--game", str(m["game"])] + extra
    env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(m["runlog"]))
    print(f"\n--- {label}: adviceprobe {' '.join(args)}")
    r = subprocess.run([str(exe)] + args, env=env, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=300)
    for ln in (r.stdout + r.stderr).splitlines():
        if any(w in ln for w in ("clash", "REFUSED", "unknown option", "neighbour", "advice:", "!")):
            print("    | " + ln)
    print(f"    exit {r.returncode}")
    return r


def load(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except Exception:
        return {}


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-adviceprobe-test-"))
    try:
        if "--installed" in sys.argv:
            i = sys.argv.index("--installed")
            exe = installed(Path(sys.argv[i + 1]) if len(sys.argv) > i + 1 else buildlayout.root())
        else:
            exe = build(tmp)
        m = machine(tmp / "machine")

        # ---- the neighbour's page states a clash with the evaluated mod
        out = tmp / "with"
        r = run(exe, m, out, ["--with", "9001", "--page", f"9001={m['pages'] / '9001.json'}"],
                "--mod 9002 --with 9001 (the neighbour's page names 9002)")
        rep = load(out / "report-9002.json")
        clashes = [c for c in rep.get("clashes") or [] if c.get("namedModId") == 9002]
        check(len(clashes) == 1 and clashes[0].get("pageModId") == 9001 and clashes[0].get("page") == URL.format(9001)
              and clashes[0].get("symptom") == "Trapdoor entrance to the catacombs is blocked",
              f"report-9002.json carries ONE clash naming mod 9002, stated on mod 9001's page, with its symptom "
              f"(clashes: {rep.get('clashes')}; exit {r.returncode})")
        check(r.returncode == 0, f"adviceprobe --with exits 0 (got {r.returncode})")
        rules = (load(out / "rules-9002.json").get("rules") or [])
        check(len(rules) == 1 and rules[0].get("before") == "Beta.esp" and rules[0].get("after") == "AlphaHalls.esp"
              and (rules[0].get("provenance") or {}).get("source") == URL.format(9001),
              f"the neighbour page's order sentence naming both mods enters rules-9002.json with that page as its "
              f"source ({rules})")
        check(not any("Gamma" in json.dumps(c) for c in rep.get("clashes") or []),
              "an item naming a mod that is not known (Gamma College) gives no clash")

        # ---- no --with: no page nobody named is read
        out2 = tmp / "without"
        r2 = run(exe, m, out2, [], "--mod 9002 alone")
        rep2 = load(out2 / "report-9002.json")
        check(r2.returncode == 0 and not (rep2.get("clashes") or [])
              and not any("9001" in str(x) for x in rep2.get("pagesRead") or []),
              f"without --with, no clash row and no read of mod 9001's page (exit {r2.returncode}, "
              f"pagesRead {rep2.get('pagesRead')})")

        # ---- a neighbour page that is not available offline is a stated gap, not a quiet empty answer
        out3 = tmp / "missing"
        r3 = run(exe, m, out3, ["--with", "9003"], "--with 9003 with no archive and no catalogue copy")
        rep3 = load(out3 / "report-9002.json")
        warn = [w for w in rep3.get("warnings") or [] if "9003" in w and "--offline" in w]
        check(r3.returncode == 0 and len(warn) == 1 and not any("9003" in str(x) for x in rep3.get("pagesRead") or []),
              f"an unreadable neighbour page is ONE warning naming mod 9003 and --offline, and is not counted as read "
              f"(exit {r3.returncode}, warnings {rep3.get('warnings')})")

    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print(f"\n{len(FAILURES)} failure(s)" if FAILURES else "\nall checks passed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
