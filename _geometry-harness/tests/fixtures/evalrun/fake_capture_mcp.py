"""A stand-in record reader for evalrecords' capture tests: a fixed small load order over stdio JSON-RPC.

Base.esm defines; Win.esp is in the dependency baseline and wins. Evaluated plugins: Subj.esp, when the arm's view loads it.
0000A1 is contested and Subj.esp touches it with a lost edit; 0000A2 is Subj.esp's own; 0000C1 is contested, inside a
comparison cell, and no evaluated plugin touches it. Every request is appended to FAKE_CAPTURE_LOG.
"""
import json
import os
from pathlib import Path
import sys

A1, A2, C1 = "0000A1:Base.esm", "0000A2:Subj.esp", "0000C1:Base.esm"
LOG = os.environ.get("FAKE_CAPTURE_LOG")
PROFILE = Path(os.environ.get("HouseCarl__ProfileDir", "."))
LOADS_SUBJ = "Subj.esp" in (PROFILE / "plugins.txt").read_text(encoding="utf-8") if (PROFILE / "plugins.txt").is_file() else False


def tree(fid):
    if fid == C1:
        return {"formid": C1, "type": "PlacedObject", "touchers": ["Base.esm", "Win.esp"],
                "nodes": [{"plugin": "Base.esm", "deltas": [], "complete": True}, {"plugin": "Win.esp", "is_winner": True, "deltas": [], "complete": True}]}
    return {"formid": A1, "type": "Static", "touchers": ["Base.esm", "Win.esp", "Subj.esp"],
            "nodes": [{"plugin": "Base.esm", "deltas": [], "complete": True},
                      {"plugin": "Subj.esp", "deltas": ["Name=mine (Win.esp theirs)"], "complete": True},
                      {"plugin": "Win.esp", "is_winner": True, "deltas": [], "complete": True}]}


def answer(a, name):
    if name == "housecarl_check":
        if "plugins" not in a:
            return {"families": {"errors": {"accounting": {}, "plugins": [], "dangling": 0, "scanned_plugins": 2}}}
        return {"families": {"scripts": {"records_with_scripts": 0, "records": []}}}
    if name == "housecarl_asset_status":
        return {"read_incomplete": False, "bsa_failures": [], "truncated": False,
                "results": [{"path": p, "exists": True} for p in a["asset_paths"]]}
    form = (a.get("project") or {}).get("form")
    if form == "summary":
        if a.get("where"):
            rows = [{"formid": C1, "type": "PlacedObject", "override_depth": 2, "winner": "Win.esp"}]
            rows += [{"formid": A1, "type": "Static", "override_depth": 3, "winner": "Win.esp"}] if LOADS_SUBJ else []
        else:
            rows = [{"formid": A1, "type": "Static", "override_depth": 3, "winner": "Win.esp"},
                    {"formid": A2, "type": "Static", "override_depth": 1, "winner": "Subj.esp"}]
        return {"matches": rows, "total": str(len(rows))}
    if form == "tree":
        return {"rows": [tree(f) for f in a["formids"]]}
    if form == "delta":
        return {"rows": [{"formid": f, "deltas": ["Name=mine (Base.esm old)"], "complete": True} for f in a["formids"]]}
    if form == "everything":
        return {"records": [{"formid": f, "fields": [{"path": "Model.File", "value": "a.nif"}]} for f in a["formids"]]}
    raise ValueError(f"not supported: {a}")


for line in sys.stdin:
    if not line.strip():
        continue
    m = json.loads(line)
    if LOG:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(m) + "\n")
    if "id" not in m:
        continue
    if m["method"] == "initialize":
        out = {"protocolVersion": "2025-06-18", "capabilities": {}, "serverInfo": {"name": "fake-capture", "version": "1"}}
    else:
        out = {"content": [{"type": "text", "text": json.dumps(answer(m["params"]["arguments"], m["params"]["name"]))}]}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": m["id"], "result": out}) + "\n")
    sys.stdout.flush()
