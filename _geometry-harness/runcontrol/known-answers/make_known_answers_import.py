"""Write import.json: the known answers for `runcontrol import-results`, computed by evalrun.py, and for the records
stage of a lab entry, computed by evalpipeline.py's stage_records with its children stood in for.

Every expected value comes from the real module: datetime.fromisoformat as the import reads a timestamp, capture_token,
initialize_requests and client_name, rows_output_errors, empty_population, origin_refusals, and _cmd_import_results over
synthetic run folders (each refusal sentence, each accepted import's receipt, ledger entry, journal line and imports
folder). The controller's own identity is stood in for and the clock is a counter. Run it again only when one of those
rules changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_import.py

Nothing outside a temporary folder is read or written except the harness sources and the output.
"""
from __future__ import annotations

import copy
import json
import os
import platform
import shutil
import sys
import tempfile
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run   # noqa: E402
import make_known_answers_finalize as F   # noqa: E402  (its plan, builder and masking helpers)
import make_known_answers_evaluate as V   # noqa: E402  (its plan with requests, and the emit a probe writes)
from make_known_answers_finalize import E   # noqa: E402
import evalpipeline as P   # noqa: E402

OUT = HERE / "import.json"
T0 = V.T0
CONTROLLER = {"controller": {"kind": "scripts", "digest": "scripts:" + "c" * 64}}
TOKEN = "evalrun:plan00000001:{arm}"


def receipt_text(path: Path, root: Path) -> str:
    """A receipt as text with what follows the folder's own path taken out: the import document holds absolute paths,
    so its hash and size are this folder's, as is the time of writing of each copied file."""
    doc = json.loads(path.read_text(encoding="utf-8"))
    for row in doc.get("outputs") or []:
        row["mtimeUtc"] = "<mtime>"
        if str(row.get("path")).endswith("/result.json"):
            row["sha256"], row["bytes"] = "<document sha256>", "<document bytes>"
    if isinstance(doc.get("imported"), dict):
        doc["imported"]["sourceSha256"] = "<document sha256>"
    return F.mask(json.dumps(doc, indent=1, ensure_ascii=False), root)


# ------------------------------------------------------------------------------------------ small tables

def iso_cases(_tmp) -> list:
    """datetime.fromisoformat over what an import's capturedUtc and a plan's createdUtc can hold, read as the import
    reads them (Z written as +00:00 first): the instant in microseconds and whether a zone was named, or null."""
    texts = ["2026-01-01T00:10:00Z", "2026-01-01T00:10:00+00:00", "2026-01-01T00:10:00", "2026-01-01", "20260101", "2026-01-01T00",
             "2026-01-01T0010", "2026-01-01T001000", "2026-01-01T00:10", "2026-01-01 00:10:00+01:00", "2026-01-01T00:10:00-08:00",
             "2026-01-01T00:10:00.5+00:00", "2026-01-01T00:10:00.123456789Z", "2026-01-01T00:10:00,25Z", "2026-01-01T00:10:00.12x456789Z",
             "2026-01-01T00:10:00+0130", "2026-01-01T00:10:00+01", "2026-01-01T00:10:00+01:30:15", "2026-01-01T00:10:00+01:30:15.5",
             "2026-01-01T00:10:00+1", "2026-01-01T00:10:00+013", "2026-01-01T00:10:00+24:00", "2026-01-01T00:10:00+23:59",
             "2026-01-01T00:10:00-00:00", "2026-01-01T24:00:00Z", "2026-01-01T23:60:00Z", "2026-01-01T23:59:60Z", "2026-13-01T00:00:00Z",
             "2026-02-29T00:00:00Z", "2024-02-29T00:00:00Z", "0000-01-01T00:00:00Z", "9999-12-31T23:59:59Z", "2026-W01-1T00:00:00Z",
             "2026-W01", "2026W01", "2026W011", "2026-W53-1", "2020-W53-7", "2026-W00-1", "2026-W01-8", "2026-W1-1", "2026-01-01X00:10:00Z",
             "2026-01-01T00:10:00 Z", "2026-01-01T00:10:00z", "2026-1-1T00:10:00Z", "26-01-01T00:10:00Z", "2026-01-01T0:10:00Z",
             "2026-01-01T00:10:0Z", "2026-01-01T00:1000Z", "2026-01-01T0010:00Z", "2026/01/01T00:10:00Z", "today", "", "None", "2026-01",
             "2026-01-0", "2026-01-01T", "2026-01-01T0", "2026-01-01TZ", "2026-01-01T00:10:00+", "2026-01-01T00:10:00.Z", "2026-01-01T00:10:00.",
             " 2026-01-01T00:10:00Z", "2026-01-01T00:10:00Z ", "2026-01-01T00:10:00+00:00:00.000000", "20260101T001000Z", "2026-01-01T00:10:00ZZ",
             "2026-0A-01T00:10:00Z", "2026-01-01T00:10:00+0A:00", "+026-01-01T00:10:00Z", "2026-01-01T-1:10:00Z", "2026001", "2026-001"]
    rows = []
    for text in texts:
        try:
            d = datetime.fromisoformat(text.replace("Z", "+00:00"))
        except ValueError:
            rows.append({"text": text, "want": None})
            continue
        offset = d.utcoffset()
        us = ((d.toordinal() * 24 + d.hour) * 60 + d.minute) * 60 + d.second
        us = us * 1_000_000 + d.microsecond
        if offset is not None:
            us -= (offset.days * 86400 + offset.seconds) * 1_000_000 + offset.microseconds
        rows.append({"text": text, "want": {"us": str(us), "aware": offset is not None}})
    return rows


def line(arm="baseline", name="same", **more) -> str:
    info = {"name": TOKEN.format(arm=arm) if name == "same" else name, "version": "1"}
    return json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"clientInfo": info}, **more})


CALL = json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "housecarl_records", "arguments": {"note": "evalrun:plan00000001:baseline"}}})


def request_files() -> list:
    """(name, bytes) of request files: what initialize_requests makes of each."""
    return [
        ("one initialize", (line() + "\n" + CALL + "\n").encode()),
        ("a byte-order mark and CRLF", b"\xef\xbb\xbf" + (line() + "\r\n" + CALL + "\r\n").encode()),
        ("blank lines and spaces", ("\n  \n" + line() + "\n\t\n\r\n").encode()),
        ("one JSON document, no line end", line().encode()),
        ("a batch", ("[" + line() + ", 5, " + line(name="other") + ", " + CALL + "]\n").encode()),
        ("two processes", (line() + "\n" + CALL + "\n" + line() + "\n").encode()),
        ("no initialize", (CALL + "\n").encode()),
        ("empty", b""),
        ("a method that is not exactly initialize", json.dumps({"method": "Initialize"}).encode() + b"\n" + json.dumps({"method": ["initialize"]}).encode()),
        ("a line that is not JSON", (line() + "\n{not json}\n").encode()),
        ("a line with text after the value", (line() + " x\n").encode()),
        ("a separator inside a string", json.dumps({"method": "initialize", "note": "a\u2028b\u0085c", "params": {"clientInfo": {"name": "n"}}},
                                                   ensure_ascii=False).encode("utf-8")),
        ("a no-break space before a value", ("\u00a0" + line()).encode("utf-8")),
        ("a line of only a no-break space", ("\u00a0\n" + line()).encode("utf-8")),
        ("no clientInfo", json.dumps({"method": "initialize", "params": {}}).encode()),
        ("params that are a list", json.dumps({"method": "initialize", "params": [1]}).encode()),
        ("clientInfo that is text", json.dumps({"method": "initialize", "params": {"clientInfo": "x"}}).encode()),
        ("a name that is a number", json.dumps({"method": "initialize", "params": {"clientInfo": {"name": 5}}}).encode()),
        ("NaN in a line", b'{"method": "initialize", "x": NaN}'),
        ("not UTF-8: a stray continuation byte", b'{"a": "\x80"}'),
        ("not UTF-8: a lead byte at the end", b'{"a": 1}\xc3'),
        ("not UTF-8: a lead byte then text", b'{"a": "\xc3x"}'),
        ("not UTF-8: an overlong form", b'ab\xc0\x80'),
        ("not UTF-8: a surrogate", b'abc\xed\xa0\x80'),
        ("not UTF-8: three bytes cut short", b'abc\xe2\x82'),
        ("not UTF-8: three bytes, the third wrong", b'abc\xe2\x82x'),
        ("not UTF-8: e0 with a low second byte", b'\xe0\x80\x80'),
        ("not UTF-8: four bytes cut short", b'ab\xf0\x9f\x98'),
        ("not UTF-8: four bytes, the fourth wrong", b'ab\xf0\x9f\x98x'),
        ("not UTF-8: f0 with a low second byte", b'ab\xf0\x80\x80\x80'),
        ("not UTF-8: f4 past the last code point", b'ab\xf4\x90\x80\x80'),
        ("not UTF-8: f5", b'ab\xf5\x80\x80\x80'),
        ("not UTF-8: after a byte-order mark", b'\xef\xbb\xbfab\xff'),
        ("four bytes that are a character", "a\U0001F600b".encode("utf-8") + b"\n" + line().encode()),
    ]


def request_cases(_tmp) -> dict:
    tokens = [{"plan": p, "arm": a, "want": E.capture_token(p, a)} for p, a in (("plan00000001", "baseline"), ("p", "candidate-01~2"), ("", ""))]
    rows = []
    for name, data in request_files():
        found, why = E.initialize_requests(data)
        rows.append({"name": name, "bytes": F.packb(data), "why": why,
                     "names": None if found is None else [E.client_name(m) for m in found]})
    return {"tokens": tokens, "files": rows}


def rows_cases(tmp: Path) -> list:
    arm = {"armId": "baseline", "requested": {"plugins": [{"name": "Skyrim.esm"}, {"name": "A.esp"}, {"name": None}, {}]}}
    docs = [("a conflict file of the arm's plugins", {"Conflicts": [{"A": "a.ESP", "B": "Skyrim.esm"}, {"A": "A.esp"}, "text", {"A": None, "B": ""}]}, arm),
            ("pairs naming other plugins", {"Conflicts": [{"A": "Zed.esp", "B": "A.esp"}, {"A": "b.esp", "B": "Zed.esp"}, {"A": 5, "B": "\u00c9.esp"}]}, arm),
            ("no Conflicts", {"conflicts": []}, arm), ("Conflicts that are an object", {"Conflicts": {}}, arm), ("a list", [1], arm),
            ("an arm with no request", {"Conflicts": [{"A": "A.esp"}]}, {"armId": "x"}), ("no arm", {"Conflicts": [{"A": "A.esp"}]}, None),
            ("an empty list", {"Conflicts": []}, None)]
    out = []
    for i, (name, doc, a) in enumerate(docs):
        p = F.write(tmp, f"rows/r{i}.rows.json", json.dumps(doc))
        out.append({"name": name, "file": p.name, "text": p.read_text(encoding="utf-8"), "arm": a, "want": E.rows_output_errors(p, a)})
    p = F.write(tmp, "rows/bad.json", "{not json")
    out.append({"name": "not JSON", "file": p.name, "text": "{not json", "arm": arm, "want": E.rows_output_errors(p, arm)})
    out.append({"name": "no file", "file": "none.json", "text": None, "arm": arm, "want": E.rows_output_errors(tmp / "rows" / "none.json", arm)})
    return out


def empty_cases(_tmp) -> list:
    loads = {"armId": "a", "requested": {"plugins": [{"name": "target.ESP"}, {"name": "Other.esp"}]}}
    free = {"armId": "b", "requested": {"plugins": [{"name": "Other.esp"}]}}
    bare = {"armId": "c", "plugins": ["Target.esp"]}
    two = {"targets": [{"name": "Target.esp"}, {"name": "It's.esp"}, "odd", {"name": None}, {"provider": "x"}]}
    rows = []
    for name, plan, arm, members, unjudgeable in (
            ("members stated 0, no target loaded", two, free, [0, 0], 0),
            ("one line states no count", two, free, [0, None], 0),
            ("no line at all", two, free, [], 0),
            ("a member was found", two, free, [0, 2], 0),
            ("something was unjudgeable", two, free, [0], 3),
            ("the arm loads a target", two, loads, [0], 0),
            ("the arm loads a target by its plugin list", two, bare, [0], 0),
            ("the plan names no target", {"targets": []}, free, [0], 0),
            ("the plan has no targets key", {}, free, [0], 0),
            ("targets that name nothing", {"targets": ["x", {"name": ""}]}, free, [0], 0)):
        said, why = E.empty_population(plan, arm, members, unjudgeable)
        rows.append({"name": name, "plan": plan, "arm": arm, "members": members, "unjudgeable": unjudgeable, "sentence": said, "why": why})
    return rows


# ------------------------------------------------------------------------------------------ run folders

RECORDS = dict(F.check_row("r.records", "mcp-only"), evidence={"strength": "record-fact"})
NOT_APPLICABLE = F.check_row("r.na", "mcp-only", arms=("baseline",))
SKIPPED = F.check_row("r.skipped", "mcp-only", arms=("baseline",), applicabilityEvidence="")
PROBE = F.check_row("g.probe", "probe-emit", exe="contactprobe")
LIVE = F.check_row("r.live", "live-import")
ONE_ARM = F.check_row("r.one", "mcp-only", arms=("baseline",))
MASTERS = dict(F.check_row("l.masters", "derived"), status="held")   # a prerequisite whose ledger entries a case fixes
GATED = F.check_row("r.gated", "mcp-only", prerequisites=["l.masters"])
NO_KIND = dict(F.check_row("r.nokind", "mcp-only"), adapter=None)


def open_rows(*checks):
    """The checks with every mcp-only row left open for an import."""
    out = copy.deepcopy(list(checks))
    for c in out:
        if (c.get("adapter") or {}).get("kind") == "mcp-only":
            for a in c["arms"]:
                a["status"] = "mcp-only"
    return out


def base_checks():
    checks = open_rows(RECORDS, NOT_APPLICABLE, SKIPPED, PROBE, LIVE, ONE_ARM, NO_KIND)
    checks[1]["arms"][0]["status"] = "not-applicable(no-target-records)"
    checks[2]["arms"][0]["status"] = "skipped-user"
    return checks


def capture(root: Path, folder: str, arm="baseline", *, cid="r.records", rows=None, request=None, **over) -> tuple:
    """One capture folder with its import document: (files to write as (relative path, bytes), the document)."""
    request = request if request is not None else (line(arm) + "\n" + CALL + "\n").encode()
    response = b'{"jsonrpc": "2.0", "id": 1, "result": {}}\n'
    files = [(f"{folder}/request.jsonl", request), (f"{folder}/response.jsonl", response)]
    outputs = [{"role": "request", "path": str(root / folder / "request.jsonl"), "sha256": F.sha(request)},
               {"role": "response", "path": "response.jsonl", "sha256": F.sha(response).upper()}]
    if rows is not None:
        data = json.dumps(rows).encode()
        files.append((f"{folder}/found.rows.json", data))
        outputs.append({"role": "rows", "path": "found.rows.json", "sha256": F.sha(data)})
    doc = {"schemaVersion": 1, "planId": "plan00000001", "checkId": cid, "armId": arm, "capturedUtc": "2026-01-01T00:10:00Z",
           "tool": {"name": "housecarl-mcp", "version": "2.0"}, "origin": "fresh", "state": "baseline",
           "configuration": V.emit_doc(root, "reader", arm)["Configuration"],
           "result": {"detector": "no-finding", "findings": 0, "coverage": [{"Population": "records", "Judged": 5, "Unjudgeable": {}}]},
           "outputs": outputs}
    doc.update(over)
    return files, doc


class Folder:
    """One run folder and the imports tried on it, one after the other, under one counting clock."""

    def __init__(self, tmp: Path, cases: list, name: str, checks, *, settle=None, raw=None, preflight=None, **plan_over):
        self.root = tmp / f"i{len(cases)}"
        self.root.mkdir()
        self.plan = V.eval_plan(self.root, checks, **plan_over)
        with F.fake_clock():
            b = F.Builder(self.root, self.plan)
            if settle:
                settle(b, self.root)
        if raw:
            raw(self.root)
        self.preflight = preflight
        self.row = {"name": name, "tree": V.snapshot(self.root), "preflight": preflight, "steps": []}
        cases.append(self.row)
        self.clock = F.Clock()
        self.n = 0

    def step(self, name, files, doc, *, text=None, missing=False, runtime_words=False, before=None, live=False):
        """Writes the capture's files and its document (or `text` in its place; `missing`: no document), then imports."""
        self.n += 1
        root = self.root
        written = []
        for rel, data in files:
            p = F.write(root, rel, data)
            os.utime(p, (T0, T0))
            written.append({"path": rel, "bytes": F.packb(data)})
        doc_rel = (files[0][0].rsplit("/", 1)[0] if files else f"none{self.n}") + "/result.json"
        if not missing:
            body = text if text is not None else json.dumps(doc, indent=1)
            F.write(root, doc_rel, body)
            written.append({"path": doc_rel, "bytes": F.packb(body)} if isinstance(body, bytes) else {"path": doc_rel, "text": F.pack(F.mask(body, root))})
        edits = None
        if before:
            before(root)
            edits = V.snapshot(root)
        journal = root / "journal.jsonl"
        lines_before = len(journal.read_text(encoding="utf-8").splitlines()) if journal.is_file() else 0
        log = SimpleNamespace(run_id=f"import-run-{self.n}", feature=lambda *a, **k: None, artifact=lambda *a, **k: None,
                              advance=lambda *a, **k: None)
        real_clock, real_pre = E.utcnow, E.tool_preflight
        E.utcnow = self.clock
        E.tool_preflight = lambda run, plan, only=None: (self.preflight, {} if self.preflight else copy.deepcopy(CONTROLLER))
        try:
            with F.captured() as (out, err):
                rc = E._cmd_import_results(SimpleNamespace(run=str(root), file=str(root / doc_rel)), log)
        finally:
            E.utcnow, E.tool_preflight = real_clock, real_pre
        key = None
        if isinstance(doc, dict) and isinstance(doc.get("checkId"), str) and isinstance(doc.get("armId"), str):
            key = f"{doc['checkId']}__{doc['armId']}"
        receipt = root / "receipts" / f"{key}.json"
        led = json.loads((root / "ledger.json").read_text(encoding="utf-8")) if (root / "ledger.json").is_file() else None
        imports = []
        for p in sorted((root / "imports").rglob("*")) if (root / "imports").is_dir() else []:
            if p.is_file():
                imports.append({"path": p.relative_to(root).as_posix(), "sha256": None if p.name == "result.json" else F.sha(p.read_bytes()),
                                "linked": p.stat().st_nlink > 1})
        self.row["steps"].append({
            "name": name, "files": written, "doc": doc_rel, "edits": edits, "exit": rc, "stdout": F.mask(out.getvalue(), root),
            "stderr": F.mask(err.getvalue(), root), "runtimeWords": runtime_words, "live": live, "key": key,
            "receipt": F.pack(receipt_text(receipt, root)) if key and receipt.is_file() else None,
            "entry": F.mask(((led or {}).get("entries") or {}).get(key), root) if isinstance((led or {}).get("entries"), dict) else None,
            "journal": journal.read_text(encoding="utf-8").splitlines()[lines_before:] if journal.is_file() else [],
            "imports": imports})
        return rc

    def refused(self, name, folder, change=None, **kw):
        """A good capture with one thing changed, which the import must refuse."""
        capture_kw = {k: kw.pop(k) for k in ("arm", "cid", "rows", "request") if k in kw}
        files, doc = capture(self.root, folder, **capture_kw)
        files = list(files)
        if change:
            change(doc, files)
        rc = self.step(name, files, doc, **kw)
        assert rc == 2, f"{name}: Python accepted it (exit {rc}): {self.row['steps'][-1]['stderr']}"

    def accepted(self, name, folder, change=None, **kw):
        capture_kw = {k: kw.pop(k) for k in ("arm", "cid", "rows", "request") if k in kw}
        files, doc = capture(self.root, folder, **capture_kw)
        files = list(files)
        if change:
            change(doc, files)
        rc = self.step(name, files, doc, **kw)
        assert rc == 0, f"{name}: Python refused it: {self.row['steps'][-1]['stderr']}"


def setv(path: str, value):
    """A change that sets doc[a][b]... = value; `path` is dotted, a number is a list index."""
    def change(doc, _files):
        node = doc
        keys = [int(k) if k.isdigit() else k for k in path.split(".")]
        for k in keys[:-1]:
            node = node[k]
        node[keys[-1]] = value
    return change


def drop(path: str):
    def change(doc, _files):
        node = doc
        keys = [int(k) if k.isdigit() else k for k in path.split(".")]
        for k in keys[:-1]:
            node = node[k]
        del node[keys[-1]]
    return change


def both(*changes):
    def change(doc, files):
        for c in changes:
            c(doc, files)
    return change


def coverage(*lines):
    return setv("result.coverage", list(lines))


def refile(index: int, data: bytes, rehash=True):
    """Replaces the bytes of the capture's file at `index`, restating its hash unless told not to."""
    def change(doc, files):
        files[index] = (files[index][0], data)
        if rehash:
            doc["outputs"][index]["sha256"] = F.sha(data)
    return change


def import_cases(tmp: Path) -> list:
    cases: list = []

    # ---- before the document is read
    f = Folder(tmp, cases, "a folder that is not a run", base_checks(), raw=lambda r: ((r / "plan.json").unlink(), (r / "plan.sha256").unlink()))
    f.refused("no plan", "c1")
    f = Folder(tmp, cases, "a plan frozen before arm views", base_checks(), requestProviders="scope")
    f.refused("an outdated request meaning", "c1")
    f = Folder(tmp, cases, "a plan with no program identity", base_checks(), toolIdentity=None)
    f.refused("no identity contract", "c1")
    f = Folder(tmp, cases, "a controller that is not the plan's", base_checks(),
               preflight="tool-changed: the controller is not the build this plan recorded. Restore the recorded build, or make a new plan in a new directory.")
    f.refused("the controller changed", "c1")
    f = Folder(tmp, cases, "a ledger of another plan", base_checks(),
               raw=lambda r: F.write(r, "ledger.json", F.jtext({"schemaVersion": 2, "planId": "other", "runIds": [], "entries": {}})))
    f.refused("a foreign ledger", "c1")

    # ---- the validation table: one folder, nothing is written by a refusal
    f = Folder(tmp, cases, "the validation table", base_checks())
    f.refused("no document", "t01", missing=True, runtime_words=True)
    f.refused("a document that is not JSON", "t02", text="{not json", runtime_words=True)
    f.refused("a document that is not UTF-8", "t02b", text=b'{"a": "\xff"}', runtime_words=True)
    f.refused("a document that is a list", "t03", text="[1, 2]")
    f.refused("schema 2", "t04", setv("schemaVersion", 2))
    f.refused("schema as text", "t05", setv("schemaVersion", "1"))
    f.refused("no schema", "t06", drop("schemaVersion"))
    f.refused("another plan's document", "t07", setv("planId", "plan00000002"))
    f.refused("no plan id", "t08", drop("planId"))
    f.refused("a check the plan does not have", "t09", setv("checkId", "r.none"))
    f.refused("no check id", "t10", drop("checkId"))
    f.refused("a live driver's check", "t11", cid="r.live", live=True)
    f.refused("a check a probe measures", "t12", cid="g.probe")
    f.refused("a check with no adapter", "t13", cid="r.nokind")
    f.refused("an arm the plan does not have", "t14", setv("armId", "candidate-09"))
    f.refused("an arm the check does not plan", "t15", cid="r.one", arm="candidate-01")
    f.refused("no arm id", "t16", drop("armId"))
    f.refused("a row the plan answered not applicable", "t17", cid="r.na")
    f.refused("a row the plan answered otherwise, with no evidence sentence", "t18", cid="r.skipped")
    f.refused("no capture time", "t20", drop("capturedUtc"))
    f.refused("no tool", "t21", drop("tool"))
    f.refused("a tool with no name", "t22", setv("tool", {"version": "1"}))
    f.refused("a tool that is text", "t23", setv("tool", "housecarl"))
    f.refused("a capture time that is a number", "t24", setv("capturedUtc", 20260101))
    f.refused("a capture time with no zone", "t25", setv("capturedUtc", "2026-01-01T00:10:00"))
    f.refused("a capture time that is not a time", "t26", setv("capturedUtc", "yesterday"))
    f.refused("a capture time before the plan", "t27", setv("capturedUtc", "2025-12-31T23:59:59Z"))
    f.refused("a capture time before the plan in another zone", "t28", setv("capturedUtc", "2026-01-01T00:30:00+01:00"))
    f.refused("no configuration", "t30", drop("configuration"))
    f.refused("a configuration that is text", "t31", setv("configuration", "load-time"))
    f.refused("a configuration with no plugins", "t32", setv("configuration.Plugins", []))
    f.refused("no result", "t33", drop("result"))
    f.refused("a result that is a list", "t34", setv("result", [1]))
    f.refused("a detector word of a probe", "t35", setv("result.detector", "catalogue"))
    f.refused("findings below zero", "t36", setv("result.findings", -1))
    f.refused("findings that are true", "t37", setv("result.findings", True))
    f.refused("findings as text", "t38", setv("result.findings", "0"))
    f.refused("findings with a fraction", "t39", setv("result.findings", 1.0))
    f.refused("a finding with no findings", "t40", setv("result.detector", "finding"))
    f.refused("no finding with two findings", "t41", setv("result.findings", 2))
    f.refused("no coverage", "t42", drop("result.coverage"))
    f.refused("an empty coverage", "t43", coverage())
    f.refused("coverage that is an object", "t44", setv("result.coverage", {"Population": "x", "Judged": 1}))
    f.refused("every kind of bad coverage line", "t45", coverage(
        "text", {"Population": " ", "Judged": 1}, {"Population": "p", "Judged": -1}, {"Population": "p", "Judged": True},
        {"Population": "p", "Judged": 1, "Unjudgeable": {"why": -1}}, {"Population": "p", "Judged": 1, "Unjudgeable": [1]},
        {"Population": "p", "Judged": 1, "Members": "3"}, {"population": None, "judged": "1", "unjudgeable": {"x": 1.5}, "members": -2},
        {"Population": None, "population": "lower is not read when the first key is there", "Judged": None, "judged": 4}))
    f.refused("nothing judged, a member found", "t46", coverage({"Population": "p", "Judged": 0, "Members": 2}))
    f.refused("nothing judged, some unjudgeable", "t47", coverage({"Population": "p", "Judged": 0, "Unjudgeable": {"no model": 2, "x": 1}, "Members": 0}))
    f.refused("nothing judged on an arm that loads the target", "t48", coverage({"Population": "p", "Judged": 0, "Members": 0}))
    f.refused("outputs that are an object", "t50", setv("outputs", {"request": "x"}))
    f.refused("one output", "t51", lambda d, _f: d["outputs"].pop())
    f.refused("four outputs", "t52", lambda d, _f: d["outputs"].extend([dict(d["outputs"][0], role="rows"), dict(d["outputs"][0], role="rows")]))
    f.refused("two requests", "t53", setv("outputs.1.role", "request"))
    f.refused("an output that is text, one with no path, one with a short hash", "t54",
              lambda d, _f: d.__setitem__("outputs", ["x", {"role": "response", "sha256": "a" * 64}, {"role": "rows", "path": "p", "sha256": "abc"}]))
    f.refused("a path that is a number, a path of spaces", "t55", both(setv("outputs.0.path", 5), setv("outputs.1.path", "  ")))
    f.refused("a file that is not there", "t56", setv("outputs.1.path", "gone.jsonl"))
    f.refused("a file that does not hash to its row", "t57", refile(1, b"changed\n", rehash=False))
    f.refused("a plugin as raw evidence", "t58", lambda d, files: (files.append(("t58/Evidence.ESP", b"TES4")),
              d["outputs"].__setitem__(1, {"role": "response", "path": "Evidence.ESP", "sha256": F.sha(b"TES4")})))
    f.refused("a rows file that is not a conflict file", "t59", rows={"rows": []})
    f.refused("a rows file naming a plugin outside the arm", "t60", rows={"Conflicts": [{"A": "Zed.esp", "B": "A.esp"}]})
    f.refused("no origin", "t61", drop("origin"))
    f.refused("an origin of another word", "t62", setv("origin", "copied"))
    f.refused("an origin that is a list", "t63", setv("origin", ["fresh"]))
    f.refused("reused with no source", "t64", setv("origin", "reused"))
    f.refused("reused with a source that is text", "t65", both(setv("origin", "reused"), setv("reusedFrom", "plan00000002")))
    f.refused("reused with every source field wrong", "t66", both(setv("origin", "reused"), setv("reusedFrom", {"planId": " ", "armId": 5, "resultSha256": "x"})))
    f.refused("reused from this arm itself", "t67", both(setv("origin", "reused"),
              setv("reusedFrom", {"planId": "plan00000001", "armId": "baseline", "resultSha256": "A" * 64})))
    f.refused("reused from a setup this plan does not have", "t68", both(setv("origin", "reused"),
              setv("reusedFrom", {"planId": "plan00000001", "armId": "candidate-07", "resultSha256": "a" * 64})))
    f.refused("reused from a setup of this plan with another request", "t69", both(setv("origin", "reused"),
              setv("reusedFrom", {"planId": "plan00000001", "armId": "candidate-01", "resultSha256": "a" * 64})))
    f.refused("fresh that names a source", "t70", setv("reusedFrom", {"planId": "p", "armId": "a", "resultSha256": "a" * 64}))
    f.refused("fresh with no readable request", "t71", setv("outputs.0.sha256", "0" * 64))
    f.refused("a request that is not UTF-8", "t72", refile(0, b'{"method": "initialize"}\n\xff\n'))
    f.refused("a request with a line that is not JSON", "t73", refile(0, (line() + "\nnot json\n").encode()))
    f.refused("a request with no initialize", "t74", refile(0, (CALL + "\n").encode()))
    f.refused("initialize requests of other captures", "t75", refile(0, "\n".join(
        [line(), line(name="evalrun:plan00000001:candidate-01"), line(name=None), line(name=5), line(name="zed"), line(name="It's"),
         json.dumps({"method": "initialize"}), line(name={"a": 1})]).encode()))
    f.refused("a state the configuration does not state", "t80", setv("state", "QuestDone"))
    f.refused("a plugin path that is not there, an overlay with no hash", "t81", both(
        setv("configuration.Plugins.1.Path", "Z:/none/A.esp"),
        setv("configuration.Overlays", [{"Kind": "overwrite", "Path": "", "Sha256": "a" * 64}, {"Kind": "ini", "Path": "x", "Sha256": None}])))
    f.refused("a plugin whose file is not the stated bytes", "t82", setv("configuration.Plugins.1.Sha256", "b" * 64))
    f.refused("another arm's configuration", "t83", lambda d, _f: d.__setitem__("configuration", V.emit_doc(f.root, "reader", "candidate-01")["Configuration"]))
    f.refused("a fingerprint that is not the request's", "t84", setv("configuration.Fingerprint", "e" * 64))
    f.refused("several reasons at once", "t85", both(drop("capturedUtc"), setv("result.findings", -1), drop("origin"), setv("state", "x")))

    # ---- accepted imports, and what follows one
    f.accepted("a fresh import with a relative path and an upper-case hash", "a1")
    f.refused("the same again", "a2")
    f.accepted("findings with a rows file, a note and a verdict", "a3", both(
        setv("result", {"detector": "finding", "findings": 3, "verdict": "review", "note": "three dangling references",
                        "coverage": [{"population": "records", "judged": 7, "unjudgeable": {"no base": 2}, "members": 9}]}),
        setv("capturedUtc", "2026-01-01T01:10:00+01:00")), arm="candidate-01", rows={"Conflicts": [{"A": "a.esp", "B": "B.ESP", "key": "000800"}]})
    f.accepted("reused from another evaluation", "a4", both(setv("origin", "reused"), setv("capturedUtc", "2020-05-05T00:00:00Z"),
               setv("reusedFrom", {"planId": "plan-older", "armId": "baseline", "resultSha256": "AB" * 32, "note": "not kept"})), cid="r.one")

    def block(root):
        # as resume leaves an import whose prerequisite failed: blocked, the folder kept
        led = E.Ledger(root, f.plan)
        with F.fake_clock():
            led.transition("r.records", "baseline", None, "blocked(prerequisite:l.masters)", cause="blocked-prerequisite")
    f.accepted("imported again after it stopped being credited: a second folder", "a5", before=block)
    f.accepted("and a third", "a6", before=block)

    # ---- nothing to judge
    f = Folder(tmp, cases, "a plan whose target no arm loads", base_checks(), targets=[{"name": "Elsewhere.esp"}, {"name": "Far.esm"}])
    f.refused("nothing judged, no member count", "n1", coverage({"Population": "p", "Judged": 0}))
    f.refused("nothing judged, one line without a count", "n2", coverage({"Population": "p", "Judged": 0, "Members": 0}, {"Population": "q", "Judged": 0}))
    f.accepted("nothing to judge", "n3", both(coverage({"Population": "p", "Judged": 0, "Members": 0}, {"population": "q", "judged": 0, "members": 0}),
               setv("result.note", "a note the sentence replaces")))
    f.refused("the same again", "n4", coverage({"Population": "p", "Judged": 0, "Members": 0}))
    f = Folder(tmp, cases, "a plan with no target", base_checks(), targets=[])
    f.refused("nothing judged, no target named", "n1", coverage({"Population": "p", "Judged": 0, "Members": 0}))

    # ---- a plan whose time of making cannot be compared
    f = Folder(tmp, cases, "a plan made at a time with no zone", base_checks(), createdUtc="2026-01-01T00:00:00")
    f.refused("fresh against a plan time with no zone", "z1")
    f = Folder(tmp, cases, "a plan with no time of making", base_checks(), createdUtc=None)
    f.refused("fresh against no plan time", "z1")

    # ---- reuse between two setups of one plan
    f = Folder(tmp, cases, "two setups with one request", base_checks())
    # the plan is rewritten so both arms froze the same request
    f.plan["arms"][1]["requested"] = copy.deepcopy(f.plan["arms"][0]["requested"])
    body = F.jtext(f.plan)
    F.write(f.root, "plan.json", body)
    F.write(f.root, "plan.sha256", F.sha(body.encode("utf-8")) + "\n")
    f.row["tree"] = V.snapshot(f.root)
    f.accepted("reused from the other setup of this plan", "w1", both(
        setv("origin", "reused"), setv("reusedFrom", {"planId": "plan00000001", "armId": "baseline", "resultSha256": "c" * 64}),
        lambda d, _f: d.__setitem__("configuration", V.emit_doc(f.root, "reader", "baseline")["Configuration"])), arm="candidate-01")

    # ---- the prerequisite gate
    def gate(name, settle, arm="candidate-01", ok=False, sub="g1"):
        g = Folder(tmp, cases, "the prerequisite gate: " + name, open_rows(MASTERS, GATED), settle=settle)
        (g.accepted if ok else g.refused)(name, sub, cid="r.gated", arm=arm)
    gate("nothing settled", None)
    gate("nothing settled, on the baseline", None, arm="baseline")
    gate("settled on the arm", lambda b, _r: b.settle("l.masters", "candidate-01", None, "completed", detector="no-finding"), ok=True)
    gate("settled on the baseline only", lambda b, _r: b.settle("l.masters", "baseline", None, "reused", detector="no-finding"), ok=True)
    gate("not applicable on the arm", lambda b, _r: b.settle("l.masters", "candidate-01", None, "not-applicable (no masters)"), ok=True)
    gate("rejected on the arm, accepted on the baseline", lambda b, _r: (
        b.settle("l.masters", "baseline", None, "completed", detector="no-finding"),
        b.settle("l.masters", "candidate-01", None, "rejected(illegal-configuration)", accepted=False)))
    gate("blocked on the arm by its own prerequisite", lambda b, _r: (
        b.settle("l.masters", "baseline", None, "completed", detector="no-finding"),
        b.settle("l.masters", "candidate-01", None, "blocked(prerequisite:p.discovery)", accepted=False)))
    gate("not attempted on the arm, accepted on the baseline", lambda b, _r: (
        b.settle("l.masters", "baseline", None, "completed", detector="no-finding"),
        b.settle("l.masters", "candidate-01", None, "blocked(fixture-not-attempted)", accepted=False)), ok=True)
    gate("failed on the baseline, asked for the baseline", lambda b, _r: b.settle("l.masters", "baseline", None, "failed", accepted=False), arm="baseline")
    return cases


def origin_cases(tmp: Path) -> list:
    """origin_refusals alone, over what _cmd_import_results never hands it in one piece."""
    root = tmp / "origin"
    root.mkdir()
    plan = V.eval_plan(root, base_checks())
    good = (line() + "\n").encode()
    req = F.write(root, "req.jsonl", good)
    rows = []
    captured = datetime.fromisoformat("2026-01-01T00:10:00+00:00")
    for name, doc, arm_id, when, request in (
            ("fresh, one initialize", {"origin": "fresh", "capturedUtc": "2026-01-01T00:10:00Z"}, "baseline", captured, (req, F.sha(good))),
            ("fresh, no capture time parsed", {"origin": "fresh"}, "baseline", None, (req, F.sha(good))),
            ("fresh, the request changed since it was hashed", {"origin": "fresh"}, "baseline", None, (req, "0" * 64)),
            ("fresh, no request", {"origin": "fresh", "reusedFrom": None}, "baseline", captured, None),
            ("an arm id that is a number", {"origin": "fresh"}, 5, None, None),
            ("origin null", {"origin": None}, "baseline", None, None),
            ("origin true", {"origin": True}, "baseline", None, None),
            ("reused, source null", {"origin": "reused", "reusedFrom": None}, "baseline", None, None),
            ("reused, another evaluation, same arm name", {"origin": "reused", "reusedFrom": {"planId": "p2", "armId": "baseline", "resultSha256": "F" * 64}},
             "baseline", captured, None),
            ("reused, this plan, an arm whose request is the same but for case and overlay order", None, "candidate-01", None, None)):
        p = copy.deepcopy(plan)
        if doc is None:
            doc = {"origin": "reused", "reusedFrom": {"planId": p["planId"], "armId": "baseline", "resultSha256": "a" * 64}}
            base, cand = p["arms"][0]["requested"], p["arms"][1]["requested"]
            base["overlays"] = [{"kind": "ini", "sha256": "B" * 64}, {"kind": "ini", "sha256": "a" * 64}, {"kind": None, "sha256": None}]
            cand.update(copy.deepcopy(base))
            cand["overlays"] = cand["overlays"][::-1]
            cand["plugins"] = [dict(x, name=x["name"].upper(), sha256=x["sha256"].upper()) for x in cand["plugins"]]
            cand["providers"] = [dict(x, name=x["name"].upper()) for x in cand["providers"]]
        errors, fields = E.origin_refusals(doc, p, arm_id, when, request)
        rows.append({"name": name, "doc": doc, "plan": V.packed_json(p, root), "arm": arm_id,
                     "captured": None if when is None else "2026-01-01T00:10:00+00:00",
                     "request": None if request is None else {"bytes": F.packb(good), "sha256": request[1]},
                     "errors": F.mask(errors, root), "fields": fields})
    return rows


# ------------------------------------------------------------------------------------------ the records stage

LAB_ENTRY = {"schema": 1, "id": "m", "subject": {"plugin": "Subj.esp", "mod": "MS"}, "source": {"instance": "S:/src", "profile": "P"},
             "lab": {"instance": "L:/lab", "game": "G:/game"}}
IN_PLACE_ENTRY = {"schema": 1, "id": "m", "inPlace": {"instance": "I:/i", "profile": "Default", "game": "G:/g"}, "selection": {"mods": ["S"]}}
OPEN = {"r.records__candidate-10": {"status": "mcp-only"}, "r.records__baseline": {"status": "mcp-only"}, "r.fields__baseline": {"status": "mcp-only"},
        "r.records__candidate-2": {"status": "mcp-only"}, "g.overlap__candidate-3": {"status": "completed"}, "odd": {"status": "mcp-only"},
        "r.none__candidate-4": {"status": None}, "r.done__candidate-5": {"status": "completed", "imported": True}}
READER = {"readerCmd": '"R:/reader.exe" --stdio'}
GOOD_SUMMARY = {"baseline": {"imports": ["{cap}/imports/r.records__baseline/result.json", "{cap}/imports/r.fields__baseline/result.json"], "seconds": 2.5},
                "candidate-10": {"imports": ["{cap}/imports/r.records__candidate-10/result.json", "{cap}/imports/r.done__candidate-5/result.json",
                                             "{cap}/imports/g.overlap__candidate-3/result.json", "loose.json"]},
                "candidate-2": {"imports": []}}


def records_stage_cases(tmp: Path) -> list:
    """stage_records with its children stood in for: the capture's command line, the imports it starts, what it
    prints, refuses and writes."""
    cases = []
    real_child, real_env = P.child, os.environ.get("HOUSECARL_RECORD_READER_CMD")

    def case(name, *, entry=LAB_ENTRY, entries=OPEN, machine=READER, summary=GOOD_SUMMARY, workers=None, capture_exit=0, import_exits=(0, 0, 0),
             capture_log=None, import_logs=True, earlier=0, env_reader=None):
        root = tmp / f"s{len(cases)}"
        out = root / "run"
        F.write(out, "evaluation/ledger.json", json.dumps({"entries": entries}))
        F.write(root, "entry.json", json.dumps(entry))
        F.write(root, "machine.json", json.dumps(machine))
        sdir = out / "records"
        sdir.mkdir()
        for i in range(earlier):
            (sdir / f"capture-{i + 1:02d}").mkdir()
        ctx = P.Context(root / "entry.json", root / "machine.json", out)
        started, imports = [], []

        def child(_ctx, ledger_dir, op, argv, **kw):
            argv = [str(a) for a in argv]
            started.append({"op": op, "dir": F.mask(str(ledger_dir), root), "script": Path(argv[1]).name, "argv": F.mask(argv[2:], root),
                            "more": {k: kw[k] for k in sorted(kw)}})
            if op == "records-capture":
                cap = Path(argv[argv.index("--out") + 1])
                fill = lambda doc: json.loads(json.dumps(doc).replace("{cap}", P.fwd(cap)))   # noqa: E731
                if summary is not None:
                    F.write(cap, "capture-summary.json", json.dumps(fill(summary)))
                if workers is not None:
                    F.write(cap, "capture-workers.json", json.dumps(workers))
                if capture_log is not None:
                    F.write(Path(ledger_dir), "records-capture.txt", capture_log)
                return {"exitCode": capture_exit}
            imports.append(op)
            if import_logs:
                F.write(Path(ledger_dir), op + ".txt", "$ import\n(exit 0, 0.1s)\n\n")
            return {"exitCode": import_exits[len(imports) - 1]}
        P.child = child
        if env_reader is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = env_reader
        row = {"name": name, "entry": entry, "entries": entries, "machine": machine, "summary": summary, "workers": workers,
               "captureExit": capture_exit, "importExits": list(import_exits), "captureLog": capture_log, "importLogs": import_logs,
               "earlier": earlier, "envReader": env_reader}
        try:
            with F.captured() as (printed, _err):
                bound, outputs = P.stage_records(ctx, {}, sdir)
            row["bound"], row["outputs"], row["refusal"] = bound, F.mask([str(o) for o in outputs], root), None
        except P.Refusal as error:
            row["refusal"] = F.mask(str(error), root)
        finally:
            P.child = real_child
        record = sdir / "records.json"
        row["printed"] = F.mask(printed.getvalue(), root)
        row["started"] = started
        row["record"] = F.mask(record.read_text(encoding="utf-8"), root) if record.exists() else None
        row["folders"] = sorted(p.relative_to(out).as_posix() for p in sdir.rglob("*") if p.is_dir())
        cases.append(row)

    slot = ["python", "S:/tools/idle_lock.py", "hold", "--lock", "{lock}", "--min-free-gb", "16", "--purpose", "{purpose}"]
    gave_up = "$ slot\n(exit 3, 2.0s)\n\nwaiting for idle\nidle_lock: gave up waiting for idle: {why}\nidle_lock: gave up waiting for idle: later line\n"
    try:
        case("an in-place entry: the note, no child", entry=IN_PLACE_ENTRY)
        case("no row open: the note, no child", entries={"g.overlap__baseline": {"status": "completed"}, "odd": {"status": "mcp-only"},
                                                        "r.records__baseline": {"status": "mcp-only (imported)"}})
        case("no entries at all", entries=None)
        case("captured and imported: only documents of rows open before the capture, in sorted order")
        case("the workers named as text with spaces", machine=dict(READER, recordWorkers=" 3 "))
        case("the workers named as a number, a third capture folder", machine=dict(READER, recordWorkers=4), earlier=2)
        for bad in ("many", 0, None, 2.5, True, "", "-1", "1.0"):
            case(f"recordWorkers {bad!r} is refused before anything starts", machine=dict(READER, recordWorkers=bad))
        case("no record reader named", machine={})
        case("the record reader named by the environment", machine={}, env_reader="E:/env-reader.exe")
        case("an import refused and an import that never ended, no log of the second", import_exits=(2, None, 0), import_logs=False)
        case("an arm the capture refused, and the workers' own words", workers={
            "requested": "auto", "workers": 2, "case": "fits", "warning": "two arms at once need 9 GB; one is read at a time", "memory": {"freeMB": 9000},
            "curedAlone": {"candidate-2": "the reader ran out of memory", "baseline": "the reader stopped answering"}, "more": "not kept"},
             summary=dict(GOOD_SUMMARY, **{"candidate-2": {"refused": "the reader answered nothing for 3 plugins"}}), import_exits=(0, 3, 0))
        case("arms stopped at their deadline", summary={"candidate-2": {"refused": "stopped at its deadline of 150 min"},
                                                         "baseline": {"refused": {"why": "the arm reached its deadline"}, "imports": ["{cap}/imports/r.records__baseline/result.json"]},
                                                         "candidate-10": {"refused": "refused for another reason"}})
        case("a capture that ended 2 with a summary is read", capture_exit=2, summary={"baseline": {"refused": "the reader could not start"}})
        case("a capture that ended 1", capture_exit=1)
        case("a capture that never ended", capture_exit=None, summary=None)
        case("a capture that ended 0 and wrote no summary", summary=None)
        case("a capture that wrote an empty summary", summary={})
        case("a capture whose slot gave up waiting for memory", capture_exit=3, summary=None, machine=dict(READER, heavyPrefix=slot),
             capture_log=gave_up.format(why="memory: 9.1 GB free under the 16 GB floor after 240 min "))
        case("a capture whose slot gave up waiting for memory, no floor in the slot command", capture_exit=3, summary=None,
             capture_log=gave_up.format(why="free memory stayed low"))
        case("a capture whose slot gave up waiting for an idle machine", capture_exit=3, summary=None, machine=dict(READER, heavyPrefix=slot),
             capture_log=gave_up.format(why="cpu busy: viewprobe.exe"))
        case("a capture that failed for another reason, with a log", capture_exit=5, summary=None, capture_log="$ slot\n(exit 5, 2.0s)\n\nTraceback\n")
    finally:
        P.child = real_child
        if real_env is None:
            os.environ.pop("HOUSECARL_RECORD_READER_CMD", None)
        else:
            os.environ["HOUSECARL_RECORD_READER_CMD"] = real_env
    return cases


SECTIONS = [("fromiso", iso_cases), ("requests", request_cases), ("rows", rows_cases), ("emptyPopulation", empty_cases),
            ("origin", origin_cases), ("imports", import_cases), ("recordsStage", records_stage_cases)]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_import.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-import", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-import-")).resolve()
    os.environ["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")   # so no run folder here becomes the run-log store
    try:
        doc = {"python": platform.python_version(), "placeholders": {"backslash": F.BACK, "forward": F.FWD, "doubled": F.DOUBLED}}
        for name, build in SECTIONS:
            doc[name] = build(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    text = json.dumps(doc, indent=1) + "\n"
    for leak in (tmp.name, Path.home().name):
        assert leak not in text, f"the answers would keep a local path ({leak})"
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
