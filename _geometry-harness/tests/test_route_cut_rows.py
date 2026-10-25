"""blockprobe writes its route-cut listing as a rows file the report can read (cases 1439-1442).

    python _geometry-harness/tests/test_route_cut_rows.py

When the route-cut check reaches no verdict, blockprobe's per-reference listing still names the objects it rated as blocking
or likely to block a route, with the passage left beside each. That listing was printed text only, capped at --list x 3
rows, so the report had nothing to read. `--rows FILE` writes every listed route-cut row, uncapped, as JSON.

The fixture is _shared/RouteCutFixture.cs (five corridors, one box each), written by `blockprobe --write-route-cut-fixture`
and laid out as an MO2 instance; the REAL blockprobe runs on it. The expected rows are the probe's own printed listing of
the same run, parsed here, so the file is checked against what the probe already says:

    1439  --rows writes one row per listed route-cut reference (2 Blocked, 2 LikelyBlocked, 1 NoLine), each with the
          listing's verdict, cell, form key and passage.
    1440  --list 1 caps the printed listing at 3 rows; the rows file still holds all 5.
    1441  a LikelyBlocked row carries the passage share the probe states ("about half the time"); a Blocked row carries
          its passage under the 48 u boundary and no share; the NoLine row carries no passage.
    1442  control: without --rows the probe writes no rows file and lists the same 5 rows.

Each test names the wrong answer it rejects. The probe comes from this checkout's Release build unless HC_RC_HARNESS names
another harness folder.
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
PROBES = Path(os.environ.get("HC_RC_HARNESS") or HARNESS)
FAILURES, PASSED = [], []
LISTING = "---- Blocked / LikelyBlocked / NoLine / Unjudgeable references (route-cut) ----"
ROW = re.compile(r"^\s+(Blocked|LikelyBlocked|NoLine|Unjudgeable)\s+was \S+\s+touched\s+\d+ passage\s+(≥?\s*\d+|-)\s.*?\)\s+(\S+)\s+.*?"
                 r"([0-9A-F]{6}:\S+\.es[mpl])")


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


def exe(probe):
    return PROBES / probe / "bin" / "Release" / "net9.0" / (probe + ".exe")


def run(argv, env=None):
    return subprocess.run([str(a) for a in argv], capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=600, env=env)


def listing(text):
    """The route-cut listing's rows: (verdict, passage or None, cell, form key)."""
    if LISTING not in text:
        return []
    out = []
    for line in text.split(LISTING, 1)[1].splitlines()[1:]:
        if not line.strip():
            break
        m = ROW.match(line)
        if not m:
            break
        p = m.group(2).replace("≥", "").strip()
        out.append((m.group(1), None if p == "-" else int(p), m.group(3), m.group(4)))
    return out


class Lab:
    def __init__(self, tmp: Path):
        fx = tmp / "fx"
        w = run([exe("blockprobe"), "--write-route-cut-fixture", fx])
        rc = fx / "rc"
        if w.returncode != 0 or not (rc / "RcMaster.esm").exists():
            raise RuntimeError(f"the fixture was not written (exit {w.returncode}): {w.stdout[-600:]} {w.stderr[-600:]}")
        inst, game = tmp / "instance", tmp / "game"
        (inst / "profiles" / "P").mkdir(parents=True)
        (inst / "overwrite").mkdir()
        (game / "Data").mkdir(parents=True)
        shutil.copytree(rc / "mods" / "RcMaster", inst / "mods" / "RcMaster")
        shutil.copyfile(rc / "RcMaster.esm", inst / "mods" / "RcMaster" / "RcMaster.esm")
        (inst / "profiles" / "P" / "plugins.txt").write_text("*RcMaster.esm\n", encoding="utf-8")
        (inst / "profiles" / "P" / "loadorder.txt").write_text("RcMaster.esm\n", encoding="utf-8")
        (inst / "profiles" / "P" / "modlist.txt").write_text("+RcMaster\n", encoding="utf-8")
        self.env = dict(os.environ, HOUSECARL_MO2_INSTANCE=str(inst), HOUSECARL_MO2_PROFILE="P",
                        HOUSECARL_GAME_PATH=str(game), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"))
        self.tmp = tmp
        self.plain = run([exe("blockprobe"), "RcMaster.esm"], self.env)
        if self.plain.returncode != 0:
            raise RuntimeError(f"blockprobe exit {self.plain.returncode}: {self.plain.stdout[-1500:]} {self.plain.stderr[-800:]}")
        self.rows_path = tmp / "rows.json"
        self.with_rows = run([exe("blockprobe"), "RcMaster.esm", "--rows", self.rows_path], self.env)
        self.capped_path = tmp / "rows-capped.json"
        self.capped = run([exe("blockprobe"), "RcMaster.esm", "--list", "1", "--rows", self.capped_path], self.env)

    def rows(self, path, proc):
        assert proc.returncode == 0, f"blockprobe --rows exit {proc.returncode}: {proc.stderr.strip()[-400:]}"
        assert path.exists(), f"blockprobe --rows exit 0 but wrote no rows file at {path}"
        return json.loads(path.read_text(encoding="utf-8"))


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        L = Lab(Path(td))
        want = listing(L.plain.stdout)

        def c1439():
            # Rejects: no rows file, a file missing a listed row, or rows that disagree with the probe's own listing.
            assert sorted(v for v, *_ in want) == ["Blocked", "Blocked", "LikelyBlocked", "LikelyBlocked", "NoLine"], want
            doc = L.rows(L.rows_path, L.with_rows)
            got = sorted((r["Verdict"], r.get("Passage"), r["Cell"], r["FormKey"]) for r in doc["Rows"])
            exp = sorted((v, p, c, f) for v, p, c, f in want)
            assert got == exp, f"rows file {got} != listing {exp}"

        def c1440():
            # Rejects: the rows file inheriting the printed listing's --list cap.
            doc = L.rows(L.capped_path, L.capped)
            capped = listing(L.capped.stdout)
            assert len(capped) == 3, f"--list 1 should print 3 route-cut rows, printed {len(capped)}"
            assert len(doc["Rows"]) == 5, f"the rows file holds {len(doc['Rows'])} rows, want all 5"

        def c1441():
            # Rejects: a share invented for a Blocked row, or a LikelyBlocked row without the probe's stated share.
            doc = L.rows(L.rows_path, L.with_rows)
            by = {}
            for r in doc["Rows"]:
                by.setdefault(r["Verdict"], []).append(r)
            for r in by.get("LikelyBlocked", []):
                assert r.get("PassageShare") == "about half the time", r
                assert r["Passage"] >= 48, r
            for r in by.get("Blocked", []):
                assert r.get("PassageShare") is None and r["Passage"] < 48, r
            for r in by.get("NoLine", []):
                assert r.get("Passage") is None and r.get("PassageShare") is None, r

        def c1442():
            assert len(want) == 5, f"the plain run lists {len(want)} route-cut rows, want 5"
            assert not any(p.name.startswith("rows") for p in L.tmp.iterdir() if p.is_file() and p.suffix == ".json"
                           and p not in (L.rows_path, L.capped_path)), "a rows file appeared without --rows"

        for name, fn in [("1439 --rows writes every listed route-cut row as the listing states it", c1439),
                         ("1440 the rows file is not capped by --list", c1440),
                         ("1441 the passage share is the probe's, on LikelyBlocked rows only", c1441),
                         ("1442 control: without --rows, the listing as today and no file", c1442)]:
            check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
