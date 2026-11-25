"""recordenrich - the rows derived after measurement from a record import, added to an AI-side overlay bundle.

    python integration/housecarl/recordenrich.py --bundle DIR --kind record-rows|order-variant

The record checks are the houseCARL integration module's (evalcontrol/ai-checks.json), so their imports, and the rows
derived from them, are in the overlay bundle evalcontrol made beside the app's bundle. The derivations (record-rows: one rows
file per credited record import, from that import's own transcripts; order-variant: the same with the evaluated pair's
order swapped) are given the plugin header facts the bound app bundle's
probe emits recorded, since an overlay bundle carries no emit. The bundle is checked with `evalcontrol verify` (the
overlay, the app bundle it is bound to and the binding) before anything is written and after. The derivation is this
module's (moved here from SkyWright's bundleenrich, whose generic helpers it calls): recordrows.py beside this file
reads the transcripts.

Exit codes: 0 written (the result as one JSON line); 2 refused before writing (one sentence); 3 written but the bundle no
longer verifies. Opens one Run (scripts/runlog.py) per invocation.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools on sys.path
sys.path.insert(0, str(SW.HARNESS))
sys.path.insert(0, str(SW.SCRIPTS))
import bundleenrich  # noqa: E402  SkyWright's generic enrichment helpers
from runlog import Run  # noqa: E402

KINDS = ("record-rows", "order-variant")
#: the files whose bytes an enrichment record names as its tool
TOOL_FILES = [SW.HARNESS / "bundleenrich.py", HERE / "recordenrich.py", HERE / "recordrows.py"]


def load(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def verify(d: Path) -> int:
    """`evalcontrol verify d`, which writes d/verify.json: its exit code."""
    exe = SW.tool("evalcontrol")
    if not exe.is_file():
        raise bundleenrich.Refused(f"the module's evalcontrol is not at {exe}; build the module (scripts/build.ps1)")
    return subprocess.run([str(exe), "verify", str(d)], capture_output=True, text=True, timeout=900).returncode


def app_headers(d: Path, manifest: dict) -> dict:
    """The plugin header facts the app bundle this overlay is bound to recorded (bundleenrich.recorded_plugin_headers)."""
    rel = (manifest.get("overlay") or {}).get("appBundleFromBundle")
    app = (d / rel).resolve() if rel else None
    am = load(app / "manifest.json") if app else None
    if not isinstance(am, dict):
        raise bundleenrich.Refused(f"the app bundle this overlay is bound to is not readable at {app}")
    return bundleenrich.recorded_plugin_headers(app, am)


def enrich(d: Path, kind: str) -> tuple[int, str]:
    """(exit code, the line to print)."""
    manifest = load(d / "manifest.json")
    if not isinstance(manifest, dict) or "overlay" not in manifest:
        return 2, (f"REFUSED: enrich refused: {d} is not an AI-side overlay bundle (its manifest has no overlay block); "
                   f"the record rows are derived in the overlay bundle evalcontrol makes beside the app's.")
    try:
        if verify(d) != 0:
            return 2, (f"REFUSED: enrich refused: {d} does not verify (see its verify.json); an enrichment is added only "
                       f"to an intact bundle")
        headers = app_headers(d, manifest)
        out = (enrich_record_rows(d, headers) if kind == "record-rows"
               else enrich_order_variant(d, headers))
    except bundleenrich.Refused as ex:
        return 2, f"REFUSED: enrich refused: {ex}"
    if verify(d) != 0:
        return 3, f"REFUSED: enrich wrote {kind} but the bundle no longer verifies; see its verify.json"
    return 0, json.dumps(out)


def configuration_masters(cfg: dict, recorded: dict | None = None) -> tuple[dict, set]:
    """Each plugin's masters, closed transitively (evalbase.arm_masters, the rule the record capture shares), and the
    plugins whose closure meets a master list that cannot be had. A plugin's list is the one the bundle's emits recorded
    for the same bytes (`recorded`, from recorded_plugin_headers), else the TES4 header of the file the configuration
    names, read only when the file hashes to the sha256 the configuration recorded. ({plugin lower: masters},
    {plugin lower})."""
    import evalbase
    plugins = [p for p in cfg.get("plugins") or [] if isinstance(p, dict) and p.get("name")]
    checked, lists = [], {}
    for p in plugins:
        path, want = p.get("path"), str(p.get("sha256") or "").lower()
        rec = (recorded or {}).get((str(p["name"]).lower(), want)) if want else None
        if rec is not None:
            lists[str(p["name"]).casefold()] = rec["masters"]
            checked.append({"name": p["name"], "path": None})
            continue
        ok = bool(path) and Path(path).is_file() and (not want or bundleenrich.sha(Path(path)) == want)
        checked.append({"name": p["name"], "path": path if ok else None})
    masters_of, unread_of = evalbase.arm_masters({"plugins": [p["name"] for p in checked],
                                                     "requested": {"plugins": checked}}, recorded=lists)
    names = [p["name"] for p in checked]
    return ({n.lower(): set(masters_of(n)) for n in names}, {n.lower() for n in names if unread_of(n)})


def enrich_record_rows(d: Path, recorded: dict | None = None) -> dict:
    import recordrows as RR
    manifest = bundleenrich._guard(d, "record-rows")
    scope = RR.load_scope()
    probs = scope.problems()
    if probs:
        raise bundleenrich.Refused("the record field scope names fields the schema does not have: " + "; ".join(probs))
    registry = bundleenrich._load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    family = {c.get("id"): c.get("family") for c in registry.get("checks") or [] if isinstance(c, dict)}
    outputs, todo = [], []
    for rp in sorted((d / "receipts").glob("*.json")):
        rc = bundleenrich._load(rp) or {}
        if (rc.get("adapter") or {}).get("kind") != "mcp-import" or not (rc.get("validation") or {}).get("accepted"):
            continue
        if family.get(rc.get("checkId")) != "records":
            continue
        outs = [o.get("path") for o in rc.get("outputs") or []]
        manifest_out = next((o for o in outs if str(o).endswith("result.json")), None)
        doc = bundleenrich._load(d / "evidence" / manifest_out) if manifest_out else None
        if not isinstance(doc, dict):
            raise bundleenrich.Refused(f"the import for {rc.get('checkId')} on {rc.get('armId')} has no readable result.json")
        roles = [str(x.get("role")) for x in doc.get("outputs") or []]
        copied = [o for o in outs if o != manifest_out]
        by_role = dict(zip(roles, copied))
        if not by_role.get("request") or not by_role.get("response"):
            raise bundleenrich.Refused(f"the import for {rc.get('checkId')} on {rc.get('armId')} lacks its request or response")
        if by_role.get("rows"):
            continue                                       # the import already carries its rows
        req, resp = d / "evidence" / by_role["request"], d / "evidence" / by_role["response"]
        cfg = ((rc.get("consumed") or {}).get("configuration")) or doc.get("configuration") or {}
        todo.append((rc, by_role, req, resp, cfg))
    # persistent cells are named by worldspace reads, which only some captures hold: gathered from all of them first
    containers = set()
    for _, _, _, resp, _ in todo:
        containers |= RR.persistent_cells(resp)
    recorded = bundleenrich.recorded_plugin_headers(d, manifest) if recorded is None else recorded
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
        bundleenrich._write(d / rel, emit)
        outputs.append(bundleenrich._row(rel, d, "record-rows", checkId=rc["checkId"], armId=rc["armId"],
                            derivedFrom=[{"path": x["path"], "sha256": x["sha256"]} for x in emit["derivedFrom"]]))
    if not outputs:
        raise bundleenrich.Refused("the bundle holds no credited record import whose rows could be derived")
    bundleenrich._commit(d, manifest, "record-rows", outputs, TOOL_FILES)
    return {"kind": "record-rows", "files": len(outputs),
            "rows": sum(len((bundleenrich._load(d / o["path"]) or {}).get("Conflicts") or []) for o in outputs)}


def _record_imports(d: Path, manifest: dict) -> list:
    """(receipt, roles, request, response, configuration) of every credited record import with both transcripts."""
    registry = bundleenrich._load(d / (manifest.get("registry") or "registry/check-registry.json")) or {}
    family = {c.get("id"): c.get("family") for c in registry.get("checks") or [] if isinstance(c, dict)}
    out = []
    for rp in sorted((d / "receipts").glob("*.json")):
        rc = bundleenrich._load(rp) or {}
        if (rc.get("adapter") or {}).get("kind") != "mcp-import" or not (rc.get("validation") or {}).get("accepted"):
            continue
        if family.get(rc.get("checkId")) != "records":
            continue
        outs = [o.get("path") for o in rc.get("outputs") or []]
        manifest_out = next((o for o in outs if str(o).endswith("result.json")), None)
        doc = bundleenrich._load(d / "evidence" / manifest_out) if manifest_out else None
        if not isinstance(doc, dict):
            continue
        roles = [str(x.get("role")) for x in doc.get("outputs") or []]
        by_role = dict(zip(roles, [o for o in outs if o != manifest_out]))
        if not by_role.get("request") or not by_role.get("response"):
            continue
        cfg = ((rc.get("consumed") or {}).get("configuration")) or doc.get("configuration") or {}
        out.append((rc, by_role, d / "evidence" / by_role["request"], d / "evidence" / by_role["response"], cfg))
    return out


def enrich_order_variant(d: Path, recorded: dict | None = None) -> dict:
    """The record lane's rows on each setup holding both evaluated mods, with the two mods' places in the load order
    swapped (phase 3 of roadmap 4.24): recordrows.derive on that setup's own transcripts with the swapped
    configuration, which is the engine's rule for which version wins. Refused, writing nothing, when the order is not a
    choice (a master relation or exactly one master file), when the header facts are missing, or when the swap would
    load a plugin before one of its masters."""
    import recordrows as RR
    manifest = bundleenrich._guard(d, "order-variant")
    plan = bundleenrich._load(d / (manifest.get("plan") or "plan.json")) or {}
    pair = [str(t.get("name")) for t in plan.get("targets") or [] if isinstance(t, dict) and t.get("name")]
    if len(pair) != 2:
        raise bundleenrich.Refused(f"the plan names {len(pair)} evaluated plugin(s), not a pair, so there is no pair's order to reverse")
    heads = bundleenrich.pair_headers(d, manifest, plan, pair, recorded,
                                      extra_sources=[cfg.get("plugins") or [] for *_, cfg in _record_imports(d, manifest)])
    ch = bundleenrich.order_choice(pair, heads)
    if ch["forced"] is None:
        raise bundleenrich.Refused(f"the order of {pair[0]} and {pair[1]} was not reversed: {ch['missing'][0]}")
    if ch["forced"]:
        other = pair[1] if ch["loadsFirst"] == pair[0] else pair[0]
        why = (f"{ch['loadsFirst']} is flagged as a master file and {other} is not" if ch["rule"] == "ESM flag"
               else f"{other} lists {ch['loadsFirst']} as a master")
        raise bundleenrich.Refused(f"the order of {pair[0]} and {pair[1]} is not a choice ({why}), so no reversed setup was derived")
    scope = RR.load_scope()
    recorded = bundleenrich.recorded_plugin_headers(d, manifest) if recorded is None else recorded
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
                raise bundleenrich.Refused(f"reversing {pair[0]} and {pair[1]} would load {ps[k]['name']} before its master "
                              f"{late[0]}, so no reversed setup was derived")
        todo.append((rc, by_role, req, resp, {**cfg, "plugins": ps}))
    if not todo:
        raise bundleenrich.Refused("the bundle holds no credited record import whose setup loads both evaluated plugins")
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
        bundleenrich._write(d / rel, emit)
        rows += len(emit.get("Conflicts") or [])
        outputs.append(bundleenrich._row(rel, d, "order-variant", checkId=rc["checkId"], armId=rc["armId"],
                            derivedFrom=[{"path": x["path"], "sha256": x["sha256"]} for x in emit["derivedFrom"]]))
    bundleenrich._commit(d, manifest, "order-variant", outputs, TOOL_FILES)
    return {"kind": "order-variant", "files": len(outputs), "rows": rows, "rule": ch["rule"]}



def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Rows derived from a record import, added to an AI-side overlay bundle.")
    ap.add_argument("--bundle", required=True, help="the overlay bundle (evalcontrol bundle's --out)")
    ap.add_argument("--kind", required=True, choices=KINDS)
    a = ap.parse_args(argv)
    with Run("recordenrich", stage_source="counted") as r:
        r.stage("enrich", total=1, unit="enrichments")
        code, line = enrich(Path(a.bundle), a.kind)
        print(line, file=sys.stderr if code else sys.stdout)
        if code:
            r.refuse(line.removeprefix("REFUSED: "), code)
        else:
            r.advance()
    return code


if __name__ == "__main__":
    sys.exit(main())
