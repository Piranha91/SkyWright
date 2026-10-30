"""modcensus - a no-game, no-render census of candidate mods, and which of them add something not yet evaluated.

    python _geometry-harness/modcensus.py run     --candidates FILE --instance DIR --profile NAME --game DIR
                                                 --reader-cmd CMD --out DIR [--batch N]
    python _geometry-harness/modcensus.py novelty --census DIR [--census DIR ...] --covered DIR [--covered DIR ...] --out FILE

What this is for. A shakedown should spend a full evaluation, report and review only on a mod whose profile shows a
kind of clash or edit the shakedown has not yet exercised. The census reads each candidate on its source profile in
minutes and records, per candidate:

  scope        scopeprobe's measured footprint of each of its plugins: record types (new and override), placed
               references by kind (interior, exterior, persistent, navmesh, landscape), cells, scripts, model paths.
  edit kinds   for each plugin's overrides, and for each key patch in the profile that masters it, the 4.24 edit
               kinds (editkinds.py) of what it changes against the provider beneath it, read with the record reader's
               delta form (versus the previous provider).
  assets       the mod folder's loose files that another provider in the profile also supplies, by asset class.

A count is reported only when its read completed; an incomplete scope, a short read or an unread asset listing is a
refusal sentence on that part, never a zero. `novelty` turns rows into features (record type, placed kind, edit kind,
asset class overridden, key patch present) and marks a candidate `full` when it has a feature no covered row has,
`census-only` when it has none, `refused` when its row was refused.
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT / "scripts"))
sys.path.insert(0, str(HERE))
from runlog import Run, Runner  # noqa: E402

ASSET_CLASSES = {"meshes": "mesh", "textures": "texture", "scripts": "script", "sound": "sound", "music": "sound",
                 "interface": "interface", "seq": "seq"}


class Refusal(Exception):
    """One sentence: what went wrong and what to try."""


# ---------------------------------------------------------------- rows

def census_row(candidate: dict, scope: dict | None, deltas: dict, assets: dict | None) -> dict:
    """One candidate's census row; every count comes from a completed read or is replaced by a refusal sentence."""
    row = {"id": candidate["id"], "mod": candidate.get("mod"), "plugins": candidate.get("plugins", []),
           "refused": None, "recordTypes": None, "placed": None, "editKinds": None, "editKindsRefused": None,
           "keyPatches": candidate.get("keyPatches"), "assetOverrides": None, "assetOverridesRefused": None}
    if not scope or scope.get("complete") is not True or scope.get("walkFailures") or scope.get("unreadableRecords"):
        row["refused"] = "the scope is incomplete or absent, so its record and placement counts are not reported."
        return row
    want = set(candidate.get("plugins") or [])
    targets = [t for t in scope.get("targets") or [] if not want or t.get("name") in want]
    if not targets:
        row["refused"] = "the scope measured none of the candidate's plugins."
        return row
    types = collections.defaultdict(lambda: {"new": 0, "override": 0})
    placed = collections.Counter()
    cells = set()
    for t in targets:
        fp = t.get("footprint") or {}
        for k, v in (fp.get("recordTypes") or {}).items():
            types[k]["new"] += int(v.get("new", 0))
            types[k]["override"] += int(v.get("override", 0))
        p = fp.get("placed") or {}
        for k in ("interior", "exterior", "persistent", "navm", "land"):
            placed[k] += int(p.get(k) or 0)
        cells |= {c["cell"] for c in p.get("cells", []) if c.get("kind") != "persistent"}
    row["recordTypes"] = {k: dict(v) for k, v in sorted(types.items())}
    row["placed"] = dict(placed)
    row["cells"] = sorted(cells)
    kinds, short = collections.Counter(), []
    for plugin, d in (deltas or {}).items():
        if d.get("returned") != d.get("requested"):
            short.append(f"{plugin}: {d.get('returned')} of {d.get('requested')}")
            continue
        kinds.update(e["kind"] for e in d.get("edits") or [])
    if short:
        row["editKindsRefused"] = "the delta reads were short (" + "; ".join(short) + "), so no edit-kind counts are given."
    else:
        row["editKinds"] = dict(kinds)
    if not assets or assets.get("complete") is not True:
        row["assetOverridesRefused"] = "the asset listing was not read completely, so no override counts are given."
    else:
        row["assetOverrides"] = dict(collections.Counter(o["class"] for o in assets.get("overrides") or []))
    return row


def features(row: dict) -> set:
    f = set()
    for k, v in (row.get("recordTypes") or {}).items():
        if v.get("new") or v.get("override"):
            f.add(("recordType", k))
    for k, v in (row.get("placed") or {}).items():
        if v:
            f.add(("placed", k))
    for k in (row.get("editKinds") or {}):
        f.add(("editKind", k))
    for k in (row.get("assetOverrides") or {}):
        f.add(("assetOverride", k))
    if row.get("keyPatches"):
        f.add(("keyPatch", "present"))
    return f


def novelty(rows: list[dict], covered: list[dict]) -> list[dict]:
    seen = set()
    for c in covered:
        if not c.get("refused"):
            seen |= features(c)
    out = []
    for r in rows:
        if r.get("refused"):
            out.append({"id": r["id"], "decision": "refused", "uncovered": [], "sentence": r["refused"]})
            continue
        new = sorted(features(r) - seen)
        out.append({"id": r["id"], "decision": "full" if new else "census-only", "uncovered": [list(x) for x in new]})
    rank = {"full": 0, "census-only": 1, "refused": 2}
    return sorted(out, key=lambda x: (rank[x["decision"]], -len(x["uncovered"]), x["id"]))


# ---------------------------------------------------------------- reading a candidate

def changed_fields(delta_lines: list[str], plugins: list[str], scope) -> list[dict]:
    """The record reader's delta lines (subject versus the provider beneath it) as editkinds' changed fields."""
    import evalrecords as ER
    out, flags, ep = [], {}, {}
    for line in delta_lines:
        p = ER.parse_delta(line, plugins)
        if p is None:
            continue
        field, own, _, other, kind = p
        if field == "MajorRecordFlagsRaw":
            flags["raw"] = (other or "0", own)
            continue
        if field == "SkyrimMajorRecordFlags":
            flags["decoded"] = ("none" if other in (None, "0") else other, "none" if own == "0" else own)
            continue
        if field.startswith("EnableParent"):
            sub = field.split(".", 1)[1] if "." in field else None
            if sub == "Reference":
                ep["parent"] = own
                ep["before_parent"] = other
            elif sub == "Flags":
                ep["flags"] = 1 if "Opposite" in str(own) else 0
            continue
        if scope.excluded("", field):
            continue
        out.append({"field": field, "before": other, "after": own})
    if flags.get("raw"):
        b, a = flags["raw"]

        def hx(v):
            try:
                return "0x%08X" % (int(float(v)) & 0xFFFFFFFF)  # flags are unsigned 32-bit; a reader may print them signed
            except (TypeError, ValueError):
                return "0x00000000"
        dec = flags.get("decoded") or ("none", "none")
        out.append({"field": "RecordFlags", "before": hx(b), "after": hx(a), "decoded": {"before": dec[0], "after": dec[1]}})
    if "parent" in ep:
        out.append({"field": "EnableParent", "before": {"parent": ep.get("before_parent")},
                    "after": {"parent": ep["parent"], "flags": ep.get("flags", 0)}})
    return out


def plugin_masters(path: Path) -> list[str]:
    import struct
    with open(path, "rb") as f:
        head = f.read(24)
        if head[:4] != b"TES4":
            return []
        size = struct.unpack_from("<I", head, 4)[0]
        body = f.read(size)
    masters, i = [], 0
    while i + 6 <= len(body):
        sig, n = body[i:i + 4], struct.unpack_from("<H", body, i + 4)[0]
        if sig == b"MAST":
            masters.append(body[i + 6:i + 6 + n].split(b"\0", 1)[0].decode("cp1252"))
        i += 6 + n
    return masters


def source_plugins(instance: Path, profile: str, game: Path) -> dict:
    """Active plugin name -> file, through the profile's enabled mods (highest priority wins) and the game's Data."""
    prof = instance / "profiles" / profile
    active = [l[1:].strip() for l in (prof / "plugins.txt").read_text(encoding="utf-8-sig").splitlines()
              if l.startswith("*")]
    mods = [l[1:].strip() for l in (prof / "modlist.txt").read_text(encoding="utf-8-sig").splitlines()
            if l.startswith("+")]
    where = {}
    for m in reversed(mods):  # modlist is strongest first; a later assignment wins
        d = instance / "mods" / m
        if d.is_dir():
            for f in d.iterdir():
                if f.suffix.lower() in (".esp", ".esm", ".esl"):
                    where[f.name.lower()] = f
    out = {}
    for p in active:
        f = where.get(p.lower()) or (game / "Data" / p)
        if f.is_file():
            out[p] = f
    return out


def loose_assets(mod_dir: Path) -> list[str]:
    out = []
    for f in mod_dir.rglob("*"):
        if f.is_file():
            rel = f.relative_to(mod_dir).as_posix().lower()
            if rel.split("/", 1)[0] in ASSET_CLASSES:
                out.append(rel)
    return sorted(out)


def read_edits(s, ER, EK, plugin, plugs, scope_rules):
    """The edit kinds of one plugin's overrides against the provider beneath each, plus its additions."""
    recs = ER.summaries(s, {"plugins": {"names": [plugin]}})
    suffix = ":" + plugin.lower()
    overrides = [r["formid"] for r in recs if not r["formid"].lower().endswith(suffix)]
    adds = [r for r in recs if r["formid"].lower().endswith(suffix)]
    edits, returned = [], 0
    for batch in ER.batched(overrides, 300):
        d = s.tool("housecarl_records", {"formids": batch, "source": plugin, "versus": "previous_provider",
                                         "project": {"form": "delta"}, "format": "json",
                                         "limit": ER.BIG, "max_chars": ER.MAX_CHARS})
        for row in d.get("rows") or []:
            returned += 1
            fields = changed_fields(row.get("deltas") or [], plugs, scope_rules)
            for e in EK.classify(row.get("type") or "", "substantive", fields):
                edits.append({"formKey": row["formid"], "kind": e["kind"], "paths": e["paths"],
                              "canonPaths": e["canonPaths"]})
    kept, _ = EK.absorb_disabled(edits)
    kept += [{"formKey": r["formid"], "kind": "ADD"} for r in adds]
    return {"requested": len(overrides), "returned": returned, "edits": kept}


def read_assets(s, inst, mod):
    mod_dir = inst / "mods" / mod if mod else None
    if not mod_dir or not mod_dir.is_dir():
        return {"complete": False, "overrides": [], "note": "the mod folder was not found in the source instance"}
    paths = loose_assets(mod_dir)
    over = []
    for i in range(0, len(paths), 5000):
        chunk = paths[i:i + 5000]
        d = s.tool("housecarl_asset_status", {"asset_paths": chunk, "format": "json", "limit": 0})
        res = d.get("results") or []
        if len(res) != len(chunk) or d.get("truncated") or d.get("read_incomplete"):
            return {"complete": False, "overrides": [], "note": f"the asset read returned {len(res)} of {len(chunk)}"}
        for r in res:
            if len(r.get("providers") or []) > 1:
                over.append({"path": r["path"], "class": ASSET_CLASSES[r["path"].split("/", 1)[0]],
                             "providers": [x.get("name") for x in r["providers"]]})
    return {"complete": True, "overrides": over, "looseFiles": len(paths),
            "note": "loose files only; files inside the mod's own archives are not listed"}


def run_census(a) -> int:
    import editkinds as EK
    import evalrecords as ER
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    cands = json.loads(Path(a.candidates).read_text(encoding="utf-8"))
    inst, game = Path(a.instance), Path(a.game)
    scope_dir = out / "scopes"
    scope_dir.mkdir(exist_ok=True)
    timing = {}

    def fwd(p):
        return str(p).replace("\\", "/")
    with Run("modcensus", mode="run", stage_source="counted") as run:
        t0 = time.perf_counter()
        plugs = source_plugins(inst, a.profile, game)
        masters = {p: plugin_masters(f) for p, f in plugs.items()}
        timing["headers"] = round(time.perf_counter() - t0, 1)
        exe = HERE / "scopeprobe" / "bin" / "Release" / "net9.0" / "scopeprobe.exe"
        t0 = time.perf_counter()
        for i in range(0, len(cands), a.batch):
            js = scope_dir / f"batch-{i // a.batch:02d}.json"
            if js.is_file():
                continue
            argv = [str(exe), "--instance", fwd(inst), "--profile", a.profile, "--game", fwd(game), "--assets",
                    "1000000", "--json", fwd(js)]
            for c in cands[i:i + a.batch]:
                for p in c["plugins"]:
                    argv += ["--target", p]
            Runner("modcensus-scope", out_dir=scope_dir / "commands").run(
                [{"id": js.stem, "argv": argv, "cwd": str(ROOT)}], echo=False)
        timing["scope"] = round(time.perf_counter() - t0, 1)
        scopes = {}
        for js in sorted(scope_dir.glob("batch-*.json")):
            d = json.loads(js.read_text(encoding="utf-8-sig"))
            for t in d.get("targets") or []:
                scopes[t["name"]] = (d, t)
        env = {"HouseCarl__DataDir": fwd(game / "Data"), "HouseCarl__ModsDir": fwd(inst / "mods"),
               "HouseCarl__ProfileDir": fwd(inst / "profiles" / a.profile), "HOUSECARL_DATA_DIR": fwd(out / "reader-data")}
        scope_rules = ER.FieldScope()
        t0 = time.perf_counter()
        s = ER.Session(a.reader_cmd, env, "modcensus")
        rows = []
        done = set()
        if (out / "rows.jsonl").is_file():
            for line in (out / "rows.jsonl").read_text(encoding="utf-8").splitlines():
                r = json.loads(line)
                rows.append(r)
                done.add(r["id"])
        run.stage("candidates", "candidate reads", len(cands), "candidates")
        try:
            for c in cands:
                if c["id"] in done:
                    run.advance()
                    continue
                c = dict(c)
                t_row = time.perf_counter()
                own = {p.lower() for p in c["plugins"]}
                keys = sorted(p for p, ms in masters.items() if p.lower() not in own and own & {m.lower() for m in ms})
                c["keyPatches"] = [{"plugin": k, "masters": len(masters[k])} for k in keys]
                deltas, refusals = {}, {}
                for p in c["plugins"] + [k for k in keys if len(masters[k]) <= a.key_max_masters]:
                    try:
                        deltas[p] = dict(read_edits(s, ER, EK, p, list(plugs), scope_rules),
                                         role="own" if p.lower() in own else "key")
                    except ER.Refusal as ex:
                        refusals[p] = str(ex)
                        if p.lower() in own:
                            deltas[p] = {"requested": None, "returned": 0, "edits": [], "role": "own"}
                assets = read_assets(s, inst, c.get("mod"))
                tg = [scopes[p] for p in c["plugins"] if p in scopes]
                own_scope = None
                if tg and len(tg) == len(c["plugins"]) and all(sc[0].get("complete") is True for sc in tg):
                    own_scope = {"complete": True, "walkFailures": [], "unreadableRecords": [], "targets": [t for _, t in tg]}
                row = census_row(c, own_scope, {k: v for k, v in deltas.items() if v.get("role") == "own"}, assets)
                row["keyEditKinds"] = {k: dict(collections.Counter(e["kind"] for e in v["edits"]))
                                       for k, v in deltas.items() if v.get("role") == "key"}
                row["readRefusals"] = refusals
                row["readSeconds"] = round(time.perf_counter() - t_row, 1)   # this candidate's reads, wall clock
                rows.append(row)
                with (out / "rows.jsonl").open("a", encoding="utf-8") as f:
                    f.write(json.dumps(row) + "\n")
                print(f"{c['id']}: {'REFUSED ' + row['refused'] if row['refused'] else 'ok'}", flush=True)
                run.advance()
        finally:
            s.close()
        timing["reads"] = round(time.perf_counter() - t0, 1)
    (out / "census.json").write_text(json.dumps({"rows": rows, "seconds": timing}, indent=1), encoding="utf-8")
    print(json.dumps({"candidates": len(rows), "refused": sum(1 for r in rows if r["refused"]), "seconds": timing}))
    return 0


def run_novelty(a) -> int:
    def load(dirs):
        rows = []
        for d in dirs:
            rows += json.loads((Path(d) / "census.json").read_text(encoding="utf-8"))["rows"]
        return rows
    res = novelty(load(a.census), load(a.covered))
    Path(a.out).write_text(json.dumps(res, indent=1), encoding="utf-8")
    for r in res:
        print(f"{r['decision']:12s} {r['id']:40s} {len(r['uncovered'])} new: "
              f"{', '.join(':'.join(x) for x in r['uncovered'][:8])}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="modcensus", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("run", help="census every candidate on its source profile")
    p.add_argument("--candidates", required=True, help="JSON list of {id, mod, plugins}")
    p.add_argument("--instance", required=True)
    p.add_argument("--profile", required=True)
    p.add_argument("--game", required=True)
    p.add_argument("--reader-cmd", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--batch", type=int, default=12, help="candidates per scopeprobe launch")
    p.add_argument("--key-max-masters", type=int, default=10,
                   help="read edit kinds of key patches with at most this many masters (larger ones are listed only)")
    p.set_defaults(fn=run_census)
    p = sub.add_parser("novelty", help="mark which candidates add features the covered set lacks")
    p.add_argument("--census", action="append", required=True)
    p.add_argument("--covered", action="append", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(fn=run_novelty)
    a = ap.parse_args(argv)
    try:
        return a.fn(a)
    except Refusal as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
