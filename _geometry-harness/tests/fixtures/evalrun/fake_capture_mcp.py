"""A stand-in record reader for evalrecords' capture tests: a fixed small load order over stdio JSON-RPC.

Base.esm defines; Win.esp is in the dependency baseline and wins. Evaluated plugins: Subj.esp, when the arm's view loads it.
0000A1 is contested and Subj.esp touches it with a lost edit; 0000A2 is Subj.esp's own; 0000C1 is contested, inside a
comparison cell, and no evaluated plugin touches it. Every request is appended to FAKE_CAPTURE_LOG.

For the side-by-side tests: FAKE_CAPTURE_DIR gets one file of events per reader (start, requests, end) and a marker while
it runs. FAKE_CAPTURE_SCRIPT is JSON, {armId: {switch: value}}, acted on at the arm's first tool call: delay (seconds),
hang, exitOnce, refuse, refuseBeside (seconds to look for another reader), killParentOnce, killParent.
"""
import json
import os
from pathlib import Path
import signal
import sys
import time

A1, A2, C1 = "0000A1:Base.esm", "0000A2:Subj.esp", "0000C1:Base.esm"
LOG = os.environ.get("FAKE_CAPTURE_LOG")
PROFILE = Path(os.environ.get("HouseCarl__ProfileDir", "."))
LOADS_SUBJ = "Subj.esp" in (PROFILE / "plugins.txt").read_text(encoding="utf-8") if (PROFILE / "plugins.txt").is_file() else False
DIR = Path(os.environ["FAKE_CAPTURE_DIR"]) if os.environ.get("FAKE_CAPTURE_DIR") else None
SCRIPT = json.loads(os.environ.get("FAKE_CAPTURE_SCRIPT") or "{}")
ARM = {"id": None, "acted": False, "events": None}


def event(kind, **more):
    if ARM["events"] is not None:
        with open(ARM["events"], "a", encoding="utf-8") as f:
            f.write(json.dumps({"event": kind, "t": time.time(), "arm": ARM["id"], "pid": os.getpid(), **more}) + "\n")


def once(name) -> bool:
    mark = DIR / f"once-{ARM['id']}-{name}"
    if mark.exists():
        return False
    mark.write_text("1", encoding="utf-8")
    return True


def others_running() -> bool:
    return any(p.name != f"running-{os.getpid()}" for p in DIR.glob("running-*"))


def act() -> bool:
    """The arm's scripted behaviour at its first tool call. True: answer this call with a refusal."""
    if ARM["acted"]:
        return False
    ARM["acted"] = True
    s = SCRIPT.get(ARM["id"]) or {}
    time.sleep(float(s.get("delay") or 0))
    if s.get("hang"):
        time.sleep(3600)
    if s.get("exitOnce") and once("exit"):
        event("exit")
        os._exit(3)
    if s.get("killParentOnce") and once("kill"):
        event("killed-parent")
        os.kill(os.getppid(), signal.SIGTERM)
        time.sleep(5)
    if s.get("killParent"):
        event("killed-parent")
        os.kill(os.getppid(), signal.SIGTERM)
        time.sleep(5)
    if s.get("refuse"):
        return True
    if s.get("refuseBeside"):
        end = time.time() + float(s["refuseBeside"])
        while time.time() < end:
            if others_running():
                event("refused-beside")
                return True
            time.sleep(0.1)
    return False


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


try:
    for line in sys.stdin:
        if not line.strip():
            continue
        m = json.loads(line)
        if LOG:
            with open(LOG, "a", encoding="utf-8") as f:
                f.write(json.dumps(m) + "\n")
        if m.get("method") == "initialize" and DIR is not None:
            ARM["id"] = str(m["params"]["clientInfo"]["name"]).rsplit(":", 1)[-1]
            ARM["events"] = DIR / f"{ARM['id']}-{os.getpid()}.jsonl"
            (DIR / f"running-{os.getpid()}").write_text("1", encoding="utf-8")
            event("start", ppid=os.getppid(), profile=str(PROFILE), dataDir=os.environ.get("HOUSECARL_DATA_DIR"),
                  runlogDir=os.environ.get("HOUSECARL_RUNLOG_DIR"), token=m["params"]["clientInfo"]["name"])
        if DIR is not None and "method" in m:
            event("request", method=m["method"])
        if "id" not in m:
            continue
        if m["method"] == "initialize":
            out = {"protocolVersion": "2025-06-18", "capabilities": {}, "serverInfo": {"name": "fake-capture", "version": "1"}}
        elif ARM["id"] is not None and act():
            out = {"isError": True, "content": [{"type": "text", "text": "error: scripted refusal"}]}
        else:
            out = {"content": [{"type": "text", "text": json.dumps(answer(m["params"]["arguments"], m["params"]["name"]))}]}
        sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": m["id"], "result": out}) + "\n")
        sys.stdout.flush()
finally:
    if DIR is not None:
        event("end")
        (DIR / f"running-{os.getpid()}").unlink(missing_ok=True)
