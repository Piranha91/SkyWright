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

def recorded_plugin_headers(d: Path, manifest: dict) -> dict:
    """The plugin header facts the bundle's own probe emits recorded at measurement time (`Configuration.Plugins[]`:
    name, sha256, masters, master and light flags), {(plugin lower, sha256 lower): {...}}. Per setup the smallest emit
    that records master lists is read (the lists are a fact of the file, the same in every emit of a setup); a reverse
    part's emit records another setup's configuration and is not read."""
    emits = [e for e in manifest.get("evidence") or [] if e.get("kind") == "emit"
             and str(e.get("path", "")).endswith(".json") and ".reverse." not in str(e.get("path"))]

    def size(e):
        try:
            return (d / e["path"]).stat().st_size
        except OSError:
            return float("inf")
    out, seen = {}, set()
    for e in sorted(emits, key=size):
        if e.get("armId") in seen:
            continue
        em = _load(d / e["path"])
        cfg = em.get("Configuration") if isinstance(em, dict) else None
        rows = [x for x in (cfg or {}).get("Plugins") or [] if isinstance(x, dict) and isinstance(x.get("Masters"), list)
                and x.get("Name") and x.get("Sha256")]
        if not rows:
            continue
        seen.add(e.get("armId"))
        for x in rows:
            out.setdefault((str(x["Name"]).lower(), str(x["Sha256"]).lower()), {
                "name": x["Name"], "sha256": str(x["Sha256"]).lower(), "masters": [str(m) for m in x["Masters"]],
                "master": x.get("MasterFlag"), "light": x.get("LightFlag"), "source": e["path"]})
    return out


def configuration_masters(cfg: dict, recorded: dict | None = None) -> tuple[dict, set]:
    """Each plugin's masters, closed transitively (the record capture's own rule, evalrecords.arm_masters), and the
    plugins whose closure meets a master list that cannot be had. A plugin's list is the one the bundle's emits recorded
    for the same bytes (`recorded`, from recorded_plugin_headers), else the TES4 header of the file the configuration
    names, read only when the file hashes to the sha256 the configuration recorded. ({plugin lower: masters},
    {plugin lower})."""
    import evalrecords
    plugins = [p for p in cfg.get("plugins") or [] if isinstance(p, dict) and p.get("name")]
    checked, lists = [], {}
    for p in plugins:
        path, want = p.get("path"), str(p.get("sha256") or "").lower()
        rec = (recorded or {}).get((str(p["name"]).lower(), want)) if want else None
        if rec is not None:
            lists[str(p["name"]).casefold()] = rec["masters"]
            checked.append({"name": p["name"], "path": None})
            continue
        ok = bool(path) and Path(path).is_file() and (not want or sha(Path(path)) == want)
        checked.append({"name": p["name"], "path": path if ok else None})
    masters_of, unread_of = evalrecords.arm_masters({"plugins": [p["name"] for p in checked],
                                                     "requested": {"plugins": checked}}, recorded=lists)
    names = [p["name"] for p in checked]
    return ({n.lower(): set(masters_of(n)) for n in names}, {n.lower() for n in names if unread_of(n)})


def enrich_record_rows(d: Path) -> dict:
    import recordrows as RR
    manifest = _guard(d, "record-rows")
    scope = RR.load_scope()
    probs = scope.problems()
    if probs:
        raise Refused("the record field scope names fields the schema does not have: " + "; ".join(probs))
    registry = _load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    family = {c.get("id"): c.get("family") for c in registry.get("checks") or [] if isinstance(c, dict)}
    outputs, todo = [], []
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
        todo.append((rc, by_role, req, resp, cfg))
    # persistent cells are named by worldspace reads, which only some captures hold: gathered from all of them first
    containers = set()
    for _, _, _, resp, _ in todo:
        containers |= RR.persistent_cells(resp)
    recorded = recorded_plugin_headers(d, manifest)
    for rc, by_role, req, resp, cfg in todo:
        masters, unread = configuration_masters(cfg, recorded)
        emit = RR.derive(req, resp, cfg, scope, rel={req: f"evidence/{by_role['request']}",
                                                     resp: f"evidence/{by_role['response']}"}, containers=containers,
                         masters=masters, masters_unread=unread)
        emit["mastersUnread"] = sorted(unread)
        # whose master list came from the bundle's own emits (else from the file's header at the recorded bytes)
        emit["mastersRecorded"] = sorted({str(p.get("name")).lower() for p in cfg.get("plugins") or []
                                          if isinstance(p, dict) and (str(p.get("name")).lower(),
                                                                      str(p.get("sha256") or "").lower()) in recorded})
        rel = f"enrichment/record-rows/{rc['checkId']}__{rc['armId']}.json"
        _write(d / rel, emit)
        outputs.append(_row(rel, d, "record-rows", checkId=rc["checkId"], armId=rc["armId"],
                            derivedFrom=[{"path": x["path"], "sha256": x["sha256"]} for x in emit["derivedFrom"]]))
    if not outputs:
        raise Refused("the bundle holds no credited record import whose rows could be derived")
    _commit(d, manifest, "record-rows", outputs, [HERE / "bundleenrich.py", HERE / "recordrows.py"])
    return {"kind": "record-rows", "files": len(outputs),
            "rows": sum(len((_load(d / o["path"]) or {}).get("Conflicts") or []) for o in outputs)}


# ------------------------------------------------------------------------------------- the order of the pair

def is_master_file(name: str, head: dict | None) -> bool:
    """A master file as the game loads it: the ESM flag, or the .esm/.esl extension (an ESL flag alone does not)."""
    return bool((head or {}).get("master")) or str(name).lower().endswith((".esm", ".esl"))


def pair_headers(d: Path, manifest: dict, plan: dict, pair: list) -> dict:
    """{plugin lower: {name, sha256, masters, master, light, source}} for the evaluated pair, as the setup with both
    loads them: the master lists and flags the bundle's emits recorded for those bytes, else the file's header read at
    the recorded bytes, else absent."""
    import evalpipeline
    recorded = recorded_plugin_headers(d, manifest)
    want = {}
    # the plan's setups first, then the record imports' own configurations (the bytes they read)
    sources = [(a_.get("requested") or {}).get("plugins") or [] for a_ in plan.get("arms") or []]
    sources += [cfg.get("plugins") or [] for *_, cfg in _record_imports(d, manifest)]
    for plugins_ in sources:
        for q in plugins_:
            if isinstance(q, dict) and str(q.get("name") or "").lower() in {p.lower() for p in pair}:
                want.setdefault(str(q["name"]).lower(), q)
    out = {}
    for low_, q in want.items():
        sha_ = str(q.get("sha256") or "").lower()
        rec = recorded.get((low_, sha_))
        if rec is not None:
            out[low_] = dict(rec)
            continue
        path = q.get("path")
        try:
            if path and Path(path).is_file() and sha(Path(path)) == sha_:
                h = evalpipeline.plugin_header(path)
                out[low_] = {"name": q["name"], "sha256": sha_, "masters": h["masters"], "master": h["esm"],
                             "light": h["esl"], "source": "header at the recorded bytes"}
        except (OSError, ValueError, evalpipeline.Refusal):
            continue
    return out


def order_choice(pair: list, heads: dict) -> dict:
    """Whether the pair's order is a choice, by the engine's rules (LOOT's statement of them, session 1's rule set):
    a plugin listing the other as a master loads after it (rule `master`); else exactly one master file loads first
    (`ESM flag`); else the order is free (`free (both ESM-flagged)` / `free (both non-master)`). Without either
    plugin's header facts, nothing is said: {forced: None, missing: [...]}."""
    a, b = pair
    ha, hb = heads.get(a.lower()), heads.get(b.lower())
    if ha is None or hb is None:
        gone = [x for x, h in ((a, ha), (b, hb)) if h is None]
        return {"pair": [a, b], "forced": None, "rule": None, "loadsFirst": None,
                "missing": [f"no recorded master list or flags for {' or '.join(gone)}, and the file is not at hand at "
                            f"the recorded bytes, so whether the order is a choice is not said"]}
    ma, mb = {str(m).lower() for m in ha.get("masters") or []}, {str(m).lower() for m in hb.get("masters") or []}
    if b.lower() in ma or a.lower() in mb:
        first = b if b.lower() in ma else a
        return {"pair": [a, b], "forced": True, "rule": "master", "loadsFirst": first, "missing": []}
    fa, fb = is_master_file(a, ha), is_master_file(b, hb)
    if fa != fb:
        return {"pair": [a, b], "forced": True, "rule": "ESM flag", "loadsFirst": a if fa else b, "missing": []}
    return {"pair": [a, b], "forced": False, "rule": "free (both ESM-flagged)" if fa else "free (both non-master)",
            "loadsFirst": None, "missing": []}


def _record_imports(d: Path, manifest: dict) -> list:
    """(receipt, roles, request, response, configuration) of every credited record import with both transcripts."""
    registry = _load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    family = {c.get("id"): c.get("family") for c in registry.get("checks") or [] if isinstance(c, dict)}
    out = []
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
            continue
        roles = [str(x.get("role")) for x in doc.get("outputs") or []]
        by_role = dict(zip(roles, [o for o in outs if o != manifest_out]))
        if not by_role.get("request") or not by_role.get("response"):
            continue
        cfg = ((rc.get("consumed") or {}).get("configuration")) or doc.get("configuration") or {}
        out.append((rc, by_role, d / "evidence" / by_role["request"], d / "evidence" / by_role["response"], cfg))
    return out


def enrich_order_variant(d: Path) -> dict:
    """The record lane's rows on each setup holding both evaluated mods, with the two mods' places in the load order
    swapped (phase 3 of roadmap 4.24): recordrows.derive on that setup's own transcripts with the swapped
    configuration, which is the engine's rule for which version wins. Refused, writing nothing, when the order is not a
    choice (a master relation or exactly one master file), when the header facts are missing, or when the swap would
    load a plugin before one of its masters."""
    import recordrows as RR
    manifest = _guard(d, "order-variant")
    plan = _load(d / (manifest.get("plan") or "plan.json")) or {}
    pair = [str(t.get("name")) for t in plan.get("targets") or [] if isinstance(t, dict) and t.get("name")]
    if len(pair) != 2:
        raise Refused(f"the plan names {len(pair)} evaluated plugin(s), not a pair, so there is no pair's order to reverse")
    heads = pair_headers(d, manifest, plan, pair)
    ch = order_choice(pair, heads)
    if ch["forced"] is None:
        raise Refused(f"the order of {pair[0]} and {pair[1]} was not reversed: {ch['missing'][0]}")
    if ch["forced"]:
        other = pair[1] if ch["loadsFirst"] == pair[0] else pair[0]
        why = (f"{ch['loadsFirst']} is flagged as a master file and {other} is not" if ch["rule"] == "ESM flag"
               else f"{other} lists {ch['loadsFirst']} as a master")
        raise Refused(f"the order of {pair[0]} and {pair[1]} is not a choice ({why}), so no reversed setup was derived")
    scope = RR.load_scope()
    recorded = recorded_plugin_headers(d, manifest)
    todo = []
    for rc, by_role, req, resp, cfg in _record_imports(d, manifest):
        names = [str(p.get("name")) for p in cfg.get("plugins") or [] if isinstance(p, dict)]
        low = [n.lower() for n in names]
        if not all(x.lower() in low for x in pair):
            continue
        ps = list(cfg["plugins"])
        i, j = sorted((low.index(pair[0].lower()), low.index(pair[1].lower())))
        # the earlier of the two moves to just after the later; every other plugin keeps its place, so the later one
        # stays after its own masters
        ps.insert(j, ps.pop(i))
        order = [str(p.get("name")).lower() for p in ps]
        # every plugin's masters (recorded, else the file's header at the recorded bytes) still load before it
        all_heads = {**{k[0]: v for k, v in recorded.items()}, **heads}
        for k, nm in enumerate(order):
            h = all_heads.get(nm) or {}
            late = [m for m in h.get("masters") or [] if str(m).lower() in order and order.index(str(m).lower()) > k]
            if late:
                raise Refused(f"reversing {pair[0]} and {pair[1]} would load {ps[k]['name']} before its master "
                              f"{late[0]}, so no reversed setup was derived")
        todo.append((rc, by_role, req, resp, {**cfg, "plugins": ps}))
    if not todo:
        raise Refused("the bundle holds no credited record import whose setup loads both evaluated plugins")
    containers = set()
    for _, _, _, resp, _ in todo:
        containers |= RR.persistent_cells(resp)
    outputs, rows = [], 0
    for rc, by_role, req, resp, cfg in todo:
        masters, unread = configuration_masters(cfg, recorded)
        emit = RR.derive(req, resp, cfg, scope, rel={req: f"evidence/{by_role['request']}",
                                                     resp: f"evidence/{by_role['response']}"}, containers=containers,
                         masters=masters, masters_unread=unread)
        emit["configuration"] = {"plugins": cfg["plugins"]}
        # (item 18 review) the rows' values are differences from the measured order's winner, so "(the winning value)"
        # here is that winner's value, not the reversed order's
        emit["order"] = {"pair": pair, "reversedFrom": rc["armId"], "rule": ch["rule"],
                         "values": "against the measured order's winning version: \"(the winning value)\" is its value"}
        emit["readForm"] = "text" if RR.parse_transcript(req, resp)["reads"] else "tree"
        emit["mastersUnread"] = sorted(unread)
        rel = f"enrichment/order-variant/{rc['checkId']}__{rc['armId']}.json"
        _write(d / rel, emit)
        rows += len(emit.get("Conflicts") or [])
        outputs.append(_row(rel, d, "order-variant", checkId=rc["checkId"], armId=rc["armId"],
                            derivedFrom=[{"path": x["path"], "sha256": x["sha256"]} for x in emit["derivedFrom"]]))
    _commit(d, manifest, "order-variant", outputs, [HERE / "bundleenrich.py", HERE / "recordrows.py"])
    return {"kind": "order-variant", "files": len(outputs), "rows": rows, "rule": ch["rule"]}


# ------------------------------------------------------------------------------------------ order geometry

def _emit_conflicts(doc: dict) -> dict:
    """identity -> (Kind, A, B, FormKey, Other, Label, Physical) for every necessary record of an emit, ordersweep's keys."""
    import ordersweep as OS
    out = {}
    for c in (doc or {}).get("Conflicts") or []:
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        part = alt[0].get("Triangle") if alt else None
        for r in c.get("Necessary") or []:
            out.setdefault(OS.row_identity(c["Kind"], r["FormKey"], other, c["A"], part),
                           (c["Kind"], c["A"], c["B"], r["FormKey"], other, r.get("Label"), r.get("Physical")))
    return out


def order_geometry_rows(base_emit: Path, cand_emit: Path, reverse_emit: Path) -> list[dict]:
    """Each geometry row of either order with its physical state in both, from ordersweep's three emits: the measured
    order's (`base_emit`), the reversed order's measured against it (`cand_emit`, whose Predicates re-measure the measured
    order's rows), and the measured order re-measuring the reversed order's rows (`reverse_emit`). A row's own emit says
    Holds; the other order's predicate says Holds, Gone or Unmeasured (absent: Unmeasured)."""
    import ordersweep as OS
    base, cand, rev = (_load(Path(p)) or {} for p in (base_emit, cand_emit, reverse_emit))
    b_rows, c_rows = _emit_conflicts(base), _emit_conflicts(cand)
    c_pred, r_pred = OS.emit_predicates(Path(cand_emit)), OS.emit_predicates(Path(reverse_emit))
    out = []
    for ident in sorted(set(b_rows) | set(c_rows)):
        kind, a, b, fk, other, label, _ = b_rows.get(ident) or c_rows[ident]
        m = "Holds" if ident in b_rows and b_rows[ident][6] in (None, "Holds") else None
        r = "Holds" if ident in c_rows and c_rows[ident][6] in (None, "Holds") else None
        if m is None:
            m = r_pred.get(ident, "Unmeasured") if ident not in b_rows else (b_rows[ident][6] or "Unmeasured")
        if r is None:
            r = c_pred.get(ident, "Unmeasured") if ident not in c_rows else (c_rows[ident][6] or "Unmeasured")
        out.append({"identity": ident, "Kind": kind, "A": a, "B": b, "FormKey": fk, "Other": other, "Label": label,
                    "measured": m, "reversed": r})
    return out


def enrich_order_geometry(d: Path, sweeps) -> dict:
    """The setup without the patch's geometry rows in both orders of the evaluated free pair, from ordersweep launches
    over the measured order and its reversal (item 10; per probe, the first launch that ran it). Refused, writing nothing, when the pair's order is not a choice,
    when the launch holds other than those two orders, or when its plugins are not the setup's. Each probe's three emits
    are copied into the bundle and cited by sha256; plugins whose bytes differ from the bundle's are named (`drifted`)."""
    import ordersweep as OS
    manifest = _guard(d, "order-geometry")
    plan = _load(d / (manifest.get("plan") or "plan.json")) or {}
    pair = [str(t.get("name")) for t in plan.get("targets") or [] if isinstance(t, dict) and t.get("name")]
    if len(pair) != 2:
        raise Refused(f"the plan names {len(pair)} evaluated plugin(s), not a pair, so there is no pair's order to reverse")
    ch = order_choice(pair, pair_headers(d, manifest, plan, pair))
    if ch["forced"] is not False:
        raise Refused(f"the order of {pair[0]} and {pair[1]} is not established as a choice ({ch['rule'] or 'unknown'}), "
                      f"so no reversed geometry was taken")
    sweeps = [Path(x) for x in ([sweeps] if isinstance(sweeps, (str, Path)) else sweeps)]
    registry = _load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    check_of = {str((c.get("adapter") or {}).get("exe")): c.get("id") for c in registry.get("checks") or []
                if isinstance(c, dict) and str(c.get("id", "")).startswith("geometry.")}
    # the setup without the patch: the plan arm whose plugins are the measured order's, by name
    outputs, files, done = [], 0, set()
    for sweep, probe in ((sw, pr) for sw in sweeps for pr in (_load(sw / "summary.json") or {}).get("probes") or []):
        summ = _load(sweep / "summary.json") or {}
        cands = summ.get("candidates") or {}
        if len(cands) != 1:
            raise Refused(f"{sweep} evaluated {len(cands)} candidate order(s); give a launch over the measured order and "
                          f"its reversal only")
        cand_id = next(iter(cands))
        cid = check_of.get(probe)
        if probe in done:
            continue
        trio = [sweep / probe / "baseline.json", sweep / probe / f"{cand_id}.json", sweep / probe / f"{cand_id}.reverse.json"]
        if cid is None or not all(p.is_file() for p in trio):
            continue
        cfg = [[(str(x.get("Name")), str(x.get("Sha256") or "").lower())
                for x in ((_load(p) or {}).get("Configuration") or {}).get("Plugins") or []] for p in trio[:2]]
        if sorted(cfg[0]) != sorted(cfg[1]) or not cfg[0]:
            raise Refused(f"{probe}: the two orders do not hold the same plugins with the same bytes")
        names = [n for n, _ in cfg[0]]
        low = [n.lower() for n in names]
        if not all(x.lower() in low for x in pair):
            raise Refused(f"{probe}: the measured order does not load both {pair[0]} and {pair[1]}")
        i, j = sorted((low.index(pair[0].lower()), low.index(pair[1].lower())))
        want = list(names)
        want.insert(j, want.pop(i))
        if [n.lower() for n, _ in cfg[1]] != [n.lower() for n in want]:
            raise Refused(f"{probe}: the candidate order is not the measured order with {names[i]} moved to just after "
                          f"{names[j]}, so it is not the pair's reversal")
        arm = next((a for a in plan.get("arms") or [] if sorted(str(x.get("name")).lower() for x in
                    (a.get("requested") or {}).get("plugins") or []) == sorted(low)), None)
        if arm is None:
            raise Refused(f"{probe}: no setup of this evaluation loads the plugins the sweep measured")
        rec = {str(x.get("name")).lower(): str(x.get("sha256") or "").lower()
               for x in (arm.get("requested") or {}).get("plugins") or []}
        drifted = [{"name": n, "bundle": rec.get(n.lower()), "measured": s} for n, s in cfg[0] if rec.get(n.lower()) != s]
        copies = []
        for role, p in zip(("measured", "reversed", "reverse"), trio):
            rel = f"enrichment/order-geometry/{probe}/{role}.json"
            (d / rel).parent.mkdir(parents=True, exist_ok=True)
            (d / rel).write_bytes(p.read_bytes())
            copies.append({"path": rel, "sha256": sha(d / rel), "source": str(p)})
        rows = order_geometry_rows(*trio)
        doc = {"probe": probe, "checkId": cid, "pair": pair, "rule": ch["rule"], "armId": arm.get("armId"),
               "orders": {"measured": [{"name": n, "sha256": s} for n, s in cfg[0]],
                          "reversed": [{"name": n, "sha256": s} for n, s in cfg[1]]},
               "drifted": drifted, "rows": rows, "derivedFrom": copies,
               "sweep": {"candidate": cand_id, "classification": (cands[cand_id] or {}).get("classification")}}
        rel = f"enrichment/order-geometry/{cid}.json"
        _write(d / rel, doc)
        files += 1
        done.add(probe)
        outputs.append(_row(rel, d, "order-geometry", checkId=cid, armId=arm.get("armId"),
                            derivedFrom=[{"path": c["path"], "sha256": c["sha256"]} for c in copies]))
        outputs += [_row(c["path"], d, "order-geometry", role="capture", checkId=cid, armId=arm.get("armId"))
                    for c in copies]
    if not files:
        raise Refused(f"{', '.join(map(str, sweeps))} hold no geometry probe with all three emits (measured, reversed, "
                      f"reverse)")
    _commit(d, manifest, "order-geometry", outputs, [HERE / "bundleenrich.py", HERE / "ordersweep.py"])
    return {"kind": "order-geometry", "files": files, "probes": [o["checkId"] for o in outputs if o.get("role") is None]}


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


def filed_cells(path: Path, fks: list[str]) -> dict:
    """{FormKey: cell FormKey} for the placed references of `fks` this plugin file carries: the cell whose children group
    the reference is filed in, read from the file's raw group tree (groups are never compressed, so record headers
    suffice). A reference filed in a worldspace's persistent cell maps to that cell, marked with a trailing `!persistent`
    so a caller can tell it names no place a camera can stand in. Plugin names in FormKeys follow the file's own master
    list; the file's own records belong to the file."""
    import struct
    data = path.read_bytes()
    if data[:4] != b"TES4":
        return {}
    hsize = struct.unpack_from("<I", data, 4)[0]
    masters, q = [], 24
    while q < 24 + hsize:
        sig, ssz = data[q:q + 4], struct.unpack_from("<H", data, q + 4)[0]
        if sig == b"MAST":
            masters.append(data[q + 6:q + 6 + ssz].rstrip(b"\0").decode("cp1252"))
        q += 6 + ssz
    names_ = masters + [path.name]

    def fk_of(raw: int) -> str | None:
        idx = raw >> 24
        return f"{raw & 0xFFFFFF:06X}:{names_[idx]}" if idx < len(names_) else None
    want = {}
    for fk in fks:
        local, plugin = fk.split(":", 1)
        low = [n.lower() for n in names_]
        if plugin.lower() in low:
            want[(low.index(plugin.lower()) << 24) | int(local, 16)] = fk
    out = {}

    def walk(pos, end, stack):
        while pos < end:
            sig = data[pos:pos + 4]
            size = struct.unpack_from("<I", data, pos + 4)[0]
            if sig == b"GRUP":
                label = struct.unpack_from("<I", data, pos + 8)[0]
                gtype = struct.unpack_from("<i", data, pos + 12)[0]
                walk(pos + 24, pos + size, stack + [(gtype, label)])
                pos += size
            else:
                fid = struct.unpack_from("<I", data, pos + 12)[0]
                if fid in want and sig in (b"REFR", b"ACHR", b"PGRE", b"PHZD", b"PMIS", b"PARW", b"PBAR", b"PBEA",
                                           b"PCON", b"PFLA"):
                    cell = next((lab for gt, lab in reversed(stack) if gt == 6), None)
                    persistent = bool(stack) and len(stack) >= 2 and stack[-2][0] == 6 and any(gt == 1 for gt, _ in stack) \
                        and not any(gt in (4, 5) for gt, _ in stack)
                    if cell is not None and fk_of(cell):
                        out[want[fid]] = fk_of(cell) + ("!persistent" if persistent else "")
                pos += 24 + size
    walk(24 + hsize, len(data), [])
    return out


PLACED_SIGS = (b"REFR", b"ACHR", b"PGRE", b"PHZD", b"PMIS", b"PARW", b"PBAR", b"PBEA", b"PCON", b"PFLA")


def _records(path: Path):
    """(masters + own name, data, the group walk) of a plugin file: yields (sig, raw formid, flags, body bytes, stack)
    for every record, with a compressed body inflated."""
    import struct
    import zlib
    data = path.read_bytes()
    if data[:4] != b"TES4":
        return [], []
    hsize = struct.unpack_from("<I", data, 4)[0]
    masters, q = [], 24
    while q < 24 + hsize:
        sig, ssz = data[q:q + 4], struct.unpack_from("<H", data, q + 4)[0]
        if sig == b"MAST":
            masters.append(data[q + 6:q + 6 + ssz].rstrip(b"\0").decode("cp1252"))
        q += 6 + ssz
    out = []

    def walk(pos, end, stack):
        while pos < end:
            sig = data[pos:pos + 4]
            size = struct.unpack_from("<I", data, pos + 4)[0]
            if sig == b"GRUP":
                label = struct.unpack_from("<I", data, pos + 8)[0]
                gtype = struct.unpack_from("<i", data, pos + 12)[0]
                walk(pos + 24, pos + size, stack + [(gtype, label)])
                pos += size
            else:
                flags = struct.unpack_from("<I", data, pos + 8)[0]
                fid = struct.unpack_from("<I", data, pos + 12)[0]
                if sig in PLACED_SIGS:
                    body = data[pos + 24:pos + 24 + size]
                    if flags & 0x40000:
                        try:
                            body = zlib.decompress(body[4:])
                        except zlib.error:
                            body = b""
                    out.append((sig, fid, flags, body, tuple(stack)))
                pos += 24 + size
    walk(24 + hsize, len(data), [])
    return masters + [path.name], out


def _position(body: bytes):
    """A placed record's DATA position, or None."""
    import struct
    q = 0
    while q + 6 <= len(body):
        ssig, ssz = body[q:q + 4], struct.unpack_from("<H", body, q + 4)[0]
        if ssig == b"DATA" and ssz >= 12:
            return [round(v, 3) for v in struct.unpack_from("<fff", body, q + 6)]
        q += 6 + ssz
    return None


def cell_overrides(path: Path, cells: set, base_positions) -> dict:
    """{cell FormKey: {dz: [...], disabled, count}} for the references this plugin OVERRIDES (filed under a master's
    FormID) inside the given cells: each one's height change against the base game's position (`base_positions(fk)`),
    and how many of them it turns off (initially disabled or deleted). The reel's orienting sentence reads it: when most
    of a mover's overrides in a cell shift by the object's height, the structure moved with it; when most are turned
    off, the mover replaced the original structure."""
    names_, recs = _records(path)
    own = len(names_) - 1
    out: dict = {}
    for sig, fid, flags, body, stack in recs:
        idx = fid >> 24
        if idx >= own or idx >= len(names_):
            continue                                    # the plugin's own new references are not overrides
        cell = next((lab for gt, lab in reversed(stack) if gt == 6), None)
        if cell is None or cell >> 24 >= len(names_):
            continue
        ck = f"{cell & 0xFFFFFF:06X}:{names_[cell >> 24]}"
        if ck not in cells:
            continue
        e = out.setdefault(ck, {"dz": [], "disabled": 0, "count": 0})
        e["count"] += 1
        if flags & 0x800 or flags & 0x20:
            e["disabled"] += 1
            continue
        pos = _position(body)
        fk = f"{fid & 0xFFFFFF:06X}:{names_[idx]}"
        base = base_positions(fk)
        if pos and base:
            e["dz"].append(round(pos[2] - base[2], 1))
    return out


QUEST_TRIGGER = re.compile(r"^quest (\S+) stage \d+ \(QF_\S+?_([0-9A-Fa-f]{8})\.")


def enrich_names(d: Path, reader_cmd: str, mod_names: dict, reader_env: dict | None = None) -> dict:
    manifest = _guard(d, "names")
    plan = _load(d / "plan.json") or {}
    scope = _load(d / (manifest.get("scope") or "scope/scope.json")) or {}
    targets = [t.get("name") for t in plan.get("targets") or [] if t.get("name")]
    # the arm under review: the plan's reviewArm when it names one (a manual-mode plan reviews the profile as it is,
    # a candidate arm), else the baseline arm, which in the pipeline is the combined configuration the player runs
    review = plan.get("reviewArm")
    if review:
        base_arm = next((a for a in plan.get("arms") or [] if a.get("armId") == review), None)
        if base_arm is None:
            raise Refused(f"the plan's reviewArm {review} names no arm of the plan, so there is no configuration to read "
                          f"names in")
    else:
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
    placement_fks = set()                      # the references of placement rows (the reel's orienting sentence)
    for e in manifest.get("evidence") or []:
        if not str(e.get("path", "")).endswith(".json") or e.get("kind") not in ("emit", "enrichment"):
            continue
        doc = _load(d / e["path"])
        for c in (doc.get("Conflicts") if isinstance(doc, dict) else None) or []:
            # a row's own object and its partner (an overlap's other object, a contact's host)
            for n in (c.get("Necessary") or []) + (c.get("Alternative") or []):
                if isinstance(n, dict) and FK.match(str(n.get("FormKey") or "")):
                    fks.add(n["FormKey"])
        for c in (doc.get("Conflicts") if isinstance(doc, dict) else None) or []:
            if c.get("Field") == "Placement":
                for n in c.get("Necessary") or []:
                    if isinstance(n, dict) and FK.match(str(n.get("FormKey") or "")):
                        placement_fks.add(n["FormKey"])
        for p_ in (doc.get("Predicates") if isinstance(doc, dict) else None) or []:
            for k in ("FormKey", "Other"):
                if isinstance(p_, dict) and FK.match(str(p_.get(k) or "")):
                    fks.add(p_[k])
    # the quests a world-state clash's switch is triggered by (switch census), named for the reel's condition sentence
    state_switches, quest_edids = set(), {}
    for e in manifest.get("evidence") or []:
        if not str(e.get("path", "")).endswith(".json") or e.get("kind") not in ("emit", "enrichment"):
            continue
        doc = _load(d / e["path"])
        for c in (doc.get("Conflicts") if isinstance(doc, dict) else None) or []:
            m = re.match(r"^STATE-[A-Z]+@([0-9A-Fa-f]{6}:[^@]+)@", str(c.get("Kind") or ""))
            if m:
                state_switches.add(m.group(1))
    for e in manifest.get("evidence") or []:
        if not str(e.get("path", "")).endswith(".json") or e.get("kind") not in ("emit", "enrichment"):
            continue
        doc = _load(d / e["path"])
        for sw in (doc.get("switches") if isinstance(doc, dict) else None) or []:
            if isinstance(sw, dict) and sw.get("key") in state_switches:
                for by in (sw.get("trigger") or {}).get("by") or []:
                    m = QUEST_TRIGGER.match(str(by))
                    if m:
                        quest_edids[m.group(1)] = m.group(2)[-6:].upper()        # the fragment's FormID, low 24 bits
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
    found, bases, quests = {}, {}, {}
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
            absent = {f.get("path") for f in rec.get("fields") or [] if f.get("note") == "(absent)"}
            prev = found.get(rec["formid"]) or {}
            # the model a later version carries wins; a version that reads the field as absent has none
            model = (fl.get("Model.File") if "Model.File" in fl else "" if "Model.File" in absent
                     else prev.get("model"))
            bounds = prev.get("bounds")
            if fl.get("ObjectBounds.First") is not None and fl.get("ObjectBounds.Second") is not None:
                try:
                    bounds = [float(v) for v in f"{fl['ObjectBounds.First']},{fl['ObjectBounds.Second']}".split(",")]
                except ValueError:
                    bounds = prev.get("bounds")
            scale = (float(fl["Scale"]) if fl.get("Scale") not in (None, "") else
                     1.0 if "Scale" in absent else prev.get("scale"))
            placements = dict(prev.get("placements") or {})
            if fl.get("Placement.Position") is not None:
                try:
                    placements[x["name"]] = [float(v) for v in str(fl["Placement.Position"]).split(",")][:3]
                except ValueError:
                    pass
            found[rec["formid"]] = {"type": rec.get("type") or prev.get("type"),
                                    "editorId": rec.get("editorid") or prev.get("editorId"),
                                    "name": fl.get("Name") or prev.get("name"),
                                    "from": x["name"] if fl.get("Name") else prev.get("from"),
                                    "model": model, "bounds": bounds, "scale": scale, "placements": placements,
                                    "carriedBy": (prev.get("carriedBy") or []) + [x["name"]]}
            return fl
        for x in order:
            for rec in read(x, targets_fk, ["Name", "Base", "Scale", "Placement.Position"]):
                fl = keep(rec, x)
                if fl and fl.get("Base") and FK.match(str(fl["Base"])):
                    bases[rec["formid"]] = fl["Base"]
        base_fks = sorted(set(bases.values()))
        if base_fks:
            for x in [p for p in plugins if str(p.get("name")).lower() in
                      {b.split(":", 1)[1].lower() for b in base_fks} | {t.lower() for t in targets}]:
                for rec in read(x, base_fks, ["Name", "Model.File", "ObjectBounds.First", "ObjectBounds.Second"]):
                    keep(rec, x)
        # a quest's in-game name, for the quests the switch census names. The census gives the editor id and the quest
        # fragment's FormID (QF_<edid>_<FormID>); its plugin index is the author's load order, so the defining plugin is
        # found by reading that FormID from each plugin's own file and keeping the one whose editor id matches; then the
        # last version in the order with a name wins.
        for edid, low in sorted(quest_edids.items()):
            owner = None
            for x in plugins:
                got = read(x, [f"{low}:{x['name']}"], ["Name"])
                if got and not got[0].get("error") and got[0].get("editorid") == edid:
                    owner = f"{low}:{x['name']}"
                    break
            if owner is None:
                continue
            entry = {"name": None, "formKey": owner, "from": None}
            for x in plugins:
                for rec in read(x, [owner], ["Name"]):
                    if rec.get("error") or not rec.get("formid"):
                        continue
                    fl = {f.get("path"): f.get("value") for f in rec.get("fields") or [] if f.get("value") is not None}
                    if fl.get("Name"):
                        entry.update(name=fl["Name"], **{"from": x["name"]})
            quests[edid] = entry
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
    # every object a row names: its base and the plugins read that carry a version of it, in load order (the last is
    # the winner among them); every base: its type and model ("" when the record has none, null when not read)
    cells_by = {}
    for x in order:
        for fk, cell in filed_cells(Path(str(x["path"])), [fk for fk in targets_fk if fk in found and fk in bases]).items():
            cells_by.setdefault(fk, {})[x["name"]] = cell
    # for each placement row's reference: every carrier's own placement (the base game's is its defining master's), and
    # each non-defining carrier's overrides in the cell that version is filed in, as height changes against the base game
    by_name = {str(x["name"]).lower(): x for x in order}
    base_cache: dict = {}

    def base_positions(fk):
        plugin = fk.split(":", 1)[1].lower()
        if plugin not in base_cache:
            x = by_name.get(plugin)
            pos = {}
            if x is not None:
                names_, recs = _records(Path(str(x["path"])))
                for sig, fid, flags, body, stack in recs:
                    if fid >> 24 == len(names_) - 1:
                        pos[f"{fid & 0xFFFFFF:06X}:{names_[-1]}".lower()] = _position(body)
            base_cache[plugin] = pos
        return base_cache[plugin].get(fk.lower())
    overrides: dict = {}
    for fk in sorted(placement_fks):
        for plugin, cell in (cells_by.get(fk) or {}).items():
            if plugin.lower() == fk.split(":", 1)[1].lower() or not cell or cell.endswith("!persistent"):
                continue
            if (plugin, cell) not in overrides:
                x = by_name.get(plugin.lower())
                got = cell_overrides(Path(str(x["path"])), {cell}, base_positions) if x is not None else {}
                overrides[(plugin, cell)] = got.get(cell) or {"dz": [], "disabled": 0, "count": 0}
    refs = {fk: {"base": bases.get(fk), "carriedBy": list(dict.fromkeys((found.get(fk) or {}).get("carriedBy") or [])),
                 "scale": (found.get(fk) or {}).get("scale"), "cells": cells_by.get(fk) or {},
                 **({"placements": (found.get(fk) or {}).get("placements") or {},
                     "cellOverrides": {pl: {"cell": cl, **overrides[(pl, cl)]}
                                       for pl, cl in (cells_by.get(fk) or {}).items() if (pl, cl) in overrides}}
                    if fk in placement_fks else {})}
            for fk in targets_fk if fk in found}
    base_rows = {fk: {"type": (found.get(fk) or {}).get("type"), "editorId": (found.get(fk) or {}).get("editorId"),
                      "model": (found.get(fk) or {}).get("model"), "bounds": (found.get(fk) or {}).get("bounds")}
                 for fk in sorted(set(bases.values())) if fk in found}
    # what could not be read: an object no plugin read carries, a base no plugin read carries (the report counts their
    # clashes as cannot-tell); the names step states the counts
    unread = {"objects": sorted(fk for fk in fks if fk not in found),
              "bases": sorted(fk for fk in set(bases.values()) if not (found.get(fk) or {}).get("type"))}
    _write(d / out_rel, {"schema": "names-v1", "places": places, "objects": objects, "mods": mods,
                         "refs": refs, "bases": base_rows, "quests": quests, "unread": unread, "readOrder": [x["name"] for x in order],
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
            "mods": {m: v["source"] for m, v in mods.items()},
            "counts": {"objectsNamed": len(fks), "objectsUnread": len(unread["objects"]),
                       "bases": len(set(bases.values())), "basesUnread": len(unread["bases"])}}


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
        if e.get("enrichment") in ("record-rows", "order-variant"):
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
            # a reversed setup derived from a 2.0 tree capture cites the transcripts, not single reads (item 18)
            if bare and not (e.get("enrichment") == "order-variant" and doc.get("readForm") == "tree"):
                defect("enrichment-source-missing", e["path"], f"{bare} derived row(s) do not cite the reads they came from")
        elif e.get("enrichment") == "order-geometry":
            for x in ((doc or {}).get("derivedFrom") if isinstance(doc, dict) else None) or [None]:
                p = d / str((x or {}).get("path") or "")
                if not x or not x.get("path") or not p.is_file() or memo_sha(p) != x.get("sha256"):
                    defect("enrichment-source-changed", e["path"],
                           f"a cited emit {(x or {}).get('path')} is absent from the bundle or does not hash as cited")
        elif e.get("enrichment") == "names":
            for x in ((doc or {}).get("derivedFrom") if isinstance(doc, dict) else None) or []:
                if str(x.get("sha256") or "").lower() not in plan_shas:
                    defect("enrichment-source-foreign", e["path"],
                           f"names were read from {x.get('plugin')} with bytes the plan does not record")
    verify_names_status(d, manifest, defect)


#: The names step's statuses (evalrun bundle, bundle standard §8): what the bundle's manifest says about its names.
NAMES_STATUSES = ("enriched", "partial", "refused", "skipped")


def verify_names_status(d: Path, manifest: dict, defect) -> None:
    """The names step's statement against the files: a status from the closed set with one sentence; enriched or
    partial only with the names files, refused or skipped only without them; partial exactly when names.json lists
    something unread, with the statement's counts equal to its lists. A manifest with no statement predates the step."""
    st = (manifest.get("enrichmentStatus") or {}).get("names")
    if st is None:
        return
    sentence = str((st or {}).get("sentence") or "") if isinstance(st, dict) else ""
    status = st.get("status") if isinstance(st, dict) else None
    if status not in NAMES_STATUSES or not sentence.strip() or "\n" in sentence:
        defect("enrichment-status-unstated", "manifest.json",
               f"the names step's statement has status {status!r} and no one-sentence reason, so a reader cannot tell "
               f"why the bundle has or lacks names")
        return
    row = next((e for e in manifest.get("evidence") or []
                if e.get("kind") == "enrichment" and e.get("enrichment") == "names" and not e.get("role")), None)
    if (status in ("enriched", "partial")) != (row is not None):
        defect("enrichment-status-contradiction", "manifest.json",
               f"the names step says {status} but the bundle {'lists' if row else 'does not list'} the names files")
        return
    if row is None:
        return
    doc = _load(d / row["path"]) or {}
    unread = doc.get("unread") if isinstance(doc, dict) else None
    counts = st.get("counts") or {}
    if not isinstance(unread, dict):
        defect("enrichment-status-mismatch", row["path"], "names.json does not list what it could not read")
        return
    got = {"objectsUnread": len(unread.get("objects") or []), "basesUnread": len(unread.get("bases") or [])}
    want_status = "partial" if any(got.values()) else "enriched"
    if status != want_status or any(counts.get(k) != v for k, v in got.items()):
        defect("enrichment-status-mismatch", "manifest.json",
               f"the names step says {status} with {canonical_counts(counts)}, names.json lists "
               f"{got['objectsUnread']} unread object(s) and {got['basesUnread']} unread base record(s)")


def canonical_counts(counts: dict) -> str:
    return ", ".join(f"{k} {counts.get(k)}" for k in ("objectsUnread", "basesUnread"))
