"""Write bundle.json: the known answers for `runcontrol bundle`, computed by evalrun.py and bundleenrich.py.

Every expected value comes from the real modules: cmd_bundle on synthetic run folders (its refusals, the copies, the
manifest, the names step with a scripted record reader, the order step, --media), order_choice, media_item_refusal,
state_media_fingerprints, bundle_premise_sources, private_instance, the raw plugin walk (filed_cells, _records,
_position, cell_overrides), the reader command line's split and float(). The run folders hold no absolute path (the
commands run from inside them), the clock is a counter and the names step's stopwatch is fixed, so every byte of a
bundle is repeatable. The record reader is a small script started for real; each conversation is recorded line by
line, and the C# self-test replays it and checks it sends the same bytes in the same order.

    python _geometry-harness/runcontrol/known-answers/make_known_answers_bundle.py

Nothing outside a temporary folder is read or written except the harness sources and the output.
"""
from __future__ import annotations

import copy
import json
import os
import platform
import shutil
import struct
import sys
import tempfile
import zlib
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import make_known_answers_finalize as F   # noqa: E402  (the run-folder builders of slice 6a)
import evalrun as E                       # noqa: E402
import bundleenrich as BE                 # noqa: E402

OUT = HERE / "bundle.json"

# ------------------------------------------------------------------------------------------------ plugin files


def sub(sig: bytes, data: bytes) -> bytes:
    return sig + struct.pack("<H", len(data)) + data


def rec(sig: bytes, formid: int, subs: bytes = b"", flags: int = 0, compress: bool = False) -> bytes:
    body = subs
    if compress:
        body, flags = struct.pack("<I", len(subs)) + zlib.compress(subs), flags | 0x40000
    return sig + struct.pack("<IIIIHH", len(body), flags, formid, 0, 44, 0) + body


def grup(label: int, gtype: int, content: bytes) -> bytes:
    return b"GRUP" + struct.pack("<IIi", 24 + len(content), label, gtype) + b"\0" * 8 + content


def tes4(masters=(), flags=0) -> bytes:
    subs = sub(b"HEDR", struct.pack("<fiI", 1.71, 0, 0x800))
    for m in masters:
        subs += sub(b"MAST", m.encode("cp1252") + b"\0") + sub(b"DATA", b"\0" * 8)
    return rec(b"TES4", 0, subs, flags)


def placed(sig, formid, pos=None, flags=0, compress=False, lead=b"") -> bytes:
    subs = sub(b"NAME", struct.pack("<I", 0x101)) + lead
    if pos is not None:
        subs += sub(b"DATA", struct.pack("<ffffff", *pos, 0.0, 0.0, 0.0))
    return rec(sig, formid, subs, flags, compress)


def world(cell_refs: dict, persistent=()) -> bytes:
    """One worldspace 000B00 with its persistent cell 000C00 and exterior cells; cell_refs: {cell formid: [records]}."""
    inner = rec(b"WRLD", 0xB00)
    kids = b""
    if persistent:
        kids += rec(b"CELL", 0xC00) + grup(0xC00, 6, grup(0xC00, 8, b"".join(persistent)))
    for cell, refs in cell_refs.items():
        kids += grup(0, 4, grup(0, 5, rec(b"CELL", cell) + grup(cell, 6, grup(cell, 9, b"".join(refs)))))
    return grup(int.from_bytes(b"WRLD", "little"), 0, inner + grup(0xB00, 1, kids))


def interior(cell: int, refs) -> bytes:
    return grup(int.from_bytes(b"CELL", "little"), 0, grup(0, 2, grup(0, 3, rec(b"CELL", cell) + grup(cell, 6, grup(cell, 9, b"".join(refs))))))


def plugin_files() -> dict:
    base = tes4(flags=0x1) + world({0xC01: [
        placed(b"REFR", 0xD02, (10, 20, 30)), placed(b"REFR", 0xD03, (1.5, 2.25, 100.125), compress=True),
        placed(b"REFR", 0xD04), placed(b"ACHR", 0xD05, (5, 5, 5.0005)), rec(b"NAVM", 0xD77, sub(b"DATA", b"\0" * 12)),
        placed(b"REFR", 0xD07, (0, 0, 7), lead=sub(b"DATA", b"short")), placed(b"REFR", 0xD08, (3, 3, 3.3333))]},
        persistent=[placed(b"REFR", 0xD01, (7, 8, 9))]) + interior(0xC02, [placed(b"REFR", 0xD06, (0.1, 0.2, 0.3))])
    target = tes4(["Base.esm"]) + world({0xC01: [
        placed(b"REFR", 0xD02, (10, 20, 40.26)), placed(b"REFR", 0xD03, (1.5, 2.25, 90), flags=0x800),
        placed(b"REFR", 0xD04, (0, 0, 0), flags=0x20), placed(b"REFR", 0x1000E01, (1, 1, 1)),
        placed(b"REFR", 0xD07, (0, 0, 17.55), compress=True), placed(b"REFR", 0xD08), placed(b"REFR", 0x2000001, (9, 9, 9))]},
        persistent=[placed(b"REFR", 0xD01, (7, 8, 19))]) + interior(0xC02, [placed(b"REFR", 0xD06, (0.1, 0.2, 5.3))])
    other = tes4(["BASE.ESM"]) + world({0xC01: [placed(b"REFR", 0xD02, (10, 20, 25)), placed(b"REFR", 0x1000F01, (2, 2, 2))]})
    patch = tes4(["Base.esm", "Target.esp"]) + world({0xC01: [placed(b"REFR", 0xD02, (10, 20, 31)), placed(b"REFR", 0x1000E01, (1, 1, 2))]})
    return {"Base.esm": base, "Target.esp": target, "Other.esp": other, "Patch.esp": patch, "Solo.esm": tes4(),
            "Flagged.esp": tes4(flags=0x1), "Plain.esp": tes4(), "Light.esp": tes4(flags=0x200), "NotAPlugin.esp": b"GRUP not a plugin",
            "Truncated.esp": tes4(["Base.esm"])[:40]}


# ------------------------------------------------------------------------------------------------ the record reader

FAKE = r'''
import json, os, sys
table = json.load(open(sys.argv[1], encoding="utf-8"))
mode = dict(a.split("=", 1) for a in sys.argv[2:])
wire = open(os.environ["FAKE_WIRE"], "a", encoding="utf-8", newline="")
calls = 0
def say(obj, raw=None):
    line = raw if raw is not None else json.dumps(obj)
    wire.write(json.dumps({"recv": line}) + "\n"); wire.flush()
    sys.stdout.write(line + "\n"); sys.stdout.flush()
for raw in sys.stdin.buffer:
    wire.write(json.dumps({"sent": raw.decode("utf-8")}) + "\n"); wire.flush()
    msg = json.loads(raw)
    if "id" not in msg:
        continue
    if msg["method"] == "initialize":
        if mode.get("die") == "0":
            sys.exit(0)
        say({"jsonrpc": "2.0", "method": "notifications/message", "params": {"level": "info"}})
        say({"result": {"protocolVersion": "2025-06-18", "serverInfo": {"name": "scripted", "version": "1"}}, "id": msg["id"], "jsonrpc": "2.0"})
        continue
    calls += 1
    if mode.get("die") == str(calls):
        sys.exit(0)
    if mode.get("garbage") == str(calls):
        say(None, raw="this is not a message")
        continue
    args = msg["params"]["arguments"]
    src = args["source"]
    name = src.replace("\\", "/").rsplit("/", 1)[-1]
    rows = table.get(name, {})
    recs = [dict(rows[fk], formid=fk) if fk in rows else {"formid": fk, "error": "file '%s' does not define or override %s" % (src, fk)}
            for fk in args["formids"]]
    body = {"form": "fields", "source": "%s \u2014 OUT-OF-LOAD-ORDER (direct path; NOT active \u2014 no MO2 layer was found providing this exact path)" % src,
            "count": len(recs), "epoch": "scripted", "records": recs, "truncated": False, "render_ms": 1.5, "big": 12345678901234567890}
    if mode.get("active") == str(calls):
        body["source"] = "%s (active in the load order at index 03)" % name
    if mode.get("short") == str(calls):
        body["records"] = recs[:-1]
    if mode.get("truncated") == str(calls):
        body["truncated"] = True
    if mode.get("spilled") == str(calls):
        body["spilled"] = "a file"
    text = json.dumps(body, indent=2)
    if mode.get("notok") == str(calls):
        text = json.dumps({"ok": False, "error": "the source is not a plugin \u2014 " + "x" * 400})
    if mode.get("plain") == str(calls):
        text = "no JSON here, only words"
    if mode.get("badjson") == str(calls):
        text = "{ this is not JSON"
    half = len(text) // 2
    say({"result": {"content": [{"type": "text", "text": text[:half]}, {"type": "image"}, {"type": "text", "text": text[half:]}]},
         "id": msg["id"], "jsonrpc": "2.0", "meta": {"ms": 1.5, "n": 12345678901234567890, "e": 1e-07, "dash": "\u2014", "path": "C:\\x"}})
'''


def fields(**kw) -> list:
    """Reader field rows: a value, or "(absent)" for None."""
    return [{"path": k.replace("__", "."), "note": "(absent)"} if v is None else {"path": k.replace("__", "."), "value": v} for k, v in kw.items()]


def reader_table() -> dict:
    ref = "PlacedObject"
    return {
        "Base.esm": {
            "000D01:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000101:Base.esm", Scale=None, Placement__Position="7,8,9")},
            "000D02:Base.esm": {"type": ref, "editorid": "RefTwo", "fields": fields(Base="000101:Base.esm", Scale="0.5", Placement__Position="10,20,30")},
            "000D03:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000102:Base.esm", Scale="", Placement__Position="1.5,2.25,100.125,0,0,0")},
            "000D05:Base.esm": {"type": "PlacedNpc", "editorid": None, "fields": fields(Base="000104:Base.esm", Scale=2)},
            "000D06:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="not a formkey", Scale="1e1")},
            "000D07:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000103:Base.esm", Placement__Position="x,y,z")},
            "000D08:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000105:Other.esp", Scale=" 1_0.5 ")},
            "000C01:Base.esm": {"type": "Cell", "editorid": "CellOne", "fields": fields(Name="Cell One")},
            "000C02:Base.esm": {"type": "Cell", "editorid": None, "fields": []},
            "000101:Base.esm": {"type": "Static", "editorid": "StaticOne", "fields": fields(Name="Rock", Model__File="rock.nif", ObjectBounds__First="-1,-2,-3", ObjectBounds__Second="1,2,3.5")},
            "000102:Base.esm": {"type": "Light", "editorid": "LightTwo", "fields": fields(Name="Lantern", Model__File=None, ObjectBounds__First="a,b,c", ObjectBounds__Second="1,2,3")},
            "000103:Base.esm": {"type": "Static", "editorid": None, "fields": fields(Name="Nameless id")},
            "000104:Base.esm": {"type": "Npc", "editorid": "AnActor", "fields": fields(Name="An actor")},
            "000D04:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000101:Base.esm")},
            "00FFFF:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000101:BASE.esm", Scale="0.25")},
            "0000F1:Base.esm": {"type": "Quest", "editorid": "MyQuest", "fields": fields(Name="The Quest")},
            "0000F2:Base.esm": {"type": "Quest", "editorid": "AnotherEditorId", "fields": fields(Name="Wrong owner")},
        },
        "Target.esp": {
            "000D02:Base.esm": {"type": ref, "editorid": None, "fields": fields(Base="000101:Base.esm", Scale=None, Placement__Position="10,20,40.26")},
            "000D03:Base.esm": {"type": ref, "editorid": None, "fields": fields(Placement__Position="1.5,2.25,90")},
            "000E01:Target.esp": {"type": ref, "editorid": "NewRef", "fields": fields(Base="000201:Target.esp", Scale="1.25", Placement__Position="1,1,1")},
            "000201:Target.esp": {"type": "Static", "editorid": "TargetStatic", "fields": fields(Name="Target thing", Model__File="t.nif")},
            "000101:Base.esm": {"type": "Static", "editorid": "StaticOne", "fields": fields(Name="Rock (renamed)", ObjectBounds__First="0,0,0", ObjectBounds__Second="2,2,2")},
            "000C01:Base.esm": {"type": "Cell", "editorid": "CellOne", "fields": []},
            "0000F1:Target.esp": {"type": "Quest", "editorid": "NotIt", "fields": fields(Name="A new quest")},
            "0000F1:Base.esm": {"type": "Quest", "editorid": "MyQuest", "fields": fields(Name="The Quest, retold")},
        },
        "Other.esp": {
            "000D02:Base.esm": {"type": ref, "editorid": None, "fields": fields(Placement__Position="10,20,25")},
            "000105:Other.esp": {"type": "Static", "editorid": "OtherStatic", "fields": fields(Name="Other thing")},
        },
        "Patch.esp": {
            "000D02:Base.esm": {"type": ref, "editorid": None, "fields": fields(Placement__Position="10,20,31")},
            "000E01:Target.esp": {"type": ref, "editorid": None, "fields": fields(Scale="nope")},
        },
    }


# ------------------------------------------------------------------------------------------------ the run folder

def relative(obj, root: Path):
    """The same value with the run folder's own absolute spelling taken off, so the folder names no place."""
    if isinstance(obj, str):
        for form in (str(root) + "\\", str(root).replace("\\", "/") + "/"):
            obj = obj.replace(form, "")
        return obj
    if isinstance(obj, dict):
        return {k: relative(v, root) for k, v in obj.items()}
    if isinstance(obj, list):
        return [relative(v, root) for v in obj]
    return obj


def build_named(root: Path) -> None:
    """A finalized run folder whose rows name objects of four small plugins, with a record import, a sweep, logs, a
    decision, a state check and a switch census; every stored path is relative to the folder."""
    files = plugin_files()
    for name, data in files.items():
        F.write(root, f"game/{name}", data)

    def req(*names):
        return {"plugins": [{"name": n, "path": f"game/{n}", "sha256": F.sha(files[n])} for n in names]}
    both = ("Base.esm", "Solo.esm", "Flagged.esp", "Target.esp", "Other.esp", "Plain.esp")
    arms = [{"armId": "baseline", "kind": "baseline", "candidateFile": "candidate-00-baseline.json", "loadedInGameNote": "not loaded",
             "payload": {"espOnly": False}, "plugins": list(both), "requested": req(*both), "candidatePath": "candidates/candidate-00-baseline.json"},
            {"armId": "candidate-01", "kind": "candidate", "candidateFile": "candidate-01-patch.json", "loadedInGameNote": "not loaded",
             "payload": {"espOnly": False}, "plugins": list(both) + ["Patch.esp"], "requested": req(*both, "Patch.esp"),
             "candidatePath": "candidates/candidate-01-patch.json"}]
    checks = [F.check_row("g.overlap", "probe-json", exe="overlapprobe"),
              F.check_row("state.conditional-geometry", "probe-emit", exe="stateprobe", family="state", arms=("baseline",)),
              F.check_row("s.switch", "probe-json", exe="depprobe", arms=("baseline",), result={"kind": "catalogue"}),
              F.check_row("r.contested", "mcp-import", family="records"),
              F.check_row("e.regen", "manual", family="environment", manualDecisionAllowed=True, arms=("baseline",)),
              F.check_row("z.rejected", "probe-json", exe="depprobe", arms=("baseline",), required=False)]
    plan = F.run_plan(root, checks, arms=arms, targets=[{"name": "Target.esp", "nexusId": 1234}],
                      search={"path": None, "sha256": None, "absent": "the search step was not run"})
    reg = F.write(root, "premises/registry.json", json.dumps({"registryVersion": 7, "checks": [
        {"id": "r.contested", "family": "records"}, {"id": "g.overlap", "family": "geometry"}, "odd"]}))
    scope = F.write(root, "premises/scope.json", json.dumps({"complete": True, "targets": [
        {"name": "Target.esp", "footprint": {"nexusId": 1234, "placed": {"cells": [
            {"cell": "000C01:Base.esm", "name": "CellOneFromScope"}, {"cell": "000C02:Base.esm", "name": "CellTwoFromScope"},
            {"cell": "000C09:Base.esm", "name": None}, {"cell": "not a key", "name": "x"}, {"name": "no cell"}]}}},
        {"name": "Other.esp", "footprint": None}, {"name": "Patch.esp", "footprint": {"nexusId": 99}}]}))
    plan["registry"].update(path="premises/registry.json", sha256=F.sha(reg.read_bytes()))
    plan["scope"].update(path="premises/scope.json", sha256=F.sha(scope.read_bytes()))
    plan["candidatesDir"] = "candidates"
    plan = relative(plan, root)
    F.write(root, "nexus/pages/1234/snapshot.json", json.dumps({"name": "Target Mod (catalogue)"}))
    F.write(root, "nexus/pages/99/snapshot.json", json.dumps({"title": "no name key"}))
    for n in ("candidate-00-baseline.json", "candidate-01-patch.json", "candidates.json"):
        F.write(root, f"candidates/{n}", json.dumps({"File": n}))
    F.write(root, "candidates/deep/note.txt", "kept: any file under candidates is copied\n")
    F.write(root, "candidates/Skyrim.esm", b"TES4 never copied")
    b = F.Builder(root, plan)
    heads = [{"Name": "Base.esm", "Sha256": F.sha(files["Base.esm"]).upper(), "Masters": [], "MasterFlag": True, "LightFlag": False},
             {"Name": "Target.esp", "Sha256": F.sha(files["Target.esp"]), "Masters": ["Base.esm"], "MasterFlag": False},
             {"Name": "Other.esp", "Sha256": F.sha(files["Other.esp"]), "Masters": ["BASE.ESM"], "MasterFlag": 0},
             {"Name": "Patch.esp", "Sha256": F.sha(files["Patch.esp"]), "Masters": ["Base.esm", "Target.esp"], "MasterFlag": False},
             {"Name": "Solo.esm", "Sha256": "0" * 64, "Masters": [], "MasterFlag": True},
             {"Name": "NoMasters.esp", "Sha256": "1" * 64, "Masters": None}, {"Name": "", "Sha256": "2" * 64, "Masters": []}, "odd"]
    rows = {"Probe": "overlapprobe", "Configuration": {"Plugins": heads}, "Conflicts": [
        {"Kind": "OVERLAP", "Field": "Placement", "A": "Target.esp", "B": "Other.esp",
         "Necessary": [{"FormKey": "000D02:Base.esm"}, {"FormKey": "000E01:Target.esp"}, "odd", {"FormKey": None}],
         "Alternative": [{"FormKey": "000D03:Base.esm"}, {"FormKey": "not a key"}]},
        {"Kind": "STATE-ON@000A01:Base.esm@when the gate opens", "Necessary": [{"FormKey": "000D01:Base.esm"}, {"FormKey": "000D05:Base.esm"}],
         "Alternative": None},
        {"Kind": "STATE-off@000A02:Base.esm@lower case is no state", "Field": "Placement", "Necessary": [{"FormKey": "000D07:Base.esm"}]},
        {"Kind": None, "Necessary": [{"FormKey": "000D06:Base.esm"}, {"FormKey": "000D08:Base.esm"}, {"FormKey": "00FFFF:Base.esm"},
                                     {"FormKey": "000D04:Base.esm"}]}],
        "Predicates": [{"FormKey": "000D02:Base.esm", "Other": "000E01:Target.esp"}, {"FormKey": "bad", "Other": None}, "odd"]}
    for aid in ("baseline", "candidate-01"):
        out = F.emit(root, f"outputs/g.overlap/{aid}.json", rows if aid == "baseline" else {"Probe": "overlapprobe", "Conflicts": []})
        cfg = {"fingerprint": ("a" if aid == "baseline" else "b") * 64, "schemaVersion": 1, "population": "load-time+population:0123456789ab",
               "state": "baseline", "plugins": [{"name": "Base.esm", "sha256": F.sha(files["Base.esm"])}, {"name": "Target.esp", "sha256": None}],
               "providers": [{"kind": "mod", "name": "Target Mod"}, {"kind": None, "name": None}], "overlays": [{"kind": "bos-ini", "sha256": "e" * 64}]}
        F.write(root, f"logs/overlap-{aid}.txt", f"$ overlapprobe {aid}\n(exit 0, 0.1s)\n")
        b.settle("g.overlap", aid, None, "completed", detector="finding" if aid == "baseline" else "no-finding", outputs=[out],
                 consumed={"configuration": cfg}, result={"findings": 3 if aid == "baseline" else 0},
                 extra={"log": f"logs/overlap-{aid}.txt"})
    # a reverse part: measured on the other order, so its fingerprint is not the arm's own
    rev = F.emit(root, "outputs/g.overlap/candidate-01.reverse.json", {"Probe": "overlapprobe", "Configuration": {"Plugins": [
        {"Name": "Target.esp", "Sha256": F.sha(files["Target.esp"]), "Masters": ["never read: a reverse emit"], "MasterFlag": True}]}, "Conflicts": []})
    F.write(root, "sweep/inv-0001/logs/reverse.txt", "$ overlapprobe reverse\n(exit 0, 0.2s)\n")
    b.settle("g.overlap", "candidate-01", "reverse", "completed", detector="no-finding", outputs=[rev],
             consumed={"configuration": {"fingerprint": "c" * 64}}, result={"findings": 0}, extra={"log": "sweep/inv-0001/logs/reverse.txt"})
    for rel, text in (("sweep/inv-0001/summary.json", "{}"), ("sweep/inv-0001/jobs.jsonl", ""), ("sweep/inv-0001/SUMMARY.md", "# sweep\n"),
                      ("sweep/inv-0001/overlapprobe/pairs.jsonl.gz", "not copied"), ("sweep/inv-0001/cache.bin", "not copied"),
                      ("sweep/inv-0001/Notes.TXT", "copied: the suffix is matched without case\n")):
        F.write(root, rel, text)
    st = F.emit(root, "outputs/state.conditional-geometry/baseline.json", {"Probe": "stateprobe", "Conflicts": [
        {"Kind": "STATE-ON@000A01:Base.esm@gate", "Necessary": []}, {"Kind": "OVERLAP"}, "odd" and {"Kind": 5}]})
    b.settle("state.conditional-geometry", "baseline", None, "completed", detector="finding", outputs=[st], result={"findings": 1},
             consumed={"configuration": {"fingerprint": "a" * 64, "schemaVersion": 1, "population": "load-time", "state": "baseline",
                                         "plugins": [{"name": "Base.esm", "sha256": F.sha(files["Base.esm"])}]}})
    sw = F.emit(root, "outputs/s.switch/baseline.json", {"switches": [
        {"key": "000A01:Base.esm", "trigger": {"by": ["quest MyQuest stage 10 (QF_MyQuest_010000f1.Fragment_0)", "a lever",
                                                      "quest Orphan stage 5 (QF_Orphan_020000F9.Fragment_1)",
                                                      "quest AnotherQuest stage 1 (QF_AnotherQuest_000000F2.Fragment_0)", 7]}},
        {"key": "000A77:Base.esm", "trigger": {"by": ["quest Unmeasured stage 1 (QF_Unmeasured_000000F3.Fragment_0)"]}},
        {"key": "000A01:Base.esm", "trigger": None}, "odd"]})
    b.settle("s.switch", "baseline", None, "completed", detector="catalogue", outputs=[sw], result={"findings": 2})
    # record imports: one whose setup has the patch between the pair, one that loads the two master files
    for aid, names in (("baseline", ("Base.esm", "Target.esp", "Patch.esp", "Other.esp")), ("candidate-01", ("Base.esm", "Solo.esm", "Flagged.esp"))):
        folder = f"imports/r.contested__{aid}"
        res = F.write(root, f"{folder}/result.json", json.dumps({"schemaVersion": 1, "outputs": [{"role": "request"}, {"role": "response"}],
                                                                "configuration": req(*names) if aid == "baseline" else None}))
        q = F.write(root, f"{folder}/01-request.jsonl", '{"id": 1}\n')
        a = F.write(root, f"{folder}/02-response.jsonl", '{"id": 1, "result": {}}\n')
        b.settle("r.contested", aid, None, "completed", detector="no-finding", adapter={"kind": "mcp-import"},
                 outputs=[p.relative_to(root).as_posix() for p in (res, q, a)], result={"findings": 0},
                 consumed={} if aid == "baseline" else {"configuration": req(*names)},
                 extra={"imported": {"origin": "fresh", "capturedUtc": "2026-01-02T00:00:00Z"}}, ledger_extra={"imported": True, "importOrigin": "fresh"})
    b.settle("e.regen", "baseline", None, "manual", detail="a person decides this one")
    F.write(root, "outputs/z.rejected/baseline.nif", b"a mesh: named by a receipt, never copied")
    b.settle("z.rejected", "baseline", None, "rejected(exit-nonzero)", cause="exit-nonzero", accepted=False,
             outputs=[F.emit(root, "outputs/z.rejected/baseline.json", {"rows": []}), "outputs/z.rejected/baseline.nif"], rejections=[{"cause": "exit-nonzero", "detail": "exit 3"}])
    F.write(root, "decisions/evidence/regen-note.txt", "LOD regenerated on 2026-01-03\n")
    ok = {"checkId": "e.regen", "armId": "baseline", "decidedBy": "Reviewer", "decidedUtc": "2026-01-03T00:00:00Z",
          "decision": "completed-no-finding", "note": "regenerated", "evidence": ["decisions/evidence/regen-note.txt"]}
    F.write(root, "decisions/01-accepted.json", json.dumps(ok))
    F.write(root, "decisions/02-bad-word.json", json.dumps(dict(ok, decision="fine")))
    F.write(root, "media/a.png", b"a picture")
    F.write(root, "media/clip.mp4", b"a clip")
    F.write(root, "media/source.mkv", b"the recording")
    F.write(root, "media/model.nif", b"a mesh is never media")
    with F.fake_clock(), F.captured(), F.cwd(root):
        code = E.cmd_finalize(SimpleNamespace(run="."))
    assert code == 0, code
    shutil.rmtree(root / "runlog", ignore_errors=True)


# ------------------------------------------------------------------------------------------------ the bundle command

class Stopwatch:
    """evalrun's `time` with a fixed stopwatch: the names step always took 3.25 seconds."""

    def __init__(self):
        self.calls = 0

    def monotonic(self):
        self.calls += 1
        return 100.0 if self.calls % 2 else 103.25

    def __getattr__(self, name):
        import time
        return getattr(time, name)


def out_tree(out: Path, root: Path) -> list:
    """A bundle folder: each file's hash and whether it is a hard link; the files bundle itself writes also as text."""
    rows = []
    for p in sorted(out.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(out).as_posix()
        row = {"path": rel, "sha256": F.sha(p.read_bytes()), "linked": p.stat().st_nlink > 1}
        if rel == "verify.json":
            row = {"path": rel, "text": F.pack(F.mask(p.read_bytes().decode("utf-8"), root))}
        elif rel == "manifest.json" or rel.startswith("enrichment/"):
            row["text"] = F.pack(p.read_bytes().decode("utf-8"))
        rows.append(row)
    return rows


def command_cases(tmp: Path) -> dict:
    base = tmp / "named"
    build_named(base)
    reader = tmp / "reader"
    F.write(tmp, "reader/fake.py", FAKE)
    F.write(tmp, "reader/table.json", json.dumps(reader_table()))
    table2 = reader_table()
    table2["Patch.esp"]["000E01:Target.esp"]["fields"] = fields(Scale="1.5")
    F.write(tmp, "reader/table2.json", json.dumps(table2))
    table3 = copy.deepcopy(table2)
    del table3["Base.esm"]["000D06:Base.esm"], table3["Base.esm"]["000D08:Base.esm"], table3["Base.esm"]["000102:Base.esm"]
    table3["Base.esm"]["000104:Base.esm"]["type"] = None
    F.write(tmp, "reader/table3.json", json.dumps(table3))
    table4 = copy.deepcopy(table2)
    table4["Base.esm"]["00FFFF:Base.esm"]["fields"] = fields(Base="000101:Base.esm", Scale="0.25")
    F.write(tmp, "reader/table4.json", json.dumps(table4))
    os.environ["HOUSECARL_NEXUS_CATALOG_DIR"] = "nexus"
    cases = []
    n = [0]

    def case(name, change=None, args=(), table="table2.json", mode="", reader_cmd="scripted", intended=None, runtime_text=False, pre=None):
        v = F.Variant(tmp, base, "bundle " + name)
        if change:
            change(v)
        n[0] += 1
        wire = reader / f"wire-{n[0]}.log"
        os.environ["FAKE_WIRE"] = str(wire)
        cmd = f"python ../../../reader/fake.py ../../../reader/{table} {mode}".strip() if reader_cmd == "scripted" else reader_cmd
        a = SimpleNamespace(run=".", out=None, media=None, registry=None, scope=None, search=None, no_names=False,
                            reader_cmd=cmd, reader_env=["HouseCarl__DataDir=names-instance/Data", "HOUSECARL_DATA_DIR=names-instance/data", "odd"],
                            mod_name=None)
        argv = []
        it = iter(args)
        for flag in it:
            key = flag[2:].replace("-", "_")
            if flag == "--no-names":
                a.no_names = True
                argv.append(flag)
                continue
            if flag == "--no-reader-env":
                a.reader_env = None
                continue
            value = next(it)
            argv += [flag, value]
            if key in ("mod_name", "reader_env"):
                setattr(a, key, (getattr(a, key) or []) + [value])
            else:
                setattr(a, key, value)
        if "--no-reader-env" not in args:
            argv = ["--reader-env", "HouseCarl__DataDir=names-instance/Data", "--reader-env", "HOUSECARL_DATA_DIR=names-instance/data",
                    "--reader-env", "odd"] + argv
        if cmd is not None:
            argv = ["--reader-cmd", cmd] + argv
        if pre:
            pre(v)
        real_time, E.time = E.time, Stopwatch()
        crash = None
        try:
            with F.fake_clock(), F.captured() as (o, e), F.cwd(v.root):
                try:
                    code = E.cmd_bundle(a)
                except Exception as ex:   # where Python stops with a traceback
                    code, crash = 1, f"{type(ex).__name__}: {ex}"
        finally:
            E.time = real_time
        out = v.root / (a.out or "bundle")
        talk = [json.loads(line) for line in wire.read_text(encoding="utf-8").splitlines()] if wire.exists() else []
        cases.append({"name": name, "ops": v.ops, "args": argv, "exit": code, "crash": crash, "stdout": F.mask(o.getvalue(), v.root),
                      "stderr": F.mask(e.getvalue(), v.root), "out": a.out or "bundle", "tree": out_tree(out, v.root) if out.is_dir() else None,
                      "wire": talk, "intended": intended, "runtimeText": runtime_text})
        return cases[-1]

    def targets(*names):
        return lambda v: v.edit("plan.json", lambda d: d.update(targets=[{"name": x} for x in names]))

    def both(*changes):
        return lambda v: [c(v) for c in changes]

    # the names step
    case("names, read with the scripted reader")
    case("names with a declared mod name and a named out folder", args=("--mod-name", " Target.esp = The Target Mod ", "--mod-name", "Patch.esp=", "--out", "named-out"))
    case("names: every object and base read (enriched)", table="table4.json")
    case("names skipped", args=("--no-names",))
    case("names: the default private instance", args=("--no-reader-env",))
    case("names: a base and two objects unread (partial)", table="table3.json")
    case("names: a scale that is no number", table="table.json")
    case("names: the review arm is the candidate", lambda v: v.edit("plan.json", lambda d: d.update(reviewArm="candidate-01")))
    case("names: a review arm the plan does not have", lambda v: v.edit("plan.json", lambda d: d.update(reviewArm="nowhere")))
    case("names: a plugin file changed since the plan", lambda v: v.put("game/Target.esp", "changed"))
    case("names: a plugin outside the read order is gone", lambda v: v.delete("game/Other.esp"), args=("--mod-name", "Target.esp=T"))
    case("names: no baseline arm, so the first arm", lambda v: v.edit("plan.json", lambda d: [a.update(kind="candidate") for a in d["arms"]]))
    case("names: the reader leaves before initialize", mode="die=0")
    case("names: the reader leaves mid-conversation", mode="die=3")
    case("names: the reader answers from its active order", mode="active=2")
    case("names: an answer one record short", mode="short=1")
    case("names: a truncated answer", mode="truncated=4")
    case("names: a spilled answer", mode="spilled=2")
    case("names: the reader says not ok", mode="notok=1")
    case("names: an answer that is not JSON text", mode="plain=2")
    case("names: an answer of broken JSON", mode="badjson=1", runtime_text=True)
    case("names: a line that is not a message", mode="garbage=2", runtime_text=True)
    case("names: a reader command with an unclosed quote", reader_cmd='"python fake.py')
    case("names: a reader that does not exist", reader_cmd='"no-such-record-reader.exe" --stdio')
    case("names: the bundle does not verify first", lambda v: v.edit("disposition.json", lambda d: d["evaluation"]["findings"].append(
        {"checkId": "no.such", "armId": "baseline", "part": None})))
    # the order step
    case("order: forced by a master", targets("Target.esp", "Patch.esp"), args=("--no-names",))
    case("order: forced by a master, the other way round", targets("Patch.esp", "TARGET.ESP"), args=("--no-names",))
    case("order: forced by the master file's extension", targets("Other.esp", "Solo.esm"), args=("--no-names",))
    case("order: forced by the master flag, read from the file", targets("Flagged.esp", "Plain.esp"), args=("--no-names",))
    case("order: free, both master files, a record import loads both", targets("Solo.esm", "Flagged.esp"), args=("--no-names",),
         intended="this controller does not derive record rows")
    case("order: free, the swap would load a plugin before its master", targets("Target.esp", "Other.esp"), args=("--no-names",))
    case("order: free, no record import loads both", targets("Other.esp", "Plain.esp"), args=("--no-names",))
    case("order: a plugin no setup names", targets("Target.esp", "Nowhere.esp"), args=("--no-names",))
    case("order: three evaluated plugins", targets("Target.esp", "Other.esp", "Plain.esp"), args=("--no-names",))
    case("order: no evaluated plugin", lambda v: v.edit("plan.json", lambda d: d.update(targets=["odd", {"name": ""}])), args=("--no-names",))
    case("order: the file changed and no emit recorded it", both(targets("Plain.esp", "Flagged.esp"), lambda v: v.put("game/Plain.esp", "changed")),
         args=("--no-names",))
    case("order: a file that is not a plugin", both(targets("Plain.esp", "Flagged.esp"), lambda v: (
        v.put("game/Plain.esp", "GRUP not a plugin"),
        v.edit("plan.json", lambda d: [p.update(sha256=F.sha(b"GRUP not a plugin")) for a in d["arms"] for p in a["requested"]["plugins"] if p["name"] == "Plain.esp"]))),
        args=("--no-names",))
    # refusals before anything is written
    case("refused: no plan", lambda v: v.delete("plan.json"))
    case("refused: a --mod-name without =", args=("--mod-name", "Target.esp"))
    case("refused: the ledger is gone", lambda v: v.delete("ledger.json"))
    case("refused: the ledger of another plan", lambda v: v.edit("ledger.json", lambda d: d.update(planId="other")))
    for name in ("plan.sha256", "journal.jsonl", "STATUS.md"):
        if name != "plan.sha256":
            case(f"refused: control file {name} missing", lambda v, name=name: v.delete(name))
    case("refused: no disposition", lambda v: v.delete("disposition.json"))
    case("refused: a disposition that is not an object", lambda v: v.put("disposition.json", "[]"))
    case("refused: the disposition of another plan", lambda v: v.edit("disposition.json", lambda d: d.update(planId="other")))
    case("refused: a refused disposition", lambda v: v.edit("disposition.json", lambda d: d["evaluation"].update(refused="registry-changed")))
    case("refused: a stale disposition (the journal grew)", lambda v: v.put("journal.jsonl", (v.root / "journal.jsonl").read_text(encoding="utf-8") + "{}\n"))
    case("refused: a stale disposition with no derivedFrom", lambda v: v.edit("disposition.json", lambda d: d.pop("derivedFrom")))
    case("refused: a settled entry's receipt is gone", lambda v: (
        v.delete("receipts/s.switch__baseline.json"),
        v.edit("disposition.json", lambda d: d.update(derivedFrom=E.derived_from(v.root)))))
    case("refused: a receipt output changed after finalize", lambda v: v.put("outputs/g.overlap/baseline.json", "changed"))
    case("refused: a receipt output gone", lambda v: v.delete("outputs/s.switch/baseline.json"))
    case("refused: a receipt that is not the ledger's", lambda v: (
        v.edit("receipts/s.switch__baseline.json", lambda d: d.update(receiptId="another")),
        v.edit("disposition.json", lambda d: d.update(derivedFrom=E.derived_from(v.root)))))
    case("refused: a receipt bound to another probe build", lambda v: (
        v.edit("receipts/g.overlap__baseline.json", lambda d: d["consumed"]["programs"]["emitter"].update(digest="folder:" + "9" * 64)),
        v.edit("disposition.json", lambda d: d.update(derivedFrom=E.derived_from(v.root)))))
    case("a decision whose evidence is gone is no longer accepted", lambda v: v.delete("decisions/evidence/regen-note.txt"))
    case("an accepted receipt's output with a binary suffix is never copied", lambda v: (
        v.edit("receipts/s.switch__baseline.json", lambda d: d["outputs"].append(
            {"path": "media/model.nif", "sha256": F.sha(b"a mesh is never media"), "bytes": 21, "mtimeUtc": "2026-01-01T00:00:00Z"})),
        v.edit("disposition.json", lambda d: d.update(derivedFrom=E.derived_from(v.root)))))
    case("refused: the registry changed", lambda v: v.put("premises/registry.json", "changed"))
    case("refused: the scope is gone", lambda v: v.delete("premises/scope.json"))
    case("refused: a named registry that does not hash to the plan", lambda v: v.put("elsewhere/registry.json", "other"),
         args=("--registry", "elsewhere/registry.json"))
    case("refused: a named scope that does not exist", args=("--scope", "elsewhere/scope.json"))
    case("refused: a named search the plan did not freeze", args=("--search", "candidates/candidates.json"))
    case("named premises in place of changed ones", lambda v: (
        shutil.copytree(v.root / "premises", v.root / "kept"), v.ops.append({"op": "copytree", "from": "premises", "path": "kept"}),
        v.put("premises/registry.json", "changed"), v.delete("premises/scope.json")),
        args=("--registry", "kept/registry.json", "--scope", "kept/scope.json", "--no-names"))
    # the candidate search
    case("search: a frozen search is copied", lambda v: v.edit("plan.json", lambda d: d.update(
        search={"path": "candidates/candidates.json", "sha256": F.sha((v.root / "candidates/candidates.json").read_bytes())})), args=("--no-names",))
    case("search: recorded absent with no reason", lambda v: v.edit("plan.json", lambda d: d.update(search=None)), args=("--no-names",))
    case("search: a plan from before the frozen search, copied unchecked", lambda v: v.edit("plan.json", lambda d: d.pop("search")), args=("--no-names",))
    case("search: a plan from before the frozen search, nothing to copy", lambda v: (
        v.edit("plan.json", lambda d: d.pop("search")), v.delete("candidates/candidates.json")), args=("--no-names",))
    case("a plan with no scope", lambda v: v.edit("plan.json", lambda d: d.update(scope={"path": None, "sha256": None})), args=("--no-names",))
    case("an out folder that already holds a bundle", lambda v: (v.put("bundle/manifest.json", "{}"), v.put("bundle/left-over.txt", "old"),
                                                                 v.put("bundle/plan.json", "an older copy")), args=("--no-names",))
    # media
    arm_fp = "a" * 64
    cfg = E.load_json(base / "receipts" / "g.overlap__baseline.json")["consumed"]["configuration"]
    state_fp = E.configuration_fingerprint(cfg["plugins"], cfg["providers"], cfg["overlays"], "load-time", "000A01:Base.esm", 1)
    good = {"path": "media/a.png", "mediaKind": "offline-render", "subject": {"checkId": "g.overlap", "armId": "baseline", "description": "the clash"},
            "state": "baseline", "configurationFingerprint": arm_fp, "fingerprintSource": "emitted",
            "provenance": {"tool": "viewprobe", "capturedUtc": "2026-01-04T00:00:00Z"}, "caption": "kept on the row", "id": "never kept", "sha256": "never kept"}
    items = [good, dict(good, path="media/clip.mp4", mediaKind="game-video", state="000A01:Base.esm", configurationFingerprint=state_fp,
                        sourceInterval={"sourcePath": "media/source.mkv", "sourceSha256": F.sha(b"the recording"), "start": 1.5},
                        original={"path": "media/a.png", "sha256": F.sha(b"a picture")}),
             dict(good), dict(good, path="media/missing.png"), {k: v for k, v in good.items() if k != "mediaKind"},
             dict(good, subject=dict(good["subject"], armId="candidate-01"), configurationFingerprint="c" * 64),
             dict(good, configurationFingerprint="d" * 64)]
    case("media: accepted and unassessed items", lambda v: v.put("media.json", json.dumps({"items": items})), args=("--media", "media.json", "--no-names"))
    case("media: a list file", lambda v: v.put("media.json", json.dumps([good])), args=("--media", "media.json", "--no-names"))
    case("media: not JSON", lambda v: v.put("media.json", "{"), args=("--media", "media.json"))
    case("media: an item that is not an object", lambda v: v.put("media.json", json.dumps([good, "a string item " * 12])), args=("--media", "media.json"))
    case("media: a binary suffix", lambda v: v.put("media.json", json.dumps([dict(good, path="media/model.nif")])), args=("--media", "media.json"))
    case("media: a file with no items", lambda v: v.put("media.json", "{}"), args=("--media", "media.json", "--no-names"))
    return {"base": F.tree(base), "cases": cases, "tool": [{"file": "bundleenrich.py", "sha256": BE.sha(F.HARNESS / "bundleenrich.py")}],
            "catalogue": "nexus"}


# ------------------------------------------------------------------------------------------------ rules called directly

def order_cases() -> list:
    def head(masters, master=None):
        return {"masters": masters, "master": master}
    table = [
        ("a plugin and the master it lists", ["A.esp", "B.esp"], {"a.esp": head(["B.esp"]), "b.esp": head([])}),
        ("the listed master named in another case", ["A.esp", "B.esp"], {"a.esp": head([]), "b.esp": head(["a.ESP"])}),
        ("each lists the other", ["A.esp", "B.esp"], {"a.esp": head(["b.esp"]), "b.esp": head(["A.esp"])}),
        ("a master file by flag and a plain plugin", ["A.esp", "B.esp"], {"a.esp": head([], True), "b.esp": head([], False)}),
        ("a master file by extension", ["A.esp", "B.esm"], {"a.esp": head([]), "b.esm": head([])}),
        ("a light master by extension", ["A.ESL", "B.esp"], {"a.esl": head([]), "b.esp": head([])}),
        ("a light flag alone is no master file", ["A.esp", "B.esp"], {"a.esp": head([]), "b.esp": {"masters": [], "master": False, "light": True}}),
        ("two master files", ["A.esm", "B.esp"], {"a.esm": head([]), "b.esp": head([], 1)}),
        ("two plain plugins", ["A.esp", "B.esp"], {"a.esp": head(None), "b.esp": head([])}),
        ("a master relation beats the master flag", ["A.esm", "B.esp"], {"a.esm": head(["B.esp"]), "b.esp": head([])}),
        ("equal names in another case", ["Same.esp", "SAME.ESP"], {"same.esp": head([])}),
        ("a name that lists itself", ["A.esp", "B.esp"], {"a.esp": head(["A.esp"]), "b.esp": head([])}),
        ("the first has no facts", ["A.esp", "B.esp"], {"b.esp": head([])}),
        ("the second has no facts", ["A.esp", "B.esp"], {"a.esp": head([])}),
        ("neither has facts", ["A.esp", "B.esp"], {}),
        ("a master that is not text", ["A.esp", "7"], {"a.esp": head([7]), "7": head([])}),
        ("a master flag of text", ["A.esp", "B.esp"], {"a.esp": head([], "yes"), "b.esp": head([], "")}),
    ]
    return [{"name": name, "pair": pair, "heads": heads, "want": BE.order_choice(pair, heads),
             "isMaster": [BE.is_master_file(p, heads.get(p.lower())) for p in pair]} for name, pair, heads in table]


def media_cases(tmp: Path) -> dict:
    root = tmp / "media-rule"
    for rel, data in (("a.png", b"a picture"), ("source.mkv", b"the recording"), ("folder/x.txt", b"x")):
        F.write(root, rel, data)
    fp, other = "f" * 64, "0" * 64
    prints, others = {"baseline": [fp], "candidate-01": ["b" * 64]}, {"candidate-01": [other], "reverse-only": [other]}
    states = {"baseline|gate": ["5" * 64]}
    good = {"path": "a.png", "mediaKind": "native-screenshot", "subject": {"checkId": "c", "armId": "baseline", "description": "d"},
            "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted", "provenance": {"tool": "t", "capturedUtc": "u"}}

    def but(**kw):
        d = copy.deepcopy(good)
        for k, v in kw.items():
            if v is ...:
                d.pop(k, None)
            else:
                d[k] = v
        return d
    sub_ = good["subject"]
    table = [("accepted", good), ("no path", but(path=...)), ("an empty path", but(path="")), ("a file that is not there", but(path="nowhere.png")),
             ("a folder", but(path="folder")), ("no kind", but(mediaKind=...)), ("an unknown kind", but(mediaKind="painting")),
             ("a kind that is no text", but(mediaKind=3)), ("no subject", but(subject=...)), ("a subject that is a list", but(subject=["c"])),
             ("a subject with no description", but(subject={"checkId": "c", "armId": "baseline", "description": ""})),
             ("no state", but(state=...)), ("an empty state", but(state="")), ("no fingerprint", but(configurationFingerprint=...)),
             ("no fingerprint source", but(fingerprintSource=...)), ("an unknown fingerprint source", but(fingerprintSource="guessed")),
             ("no provenance", but(provenance=...)), ("provenance with no tool", but(provenance={"capturedUtc": "u"})),
             ("provenance with no time", but(provenance={"tool": "t", "capturedUtc": None})), ("provenance that is text", but(provenance="viewprobe")),
             ("an arm with no accepted receipt", but(subject=dict(sub_, armId="nowhere"))),
             ("an arm only its reverse part measured", but(subject=dict(sub_, armId="reverse-only"))),
             ("an arm named by a number", but(subject=dict(sub_, armId=7))),
             ("the other order's fingerprint", but(subject=dict(sub_, armId="candidate-01"), configurationFingerprint=other)),
             ("a fingerprint of no receipt", but(configurationFingerprint="9" * 64)),
             ("a fingerprint of a measured state", but(state="gate", configurationFingerprint="5" * 64)),
             ("a state fingerprint under the baseline state", but(state="baseline", configurationFingerprint="5" * 64)),
             ("a state fingerprint of another state", but(state="other", configurationFingerprint="5" * 64)),
             ("a clip with its source", but(sourceInterval={"sourcePath": "source.mkv", "sourceSha256": F.sha(b"the recording")})),
             ("a clip interval that is text", but(sourceInterval="0-5")), ("a clip with no source path", but(sourceInterval={"start": 0})),
             ("a clip whose source is gone", but(sourceInterval={"sourcePath": "gone.mkv", "sourceSha256": "x"})),
             ("a clip whose source is a folder", but(sourceInterval={"sourcePath": "folder", "sourceSha256": "x"})),
             ("a clip with no source hash", but(sourceInterval={"sourcePath": "source.mkv"})),
             ("a clip whose source changed", but(sourceInterval={"sourcePath": "source.mkv", "sourceSha256": "1" * 64})),
             ("an original", but(original={"path": "a.png", "sha256": F.sha(b"a picture")})), ("an original that is text", but(original="a.png")),
             ("an original with no path", but(original={"sha256": "x"})), ("an original that is gone", but(original={"path": "gone.png", "sha256": "x"})),
             ("an original with no hash", but(original={"path": "a.png"})), ("an original that changed", but(original={"path": "a.png", "sha256": "2" * 64})),
             ("an empty interval and original", but(sourceInterval={}, original={}))]
    rows = []
    with F.cwd(root):
        for name, item in table:
            reason, detail = E.media_item_refusal(item, Path(item.get("path") or ""), {k: set(v) for k, v in prints.items()},
                                                  {k: set(v) for k, v in others.items()},
                                                  lambda arm, state: set(states.get(f"{arm}|{state}", [])))
            rows.append({"name": name, "item": item, "reason": reason, "detail": detail})
    cfgs = [{"plugins": [{"name": "A.ESP", "sha256": "1" * 64}, {"name": None, "sha256": None}], "providers": [{"kind": "mod", "name": "The Mod"}],
             "overlays": [{"kind": "bos-ini", "sha256": "2" * 64}], "population": "load-time+population:0123456789ab", "schemaVersion": 1},
            {"plugins": None, "population": None}, {"population": "flags+population:XYZ", "schemaVersion": None, "plugins": []}]
    fps = [{"name": name, "configs": use, "measured": measured, "state": state,
            "want": sorted(E.state_media_fingerprints(use, set(measured), state))}
           for name, use, measured, state in (("a measured state", cfgs, ["gate"], "gate"), ("a state nobody measured", cfgs, ["gate"], "door"),
                                              ("no configuration", [], ["gate"], "gate"), ("one configuration", cfgs[:1], ["gate", "door"], "door"))]
    return {"tree": F.tree(root), "vocabulary": {"kinds": list(E.MEDIA_KINDS), "sources": list(E.FINGERPRINT_SOURCES), "reasons": list(E.MEDIA_REASONS)},
            "fingerprints": prints, "others": others, "states": states, "cases": rows, "stateFingerprints": fps,
            "binarySuffixes": sorted(E.BINARY_SUFFIXES), "controlFiles": list(E.CONTROL_FILES)}


def text_cases() -> dict:
    lines = ['"C:/a b/reader.exe" --stdio', "plain.exe  two\tthree", 'a"b c"d', '"quoted"tail more', "'single quoted' x", '""', '"', "it's",
             'say "unclosed', '--x="a b"', "", "   ", 'python "fake reader.py" "table.json" die=3', '"a" "b"c "d', "\"one\"\n\"two\"\r\n"]
    split = []
    for line in lines:
        try:
            import shlex
            got = [t[1:-1] if len(t) > 1 and t[0] == t[-1] == '"' else t for t in shlex.split(line, posix=False)]
        except ValueError as ex:
            got = f"ValueError: {ex}"
        split.append({"line": line, "want": got})
    floats = []
    for v in ["1", "1.5", " 2.5 ", "-0", "+3", ".5", "5.", "1e3", "1E-2", "1_000", "1__0", "_1", "1_", "inf", "-Infinity", "nan", "NaN", "+nan", "0x10",
              "", " ", "1,5", "abc", "1 2", "١٢", "1e", "--1", "infinit", 1, 2.5, True, False, 10 ** 20, "\t7\n", "1.5.2", "1_0.5", "1._5", "1e1_0"]:
        try:
            got = float(v)
            floats.append({"value": v, "want": repr(got)})
        except ValueError as ex:
            floats.append({"value": v, "error": str(ex)})
    mods = [["A.esp=Name", " B.esp = Spaced Name ", "C.esp=a=b", "no equals", "=empty key", "D.esp="], []]
    return {"split": split, "floats": floats,
            "modNames": [{"pairs": p, "want": [[k, v] for k, v in E.mod_names(p).items()]} for p in mods],
            "readerEnv": [{"pairs": p, "want": [[k, v] for k, v in E.reader_env(p).items()]} for p in mods]}


def walk_cases(tmp: Path) -> dict:
    root = tmp / "walk"
    files = plugin_files()
    for name, data in files.items():
        F.write(root, name, data)
    fks = ["000D01:Base.esm", "000D02:base.esm", "000D03:Base.esm", "000D05:Base.esm", "000D77:Base.esm", "000E01:Target.esp", "000F01:Other.esp",
           "000001:Nowhere.esp", "000D09:Base.esm", "000D06:Base.esm"]
    base_pos = {}
    names_, recs = BE._records(root / "Base.esm")
    for sig, fid, flags, body, stack in recs:
        base_pos[f"{fid & 0xFFFFFF:06X}:{names_[-1]}".lower()] = BE._position(body)
    rows = []
    for name in files:
        if name == "Truncated.esp":
            continue
        p = root / name
        names_, recs = BE._records(p)
        own = {}
        for sig, fid, flags, body, stack in recs:
            if fid >> 24 == len(names_) - 1:
                own[f"{fid & 0xFFFFFF:06X}:{names_[-1]}".lower()] = BE._position(body)
        cells = {}
        for cell in ("000C01:Base.esm", "000C02:Base.esm", "000C00:Base.esm", "000C09:Base.esm"):
            cells[cell] = BE.cell_overrides(p, {cell}, lambda fk: base_pos.get(fk.lower())).get(cell)
        rows.append({"file": name, "names": names_, "filed": [[k, v] for k, v in BE.filed_cells(p, fks).items()],
                     "own": [[k, v] for k, v in own.items()], "overrides": cells})
    inst = tmp / "instance"
    env = BE.private_instance(inst)
    return {"tree": F.tree(root), "fks": fks, "files": rows,
            "instance": {"tree": F.tree(inst), "env": [[k, F.mask(v, inst)] for k, v in env.items()]}}


def premise_cases(tmp: Path) -> dict:
    root = tmp / "premise"
    reg, scope, search = (F.write(root, n, n) for n in ("registry.json", "scope.json", "candidates.json"))
    full = {"registry": {"path": "registry.json", "sha256": F.sha(b"registry.json")}, "scope": {"path": "scope.json", "sha256": F.sha(b"scope.json")},
            "search": {"path": "candidates.json", "sha256": F.sha(b"candidates.json")}, "candidatesDir": "candidates", "arms": []}
    F.write(root, "copy/registry.json", "registry.json")
    F.write(root, "copy/other.json", "other")
    rows = []
    with F.cwd(root):
        for name, plan, named in (
                ("all three stored", full, {}), ("a named copy of each", full, {"registry": "copy/registry.json", "scope": "scope.json", "search": "candidates.json"}),
                ("a named registry that hashes otherwise", full, {"registry": "copy/other.json"}),
                ("a named scope that is not there", full, {"scope": "copy/gone.json"}),
                ("no scope frozen", dict(full, scope={"path": None}), {}), ("a scope named where none was frozen", dict(full, scope=None), {"scope": "scope.json"}),
                ("no search frozen", {k: v for k, v in full.items() if k != "search"}, {"search": ""}),
                ("the stored registry changed", dict(full, registry={"path": "registry.json", "sha256": "0" * 64}), {}),
                ("the stored scope gone", dict(full, scope={"path": "gone/scope.json", "sha256": "0" * 64}), {}),
                ("the stored search changed", dict(full, search={"path": "candidates.json", "sha256": None}), {}),
                ("an empty name is no name", full, {"registry": ""})):
            sources, bad, code = E.bundle_premise_sources(plan, {k: named.get(k) for k in ("registry", "scope", "search")})
            rows.append({"name": name, "plan": plan, "named": named, "sources": [[k, str(v)] for k, v in sources.items()],
                         "bad": F.mask(bad, root) if bad else bad, "code": code})
    return {"tree": F.tree(root), "cases": rows}


SECTIONS = [("commands", command_cases), ("order", lambda tmp: order_cases()), ("media", media_cases), ("text", lambda tmp: text_cases()),
            ("walk", walk_cases), ("premises", premise_cases)]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_bundle.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with F.Run("make-known-answers-bundle", stage_source="single-stage"):
        tmp = Path(tempfile.mkdtemp(prefix="hc-known-bundle-")).resolve()
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")   # so no run folder here becomes the run-log store
        try:
            doc = {"python": platform.python_version(), "placeholders": {"backslash": F.BACK, "forward": F.FWD, "doubled": F.DOUBLED}}
            for name, build in SECTIONS:
                doc[name] = build(tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        text = json.dumps(doc, indent=1) + "\n"
        for leak in (tmp.name, Path.home().name):
            assert leak not in text, f"the answers would keep a local path ({leak})"
        OUT.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
        return 0


if __name__ == "__main__":
    sys.exit(main())
