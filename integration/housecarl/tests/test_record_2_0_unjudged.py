"""Tests for recordrows on houseCARL 2.0 captures: a field it cannot judge is listed as unjudged with its reason.

Dependency-free: `python _geometry-harness/tests/test_record_2_0_unjudged.py`. RECORDROWS_PATH, if set, loads the
module from that file instead (the sabotage arms use it).

A 2.0 record import holds `housecarl_records` by-name reads (one plugin's version of a record) and tree answers (each
plugin's differences from the winner). recordrows judges a field from them or lists it in `Unjudged` with the record,
the field, the reason and the plugins whose versions were compared. It never drops such a field, and it never treats it
as equal.

    C2174  a by-name read cut off at the read limit: its fields are unjudged ("cut off"); a field printed in full
           before the cut stays judged
    C2175  a field one plugin's read did not select ("did not read"), a field the schema does not name, and a field
           read at different depths are unjudged
    C2176  a by-name read that failed (an error answer, an answer that is not JSON, an answer without the record):
           the record is unjudged ("read failed")
    C2177  tree answers: a difference in a field the schema does not name, a plugin the tree could not compare in full
           (its listed difference still found), a narrowed tree's unread fields, and a tree whose plugin list was cut
           off are unjudged
    C2178  guards: a capture that judges every field lists nothing; a tree's entries for a record a by-name read
           judged are not listed; a record read in another setup and a worldspace's persistent cell list nothing; the
           plugins are named in the setup's order
    C2179  (a real 2.0 capture's case; it names its load order, so it is kept with the run that wrote it, not here)
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

BASE, A, B, C = "Skyrim.esm", "ModA.esp", "ModB.esp", "ModC.esp"
ORDER = [{"name": n} for n in (BASE, A, B)]
FK = "000AAA:Skyrim.esm"
CUT = "…"
CUT_NOTE = "(expansion truncated at 2000 lines — narrow with a field path or a lower depth)"
# the reasons, as recordrows words them
CUT_OFF = "a field the read's expansion was cut off before"
NOT_READ = "a field the capture did not read"
DEPTH = "a field not read at the same depth by every plugin"
UNKNOWN = "a field the schema does not name"
FAILED = "the record read failed for one plugin"
TREE_PART = "a field the tree answer could not compare in full for one plugin"
TREE_CUT = "the tree answer's list of plugins was cut off"
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


def by_name(plugin, fields, selection=None, depth=6, rtype="Static", fk=FK):
    """One plugin's by-name read: (arguments, answer body). A value starting with "[" is a note, as 2.0 prints it."""
    proj = {"form": "fields", "fields": selection, "depth": depth} if selection else {"form": "everything", "depth": depth}
    if selection:
        # a selected read prints only the fields it selected
        fields = {k: v for k, v in fields.items() if k == CUT or k.split(".")[0].split("[")[0] in selection}
    body = {"form": proj["form"], "records": [{"formid": fk, "type": rtype, "editorid": None, "source": plugin,
                                               "fields": [{"path": k, ("note" if v.startswith("[") or k == CUT
                                                                       else "value"): v} for k, v in fields.items()]}]}
    return {"formids": [fk], "source": plugin, "format": "json", "project": proj}, body


def tree(nodes, rtype="Static", selection=None, fk=FK):
    """A tree answer: nodes [(plugin, deltas, complete)], the last the winner; a plugin None is a cut-off note."""
    out = []
    for i, (plugin, deltas, complete) in enumerate(nodes):
        if plugin is None:
            out.append({"note": "[nodes truncated at max_chars — raise max_chars or narrow with fields]"})
        elif i == len(nodes) - 1:
            out.append({"plugin": plugin, "is_winner": True, "is_reference": False, "complete": True, "deltas": [],
                        "delta_count": 0, "agreed_count": 0})
        else:
            out.append({"plugin": plugin, "is_winner": False, "is_reference": False, "complete": complete,
                        "deltas": deltas, "delta_count": len(deltas), "agreed_count": 0})
    proj = {"form": "tree", **({"fields": selection} if selection else {})}
    return ({"formids": [fk], "format": "json", "project": proj},
            {"form": "tree", "rows": [{"formid": fk, "type": rtype, "editorid": None, "touchers": [], "nodes": out}]})


def derive(calls, order=ORDER, containers=None):
    """recordrows.derive on a 2.0 capture of `calls`: [(arguments, body)], a body being an answer object, ("error", x)
    for a JSON-RPC error, or ("text", x) for an answer whose text is x."""
    with tempfile.TemporaryDirectory() as td:
        req, resp = [{"batchMarker": "b1"}], [{"batchMarker": "b1"}]
        for i, (args, body) in enumerate(calls, start=1):
            req.append({"jsonrpc": "2.0", "id": i, "method": "tools/call",
                        "params": {"name": "housecarl_records", "arguments": args}})
            if isinstance(body, tuple) and body[0] == "error":
                resp.append({"jsonrpc": "2.0", "id": i, "error": {"code": -32603, "message": body[1]}})
                continue
            text = body[1] if isinstance(body, tuple) else json.dumps(body)
            resp.append({"jsonrpc": "2.0", "id": i, "result": {"content": [{"type": "text", "text": text}]}})
        rq, rs = Path(td) / "01-request.jsonl", Path(td) / "02-response.jsonl"
        rq.write_text("\n".join(json.dumps(x) for x in req) + "\n", encoding="utf-8")
        rs.write_text("\n".join(json.dumps(x) for x in resp) + "\n", encoding="utf-8")
        return RR.derive(rq, rs, {"plugins": order}, RR.load_scope(), containers=containers)


def lost(emit):
    return sorted((r["Field"], r["A"], r["B"]) for r in emit["Conflicts"] if r["Kind"] == "LOST-EDIT")


def unjudged(emit):
    """{(field, reason): plugins} of every listed entry, checked for the record and type."""
    out = {}
    for u in emit.get("Unjudged") or []:
        assert u.get("FormKey") == FK, f"an entry names another record: {u}"
        out[(u.get("Field"), u.get("Reason"))] = list(u.get("Plugins") or [])
    return out


def counted(emit, rtype, reason):
    return sum((c.get("Unjudgeable") or {}).get(reason, 0) for c in emit.get("Coverage") or []
               if c.get("Population") == f"contested {rtype} records")


def static(max_angle="30", material="000123:Skyrim.esm", **more):
    return {"EditorID": "Rock01", "MaxAngle": max_angle, "Material": material, **more}


def case_2174_a_cut_off_by_name_read_is_unjudged():
    """A selected read cut off at the read limit covers none of its fields; a whole-record read cut off inside a field
    covers the fields it printed before it."""
    sel = ["MaxAngle", "Material"]
    cut = {"MaxAngle": "45", CUT: CUT_NOTE}
    emit = derive([by_name(BASE, static(), sel), by_name(A, cut, sel), by_name(B, static(), sel)])
    u = unjudged(emit)
    for f in sel:
        assert u.get((f, CUT_OFF)) == [BASE, A, B], f"{f} is not listed as cut off: {emit.get('Unjudged')}"
    assert counted(emit, "Static", CUT_OFF) == 2, f"coverage line: {emit.get('Coverage')}"
    assert lost(emit) == [], f"a field of a cut-off read was compared: {lost(emit)}"
    # whole-record reads: A changed MaxAngle (printed in full) and loses it; its read was cut inside Model
    a = {"EditorID": "Rock01", "MaxAngle": "45", "Material": "000123:Skyrim.esm", "Model": "[Model]",
         "Model.File": "rock01.nif", CUT: CUT_NOTE}
    full = static(**{"Model": "[Model]", "Model.File": "rock01.nif"})
    emit = derive([by_name(BASE, full), by_name(A, a), by_name(B, full)])
    assert lost(emit) == [("MaxAngle", A, B)], f"the field printed before the cut is not judged: {lost(emit)}"
    assert unjudged(emit).get(("Model", CUT_OFF)) == [BASE, A, B], f"Model: {emit.get('Unjudged')}"


def case_2175_unread_unknown_and_depth_mismatched_fields_are_unjudged():
    sel = ["MaxAngle", "Material"]
    emit = derive([by_name(BASE, static(), sel), by_name(A, {"MaxAngle": "30"}, ["MaxAngle"]),
                   by_name(B, static(), sel)])
    assert unjudged(emit).get(("Material", NOT_READ)) == [BASE, A, B], f"unread: {emit.get('Unjudged')}"
    emit = derive([by_name(BASE, static(Bogus="1")), by_name(A, static(Bogus="2")), by_name(B, static(Bogus="1"))])
    assert unjudged(emit).get(("Bogus", UNKNOWN)) == [BASE, A, B], f"unknown: {emit.get('Unjudged')}"
    deep = static(**{"ObjectBounds": "[ObjectBounds]", "ObjectBounds.First": "-8,-8,0", "ObjectBounds.Second": "8,8,16"})
    emit = derive([by_name(BASE, deep), by_name(A, static(), depth=1), by_name(B, deep)])
    assert unjudged(emit).get(("ObjectBounds", DEPTH)) == [BASE, A, B], f"depth: {emit.get('Unjudged')}"


def case_2176_a_failed_by_name_read_leaves_the_record_unjudged():
    good = [by_name(BASE, static()), by_name(B, static())]
    args_a, body_a = by_name(A, static("45"))
    for label, answer in (("an error answer", ("error", "the plugin could not be opened")),
                          ("an answer that is not JSON", ("text", "error: the plugin could not be opened")),
                          ("an answer without the record", {"form": "everything", "records": []})):
        emit = derive([good[0], (args_a, answer), good[1]])
        u = unjudged(emit)
        assert u.get((None, FAILED)) == [BASE, A, B], f"{label}: {emit.get('Unjudged')}"
        assert lost(emit) == [], f"{label}: compared without the failed version: {lost(emit)}"


def case_2177_tree_answers_list_what_they_cannot_judge():
    # a difference in a field the schema does not name
    emit = derive([tree([(BASE, [], True), (A, [f"Bogus=2 ({B} 1)"], True), (B, None, True)])])
    assert unjudged(emit).get(("Bogus", UNKNOWN)) == [BASE, A, B], f"unknown: {emit.get('Unjudged')}"
    # a plugin the tree could not compare in full: its listed difference is found; any other field is unjudged
    emit = derive([tree([(BASE, [], True), (A, [f"MaxAngle=45 ({B} 30)"], False), (B, None, True)])])
    assert lost(emit) == [("MaxAngle", A, B)], f"the listed difference is not found: {lost(emit)}"
    u = unjudged(emit)
    assert u.get(("Material", TREE_PART)) == [BASE, A, B], f"incomplete: {emit.get('Unjudged')}"
    assert not any(f == "MaxAngle" for f, _ in u), f"the listed field is unjudged too: {u}"
    # a narrowed tree (a cell's own fields): a compared field outside its selection is not read
    emit = derive([tree([(BASE, [], True), (A, ["WaterHeight=10 (ModB.esp 0)"], True), (B, None, True)],
                        rtype="Cell", selection=["WaterHeight"])])
    assert lost(emit) == [("WaterHeight", A, B)], f"narrowed tree: {lost(emit)}"
    assert unjudged(emit).get(("Water", NOT_READ)) == [BASE, A, B], f"narrowed: {emit.get('Unjudged')}"
    # a plugin list cut off at the answer's size limit
    emit = derive([tree([(BASE, [], True), (A, [f"MaxAngle=45 ({B} 30)"], True), (None, None, None)])])
    assert any(f is None and r == TREE_CUT for f, r in unjudged(emit)), f"cut-off nodes: {emit.get('Unjudged')}"


def case_2178_guards_and_the_setup_order():
    # every field read and judged: nothing listed, the lost edit found
    emit = derive([by_name(BASE, static()), by_name(A, static("45")), by_name(B, static())])
    assert lost(emit) == [("MaxAngle", A, B)] and not emit.get("Unjudged"), f"{lost(emit)}; {emit.get('Unjudged')}"
    # a tree's entries for a record the by-name reads judged are not listed (the by-name chain is the one used)
    emit = derive([tree([(BASE, [], True), (A, [f"Bogus=2 ({B} 1)", f"MaxAngle=45 ({B} 30)"], False), (B, None, True)]),
                   by_name(BASE, static()), by_name(A, static("45")), by_name(B, static())])
    assert lost(emit) == [("MaxAngle", A, B)] and not emit.get("Unjudged"), f"superseded: {emit.get('Unjudged')}"
    cut = {"MaxAngle": "45", CUT: CUT_NOTE}
    sel = ["MaxAngle"]
    # a record holding a plugin the setup does not load was read in another setup
    emit = derive([by_name(BASE, static(), sel), by_name(C, cut, sel), by_name(B, static(), sel)])
    assert not emit.get("Unjudged"), f"another setup's record is listed: {emit.get('Unjudged')}"
    # a worldspace's persistent cell is set aside, not unjudged
    emit = derive([by_name(BASE, static(), sel), by_name(A, cut, sel), by_name(B, static(), sel)], containers={FK})
    assert not emit.get("Unjudged"), f"a container is listed: {emit.get('Unjudged')}"
    # the plugins are named in the setup's order (the order-variant setup reverses A and B)
    emit = derive([by_name(BASE, static(), sel), by_name(A, cut, sel), by_name(B, static(), sel)],
                  order=[{"name": n} for n in (BASE, B, A)])
    assert unjudged(emit).get(("MaxAngle", CUT_OFF)) == [BASE, B, A], f"order: {emit.get('Unjudged')}"
    # a tree names its plugins in the order the reader loaded them: the entry follows the setup's order, and a tree
    # holding a plugin the setup does not load lists nothing
    bogus = [(BASE, [], True), (A, [f"Bogus=2 ({B} 1)"], True), (B, None, True)]
    emit = derive([tree(bogus)], order=[{"name": n} for n in (BASE, B, A)])
    assert unjudged(emit).get(("Bogus", UNKNOWN)) == [BASE, B, A], f"tree order: {emit.get('Unjudged')}"
    emit = derive([tree([(BASE, [], True), (C, [f"Bogus=2 ({B} 1)"], True), (B, None, True)])])
    assert not emit.get("Unjudged"), f"another setup's tree record is listed: {emit.get('Unjudged')}"


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
