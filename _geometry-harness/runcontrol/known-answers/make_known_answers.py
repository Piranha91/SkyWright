"""Write foundations.json: the known answers runcontrol's self-test must reach, computed by the Python controller.

Every expected value comes from the real functions in evalpipeline.py, evalrun.py and toolidentity.py (or from the
Python runtime itself: json, repr, float formatting, pathlib, ntpath). Run it again only when one of those rules
changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers.py

Windows only: the path and text-mode rules recorded here are the Windows ones.
"""
from __future__ import annotations

import base64
import json
import os
import platform
import random
import shutil
import struct
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1]))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run      # noqa: E402
import evalpipeline as EP   # noqa: E402
import evalrun as ER        # noqa: E402
import toolidentity as TI   # noqa: E402

OUT = HERE / "foundations.json"
BACK, FWD = "<ROOT\\>", "<ROOT/>"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def mask(obj, root: Path):
    """The same value with the temporary root replaced by a placeholder, in both spellings."""
    if isinstance(obj, str):
        return obj.replace(str(root), BACK).replace(EP.fwd(root), FWD)
    if isinstance(obj, dict):
        return {mask(k, root): mask(v, root) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [mask(v, root) for v in obj]
    return obj


def without_at(obj):
    if isinstance(obj, dict):
        return {k: without_at(v) for k, v in obj.items() if k != "at"}
    if isinstance(obj, list):
        return [without_at(v) for v in obj]
    return obj


# ------------------------------------------------------------------ JSON documents

DOCS = {
    "nested": {"b": 1, "a": [1, 2.0, {"z": None, "y": True, "x": False}], "B": "x", "empty": {}, "emptyList": [],
               "nestedEmpty": {"a": [], "b": {}, "c": [[]], "d": [{}], "e": [[], {}, [{}]]}},
    "strings": {"controls": "".join(chr(c) for c in range(0x20)), "del": "\x7f", "quotes": "\"'`", "backslash": "C:\\a\\b",
                "separators": "a\u2028b\u2029c", "latin": "\u00e9\u00c9\u00df\u0080\u00ff", "astral": "\U0001F600\U00010000\U0010FFFF",
                "bmpHigh": "\uff21\uffff\ue000\ud7ff", "html": "<>&+'/", "mixed": "tab\tnl\ncr\rbs\bff\f\u00e9\U0001F600"},
    "floats": [90.0, 1e-05, 0.1, 1e22, -0.0, 0.0, 1e16, 1e15, 123456789012345680.0, 5e-324, 1.7976931348623157e308,
               0.30000000000000004, 1.5e-07, 100.0, 1e21, 0.0001, 0.00001, 2.5, -1.25e-10, 1e100, 9007199254740993.0,
               float("nan"), float("inf"), float("-inf")],
    "ints": [0, -1, 1, 2 ** 31, 2 ** 53 + 1, 2 ** 63 - 1, 2 ** 63, 2 ** 64, -2 ** 70, 10 ** 30, 3, 3.0, True, False],
    "keys": {"\U0001F600": 1, "\uff21": 2, "a": 3, "A": 4, "\u00e9": 5, "Z": 6, "_": 7, "": 8, "aB": 9, "Ab": 10,
             "\ud7ff": 11, "\ue000": 12, "a\U00010000": 13, "a\uffff": 14, "10": 15, "9": 16, " ": 17, "a.b": 18,
             "a-b": 19, "a/b": 20, "\u2028": 21, "nested": {"\U0001F600": {"b": 1, "a": 2}, "\uffff": [{"z": 1, "y": 2}]}},
    "receipt": {"stage": "census", "exitCode": 0, "inputsSha256": "ab" * 32, "accepted": True,
                "inputs": {"entry": "cd" * 32, "upstream": {}}, "outputs": {"C:/runs/r 1/census/census.json": "ef" * 32,
                                                                            "C:/runs/r 1/census": "dir:" + "01" * 32},
                "bound": None, "seconds": 12.3, "driverSha256": "23" * 32,
                "disk": {"addedBytes": 123456789012, "evaluatedRecords": 4567}, "budgetMinutes": 90.0, "limit": 90},
    "scalarString": "plain \u00e9", "scalarInt": 3, "scalarFloat": 2.5, "scalarNull": None, "scalarTrue": True,
    "emptyList": [], "emptyDict": {},
    "deep": {"a": {"b": {"c": {"d": [1, [2, [3, [4, {"e": []}]]]]}}}},
}
#: a lone surrogate cannot be encoded as UTF-8, so only the ASCII-escaped forms exist for this one
DOCS_ASCII_ONLY = {"loneSurrogate": {"k\ud800": "v\udfff", "pairSplit": "\ud83d x \ude00"}}

FORMS = [(ind, sort, asc) for ind in ("c", "n", 1, 2) for sort in (False, True) for asc in (False, True)]


def dumps(doc, ind, sort, asc) -> str:
    if ind == "c":
        return json.dumps(doc, separators=(",", ":"), sort_keys=sort, ensure_ascii=asc)
    return json.dumps(doc, indent=None if ind == "n" else ind, sort_keys=sort, ensure_ascii=asc)


def json_cases(tmp: Path) -> list:
    rows = []
    for name, doc in {**DOCS, **DOCS_ASCII_ONLY}.items():
        raw_ok = name not in DOCS_ASCII_ONLY
        row = {"name": name, "src": json.dumps(doc), "forms": {}}
        for ind, sort, asc in FORMS:
            key = f"indent={ind} sort={int(sort)} ascii={int(asc)}"
            row["forms"][key] = b64(dumps(doc, ind, sort, asc).encode("utf-8")) if (asc or raw_ok) else None
        row["sha256Json"] = EP.sha256_json(doc)
        text = json.dumps(doc, indent=1, sort_keys=True) + "\n"          # evalrun's population form
        row["populationText"] = b64(text.encode("utf-8"))
        (tmp / "population.json").write_text(text, encoding="utf-8", newline="\n")
        row["populationFile"] = b64((tmp / "population.json").read_bytes())
        if raw_ok:
            row["canonical"] = b64(ER.canonical(doc).encode("utf-8"))
            row["canonicalSha256"] = ER.sha256_text(ER.canonical(doc))
            EP.write_json(tmp / "pipeline.json", doc)
            row["pipelineFile"] = b64((tmp / "pipeline.json").read_bytes())
            ER.write_json(tmp / "eval.json", doc)
            row["evalFile"] = b64((tmp / "eval.json").read_bytes())
        rows.append(row)
    return rows


PARSE = [
    b'{"a": 1, "b": [true, false, null]}', b'\xef\xbb\xbf{"bom": 1}', b'[NaN, Infinity, -Infinity]',
    b'{"a": 1, "b": 2, "a": 3}', b' \t\r\n[ 1 ,\n2 ]\r\n ', b'[1E5, 1e+2, 1e-2, -0, -0.0, 1.0, 3, 0, 0.0, 10, 1.5e3]',
    b'12345678901234567890123', b'-9223372036854775808', b'-9223372036854775809', b'9223372036854775807',
    b'9223372036854775808', b'1e999', b'-1e999', b'"\\ud83d\\ude00"', b'"\\ud83d"', b'"\\ude00\\ud83d"', b'"a\\/b"',
    b'"\\u00e9\\u2028\\b\\f\\n\\r\\t\\"\\\\"', '"raw \u00e9 \U0001F600 \u2028"'.encode("utf-8"), b'""', b'{}', b'[]',
    b'{"": {"": []}}', b'[[[[[[1]]]]]]', b'0.1', b'1.7976931348623157e308', b'5e-324', b'2.5E-3', b'4.9e-325',
    b'"\\u0000"', b'"\x7f"', b'{"a":1,"A":2}',
    # refused by Python, so refused by the port
    b'[1,]', b'{"a":1,}', b'01', b'+1', b'.5', b'1.', b"'a'", b'[1] x', b'"a\tb"', b'nan', b'-NaN', b'tru', b'', b'  ',
    b'{"a" 1}', b'[1 2]', b'"\\x41"', b'"\\u12"', b'"\\u00g1"', b'"\\u+123"', b'"\\u1_23"', b'-', b'--1', b'Infinity1',
    b'{1: 2}', b'\x0b[1]', b'[1]\x0c', b'"a', b'{"a":', b'[', b'\xff\xfe', b'"\xc3"', b'-Infinit', b'1e', b'1e+',
    b'\xef\xbb\xbf\xef\xbb\xbf[1]', b'[1]\x00', b'\xc2\xa0[1]', b'{"a":1 "b":2}', b'[,1]', b'{,}', b'1 2', b'"a"b',
    b'1.e5', b'0x10', b'1_000', b'-01', b'00', b'0e0', b'-0e-0', b'[-]', b'{"a":NaN}', b'Nan', b'INFINITY', b'null ',
    b'TRUE',
]


def parse_cases() -> list:
    rows = []
    for raw in PARSE:
        for sig in (True, False):
            row = {"bytes": b64(raw), "bom": sig}
            try:
                value = json.loads(raw.decode("utf-8-sig" if sig else "utf-8"))
                row["ok"], row["dump"] = True, json.dumps(value, separators=(",", ":"))
            except ValueError:
                row["ok"] = False
            rows.append(row)
    return rows


# ------------------------------------------------------------------ floats and text

def bits_of(x: float) -> str:
    return "%016x" % struct.unpack("<Q", struct.pack("<d", x))[0]


def float_cases() -> list:
    rng = random.Random(20261118)
    fixed = [0.0, -0.0, 1.0, -1.0, 90.0, 0.1, 0.25, 0.35, 0.05, 0.15, 0.45, 2.5, 3.5, 0.125, 0.0625, 0.1875, 0.5, 1.5,
             0.95, 0.949999, 9.95, 9.999, 99.95, 0.04, -0.04, -0.05, -0.06, 1e-05, 1e-04, 0.00012345, 1e15, 1e16, 1e17,
             1e21, 1e22, 123456.7, 1234567.0, 999999.5, 999999.4, 100000.0, 1000000.0, 0.0001, 0.00001234, 2.5e10,
             123456789.123, 0.30000000000000004, 1 / 3, 2 / 3, 1e-300, 5e-324, 1.7976931348623157e308, 12.34, 12.35,
             1048576.0, 1073741824.0, 21.8, 0.049999999999999996, 8.5, 9.5, 1e-07, 123.456, 1.25, 1.35, 7.0, 16.0,
             4.35, 1023.99, 1024.0, 22.0, 0.999, 0.9995, 1.0005, 2.675, 1.005, 72057594037927936.0]
    fixed += [round(rng.uniform(-1000, 1000), rng.randint(0, 6)) for _ in range(150)]
    fixed += [rng.uniform(0, 1) * 10 ** rng.randint(-8, 12) for _ in range(150)]
    rows = []
    for x in fixed:
        rows.append({"bits": bits_of(x), "repr": repr(x), "json": json.dumps(x), "f1": f"{x:.1f}", "f2": f"{x:.2f}",
                     "f3": f"{x:.3f}", "g": f"{x:g}", "round1": repr(round(x, 1))})
    for _ in range(400):
        x = struct.unpack("<d", struct.pack("<Q", rng.getrandbits(64)))[0]
        row = {"bits": bits_of(x), "repr": repr(x), "json": json.dumps(x), "g": f"{x:g}"}
        rows.append(row)
    for x in (float("nan"), float("inf"), float("-inf")):
        rows.append({"bits": bits_of(x), "repr": repr(x), "json": json.dumps(x), "f1": f"{x:.1f}", "g": f"{x:g}"})
    return rows


REPR = ["", "plain", "it's", 'say "hi"', "both ' and \"", "C:\\Games\\Skyrim VR\\mods", "tab\there", "nl\nx", "cr\rx",
        "nul\x00x", "bell\x07", "esc\x1b", "del\x7f", "nbsp\u00a0x", "latin \u00e9\u00ff", "c1 \u0080\u009f",
        "soft\u00adhyphen", "zwsp\u200b", "ls\u2028ps\u2029", "bom\ufeff", "pua\ue000", "astral \U0001F600",
        "astral private \U000F0000", "unassigned \U000E0080", "\uffff", "ideographic space\u3000", "cjk \u4e2d\u6587",
        "combining e\u0301", "rtl \u202e", "\u0378", "\U0001FAE8", "en quad\u2000", "line\x0bvt\x0cff\x1c\x1d\x1e\x1f\x85"]
REPR_VALUES = [None, True, False, 0, -7, 2 ** 70, 1.5, 90.0, 1e-05, 1e22, float("inf"), float("-inf"), float("nan"), -0.0,
               [], ["a", "b"], ["it's", 1, None, 2.5], {}, {"a": 1, "b": [True, None]}, {"k'": "v\""},
               {"__tuple__": []}, {"__tuple__": ["a"]}, {"__tuple__": ["kind", "sha"]},
               [{"__tuple__": ["kind", "sha"]}, {"__tuple__": [1, 2.0]}], {"x": {"__tuple__": [None]}}]


def from_wire(v):
    if isinstance(v, dict):
        if set(v) == {"__tuple__"}:
            return tuple(from_wire(x) for x in v["__tuple__"])
        return {k: from_wire(x) for k, x in v.items()}
    if isinstance(v, list):
        return [from_wire(x) for x in v]
    return v


def repr_cases() -> list:
    rows = [{"src": json.dumps(s), "repr": repr(s), "str": str(s)} for s in REPR + ["lone\ud800", "\udfff\ud800"]]
    rows += [{"src": json.dumps(v), "repr": repr(from_wire(v)), "str": str(from_wire(v))} for v in REPR_VALUES]
    return rows


LINES = ["", "one", "a\nb", "a\n", "\n", "\n\n", "a\r\nb", "a\rb", "a\n\rb", "a\r\n", "a\x0bb\x0cc", "a\x1cb\x1dc\x1ed",
         "a\x1fb", "a\x85b", "a\u2028b\u2029c", "a\u2028", "\r\n\r\n", "x\r", "tail\n\n", "\u00e9\nz", "a\x00b", "a\tb"]
SPLITS = ["", "  ", "a b", " a  b ", "a\tb\nc\rd\x0be\x0cf", "a\x1cb\x1dc\x1ed\x1fe", "a\x85b\xa0c", "a\u1680b\u2000c\u200ad",
          "a\u2028b\u2029c\u202fd\u205fe\u3000f", "a\u200bb", "a\ufeffb", "python  C:\\x y\\z.py --flag", "a\x00b",
          "a\u180eb", "a\u2007b\u2008c"]
SORTS = [["b", "A", "a", "B", "aa", "Aa", "aA", "AA"], ["Zeta.esp", "alpha.esp", "Alpha.esm", "beta.esl", "ALPHA.ESP"],
         ["a_b", "a-b", "a.b", "a b", "ab", "aB", "A_b", "a~b", "a{b"], ["\U0001F600", "\uff21", "\uffff", "\ue000", "z", "Z"],
         ["x10", "x9", "X2", "x1"], ["\u00e9", "\u00c9", "e", "E", "f"], [], ["one"],
         ["mod B", "Mod a", "mod A", "MOD b", "mod a"], ["_under", "Upper", "lower", "[bracket", "^caret", "`tick"]]
PATHS = ["a/b/", "a/./b", "a//b", "a/../b", "C:/x/y/", "c:x", "C:\\", "C:/", ".", "", "./a", "//srv/share/x/", "a\\b/c",
         "/a/b", "C:/a/./b/..", "..", "../..", "../a/..//b", "C:a/..", "C:/..", "C:/../x", "//srv/Share/a/..", "//srv/share",
         "//srv/share/", "//srv", "///a", "\\\\?\\C:\\x\\y", "\\\\?\\UNC\\srv\\share\\z", "\\\\.\\pipe\\x", "x.pdb", ".pdb",
         "x.pdb.", "x.PDB", "a.b.pdb", "x.", "x..pdb", "pdb", "dir.d/file", "C:/Mods/Some Mod/Plugin.ESP", "a/b.tar.gz",
         "trailing.dot./x", "C:/A/./b/../C\\d/", "./.", "a/.", "a/..", "/", "\\", "c:", "c:/", "C:/x/../..", "é/Ü.TXT",
         "name with space.txt ", "C:rel/./x", "a\\\\b", "C:\\\\x", "./C:x", "a/C:x"]
SAME = [("C:/A/b", "c:\\a\\B"), ("C:/a/./b/", "C:/a/b"), ("C:/a/x/../b", "c:/A/B"), ("a/b", "a/b/c"), ("", ""), ("a", "A"),
        ("X:/inst\\mods\\M\\P.esp", "X:\\inst\\mods\\M\\p.esp"), ("C:/a", "D:/a"), ("C:/a b", "C:/a  b"), ("./a", "a"),
        ("//srv/share/a", "\\\\SRV\\share\\A"), ("C:a", "C:/a"), ("\u00c9", "\u00e9")]
RELS = [("C:/run/a/b.json", "C:/run"), ("C:/RUN/a/b.json", "c:/run"), ("C:/run", "C:/run"), ("C:/run/a", "C:/run/a/b"),
        ("C:/other/a", "C:/run"), ("D:/run/a", "C:/run"), ("a/b/c", "a"), ("a/b", "b"), ("C:/run/A/B", "C:/run/a"),
        ("C:/runx/a", "C:/run"), ("//srv/share/a/b", "//SRV/share/a")]
PATH_SORTS = [["a/b.txt", "a-b.txt", "a_b.txt", "a.b.txt", "A/c.txt", "B.txt", "b/x.txt", "ab.txt", "Z.txt", "a/B/q.txt",
               "a/b-1.txt", "a b.txt", "a.txt", "a"],
              ["x/A/1", "x/a/0", "X/a/2", "x/A", "x/a.b", "x/a/", "x"], ["C:/r/b", "c:/R/a", "C:/r", "D:/a", "C:/r/a/z", "C:/r/A"],
              ["\U0001F600.txt", "\uff21.txt", "\uffff.txt", "z.txt", "Z/\U0001F600", "z/\uff21"],
              ["candidate-10.json", "candidate-2.json", "candidate-01.json", "candidate-00-baseline.json", "Candidate-03.json"],
              ["run/b", "run/a/c", "run-2/a", "run.old/a", "run_x/a", "run/a", "run"]]
ISO = [0.0, 1.0, 1700000000.0, 1700000000.9999996, 1700000000.9999994, 1700000000.5, 1000000000.999999,
       1699999999.9999995, 1700000000.4999995, 1795045638.8048332, 951782400.0, 4102444799.9999995, 86399.99999949,
       86399.9999995, 1709164800.25]
UNIX = [0, 1, 951782399, 951782400, 1700000000, 1709164800, 1795045638, 4102444799]


def text_cases() -> dict:
    return {
        "repr": repr_cases(),
        "splitlines": [{"src": s, "lines": s.splitlines()} for s in LINES],
        "split": [{"src": s, "parts": s.split()} for s in SPLITS],
        "sorted": [{"items": x, "byCodePoint": sorted(x), "byLower": sorted(x, key=str.lower)} for x in SORTS],
        "paths": [{"p": p, "str": str(Path(p)), "posix": Path(p).as_posix(), "name": Path(p).name, "suffix": Path(p).suffix,
                   "normpath": os.path.normpath(p), "normcase": os.path.normcase(p)} for p in PATHS],
        "samePath": [{"a": a, "b": b, "pipeline": EP.same_path(a, b), "evalrun": ER.same_path(a, b)} for a, b in SAME],
        "relativeTo": [{"child": c, "parent": p, "rel": rel_or_none(c, p)} for c, p in RELS],
        "sortedPaths": [{"items": x, "sorted": [x[i] for i in sorted(range(len(x)), key=lambda i: Path(x[i]))]}
                        for x in PATH_SORTS],
        "isoOf": [{"bits": bits_of(t), "iso": ER.iso_of(t)} for t in ISO],
        "utc": [{"unix": n, "plus": datetime.fromtimestamp(n, timezone.utc).isoformat(timespec="seconds"),
                 "z": datetime.fromtimestamp(n, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")}
                for n in UNIX],
        "sha256Text": [{"src": s, "sha256": ER.sha256_text(s)} for s in ("", "abc", "\u00e9\U0001F600\n", "a\u2028b")],
        "dotted": [{"path": p, "value": json.dumps(ER.dotted(DOTTED, p))}
                   for p in ("a", "a.b", "a.b.0", "a.b.1.c", "a.b.2", "a.b.-1", "a.b.01", "a.x", "", "a..b", "n", "n.0",
                             "l.0.0", "l.1", "a.b.1.c.d", "z", "s.0")],
    }


DOTTED = {"a": {"b": [10, {"c": None}]}, "n": None, "l": [[5]], "z": 0, "s": "text", "": {"": 1}}


def rel_or_none(child, parent):
    try:
        return Path(child).relative_to(Path(parent)).as_posix()
    except ValueError:
        return None


# ------------------------------------------------------------------ a folder with fixed bytes and times

T0 = 1_700_000_000_000_000_000
TREE = [  # (relative path, bytes, mtime in ns: a multiple of 100, as Windows keeps it)
    ("Tool.exe", b"MZ\x90\x00 apphost", T0),
    ("tool.dll", b"managed code \x00\x01\x02", T0 + 1_234_567_800),
    ("Tool.PDB", b"symbols", T0 + 2_000_000_000),
    ("lower.pdb", b"more symbols", T0 + 3_000_000_000),
    ("notes.txt", b"a stray text file counts\r\n", T0 + 123_456_700),
    ("UPPER.TXT", b"", T0 + 999_999_900),
    (".hidden-top", b"dot file", 1_000_000_000_000_000_100),
    ("tool.deps.json", b"{}", 2_200_000_000_987_654_300),
    ("a-b.txt", b"1", T0 + 50_000_000),
    ("a/b.txt", b"22", T0 + 100),
    ("a.b/c.txt", b"333", T0 + 200),
    ("runtimes/win-x64/native/Lib.dll", b"native", T0 + 300),
    ("runtimes/readme.TXT", b"runtime text", T0 + 400),
    ("runtimes/Win-ARM64/x.so", b"arm", T0 + 500),
    ("runtimes/keep.pdb", b"runtime symbols", T0 + 600),
    ("shots/a.png", b"\x89PNG not identity", T0 + 700),
    ("sub/b.txt", b"sub b", T0 + 800),
    ("sub/B-1.txt", b"sub b-1", T0 + 900),
    ("sub/b_2.txt", b"sub b_2", T0 + 1_000),
    ("sub/Deep/c.bin", bytes(range(256)), T0 + 1_100),
    ("sub/Deep/\u00e9\u00dc.txt", "caf\u00e9".encode("utf-8"), T0 + 1_200),
    ("\U0001F600.bin", b"astral name", T0 + 1_300),
    ("\uff21.txt", b"fullwidth name", T0 + 1_400),
    ("links/one.bin", b"x" * 4096, T0 + 1_500),
]
LINKS = [("links/two.bin", "links/one.bin"), ("links/deeper/three.bin", "links/one.bin")]
DIRS = ["hollow", "sub/Empty"]


def build_tree(root: Path):
    for rel, data, _ in TREE:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
    for rel, target in LINKS:
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        os.link(root / target, root / rel)
    for rel in DIRS:
        (root / rel).mkdir(parents=True, exist_ok=True)
    for rel, _, ns in TREE:
        os.utime(root / rel, ns=(ns, ns))
        if (root / rel).stat().st_mtime_ns != ns:
            raise SystemExit(f"{rel}: the volume did not keep the time {ns}; the fixture needs 100 ns steps")


PROBE_CMDS = ["no-such-interpreter-zz tool.dll --flag sub/b.txt", "./Tool.exe  sub\\missing.txt notes.txt", "justwords --x",
              "TOOL.DLL", "sub/b.txt\tsub/Deep/c.bin\u00a0notes.txt", "sub/Deep/\u00e9\u00dc.txt x", "", "   ",
              "no-such-interpreter-zz SUB/B.TXT ./a/../notes.txt"]


def tree_cases(root: Path) -> dict:
    build_tree(root)
    out = {
        "files": [{"path": rel, "bytes": b64(data), "mtimeNs": ns} for rel, data, ns in TREE],
        "links": [{"path": rel, "to": target} for rel, target in LINKS],
        "dirs": DIRS,
        "rglobSorted": [EP.fwd(p.relative_to(root)) for p in sorted(root.rglob("*"))],
        "fingerprint": {rel: EP._fingerprint(root / rel) for rel in (".", "sub", "notes.txt", "UPPER.TXT", "hollow", "absent", "links")},
        "toolDigest": {rel: EP._tool_digest(root / rel) for rel in (".", "sub", "notes.txt", "hollow", "absent")},
        "uniqueBytes": EP.unique_bytes(root), "uniqueBytesNoTop": EP.unique_bytes(root, top_files=False),
        "uniqueBytesLinks": EP.unique_bytes(root / "links"),
        "plainBytes": sum(p.stat().st_size for p in root.rglob("*") if p.is_file()),
        "package": {rel: mask(TI.package(root / rel), root) for rel in (".", "sub", "absent", "runtimes", "links")},
        "sha256File": {rel: EP.sha256_file(root / rel) for rel in ("Tool.exe", "UPPER.TXT", "sub/Deep/c.bin")},
    }
    cwd = os.getcwd()
    os.chdir(root)
    try:
        out["probeCmd"] = [{"text": t, "identity": mask(TI.probe_cmd(t), root)} for t in PROBE_CMDS]
    finally:
        os.chdir(cwd)
    pkg = TI.package(root)
    changed = json.loads(json.dumps(pkg))
    changed["files"] = [f for f in changed["files"] if f["path"] != "notes.txt"] + [{"path": "zz new.txt", "sha256": "0" * 64}]
    changed["files"][0]["sha256"] = "f" * 64
    os.chdir(root)
    try:
        cmd_a, cmd_b = TI.probe_cmd(PROBE_CMDS[1]), TI.probe_cmd("./Tool.exe  sub\\b.txt 'quoted' notes.txt")
    finally:
        os.chdir(cwd)
    scripts_a = {"kind": "scripts", "files": [{"name": "a.py", "sha256": "1" * 64}, {"name": "b.py", "sha256": None},
                                                {"name": "c.py", "sha256": "3" * 64}]}
    scripts_b = {"kind": "scripts", "files": [{"name": "a.py", "sha256": "2" * 64}, {"name": "b.py", "sha256": "4" * 64},
                                                {"name": "c.py", "sha256": None}, {"name": "\U0001F600.py", "sha256": "5" * 64}]}
    pairs = [(pkg, changed), (changed, pkg), (pkg, pkg), (cmd_a, cmd_b), (cmd_b, cmd_a), (scripts_a, scripts_b),
             (scripts_b, scripts_a), ({"kind": "package"}, pkg), (dict(cmd_a, command=None), cmd_a)]
    out["differences"] = [{"old": json.dumps(mask(a, root)), "new": json.dumps(mask(b, root)),
                           "out": mask(TI.differences(a, b), root)} for a, b in pairs]
    out["short"] = [{"src": json.dumps(v), "out": TI.short(v)}
                    for v in ("folder:" + "ab" * 32, "probe-cmd:0123456789abcdef", "nocolon0123456789", None, "", "a:b:c",
                              "scripts:short", 0, 12345678901234567890, "\U0001F600:" + "\U0001F600" * 14, ":", False)]
    return out


# ------------------------------------------------------------------ the stage ledger

IN1 = {"entry": "e" * 64, "upstream": {}}
IN2 = {"entry": "f" * 64, "upstream": {"census": None, "prepare": "a" * 64}, "tools": {"x.py": "caf\u00e9", "y": None},
       "budgetInForce": 90.0}
EXTRA = {"bound": {"n": 1, "why": None}, "seconds": 1.5, "driverSha256": "d" * 64,
         "disk": {"addedBytes": 10, "evaluatedRecords": None}}


def ledger_case(root: Path) -> dict:
    """One scripted life of a stages.json. SelfTest.cs runs the same steps, in the same order."""
    build_tree(root)
    run = root / "run"
    run.mkdir()
    outs = [root / "notes.txt", root / "sub"]
    L = EP.StageLedger(run)
    said = [L.decide("census", IN1)]                                    # nothing recorded: run
    L.record("census", IN1, outs, 0, EXTRA)
    said.append(L.decide("census", IN1))                                # reuse
    (root / "notes.txt").write_bytes(b"changed")
    said.append(L.decide("census", IN1))                                # a file output changed: run
    L.record("census", IN1, outs, 0, EXTRA)
    said.append(L.decide("census", IN1))                                # reuse
    os.utime(root / "sub" / "b.txt", ns=(T0 + 77_700, T0 + 77_700))
    said.append(L.decide("census", IN1))                                # a folder output's time changed: run
    L.record("census", IN1, outs, 0, dict(EXTRA, seconds=0.0, reusedFrom="C:/runs/earlier", reusedSeconds=None))
    L.record("census", IN2, [], 2)                                      # refused: the receipt is set aside
    L.doc["history"][-1]["reason"] = "refused: why it stopped"
    L._save()
    said.append(L.decide("census", IN2))                                # run
    (root / "notes.txt").write_bytes(b"changed again")
    said.append(L.decide("census", IN1))                                # the inputs are back, an output is not: run
    (root / "notes.txt").write_bytes(b"changed")
    said.append(L.decide("census", IN1))                                # inputs and outputs are back: restored, reuse
    L.record("scope", IN2, [root / "sub"], 0, EXTRA)
    try:
        L.record("plan", IN1, [root / "missing.json", root / "notes.txt", root / "gone" / "x"], 0)
        sentence = None
    except EP.Refusal as r:
        sentence = str(r)
    L = EP.StageLedger(run)                                             # read back from the file
    said.append(L.decide("scope", IN2))                                 # reuse
    L.record("scope", IN2, [root / "sub"], 3)                           # exit 3: set aside
    said.append(L.decide("plan", IN1))                                  # run
    said.append(L.decide("scope", IN1))                                 # run
    L.record("scope", IN1, [root / "sub"], 0, EXTRA)                    # a new acceptance drops the one set aside
    said.append(L.decide("scope", IN2))                                 # so the old inputs no longer restore it: run
    said.append(L.decide("scope", IN1))                                 # reuse
    doc = EP.read_json(run / "stages.json")
    return {"in1": IN1, "in2": IN2, "extra": EXTRA, "decisions": said, "refusal": mask(sentence, root),
            "stages": json.dumps(mask(without_at(doc), root), indent=2, ensure_ascii=False)}


ENTRY = {"schema": 1, "id": "wb-caf\u00e9", "inPlace": {"instance": "C:/MO2", "profile": "Default", "game": "C:/Game"},
         "selection": {"mods": ["Mod A", "Mod \u00e9"]}, "evaluation": {"select": ["a", "b"], "depth": "standard", "budgetMinutes": 90},
         "modNames": {"Mod A": "The A mod"}, "nexusNames": True, "patchSearch": {"pairs": []}, "report": {"pages": 3},
         "captureSize": [1920, 1080]}
PORTED = ("census", "prepare", "scope", "plan", "evaluate", "records", "scenes", "bundle")


def entry_case() -> dict:
    reads = EP.stage_entry_keys()
    measured = {k: v for k, v in ENTRY.items() if k not in EP.PRESENTATION_KEYS}
    budget = dict(measured, evaluation=dict(measured["evaluation"], budgetMinutes=45.5))
    return {"entry": ENTRY, "presentationKeys": sorted(EP.PRESENTATION_KEYS), "reads": {s: reads[s] for s in PORTED},
            "hashes": {s: EP.stage_entry_hash(ENTRY, measured, reads[s]) for s in PORTED},
            "budgetMeasured": budget,
            "budgetHashes": {s: EP.stage_entry_hash(ENTRY, budget, reads[s]) for s in PORTED},
            "nonBlocking": sorted(EP.NON_BLOCKING)}


OUTCOMES = [("census", 0, True, ""), ("census", 0, False, "no outputs"), ("plan", 2, True, "refused: x"),
            ("precedent", 2, False, "not blocking"), ("precedent", 0, True, ""), ("bundle", 3, True, "awaiting")]
STATUSES = [[], ["accepted"], ["accepted", "accepted"], ["accepted", "refused"], ["awaiting"], ["refused", "awaiting"],
            ["accepted", "awaiting", "accepted"]]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers.py records Windows path and text rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-")).resolve()
    try:
        (tmp / "tree").mkdir()
        (tmp / "ledger").mkdir()
        doc = {
            "python": platform.python_version(),
            "placeholders": {"backslash": BACK, "forward": FWD},
            "json": json_cases(tmp),
            "parse": parse_cases(),
            "floats": float_cases(),
            "text": text_cases(),
            "tree": tree_cases(tmp / "tree"),
            "ledger": ledger_case(tmp / "ledger"),
            "entry": entry_case(),
            "outcomes": [{"args": list(a), "out": EP.stage_outcome(*a)} for a in OUTCOMES],
            "runStatus": [{"statuses": s, "out": EP.run_status([{"status": x} for x in s])} for s in STATUSES],
            "exitCodes": {"ok": EP.EXIT_OK, "refused": EP.EXIT_REFUSED, "judgement": EP.EXIT_JUDGEMENT},
        }
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
