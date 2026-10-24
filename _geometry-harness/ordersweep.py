"""ordersweep — evaluate bounded alternative load orders through the real detectors (roadmap 4.20).

    python ordersweep.py --candidates DIR --out DIR [--probes navownprobe,overlapprobe,contactprobe]
                         [--instance DIR --profile NAME --game DIR] [--probe-args "..."]
                         [--harness DIR] [--baseline FILE] [--max-candidates N] [--no-reverse]
                         [--probe-cmd NAME=<argv prefix>]...

The question this answers is the roadmap's: WHICH of the examined configurations passes the required
checks, what would the user change, and what remains unknown. It does not sort a load order and it
does not search — `orderprobe` does the bounded search and writes one CandidateFile per candidate
(`candidate-00-baseline.json`, `candidate-01-....json`, ...) plus `candidates.json`, the manifest that
carries the constraints, the contradictions and the budget outcome. This driver takes that directory
and, for every candidate and every probe named:

  1. runs the probe with `--order <candidate>` (the candidate reaches the actual consumer, asset view
     included — HarnessOrderCli.cs) and `--base <baseline emit>`, so the candidate arm re-measures every
     baseline row's physical predicate under its own order;
  2. runs the REVERSE measurement: the probe on the BASELINE with `--base <candidate emit>`, so every
     candidate row's physical predicate is also known under the baseline order. This is what separates a
     row that ENTERED the class because a defect is new from one that entered because the class's
     attribution changed with the history (an overlap the later base mod "introduced blind" is a
     different row depending on which mod loads later, with identical winners — the same world). A
     created row whose predicate ALSO holds under the baseline is attribution, not a regression; one
     whose predicate is Gone under the baseline is a new physical defect; one the reverse run could not
     measure stays unmeasured and blocks a pass. Both are existing primitives (`--base`), composed;
  3. runs `conflictdiff <baseline emit> <candidate emit>` — the credit rule is unchanged: REPAIRED only
     when a row's physical predicate went Holds -> Gone, CREATED when a row entered with it holding,
     the coverage test refuses a candidate whose unjudgeable share rose;
  4. classifies the candidate from what those steps ESTABLISHED, never from a row count:
       PASSES-COMPLETED-CHECKS   every probe ran, nothing refused, no NEW physical defect, nothing
                                 UNJUDGED, nothing resting on an unmeasured predicate
       REGRESSION                a created row with a NEW physical defect anywhere — aggregate
                                 improvements cannot cancel it
       UNRESOLVED                a probe refused a check, or a verdict is UNJUDGED, or a created or
                                 vanished row rests on an unmeasured predicate: the checks are not
                                 complete for it
       ILLEGAL                   the probe refused the configuration itself (a master after its
                                 dependent, an .esp ahead of a master-flagged plugin, a name nothing
                                 provides) — the game would not load it as written.

REUSE (the handoff's requirement 4). A probe run is expensive, so a result is reused only when every
input it consumed is unchanged: the request key covers the probe binary (its hash), the candidate file,
the base emit and the arguments; and before an entry is reused, every plugin and overlay the emit's own
ConfigurationIdentity recorded is re-hashed on disk. A cache entry whose emit carries no identity is
never reused — a missing identity prevents reuse, it does not permit it. An entry names its emit relative
to the cache file's folder with the emit's sha256, and is reused only when that file still hashes to it.
Earlier launches' caches can be searched read-only (`--reuse-cache`). Every decision is printed and
written to `cache-log.txt` (REUSED / MISS / INVALIDATED <reason>), so reuse and invalidation are
observable rather than assumed.

JOB RECORDS. `jobs.jsonl` gets one line per job (probe or diff) as soon as it has a result: whether it ran,
was reused or was stopped, its own exit code, the emit it produced or was served and whether this launch
wrote it, and the emits it read. A zero exit that wrote no emit is not a measurement: no reverse or diff is
built on it and its candidate reads UNRESOLVED.

The report (`REPORT.md`) ends with the search outcome from the manifest — exhausted or not, how many
legal candidates were not evaluated, which constraints contradicted — and, whenever no candidate passes,
the sentence that no passing order found within the budget is not proof that none exists.

Every child process is a RunLog child of one parent run (scripts/runlog.py's Runner), so the sweep's
timing lands in the shared history and a later run can be estimated from it.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
from pathlib import Path, PurePosixPath, PureWindowsPath

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run, Runner  # noqa: E402

DEFAULT_PROBES = ["navownprobe", "overlapprobe", "contactprobe"]
VERDICT_RE = re.compile(r"verdict\s*:\s*(DIFFERENTIAL-[A-Z]+)")
COUNT_RE = re.compile(r"^\s*(repaired|reclassified|created|unmeasured|unchanged)\s*:\s*([\d,]+)", re.M)
# A refusal, in any case, but not a count: "refused 50" and "3 refused" are tallies (overlapprobe's timer line, review summaries).
REFUSED_RE = re.compile(r"MISSING MASTER|CANNOT ENABLE|PROFILE MISMATCH|(?<![\d,] )\brefused\b(?!\s*[\d,]*\d)", re.I)
NOT_PROOF = "No passing order found within the budget is not proof that none exists."


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def probe_exe(harness: Path, probe: str) -> Path:
    name = f"{probe}.exe" if os.name == "nt" else probe
    p = harness / probe / "bin" / "Release" / "net9.0" / name
    if not p.exists():
        sys.exit(f"{probe}: no Release binary at {p} — build it first (dotnet build -c Release)")
    return p


def probe_program(harness: Path, probe: str, overrides: dict[str, str]) -> list[str]:
    """The argv PREFIX that runs one probe (or conflictdiff). Normally the Release binary; a
    `--probe-cmd NAME=<argv prefix>` stands a different program in, which is how a fixture runs a
    fake probe against the real driver. Nothing else about this driver changes: an override alters
    WHICH program runs, never how its result is read or classified."""
    override = overrides.get(probe)
    if override:
        return override.split()
    return [str(probe_exe(harness, probe))]


#: Probes whose reverse run can take the measured baseline emit's rows instead of sweeping the baseline order again
#: (overlap acceleration P1; contactprobe 2026-10-21): probe -> the flag that names the baseline emit. The reverse still
#: re-measures every --base row under the baseline order; the probe refuses by name unless the baseline emit is its own on this
#: configuration.
RECONCILE_REVERSE = {"overlapprobe": "--reverse-of", "contactprobe": "--reverse-of"}

#: Probes that keep placed-pair results across runs of one build (overlap acceleration P3). Each job writes the results it
#: measured to <out>/<probe>/<job>.pair-results.jsonl.gz and reads every such file written before it in this launch and in
#: the earlier launches --reuse-cache names. The probe reads a file only when it was written by the same build under the
#: same pair time limit, and keys each result by every argument that can change it, so reading one never changes an emit.
PAIR_RESULTS = {"overlapprobe"}
PAIR_RESULTS_SUFFIX = ".pair-results.jsonl.gz"


def program_key(harness: Path, probe: str, overrides: dict[str, str]) -> str:
    """What the cache must key a program by: the files a build puts in its Release folder, by relative path and hash:
    every top-level file (the exe apphost, the dlls, the runtime json, data files the project copies to output) and the
    runtimes/ tree; debug symbols are left out. Other subfolders are not build output (viewprobe writes shots/ there at
    run time), so a run never changes its own key. The exe alone is the same launcher for every build of a project, so a
    deploy that replaces only the dlls must still change the key. A fixture that stood a program in is keyed by the
    override's own text (its identity is the command line)."""
    override = overrides.get(probe)
    if override:
        return "probe-cmd:" + hashlib.sha256(override.encode("utf-8")).hexdigest()
    folder = probe_exe(harness, probe).parent
    h = hashlib.sha256()
    built = [q for q in folder.iterdir() if q.is_file()] + [q for q in (folder / "runtimes").rglob("*") if q.is_file()]
    for p in sorted((q for q in built if q.suffix.lower() != ".pdb"),
                    key=lambda q: q.relative_to(folder).as_posix().lower()):
        h.update(f"{p.relative_to(folder).as_posix().lower()}\0{sha256_file(p)}\n".encode("utf-8"))
    return "folder:" + h.hexdigest()


def load_json(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def load_manifest(cdir: Path) -> dict:
    m = cdir / "candidates.json"
    if not m.exists():
        return {"_note": "no candidates.json manifest: search outcome unknown"}
    d = load_json(m)
    return d if isinstance(d, dict) else {"_note": "candidates.json unreadable"}


def candidate_files(cdir: Path, baseline: Path | None, limit: int | None) -> tuple[Path, list[Path]]:
    files = sorted(cdir.glob("candidate-*.json"))
    if not files:
        sys.exit(f"no candidate-*.json in {cdir}")
    if baseline is None:
        base = [f for f in files if f.stem.endswith("baseline")]
        if len(base) != 1:
            sys.exit(f"{cdir}: expected exactly one candidate-*-baseline.json, found {len(base)}; pass --baseline")
        baseline = base[0]
    others = [f for f in files if f != baseline]
    if limit is not None:
        others = others[:limit]
    return baseline, others


# ---------------------------------------------------------------- emit reading (ConflictReport.cs shapes)

def row_identity(kind: str, form_key: str, other: str | None, a: str, part: int | None = None) -> str:
    """ConflictIo.RowIdentity, mirrored: the records a predicate is about, never the plugin pair; case-insensitive,
    as the C# key is since 2026-09-11 (a filename's letter case differs between providers). With a part (an EDGE-IDX
    row's target triangle, since 2026-10-24) the row is one link: the two records in their roles and the part."""
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    other = other.lower() if other is not None else None
    if other is None:
        return f"{kind}|{form_key}|{a}"
    if part is not None:
        return f"{kind}|{form_key}|{other}|{part}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def emit_rows(emit: Path) -> dict[str, str | None]:
    """identity -> Physical ('Holds' | 'Gone' | 'Unmeasured' | None) for every necessary record of an emit."""
    doc = load_json(emit) or {}
    rows: dict[str, str | None] = {}
    for c in doc.get("Conflicts", []):
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        part = alt[0].get("Triangle") if alt else None
        for r in c.get("Necessary", []):
            rows.setdefault(row_identity(c["Kind"], r["FormKey"], other, c["A"], part), r.get("Physical"))
    return rows


def emit_predicates(emit: Path) -> dict[str, str]:
    """identity -> Physical for the base emit's rows re-measured under this emit's arm (--base)."""
    doc = load_json(emit) or {}
    out: dict[str, str] = {}
    for p in doc.get("Predicates", []) or []:
        out.setdefault(row_identity(p["Kind"], p["FormKey"], p.get("Other"), p["A"], p.get("OtherTriangle")),
                       p.get("Physical", "Unmeasured"))
    return out


def read_identity(emit: Path) -> dict | None:
    doc = load_json(emit) or {}
    cfg = doc.get("Configuration") or doc.get("configuration")
    if not cfg:
        return None
    return {"fingerprint": cfg.get("Fingerprint", ""),
            "plugins": [{"path": p["Path"], "sha256": p["Sha256"]} for p in cfg.get("Plugins", [])],
            "overlays": [{"path": o["Path"], "sha256": o["Sha256"]} for o in cfg.get("Overlays", [])],
            "population": cfg.get("Population"), "state": cfg.get("State"), "limits": cfg.get("Limits", [])}


# ------------------------------------------------------------------------------------------ the cache

def emit_signature(p: Path):
    """(mtime_ns, size) of a file, None when absent: a write by the probe changes it."""
    try:
        st = p.stat()
    except OSError:
        return None
    return (st.st_mtime_ns, st.st_size)


CACHE_VERSION = 2


class Cache:
    """Request key -> what was produced, with the identity's own hashes for re-validation.

    An entry names its emit RELATIVE to the cache file's own folder and records the emit's sha256. A copied folder
    therefore resolves every entry inside the copy, and a file whose bytes are not the ones recorded is refused
    rather than trusted. Earlier caches (`--reuse-cache`, one per earlier launch when evalrun keeps a folder per
    launch) are read and never written: only this launch's own `cache.json` receives entries."""

    def __init__(self, path: Path, log: Path, prior: list[Path] | None = None):
        self.path, self.log = path, log
        self.entries = (load_json(path) or {}) if path.exists() else {}
        self.prior: list[tuple[Path, dict]] = []
        for p in prior or []:
            p = Path(p)
            doc = load_json(p) if p.exists() else None
            if not isinstance(doc, dict):
                self.note(f"PRIOR {p}: no readable cache file there (that launch recorded nothing); nothing reused from it")
                continue
            self.prior.append((p, doc))

    def save(self):
        # Atomic: a sweep stopped at the controller's deadline can die mid-save, and this cache is the
        # only thing that carries probe jobs finished before the stop into the next invocation.
        tmp = self.path.with_name(self.path.name + ".tmp")
        tmp.write_text(json.dumps(self.entries, indent=1), encoding="utf-8")
        os.replace(tmp, self.path)

    def note(self, line: str):
        print(f"   cache: {line}")
        with open(self.log, "a", encoding="utf-8") as f:
            f.write(line + "\n")

    @staticmethod
    def entry_emit(e: dict, cache_file: Path) -> tuple[Path | None, str | None]:
        """(the emit an entry names, None) when the entry can be verified at all, else (None, why not)."""
        if e.get("cacheVersion") != CACHE_VERSION or not e.get("emitSha256"):
            return None, ("the entry predates folder-relative, hashed entries, so the emit it names cannot be "
                          "verified — never reused")
        rel = str(e.get("emit") or "")
        pure = PurePosixPath(rel)
        if not rel or pure.is_absolute() or PureWindowsPath(rel).drive or PureWindowsPath(rel).is_absolute() \
                or ".." in pure.parts:
            return None, f"emit path {rel!r} is not inside the cache's own folder"
        folder = cache_file.parent.resolve()
        emit = folder.joinpath(*pure.parts)
        try:
            emit.resolve().relative_to(folder)
        except ValueError:
            return None, f"emit path {rel!r} resolves outside the cache's own folder"
        return emit, None

    def lookup(self, key: str, label: str) -> tuple[Path, Path] | None:
        """(emit, the cache file that served it) for a verified entry, else None. This launch's own entries come
        first, then each earlier cache in the order given; an entry that fails verification is named and the next
        source is tried."""
        found = False
        for cache_file, entries in [(self.path, self.entries)] + self.prior:
            e = entries.get(key)
            if e is None:
                continue
            found = True
            where = "" if cache_file == self.path else f" (earlier launch {cache_file.parent.name})"
            emit, why = self.entry_emit(e, cache_file)
            if emit is None:
                self.note(f"INVALIDATED {label}: {why}{where}")
                continue
            if not emit.is_file():
                self.note(f"INVALIDATED {label}: emit {e['emit']} is gone{where}")
                continue
            if sha256_file(emit) != str(e["emitSha256"]).lower():
                self.note(f"INVALIDATED {label}: emit {e['emit']} no longer hashes to the value recorded with it{where}")
                continue
            if not e.get("identity"):
                self.note(f"INVALIDATED {label}: the cached emit carries no configuration identity — missing identity prevents reuse{where}")
                continue
            changed = None
            for kind, items in (("plugin", e["identity"].get("plugins", [])), ("overlay", e["identity"].get("overlays", []))):
                for it in items:
                    p = Path(it["path"])
                    if not p.exists():
                        changed = f"{kind} {p} no longer exists"
                    elif sha256_file(p) != it["sha256"].lower():
                        changed = f"{kind} {p.name} content changed since the cached run"
                    if changed:
                        break
                if changed:
                    break
            if changed:
                self.note(f"INVALIDATED {label}: {changed}{where}")
                continue
            n = len(e["identity"].get("plugins", [])); m = len(e["identity"].get("overlays", []))
            self.note(f"REUSED {label}: request key {key[:12]} matched; {n} plugin and {m} overlay hashes and the emit's "
                      f"own hash re-verified on disk; emit {e['emit']}{where}")
            return emit, cache_file
        if not found:
            self.note(f"MISS {label}: no entry for request key {key[:12]}")
        return None

    def record(self, key: str, label: str, emit: Path):
        ident = read_identity(emit)
        try:
            rel = emit.resolve().relative_to(self.path.parent.resolve()).as_posix()
        except ValueError:
            self.note(f"NOT-RECORDED {label}: emit {emit} is not inside this cache's folder, so an entry could not name "
                      f"it relative to the cache")
            return
        self.entries[key] = {"cacheVersion": CACHE_VERSION, "emit": rel, "emitSha256": sha256_file(emit), "label": label,
                             "recordedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "identity": ident}
        if ident is None:
            self.note(f"RECORDED {label}: emit carries NO configuration identity — this entry can never be reused")
        else:
            self.note(f"RECORDED {label}: fingerprint {ident['fingerprint'][:12]}, {len(ident['plugins'])} plugins hashed")
        self.save()


# ------------------------------------------------------------------------------------- diff parsing

def parse_diff(text: str) -> dict:
    v = VERDICT_RE.search(text)
    counts = {k: int(n.replace(",", "")) for k, n in COUNT_RE.findall(text)}
    refusals = [ln.strip() for ln in text.splitlines() if "! REFUSED" in ln]
    delta, grab = [], False
    for ln in text.splitlines():
        if "configuration delta" in ln.lower():
            grab = True
            continue
        if grab:
            if not ln.strip() or not ln.startswith(" "):
                grab = False
                continue
            delta.append(ln.strip())
    return {"verdict": v.group(1) if v else "(no verdict line)", "counts": counts, "refusals": refusals, "delta": delta}


def created_breakdown(base_emit: Path, cand_emit: Path, reverse_emit: Path | None) -> dict:
    """Split the candidate's created rows by what the reverse measurement says about them under the baseline."""
    base_rows = emit_rows(base_emit)
    cand_rows = emit_rows(cand_emit)
    created = [i for i, ph in cand_rows.items() if i not in base_rows and ph == "Holds"]
    if reverse_emit is None or not reverse_emit.exists():
        return {"created": len(created), "newDefect": 0, "attributed": 0, "unmeasured": len(created),
                "note": "no reverse measurement: every created row is unmeasured under the baseline order"}
    rev = emit_predicates(reverse_emit)
    new, attributed, unmeasured, examples = 0, 0, 0, []
    for i in created:
        p = rev.get(i)
        if p == "Holds":
            attributed += 1
        elif p == "Gone":
            new += 1
            if len(examples) < 5:
                examples.append(i)
        else:
            unmeasured += 1
    return {"created": len(created), "newDefect": new, "attributed": attributed, "unmeasured": unmeasured, "newExamples": examples}


def classify(rows: list[dict]) -> str:
    if any(r["illegal"] for r in rows):
        return "ILLEGAL"
    if any(r["created"]["newDefect"] > 0 for r in rows):
        return "REGRESSION"
    if any(r["probeExit"] != 0 or r["probeRefused"] or r.get("emitNotWritten") or r["diff"]["verdict"] in ("DIFFERENTIAL-UNJUDGED", "(no verdict line)", "(not run)")
           or r["diff"]["refusals"] or r["diff"]["counts"].get("unmeasured", 0) > 0 or r["created"]["unmeasured"] > 0 for r in rows):
        return "UNRESOLVED"
    return "PASSES-COMPLETED-CHECKS"


# ------------------------------------------------------------------------------------------------ main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--candidates", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--probes", default=",".join(DEFAULT_PROBES))
    ap.add_argument("--instance"); ap.add_argument("--profile"); ap.add_argument("--game")
    ap.add_argument("--probe-args", default="", help="extra arguments passed to every probe, e.g. --state X")
    ap.add_argument("--harness", default=str(HERE))
    ap.add_argument("--baseline", help="the baseline CandidateFile (default: the one named *-baseline.json)")
    ap.add_argument("--max-candidates", type=int)
    ap.add_argument("--diff-tolerance", default=None, help="conflictdiff --tolerance (points)")
    ap.add_argument("--no-reverse", action="store_true", help="skip the reverse measurement (created rows then read unmeasured)")
    ap.add_argument("--no-pair-results", action="store_true",
                    help="run every probe without the placed-pair results of earlier jobs (PAIR_RESULTS): each measures every pair")
    ap.add_argument("--full-reverse", action="store_true",
                    help="measure every reverse by sweeping the baseline order again, even for a probe that can take the "
                         "baseline emit's rows (RECONCILE_REVERSE)")
    ap.add_argument("--deadline-seconds", type=float,
                    help="the caller's remaining deadline: each probe is then finished only when its whole process "
                         "tree is empty, and a probe is cached only after that (evalrun passes it under a budget)")
    ap.add_argument("--probe-cmd", action="append", default=[], metavar="NAME=<argv prefix>",
                    help="run NAME (a probe, or conflictdiff) as <argv prefix> instead of its Release binary. "
                         "Repeatable. A test seam: it changes which program runs, not how the result is read.")
    ap.add_argument("--population", metavar="FILE",
                    help="the plan's population file (P25): passed as --population to the probes named by "
                         "--population-probes, and part of their request key by its sha256")
    ap.add_argument("--population-probes", default="",
                    help="comma-separated probes that take --population; the others run on the whole order")
    ap.add_argument("--reuse-cache", action="append", default=[], metavar="FILE",
                    help="an earlier launch's cache.json to reuse verified emits from, read-only; repeatable, searched "
                         "in the order given after this --out's own cache (evalrun passes one per earlier launch)")
    a = ap.parse_args()

    cdir, out, harness = Path(a.candidates), Path(a.out), Path(a.harness)
    out.mkdir(parents=True, exist_ok=True)
    probes = [p.strip() for p in a.probes.split(",") if p.strip()]
    common = []
    for flag, val in (("--instance", a.instance), ("--profile", a.profile), ("--game", a.game)):
        if val:
            common += [flag, val]
    extra = a.probe_args.split() if a.probe_args else []
    baseline, cands = candidate_files(cdir, Path(a.baseline) if a.baseline else None, a.max_candidates)
    manifest = load_manifest(cdir)
    # Which advice/preference rules each candidate violates or satisfies, from the manifest (orderprobe writes
    # them per candidate). A hard rule is never here: an order that breaks one is never emitted as a candidate.
    rules_of = {}
    for mc in manifest.get("Candidates", []) if isinstance(manifest, dict) else []:
        stem = Path(mc.get("File", "")).stem
        rules_of[stem] = {"violates": list(mc.get("ViolatesAdvice") or []), "satisfies": list(mc.get("SatisfiesAdvice") or [])}
    cache = Cache(out / "cache.json", out / "cache-log.txt", prior=[Path(p) for p in a.reuse_cache])
    # One record per job, appended the moment the job has a result, so a launch that dies later still says what each
    # finished job produced. A reader credits a job from its record, never from a file's presence at a path. Emptied
    # first: a record is this launch's or it is nothing.
    jobs_log = out / "jobs.jsonl"
    jobs_log.write_text("", encoding="utf-8")

    def record_job(job: str, kind: str, outcome: str, exit_code, emit: Path | None, *, written: bool,
                   reused_from: Path | None = None, stopped=None, log: Path | None = None, inputs: dict | None = None,
                   runner: dict | None = None):
        rec = {"job": job, "kind": kind, "outcome": outcome, "exitCode": exit_code, "stopped": stopped,
               # what the Runner said about a stop (why, how long, whether the tree was confirmed gone), so a reader
               # names the real cause - a containment refusal is not a budget stop
               "stop": ({k: runner.get(k) for k in ("seconds", "treeStopped", "refused", "launchedExitCode")}
                        if stopped and runner else None),
               "emit": str(emit.resolve()) if emit else None, "emitWritten": bool(written),
               "emitSha256": sha256_file(emit) if emit and emit.is_file() else None,
               "reusedFrom": str(reused_from.resolve()) if reused_from else None,
               "log": str(log.resolve()) if log and log.exists() else None,
               "inputs": {k: (str(Path(v).resolve()) if v else None) for k, v in (inputs or {}).items()}}
        with open(jobs_log, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")

    overrides = {}
    for spec in a.probe_cmd:
        name, _, prefix = spec.partition("=")
        if not name.strip() or not prefix.strip():
            sys.exit(f"--probe-cmd {spec!r}: expected NAME=<argv prefix>")
        overrides[name.strip()] = prefix.strip()
    pop_probes = {p.strip() for p in a.population_probes.split(",") if p.strip()}
    if pop_probes and not a.population:
        sys.exit("--population-probes needs --population FILE")
    if a.population and not pop_probes:
        sys.exit("--population needs --population-probes naming the probes that take it")
    if pop_probes - set(probes):
        sys.exit("--population-probes names probes this sweep does not run: " + ", ".join(sorted(pop_probes - set(probes))))
    pop_file = Path(a.population).resolve() if a.population else None
    if pop_file is not None and not pop_file.is_file():
        sys.exit(f"--population {pop_file}: no such file")
    pop_sha = sha256_file(pop_file) if pop_file is not None else None
    exes = {p: probe_program(harness, p, overrides) for p in probes}
    exe_sha = {p: program_key(harness, p, overrides) for p in probes}
    diff_exe = probe_program(harness, "conflictdiff", overrides)
    logs = out / "logs"

    def key_for(probe: str, order_file: Path, base_emit: Path | None, reverse_of: Path | None = None) -> str:
        parts = [probe, exe_sha[probe], sha256_file(order_file), sha256_file(base_emit) if base_emit else "-", " ".join(common + extra)]
        if probe in pop_probes:
            # Only a scoped probe's key gains a term, so a sweep without --population keys exactly as before.
            parts.append("population:" + pop_sha)
        if reverse_of is not None:
            # A reverse measured from the baseline emit's rows is its own request: never served a full reverse's emit.
            parts.append("reverse-of:" + sha256_file(reverse_of))
        return hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest()

    # Under a caller's deadline the Runner's tree semantics apply: a probe whose launched process exits while
    # a descendant still runs is not finished, so it is never recorded in the cache - a later `resume` must not
    # reuse work whose tree was not shown empty. Without one, the historical behaviour is unchanged.
    deadline = None if a.deadline_seconds is None else time.perf_counter() + max(0.0, a.deadline_seconds)
    runner = Runner("ordersweep", out_dir=logs, retries=0, deadline=deadline)

    def run_probe(job_id: str, probe: str, order_file: Path, base_emit: Path | None, emit: Path,
                  reverse_of: Path | None = None) -> tuple[int, bool, bool, Path, bool]:
        """Run (or reuse) one probe arm. Returns (exit, refused, illegal, emit path, measured).

        `measured` is True only for an emit this launch wrote or a cache entry it verified. A zero exit over a file
        this launch did not write is not a measurement: nothing downstream (reverse, diff, classification) is built
        on it, and it is never cached."""
        k = key_for(probe, order_file, base_emit, reverse_of)
        hit = cache.lookup(k, job_id)
        inputs = {"order": order_file, "base": base_emit}
        if reverse_of is not None:
            inputs["baselineEmit"] = reverse_of
        if hit:
            reused, src = hit
            record_job(job_id, "probe", "reused", 0, reused, written=False, reused_from=src, inputs=inputs)
            return 0, False, False, reused, True
        argv = exes[probe] + ["--order", str(order_file)] + (["--base", str(base_emit)] if base_emit else []) + ["--emit", str(emit)] + common + extra
        if reverse_of is not None:
            argv += [RECONCILE_REVERSE[probe], str(reverse_of)]
        if probe in PAIR_RESULTS and not a.no_pair_results:
            mine = out / probe / f"{job_id}{PAIR_RESULTS_SUFFIX}"
            earlier = [f for d in [out] + [Path(c).resolve().parent for c in a.reuse_cache]
                       for f in sorted((d / probe).glob("*" + PAIR_RESULTS_SUFFIX)) if f.resolve() != mine.resolve()]
            for f in earlier:
                argv += ["--pair-cache-read", str(f)]
            argv += ["--pair-cache-write", str(mine)]
        if probe in pop_probes:
            argv += ["--population", str(pop_file)]
        # An earlier run's emit may already be at this path when --out is reused by hand (evalrun gives every launch
        # a fresh folder): only an emit THIS launch wrote is recorded or measured.
        before = emit_signature(emit)
        res = runner.run([{"id": job_id, "argv": argv}])[0]
        log = logs / f"{job_id}.txt"
        text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        refused = bool(REFUSED_RE.search(text))
        illegal = res["exitCode"] == 2 and "REFUSED" in text and not emit.exists()
        after = emit_signature(emit)
        written = after is not None and after != before
        if res.get("stopped"):
            # stopped at (or refused by) the caller's deadline: not finished, never cached, never a clean exit
            record_job(job_id, "probe", "stopped", res["exitCode"], emit, written=written, stopped=res["stopped"],
                       log=log, inputs=inputs, runner=res)
            return (res["exitCode"] if res["exitCode"] not in (None, 0) else -1), refused, False, emit, False
        if res["exitCode"] == 0 and emit.exists():
            if written:
                cache.record(k, job_id, emit)
            else:
                cache.note(f"NOT-RECORDED {job_id}: exited 0 but did not write {emit.name}; the file at that path "
                           f"is an earlier run's and is neither cached nor measured as this request's result")
        record_job(job_id, "probe", "ran", res["exitCode"], emit, written=written, log=log, inputs=inputs)
        return res["exitCode"], refused, illegal, emit, res["exitCode"] == 0 and written

    results: dict[str, list[dict]] = {}
    base_emits: dict[str, Path] = {}
    with Run("ordersweep-plan", stage_source="counted") as plan:
        plan.feature("candidates", len(cands), "candidates"); plan.feature("probes", len(probes), "probes")
        plan.stage("baseline", total=len(probes), unit="probes")
        for p in probes:
            emit = out / p / "baseline.json"; emit.parent.mkdir(parents=True, exist_ok=True)
            code, _, _, got, measured = run_probe(f"{p}-baseline", p, baseline, None, emit)
            if measured:
                base_emits[p] = got
            else:
                print(f"   !! {p} baseline exited {code}" + ("" if code != 0 else " without writing its emit")
                      + " — its candidates read UNRESOLVED")
            plan.advance()

        arms = 3 if not a.no_reverse else 2
        plan.stage("candidates", total=len(cands) * len(probes) * arms, unit="probe-arms")
        for cand in cands:
            rows = []
            for p in probes:
                row = {"probe": p, "candidate": cand.stem, "probeExit": None, "probeRefused": False, "illegal": False,
                       "diff": {"verdict": "(not run)", "counts": {}, "refusals": [], "delta": []},
                       "created": {"created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}}
                base_emit = base_emits.get(p)
                if base_emit is None:
                    row["probeExit"] = -1; rows.append(row); plan.advance(arms); continue
                emit = out / p / f"{cand.stem}.json"
                code, refused, illegal, got, measured = run_probe(f"{p}-{cand.stem}", p, cand, base_emit, emit)
                row.update(probeExit=code, probeRefused=refused, illegal=illegal, emit=str(got))
                if code == 0 and not measured:
                    row["emitNotWritten"] = True
                plan.advance()
                if measured:
                    reverse = None
                    if not a.no_reverse:
                        rev_emit = out / p / f"{cand.stem}.reverse.json"
                        rof = base_emit if p in RECONCILE_REVERSE and not a.full_reverse else None
                        rcode, _, _, rgot, rmeasured = run_probe(f"{p}-{cand.stem}-reverse", p, baseline, got, rev_emit, rof)
                        reverse = rgot if rmeasured else None
                        row["reverseExit"] = rcode
                        row["reverseEmit"] = str(rgot)
                        if rcode == 0 and not rmeasured:
                            row["reverseEmitNotWritten"] = True
                        plan.advance()
                    row["created"] = created_breakdown(base_emit, got, reverse)
                    dargs = diff_exe + [str(base_emit), str(got)] + (["--tolerance", a.diff_tolerance] if a.diff_tolerance else [])
                    djob = f"diff-{p}-{cand.stem}"
                    dlog = logs / f"{djob}.txt"
                    dbefore = emit_signature(dlog)
                    dres = runner.run([{"id": djob, "argv": dargs}])[0]
                    dafter = emit_signature(dlog)
                    row["diff"] = parse_diff(dlog.read_text(encoding="utf-8", errors="replace") if dlog.exists() else "")
                    row["diffExit"] = dres["exitCode"]
                    # The diff's own record names every emit its result was read from: conflictdiff compared base and
                    # arm, and the created rows were split by the reverse emit (none when it was not measured).
                    record_job(djob, "diff", "stopped" if dres.get("stopped") else "ran", dres["exitCode"], dlog,
                               written=dafter is not None and dafter != dbefore, stopped=dres.get("stopped"),
                               log=dlog, inputs={"base": base_emit, "arm": got, "reverse": reverse}, runner=dres)
                    plan.advance()
                else:
                    plan.advance(arms - 1)
                rows.append(row)
            results[cand.stem] = rows

    # ------------------------------------------------------------------------------------ the report
    summary = {"baseline": str(baseline), "probes": probes, "candidates": {}, "manifest": manifest,
               **({"population": {"file": str(pop_file), "sha256": pop_sha, "probes": sorted(pop_probes)}} if pop_probes else {})}
    lines = ["# ordersweep report", "",
             f"baseline: `{baseline.name}`; probes: {', '.join(probes)}; candidates evaluated: {len(cands)}; "
             f"reverse measurement: {'off' if a.no_reverse else 'on'}"
             + (f"; population {pop_sha[:12]} for {', '.join(sorted(pop_probes))}, the other probes on the whole order"
                if pop_probes else ""), ""]
    lines += ["| candidate | changes | " + " | ".join(probes) + " | rules | classification |", "|---|---|" + "---|" * len(probes) + "---|---|"]
    ranking = []
    for cand in cands:
        rows = results[cand.stem]
        cls = classify(rows)
        cf = load_json(cand) or {}
        changes = "; ".join(cf.get("Changes") or cf.get("changes") or []) or "(none listed)"
        cells = []
        for r in rows:
            c, cr = r["diff"]["counts"], r["created"]
            cells.append(f"{r['diff']['verdict'].replace('DIFFERENTIAL-', '')} r{c.get('repaired', 0)} c{c.get('created', 0)}"
                         f"(new {cr['newDefect']}/attr {cr['attributed']}/unm {cr['unmeasured']}) q{c.get('reclassified', 0)} u{c.get('unmeasured', 0)}"
                         + (" REFUSED" if r["probeRefused"] else "") + (f" exit {r['probeExit']}" if r["probeExit"] not in (0, None) else ""))
        ru = rules_of.get(cand.stem, {"violates": [], "satisfies": []})
        rules_cell = ("violates " + "; ".join(v.split(" — ")[0].split(" — ")[0] for v in ru["violates"])) if ru["violates"] else ("satisfies " + str(len(ru["satisfies"])) if ru["satisfies"] else "—")
        lines.append(f"| {cand.stem} | {changes} | " + " | ".join(cells) + f" | {rules_cell} | **{cls}** |")
        summary["candidates"][cand.stem] = {"classification": cls, "changes": changes, "rows": rows, "rules": ru}
        new_defects = sum(r["created"]["newDefect"] for r in rows)
        repaired = sum(r["diff"]["counts"].get("repaired", 0) for r in rows)
        displaced = len(cf.get("Changes") or cf.get("changes") or [])
        # Rank: class, then new defects, then rules violated (a candidate that breaks stated advice sorts below one that
        # keeps it — preferences stay distinct from hard rules, but they are not ignored), then repairs, then displacement.
        ranking.append((0 if cls == "PASSES-COMPLETED-CHECKS" else 1 if cls == "UNRESOLVED" else 2, new_defects, len(ru["violates"]), -repaired, displaced, cand.stem))
    lines += ["", "Cells read: conflictdiff verdict; r = repaired (physical predicate Holds -> Gone); c = created (entered with the "
              "predicate holding), split by the reverse measurement into NEW physical defects (a regression), ATTRIBUTED "
              "(the same physical state under the baseline order — the class's history-dependent attribution, not an engine "
              "difference) and UNMEASURED; q = reclassified (changed hands, defect intact); u = unmeasured.", ""]
    for cand in cands:
        deltas = sorted({d for r in results[cand.stem] for d in r["diff"]["delta"]})
        if deltas:
            lines.append(f"- `{cand.stem}` configuration delta vs baseline: " + "; ".join(deltas))
        ex = [e for r in results[cand.stem] for e in r["created"].get("newExamples", [])]
        if ex:
            lines.append(f"  - new physical defects, first rows: " + "; ".join(f"`{e}`" for e in ex[:5]))
    lines += ["", "## What was established", ""]
    passing = [c for c in cands if summary["candidates"][c.stem]["classification"] == "PASSES-COMPLETED-CHECKS"]
    if passing:
        lines.append(f"- {len(passing)} candidate(s) pass the completed checks within this scope: " + ", ".join(f"`{c.stem}`" for c in passing) + ".")
        lines.append("  Passing the completed checks is a statement about the probes named above, in the population and state they ran in; it is not a compatibility claim beyond them.")
    else:
        lines.append(f"- No candidate passes the completed checks within this budget. **{NOT_PROOF}**")
    ranking.sort()
    # The baseline is the reference every candidate is measured against, so "passing" means "no new physical defect
    # against the user's own order", not "better than it". A candidate is worth changing to only when it REPAIRS
    # something the completed checks flag in the baseline and creates no new defect; otherwise the baseline stands.
    improving = [r for r in ranking if r[0] == 0 and -r[3] > 0]
    if improving:
        best = improving[0]
        viol = summary["candidates"][best[5]]["rules"]["violates"]
        lines.append(f"- What the user would change first: `{best[5]}` — {summary['candidates'][best[5]]['changes']}: repairs {-best[3]} row(s) of the baseline's completed checks and creates no new physical defect"
                     + (f"; **but it violates {len(viol)} stated rule(s)**: " + " | ".join(viol) + " — the user's call, not the tool's." if viol else "."))
    elif passing:
        lines.append("- No passing candidate repairs anything the completed checks flag in the baseline: the passing candidates are alternatives that do not regress, and the user's own order stands.")
    lines += ["", "## Search outcome (from the manifest)", ""]
    if "_note" in manifest:
        lines.append(f"- {manifest['_note']}")
    else:
        for k, v in manifest.items():
            if k.lower() in ("budget", "contradictions", "limits", "unsatisfiableadvice", "search", "outcome", "result"):
                lines.append(f"- {k}: {json.dumps(v)[:600]}")
    lines += ["", f"Cache decisions: `{cache.log.name}`; per-run logs: `logs/`; the RunLog history carries every child's timing."]
    (out / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (out / "summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
