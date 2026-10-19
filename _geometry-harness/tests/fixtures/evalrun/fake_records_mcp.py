"""A stand-in for the houseCARL MCP server's `housecarl_records` read, for the names enrichment's tests (case 51).

Speaks JSON-RPC over stdio, one message per line. `initialize` gets a server info answer; a `tools/call` of
`housecarl_records` with `formids`, `source` (an absolute plugin path) and `format: json` gets one record per FormID
in the real tool's json shape (`records[].fields[] {path, value}`), its `source` saying the file was read off the load
order by direct path. Every record is named "Fixture <first six of its FormID>"; a FormID ending in "FF" has no Name.
When `Base` is asked for, a placed object's base is "0BB0<last two of its FormID>:<its master>"; when `Model.File` is
asked for, a base whose FormID ends in "0E" is a Light with no model (the field reads "(absent)"), any other base a
Static with the model "fixture.nif". Anything else gets a JSON-RPC error. Nothing is read from disk.

`--unread PREFIX` (repeatable): a FormID starting with PREFIX is answered as not found in the file, so the names
enrichment cannot read it (a partial read, case 564).
"""
import json
import sys

UNREAD = [sys.argv[i + 1].upper() for i, a in enumerate(sys.argv[:-1]) if a == "--unread"]


def record(fk, wanted=()):
    fields = [] if fk.split(":")[0].upper().endswith("FF") else [{"path": "Name", "value": f"Fixture {fk[:6]}"}]
    kind = "PlacedObject"
    if fk.upper().startswith("0BB0"):
        kind = "Light" if fk.split(":")[0].upper().endswith("0E") else "Static"
        if "Model.File" in wanted:
            fields.append({"path": "Model.File", "note": "(absent)"} if kind == "Light"
                          else {"path": "Model.File", "value": "fixture.nif"})
    elif "Base" in wanted:
        fields.append({"path": "Base", "value": f"0BB0{fk[4:6]}:{fk.split(':', 1)[1]}"})
    return {"formid": fk, "type": kind, "editorid": f"Fixture{fk[:6]}", "source": None, "fields": fields}


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    msg = json.loads(line)
    if "id" not in msg:
        continue
    if msg.get("method") == "initialize":
        out = {"jsonrpc": "2.0", "id": msg["id"],
               "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                          "serverInfo": {"name": "fake-records", "version": "1"}}}
    elif msg.get("method") == "tools/call" and (msg.get("params") or {}).get("name") == "housecarl_records":
        args = msg["params"].get("arguments") or {}
        src = str(args.get("source") or "")
        name = src.replace("\\", "/").rsplit("/", 1)[-1]
        wanted = ((args.get("project") or {}).get("fields")) or []
        recs = [{"formid": fk, "error": "not found in this file"} if any(fk.upper().startswith(u) for u in UNREAD)
                else dict(record(fk, wanted), source=name) for fk in args.get("formids") or []]
        body = {"form": "fields", "source": f"{name} — on disk, outside the load order (direct path {src})",
                "count": len(recs), "epoch": "fake", "records": recs, "truncated": False}
        out = {"jsonrpc": "2.0", "id": msg["id"],
               "result": {"content": [{"type": "text", "text": json.dumps(body)}]}}
    else:
        out = {"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "not supported by the fake"}}
    sys.stdout.write(json.dumps(out) + "\n")
    sys.stdout.flush()
