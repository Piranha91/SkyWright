"""Tests for recordrows.py — record-lane rows derived from a record import's own MCP transcripts.

Dependency-free: `python _geometry-harness/tests/test_recordrows.py`. Case numbers are odd. Transcripts are written by
hand in the shape the record imports carry (JSON lines; batches separated by `batchMarker` lines, request ids restarting
in each batch; `housecarl_read_plugin_file` text responses of `path = value` lines). RECORDROWS_PATH, if set, loads
the module from that file instead (the sabotage arms use it).

A row is one record, field and losing plugin on one setup: the plugin wrote the field with a value different from the
one it overrides (it authored a change), and the winning value differs from its own. The fields compared come from the
generated schema reference; child records, file bookkeeping and runtime-merged fields are named in skill data by their
schema names (record-field-scope.json).
"""
import copy
import hashlib
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
_path = os.environ.get("RECORDROWS_PATH") or str(HERE.parent / "recordrows.py")
RR, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("recordrows_under_test", _path)
    RR = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(RR)
except (FileNotFoundError, ImportError, OSError) as _ex:   # the module does not exist yet on the base
    RR, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"

SCOPE_FILE = ROOT / ".claude/skills/world-edit-compatibility/references/record-field-scope.json"
BASE, A, B = "Skyrim.esm", "ModA.esp", "ModB.esp"
PATHS = {BASE: "C:/Game/Data/Skyrim.esm", A: "X:/Lab/mods/Mod A/ModA.esp", B: "X:/Lab/mods/Mod B/ModB.esp"}
ORDER = [{"name": n, "path": PATHS[n], "sha256": "0" * 64} for n in (BASE, A, B)]
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        if RR is None:
            raise AssertionError(f"recordrows.py is not loadable: {LOAD_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


def text_response(plugin, formid, rtype, editorid, fields):
    lines = ["read_plugin_file \u2014 OUT-OF-LOAD-ORDER (raw file read; the game does not load this file)",
             f"file: {PATHS[plugin].replace('/', chr(92))}  [direct path; NOT active]",
             "masters: Skyrim.esm",
             f"type={rtype}  formid={formid}  editorid={editorid or '<none>'}",
             f"fields (raw, from {plugin}):"]
    lines += [f"  {k} = {v}" for k, v in fields.items()]
    return "\n".join(lines) + "\n"


def write_transcript(folder: Path, batches):
    """batches: [(marker, [(plugin, formid, rtype, editorid, fields | 'error: ...', selection | None, depth)])]."""
    folder.mkdir(parents=True, exist_ok=True)
    req, resp = [], []
    for marker, reads in batches:
        req.append({"batchMarker": marker, "sourceFile": f"raw/{marker}.request.jsonl", "sourceSha256": "0" * 64})
        resp.append({"batchMarker": marker, "sourceFile": f"raw/{marker}.response.jsonl", "sourceSha256": "0" * 64})
        req.append({"jsonrpc": "2.0", "id": 0, "method": "initialize",
                    "params": {"clientInfo": {"name": "evalrun:p:baseline"}}})
        resp.append({"jsonrpc": "2.0", "id": 0, "result": {"serverInfo": {"name": "houseCARL"}}})
        for i, (plugin, formid, rtype, eid, fields, selection, depth) in enumerate(reads, start=1):
            args = {"plugin": PATHS[plugin], "formid": formid, "format": "text", "depth": depth}
            if selection is not None:
                args["fields"] = selection
            req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call",
                        "params": {"name": "housecarl_read_plugin_file", "arguments": args}})
            text = fields if isinstance(fields, str) else text_response(plugin, formid, rtype, eid, fields)
            resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": text}]}})
    (folder / "01-request.jsonl").write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
    (folder / "02-response.jsonl").write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
    return folder / "01-request.jsonl", folder / "02-response.jsonl"


REF = {"FormKey": "000801:Skyrim.esm", "FormVersion": "44", "Version2": "0", "IsDeleted": "False",
       "Base": "0136D6:Skyrim.esm", "LocationReference": "02008A:Skyrim.esm", "Placement": "[Placement]"}


def ref(**over):
    f = dict(REF)
    f["Placement.Position"] = "10,20,30"
    f.update(over)
    return f


def rows_of(batches, tmp, order=ORDER):
    req, resp = write_transcript(tmp, batches)
    parsed = RR.parse_transcript(req, resp)
    return RR.rows_for_setup(parsed["reads"], order, RR.load_scope()), parsed


def three(fk, base, a, b, rtype="PlacedObject", sel_b=None):
    return [("b1", [(BASE, fk, rtype, None, base, None, 3), (A, fk, rtype, None, a, None, 3),
                    (B, fk, rtype, None, b, sel_b, 3)])]


def conflicts(emit):
    return [c for c in emit.get("Conflicts") or [] if c.get("Kind") == "LOST-EDIT"]


def unjudged(emit):
    out = {}
    for c in emit.get("Coverage") or []:
        for k, v in (c.get("Unjudgeable") or {}).items():
            out[k] = out.get(k, 0) + v
    return out


# --------------------------------------------------------------------------------------------- cases

def case_1_every_listed_name_is_a_schema_name():
    sc = RR.load_scope()
    assert sc.problems() == [], sc.problems()
    bad = json.loads(SCOPE_FILE.read_text(encoding="utf-8"))
    bad["fileBookkeeping"] = list(bad["fileBookkeeping"]) + ["FormVersionn"]
    with tempfile.TemporaryDirectory() as td:
        f = Path(td) / "scope.json"
        f.write_text(json.dumps(bad), encoding="utf-8")
        probs = RR.load_scope(scope_file=f).problems()
    assert any("FormVersionn" in p for p in probs), probs


def case_3_classify_by_schema():
    sc = RR.load_scope()
    want = [("Cell", "Temporary[0].Placement.Position", "child-record"),
            ("Cell", "Persistent", "child-record"),
            ("PlacedObject", "FormVersion", "bookkeeping"),
            ("PlacedObject", "EnableParent.Versioning", "bookkeeping"),
            ("Location", "WorldspaceCellsAdded[2]", "merged"),
            ("PlacedObject", "LocationReference", "compared"),
            ("PlacedObject", "Placement.Position", "compared"),
            ("PlacedObject", "FormKey", "identity"),
            ("PlacedObject", "ZzzNotAField", "unknown")]
    got = [(t, p, sc.classify(t, p)) for t, p, _ in want]
    assert got == want, [g for g, w in zip(got, want) if g != w]


def case_5_requests_and_responses_pair_within_their_batch():
    with tempfile.TemporaryDirectory() as td:
        req, resp = write_transcript(Path(td), [
            ("b1", [(A, "000801:Skyrim.esm", "PlacedObject", None, ref(), None, 3)]),
            ("b2", [(B, "000802:Skyrim.esm", "PlacedObject", None, ref(), None, 3)])])
        reads = RR.parse_transcript(req, resp)["reads"]
    got = sorted((r["batch"], r["id"], r["plugin"], r["formKey"]) for r in reads)
    assert got == [("b1", 1, PATHS[A], "000801:Skyrim.esm"), ("b2", 1, PATHS[B], "000802:Skyrim.esm")], got


def case_7_a_lost_change_is_one_row_with_its_values():
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                ref(LocationReference="0B0001:ModB.esp")), Path(td))
    cs = conflicts(emit)
    assert len(cs) == 1, cs
    c = cs[0]
    v = c["Necessary"][0]["Values"]
    assert (c["A"], c["B"], c["Field"]) == (A, B, "LocationReference"), c
    assert (v["lost"], v["winner"], v["overridden"]) == ("0A0001:ModA.esp", "0B0001:ModB.esp", "02008A:Skyrim.esm"), v


def case_9_an_identity_forward_is_not_a_row():
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                ref(LocationReference="0A0001:ModA.esp")), Path(td))
    assert conflicts(emit) == [], conflicts(emit)


def case_11_child_records_bookkeeping_and_merged_fields_are_not_rows():
    cell = {"FormKey": "000D74:Skyrim.esm", "FormVersion": "44", "Temporary": "[list: 1 item(s)]",
            "Temporary[0].Placement.Position": "0,0,0"}
    a = dict(cell, **{"FormVersion": "43", "Temporary[0].Placement.Position": "1,1,1"})
    b = dict(cell, **{"FormVersion": "40", "Temporary[0].Placement.Position": "2,2,2"})
    loc = {"FormKey": "000E01:Skyrim.esm", "WorldspaceCellsAdded": "[list: 1 item(s)]", "WorldspaceCellsAdded[0]": "1,1"}
    batches = three("000D74:Skyrim.esm", cell, a, b, rtype="Cell") + three(
        "000E01:Skyrim.esm", loc, dict(loc, **{"WorldspaceCellsAdded[0]": "2,2"}),
        dict(loc, **{"WorldspaceCellsAdded[0]": "3,3"}), rtype="Location")
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    assert conflicts(emit) == [], conflicts(emit)
    aside = {}
    for c in emit.get("Coverage") or []:
        for k, v in (c.get("SetAside") or {}).items():
            aside[k] = aside.get(k, 0) + v
    assert any("merged" in k for k in aside), f"merged fields are not counted as set aside: {emit.get('Coverage')}"


def case_13_a_field_the_capture_did_not_read_is_unjudged_not_equal():
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                {"FormKey": "000801:Skyrim.esm", "Placement": "[Placement]",
                                 "Placement.Position": "10,20,30"}, sel_b=["Placement"]), Path(td))
    assert conflicts(emit) == [], conflicts(emit)
    uj = unjudged(emit)
    assert any("not read" in k for k in uj), f"the unread field is not counted as unjudged: {uj}"


def case_15_list_order_and_float_noise_are_not_changes():
    base = ref(**{"LinkedReferences": "[list: 2 item(s)]", "LinkedReferences[0].Reference": "000001:Skyrim.esm",
                  "LinkedReferences[1].Reference": "000002:Skyrim.esm"})
    a = ref(**{"LinkedReferences": "[list: 2 item(s)]", "LinkedReferences[0].Reference": "000002:Skyrim.esm",
               "LinkedReferences[1].Reference": "000001:Skyrim.esm", "Placement.Position": "-0,20.00001,30"})
    b = dict(a, **{"Placement.Position": "0,20,30.000004"})
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", base, a, b), Path(td))
    assert conflicts(emit) == [], conflicts(emit)


def case_17_a_failed_read_leaves_the_record_unjudged():
    batches = three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                    "error: the plugin could not be read")
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    assert conflicts(emit) == [], conflicts(emit)
    assert any("read failed" in k for k in unjudged(emit)), unjudged(emit)


def case_19_every_row_cites_the_bytes_it_came_from():
    with tempfile.TemporaryDirectory() as td:
        req, resp = write_transcript(Path(td), three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                                     ref(LocationReference="0B0001:ModB.esp")))
        cfg = {"plugins": ORDER}
        emit = RR.derive(req, resp, cfg, RR.load_scope(), rel={req: "evidence/x/01-request.jsonl",
                                                                 resp: "evidence/x/02-response.jsonl"})
        shas = {hashlib.sha256(p.read_bytes()).hexdigest() for p in (req, resp)}
    src = {(d.get("path"), d.get("sha256")) for d in emit.get("derivedFrom") or []}
    assert {s for _, s in src} == shas and {p for p, _ in src} == {"evidence/x/01-request.jsonl",
                                                                   "evidence/x/02-response.jsonl"}, src
    for c in conflicts(emit):
        reads = c["Necessary"][0].get("Reads") or []
        assert {r.get("plugin") for r in reads} >= {A, B} and all(r.get("batch") and r.get("id") for r in reads), c


def case_21_a_defining_plugin_overridden_is_not_a_lost_edit():
    batches = [("b1", [(BASE, "000801:Skyrim.esm", "PlacedObject", None, ref(), None, 3),
                       (A, "000801:Skyrim.esm", "PlacedObject", None, ref(LocationReference="0A0001:ModA.esp"),
                        None, 3)])]
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    assert conflicts(emit) == [], conflicts(emit)


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
