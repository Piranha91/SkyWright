#!/usr/bin/env python
"""Leads from a whole-order pass, queryable by added mod, place and pair.

    orderleads.py build --pass DIR [--added PLUGIN|FOLDER ...] [--rules F] [--dummies F] [--cell-names F] [--out DIR]
    orderleads.py read  --pass DIR [--added X ...] [--place P | --place-prefix P] [--pair A B] [--family F]
                        [--whole] [--limit N] [--format md|json] [--rules F] [--dummies F] [--cell-names F]

A pass folder holds ordermap's map.json and the checks' outputs (orderpass.py writes one). Its rows are grouped by the
plugins they name, the check family and a tier (orderleads-rules.json), and ordered so tiers and families take turns.
A LEAD is a group whose rows each name two different non-base plugins (or two mod folders whose bytes differ on a
shared path): a place to open, not a verdict. Everything else stays in the record with a counted reason: a row naming
one plugin, a row against the base game only, a map edge with no row, a record row whose only difference is the
compressed-storage flag. A query filters the one whole-order lead list; a place query also narrows each lead to that
place's rows. When the pass was run for an added mod (pass.json) its view is what `read` shows first.
Files may be stored gzipped (X.json.gz) beside or instead of X.json.
"""
from __future__ import annotations

import argparse
import collections
import gzip
import hashlib
import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

RULES = HERE / "orderleads-rules.json"
SCOPE_FILE = HERE.parent / ".claude" / "skills" / "world-edit-compatibility" / "references" / "record-field-scope.json"
FIELD = re.compile(r"^\S+ \S+: (\S+) - ")
REGISTRATION = {"Quest", "DialogTopic", "DialogResponses", "Scene", "FormList", "Location", "StoryManagerQuestNode", "StoryManagerBranchNode",
                "StoryManagerEventNode", "Package", "DialogBranch", "DialogView"}
STAGES = ("map", "overlap", "contact", "empty", "census", "state", "navown", "seam", "water", "dep", "drift", "route", "records")
STAGE_FILES = {"map": "map", "overlap": "overlap", "contact": "contact", "empty": "empty", "census": "census", "state": "state", "navown": "navown",
               "seam": "seam", "water": "water", "dep": "dep", "drift": "drift", "route": "route", "records": "records"}

REASON_ONE = "names one plugin: not a clash between mods"
REASON_BASE = "differs from the base game only: not a clash between mods"
REASON_EDGE = "no row: the two share records or cells, but no check raised a clash"
REASON_MASK = "only the compressed-storage flag differs"

HEAD = ("These are leads to open, not verdicts. Each names where two mods meet in a way one of the checks measured; "
        "nothing here is tested in game. The plugins are named by the rows: a pair is where to look, not a joint cause, "
        "and whether both are needed takes a run without each.")


# ---- files -----------------------------------------------------------------------------------------------------------

def load(path):
    p = Path(path)
    if p.exists():
        return json.loads(p.read_text(encoding="utf-8"))
    gz = Path(str(p) + ".gz")
    if gz.exists():
        with gzip.open(gz, "rt", encoding="utf-8") as f:
            return json.load(f)
    return None


# ---- the order's facts -----------------------------------------------------------------------------------------------

class Ctx:
    def __init__(self, m):
        self.name = {p["name"].lower(): p["name"] for p in m["plugins"]}
        self.base = {p["name"] for p in m["plugins"] if p["baseGame"]}
        self.masters = {p["name"]: list(p["masters"]) for p in m["plugins"]}
        self.folder = {p["name"].lower(): p.get("folder") for p in m["plugins"]}
        self.plugins_of = collections.defaultdict(list)
        for p in m["plugins"]:
            if p.get("folder"):
                self.plugins_of[p["folder"].lower()].append(p["name"])
        self._closure = {}

    def all_masters(self, n):
        if n not in self._closure:
            seen, stack = set(), list(self.masters.get(n, ()))
            while stack:
                x = stack.pop()
                if x not in seen:
                    seen.add(x)
                    stack.extend(self.masters.get(x, ()))
            self._closure[n] = {x.lower() for x in seen}
        return self._closure[n]

    def norm(self, n):
        return self.name.get((n or "").lower())

    def is_base(self, n):
        return self.norm(n) in self.base

    def mods(self, *names):
        out = []
        for n in names:
            x = self.norm(n)
            if x and x not in self.base and x not in out:
                out.append(x)
        return out

    def related(self, a, b):
        return b.lower() in self.all_masters(a) or a.lower() in self.all_masters(b)


# ---- rows ------------------------------------------------------------------------------------------------------------

def _conflicts(doc, family, kind_fn):
    for c in (doc or {}).get("Conflicts") or []:
        nec = c.get("Necessary") or []
        alt = c.get("Alternative") or []
        obj = "|".join(str(x) for x in [c.get("Kind"), nec[0].get("FormKey") if nec else None, alt[0].get("FormKey") if alt else None])
        yield {"family": family, "kind": kind_fn(c), "a": c.get("A"), "b": c.get("B"), "place": (nec[0].get("Label") if nec else None), "isPlace": True,
               "obj": obj, "text": c.get("Summary"), "fact": False}


def rows(d):
    """Every row of a pass, in a fixed order, normalised. Record rows keep their source row as 'src'."""
    d = Path(d)
    for name, fam in (("overlap", "overlap"), ("contact", "contact"), ("empty", "empty"), ("navown", "navmesh"), ("seam", "seam"), ("water", "water"), ("drift", "quest-alias")):
        yield from _conflicts(load(d / f"{name}.json"), fam, lambda c, fam=fam: fam)

    def state_kind(c):
        k, s = c.get("Kind", ""), c.get("Summary") or ""
        return "state:Blocked" if k.startswith("STATE-ROUTE") and "Blocked:" in s and "LikelyBlocked" not in s else "state:other"
    yield from _conflicts(load(d / "state.json"), "state", state_kind)
    for r in (load(d / "route.json") or {}).get("Rows") or []:
        if r.get("Verdict") in ("Blocked", "LikelyBlocked"):
            by = r.get("By") or []
            yield {"family": "route", "kind": f"route:{r['Verdict']}", "a": by[0] if by else None, "b": by[1] if len(by) > 1 else None, "place": r.get("Cell"), "isPlace": True,
                   "obj": r.get("FormKey"), "text": f"{r['Verdict']}: {r.get('Mesh')} {r.get('FormKey')} in {r.get('Cell')}. {r.get('Why')}", "fact": False}
    for r in (load(d / "dep.json") or {}).get("rows") or []:
        yield {"family": "left-behind", "kind": "left-behind", "a": r.get("mover"), "b": r.get("author"), "place": r.get("cell"), "isPlace": True,
               "obj": f"{r.get('dependent')}|{r.get('host')}", "text": f"{r.get('dependent')} left behind when {r.get('mover')} moved {r.get('host')} in {r.get('cell')}: {r.get('why')}", "fact": False}
    for check, v in (load(d / "records.json") or {}).items():
        for c in v["rows"]:
            k = c.get("Kind")
            nec = c.get("Necessary") or []
            fk = nec[0].get("FormKey") if nec else None
            text = c.get("Summary") or ""
            if k == "LOST-EDIT" or " lost to " in text:
                rtype = text.split(" ", 1)[0]
                kind = f"lost:{rtype}" if rtype in ("Quest", "DialogTopic", "DialogResponses", "Scene", "Cell") else ("lost:registration" if rtype in REGISTRATION else "lost:other")
                yield {"family": "records", "kind": kind, "a": c.get("A"), "b": c.get("B"), "place": fk if rtype == "Cell" else rtype, "isPlace": rtype == "Cell",
                       "obj": f"{fk}|{text.split(':')[-1].split(' - ')[0].strip() if ':' in text else ''}", "text": text, "fact": True, "lost": True, "src": c, "check": check}
            elif k == "MISSING-MASTER":
                yield {"family": "masters", "kind": "MISSING-MASTER", "a": c.get("NativePluginSection"), "b": None, "place": None, "isPlace": False, "obj": text, "text": text, "fact": True, "src": c, "check": check}
            else:
                yield {"family": "one-mod-records", "kind": k, "a": c.get("A") or c.get("NativePluginSection"), "b": c.get("B"), "place": None, "isPlace": False,
                       "obj": fk or text, "text": text, "fact": True, "src": c, "check": check}


# ---- the compressed-storage flag --------------------------------------------------------------------------------------

def record_flags(scope_file=None) -> dict:
    """The record-flag table shared with the record lane (record-field-scope.json `recordFlags`): the fields that hold
    the header flags, the bits by name and the storage-only bits; empty where the skill data has none."""
    try:
        rf = json.loads(Path(scope_file or SCOPE_FILE).read_text(encoding="utf-8")).get("recordFlags") or {}
    except (OSError, ValueError):
        rf = {}
    bits = {str(k): int(v) for k, v in (rf.get("bits") or {}).items()}
    if not rf.get("field") or not bits:
        return {}
    return {"fields": [rf["field"], *(rf.get("sameBitsAs") or [])], "bits": bits,
            "mask": sum(bits[n] for n in rf.get("storageOnly") or [] if n in bits)}


_FLAGS = record_flags()


def _flag_table(rules=None):
    return dict(_FLAGS.get("bits") or {})


def flag_value(v, table):
    """A record-flags value as an int, from a number or comma-separated flag names; None when it can't be read."""
    if v is None:
        return None
    s = str(v).strip()
    if re.fullmatch(r"-?\d+", s):
        return int(s) & 0xFFFFFFFF
    total = 0
    for part in [x.strip() for x in s.split(",") if x.strip()]:
        if part not in table:
            return None
        total |= table[part]
    return total


def compressed_only(row, rules):
    """True when a record row's lost and winning flag values differ only in the storage-only flags (Compressed)."""
    mf = _FLAGS
    src = row.get("src") or {}
    if not mf or src.get("Field") not in mf["fields"]:
        return False
    table = _flag_table(rules)
    bit = mf["mask"]
    nec = src.get("Necessary") or []
    vals = (nec[0].get("Values") if nec else None) or {}
    a, b = flag_value(vals.get("lost"), table), flag_value(vals.get("winner"), table)
    if a is None or b is None:
        return False
    return a != b and (a & ~bit) == (b & ~bit)


def flag_census(all_rows, rules):
    """Every record-flags row by field and check: masked, kept with the bits that differ named, kept unreadable."""
    mf = _FLAGS
    if not mf:
        return {}
    table = _flag_table(rules)
    names = {v: k for k, v in table.items()}
    out = collections.defaultdict(lambda: {"masked": 0, "keptOtherBits": collections.Counter(), "keptUnreadable": 0, "keptEqual": 0})
    for r in all_rows:
        src = r.get("src") or {}
        if src.get("Field") not in mf["fields"]:
            continue
        c = out[f"{r.get('check')}:{src['Field']}"]
        nec = src.get("Necessary") or []
        vals = (nec[0].get("Values") if nec else None) or {}
        a, b = flag_value(vals.get("lost"), table), flag_value(vals.get("winner"), table)
        if compressed_only(r, rules):
            c["masked"] += 1
        elif a is None or b is None:
            c["keptUnreadable"] += 1
        elif a == b:
            c["keptEqual"] += 1
        else:
            x = a ^ b
            bits = [names.get(1 << i, f"bit 0x{1 << i:X}") for i in range(32) if x >> i & 1]
            c["keptOtherBits"][", ".join(bits)] += 1
    return {k: {"masked": v["masked"], "keptOtherBits": dict(v["keptOtherBits"].most_common()), "keptUnreadable": v["keptUnreadable"], "keptEqual": v["keptEqual"]}
            for k, v in sorted(out.items())}


# ---- dummies, admission, matching ------------------------------------------------------------------------------------

def is_dummy(p, known=None):
    """An empty placeholder: no records and, by the map's size, the 79-byte header-only file; a map without sizes uses a
    measured list of names."""
    if p.get("baseGame") or p.get("newRecords") or p.get("overrides"):
        return False
    if "bytes" in p:
        return p.get("bytes") == 79
    return known is not None and p["name"].lower() in known


def admit(group):
    """None for a lead, else the reason it is not one."""
    if group["family"] == "edge":
        return REASON_EDGE
    if len(group["mods"]) < 2:
        return REASON_BASE if group.get("allBase") else REASON_ONE
    return None


def _strip(label):
    return re.sub(r" \((int|ext)\)$", "", str(label)).strip()


def place_names(label, cells):
    """The names a row's place answers to, lower case: its label, and through the cell table its FormKey, EditorID and
    worldspace-and-grid label."""
    s = _strip(label)
    out = {s.lower()}
    info = cells.get("byKey", {}).get(s)
    if info:
        if info.get("edid"):
            out.add(info["edid"].lower())
        if info.get("worldspace") is not None and info.get("grid"):
            out.add(f"{info['worldspace']} ({info['grid'][0]},{info['grid'][1]})".lower())
    for k in cells.get("byName", {}).get(s.lower(), ()):
        out.add(k.lower())
    return out


def place_matches(label, query, cells, prefix=False):
    q = query.strip().lower()
    names = place_names(label, cells)
    return any(n.startswith(q) for n in names) if prefix else q in names


def pair_matches(lead, a, b, folders=None):
    return {m.lower() for m in lead["mods"]} == {a.lower(), b.lower()}


def default_added(pass_dir, added):
    if added:
        return list(added)
    pj = load(Path(pass_dir) / "pass.json") or {}
    return list(pj.get("added") or [])


def _pass_file(d, pj, key):
    """A file the pass folder names in pass.json (relative to the folder), or None."""
    v = (pj.get(key) or {}).get("file") if isinstance(pj.get(key), dict) else pj.get(key)
    if not v:
        return None
    p = Path(v)
    return p if p.is_absolute() else Path(d) / p


def stage_record(d, pj):
    """Each stage: ran, failed, refused, skipped, not run (only when the record says it is complete) or unknown."""
    d = Path(d)
    rec = pj.get("stages") if isinstance(pj.get("stages"), dict) else load(d / "stages.json")
    complete = bool(pj.get("stagesComplete"))
    out = {}
    for s in STAGES:
        v = (rec or {}).get(s)
        present = (d / f"{STAGE_FILES[s]}.json").exists() or (d / f"{STAGE_FILES[s]}.json.gz").exists()
        if v is None:
            out[s] = {"status": "not run" if complete else "unknown", "outputPresent": present,
                      "why": "the pass's stage record says it did not run" if complete else "the pass's stage record does not cover it"}
        elif v.get("status") == "unknown":
            out[s] = {"status": "unknown", "outputPresent": present, "why": v.get("why") or "the pass's stage record does not cover it"}
        elif v.get("skipped"):
            out[s] = {"status": "skipped", "outputPresent": present, "why": "skipped"}
        elif v.get("refused"):
            out[s] = {"status": "refused", "outputPresent": present, "why": v["refused"]}
        elif v.get("exit") not in (0, None) or v.get("stopped"):
            out[s] = {"status": "failed", "outputPresent": present, "why": f"stopped: {v.get('stopped')}" if v.get("stopped") else f"exit {v.get('exit')}"}
        else:
            out[s] = {"status": "ran", "outputPresent": present, "why": ""}
    return out


def unassessed(d, m, rules, rec_doc, pj=None):
    d = Path(d)
    rec = stage_record(d, pj or {})
    # unassessed: a stage that did not run, failed, was refused or skipped, or whose record is unknown and left no output
    st = [{"stage": s, "status": v["status"], "why": v["why"]} for s, v in rec.items()
          if v["status"] in ("not run", "failed", "refused", "skipped") or (v["status"] == "unknown" and not v["outputPresent"])]
    unknown_read = [s for s, v in rec.items() if v["status"] == "unknown" and v["outputPresent"]]
    layers = collections.defaultdict(list)
    for folder, v in (m.get("runtimeLayers") or {}).items():
        for k in v:
            layers[k].append(folder)
    rt = load(d / "route-targets.json") or {}
    cov = {}
    for k, v in (rec_doc or {}).items():
        bad = [x for x in v.get("coverage") or [] if any((x.get("Unjudgeable") or {}).values())]
        if bad:
            cov[k] = bad
    return {"stages": st, "stagesUnknownButRead": unknown_read, "stageRecord": rec, "runtimeLayers": {k: sorted(v) for k, v in sorted(layers.items())},
            "registry": {k: v for k, v in (rules.get("coverage", {}).get("registry") or {}).items() if str(v).startswith("unassessed")},
            "runtimeLayersNote": rules.get("coverage", {}).get("runtimeLayers"),
            "unreadable": [p["name"] for p in m["plugins"] if p.get("error")] + list(m.get("archivesUnreadable") or []),
            "routeNotReached": len(rt.get("notReached") or []), "recordCoverage": cov}


# ---- grouping and order ----------------------------------------------------------------------------------------------

def next_step(g):
    a, b = (g["mods"] + ["", ""])[:2]
    f, n = g["family"], g["objects"]
    where = ", ".join(x for x, _ in g["places"].most_common(3)) or "several places"
    fields = ", ".join(x for x, _ in g["fields"].most_common(3))
    if f == "records":
        w = g["winners"].most_common(1)
        win = w[0][0] if w else b
        lose = a if win == b else b
        rec = g["records"][0] if g["records"] else ""
        return (f"{win} wins over {lose} on {fields or 'these fields'} in {n} records. Open {rec} in xEdit: if you want {lose}'s "
                f"{g['fields'].most_common(1)[0][0] if g['fields'] else 'values'} back, a small patch carries it forward; if {win}'s is what you want, leave it.")
    if f == "asset":
        return f"{g['examples'][0][:110]}. Decide which should sit on top in MO2; a face mesh that disagrees with its NPC record shows as a dark face."
    if f == "edge":
        return "They share records or cells but no row was raised: no clash measured, not shown compatible."
    if f == "route":
        return f"An object from {a or 'this mod'} stands across a walking path in {where}. Walk it in game, or compare the order without each plugin, before patching."
    if f == "navmesh":
        return f"Navmesh in {where}: compare the order without each plugin to see which of the two the break needs, then rebuild it in the Creation Kit or find a navmesh patch."
    if f == "quest-alias":
        return f"A quest's actors moved apart in {where}. Play that quest stage, or check the mod's notes, before changing it."
    if f in ("overlap", "contact", "empty", "state", "left-behind", "seam", "water"):
        return f"Look at {where} with both on, {n} objects. A run without each plugin can say which of the two causes it; look for a patch from either author first."
    return "Read the mod's own notes on this."


def _new_group(mods, fam, tier, one):
    return {"mods": mods, "family": fam, "tier": tier, "objs": set(), "rows": 0, "kinds": collections.Counter(), "examples": [], "allFact": True, "oneMod": one,
            "places": collections.Counter(), "fields": collections.Counter(), "winners": collections.Counter(), "records": [], "members": [], "allBase": True}


def _add(g, r, field):
    g["objs"].add(r["obj"])
    g["rows"] += 1
    g["kinds"][r["kind"]] += 1
    g["allFact"] = g["allFact"] and r["fact"]
    if r["place"]:
        g["places"][str(r["place"])] += 1
    if field:
        g["fields"][field] += 1
    if r.get("lost") and r["b"]:
        g["winners"][r["b"]] += 1
        fk = (r["obj"] or "").split("|")[0]
        if len(g["records"]) < 3 and fk not in g["records"]:
            g["records"].append(fk)
    if len(g["examples"]) < 3:
        g["examples"].append(r["text"])
    g["members"].append(r)


def order(groups, shares):
    by = collections.defaultdict(lambda: collections.defaultdict(list))
    for g in groups:
        by[g["tier"]][g["family"]].append(g)
    streams = {}
    for t, fams in by.items():
        for lst in fams.values():
            lst.sort(key=lambda g: (-g["objects"], g["mods"]))
        seq, names, i = [], sorted(fams, key=lambda f: (-len(fams[f]), f)), 0
        while any(i < len(fams[f]) for f in names):
            seq += [fams[f][i] for f in names if i < len(fams[f])]
            i += 1
        streams[t] = iter(seq)
    out, live = [], True
    while live:
        live = False
        for t in ("1", "2", "3", "4"):
            for _ in range(shares[t]):
                g = next(streams.get(int(t), iter(())), None)
                if g is not None:
                    out.append(g)
                    live = True
    return out


def _cells(m, cell_names):
    by_key = {}
    for k, v in (m.get("cells") or {}).items():
        by_key[k] = v
    if cell_names:
        for k, v in ((load(cell_names) or {}).get("cells") or {}).items():
            by_key.setdefault(k, v)
    by_name = collections.defaultdict(set)
    for k, v in by_key.items():
        if v.get("edid"):
            by_name[v["edid"].lower()].add(k)
        if v.get("worldspace") is not None and v.get("grid"):
            by_name[f"{v['worldspace']} ({v['grid'][0]},{v['grid'][1]})".lower()].add(k)
    return {"byKey": by_key, "byName": dict(by_name)}


class Result:
    def __init__(self, leads, message=None, asked=None, notes=None):
        self.leads, self.message, self.asked, self.notes = leads, message, asked or {}, notes or []


def _public(g, rank=None):
    return {"mods": g["mods"], "family": g["family"], "tier": g["tier"], "objects": g["objects"], "rows": g["rows"],
            "places": [[p, n] for p, n in g["places"].most_common()], "examples": g["examples"], "rank": rank if rank is not None else g.get("rank"),
            "list": "confirmed" if g["allFact"] and g["rows"] else "screened-only", "kinds": dict(g["kinds"]), "fields": g["fields"].most_common(6),
            "next": next_step(g), "assetOnly": g.get("assetOnly", False)}


class Index:
    def __init__(self, d, added, rules, dummies, cell_names):
        self.pass_dir = Path(d)
        raw_rules = Path(rules or RULES).read_bytes()
        self.rules = json.loads(raw_rules)
        self.rules_sha256 = hashlib.sha256(raw_rules).hexdigest()
        R = self.rules
        m = load(self.pass_dir / "map.json")
        if m is None:
            raise ValueError(f"{self.pass_dir} has no map.json; run ordermap (or orderpass) for this order first.")
        self.map = m
        ctx = self.ctx = Ctx(m)
        pj = self.pass_info = load(self.pass_dir / "pass.json") or {}
        # what the pass folder says about itself, unless the caller names another file
        dummies = dummies or _pass_file(self.pass_dir, pj, "dummies")
        cell_names = cell_names or _pass_file(self.pass_dir, pj, "cellNames")
        self.inputs = {"dummies": str(dummies) if dummies else None, "cellNames": str(cell_names) if cell_names else None,
                       "passJson": bool(pj)}
        self.cells = _cells(m, cell_names)
        # dummies: the map's sizes, else a measured list, else none can be told
        if any("bytes" in p for p in m["plugins"]):
            self.dummy_source, known = "map", None
        elif dummies and load(dummies) is not None:
            self.dummy_source, known = "list", {x.lower() for x in (load(dummies) or {}).get("dummies", [])}
        else:
            self.dummy_source, known = "none", None
        self.dummies = sorted(p["name"] for p in m["plugins"] if is_dummy(p, known))
        kinds = R["kinds"]
        groups, seen_pairs = {}, set()
        set_rows = collections.Counter()
        all_rows = list(rows(self.pass_dir))
        self.raw_rows = len(all_rows)
        rec_doc = load(self.pass_dir / "records.json")
        self.flag_census = flag_census(all_rows, R)
        for r in all_rows:
            mods = sorted(ctx.mods(r["a"], r["b"]))
            if len(mods) == 2:
                seen_pairs.add(tuple(mods))
            if compressed_only(r, R):
                set_rows[REASON_MASK] += 1
                continue
            kind = r["kind"]
            tier = kinds.get(kind, kinds["lost:other"] if kind.startswith("lost") else 4)
            fam = r["family"]
            field = None
            if r.get("lost"):
                x = FIELD.match(r["text"] or "")
                field = x.group(1) if x else None
            if field in R["fieldsNoEffect"]:
                tier = 4
            elif field in R["fieldsLesser"] and tier == 1:
                tier = 2
            one = len(mods) < 2
            names_base = ctx.is_base(r["a"]) or ctx.is_base(r["b"])
            if one:
                tier = R["oneModBlockedTier"] if kind == "route:Blocked" and len(mods) == 1 else 4
                if tier == 1 and mods[0].lower().startswith("unofficial skyrim special edition patch"):
                    tier = 2
                if R.get("admission", True):
                    set_rows[REASON_BASE if names_base else REASON_ONE] += 1
            elif r.get("lost") and r["b"] and ctx.norm(r["a"]) and r["a"].lower() in ctx.all_masters(ctx.norm(r["b"]) or ""):
                tier = 4
            else:
                if ctx.related(mods[0], mods[1]):
                    tier = min(4, tier + 1)
                if any(x.lower().startswith("unofficial skyrim special edition patch") for x in mods):
                    tier = min(4, tier + 1)
            key = (tuple(mods), fam, tier)
            g = groups.get(key)
            if g is None:
                g = groups[key] = _new_group(mods, fam, tier, one)
            g["allBase"] = g["allBase"] and bool(names_base)
            _add(g, r, field)
        folder_plugins = collections.defaultdict(list)
        for p in m["plugins"]:
            if p["folder"]:
                folder_plugins[p["folder"]].append(p["name"])
        plugin_pairs = set()
        for e in m["edges"]:
            pr = tuple(sorted((e["a"], e["b"])))
            plugin_pairs.add(pr)
            if pr not in seen_pairs:
                g = groups[(pr, "edge", 4)] = _new_group(list(pr), "edge", 4, False)
                g["objs"] = set(range(e["records"] + e["interior"] + e["exterior"]))
                g["kinds"]["edge"] = 1
                g["allFact"] = False
                g["examples"] = [f"share {e['records']} records, {e['interior']} interior and {e['exterior']} exterior cells, {e['neighbours']} neighbouring cell sightings, {e['baseLinks']} placed bases, {e['formLinks']} form links"]
        for e in m["assetEdges"]:
            if not e["different"]:
                continue
            pa, pb = folder_plugins.get(e["a"], []), folder_plugins.get(e["b"], [])
            linked = any(tuple(sorted((x, y))) in plugin_pairs for x in pa for y in pb)
            related = any(ctx.related(x, y) for x in pa for y in pb)
            t = e["differentByTop"]
            tier = kinds["asset:mesh-or-script"] if (t.get("meshes", 0) + t.get("scripts", 0) > 0 and not linked and not related) else kinds["asset:other"]
            g = groups[(tuple(sorted((e["a"], e["b"]))), "asset", tier)] = _new_group(sorted((e["a"], e["b"])), "asset", tier, False)
            g["objs"] = set(range(e["different"]))
            g["kinds"]["asset-only" if not linked and not related else "asset"] = 1
            g["allFact"] = False
            g["assetOnly"] = not linked and not related
            g["examples"] = [f"{e['different']} paths differ, {e['identical']} identical; by kind {t}; " + "; ".join(e["examples"][:2])]
        for g in groups.values():
            g["objects"] = len(g["objs"])
            g["reason"] = admit(g) if R.get("admission", True) else None
        set_groups = collections.Counter(g["reason"] for g in groups.values() if g["reason"])
        lead_groups = [g for g in groups.values() if not g["reason"]]
        rest = [g for g in groups.values() if g["reason"]]
        shares = R["tierShares"]
        if R.get("orderOver", "leads") == "all":
            ordered = order(list(groups.values()), shares)
            self._leads = [g for g in ordered if not g["reason"]]
            self._record = ordered
        else:
            self._leads = order(lead_groups, shares)
            self._record = self._leads + order(rest, shares)
        for i, g in enumerate(self._leads, 1):
            g["rank"] = i
        self.set_aside = {"groups": dict(set_groups), "rows": dict(set_rows)}
        self.leads = [_public(g) for g in self._leads]
        self.record = [dict(_public(g), lead=not g["reason"], reason=g["reason"]) for g in self._record]
        real = [p["name"] for p in m["plugins"] if not p["baseGame"] and p["name"] not in self.dummies]
        named = {x for g in groups.values() for x in g["mods"]}
        self.real_plugins = len(real)
        self.no_signal = sorted(set(real) - named - {x for pr in plugin_pairs for x in pr})
        self.unassessed = unassessed(self.pass_dir, m, R, rec_doc, pj)
        self.added = default_added(self.pass_dir, added)

    # ---- queries ----------------------------------------------------------------------------------------------------

    def _expand(self, names):
        """Plugins and folders a name list stands for, lower case: a folder takes its plugins, a plugin its folder."""
        out = set()
        for n in names:
            low = n.lower()
            out.add(low)
            if self.ctx.folder.get(low):
                out.add(self.ctx.folder[low].lower())
            for p in self.ctx.plugins_of.get(low, ()):
                out.add(p.lower())
        return out

    def _known(self, n):
        low = n.lower()
        return low in self.ctx.name or low in self.ctx.plugins_of or any(low in (e["a"].lower(), e["b"].lower()) for e in self.map["assetEdges"])

    def query(self, added=None, place=None, place_prefix=None, pair=None, family=None):
        asked = {k: v for k, v in (("added", added), ("place", place), ("place_prefix", place_prefix), ("pair", pair), ("family", family)) if v}
        kept = list(self._leads)
        if added:
            want = self._expand(added)
            kept = [g for g in kept if any(x.lower() in want for x in g["mods"])]
        if pair:
            a, b = pair
            kept = [g for g in kept if pair_matches(g, a, b)]
        if family:
            kept = [g for g in kept if g["family"] == family]
        out = []
        q = place or place_prefix
        for g in kept:
            if q:
                members = [r for r in g["members"] if r.get("isPlace") and r["place"] and place_matches(r["place"], q, self.cells, prefix=bool(place_prefix))]
                if not members:
                    continue
                sub = _new_group(g["mods"], g["family"], g["tier"], g["oneMod"])
                for r in members:
                    field = None
                    if r.get("lost"):
                        x = FIELD.match(r["text"] or "")
                        field = x.group(1) if x else None
                    _add(sub, r, field)
                sub["objects"] = len(sub["objs"])
                out.append(_public(sub, rank=g["rank"]))
            else:
                out.append(_public(g))
        for i, g in enumerate(out, 1):
            g["position"] = i
        msg = None if out else self._why_none(added, place or place_prefix, pair, family)
        notes = self._place_notes(kept, q) if q else []
        return Result(out, msg, asked, notes)

    def _place_notes(self, kept, q):
        """What a place query could not match because the pass lacks a name or a FormKey, said whatever the hit count."""
        by_key = re.fullmatch(r"[0-9A-Fa-f]{6}:\S.*", q.strip()) is not None
        n, leads = 0, set()
        for g in kept:
            for r in g["members"]:
                if not (r.get("isPlace") and r["place"]):
                    continue
                s = _strip(r["place"])
                key_shaped = re.fullmatch(r"[0-9A-Fa-f]{6}:.+", s) is not None
                if not by_key and key_shaped and not (self.cells["byKey"].get(s) or {}).get("edid") and not (self.cells["byKey"].get(s) or {}).get("grid"):
                    n += 1
                    leads.add(id(g))
                elif by_key and not key_shaped and s.lower() not in self.cells["byName"]:
                    n += 1
                    leads.add(id(g))
        if not n:
            return []
        if by_key:
            return [f"{n} rows in {len(leads)} leads this query could have kept are labelled by a cell name this pass has no FormKey for, so a FormKey can't match them; ask by name too."]
        return [f"{n} rows in {len(leads)} leads this query could have kept sit in cells this pass has no name for (record rows name a cell by FormKey), so a name can't match them; "
                "ask by FormKey too, or give the pass a cell-name table."]

    def _why_none(self, added, place, pair, family):
        for n in list(added or []) + list(pair or []):
            if not self._known(n):
                return f"{n} is not in this pass's order: no plugin or mod folder has that name."
            p = next((x for x in self.map["plugins"] if x["name"].lower() == n.lower()), None)
            if p and p["name"] in self.dummies:
                return f"{p['name']} is an empty placeholder (a dummy plugin with no records), so no check can name it."
        if pair:
            return f"No lead names both {pair[0]} and {pair[1]}" + (f" in {family}" if family else "") + (f" at {place}" if place else "") + "."
        if place:
            unnamed = sum(1 for g in self._leads for r in g["members"] if r.get("isPlace") and re.fullmatch(r"[0-9A-Fa-f]{6}:.+", _strip(r["place"])) and _strip(r["place"]) not in self.cells["byKey"])
            return (f"No lead has a row at {place}. {unnamed} lead rows sit in cells this pass has no name for; ask by FormKey "
                    "(for example 0213A0:Skyrim.esm) or by worldspace and grid (Tamriel (31,7)).")
        if added:
            n = ", ".join(added)
            return f"No lead names {n}: every row naming it names one plugin or the base game, or the map has only edges for it."
        if family:
            return f"No lead in {family}."
        return "This pass has no leads."

    # ---- output -----------------------------------------------------------------------------------------------------

    def render(self, result=None, whole=False, limit=None):
        if result is None:
            result = self.query(added=self.added) if (self.added and not whole) else self.query()
        asked = result.asked
        lim = limit or (self.rules["readingLimit"]["wholeOrderGroups"] if not asked else self.rules["readingLimit"]["oneModGroups"])
        L = []
        if asked.get("added") and set(asked) == {"added"}:
            title = f"Leads for the added mod {', '.join(asked['added'])}"
        elif asked:
            title = "Leads for " + "; ".join(f"{k.replace('_', ' ')} {' + '.join(v) if isinstance(v, (list, tuple)) else v}" for k, v in asked.items())
        else:
            title = "Leads for the whole order"
        L.append(f"# {title}\n")
        L.append(HEAD + "\n")
        if asked:
            L.append(f"{len(result.leads)} leads match, shown in the whole-order list's order (the whole-order rank is in brackets). "
                     f"The whole-order list: run read with --whole{' (' + str(len(self._leads)) + ' leads)' if True else ''}.\n")
        else:
            L.append(f"{len(self._leads)} leads in the whole order, from {self.raw_rows} rows. Reading limit {lim}.\n")
        sa = "; ".join(f"{n} {r}" for r, n in sorted({**{k: v for k, v in self.set_aside['rows'].items()}}.items()))
        sg = "; ".join(f"{n} groups: {r}" for r, n in sorted(self.set_aside["groups"].items()))
        L.append(f"Not offered as leads (kept in the record, index.json): rows: {sa or 'none'}. {sg}.\n")
        if result.message:
            L.append(f"**{result.message}**\n")
        for n in result.notes:
            L.append(f"**Not matched by this query:** {n}\n")
        for g in result.leads[:lim]:
            pos = g.get("position", g.get("rank"))
            places = ", ".join(self._place_label(p) for p, _ in g["places"][:10])
            L.append(f"{pos}. **{' and '.join(g['mods'])}** ({g['family']}, tier {g['tier']}, {g['list']}, {g['objects']} objects"
                     f"{' in ' + str(len(g['places'])) + ' places' if g['places'] else ''}) [whole-order rank {g['rank']}]")
            L.append(f"   {(g['examples'][0] or '')[:240]}")
            if places:
                L.append(f"   Places: {places}")
            L.append(f"   Next: {g['next']}")
        if len(result.leads) > lim:
            L.append(f"\n{len(result.leads) - lim} more past the reading limit; --limit N shows more.")
        L.append("\n## Dummy plugins\n")
        L.extend(dummy_lines(self))
        L.append(f"\n## No signal (not compatible: nothing measured them)\n\n{len(self.no_signal)} real plugins of {self.real_plugins}: {', '.join(self.no_signal) or 'none'}.")
        u = self.unassessed
        L.append("\n## Unassessed\n")
        if u.get("runtimeLayersNote"):
            L.append(u["runtimeLayersNote"] + "\n")
        for k, v in u["runtimeLayers"].items():
            L.append(f"- **{k}:** {len(v)} folders: {', '.join(v[:12])}{' ...' if len(v) > 12 else ''}")
        for s in u["stages"]:
            L.append(f"- **Stage {s['stage']}:** {s['status']} ({s['why']}); its check is unassessed in this pass.")
        if u.get("stagesUnknownButRead"):
            L.append(f"- **Stage record incomplete:** {', '.join(u['stagesUnknownButRead'])}: the pass's record doesn't say how they ended; their outputs are present and were read.")
        for k, v in u["registry"].items():
            L.append(f"- **{k}:** {v}")
        if u["unreadable"]:
            L.append(f"- **Unreadable:** {', '.join(u['unreadable'])}")
        if u["routeNotReached"]:
            L.append(f"- **Route targets not reached:** {u['routeNotReached']}")
        for k, v in u["recordCoverage"].items():
            L.append(f"- **{k}:** " + "; ".join(f"{x.get('Population')}: {json.dumps({a: b for a, b in (x.get('Unjudgeable') or {}).items() if b})}" for x in v))
        return "\n".join(L) + "\n"

    def _place_label(self, p):
        s = _strip(p)
        info = self.cells["byKey"].get(s)
        if info and info.get("edid"):
            return f"{info['edid']} ({s})"
        if info and info.get("worldspace") is not None and info.get("grid"):
            return f"{info['worldspace']} ({info['grid'][0]},{info['grid'][1]}) ({s})"
        return str(p)

    def to_json(self):
        return {"tool": "orderleads", "schema": 1, "pass": str(self.pass_dir), "rulesSha256": self.rules_sha256, "rawRows": self.raw_rows,
                "leads": len(self._leads), "setAside": self.set_aside, "added": self.added, "dummies": self.dummies, "dummySource": self.dummy_source,
                "realPlugins": self.real_plugins, "noSignal": self.no_signal, "unassessed": self.unassessed, "flagCensus": self.flag_census, "inputs": self.inputs,
                "record": self.record}


def dummy_lines(idx):
    if idx.dummy_source == "none":
        return ["Dummy plugins could not be told: this map records no plugin sizes and no list of dummies was given."]
    src = "the map's plugin sizes" if idx.dummy_source == "map" else "a measured list (this map records no sizes)"
    out = [f"{len(idx.dummies)} empty placeholders (no records, the 79-byte header-only file), by {src}. Not counted as real plugins and not on the no-signal list:"]
    out.append(", ".join(idx.dummies) if idx.dummies else "none.")
    return out


def build(pass_dir, added=None, rules=None, dummies=None, cell_names=None):
    return Index(pass_dir, added, rules, dummies, cell_names)


# ---- command line ----------------------------------------------------------------------------------------------------

def main(argv=None):
    # rows carry non-ASCII text (a Δ, an em dash); a Windows console's code page must not turn a read into a crash
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure") and (stream.encoding or "").lower().replace("-", "") != "utf8":
            stream.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(prog="orderleads.py")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("build", "read"):
        s = sub.add_parser(name)
        s.add_argument("--pass", dest="pass_dir", required=True, help="a pass folder (orderpass.py's output)")
        s.add_argument("--added", action="append", default=[], help="an added mod's plugin or folder; repeat for several")
        s.add_argument("--rules", default=None, help="a rules file (default orderleads-rules.json beside this script)")
        s.add_argument("--dummies", default=None, help="a measured dummy list for a map without plugin sizes")
        s.add_argument("--cell-names", default=None, help="a cell-name table for a map without cell names")
    b = sub.choices["build"]
    b.add_argument("--out", default=None, help="where index.json, LEADS.md and LEADS-whole.md go (default the pass folder)")
    r = sub.choices["read"]
    r.add_argument("--place", default=None, help="a cell EditorID, FormKey, or worldspace and grid as rows print it")
    r.add_argument("--place-prefix", default=None, help="every place whose name starts with this")
    r.add_argument("--pair", nargs=2, default=None, metavar=("A", "B"), help="two plugins (or two mod folders)")
    r.add_argument("--family", default=None, help="one check family (records, overlap, navmesh, asset, ...)")
    r.add_argument("--whole", action="store_true", help="the whole-order list, not the added mod's view")
    r.add_argument("--limit", type=int, default=None, help="how many leads to print")
    r.add_argument("--format", choices=("md", "json"), default="md")
    a = ap.parse_args(argv)
    with Run(f"orderleads-{a.cmd}", stage_source="counted") as run:
        run.stage("index", total=1, unit="passes")
        try:
            idx = build(a.pass_dir, a.added or None, a.rules, a.dummies, a.cell_names)
        except (ValueError, OSError) as ex:
            print(str(ex), file=sys.stderr)
            return 2
        run.feature("rows", idx.raw_rows, "rows").feature("leads", len(idx.leads), "leads")
        run.advance(1)
        if a.cmd == "build":
            out = Path(a.out or a.pass_dir)
            out.mkdir(parents=True, exist_ok=True)
            (out / "index.json").write_text(json.dumps(idx.to_json(), ensure_ascii=False), encoding="utf-8")
            (out / "LEADS.md").write_text(idx.render(), encoding="utf-8")
            (out / "LEADS-whole.md").write_text(idx.render(whole=True), encoding="utf-8")
            run.artifact(str(out / "index.json"))
            print(json.dumps({"out": str(out), "rows": idx.raw_rows, "leads": len(idx.leads), "setAside": idx.set_aside, "added": idx.added,
                              "dummies": len(idx.dummies), "dummySource": idx.dummy_source}, ensure_ascii=False))
            return 0
        asked = any((a.place, a.place_prefix, a.pair, a.family)) or a.whole or a.added
        if asked and not a.whole:
            res = idx.query(added=a.added or (idx.added if not any((a.place, a.place_prefix, a.pair, a.family)) else None) or None,
                            place=a.place, place_prefix=a.place_prefix, pair=tuple(a.pair) if a.pair else None, family=a.family)
        elif a.whole:
            res = idx.query()
        else:
            res = None
        if a.format == "json":
            r_ = res or (idx.query(added=idx.added) if idx.added else idx.query())
            print(json.dumps({"asked": r_.asked, "message": r_.message, "notes": r_.notes, "matched": len(r_.leads), "leads": r_.leads[: a.limit or len(r_.leads)]}, ensure_ascii=False))
        else:
            sys.stdout.write(idx.render(res, whole=a.whole, limit=a.limit))
    return 0


if __name__ == "__main__":
    sys.exit(main())
