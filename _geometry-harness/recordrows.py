"""recordrows - record-lane rows derived from a record import's own MCP transcripts.

  python _geometry-harness/recordrows.py --request REQ.jsonl --response RESP.jsonl --configuration CONF.json --out ROWS.json

A record import carries the raw `housecarl_read_plugin_file` requests and responses it was counted from. This reads
them back and writes one row per record, field and losing plugin on the setup: a plugin wrote the field with a value
different from the one it overrides (it authored a change), and the setup's winning value differs from its own. The
row names the losing plugin (A) and the winner (B), the field, the three values and the reads they came from, in the
conflict-file shape the report compiler reads. It measures nothing: the output is a pure function of the transcript
bytes, the setup's plugin order and the field scope.

The fields compared come from the generated schema reference (.claude/skills/mutagen-reference/references/): a field
whose type is, or transitively contains, another major record is a child record and is not a field of this one; the
identity field is not compared; file bookkeeping, group bookkeeping and runtime-merged fields are named in skill data
(.claude/skills/world-edit-compatibility/references/record-field-scope.json) by their schema names. A field the
capture did not read, a field the schema does not know and a record whose read failed are unjudged with the reason,
never treated as equal. Lists are compared as multisets; numbers at four decimals with -0 equal to 0.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REF_DIR = HERE.parent / ".claude" / "skills" / "mutagen-reference" / "references"
SCOPE_FILE = HERE.parent / ".claude" / "skills" / "world-edit-compatibility" / "references" / "record-field-scope.json"
HEAD = re.compile(r"^type=(\S+)\s+formid=(\S+)\s+editorid=(.*)$")
INDEX = re.compile(r"\[\d+\]")
LIST_SUMMARY = re.compile(r"^\[list: \d+ item\(s\)\]$")
NUM = re.compile(r"^-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?$")

REASON_NOT_READ = "a field the capture did not read"
REASON_DEPTH = "a field not read at the same depth by every plugin"
REASON_TRUNCATED = "a field the read's expansion was cut off before"
TRUNCATED = "\u2026"
REASON_UNKNOWN = "a field the schema does not name"
REASON_FAILED = "the record read failed for one plugin"
MERGED = "merged at runtime, not compared"


def sha256(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def _jsonl(path: Path) -> dict[str, list]:
    return {r["name"]: r for r in (json.loads(x) for x in Path(path).read_text(encoding="utf-8").splitlines() if x.strip())}


class Scope:
    """The field scope: the schema reference plus the skill's named exclusions."""

    def __init__(self, ref_dir: Path, scope_file: Path):
        self.records = _jsonl(ref_dir / "records.jsonl")
        self.structs = _jsonl(ref_dir / "structs.jsonl")
        self.poly = _jsonl(ref_dir / "polymorphic.jsonl")
        self.arms = _jsonl(ref_dir / "arms.jsonl")
        self.data = json.loads(Path(scope_file).read_text(encoding="utf-8"))
        self.file_bk = set(self.data.get("fileBookkeeping") or [])
        self.struct_bk = set(self.data.get("structBookkeeping") or [])
        self.group_bk = {k: set(v) for k, v in (self.data.get("groupBookkeeping") or {}).items()}
        self.merged = {k: set(v) for k, v in (self.data.get("runtimeMerged") or {}).items()}
        self.merged_records = set(self.data.get("runtimeMergedRecords") or [])
        self._contains = {}

    def _type(self, name):
        if name in self.records:
            return self.records[name]
        if name in self.structs:
            return self.structs[name]
        return self.poly.get(name) or self.arms.get(name)

    def is_record(self, name) -> bool:
        if not name:
            return False
        if name in self.records:
            return True
        p = self.poly.get(name)
        return bool(p and p.get("arms") and all(a in self.records for a in p["arms"]))

    def contains_record(self, name, seen=None) -> bool:
        """A type that is a major record, or a struct that holds one at any depth (a worldspace block's cells)."""
        if not name:
            return False
        if name in self._contains:
            return self._contains[name]
        if self.is_record(name):
            return True
        seen = set(seen or ()) | {name}
        t = self.structs.get(name) or self.poly.get(name)
        out = False
        for f in (t or {}).get("fields") or []:
            ref = f.get("ref") or f.get("elemRef")
            if ref and ref not in seen and self.contains_record(ref, seen):
                out = True
                break
        self._contains[name] = out
        return out

    def problems(self) -> list[str]:
        """Every listed name that the schema does not carry, one sentence each."""
        out = []
        every = None
        for r in self.records.values():
            names = {f["n"] for f in r.get("fields") or []}
            every = names if every is None else every | names
        for n in sorted(self.file_bk):
            if n not in (every or set()):
                out.append(f"fileBookkeeping names {n}, which no record type in the schema has")
        struct_fields = {f["n"] for t in list(self.structs.values()) + list(self.poly.values())
                         for f in t.get("fields") or []}
        for n in sorted(self.struct_bk):
            if n not in struct_fields:
                out.append(f"structBookkeeping names {n}, which no struct in the schema has")
        for kind, table in (("groupBookkeeping", self.group_bk), ("runtimeMerged", self.merged)):
            for rtype, names in sorted(table.items()):
                r = self.records.get(rtype)
                if r is None:
                    out.append(f"{kind} names record type {rtype}, which the schema does not have")
                    continue
                have = {f["n"] for f in r.get("fields") or []}
                out += [f"{kind} names {rtype}.{n}, which the schema does not have" for n in sorted(names - have)]
        out += [f"runtimeMergedRecords names {n}, which the schema does not have"
                for n in sorted(self.merged_records - set(self.records))]
        return out

    def classify(self, rtype: str, path: str) -> str:
        if rtype in self.merged_records:
            return "merged"
        segs = INDEX.sub("", path).split(".")
        t = self.records.get(rtype)
        if t is None:
            return "unknown"
        top = segs[0]
        if top in self.file_bk or top in self.group_bk.get(rtype, ()):
            return "bookkeeping"
        if top in self.merged.get(rtype, ()):
            return "merged"
        for i, seg in enumerate(segs):
            if i == 0 and rtype in self.poly:
                t = self._merged_type(rtype)
            f = next((x for x in (t or {}).get("fields") or [] if x["n"] == seg), None)
            if f is None:
                return "unknown"
            if i == 0 and f.get("id"):
                return "identity"
            if i > 0 and seg in self.struct_bk:
                return "bookkeeping"
            ref = f.get("ref") or f.get("elemRef")
            if ref and self.contains_record(ref):
                return "child-record"
            if i + 1 < len(segs):
                t = self._merged_type(ref) if ref else None
                if t is None:
                    return "unknown"
        return "compared"

    def _merged_type(self, name):
        """A struct, or a polymorphic base with its arms' fields (a condition's Data names the arm's own fields)."""
        t = self._type(name)
        if t is None or name not in self.poly:
            return t
        fields = list(t.get("fields") or [])
        seen = {f["n"] for f in fields}
        arms = list(self.poly[name].get("arms") or []) + [a for a, t in self.arms.items() if t.get("base") == name]
        for a in dict.fromkeys(arms):
            for f in (self._type(a) or {}).get("fields") or []:
                if f["n"] not in seen:
                    seen.add(f["n"])
                    fields.append(f)
        return {"name": name, "fields": fields}


def load_scope(ref_dir: Path | None = None, scope_file: Path | None = None) -> Scope:
    return Scope(Path(ref_dir or REF_DIR), Path(scope_file or SCOPE_FILE))


# ------------------------------------------------------------------------------------------ transcript

def _messages(path: Path):
    """(batch marker, message) per JSON line; a batch starts at each `batchMarker` line."""
    batch = None
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        m = json.loads(line)
        if isinstance(m, dict) and "batchMarker" in m:
            batch = m["batchMarker"]
            continue
        for x in (m if isinstance(m, list) else [m]):
            if isinstance(x, dict):
                yield batch, x


def parse_text(text: str) -> dict:
    rec = {"status": "ok", "fields": {}}
    if not text.strip() or text.lstrip().startswith("error"):
        return {"status": "error", "error": text.strip()[:300], "fields": {}}
    infields = False
    for ln in text.splitlines():
        if not infields:
            m = HEAD.match(ln.strip())
            if m:
                rec["type"], rec["formKey"], rec["editorId"] = m.group(1), m.group(2), m.group(3).strip()
            if ln.startswith("fields (raw"):
                infields = True
            continue
        if ln.startswith("  ") and " = " in ln:
            k, v = ln[2:].split(" = ", 1)
            rec["fields"][k] = v
        elif ln.startswith("  ") and ln.rstrip().endswith(" ="):
            rec["fields"][ln[2:].rstrip()[:-2]] = ""
    if "type" not in rec:
        return {"status": "error", "error": text.strip()[:300], "fields": {}}
    return rec


def parse_transcript(request: Path, response: Path) -> dict:
    """Every record read (a `housecarl_read_plugin_file` call that names a formid), paired with its answer by batch
    and id. Reads with no formid (whole-plugin enumerations) and other calls are counted under `ignored`."""
    calls = {}
    ignored = collections.Counter()
    for batch, m in _messages(request):
        if m.get("method") != "tools/call":
            continue
        params = m.get("params") or {}
        args = params.get("arguments") or {}
        if params.get("name") != "housecarl_read_plugin_file" or not args.get("formid"):
            ignored["call that is not a record read"] += 1
            continue
        calls[(batch, m.get("id"))] = args
    reads = []
    for batch, m in _messages(response):
        args = calls.pop((batch, m.get("id")), None)
        if args is None:
            continue
        text = "".join(c.get("text", "") for c in ((m.get("result") or {}).get("content") or []) if isinstance(c, dict))
        if m.get("error"):
            text = "error: " + json.dumps(m["error"])
        rec = parse_text(text)
        rec.pop("formKey", None)
        reads.append({"batch": batch, "id": m.get("id"), "plugin": args["plugin"], "formKey": args["formid"],
                      "selection": args.get("fields"), "depth": args.get("depth"), **rec})
    ignored["request with no answer"] += len(calls)
    return {"reads": reads, "ignored": {k: v for k, v in ignored.items() if v}}


# ---------------------------------------------------------------------------------------------- rows

def _norm(v: str) -> str:
    v = str(v).strip()
    parts = [p.strip() for p in v.split(",")]
    if parts and all(NUM.match(p) for p in parts):
        out = []
        for p in parts:
            x = round(float(p), 4)
            out.append("0" if x == 0 else f"{x:.4f}".rstrip("0").rstrip("."))
        return ",".join(out)
    return v


def _keyed(fields: dict) -> dict[str, tuple]:
    """Stripped field path -> the multiset of its normalised leaf values (list order and summary lines ignored)."""
    parents = set()
    for p in fields:
        for i, ch in enumerate(p):
            if ch in ".[" and i:
                parents.add(p[:i])
    out = collections.defaultdict(list)
    for p, v in fields.items():
        if LIST_SUMMARY.match(str(v).strip()):
            continue
        if p in parents:
            continue  # a struct's or list's own summary line; its leaves are compared
        out[INDEX.sub("", p)].append(_norm(v))
    return {k: tuple(sorted(v)) for k, v in out.items()}


def _show(vals) -> str:
    return "; ".join(vals) if vals else "(absent)"


def _norm_path(p: str) -> str:
    return str(p).replace("\\", "/").lower()


class _View:
    """One plugin's reads of one record. A read covers a top-level field when it selected it, or selected nothing and
    was not cut off before it (a text dump prints every top-level field, absent ones included, so a read cut off at its line limit
    covers every name it printed in full, that is every name but the one it was cut off in). A field is taken from the narrowest covering read."""

    def __init__(self, reads):
        self.reads = reads
        self._k = {}
        self.cut = {}
        for r in reads:
            f = r["fields"]
            if TRUNCATED in f:
                tops = [k.split(".")[0].split("[")[0] for k in f if k != TRUNCATED]
                self.cut[id(r)] = (tops[-1] if tops else "", set(tops))
        self.keys = set()
        for r in reads:
            self.keys |= {k for k in self.keyed(r) if not k.startswith(TRUNCATED)}

    def keyed(self, r):
        if id(r) not in self._k:
            self._k[id(r)] = _keyed({k: v for k, v in r["fields"].items() if k != TRUNCATED})
        return self._k[id(r)]

    def covers(self, r, top) -> bool:
        if r.get("selection"):
            return top in set(r["selection"]) and id(r) not in self.cut
        if id(r) in self.cut:
            last, seen = self.cut[id(r)]
            return top in seen and top != last
        return True

    def read_for(self, top):
        ok = [r for r in self.reads if self.covers(r, top)]
        return min(ok, key=lambda r: (0 if r.get("selection") else 1)) if ok else None

    def truncated_at(self, top) -> bool:
        return any(id(r) in self.cut and not self.covers(r, top) for r in self.reads)


def rows_for_setup(reads: list[dict], plugins: list[dict], scope: Scope) -> dict:
    """The conflict-file emit for one setup: `plugins` in load order, each {name, path}."""
    order = {_norm_path(p.get("path")): (i, p["name"]) for i, p in enumerate(plugins) if p.get("path")}
    by_fk = collections.defaultdict(list)
    for r in reads:
        hit = order.get(_norm_path(r["plugin"]))
        if hit is None:
            continue
        by_fk[r["formKey"]].append((hit[0], hit[1], r))
    conflicts = []
    cov = collections.OrderedDict()

    def line(rtype):
        return cov.setdefault(rtype, {"Population": f"contested {rtype} records", "Judged": 0,
                                      "Unjudgeable": collections.Counter(), "SetAside": collections.Counter()})
    for fk in sorted(by_fk):
        per = collections.OrderedDict()
        for i, name, r in sorted(by_fk[fk], key=lambda x: x[0]):
            per.setdefault((i, name), []).append(r)
        chain = [(i, name, rs) for (i, name), rs in per.items()]
        rtype = next((r.get("type") for _, _, rs in chain for r in rs if r.get("type")), "unknown")
        c = line(rtype)
        if len(chain) < 2:
            continue
        if any(r["status"] != "ok" for _, _, rs in chain for r in rs):
            c["Unjudgeable"][REASON_FAILED] += 1
            continue
        c["Judged"] += 1
        views = [_View(rs) for _, _, rs in chain]
        keys = sorted(set().union(*[set(v.keys) for v in views]))
        for key in keys:
            cls = scope.classify(rtype, key)
            if cls in ("identity", "bookkeeping", "child-record"):
                continue
            if cls == "merged":
                c["SetAside"][MERGED] += 1
                continue
            if cls == "unknown":
                c["Unjudgeable"][REASON_UNKNOWN] += 1
                continue
            top = key.split(".")[0]
            picked = [v.read_for(top) for v in views]
            if any(x is None for x in picked):
                c["Unjudgeable"][REASON_TRUNCATED if any(v.truncated_at(top) for v in views) else REASON_NOT_READ] += 1
                continue
            got = [v.keyed(x) for v, x in zip(views, picked)]
            if len({x.get("depth") for x in picked}) > 1 and any(key not in g for g in got):
                c["Unjudgeable"][REASON_DEPTH] += 1
                continue
            vals = [g.get(key, ()) for g in got]
            win_i = len(chain) - 1
            for i in range(1, win_i):
                if vals[i] != vals[i - 1] and vals[win_i] != vals[i]:
                    reads_ = [{"batch": picked[j]["batch"], "id": picked[j]["id"], "plugin": chain[j][1]}
                              for j in (i - 1, i, win_i)]
                    a, b = chain[i][1], chain[win_i][1]
                    eid = next((r.get("editorId") for _, _, rs in chain for r in rs
                                if r.get("editorId") not in (None, "", "<none>")), None)
                    conflicts.append({
                        "Kind": "LOST-EDIT", "A": a, "B": b, "Field": key,
                        "Summary": f"{rtype} {eid or fk}: {key} - {a}'s value lost to {b}'s",
                        "Necessary": [{"FormKey": fk, "Label": f"{rtype} {eid or fk}",
                                       "Values": {"lost": _show(vals[i]), "winner": _show(vals[win_i]),
                                                  "overridden": _show(vals[i - 1])},
                                       "Reads": reads_}]})
    coverage = [{"Population": v["Population"], "Judged": v["Judged"],
                 "Unjudgeable": dict(v["Unjudgeable"]), "SetAside": dict(v["SetAside"])} for v in cov.values()]
    return {"Population": "contested records", "State": "baseline", "Conflicts": conflicts, "Coverage": coverage}


def derive(request: Path, response: Path, configuration: dict, scope: Scope, rel: dict | None = None) -> dict:
    """rows_for_setup over one import's transcripts, citing the bytes it read: `rel` maps each file to the path a
    reader of the output finds it at (inside the bundle)."""
    parsed = parse_transcript(Path(request), Path(response))
    emit = rows_for_setup(parsed["reads"], list(configuration.get("plugins") or []), scope)
    rel = rel or {}
    emit["derivedFrom"] = [{"path": str(rel.get(p, p)).replace("\\", "/"), "sha256": sha256(p), "role": role}
                           for role, p in (("request", Path(request)), ("response", Path(response)))]
    emit["tool"] = {"name": "recordrows", "sha256": sha256(Path(__file__)),
                    "scopeSha256": sha256(SCOPE_FILE) if SCOPE_FILE.exists() else None,
                    "schemaSha256": sha256(REF_DIR / "records.jsonl")}
    emit["ignored"] = parsed["ignored"]
    return emit


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--request", required=True)
    ap.add_argument("--response", required=True)
    ap.add_argument("--configuration", required=True, help="a JSON file holding {plugins: [{name, path}, ...]} in order")
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    sys.path.insert(0, str(HERE.parent / "scripts"))
    from runlog import Run  # noqa: E402
    with Run("recordrows", stage_source="counted") as r:
        sc = load_scope()
        probs = sc.problems()
        if probs:
            msg = "the field scope names fields the schema does not have: " + "; ".join(probs)
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg)
            return 2
        cfg = json.loads(Path(a.configuration).read_text(encoding="utf-8"))
        emit = derive(Path(a.request), Path(a.response), cfg, sc)
        Path(a.out).write_text(json.dumps(emit, indent=1), encoding="utf-8")
        r.feature("rows", len(emit["Conflicts"]), "rows")
        print(json.dumps({"rows": len(emit["Conflicts"]), "out": a.out}))
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
