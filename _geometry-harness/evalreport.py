#!/usr/bin/env python
"""evalreport - compile a verified schema-2 evaluation bundle into one presentation plan and, from that
plan, a concise illustrated Markdown report, an HTML rendition, a paginated PDF with a verifiable page
limit, a complete evidence index and a presentation manifest.

No model call, no paid service, no network. Every sentence the report prints is a template sentence from
the evaluation-reporting skill's `report-priorities.json`, bound to numbers and names the bundle itself
carries: the disposition's findings, missing list, order evaluation and unassessed block; the receipts'
coverage lines; the emits' conflict rows; the registry copy's titles, claims and limits. Where the bundle
lacks something the report says "not assessed" or "not supplied" - never a guess.

Subcommands (contract: standards/HOUSECARL_EVALUATION_PRESENTATION.md):

  plan            --bundle DIR --out OUT [--mode concise|exhaustive] [--max-pages N] [--verify-copy]
  report          --presentation OUT/presentation.json --out OUT [--no-print]
  verify          --out OUT
  narrate-packet  --presentation P --out FILE
  narrate-apply   --presentation P --answers FILE [--out P2]

Exit codes: 0 ok; 2 a refused input (named on stderr, nothing written past the refusal record); 3 a
refused result (verify found a defect). A missing browser is `print.status: "not-rendered"` with the
reason - the Markdown and HTML are still written, and no page count is ever invented.

Q31: every subcommand opens a `scripts/runlog.py` Run (`evalreport-plan`, `evalreport-report`,
`evalreport-verify`, `evalreport-narrate`) with findings / groups / media / figures / pages as features.
"""
from __future__ import annotations

import argparse
import collections
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

try:
    import jinja2
except ImportError:  # pragma: no cover - stated, never worked around
    jinja2 = None

TEMPLATES = HERE / "evalreport_templates"
PRIORITIES = HERE.parent / ".claude" / "skills" / "evaluation-reporting" / "references" / "report-priorities.json"
EVALRUN = HERE / "evalrun.py"

PRESENTATION_SCHEMA = 1
BUNDLE_SCHEMA = 2
COMPILER_VERSION = "1"
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3

ARTIFACTS = ("report.md", "report.html", "report.pdf", "evidence-index.md")


# ------------------------------------------------------------------------------------------ helpers --

def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def sha256_text(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def canonical(obj) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def load_json(p: Path):
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def write_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False)
        f.write("\n")


def write_text(p: Path, text: str) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def read_text(p: Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


def refuse(run: Run | None, reason: str, code: int = EXIT_REFUSED_INPUT) -> int:
    print(f"REFUSED: {reason}", file=sys.stderr)
    if run is not None:
        run.refuse(reason, exit_code=code)
    return code


def fmt_n(v) -> str:
    if isinstance(v, bool) or v is None:
        return str(v)
    if isinstance(v, int):
        return f"{v:,}"
    if isinstance(v, float):
        return f"{v:,.2f}"
    return str(v)


def arm_sort_key(arm_id: str, kinds: dict | None = None) -> tuple:
    kind = (kinds or {}).get(arm_id)
    return (0 if (arm_id == "baseline" or kind == "baseline") else 1, arm_id)


PART_ORDER = {None: 0, "arm": 1, "reverse": 2, "diff": 3}


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", str(text).lower()).strip("-")


def load_priorities() -> dict:
    pr = load_json(PRIORITIES)
    if not isinstance(pr, dict):
        raise SystemExit(f"REFUSED: report-priorities.json is absent or not JSON at {PRIORITIES}")
    return pr


# ------------------------------------------------------------------------------------ the browser --

WINDOWS_BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Chromium\Application\chrome.exe",
]


def browser_version(exe: Path) -> str:
    # Chrome and Edge on Windows keep the version as a sibling directory of the executable; asking the
    # binary itself prints nothing usable in headless mode. Elsewhere `--version` answers.
    try:
        vers = sorted(d.name for d in exe.parent.iterdir()
                      if d.is_dir() and re.fullmatch(r"\d+\.\d+\.\d+\.\d+", d.name))
        if vers:
            return vers[-1]
    except OSError:
        pass
    if os.name != "nt":
        try:
            out = subprocess.run([str(exe), "--version"], capture_output=True, text=True, timeout=20)
            if out.stdout.strip():
                return out.stdout.strip()
        except (OSError, subprocess.SubprocessError):
            pass
    return "unknown"


def resolve_browser() -> tuple[Path | None, str, str]:
    """Resolution order (D4): HOUSECARL_BROWSER, PATH chrome / msedge / chromium, the standard Windows
    install paths. Returns (path, version, reason-when-absent)."""
    env = (os.environ.get("HOUSECARL_BROWSER") or "").strip()
    if env:
        p = Path(env)
        if p.exists():
            return p, browser_version(p), ""
        return None, "", f"HOUSECARL_BROWSER points at {env}, which does not exist"
    for name in ("chrome", "google-chrome", "google-chrome-stable", "msedge", "chromium", "chromium-browser"):
        w = shutil.which(name)
        if w:
            return Path(w), browser_version(Path(w)), ""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        cands = list(WINDOWS_BROWSERS)
        if local:
            cands.append(str(Path(local) / "Google" / "Chrome" / "Application" / "chrome.exe"))
        for c in cands:
            if Path(c).exists():
                return Path(c), browser_version(Path(c)), ""
    return None, "", ("no Chromium browser found: set HOUSECARL_BROWSER, or put chrome / msedge / chromium "
                      "on PATH, or install Chrome or Edge at the standard location")


def pdf_page_count(pdf: Path) -> tuple[int | None, int | None, str]:
    """Count `/Type /Page` objects (not `/Pages`) in the PDF's uncompressed page tree and cross-check
    against the root `/Pages` node's `/Count`. Chrome's Skia backend writes the page tree uncompressed.
    Returns (pages, rootCount, note); pages is None when nothing countable was found."""
    try:
        data = pdf.read_bytes()
    except OSError as ex:
        return None, None, f"unreadable: {ex}"
    if not data.startswith(b"%PDF"):
        return None, None, "not a PDF (no %PDF header)"
    pages = len(re.findall(rb"/Type\s*/Page\b", data))
    counts = []
    for m in re.finditer(rb"\d+\s+\d+\s+obj\b(.*?)endobj", data, re.S):
        body = m.group(1)
        if re.search(rb"/Type\s*/Pages\b", body):
            c = re.search(rb"/Count\s+(\d+)", body)
            if c:
                counts.append(int(c.group(1)))
    root = max(counts) if counts else None
    if pages == 0 and root is None:
        return None, None, "no page objects visible (object streams?) - the count could not be established"
    if root is not None and root != pages:
        return pages, root, f"page objects {pages} disagree with the root /Count {root}"
    return pages, root, "ok"


def print_pdf(browser: Path, html: Path, pdf: Path, timeout: int = 180) -> tuple[bool, str]:
    profile = Path(tempfile.mkdtemp(prefix="evalreport-browser-"))
    try:
        argv = [str(browser), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                f"--user-data-dir={profile}", f"--print-to-pdf={pdf}", html.resolve().as_uri()]
        try:
            r = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return False, f"the browser did not finish within {timeout}s"
        except OSError as ex:
            return False, f"the browser could not be started: {ex}"
        if not pdf.exists() or pdf.stat().st_size == 0:
            tail = (r.stderr or "")[-400:].strip().replace("\n", " | ")
            return False, f"the browser exited {r.returncode} and wrote no PDF: {tail or 'no stderr'}"
        return True, ""
    finally:
        shutil.rmtree(profile, ignore_errors=True)


# ------------------------------------------------------------------------------ bundle verification --

def run_verify_bundle(bundle: Path, out: Path, copy: bool) -> tuple[dict | None, int, str]:
    """D1: `evalrun.py verify-bundle DIR` is the authority. With --verify-copy the bundle is copied under
    OUT first so the original directory is not written to (verify-bundle writes DIR/verify.json); the
    copy verifies identically because verify-bundle reads nothing outside the bundle."""
    target = bundle
    tmp = None
    if copy:
        tmp = out / ".verify-copy"
        shutil.rmtree(tmp, ignore_errors=True)
        shutil.copytree(bundle, tmp)
        target = tmp
    try:
        r = subprocess.run([sys.executable, str(EVALRUN), "verify-bundle", str(target)],
                           capture_output=True, text=True, timeout=900)
    except (OSError, subprocess.SubprocessError) as ex:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)
        return None, EXIT_REFUSED_INPUT, f"verify-bundle could not run: {ex}"
    verify = load_json(target / "verify.json")
    if isinstance(verify, dict):
        write_json(out / "verify.json", verify)
    if tmp:
        shutil.rmtree(tmp, ignore_errors=True)
    if not isinstance(verify, dict):
        return None, r.returncode, ("verify-bundle wrote no readable verify.json: " +
                                    (r.stderr or r.stdout or "")[-400:].strip())
    return verify, r.returncode, ""


# -------------------------------------------------------------------------------------- compile --

class Bundle:
    """Read-only view of a schema-2 bundle: manifest, disposition, plan, ledger, registry, receipts."""

    def __init__(self, d: Path):
        self.dir = d
        self.manifest = load_json(d / "manifest.json") or {}
        self.disposition = load_json(d / self.manifest.get("disposition", "disposition.json")) or {}
        self.plan = load_json(d / self.manifest.get("plan", "plan.json")) or {}
        self.ledger = load_json(d / self.manifest.get("ledger", "ledger.json")) or {}
        self.registry = load_json(d / self.manifest.get("registry", "registry/check-registry.json")) or {}
        self.scope = load_json(d / self.manifest["scope"]) if self.manifest.get("scope") else None
        self.checks = {c.get("id"): c for c in (self.registry.get("checks") or []) if isinstance(c, dict)}
        self.families = {f.get("id"): f for f in (self.registry.get("families") or []) if isinstance(f, dict)}
        self.exe_to_check = {}
        for cid, c in self.checks.items():
            exe = (c.get("adapter") or {}).get("exe")
            if exe and exe not in self.exe_to_check:
                self.exe_to_check[exe] = cid
        self.evidence = [e for e in (self.manifest.get("evidence") or []) if isinstance(e, dict)]
        self.by_path = {e["path"]: e for e in self.evidence if e.get("path")}
        self.receipts = {}
        for e in self.evidence:
            if e.get("kind") == "receipt":
                rc = load_json(d / e["path"])
                if isinstance(rc, dict) and rc.get("receiptId"):
                    self.receipts[rc["receiptId"]] = rc
        self.receipt_by_key = {}
        for rid, rc in self.receipts.items():
            self.receipt_by_key[(rc.get("checkId"), rc.get("armId"), rc.get("part"))] = rc
        self._emits = {}

    def emit(self, path: str):
        if path not in self._emits:
            self._emits[path] = load_json(self.dir / path)
        return self._emits[path]

    def check_title(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("title") or cid

    def check_family(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("family") or (cid.split(".")[0] if cid else "")

    def evidence_for(self, check_id: str, arm_id: str, part: str | None) -> list[dict]:
        """Manifest evidence rows recorded by the receipt of (check, arm, part), plus the receipt row."""
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        rows = []
        if rc:
            rid = rc["receiptId"]
            for e in self.evidence:
                if e.get("recordedBy") == rid or ((e.get("provenance") or {}).get("receiptId") == rid):
                    rows.append(e)
            rp = f"receipts/{check_id}__{arm_id}" + (f"__{part}" if part else "") + ".json"
            if rp in self.by_path:
                rows.append(self.by_path[rp])
        return rows

    def output_emit_path(self, check_id: str, arm_id: str, part: str | None) -> str | None:
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        if not rc:
            return None
        rid = rc["receiptId"]
        for e in self.evidence:
            if e.get("kind") == "emit" and e.get("recordedBy") == rid and str(e.get("path", "")).endswith(".json"):
                return e["path"]
        return None

    def diff_log_path(self, check_id: str, arm_id: str) -> str | None:
        rc = self.receipt_by_key.get((check_id, arm_id, "diff"))
        if not rc:
            return None
        rid = rc["receiptId"]
        for e in self.evidence:
            if e.get("recordedBy") == rid and str(e.get("path", "")).endswith(".txt"):
                return e["path"]
        return None


DIFF_LINE = re.compile(r"^\s*(NEW|GREW|SHRANK|GONE|SAME|UNCHANGED)\s+(\S+)\s+(.+?)\s{2,}x\s{2,}(.+?)\s{2,}([+-]?\d+)\s+record\(s\):\s*(.*?)\s*$")


def parse_diff_log(text: str) -> list[dict]:
    out = []
    for line in text.splitlines():
        m = DIFF_LINE.match(line)
        if not m:
            continue
        tail = m.group(6)
        nums = {k: int(v) for k, v in re.findall(r"(repaired|reclassified|created|unmeasured)\s+(\d+)", tail)}
        out.append({"change": m.group(1), "kind": m.group(2), "A": m.group(3).strip(), "B": m.group(4).strip(),
                    "delta": int(m.group(5)), "line": line.strip(), **nums})
    return out


def conflict_rows(conflict: dict, evidence_id: str | None) -> list[dict]:
    rows = []
    for n in conflict.get("Necessary") or []:
        if not isinstance(n, dict):
            continue
        rows.append({"formKey": n.get("FormKey"), "label": n.get("Label"), "summary": conflict.get("Summary"),
                     "physical": n.get("Physical"), "physicalDetail": n.get("PhysicalDetail"),
                     "evidenceId": evidence_id})
    if not rows:
        rows.append({"formKey": None, "label": None, "summary": conflict.get("Summary"), "physical": None,
                     "physicalDetail": None, "evidenceId": evidence_id})
    return rows


def bind(template: str, values: dict) -> str:
    class Safe(dict):
        def __missing__(self, k):
            return "not supplied"
    vals = {k: (fmt_n(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else
                ("not supplied" if v is None else v)) for k, v in values.items()}
    return template.format_map(Safe(vals))


def compile_plan(bundle_dir: Path, verify: dict, settings: dict, pr: dict, bundle_dir_as_given: str) -> dict:
    b = Bundle(bundle_dir)
    disp = b.disposition
    ev = disp.get("evaluation") or {}
    arms_disp = disp.get("arms") or {}
    plan_arms = {a.get("armId"): a for a in (b.plan.get("arms") or []) if isinstance(a, dict)}
    kinds = {a: (v.get("kind") or plan_arms.get(a, {}).get("kind")) for a, v in arms_disp.items()}
    for a, v in plan_arms.items():
        kinds.setdefault(a, v.get("kind"))
    arm_ids = sorted(set(arms_disp) | set(plan_arms), key=lambda a: arm_sort_key(a, kinds))
    templates = pr.get("templates") or {}

    # --- evidence rows: E-<manifest id>, with the receipt's part attached where the manifest lacks it
    evidence = []
    eid_by_path = {}
    for e in b.evidence:
        rid = e.get("recordedBy") or ((e.get("provenance") or {}).get("receiptId"))
        rc = b.receipts.get(rid) if rid else None
        row = {"id": f"E-{e.get('id')}", "kind": e.get("kind"), "path": e.get("path"), "sha256": e.get("sha256"),
               "bytes": e.get("bytes"), "checkId": e.get("checkId") or (rc or {}).get("checkId"),
               "armId": e.get("armId") or (rc or {}).get("armId"), "part": (rc or {}).get("part"),
               "strength": e.get("strength"), "recordedBy": e.get("recordedBy")}
        evidence.append(row)
        eid_by_path[e.get("path")] = row["id"]
    evidence.sort(key=lambda r: (r["path"] or ""))

    def eids_for(check_id, arm_id, part) -> list[str]:
        ids = []
        for e in b.evidence_for(check_id, arm_id, part):
            i = f"E-{e.get('id')}"
            if i not in ids:
                ids.append(i)
        return ids

    # --- media rows (manifest rows whose strength is supplied-media) and unassessed media
    media = []
    for e in b.evidence:
        if e.get("strength") != "supplied-media":
            continue
        sha = e.get("sha256") or ""
        media.append({"id": f"M-{sha[:12]}", "manifestId": e.get("id"), "path": e.get("path"), "sha256": sha,
                      "bytes": e.get("bytes"), "mediaKind": e.get("mediaKind"), "subject": e.get("subject"),
                      "state": e.get("state"), "configurationFingerprint": e.get("configurationFingerprint"),
                      "fingerprintSource": e.get("fingerprintSource"), "provenance": e.get("provenance"),
                      "sourceInterval": e.get("sourceInterval"), "original": e.get("original"),
                      "transformation": e.get("transformation"), "assessed": True, "reason": None})
    unassessed_media = []
    for u in b.manifest.get("unassessedMedia") or []:
        if not isinstance(u, dict):
            continue
        item = u.get("item") if isinstance(u.get("item"), dict) else u
        sha = u.get("sha256") or item.get("sha256") or sha256_text(canonical(u))
        unassessed_media.append({"id": f"M-{sha[:12]}", "manifestId": None, "path": item.get("path") or u.get("path"),
                                 "sha256": u.get("sha256") or item.get("sha256"), "mediaKind": item.get("mediaKind"),
                                 "subject": item.get("subject"), "state": item.get("state"),
                                 "configurationFingerprint": item.get("configurationFingerprint"),
                                 "fingerprintSource": item.get("fingerprintSource"), "provenance": item.get("provenance"),
                                 "sourceInterval": item.get("sourceInterval"), "original": item.get("original"),
                                 "transformation": item.get("transformation"), "assessed": False,
                                 "reason": u.get("reason") or "reason not stated"})
    media.sort(key=lambda m: (m["path"] or ""))
    unassessed_media.sort(key=lambda m: (m["path"] or ""))

    def media_for(check_id, arm_id, form_keys, part=None) -> list[str]:
        """A media item declared for arm X illustrates X's OWN configuration: it binds to X's blocks with part
        null, arm or diff, never to X's reverse part (the baseline order measured with X as --base)."""
        out = []
        if part == "reverse":
            return out
        for m in media:
            s = m.get("subject") or {}
            if s.get("checkId") != check_id or s.get("armId") != arm_id:
                continue
            fk = s.get("formKey")
            if fk and form_keys and fk not in form_keys:
                continue
            out.append(m["id"])
        return out

    # --- groups
    groups: dict[tuple, dict] = {}

    def group_for(check_id, kind, a_, b_) -> dict:
        key = (check_id, kind, a_, b_)
        if key not in groups:
            groups[key] = {"id": None, "title": f"{kind}: {a_} x {b_}", "checkId": check_id,
                           "key": f"{kind}|{a_}|{b_}", "findingIds": [], "rowsTotal": 0}
        return groups[key]

    findings: list[dict] = []
    mode = settings["mode"]
    rows_per_group = settings["rowsPerGroup"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")

    def add(f: dict) -> dict:
        f.setdefault("rows", [])
        f["rowsTotal"] = len(f["rows"])
        f.setdefault("mediaIds", [])
        f.setdefault("evidenceIds", [])
        f.setdefault("groupId", None)
        findings.append(f)
        return f

    # (1) regressions and (4) repairs, from orderEvaluation verbatim
    for arm_id in arm_ids:
        oe = (arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}
        per = oe.get("perProbe") or {}
        for probe in sorted(per):
            p = per[probe] or {}
            check_id = b.exe_to_check.get(probe, probe)
            counts = {"probe": probe, "classification": oe.get("classification"), **{k: p.get(k) for k in
                      ("verdict", "repaired", "reclassified", "created", "newDefect", "attributed", "unmeasured")}}
            log_path = b.diff_log_path(check_id, arm_id)
            diff_lines = parse_diff_log(read_text(bundle_dir / log_path)) if log_path else []
            disp_row = next((r for r in ev.get("findings") or [] if r.get("checkId") == check_id and
                             r.get("armId") == arm_id and r.get("part") == "diff"), None)
            strength = (disp_row or {}).get("strength") or (b.checks.get(check_id, {}).get("evidence") or {}).get("strength")
            emit_path = b.output_emit_path(check_id, arm_id, "arm")
            emit = b.emit(emit_path) if emit_path else None
            if (p.get("newDefect") or 0) > 0:
                created_groups = [d for d in diff_lines if d["change"] in ("NEW", "GREW") and d.get("created", 0) > 0]
                rows = []
                group = None
                for d in created_groups:
                    g = group_for(check_id, d["kind"], d["A"], d["B"])
                    group = group or g
                    if isinstance(emit, dict):
                        for c in emit.get("Conflicts") or []:
                            if (c.get("Kind"), c.get("A"), c.get("B")) == (d["kind"], d["A"], d["B"]):
                                rows.extend(conflict_rows(c, eid_by_path.get(emit_path)))
                first = created_groups[0] if created_groups else {}
                counts_r = {**counts, "groups": [{"kind": d["kind"], "A": d["A"], "B": d["B"], "created": d.get("created", 0)}
                                                 for d in created_groups]}
                f = add({"id": None, "kind": "regression", "priority": 1, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get("regression", ""), {**counts, "armId": arm_id,
                                                                            "groupKind": first.get("kind", "conflict")}),
                         "counts": {**counts_r, "diffLines": [d["line"] for d in created_groups]},
                         "evidenceIds": eids_for(check_id, arm_id, "diff") + [x for x in eids_for(check_id, arm_id, "arm")
                                                                             if x not in eids_for(check_id, arm_id, "diff")],
                         "mediaIds": media_for(check_id, arm_id, {r["formKey"] for r in rows}),
                         "groupId": group, "rows": rows})
                f["_group"] = group
            if (p.get("repaired") or 0) > 0 or (p.get("reclassified") or 0) > 0:
                kind = "repaired" if (p.get("repaired") or 0) > 0 else "reclassified"
                shrank = [d for d in diff_lines if d["change"] in ("SHRANK", "GONE")]
                f = add({"id": None, "kind": kind, "priority": 4, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get(kind, ""), {**counts, "armId": arm_id}),
                         "counts": {**counts, "diffLines": [d["line"] for d in shrank]},
                         "evidenceIds": eids_for(check_id, arm_id, "diff"), "rows": []})

    # (2) missing, verbatim from the disposition
    for m in ev.get("missing") or []:
        check_id = m.get("checkId")
        add({"id": None, "kind": "missing", "priority": 2, "checkId": check_id, "family": m.get("family") or b.check_family(check_id),
             "armId": m.get("armId"), "part": m.get("part"), "title": b.check_title(check_id), "strength": None,
             "verdict": None, "plain": bind(templates.get("missing", ""), {"armId": m.get("armId"), "status": m.get("status"),
                                                                             "cause": m.get("cause") or ""}),
             "counts": {"status": m.get("status"), "cause": m.get("cause"), "required": m.get("required")},
             "evidenceIds": eids_for(check_id, m.get("armId"), m.get("part")), "rows": []})

    # (3) findings on each arm: one per conflict group per (check, arm, part); a diff part with findings
    #     and no regression row; a non-conflict-file finding as one count
    regression_keys = {(f["checkId"], f["armId"]) for f in findings if f["kind"] == "regression"}
    for r in sorted(ev.get("findings") or [], key=lambda r: (r.get("checkId") or "", arm_sort_key(r.get("armId") or "", kinds),
                                                             PART_ORDER.get(r.get("part"), 9))):
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        oe = ((arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}).get("perProbe") or {}
        exe = (b.checks.get(check_id, {}).get("adapter") or {}).get("exe")
        verdict = r.get("verdict") or (oe.get(exe) or {}).get("verdict")
        base_eids = eids_for(check_id, arm_id, part)
        if part == "diff":
            if (check_id, arm_id) in regression_keys:
                continue
            p = oe.get(exe) or {}
            add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-diff", ""), {**p, "probe": exe, "armId": arm_id}),
                 "counts": {"findings": r.get("findings"), **p}, "evidenceIds": base_eids, "rows": []})
            continue
        emit_path = b.output_emit_path(check_id, arm_id, part)
        emit = b.emit(emit_path) if emit_path else None
        conflicts = emit.get("Conflicts") if isinstance(emit, dict) else None
        if not isinstance(conflicts, list):
            add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-count", ""), {"findings": r.get("findings"),
                                                                                       "armId": arm_id, "strength": r.get("strength")}),
                 "counts": {"findings": r.get("findings")}, "evidenceIds": base_eids, "rows": []})
            continue
        by_key: dict[tuple, list] = collections.OrderedDict()
        for c in conflicts:
            if not isinstance(c, dict):
                continue
            by_key.setdefault((c.get("Kind"), c.get("A"), c.get("B")), []).append(c)
        coverage = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                     "setAside": c.get("SetAside"), "note": c.get("Note")}
                    for c in (emit.get("Coverage") or []) if isinstance(c, dict)]
        ordered = sorted(by_key.items(), key=lambda kv: (-sum(len(c.get("Necessary") or []) or 1 for c in kv[1]),
                                                         kv[0][1] or "", kv[0][2] or "", kv[0][0] or ""))
        for (kind, a_, b_), cs in ordered:
            rows = []
            for c in cs:
                rows.extend(conflict_rows(c, eid_by_path.get(emit_path)))
            rows.sort(key=lambda x: (x.get("formKey") or "", x.get("summary") or ""))
            g = group_for(check_id, kind, a_, b_)
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                     "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                     "verdict": verdict,
                     "plain": bind(templates.get("finding", ""), {"rowsTotal": len(rows), "groupKind": kind, "A": a_, "B": b_,
                                                                  "armId": arm_id, "part": part or "arm",
                                                                  "findingsInPart": r.get("findings"), "strength": r.get("strength")}),
                     "counts": {"groupKind": kind, "A": a_, "B": b_, "conflicts": len(cs), "rows": len(rows),
                                "findingsInPart": r.get("findings"), "coverage": coverage,
                                "population": emit.get("Population"), "state": emit.get("State")},
                     "evidenceIds": base_eids, "mediaIds": media_for(check_id, arm_id, {x["formKey"] for x in rows}, part),
                     "rows": rows})
            f["_group"] = g

    # (5) completed null checks with their coverage lines from the receipt
    for r in ev.get("completedNullChecks") or []:
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        rc = b.receipt_by_key.get((check_id, arm_id, part)) or {}
        cov = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                "setAside": c.get("SetAside"), "note": c.get("Note")}
               for c in ((rc.get("result") or {}).get("coverage") or []) if isinstance(c, dict)]
        add({"id": None, "kind": "null", "priority": 5, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": part, "title": b.check_title(check_id),
             "strength": (rc.get("result") or {}).get("strength"), "verdict": (rc.get("result") or {}).get("verdict"),
             "plain": bind(templates.get("null", ""), {"armId": arm_id, "partText": f" ({part} part)" if part else "",
                                                      "coverage": r.get("coverage")}),
             "counts": {"coverage": r.get("coverage"), "coverageLines": cov}, "evidenceIds": eids_for(check_id, arm_id, part),
             "rows": []})

    # (6) catalogues
    for r in ev.get("completedCatalogues") or []:
        check_id, arm_id = r.get("checkId"), r.get("armId")
        add({"id": None, "kind": "catalogue", "priority": 6, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": r.get("part"), "title": b.check_title(check_id),
             "strength": (b.checks.get(check_id, {}).get("evidence") or {}).get("strength"), "verdict": None,
             "plain": bind(templates.get("catalogue", ""), {"count": r.get("count"), "unit": r.get("unit"), "armId": arm_id}),
             "counts": {"count": r.get("count"), "unit": r.get("unit"), "resultKind": r.get("kind")},
             "evidenceIds": eids_for(check_id, arm_id, r.get("part")), "rows": []})

    # (7) media not assessed, (8) everything under unassessed
    un = disp.get("unassessed") or {}
    for m in unassessed_media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": (m.get("subject") or {}).get("checkId"),
             "family": "runtime", "armId": (m.get("subject") or {}).get("armId"), "part": None, "title": "Media not assessed",
             "strength": None, "verdict": None,
             "plain": bind(templates.get("unassessed-media", ""), {"basename": Path(str(m.get("path") or "")).name or "unnamed",
                                                                   "mediaId": m["id"], "mediaKind": m.get("mediaKind"),
                                                                   "armId": (m.get("subject") or {}).get("armId"), "reason": m.get("reason")}),
             "counts": {"reason": m.get("reason")}, "mediaIds": [m["id"]], "rows": []})
    if not unassessed_media and not media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": None, "family": "runtime",
             "armId": None, "part": None, "title": "Media not assessed", "strength": None, "verdict": None,
             "plain": "No media was supplied to this bundle: no screenshot, render or clip illustrates any finding. "
                      + str(un.get("mediaUnassessed") or ""),
             "counts": {"supplied": 0}, "rows": []})

    def un_add(area, title, text, counts=None, check_id=None, arm_id=None):
        add({"id": None, "kind": "unassessed", "area": area, "priority": 8, "checkId": check_id,
             "family": b.check_family(check_id) if check_id else None, "armId": arm_id, "part": None, "title": title,
             "strength": None, "verdict": None, "plain": bind(templates.get("unassessed", "{text}"), {"text": text}),
             "counts": counts or {}, "evidenceIds": eids_for(check_id, arm_id, None) if check_id and arm_id else [],
             "rows": []})

    states = un.get("statesNotExplored")
    if states:
        un_add("states", "States not explored", "States not explored: " + ", ".join(map(str, states)) + ".",
               {"states": states})
    else:
        un_add("states", "States not explored", "No state beyond those the plan selected was named by the census as "
               "unexplored; the extent of world states is not enumerated by this runner.", {"states": []})
    held = un.get("candidatesHeldBack") or {}
    un_add("candidates", "Candidates held back",
           f"Candidates held back: {fmt_n(held.get('count') or 0)} by --max-candidates; the candidate manifest's own budget "
           f"evaluated {fmt_n((held.get('fromManifestBudget') or {}).get('evaluated') or 0)} and left "
           f"{fmt_n((held.get('fromManifestBudget') or {}).get('legalNotEvaluated') or 0)} legal order(s) not evaluated. "
           + str((held.get("fromManifestBudget") or {}).get("note") or ""), held)
    for u in un.get("unjudgeablePopulations") or []:
        uj = u.get("unjudgeable") or {}
        total = sum(v for v in uj.values() if isinstance(v, (int, float))) if isinstance(uj, dict) else uj
        detail = ", ".join(f"{k} {fmt_n(v)}" for k, v in uj.items()) if isinstance(uj, dict) else str(uj)
        un_add("unjudgeable", "Unjudgeable population",
               bind(templates.get("unjudgeable", ""), {"armId": u.get("armId"), "checkId": u.get("checkId"),
                                                       "judged": u.get("judged"), "population": u.get("population"),
                                                       "unjudgeableTotal": total, "unjudgeableDetail": detail}),
               {"judged": u.get("judged"), "unjudgeable": uj, "population": u.get("population")},
               u.get("checkId"), u.get("armId"))
    fam = un.get("familiesUnsupportedOrManual") or []
    if fam:
        un_add("families", "Families unsupported or manual",
               "Families with checks this runner cannot perform (unsupported, MCP-only or manual): "
               + ", ".join(b.families.get(x, {}).get("title") or x for x in fam) + ".", {"families": fam})
    for s in un.get("scopeUnmeasured") or []:
        un_add("scope", f"Scope extent not measured: {s.get('extent')}",
               f"Not measured ({s.get('extent')}): {s.get('why')}", {"extent": s.get("extent")})
    if un.get("runtimeObservations"):
        un_add("runtime", "Runtime observations", str(un.get("runtimeObservations")), {})

    # --- sort within sections, number findings and groups
    section_of = {}
    for s in pr.get("sections") or []:
        for k in s.get("kinds") or []:
            for area in (s.get("areas") or [None]):
                section_of[(k, area)] = s.get("order", 99)

    def sec(f):
        return section_of.get((f["kind"], f.get("area")), section_of.get((f["kind"], None), 99))

    def count_of(f):
        c = f.get("counts") or {}
        for k in ("newDefect", "created", "rows", "repaired", "reclassified", "findings", "count", "coverage"):
            v = c.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool):
                return v
        return f.get("rowsTotal") or 0

    def group_key(f):
        c = f.get("counts") or {}
        return "|".join(str(c.get(k) or "") for k in ("groupKind", "A", "B"))

    findings.sort(key=lambda f: (sec(f), f.get("checkId") or "", arm_sort_key(f.get("armId") or "", kinds), -count_of(f),
                                 (f.get("rows") or [{}])[0].get("formKey") or "", group_key(f),
                                 f.get("counts", {}).get("status") or "", PART_ORDER.get(f.get("part"), 9), f.get("plain") or ""))
    for i, f in enumerate(findings, 1):
        f["id"] = f"F-{i:03d}"
        f["priority"] = sec(f)
    gi = 0
    for key in sorted(groups):
        gi += 1
        groups[key]["id"] = f"G-{gi:03d}"
    for f in findings:
        g = f.pop("_group", None)
        if g:
            f["groupId"] = g["id"]
            g["findingIds"].append(f["id"])
            g["rowsTotal"] += f["rowsTotal"]

    # --- rows shown and omissions (concise), everything (exhaustive). `allRows` is the complete list the
    #     evidence index renders; `rows` is the shown subset, so len(rows) == rowsShown always.
    omissions = []
    for f in findings:
        f["allRows"] = f["rows"]
        if mode == "exhaustive":
            f["rowsShown"] = f["rowsTotal"]
            continue
        if f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            f["rowsShown"] = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
            continue
        f["rowsShown"] = min(f["rowsTotal"], rows_per_group)
        if f["rowsShown"] < f["rowsTotal"]:
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - f['rowsShown'])} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                              "why": f"concise mode lists {rows_per_group} row(s) per group",
                              "where": f"evidence-index.md#{f['id']}"})
    for f in findings:
        f["rows"] = (f.get("allRows") or [])[: f["rowsShown"]]

    # --- scope
    targets = [{"name": t.get("name"), "sha256": t.get("sha256"), "bytes": t.get("bytes"), "provider": t.get("provider"),
                "providerKind": t.get("providerKind"), "records": (t.get("footprint") or {}).get("records")}
               for t in (b.plan.get("targets") or []) if isinstance(t, dict)]
    base_names = [p.get("name") for p in ((plan_arms.get("baseline") or {}).get("requested") or {}).get("plugins") or []]
    base_set = {n.lower() for n in base_names if n}
    arms_out = []
    for arm_id in arm_ids:
        pa = plan_arms.get(arm_id) or {}
        da = arms_disp.get(arm_id) or {}
        req = pa.get("requested") or {}
        names = [p.get("name") for p in req.get("plugins") or []]
        added = [n for n in names if n and n.lower() not in base_set] if arm_id != "baseline" else []
        removed = [n for n in base_names if n and n.lower() not in {x.lower() for x in names if x}] if (arm_id != "baseline" and names) else []
        unstated = [{"name": p.get("name"), "unstatedReason": p.get("unstatedReason")}
                    for p in req.get("plugins") or [] if p.get("hashSource") == "unstated"]
        limits = 0
        for rc in b.receipts.values():
            if rc.get("armId") == arm_id and (rc.get("validation") or {}).get("accepted"):
                limits += len((rc.get("validation") or {}).get("identityLimits") or [])
        arms_out.append({"armId": arm_id, "kind": kinds.get(arm_id), "candidateFile": da.get("candidateFile") or pa.get("candidateFile"),
                         "pluginsAdded": added, "pluginsRemoved": removed,
                         "payloadNote": (da.get("payload") or pa.get("payload") or {}).get("note"),
                         "espOnly": (da.get("payload") or pa.get("payload") or {}).get("espOnly"),
                         "loadedInGameNote": da.get("loadedInGameNote") or pa.get("loadedInGameNote"),
                         "orderEvaluation": da.get("orderEvaluation"),
                         "identity": {"pluginCount": len(names), "unstatedRows": unstated, "identityLimits": limits}})
    missing = ev.get("missing") or []
    by_cause = collections.Counter(str(m.get("status")) for m in missing)
    run_ids = b.manifest.get("runIds") or []
    scope_out = {"targets": targets, "registryVersion": b.plan.get("registry", {}).get("registryVersion") or disp.get("registryVersion"),
                 "planId": b.plan.get("planId"), "planCreatedUtc": b.plan.get("createdUtc"),
                 "runStartedUtc": (run_ids[0].get("startedUtc") if run_ids else None),
                 "bundleCreatedUtc": b.manifest.get("createdUtc"),
                 "scopeGeneratedUtc": (b.scope or {}).get("generatedUtc") if isinstance(b.scope, dict) else None,
                 "dispositionCreatedUtc": disp.get("createdUtc"),
                 "arms": arms_out, "states": b.plan.get("states") or {"selected": [], "notExplored": []},
                 "completion": {"complete": bool(ev.get("complete")), "claim": ev.get("claim"), "missingRequired": len(missing),
                                "byCause": dict(sorted(by_cause.items())), "unknownApplicability": ev.get("unknownApplicability") or []},
                 "integrity": ev.get("integrity") or {"ok": None, "defects": []},
                 "explicitDecisions": {"select": (disp.get("explicitDecisions") or {}).get("select"),
                                       "exclude": (disp.get("explicitDecisions") or {}).get("exclude")},
                 "checks": {cid: {"title": c.get("title"), "family": c.get("family"), "required": c.get("required"),
                                  "strength": (c.get("evidence") or {}).get("strength"), "claim": (c.get("evidence") or {}).get("claim"),
                                  "limits": (c.get("evidence") or {}).get("limits")} for cid, c in sorted(b.checks.items())}}

    # --- chapters (the reel's plan; D6 renders them)
    reel = pr.get("reel") or {}
    chapters = []
    claim = ev.get("claim")
    claim_sentence = bind((pr.get("claimSentences") or {}).get(claim or "incomplete", "{claim}"),
                          {"missingRequired": len(missing), "claim": claim})
    # the `missing` chapter names every required missing check id, one caption per status group
    missing_by_status: dict = collections.OrderedDict()
    for m in missing:
        if m.get("required", True) is False:
            continue                                     # an optional check's missing row keeps nothing incomplete
        missing_by_status.setdefault(str(m.get("status")), []).append(m)
    missing_lines = []
    for status in sorted(missing_by_status):
        rows_ = missing_by_status[status]
        ids = sorted({str(m.get("checkId")) for m in rows_})
        missing_lines.append(f"{status} ({fmt_n(len(rows_))}): " + ", ".join(ids))
    chapters.append({"id": "C-01", "kind": "opening", "title": "Evaluation of " + ", ".join(t["name"] for t in targets if t.get("name")),
                     "findingIds": [], "mediaIds": [],
                     "captions": [{"text": f"Arms: " + ", ".join(a['armId'] for a in arms_out) + ". " + claim_sentence
                                   + (" Missing: " + "; ".join(missing_lines) + "." if missing_lines else ""),
                                   "evidenceIds": [eid_by_path.get("disposition.json")] if eid_by_path.get("disposition.json") else [],
                                   "mediaId": None}],
                     "plannedSeconds": reel.get("openingSeconds", 6)})
    n = 1
    media_by_id = {m["id"]: m for m in media}
    blocks: dict = collections.OrderedDict()
    for f in findings:
        if f["kind"] in ("regression", "finding"):
            blocks.setdefault((f["checkId"], f["armId"], f.get("part")), []).append(f)
    order = {f["id"]: i for i, f in enumerate(findings)}
    ordered_blocks = sorted(blocks.items(), key=lambda kv: (0 if any(x["kind"] == "regression" for x in kv[1]) else 1,
                                                          kv[0][0] or "", arm_sort_key(kv[0][1] or "", kinds),
                                                          PART_ORDER.get(kv[0][2], 9), order[kv[1][0]["id"]]))

    def seconds_for(mid):
        m = media_by_id.get(mid) if mid else None
        secs = reel.get("imageSeconds", 5)
        if m and isinstance(m.get("sourceInterval"), dict):
            si = m["sourceInterval"]
            try:
                secs = max(1, float(si.get("endSeconds", 0)) - float(si.get("startSeconds", 0)))
            except (TypeError, ValueError):
                pass
        return secs

    # one `finding` chapter per (checkId, armId, part) block; one caption per qualified media item bound to the
    # block, else a single text caption with mediaId null (the reel draws its "no qualified media" card)
    for (check_id, arm_id, part), fs in ordered_blocks:
        n += 1
        mids = []
        for f in fs:
            for mid in f.get("mediaIds") or []:
                if mid not in mids and mid in media_by_id and media_by_id[mid].get("assessed"):
                    mids.append(mid)
        captions = []
        for mid in mids:
            m = media_by_id[mid]
            owner = next(f for f in fs if mid in (f.get("mediaIds") or []))
            desc = str((m.get("subject") or {}).get("description") or "").strip()
            captions.append({"text": (desc + " " if desc else "") + owner["plain"], "evidenceIds": owner.get("evidenceIds") or [],
                             "mediaId": mid})
        if not captions:
            head = fs[0]
            text = head["plain"] if len(fs) == 1 else (
                f"{check_id} on arm {arm_id} ({part or 'arm'}): {len(fs)} conflict group(s), "
                f"{fmt_n(sum(x['rowsTotal'] for x in fs))} row(s). " + head["plain"])
            captions.append({"text": text, "evidenceIds": sorted({e for f in fs for e in (f.get("evidenceIds") or [])}),
                             "mediaId": None})
        chapters.append({"id": f"C-{n:02d}", "kind": "finding", "title": f"{fs[0]['title']} - {arm_id} / {part or 'arm'}",
                         "findingIds": [f["id"] for f in fs], "mediaIds": mids, "captions": captions,
                         "plannedSeconds": sum(seconds_for(c["mediaId"]) for c in captions)})
    for a in arms_out:
        oe = a.get("orderEvaluation") or {}
        if oe.get("classification"):
            n += 1
            per = oe.get("perProbe") or {}
            text = (f"Arm {a['armId']}: classification {oe.get('classification')}; " +
                    "; ".join(f"{p}: {v.get('verdict')} repaired {fmt_n(v.get('repaired'))} reclassified {fmt_n(v.get('reclassified'))} "
                              f"created {fmt_n(v.get('created'))}" for p, v in sorted(per.items())) + ". " + str(a.get("payloadNote") or ""))
            fids = [f["id"] for f in findings if f["armId"] == a["armId"] and f["kind"] in ("regression", "repaired", "reclassified")]
            chapters.append({"id": f"C-{n:02d}", "kind": "comparison", "title": f"Order evaluation: {a['armId']}", "findingIds": fids,
                             "mediaIds": [], "captions": [{"text": text, "evidenceIds": sorted({e for f in findings if f["id"] in fids for e in f["evidenceIds"]}),
                                                          "mediaId": None}],
                             "plannedSeconds": reel.get("imageSeconds", 5)})
    if missing:
        n += 1
        fids = [f["id"] for f in findings if f["kind"] == "missing"]
        chapters.append({"id": f"C-{n:02d}", "kind": "missing", "title": "Required checks with no accepted result", "findingIds": fids,
                         "mediaIds": [], "captions": [{"text": line, "evidenceIds": [], "mediaId": None} for line in missing_lines],
                         "plannedSeconds": reel.get("imageSeconds", 5) * len(missing_lines)})
    n += 1
    chapters.append({"id": f"C-{n:02d}", "kind": "closing", "title": "What this evaluation does not say", "findingIds":
                     [f["id"] for f in findings if f["kind"] == "unassessed"], "mediaIds": [],
                     "captions": [{"text": claim_sentence
                                   + (" Required checks still missing: " + "; ".join(missing_lines) + "." if missing_lines else "")
                                   + " Not assessed: " + ", ".join(sorted({f.get("area") or "" for f in findings if f["kind"] == "unassessed"})) + ".",
                                   "evidenceIds": [], "mediaId": None}],
                     "plannedSeconds": reel.get("closingSeconds", 8)})

    plan = {
        "schemaVersion": PRESENTATION_SCHEMA,
        "presentationId": None,
        "generatedUtc": None,
        "source": {"bundleDir": bundle_dir_as_given, "planId": b.manifest.get("planId"), "bundleSchema": b.manifest.get("bundleSchema"),
                   "manifestSha256": sha256_file(bundle_dir / "manifest.json"),
                   "dispositionSha256": sha256_file(bundle_dir / b.manifest.get("disposition", "disposition.json")),
                   "ledgerSha256": sha256_file(bundle_dir / b.manifest.get("ledger", "ledger.json")),
                   "verify": {"bundleComplete": bool((verify.get("bundle") or {}).get("complete")),
                              "evaluationComplete": bool((verify.get("evaluation") or {}).get("complete")),
                              "claim": (verify.get("evaluation") or {}).get("claim")},
                   "verifiedFiles": (verify.get("bundle") or {}).get("verified")},
        "compiler": {"name": "evalreport", "version": COMPILER_VERSION, "sourceSha256": compiler_hashes()},
        "settings": settings,
        "scope": scope_out,
        "claimSentence": claim_sentence,
        "findings": findings,
        "groups": [groups[k] for k in sorted(groups)],
        "evidence": evidence,
        "media": media + unassessed_media,
        "unassessed": {**un, "media": unassessed_media},
        "omissions": omissions,
        "chapters": chapters,
        "narration": {"source": "template", "validated": True, "refused": []},
    }
    return plan


def compiler_hashes() -> dict:
    out = {"evalreport.py": sha256_file(Path(__file__).resolve())}
    for t in sorted(TEMPLATES.glob("*")) if TEMPLATES.exists() else []:
        out[f"templates/{t.name}"] = sha256_file(t)
    out["report-priorities.json"] = sha256_file(PRIORITIES)
    return out


def stamp(plan: dict) -> dict:
    body = {k: v for k, v in plan.items() if k not in ("presentationId", "generatedUtc")}
    plan["presentationId"] = sha256_text(canonical(body))[:12]
    plan["generatedUtc"] = utc_now()
    return plan


# ------------------------------------------------------------------------------------------ render --

def env(autoescape: bool):
    if jinja2 is None:
        raise SystemExit("REFUSED: jinja2 is not installed; the templates cannot be rendered (pip install jinja2)")
    e = jinja2.Environment(loader=jinja2.FileSystemLoader(str(TEMPLATES)), autoescape=autoescape,
                           trim_blocks=True, lstrip_blocks=True, keep_trailing_newline=True, undefined=jinja2.StrictUndefined)
    e.filters["n"] = fmt_n
    e.filters["slug"] = slug
    e.filters["md"] = lambda s: str(s if s is not None else "").replace("|", "\\|").replace("\n", " ")
    e.filters["short"] = lambda s, n=96: (str(s) if s is not None else "")[:n] + ("..." if s and len(str(s)) > n else "")
    e.filters["kv"] = fmt_kv
    e.filters["sentences"] = sentence_cut
    e.filters["tailpath"] = tail_path
    return e


def sentence_cut(s, n=220) -> str:
    """Cut at a sentence boundary within n characters; empty when no boundary falls past 40% of n."""
    s = str(s or "").strip()
    if len(s) <= n:
        return s
    head = s[:n]
    cut = max(head.rfind(". "), head.rfind("; "))
    if cut < int(n * 0.4):
        return ""
    return head[:cut + 1]


def tail_path(p) -> str:
    """`<supplier folder>/<basename>` - a shareable rendition carries no machine path."""
    parts = [x for x in re.split(r"[\\/]+", str(p or "")) if x]
    return "/".join(parts[-2:]) if len(parts) > 2 else "/".join(parts)


def fmt_kv(d) -> str:
    """`{'NoGeometry': 65}` -> `NoGeometry 65`; an empty or all-zero mapping reads `none`."""
    if not isinstance(d, dict):
        return "none" if d in (None, {}, [], "") else str(d)
    items = [(k, v) for k, v in d.items()]
    if not items or all((isinstance(v, (int, float)) and v == 0) for _, v in items):
        return "none"
    return ", ".join(f"{k} {fmt_n(v)}" for k, v in items)


def effective_view(plan: dict, rows_per_group: int, figures_per_finding: int, pr: dict, layout: str = "normal") -> dict:
    """The plan as the templates see it at one ladder step: rows shown per finding, figures per finding,
    omissions recomputed for the effective settings (exhaustive mode: everything, no ladder)."""
    mode = plan["settings"]["mode"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")
    media_by_id = {m["id"]: m for m in plan.get("media") or []}
    findings = []
    omissions = []
    for f in plan["findings"]:
        g = dict(f)
        if mode == "exhaustive":
            shown = f["rowsTotal"]
        elif f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            shown = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
        else:
            shown = min(f["rowsTotal"], rows_per_group)
            if shown < f["rowsTotal"]:
                omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - shown)} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                                  "why": f"concise mode lists {rows_per_group} row(s) per group", "where": f"evidence-index.md#{f['id']}"})
        g["rowsShown"] = shown
        g["shownRows"] = (f.get("allRows") or f.get("rows") or [])[:shown]
        figs = [media_by_id[m] for m in (f.get("mediaIds") or []) if m in media_by_id and media_by_id[m].get("assessed")]
        if mode != "exhaustive" and len(figs) > figures_per_finding:
            omissions.append({"what": f"{f['id']}: {len(figs) - figures_per_finding} of {len(figs)} figure(s) not shown",
                              "why": f"concise mode shows {figures_per_finding} figure(s) per finding", "where": f"evidence-index.md#{f['id']}"})
            figs = figs[:figures_per_finding]
        g["figures"] = figs
        findings.append(g)
    sections = []
    for s in sorted(pr.get("sections") or [], key=lambda s: s.get("order", 99)):
        kinds = set(s.get("kinds") or [])
        areas = set(s.get("areas") or [])
        fs = [f for f in findings if f["kind"] in kinds and (not areas or f.get("area") in areas)]
        sections.append({**s, "findings": fs})
    # section 2, compact: one row per (status, check, cause) with the arms joined
    missing_rows = []
    seen = collections.OrderedDict()
    for f in findings:
        if f["kind"] != "missing":
            continue
        key = (str(f["counts"].get("status")), str(f["counts"].get("cause")))
        if key not in seen:
            seen[key] = {"status": key[0], "cause": f["counts"].get("cause"), "checks": collections.OrderedDict(), "findingIds": []}
        seen[key]["checks"].setdefault(f.get("checkId"), []).append(f.get("armId"))
        seen[key]["findingIds"].append(f["id"])
    for key in sorted(seen):
        row = seen[key]
        row["checks"] = [{"checkId": c, "arms": a} for c, a in row["checks"].items()]
        row["count"] = len(row["findingIds"])
        missing_rows.append(row)
    # section 3, compact: per check, per (arm, part) block with its groups
    blocks = collections.OrderedDict()
    for f in findings:
        if f["kind"] != "finding":
            continue
        cb = blocks.setdefault(f["checkId"], {"checkId": f["checkId"], "title": f.get("title"),
                                              "check": (plan["scope"].get("checks") or {}).get(f["checkId"]) or {},
                                              "parts": collections.OrderedDict()})
        pk = (f.get("armId"), f.get("part"))
        part = cb["parts"].setdefault(pk, {"armId": f.get("armId"), "part": f.get("part"), "findingsInPart": f["counts"].get("findingsInPart"),
                                           "verdict": f.get("verdict"), "strength": f.get("strength"), "evidenceIds": f.get("evidenceIds") or [],
                                           "indexOnly": (mode != "exhaustive" and f.get("part") == "reverse" and reverse_rule == "index-only"),
                                           "population": f["counts"].get("population"), "state": f["counts"].get("state"),
                                           "groups": [], "rowsTotal": 0})
        part["groups"].append(f)
        part["rowsTotal"] += f["rowsTotal"]
    finding_blocks = []
    arm_kinds = {a["armId"]: a.get("kind") for a in plan["scope"]["arms"]}
    for cb in blocks.values():
        parts = sorted(cb["parts"].values(), key=lambda x: (arm_sort_key(x["armId"] or "", arm_kinds), PART_ORDER.get(x["part"], 9)))
        finding_blocks.append({**cb, "parts": parts})
    view = dict(plan)
    view["missingRows"] = missing_rows
    view["findingBlocks"] = finding_blocks
    view["unjudgeable"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") == "unjudgeable"]
    view["unassessedOther"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") not in ("unjudgeable", "media")]
    view["findings"] = findings
    view["sections"] = sections
    view["omissions"] = omissions
    view["effective"] = {"layout": layout, "rowsPerGroup": rows_per_group, "figuresPerFinding": figures_per_finding}
    view["layout"] = layout
    view["evidenceTokensCompact"] = (pr.get("concise") or {}).get("evidenceTokensCompact", 3)
    view["mediaAssessed"] = [m for m in plan.get("media") or [] if m.get("assessed")]
    view["mediaUnassessed"] = [m for m in plan.get("media") or [] if not m.get("assessed")]
    view["assertedSentence"] = pr.get("mediaAssertedSentence")
    return view


def copy_media(plan: dict, bundle_dir: Path, out: Path) -> list[dict]:
    """Copy every assessed media file the report may show under OUT/media/, by content hash - so a
    relocated OUT still resolves its figures. Returns the referenced-media rows for the manifest."""
    rows = []
    for m in plan.get("media") or []:
        if not m.get("assessed") or not m.get("path"):
            continue
        src = bundle_dir / m["path"]
        if not src.exists():
            m["reportPath"] = None
            m["reportNote"] = "media file absent from the bundle at render time"
            continue
        dest = out / "media" / Path(m["path"]).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.exists() or sha256_file(dest) != m.get("sha256"):
            shutil.copy2(src, dest)
        m["reportPath"] = f"media/{dest.name}"
        rows.append({"id": m["id"], "path": m["reportPath"], "sha256": sha256_file(dest), "bundlePath": m["path"]})
    return rows


def render_all(plan: dict, view: dict, out: Path, css: str) -> dict:
    e_md = env(False)
    e_html = env(True)
    ctx = {"p": view, "css": css}
    md = e_md.get_template("report.md.j2").render(**ctx)
    html = e_html.get_template("report.html.j2").render(**ctx)
    idx = e_md.get_template("evidence-index.md.j2").render(**ctx)
    write_text(out / "report.md", md)
    write_text(out / "report.html", html)
    write_text(out / "evidence-index.md", idx)
    return {"report.md": md, "report.html": html, "evidence-index.md": idx}


# -------------------------------------------------------------------------------------- commands --

def cmd_plan(a) -> int:
    pr = load_priorities()
    bundle = Path(a.bundle)
    out = Path(a.out)
    if getattr(a, "exhaustive", False):
        a.mode = "exhaustive"
    with Run("evalreport-plan", stage_source="counted") as r:
        r.stage("verify-bundle", total=1, unit="bundles")
        if not (bundle / "manifest.json").exists():
            return refuse(r, f"bundle-missing: {bundle / 'manifest.json'} does not exist")
        manifest = load_json(bundle / "manifest.json")
        if not isinstance(manifest, dict):
            return refuse(r, f"manifest-unreadable: {bundle / 'manifest.json'} is not JSON")
        if manifest.get("bundleSchema") != BUNDLE_SCHEMA:
            return refuse(r, f"schema-unsupported: bundleSchema {manifest.get('bundleSchema')!r} is not {BUNDLE_SCHEMA}; "
                             "this bundle predates evidence-chain verification - rebuild it from its run")
        out.mkdir(parents=True, exist_ok=True)
        verify, code, err = run_verify_bundle(bundle, out, a.verify_copy)
        if verify is None:
            return refuse(r, f"verify-failed: {err}")
        bl = verify.get("bundle") or {}
        if not bl.get("schemaSupported", True):
            return refuse(r, "schema-unsupported: " + "; ".join(d.get("detail", "") for d in bl.get("defects") or []))
        if not bl.get("complete"):
            defects = bl.get("defects") or []
            names = ", ".join(f"{d.get('kind')} ({d.get('path')})" for d in defects[:8]) or "unnamed defect"
            more = f" and {len(defects) - 8} more" if len(defects) > 8 else ""
            return refuse(r, f"bundle-refused: verify-bundle found the bundle incomplete: {names}{more}; nothing is compiled "
                             f"from a bundle whose evidence does not hash to what was measured")
        r.advance(1)
        r.stage("compile", total=1, unit="plans")
        conc = pr.get("concise") or {}
        page = pr.get("page") or {}
        settings = {"mode": a.mode, "layout": conc.get("layout", "normal"),
                    "maxPages": (a.max_pages if a.max_pages is not None else conc.get("maxPages", 8)),
                    "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16), "baseFontPt": page.get("baseFontPt", 10.5)},
                    "rowsPerGroup": (a.rows_per_group if a.rows_per_group is not None else conc.get("rowsPerGroup", 3)),
                    "figuresPerFinding": (a.figures_per_finding if a.figures_per_finding is not None else conc.get("figuresPerFinding", 1)),
                    "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height")}}
        if a.mode == "exhaustive":
            settings["maxPages"] = None
        plan = compile_plan(bundle, verify, settings, pr, str(a.bundle))
        stamp(plan)
        write_json(out / "presentation.json", plan)
        r.artifact(str(out / "presentation.json"))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("groups", len(plan["groups"]), "groups")
        r.feature("media", len(plan["media"]), "media")
        r.feature("evidence", len(plan["evidence"]), "files")
        r.advance(1)
        print(f"presentation {plan['presentationId']}: {len(plan['findings'])} findings, {len(plan['groups'])} groups, "
              f"{len(plan['media'])} media rows, {len(plan['evidence'])} evidence files; mode {a.mode}; "
              f"bundle complete {plan['source']['verify']['bundleComplete']}, evaluation complete "
              f"{plan['source']['verify']['evaluationComplete']} ({plan['source']['verify']['claim']})")
        print(f"wrote {out / 'presentation.json'}")
    return EXIT_OK


def load_presentation(path: Path):
    plan = load_json(path)
    if not isinstance(plan, dict):
        return None, f"presentation-unreadable: {path} is absent or not JSON"
    if plan.get("schemaVersion") != PRESENTATION_SCHEMA:
        return None, f"presentation-schema-unsupported: schemaVersion {plan.get('schemaVersion')!r} is not {PRESENTATION_SCHEMA}"
    for k in ("findings", "settings", "source", "scope", "evidence"):
        if k not in plan:
            return None, f"presentation-incomplete: no `{k}`"
    return plan, ""


def cmd_report(a) -> int:
    pr = load_priorities()
    ppath = Path(a.presentation)
    out = Path(a.out)
    with Run("evalreport-report", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        bundle_dir = Path(plan["source"]["bundleDir"])
        if not bundle_dir.is_absolute():
            bundle_dir = (ppath.parent / bundle_dir).resolve() if not bundle_dir.exists() else bundle_dir
        out.mkdir(parents=True, exist_ok=True)
        css = read_text(TEMPLATES / "report.css") if (TEMPLATES / "report.css").exists() else ""
        settings = plan["settings"]
        mode = settings.get("mode", "concise")
        ladder = pr.get("abridgementLadder") or [{"step": 0, "layout": settings.get("layout", "normal"),
                                                  "rowsPerGroup": settings.get("rowsPerGroup", 3),
                                                  "figuresPerFinding": settings.get("figuresPerFinding", 1)}]
        # the ladder starts at the plan's own settings and only descends
        steps = [s for s in ladder if s["rowsPerGroup"] <= settings.get("rowsPerGroup", 3) and
                 s["figuresPerFinding"] <= settings.get("figuresPerFinding", 1)] or ladder
        if mode == "exhaustive":
            steps = [{"step": 0, "layout": settings.get("layout", "normal"), "rowsPerGroup": 0, "figuresPerFinding": 10 ** 6}]
        assessed_ids = {m["id"] for m in plan.get("media") or [] if m.get("assessed")}
        has_figures = any(mid in assessed_ids for f in plan["findings"] for mid in (f.get("mediaIds") or []))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("media", len(assessed_ids), "media")
        media_rows = copy_media(plan, bundle_dir, out)
        browser, version, why = (None, "", "printing disabled by --no-print") if a.no_print else resolve_browser()
        print_info = {"browser": str(browser) if browser else None, "version": version or None, "pages": None,
                      "maxPages": settings.get("maxPages"), "status": "not-rendered", "reason": why or None,
                      "ladder": [], "rootCount": None, "kept": None}
        limit = settings.get("maxPages")
        view = None
        rendered = 0

        def step_row(step, pages=None, note=None):
            return {"step": step.get("step"), "layout": step.get("layout", "normal"), "rowsPerGroup": step["rowsPerGroup"],
                    "figuresPerFinding": step["figuresPerFinding"], "pages": pages, "note": note}

        def render_step(step):
            nonlocal rendered
            v = effective_view(plan, step["rowsPerGroup"], step["figuresPerFinding"], pr, step.get("layout", "normal"))
            render_all(plan, v, out, css)
            rendered += 3
            r.feature("figures", sum(len(f.get("figures") or []) for f in v["findings"]), "figures")
            return v

        def print_step():
            pdf = out / "report.pdf"
            if pdf.exists():
                pdf.unlink()
            ok, perr = print_pdf(browser, out / "report.html", pdf)
            if not ok:
                return None, None, perr
            return pdf_page_count(pdf)

        r.stage("print", total=len(steps), unit="attempts")
        prev_figs = None
        for step in steps:
            if prev_figs is not None and step["figuresPerFinding"] < prev_figs and not has_figures:
                print_info["ladder"].append(step_row(step, None, "skipped: no figure to drop"))
                r.advance(1)
                continue
            prev_figs = step["figuresPerFinding"]
            view = render_step(step)
            if browser is None:
                r.advance(1)
                break
            pages, root, note = print_step()
            r.advance(1)
            if pages is None and root is None and note and not note.startswith("no page objects"):
                print_info.update({"status": "not-rendered", "reason": note, "pages": None})
                r.note(f"print failed: {note}")
                break
            print_info["rootCount"] = root
            print_info["ladder"].append(step_row(step, pages, note))
            if pages is None or note != "ok":
                print_info.update({"status": "count-unverified", "reason": note, "pages": pages})
                break
            print_info["pages"] = pages
            print_info["reason"] = None
            print_info["kept"] = step_row(step, pages, "kept")
            if mode == "exhaustive" or limit is None:
                print_info["status"] = "rendered"
                break
            if pages <= limit:
                print_info["status"] = "within-limit"
                break
            print_info["status"] = "over-limit"
        if print_info["status"] == "over-limit":
            # A-9 / A-11: a report over the limit either way keeps its pictures, and an abridgement that does not
            # buy the limit is pure loss - the kept rendition is the LEAST abridged step that loses no content:
            # the compact layout (chrome, not content) at the setting's own rows per group and figures per
            # finding; the normal-layout step when the ladder has no compact one. Every step stays in the ladder.
            counted = [l for l in print_info["ladder"] if l.get("pages") is not None]
            full = [l for l in counted if l["rowsPerGroup"] >= settings.get("rowsPerGroup", 3)
                    and l["figuresPerFinding"] >= settings.get("figuresPerFinding", 1)]
            compact_full = [l for l in full if l["layout"] == "compact"]
            best = min(compact_full or full or counted, key=lambda l: l["step"])
            print_info["reason"] = (f"the limit of {limit} page(s) was unreachable without hiding required content; "
                                    "pages at each step: " + ", ".join(f"{l['layout']}/{l['rowsPerGroup']}/{l['figuresPerFinding']} "
                                                                       f"{l['pages']}" for l in counted))
            if best["step"] != (print_info["kept"] or {}).get("step"):
                bstep = next(x for x in steps if x.get("step") == best["step"])
                view = render_step(bstep)
                pages, root, note = print_step()
                print_info["ladder"].append(step_row(bstep, pages, f"{note}; re-rendered as the kept step"))
                best = {**best, "pages": pages if pages is not None else best["pages"]}
            print_info["pages"] = best["pages"]
            print_info["kept"] = {**best, "note": "kept"}
        # ------- manifest
        produced = []
        for name in ARTIFACTS:
            p = out / name
            if p.exists():
                produced.append({"path": name, "sha256": sha256_file(p), "bytes": p.stat().st_size})
        effective = view["effective"] if view else {"layout": settings.get("layout", "normal"), "rowsPerGroup": settings.get("rowsPerGroup"),
                                                    "figuresPerFinding": settings.get("figuresPerFinding")}
        r.note(f"rendered {rendered} rendition(s) over {len(print_info['ladder']) or 1} ladder step(s)")
        manifest = {"schemaVersion": 1, "presentationId": plan.get("presentationId"), "generatedUtc": utc_now(),
                    "source": plan["source"], "compiler": plan["compiler"], "settings": settings, "effective": effective,
                    "artifacts": produced, "media": media_rows,
                    "omissions": view["omissions"] if view else plan.get("omissions"),
                    "print": print_info}
        write_json(out / "presentation-manifest.json", manifest)
        for x in produced:
            r.artifact(str(out / x["path"]))
        r.artifact(str(out / "presentation-manifest.json"))
        if print_info.get("pages") is not None:
            r.feature("pages", print_info["pages"], "pages", at="final")
        st = print_info["status"]
        print(f"report: {', '.join(x['path'] for x in produced)}; print {st}" +
              (f" ({print_info['pages']} page(s), limit {print_info['maxPages']})" if print_info.get("pages") is not None else
               f" ({print_info.get('reason')})") +
              f"; layout {effective['layout']}, rows per group {effective['rowsPerGroup']}, figures per finding {effective['figuresPerFinding']}"
              + (f"; ladder {[(l['layout'], l['rowsPerGroup'], l['figuresPerFinding'], l['pages']) for l in print_info['ladder']]}" if print_info['ladder'] else ""))
        if st == "over-limit":
            print(f"over-limit: the abridgement ladder ended at {print_info['pages']} page(s) against a limit of "
                  f"{print_info['maxPages']}; the font was not reduced. Use --mode exhaustive for an unbounded rendition.")
        if st == "not-rendered":
            print(f"not-rendered: {print_info.get('reason')}; report.md and report.html were written, no page count is claimed.")
    return EXIT_OK


LINK_MD = re.compile(r"\]\(([^)\s]+)\)")
LINK_HTML = re.compile(r"(?:href|src)=\"([^\"]+)\"")


def check_links(out: Path, name: str, text: str, defects: list, counter: dict) -> None:
    pat = LINK_HTML if name.endswith(".html") else LINK_MD
    anchors_cache: dict[str, set] = {}
    for m in pat.finditer(text):
        target = m.group(1)
        if re.match(r"^[a-z]+:", target) or target.startswith("data:"):
            continue
        path, _, frag = target.partition("#")
        counter["links"] += 1
        if path:
            fp = out / path
            if not fp.exists():
                defects.append({"kind": "link-unresolved", "path": name, "detail": f"{target} does not resolve from {out}"})
                continue
        else:
            fp = out / name
        if frag:
            key = str(fp)
            if key not in anchors_cache:
                t = read_text(fp) if fp.exists() else ""
                anchors_cache[key] = set(re.findall(r"id=\"([^\"]+)\"", t))
            if frag not in anchors_cache[key]:
                defects.append({"kind": "anchor-unresolved", "path": name, "detail": f"{target}: no anchor `{frag}` in {fp.name}"})


def cmd_verify(a) -> int:
    out = Path(a.out)
    with Run("evalreport-verify", stage_source="counted") as r:
        man = load_json(out / "presentation-manifest.json")
        if not isinstance(man, dict):
            return refuse(r, f"manifest-missing: {out / 'presentation-manifest.json'} is absent or not JSON")
        plan, err = load_presentation(out / "presentation.json")
        if plan is None:
            return refuse(r, err)
        defects = []
        counter = {"links": 0, "artifacts": 0, "media": 0}
        arts = man.get("artifacts") or []
        r.stage("verify", total=len(arts) + len(man.get("media") or []) + 1, unit="checks")
        if plan.get("presentationId") != man.get("presentationId"):
            defects.append({"kind": "presentation-mismatch", "path": "presentation.json",
                            "detail": f"presentationId {plan.get('presentationId')} is not the manifest's {man.get('presentationId')}"})
        for x in arts:
            p = out / x["path"]
            counter["artifacts"] += 1
            if not p.exists():
                defects.append({"kind": "artifact-missing", "path": x["path"], "detail": "listed in the manifest, absent on disk"})
            elif sha256_file(p) != x.get("sha256"):
                defects.append({"kind": "artifact-changed", "path": x["path"], "detail": "does not hash to the manifest's sha256"})
            elif x["path"].endswith((".md", ".html")):
                check_links(out, x["path"], read_text(p), defects, counter)
            r.advance(1)
        for m in man.get("media") or []:
            p = out / m["path"]
            counter["media"] += 1
            if not p.exists():
                defects.append({"kind": "media-missing", "path": m["path"], "detail": "referenced media absent"})
            elif sha256_file(p) != m.get("sha256"):
                defects.append({"kind": "media-changed", "path": m["path"], "detail": "does not hash to the manifest's sha256"})
            r.advance(1)
        pi = man.get("print") or {}
        pages_now = None
        if pi.get("status") in ("within-limit", "over-limit", "rendered", "count-unverified"):
            if not (out / "report.pdf").exists():
                defects.append({"kind": "pdf-missing", "path": "report.pdf", "detail": f"print status {pi.get('status')} but no PDF"})
            else:
                pages_now, root, note = pdf_page_count(out / "report.pdf")
                if pages_now != pi.get("pages"):
                    defects.append({"kind": "page-count-mismatch", "path": "report.pdf",
                                    "detail": f"recomputed {pages_now} page(s) ({note}); the manifest says {pi.get('pages')}"})
                if pi.get("status") == "within-limit" and pi.get("maxPages") is not None and (pages_now or 0) > pi["maxPages"]:
                    defects.append({"kind": "limit-contradiction", "path": "report.pdf",
                                    "detail": f"{pages_now} page(s) exceed the limit {pi['maxPages']} yet the status is within-limit"})
        r.advance(1)
        result = {"schemaVersion": 1, "checkedUtc": utc_now(), "out": str(out), "presentationId": plan.get("presentationId"),
                  "ok": not defects, "artifacts": counter["artifacts"], "media": counter["media"], "links": counter["links"],
                  "pages": pages_now, "printStatus": pi.get("status"), "defects": defects}
        write_json(out / "presentation-verify.json", result)
        r.feature("links", counter["links"], "links")
        if pages_now is not None:
            r.feature("pages", pages_now, "pages", at="final")
        if defects:
            for d in defects:
                print(f"DEFECT {d['kind']} {d['path']}: {d['detail']}", file=sys.stderr)
            return refuse(r, f"presentation-refused: {len(defects)} defect(s) - see presentation-verify.json", EXIT_REFUSED_RESULT)
        print(f"verified: {counter['artifacts']} artifact(s), {counter['media']} media file(s), {counter['links']} link(s) resolve"
              + (f", {pages_now} page(s) ({pi.get('status')})" if pages_now is not None else f", print {pi.get('status')}"))
    return EXIT_OK


# ------------------------------------------------------------------------------- narration seam --

NUM = re.compile(r"(?<![\w:./-])\d[\d,]*(?:\.\d+)?(?![\w:./-])")
CHECK_TOKEN = re.compile(r"\b[a-z]+\.[a-z][a-z-]+\b")
ARM_TOKEN = re.compile(r"\b(?:candidate|supplied)-\d\d[\w-]*")
PLUGIN_TOKEN = re.compile(r"[A-Za-z0-9_'()\-]+(?: [A-Za-z0-9_'()\-]+)*\.(?:esp|esm|esl)\b", re.I)
FORMKEY = re.compile(r"\b[0-9A-Fa-f]{6}:[^\s,;()]+\.(?:esp|esm|esl)\b", re.I)


def bound_sets(plan: dict, f: dict) -> tuple[set, set]:
    nums = set()

    def walk(v):
        if isinstance(v, bool):
            return
        if isinstance(v, (int, float)):
            nums.add(str(int(v)) if float(v).is_integer() else str(v))
        elif isinstance(v, dict):
            for x in v.values():
                walk(x)
        elif isinstance(v, list):
            for x in v:
                walk(x)
        elif isinstance(v, str):
            for n in NUM.findall(v):
                nums.add(n.replace(",", ""))
    walk(f.get("counts"))
    for row in f.get("allRows") or f.get("rows") or []:
        walk({k: row.get(k) for k in ("summary", "physicalDetail", "label")})
    names = set()
    c = f.get("counts") or {}
    for k in ("A", "B", "probe"):
        if c.get(k):
            names.add(str(c[k]))
    for g in c.get("groups") or []:
        for k in ("A", "B"):
            if g.get(k):
                names.add(str(g[k]))
    for row in f.get("allRows") or f.get("rows") or []:
        for k in ("formKey", "label", "summary"):
            if row.get(k):
                names.add(str(row[k]))
    if f.get("checkId"):
        names.add(f["checkId"])
    if f.get("armId"):
        names.add(f["armId"])
    arm = next((a for a in plan["scope"]["arms"] if a["armId"] == f.get("armId")), None)
    if arm:
        for n in (arm.get("pluginsAdded") or []) + (arm.get("pluginsRemoved") or []):
            names.add(n)
    return nums, names


def universe(plan: dict) -> tuple[set, set, set, set]:
    plugins, formkeys, labels = set(), set(), set()
    for f in plan["findings"]:
        c = f.get("counts") or {}
        for k in ("A", "B"):
            if c.get(k):
                plugins.add(str(c[k]))
        for row in f.get("allRows") or f.get("rows") or []:
            if row.get("formKey"):
                formkeys.add(row["formKey"])
            if row.get("label"):
                labels.add(row["label"])
    for a in plan["scope"]["arms"]:
        for n in (a.get("pluginsAdded") or []) + (a.get("pluginsRemoved") or []):
            plugins.add(n)
    for t in plan["scope"].get("targets") or []:
        if t.get("name"):
            plugins.add(t["name"])
    checks = set(plan["scope"].get("checks") or {}) | {f["checkId"] for f in plan["findings"] if f.get("checkId")}
    return plugins, formkeys, labels, checks


def validate_answer(plan: dict, f: dict, text: str, pr: dict) -> list[dict]:
    """D7: every number bound, every named plugin / cell / formKey bound, no check or arm outside the plan,
    none of the forbidden words. Returns the rule violations (empty = accepted)."""
    bad = []
    nums, names = bound_sets(plan, f)
    plugins, formkeys, labels, checks = universe(plan)
    arms = {a["armId"] for a in plan["scope"]["arms"]}
    low = text.lower()
    for pat in pr.get("forbiddenPatterns") or []:
        m = re.search(pat, low)
        if m:
            bad.append({"rule": "forbidden-word", "detail": f"contains `{m.group(0)}`"})
    for n in NUM.findall(text):
        v = n.replace(",", "")
        if v not in nums:
            bad.append({"rule": "unbound-number", "detail": f"{n} is not one of the finding's bound numbers"})
    for tok in set(CHECK_TOKEN.findall(text)):
        if tok in checks and tok != f.get("checkId"):
            bad.append({"rule": "unbound-check", "detail": f"names check {tok}, which is not this finding's"})
        elif tok not in checks and re.match(r"^(loadability|records|geometry|functional|state|environment|patch|runtime)\.", tok):
            bad.append({"rule": "unknown-check", "detail": f"names {tok}, which is not a check in the plan"})
    for tok in set(ARM_TOKEN.findall(text)):
        if tok not in arms:
            bad.append({"rule": "unknown-arm", "detail": f"names arm {tok}, which is not in the plan"})
        elif tok != f.get("armId"):
            bad.append({"rule": "unbound-arm", "detail": f"names arm {tok}, which is not this finding's"})
    if re.search(r"\bbaseline\b", low) and f.get("armId") not in (None, "baseline") and (f.get("armId") or "").lower() not in low:
        # the baseline is the reference a differential is stated against, so naming it beside the finding's own
        # arm is fine; naming it INSTEAD of that arm places the finding on the wrong arm
        bad.append({"rule": "unbound-arm", "detail": "names the baseline arm without naming this finding's own arm"})
    for p in plugins:
        if p.lower() in low and p not in names:
            bad.append({"rule": "unbound-plugin", "detail": f"names {p}, which is not bound to this finding"})
    masked = text
    for known in sorted(set(names) | set(plugins), key=len, reverse=True):
        if known.lower().endswith((".esp", ".esm", ".esl")):
            masked = re.sub(re.escape(known), " " * len(known), masked, flags=re.I)
    for tok in set(PLUGIN_TOKEN.findall(masked)):
        short = " ".join(tok.split()[-4:])
        bad.append({"rule": "unknown-plugin", "detail": f"names {short}, which is not a plugin bound to this finding"})
    for fk in set(FORMKEY.findall(text)):
        if fk not in names and fk.upper() not in {n.upper() for n in names}:
            bad.append({"rule": "unbound-formkey" if fk in formkeys else "unknown-formkey",
                        "detail": f"names {fk}, which is not bound to this finding"})
    for lab in labels:
        if len(lab) >= 6 and lab.lower() in low and lab not in names:
            bad.append({"rule": "unbound-cell", "detail": f"names {lab}, which is not bound to this finding"})
    return bad


def cmd_narrate_packet(a) -> int:
    ppath = Path(a.presentation)
    with Run("evalreport-narrate", mode="packet", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        r.stage("packet", total=len(plan["findings"]), unit="findings")
        items = []
        for f in plan["findings"]:
            nums, names = bound_sets(plan, f)
            items.append({"id": f["id"], "kind": f["kind"], "checkId": f.get("checkId"), "armId": f.get("armId"), "part": f.get("part"),
                          "title": f.get("title"), "template": f.get("plain"), "counts": f.get("counts"),
                          "boundNumbers": sorted(nums, key=lambda s: (len(s), s)), "boundNames": sorted(names),
                          "rows": (f.get("allRows") or f.get("rows") or [])[: max(1, plan["settings"].get("rowsPerGroup") or 3)]})
            r.advance(1)
        packet = {"schemaVersion": 1, "presentationId": plan.get("presentationId"),
                  "instructions": "Rewrite each finding's `template` sentence in plain language. Use only the numbers in "
                                  "boundNumbers and the names in boundNames; name no other check, arm, plugin, cell or form key; "
                                  "never use the words pass, compatible, clean or verified-in-game; never claim completion. "
                                  "Answer as {\"answers\": [{\"id\": \"F-001\", \"text\": \"...\"}]}. A refused answer keeps the template.",
                  "checks": sorted(plan["scope"].get("checks") or {}), "arms": [x["armId"] for x in plan["scope"]["arms"]],
                  "findings": items}
        write_json(Path(a.out), packet)
        r.artifact(str(a.out))
        r.feature("findings", len(items), "findings")
        print(f"packet: {len(items)} finding(s) -> {a.out}")
    return EXIT_OK


def cmd_narrate_apply(a) -> int:
    pr = load_priorities()
    ppath = Path(a.presentation)
    with Run("evalreport-narrate", mode="apply", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        answers = load_json(Path(a.answers))
        if isinstance(answers, dict) and isinstance(answers.get("answers"), dict):
            answers = {**answers, "answers": [{"id": k, "text": v} for k, v in answers["answers"].items()]}
        if not isinstance(answers, dict) or not isinstance(answers.get("answers"), list):
            return refuse(r, f"answers-unreadable: {a.answers} is not a JSON object with an `answers` list "
                             f"([{{id, text}}]) or mapping ({{id: text}})")
        if answers.get("presentationId") not in (None, plan.get("presentationId")):
            return refuse(r, f"answers-foreign: the answers name presentation {answers.get('presentationId')}, this is {plan.get('presentationId')}")
        by_id = {f["id"]: f for f in plan["findings"]}
        r.stage("apply", total=len(answers["answers"]), unit="answers")
        accepted, refused = 0, []
        for ans in answers["answers"]:
            fid = (ans or {}).get("id")
            text = (ans or {}).get("text")
            f = by_id.get(fid)
            if f is None:
                refused.append({"id": fid, "rule": "unknown-finding", "detail": f"{fid} is not in the plan"})
                r.advance(1)
                continue
            if not isinstance(text, str) or not text.strip():
                refused.append({"id": fid, "rule": "empty-answer", "detail": "no text"})
                r.advance(1)
                continue
            bad = validate_answer(plan, f, text, pr)
            if bad:
                for x in bad:
                    refused.append({"id": fid, **x})
            else:
                f.setdefault("templatePlain", f["plain"])
                f["plain"] = text.strip()
                f["narrated"] = True
                accepted += 1
            r.advance(1)
        plan["narration"] = {"source": f"answers:{sha256_file(Path(a.answers))[:12]}", "validated": True,
                             "accepted": accepted, "refused": refused}
        dest = Path(a.out) if a.out else ppath
        stamp(plan)
        write_json(dest, plan)
        r.artifact(str(dest))
        r.feature("findings", len(plan["findings"]), "findings")
        r.tally(ok=accepted, failed=len(refused))
        print(f"narration: {accepted} answer(s) accepted, {len(refused)} refusal(s); wrote {dest}")
        for x in refused:
            print(f"  refused {x['id']}: {x['rule']} - {x['detail']}")
    return EXIT_OK


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalreport", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("plan", help="verify the bundle and compile the presentation plan")
    p.add_argument("--bundle", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--mode", choices=("concise", "exhaustive"), default="concise")
    p.add_argument("--exhaustive", action="store_true", help="list every row; no page limit (same as --mode exhaustive)")
    p.add_argument("--max-pages", type=int, default=None)
    p.add_argument("--rows-per-group", type=int, default=None)
    p.add_argument("--figures-per-finding", type=int, default=None)
    p.add_argument("--verify-copy", action="store_true",
                   help="verify a temporary copy of the bundle so the bundle directory itself is never written to")
    p.set_defaults(fn=cmd_plan)
    p = sub.add_parser("report", help="render report.md / report.html / report.pdf / evidence-index.md from the plan")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--no-print", action="store_true", help="skip the browser; print.status is not-rendered")
    p.set_defaults(fn=cmd_report)
    p = sub.add_parser("verify", help="re-hash the artifacts, resolve every link, recount the pages")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_verify)
    p = sub.add_parser("narrate-packet", help="write every finding's bound numbers and names for an optional narrator")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_narrate_packet)
    p = sub.add_parser("narrate-apply", help="accept narrated sentences only where every number and name is bound")
    p.add_argument("--presentation", required=True)
    p.add_argument("--answers", required=True)
    p.add_argument("--out", default=None)
    p.set_defaults(fn=cmd_narrate_apply)
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
