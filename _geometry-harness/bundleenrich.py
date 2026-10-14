"""bundleenrich - evidence derived after measurement, added to an evaluation bundle (bundle standard §8, enrichment).

Reached through `evalrun enrich --bundle DIR --kind record-rows|names`. An enrichment derives; it never measures:

  record-rows  one rows file per credited record import (registry family `records`), derived by recordrows.py from that
               import's own request and response transcripts inside the bundle; every file and every row cites the
               bytes it came from.
  names        in-game names for the places and objects the bundle's rows name, and each evaluated mod's and patch's
               ordinary name. Places and objects are read with houseCARL's `housecarl_records` from the plugin files the
               plan records, each hashed first and refused by name if its bytes differ; the reads' request and response
               travel with the names. A mod's name is the declared one, else the Nexus catalogue cache's name for the
               scope's Nexus id (offline), else the plugin name; each says which. No network read.

What an enrichment may not do: change the disposition, a receipt, the ledger, a claim or a count. It records the
disposition's sha256 beside its outputs, and verify-bundle refuses a bundle whose disposition no longer matches, whose
enrichment rows lack their sources, or whose cited sources do not hash as cited. A bundle is enriched once per kind.
"""
from __future__ import annotations

import collections
import hashlib
import json
import os
import re
import shlex
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCHEMA = 1
FK = re.compile(r"^[0-9A-Fa-f]{6}:.+\.(?:esm|esp|esl)$")


class Refused(Exception):
    """An enrichment refused before anything was written; the message is one sentence."""


def sha(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def _load(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _write(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(obj, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def _row(rel: str, d: Path, kind: str, **kw) -> dict:
    return {"id": hashlib.sha256(rel.encode()).hexdigest()[:16], "kind": "enrichment", "enrichment": kind,
            "path": rel, "sha256": sha(d / rel), "bytes": (d / rel).stat().st_size, "strength": None,
            "provenance": {"derivedAfterMeasurement": True}, "recordedBy": f"enrichment:{kind}", **kw}


def _guard(d: Path, kind: str) -> dict:
    manifest = _load(d / "manifest.json")
    if not isinstance(manifest, dict):
        raise Refused(f"{d / 'manifest.json'} is absent or not JSON, so this is not a bundle")
    if any(e.get("kind") == "enrichment" and e.get("enrichment") == kind for e in manifest.get("evidence") or []):
        raise Refused(f"the bundle is already enriched with {kind}; enrich a fresh copy of the original instead")
    return manifest


def _commit(d: Path, manifest: dict, kind: str, outputs: list[dict], tool_files: list[Path]) -> None:
    rec_rel = f"enrichment/{kind}/enrichment.json"
    _write(d / rec_rel, {"schemaVersion": SCHEMA, "kind": kind,
                         "tool": [{"file": p.name, "sha256": sha(p)} for p in tool_files],
                         "baseManifestSha256": sha(d / "manifest.json"),
                         "dispositionSha256": sha(d / "disposition.json"),
                         "outputs": [o["path"] for o in outputs],
                         "note": "derived after measurement; changes no result, count or claim"})
    rows = outputs + [_row(rec_rel, d, kind, role="record", checkId=None, armId=None)]
    manifest.setdefault("evidence", []).extend(rows)
    req = manifest.setdefault("required", [])
    req.extend(r["path"] for r in rows if r["path"] not in req)
    manifest.setdefault("enrichment", []).append({"kind": kind, "record": rec_rel})
    _write(d / "manifest.json", manifest)


# ------------------------------------------------------------------------------------------ record rows

def enrich_record_rows(d: Path) -> dict:
    import recordrows as RR
    manifest = _guard(d, "record-rows")
    scope = RR.load_scope()
    probs = scope.problems()
    if probs:
        raise Refused("the record field scope names fields the schema does not have: " + "; ".join(probs))
    registry = _load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    family = {c.get("id"): c.get("family") for c in registry.get("checks") or [] if isinstance(c, dict)}
    outputs = []
    for rp in sorted((d / "receipts").glob("*.json")):
        rc = _load(rp) or {}
        if (rc.get("adapter") or {}).get("kind") != "mcp-import" or not (rc.get("validation") or {}).get("accepted"):
            continue
        if family.get(rc.get("checkId")) != "records":
            continue
        outs = [o.get("path") for o in rc.get("outputs") or []]
        manifest_out = next((o for o in outs if str(o).endswith("result.json")), None)
        doc = _load(d / "evidence" / manifest_out) if manifest_out else None
        if not isinstance(doc, dict):
            raise Refused(f"the import for {rc.get('checkId')} on {rc.get('armId')} has no readable result.json")
        roles = [str(x.get("role")) for x in doc.get("outputs") or []]
        copied = [o for o in outs if o != manifest_out]
        by_role = dict(zip(roles, copied))
        if not by_role.get("request") or not by_role.get("response"):
            raise Refused(f"the import for {rc.get('checkId')} on {rc.get('armId')} lacks its request or response")
        if by_role.get("rows"):
            continue                                       # the import already carries its rows
        req, resp = d / "evidence" / by_role["request"], d / "evidence" / by_role["response"]
        cfg = ((rc.get("consumed") or {}).get("configuration")) or doc.get("configuration") or {}
        emit = RR.derive(req, resp, cfg, scope, rel={req: f"evidence/{by_role['request']}",
                                                     resp: f"evidence/{by_role['response']}"})
        rel = f"enrichment/record-rows/{rc['checkId']}__{rc['armId']}.json"
        _write(d / rel, emit)
        outputs.append(_row(rel, d, "record-rows", checkId=rc["checkId"], armId=rc["armId"],
                            derivedFrom=[{"path": x["path"], "sha256": x["sha256"]} for x in emit["derivedFrom"]]))
    if not outputs:
        raise Refused("the bundle holds no credited record import whose rows could be derived")
    _commit(d, manifest, "record-rows", outputs, [HERE / "bundleenrich.py", HERE / "recordrows.py"])
    return {"kind": "record-rows", "files": len(outputs),
            "rows": sum(len((_load(d / o["path"]) or {}).get("Conflicts") or []) for o in outputs)}


# ------------------------------------------------------------------------------------------------ names

class Reader:
    """One MCP server process spoken to over stdio, every message kept for the transcript."""

    def __init__(self, cmd: str, env: dict):
        argv = [t[1:-1] if len(t) > 1 and t[0] == t[-1] == '"' else t
                for t in shlex.split(cmd, posix=os.name != "nt")]
        self.p = subprocess.Popen(argv, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, text=True, encoding="utf-8", env={**os.environ, **env})
        self.req, self.resp, self.n = [], [], 0
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "evalrun-enrich-names", "version": "1"}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _send(self, m):
        self.req.append(m)
        self.p.stdin.write(json.dumps(m) + "\n")
        self.p.stdin.flush()

    def call(self, method, params):
        self.n += 1
        self._send({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params})
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise Refused(f"the record reader exited before answering request {self.n}")
            m = json.loads(line)
            self.resp.append(m)
            if m.get("id") == self.n:
                return m

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=30)
        except (OSError, subprocess.TimeoutExpired):
            self.p.kill()


def private_instance(root: Path) -> dict:
    """An instance with one header-only plugin active and nothing else, so every record read by absolute path is an
    off-order read of the file at that path (never the instance's active copy of a same-named plugin). Returns the
    reader's environment."""
    import struct
    for sub in ("data", "Data", "mods", "profile"):
        (root / sub).mkdir(parents=True, exist_ok=True)
    hedr = b"HEDR" + struct.pack("<H", 12) + struct.pack("<fiI", 1.71, 0, 0x800)
    (root / "Data" / "PrivateInstanceAnchor.esp").write_bytes(
        b"TES4" + struct.pack("<IIIIHH", len(hedr), 0, 0, 0, 44, 0) + hedr)
    (root / "profile" / "plugins.txt").write_text("*PrivateInstanceAnchor.esp" + chr(10), encoding="utf-8")
    (root / "profile" / "loadorder.txt").write_text("PrivateInstanceAnchor.esp" + chr(10), encoding="utf-8")
    (root / "profile" / "modlist.txt").write_text("", encoding="utf-8")
    return {"HOUSECARL_DATA_DIR": str(root / "data"), "HouseCarl__DataDir": str(root / "Data"),
            "HouseCarl__ModsDir": str(root / "mods"), "HouseCarl__ProfileDir": str(root / "profile")}


def _catalogue_name(nexus_id) -> tuple[str | None, dict | None]:
    if not nexus_id:
        return None, None
    base = os.environ.get("HOUSECARL_NEXUS_CATALOG_DIR") or (
        str(Path(os.environ["HOUSECARL_DATA_DIR"]) / "nexus-catalog") if os.environ.get("HOUSECARL_DATA_DIR") else
        str(Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "houseCARL" / "nexus-catalog"))
    snap = Path(base) / "pages" / str(nexus_id) / "snapshot.json"
    doc = _load(snap) if snap.is_file() else None
    if isinstance(doc, dict) and doc.get("name"):
        return str(doc["name"]), {"path": str(snap), "sha256": sha(snap)}
    return None, None


def enrich_names(d: Path, reader_cmd: str, mod_names: dict, reader_env: dict | None = None) -> dict:
    manifest = _guard(d, "names")
    plan = _load(d / "plan.json") or {}
    scope = _load(d / (manifest.get("scope") or "scope/scope.json")) or {}
    targets = [t.get("name") for t in plan.get("targets") or [] if t.get("name")]
    base_arm = next((a for a in plan.get("arms") or [] if a.get("kind") == "baseline"), (plan.get("arms") or [{}])[0])
    plugins = [x for x in ((base_arm or {}).get("requested") or {}).get("plugins") or []]
    every = {}
    for a in plan.get("arms") or []:
        for x in (a.get("requested") or {}).get("plugins") or []:
            every.setdefault(str(x.get("name")).lower(), x)
    # a patch is a plugin some setups have and others lack, other than the evaluated mods
    arm_sets = [{str(x.get("name")).lower() for x in (a.get("requested") or {}).get("plugins") or []}
                for a in plan.get("arms") or []]
    patches = [x["name"] for k, x in every.items()
               if x["name"] not in targets and not all(k in s_ for s_ in arm_sets)]
    # what to name: every FormKey a row of the bundle names, and every placed cell of the targets
    fks, cells = set(), {}
    for e in manifest.get("evidence") or []:
        if not str(e.get("path", "")).endswith(".json") or e.get("kind") not in ("emit", "enrichment"):
            continue
        doc = _load(d / e["path"])
        for c in (doc.get("Conflicts") if isinstance(doc, dict) else None) or []:
            for n in c.get("Necessary") or []:
                if isinstance(n, dict) and FK.match(str(n.get("FormKey") or "")):
                    fks.add(n["FormKey"])
    for t in scope.get("targets") or []:
        for c in ((t.get("footprint") or {}).get("placed") or {}).get("cells") or []:
            if c.get("cell") and FK.match(str(c["cell"])):
                cells[c["cell"]] = c.get("name")
    # which files to read: the defining master of each FormKey, the targets and the patches, in load order
    names_needed = {fk.split(":", 1)[1].lower() for fk in fks | set(cells)} | {t.lower() for t in targets} | \
                   {p.lower() for p in patches}
    order = [x for x in plugins if str(x.get("name")).lower() in names_needed] + \
            [every[p.lower()] for p in patches if p.lower() in every and every[p.lower()] not in plugins]
    for x in order:                                        # hash every file before reading any
        path = Path(str(x.get("path") or ""))
        if not x.get("path") or not path.is_file():
            raise Refused(f"{x.get('name')} is not on disk at the path the plan records ({x.get('path')})")
        if sha(path) != str(x.get("sha256") or "").lower():
            raise Refused(f"{x.get('name')} at {path} does not hash to the sha256 the plan records, so it is not read")
    import tempfile
    tmp = tempfile.TemporaryDirectory(prefix="enrich-names-")
    env = reader_env or private_instance(Path(tmp.name))
    reader = Reader(reader_cmd, env)
    found, bases = {}, {}
    try:
        def read(x, wanted, fields, chunk=100):
            out = []
            wanted = sorted(wanted)
            for i in range(0, len(wanted), chunk):
                part = wanted[i:i + chunk]
                r = reader.call("tools/call", {"name": "housecarl_records", "arguments": {
                    "formids": part, "source": x["path"], "format": "json", "limit": len(part),
                    "max_chars": 4000000, "project": {"form": "fields", "fields": fields}}})
                text = "".join(c.get("text", "") for c in ((r.get("result") or {}).get("content") or []))
                body = json.loads(text) if text.strip().startswith("{") else {}
                src = str(body.get("source") or "")
                if not body or body.get("ok") is False:
                    raise Refused(f"the reader could not read {x['name']}: {str(body.get('error') or text)[:300]}")
                if "active in the load order" in src.lower() and "not active" not in src.lower():
                    raise Refused(f"the reader answered {x['name']} from its active load order ({src}), not from the "
                                  f"file at {x['path']}; start it on an instance in which no plugin of this setup is active")
                if body.get("truncated") or body.get("spilled") or len(body.get("records") or []) != len(part):
                    raise Refused(f"the reader's answer for {x['name']} was cut short ({len(body.get('records') or [])} of "
                                  f"{len(part)} records), so its names are not used")
                out += body.get("records") or []
            return out
        targets_fk = sorted(fks | set(cells))
        def keep(rec, x):
            """A later plugin's version wins, field by field, where it has a value (load order)."""
            if rec.get("error") or not rec.get("formid"):
                return None                     # this plugin has no version of the record
            fl = {f.get("path"): f.get("value") for f in rec.get("fields") or [] if f.get("value") is not None}
            prev = found.get(rec["formid"]) or {}
            found[rec["formid"]] = {"type": rec.get("type") or prev.get("type"),
                                    "editorId": rec.get("editorid") or prev.get("editorId"),
                                    "name": fl.get("Name") or prev.get("name"),
                                    "from": x["name"] if fl.get("Name") else prev.get("from")}
            return fl
        for x in order:
            for rec in read(x, targets_fk, ["Name", "Base"]):
                fl = keep(rec, x)
                if fl and fl.get("Base") and FK.match(str(fl["Base"])):
                    bases[rec["formid"]] = fl["Base"]
        base_fks = sorted(set(bases.values()))
        if base_fks:
            for x in [p for p in plugins if str(p.get("name")).lower() in
                      {b.split(":", 1)[1].lower() for b in base_fks} | {t.lower() for t in targets}]:
                for rec in read(x, base_fks, ["Name"]):
                    keep(rec, x)
    finally:
        reader.close()
        tmp.cleanup()
    places, objects, missing = {}, {}, []
    for fk, editor in cells.items():
        r = found.get(fk) or {}
        key = r.get("editorId") or editor
        if r.get("name"):
            places[key] = {"name": r["name"], "formKey": fk, "from": r.get("from")}
        elif key:
            missing.append({"formKey": fk, "editorId": key, "type": r.get("type") or "Cell"})
    for fk in sorted(set(bases.values())):
        r = found.get(fk) or {}
        if r.get("name") and r.get("editorId"):
            objects[r["editorId"]] = {"name": r["name"], "formKey": fk, "from": r.get("from")}
        else:
            missing.append({"formKey": fk, "editorId": r.get("editorId"), "type": r.get("type")})
    mods = {}
    nexus = {t.get("name"): (t.get("footprint") or {}).get("nexusId") for t in scope.get("targets") or []}
    for m in targets + [p for p in patches if p not in targets]:
        if mod_names.get(m):
            mods[m] = {"name": mod_names[m], "source": "declared"}
            continue
        nm, src = _catalogue_name(nexus.get(m))
        mods[m] = ({"name": nm, "source": "nexus-catalogue", "sourceFile": src} if nm else
                   {"name": re.sub(r"\.(esp|esm|esl)$", "", m, flags=re.I), "source": "plugin-name"})
    req_rel, resp_rel, out_rel = "enrichment/names/01-request.jsonl", "enrichment/names/02-response.jsonl", \
        "enrichment/names/names.json"
    (d / req_rel).parent.mkdir(parents=True, exist_ok=True)
    (d / req_rel).write_text("".join(json.dumps(m) + "\n" for m in reader.req), encoding="utf-8")
    (d / resp_rel).write_text("".join(json.dumps(m) + "\n" for m in reader.resp), encoding="utf-8")
    derived = [{"path": str(x["path"]), "sha256": str(x["sha256"]).lower(), "plugin": x["name"], "outside": True}
               for x in order]
    _write(d / out_rel, {"schema": "names-v1", "places": places, "objects": objects, "mods": mods,
                         "missing": missing, "derivedFrom": derived,
                         "capture": [{"path": req_rel, "sha256": sha(d / req_rel)},
                                     {"path": resp_rel, "sha256": sha(d / resp_rel)}],
                         "limits": "each place or object is read from its defining master, the evaluated mods and "
                                   "the patches; a name another mod in the order gives it is not read"})
    outputs = [_row(out_rel, d, "names", checkId=None, armId=None, derivedFrom=derived),
               _row(req_rel, d, "names", role="capture", checkId=None, armId=None),
               _row(resp_rel, d, "names", role="capture", checkId=None, armId=None)]
    _commit(d, manifest, "names", outputs, [HERE / "bundleenrich.py"])
    return {"kind": "names", "places": len(places), "objects": len(objects), "missing": len(missing),
            "mods": {m: v["source"] for m, v in mods.items()}}


# --------------------------------------------------------------------------------------------- verify

def verify(d: Path, manifest: dict, plan: dict, defect, memo_sha) -> None:
    """verify-bundle's enrichment checks: sources present and hashing as cited, every row citing its reads, plugin
    sources matching the plan's recorded bytes, and the disposition unchanged since enrichment."""
    plan_shas = {str(x.get("sha256") or "").lower() for a in plan.get("arms") or []
                 for x in (a.get("requested") or {}).get("plugins") or []}
    disp_now = memo_sha(d / "disposition.json") if (d / "disposition.json").exists() else None
    for e in manifest.get("evidence") or []:
        if e.get("kind") != "enrichment" or not (d / e["path"]).exists():
            continue
        doc = _load(d / e["path"])
        if e.get("role") == "record":
            if not isinstance(doc, dict) or doc.get("dispositionSha256") != disp_now:
                defect("enrichment-stale", e["path"],
                       "the disposition is not the one this enrichment was derived beside, so a count or claim "
                       "changed after enrichment")
            continue
        if e.get("role") == "capture":
            continue
        if e.get("enrichment") == "record-rows":
            src = (doc or {}).get("derivedFrom") if isinstance(doc, dict) else None
            if not src:
                defect("enrichment-source-missing", e["path"], "the derived rows do not cite the bytes they came from")
                continue
            for x in src:
                p = d / str(x.get("path") or "")
                if not x.get("path") or not p.is_file() or memo_sha(p) != x.get("sha256"):
                    defect("enrichment-source-changed", e["path"],
                           f"a cited source {x.get('path')} is absent from the bundle or does not hash as cited")
            bare = sum(1 for c in (doc.get("Conflicts") or []) for n in c.get("Necessary") or [] if not n.get("Reads"))
            if bare:
                defect("enrichment-source-missing", e["path"], f"{bare} derived row(s) do not cite the reads they came from")
        elif e.get("enrichment") == "names":
            for x in ((doc or {}).get("derivedFrom") if isinstance(doc, dict) else None) or []:
                if str(x.get("sha256") or "").lower() not in plan_shas:
                    defect("enrichment-source-foreign", e["path"],
                           f"names were read from {x.get('plugin')} with bytes the plan does not record")
