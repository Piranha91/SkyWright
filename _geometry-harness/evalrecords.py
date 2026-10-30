"""evalrecords - the record capture for an evaluation's mcp-only checks, one reader session per arm.

    python _geometry-harness/evalrecords.py capture --run RUN --views DIR --reader-cmd CMD --out DIR
           --profile-dir PROFILE --data DIR --mods DIR [--cells FILE] [--arm ARM ...] [--reader-data DIR]

What this is for. Five registry checks are `mcp-only`: their capability is the houseCARL record and check tools,
which evalrun cannot call. This module calls them for each planned arm through the built houseCARL server, in its
explicit-paths mode (the arm's own plugins.txt, loadorder.txt and modlist.txt, and the base profile's INIs, so the
base archives are read), and writes one schema-1 import document per (check, arm) for `evalrun import-results`.
Every session's `initialize` names `evalrun:<planId>:<armId>`, so each import is `fresh` for its arm.

The selection, per arm (the recipe accepted in round 2's record method assessment):
  A  every record the arm's evaluated plugins (its plugins minus the dependency baseline) touch;
  C  the order's contested placed records, navmeshes and landscape inside the comparison cells H;
  U  = A union C. A record of U is contested when its override depth is above one; a row without a depth is
     unjudged, never uncontested.

The checks:
  loadability.dangling-references   housecarl_check errors family over the arm's whole order: every dangling link.
  records.contested-fields          lost edits on contested records of U that are not cells or registration records:
  records.cell-attributes           the same on CELL records;
  records.registration-and-dialogue the same on registration and dialogue records, plus the dialogue and script
                                    findings housecarl_check reports for the evaluated plugins' quests and topics.
                                    A lost edit is a field an evaluated plugin authored (its value differs from the
                                    provider beneath it) whose winning value differs from its own (recordrows' rule),
                                    at the top-level field, file, group and runtime-merged bookkeeping excluded by the
                                    world-edit-compatibility skill's record-field-scope.json.
  loadability.missing-assets        every Data-relative asset path the evaluated plugins' winning records name,
                                    resolved through the arm's file view; an absent one is a finding.

What it refuses. An empty enumeration where an evaluated plugin loads, an export shorter than its selection, a
reader error, or an unparsable answer stops the arm with one sentence; nothing is imported for it. A count is only
ever what a completed read returned. Reads the server marks incomplete are unjudged with their reason.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT / "scripts"))
sys.path.insert(0, str(HERE))
from runlog import Run  # noqa: E402

SCOPE_FILE = ROOT / ".claude/skills/world-edit-compatibility/references/record-field-scope.json"
LOCAL_TYPES = ["REFR", "ACHR", "NAVM", "LAND"]
REGISTRATION_TYPES = {"Quest", "DialogTopic", "DialogResponses", "Scene", "FormList", "Location",
                      "StoryManagerQuestNode", "StoryManagerBranchNode", "StoryManagerEventNode", "Package",
                      "DialogBranch", "DialogView"}
CHILD_GROUPS = {"Persistent", "Temporary", "NavigationMeshes", "Landscape", "SubCells", "TopCell", "Responses",
                "LargeReferences"}
ASSET_RE = re.compile(r"[A-Za-z0-9_ .\-\\/'()&!,+]+\.(?:nif|dds|hkx|wav|xwm|fuz|lip|tri|egm|bto|btr|btt|swf|pex|seq)$",
                      re.I)
ASSET_ROOTS = {"nif": "meshes", "tri": "meshes", "egm": "meshes", "bto": "meshes", "btr": "meshes", "btt": "meshes",
               "hkx": "meshes", "dds": "textures", "wav": "sound", "xwm": "sound", "fuz": "sound", "lip": "sound",
               "swf": "interface", "pex": "scripts", "seq": "seq"}
BIG = 1_000_000
MAX_CHARS = 200_000_000


class Refusal(Exception):
    """One sentence: what went wrong and what to try."""


# ---------------------------------------------------------------- selection rules (pure)

def local_union(partner_rows, contested_rows, cells, types) -> set:
    """(P intersect T intersect H) union C: the partners' local records in the comparison cells, plus the order's
    contested local records there."""
    tset = set(types)
    keep = {r["formid"] for r in partner_rows if r.get("type") in tset and r.get("parentCell") in cells}
    return keep | {r["formid"] for r in contested_rows}


def touched(arm_plugins, owners, rows) -> set:
    """The records the arm's owners touch; refuses an empty answer while one of them loads."""
    loaded = [p for p in owners if p in set(arm_plugins)]
    if loaded and not rows:
        raise Refusal(f"the touched-record enumeration came back empty while {', '.join(loaded)} loads; "
                      f"the reader did not read the plugin, so nothing is certified.")
    return {r["formid"] for r in rows}


def classify_depth(rows):
    contested, uncontested, unjudged = set(), set(), set()
    for r in rows:
        d = r.get("override_depth")
        if not isinstance(d, int):
            unjudged.add(r["formid"])
        elif d > 1:
            contested.add(r["formid"])
        else:
            uncontested.add(r["formid"])
    return contested, uncontested, unjudged


def population_members(contested, trees, by_id, evaluated) -> dict:
    """How many members each record check's population has on this arm: the contested records an evaluated plugin
    touches (the fields it authored are what is judged). An arm that loads no evaluated plugin has none, so its import
    states Members 0 rather than a judged-nothing extraction (evalrun's known-empty rule decides the rest)."""
    ev = set(evaluated)
    out = {"records.contested-fields": 0, "records.cell-attributes": 0, "records.registration-and-dialogue": 0}
    for fid in contested:
        t = trees.get(fid)
        if not ev or (t is not None and not ev & set(t.get("touchers") or [])):
            continue  # a record without a tree stays a member: it is reported unjudgeable, not dropped
        t = t or {}
        rtype = (t or by_id.get(fid) or {}).get("type")
        key = ("records.cell-attributes" if rtype == "Cell" else
               "records.registration-and-dialogue" if rtype in REGISTRATION_TYPES else "records.contested-fields")
        out[key] += 1
    return out


def complete_rows(export) -> list:
    rows = export.get("rows") or []
    want = export.get("requested")
    if not isinstance(want, int) or len(rows) < want:
        raise Refusal(f"the export returned {len(rows)} of {want} selected rows; it was cut short, so it is not used.")
    return rows


# ---------------------------------------------------------------- delta lines

def parse_delta(line: str, plugins: list[str]):
    """One delta line of the record reader -> (field, own value, other plugin, other value, kind).
    kind: 'value', 'absent' (the other side lacks the field) or 'list' (compared by content, not itemised)."""
    m = re.match(r"^([A-Za-z0-9_.\[\]]+): ABSENT here \((.+?) has (.*)\)$", line)
    if m:
        other = next((p for p in plugins if m.group(2) == p), m.group(2))
        return m.group(1), None, other, m.group(3), "own-absent"
    m = re.match(r"^([A-Za-z0-9_.\[\]]+): (.*) vs (.+?) (\S+) item\(s\)", line)
    if m:
        other = next((p for p in plugins if m.group(3).startswith(p)), m.group(3))
        return m.group(1), m.group(2), other, m.group(4), "list"
    eq = line.find("=")
    if eq < 0:
        return None
    field = line[:eq]
    rest = line[eq + 1:]
    for p in sorted(plugins, key=len, reverse=True):
        for marker, kind in ((f" ({p} has no ", "absent"), (f" ({p} has {field} ABSENT", "absent"), (f" ({p} ", "value")):
            i = rest.rfind(marker)
            if i >= 0 and rest.endswith(")"):
                own = rest[:i]
                other = None if kind == "absent" else rest[i + len(marker):-1]
                return field, own, p, other, kind
    return None


_NUM = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


def same_value(a, b) -> bool:
    """recordrows' number rule: numbers at four decimals, -0 equal to 0; other text exactly."""
    if a is None or b is None:
        return a is b
    na, nb = _NUM.findall(str(a)), _NUM.findall(str(b))
    if na and nb and len(na) == len(nb) and _NUM.sub("#", str(a)) == _NUM.sub("#", str(b)):
        return all(round(float(x), 4) == round(float(y), 4) or abs(float(x) - float(y)) < 1e-4 for x, y in zip(na, nb))
    return str(a) == str(b)


class FieldScope:
    """The fields the comparator does not compare, from the skill's record-field-scope.json."""

    def __init__(self, path=SCOPE_FILE):
        d = json.loads(Path(path).read_text(encoding="utf-8"))
        self.file = set(d["fileBookkeeping"]) | set(d["structBookkeeping"])
        self.group = {k: set(v) for k, v in d["groupBookkeeping"].items()}
        self.merged = {k: set(v) for k, v in d["runtimeMerged"].items()}
        self.generated = {k: set(v) for k, v in d["generatedBookkeeping"].items() if k != "about"}
        self.merged_records = set(d["runtimeMergedRecords"])
        self.masks = d.get("integerMasks", {})
        self.enum_names = d.get("integerEnumNames", {})

    def excluded(self, rtype: str, field: str) -> str | None:
        top = field.split(".")[0].split("[")[0]
        leaf = field.split(".")[-1]
        if rtype in self.merged_records:
            return "runtime-merged record"
        if top in self.file or leaf in self.file or field in self.file:
            return "file bookkeeping"
        if top in self.group.get(rtype, set()) or field in self.group.get(rtype, set()):
            return "group bookkeeping"
        if top in self.merged.get(rtype, set()):
            return "runtime-merged field"
        if top in self.generated.get(rtype, set()):
            return "generated bookkeeping"
        if top in CHILD_GROUPS:
            return "child records"
        return None

    def masked_equal(self, rtype, field, a, b) -> bool:
        mask = (self.masks.get(rtype) or {}).get(field)
        if mask is None:
            return False
        names = (self.enum_names.get(rtype) or {}).get(field, {})

        def num(v):
            v = str(v).strip()
            return names[v] if v in names else int(float(v))
        try:
            return (num(a) & mask) == (num(b) & mask)
        except (ValueError, TypeError):
            return False


def lost_edits(rtype, formid, plugin, authored_lines, vs_winner_lines, winner, plugins, scope: FieldScope,
               defines: bool = False):
    """(rows, judged, unjudged reasons) for one evaluated non-winning plugin on one contested record. A plugin that
    DEFINES the record authored every field it holds (there is nothing beneath it): its fields are read from its
    differences with the winner, with no overridden value."""
    authored, against = {}, {}
    unjudged = {}
    if defines:
        authored_lines = []
        for line in vs_winner_lines:
            p = parse_delta(line, plugins)
            if p is not None and p[4] != "own-absent":
                authored[p[0]] = (p[0], p[1], None, None, "defined")
    for line in authored_lines:
        p = parse_delta(line, plugins)
        if p is None:
            unjudged["a delta line the comparator could not read"] = unjudged.get("a delta line the comparator could not read", 0) + 1
            continue
        if p[4] != "own-absent":
            authored[p[0]] = p
    for line in vs_winner_lines:
        p = parse_delta(line, plugins)
        if p is None:
            unjudged["a delta line the comparator could not read"] = unjudged.get("a delta line the comparator could not read", 0) + 1
            continue
        against[p[0]] = p
    rows, judged = {}, 0
    for field, a in authored.items():
        why = scope.excluded(rtype, field)
        if why:
            continue
        judged += 1
        w = against.get(field)
        if w is None:
            continue  # the winner keeps this plugin's value
        own, prev = a[1], a[3]
        if a[4] == "value" and same_value(own, prev):
            continue
        if w[4] == "value" and (same_value(w[1], w[3]) or scope.masked_equal(rtype, field, w[1], w[3])):
            continue
        if a[4] == "value" and scope.masked_equal(rtype, field, own, prev):
            continue
        top = field.split(".")[0].split("[")[0]
        row = rows.setdefault(top, {"Kind": "LOST-EDIT", "A": plugin, "B": winner, "Field": top,
                                    "Summary": f"{rtype} {formid}: {top} - {plugin}'s value lost to {winner}'s",
                                    "Necessary": [{"FormKey": formid, "Label": None, "Values": {}, "Paths": []}]})
        n = row["Necessary"][0]
        n["Paths"].append(field)
        n.setdefault("_parts", []).append((field, own, w[3], prev))
    for row in rows.values():
        # the record-lane row shape (recordrows, the report compiler): Values {lost, winner, overridden}, a field's
        # leaves joined "Leaf=value; Leaf=value" (a placement reads "Position=x,y,z; Rotation=x,y,z")
        n = row["Necessary"][0]
        parts = n.pop("_parts")
        top = row["Field"]

        def joined(i):
            if len(parts) == 1 and parts[0][0] == top:
                return parts[0][i]
            return "; ".join(f"{p[0][len(top) + 1:] if p[0].startswith(top + '.') else p[0]}={p[i]}" for p in parts)
        n["Values"] = {"lost": joined(1), "winner": joined(2), "overridden": joined(3)}
    return list(rows.values()), judged, unjudged


# ---------------------------------------------------------------- the reader session

class Session:
    """One houseCARL server over stdio for one arm, every message kept for the import's transcripts."""

    def __init__(self, cmd: str, env: dict, token: str):
        argv = [t[1:-1] if len(t) > 1 and t[0] == t[-1] == '"' else t for t in shlex.split(cmd, posix=os.name != "nt")]
        self.p = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  text=True, encoding="utf-8", env={**os.environ, **env})
        self.req, self.resp, self.n = [], [], 0
        m = self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                     "clientInfo": {"name": token, "version": "evalrecords 1"}})
        if "error" in m:
            raise Refusal(f"the record reader refused to start: {m['error']}")
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
                raise Refusal(f"the record reader exited before answering request {self.n} ({method}).")
            m = json.loads(line)
            self.resp.append(m)
            if m.get("id") == self.n:
                return m

    def tool(self, name, args, *, want_json=True):
        m = self.call("tools/call", {"name": name, "arguments": args})
        if "error" in m:
            raise Refusal(f"{name} returned a protocol error: {m['error']}")
        res = m.get("result") or {}
        text = "".join(c.get("text", "") for c in res.get("content", []))
        if res.get("isError") or text.startswith("error:"):
            raise Refusal(f"{name} refused: {text[:300]}")
        if not want_json:
            return text
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            raise Refusal(f"{name} did not answer in JSON: {text[:200]}")

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=60)
        except (OSError, subprocess.TimeoutExpired):
            self.p.kill()

    def write(self, folder: Path):
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "request.jsonl").write_text("".join(json.dumps(m) + "\n" for m in self.req), encoding="utf-8")
        (folder / "response.jsonl").write_text("".join(json.dumps(m) + "\n" for m in self.resp), encoding="utf-8")


# ---------------------------------------------------------------- one arm

def sha256_file(p) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def write_view(folder: Path, arm: dict, ini_dir: Path | None):
    folder.mkdir(parents=True, exist_ok=True)
    plugins = arm["plugins"]
    (folder / "plugins.txt").write_text("".join("*" + p + "\n" for p in plugins), encoding="utf-8")
    (folder / "loadorder.txt").write_text("".join(p + "\n" for p in plugins), encoding="utf-8")
    (folder / "modlist.txt").write_text("".join("+" + m + "\n" for m in arm["mods"]), encoding="utf-8")
    if ini_dir:
        for f in ini_dir.glob("*.ini"):
            (folder / f.name).write_bytes(f.read_bytes())


def summaries(s: Session, args: dict) -> list:
    d = s.tool("housecarl_records", {**args, "project": {"form": "summary"}, "format": "json", "limit": BIG,
                                     "max_chars": MAX_CHARS})
    rows = d.get("matches") or []
    total = d.get("total")
    total = int(total) if str(total).isdigit() else None
    return complete_rows({"rows": rows, "requested": total})


def batched(ids, n=400):
    ids = sorted(ids)
    for i in range(0, len(ids), n):
        yield ids[i:i + n]


def capture_arm(plan, arm, baseline, cells, reader_cmd, env, out: Path, scope: FieldScope, log):
    token = f"evalrun:{plan['planId']}:{arm['armId']}"
    evaluated = [p for p in arm["plugins"] if p not in set(baseline)]
    s = Session(reader_cmd, env, token)
    result = {}
    try:
        # dangling references over the whole order
        d = s.tool("housecarl_check", {"findings": ["dangling", "missing_masters"], "format": "json", "limit": BIG,
                                       "max_chars": MAX_CHARS})
        fam = (d.get("families") or {}).get("errors") or {}
        acc = fam.get("accounting") or {}
        if acc.get("dangling_missing"):
            raise Refusal(f"the errors listing omitted {acc['dangling_missing']} dangling references; raise its limit.")
        drows, unscannable = [], 0
        for sec in fam.get("plugins") or []:
            unscannable += int(sec.get("unscannable_records") or 0)
            for x in sec.get("dangling") or []:
                drows.append({"Kind": "DANGLING-REFERENCE", "Summary": f"{x['source']} -> {x['target']} at {x['path']}",
                              "Necessary": [{"FormKey": x["source"], "Native": x}], "NativePluginSection": sec["plugin"]})
            for m in sec.get("missing_masters") or []:
                drows.append({"Kind": "MISSING-MASTER", "Summary": f"{sec['plugin']} declares missing master {m}",
                              "Necessary": [], "NativePluginSection": sec["plugin"]})
        if len([r for r in drows if r["Kind"] == "DANGLING-REFERENCE"]) != int(fam.get("dangling") or 0):
            raise Refusal("the errors listing's dangling rows do not add up to its own total.")
        cov = {"Population": "winning-order errors sweep (dangling references, missing masters)",
               "Unit": "plugins swept", "Judged": int(fam.get("scanned_plugins") or 0),
               "Unjudgeable": {"unscannable records": unscannable} if unscannable else {}, "SetAside": {}}
        result["loadability.dangling-references"] = (drows, [cov])
        log(f"{arm['armId']}: dangling {len(drows)}")

        # the selection U
        a_rows = summaries(s, {"plugins": {"names": evaluated}}) if evaluated else []
        touched(arm["plugins"], evaluated, a_rows)
        c_rows = []
        if cells:
            where = ["*parent.FormKey in [" + ", ".join(sorted(cells)) + "]"]
            c_rows = summaries(s, {"types": LOCAL_TYPES, "conflicts_only": True, "where": where})
        by_id = {r["formid"]: r for r in a_rows + c_rows}
        contested, uncontested, unjudged_ids = classify_depth(list(by_id.values()))
        log(f"{arm['armId']}: U {len(by_id)} (A {len(a_rows)}, C {len(c_rows)}); contested {len(contested)}")

        # trees of the contested records: who touches, who wins, what each provider differs from the winner by
        trees = {}
        for batch in batched(contested):
            t = s.tool("housecarl_records", {"formids": batch, "project": {"form": "tree"}, "format": "json",
                                             "limit": BIG, "max_chars": MAX_CHARS})
            for row in t.get("rows") or []:
                trees[row["formid"]] = row
        missing_trees = contested - set(trees)
        # each evaluated plugin's own changes against the provider beneath it, where it does not win
        lost_rows = {"records.contested-fields": [], "records.cell-attributes": [], "records.registration-and-dialogue": []}
        judged = {k: 0 for k in lost_rows}
        unj = {k: {} for k in lost_rows}
        for p in evaluated:
            # every contested record this plugin touches: where it wins its authored fields are judged and kept,
            # where it does not they are judged against the winner
            mine = [fid for fid, t in trees.items() if p in (t.get("touchers") or [])]
            authored = {}
            for batch in batched(mine):
                dd = s.tool("housecarl_records", {"formids": batch, "source": p, "versus": "previous_provider",
                                                  "project": {"form": "delta"}, "format": "json", "limit": BIG,
                                                  "max_chars": MAX_CHARS})
                for row in dd.get("rows") or []:
                    authored[row["formid"]] = row
            for fid in mine:
                t = trees[fid]
                rtype = t.get("type")
                key = ("records.cell-attributes" if rtype == "Cell" else
                       "records.registration-and-dialogue" if rtype in REGISTRATION_TYPES else "records.contested-fields")
                node = next((n for n in t.get("nodes") or [] if n.get("plugin") == p), None)
                winner = next((n.get("plugin") for n in t.get("nodes") or [] if n.get("is_winner")), None)
                arow = authored.get(fid)
                if winner == p and arow is not None and arow.get("complete", True):
                    kept = [x for x in (parse_delta(l, arm["plugins"]) for l in arow.get("deltas") or []) if x]
                    judged[key] += sum(1 for x in kept if not scope.excluded(rtype, x[0]))
                    continue
                defines = arow is not None and "DEFINES" in str(arow.get("error") or "")
                if defines and node is not None and node.get("complete", True):
                    rows, n_judged, n_unj = lost_edits(rtype, fid, p, [], node.get("deltas") or [], winner,
                                                       arm["plugins"], scope, defines=True)
                    lost_rows[key] += rows
                    judged[key] += n_judged
                    for k, v in n_unj.items():
                        unj[key][k] = unj[key].get(k, 0) + v
                    continue
                if node is None or arow is None or not node.get("complete", True) or not arow.get("complete", True):
                    unj[key]["a provider read the reader marked incomplete or did not return"] = \
                        unj[key].get("a provider read the reader marked incomplete or did not return", 0) + 1
                    continue
                rows, n_judged, n_unj = lost_edits(rtype, fid, p, arow.get("deltas") or [], node.get("deltas") or [],
                                                   winner, arm["plugins"], scope)
                lost_rows[key] += rows
                judged[key] += n_judged
                for k, v in n_unj.items():
                    unj[key][k] = unj[key].get(k, 0) + v
        for key in lost_rows:
            if missing_trees:
                unj[key]["a contested record whose tree the reader did not return"] = len(missing_trees)
            if unjudged_ids:
                unj[key]["a selected record without an override depth"] = len(unjudged_ids)
        members = population_members(contested, trees, by_id, evaluated)

        # registration and dialogue: the native dialogue and script findings for the evaluated plugins
        native_rows, native_judged, native_unj = [], 0, {}
        if evaluated:
            seeds = [r["formid"] for r in a_rows if r.get("type") in ("Quest", "DialogTopic")
                     and r.get("winner") in evaluated]
            args = {"findings": ["scripts"] + (["dialogue"] if seeds else []), "plugins": evaluated, "format": "json",
                    "limit": BIG, "max_chars": MAX_CHARS}
            if seeds:
                args["seeds"] = seeds
            d = s.tool("housecarl_check", args)
            fams = d.get("families") or {}
            sc = fams.get("scripts") or {}
            native_judged += int(sc.get("records_with_scripts") or 0)
            for rec in sc.get("records") or []:
                for u in rec.get("unbound") or []:
                    native_rows.append({"Kind": "UNBOUND-SCRIPT-PROPERTY", "A": rec["plugin"], "B": rec["plugin"],
                                        "Summary": f"{rec['type']} {rec['formid']}: unbound script property",
                                        "Necessary": [{"FormKey": rec["formid"], "Native": u}]})
            if sc.get("unverifiable"):
                native_unj["a script attachment whose .pex chain could not be read"] = int(sc["unverifiable"])
            dl = fams.get("dialogue")
            if seeds:
                if not isinstance(dl, dict):
                    raise Refusal("the dialogue family did not answer for the seeded quests and topics.")
                native_judged += int(dl.get("seeds_validated") or 0)
                if dl.get("seeds_unreachable_total"):
                    native_unj["a seeded quest or topic the dialogue check could not reach"] = int(dl["seeds_unreachable_total"])
                listed = 0
                for seed in dl.get("seeds") or []:
                    for item in seed.get("input_issues") or []:
                        listed += 1
                        native_rows.append({"Kind": "DIALOGUE-FINDING", "Summary": f"{seed['seed']}: {item}",
                                            "Necessary": [{"FormKey": seed["seed"], "Native": item}]})
                    for topic in seed.get("topics") or []:
                        for key in ("issues", "silent_lines"):
                            for item in topic.get(key) or []:
                                listed += 1
                                native_rows.append({"Kind": "DIALOGUE-FINDING",
                                                    "Summary": f"{topic['topic']}: {key}: {json.dumps(item)[:200]}",
                                                    "Necessary": [{"FormKey": topic["topic"], "Native": item}]})
                found = int(dl.get("findings_found") or 0)
                if found != listed:
                    native_unj["dialogue findings the listing did not itemise"] = abs(found - listed)
        reg = "records.registration-and-dialogue"
        result["records.contested-fields"] = (lost_rows["records.contested-fields"], [
            {"Population": "fields of selected contested records an evaluated plugin authored",
             "Unit": "authored top-level and leaf fields compared", "Judged": judged["records.contested-fields"],
             "Unjudgeable": unj["records.contested-fields"], "SetAside": {}, "Members": members["records.contested-fields"]}])
        result["records.cell-attributes"] = (lost_rows["records.cell-attributes"], [
            {"Population": "fields of selected contested CELL records an evaluated plugin authored",
             "Unit": "authored fields compared", "Judged": judged["records.cell-attributes"],
             "Unjudgeable": unj["records.cell-attributes"], "SetAside": {}, "Members": members["records.cell-attributes"]}])
        result[reg] = (lost_rows[reg] + native_rows, [
            {"Population": "fields of selected contested registration and dialogue records an evaluated plugin authored",
             "Unit": "authored fields compared", "Judged": judged[reg], "Unjudgeable": unj[reg], "SetAside": {},
             "Members": members[reg]},
            {"Population": "the evaluated plugins' script attachments and seeded quests and topics (native check)",
             "Unit": "records with scripts plus seeded quests and topics", "Judged": native_judged,
             "Unjudgeable": native_unj, "SetAside": {}, "Members": native_judged}])

        # missing assets: every asset path the evaluated plugins' winning records name
        paths = set()
        winners = [r["formid"] for r in a_rows if r.get("winner") in evaluated]
        for batch in batched(winners, 200):
            d = s.tool("housecarl_records", {"formids": batch, "project": {"form": "everything", "depth": 6},
                                             "format": "json", "limit": BIG, "max_chars": MAX_CHARS})
            if len(d.get("records") or []) < len(batch):
                raise Refusal(f"the record body read returned {len(d.get('records') or [])} of {len(batch)} records.")
            for rec in d.get("records") or []:
                for f in rec.get("fields") or []:
                    v = f.get("value")
                    if isinstance(v, str) and ASSET_RE.fullmatch(v.strip()):
                        paths.add(asset_path(v.strip(), f.get("path", "")))
        arows, absent, unread = [], 0, {}
        if paths:
            ad = s.tool("housecarl_asset_status", {"asset_paths": sorted(paths), "format": "json", "limit": 0})
            rows = ad.get("results") or []
            if len(rows) != len(paths) or ad.get("read_incomplete") or ad.get("bsa_failures") or ad.get("truncated"):
                raise Refusal(f"the asset read returned {len(rows)} of {len(paths)} paths, or read archives incompletely.")
            for r in rows:
                if r.get("error"):
                    unread[str(r["error"])[:80]] = unread.get(str(r["error"])[:80], 0) + 1
                elif not r.get("exists") and (r.get("absent_may_be_incomplete_read_failure")
                                              or r.get("absent_may_be_incomplete_undiscovered_archives")):
                    unread["an absent path the reader says may be an incomplete read"] =                         unread.get("an absent path the reader says may be an incomplete read", 0) + 1
                elif not r.get("exists"):
                    absent += 1
                    arows.append({"Kind": "MISSING-ASSET", "Summary": f"{r.get('path')} has no provider in this view",
                                  "Necessary": [], "Native": r})
        result["loadability.missing-assets"] = (arows, [
            {"Population": "Data-relative asset paths named by the evaluated plugins' winning records",
             "Unit": "unique asset paths", "Judged": len(paths) - sum(unread.values()), "Unjudgeable": unread,
             "SetAside": {}, "Members": len(paths)}])
    finally:
        s.close()
        s.write(out)
    return result


def asset_path(value: str, field_path: str) -> str:
    v = value.replace("\\", "/").lstrip("/")
    if v.lower().startswith("data/"):
        v = v[5:]
    ext = v.rsplit(".", 1)[-1].lower()
    root = ASSET_ROOTS.get(ext)
    if root and not v.lower().startswith(root + "/"):
        v = f"{root}/{v}"
    return v.lower()


# ---------------------------------------------------------------- import documents

def configuration_for(arm, evalrun):
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in req["providers"]], "overlays": req.get("overlays") or [],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = evalrun.configuration_fingerprint(req["plugins"], req["providers"], req.get("overlays") or [],
                                                           req["population"], "baseline", schema=1)
    return cfg


def write_imports(plan, arm, result, session_dir: Path, out: Path, captured: str, evalrun) -> list[Path]:
    docs = []
    open_checks = {c["checkId"] for c in plan["checks"] if c["adapter"]["kind"] == "mcp-only"
                   and any(x.get("armId") == arm["armId"] and x.get("status") == "mcp-only" for x in c.get("arms", []))}
    for cid, (rows, coverage) in result.items():
        if cid not in open_checks:
            continue
        folder = out / f"{cid}__{arm['armId']}"
        folder.mkdir(parents=True, exist_ok=True)
        rows_doc = {"Population": f"{cid}: evalrecords selection", "State": "baseline", "Coverage": coverage,
                    "Conflicts": rows}
        (folder / "rows.json").write_text(json.dumps(rows_doc, indent=1), encoding="utf-8")
        outputs = [{"role": "request", "path": str(session_dir / "request.jsonl"),
                    "sha256": sha256_file(session_dir / "request.jsonl")},
                   {"role": "response", "path": str(session_dir / "response.jsonl"),
                    "sha256": sha256_file(session_dir / "response.jsonl")},
                   {"role": "rows", "path": "rows.json", "sha256": sha256_file(folder / "rows.json")}]
        doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"], "state": "baseline",
               "capturedUtc": captured, "origin": "fresh",
               "tool": {"name": "evalrecords: houseCARL record and check reads, one session per arm", "version": "1"},
               "configuration": configuration_for(arm, evalrun),
               "result": {"detector": "finding" if rows else "no-finding", "findings": len(rows), "coverage": coverage},
               "outputs": outputs}
        (folder / "import.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")
        docs.append(folder / "import.json")
    return docs


def cmd_capture(a) -> int:
    import evalrun
    run = Path(a.run)
    plan = json.loads((run / "plan.json").read_text(encoding="utf-8"))
    baseline = json.loads(Path(a.baseline_from).read_text(encoding="utf-8"))["baseline"]
    cells = json.loads(Path(a.cells).read_text(encoding="utf-8")) if a.cells else []
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    scope = FieldScope()
    summary = {}
    with Run("evalrecords", mode="capture", stage_source="counted") as r:
        arms = [x for x in plan["arms"] if not a.arm or x["armId"] in a.arm]
        r.stage("arms", "arm captures", len(arms), "arms")
        for arm in arms:
            view = out / "views" / arm["armId"]
            write_view(view, arm, Path(a.profile_dir) if a.profile_dir else None)
            env = {"HouseCarl__DataDir": a.data, "HouseCarl__ModsDir": a.mods, "HouseCarl__ProfileDir": str(view)}
            if a.reader_data:
                env["HOUSECARL_DATA_DIR"] = a.reader_data
            captured = datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")
            session_dir = out / "sessions" / arm["armId"]
            try:
                result = capture_arm(plan, arm, baseline, cells, a.reader_cmd, env, session_dir, scope,
                                     lambda m: print(m, flush=True))
            except Refusal as ex:
                summary[arm["armId"]] = {"refused": str(ex)}
                print(f"{arm['armId']}: REFUSED: {ex}", flush=True)
                r.advance()
                continue
            docs = write_imports(plan, arm, result, session_dir, out / "imports", captured, evalrun)
            summary[arm["armId"]] = {"imports": [str(d) for d in docs],
                                     "findings": {k: len(v[0]) for k, v in result.items()},
                                     "judged": {k: sum(c["Judged"] for c in v[1]) for k, v in result.items()}}
            r.advance()
    (out / "capture-summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    refused = [k for k, v in summary.items() if "refused" in v]
    print(json.dumps({"arms": len(summary), "refused": refused}))
    return 2 if refused else 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="evalrecords", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("capture", help="capture every planned arm's mcp-only checks and write import documents")
    p.add_argument("--run", required=True, help="the evalrun run folder (plan.json)")
    p.add_argument("--baseline-from", required=True, help="evalpipeline's preparation.json (its dependency baseline)")
    p.add_argument("--reader-cmd", required=True, help="the houseCARL server command line")
    p.add_argument("--data", required=True, help="the game's Data folder")
    p.add_argument("--mods", required=True, help="the lab instance's mods folder")
    p.add_argument("--profile-dir", help="the base profile, whose INIs every view copies")
    p.add_argument("--cells", help="a JSON list of comparison cell FormKeys")
    p.add_argument("--reader-data", help="a private HOUSECARL_DATA_DIR for the reader")
    p.add_argument("--arm", action="append")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_capture)
    a = ap.parse_args(argv)
    try:
        return a.fn(a)
    except Refusal as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
