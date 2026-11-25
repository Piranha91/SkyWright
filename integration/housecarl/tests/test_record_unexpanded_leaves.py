"""Tests for recordrows: a leaf that carries no value is unjudged with its reason, never equal.

Dependency-free: `python _geometry-harness/tests/test_record_unexpanded_leaves.py`. RECORDROWS_PATH, if set, loads the
module from that file instead (the sabotage arms use it).

A depth-limited record read prints an item or a struct it did not expand as its type name (`[KeyValue`2]`) and a list
it did not itemise as its summary (`[list: 1024 item(s)]`). Neither is the field's value: two versions printing the
same type name can hold different values. Such a field is unjudged, with the reason, and is never compared as equal.
A field read with its values (the item's leaves, or a 2.0 tree answer's difference) stays judged.

    C2170  items printed as type names only, and a list printed as its summary only (no items under it): unjudged
           with the reason, no row, never equal
    C2171  (a real 1.9.0 capture's case; it names its load order, so it is kept with the run that wrote it, not here)
    C2172  guards: an itemised grid with one entry changed is a lost edit; a difference in the part read, or in an
           unexpanded list's item count, is still a lost edit; an empty list and a text field whose value is a bracketed
           word stay judged; a dictionary's arm fields stay named; a read that itemises the field is preferred
    C2173  2.0 captures: a by-name read with each grid entry's key and value is judged (row found), and a tree answer's
           content-hash difference is judged (row found)
"""
import importlib.util
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
_path = os.environ.get("RECORDROWS_PATH") or str(HERE.parent / "recordrows.py")
RR, LOAD_ERROR = None, None
try:
    _spec = importlib.util.spec_from_file_location("recordrows_under_test", _path)
    RR = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(RR)
except (FileNotFoundError, ImportError, OSError) as _ex:
    RR, LOAD_ERROR = None, f"{type(_ex).__name__}: {_ex}"

BASE, A, B = "Skyrim.esm", "ModA.esp", "ModB.esp"
PATHS = {BASE: "C:/Game/Data/Skyrim.esm", A: "X:/Lab/mods/Mod A/ModA.esp", B: "X:/Lab/mods/Mod B/ModB.esp"}
ORDER = [{"name": n, "path": PATHS[n], "sha256": "0" * 64} for n in (BASE, A, B)]
FK = "00927C:Skyrim.esm"
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


def text_response(plugin, rtype, fields):
    lines = ["read_plugin_file \u2014 OUT-OF-LOAD-ORDER (raw file read; the game does not load this file)",
             f"file: {PATHS[plugin].replace('/', chr(92))}  [direct path; NOT active]", "masters: Skyrim.esm",
             f"type={rtype}  formid={FK}  editorid=<none>", f"fields (raw, from {plugin}):"]
    return "\n".join(lines + [f"  {k} = {v}" for k, v in fields.items()]) + "\n"


def text_transcript(folder: Path, batches):
    """1.9.0 text reads. batches: [(marker, [(plugin, fields, selection | None, depth)])]."""
    folder.mkdir(parents=True, exist_ok=True)
    req, resp = [], []
    for marker, reads in batches:
        req.append({"batchMarker": marker})
        resp.append({"batchMarker": marker})
        for i, (plugin, fields, selection, depth) in enumerate(reads, start=1):
            args = {"plugin": PATHS[plugin], "formid": FK, "format": "text", "depth": depth}
            if selection is not None:
                args["fields"] = selection
            req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call",
                        "params": {"name": "housecarl_read_plugin_file", "arguments": args}})
            resp.append({"jsonrpc": "2.0", "id": i,
                         "result": {"content": [{"type": "text", "text": text_response(plugin, "Cell", fields)}]}})
    (folder / "01-request.jsonl").write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
    (folder / "02-response.jsonl").write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
    return folder / "01-request.jsonl", folder / "02-response.jsonl"


def rows_19(batches):
    with tempfile.TemporaryDirectory() as td:
        req, resp = text_transcript(Path(td), batches)
        parsed = RR.parse_transcript(req, resp)
        return RR.rows_for_setup(parsed["reads"], ORDER, RR.load_scope())


def lost(emit, field="MaxHeightData"):
    return [(c["A"], c["B"]) for c in emit.get("Conflicts") or [] if c.get("Kind") == "LOST-EDIT"
            and c.get("Field") == field]


def unjudged_on(emit, field="MaxHeightData"):
    return [u.get("Reason") for u in emit.get("Unjudged") or [] if u.get("FormKey") == FK and u.get("Field") == field]


def grid_placeholders(offset, n=4):
    f = {"MaxHeightData": "[CellMaxHeightData]", "MaxHeightData.Offset": offset,
         "MaxHeightData.HeightMap": f"[list: {n} item(s)]"}
    f.update({f"MaxHeightData.HeightMap[{i}]": "[KeyValue`2]" for i in range(n)})
    return f


def grid_items(offset, values):
    f = {"MaxHeightData": "[CellMaxHeightData]", "MaxHeightData.Offset": offset,
         "MaxHeightData.HeightMap": f"[list: {len(values)} item(s)]"}
    for i, v in enumerate(values):
        f.update({f"MaxHeightData.HeightMap[{i}]": "[KeyValue`2]", f"MaxHeightData.HeightMap[{i}].Key": f"{i},0",
                  f"MaxHeightData.HeightMap[{i}].Value": str(v)})
    return f


def grid_summary(offset, n=4):
    return {"MaxHeightData": "[CellMaxHeightData]", "MaxHeightData.Offset": offset,
            "MaxHeightData.HeightMap": f"[list: {n} item(s)]"}


def three(base, a, b, sel=None, depth=3):
    return [(BASE, base, sel, depth), (A, a, sel, depth), (B, b, sel, depth)]


# ------------------------------------------------------------------------------------------------ cases

def case_2170_a_field_printed_without_its_values_is_unjudged_never_equal():
    """ModA rewrote a cell's height grid entries and kept its Offset; ModB carries the base value. The read prints every
    entry as `[KeyValue`2]`, so only the Offset is a value, and it agrees: the field must not compare equal."""
    emit = rows_19([("deep", three(grid_placeholders("-9728"), grid_placeholders("-9728"), grid_placeholders("-9728")))])
    assert lost(emit) == [], f"a row from values the read never gave: {lost(emit)}"
    reasons = unjudged_on(emit)
    assert reasons, "MaxHeightData compared as equal from type names alone; expected an unjudged entry"
    assert reasons == [getattr(RR, "REASON_UNEXPANDED", "(no such reason)")], reasons
    # the narrow read form: `HeightMap = [list: 1024 item(s)]` with no item under it
    emit = rows_19([("narrow", three(grid_summary("-9728"), grid_summary("-9728"), grid_summary("-9728"),
                                     sel=["MaxHeightData"], depth=2))])
    assert lost(emit) == [], lost(emit)
    assert unjudged_on(emit), "a list read as its summary only compared as equal; expected an unjudged entry"
    assert unjudged_on(emit) == [getattr(RR, "REASON_UNEXPANDED", "(no such reason)")], unjudged_on(emit)


def case_2172_fields_read_with_their_values_stay_judged():
    # an itemised grid: ModA changed one entry, ModB carries the base: a lost edit
    emit = rows_19([("deep", three(grid_items("-9728", [1, 2, 3, 4]), grid_items("-9728", [1, 2, 9, 4]),
                                   grid_items("-9728", [1, 2, 3, 4])))])
    assert lost(emit) == [(A, B)], f"itemised grid: {lost(emit)}"
    assert unjudged_on(emit) == [], unjudged_on(emit)
    # the part that was read differs (ModA also moved the Offset): the lost edit is shown although the grid was not read
    emit = rows_19([("deep", three(grid_placeholders("-9216"), grid_placeholders("-9472"), grid_placeholders("-9216")))])
    assert lost(emit) == [(A, B)], f"a difference in the part read was hidden: {lost(emit)}"
    assert unjudged_on(emit) == [], unjudged_on(emit)
    # what is known of an unexpanded list is its item count: ModA added an entry, so its change is shown
    emit = rows_19([("deep", three(grid_placeholders("-9728", 4), grid_placeholders("-9728", 5),
                                   grid_placeholders("-9728", 4)))])
    assert lost(emit) == [(A, B)], f"a different item count was hidden: {lost(emit)}"
    # a dictionary's entries are read as Key and Value only where the read prints them so; an arm's own fields stay
    # named (a package's Data.Location)
    sc = RR.load_scope()
    named = {p: sc.classify(t, p) for t, p in (("Package", "Data.Location"), ("Cell", "MaxHeightData.HeightMap.Value"))}
    assert set(named.values()) == {"compared"}, named
    # an empty list and a text field holding a bracketed word are values
    base = {"Regions": "[list: 0 item(s)]", "Name": "[Hall]"}
    emit = rows_19([("narrow", three(base, dict(base, Name="[Keep]"), base, sel=["Regions", "Name"], depth=2))])
    assert lost(emit, "Name") == [(A, B)], f"a bracketed text value was not compared: {lost(emit, 'Name')}"
    assert unjudged_on(emit, "Regions") == [] and unjudged_on(emit, "Name") == [], emit.get("Unjudged")
    # a narrow read that does not itemise and a deep read that does: the deep read decides
    emit = rows_19([("narrow", three(grid_summary("-9728"), grid_summary("-9728"), grid_summary("-9728"),
                                     sel=["MaxHeightData"], depth=2)),
                    ("deep", three(grid_items("-9728", [1, 2, 3, 4]), grid_items("-9728", [1, 2, 9, 4]),
                                   grid_items("-9728", [1, 2, 3, 4]), depth=6))])
    assert lost(emit) == [(A, B)], f"the itemising read was not used: {lost(emit)}; {emit.get('Unjudged')}"


def records_transcript(folder: Path, chain, form):
    """A houseCARL 2.0 `housecarl_records` capture: form "everything" (one by-name read per plugin, leaves as values and
    notes) or "tree" (one tree answer listing each plugin's differences from the winner)."""
    folder.mkdir(parents=True, exist_ok=True)
    req, resp = [{"batchMarker": "b1"}], [{"batchMarker": "b1"}]
    if form == "everything":
        for i, (plugin, fields) in enumerate(chain, start=1):
            req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": "housecarl_records",
                        "arguments": {"formids": [FK], "source": plugin, "format": "json",
                                      "project": {"form": "everything", "depth": 6}}}})
            body = {"records": [{"formid": FK, "type": "Cell", "editorid": None, "source": plugin,
                                 "fields": [{"path": k, ("note" if v.startswith("[") else "value"): v}
                                            for k, v in fields.items()]}]}
            resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": json.dumps(body)}]}})
    else:
        winner = chain[-1][0]
        nodes = [{"plugin": p, "is_winner": False, "complete": True, "deltas": d} for p, d in chain[:-1]]
        nodes.append({"plugin": winner, "is_winner": True})
        req.append({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "housecarl_records",
                    "arguments": {"formids": [FK], "format": "json", "project": {"form": "tree"}}}})
        body = {"form": "tree", "rows": [{"formid": FK, "type": "Cell", "editorid": None,
                                          "touchers": [p for p, _ in chain], "nodes": nodes}]}
        resp.append({"jsonrpc": "2.0", "id": 1, "result": {"content": [{"type": "text", "text": json.dumps(body)}]}})
    (folder / "01-request.jsonl").write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
    (folder / "02-response.jsonl").write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
    return folder / "01-request.jsonl", folder / "02-response.jsonl"


def rows_20(chain, form):
    with tempfile.TemporaryDirectory() as td:
        req, resp = records_transcript(Path(td), chain, form)
        sc = RR.load_scope()
        chains = RR.chains_for_setup(req, resp, ORDER, sc)
        return RR.rows_for_setup([], ORDER, sc, chains=chains)


def case_2173_a_20_capture_with_values_or_hashes_stays_judged():
    """houseCARL 2.0 reads print each grid entry's key and value (by-name), or compare a long list by content hash
    (tree, since d99817eb). Both carry the value; the change is found and nothing is unjudged."""
    emit = rows_20([(BASE, grid_items("-9728", [1, 2, 3, 4])), (A, grid_items("-9728", [1, 2, 9, 4])),
                    (B, grid_items("-9728", [1, 2, 3, 4]))], "everything")
    assert lost(emit) == [(A, B)], f"by-name 2.0 read: {lost(emit)}; unjudged {emit.get('Unjudged')}"
    assert unjudged_on(emit) == [], unjudged_on(emit)
    note = (" \u2014 compared by content hash, not itemised (over the 2000-line read limit)")
    emit = rows_20([(BASE, []),
                    (A, [f"MaxHeightData.HeightMap: 1024 vs {B} 1024 item(s), contents differ{note}"]),
                    (B, None)], "tree")
    assert lost(emit) == [(A, B)], f"tree content-hash difference: {lost(emit)}; unjudged {emit.get('Unjudged')}"


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
