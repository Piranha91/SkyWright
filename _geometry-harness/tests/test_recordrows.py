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
    return [(f"b-{fk[:6]}", [(BASE, fk, rtype, None, base, None, 3), (A, fk, rtype, None, a, None, 3),
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
    # ModA adds a second linked reference; ModB carries both in the other order: the same change, not a lost one
    base = ref(**{"LinkedReferences": "[list: 1 item(s)]", "LinkedReferences[0].Reference": "000001:Skyrim.esm"})
    a = ref(**{"LinkedReferences": "[list: 2 item(s)]", "LinkedReferences[0].Reference": "000001:Skyrim.esm",
               "LinkedReferences[1].Reference": "000002:Skyrim.esm", "Placement.Position": "-0,20.00001,30"})
    b = dict(a, **{"LinkedReferences[0].Reference": "000002:Skyrim.esm",
                   "LinkedReferences[1].Reference": "000001:Skyrim.esm", "Placement.Position": "0,20,30.000004"})
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


def case_23_a_cut_off_deep_read_and_a_narrow_read_of_one_plugin_combine():
    """Found on Group 1's transcripts after Gate B: each cell is read twice per plugin, once deep (cut off at the
    server's line limit) and once for named fields. A field is taken from the read that covers it."""
    deep = {"FormKey": "000D74:Skyrim.esm", "Location": "02008A:Skyrim.esm", "Music": "000001:Skyrim.esm",
            "Name": "Hall", "Persistent": "[list: 40 item(s)]", "Persistent[0].Version2": "0",
            "…": "(expansion truncated)"}
    # ModA's deep read is cut off before Name: its Name is unknown, not absent
    a_deep = {k: v for k, v in dict(deep, Location="0A0001:ModA.esp").items() if k != "Name"}
    fk = "000D74:Skyrim.esm"
    batches = [("deep", [(BASE, fk, "Cell", None, deep, None, 3), (A, fk, "Cell", None, a_deep, None, 3),
                         (B, fk, "Cell", None, dict(deep, Location="0B0001:ModB.esp"), None, 3)]),
               ("narrow", [(BASE, fk, "Cell", None, {"FormKey": fk, "Regions": "[list: 0 item(s)]"}, ["Regions"], 2),
                           (A, fk, "Cell", None, {"FormKey": fk, "Regions": "[list: 0 item(s)]"}, ["Regions"], 2),
                           (B, fk, "Cell", None, {"FormKey": fk, "Regions": "[list: 0 item(s)]"}, ["Regions"], 2)])]
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    got = [(c["A"], c["B"], c["Field"]) for c in conflicts(emit)]
    assert got == [(A, B, "Location")], got
    assert not any("depth" in k for k in unjudged(emit)), unjudged(emit)


def case_25_a_polymorphic_field_is_found_on_its_arms():
    sc = RR.load_scope()
    got = sc.classify("DialogResponses", "Conditions[0].Data.Function")
    assert got == "compared", got


def case_27_a_worldspace_persistent_cell_is_a_container():
    """Found reconciling with session 1 (C3): a persistent cell's Grid differs between mods and means nothing in game."""
    wrld = {"FormKey": "00003C:Skyrim.esm", "TopCell": "[Cell]", "TopCell.FormKey": "000D74:Skyrim.esm"}
    cell = {"FormKey": "000D74:Skyrim.esm", "Grid": "[CellGrid]", "Grid.Flags": "1", "Grid.Point": "0,0"}
    batches = [("w", [(BASE, "00003C:Skyrim.esm", "Worldspace", None, wrld, None, 3)])] + three(
        "000D74:Skyrim.esm", cell, dict(cell, **{"Grid.Flags": "2"}), dict(cell, **{"Grid.Flags": "3"}), rtype="Cell")
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    assert conflicts(emit) == [], conflicts(emit)
    aside = {k: v for c in emit.get("Coverage") or [] for k, v in (c.get("SetAside") or {}).items()}
    assert any("container" in k for k in aside), aside


def case_29_a_rotation_a_full_turn_apart_is_the_same_rotation():
    """Found reconciling Group 6 with session 1 (C3): a patch wrote -0.6104 as 5.6728 radians."""
    base_ = ref(**{"Placement.Rotation": "-0.6104,-0.148,6.2094"})
    a = ref(**{"Placement.Rotation": "0.5,-0.148,6.2094"})
    b = ref(**{"Placement.Rotation": "0.5000001,6.1351853,-0.0737853"})
    # and the Group 6 case: -1.7884 stored by a patch as 4.4947, a ten-thousandth of a radian off a full turn
    with tempfile.TemporaryDirectory() as td:
        emit2, _ = rows_of(three("000802:Skyrim.esm", ref(**{"Placement.Rotation": "0.5,-0.148,6.2094"}),
                                 ref(**{"Placement.Rotation": "-1.7884,-0.148,6.2094",
                                                                     "LocationReference": "0A0001:ModA.esp"}),
                                 ref(**{"Placement.Rotation": "4.4947,-0.148,6.2094",
                                        "LocationReference": "0B0001:ModB.esp"})), Path(td))
    assert [c["Field"] for c in conflicts(emit2)] == ["LocationReference"], conflicts(emit2)
    base_ = ref(**{"Placement.Rotation": "-0.6104,-0.148,6.2094"})
    a = ref(**{"Placement.Rotation": "0.5,-0.148,6.2094"})
    b = ref(**{"Placement.Rotation": "0.5000001,6.1351853,-0.0737853"})
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", base_, a, b), Path(td))
    assert conflicts(emit) == [], conflicts(emit)


def case_31_a_worldspace_large_references_list_is_not_compared():
    """Ruled at Gate C round 1: a worldspace's LargeReferences is generated bookkeeping, not authored game data."""
    sc = RR.load_scope()
    named = (json.loads(SCOPE_FILE.read_text(encoding="utf-8")).get("generatedBookkeeping") or {}).get("Worldspace")
    assert named and "LargeReferences" in named, f"the field scope does not name Worldspace.LargeReferences: {named}"
    got = sc.classify("Worldspace", "LargeReferences[0].GridPosition")
    assert got == "bookkeeping", got
    w = {"FormKey": "00003C:Skyrim.esm", "LargeReferences": "[list: 1 item(s)]", "LargeReferences[0].GridPosition": "1,1"}
    batches = three("00003C:Skyrim.esm", w, dict(w, **{"LargeReferences[0].GridPosition": "2,2"}),
                    dict(w, **{"LargeReferences": "[list: 0 item(s)]"}), rtype="Worldspace")
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(batches, Path(td))
    assert conflicts(emit) == [], conflicts(emit)


def case_33_every_unjudged_field_is_listed_with_its_record_and_plugins():
    """Gate C round 1 (M1): a field left unjudged is counted, and listed with its record, reason and the plugins whose
    versions of the record were compared, so a report can say how many could hide a clash between two mods."""
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                {"FormKey": "000801:Skyrim.esm", "Placement": "[Placement]",
                                 "Placement.Position": "10,20,30"}, sel_b=["Placement"]), Path(td))
    listed = emit.get("Unjudged")
    assert isinstance(listed, list), f"the emit lists no unjudged fields: keys {sorted(emit)}"
    hit = [u for u in listed if u.get("Field") == "LocationReference"]
    assert len(hit) == 1 and hit[0].get("FormKey") == "000801:Skyrim.esm" and "not read" in str(hit[0].get("Reason")) \
        and hit[0].get("Plugins") == [BASE, A, B], listed
    assert len(listed) == sum(unjudged(emit).values()), (len(listed), unjudged(emit))
    with tempfile.TemporaryDirectory() as td:
        emit2, _ = rows_of(three("000801:Skyrim.esm", ref(), ref(LocationReference="0A0001:ModA.esp"),
                                 "error: the plugin could not be read"), Path(td))
    failed = emit2.get("Unjudged") or []
    assert len(failed) == 1 and failed[0].get("Field") is None and "read failed" in str(failed[0].get("Reason")), failed


def case_35_a_change_only_in_bytes_the_game_does_not_read_is_not_a_row():
    """Clockwork review 1 (P21, lane cw C630): a cell's Grid flags are one byte of land flags and three unused bytes
    (record-field-scope.json integerMasks); 103 cells differed only in the unused bytes. A change in the read byte stays."""
    def cell(flags):
        return {"Grid.Flags": flags, "Grid.Point": "1,2"}
    with tempfile.TemporaryDirectory() as td:
        emit, _ = rows_of(three("000D01:Skyrim.esm", cell("5504256"), cell("327680"), cell("0"), rtype="Cell"), Path(td))
    assert conflicts(emit) == [], conflicts(emit)
    with tempfile.TemporaryDirectory() as td:
        emit2, _ = rows_of(three("000D02:Skyrim.esm", cell("5504256"), cell("327681"), cell("0"), rtype="Cell"), Path(td))
    assert [c["Field"] for c in conflicts(emit2)] == ["Grid"], conflicts(emit2)


C = "ModC.esp"                              # a mod loaded between the base game and ModA that ModA does not master


def records_transcript(folder: Path, chain, fk, rtype, form):
    """A houseCARL 2.0 `housecarl_records` capture of one record in every plugin of `chain` ([(plugin, {field: value})],
    load order, winner last). form "fields": one by-name read per plugin (`source`); form "tree": one tree answer whose
    nodes list each plugin's differences from the winner, as the record imports carry them."""
    folder.mkdir(parents=True, exist_ok=True)
    req = [{"batchMarker": "b1"}]
    resp = [{"batchMarker": "b1"}]
    if form == "fields":
        for i, (plugin, fields) in enumerate(chain, start=1):
            req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": "housecarl_records",
                        "arguments": {"formids": [fk], "source": plugin, "format": "json",
                                      "project": {"form": "fields",
                                                  "fields": sorted({k.split(".")[0] for k in fields})}}}})
            body = {"records": [{"formid": fk, "type": rtype, "editorid": None, "source": plugin,
                                 "fields": [{"path": k, "value": v} for k, v in fields.items()]}]}
            resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(body)}]}})
    else:
        winner, wf = chain[-1]
        nodes = []
        for plugin, fields in chain[:-1]:
            deltas = [f"{k}={v} ({winner} {wf.get(k)})" for k, v in fields.items() if wf.get(k) != v]
            nodes.append({"plugin": plugin, "is_winner": False, "complete": True, "deltas": deltas})
        nodes.append({"plugin": winner, "is_winner": True})
        req.append({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "housecarl_records",
                    "arguments": {"formids": [fk], "format": "json", "project": {"form": "tree"}}}})
        body = {"form": "tree", "rows": [{"formid": fk, "type": rtype, "editorid": None,
                                          "touchers": [p for p, _ in chain], "nodes": nodes}]}
        resp.append({"jsonrpc": "2.0", "id": 1, "result": {"content": [{"type": "text", "text": json.dumps(body)}]}})
    (folder / "01-request.jsonl").write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
    (folder / "02-response.jsonl").write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
    return folder / "01-request.jsonl", folder / "02-response.jsonl"


ORDER_C = [{"name": n, "path": f"X:/Lab/{n}", "sha256": "0" * 64} for n in (BASE, C, A, B)]


def case_37_a_value_equal_to_the_masters_is_no_change_of_that_plugin():
    """Window 2, lane core (Pilgrim's blind review, B1): ModA's water height is its master's (the base game's) value;
    ModC, loaded before it and not its master, changed it, and ModB puts ModC's value back. ModA made no change of its
    own: it only undoes ModC's, so it loses no edit. Without master lists the plugin before it is what it overrides."""
    fk = "0216BB:Skyrim.esm"
    chain = [(BASE, {"WaterHeight": "0"}), (C, {"WaterHeight": "-2147483600"}), (A, {"WaterHeight": "0"}),
             (B, {"WaterHeight": "-2147483600"})]
    masters = {BASE.lower(): set(), C.lower(): {BASE.lower()}, A.lower(): {BASE.lower()}, B.lower(): {BASE.lower()}}
    with tempfile.TemporaryDirectory() as td:
        req, resp = records_transcript(Path(td), chain, fk, "Cell", "fields")
        chains = RR.read_chains(req, resp, ORDER_C, RR.load_scope())
        got = chains[fk]
        i = got["providers"].index(A)
        changed, j = RR.own_change(got["providers"], got["values"]["WaterHeight"], i, masters.get(A.lower()))
        assert (changed, got["providers"][j]) == (False, BASE), (changed, got["providers"][j])
        changed2, j2 = RR.own_change(got["providers"], got["values"]["WaterHeight"], i, None)
        assert (changed2, got["providers"][j2]) == (True, C), (changed2, got["providers"][j2])
        emit = RR.rows_for_setup(RR.reads_of(req, resp, ORDER_C), ORDER_C, RR.load_scope(), masters=masters)
    assert [c for c in conflicts(emit) if c["A"] == A] == [], conflicts(emit)


def case_39_the_tree_and_the_by_name_reads_give_one_answer():
    """Window 2, lane core: the record imports carry two capture forms (by-name reads per plugin; one tree answer of
    each plugin's differences from the winner). Whose change a value is, is the same question in both: ModA's value is
    its master ModC's (no change of its own); ModB's is its own."""
    fk = "000801:Skyrim.esm"
    chain = [(BASE, {"Owner": "1"}), (C, {"Owner": "2"}), (A, {"Owner": "2"}), (B, {"Owner": "3"})]
    masters = {A.lower(): {BASE.lower(), C.lower()}, B.lower(): {BASE.lower()}}
    answers = {}
    for form in ("fields", "tree"):
        with tempfile.TemporaryDirectory() as td:
            req, resp = records_transcript(Path(td), chain, fk, "PlacedObject", form)
            got = RR.read_chains(req, resp, ORDER_C, RR.load_scope())[fk]
            answers[form] = [RR.own_change(got["providers"], got["values"]["Owner"], got["providers"].index(p),
                                           masters.get(p.lower()))[0] for p in (A, B)]
    assert answers == {"fields": [False, True], "tree": [False, True]}, answers


def case_41_a_tree_value_differing_from_the_winner_only_in_path_case_is_the_winners():
    """Window 2, lane core (Clockwork review B1): a tree answer lists Grand Solitude's LOD texture as a difference from
    the winner's, only in letter case and slashes. The game reads one file: ModA holds the value it inherits (no change
    of its own), ModB its own."""
    fk = "00003C:Skyrim.esm"
    win = "Data\\Textures\\Landscape\\Mountains\\MountainSlab02.dds"
    chain = [(BASE, {"HdLodDiffuseTexture": win}), (C, {"HdLodDiffuseTexture": win}),
             (A, {"HdLodDiffuseTexture": "data/textures/landscape/mountains/mountainslab02.dds"}),
             (B, {"HdLodDiffuseTexture": "data/textures/other.dds"}), ("ModD.esp", {"HdLodDiffuseTexture": win})]
    order = ORDER_C + [{"name": "ModD.esp", "path": "X:/Lab/ModD.esp", "sha256": "0" * 64}]
    masters = {A.lower(): {C.lower()}, B.lower(): {C.lower()}}
    with tempfile.TemporaryDirectory() as td:
        req, resp = records_transcript(Path(td), chain, fk, "Worldspace", "tree")
        got = RR.read_chains(req, resp, order, RR.load_scope())[fk]
    vals = got["values"]["HdLodDiffuseTexture"]
    own = [RR.own_change(got["providers"], vals, got["providers"].index(p), masters.get(p.lower()))[0] for p in (A, B)]
    assert own == [False, True], (own, vals)


def case_43_a_list_change_the_winner_keeps_item_by_item_is_not_lost():
    """Window 2, lane final (Pilgrim review B1): ModA adds a condition and marks the first OR; ModB's winning list keeps
    both and adds one of its own, so ModA's change is not lost (no row); where ModB's list drops ModA's added condition,
    ModA's change is lost (a row). Lists are compared item by item, an item being all of its leaves."""
    def conds(*items):
        return {f"Conditions[{i}].{k}": v for i, (fn_, fl) in enumerate(items)
                for k, v in (("Data.Function", fn_), ("Flags", fl))}
    base_, a_ = conds(("HasKeyword", "0")), conds(("HasKeyword", "OR"), ("IsUndead", "0"))
    rows = {}
    for name, b_ in (("kept", conds(("HasKeyword", "OR"), ("HasPerk", "OR"), ("IsUndead", "0"))),
                     ("dropped", conds(("HasKeyword", "OR"), ("HasPerk", "0")))):
        with tempfile.TemporaryDirectory() as td:
            req, resp = records_transcript(Path(td) / name, [(BASE, base_), (A, a_), (B, b_)], "00C743:Skyrim.esm",
                                           "MagicEffect", "fields")
            lines = [json.loads(x) for x in req.read_text(encoding="utf-8").splitlines() if x.strip()]
            for m in lines:
                proj = ((m.get("params") or {}).get("arguments") or {}).get("project") or {}
                if proj.get("fields"):
                    proj["fields"] = ["Conditions"]          # the read selects the field, as the imports do
            req.write_text("\n".join(json.dumps(x) for x in lines) + "\n", encoding="utf-8")
            order = [{"name": n, "path": f"X:/Lab/{n}", "sha256": "0" * 64} for n in (BASE, A, B)]
            got = RR.read_chains(req, resp, order, RR.load_scope())["00C743:Skyrim.esm"]
            its = (got.get("items") or {}).get("Conditions")
            emit = RR.rows_for_setup(RR.reads_of(req, resp, order), order, RR.load_scope())
            rows[name] = (RR.list_change_kept(its[0], its[1], its[2]) if its and hasattr(RR, "list_change_kept") else None,
                          [(c["A"], c["Field"]) for c in emit["Conflicts"]])
    assert rows == {"kept": (True, []), "dropped": (False, [(A, "Conditions")])}, rows


def case_45_both_capture_forms_give_every_providers_field_values():
    """After the final round (Pilgrim review B1): a rule that reads a record's values (where an object stands, which
    actor value an effect has) gets each provider's full value of a field from either capture form. A tree answer lists
    a provider's value only where it differs from the winner's, so the winner's value (in each difference's
    parenthesis) fills the rest. A tree answer omits a leaf no provider differs in (it is the same throughout)."""
    fk = "0825F7:Skyrim.esm"
    chain = [(BASE, {"Placement.Position": "-9153.301,5547.0957,-241.0651", "Placement.Rotation": "0,0,0"}),
             (C, {"Placement.Position": "-9153.301,5547.0957,-241.395", "Placement.Rotation": "0,0,0.5"}),
             (A, {"Placement.Position": "-9170.188,5452.2637,-227.8473", "Placement.Rotation": "0,0,0"}),
             (B, {"Placement.Position": "-9170.188,5452.2637,-227.8473", "Placement.Rotation": "0,0,0"})]
    answers = {}
    for form in ("fields", "tree"):
        with tempfile.TemporaryDirectory() as td:
            req, resp = records_transcript(Path(td), chain, fk, "PlacedObject", form)
            got = RR.read_chains(req, resp, ORDER_C, RR.load_scope())[fk]
            answers[form] = [RR.provider_fields(got, "Placement", got["providers"].index(p)) for p in (BASE, C, A, B)]
    assert answers["fields"] == answers["tree"], answers
    assert answers["tree"][1]["Placement.Position"] == "-9153.301,5547.0957,-241.395", answers["tree"][1]
    assert answers["tree"][2]["Placement.Rotation"] == answers["fields"][2]["Placement.Rotation"], answers


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
