"""Write sweep.json: the known answers for `runcontrol ordersweep`, computed by ordersweep.py.

Every expected value comes from the real module: row_identity, emit_rows, emit_predicates, read_identity, parse_diff,
created_breakdown, classify, memory_route, candidate_files, program_key, the Cache (lookup and record), REFUSED_RE, the
argument parser main() builds, and the request keys of one real sweep over the test fixture's fake probes. Run it again
only when one of those rules changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_sweep.py

Nothing outside a temporary folder is read or written except the harness sources (for the refusal strings), the test
fixture's fake probes and the output.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import hashlib
import io
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parents[1]
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run   # noqa: E402
import ordersweep as O   # noqa: E402

OUT = HERE / "sweep.json"
BACK, FWD, DOUBLED = "<ROOT\\>", "<ROOT/>", "<ROOT\\\\>"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def mask(obj, root: Path):
    """The same value with the temporary root replaced by a placeholder: as written, with forward slashes, and with
    every backslash doubled (how repr() shows it)."""
    if isinstance(obj, str):
        doubled = str(root).replace("\\", "\\\\")
        return obj.replace(doubled, DOUBLED).replace(str(root), BACK).replace(str(root).replace("\\", "/"), FWD)
    if isinstance(obj, dict):
        return {mask(k, root): mask(v, root) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [mask(v, root) for v in obj]
    return obj


def pairs(d: dict) -> list:
    """A dict as [key, value] rows, so its order is part of the answer."""
    return [[k, v] for k, v in d.items()]


def write(root: Path, rel: str, data: bytes | str) -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data.encode("utf-8") if isinstance(data, str) else data)
    return p


# ------------------------------------------------------------------------------------------ row identity

def row_identity_cases() -> list:
    rows = []
    for args in [
        ("NAV-EDGE", "000801:Target.esp", None, "Target.esp", None),
        ("Nav-Edge", "000801:TARGET.ESP", None, "TARGET.esp", 3),
        ("OVERLAP", "000802:B.esp", "000801:A.esp", "B.esp", None),
        ("OVERLAP", "000801:A.esp", "000802:B.esp", "B.esp", None),
        ("OVERLAP", "000801:a.esp", "000801:A.esp", "x", None),
        ("EDGE-IDX", "000802:B.esp", "000801:A.esp", "B.esp", 17),
        ("EDGE-IDX", "000801:A.esp", "000802:B.esp", "B.esp", 0),
        ("CONTACT", "00AB01:\u00c4rger.esp", "00ab01:\u00e4rger.esp", "\u00c9.esp", None),
        ("CONTACT", "Z", "a", "q", None),
        ("CONTACT", "_", "A", "q", None),
    ]:
        rows.append({"args": list(args), "want": O.row_identity(*args)})
    return rows


# ---------------------------------------------------------------------------------------------- the diff

DIFF_NEUTRAL = """$ conflictdiff.exe C:\\fixture\\navownprobe\\baseline.json C:\\fixture\\navownprobe\\candidate-02-repair.json
(exit 0, 0.2s)

base    : C:\\fixture\\navownprobe\\baseline.json  (1 conflicts, 1 rows; predicates on 1)
changed : C:\\fixture\\navownprobe\\candidate-02-repair.json  (1 conflicts, 1 rows; base-row predicates carried: 1, measured against baseline.json)
population: base load-time / state baseline | changed load-time / state baseline
   configuration (base)   : ca002c555987 (4 plugins, 3 providers, load-time/baseline, candidate:candidate-00-baseline.json)
   configuration (changed): bfd09d203e4a (4 plugins, 3 providers, load-time/baseline, candidate:candidate-02-repair.json)
   configuration delta    : this is what the differential below is a difference OF \u2014
      - plugin order changed: 1 pair(s) flipped, first FixtureOther.esp / FixtureThird.esp
      - plugin content changed: FixtureThird.esp
not indented: the block ends here
      - not part of the delta


   GONE    NAV-EDGE   FixtureTarget.esp  x  FixtureBase.esm   -1 record(s): repaired 1
   NEW     NAV-EDGE   FixtureThird.esp  x  FixtureOther.esp   +1 record(s): created 1

   repaired     : 1 record(s)   (the physical predicate went Holds -> Gone)
   reclassified : 0 record(s)   (left the class still holding 0; left with no physical defect to begin with 0; entered reading Gone 0)
   created      : 1 record(s)   (entered the class with the predicate holding)
   unmeasured   : 0 record(s)   (a predicate the credit rests on could not be measured \u2014 counted apart)
   unchanged    : 0 record(s)   (predicate flips beside: Holds -> Gone 0, Gone -> Holds 0)

   verdict : DIFFERENTIAL-NEUTRAL  (repaired 1 - created 1 = net 0; reclassified 0, unmeasured 0)
"""

DIFF_UNJUDGED = """   configuration delta    : none \u2014 the two runs consumed the same plugins, in the same order, over the same providers and overlays.

   ! the base emit carries no physical predicates (written before 2026-09-03?) \u2014 every vanished row reads UNMEASURED
   repaired     : 0 record(s)
   reclassified : 12,345 record(s)
   created      : 1,234,567 record(s)
   unmeasured   : 7 record(s)
   unchanged    : 0 record(s)
   ! REFUSED: the unjudgeable share rose from 0.00% to 40.00%
      ! REFUSED: a second refusal, indented   \t
   !! IDENTICAL CONFIGURATIONS PRODUCED DIFFERENT ROWS.

   verdict : DIFFERENTIAL-UNJUDGED   [POPULATION/STATE DIFFERENCE \u2014 not repair credit]  (repaired 0 - created 1 = net -1)
   verdict : DIFFERENTIAL-VERIFIED  (a second verdict line is not read)
"""

DIFF_EDGES = [
    ("empty", ""),
    ("no verdict", "conflictdiff: could not read the base emit\n"),
    ("verdict without spaces", "verdict:DIFFERENTIAL-HARMFUL123"),
    ("lower-case verdict word is not one", "verdict : differential-neutral"),
    ("a count repeated keeps its first place and last value", "created : 1\nrepaired : 2\ncreated : 3\n"),
    ("a count not at a line start", "x repaired : 5\n  \t repaired:6 trailing\nunchanged :\n"),
    ("counts after a carriage return alone are one line to the pattern", "repaired : 1\rcreated : 2\r\nunmeasured : 3"),
    ("line separators splitlines knows", "a ! REFUSED one\x0bCONFIGURATION DELTA here\x0c  first\u2028  second\u2029\x1cthird\x85 ! REFUSED two"),
    ("the delta block ends at a blank line", "Configuration Delta:\n  a\n   \n  b\n"),
    ("the delta block ends at a line with no leading space", "configuration delta\n\ttabbed is not a space\n  c\n"),
    ("two delta blocks", "configuration delta\n  one\n\nconfiguration delta\n  two\n  configuration delta inside restarts\n  three"),
    ("unicode digits in a count", "repaired : \u0663\u0664\n"),
]


def parse_diff_cases() -> list:
    rows = [{"name": "a neutral diff with a delta block", "text": DIFF_NEUTRAL, "want": O.parse_diff(DIFF_NEUTRAL)},
            {"name": "an unjudged diff with refusals", "text": DIFF_UNJUDGED, "want": O.parse_diff(DIFF_UNJUDGED)}]
    for name, text in DIFF_EDGES:
        rows.append({"name": name, "text": text, "want": O.parse_diff(text)})
    return rows


# ---------------------------------------------------------------------------------------- classification

def row(**kw) -> dict:
    r = {"probe": "p", "candidate": "c", "probeExit": 0, "probeRefused": False, "illegal": False,
         "diff": {"verdict": "DIFFERENTIAL-INERT", "counts": {}, "refusals": [], "delta": []},
         "created": {"created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}}
    for k, v in kw.items():
        if k in ("verdict", "counts", "refusals"):
            r["diff"][k] = v
        elif k in ("newDefect", "unmeasuredCreated"):
            r["created"]["unmeasured" if k == "unmeasuredCreated" else k] = v
        else:
            r[k] = v
    return r


def classify_cases() -> list:
    cases = [
        ("no rows", []),
        ("one clean row", [row()]),
        ("every verdict that completes", [row(verdict=v) for v in ("DIFFERENTIAL-VERIFIED", "DIFFERENTIAL-NEUTRAL", "DIFFERENTIAL-HARMFUL", "DIFFERENTIAL-INERT")]),
        ("illegal outranks a new defect", [row(newDefect=3), row(illegal=True)]),
        ("one new defect", [row(), row(newDefect=1)]),
        ("no new defect at zero", [row(newDefect=0)]),
        ("a new defect outranks a refusal", [row(probeRefused=True), row(newDefect=1)]),
        ("probe exit non-zero", [row(probeExit=4)]),
        ("probe exit -1", [row(probeExit=-1)]),
        ("probe exit not set", [row(probeExit=None)]),
        ("probe refused", [row(probeRefused=True)]),
        ("emit not written", [row(emitNotWritten=True)]),
        ("emit not written, false", [row(emitNotWritten=False)]),
        ("verdict unjudged", [row(verdict="DIFFERENTIAL-UNJUDGED")]),
        ("no verdict line", [row(verdict="(no verdict line)")]),
        ("diff not run", [row(verdict="(not run)")]),
        ("a diff refusal", [row(refusals=["! REFUSED: x"])]),
        ("one unmeasured count", [row(counts={"unmeasured": 1})]),
        ("unmeasured count zero", [row(counts={"unmeasured": 0, "created": 9, "repaired": 2})]),
        ("one created row unmeasured", [row(unmeasuredCreated=1)]),
        ("a reverse that did not write its emit does not block by itself", [row(reverseEmitNotWritten=True, reverseExit=0)]),
        ("a diff exit code is not read", [row(diffExit=3)]),
        ("the second row decides", [row(), row(), row(counts={"unmeasured": 2})]),
    ]
    return [{"name": n, "rows": rows, "want": O.classify(rows)} for n, rows in cases]


# -------------------------------------------------------------------------------------------- emit reading

def conflict(kind, a, b, rows, alt=None):
    c = {"Kind": kind, "A": a, "B": b, "Summary": "fixture", "Necessary": [{"FormKey": fk, **({"Physical": ph} if ph is not ... else {})} for fk, ph in rows]}
    if alt is not None:
        c["Alternative"] = alt
    return c


CFG = {"SchemaVersion": 1, "Fingerprint": "ca002c555987aaaabbbbccccddddeeeeffff0000111122223333444455556666",
       "Plugins": [{"Name": "A.esm", "Path": "C:/fixture/plugins/A.esm", "Sha256": "aa" * 32, "Bytes": 3},
                   {"Name": "B.esp", "Path": "C:/fixture/plugins/B.esp", "Sha256": "bb" * 32, "Bytes": 4}],
       "Providers": [{"Name": "overwrite", "Kind": "overwrite"}],
       "Overlays": [{"Kind": "bos", "Path": "C:/fixture/x_SWAP.ini", "Sha256": "cc" * 32}],
       "Population": "load-time", "State": "baseline", "Limits": ["a limit sentence"]}

EMITS = {
    "base": {"Probe": "navownprobe", "Conflicts": [
        conflict("NAV-EDGE", "Target.esp", "Base.esm", [("000801:Target.esp", "Holds"), ("000802:Target.esp", "Gone")]),
        conflict("OVERLAP", "Other.esp", "Base.esm", [("000810:Other.esp", "Holds")], alt=[{"FormKey": "000811:Base.esm"}]),
    ], "Configuration": CFG},
    "candidate": {"Probe": "navownprobe", "Conflicts": [
        conflict("NAV-EDGE", "Target.esp", "Base.esm", [("000801:Target.esp", "Holds")]),
        conflict("NAV-EDGE", "Other.esp", "Target.esp", [("000820:Other.esp", "Holds"), ("000821:Other.esp", "Holds"), ("000822:Other.esp", "Unmeasured"),
                                                         ("000823:Other.esp", None), ("000824:Other.esp", ...), ("000825:Other.esp", "Holds"),
                                                         ("000826:Other.esp", "Holds"), ("000827:Other.esp", "Holds"), ("000828:Other.esp", "Holds"),
                                                         ("000829:Other.esp", "Holds"), ("00082A:Other.esp", "Holds"), ("00082B:Other.esp", "Holds")]),
        # the same identity twice: the first row's value is kept
        conflict("nav-edge", "OTHER.ESP", "Target.esp", [("000820:OTHER.esp", "Gone")]),
        conflict("EDGE-IDX", "Other.esp", "Base.esm", [("000830:Other.esp", "Holds")], alt=[{"FormKey": "000831:Base.esm", "Triangle": 12}, {"FormKey": "ignored"}]),
        conflict("OVERLAP", "Other.esp", "Base.esm", [("000811:Base.esm", "Holds")], alt=[{"FormKey": "000810:Other.esp"}]),
        conflict("CONTACT", "Other.esp", "Base.esm", [], alt=[]),
    ], "Predicates": [
        {"Kind": "NAV-EDGE", "A": "Target.esp", "B": "Base.esm", "FormKey": "000801:Target.esp", "Other": None, "Physical": "Holds"},
        {"Kind": "NAV-EDGE", "A": "Target.esp", "B": "Base.esm", "FormKey": "000802:Target.esp", "Physical": "Gone"},
        {"Kind": "OVERLAP", "A": "Other.esp", "B": "Base.esm", "FormKey": "000810:Other.esp", "Other": "000811:Base.esm"},
        {"Kind": "EDGE-IDX", "A": "x", "B": "y", "FormKey": "000830:Other.esp", "Other": "000831:Base.esm", "OtherTriangle": 12, "Physical": None},
        {"Kind": "nav-edge", "A": "TARGET.esp", "B": "Base.esm", "FormKey": "000801:TARGET.esp", "Physical": "Gone"},
    ], "configuration": dict(CFG, Fingerprint="lower-case key")},
    "reverse": {"Probe": "navownprobe", "Conflicts": [], "Predicates": [
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000820:Other.esp", "Physical": "Gone"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000821:Other.esp", "Physical": "Holds"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000825:Other.esp", "Physical": "Unmeasured"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000826:Other.esp", "Physical": "Gone"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000827:Other.esp", "Physical": "Gone"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000828:Other.esp", "Physical": "Gone"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "000829:Other.esp", "Physical": "Gone"},
        {"Kind": "NAV-EDGE", "A": "Other.esp", "B": "Target.esp", "FormKey": "00082A:Other.esp", "Physical": "Gone"},
        {"Kind": "EDGE-IDX", "A": "x", "B": "y", "FormKey": "000830:Other.esp", "Other": "000831:Base.esm", "OtherTriangle": 12, "Physical": "Gone"},
    ]},
    "bare": {"Probe": "x"},
    "empty configuration": {"Configuration": {}, "Conflicts": [], "Predicates": None},
    "configuration without lists": {"Configuration": {"Fingerprint": "f00d"}},
}


def strip_absent(doc):
    """`...` marks a Physical that is left out of the row."""
    if isinstance(doc, dict):
        return {k: strip_absent(v) for k, v in doc.items() if v is not ...}
    if isinstance(doc, list):
        return [strip_absent(v) for v in doc]
    return doc


def emit_cases(tmp: Path) -> tuple[list, list]:
    texts = {name: json.dumps(strip_absent(doc), indent=1) for name, doc in EMITS.items()}
    texts["not json"] = "{not json"
    texts["a list"] = "[]"
    texts["empty object"] = "{}"
    emits = []
    for name, text in texts.items():
        p = write(tmp, f"emits/{name}.json", text)
        emits.append({"name": name, "text": text, "rows": pairs(O.emit_rows(p)), "predicates": pairs(O.emit_predicates(p)),
                      "identity": O.read_identity(p)})
    absent = tmp / "emits" / "absent.json"
    emits.append({"name": "absent", "text": None, "rows": pairs(O.emit_rows(absent)), "predicates": pairs(O.emit_predicates(absent)),
                  "identity": O.read_identity(absent)})
    created = []
    for name, base, cand, rev in [("with a reverse", "base", "candidate", "reverse"), ("no reverse given", "base", "candidate", None),
                                  ("the reverse file is absent", "base", "candidate", "absent"), ("a reverse with no predicates", "base", "candidate", "bare"),
                                  ("nothing created", "candidate", "base", "reverse"), ("an unreadable candidate", "base", "not json", "reverse"),
                                  ("an unreadable base", "not json", "candidate", "reverse")]:
        want = O.created_breakdown(tmp / "emits" / f"{base}.json", tmp / "emits" / f"{cand}.json",
                                   None if rev is None else tmp / "emits" / f"{rev}.json")
        created.append({"name": name, "base": base, "candidate": cand, "reverse": rev, "want": want})
    return emits, created


# ------------------------------------------------------------------------------------------ memory route

def memory_cases(tmp: Path) -> list:
    rows = []
    for name, side in [
        ("a lean run under contention", json.dumps({"route": "lean", "case": "contended", "unattendedDefault": True, "outcome": "completed",
                                                   "warning": "memory: the fast route needs 10.4 GB and 5.0 GB is free; closing python (pid 4242, 26.0 GB) frees it"})),
        ("no sidecar", None),
        ("an unreadable sidecar", "{not json"),
        ("an empty route reads not reported", json.dumps({"route": "", "case": None, "extra": 1})),
        ("a sidecar with nothing in it", "{}"),
        ("a sidecar with a byte-order mark", "\ufeff{}"),
    ]:
        emit = write(tmp, f"memory/{len(rows)}.json", "{}")
        if side is not None:
            write(tmp, f"memory/{len(rows)}.json.memory.json", side)
        got = O.memory_route(emit)
        if "sidecarUnreadable" in got:
            got["sidecarUnreadable"] = "<the runtime's own words>"
        rows.append({"name": name, "emit": f"memory/{len(rows)}.json", "sidecar": side, "want": mask(got, tmp)})
    rows.append({"name": "no emit", "emit": None, "sidecar": None, "want": O.memory_route(None)})
    return rows


# --------------------------------------------------------------------------------------- candidate files

def candidate_cases(tmp: Path) -> list:
    rows = []
    for name, files, baseline, limit in [
        ("one baseline, path order", ["candidate-10.json", "candidate-02-b.json", "Candidate-01-A.json", "candidate-00-baseline.json", "candidates.json",
                                      "candidate-03.JSON", "candidate-.json", "notes.txt", "candidate-04.json.bak"], None, None),
        ("a limit", ["candidate-00-baseline.json", "candidate-01.json", "candidate-02.json", "candidate-03.json"], None, 2),
        ("a limit of zero", ["candidate-00-baseline.json", "candidate-01.json"], None, 0),
        ("a limit above the count", ["candidate-00-baseline.json", "candidate-01.json"], None, 9),
        ("a negative limit counts from the end", ["candidate-00-baseline.json", "candidate-01.json", "candidate-02.json", "candidate-03.json"], None, -1),
        ("a negative limit past the start", ["candidate-00-baseline.json", "candidate-01.json"], None, -5),
        ("a baseline named, other case", ["candidate-00-baseline.json", "candidate-01.json", "candidate-02-baseline.json"], "CANDIDATE-01.JSON", None),
        ("a baseline named that is not in the folder", ["candidate-00-baseline.json", "candidate-01.json"], "elsewhere.json", None),
        ("two baselines", ["candidate-00-baseline.json", "candidate-01-baseline.json"], None, None),
        ("no baseline", ["candidate-01.json", "candidate-02.json"], None, None),
        ("no candidate files", ["candidates.json", "other.json"], None, None),
        ("the stem must end in baseline", ["candidate-00-baseline.old.json", "candidate-01-xbaseline.json", "candidate-02.json"], None, None),
    ]:
        d = tmp / "cands" / str(len(rows))
        for f in files:
            write(d, f, "{}")
        given = None if baseline is None else (d / baseline if baseline != "elsewhere.json" else tmp / baseline)
        try:
            base, others = O.candidate_files(d, given, limit)
            want = {"baseline": mask(str(base), tmp), "others": [mask(str(p), tmp) for p in others], "stems": [p.stem for p in others]}
        except SystemExit as e:
            want = {"exit": mask(str(e), tmp)}
        rows.append({"name": name, "folder": f"cands/{len(rows)}", "files": files, "baseline": None if given is None else mask(str(given), tmp),
                     "limit": limit, "want": want})
    d = tmp / "cands" / "absent"
    try:
        O.candidate_files(d, None, None)
    except SystemExit as e:
        rows.append({"name": "the folder is absent", "folder": "cands/absent", "files": [], "baseline": None, "limit": None, "want": {"exit": mask(str(e), tmp)}})
    return rows


# -------------------------------------------------------------------------------------------- program key

KEY_BASE = {"fakeprobe.exe": b"apphost", "fakeprobe.dll": b"probe code v1", "housecarl-core.dll": b"core v1",
            "fakeprobe.runtimeconfig.json": b"{}", "runtimes/win/lib/net9.0/native.dll": b"native v1"}
KEY_VARIANTS = [
    ("the build", {}, True),
    ("the same build in another folder", {}, True),
    ("a different probe dll", {"fakeprobe.dll": b"probe code v2"}, False),
    ("a different housecarl-core.dll", {"housecarl-core.dll": b"core v2"}, False),
    ("a different dll under runtimes/", {"runtimes/win/lib/net9.0/native.dll": b"native v2"}, False),
    ("an added dll", {"extra.dll": b"new dependency"}, False),
    ("debug symbols", {"fakeprobe.pdb": b"symbols 1"}, True),
    ("other debug symbols", {"fakeprobe.pdb": b"symbols 2", "Other.PDB": b"x"}, True),
    ("files a run writes in a subfolder", {"shots/view-fp.png": b"a picture", "extract-123/x.nif": b"scratch"}, True),
    ("a copied data file", {"load-settle-exemptions.json": b"[1]"}, False),
    ("another copied data file", {"load-settle-exemptions.json": b"[2]"}, False),
]


def program_key_cases(tmp: Path) -> dict:
    rows = []
    for i, (name, extra, same) in enumerate(KEY_VARIANTS):
        files = dict(KEY_BASE, **extra)
        h = tmp / "keys" / str(i)
        for rel, data in files.items():
            write(h / "fakeprobe" / "bin" / "Release" / "net9.0", rel, data)
        rows.append({"name": name, "files": [{"path": rel, "bytes": b64(data)} for rel, data in files.items()],
                     "sameAsTheBuild": same, "want": O.program_key(h, "fakeprobe", {})})
    assert all((r["want"] == rows[0]["want"]) == r["sameAsTheBuild"] for r in rows), "program_key no longer separates the builds as the tests say"
    text = "no-such-program-on-path-7f3a fake.py --flag"
    return {"folders": rows, "unverifiable": {"command": text, "want": O.program_key(Path("."), "fakeprobe", {"fakeprobe": text})}}


# ---------------------------------------------------------------------------------------------- the cache

def cache_cases(tmp: Path) -> dict:
    """One tree: this launch's folder `launch` (its own cache), two earlier launches `sweep/inv-0001` and `inv-0002`, the
    plugins their emits name. Each lookup is a fresh Cache over the same files; what it printed and logged is the answer."""
    root = tmp / "cache"
    plugins = {n: write(root, f"plugins/{n}", f"plugin bytes {n}") for n in ("A.esm", "B.esp", "Changed.esp")}
    overlay = write(root, "plugins/x_SWAP.ini", "overlay bytes")
    changed_overlay = write(root, "plugins/y_SWAP.ini", "overlay bytes before")

    def identity(names=("A.esm", "B.esp"), overlays=(overlay,), **kw):
        d = {"fingerprint": "ca002c555987aaaabbbbccccdddd", "plugins": [{"path": str(plugins[n]), "sha256": sha(plugins[n].read_bytes())} for n in names],
             "overlays": [{"path": str(o), "sha256": sha(o.read_bytes()).upper()} for o in overlays], "population": "load-time", "state": "baseline", "limits": []}
        d.update(kw)
        return d

    def entry(folder: str, rel: str, text="{}", **kw):
        emit = write(root / folder, rel, text)
        e = {"cacheVersion": 2, "emit": rel, "emitSha256": sha(emit.read_bytes()), "label": "recorded-label", "recordedUtc": "2026-11-01T00:00:00Z",
             "identity": identity()}
        e.update(kw)
        return e

    inv1, inv2, own = "sweep/inv-0001", "sweep/inv-0002", "launch"
    gone_plugin = dict(identity(), plugins=[{"path": str(root / "plugins" / "Gone.esp"), "sha256": "00" * 32}])
    caches = {
        own: {
            "k-own": entry(own, "p/own.json"),
            "k-own-bad-then-earlier": entry(own, "p/own-bad.json", cacheVersion=1),
        },
        inv2: {
            "k-second": entry(inv2, "p/second.json"),
            "k-both": entry(inv2, "p/both.json", '{"from": "the newer launch"}'),
            "k-own-bad-then-earlier": entry(inv2, "p/own-bad.json"),
            "k-first-bad-then-second": entry(inv2, "p/fb.json", identity=None),
        },
        inv1: {
            "k-both": entry(inv1, "p/both.json", '{"from": "the older launch"}'),
            "k-first-bad-then-second": entry(inv1, "p/fb.json"),
            "k-old-version": entry(inv1, "p/a.json", cacheVersion=1),
            "k-version-as-float": entry(inv1, "p/a.json", cacheVersion=2.0),
            "k-version-true": entry(inv1, "p/a.json", cacheVersion=True),
            "k-no-hash": entry(inv1, "p/a.json", emitSha256=""),
            "k-no-emit-name": entry(inv1, "p/a.json", emit=""),
            "k-emit-null": entry(inv1, "p/a.json", emit=None),
            "k-absolute": entry(inv1, "p/a.json", emit=str(root / inv1 / "p" / "a.json")),
            "k-rooted": entry(inv1, "p/a.json", emit="/p/a.json"),
            "k-drive-relative": entry(inv1, "p/a.json", emit="C:p/a.json"),
            "k-unc": entry(inv1, "p/a.json", emit="\\\\server\\share\\a.json"),
            "k-dotdot": entry(inv1, "p/a.json", emit="p/../../inv-0002/p/second.json"),
            "k-backslash-dotdot": entry(inv1, "p/a.json", emit="p\\..\\..\\inv-0002\\p\\second.json"),
            "k-dot-and-slashes": entry(inv1, "p/a.json", emit="./p//a.json"),
            "k-backslash-inside": entry(inv1, "p/a.json", emit="p\\a.json"),
            "k-quote-in-name": entry(inv1, "p/a.json", emit="../it's.json"),
            "k-gone": entry(inv1, "p/a.json", emit="p/gone.json"),
            "k-emit-is-a-folder": entry(inv1, "p/a.json", emit="p"),
            "k-hash-mismatch": entry(inv1, "p/a.json", emitSha256="11" * 32),
            "k-hash-upper": entry(inv1, "p/a.json", emitSha256=sha(b"{}").upper()),
            "k-no-identity": entry(inv1, "p/a.json", identity=None),
            "k-empty-identity": entry(inv1, "p/a.json", identity={}),
            "k-identity-no-lists": entry(inv1, "p/a.json", identity={"fingerprint": "f"}),
            "k-plugin-gone": entry(inv1, "p/a.json", identity=gone_plugin),
            "k-plugin-changed": entry(inv1, "p/a.json", identity=identity(names=("A.esm", "Changed.esp", "B.esp"))),
            "k-overlay-changed": entry(inv1, "p/a.json", identity=identity(overlays=(overlay, changed_overlay))),
            "k-plugin-and-overlay-changed": entry(inv1, "p/a.json", identity=identity(names=("Changed.esp",), overlays=(changed_overlay,))),
        },
    }
    for folder, entries in caches.items():
        write(root / folder, "cache.json", json.dumps(entries, indent=1))
    write(root, "sweep/inv-0003/cache.json", "{not json")
    write(root, "sweep/inv-0004/cache.json", "[]")
    # after the entries were made: a plugin and an overlay change on disk
    plugins["Changed.esp"].write_bytes(b"plugin bytes after")
    changed_overlay.write_bytes(b"overlay bytes after")
    # a cache file is given as its document (the paths in it are masked); every other file as its text
    files = []
    for p in sorted(root.rglob("*")):
        if p.is_file():
            rel = str(p.relative_to(root)).replace("\\", "/")
            if p.name == "cache.json" and p.parent.name in ("launch", "inv-0001", "inv-0002"):
                files.append({"path": rel, "json": mask(json.loads(p.read_text(encoding="utf-8")), tmp)})
            else:
                files.append({"path": rel, "text": p.read_text(encoding="utf-8")})

    prior = [root / "sweep" / n / "cache.json" for n in ("inv-0005", "inv-0004", "inv-0003", "inv-0002", "inv-0001")]
    log = root / own / "cache-log.txt"

    def fresh():
        log.unlink(missing_ok=True)
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            c = O.Cache(root / own / "cache.json", log, prior=prior)
        return c, buf

    lookups = []
    keys = ["k-nowhere"] + sorted({k for entries in caches.values() for k in entries})
    for key in keys:
        c, buf = fresh()
        with contextlib.redirect_stdout(buf):
            hit = c.lookup(key, f"job-{key}")
        lookups.append({"key": key, "label": f"job-{key}", "want": None if hit is None else [mask(str(hit[0]), tmp), mask(str(hit[1]), tmp)],
                        "stdout": mask(buf.getvalue(), tmp), "log": mask(log.read_text(encoding="utf-8"), tmp)})

    records = []
    with_identity = write(root, f"{own}/navownprobe/recorded.json", json.dumps({"Configuration": CFG}, indent=1))
    without = write(root, f"{own}/navownprobe/no-identity.json", "{}")
    outside = write(root, "elsewhere/outside.json", json.dumps({"Configuration": CFG}))
    more = [{"path": str(p.relative_to(root)).replace("\\", "/"), "text": p.read_text(encoding="utf-8")} for p in (with_identity, without, outside)]
    for name, key, emit in [("an emit with an identity", "a" * 64, with_identity), ("an emit with no identity", "b" * 64, without),
                            ("an emit outside the cache's folder", "c" * 64, outside)]:
        c, buf = fresh()
        with contextlib.redirect_stdout(buf):
            c.record(key, f"job-{name.split()[-1]}", emit)
        e = c.entries.get(key)
        if e:
            e = dict(e, recordedUtc="<TIME>")
        saved = json.loads((root / own / "cache.json").read_text(encoding="utf-8"))
        records.append({"name": name, "key": key, "label": f"job-{name.split()[-1]}", "emit": str(emit.relative_to(root)).replace("\\", "/"),
                        "entry": e, "savedKeys": list(saved), "stdout": mask(buf.getvalue(), tmp), "log": mask(log.read_text(encoding="utf-8"), tmp)})
        write(root / own, "cache.json", json.dumps(caches[own], indent=1))      # each record starts from the same file
    # the bytes a save writes, from an empty cache
    write(root / own, "cache.json", "{}")
    c, buf = fresh()
    with contextlib.redirect_stdout(buf):
        c.record("a" * 64, "job-identity", with_identity)
    saved_text = re.sub(r'"recordedUtc": "[^"]*"', '"recordedUtc": "<TIME>"', (root / own / "cache.json").read_bytes().decode("utf-8"))
    write(root / own, "cache.json", json.dumps(caches[own], indent=1))
    log.unlink(missing_ok=True)
    return {"files": files + more, "own": f"{own}/cache.json", "log": f"{own}/cache-log.txt",
            "prior": [str(p.relative_to(root)).replace("\\", "/") for p in prior], "lookups": lookups, "records": records,
            "savedText": saved_text}


# ---------------------------------------------------------------------------------------- refusal marker

def refused_cases() -> list:
    """The strings tests/test_ordersweep_refused.py collects from the harness sources, each with what REFUSED_RE says."""
    literal = re.compile(r'(\$@|@\$|\$|@)?"((?:[^"\\\n]|\\.|"")*)"')
    hole = re.compile(r"\{[^{}]*\}")
    call = re.compile(r"\b(?:new PopulationRefused|Refuse)\(\s*((?:\$@|@\$|\$|@)?\"(?:[^\"\\\n]|\\.|\"\")*\")")

    def fill(text, word):
        return hole.sub(word, text.replace("\\n", "\n").replace('\\"', '"').replace('""', '"'))

    found = []
    for p in sorted(HARNESS.rglob("*.cs")):
        parts = set(p.parts)
        if "bin" in parts or "obj" in parts or "runcontrol" in parts:
            continue
        src = p.read_text(encoding="utf-8", errors="replace")
        for m in literal.finditer(src):
            body = m.group(2)
            if "REFUSED" in body or "MISSING MASTER" in body:
                found.append(fill(body, "Name.esp"))
            if re.search(r"\} refused:", body):
                found.append(fill(body, "0A1B2C:Name.esp"))
            if re.search(r"refused \{|\} refused\b(?!:)", body):
                found.append(fill(body, "1,234"))
        for m in call.finditer(src):
            lit = literal.match(m.group(1))
            if lit:
                found.append(fill(lit.group(2), "Name.esp"))
    found += [
        "   pair-loop timers same: 537,452 pairs (223,553 mesh queries), 497.8s; paths world 537,189 / zero-area skip 123 / shifted 90 / refused 50; "
        "budget-exhausted 0, time-outs 0, preparation refusals 0; served from the pair cache 313,887",
        "   ! population gate: the arm REFUSED.", "REFUSED: a plugin's master is absent",
        "MISSING MASTER - 'Child.esp' masters 'Parent.esp', which this order does not load",
        "      the two configurations differ: Probe overlapprobe vs contactprobe. REFUSED.",
        "--population x.json names no plugin and no mod folder; an empty population would judge nothing, so it is refused.",
        "", "refused", "Refused 5", "refused  1,234", "refused,5", "3 refused", "3, refused", "1,2 refused: why", "x refused y", "unrefused", "refusedx",
        "cannot enable", "Profile Mismatch", "missing  master", "refused\n5", "refused\t\u0663", "\u0663 refused", "refused 1,", "refused ,", "is refused.",
        "refused-5", "refused (5)", "5\u00a0refused", "pre_refused", "\u00e9refused", "refused\u00e9",
    ]
    rows, seen = [], set()
    for s in found:
        if s not in seen:
            seen.add(s)
            rows.append({"text": s, "want": bool(O.REFUSED_RE.search(s))})
    assert sum(r["want"] for r in rows) >= 60 and sum(not r["want"] for r in rows) >= 8, "the source scan found too few refusal strings"
    return rows


# -------------------------------------------------------------------------------------------- arguments

class Parsed(Exception):
    pass


def args_cases() -> list:
    """What the parser main() builds makes of an argument list: the values, or argparse's error sentence."""
    original = argparse.ArgumentParser.parse_args

    def spy(self, *a, **k):
        raise Parsed(original(self, *a, **k))

    rows = []
    for argv in [
        ["--candidates", "C", "--out", "O"],
        ["--candidates", "C", "--out", "O", "--probes", "a, b ,,c", "--instance", "I", "--profile", "P", "--game", "G", "--probe-args", "--state X  --lean",
         "--harness", "H", "--baseline", "B", "--max-candidates", "3", "--diff-tolerance", "1.5", "--no-reverse", "--no-pair-results", "--full-reverse",
         "--deadline-seconds", "5397.833", "--probe-cmd", "a=python a.py", "--probe-cmd", "b=x", "--population", "pop.json", "--population-probes", "a,b",
         "--reuse-cache", "one", "--reuse-cache", "two"],
        ["--candidates=C", "--out=O=1", "--max-candidates=-2", "--deadline-seconds=1e3", "--probe-args=--lean", "--harness", "H"],
        ["--cand", "C", "--o", "O", "--max", " 7 ", "--dead", "-0.5", "--no-r", "--full", "--population", "f", "--population-", "a", "--harness", "H"],
        ["--candidates", "C", "--out", "O", "--out", "O2", "--max-candidates", "1_000", "--deadline-seconds", "inf", "--harness", "H"],
        ["--candidates", "C", "--out", "O", "--deadline-seconds", "nan", "--max-candidates", "-0", "--diff-tolerance", "-1", "--harness", "H"],
        ["--candidates", "C", "--out", "O", "--probe-args", "", "--probes", "", "--harness", ""],
        ["--candidates", "C", "--out", "-", "--harness", "-5", "--baseline", "-.5", "--instance", "- x"],
        ["--candidates", "C"],
        [],
        ["--candidates", "C", "--out", "O", "stray", "--frobnicate", "more"],
        ["--candidates", "C", "--out", "O", "--probe-args", "--lean"],
        ["--candidates", "C", "--out", "O", "--max-candidates", "two"],
        ["--candidates", "C", "--out", "O", "--max-candidates", "1.0"],
        ["--candidates", "C", "--out", "O", "--deadline-seconds", "soon"],
        ["--candidates", "C", "--out", "O", "--pro", "x"],
        ["--candidates", "C", "--out", "O", "--no-reverse=yes"],
        ["--candidates", "C", "--out", "O", "--harness"],
        ["--candidates", "C", "--out", "O", "--harness", "--out"],
        ["--candidates", "C", "--out", "O", "-x"],
        ["--candidates", "C", "--out", "O", "--no"],
    ]:
        err = io.StringIO()
        argparse.ArgumentParser.parse_args = spy
        old_argv = sys.argv
        sys.argv = ["ordersweep.py"] + argv
        try:
            with contextlib.redirect_stderr(err):
                O.main()
            raise AssertionError("main() went past its parser")
        except Parsed as p:
            ns = vars(p.args[0])
            if "--harness" not in argv and not any(x.startswith("--harness=") for x in argv):
                ns.pop("harness")
            want = {"values": ns}
        except SystemExit as e:
            last = err.getvalue().strip().splitlines()[-1]
            assert e.code == 2 and last.startswith("ordersweep.py: error: "), (e.code, err.getvalue())
            want = {"error": last[len("ordersweep.py: error: "):]}
        finally:
            argparse.ArgumentParser.parse_args = original
            sys.argv = old_argv
        rows.append({"argv": argv, "want": want})
    return rows


# ------------------------------------------------------------------------------------------ request keys

FAKES = HARNESS / "tests" / "fixtures" / "evalrun"


def request_key_cases(tmp: Path) -> list:
    """One real sweep over the test fixture's fake probes: every cache entry's key with the parts it was made from."""
    root = tmp / "sweep"
    plugins = ["FixtureBase.esm", "FixtureTarget.esp", "FixtureOther.esp"]
    for n in plugins:
        write(root, f"plugins/{n}", f"TES4 {n}")
    write(root, "candidates/candidate-00-baseline.json", json.dumps({"Plugins": plugins, "Changes": []}))
    write(root, "candidates/candidate-01-swap.json", json.dumps({"Plugins": [plugins[0], plugins[2], plugins[1]], "Changes": ["swap"]}))
    pop = write(root, "population.json", json.dumps({"schema": 1, "plugins": [], "mods": [], "targets": ["x"]}))
    scen = write(root, "scenario.json", json.dumps({"pluginDir": str(root / "plugins").replace("\\", "/"), "arms": {}}))
    out = root / "out"
    probe_args = "--state baseline --note x"
    argv = [sys.executable, str(HARNESS / "ordersweep.py"), "--candidates", str(root / "candidates"), "--out", str(out),
            "--probes", "navownprobe,overlapprobe", "--harness", str(root / "no-harness"), "--profile", "Default", "--probe-args", probe_args,
            "--population", str(pop), "--population-probes", "overlapprobe",
            "--probe-cmd", f"navownprobe={sys.executable} {FAKES / 'fakeprobes' / 'navownprobe.py'}",
            "--probe-cmd", f"overlapprobe={sys.executable} {FAKES / 'fakeprobes' / 'overlapprobe.py'}",
            "--probe-cmd", f"conflictdiff={sys.executable} -c pass"]
    env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(scen), HOUSECARL_RUNLOG_DIR=str(root / "runlog"), PYTHONUTF8="1")
    done = subprocess.run(argv, env=env, capture_output=True, text=True, encoding="utf-8")
    assert done.returncode == 0, done.stderr + done.stdout
    jobs = {}
    for ln in (out / "jobs.jsonl").read_text(encoding="utf-8").splitlines():
        rec = json.loads(ln)
        jobs[rec["job"]] = rec
    cache = json.loads((out / "cache.json").read_text(encoding="utf-8"))
    rows = []
    for key, e in cache.items():
        rec = jobs[e["label"]]
        probe = e["label"].split("-")[0]
        i = rec["inputs"]
        rows.append({"job": e["label"], "probe": probe, "program": rec["program"], "orderSha": sha(Path(i["order"]).read_bytes()),
                     "baseSha": sha(Path(i["base"]).read_bytes()) if i.get("base") else None,
                     "arguments": ["--profile", "Default"] + probe_args.split(),
                     "populationSha": sha(pop.read_bytes()) if probe == "overlapprobe" else None,
                     "reverseOfSha": sha(Path(i["baselineEmit"]).read_bytes()) if i.get("baselineEmit") else None, "want": key})
    assert [r["job"] for r in rows] == ["navownprobe-baseline", "overlapprobe-baseline", "navownprobe-candidate-01-swap", "navownprobe-candidate-01-swap-reverse",
                                        "overlapprobe-candidate-01-swap", "overlapprobe-candidate-01-swap-reverse"], [r["job"] for r in rows]
    assert rows[5]["reverseOfSha"] and not rows[3]["reverseOfSha"] and rows[1]["populationSha"] and not rows[0]["populationSha"]
    return rows


# ------------------------------------------------------------------------------------------------- main

def main() -> int:
    if os.name != "nt":
        print("make_known_answers_sweep.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-sweep", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-sweep-")).resolve()
    try:
        emits, created = emit_cases(tmp)
        doc = {
            "python": platform.python_version(),
            "placeholders": {"backslash": BACK, "forward": FWD, "doubled": DOUBLED},
            "notProof": O.NOT_PROOF,
            "defaultProbes": O.DEFAULT_PROBES,
            "rowIdentity": row_identity_cases(),
            "parseDiff": parse_diff_cases(),
            "classify": classify_cases(),
            "emits": emits,
            "created": created,
            "memoryRoute": memory_cases(tmp),
            "candidateFiles": candidate_cases(tmp),
            "programKey": program_key_cases(tmp),
            "cache": cache_cases(tmp),
            "refused": refused_cases(),
            "arguments": args_cases(),
            "requestKeys": request_key_cases(tmp),
        }
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
