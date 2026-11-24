#!/usr/bin/env python
"""Record a Nexus patch search for one plugin pair through the houseCARL server, every request and response kept, in the
curated shape the review reel and the report read (evalreport plan --patch-search).

    python _geometry-harness/patchsearch.py --reader-cmd CMD --out DIR
        --pair PLUGIN_A=MODID_A=TERMS_A --pair PLUGIN_B=MODID_B=TERMS_B
        [--known PLUGIN_A,PLUGIN_B=PATCH.esp=PAGEID] [--merge EXISTING.json] [--no-catalog-add]

For the pair: housecarl_nexus_catalog add= on each mod's own page first, so their archive listings are read (unless
--no-catalog-add); housecarl_nexus_mod (description and files) on each page; housecarl_nexus_file_search for each mod's
terms; one file search naming both. --known also runs the search's own known answer (a patch known to exist, on its
page), so a later "no patch exists" can rest on a search shown to find one. TERMS is the search term with its aliases
(`Name|Alias|...`).

What the pair record carries:
  * hits: each file naming both mods, with every archive path the search matched it by (`contentPaths`);
  * unreadSources: every source the search did not read: each answer's `coverage.contentsUnread` rows, a page
    description the tool cut short, and a "not known" row for any answer whose coverage could not be read. The key is
    always written, so a reader never takes a missing list for "everything was read";
  * build: the server's commit (where its folder is a git checkout) and the sha256 of housecarl-mcp.dll and
    housecarl-nexus.dll beside it; housecarl-mcp.exe is the .NET apphost stub, the same for every build.
The output DIR gets query-N.json (request and response envelope), response-N.json (the tool's text) and
patch-search.json; --merge copies an existing search's pairs and known answer in unchanged.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shlex
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
from runlog import Run  # noqa: E402

NOT_KNOWN = ("the search's answer carries no readable coverage, so which archive listings it did not read is not "
             "known; nothing may be said to be absent from it")


def now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def sha256(p: Path) -> str | None:
    return hashlib.sha256(p.read_bytes()).hexdigest() if p.is_file() else None


def curated_hit(row: dict) -> dict:
    """One file-search hit in the curated shape: the file's name as a string, and the archive paths it matched by."""
    f = row.get("file") if isinstance(row.get("file"), dict) else {}
    paths = [m.get("contentPath") for m in row.get("matches") or [] if isinstance(m, dict) and m.get("contentPath")]
    return {"file": f.get("name") if f else row.get("file"), "pageId": row.get("modId"), "fileId": f.get("fileId"),
            "version": f.get("version"), "status": f.get("status"), "category": f.get("category"),
            "source": row.get("sourceUrl"), "contentPaths": list(dict.fromkeys(paths))}


def unread_of(text: str, label: str) -> list[dict]:
    """The sources one file-search answer did not read: its coverage's unread archive listings, or one "not known"
    row where the answer or its coverage cannot be read."""
    try:
        cov = (json.loads(text) or {}).get("coverage")
    except (ValueError, AttributeError):
        cov = None
    if not isinstance(cov, dict) or not isinstance(cov.get("contentsUnread"), list):
        return [{"source": label, "why": NOT_KNOWN}]
    return [{"source": f"{label} coverage: page {u.get('modId')} file {u.get('fileId')}",
             "why": u.get("sentence") or u.get("message") or u.get("reason")} for u in cov["contentsUnread"]]


def description_unread(text: str, modid: str) -> list[dict]:
    return ([{"source": f"housecarl_nexus_mod {modid} description",
              "why": "the tool cut the description short ('description truncated'), so whether the page names the "
                     "other mod is unknown"}] if "description truncated" in (text or "") else [])


def curate_pair(pair: list, per_mod: list, both: str, queries: list, mod_pages=(), nexus_ids=None) -> dict:
    """The pair record from the answers: per_mod each mod's file-search text, both the search naming both mods,
    mod_pages [(mod id, housecarl_nexus_mod text)]."""
    try:
        hits = [curated_hit(h) for h in json.loads(both).get("hits") or []]
    except (ValueError, AttributeError):
        hits = None
    unread = []
    for i, text in enumerate(per_mod):
        unread += unread_of(text, f"housecarl_nexus_file_search for {pair[i] if i < len(pair) else i}")
    unread += unread_of(both, "housecarl_nexus_file_search naming both")
    for modid, text in mod_pages:
        unread += description_unread(text, modid)
    # one row per unread source: an archive listing two answers both name is one listing
    unread = list({u["source"].split(" coverage: ", 1)[-1] + "|" + str(u["why"]): u for u in unread}.values())
    result = ("the combined search did not answer in JSON; read its response" if hits is None else
              "none found" if not hits else f"{len(hits)} file(s) name both mods")
    return {"pair": list(pair), "queries": list(queries), "hits": hits or [], "unreadSources": unread,
            "result": result, **({"nexusModIds": nexus_ids} if nexus_ids else {})}


def build_identity(exe: Path) -> dict:
    """The server build: its commit where its folder is a git checkout (else None, said), and the sha256 of the dlls
    that are the build (the exe is the .NET apphost stub)."""
    exe = Path(exe)
    try:
        got = subprocess.run(["git", "-C", str(exe.parent), "rev-parse", "HEAD"], capture_output=True, text=True,
                             timeout=30)
        commit = got.stdout.strip() if got.returncode == 0 and got.stdout.strip() else None
    except (OSError, subprocess.SubprocessError):
        commit = None
    return {"exe": str(exe), "commit": commit,
            **({} if commit else {"commitNote": "the server's folder is not a git checkout, so its commit is not known"}),
            "sha256": {n: sha256(exe.parent / n) for n in ("housecarl-mcp.dll", "housecarl-nexus.dll")},
            "exeSha256Covers": "housecarl-mcp.exe is the .NET apphost stub, the same for every build; the dlls are the build"}


def known_answer(spec: str | None, text: str | None, query: dict | None) -> dict:
    if not spec:
        return {}
    pair, patch, page = spec.split("=", 2)
    try:
        hits = [curated_hit(h) for h in json.loads(text or "").get("hits") or []]
    except (ValueError, AttributeError):
        hits = []
    return {"pair": [p.strip() for p in pair.split(",")], "patch": patch.strip(), "page": page.strip(),
            "query": query, "hits": hits}


def exe_of(cmd: str) -> Path:
    first = shlex.split(cmd, posix=False)[0]
    return Path(first.strip('"'))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Record a Nexus patch search for one plugin pair.")
    ap.add_argument("--reader-cmd", required=True, help="the houseCARL server command")
    ap.add_argument("--out", required=True, help="the folder the search is written to")
    ap.add_argument("--pair", action="append", required=True, help="PLUGIN=MODID=TERMS, once per mod of the pair")
    ap.add_argument("--known", help="PLUGIN_A,PLUGIN_B=PATCH.esp=PAGEID: the search's own known answer")
    ap.add_argument("--merge", help="an existing patch-search.json whose pairs and known answer are kept")
    ap.add_argument("--no-catalog-add", action="store_true", help="do not read the pair's own pages' listings first")
    a = ap.parse_args(argv)
    import evalrecords
    mods = [p.split("=", 2) for p in a.pair]
    if len(mods) != 2 or any(len(m) != 3 for m in mods):
        print("--pair takes PLUGIN=MODID=TERMS and is given twice, once per mod of the pair.", file=sys.stderr)
        return 2
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    with Run("patchsearch", stage_source="counted") as run:
        s = evalrecords.Session(a.reader_cmd, {}, "patch-search")
        queries, n = [], [0]

        def call(tool, args):
            t0 = now()
            m = s.call("tools/call", {"name": tool, "arguments": args})
            res = m.get("result") or {}
            text = "".join(c.get("text", "") for c in res.get("content", []))
            k = n[0]
            (out / f"response-{k}.json").write_text(text, encoding="utf-8")
            (out / f"query-{k}.json").write_text(json.dumps({"request": s.req[-1], "response": m}, indent=1),
                                                 encoding="utf-8")
            queries.append({"tool": tool, "args": args, "date": t0, "completedUtc": now(),
                            "response": str(out / f"response-{k}.json"),
                            "responseSha256": sha256(out / f"response-{k}.json"),
                            "requestResponseEnvelope": str(out / f"query-{k}.json"),
                            "requestResponseEnvelopeSha256": sha256(out / f"query-{k}.json"),
                            "isError": bool(res.get("isError")), "protocolError": m.get("error")})
            n[0] += 1
            run.advance(1)
            return text
        run.stage("queries", total=None, unit="calls")
        pages, per_mod, known_text, known_q = [], [], None, None
        try:
            if not a.no_catalog_add:
                for _, modid, _ in mods:
                    call("housecarl_nexus_catalog", {"add": modid, "format": "json"})
            for _, modid, _ in mods:
                pages.append((modid, call("housecarl_nexus_mod", {"mod": modid, "description": True, "files": True})))
            for _, _, terms in mods:
                per_mod.append(call("housecarl_nexus_file_search", {"terms": terms, "format": "json", "limit": 200}))
            both = call("housecarl_nexus_file_search", {"terms": "\n".join(t for _, _, t in mods), "format": "json",
                                                        "limit": 200})
            if a.known:
                page = a.known.split("=", 2)[2]
                known_text = call("housecarl_nexus_file_search", {"terms": "\n".join(t for _, _, t in mods),
                                                                  "mods": page, "format": "json", "limit": 200})
                known_q = queries[-1]
        finally:
            s.close()
        pair = curate_pair([m[0] for m in mods], per_mod, both, queries, pages, {m[0]: m[1] for m in mods})
        doc = {"schemaVersion": 1, "pairs": [], "knownAnswer": {}}
        if a.merge:
            old = json.loads(Path(a.merge).read_text(encoding="utf-8"))
            doc = dict(old, pairs=list(old.get("pairs") or []),
                       mergedFrom={"path": a.merge, "sha256": sha256(Path(a.merge))})
        doc["pairs"].append(pair)
        if a.known:
            doc["knownAnswer"] = known_answer(a.known, known_text, known_q)
        doc["build"] = build_identity(exe_of(a.reader_cmd))
        doc["createdUtc"] = now()
        (out / "patch-search.json").write_text(json.dumps(doc, indent=1, ensure_ascii=False), encoding="utf-8")
        run.feature("queries", len(queries), "calls").feature("hits", len(pair["hits"]), "files")
        run.feature("unread", len(pair["unreadSources"]), "sources")
        print(json.dumps({"queries": len(queries), "errors": [q["tool"] for q in queries
                                                             if q["isError"] or q["protocolError"]],
                          "hits": len(pair["hits"]), "unreadSources": len(pair["unreadSources"]),
                          "result": pair["result"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
