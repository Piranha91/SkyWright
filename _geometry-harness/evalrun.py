"""evalrun - what stays in Python of the evaluation controller: checking a bundle, and enriching one.

    python evalrun.py verify-bundle DIR [--out FILE]
    python evalrun.py enrich --bundle DIR --kind record-rows|names|order-variant|order-geometry [...]

What this is for. An evaluation is planned, run, finalized and bundled by the built-in controller, runcontrol
(_geometry-harness/runcontrol; `runcontrol plan|run|resume|import-results|finalize|bundle|population`), which owns those
rules. Two commands stay here until they are ported: `enrich`, which adds evidence derived after measurement to a bundle
(record rows, names, the reversed order's record view, an order sweep's geometry), and `verify-bundle`, which recomputes
every hash from the bundle-relative paths and which enrich runs before and after it writes (evalreport runs it too).
`configuration_fingerprint` is the one Python spelling of the configuration identity (the text of
`_shared/Configuration.cs`'s ConfigurationIdentity.Compute) for the Python tools that need it.
`standards/HOUSECARL_EVALUATION_BUNDLE.md` is the contract.

Exit codes: 0 ok; 2 usage or a refused input; 3 a verify-bundle refusal.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

sys.path.insert(0, str(HERE))
import toolidentity  # noqa: E402

#: Schema 2 is the evidence-chain schema: the plan freezes a per-arm REQUESTED identity, receipts record
#: their comparison inputs, and finalize/bundle/verify walk the receipt-to-evidence chain. A schema-1
#: artifact carries none of that, so it is refused BY NAME rather than certified by a reader that would
#: silently find nothing to check.
SCHEMA_VERSION = 2
BUNDLE_SCHEMA = 2
#: The tool identity contract a plan's `toolIdentity` follows (toolidentity.py's rule). A plan without it is refused by
#: name where it would run or import (`identity_contract_refusal`); `finalize`, `bundle` and `verify-bundle` read it under
#: the legacy guarantee, labelled as such.
TOOL_IDENTITY_CONTRACT = 1

# Exit codes, spelled once.
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3


TERMINAL_OK = ("completed", "reused")


# ------------------------------------------------------------------------------- small helpers --

def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


#: The configuration schema the fingerprint text is keyed on (`ConfigurationIdentity.CurrentSchema`).
CONFIG_SCHEMA = 1


def configuration_fingerprint(plugins, providers, overlays, population, state, schema=None) -> str:
    """`ConfigurationIdentity.Compute`'s canonical text, line for line.

    The one fingerprint rule lives in `_geometry-harness/_shared/Configuration.cs`
    (`ConfigurationIdentity.Compute`); this function is its only Python spelling in this controller, so
    a second convention cannot be invented here. The text is: `schema:<n>`, `population:<p>`,
    `state:<s>`, then one `plugin:<lowercase name>:<sha256>` per plugin IN ORDER, one
    `provider:<kind>:<lowercase name>` per provider, one `overlay:<kind>:<sha256>` per overlay; every
    line newline-terminated; the digest is sha256 hex, lower case. Paths are NOT in the text — names
    and content hashes are."""
    sb = [f"schema:{CONFIG_SCHEMA if schema is None else schema}\n",
          f"population:{population}\n", f"state:{state}\n"]
    for p in plugins or []:
        sb.append(f"plugin:{str(p.get('name') or '').lower()}:{p.get('sha256') or ''}\n")
    for v in providers or []:
        sb.append(f"provider:{v.get('kind') or ''}:{str(v.get('name') or '').lower()}\n")
    for o in overlays or []:
        sb.append(f"overlay:{o.get('kind') or ''}:{o.get('sha256') or ''}\n")
    return hashlib.sha256("".join(sb).encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------------- the plan's population (P25)


class HashMemo:
    """One hash per (resolved path, size, mtime) per invocation: re-checking 80 plugins across 60
    receipts reads each file once, not sixty times."""

    def __init__(self):
        self.cache: dict = {}

    def sha256(self, p) -> str | None:
        p = Path(p)
        try:
            st = p.stat()
            key = (str(p.resolve()), st.st_size, st.st_mtime_ns)
        except OSError:
            return None
        if key not in self.cache:
            self.cache[key] = sha256_file(p)
        return self.cache[key]


def canonical(obj) -> str:
    """One spelling of a JSON value, so a hash over it is stable across machines."""
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def load_json(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_json(p: Path, obj) -> None:
    """Atomic by construction: a killed process never leaves a half-written ledger."""
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(p.suffix + ".tmp")
    tmp.write_text(json.dumps(obj, indent=1, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, p)


def refuse(message: str) -> int:
    """A refused input says what was wrong and exits 2 — never a degraded run."""
    print(f"REFUSED: {message}", file=sys.stderr)
    return EXIT_REFUSED_INPUT


def exe_name(name: str) -> str:
    return f"{name}.exe" if os.name == "nt" else name


# ------------------------------------------------------------------------- program identity --
#
# The identity of every program a plan can launch is frozen into `plan.toolIdentity` (toolidentity.py's one rule) and
# compared with the disk before `run` / `resume` / `import-results` write anything, again when each child launches and
# before its result is accepted. Every accepted receipt binds the identity it was accepted under, per role, and
# finalize / bundle / verify-bundle compare those bindings with the plan offline.

#: Adapter kinds whose check launches its probe as a child.
LAUNCH_KINDS = {"scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target", "adviceprobe", "patchprobe"}
CONTROLLER = "controller"
COMPARATOR = "helper:conflictdiff"


def program_id(exe: str) -> str:
    return COMPARATOR if exe == "conflictdiff" else f"probe:{exe}"


def receipt_roles(receipt: dict) -> dict:
    """role -> program a receipt's result rests on: a probe child's emitter, a swept diff's comparator, and always the
    controller that decided the result. An import names no local emitter: its declared tool is not a local apphost."""
    ad = receipt.get("adapter") or {}
    if ad.get("kind") == "ordersweep-probe" and receipt.get("part") == "diff":
        return {"comparator": COMPARATOR, "controller": CONTROLLER}
    if ad.get("kind") in LAUNCH_KINDS and ad.get("exe"):
        return {"emitter": program_id(ad["exe"]), "controller": CONTROLLER}
    return {"controller": CONTROLLER}


def identity_contract_refusal(plan: dict) -> str | None:
    """A plan without the tool identity contract cannot run, resume or import under it: what it would launch cannot be
    compared with what it was planned with. Its old results still finalize and verify, labelled legacy."""
    ti = plan.get("toolIdentity")
    if isinstance(ti, dict) and ti.get("contract") == TOOL_IDENTITY_CONTRACT and isinstance(ti.get("programs"), dict):
        return None
    return (f"plan-identity-outdated: plan.json records no tool identity (toolIdentity contract "
            f"{TOOL_IDENTITY_CONTRACT}), so the programs this would launch cannot be compared with the ones it was "
            f"planned with. Make a new plan in a new directory.")


def identity_defects(plan: dict, receipt: dict) -> list[tuple]:
    """Offline, from retained metadata only: (role, program, kind, recorded, found) for every role of an accepted receipt
    whose bound identity is not the plan's. A role with no digest is `tool-identity-missing`; two absent digests are
    never equal. A plan without the contract has nothing to compare (the legacy guarantee)."""
    if identity_contract_refusal(plan):
        return []
    progs = plan["toolIdentity"]["programs"]
    bound = (receipt.get("consumed") or {}).get("programs")
    out = []
    for role, pid in receipt_roles(receipt).items():
        want = progs.get(pid) if isinstance(progs.get(pid), dict) else {}
        wd = want.get("digest")
        row = bound.get(role) if isinstance(bound, dict) else None
        got = row.get("digest") if isinstance(row, dict) else None
        if not isinstance(got, str) or not got:
            out.append((role, pid, "tool-identity-missing", wd, got))
        elif not isinstance(wd, str) or not wd or want.get("state") != "present" or got != wd \
                or row.get("program") != pid:
            out.append((role, pid, "tool-identity-mismatch", wd, got))
    return out


def identity_guarantee(plan: dict) -> dict:
    if identity_contract_refusal(plan):
        return {"guarantee": "legacy",
                "detail": "this plan predates tool identity: which build produced each result was not checked"}
    return {"guarantee": "verified",
            "detail": "every accepted local result is bound, per role, to the program identity this plan froze; "
                      "evaluation.integrity lists any receipt that is not"}


# ------------------------------------------------------------------------- registry adapter --
#
# Everything evalrun asks of evalregistry goes through this class, so the module's function names
# are coupled in ONE place. (Agent A owns evalregistry.py; this adapter accepts either the plain
# names or a `*_checks`-suffixed spelling, and refuses by name when neither is there.)


# ---------------------------------------------------------------------------------- the plan --

def arm_id(kind: str, stem: str, index: int) -> str:
    """Short, stable, and a path a receipt filename can live inside on Windows. The full candidate
    file name stays in the arm record; only the id is shortened."""
    return "baseline" if kind == "baseline" else stem


# -------------------------------------------------------------------------- run store (D9) --


#: What a missing or changed premise costs, in the refusal's words.
PREMISE_WORDS = {"registry": ("the checklist this run was derived from cannot be re-read",
                              "A checklist cannot change under a frozen plan."),
                 "scope": ("the applicability this plan froze cannot be re-derived",
                           "A scope change changes the checklist."),
                 "search": ("the candidate search this plan read cannot be re-read",
                            "A candidate search cannot change under a frozen plan.")}


def premise_file_refusal(plan: dict, label: str, nxt: str, named: str | None = None) -> str | None:
    """The one hash check of a frozen premise (`registry` or `scope`): the plan's stored file, or a file named on the
    command line in its place (`--registry` / `--scope`), must exist and hash to what the plan froze."""
    want = str((plan.get(label) or {}).get("sha256"))
    p = Path(named if named is not None else plan[label]["path"])
    lost, changed = PREMISE_WORDS[label]
    if not p.is_file():
        if named is not None:
            return f"{label}-missing: --{label} {p} does not exist. {nxt}"
        return f"{label}-missing: the plan's {label} {p} does not exist, so {lost}. {nxt}"
    got = sha256_file(p)
    if got != want:
        if named is not None:
            return f"{label}-mismatch: --{label} {p} hashes {str(got)[:12]}, the plan froze {want[:12]}. {nxt}"
        return f"{label}-changed: {p} now hashes {str(got)[:12]}, the plan recorded {want[:12]}. {changed} {nxt}"
    return None


#: Where bundle copies each premise.
PREMISE_COPIES = {"registry": "registry/check-registry.json", "scope": "scope/scope.json",
                  "search": "search/candidates.json"}


# ------------------------------------------------------------------- ledger and journal (D5) --

class Ledger:
    def __init__(self, run: Path, plan: dict):
        self.run = Path(run)
        self.plan = plan
        self.path = self.run / "ledger.json"
        self.journal = self.run / "journal.jsonl"
        self.doc = load_json(self.path)
        self.run_id = None

    @staticmethod
    def key(check_id: str, arm_id: str, part: str | None) -> str:
        return f"{check_id}__{arm_id}" + (f"__{part}" if part else "")

    def refusal(self) -> str | None:
        if self.doc is None:
            return (f"{self.path} is missing or corrupt. It is the run's record of what happened; "
                    f"rebuild it from the journal with `resume --rebuild-ledger` rather than guessing.")
        if self.doc.get("planId") != self.plan["planId"]:
            return (f"foreign-run: {self.path} belongs to plan {self.doc.get('planId')}, "
                    f"not {self.plan['planId']}")
        return None

    def rebuild(self) -> None:
        """The journal is the recovery source: the ledger is a view of it PLUS the receipts.

        The journal line records the transition (from, to, cause, receiptId); it has never carried the
        per-entry facts a transition also writes — `detector`, `validation`, and an import's `imported` /
        `importOrigin` block. Those live in the receipt, which is why this is a view of both. Replaying the
        journal alone dropped them, and `finalize` reads `detector` from the LEDGER while reading everything
        else from the receipt, so every rebuilt entry then finalized as `detector-undetermined` — a check
        with accepted evidence on disk reported as missing (Piranha, 2026-09-17). A fact is restored only
        from the receipt this entry actually names, so a stale or foreign receipt file cannot donate one."""
        self.doc = {"schemaVersion": SCHEMA_VERSION, "planId": self.plan["planId"],
                    "registrySha256": self.plan["registry"]["sha256"], "runIds": [], "entries": {}}
        if self.journal.exists():
            for line in self.journal.read_text(encoding="utf-8", errors="replace").splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                k = self.key(ev.get("checkId", "?"), ev.get("armId", "?"), ev.get("part"))
                self.doc["entries"][k] = {
                    "checkId": ev.get("checkId"), "armId": ev.get("armId"), "part": ev.get("part"),
                    "status": ev.get("to"), "cause": ev.get("cause"),
                    "detail": ev.get("detail"), "nextAction": ev.get("nextAction"),
                    "receiptId": ev.get("receiptId"), "runId": ev.get("runId"),
                    "receiptPath": f"receipts/{k}.json", "updatedUtc": ev.get("t")}
        for k, e in self.doc["entries"].items():
            e.update(self.receipt_facts(k, e))
        self.save()

    def receipt_facts(self, key: str, entry: dict) -> dict:
        """The per-entry facts the journal does not carry, read from the receipt this entry NAMES.

        Refused unless the receipt on disk is this plan's, this (check, arm, part)'s, and the very
        `receiptId` the journal recorded — a rebuild recovers what was written, it never adopts whatever
        file happens to sit at the path. An entry that names no receipt, or whose receipt does not match,
        gets nothing back and keeps the journal's own account of itself."""
        rid = entry.get("receiptId")
        if not rid:
            return {}
        r = load_json(Path(self.run) / "receipts" / f"{key}.json")
        if not isinstance(r, dict) or r.get("receiptId") != rid:
            return {}
        if (r.get("planId") != self.plan["planId"] or r.get("checkId") != entry.get("checkId")
                or r.get("armId") != entry.get("armId") or r.get("part") != entry.get("part")):
            return {}
        out: dict = {}
        detector = (r.get("result") or {}).get("detector")
        if detector is not None:
            out["detector"] = detector
            out["result"] = {"detector": detector, "execution": entry.get("status")}
        if isinstance(r.get("validation"), dict):
            out["validation"] = {"accepted": bool(r["validation"].get("accepted")),
                                 "rejections": r["validation"].get("rejections") or []}
        if isinstance(r.get("imported"), dict):
            out["imported"] = True
            out["importOrigin"] = r["imported"].get("origin")
        return out

    def open_invocation(self, command: str, run_id: str | None, parent: str | None, budget_minutes=None) -> None:
        self.run_id = run_id
        self.doc.setdefault("runIds", []).append(
            {"runId": run_id, "parentRunId": parent, "command": command, "startedUtc": utcnow(),
             **({"budgetMinutes": budget_minutes} if budget_minutes is not None else {})})
        self.save()

    def entry(self, check_id: str, arm_id: str, part: str | None) -> dict | None:
        return self.doc["entries"].get(self.key(check_id, arm_id, part))

    def transition(self, check_id: str, arm_id: str, part: str | None, to: str, *,
                   cause: str | None = None, receipt_id: str | None = None,
                   detector: str | None = None, extra: dict | None = None,
                   rejections: list | None = None, detail: str | None = None,
                   next_action: str | None = None) -> None:
        k = self.key(check_id, arm_id, part)
        prev = self.doc["entries"].get(k)
        frm = prev.get("status") if prev else None
        rec = {"checkId": check_id, "armId": arm_id, "part": part, "status": to,
               "cause": cause, "receiptId": receipt_id, "detector": detector,
               "detail": detail, "nextAction": next_action,
               "validation": {"accepted": not (rejections or []), "rejections": rejections or []},
               "result": {"detector": detector, "execution": to},
               "receiptPath": f"receipts/{k}.json", "runId": self.run_id, "updatedUtc": utcnow()}
        if prev:
            for keep in ("reusedFromRunId", "derivedFrom", "imported", "importOrigin"):
                if keep in prev and (extra or {}).get(keep) is None:
                    rec[keep] = prev[keep]
        rec.update(extra or {})
        self.doc["entries"][k] = rec
        with open(self.journal, "a", encoding="utf-8") as f:
            f.write(canonical({"t": utcnow(), "checkId": check_id, "armId": arm_id, "part": part,
                               "from": frm, "to": to, "cause": cause, "receiptId": receipt_id,
                               "detail": detail, "nextAction": next_action,
                               "runId": self.run_id}) + "\n")
        self.save()

    def save(self) -> None:
        write_json(self.path, self.doc)

    def heal_interrupted(self) -> int:
        """A `running` entry with no terminal line is an INTERRUPTED one, never a completed one."""
        n = 0
        for k, e in list(self.doc["entries"].items()):
            if e.get("status") == "running":
                self.transition(e["checkId"], e["armId"], e.get("part"), "interrupted",
                                cause="the process did not write a terminal transition")
                n += 1
        return n


def read_receipts(run: Path) -> dict[str, dict]:
    out = {}
    d = Path(run) / "receipts"
    if not d.exists():
        return out
    for p in sorted(d.glob("*.json")):
        doc = load_json(p)
        if isinstance(doc, dict):
            out[p.stem] = doc
    return out


# ------------------------------------------------------------------------------ finalize (D7) --


def derived_from(root: Path, receipts: dict | None = None) -> dict:
    """What a disposition was derived FROM, so a later verb can tell whether it still holds: the
    ledger's own hash, one hash over the sorted (ledger key, receipt file hash) pairs, and the journal's
    line count."""
    root = Path(root)
    if receipts is None:
        receipts = read_receipts(root)
    pairs = sorted((k, sha256_file(root / "receipts" / f"{k}.json")) for k in receipts)
    journal = root / "journal.jsonl"
    lines = 0
    if journal.exists():
        lines = sum(1 for ln in journal.read_text(encoding="utf-8", errors="replace").splitlines()
                    if ln.strip())
    return {"ledgerSha256": sha256_file(root / "ledger.json"),
            "receipts": sha256_text(canonical(pairs)), "journalLines": lines}


# --------------------------------------------------------------------------------- bundle (D8) --


def receipt_evidence_rel(receipt_path: str) -> str:
    """Where a bundle keeps a file a receipt names (an output, a base emit, a comparison input): bundle-relative, under
    evidence/. The one rule `bundle` copies by and `verify-bundle` checks by; `verify-bundle --out` lists every receipt
    output it resolved this way under `receiptOutputs`, so a reader of a bundle takes its paths from there."""
    return f"evidence/{receipt_path}"


MANIFEST_KEYS = ("bundleSchema", "planId", "required", "evidence", "plan", "ledger", "journal",
                 "disposition", "status", "registry")


def cmd_verify_bundle(a) -> int:
    """D8: structural validity, then hashes, then the evidence chain — reading NOTHING outside the
    bundle. Structural validity and evaluation completion are two separate states and both are written:
    a correctly accounted INCOMPLETE evaluation in an intact bundle is a valid deliverable and exits 0.
    A list that omits a required file cannot prove completeness, so the chain is walked from the
    bundle's own ledger and receipts rather than from what the manifest chose to list. With --out the result goes to
    that file and nothing is written into DIR."""
    d = Path(a.dir)
    defects: list[dict] = []
    missing: list[str] = []
    mismatched: list[dict] = []
    ok = 0
    memo = HashMemo()
    identity_count = 0
    guarantee = None
    receipt_outputs: list[dict] = []

    def defect(kind: str, path: str | None, detail: str) -> None:
        defects.append({"kind": kind, "path": path, "detail": detail})

    # (1) the manifest itself
    manifest = load_json(d / "manifest.json")
    schema_supported = False
    if not isinstance(manifest, dict):
        manifest = {}
        defect("manifest-missing", "manifest.json",
               f"{d / 'manifest.json'} is absent or not readable as JSON: this is not a bundle.")
    else:
        if manifest.get("bundleSchema") != BUNDLE_SCHEMA:
            defect("schema-unsupported", "manifest.json",
                   f"bundleSchema {manifest.get('bundleSchema')!r} is not {BUNDLE_SCHEMA}: this bundle "
                   f"predates evidence-chain verification; rebuild it from its run.")
        else:
            schema_supported = True
        absent_keys = [k for k in MANIFEST_KEYS if k not in manifest]
        if absent_keys:
            defect("manifest-incomplete", "manifest.json",
                   "the manifest does not carry " + ", ".join(absent_keys))

    if schema_supported:
        # (2) required paths, then every listed evidence row against its recorded hash
        for rel in manifest.get("required") or []:
            if not (d / rel).exists():
                defect("required-missing", rel, "a path the manifest declares required is not present")
        for e in manifest.get("evidence") or []:
            p = d / e["path"]
            if not p.exists():
                missing.append(e["path"])
                continue
            got = memo.sha256(p)
            if got != e["sha256"]:
                mismatched.append({"path": e["path"], "recorded": e["sha256"], "found": got})
            else:
                ok += 1

        # (3) the plan's own immutability and the planId every document agrees on
        plan = load_json(d / "plan.json") or {}
        guarantee = identity_guarantee(plan)["guarantee"]
        recorded_plan = (d / "plan.sha256")
        if not (d / "plan.json").exists() or not recorded_plan.exists():
            defect("plan-missing", "plan.json", "the bundle carries no plan and its recorded hash")
        elif memo.sha256(d / "plan.json") != recorded_plan.read_text(encoding="utf-8").strip():
            defect("plan-changed", "plan.json", "plan.json does not hash to plan.sha256")
        ledger_doc = load_json(d / "ledger.json") or {}
        disp = load_json(d / "disposition.json") or {}
        ids = {"manifest": manifest.get("planId"), "plan": plan.get("planId"),
               "ledger": ledger_doc.get("planId"), "disposition": disp.get("planId")}
        if len({v for v in ids.values()}) > 1:
            defect("planid-disagreement", None,
                   "the manifest, plan, ledger and disposition do not agree on planId: " + canonical(ids))

        # (4) the registry, scope and candidate-search copies against the hashes the plan froze
        for rel, want, label in ((manifest.get("registry") or "registry/check-registry.json",
                                  (plan.get("registry") or {}).get("sha256"), "registry"),
                                 (manifest.get("scope") or "scope/scope.json",
                                  (plan.get("scope") or {}).get("sha256"), "scope"),
                                 (manifest.get("search") or "search/candidates.json",
                                  (plan.get("search") or {}).get("sha256"), "search")):
            if want is None:
                continue  # a plan made without --scope froze no scope hash; a search recorded absent froze none
            if not (d / rel).exists():
                defect(f"{label}-missing", rel, f"the plan froze a {label} hash but the copy is absent")
            elif memo.sha256(d / rel) != want:
                defect(f"{label}-mismatch", rel,
                       f"the {label} copy hashes {str(memo.sha256(d / rel))[:12]}, the plan froze "
                       f"{str(want)[:12]}")

        # (5) the receipt-to-evidence chain, walked from the LEDGER
        listed = {e["path"] for e in (manifest.get("evidence") or [])}
        for k, e in (ledger_doc.get("entries") or {}).items():
            if e.get("status") not in TERMINAL_OK:
                continue
            rec = load_json(d / "receipts" / f"{k}.json")
            if not isinstance(rec, dict):
                defect("receipt-missing", f"receipts/{k}.json",
                       f"the ledger records {k} as {e.get('status')} and the bundle carries no receipt")
                continue
            if e.get("receiptId") and rec.get("receiptId") != e.get("receiptId"):
                defect("receipt-mismatch", f"receipts/{k}.json",
                       f"the ledger names receipt {e.get('receiptId')}, the file is {rec.get('receiptId')}")
            if not (rec.get("validation") or {}).get("accepted"):
                defect("receipt-mismatch", f"receipts/{k}.json",
                       "a terminal-ok ledger entry whose receipt was not accepted")
            # the identity each role was accepted under, against the bundle's own plan: no tool is read
            for role, pid, kind, want, got in identity_defects(plan, rec):
                identity_count += 1
                defect(kind, f"receipts/{k}.json",
                       f"receipt {rec.get('receiptId')} names its {role} {pid} at {toolidentity.short(got)}; the plan "
                       f"recorded {toolidentity.short(want)}")
            # Its outputs AND the run-internal emits it was compared against: a diff whose arm emit the
            # bundle omits cannot be re-read as a diff of anything.
            chain = [(o.get("path"), o.get("sha256"), True) for o in (rec.get("outputs") or [])]
            consumed = rec.get("consumed") or {}
            if consumed.get("baseEmitPath"):
                chain.append((consumed["baseEmitPath"], consumed.get("baseEmitSha256"), False))
            for row in consumed.get("inputs") or []:
                chain.append((row.get("path"), row.get("sha256"), False))
            for path, recorded, is_output in chain:
                if not path:
                    continue
                rel = receipt_evidence_rel(path)
                if is_output:
                    present = (d / rel).exists()
                    receipt_outputs.append({
                        "receipt": k, "receiptId": rec.get("receiptId"), "checkId": rec.get("checkId"),
                        "armId": rec.get("armId"), "part": rec.get("part"), "recordedPath": path, "path": rel,
                        "sha256": recorded, "present": present,
                        "matches": bool(present and recorded is not None and memo.sha256(d / rel) == recorded)})
                if not (d / rel).exists():
                    defect("evidence-missing", rel,
                           f"receipt {rec.get('receiptId')} records this file and the bundle omits it")
                    continue
                if recorded is not None and memo.sha256(d / rel) != recorded:
                    defect("evidence-changed", rel,
                           f"receipt {rec.get('receiptId')} recorded {str(recorded)[:12]}, the copy "
                           f"hashes {str(memo.sha256(d / rel))[:12]}")
                if is_output and rel not in listed:
                    defect("manifest-omits-required", rel,
                           "a receipt's required output is present but not listed in the manifest, so the "
                           "manifest cannot prove the bundle complete")
        for row in (disp.get("manualDecisions") or []):
            if not row.get("accepted"):
                continue
            for ev_row in row.get("evidence") or []:
                rel = f"evidence/{ev_row['path']}"
                if not (d / rel).exists():
                    defect("decision-evidence-missing", rel,
                           f"the accepted decision on {row.get('checkId')} cites evidence the bundle omits")

        # (6) the disposition must agree with the ledger it claims to be derived from
        entries = ledger_doc.get("entries") or {}
        ev = disp.get("evaluation") or {}
        for label, rows, want_detector in (("findings", ev.get("findings") or [], "finding"),
                                           ("completedNullChecks", ev.get("completedNullChecks") or [],
                                            "no-finding"),
                                           ("completedCatalogues", ev.get("completedCatalogues") or [],
                                            "catalogue")):
            for row in rows:
                key = Ledger.key(row.get("checkId"), row.get("armId"), row.get("part"))
                entry = entries.get(key)
                if not entry or entry.get("status") not in TERMINAL_OK \
                        or entry.get("detector") != want_detector:
                    defect("disposition-unsupported-claim", None,
                           f"disposition.evaluation.{label} names {key}, which the ledger does not hold "
                           f"as a terminal-ok {want_detector} entry")
        # (7) evidence derived after measurement: sources, reads, and the disposition it was derived beside
        import bundleenrich
        bundleenrich.verify(d, manifest, plan, defect, memo.sha256)
        if ev.get("complete") and (ev.get("missing") or ev.get("unknownApplicability")):
            defect("disposition-complete-contradiction", "disposition.json",
                   "evaluation.complete is true while missing or unknownApplicability is non-empty")
        want_derived = derived_from(d, read_receipts(d))
        if disp.get("derivedFrom") != want_derived:
            defect("disposition-stale", "disposition.json",
                   f"the disposition was derived from ledger "
                   f"{str((disp.get('derivedFrom') or {}).get('ledgerSha256'))[:12]}, the bundle carries "
                   f"{str(want_derived['ledgerSha256'])[:12]}")

    structurally_valid = schema_supported and not defects
    complete = structurally_valid and not missing and not mismatched
    disp = load_json(d / "disposition.json") or {}
    ev = disp.get("evaluation") or {}
    verify = {"schemaVersion": SCHEMA_VERSION, "checkedUtc": utcnow(), "dir": str(d.resolve()),
              "bundleSchema": manifest.get("bundleSchema"), "planId": manifest.get("planId"),
              "bundle": {"schemaSupported": schema_supported, "structurallyValid": structurally_valid,
                         "complete": complete, "verified": ok,
                         "missing": missing, "mismatched": mismatched, "defects": defects},
              # verified: the bundle's plan froze program identities and every accepted local receipt was compared with
              # them here; legacy: the plan predates that contract, so which build produced each result is not checked
              "toolIdentity": {"guarantee": guarantee, "defects": identity_count},
              # READ from the bundle's disposition, never recomputed: verify-bundle says whether the
              # package is intact, not whether the evaluation was finished.
              "evaluation": {"complete": ev.get("complete"), "claim": ev.get("claim"),
                             "missing": ev.get("missing", [])},
              # every accepted terminal receipt's outputs as this verb resolved and checked them (bundle-relative):
              # a reader takes a receipt's files from here, never by guessing a root
              "receiptOutputs": receipt_outputs,
              "note": "every path is bundle-relative, so a relocated bundle validates identically; bundle "
                      "integrity and evaluation completion are two states and both are written here"}
    write_json(Path(getattr(a, "out", None) or d / "verify.json"), verify)
    print(f"verify: {ok} file(s) verified, {len(missing)} missing, {len(mismatched)} mismatched, "
          f"{len(defects)} defect(s); bundle.complete: {complete}; "
          f"evaluation.complete: {ev.get('complete')}")
    for x in defects[:20]:
        print(f"   {x['kind']}: {x['path'] or '-'} — {x['detail']}", file=sys.stderr)
    return EXIT_OK if complete else EXIT_REFUSED_RESULT


# ------------------------------------------------------------------------------------ enrich --

def cmd_enrich(a) -> int:
    """Evidence derived after measurement, added to a bundle (bundle standard §8): verified first, refused by name."""
    import bundleenrich
    d = Path(a.bundle)
    # the run history goes to the configured store, never into the bundle
    with Run("evalrun-enrich", stage_source="counted") as r:
        r.stage("enrich", total=1, unit="enrichments")
        if verify_quiet(d) != EXIT_OK:
            msg = (f"enrich refused: {d} does not verify (see its verify.json); an enrichment is added only to an "
                   f"intact bundle")
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg, EXIT_REFUSED_INPUT)
            return EXIT_REFUSED_INPUT
        bad = next((x for x in a.mod_name or [] if "=" not in x), None)
        if bad is not None:
            return refuse(f"--mod-name takes PLUGIN=NAME, got {bad!r}")
        if a.kind == "names":
            cmd, source = default_reader_cmd(a.reader_cmd)
            st = names_step(d, cmd, source, mod_names(a.mod_name), reader_env(a.reader_env))
            if st["status"] == "refused":
                msg = f"enrich refused: {st['sentence']}"
                print(f"REFUSED: {msg}", file=sys.stderr)
                r.refuse(msg, EXIT_REFUSED_INPUT)
                return EXIT_REFUSED_INPUT
            write_names_status(d, st)            # a bundle made with the step skipped or refused now says enriched
            out = st["result"]
        else:
            try:
                if a.kind == "order-geometry" and not a.sweep:
                    raise bundleenrich.Refused("--kind order-geometry needs --sweep, the ordersweep folder over the "
                                               "measured order and its reversal")
                out = (bundleenrich.enrich_order_variant(d) if a.kind == "order-variant"
                       else bundleenrich.enrich_order_geometry(d, [Path(x) for x in a.sweep]) if a.kind == "order-geometry"
                       else bundleenrich.enrich_record_rows(d))
            except bundleenrich.Refused as ex:
                msg = f"enrich refused: {ex}"
                print(f"REFUSED: {msg}", file=sys.stderr)
                r.refuse(msg, EXIT_REFUSED_INPUT)
                return EXIT_REFUSED_INPUT
        if verify_quiet(d) != EXIT_OK:
            msg = f"enrich wrote {a.kind} but the bundle no longer verifies; see its verify.json"
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg, EXIT_REFUSED_RESULT)
            return EXIT_REFUSED_RESULT
        r.advance()
        print(json.dumps(out))
        return EXIT_OK


def verify_quiet(d: Path) -> int:
    import contextlib
    import io
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return cmd_verify_bundle(SimpleNamespace(dir=str(d)))


#: The names enrichment's record reader when --reader-cmd names none (a command line, as --reader-cmd takes).
READER_ENV = "HOUSECARL_RECORD_READER_CMD"


def default_reader_cmd(explicit: str | None) -> tuple[str | None, str]:
    """The record reader the names enrichment starts: --reader-cmd, else HOUSECARL_RECORD_READER_CMD, else this
    checkout's built houseCARL server. Returns (command, where it came from), or (None, one sentence why none)."""
    if explicit:
        return explicit, "--reader-cmd"
    env = (os.environ.get(READER_ENV) or "").strip()
    if env:
        return env, READER_ENV
    exe = HERE.parent / "src" / "housecarl-mcp" / "bin" / "Release" / "net9.0" / exe_name("housecarl-mcp")
    if exe.is_file():
        return f'"{exe}"', "checkout"
    return None, (f"no record reader was given (--reader-cmd or {READER_ENV}) and this checkout's houseCARL server is "
                  f"not built at {exe}.")


def mod_names(pairs) -> dict:
    """--mod-name PLUGIN=NAME pairs (the caller has refused a pair without '=')."""
    return {k.strip(): v.strip() for k, v in (x.split("=", 1) for x in pairs or [] if "=" in x)}


def reader_env(pairs) -> dict:
    return dict(x.split("=", 1) for x in pairs or [] if "=" in x)


def names_step(d: Path, cmd: str | None, source: str, mods: dict, env: dict) -> dict:
    """Run the names enrichment on bundle d and say what happened in one sentence: enriched, partial (something the
    rows name could not be read) or refused. Never raises for a refusal; nothing of the names is written on one."""
    import bundleenrich
    t0 = time.monotonic()

    def done(status, sentence, result=None, counts=None):
        return {"status": status, "sentence": " ".join(str(sentence).split()), "reader": source if cmd else None,
                "counts": counts, "seconds": round(time.monotonic() - t0, 1), "result": result}

    if cmd is None:
        return done("refused", "The names enrichment was not run because " + source)
    try:
        out = bundleenrich.enrich_names(d, cmd, mods, env or None)
    except bundleenrich.Refused as ex:
        return done("refused", f"The names enrichment was refused: {str(ex).rstrip('.')}.")
    except (OSError, ValueError) as ex:
        return done("refused", f"The names enrichment could not start or read with its record reader ({cmd}): "
                               f"{str(ex).rstrip('.') or type(ex).__name__}.")
    c = out.get("counts") or {}
    if c.get("objectsUnread") or c.get("basesUnread"):
        return done("partial", f"The names enrichment could not read {c.get('objectsUnread')} of the "
                               f"{c.get('objectsNamed')} objects the rows name and {c.get('basesUnread')} of their "
                               f"{c.get('bases')} base records (listed under unread in enrichment/names/names.json), so "
                               f"the report counts their clashes as cannot-tell.", out, c)
    return done("enriched", f"The names enrichment read all {c.get('objectsNamed')} objects the rows name and their "
                            f"{c.get('bases')} base records.", out, c)


def write_names_status(d: Path, st: dict) -> dict:
    """Record the names step's statement in the bundle's manifest (`enrichmentStatus.names`), which verify-bundle
    checks against the files."""
    m = load_json(d / "manifest.json") or {}
    m.setdefault("enrichmentStatus", {})["names"] = {k: v for k, v in st.items() if k != "result"}
    write_json(d / "manifest.json", m)
    return st


# -------------------------------------------------------------------------------------- cli --

def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalrun", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("verify-bundle", help="recompute every hash from the bundle-relative paths")
    p.add_argument("dir")
    p.add_argument("--out", default=None, metavar="FILE",
                   help="write the result to FILE instead of DIR/verify.json, so DIR is only read")
    p.set_defaults(fn=cmd_verify_bundle)

    p = sub.add_parser("enrich", help="add evidence derived after measurement to a bundle (record rows, names)")
    p.add_argument("--bundle", required=True)
    p.add_argument("--kind", required=True, choices=("record-rows", "names", "order-variant", "order-geometry"))
    p.add_argument("--sweep", action="append", default=[],
                   help="order-geometry: an ordersweep out folder over the measured order and its reversal (repeatable)")
    p.add_argument("--reader-cmd", help="names: the houseCARL MCP server command line to read records with (default: "
                                        f"{READER_ENV}, else this checkout's built server)")
    p.add_argument("--reader-env", action="append", metavar="KEY=VALUE",
                   help="names: an environment variable for the reader, such as a private HOUSECARL_DATA_DIR")
    p.add_argument("--mod-name", action="append", metavar="PLUGIN=NAME", help="names: a mod's declared ordinary name")
    p.set_defaults(fn=cmd_enrich)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return EXIT_REFUSED_INPUT


if __name__ == "__main__":
    sys.exit(main())
