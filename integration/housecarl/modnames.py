"""The review reel's mod names: each plugin's Nexus page name, with where it came from kept as evidence.

    resolve(plans, targets, declared, mode="offline", reader_cmd=None, transcript_dir=None) -> (names, doc)

A plugin's spoken name is, in order: the declared one (`evalreport plan --mod-name`); the name on the Nexus page of its
mod, read from houseCARL's Nexus catalogue (the scope target's `footprint.nexusId`, else `[General] modid` in its MO2 mod
folder's meta.ini); else the MO2 folder's name, with one sentence saying why no Nexus name was found. A plugin read from
the game's Data folder has no folder and no entry (the reel refuses to speak about it by name).

The catalogue is read from disk (`HOUSECARL_NEXUS_CATALOG_DIR`, else `HOUSECARL_DATA_DIR/nexus-catalog`, else
`%LOCALAPPDATA%/houseCARL/nexus-catalog`: the store houseCARL's own `housecarl_nexus_catalog` keeps). With
mode "fetch", a page the catalogue lacks is asked for once through that tool (`add=<id> refresh=if-stale
max_requests=1`) on a houseCARL server; each fetch is recorded with its time and result, and a failed one leaves the
folder name with the failure as its reason. It never stops the reel.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
import tempfile
from pathlib import Path

SCHEMA = "mod-names-v1"


def meta_ini_mod_id(path: Path):
    """The Nexus mod id in a mod folder's meta.ini ([General] modid=N), or None when there is none."""
    try:
        text = Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    section = None
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("["):
            section = s.lower()
        elif section == "[general]" and s.lower().startswith("modid="):
            v = s.split("=", 1)[1].strip()
            return int(v) if v.isdigit() and int(v) > 0 else None
    return None


def catalogue_dir() -> Path:
    """The Nexus catalogue store, resolved as houseCARL's NexusCatalog resolves it."""
    if os.environ.get("HOUSECARL_NEXUS_CATALOG_DIR", "").strip():
        return Path(os.environ["HOUSECARL_NEXUS_CATALOG_DIR"].strip())
    if os.environ.get("HOUSECARL_DATA_DIR", "").strip():
        return Path(os.environ["HOUSECARL_DATA_DIR"].strip()) / "nexus-catalog"
    return Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "houseCARL" / "nexus-catalog"


def _sha(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def _now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def plugin_folders(plans: list[dict]) -> dict:
    """{plugin: MO2 mod folder path} from where each arm's plugins were read (<instance>/mods/<folder>/<plugin>)."""
    out = {}
    for plan in plans:
        for arm in (plan or {}).get("arms") or []:
            for p in ((arm.get("requested") or {}).get("plugins") or []):
                raw = str(p.get("path") or "")
                parts = re.split(r"[\\/]+", raw)
                if len(parts) >= 3 and parts[-3].lower() == "mods" and p.get("name"):
                    out.setdefault(str(p["name"]), Path(raw).parent)
    return out


def page_name(store: Path, nexus_id) -> tuple[str | None, dict | None]:
    snap = store / "pages" / str(nexus_id) / "snapshot.json"
    try:
        doc = json.loads(snap.read_text(encoding="utf-8")) if snap.is_file() else None
    except (OSError, ValueError):
        doc = None
    if isinstance(doc, dict) and str(doc.get("name") or "").strip():
        return str(doc["name"]).strip(), {"path": str(snap), "sha256": _sha(snap), "readDate": doc.get("readDate"),
                                          "sourceUrl": doc.get("sourceUrl")}
    return None, None


def fetch_pages(ids: list[int], reader_cmd: str | None, store: Path, transcript_dir: Path | None) -> list[dict]:
    """One `housecarl_nexus_catalog add=<id>` per id on one houseCARL server; each recorded with its time and result."""
    import bundleenrich
    rows = []
    if not ids:
        return rows
    if not reader_cmd:
        return [{"modId": i, "time": _now(), "ok": False,
                 "result": "no houseCARL server was named to fetch with (--nexus-reader-cmd)"} for i in ids]
    reader = None
    with tempfile.TemporaryDirectory(prefix="hc-modnames-") as tmp:
        env = {**bundleenrich.private_instance(Path(tmp)), "HOUSECARL_NEXUS_CATALOG_DIR": str(store)}
        try:
            reader = bundleenrich.Reader(reader_cmd, env)
        except (OSError, ValueError, bundleenrich.Refused) as ex:
            return [{"modId": i, "time": _now(), "ok": False,
                     "result": f"the houseCARL server ({reader_cmd}) could not start: {str(ex).rstrip('.') or type(ex).__name__}"}
                    for i in ids]
        try:
            for i in ids:
                args = {"add": str(i), "refresh": "if-stale", "max_requests": 1, "format": "json"}
                row = {"modId": i, "time": _now(), "tool": "housecarl_nexus_catalog", "args": args}
                try:
                    m = reader.call("tools/call", {"name": "housecarl_nexus_catalog", "arguments": args})
                    res = m.get("result") or {}
                    text = " ".join(str(c.get("text") or "") for c in res.get("content") or [] if isinstance(c, dict))
                    err = m.get("error")
                    row["isError"] = bool(res.get("isError")) or err is not None
                    row["result"] = " ".join((str((err or {}).get("message")) if err else text).split())[:400]
                except (OSError, ValueError, bundleenrich.Refused) as ex:
                    row["isError"] = True
                    row["result"] = f"the request failed: {str(ex).rstrip('.') or type(ex).__name__}"
                row["completed"] = _now()
                row["ok"] = page_name(store, i)[0] is not None
                rows.append(row)
        finally:
            reader.close()
            if transcript_dir is not None:
                transcript_dir.mkdir(parents=True, exist_ok=True)
                for nm, msgs in (("fetch-request.jsonl", reader.req), ("fetch-response.jsonl", reader.resp)):
                    (transcript_dir / nm).write_text("".join(json.dumps(x) + "\n" for x in msgs), encoding="utf-8")
    return rows


def resolve(plans: list[dict], targets: list | None, declared: dict, mode: str = "offline",
            reader_cmd: str | None = None, transcript_dir: Path | None = None) -> tuple[dict, dict]:
    """({plugin: {name, source, ...}}, the evidence document). `declared` is {plugin: name}."""
    if mode not in ("offline", "fetch"):
        raise ValueError(f"nexus names mode must be offline or fetch, not {mode!r}")
    store = catalogue_dir()
    folders = plugin_folders(plans)
    footprint = {str(t.get("name")): (t.get("footprint") or {}).get("nexusId")
                 for t in targets or [] if isinstance(t, dict) and t.get("name")}
    names: dict = {}
    want: dict = {}
    for plugin in sorted(set(folders) | {k for k in declared}):
        if declared.get(plugin):
            names[plugin] = {"name": declared[plugin], "source": "declared"}
            continue
        folder = folders.get(plugin)
        if folder is None:
            continue
        e = {"name": folder.name, "source": "mo2-folder", "folder": folder.name}
        meta = folder / "meta.ini"
        nid = footprint.get(plugin)
        if meta.is_file():
            e["metaIni"] = {"path": str(meta), "sha256": _sha(meta), "modid": meta_ini_mod_id(meta)}
            nid = nid or e["metaIni"]["modid"]
        if str(nid or "").isdigit() and int(nid) > 0:
            e["nexusId"] = int(nid)
            want.setdefault(int(nid), []).append(plugin)
        elif meta.is_file():
            e["reason"] = f"the meta.ini of mod folder {folder.name!r} names no Nexus mod id"
        else:
            e["reason"] = f"mod folder {folder.name!r} has no meta.ini, so no Nexus mod id is known"
        names[plugin] = e
    missing = sorted(i for i in want if page_name(store, i)[0] is None)
    fetches = fetch_pages(missing, reader_cmd, store, transcript_dir) if mode == "fetch" else []
    by_id = {f["modId"]: f for f in fetches}
    for nid, plugins in want.items():
        nm, src = page_name(store, nid)
        for plugin in plugins:
            e = names[plugin]
            if nm:
                e.update(name=nm, source="nexus-catalogue", catalogue=src, fetched=nid in by_id)
            elif nid in by_id:
                e["reason"] = (f"Nexus mod {nid} has no page in the catalogue and fetching it did not store one: "
                               f"{by_id[nid].get('result') or 'no answer'}")
            else:
                e["reason"] = (f"Nexus mod {nid} has no page in the catalogue at {store}, and names were read offline")
    doc = {"schema": SCHEMA, "mode": mode, "catalogue": str(store), "mods": names, "fetches": fetches,
           "transcript": ([{"path": str(transcript_dir / n), "sha256": _sha(transcript_dir / n)}
                           for n in ("fetch-request.jsonl", "fetch-response.jsonl") if (transcript_dir / n).is_file()]
                          if transcript_dir is not None and fetches else [])}
    return names, doc


def summary(names: dict) -> str:
    """One line: how many names came from where, and why each fallback fell back."""
    by = {}
    for v in names.values():
        by[v.get("source")] = by.get(v.get("source"), 0) + 1
    fb = [f"{p}: {v.get('reason')}" for p, v in sorted(names.items()) if v.get("source") == "mo2-folder"]
    return (f"Mod names: {by.get('nexus-catalogue', 0)} from the Nexus catalogue, {by.get('declared', 0)} declared, "
            f"{by.get('mo2-folder', 0)} from MO2 folders" + (f" ({'; '.join(fb)})" if fb else "") + ".")
