"""controller_parity - run two run controllers on the same entry and compare what they wrote.

    python _geometry-harness/controller_parity.py run --entry E --machine M --a "<command A>" --b "<command B>" --out DIR [--through STAGE]
    python _geometry-harness/controller_parity.py compare A_RUN B_RUN [--report FILE] [--b-origin FOLDER]
    python _geometry-harness/controller_parity.py exclusions
    python _geometry-harness/controller_parity.py plant KIND RUN_COPY

What this is for. The run controller (evalpipeline.py and evalrun.py) is being ported. A port is right when, on the
same entry and machine file, it writes the same run folder: the same arms, stage decisions, plan, ledger, receipts,
disposition and bundle. This tool runs controller A and then controller B, each into its own Work Folder, and compares
the two run folders file by file.

How it compares. Every file in both run folders is read (JSON, JSON lines, text, gzip of those, else bytes) and
compared in full: JSON deep, key order ignored, list order kept; text line by line. Nothing is skipped by default. The
only values not compared raw are the ones a row of EXCLUSIONS names, each with its reason: timestamps, durations,
random ids, absolute paths and tool digests. Paths and ids are rewritten, not dropped: each side's run folder becomes
<RUN>, its plan id <PLAN>, each receipt id <RECEIPT:key>, and a sha256 that is the hash of a file in the same run
becomes <SHA:that file>, so a reference to the wrong receipt or the wrong file still shows. A comparison reports how
many values each row matched.

It reads files only. It imports neither controller, so it cannot share a mistake with one.

Exit codes: 0 equal after the exclusions; 1 differences (one line each, and all of them in the JSON report);
2 a usage error or a refusal.
"""
from __future__ import annotations

import argparse
import fnmatch
import gzip
import hashlib
import json
import re
import shlex
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT / "scripts"))
from runlog import Run, Runner  # noqa: E402

EXIT_EQUAL, EXIT_DIFFERENT, EXIT_REFUSED = 0, 1, 2
PRESENT = "<present>"


class Refusal(Exception):
    """One sentence: what went wrong and what to try."""


# ---------------------------------------------------------------- the exclusion table
#
# One row per thing that is not compared raw. Fields:
#   id      the row's name in reports
#   files   which files, as patterns on the path inside the run folder ('*' also crosses '/'; ' | ' separates patterns)
#   where   "file"            the whole file or folder
#           "json:<path>"     a JSON value; path segments are split on '/', '*' is one segment, '**' any number
#           "text:<regex>"    a part of a line of a text file
#           "string:<regex>"  a part of any string value or text line ("string:(built in)" for the path and id rewrites)
#   action  ignore            not compared, and may exist on one side only
#           present-only      both sides must have it or both lack it; the value is not compared (null stays null)
#           normalise-path    the absolute root is rewritten to a marker; the rest of the path is compared
#           normalise-id      each side's id is rewritten to the same marker; the reference is still compared
#           unordered         the list is compared as a bag
#   to      for a regex: the replacement
#   when    "different-checkout": the row applies only when the two runs came from different checkouts
#   reason  one sentence

EXCLUSIONS = [
    # ---- whole files and folders
    dict(id="runlog-folder", files="runlog/*", where="file", action="ignore",
         reason="The run log's event store holds only timings, process ids and run ids."),
    dict(id="heavy-lock", files="HEAVY-LOCK-* | */HEAVY-LOCK-*", where="file", action="ignore",
         reason="A machine slot lock file exists only while a heavy step runs and holds a process id."),
    dict(id="idle-lock-log", files="idle_lock.log | */idle_lock.log", where="file", action="ignore",
         reason="The slot command's log holds process ids and local times."),
    dict(id="names-instance", files="names-instance/*", where="file", action="ignore",
         reason="The record reader's private scratch instance is rebuilt by the reader on every run and is not evidence."),

    # ---- paths (built in: every string and every text line)
    dict(id="path-run", files="*", where="string:(built in)", action="normalise-path", to="<RUN>",
         reason="Each side writes into its own run folder; its absolute path is rewritten and the rest is compared."),
    dict(id="path-work", files="*", where="string:(built in)", action="normalise-path", to="<WORK>",
         reason="Each side has its own Work Folder; its absolute path is rewritten and the rest is compared."),
    dict(id="path-repo", files="*", where="string:(built in)", action="normalise-path", to="<REPO>",
         reason="Two runs may come from different checkouts; the checkout root is rewritten and the rest is compared."),
    dict(id="path-tmp", files="*", where="string:(built in)", action="normalise-path", to="<TMP>",
         reason="The system temp folder differs by machine and user; it is rewritten and the rest is compared."),

    # ---- ids (built in: every string and every text line)
    dict(id="id-plan", files="*", where="string:(built in)", action="normalise-id", to="<PLAN>",
         reason="planId is a hash over the plan's timestamps and absolute paths, so it is new on every plan."),
    dict(id="id-receipt", files="*", where="string:(built in)", action="normalise-id", to="<RECEIPT:key>",
         reason="receiptId is a hash over the time of writing; each is rewritten to the key of the receipt that owns it."),
    dict(id="id-run", files="*", where="json:**/runId | json:**/parentRunId | json:**/runIds/* | json:**/reusedFromRunId",
         action="normalise-id", to="<RUNID:n>",
         reason="Run log ids hold a start time and a random part; each is numbered by first appearance."),
    dict(id="id-sweep-cache-key", files="*sweep/*/cache.json", where="json:*", action="normalise-id",
         to="<CACHEKEY:label>", named_by="label", prefix=12,
         reason="A sweep cache key is a hash over the candidate file's path in the run folder; each key, and its "
                "12-character form in logs, is rewritten to the label of the job it stores."),
    dict(id="hash-of-run-file", files="*", where="string:(built in)", action="normalise-id", to="<SHA:file>",
         reason="A sha256 equal to the hash of a file in the same run is rewritten to that file's name; the file's "
                "content is compared instead, since it may embed a time, an id or a path."),

    # ---- timestamps
    dict(id="time-utc-fields", files="*", where="json:**/*Utc", action="present-only",
         reason="A UTC time of writing (createdUtc, startedUtc, endedUtc, updatedUtc, mtimeUtc, checkedUtc and the like)."),
    dict(id="time-at", files="status.json | stages.json | disk-forecast.json", where="json:**/at", action="present-only",
         reason="The pipeline's time of writing."),
    dict(id="time-journal-t", files="*journal.jsonl", where="json:*/t", action="present-only",
         reason="The time of a ledger transition."),
    # ---- durations
    dict(id="seconds-fields", files="*", where="json:**/seconds | json:**/wallSeconds | json:**/reusedSeconds",
         action="present-only", reason="A measured duration."),
    dict(id="seconds-command-log", files="*", where=r"text:^\((exit -?\d+), \d+(?:\.\d+)?s\)$", action="ignore",
         to=r"(\1, <SECONDS>s)", reason="The duration in the second line of a child's log; the exit code is kept."),
    dict(id="seconds-stage-line", files="<stdout>", where=r"text:^([\w-]+: (?:accepted|reused)\b.*? in )\d+(?:\.\d+)? s\b",
         action="ignore", to=r"\1<SECONDS> s", reason="The duration in a stage's stdout line; the decision is kept."),

    dict(id="seconds-runner-echo", files="*.txt", where=r"text:^(\s+\S+\s+exit -?\d+\s+)\d+(?:\.\d+)?s\b", action="ignore",
         to=r"\1<SECONDS>s", reason="The duration in the shared runner's one line per child; the exit code is kept."),
    dict(id="seconds-probe-line", files="*.txt", where=r"text:^\s+\d+\.\d+ s$", action="ignore", to="  <SECONDS> s",
         reason="A probe's own closing line: how long it ran."),
    dict(id="seconds-server-log", files="*.txt", where=r"text:(request handler completed in )\d+(?:\.\d+)?ms", action="ignore",
         to=r"\1<MS>ms", reason="The record reader's server log: how long a request took."),
    dict(id="deadline-argument", files="*.txt | *jobs.jsonl", where=r"string:(--deadline-seconds[ =])\d+(?:\.\d+)?",
         action="ignore", to=r"\1<SECONDS>",
         reason="The time left of the budget when the sweep was started, passed to it on its command line."),
    dict(id="reader-epoch", files="*enrichment/names/02-response.jsonl", where=r'string:("epoch": ")e2-[0-9a-f]+', action="ignore",
         to=r"\1<EPOCH>", reason="The record reader's cache generation, new for every reader process."),
    dict(id="reader-render-ms", files="*enrichment/names/02-response.jsonl", where=r'string:("render_ms": )\d+', action="ignore",
         to=r"\1<MS>", reason="How long the record reader took to render an answer."),

    # ---- the controller's own identity (differs between a Python and a C# controller by design)
    dict(id="controller-identity", files="*plan.json | *receipts/*.json",
         where="json:**/programs/controller/kind | json:**/programs/controller/digest | "
               "json:**/programs/controller/files | json:**/programs/controller/locator | "
               "json:**/programs/controller/interpreter",
         action="present-only", reason="The controller's own program identity: its kind, digest, files and place."),
    dict(id="controller-runner", files="*plan.json", where="json:runner", action="present-only",
         reason="The hashes of the controller's own source or build files, recorded only."),
    dict(id="controller-driver", files="stages.json", where="json:**/driverSha256", action="present-only",
         reason="The hash of the controller file that wrote a stage receipt, recorded only."),
    dict(id="controller-prep-tools", files="preparation-key.json", where="json:tools", action="present-only",
         reason="The controller and probe digests that key preparation reuse."),
    dict(id="options-hash", files="*receipts/*.json", where="json:consumed/optionsSha256", action="present-only",
         reason="A hash over the plan's options, which hold absolute paths."),

    # ---- probe builds (the same on both sides unless the runs came from different checkouts)
    dict(id="probe-build-hashes", files="*plan.json | *receipts/*.json | *.pair-results.jsonl.gz", when="different-checkout",
         where="json:**/exeSha256 | json:**/dllSha256 | json:**/coreDllSha256 | json:*/Probe | json:*/Core",
         action="present-only",
         reason="A probe's build hashes; two checkouts build different binaries from the same source."),
    dict(id="probe-program-digest", files="*plan.json | *receipts/*.json | *jobs.jsonl", when="different-checkout",
         where="json:**/programs/*/digest | json:**/programs/*/files | json:*/program", action="present-only",
         reason="A probe package's digest and file list; two checkouts build different binaries from the same source."),

    # ---- hashes and sizes of things that embed an excluded value
    dict(id="stage-dir-fingerprint", files="stages.json", where="json:**/outputs/*", action="present-only",
         reason="A stage output's fingerprint: a file's is rewritten to the file's name by hash-of-run-file before "
                "this row is reached; a folder's holds file times, and the folder's files are compared one by one."),
    dict(id="stage-disk", files="stages.json", where="json:**/disk/addedBytes", action="present-only",
         reason="Bytes a stage added on disk, which follow the length of every absolute path written."),
    dict(id="size-of-run-file", files="*manifest.json | *receipts/*.json",
         where="json:evidence/*/bytes | json:outputs/*/bytes", action="present-only",
         reason="The size of a run file that may embed a path, time or id; its content is compared instead."),
    dict(id="hash-of-receipts", files="*disposition.json", where="json:derivedFrom/receipts", action="present-only",
         reason="One hash over every receipt file; each receipt holds times and ids, and each is compared by content."),
    dict(id="hash-of-base-manifest", files="*enrichment/*/enrichment.json", where="json:baseManifestSha256", action="present-only",
         reason="The hash of the manifest as it was before enrichment rewrote it; the final manifest is compared by content."),
    # ---- lists with no stated order
    dict(id="historical-classifications", files="*", where="json:**/historicalClassifications", action="unordered",
         reason="order_classification leaves ties in set order, so the list is compared as a bag."),
]

# Lists compared element by element under a name instead of a position, so one dropped element is one difference.
# The order of the names is still compared.
KEYED_LISTS = [
    ("arms.json", "", "id"),
    ("*plan.json", "arms", "armId"),
    ("*plan.json", "checks", "checkId"),
    ("*plan.json", "checks/*/arms", "armId"),
    ("*manifest.json", "evidence", "path"),
]

# Which part of the report a file belongs to; the first match wins.
SECTIONS = [
    ("status", "<exit-code> | <stderr> | status.json"),
    ("arms", "arms.json"),
    ("stage decisions", "stages.json | <stdout>"),
    ("plan", "evaluation/plan.json | evaluation/plan.sha256 | evaluation/population.json | evaluation/inputs/*"),
    ("ledger", "evaluation/ledger.json | evaluation/journal.jsonl"),
    ("receipts", "evaluation/receipts/*"),
    ("disposition", "evaluation/disposition.json | evaluation/STATUS.md"),
    ("bundle", "bundle/*"),
    ("probe outputs", "evaluation/*"),
    ("pipeline stage files", "*"),
]
INVENTORY = "file inventory"


# ---------------------------------------------------------------- small helpers

def _patterns(spec: str) -> list[str]:
    return [p.strip() for p in spec.split(" | ") if p.strip()]


def _file_matches(spec: str, rel: str) -> bool:
    return any(fnmatch.fnmatchcase(rel, p) for p in _patterns(spec))


def _path_matches(pattern: tuple, path: tuple) -> bool:
    """Whether a JSON path matches a pattern: '*' is one segment, '**' any number of segments."""
    if not pattern:
        return not path
    head, rest = pattern[0], pattern[1:]
    if head == "**":
        return any(_path_matches(rest, path[i:]) for i in range(len(path) + 1))
    return bool(path) and fnmatch.fnmatchcase(path[0], head) and _path_matches(rest, path[1:])


def _where(row: dict, kind: str) -> list[str]:
    """The row's selectors of one kind (json, text, string, file), without the kind prefix."""
    out = []
    for part in _patterns(row["where"]):
        if part == kind:
            out.append("")
        elif part.startswith(kind + ":"):
            out.append(part[len(kind) + 1:])
    return out


def _root_regex(root: str):
    """A pattern for one absolute folder in any spelling: either slash, doubled backslashes, any case."""
    parts = [p for p in re.split(r"[\\/]+", root) if p]
    if not parts:
        return None
    return re.compile(r"[\\/]+".join(re.escape(p) for p in parts) + r"(?![\w-])", re.IGNORECASE)


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def _short(value, n=160) -> str:
    text = json.dumps(value, ensure_ascii=False) if not isinstance(value, _Missing) else "(missing)"
    return text if len(text) <= n else text[:n - 3] + "..."


class _Missing:
    def __repr__(self):
        return "(missing)"


MISSING = _Missing()


class _Keyed:
    """A list read under its elements' names: the order of the names, and each element by name."""

    def __init__(self, order, items):
        self.order, self.items = order, items


# ---------------------------------------------------------------- one side

class Side:
    """One run folder, read into memory, with what is needed to rewrite its paths and ids."""

    def __init__(self, label: str, run: Path, *, work=None, repo=None, stdout=None, stderr=None, exit_code=None,
                 origin=None):
        self.label = label
        self.run = Path(run).resolve()
        if not self.run.is_dir():
            raise Refusal(f"{label}: {run} is not a folder; give a run folder (the controller's --out).")
        # a copy of a run folder still names, inside its files, the folder it was written in
        self.origin = Path(origin).resolve() if origin else self.run
        self.work = Path(work).resolve() if work else self.origin.parent
        self.files: dict[str, tuple[str, object]] = {}     # relative path -> (kind, content)
        self.ignored: dict[str, str] = {}                  # relative path -> row id
        self.present_only: dict[str, str] = {}
        self.hash_of: dict[str, str] = {}                  # sha256 -> the first file with it
        self.ids: dict[str, str] = {}                      # id value -> marker
        self.id_rows: dict[str, str] = {}                  # id value -> row id
        self._read_files()
        self._read_process(stdout, stderr, exit_code)
        self.repo = str(Path(repo).resolve()) if repo else self._find_repo()
        self.roots = []
        for row_id, marker, root in (("path-run", "<RUN>", str(self.origin)), ("path-work", "<WORK>", str(self.work)),
                                     ("path-repo", "<REPO>", self.repo), ("path-tmp", "<TMP>", tempfile.gettempdir())):
            rx = _root_regex(root) if root else None
            if rx is not None:
                self.roots.append((len(root), row_id, marker, rx))
        self.roots.sort(key=lambda r: -r[0])

    # -- reading

    def _read_files(self):
        hashes = {}
        for path in sorted(self.run.rglob("*")):
            if not path.is_file():
                continue
            rel = path.relative_to(self.run).as_posix()
            row = next((r for r in EXCLUSIONS if "file" in _patterns(r["where"]) and _file_matches(r["files"], rel)), None)
            if row is not None and row["action"] == "ignore":
                self.ignored[rel] = row["id"]
                continue
            if row is not None:
                self.present_only[rel] = row["id"]
                continue
            hashes.setdefault(_sha256(path), rel)
            self.files[rel] = self._load(rel, path.read_bytes())
        self.hash_of = hashes

    @staticmethod
    def _load(rel: str, data: bytes):
        name = rel
        if name.endswith(".gz"):
            try:
                data, name = gzip.decompress(data), name[:-3]
            except (OSError, EOFError):
                return ("bytes", hashlib.sha256(data).hexdigest())
        try:
            text = data.decode("utf-8-sig")
        except UnicodeDecodeError:
            return ("bytes", hashlib.sha256(data).hexdigest())
        if name.endswith(".json"):
            try:
                return ("json", json.loads(text))
            except ValueError:
                return ("text", text.splitlines())
        if name.endswith(".jsonl"):
            try:
                return ("json", [json.loads(line) for line in text.splitlines() if line.strip()])
            except ValueError:
                return ("text", text.splitlines())
        return ("text", text.splitlines())

    def _read_process(self, stdout, stderr, exit_code):
        """The controller's captured stdout, stderr and exit code: given, or found beside the run folder."""
        beside = self.run.parent
        if stdout is None and (beside / "stdout.txt").is_file():
            stdout = beside / "stdout.txt"
        if stderr is None and (beside / "stderr.txt").is_file():
            stderr = beside / "stderr.txt"
        if exit_code is None and (beside / "exit-code.txt").is_file():
            exit_code = (beside / "exit-code.txt").read_text(encoding="utf-8").strip()
        if stdout is None:
            # the layout of a driver that numbers its launches: the last one made the run folder as it is
            logs = sorted(beside.glob("pipeline-*.stdout.log"))
            if logs:
                stdout = logs[-1]
                err = Path(str(stdout).replace(".stdout.log", ".stderr.log"))
                inv = Path(str(stdout).replace(".stdout.log", ".invocation.json"))
                stderr = err if err.is_file() and stderr is None else stderr
                if inv.is_file() and exit_code is None:
                    exit_code = json.loads(inv.read_text(encoding="utf-8")).get("exitCode")
        self.process = {}
        if stdout is not None:
            self.process["<stdout>"] = ("text", Path(stdout).read_text(encoding="utf-8", errors="replace").splitlines())
        if stderr is not None:
            self.process["<stderr>"] = ("text", Path(stderr).read_text(encoding="utf-8", errors="replace").splitlines())
        if exit_code is not None:
            self.process["<exit-code>"] = ("json", int(exit_code))

    def _find_repo(self):
        """The checkout the run's tools came from: the folder most often named before _geometry-harness."""
        rx = re.compile(r"([A-Za-z]:(?:[\\/]+[^\\/:*?\"<>|\r\n\t]+)*?)[\\/]+_geometry-harness(?![\w-])")
        seen: dict[str, int] = {}

        def note(text):
            for m in rx.finditer(text):
                root = re.sub(r"[\\/]+", "/", m.group(1)).lower()
                seen[root] = seen.get(root, 0) + 1

        def walk(node):
            if isinstance(node, str):
                note(node)
            elif isinstance(node, dict):
                for v in node.values():
                    walk(v)
            elif isinstance(node, list):
                for v in node:
                    walk(v)

        for rel, (kind, content) in {**self.files, **self.process}.items():
            if kind == "bytes":
                continue
            if kind == "json" and not (rel.endswith("plan.json") or rel == "stages.json"):
                continue
            walk(content)
        return max(sorted(seen), key=lambda k: seen[k]) if seen else None

    # -- ids

    def collect_ids(self):
        """Names every random id of this run: the plan id, each receipt's id by its key, run ids by first appearance."""
        plan = self.files.get("evaluation/plan.json") or self.files.get("bundle/evidence/plan.json")
        if plan and plan[0] == "json" and isinstance(plan[1], dict) and isinstance(plan[1].get("planId"), str):
            self._name(plan[1]["planId"], "<PLAN>", "id-plan")
        for rel, (kind, doc) in self.files.items():
            if kind == "json" and "receipts/" in rel and isinstance(doc, dict) and isinstance(doc.get("receiptId"), str):
                key = rel.rsplit("receipts/", 1)[1].removesuffix(".json")
                self._name(doc["receiptId"], f"<RECEIPT:{key}>", "id-receipt")
        counters = {"plan": 1, "receipt": 0, "run": 0}
        run_patterns = [tuple(p.split("/")) for p in _where(_row("id-run"), "json")]

        def walk(node, path):
            if isinstance(node, dict):
                for k, v in node.items():
                    walk(v, path + (k,))
            elif isinstance(node, list):
                for v in node:
                    walk(v, path + ("#",))
            elif isinstance(node, str) and node and path:
                if path[-1] == "planId" and node not in self.ids:
                    counters["plan"] += 1
                    self._name(node, f"<PLAN:{counters['plan']}>", "id-plan")
                elif path[-1] == "receiptId" and node not in self.ids:
                    counters["receipt"] += 1
                    self._name(node, f"<RECEIPT:not-in-run-{counters['receipt']}>", "id-receipt")
                elif node not in self.ids and any(_path_matches(p, path) for p in run_patterns):
                    counters["run"] += 1
                    self._name(node, f"<RUNID:{counters['run']}>", "id-run")

        # ids that are the keys of a dictionary, each named after a field of its value
        for r in EXCLUSIONS:
            if not r.get("named_by"):
                continue
            pats = [tuple(sel.split("/")) for sel in _where(r, "json")]
            for rel in sorted(self.files):
                kind, doc = self.files[rel]
                if kind != "json" or not isinstance(doc, dict) or not _file_matches(r["files"], rel):
                    continue
                for key, value in doc.items():
                    if any(_path_matches(p, (key,)) for p in pats) and isinstance(value, dict):
                        marker = r["to"].replace(r["named_by"] + ">", str(value.get(r["named_by"])) + ">")
                        self._name(key, marker, r["id"])
                        if r.get("prefix"):
                            self._name(key[:r["prefix"]], marker, r["id"])
        # the ledger first, so run ids are numbered in the order the runs happened
        order = sorted(self.files, key=lambda r: (not r.endswith("evaluation/ledger.json"), r))
        for rel in order:
            kind, doc = self.files[rel]
            if kind == "json":
                walk(doc, ())
        if self.ids:
            self._id_rx = re.compile("|".join(re.escape(v) for v in sorted(self.ids, key=len, reverse=True)))
        else:
            self._id_rx = None

    def _name(self, value, marker, row_id):
        if value and value not in self.ids:
            self.ids[value] = marker
            self.id_rows[value] = row_id


def _row(row_id: str) -> dict:
    return next(r for r in EXCLUSIONS if r["id"] == row_id)


# ---------------------------------------------------------------- normalising

class Normaliser:
    """Rewrites one side's content by the exclusion table and counts what each row matched."""

    HEX64 = re.compile(r"(?<![0-9A-Fa-f])[0-9a-f]{64}(?![0-9A-Fa-f])")

    def __init__(self, side: Side, counts: dict, different_checkout: bool):
        self.side, self.counts = side, counts
        self.rows = [r for r in EXCLUSIONS if r.get("when") != "different-checkout" or different_checkout]
        self.string_rules = [(r, re.compile(sel)) for r in self.rows for sel in _where(r, "string") if sel != "(built in)"]

    def _hit(self, row_id, n=1):
        if n:
            self.counts[row_id] = self.counts.get(row_id, 0) + n

    def string(self, text: str, rel: str) -> str:
        side = self.side
        if side._id_rx is not None:
            def swap(m):
                self._hit(side.id_rows[m.group(0)])
                return side.ids[m.group(0)]
            text = side._id_rx.sub(swap, text)
        for _, row_id, marker, rx in side.roots:
            text, n = rx.subn(lambda m, marker=marker: marker, text)
            self._hit(row_id, n)

        def named(m):
            rel_of = side.hash_of.get(m.group(0))
            if rel_of is None:
                return m.group(0)
            self._hit("hash-of-run-file")
            return f"<SHA:{rel_of}>"
        if len(text) >= 64:
            text = self.HEX64.sub(named, text)
        for row, rx in self.string_rules:
            if _file_matches(row["files"], rel):
                text, n = rx.subn(row["to"], text)
                self._hit(row["id"], n)
        return text

    def text(self, lines: list[str], rel: str) -> list[str]:
        rules = [(r, re.compile(sel)) for r in self.rows if _file_matches(r["files"], rel) for sel in _where(r, "text")]
        out = []
        for line in lines:
            for row, rx in rules:
                line, n = rx.subn(row["to"], line)
                self._hit(row["id"], n)
            out.append(self.string(line, rel))
        return out

    def json(self, doc, rel: str):
        rules = []
        for r in self.rows:
            if r["action"] != "normalise-id" and _file_matches(r["files"], rel):
                rules += [(r, tuple(sel.split("/")) if sel else ()) for sel in _where(r, "json")]
        keyed = [(tuple(p.split("/")) if p else (), field) for files, p, field in KEYED_LISTS if _file_matches(files, rel)]

        def walk(node, path):
            row = next((r for r, pat in rules if _path_matches(pat, path)), None)
            if row is not None and row["action"] == "ignore":
                self._hit(row["id"])
                return MISSING
            if row is not None and row["action"] == "present-only":
                # a file hash is first rewritten to the file's name; only what is left is compared by presence
                if isinstance(node, str) and node in self.side.hash_of:
                    self._hit("hash-of-run-file")
                    return f"<SHA:{self.side.hash_of[node]}>"
                self._hit(row["id"])
                return None if node is None else PRESENT
            if isinstance(node, dict):
                out = {}
                for k, v in node.items():
                    value = walk(v, path + (k,))
                    if value is not MISSING:
                        out[self.string(k, rel)] = value
                return out
            if isinstance(node, list):
                items = [walk(v, path + ("#",)) for v in node]
                items = [v for v in items if v is not MISSING]
                if row is not None and row["action"] == "unordered":
                    self._hit(row["id"], len(items))
                    return sorted(items, key=lambda v: json.dumps(v, sort_keys=True, default=str))
                field = next((f for pat, f in keyed if _path_matches(pat, path)), None)
                if field and all(isinstance(v, dict) and isinstance(v.get(field), str) for v in items):
                    names = [v[field] for v in items]
                    if len(set(names)) == len(names):
                        return _Keyed(names, dict(zip(names, items)))
                return items
            if isinstance(node, str):
                return self.string(node, rel)
            return node

        return walk(doc, ())


# ---------------------------------------------------------------- comparing

def _same_scalar(a, b) -> bool:
    return type(a) is type(b) and a == b


def _diff(a, b, where: str, out: list):
    """Every difference between two normalised values, as (where, a, b)."""
    if isinstance(a, _Keyed) and isinstance(b, _Keyed):
        if [n for n in a.order if n in b.items] != [n for n in b.order if n in a.items]:
            out.append((where + " (order)", a.order, b.order))
        for name in list(a.order) + [n for n in b.order if n not in a.items]:
            _diff(a.items.get(name, MISSING), b.items.get(name, MISSING), f"{where}[{name}]", out)
        return
    if isinstance(a, _Keyed) or isinstance(b, _Keyed):
        # one side's names repeat or are missing, so the list could not be read by name there: compare by position
        a = [a.items[n] for n in a.order] if isinstance(a, _Keyed) else a
        b = [b.items[n] for n in b.order] if isinstance(b, _Keyed) else b
    if isinstance(a, dict) and isinstance(b, dict):
        for key in list(a) + [k for k in b if k not in a]:
            _diff(a.get(key, MISSING), b.get(key, MISSING), f"{where}/{key}" if where else key, out)
        return
    if isinstance(a, list) and isinstance(b, list):
        for i in range(max(len(a), len(b))):
            _diff(a[i] if i < len(a) else MISSING, b[i] if i < len(b) else MISSING, f"{where}[{i}]", out)
        return
    if a is MISSING and b is MISSING:
        return
    if a is MISSING or b is MISSING or not _same_scalar(a, b):
        out.append((where, _plain(a), _plain(b)))


def _plain(value):
    """A normalised value as plain JSON, for the report."""
    if isinstance(value, _Keyed):
        return [_plain(value.items[n]) for n in value.order]
    if isinstance(value, dict):
        return {k: _plain(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_plain(v) for v in value]
    return value


def _count_values(value) -> int:
    if isinstance(value, _Keyed):
        return sum(_count_values(v) for v in value.items.values())
    if isinstance(value, dict):
        return sum(_count_values(v) for v in value.values())
    if isinstance(value, list):
        return sum(_count_values(v) for v in value)
    return 1


def _section_of(rel: str) -> str:
    return next(name for name, spec in SECTIONS if _file_matches(spec, rel))


def compare(a: Side, b: Side) -> dict:
    """The full comparison of two sides: differences by section, and what each exclusion row matched."""
    a.collect_ids()
    b.collect_ids()
    different_checkout = (a.repo or "").lower() != (b.repo or "").lower()
    counts: dict[str, int] = {}
    na, nb = Normaliser(a, counts, different_checkout), Normaliser(b, counts, different_checkout)
    sections = {name: {"name": name, "files": 0, "values": 0, "differences": []} for name, _ in SECTIONS}
    sections[INVENTORY] = {"name": INVENTORY, "files": 0, "values": 0, "differences": []}
    differences = []

    def add(section, file, where, va, vb):
        d = {"section": section, "file": file, "where": where, "a": va if va is not MISSING else None,
             "b": vb if vb is not MISSING else None, "aMissing": va is MISSING, "bMissing": vb is MISSING}
        d["line"] = f"DIFF [{section}] {file} :: {where or '(whole file)'} :: A={_short(va)} B={_short(vb)}"
        sections[section]["differences"].append(d)
        differences.append(d)

    # the file inventory, under names with ids and roots rewritten
    names_a = {na.string(rel, rel): rel for rel in list(a.files) + list(a.present_only)}
    names_b = {nb.string(rel, rel): rel for rel in list(b.files) + list(b.present_only)}
    sections[INVENTORY]["files"] = len(set(names_a) | set(names_b))
    sections[INVENTORY]["values"] = sections[INVENTORY]["files"]
    for name in sorted(set(names_a) | set(names_b)):
        if name not in names_a or name not in names_b:
            add(INVENTORY, name, "", "file written" if name in names_a else MISSING,
                "file written" if name in names_b else MISSING)
    for rel in a.ignored:
        counts[a.ignored[rel]] = counts.get(a.ignored[rel], 0) + 1
    for rel in b.ignored:
        counts[b.ignored[rel]] = counts.get(b.ignored[rel], 0) + 1
    for side in (a, b):
        for rel, row_id in side.present_only.items():
            counts[row_id] = counts.get(row_id, 0) + 1

    # each file both sides wrote, and the controller's own stdout, stderr and exit code
    pairs = [(name, a.files.get(names_a[name]), b.files.get(names_b[name]))
             for name in sorted(set(names_a) & set(names_b)) if names_a[name] in a.files and names_b[name] in b.files]
    not_checked = []
    for name in ("<exit-code>", "<stdout>", "<stderr>"):
        if name in a.process and name in b.process:
            pairs.append((name, a.process[name], b.process[name]))
        else:
            missing = [s.label for s in (a, b) if name not in s.process]
            not_checked.append(f"{name}: not captured for {' and '.join(missing)}")
    for name, (kind_a, doc_a), (kind_b, doc_b) in pairs:
        section = _section_of(name)
        sections[section]["files"] += 1
        if kind_a != kind_b:
            add(section, name, "", f"read as {kind_a}", f"read as {kind_b}")
            continue
        if kind_a == "json":
            va, vb = na.json(doc_a, name), nb.json(doc_b, name)
        elif kind_a == "text":
            va, vb = na.text(doc_a, name), nb.text(doc_b, name)
        else:
            va, vb = doc_a, doc_b
        sections[section]["values"] += _count_values(va)
        found = []
        _diff(va, vb, "", found)
        for where, x, y in found:
            if kind_a == "text":
                where = "line " + str(int(where.strip("[]")) + 1)
            add(section, name, where, x, y)

    rows = []
    for r in EXCLUSIONS:
        applies = r.get("when") != "different-checkout" or different_checkout
        rows.append({"id": r["id"], "files": r["files"], "where": r["where"], "action": r["action"],
                     "reason": r["reason"], "applied": applies, "matched": counts.get(r["id"], 0)})
    return {
        "schema": 1,
        "equal": not differences,
        "a": {"label": a.label, "run": str(a.run), "work": str(a.work), "repo": a.repo},
        "b": {"label": b.label, "run": str(b.run), "work": str(b.work), "repo": b.repo},
        "differentCheckout": different_checkout,
        "notChecked": not_checked,
        "sections": list(sections.values()),
        "differenceCount": len(differences),
        "exclusions": rows,
        "ignoredFiles": {"a": a.ignored, "b": b.ignored},
    }


def print_report(report: dict):
    for s in report["sections"]:
        state = "equal" if not s["differences"] else f"{len(s['differences'])} difference(s)"
        print(f"{s['name']:22} {s['files']:4} file(s) {s['values']:7} value(s)  {state}")
    for line in report["notChecked"]:
        print("not checked: " + line)
    top = sorted((r for r in report["exclusions"] if r["matched"]), key=lambda r: -r["matched"])
    print("exclusion rows that matched: " + (", ".join(f"{r['id']} {r['matched']}" for r in top) or "none"))
    for s in report["sections"]:
        for d in s["differences"]:
            print(d["line"])
    print("EQUAL after exclusions" if report["equal"] else f"DIFFERENT: {report['differenceCount']} difference(s)")


# ---------------------------------------------------------------- planted differences

PLANT_KINDS = ("ledger-status", "evidence-row", "arm-dropped", "refusal-sentence", "plan-status", "finding-count",
               "file-missing", "stage-decision")


def _read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _write(path: Path, doc):
    path.write_text(json.dumps(doc, indent=1, ensure_ascii=False), encoding="utf-8")


def _need(path: Path, what: str) -> Path:
    if not path.is_file():
        raise Refusal(f"this run has no {what} ({path.name}), so this kind cannot be planted in it; pick another kind "
                      f"or a run that got further.")
    return path


def _compared_leaf(doc, rel: str):
    """The first number or plain word in a JSON document that the comparison reads raw: its path, as keys and indexes."""
    rules = []
    for r in EXCLUSIONS:
        if _file_matches(r["files"], rel):
            rules += [tuple(sel.split("/")) if sel else () for sel in _where(r, "json")]
    found = []

    def walk(node, path, keys):
        if found or any(_path_matches(p, path) for p in rules):
            return
        if isinstance(node, dict):
            for k, v in node.items():
                walk(v, path + (k,), keys + [k])
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk(v, path + ("#",), keys + [i])
        elif isinstance(node, bool):
            return
        elif isinstance(node, int) or (isinstance(node, str) and re.fullmatch(r"[A-Za-z][A-Za-z -]{2,40}", node)):
            found.append(keys)

    walk(doc, (), [])
    return found[0] if found else None


def plant(kind: str, run: Path) -> dict:
    """Edits a COPY of a run folder to hold one real difference; returns what was changed and where."""
    run = Path(run)
    if kind == "ledger-status":
        path = _need(run / "evaluation/ledger.json", "ledger")
        doc = _read(path)
        key = sorted(doc.get("entries") or {})[0] if doc.get("entries") else None
        if key is None:
            raise Refusal("this run's ledger has no entry, so no status can be flipped; pick a run that evaluated.")
        old = doc["entries"][key]["status"]
        doc["entries"][key]["status"] = "failed" if old == "completed" else "completed"
        _write(path, doc)
        return {"kind": kind, "file": "evaluation/ledger.json", "where": f"entries/{key}/status", "was": old}
    if kind == "evidence-row":
        manifest = _read(_need(run / "bundle/evidence/manifest.json", "bundle manifest"))
        for row in manifest.get("evidence") or []:
            rel = row.get("path") or ""
            origin = run / "evaluation" / rel.removeprefix("evidence/")
            copy = run / "bundle/evidence" / rel
            if rel.startswith("evidence/") and rel.endswith(".json") and origin.is_file() and copy.is_file():
                doc = _read(copy)
                keys = _compared_leaf(doc, "bundle/evidence/" + rel)
                if keys is None:
                    continue
                for target in (copy, origin):
                    d = _read(target)
                    node = d
                    for k in keys[:-1]:
                        node = node[k]
                    old = node[keys[-1]]
                    node[keys[-1]] = old + 1 if isinstance(old, int) else old + " planted"
                    _write(target, d)
                where = "".join(f"[{k}]" if isinstance(k, int) else "/" + k for k in keys).lstrip("/")
                return {"kind": kind, "file": "bundle/evidence/" + rel, "alsoFile": "evaluation/" + rel.removeprefix("evidence/"),
                        "where": where, "was": old}
        raise Refusal("this run's bundle holds no JSON evidence file with a value to change; pick a run with probe output.")
    if kind == "arm-dropped":
        path = _need(run / "arms.json", "arm list")
        arms = _read(path)
        if not arms:
            raise Refusal("this run's arms.json is empty, so no arm can be dropped.")
        dropped = arms.pop()
        _write(path, arms)
        plan = run / "evaluation/plan.json"
        if plan.is_file():
            doc = _read(plan)
            if doc.get("arms"):
                doc["arms"].pop()
                _write(plan, doc)
        return {"kind": kind, "file": "arms.json", "where": f"[{dropped['id']}]", "was": dropped["id"]}
    if kind == "refusal-sentence":
        captured = run.parent / "stderr.txt"
        if not (run / "status.json").is_file() and captured.is_file() and captured.read_text(encoding="utf-8").strip():
            # a refusal before the run folder holds anything: the sentence is only in the captured stderr beside it
            lines = captured.read_text(encoding="utf-8").splitlines()
            old, lines[-1] = lines[-1], lines[-1] + " (planted)"
            captured.write_text("\n".join(lines) + "\n", encoding="utf-8")
            return {"kind": kind, "file": "<stderr>", "where": f"line {len(lines)}", "was": old}
        path = _need(run / "status.json", "status")
        doc = _read(path)
        old = doc.get("sentence")
        doc["sentence"] = (old or "") + " (planted)"
        _write(path, doc)
        return {"kind": kind, "file": "status.json", "where": "sentence", "was": old}
    if kind == "plan-status":
        path = _need(run / "evaluation/plan.json", "plan")
        doc = _read(path)
        check = next((c for c in doc.get("checks") or [] if c.get("arms")), None)
        if check is None:
            raise Refusal("this run's plan has no check with a per-arm status.")
        arm = check["arms"][0]
        old, arm["status"] = arm["status"], "planted-status"
        _write(path, doc)
        return {"kind": kind, "file": "evaluation/plan.json",
                "where": f"checks[{check['checkId']}]/arms[{arm['armId']}]/status", "was": old}
    if kind == "finding-count":
        folder = run / "evaluation/receipts"
        for path in sorted(folder.glob("*.json")) if folder.is_dir() else []:
            doc = _read(path)
            if isinstance((doc.get("result") or {}).get("findings"), int):
                old = doc["result"]["findings"]
                doc["result"]["findings"] = old + 1
                _write(path, doc)
                return {"kind": kind, "file": "evaluation/receipts/" + path.name, "where": "result/findings", "was": old}
        raise Refusal("this run has no receipt with a finding count; pick a run that evaluated.")
    if kind == "file-missing":
        folder = run / "bundle/evidence"
        files = sorted(p for p in (folder / "evidence").rglob("*") if p.is_file()) if (folder / "evidence").is_dir() else []
        files = files or (sorted((folder / "receipts").glob("*.json")) if (folder / "receipts").is_dir() else [])
        if not files:
            raise Refusal("this run's bundle holds no evidence file to delete; pick a run that made a bundle.")
        files[0].unlink()
        return {"kind": kind, "file": files[0].relative_to(run).as_posix(), "where": "", "was": "file written"}
    if kind == "stage-decision":
        path = _need(run / "stages.json", "stage ledger")
        doc = _read(path)
        if not doc.get("stages"):
            raise Refusal("this run's stages.json has no stage receipt; pick a run where a stage finished.")
        stage = list(doc["stages"])[-1]
        old = doc["stages"][stage].get("accepted")
        doc["stages"][stage]["accepted"] = not old
        _write(path, doc)
        return {"kind": kind, "file": "stages.json", "where": f"stages/{stage}/accepted", "was": old}
    raise Refusal(f"unknown kind {kind!r}; the kinds are: {', '.join(PLANT_KINDS)}.")


# ---------------------------------------------------------------- running both controllers

def _launch(label: str, command: str, extra: list[str], args, side_dir: Path, in_place: bool) -> int:
    """One controller run into side_dir/run, with its stdout, stderr and exit code kept beside it."""
    side_dir.mkdir(parents=True, exist_ok=True)
    argv = [part.strip('"') for part in shlex.split(command, posix=False)]
    argv += ["run", "--entry", str(args.entry), "--machine", str(args.machine), "--out", str(side_dir / "run")]
    if args.through:
        argv += ["--through", args.through]
    if in_place:
        argv += ["--reuse-preparation-from", str(side_dir)]
    argv += extra
    env = {"HOUSECARL_RUNLOG_DIR": str(side_dir / "runlog"), "PYTHONIOENCODING": "utf-8", "PYTHONDONTWRITEBYTECODE": "1"}
    job = {"id": "controller", "argv": argv, "cwd": str(side_dir), "env": env}
    res = Runner("controller-parity-" + label, out_dir=side_dir).run([job], echo=False)[0]
    if res.get("exitCode") is None:
        raise Refusal(f"controller {label} did not finish ({res.get('stopped')}); see {side_dir / 'controller.txt'}.")
    log = (side_dir / "controller.txt").read_text(encoding="utf-8")
    body = log.split("\n\n", 1)[1] if "\n\n" in log else ""
    out, _, err = body.partition("\n---- stderr ----\n")
    (side_dir / "stdout.txt").write_text(out, encoding="utf-8")
    (side_dir / "stderr.txt").write_text(err, encoding="utf-8")
    (side_dir / "exit-code.txt").write_text(str(res["exitCode"]), encoding="utf-8")
    (side_dir / "command.json").write_text(json.dumps({"argv": argv, "cwd": str(side_dir), "env": env}, indent=1),
                                           encoding="utf-8")
    print(f"controller {label}: exit {res['exitCode']} in {res['seconds']:.1f} s")
    return res["exitCode"]


def cmd_run(args) -> int:
    for name in ("entry", "machine"):
        if not Path(getattr(args, name)).is_file():
            raise Refusal(f"--{name} {getattr(args, name)} is not a file.")
    args.entry, args.machine = Path(args.entry).resolve(), Path(args.machine).resolve()
    out = Path(args.out).resolve()
    for label in ("a", "b"):
        if (out / label / "run").exists():
            raise Refusal(f"{out / label / 'run'} already exists; give an empty --out so each controller starts fresh.")
    in_place = "inPlace" in _read(args.entry)
    with Run("controller-parity", mode="run", stage_source="counted") as run:
        run.stage("controllers", "running both controllers", 2, "runs")
        _launch("a", args.a, args.a_arg, args, out / "a", in_place)
        run.advance()
        _launch("b", args.b, args.b_arg, args, out / "b", in_place)
        run.advance()
        run.stage("compare", "comparing the run folders", 1, "comparisons")
        for label in ("a", "b"):
            if not (out / label / "run").is_dir():
                # a controller that refuses before it writes anything is still compared: on what it printed
                (out / label / "run").mkdir(parents=True)
        report = compare(Side("A", out / "a" / "run", work=out / "a"), Side("B", out / "b" / "run", work=out / "b"))
        run.advance()
        return _finish(report, Path(args.report) if args.report else out / "parity.json")


def cmd_compare(args) -> int:
    with Run("controller-parity", mode="compare"):
        a = Side("A", args.a_run, work=args.a_work, repo=args.a_repo, stdout=args.a_stdout, exit_code=args.a_exit,
                 origin=args.a_origin)
        b = Side("B", args.b_run, work=args.b_work, repo=args.b_repo, stdout=args.b_stdout, exit_code=args.b_exit,
                 origin=args.b_origin)
        return _finish(compare(a, b), Path(args.report) if args.report else None)


def _finish(report: dict, report_path) -> int:
    print_report(report)
    if report_path is not None:
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
        print(f"report: {report_path}")
    return EXIT_EQUAL if report["equal"] else EXIT_DIFFERENT


def cmd_exclusions(_args) -> int:
    for r in EXCLUSIONS:
        when = " (only between different checkouts)" if r.get("when") == "different-checkout" else ""
        print(f"{r['id']}\n  files : {r['files']}\n  where : {r['where']}\n  action: {r['action']}{when}\n  reason: {r['reason']}")
    print(f"{len(EXCLUSIONS)} rows. Anything no row names is compared raw.")
    return EXIT_EQUAL


def cmd_plant(args) -> int:
    print(json.dumps(plant(args.kind, Path(args.run_copy)), ensure_ascii=False))
    return EXIT_EQUAL


def build_parser():
    ap = argparse.ArgumentParser(description="Run two run controllers on one entry and compare their run folders.")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("run", help="run controller A, then B, then compare")
    p.add_argument("--entry", required=True, help="the selection entry both controllers get")
    p.add_argument("--machine", required=True, help="the machine file both controllers get")
    p.add_argument("--a", required=True, help="controller A's command line, without its subcommand")
    p.add_argument("--b", required=True, help="controller B's command line, without its subcommand")
    p.add_argument("--out", required=True, help="an empty folder; each controller writes under a/ or b/")
    p.add_argument("--through", help="stop both controllers after this stage")
    p.add_argument("--a-arg", action="append", default=[], help="one more argument for controller A (repeatable)")
    p.add_argument("--b-arg", action="append", default=[], help="one more argument for controller B (repeatable)")
    p.add_argument("--report", help="where to write the JSON report (default: parity.json under --out)")
    p.set_defaults(fn=cmd_run)
    p = sub.add_parser("compare", help="compare two finished run folders")
    p.add_argument("a_run", metavar="A_RUN")
    p.add_argument("b_run", metavar="B_RUN")
    p.add_argument("--report", help="where to write the JSON report")
    p.add_argument("--a-origin", help="for a copy: the folder side A's run was written in")
    p.add_argument("--b-origin", help="for a copy: the folder side B's run was written in")
    p.add_argument("--a-work", help="side A's Work Folder (default: the parent of the folder the run was written in)")
    p.add_argument("--b-work", help="side B's Work Folder (default: the parent of the folder the run was written in)")
    p.add_argument("--a-repo", help="the checkout side A's tools came from (default: read from the run)")
    p.add_argument("--b-repo", help="the checkout side B's tools came from (default: read from the run)")
    p.add_argument("--a-stdout", help="side A's captured stdout (default: found beside the run folder)")
    p.add_argument("--b-stdout", help="side B's captured stdout (default: found beside the run folder)")
    p.add_argument("--a-exit", help="side A's exit code (default: found beside the run folder)")
    p.add_argument("--b-exit", help="side B's exit code (default: found beside the run folder)")
    p.set_defaults(fn=cmd_compare)
    p = sub.add_parser("exclusions", help="print every value that is not compared raw, with its reason")
    p.set_defaults(fn=cmd_exclusions)
    p = sub.add_parser("plant", help="edit a COPY of a run folder to hold one real difference")
    p.add_argument("kind", choices=PLANT_KINDS)
    p.add_argument("run_copy", metavar="RUN_COPY")
    p.set_defaults(fn=cmd_plant)
    return ap


def main(argv=None) -> int:
    try:
        args = build_parser().parse_args(argv)
    except SystemExit as e:
        return EXIT_REFUSED if e.code not in (0, None) else 0
    try:
        return args.fn(args)
    except Refusal as e:
        print(f"REFUSED: {e}", file=sys.stderr)
        return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
