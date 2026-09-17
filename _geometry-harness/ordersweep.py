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
never reused — a missing identity prevents reuse, it does not permit it. Every decision is printed and
written to `cache-log.txt` (REUSED / MISS / INVALIDATED <reason>), so reuse and invalidation are
observable rather than assumed.

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
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run, Runner  # noqa: E402

DEFAULT_PROBES = ["navownprobe", "overlapprobe", "contactprobe"]
VERDICT_RE = re.compile(r"verdict\s*:\s*(DIFFERENTIAL-[A-Z]+)")
COUNT_RE = re.compile(r"^\s*(repaired|reclassified|created|unmeasured|unchanged)\s*:\s*([\d,]+)", re.M)
REFUSED_RE = re.compile(r"REFUSED|MISSING MASTER", re.I)
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


def program_key(harness: Path, probe: str, overrides: dict[str, str]) -> str:
    """What the cache must key a program by: the binary's hash, or the override's own text when a
    fixture stood a program in (a fake probe's identity is the command line, not a Release build)."""
    override = overrides.get(probe)
    if override:
        return "probe-cmd:" + hashlib.sha256(override.encode("utf-8")).hexdigest()
    return sha256_file(probe_exe(harness, probe))


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

def row_identity(kind: str, form_key: str, other: str | None, a: str) -> str:
    """ConflictIo.RowIdentity, mirrored: the records a predicate is about, never the plugin pair; case-insensitive,
    as the C# key is since 2026-09-11 (a filename's letter case differs between providers)."""
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    other = other.lower() if other is not None else None
    if other is None:
        return f"{kind}|{form_key}|{a}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def emit_rows(emit: Path) -> dict[str, str | None]:
    """identity -> Physical ('Holds' | 'Gone' | 'Unmeasured' | None) for every necessary record of an emit."""
    doc = load_json(emit) or {}
    rows: dict[str, str | None] = {}
    for c in doc.get("Conflicts", []):
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        for r in c.get("Necessary", []):
            rows.setdefault(row_identity(c["Kind"], r["FormKey"], other, c["A"]), r.get("Physical"))
    return rows


def emit_predicates(emit: Path) -> dict[str, str]:
    """identity -> Physical for the base emit's rows re-measured under this emit's arm (--base)."""
    doc = load_json(emit) or {}
    out: dict[str, str] = {}
    for p in doc.get("Predicates", []) or []:
        out.setdefault(row_identity(p["Kind"], p["FormKey"], p.get("Other"), p["A"]), p.get("Physical", "Unmeasured"))
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

class Cache:
    """Request key -> what was produced, with the identity's own hashes for re-validation."""

    def __init__(self, path: Path, log: Path):
        self.path, self.log = path, log
        self.entries = load_json(path) or {} if path.exists() else {}

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

    def lookup(self, key: str, label: str) -> Path | None:
        e = self.entries.get(key)
        if e is None:
            self.note(f"MISS {label}: no entry for request key {key[:12]}")
            return None
        emit = Path(e["emit"])
        if not emit.exists():
            self.note(f"INVALIDATED {label}: emit {emit} is gone")
            return None
        if not e.get("identity"):
            self.note(f"INVALIDATED {label}: the cached emit carries no configuration identity — missing identity prevents reuse")
            return None
        for kind, items in (("plugin", e["identity"].get("plugins", [])), ("overlay", e["identity"].get("overlays", []))):
            for it in items:
                p = Path(it["path"])
                if not p.exists():
                    self.note(f"INVALIDATED {label}: {kind} {p} no longer exists")
                    return None
                if sha256_file(p) != it["sha256"].lower():
                    self.note(f"INVALIDATED {label}: {kind} {p.name} content changed since the cached run")
                    return None
        n = len(e["identity"].get("plugins", [])); m = len(e["identity"].get("overlays", []))
        self.note(f"REUSED {label}: request key {key[:12]} matched; {n} plugin and {m} overlay hashes re-verified on disk; emit {emit.name}")
        return emit

    def record(self, key: str, label: str, emit: Path):
        ident = read_identity(emit)
        self.entries[key] = {"emit": str(emit), "label": label,
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
    if any(r["probeExit"] != 0 or r["probeRefused"] or r["diff"]["verdict"] in ("DIFFERENTIAL-UNJUDGED", "(no verdict line)", "(not run)")
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
    ap.add_argument("--deadline-seconds", type=float,
                    help="the caller's remaining deadline: each probe is then finished only when its whole process "
                         "tree is empty, and a probe is cached only after that (evalrun passes it under a budget)")
    ap.add_argument("--probe-cmd", action="append", default=[], metavar="NAME=<argv prefix>",
                    help="run NAME (a probe, or conflictdiff) as <argv prefix> instead of its Release binary. "
                         "Repeatable. A test seam: it changes which program runs, not how the result is read.")
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
    cache = Cache(out / "cache.json", out / "cache-log.txt")
    overrides = {}
    for spec in a.probe_cmd:
        name, _, prefix = spec.partition("=")
        if not name.strip() or not prefix.strip():
            sys.exit(f"--probe-cmd {spec!r}: expected NAME=<argv prefix>")
        overrides[name.strip()] = prefix.strip()
    exes = {p: probe_program(harness, p, overrides) for p in probes}
    exe_sha = {p: program_key(harness, p, overrides) for p in probes}
    diff_exe = probe_program(harness, "conflictdiff", overrides)
    logs = out / "logs"

    def key_for(probe: str, order_file: Path, base_emit: Path | None) -> str:
        parts = [probe, exe_sha[probe], sha256_file(order_file), sha256_file(base_emit) if base_emit else "-", " ".join(common + extra)]
        return hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest()

    # Under a caller's deadline the Runner's tree semantics apply: a probe whose launched process exits while
    # a descendant still runs is not finished, so it is never recorded in the cache - a later `resume` must not
    # reuse work whose tree was not shown empty. Without one, the historical behaviour is unchanged.
    deadline = None if a.deadline_seconds is None else time.perf_counter() + max(0.0, a.deadline_seconds)
    runner = Runner("ordersweep", out_dir=logs, retries=0, deadline=deadline)

    def run_probe(job_id: str, probe: str, order_file: Path, base_emit: Path | None, emit: Path) -> tuple[int, bool, bool, Path]:
        """Run (or reuse) one probe arm. Returns (exit, refused, illegal, emit path)."""
        k = key_for(probe, order_file, base_emit)
        reused = cache.lookup(k, job_id)
        if reused:
            return 0, False, False, reused
        argv = exes[probe] + ["--order", str(order_file)] + (["--base", str(base_emit)] if base_emit else []) + ["--emit", str(emit)] + common + extra
        res = runner.run([{"id": job_id, "argv": argv}])[0]
        log = logs / f"{job_id}.txt"
        text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        refused = bool(REFUSED_RE.search(text))
        illegal = res["exitCode"] == 2 and "REFUSED" in text and not emit.exists()
        if res.get("stopped"):
            # stopped at (or refused by) the caller's deadline: not finished, never cached, never a clean exit
            return (res["exitCode"] if res["exitCode"] not in (None, 0) else -1), refused, False, emit
        if res["exitCode"] == 0 and emit.exists():
            cache.record(k, job_id, emit)
        return res["exitCode"], refused, illegal, emit

    results: dict[str, list[dict]] = {}
    base_emits: dict[str, Path] = {}
    with Run("ordersweep-plan", stage_source="counted") as plan:
        plan.feature("candidates", len(cands), "candidates"); plan.feature("probes", len(probes), "probes")
        plan.stage("baseline", total=len(probes), unit="probes")
        for p in probes:
            emit = out / p / "baseline.json"; emit.parent.mkdir(parents=True, exist_ok=True)
            code, _, _, got = run_probe(f"{p}-baseline", p, baseline, None, emit)
            if code == 0 and got.exists():
                base_emits[p] = got
            else:
                print(f"   !! {p} baseline exited {code} — its candidates read UNRESOLVED")
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
                code, refused, illegal, got = run_probe(f"{p}-{cand.stem}", p, cand, base_emit, emit)
                row.update(probeExit=code, probeRefused=refused, illegal=illegal, emit=str(got))
                plan.advance()
                if code == 0 and got.exists():
                    reverse = None
                    if not a.no_reverse:
                        rev_emit = out / p / f"{cand.stem}.reverse.json"
                        rcode, _, _, rgot = run_probe(f"{p}-{cand.stem}-reverse", p, baseline, got, rev_emit)
                        reverse = rgot if rcode == 0 and rgot.exists() else None
                        row["reverseExit"] = rcode
                        plan.advance()
                    row["created"] = created_breakdown(base_emit, got, reverse)
                    dargs = diff_exe + [str(base_emit), str(got)] + (["--tolerance", a.diff_tolerance] if a.diff_tolerance else [])
                    dres = runner.run([{"id": f"diff-{p}-{cand.stem}", "argv": dargs}])[0]
                    dlog = logs / f"diff-{p}-{cand.stem}.txt"
                    row["diff"] = parse_diff(dlog.read_text(encoding="utf-8", errors="replace") if dlog.exists() else "")
                    row["diffExit"] = dres["exitCode"]
                    plan.advance()
                else:
                    plan.advance(arms - 1)
                rows.append(row)
            results[cand.stem] = rows

    # ------------------------------------------------------------------------------------ the report
    summary = {"baseline": str(baseline), "probes": probes, "candidates": {}, "manifest": manifest}
    lines = ["# ordersweep report", "",
             f"baseline: `{baseline.name}`; probes: {', '.join(probes)}; candidates evaluated: {len(cands)}; "
             f"reverse measurement: {'off' if a.no_reverse else 'on'}", ""]
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
